using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A REDE DO PIX DE ACORDO COM ESTE TERMINAL (14/09/2026, loja Castelo).
///
/// O Pix da Castelo voltou "MODALIDADE DE PAGAMENTO INVALIDA" com a Rede do Pix = "PIX ITAU",
/// copiada da Savassi. O cartão já preferia as redes que o terminal ofereceu no menu de rede; o
/// Pix não. Aqui: as redes do menu numa cobrança Pix são guardadas à parte, aparecem primeiro na
/// lista do Pix, e o automático continua sendo o padrão de instalação nova.
/// </summary>
public static class TestesRedePixDoTerminal
{
    public static void Rodar(Action<bool, string> checar)
    {
        Listas(checar);
        Guardar(checar);
        Provedor(checar);
        Fiacao(checar);
    }

    private static void Listas(Action<bool, string> checar)
    {
        var sempre = RedesPayGo.OpcoesPix();
        var comVistas = RedesPayGo.OpcoesPix(null, new[] { "PIX C6 BANK" });
        checar(comVistas.Count > 1 && comVistas[0].Automatica,
            "pix: o automático continua sendo a primeira opção");
        checar(comVistas.Count > 1 && comVistas[1].Valor == "PIX C6 BANK" && comVistas[1].Rotulo.Contains("este terminal oferece", StringComparison.Ordinal),
            "pix: a rede que ESTE terminal ofereceu vem logo depois, dita como tal: " + (comVistas.Count > 1 ? comVistas[1].Rotulo : "-"));
        checar(comVistas.Count == sempre.Count, $"pix: sem duplicar a rede que já estava na lista ({comVistas.Count} x {sempre.Count})");
        checar(RedesPayGo.Indice(comVistas, null) == 0 && comVistas[RedesPayGo.Indice(comVistas, "")].Automatica,
            "pix: instalação nova (nada gravado) abre no automático, mesmo com redes vistas");

        var inedita = RedesPayGo.OpcoesPix(null, new[] { "PIX BANCO NOVO" });
        checar(inedita.Count > 1 && inedita[1].Valor == "PIX BANCO NOVO" && inedita[1].Conhecida,
            "pix: rede do terminal que a lista não conhece entra, sem aviso de config estranha");

        var castelo = RedesPayGo.OpcoesPix("PIX ITAU", new[] { "PIX C6 BANK" });
        checar(castelo[RedesPayGo.Indice(castelo, "PIX ITAU")].Valor == "PIX ITAU",
            "pix: a rede gravada (PIX ITAU) continua à vista, para o dono ver e trocar");

        checar(RedesPayGo.OpcoesPix(null, Array.Empty<string>()).Count == sempre.Count,
            "pix: terminal que nunca cobrou Pix fica com a lista de sempre");
        checar(!RedesPayGo.OpcoesPix(null, new[] { "CIELO" }).Take(2).Any(o => o.Valor == "CIELO"),
            "pix: rede de cartão que apareceu no menu não sobe para a lista do Pix");

        // O cartão: nada gravado abre no automático mesmo quando o terminal já mostrou redes.
        var cartao = RedesPayGo.OpcoesCartao(null, new[] { "C6 PAY", "REDE" });
        checar(cartao[RedesPayGo.Indice(cartao, null)].Automatica,
            "cartão: nada gravado abre no automático, e não na primeira rede vista (Salvar fixaria a rede sem o dono escolher)");
        checar(!RedesPayGo.OpcoesCartao(null, new[] { "PIX C6 BANK", "REDE" }).Take(2).Any(o => o.Valor.StartsWith("PIX ", StringComparison.Ordinal)),
            "cartão: rede de Pix que apareceu no menu não sobe para a lista do cartão");
    }

    private static void Guardar(Action<bool, string> checar)
    {
        checar(RedesPayGo.ChaveVistasPix != RedesPayGo.ChaveVistas, "as redes do Pix e as do cartão ficam em chaves separadas");
        checar(RedesPayGo.Acrescentar(null, new[] { "PIX C6 BANK" }) == "PIX C6 BANK", "guardar: a primeira rede vista");
        checar(RedesPayGo.Acrescentar("PIX C6 BANK", new[] { "PIX ITAU", "PIX C6 BANK" }) == "PIX C6 BANK|PIX ITAU",
            "guardar: só acrescenta, na ordem em que apareceram");
        checar(RedesPayGo.Acrescentar("PIX C6 BANK|PIX ITAU", new[] { "PIX ITAU" }) is null,
            "guardar: menu curto não apaga o que já se sabia e não regrava nada");
        checar(RedesPayGo.Acrescentar("PIX C6 BANK", new[] { " ", "" }) is null, "guardar: nome vazio não entra");
    }

    private static void Provedor(Action<bool, string> checar)
    {
        var pasta = TestesPGWebLib.PastaTeste;
        Directory.CreateDirectory(pasta);

        (DesfechoTef D, List<string>? Pix, List<string>? Cartao, int Perguntas) Cobrar(TipoTef tipo, string[] menu)
        {
            var f = new FakePGWebLib { RedesDoMenu = menu, ComSenha = false, PedirRemocao = false };
            List<string>? pix = null, cartao = null;
            var perguntas = 0;
            var p = new ProvedorPGWebLib(f, pasta, new OpcoesPGWebLib("Pdv.AmericanDay", "1.0.10", "MMTech", Ambiente: PW.ENVRMNT_PROD))
            {
                IntervaloPollMs = 5, TempoMaxExecMs = 2000, TempoMaxCapturaMs = 2000, TempoPerguntaMs = 500,
                Guardar = _ => true,
                Perguntar = (g, _) => { perguntas++; return Task.FromResult(RespostaDaTela.Menu(g, 0)); },
                RedesDoTerminal = r => cartao = r.ToList(),
                RedesPixDoTerminal = r => pix = r.ToList(),
            };
            var d = p.CobrarAsync(tipo, Dinheiro.DeReais(10m), null, 1, null, CancellationToken.None).GetAwaiter().GetResult();
            return (d, pix, cartao, perguntas);
        }

        var dois = Cobrar(TipoTef.Pix, new[] { "PIX C6 BANK", "PIX ITAU" });
        checar(dois.D.Pago && dois.Pix is not null && string.Join("|", dois.Pix) == "PIX C6 BANK|PIX ITAU",
            "provedor: numa cobrança Pix no automático, as redes do menu vão para a lista do Pix: " + (dois.Pix is null ? "nada" : string.Join("|", dois.Pix)));
        checar(dois.Cartao is null, "provedor: e nada disso vai para a lista do cartão");

        var uma = Cobrar(TipoTef.Pix, new[] { "PIX C6 BANK" });
        checar(uma.D.Pago && uma.Perguntas == 0 && uma.Pix is not null && string.Join("|", uma.Pix) == "PIX C6 BANK",
            "provedor: em produção a rede única é respondida sozinha e mesmo assim fica guardada: " + (uma.Pix is null ? "nada" : string.Join("|", uma.Pix)));

        var cartao = Cobrar(TipoTef.Credito, new[] { "REDE", "CIELO" });
        checar(cartao.D.Pago && cartao.Cartao is not null && cartao.Pix is null,
            "provedor: cobrança de cartão continua guardando na lista do cartão");
    }

    private static void Fiacao(Action<bool, string> checar)
    {
        var servicos = Fonte("Servicos.cs") ?? "";
        checar(servicos.Contains("RedesPixDoTerminal = redes => RedesPayGo.GuardarVistas(redes, pix: true)", StringComparison.Ordinal),
            "o caixa guarda as redes do Pix que o terminal ofereceu");
        var config = Fonte("Telas", "Configuracao.xaml.cs") ?? "";
        checar(config.Contains("RedesPayGo.VistasPix(", StringComparison.Ordinal),
            "a Configuração mostra primeiro as redes do Pix deste terminal");
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
