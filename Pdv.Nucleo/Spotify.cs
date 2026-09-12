using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Pdv.Nucleo;

/// <summary>
/// SPOTIFY NO CAIXA (12/09/2026, pedido do dono: "no PDV ter controle também"). O painel
/// escolhe a playlist e o aparelho da loja; aqui o caixa toca, pausa, pula e ajusta o
/// volume pela Web API do Spotify. O access_token (1 h) vem da função de borda `spotify`
/// pela sessão do terminal (Nuvem.TokenSpotifyAsync); o refresh_token nunca chega aqui.
///
/// O caixa NÃO toca o som: o WebView2 desta máquina não tem Widevine (medido 12/09 na
/// sonda wv2exp: PlayReady sim, Widevine pendente), e o Web Playback SDK exige Widevine.
/// Quem toca é um aparelho com o Spotify aberto (caixa de som, celular, ou o próprio PC
/// com o app do Spotify), e este controle fala com ele pelo Spotify Connect.
/// </summary>
public sealed class Spotify
{
    public const string Api = "https://api.spotify.com/v1";

    public sealed record Estado(bool Tocando, string? Faixa, string? Artista, string? Aparelho,
                                string? AparelhoId, int? Volume, string? Contexto);

    private readonly Func<CancellationToken, Task<string?>> _token;
    private readonly Func<string?> _erroDoToken;
    private readonly Action _esquecerToken;
    private readonly HttpClient _http;

    public Spotify(Func<CancellationToken, Task<string?>> token, Func<string?> erroDoToken, Action esquecerToken, HttpClient? http = null)
    {
        _token = token; _erroDoToken = erroDoToken; _esquecerToken = esquecerToken;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
    }

    // ── leitura pura das respostas ──────────────────────────────────────────────
    /// <summary>A resposta da função `spotify` (acao token): token e validade, ou o motivo.</summary>
    public static (string? Token, int ExpiraEm, string? Erro) LerToken(int status, string? corpo)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(corpo) ? "{}" : corpo);
            var r = doc.RootElement;
            if (r.TryGetProperty("access_token", out var t) && t.ValueKind == JsonValueKind.String)
            {
                var exp = r.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var i) ? i : 3600;
                return (t.GetString(), exp, null);
            }
            var codigo = r.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String ? er.GetString() : null;
            return (null, 0, MensagemFuncao(codigo, status));
        }
        catch { return (null, 0, MensagemFuncao(null, status)); }
    }

    public static string MensagemFuncao(string? codigo, int status) => codigo switch
    {
        "nao_conectado" => "O Spotify ainda não foi conectado no painel (Música).",
        "sem_permissao" => "Este caixa não tem permissão para a música (papel no painel).",
        "sem_token" or "token_invalido" => "O caixa está sem sessão na nuvem. Toque em Atualizar.",
        "spotify_recusou" => "O Spotify recusou o acesso. Conecte de novo no painel (Música).",
        _ => status == 401 ? "O caixa está sem sessão na nuvem. Toque em Atualizar."
           : status == 0 ? "Sem internet agora." : $"A nuvem não respondeu ({status}).",
    };

    /// <summary>Um aparelho com o Spotify aberto nesta conta (GET /me/player/devices).</summary>
    public sealed record Aparelho(string Id, string Nome, string Tipo, bool Ativo);

    /// <summary>GET /me/player/devices: a lista, ou vazia quando o corpo não é o esperado.</summary>
    public static IReadOnlyList<Aparelho> LerAparelhos(int status, string? corpo)
    {
        if (status != 200 || string.IsNullOrWhiteSpace(corpo)) return Array.Empty<Aparelho>();
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            if (!doc.RootElement.TryGetProperty("devices", out var devs) || devs.ValueKind != JsonValueKind.Array)
                return Array.Empty<Aparelho>();
            var lista = new List<Aparelho>();
            foreach (var d in devs.EnumerateArray())
            {
                if (d.ValueKind != JsonValueKind.Object) continue;
                var id = d.TryGetProperty("id", out var i) ? i.GetString() : null;
                var nome = d.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(nome)) continue;
                lista.Add(new Aparelho(id ?? "", nome,
                    d.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                    d.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True));
            }
            return lista;
        }
        catch { return Array.Empty<Aparelho>(); }
    }

    /// <summary>GET /me/player: 204 = nada tocando (null); 200 = o estado.</summary>
    public static Estado? LerEstado(int status, string? corpo)
    {
        if (status != 200 || string.IsNullOrWhiteSpace(corpo)) return null;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            string? faixa = null, artista = null;
            if (r.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
            {
                faixa = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (item.TryGetProperty("artists", out var arts) && arts.ValueKind == JsonValueKind.Array)
                    artista = string.Join(", ", arts.EnumerateArray()
                        .Select(a => a.TryGetProperty("name", out var an) ? an.GetString() : null)
                        .Where(s => !string.IsNullOrEmpty(s)));
            }
            string? ap = null, apId = null; int? vol = null;
            if (r.TryGetProperty("device", out var dev) && dev.ValueKind == JsonValueKind.Object)
            {
                ap = dev.TryGetProperty("name", out var dn) ? dn.GetString() : null;
                apId = dev.TryGetProperty("id", out var di) ? di.GetString() : null;
                vol = dev.TryGetProperty("volume_percent", out var dv) && dv.TryGetInt32(out var vi) ? vi : null;
            }
            var ctx = r.TryGetProperty("context", out var c) && c.ValueKind == JsonValueKind.Object
                      && c.TryGetProperty("uri", out var cu) ? cu.GetString() : null;
            var tocando = r.TryGetProperty("is_playing", out var ip) && ip.ValueKind == JsonValueKind.True;
            return new Estado(tocando, faixa, artista, ap, apId, vol, ctx);
        }
        catch { return null; }
    }

    /// <summary>Erro da Web API em frase de gente (o motivo vem em error.reason ou error.message).</summary>
    public static string MensagemErro(int status, string? corpo)
    {
        string? razao = null, msg = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(corpo) ? "{}" : corpo);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                razao = e.TryGetProperty("reason", out var r) ? r.GetString() : null;
                msg = e.TryGetProperty("message", out var m) ? m.GetString() : null;
            }
        }
        catch { }
        return razao switch
        {
            "NO_ACTIVE_DEVICE" => "Nenhum aparelho com o Spotify aberto agora. Abra o Spotify no aparelho da loja.",
            "PREMIUM_REQUIRED" => "A conta do Spotify não é Premium; o controle exige Premium.",
            _ => status switch
            {
                404 => "Nenhum aparelho com o Spotify aberto agora. Abra o Spotify no aparelho da loja.",
                403 => msg is { Length: > 0 } ? "O Spotify não deixou: " + msg : "O Spotify não deixou fazer isso agora.",
                429 => "O Spotify pediu para esperar um pouco.",
                _ => msg is { Length: > 0 } ? "Spotify: " + msg : $"O Spotify não respondeu ({status}).",
            },
        };
    }

    // ── chamadas ─────────────────────────────────────────────────────────────────
    private async Task<(int Status, string Corpo)> ChamarAsync(HttpMethod m, string caminho, string? json, CancellationToken ct)
    {
        var token = await _token(ct).ConfigureAwait(false);
        if (token is null) return (0, "");
        using var req = new HttpRequestMessage(m, Api + caminho);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var corpo = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if ((int)resp.StatusCode == 401) _esquecerToken();
            return ((int)resp.StatusCode, corpo);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (0, ""); }
    }

    private async Task<string?> ComandoAsync(HttpMethod m, string caminho, string? json, CancellationToken ct)
    {
        var (st, corpo) = await ChamarAsync(m, caminho, json, ct).ConfigureAwait(false);
        if (st == 0) return _erroDoToken() ?? "Sem internet agora.";
        return st is 200 or 202 or 204 ? null : MensagemErro(st, corpo);
    }

    /// <summary>Os aparelhos com o Spotify aberto nesta conta, e o erro se a pergunta falhou.</summary>
    public async Task<(IReadOnlyList<Aparelho> Aparelhos, string? Erro)> AparelhosAsync(CancellationToken ct = default)
    {
        var (st, corpo) = await ChamarAsync(HttpMethod.Get, "/me/player/devices", null, ct).ConfigureAwait(false);
        if (st == 0) return (Array.Empty<Aparelho>(), _erroDoToken() ?? "Sem internet agora.");
        if (st != 200) return (Array.Empty<Aparelho>(), MensagemErro(st, corpo));
        return (LerAparelhos(st, corpo), null);
    }

    /// <summary>O que toca agora (null = nada), e o erro se a pergunta falhou.</summary>
    public async Task<(Estado? Estado, string? Erro)> EstadoAsync(CancellationToken ct = default)
    {
        var (st, corpo) = await ChamarAsync(HttpMethod.Get, "/me/player", null, ct).ConfigureAwait(false);
        if (st == 0) return (null, _erroDoToken() ?? "Sem internet agora.");
        if (st == 204) return (null, null);
        if (st != 200) return (null, MensagemErro(st, corpo));
        return (LerEstado(st, corpo), null);
    }

    private static string Q(string? deviceId, string prefixo = "?")
        => string.IsNullOrEmpty(deviceId) ? "" : prefixo + "device_id=" + Uri.EscapeDataString(deviceId);

    public Task<string?> TocarPlaylistAsync(string? deviceId, string playlistUri, CancellationToken ct = default)
        => ComandoAsync(HttpMethod.Put, "/me/player/play" + Q(deviceId),
            JsonSerializer.Serialize(new { context_uri = playlistUri }), ct);
    public Task<string?> RetomarAsync(string? deviceId, CancellationToken ct = default)
        => ComandoAsync(HttpMethod.Put, "/me/player/play" + Q(deviceId), null, ct);
    public Task<string?> PausarAsync(CancellationToken ct = default)
        => ComandoAsync(HttpMethod.Put, "/me/player/pause", null, ct);
    public Task<string?> ProximaAsync(CancellationToken ct = default)
        => ComandoAsync(HttpMethod.Post, "/me/player/next", null, ct);
    public Task<string?> AnteriorAsync(CancellationToken ct = default)
        => ComandoAsync(HttpMethod.Post, "/me/player/previous", null, ct);
    public Task<string?> VolumeAsync(int pct, string? deviceId, CancellationToken ct = default)
        => ComandoAsync(HttpMethod.Put, "/me/player/volume?volume_percent=" + Math.Clamp(pct, 0, 100) + Q(deviceId, "&"), null, ct);
}
