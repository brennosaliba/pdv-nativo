using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pdv.Testes;

/// <summary>
/// A RPC `pdv_autorizacao_totp` DE MENTIRA (PostgREST em 127.0.0.1): implementa o
/// contrato que o <see cref="Pdv.Nucleo.ClienteAutorizacao"/> consome.
///
/// Existe porque a RPC de verdade só nasce na migration do ERP, e porque o segredo
/// do autenticador do dono não pode viver em máquina de teste. Aqui o segredo é o
/// da RFC 6238 ("12345678901234567890") e o fake FALA TOTP DE VERDADE: os quatro
/// vetores da RFC são conferidos na suíte (FK-1..FK-4), então o que o PDV aguenta
/// aqui é o que vai aguentar contra o servidor.
///
/// O que é copiado do contrato de propósito:
///  · exige o bearer da SESSÃO do terminal (não a chave pública): sem ele, 401
///    no formato do PostgREST;
///  · janela de ±1 passo (30 s) e replay recusado (um contador vale UMA vez);
///  · rate limit: 5 falhas em 10 min por terminal_uuid (ou por sessão, se nulo)
///    e depois "muitas tentativas, aguarde" SEM testar o código;
///  · sem segredo configurado: "autenticador nao configurado";
///  · código errado: "codigo invalido", e nada mais (nem qual dono, nem resto);
///  · o segredo nunca volta no corpo, e cada tentativa vai para o log.
/// </summary>
public sealed class FakeTotp : IDisposable
{
    public const string Caminho = "/rest/v1/rpc/pdv_autorizacao_totp";

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _trava = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _falhas = new();

    public string Url { get; }

    /// <summary>Bearer que a RPC aceita: o token da sessão do terminal.</summary>
    public string Token { get; set; } = "sessao-do-terminal-de-teste";

    /// <summary>A chave pública (vai no header apikey).</summary>
    public string AnonKey { get; set; } = "chave-publica-de-teste";

    /// <summary>Segredo do dono: o ASCII da RFC 6238 (base32 GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ).</summary>
    public byte[] Segredo { get; set; } = Encoding.ASCII.GetBytes("12345678901234567890");

    /// <summary>Nome do dono que a RPC devolve como `autorizador`.</summary>
    public string Autorizador { get; set; } = "Brenno";

    /// <summary>false = nenhum owner com segredo.</summary>
    public bool Configurado { get; set; } = true;

    public int MaxFalhas { get; set; } = 5;

    /// <summary>Contador (T) do último código aceito: replay é T &lt;= isto.</summary>
    public long UltimoContador { get; set; }

    /// <summary>Relógio da RPC (o `_agora` do servidor). null = UtcNow.</summary>
    public Func<DateTimeOffset>? Relogio { get; set; }

    /// <summary>Atraso antes de responder (nuvem lenta).</summary>
    public int AtrasoMs { get; set; }

    /// <summary>Quando &gt; 0, as próximas N requisições ficam sem resposta.</summary>
    public int EngolirProximas;

    public sealed record Chamada(string Caminho, string Authorization, string ApiKey, string Corpo);

    /// <summary>Tudo que chegou, com headers: o teste confere o que o PDV mandou.</summary>
    public ConcurrentQueue<Chamada> Chamadas { get; } = new();

    public sealed record Tentativa(bool Ok, string? Motivo, string? Referencia, string? Tipo,
        string? TerminalUuid, bool TestouOCodigo, string? Id);

    /// <summary>O `pdv_autorizacao_totp_log`: toda tentativa, ok ou não.</summary>
    public ConcurrentQueue<Tentativa> Log { get; } = new();

    public FakeTotp()
    {
        var porta = PortaLivre();
        Url = $"http://127.0.0.1:{porta}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(LacoAsync);
    }

    /// <summary>TOTP da RFC 6238 (HMAC-SHA1, 6 dígitos, truncamento dinâmico).</summary>
    public static string Codigo(byte[] segredo, long unixSegundos, int passo = 30)
    {
        var contador = unixSegundos / passo;
        var msg = new byte[8];
        for (var i = 7; i >= 0; i--) { msg[i] = (byte)(contador & 0xff); contador >>= 8; }
        using var h = new HMACSHA1(segredo);
        var hash = h.ComputeHash(msg);
        var off = hash[^1] & 0x0f;
        var bin = ((hash[off] & 0x7f) << 24) | (hash[off + 1] << 16) | (hash[off + 2] << 8) | hash[off + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    private DateTimeOffset Agora => Relogio?.Invoke() ?? DateTimeOffset.UtcNow;

    /// <summary>O código que está no celular do dono agora (deslocado em N passos de 30 s).</summary>
    public string CodigoAgora(int passos = 0) => Codigo(Segredo, Agora.ToUnixTimeSeconds() + passos * 30L);

    /// <summary>Esvazia os baldes do rate limit (cada cenário da suíte começa limpo).</summary>
    public void ZerarBaldes() { lock (_trava) _falhas.Clear(); }

    private static int PortaLivre()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private async Task LacoAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => AtenderAsync(ctx));
        }
    }

    private async Task AtenderAsync(HttpListenerContext ctx)
    {
        try
        {
            string corpo;
            using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                corpo = await sr.ReadToEndAsync();
            var caminho = ctx.Request.Url?.AbsolutePath ?? "";
            Chamadas.Enqueue(new Chamada(caminho, ctx.Request.Headers["Authorization"] ?? "",
                ctx.Request.Headers["apikey"] ?? "", corpo));

            if (AtrasoMs > 0) await Task.Delay(AtrasoMs);
            if (Volatile.Read(ref EngolirProximas) > 0)
            {
                Interlocked.Decrement(ref EngolirProximas);
                ctx.Response.Abort();
                return;
            }

            if (caminho != Caminho)
            {
                await Responder(ctx, 404, new { code = "PGRST202", message = "Could not find the function", details = (string?)null, hint = (string?)null });
                return;
            }
            if (ctx.Request.Headers["Authorization"] != "Bearer " + Token)
            {
                await Responder(ctx, 401, new { code = "PGRST301", message = "JWT expired" });
                return;
            }

            string? codigo = null, referencia = null, tipo = null, terminal = null;
            try
            {
                var j = JsonDocument.Parse(corpo).RootElement;
                codigo = Txt(j, "_codigo");
                referencia = Txt(j, "_referencia");
                tipo = Txt(j, "_tipo");
                terminal = Txt(j, "_terminal_uuid");
            }
            catch { /* corpo torto vira código inválido */ }

            await Responder(ctx, 200, Decidir(codigo ?? "", referencia, tipo, terminal));
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }

    private object Decidir(string codigo, string? referencia, string? tipo, string? terminal)
    {
        lock (_trava)
        {
            var agora = Agora;
            var balde = terminal ?? "sessao";
            if (!_falhas.TryGetValue(balde, out var falhas)) _falhas[balde] = falhas = new List<DateTimeOffset>();
            falhas.RemoveAll(f => f < agora.AddMinutes(-10));

            object Nao(string motivo, bool testou)
            {
                Log.Enqueue(new Tentativa(false, motivo, referencia, tipo, terminal, testou, null));
                return new { ok = false, motivo };
            }

            // RATE LIMIT vem ANTES de tocar no código: quem estourou o balde não
            // ganha nem a informação de que o chute foi perto.
            if (falhas.Count >= MaxFalhas) return Nao("muitas tentativas, aguarde", false);
            if (!Configurado) return Nao("autenticador nao configurado", false);

            var t = agora.ToUnixTimeSeconds() / 30;
            for (var d = -1; d <= 1; d++)
            {
                var candidato = t + d;
                if (Codigo(Segredo, candidato * 30) != codigo) continue;
                if (candidato <= UltimoContador) break;          // replay: contador já gasto
                UltimoContador = candidato;
                var id = Guid.NewGuid().ToString();
                Log.Enqueue(new Tentativa(true, null, referencia, tipo, terminal, true, id));
                return new { ok = true, id, autorizador = Autorizador };
            }
            falhas.Add(agora);
            return Nao("codigo invalido", true);
        }
    }

    private static string? Txt(JsonElement e, string nome)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static async Task Responder(HttpListenerContext ctx, int status, object corpo)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(corpo));
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
