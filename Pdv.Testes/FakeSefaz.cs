using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Pdv.Testes;

/// <summary>
/// Agente fiscal de MENTIRA: responde `POST /nfce/emitir` como o agente local
/// (<see cref="Pdv.Nucleo.EmissorAgente"/>) espera, devolvendo os desfechos reais da
/// SEFAZ-MG — autorizada (cStat 100), rejeitada, contingência offline e a queda de rede.
///
/// Existe porque a SEFAZ de verdade precisa de certificado A1, CSC e o servidor na EC2,
/// e sem isso o caminho fiscal só era exercitado pelo estado local: a venda sabia que a
/// nota estava "pendente", mas ninguém testava o que acontece quando a resposta chega.
///
/// O que este fake NÃO faz: assinar XML, validar schema da SEFAZ, ou provar que o XML
/// gerado passa no autorizador. Isso só o ambiente real prova.
/// </summary>
public sealed class FakeSefaz : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private int _proximoNumero = 1;

    public string Url { get; }

    /// <summary>Desfecho de cada emissão, na ordem em que forem pedidas.</summary>
    public ConcurrentQueue<Desfecho> Roteiro { get; } = new();

    /// <summary>Notas autorizadas: chave → estado.</summary>
    public ConcurrentDictionary<string, Nota> Notas { get; } = new();

    /// <summary>Corpo de cada requisição, para o teste conferir o que foi enviado.</summary>
    public ConcurrentBag<string> Chamadas { get; } = new();

    /// <summary>Série usada nas notas emitidas (o PDV próprio usa 2+).</summary>
    public int Serie = 3;

    /// <summary>Quando true, não responde nada (agente fora do ar).</summary>
    public bool ForaDoAr;

    public enum Desfecho
    {
        /// <summary>cStat 100 — autorizada em produção.</summary>
        Autorizar,
        /// <summary>Rejeitada pelo autorizador (ex.: 539, duplicidade).</summary>
        Rejeitar,
        /// <summary>SEFAZ fora do ar: a nota sai em contingência offline.</summary>
        Contingencia,
        /// <summary>Responde 500 — falha do próprio agente.</summary>
        ErroDoAgente,
    }

    public sealed class Nota
    {
        public string Chave = "";
        public int Numero;
        public int Serie;
        public string Protocolo = "";
        public string CStat = "";
        public bool Cancelada;
        public string? MotivoCancelamento;
    }

    public FakeSefaz()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var porta = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        Url = $"http://127.0.0.1:{porta}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(LacoAsync);
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

            if (ForaDoAr) { await Task.Delay(60_000).ConfigureAwait(false); return; }

            var rota = ctx.Request.Url?.AbsolutePath ?? "";
            switch (rota)
            {
                case "/nfce/emitir":   Emitir(ctx);            return;
                case "/nfce/cancelar": Cancelar(ctx, corpo);   return;
                case "/health":        Responder(ctx, 200, """{"ok":true}"""); return;
                default: Responder(ctx, 404, """{"erro":"rota desconhecida"}"""); return;
            }
        }
        catch (Exception e)
        {
            try { Responder(ctx, 500, "{\"erro\":" + JsonSerializer.Serialize(e.Message) + "}"); }
            catch { }
        }
    }

    private void Emitir(HttpListenerContext ctx)
    {
        var desfecho = Roteiro.TryDequeue(out var d) ? d : Desfecho.Autorizar;

        if (desfecho == Desfecho.ErroDoAgente)
        {
            Responder(ctx, 500, """{"erro":"emissor indisponivel"}""");
            return;
        }

        var numero = Interlocked.Increment(ref _proximoNumero);
        // Chave de 44 dígitos, como a real. O conteúdo não é a chave verdadeira —
        // é só o formato, que é o que o PDV valida antes de gravar.
        var chave = ("31260801027058000191650" + Serie.ToString("D3")
                     + numero.ToString("D9") + "1" + numero.ToString("D8")).PadRight(44, '0')[..44];

        if (desfecho == Desfecho.Rejeitar)
        {
            Responder(ctx, 200,
                "{\"autorizado\":false,\"contingencia\":false,\"cStat\":\"539\","
                + "\"xMotivo\":\"Duplicidade de NF-e com diferenca na chave de acesso\","
                + "\"numero\":" + numero + ",\"serie\":" + Serie + ",\"tpAmb\":1}");
            return;
        }

        var contingencia = desfecho == Desfecho.Contingencia;
        var nota = new Nota
        {
            Chave = chave, Numero = numero, Serie = Serie,
            Protocolo = contingencia ? "" : "131" + numero.ToString("D12"),
            CStat = contingencia ? "" : "100",
        };
        Notas[chave] = nota;

        Responder(ctx, 200,
            "{\"autorizado\":true,\"contingencia\":" + (contingencia ? "true" : "false") + ","
            + "\"chave\":" + J(chave) + ",\"numero\":" + numero + ",\"serie\":" + Serie + ","
            + "\"protocolo\":" + J(nota.Protocolo) + ",\"cStat\":" + J(nota.CStat) + ","
            + "\"xMotivo\":" + J(contingencia ? "Emitida em contingencia offline" : "Autorizado o uso da NF-e")
            + ",\"tpAmb\":1,\"qrCode\":\"http://nfce.fazenda.mg.gov.br/qr?p=" + chave + "\"}");
    }

    private void Cancelar(HttpListenerContext ctx, string corpo)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(corpo) ? "{}" : corpo);
        var chave  = Txt(doc.RootElement, "chave");
        var motivo = Txt(doc.RootElement, "motivo") ?? Txt(doc.RootElement, "justificativa");

        if (chave is null || !Notas.TryGetValue(chave, out var nota))
        {
            Responder(ctx, 200,
                """{"ok":false,"cStat":"217","xMotivo":"NF-e nao consta na base da SEFAZ"}""");
            return;
        }

        // A SEFAZ exige justificativa de 15 a 255 caracteres no evento 110111.
        if ((motivo?.Trim().Length ?? 0) < 15)
        {
            Responder(ctx, 200,
                """{"ok":false,"cStat":"573","xMotivo":"Justificativa com menos de 15 caracteres"}""");
            return;
        }

        if (nota.Cancelada)
        {
            // Rejeição 573 é "duplicidade de evento" — cancelar de novo não é sucesso novo.
            Responder(ctx, 200,
                """{"ok":false,"cStat":"573","xMotivo":"Duplicidade de evento de cancelamento"}""");
            return;
        }

        nota.Cancelada = true;
        nota.MotivoCancelamento = motivo;
        Responder(ctx, 200,
            "{\"ok\":true,\"cStat\":\"135\",\"xMotivo\":\"Evento registrado e vinculado a NF-e\","
            + "\"chave\":" + J(nota.Chave) + ",\"protocolo\":\"131" + nota.Numero.ToString("D12") + "\"}");
    }

    private static string? Txt(JsonElement e, string campo) =>
        e.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

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
