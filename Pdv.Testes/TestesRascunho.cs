using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A COMANDA EM ANDAMENTO CONTRA A QUEDA DE ENERGIA.
///
/// Cenário real (24/08/2026, passo 24 do roteiro da PayGo): o dono desligou o PC no
/// meio de uma venda. Os itens da comanda viviam SÓ na memória da tela — `venda` e
/// `venda_item` só nascem na finalização — então religar significou rebipar tudo:
/// "vou ter que perder tempo escaneando tudo de novo", com o cliente no balcão.
///
/// A linha que separa este teste do passo 24: restaurar os ITENS na tela NÃO é
/// realizar a venda. O roteiro exige que a venda não se realize — e ela não se
/// realiza: nenhuma linha em `venda`, nenhum pagamento reaproveitado, nenhuma
/// cobrança de TEF herdada. O que volta é o trabalho de digitação, não o dinheiro.
/// </summary>
public static class TestesRascunho
{
    public static void Rodar(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"rascunho_teste_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);

            var op = new Operador("op-rascunho", "Cleide", "operador");
            Sessao sessao;
            using (var cx = Banco.Abrir(arquivo))
            {
                Operadores.Salvar(cx, op.Id, op.Nome, "1357", "operador");
                sessao = Caixa.Abrir(cx, op, Dinheiro.DeReais(200));
            }

            // ── O BALCÃO: três bipes, um deles em promoção e um por peso ──────
            // O preço que entra na comanda é o EFETIVO no momento do toque (a tela
            // congela a promoção ali). O rascunho tem que devolver esse preço, não
            // o de tabela — senão restaurar a comanda muda o que o cliente já ouviu.
            var itens = new List<ItemRascunho>
            {
                new("p-1", "101", "COMBO AMERICANO", "Combos", 1990, 2000, "UN", "19053100", null, "102", 0, null),
                new("p-2", "102", "COOKIE PROMO", "Doces", 500, 1000, "UN", "19053100", null, "102", 0, null),
                new("p-3", "103", "PAO DE QUEIJO", "Salgados", 2490, 375, "KG", "19053100", null, "102", 0, null),
            };
            // 19,90×2 + 5,00×1 + 24,90×0,375 = 39,80 + 5,00 + 9,34 = 54,14
            var esperado = new Dinheiro(3980 + 500 + 934);

            using (var cx = Banco.Abrir(arquivo))
                Rascunho.Gravar(cx, sessao, op, itens, Dinheiro.Zero);

            // ── QUEDA DE ENERGIA ─────────────────────────────────────────────
            // Fecha a camada de persistência inteira (conexão e pool) e reabre, que
            // é o que o religamento faz. Nada de estado em memória sobrevive daqui.
            SqliteConnection.ClearAllPools();

            using (var cx = Banco.Abrir(arquivo))
            {
                var r = Rascunho.Ler(cx, sessao.Id);
                checar(r is not null, "a comanda em andamento sobrevive ao religamento");
                checar(r?.Itens.Count == 3, $"os 3 itens voltam (voltaram {r?.Itens.Count ?? 0})");

                var total = new Dinheiro(r?.Itens.Sum(i => new Dinheiro(i.PrecoCent).VezesQtd(i.QtdMilesimos).Centavos) ?? 0);
                checar(total.Centavos == esperado.Centavos,
                    $"volta o preço e a quantidade do momento do bipe: {total.Formatado()} (esperado {esperado.Formatado()})");
                checar(r?.Itens.FirstOrDefault(i => i.ProdutoId == "p-3")?.QtdMilesimos == 375,
                    "quantidade por peso volta em milésimos (0,375 kg não vira 0 nem 1)");
                checar(r?.Itens.FirstOrDefault(i => i.ProdutoId == "p-1")?.Ncm == "19053100",
                    "o item volta com o bloco fiscal (NCM/CSOSN) — a nota sai igual");
                checar(r?.OperadorId == op.Id && r?.SessaoId == sessao.Id,
                    "o rascunho volta amarrado ao operador e à sessão de caixa");

                // ── O PASSO 24 CONTINUA VALENDO ──────────────────────────────
                checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM venda") == 0,
                    "religar com rascunho NÃO realiza a venda (nenhuma linha em venda)");
                checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM venda_pagamento") == 0,
                    "religar com rascunho NÃO cria pagamento");
                checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo='venda'") == 0,
                    "religar com rascunho NÃO enfileira nada para a nuvem");
            }

            // ── O RASCUNHO É DO TURNO, NÃO DO TERMINAL ───────────────────────
            // Comanda de um caixa que já fechou não se oferece no turno seguinte: o
            // cliente foi embora há horas e os itens entrariam na venda de outra pessoa.
            using (var cx = Banco.Abrir(arquivo))
            {
                checar(Rascunho.Ler(cx, "sessao-de-outro-turno") is null,
                    "rascunho de OUTRA sessão de caixa não é oferecido");
                checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM comanda_rascunho") == 0,
                    "e o rascunho velho não fica no banco esperando uma chance");
            }

            // ── CORTESIA (o abatimento da comanda) VOLTA JUNTO ───────────────
            using (var cx = Banco.Abrir(arquivo))
            {
                var cobertura = new Dictionary<string, int> { ["COMBO AMERICANO"] = 1 };
                Rascunho.Gravar(cx, sessao, op, itens, Dinheiro.DeReais(1.50m), "AD-XYZ123", cobertura);
            }
            SqliteConnection.ClearAllPools();
            using (var cx = Banco.Abrir(arquivo))
            {
                var r = Rascunho.Ler(cx, sessao.Id);
                checar(r?.CortesiaCodigo == "AD-XYZ123"
                       && r.CortesiaCobertura.TryGetValue("COMBO AMERICANO", out var n) && n == 1,
                    "a cortesia aplicada volta com o cupom e a cobertura por item");
                checar(r?.Desconto.Centavos == 150, "o desconto da comanda volta em centavos");
            }

            // ── TEF EM VOO NÃO É HERANÇA DO RASCUNHO ─────────────────────────
            // A cobrança que ficou armada na maquininha tem destino PRÓPRIO (órfã, pela
            // reconciliação). Se o rascunho restaurado trouxesse aquele pagamento junto,
            // a venda nova nasceria "paga" por um dinheiro que ninguém sabe se entrou.
            using (var cx = Banco.Abrir(arquivo))
            {
                checar(Caixa.CobrancaSemVenda(cx, sessao).Centavos == 0,
                    "turno sem cobrança nenhuma: não há dinheiro na maquininha para avisar");
                cx.Execute("""
                    INSERT INTO tef_transacao (id, venda_id, charge_id, identificacao, tipo, valor_cent,
                                               parcelas, situacao, criado_em, atualizado_em)
                    VALUES ('tef-1', NULL, 'chg-167602', '167602', 'credito', 50000, 1, 'aguardando', @Em, @Em)
                    """, new { Em = DateTime.Now.ToString("o") });
                Rascunho.Gravar(cx, sessao, op, itens, Dinheiro.Zero);
            }
            SqliteConnection.ClearAllPools();
            using (var cx = Banco.Abrir(arquivo))
            {
                var r = Rascunho.Ler(cx, sessao.Id);
                checar(r is not null, "o rascunho volta mesmo com um TEF em voo ao lado");

                var linha = cx.QueryFirst("SELECT venda_id, situacao FROM tef_transacao WHERE id='tef-1'");
                checar(linha.venda_id is null && (string)linha.situacao == "aguardando",
                    "restaurar o rascunho não encosta na cobrança de TEF em voo");

                // Trava de ESTRUTURA: ninguém consegue pendurar um pagamento no rascunho
                // depois, "só para não perder o cartão que já passou".
                var proibidos = typeof(ComandaRascunho).GetProperties()
                    .Select(p => p.Name.ToLowerInvariant())
                    .Where(n => n.Contains("tef") || n.Contains("pagamento") || n.Contains("charge")
                                || n.Contains("nsu") || n.Contains("aut"))
                    .ToArray();
                checar(proibidos.Length == 0,
                    $"o rascunho não tem CAMPO de pagamento para herdar ({string.Join(", ", proibidos)})");
            }

            // ── QUANTO A MAQUININHA COBROU SEM VENDA GRAVADA ─────────────────
            // A conta que o diálogo do rascunho precisa fazer antes de abrir a boca.
            // Ela NÃO pode ser "venda_id IS NULL": essa coluna nasce NULL e nenhum
            // caminho de produção a preenche (a linha do TEF é gravada antes de a
            // venda existir e nunca mais é amarrada a ela). Quem amarra venda e TEF
            // na loja é o NSU — sem isso, TODA venda de cartão do turno viraria
            // alarme e o aviso morreria de tanto gritar à toa.
            using (var cx = Banco.Abrir(arquivo))
            {
                // O caso que o teste antigo deixou de fora: cartão passado DE VERDADE
                // (linha 'pago', com NSU) e a venda ainda não gravada.
                InserirTef(cx, "tef-pago-sem-venda", "pago", 12345, nsu: "900500");
                var cobrado = Caixa.CobrancaSemVenda(cx, sessao);
                checar(cobrado.Centavos == 50000 + 12345,
                    $"cobrança sem venda soma o 'pago' e o 'aguardando' do turno: {cobrado.Formatado()}");

                // Venda de cartão CONCLUÍDA no mesmo turno: dinheiro e venda batem,
                // nada a avisar.
                var totalCartao = Dinheiro.DeReais(30m);
                Vendas.Finalizar(cx, sessao, op,
                    new[] { new LinhaVenda("p-1", "101", "COMBO AMERICANO", Quantidade.Um, totalCartao, totalCartao,
                                           "UN", "19053100", null, "102", null, 0) },
                    new[] { new PagamentoVenda("credito", totalCartao, Dinheiro.Zero, Aut: "770500", Nsu: "900777") },
                    null, "Loja", null);
                InserirTef(cx, "tef-vendido", "pago", totalCartao.Centavos, nsu: "900777");
                checar(Caixa.CobrancaSemVenda(cx, sessao).Centavos == cobrado.Centavos,
                    "cartão de venda CONCLUÍDA (amarrado pelo NSU) NÃO vira alarme de cobrança sem venda");

                // Estorno já feito: o dinheiro voltou, não se manda conferir de novo.
                InserirTef(cx, "tef-estornada", "estornada", 7777, nsu: "900888");
                InserirTef(cx, "tef-estorno", "estornado", 7777, nsu: "900889");
                checar(Caixa.CobrancaSemVenda(cx, sessao).Centavos == cobrado.Centavos,
                    "cobrança já ESTORNADA não entra no aviso (o dinheiro voltou)");

                // Recusado/cancelado não é dinheiro.
                InserirTef(cx, "tef-recusado", "recusado", 4321);
                InserirTef(cx, "tef-cancelado", "cancelado", 4321);
                checar(Caixa.CobrancaSemVenda(cx, sessao).Centavos == cobrado.Centavos,
                    "cartão recusado/cancelado não entra no aviso");

                // Turno anterior é problema do fechamento anterior, não deste caixa.
                InserirTef(cx, "tef-ontem", "pago", 9999, nsu: "900999",
                           em: sessao.AberturaEm.AddHours(-3));
                checar(Caixa.CobrancaSemVenda(cx, sessao).Centavos == cobrado.Centavos,
                    "cobrança de um turno anterior não entra no aviso deste caixa");

                // E a órfã do religamento (ControlPay/PayGo declaram assim) entra:
                // é o caso do passo 24 do roteiro, dinheiro possivelmente vivo.
                InserirTef(cx, "tef-orfa", "orfa", 2500);
                checar(Caixa.CobrancaSemVenda(cx, sessao).Centavos == cobrado.Centavos + 2500,
                    "cobrança ÓRFÃ (religamento) entra no aviso");

                // O rascunho continua na mesa para os blocos seguintes.
                Rascunho.Gravar(cx, sessao, op, itens, Dinheiro.Zero);
            }

            // ── O AVISO DO DIÁLOGO NÃO PODE MENTIR ───────────────────────────
            // A linha do TEF nasce ANTES da venda (Pagamento.xaml.cs: a cobrança sai
            // em CobrarNoTefAsync e `Vendas.Finalizar` só roda em ConcluirTudoAsync).
            // Existe, portanto, uma janela em que o cartão JÁ PASSOU e a venda ainda
            // não existe — e ela é EXATAMENTE a janela para a qual o rascunho foi
            // feito (queda de energia no meio do atendimento). Afirmar ali "Nada foi
            // cobrado" tranquiliza o operador na hora em que ele tinha que desconfiar,
            // e o cliente paga duas vezes.
            {
                string? fonte = null;
                for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                {
                    var alvo = Path.Combine(d.FullName, "Telas", "Venda.xaml.cs");
                    if (File.Exists(alvo)) { fonte = File.ReadAllText(alvo); break; }
                }
                checar(fonte is not null, "achei a fonte da tela de venda para conferir o texto do diálogo");

                var i = fonte?.IndexOf("private void OferecerRascunho()", StringComparison.Ordinal) ?? -1;
                var fim = i < 0 ? -1 : fonte!.IndexOf("\n    private ", i + 1, StringComparison.Ordinal);
                var corpo = i < 0 ? "" : (fim > i ? fonte![i..fim] : fonte![i..]);
                checar(corpo.Contains("CobrancaSemVenda", StringComparison.Ordinal),
                    "o diálogo do rascunho confere a cobrança sem venda do turno antes de falar");
                checar(!corpo.Contains("nenhum valor foi cobrado", StringComparison.Ordinal),
                    "a frase do dinheiro não é escrita solta na tela: sai de Rascunho.AvisoDeCobranca");

                // ── LINGUAGEM DA TELA (pedido do dono 28/08) ──────────────────
                // O atendente lê isto no meio do atendimento: nada de jargão nem de
                // plural entre parênteses.
                checar(!corpo.Contains("item(ns)", StringComparison.Ordinal)
                       && !corpo.Contains("linha(s)", StringComparison.Ordinal),
                    "o diálogo não mostra plural automático entre parênteses");
                foreach (var jargao in new[] { "venda gravada", "registro", "persistência", "transação" })
                    checar(!corpo.Contains(jargao, StringComparison.OrdinalIgnoreCase),
                        $"o diálogo não usa o termo técnico \"{jargao}\"");
                checar(corpo.Contains("Continuar comanda", StringComparison.Ordinal)
                       && corpo.Contains("Descartar comanda", StringComparison.Ordinal),
                    "os botões dizem a ação: Continuar comanda / Descartar comanda");

                // E a frase, agora que dá para testá-la, é testada pelo valor.
                var quieto = Rascunho.AvisoDeCobranca(Dinheiro.Zero);
                checar(quieto.Contains("nenhum valor foi cobrado", StringComparison.Ordinal),
                    "sem cobrança viva no turno o aviso tranquiliza: nenhum valor foi cobrado");

                var cincoCentos = Dinheiro.DeReais(500m);
                var alarme = Rascunho.AvisoDeCobranca(cincoCentos);
                checar(!alarme.Contains("nenhum valor foi cobrado", StringComparison.Ordinal),
                    "com cartão passado e venda não finalizada o aviso NÃO diz que nada foi cobrado");
                checar(alarme.Contains(cincoCentos.Formatado(), StringComparison.Ordinal),
                    $"o aviso mostra QUANTO está cobrado sem venda ({cincoCentos.Formatado()})");
                checar(alarme.Contains("NÃO cobre de novo", StringComparison.Ordinal),
                    "o aviso manda NÃO cobrar de novo — é a cobrança em dobro que se quer evitar");

                var cego = Rascunho.AvisoDeCobranca(null);
                checar(!cego.Contains("nenhum valor foi cobrado", StringComparison.Ordinal),
                    "sem conseguir conferir a maquininha, o aviso não afirma que nada foi cobrado");

                // ── PLURAL: "1 item" / "2 itens", nunca "item(ns)" ────────────
                checar(Rascunho.TextoItens(1m) == "1 item", "uma unidade fica no singular");
                checar(Rascunho.TextoItens(2m) == "2 itens", "duas unidades vão pro plural");
                checar(Rascunho.TextoItens(0m) == "0 itens", "zero é plural em português");
                checar(Rascunho.TextoItens(1.5m) == "1,5 itens" || Rascunho.TextoItens(1.5m) == "1.5 itens",
                    "item pesado (fração de quilo) também é plural");
                foreach (var q in new[] { 0m, 1m, 2m, 1.5m, 10m })
                    checar(!Rascunho.TextoItens(q).Contains("(", StringComparison.Ordinal),
                        $"o texto de {q} unidades não tem parênteses de plural");
            }

            // ── VENDA FINALIZADA MATA O RASCUNHO ─────────────────────────────
            // Rascunho órfão que ressuscita depois da venda paga é pior que rebipar:
            // o operador cobra duas vezes os mesmos itens sem perceber.
            using (var cx = Banco.Abrir(arquivo))
            {
                var total = Dinheiro.DeReais(54.14m);
                Vendas.Finalizar(cx, sessao, op,
                    new[] { new LinhaVenda("p-1", "101", "COMBO AMERICANO", Quantidade.Um, total, total,
                                           "UN", "19053100", null, "102", null, 0) },
                    new[] { new PagamentoVenda("dinheiro", total, Dinheiro.Zero) },
                    null, "Loja", null);
            }
            SqliteConnection.ClearAllPools();
            using (var cx = Banco.Abrir(arquivo))
                checar(Rascunho.Ler(cx, sessao.Id) is null && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM comanda_rascunho") == 0,
                    "venda finalizada apaga o rascunho (não ressuscita depois de pago)");

            // ── E NÃO RESSUSCITA NA JANELA DEPOIS DO COMMIT ──────────────────
            // O bloco acima cobre o caminho SÍNCRONO: `Vendas.Finalizar` apaga o
            // rascunho dentro da transação da venda. Só que a tela continua viva
            // depois do commit — a NFC-e pode levar 25 s, a impressão vem depois e,
            // com a bobina entalada, a tela FICA parada no "Reimprimir" até alguém
            // tocar. `_comanda` só é esvaziada no fim disso tudo (handler `Encerrou`).
            //
            // Nessa janela, um push de catálogo do painel (Sino.CatalogoMudou, thread
            // de fundo, sem operador nenhum) desce por RecarregarCatalogo →
            // CarregarCatalogo → PintarComanda → SalvarRascunho e regrava no disco a
            // comanda de uma venda JÁ PAGA. Queda de energia ali: religa, o diálogo
            // afirma que nenhuma venda foi gravada — mentira — e o operador restaura
            // e cobra o cliente de novo. É o dano que o rascunho existe para evitar.
            using (var cx = Banco.Abrir(arquivo))
            {
                Rascunho.Gravar(cx, sessao, op, itens, Dinheiro.Zero);
                var total = Dinheiro.DeReais(54.14m);
                Vendas.Finalizar(cx, sessao, op,
                    new[] { new LinhaVenda("p-1", "101", "COMBO AMERICANO", Quantidade.Um, total, total,
                                           "UN", "19053100", null, "102", null, 0) },
                    new[] { new PagamentoVenda("dinheiro", total, Dinheiro.Zero) },
                    null, "Loja", null);
                checar(Rascunho.Ler(cx, sessao.Id) is null,
                    "a venda paga apaga o rascunho na própria transação");

                // Exatamente o que PintarComanda() faz enquanto `_comanda` ainda tem
                // os itens vendidos: uma gravação a mais, sem operador nenhum.
                Rascunho.Gravar(cx, sessao, op, itens, Dinheiro.Zero);
            }
            SqliteConnection.ClearAllPools();
            using (var cx = Banco.Abrir(arquivo))
            {
                checar(Rascunho.Ler(cx, sessao.Id) is not null,
                    "SONDA: Rascunho.Gravar é primitivo e ressuscita a comanda paga — "
                  + "a trava tem que estar em QUEM chama");
                Rascunho.Apagar(cx);
            }

            // A trava, então, mora na tela — e se testa na fonte, do mesmo jeito que o
            // texto do diálogo acima. Enquanto o pagamento está no ar a comanda NÃO
            // muda (o PainelPagamento é Grid.RowSpan=3/ZIndex=10 e cobre a tela
            // inteira; nenhum caminho que mexe em `_comanda` é alcançável ali), então
            // não gravar nessa janela não perde bipe nenhum — só fecha a ressurreição.
            {
                var fonte = FonteDaTelaDeVenda();
                checar(fonte is not null, "achei a fonte da tela de venda para conferir a trava do rascunho");

                var i = fonte?.IndexOf("private void SalvarRascunho()", StringComparison.Ordinal) ?? -1;
                var fim = i < 0 ? -1 : fonte!.IndexOf("\n    private ", i + 1, StringComparison.Ordinal);
                var corpo = i < 0 ? "" : (fim > i ? fonte![i..fim] : fonte![i..]);
                checar(corpo.Length > 0, "achei o corpo de SalvarRascunho()");

                var guarda = corpo.IndexOf("PainelPagamento", StringComparison.Ordinal);
                var grava = corpo.IndexOf("Rascunho.Gravar", StringComparison.Ordinal);
                var linhaGuarda = guarda < 0 ? "" : corpo[guarda..corpo.IndexOf('\n', guarda)];
                checar(guarda >= 0 && grava > guarda && linhaGuarda.Contains("return", StringComparison.Ordinal),
                    "SalvarRascunho desiste com a tela de pagamento no ar (a venda pode já estar paga)");
            }

            // ── VENDA QUE NÃO FOI GRAVADA NÃO LEVA A COMANDA JUNTO ───────────
            using (var cx = Banco.Abrir(arquivo))
            {
                Rascunho.Gravar(cx, sessao, op, itens, Dinheiro.Zero);
                var total = Dinheiro.DeReais(54.14m);
                try
                {
                    Vendas.Finalizar(cx, sessao, op,
                        new[] { new LinhaVenda("p-1", "101", "COMBO", Quantidade.Um, total, total,
                                               "UN", "19053100", null, "102", null, 0) },
                        // paga menos do que a venda: Finalizar recusa
                        new[] { new PagamentoVenda("dinheiro", Dinheiro.DeReais(10m), Dinheiro.Zero) },
                        null, "Loja", null);
                }
                catch (InvalidOperationException) { }
                checar(Rascunho.Ler(cx, sessao.Id) is not null,
                    "finalização RECUSADA não apaga o rascunho (a comanda continua na tela)");
            }

            // ── LIMPAR/CANCELAR A COMANDA TAMBÉM APAGA ───────────────────────
            using (var cx = Banco.Abrir(arquivo))
            {
                Rascunho.Apagar(cx);
                checar(Rascunho.Ler(cx, sessao.Id) is null, "limpar a comanda apaga o rascunho");

                // Tirar o último item é limpar a comanda: rascunho vazio não existe.
                Rascunho.Gravar(cx, sessao, op, Array.Empty<ItemRascunho>(), Dinheiro.Zero);
                checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM comanda_rascunho") == 0,
                    "comanda esvaziada não deixa rascunho vazio para trás");
            }

            // ── BARATO: É O CAMINHO QUENTE DO BALCÃO ─────────────────────────
            // Uma linha por caixa, sempre a mesma: o rascunho não pode virar tabela
            // que cresce a cada bipe do leitor de código de barras.
            using (var cx = Banco.Abrir(arquivo))
            {
                var comanda = new List<ItemRascunho>();
                var relogio = Stopwatch.StartNew();
                for (var i = 0; i < 200; i++)
                {
                    comanda.Add(new ItemRascunho($"p-{i}", $"{i}", $"ITEM {i}", "Cat", 990, 1000,
                                                 "UN", "19053100", null, "102", 0, null));
                    Rascunho.Gravar(cx, sessao, op, comanda, Dinheiro.Zero);
                }
                relogio.Stop();
                var porBipe = relogio.Elapsed.TotalMilliseconds / 200;
                checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM comanda_rascunho") == 1,
                    "200 bipes deixam UMA linha de rascunho (uma por caixa, não uma por bipe)");
                checar(porBipe < 25,
                    $"gravar o rascunho cabe no caminho quente: {porBipe:0.0} ms por bipe (teto 25 ms)");
                Rascunho.Apagar(cx);
            }

            // ── DOIS PDVs NA MESMA MÁQUINA: A COMANDA DE UM NÃO COME A DO OUTRO ──
            //
            // `Ler` só compara `sessao_id` — e sessão NÃO distingue dois Pdv.exe abertos
            // no mesmo terminal: os dois attacham no MESMO turno (MainWindow.Roteia →
            // `Caixa.SessaoAberta`). A linha é uma só (`id = 1`), então o segundo grava
            // por cima do primeiro e `Ler` devolve a mesma comanda para os dois.
            //
            // A trava de instância única fecha o cenário comum, mas ela DESISTE em
            // silêncio: `Global\` negado cai para `Local\`, que vale só para UMA sessão
            // do Windows, e se nem `Local\` sair o caixa abre sem guarda nenhuma
            // (`InstanciaUnica.Escopo == ""`, de propósito — loja parada é pior). O
            // rascunho não pode DEPENDER da trava para não destruir comanda viva.
            //
            // O estrago é o defeito 4 ao contrário: em vez de devolver a digitação
            // perdida, devolve a comanda de outra tela — ou apaga a que está em uso, em
            // silêncio (`SalvarRascunho` engole tudo num `catch { }`).
            using (var outroPdv = SubirSegundoPdv(arquivo, sessao.Id, op.Id, "COXINHA DO OUTRO PDV"))
            {
                checar(outroPdv is not null, "sonda: subiu um 2º PDV segurando a comanda dele");
                SqliteConnection.ClearAllPools();

                using (var cx = Banco.Abrir(arquivo))
                {
                    checar(ItemNoDisco(cx) == "COXINHA DO OUTRO PDV",
                        $"sonda: a comanda no disco é a do 2º PDV (é '{ItemNoDisco(cx)}')");

                    // 1) GRAVAR — a minha comanda não pode comer a dele.
                    Rascunho.Gravar(cx, sessao, op, itens, Dinheiro.Zero);
                    checar(ItemNoDisco(cx) == "COXINHA DO OUTRO PDV",
                        $"gravar não sobrescreve a comanda de um PDV VIVO (ficou '{ItemNoDisco(cx)}')");

                    // 2) LER — a comanda dele não é minha para recuperar. Restaurar aqui
                    //    põe na minha tela itens que o cliente do OUTRO caixa está pedindo.
                    checar(Rascunho.Ler(cx, sessao.Id) is null,
                        "não recupero a comanda de um PDV VIVO, mesmo com o turno igual");
                    checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM comanda_rascunho") == 1,
                        "e a tentativa de ler não apaga a comanda viva dele");

                    // 3) LER COM SESSÃO VELHA — o caso que APAGA comanda em uso. A tela
                    //    antiga segura o `_sessao` de um turno já fechado; hoje `Ler` vê
                    //    `sessao_id` diferente e dá DELETE na comanda do caixa aberto.
                    checar(Rascunho.Ler(cx, "sessao-de-um-turno-fechado") is null,
                        "com a sessão velha eu continuo sem recuperar nada");
                    checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM comanda_rascunho") == 1,
                        "ler com a sessão VELHA não destrói a comanda do PDV que está de pé");

                    // 4) APAGAR — limpar a MINHA comanda (ou finalizar a MINHA venda) não
                    //    pode levar junto a comanda que o outro PDV está digitando.
                    Rascunho.Apagar(cx);
                    checar(ItemNoDisco(cx) == "COXINHA DO OUTRO PDV",
                        $"apagar o meu rascunho não apaga o do PDV vivo (ficou '{ItemNoDisco(cx)}')");
                }

                // 5) QUEDA DE ENERGIA NAQUELE PROCESSO: aí sim a comanda é para recuperar.
                //    É o defeito 4, e ele continua de pé — a regra é "outro PDV VIVO",
                //    não "outro PDV".
                try { outroPdv?.Kill(true); outroPdv?.WaitForExit(15_000); } catch { }
                SqliteConnection.ClearAllPools();
                using (var cx = Banco.Abrir(arquivo))
                {
                    var r = Rascunho.Ler(cx, sessao.Id);
                    checar(r?.Itens.Count == 1 && r.Itens[0].Nome == "COXINHA DO OUTRO PDV",
                        "morto o outro processo (queda de energia), a comanda dele VOLTA — o defeito 4 continua resolvido");
                    Rascunho.Apagar(cx);
                    checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM comanda_rascunho") == 0,
                        "e apagar volta a funcionar quando não há outro PDV de pé");
                }
            }

            // ── A COLUNA NOVA NUM BANCO QUE JÁ EXISTE ────────────────────────
            // `dono` nasceu depois da tabela. Máquina que já rodou o build sem ela tem
            // `comanda_rascunho` sem a coluna — e o CREATE IF NOT EXISTS não alcança
            // tabela que já está lá. Se o ALTER não pegar, o primeiro bipe depois da
            // atualização derruba a gravação do rascunho ("no such column: dono") e o
            // caixa volta a perder a comanda na queda de energia, em silêncio.
            //
            // E a comanda deixada pelo build ANTIGO (sem dono) tem que continuar
            // voltando: quem atualiza o PDV com uma comanda no disco não pode perdê-la.
            {
                var velho = Path.Combine(Path.GetTempPath(), $"rascunho_velho_{Guid.NewGuid():N}.db");
                try
                {
                    Banco.Migrar(velho);
                    using (var cx = Banco.Abrir(velho))
                    {
                        Operadores.Salvar(cx, op.Id, op.Nome, "1357", "operador");
                        var s = Caixa.Abrir(cx, op, Dinheiro.DeReais(200));
                        Rascunho.Gravar(cx, s, op, itens, Dinheiro.Zero);
                        // Volta ao esquema de antes: linha gravada, sem dono nenhum.
                        cx.Execute("ALTER TABLE comanda_rascunho DROP COLUMN dono");
                        checar(cx.ExecuteScalar<int>(
                            "SELECT COUNT(*) FROM pragma_table_info('comanda_rascunho') WHERE name='dono'") == 0,
                            "sonda: banco no esquema antigo, com comanda gravada e sem a coluna dono");
                    }
                    SqliteConnection.ClearAllPools();

                    Banco.Migrar(velho);   // é o que a loja roda no boot depois de atualizar
                    using (var cx = Banco.Abrir(velho))
                    {
                        checar(cx.ExecuteScalar<int>(
                            "SELECT COUNT(*) FROM pragma_table_info('comanda_rascunho') WHERE name='dono'") == 1,
                            "atualizar o PDV acrescenta a coluna dono na tabela que já existia");

                        var s = Caixa.SessaoAberta(cx)!;
                        var r = Rascunho.Ler(cx, s.Id);
                        checar(r?.Itens.Count == 3,
                            $"a comanda deixada pelo build ANTIGO (dono nulo) continua voltando (voltaram {r?.Itens.Count ?? 0})");

                        Rascunho.Gravar(cx, s, op, itens, Dinheiro.Zero);
                        checar(cx.ExecuteScalar<string?>("SELECT dono FROM comanda_rascunho WHERE id = 1") is { Length: > 0 },
                            "e o primeiro bipe depois da atualização já carimba o dono");
                    }
                }
                finally
                {
                    SqliteConnection.ClearAllPools();
                    try { File.Delete(velho); } catch { }
                }
            }
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }

    /// <summary>O nome do único item da comanda que está no disco (ou "(vazio)").</summary>
    private static string ItemNoDisco(SqliteConnection cx)
    {
        var j = cx.ExecuteScalar<string?>("SELECT itens_json FROM comanda_rascunho WHERE id = 1");
        if (j is null) return "(vazio)";
        var itens = System.Text.Json.JsonSerializer.Deserialize<List<ItemRascunho>>(j);
        return itens is { Count: > 0 } ? itens[0].Nome : "(vazio)";
    }

    /// <summary>
    /// Um 2º Pdv.exe DE VERDADE, com comanda em andamento e vivo. Tem que ser outro
    /// PROCESSO: o furo é justamente que nada na linha do rascunho diz de quem ela é, e
    /// duas chamadas no mesmo processo não provariam nada. Volta quando a comanda dele
    /// já está gravada (o filho anuncia "ok").
    /// </summary>
    private static Process? SubirSegundoPdv(string banco, string sessaoId, string operadorId, string item)
    {
        var exe = Environment.ProcessPath ?? "dotnet";
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // `dotnet Pdv.Testes.dll` em vez do apphost: o .dll entra como 1º argumento.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "");
        psi.ArgumentList.Add("--sonda-rascunho");
        psi.ArgumentList.Add(banco);
        psi.ArgumentList.Add(sessaoId);
        psi.ArgumentList.Add(operadorId);
        psi.ArgumentList.Add(item);

        var p = Process.Start(psi);
        if (p is null) return null;
        if (p.StandardOutput.ReadLine() != "ok") { try { p.Kill(true); } catch { } p.Dispose(); return null; }
        return p;
    }

    /// <summary>A fonte da tela de venda, para travar o que só existe no code-behind.</summary>
    private static string? FonteDaTelaDeVenda()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var alvo = Path.Combine(d.FullName, "Telas", "Venda.xaml.cs");
            if (File.Exists(alvo)) return File.ReadAllText(alvo);
        }
        return null;
    }

    /// <summary>
    /// Uma linha de `tef_transacao` como a loja grava: `venda_id` NULL (nenhum caminho
    /// de produção preenche essa coluna) e o vínculo com a venda, quando existe, só
    /// pelo NSU.
    /// </summary>
    private static void InserirTef(SqliteConnection cx, string id, string situacao, long valorCent,
                                   string? nsu = null, DateTime? em = null)
        => cx.Execute("""
            INSERT INTO tef_transacao (id, venda_id, charge_id, provedor, identificacao, tipo,
                                       valor_cent, parcelas, situacao, nsu, criado_em, atualizado_em)
            VALUES (@Id, NULL, @Id, 'controlpay', @Id, 'credito', @V, 1, @S, @Nsu, @Em, @Em)
            """,
            new { Id = id, V = valorCent, S = situacao, Nsu = nsu,
                  Em = (em ?? DateTime.Now).ToString("o") });
}
