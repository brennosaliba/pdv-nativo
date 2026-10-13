using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// PEDIDO PARA A COZINHA (13/10/2026). Pedido do dono: "caixa faz pedido PDV coloca se e consumo
/// loja ou viagem... e cai no KDS pra producao em cor de card diferente e com mencao Venda PDV".
///
/// O que nao pode quebrar:
///  · o pedido nasce NA TRANSACAO da venda paga, e e so producao: a venda continua sendo UMA
///    linha "venda" na fila (estoque, receita e nota contam uma vez so);
///  · sem internet a venda nao trava: o pedido fica na fila e sai quando a rede volta, sem
///    empurrar a venda para fora da janela do dreno;
///  · venda de teste nao manda nada para a cozinha de verdade;
///  · textos de uma linha, sem travessao.
/// </summary>
public static class TestesPedidoCozinha
{
    public static async Task RodarAsync(Action<bool, string> checar)
    {
        // ── textos da tela ───────────────────────────────────────────────────
        foreach (var t in new[] { PedidoCozinha.Rotulo, PedidoCozinha.TextoNao, PedidoCozinha.TextoLoja, PedidoCozinha.TextoViagem })
            checar(!t.Contains('—') && !t.Contains('–') && !t.Contains('\n') && t.Length <= 20,
                $"texto curto, uma linha e sem travessao: \"{t}\"");
        checar(PedidoCozinha.TextoLoja == "Consumo na loja" && PedidoCozinha.TextoViagem == "Para viagem"
               && PedidoCozinha.Rotulo == "Para a cozinha",
            "os textos sao os do dono: Para a cozinha, Consumo na loja, Para viagem");
        checar(PedidoCozinha.Texto(null) == "Não" && PedidoCozinha.Texto(DestinoCozinha.Loja) == "Consumo na loja",
            "Texto(null) = Não; Texto(Loja) = Consumo na loja");

        // ── fila do dreno: tipo novo com handler, janela propria e validade de 3 h ──
        checar(Drenagem.TiposComHandler.Contains(PedidoCozinha.TipoNaFila),
            "kds_cozinha tem handler no dreno (tipo sem handler vira dead-letter)");
        checar(Drenagem.JanelaPropria.Any(j => j.Tipo == PedidoCozinha.TipoNaFila && j.Janela <= 10),
            "kds_cozinha tem janela propria pequena (nao disputa a janela da venda)");
        checar(Drenagem.PrazoDoTransitorio(PedidoCozinha.TipoNaFila) == TimeSpan.FromHours(3),
            "pedido da cozinha desiste em 3 h sem rede (a venda continua com 7 dias)");
        checar(Drenagem.PrazoDoTransitorio("venda") == TimeSpan.FromDays(Drenagem.DiasParaDesistir),
            "a validade da venda nao mudou");

        // ── desfecho do envio (puro) ─────────────────────────────────────────
        var agora = DateTime.Now;
        string Payload(DateTime criado) => JsonSerializer.Serialize(new { vendaId = "v1", criadoEm = criado.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") });
        var chamadas = new List<(string Url, string Token, string Corpo)>();
        Func<string, string, string, Task<(int, string?)>> Responde(int status) => (u, t, c) =>
        {
            chamadas.Add((u, t, c));
            return Task.FromResult<(int, string?)>((status, "{}"));
        };
        var p = Payload(agora);

        var r201 = await PedidoCozinha.ResolverNaFilaAsync(p, "https://kds.exemplo/homolog/", "chave-123", Responde(201), agora);
        checar(r201.Ok == true, "201: pedido na TV, sai da fila");
        checar(chamadas.Count == 1 && chamadas[0].Url == "https://kds.exemplo/homolog/api/pdv/pedidos"
               && chamadas[0].Token == "chave-123" && chamadas[0].Corpo == p,
            "POST no /api/pdv/pedidos do servidor do KDS, com a chave e o payload da fila sem mudar nada");
        checar((await PedidoCozinha.ResolverNaFilaAsync(p, "u", "k", Responde(200), agora)).Ok == true,
            "200: o servidor ja tinha (reenvio), sai da fila sem card dobrado");
        checar((await PedidoCozinha.ResolverNaFilaAsync(p, "u", "k", Responde(401), agora)).Ok is null,
            "401: chave recusada espera (a loja corrige a chave e o pedido sai)");
        checar((await PedidoCozinha.ResolverNaFilaAsync(p, "u", "k", Responde(404), agora)).Ok is null,
            "404: rota ainda nao publicada espera");
        checar((await PedidoCozinha.ResolverNaFilaAsync(p, "u", "k", Responde(400), agora)).Ok == false,
            "400: corpo recusado conta tentativa (dead-letter no teto)");
        checar((await PedidoCozinha.ResolverNaFilaAsync(p, "u", "k", Responde(0), agora)).Ok is null,
            "sem resposta (sem internet): espera, a venda nao trava");
        checar((await PedidoCozinha.ResolverNaFilaAsync(p, "u", "k", Responde(503), agora)).Ok is null,
            "503: espera");
        var antes = chamadas.Count;
        checar((await PedidoCozinha.ResolverNaFilaAsync(p, "u", null, Responde(201), agora)).Ok is null && chamadas.Count == antes,
            "sem chave no caixa: espera e nao chama ninguem");
        var velho = await PedidoCozinha.ResolverNaFilaAsync(Payload(agora.AddHours(-4)), "u", "k", Responde(201), agora);
        checar(velho.Ok == true && chamadas.Count == antes && (velho.Erro ?? "").Contains("descartado"),
            "pedido de 4 h atras sai da fila SEM ir para a TV (o cliente ja foi atendido)");
        checar((await PedidoCozinha.ResolverNaFilaAsync("nao e json", "u", "k", Responde(201), agora)).Ok == false,
            "payload ilegivel nao fica em retry eterno");

        // ── a venda de verdade: Vendas.Finalizar com e sem cozinha ───────────
        var arquivo = Path.Combine(Path.GetTempPath(), $"cozinha_teste_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            var op = new Operador("op-cozinha", "Cleide", "operador");
            using var cx = Banco.Abrir(arquivo);
            Operadores.Salvar(cx, op.Id, op.Nome, "2468", "operador");
            var sessao = Caixa.Abrir(cx, op, Dinheiro.DeReais(100));

            checar(!PedidoCozinha.Ligado(cx), "sem chave gravada o botao da cozinha nao aparece");
            Vendas.GravarConfig(cx, PedidoCozinha.ChaveToken, "  chave-da-savassi-0123456789abcdef ");
            checar(PedidoCozinha.Ligado(cx) && PedidoCozinha.Token(cx) == "chave-da-savassi-0123456789abcdef",
                "com a chave gravada o botao aparece (espacos em volta nao contam)");
            checar(PedidoCozinha.Url(cx) == "https://kds.americandaybrasil.com.br/homolog",
                "endereco padrao: o servidor da TV da cozinha");

            var combo = new List<Escolha>
            {
                new("d-ovo", "5", "DONUT OVOMALTINE", "regra-donuts", 2, "Donuts"),
                new("d-homer", "1", "DONUT HOMER", "regra-donuts", 2, "Donuts"),
            };
            var itens = new List<LinhaVenda>
            {
                new("p1", "4", "DONUT NINHO", new Quantidade(2000), Dinheiro.DeReais(12), Dinheiro.DeReais(24),
                    "UN", "19053100", null, "102", null, 0),
                new("p2", "87", "COMBO 4 DONUTS", Quantidade.Um, Dinheiro.DeReais(40), Dinheiro.DeReais(40),
                    "UN", "19053100", null, "102", null, 0, Escolhas: combo),
                new("p3", "56", "AGUA 500ML", Quantidade.Um, Dinheiro.DeReais(5), Dinheiro.DeReais(5),
                    "UN", "22011000", null, "102", null, 0),
            };
            var pagos = new[] { new PagamentoVenda("dinheiro", Dinheiro.DeReais(69), Dinheiro.Zero) };

            int Fila(string tipo, string venda) => cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM outbox WHERE tipo = @t AND ref_id = @v", new { t = tipo, v = venda });

            // 1) venda comum: nada para a cozinha
            var comum = Vendas.Finalizar(cx, sessao, op, itens, pagos, null, "American Day Savassi", null);
            checar(Fila(PedidoCozinha.TipoNaFila, comum.Id) == 0, "venda sem 'Para a cozinha' nao manda pedido");

            // 2) venda para a cozinha, consumo na loja
            var v = Vendas.Finalizar(cx, sessao, op, itens, pagos, null, "American Day Savassi", null,
                DestinoCozinha.Loja, "Joana");
            checar(Fila(PedidoCozinha.TipoNaFila, v.Id) == 1, "venda 'Para a cozinha' poe UM pedido na fila");
            checar(Fila("venda_composta", v.Id) + Fila("venda", v.Id) == 1,
                "a venda continua sendo UMA linha de venda na fila (estoque, receita e nota contam uma vez)");
            var linha = cx.QueryFirst("SELECT client_key, payload FROM outbox WHERE tipo = @t AND ref_id = @v",
                new { t = PedidoCozinha.TipoNaFila, v = v.Id });
            var kChave = (string)linha.client_key;
            var corpo = JsonDocument.Parse((string)linha.payload).RootElement;
            checar(kChave != v.ClientKey,
                "o pedido da cozinha nao usa a client_key da venda (nao se confunde com ela no dreno)");
            checar(!corpo.TryGetProperty("p_itens", out _) && !corpo.TryGetProperty("p_pagamentos", out _)
                   && !((string)linha.payload).Contains("valor", StringComparison.OrdinalIgnoreCase)
                   && !((string)linha.payload).Contains("chave-da-savassi", StringComparison.Ordinal),
                "o pedido da cozinha nao carrega preco, pagamento nem a chave: e so producao");
            checar(corpo.GetProperty("vendaId").GetString() == v.Id
                   && corpo.GetProperty("numero").GetString() == v.Numero.ToString()
                   && corpo.GetProperty("consumo").GetString() == "loja"
                   && corpo.GetProperty("cliente").GetString() == "Joana"
                   && corpo.GetProperty("operador").GetString() == "Cleide",
                "payload: id e numero da venda, consumo na loja, cliente e operador");
            var its = corpo.GetProperty("itens").EnumerateArray().ToList();
            checar(its.Count == 3
                   && its[0].GetProperty("nome").GetString() == "DONUT NINHO" && its[0].GetProperty("qtd").GetInt32() == 2
                   && its[1].GetProperty("nome").GetString() == "COMBO 4 DONUTS" && its[1].GetProperty("qtd").GetInt32() == 1,
                "todos os itens da venda vao, com a quantidade em unidades");
            var sabores = its[1].GetProperty("sabores").EnumerateArray()
                .Select(s => $"{s.GetProperty("qtd").GetInt32()}x {s.GetProperty("nome").GetString()}").ToList();
            checar(sabores.SequenceEqual(new[] { "2x DONUT OVOMALTINE", "2x DONUT HOMER" }),
                "o combo leva os sabores por unidade (a TV multiplica): " + string.Join(", ", sabores));
            checar(!its[0].TryGetProperty("sabores", out _), "item simples nao leva sabores");

            // 3) para viagem
            var vv = Vendas.Finalizar(cx, sessao, op, itens.Take(1).ToList(),
                new[] { new PagamentoVenda("dinheiro", Dinheiro.DeReais(24), Dinheiro.Zero) },
                null, "American Day Savassi", null, DestinoCozinha.Viagem);
            var corpoV = JsonDocument.Parse(cx.ExecuteScalar<string>(
                "SELECT payload FROM outbox WHERE tipo = @t AND ref_id = @v", new { t = PedidoCozinha.TipoNaFila, v = vv.Id })!).RootElement;
            checar(corpoV.GetProperty("consumo").GetString() == "viagem"
                   && corpoV.GetProperty("cliente").ValueKind == JsonValueKind.Null,
                "para viagem; sem nome o cliente vai vazio");

            // 4) pagamento que nao fecha: nem venda nem pedido
            var antesFila = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo = @t", new { t = PedidoCozinha.TipoNaFila });
            try
            {
                Vendas.Finalizar(cx, sessao, op, itens, new[] { new PagamentoVenda("dinheiro", Dinheiro.DeReais(1), Dinheiro.Zero) },
                    null, "American Day Savassi", null, DestinoCozinha.Loja);
            }
            catch (InvalidOperationException) { }
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo = @t", new { t = PedidoCozinha.TipoNaFila }) == antesFila,
                "venda recusada (pagamento nao fecha) nao manda pedido para a cozinha");

            // 5) janela do dreno: 30 pedidos da cozinha presos (rota fora) nao escondem a venda
            for (var i = 0; i < 30; i++)
                PedidoCozinha.Enfileirar(cx, null, $"preso-{i}", new { vendaId = $"preso-{i}" });
            var vDepois = Vendas.Finalizar(cx, sessao, op, itens.Take(1).ToList(),
                new[] { new PagamentoVenda("dinheiro", Dinheiro.DeReais(24), Dinheiro.Zero) },
                null, "American Day Savassi", null);
            var janela = Drenagem.JanelaDaFila(cx);
            checar(janela.Any(l => (string)l.tipo == "venda" && (string)l.ref_id == vDepois.Id),
                "com 30 pedidos da cozinha esperando a rede, a venda nova continua na janela do dreno");
            checar(janela.Count(l => (string)l.tipo == PedidoCozinha.TipoNaFila) <= 10,
                "os pedidos da cozinha entram no maximo 10 por varredura");

            // 6) venda de teste (homologacao): nada para a cozinha de verdade
            Vendas.GravarConfig(cx, "homologacao", "1");
            var vt = Vendas.Finalizar(cx, sessao, op, itens.Take(1).ToList(),
                new[] { new PagamentoVenda("dinheiro", Dinheiro.DeReais(24), Dinheiro.Zero) },
                null, "American Day Savassi", null, DestinoCozinha.Loja);
            checar(Fila(PedidoCozinha.TipoNaFila, vt.Id) == 0, "venda de teste nao manda pedido para a cozinha");
            Vendas.GravarConfig(cx, "homologacao", "0");

            // 7) item pesavel: quantidade no nome, 1 na TV
            var peso = PedidoCozinha.Corpo("v-peso", 9, new[]
            {
                new LinhaVenda("p9", "9", "PAO DE QUEIJO", new Quantidade(375), Dinheiro.DeReais(24.90m), Dinheiro.DeReais(9.34m),
                    "KG", "19053100", null, "102", null, 0),
            }, DestinoCozinha.Viagem, "Cleide", null, DateTime.Now);
            var itPeso = ((List<Dictionary<string, object?>>)peso["itens"]!)[0];
            checar((int)itPeso["qtd"]! == 1 && ((string)itPeso["nome"]!).Contains("0,375 KG", StringComparison.Ordinal),
                "item pesavel vai como 1 com a quantidade no nome: " + itPeso["nome"]);
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }
}
