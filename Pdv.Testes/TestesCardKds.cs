using System.Globalization;
using System.Text.RegularExpressions;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O TEXTO do card do quadro de preparo (Pdv.Nucleo/CardKds).
///
/// Nasceu de uma FOTO. O dono mandou um card real da cozinha, estreito, com tudo
/// quebrando linha, e pediu diferenciação entre item, subitem e o rodapé de espera.
/// A cor é assunto do tema (TestesTema mede); o que se prova aqui é o texto:
///
///   #8998  13 min
///   Cassia Nery Lucas Si...
///   1× Combo Box 4un
///       - 2x Donut Homer
///       - 1x Donut Morango c/ Ninho
///       - 1x Donut Ninho c/ Nutella
///       - 1x Donut Calabresa
///   1× Combo 1 Cookies - 4 unidades
///       - 2x Cookie Tradicional
///       - 2x Cookie Brigadeiro
///   AGUARDANDO O ENTREGADOR
///
/// Dois "x" diferentes no mesmo card e 14 caracteres repetindo o que já está
/// listado embaixo. A metade dos testes daqui é do lado do CORTE; a outra metade
/// é do lado do NÃO CORTE, que é o que importa: nome mutilado na cozinha custa
/// mais caro que nome comprido.
/// </summary>
public static class TestesCardKds
{
    // Os dois combos da foto, com os componentes exatamente como a foto mostrava.
    private static readonly string[] ComboBox =
    {
        "2x Donut Homer", "1x Donut Morango c/ Ninho",
        "1x Donut Ninho c/ Nutella", "1x Donut Calabresa",
    };
    private static readonly string[] ComboCookies =
    {
        "2x Cookie Tradicional", "2x Cookie Brigadeiro",
    };

    public static void Rodar(Action<bool, string> checar)
    {
        // ── quantidade do item ──────────────────────────────────────────────
        checar(CardKds.Quantidade(1000) == "1", "1000 milésimos vira '1'");
        checar(CardKds.Quantidade(2000) == "2", "2000 milésimos vira '2'");
        var sep = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        checar(CardKds.Quantidade(1500) == "1" + sep + "5",
            $"quantidade fracionada usa o separador da máquina: 1500 -> '1{sep}5'");
        checar(CardKds.Quantidade(500) == "0" + sep + "5", "meia unidade vira '0,5', não some");

        // ── marcador de quantidade: UM SÓ no card ───────────────────────────
        // Era o defeito nº 1 da foto: item com "×" e subitem com "x".
        var item = CardKds.ItemPrincipal(1000, "Donut Homer", null);
        checar(item.Qtd == "1×", "item principal marca a quantidade com ×");
        checar(item.Texto == "1× Donut Homer", "item simples sai inteiro: '1× Donut Homer'");

        var sub = CardKds.SubItem("2x Donut Homer");
        checar(sub.Qtd == "2×", "subitem também marca com × (vinha com x minúsculo do JSON)");
        checar(sub.Nome == "Donut Homer", "subitem separa nome da quantidade");
        checar(sub.Texto == "2× Donut Homer", "subitem inteiro: '2× Donut Homer'");
        checar(CardKds.SubItem("2 x Donut Homer").Texto == "2× Donut Homer",
            "espaço antes do x também normaliza");
        checar(CardKds.SubItem(CardKds.SubItem("2x Donut Homer").Texto).Texto == "2× Donut Homer",
            "normalizar de novo não muda nada (idempotente)");

        // grupo do combo: a quantidade sobe para a frente, o grupo fica no nome —
        // a lista só funciona como coluna se TODA linha começar por número
        var comGrupo = CardKds.SubItem("Clássicos: 2x Donut Ninho");
        checar(comGrupo.Qtd == "2×", "escolha com grupo mantém a quantidade na frente");
        checar(comGrupo.Nome == "Clássicos: Donut Ninho", "o grupo continua na linha, sem se perder");

        // escolha sem quantidade legível: não inventa "1×"
        var crua = CardKds.SubItem("Sem cebola");
        checar(crua.Qtd == "" && crua.Nome == "Sem cebola",
            "escolha sem número sai como veio, sem '1×' inventado");
        checar(CardKds.SubItem("3x4 Bolo").Texto == "3x4 Bolo",
            "'3x4' não é quantidade: sem espaço depois do x, nada é reescrito");
        checar(CardKds.SubItem(null).Texto == "" && CardKds.SubItem("  ").Texto == "",
            "escolha nula ou em branco não vira lixo na tela");

        // ── a cauda redundante: os dois combos da foto ──────────────────────
        // "Combo Box 4un" com os 4 sabores DA FOTO: eles somam CINCO donuts, não quatro.
        // O 4 do nome só casa com a CONTAGEM de linhas, e contagem não é prova de nada.
        // Cortar aqui apagaria a única pista de que uma caixa de 4 está saindo com 5.
        var box = CardKds.ItemPrincipal(1000, "Combo Box 4un", ComboBox);
        checar(box.Texto == "1× Combo Box 4un",
            $"cauda que DISCORDA do que está embaixo (4un x 5 donuts) fica na tela (saiu: {box.Texto})");

        // "Combo 1 Cookies - 4 unidades" com 2x + 2x embaixo: o 4 é a SOMA.
        var cookies = CardKds.ItemPrincipal(1000, "Combo 1 Cookies - 4 unidades", ComboCookies);
        checar(cookies.Texto == "1× Combo 1 Cookies",
            $"'Combo 1 Cookies - 4 unidades' com 2+2 embaixo vira '1× Combo 1 Cookies' (saiu: {cookies.Texto})");
        checar(CardKds.SemCaudaRedundante("Caixa Mista (6 unidades)",
                   new[] { "3x Donut Ninho", "3x Donut Nutella" }) == "Caixa Mista",
            "cauda entre parênteses também sai, com o parêntese junto");
        checar(CardKds.SemCaudaRedundante("Combo Duo 2 pçs",
                   new[] { "1x Donut", "1x Cookie" }) == "Combo Duo",
            "'pçs' conta como palavra de unidade");

        // ── e agora o que NÃO pode ser tocado ───────────────────────────────
        checar(CardKds.ItemPrincipal(1000, "Combo Box 4un", null).Texto == "1× Combo Box 4un",
            "sem componentes listados o número FICA: é a única medida de tamanho que sobra");
        checar(CardKds.SemCaudaRedundante("Caixa Donut 6un", Array.Empty<string>()) == "Caixa Donut 6un",
            "lista de componentes vazia também deixa o nome em paz");
        checar(CardKds.SemCaudaRedundante("Combo Família 12un",
                   new[] { "2x Donut Homer", "2x Donut Ninho", "2x Donut Nutella" }) == "Combo Família 12un",
            "12 não é a soma (6) nem a contagem (3): não está provado, não corta");
        checar(CardKds.SemCaudaRedundante("Combo Box 2un",
                   new[] { "Donut Homer", "Donut Ninho" }) == "Combo Box 2un",
            "componente sem quantidade legível derruba a prova inteira: nome intacto");
        checar(CardKds.SemCaudaRedundante("Kit 12 un Mini",
                   Enumerable.Repeat("1x Donut", 12).ToArray()) == "Kit 12 un Mini",
            "a contagem tem que estar no FIM do nome; no meio ela é parte do nome");
        checar(CardKds.SemCaudaRedundante("Combo 4",
                   new[] { "1x A", "1x B", "1x C", "1x D" }) == "Combo 4",
            "número solto no fim é linha de produto, não contagem: fica");
        checar(CardKds.SemCaudaRedundante("Combo 3 Unicórnios",
                   new[] { "1x A", "1x B", "1x C" }) == "Combo 3 Unicórnios",
            "'Unicórnios' não é 'uni': palavra que só começa igual não é unidade");
        checar(CardKds.SemCaudaRedundante("Kit 4un",
                   new[] { "1x A", "1x B", "1x C", "1x D" }) == "Kit 4un",
            "sobraria só 'Kit': curto demais para continuar sendo nome, então não corta");
        checar(CardKds.SemCaudaRedundante("Combo c/ 4 unidades",
                   new[] { "1x A", "1x B", "1x C", "1x D" }) == "Combo c/ 4 unidades",
            "'Combo c/' seria nome mutilado: a trava do último pedaço segura o corte");
        checar(CardKds.SemCaudaRedundante("Donut Homer", ComboBox) == "Donut Homer",
            "nome que não tem cauda nenhuma passa inteiro");
        checar(CardKds.SemCaudaRedundante("", ComboBox) == "" &&
               CardKds.SemCaudaRedundante("   ", ComboBox) == "",
            "nome vazio não quebra a pintura do card");

        // ── nada do que o cliente pediu some do card ────────────────────────
        // A regra do dono é explícita: o card CRESCE, não esconde. Aqui isso vira
        // asserção — cada componente do combo continua legível linha a linha.
        foreach (var esc in ComboBox)
        {
            var nome = esc[(esc.IndexOf(' ') + 1)..];
            checar(CardKds.SubItem(esc).Nome == nome,
                $"o sabor '{nome}' continua inteiro na linha do subitem");
        }
        checar(CardKds.QtdDaEscolha("1,5x Bolo") == 1.5m && CardKds.QtdDaEscolha("1.5x Bolo") == 1.5m,
            "quantidade fracionada da escolha lê com vírgula E com ponto");
        checar(CardKds.QtdDaEscolha("Bolo") is null, "escolha sem número devolve null, não zero");

        // ── o card da foto, do primeiro item ao último ──────────────────────
        var itens = new List<TicketItem>
        {
            new("Combo Box 4un", 1000, null, ComboBox),
            new("Combo 1 Cookies - 4 unidades", 1000, null, ComboCookies),
        };
        var linhas = new List<string>();
        foreach (var i in itens)
        {
            linhas.Add(CardKds.ItemPrincipal(i).Texto);
            foreach (var e in i.Escolhas!) linhas.Add(CardKds.SubItem(e).Texto);
        }
        var esperado = new[]
        {
            // O "4un" FICA: os sabores somam 5. Só a segunda cauda ("- 4 unidades",
            // com 2+2 = 4 embaixo) é redundante de verdade e sai.
            "1× Combo Box 4un",
            "2× Donut Homer", "1× Donut Morango c/ Ninho",
            "1× Donut Ninho c/ Nutella", "1× Donut Calabresa",
            "1× Combo 1 Cookies",
            "2× Cookie Tradicional", "2× Cookie Brigadeiro",
        };
        checar(linhas.SequenceEqual(esperado),
            "o card inteiro da foto sai como esperado: " + string.Join(" | ", linhas));
        checar(linhas.Count == esperado.Length,
            "o card continua com 8 linhas: normalizar texto não some com item nenhum");
        // O defeito nº 1 da foto, virado asserção: nenhuma linha do card marca
        // quantidade com "x" minúsculo, e toda linha começa pela quantidade.
        checar(linhas.All(l => Regex.IsMatch(l, @"^\d+(?:[.,]\d+)?× ")),
            "toda linha do card começa com a quantidade seguida de ×");
        checar(!linhas.Any(l => Regex.IsMatch(l, @"^\d+(?:[.,]\d+)?\s*x\s")),
            "nenhuma linha do card ainda marca quantidade com x minúsculo");
    }
}
