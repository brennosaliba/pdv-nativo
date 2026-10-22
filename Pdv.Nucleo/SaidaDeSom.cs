using System.Text.RegularExpressions;

namespace Pdv.Nucleo;

/// <summary>
/// A SAÍDA DE SOM DO WINDOWS (1.0.48; follow-up da 1.0.42). Em 16/10 a Savassi "tocava" a playlist
/// pelo ERP (HTTP 204, confirmado neste caixa) e ninguém ouvia: a saída padrão do Windows era
/// "Fones de ouvido (Oneal)" e, na coleta, nenhuma saída ativa. O caixa agora avisa na janela
/// ♫ música e manda a chave no sinal do chat. Quem lê o Windows é o exe (o COM do Core Audio que
/// o Spotify do caixa já usava); aqui só a regra.
/// </summary>
public static class SaidaDeSom
{
    /// <param name="Achou">O Windows tem uma saída padrão ativa.</param>
    /// <param name="Formato">PKEY_AudioEndpoint_FormFactor (3 = fone, 6 = headset), quando o Windows diz.</param>
    public sealed record Leitura(bool Achou, string? Nome, int? Formato);

    public const int FormatoFone = 3;
    public const int FormatoHeadset = 6;

    public const string SemSaida = "Sem saída de som ativa no Windows.";
    public const string Fone = "A saída de som padrão é um fone.";

    // "Fones de ouvido", "Headphones", "Headset"; "Microfone" não conta
    private static readonly Regex NomeDeFone = new(@"(?<!micro)fone|headphone|headset", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool EhFone(Leitura l)
        => l.Achou && (l.Formato is FormatoFone or FormatoHeadset || (l.Nome is not null && NomeDeFone.IsMatch(l.Nome)));

    /// <summary>A frase para a tela, ou null quando não há o que avisar.</summary>
    public static string? Aviso(Leitura l) => !l.Achou ? SemSaida : EhFone(l) ? Fone : null;

    /// <summary>A chave para o sinal (lista fechada, nunca o nome do aparelho), ou null.</summary>
    public static string? ParaOSinal(Leitura l) => !l.Achou ? "sem_saida" : EhFone(l) ? "fone" : null;
}
