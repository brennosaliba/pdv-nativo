using System.Text.Json.Nodes;

namespace Pdv.Nucleo;

/// <summary>
/// 1.0.35 (14/10/2026, caso #8039): o RASTRO de um envio pelo SDK, tentativa por tentativa, para o
/// detalhe que vai ao ERP junto do resultado da saída (ver <see cref="ConversaRaspadinha.MontarDetalhe"/>).
/// Guarda o detalhe da ÚLTIMA tentativa que a página contou, o código do Sendbird de cada uma, se o
/// caixa aqueceu o SDK no meio, o que o plano B fez e o erro cru do script quando o caixa o trocou por
/// erro_envio. Puro: não fala com a página nem com a rede.
/// </summary>
public sealed class RastroDoEnvio
{
    private readonly string? _versao;
    private readonly List<int?> _codigos = new();
    private JsonObject? _ultimo;
    private int _tentativa;

    public RastroDoEnvio(string? versao) => _versao = versao;

    /// <summary>Quantas tentativas chegaram à página.</summary>
    public int Tentativas => _codigos.Count;

    public bool Aqueceu { get; set; }

    /// <summary>recarregou, na_frente, login, teto, sem_sdk, sem_tempo ou null (não houve plano B).</summary>
    public string? PlanoB { get; set; }

    public string? ErroScript { get; private set; }

    /// <summary>Uma tentativa que a página respondeu (ou não). <paramref name="erroCru"/> é o erro como veio.</summary>
    public void Registrar(int tentativa, JsonObject? detalhe, string? erroCru)
    {
        _tentativa = tentativa;
        int? codigo = detalhe?["codigo"] is JsonValue v && v.TryGetValue<int>(out var c) ? c : null;
        _codigos.Add(codigo);
        if (detalhe is not null) _ultimo = detalhe;
        // o erro cru só importa quando o caixa o trocou por erro_envio (token, limite, sem_resposta)
        ErroScript = erroCru is not null && ConversaRaspadinha.ErrosDoScript.Contains(erroCru) ? erroCru : null;
    }

    /// <summary>O detalhe para o ERP (até 600 bytes), ou null quando nenhuma tentativa chegou à página.</summary>
    public JsonObject? Detalhe()
        => ConversaRaspadinha.MontarDetalhe(_ultimo, _tentativa, _codigos.Count, _codigos, Aqueceu, PlanoB, ErroScript, _versao);
}
