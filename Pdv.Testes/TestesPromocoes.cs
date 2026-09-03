using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// Motor de promoções — relógio SEMPRE cravado (a promoção de quinta é o bug
/// que motivou tudo: publicada no painel, invisível no caixa).
/// O primeiro teste usa o payload REAL de produção, medido em 20/08/2026.
/// </summary>
public static class TestesPromocoes
{
    public static void Rodar(Action<bool, string> checar)
    {
        // quinta-feira 20/08/2026, 15h (ISO: quinta = 4)
        var quinta = new DateTime(2026, 8, 20, 15, 0, 0);
        var quarta = new DateTime(2026, 8, 19, 15, 0, 0);
        const string donut = "79af65dd-425f-4994-b30e-383cad3fa6f5";

        // ── o payload VERBATIM do banco (com os nulls que derrubaram o parser) ──
        // "valor_desconto_cent": null fazia TryGetInt64 LANÇAR e a promoção era
        // descartada em silêncio — a aba PROMOÇÃO não nascia com dado real.
        var verbatim = Promocoes.Parsear("""
            {"id": "dd658ac8-a3a5-437e-89dd-7c05e98a30ca", "fim": "2026-09-30", "alvo": "produtos", "leve": null, "nome": "donuts do dia", "tipo": "percentual", "ativa": true, "combo": null, "lojas": ["American Day Savassi"], "pague": null, "store": "American Day Savassi", "config": null, "inicio": "2026-08-06", "hora_fim": null, "categorias": null, "percentual": null, "dias_semana": null, "hora_inicio": null, "produto_ids": ["79af65dd-425f-4994-b30e-383cad3fa6f5"], "regras_semana": [{"dias": [4], "precos_cent": {"79af65dd-425f-4994-b30e-383cad3fa6f5": 1450}, "produto_ids": ["79af65dd-425f-4994-b30e-383cad3fa6f5"]}], "valor_desconto_cent": null}
            """);
        checar(verbatim is not null,
            "o payload VERBATIM do banco da loja (com nulls) parseia");
        checar(verbatim is not null && verbatim.Regras.Count == 1,
            "as regras_semana do payload verbatim sobrevivem");

        // ── o payload REAL do "donuts do dia" (producao, 20/08/2026) ────────
        var real = Promocoes.Parsear("""
            {"id":"dd658ac8","nome":"donuts do dia","tipo":"percentual","percentual":null,
             "alvo":"produtos","produto_ids":["79af65dd-425f-4994-b30e-383cad3fa6f5"],
             "inicio":"2026-08-06","fim":"2026-09-30","dias_semana":null,
             "regras_semana":[{"dias":[4],
               "precos_cent":{"79af65dd-425f-4994-b30e-383cad3fa6f5":1450},
               "produto_ids":["79af65dd-425f-4994-b30e-383cad3fa6f5"]}]}
            """);
        checar(real is not null, "o payload REAL de producao parseia");
        var promos = new[] { real! };

        var (cent, nome) = Promocoes.PrecoEfetivoCent(promos, donut, "Donuts", 2190, quinta);
        checar(cent == 1450 && nome == "donuts do dia",
            $"QUINTA: ninho com nutella sai por 14,50 (mediu {cent})");
        (cent, _) = Promocoes.PrecoEfetivoCent(promos, donut, "Donuts", 2190, quarta);
        checar(cent == 2190, "QUARTA: preco cheio — a regra e so de quinta");
        (cent, _) = Promocoes.PrecoEfetivoCent(promos, "outro-produto", "Donuts", 2190, quinta);
        checar(cent == 2190, "outro produto nao pega o preco do dia");
        (cent, _) = Promocoes.PrecoEfetivoCent(promos, donut, "Donuts", 2190,
            new DateTime(2026, 10, 1, 15, 0, 0));
        checar(cent == 2190, "depois do fim da vigencia o preco volta ao cheio");

        // ── ISO 8601: domingo e 7, nunca 0 ──────────────────────────────────
        checar(Promocoes.DiaIso(new DateTime(2026, 8, 23)) == 7, "domingo = 7 (ISO), nao 0");
        checar(Promocoes.DiaIso(new DateTime(2026, 8, 17)) == 1, "segunda = 1 (ISO)");

        // ── percentual simples com dias_semana de cima ──────────────────────
        var pct = Promocoes.Parsear("""
            {"id":"p1","nome":"quarta 10","tipo":"percentual","percentual":10,
             "alvo":"produtos","produto_ids":["X"],"dias_semana":[3]}
            """)!;
        (cent, _) = Promocoes.PrecoEfetivoCent(new[] { pct }, "X", "c", 1000, quarta);
        checar(cent == 900, "10% na quarta aplica (1000 -> 900)");
        (cent, _) = Promocoes.PrecoEfetivoCent(new[] { pct }, "X", "c", 1000, quinta);
        checar(cent == 1000, "10% da quarta NAO vale na quinta");

        // ── valor fixo + piso zero ──────────────────────────────────────────
        var val = Promocoes.Parsear("""
            {"id":"v1","nome":"5 off","tipo":"valor","valor_desconto_cent":500,"alvo":"todos"}
            """)!;
        (cent, _) = Promocoes.PrecoEfetivoCent(new[] { val }, "Y", "c", 1200, quinta);
        checar(cent == 700, "valor fixo desconta em centavos (1200 -> 700)");
        (cent, _) = Promocoes.PrecoEfetivoCent(new[] { val }, "Y", "c", 300, quinta);
        checar(cent == 0, "desconto maior que o preco NAO fica negativo");

        // ── janela de horario (config.janelas) ──────────────────────────────
        var happy = Promocoes.Parsear("""
            {"id":"h1","nome":"happy","tipo":"percentual","percentual":50,"alvo":"todos",
             "config":{"janelas":[{"das":"18:00","ate":"20:00"}]}}
            """)!;
        (cent, _) = Promocoes.PrecoEfetivoCent(new[] { happy }, "Z", "c", 1000,
            new DateTime(2026, 8, 20, 19, 0, 0));
        checar(cent == 500, "dentro da janela 18-20h aplica");
        (cent, _) = Promocoes.PrecoEfetivoCent(new[] { happy }, "Z", "c", 1000,
            new DateTime(2026, 8, 20, 21, 0, 0));
        checar(cent == 1000, "fora da janela nao aplica");

        // ── entre duas promocoes vale a MELHOR pro cliente ──────────────────
        (cent, nome) = Promocoes.PrecoEfetivoCent(new[] { pct, val }, "X", "c", 1000, quarta);
        checar(cent == 500 && nome == "5 off",
            "concorrencia: vale o menor preco (10% da 900, valor da 500)");

        // ── tipos de carrinho nao mexem no preco unitario ───────────────────
        var lxpy = Promocoes.Parsear("""
            {"id":"l1","nome":"duo","tipo":"leve_x_pague_y","leve":2,"pague":1,
             "alvo":"produtos","produto_ids":["X"]}
            """)!;
        (cent, _) = Promocoes.PrecoEfetivoCent(new[] { lxpy }, "X", "c", 1000, quinta);
        checar(cent == 1000, "leve 2 pague 1 NAO altera preco unitario (e regra de carrinho)");

        // ── lixo nao derruba o caixa ────────────────────────────────────────
        checar(Promocoes.Parsear("{nao-e-json") is null, "JSON quebrado vira null, nao excecao");
        checar(Promocoes.Parsear("""{"id":"x"}""") is not null,
            "promocao quase vazia parseia (e nunca aplica nada)");
        (cent, _) = Promocoes.PrecoEfetivoCent(
            new[] { Promocoes.Parsear("""{"id":"x"}""")! }, "X", "c", 1000, quinta);
        checar(cent == 1000, "promocao sem tipo conhecido devolve o preco base");

        // ── vitrine da categoria PROMOÇÃO ───────────────────────────────────
        var vitrine = Promocoes.ProdutosEmPromocao(promos, quinta);
        checar(vitrine.TryGetValue(donut, out var vq) && vq.AtivaAgora,
            "vitrine: donuts do dia ATIVO na quinta");
        vitrine = Promocoes.ProdutosEmPromocao(promos, quarta);
        checar(vitrine.TryGetValue(donut, out var vqa) && !vqa.AtivaAgora,
            "vitrine: na quarta aparece CINZA (fora do dia)");
        checar(vqa!.Quando.Contains("qui"),
            $"o card cinza explica QUANDO vale (mediu '{vqa.Quando}')");
        vitrine = Promocoes.ProdutosEmPromocao(promos, new DateTime(2026, 10, 2, 12, 0, 0));
        checar(!vitrine.ContainsKey(donut),
            "fora da vigencia a promocao SOME da vitrine (nem cinza)");

        // leve_x_pague_y tambem aparece na vitrine (produto citado por id)
        vitrine = Promocoes.ProdutosEmPromocao(new[] { lxpy }, quinta);
        checar(vitrine.ContainsKey("X"), "produto do leve-2-pague-1 aparece na vitrine");

        // janela de hora controla o cinza
        vitrine = Promocoes.ProdutosEmPromocao(new[] { happy }, new DateTime(2026, 8, 20, 19, 0, 0));
        checar(vitrine.Count == 0, "promo de alvo 'todos' nao lista produto (viraria ruido)");

        // ── 03/09/2026 (Savassi): "donuts do dia" com um produto POR DIA ────────
        // A vitrine do caixa pintava a seção inteira com a regra do PRIMEIRO
        // produto do grupo: na quinta, brigadeiro (quarta) puxava "só vale qua"
        // sobre o ovomaltine (quinta). O motor sempre soube a verdade POR
        // PRODUTO; este teste a fixa para a tela não voltar a ignorá-la.
        const string brig = "BRIG", ovo = "OVO";
        var porDia = Promocoes.Parsear("""
            {"id": "pd", "fim": "2026-12-31", "alvo": "produtos", "nome": "donuts do dia", "tipo": "percentual", "ativa": true,
             "lojas": ["American Day Savassi"], "inicio": "2026-08-06", "hora_fim": null, "hora_inicio": null,
             "categorias": null, "percentual": null, "dias_semana": null, "produto_ids": ["BRIG", "OVO"],
             "regras_semana": [
               {"dias": [3], "precos_cent": {"BRIG": 1450}, "produto_ids": ["BRIG"]},
               {"dias": [4], "precos_cent": {"OVO": 1450},  "produto_ids": ["OVO"]}
             ], "valor_desconto_cent": null}
            """)!;
        vitrine = Promocoes.ProdutosEmPromocao(new[] { porDia }, quinta);
        checar(vitrine.TryGetValue(ovo, out var vOvo) && vOvo.AtivaAgora,
            "quinta: o produto da regra de quinta (ovomaltine) vale AGORA");
        checar(vitrine.TryGetValue(brig, out var vBrig) && !vBrig.AtivaAgora,
            "quinta: o produto da regra de quarta (brigadeiro) NAO vale agora");
        checar(vOvo?.Quando == "qui" && vBrig?.Quando == "qua",
            "cada produto descreve o SEU dia (qui / qua), nao o do primeiro do grupo");
        checar(Promocoes.PrecoEfetivoCent(new[] { porDia }, ovo, "Donuts", 1800, quinta).Cent == 1450
            && Promocoes.PrecoEfetivoCent(new[] { porDia }, brig, "Donuts", 1800, quinta).Cent == 1800,
            "quinta: ovomaltine sai a 14,50 e brigadeiro fica no preco cheio");
        vitrine = Promocoes.ProdutosEmPromocao(new[] { porDia }, quarta);
        checar(vitrine[brig].AtivaAgora && !vitrine[ovo].AtivaAgora,
            "quarta: inverte (brigadeiro vale, ovomaltine nao)");

        // ══════════════════════════════════════════════════════════════════
        // 03/09/2026: promoções de CARRINHO no caixa (leve X pague Y, combo,
        // compre e ganhe) e a regra "UMA promoção por pedido". Antes, o caixa
        // só aplicava promoção de preço: o painel criava as três de carrinho e
        // o cliente pagava preço cheio, em silêncio.
        // ══════════════════════════════════════════════════════════════════
        var sexta = new DateTime(2026, 9, 4, 15, 0, 0);      // sexta (ISO 5)
        var domingo = new DateTime(2026, 9, 6, 15, 0, 0);    // domingo (ISO 7, JS 0)
        var sabado = new DateTime(2026, 9, 5, 15, 0, 0);
        static Promocoes.ItemCarrinho It(string id, long preco, int qtd = 1, string cat = "Donuts")
            => new(id, cat, preco, qtd * 1000L);
        static Promocoes.Promo P(string json) => Promocoes.Parsear(json)
            ?? throw new InvalidOperationException("payload de teste nao parseou: " + json);
        static string Cg(string regra, string extra = "", string alvo = "\"alvo\":\"produtos\",\"produto_ids\":[\"A\"]") =>
            "{\"id\":\"cg-" + regra + "\",\"nome\":\"compre e ganhe\",\"tipo\":\"compre_ganhe\",\"ativa\":true," + alvo +
            ",\"inicio\":\"2026-01-01\",\"fim\":null,\"config\":{\"ganha_regra\":\"" + regra + "\"" + extra + "}}";

        // ── parser ────────────────────────────────────────────────────────
        var lx = P("""{"id":"lx","nome":"leve 3 pague 2","tipo":"leve_x_pague_y","leve":3,"pague":2,"alvo":"todos","ativa":true,"inicio":"2026-01-01","fim":null,"config":{"lxpy":{"gratis_mais_barato":true}}}""");
        checar(lx.Leve == 3 && lx.Pague == 2, "parser: leve/pague do nivel de cima");
        var cbP = P("""{"id":"cb","nome":"duo","tipo":"combo","ativa":true,"inicio":"2026-01-01","fim":null,"combo":{"itens":[{"produto_id":"A","qtd":2},{"produto_id":"D","qtd":1}],"preco_cent":4500},"config":{"combo":{"modo":"preco","preco_cent":4500}}}""");
        checar(cbP.Combo is { Itens.Count: 2, PrecoCent: 4500, Modo: "preco" }, "parser: combo.itens + config.combo");
        var cgP = P(Cg("qualquer_item", ",\"ganha\":[\"C\",\"D\"],\"teto_cent\":1500,\"limite_por_venda\":2"));
        checar(cgP.GanhaRegra == Promocoes.GanhaRegra.QualquerItem && cgP.Ganha is { Count: 2 }
            && cgP.TetoCent == 1500 && cgP.LimitePorVenda == 2, "parser: ganha_regra, ganha, teto_cent, limite_por_venda");
        checar(P(Cg("lista")).GanhaRegra == Promocoes.GanhaRegra.Lista
            && P("""{"id":"cg0","nome":"x","tipo":"compre_ganhe","ativa":true,"inicio":"2026-01-01","config":{"ganha":["C"]}}""").GanhaRegra == Promocoes.GanhaRegra.Lista,
            "parser: sem ganha_regra (promocao antiga) = lista");
        var desc = P(Cg("xyz"));
        checar(desc.GanhaRegra == Promocoes.GanhaRegra.Desconhecida && desc.Aviso is { Length: > 0 },
            "parser: regra desconhecida vira Desconhecida com aviso (nao explode, nao aplica)");
        checar(Promocoes.AvaliarCarrinho(new[] { desc }, new[] { It("A", 2190), It("B", 2190) }, sexta).TotalCent == 0,
            "regra desconhecida nao da desconto nenhum");

        // ── domingo: dias_semana do painel e JS (0 = domingo) ─────────────
        var dom = P("""{"id":"dom","nome":"domingo","tipo":"percentual","percentual":10,"alvo":"todos","ativa":true,"inicio":"2026-01-01","fim":null,"dias_semana":[0]}""");
        checar(Promocoes.PrecoEfetivoCent(new[] { dom }, "A", "Donuts", 1000, domingo).Cent == 900,
            "dias_semana [0] (JS) liga no DOMINGO (antes nunca ligava)");
        checar(Promocoes.PrecoEfetivoCent(new[] { dom }, "A", "Donuts", 1000, sabado).Cent == 1000,
            "dias_semana [0] nao liga no sabado");

        // ── preco via carrinho e "uma por pedido" ──────────────────────────
        var dez = P("""{"id":"dez","nome":"10 por cento","tipo":"percentual","percentual":10,"alvo":"produtos","produto_ids":["A"],"ativa":true,"inicio":"2026-01-01","fim":null}""");
        var cinco = P("""{"id":"cinco","nome":"5 reais off","tipo":"valor","valor_desconto_cent":500,"alvo":"todos","ativa":true,"inicio":"2026-01-01","fim":null}""");
        var av = Promocoes.AvaliarCarrinho(new[] { dez }, new[] { It("A", 2190) }, sexta);
        checar(av.PromoId == "dez" && av.DescontoCent[0] == 219, "percentual pelo carrinho: 10% de 21,90 = 2,19 na linha");
        av = Promocoes.AvaliarCarrinho(new[] { dez, cinco }, new[] { It("A", 2190), It("B", 2190) }, sexta);
        checar(av.PromoId == "cinco" && av.TotalCent == 1000 && av.DescontoCent[0] == 500 && av.DescontoCent[1] == 500,
            "UMA por pedido: 5 off em dois itens (10,00) vence 10% num item (2,19)");
        checar(av.Perdedoras.Count == 1 && av.Perdedoras[0].PromoId == "dez" && av.Perdedoras[0].DescontoCent == 219,
            "a perdedora e nomeada com o desconto que teria dado");
        var dezB = P("""{"id":"a-dez","nome":"10 por cento B","tipo":"percentual","percentual":10,"alvo":"produtos","produto_ids":["A"],"ativa":true,"inicio":"2026-01-01","fim":null}""");
        av = Promocoes.AvaliarCarrinho(new[] { dez, dezB }, new[] { It("A", 2190) }, sexta);
        checar(av.PromoId == "a-dez", "empate de desconto: vence o menor Id (ordinal), estavel");

        // ── leve X pague Y ─────────────────────────────────────────────────
        av = Promocoes.AvaliarCarrinho(new[] { lx }, new[] { It("A", 2190, 2), It("C", 1350), It("D", 800) }, sexta);
        checar(av.PromoId == "lx" && av.TotalCent == 800 && av.DescontoCent[2] == 800 && av.UnidadesGratis[2] == 1,
            "leve 3 pague 2 com 4 unidades: 1 gratis, a mais barata (cafe 8,00)");
        av = Promocoes.AvaliarCarrinho(new[] { lx }, new[] { It("A", 2190, 4), It("C", 1350, 2), It("D", 800) }, sexta);
        checar(av.TotalCent == 800 + 1350 && av.UnidadesGratis[2] == 1 && av.UnidadesGratis[1] == 1,
            "leve 3 pague 2 com 7 unidades: 2 gratis (cafe e um cookie)");
        checar(Promocoes.AvaliarCarrinho(new[] { lx }, new[] { It("A", 2190, 2) }, sexta).TotalCent == 0,
            "leve 3 pague 2 com 2 unidades: nada");
        checar(Promocoes.AvaliarCarrinho(new[] { lx }, new[] { It("A", 2190, 2), new("D", "Bebidas", 800, 500) }, sexta).TotalCent == 0,
            "fracao (0,5 un) nao conta como unidade do grupo");

        // ── combo ─────────────────────────────────────────────────────────
        av = Promocoes.AvaliarCarrinho(new[] { cbP }, new[] { It("A", 2190, 2), It("D", 800) }, sexta);
        checar(av.PromoId == "cb" && av.TotalCent == 680 && av.DescontoCent[0] + av.DescontoCent[1] == 680,
            "combo 2 donuts + cafe por 45,00: soma 51,80, desconto 6,80 rateado fechando exato");
        av = Promocoes.AvaliarCarrinho(new[] { cbP }, new[] { It("A", 2190, 4), It("D", 800, 2) }, sexta);
        checar(av.TotalCent == 1360, "combo cabe 2 vezes: desconto dobra");
        checar(Promocoes.AvaliarCarrinho(new[] { cbP }, new[] { It("A", 2190, 2) }, sexta).TotalCent == 0,
            "combo sem o cafe na comanda: nada");
        var cbPct = P("""{"id":"cbp","nome":"duo 15","tipo":"combo","ativa":true,"inicio":"2026-01-01","fim":null,"combo":{"itens":[{"produto_id":"A","qtd":1},{"produto_id":"D","qtd":1}],"preco_cent":2541},"config":{"combo":{"modo":"desconto","desconto_pct":15}}}""");
        av = Promocoes.AvaliarCarrinho(new[] { cbPct }, new[] { It("A", 2190), It("D", 800) }, sexta);
        checar(av.TotalCent == 449, "combo modo desconto 15% sobre 29,90 = 4,49 (arredondado)");

        // ── compre e ganhe: as cinco regras (A Nutella 21,90 e o alvo) ─────
        var carrinho = new[] { It("A", 2190), It("B", 2190), It("C", 1350), It("D", 800) };
        av = Promocoes.AvaliarCarrinho(new[] { P(Cg("qualquer_item")) }, carrinho, sexta);
        checar(av.TotalCent == 2190 && av.DescontoCent[1] == 2190 && av.UnidadesGratis[1] == 1,
            "qualquer_item (legado): o brinde mais caro de valor IGUAL ou menor (B 21,90) sai gratis");
        // REGRA DO DONO: brinde nunca mais caro que a compra, em nenhuma regra
        var caro = new[] { It("A", 1350), It("B", 2190), It("C", 1350), It("D", 800) };   // A (alvo) custa 13,50
        av = Promocoes.AvaliarCarrinho(new[] { P(Cg("qualquer_item")) }, caro, sexta);
        checar(av.TotalCent == 1350 && av.DescontoCent[2] == 1350 && av.DescontoCent[1] == 0,
            "qualquer_item: B (21,90) e mais caro que a compra (13,50) e NAO sai; sai C (13,50)");
        av = Promocoes.AvaliarCarrinho(new[] { P(Cg("lista", ",\"ganha\":[\"B\"]")) }, caro, sexta);
        checar(av.TotalCent == 0 && av.Dica is { Length: > 0 } && av.Dica.Contains("13,50"),
            "lista [B] com B mais caro que a compra: nada, e a dica diz o teto (13,50)");
        av = Promocoes.AvaliarCarrinho(new[] { P(Cg("lista", ",\"ganha\":[\"B\",\"D\"]")) }, caro, sexta);
        checar(av.TotalCent == 800 && av.DescontoCent[3] == 800, "lista [B, D]: so D (8,00) cabe abaixo da compra");
        av = Promocoes.AvaliarCarrinho(new[] { P(Cg("mesmo_valor")) }, carrinho, sexta);
        checar(av.TotalCent == 2190 && av.DescontoCent[1] == 2190, "mesmo_valor: B (21,90) sai gratis");
        av = Promocoes.AvaliarCarrinho(new[] { P(Cg("qualquer_mais_barato")) }, carrinho, sexta);
        checar(av.TotalCent == 2190 && av.DescontoCent[1] == 2190, "qualquer_mais_barato inclui o de valor IGUAL (B)");
        av = Promocoes.AvaliarCarrinho(new[] { P(Cg("qualquer_mais_barato")) }, new[] { It("A", 2190), It("C", 1350), It("D", 800) }, sexta);
        checar(av.TotalCent == 1350 && av.DescontoCent[1] == 1350, "qualquer_mais_barato sem B: o cookie (13,50), nao o cafe");
        av = Promocoes.AvaliarCarrinho(new[] { P(Cg("mesmo_produto")) }, carrinho, sexta);
        checar(av.TotalCent == 0 && av.Dica is { Length: > 0 } && !av.Dica.Contains('—'),
            "mesmo_produto com 1 A: nada, e a dica explica (leve mais 1)");
        av = Promocoes.AvaliarCarrinho(new[] { P(Cg("mesmo_produto")) }, new[] { It("A", 2190, 2), It("B", 2190) }, sexta);
        checar(av.TotalCent == 2190 && av.DescontoCent[0] == 2190 && av.UnidadesGratis[0] == 1,
            "mesmo_produto com A x2: um A sai gratis, B nao");
        av = Promocoes.AvaliarCarrinho(new[] { P(Cg("mesmo_produto")) }, new[] { It("A", 2190, 4) }, sexta);
        checar(av.TotalCent == 4380 && av.UnidadesGratis[0] == 2, "mesmo_produto com A x4 sem limite: 2 pares");
        av = Promocoes.AvaliarCarrinho(new[] { P(Cg("mesmo_produto", ",\"limite_por_venda\":1")) }, new[] { It("A", 2190, 4) }, sexta);
        checar(av.TotalCent == 2190 && av.UnidadesGratis[0] == 1, "limite_por_venda 1: so um par por venda");
        av = Promocoes.AvaliarCarrinho(new[] { P(Cg("lista", ",\"ganha\":[\"C\"]")) }, carrinho, sexta);
        checar(av.TotalCent == 1350 && av.DescontoCent[2] == 1350, "lista [C]: o cookie sai gratis");
        av = Promocoes.AvaliarCarrinho(new[] { P(Cg("qualquer_item", ",\"teto_cent\":1000")) }, carrinho, sexta);
        checar(av.TotalCent == 800 && av.DescontoCent[3] == 800, "teto 10,00: so o cafe cabe como brinde");
        checar(Promocoes.AvaliarCarrinho(new[] { P(Cg("qualquer_item")) }, new[] { It("B", 2190), It("C", 1350) }, sexta).TotalCent == 0,
            "sem o produto-alvo (A) na comanda: nada");
        av = Promocoes.AvaliarCarrinho(new[] { P(Cg("qualquer_mais_barato", "", "\"alvo\":\"todos\"")) },
            new[] { It("A", 3000), It("B", 2500), It("C", 2000), It("D", 2000) }, sexta);
        checar(av.TotalCent == 4500 && av.UnidadesGratis[1] == 1 && (av.UnidadesGratis[2] + av.UnidadesGratis[3]) == 1,
            "alvo todos + mais_barato: pareia 30->25 e 20->20 (45,00), o melhor possivel");

        // ── brinde x donut do dia: vence o maior desconto total ───────────
        var dia = P("""{"id":"dia","nome":"donuts do dia","tipo":"percentual","alvo":"produtos","produto_ids":["A"],"ativa":true,"inicio":"2026-01-01","fim":null,"regras_semana":[{"dias":[5],"precos_cent":{"A":1450},"produto_ids":["A"]}]}""");
        av = Promocoes.AvaliarCarrinho(new[] { dia, P(Cg("qualquer_item")) }, carrinho, sexta);
        checar(av.PromoId == "cg-qualquer_item" && av.TotalCent == 2190
            && av.Perdedoras.Count == 1 && av.Perdedoras[0].Nome == "donuts do dia" && av.Perdedoras[0].DescontoCent == 740,
            "brinde (21,90) vence o donut do dia (7,40); a perdedora e nomeada");
        av = Promocoes.AvaliarCarrinho(new[] { dia, P(Cg("qualquer_item")) }, new[] { It("A", 2190) }, sexta);
        checar(av.PromoId == "dia" && av.TotalCent == 740, "so A na comanda: sem brinde possivel, vale o donut do dia");

        // ── janela de hora e dia nas de carrinho ──────────────────────────
        var lxJanela = P("""{"id":"lxj","nome":"happy","tipo":"leve_x_pague_y","leve":2,"pague":1,"alvo":"todos","ativa":true,"inicio":"2026-01-01","fim":null,"dias_semana":[5],"config":{"janelas":[{"das":"18:00","ate":"20:00"}]}}""");
        checar(Promocoes.AvaliarCarrinho(new[] { lxJanela }, new[] { It("A", 2190, 2) }, new DateTime(2026, 9, 4, 19, 59, 0)).TotalCent == 2190,
            "leve 2 pague 1 dentro da janela (19:59, sexta)");
        checar(Promocoes.AvaliarCarrinho(new[] { lxJanela }, new[] { It("A", 2190, 2) }, new DateTime(2026, 9, 4, 20, 0, 0)).TotalCent == 0,
            "fora da janela (20:00) nao vale");
        checar(Promocoes.AvaliarCarrinho(new[] { lxJanela }, new[] { It("A", 2190, 2) }, new DateTime(2026, 9, 5, 19, 0, 0)).TotalCent == 0,
            "sabado nao vale (dias_semana [5])");

        // ── invariantes ───────────────────────────────────────────────────
        av = Promocoes.AvaliarCarrinho(new[] { cinco }, new[] { It("D", 300) }, sexta);
        checar(av.TotalCent == 300, "desconto nunca passa do bruto da linha (5,00 off num item de 3,00 = 3,00)");
        checar(Promocoes.AvaliarCarrinho(new[] { lx, cbP, dia, cinco }, Array.Empty<Promocoes.ItemCarrinho>(), sexta).TotalCent == 0,
            "comanda vazia: nada");
        foreach (var pr in new[] { lx, cbP, cbPct, dia, dez, cinco, P(Cg("qualquer_item")), P(Cg("mesmo_produto")), P(Cg("mesmo_valor")), P(Cg("qualquer_mais_barato")), P(Cg("lista")), desc })
        {
            var frase = Promocoes.DescreveRegra(pr);
            checar(frase.Length > 0 && !frase.Contains('—') && !frase.Contains('–'),
                "DescreveRegra sem travessao: " + frase);
        }
        checar(!Promocoes.DescreveQuando(lxJanela, "A").Contains('–'), "DescreveQuando sem meia-risca na faixa de hora");
    }
}
