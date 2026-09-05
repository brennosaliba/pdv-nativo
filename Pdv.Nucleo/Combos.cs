using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>
/// Uma escolha feita dentro de um combo ("2x Donut Ovomaltine" no grupo "Donuts").
///
/// A quantidade e POR UNIDADE do combo: a linha "2x COMBO 10 DONUTS" com 10
/// escolhas e duas caixas iguais. E assim que o motor de estoque le
/// (pdv_baixar_estoque_venda multiplica a quantidade da escolha pela da linha),
/// e e assim que o "+" da comanda duplica a caixa sem pedir os sabores de novo.
/// </summary>
/// <param name="GrupoId">pdv_combo_regras.id do grupo (grupo_regra_id na nuvem). Nulo
/// quando a regra sumiu entre a escolha e a venda: a escolha continua valendo.</param>
/// <param name="GrupoNome">Snapshot do nome do grupo, para a cozinha ("Donuts: 2x Ninho").</param>
public sealed record Escolha(string ProdutoId, string? Plu, string Nome, string? GrupoId, int Qtd,
    string? GrupoNome = null);

/// <summary>
/// Combos com sub-escolhas no caixa (05/09/2026). A composicao vive no PRODUTO-combo,
/// em grupos ("Donuts: 10 de 10"), e desce pronta da RPC pdv_combos_ativos para a
/// tabela local <c>combo(produto_id, payload)</c>, no mesmo padrao das promocoes.
///
/// O que mora aqui e PURO (sem WPF): o parser do payload, a resolucao da fonte de
/// cada grupo contra o catalogo local, o estado do dialogo (contadores, minimo,
/// maximo, "tudo igual") e os textos que a comanda, o cupom e a cozinha mostram.
/// A tela so desenha o que este tipo decide, e a suite prova aqui.
/// </summary>
public static class Combos
{
    /// <summary>Um produto que pode ser escolhido dentro de um grupo.</summary>
    public sealed record ItemFonte(string ProdutoId, string? Plu, string Nome);

    /// <summary>
    /// De onde saem as opcoes do grupo. <c>Tipo</c>: "itens" (lista fixa), "categoria"
    /// (a categoria inteira; <c>Grupo</c> e o TEXTO da categoria, que e o que o catalogo
    /// local tem) ou "todos" (o cardapio todo). <c>Itens</c> ja vem expandido do
    /// servidor nos tres casos.
    /// </summary>
    public sealed record Fonte(string Tipo, string? Grupo, IReadOnlyList<ItemFonte> Itens);

    /// <summary>Um grupo de escolha do combo: "Donuts", de 10 a 10, vindos da categoria Donuts.</summary>
    public sealed record GrupoDef(string Id, string Nome, int Min, int Max, Fonte Fonte);

    /// <summary>A composicao de UM produto-combo.</summary>
    public sealed record ComboDef(string ProdutoId, string? Plu, string Nome, IReadOnlyList<GrupoDef> Grupos);

    /// <summary>O que a tela sabe de um produto do catalogo local (o que ResolverFonte precisa).</summary>
    public sealed record ProdutoLocal(string Id, string? Plu, string Nome, string Categoria);

    // ── carga ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Todos os combos espelhados, por produto_id. Caixa cujo banco ainda nao tem a
    /// tabela (exe novo sobre banco antigo antes do Migrar) devolve vazio: sem combo
    /// cadastrado o caixa vende o produto como sempre vendeu.
    /// </summary>
    public static Dictionary<string, ComboDef> Carregar(SqliteConnection cx)
    {
        var r = new Dictionary<string, ComboDef>(StringComparer.Ordinal);
        IEnumerable<string> payloads;
        try { payloads = cx.Query<string>("SELECT payload FROM combo").ToList(); }
        catch { return r; }
        foreach (var payload in payloads)
        {
            var def = Parsear(payload);
            if (def is not null && def.Grupos.Count > 0) r[def.ProdutoId] = def;
        }
        return r;
    }

    /// <summary>
    /// Parser do jsonb de pdv_combos_ativos:
    /// {produto_id, plu, nome, grupos:[{id, nome, min, max, fonte:{tipo, grupo, itens:[{produto_id, plu, nome}]}}]}.
    /// Tolerante como o das promocoes: JSON estranho devolve null e o produto vende
    /// como item simples. Grupo sem id ou com min/max invalidos e descartado; combo
    /// sem grupo util devolve null.
    /// </summary>
    public static ComboDef? Parsear(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return null;
            var produtoId = Str(e, "produto_id");
            if (produtoId is null) return null;
            var grupos = new List<GrupoDef>();
            if (e.TryGetProperty("grupos", out var gs) && gs.ValueKind == JsonValueKind.Array)
                foreach (var g in gs.EnumerateArray())
                {
                    if (g.ValueKind != JsonValueKind.Object) continue;
                    var id = Str(g, "id");
                    if (id is null) continue;
                    var min = Int(g, "min") ?? Int(g, "quantidade") ?? 1;
                    var max = Int(g, "max") ?? min;
                    if (min < 0) min = 0;
                    if (max < Math.Max(1, min)) max = Math.Max(1, min);
                    var tipo = "itens"; string? grupoTxt = null;
                    var itens = new List<ItemFonte>();
                    if (g.TryGetProperty("fonte", out var f) && f.ValueKind == JsonValueKind.Object)
                    {
                        tipo = Str(f, "tipo") ?? "itens";
                        grupoTxt = Str(f, "grupo");
                        if (f.TryGetProperty("itens", out var its) && its.ValueKind == JsonValueKind.Array)
                            foreach (var it in its.EnumerateArray())
                            {
                                if (it.ValueKind != JsonValueKind.Object) continue;
                                var pid = Str(it, "produto_id");
                                if (pid is null) continue;
                                itens.Add(new ItemFonte(pid, Str(it, "plu"), Str(it, "nome") ?? pid));
                            }
                    }
                    if (tipo is not ("itens" or "categoria" or "todos")) tipo = "itens";
                    grupos.Add(new GrupoDef(id, Str(g, "nome") ?? "Escolha", min, max,
                        new Fonte(tipo, grupoTxt, itens)));
                }
            if (grupos.Count == 0) return null;
            return new ComboDef(produtoId, Str(e, "plu"), Str(e, "nome") ?? "COMBO", grupos);
        }
        catch { return null; }
    }

    /// <summary>
    /// As opcoes que o dialogo mostra para um grupo, ordenadas por nome (pt-BR).
    ///  · "itens": a lista do servidor, como veio;
    ///  · "categoria": a lista do servidor UNIAO o catalogo local da mesma categoria
    ///    (um produto que chegou por BaixarProdutosAsync antes do sino do combo
    ///    aparece na proxima abertura, sem esperar a RPC);
    ///  · "todos": a lista do servidor uniao o catalogo local inteiro.
    /// O proprio combo nunca entra (combo dentro de combo nao existe).
    /// </summary>
    public static List<ItemFonte> ResolverFonte(ComboDef combo, GrupoDef g, IEnumerable<ProdutoLocal> catalogo)
    {
        var vistos = new Dictionary<string, ItemFonte>(StringComparer.Ordinal);
        foreach (var i in g.Fonte.Itens)
            if (i.ProdutoId != combo.ProdutoId) vistos.TryAdd(i.ProdutoId, i);

        if (g.Fonte.Tipo is "categoria" or "todos")
        {
            var grupo = (g.Fonte.Grupo ?? "").Trim();
            foreach (var p in catalogo)
            {
                if (p.Id == combo.ProdutoId) continue;
                if (g.Fonte.Tipo == "categoria"
                    && !string.Equals(p.Categoria.Trim(), grupo, StringComparison.OrdinalIgnoreCase)) continue;
                vistos.TryAdd(p.Id, new ItemFonte(p.Id, p.Plu, p.Nome));
            }
        }
        return Categorias.OrdenarPorNome(vistos.Values, i => i.Nome).ToList();
    }

    // ── textos ──────────────────────────────────────────────────────────────

    /// <summary>"COMBO 10 DONUTS" vira "Combo 10 Donuts" (titulo do dialogo e das mensagens).</summary>
    public static string Titulo(ComboDef combo) => Capitalizar(combo.Nome);

    /// <summary>
    /// A sub-linha da comanda: "2x Ovomaltine · 3x Ninho". O nome do produto perde o
    /// prefixo redundante do grupo ("DONUT OVOMALTINE" no grupo "Donuts" vira
    /// "Ovomaltine") para caber mais sabores na linha; sem grupo, sai inteiro.
    /// </summary>
    public static string Resumo(IEnumerable<Escolha> escolhas)
        => string.Join(" · ", escolhas.Where(e => e.Qtd > 0).Select(e => $"{e.Qtd}x {NomeCurto(e)}"));

    /// <summary>Linhas do cupom, sem valor: "2x Donut Ovomaltine".</summary>
    public static List<string> LinhasCupom(IEnumerable<Escolha>? escolhas)
        => escolhas is null ? new() : escolhas.Where(e => e.Qtd > 0).Select(e => $"{e.Qtd}x {Capitalizar(e.Nome)}").ToList();

    /// <summary>
    /// Linhas da cozinha, no formato que o card e a comanda ja desenham
    /// ("Donuts: 2x Ovomaltine"; sem grupo, "2x Ovomaltine"). Multiplica pelas
    /// unidades da linha: duas caixas iguais sao 4 Ovomaltine para quem monta.
    /// </summary>
    public static List<string> LinhasKds(IEnumerable<Escolha>? escolhas, int unidades = 1)
    {
        if (escolhas is null) return new();
        var mult = Math.Max(1, unidades);
        return escolhas.Where(e => e.Qtd > 0)
            .Select(e => (e.GrupoNome is { Length: > 0 } g ? g + ": " : "") + $"{e.Qtd * mult}x {NomeCurto(e)}")
            .ToList();
    }

    /// <summary>
    /// O que falta para o combo poder ser vendido: "Combo 10 Donuts: faltam 3 sabores"
    /// (ou "falta 1 sabor"); com escolha que nenhum grupo aceita, "..., 1 fora do combo".
    /// Nulo quando todos os grupos estao no minimo e nada esta fora. E o portao do
    /// Finalizar: comanda com combo incompleto nao vai para o pagamento.
    ///
    /// Passa pela mesma realocacao do <see cref="Estado"/>: escolha com id de grupo
    /// velho (o painel republicou a composicao) conta no grupo cuja fonte tem o
    /// produto. <paramref name="catalogo"/> e o catalogo local, para fonte por
    /// categoria/todos casar como o dialogo casa.
    /// </summary>
    public static string? Pendencia(ComboDef combo, IReadOnlyList<Escolha>? escolhas, IEnumerable<ProdutoLocal>? catalogo = null)
    {
        var estado = new Estado(combo, escolhas, catalogo);
        var faltam = combo.Grupos.Sum(g => Math.Max(0, g.Min - estado.Total(g.Id)));
        var fora = estado.ForaDoCombo.Sum(e => e.Qtd);
        if (faltam == 0 && fora == 0) return null;
        var partes = new List<string>();
        if (faltam > 0) partes.Add(faltam == 1 ? "falta 1 sabor" : $"faltam {faltam} sabores");
        if (fora > 0) partes.Add($"{fora} fora do combo");
        return $"{Titulo(combo)}: {string.Join(", ", partes)}";
    }

    /// <summary>
    /// As escolhas como o combo ATUAL as entende: realocadas nos grupos de hoje (ids
    /// novos), sem o que ficou fora. E o que a venda deve gravar depois que a
    /// <see cref="Pendencia"/> liberou, para o grupo_regra_id que sobe ser o vigente.
    /// </summary>
    public static List<Escolha> Realocar(ComboDef combo, IReadOnlyList<Escolha>? escolhas, IEnumerable<ProdutoLocal>? catalogo = null)
        => new Estado(combo, escolhas, catalogo).Escolhas();

    /// <summary>JSON de venda_item.escolhas_json / ItemRascunho.EscolhasJson. Nulo sem escolhas.</summary>
    public static string? ParaJson(IReadOnlyList<Escolha>? escolhas)
        => escolhas is null ? null : JsonSerializer.Serialize(escolhas);

    /// <summary>O inverso de ParaJson. JSON ilegivel devolve null (a linha vira item simples, nunca derruba).</summary>
    public static List<Escolha>? DeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<Escolha>>(json); }
        catch { return null; }
    }

    /// <summary>"DONUT OVOMALTINE" no grupo "Donuts" vira "Ovomaltine"; sem grupo, "Donut Ovomaltine".</summary>
    public static string NomeCurto(Escolha e) => NomeCurto(e.Nome, e.GrupoNome);

    /// <summary>A mesma regra para o card do dialogo: o prefixo redundante do grupo sai.</summary>
    public static string NomeCurto(string nomeProduto, string? grupoNome)
    {
        var nome = nomeProduto.Trim();
        if (grupoNome is { Length: > 1 } g)
        {
            var singular = g.Trim();
            if (singular.EndsWith("s", StringComparison.OrdinalIgnoreCase)) singular = singular[..^1];
            foreach (var prefixo in new[] { g.Trim(), singular })
                if (nome.Length > prefixo.Length + 1
                    && nome.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase)
                    && nome[prefixo.Length] == ' ')
                { nome = nome[(prefixo.Length + 1)..].Trim(); break; }
        }
        return Capitalizar(nome);
    }

    /// <summary>CAIXA ALTA cansa: "DONUT NINHO" vira "Donut Ninho" (mesma regra da tela de venda).</summary>
    public static string Capitalizar(string s) =>
        string.Join(' ', s.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Length <= 2 && p != "kg" ? p : char.ToUpperInvariant(p[0]) + p[1..]));

    // ── o estado do dialogo (puro) ──────────────────────────────────────────

    /// <summary>
    /// Os contadores do dialogo, sem WPF. A tela chama Mais/Menos/TudoIgual e repinta
    /// pelo que este objeto diz: o que pode, o que falta, o que esta completo.
    /// </summary>
    public sealed class Estado
    {
        public ComboDef Combo { get; }
        private readonly Dictionary<string, List<Escolha>> _porGrupo = new(StringComparer.Ordinal);

        private readonly List<Escolha> _fora = new();
        private readonly Dictionary<string, List<ItemFonte>> _fontes = new(StringComparer.Ordinal);

        /// <summary>
        /// Escolhas que nenhum grupo aceita (sabor que saiu da composicao, ou grupo ja
        /// cheio). Nunca somem em silencio: o dialogo lista, o operador tira ou troca, e
        /// enquanto houver uma o combo nao esta <see cref="Completo"/>.
        /// </summary>
        public IReadOnlyList<Escolha> ForaDoCombo => _fora;

        /// <summary>
        /// Monta os contadores a partir do que a comanda guardou. A escolha entra no seu
        /// grupo quando ele ainda existe e a fonte tem o produto. Senao (o painel
        /// republicou a composicao e os ids trocaram), vai para o primeiro grupo cuja fonte
        /// tem o produto (por produto_id ou PLU) e que ainda tem vaga; o que nao coube
        /// em lugar nenhum vai para <see cref="ForaDoCombo"/>. <paramref name="catalogo"/>
        /// e o catalogo local (fonte por categoria/todos resolve como no dialogo).
        /// </summary>
        public Estado(ComboDef combo, IReadOnlyList<Escolha>? atual = null, IEnumerable<ProdutoLocal>? catalogo = null)
        {
            Combo = combo;
            var cat = catalogo?.ToList();
            foreach (var g in combo.Grupos)
            {
                _porGrupo[g.Id] = new List<Escolha>();
                _fontes[g.Id] = cat is null
                    ? g.Fonte.Itens.Where(i => i.ProdutoId != combo.ProdutoId).ToList()
                    : ResolverFonte(combo, g, cat);
            }
            if (atual is null) return;
            foreach (var e in atual)
            {
                if (e.Qtd <= 0) continue;
                var item = new ItemFonte(e.ProdutoId, e.Plu, e.Nome);
                var proprio = e.GrupoId is not null && _porGrupo.ContainsKey(e.GrupoId)
                    ? combo.Grupos.First(x => x.Id == e.GrupoId) : null;
                if (proprio is not null && Aceita(proprio, e))
                {
                    // o grupo de origem continua valendo: acima do maximo (a regra
                    // encolheu) corta no maximo, como o cabecalho "10 de 10" mostra
                    var pode = Math.Min(e.Qtd, proprio.Max - Total(proprio.Id));
                    if (pode > 0) Somar(proprio, item, pode);
                    continue;
                }
                var resta = e.Qtd;
                foreach (var g in combo.Grupos)
                {
                    if (resta == 0) break;
                    if (!Aceita(g, e)) continue;
                    var pode = Math.Min(resta, g.Max - Total(g.Id));
                    if (pode <= 0) continue;
                    Somar(g, item, pode);
                    resta -= pode;
                }
                if (resta > 0) _fora.Add(e with { Qtd = resta });
            }
        }

        /// <summary>A fonte do grupo tem este produto (por id, ou por PLU quando os dois tem)?</summary>
        private bool Aceita(GrupoDef g, Escolha e)
            => _fontes[g.Id].Any(i => i.ProdutoId == e.ProdutoId
                || (e.Plu is { Length: > 0 } && i.Plu is { Length: > 0 } && i.Plu == e.Plu));

        /// <summary>Tira uma escolha da lista "fora do combo". Devolve false se nao estava la.</summary>
        public bool TirarFora(string produtoId)
            => _fora.RemoveAll(e => e.ProdutoId == produtoId) > 0;

        public int Total(string grupoId) => _porGrupo[grupoId].Sum(e => e.Qtd);

        public int Quantos(string grupoId, string produtoId)
            => _porGrupo[grupoId].FirstOrDefault(e => e.ProdutoId == produtoId)?.Qtd ?? 0;

        /// <summary>Ainda cabe mais um neste grupo?</summary>
        public bool PodeMais(GrupoDef g) => Total(g.Id) < g.Max;

        /// <summary>+1 no item. Devolve false (e nao mexe) quando o grupo ja esta no maximo.</summary>
        public bool Mais(GrupoDef g, ItemFonte item)
        {
            if (!PodeMais(g)) return false;
            Somar(g, item, 1);
            return true;
        }

        /// <summary>-1 no item; em zero a linha some.</summary>
        public bool Menos(GrupoDef g, string produtoId)
        {
            var lista = _porGrupo[g.Id];
            var idx = lista.FindIndex(e => e.ProdutoId == produtoId);
            if (idx < 0) return false;
            var e = lista[idx];
            if (e.Qtd <= 1) lista.RemoveAt(idx);
            else lista[idx] = e with { Qtd = e.Qtd - 1 };
            return true;
        }

        /// <summary>
        /// "Tudo igual": completa o grupo ate o maximo com este item ("12 Ninho" em dois
        /// toques). So faz sentido com um item marcado, mas aceita qualquer estado: o que
        /// ja estava marcado fica.
        /// </summary>
        public void TudoIgual(GrupoDef g, ItemFonte item)
        {
            var falta = g.Max - Total(g.Id);
            if (falta > 0) Somar(g, item, falta);
        }

        /// <summary>O unico item marcado no grupo, quando ha exatamente um (e o gatilho do "Tudo igual").</summary>
        public ItemFonte? UnicoMarcado(GrupoDef g)
        {
            var lista = _porGrupo[g.Id];
            return lista.Count == 1 ? new ItemFonte(lista[0].ProdutoId, lista[0].Plu, lista[0].Nome) : null;
        }

        /// <summary>Todos os grupos no minimo e nada fora do combo: o botao Adicionar liga.</summary>
        public bool Completo => _fora.Count == 0 && Combo.Grupos.All(g => Total(g.Id) >= g.Min);

        /// <summary>"Donuts · 7 de 10" (cabecalho do grupo).</summary>
        public string Progresso(GrupoDef g) => $"{g.Nome} · {Total(g.Id)} de {g.Max}";

        /// <summary>Fracao preenchida do grupo, 0..1 (a barra fina do cabecalho).</summary>
        public double Fracao(GrupoDef g) => g.Max <= 0 ? 1 : Math.Min(1.0, (double)Total(g.Id) / g.Max);

        /// <summary>
        /// "Faltam 3 donuts" / "Falta 1 bebida" / "Faltam 2 donuts e 1 bebida"; com escolha
        /// fora do combo, "... · 1 fora do combo" (ou so "1 fora do combo"). Nulo quando completo.
        /// </summary>
        public string? Faltam
        {
            get
            {
                var partes = new List<string>();
                var total = 0;
                foreach (var g in Combo.Grupos)
                {
                    var falta = g.Min - Total(g.Id);
                    if (falta <= 0) continue;
                    total += falta;
                    partes.Add($"{falta} {g.Nome.ToLowerInvariant()}");
                }
                var texto = partes.Count == 0 ? null : (total == 1 ? "Falta " : "Faltam ") + string.Join(" e ", partes);
                var fora = _fora.Sum(e => e.Qtd);
                if (fora == 0) return texto;
                return (texto is null ? "" : texto + " · ") + $"{fora} fora do combo";
            }
        }

        /// <summary>As escolhas, na ordem dos grupos e de marcacao. E o que vai para a comanda.</summary>
        public List<Escolha> Escolhas()
            => Combo.Grupos.SelectMany(g => _porGrupo[g.Id]).ToList();

        private void Somar(GrupoDef g, ItemFonte item, int qtd)
        {
            var lista = _porGrupo[g.Id];
            var idx = lista.FindIndex(e => e.ProdutoId == item.ProdutoId);
            if (idx >= 0) lista[idx] = lista[idx] with { Qtd = lista[idx].Qtd + qtd };
            else lista.Add(new Escolha(item.ProdutoId, item.Plu, item.Nome, g.Id, qtd, g.Nome));
        }
    }

    // ── json helpers ────────────────────────────────────────────────────────
    private static string? Str(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string k)
    {
        if (!e.TryGetProperty(k, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }
}
