using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// RECUSAS DO HOST EM PORTUGUÊS (14/09/2026, loja Castelo).
///
/// O host devolveu "[NA 0201] 03 ESTABELECIMENTO INVALIDO" e, no Pix, "MODALIDADE DE PAGAMENTO
/// INVALIDA". O operador leu isso cru e não havia o que fazer com a frase. A tela passa a dizer o
/// que aconteceu e o que fazer; o texto original continua em `Motivo` (banco) e na auditoria.
/// </summary>
public static class TestesRecusasDoHost
{
    public static void Rodar(Action<bool, string> checar)
    {
        Tabela(checar);
        Desfecho(checar);
        Provedor(checar);
        Telas(checar);
    }

    private static void Tabela(Action<bool, string> checar)
    {
        foreach (var bruto in new[] { "[NA 0201] 03 ESTABELECIMENTO INVALIDO", "NA 0201 03 ESTABELECIMENTO INVÁLIDO", "[na 0201] 03 estabelecimento invalido" })
        {
            var r = RecusasDoHost.Traduzir(bruto);
            checar(r is not null && r.Frase == "A adquirente não reconhece este estabelecimento."
                   && r.OQueFazer == "Peça à PayGo para ativar o cadastro do ponto de captura.",
                $"'{bruto}': a adquirente não reconhece o estabelecimento, e o que fazer ({r?.ParaTela ?? "sem tradução"})");
        }

        var a110 = RecusasDoHost.Traduzir("[NA A110] TIPO PONTO DE CAPTURA INCORRETO");
        checar(a110 is not null && a110.OQueFazer.Contains("automação", StringComparison.Ordinal),
            "A110: ponto de captura do tipo errado, pedir à PayGo um do tipo automação: " + (a110?.ParaTela ?? "sem tradução"));

        var a116Fixa = RecusasDoHost.Traduzir("[NA A116] SERVICO NAO HABILITADO", TipoTef.Credito, redeFixada: true);
        checar(a116Fixa is not null && a116Fixa.OQueFazer.Contains("automático", StringComparison.Ordinal),
            "A116 com rede fixada: sugere a rede do cartão em automático: " + (a116Fixa?.ParaTela ?? "sem tradução"));
        var a116 = RecusasDoHost.Traduzir("[NA A116] SERVICO NAO HABILITADO", TipoTef.Credito, redeFixada: false);
        checar(a116 is not null && a116.OQueFazer.Contains("PayGo", StringComparison.Ordinal),
            "A116 no automático: o que falta é habilitar na PayGo: " + (a116?.ParaTela ?? "sem tradução"));

        var pixFixo = RecusasDoHost.Traduzir("MODALIDADE DE PAGAMENTO INVALIDA", TipoTef.Pix, redeFixada: true);
        checar(pixFixo is not null && pixFixo.OQueFazer == "Na Configuração, deixe a Rede do Pix em automático e tente de novo.",
            "Pix com rede fixada e modalidade inválida: sugere o automático (" + (pixFixo?.ParaTela ?? "sem tradução") + ")");
        var cartaoFixo = RecusasDoHost.Traduzir("[NA 0057] MODALIDADE DE PAGAMENTO INVALIDA", TipoTef.Debito, redeFixada: true);
        checar(cartaoFixo is not null && cartaoFixo.OQueFazer.Contains("rede do cartão em automático", StringComparison.Ordinal),
            "cartão com rede fixada e modalidade inválida: sugere a rede do cartão em automático: " + (cartaoFixo?.ParaTela ?? "sem tradução"));
        var pixAuto = RecusasDoHost.Traduzir("MODALIDADE DE PAGAMENTO INVALIDA", TipoTef.Pix, redeFixada: false);
        checar(pixAuto is not null && !pixAuto.OQueFazer.Contains("automático", StringComparison.Ordinal) && pixAuto.OQueFazer.Contains("PayGo", StringComparison.Ordinal),
            "Pix já no automático e modalidade inválida: não manda pôr no automático de novo, manda à PayGo: " + (pixAuto?.ParaTela ?? "sem tradução"));

        foreach (var outra in new[] { "TRANSACAO NAO AUTORIZADA", "SALDO INSUFICIENTE", "", null })
            checar(RecusasDoHost.Traduzir(outra) is null,
                $"'{outra ?? "null"}' não é recusa de cadastro: a frase da rede segue como veio");

        var todas = new[] { RecusasDoHost.Traduzir("[NA 0201] 03 ESTABELECIMENTO INVALIDO"), a110, a116Fixa, a116, pixFixo, cartaoFixo, pixAuto };
        checar(todas.All(t => t is not null && t.Frase.EndsWith('.') && t.OQueFazer.EndsWith('.') && t.OQueFazer.Length > 0),
            "toda tradução tem o que aconteceu e o que fazer, em frases inteiras");
        checar(todas.All(t => t is not null && !t.ParaTela.Contains('—') && !t.ParaTela.Contains("[NA", StringComparison.Ordinal)
                              && !t.ParaTela.Contains("0201", StringComparison.Ordinal)),
            "nenhuma tradução leva travessão ou código técnico para a tela");
    }

    private static void Desfecho(Action<bool, string> checar)
    {
        var d = new DesfechoTef(SituacaoTef.Recusado, null, "pgweb-1", null, "[NA 0201] 03 ESTABELECIMENTO INVALIDO", false) { Tipo = TipoTef.Credito };
        checar(d.MensagemParaTela == "A adquirente não reconhece este estabelecimento. Peça à PayGo para ativar o cadastro do ponto de captura.",
            "a tela de pagamento lê a recusa traduzida: " + d.MensagemParaTela);
        checar(d.Motivo == "[NA 0201] 03 ESTABELECIMENTO INVALIDO", "e o Motivo (banco e auditoria) guarda o texto original do host");

        var pix = new DesfechoTef(SituacaoTef.Recusado, null, "pgweb-2", null, "MODALIDADE DE PAGAMENTO INVALIDA", false) { Tipo = TipoTef.Pix, RedeFixada = true };
        checar(pix.MensagemParaTela.Contains("Rede do Pix em automático", StringComparison.Ordinal),
            "Pix recusado com rede fixada: a tela sugere o automático: " + pix.MensagemParaTela);

        var comum = new DesfechoTef(SituacaoTef.Recusado, null, "pgweb-3", null, "TRANSACAO NAO AUTORIZADA", false);
        checar(comum.MensagemParaTela == "TRANSACAO NAO AUTORIZADA", "recusa comum continua com a frase da rede");
        var pago = new DesfechoTef(SituacaoTef.Pago, null, "pgweb-4", null, "TRANSACAO APROVADA", false);
        checar(pago.MensagemParaTela == "TRANSACAO APROVADA", "venda aprovada não muda");
    }

    private static void Provedor(Action<bool, string> checar)
    {
        var pasta = TestesPGWebLib.PastaTeste;
        Directory.CreateDirectory(pasta);

        ProvedorPGWebLib Montar(FakePGWebLib f, OpcoesPGWebLib op) => new(f, pasta, op)
        {
            IntervaloPollMs = 5, TempoMaxExecMs = 2000, TempoMaxCapturaMs = 2000, TempoPerguntaMs = 500,
            Guardar = _ => true,
            Perguntar = (g, _) => Task.FromResult(RespostaDaTela.Menu(g, 0)),
        };

        {
            var f = new FakePGWebLib { ComSenha = false, PedirRemocao = false, MensagemRecusa = "MODALIDADE DE PAGAMENTO INVALIDA" };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Recusar);
            var p = Montar(f, new OpcoesPGWebLib("Pdv.AmericanDay", "1.0.10", "MMTech", RedePix: "PIX ITAU", Ambiente: PW.ENVRMNT_PROD));
            var d = p.CobrarAsync(TipoTef.Pix, Dinheiro.DeReais(10m), null, 1, null, CancellationToken.None).GetAwaiter().GetResult();
            checar(!d.Pago && d.Tipo == TipoTef.Pix && d.RedeFixada,
                $"provedor: Pix recusado com a Rede do Pix gravada sai marcado como Pix e rede fixada ({d.Situacao}, tipo={d.Tipo}, fixada={d.RedeFixada})");
            checar(d.Motivo == "MODALIDADE DE PAGAMENTO INVALIDA" && d.MensagemParaTela.Contains("Rede do Pix em automático", StringComparison.Ordinal),
                "provedor: o original fica no Motivo e a tela sugere o automático: " + d.MensagemParaTela);
        }
        {
            var f = new FakePGWebLib { ComSenha = false, PedirRemocao = false, MensagemRecusa = "[NA 0201] 03 ESTABELECIMENTO INVALIDO" };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Recusar);
            var p = Montar(f, new OpcoesPGWebLib("Pdv.AmericanDay", "1.0.10", "MMTech", Ambiente: PW.ENVRMNT_PROD));
            var d = p.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(10m), null, 1, null, CancellationToken.None).GetAwaiter().GetResult();
            checar(!d.Pago && d.Tipo == TipoTef.Credito && !d.RedeFixada,
                $"provedor: crédito no automático recusado sai sem rede fixada ({d.Situacao}, fixada={d.RedeFixada})");
            checar(d.MensagemParaTela.StartsWith("A adquirente não reconhece este estabelecimento.", StringComparison.Ordinal),
                "provedor: a tela explica o NA 0201: " + d.MensagemParaTela);
        }
    }

    private static void Telas(Action<bool, string> checar)
    {
        var pagamento = Fonte("Telas", "Pagamento.xaml.cs") ?? "";
        checar(pagamento.Contains("\"tef_recusa_host\"", StringComparison.Ordinal) && pagamento.Contains("RecusasDoHost.Traduzir(", StringComparison.Ordinal),
            "a tela de pagamento audita a recusa traduzida com o texto original do host");
        var config = Fonte("Telas", "Configuracao.xaml.cs") ?? "";
        checar(config.Contains("RecusasDoHost.ParaTela(di.Motivo", StringComparison.Ordinal),
            "a instalação do ponto de captura também traduz a recusa do host");
    }

    private static string? Fonte(params string[] partes)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var alvo = Path.Combine(new[] { d.FullName }.Concat(partes).ToArray());
            if (File.Exists(alvo)) return File.ReadAllText(alvo);
        }
        return null;
    }
}
