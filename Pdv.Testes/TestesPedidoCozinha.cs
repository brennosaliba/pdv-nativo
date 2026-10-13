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

            // 8) A CHAVE VEM DA NUVEM (1.0.31, SQL 168 do ERP)
            ChaveDaNuvem(cx, checar);
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }

        // a Nuvem chama a RPC, escolhe a linha pelo nome do terminal, e o Atualizar puxa
        var nuvem = Fonte(Path.Combine("Pdv.Nucleo", "Nuvem.cs")) ?? "";
        var sinc = Fonte(Path.Combine("Pdv.Nucleo", "Sincronizacao.cs")) ?? "";
        var tela = Fonte(Path.Combine("Telas", "Configuracao.xaml.cs")) ?? "";
        var xaml = Fonte(Path.Combine("Telas", "Configuracao.xaml")) ?? "";
        checar(PedidoCozinha.Rpc == "pdv_kds_cozinha_chave_caixa"
               && nuvem.Contains("\"/rest/v1/rpc/\" + PedidoCozinha.Rpc", StringComparison.Ordinal)
               && nuvem.Contains("PedidoCozinha.EscolherDaLoja(linhas, loja)", StringComparison.Ordinal),
            "nuvem: a Nuvem chama pdv_kds_cozinha_chave_caixa e escolhe a linha pelo nome do terminal");
        checar(sinc.Contains("await nuvem.BaixarChaveCozinhaAsync(cx)", StringComparison.Ordinal),
            "nuvem: o Atualizar (Sincronizacao) puxa a chave da TV da cozinha");
        checar(tela.Contains("PedidoCozinha.DescricaoOrigem(cx)", StringComparison.Ordinal)
               && tela.Contains("PedidoCozinha.GravarManual(cx, TxtChaveCozinha.Text)", StringComparison.Ordinal)
               && tela.Contains("TxtChaveCozinha.Text = PedidoCozinha.TokenManual(cx)", StringComparison.Ordinal),
            "nuvem: a Configuracao mostra de onde vem a chave e o campo e so a sobrescrita");
        var ini = xaml.IndexOf("PEDIDO PARA A TV DA COZINHA (PedidoCozinha.cs)", StringComparison.Ordinal);
        var fim = ini < 0 ? -1 : xaml.IndexOf("TEMA DA TELA", ini, StringComparison.Ordinal);
        var bloco = ini >= 0 && fim > ini ? xaml[ini..fim] : "";
        checar(bloco.Contains("TxtOrigemChaveCozinha", StringComparison.Ordinal) && !bloco.Contains('—') && !bloco.Contains('–'),
            "nuvem: o bloco da TV da cozinha na Configuracao tem a linha da origem da chave e nenhum travessao");
    }

    private static void ChaveDaNuvem(SqliteConnection cx, Action<bool, string> checar)
    {
        const string sav = "a0a1a2a3a4a5a6a7a8a9b0b1b2b3b4b5b6b7b8b9c0c1235e";
        const string sav2 = "f0f1f2f3f4f5f6f7f8f9e0e1e2e3e4e5e6e7e8e9d0d1b7c9";
        const string cas = "c0c1c2c3c4c5c6c7c8c9c0c1c2c3c4c5c6c7c8c9c0c1ca51";
        cx.Execute("DELETE FROM config WHERE chave IN (@A, @B)", new { A = PedidoCozinha.ChaveToken, B = PedidoCozinha.ChaveTokenNuvem });

        // leitura da RPC
        var json = "[{\"store\":\"American Day Savassi\",\"chave\":\"" + sav + "\",\"termina_em\":\"235e\"},"
                   + "{\"store\":\"American Day Castelo\",\"chave\":\"" + cas + "\"},"
                   + "{\"store\":\"Loja Curta\",\"chave\":\"curta\"},{\"chave\":\"" + sav + "\"},7]";
        var linhas = PedidoCozinha.LerDaNuvem(json);
        checar(linhas.Count == 2, $"nuvem: le as linhas boas e ignora chave curta, sem loja e lixo ({linhas.Count})");
        checar(PedidoCozinha.LerDaNuvem("nao e json").Count == 0 && PedidoCozinha.LerDaNuvem(null).Count == 0
               && PedidoCozinha.LerDaNuvem("{}").Count == 0, "nuvem: JSON estranho vira lista vazia, sem excecao");
        checar(PedidoCozinha.EscolherDaLoja(linhas, "American Day Savassi")?.Chave == sav
               && PedidoCozinha.EscolherDaLoja(linhas, "american day CASTELO")?.Chave == cas,
            "nuvem: escolhe a linha pelo nome do terminal (sem caixa)");
        checar(PedidoCozinha.EscolherDaLoja(linhas, "Loja Nova") is null,
            "nuvem: dono com varias lojas e terminal de outra loja: nenhuma (nunca chuta)");
        checar(PedidoCozinha.EscolherDaLoja(linhas.Take(1).ToList(), null)?.Chave == sav,
            "nuvem: conta de terminal recebe uma linha so: e ela");

        // sem chave nenhuma
        checar(!PedidoCozinha.Ligado(cx) && PedidoCozinha.DescricaoOrigem(cx).Contains("a nuvem ainda não mandou", StringComparison.Ordinal),
            "nuvem: sem chave o botao nao aparece e a Configuracao diz para tocar em Atualizar");

        // a nuvem manda a chave: liga sozinho, sem digitar
        var mudou = PedidoCozinha.AplicarDaNuvem(cx, PedidoCozinha.EscolherDaLoja(linhas, "American Day Savassi"));
        checar(mudou != "" && PedidoCozinha.Ligado(cx) && PedidoCozinha.Token(cx) == sav,
            "nuvem: a chave da nuvem liga o Para a cozinha sem digitar nada");
        checar(PedidoCozinha.DescricaoOrigem(cx) == "Chave da TV da cozinha: vem da nuvem (termina em 235e).",
            "nuvem: a Configuracao diz 'vem da nuvem (termina em 235e)': " + PedidoCozinha.DescricaoOrigem(cx));
        var aud = cx.ExecuteScalar<string>("SELECT detalhe FROM auditoria WHERE evento = 'chave_cozinha_da_nuvem' ORDER BY rowid DESC LIMIT 1") ?? "";
        checar(aud.Contains("235e", StringComparison.Ordinal) && !aud.Contains(sav, StringComparison.Ordinal),
            "nuvem: a auditoria guarda so o final, nunca a chave inteira");
        checar(PedidoCozinha.AplicarDaNuvem(cx, new PedidoCozinha.ChaveDaNuvem("American Day Savassi", sav)) == "",
            "nuvem: a mesma chave de novo nao muda nada (idempotente)");

        // a 1.0.30 tinha a chave digitada igual: ela sai sozinha, e a troca no painel chega
        Vendas.GravarConfig(cx, PedidoCozinha.ChaveToken, sav);
        PedidoCozinha.AplicarDaNuvem(cx, new PedidoCozinha.ChaveDaNuvem("American Day Savassi", sav));
        checar(PedidoCozinha.TokenManual(cx) is null, "nuvem: a chave digitada igual a da nuvem (1.0.30) sai sozinha");
        PedidoCozinha.AplicarDaNuvem(cx, new PedidoCozinha.ChaveDaNuvem("American Day Savassi", sav2));
        checar(PedidoCozinha.Token(cx) == sav2, "nuvem: chave trocada no painel chega ao caixa no Atualizar");

        // sobrescrita: digitada vale antes da nuvem; em branco ou igual a da nuvem, sai
        PedidoCozinha.GravarManual(cx, "  outra-chave-so-deste-caixa-0123456789  ");
        checar(PedidoCozinha.Token(cx) == "outra-chave-so-deste-caixa-0123456789"
               && PedidoCozinha.DescricaoOrigem(cx).StartsWith("Chave da TV da cozinha: digitada neste caixa (termina em 6789).", StringComparison.Ordinal),
            "nuvem: a sobrescrita digitada vale antes da nuvem e a tela diz isso");
        PedidoCozinha.GravarManual(cx, "   ");
        checar(PedidoCozinha.TokenManual(cx) is null && PedidoCozinha.Token(cx) == sav2, "nuvem: campo em branco volta a valer a da nuvem");
        PedidoCozinha.GravarManual(cx, sav2);
        checar(PedidoCozinha.TokenManual(cx) is null, "nuvem: digitar a mesma da nuvem nao vira sobrescrita");

        // a nuvem tirou a chave da loja: o botao some (a nuvem e a verdade)
        PedidoCozinha.AplicarDaNuvem(cx, null);
        checar(!PedidoCozinha.Ligado(cx) && PedidoCozinha.TokenNuvem(cx) is null,
            "nuvem: sem chave da loja na nuvem, o Para a cozinha some");

        checar(PedidoCozinha.TerminaEm("abc") == "abc" && PedidoCozinha.TerminaEm(null) is null && PedidoCozinha.TerminaEm("123456") == "3456",
            "nuvem: TerminaEm mostra no maximo 4 letras");
        foreach (var t in new[] { PedidoCozinha.DescricaoOrigem(cx), "Chave da TV da cozinha: vem da nuvem (termina em 235e)." })
            checar(!t.Contains('—') && !t.Contains('–') && !t.Contains('\n'), "nuvem: texto da origem sem travessao e em uma linha");
    }

    private static string? Fonte(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj")))
            {
                var c = Path.Combine(dir.FullName, relativo);
                return File.Exists(c) ? File.ReadAllText(c) : null;
            }
        return null;
    }
}
