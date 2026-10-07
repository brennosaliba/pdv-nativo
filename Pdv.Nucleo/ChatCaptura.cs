using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pdv.Nucleo;

/// <summary>Uma mensagem de chat já normalizada (modelo do "nativo depois").</summary>
/// <summary>
/// Uma fala do chat. <c>Minha</c> é TRI-ESTADO de propósito (22/09/2026): true = a loja falou,
/// false = o protocolo disse que não foi a loja, null = NÃO SE SABE de que lado veio.
///
/// Antes disto o campo era <c>bool</c> e o desconhecido virava false, ou seja "é do cliente".
/// Num caixa reiniciado com a sessão do Gestor já quente, o token do chat não é refeito, o id do
/// usuário desta loja fica desconhecido o dia inteiro e TODO quadro recebido com texto passava a
/// contar como fala do cliente: inclusive o eco do que a própria loja manda. A regra do DOM já
/// era a certa (balão sem lado decidido fica de fora) e agora vale para o WebSocket também.
/// </summary>
public sealed record MensagemChat(
    string? ConversaId, string? Autor, string Texto, DateTimeOffset? Quando, bool? Minha);

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
/// escrever qualquer coisa em disco: nunca o token em claro, nunca enviado para
/// lugar nenhum. Fica na máquina. O mascaramento é LISTA BRANCA (some por nome
/// suspeito E por formato de segredo) e o texto final ainda passa por uma
/// varredura de rede de segurança, para que caminho novo não fure a promessa.
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
    private static readonly string[] CamposConversa = { "conversationId", "chatId", "threadId", "conversation", "chat", "roomId", "groupId", "channel_url", "channelUrl", "channel" };

    // Quem escreveu costuma vir num OBJETO ao lado do texto ({"user":{"user_id":"..."}}), e não
    // como campo solto. Sem isto a mensagem chega sem autor, e sem autor não dá para separar a
    // fala da loja da fala do cliente (22/09/2026, código da raspadinha no chat).
    private static readonly string[] ObjetosAutor = { "user", "sender", "author", "from", "participant" };
    // guest_id PRIMEIRO (07/10/2026): é onde o Sendbird do iFood põe o id de quem escreveu. Sem ele
    // o autor caía no "nickname"/"name", que é o nome da pessoa, e a autoria virava palpite.
    private static readonly string[] CamposIdDoAutor = { "guest_id", "user_id", "userId", "id", "nickname", "name" };

    // ── a regra do mascaramento (LISTA BRANCA, não lista negra) ─────────────
    //
    // ⚠️ INCIDENTE REAL: a regra antiga era por NOME EXATO conhecido de parâmetro.
    // Pegou "token=" (userpilot) e deixou passar, em claro, dentro da query do
    // WebSocket do firefly, um JWT inteiro em x-firefly-access-key e a assinatura
    // AWS em x-amz-customauthorizer-signature. Nome desconhecido = segredo vazado.
    //
    // Agora sai TUDO que (a) tem NOME suspeito (por PEDAÇO do nome, não exato) ou
    // (b) tem CARA de segredo (JWT ou string opaca longa), venha com o nome que
    // vier. Fica só o que tem valor de diagnóstico e não é segredo: host, caminho,
    // NOMES de parâmetro/campo e identificadores curtos e públicos.

    // pedaços de NOME (de parâmetro de query ou de campo JSON) que denunciam segredo
    private static readonly string[] PedacosSensiveis =
    {
        "token", "key", "secret", "signature", "sig", "auth", "credential",
        "password", "senha", "session", "access",
    };

    // JWT: três segmentos base64url, o primeiro começando em eyJ (o "{" do header)
    private const string PadraoJwt = @"eyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]*";

    // uma URL inteira dentro de um texto qualquer (mascarada por parâmetro, para
    // preservar host, caminho e nomes)
    private const string PadraoUrl = @"[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s""'<>\\)\]}]+";

    // string opaca longa: 40+ caracteres de base64/hex/percent-encoded sem espaço.
    // Sem "=" e sem "." de propósito: assim a varredura não engole o NOME antes do
    // "=" nem o host/caminho de uma URL.
    private const string PadraoOpaco = @"[A-Za-z0-9%+/_-]{40,}";

    private static readonly Regex RxJwt = new(PadraoJwt, RegexOptions.Compiled);
    private static readonly Regex RxUrlOuOpaco = new(
        "(?<url>" + PadraoUrl + ")|(?<opaco>" + PadraoOpaco + ")", RegexOptions.Compiled);

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
        catch { /* pode ser JSON atrás de um prefixo de comando: ver abaixo */ }

        // QUADRO COM PREFIXO DE COMANDO (22/09/2026). Vários protocolos de chat mandam o
        // comando antes do corpo ("MESG{...}", "42[...]", "a[...]"), e aí o JSON.Parse do
        // quadro inteiro falha e a mensagem some. Tentar de novo a partir do primeiro "{"
        // ou "[" custa nada e não relaxa nada: texto sem JSON dentro continua devolvendo
        // null, e quadro de controle continua sem virar mensagem.
        try
        {
            var i = frameJson.IndexOfAny(new[] { '{', '[' });
            if (i <= 0) return null;
            using var doc = JsonDocument.Parse(frameJson[i..]);
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
                var autor = PrimeiroTexto(e, CamposAutor) ?? PrimeiroNumeroComoTexto(e, CamposAutor)
                            ?? AutorAninhado(e);
                var conversa = PrimeiroTexto(e, CamposConversa) ?? PrimeiroNumeroComoTexto(e, CamposConversa);
                var quando = PrimeiroInstante(e, CamposQuando);
                // SEM `?? false`: quadro que não diz de que lado veio fica com o lado DESCONHECIDO,
                // e quem decide o que fazer com isso é o ChatRaspadinha.DoCliente. Ver MensagemChat.
                var minha = LerBool(e, "mine") ?? LerBool(e, "isMine") ?? LerBool(e, "fromMe") ?? LerBool(e, "outgoing");
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

    /// <summary>
    /// Quem escreveu, quando ele vem dentro de um objeto ao lado do texto:
    /// <c>{"message":"...","user":{"user_id":"cliente-77"}}</c>. Um nível só de propósito, para
    /// não sair pescando id em qualquer canto do quadro.
    /// </summary>
    private static string? AutorAninhado(JsonElement obj)
    {
        foreach (var nome in ObjetosAutor)
        {
            if (!obj.TryGetProperty(nome, out var o) || o.ValueKind != JsonValueKind.Object) continue;
            var id = PrimeiroTexto(o, CamposIdDoAutor) ?? PrimeiroNumeroComoTexto(o, CamposIdDoAutor);
            if (id is not null) return id;
        }
        return null;
    }

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

    /// <summary>
    /// O claim <c>u</c> do token, EM CLARO (22/09/2026, código da raspadinha no chat). É o id do
    /// usuário desta loja no chat do iFood, e serve para uma pergunta só: a fala é da LOJA ou do
    /// CLIENTE? Sem ela, a mensagem que a própria loja escreve poderia virar chamada da borda.
    ///
    /// ⚠️ Este valor NUNCA é gravado, logado nem enviado. Ele vive em memória e é usado só para
    /// comparar com o autor do quadro. O diagnóstico continua vendo o mascarado
    /// (<see cref="LerFormatoToken"/> devolve "XXXX"), e a rede de segurança do
    /// <see cref="MascararTexto"/> continua valendo para tudo que chega ao disco.
    /// </summary>
    public static string? UserIdDoToken(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return null;
        var partes = jwt.Split('.');
        if (partes.Length != 3) return null;
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Base64UrlDecode(partes[1])));
            if (!doc.RootElement.TryGetProperty("u", out var u)) return null;
            var s = u.ValueKind == JsonValueKind.String ? u.GetString() : u.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
        catch { return null; }
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
    /// O NOME (de parâmetro de query ou de campo) denuncia segredo? A conta é por
    /// PEDAÇO do nome: "x-firefly-access-key" contém "access" e "key", e
    /// "x-amz-customauthorizer-signature" contém "auth" e "signature".
    /// </summary>
    public static bool NomeSensivel(string? nome)
    {
        if (string.IsNullOrEmpty(nome)) return false;
        var n = nome.ToLowerInvariant();
        foreach (var p in PedacosSensiveis)
            if (n.Contains(p, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Uma string opaca longa (40+ de base64/hex/percent-encoded, sem espaço).
    /// Serve para valores INTEIROS (parâmetro de query, segmento de caminho), onde
    /// o começo e o fim são conhecidos.
    /// </summary>
    public static bool ParaceSegredoOpaco(string? s)
    {
        if (s is null || s.Length < 40) return false;
        foreach (var c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '%' or '+' or '/' or '=' or '_' or '-' or '.' or '~'))
                return false;
        return true;
    }

    /// <summary>Um VALOR tem cara de segredo, independentemente do nome que carrega?</summary>
    public static bool ValorSensivel(string? valor)
        => !string.IsNullOrWhiteSpace(valor)
            && (ParaceJwt(valor) || RxJwt.IsMatch(valor) || ParaceSegredoOpaco(valor));

    /// <summary>
    /// REDE DE SEGURANÇA. Varre um TEXTO inteiro e apaga segredo venha de onde
    /// vier (URL, corpo de quadro, cabeçalho, campo nunca visto): primeiro todo
    /// JWT, depois toda URL (mascarada parâmetro a parâmetro, preservando host,
    /// caminho e nomes) e toda string opaca longa. Idempotente: passar duas vezes
    /// dá o mesmo texto.
    /// </summary>
    public static string MascararTexto(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return texto ?? "";
        var semJwt = RxJwt.Replace(texto, "XXXX");
        return RxUrlOuOpaco.Replace(semJwt, m => m.Groups["url"].Success ? MascararUrl(m.Value) : "XXXX");
    }

    /// <summary>
    /// Reescreve um JSON MASCARANDO qualquer valor sensível: campo de nome
    /// suspeito, string com cara de JWT ou segredo opaco, e ainda o segredo
    /// escondido dentro de um valor composto (uma URL, um "Bearer &lt;jwt&gt;"),
    /// tudo vira "XXXX". Preserva as CHAVES e a
    /// estrutura — que é o que o dono precisa me mandar. JSON inválido devolve
    /// um aviso, nunca o texto cru (poderia carregar segredo).
    /// </summary>
    public static string MascararJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "(vazio)";
        // O quadro do Sendbird vem com o comando colado ("MESG{...}"): sem tirar o prefixo, todo
        // quadro de mensagem virava "não-JSON" e o diagnóstico não mostrava nada do que importa.
        var (prefixo, corpo) = SemPrefixoDeComando(json);
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            using var ms = new MemoryStream();
            using (var wr = new Utf8JsonWriter(ms))
                EscreverMascarado(doc.RootElement, wr, chavePai: null);
            return prefixo + Encoding.UTF8.GetString(ms.ToArray());
        }
        catch { return "(quadro não-JSON; omitido por segurança)"; }
    }

    /// <summary>
    /// Separa o comando de 4 letras maiúsculas ("MESG", "ADMM", "LOGI") do JSON que vem colado nele.
    /// Sem prefixo, devolve ("", o texto inteiro). O prefixo não é segredo: são só 4 letras.
    /// </summary>
    public static (string Prefixo, string Json) SemPrefixoDeComando(string texto)
    {
        var cmd = QuadroSendbird.Comando(texto);
        return cmd is not null && texto.Length > 4 && texto[4] is '{' or '['
            ? (cmd, texto[4..])
            : ("", texto);
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
                // nome suspeito ou valor que É um segredo: some inteiro.
                // Valor COMPOSTO (uma URL, um "Bearer <jwt>"): passa pela varredura,
                // que apaga só o segredo e preserva o resto (host, caminho, nomes).
                w.WriteStringValue(
                    NomeSensivel(chavePai) || ParaceJwt(s) || ParaceSegredoOpaco(s)
                        ? "XXXX"
                        : MascararTexto(s));
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
    /// Mascara a URL: mantém host, caminho e os NOMES dos parâmetros (é isso que
    /// tem valor de diagnóstico) e troca por XXXX o VALOR de todo parâmetro de
    /// nome suspeito E de todo valor com cara de segredo, mesmo sob nome nunca
    /// visto. Serve para a URL do WebSocket, que leva o token na query string.
    /// </summary>
    public static string MascararUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "(vazio)";
        var i = url.IndexOf('?');
        var saida = new StringBuilder(MascararCaminho(i < 0 ? url : url[..i]));
        if (i < 0) return saida.ToString();

        var partes = url[(i + 1)..].Split('&').Select(par =>
        {
            var eq = par.IndexOf('=');
            if (eq < 0) return ValorSensivel(par) ? "XXXX" : par;
            var nome = par[..eq];
            return NomeSensivel(nome) || ValorSensivel(par[(eq + 1)..]) ? nome + "=XXXX" : par;
        });
        return saida.Append('?').Append(string.Join("&", partes)).ToString();
    }

    /// <summary>
    /// Segredo às vezes viaja no CAMINHO, não na query. Preserva esquema, host e a
    /// estrutura do caminho; troca por XXXX só o SEGMENTO que é segredo.
    /// </summary>
    private static string MascararCaminho(string semQuery)
    {
        var esquema = semQuery.IndexOf("://", StringComparison.Ordinal);
        var barra = semQuery.IndexOf('/', esquema < 0 ? 0 : esquema + 3);
        if (barra < 0) return semQuery;
        var segmentos = semQuery[barra..].Split('/').Select(s => ValorSensivel(s) ? "XXXX" : s);
        return semQuery[..barra] + string.Join("/", segmentos);
    }

    /// <summary>Descreve o SHAPE de um quadro: nomes de campo de topo e seus tipos.</summary>
    public static string DescreverShape(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "(vazio)";
        var (prefixo, corpo) = SemPrefixoDeComando(json);
        var antes = prefixo.Length > 0 ? prefixo + " " : "";
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return antes + doc.RootElement.ValueKind.ToString().ToLowerInvariant();
            var campos = doc.RootElement.EnumerateObject()
                .Select(p => $"{p.Name}:{p.Value.ValueKind.ToString().ToLowerInvariant()}");
            return antes + "{ " + string.Join(", ", campos) + " }";
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
        /// <summary>
        /// Quadros guardados POR CONEXÃO (07/10/2026). O teto era um só para tudo, e o WebSocket que
        /// fala mais (o firefly do Gestor, os pulsos) enchia as 40 vagas antes de o Sendbird mandar a
        /// primeira mensagem: o diagnóstico nunca mostrava um MESG.
        /// </summary>
        public const int TetoFrames = 40;
        /// <summary>Teto do total, para a memória não crescer com conexões que vêm e vão.</summary>
        public const int TetoTotal = 400;
        private readonly object _trava = new();
        private readonly HashSet<string> _wsUrls = new();
        private readonly List<(string Payload, bool Enviado)> _frames = new();
        private readonly Dictionary<string, int> _porConexao = new(StringComparer.Ordinal);
        private string? _tokenRaw;          // SEGREDO: só em memória, nunca gravado em claro
        private string? _authRespostaRaw;   // idem

        /// <summary>Token capturado (em memória), para o futuro parser nativo. Nunca logar.</summary>
        public string? TokenEmMemoria { get { lock (_trava) return _tokenRaw; } }

        /// <summary>
        /// O id do usuário DESTA loja no chat (claim <c>u</c> do token). Só para separar a fala da
        /// loja da fala do cliente. Nunca é gravado nem enviado. Null enquanto o token não veio.
        /// </summary>
        public string? UserIdEmMemoria { get { lock (_trava) return UserIdDoToken(_tokenRaw); } }

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

        public void RegistrarFrame(string? payload, bool enviado, string? conexao = null)
        {
            if (string.IsNullOrWhiteSpace(payload)) return;
            lock (_trava)
            {
                if (_frames.Count >= TetoTotal) return;
                var k = conexao ?? "";
                _porConexao.TryGetValue(k, out var n);
                if (n >= TetoFrames) return;
                _porConexao[k] = n + 1;
                _frames.Add((payload, enviado));
            }
        }

        /// <summary>Quantos quadros desta conexão estão guardados (para o teste do teto por conexão).</summary>
        public int QuadrosDaConexao(string? conexao)
        {
            lock (_trava) return _porConexao.TryGetValue(conexao ?? "", out var n) ? n : 0;
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
            sb.AppendLine("Tokens, chaves e assinaturas sao MASCARADOS (XXXX) por NOME de parametro/campo");
            sb.AppendLine("(token, key, secret, signature, auth, session, access...) E por FORMATO (JWT e");
            sb.AppendLine("string longa opaca), mesmo em campo nunca visto. O texto inteiro passa por essa");
            sb.AppendLine("varredura antes de ser gravado. Host, caminho, nomes de parametro e ids curtos");
            sb.AppendLine("e publicos (app_id, versao do SDK) ficam legiveis de proposito.");
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
                        + $"quando={(m.Quando?.ToString("HH:mm:ss") ?? "?")} "
                        + $"minha={(m.Minha?.ToString() ?? "?")} texto=(len {m.Texto.Length})");
                sb.AppendLine();
            }

            var msgs = frames.Select(f => NormalizarFrame(f.Payload)).Where(m => m is not null).Select(m => m!).ToList();
            var conversas = AgruparConversas(msgs);
            sb.AppendLine($"-- Resumo normalizado: {msgs.Count} mensagem(ns) em {conversas.Count} conversa(s) --");

            // REDE DE SEGURANÇA: o texto inteiro passa pela varredura antes de sair
            // daqui. Caminho novo (seção nova, campo novo, cabeçalho novo) não fura
            // a promessa do cabeçalho, porque nenhum deles escapa desta linha.
            return MascararTexto(sb.ToString());
        }
    }

    /// <summary>
    /// OS CONTADORES DO DIAGNÓSTICO DO SINAL (07/10/2026, seção 4.9 e 16). O que a loja precisa
    /// provar antes de sair da Sombra (onde vem o autor, se vem msg_id, que tipos de usuário
    /// aparecem, se o autor da loja é o id da URL do WebSocket) vira CONTAGEM e NOME DE CAMPO aqui.
    ///
    /// ⚠️ Só chaves e contagens: nunca o texto de ninguém, nunca um id. Nome de campo só entra se for
    /// um identificador curto ([A-Za-z0-9_], até 40), e os mapas têm teto.
    /// </summary>
    public sealed class ContadoresSendbird
    {
        private const int TetoCampos = 60;
        private const int TetoMapa = 30;
        private const int TetoCanais = 500;
        private static readonly Regex NomeDeCampo = new("^[A-Za-z0-9_]{1,40}$", RegexOptions.Compiled);

        private readonly object _trava = new();
        private int _mesg, _autorIgualWs, _autorDiferenteWs, _whisper;
        private readonly SortedSet<string> _camposRaiz = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _camposUser = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, int> _userTypes = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, int> _comandos = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _canais = new(StringComparer.Ordinal);
        private DateTime? _ultimoQuadro;

        public DateTime? UltimoQuadro { get { lock (_trava) return _ultimoQuadro; } }

        /// <summary>Conta um quadro recebido. Nunca lança.</summary>
        public void Registrar(string? payload, string? wsUserId, DateTime agora)
        {
            if (string.IsNullOrEmpty(payload)) return;
            try
            {
                var cmd = QuadroSendbird.Comando(payload);
                if (cmd is null) return;
                // ws_vivo é do WebSocket do SENDBIRD: o pulso de outra conexão do Gestor (firefly)
                // não pode dizer que o chat está vivo (seção 16, item 4).
                lock (_trava) { _ultimoQuadro = agora; Somar(_comandos, cmd); }
                if (cmd != QuadroSendbird.ComandoMensagem) return;

                var m = QuadroSendbird.Ler(payload);
                var (_, json) = SemPrefixoDeComando(payload);
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                lock (_trava)
                {
                    _mesg++;
                    if (r.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in r.EnumerateObject()) Campo(_camposRaiz, p.Name);
                        if (r.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object)
                            foreach (var p in u.EnumerateObject()) Campo(_camposUser, p.Name);
                    }
                    if (m is null) return;
                    if (m.Whisper) _whisper++;
                    if (m.UserType is { Length: > 0 } ut && NomeDeCampo.IsMatch(ut)) Somar(_userTypes, ut);
                    if (!string.IsNullOrWhiteSpace(wsUserId) && m.AutorId is not null)
                    {
                        if (string.Equals(m.AutorId.Trim(), wsUserId.Trim(), StringComparison.OrdinalIgnoreCase)) _autorIgualWs++;
                        else _autorDiferenteWs++;
                    }
                    if (CanalIfood.Ler(m.Canal) is { } ids)
                    {
                        var k = ids.MerchantId.Length >= 4 ? ids.MerchantId[..4] : ids.MerchantId;
                        if (!_canais.TryGetValue(k, out var set))
                        {
                            if (_canais.Count >= TetoMapa) return;
                            _canais[k] = set = new HashSet<string>(StringComparer.Ordinal);
                        }
                        if (set.Count < TetoCanais) set.Add(m.Canal!);
                    }
                }
            }
            catch { /* diagnóstico nunca atrapalha a captura */ }
        }

        /// <summary>Quantas conversas de pedido (_cm_) já apareceram, pelos 4 primeiros caracteres do merchant.</summary>
        public IReadOnlyDictionary<string, int> CanaisPorMerchant()
        {
            lock (_trava) return _canais.ToDictionary(x => x.Key, x => x.Value.Count, StringComparer.Ordinal);
        }

        /// <summary>Alguns canais de pedido que já apareceram (para o script conferir no SDK). Só memória.</summary>
        public IReadOnlyList<string> AlgunsCanais(int max)
        {
            lock (_trava) return _canais.Values.SelectMany(x => x).Take(max).ToList();
        }

        /// <summary>O nó <c>quadros</c> do estado do sinal.</summary>
        public System.Text.Json.Nodes.JsonObject Quadros()
        {
            lock (_trava)
            {
                var ut = new System.Text.Json.Nodes.JsonObject();
                foreach (var (k, v) in _userTypes) ut[k] = v;
                var cm = new System.Text.Json.Nodes.JsonObject();
                foreach (var (k, v) in _comandos) cm[k] = v;
                return new System.Text.Json.Nodes.JsonObject
                {
                    ["mesg"] = _mesg,
                    ["campos_raiz"] = new System.Text.Json.Nodes.JsonArray(_camposRaiz.Select(x => (System.Text.Json.Nodes.JsonNode?)x).ToArray()),
                    ["campos_user"] = new System.Text.Json.Nodes.JsonArray(_camposUser.Select(x => (System.Text.Json.Nodes.JsonNode?)x).ToArray()),
                    ["user_types"] = ut,
                    ["autor_igual_ws"] = _autorIgualWs,
                    ["autor_diferente_ws"] = _autorDiferenteWs,
                    ["whisper"] = _whisper,
                    ["comandos"] = cm,
                };
            }
        }

        private static void Campo(SortedSet<string> s, string nome)
        {
            if (s.Count < TetoCampos && NomeDeCampo.IsMatch(nome)) s.Add(nome);
        }

        private static void Somar(SortedDictionary<string, int> d, string k)
        {
            if (d.TryGetValue(k, out var n)) d[k] = n + 1;
            else if (d.Count < TetoMapa) d[k] = 1;
        }
    }
}
