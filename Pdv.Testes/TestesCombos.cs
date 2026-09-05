using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// COMBO COM SUB-ESCOLHAS no caixa (05/09/2026). O dono descreveu a quebra: "COMBO 10
/// DONUTS" vendia como uma linha sem conteudo, e o estoque baixava "10 de um sabor
/// qualquer" (ou nada). Agora o toque no combo abre o dialogo dos sabores, a comanda
/// mostra o que foi montado, a nuvem recebe as escolhas por linha (p_escolhas) e a
/// nota continua com UM det por linha.
///
/// O que se prova, em ordem: o parser do payload de pdv_combos_ativos; a resolucao da
/// fonte (servidor ∪ catalogo local); o estado do dialogo (minimo, maximo, tudo
/// igual); os textos (comanda, cupom, cozinha, pendencia; sem travessao); o rascunho
/// antigo; a venda (escolhas_json, tipo venda_composta, p_escolhas com seq; venda
/// comum byte a byte como era); a fila contra o Supabase de mentira (RPC certa para
/// cada tipo); a descida (BaixarCombosAsync + impressao digital); a nota (1 det); e a
/// TELA (dialogo e comanda, em STA).
/// </summary>
public static class TestesCombos
{
    private const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;

    // ids fixos do cenario
    private const string Combo = "combo-10-donuts";
    private const string Ninho = "d-ninho", Ovo = "d-ovomaltine", Churros = "d-churros", Agua = "b-agua";
    private const string GrupoDonuts = "regra-donuts";

    /// <summary>O payload no shape EXATO de pdv_combos_ativos (contrato do desenho).</summary>
    private static string PayloadCombo(int min = 10, int max = 10) => $$$"""
        {"produto_id":"{{{Combo}}}","plu":"87","nome":"COMBO 10 DONUTS",
         "grupos":[{"id":"{{{GrupoDonuts}}}","nome":"Donuts","min":{{{min}}},"max":{{{max}}},
                    "fonte":{"tipo":"categoria","grupo":"Donuts",
                             "itens":[{"produto_id":"{{{Ninho}}}","plu":"4","nome":"DONUT NINHO"},
                                      {"produto_id":"{{{Ovo}}}","plu":"5","nome":"DONUT OVOMALTINE"}]}}]}
        """;

    private static readonly List<Combos.ProdutoLocal> Catalogo = new()
    {
        new(Combo, "87", "COMBO 10 DONUTS", "Combos"),
        new(Ninho, "4", "DONUT NINHO", "Donuts"),
        new(Ovo, "5", "DONUT OVOMALTINE", "Donuts"),
        new(Churros, "6", "DONUT CHURROS", "Donuts"),   // so no catalogo local (chegou antes do sino)
        new(Agua, "9", "AGUA 500ML", "Bebidas"),
    };

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        Parser(checar);
        Fonte(checar);
        Estado(checar);
        Textos(checar);
        RascunhoAntigo(checar);
        await VendaEFilaAsync(checar);
        Fiscal(checar);
        await DescidaAsync(checar);
        Tela(checar);
    }

    // ── 1. parser ──────────────────────────────────────────────────────────
    private static void Parser(Action<bool, string> checar)
    {
        var def = Combos.Parsear(PayloadCombo());
        checar(def is not null && def.ProdutoId == Combo && def.Plu == "87" && def.Nome == "COMBO 10 DONUTS",
            "parser: produto_id, plu e nome do payload de pdv_combos_ativos");
        checar(def is { Grupos.Count: 1 } && def.Grupos[0].Id == GrupoDonuts && def.Grupos[0].Nome == "Donuts"
            && def.Grupos[0].Min == 10 && def.Grupos[0].Max == 10,
            "parser: grupo com id (grupo_regra_id), nome, min e max");
        checar(def!.Grupos[0].Fonte.Tipo == "categoria" && def.Grupos[0].Fonte.Grupo == "Donuts"
            && def.Grupos[0].Fonte.Itens.Count == 2 && def.Grupos[0].Fonte.Itens[1].Nome == "DONUT OVOMALTINE",
            "parser: fonte categoria com o texto da categoria E a lista expandida pelo servidor");

        // min/max: "quantidade" antiga vale como min=max; max menor que min sobe para min
        var q = Combos.Parsear($$$"""{"produto_id":"x","nome":"X","grupos":[{"id":"g","nome":"G","quantidade":4,"fonte":{"tipo":"todos"}}]}""");
        checar(q is not null && q.Grupos[0].Min == 4 && q.Grupos[0].Max == 4 && q.Grupos[0].Fonte.Tipo == "todos",
            "parser: 'quantidade' sem min/max vira min=max; fonte 'todos' sem itens e valida");
        var mm = Combos.Parsear($$$"""{"produto_id":"x","nome":"X","grupos":[{"id":"g","nome":"G","min":3,"max":1,"fonte":{"tipo":"itens","itens":[]}}]}""");
        checar(mm is not null && mm.Grupos[0].Max == 3, "parser: max menor que min sobe para o min (nunca trava o dialogo)");

        checar(Combos.Parsear("{nao-e-json") is null, "parser: JSON quebrado devolve null (o produto vende como simples)");
        checar(Combos.Parsear("""{"produto_id":"x","nome":"X","grupos":[]}""") is null, "parser: combo sem grupo nao e combo");
        checar(Combos.Parsear("""{"produto_id":"x","nome":"X","grupos":[{"nome":"sem id","min":1}]}""") is null,
            "parser: grupo sem id e descartado (sem grupo_regra_id a nuvem nao casa a escolha)");
        checar(Combos.Parsear("""{"produto_id":"x","nome":"X","grupos":[{"id":"g","nome":"G","min":2,"fonte":{"tipo":"xyz"}}]}""")!.Grupos[0].Fonte.Tipo == "itens",
            "parser: tipo de fonte desconhecido cai em 'itens' (so o que o servidor listou)");

        // carga do espelho local, inclusive quando a tabela ainda nao existe
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-combos-{Guid.NewGuid():N}.db");
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir(arquivo);
            cx.Execute("INSERT INTO combo (produto_id, payload) VALUES (@i,@p)", new { i = Combo, p = PayloadCombo() });
            cx.Execute("INSERT INTO combo (produto_id, payload) VALUES ('lixo','{nao')");
            var todos = Combos.Carregar(cx);
            checar(todos.Count == 1 && todos.ContainsKey(Combo), "Carregar: le o espelho `combo` e ignora payload ilegivel");
            cx.Execute("DROP TABLE combo");
            checar(Combos.Carregar(cx).Count == 0, "Carregar: banco sem a tabela (exe novo antes do Migrar) devolve vazio, nao excecao");
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(arquivo); } catch { } }
    }

    // ── 2. fonte: servidor ∪ catalogo local ────────────────────────────────
    private static void Fonte(Action<bool, string> checar)
    {
        var def = Combos.Parsear(PayloadCombo())!;
        var g = def.Grupos[0];
        var itens = Combos.ResolverFonte(def, g, Catalogo);
        checar(itens.Select(i => i.ProdutoId).OrderBy(x => x).SequenceEqual(new[] { Churros, Ninho, Ovo }),
            "fonte categoria = lista do servidor UNIAO catalogo local da categoria (o Churros so local entra)");
        checar(itens.Select(i => i.Nome).SequenceEqual(new[] { "DONUT CHURROS", "DONUT NINHO", "DONUT OVOMALTINE" }),
            "fonte ordenada por nome (pt-BR)");
        checar(itens.All(i => i.ProdutoId != Agua && i.ProdutoId != Combo),
            "fonte categoria nao traz outra categoria nem o proprio combo");

        var soItens = def with { Grupos = new[] { g with { Fonte = g.Fonte with { Tipo = "itens" } } } };
        checar(Combos.ResolverFonte(soItens, soItens.Grupos[0], Catalogo).Select(i => i.ProdutoId).OrderBy(x => x)
                   .SequenceEqual(new[] { Ninho, Ovo }),
            "fonte 'itens' = so o que o servidor listou (o catalogo local nao acrescenta)");

        var todos = def with { Grupos = new[] { g with { Fonte = new Combos.Fonte("todos", null, Array.Empty<Combos.ItemFonte>()) } } };
        var t = Combos.ResolverFonte(todos, todos.Grupos[0], Catalogo);
        checar(t.Count == 4 && t.All(i => i.ProdutoId != Combo) && t.Any(i => i.ProdutoId == Agua),
            "fonte 'todos' = o cardapio local inteiro menos o proprio combo");

        // o mesmo produto no servidor e no local nao duplica
        checar(itens.Count(i => i.ProdutoId == Ninho) == 1, "produto presente nos dois lados aparece uma vez");
    }

    // ── 3. o estado do dialogo ─────────────────────────────────────────────
    private static void Estado(Action<bool, string> checar)
    {
        var def = Combos.Parsear(PayloadCombo())!;
        var g = def.Grupos[0];
        var ninho = new Combos.ItemFonte(Ninho, "4", "DONUT NINHO");
        var ovo = new Combos.ItemFonte(Ovo, "5", "DONUT OVOMALTINE");
        var e = new Combos.Estado(def);

        checar(!e.Completo && e.Faltam == "Faltam 10 donuts" && e.Progresso(g) == "Donuts · 0 de 10",
            "vazio: incompleto, 'Faltam 10 donuts', 'Donuts · 0 de 10'");
        for (var i = 0; i < 3; i++) e.Mais(g, ovo);
        checar(e.Total(g.Id) == 3 && e.Quantos(g.Id, Ovo) == 3 && e.Faltam == "Faltam 7 donuts" && e.Progresso(g) == "Donuts · 3 de 10",
            "3 toques no Ovomaltine: 'Donuts · 3 de 10', 'Faltam 7 donuts'");
        checar(e.UnicoMarcado(g)?.ProdutoId == Ovo, "com um sabor marcado, ele e o candidato do 'Tudo igual'");
        e.TudoIgual(g, ovo);
        checar(e.Total(g.Id) == 10 && e.Quantos(g.Id, Ovo) == 10 && e.Completo && e.Faltam is null,
            "'Tudo igual' completa o grupo ate o maximo com o sabor marcado (10 Ovomaltine em dois toques)");
        checar(!e.PodeMais(g) && !e.Mais(g, ninho) && e.Total(g.Id) == 10,
            "no maximo o + nao entra (Mais devolve false e nao mexe)");
        checar(e.Menos(g, Ovo) && e.Total(g.Id) == 9 && !e.Completo && e.Faltam == "Falta 1 donuts".Replace("donuts", "donuts"),
            "menos 1: 9 de 10, e 'Falta 1 donuts' no singular do verbo");
        checar(e.Mais(g, ninho) && e.Quantos(g.Id, Ninho) == 1 && e.UnicoMarcado(g) is null,
            "dois sabores marcados: nao ha 'unico' para o Tudo igual");
        var escolhas = e.Escolhas();
        checar(escolhas.Count == 2 && escolhas[0].ProdutoId == Ovo && escolhas[0].Qtd == 9 && escolhas[0].GrupoId == GrupoDonuts
            && escolhas[0].GrupoNome == "Donuts" && escolhas[1].ProdutoId == Ninho && escolhas[1].Qtd == 1,
            "Escolhas(): uma por sabor, com grupo_regra_id e o nome do grupo, na ordem de marcacao");
        for (var i = 0; i < 9; i++) e.Menos(g, Ovo);
        checar(e.Quantos(g.Id, Ovo) == 0 && e.Escolhas().Count == 1, "menos ate zero tira o sabor da lista");

        // reabertura pre-preenchida (toque na sub-linha)
        var pre = new Combos.Estado(def, new[] { new Escolha(Ovo, "5", "DONUT OVOMALTINE", GrupoDonuts, 7) });
        checar(pre.Total(g.Id) == 7 && pre.Progresso(g) == "Donuts · 7 de 10" && pre.Faltam == "Faltam 3 donuts",
            "reabrir com 7 marcados: 'Donuts · 7 de 10' e 'Faltam 3 donuts'");
        var estourado = new Combos.Estado(def, new[] { new Escolha(Ovo, "5", "DONUT OVOMALTINE", GrupoDonuts, 14) });
        checar(estourado.Total(g.Id) == 10, "escolha antiga acima do maximo (regra mudou) e cortada no maximo");
        var semGrupo = new Combos.Estado(def, new[] { new Escolha(Ovo, "5", "DONUT OVOMALTINE", null, 2) });
        checar(semGrupo.Total(g.Id) == 2, "escolha sem grupo cai no unico grupo do combo");

        // dois grupos: cada um no seu minimo
        var dois = Combos.Parsear($$$"""
            {"produto_id":"c2","nome":"COMBO LANCHE","grupos":[
              {"id":"g1","nome":"Donuts","min":2,"max":2,"fonte":{"tipo":"itens","itens":[{"produto_id":"{{{Ninho}}}","nome":"DONUT NINHO"}]}},
              {"id":"g2","nome":"Bebida","min":1,"max":1,"fonte":{"tipo":"itens","itens":[{"produto_id":"{{{Agua}}}","nome":"AGUA 500ML"}]}}]}
            """)!;
        var e2 = new Combos.Estado(dois);
        checar(e2.Faltam == "Faltam 2 donuts e 1 bebida", "dois grupos: 'Faltam 2 donuts e 1 bebida'");
        e2.Mais(dois.Grupos[0], ninho); e2.Mais(dois.Grupos[0], ninho);
        checar(!e2.Completo && e2.Faltam == "Falta 1 bebida", "donuts completos, bebida faltando: 'Falta 1 bebida'");
        e2.Mais(dois.Grupos[1], new Combos.ItemFonte(Agua, "9", "AGUA 500ML"));
        checar(e2.Completo && e2.Escolhas().Count == 2 && e2.Escolhas()[1].GrupoId == "g2", "os dois no minimo: completo, escolhas por grupo");

        Republicacao(checar);
    }

    // ── 3b. republicacao: os ids dos grupos mudaram entre a escolha e a reabertura ──
    /// <summary>
    /// O painel regrava a composicao e os ids de pdv_combo_regras trocam. A comanda
    /// guarda os ids velhos. Antes, Estado descartava em silencio toda escolha de grupo
    /// desconhecido (salvo combo de um grupo so) e o Finalizar recusava "faltam N".
    /// Agora a escolha orfa e REALOCADA no primeiro grupo cuja fonte tem o produto e
    /// ainda tem vaga; sem grupo que a aceite, fica "fora do combo" (o operador tira ou
    /// troca) e a pendencia conta so o que esta alocado.
    /// </summary>
    private static void Republicacao(Action<bool, string> checar)
    {
        const string Cookie = "c-choco";
        static string Payload(string idDonuts, string idCookies, string tipo = "itens", bool comChurros = false) => $$$"""
            {"produto_id":"c4","nome":"COMBO 4 DONUTS + 2 COOKIES","grupos":[
              {"id":"{{{idDonuts}}}","nome":"Donuts","min":4,"max":4,"fonte":{"tipo":"{{{tipo}}}","grupo":"Donuts","itens":[
                 {"produto_id":"{{{Ninho}}}","plu":"4","nome":"DONUT NINHO"},{"produto_id":"{{{Ovo}}}","plu":"5","nome":"DONUT OVOMALTINE"}
                 {{{(comChurros ? $$"""
            ,{"produto_id":"{{Churros}}","plu":"6","nome":"DONUT CHURROS"}
            """ : "")}}}]}},
              {"id":"{{{idCookies}}}","nome":"Cookies","min":2,"max":2,"fonte":{"tipo":"itens","itens":[
                 {"produto_id":"{{{Cookie}}}","plu":"20","nome":"COOKIE CHOCOLATE"}]}}]}
            """;
        var antigo = Combos.Parsear(Payload("regra-d-1", "regra-c-1"))!;
        var novo = Combos.Parsear(Payload("regra-d-2", "regra-c-2"))!;
        var gD = novo.Grupos[0]; var gC = novo.Grupos[1];

        // a comanda foi montada com o combo ANTIGO
        var montado = new Combos.Estado(antigo);
        montado.Mais(antigo.Grupos[0], new Combos.ItemFonte(Ninho, "4", "DONUT NINHO"));
        montado.Mais(antigo.Grupos[0], new Combos.ItemFonte(Ninho, "4", "DONUT NINHO"));
        montado.Mais(antigo.Grupos[0], new Combos.ItemFonte(Ovo, "5", "DONUT OVOMALTINE"));
        montado.Mais(antigo.Grupos[0], new Combos.ItemFonte(Ovo, "5", "DONUT OVOMALTINE"));
        montado.Mais(antigo.Grupos[1], new Combos.ItemFonte(Cookie, "20", "COOKIE CHOCOLATE"));
        montado.Mais(antigo.Grupos[1], new Combos.ItemFonte(Cookie, "20", "COOKIE CHOCOLATE"));
        var escolhas = montado.Escolhas();
        checar(montado.Completo && Combos.Pendencia(antigo, escolhas) is null, "republicacao: comanda montada no combo antigo esta completa");

        // (a) ids trocados, mesmas fontes: tudo realocado, nada pendente
        var a = new Combos.Estado(novo, escolhas);
        checar(a.Total(gD.Id) == 4 && a.Total(gC.Id) == 2 && a.ForaDoCombo.Count == 0 && a.Completo && a.Faltam is null,
            $"(a) ids trocados, mesmas fontes: 4 donuts e 2 cookies realocados, completo (donuts={a.Total(gD.Id)} cookies={a.Total(gC.Id)} fora={a.ForaDoCombo.Count})");
        checar(a.Escolhas().All(e => e.GrupoId == gD.Id || e.GrupoId == gC.Id) && a.Escolhas().Count == 3
            && a.Quantos(gD.Id, Ninho) == 2 && a.Quantos(gD.Id, Ovo) == 2 && a.Quantos(gC.Id, Cookie) == 2,
            "(a) as escolhas saem com os ids NOVOS dos grupos, mesmas quantidades");
        checar(Combos.Pendencia(novo, escolhas) is null, $"(a) Pendencia zero: o Finalizar nao recusa (veio '{Combos.Pendencia(novo, escolhas)}')");
        // por PLU tambem casa (produto_id trocou no painel, plu ficou)
        var porPlu = new Combos.Estado(novo, new[] { new Escolha("id-velho", "4", "DONUT NINHO", "regra-d-1", 4), new Escolha("id-velho-2", "20", "COOKIE CHOCOLATE", "regra-c-1", 2) });
        checar(porPlu.Completo && porPlu.ForaDoCombo.Count == 0, "(a) escolha orfa casa com a fonte tambem pelo PLU");

        // (b) um sabor saiu da fonte: fica "fora do combo", visivel; pendencia conta so o alocado
        var comChurros = Combos.Parsear(Payload("regra-d-1", "regra-c-1", comChurros: true))!;
        var montadoB = new Combos.Estado(comChurros);
        montadoB.Mais(comChurros.Grupos[0], new Combos.ItemFonte(Ninho, "4", "DONUT NINHO"));
        montadoB.Mais(comChurros.Grupos[0], new Combos.ItemFonte(Ninho, "4", "DONUT NINHO"));
        montadoB.Mais(comChurros.Grupos[0], new Combos.ItemFonte(Ninho, "4", "DONUT NINHO"));
        montadoB.Mais(comChurros.Grupos[0], new Combos.ItemFonte(Churros, "6", "DONUT CHURROS"));
        montadoB.Mais(comChurros.Grupos[1], new Combos.ItemFonte(Cookie, "20", "COOKIE CHOCOLATE"));
        montadoB.Mais(comChurros.Grupos[1], new Combos.ItemFonte(Cookie, "20", "COOKIE CHOCOLATE"));
        var escolhasB = montadoB.Escolhas();
        var b = new Combos.Estado(novo, escolhasB);
        checar(b.Total(gD.Id) == 3 && b.Total(gC.Id) == 2 && b.ForaDoCombo.Count == 1
            && b.ForaDoCombo[0].ProdutoId == Churros && b.ForaDoCombo[0].Qtd == 1,
            $"(b) Churros saiu da fonte: 3 donuts alocados, 1 Churros 'fora do combo' (fora={b.ForaDoCombo.Count})");
        checar(!b.Completo && b.Faltam == "Falta 1 donuts · 1 fora do combo",
            $"(b) rodape: 'Falta 1 donuts · 1 fora do combo' (veio '{b.Faltam}')");
        checar(Combos.Pendencia(novo, escolhasB) == "Combo 4 Donuts + 2 Cookies: falta 1 sabor, 1 fora do combo",
            $"(b) Pendencia diz o motivo: 'falta 1 sabor, 1 fora do combo' (veio '{Combos.Pendencia(novo, escolhasB)}')");
        checar(b.Escolhas().All(e => e.ProdutoId != Churros), "(b) Escolhas() nao leva o que esta fora do combo");
        checar(b.TirarFora(Churros) && b.ForaDoCombo.Count == 0 && b.Faltam == "Falta 1 donuts" && !b.TirarFora(Churros),
            "(b) Tirar o Churros: some da lista, rodape volta a 'Falta 1 donuts'");
        b.Mais(gD, new Combos.ItemFonte(Ovo, "5", "DONUT OVOMALTINE"));
        checar(b.Completo && b.Faltam is null, "(b) trocado por um Ovomaltine: completo");
        // so fora, nada faltando: a pendencia ainda barra (o operador precisa ver)
        var soFora = new Combos.Estado(novo, escolhasB.Concat(new[] { new Escolha(Ovo, "5", "DONUT OVOMALTINE", "regra-d-1", 1) }).ToList());
        checar(soFora.Total(gD.Id) == 4 && soFora.ForaDoCombo.Count == 1 && !soFora.Completo && soFora.Faltam == "1 fora do combo",
            $"(b) grupos cheios e um fora: nao completa, rodape '1 fora do combo' (veio '{soFora.Faltam}')");
        checar(Combos.Pendencia(novo, soFora.Escolhas().Concat(soFora.ForaDoCombo).ToList()) == "Combo 4 Donuts + 2 Cookies: 1 fora do combo",
            "(b) Pendencia so com fora: 'Combo 4 Donuts + 2 Cookies: 1 fora do combo'");
        // com o catalogo local e fonte por categoria, o Churros (so local) E aceito
        var categoria = Combos.Parsear(Payload("regra-d-2", "regra-c-2", tipo: "categoria"))!;
        var bCat = new Combos.Estado(categoria, escolhasB, Catalogo);
        checar(bCat.ForaDoCombo.Count == 0 && bCat.Total(categoria.Grupos[0].Id) == 4 && Combos.Pendencia(categoria, escolhasB, Catalogo) is null,
            "(b) fonte por categoria + catalogo local: o Churros e aceito (nao e fora do combo)");
        // grupo cheio nao aceita: a sobra fica fora, nao some
        var lotado = new Combos.Estado(novo, escolhas.Concat(new[] { new Escolha(Churros, "6", "DONUT CHURROS", "regra-d-1", 1), new Escolha(Ninho, "4", "DONUT NINHO", "regra-x", 2) }).ToList());
        checar(lotado.Total(gD.Id) == 4 && lotado.ForaDoCombo.Count == 2 && lotado.ForaDoCombo.Sum(e => e.Qtd) == 3,
            $"(b) o que nao coube (grupo cheio) tambem fica fora do combo, nunca some (fora={lotado.ForaDoCombo.Sum(e => e.Qtd)})");

        // (c) combo de um grupo so continua igual
        var um = Combos.Parsear(PayloadCombo())!;
        var c = new Combos.Estado(um, new[] { new Escolha(Ovo, "5", "DONUT OVOMALTINE", "regra-velha", 7) });
        checar(c.Total(um.Grupos[0].Id) == 7 && c.ForaDoCombo.Count == 0 && c.Faltam == "Faltam 3 donuts",
            "(c) um grupo: escolha de id velho cai no grupo, 'Faltam 3 donuts'");
        checar(Combos.Pendencia(um, new[] { new Escolha(Ovo, "5", "DONUT OVOMALTINE", "regra-velha", 10) }) is null,
            "(c) um grupo: Pendencia zero com o id velho");
        var cFora = new Combos.Estado(um, new[] { new Escolha(Ovo, "5", "DONUT OVOMALTINE", GrupoDonuts, 10), new Escolha(Agua, "9", "AGUA 500ML", GrupoDonuts, 1) });
        checar(cFora.Total(um.Grupos[0].Id) == 10 && cFora.ForaDoCombo.Count == 1 && cFora.Faltam == "1 fora do combo",
            "(c) um grupo: produto que nao e da fonte fica fora do combo, mesmo com o id do grupo");
    }

    // ── 4. textos ──────────────────────────────────────────────────────────
    private static void Textos(Action<bool, string> checar)
    {
        var def = Combos.Parsear(PayloadCombo())!;
        var escolhas = new List<Escolha>
        {
            new(Ovo, "5", "DONUT OVOMALTINE", GrupoDonuts, 2, "Donuts"),
            new(Ninho, "4", "DONUT NINHO", GrupoDonuts, 8, "Donuts"),
        };
        checar(Combos.Titulo(def) == "Combo 10 Donuts", "titulo: 'COMBO 10 DONUTS' vira 'Combo 10 Donuts'");
        checar(Combos.Resumo(escolhas) == "2x Ovomaltine · 8x Ninho",
            $"sub-linha da comanda: '2x Ovomaltine · 8x Ninho' (sem o prefixo do grupo); veio '{Combos.Resumo(escolhas)}'");
        checar(Combos.Resumo(new[] { new Escolha(Ovo, "5", "DONUT OVOMALTINE", null, 2) }) == "2x Donut Ovomaltine",
            "sem nome de grupo o nome do produto sai inteiro");
        checar(Combos.NomeCurto("DONUT OVOMALTINE", "Donuts") == "Ovomaltine" && Combos.NomeCurto("AGUA 500ML", "Donuts") == "Agua 500ml"
            && Combos.NomeCurto("DONUT OVOMALTINE", null) == "Donut Ovomaltine",
            "card do dialogo: o prefixo do grupo sai ('Ovomaltine'); nome sem o prefixo fica inteiro");
        checar(Combos.LinhasKds(escolhas).SequenceEqual(new[] { "Donuts: 2x Ovomaltine", "Donuts: 8x Ninho" }),
            "cozinha: 'Donuts: 2x Ovomaltine' (o formato que o card e a comanda ja desenham)");
        checar(Combos.LinhasKds(escolhas, 2).SequenceEqual(new[] { "Donuts: 4x Ovomaltine", "Donuts: 16x Ninho" }),
            "cozinha: duas caixas iguais multiplicam os sabores (4 e 16)");
        checar(Combos.LinhasCupom(escolhas).SequenceEqual(new[] { "2x Donut Ovomaltine", "8x Donut Ninho" }),
            "cupom: '2x Donut Ovomaltine', sem valor");
        checar(Combos.LinhasCupom(null).Count == 0 && Combos.LinhasKds(null).Count == 0, "item simples: sem sub-linhas");

        checar(Combos.Pendencia(def, escolhas) is null, "pendencia: 10 de 10, nada falta");
        checar(Combos.Pendencia(def, escolhas.Take(1).ToList()) == "Combo 10 Donuts: faltam 8 sabores",
            "pendencia: 'Combo 10 Donuts: faltam 8 sabores'");
        checar(Combos.Pendencia(def, new[] { new Escolha(Ninho, "4", "DONUT NINHO", GrupoDonuts, 9) }) == "Combo 10 Donuts: falta 1 sabor",
            "pendencia no singular: 'falta 1 sabor'");
        checar(Combos.Pendencia(def, null) == "Combo 10 Donuts: faltam 10 sabores",
            "combo sem escolha nenhuma (rascunho antigo) pende inteiro");

        var json = Combos.ParaJson(escolhas);
        var volta = Combos.DeJson(json);
        checar(volta is { Count: 2 } && volta[1].Qtd == 8 && volta[1].GrupoNome == "Donuts", "ParaJson/DeJson: ida e volta inteira");
        checar(Combos.DeJson("{quebrado") is null && Combos.DeJson(null) is null && Combos.ParaJson(null) is null,
            "JSON ilegivel ou nulo vira nulo, nunca excecao");

        var textos = new[]
        {
            Combos.Titulo(def), Combos.Resumo(escolhas), Combos.Pendencia(def, null)!,
            new Combos.Estado(def).Faltam!, new Combos.Estado(def).Progresso(def.Grupos[0]),
        }.Concat(Combos.LinhasKds(escolhas)).Concat(Combos.LinhasCupom(escolhas));
        checar(textos.All(t => !t.Contains('—') && !t.Contains('–')), "nenhum texto de tela com travessao ou meia-risca");
    }

    // ── 5. rascunho: o campo novo com default ──────────────────────────────
    private static void RascunhoAntigo(Action<bool, string> checar)
    {
        // exatamente o que um exe ANTERIOR gravou em comanda_rascunho.itens_json
        const string antigo = """
            [{"ProdutoId":"combo-10-donuts","Plu":"87","Nome":"COMBO 10 DONUTS","Categoria":"Combos","PrecoCent":9900,"QtdMilesimos":1000,"Unidade":"UN","Ncm":null,"Cest":null,"Csosn":"102","Origem":0,"Foto":null}]
            """;
        var itens = JsonSerializer.Deserialize<List<ItemRascunho>>(antigo);
        checar(itens is { Count: 1 } && itens[0].EscolhasJson is null,
            "rascunho de exe antigo (sem EscolhasJson) desserializa, com o campo nulo");
        var novo = new ItemRascunho(Combo, "87", "COMBO 10 DONUTS", "Combos", 9900, 1000, "UN", null, null, "102", 0, null,
            Combos.ParaJson(new[] { new Escolha(Ninho, "4", "DONUT NINHO", GrupoDonuts, 10, "Donuts") }));
        var volta = JsonSerializer.Deserialize<List<ItemRascunho>>(JsonSerializer.Serialize(new[] { novo }))!;
        checar(Combos.DeJson(volta[0].EscolhasJson) is { Count: 1 } esc && esc[0].Qtd == 10,
            "rascunho novo: as escolhas sobrevivem a ida e volta do disco");
    }

    // ── 6. a venda e a fila ────────────────────────────────────────────────
    private static async Task VendaEFilaAsync(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-combos-venda-{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir(arquivo);
            var op = new Operador("op-combo", "Bia", "operador");
            Operadores.Salvar(cx, op.Id, op.Nome, "4321", "operador");
            var sessao = Caixa.Abrir(cx, op, Dinheiro.DeReais(50));

            var escolhas = new List<Escolha>
            {
                new(Ovo, "5", "DONUT OVOMALTINE", GrupoDonuts, 2, "Donuts"),
                new(Ninho, "4", "DONUT NINHO", GrupoDonuts, 8, "Donuts"),
            };
            var preco = Dinheiro.DeReais(99);
            var itens = new[]
            {
                new LinhaVenda(Agua, "9", "AGUA 500ML", Quantidade.Um, Dinheiro.DeReais(6), Dinheiro.DeReais(6), "UN", "22011000", null, "102", null, 0),
                new LinhaVenda(Combo, "87", "COMBO 10 DONUTS", new Quantidade(2000), preco, new Dinheiro(preco.Centavos * 2), "UN", "19053100", null, "102", null, 0,
                    Escolhas: escolhas),
            };
            var total = new Dinheiro(600 + 9900 * 2);
            var composta = Vendas.Finalizar(cx, sessao, op, itens,
                new[] { new PagamentoVenda("dinheiro", total, Dinheiro.Zero) }, null, "Loja", null);

            var linha = cx.QueryFirst("SELECT seq, escolhas_json FROM venda_item WHERE venda_id=@v AND produto_id=@p",
                new { v = composta.Id, p = Combo });
            checar((long)linha.seq == 2 && Combos.DeJson(linha.escolhas_json as string) is { Count: 2 } grav && grav[1].Qtd == 8,
                "venda_item.escolhas_json guarda as escolhas da linha do combo (seq 2)");
            checar(cx.ExecuteScalar<string?>("SELECT escolhas_json FROM venda_item WHERE venda_id=@v AND produto_id=@p",
                       new { v = composta.Id, p = Agua }) is null,
                "item simples fica com escolhas_json nulo");
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM venda_item WHERE venda_id=@v", new { v = composta.Id }) == 2,
                "as escolhas NAO viram linhas da venda (2 linhas: agua e combo)");

            var fila = cx.QueryFirst("SELECT tipo, payload FROM outbox WHERE ref_id=@v", new { v = composta.Id });
            checar((string)fila.tipo == "venda_composta", "venda com combo entra na fila como 'venda_composta'");
            using (var doc = JsonDocument.Parse((string)fila.payload))
            {
                var r = doc.RootElement;
                checar(r.TryGetProperty("p_escolhas", out var pe) && pe.ValueKind == JsonValueKind.Array && pe.GetArrayLength() == 2,
                    "payload composto leva p_escolhas com uma linha por escolha");
                var e0 = pe[0];
                checar(e0.GetProperty("seq").GetInt32() == 2 && e0.GetProperty("combo_id").GetString() == Combo
                    && e0.GetProperty("produto_id").GetString() == Ovo && e0.GetProperty("quantidade").GetInt32() == 2
                    && e0.GetProperty("nome").GetString() == "DONUT OVOMALTINE" && e0.GetProperty("grupo_regra_id").GetString() == GrupoDonuts,
                    "p_escolhas[0] = {seq 2, combo_id, produto_id, quantidade POR UNIDADE, nome, grupo_regra_id}");
                var it = r.GetProperty("p_itens");
                checar(it.GetArrayLength() == 2 && it[1].GetProperty("qtd").GetDecimal() == 2m
                    && it[1].GetProperty("valor_unitario").GetDecimal() == 99m,
                    "p_itens continua com UMA linha para o combo, qtd 2 e o preco do combo");
                checar(it[1].TryGetProperty("escolhas", out var esc) && esc.GetArrayLength() == 2
                    && esc[1].GetProperty("qtd").GetInt32() == 8 && esc[1].GetProperty("plu").GetString() == "4",
                    "o item do combo leva a chave `escolhas` [{produto_id, plu, nome, qtd, grupo_regra_id}]");
                checar(!it[0].TryGetProperty("escolhas", out _), "o item simples NAO ganha a chave `escolhas`");
                checar(r.GetProperty("p_client_key").GetString() == composta.ClientKey
                    && r.TryGetProperty("p_business_date", out _) && r.TryGetProperty("p_operator_id", out _),
                    "os 14 parametros da venda de sempre continuam no corpo composto");
            }

            // venda SEM combo: nada muda, byte a byte
            var simples = Vendas.Finalizar(cx, sessao, op,
                new[] { itens[0] }, new[] { new PagamentoVenda("dinheiro", Dinheiro.DeReais(6), Dinheiro.Zero) }, null, "Loja", null);
            var filaSimples = cx.QueryFirst("SELECT tipo, payload FROM outbox WHERE ref_id=@v", new { v = simples.Id });
            checar((string)filaSimples.tipo == "venda", "venda sem combo continua 'venda'");
            checar(!((string)filaSimples.payload).Contains("p_escolhas") && !((string)filaSimples.payload).Contains("escolhas"),
                "payload da venda comum NAO leva p_escolhas nem `escolhas` (a RPC antiga casa pelos nomes dos parametros)");

            checar(Sincronizacao.VendasNaoEntregues().Total == 2,
                "o contador de vendas paradas conta a composta junto (antes de drenar: 2)");
            checar(Drenagem.TiposComHandler.Contains("venda_composta"), "'venda_composta' esta na lista de tipos com handler");

            // a fila, contra o Supabase de mentira: cada tipo na sua RPC
            using var fake = new FakePostgrest(4661);
            var nuvem = new Nuvem(fake.Url);
            checar(await nuvem.EntrarAsync("combo@teste.com", "x"), "nuvem fake autentica");
            using var dren = new Drenagem(nuvem, fake.Url);
            await dren.DrenarAsync();
            checar(fake.ChamadasPorRpc.GetValueOrDefault("pdv_registrar_venda_composta") == 1
                && fake.ChamadasPorRpc.GetValueOrDefault("pdv_registrar_venda") == 1,
                "a composta vai para rpc/pdv_registrar_venda_composta e a comum para rpc/pdv_registrar_venda");
            checar(fake.EscolhasRecebidas.TryGetValue(composta.ClientKey, out var recebidas) && recebidas.Contains("\"seq\":2")
                && recebidas.Contains(Ninho),
                "a nuvem recebeu p_escolhas com o seq da linha");
            checar(!fake.EscolhasRecebidas.ContainsKey(simples.ClientKey), "a venda comum nao manda escolha nenhuma");
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo IN ('venda','venda_composta') AND enviado_em IS NULL") == 0,
                "as duas linhas saem da fila com enviado_em");
            checar(Sincronizacao.VendasNaoEntregues().Total == 0, "depois de drenar, nenhuma venda parada");

            // reenvio (resposta perdida): a mesma client_key nao duplica as escolhas
            cx.Execute("UPDATE outbox SET enviado_em = NULL WHERE ref_id = @v", new { v = composta.Id });
            fake.EscolhasRecebidas.Clear();
            await dren.DrenarAsync();
            checar(fake.ChamadasPorRpc["pdv_registrar_venda_composta"] == 2 && fake.EscolhasRecebidas.Count == 0
                && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE ref_id=@v AND enviado_em IS NOT NULL", new { v = composta.Id }) == 1,
                "reenvio com a mesma client_key: a nuvem responde idempotente e nao grava escolhas de novo");

            // servidor SEM a RPC composta (exe publicado antes da migration do ERP): a venda
            // ESPERA na fila como transitorio, com o motivo legivel; nao vai para o cemiterio
            // (dinheiro ja recebido, cupom ja emitido). Quando a RPC aparece, sobe sozinha.
            fake.CompostaAusente = true;
            var composta2 = Vendas.Finalizar(cx, sessao, op, itens,
                new[] { new PagamentoVenda("dinheiro", total, Dinheiro.Zero) }, null, "Loja", null);
            for (var k = 0; k < 3; k++) await dren.DrenarAsync();
            var presa = cx.QueryFirst("SELECT enviado_em, desistido_em, tentativas, ultimo_erro FROM outbox WHERE ref_id=@v", new { v = composta2.Id });
            checar(presa.enviado_em is null && presa.desistido_em is null && (presa.tentativas is null || (long)presa.tentativas == 0)
                && ((string?)presa.ultimo_erro ?? "").Contains("pdv_registrar_venda_composta"),
                $"RPC composta ausente na nuvem (404 PGRST202): a venda espera na fila sem contar tentativa, com o motivo (viu: {presa.ultimo_erro})");
            fake.CompostaAusente = false;
            await dren.DrenarAsync();
            checar(cx.ExecuteScalar<string?>("SELECT enviado_em FROM outbox WHERE ref_id=@v", new { v = composta2.Id }) is not null
                && fake.EscolhasRecebidas.ContainsKey(composta2.ClientKey),
                "quando a RPC composta aparece, a venda presa sobe sozinha com as escolhas");

            // a cozinha ve o que foi montado
            var ticket = Kds.DoBalcao(composta.Id);
            var t = Kds.Abertos().First(x => x.Id == ticket);
            var comboKds = t.Itens.First(i => i.Descricao == "COMBO 10 DONUTS");
            checar(comboKds.Escolhas is { Count: 2 } && comboKds.Escolhas[0] == "Donuts: 4x Ovomaltine" && comboKds.Escolhas[1] == "Donuts: 16x Ninho",
                "Kds.DoBalcao: duas caixas viram 'Donuts: 4x Ovomaltine' e 'Donuts: 16x Ninho' no ticket");
            checar(t.Itens.First(i => i.Descricao == "AGUA 500ML").Escolhas is null, "item simples no ticket sem escolhas");
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }

    // ── 7. a nota: um det por linha ────────────────────────────────────────
    private static void Fiscal(Action<bool, string> checar)
    {
        var escolhas = new List<Escolha> { new(Ovo, "5", "DONUT OVOMALTINE", GrupoDonuts, 10, "Donuts") };
        var itens = new[]
        {
            new LinhaVenda(Combo, "87", "COMBO 10 DONUTS", Quantidade.Um, Dinheiro.DeReais(99), Dinheiro.DeReais(99), "UN", "19053100", null, "102", null, 0,
                Escolhas: escolhas),
            new LinhaVenda(Agua, "9", "AGUA 500ML", Quantidade.Um, Dinheiro.DeReais(6), Dinheiro.DeReais(6), "UN", "22011000", null, "102", null, 0),
        };
        foreach (var vdesc in new[] { true, false })
        {
            var dets = ItemFiscal.DaVenda(itens, vdesc);
            checar(dets.Count == 2 && dets[0].Codigo == "87" && dets[0].VUnit == 99m && dets[0].Ncm == "19053100",
                $"Fiscal.DaVenda (vdescPorItem={vdesc}): UM det por linha, o combo com o preco e o NCM do combo (as escolhas nao viram det)");
        }
        checar(itens[0].TemEscolhas && !itens[1].TemEscolhas
            && !new LinhaVenda(Combo, "87", "X", Quantidade.Um, Dinheiro.Zero, Dinheiro.Zero, "UN", null, null, null, null, 0, Escolhas: new List<Escolha>()).TemEscolhas,
            "LinhaVenda.TemEscolhas: lista vazia nao conta como combo");
        var cupom = new Pdv.ItemCupom("87", "COMBO 10 DONUTS", Quantidade.Um, "UN", Dinheiro.DeReais(99), Dinheiro.DeReais(99),
            Escolhas: Combos.LinhasCupom(escolhas));
        checar(Pdv.Impressao.SubLinhasEscolhas(cupom).SequenceEqual(new[] { "     10x Donut Ovomaltine" })
            && cupom.Total == Dinheiro.DeReais(99),
            "cupom: sub-linha '     10x Donut Ovomaltine' sem valor, total da linha intacto");
        checar(Pdv.Impressao.SubLinhasEscolhas(new Pdv.ItemCupom("9", "AGUA", Quantidade.Um, "UN", Dinheiro.DeReais(6), Dinheiro.DeReais(6))).Count == 0,
            "cupom: item simples sem sub-linha");
    }

    // ── 8. descida: pdv_combos_ativos -> tabela combo -> impressao digital ──
    private static async Task DescidaAsync(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-combos-desc-{Guid.NewGuid():N}.db");
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir(arquivo);
            using var fake = new FakePostgrest(4662) { CombosAtivos = "[" + PayloadCombo() + "]" };
            var nuvem = new Nuvem(fake.Url);
            checar(await nuvem.EntrarAsync("combo@teste.com", "x"), "nuvem fake autentica (descida)");
            var antes = Sincronizacao.ImpressaoDigital(cx);
            var n = await nuvem.BaixarCombosAsync(cx, "American Day Savassi");
            checar(n == 1 && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM combo") == 1
                && Combos.Carregar(cx).TryGetValue(Combo, out var def) && def.Grupos[0].Max == 10,
                "BaixarCombosAsync: a RPC pdv_combos_ativos vira a tabela `combo` (1 combo, parseavel)");
            checar(Sincronizacao.ImpressaoDigital(cx) != antes,
                "a impressao digital do catalogo muda com o combo (publicar combo no painel nao diz 'tudo em dia')");
            fake.CombosAtivos = "{}";
            checar(await nuvem.BaixarCombosAsync(cx, "x") == -1 && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM combo") == 1,
                "resposta que nao e lista: -1 e o espelho anterior fica");
            fake.CombosAtivos = "[]";
            checar(await nuvem.BaixarCombosAsync(cx, "x") == 0 && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM combo") == 0,
                "loja sem combo: lista vazia limpa o espelho");
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(arquivo); } catch { } }
    }

    // ── 9. a tela ──────────────────────────────────────────────────────────
    private static void Tela(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-combos-tela-{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        Exception? erro = null;
        try
        {
            Banco.Migrar(arquivo);
            SemearTela(arquivo);
            // no Application compartilhado da bateria (HostWpf): o WPF so deixa criar um
            try { HostWpf.Executar(() => Passos(checar)); }
            catch (Exception ex) { erro = ex; }
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
        checar(erro is null, "tela: dialogo e comanda do combo subiram e os passos rodaram (" + (erro?.ToString() ?? "ok") + ")");
    }

    private static void SemearTela(string arquivo)
    {
        using var cx = Banco.Abrir(arquivo);
        var agora = DateTime.Now.ToString("o");
        cx.Execute("""
            INSERT INTO terminal (id, terminal_uuid, loja_id, loja_nome, cnpj, serie_nfce, ambiente, api_base, criado_em)
            VALUES (1, 'term-teste', 'loja-1', 'Loja Teste', '00000000000000', 1, 2, 'http://127.0.0.1:9', @a)
            """, new { a = agora });
        cx.Execute("INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,atualizado) VALUES ('op-ui','Tela','x','y','operador',@a)", new { a = agora });
        cx.Execute("""
            INSERT INTO caixa_sessao (id,business_date,operador_id,operador_nome,abertura_em,fundo_troco_cent)
            VALUES ('sessao-ui',@d,'op-ui','Tela',@a,0)
            """, new { d = Caixa.DiaOperacional(), a = agora });
        foreach (var p in Catalogo)
            cx.Execute("""
                INSERT INTO produto (id, plu, nome, categoria, preco_cent, unidade, ativo, atualizado, csosn)
                VALUES (@Id, @Plu, @Nome, @Categoria, @Preco, 'UN', 1, @a, '102')
                """, new { p.Id, p.Plu, p.Nome, p.Categoria, Preco = p.Id == Combo ? 9900 : 1290, a = agora });
        cx.Execute("INSERT INTO combo (produto_id, payload) VALUES (@i,@p)", new { i = Combo, p = PayloadCombo() });
    }

    private static void Passos(Action<bool, string> checar)
    {
        var host = new Window
        {
            Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0,
        };
        host.Show();
        var def = Combos.Parsear(PayloadCombo())!;

        // ── (1) o dialogo sozinho: minimo, maximo, tudo igual, voltar ───────
        string? faltamInicial = null; bool adicionarDesligado = false, maisDesligadoNoMax = false, adicionarLigadoNoMax = false;
        string? progressoNoMax = null; string? titulo = null;
        QuandoAbrir(host, d =>
        {
            titulo = Descendentes<TextBlock>(d).First(t => AutomationProperties.GetName(t) == "TituloCombo").Text;
            faltamInicial = Descendentes<TextBlock>(d).First(t => AutomationProperties.GetName(t) == "Faltam").Text;
            adicionarDesligado = !Botao(d, "Adicionar").IsEnabled;
            Clicar(Botao(d, "DONUT OVOMALTINE"));           // toque no card = +1
            Clicar(Botao(d, "Mais DONUT OVOMALTINE"));      // + no card
            Clicar(Botao(d, "Menos DONUT OVOMALTINE"));     // - no card
            Clicar(Botao(d, "Tudo igual Donuts"));          // 1 marcado -> completa ate 10
            progressoNoMax = Descendentes<TextBlock>(d).First(t => AutomationProperties.GetName(t) == "Progresso Donuts").Text;
            maisDesligadoNoMax = !Botao(d, "Mais DONUT NINHO").IsEnabled && !Botao(d, "DONUT NINHO").IsEnabled;
            adicionarLigadoNoMax = Botao(d, "Adicionar").IsEnabled;
            Clicar(Botao(d, "Adicionar"));
        });
        var escolhido = Pdv.Telas.DialogoCombo.Abrir(host, def, Catalogo);
        checar(titulo == "Combo 10 Donuts", "dialogo: o titulo e 'Combo 10 Donuts'");
        checar(faltamInicial == "Faltam 10 donuts" && adicionarDesligado, "dialogo vazio: 'Faltam 10 donuts' e Adicionar desligado");
        checar(progressoNoMax == "Donuts · 10 de 10", "cabecalho do grupo no maximo: 'Donuts · 10 de 10'");
        checar(maisDesligadoNoMax, "no maximo, o + e o card dos outros sabores desligam");
        checar(adicionarLigadoNoMax, "no minimo o Adicionar liga");
        checar(escolhido is { Count: 1 } && escolhido[0].ProdutoId == Ovo && escolhido[0].Qtd == 10 && escolhido[0].GrupoId == GrupoDonuts,
            "Adicionar devolve as escolhas: 10x Ovomaltine, com o grupo_regra_id");

        // cards: servidor ∪ local (o Churros so local aparece) e nao o proprio combo
        List<string> cards = new();
        QuandoAbrir(host, d =>
        {
            cards = Descendentes<Button>(d).Select(AutomationProperties.GetName)
                .Where(n => n is not null && !n.StartsWith("Mais ") && !n.StartsWith("Menos ") && !n.StartsWith("Tudo igual") && n is not ("Adicionar" or "Voltar"))
                .ToList()!;
            Clicar(Botao(d, "Voltar"));
        });
        var cancelado = Pdv.Telas.DialogoCombo.Abrir(host, def, Catalogo);
        checar(cancelado is null, "Voltar devolve null (nada entra na comanda)");
        checar(cards.SequenceEqual(new[] { "DONUT CHURROS", "DONUT NINHO", "DONUT OVOMALTINE" }),
            $"os cards sao a fonte resolvida, em ordem (veio: {string.Join(", ", cards)})");

        // reabertura pre-preenchida
        string? progressoPre = null;
        QuandoAbrir(host, d =>
        {
            progressoPre = Descendentes<TextBlock>(d).First(t => AutomationProperties.GetName(t) == "Progresso Donuts").Text;
            Clicar(Botao(d, "Voltar"));
        });
        Pdv.Telas.DialogoCombo.Abrir(host, def, Catalogo, new[] { new Escolha(Ovo, "5", "DONUT OVOMALTINE", GrupoDonuts, 7, "Donuts") });
        checar(progressoPre == "Donuts · 7 de 10", "reabrir com 7 marcados mostra 'Donuts · 7 de 10'");

        // republicacao: escolha que nenhum grupo aceita aparece como "Fora do combo",
        // com o botao Tirar; Adicionar so liga depois que o operador tira ou troca
        string? foraTexto = null, faltamFora = null, faltamDepois = null; bool adicionarComFora = true, adicionarSemFora = false;
        QuandoAbrir(host, d =>
        {
            foraTexto = Descendentes<TextBlock>(d).FirstOrDefault(t => AutomationProperties.GetName(t) == "ForaDoCombo AGUA 500ML")?.Text;
            faltamFora = Descendentes<TextBlock>(d).First(t => AutomationProperties.GetName(t) == "Faltam").Text;
            adicionarComFora = Botao(d, "Adicionar").IsEnabled;
            Clicar(Botao(d, "Tirar AGUA 500ML"));
            faltamDepois = Descendentes<TextBlock>(d).First(t => AutomationProperties.GetName(t) == "Faltam").Text;
            adicionarSemFora = Botao(d, "Adicionar").IsEnabled;
            Clicar(Botao(d, "Adicionar"));
        });
        var realocado = Pdv.Telas.DialogoCombo.Abrir(host, def, Catalogo, new[]
        {
            new Escolha(Ovo, "5", "DONUT OVOMALTINE", "regra-velha", 10, "Donuts"),
            new Escolha(Agua, "9", "AGUA 500ML", "regra-velha", 1, "Donuts"),
        });
        checar(foraTexto == "1x Agua 500ml", $"dialogo: a escolha que saiu da fonte aparece em 'Fora do combo' como '1x Agua 500ml' (veio '{foraTexto}')");
        checar(faltamFora == "1 fora do combo" && !adicionarComFora, $"dialogo: rodape '1 fora do combo' e Adicionar desligado (veio '{faltamFora}')");
        checar(faltamDepois == "" && adicionarSemFora, "dialogo: Tirar limpa o rodape e liga o Adicionar");
        checar(realocado is { Count: 1 } && realocado[0].ProdutoId == Ovo && realocado[0].Qtd == 10 && realocado[0].GrupoId == GrupoDonuts,
            "dialogo: Adicionar devolve os 10 Ovomaltine ja com o id NOVO do grupo");

        // ── (2) a tela de venda ───────────────────────────────────────────
        var op = new Operador("op-ui", "Tela", "operador");
        var sessao = new Sessao("sessao-ui", Caixa.DiaOperacional(), op.Id, op.Nome, DateTime.Now, Dinheiro.Zero);
        var tela = new Pdv.Telas.Venda(op, sessao);
        host.Content = tela;
        host.UpdateLayout();
        var catalogo = Campo<System.Collections.IList>(tela, "_catalogo").Cast<Pdv.Telas.Produto>().ToList();
        var produtoCombo = catalogo.First(p => p.Id == Combo);
        var comanda = Campo<List<Pdv.Telas.ItemComanda>>(tela, "_comanda");
        Definir(tela, "_rascunhoOferecido", true);   // sem isto a tela nao grava rascunho (ver Venda.SalvarRascunho)

        // toque no combo: dialogo, 10 Ninho, Adicionar
        QuandoAbrir(host, d => { Clicar(Botao(d, "DONUT NINHO")); Clicar(Botao(d, "Tudo igual Donuts")); Clicar(Botao(d, "Adicionar")); });
        Invocar(tela, "Adicionar", produtoCombo);
        checar(comanda.Count == 1 && comanda[0].EhCombo && comanda[0].Escolhas!.Sum(e => e.Qtd) == 10 && comanda[0].Escolhas![0].ProdutoId == Ninho,
            "tocar no combo abre o dialogo e a linha entra com as 10 escolhas");
        checar(SubLinha(tela, produtoCombo.Nome) == "10x Ninho", $"a comanda mostra a sub-linha '10x Ninho' (veio '{SubLinha(tela, produtoCombo.Nome)}')");

        // segundo toque: NOVA linha (sabores diferentes), nao incremento
        QuandoAbrir(host, d => { Clicar(Botao(d, "DONUT OVOMALTINE")); Clicar(Botao(d, "Tudo igual Donuts")); Clicar(Botao(d, "Adicionar")); });
        Invocar(tela, "Adicionar", produtoCombo);
        checar(comanda.Count == 2 && comanda[0].Escolhas![0].ProdutoId == Ovo && comanda[1].Escolhas![0].ProdutoId == Ninho,
            "segundo toque no combo cria OUTRA linha (a nova no topo), nao incrementa a primeira");

        // cancelar no dialogo: nada entra
        QuandoAbrir(host, d => Clicar(Botao(d, "Voltar")));
        Invocar(tela, "Adicionar", produtoCombo);
        checar(comanda.Count == 2, "Voltar no dialogo nao poe linha na comanda");

        // "+" da linha duplica a caixa (mesmas escolhas, qtd 2)
        var escolhasAntes = comanda[0].Escolhas!.ToList();
        Clicar(BotaoDaComanda(tela, "Aumentar " + produtoCombo.Nome));
        checar(comanda[0].Qtd.Milesimos == 2000 && comanda[0].Escolhas!.SequenceEqual(escolhasAntes),
            "o + da linha do combo duplica a caixa: qtd 2 com as MESMAS escolhas");

        // produto simples continua incrementando no segundo toque
        var agua = catalogo.First(p => p.Id == Agua);
        Invocar(tela, "Adicionar", agua); Invocar(tela, "Adicionar", agua);
        checar(comanda.Count == 3 && comanda[0].Produto.Id == Agua && comanda[0].Qtd.Milesimos == 2000 && !comanda[0].EhCombo,
            "controle: produto simples continua incrementando no segundo toque");

        // rascunho: as escolhas encostam no disco e voltam
        Invocar(tela, "PintarComanda");
        using (var cx = Banco.Abrir())
        {
            var r = Rascunho.Ler(cx, sessao.Id);
            var doCombo = r?.Itens.Where(i => i.ProdutoId == Combo).ToList();
            checar(doCombo is { Count: 2 } && doCombo.All(i => Combos.DeJson(i.EscolhasJson) is { Count: 1 }),
                "o rascunho da comanda guarda as escolhas de cada linha de combo");
        }

        // toque na sub-linha reabre pre-preenchido; Adicionar troca as escolhas da linha
        string? progressoReaberto = null;
        QuandoAbrir(host, d =>
        {
            progressoReaberto = Descendentes<TextBlock>(d).First(t => AutomationProperties.GetName(t) == "Progresso Donuts").Text;
            Clicar(Botao(d, "Menos DONUT OVOMALTINE"));
            Clicar(Botao(d, "DONUT CHURROS"));
            Clicar(Botao(d, "Adicionar"));
        });
        Clicar(BotaoDaComanda(tela, "Sabores " + produtoCombo.Nome));
        var editada = comanda.First(i => i.EhCombo && i.Qtd.Milesimos == 2000);
        checar(progressoReaberto == "Donuts · 10 de 10" && editada.Escolhas!.Count == 2
            && editada.Escolhas.Any(e => e.ProdutoId == Churros && e.Qtd == 1) && editada.Escolhas.Sum(e => e.Qtd) == 10,
            "toque na sub-linha reabre com os 10 marcados; trocar um sabor atualiza a linha");

        // Finalizar recusa combo incompleto, com a frase de uma linha
        editada.Escolhas = new List<Escolha> { new(Ovo, "5", "DONUT OVOMALTINE", GrupoDonuts, 7, "Donuts") };
        Invocar(tela, "PintarComanda");
        string? aviso = null;
        QuandoAbrir(host, d => { aviso = string.Join(" | ", Descendentes<TextBlock>(d).Select(t => t.Text)); Clicar(Botao(d, "Entendi")); });
        Invocar(tela, "Finalizar", null, new RoutedEventArgs());
        var painel = Campo<ContentControl>(tela, "PainelPagamento");
        checar(aviso is not null && aviso.Contains("Combo 10 Donuts: faltam 3 sabores") && painel.Visibility != Visibility.Visible,
            $"Finalizar recusa combo incompleto: 'Combo 10 Donuts: faltam 3 sabores' e nao abre o pagamento (aviso: {aviso})");

        // republicacao: id de grupo velho NAO derruba a comanda (realocado pela fonte);
        // um produto fora da fonte e recusado com o motivo, nao com "faltam N"
        editada.Escolhas = new List<Escolha> { new(Ovo, "5", "DONUT OVOMALTINE", "regra-velha", 8, "Donuts"), new(Churros, "6", "DONUT CHURROS", "regra-velha", 2, "Donuts") };
        Invocar(tela, "PintarComanda");
        checar(PendenciaNaTela(tela, editada) is null, "Finalizar: escolhas com id de grupo velho, todas da fonte (Churros pelo catalogo local), nao sao pendencia");
        editada.Escolhas = new List<Escolha> { new(Ovo, "5", "DONUT OVOMALTINE", "regra-velha", 10, "Donuts"), new(Agua, "9", "AGUA 500ML", "regra-velha", 1, "Donuts") };
        Invocar(tela, "PintarComanda");
        aviso = null;
        QuandoAbrir(host, d => { aviso = string.Join(" | ", Descendentes<TextBlock>(d).Select(t => t.Text)); Clicar(Botao(d, "Entendi")); });
        Invocar(tela, "Finalizar", null, new RoutedEventArgs());
        checar(aviso is not null && aviso.Contains("Combo 10 Donuts: 1 fora do combo") && painel.Visibility != Visibility.Visible,
            $"Finalizar recusa com o motivo: 'Combo 10 Donuts: 1 fora do combo' (aviso: {aviso})");

        // combo restaurado de rascunho antigo (sem escolhas): a linha pede os sabores
        comanda.Clear();
        comanda.Add(new Pdv.Telas.ItemComanda { Produto = produtoCombo });
        Invocar(tela, "PintarComanda");
        checar(SubLinha(tela, produtoCombo.Nome) == "Toque para escolher os sabores",
            "combo sem escolhas (rascunho de exe antigo) mostra 'Toque para escolher os sabores'");
        QuandoAbrir(host, d => { aviso = string.Join(" | ", Descendentes<TextBlock>(d).Select(t => t.Text)); Clicar(Botao(d, "Entendi")); });
        Invocar(tela, "Finalizar", null, new RoutedEventArgs());
        checar(aviso is not null && aviso.Contains("faltam 10 sabores"), "e o Finalizar recusa: 'faltam 10 sabores'");

        // o pagamento monta o cupom com as sub-linhas, sem mexer no total
        var linhas = new List<LinhaVenda>
        {
            new(Combo, "87", "COMBO 10 DONUTS", Quantidade.Um, Dinheiro.DeReais(99), Dinheiro.DeReais(99), "UN", null, null, null, null, 0,
                Escolhas: new List<Escolha> { new(Ovo, "5", "DONUT OVOMALTINE", GrupoDonuts, 10, "Donuts") }),
        };
        var pg = new Pdv.Telas.Pagamento(op, sessao, linhas, new EmissorMudo(), null, "Loja", null);
        var cupom = (List<Pdv.ItemCupom>)typeof(Pdv.Telas.Pagamento).GetMethods(P).First(m => m.Name == "ItensDoCupom")
            .Invoke(pg, new object[] { true })!;
        checar(cupom.Count == 1 && cupom[0].Escolhas is { Count: 1 } && cupom[0].Escolhas[0] == "10x Donut Ovomaltine"
            && cupom[0].Total == Dinheiro.DeReais(99),
            "cupom: a linha do combo leva '10x Donut Ovomaltine' como sub-linha e o total continua 99,00");

        host.Content = null;
        host.Close();
    }

    // ── ajudantes ──────────────────────────────────────────────────────────
    /// <summary>Botao pelo nome de automacao (DialogoCombo, comanda) ou pelo Content textual (Dialogo.Avisar).</summary>
    private static Button Botao(DependencyObject raiz, string nome)
        => Descendentes<Button>(raiz).First(b => AutomationProperties.GetName(b) == nome || b.Content as string == nome);

    private static Button BotaoDaComanda(Pdv.Telas.Venda tela, string nome)
        => Botao(Campo<ItemsControl>(tela, "ListaComanda"), nome);

    /// <summary>O texto da sub-linha dos sabores da primeira linha daquele produto na comanda.</summary>
    private static string? SubLinha(Pdv.Telas.Venda tela, string nomeProduto)
    {
        var b = Descendentes<Button>(Campo<ItemsControl>(tela, "ListaComanda"))
            .FirstOrDefault(x => AutomationProperties.GetName(x) == "Sabores " + nomeProduto);
        return (b?.Content as TextBlock)?.Text;
    }

    /// <summary>O que o Finalizar da tela pergunta ao nucleo para esta linha (combo + escolhas + catalogo local).</summary>
    private static string? PendenciaNaTela(Pdv.Telas.Venda tela, Pdv.Telas.ItemComanda item)
    {
        var combos = Campo<Dictionary<string, Combos.ComboDef>>(tela, "_combos");
        var catalogo = (List<Combos.ProdutoLocal>)typeof(Pdv.Telas.Venda).GetMethod("CatalogoLocal", P)!.Invoke(tela, null)!;
        return Combos.Pendencia(combos[item.Produto.Id], item.Escolhas, catalogo);
    }

    private static T Campo<T>(object alvo, string nome)
        => (T)alvo.GetType().GetField(nome, P)!.GetValue(alvo)!;

    private static void Definir(object alvo, string nome, object valor)
        => alvo.GetType().GetField(nome, P)!.SetValue(alvo, valor);

    private static void Invocar(object alvo, string metodo, params object?[] args)
        => alvo.GetType().GetMethods(P).First(m => m.Name == metodo && m.GetParameters().Length == args.Length)
            .Invoke(alvo, args);

    private static void Clicar(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    /// <summary>Espera o proximo dialogo modal abrir sobre o host e age nele (padrao de TestesPos).</summary>
    private static void QuandoAbrir(Window host, Action<Window> acao)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        var tentativas = 0;
        timer.Tick += (_, _) =>
        {
            var d = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w != host && w.Owner == host && w.IsVisible);
            if (d is null)
            {
                if (++tentativas > 50) timer.Stop();
                return;
            }
            timer.Stop();
            acao(d);
        };
        timer.Start();
    }

    private static IEnumerable<T> Descendentes<T>(DependencyObject raiz) where T : DependencyObject
    {
        foreach (var filho in LogicalTreeHelper.GetChildren(raiz))
        {
            if (filho is not DependencyObject d) continue;
            if (d is T t) yield return t;
            foreach (var neto in Descendentes<T>(d)) yield return neto;
        }
    }

    private sealed class EmissorMudo : IEmissorFiscal
    {
        public Task<SaudeEmissor> SondarAsync(CancellationToken ct)
            => Task.FromResult(new SaudeEmissor(false, null, null, null, 0, null, "mudo"));
        public Task<ResultadoEmissao> EmitirAsync(IReadOnlyList<ItemFiscal> itens,
            IReadOnlyList<PagamentoFiscal> pagamentos, string? documento, CancellationToken ct)
            => Task.FromResult(ResultadoEmissao.ForaDoAr("teste", "mudo"));
    }
}
