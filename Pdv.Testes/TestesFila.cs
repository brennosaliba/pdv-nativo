using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A fila de sincronização quando o servidor RECUSA para sempre.
///
/// Cenário real (24/08/2026, banco do caixa): 16 vendas — R$ 102.626,50 — foram
/// recusadas com HTTP 409 (o operator_id do caixa não existe em employees), o dreno
/// desistiu depois de 12 tentativas e carimbou <c>enviado_em</c> em todas. Resultado:
/// o contador de pendentes mostrava ZERO, tudo verde na tela, e cem mil reais que a
/// nuvem nunca teve. Desistir NÃO é entregar — e a fila não pode mentir sobre isso.
/// </summary>
public static class TestesFila
{
    public static async Task RodarAsync(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"fila_teste_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir(arquivo);

            var op = new Operador("op-fila", "Bia", "operador");
            Operadores.Salvar(cx, op.Id, op.Nome, "4321", "operador");
            var sessao = Caixa.Abrir(cx, op, Dinheiro.DeReais(100));

            // As 3 vendas do roteiro de hoje: 990,00 + 1.003,00 + 500,00 = 2.493,00.
            var valores = new[] { 990.00m, 1003.00m, 500.00m };
            var chaves = new List<string>();
            foreach (var reais in valores)
            {
                var total = Dinheiro.DeReais(reais);
                var g = Vendas.Finalizar(cx, sessao, op,
                    new[] { new LinhaVenda(null, "SKU", "COMBO", Quantidade.Um, total, total,
                                           "UN", "19053100", null, "102", null, 0) },
                    new[] { new PagamentoVenda("dinheiro", total, Dinheiro.Zero) },
                    null, "Loja", null);
                chaves.Add(g.ClientKey);
            }

            using var fake = new FakePostgrest(4658);
            // o 409 do incidente: chave estrangeira que não resolve na nuvem. Permanente.
            foreach (var k in chaves) fake.FalhaPorChave[k] = (409, 9999);

            var nuvem = new Nuvem(fake.Url);
            checar(await nuvem.EntrarAsync("fila@teste.com", "x"), "nuvem fake autentica");
            using var dren = new Drenagem(nuvem, fake.Url);

            for (var i = 0; i < Drenagem.MaxTentativas + 2; i++) await dren.DrenarAsync();

            checar(cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM outbox WHERE tipo='venda' AND ultimo_erro LIKE 'desistido%'") == 3,
                "as 3 vendas recusadas esgotaram as tentativas e viraram dead-letter");

            // ── O DEFEITO ───────────────────────────────────────────────────
            // Carimbar enviado_em no dead-letter é dizer "a nuvem recebeu". Ela não
            // recebeu — e é esse carimbo que tira a venda de TODO contador de pendência.
            checar(cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM outbox WHERE ultimo_erro LIKE 'desistido%' AND enviado_em IS NOT NULL") == 0,
                "desistir NÃO é entregar: a linha que desistiu nunca ganha enviado_em");

            var (_, vendasNaoEntregues) = Sincronizacao.Pendencias();
            checar(vendasNaoEntregues.Total == 3,
                $"o contador de não-entregues inclui as desistidas (viu {vendasNaoEntregues.Total}, esperado 3)");

            checar(Sincronizacao.Desistidos() == 3,
                "o contador de desistidas continua acusando as 3");

            // ── "3 vendas paradas" esconde R$ 2.493,00 ──────────────────────
            // Contagem sozinha não dimensiona o estrago: 3 cafés e 3 vendas de mil
            // reais dão o mesmo "3". O que decide se o dono liga pro suporte agora
            // ou amanhã é o VALOR.
            var paradas = Sincronizacao.VendasNaoEntregues();
            checar(paradas.Desistidas == 3 && paradas.Aguardando == 0,
                "o resumo separa o que ainda espera do que já desistiu");
            checar(paradas.Valor.Centavos == 249300,
                $"o resumo diz QUANTO está parado: {paradas.Valor.Formatado()} (esperado R$ 2.493,00)");
            checar(paradas.Resumo is string aviso
                   && aviso.Contains("2.493,00")
                   && aviso.Contains("desistiu", StringComparison.OrdinalIgnoreCase),
                "o aviso da tela leva o valor e diz que o envio DESISTIU (pede conferência)");

            // ── E O BUG QUE NÃO PODE NASCER NO LUGAR ────────────────────────
            // Parar de carimbar enviado_em sem consertar o WHERE da drenagem faria a
            // linha morta voltar em TODA varredura, para sempre, ocupando a janela.
            var restamAntes = chaves.Select(k => fake.FalhaPorChave[k].Restam).ToArray();
            for (var i = 0; i < 5; i++) await dren.DrenarAsync();
            checar(chaves.Select(k => fake.FalhaPorChave[k].Restam).SequenceEqual(restamAntes),
                "a drenagem NÃO reprocessa o que já desistiu (não bate mais no servidor)");
            checar(cx.ExecuteScalar<int>(
                "SELECT MAX(tentativas) FROM outbox WHERE ultimo_erro LIKE 'desistido%'") == Drenagem.MaxTentativas,
                "a linha desistida também não segue contando tentativas");

            // Fila que não starva: venda nova sobe normalmente com as mortas ao lado.
            var totalNova = Dinheiro.DeReais(7.50m);
            var nova = Vendas.Finalizar(cx, sessao, op,
                new[] { new LinhaVenda(null, "SKU", "CAFE", Quantidade.Um, totalNova, totalNova,
                                       "UN", "19053100", null, "102", null, 0) },
                new[] { new PagamentoVenda("dinheiro", totalNova, Dinheiro.Zero) },
                null, "Loja", null);
            await dren.DrenarAsync();
            checar(fake.Vendas.ContainsKey(nova.ClientKey),
                "venda nova sobe mesmo com dead-letters na fila (sem starvation)");

            // Dependente de venda desistida (o vínculo da nota / o cancelamento) não
            // pode esperar para sempre por uma venda que nunca terá futuro.
            Vendas.RegistrarEmissao(cx, chaves.Count > 0 ? VendaDe(cx, chaves[0]) : "", new ResultadoEmissao
            {
                Caminho = "agente", Autorizado = true, Modo = "online", Chave = new string('7', 44),
                Numero = 91, Serie = 1, TpAmb = 1, Protocolo = "1312600007371", CStat = "100", VNF = 990m,
            });
            for (var i = 0; i < Drenagem.MaxTentativas + 2; i++) await dren.DrenarAsync();
            checar(cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM outbox WHERE tipo='nfce_vinculo' AND enviado_em IS NULL "
                + "AND COALESCE(ultimo_erro,'') NOT LIKE 'desistido%'") == 0,
                "vínculo de venda desistida não fica esperando eternamente");
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }

    private static string VendaDe(SqliteConnection cx, string clientKey)
        => cx.ExecuteScalar<string>("SELECT id FROM venda WHERE client_key = @K", new { K = clientKey })!;

    /// <summary>
    /// UMA LINHA ENVENENADA NÃO PODE PARAR A FILA INTEIRA.
    ///
    /// Incidente (04/09/2026, Savassi, em produção): a fila local parou de subir às
    /// 16:15 com o caixa vivo e vendendo. A nuvem tinha 52 vendas até essa hora e ZERO
    /// depois; o aviso de pronto do KDS nunca saiu; o heartbeat dizia "turno aberto ·
    /// nenhum" e nada mais. Sem rastro em lugar nenhum.
    ///
    /// O mecanismo que reproduz TODOS os sintomas: os handlers engolem exceção, mas o
    /// código EM VOLTA de cada item (os casts do `item` dynamic, o Banco.Abrir, o UPDATE
    /// da linha) vivia dentro de UM try para a varredura inteira. Uma exceção ali
    /// abortava a varredura sem escrever nada na linha, e a varredura seguinte (ORDER
    /// BY id) batia na MESMA linha primeiro. Um item podre na frente = fila morta para
    /// sempre, em silêncio.
    ///
    /// As quatro regras que este bloco vigia:
    ///  1. exceção num item fica NA LINHA dele (ultimo_erro, tentativas, primeiro_erro_em)
    ///     e os itens SEGUINTES sobem na mesma varredura;
    ///  2. depois de MaxTentativas exceções a linha vai para o dead-letter com motivo
    ///     legível, e não volta para a frente da fila;
    ///  3. o resumo da fila que o heartbeat manda ao painel diz quantos, de que tipo,
    ///     há quanto tempo e qual foi o último erro, sem travessão;
    ///  4. varredura abortada por exceção geral deixa rastro em fila.txt.
    /// </summary>
    public static async Task RodarIsolamentoAsync(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"fila_isolamento_{Guid.NewGuid():N}.db");
        var arquivoVazio = Path.Combine(Path.GetTempPath(), $"fila_sem_esquema_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        // Banco.Pasta é a pasta REAL (ProgramData) mesmo em teste, igual ao kds-pronto.txt.
        var diag = Path.Combine(Banco.Pasta, "fila.txt");
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir(arquivo);

            var op = new Operador("op-iso", "Duda", "operador");
            Operadores.Salvar(cx, op.Id, op.Nome, "5432", "operador");
            var sessao = Caixa.Abrir(cx, op, Dinheiro.DeReais(50));

            // A linha envenenada entra PRIMEIRO (id mais baixo): é ela que toda varredura
            // encontra antes de tudo. payload é BLOB, não texto: o cast `(string)item.payload`
            // do laço lança ANTES de qualquer handler rodar — exatamente "o código em volta".
            cx.Execute("""
                INSERT INTO outbox (tipo, ref_id, client_key, payload, criado_em)
                VALUES ('venda', 'venda-podre', 'ck-podre', X'00FF', @Em)
                """, new { Em = DateTime.Now.ToString("o") });
            var idPodre = cx.ExecuteScalar<long>("SELECT id FROM outbox WHERE ref_id = 'venda-podre'");

            // Duas vendas de verdade, ATRÁS da podre.
            var chaves = new List<string>();
            foreach (var reais in new[] { 12.00m, 8.50m }) chaves.Add(Vender(cx, sessao, op, reais).ClientKey);

            using var fake = new FakePostgrest(4659);
            var nuvem = new Nuvem(fake.Url);
            checar(await nuvem.EntrarAsync("iso@teste.com", "x"), "nuvem fake autentica");
            using var dren = new Drenagem(nuvem, fake.Url);

            try { if (File.Exists(diag)) File.Delete(diag); } catch { }

            // ── 1. ISOLAMENTO: uma varredura só, e as vendas de trás sobem ─────────
            await dren.DrenarAsync();
            checar(chaves.All(k => fake.Vendas.ContainsKey(k)),
                "as vendas ATRÁS da linha envenenada sobem na MESMA varredura (era: varredura inteira abortada, nada subia)");

            var podre = cx.QuerySingle(
                "SELECT tentativas, ultimo_erro, primeiro_erro_em, desistido_em, enviado_em FROM outbox WHERE id = @Id",
                new { Id = idPodre });
            var erroPodre = (string?)podre.ultimo_erro ?? "";
            checar(erroPodre.StartsWith("exceção: ", StringComparison.Ordinal),
                $"a exceção fica gravada NA LINHA como 'exceção: <mensagem>' (viu: '{erroPodre}')");
            checar((long)podre.tentativas == 1,
                $"a exceção conta UMA tentativa na linha (viu {(long)podre.tentativas})");
            checar(podre.primeiro_erro_em is string,
                "a primeira exceção carimba primeiro_erro_em (o relógio da expiração)");
            checar(podre.desistido_em is null && podre.enviado_em is null,
                "uma exceção só não desiste nem finge entrega");

            var rastro = File.Exists(diag) ? File.ReadAllText(diag) : "";
            checar(rastro.Contains($"linha {idPodre}", StringComparison.Ordinal),
                "fila.txt registra a exceção da linha (o rastro que faltou no dia 04/09)");

            // ── 2. POISON ROW: depois de MaxTentativas, dead-letter com motivo ──────
            for (var i = 0; i < Drenagem.MaxTentativas + 2; i++) await dren.DrenarAsync();
            podre = cx.QuerySingle(
                "SELECT tentativas, ultimo_erro, primeiro_erro_em, desistido_em, enviado_em FROM outbox WHERE id = @Id",
                new { Id = idPodre });
            erroPodre = (string?)podre.ultimo_erro ?? "";
            checar(podre.desistido_em is string,
                "a linha que lança em toda varredura vai para o dead-letter (desistido_em), como as recusas");
            checar(podre.enviado_em is null, "desistir por exceção também NÃO é entregar");
            checar((long)podre.tentativas == Drenagem.MaxTentativas,
                $"o contador para em MaxTentativas (viu {(long)podre.tentativas}): a linha morta não é reprocessada");
            checar(erroPodre.StartsWith("desistido", StringComparison.Ordinal)
                   && erroPodre.Contains("exceção", StringComparison.Ordinal),
                $"o motivo do dead-letter é legível e diz que foi exceção (viu: '{erroPodre}')");
            checar(!erroPodre.Contains('—') && !erroPodre.Contains('–'),
                "sem travessão no rastro: ele chega ao painel pelo heartbeat");
            checar(Sincronizacao.MotivoHumano(erroPodre) is string humano
                   && humano.Contains("caixa", StringComparison.OrdinalIgnoreCase),
                $"MotivoHumano traduz a exceção para quem está no balcão (viu: '{Sincronizacao.MotivoHumano(erroPodre)}')");

            // ── 3. A FILA SEGUE SAUDÁVEL: venda nova sobe; a podre não volta ────────
            var nova = Vender(cx, sessao, op, 3.00m);
            await dren.DrenarAsync();
            checar(fake.Vendas.ContainsKey(nova.ClientKey),
                "venda nova sobe com a linha podre morta ao lado");
            checar(cx.ExecuteScalar<long>("SELECT tentativas FROM outbox WHERE id = @Id", new { Id = idPodre })
                   == Drenagem.MaxTentativas,
                "a linha morta não ganha tentativa nova depois de desistida");

            // ── 4. RESUMO DA FILA (o que o heartbeat manda ao painel) ───────────────
            // Função pura: o formato que o dono lê na coluna "último detalhe".
            var agora = new DateTime(2026, 9, 4, 18, 39, 0);
            var resumo = Sincronizacao.MontarResumoDaFila(
                new[] { ("venda", 17), ("kds_pronto", 3) },
                agora.AddMinutes(-146), "exceção: database is locked", agora);
            checar(resumo == "fila: venda 17, kds_pronto 3 · mais antigo 146 min · último erro: exceção: database is locked",
                $"resumo da fila: quantos por tipo, idade do mais antigo e último erro (viu: '{resumo}')");
            checar(Sincronizacao.MontarResumoDaFila(Array.Empty<(string, int)>(), null, null, agora) == "fila vazia",
                "fila vazia diz 'fila vazia' (não some do painel)");
            var comprido = Sincronizacao.MontarResumoDaFila(
                new[] { ("venda", 1) }, null, new string('x', 200), agora);
            checar(comprido.Length <= "fila: venda 1 · último erro: ".Length + 60,
                $"o último erro é cortado em ~60 caracteres (viu {comprido.Length})");
            var comTravessao = Sincronizacao.MontarResumoDaFila(
                new[] { ("venda", 1) }, agora, "desistido após 3 tentativas — HTTP 409", agora);
            checar(!comTravessao.Contains('—') && !comTravessao.Contains('–'),
                "o resumo nunca leva travessão para o painel, nem vindo do rastro");

            // Com o banco: uma venda e um pronto presos por 503 (transitório) ficam na
            // fila e o resumo lido do SQLite enxerga os dois, com o erro que a nuvem deu.
            var presa = Vender(cx, sessao, op, 5.00m);
            cx.Execute("""
                INSERT INTO outbox (tipo, ref_id, client_key, payload, criado_em)
                VALUES ('kds_pronto', 'order-preso', 'kds_pronto:order-preso', '{"order_id":"order-preso"}', @Em)
                """, new { Em = DateTime.Now.AddMinutes(-30).ToString("o") });
            fake.PctErro503 = 100;
            await dren.DrenarAsync();
            fake.PctErro503 = 0;
            checar(!fake.Vendas.ContainsKey(presa.ClientKey), "cenário: a venda ficou presa no 503");
            var doBanco = Sincronizacao.ResumoDaFila() ?? "";
            checar(doBanco.StartsWith("fila: ", StringComparison.Ordinal)
                   && doBanco.Contains("venda 1", StringComparison.Ordinal)
                   && doBanco.Contains("kds_pronto 1", StringComparison.Ordinal),
                $"o resumo lido do banco conta só o que está pendente, por tipo (viu: '{doBanco}')");
            checar(doBanco.Contains("mais antigo 30 min", StringComparison.Ordinal)
                   || doBanco.Contains("mais antigo 31 min", StringComparison.Ordinal),
                $"o resumo diz a idade do item mais antigo em minutos (viu: '{doBanco}')");
            checar(doBanco.Contains("último erro: HTTP 503", StringComparison.Ordinal),
                $"o resumo leva o último erro que a nuvem deu (viu: '{doBanco}')");

            // ── 5. VARREDURA ABORTADA deixa rastro em fila.txt ─────────────────────
            // Banco sem esquema: o SELECT da fila lança antes de qualquer item, que é o
            // catch externo da varredura. Antes, esse caminho devolvia 0 e não dizia nada.
            try { if (File.Exists(diag)) File.Delete(diag); } catch { }
            Banco.CaminhoForcado = arquivoVazio;
            var enviados = await dren.DrenarAsync();
            Banco.CaminhoForcado = arquivo;
            rastro = File.Exists(diag) ? File.ReadAllText(diag) : "";
            checar(enviados == 0 && rastro.Contains("varredura abortada", StringComparison.Ordinal),
                $"varredura que aborta por exceção geral escreve em fila.txt (viu: '{rastro.Trim()}')");
            checar(rastro.Contains("no such table", StringComparison.OrdinalIgnoreCase),
                "…e o rastro leva a mensagem da exceção, não só 'abortou'");
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
            try { File.Delete(arquivoVazio); } catch { }
        }
    }

    private static VendaGravada Vender(SqliteConnection cx, Sessao sessao, Operador op, decimal reais)
    {
        var total = Dinheiro.DeReais(reais);
        return Vendas.Finalizar(cx, sessao, op,
            new[] { new LinhaVenda(null, "SKU", "COMBO", Quantidade.Um, total, total,
                                   "UN", "19053100", null, "102", null, 0) },
            new[] { new PagamentoVenda("dinheiro", total, Dinheiro.Zero) },
            null, "Loja", null);
    }
}
