namespace Pdv.Nucleo;

/// <summary>
/// A ABA PROMOÇÕES EM COLUNAS INDEPENDENTES (21/09/2026, pedido do dono).
///
/// A aba punha cada promoção como célula de um UniformGrid, e o UniformGrid dá a TODA célula a
/// altura da maior: com 13 promoções, uma seção de 1 produto ao lado de outra de 6 deixava um vão
/// de 180 px por linha, e o cartão do desconto de funcionário (sem produto nenhum) ficava do tamanho
/// da maior seção. Agora cada bloco entra na coluna mais baixa naquele momento e tem a altura dele.
///
/// Puro de propósito: a tela mede cada bloco e pergunta aqui em que coluna ele vai. A suíte prova a
/// regra sem WPF e mede o antes e o depois na tela de verdade.
/// </summary>
public static class ColunasPorAltura
{
    /// <summary>
    /// A coluna de cada bloco, na ordem dos blocos: sempre a mais baixa até ali (empate: a da
    /// esquerda). Assim a ordem de leitura continua a da lista (o primeiro bloco abre a primeira
    /// coluna) e as colunas terminam com alturas parecidas.
    /// </summary>
    public static int[] Distribuir(IReadOnlyList<double> alturas, int colunas)
    {
        colunas = Math.Max(1, colunas);
        var soma = new double[colunas];
        var destino = new int[alturas.Count];
        for (var i = 0; i < alturas.Count; i++)
        {
            var menor = 0;
            for (var c = 1; c < colunas; c++)
                if (soma[c] < soma[menor]) menor = c;
            destino[i] = menor;
            soma[menor] += Math.Max(0, alturas[i]);
        }
        return destino;
    }

    /// <summary>Altura da aba com a distribuição acima: a coluna mais alta.</summary>
    public static double AlturaEmColunas(IReadOnlyList<double> alturas, int colunas)
    {
        colunas = Math.Max(1, colunas);
        var destino = Distribuir(alturas, colunas);
        var soma = new double[colunas];
        for (var i = 0; i < alturas.Count; i++) soma[destino[i]] += Math.Max(0, alturas[i]);
        return alturas.Count == 0 ? 0 : soma.Max();
    }

    /// <summary>
    /// Altura que o UniformGrid antigo dava aos mesmos blocos: toda linha com a altura do MAIOR
    /// bloco. É o "antes" que a suíte compara.
    /// </summary>
    public static double AlturaEmGradeUniforme(IReadOnlyList<double> alturas, int colunas)
    {
        colunas = Math.Max(1, colunas);
        if (alturas.Count == 0) return 0;
        var linhas = (alturas.Count + colunas - 1) / colunas;
        return linhas * alturas.Max();
    }
}
