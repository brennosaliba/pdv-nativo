namespace Pdv.Nucleo;

/// <summary>
/// Marca de TAMANHO numa linha de texto para impressão.
///
/// Por que existe: a comanda da cozinha é lida de longe, em pé e com pressa —
/// 10pt uniforme serve ao cupom fiscal, não ao balcão. Mas quem monta a comanda
/// (<see cref="Kds.ComandaLinhas"/>) devolve linhas PURAS, porque o KDS e os
/// testes leem as mesmas linhas; e quem desenha o papel está na camada de cima.
/// A escala viaja então como um prefixo invisível no próprio texto: quem só lê
/// continua lendo texto, e a impressão traduz em tamanho de fonte.
/// </summary>
public static class LinhaEscala
{
    public const char Marca = '\x01';

    /// <summary>Marca a linha para sair `escala` vezes maior (1.0 = normal, teto 4).</summary>
    public static string Com(string linha, double escala)
        => Marca + ((int)System.Math.Round(System.Math.Clamp(escala, 1.0, 4.0) * 10))
                       .ToString(System.Globalization.CultureInfo.InvariantCulture)
           + "|" + linha;

    /// <summary>Separa texto e escala. Linha sem marca volta inteira com escala 1.</summary>
    public static (string Texto, double Escala) Le(string? linha)
    {
        var l = linha ?? "";
        if (l.Length < 3 || l[0] != Marca) return (l, 1.0);
        var barra = l.IndexOf('|');
        if (barra < 2 || !int.TryParse(l[1..barra], out var d)) return (l, 1.0);
        return (l[(barra + 1)..], System.Math.Clamp(d / 10.0, 1.0, 4.0));
    }

    /// <summary>Texto sem a marca — para tela, log e conferência.</summary>
    public static string Limpa(string? linha) => Le(linha).Texto;
}
