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
}
