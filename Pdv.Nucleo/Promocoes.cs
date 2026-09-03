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
/// Preço unitário (preço do dia, %, valor fixo) em PrecoEfetivoCent, e as
/// promoções de CARRINHO (leve_x_pague_y, combo, compre_ganhe) em
/// AvaliarCarrinho, que também aplica a regra "uma promoção por pedido".
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
        DateOnly? Inicio, DateOnly? Fim,
        int? Leve = null, int? Pague = null, ComboDef? Combo = null,
        HashSet<string>? Ganha = null, GanhaRegra GanhaRegra = GanhaRegra.Lista,
        long? TetoCent = null, int LimitePorVenda = 0, string? Aviso = null);

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
                            if (kv.Value.ValueKind == JsonValueKind.Number
                                && kv.Value.TryGetInt64(out var cent)) precos[kv.Name] = cent;
                    regras.Add(new Regra(
                        Ints(reg, "dias"),
                        Strs(reg, "produto_ids"),
                        precos,
                        Num(reg, "percentual")));
                }

            // ── promoções de carrinho ────────────────────────────────────
            var leve = Int(e, "leve"); var pague = Int(e, "pague");
            ComboDef? combo = null;
            var temCfg = e.TryGetProperty("config", out var cfgC) && cfgC.ValueKind == JsonValueKind.Object;
            if (e.TryGetProperty("combo", out var cb) && cb.ValueKind == JsonValueKind.Object)
            {
                var itensCombo = new List<ComboItem>();
                if (cb.TryGetProperty("itens", out var its) && its.ValueKind == JsonValueKind.Array)
                    foreach (var it in its.EnumerateArray())
                    {
                        var pid = Str(it, "produto_id"); var q = Int(it, "qtd") ?? 1;
                        if (pid is not null && q > 0) itensCombo.Add(new ComboItem(pid, q));
                    }
                var precoCombo = Long(cb, "preco_cent") ?? 0;
                var modo = "preco"; decimal? pct = null;
                if (temCfg && cfgC.TryGetProperty("combo", out var cc) && cc.ValueKind == JsonValueKind.Object)
                {
                    modo = Str(cc, "modo") ?? "preco";
                    pct = Num(cc, "desconto_pct");
                    if (Long(cc, "preco_cent") is long pc2) precoCombo = pc2;
                }
                if (itensCombo.Count > 0) combo = new ComboDef(itensCombo, precoCombo, modo, pct);
            }
            HashSet<string>? ganha = null; var regraGanha = GanhaRegra.Lista;
            long? teto = null; var limite = 0; string? aviso = null;
            if (temCfg)
            {
                var g = Strs(cfgC, "ganha");
                if (g.Count > 0) ganha = g;
                var gr = Str(cfgC, "ganha_regra");
                regraGanha = gr switch
                {
                    null or "" or "lista" => GanhaRegra.Lista,
                    "mesmo_produto" => GanhaRegra.MesmoProduto,
                    "mesmo_valor" => GanhaRegra.MesmoValor,
                    "qualquer_mais_barato" => GanhaRegra.QualquerMaisBarato,
                    "qualquer_item" => GanhaRegra.QualquerItem,
                    _ => GanhaRegra.Desconhecida,
                };
                if (regraGanha == GanhaRegra.Desconhecida) aviso = "Regra do brinde desconhecida: " + gr;
                teto = Long(cfgC, "teto_cent");
                limite = Math.Max(0, Int(cfgC, "limite_por_venda") ?? 0);
            }
            return new Promo(
                Str(e, "id") ?? Guid.NewGuid().ToString(),
                Str(e, "nome") ?? "(sem nome)",
                Str(e, "tipo") ?? "",
                Str(e, "alvo"),
                Num(e, "percentual"),
                // ValueKind PRIMEIRO: TryGetInt64 em Null LANÇA (não devolve false).
                // Os payloads reais têm "valor_desconto_cent": null — a 1ª versão
                // descartava as TRÊS promoções da loja por causa disto, e a aba
                // PROMOÇÃO simplesmente não nascia.
                e.TryGetProperty("valor_desconto_cent", out var v)
                    && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var vc) ? vc : null,
                Strs(e, "produto_ids"),
                Strs(e, "categorias"),
                // dias_semana de CIMA vem do painel em JS (0=domingo … 6=sábado);
                // aqui tudo é ISO (1=segunda … 7=domingo). Só o domingo difere:
                // sem esta troca, promoção de domingo nunca ligava.
                e.TryGetProperty("dias_semana", out var ds) && ds.ValueKind == JsonValueKind.Array
                    ? Ints(e, "dias_semana").Select(dd => dd == 0 ? 7 : dd).ToArray() : null,
                janelas, regras,
                Data(e, "inicio"), Data(e, "fim"),
                leve, pague, combo, ganha, regraGanha, teto, limite, aviso);
        }
        catch { return null; }
    }

    // ── o motor ─────────────────────────────────────────────────────────────
    /// <summary>ISO 8601: segunda=1 … domingo=7 (DayOfWeek tem domingo=0).</summary>
    public static int DiaIso(DateTime agora)
        => agora.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)agora.DayOfWeek;

    /// <summary>
    /// Preço efetivo do produto AGORA. Entre promoções concorrentes vale a
    /// MELHOR PRO CLIENTE (menor preço). Devolve o preço base intacto quando
    /// nada se aplica. É o preço do CARD; a comanda usa AvaliarCarrinho.
    /// </summary>
    public static (long Cent, string? Promo) PrecoEfetivoCent(
        IEnumerable<Promo> promos, string produtoId, string categoria, long baseCent, DateTime agora)
    {
        var melhor = baseCent;
        string? nome = null;
        foreach (var p in promos)
        {
            var cand = PrecoComPromo(p, produtoId, categoria, baseCent, agora);
            if (cand is not null && cand < melhor) { melhor = cand.Value; nome = p.Nome; }
        }
        return (melhor, nome);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PROMOÇÕES DE CARRINHO (03/09/2026): leve X pague Y, combo, compre e ganhe.
    //
    //  Até aqui o caixa só aplicava promoção de PREÇO. O painel deixava o dono
    //  criar as três de carrinho, e o caixa cobrava preço cheio em silêncio.
    //  Regras que o painel promete e agora valem aqui:
    //   · UMA promoção por pedido: entre todas as vigentes (as de preço também)
    //     vale a de MAIOR desconto total; nunca duas juntas.
    //   · leve X pague Y: o grátis é sempre a unidade mais barata do grupo.
    //   · compre e ganhe: quem decide o brinde é o caixa, pela regra do painel
    //     (config.ganha_regra); o operador não escolhe.
    //  Preços aqui são sempre de TABELA; o desconto sai por linha, em centavos.
    // ═══════════════════════════════════════════════════════════════════════
    public enum GanhaRegra { Lista, MesmoProduto, MesmoValor, QualquerMaisBarato, QualquerItem, Desconhecida }
    public sealed record ComboItem(string ProdutoId, int Qtd);
    public sealed record ComboDef(List<ComboItem> Itens, long PrecoCent, string Modo, decimal? DescontoPct);

    /// <summary>Uma linha da comanda como o motor a vê (quantidade já sem cortesia).</summary>
    public sealed record ItemCarrinho(string ProdutoId, string Categoria, long PrecoCent, long QtdMilesimos);
    public sealed record Candidata(string PromoId, string Nome, string Tipo, long DescontoCent);
    public sealed record Avaliacao(
        string? PromoId, string? PromoNome, string? PromoTipo,
        long[] DescontoCent, int[] UnidadesGratis,
        IReadOnlyList<Candidata> Perdedoras, string? Dica)
    {
        public long TotalCent => DescontoCent.Sum();
    }

    /// <summary>Vigência de calendário e horário (dias ficam por tipo: ver DiaBate).</summary>
    public static bool Vigente(Promo p, DateTime agora)
    {
        var hoje = DateOnly.FromDateTime(agora);
        if (p.Inicio is not null && hoje < p.Inicio) return false;
        if (p.Fim is not null && hoje > p.Fim) return false;
        return DentroDaJanela(p.Janelas, TimeOnly.FromDateTime(agora));
    }

    private static bool DiaBate(Promo p, DateTime agora)
        => p.DiasSemana is not { Length: > 0 } || p.DiasSemana.Contains(DiaIso(agora));

    /// <summary>
    /// Preço de UMA promoção de preço para um produto, ou null se ela não
    /// alcança o produto agora (é o miolo de PrecoEfetivoCent).
    /// </summary>
    public static long? PrecoComPromo(Promo p, string produtoId, string categoria, long baseCent, DateTime agora)
    {
        if (!Vigente(p, agora)) return null;
        var iso = DiaIso(agora);
        long? cand = null;
        if (p.Regras.Count > 0)
        {
            foreach (var r in p.Regras)
            {
                if (r.DiasIso.Length > 0 && !r.DiasIso.Contains(iso)) continue;
                if (r.ProdutoIds.Count > 0 && !r.ProdutoIds.Contains(produtoId)) continue;
                if (r.PrecosCent.TryGetValue(produtoId, out var fixo))
                    cand = Math.Min(cand ?? long.MaxValue, fixo);
                else if ((r.Percentual ?? p.Percentual) is decimal rp && rp > 0)
                    cand = Math.Min(cand ?? long.MaxValue, PorPercentual(baseCent, rp));
            }
            return cand;
        }
        if (!DiaBate(p, agora)) return null;
        if (!AlvoBate(p, produtoId, categoria)) return null;
        return p.Tipo switch
        {
            "percentual" when p.Percentual is > 0 => PorPercentual(baseCent, p.Percentual.Value),
            "valor" or "valor_fixo" when p.ValorDescCent is > 0 => Math.Max(0, baseCent - p.ValorDescCent.Value),
            _ => null,
        };
    }

    /// <summary>
    /// Avalia o carrinho inteiro: para cada promoção vigente calcula o desconto
    /// por linha; vence a de maior desconto total (empate: menor Id). Desconto
    /// zero em todas = nenhuma. As outras com desconto viram Perdedoras, para a
    /// comanda explicar ao operador o que não valeu junto.
    /// </summary>
    public static Avaliacao AvaliarCarrinho(IEnumerable<Promo> promos, IReadOnlyList<ItemCarrinho> itens, DateTime agora)
    {
        var n = itens.Count;
        var vazio = new Avaliacao(null, null, null, new long[n], new int[n], Array.Empty<Candidata>(), null);
        if (n == 0) return vazio;
        Promo? vencedora = null;
        long[]? melhorD = null; int[]? melhorG = null; long melhorTotal = 0; string? melhorDica = null;
        var candidatas = new List<(Promo p, long total)>();
        foreach (var p in promos)
        {
            if (!Vigente(p, agora)) continue;
            var d = new long[n]; var g = new int[n]; string? dica = null;
            switch (p.Tipo)
            {
                case "leve_x_pague_y":
                    if (!DiaBate(p, agora)) continue;
                    LeveXPagueY(p, itens, d, g);
                    break;
                case "combo":
                    if (!DiaBate(p, agora)) continue;
                    Combo(p, itens, d);
                    break;
                case "compre_ganhe":
                    if (!DiaBate(p, agora)) continue;
                    if (p.GanhaRegra == GanhaRegra.Desconhecida) continue;
                    dica = Parear(p, itens, d, g);
                    break;
                default:
                    for (var i = 0; i < n; i++)
                    {
                        var it = itens[i];
                        var pc = PrecoComPromo(p, it.ProdutoId, it.Categoria, it.PrecoCent, agora);
                        if (pc is long novo && novo < it.PrecoCent)
                            d[i] = new Dinheiro(it.PrecoCent - novo).VezesQtd(it.QtdMilesimos).Centavos;
                    }
                    break;
            }
            for (var i = 0; i < n; i++)
            {
                var bruto = new Dinheiro(itens[i].PrecoCent).VezesQtd(itens[i].QtdMilesimos).Centavos;
                d[i] = Math.Clamp(d[i], 0, bruto);
            }
            var total = d.Sum();
            if (total <= 0)
            {
                if (dica is not null && melhorDica is null) melhorDica = dica;
                continue;
            }
            candidatas.Add((p, total));
            var ganha = vencedora is null || total > melhorTotal
                || (total == melhorTotal && string.CompareOrdinal(p.Id, vencedora.Id) < 0);
            if (ganha) { vencedora = p; melhorD = d; melhorG = g; melhorTotal = total; }
        }
        if (vencedora is null) return vazio with { Dica = melhorDica };
        var venc = vencedora;
        var perdedoras = candidatas
            .Where(c => c.p.Id != venc.Id)
            .OrderByDescending(c => c.total)
            .Select(c => new Candidata(c.p.Id, c.p.Nome, c.p.Tipo, c.total))
            .ToList();
        return new Avaliacao(venc.Id, venc.Nome, venc.Tipo, melhorD!, melhorG!, perdedoras, null);
    }

    private sealed record Unidade(int Linha, string ProdutoId, string Categoria, long PrecoCent);

    private static List<Unidade> Unidades(IReadOnlyList<ItemCarrinho> itens)
    {
        var r = new List<Unidade>();
        for (var i = 0; i < itens.Count; i++)
        {
            var inteiras = itens[i].QtdMilesimos / 1000;   // fração nunca é compra nem brinde
            for (var k = 0; k < inteiras; k++) r.Add(new Unidade(i, itens[i].ProdutoId, itens[i].Categoria, itens[i].PrecoCent));
        }
        return r;
    }

    private static void LeveXPagueY(Promo p, IReadOnlyList<ItemCarrinho> itens, long[] d, int[] g)
    {
        if (p.Leve is not int leve || p.Pague is not int pague || leve <= pague || pague < 0) return;
        var grupo = Unidades(itens).Where(u => AlvoBate(p, u.ProdutoId, u.Categoria)).ToList();
        var gratis = (grupo.Count / leve) * (leve - pague);
        if (gratis <= 0) return;
        foreach (var u in grupo.OrderBy(u => u.PrecoCent).ThenBy(u => u.Linha).Take(gratis))
        {
            d[u.Linha] += u.PrecoCent;
            g[u.Linha]++;
        }
    }

    private static void Combo(Promo p, IReadOnlyList<ItemCarrinho> itens, long[] d)
    {
        if (p.Combo is not { Itens.Count: > 0 } combo) return;
        // quantas vezes o combo cabe na comanda (unidades inteiras por produto)
        var k = int.MaxValue;
        long soma = 0;
        var linhas = new List<(int linha, long valor)>();
        foreach (var ci in combo.Itens)
        {
            var idx = -1;
            for (var i = 0; i < itens.Count; i++) if (itens[i].ProdutoId == ci.ProdutoId) { idx = i; break; }
            if (idx < 0) return;
            var inteiras = (int)(itens[idx].QtdMilesimos / 1000);
            k = Math.Min(k, inteiras / Math.Max(1, ci.Qtd));
            soma += itens[idx].PrecoCent * ci.Qtd;
            linhas.Add((idx, itens[idx].PrecoCent * ci.Qtd));
        }
        if (k <= 0 || k == int.MaxValue || soma <= 0) return;
        var alvo = combo.Modo == "desconto" && combo.DescontoPct is decimal pct && pct > 0
            ? soma - (long)Math.Round(soma * pct / 100m, MidpointRounding.AwayFromZero)
            : combo.PrecoCent;
        var desconto = k * Math.Max(0, soma - alvo);
        if (desconto <= 0) return;
        // rateio proporcional ao valor de cada linha no combo; a sobra vai para a maior
        long distribuido = 0; var maior = 0;
        for (var j = 0; j < linhas.Count; j++)
        {
            var parte = desconto * linhas[j].valor / soma;   // floor
            d[linhas[j].linha] += parte;
            distribuido += parte;
            if (linhas[j].valor > linhas[maior].valor) maior = j;
        }
        d[linhas[maior].linha] += desconto - distribuido;
    }

    /// <summary>
    /// Compre e ganhe. Compras = unidades do alvo; brindes = unidades que a
    /// regra admite. Pareia do brinde mais caro para o mais barato com a compra
    /// livre mais barata que a regra aceita. Devolve a dica quando há compra
    /// mas nenhum brinde possível (a comanda explica o que falta).
    /// </summary>
    private static string? Parear(Promo p, IReadOnlyList<ItemCarrinho> itens, long[] d, int[] g)
    {
        var unidades = Unidades(itens);
        var compras = unidades.Where(u => AlvoBate(p, u.ProdutoId, u.Categoria)).ToList();
        if (compras.Count == 0) return null;
        var brindes = unidades.Where(u =>
            (p.GanhaRegra == GanhaRegra.MesmoProduto || p.Ganha is null || p.Ganha.Contains(u.ProdutoId))
            && (p.TetoCent is null || u.PrecoCent <= p.TetoCent)).ToList();
        var usada = new HashSet<Unidade>(ReferenceEqualityComparer.Instance);
        var pares = 0;
        foreach (var b in brindes.OrderByDescending(u => u.PrecoCent).ThenBy(u => u.Linha))
        {
            if (p.LimitePorVenda > 0 && pares >= p.LimitePorVenda) break;
            if (usada.Contains(b)) continue;
            Unidade? compra = null;
            foreach (var c in compras.OrderBy(u => u.PrecoCent).ThenBy(u => u.Linha))
            {
                if (ReferenceEquals(c, b) || usada.Contains(c)) continue;
                var ok = p.GanhaRegra switch
                {
                    GanhaRegra.Lista => true,
                    GanhaRegra.MesmoProduto => c.ProdutoId == b.ProdutoId,
                    GanhaRegra.MesmoValor => c.PrecoCent == b.PrecoCent,
                    GanhaRegra.QualquerMaisBarato => b.PrecoCent <= c.PrecoCent,
                    GanhaRegra.QualquerItem => true,
                    _ => false,
                };
                if (ok) { compra = c; break; }
            }
            if (compra is null) continue;
            usada.Add(compra); usada.Add(b);
            d[b.Linha] += b.PrecoCent;
            g[b.Linha]++;
            pares++;
        }
        if (pares > 0) return null;
        var c0 = compras.OrderBy(u => u.PrecoCent).First();
        var nomeCompra = itens[c0.Linha].ProdutoId;
        return p.GanhaRegra switch
        {
            GanhaRegra.MesmoProduto => "Compre e ganhe: leve mais 1 do mesmo produto e o segundo sai grátis",
            GanhaRegra.MesmoValor => $"Compre e ganhe: o brinde precisa custar {new Dinheiro(c0.PrecoCent).Formatado()}",
            GanhaRegra.QualquerMaisBarato => $"Compre e ganhe: o brinde precisa custar até {new Dinheiro(c0.PrecoCent).Formatado()}",
            GanhaRegra.Lista => "Compre e ganhe: falta o brinde da lista na comanda",
            _ => "Compre e ganhe: falta o brinde na comanda",
        };
    }

    /// <summary>Uma frase da regra, para o cabeçalho da vitrine e da comanda. Sem travessão.</summary>
    public static string DescreveRegra(Promo p) => p.Tipo switch
    {
        "leve_x_pague_y" when p.Leve is int l && p.Pague is int q => $"Leve {l}, pague {q}",
        "combo" when p.Combo is { } c => c.Modo == "desconto" && c.DescontoPct is decimal pct
            ? $"Combo com {pct:0.#}% de desconto"
            : $"Combo por {new Dinheiro(c.PrecoCent).Formatado()}",
        "compre_ganhe" => p.GanhaRegra switch
        {
            GanhaRegra.MesmoProduto => "Compre 1 e ganhe 1 do mesmo produto",
            GanhaRegra.MesmoValor => "Compre 1 e ganhe 1 do mesmo valor",
            GanhaRegra.QualquerMaisBarato => "Compre 1 e ganhe 1 de valor igual ou menor",
            GanhaRegra.QualquerItem => p.TetoCent is long t
                ? $"Compre 1 e ganhe 1 de até {new Dinheiro(t).Formatado()}"
                : "Compre 1 e ganhe 1, qualquer item",
            GanhaRegra.Lista => "Compre 1 e ganhe 1 da lista",
            _ => "Compre e ganhe (regra desconhecida)",
        },
        "percentual" when p.Percentual is decimal pp => $"{pp:0.#}% de desconto",
        "valor" or "valor_fixo" when p.ValorDescCent is long v => $"{new Dinheiro(v).Formatado()} de desconto",
        _ => p.Regras.Count > 0 ? "Preço do dia" : "Promoção",
    };

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
            partes.Add(string.Join(" e ", p.Janelas.Select(j => $"{j.Das:HH:mm} às {j.Ate:HH:mm}")));
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
    private static int? Int(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    private static long? Long(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
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
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _))
               .Select(x => x.GetInt32()).ToArray()
            : Array.Empty<int>();
    private static HashSet<string> Strs(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
               .Select(x => x.GetString()!).ToHashSet()
            : new HashSet<string>();
}
