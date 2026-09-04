using System.Text.RegularExpressions;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// Os dois MENUS da barra de cima da tela de venda (04/09/2026, pedido do dono:
/// "juntar o menu impressora com cancelar venda, e o sair com o fechar caixa").
///
/// A REGRA (quais itens, em que ordem, qual chave de ação, altura mínima do alvo)
/// vive em Pdv.Nucleo/MenuBarra e é provada pelo VALOR. O que só existe no WPF (o
/// véu que fecha ao tocar fora e com Esc, o reencaminhamento de cada chave para o
/// handler que já existia, o topo mais baixo) é travado no FONTE da tela, como as
/// zonas de toque do KDS (TestesDetalhePedido) e os CV-* do estorno.
/// </summary>
public static class TestesMenuBarra
{
    public static void Rodar(Action<bool, string> checar)
    {
        // ── 1. Cancelar / Imprimir: exatamente estes, nesta ordem ───────────
        var comTef = MenuBarra.CancelarImprimir(temTef: true);
        checar(comTef.Select(i => i.Rotulo).SequenceEqual(
                   new[] { "Cancelar venda", "Estornar", "Reimpressão", "Configuração da impressora" }),
            "Cancelar / Imprimir lista: Cancelar venda, Estornar, Reimpressão, Configuração da impressora (nesta ordem)");
        checar(comTef.Select(i => i.Acao).SequenceEqual(
                   new[] { MenuBarra.Cancelar, MenuBarra.Estornar, MenuBarra.Reimprimir, MenuBarra.Impressora }),
            "cada item do Cancelar / Imprimir aponta para a sua chave de ação");

        // Sem maquininha integrada estorno e reimpressão saem (regra que já valia no
        // menu antigo: maquininha avulsa estorna na mão); a ordem dos que ficam não muda.
        var semTef = MenuBarra.CancelarImprimir(temTef: false);
        checar(semTef.Select(i => i.Acao).SequenceEqual(new[] { MenuBarra.Cancelar, MenuBarra.Impressora }),
            "sem TEF: Cancelar venda e Configuração da impressora, nessa ordem (estorno e reimpressão só com maquininha integrada)");

        // ── 2. Fechar / Sair ────────────────────────────────────────────────
        var fecharSair = MenuBarra.FecharSair();
        checar(fecharSair.Select(i => i.Rotulo).SequenceEqual(new[] { "Fechamento de caixa", "Sair" }),
            "Fechar / Sair lista: Fechamento de caixa, Sair (nesta ordem)");
        checar(fecharSair.Select(i => i.Acao).SequenceEqual(new[] { MenuBarra.FecharCaixa, MenuBarra.Sair }),
            "cada item do Fechar / Sair aponta para a sua chave de ação");

        // ── 3. alvo de toque e texto ────────────────────────────────────────
        checar(MenuBarra.AlturaItem >= 44, $"item do menu tem no mínimo 44 px de altura (tem {MenuBarra.AlturaItem})");
        var todos = comTef.Concat(fecharSair).ToList();
        checar(todos.Select(i => i.Acao).Distinct().Count() == todos.Count,
            "nenhuma chave de ação se repete entre os dois menus");
        checar(todos.All(i => i.Rotulo.Length is > 0 and <= 28),
            "rótulos curtos (até 28 caracteres): cabem numa linha do cartão");
        var textos = todos.Select(i => i.Rotulo).Append(MenuBarra.RotuloCancelarImprimir).Append(MenuBarra.RotuloFecharSair).ToList();
        checar(textos.All(t => !t.Contains('—') && !t.Contains('–')),
            "sem travessão nem meia-risca em texto de tela (o dono lê como texto de IA)");
        checar(textos.All(t => !Regex.IsMatch(t, @"\(\w+\)")), "sem plural automático entre parênteses");
        checar(todos.All(i => i.Icone.Length > 0), "todo item tem ícone (a barra já usa ícones)");
        checar(MenuBarra.RotuloCancelarImprimir == "Cancelar / Imprimir" && MenuBarra.RotuloFecharSair == "Fechar / Sair",
            "os dois botões da barra dizem as duas ações que moram dentro: Cancelar / Imprimir e Fechar / Sair");

        // ── 4. FONTE da tela: o que só existe no WPF ────────────────────────
        var xaml = Fonte(Path.Combine("Telas", "Venda.xaml"));
        var cs = Fonte(Path.Combine("Telas", "Venda.xaml.cs"));
        checar(xaml is not null && cs is not null, "achei Telas/Venda.xaml e Telas/Venda.xaml.cs");
        if (xaml is null || cs is null) return;

        // (a) a barra: dois botões de menu no lugar dos quatro
        checar(xaml.Contains("Click=\"MenuCancelamento\"", StringComparison.Ordinal)
               && xaml.Contains("Click=\"MenuFecharSair\"", StringComparison.Ordinal),
            "a barra tem os dois botões de menu (MenuCancelamento e MenuFecharSair)");
        checar(xaml.Contains($"Text=\"{MenuBarra.RotuloCancelarImprimir}\"", StringComparison.Ordinal)
               && xaml.Contains($"Text=\"{MenuBarra.RotuloFecharSair}\"", StringComparison.Ordinal),
            "os rótulos dos botões da barra são os de MenuBarra");
        checar(!xaml.Contains("Click=\"FecharCaixa\"", StringComparison.Ordinal)
               && !xaml.Contains("Click=\"TrocarImpressora\"", StringComparison.Ordinal)
               && !xaml.Contains("Click=\"Sair\"", StringComparison.Ordinal),
            "Fechar caixa, Impressora e Sair não são mais botões soltos na barra");
        var ordemBarra = Regex.Matches(xaml, @"Click=""(MenuCancelamento|MenuFecharSair|Sangria|Suprimento)""")
            .Select(m => m.Groups[1].Value).ToList();
        checar(ordemBarra.SequenceEqual(new[] { "Sangria", "Suprimento", "MenuCancelamento", "MenuFecharSair" }),
            "na barra: Sangria, Suprimento, Cancelar / Imprimir, Fechar / Sair (o Fechar / Sair fecha a fila, como o Sair fechava)");

        // (b) os selos continuam, e Sangria/Suprimento também (o dono não pediu para mexer)
        foreach (var selo in new[] { "BadgeKds", "BadgeChat", "ChipPendencia", "ChipVersaoNova" })
            checar(xaml.Contains($"x:Name=\"{selo}\"", StringComparison.Ordinal), $"o selo {selo} continua na barra");
        checar(xaml.Contains("Click=\"Sangria\"", StringComparison.Ordinal) && xaml.Contains("Click=\"Suprimento\"", StringComparison.Ordinal),
            "Sangria e Suprimento continuam como botões da barra");

        // (c) categorias à ESQUERDA (regra do dono, 03/09) e a lógica de tela estreita intacta
        var colunas = Regex.Matches(xaml, @"<ColumnDefinition x:Name=""(Col\w+)""").Select(m => m.Groups[1].Value).ToList();
        checar(colunas.Count == 3 && colunas[0] == "ColCategorias" && colunas[1] == "ColProdutos" && colunas[2] == "ColComanda",
            "coluna de categorias continua à ESQUERDA, produtos no meio, comanda à direita");
        checar(cs.Contains("if (estreita) { ColComanda.MinWidth = 236; ColComanda.MaxWidth = 250; }", StringComparison.Ordinal),
            "em tela estreita quem encolhe continua sendo a coluna da DIREITA (comanda 236..250)");
        checar(cs.Contains("private const double LarguraEstreita = 1500;", StringComparison.Ordinal),
            "o limiar de tela estreita (_estreita) não mudou");

        // (d) o véu: toque fora fecha, Esc fecha, um menu por vez, sem submenu
        checar(xaml.Contains("x:Name=\"VeuMenu\"", StringComparison.Ordinal)
               && xaml.Contains("MouseLeftButtonDown=\"ToqueForaDoMenu\"", StringComparison.Ordinal),
            "o menu é um véu dentro da tela e fecha ao tocar fora (ToqueForaDoMenu)");
        checar(cs.Contains("ToqueForaDoMenu(object sender, MouseButtonEventArgs e) => FecharMenu()", StringComparison.Ordinal),
            "tocar fora fecha sem escolher nada");
        var esc = Regex.Match(cs, @"PreviewKeyDown \+= .*?\};", RegexOptions.Singleline).Value;
        checar(esc.Contains("VeuMenu.Visibility == Visibility.Visible", StringComparison.Ordinal)
               && esc.Contains("Key.Escape", StringComparison.Ordinal)
               && esc.Contains("FecharMenu()", StringComparison.Ordinal),
            "Esc fecha o menu (PreviewKeyDown), e só quando ele está aberto");
        var abrir = Trecho(cs, "private Task<string?> AbrirMenu(", "private void FecharMenu(");
        checar(abrir.Length > 0, "achei AbrirMenu na tela de venda");
        checar(abrir.Contains("MinHeight = MenuBarra.AlturaItem", StringComparison.Ordinal),
            "cada item do véu usa a altura mínima de MenuBarra (alvo de dedo)");
        checar(abrir.Contains("FecharMenu();", StringComparison.Ordinal), "abrir um menu fecha o que estava aberto (um por vez)");
        checar(abrir.Contains("Resources[\"BotaoBase\"]", StringComparison.Ordinal),
            "os itens são BotaoBase (o mesmo botão de toque do resto do app)");
        checar(abrir.Contains("SombraDialogoOpacidade", StringComparison.Ordinal),
            "o cartão usa a sombra do Dialogo (mesmo desenho das outras janelas, não um terceiro estilo)");
        checar(!cs.Contains("new ContextMenu", StringComparison.Ordinal) && !cs.Contains("new Popup", StringComparison.Ordinal),
            "sem ContextMenu nem Popup do Windows: o menu é o véu da casa");

        // (e) item -> handler que JÁ existia (nada reimplementado)
        var menu = Trecho(cs, "private async void MenuCancelamento", "private static void GuardarPasso");
        checar(menu.Contains("AbrirMenu(BtnCancelar, MenuBarra.CancelarImprimir(temTef))", StringComparison.Ordinal),
            "Cancelar / Imprimir abre o véu com a lista de MenuBarra.CancelarImprimir");
        checar(Regex.IsMatch(menu, @"case MenuBarra\.Cancelar:\s*await CancelarVendaAsync\("),
            "Cancelar venda -> CancelarVendaAsync (o cancelamento que já existia)");
        checar(Regex.IsMatch(menu, @"case MenuBarra\.Estornar:.*?Servicos\.Operavel\(\).*?EstornarTefAsync\(dono, cli\)", RegexOptions.Singleline),
            "Estornar -> EstornarTefAsync (o estorno que já existia, com a maquininha conferida antes)");
        checar(Regex.IsMatch(menu, @"case MenuBarra\.Reimprimir:\s*await ReimprimirComprovanteAsync\("),
            "Reimpressão -> ReimprimirComprovanteAsync (a reimpressão que já existia)");
        checar(Regex.IsMatch(menu, @"acao == MenuBarra\.Impressora\) \{ await TrocarImpressoraAsync\(\)"),
            "Configuração da impressora -> TrocarImpressoraAsync (o antigo botão Impressora)");
        checar(Ordem(menu, "MenuBarra.Impressora", "Comanda aberta"),
            "trocar a impressora não exige comanda vazia (a bobina acaba no meio da venda)");
        checar(Ordem(menu, "Comanda aberta", "case MenuBarra.Cancelar"),
            "cancelar, estornar e reimprimir continuam exigindo comanda vazia");
        checar(Regex.Matches(menu, @"AbrirMenu\(").Count == 1, "sem submenu: o Cancelar / Imprimir abre UM véu");
        checar(!menu.Contains("EscolherOpcao(", StringComparison.Ordinal),
            "o menu da barra não usa mais a janela EscolherOpcao (moldura do Windows, não fechava ao tocar fora)");

        var fs = Trecho(cs, "private async void MenuFecharSair", "private TaskCompletionSource<string?>? _menuAberto");
        checar(fs.Contains("AbrirMenu(BtnFecharSair, MenuBarra.FecharSair())", StringComparison.Ordinal),
            "Fechar / Sair abre o véu com a lista de MenuBarra.FecharSair");
        checar(Regex.IsMatch(fs, @"case MenuBarra\.FecharCaixa:\s*FecharCaixa\(sender, e\)"),
            "Fechamento de caixa -> FecharCaixa (o pop-up de fechamento que já existia)");
        checar(Regex.IsMatch(fs, @"case MenuBarra\.Sair:\s*Sair\(sender, e\)"), "Sair -> Sair (o handler que já existia)");
        checar(Regex.Matches(fs, @"AbrirMenu\(").Count == 1, "sem submenu: o Fechar / Sair abre UM véu");

        // os handlers antigos continuam existindo, com as travas deles
        var fechar = Trecho(cs, "private void FecharCaixa(object sender, RoutedEventArgs e)", "private static void MostrarResultado");
        checar(fechar.Contains("TefEmAndamento(dono)", StringComparison.Ordinal) && fechar.Contains("Comanda aberta", StringComparison.Ordinal)
               && fechar.Contains("Caixa.Fechar(", StringComparison.Ordinal),
            "FecharCaixa continua o mesmo: trava de TEF, comanda aberta e Caixa.Fechar");
        var sair = Trecho(cs, "private void Sair(object sender, RoutedEventArgs e)", "// ── MENUS DA BARRA");
        checar(sair.Contains("TefEmAndamento(", StringComparison.Ordinal) && sair.Contains("Descartar e sair", StringComparison.Ordinal)
               && sair.Contains("Deslogou?.Invoke()", StringComparison.Ordinal),
            "Sair continua o mesmo: trava de TEF, confirmação de descarte e Deslogou");

        // (f) o topo mais baixo, sem alvo de toque abaixo de 40 px
        var cab = Regex.Match(xaml, @"<Border Grid\.Row=""0"" x:Name=""Cabecalho""[^>]*Padding=""(\d+),(\d+)""");
        checar(cab.Success && int.Parse(cab.Groups[2].Value) <= 6,
            $"padding vertical da barra de cima é de no máximo 6 px (tem {(cab.Success ? cab.Groups[2].Value : "?")})");
        var largura = Trecho(cs, "private void AplicarLargura(", "private void FaixaEsquerda(");
        checar(largura.Contains("b.MinHeight = 40;", StringComparison.Ordinal),
            "botão da barra em tela estreita tem 40 px de altura mínima (era 38)");
        checar(!Regex.IsMatch(largura, @"MinHeight = [0-3]\d;"), "nenhum botão da barra fica abaixo de 40 px");
        checar(largura.Contains("Cabecalho.Padding = estreita ? new Thickness(12, 5, 12, 5) : new Thickness(18, 6, 18, 6)", StringComparison.Ordinal),
            "AplicarLargura ajusta o padding do topo: 5 em tela estreita, 6 no resto");
        var estilos = Fonte("Estilos.xaml");
        var barra = estilos is null ? "" : Regex.Match(estilos, @"<Style x:Key=""BotaoBarra"".*?</Style>", RegexOptions.Singleline).Value;
        var minBarra = Regex.Match(barra, @"Property=""MinHeight"" Value=""(\d+)""").Groups[1].Value;
        checar(int.TryParse(minBarra, out var mb) && mb >= 40, $"BotaoBarra (Estilos.xaml) tem MinHeight >= 40 (tem '{minBarra}')");

        // (g) texto de tela do XAML da barra sem travessão
        var atributos = Regex.Matches(xaml, @"(?:Text|ToolTip|Content)=""([^""]*)""").Select(m => m.Groups[1].Value).ToList();
        checar(atributos.Count > 0 && atributos.All(t => !t.Contains('—') && !t.Contains('–')),
            "nenhum Text/ToolTip/Content do Venda.xaml tem travessão ou meia-risca");
    }

    private static string Trecho(string todo, string de, string ate)
    {
        var i = todo.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var f = todo.IndexOf(ate, i + de.Length, StringComparison.Ordinal);
        return f < 0 ? "" : todo[i..f];
    }

    private static bool Ordem(string corpo, string primeiro, string depois)
    {
        var a = corpo.IndexOf(primeiro, StringComparison.Ordinal);
        var b = corpo.IndexOf(depois, StringComparison.Ordinal);
        return a >= 0 && b > a;
    }

    /// <summary>Sobe do binário do teste até achar o arquivo pedido no repositório.</summary>
    private static string? Fonte(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidato = Path.Combine(dir.FullName, relativo);
            if (File.Exists(candidato)) return File.ReadAllText(candidato);
        }
        return null;
    }
}
