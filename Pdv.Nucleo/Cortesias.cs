using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pdv.Nucleo;

/// <summary>
/// Um item do cupom de cortesia. Produto: <see cref="Nome"/> e (cupom novo) <see cref="ProdutoId"/>.
/// Categoria (SQL 212, 22/10/2026): <see cref="Categoria"/> preenchida SEM produto, e qualquer produto
/// do cardapio do caixa com esse grupo vale, ate <see cref="Quantidade"/>; o sabor e escolhido na hora.
/// <see cref="ValorCent"/> (SQL 215): o valor de referencia por unidade. Item mais barato sai de graca;
/// mais caro desconta so o valor e cobra a diferenca. Nulo = cupom de antes do 215: sai de graca.
/// </summary>
public sealed record ItemCortesia(string Nome, int Quantidade, string? Categoria = null, string? ProdutoId = null, long? ValorCent = null)
{
    public bool EhCategoria => !string.IsNullOrWhiteSpace(Categoria) && string.IsNullOrWhiteSpace(ProdutoId);
    /// <summary>"2× DONUT NINHO" ou "4× Donuts Clássicos (sabor à escolha)".</summary>
    public string Rotulo => EhCategoria ? $"{Quantidade}× {Categoria} (sabor à escolha)" : $"{Quantidade}× {Nome}";
}

/// <summary>
/// O que a validação da cortesia devolveu. <see cref="Ok"/> falso traz o
/// <see cref="Erro"/> cru da RPC (not_found, expired, already_redeemed,
/// cancelled, loja_errada) e a tela traduz. Quando ok, <see cref="Itens"/> são os
/// produtos e categorias grátis e <see cref="Loja"/> é a loja a que o cupom pertence.
/// </summary>
public sealed record Cortesia(bool Ok, string? Erro, string? Codigo, string? Loja,
    IReadOnlyList<ItemCortesia> Itens, string? Motivo = null, string? Cliente = null);

/// <summary>Uma linha da comanda como a cobertura da cortesia a enxerga.</summary>
public sealed record LinhaParaCortesia(string ProdutoId, string Nome, string? Categoria, long PrecoCent, int Unidades);

/// <summary>
/// Unidades de um produto que a cortesia cobriu (o que vai para o servidor conferir) e quanto foi
/// abatido POR UNIDADE: o preco, quando saiu de graca; o valor do cupom, quando o item era mais caro.
/// </summary>
public sealed record ItemCoberto(string ProdutoId, int Quantidade, long? DescontoUnitCent = null);

/// <summary>
/// Quanto da comanda o cupom cobre (SQL 212 e 215). Espelha a conferência do servidor
/// (pdv_cupom_cortesia_usar): cada unidade só é coberta se casa com um item do cupom.
///  · item de PRODUTO casa pelo id (cupom novo) ou pelo nome (cupom antigo), até a quantidade;
///  · item de CATEGORIA casa com qualquer produto do grupo, até a quantidade, primeiro o que
///    sobrou dos produtos e pelo MAIS BARATO;
///  · produto primeiro, categoria depois: a categoria é a mais flexível.
/// REGRA DE VALOR (SQL 215): cada unidade coberta leva o TETO da linha do cupom (ValorCent). Preço
/// até o teto sai de graça; acima, o caixa abate só o teto e cobra a diferença. Sem teto (cupom de
/// antes) sai de graça.
/// O resultado é por NOME normalizado (é assim que a comanda, o rascunho e a conta guardam a
/// cobertura desde a 1.0.32): as unidades em <see cref="Resultado.PorNome"/> e o teto por unidade em
/// <see cref="Resultado.TetoPorNome"/> (sem entrada = sem teto), mais a lista do que ficou de fora.
/// </summary>
public static class CoberturaCortesia
{
    public sealed record Resultado(Dictionary<string, int> PorNome, Dictionary<string, long> TetoPorNome, IReadOnlyList<ItemCortesia> Faltando);

    /// <summary>Prefixo das chaves que guardam o teto dentro do mesmo dicionário da cobertura (persistência).</summary>
    public const string PrefixoTeto = "$teto:";

    public static Resultado Calcular(IReadOnlyList<ItemCortesia> cupom, IReadOnlyList<LinhaParaCortesia> comanda,
        Func<string, string> normalizar)
    {
        // unidades livres por produto (soma das linhas do mesmo produto)
        var livres = new Dictionary<string, int>(StringComparer.Ordinal);
        var info = new Dictionary<string, LinhaParaCortesia>(StringComparer.Ordinal);
        var ordem = new List<string>();
        foreach (var l in comanda)
        {
            if (l.Unidades <= 0) continue;
            if (!livres.ContainsKey(l.ProdutoId)) { livres[l.ProdutoId] = 0; info[l.ProdutoId] = l; ordem.Add(l.ProdutoId); }
            livres[l.ProdutoId] += l.Unidades;
        }
        var cobertoPorProduto = new Dictionary<string, int>(StringComparer.Ordinal);
        // teto por produto: null = alguma linha sem teto cobriu (sai de graca)
        var tetoPorProduto = new Dictionary<string, long?>(StringComparer.Ordinal);
        var faltando = new List<ItemCortesia>();

        void Cobrir(string pid, int q, long? teto)
        {
            livres[pid] -= q;
            cobertoPorProduto[pid] = cobertoPorProduto.GetValueOrDefault(pid) + q;
            if (!tetoPorProduto.TryGetValue(pid, out var atual)) tetoPorProduto[pid] = teto;
            else if (atual is not null) tetoPorProduto[pid] = teto is null ? null : Math.Max(atual.Value, teto.Value);
        }

        foreach (var item in cupom.Where(i => !i.EhCategoria))
        {
            var resta = item.Quantidade;
            foreach (var pid in ordem)
            {
                if (resta <= 0) break;
                var l = info[pid];
                var casa = !string.IsNullOrWhiteSpace(item.ProdutoId)
                    ? string.Equals(item.ProdutoId, pid, StringComparison.OrdinalIgnoreCase)
                    : normalizar(item.Nome) == normalizar(l.Nome);
                if (!casa || livres[pid] <= 0) continue;
                var q = Math.Min(resta, livres[pid]);
                Cobrir(pid, q, item.ValorCent);
                resta -= q;
            }
            if (resta == item.Quantidade) faltando.Add(item);
        }

        foreach (var item in cupom.Where(i => i.EhCategoria))
        {
            var resta = item.Quantidade;
            var candidatos = ordem
                .Where(pid => info[pid].Categoria is { } c && normalizar(c) == normalizar(item.Categoria!))
                .OrderBy(pid => info[pid].PrecoCent)
                .ThenBy(pid => ordem.IndexOf(pid))
                .ToList();
            foreach (var pid in candidatos)
            {
                if (resta <= 0) break;
                if (livres[pid] <= 0) continue;
                var q = Math.Min(resta, livres[pid]);
                Cobrir(pid, q, item.ValorCent);
                resta -= q;
            }
            if (resta == item.Quantidade) faltando.Add(item);
        }

        var porNome = new Dictionary<string, int>(StringComparer.Ordinal);
        var tetoPorNome = new Dictionary<string, long>(StringComparer.Ordinal);
        var semTeto = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (pid, q) in cobertoPorProduto)
        {
            var nome = normalizar(info[pid].Nome);
            porNome[nome] = porNome.GetValueOrDefault(nome) + q;
            var teto = tetoPorProduto[pid];
            if (teto is null) semTeto.Add(nome);
            else if (!tetoPorNome.TryGetValue(nome, out var t) || t < teto.Value) tetoPorNome[nome] = teto.Value;
        }
        foreach (var n in semTeto) tetoPorNome.Remove(n);
        return new Resultado(porNome, tetoPorNome, faltando);
    }

    /// <summary>
    /// Quanto cada unidade coberta abate: o preço inteiro até o teto; só o teto acima dele.
    /// </summary>
    public static long AbatimentoPorUnidade(long precoCent, long? tetoCent)
        => tetoCent is null ? precoCent : Math.Min(precoCent, tetoCent.Value);

    /// <summary>
    /// Grava os tetos no MESMO dicionário da cobertura (chaves com <see cref="PrefixoTeto"/>): é esse
    /// dicionário que o rascunho e a conta guardam no disco, e assim o teto sobrevive ao reinício.
    /// </summary>
    public static void GravarNoDicionario(Resultado r, Dictionary<string, int> destino)
    {
        destino.Clear();
        foreach (var kv in r.PorNome) destino[kv.Key] = kv.Value;
        foreach (var kv in r.TetoPorNome) destino[PrefixoTeto + kv.Key] = (int)Math.Min(int.MaxValue, kv.Value);
    }

    /// <summary>O teto por unidade de um nome, lido do dicionário da cobertura; nulo = sai de graça.</summary>
    public static long? TetoDe(IReadOnlyDictionary<string, int> cobertura, string nomeNormalizado)
        => cobertura.TryGetValue(PrefixoTeto + nomeNormalizado, out var t) ? t : null;

    /// <summary>Só as entradas de unidades (sem as chaves de teto).</summary>
    public static bool EhChaveDeTeto(string chave) => chave.StartsWith(PrefixoTeto, StringComparison.Ordinal);
}

/// <summary>
/// Cupom de cortesia (erro de pedido) resgatado NO CAIXA. O cupom dá produtos
/// específicos ou uma categoria de graça; as unidades cobertas saem da venda (e da
/// nota) e o resto é cobrado normalmente.
///
/// Duas portas no servidor:
///  · SQL 212 (22/10/2026): pdv_cupom_cortesia_conferir / pdv_cupom_cortesia_usar, com a
///    SESSÃO do terminal (a loja vem do terminal). O "usar" manda o que foi coberto e o
///    abatimento por unidade; o servidor confere contra o cupom (SQL 215: o valor de cada
///    linha) e queima com trava: dois caixas, só um leva. A mesma chave de uso no reenvio
///    devolve ok sem queimar de novo.
///  · as antigas courtesy_validate/courtesy_redeem pela chave anônima: o caixa cai nelas
///    quando o servidor ainda não tem o SQL 212 (404) ou o terminal está sem sessão.
///
/// Validar (mostra os itens) e usar (queima o cupom) são passos SEPARADOS:
/// valida na hora de aplicar na comanda, usa só quando a venda fecha. Cupom
/// não pode morrer numa venda que o cliente desistiu.
/// </summary>
public sealed class Cortesias
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly TimeSpan Prazo = TimeSpan.FromSeconds(12);
    private readonly string _url;
    private readonly Nuvem? _nuvem;

    public Cortesias(string? urlNuvem = null) => _url = (urlNuvem ?? Nuvem.UrlPadrao).TrimEnd('/');

    /// <summary>Com a nuvem do terminal: usa as portas do SQL 212 pela sessão dele.</summary>
    public Cortesias(Nuvem nuvem, string? urlNuvem = null) : this(urlNuvem) => _nuvem = nuvem;

    /// <summary>Valida sem consumir. Rede caída devolve Ok=false com erro "sem_rede".</summary>
    public async Task<Cortesia> ValidarAsync(string codigo, CancellationToken ct = default)
    {
        if (_nuvem is not null)
        {
            var (st, corpo) = await _nuvem.RpcAsync("pdv_cupom_cortesia_conferir",
                JsonSerializer.Serialize(new { _code = codigo }), Prazo, ct).ConfigureAwait(false);
            if (st is >= 200 and < 300) return LerCortesia(Parse(corpo));
            if (st == 0) return SemRede(codigo);
            // 404 = servidor sem o SQL 212; -1 = sem sessão: cai na porta antiga
        }
        var r = await ChamarAsync("courtesy_validate", new { _code = codigo }, ct).ConfigureAwait(false);
        return LerCortesia(r);
    }

    /// <summary>
    /// Queima o cupom dizendo o que foi coberto (<paramref name="cobertura"/>). <paramref name="chave"/>
    /// identifica este uso: o reenvio com a mesma chave devolve ok. <paramref name="venda"/> é a
    /// client_key da venda (nula quando a comanda toda foi cortesia). Sem o SQL 212 no servidor,
    /// cai no courtesy_redeem antigo.
    /// </summary>
    public async Task<Cortesia> UsarAsync(string codigo, IReadOnlyList<ItemCoberto> cobertura, string chave,
        string? operador, string? loja, string? venda, CancellationToken ct = default)
    {
        if (_nuvem is not null)
        {
            var (st, corpo) = await _nuvem.RpcAsync("pdv_cupom_cortesia_usar", CorpoDoUso(codigo, cobertura, chave, operador, venda),
                Prazo, ct).ConfigureAwait(false);
            if (st is >= 200 and < 300) return LerCortesia(Parse(corpo));
            if (st == 0) return SemRede(codigo);
        }
        return await ResgatarAsync(codigo, operador, loja, ct).ConfigureAwait(false);
    }

    /// <summary>O corpo de pdv_cupom_cortesia_usar (também o que a fila reenvia).</summary>
    public static string CorpoDoUso(string codigo, IReadOnlyList<ItemCoberto> cobertura, string chave, string? operador, string? venda)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["_code"] = codigo,
            ["_cobertura"] = cobertura.Select(c =>
            {
                var d = new Dictionary<string, object> { ["product_id"] = c.ProdutoId, ["quantidade"] = c.Quantidade };
                if (c.DescontoUnitCent is { } desc) d["desconto_unit_cent"] = desc;
                return d;
            }).ToArray(),
            ["_ref"] = chave,
            ["_operador"] = operador,
            ["_venda"] = venda,
        });

    /// <summary>
    /// Porta antiga: queima o cupom (uso único). <paramref name="loja"/> vai para a trava de loja
    /// do servidor: cortesia de uma loja não se resgata em outra.
    /// </summary>
    public async Task<Cortesia> ResgatarAsync(string codigo, string? operador, string? loja,
        CancellationToken ct = default)
    {
        var r = await ChamarAsync("courtesy_redeem",
            new { _code = codigo, _staff_name = operador, _store = loja }, ct).ConfigureAwait(false);
        return LerCortesia(r);
    }

    private static Cortesia SemRede(string codigo) => new(false, "sem_rede", codigo, null, Array.Empty<ItemCortesia>());

    private static JsonElement? Parse(string corpo)
    {
        try { return JsonDocument.Parse(corpo).RootElement.Clone(); }
        catch { return null; }
    }

    private async Task<JsonElement?> ChamarAsync(string rpc, object corpo, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_url}/rest/v1/rpc/{rpc}");
            req.Headers.TryAddWithoutValidation("apikey", Nuvem.AnonKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Nuvem.AnonKey);
            req.Content = new StringContent(JsonSerializer.Serialize(corpo), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var texto = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(texto).RootElement.Clone();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    internal static Cortesia LerCortesia(JsonElement? raiz)
    {
        if (raiz is not { } r || r.ValueKind != JsonValueKind.Object)
            return new Cortesia(false, "sem_rede", null, null, Array.Empty<ItemCortesia>());

        string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        long? L(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
        var ok = r.TryGetProperty("ok", out var okv) && okv.ValueKind == JsonValueKind.True;

        var itens = new List<ItemCortesia>();
        if (r.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var it in arr.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;
                var nome = S(it, "name");
                var categoria = S(it, "categoria");
                var pid = S(it, "product_id");
                var qtd = it.TryGetProperty("quantity", out var qv) && qv.TryGetInt32(out var q) ? q : 1;
                var valor = L(it, "valor_cent");
                if (!string.IsNullOrWhiteSpace(nome) || !string.IsNullOrWhiteSpace(pid))
                    itens.Add(new ItemCortesia(nome ?? "", Math.Max(1, qtd), categoria, pid, valor));
                else if (!string.IsNullOrWhiteSpace(categoria))
                    itens.Add(new ItemCortesia(categoria!, Math.Max(1, qtd), categoria, null, valor));
            }

        return new Cortesia(ok, S(r, "error"), S(r, "code"), S(r, "store"), itens, S(r, "reason"), S(r, "customer_name"));
    }
}
