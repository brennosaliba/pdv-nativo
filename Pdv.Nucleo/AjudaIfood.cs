namespace Pdv.Nucleo;

/// <summary>
/// "FALE COM O iFOOD" (12/09/2026, pedido do dono): o atendimento do iFood, que no
/// Gestor de Pedidos abre por um link dentro do detalhe do pedido, chega ao KDS (botão
/// no detalhe) e à aba Chat (coluna do meio, entre as respostas prontas e as conversas).
/// Aqui mora só a regra pura: para quais pedidos o botão existe e os textos.
/// </summary>
public static class AjudaIfood
{
    public const string TextoBotao = "Fale com o iFood";

    /// <summary>Só pedido do iFood de verdade tem atendimento do iFood: balcão, encomenda e Cardápio Digital (CD-) não.</summary>
    public static bool PodePedirAjuda(string? origem, string? numero)
        => origem == "ifood"
           && !string.IsNullOrWhiteSpace(numero)
           && !numero.TrimStart('#').StartsWith("CD-", StringComparison.OrdinalIgnoreCase)
           && SoDigitos(numero).Length > 0;

    /// <summary>O número como o Gestor busca: só dígitos ("#5077" vira "5077").</summary>
    public static string SoDigitos(string? numero)
        => new string((numero ?? "").Where(char.IsDigit).ToArray());

    public static string Abrindo(string numero) => $"abrindo o Fale com o iFood do pedido #{SoDigitos(numero)}…";
}
