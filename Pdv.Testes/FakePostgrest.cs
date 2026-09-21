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

    /// <summary>
    /// O que pdv_kds_pronto responde por pedido: "true" (marcou) ou "null" (fora do
    /// escopo da loja / inexistente, que e o que a RPC real devolve). Sem entrada = "true".
    /// </summary>
    public ConcurrentDictionary<string, string> RespostaKdsPronto { get; } = new();
    /// <summary>Pedidos cujo pronto chegou aqui (a ponte leria kds_pronto_em).</summary>
    public ConcurrentDictionary<string, int> ProntosRecebidos { get; } = new();
    public int Vinculos;

    /// <summary>
    /// COMBOS (05/09): o que pdv_combos_ativos responde (um array jsonb, um por
    /// produto-combo). "[]" = loja sem combo. E o p_escolhas que chegou por
    /// pdv_registrar_venda_composta, por client_key (o que a RPC real grava em
    /// pdv_combo_escolhas); a RPC de venda comum NUNCA recebe escolhas.
    /// </summary>
    public volatile string CombosAtivos = "[]";
    /// <summary>
    /// COMBO POR TOTAL (13/09): o que pdv_combos_ativos_v2 responde. Nulo = servidor sem a
    /// v2 (404 PGRST202): o caixa cai na v1.
    /// </summary>
    public volatile string? CombosAtivosV2;
    /// <summary>Encena o servidor SEM a RPC composta (exe publicado antes da migration): PostgREST responde 404 PGRST202.</summary>
    public volatile bool CompostaAusente;
    public ConcurrentDictionary<string, string> EscolhasRecebidas { get; } = new();
    public ConcurrentDictionary<string, int> ChamadasPorRpc { get; } = new();

    /// <summary>
    /// A LISTA DE FUNCIONÁRIOS DO PAINEL, do jeito que `pdv_operadores_sync` devolve.
    /// É por aqui que o teste encena o encontro entre o operador que nasceu NO CAIXA e
    /// o que o painel governa — o cruzamento onde os dois viravam duas pessoas.
    /// </summary>
    public sealed record OperadorDoPainel(string Id, string Nome, string PinHash, string PinSalt,
        string Perfil = "operador", string? Cpf = null, bool Ativo = true);

    public List<OperadorDoPainel> OperadoresDoPainel { get; } = new();

    /// <summary>
    /// USUÁRIO MASTER DA REDE (15/09): o corpo que pdv_master_caixa responde. Nulo encena o
    /// servidor sem a migration (404 PGRST202).
    /// </summary>
    public volatile string? MasterDoPainel;

    // ── BRINDE DA RASPADINHA (21/09/2026) ───────────────────────────────────
    /// <summary>O corpo que brinde_raspadinha_conferir devolve para um código que vale (com as regras).</summary>
    public ConcurrentDictionary<string, string> ConferirPorCodigo { get; } = new();
    /// <summary>Código queimado → client_key do brinde que o queimou (o "banco" da raspadinha).</summary>
    public ConcurrentDictionary<string, string> RaspadinhasUsadas { get; } = new();
    /// <summary>client_key → brinde_id: a idempotência da RPC real (unique em brindes.client_key).</summary>
    public ConcurrentDictionary<string, string> BrindesPorChave { get; } = new();
    /// <summary>client_key → corpo recebido (itens, operador, terminal), para a suíte conferir o que subiu.</summary>
    public ConcurrentDictionary<string, string> CorpoEntregarPorChave { get; } = new();
    /// <summary>RPC → o último Authorization recebido (prova de que foi a sessão do terminal, não a chave pública).</summary>
    public ConcurrentDictionary<string, string> BearerPorRpc { get; } = new();
    /// <summary>Encena o servidor SEM o SQL 37: as duas RPCs respondem 404 PGRST202.</summary>
    public volatile bool BrindeAusente;
    /// <summary>
    /// Encena a loja com raspadinha_no_caixa DESLIGADA, na ordem do SQL 37 (21/09, revisão):
    /// conferir recusa 'loja_sem_raspadinha' antes de olhar o código; entregar devolve o brinde
    /// da mesma chave (idempotência vem antes) e só depois recusa pela loja.
    /// </summary>
    public volatile bool LojaDesligada;
    /// <summary>Quantas próximas entregas PROCESSAM (gravam e queimam) e respondem 502: a resposta que se perde.</summary>
    public volatile int EntregarProcessaEPerde;
    /// <summary>Quantas próximas entregas respondem 503 SEM processar: a chamada que não chegou a rodar.</summary>
    public volatile int EntregarCaiAntes;
    /// <summary>Chamado com a client_key quando a entrega chega, antes de processar (a suíte olha o SQLite nessa hora).</summary>
    public Action<string>? AoReceberEntregar;

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
                    ChamadasPorRpc.AddOrUpdate("pdv_registrar_venda", 1, (_, n) => n + 1);
                    if (chave is null) { Responder(ctx, 200, """{"ok":false,"error":"sem_client_key"}"""); return; }
                    var saleId = Vendas.GetOrAdd(chave, _ => Guid.NewGuid().ToString());
                    Responder(ctx, 200, $$"""{"ok":true,"sale_id":"{{saleId}}"}""");
                    return;
                }
                case "/rest/v1/rpc/pdv_registrar_venda_composta":
                {
                    // o invólucro real: mesma venda + p_escolhas; idempotente pela
                    // client_key (repetir NAO duplica as escolhas)
                    ChamadasPorRpc.AddOrUpdate("pdv_registrar_venda_composta", 1, (_, n) => n + 1);
                    if (CompostaAusente)
                    {
                        Responder(ctx, 404, """{"code":"PGRST202","details":"Searched for the function public.pdv_registrar_venda_composta with parameters p_business_date, p_client_key, p_escolhas but no matches were found in the schema cache.","hint":null,"message":"Could not find the function public.pdv_registrar_venda_composta(p_business_date, p_client_key, p_escolhas) in the schema cache"}""");
                        return;
                    }
                    if (chave is null) { Responder(ctx, 200, """{"ok":false,"error":"sem_client_key"}"""); return; }
                    var nova = !Vendas.ContainsKey(chave);
                    var saleId = Vendas.GetOrAdd(chave, _ => Guid.NewGuid().ToString());
                    if (nova)
                    {
                        try
                        {
                            using var d = JsonDocument.Parse(corpo);
                            if (d.RootElement.TryGetProperty("p_escolhas", out var pe) && pe.ValueKind == JsonValueKind.Array)
                                EscolhasRecebidas[chave] = pe.GetRawText();
                        }
                        catch { }
                    }
                    Responder(ctx, 200, $$"""{"ok":true,"sale_id":"{{saleId}}","idempotente":{{(nova ? "false" : "true")}}}""");
                    return;
                }
                case "/rest/v1/rpc/pdv_combos_ativos_v2":
                    // combo por total (13/09): nulo encena o servidor SEM a v2 (404 PGRST202)
                    ChamadasPorRpc.AddOrUpdate("pdv_combos_ativos_v2", 1, (_, n) => n + 1);
                    if (CombosAtivosV2 is null)
                        Responder(ctx, 404, """{"code":"PGRST202","details":null,"hint":null,"message":"Could not find the function public.pdv_combos_ativos_v2(_loja) in the schema cache"}""");
                    else
                        Responder(ctx, 200, CombosAtivosV2);
                    return;
                case "/rest/v1/rpc/pdv_combos_ativos":
                    ChamadasPorRpc.AddOrUpdate("pdv_combos_ativos", 1, (_, n) => n + 1);
                    Responder(ctx, 200, CombosAtivos);
                    return;
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
                case "/rest/v1/rpc/pdv_master_caixa":
                    ChamadasPorRpc.AddOrUpdate("pdv_master_caixa", 1, (_, n) => n + 1);
                    if (MasterDoPainel is null)
                        Responder(ctx, 404, """{"code":"PGRST202","details":null,"hint":null,"message":"Could not find the function public.pdv_master_caixa without parameters in the schema cache"}""");
                    else
                        Responder(ctx, 200, MasterDoPainel);
                    return;
                case "/rest/v1/rpc/brinde_raspadinha_conferir":
                case "/rest/v1/rpc/brinde_raspadinha_entregar":
                    AtenderBrinde(ctx, caminho, corpo);
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
                case "/rest/v1/rpc/pdv_kds_pronto":
                {
                    // A RPC real devolve `true` quando carimba kds_pronto_em e `null`
                    // quando o pedido nao esta no escopo da loja do usuario (ou nao
                    // existe). O corpo e um JSON escalar, nao um objeto {ok:...}.
                    string? oid = null;
                    try {
                        using var d = JsonDocument.Parse(corpo);
                        if (d.RootElement.TryGetProperty("_order_id", out var ov) && ov.ValueKind == JsonValueKind.String)
                            oid = ov.GetString();
                    } catch { }
                    if (string.IsNullOrWhiteSpace(oid)) { Responder(ctx, 400, """{"message":"_order_id obrigatorio"}"""); return; }
                    var resposta = RespostaKdsPronto.TryGetValue(oid!, out var r) ? r : "true";
                    if (resposta == "true") ProntosRecebidos.AddOrUpdate(oid!, 1, (_, n) => n + 1);
                    Responder(ctx, 200, resposta);
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

    /// <summary>
    /// As duas RPCs do brinde, com a regra da real: conferir não queima; entregar é idempotente pela
    /// client_key ANTES de olhar o código (a mesma chave devolve o mesmo brinde), recusa código
    /// queimado e confere os itens contra as regras do código (produto real, soma por regra).
    /// </summary>
    private void AtenderBrinde(HttpListenerContext ctx, string caminho, string corpo)
    {
        var rpc = caminho[(caminho.LastIndexOf('/') + 1)..];
        ChamadasPorRpc.AddOrUpdate(rpc, 1, (_, n) => n + 1);
        BearerPorRpc[rpc] = ctx.Request.Headers["Authorization"] ?? "";
        if (BrindeAusente)
        {
            Responder(ctx, 404, $$"""{"code":"PGRST202","details":null,"hint":null,"message":"Could not find the function public.{{rpc}} in the schema cache"}""");
            return;
        }
        using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(corpo) ? "{}" : corpo);
        var r = d.RootElement;
        string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var code = S("_code") ?? "";

        if (rpc == "brinde_raspadinha_conferir")
        {
            if (LojaDesligada)
                Responder(ctx, 200, """{"ok":false,"erro":"loja_sem_raspadinha"}""");
            else if (RaspadinhasUsadas.ContainsKey(code))
                Responder(ctx, 200, """{"ok":false,"erro":"already_redeemed","codigo":"__C__","quando":"2026-09-21T16:40:00-03:00","por":"Maria Souza"}""".Replace("__C__", code));
            else if (ConferirPorCodigo.TryGetValue(code, out var ok))
                Responder(ctx, 200, ok);
            else
                Responder(ctx, 200, """{"ok":false,"erro":"not_found"}""");
            return;
        }

        var chave = S("_client_key") ?? "";
        AoReceberEntregar?.Invoke(chave);
        if (EntregarCaiAntes > 0) { EntregarCaiAntes--; Responder(ctx, 503, """{"message":"caiu antes"}"""); return; }
        if (BrindesPorChave.TryGetValue(chave, out var ja))
        {
            Responder(ctx, 200, $$"""{"ok":true,"brinde_id":"{{ja}}","idempotente":true,"falhas":0}""");
            return;
        }
        if (LojaDesligada) { Responder(ctx, 200, """{"ok":false,"erro":"loja_sem_raspadinha"}"""); return; }
        if (RaspadinhasUsadas.ContainsKey(code)) { Responder(ctx, 200, """{"ok":false,"erro":"already_redeemed"}"""); return; }
        if (!ConferirPorCodigo.TryGetValue(code, out var conferido)) { Responder(ctx, 200, """{"ok":false,"erro":"not_found"}"""); return; }

        // itens contra as regras do código: produto listado e soma exata por regra
        var regras = new Dictionary<string, (int Qtd, HashSet<string> Ids)>();
        using (var dc = JsonDocument.Parse(conferido))
            foreach (var g in dc.RootElement.GetProperty("regras").EnumerateArray())
                regras[g.GetProperty("id").GetString()!] = (g.GetProperty("quantidade").GetInt32(),
                    g.GetProperty("opcoes").EnumerateArray().Select(o => o.GetProperty("pdv_product_id").GetString()!).ToHashSet());
        var somas = regras.Keys.ToDictionary(k => k, _ => 0);
        if (!r.TryGetProperty("_itens", out var itens) || itens.ValueKind != JsonValueKind.Array || itens.GetArrayLength() == 0)
        { Responder(ctx, 200, """{"ok":false,"erro":"quantidade_errada"}"""); return; }
        foreach (var it in itens.EnumerateArray())
        {
            var regra = it.TryGetProperty("regra_id", out var rv) && rv.ValueKind == JsonValueKind.String ? rv.GetString()! : "";
            var pid = it.TryGetProperty("pdv_product_id", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString()! : "";
            if (!regras.TryGetValue(regra, out var g) || !g.Ids.Contains(pid))
            { Responder(ctx, 200, """{"ok":false,"erro":"itens_fora_da_regra"}"""); return; }
            somas[regra] += it.GetProperty("qtd").GetInt32();
        }
        if (somas.Any(kv => kv.Value != regras[kv.Key].Qtd)) { Responder(ctx, 200, """{"ok":false,"erro":"quantidade_errada"}"""); return; }

        var id = Guid.NewGuid().ToString();
        BrindesPorChave[chave] = id;
        RaspadinhasUsadas[code] = chave;
        CorpoEntregarPorChave[chave] = corpo;
        if (EntregarProcessaEPerde > 0) { EntregarProcessaEPerde--; Responder(ctx, 502, """{"message":"bad gateway"}"""); return; }
        Responder(ctx, 200, $$"""{"ok":true,"brinde_id":"{{id}}","idempotente":false,"falhas":0}""");
    }

    /// <summary>client_key do corpo: aceita "p_client_key" (RPCs), "client_key" (tabelas) e "_client_key" (brinde).</summary>
    private static string? ExtrairChave(string corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return null;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            foreach (var nome in new[] { "p_client_key", "client_key", "_client_key" })
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
