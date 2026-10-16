using System.Globalization;
using System.Text.Json;

namespace Pdv.Nucleo;

/// <summary>
/// O RELÓGIO CORRIGIDO PELO SERVIDOR (1.0.42, 16/10/2026, Savassi). O PC do caixa estava 566 s ATRASADO: toda
/// fala do cliente chegava com created_at "no futuro" além da folga de 2 min e as duas peneiras
/// (<see cref="ChatRaspadinha.Recente"/> e <see cref="ConversaRaspadinha.Triagem"/>, "antiga") a descartavam com
/// <c>DateTime.Now</c>. O robô ficou cego o dia inteiro, com o chat vivo.
///
/// Agora o sinal de 60 s devolve a hora do servidor (<c>agora</c>, o now() do Postgres); o caixa mede o DESVIO
/// do PC (positivo = o PC adiantado, o mesmo sinal de <c>relogio_seg</c>) na resposta, guarda o último valor
/// válido e TODA comparação de hora de fala, quadro, recuperação, eco e incerta usa <see cref="Agora(DateTime, TimeSpan?)"/>:
/// o relógio local menos o desvio. A tolerância de 2 min continua, mas sobre o relógio corrigido. A latência da
/// chamada não entra (a resposta chega centenas de ms depois do now(): o desvio fica um pouco otimista, dentro
/// da folga). Sem sinal ainda, o relógio é o do PC, como sempre. Nunca lança.
/// </summary>
public static class RelogioCorrigido
{
    /// <summary>Acima disto o "agora" do servidor é lixo (data de outro ano): a leitura não vale.</summary>
    public static readonly TimeSpan DesvioMaximo = TimeSpan.FromDays(366);

    private static readonly object Trava = new();
    private static TimeSpan? _desvio;
    private static DateTime? _medidoEm;

    /// <summary>O relógio corrigido: o local menos o desvio do PC. Sem desvio (ou desvio fora do máximo), o local. Nunca lança.</summary>
    public static DateTime Agora(DateTime local, TimeSpan? desvio)
    {
        if (desvio is not { } d || d.Duration() > DesvioMaximo) return local;
        try { return local - d; }
        catch { return local; }
    }

    /// <summary>
    /// O desvio do PC medido na resposta do sinal: o relógio local NA RESPOSTA menos o "agora" do servidor
    /// (positivo = o PC adiantado). Null quando o servidor não disse a hora ou a diferença passa de um ano.
    /// </summary>
    public static TimeSpan? Medir(DateTimeOffset? servidor, DateTimeOffset localNaResposta)
    {
        if (servidor is not { } s) return null;
        try
        {
            var d = localNaResposta - s;
            return d.Duration() > DesvioMaximo ? null : d;
        }
        catch { return null; }
    }

    /// <summary>O "agora" do servidor no corpo de um sinal (<c>agora</c>, ISO 8601 com fuso), ou null. Nunca lança.</summary>
    public static DateTimeOffset? AgoraDoSinal(string? corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return null;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("agora", out var a) || a.ValueKind != JsonValueKind.String) return null;
            return DateTimeOffset.TryParse(a.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var dt) ? dt : null;
        }
        catch { return null; }
    }

    /// <summary>O desvio em segundos inteiros para o sinal (positivo = adiantado), ou null.</summary>
    public static int? Segundos(TimeSpan? desvio) => RelogioDoWindows.Segundos(desvio);

    // ── o estado do caixa (um por processo) ──────────────────────────────────

    /// <summary>O último desvio válido medido (positivo = o PC adiantado), ou null antes do primeiro sinal.</summary>
    public static TimeSpan? Desvio { get { lock (Trava) return _desvio; } }

    /// <summary>Quando o desvio foi medido pela última vez (relógio local), ou null.</summary>
    public static DateTime? MedidoEm { get { lock (Trava) return _medidoEm; } }

    /// <summary>O relógio corrigido de agora: <c>DateTime.Now</c> menos o desvio conhecido.</summary>
    public static DateTime AgoraCorrigido => Agora(DateTime.Now, Desvio);

    /// <summary>Guarda uma medição. Null (o servidor não disse a hora, ou lixo) NÃO apaga a última válida. Nunca lança.</summary>
    public static void Aprender(TimeSpan? desvio, DateTime? localNaResposta = null)
    {
        if (desvio is not { } d || d.Duration() > DesvioMaximo) return;
        lock (Trava)
        {
            _desvio = d;
            _medidoEm = localNaResposta ?? DateTime.Now;
        }
    }

    /// <summary>Mede e guarda numa chamada: a resposta do sinal e o relógio local na resposta. Devolve o desvio aprendido (ou null).</summary>
    public static TimeSpan? AprenderDoSinal(string? corpo, DateTimeOffset localNaResposta)
    {
        var d = Medir(AgoraDoSinal(corpo), localNaResposta);
        Aprender(d, localNaResposta.LocalDateTime);
        return d;
    }

    /// <summary>Só a bateria: esquece o desvio, como num caixa que acabou de subir.</summary>
    public static void Esquecer()
    {
        lock (Trava) { _desvio = null; _medidoEm = null; }
    }
}
