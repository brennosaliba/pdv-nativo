using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Pdv.Testes;

/// <summary>
/// Maquininha de MENTIRA: implementa o contrato que o <see cref="Pdv.Nucleo.ClienteTef"/>
/// consome da edge `tef-pagar` — as ações `criar`, `status`, `cancelar` e `estornar`.
///
/// Existe porque o TEF real (Rede Smart) precisa de hardware e credencial, e sem ele o
/// caminho do cartão — que é por onde o dinheiro entra sem passar pela gaveta — nunca era
/// exercitado. Aqui dá para roteirizar aprovação, recusa, cancelamento pelo cliente,
/// timeout, queda de rede, sessão expirada e o caso mais traiçoeiro de todos: o cliente
/// que paga no último segundo, DEPOIS de o operador mandar cancelar.
///
/// O roteiro é por cobrança: cada `criar` consome o próximo do <see cref="Roteiro"/>.
/// </summary>
public sealed class FakeTef : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    public string Url { get; }

    /// <summary>Como cada cobrança deve terminar, na ordem em que forem criadas.</summary>
    public ConcurrentQueue<Desfecho> Roteiro { get; } = new();

    /// <summary>Cobranças criadas: payment_identifier → estado corrente.</summary>
    public ConcurrentDictionary<string, Cobranca> Cobrancas { get; } = new();

    /// <summary>Toda requisição recebida, para o teste conferir o que foi enviado.</summary>
    public ConcurrentBag<string> Chamadas { get; } = new();

    /// <summary>Quantas vezes cada ação foi chamada — pega recobrança e retry indevido.</summary>
    public ConcurrentDictionary<string, int> Contagem { get; } = new();

    /// <summary>Quando > 0, as próximas N requisições não respondem (simula rede caída).</summary>
    public int EngolirProximas;

    /// <summary>Quando true, responde 401 (sessão expirada).</summary>
    public bool SessaoExpirada;

    public enum Desfecho
    {
        /// <summary>Aprova na primeira consulta.</summary>
        Aprovar,
        /// <summary>Recusa (cartão negado).</summary>
        Recusar,
        /// <summary>Fica em andamento para sempre — o teste decide quando cancelar.</summary>
        Pendurar,
        /// <summary>Gateway recusa a própria criação da cobrança.</summary>
        RecusarCriacao,
        /// <summary>Fica pendurada, mas se o operador cancelar, revela que o cliente JÁ tinha pago.</summary>
        PagarNoUltimoSegundo,
        /// <summary>Aprova, porém por um valor diferente do pedido (erro de digitação na maquininha).</summary>
        AprovarComValorDivergente,
    }

    public sealed class Cobranca
    {
        public string ChargeId = "";
        public string Pid = "";
        public decimal Valor;
        public string Tipo = "";
        public int Parcelas;
        public string? Documento;
        public Desfecho Desfecho;
        public bool Cancelada;
        public bool Estornada;
        public int ConsultasStatus;
    }

    public FakeTef()
    {
        var porta = PortaLivre();
        Url = $"http://127.0.0.1:{porta}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(LacoAsync);
    }

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
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
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
                corpo = await sr.ReadToEndAsync().ConfigureAwait(false);

            Chamadas.Add(corpo);

            // Rede caída: não responde nada, o cliente estoura o próprio relógio.
            if (Interlocked.Decrement(ref EngolirProximas) >= 0)
            {
                await Task.Delay(60_000).ConfigureAwait(false);
                return;
            }
            Interlocked.Exchange(ref EngolirProximas, Math.Max(0, EngolirProximas));

            if (SessaoExpirada) { Responder(ctx, 401, """{"error":"jwt expired"}"""); return; }

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(corpo) ? "{}" : corpo);
            var raiz = doc.RootElement;
            var acao = Txt(raiz, "acao") ?? "";
            Contagem.AddOrUpdate(acao, 1, (_, v) => v + 1);

            switch (acao)
            {
                case "criar":    Criar(ctx, raiz);    return;
                case "status":   Status(ctx, raiz);   return;
                case "cancelar": Cancelar(ctx, raiz); return;
                case "estornar": Estornar(ctx, raiz); return;
                default: Responder(ctx, 400, """{"ok":false,"error":"acao desconhecida"}"""); return;
            }
        }
        catch (Exception e)
        {
            try { Responder(ctx, 500, $$"""{"ok":false,"error":{{JsonSerializer.Serialize(e.Message)}}}"""); }
            catch { }
        }
    }

    private void Criar(HttpListenerContext ctx, JsonElement r)
    {
        var desfecho = Roteiro.TryDequeue(out var d) ? d : Desfecho.Aprovar;

        if (desfecho == Desfecho.RecusarCriacao)
        {
            Responder(ctx, 200, """{"ok":false,"error":"terminal ocupado"}""");
            return;
        }

        var c = new Cobranca
        {
            ChargeId  = Txt(r, "charge_id") ?? "",
            Pid       = "PID-" + Guid.NewGuid().ToString("N")[..12],
            Valor     = Num(r, "valor"),
            Tipo      = Txt(r, "tipo") ?? "",
            Parcelas  = (int)Num(r, "parcelas"),
            Documento = Txt(r, "documento"),
            Desfecho  = desfecho,
        };
        Cobrancas[c.Pid] = c;

        Responder(ctx, 200, $$"""
        {"ok":true,"payment_identifier":{{J(c.Pid)}},"payment_status":"AGUARDANDO_CARTAO"}
        """);
    }

    private void Status(HttpListenerContext ctx, JsonElement r)
    {
        var pid = Txt(r, "payment_identifier");
        var chargeId = Txt(r, "charge_id");
        var c = Achar(pid, chargeId);

        if (c is null) { Responder(ctx, 200, """{"ok":true,"payment_status":""}"""); return; }

        c.ConsultasStatus++;

        // Cancelada de fato: liberada, sem pagamento.
        if (c.Cancelada && c.Desfecho != Desfecho.PagarNoUltimoSegundo)
        {
            Responder(ctx, 200, $$"""
            {"ok":true,"pago":false,"andamento":false,"payment_status":"CANCELADO",
             "payment_identifier":{{J(c.Pid)}}}
            """);
            return;
        }

        switch (c.Desfecho)
        {
            case Desfecho.Aprovar:
            case Desfecho.AprovarComValorDivergente:
            case Desfecho.PagarNoUltimoSegundo when c.Cancelada:
            {
                var valor = c.Desfecho == Desfecho.AprovarComValorDivergente ? c.Valor + 10m : c.Valor;
                var vTxt = valor.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var card = "{\"cAut\":\"123456\",\"cnpj\":\"01027058000191\",\"tBand\":\"01\","
                         + "\"bandeira\":\"VISA\",\"adquirente\":\"REDE\",\"nsu\":\"000123\","
                         + "\"parcelas\":" + c.Parcelas + ",\"terminal\":\"POS-TESTE\",\"valor\":" + vTxt + "}";
                Responder(ctx, 200,
                    "{\"ok\":true,\"pago\":true,\"andamento\":false,\"payment_status\":\"PAGO\","
                    + "\"payment_identifier\":" + J(c.Pid) + ",\"card\":" + card + "}");
                return;
            }

            case Desfecho.Recusar:
                // `recusado: true` EXPLÍCITO. O contrato não deixa deduzir recusa de
                // "não está pago" — booleanos todos falsos significa "não sei ainda".
                Responder(ctx, 200,
                    "{\"ok\":true,\"pago\":false,\"andamento\":false,\"recusado\":true,"
                    + "\"payment_status\":\"RECUSADO\",\"motivo\":\"cartao negado pelo emissor\","
                    + "\"payment_identifier\":" + J(c.Pid) + "}");
                return;

            default: // Pendurar e PagarNoUltimoSegundo ainda não cancelada
                Responder(ctx, 200, $$"""
                {"ok":true,"pago":false,"andamento":true,"payment_status":"AGUARDANDO_CARTAO",
                 "payment_identifier":{{J(c.Pid)}}}
                """);
                return;
        }
    }

    private void Cancelar(HttpListenerContext ctx, JsonElement r)
    {
        var c = Achar(Txt(r, "payment_identifier"), null);
        if (c is null) { Responder(ctx, 200, """{"ok":false,"error":"cobranca nao encontrada"}"""); return; }
        c.Cancelada = true;
        Responder(ctx, 200, """{"ok":true}""");
    }

    private void Estornar(HttpListenerContext ctx, JsonElement r)
    {
        var c = Achar(Txt(r, "payment_identifier"), null);
        if (c is null) { Responder(ctx, 200, """{"ok":false,"error":"cobranca nao encontrada"}"""); return; }
        c.Estornada = true;
        Responder(ctx, 200, """{"ok":true}""");
    }

    private Cobranca? Achar(string? pid, string? chargeId)
    {
        if (!string.IsNullOrWhiteSpace(pid) && Cobrancas.TryGetValue(pid, out var c)) return c;
        if (!string.IsNullOrWhiteSpace(chargeId))
            return Cobrancas.Values.FirstOrDefault(x => x.ChargeId == chargeId);
        return null;
    }

    private static string? Txt(JsonElement e, string campo) =>
        e.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal Num(JsonElement e, string campo) =>
        e.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

    private static string J(string s) => JsonSerializer.Serialize(s);

    private static void Responder(HttpListenerContext ctx, int status, string json)
    {
        var b = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = b.Length;
        ctx.Response.OutputStream.Write(b, 0, b.Length);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
        _cts.Dispose();
    }
}
