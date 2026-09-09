namespace Pdv.Nucleo;

/// <summary>
/// PREÇO NOVO EM COMANDA JÁ ABERTA.
///
/// O QUE O DONO VIU (09/09/2026): trocou o preço de PRODUTO TESTE de R$ 0,25 para
/// R$ 0,10 no painel, sincronizou, e a linha que já estava na comanda continuou
/// R$ 0,25. A grade de produtos ao lado já mostrava R$ 0,10.
///
/// POR QUE ACONTECIA. A linha da comanda não guarda preço: ela guarda uma
/// REFERÊNCIA ao objeto do produto (`ItemComanda.Produto`), e o total sai de
/// `Produto.Preco`. O Sincronizar grava o preço novo no banco local e o
/// `RecarregarCatalogo` monta objetos NOVOS para a grade, mas a linha da comanda
/// continua apontando para o objeto VELHO. Grade e comanda passavam a discordar.
///
/// ⚠️ E o pior: no religamento a comanda voltava com o preço NOVO, porque a
/// restauração do rascunho lê o preço do catálogo atual. Ou seja, o mesmo item
/// custava um valor depois de sincronizar e outro depois de reiniciar o caixa.
/// Isso não é uma escolha de desenho, é acidente de referência de objeto, e por
/// isso é bug e não regra de negócio.
///
/// A REGRA QUE FICA: a comanda acompanha a tabela de preços, e o operador é
/// AVISADO do que mudou. Trocar o preço calado seria pior que não trocar: o caixa
/// que acabou de dizer "são R$ 12,00" para o cliente precisa saber que virou
/// outro valor, principalmente quando SOBE.
///
/// O que esta classe NÃO faz: mexer em promoção. O desconto continua saindo do
/// motor de promoções na pintura da comanda. Aqui só se resolve o preço de tabela.
/// </summary>
public static class Reprecificacao
{
    /// <summary>Uma linha da comanda, reduzida ao que interessa para reprecificar.</summary>
    public sealed record Linha(string ProdutoId, string Nome, long PrecoCent);

    /// <summary>O que mudou numa linha. `DeCent` é o que estava na comanda.</summary>
    public sealed record Troca(string ProdutoId, string Nome, long DeCent, long ParaCent)
    {
        public bool Subiu => ParaCent > DeCent;
    }

    /// <summary>
    /// O que mudou entre a comanda aberta e o catálogo recém baixado.
    ///
    /// Produto que SUMIU do catálogo (foi desativado no painel) não entra na lista
    /// e mantém o preço que tinha. A linha já foi pedida pelo cliente: apagá-la ou
    /// zerá-la porque alguém desativou o produto no painel seria trocar um problema
    /// pequeno por um grande.
    ///
    /// A mesma linha repetida na comanda aparece UMA vez aqui: o aviso fala de
    /// produto, não de linha, senão trocar o preço de um item com 8 unidades
    /// encheria a tela com a mesma frase 8 vezes.
    /// </summary>
    public static IReadOnlyList<Troca> Trocas(
        IEnumerable<Linha> naComanda,
        IReadOnlyDictionary<string, long> precoPorProdutoId)
    {
        var vistos = new HashSet<string>(StringComparer.Ordinal);
        var trocas = new List<Troca>();
        foreach (var linha in naComanda)
        {
            if (linha is null || string.IsNullOrEmpty(linha.ProdutoId)) continue;
            if (!vistos.Add(linha.ProdutoId)) continue;
            if (!precoPorProdutoId.TryGetValue(linha.ProdutoId, out var agora)) continue;
            if (agora == linha.PrecoCent) continue;
            trocas.Add(new Troca(linha.ProdutoId, linha.Nome, linha.PrecoCent, agora));
        }
        return trocas;
    }

    /// <summary>
    /// O aviso que aparece para o operador. `null` quando nada mudou, para a tela
    /// não piscar caixa de texto à toa a cada sincronização.
    ///
    /// Mostra os dois valores de propósito: "preço atualizado" sozinho não deixa o
    /// operador conferir nada, e é justamente conferir que evita a discussão no
    /// balcão.
    /// </summary>
    public static string? Aviso(IReadOnlyList<Troca> trocas)
    {
        if (trocas is null || trocas.Count == 0) return null;

        var linhas = trocas
            .Select(t => $"{t.Nome}: {new Dinheiro(t.DeCent).Formatado()} para {new Dinheiro(t.ParaCent).Formatado()}")
            .ToList();

        var cabeca = trocas.Count == 1
            ? "Um item da comanda mudou de preço:"
            : $"{trocas.Count} itens da comanda mudaram de preço:";

        // Se algum SUBIU, o operador precisa avisar o cliente antes de fechar. É a
        // única situação em que ficar calado gera briga no caixa.
        var rodape = trocas.Any(t => t.Subiu)
            ? "\n\nUm preço subiu. Confira com o cliente antes de finalizar."
            : "";

        return cabeca + "\n" + string.Join("\n", linhas) + rodape;
    }

    /// <summary>
    /// A mesma notícia em UMA linha, para a faixa que aparece sem travar a tela.
    ///
    /// A tela de venda tem uma regra antiga, escrita no topo dela: nada de diálogo
    /// bloqueante no caminho de alta frequência. Preço que CAI cabe aqui. Preço que
    /// SOBE não: o operador já falou o total em voz alta para o cliente, e esse
    /// precisa parar a tela.
    /// </summary>
    public static string? Faixa(IReadOnlyList<Troca> trocas)
    {
        if (trocas is null || trocas.Count == 0) return null;
        if (trocas.Count == 1)
        {
            var t = trocas[0];
            return $"{t.Nome}: {new Dinheiro(t.DeCent).Formatado()} para {new Dinheiro(t.ParaCent).Formatado()}";
        }
        return $"{trocas.Count} itens da comanda mudaram de preço";
    }

    /// <summary>Preço que subiu para a tela; preço que só caiu, não.</summary>
    public static bool PrecisaParar(IReadOnlyList<Troca> trocas)
        => trocas is not null && trocas.Any(t => t.Subiu);
}
