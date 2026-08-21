using System.Diagnostics;
using System.Text.Json;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// TEF pelo ControlPay (WebService), contra o <see cref="FakeControlPay"/>.
/// O que é inegociável aqui: chave só na query e NUNCA em mensagem/auditoria; User-Agent em
/// toda chamada; resultado só pelo status da intenção (10/25/15/20), nunca pelo status do
/// pagamento externo; linha gravada ANTES de devolver pago; cancelamento acompanhado até 20
/// ou até o host negar; dados do cartão (NSU/aut/rede/bandeira) e vias saindo do pagamento
/// externo mesmo quando o dump da adquirente não vem.
/// </summary>
public static class TestesControlPay
{
    private static ClienteControlPay Cliente(FakeControlPay f, Func<TransacaoPayGo, bool>? guardar = null,
        Func<TransacaoPayGo, Task<bool>>? imprimir = null, string? terminal = null, string chave = FakeControlPay.ChaveCerta,
        int desistirMs = 500, List<string>? auditoria = null)
        => new(new OpcoesControlPay(OpcoesControlPay.SandboxUrl, chave, "314159", terminal ?? f.TerminalId, f.PessoaId, "PdvNativo/teste"), f)
        {
            IntervaloPollMs = 15,
            TempoHttpMs = 5_000,
            TempoDesistirAposCancelarMs = desistirMs,
            TempoMaxOperacaoMs = 3_000,
            Guardar = guardar ?? (_ => true),
            ImprimirComprovante = imprimir,
            Auditar = auditoria is null ? null : auditoria.Add,
        };

    private static DesfechoTef Cobrar(ClienteControlPay c, TipoTef tipo, decimal reais, int parcelas = 1, CancellationToken ct = default,
        IProgress<AndamentoTef>? andamento = null)
        => c.CobrarAsync(tipo, Dinheiro.DeReais(reais), null, parcelas, andamento, ct).GetAwaiter().GetResult();

    private static JsonElement Body(FakeControlPay.Chamada c) => JsonDocument.Parse(c.Body ?? "{}").RootElement;

    public static void Rodar(Action<bool, string> checar)
    {
        // ── utilitários ───────────────────────────────────────────────────
        {
            checar(ClienteControlPay.ValorComVirgula(1234567) == "12345,67", "valorTotalVendido com vírgula e sem milhar: " + ClienteControlPay.ValorComVirgula(1234567));
            checar(ClienteControlPay.ValorComVirgula(100) == "1,00", "R$ 1,00 → \"1,00\"");
            var refs = Enumerable.Range(0, 300).Select(_ => ClienteControlPay.NovaReferencia()).ToList();
            checar(refs.Distinct().Count() == refs.Count, "referência nunca repete (300 seguidas)");
            checar(OpcoesControlPay.UrlDoAmbiente("producao") == "https://api.controlpay.com.br/" && OpcoesControlPay.UrlDoAmbiente(null) == "https://sandbox.controlpay.com.br/webapi/",
                   "produção SEM /webapi; sandbox COM /webapi");
        }

        // ── ativo / terminais ────────────────────────────────────────────
        {
            using var f = new FakeControlPay();
            using var c = Cliente(f);
            checar(c.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult(), "terminal configurado existe → ativo");
            var ts = c.ListarTerminaisAsync(CancellationToken.None).GetAwaiter().GetResult();
            checar(ts.Count == 2 && ts[0].Id == "6408" && ts[0].TerminalFisico == "Terminal Fisico 114975" && ts[1].TerminalFisico is null, "lista terminais com/sem terminal físico");
            var ch = f.Rota("Terminal/GetByPessoaId").First();
            checar(ch.Metodo == "GET" && ch.Query.Contains("pessoaId=12247") && ch.Query.Contains("key=") && ch.UserAgent == "PdvNativo/teste",
                   "GET com pessoaId, key na query e User-Agent");
            using var c2 = Cliente(f, terminal: "1");
            checar(!c2.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult(), "terminal que não existe → inativo");
            using var c3 = Cliente(f, chave: "errada");
            checar(!c3.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult(), "chave errada → inativo (401)");
        }

        // ── venda crédito 3x aprovada: caminho feliz inteiro ──────────────
        TransacaoPayGo? ultimaPaga = null;
        {
            using var f = new FakeControlPay();
            var guardadas = new List<(string situacao, int impressoes)>();
            var impressoes = 0;
            var auditoria = new List<string>();
            using var c = Cliente(f,
                guardar: t => { guardadas.Add((t.Situacao, impressoes)); if (t.Situacao == "pago") ultimaPaga = t; return true; },
                imprimir: t => { impressoes++; checar(t.Resposta!.ViaCliente.Count == 4 && t.Resposta.ViaEstabelecimento.Count == 5, "hook recebe as vias 713/715 do dump"); return Task.FromResult(true); },
                auditoria: auditoria);
            var fases = new List<string>();
            var d = Cobrar(c, TipoTef.Credito, 150.00m, 3, andamento: new Progress<AndamentoTef>(a => fases.Add(a.Fase + ":" + (a.PaymentIdentifier ?? "-"))));

            checar(d.Pago && d.Codigo == CodigoTef.Pago && d.PaymentStatus == "pago", "crédito aprovado volta Pago: " + d.Motivo);
            checar(d.PaymentIdentifier == f.UltimaIntencao.ToString(), "PaymentIdentifier = id da intenção (é o que vai na planilha da homologação)");
            checar(d.ChargeId!.StartsWith("cpay-"), "charge_id = cpay-<referencia>");
            checar(d.Cartao?.CAut == "019501" && d.Cartao.Nsu == "16175123815" && d.Cartao.Adquirente == "VISANET", "cAut/NSU/rede do pagamento externo");
            checar(d.Cartao?.Bandeira == "VISA" && d.Cartao.TBand == "01", "bandeira do PagamentoExterno/GetById (completa o dump) → tBand Visa");
            checar(d.Cartao?.Cnpj == "01027058000191", "VISANET = Cielo → CNPJ da credenciadora (tpIntegra=1)");
            checar(d.Cartao?.Valor == 150.00m, "valor ← 003 do dump");

            var vender = f.Rota("Venda/Vender").Single();
            var b = Body(vender);
            checar(b.GetProperty("formaPagamentoId").GetInt32() == 21 && b.GetProperty("terminalId").GetString() == "6408", "Vender: formaPagamentoId 21 (TEF crédito) e terminalId");
            checar(b.GetProperty("quantidadeParcelas").GetInt32() == 3 && b.GetProperty("parcelamentoAdmin").GetBoolean() == false, "Vender: 3 parcelas pela LOJA (parcelamentoAdmin=false)");
            checar(b.GetProperty("valorTotalVendido").GetString() == "150,00", "Vender: valorTotalVendido \"150,00\"");
            checar(b.GetProperty("iniciarTransacaoAutomaticamente").GetBoolean() && b.GetProperty("aguardarTefIniciarTransacao").GetBoolean(), "Vender: modo ATIVO (os dois nomes do flag)");
            checar(!string.IsNullOrEmpty(b.GetProperty("referencia").GetString()) && d.ChargeId == "cpay-" + b.GetProperty("referencia").GetString(), "referencia = a do charge_id");
            checar(vender.Query.Contains("key=" + Uri.EscapeDataString(FakeControlPay.ChaveCerta)), "chave vai URL-encoded na query");
            checar(f.Rota("IntencaoVenda/GetById").Count >= 2, "acompanhou por GetById até o final");
            checar(f.Rota("IntencaoVenda/GetByFiltros").Count >= 1 && f.Rota("PagamentoExterno/GetById").Count >= 1, "detalhes: GetByFiltros + PagamentoExterno/GetById (bandeira)");

            checar(guardadas.Select(g => g.situacao).SequenceEqual(new[] { "aguardando", "aprovada", "pago" }),
                   "guardou aguardando → aprovada → pago: " + string.Join(",", guardadas.Select(g => g.situacao)));
            checar(guardadas.First(g => g.situacao == "aprovada").impressoes == 0 && guardadas.First(g => g.situacao == "pago").impressoes == 1,
                   "gravou 'aprovada' ANTES de imprimir e 'pago' DEPOIS do comprovante");
            checar(impressoes == 1, "comprovante impresso uma vez");
            checar(fases.Count >= 2 && fases[0].StartsWith("criando:") && fases.Any(x => x.StartsWith("aguardando:" + d.PaymentIdentifier)), "andamento criando → aguardando(com id)");
            checar(ultimaPaga?.Identificacao == d.PaymentIdentifier && ultimaPaga.Resposta?.Nsu == "16175123815", "linha guardada: identificacao = id da intenção, NSU da resposta");
            checar(auditoria.Count > 0 && auditoria.All(a => !a.Contains(FakeControlPay.ChaveCerta) && !a.Contains(Uri.EscapeDataString(FakeControlPay.ChaveCerta))), "auditoria NUNCA carrega a chave");
        }

        // ── débito e pix: forma certa, sem parcelas ───────────────────────
        {
            using var f = new FakeControlPay();
            using var c = Cliente(f);
            checar(Cobrar(c, TipoTef.Debito, 20m, 5).Pago, "débito aprovado");
            checar(Cobrar(c, TipoTef.Pix, 10m).Pago, "pix aprovado");
            var vs = f.Rota("Venda/Vender");
            checar(Body(vs[0]).GetProperty("formaPagamentoId").GetInt32() == 22 && Body(vs[0]).GetProperty("quantidadeParcelas").GetInt32() == 1, "débito: forma 22, 1 parcela mesmo pedindo 5");
            checar(Body(vs[1]).GetProperty("formaPagamentoId").GetInt32() == 25, "pix: forma 25 (TEF carteira digital)");
        }

        // ── recusada / cancelada no pinpad ────────────────────────────────
        {
            using var f = new FakeControlPay();
            f.Roteiro.Enqueue(FakeControlPay.Desfecho.Recusar);
            var sits = new List<string>();
            using var c = Cliente(f, guardar: t => { sits.Add(t.Situacao); return true; });
            var d = Cobrar(c, TipoTef.Credito, 1000.01m);
            checar(d.Situacao == SituacaoTef.Recusado && d.Codigo == CodigoTef.Recusado, "recusada volta Recusado");
            checar(d.Motivo == "NEGADA 01", "motivo = mensagem da adquirente (roteiro P4): " + d.Motivo);
            checar(sits.Last() == "recusado" && d.PaymentIdentifier == f.UltimaIntencao.ToString(), "guardou recusado com o id da intenção");
        }
        {
            using var f = new FakeControlPay();
            f.Roteiro.Enqueue(FakeControlPay.Desfecho.CancelarNoPinpad);
            using var c = Cliente(f);
            var d = Cobrar(c, TipoTef.Pix, 10m);
            checar(d.Situacao == SituacaoTef.Cancelado && d.Codigo == CodigoTef.Cancelado && d.Motivo == "OPERAÇÃO CANCELADA", "Esc no QR/pinpad → Cancelado (P5/P52): " + d.Motivo);
        }

        // ── expirada: PayGo não pegou ─────────────────────────────────────
        {
            using var f = new FakeControlPay();
            f.Roteiro.Enqueue(FakeControlPay.Desfecho.ExpirarNoVender);
            f.Roteiro.Enqueue(FakeControlPay.Desfecho.ExpirarDepois);
            var sits = new List<string>();
            using var c = Cliente(f, guardar: t => { sits.Add(t.Situacao); return true; });
            var d1 = Cobrar(c, TipoTef.Credito, 10m);
            checar(d1.Situacao == SituacaoTef.Erro && d1.Codigo == CodigoTef.TefNaoResponde && (d1.Motivo ?? "").StartsWith("TEF não responde"), "expirou no Vender → TEF não responde: " + d1.Motivo);
            var d2 = Cobrar(c, TipoTef.Credito, 10m);
            checar(d2.Situacao == SituacaoTef.Erro && d2.Codigo == CodigoTef.TefNaoResponde, "expirou depois → TEF não responde");
            checar(sits.Count(s => s == "erro") == 2 && !sits.Contains("pago"), "guardou erro, nunca pago");
        }

        // ── operador cancela e a intenção nunca termina → órfã ────────────
        {
            using var f = new FakeControlPay();
            f.Roteiro.Enqueue(FakeControlPay.Desfecho.NuncaTermina);
            var sits = new List<string>();
            var recados = new List<string>();
            using var c = Cliente(f, guardar: t => { sits.Add(t.Situacao); return true; }, desistirMs: 200);
            using var cts = new CancellationTokenSource(60);
            var sw = Stopwatch.StartNew();
            var d = Cobrar(c, TipoTef.Credito, 10m, ct: cts.Token, andamento: new Progress<AndamentoTef>(a => { if (a.Fase == FaseTef.Recado) recados.Add(a.Mensagem); }));
            checar(d.Situacao == SituacaoTef.Cancelado && d.PosPodeTerFicadoOcupado, "cancelou e o PayGo não terminou → Cancelado COM aviso");
            checar(sw.ElapsedMilliseconds >= 200 && sw.ElapsedMilliseconds < 3000, $"desistiu só após o prazo pós-cancelamento ({sw.ElapsedMilliseconds} ms)");
            checar(sits.Last() == "orfa", "guardou órfã");
            checar(recados.Any(r => r.Contains("janela do PayGo")), "recado manda cancelar na janela do PayGo");
        }

        // ── erro HTTP: mensagem sem chave; 401 = chave recusada ───────────
        {
            using var f = new FakeControlPay();
            f.Roteiro.Enqueue(FakeControlPay.Desfecho.Erro500);
            var auditoria = new List<string>();
            using var c = Cliente(f, auditoria: auditoria);
            var d = Cobrar(c, TipoTef.Credito, 10m);
            checar(d.Situacao == SituacaoTef.Erro && d.Codigo == CodigoTef.Plataforma, "HTTP 500 no Vender → Erro plataforma: " + d.Motivo);
            checar(!d.Motivo!.Contains(FakeControlPay.ChaveCerta) && !d.Motivo.Contains(Uri.EscapeDataString(FakeControlPay.ChaveCerta)) && d.Motivo.Contains("***"),
                   "mensagem de erro NÃO carrega a chave (sanitizada): " + d.Motivo);
            checar(auditoria.All(a => !a.Contains(FakeControlPay.ChaveCerta)), "auditoria do erro sem chave");
            using var c2 = Cliente(f, chave: "errada");
            var d2 = Cobrar(c2, TipoTef.Credito, 10m);
            checar(d2.Situacao == SituacaoTef.Erro && (d2.Motivo ?? "").Contains("chave de integração recusada"), "401 → 'chave de integração recusada': " + d2.Motivo);
        }

        // ── gravar falhou depois de aprovada: NÃO cobrar de novo ──────────
        {
            using var f = new FakeControlPay();
            var sits = new List<string>();
            using var c = Cliente(f, guardar: t => { sits.Add(t.Situacao); return t.Situacao != "aprovada"; });
            var d = Cobrar(c, TipoTef.Credito, 10m);
            checar(d.Situacao == SituacaoTef.Erro && !d.Pago && d.PosPodeTerFicadoOcupado, "aprovada + não gravou → Erro com aviso (não há NCN no ControlPay)");
            checar((d.Motivo ?? "").Contains("NÃO cobre de novo"), "mensagem proíbe cobrar de novo: " + d.Motivo);
            checar(!sits.Contains("pago"), "nunca gravou pago");
            checar(d.Cartao?.Nsu == "16175123815", "devolve os dados do cartão para o operador registrar/estornar");
        }

        // ── compat: lista com nome antigo e resposta SEM dump ─────────────
        {
            using var f = new FakeControlPay { NomeAntigoDaLista = true, SemDump = true };
            var impressas = 0;
            using var c = Cliente(f, imprimir: t =>
            {
                impressas++;
                checar(t.Resposta!.ViaCliente.Count == 4 && t.Resposta.ViaEstabelecimento.Count == 5 && t.Resposta.ViaCliente[1] == " VIA: CLIENTE",
                       "sem dump: vias montadas dos comprovantes em texto (quebra por CRLF, sem linhas vazias nas pontas)");
                return Task.FromResult(true);
            });
            var d = Cobrar(c, TipoTef.Credito, 10m);
            checar(d.Pago && d.Cartao?.Nsu == "16175123815" && d.Cartao.CAut == "019501" && d.Cartao.Adquirente == "VISANET" && d.Cartao.Bandeira == "VISA",
                   "'pagamentosExterno' (nome antigo) + sem dump: NSU/aut/rede/bandeira vêm dos campos estruturados");
            checar(impressas == 1, "imprimiu com as vias sintéticas");
        }

        // ── cancelamento (estorno) ────────────────────────────────────────
        {
            using var f = new FakeControlPay();
            var sits = new List<(string id, string s)>();
            var impressas = 0;
            using var c = Cliente(f, guardar: t => { sits.Add((t.ChargeId, t.Situacao)); return true; }, imprimir: _ => { impressas++; return Task.FromResult(true); });
            var venda = Cobrar(c, TipoTef.Credito, 12345.67m);
            checar(venda.Pago, "venda para estornar aprovada (P19 R$ 12.345,67)");
            var original = new TransacaoPayGo(venda.ChargeId!, venda.PaymentIdentifier!, TipoTef.Credito, 1234567, 1, "pago", null);
            var d = c.CancelarAsync(original, CancellationToken.None).GetAwaiter().GetResult();
            checar(d.Pago && d.PaymentStatus == "estornado", "CNC aprovado → estornado: " + d.Motivo);
            var cb = Body(f.Rota("Venda/CancelarVenda").Single());
            checar(cb.GetProperty("intencaoVendaId").GetString() == venda.PaymentIdentifier && cb.GetProperty("terminalId").GetString() == "6408" && cb.GetProperty("senhaTecnica").GetString() == "314159",
                   "CancelarVenda leva id da intenção, MESMO terminal e senha técnica");
            checar(sits.Any(s => s.id.StartsWith("cpay-cnc-") && s.s == "estornado") && sits.Any(s => s.id == venda.ChargeId && s.s == "estornada"),
                   "linha do cancelamento 'estornado' e a original 'estornada'");
            checar(impressas == 2, "comprovante da venda e do cancelamento impressos");
        }
        {
            using var f = new FakeControlPay();
            f.RoteiroCancelamento.Enqueue(FakeControlPay.DesfechoCancelamento.NegarPeloHost);
            using var c = Cliente(f);
            var venda = Cobrar(c, TipoTef.Pix, 500m);
            var original = new TransacaoPayGo(venda.ChargeId!, venda.PaymentIdentifier!, TipoTef.Pix, 50000, 1, "pago", null);
            var d = c.CancelarAsync(original, CancellationToken.None).GetAwaiter().GetResult();
            checar(!d.Pago && d.Situacao == SituacaoTef.Recusado && d.Motivo == "TRANSAÇÃO NEGADA PELO HOST", "cancelamento PIX negado pelo host (P54): " + d.Motivo);
        }
        {
            using var f = new FakeControlPay();
            using var c = Cliente(f);
            var d = c.CancelarAsync(new TransacaoPayGo("cpay-x", "", TipoTef.Credito, 100, 1, "pago", null), CancellationToken.None).GetAwaiter().GetResult();
            checar(!d.Pago && (d.Motivo ?? "").Contains("sem id de intenção"), "original sem id → não tenta");
        }

        // ── administrativa ────────────────────────────────────────────────
        {
            using var f = new FakeControlPay();
            using var c = Cliente(f);
            var d = c.AdministrativaAsync(CancellationToken.None).GetAwaiter().GetResult();
            checar(d.Pago && d.PaymentStatus == "adm", "ADM concluída (PagamentoExterno tipo 15 finalizado): " + d.Motivo);
            checar(f.Rota("PagamentoExterno/GetById").Count >= 2, "acompanhou a ADM por PagamentoExterno/GetById até finalizar");
            checar(Body(f.Rota("PagamentoExterno/InsertPagamentoExternoTipoAdmin").Single()).GetProperty("senhaTecnica").GetString() == "314159", "ADM leva a senha técnica");
        }

        // ── religamento: aguardando → status real ─────────────────────────
        {
            using var f = new FakeControlPay();
            f.Roteiro.Enqueue(FakeControlPay.Desfecho.Aprovar);
            f.Roteiro.Enqueue(FakeControlPay.Desfecho.Recusar);
            using var c0 = Cliente(f);
            var a = Cobrar(c0, TipoTef.Credito, 10m);
            var b = Cobrar(c0, TipoTef.Credito, 20m);
            var guardadas = new List<TransacaoPayGo>();
            using var c = Cliente(f, guardar: t => { guardadas.Add(t); return true; });
            var n = c.ReconciliarAsync(new[]
            {
                new TransacaoPayGo("cpay-1", a.PaymentIdentifier!, TipoTef.Credito, 1000, 1, "aguardando", null),
                new TransacaoPayGo("cpay-2", b.PaymentIdentifier!, TipoTef.Credito, 2000, 1, "aguardando", null),
                new TransacaoPayGo("cpay-3", "", TipoTef.Credito, 2000, 1, "aguardando", null),
            }).GetAwaiter().GetResult();
            checar(n == 2, $"reconciliou as 2 com id ({n})");
            checar(guardadas.Any(g => g.ChargeId == "cpay-1" && g.Situacao == "pago" && (g.Motivo ?? "").Contains("SEM VENDA") && g.Resposta?.Nsu == "16175123815"),
                   "aprovada sem venda → 'pago' marcada órfã (o fechamento acusa; estorno pelo menu TEF)");
            checar(guardadas.Any(g => g.ChargeId == "cpay-2" && g.Situacao == "recusado"), "recusada → 'recusado'");
        }

        // ── resposta sintética ────────────────────────────────────────────
        {
            using var doc = JsonDocument.Parse("""{"id":1,"nsuTid":"123","autorizacao":"ABC","adquirente":"REDE","bandeira":"MASTERCARD","mensagemRespostaAdquirente":"OK","comprovanteCliente":" \r\nLINHA 1\r\nLINHA 2\r\n \r\n","comprovanteEstabelecimento":null}""");
            var r = ClienteControlPay.RespostaSintetica(doc.RootElement, 990, TipoTef.Debito, "77");
            checar(r.Aprovada && r.Nsu == "123" && r.Autorizacao == "ABC" && r.Rede == "REDE" && r.NomeCartao == "MASTERCARD" && r.ValorCent == 990 && r.TipoCartao == 2,
                   "sintética: campos estruturados viram 012/013/010/040/003/731");
            checar(r.ViaCliente.Count == 2 && r.ViaCliente[0] == "LINHA 1" && r.ViaEstabelecimento.Count == 0 && r.Vias == 1 && !r.RequerConfirmacao,
                   "sintética: via cliente de 2 linhas, 737=1, 729=1 (nada a confirmar)");
            checar(ClientePayGo.TBand("MASTERCARD") == "02" && ClientePayGo.CnpjConhecido("REDE") == "01425787000104", "tBand/CNPJ pela rede e bandeira");
        }
    }
}
