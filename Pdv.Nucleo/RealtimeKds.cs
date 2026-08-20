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
        var join = MontarQuadro(_topico, "phx_join",
            $$"""{"config":{"broadcast":{"self":false},"private":true},"access_token":"{{token}}"}""",
            ProximoRef());
        await EnviarAsync(ws, join, ct).ConfigureAwait(false);
        Conectado = true;

        var buf = new byte[16 * 1024];
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

            using var espera = CancellationTokenSource.CreateLinkedTokenSource(ct);
            espera.CancelAfter(TimeSpan.FromSeconds(26));
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buf, espera.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { continue; }                        // silêncio de 26 s: volta pro pulso

            if (r.MessageType == WebSocketMessageType.Close) break;
            var texto = Encoding.UTF8.GetString(buf, 0, r.Count);
            if (EhSino(texto)) Ping?.Invoke();
        }
    }

    private static async Task EnviarAsync(ClientWebSocket ws, string quadro, CancellationToken ct)
        => await ws.SendAsync(Encoding.UTF8.GetBytes(quadro),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

    private string ProximoRef() => Interlocked.Increment(ref _ref).ToString();

    // ── protocolo puro (testável sem rede) ──────────────────────────────────
    public static string MontarQuadro(string topico, string evento, string payloadJson, string @ref)
        => $$"""{"topic":"{{topico}}","event":"{{evento}}","payload":{{payloadJson}},"ref":"{{@ref}}"}""";

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
