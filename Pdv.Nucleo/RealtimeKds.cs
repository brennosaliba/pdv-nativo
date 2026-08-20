using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Pdv.Nucleo;

/// <summary>
/// O SINO do KDS: cliente Realtime (websocket Phoenix) que escuta o canal
/// privado <c>kds:&lt;loja&gt;</c>. O servidor manda um toque quando entra pedido —
/// e SÓ um toque: o dado continua descendo pela RPC com sessão e escopo.
///
/// Filosofia igual ao resto do PDV: isto é ACELERADOR, não dependência. O
/// polling de 10 s continua embaixo como rede de segurança; se o socket cair,
/// ninguém percebe além do pedido levar alguns segundos a mais. Por isso:
/// reconexão infinita com recuo, e NENHUMA exceção escapa deste arquivo.
///
/// Protocolo (vsn=1.0.0): quadros JSON {topic, event, payload, ref}.
/// join   -> {"topic":"realtime:kds:X","event":"phx_join","payload":{...},"ref":"1"}
/// pulso  -> {"topic":"phoenix","event":"heartbeat","payload":{},"ref":"n"} a cada 25 s
///           (sem pulso o servidor derruba em ~60 s)
/// sino   -> event "broadcast" com payload.event == "novo_pedido"
/// </summary>
public sealed class RealtimeKds : IDisposable
{
    private readonly string _wsUrl;
    private readonly string _anonKey;
    private readonly Func<Task<string?>> _token;
    private readonly string _topico;
    private readonly CancellationTokenSource _vida = new();
    private int _ref;

    /// <summary>Tocou o sino: chegou pedido na loja. Disparado em thread de fundo —
    /// quem assina faz o Dispatcher.</summary>
    public event Action? Ping;

    /// <summary>O painel publicou catálogo ou mexeu em promoção: o PDV baixa
    /// sozinho. É o "webhook" que o dono pediu — na única forma que alcança
    /// uma máquina atrás do NAT da loja.</summary>
    public event Action? CatalogoMudou;

    public bool Conectado { get; private set; }

    public RealtimeKds(string urlNuvem, string anonKey, Func<Task<string?>> tokenAsync, string loja)
    {
        _wsUrl = urlNuvem.Replace("https://", "wss://").TrimEnd('/')
               + $"/realtime/v1/websocket?apikey={anonKey}&vsn=1.0.0";
        _anonKey = anonKey;
        _token = tokenAsync;
        _topico = "realtime:kds:" + loja;
    }

    public void Iniciar() => _ = Task.Run(() => LacoAsync(_vida.Token));

    private async Task LacoAsync(CancellationToken ct)
    {
        var recuo = 5;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SessaoAsync(ct).ConfigureAwait(false);
                recuo = 5;                       // conexão valeu: zera o recuo
            }
            catch { /* rede é rede */ }
            finally { Conectado = false; }

            try { await Task.Delay(TimeSpan.FromSeconds(recuo), ct).ConfigureAwait(false); }
            catch { break; }
            recuo = Math.Min(recuo * 2, 60);
        }
    }

    private async Task SessaoAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(_wsUrl), ct).ConfigureAwait(false);

        // canal privado: o access_token é o que a política de recebimento avalia
        var token = await _token().ConfigureAwait(false) ?? _anonKey;
        var refJoin = ProximoRef();
        var join = MontarQuadro(_topico, "phx_join",
            $$"""{"config":{"broadcast":{"self":false},"private":true},"access_token":"{{token}}"}""",
            refJoin);
        await EnviarAsync(ws, join, ct).ConfigureAwait(false);

        // O join TEM resposta - e canal privado com token vencido e RECUSADO.
        // A 1a versao marcava Conectado logo apos enviar e virava zumbi: socket
        // saudavel, canal surdo, pra sempre (o token novo so vem reconectando).
        // Aqui: sem phx_reply ok em 10 s, derruba e deixa o laco reconectar -
        // cada tentativa busca token fresco no _token().
        var buf = new byte[16 * 1024];
        var prazoJoin = DateTime.UtcNow.AddSeconds(10);
        var confirmado = false;
        while (!confirmado && DateTime.UtcNow < prazoJoin)
        {
            var quadro = await ReceberQuadroAsync(ws, buf, TimeSpan.FromSeconds(10), ct)
                .ConfigureAwait(false);
            if (quadro is null) break;
            switch (JulgarJoin(quadro, refJoin))
            {
                case true:  confirmado = true; break;
                case false: return;                      // recusado: reconecta com token novo
                case null:  continue;                    // outro quadro qualquer
            }
        }
        if (!confirmado) return;
        Conectado = true;

        // canal do CATÁLOGO (rede inteira): best-effort — se o join falhar, o
        // botão Sincronizar continua sendo o caminho, como sempre foi
        await EnviarAsync(ws, MontarQuadro("realtime:kds:catalogo", "phx_join",
            $$"""{"config":{"broadcast":{"self":false},"private":true},"access_token":"{{token}}"}""",
            ProximoRef()), ct).ConfigureAwait(false);

        var ultimoPulso = DateTime.UtcNow;
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            // pulso a cada 25 s, intercalado com a escuta
            if ((DateTime.UtcNow - ultimoPulso).TotalSeconds >= 25)
            {
                await EnviarAsync(ws, MontarQuadro("phoenix", "heartbeat", "{}", ProximoRef()), ct)
                    .ConfigureAwait(false);
                ultimoPulso = DateTime.UtcNow;
            }

            string? texto;
            try
            {
                texto = await ReceberQuadroAsync(ws, buf, TimeSpan.FromSeconds(26), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { continue; }                        // silencio de 26 s: volta pro pulso

            if (texto is null) break;            // close do servidor
            // JWT venceu no meio da sessao: o servidor fecha SO o canal
            // (phx_close/phx_error) e o socket segue vivo - sem isto o cliente
            // ficava surdo achando que estava conectado
            if (FechouCanal(texto, _topico)) break;
            if (EhSino(texto)) Ping?.Invoke();
            if (EhCatalogo(texto)) CatalogoMudou?.Invoke();
        }
    }

    /// <summary>
    /// Recebe UMA mensagem completa (juntando fragmentos - quadro maior que o
    /// buffer chega partido e cada pedaco sozinho e JSON invalido). Teto de
    /// 256 KB: acima disso descarta em vez de crescer sem limite.
    /// Devolve null no Close do servidor. Timeout vira OperationCanceledException.
    /// </summary>
    private static async Task<string?> ReceberQuadroAsync(
        ClientWebSocket ws, byte[] buf, TimeSpan prazo, CancellationToken ct)
    {
        using var espera = CancellationTokenSource.CreateLinkedTokenSource(ct);
        espera.CancelAfter(prazo);
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, espera.Token).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (ms.Length > 256 * 1024) return "";   // grande demais: descarta, segue vivo
            if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    private static async Task EnviarAsync(ClientWebSocket ws, string quadro, CancellationToken ct)
        => await ws.SendAsync(Encoding.UTF8.GetBytes(quadro),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

    private string ProximoRef() => Interlocked.Increment(ref _ref).ToString();

    // ── protocolo puro (testável sem rede) ──────────────────────────────────
    public static string MontarQuadro(string topico, string evento, string payloadJson, string @ref)
        => $$"""{"topic":"{{topico}}","event":"{{evento}}","payload":{{payloadJson}},"ref":"{{@ref}}"}""";

    /// <summary>
    /// Resposta do join? true = aceito; false = RECUSADO (token vencido/politica);
    /// null = quadro que nao e a resposta deste join.
    /// </summary>
    public static bool? JulgarJoin(string quadroJson, string refJoin)
    {
        try
        {
            using var doc = JsonDocument.Parse(quadroJson);
            if (!doc.RootElement.TryGetProperty("event", out var ev)
                || ev.GetString() != "phx_reply") return null;
            if (!doc.RootElement.TryGetProperty("ref", out var rf)
                || rf.GetString() != refJoin) return null;
            return doc.RootElement.TryGetProperty("payload", out var p)
                && p.TryGetProperty("status", out var st)
                && st.GetString() == "ok";
        }
        catch { return null; }
    }

    /// <summary>O servidor fechou o NOSSO canal (phx_close/phx_error no topico)?</summary>
    public static bool FechouCanal(string quadroJson, string topico)
    {
        try
        {
            using var doc = JsonDocument.Parse(quadroJson);
            return doc.RootElement.TryGetProperty("topic", out var t)
                && t.GetString() == topico
                && doc.RootElement.TryGetProperty("event", out var ev)
                && ev.GetString() is "phx_close" or "phx_error";
        }
        catch { return false; }
    }

    /// <summary>Publicação de catálogo/promoção? (event=broadcast, payload.event=catalogo)</summary>
    public static bool EhCatalogo(string quadroJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(quadroJson);
            if (!doc.RootElement.TryGetProperty("event", out var ev)
                || ev.GetString() != "broadcast") return false;
            return doc.RootElement.TryGetProperty("payload", out var p)
                && p.TryGetProperty("event", out var pe)
                && pe.GetString() == "catalogo";
        }
        catch { return false; }
    }

    /// <summary>É um toque de sino? (event=broadcast e payload.event=novo_pedido)</summary>
    public static bool EhSino(string quadroJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(quadroJson);
            if (!doc.RootElement.TryGetProperty("event", out var ev)
                || ev.GetString() != "broadcast") return false;
            return doc.RootElement.TryGetProperty("payload", out var p)
                && p.TryGetProperty("event", out var pe)
                && pe.GetString() == "novo_pedido";
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _vida.Cancel();
        _vida.Dispose();
    }
}
