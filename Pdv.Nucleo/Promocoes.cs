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

    public sealed record ProdutoPromo(bool AtivaAgora, string Nome, string Quando);

    /// <summary>
    /// Os produtos que aparecem na categoria PROMOÇÃO do menu: tudo que alguma
    /// promoção DENTRO DA VIGÊNCIA menciona por id. AtivaAgora diz se o dia e
    /// a hora batem NESTE momento; Quando descreve a regra ("qui · 18:00–20:00")
    /// para o card cinza explicar por que não vende agora.
    /// (Alvo por categoria/todos fica de fora da listagem: enumeraria o cardápio
    /// inteiro e a vitrine viraria ruído.)
    /// </summary>
    public static Dictionary<string, ProdutoPromo> ProdutosEmPromocao(
        IEnumerable<Promo> promos, DateTime agora)
    {
        var r = new Dictionary<string, ProdutoPromo>();
        var hoje = DateOnly.FromDateTime(agora);
        var hora = TimeOnly.FromDateTime(agora);
        var iso = DiaIso(agora);

        foreach (var p in promos)
        {
            if (p.Inicio is not null && hoje < p.Inicio) continue;
            if (p.Fim is not null && hoje > p.Fim) continue;

            var horaOk = DentroDaJanela(p.Janelas, hora);
            var ids = new HashSet<string>(p.ProdutoIds);
            foreach (var reg in p.Regras)
            {
                foreach (var id in reg.ProdutoIds) ids.Add(id);
                foreach (var id in reg.PrecosCent.Keys) ids.Add(id);
            }

            foreach (var id in ids)
            {
                bool diaOk = p.Regras.Count > 0
                    ? p.Regras.Any(reg =>
                        (reg.DiasIso.Length == 0 || reg.DiasIso.Contains(iso))
                        && (reg.ProdutoIds.Count == 0 || reg.ProdutoIds.Contains(id)
                            || reg.PrecosCent.ContainsKey(id)))
                    : p.DiasSemana is not { Length: > 0 } || p.DiasSemana.Contains(iso);

                var ativa = diaOk && horaOk;
                if (!r.TryGetValue(id, out var atual) || (ativa && !atual.AtivaAgora))
                    r[id] = new ProdutoPromo(ativa, p.Nome, DescreveQuando(p, id));
            }
        }
        return r;
    }

    /// <summary>"qui · 18:00–20:00" — os dias/horários em que a promoção vale.</summary>
    public static string DescreveQuando(Promo p, string produtoId)
    {
        string[] nomes = { "", "seg", "ter", "qua", "qui", "sex", "sáb", "dom" };
        var dias = new SortedSet<int>();
        if (p.Regras.Count > 0)
            foreach (var reg in p.Regras.Where(reg =>
                reg.ProdutoIds.Count == 0 || reg.ProdutoIds.Contains(produtoId)
                || reg.PrecosCent.ContainsKey(produtoId)))
                foreach (var d in reg.DiasIso) dias.Add(d);
        else if (p.DiasSemana is not null)
            foreach (var d in p.DiasSemana) dias.Add(d);

        var partes = new List<string>();
        if (dias.Count > 0)
            partes.Add(string.Join(" ", dias.Where(d => d is >= 1 and <= 7).Select(d => nomes[d])));
        if (p.Janelas.Count > 0)
            partes.Add(string.Join(" e ", p.Janelas.Select(j => $"{j.Das:HH:mm}–{j.Ate:HH:mm}")));
        return partes.Count == 0 ? "todos os dias" : string.Join(" · ", partes);
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
