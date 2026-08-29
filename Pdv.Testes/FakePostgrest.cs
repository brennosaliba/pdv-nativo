using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Pdv.Testes;

/// <summary>
/// Supabase de MENTIRA para o simulador: implementa exatamente o contrato que a
/// Drenagem consome (auth por senha, RPCs pdv_registrar_venda / pdv_vincular_nfce /
/// pdv_sync_cancelamento, POST das tabelas de caixa e o GET de pdv_sales por
/// client_key), com INJEÇÃO DE FALHAS controlada — 503/429 aleatórios e erros
/// dirigidos por client_key. É o que permite testar a fila offline de ponta a
/// ponta, incluindo os cenários que derrubaram a fila em produção, sem tocar na
/// nuvem real.
/// </summary>
public sealed class FakePostgrest : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Random _rand;
    public string Url { get; }

    // ── estado do "banco" ───────────────────────────────────────────────────
    /// <summary>client_key da venda → sale_id na "nuvem".</summary>
    public ConcurrentDictionary<string, string> Vendas { get; } = new();
    /// <summary>client_keys canceladas via pdv_sync_cancelamento.</summary>
    public ConcurrentDictionary<string, byte> Canceladas { get; } = new();
    /// <summary>client_keys recebidas por tabela (fechamentos/movimentos/sessões) — deduplicadas como o on_conflict faria.</summary>
    public ConcurrentDictionary<string, byte> Fechamentos { get; } = new();
    public ConcurrentDictionary<string, byte> Movimentos { get; } = new();
    public ConcurrentDictionary<string, byte> Sessoes { get; } = new();
    /// <summary>Códigos de cortesia queimados via courtesy_redeem.</summary>
    public ConcurrentDictionary<string, byte> Resgatadas { get; } = new();
    public int Vinculos;

    /// <summary>
    /// A LISTA DE FUNCIONÁRIOS DO PAINEL, do jeito que `pdv_operadores_sync` devolve.
    /// É por aqui que o teste encena o encontro entre o operador que nasceu NO CAIXA e
    /// o que o painel governa — o cruzamento onde os dois viravam duas pessoas.
    /// </summary>
    public sealed record OperadorDoPainel(string Id, string Nome, string PinHash, string PinSalt,
        string Perfil = "operador", string? Cpf = null, bool Ativo = true);

    public List<OperadorDoPainel> OperadoresDoPainel { get; } = new();

    // ── injeção de falhas ───────────────────────────────────────────────────
    /// <summary>% de respostas 503 nas rotas de DADOS (auth nunca falha).</summary>
    public volatile int PctErro503;
    /// <summary>% de respostas 429 nas rotas de dados.</summary>
    public volatile int PctErro429;
    /// <summary>Falha dirigida: client_key → (status HTTP, quantas vezes ainda falhar). 0 restante = some sozinho.</summary>
    public ConcurrentDictionary<string, (int Status, int Restam)> FalhaPorChave { get; } = new();

    public FakePostgrest(int porta, int seed = 20260808)
    {
        _rand = new Random(seed);
        Url = $"http://127.0.0.1:{porta}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => Atender(ctx));
        }
    }

    private void Atender(HttpListenerContext ctx)
    {
        try
        {
            var caminho = ctx.Request.Url!.AbsolutePath;
            string corpo;
            using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                corpo = sr.ReadToEnd();

            // auth SEMPRE funciona: o que se testa aqui é a fila, não o login
            if (caminho.StartsWith("/auth/v1/token"))
            {
                Responder(ctx, 200, """{"access_token":"tok-fake","refresh_token":"ref-fake","expires_in":3600}""");
                return;
            }

            // falha dirigida tem prioridade (é o bisturi dos cenários) — mas só em
            // ESCRITA: o GET de consulta (pdv_sales) responde a verdade, senão o
            // vínculo da venda "podre" nunca descobre que ela virou órfã
            var chave = ExtrairChave(corpo) ?? ExtrairChaveDaQuery(ctx.Request.Url);
            if (chave is not null && ctx.Request.HttpMethod != "GET"
                && FalhaPorChave.TryGetValue(chave, out var f) && f.Restam > 0)
            {
                FalhaPorChave[chave] = (f.Status, f.Restam - 1);
                Responder(ctx, f.Status, $$"""{"message":"falha injetada {{f.Status}}"}""");
                return;
            }

            // caos aleatório
            int sorte; lock (_rand) sorte = _rand.Next(100);
            if (sorte < PctErro503) { Responder(ctx, 503, """{"message":"caos 503"}"""); return; }
            if (sorte < PctErro503 + PctErro429) { Responder(ctx, 429, """{"message":"caos 429"}"""); return; }

            switch (caminho)
            {
                case "/rest/v1/rpc/pdv_registrar_venda":
                {
                    if (chave is null) { Responder(ctx, 200, """{"ok":false,"error":"sem_client_key"}"""); return; }
                    var saleId = Vendas.GetOrAdd(chave, _ => Guid.NewGuid().ToString());
                    Responder(ctx, 200, $$"""{"ok":true,"sale_id":"{{saleId}}"}""");
                    return;
                }
                case "/rest/v1/rpc/pdv_operadores_sync":
                    // O painel devolve o hash PRONTO (mesmo PBKDF2 do caixa) — quem baixa
                    // só copia. Nomes de campo são CONTRATO com Nuvem.BaixarOperadoresAsync.
                    Responder(ctx, 200, JsonSerializer.Serialize(OperadoresDoPainel
                        .Select(o => new
                        {
                            id = o.Id, nome = o.Nome, pin_hash = o.PinHash, pin_salt = o.PinSalt,
                            perfil = o.Perfil, cpf = o.Cpf, ativo = o.Ativo,
                        })));
                    return;
                case "/rest/v1/rpc/pdv_vincular_nfce":
                    Interlocked.Increment(ref Vinculos);
                    Responder(ctx, 200, """{"ok":true}""");
                    return;
                case "/rest/v1/rpc/pdv_sync_cancelamento":
                {
                    if (chave is null || !Vendas.ContainsKey(chave))
                    { Responder(ctx, 200, """{"ok":false,"error":"venda_nao_encontrada"}"""); return; }
                    Canceladas.TryAdd(chave, 1);
                    Responder(ctx, 200, """{"ok":true,"sale_id":"fake"}""");
                    return;
                }
                case "/rest/v1/rpc/courtesy_redeem":
                {
                    string? code = null;
                    try {
                        using var d = JsonDocument.Parse(corpo);
                        if (d.RootElement.TryGetProperty("_code", out var cv) && cv.ValueKind == JsonValueKind.String)
                            code = cv.GetString();
                    } catch { }
                    if (string.IsNullOrWhiteSpace(code))
                    { Responder(ctx, 200, """{"ok":false,"error":"sem_code"}"""); return; }
                    // uso único: segundo resgate do mesmo código responde ja_resgatado
                    if (!Resgatadas.TryAdd(code!, 1))
                    { Responder(ctx, 200, """{"ok":false,"error":"ja_resgatado"}"""); return; }
                    Responder(ctx, 200, """{"ok":true}""");
                    return;
                }
                case "/rest/v1/pdv_caixa_fechamentos":
                    if (chave is not null) Fechamentos.TryAdd(chave, 1);
                    Responder(ctx, 201, "");
                    return;
                case "/rest/v1/pdv_caixa_movimentos":
                    if (chave is not null) Movimentos.TryAdd(chave, 1);
                    Responder(ctx, 201, "");
                    return;
                case "/rest/v1/pdv_caixa_sessoes":
                    if (chave is not null) Sessoes.TryAdd(chave, 1);
                    Responder(ctx, 201, "");
                    return;
                case "/rest/v1/pdv_sales":
                {
                    // GET ?select=id&client_key=eq.X&limit=1
                    var k = ExtrairChaveDaQuery(ctx.Request.Url);
                    Responder(ctx, 200, k is not null && Vendas.TryGetValue(k, out var id)
                        ? $$"""[{"id":"{{id}}"}]""" : "[]");
                    return;
                }
                default:
                    Responder(ctx, 404, """{"message":"rota desconhecida no fake"}""");
                    return;
            }
        }
        catch
        {
            try { Responder(ctx, 500, """{"message":"erro no fake"}"""); } catch { }
        }
    }

    /// <summary>client_key do corpo: aceita "p_client_key" (RPCs) e "client_key" (tabelas).</summary>
    private static string? ExtrairChave(string corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return null;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            foreach (var nome in new[] { "p_client_key", "client_key" })
                if (r.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            return null;
        }
        catch { return null; }
    }

    private static string? ExtrairChaveDaQuery(Uri? url)
    {
        var q = url?.Query;
        if (string.IsNullOrEmpty(q)) return null;
        foreach (var par in q.TrimStart('?').Split('&'))
            if (par.StartsWith("client_key=eq."))
                return Uri.UnescapeDataString(par["client_key=eq.".Length..]);
        return null;
    }

    private static void Responder(HttpListenerContext ctx, int status, string corpo)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(corpo);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
