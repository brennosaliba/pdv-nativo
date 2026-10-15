using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pdv.Nucleo;

/// <summary>O que o servidor do Sendbird devolveu para um quadro do caixa: o eco (deu, com o msg_id) ou o EROR (código e mensagem crua).</summary>
public sealed record EcoDoSocket(bool Deu, string? MsgId, int? Codigo, string? Mensagem);

/// <summary>
/// 1.0.39: as portas da camada C para o navegador (na tela, o CDP; na bateria, falsas). Só API padrão do
/// navegador: os WebSocket abertos do Sendbird achados pela heap, o send de um deles e a liberação do grupo
/// de objetos do CDP.
/// </summary>
public interface IPortasDoSocket
{
    /// <summary>As URLs dos WebSocket do Sendbird ABERTOS (readyState 1), na ordem em que a heap os deu. O índice é o do envio.</summary>
    Task<IReadOnlyList<string?>> AbertosAsync();
    /// <summary>Manda o quadro no socket deste índice. true: o send foi chamado; false: o socket já não estava aberto (nada saiu). Exceção: não se sabe.</summary>
    Task<bool> EnviarAsync(int indice, string quadro);
    /// <summary>Solta o grupo de objetos do CDP (sempre, no finally).</summary>
    Task LiberarAsync();
}

/// <summary>O que a camada C precisa para UM envio. As URLs ficam só em memória: nunca saem em log nem diagnóstico.</summary>
/// <param name="UrlDoCanal">A URL do requestId do CDP que recebeu o último quadro daquele canal (pista a).</param>
/// <param name="UsuarioDaLoja">O user_id da loja já aprendido na conexão do chat de clientes (pista b).</param>
public sealed record PedidoPeloSocket(string Canal, string OrderUuid, string Texto, string ReqId, string? UrlDoCanal, string? UsuarioDaLoja);

/// <summary>
/// O resultado da camada C no vocabulário do envio de hoje: Ok com o msg_id do eco; ou Erro
/// "erro_envio" (EROR do servidor: falha certa), "sem_eco" (incerta: nunca reenviar), "sdk_ausente"
/// (nenhum socket, ou ambíguo: nada saiu) ou "canal_errado". O detalhe já vem pela lista branca.
/// </summary>
public sealed record ResultadoDoSocket(bool Ok, string? Erro, string? MsgId, JsonObject Detalhe, string Escolha, int Abertos);

/// <summary>
/// 1.0.39: as esperas pelo eco, por req_id. O quadro RECEBIDO (MESG ou EROR) com o req_id de um envio do
/// caixa resolve a espera dele; o resto passa reto. Seguro entre threads (a tela entrega os quadros do CDP).
/// </summary>
public sealed class EcosDoSocket
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<EcoDoSocket>> _esperas = new(StringComparer.Ordinal);

    public int Esperando => _esperas.Count;

    /// <summary>Começa a esperar o eco deste req_id (antes do send: o eco pode chegar antes de o send voltar).</summary>
    public Task<EcoDoSocket> Aguardar(string reqId)
    {
        if (_esperas.Count > 100) _esperas.Clear();   // teto: nada fica preso para sempre
        var tcs = new TaskCompletionSource<EcoDoSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        _esperas[reqId] = tcs;
        return tcs.Task;
    }

    /// <summary>Um quadro recebido. Devolve true quando era o eco de um envio que esperava.</summary>
    public bool Ver(string? payload)
    {
        try
        {
            if (_esperas.IsEmpty || string.IsNullOrEmpty(payload) || !payload.Contains("pdv", StringComparison.Ordinal)) return false;
            if (EnvioPeloSocket.LerEco(payload) is not { } e) return false;
            if (!_esperas.TryRemove(e.ReqId, out var tcs)) return false;
            tcs.TrySetResult(e.Eco);
            return true;
        }
        catch { return false; }
    }

    public void Cancelar(string reqId)
    {
        if (_esperas.TryRemove(reqId, out var tcs)) tcs.TrySetCanceled();
    }
}

/// <summary>
/// 1.0.39 (15/10/2026, Savassi, pedido 5403): A CAMADA C DO ENVIO, PELO SOCKET DO SENDBIRD.
///
/// Na Savassi o caixa 1.0.38 não achava o SDK (ele nasce num chunk que o webpack, o React, o singleton, a
/// heap e o berço não alcançam), com a conexão do Gestor VIVA: 6 sockets abertos e quadros chegando. Desde
/// 12:54 o robô não respondeu ninguém. Decisão do dono: não procurar mais o SDK dentro do Gestor. A camada C
/// usa só API padrão do navegador e o protocolo de fio do Sendbird, que o caixa já lê:
///  1. só quando as camadas de hoje não acham o SDK (a lista vazia do script, <see cref="Cabe"/>);
///  2. os WebSocket abertos do Sendbird pela heap (a tela, pelo CDP);
///  3. UM socket, sem chute (<see cref="Escolher"/>): o do canal, o do usuário da loja, o único, ou nenhum;
///  4. UM quadro MESG (<see cref="QuadroMesg"/>) com req_id único por tentativa;
///  5. a confirmação SÓ pelo eco: MESG com o mesmo req_id deu; EROR com o mesmo req_id é falha certa; sem
///     eco no prazo é incerta, e incerta NUNCA é reenviada (regra do 176/185).
/// Nenhum token, URL com chave, session key ou texto de cliente vai para log ou diagnóstico.
/// </summary>
public static class EnvioPeloSocket
{
    public const string Via = "socket";
    public const string PorCanal = "canal";
    public const string PorUsuario = "usuario";
    public const string Unico = "unico";
    public const string Ambiguo = "ambiguo";
    public const string Nenhum = "nenhum";

    /// <summary>As escolhas do socket, no contrato do diagnóstico e do detalhe.</summary>
    public static readonly IReadOnlySet<string> Escolhas = new HashSet<string>(StringComparer.Ordinal)
    {
        PorCanal, PorUsuario, Unico, Ambiguo, Nenhum,
    };

    /// <summary>Quanto o caixa espera o eco do servidor depois do send.</summary>
    public static readonly TimeSpan PrazoDoEco = TimeSpan.FromSeconds(12);

    private static readonly JsonSerializerOptions JsonDoQuadro = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// A camada C cabe? Só quando o script respondeu que a lista de instâncias do SDK está vazia
    /// (erro sdk_ausente com a etapa sdk_ausente). SDK achado, recusa do SDK, token, sem resposta ou
    /// script ausente: não.
    /// </summary>
    public static bool Cabe(string? erro, JsonObject? detalhe)
        => erro == "sdk_ausente"
           && detalhe?["etapa"] is JsonValue v && v.TryGetValue<string>(out var e) && e == "sdk_ausente";

    /// <summary>Roda a camada C só se <see cref="Cabe"/>; senão devolve null e não a chama.</summary>
    public static async Task<ResultadoDoSocket?> SeCouberAsync(string? erro, JsonObject? detalhe, Func<Task<ResultadoDoSocket>> camadaC)
        => Cabe(erro, detalhe) ? await camadaC().ConfigureAwait(false) : null;

    /// <summary>As etapas que só a camada C (o C#) produz.</summary>
    public static readonly IReadOnlySet<string> EtapasDoSocket = new HashSet<string>(StringComparer.Ordinal)
    {
        "sem_socket", "socket_ambiguo", "falhou_socket", "sem_eco",
    };

    /// <summary>
    /// O detalhe que veio da PÁGINA (dado não confiável) sem as marcas que só a camada C põe: via, escolha,
    /// abertos e as etapas do socket (viram "geral"). Sem isto, a página poderia dizer "falhou_socket" e
    /// fazer o laço tratar um quadro visto saindo como falha certa (e mandar de novo).
    /// </summary>
    public static JsonObject? SemMarcaDaPagina(JsonObject? d)
    {
        if (d is null) return null;
        var o = (JsonObject)d.DeepClone();
        o.Remove("via"); o.Remove("escolha"); o.Remove("abertos");
        if (o["etapa"] is JsonValue v && v.TryGetValue<string>(out var e) && EtapasDoSocket.Contains(e)) o["etapa"] = "geral";
        return o.Count == 0 ? null : o;
    }

    /// <summary>O req_id de uma tentativa: "pdv" + saída + "-" + tentativa + "-" + ms. Único por tentativa.</summary>
    public static string ReqId(long saidaId, int tentativa, long ms) => $"pdv{saidaId}-{tentativa}-{ms}";

    /// <summary>O req_id é de um quadro do caixa (e não do Gestor)?</summary>
    public static bool EhNosso(string? reqId) => reqId is { Length: > 3 } r && r.StartsWith("pdv", StringComparison.Ordinal);

    /// <summary>
    /// O quadro MESG: "MESG" + o JSON (channel_url, message, data, custom_type, mention_type,
    /// mentioned_user_ids, req_id, nessa ordem e sem campo indefinido) + "\n".
    /// </summary>
    public static string QuadroMesg(string canal, string texto, string reqId)
    {
        var o = new JsonObject
        {
            ["channel_url"] = canal,
            ["message"] = texto,
            ["data"] = "",
            ["custom_type"] = "",
            ["mention_type"] = "users",
            ["mentioned_user_ids"] = new JsonArray(),
            ["req_id"] = reqId,
        };
        return "MESG" + o.ToJsonString(JsonDoQuadro) + "\n";
    }

    /// <summary>
    /// Escolhe UM socket entre os abertos, sem chute: (a) o da URL do canal; (b) o do usuário da loja; (c)
    /// o único (URLs iguais são o mesmo usuário), desde que não seja de OUTRO usuário quando o da loja é
    /// conhecido; (d) senão nenhum (ambíguo). Devolve o índice (-1 quando não há) e a escolha.
    /// </summary>
    public static (int Indice, string Escolha) Escolher(IReadOnlyList<string?> abertos, string? urlDoCanal, string? usuarioDaLoja)
    {
        var validos = new List<int>();
        for (var i = 0; i < abertos.Count; i++)
            if (QuadroSendbird.EhSocketDoSendbird(abertos[i])) validos.Add(i);
        if (validos.Count == 0) return (-1, Nenhum);
        if (Chave(urlDoCanal) is { } kc)
            foreach (var i in validos)
                if (Chave(abertos[i]) == kc) return (i, PorCanal);
        if (!string.IsNullOrWhiteSpace(usuarioDaLoja))
            foreach (var i in validos)
                if (QuadroSendbird.UserIdDaUrl(abertos[i]) == usuarioDaLoja) return (i, PorUsuario);
        if (validos.Select(i => Chave(abertos[i])).Distinct(StringComparer.Ordinal).Count() == 1)
        {
            // o único socket é de outro usuário (o da loja é conhecido pelo canal ou pelo aprendizado): não manda como ele
            var dele = QuadroSendbird.UserIdDaUrl(abertos[validos[0]]);
            var daLoja = !string.IsNullOrWhiteSpace(usuarioDaLoja) ? usuarioDaLoja : QuadroSendbird.UserIdDaUrl(urlDoCanal);
            if (dele is not null && !string.IsNullOrWhiteSpace(daLoja) && dele != daLoja) return (-1, Ambiguo);
            return (validos[0], Unico);
        }
        return (-1, Ambiguo);
    }

    /// <summary>A URL normalizada para comparar a do CDP com a do ws.url (a barra do caminho, maiúsculas do host).</summary>
    private static string? Chave(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try { return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) ? u.AbsoluteUri : url.Trim(); }
        catch { return url.Trim(); }
    }

    /// <summary>
    /// O eco de um quadro do caixa: MESG com req_id "pdv..." (deu, com o msg_id) ou EROR com req_id "pdv..."
    /// (não deu, com o código e a mensagem crua, que só sai daqui limpa). Qualquer outro quadro: null.
    /// </summary>
    public static (string ReqId, EcoDoSocket Eco)? LerEco(string? payload)
    {
        try
        {
            var cmd = QuadroSendbird.Comando(payload);
            if (cmd is not ("MESG" or "EROR")) return null;
            using var d = JsonDocument.Parse(Corpo(payload!));
            var r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var req = r.TryGetProperty("req_id", out var rq) && rq.ValueKind == JsonValueKind.String ? rq.GetString() : null;
            if (!EhNosso(req)) return null;
            if (cmd == "MESG")
            {
                string? id = null;
                if (r.TryGetProperty("msg_id", out var mi))
                    id = mi.ValueKind switch
                    {
                        JsonValueKind.Number => mi.GetRawText(),
                        JsonValueKind.String => mi.GetString(),
                        _ => null,
                    };
                return (req!, new EcoDoSocket(true, string.IsNullOrWhiteSpace(id) ? null : id, null, null));
            }
            int? codigo = r.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var ci) ? ci : null;
            var msg = r.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return (req!, new EcoDoSocket(false, null, codigo, msg));
        }
        catch { return null; }
    }

    /// <summary>O channel_url de qualquer quadro (MESG, READ, BRDM...), para saber qual conexão recebeu aquele canal.</summary>
    public static string? CanalDoQuadro(string? payload)
    {
        try
        {
            if (string.IsNullOrEmpty(payload) || payload.Length < 5 || !payload.Contains("\"channel_url\"", StringComparison.Ordinal)) return null;
            if (QuadroSendbird.Comando(payload) is null) return null;
            using var d = JsonDocument.Parse(Corpo(payload));
            return d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("channel_url", out var c)
                   && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } s ? s : null;
        }
        catch { return null; }
    }

    private static readonly Regex ReNomeDeCampo = new(@"^[a-z][a-z0-9_]{0,39}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Os NOMES dos campos de um MESG (nunca os valores), em ordem alfabética, até 30: para conferir o formato
    /// do quadro do Gestor com o do caixa. Nome que não tem cara de nome (segredo, URL, texto) cai. Não MESG: null.
    /// </summary>
    public static IReadOnlyList<string>? CamposDoMesg(string? payload)
    {
        try
        {
            if (QuadroSendbird.Comando(payload) != "MESG") return null;
            using var d = JsonDocument.Parse(Corpo(payload!));
            if (d.RootElement.ValueKind != JsonValueKind.Object) return null;
            return d.RootElement.EnumerateObject().Select(p => p.Name).Where(n => ReNomeDeCampo.IsMatch(n))
                .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).Take(30).ToList();
        }
        catch { return null; }
    }

    private static string Corpo(string payload) => payload[4..].TrimEnd('\n', '\r');

    // ── o mascaramento por FORMATO ───────────────────────────────────────────
    private static readonly Regex ReJwt = new(@"eyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}(?:\.[A-Za-z0-9_-]*)?", RegexOptions.CultureInvariant);
    private static readonly Regex ReUrl = new(@"[a-zA-Z][a-zA-Z0-9+.\-]*://\S*", RegexOptions.CultureInvariant);
    private static readonly Regex ReOpaco = new(@"[A-Za-z0-9%+/_=-]{24,}", RegexOptions.CultureInvariant);

    /// <summary>
    /// Tira de um texto livre (para log) o que tem CARA de segredo, venha com o nome que vier: JWT, URL
    /// inteira (o socket leva user_id e chave na query) e qualquer sequência opaca de 24 ou mais caracteres
    /// com dígito (session key, access token).
    /// </summary>
    public static string Mascarar(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return "";
        var t = ReJwt.Replace(texto, "#");
        t = ReUrl.Replace(t, "url");
        t = ReOpaco.Replace(t, m => m.Value.Any(char.IsDigit) ? "#" : m.Value);
        return t;
    }

    // ── o envio ──────────────────────────────────────────────────────────────

    /// <summary>
    /// UM envio pela camada C. Confere o canal (do iFood e do pedido), acha os sockets abertos, escolhe UM,
    /// manda UM quadro e espera o eco pelo req_id. O grupo do CDP é liberado sempre (finally). Nunca lança.
    /// <paramref name="antesDeEnviar"/> roda logo antes do send (a confirmação pelo quadro passa a valer pelo
    /// socket: sem eco, incerta).
    /// </summary>
    public static async Task<ResultadoDoSocket> EnviarAsync(IPortasDoSocket portas, EcosDoSocket ecos, PedidoPeloSocket p,
        TimeSpan? prazo = null, Func<TimeSpan, Task>? esperar = null, Action? antesDeEnviar = null)
    {
        var relogio = Stopwatch.StartNew();
        var abertos = 0;
        var escolha = Nenhum;
        var esperando = false;
        ResultadoDoSocket Fim(bool ok, string? erro, string? msgId, string? etapa, int? codigo = null, string? msg = null)
            => new(ok, erro, msgId, Detalhe(etapa, escolha, abertos, codigo, msg, (int)Math.Min(60_000, relogio.ElapsedMilliseconds), p), escolha, abertos);

        if (CanalIfood.Ler(p.Canal) is not { } ids || !string.Equals(ids.OrderId, p.OrderUuid, StringComparison.OrdinalIgnoreCase))
            return Fim(false, "canal_errado", null, "canal_errado");
        try
        {
            IReadOnlyList<string?> urls;
            try { urls = await portas.AbertosAsync().ConfigureAwait(false) ?? Array.Empty<string?>(); }
            catch { return Fim(false, "sdk_ausente", null, "sem_socket"); }
            abertos = urls.Count(QuadroSendbird.EhSocketDoSendbird);
            var (indice, esc) = Escolher(urls, p.UrlDoCanal, p.UsuarioDaLoja);
            escolha = esc;
            if (indice < 0) return Fim(false, "sdk_ausente", null, esc == Ambiguo ? "socket_ambiguo" : "sem_socket");

            var quadro = QuadroMesg(p.Canal, p.Texto, p.ReqId);
            var eco = ecos.Aguardar(p.ReqId);
            esperando = true;
            try { antesDeEnviar?.Invoke(); } catch { }
            bool saiu;
            try { saiu = await portas.EnviarAsync(indice, quadro).ConfigureAwait(false); }
            catch { saiu = true; }   // pode ter saído: quem decide é o eco
            if (!saiu) return Fim(false, "sdk_ausente", null, "sem_socket");

            var espera = (esperar ?? (t => Task.Delay(t)))(prazo ?? PrazoDoEco);
            var fim = await Task.WhenAny(eco, espera).ConfigureAwait(false);
            if (fim != eco || !eco.IsCompletedSuccessfully) return Fim(false, "sem_eco", null, "sem_eco");
            var e = eco.Result;
            if (e.Deu) return Fim(true, null, e.MsgId, null);
            return Fim(false, "erro_envio", null, "falhou_socket", e.Codigo, e.Mensagem);
        }
        catch { return Fim(false, esperando ? "sem_eco" : "sdk_ausente", null, esperando ? "sem_eco" : "sem_socket"); }
        finally
        {
            if (esperando) ecos.Cancelar(p.ReqId);
            try { await portas.LiberarAsync().ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>
    /// O detalhe da camada C, já pela lista branca do ERP (etapa, via, escolha, abertos, código, mensagem limpa,
    /// ms). A mensagem do EROR perde os segredos deste envio (texto, canal, pedido, usuário, URLs) e tudo que
    /// tem cara de segredo, antes da limpeza de sempre.
    /// </summary>
    private static JsonObject Detalhe(string? etapa, string escolha, int abertos, int? codigo, string? msg, int ms, PedidoPeloSocket p)
    {
        var o = new JsonObject();
        if (etapa is not null) o["etapa"] = etapa;
        o["via"] = Via;
        o["escolha"] = escolha;
        o["abertos"] = Math.Clamp(abertos, 0, 20);
        if (codigo is int c) o["codigo"] = c;
        if (msg is not null)
        {
            var semSegredo = ConversaRaspadinha.SemSegredos(msg, new[] { p.Texto, p.Canal, p.OrderUuid, p.UsuarioDaLoja, p.UrlDoCanal });
            if (ConversaRaspadinha.MsgDoErroLimpa(Mascarar(semSegredo)) is { } limpa) o["msg"] = limpa;
        }
        o["ms"] = Math.Clamp(ms, 0, 60_000);
        return o;
    }

    /// <summary>A linha do rastro local: só a via, a escolha, as contagens, a etapa e o código (nunca URL, req_id cru do Gestor ou texto).</summary>
    public static string Linha(ResultadoDoSocket r)
        => $"socket: ok={(r.Ok ? "true" : "false")} erro={r.Erro ?? "-"} escolha={r.Escolha} abertos={r.Abertos}"
           + (r.MsgId is { } id && Regex.IsMatch(id, "^[0-9]{1,20}$") ? " msg_id=" + id : "")
           + ConversaRaspadinha.LinhaDoDetalhe(r.Detalhe);
}
