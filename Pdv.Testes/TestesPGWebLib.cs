using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// TEF PayGo pela PGWebLib, contra o <see cref="FakePGWebLib"/>. O inegociável aqui é o mesmo
/// dos outros provedores: Guardar ANTES de PW_iConfirmation, REV em toda desistência depois da
/// aprovação, laços com teto (nunca travar), pendência resolvida no boot sem perguntar ao
/// operador, e PWINFO_CARDFULLPAN nunca lido.
/// </summary>
public static class TestesPGWebLib
{
    private static readonly OpcoesPGWebLib Opcoes = new("Pdv.AmericanDay", "0.5.9", "American Day", RedeCartao: "REDE", RedePix: "PIX ITAU");

    /// <summary>Diretório de trabalho da bateria: o provedor CRIA a pasta antes do PW_iInit, então tem que ser uma que possa existir sem sujar a máquina.</summary>
    public static readonly string PastaTeste = Path.Combine(Path.GetTempPath(), "pdv-pgweb-teste");

    private static ProvedorPGWebLib Provedor(FakePGWebLib f, Func<TransacaoPayGo, bool>? guardar = null,
        Func<TransacaoPayGo, Task<bool>>? imprimir = null, Func<PwGetData, CancellationToken, Task<string?>>? perguntar = null,
        OpcoesPGWebLib? opcoes = null, Func<string, bool>? conhecida = null, List<string>? auditoria = null,
        int tetoExecMs = 2000, int tetoCapturaMs = 2000)
        => new(f, PastaTeste, opcoes ?? Opcoes)
        {
            IntervaloPollMs = 5,
            TempoMaxExecMs = tetoExecMs,
            TempoMaxCapturaMs = tetoCapturaMs,
            TempoPerguntaMs = 500,
            Guardar = guardar ?? (_ => true),
            ImprimirComprovante = imprimir,
            Perguntar = perguntar,
            ConhecidaConfirmada = conhecida,
            Auditar = auditoria is null ? null : auditoria.Add,
        };

    private static DesfechoTef Cobrar(ProvedorPGWebLib p, TipoTef tipo, decimal reais, int parcelas = 1,
        CancellationToken ct = default, IProgress<AndamentoTef>? andamento = null)
        => p.CobrarAsync(tipo, Dinheiro.DeReais(reais), null, parcelas, andamento, ct).GetAwaiter().GetResult();

    private static string? P(FakePGWebLib f, ushort info) => f.Ultima?.Params.GetValueOrDefault(info);

    private sealed class Progresso : IProgress<AndamentoTef>
    {
        public List<AndamentoTef> Itens { get; } = new();
        public Action<AndamentoTef>? AoReceber;
        public void Report(AndamentoTef v) { Itens.Add(v); AoReceber?.Invoke(v); }
    }

    public static void Rodar(Action<bool, string> checar)
    {
        // ── constantes: os números da especificação ───────────────────────
        {
            checar(PW.PWRET_MOREDATA == -2497 && PW.PWRET_NOTHING == -2493 && PW.PWRET_DISPLAY == -2495 && PW.PWRET_INVALIDTRN == -2482, "PWRET_* com os números da spec");
            checar(PW.PWOPER_SALE == 33 && PW.PWOPER_SALEVOID == 34 && PW.PWOPER_ADMIN == 32 && PW.PWOPER_INSTALL == 1 && PW.PWOPER_REPRINT == 16, "PWOPER_*");
            checar(PW.PWINFO_CNFREQ == 67 && PW.PWINFO_REQNUM == 50 && PW.PWINFO_AUTEXTREF == 69 && PW.PWINFO_PAYMNTTYPE == 7969 && PW.PWINFO_PNDREQNUM == 32519, "PWINFO_*");
            checar(PW.PWINFO_DUEAMNT == 48902 && PW.PWINFO_CARDFULLPAN == 193 && PW.PWINFO_USINGPINPAD == 32513, "PWINFO_DUEAMNT=0xBF06, CARDFULLPAN=193");
            checar(PW.PWCNF_CNF_AUTO == 289 && PW.PWCNF_REV_ABORT == 274737 && PW.PWCNF_REV_PWR_AUT == 536881 && PW.PWCNF_REV_PRN_AUT == 78129, "PWCNF_*");
            checar(PW.PWDAT_MENU == 1 && PW.PWDAT_PPENCPIN == 6 && PW.PWDAT_PPREMCRD == 13 && PW.PWDAT_USERAUTH == 17, "PWDAT_*");
            checar(PW.EhRecusaDoHost(-2596) && PW.EhRecusaDoHost(-2599) && !PW.EhRecusaDoHost(-2595) && !PW.EhRecusaDoHost(-2600), "faixa PWRET_FROMHOST* -2599..-2596");
            checar(PW.Nome(PW.PWRET_CANCEL) == "PWRET_CANCEL (-2491)", "nome legível para auditoria: " + PW.Nome(PW.PWRET_CANCEL));
        }

        // ── venda aprovada: Guardar ANTES de Confirmation; NSU/aut/rede/vias ─
        {
            var f = new FakePGWebLib();
            var guardadas = new List<TransacaoPayGo>();
            var confirmadasAoGravarAprovada = -1;
            var aud = new List<string>();
            var perguntou = 0;
            var p = Provedor(f, t =>
            {
                guardadas.Add(t);
                if (t.Situacao == "aprovada") confirmadasAoGravarAprovada = f.Confirmadas.Count;
                return true;
            }, perguntar: (_, _) => { perguntou++; return Task.FromResult<string?>(null); }, auditoria: aud);
            var prog = new Progresso();
            var d = Cobrar(p, TipoTef.Credito, 15.00m, andamento: prog);
            checar(d.Pago && d.Codigo == CodigoTef.Pago && d.PaymentStatus == "pago", "venda aprovada: Pago/pago (" + d.Motivo + ")");
            checar(guardadas.Select(g => g.Situacao).SequenceEqual(new[] { "aguardando", "aprovada", "pago" }), "guardadas na ordem aguardando -> aprovada -> pago: " + string.Join(",", guardadas.Select(g => g.Situacao)));
            checar(confirmadasAoGravarAprovada == 0, "a linha 'aprovada' foi GRAVADA antes de PW_iConfirmation (memória não volátil primeiro)");
            checar(f.Confirmadas.Count == 1 && f.Confirmadas[0].Resultado == PW.PWCNF_CNF_AUTO && f.Confirmadas[0].ReqNum == f.UltimoReqNum, "PW_iConfirmation(PWCNF_CNF_AUTO) com o REQNUM da venda");
            checar(f.Chamadas.IndexOf("WaitConfirmation") > f.Chamadas.FindIndex(c => c.StartsWith("Confirmation(")), "PW_iWaitConfirmation depois da confirmação");
            checar(f.Pendente is null, "a biblioteca não segura mais nada");
            checar(d.Cartao is { } c1 && c1.Nsu == f.UltimoNsu && c1.CAut == "A" + f.UltimoReqNum && c1.Adquirente == "REDE" && c1.Cnpj == ClientePayGo.CnpjConhecido("REDE") && c1.TBand == "01" && c1.Bandeira == "VISA",
                $"cartão: NSU=AUTEXTREF, cAut=AUTHCODE, rede=AUTHSYST, CNPJ da REDE, tBand VISA ({d.Cartao})");
            checar(d.Cartao?.Valor == 15.00m, "valor confirmado pela biblioteca (TOTAMNT) chega no cartão");
            var tx = guardadas.First(g => g.Situacao == "aprovada");
            var r = tx.Resposta!;
            checar(r.Aprovada && r.RequerConfirmacao && r.CodigoControle == f.UltimoReqNum && r.Nsu == f.UltimoNsu && r.Rede == "REDE", "resposta intpos: 009=0, 729=2 (CNFREQ=1), 027=REQNUM, 012=NSU, 010=rede");
            checar(r.ViaCliente.Count == 5 && r.ViaEstabelecimento.Count == 6 && r.Vias == 3, $"vias 713/715 a partir de RCPTCHOLDER/RCPTMERCH (0Dh) e 737=RCPTPRN ({r.ViaCliente.Count}/{r.ViaEstabelecimento.Count}/{r.Vias})");
            checar(r.Data == "05092026" && r.Hora == "143000", "AUTDATETIME vira 022 DDMMYYYY / 023 hhmmss: " + r.Data + " " + r.Hora);
            checar(r.CartaoMascarado == "489391******0008" && !r.Texto.Contains("4893910000000008"), "PAN só mascarado (740); o completo não existe em lugar nenhum");
            checar(!f.Lidos.Contains(PW.PWINFO_CARDFULLPAN), "PWINFO_CARDFULLPAN (193) nunca foi lido");
            checar(P(f, PW.PWINFO_TOTAMNT) == "1500" && P(f, PW.PWINFO_CURRENCY) == "986" && P(f, PW.PWINFO_CURREXP) == "2", "TOTAMNT em centavos, CURRENCY 986, CURREXP 2");
            checar(P(f, PW.PWINFO_PAYMNTTYPE) == "1" && P(f, PW.PWINFO_CARDTYPE) == "1" && P(f, PW.PWINFO_FINTYPE) == "1" && P(f, PW.PWINFO_INSTALLMENTS) is null, "crédito à vista: PAYMNTTYPE 1, CARDTYPE 1, FINTYPE 1, sem INSTALLMENTS");
            checar(P(f, PW.PWINFO_AUTNAME) == "Pdv.AmericanDay" && P(f, PW.PWINFO_AUTVER) == "0.5.9" && P(f, PW.PWINFO_AUTDEV) == "American Day" && P(f, PW.PWINFO_AUTCAP) == "28", "identidade AUTNAME/AUTVER/AUTDEV e AUTCAP=4+8+16");
            checar(P(f, PW.PWINFO_USINGPINPAD) == "1" && P(f, PW.PWINFO_PPCOMMPORT) == "0" && P(f, PW.PWINFO_AUTHSYST) == "REDE", "USINGPINPAD=1, PPCOMMPORT=0, rede pré-selecionada em AUTHSYST");
            checar(perguntou == 0, "com rede pré-selecionada a tela não é perguntada");
            var ordem = f.Chamadas;
            checar(ordem.IndexOf("PPGetCard(0)") > 0 && ordem.IndexOf("PPGetPIN(0)") > ordem.IndexOf("PPGetCard(0)") && ordem.IndexOf("PPRemoveCard(0)") > ordem.IndexOf("PPGetPIN(0)"),
                "PWDAT_CARDINF -> PPGetCard, PPENCPIN -> PPGetPIN, PPREMCRD -> PPRemoveCard, nesta ordem");
            checar(prog.Itens.Any(i => i.Fase == FaseTef.Recado && i.Mensagem == "APROXIME O CARTAO") && prog.Itens.Any(i => i.Mensagem == "DIGITE A SENHA") && prog.Itens.Any(i => i.Mensagem == "RETIRE O CARTAO"),
                "PWRET_DISPLAY vira Recado para a tela (APROXIME/SENHA/RETIRE)");
            checar(prog.Itens[0].Fase == FaseTef.Criando && prog.Itens[1].Fase == FaseTef.Aguardando && prog.Itens[1].PaymentIdentifier == tx.Identificacao, "Criando -> Aguardando com a identificação");
            checar(d.ChargeId!.StartsWith("pgweb-") && tx.ChargeId == d.ChargeId, "charge_id com prefixo pgweb-");
            checar(aud.Any(a => a.Contains("CNF") && a.Contains(f.UltimoReqNum!)), "auditoria registra o CNF com o REQNUM");
        }

        // ── menu de rede: responde do que já sabe; sem saber, pergunta à tela ─
        {
            var f = new FakePGWebLib { SempreMenuRede = true };
            var perguntas = new List<PwGetData>();
            var p = Provedor(f, perguntar: (g, _) => { perguntas.Add(g); return Task.FromResult<string?>("CIELO"); });
            var d = Cobrar(p, TipoTef.Credito, 10m);
            checar(d.Pago && perguntas.Count == 0 && P(f, PW.PWINFO_AUTHSYST) == "REDE", "menu AUTHSYST com rede pré-selecionada: respondido sem perguntar");

            var f2 = new FakePGWebLib();
            var p2 = Provedor(f2, perguntar: (g, _) => { perguntas.Add(g); return Task.FromResult<string?>("cielo"); },
                opcoes: Opcoes with { RedeCartao = null });
            var d2 = Cobrar(p2, TipoTef.Credito, 10m);
            checar(d2.Pago && perguntas.Count == 1 && perguntas[0].EhMenu && perguntas[0].Identificador == PW.PWINFO_AUTHSYST && perguntas[0].Opcoes!.Count == 3,
                "sem rede pré-selecionada: a tela recebe o PWDAT_MENU com as 3 opções");
            checar(P(f2, PW.PWINFO_AUTHSYST) == "CIELO" && d2.Cartao?.Adquirente == "CIELO", "resposta da tela vai em PW_iAddParam(AUTHSYST) com o VALOR da opção (case-insensitive)");

            var f3 = new FakePGWebLib();
            var guardadas = new List<TransacaoPayGo>();
            var p3 = Provedor(f3, t => { guardadas.Add(t); return true; }, perguntar: (_, _) => Task.FromResult<string?>(null), opcoes: Opcoes with { RedeCartao = null });
            var d3 = Cobrar(p3, TipoTef.Credito, 10m);
            checar(!d3.Pago && d3.Situacao == SituacaoTef.Cancelado && guardadas[^1].Situacao == "cancelado" && f3.Confirmadas.Count == 0,
                "tela cancela o menu: cancelado, nada aprovado, nada confirmado");

            var f4 = new FakePGWebLib { SempreMenuRede = true };
            var p4 = Provedor(f4, opcoes: Opcoes with { RedeCartao = "STONE" }, perguntar: (g, _) => Task.FromResult<string?>("REDE"));
            var d4 = Cobrar(p4, TipoTef.Credito, 10m);
            checar(d4.Pago && P(f4, PW.PWINFO_AUTHSYST) == "REDE", "rede configurada que NÃO está no menu: cai na pergunta em vez de mandar valor inválido");

            var f5 = new FakePGWebLib();
            var p5 = Provedor(f5, opcoes: Opcoes with { RedeCartao = null }, perguntar: (_, ct) => Task.Delay(5000, ct).ContinueWith(_ => (string?)"REDE"));
            var d5 = Cobrar(p5, TipoTef.Credito, 10m);
            checar(!d5.Pago && d5.Situacao == SituacaoTef.Cancelado, "tela que não responde: TempoPerguntaMs encerra (nunca trava)");
        }

        // ── o mesmo menu pedido duas vezes (passos 30 e 31 do roteiro) ────
        {
            var f = new FakePGWebLib { MenuGenericoVezes = 2 };
            var perguntas = new List<PwGetData>();
            var p = Provedor(f, perguntar: (g, _) => { perguntas.Add(g); return Task.FromResult<string?>("ABCDEF"); });
            var d = Cobrar(p, TipoTef.Credito, 1002m);
            var genericos = perguntas.Where(g => g.Identificador == FakePGWebLib.IdMenuGenerico).ToList();
            checar(genericos.Count == 2, $"a biblioteca pede a tag 0x2F duas vezes e a tela recebe as duas (recebeu {genericos.Count})");
            checar(genericos.Count == 2 && genericos[1].Prompt == "SELECIONAR:" && genericos[1].Opcoes!.Count == 2
                && genericos[1].Opcoes![0].Texto == "123456" && genericos[1].Opcoes![1].Texto == "ABCDEF",
                "o segundo menu chega igual ao primeiro: prompt SELECIONAR: e as opções 123456 e ABCDEF");
            checar(d.Pago && P(f, FakePGWebLib.IdMenuGenerico) == "ABCDEF", "depois da segunda escolha a venda aprova, com ABCDEF na tag 0x2F");
            checar(f.Chamadas.IndexOf($"AddParam({FakePGWebLib.IdMenuGenerico}=ABCDEF)") > f.Chamadas.IndexOf("ExecTransac") && d.Situacao != SituacaoTef.Recusado,
                "a tag 0x2F só vai para a biblioteca depois de pedida (adiantar reprova o passo 30)");

            // Desistir no segundo menu cancela a venda: a resposta do primeiro não responde pelo operador.
            var f2 = new FakePGWebLib { MenuGenericoVezes = 2 };
            var vezes = 0;
            var p2 = Provedor(f2, perguntar: (_, _) => Task.FromResult(++vezes == 1 ? "123456" : null));
            var d2 = Cobrar(p2, TipoTef.Credito, 1002m);
            checar(!d2.Pago && d2.Situacao == SituacaoTef.Cancelado && vezes == 2 && f2.Confirmadas.Count == 0,
                "desistir no segundo menu cancela a venda, e a tela foi perguntada as duas vezes");
        }

        // ── débito, voucher e Pix ─────────────────────────────────────────
        {
            var f = new FakePGWebLib();
            var p = Provedor(f);
            var d = Cobrar(p, TipoTef.Debito, 10m);
            checar(d.Pago && P(f, PW.PWINFO_CARDTYPE) == "2" && P(f, PW.PWINFO_PAYMNTTYPE) == "1", "débito: CARDTYPE 2");
            d = Cobrar(p, TipoTef.Voucher, 10m);
            checar(d.Pago && P(f, PW.PWINFO_CARDTYPE) == "4", "voucher: CARDTYPE 4");
            d = Cobrar(p, TipoTef.Pix, 10m);
            checar(d.Pago && P(f, PW.PWINFO_PAYMNTTYPE) == "8" && P(f, PW.PWINFO_CARDTYPE) is null && P(f, PW.PWINFO_AUTHSYST) == "PIX ITAU",
                "Pix: PAYMNTTYPE 8 (carteira digital), sem CARDTYPE, rede Pix pré-selecionada");
            var ipTx = d;
            checar(ipTx.Cartao?.Adquirente == "PIX ITAU", "Pix devolve a rede no cartão para a conciliação");
        }

        // ── parcelado ────────────────────────────────────────────────────
        {
            var f = new FakePGWebLib();
            var guardadas = new List<TransacaoPayGo>();
            var p = Provedor(f, t => { guardadas.Add(t); return true; });
            var d = Cobrar(p, TipoTef.Credito, 300m, parcelas: 3);
            checar(d.Pago && P(f, PW.PWINFO_FINTYPE) == "4" && P(f, PW.PWINFO_INSTALLMENTS) == "3", "crédito 3x: FINTYPE 4 (parcelado loja) + INSTALLMENTS 3");
            checar(guardadas[^1].Parcelas == 3 && guardadas[^1].Resposta!.Parcelas == 3 && d.Cartao?.Parcelas == 3, "parcelas gravadas na linha, na resposta (018) e no cartão");
            d = Cobrar(p, TipoTef.Debito, 300m, parcelas: 3);
            checar(d.Pago && P(f, PW.PWINFO_INSTALLMENTS) is null && P(f, PW.PWINFO_FINTYPE) == "1", "débito ignora parcelas");
        }

        // ── recusa pelo host ─────────────────────────────────────────────
        {
            var f = new FakePGWebLib();
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Recusar);
            var guardadas = new List<TransacaoPayGo>();
            var p = Provedor(f, t => { guardadas.Add(t); return true; });
            var d = Cobrar(p, TipoTef.Credito, 10m);
            checar(!d.Pago && d.Situacao == SituacaoTef.Recusado && d.Codigo == CodigoTef.Recusado && d.Motivo == "TRANSACAO NAO AUTORIZADA", "PWRET_FROMHOST: Recusado com RESULTMSG (" + d.Motivo + ")");
            checar(guardadas[^1].Situacao == "recusado" && f.Confirmadas.Count == 0 && !d.Desfeita && !d.PosPodeTerFicadoOcupado, "gravada 'recusado', sem confirmação, sem aviso de POS ocupado");
            checar(guardadas[^1].Resposta is { Aprovada: false } rr && rr.Mensagem == "TRANSACAO NAO AUTORIZADA", "resposta negada guarda a 030 para a tela");
        }

        // ── cancelamento pelo pinpad (senha) ─────────────────────────────
        {
            var f = new FakePGWebLib();
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.CancelarNoPinpad);
            var guardadas = new List<TransacaoPayGo>();
            var p = Provedor(f, t => { guardadas.Add(t); return true; });
            var d = Cobrar(p, TipoTef.Credito, 10m);
            checar(!d.Pago && d.Situacao == SituacaoTef.Cancelado && d.Codigo == CodigoTef.Cancelado, "PWRET_CANCEL no PPEventLoop: Cancelado (" + d.Motivo + ")");
            checar(guardadas[^1].Situacao == "cancelado" && f.Confirmadas.Count == 0 && !d.Desfeita, "gravada 'cancelado'; nada a desfazer (não chegou ao host)");
        }

        // ── timeout no pinpad ────────────────────────────────────────────
        {
            var f = new FakePGWebLib();
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Timeout);
            var guardadas = new List<TransacaoPayGo>();
            var p = Provedor(f, t => { guardadas.Add(t); return true; });
            var d = Cobrar(p, TipoTef.Credito, 10m);
            checar(!d.Pago && d.Situacao == SituacaoTef.Timeout && d.Codigo == CodigoTef.Timeout, "PWRET_TIMEOUT no PPEventLoop: Timeout (" + d.Motivo + ")");
            checar(guardadas[^1].Situacao == "cancelado" && f.Confirmadas.Count == 0, "timeout antes do host: gravada 'cancelado', nada confirmado");
        }

        // ── host fora ────────────────────────────────────────────────────
        {
            var f = new FakePGWebLib();
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.HostFora);
            var guardadas = new List<TransacaoPayGo>();
            var p = Provedor(f, t => { guardadas.Add(t); return true; });
            var d = Cobrar(p, TipoTef.Credito, 10m);
            checar(!d.Pago && d.Situacao == SituacaoTef.Erro && d.Codigo == CodigoTef.SemRede && d.Motivo == "SEM COMUNICACAO COM O HOST", "PWRET_HOSTCONNERR: Erro/sem_rede com RESULTMSG (" + d.Motivo + ")");
            checar(guardadas[^1].Situacao == "erro" && f.Confirmadas.Count == 0, "gravada 'erro', sem confirmação");
        }

        // ── biblioteca que nunca termina: teto + PPAbort, nunca trava ────
        {
            var f = new FakePGWebLib();
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.NuncaTermina);
            var guardadas = new List<TransacaoPayGo>();
            var p = Provedor(f, t => { guardadas.Add(t); return true; }, tetoExecMs: 200);
            var relogio = System.Diagnostics.Stopwatch.StartNew();
            var d = Cobrar(p, TipoTef.Credito, 10m);
            checar(relogio.ElapsedMilliseconds < 1500 && d.Situacao == SituacaoTef.Timeout && d.PosPodeTerFicadoOcupado, $"PWRET_NOTHING sem fim: TempoMaxExecMs encerra em {relogio.ElapsedMilliseconds} ms com aviso de POS");
            checar(f.Abortos >= 1 && guardadas[^1].Situacao == "orfa", "PW_iPPAbort chamado e linha gravada como órfã (conferir no PayGo)");
            checar(!p.Ocupado, "semáforo liberado depois do teto");
        }

        // ── operador cancela DEPOIS da aprovação (durante RETIRE O CARTAO): REV_ABORT ─
        {
            var f = new FakePGWebLib();
            var guardadas = new List<TransacaoPayGo>();
            using var cts = new CancellationTokenSource();
            var p = Provedor(f, t => { guardadas.Add(t); return true; });
            var prog = new Progresso { AoReceber = a => { if (a.Mensagem == "RETIRE O CARTAO") cts.Cancel(); } };
            var d = Cobrar(p, TipoTef.Credito, 10m, ct: cts.Token, andamento: prog);
            checar(!d.Pago && d.Situacao == SituacaoTef.Cancelado && d.Desfeita, "cancelado no meio de PWRET_MOREDATA com CNFREQ=1: Cancelado + Desfeita (" + d.Motivo + ")");
            checar(f.Confirmadas.Count == 1 && f.Confirmadas[0].Resultado == PW.PWCNF_REV_ABORT && f.Confirmadas[0].ReqNum == f.UltimoReqNum, "PW_iConfirmation(PWCNF_REV_ABORT) com o REQNUM aprovado");
            checar(guardadas.Select(g => g.Situacao).SequenceEqual(new[] { "aguardando", "aprovada", "desfeita" }), "gravada aprovada -> desfeita (nunca 'pago'): " + string.Join(",", guardadas.Select(g => g.Situacao)));
            checar(f.Pendente is null, "a biblioteca não ficou com pendência");
        }

        // ── não gravou: REV_OTHER_AUT ────────────────────────────────────
        {
            var f = new FakePGWebLib();
            var guardadas = new List<TransacaoPayGo>();
            var p = Provedor(f, t => { guardadas.Add(t); return t.Situacao != "aprovada"; });
            var d = Cobrar(p, TipoTef.Credito, 10m);
            checar(!d.Pago && d.Situacao == SituacaoTef.Erro && d.Desfeita && d.Codigo == CodigoTef.Plataforma, "Guardar false na 'aprovada': desfeita, sem cobrar o cliente");
            checar(f.Confirmadas.Count == 1 && f.Confirmadas[0].Resultado == PW.PWCNF_REV_OTHER_AUT, "PWCNF_REV_OTHER_AUT");
            checar(!guardadas.Any(g => g.Situacao == "pago"), "nunca 'pago'");

            var f2 = new FakePGWebLib();
            var p2 = Provedor(f2, t => t.Situacao == "aprovada" ? throw new IOException("disco cheio") : true);
            var d2 = Cobrar(p2, TipoTef.Credito, 10m);
            checar(!d2.Pago && d2.Desfeita && f2.Confirmadas[0].Resultado == PW.PWCNF_REV_OTHER_AUT, "Guardar lança: mesmo destino (REV)");
        }

        // ── comprovante não saiu: REV_PRN_AUT e a mensagem literal ────────
        {
            var f = new FakePGWebLib();
            var guardadas = new List<TransacaoPayGo>();
            var p = Provedor(f, t => { guardadas.Add(t); return true; }, imprimir: _ => Task.FromResult(false));
            var d = Cobrar(p, TipoTef.Credito, 12.50m);
            checar(!d.Pago && d.Situacao == SituacaoTef.Cancelado && d.Desfeita, "impressão falhou e o operador desistiu: Cancelado + Desfeita");
            checar(f.Confirmadas.Count == 1 && f.Confirmadas[0].Resultado == PW.PWCNF_REV_PRN_AUT, "PWCNF_REV_PRN_AUT");
            checar(d.Motivo == ClientePayGo.MsgCancelada("REDE", f.UltimoNsu, 1250), "mensagem literal da spec: " + d.Motivo);
            checar(guardadas[^1].Situacao == "desfeita", "gravada 'desfeita'");

            var impressas = new List<string>();
            var f2 = new FakePGWebLib();
            var p2 = Provedor(f2, imprimir: t => { impressas.Add(t.Situacao + ":" + f2.Confirmadas.Count); return Task.FromResult(true); });
            var d2 = Cobrar(p2, TipoTef.Credito, 12.50m);
            checar(d2.Pago && impressas.Count == 1 && impressas[0] == "aprovada:0", "comprovante impresso ANTES da confirmação (spec: a impressão decide o commit)");

            var f3 = new FakePGWebLib { Cnfreq = false };
            var p3 = Provedor(f3, imprimir: _ => Task.FromResult(false));
            var d3 = Cobrar(p3, TipoTef.Credito, 12.50m);
            checar(d3.Pago && d3.PaymentStatus == "pago" && f3.Confirmadas.Count == 0 && f3.Chamadas.All(c => !c.StartsWith("Confirmation(")), "CNFREQ=0: paga sem PW_iConfirmation; impressão em melhor-esforço");
        }

        // ── confirmação sem ack: cnf_sem_ack, reenviada antes do próximo comando ─
        {
            var f = new FakePGWebLib { FalharConfirmacao = true };
            var guardadas = new List<TransacaoPayGo>();
            var p = Provedor(f, t => { guardadas.Add(t); return true; });
            var d = Cobrar(p, TipoTef.Credito, 10m);
            var req1 = f.UltimoReqNum;
            checar(d.Pago && d.PaymentStatus == "cnf_sem_ack" && guardadas[^1].Situacao == "cnf_sem_ack", "PWRET_WRITERR na confirmação: paga para o caixa, gravada cnf_sem_ack");
            checar(f.Pendente is not null && f.Pendente.ReqNum == req1, "a biblioteca ainda segura a pendência");
            var d2 = Cobrar(p, TipoTef.Credito, 20m);
            checar(d2.Pago && d2.PaymentStatus == "pago", "venda seguinte sai normal (a pendência foi resolvida antes dela): " + d2.Motivo);
            checar(f.Confirmadas.Count == 2 && f.Confirmadas[0] == (PW.PWCNF_CNF_AUTO, req1!), "o reenvio do CNF da 1ª veio ANTES da 2ª venda");
            checar(guardadas.Any(g => g.ChargeId == d.ChargeId && g.Situacao == "pago"), "a 1ª linha virou 'pago' quando o reenvio foi acusado");
        }

        // ── queda entre aprovar e confirmar: o religamento resolve pela pendência ─
        {
            var f = new FakePGWebLib { ConfirmacaoLanca = true };
            var guardadas = new List<TransacaoPayGo>();
            var p = Provedor(f, t => { guardadas.Add(t); return true; });
            var d = Cobrar(p, TipoTef.Credito, 10m);
            var req = f.UltimoReqNum!;
            checar(d.Pago && d.PaymentStatus == "cnf_sem_ack", "PW_iConfirmation lançou (processo caiu): linha cnf_sem_ack com a resposta inteira");
            var linha = guardadas.Last(g => g.Situacao == "cnf_sem_ack");
            checar(linha.Resposta!.CodigoControle == req && linha.Resposta.Campos.ContainsKey("950-000") && linha.Resposta.Campos.ContainsKey("951-000"),
                "REQNUM, AUTLOCREF (950) e VIRTMERCH (951) estão na linha gravada: dá para confirmar depois");

            // religamento: biblioteca nova, com a pendência descrita nos PWINFO_PND*
            var f2 = new FakePGWebLib { PendenciaNoInit = new FakePGWebLib.Pendencia(req, "LOC" + req, f.UltimoNsu!, "VM1", "REDE") };
            var guardadas2 = new List<TransacaoPayGo>();
            var p2 = Provedor(f2, t => { guardadas2.Add(t); return true; });
            var n = p2.ResolverPendenciasAsync(new[] { (linha, true) }).GetAwaiter().GetResult();
            checar(n >= 1 && f2.Confirmadas.Any(c => c == (PW.PWCNF_CNF_AUTO, req)), "boot: venda conhecida como paga -> PWCNF_CNF_AUTO com o REQNUM");
            checar(f2.Pendente is null && guardadas2.Any(g => g.ChargeId == linha.ChargeId && g.Situacao == "pago"), "pendência resolvida e linha 'pago'");
            checar(!f2.Confirmadas.Any(c => c.Resultado != PW.PWCNF_CNF_AUTO), "sem REV (a venda existe)");

            // pendência que o caixa NÃO conhece: REV_PWR_AUT
            var f3 = new FakePGWebLib { PendenciaNoInit = new FakePGWebLib.Pendencia("999", "LOC999", "700999", "VM1", "REDE") };
            var p3 = Provedor(f3);
            n = p3.ResolverPendenciasAsync(Array.Empty<(TransacaoPayGo, bool)>()).GetAwaiter().GetResult();
            checar(n == 1 && f3.Confirmadas.Count == 1 && f3.Confirmadas[0] == (PW.PWCNF_REV_PWR_AUT, "999") && f3.Pendente is null, "boot: pendência desconhecida -> PWCNF_REV_PWR_AUT, sem perguntar ao operador");

            // 'aprovada' sem venda concluída: REV
            var f4 = new FakePGWebLib { PendenciaNoInit = new FakePGWebLib.Pendencia(req, "LOC" + req, f.UltimoNsu!, "VM1", "REDE") };
            var guardadas4 = new List<TransacaoPayGo>();
            var p4 = Provedor(f4, t => { guardadas4.Add(t); return true; });
            var aprovada = linha with { Situacao = "aprovada" };
            n = p4.ResolverPendenciasAsync(new[] { (aprovada, false) }).GetAwaiter().GetResult();
            checar(f4.Confirmadas.Count == 1 && f4.Confirmadas[0].Resultado == PW.PWCNF_REV_PWR_AUT && guardadas4.Any(g => g.Situacao == "desfeita"), "boot: 'aprovada' sem venda -> REV_PWR_AUT e 'desfeita'");

            // ConhecidaConfirmada (tef_transacao sabe) resolve a pendência da biblioteca antes de vender
            var f5 = new FakePGWebLib { PendenciaNoInit = new FakePGWebLib.Pendencia("555", "LOC555", "700555", "VM1", "REDE") };
            var p5 = Provedor(f5, conhecida: r => r == "555");
            var d5 = Cobrar(p5, TipoTef.Credito, 10m);
            checar(d5.Pago && f5.Confirmadas.Count == 2 && f5.Confirmadas[0] == (PW.PWCNF_CNF_AUTO, "555"), "pendência conhecida como paga é confirmada ANTES da venda nova (que sai normal)");

            // sem 027 não dá para confirmar às cegas
            var f6 = new FakePGWebLib();
            var guardadas6 = new List<TransacaoPayGo>();
            var p6 = Provedor(f6, t => { guardadas6.Add(t); return true; });
            var semReq = new TransacaoPayGo("pgweb-1", "1", TipoTef.Credito, 1000, 1, "aprovada", RespostaPayGo.Analisar("000-000 = CRT\r\n009-000 = 0\r\n999-999 = 0\r\n"));
            p6.ResolverPendenciasAsync(new[] { (semReq, true) }).GetAwaiter().GetResult();
            checar(f6.Confirmadas.Count == 0 && guardadas6.Any(g => g.Situacao == "orfa"), "linha sem REQNUM vira órfã (nunca CNF às cegas)");
        }

        // ── CNFREQ=0 (aprovada e já definitiva): nada aqui pode dizer "desfeita" ─
        {
            // Guardar falhou na 'aprovada': não existe REV para uma transação sem confirmação. A
            // tela não pode mandar cobrar de novo: o cliente JÁ pagou.
            var f = new FakePGWebLib { Cnfreq = false };
            var guardadas = new List<TransacaoPayGo>();
            var p = Provedor(f, t => { guardadas.Add(t); return t.Situacao != "aprovada"; });
            var d = Cobrar(p, TipoTef.Credito, 10m);
            checar(!d.Pago && d.Situacao == SituacaoTef.Erro && !d.Desfeita && d.PosPodeTerFicadoOcupado, "CNFREQ=0 + Guardar false: Erro SEM Desfeita e com aviso de conferir (" + d.Motivo + ")");
            checar(d.Motivo is { } m0 && m0.Contains("Não cobre de novo") && !m0.Contains("desfeita") && m0.Contains(f.UltimoNsu!), "mensagem: aprovada (NSU) mas não gravada; não cobre de novo");
            checar(f.Confirmadas.Count == 0 && f.Chamadas.All(c => !c.StartsWith("Confirmation(")), "nenhum PW_iConfirmation (não há o que desfazer)");
            checar(guardadas.Select(g => g.Situacao).SequenceEqual(new[] { "aguardando", "aprovada", "orfa" }) && guardadas[^1].Resposta?.Nsu == f.UltimoNsu,
                "tentou gravar 'orfa' com a resposta inteira (nunca 'desfeita'): " + string.Join(",", guardadas.Select(g => g.Situacao)));

            // Operador cancela em RETIRE O CARTAO depois que o host aprovou com CNFREQ=0: a
            // transação é definitiva; a saída do operador não muda o dinheiro. Paga.
            var f2 = new FakePGWebLib { Cnfreq = false };
            var guardadas2 = new List<TransacaoPayGo>();
            using var cts = new CancellationTokenSource();
            var aud2 = new List<string>();
            var p2 = Provedor(f2, t => { guardadas2.Add(t); return true; }, auditoria: aud2);
            var prog = new Progresso { AoReceber = a => { if (a.Mensagem == "RETIRE O CARTAO") cts.Cancel(); } };
            var d2 = Cobrar(p2, TipoTef.Credito, 10m, ct: cts.Token, andamento: prog);
            checar(d2.Pago && d2.PaymentStatus == "pago" && !d2.Desfeita && d2.Cartao?.Nsu == f2.UltimoNsu && d2.Cartao?.CAut == "A" + f2.UltimoReqNum,
                "cancelar em PPREMCRD com CNFREQ=0: Pago com o cartão (jamais 'cancelado'): " + d2.Situacao + " " + d2.Motivo);
            checar(guardadas2.Select(g => g.Situacao).SequenceEqual(new[] { "aguardando", "aprovada", "pago" }), "gravada aguardando -> aprovada -> pago: " + string.Join(",", guardadas2.Select(g => g.Situacao)));
            checar(f2.Confirmadas.Count == 0 && f2.Abortos >= 1, "PPAbort saiu, mas sem PW_iConfirmation (nada a desfazer)");
            checar(aud2.Any(a => a.Contains("definitiva")), "auditoria explica que a saída depois da aprovação definitiva foi ignorada");

            // Cancelamento (SALEVOID) aprovado com CNFREQ=0 e Guardar falhou: idem, nunca "desfeito".
            var f3 = new FakePGWebLib { Cnfreq = false };
            var guardadas3 = new List<TransacaoPayGo>();
            var p3 = Provedor(f3, t => { guardadas3.Add(t); return t.Situacao != "aprovada"; }, perguntar: (_, _) => Task.FromResult<string?>("1234"));
            var original = new TransacaoPayGo("pgweb-x", "x", TipoTef.Credito, 1000, 1, "pago",
                RespostaPayGo.Analisar("000-000 = CRT\r\n009-000 = 0\r\n012-000 = 700001\r\n022-000 = 05092026\r\n023-000 = 143000\r\n999-999 = 0\r\n"));
            var e3 = p3.CancelarAsync(original, CancellationToken.None).GetAwaiter().GetResult();
            checar(!e3.Pago && e3.Situacao == SituacaoTef.Erro && !e3.Desfeita && e3.PosPodeTerFicadoOcupado && f3.Confirmadas.Count == 0, "SALEVOID CNFREQ=0 + Guardar false: Erro sem Desfeita, sem REV (" + e3.Motivo + ")");
            checar(guardadas3.Select(g => g.Situacao).SequenceEqual(new[] { "aprovada", "orfa" }) && !guardadas3.Any(g => g.Situacao == "estornada"), "linha 'orfa' e a original NÃO vira 'estornada': " + string.Join(",", guardadas3.Select(g => g.Situacao)));
        }

        // ── PWRET_INVALIDTRN não é sucesso: a biblioteca não reconhece; vira 'orfa' ─
        {
            // (a) 'aprovada' de uma venda anterior; biblioteca nova sem pendência: REV devolve
            // INVALIDTRN. Não dá para dizer 'desfeita' (pode estar confirmada e cobrada).
            var f0 = new FakePGWebLib();
            var g0 = new List<TransacaoPayGo>();
            var p0 = Provedor(f0, t => { g0.Add(t); return true; });
            Cobrar(p0, TipoTef.Credito, 10m);
            var aprovada = g0.First(g => g.Situacao == "aprovada");

            var f = new FakePGWebLib();
            var guardadas = new List<TransacaoPayGo>();
            var aud = new List<string>();
            var p = Provedor(f, t => { guardadas.Add(t); return true; }, auditoria: aud);
            var n = p.ResolverPendenciasAsync(new[] { (aprovada, false) }).GetAwaiter().GetResult();
            checar(n == 1 && f.Confirmadas.Count == 0 && guardadas.Count == 1 && guardadas[0].Situacao == "orfa", "boot: REV com INVALIDTRN -> 'orfa' (nunca 'desfeita'): " + string.Join(",", guardadas.Select(g => g.Situacao)));
            checar(guardadas[0].Motivo is { } mo && mo.Contains("não reconhece") && aud.Any(a => a.Contains("INVALIDTRN") && a.Contains("orfa")), "motivo 'PayGo não reconhece' e auditoria: " + guardadas[0].Motivo);

            // (b) 'cnf_sem_ack' cujo REQNUM a biblioteca não segura (ela segura OUTRO): CNF devolve
            // INVALIDTRN -> 'orfa', nunca 'pago'. A pendência da biblioteca (desconhecida) leva REV.
            var semAck = aprovada with { Situacao = "cnf_sem_ack" };
            var f2 = new FakePGWebLib { PendenciaNoInit = new FakePGWebLib.Pendencia("999", "LOC999", "700999", "VM1", "REDE") };
            var guardadas2 = new List<TransacaoPayGo>();
            var p2 = Provedor(f2, t => { guardadas2.Add(t); return true; });
            p2.ResolverPendenciasAsync(new[] { (semAck, true) }).GetAwaiter().GetResult();
            checar(guardadas2.Count == 1 && guardadas2[0].Situacao == "orfa" && !guardadas2.Any(g => g.Situacao == "pago"), "boot: CNF com INVALIDTRN -> 'orfa' (nunca 'pago'): " + string.Join(",", guardadas2.Select(g => g.Situacao)));
            checar(f2.Confirmadas.Count == 1 && f2.Confirmadas[0] == (PW.PWCNF_REV_PWR_AUT, "999"), "só a pendência da biblioteca (outro REQNUM) foi resolvida, por REV");

            // (c) em voo: a biblioteca aprova e não reconhece o CNF em seguida. Erro com aviso de
            // conferir; a linha fica 'orfa'; a tela não pode dizer nem pago nem desfeita.
            var f3 = new FakePGWebLib { EsquecerPendencia = true };
            var guardadas3 = new List<TransacaoPayGo>();
            var p3 = Provedor(f3, t => { guardadas3.Add(t); return true; });
            var d3 = Cobrar(p3, TipoTef.Credito, 10m);
            checar(!d3.Pago && d3.Situacao == SituacaoTef.Erro && !d3.Desfeita && d3.PosPodeTerFicadoOcupado && d3.Motivo!.Contains("não reconhece"), "em voo: CNF INVALIDTRN -> Erro com aviso (" + d3.Motivo + ")");
            checar(guardadas3[^1].Situacao == "orfa" && !guardadas3.Any(g => g.Situacao == "pago"), "linha 'orfa', nunca 'pago': " + string.Join(",", guardadas3.Select(g => g.Situacao)));

            // (d) em voo: Guardar falhou, REV devolve INVALIDTRN: não é 'desfeita'.
            var f4 = new FakePGWebLib { EsquecerPendencia = true };
            var guardadas4 = new List<TransacaoPayGo>();
            var p4 = Provedor(f4, t => { guardadas4.Add(t); return t.Situacao != "aprovada"; });
            var d4 = Cobrar(p4, TipoTef.Credito, 10m);
            checar(!d4.Pago && !d4.Desfeita && d4.PosPodeTerFicadoOcupado && guardadas4[^1].Situacao == "orfa", "REV com INVALIDTRN em voo: sem Desfeita, linha 'orfa' (" + d4.Motivo + ")");

            // (e) reenvio: cnf_sem_ack e a biblioteca perde a pendência antes do reenvio -> 'orfa' e sai da fila.
            var f5 = new FakePGWebLib { FalharConfirmacao = true };
            var guardadas5 = new List<TransacaoPayGo>();
            var p5 = Provedor(f5, t => { guardadas5.Add(t); return true; });
            var d5 = Cobrar(p5, TipoTef.Credito, 10m);
            checar(d5.Pago && d5.PaymentStatus == "cnf_sem_ack", "1ª venda ficou cnf_sem_ack");
            f5.Esquecer();
            var d6 = Cobrar(p5, TipoTef.Credito, 20m);
            var linha1 = guardadas5.Last(g => g.ChargeId == d5.ChargeId);
            checar(d6.Pago && d6.PaymentStatus == "pago" && linha1.Situacao == "orfa", "reenvio com INVALIDTRN: 1ª vira 'orfa' (não 'pago'); 2ª sai normal: " + linha1.Situacao);
            var d7 = Cobrar(p5, TipoTef.Credito, 30m);
            checar(d7.Pago && guardadas5.Count(g => g.ChargeId == d5.ChargeId && g.Situacao == "orfa") == 1, "saiu da fila de reenvio (não tenta de novo)");
        }

        // ── cancelamento de venda (SALEVOID) ─────────────────────────────
        {
            var f = new FakePGWebLib();
            var guardadas = new List<TransacaoPayGo>();
            var perguntas = new List<PwGetData>();
            var p = Provedor(f, t => { guardadas.Add(t); return true; }, perguntar: (g, _) => { perguntas.Add(g); return Task.FromResult<string?>("1234"); });
            var d = Cobrar(p, TipoTef.Credito, 15m);
            var nsu = f.UltimoNsu!; var req = f.UltimoReqNum!;
            var original = guardadas.Last(g => g.Situacao == "pago");
            var e = p.CancelarAsync(original, CancellationToken.None).GetAwaiter().GetResult();
            checar(e.Pago && e.PaymentStatus == "estornado" && e.ChargeId!.StartsWith("pgweb-cnc-"), "SALEVOID aprovado: Pago com payment_status estornado (" + e.Motivo + ")");
            checar(f.Ultima!.Oper == PW.PWOPER_SALEVOID, "PW_iNewTransac(PWOPER_SALEVOID)");
            checar(P(f, PW.PWINFO_TRNORIGNSU) == nsu && P(f, PW.PWINFO_TRNORIGAMNT) == "1500" && P(f, PW.PWINFO_TRNORIGAUTH) == "A" + req && P(f, PW.PWINFO_TRNORIGREQNUM) == req,
                "TRNORIGNSU/AMNT/AUTH/REQNUM da venda original");
            checar(P(f, PW.PWINFO_TRNORIGDATE) == "050926" && P(f, PW.PWINFO_TRNORIGTIME) == "143000", $"TRNORIGDATE DDMMAA e TRNORIGTIME hhmmss ({P(f, PW.PWINFO_TRNORIGDATE)} {P(f, PW.PWINFO_TRNORIGTIME)})");
            checar(perguntas.Count == 1 && perguntas[0].Tipo == PW.PWDAT_USERAUTH && perguntas[0].Ocultar && P(f, FakePGWebLib.IdSenhaLojista) == "1234", "senha do lojista (PWDAT_USERAUTH, oculta) perguntada à tela e devolvida em AddParam");
            var sits = guardadas.Where(g => g.ChargeId == e.ChargeId).Select(g => g.Situacao).ToList();
            checar(sits.SequenceEqual(new[] { "aprovada", "estornado" }), "linha do cancelamento: aprovada -> estornado (nunca 'pago'): " + string.Join(",", sits));
            checar(guardadas.Any(g => g.ChargeId == original.ChargeId && g.Situacao == "estornada"), "venda original vira 'estornada'");
            checar(f.Confirmadas.Count == 2 && f.Confirmadas[1].Resultado == PW.PWCNF_CNF_AUTO && f.Confirmadas[1].ReqNum == f.UltimoReqNum, "CNF do cancelamento com o REQNUM novo");
            checar(guardadas.Last(g => g.ChargeId == e.ChargeId).EhCancelamento, "resposta do CNC é reconhecida como cancelamento (000=CNC)");

            var semNsu = original with { Resposta = RespostaPayGo.Analisar("000-000 = CRT\r\n009-000 = 0\r\n999-999 = 0\r\n") };
            var e2 = p.CancelarAsync(semNsu, CancellationToken.None).GetAwaiter().GetResult();
            checar(!e2.Pago && e2.Situacao == SituacaoTef.Erro, "original sem NSU: não tenta");

            var f3 = new FakePGWebLib();
            var p3 = Provedor(f3, perguntar: (_, _) => Task.FromResult<string?>("1234"));
            Cobrar(p3, TipoTef.Credito, 15m);
            f3.Roteiro.Enqueue(FakePGWebLib.Desfecho.Recusar);
            var e3 = p3.CancelarAsync(guardadas.Last(g => g.Situacao == "pago"), CancellationToken.None).GetAwaiter().GetResult();
            checar(!e3.Pago && e3.Situacao == SituacaoTef.Recusado && e3.Motivo == "CANCELAMENTO NAO AUTORIZADO", "cancelamento negado pelo host: Recusado com a mensagem");
        }

        // ── administrativa e reimpressão ─────────────────────────────────
        {
            var f = new FakePGWebLib();
            var guardadas = new List<TransacaoPayGo>();
            var perguntas = new List<PwGetData>();
            var impressas = new List<TransacaoPayGo>();
            var p = Provedor(f, t => { guardadas.Add(t); return true; }, imprimir: t => { impressas.Add(t); return Task.FromResult(true); },
                perguntar: (g, _) => { perguntas.Add(g); return Task.FromResult<string?>("RELATORIO"); });
            var d = p.AdministrativaAsync(CancellationToken.None).GetAwaiter().GetResult();
            checar(d.Pago && d.PaymentStatus == "adm" && d.ChargeId!.StartsWith("pgweb-adm-"), "ADMIN concluída: PaymentStatus adm (" + d.Motivo + ")");
            checar(f.Ultima!.Oper == PW.PWOPER_ADMIN && perguntas.Count == 1 && perguntas[0].EhMenu && perguntas[0].Opcoes!.Count == 3, "PW_iNewTransac(PWOPER_ADMIN) e o menu vai para a tela");
            checar(P(f, FakePGWebLib.IdMenuAdm) == "3", "texto da opção escolhido na tela vira o VALOR no AddParam");
            checar(guardadas.Count == 0 && f.Confirmadas.Count == 0, "CNFREQ=0: nada gravado como transação, nada confirmado");
            checar(impressas.Count == 1 && impressas[0].Resposta!.ViaUnica.Count == 2, "RCPTFULL vira via única (029) e é impressa");

            var r = p.ReimprimirAsync(CancellationToken.None).GetAwaiter().GetResult();
            checar(r.Pago && r.PaymentStatus == "adm" && f.Ultima!.Oper == PW.PWOPER_REPRINT && impressas.Count == 2, "REPRINT: PWOPER_REPRINT e o comprovante devolvido é impresso");
            checar(!guardadas.Any(g => g.Situacao == "pago"), "administrativa/reimpressão nunca viram 'pago'");
        }

        // ── passo 16: Esc no menu administrativo ─────────────────────────
        // "Operação cancelada no menu administrativo": o operador abre o ADM pela Configuração,
        // a lista da biblioteca vira diálogo da casa e ele aperta Esc sem escolher nada. O roteiro
        // v20260819 cobra duas coisas: nada realizado para a automação, e a mensagem de erro
        // "OPERAÇÃO CANCELADA" — que é da biblioteca, não nossa. Para ela poder escrevê-la, o caixa
        // tem que avisar que o dado não vem (PW_iAddParam(PWINFO_OPERABORTED)); antes disto a
        // transação era abandonada aberta e a tela mostrava um texto nosso no lugar da frase dela.
        {
            var f = new FakePGWebLib();
            var guardadas = new List<TransacaoPayGo>();
            var impressas = new List<TransacaoPayGo>();
            var aud = new List<string>();
            var perguntas = new List<PwGetData>();
            var p = Provedor(f, t => { guardadas.Add(t); return true; }, imprimir: t => { impressas.Add(t); return Task.FromResult(true); },
                perguntar: (g, _) => { perguntas.Add(g); return Task.FromResult<string?>(null); }, auditoria: aud);
            var d = p.AdministrativaAsync(CancellationToken.None).GetAwaiter().GetResult();

            checar(perguntas.Count == 1 && perguntas[0].EhMenu && perguntas[0].NotificarCancelamento,
                "o menu administrativo chega ao operador com bNotificarCancelamento ligado");
            checar(P(f, PW.PWINFO_OPERABORTED) == "1",
                "Esc no menu: o caixa avisa a biblioteca com PW_iAddParam(PWINFO_OPERABORTED)");
            checar(d.Motivo == "OPERACAO CANCELADA",
                "e a mensagem que sobe para a tela é a da biblioteca (PWINFO_RESULTMSG), não uma frase nossa: " + d.Motivo);
            checar(!d.Pago && d.Situacao == SituacaoTef.Cancelado && d.Codigo == CodigoTef.Cancelado,
                "desfecho cancelado, nunca recusa nem erro de plataforma");
            checar(guardadas.Count == 0 && f.Confirmadas.Count == 0 && impressas.Count == 0,
                "transação não realizada: nada em tef_transacao, nada confirmado, nada impresso");
            checar(P(f, FakePGWebLib.IdMenuAdm) is null, "e nenhuma opção do menu foi respondida no lugar do operador");
            checar(aud.Any(a => a.Contains("PWINFO_OPERABORTED")), "a auditoria registra o aviso, que é a prova do passo 16");

            // A frase da tela da Configuração é montada com esse Motivo: é ela a evidência do passo.
            checar(("✗ Operação administrativa não concluída: " + (d.Motivo ?? "sem detalhe")).Contains("OPERACAO CANCELADA"),
                "a linha da Configuração mostra a frase da biblioteca");

            // Biblioteca que NÃO pede aviso (bNotificarCancelamento=0): o caixa não inventa o
            // parâmetro, encerra por conta e mostra a frase da casa. É o comportamento de sempre.
            var f2 = new FakePGWebLib { MenuAdmPedeAviso = false };
            var guardadas2 = new List<TransacaoPayGo>();
            var p2 = Provedor(f2, t => { guardadas2.Add(t); return true; }, perguntar: (_, _) => Task.FromResult<string?>(null));
            var d2 = p2.AdministrativaAsync(CancellationToken.None).GetAwaiter().GetResult();
            checar(P(f2, PW.PWINFO_OPERABORTED) is null, "sem a marca da biblioteca o caixa não manda PWINFO_OPERABORTED");
            checar(!d2.Pago && d2.Situacao == SituacaoTef.Cancelado && d2.Motivo == "operação cancelada pelo operador" && guardadas2.Count == 0,
                "e mesmo assim: cancelado, nada gravado, frase da casa intacta (" + d2.Motivo + ")");
            checar(d2.Motivo is not null && !d2.Motivo.Contains('—'), "sem travessão no texto que vai para a tela");
        }

        // ── ambiente: produção x homologação (PW_iSetEnvironment) ────────
        {
            // O kit avulso da biblioteca, sem o PayGo Windows, atende os dois ambientes com a
            // MESMA DLL. Quem escolhe é PW_iSetEnvironment, e sem chamar nada vale produção.
            // Antes disso, quem escolhia era o instalador do PayGo Windows.
            var f = new FakePGWebLib();
            var p = Provedor(f, opcoes: Opcoes with { Ambiente = PW.ENVRMNT_TEST });
            p.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult();
            checar(f.AmbientesPedidos.Count == 1 && f.AmbientesPedidos[0] == PW.ENVRMNT_TEST,
                "config em homologação: PW_iSetEnvironment(ENVRMNT_TEST) uma vez");
            // Medido em 07/09/2026 com a biblioteca de verdade: chamada ANTES do PW_iInit, ela
            // devolve PWRET_NOTINST e o ambiente NAO e aplicado, ou seja, o caixa continuaria no
            // host de producao calado. Depois do Init funciona. O cabecalho oficial exige que ela
            // venha antes de o PONTO DE CAPTURA estar instalado, e vem.
            var ordem = f.Chamadas.FindIndex(c => c.StartsWith("SetEnvironment"));
            var ordemInit = f.Chamadas.IndexOf("Init");
            checar(ordem >= 0 && ordemInit >= 0 && ordem > ordemInit,
                "e ela vem DEPOIS do PW_iInit (antes, a biblioteca de verdade recusa com NOTINST)");

            var f2 = new FakePGWebLib();
            var p2 = Provedor(f2);
            p2.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult();
            checar(f2.AmbientesPedidos.Count == 1 && f2.AmbientesPedidos[0] == PW.ENVRMNT_PROD,
                "sem configuração o caixa fica em produção, que é o padrão da própria biblioteca");

            // Terminal já instalado recusa a troca. Isso não é defeito e não pode derrubar o caixa.
            var aud3 = new List<string>();
            var f3 = new FakePGWebLib { RecusarAmbiente = true };
            var p3 = Provedor(f3, auditoria: aud3);
            checar(p3.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult(),
                "biblioteca recusa trocar de ambiente (terminal já instalado): o caixa continua ativo");
            checar(aud3.Any(a => a.Contains("PW_iSetEnvironment")), "e a recusa fica na auditoria");

            // Biblioteca anterior à 4.1.43.10 não exporta a função.
            var aud4 = new List<string>();
            var f4 = new FakePGWebLib { AmbienteLanca = true };
            var p4 = Provedor(f4, auditoria: aud4);
            checar(p4.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult(),
                "biblioteca antiga sem PW_iSetEnvironment: segue no ambiente padrão, sem derrubar o caixa");
            checar(aud4.Any(a => a.Contains("indisponível")), "e o motivo fica na auditoria");

            checar(ConfigPGWebLib.Ambiente(c => c == ConfigPGWebLib.ChaveAmbiente ? "homologacao" : null) == PW.ENVRMNT_TEST
                && ConfigPGWebLib.Ambiente(c => c == ConfigPGWebLib.ChaveAmbiente ? "homologação" : null) == PW.ENVRMNT_TEST
                && ConfigPGWebLib.Ambiente(c => c == ConfigPGWebLib.ChaveAmbiente ? "producao" : null) == PW.ENVRMNT_PROD
                && ConfigPGWebLib.Ambiente(_ => null) == PW.ENVRMNT_PROD
                && ConfigPGWebLib.Ambiente(c => c == ConfigPGWebLib.ChaveAmbiente ? "  " : null) == PW.ENVRMNT_PROD,
                "tef_pgweb_ambiente: com e sem acento vale homologação; vazio e qualquer outra coisa é produção");
            checar(ConfigPGWebLib.RotuloAmbiente(PW.ENVRMNT_TEST) == "Homologação" && ConfigPGWebLib.RotuloAmbiente(PW.ENVRMNT_PROD) == "Produção",
                "o rótulo que a tela mostra");
        }

        // ── NOTINST, INSTALL, Init ───────────────────────────────────────
        {
            var f = new FakePGWebLib { Instalado = false };
            var p = Provedor(f);

            // Medido em 07/09/2026 com a PGWebLib 4.1.50.924 de verdade, numa pasta de
            // trabalho nova: PW_iInit devolve PWRET_OK e a biblioteca sobe inteira, mas a
            // lista de operações de venda devolve PWRET_NOTINST. Antes deste conserto o
            // provedor respondia "ativo" nesse estado, e a tela do caixa oferecia cartão
            // para depois falhar com o cliente esperando.
            checar(!p.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult(),
                "terminal sem instalação: AtivoAsync false mesmo com PW_iInit OK");
            checar(p.MotivoIndisponivel == ProvedorPGWebLib.MsgNaoInstalado,
                "e o motivo é o que manda instalar, não 'TEF não responde'");

            var d = Cobrar(p, TipoTef.Credito, 10m);
            checar(!d.Pago && d.Situacao == SituacaoTef.Erro && d.Motivo == ProvedorPGWebLib.MsgNaoInstalado, "PWRET_NOTINST: mensagem curta mandando instalar");
            var i = p.InstalarAsync(CancellationToken.None).GetAwaiter().GetResult();
            checar(i.Pago && f.Instalado, "PWOPER_INSTALL conclui a instalação");
            checar(p.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult() && p.MotivoIndisponivel is null,
                "instalado, AtivoAsync volta a ser true e o motivo some");
            d = Cobrar(p, TipoTef.Credito, 10m);
            checar(d.Pago, "depois da instalação a venda sai");
            checar(f.Inits == 1, "PW_iInit uma vez só por processo (não repete a cada comando)");

            var p2 = Provedor(f);
            checar(p2.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult(), "segunda instância no mesmo processo: PWRET_INVCALL no Init é 'já iniciada', serve");

            var f3 = new FakePGWebLib { InitLanca = true };
            var p3 = Provedor(f3);
            checar(!p3.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult(), "DLL ausente (DllNotFoundException): AtivoAsync false, sem derrubar o caixa");
            var d3 = Cobrar(p3, TipoTef.Credito, 10m);
            checar(!d3.Pago && d3.Codigo == CodigoTef.TefNaoResponde && d3.Motivo == ProvedorPGWebLib.MsgTefNaoResponde, "cobrança sem DLL: tef_nao_responde");
            checar(p3.Descricao.Contains("PGWebLib") && p3.Nome == "pgweblib", "Nome/Descricao do provedor");
        }

        // ── ocupado / concorrência ───────────────────────────────────────
        {
            var f = new FakePGWebLib();
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.NuncaTermina);
            var p = Provedor(f, tetoExecMs: 400);
            var t = Task.Run(() => Cobrar(p, TipoTef.Credito, 10m));
            var viuOcupado = false;
            for (var i = 0; i < 100 && !viuOcupado; i++) { viuOcupado = p.Ocupado; Thread.Sleep(5); }
            t.GetAwaiter().GetResult();
            checar(viuOcupado && !p.Ocupado, "Ocupado enquanto a venda está em voo e livre depois");
        }

        // ── idle ─────────────────────────────────────────────────────────
        {
            // Horário que já passou NÃO é "agora": cai no intervalo de segurança (a DLL de verdade
            // devolvia 1955 pelo pivô do .NET e o IdleProc rodaria a cada tique).
            var f = new FakePGWebLib { IdleProcTime = DateTime.Now.AddSeconds(-1).ToString("yyMMddHHmmss") };
            var p = Provedor(f);
            p.IntervaloIdleMs = 200;
            checar(!p.IdleSeDevidoAsync().GetAwaiter().GetResult() && f.IdleProcs == 0, "sem IDLEPROCTIME lido ainda: não roda");
            Cobrar(p, TipoTef.Credito, 10m);
            checar(p.ProximoIdle is { } q0 && q0 > DateTime.Now && !p.IdleSeDevidoAsync().GetAwaiter().GetResult() && f.IdleProcs == 0,
                "PWINFO_IDLEPROCTIME lido na venda; horário no passado vira agora + intervalo de segurança, não roda já");
            Thread.Sleep(250);
            checar(p.IdleSeDevidoAsync().GetAwaiter().GetResult() && f.IdleProcs == 1, "passado o intervalo -> PW_iIdleProc");
            checar(!p.IdleSeDevidoAsync().GetAwaiter().GetResult() && f.IdleProcs == 1, "não repete até a biblioteca informar horário novo");
        }

        // ── PWRET_OK é a aprovação; AUTRESPCODE é informativo ("000", "0000", vazio) ─
        {
            // Spec: PW_iExecTransac devolveu PWRET_OK = aprovada. O código da rede vai para
            // mensagem/auditoria; negar por ele seria recusar uma venda que o cliente pagou.
            foreach (var codigo in new[] { "000", "0000", "0", "" })
            {
                var f = new FakePGWebLib { AutRespCode = codigo };
                var guardadas = new List<TransacaoPayGo>();
                var aud = new List<string>();
                var p = Provedor(f, t => { guardadas.Add(t); return true; }, auditoria: aud);
                var d = Cobrar(p, TipoTef.Credito, 10m);
                checar(d.Pago && d.PaymentStatus == "pago" && f.Confirmadas.Count == 1 && guardadas[^1].Situacao == "pago" && d.Cartao?.Nsu == f.UltimoNsu,
                    $"PWRET_OK com AUTRESPCODE '{codigo}': aprovada, CNF enviado, linha 'pago' ({d.Situacao} {d.Motivo})");
                checar(aud.Any(a => a.Contains("PWRET_OK") && a.Contains("AUTRESPCODE")), $"AUTRESPCODE '{codigo}' fica na auditoria (informativo)");
            }
            // A recusa continua vindo pelo RETORNO (PWRET_FROMHOST), nunca pelo código.
            var fr = new FakePGWebLib();
            fr.Roteiro.Enqueue(FakePGWebLib.Desfecho.Recusar);
            var pr = Provedor(fr);
            var dr = Cobrar(pr, TipoTef.Credito, 10m);
            checar(!dr.Pago && dr.Situacao == SituacaoTef.Recusado && fr.Confirmadas.Count == 0, "PWRET_FROMHOST segue Recusado (o retorno decide, não o código)");
            // Aprovação definitiva (CNFREQ=0) com "000" e o operador saindo em RETIRE O CARTAO: paga, como com "00".
            var f0 = new FakePGWebLib { Cnfreq = false, AutRespCode = "000" };
            using var cts0 = new CancellationTokenSource();
            var p0 = Provedor(f0);
            var prog0 = new Progresso { AoReceber = a => { if (a.Mensagem == "RETIRE O CARTAO") cts0.Cancel(); } };
            var d0 = Cobrar(p0, TipoTef.Credito, 10m, ct: cts0.Token, andamento: prog0);
            checar(d0.Pago && d0.PaymentStatus == "pago" && d0.Cartao?.Nsu == f0.UltimoNsu, "CNFREQ=0 + AUTRESPCODE '000' + saída em PPREMCRD: Pago (a rede já efetivou): " + d0.Situacao + " " + d0.Motivo);
        }

        // ── idle: PWINFO_IDLEPROCTIME relido após PW_iInit e após cada PW_iIdleProc ─
        {
            var passado = DateTime.Now.AddSeconds(-1).ToString("yyMMddHHmmss");
            var futuro = DateTime.Now.AddHours(1).ToString("yyMMddHHmmss");
            var f = new FakePGWebLib { IdleProcTime = passado, IdleProcTimeDepois = futuro };
            var p = Provedor(f);
            p.IntervaloIdleMs = 200;
            p.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult();   // só PW_iInit, nenhuma venda
            checar(p.ProximoIdle is { } q0 && q0 > DateTime.Now && q0 <= DateTime.Now.AddSeconds(1) && f.Lidos.Contains(PW.PWINFO_IDLEPROCTIME),
                "após PW_iInit o provedor lê PWINFO_IDLEPROCTIME (sem esperar uma venda); horário no passado = agora + intervalo");
            Thread.Sleep(250);
            checar(p.IdleSeDevidoAsync().GetAwaiter().GetResult() && f.IdleProcs == 1, "passado o intervalo: PW_iIdleProc");
            checar(p.ProximoIdle is { } q1 && q1 > DateTime.Now.AddMinutes(30), "após o IdleProc releu o horário novo que a biblioteca informou");
            checar(!p.IdleSeDevidoAsync().GetAwaiter().GetResult() && f.IdleProcs == 1, "e não roda de novo antes dele");

            // Biblioteca sem horário (vazio) ou com lixo: intervalo padrão, nunca "nunca mais".
            var f2 = new FakePGWebLib { IdleProcTime = "" };
            var p2 = Provedor(f2);
            p2.IntervaloIdleMs = 50;
            p2.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult();
            checar(p2.ProximoIdle is { } q2 && q2 > DateTime.Now.AddMilliseconds(-20) && q2 <= DateTime.Now.AddSeconds(1), "PW_iInit sem IDLEPROCTIME: próximo IdleProc em IntervaloIdleMs (fallback), não nulo");
            checar(!p2.IdleSeDevidoAsync().GetAwaiter().GetResult() && f2.IdleProcs == 0, "antes do intervalo não roda");
            Thread.Sleep(80);
            checar(p2.IdleSeDevidoAsync().GetAwaiter().GetResult() && f2.IdleProcs == 1, "passado o intervalo, roda");
            checar(p2.ProximoIdle is { } q3 && q3 > DateTime.Now.AddMilliseconds(-20) && q3 <= DateTime.Now.AddSeconds(1), "IdleProc sem IDLEPROCTIME depois: fallback de novo");
            f2.IdleProcTimeDepois = "lixo";
            Thread.Sleep(80);
            checar(p2.IdleSeDevidoAsync().GetAwaiter().GetResult() && f2.IdleProcs == 2 && p2.ProximoIdle is { } q4 && q4 > DateTime.Now.AddMilliseconds(-20) && q4 <= DateTime.Now.AddSeconds(1),
                "IDLEPROCTIME inválido após o IdleProc: cai no intervalo padrão");

            // Em voo: nunca por cima de uma transação.
            var f3 = new FakePGWebLib { IdleProcTime = passado };
            var p3 = Provedor(f3, tetoExecMs: 300);
            p3.IntervaloIdleMs = 100;   // fica devido no meio da venda em voo (que dura o teto de 300 ms)
            Cobrar(p3, TipoTef.Credito, 10m);
            f3.Roteiro.Enqueue(FakePGWebLib.Desfecho.NuncaTermina);
            var t = Task.Run(() => Cobrar(p3, TipoTef.Credito, 10m));
            for (var i = 0; i < 100 && !p3.Ocupado; i++) Thread.Sleep(5);
            var viuOcupado = p3.Ocupado;
            var rodouEmVoo = viuOcupado && p3.IdleSeDevidoAsync().GetAwaiter().GetResult();
            t.GetAwaiter().GetResult();
            checar(viuOcupado && !rodouEmVoo && f3.IdleProcs == 0, "IdleProc devido com venda em voo: espera (nunca por cima da transação)");
            Thread.Sleep(150);   // a venda que acabou releu o horário (no passado): agora + 100 ms de novo
            checar(p3.IdleSeDevidoAsync().GetAwaiter().GetResult() && f3.IdleProcs == 1, "livre de novo: roda");

            // Timer: roda sozinho e MORRE no Dispose (é o que Servicos.RecarregarTef faz com a instância velha).
            var f4 = new FakePGWebLib { IdleProcTime = passado };
            var p4 = Provedor(f4);
            p4.IntervaloIdleMs = 20;
            p4.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult();
            p4.IniciarIdle(20);
            for (var i = 0; i < 200 && f4.IdleProcs == 0; i++) Thread.Sleep(5);
            checar(f4.IdleProcs >= 1 && p4.IntervaloIdleMs == 20, "o timer de IniciarIdle chama PW_iIdleProc sozinho e fixa o intervalo de segurança");
            p4.Dispose();
            Thread.Sleep(60);
            var depois = f4.IdleProcs;
            p4.IniciarIdle(20);
            Thread.Sleep(120);
            checar(f4.IdleProcs == depois, $"Dispose para o timer e IniciarIdle depois dele não ressuscita ({depois} -> {f4.IdleProcs})");
            p4.Dispose();
            checar(true, "Dispose duas vezes não lança");
        }

        // ── PW_iPPAbort: o ExecTransac seguinte devolve PWRET_CANCEL; cancelada pelo operador ─
        {
            // Fake, direto: depois de PPAbort, PPEventLoop e ExecTransac cancelam até o próximo NewTransac.
            var fx = new FakePGWebLib();
            fx.Init(@"C:\PAYGO\teste");
            fx.NewTransac(PW.PWOPER_SALE);
            foreach (var (k, v) in new[] { (PW.PWINFO_TOTAMNT, "1000"), (PW.PWINFO_CURRENCY, "986"), (PW.PWINFO_AUTNAME, "X"), (PW.PWINFO_AUTVER, "1"), (PW.PWINFO_AUTHSYST, "REDE") })
                fx.AddParam(k, v);
            checar(fx.ExecTransac(out _) == PW.PWRET_NOTHING && fx.ExecTransac(out var ped) == PW.PWRET_MOREDATA && ped.Count == 1 && fx.PPGetCard(0) == PW.PWRET_OK, "fake: chegou à captura do cartão");
            checar(fx.PPAbort() == PW.PWRET_OK && fx.PPEventLoop(out _) == PW.PWRET_CANCEL && fx.ExecTransac(out _) == PW.PWRET_CANCEL && fx.ExecTransac(out _) == PW.PWRET_CANCEL,
                "fake: depois de PW_iPPAbort, PW_iPPEventLoop e PW_iExecTransac devolvem PWRET_CANCEL (e continuam)");
            fx.NewTransac(PW.PWOPER_SALE);
            checar(fx.ExecTransac(out _) != PW.PWRET_CANCEL, "fake: PW_iNewTransac zera o cancelamento");

            // Provedor: operador cancela com a biblioteca no host (laço PWRET_NOTHING, sem captura
            // no pinpad): PPAbort, e o ExecTransac seguinte cancela. Sem confirmação, sem 'aprovada'.
            var f = new FakePGWebLib();
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.NuncaTermina);
            var guardadas = new List<TransacaoPayGo>();
            using var cts = new CancellationTokenSource();
            var p = Provedor(f, t => { guardadas.Add(t); return true; }, tetoExecMs: 5000);
            var relogio = System.Diagnostics.Stopwatch.StartNew();
            cts.CancelAfter(150);
            var d = Cobrar(p, TipoTef.Credito, 10m, ct: cts.Token);
            checar(!d.Pago && d.Situacao == SituacaoTef.Cancelado && d.Codigo == CodigoTef.Cancelado && d.Motivo == "cobrança cancelada pelo operador",
                "PPAbort no host e PWRET_CANCEL no ExecTransac seguinte: Cancelado pelo operador (" + d.Motivo + ")");
            checar(f.Abortos == 1 && f.Chamadas.LastIndexOf("ExecTransac") > f.Chamadas.LastIndexOf("PPAbort"), "PW_iPPAbort chamado uma vez e a automação voltou ao PW_iExecTransac, que cancelou");
            checar(relogio.ElapsedMilliseconds < 2000, $"saiu na hora ({relogio.ElapsedMilliseconds} ms), sem esperar o teto");
            checar(f.Confirmadas.Count == 0 && guardadas[^1].Situacao == "cancelado" && !guardadas.Any(g => g.Situacao is "aprovada" or "pago"),
                "sem PW_iConfirmation, nada gravado como aprovada/pago: " + string.Join(",", guardadas.Select(g => g.Situacao)));
            checar(!d.PosPodeTerFicadoOcupado && !d.Desfeita, "a biblioteca acusou o cancelamento: sem aviso de POS ocupado, nada a desfazer");
            checar(!p.Ocupado, "semáforo liberado");
        }

        // ── RespostaDaLib (mapa PWINFO -> intpos) ─────────────────────────
        {
            var r = ProvedorPGWebLib.RespostaDaLib("CRT", "123", 1500, TipoTef.Credito, 1, true, new Dictionary<ushort, string>
            {
                [PW.PWINFO_CNFREQ] = "0", [PW.PWINFO_AUTHSYST] = "CIELO", [PW.PWINFO_AUTEXTREF] = "99", [PW.PWINFO_RCPTFULL] = "L1\r\nL2\nL3\r",
                [PW.PWINFO_RCPTPRN] = "1", [PW.PWINFO_CARDNAME] = "MASTERCARD DEBITO",
            });
            checar(!r.RequerConfirmacao && r.Rede == "CIELO" && r.Nsu == "99" && r.ValorCent == 1500, "CNFREQ=0 -> 729=1; rede/NSU/valor");
            checar(r.ViaUnica.Count == 3 && r.ViaCliente.Count == 0 && r.Vias == 1, "RCPTFULL só vira 029 quando não há 713/715; tolera CR, LF e CRLF");
            checar(r.NomeCartao == "MASTERCARD DEBITO" && ClientePayGo.TBand(r.NomeCartao) == "02", "sem CARDNAMESTD, 040 recebe CARDNAME");
            // ⭐ PASSO 09: reduzido do portador (RCPTCHSHORT) + diferenciado do lojista
            // (RCPTMERCH), e a biblioteca não disse quantas vias (sem RCPTPRN). O reduzido
            // conta como via do CLIENTE na hora de deduzir o 737: contando só o 713/715 o
            // 737 saía 2 (só o lojista) e o papel do cliente ficava preso na resposta.
            var c6 = ProvedorPGWebLib.RespostaDaLib("CRT", "9", 500, TipoTef.Credito, 1, true, new Dictionary<ushort, string>
            {
                [PW.PWINFO_RCPTCHSHORT] = "REDUZIDO L1\rREDUZIDO L2", [PW.PWINFO_RCPTMERCH] = "LOJISTA L1",
            });
            checar(c6.Vias == 3 && c6.CupomReduzido.Count == 2 && c6.ViaEstabelecimento.Count == 1 && c6.ViaCliente.Count == 0,
                $"⭐ sem RCPTPRN, reduzido + lojista deduz 737=3 (deu {c6.Vias})");
            var rotC6 = Servicos.ViasRotuladas(c6);
            checar(rotC6.Count == 2 && rotC6[0].Qual == Servicos.ViaTef.Cliente && rotC6[1].Qual == Servicos.ViaTef.Estabelecimento,
                "⭐ e as duas folhas do passo 09 chegam rotuladas à impressão automática");
            var soReduzido = ProvedorPGWebLib.RespostaDaLib("CRT", "9", 500, TipoTef.Credito, 1, true,
                new Dictionary<ushort, string> { [PW.PWINFO_RCPTCHSHORT] = "REDUZIDO L1" });
            checar(soReduzido.Vias == 1 && Servicos.ViasRotuladas(soReduzido).Count == 1,
                $"só o reduzido e sem RCPTPRN: 737=1 e a via do cliente sai (deu {soReduzido.Vias})");
            var neg = ProvedorPGWebLib.RespostaDaLib("CRT", "1", 100, TipoTef.Debito, 1, false, new Dictionary<ushort, string> { [PW.PWINFO_RESULTMSG] = "SALDO\rINSUFICIENTE" });
            checar(!neg.Aprovada && neg.Mensagem == "SALDO INSUFICIENTE" && neg.TipoCartao == 2, "negada: 009=1, 030 sem 0Dh, 731 pelo tipo");
        }

        // ── achados da DLL de verdade (07/09/2026, PGWebLib.dll 4.1.50.24 x86) ─
        // Cada bloco reproduz no fake o que a DLL fez no harness e prova o que o provedor faz a respeito.
        {
            // 1. PW_iInit devolve PWRET_WRITERR se o diretório de trabalho não existe: a DLL não o cria, o provedor cria.
            var pasta = Path.Combine(Path.GetTempPath(), "pdv-pgweb-" + Guid.NewGuid().ToString("N"));
            var f = new FakePGWebLib { ExigePasta = true };
            var aud = new List<string>();
            var p = new ProvedorPGWebLib(f, pasta, Opcoes) { Auditar = aud.Add };
            checar(!Directory.Exists(pasta), "a pasta de trabalho ainda não existe");
            checar(p.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult() && f.Inits == 1 && Directory.Exists(pasta),
                "o provedor CRIA a pasta antes do PW_iInit e o Init passa (o fake devolve WRITERR sem ela)");
            checar(p.MotivoIndisponivel is null && aud.Count == 0, "iniciada: sem motivo de indisponibilidade e sem auditoria");
            try { Directory.Delete(pasta, true); } catch { }

            // Pasta que NÃO dá para criar (um arquivo no lugar do pai): auditoria com a pasta e o motivo, mensagem curta na tela.
            var arq = Path.Combine(Path.GetTempPath(), "pdv-pgweb-arq-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(arq, "x");
            var pastaRuim = Path.Combine(arq, "pgweb");
            var f2 = new FakePGWebLib { ExigePasta = true };
            var aud2 = new List<string>();
            var g2 = new List<TransacaoPayGo>();
            var p2 = new ProvedorPGWebLib(f2, pastaRuim, Opcoes) { Auditar = aud2.Add, Guardar = t => { g2.Add(t); return true; } };
            checar(!p2.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult() && f2.Inits == 0, "pasta inacessível: AtivoAsync false sem chamar PW_iInit");
            checar(aud2.Count == 1 && aud2[0].Contains(pastaRuim) && aud2[0].Contains("pasta de trabalho inacessível") && !aud2[0].Contains('\n'),
                "uma linha de auditoria com a pasta e o motivo: " + aud2.FirstOrDefault());
            checar(p2.MotivoIndisponivel == ProvedorPGWebLib.MsgPastaInacessivel, "MotivoIndisponivel é a mensagem da pasta");
            var d2 = Cobrar(p2, TipoTef.Credito, 10m);
            checar(!d2.Pago && d2.Codigo == CodigoTef.TefNaoResponde && d2.Motivo == ProvedorPGWebLib.MsgPastaInacessivel, "cobrança: tef_nao_responde com a mensagem da pasta (" + d2.Motivo + ")");
            checar(ProvedorPGWebLib.MsgPastaInacessivel.StartsWith("TEF não responde: a PGWebLib não iniciou", StringComparison.Ordinal)
                   && ProvedorPGWebLib.MsgPastaInacessivel.EndsWith(", pasta de trabalho inacessível", StringComparison.Ordinal)
                   && !ProvedorPGWebLib.MsgPastaInacessivel.Contains('—'),
                "a mensagem de tela existente ganha o sufixo curto, sem travessão");
            checar(g2.Count == 0 && f2.Chamadas.Count == 0, "nada gravado e nenhuma chamada à biblioteca");
            try { File.Delete(arq); } catch { }

            // 2. PWINFO_IDLEPROCTIME "551231235959": a DLL usa 2055 como "nunca"; pelo pivô do .NET seria 1955 (passado, IdleProc a cada tique).
            var f3 = new FakePGWebLib { IdleProcTime = "551231235959" };
            var p3 = Provedor(f3);
            p3.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult();
            checar(p3.ProximoIdle == new DateTime(2055, 12, 31, 23, 59, 59), "551231235959 -> 31/12/2055 23:59:59 (século 20, não o pivô do .NET): " + p3.ProximoIdle?.ToString("s"));
            checar(!p3.IdleSeDevidoAsync().GetAwaiter().GetResult() && f3.IdleProcs == 0, "PW_iIdleProc NÃO roda no tique seguinte");
            p3.IniciarIdle(20);
            Thread.Sleep(120);
            checar(f3.IdleProcs == 0, "nem com o timer ligado");
            p3.Dispose();
            checar(ProvedorPGWebLib.HorarioIdle("260907120000") == new DateTime(2026, 9, 7, 12, 0, 0)
                   && ProvedorPGWebLib.HorarioIdle("991231235959") == new DateTime(2099, 12, 31, 23, 59, 59)
                   && ProvedorPGWebLib.HorarioIdle("lixo") is null && ProvedorPGWebLib.HorarioIdle("5512312359") is null
                   && ProvedorPGWebLib.HorarioIdle("551331235959") is null && ProvedorPGWebLib.HorarioIdle(null) is null,
                "HorarioIdle: sempre 20xx; lixo, tamanho errado e mês 13 são nulos");
            // Horário no passado (a DLL informou um que já venceu) cai no intervalo padrão, com uma linha de auditoria por valor.
            var f4 = new FakePGWebLib { IdleProcTime = "260101000000" };
            var aud4 = new List<string>();
            var p4 = Provedor(f4, auditoria: aud4);
            p4.IntervaloIdleMs = 200;
            p4.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult();
            checar(p4.ProximoIdle is { } q4 && q4 > DateTime.Now && q4 <= DateTime.Now.AddSeconds(1), "horário no passado (260101000000) cai em agora + intervalo padrão, nunca em 'já'");
            checar(!p4.IdleSeDevidoAsync().GetAwaiter().GetResult() && f4.IdleProcs == 0, "e não roda no tique seguinte");
            checar(aud4.Count(a => a.Contains("IDLEPROCTIME") && a.Contains("no passado") && a.Contains("260101000000")) == 1, "uma linha de auditoria: horário no passado");

            // 3. PW_iAddParam(USINGPINPAD) e (PPCOMMPORT) devolvem PWRET_INVPARAM em PWOPER_ADMIN: parâmetro opcional, a operação segue.
            var f5 = new FakePGWebLib();
            f5.ParamsRecusadosNoAdmin.Add(PW.PWINFO_USINGPINPAD);
            f5.ParamsRecusadosNoAdmin.Add(PW.PWINFO_PPCOMMPORT);
            var aud5 = new List<string>();
            var p5 = Provedor(f5, auditoria: aud5, perguntar: (_, _) => Task.FromResult<string?>("1"));
            var d5 = p5.AdministrativaAsync(CancellationToken.None).GetAwaiter().GetResult();
            checar(d5.Pago && d5.PaymentStatus == "adm", "ADM conclui mesmo com INVPARAM nos dois parâmetros opcionais (" + d5.Motivo + ")");
            checar(!f5.Ultima!.Params.ContainsKey(PW.PWINFO_USINGPINPAD) && !f5.Ultima.Params.ContainsKey(PW.PWINFO_PPCOMMPORT), "o fake de fato recusou (não guardou os dois)");
            checar(aud5.Count(a => a.Contains("PW_iAddParam(32513)") && a.Contains("INVPARAM")) == 1 && aud5.Count(a => a.Contains("PW_iAddParam(32514)") && a.Contains("INVPARAM")) == 1,
                "cada recusa vira UMA linha de auditoria");
            var d5b = Cobrar(p5, TipoTef.Credito, 10m);
            checar(d5b.Pago && P(f5, PW.PWINFO_USINGPINPAD) == "1" && P(f5, PW.PWINFO_PPCOMMPORT) == "0", "na venda os dois são aceitos e a venda sai");

            // 4. PW_iGetResult(PWINFO_AUTDATETIME) devolve PWRET_INVPARAM nesta DLL: campo vazio, sem erro.
            var f6 = new FakePGWebLib();
            f6.InfosRecusados.Add(PW.PWINFO_AUTDATETIME);
            var g6 = new List<TransacaoPayGo>();
            var p6 = Provedor(f6, t => { g6.Add(t); return true; });
            var d6 = Cobrar(p6, TipoTef.Credito, 10m);
            checar(d6.Pago && d6.PaymentStatus == "pago" && f6.Lidos.Contains(PW.PWINFO_AUTDATETIME), "venda aprovada com AUTDATETIME recusado (INVPARAM): Pago");
            var r6 = g6.First(g => g.Situacao == "aprovada").Resposta!;
            checar(r6.Data is null && r6.Hora is null && !r6.Campos.ContainsKey("952-000") && r6.Nsu == f6.UltimoNsu && r6.CodigoControle == f6.UltimoReqNum,
                "022/023/952 ausentes e o resto da resposta intacto");
            var d6b = p6.AdministrativaAsync(CancellationToken.None).GetAwaiter().GetResult();
            checar(d6b.Situacao == SituacaoTef.Cancelado, "ADM sem quem responda o menu cancela como antes (LerResultados tolera o INVPARAM): " + d6b.Motivo);

            // 5. PW_End no fechamento: a DLL iniciada e não encerrada aborta o processo no DLL_PROCESS_DETACH (0xC0000409).
            var f7 = new FakePGWebLib();
            var p7 = Provedor(f7);
            checar(p7.Encerrar() == ProvedorPGWebLib.Encerramento.NaoIniciada && f7.Ends == 0, "sem PW_iInit nesta instância: NaoIniciada, sem PW_End");
            p7.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult();
            checar(p7.Encerrar() == ProvedorPGWebLib.Encerramento.Encerrada && f7.Ends == 1 && f7.Chamadas[^1] == "End", "iniciada: Encerrar chama PW_End (Encerrada)");
            checar(p7.Encerrar() == ProvedorPGWebLib.Encerramento.NaoIniciada && f7.Ends == 1, "segunda vez: nada (PW_End uma vez só)");
            checar(!p7.Ocupado && p7.ProximoIdle is null, "semáforo livre e nenhum idle agendado depois do PW_End");
            var f7b = new FakePGWebLib { InitLanca = false };
            var p7b = Provedor(f7b);
            p7b.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult();
            f7b.EndLanca = true;
            var aud7b = new List<string>();
            var p7c = new ProvedorPGWebLib(f7b, PastaTeste, Opcoes) { Auditar = aud7b.Add };
            p7c.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult();   // INVCALL: já iniciada, serve
            checar(p7c.Encerrar() == ProvedorPGWebLib.Encerramento.Falhou && aud7b.Any(a => a.Contains("PW_End lançou")), "PW_End que lança: Falhou, com auditoria, sem derrubar o fechamento");
            // Nunca por cima de uma operação em voo: espera o teto, desiste com auditoria, e faz depois.
            var f8 = new FakePGWebLib();
            f8.Roteiro.Enqueue(FakePGWebLib.Desfecho.NuncaTermina);
            var aud8 = new List<string>();
            var p8 = Provedor(f8, auditoria: aud8, tetoExecMs: 600);
            var t8 = Task.Run(() => Cobrar(p8, TipoTef.Credito, 10m));
            for (var i = 0; i < 200 && !p8.Ocupado; i++) Thread.Sleep(5);
            var relogio8 = System.Diagnostics.Stopwatch.StartNew();
            var r8 = p8.Encerrar(esperaMs: 100);
            checar(p8.Ocupado && r8 == ProvedorPGWebLib.Encerramento.EmVoo && f8.Ends == 0 && relogio8.ElapsedMilliseconds < 500 && aud8.Any(a => a.Contains("PW_End") && a.Contains("em voo")),
                $"venda em voo: EmVoo, não chama PW_End, desiste no teto ({relogio8.ElapsedMilliseconds} ms) e audita");
            t8.GetAwaiter().GetResult();
            checar(p8.Encerrar() == ProvedorPGWebLib.Encerramento.Encerrada && f8.Ends == 1 && f8.Chamadas.LastIndexOf("End") > f8.Chamadas.LastIndexOf("ExecTransac"), "livre de novo: PW_End depois da última chamada da venda");
            // O timer do idle morre no Encerrar: nenhum PW_iIdleProc depois do PW_End.
            var f9 = new FakePGWebLib { IdleProcTime = DateTime.Now.AddSeconds(-1).ToString("yyMMddHHmmss") };
            var p9 = Provedor(f9);
            p9.IntervaloIdleMs = 20;
            p9.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult();
            p9.IniciarIdle(20);
            for (var i = 0; i < 200 && f9.IdleProcs == 0; i++) Thread.Sleep(5);
            checar(f9.IdleProcs >= 1 && p9.Encerrar() == ProvedorPGWebLib.Encerramento.Encerrada, "idle rodando; Encerrar chama PW_End");
            var n9 = f9.IdleProcs;
            Thread.Sleep(120);
            checar(f9.IdleProcs == n9 && f9.Chamadas.LastIndexOf("IdleProc") < f9.Chamadas.LastIndexOf("End") && f9.Inits == 1,
                "depois do PW_End: nenhum PW_iIdleProc e nenhum PW_iInit novo");
        }

        checar(FakePGWebLib.LeiturasDePan == 0, "em TODA a suíte, PWINFO_CARDFULLPAN nunca foi lido");
    }
}
