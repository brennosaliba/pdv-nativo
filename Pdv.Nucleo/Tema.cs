using System.Globalization;

namespace Pdv.Nucleo;

public enum ModoTema { Escuro, Claro }

/// <summary>
/// Decide qual tema vale agora. Lógica pura — o relógio entra por parâmetro,
/// como em Drenagem.DecidirFila, para o teste cravar o horário em vez de
/// depender da hora em que a bateria rodou.
///
/// Config (tabela config, por MÁQUINA — o que pede modo claro é a luz do
/// balcão, não a pessoa logada):
///   tema           = escuro | claro | auto     (default: escuro)
///   tema_claro_de  = HH:mm                     (default: 06:00)
///   tema_claro_ate = HH:mm                     (default: 18:00)
/// </summary>
public static class Tema
{
    /// <summary>
    /// "claro"/"escuro" mandam sem olhar o relógio; "auto" aplica a janela.
    /// Qualquer outra coisa (null, "", "azul", "AUTO ") cai em Escuro SEM
    /// lançar: o CLI `config &lt;chave&gt; &lt;valor&gt;` grava qualquer string sem
    /// validar, então valor inválido é alcançável em produção — e a tela de
    /// venda não pode morrer por causa de um typo de configuração.
    /// </summary>
    public static ModoTema Decidir(string? configurado, TimeOnly claroDe, TimeOnly claroAte, DateTime agora)
    {
        switch ((configurado ?? "").Trim().ToLowerInvariant())
        {
            case "claro":  return ModoTema.Claro;
            case "escuro": return ModoTema.Escuro;
            case "auto":   break;
            default:       return ModoTema.Escuro;
        }

        var hora = TimeOnly.FromDateTime(agora);

        // Janela normal (06:00–18:00): claro DENTRO dela. Janela invertida
        // (18:00–06:00, turno noturno): cruza a meia-noite, claro fora do miolo.
        // Borda: o início pertence à janela, o fim não — 18:00 em ponto já é escuro.
        var dentro = claroDe <= claroAte
            ? hora >= claroDe && hora < claroAte
            : hora >= claroDe || hora < claroAte;

        return dentro ? ModoTema.Claro : ModoTema.Escuro;
    }

    /// <summary>
    /// Parse tolerante da janela. Horário malformado ("25:99", "meio-dia", null)
    /// devolve o padrão 06:00–18:00 em vez de quebrar a subida do app.
    /// </summary>
    public static (TimeOnly de, TimeOnly ate) LerJanela(string? de, string? ate)
    {
        var pDe  = Parse(de)  ?? new TimeOnly(6, 0);
        var pAte = Parse(ate) ?? new TimeOnly(18, 0);
        return (pDe, pAte);

        static TimeOnly? Parse(string? s) =>
            TimeOnly.TryParseExact((s ?? "").Trim(), "HH:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }

    /// <summary>
    /// Razão de contraste WCAG 2.1 entre duas cores "#RRGGBB". Vive no Núcleo
    /// para o teste medir a paleta clara lendo o próprio Temas/Claro.xaml —
    /// legibilidade vira asserção, não opinião.
    /// </summary>
    public static double Contraste(string hexA, string hexB)
    {
        var la = Luminancia(hexA);
        var lb = Luminancia(hexB);
        var (claro, escuro) = la >= lb ? (la, lb) : (lb, la);
        return (claro + 0.05) / (escuro + 0.05);
    }

    private static double Luminancia(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 8) hex = hex[2..];              // ignora canal alpha
        if (hex.Length != 6) throw new ArgumentException($"cor inválida: {hex}");

        double C(int i)
        {
            var c = int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * C(0) + 0.7152 * C(2) + 0.0722 * C(4);
    }
}
