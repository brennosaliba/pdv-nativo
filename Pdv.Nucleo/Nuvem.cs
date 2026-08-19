using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>
/// Ponte com o backend (Supabase). O caixa NUNCA espera a nuvem pra vender: tudo que
/// ele precisa (catálogo, operadores) é espelhado no SQLite local. A nuvem entra
/// para (a) trazer o que mudou e (b) receber o que foi vendido.
///
/// A credencial é do TERMINAL, não da pessoa — o operador não deve ter conta no
/// sistema de gestão. Ela fica cifrada na máquina e o app renova a sessão sozinho.
/// </summary>
public sealed class Nuvem
{
    public const string ProjectRef = "ctwjedradxhlmqmdmbif";
    public const string UrlPadrao = "https://" + ProjectRef + ".supabase.co";
    public const string AnonKey =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImN0d2plZHJhZHhobG1xbWRtYmlmIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NzYxMDY4OTYsImV4cCI6MjA5MTY4Mjg5Nn0.bApYhWCP5lYHBGe5WFpSI1ga5pQv3G4kCHkwQhmLnwI";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly SemaphoreSlim _renovando = new(1, 1);
    private string? _token;
    private string? _refresh;
    private DateTime _expira = DateTime.MinValue;
    private readonly string _url;

    public Nuvem(string? url = null) => _url = (url ?? UrlPadrao).TrimEnd('/');

    /// <summary>
    /// De onde vêm e-mail e senha do TERMINAL para o re-login silencioso, quando nem o
    /// refresh_token serve mais. Fica como delegate porque as credenciais são cifradas
    /// com DPAPI lá no app, e o núcleo não conhece essa camada.
    /// </summary>
    public Func<(string email, string senha)?>? Credenciais { get; set; }

    public async Task<bool> EntrarAsync(string email, string senha)
        => await AutenticarAsync("password",
            new { email = email.Trim(), password = senha }, CancellationToken.None);

    private async Task<bool> AutenticarAsync(string grant, object corpo, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_url}/auth/v1/token?grant_type={grant}");
            req.Headers.TryAddWithoutValidation("apikey", AnonKey);
            req.Content = new StringContent(JsonSerializer.Serialize(corpo), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // refresh_token recusado não volta a funcionar sozinho: descarta, senão
                // toda chamada seguinte gasta um round-trip para levar o mesmo 400.
                if (grant == "refresh_token") _refresh = null;
                return false;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            _token = doc.RootElement.GetProperty("access_token").GetString();
            _refresh = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : _refresh;
            var seg = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            _expira = DateTime.Now.AddSeconds(seg - 120);   // renova antes de vencer
            return _token is not null;
        }
        catch { return false; }
    }

    public bool Autenticado => _token is not null && DateTime.Now < _expira;

    /// <summary>
    /// Garante sessão válida antes de qualquer chamada autenticada.
    ///
    /// O caixa entra às 8h e fica logado o dia inteiro, mas o token vale ~1h. Sem isso,
    /// a primeira venda no cartão depois das 9h leva 401 — e, pior, um 401 tratado como
    /// "sem internet" jogaria o PDV em contingência sem necessidade, queimando numeração
    /// da série local e criando trabalho de sincronização à toa.
    ///
    /// Ordem: token válido → refresh_token → re-login com a credencial do terminal.
    /// </summary>
    public async Task<bool> SessaoOkAsync(CancellationToken ct = default)
    {
        if (Autenticado) return true;

        // Uma renovação por vez: emissão e TEF podem pedir ao mesmo tempo, e duas
        // trocas simultâneas invalidam o refresh_token uma da outra.
        await _renovando.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Autenticado) return true;                       // outro já renovou enquanto esperava
            if (_refresh is { Length: > 0 }
                && await AutenticarAsync("refresh_token", new { refresh_token = _refresh }, ct)) return true;

            var cred = Credenciais?.Invoke();
            if (cred is null) return false;
            return await AutenticarAsync("password",
                new { email = cred.Value.email.Trim(), password = cred.Value.senha }, ct);
        }
        finally { _renovando.Release(); }
    }

    /// <summary>Token pronto para o cabeçalho Authorization, renovando se precisar.</summary>
    public async Task<string?> TokenAsync(CancellationToken ct = default)
        => await SessaoOkAsync(ct).ConfigureAwait(false) ? _token : null;

    private HttpRequestMessage Montar(HttpMethod m, string caminho)
    {
        var req = new HttpRequestMessage(m, $"{_url}{caminho}");
        req.Headers.TryAddWithoutValidation("apikey", AnonKey);
        // Sem sessão, vai com a chave pública: o servidor decide o que ela pode ler
        // (hoje, o catálogo). Escrever continua exigindo identidade — de propósito:
        // chave embutida em EXE é extraível por qualquer um com o arquivo.
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token ?? AnonKey);
        return req;
    }

    /// <summary>
    /// Traz o catálogo pro SQLite local. Só o que a venda precisa — e com o bloco
    /// fiscal junto, porque errar NCM/CSOSN na hora da venda vira rejeição da SEFAZ.
    /// Devolve quantos produtos entraram.
    /// </summary>
    public async Task<int> BaixarProdutosAsync(SqliteConnection cx)
    {
        // Catálogo desce sem login: leitura é liberada pela chave pública do app.
        // (Se a política do servidor não existir, a resposta vem vazia — não é erro.)
        const string campos = "id,degust_code,name,grupo,unit_price,unidade,ncm,cest,csosn,origem,image_url,ativo";

        // PREÇO POR LOJA: a view `pdv_catalogo_loja` resolve preço próprio da loja
        // com queda para o preço base. Ler dela em vez de pdv_products é o que faz
        // uma loja poder divergir de preço sem duplicar o cadastro.
        //
        // Sem nome de loja (caixa ainda não pareado), volta para a tabela crua —
        // aí o preço é o base, que é o mesmo de hoje. O pior caso desta troca é
        // vender pelo preço base; nunca pelo preço de OUTRA loja.
        var loja = cx.QueryFirstOrDefault<string>("SELECT loja_nome FROM terminal LIMIT 1");
        // canal=eq.pdv é OBRIGATÓRIO: desde a migração de canais a view devolve UMA
        // LINHA POR CANAL (pdv|ifood). Sem este filtro, produto vendido também no
        // iFood volta DUPLICADO — e o caixa poderia pegar o preço do iFood (com a
        // comissão do marketplace embutida), emitindo NFC-e com valor a maior.
        var caminho = string.IsNullOrWhiteSpace(loja)
            ? $"/rest/v1/pdv_products?select={campos}&ativo=eq.true&channel_pdv=eq.true&unit_price=gt.0&order=grupo.asc,name.asc"
            : $"/rest/v1/pdv_catalogo_loja?select={campos}&store=eq.{Uri.EscapeDataString(loja)}" +
              "&canal=eq.pdv&ativo=eq.true&channel_pdv=eq.true&unit_price=gt.0&order=grupo.asc,name.asc";

        using var req = Montar(HttpMethod.Get, caminho);
        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Não consegui baixar o catálogo ({(int)resp.StatusCode}).");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        // Lista vazia NÃO desativa nada: pode ser a política de leitura ausente no
        // servidor, não um catálogo de fato vazio. Zerar a loja por causa disso
        // deixaria o caixa sem nenhum produto para vender.
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return 0;

        var agora = DateTime.Now.ToString("o");
        var n = 0;

        using var tx = cx.BeginTransaction();
        // Some da lista = inativo aqui. Não apaga: venda antiga referencia o produto,
        // e apagar quebraria o histórico.
        cx.Execute("UPDATE produto SET ativo = 0", transaction: tx);

        foreach (var p in doc.RootElement.EnumerateArray())
        {
            string? S(string nome) => p.TryGetProperty(nome, out var v) && v.ValueKind != JsonValueKind.Null
                ? (v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString()) : null;

            var precoTxt = S("unit_price") ?? "0";
            if (!decimal.TryParse(precoTxt, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var preco) || preco <= 0) continue;

            cx.Execute("""
                INSERT INTO produto (id, plu, ean, nome, categoria, preco_cent, unidade, foto_local,
                                     ncm, cest, csosn, cfop, origem, pesavel, ativo, atualizado)
                VALUES (@Id,@Plu,NULL,@Nome,@Cat,@Preco,@Un,@Foto,@Ncm,@Cest,@Csosn,NULL,@Orig,0,1,@Em)
                ON CONFLICT(id) DO UPDATE SET
                    plu=@Plu, nome=@Nome, categoria=@Cat, preco_cent=@Preco, unidade=@Un,
                    foto_local=@Foto, ncm=@Ncm, cest=@Cest, csosn=@Csosn, origem=@Orig,
                    ativo=1, atualizado=@Em
                """,
                new
                {
                    Id = S("id"), Plu = S("degust_code"), Nome = S("name") ?? "PRODUTO",
                    Cat = S("grupo") ?? "Outros", Preco = Dinheiro.DeReais(preco).Centavos,
                    Un = S("unidade") ?? "UN", Foto = S("image_url"),
                    Ncm = S("ncm"), Cest = S("cest"), Csosn = S("csosn"),
                    Orig = int.TryParse(S("origem"), out var o) ? o : 0, Em = agora,
                }, tx);
            n++;
        }
        tx.Commit();
        return n;
    }

    /// <summary>
    /// Traz os operadores do painel para o caixa. O hash da senha vem PRONTO no
    /// formato do contrato (PBKDF2 idêntico ao Operadores.cs) — aqui só se copia.
    ///
    /// Operadores criados localmente (suporte, primeira instalação) NÃO são tocados:
    /// só as linhas marcadas como vindas da nuvem são regidas pelo painel — inclusive
    /// a desativação de quem sumiu de lá (demitido some do caixa no próximo Sincronizar).
    /// </summary>
    public async Task<int> BaixarOperadoresAsync(SqliteConnection cx)
    {
        if (!await SessaoOkAsync()) return 0;
        try
        {
            using var req = Montar(HttpMethod.Post, "/rest/v1/rpc/pdv_operadores_sync");
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return 0;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return 0;

            var agora = DateTime.Now.ToString("o");
            var vieram = new List<string>();
            var n = 0;

            using var tx = cx.BeginTransaction();
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                string? S(string nome) => o.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;
                var id = S("id");
                var hash = S("pin_hash");
                var salt = S("pin_salt");
                var cpf = S("cpf");
                if (id is null || hash is null || salt is null) continue;

                // perfil do painel → perfil do caixa (gerente autoriza sangria/cancelamento)
                var perfil = S("perfil") == "gerente" ? "gerente" : "operador";
                var ativo = !o.TryGetProperty("ativo", out var a) || a.ValueKind != JsonValueKind.False;

                cx.Execute("""
                    INSERT INTO operador (id, nome, pin_hash, pin_salt, perfil, cpf, ativo, da_nuvem, atualizado)
                    VALUES (@Id,@N,@H,@S,@P,@C,@A,1,@Em)
                    ON CONFLICT(id) DO UPDATE SET nome=@N, pin_hash=@H, pin_salt=@S, perfil=@P,
                                                  cpf=@C, ativo=@A, da_nuvem=1, atualizado=@Em
                    """,
                    new { Id = id, N = S("nome") ?? "OPERADOR", H = hash, S = salt, P = perfil,
                          C = cpf, A = ativo ? 1 : 0, Em = agora }, tx);
                vieram.Add(id);
                n++;
            }

            // quem é da nuvem e não veio mais, desativa — demissão vale no caixa também
            if (vieram.Count == 0)
                cx.Execute("UPDATE operador SET ativo = 0, atualizado = @Em WHERE da_nuvem = 1 AND ativo = 1",
                    new { Em = agora }, tx);
            else
                cx.Execute($"""
                    UPDATE operador SET ativo = 0, atualizado = @Em
                     WHERE da_nuvem = 1 AND ativo = 1
                       AND id NOT IN ({string.Join(',', vieram.Select((_, i) => "@P" + i))})
                    """,
                    BuildParams(vieram, agora), tx);
            tx.Commit();
            return n;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Pedidos de delivery do dia (ifood_orders), para o KDS do quiosque. Leitura
    /// leve: so o que a tela de preparo mostra. Falha de rede devolve lista VAZIA -
    /// o KDS continua com o que ja tem no SQLite, que e o contrato do PDV inteiro.
    /// </summary>
    public async Task<List<PedidoDelivery>> BaixarPedidosDeliveryAsync(string loja, int janelaMin = 45)
    {
        try
        {
            // RPC com escopo, nao a tabela: ifood_orders tem RLS de gestor, e o
            // terminal pareado nao e gestor. A funcao devolve so a janela recente
            // da PROPRIA loja - e exige sessao valida (o anon le zero, de proposito).
            if (!await SessaoOkAsync()) return new();
            using var req = Montar(HttpMethod.Post, "/rest/v1/rpc/pdv_kds_pedidos");
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { _loja = loja, _janela_min = janelaMin }),
                Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return new();

            var r = new List<PedidoDelivery>();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var id = e.TryGetProperty("order_id", out var oid) ? oid.GetString() : null;
                if (id is null) continue;
                r.Add(new PedidoDelivery(
                    id,
                    e.TryGetProperty("display_id", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString()! : id[..Math.Min(4, id.Length)],
                    e.TryGetProperty("customer_name", out var c) && c.ValueKind == JsonValueKind.String
                        ? c.GetString() : null,
                    e.TryGetProperty("itens", out var it) && it.ValueKind is JsonValueKind.Array or JsonValueKind.Object
                        ? it.GetRawText() : "[]",
                    e.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                        ? st.GetString() ?? "" : ""));
            }
            return r;
        }
        catch { return new(); }
    }

    private static Dapper.DynamicParameters BuildParams(List<string> ids, string agora)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("Em", agora);
        for (var i = 0; i < ids.Count; i++) p.Add("P" + i, ids[i]);
        return p;
    }
}
