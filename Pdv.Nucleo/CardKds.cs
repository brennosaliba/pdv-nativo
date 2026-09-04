using System.Globalization;
using System.Text.RegularExpressions;

namespace Pdv.Nucleo;

/// <summary>
/// Uma linha do card do KDS já partida em QUANTIDADE e NOME.
///
/// A partição existe porque a tela pinta as duas metades com pesos diferentes: o
/// cozinheiro varre a coluna de quantidades antes de ler nome nenhum, então a
/// quantidade vai em negrito e o nome em peso normal. Sem a partição a tela
/// precisaria fatiar string com índice — que é exatamente o tipo de código que
/// some com um item quando chega um nome estranho.
/// </summary>
/// <param name="Qtd">"2×", ou vazio quando a linha não tem quantidade reconhecível.</param>
/// <param name="Nome">O resto da linha, já normalizado.</param>
public readonly record struct LinhaCard(string Qtd, string Nome)
{
    /// <summary>A linha inteira, do jeito que ela é lida na parede. Só para teste e log.</summary>
    public string Texto => Qtd.Length == 0 ? Nome : Qtd + " " + Nome;
}

/// <summary>
/// O TEXTO do card do quadro de preparo. Lógica pura, sem WPF, pelo mesmo motivo
/// de Categorias: quem prova que "Combo 1 Cookies - 4 unidades" vira
/// "Combo 1 Cookies" é a suíte, não o olho de quem abriu a tela.
///
/// NASCEU DE UMA FOTO (04/09). O dono mandou um card real e disse: "DIFERENCIAÇÃO
/// DA COR DO AGUARDANDO ENTREGADOR, DO ITEM PRINCIPAL, DOS SUBITENS, pra não ficar
/// tão confuso". Cor é assunto do tema; o que sobra para cá são os dois defeitos de
/// TEXTO que a foto mostrava:
///
///   1. MARCADOR DE QUANTIDADE MISTURADO. O item principal saía com "×" (a tela
///      montava $"{qtd}× {nome}") e o subitem saía com "x" minúsculo, porque a
///      string do subitem vem pronta de Kds.ItensDeJson ("2x Donut Homer"). Dois
///      símbolos para a mesma coisa no mesmo card.
///
///   2. CAUDA REDUNDANTE NO NOME DO COMBO. "Combo 1 Cookies - 4 unidades" com as
///      4 unidades listadas logo abaixo gasta 14 caracteres para repetir o que o
///      olho já vê. Num card de ~250 px isso não é estética: é a quebra de linha
///      que partia o nome em "1× Combo 1 Cookies - 4 / unidades".
///
/// A REGRA DA CAUDA É PARANOICA DE PROPÓSITO. Só corta quando dá para PROVAR que o
/// número já está na tela logo abaixo, e na dúvida devolve o nome intacto: nome
/// mutilado na cozinha custa mais caro que nome comprido.
/// </summary>
public static class CardKds
{
    /// <summary>O marcador de quantidade do quadro, um só: "2× Donut", nunca "2x Donut".</summary>
    public const string Vezes = "×";

    // ── QUANTOS CARDS CABEM LADO A LADO (04/09, segunda foto do dono) ────────
    // "ainda nao esta bom o ux..talvez diminuir um poouco a fonte..aumentar o box".
    // O quadro divide a tela em TRÊS colunas e cada coluna trazia DOIS cards fixos.
    // Medido na foto do modo --foto-kds a 1024x768, que é a tela da Savassi: sobram
    // ~150 px por card, e a 16 px de fonte quase todo item quebra em duas linhas
    // ("1× Tortinha de / Frango com / Catupiry"). Dois fixos não era uma escolha de
    // densidade: era o número que estava escrito no código desde o primeiro dia.
    //
    // A saída NÃO é encolher a fonte. A cozinha lê o card a 1 ou 2 metros, e o que
    // sobra de legibilidade ali é justamente o que não pode ser gasto. O que estava
    // sobrando era COLUNA: em tela estreita cabe um card por linha, e ele fica com o
    // dobro da largura.

    /// <summary>
    /// Largura mínima que um card precisa para o nome de item comum caber numa linha
    /// só a 16 px. Medida no pior nome real do cardápio ("Tortinha de Frango com
    /// Catupiry", ~245 px com a quantidade e as folgas), arredondada para cima.
    /// </summary>
    public const double LarguraMinimaCard = 270;

    /// <summary>
    /// Quantos cards cabem lado a lado numa coluna do quadro de
    /// <paramref name="larguraDaColuna"/> pixels úteis.
    ///
    /// Chão de 1 e teto de 3: um card sozinho numa coluna larga é feio, mas card
    /// ilegível é caro. Largura desconhecida (a primeira pintura acontece antes de o
    /// WPF medir a tela) responde 1, que é o valor que nunca quebra nome.
    /// </summary>
    public static int CardsPorLinha(double larguraDaColuna, int teto = 3)
        => larguraDaColuna <= 0
            ? 1
            : Math.Clamp((int)(larguraDaColuna / LarguraMinimaCard), 1, Math.Max(1, teto));

    /// <summary>
    /// A quantidade do item como ela aparece: milésimos viram "1", "2", "1,5".
    /// Mesma conta que a tela fazia inline (e que a comanda continua fazendo), agora
    /// em lugar onde o teste alcança.
    /// </summary>
    public static string Quantidade(int qtdMilesimo) =>
        qtdMilesimo % 1000 == 0
            ? (qtdMilesimo / 1000).ToString()
            : (qtdMilesimo / 1000m).ToString("0.###");

    /// <summary>O item principal do card: quantidade em negrito, nome sem cauda redundante.</summary>
    public static LinhaCard ItemPrincipal(TicketItem i) =>
        ItemPrincipal(i.Qtd, i.Descricao, i.Escolhas);

    /// <inheritdoc cref="ItemPrincipal(TicketItem)"/>
    public static LinhaCard ItemPrincipal(int qtdMilesimo, string? descricao,
                                          IReadOnlyList<string>? escolhas)
        => new(Quantidade(qtdMilesimo) + Vezes, SemCaudaRedundante(descricao ?? "", escolhas));

    /// <summary>
    /// Uma escolha do combo ("2x Donut Homer") virando linha de card ("2×" + "Donut Homer").
    ///
    /// Quando a escolha vem com o GRUPO na frente ("Clássicos: 2x Donut Ninho"), a
    /// quantidade sobe para a frente e o grupo fica no nome ("2×" + "Clássicos: Donut
    /// Ninho"). A troca de ordem é de propósito: a lista de subitens só vale como
    /// coluna se TODA linha começar pela quantidade — com o grupo na frente, uma linha
    /// começa por número e a outra por texto, e a varredura se perde. Nada é
    /// descartado, só reordenado.
    ///
    /// Escolha sem quantidade reconhecível (o cardápio também manda string crua)
    /// devolve quantidade vazia e o texto como veio. Nunca inventa "1×".
    /// </summary>
    public static LinhaCard SubItem(string? escolha)
    {
        var s = (escolha ?? "").Trim();
        if (s.Length == 0) return new LinhaCard("", "");

        var m = ReQtd.Match(s);
        if (!m.Success) return new LinhaCard("", s);

        var grupo = m.Groups["pre"].Success ? m.Groups["pre"].Value.Trim() + " " : "";
        return new LinhaCard(m.Groups["q"].Value + Vezes, grupo + m.Groups["nome"].Value.Trim());
    }

    /// <summary>
    /// O nome do item sem a contagem de unidades no fim, QUANDO essa contagem já está
    /// listada logo abaixo. "Combo 1 Cookies - 4 unidades" com 2x + 2x embaixo vira
    /// "Combo 1 Cookies"; "Combo Box 4un" com 4 sabores embaixo vira "Combo Box".
    ///
    /// As cinco travas, todas obrigatórias (qualquer uma falhando devolve o nome como veio):
    ///   1. Tem componentes listados. Item simples ("Donut Homer 6un" vendido avulso)
    ///      nunca é tocado — ali o número é a única informação de tamanho que existe.
    ///   2. TODA escolha tem quantidade legível. Uma linha sem número já impede a prova.
    ///   3. A cauda casa um padrão fechado: número + palavra de unidade + FIM do nome.
    ///      "Kit 12 un Mini" não casa (não termina ali) e sai intacto.
    ///   4. O número da cauda é igual à SOMA das quantidades listadas abaixo. Só a soma,
    ///      nunca a contagem de linhas. Casar com a contagem parecia razoável ("Box 4un"
    ///      com 4 sabores) e é armadilha: o card da foto do dono tinha 4 sabores somando
    ///      CINCO donuts numa caixa que se diz de 4. Pela contagem o "4un" seria cortado
    ///      e o cozinheiro perderia a única pista de que a caixa está saindo errada.
    ///      Cauda que discorda do que está embaixo é justamente a que tem de ficar.
    ///   5. O que sobra continua sendo nome: pelo menos 4 caracteres, com letra,
    ///      terminando em letra ou dígito e com a última palavra de 2+ caracteres.
    ///      É esta trava que impede "Combo c/ 4 unidades" de virar "Combo c/".
    /// </summary>
    public static string SemCaudaRedundante(string nome, IReadOnlyList<string>? escolhas)
    {
        nome = (nome ?? "").Trim();
        if (nome.Length == 0) return nome;
        if (escolhas is not { Count: > 0 }) return nome;              // trava 1

        decimal soma = 0;
        foreach (var e in escolhas)
        {
            if (QtdDaEscolha(e) is not { } q) return nome;            // trava 2
            soma += q;
        }

        var m = ReCauda.Match(nome);
        if (!m.Success) return nome;                                  // trava 3
        if (!LeNumero(m.Groups["n"].Value, out var n)) return nome;
        if (n != soma) return nome;                                   // trava 4

        var resto = nome[..m.Index].TrimEnd(' ', '\t', '-', '–', '—', '|', ',', '(');
        if (resto.Length < 4 || !resto.Any(char.IsLetter)) return nome;               // trava 5
        if (!char.IsLetterOrDigit(resto[^1])) return nome;
        if (resto.Split(' ', '\t').Last().Length < 2) return nome;
        return resto;
    }

    /// <summary>A quantidade de uma escolha, ou null quando não dá para ler número nenhum.</summary>
    public static decimal? QtdDaEscolha(string? escolha)
    {
        var m = ReQtd.Match((escolha ?? "").Trim());
        return m.Success && LeNumero(m.Groups["q"].Value, out var q) ? q : null;
    }

    /// <summary>
    /// "2x Donut Homer" e "Clássicos: 2x Donut Ninho". O espaço depois do x é
    /// OBRIGATÓRIO: sem ele "3x4 Bolo" viraria "3× 4 Bolo", que é nome inventado.
    /// </summary>
    private static readonly Regex ReQtd = new(
        @"^(?<pre>[^:]{1,40}:\s+)?(?<q>\d+(?:[.,]\d+)?)\s*[xX×]\s+(?<nome>\S.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A contagem de unidades no FIM do nome: "- 4 unidades", "4un", "(6 peças)".
    /// A palavra de unidade é obrigatória — nome terminado em número solto
    /// ("Combo 4") é linha de produto, não contagem, e fica de fora.
    /// </summary>
    private static readonly Regex ReCauda = new(
        @"\s*[\-–—|,]?\s*\(?\s*(?<n>\d+(?:[.,]\d+)?)\s*"
      + @"(?:unidades|unidade|unids|unid|unds|und|uni|un|pe[çc]as|pe[çc]a|p[çc]s|p[çc]|pcs|pc)"
      + @"\b\.?\s*\)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Aceita "4", "1,5" e "1.5": o texto vem de fonte que não escolhe cultura.</summary>
    private static bool LeNumero(string s, out decimal valor) =>
        decimal.TryParse(s.Replace(',', '.'), NumberStyles.Number,
                         CultureInfo.InvariantCulture, out valor);
}
