using System.Diagnostics;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// TEF PayGo Windows por troca de arquivos, contra o <see cref="FakePayGo"/>.
///
/// O que está aqui é o que a spec chama de inegociável: .tmp + rename, .sts em 7 s,
/// SEM timeout depois do .sts, two-phase commit (aprovou → gravou → CNF; qualquer
/// desistência → NCN), resposta órfã desfeita, religamento sem perguntar ao operador.
/// Os arquivos de resposta são os do kit de integração (Passo 19), verbatim.
/// </summary>
public static class TestesPayGo
{
    /// <summary>Resp\intpos.001 do Passo 19 do kit — copiado da documentação, sem retoque.</summary>
    private static readonly string RespostaPasso19 = string.Join("\r\n", new[]
    {
        "000-000 = CRT", "001-000 = 69746", "003-000 = 1234567", "004-000 = 0", "009-000 = 0",
        "010-000 = DEMO", "011-000 = 10", "012-000 = 721554", "013-000 = 543733",
        "015-000 = 3103120343", "016-000 = 3103120343", "022-000 = 31032025", "023-000 = 120343",
        "027-000 = 310320251203721554", "028-000 = 0", "030-000 = TRANSACAO AUTORIZADA",
        "040-000 = VISA", "710-000 = 5",
        "711-001 = \" *** PAYGO - AMBIENTE SANDBOX *** \"",
        "711-002 = \"--------------------------------------\"",
        "711-003 = \"86132 EC:0000001380 REF:0000003801\"",
        "711-004 = \" \"",
        "711-005 = \" TRANSACAO TESTE SEM VALOR FINANCEIRO! \"",
        "712-000 = 14",
        "713-001 = \" *** PAYGO - AMBIENTE SANDBOX *** \"",
        "713-002 = \"VIA CLIENTE 31/MAR/25 12:03\"",
        "713-003 = \"SETIS*SETIS\"",
        "713-004 = \"CNPJ:03.361.770/0001-58 PDC:86132\"",
        "713-005 = \"REF:3801 EC:1380\"",
        "713-006 = \"C-489391******0008 VISA CREDITO\"",
        "713-007 = \"AID:A0000000031010\"",
        "713-008 = \" VENDA CREDITO A VISTA \"",
        "713-009 = \"VALOR FINAL: R$ 12.345,67\"",
        "713-010 = \" \"",
        "713-011 = \"--------------------------------------\"",
        "713-012 = \"86132 EC:0000001380 REF:0000003801\"",
        "713-013 = \" \"",
        "713-014 = \" TRANSACAO TESTE SEM VALOR FINANCEIRO! \"",
        "714-000 = 16",
        "715-001 = \" *** PAYGO - AMBIENTE SANDBOX *** \"",
        "715-002 = \"VIA ESTABELECIMENTO 31/MAR/25 12:03\"",
        "715-003 = \"SETIS*SETIS\"",
        "715-004 = \"CNPJ:03.361.770/0001-58 PDC:86132\"",
        "715-005 = \"REF:3801 EC:1380\"",
        "715-006 = \"C-489391******0008 VISA CREDITO\"",
        "715-007 = \"AID:A0000000031010\"",
        "715-008 = \"ARQC:2027E71B1A9D9755\"",
        "715-009 = \" VENDA CREDITO A VISTA \"",
        "715-010 = \"VALOR FINAL: R$ 12.345,67\"",
        "715-011 = \" TRANSACAO AUTORIZADA COM SENHA \"",
        "715-012 = \" \"",
        "715-013 = \"--------------------------------------\"",
        "715-014 = \"86132 EC:0000001380 REF:0000003801\"",
        "715-015 = \" \"",
        "715-016 = \" TRANSACAO TESTE SEM VALOR FINANCEIRO! \"",
        "718-000 = 86132", "719-000 = 03361770000158", "729-000 = 2", "730-000 = 1",
        "731-000 = 1", "732-000 = 1", "737-000 = 3", "739-000 = 100",
        "740-000 = 4***********0008", "747-000 = 0230", "748-000 = VISA CREDITO",
        "999-999 = 0", "",
    });

    private static ClientePayGo Cliente(FakePayGo f, Func<TransacaoPayGo, bool>? guardar = null, int desistirMs = 600,
        OpcoesPayGo? opcoes = null, Func<string, bool>? conhecida = null)
        => new(f.Pasta, opcoes ?? new OpcoesPayGo("Setis", "Teste", "v1", "G45J35G3JH45B435"))
        {
            TempoStsMs = 1500,
            IntervaloPollMs = 20,
            TempoDesistirAposCancelarMs = desistirMs,
            Guardar = guardar ?? (_ => true),
            ConhecidaConfirmada = conhecida,
        };

    private static DesfechoTef Cobrar(ClientePayGo c, TipoTef tipo, decimal reais, int parcelas = 1,
        CancellationToken ct = default, IProgress<AndamentoTef>? andamento = null)
        => c.CobrarAsync(tipo, Dinheiro.DeReais(reais), null, parcelas, andamento, ct).GetAwaiter().GetResult();

    private static bool VazioResp(FakePayGo f)
        => !File.Exists(Path.Combine(f.Resp, "intpos.sts")) && !File.Exists(Path.Combine(f.Resp, "intpos.001"));

    public static void Rodar(Action<bool, string> checar)
    {
        // ── formato do arquivo ─────────────────────────────────────────────
        {
            var txt = ArquivoIntpos.Serializar(new Dictionary<string, string>
            {
                ["999-999"] = "0",                       // dado fora de ordem de propósito
                ["000-000"] = "CRT",
                ["716-000"] = "Açaí & Cia Ltda — São João",
                ["003-000"] = "1500",
            });
            var linhas = txt.Split("\r\n");
            checar(txt.EndsWith("999-999 = 0\r\n"), "serializa com 999-999 = 0 como ÚLTIMA linha (mesmo se veio primeiro)");
            checar(!txt.Contains('\n') || txt.Replace("\r\n", "").IndexOf('\n') < 0, "todas as quebras são CRLF");
            checar(linhas[0] == "000-000 = CRT", "linha = campo, espaço, igual, espaço, valor");
            checar(txt.Contains("716-000 = Acai & Cia Ltda  Sao Joao"), "acento e travessão saem (só ASCII 20h-7Eh): " + linhas[1]);
            checar(txt.All(ch => ch == '\r' || ch == '\n' || (ch >= 0x20 && ch <= 0x7E)), "nenhum byte fora de 20h-7Eh");
        }

        // ── parse da resposta do kit (Passo 19) ───────────────────────────
        {
            var r = RespostaPayGo.Analisar(RespostaPasso19 + "LINHA TORTA SEM IGUAL\r\n123 = x\r\n");
            checar(r.Aprovada && r.Status == 0, "009-000 = 0 é aprovada");
            checar(r.RequerConfirmacao, "729-000 = 2 requer CNF/NCN");
            checar(r.CodigoControle == "310320251203721554", "027 código de controle");
            checar(r.Nsu == "721554" && r.Autorizacao == "543733", "012 NSU e 013 autorização");
            checar(r.ValorCent == 1234567, "003 valor em centavos");
            checar(r.Rede == "DEMO" && r.NomeCartao == "VISA" && r.Produto == "VISA CREDITO", "010/040/748");
            checar(r.Terminal == "86132" && r.Estabelecimento == "03361770000158", "718/719");
            checar(r.Data == "31032025" && r.Hora == "120343", "022/023 data e hora do comprovante");
            checar(r.TipoCartao == 1 && r.Financiamento == 1 && r.Vias == 3, "731/732/737");
            checar(r.ViaCliente.Count == 14 && r.ViaEstabelecimento.Count == 16 && r.CupomReduzido.Count == 5,
                   $"vias com a contagem dos campos 712/714/710 ({r.ViaCliente.Count}/{r.ViaEstabelecimento.Count}/{r.CupomReduzido.Count})");
            checar(r.ViaCliente[1] == "VIA CLIENTE 31/MAR/25 12:03", "aspas saem, conteúdo fica: " + r.ViaCliente[1]);
            checar(r.ViaEstabelecimento[10] == " TRANSACAO AUTORIZADA COM SENHA ", "espaços internos preservados");
            checar(r.ViaUnica.Count == 0, "028-000 = 0: sem via única");
            checar(r.Mensagem == "TRANSACAO AUTORIZADA", "030 mensagem ao operador");
            checar(r.Texto.StartsWith("000-000 = CRT"), "resposta guarda o texto cru (auditoria/religamento)");
            checar(r.ViasJson().Contains("VIA CLIENTE"), "vias viram JSON para tef_transacao.vias_json");

            var semConf = RespostaPayGo.Analisar("000-000 = CRT\r\n009-000 = 0\r\n729-000 = 1\r\n999-999 = 0\r\n");
            checar(!semConf.RequerConfirmacao, "729-000 = 1 não requer confirmação");
            var antigo = RespostaPayGo.Analisar("000-000 = CRT\r\n009-000 = 0\r\n712-000 = 1\r\n713-001 = \"x\"\r\n999-999 = 0\r\n");
            checar(antigo.RequerConfirmacao, "729 ausente + comprovante = requer (compat com PayGo antigo)");
            var negada = RespostaPayGo.Analisar("000-000 = CRT\r\n009-000 = 7\r\n030-000 = SALDO INSUFICIENTE\r\n999-999 = 0\r\n");
            checar(!negada.Aprovada && negada.Mensagem == "SALDO INSUFICIENTE", "negada carrega a 030 para a tela");
        }

        // ── identificação (001-000) ───────────────────────────────────────
        {
            var ids = Enumerable.Range(0, 500).Select(_ => ClientePayGo.NovaIdentificacao()).ToList();
            checar(ids.All(i => i.Length == 10 && i.All(char.IsDigit)), "001-000 é n..10");
            checar(ids.Distinct().Count() == ids.Count, "001-000 nunca repete (500 seguidas no mesmo segundo)");
            checar(ids.Zip(ids.Skip(1)).All(p => long.Parse(p.Second) > long.Parse(p.First)), "001-000 é crescente");
        }

        // ── ATV: PayGo de pé / desligado ──────────────────────────────────
        {
            using var f = new FakePayGo();
            var c = Cliente(f);
            var ok = c.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult();
            checar(ok, "ATV com PayGo de pé → ativo");
            var atv = f.Comandos("ATV");
            checar(atv.Count == 1 && atv[0]["733-000"] == "210" && atv[0]["738-000"] == "G45J35G3JH45B435",
                   "ATV leva 733 (interface 210) e 738 (registro)");
            checar(VazioResp(f), "depois do ATV a pasta Resp fica limpa (o PDV apaga o que lê)");
        }
        {
            using var f = new FakePayGo { SemStsTudo = true };
            var c = Cliente(f);
            var sw = Stopwatch.StartNew();
            var ok = c.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult();
            checar(!ok, "ATV sem .sts → inativo");
            checar(sw.ElapsedMilliseconds >= 1400 && sw.ElapsedMilliseconds < 4000, $"esperou o tempo do .sts e desistiu ({sw.ElapsedMilliseconds} ms)");
            checar(!File.Exists(Path.Combine(f.Req, "intpos.001")) || f.Esperar(() => !File.Exists(Path.Combine(f.Req, "intpos.001")), 500),
                   "Req\\intpos.001 é removido quando o PayGo não responde (senão ele roda sozinho quando abrir)");
        }

        // ── venda crédito 3x aprovada: o caminho feliz inteiro ────────────
        TransacaoPayGo? ultimaPaga = null;
        {
            using var f = new FakePayGo();
            var guardadas = new List<(string situacao, int cnfsNaHora)>();
            var c = Cliente(f, t =>
            {
                guardadas.Add((t.Situacao, f.Quantos("CNF")));
                if (t.Situacao == "pago") ultimaPaga = t;
                return true;
            });
            var fases = new List<string>();
            var progresso = new Progress<AndamentoTef>(a => fases.Add(a.Fase + ":" + (a.PaymentIdentifier ?? "-")));

            var d = Cobrar(c, TipoTef.Credito, 150.00m, 3, andamento: progresso);

            checar(d.Pago && d.Codigo == CodigoTef.Pago, "crédito aprovado volta Pago: " + d.Motivo);
            checar(d.Cartao?.CAut == "543733", "cAut ← 013");
            checar(d.Cartao?.Nsu is not null && d.Cartao.Nsu.Length == 6, "NSU ← 012");
            checar(d.Cartao?.Bandeira == "VISA" && d.Cartao.TBand == "01", "bandeira ← 040 e tBand Visa = 01");
            checar(d.Cartao?.Adquirente == "DEMO" && d.Cartao.Terminal == "86132", "adquirente ← 010, terminal ← 718");
            checar(d.Cartao?.Valor == 150.00m, "valor ← 003 (conferência contra a venda)");
            checar(d.Cartao?.Cnpj is null && d.Cartao?.ServeParaXml == false, "rede DEMO sem CNPJ conhecido → tpIntegra=2 (honesto)");
            checar(d.ChargeId!.StartsWith("paygo-") && d.PaymentIdentifier == d.ChargeId[6..], "charge_id = paygo-<001>, pid = 001");
            checar(d.PaymentStatus == "pago", "payment_status final = pago");

            var crt = f.Comandos("CRT").Single();
            checar(crt["003-000"] == "15000" && crt["004-000"] == "0", "CRT: valor em centavos e moeda 0");
            checar(crt["731-000"] == "1" && crt["732-000"] == "3" && crt["018-000"] == "3", "CRT crédito 3x: 731=1, 732=3 (estabelecimento), 018=3");
            checar(crt["706-000"] == ClientePayGo.CapacidadesPadrao.ToString() && crt["706-000"] == "156", "706 capacidades = 156 (4+8+16+128)");
            checar(crt["716-000"] == "Setis" && crt["735-000"] == "Teste" && crt["736-000"] == "v1" && crt["738-000"] == "G45J35G3JH45B435",
                   "CRT: 716/735/736/738 identificam a automação");
            checar(crt["001-000"] == d.PaymentIdentifier, "CRT 001 = identificação devolvida como pid");

            var cnf = f.Comandos("CNF").Single();
            checar(cnf["001-000"] == crt["001-000"], "CNF usa a MESMA 001 da venda");
            checar(cnf["027-000"] == ultimaPaga?.CodigoControle && cnf["027-000"]!.Length > 10, "CNF leva o 027 da resposta");
            checar(f.Quantos("NCN") == 0, "venda boa não manda NCN");

            checar(guardadas.Select(g => g.situacao).SequenceEqual(new[] { "aguardando", "aprovada", "pago" }),
                   "guardou aguardando → aprovada → pago: " + string.Join(",", guardadas.Select(g => g.situacao)));
            checar(guardadas.First(g => g.situacao == "aprovada").cnfsNaHora == 0,
                   "a transação foi GRAVADA antes de o CNF sair (memória não volátil primeiro)");
            checar(fases.Count >= 2 && fases[0].StartsWith("criando:") && fases.Any(x => x.StartsWith("aguardando:" + d.PaymentIdentifier)),
                   "andamento reporta criando e aguardando (com o pid) para a tela gravar tef_transacao");
            checar(!f.ViuArquivoParcial, "o PayGo NUNCA viu intpos.001 pela metade (.tmp + rename)");
            checar(VazioResp(f) && !File.Exists(Path.Combine(f.Req, "intpos.001")), "pastas limpas ao final");
            checar(ultimaPaga?.Resposta?.ViaCliente.Count == 14, "a transação guardada carrega as vias (reimpressão)");
        }

        // ── débito e pix: campos certos ───────────────────────────────────
        {
            using var f = new FakePayGo();
            var c = Cliente(f);
            var d = Cobrar(c, TipoTef.Debito, 89.90m, 5);
            checar(d.Pago, "débito aprovado");
            var crt = f.Comandos("CRT").Single();
            checar(crt["731-000"] == "2" && crt["732-000"] == "1" && !crt.ContainsKey("018-000"), "débito: 731=2, 732=1, sem 018 mesmo com parcelas=5");
            checar(d.Cartao?.Bandeira == "VISA ELECTRO" && d.Cartao.TBand == "01", "débito devolve o nome do cartão da resposta");
        }
        {
            using var f = new FakePayGo();
            var c = Cliente(f);
            var d = Cobrar(c, TipoTef.Pix, 10m);
            checar(d.Pago, "pix aprovado");
            var crt = f.Comandos("CRT").Single();
            checar(crt["731-000"] == "0" && crt["749-000"] == "8" && crt["750-000"] == "4", "pix: 731=0 (qualquer), 749=8 (carteira digital), 750=4 (QR dinâmico)");
        }

        // ── recusada ──────────────────────────────────────────────────────
        {
            using var f = new FakePayGo();
            f.Roteiro.Enqueue(FakePayGo.Desfecho.Recusar);
            var sits = new List<string>();
            var c = Cliente(f, t => { sits.Add(t.Situacao); return true; });
            var d = Cobrar(c, TipoTef.Credito, 50m);
            checar(d.Situacao == SituacaoTef.Recusado && d.Codigo == CodigoTef.Recusado, "negada volta Recusado");
            checar(d.Motivo == "TRANSACAO NAO AUTORIZADA", "motivo = 030-000 literal: " + d.Motivo);
            checar(f.Quantos("CNF") == 0 && f.Quantos("NCN") == 0, "negada não confirma nem desfaz");
            checar(sits.Last() == "recusado", "guardou recusado");
            checar(VazioResp(f), "Resp limpa após recusa");
        }

        // ── sem .sts: PayGo inativo ───────────────────────────────────────
        {
            using var f = new FakePayGo();
            f.Roteiro.Enqueue(FakePayGo.Desfecho.SemSts);
            var sits = new List<string>();
            var c = Cliente(f, t => { sits.Add(t.Situacao); return true; });
            var d = Cobrar(c, TipoTef.Credito, 20m);
            checar(d.Situacao == SituacaoTef.Erro && d.Codigo == CodigoTef.TefNaoResponde, "sem .sts → Erro tef_nao_responde");
            checar((d.Motivo ?? "").Contains("TEF não responde"), "mensagem manda abrir o PayGo: " + d.Motivo);
            checar(!d.PosPodeTerFicadoOcupado, "sem ack nada foi armado — sem aviso de maquininha ocupada");
            checar(sits.Count == 0, "sem ack não grava 'aguardando' (não existe cobrança)");
            checar(f.Esperar(() => !File.Exists(Path.Combine(f.Req, "intpos.001")), 500), "Req limpo: o comando não fica para rodar quando abrirem o PayGo");
        }

        // ── gravar falhou → NCN ───────────────────────────────────────────
        {
            using var f = new FakePayGo();
            var c = Cliente(f, t => t.Situacao != "aprovada");   // falha só no passo que importa
            var d = Cobrar(c, TipoTef.Credito, 30m);
            checar(d.Situacao == SituacaoTef.Erro && !d.Pago, "não conseguiu gravar → NÃO é pago");
            checar(f.Quantos("NCN") == 1 && f.Quantos("CNF") == 0, "não conseguiu gravar → NCN (nunca CNF)");
            checar(f.Comandos("NCN")[0]["027-000"].Length > 10, "NCN leva o 027 da resposta");
            checar((d.Motivo ?? "").Contains("desfeita"), "mensagem diz que foi desfeita: " + d.Motivo);
        }

        // ── operador cancela DURANTE a espera: TEF não é interrompido; aprovou → NCN ──
        {
            using var f = new FakePayGo { AtrasoRespostaMs = 400 };
            var sits = new List<string>();
            var c = Cliente(f, t => { sits.Add(t.Situacao); return true; });
            using var cts = new CancellationTokenSource(60);
            var recados = new List<string>();
            var d = Cobrar(c, TipoTef.Credito, 40m, ct: cts.Token,
                andamento: new Progress<AndamentoTef>(a => { if (a.Fase == FaseTef.Recado) recados.Add(a.Mensagem); }));
            checar(d.Situacao == SituacaoTef.Cancelado, "cancelou e o PayGo aprovou depois → Cancelado (não Pago)");
            checar(f.Quantos("NCN") == 1 && f.Quantos("CNF") == 0, "aprovação depois do cancelamento é DESFEITA (NCN)");
            checar(sits.Last() == "desfeita", "guardou desfeita");
            checar(!d.PosPodeTerFicadoOcupado, "desfeita com ack: maquininha livre, sem aviso");
            checar(f.Esperar(() => recados.Count > 0, 500) && recados[0].Contains("não pode ser interrompido"),
                   "tela recebe o recado de que o TEF não pode ser interrompido");
        }

        // ── operador cancela e o PayGo some: órfã; resposta tardia é desfeita no próximo comando ──
        {
            using var f = new FakePayGo();
            f.Roteiro.Enqueue(FakePayGo.Desfecho.Sumir);
            var sits = new List<string>();
            var c = Cliente(f, t => { sits.Add(t.Situacao); return true; }, desistirMs: 300);
            using var cts = new CancellationTokenSource(60);
            var sw = Stopwatch.StartNew();
            var d = Cobrar(c, TipoTef.Credito, 25m, ct: cts.Token);
            checar(d.Situacao == SituacaoTef.Cancelado && d.PosPodeTerFicadoOcupado, "PayGo mudo após cancelar → Cancelado COM aviso de maquininha");
            checar(sw.ElapsedMilliseconds >= 300 && sw.ElapsedMilliseconds < 3000, $"desistiu só depois do prazo pós-cancelamento ({sw.ElapsedMilliseconds} ms)");
            checar(sits.Last() == "orfa", "guardou órfã");

            // resposta tardia aprovada aparece na pasta → o próximo comando (ATV) desfaz antes de tudo
            f.PlantarRespostaOrfa(d.PaymentIdentifier!, "CTRL-TARDIO-1");
            var ok = c.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult();
            checar(ok, "ATV depois da órfã funciona");
            var ncn = f.Comandos("NCN");
            checar(ncn.Count == 1 && ncn[0]["027-000"] == "CTRL-TARDIO-1", "resposta órfã aprovada foi DESFEITA com o 027 dela");
            var ordem = f.Recebidos.Select(r => r["000-000"]).ToList();
            checar(ordem.IndexOf("NCN") < ordem.LastIndexOf("ATV"), "o NCN da órfã sai ANTES do comando novo");
        }

        // ── valor divergente → NCN ────────────────────────────────────────
        {
            using var f = new FakePayGo();
            f.Roteiro.Enqueue(FakePayGo.Desfecho.AprovarValorDivergente);
            var sits = new List<string>();
            var c = Cliente(f, t => { sits.Add(t.Situacao); return true; });
            var d = Cobrar(c, TipoTef.Credito, 10m);
            checar(d.Situacao == SituacaoTef.Erro && d.Codigo == CodigoTef.ValorDivergente, "valor diferente do pedido → valor_divergente");
            checar(f.Quantos("NCN") == 1 && f.Quantos("CNF") == 0, "valor divergente é DESFEITO, não confirmado");
            checar(sits.Last() == "desfeita", "guardou desfeita");
            checar((d.Motivo ?? "").Contains("11,00") && d.Motivo!.Contains("10,00"), "mensagem mostra os dois valores: " + d.Motivo);
        }

        // ── antivírus segurando o arquivo ─────────────────────────────────
        {
            using var f = new FakePayGo { TravarRespostaMs = 400 };
            var c = Cliente(f);
            var d = Cobrar(c, TipoTef.Credito, 10m);
            checar(d.Pago, "arquivo travado por 400 ms: o PDV re-tenta e lê quando soltar");
            checar(f.Quantos("CNF") == 1, "e confirma normalmente");
        }

        // ── 729 = 1: não requer confirmação ───────────────────────────────
        {
            using var f = new FakePayGo();
            f.Roteiro.Enqueue(FakePayGo.Desfecho.AprovarSemConfirmacao);
            var sits = new List<string>();
            var c = Cliente(f, t => { sits.Add(t.Situacao); return true; });
            var d = Cobrar(c, TipoTef.Credito, 10m);
            checar(d.Pago && f.Quantos("CNF") == 0 && f.Quantos("NCN") == 0, "729=1: pago sem CNF");
            checar(sits.Last() == "pago", "guardou pago");
        }

        // ── cancelamento (CNC) de uma venda confirmada ────────────────────
        {
            using var f = new FakePayGo();
            var c = Cliente(f);
            checar(ultimaPaga is not null, "há uma venda confirmada para cancelar");
            if (ultimaPaga is not null)
            {
                var d = c.CancelarAsync(ultimaPaga, CancellationToken.None).GetAwaiter().GetResult();
                checar(d.Pago, "CNC aprovado: " + d.Motivo);
                var cnc = f.Comandos("CNC").Single();
                checar(cnc["012-000"] == ultimaPaga.Resposta!.Nsu && cnc["027-000"] == ultimaPaga.CodigoControle,
                       "CNC leva NSU (012) e código de controle (027) da venda original");
                checar(cnc["022-000"] == "31032025" && cnc["023-000"] == "120343" && cnc["003-000"] == "15000",
                       "CNC leva data/hora do comprovante e o valor original");
                checar(cnc["706-000"] == "156", "CNC declara as capacidades (128 = NSU 40 chars, obrigatório p/ Pix)");
                var cnf = f.Comandos("CNF").Single();
                checar(cnf["001-000"] == cnc["001-000"], "cancelamento também é two-phase: CNF do CNC");
            }
        }

        // ── religamento: pendências resolvidas sem perguntar ──────────────
        {
            using var f = new FakePayGo();
            var guardadas = new List<TransacaoPayGo>();
            var c = Cliente(f, t => { guardadas.Add(t); return true; });
            var req = new Dictionary<string, string> { ["003-000"] = "1000", ["731-000"] = "1" };
            TransacaoPayGo Tx(string id, string ctrl, string sit) => new("paygo-" + id, id, TipoTef.Credito, 1000, 1, sit,
                RespostaPayGo.Analisar(FakePayGo.Resposta("CRT", id, req, FakePayGo.Desfecho.Aprovar, ctrl)));

            var n = c.ResolverPendenciasAsync(new List<(TransacaoPayGo, bool)>
            {
                (Tx("1000000001", "CTRL-A", "aprovada"), true),      // venda gravada → CNF
                (Tx("1000000002", "CTRL-B", "aprovada"), false),     // sem venda → NCN
                (Tx("1000000003", "CTRL-C", "cnf_sem_ack"), true),   // CNF sem ack → reenvia
                (Tx("1000000004", "CTRL-D", "pago"), true),          // já resolvida → nada
            }).GetAwaiter().GetResult();

            checar(n == 3, $"resolveu as 3 pendentes ({n})");
            var cnfs = f.Comandos("CNF").Select(x => x["027-000"]).ToList();
            var ncns = f.Comandos("NCN").Select(x => x["027-000"]).ToList();
            checar(cnfs.Contains("CTRL-A") && cnfs.Contains("CTRL-C") && cnfs.Count == 2, "CNF para a venda gravada e para o CNF sem ack: " + string.Join(",", cnfs));
            checar(ncns.SequenceEqual(new[] { "CTRL-B" }), "NCN para a aprovada sem venda: " + string.Join(",", ncns));
            checar(guardadas.Any(g => g.Identificacao == "1000000001" && g.Situacao == "pago")
                && guardadas.Any(g => g.Identificacao == "1000000002" && g.Situacao == "desfeita")
                && guardadas.Any(g => g.Identificacao == "1000000003" && g.Situacao == "pago"),
                "estado final gravado: pago / desfeita / pago");
            checar(!guardadas.Any(g => g.Identificacao == "1000000004"), "a já resolvida não é tocada");
        }

        // ── duas cobranças ao mesmo tempo: a pasta é UMA, o cliente serializa ─
        {
            using var f = new FakePayGo { AtrasoRespostaMs = 80 };
            var c = Cliente(f);
            var t1 = c.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(10m), null, 1, null, CancellationToken.None);
            var t2 = c.CobrarAsync(TipoTef.Debito, Dinheiro.DeReais(20m), null, 1, null, CancellationToken.None);
            Task.WaitAll(t1, t2);
            checar(t1.Result.Pago && t2.Result.Pago, "as duas aprovam");
            checar(f.Quantos("CRT") == 2 && f.Quantos("CNF") == 2, "2 CRT + 2 CNF, sem um atropelar o outro");
            var ordem = f.Recebidos.Select(r => r["000-000"]).ToList();
            checar(ordem.SequenceEqual(new[] { "CRT", "CNF", "CRT", "CNF" }), "serializadas: CRT,CNF,CRT,CNF — " + string.Join(",", ordem));
            checar(t1.Result.PaymentIdentifier != t2.Result.PaymentIdentifier, "identificações distintas");
        }

        // ── pré-seleção de rede (roteiro P3 / P11) e 749 = 1 cartão ──────
        {
            using var f = new FakePayGo();
            var c = Cliente(f, opcoes: new OpcoesPayGo("Setis", "Teste", "v1", "G45J35G3JH45B435",
                RedeCartao: "C6PAY", RedePix: "PIX C6 BANK"));
            checar(Cobrar(c, TipoTef.Credito, 10m).Pago && Cobrar(c, TipoTef.Pix, 10m).Pago, "vendas com rede pré-selecionada aprovam");
            var crts = f.Comandos("CRT");
            checar(crts[0]["010-000"] == "C6PAY" && crts[0]["749-000"] == "1" && crts[0]["731-000"] == "1" && crts[0]["732-000"] == "1",
                   "P3: cartão pré-selecionado leva 010=C6PAY, 749=1, 731=1, 732=1");
            checar(crts[1]["010-000"] == "PIX C6 BANK" && crts[1]["749-000"] == "8", "P11: Pix pré-selecionado leva 010=PIX C6 BANK, 749=8");
        }
        {
            using var f = new FakePayGo();
            var c = Cliente(f);
            Cobrar(c, TipoTef.Debito, 10m);
            checar(!f.Comandos("CRT")[0].ContainsKey("010-000"), "sem pré-seleção o CRT não leva 010 (o PayGo mostra o menu de redes)");
        }

        // ── "transação pendente" (roteiro P31–34) ─────────────────────────
        {
            using var f = new FakePayGo();
            f.Roteiro.Enqueue(FakePayGo.Desfecho.NegarComPendencia);
            var sits = new List<string>();
            var c = Cliente(f, t => { sits.Add(t.Situacao); return true; }, conhecida: ctrl => ctrl == "CTRL-PENDENTE");
            var d = Cobrar(c, TipoTef.Credito, 1005.51m);
            checar(!d.Pago && d.Codigo == CodigoTef.Pendencia, "P32: pendente CONHECIDA → venda atual não realizada, código 'pendencia'");
            checar(f.Quantos("CNF") == 1 && f.Comandos("CNF")[0]["027-000"] == "CTRL-PENDENTE" && f.Quantos("NCN") == 0,
                   "P32: pendente conhecida é CONFIRMADA com o 027 recebido");
            var cnf = f.Comandos("CNF")[0];
            var crt = f.Comandos("CRT")[0];
            checar(cnf["001-000"] != crt["001-000"] && cnf["010-000"] == "DEMO",
                   "CNF da pendente vai com identificação NOVA e a rede (010) recebida");
            checar((d.Motivo ?? "").Contains("PENDENTE") && d.Motivo!.Contains("confirmada"), "mensagem = 030 + o que foi feito: " + d.Motivo);
            checar(sits.Last() == "recusado", "a venda atual fica como não realizada");
        }
        {
            using var f = new FakePayGo();
            f.Roteiro.Enqueue(FakePayGo.Desfecho.NegarComPendencia);
            var c = Cliente(f, conhecida: _ => false);
            var d = Cobrar(c, TipoTef.Credito, 1005.51m);
            checar(!d.Pago && d.Codigo == CodigoTef.Pendencia, "P34: pendente DESCONHECIDA → venda atual não realizada");
            checar(f.Quantos("NCN") == 1 && f.Comandos("NCN")[0]["027-000"] == "CTRL-PENDENTE" && f.Quantos("CNF") == 0,
                   "P34: pendente desconhecida é DESFEITA com o 027 recebido");
            checar((d.Motivo ?? "").Contains("desfeita"), "mensagem diz que desfez: " + d.Motivo);
        }
        {
            using var f = new FakePayGo();
            f.Roteiro.Enqueue(FakePayGo.Desfecho.NegarComPendencia);
            var c = Cliente(f, conhecida: _ => throw new InvalidOperationException("banco fora"));
            var d = Cobrar(c, TipoTef.Credito, 10m);
            checar(f.Quantos("NCN") == 1 && f.Quantos("CNF") == 0, "consulta quebrada nunca vira CNF por engano (= desconhecida = NCN)");
        }

        // ── resposta inconsistente (sem 009-000) ──────────────────────────
        {
            using var f = new FakePayGo();
            f.Roteiro.Enqueue(FakePayGo.Desfecho.Inconsistente);
            var c = Cliente(f);
            var d = Cobrar(c, TipoTef.Credito, 10m);
            checar(d.Situacao == SituacaoTef.Erro && !d.Pago, "sem 009 → erro, nunca pago");
            checar(d.Motivo == "Inconsistência no campo 009-000 do arquivo intpos.001 gerado pelo TEF", "mensagem padronizada da spec: " + d.Motivo);
            checar(d.PosPodeTerFicadoOcupado, "sem status o operador precisa conferir no PayGo (aviso ligado)");
            checar(f.Quantos("CNF") == 0 && f.Quantos("NCN") == 0, "sem status não confirma nem desfaz");
        }

        // ── queda ANTES de o PayGo pegar o comando (roteiro P24/25) ───────
        {
            using var f = new FakePayGo { Pausado = true };
            var c = Cliente(f);
            var req = Path.Combine(f.Req, "intpos.001");
            File.WriteAllText(req, "000-000 = CRT\r\n001-000 = 1\r\n003-000 = 100\r\n999-999 = 0\r\n");
            var n = c.ResolverPendenciasAsync(new List<(TransacaoPayGo, bool)>()).GetAwaiter().GetResult();
            checar(n == 0 && !File.Exists(req), "no boot, Req\\intpos.001 órfão é apagado (não reprocessado)");
            f.Pausado = false;
            Thread.Sleep(120);
            checar(f.Quantos("CRT") == 0, "o PayGo 'religado' não vê cobrança nenhuma");
        }

        // ── tabelas auxiliares ────────────────────────────────────────────
        {
            checar(ClientePayGo.TBand("MASTERCARD CREDITO") == "02" && ClientePayGo.TBand("ELO DEBITO") == "06"
                && ClientePayGo.TBand("HIPERCARD") == "07" && ClientePayGo.TBand("CARTAO XPTO") == "99" && ClientePayGo.TBand(null) == "99",
                "tBand da NFC-e a partir do nome do cartão");
            checar(ClientePayGo.CnpjConhecido("REDE") == "01425787000104" && ClientePayGo.CnpjConhecido("cielo") == "01027058000191"
                && ClientePayGo.CnpjConhecido("DEMO") is null,
                "CNPJ das credenciadoras conhecidas; desconhecida = null (tpIntegra=2)");
        }
    }
}
