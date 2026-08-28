using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// Testes da fila de preparo (o monitor touch ao lado do forno).
///
/// O que quebra aqui não custa dinheiro direto, custa PEDIDO: card duplicado faz
/// a loja produzir duas vezes; card que some faz o cliente esperar para sempre;
/// e carimbo de tempo reescrito faz o painel do dono mentir sobre a operação.
/// </summary>
public static class TestesKds
{
    public static void Rodar(Action<bool, string> checar)
    {
        // ── COMANDA DA COZINHA ─────────────────────────────
        // O papel que sai na cozinha é lido de longe, em pé e com pressa. O que não
        // pode faltar: o que produzir (inclusive o CONTEÚDO do combo), a observação
        // do cliente e um quadradinho para conferir item a item antes de fechar a
        // sacola. O JSON aqui é o shape real de monta_itens_v2.
        {
            // shape do TicketItem (e o que fica gravado em kds_ticket.itens_json,
            // ja normalizado por ItensDeJson na entrada)
            var json = """
            [
              {"Descricao":"Combo Box 4un","Qtd":1000,"Observacao":null,
               "Escolhas":["Clássicos: 2x Donut Ninho","Premium: 2x Donut Nutella"]},
              {"Descricao":"Cookie Duplo","Qtd":3000,"Observacao":"sem castanha"}
            ]
            """;
            var t0 = new Ticket("t1", "balcao", "r1", "CD-2246", "Asani Vasconcelos", json,
                Kds.Recebido, new DateTime(2026, 8, 28, 17, 37, 0), null, null);

            var linhas = Kds.ComandaLinhas(t0);
            var limpas = linhas.Select(LinhaEscala.Limpa).ToList();
            var puro = string.Join("\n", limpas);

            // 1. o conteúdo do combo TEM que sair: sem isto a cozinha lê
            //    "1x Combo Box 4un" e não tem o que produzir.
            checar(puro.Contains("2x Donut Ninho", StringComparison.Ordinal)
                   && puro.Contains("2x Donut Nutella", StringComparison.Ordinal),
                "a comanda mostra o que o cliente montou dentro do combo");
            checar(puro.Contains("Clássicos:", StringComparison.Ordinal),
                "cada escolha vem com o grupo, para a cozinha separar");

            // 2. observação do cliente
            checar(puro.Contains("sem castanha", StringComparison.Ordinal),
                "a observação do cliente sai na comanda");

            // 3. quadradinho de conferência em CADA item (e só nos itens)
            // Quadradinho no item E em cada sabor do combo: quem monta a caixa
            // confere donut a donut. 2 itens + 2 sabores = 4.
            var comBox = limpas.Count(l => l.Contains("[ ]", StringComparison.Ordinal));
            checar(comBox == 4, $"quadradinho no item E em cada sabor do combo (achei {comBox}, esperado 4)");
            var linhaSabor = limpas.FirstOrDefault(l => l.Contains("Donut Ninho", StringComparison.Ordinal));
            checar(linhaSabor is not null && linhaSabor.Contains("[ ]", StringComparison.Ordinal),
                "o sabor do combo tem o proprio quadradinho");

            // 4. "Impresso" saiu — a cozinha usa a hora que o pedido CHEGOU
            checar(!puro.Contains("Impresso", StringComparison.OrdinalIgnoreCase),
                "a comanda não mostra mais a hora da impressão");
            checar(puro.Contains("17:37", StringComparison.Ordinal),
                "a hora de chegada continua na comanda");

            // 5. TAMANHOS: número do pedido maior que o item, item maior que o corpo
            double EscalaDe(string trecho)
            {
                foreach (var l in linhas)
                {
                    var (txt, esc) = LinhaEscala.Le(l);
                    if (txt.Contains(trecho, StringComparison.Ordinal)) return esc;
                }
                return -1;
            }
            var eNumero = EscalaDe("PEDIDO");
            var eItem = EscalaDe("Cookie Duplo");
            var eEscolha = EscalaDe("Donut Ninho");
            checar(eNumero > eItem, $"o número do pedido é o maior da comanda ({eNumero} vs {eItem})");
            checar(eItem > 1.0, $"os itens saem maiores que o corpo ({eItem})");
            checar(eEscolha > 1.0 && eEscolha < eItem,
                $"a escolha do combo fica menor que o item e maior que o corpo ({eEscolha})");

            // 6. a marca de escala é INVISÍVEL para quem só lê o texto
            checar(!puro.Contains(LinhaEscala.Marca),
                "a marca de tamanho nunca aparece no texto lido");
            var (txtSem, escSem) = LinhaEscala.Le("linha sem marca");
            checar(txtSem == "linha sem marca" && escSem == 1.0,
                "linha sem marca volta inteira, escala 1");
        }


        var arquivo = Path.Combine(Path.GetTempPath(), $"kds_teste_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);

                // ── LARGURA DO CARD: O GUARD DE NaN ───────────────────
            // WrapPanel.ItemWidth nasce NaN. Toda comparacao com NaN e FALSA,
            // entao "Math.Abs(ItemWidth - w) > 0.5" nunca disparava e a largura
            // NUNCA era aplicada: os cards ficavam com a largura natural de cada
            // um e cabiam 4 por linha em vez de 2, desalinhados. Este teste trava
            // a regra do calculo (a chamada do WPF nao roda aqui).
            {
                bool PrecisaAplicar(double atual, double alvo)
                    => double.IsNaN(atual) || Math.Abs(atual - alvo) > 0.5;

                checar(PrecisaAplicar(double.NaN, 300),
                    "valor NaO definido (NaN) TEM que ser aplicado — era o bug dos 4 cards por linha");
                checar(!PrecisaAplicar(300, 300),
                    "valor ja correto nao e reescrito (evita layout em loop)");
                checar(PrecisaAplicar(280, 300),
                    "largura mudou (janela redimensionada) -> aplica de novo");
                checar(!PrecisaAplicar(300.2, 300),
                    "diferenca de arredondamento nao conta como mudanca");
            }

        // ── CARD FANTASMA: PEDIDO VELHO TEM QUE SAIR DO QUADRO ────
            // Aconteceu de verdade: pedido de 22/08 ainda no quadro em 28/08, já
            // cancelado no iFood, mostrando "+9847 min". A expiração só olhava
            // 'recebido' — quem chegou a 'preparando'/'pronto' ficava para sempre.
            // Card morto que não sai faz o operador desconfiar do quadro inteiro.
            {
                using var cx = Banco.Abrir(arquivo);
                cx.Execute("DELETE FROM kds_ticket");
                var velho = DateTime.Now.AddDays(-6).ToString("o");
                var agora = DateTime.Now.ToString("o");
                foreach (var (id, st, quando) in new[]
                {
                    ("f-pronto-velho",    Kds.Pronto,     velho),
                    ("f-preparo-velho",   Kds.Preparando, velho),
                    ("f-recebido-velho",  Kds.Recebido,   velho),
                    ("f-pronto-agora",    Kds.Pronto,     agora),
                    ("f-preparo-agora",   Kds.Preparando, agora),
                })
                    cx.Execute(
                        @"INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json, status, criado_em)
                          VALUES (@i,'ifood',@i,@i,'x','[]',@s,@q)",
                        new { i = id, s = st, q = quando });

                Kds.SincronizarDelivery(Array.Empty<PedidoDelivery>());

                string Status(string id) => cx.ExecuteScalar<string>(
                    "SELECT status FROM kds_ticket WHERE id=@i", new { i = id })!;

                checar(Status("f-pronto-velho") == Kds.Cancelado,
                    "pedido PRONTO de 6 dias atrás sai do quadro (era o fantasma do +9847 min)");
                checar(Status("f-preparo-velho") == Kds.Cancelado,
                    "pedido EM PREPARO de 6 dias atrás também sai");
                checar(Status("f-recebido-velho") == Kds.Cancelado,
                    "pedido RECEBIDO velho continua saindo (regra das 4h, intacta)");
                checar(Status("f-pronto-agora") == Kds.Pronto,
                    "pedido pronto de AGORA fica — o teto não pode varrer o trabalho do dia");
                checar(Status("f-preparo-agora") == Kds.Preparando,
                    "pedido em preparo de AGORA fica: ninguém tira da tela o que está no forno");

                // Limpa o que este bloco semeou: os dois cards de AGORA continuam
                // vivos de propósito, e entrariam nos contadores dos testes abaixo.
                cx.Execute("DELETE FROM kds_ticket WHERE id LIKE 'f-%'");
            }


            var itens = new[] { new TicketItem("COOKIE TRIPLO", 2000, null),
                                new TicketItem("AGUA 500ML",    1000, "sem gelo") };

            // ── nasce o ticket ──────────────────────────────────────────────
            var t1 = Kds.DoDelivery("order-A", "1234", "Fulano", itens);
            checar(t1 is not null, "pedido de delivery vira ticket");
            checar(Kds.Pendentes() == 1, "um pedido pendente aparece no contador do botão");

            // ── o polling repete: NÃO pode virar dois cards ─────────────────
            var t1b = Kds.DoDelivery("order-A", "1234", "Fulano", itens);
            checar(t1b == t1, "o mesmo pedido chegando de novo devolve o MESMO ticket");
            checar(Kds.Pendentes() == 1, "pedido repetido não duplica card na tela de produção");

            // ── pedido sem item não ocupa a tela ────────────────────────────
            var vazio = Kds.DoDelivery("order-vazio", "9", null, Array.Empty<TicketItem>());
            checar(vazio is null, "pedido sem item nenhum não vira ticket");

            // ── a máquina de estados ────────────────────────────────────────
            checar(!Kds.Liberar(t1!), "não dá para liberar um pedido que ninguém assumiu");
            checar(Kds.Assumir(t1!), "primeiro toque assume a produção");
            checar(!Kds.Assumir(t1!), "assumir duas vezes não avança nem reescreve carimbo");

            var carimbo1 = Carimbo(t1!, "preparo_em");
            Kds.Assumir(t1!);
            checar(Carimbo(t1!, "preparo_em") == carimbo1,
                   "toque repetido preserva a hora em que a produção começou");

            checar(Kds.Liberar(t1!), "segundo toque libera o pedido");
            checar(!Kds.Liberar(t1!), "liberar duas vezes não reescreve a hora de saída");
            checar(Kds.Pendentes() == 0, "pedido pronto sai do contador do botão");

            // ── quadro: pronto fica na coluna de coleta até alguém levar ────
            var pronto = Kds.Abertos().FirstOrDefault(x => x.Id == t1);
            checar(pronto is not null && pronto.Status == Kds.Pronto,
                   "pedido pronto continua no quadro, aguardando coleta");
            checar(Carimbo(t1!, "pronto_em") is not null, "a hora de saída fica gravada");

            // ── desfazer o assumir (pegou o card errado) ────────────────────
            var t9 = Kds.DoDelivery("order-desfaz", "9999", null, itens);
            Kds.Assumir(t9!);
            checar(Kds.Desassumir(t9!), "desfazer devolve o pedido para A PREPARAR");
            checar(Carimbo(t9!, "preparo_em") is null,
                   "desfazer APAGA a hora de início — senão o tempo de preparo mente");
            checar(!Kds.Desassumir(t9!), "desfazer duas vezes não inventa transição");
            checar(!Kds.Liberar(t9!), "desfeito não pode ser liberado sem assumir de novo");
            checar(Kds.Assumir(t9!) && Carimbo(t9!, "preparo_em") is not null,
                   "assumir de novo grava hora NOVA de início");
            // tira o t9 do quadro: os testes seguintes contam a fila inteira
            Kds.Liberar(t9!);
            Kds.Entregar(t9!);

            checar(!Kds.Entregar("id-que-nao-existe"), "entregar ticket inexistente não finge sucesso");
            checar(Kds.Entregar(t1!), "toque na coleta marca como entregue");
            checar(!Kds.Entregar(t1!), "entregar duas vezes não reescreve o carimbo");
            checar(Kds.Abertos().All(x => x.Id != t1), "entregue sai do quadro");
            checar(Carimbo(t1!, "entregue_em") is not null, "a hora da coleta fica gravada");

            // ── venda de balcão vira ticket, e cancelada some ───────────────
            var vendaId = SemearVenda(arquivo);
            var aberta = SemearVenda(arquivo, status: "aberta", numero: 8);
            checar(Kds.DoBalcao(aberta) is null,
                   "venda ainda ABERTA nao manda produzir (cliente pode desistir antes de pagar)");
            var t2 = Kds.DoBalcao(vendaId);
            checar(t2 is not null, "venda de balcão fechada vira pedido para produzir");
            checar(Kds.Abertos().Any(x => x.Numero == "7"),
                   "o card mostra o número da venda, que é o que o operador grita");

            Kds.CancelarPorVenda(vendaId);
            checar(Kds.Pendentes() == 0, "venda cancelada tira o pedido da produção");

            // ── itens sobrevivem à ida e volta do JSON ──────────────────────
            var t3 = Kds.DoDelivery("order-B", "5678", "Sicrano", itens);
            var lido = Kds.Abertos().First(x => x.Id == t3);
            checar(lido.Itens.Count == 2, "os itens do pedido chegam na tela");
            checar(lido.Itens[1].Observacao == "sem gelo",
                   "a observação do cliente não se perde no caminho");
            checar(lido.Itens[0].Qtd == 2000, "quantidade em milésimos preserva 2 unidades");

            // ── o JSON do iFood, nos shapes que ele realmente tem ───────────
            var doIfood = Nucleo.Kds.ItensDeJson(
                "[{\"name\":\"COOKIE TRIPLO\",\"quantity\":2}," +
                "{\"nome\":\"AGUA 500ML\",\"qtd\":\"1\",\"observacao\":\"sem gelo\"}]");
            checar(doIfood.Count == 2, "parser aceita name/quantity E nome/qtd no mesmo pedido");
            checar(doIfood[0].Qtd == 2000, "quantity numerico vira milesimos");
            checar(doIfood[1].Qtd == 1000 && doIfood[1].Observacao == "sem gelo",
                   "qtd em string e observacao sobrevivem");
            checar(Nucleo.Kds.ItensDeJson("{nao-e-json").Count == 0,
                   "JSON quebrado devolve vazio, nao excecao");
            checar(Nucleo.Kds.ItensDeJson(null).Count == 0, "itens nulos devolvem vazio");

            // O shape que a ponte grava DE VERDADE em producao (medido 19/08/2026):
            var real = Nucleo.Kds.ItensDeJson(
                "[{\"qtd\": 1, \"descricao\": \"Donut Homer\", \"valor_unitario\": 21.9}," +
                "{\"qtd\": 2, \"descricao\": \"Donut Churros\", \"valor_unitario\": 21.9}]");
            checar(real.Count == 2 && real[0].Descricao == "Donut Homer" && real[1].Qtd == 2000,
                   "o shape REAL da ponte (qtd/descricao) vira card com nome e quantidade");

            // ── sincronizacao com a nuvem: dedup e cancelamento ─────────────
            var lote = new[]
            {
                new PedidoDelivery("nuvem-1", "0101", "Cliente A",
                    "[{\"name\":\"DONUT\",\"quantity\":3}]", "faturado"),
                new PedidoDelivery("nuvem-2", "0102", null,
                    "[{\"name\":\"COOKIE\",\"quantity\":1}]", "faturado"),
            };
            checar(Nucleo.Kds.SincronizarDelivery(lote) == 2, "dois pedidos novos = dois tickets");
            checar(Nucleo.Kds.SincronizarDelivery(lote) == 0,
                   "o MESMO lote de novo nao cria nada (polling repete, tela nao duplica)");

            var cancelado = new[] { new PedidoDelivery("nuvem-1", "0101", "Cliente A",
                "[{\"name\":\"DONUT\",\"quantity\":3}]", "cancelado") };
            Nucleo.Kds.SincronizarDelivery(cancelado);
            checar(!Nucleo.Kds.Abertos().Any(x => x.RefId == "nuvem-1"),
                   "pedido cancelado na nuvem sai da fila de preparo");
            checar(Nucleo.Kds.Abertos().Any(x => x.RefId == "nuvem-2"),
                   "cancelar um pedido nao derruba o vizinho");

            // ── re-sync CONSERTA card gravado com parser antigo ─────────────
            // (aconteceu de verdade: "(item sem nome)" ficou queimado no banco
            // local porque a criacao e idempotente e nada atualizava depois)
            var consertado = new[] { new PedidoDelivery("nuvem-2", "0102", "Cliente B",
                "[{\"qtd\": 1, \"descricao\": \"DONUT ABACAXI\"}]", "faturado") };
            Nucleo.Kds.SincronizarDelivery(consertado);
            var vivo = Nucleo.Kds.Abertos().First(x => x.RefId == "nuvem-2");
            checar(vivo.Itens[0].Descricao == "DONUT ABACAXI",
                   "re-sync atualiza os itens de ticket ainda nao pronto");
            checar(vivo.Cliente == "Cliente B", "re-sync atualiza o cliente");

            // ── o relogio conta da CHEGADA no iFood, nao da importacao ──────
            var chegada30 = DateTimeOffset.UtcNow.AddMinutes(-30).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz");
            Nucleo.Kds.SincronizarDelivery(new[] { new PedidoDelivery("nuvem-relogio", "0303", null,
                "[{\"qtd\":1,\"descricao\":\"DONUT\"}]", "faturado", chegada30) });
            var tRel = Nucleo.Kds.Abertos().First(x => x.RefId == "nuvem-relogio");
            checar(tRel.Espera.TotalMinutes is >= 29 and <= 31,
                $"pedido de 30 min atras nasce com espera ~30 min (mediu {(int)tRel.Espera.TotalMinutes})");
            // e o re-sync CONSERTA o relogio de ticket importado antes do fix
            using (var cxr = Banco.Abrir())
                cxr.Execute("UPDATE kds_ticket SET criado_em=@a WHERE ref_id='nuvem-relogio'",
                    new { a = DateTime.Now.ToString("o") });
            Nucleo.Kds.SincronizarDelivery(new[] { new PedidoDelivery("nuvem-relogio", "0303", null,
                "[{\"qtd\":1,\"descricao\":\"DONUT\"}]", "faturado", chegada30) });
            tRel = Nucleo.Kds.Abertos().First(x => x.RefId == "nuvem-relogio");
            checar(tRel.Espera.TotalMinutes >= 29,
                "re-sync corrige o relogio de card importado com hora errada");

            // ── Gestor despachou/concluiu: o quadro larga o pedido ──────────
            checar(Nucleo.Kds.Abertos().Any(x => x.RefId == "nuvem-relogio"),
                "antes do despacho o card esta no quadro");
            Nucleo.Kds.SincronizarDelivery(new[] { new PedidoDelivery("nuvem-relogio", "0303", null,
                "[{\"qtd\":1,\"descricao\":\"DONUT\"}]", "despachado", chegada30) });
            checar(Nucleo.Kds.Abertos().All(x => x.RefId != "nuvem-relogio"),
                "pedido DESPACHADO pelo Gestor some do quadro (a preparar -> cancelado)");

            // pronto aqui + concluido la = entregue (o tempo de preparo fica)
            var tPr = Kds.DoDelivery("nuvem-pronto-la", "0304", null,
                Nucleo.Kds.ItensDeJson("[{\"qtd\":1,\"descricao\":\"DONUT\"}]"));
            Kds.Assumir(tPr!); Kds.Liberar(tPr!);
            Nucleo.Kds.SincronizarDelivery(new[] { new PedidoDelivery("nuvem-pronto-la", "0304", null,
                "[{\"qtd\":1,\"descricao\":\"DONUT\"}]", "concluido", null) });
            using (var cxe = Banco.Abrir())
                checar(cxe.ExecuteScalar<string>(
                    "SELECT status FROM kds_ticket WHERE id=@id", new { id = tPr }) == "entregue",
                    "PRONTO aqui + concluido no Gestor = entregue (nao cancelado: foi produzido)");

            // ── PRONTO no Gestor: o card pula direto pra coluna de coleta ───
            Nucleo.Kds.SincronizarDelivery(new[] { new PedidoDelivery("gestor-pronto", "0500", null,
                "[{\"qtd\":1,\"descricao\":\"DONUT\"}]", "pronto", null, null) });
            var tg = Nucleo.Kds.Abertos().FirstOrDefault(x => x.RefId == "gestor-pronto");
            checar(tg is not null && tg.Status == Kds.Pronto,
                "pedido PRONTO no Gestor entra direto na coluna de coleta, nao em a-preparar");

            // e quem ja estava a-preparar aqui e ficou pronto LA, move
            Nucleo.Kds.SincronizarDelivery(new[] { new PedidoDelivery("gestor-pronto-2", "0501", null,
                "[{\"qtd\":1,\"descricao\":\"DONUT\"}]", "faturado", null, null) });
            Nucleo.Kds.SincronizarDelivery(new[] { new PedidoDelivery("gestor-pronto-2", "0501", null,
                "[{\"qtd\":1,\"descricao\":\"DONUT\"}]", "pronto", null, null) });
            checar(Nucleo.Kds.Abertos().First(x => x.RefId == "gestor-pronto-2").Status == Kds.Pronto,
                "a-preparar daqui + pronto no Gestor = move pra coleta");

            // ── prazo do iFood no relogio do card ───────────────────────────
            var prazoIso = DateTimeOffset.UtcNow.AddMinutes(12).ToString("yyyy-MM-dd'T'HH:mm:sszzz");
            Nucleo.Kds.SincronizarDelivery(new[] { new PedidoDelivery("com-prazo", "0502", null,
                "[{\"qtd\":1,\"descricao\":\"DONUT\"}]", "faturado", null, prazoIso) });
            var tp2 = Nucleo.Kds.Abertos().First(x => x.RefId == "com-prazo");
            checar(tp2.PrazoRestante is { } pr && pr.TotalMinutes is > 10 and <= 12,
                "o card conta o PRAZO do iFood (dueAt), o mesmo relogio do Gestor");
            checar(Nucleo.Kds.Abertos().First(x => x.RefId == "gestor-pronto-2").PrazoRestante is null,
                "pedido sem prazo continua no relogio decorrido");

            // ── reconciliacao de ORFAOS (o bug dos 12 cards presos) ─────────
            // ticket local aberto cujo pedido SAIU da janela do feed: quando a
            // nuvem diz 'concluido', o card tem que sumir — mesmo sem feed.
            var tOrfao = Kds.DoDelivery("orfao-1", "0600", null,
                Nucleo.Kds.ItensDeJson("[{\"qtd\":1,\"descricao\":\"DONUT\"}]"));
            checar(Nucleo.Kds.Abertos().Any(x => x.RefId == "orfao-1"), "orfao comeca no quadro");
            Nucleo.Kds.AplicarStatusDaNuvem("orfao-1", "concluido", null);
            checar(Nucleo.Kds.Abertos().All(x => x.RefId != "orfao-1"),
                "nuvem diz CONCLUIDO -> orfao some do quadro (era o furo dos 12 cards)");

            var tOrfao2 = Kds.DoDelivery("orfao-2", "0601", null,
                Nucleo.Kds.ItensDeJson("[{\"qtd\":1,\"descricao\":\"DONUT\"}]"));
            Nucleo.Kds.AplicarStatusDaNuvem("orfao-2", "pronto",
                DateTime.Now.AddMinutes(8));
            var o2 = Nucleo.Kds.Abertos().First(x => x.RefId == "orfao-2");
            checar(o2.Status == Kds.Pronto, "nuvem diz PRONTO -> orfao pula pra coleta");
            checar(o2.PrazoRestante is { } opz && opz.TotalMinutes is > 6 and <= 8,
                "a reconciliacao tambem preenche o prazo que faltava");
            Nucleo.Kds.AplicarStatusDaNuvem("orfao-2", "faturado", null);
            checar(Nucleo.Kds.Abertos().Any(x => x.RefId == "orfao-2"),
                "status ainda ativo ('faturado') NAO mexe no ticket");

            // ── PRONTO do delivery viaja pela OUTBOX (ponte dispara readyToPickup) ──
            var tIf = Kds.DoDelivery("order-ready-1", "7777", null,
                Nucleo.Kds.ItensDeJson("[{\"qtd\":1,\"descricao\":\"DONUT\"}]"));
            Kds.Assumir(tIf!);
            Kds.Liberar(tIf!);
            using (var cxq = Banco.Abrir())
            {
                checar(cxq.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM outbox WHERE tipo='kds_pronto' AND ref_id='order-ready-1'") == 1,
                    "liberar delivery enfileira o pronto pra nuvem");
                var antesBal = cxq.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM outbox WHERE tipo='kds_pronto'");
                var tBal = Kds.DoBalcao(SemearVenda(arquivo, numero: 77));
                if (tBal is not null) { Kds.Assumir(tBal); Kds.Liberar(tBal); }
                checar(cxq.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM outbox WHERE tipo='kds_pronto'") == antesBal,
                    "balcao pronto NAO avisa o iFood (nao e pedido de la)");
            }
            Kds.Entregar(tIf!);

            // ── sino (Realtime): protocolo puro ─────────────────────────────
            var quadro = RealtimeKds.MontarQuadro("realtime:kds:Loja X", "phx_join", "{\"a\":1}", "1");
            checar(quadro.Contains("\"topic\":\"realtime:kds:Loja X\"")
                && quadro.Contains("\"event\":\"phx_join\"")
                && quadro.Contains("\"ref\":\"1\""),
                "quadro phoenix sai com topico, evento e ref");
            checar(RealtimeKds.EhSino(
                "{\"event\":\"broadcast\",\"payload\":{\"event\":\"novo_pedido\",\"payload\":{\"order_id\":\"x\"}}}"),
                "toque de sino e reconhecido");
            checar(!RealtimeKds.EhSino("{\"event\":\"phx_reply\",\"payload\":{}}"),
                "resposta de join NAO e sino");

            // join confirmado/recusado (o zumbi da revisao adversarial)
            checar(RealtimeKds.JulgarJoin(
                "{\"event\":\"phx_reply\",\"ref\":\"7\",\"payload\":{\"status\":\"ok\"}}", "7") == true,
                "join aceito e reconhecido pelo ref");
            checar(RealtimeKds.JulgarJoin(
                "{\"event\":\"phx_reply\",\"ref\":\"7\",\"payload\":{\"status\":\"error\"}}", "7") == false,
                "join RECUSADO (token vencido) derruba a sessao em vez de virar zumbi");
            checar(RealtimeKds.JulgarJoin(
                "{\"event\":\"phx_reply\",\"ref\":\"9\",\"payload\":{\"status\":\"ok\"}}", "7") is null,
                "reply de OUTRO ref nao conta como confirmacao");
            checar(RealtimeKds.FechouCanal(
                "{\"topic\":\"realtime:kds:X\",\"event\":\"phx_close\",\"payload\":{}}", "realtime:kds:X"),
                "phx_close do nosso canal derruba a sessao (JWT venceu no meio)");
            checar(!RealtimeKds.FechouCanal(
                "{\"topic\":\"realtime:kds:OUTRA\",\"event\":\"phx_close\",\"payload\":{}}", "realtime:kds:X"),
                "phx_close de canal alheio nao derruba o nosso");

            // a fila drena o tipo novo: fonte unica vigia o filtro do SELECT
            checar(Drenagem.TiposComHandler.Contains("kds_pronto"),
                "kds_pronto esta no filtro da fila (era o furo: handler sem SELECT)");
            using (var cxf = Banco.Abrir())
            {
                var naFila = cxf.ExecuteScalar<int>(
                    $"SELECT COUNT(*) FROM outbox WHERE enviado_em IS NULL AND ref_id='order-ready-1' " +
                    $"AND tipo IN ('{string.Join("','", Drenagem.TiposComHandler)}')");
                checar(naFila == 1, "a linha kds_pronto e SELECIONAVEL pela varredura da fila");
            }

            // expiracao de 4h poupa quem esta EM PREPARO
            var tVelho = Kds.DoDelivery("order-velho-preparando", "0405", null,
                Nucleo.Kds.ItensDeJson("[{\"qtd\":1,\"descricao\":\"DONUT\"}]"));
            Kds.Assumir(tVelho!);
            using (var cxv = Banco.Abrir())
                cxv.Execute("UPDATE kds_ticket SET criado_em=@v WHERE id=@id",
                    new { v = DateTime.Now.AddHours(-5).ToString("o"), id = tVelho });
            Nucleo.Kds.SincronizarDelivery(Array.Empty<PedidoDelivery>());
            checar(Nucleo.Kds.Abertos().Any(x => x.Id == tVelho),
                "expiracao de 4h NAO cancela pedido que o cozinheiro ja assumiu");
            var tVelho2 = Kds.DoDelivery("order-velho-recebido", "0406", null,
                Nucleo.Kds.ItensDeJson("[{\"qtd\":1,\"descricao\":\"DONUT\"}]"));
            using (var cxv = Banco.Abrir())
                cxv.Execute("UPDATE kds_ticket SET criado_em=@v WHERE id=@id",
                    new { v = DateTime.Now.AddHours(-5).ToString("o"), id = tVelho2 });
            Nucleo.Kds.SincronizarDelivery(Array.Empty<PedidoDelivery>());
            checar(Nucleo.Kds.Abertos().All(x => x.Id != tVelho2),
                "expiracao de 4h limpa pedido de 5h que NINGUEM tocou");
            Kds.Liberar(tVelho!); Kds.Entregar(tVelho!);
            checar(!RealtimeKds.EhSino("lixo{{{"), "quadro ilegivel nao derruba o cliente");
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }

    private static string? Carimbo(string ticketId, string coluna)
    {
        using var cx = Banco.Abrir();
        return cx.QueryFirstOrDefault<string>(
            $"SELECT {coluna} FROM kds_ticket WHERE id = @id", new { id = ticketId });
    }

    private static string? _ses, _op;

    /// <summary>
    /// Venda mínima válida: operador -> sessão -> venda -> item (as FKs estão ligadas).
    /// A sessão é criada UMA vez: há índice único que só admite um caixa aberto,
    /// e é assim mesmo que a loja funciona.
    /// </summary>
    private static string SemearVenda(string arquivo, string status = "finalizada", int numero = 7)
    {
        using var cx = Banco.Abrir();
        var agora = DateTime.Now.ToString("o");
        var hoje = DateTime.Now.ToString("yyyy-MM-dd");
        var venda = Guid.NewGuid().ToString();

        if (_ses is null)
        {
            _op = Guid.NewGuid().ToString();
            _ses = Guid.NewGuid().ToString();
            cx.Execute(@"INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,atualizado)
                         VALUES (@op,'TESTE','x','y','operador',@t)", new { op = _op, t = agora });
            cx.Execute(@"INSERT INTO caixa_sessao (id,business_date,operador_id,operador_nome,abertura_em,fundo_troco_cent)
                         VALUES (@s,@d,@op,'TESTE',@t,0)", new { s = _ses, d = hoje, op = _op, t = agora });
        }

        cx.Execute(@"INSERT INTO venda (id,client_key,sessao_id,business_date,numero_local,operador_id,
                                        subtotal_cent,total_cent,status,criada_em)
                     VALUES (@v,@v,@s,@d,@num,@op,1390,1390,@st,@t)",
                   new { v = venda, s = _ses, d = hoje, op = _op, t = agora, num = numero, st = status });
        cx.Execute(@"INSERT INTO venda_item (id,venda_id,seq,descricao,qtd_milesimo,preco_cent,total_cent)
                     VALUES (@i,@v,1,'COOKIE TRIPLO',1000,1390,1390)",
                   new { i = Guid.NewGuid().ToString(), v = venda });
        return venda;
    }
}
