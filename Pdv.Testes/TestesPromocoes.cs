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
    }
}
