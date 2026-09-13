using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A LINHA DA PROMOÇÃO NA VITRINE (12/09/2026, pedido do dono).
///
/// Na promoção "donut do dia" o caixa mostrava "Não vale agora" e, embaixo, a agenda da
/// semana inteira: "qua: Donut Brigadeiro Gourmet · sex: Donut Churros · seg: Donut Homer,
/// Donut Ninho Com Nutella · qui: Donut Ovomaltine · ter: Donut Redvelvet". O dono pediu
/// direto: hoje não tem, e pronto.
///
/// A regra é pura (Promocoes.LinhaDaPromocao); que a agenda saiu da tela é travado no fonte,
/// senão ela volta na próxima mexida e ninguém percebe.
/// </summary>
public static class TestesLinhaPromocao
{
    public static void Rodar(Action<bool, string> checar)
    {
        var vazio = Promocoes.LinhaDaPromocao(Array.Empty<string>());
        // 13/09/2026: "é só falar sem produto ativo para promoção data de hoje ou algo mais simples"
        checar(vazio == "Sem produto na promoção hoje.", $"sem nada valendo agora, a linha é uma frase só (viu: {vazio})");
        checar(!vazio.Contains(':') || vazio.IndexOf(':') == vazio.Length - 1 || vazio.Count(c => c == ':') == 0,
            "a frase de hoje-não-tem não vira lista com dois pontos");
        checar(vazio.Length <= 40, $"cabe numa linha do cabeçalho da seção ({vazio.Length} caracteres)");
        checar(!vazio.Contains('—') && !vazio.Contains('–'), "sem travessão");

        checar(Promocoes.LinhaDaPromocao(new[] { "qui" }) == "Vale hoje: qui",
            "valendo, a linha diz quando vale");
        checar(Promocoes.LinhaDaPromocao(new[] { "qui · 18:00–20:00" }) == "Vale hoje: qui · 18:00–20:00",
            "o horário da regra vai junto (é o que responde 'posso vender agora?')");
        checar(Promocoes.LinhaDaPromocao(new[] { "qui", "qui", "sex" }) == "Vale hoje: qui  ·  sex",
            "regra repetida aparece uma vez só");
        checar(Promocoes.LinhaDaPromocao(new[] { "", "  ", "qui" }) == "Vale hoje: qui",
            "regra em branco não vira separador solto");
        checar(Promocoes.LinhaDaPromocao(new[] { "", "  " }) == "Sem produto na promoção hoje.",
            "só regras em branco valem o mesmo que nada valendo");

        var venda = Fonte(Path.Combine("Telas", "Venda.xaml.cs")) ?? "";
        checar(venda.Contains("Nucleo.Promocoes.LinhaDaPromocao("),
            "a tela usa a regra pura, não monta o texto por conta própria");
        checar(!venda.Contains("Não vale agora"), "o texto antigo saiu da tela");
        checar(!venda.Contains("linhaOutros") && !venda.Contains("var porDia"),
            "a agenda da semana embaixo do nome da promoção saiu de vez");
    }

    private static string? Fonte(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj")))
            {
                var c = Path.Combine(dir.FullName, relativo);
                return File.Exists(c) ? File.ReadAllText(c) : null;
            }
        return null;
    }
}
