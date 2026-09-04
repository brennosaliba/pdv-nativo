using System.Text;
using System.Text.Json;

namespace Pdv.Nucleo;

/// <summary>Uma mensagem de chat já normalizada (modelo do "nativo depois").</summary>
public sealed record MensagemChat(
    string? ConversaId, string? Autor, string Texto, DateTimeOffset? Quando, bool Minha);

/// <summary>Uma conversa (agrupamento de mensagens por id).</summary>
public sealed record ConversaChat(string Id, IReadOnlyList<MensagemChat> Mensagens);

/// <summary>Formato do token do /chat/v1.0/auth (JWT HS256 {u,v,e}), sem valores secretos.</summary>
public sealed record FormatoToken(bool EhJwt, int Segmentos, string? UserIdMascarado, long? V, long? E, DateTimeOffset? Expira);

/// <summary>
/// GROUNDWORK do chat nativo. Recebe os quadros (frames) do WebSocket capturados
/// pelo CDP dentro do próprio WebView2 e os normaliza para um modelo (Conversa,
/// Mensagem) por FUNÇÃO PURA — hoje só alimenta o diagnóstico, amanhã alimenta a
/// lista nativa.
///
/// ⚠️ SEGREDO: o token capturado é a sessão do dono. Tudo aqui MASCARA antes de
/// escrever qualquer coisa em disco — nunca o token em claro, nunca enviado para
/// lugar nenhum. Fica na máquina.
///
/// Como o formato real do iFood só se vê com a página logada, o normalizador é
/// LENIENTE de propósito: tenta vários nomes de campo conhecidos e devolve null
/// quando o quadro não parece uma mensagem (ack, pulso, controle). Assim ele já
/// funciona espelhando o formato conhecido e não quebra quando o iFood mexe.
/// </summary>
public static class ChatCaptura
{
    // nomes de campo candidatos (o formato real confirma quais valem)
    private static readonly string[] CamposTexto = { "text", "message", "content", "body", "texto", "msg" };
    private static readonly string[] CamposAutor = { "author", "sender", "senderId", "from", "userId", "autor", "user" };
    private static readonly string[] CamposQuando = { "timestamp", "ts", "createdAt", "created_at", "date", "sentAt", "time" };
    private static readonly string[] CamposConversa = { "conversationId", "chatId", "threadId", "conversation", "chat", "roomId", "groupId" };

    // chaves cujo VALOR é segredo e precisa sumir do diagnóstico
    private static readonly string[] ChavesSensiveis =
        { "token", "access_token", "accesstoken", "authorization", "auth", "jwt", "secret", "refresh_token", "id_token", "apikey", "api_key" };

    // ── normalização (o coração testável) ───────────────────────────────────

    /// <summary>
    /// Tenta ler UMA mensagem de um quadro JSON. Devolve null se o quadro não
    /// parece uma mensagem de chat. Procura recursivamente (o texto costuma vir
    /// aninhado em payload/data). Nunca lança.
    /// </summary>
    public static MensagemChat? NormalizarFrame(string? frameJson)
    {
        if (string.IsNullOrWhiteSpace(frameJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(frameJson);
            return DoElemento(doc.RootElement);
        }
        catch { return null; }
    }

    private static MensagemChat? DoElemento(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var texto = PrimeiroTexto(e, CamposTexto);
            if (texto is not null)
            {
                var autor = PrimeiroTexto(e, CamposAutor) ?? PrimeiroNumeroComoTexto(e, CamposAutor);
                var conversa = PrimeiroTexto(e, CamposConversa) ?? PrimeiroNumeroComoTexto(e, CamposConversa);
                var quando = PrimeiroInstante(e, CamposQuando);
                var minha = LerBool(e, "mine") ?? LerBool(e, "isMine") ?? LerBool(e, "fromMe") ?? LerBool(e, "outgoing") ?? false;
                return new MensagemChat(conversa, autor, texto, quando, minha);
            }
            // desce em payload/data/message aninhados
            foreach (var prop in e.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    var achou = DoElemento(prop.Value);
                    if (achou is not null) return achou;
                }
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
            {
                var achou = DoElemento(item);
                if (achou is not null) return achou;
            }
        }
        return null;
    }

    /// <summary>Agrupa mensagens em conversas por id (as sem id caem em "sem-id").</summary>
    public static IReadOnlyList<ConversaChat> AgruparConversas(IEnumerable<MensagemChat> mensagens)
        => mensagens
            .GroupBy(m => m.ConversaId ?? "sem-id")
            .Select(g => new ConversaChat(g.Key, g.ToList()))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

    private static string? PrimeiroTexto(JsonElement obj, string[] chaves)
    {
        foreach (var c in chaves)
            if (obj.TryGetProperty(c, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        return null;
    }

    private static string? PrimeiroNumeroComoTexto(JsonElement obj, string[] chaves)
    {
        foreach (var c in chaves)
            if (obj.TryGetProperty(c, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.ToString();
        return null;
    }

    private static bool? LerBool(JsonElement obj, string chave)
        => obj.TryGetProperty(chave, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    private static DateTimeOffset? PrimeiroInstante(JsonElement obj, string[] chaves)
    {
        foreach (var c in chaves)
        {
            if (!obj.TryGetProperty(c, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
                return EpocaFlexivel(n);
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (long.TryParse(s, out var ms)) return EpocaFlexivel(ms);
                if (DateTimeOffset.TryParse(s, out var dto)) return dto;
            }
        }
        return null;
    }

    /// <summary>Epoch em segundos ou milissegundos (o dígito diz qual).</summary>
    private static DateTimeOffset EpocaFlexivel(long n)
        => n > 100_000_000_000L
            ? DateTimeOffset.FromUnixTimeMilliseconds(n)
            : DateTimeOffset.FromUnixTimeSeconds(n);

    // ── JWT do /chat/v1.0/auth (só o FORMATO, nunca o valor) ─────────────────

    /// <summary>
    /// Lê o FORMATO do token sem verificar assinatura (o dono não confia em nós
    /// para isso, nem precisa). Extrai o shape do payload {u,v,e}, mascarando o
    /// userId. v e e (versão e expiração) não são segredo — ajudam o diagnóstico.
    /// </summary>
    public static FormatoToken? LerFormatoToken(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return null;
        var partes = jwt.Split('.');
        if (partes.Length != 3)
            return new FormatoToken(false, partes.Length, null, null, null, null);
        try
        {
            var payload = Encoding.UTF8.GetString(Base64UrlDecode(partes[1]));
            using var doc = JsonDocument.Parse(payload);
            var raiz = doc.RootElement;
            string? u = raiz.TryGetProperty("u", out var uv) ? uv.ToString() : null;
            long? v = raiz.TryGetProperty("v", out var vv) && vv.TryGetInt64(out var vl) ? vl : null;
            long? e = raiz.TryGetProperty("e", out var ev) && ev.TryGetInt64(out var el) ? el : null;
            DateTimeOffset? exp = e is not null ? EpocaFlexivel(e.Value) : null;
            return new FormatoToken(true, 3, u is null ? null : "XXXX", v, e, exp);
        }
        catch { return new FormatoToken(true, 3, null, null, null, null); }
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }

    // ── mascaramento para o diagnóstico ──────────────────────────────────────

    /// <summary>Troca o valor por XXXX (o dono precisa do FORMATO, não do segredo).</summary>
    public static string Mascarar(string? _) => "XXXX";

    /// <summary>Um texto "cheira" a JWT? (três segmentos base64url).</summary>
    public static bool ParaceJwt(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        var p = s.Split('.');
        return p.Length == 3 && p[0].Length >= 8 && p[1].Length >= 8
            && p.All(x => x.Length > 0 && x.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
    }

    /// <summary>
    /// Reescreve um JSON MASCARANDO qualquer valor sensível: chaves de token e
    /// qualquer string com cara de JWT viram "XXXX". Preserva as CHAVES e a
    /// estrutura — que é o que o dono precisa me mandar. JSON inválido devolve
    /// um aviso, nunca o texto cru (poderia carregar segredo).
    /// </summary>
    public static string MascararJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "(vazio)";
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using (var wr = new Utf8JsonWriter(ms))
                EscreverMascarado(doc.RootElement, wr, chavePai: null);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch { return "(quadro não-JSON; omitido por segurança)"; }
    }

    private static void EscreverMascarado(JsonElement e, Utf8JsonWriter w, string? chavePai)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject())
                {
                    w.WritePropertyName(p.Name);
                    EscreverMascarado(p.Value, w, p.Name);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in e.EnumerateArray())
                    EscreverMascarado(item, w, chavePai);
                w.WriteEndArray();
                break;
            case JsonValueKind.String:
                var s = e.GetString();
                var sensivel = chavePai is not null
                    && ChavesSensiveis.Contains(chavePai.ToLowerInvariant());
                w.WriteStringValue(sensivel || ParaceJwt(s) ? "XXXX" : s);
                break;
            case JsonValueKind.Number:
                w.WriteRawValue(e.GetRawText());
                break;
            case JsonValueKind.True: w.WriteBooleanValue(true); break;
            case JsonValueKind.False: w.WriteBooleanValue(false); break;
            default: w.WriteNullValue(); break;
        }
    }

    /// <summary>
    /// Mascara a URL: mantém host, caminho e os NOMES dos parâmetros; troca o
    /// valor de parâmetros sensíveis (token, apikey…) por XXXX. Serve para a URL
    /// do WebSocket, que às vezes leva o token na query string.
    /// </summary>
    public static string MascararUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "(vazio)";
        var i = url.IndexOf('?');
        if (i < 0) return url;
        var baseUrl = url[..i];
        var query = url[(i + 1)..];
        var partes = query.Split('&').Select(par =>
        {
            var eq = par.IndexOf('=');
            if (eq < 0) return par;
            var nome = par[..eq];
            var sensivel = ChavesSensiveis.Contains(nome.ToLowerInvariant()) || ParaceJwt(par[(eq + 1)..]);
            return sensivel ? nome + "=XXXX" : par;
        });
        return baseUrl + "?" + string.Join("&", partes);
    }

    /// <summary>Descreve o SHAPE de um quadro: nomes de campo de topo e seus tipos.</summary>
    public static string DescreverShape(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "(vazio)";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return doc.RootElement.ValueKind.ToString().ToLowerInvariant();
            var campos = doc.RootElement.EnumerateObject()
                .Select(p => $"{p.Name}:{p.Value.ValueKind.ToString().ToLowerInvariant()}");
            return "{ " + string.Join(", ", campos) + " }";
        }
        catch { return "(não-JSON)"; }
    }

    /// <summary>
    /// Acumulador VIVO (em memória) do que o CDP capturou no WebView2 do chat.
    /// A tela alimenta; ele produz o diagnóstico já MASCARADO para o dono me
    /// mandar. O token fica só aqui, em RAM — não é gravado em claro em lugar
    /// nenhum. Limita quantos quadros guarda para não crescer sem fim.
    /// </summary>
    public sealed class Acumulador
    {
        private const int TetoFrames = 40;
        private readonly object _trava = new();
        private readonly HashSet<string> _wsUrls = new();
        private readonly List<(string Payload, bool Enviado)> _frames = new();
        private string? _tokenRaw;          // SEGREDO: só em memória, nunca gravado em claro
        private string? _authRespostaRaw;   // idem

        /// <summary>Token capturado (em memória), para o futuro parser nativo. Nunca logar.</summary>
        public string? TokenEmMemoria { get { lock (_trava) return _tokenRaw; } }

        public void RegistrarWebSocket(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            lock (_trava) _wsUrls.Add(url);
        }

        public void RegistrarToken(string? jwt)
        {
            if (string.IsNullOrWhiteSpace(jwt)) return;
            lock (_trava) _tokenRaw = jwt;
        }

        public void RegistrarAuthResposta(string? corpo)
        {
            if (string.IsNullOrWhiteSpace(corpo)) return;
            lock (_trava) _authRespostaRaw = corpo;
        }

        public void RegistrarFrame(string? payload, bool enviado)
        {
            if (string.IsNullOrWhiteSpace(payload)) return;
            lock (_trava)
            {
                if (_frames.Count >= TetoFrames) return;
                _frames.Add((payload, enviado));
            }
        }

        /// <summary>Mensagens normalizadas dos quadros capturados (para o "nativo depois").</summary>
        public IReadOnlyList<MensagemChat> Mensagens()
        {
            List<(string, bool)> copia;
            lock (_trava) copia = _frames.ToList();
            return copia.Select(f => NormalizarFrame(f.Item1)).Where(m => m is not null).Select(m => m!).ToList();
        }

        /// <summary>
        /// Monta o texto do diagnóstico, TUDO mascarado. É o que o dono me manda
        /// para eu fechar o parser nativo: URL do WS, formato do token e o shape
        /// dos quadros com um exemplo mascarado de cada.
        /// </summary>
        public string MontarDiagnostico()
        {
            List<(string Payload, bool Enviado)> frames;
            List<string> urls;
            string? token, auth;
            lock (_trava)
            {
                frames = _frames.ToList();
                urls = _wsUrls.ToList();
                token = _tokenRaw;
                auth = _authRespostaRaw;
            }

            var sb = new StringBuilder();
            sb.AppendLine("== DIAGNOSTICO DO CHAT DO iFOOD (PDV nativo) ==");
            sb.AppendLine("Gerado em: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            sb.AppendLine("TODOS os tokens e segredos estao MASCARADOS (XXXX). Pode me mandar este arquivo.");
            sb.AppendLine();

            sb.AppendLine("-- WebSocket --");
            if (urls.Count == 0) sb.AppendLine("(nenhuma URL de WebSocket capturada ainda)");
            foreach (var u in urls) sb.AppendLine("  " + MascararUrl(u));
            sb.AppendLine();

            sb.AppendLine("-- Token do /chat/v1.0/auth --");
            var fmt = LerFormatoToken(token);
            if (fmt is null) sb.AppendLine("(token ainda nao capturado)");
            else
            {
                sb.AppendLine("  valor: XXXX (nunca gravado em claro)");
                sb.AppendLine($"  jwt: {fmt.EhJwt}  segmentos: {fmt.Segmentos}");
                sb.AppendLine($"  claim u (userId): {fmt.UserIdMascarado ?? "(ausente)"}");
                sb.AppendLine($"  claim v (versao): {(fmt.V?.ToString() ?? "(ausente)")}");
                sb.AppendLine($"  claim e (expira): {(fmt.E?.ToString() ?? "(ausente)")}"
                    + (fmt.Expira is not null ? $"  => {fmt.Expira:yyyy-MM-dd HH:mm:ss zzz}" : ""));
            }
            if (auth is not null)
            {
                sb.AppendLine("  resposta do auth (mascarada):");
                sb.AppendLine("    shape: " + DescreverShape(auth));
                sb.AppendLine("    " + MascararJson(auth));
            }
            sb.AppendLine();

            sb.AppendLine($"-- Quadros do WebSocket ({frames.Count} capturados) --");
            var vistos = new HashSet<string>();
            foreach (var (payload, enviado) in frames)
            {
                var shape = DescreverShape(payload);
                if (!vistos.Add(shape)) continue;   // um exemplo por SHAPE distinto
                sb.AppendLine($"  [{(enviado ? "ENVIADO" : "RECEBIDO")}] shape: {shape}");
                sb.AppendLine("    " + MascararJson(payload));
                var m = NormalizarFrame(payload);
                if (m is not null)
                    sb.AppendLine($"    normalizado -> conversa={m.ConversaId ?? "?"} autor={m.Autor ?? "?"} "
                        + $"quando={(m.Quando?.ToString("HH:mm:ss") ?? "?")} minha={m.Minha} texto=(len {m.Texto.Length})");
                sb.AppendLine();
            }

            var msgs = frames.Select(f => NormalizarFrame(f.Payload)).Where(m => m is not null).Select(m => m!).ToList();
            var conversas = AgruparConversas(msgs);
            sb.AppendLine($"-- Resumo normalizado: {msgs.Count} mensagem(ns) em {conversas.Count} conversa(s) --");

            return sb.ToString();
        }
    }
}
