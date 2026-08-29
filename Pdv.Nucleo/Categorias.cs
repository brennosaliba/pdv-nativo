using System.Globalization;

namespace Pdv.Nucleo;

// A ORDEM em que a coluna de categorias e a grade de produtos aparecem na tela de venda.
//
// Existe por causa de um defeito que o dono viu no balcão: em "Bebidas", ÁGUA MINERAL COM GÁS
// era o ÚLTIMO item da grade, depois de SUCO UVA. O motivo é que a ordem vinha do
// `ORDER BY categoria, nome` do SQLite, e o SQLite ordena texto por BYTE (collation BINARY):
// "Á" em UTF-8 é 0xC3 0x81, maior que qualquer letra ASCII, então TODA palavra acentuada cai
// no fim da lista. O `COLLATE NOCASE` do SQLite não resolve — ele só dobra caixa de A-Z, não
// conhece acento. Por isso a ordem é decidida AQUI, em C#, depois de ler do banco.
//
// A lista de categorias em si vinha de um `OrderBy(c => c)`, que usa a CultureInfo da máquina.
// Na loja (Windows pt-BR) isso até acertava, mas por acidente: basta o Windows estar em outro
// idioma, ou alguém publicar com `InvariantGlobalization=true` para reduzir o instalador, e o
// `OrderBy(c => c)` vira comparação ORDINAL — aí "Açaí" e "Águas" saltam para depois de
// "Zebra" e ninguém liga o defeito na tela a uma flag de build. Cravar a cultura pt-BR aqui
// tira a ordem da sorte do ambiente.
//
// Duas regras que NÃO são detalhe de implementação, e por isso têm teste (TestesCategorias):
//
//   1. PROMOÇÃO é sentinela: quando existe promoção vigente, ela é a primeira categoria da
//      coluna e a que abre por padrão — não entra no alfabeto. É a vitrine do dia; se o "P"
//      a jogasse para o meio da lista, o operador teria que procurar o desconto que ele mesmo
//      acabou de anunciar para o cliente.
//   2. A deduplicação é ORDINAL, nunca por caixa. Se o cardápio da nuvem trouxer "Bebidas" e
//      "bebidas" como grupos diferentes, as duas continuam sendo dois botões: a grade filtra
//      produto por igualdade exata de categoria, então colapsar os nomes aqui faria os
//      produtos de uma delas sumirem da tela sem erro nenhum. Ordem alfabética é assunto de
//      APRESENTAÇÃO; identidade de categoria é assunto do banco.

/// <summary>
/// Ordem alfabética em português das categorias e dos produtos da tela de venda.
/// Sem dependência de WPF de propósito: é a suíte de testes que prova a ordem, não a tela.
/// </summary>
public static class Categorias
{
    /// <summary>
    /// A categoria-vitrine de promoções. Não vem do cardápio: a tela a acrescenta quando
    /// alguma promoção vigente alcança produto do catálogo. Fica sempre em primeiro.
    /// </summary>
    public const string Promocao = "promoção";

    /// <summary>
    /// O comparador que define "ordem alfabética" no PDV: pt-BR, ignorando caixa.
    ///
    /// pt-BR (e não Ordinal/InvariantCulture com bytes) porque em português acento é a MESMA
    /// letra: "Açaí" pertence ao A, entre "Açúcar" e "Adoçante" — não depois de "Zebra".
    /// Ignorando caixa porque o cardápio chega com grafia mista da nuvem ("ENCOMENDAS",
    /// "Donuts sem Recheio") e o operador lê a coluna como uma lista só, não como duas.
    /// </summary>
    public static readonly StringComparer Alfabetica =
        StringComparer.Create(new CultureInfo("pt-BR"), ignoreCase: true);

    /// <summary>
    /// A coluna de categorias como ela aparece na tela: sem repetição, em ordem alfabética
    /// de português, com <paramref name="fixaNoTopo"/> (a vitrine de PROMOÇÃO) na frente
    /// quando ela está presente. Nomes vazios são descartados — botão sem rótulo é pior
    /// que categoria faltando.
    /// </summary>
    /// <param name="categorias">Os nomes crus, na ordem que vieram do banco (pode repetir).</param>
    /// <param name="fixaNoTopo">Categoria sentinela que não entra no alfabeto; null = nenhuma.</param>
    public static List<string> Ordenar(IEnumerable<string?> categorias, string? fixaNoTopo = null)
        => categorias
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            // Ordinal de propósito: ver regra 2 no comentário do topo.
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => EhSentinela(c, fixaNoTopo) ? 0 : 1)
            .ThenBy(c => c, Alfabetica)
            // Desempate: "bebidas" e "Bebidas" são IGUAIS para o comparador acima, e sem isto
            // a ordem entre elas seria a ordem de leitura do banco — que muda com um INSERT.
            .ThenBy(c => c, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Os produtos de uma categoria na ordem em que a grade os desenha: alfabética de
    /// português pelo nome. É o mesmo comparador da coluna de categorias de propósito — a
    /// tela ficaria incoerente se a lateral respeitasse acento e a grade não.
    /// </summary>
    public static List<T> OrdenarPorNome<T>(IEnumerable<T> itens, Func<T, string> nome)
        => itens
            .OrderBy(nome, Alfabetica)
            .ThenBy(nome, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// A sentinela é reconhecida ignorando caixa e acento-como-letra (mesmo critério da
    /// ordem), para "Promoção" vindo do cardápio da nuvem cair no mesmo lugar que a nossa
    /// constante em vez de virar uma segunda categoria no meio do alfabeto.
    /// </summary>
    private static bool EhSentinela(string categoria, string? fixaNoTopo)
        => fixaNoTopo is { Length: > 0 } && Alfabetica.Equals(categoria, fixaNoTopo);
}
