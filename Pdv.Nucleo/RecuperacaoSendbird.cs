using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pdv.Nucleo;

/// <summary>Um canal que a varredura vai ler: a conversa e a marca (ms) a partir da qual pede o histórico.</summary>
public sealed record CanalPlanejado(string Canal, long MessageTs, long UltimaMs, string? UltimoMsgId);

/// <summary>
/// 1.0.41: o estado da recuperação para o sinal (estado.recuperacao). Só enums, contagens e horas: nunca chave,
/// URL, id ou texto.
/// </summary>
public sealed record EstadoDaRecuperacao(string Fonte, DateTime? UltimaEm, DateTime? UltimaOkEm, int Canais, int CanaisLidos,
    long MensagensNovas, string? Erro, int RecuoS, int Varreduras, int RestRecebidas = 0, int RestEntregues = 0);

/// <summary>O que uma varredura fez (para o diag e a bateria).</summary>
/// <param name="Erro">null quando deu; senão um de <see cref="RecuperacaoSendbird.Erros"/> ou "portao"/"recuo" (pulada).</param>
/// <param name="Completa">false quando o prazo da varredura inteira (30 s) parou no meio: o resto fica para a próxima.</param>
public sealed record ResultadoDaVarredura(string? Erro, int Canais, int Mudados, int Lidos, int Novas, int Ms, bool Completa, bool Chamou);

/// <summary>
/// 1.0.41 (16/10/2026, Savassi, pedido 2029): A RECUPERAÇÃO DO CHAT PELO HISTÓRICO DO SENDBIRD, o núcleo puro.
///
/// O robô só via as mensagens que chegavam pelo WebSocket enquanto o Gestor estava aberto dentro do caixa. Com o
/// chat morto de 12:32 a 13:22 (atualização para a 1.0.40), o cliente do pedido 2029 mandou 3 códigos às 13:21 e
/// NADA foi capturado. Decisão de 04/09 (memória project_pdv_nativo): o chat nativo vira cliente Sendbird
/// documentado: REST api-&lt;appid&gt;.sendbird.com/v3 para lista de canais e histórico + WS para tempo real.
///
/// Aqui mora o que é puro: as URLs e os cabeçalhos (os mesmos do SDK), o planejamento (que canais mudaram desde a
/// marca d'água), o filtro (só o que NÃO é da loja), a reconstrução do quadro no formato do WebSocket (para o
/// resto do caixa não mudar), o dedup por msg_id, a janela de 12 h e o recuo exponencial. GET apenas: nunca
/// mark_as_read, nunca abrir conversa, nunca tocar o DOM.
///
/// ⚠️ A chave de sessão nunca entra em string de diag, exceção ou arquivo. Toda linha de diag passa por
/// <see cref="ChatCaptura.MascararTexto"/> como rede de segurança.
/// </summary>
public static class RecuperacaoSendbird
{
    public const string VersaoDoSdk = "4.19.9";
    public const int TetoCanaisPorVarredura = 20;
    public const int TetoMensagensPorCanal = 100;
    public const int LimiteDaLista = 100;
    public const int PaginasDaLista = 3;
    public static readonly TimeSpan JanelaInicial = TimeSpan.FromHours(12);
    public static readonly TimeSpan PrazoDaChamada = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PrazoDaVarredura = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(10);

    public const string ErroSessaoInvalida = "sessao_invalida";
    public const string ErroHttp429 = "http_429";
    public const string ErroHttp5xx = "http_5xx";
    public const string ErroHttp4xx = "http_4xx";
    public const string ErroTimeout = "timeout";
    public const string ErroJson = "json";
    public const string ErroSemSessao = "sem_sessao";
    /// <summary>Revisão 1.0.41: há credencial, mas nenhum dos usuários dela listou canal de cliente (a loja segue desconhecida).</summary>
    public const string ErroSemLoja = "sem_loja";
    public const string ErroRede = "rede";
    /// <summary>Não é erro: o prazo da varredura inteira (30 s) parou a rodada; o resto fica para a próxima.</summary>
    public const string Prazo = "prazo";

    /// <summary>Os erros que o sinal aceita (lista branca).</summary>
    public static readonly IReadOnlySet<string> Erros = new HashSet<string>(StringComparer.Ordinal)
    {
        ErroSessaoInvalida, ErroHttp429, ErroHttp5xx, ErroHttp4xx, ErroTimeout, ErroJson, ErroSemSessao, ErroSemLoja, ErroRede,
    };

    /// <summary>Quantos usuários a varredura experimenta quando a loja é desconhecida (a loja e o entregador; sobra um).</summary>
    public const int TetoDeCandidatas = 3;

    /// <summary>Os comandos do histórico que viram quadro.</summary>
    public static readonly IReadOnlySet<string> Comandos = new HashSet<string>(StringComparer.Ordinal) { "MESG", "FILE", "ADMM", "BRDM", "AEDI" };

    private static readonly JsonSerializerOptions JsonDoQuadro = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ── as URLs (A3) ─────────────────────────────────────────────────────────

    /// <summary>
    /// L: a lista dos canais do usuário, da conversa com mensagem mais nova para a mais velha. SEM unread_filter
    /// (revisão 1.0.41): o não lido é por usuário e some quando alguém abre a conversa no celular; a janela de
    /// 12 h e a marca d'água já deixam a lista leve.
    /// </summary>
    public static string UrlLista(string appId, string userId, string? next = null)
    {
        var u = $"https://api-{appId.ToLowerInvariant()}.sendbird.com/v3/users/{Uri.EscapeDataString(userId)}/my_group_channels"
                + $"?limit={LimiteDaLista}&order=latest_last_message&show_member=false&show_empty=false";
        if (!string.IsNullOrEmpty(next)) u += "&token=" + Uri.EscapeDataString(next);
        return u;
    }

    /// <summary>M: as mensagens de um canal a partir da marca (inclusive), em ordem crescente, até 100.</summary>
    public static string UrlMensagens(string appId, string canal, long marcaMs)
        => $"https://api-{appId.ToLowerInvariant()}.sendbird.com/v3/group_channels/{Uri.EscapeDataString(canal)}/messages"
           + $"?message_ts={marcaMs.ToString(CultureInfo.InvariantCulture)}&prev_limit=0&next_limit={TetoMensagensPorCanal}&include=true&reverse=false";

    // ── os cabeçalhos (A2) ───────────────────────────────────────────────────

    /// <summary>
    /// O pedido GET com EXATAMENTE os cabeçalhos do SDK: App-Id, Session-Key, Accept, SB-User-Agent, SendBird,
    /// SB-SDK-User-Agent (só se capturado) e Request-Sent-Timestamp. Nenhum outro.
    /// </summary>
    public static HttpRequestMessage Pedido(string url, CredencialSendbird cred, CabecalhosDoSdk? cab, DateTime agora)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        r.Headers.TryAddWithoutValidation("App-Id", cred.AppId);
        r.Headers.TryAddWithoutValidation("Session-Key", cred.SessionKey);
        r.Headers.TryAddWithoutValidation("Accept", cab?.Accept ?? "application/json");
        r.Headers.TryAddWithoutValidation("SB-User-Agent", cab?.SbUserAgent ?? $"JS/c{VersaoDoSdk}//");
        r.Headers.TryAddWithoutValidation("SendBird", cab?.SendBird ?? $"JS,{VersaoDoSdk},{cred.AppId}");
        if (cab?.SbSdkUserAgent is { Length: > 0 } sdk) r.Headers.TryAddWithoutValidation("SB-SDK-User-Agent", sdk);
        r.Headers.TryAddWithoutValidation("Request-Sent-Timestamp", new DateTimeOffset(agora).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        return r;
    }

    // ── o quadro a partir do histórico (A4) ──────────────────────────────────

    /// <summary>
    /// Uma mensagem do histórico REST vira o quadro "CMD"+JSON que o WebSocket traria, para o leitor de hoje
    /// (<see cref="QuadroSendbird.Ler"/>, <see cref="ChatArquivo.LerQuadro"/>) dar os mesmos campos e a mesma
    /// chave "sb:&lt;msg_id&gt;". Copia o objeto inteiro e normaliza: msg_id = msg_id ?? message_id; user.guest_id =
    /// user_id quando falta; user.name = nickname quando falta; FILE com file aninhado espelha url/name/type na
    /// raiz. Tipo desconhecido ou sem channel_url: null. Sem "\n" no fim. Nunca lança.
    /// </summary>
    public static string? QuadroDoHistorico(JsonElement m)
    {
        try
        {
            if (m.ValueKind != JsonValueKind.Object) return null;
            var tipo = Texto(m, "type");
            if (tipo is null || !Comandos.Contains(tipo)) return null;
            if (string.IsNullOrWhiteSpace(Texto(m, "channel_url"))) return null;
            if (JsonNode.Parse(m.GetRawText()) is not JsonObject o) return null;
            o.Remove("type");

            var msgId = NumeroOuTexto(m, "msg_id") ?? NumeroOuTexto(m, "message_id");
            if (msgId is { } id && id != "0") o["msg_id"] = long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : id;
            else o.Remove("msg_id");

            if (o["user"] is JsonObject u)
            {
                if (u["guest_id"] is null && u["user_id"] is JsonValue uid) u["guest_id"] = uid.DeepClone();
                if (u["name"] is null && u["nickname"] is JsonValue nick) u["name"] = nick.DeepClone();
            }
            if (o["created_at"] is null && NumeroOuTexto(m, "ts") is { } ts && long.TryParse(ts, out var tsn)) o["created_at"] = tsn;
            if (tipo == "FILE" && o["file"] is JsonObject f)
            {
                foreach (var campo in new[] { "url", "name", "type", "size" })
                    if (o[campo] is null && f[campo] is JsonNode v) o[campo] = v.DeepClone();
            }
            return tipo + o.ToJsonString(JsonDoQuadro);
        }
        catch { return null; }
    }

    /// <summary>A chave local do quadro recuperado, a mesma do robô e do arquivo: "sb:&lt;msg_id&gt;", senão "h:...".</summary>
    public static string ChaveDoQuadro(string quadro, string? usuarioLoja)
    {
        var m = QuadroSendbird.Ler(quadro);
        if (m is not null) return ConversaRaspadinha.Chave(m);
        var a = ChatArquivo.LerQuadro(quadro, usuarioLoja, null);
        if (a is not null) return ChatArquivo.Chave(a);
        return "h:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(quadro))).ToLowerInvariant();
    }

    // ── o planejamento (A6) ──────────────────────────────────────────────────

    /// <summary>
    /// Da lista L, os canais que valem uma leitura: só canal de pedido de cliente (<see cref="CanalIfood.Ler"/>)
    /// cuja last_message (created_at, message_id) está além da marca do canal (ou da janela de 12 h, na primeira
    /// vez). Só a marca decide (revisão 1.0.41): nem o autor da última fala nem o não lido entram, porque a loja
    /// respondendo pelo celular ("já vou ver") escondia os códigos que o cliente mandou antes. Quem tira a fala da
    /// loja é <see cref="FiltrarEOrdenar"/>. Até 20 (os primeiros da lista, que vem da conversa mais recente).
    /// Nunca lança.
    /// </summary>
    public static IReadOnlyList<CanalPlanejado> Planejar(JsonElement lista, MarcasDAgua marcas, string? usuarioLoja, DateTime agora)
    {
        var saida = new List<CanalPlanejado>();
        try
        {
            var canais = Canais(lista);
            if (canais.ValueKind != JsonValueKind.Array) return saida;
            var inicio = new DateTimeOffset(agora).ToUnixTimeMilliseconds() - (long)JanelaInicial.TotalMilliseconds;
            foreach (var c in canais.EnumerateArray())
            {
                if (saida.Count >= TetoCanaisPorVarredura) break;
                if (c.ValueKind != JsonValueKind.Object) continue;
                var canal = Texto(c, "channel_url");
                if (CanalIfood.Ler(canal) is null) continue;
                if (!c.TryGetProperty("last_message", out var ult) || ult.ValueKind != JsonValueKind.Object) continue;
                var criado = Numero(ult, "created_at");
                if (criado is not { } ms) continue;
                var msgId = NumeroOuTexto(ult, "message_id") ?? NumeroOuTexto(ult, "msg_id");
                var marca = marcas.Marca(canal!);
                var desde = marca?.Ms ?? inicio;
                if (ms < desde) continue;
                if (ms == desde && (marca is null ? false : marca.Value.MsgId is null || marca.Value.MsgId == msgId)) continue;
                saida.Add(new CanalPlanejado(canal!, desde, ms, msgId));
            }
        }
        catch { }
        return saida;
    }

    /// <summary>
    /// Revisão 1.0.41: a lista L traz algum canal de pedido de CLIENTE (sendbird_gc_cm_...)? É assim que a
    /// varredura elege a loja quando o caixa sobe sem saber qual dos dois usuários do Gestor (a loja e o
    /// entregador) é ela: a lista do entregador só tem canais sem _cm_. Nunca lança.
    /// </summary>
    public static bool TemCanalDeCliente(JsonElement lista)
    {
        try
        {
            var canais = Canais(lista);
            if (canais.ValueKind != JsonValueKind.Array) return false;
            foreach (var c in canais.EnumerateArray())
                if (c.ValueKind == JsonValueKind.Object && CanalIfood.Ler(Texto(c, "channel_url")) is not null) return true;
            return false;
        }
        catch { return false; }
    }

    /// <summary>O array de canais: a raiz "channels", ou a própria raiz quando já é um array.</summary>
    private static JsonElement Canais(JsonElement lista)
        => lista.ValueKind == JsonValueKind.Object && lista.TryGetProperty("channels", out var c) ? c : lista;

    /// <summary>O "next" da lista (a página seguinte), ou null.</summary>
    public static string? ProximaPagina(JsonElement lista)
        => lista.ValueKind == JsonValueKind.Object ? Texto(lista, "next") : null;

    /// <summary>O último canal da página ainda tem last_message dentro da janela de 12 h? (senão a próxima página não interessa)</summary>
    public static bool UltimoDentroDaJanela(JsonElement lista, DateTime agora)
    {
        try
        {
            var canais = Canais(lista);
            if (canais.ValueKind != JsonValueKind.Array || canais.GetArrayLength() == 0) return false;
            var ult = canais[canais.GetArrayLength() - 1];
            if (!ult.TryGetProperty("last_message", out var lm) || Numero(lm, "created_at") is not { } ms) return false;
            return ms >= new DateTimeOffset(agora).ToUnixTimeMilliseconds() - (long)JanelaInicial.TotalMilliseconds;
        }
        catch { return false; }
    }

    /// <summary>A mensagem (do histórico ou o last_message) é da loja: user_id igual ao da loja, req_id do caixa ou userType MERCHANT.</summary>
    public static bool EhDaLoja(JsonElement m, string? usuarioLoja)
    {
        try
        {
            if (EnvioPeloSocket.EhNosso(Texto(m, "req_id"))) return true;
            string? autor = null, tipo = null;
            if (m.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                autor = NumeroOuTexto(u, "user_id") ?? NumeroOuTexto(u, "guest_id");
                tipo = UserType(u);
            }
            autor ??= NumeroOuTexto(m, "user_id");
            tipo ??= UserType(m);
            if (!string.IsNullOrWhiteSpace(usuarioLoja) && autor is not null
                && string.Equals(autor.Trim(), usuarioLoja.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return tipo == "MERCHANT";
        }
        catch { return false; }
    }

    private static string? UserType(JsonElement o)
    {
        if (!o.TryGetProperty("metadata", out var md)) return null;
        try
        {
            if (md.ValueKind == JsonValueKind.String)
            {
                var s = md.GetString();
                if (string.IsNullOrWhiteSpace(s) || !s.TrimStart().StartsWith('{')) return null;
                using var d = JsonDocument.Parse(s);
                return Texto(d.RootElement, "userType")?.Trim().ToUpperInvariant();
            }
            return md.ValueKind == JsonValueKind.Object ? Texto(md, "userType")?.Trim().ToUpperInvariant() : null;
        }
        catch { return null; }
    }

    // ── o filtro (A5) ────────────────────────────────────────────────────────

    /// <summary>
    /// Das mensagens M de um canal, os quadros que entram: MESG/FILE só quando NÃO são da loja (user_id, req_id
    /// "pdv", MERCHANT); ADMM/BRDM/AEDI entram (são do iFood); created_at abaixo da marca fica de fora; ordem
    /// crescente de created_at (e msg_id); até 100. Nunca lança.
    /// </summary>
    public static IReadOnlyList<string> FiltrarEOrdenar(JsonElement mensagens, string? usuarioLoja, long marcaMs)
    {
        return Peneirar(mensagens, usuarioLoja, marcaMs).Select(x => x.Quadro).ToList();
    }

    /// <summary>
    /// O id de uma mensagem do histórico para casar a fala do cliente com a resposta da loja: "msg_id" (ou
    /// message_id); sem id, "ts:" + created_at. Null quando não dá para dizer. Nunca lança.
    /// </summary>
    public static string? IdDaMensagem(JsonElement m)
    {
        try
        {
            if (m.ValueKind != JsonValueKind.Object) return null;
            var id = NumeroOuTexto(m, "msg_id") ?? NumeroOuTexto(m, "message_id");
            if (id is { } s && s != "0") return s;
            return (Numero(m, "created_at") ?? Numero(m, "ts")) is { } ms ? "ts:" + ms.ToString(CultureInfo.InvariantCulture) : null;
        }
        catch { return null; }
    }

    /// <summary>O mesmo id para um quadro já montado ("CMD"+JSON): msg_id, senão "ts:" + created_at. Nunca lança.</summary>
    public static string? IdDoQuadro(string? quadro)
    {
        try
        {
            if (string.IsNullOrEmpty(quadro) || quadro.Length < 5) return null;
            using var d = JsonDocument.Parse(quadro.AsMemory(4).TrimEnd('\n').TrimEnd('\r'));
            return IdDaMensagem(d.RootElement);
        }
        catch { return null; }
    }

    /// <summary>
    /// A mensagem é da EQUIPE da loja, de verdade (um humano no Gestor ou no celular): é da loja
    /// (<see cref="EhDaLoja"/>) e NÃO é do robô (req_id "pdv..." do envio pelo socket, ou texto igual a uma saída que
    /// este caixa mandou pelo SDK). Nunca lança.
    /// </summary>
    public static bool EhDaEquipe(JsonElement m, string? usuarioLoja, Func<string?, bool>? textoDoRobo)
    {
        try
        {
            if (m.ValueKind != JsonValueKind.Object || !EhDaLoja(m, usuarioLoja)) return false;
            if (EnvioPeloSocket.EhNosso(Texto(m, "req_id"))) return false;
            if (textoDoRobo is not null && Texto(m, "message") is { } texto)
            {
                try { if (textoDoRobo(texto)) return false; } catch { }
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 1.0.42 (Savassi 16/10 17:47, pedido 0002): a recuperação entregou ao robô os códigos que a cliente mandou às
    /// 17:02 e que a equipe JÁ TINHA ATENDIDO à mão às 17:05 ("um brownie nutella vai junto ao seu pedido"); o robô
    /// respondeu 45 min depois contradizendo a equipe. Regra: a fala de cliente recuperada que tem, no MESMO canal,
    /// uma mensagem da EQUIPE (humana, não do robô) DEPOIS dela vai só para o arquivo, nunca para o robô. O
    /// histórico já traz as mensagens da loja, então a decisão é local. Devolve os ids (<see cref="IdDaMensagem"/>)
    /// das falas de cliente já respondidas, acima da marca. Nunca lança.
    /// </summary>
    public static IReadOnlySet<string> RespondidasPelaEquipe(JsonElement mensagens, string? usuarioLoja, long marcaMs, Func<string?, bool>? textoDoRobo)
    {
        var respondidas = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var arr = mensagens.ValueKind == JsonValueKind.Object && mensagens.TryGetProperty("messages", out var ms) ? ms : mensagens;
            if (arr.ValueKind != JsonValueKind.Array) return respondidas;
            var clientes = new List<(long Ms, long Id, string Chave)>();
            var equipe = new List<(long Ms, long Id)>();
            foreach (var m in arr.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                var tipo = Texto(m, "type");
                if (tipo is not ("MESG" or "FILE")) continue;
                var criado = Numero(m, "created_at") ?? Numero(m, "ts") ?? 0;
                var idTxt = NumeroOuTexto(m, "message_id") ?? NumeroOuTexto(m, "msg_id");
                var id = idTxt is not null && long.TryParse(idTxt, out var n) ? n : 0;
                if (EhDaLoja(m, usuarioLoja))
                {
                    if (EhDaEquipe(m, usuarioLoja, textoDoRobo)) equipe.Add((criado, id));
                    continue;
                }
                if (criado < marcaMs) continue;
                if (IdDaMensagem(m) is { } chave) clientes.Add((criado, id, chave));
            }
            if (equipe.Count == 0) return respondidas;
            foreach (var c in clientes)
                if (equipe.Any(e => e.Ms > c.Ms || (e.Ms == c.Ms && e.Id > c.Id))) respondidas.Add(c.Chave);
        }
        catch { }
        return respondidas;
    }

    private static List<(long Ms, long Id, string Quadro)> Peneirar(JsonElement mensagens, string? usuarioLoja, long marcaMs)
    {
        var lista = new List<(long Ms, long Id, string Quadro)>();
        try
        {
            var arr = mensagens.ValueKind == JsonValueKind.Object && mensagens.TryGetProperty("messages", out var ms) ? ms : mensagens;
            if (arr.ValueKind != JsonValueKind.Array) return lista;
            foreach (var m in arr.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                var tipo = Texto(m, "type");
                if (tipo is null || !Comandos.Contains(tipo)) continue;
                if (tipo is "MESG" or "FILE" && EhDaLoja(m, usuarioLoja)) continue;
                var criado = Numero(m, "created_at") ?? Numero(m, "ts") ?? 0;
                if (criado < marcaMs) continue;
                var quadro = QuadroDoHistorico(m);
                if (quadro is null) continue;
                var id = NumeroOuTexto(m, "message_id") ?? NumeroOuTexto(m, "msg_id");
                lista.Add((criado, id is not null && long.TryParse(id, out var n) ? n : 0, quadro));
            }
        }
        catch { }
        return lista.OrderBy(x => x.Ms).ThenBy(x => x.Id).Take(TetoMensagensPorCanal).ToList();
    }

    /// <summary>O canal, o created_at e o msg_id de um quadro (do WS ou reconstruído), para a marca d'água; null quando não é mensagem.</summary>
    public static (string Canal, long Ms, string? MsgId)? MarcaDoQuadro(string? payload)
    {
        try
        {
            var cmd = QuadroSendbird.Comando(payload);
            if (cmd is null || !Comandos.Contains(cmd)) return null;
            using var d = JsonDocument.Parse(payload!.AsMemory(4).TrimEnd('\n').TrimEnd('\r'));
            var r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var canal = Texto(r, "channel_url");
            if (CanalIfood.Ler(canal) is null) return null;
            if ((Numero(r, "created_at") ?? Numero(r, "ts")) is not { } ms) return null;
            var id = NumeroOuTexto(r, "msg_id");
            if (id is "0") id = null;
            return (canal!, ms, id);
        }
        catch { return null; }
    }

    /// <summary>O código de erro do Sendbird no corpo ({"code":400302,...}), ou null.</summary>
    public static int? CodigoDoCorpo(string? corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo) || !corpo.TrimStart().StartsWith('{')) return null;
        try
        {
            using var d = JsonDocument.Parse(corpo);
            return d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("code", out var c)
                   && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : null;
        }
        catch { return null; }
    }

    /// <summary>400302 (sessão), 400309/400310 (expirada) e a família 4003xx: a sessão não vale.</summary>
    public static bool CodigoDeSessaoInvalida(int? codigo) => codigo is >= 400300 and <= 400399;

    private static string? Texto(JsonElement o, string k)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s ? s : null;

    private static string? NumeroOuTexto(JsonElement o, string k)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(k, out var v)) return null;
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static long? Numero(JsonElement o, string k)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(k, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var m)) return m;
        return null;
    }
}

/// <summary>
/// A MARCA D'ÁGUA por canal (created_at, msg_id da última mensagem vista), alimentada pelos quadros do WebSocket
/// e pelas mensagens recuperadas. Teto 2000 canais; faxina dos canais sem novidade há mais de 24 h. Seguro entre
/// threads.
/// </summary>
public sealed class MarcasDAgua
{
    public const int Teto = 2000;
    public static readonly TimeSpan Validade = TimeSpan.FromHours(24);

    private readonly object _trava = new();
    private readonly Dictionary<string, (long Ms, string? MsgId, DateTime Em)> _marcas = new(StringComparer.Ordinal);

    public (long Ms, string? MsgId)? Marca(string canal)
    {
        lock (_trava) return _marcas.TryGetValue(canal, out var m) ? (m.Ms, m.MsgId) : null;
    }

    /// <summary>Avança a marca do canal (nunca anda para trás). Devolve true quando avançou.</summary>
    public bool Avancar(string canal, long ms, string? msgId, DateTime agora)
    {
        lock (_trava)
        {
            if (_marcas.TryGetValue(canal, out var m) && (m.Ms > ms || (m.Ms == ms && m.MsgId == msgId))) return false;
            if (_marcas.Count >= Teto && !_marcas.ContainsKey(canal)) Faxina(agora, true);
            _marcas[canal] = (ms, msgId, agora);
            return true;
        }
    }

    /// <summary>Um quadro visto (pelo WS ou recuperado): avança a marca do canal dele. Devolve a marca lida, ou null.</summary>
    public (string Canal, long Ms, string? MsgId)? Viu(string? payload, DateTime agora)
    {
        var m = RecuperacaoSendbird.MarcaDoQuadro(payload);
        if (m is { } x) Avancar(x.Canal, x.Ms, x.MsgId, agora);
        return m;
    }

    public int Quantas { get { lock (_trava) return _marcas.Count; } }

    public void Faxina(DateTime agora, bool forcar = false)
    {
        lock (_trava)
        {
            foreach (var k in _marcas.Where(x => agora - x.Value.Em > Validade).Select(x => x.Key).ToList()) _marcas.Remove(k);
            if (forcar && _marcas.Count >= Teto) _marcas.Clear();
        }
    }
}

/// <summary>O dedup próprio da recuperação por chave ("sb:&lt;msg_id&gt;" ou "h:..."): fila de 5000. Seguro entre threads.</summary>
public sealed class DedupDeMensagens
{
    public const int Teto = 5000;
    private readonly object _trava = new();
    private readonly HashSet<string> _vistas = new(StringComparer.Ordinal);
    private readonly Queue<string> _ordem = new();

    /// <summary>true quando a chave é nova (e passa a ser conhecida).</summary>
    public bool Nova(string chave)
    {
        lock (_trava)
        {
            if (!_vistas.Add(chave)) return false;
            _ordem.Enqueue(chave);
            while (_ordem.Count > Teto) _vistas.Remove(_ordem.Dequeue());
            return true;
        }
    }

    public bool Conhece(string chave) { lock (_trava) return _vistas.Contains(chave); }
    public int Quantas { get { lock (_trava) return _vistas.Count; } }
}

/// <summary>O recuo exponencial: 10, 20, 40, 80, 160, 300 s; zera no primeiro 2xx; respeita Retry-After quando maior.</summary>
public sealed class Recuo
{
    public static readonly int[] Degraus = { 10, 20, 40, 80, 160, 300 };
    private readonly object _trava = new();
    private int _falhas;
    private DateTime _ate = DateTime.MinValue;

    /// <summary>Uma falha (429, 5xx, timeout): o próximo degrau. Devolve quantos segundos de espera.</summary>
    public int Falhou(DateTime agora, int? retryAfterS = null)
    {
        lock (_trava)
        {
            var s = Degraus[Math.Min(_falhas, Degraus.Length - 1)];
            _falhas++;
            if (retryAfterS is { } ra && ra > s) s = Math.Min(ra, 3600);
            _ate = agora.AddSeconds(s);
            return s;
        }
    }

    public void Deu() { lock (_trava) { _falhas = 0; _ate = DateTime.MinValue; } }

    public bool Ativo(DateTime agora) { lock (_trava) return agora < _ate; }

    public int SegundosRestantes(DateTime agora) { lock (_trava) return agora < _ate ? (int)Math.Ceiling((_ate - agora).TotalSeconds) : 0; }

    public int Falhas { get { lock (_trava) return _falhas; } }
}

/// <summary>
/// A VARREDURA: uma rodada da recuperação, com as portas trocáveis (o HttpClient na tela, o handler falso na
/// bateria; o relógio; quem recebe cada quadro; o diag; a credencial). Uma chamada leve de lista (L) e só as
/// mensagens (M) dos canais que mudaram. Nunca lança; tudo com prazo (5 s por chamada, 30 s a varredura).
/// </summary>
public sealed class Varredura
{
    /// <summary>O envio HTTP (GET). O handler falso da bateria grava cada pedido.</summary>
    public required Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Http { get; init; }
    public Func<DateTime> Agora { get; init; } = () => DateTime.Now;
    /// <summary>Cada quadro recuperado (no formato do WebSocket), na ordem.</summary>
    public required Action<string> Entregar { get; init; }
    /// <summary>
    /// 1.0.42: a fala de cliente que a equipe JÁ respondeu à mão depois dela (<see cref="RecuperacaoSendbird.RespondidasPelaEquipe"/>)
    /// vai só por aqui (o arquivo), nunca por <see cref="Entregar"/> (o robô). Sem a porta, ela não vai a lugar nenhum.
    /// </summary>
    public Action<string>? EntregarSoArquivo { get; init; }
    /// <summary>1.0.42: o texto é de uma saída que esta ponta mandou pelo SDK (então não é resposta humana)? Null: só o req_id decide.</summary>
    public Func<string?, bool>? TextoDoRobo { get; init; }
    /// <summary>Uma linha de diag (já sem segredo; o serviço ainda a mascara).</summary>
    public Action<string> Diag { get; init; } = _ => { };
    /// <summary>A melhor credencial de agora para a loja (null: sem sessão). Usada quando a loja é conhecida.</summary>
    public required Func<CredencialSendbird?> Credencial { get; init; }
    /// <summary>
    /// Revisão 1.0.41: com a loja desconhecida, a melhor credencial de cada usuário (a loja e o entregador). A
    /// varredura lista os canais de cada um e elege a loja pelo canal de cliente na lista.
    /// </summary>
    public Func<IReadOnlyList<CredencialSendbird>> Candidatas { get; init; } = () => Array.Empty<CredencialSendbird>();
    /// <summary>O user_id da loja, quando já se sabe (ServicoConversaChat.UsuarioDaLoja); null na primeira hora depois de subir.</summary>
    public Func<string?> UsuarioDaLoja { get; init; } = () => null;
    /// <summary>A loja foi eleita pela lista de canais: o user_id dela, para o resto do caixa aprender.</summary>
    public Action<string> LojaDescoberta { get; init; } = _ => { };
    public Func<CabecalhosDoSdk?> Cabecalhos { get; init; } = () => null;
    /// <summary>A chave que a chamada levou não vale (401/403, 4003xx): (motivo, chave). Só ela é condenada.</summary>
    public Action<string, string> Invalidar { get; init; } = (_, _) => { };
    /// <summary>O portão: fora dele a varredura não chama a rede.</summary>
    public Func<bool> Portao { get; init; } = () => true;
    public TimeSpan PrazoDaChamada { get; init; } = RecuperacaoSendbird.PrazoDaChamada;
    public TimeSpan PrazoDaVarredura { get; init; } = RecuperacaoSendbird.PrazoDaVarredura;

    public MarcasDAgua Marcas { get; } = new();
    public DedupDeMensagens Dedup { get; } = new();
    public Recuo Recuo { get; } = new();

    private readonly object _trava = new();
    private bool _shapeAnotado;
    private string _fonte = SessaoSendbird.FonteNenhuma;
    private DateTime? _ultimaEm, _ultimaOkEm;
    private int _canais, _canaisLidos, _varreduras;
    private long _novas;
    private string? _erro;

    /// <summary>Um quadro recebido pelo WebSocket: avança a marca e ensina o dedup (a mesma msg_id nunca sai pelo REST depois).</summary>
    public void Viu(string? payload)
    {
        try
        {
            var m = Marcas.Viu(payload, Agora());
            if (m is { MsgId: { Length: > 0 } id }) Dedup.Nova("sb:" + id.Trim());
        }
        catch { }
    }

    public EstadoDaRecuperacao Estado()
    {
        lock (_trava)
            return new EstadoDaRecuperacao(_fonte, _ultimaEm, _ultimaOkEm, _canais, _canaisLidos, _novas, _erro,
                Recuo.SegundosRestantes(Agora()), _varreduras);
    }

    private void Erro(string? erro) { lock (_trava) _erro = erro; }

    /// <summary>UMA varredura. Nunca lança.</summary>
    public async Task<ResultadoDaVarredura> ExecutarAsync()
    {
        var relogio = Stopwatch.StartNew();
        var canais = 0; var mudados = 0; var lidos = 0; var novas = 0; var jaRespondidas = 0; var chamou = false;
        ResultadoDaVarredura Fim(string? erro, bool completa = true)
            => new(erro, canais, mudados, lidos, novas, (int)Math.Min(60_000, relogio.ElapsedMilliseconds), completa, chamou);
        try
        {
            bool portao;
            try { portao = Portao(); } catch { portao = false; }
            if (!portao) return Fim("portao");
            var agora = Agora();
            var lojaConhecida = UsuarioDaLoja();
            if (string.IsNullOrWhiteSpace(lojaConhecida)) lojaConhecida = null;
            var cred = lojaConhecida is not null ? Credencial() : null;
            IReadOnlyList<CredencialSendbird> candidatas = Array.Empty<CredencialSendbird>();
            if (lojaConhecida is null)
            {
                try { candidatas = Candidatas().Where(c => !string.IsNullOrWhiteSpace(c.UserId)).Take(RecuperacaoSendbird.TetoDeCandidatas).ToList(); }
                catch { candidatas = Array.Empty<CredencialSendbird>(); }
            }
            lock (_trava) _fonte = (cred ?? candidatas.FirstOrDefault())?.Fonte ?? SessaoSendbird.FonteNenhuma;
            if ((cred is null || string.IsNullOrWhiteSpace(cred.UserId)) && candidatas.Count == 0)
            {
                Erro(RecuperacaoSendbird.ErroSemSessao);
                return Fim(RecuperacaoSendbird.ErroSemSessao);
            }
            if (Recuo.Ativo(agora)) return Fim("recuo");
            lock (_trava) { _varreduras++; _ultimaEm = agora; }
            var cab = Cabecalhos();
            using var prazo = new CancellationTokenSource(PrazoDaVarredura);

            // Revisão 1.0.41: a loja desconhecida (o caixa acabou de subir, nada chegou pelo socket que a ensine).
            // Cada usuário candidato lista os canais; a lista com canal de CLIENTE (_cm_) diz quem é a loja. A
            // sessão de quem falhar com 401/403 é condenada sozinha; 429/5xx param a rodada inteira (recuo).
            string? corpoPronto = null;
            if (cred is null)
            {
                string? ultimoErroDaEleicao = null;
                var algumaListou = false;
                foreach (var cand in candidatas)
                {
                    if (prazo.IsCancellationRequested) return Fim(null, completa: false);
                    var (st, corpo, erro) = await ChamarAsync(RecuperacaoSendbird.UrlLista(cand.AppId, cand.UserId!), cand, cab, prazo.Token).ConfigureAwait(false);
                    chamou = true;
                    if (erro == RecuperacaoSendbird.Prazo) return Fim(null, completa: false);
                    if (erro is not null) return Fim(erro);
                    var e = Classificar(st, corpo, agora, cand);
                    if (e is RecuperacaoSendbird.ErroHttp429 or RecuperacaoSendbird.ErroHttp5xx or RecuperacaoSendbird.ErroRede) return Fim(e);
                    if (e is not null) { ultimoErroDaEleicao = e; continue; }
                    Recuo.Deu();
                    bool deCliente;
                    try { using var d = JsonDocument.Parse(corpo ?? ""); deCliente = RecuperacaoSendbird.TemCanalDeCliente(d.RootElement); }
                    catch { Erro(RecuperacaoSendbird.ErroJson); return Fim(RecuperacaoSendbird.ErroJson); }
                    algumaListou = true;
                    if (!deCliente) continue;
                    cred = cand;
                    corpoPronto = corpo;
                    lock (_trava) _fonte = cand.Fonte;
                    Diag($"recuperacao loja eleita pela lista de canais (fonte={cand.Fonte}, candidatas={candidatas.Count})");
                    try { LojaDescoberta(cand.UserId!); } catch (Exception ex) { Diag("recuperacao loja descoberta: " + ex.GetType().Name); }
                    break;
                }
                if (cred is null)
                {
                    // ninguém listou canal de cliente: sem_loja; se ninguém sequer listou, vale o erro da última chamada
                    var e = algumaListou || ultimoErroDaEleicao is null ? RecuperacaoSendbird.ErroSemLoja : ultimoErroDaEleicao;
                    Erro(e);
                    if (e == RecuperacaoSendbird.ErroSemLoja) Diag($"recuperacao erro=sem_loja candidatas={candidatas.Count}");
                    return Fim(e);
                }
            }
            var loja = cred.UserId!;

            // L: a lista dos canais com novidade
            var planejados = new List<CanalPlanejado>();
            string? next = null;
            for (var pagina = 0; pagina < RecuperacaoSendbird.PaginasDaLista; pagina++)
            {
                int st; string? corpo;
                if (corpoPronto is not null) { st = 200; corpo = corpoPronto; corpoPronto = null; }
                else
                {
                    string? erro;
                    (st, corpo, erro) = await ChamarAsync(RecuperacaoSendbird.UrlLista(cred.AppId, loja, next), cred, cab, prazo.Token).ConfigureAwait(false);
                    chamou = true;
                    if (erro == RecuperacaoSendbird.Prazo) return Fim(null, completa: false);
                    if (erro is not null) return Fim(erro);
                    if (Classificar(st, corpo, agora, cred) is { } e) return Fim(e);
                    Recuo.Deu();
                }
                JsonDocument doc;
                try { doc = JsonDocument.Parse(corpo ?? ""); }
                catch { Erro(RecuperacaoSendbird.ErroJson); return Fim(RecuperacaoSendbird.ErroJson); }
                using (doc)
                {
                    var raiz = doc.RootElement;
                    var lista = raiz.ValueKind == JsonValueKind.Object && raiz.TryGetProperty("channels", out var cs) && cs.ValueKind == JsonValueKind.Array ? cs : raiz;
                    canais += lista.ValueKind == JsonValueKind.Array ? lista.GetArrayLength() : 0;
                    planejados.AddRange(RecuperacaoSendbird.Planejar(raiz, Marcas, loja, agora).Where(p => planejados.All(q => q.Canal != p.Canal)));
                    next = RecuperacaoSendbird.ProximaPagina(raiz);
                    if (string.IsNullOrEmpty(next) || !RecuperacaoSendbird.UltimoDentroDaJanela(raiz, agora)) break;
                }
            }
            planejados = planejados.Take(RecuperacaoSendbird.TetoCanaisPorVarredura).ToList();
            mudados = planejados.Count;
            lock (_trava) _canais = canais;

            // M: só as mensagens dos canais que mudaram
            foreach (var p in planejados)
            {
                if (prazo.IsCancellationRequested) return Fim(null, completa: false);
                var (st, corpo, erro) = await ChamarAsync(RecuperacaoSendbird.UrlMensagens(cred.AppId, p.Canal, p.MessageTs), cred, cab, prazo.Token).ConfigureAwait(false);
                if (erro == RecuperacaoSendbird.Prazo) return Fim(null, completa: false);
                if (erro is not null) return Fim(erro);
                if (Classificar(st, corpo, agora, cred) is { } e) return Fim(e);
                Recuo.Deu();
                IReadOnlyList<string> quadros;
                IReadOnlySet<string> respondidas;
                try
                {
                    using var doc = JsonDocument.Parse(corpo ?? "");
                    AnotarShape(doc.RootElement);
                    quadros = RecuperacaoSendbird.FiltrarEOrdenar(doc.RootElement, loja, p.MessageTs);
                    // 1.0.42: a fala de cliente que a equipe já respondeu depois dela vai só para o arquivo
                    respondidas = RecuperacaoSendbird.RespondidasPelaEquipe(doc.RootElement, loja, p.MessageTs, TextoDoRobo);
                }
                catch { Erro(RecuperacaoSendbird.ErroJson); return Fim(RecuperacaoSendbird.ErroJson); }
                lidos++;
                lock (_trava) _canaisLidos++;
                foreach (var q in quadros)
                {
                    var marca = Marcas.Viu(q, Agora());
                    var chave = RecuperacaoSendbird.ChaveDoQuadro(q, loja);
                    if (!Dedup.Nova(chave)) continue;
                    if (respondidas.Count > 0 && RecuperacaoSendbird.IdDoQuadro(q) is { } idq && respondidas.Contains(idq))
                    {
                        jaRespondidas++;
                        try { EntregarSoArquivo?.Invoke(q); } catch (Exception ex) { Diag("recuperacao entregar so arquivo: " + ex.GetType().Name); }
                        continue;
                    }
                    novas++;
                    lock (_trava) _novas++;
                    try { Entregar(q); } catch (Exception ex) { Diag("recuperacao entregar: " + ex.GetType().Name); }
                }
                // o canal cuja novidade era só da loja também avança a marca (a última da lista): senão volta toda varredura.
                // Com a página cheia (100) a marca fica na última lida: o resto vem na próxima varredura.
                if (quadros.Count < RecuperacaoSendbird.TetoMensagensPorCanal) Marcas.Avancar(p.Canal, p.UltimaMs, p.UltimoMsgId, Agora());
            }
            lock (_trava) { _ultimaOkEm = Agora(); _erro = null; }
            if (jaRespondidas > 0) Diag($"recuperacao: {jaRespondidas} falas ja respondidas pela equipe (so arquivo)");
            Diag($"recuperacao fonte={cred.Fonte} canais={canais} mudados={mudados} lidos={lidos} novas={novas} ms={relogio.ElapsedMilliseconds}");
            return Fim(null);
        }
        catch (Exception ex)
        {
            Diag("recuperacao falhou: " + ex.GetType().Name);
            Erro(RecuperacaoSendbird.ErroRede);
            return Fim(RecuperacaoSendbird.ErroRede);
        }
    }

    /// <summary>401/403 ou 4003xx: só a chave que a chamada levou é invalidada; 429 e 5xx: recuo; outro 4xx: http_4xx; 2xx: null.</summary>
    private string? Classificar(int st, string? corpo, DateTime agora, CredencialSendbird cred)
    {
        var codigo = RecuperacaoSendbird.CodigoDoCorpo(corpo);
        if (st is 401 or 403 || RecuperacaoSendbird.CodigoDeSessaoInvalida(codigo))
        {
            try { Invalidar(st is 401 or 403 ? "http_" + st : "codigo_" + codigo, cred.SessionKey); } catch { }
            Erro(RecuperacaoSendbird.ErroSessaoInvalida);
            Diag($"recuperacao erro=sessao_invalida http={st} codigo={codigo?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
            return RecuperacaoSendbird.ErroSessaoInvalida;
        }
        if (st == 429 || st >= 500)
        {
            var e = st == 429 ? RecuperacaoSendbird.ErroHttp429 : RecuperacaoSendbird.ErroHttp5xx;
            var s = Recuo.Falhou(agora, _retryAfter);
            Erro(e);
            Diag($"recuperacao erro={e} recuo={s}s");
            return e;
        }
        if (st is >= 400 and < 500)
        {
            Erro(RecuperacaoSendbird.ErroHttp4xx);
            Diag($"recuperacao erro=http_4xx http={st} codigo={codigo?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
            return RecuperacaoSendbird.ErroHttp4xx;
        }
        if (st is < 200 or >= 300)
        {
            Erro(RecuperacaoSendbird.ErroRede);
            Diag($"recuperacao erro=rede http={st}");
            return RecuperacaoSendbird.ErroRede;
        }
        return null;
    }

    private int? _retryAfter;

    /// <summary>Uma chamada GET com prazo de 5 s. Devolve (status, corpo, erro): timeout ou rede viram erro com recuo.</summary>
    private async Task<(int Status, string? Corpo, string? Erro)> ChamarAsync(string url, CredencialSendbird cred, CabecalhosDoSdk? cab, CancellationToken varredura)
    {
        _retryAfter = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(varredura);
        cts.CancelAfter(PrazoDaChamada);
        try
        {
            using var pedido = RecuperacaoSendbird.Pedido(url, cred, cab, Agora());
            using var resp = await Http(pedido, cts.Token).ConfigureAwait(false);
            var corpo = resp.Content is null ? null : await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (resp.Headers.RetryAfter is { } ra)
            {
                if (ra.Delta is { } d) _retryAfter = (int)Math.Ceiling(d.TotalSeconds);
                else if (ra.Date is { } dt) _retryAfter = (int)Math.Ceiling((dt - DateTimeOffset.Now).TotalSeconds);
            }
            return ((int)resp.StatusCode, corpo, null);
        }
        catch (OperationCanceledException)
        {
            if (varredura.IsCancellationRequested) { Diag("recuperacao parou no prazo da varredura; o resto fica para a proxima"); return (0, null, RecuperacaoSendbird.Prazo); }
            Recuo.Falhou(Agora());
            Erro(RecuperacaoSendbird.ErroTimeout);
            Diag("recuperacao erro=timeout");
            return (0, null, RecuperacaoSendbird.ErroTimeout);
        }
        catch (Exception ex)
        {
            var s = Recuo.Falhou(Agora());
            Erro(RecuperacaoSendbird.ErroRede);
            Diag($"recuperacao erro=rede ({ex.GetType().Name}) recuo={s}s");
            return (0, null, RecuperacaoSendbird.ErroRede);
        }
    }

    /// <summary>Uma vez por sessão: a forma (nomes e tipos, sem valores) da primeira mensagem real do histórico.</summary>
    private void AnotarShape(JsonElement raiz)
    {
        if (_shapeAnotado) return;
        try
        {
            var arr = raiz.ValueKind == JsonValueKind.Object && raiz.TryGetProperty("messages", out var ms) ? ms : raiz;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return;
            var m = arr[0];
            _shapeAnotado = true;
            var tipo = m.ValueKind == JsonValueKind.Object && m.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "?";
            Diag($"recuperacao shape={tipo} {ChatCaptura.DescreverShape(m.GetRawText())}");
        }
        catch { }
    }
}
