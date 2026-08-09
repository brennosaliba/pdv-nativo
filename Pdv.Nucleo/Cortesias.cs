using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pdv.Nucleo;

/// <summary>Um item do cupom de cortesia: um produto grátis, em certa quantidade.</summary>
public sealed record ItemCortesia(string Nome, int Quantidade);

/// <summary>
/// O que a validação da cortesia devolveu. <see cref="Ok"/> falso traz o
/// <see cref="Erro"/> cru da RPC (not_found, expired, already_redeemed,
/// cancelled) — a tela traduz. Quando ok, <see cref="Itens"/> são os produtos
/// grátis e <see cref="Loja"/> é a loja a que o cupom pertence.
/// </summary>
public sealed record Cortesia(bool Ok, string? Erro, string? Codigo, string? Loja,
    IReadOnlyList<ItemCortesia> Itens);

/// <summary>
/// Cupom de cortesia (erro de pedido) resgatado NO CAIXA. O cupom dá produtos
/// específicos de graça; aqui ele vira um DESCONTO na venda igual ao valor dos
/// itens do cupom que estão na comanda — nunca mais que isso, pra não descontar
/// um produto que o cliente não levou.
///
/// Fala com as MESMAS RPCs do validador web (courtesy_validate/courtesy_redeem),
/// pela chave anônima: validar e resgatar são operações públicas de balcão, com
/// uso único atômico e trava de loja no servidor.
///
/// Validar (mostra os itens) e resgatar (queima o cupom) são passos SEPARADOS:
/// valida na hora de aplicar na comanda, resgata só quando a venda fecha — cupom
/// não pode morrer numa venda que o cliente desistiu.
/// </summary>
public sealed class Cortesias
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly string _url;

    public Cortesias(string? urlNuvem = null) => _url = (urlNuvem ?? Nuvem.UrlPadrao).TrimEnd('/');

    /// <summary>Valida sem consumir. Rede caída devolve Ok=false com erro "sem_rede".</summary>
    public async Task<Cortesia> ValidarAsync(string codigo, CancellationToken ct = default)
    {
        var r = await ChamarAsync("courtesy_validate", new { _code = codigo }, ct).ConfigureAwait(false);
        return LerCortesia(r);
    }

    /// <summary>
    /// Queima o cupom (uso único). <paramref name="loja"/> vai para a trava de loja
    /// do servidor: cortesia de uma loja não se resgata em outra. Chamado só depois
    /// que a venda foi gravada.
    /// </summary>
    public async Task<Cortesia> ResgatarAsync(string codigo, string? operador, string? loja,
        CancellationToken ct = default)
    {
        var r = await ChamarAsync("courtesy_redeem",
            new { _code = codigo, _staff_name = operador, _store = loja }, ct).ConfigureAwait(false);
        return LerCortesia(r);
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

    private static Cortesia LerCortesia(JsonElement? raiz)
    {
        if (raiz is not { } r || r.ValueKind != JsonValueKind.Object)
            return new Cortesia(false, "sem_rede", null, null, Array.Empty<ItemCortesia>());

        var ok = r.TryGetProperty("ok", out var okv) && okv.ValueKind == JsonValueKind.True;
        var erro = r.TryGetProperty("error", out var ev) && ev.ValueKind == JsonValueKind.String ? ev.GetString() : null;
        var codigo = r.TryGetProperty("code", out var cv) && cv.ValueKind == JsonValueKind.String ? cv.GetString() : null;
        var loja = r.TryGetProperty("store", out var sv) && sv.ValueKind == JsonValueKind.String ? sv.GetString() : null;

        var itens = new List<ItemCortesia>();
        if (r.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var it in arr.EnumerateArray())
            {
                var nome = it.TryGetProperty("name", out var nv) ? nv.GetString() : null;
                var qtd = it.TryGetProperty("quantity", out var qv) && qv.TryGetInt32(out var q) ? q : 1;
                if (!string.IsNullOrWhiteSpace(nome)) itens.Add(new ItemCortesia(nome!, Math.Max(1, qtd)));
            }

        return new Cortesia(ok, erro, codigo, loja, itens);
    }
}
