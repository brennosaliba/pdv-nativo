using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// BUSCA POR NÚMERO NO KDS (11/09/2026, pedido do dono: "barra de procura pra procurar um
/// pedido pelo número", no KDS do caixa e no da cozinha). A regra é pura (Nucleo.Kds.CasaBusca);
/// a caixa de texto do quadro é travada pelo fonte.
/// </summary>
public static class TestesBuscaKds
{
    public static void Rodar(Action<bool, string> checar)
    {
        checar(Kds.CasaBusca("4719", "4719") && Kds.CasaBusca("#4719", "4719") && Kds.CasaBusca("4719", "#47 19"),
            "só os dígitos contam: '#4719', '4719' e '47 19' são o mesmo pedido");
        checar(Kds.CasaBusca("4719", "47") && Kds.CasaBusca("1470", "47") && !Kds.CasaBusca("4719", "48"),
            "um pedaço do número acha (47 acha 4719 e 1470); 48 não acha 4719");
        checar(Kds.CasaBusca("4719", "") && Kds.CasaBusca("4719", null) && Kds.CasaBusca("4719", "  ") && Kds.CasaBusca("4719", "abc"),
            "busca vazia, nula, só espaço ou sem dígito = tudo aparece");
        checar(!Kds.CasaBusca(null, "1") && !Kds.CasaBusca("", "1"), "pedido sem número não casa com busca com dígito");

        Ticket T(string n) => new(n, "ifood", n, n, null, "[]", Kds.Recebido, DateTime.Now, null, null, null);
        var lista = new[] { T("4719"), T("8146"), T("1470") };
        checar(Kds.FiltrarPorNumero(lista, "47").Select(t => t.Numero).SequenceEqual(new[] { "4719", "1470" }),
            "FiltrarPorNumero mantém a ordem e devolve só os que casam");
        checar(Kds.FiltrarPorNumero(lista, "").Count() == 3, "sem busca, a lista inteira");

        var xaml = Fonte(Path.Combine("Telas", "Kds.xaml")) ?? "";
        var cs = Fonte(Path.Combine("Telas", "Kds.xaml.cs")) ?? "";
        checar(xaml.Contains("x:Name=\"TxtBusca\"") && xaml.Contains("TextChanged=\"BuscaMudou\"") && xaml.Contains("x:Name=\"BtnLimparBusca\""),
            "o quadro tem a caixa de busca com o X de limpar");
        // Recurso que so existe nos Resources da Venda (BotaoTexto) nao se alcanca do KDS: a
        // 1.0.4 caiu ao abrir o Delivery por isso. Todo StaticResource do KDS tem que ser global.
        var estilos = Fonte("Estilos.xaml") ?? "";
        var app = Fonte("App.xaml") ?? "";
        var recursos = System.Text.RegularExpressions.Regex.Matches(xaml, @"\{StaticResource (\w+)\}").Select(m => m.Groups[1].Value).Distinct().ToList();
        var faltando = recursos.Where(r => !estilos.Contains($"x:Key=\"{r}\"") && !app.Contains($"x:Key=\"{r}\"")).ToList();
        checar(recursos.Count > 0 && faltando.Count == 0, "todo StaticResource do Kds.xaml existe em Estilos.xaml ou App.xaml" + (faltando.Count == 0 ? "" : ": faltam " + string.Join(", ", faltando)));
        checar(cs.Contains("Nucleo.Kds.FiltrarPorNumero(todos, _busca)") && cs.Contains("private void BuscaMudou(") && cs.Contains("PedirTexto.AbrirTecladoVirtualSeTouch()"),
            "digitar filtra as três colunas pela regra do núcleo, e o toque no campo abre o teclado do Windows no caixa touch");
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
