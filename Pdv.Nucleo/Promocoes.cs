using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>
/// Motor de promoções do PDV — o formato canônico é o do painel (pdv-painel
/// sql/005 + 047), e a REGRA DE OURO vem de lá:
///
///  · regras_semana[].dias usa ISO 8601: 1=segunda … 7=domingo;
///  · regras_semana, quando presente, MANDA (ignora dias_semana de cima);
///  · precos_cent{produto: cent} é preço FINAL fechado — vence qualquer %;
///  · janelas de horário em config.janelas [{das,ate}]; sem janela = dia todo;
///  · vigência inicio/fim em datas; nulo = sem limite.
///
/// AQUI SÓ PREÇO UNITÁRIO (preço do dia, %, valor fixo). Os tipos de carrinho
/// (leve_x_pague_y, combo, compre_ganhe) mexem em desconto por item na venda
/// e na NFC-e — entram numa etapa própria, com o cuidado fiscal que merecem.
///
/// O dia/hora são avaliados AQUI, no relógio do caixa: a promoção de quinta
/// tem que ligar à meia-noite sem depender de sync com a nuvem.
/// </summary>
public static class Promocoes
{
    public sealed record Janela(TimeOnly Das, TimeOnly Ate);

    public sealed record Regra(
        int[] DiasIso,
        HashSet<string> ProdutoIds,
        Dictionary<string, long> PrecosCent,
        decimal? Percentual);

    public sealed record Promo(
        string Id, string Nome, string Tipo, string? Alvo,
        decimal? Percentual, long? ValorDescCent,
        HashSet<string> ProdutoIds, HashSet<string> Categorias,
        int[]? DiasSemana, List<Janela> Janelas, List<Regra> Regras,
        DateOnly? Inicio, DateOnly? Fim);

    // ── carga ───────────────────────────────────────────────────────────────
    public static List<Promo> Carregar(SqliteConnection cx)
    {
        var r = new List<Promo>();
        foreach (var payload in cx.Query<string>("SELECT payload FROM promo"))
        {
            var p = Parsear(payload);
            if (p is not null) r.Add(p);
        }
        return r;
    }

    /// <summary>Tolerante como o parser de itens do KDS: promoção com JSON
    /// estranho é IGNORADA (preço base vale) — nunca derruba a venda.</summary>
    public static Promo? Parsear(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var e = doc.RootElement;

            var janelas = new List<Janela>();
            if (e.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object
                && cfg.TryGetProperty("janelas", out var js) && js.ValueKind == JsonValueKind.Array)
                foreach (var j in js.EnumerateArray())
                {
                    var das = Hora(j, "das"); var ate = Hora(j, "ate");
                    if (das is not null && ate is not null) janelas.Add(new Janela(das.Value, ate.Value));
                }
            // compat: sem config.janelas, hora_inicio/fim são a única janela
            if (janelas.Count == 0)
            {
                var das = Hora(e, "hora_inicio"); var ate = Hora(e, "hora_fim");
                if (das is not null && ate is not null) janelas.Add(new Janela(das.Value, ate.Value));
            }

            var regras = new List<Regra>();
            if (e.TryGetProperty("regras_semana", out var rs) && rs.ValueKind == JsonValueKind.Array)
                foreach (var reg in rs.EnumerateArray())
                {
                    var precos = new Dictionary<string, long>();
                    if (reg.TryGetProperty("precos_cent", out var pc) && pc.ValueKind == JsonValueKind.Object)
                        foreach (var kv in pc.EnumerateObject())
                            if (kv.Value.TryGetInt64(out var cent)) precos[kv.Name] = cent;
                    regras.Add(new Regra(
                        Ints(reg, "dias"),
                        Strs(reg, "produto_ids"),
                        precos,
                        Num(reg, "percentual")));
                }

            return new Promo(
                Str(e, "id") ?? Guid.NewGuid().ToString(),
                Str(e, "nome") ?? "(sem nome)",
                Str(e, "tipo") ?? "",
                Str(e, "alvo"),
                Num(e, "percentual"),
                e.TryGetProperty("valor_desconto_cent", out var v) && v.TryGetInt64(out var vc) ? vc : null,
                Strs(e, "produto_ids"),
                Strs(e, "categorias"),
                e.TryGetProperty("dias_semana", out var ds) && ds.ValueKind == JsonValueKind.Array
                    ? Ints(e, "dias_semana") : null,
                janelas, regras,
                Data(e, "inicio"), Data(e, "fim"));
        }
        catch { return null; }
    }

    // ── o motor ─────────────────────────────────────────────────────────────
    /// <summary>ISO 8601: segunda=1 … domingo=7 (DayOfWeek tem domingo=0).</summary>
    public static int DiaIso(DateTime agora)
        => agora.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)agora.DayOfWeek;

    /// <summary>
    /// Preço efetivo do produto AGORA. Entre promoções concorrentes vale a
    /// MELHOR PRO CLIENTE (menor preço) — é o que o balcão consegue defender.
    /// Devolve o preço base intacto quando nada se aplica.
    /// </summary>
    public static (long Cent, string? Promo) PrecoEfetivoCent(
        IEnumerable<Promo> promos, string produtoId, string categoria, long baseCent, DateTime agora)
    {
        var melhor = baseCent;
        string? nome = null;
        var iso = DiaIso(agora);
        var hora = TimeOnly.FromDateTime(agora);
        var hoje = DateOnly.FromDateTime(agora);

        foreach (var p in promos)
        {
            if (p.Inicio is not null && hoje < p.Inicio) continue;
            if (p.Fim is not null && hoje > p.Fim) continue;
            if (!DentroDaJanela(p.Janelas, hora)) continue;

            long? cand = null;

            if (p.Regras.Count > 0)
            {
                // regras_semana MANDA: dias_semana de cima é ignorado (005)
                foreach (var r in p.Regras)
                {
                    if (r.DiasIso.Length > 0 && !r.DiasIso.Contains(iso)) continue;
                    if (r.ProdutoIds.Count > 0 && !r.ProdutoIds.Contains(produtoId)) continue;
                    if (r.PrecosCent.TryGetValue(produtoId, out var fixo))
                        cand = Math.Min(cand ?? long.MaxValue, fixo);   // preço fechado VENCE
                    else if ((r.Percentual ?? p.Percentual) is decimal rp && rp > 0)
                        cand = Math.Min(cand ?? long.MaxValue, PorPercentual(baseCent, rp));
                }
            }
            else
            {
                if (p.DiasSemana is { Length: > 0 } && !p.DiasSemana.Contains(iso)) continue;
                if (!AlvoBate(p, produtoId, categoria)) continue;

                cand = p.Tipo switch
                {
                    "percentual" when p.Percentual is > 0 => PorPercentual(baseCent, p.Percentual.Value),
                    "valor" or "valor_fixo" when p.ValorDescCent is > 0
                        => Math.Max(0, baseCent - p.ValorDescCent.Value),
                    _ => null,   // leve_x_pague_y / combo / compre_ganhe: carrinho, não preço
                };
            }

            if (cand is not null && cand < melhor) { melhor = cand.Value; nome = p.Nome; }
        }
        return (melhor, nome);
    }

    private static bool AlvoBate(Promo p, string produtoId, string categoria) => p.Alvo switch
    {
        "produtos" => p.ProdutoIds.Contains(produtoId),
        "categorias" => p.Categorias.Contains(categoria)
                     || p.Categorias.Any(c => string.Equals(c, categoria, StringComparison.OrdinalIgnoreCase)),
        _ => true,   // 'todos' (ou alvo ausente)
    };

    private static bool DentroDaJanela(List<Janela> janelas, TimeOnly hora)
        => janelas.Count == 0 || janelas.Any(j => j.Das <= j.Ate
            ? hora >= j.Das && hora < j.Ate
            : hora >= j.Das || hora < j.Ate);     // janela que cruza a meia-noite

    private static long PorPercentual(long baseCent, decimal pct)
        => Math.Max(0, (long)Math.Round(baseCent * (100m - pct) / 100m, MidpointRounding.AwayFromZero));

    // ── leitura tolerante ───────────────────────────────────────────────────
    private static string? Str(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static decimal? Num(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
    private static DateOnly? Data(JsonElement e, string k)
        => Str(e, k) is { Length: >= 10 } s && DateOnly.TryParse(s[..10], out var d) ? d : null;
    private static TimeOnly? Hora(JsonElement e, string k)
        => Str(e, k) is { Length: >= 5 } s && TimeOnly.TryParse(s[..5], out var t) ? t : null;
    private static int[] Ints(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.TryGetInt32(out _)).Select(x => x.GetInt32()).ToArray()
            : Array.Empty<int>();
    private static HashSet<string> Strs(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
               .Select(x => x.GetString()!).ToHashSet()
            : new HashSet<string>();
}
