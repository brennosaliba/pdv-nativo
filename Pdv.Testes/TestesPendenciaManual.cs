using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// PENDÊNCIA RESOLVIDA ANTES DE UMA VENDA NOVA: CÓDIGOS MANUAIS (retorno da PayGo em 10/09/2026).
///
/// Passo 34: a segunda venda volta negada trazendo a transação pendente do passo 33, que
/// este caixa conhece como paga. A automação decide sozinha, pelo próprio registro, e por
/// isso confirma com PWCNF_CNF_MANU_AUT (12833), não com o automático de logo depois da
/// venda (289). Passo 36: a pendente é uma que este caixa nunca viu; desfaz com
/// PWCNF_REV_MANU_AUT (12849), não com o de queda de energia (536881), que fica só para
/// o religamento (passo 54, coberto em TestesPGWebLib).
/// </summary>
public static class TestesPendenciaManual
{
    private static readonly OpcoesPGWebLib Opcoes = new("Pdv.AmericanDay", "0.5.9", "American Day", RedeCartao: "REDE", RedePix: "PIX ITAU");

    private static ProvedorPGWebLib Provedor(FakePGWebLib f, Func<string, bool>? conhecida, List<string> aud)
        => new(f, TestesPGWebLib.PastaTeste, Opcoes)
        {
            IntervaloPollMs = 5,
            TempoMaxExecMs = 2000,
            TempoMaxCapturaMs = 2000,
            TempoPerguntaMs = 500,
            Guardar = _ => true,
            ConhecidaConfirmada = conhecida,
            Auditar = aud.Add,
        };

    private static DesfechoTef Cobrar(ProvedorPGWebLib p)
        => p.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(10), null, 1, null, CancellationToken.None).GetAwaiter().GetResult();

    public static void Rodar(Action<bool, string> checar)
    {
        checar(PW.PWCNF_CNF_MANU_AUT == 12833 && PW.PWCNF_REV_MANU_AUT == 12849, "os dois códigos manuais da spec (0x3221 e 0x3231)");

        // ── passo 36: pendente que este caixa nunca viu ───────────────────────
        {
            var f = new FakePGWebLib { PendenciaNoInit = new FakePGWebLib.Pendencia("999", "LOC999", "700999", "VM1", "C6 PAY") };
            var aud = new List<string>();
            var p = Provedor(f, conhecida: null, aud);
            var d = Cobrar(p);
            checar(f.Confirmadas.Count >= 1 && f.Confirmadas[0] == (PW.PWCNF_REV_MANU_AUT, "999"),
                "passo 36: pendente desconhecida antes da venda -> PWCNF_REV_MANU_AUT (12849), não o de queda de energia: "
                + string.Join(",", f.Confirmadas.Select(c => c.Resultado + "/" + c.ReqNum)));
            checar(!f.Confirmadas.Any(c => c.Resultado == PW.PWCNF_REV_PWR_AUT), "o 536881 não sai fora do religamento");
            checar(aud.Any(a => a.Contains("REV manual") && a.Contains("12849") && a.Contains("999")),
                "a auditoria diz que foi o desfazimento manual e de qual REQNUM");
            checar(d.Pago, "a venda nova segue normal depois de resolver a pendência");
        }

        // ── passo 34: pendente que o caixa conhece como paga ──────────────────
        {
            var f = new FakePGWebLib { PendenciaNoInit = new FakePGWebLib.Pendencia("777", "LOC777", "700777", "VM1", "C6 PAY") };
            var aud = new List<string>();
            var p = Provedor(f, conhecida: r => r == "777", aud);
            var d = Cobrar(p);
            checar(f.Confirmadas.Count >= 1 && f.Confirmadas[0] == (PW.PWCNF_CNF_MANU_AUT, "777"),
                "passo 34: pendente conhecida antes da venda -> PWCNF_CNF_MANU_AUT (12833), não o automático: "
                + string.Join(",", f.Confirmadas.Select(c => c.Resultado + "/" + c.ReqNum)));
            checar(aud.Any(a => a.Contains("CNF manual") && a.Contains("12833") && a.Contains("777")),
                "a auditoria diz que foi a confirmação manual e de qual REQNUM");
            checar(d.Pago, "e a venda nova segue normal");
        }

        // ── NA HORA DA RECUSA: o roteiro manda resolver "com os dados recebidos". No
        //    teste do dono em 10/09 a resolução só saiu na venda seguinte, 52 min depois.
        //    Agora sai dentro da própria recusa, sem recibo, e a venda segue recusada. ──
        {
            var f = new FakePGWebLib { RecusarComPendencia = new FakePGWebLib.Pendencia("888", "LOC888", "700888", "VM1", "C6 PAY") };
            var aud = new List<string>();
            var p = Provedor(f, conhecida: r => r == "888", aud);
            var d = Cobrar(p);
            checar(!d.Pago && d.Situacao == SituacaoTef.Recusado, "a venda negada com TRANSACAO PENDENTE segue recusada: " + d.Motivo);
            checar(f.Confirmadas.Count == 1 && f.Confirmadas[0] == (PW.PWCNF_CNF_MANU_AUT, "888"),
                "passo 34 NA HORA: a pendente conhecida é confirmada com 12833 dentro da própria recusa: "
                + string.Join(",", f.Confirmadas.Select(c => c.Resultado + "/" + c.ReqNum)));
            checar(aud.Any(a => a.Contains("na recusa do host") && a.Contains("12833") && a.Contains("888")),
                "a auditoria diz que foi na recusa, manual, e de qual REQNUM");
            var d2 = Cobrar(p);
            checar(d2.Pago && f.Confirmadas.Count == 2 && f.Confirmadas[1].Resultado == PW.PWCNF_CNF_AUTO,
                "a venda seguinte não repete a resolução (a pendência já foi) e confirma a si mesma com 289");
        }
        {
            var f = new FakePGWebLib { RecusarComPendencia = new FakePGWebLib.Pendencia("889", "LOC889", "700889", "VM1", "C6 PAY") };
            var aud = new List<string>();
            var p = Provedor(f, conhecida: null, aud);
            var d = Cobrar(p);
            checar(!d.Pago && d.Situacao == SituacaoTef.Recusado && f.Confirmadas.Count == 1 && f.Confirmadas[0] == (PW.PWCNF_REV_MANU_AUT, "889"),
                "passo 36 NA HORA: a pendente desconhecida é desfeita com 12849 dentro da própria recusa: "
                + string.Join(",", f.Confirmadas.Select(c => c.Resultado + "/" + c.ReqNum)));
            checar(aud.Any(a => a.Contains("na recusa do host") && a.Contains("12849") && a.Contains("889")),
                "a auditoria diz que foi na recusa, manual, e de qual REQNUM");
        }

        // ── a venda comum, sem pendência, continua com o automático (289) ─────
        {
            var f = new FakePGWebLib();
            var aud = new List<string>();
            var p = Provedor(f, conhecida: null, aud);
            var d = Cobrar(p);
            checar(d.Pago && f.Confirmadas.Count == 1 && f.Confirmadas[0].Resultado == PW.PWCNF_CNF_AUTO,
                "sem pendência a confirmação da própria venda segue automática (289)");
        }
    }
}
