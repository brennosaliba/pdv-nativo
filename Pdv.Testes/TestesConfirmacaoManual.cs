using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// Confirmacao e desfazimento MANUAIS (passos 37 a 40 do roteiro v20260819).
///
/// A rede aprova, o caixa grava, o comprovante sai e, so nesses passos, a tela pergunta ao
/// operador: confirmar ou desfazer? A resposta decide o codigo do PW_iConfirmation:
///   confirmar = PWCNF_CNF_MANU_AUT (12833), "confirmada manualmente na Automacao";
///   desfazer  = PWCNF_REV_MANU_AUT (12849), "desfeita manualmente na Automacao".
/// Sem resposta (venda de loja) segue o PWCNF_CNF_AUTO (289) de sempre.
///
/// Nasceu em 09/09/2026: o dono rodou o passo 37 e o caixa confirmou sozinho com 289; no 39
/// ele estornou a venda (CNC), que e outro comando. O roteiro cobra o codigo manual no log.
/// </summary>
public static class TestesConfirmacaoManual
{
    private static ProvedorPGWebLib Provedor(FakePGWebLib f, Func<TransacaoPayGo, CancellationToken, Task<bool?>>? decidir,
        List<TransacaoPayGo>? guardadas = null, List<string>? auditoria = null)
        => new(f, TestesPGWebLib.PastaTeste, new OpcoesPGWebLib("Pdv.AmericanDay", "0.8.9", "American Day", RedeCartao: "C6 PAY", RedePix: "PIX C6 BANK"))
        {
            IntervaloPollMs = 5,
            TempoMaxExecMs = 2000,
            TempoMaxCapturaMs = 2000,
            TempoPerguntaMs = 500,
            Guardar = t => { guardadas?.Add(t); return true; },
            DecidirConfirmacao = decidir,
            Auditar = auditoria is null ? null : auditoria.Add,
        };

    private static DesfechoTef Vender(ProvedorPGWebLib p, decimal reais)
        => p.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(reais), null, 1, null, CancellationToken.None).GetAwaiter().GetResult();

    public static void Rodar(Action<bool, string> checar)
    {
        Directory.CreateDirectory(TestesPGWebLib.PastaTeste);

        // ── 1. passo 37: o operador CONFIRMA na mao -> PWCNF_CNF_MANU_AUT ────────
        {
            var f = new FakePGWebLib();
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var guardadas = new List<TransacaoPayGo>();
            var aud = new List<string>();
            var perguntas = 0;
            var p = Provedor(f, (tx, _) => { perguntas++; return Task.FromResult<bool?>(true); }, guardadas, aud);
            var d = Vender(p, 1012.00m);
            checar(d.Pago && d.PaymentStatus == "pago", "a venda sai paga (" + d.Motivo + ")");
            checar(perguntas == 1, "a tela foi consultada uma vez");
            checar(f.Confirmadas.Count == 1 && f.Confirmadas[0].Resultado == PW.PWCNF_CNF_MANU_AUT && f.Confirmadas[0].ReqNum == f.UltimoReqNum,
                $"PW_iConfirmation com PWCNF_CNF_MANU_AUT (12833) e o REQNUM da venda (veio {(f.Confirmadas.Count > 0 ? f.Confirmadas[0].Resultado : 0)})");
            checar(PW.PWCNF_CNF_MANU_AUT == 12833 && PW.PWCNF_REV_MANU_AUT == 12849, "12833 = 0x3221 e 12849 = 0x3231, como no PGWebLib.h");
            checar(guardadas.Select(g => g.Situacao).SequenceEqual(new[] { "aguardando", "aprovada", "pago" }),
                "gravou aguardando -> aprovada -> pago, como na venda automatica: " + string.Join(",", guardadas.Select(g => g.Situacao)));
            checar(aud.Any(a => a.Contains("PWCNF_CNF_MANU_AUT 12833", StringComparison.Ordinal)),
                "a auditoria diz que a confirmacao foi manual, com o codigo: e isso que a planilha da PayGo cobra");
            checar(f.Pendente is null, "a biblioteca nao segura mais nada");
        }

        // ── 2. passo 39: o operador DESFAZ na mao -> PWCNF_REV_MANU_AUT ──────────
        {
            var f = new FakePGWebLib();
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var guardadas = new List<TransacaoPayGo>();
            var aud = new List<string>();
            var p = Provedor(f, (_, _) => Task.FromResult<bool?>(false), guardadas, aud);
            var d = Vender(p, 1011.00m);
            checar(!d.Pago && d.Situacao == SituacaoTef.Cancelado && d.Codigo == CodigoTef.Cancelado && d.Desfeita,
                $"desfecho Cancelado + Desfeita: a venda nao existe ({d.Situacao}/{d.Codigo}/{d.Desfeita})");
            checar(f.Confirmadas.Count == 1 && f.Confirmadas[0].Resultado == PW.PWCNF_REV_MANU_AUT && f.Confirmadas[0].ReqNum == f.UltimoReqNum,
                $"PW_iConfirmation com PWCNF_REV_MANU_AUT (12849) e o REQNUM da venda (veio {(f.Confirmadas.Count > 0 ? f.Confirmadas[0].Resultado : 0)})");
            checar(d.Reqnum == f.UltimoReqNum, "o REQNUM sobe na tela tambem no desfazimento: e ele que vai para a planilha");
            checar(guardadas.Select(g => g.Situacao).SequenceEqual(new[] { "aguardando", "aprovada", "desfeita" }),
                "gravou aguardando -> aprovada -> desfeita: " + string.Join(",", guardadas.Select(g => g.Situacao)));
            checar(d.Motivo == "venda desfeita pelo operador", "motivo curto; a tela completa com 'o cliente não pagou nada' (veio " + d.Motivo + ")");
            checar(aud.Any(a => a.Contains("PWCNF_REV_MANU_AUT 12849", StringComparison.Ordinal)), "a auditoria registra o desfazimento manual com o codigo");
            checar(!d.PosPodeTerFicadoOcupado, "desfeita com ack nao e orfa");
        }

        // ── 3. sem resposta (null) e sem gancho: o automatico de sempre ─────────
        {
            var f = new FakePGWebLib();
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var d = Vender(Provedor(f, (_, _) => Task.FromResult<bool?>(null)), 25.00m);
            checar(d.Pago && f.Confirmadas.Count == 1 && f.Confirmadas[0].Resultado == PW.PWCNF_CNF_AUTO,
                "null = ninguem quis decidir: PWCNF_CNF_AUTO (289), a venda de loja nao muda");

            var f2 = new FakePGWebLib();
            f2.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var d2 = Vender(Provedor(f2, null), 25.00m);
            checar(d2.Pago && f2.Confirmadas.Count == 1 && f2.Confirmadas[0].Resultado == PW.PWCNF_CNF_AUTO, "sem gancho: PWCNF_CNF_AUTO");
        }

        // ── 4. CNFREQ=0: nao ha o que confirmar, a tela nem e consultada ─────────
        {
            var f = new FakePGWebLib { Cnfreq = false };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var perguntas = 0;
            var d = Vender(Provedor(f, (_, _) => { perguntas++; return Task.FromResult<bool?>(false); }), 25.00m);
            checar(d.Pago && perguntas == 0 && f.Confirmadas.Count == 0,
                "com CNFREQ=0 a rede ja efetivou: sem pergunta, sem CNF e sem REV (perguntas=" + perguntas + ")");
        }

        // ── 5. o gancho lancou: segue automatico, e a auditoria conta ───────────
        {
            var f = new FakePGWebLib();
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var aud = new List<string>();
            var d = Vender(Provedor(f, (_, _) => throw new InvalidOperationException("tela fechou"), auditoria: aud), 25.00m);
            checar(d.Pago && f.Confirmadas.Count == 1 && f.Confirmadas[0].Resultado == PW.PWCNF_CNF_AUTO,
                "gancho que lanca nao derruba a venda: PWCNF_CNF_AUTO");
            checar(aud.Any(a => a.Contains("segue automatica", StringComparison.Ordinal)), "e a auditoria registra que a decisao manual falhou");
        }

        // ── 6. CNF manual sem ack: o reenvio mantem o MESMO codigo (12833) ───────
        {
            var f = new FakePGWebLib { FalharConfirmacao = true };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var vez = 0;
            var p = Provedor(f, (_, _) => Task.FromResult<bool?>(vez++ == 0 ? true : null));
            var d1 = Vender(p, 1012.00m);
            var req1 = f.UltimoReqNum;
            checar(d1.Pago && d1.PaymentStatus == "cnf_sem_ack", "1a venda: CNF manual sem ack fica em cnf_sem_ack (" + d1.PaymentStatus + ")");
            var d2 = Vender(p, 25.00m);
            checar(d2.Pago && f.Confirmadas.Count == 2 && f.Confirmadas[0] == (PW.PWCNF_CNF_MANU_AUT, req1!),
                "o reenvio, antes da 2a venda, foi com PWCNF_CNF_MANU_AUT: o codigo nao vira 289 no caminho");
            checar(f.Confirmadas[1].Resultado == PW.PWCNF_CNF_AUTO, "e a 2a venda, sem decisao, e automatica");
        }

        // ── 7. quais passos perguntam ────────────────────────────────────────────
        {
            checar(RoteiroTef.ConfirmacaoManual(37) && RoteiroTef.ConfirmacaoManual(38) && RoteiroTef.ConfirmacaoManual(39) && RoteiroTef.ConfirmacaoManual(40),
                "37, 38, 39 e 40 sao os passos de confirmacao/desfazimento manual");
            checar(!RoteiroTef.ConfirmacaoManual(36) && !RoteiroTef.ConfirmacaoManual(41) && !RoteiroTef.ConfirmacaoManual(2) && !RoteiroTef.ConfirmacaoManual(null),
                "vizinhos, venda comum e venda sem passo (null) nao perguntam");
            checar(RoteiroTef.Passos.Where(p => RoteiroTef.ConfirmacaoManual(p.Numero)).All(p => p.Titulo.Contains("manual", StringComparison.OrdinalIgnoreCase)),
                "e todos os quatro tem 'manual' no titulo do roteiro");
            checar(RoteiroTef.Passos.Where(p => RoteiroTef.ConfirmacaoManual(p.Numero)).All(p => p.Situacao == "pronto"),
                "e o roteiro os declara prontos");
        }

        // ── 8. a tela liga o gancho, e so pelo passo do roteiro ─────────────────
        {
            var raiz = AcharRaiz();
            checar(raiz is not null, "achei a raiz do repositorio");
            if (raiz is not null)
            {
                var servicos = File.ReadAllText(Path.Combine(raiz, "Servicos.cs"));
                var pagamento = File.ReadAllText(Path.Combine(raiz, "Telas", "Pagamento.xaml.cs"));
                checar(servicos.Contains("DecidirConfirmacao = DecidirConfirmacaoNaTelaAsync", StringComparison.Ordinal),
                    "Servicos liga DecidirConfirmacao no provedor PGWebLib");
                checar(servicos.Contains("if (!ConfirmacaoManualTef || ct.IsCancellationRequested) return null;", StringComparison.Ordinal),
                    "e devolve null (automatico) fora dos passos de confirmacao manual");
                checar(servicos.Contains("\"Confirmar venda\", \"Desfazer venda\"", StringComparison.Ordinal),
                    "o dialogo tem os dois botoes, Confirmar venda e Desfazer venda");
                checar(pagamento.Contains("Servicos.ConfirmacaoManualTef = RoteiroTef.ConfirmacaoManual(PassoDoRoteiro);", StringComparison.Ordinal),
                    "a tela de pagamento decide pelo passo do roteiro, a cada cobranca");
                var i = servicos.IndexOf("DecidirConfirmacaoNaTelaAsync(TransacaoPayGo tx", StringComparison.Ordinal);
                var trecho = i < 0 ? "" : servicos.Substring(i, Math.Min(900, servicos.Length - i));
                checar(trecho.Length > 0 && !trecho.Contains('—'), "sem travessao nos textos do dialogo");
            }
        }
    }

    /// <summary>
    /// O placar (revisao adversarial de 09/09/2026): o desfazimento manual dos passos 39 e 40 e
    /// o resultado ESPERADO, e o placar chamava isso de "erro" (✗ na tela, "erro em ..." na
    /// planilha). Agora e "desfeito", e conta como feito so nesses dois passos.
    /// </summary>
    public static void RodarPlacar(Action<bool, string> checar)
    {
        var p39 = RoteiroTef.Passos.First(p => p.Numero == 39);
        var p37 = RoteiroTef.Passos.First(p => p.Numero == 37);
        var agora = DateTime.Now;
        var l39 = new LinhaDoPlacar(p39, new PassoFeito(39, PlacarHomologacao.Desfeito, "0000283005", agora));
        var l37 = new LinhaDoPlacar(p37, new PassoFeito(37, PlacarHomologacao.Desfeito, "0000283006", agora));
        checar(l39.Ok, "passo 39 desfeito na mao e FEITO (✓)");
        checar(!l37.Ok && l37.Tentado, "passo 37 desfeito e engano: tentado, nao feito");
        checar(RoteiroTef.DesfazimentoEsperado(39) && RoteiroTef.DesfazimentoEsperado(40) && !RoteiroTef.DesfazimentoEsperado(37) && !RoteiroTef.DesfazimentoEsperado(null),
            "39 e 40 esperam desfazimento; 37 e venda sem passo nao");
        var csv = PlacarHomologacao.Csv(new[] { l39 });
        checar(csv.Contains("0000283005", StringComparison.Ordinal) && csv.Contains("desfeito em", StringComparison.Ordinal) && !csv.Contains("erro em", StringComparison.Ordinal),
            "a planilha diz 'desfeito', com o REQNUM, e nunca 'erro' no 39");
        var (feitos, _) = PlacarHomologacao.Progresso(new[] { l39, l37 });
        checar(feitos == 1, "o progresso conta o 39 e nao o 37: " + feitos);

        var raiz = AcharRaiz();
        if (raiz is not null)
        {
            var pagamento = File.ReadAllText(Path.Combine(raiz, "Telas", "Pagamento.xaml.cs"));
            checar(pagamento.Contains("desfeitaNaMao ? PlacarHomologacao.Desfeito", StringComparison.Ordinal),
                "a tela de pagamento anota 'desfeito' quando a venda foi aprovada e desfeita na mao");
            var servicos = File.ReadAllText(Path.Combine(raiz, "Servicos.cs"));
            var i = servicos.IndexOf("DecidirConfirmacaoNaTelaAsync(TransacaoPayGo tx", StringComparison.Ordinal);
            var fimMetodo = i < 0 ? -1 : servicos.IndexOf("});", i, StringComparison.Ordinal);
            var corpo = i < 0 || fimMetodo < 0 ? "" : servicos[i..fimMetodo];
            checar(corpo.Contains("Dialogo.Escolher(", StringComparison.Ordinal) && !corpo.Contains("Dialogo.Confirmar(", StringComparison.Ordinal),
                "o dialogo 'Rede aprovou' e um Escolher: Esc (-1) nao vira 'Desfazer venda'");
            checar(corpo.Contains("if (i == 0) return true;", StringComparison.Ordinal) && corpo.Contains("if (i == 1) return false;", StringComparison.Ordinal),
                "so os dois botoes decidem; o resto repete a pergunta");
        }
    }

    private static string? AcharRaiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "Telas")) && File.Exists(Path.Combine(dir.FullName, "Servicos.cs")))
                return dir.FullName;
        return null;
    }
}
