using System.Text.Json;

namespace Pdv.Nucleo;

/// <summary>
/// 1.0.41 (16/10/2026, pedido 2029): a CREDENCIAL da sessão do Sendbird que o Gestor abriu, lida de passagem
/// pelo CDP. É com ela que o caixa busca sozinho o histórico das conversas (REST api-&lt;appid&gt;.sendbird.com/v3).
/// <para>
/// ⚠️ SEGREDO. <see cref="SessionKey"/> é a sessão do dono no chat. Nunca entra em string interpolada, exceção,
/// diag, sinal ou arquivo: o <see cref="ToString"/> é sobrescrito de propósito e não a mostra.
/// </para>
/// </summary>
/// <param name="AppId">O app id do Sendbird (não é segredo: aparece no host ws-&lt;appid&gt;).</param>
/// <param name="UserId">O user_id da loja nesta sessão, quando a fonte o diz (o cabeçalho não diz: fica null).</param>
/// <param name="SessionKey">A chave de sessão (Session-Key). Só memória.</param>
/// <param name="Fonte">cabecalho, logi ou url.</param>
/// <param name="Em">Quando foi vista.</param>
public sealed record CredencialSendbird(string AppId, string? UserId, string SessionKey, string Fonte, DateTime Em)
{
    public override string ToString() => $"<sessao appid={AppId} fonte={Fonte} em={Em:yyyy-MM-dd HH:mm:ss}>";
}

/// <summary>
/// 1.0.41: os cabeçalhos que o próprio SDK manda nas chamadas REST (copiados quando capturados). Não são segredo,
/// mas também não vão a log: ficam em memória junto da credencial.
/// </summary>
public sealed record CabecalhosDoSdk(string? SbUserAgent, string? SendBird, string? SbSdkUserAgent, string? Accept);

/// <summary>
/// 1.0.41: A SESSÃO DO SENDBIRD, PURA E SÓ EM MEMÓRIA. Três fontes com prioridade cabeçalho &gt; LOGI &gt; URL, e
/// dentro da mesma fonte a mais recente vence:
///  (a) cabeçalho: Network.requestWillBeSent de uma chamada do SDK a api-&lt;appid&gt;.sendbird.com (Session-Key, App-Id);
///  (b) LOGI: o quadro de login recebido num socket do Sendbird (campo "key");
///  (c) URL: o socket do Sendbird aberto com key= na query (as reconexões do SDK).
/// O access_token NUNCA vira chave de sessão. A sessão só vale se o user_id dela for o da loja (a conexão do
/// entregador tem outro usuário). Nunca lança. Os métodos estáticos são puros; a instância guarda o que a tela
/// ofereceu e o que foi invalidado (401/403).
/// </summary>
public sealed class SessaoSendbird
{
    public const string FonteCabecalho = "cabecalho";
    public const string FonteLogi = "logi";
    public const string FonteUrl = "url";
    public const string FonteNenhuma = "nenhuma";

    public static readonly IReadOnlySet<string> Fontes = new HashSet<string>(StringComparer.Ordinal)
    {
        FonteCabecalho, FonteLogi, FonteUrl, FonteNenhuma,
    };

    private const string NomeSessionKey = "Session-Key";
    private const string NomeAppId = "App-Id";

    /// <summary>cabecalho 3, logi 2, url 1, o resto 0.</summary>
    public static int Prioridade(string? fonte) => fonte switch
    {
        FonteCabecalho => 3,
        FonteLogi => 2,
        FonteUrl => 1,
        _ => 0,
    };

    // ── onde o Sendbird mora ─────────────────────────────────────────────────

    /// <summary>A URL é de uma chamada REST do SDK: host EXATAMENTE api-&lt;appid&gt;.sendbird.com (nada antes, nada depois).</summary>
    public static bool EhApiDoSendbird(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
        var h = u.Host.ToLowerInvariant();
        return h.StartsWith("api-", StringComparison.Ordinal) && h.EndsWith(".sendbird.com", StringComparison.Ordinal)
               && h.Length > "api-.sendbird.com".Length
               && h.IndexOf('.') == h.IndexOf(".sendbird.com", StringComparison.Ordinal);
    }

    /// <summary>O app id pelo host (ws-&lt;appid&gt;.sendbird.com ou api-&lt;appid&gt;.sendbird.com), em minúsculas; null fora disso.</summary>
    public static string? AppIdDoHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return null;
        var h = u.Host.ToLowerInvariant();
        if (!h.EndsWith(".sendbird.com", StringComparison.Ordinal)) return null;
        var corpo = h[..^".sendbird.com".Length];
        foreach (var prefixo in new[] { "ws-", "api-" })
            if (corpo.StartsWith(prefixo, StringComparison.Ordinal) && corpo.Length > prefixo.Length && !corpo[prefixo.Length..].Contains('.'))
                return corpo[prefixo.Length..];
        return null;
    }

    /// <summary>O app id de uma URL do Sendbird: o parâmetro ai=, senão o host.</summary>
    public static string? AppIdDaUrl(string? url)
    {
        var ai = QuadroSendbird.AppIdDaUrl(url);
        return !string.IsNullOrWhiteSpace(ai) ? ai : AppIdDoHost(url);
    }

    /// <summary>Um app id com cara de app id (letras, dígitos e hífen, até 64).</summary>
    public static bool AppIdValido(string? s)
        => !string.IsNullOrWhiteSpace(s) && s.Length <= 64 && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    // ── as três fontes (puras) ───────────────────────────────────────────────

    /// <summary>
    /// (c) A URL do socket do Sendbird: user_id e key= na query. Sem key= (a primeira conexão, só com
    /// access_token) não há credencial. Fonte "url".
    /// </summary>
    public static CredencialSendbird? DaUrlDoSocket(string? url, DateTime agora)
    {
        try
        {
            if (!QuadroSendbird.EhSocketDoSendbird(url)) return null;
            var chave = QuadroSendbird.ChaveDaSessaoDaUrl(url);
            if (!ChaveValida(chave)) return null;
            var appId = AppIdDaUrl(url);
            if (!AppIdValido(appId)) return null;
            return new CredencialSendbird(appId!, QuadroSendbird.UserIdDaUrl(url), chave!, FonteUrl, agora);
        }
        catch { return null; }
    }

    /// <summary>
    /// (b) O quadro LOGI recebido: "key" (a chave de sessão) e, quando vem, "user_id"; senão o user_id da URL
    /// daquela conexão. O app id vem da URL da conexão. Sem key, não é credencial. Fonte "logi".
    /// </summary>
    public static CredencialSendbird? DoLogi(string? payload, string? userIdDaUrl, string? appIdDaConexao, DateTime agora)
    {
        try
        {
            if (QuadroSendbird.Comando(payload) != "LOGI" || payload!.Length < 6) return null;
            using var doc = JsonDocument.Parse(payload.AsMemory(4).TrimEnd('\n').TrimEnd('\r'));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var chave = Texto(r, "key");
            if (!ChaveValida(chave)) return null;
            var usuario = Texto(r, "user_id") ?? (r.TryGetProperty("user_id", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetRawText() : null);
            if (string.IsNullOrWhiteSpace(usuario)) usuario = userIdDaUrl;
            if (!AppIdValido(appIdDaConexao)) return null;
            return new CredencialSendbird(appIdDaConexao!, string.IsNullOrWhiteSpace(usuario) ? null : usuario.Trim(), chave!, FonteLogi, agora);
        }
        catch { return null; }
    }

    /// <summary>
    /// (a) Os cabeçalhos de uma chamada do SDK a api-&lt;appid&gt;.sendbird.com (nomes sem distinguir caixa):
    /// Session-Key e App-Id (senão o host); e os do SDK para copiar (SB-User-Agent, SendBird, SB-SDK-User-Agent,
    /// Accept). Host que não é exatamente api-&lt;appid&gt;.sendbird.com: null. Fonte "cabecalho" (sem user_id).
    /// </summary>
    public static (CredencialSendbird Credencial, CabecalhosDoSdk Cabecalhos)? DosCabecalhos(string? url, JsonElement headers, DateTime agora)
    {
        try
        {
            if (!EhApiDoSendbird(url) || headers.ValueKind != JsonValueKind.Object) return null;
            string? chave = null, appId = null, sbUa = null, sendbird = null, sdkUa = null, accept = null;
            foreach (var p in headers.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String) continue;
                var v = p.Value.GetString();
                if (p.Name.Equals(NomeSessionKey, StringComparison.OrdinalIgnoreCase)) chave = v;
                else if (p.Name.Equals(NomeAppId, StringComparison.OrdinalIgnoreCase)) appId = v;
                else if (p.Name.Equals("SB-User-Agent", StringComparison.OrdinalIgnoreCase)) sbUa = v;
                else if (p.Name.Equals("SendBird", StringComparison.OrdinalIgnoreCase)) sendbird = v;
                else if (p.Name.Equals("SB-SDK-User-Agent", StringComparison.OrdinalIgnoreCase)) sdkUa = v;
                else if (p.Name.Equals("Accept", StringComparison.OrdinalIgnoreCase)) accept = v;
            }
            if (!ChaveValida(chave)) return null;
            if (!AppIdValido(appId)) appId = AppIdDoHost(url);
            if (!AppIdValido(appId)) return null;
            return (new CredencialSendbird(appId!, null, chave!.Trim(), FonteCabecalho, agora),
                new CabecalhosDoSdk(Limpo(sbUa), Limpo(sendbird), Limpo(sdkUa), Limpo(accept)));
        }
        catch { return null; }
    }

    /// <summary>
    /// A melhor credencial entre as candidatas: prioridade cabeçalho &gt; logi &gt; url; na mesma fonte, a mais
    /// recente. Com a loja conhecida, a candidata cujo user_id é OUTRO (o entregador) é ignorada; a que não diz
    /// user_id (cabeçalho) ganha o da loja. Sem a loja conhecida, vale o user_id da própria candidata; a que não
    /// tem nenhum não serve para a lista de canais. Nunca lança.
    /// </summary>
    public static CredencialSendbird? Melhor(IEnumerable<CredencialSendbird?> candidatas, string? usuarioDaLoja)
    {
        try
        {
            var loja = string.IsNullOrWhiteSpace(usuarioDaLoja) ? null : usuarioDaLoja.Trim();
            CredencialSendbird? melhor = null;
            foreach (var c in candidatas)
            {
                if (c is null || !ChaveValida(c.SessionKey) || !AppIdValido(c.AppId)) continue;
                if (loja is not null && c.UserId is not null && !string.Equals(c.UserId.Trim(), loja, StringComparison.Ordinal)) continue;
                if (loja is null && string.IsNullOrWhiteSpace(c.UserId)) continue;
                if (melhor is null || Prioridade(c.Fonte) > Prioridade(melhor.Fonte)
                    || (Prioridade(c.Fonte) == Prioridade(melhor.Fonte) && c.Em > melhor.Em))
                    melhor = c;
            }
            if (melhor is null) return null;
            return loja is not null ? melhor with { UserId = loja } : melhor;
        }
        catch { return null; }
    }

    /// <summary>Uma chave de sessão com cara de chave: 8 ou mais caracteres sem espaço, e nunca um JWT (o access_token).</summary>
    public static bool ChaveValida(string? chave)
        => !string.IsNullOrWhiteSpace(chave) && chave.Trim().Length >= 8 && !chave.Any(char.IsWhiteSpace)
           && !ChatCaptura.ParaceJwt(chave) && !chave.StartsWith("eyJ", StringComparison.Ordinal);

    private static string? Texto(JsonElement o, string k)
        => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static string? Limpo(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Length > 200 ? null : t;
    }

    // ── a instância: o que a tela ofereceu ───────────────────────────────────

    private readonly object _trava = new();
    /// <summary>Por (fonte, user_id): a conexão do entregador (outro usuário) nunca sobrescreve a da loja na mesma fonte.</summary>
    private readonly Dictionary<string, CredencialSendbird> _porFonte = new(StringComparer.Ordinal);
    private readonly List<string> _invalidadas = new();
    private CabecalhosDoSdk? _cabecalhos;
    private string? _motivoDaInvalidacao;
    private const int TetoDeCredenciais = 12;

    private static string ChaveDaFonte(CredencialSendbird c) => c.Fonte + "|" + (c.UserId ?? "");

    /// <summary>
    /// Oferece uma credencial vista pela tela. Devolve true quando ela NASCEU ou MUDOU DE VALOR (a dupla chave +
    /// user_id ainda não era conhecida): é o gatilho da varredura em 2 s. A mesma chave pela mesma fonte de
    /// novo, ou a mesma chave por outra fonte (o LOGI depois da URL), não é mudança. Uma chave já invalidada
    /// (401/403) não entra de novo.
    /// </summary>
    public bool Oferecer(CredencialSendbird? c)
    {
        if (c is null || !ChaveValida(c.SessionKey) || !Fontes.Contains(c.Fonte) || c.Fonte == FonteNenhuma) return false;
        lock (_trava)
        {
            if (_invalidadas.Contains(c.SessionKey, StringComparer.Ordinal)) return false;
            var conhecida = _porFonte.Values.Any(x => x.SessionKey == c.SessionKey && x.UserId == c.UserId);
            _porFonte[ChaveDaFonte(c)] = c;
            while (_porFonte.Count > TetoDeCredenciais)
                _porFonte.Remove(_porFonte.OrderBy(x => x.Value.Em).First().Key);
            return !conhecida;
        }
    }

    /// <summary>Os cabeçalhos do SDK capturados (os últimos vencem).</summary>
    public void OferecerCabecalhos(CabecalhosDoSdk? cab)
    {
        if (cab is null) return;
        lock (_trava) _cabecalhos = cab;
    }

    public CabecalhosDoSdk? Cabecalhos { get { lock (_trava) return _cabecalhos; } }

    /// <summary>
    /// A sessão não vale mais (401/403, ou código 4003xx do Sendbird): as credenciais de agora saem e a chave
    /// invalidada não volta a entrar; só uma chave nova (valor diferente) reabre. O motivo é só diagnóstico.
    /// </summary>
    public void Invalidar(string? motivo)
    {
        lock (_trava)
        {
            foreach (var c in _porFonte.Values)
                if (!_invalidadas.Contains(c.SessionKey, StringComparer.Ordinal)) _invalidadas.Add(c.SessionKey);
            while (_invalidadas.Count > 20) _invalidadas.RemoveAt(0);
            _porFonte.Clear();
            _motivoDaInvalidacao = ConversaRaspadinha.NomeSeguroDoDiag(motivo) ?? "invalidada";
        }
    }

    /// <summary>O motivo da última invalidação (nome curto e seguro), ou null.</summary>
    public string? MotivoDaInvalidacao { get { lock (_trava) return _motivoDaInvalidacao; } }

    /// <summary>A melhor credencial de agora para a loja (ver <see cref="Melhor"/>), ou null.</summary>
    public CredencialSendbird? Atual(string? usuarioDaLoja)
    {
        lock (_trava) return MelhorSemTrava(usuarioDaLoja);
    }

    /// <summary>A fonte da credencial de agora: cabecalho, logi, url ou nenhuma.</summary>
    public string Fonte(string? usuarioDaLoja) => Atual(usuarioDaLoja)?.Fonte ?? FonteNenhuma;

    /// <summary>Só a bateria: esquece tudo.</summary>
    public void Zerar()
    {
        lock (_trava)
        {
            _porFonte.Clear();
            _invalidadas.Clear();
            _cabecalhos = null;
            _motivoDaInvalidacao = null;
        }
    }

    private CredencialSendbird? MelhorSemTrava(string? usuarioDaLoja)
        => Melhor(_porFonte.Values.Where(c => !_invalidadas.Contains(c.SessionKey, StringComparer.Ordinal)), usuarioDaLoja);
}
