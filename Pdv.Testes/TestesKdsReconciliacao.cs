using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A RECONCILIAÇÃO DO QUADRO COM A NUVEM (04/09/2026).
///
/// O relato do dono: uma recuperação de 72 pedidos antigos do iFood gravou
/// linhas com recebido_em = agora. O quadro da loja encheu com 71 cards de
/// ontem, todos já concluídos, e só se limparia 4 horas depois pelo timer.
/// "não é melhor colocar uma sincronia com a api? vai ficar dando esse erro
/// pra sempre? imagina um SAAS o cliente reclamando".
///
/// A resposta: o feed do servidor virou ESPELHO do conjunto ABERTO e o exe
/// RECONCILIA. Tempo deixa de definir o quadro e vira cerca.
///
/// O que estes testes protegem, e por que cada um existe:
///  · a decisão virou função PURA (Classificar/Reconciliar): sem banco, sem
///    rede, com o "agora" injetado. Antes o defeito morava exatamente na
///    costura que nenhum teste alcançava, porque Nuvem é classe selada sem
///    interface;
///  · toda guarda é do tipo "na dúvida, o card FICA". Errar para o lado de um
///    card a mais custa um card a mais; errar para o outro lado é comida não
///    produzida com cliente esperando.
/// </summary>
public static class TestesKdsReconciliacao
{
    /// <summary>Nuvem de mentira: o feed e a resposta por pedido são cravados
    /// pelo teste, com a CONFIABILIDADE de cada um.</summary>
    private sealed class FeedFalso : IFeedKds
    {
        public bool FeedConfiavel = true;
        public List<PedidoDelivery> Pedidos = new();
        public bool StatusConfiavel = true;
        public Dictionary<string, string> Status = new(StringComparer.OrdinalIgnoreCase);
        public List<string> UltimoLotePerguntado = new();

        public Task<(bool Confiavel, List<PedidoDelivery> Pedidos)> FeedKdsAsync(
            string loja, int janelaMin = 45)
            => Task.FromResult((FeedConfiavel, FeedConfiavel ? Pedidos : new List<PedidoDelivery>()));

        public Task<(bool Confiavel, List<(string OrderId, string Status, string? PreparoAte)> Itens)>
            StatusKdsAsync(IReadOnlyList<string> orderIds)
        {
            UltimoLotePerguntado = orderIds.ToList();
            var r = new List<(string OrderId, string Status, string? PreparoAte)>();
            if (!StatusConfiavel) return Task.FromResult((false, r));
            foreach (var id in orderIds)
                if (Status.TryGetValue(id, out var st)) r.Add((id, st, null));
            return Task.FromResult((true, r));
        }
    }

    private static Ticket TicketLocal(string refId, string status, string origem = "ifood",
                                      DateTime? vistoEm = null)
        => new("id-" + refId, origem, refId, refId, null, "[]", status,
               DateTime.Now.AddMinutes(-30), null, null, null, false, false, null, null, vistoEm);

    private static FotoDaNuvem Foto(bool feedOk, IEnumerable<string>? abertos = null,
                                    bool statusOk = true,
                                    IDictionary<string, string>? status = null,
                                    bool loteCompleto = true)
        => new(feedOk,
               (abertos ?? Array.Empty<string>())
                   .Select(id => new PedidoDelivery(id, id, null, "[]", "aberto")).ToList(),
               statusOk,
               new Dictionary<string, string>(status ?? new Dictionary<string, string>(),
                                              StringComparer.OrdinalIgnoreCase),
               loteCompleto);

    public static void Rodar(Action<bool, string> checar)
    {
        // ═══════════════════════════════════════════════════════════════════
        //  1. Classificar: a tabela verdade inteira
        // ═══════════════════════════════════════════════════════════════════
        // As três últimas linhas ('faturado', vazio e palavra inventada) são o
        // TESTE VERMELHO do furo que criou os 71 cards: o ramo default do
        // sincronizador era CRIAR CARD, e 'faturado' é o estado que 100% das
        // linhas de ifood_orders têm no ingresso.
        {
            checar(Kds.Classificar("cancelado", Kds.Recebido) == DestinoDoCard.Cancelar,
                "cancelado + a preparar = cai do quadro");
            checar(Kds.Classificar("cancelado", Kds.Preparando) == DestinoDoCard.Cancelar,
                "cancelado + em preparo = cai do quadro");
            checar(Kds.Classificar("cancelado", Kds.Pronto) == DestinoDoCard.Manter,
                "cancelado + JA PRONTO = fica (o produto existe, a divergencia e de gente)");

            checar(Kds.Classificar("despachado", Kds.Pronto) == DestinoDoCard.Entregue,
                "despachado + pronto = entregue (o tempo de preparo continua valendo)");
            checar(Kds.Classificar("concluido", Kds.Pronto) == DestinoDoCard.Entregue,
                "concluido + pronto = entregue");
            checar(Kds.Classificar("despachado", Kds.Recebido) == DestinoDoCard.Cancelar,
                "despachado + a preparar = cancelado (nunca foi produzido AQUI)");
            checar(Kds.Classificar("concluido", Kds.Preparando) == DestinoDoCard.Cancelar,
                "concluido + em preparo = cancelado");

            checar(Kds.Classificar("pronto", Kds.Recebido) == DestinoDoCard.ParaColeta,
                "pronto no Gestor = pula pra coluna de coleta");
            checar(Kds.Classificar("pronto", Kds.Pronto) == DestinoDoCard.ParaColeta,
                "pronto + pronto e idempotente");

            checar(Kds.Classificar("aberto", Kds.Recebido) == DestinoDoCard.Manter,
                "ABERTO (palavra nova do servidor) = o card FICA");
            checar(Kds.Classificar("faturado", Kds.Recebido) == DestinoDoCard.Manter,
                "FATURADO = o card FICA (era o furo: 'faturado' e o estado de toda linha)");
            checar(Kds.Classificar("FATURADO", Kds.Recebido) == DestinoDoCard.Manter,
                "FATURADO em caixa alta tambem fica");
            checar(Kds.Classificar("", Kds.Recebido) == DestinoDoCard.Manter,
                "string VAZIA = o card FICA");
            checar(Kds.Classificar(null, Kds.Recebido) == DestinoDoCard.Manter,
                "estado nulo = o card FICA");
            checar(Kds.Classificar("palavra_que_ninguem_previu", Kds.Recebido) == DestinoDoCard.Manter,
                "palavra DESCONHECIDA = o card FICA (lista negra, nunca lista branca)");
            checar(Kds.Classificar("  CANCELADO  ", Kds.Recebido) == DestinoDoCard.Cancelar,
                "espaco e caixa alta nao escondem um cancelamento");
            checar(Kds.Classificar("recebido", "") == DestinoDoCard.Manter
                   && Kds.Classificar("pago", "") == DestinoDoCard.Manter,
                "recebido e pago (cardapio digital) sao abertos");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  2. G1 — feed nao confiavel NAO derruba nada
        // ═══════════════════════════════════════════════════════════════════
        // Este e o teste que tem que existir antes de todos os outros: uma queda
        // de wi-fi de 30 segundos nao pode limpar o quadro com a cozinha cheia.
        {
            var locais = Enumerable.Range(1, 71)
                .Select(i => TicketLocal("inc-" + i, Kds.Recebido)).ToList();

            var semRede = Kds.Reconciliar(locais, Foto(feedOk: false), DateTime.Now);
            checar(semRede.Count == 0,
                "G1: feed NAO confiavel com 71 cards abertos = ZERO mudancas");

            // e a diferenca que o tipo carrega: vazio por SUCESSO e outra coisa
            var vazioPorSucesso = Kds.Reconciliar(locais, Foto(feedOk: true), DateTime.Now);
            checar(vazioPorSucesso.Count == 71,
                $"feed vazio por SUCESSO e diferente de vazio por FALHA (mediu {vazioPorSucesso.Count})");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  3. G2 — ausencia nao fecha sozinha: ela PERGUNTA
        // ═══════════════════════════════════════════════════════════════════
        {
            var locais = new[]
            {
                TicketLocal("orf-concluido", Kds.Recebido),
                TicketLocal("orf-aberto",    Kds.Recebido),
                TicketLocal("orf-pronto",    Kds.Recebido),
                TicketLocal("orf-sem-resposta", Kds.Recebido),
            };
            var foto = Foto(feedOk: true, abertos: null, statusOk: true,
                status: new Dictionary<string, string>
                {
                    ["orf-concluido"] = "concluido",
                    ["orf-aberto"]    = "aberto",
                    ["orf-pronto"]    = "pronto",
                });
            var m = Kds.Reconciliar(locais, foto, DateTime.Now).ToDictionary(x => x.RefId, x => x.Destino);

            checar(m.TryGetValue("orf-concluido", out var d1) && d1 == DestinoDoCard.Cancelar,
                "G2: orfao com CONCLUIDO explicito sai do quadro");
            checar(!m.ContainsKey("orf-aberto"),
                "G2: orfao que a nuvem diz ABERTO fica (pedido velho e VIVO, fora do horizonte)");
            checar(m.TryGetValue("orf-pronto", out var d2) && d2 == DestinoDoCard.ParaColeta,
                "G2: orfao PRONTO vai pra coluna de coleta");
            checar(m.TryGetValue("orf-sem-resposta", out var d3) && d3 == DestinoDoCard.Cancelar,
                "G2: perguntado com sucesso e NAO devolvido = a nuvem nao conhece = sai");

            // a mesma foto, agora com a pergunta QUEBRADA
            var fotoRuim = Foto(feedOk: true, abertos: null, statusOk: false);
            checar(Kds.Reconciliar(locais, fotoRuim, DateTime.Now).Count == 0,
                "G2: StatusConfiavel = false mantem TUDO (ausencia sem pergunta nao fecha nada)");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  4. G3 — 'preparando' e comida no forno
        // ═══════════════════════════════════════════════════════════════════
        {
            var emPreparo = new[] { TicketLocal("forno", Kds.Preparando) };

            checar(Kds.Reconciliar(emPreparo, Foto(true), DateTime.Now).Count == 0,
                "G3: em preparo, ausente do feed, sem resposta = FICA");
            checar(Kds.Reconciliar(emPreparo, Foto(true, statusOk: false), DateTime.Now).Count == 0,
                "G3: em preparo com a pergunta quebrada = FICA");
            checar(Kds.Reconciliar(emPreparo, Foto(true, loteCompleto: false), DateTime.Now).Count == 0,
                "G3: em preparo no excedente do teto de 100 = FICA");
            checar(Kds.Reconciliar(emPreparo,
                       Foto(true, status: new Dictionary<string, string> { ["forno"] = "aberto" }),
                       DateTime.Now).Count == 0,
                "G3: em preparo com a nuvem dizendo ABERTO = FICA");

            var comTerminal = Kds.Reconciliar(emPreparo,
                Foto(true, status: new Dictionary<string, string> { ["forno"] = "cancelado" }),
                DateTime.Now);
            checar(comTerminal.Count == 1 && comTerminal[0].Destino == DestinoDoCard.Cancelar,
                "G3: em preparo SO sai com terminal EXPLICITO sobre aquele pedido");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  5. G4 — periodo de graca do ticket recem-inserido
        // ═══════════════════════════════════════════════════════════════════
        // criado_em NAO serve pra isso: numa reingestao ele e de ontem. O relogio
        // e visto_em, a hora em que o ticket nasceu NESTA maquina.
        {
            var agora = new DateTime(2026, 9, 4, 10, 0, 0);
            var novinho = new[] { TicketLocal("recem", Kds.Recebido, vistoEm: agora.AddSeconds(-30)) };
            var maduro  = new[] { TicketLocal("velhote", Kds.Recebido, vistoEm: agora.AddMinutes(-5)) };

            checar(Kds.Reconciliar(novinho, Foto(true), agora).Count == 0,
                "G4: ticket com visto_em de 30 s, ausente e sem resposta, FICA");
            checar(Kds.Reconciliar(novinho,
                       Foto(true, status: new Dictionary<string, string> { ["recem"] = "concluido" }),
                       agora).Count == 0,
                "G4: a graca vale ATE contra terminal explicito (a corrida do sino)");
            var m = Kds.Reconciliar(maduro, Foto(true), agora);
            checar(m.Count == 1 && m[0].Destino == DestinoDoCard.Cancelar,
                "G4: o mesmo ticket com 5 minutos ja pode cair");

            // a corrida real: feed capturado as 10:00:00, ticket nascido as 10:00:01
            var corrida = new[] { TicketLocal("corrida", Kds.Recebido, vistoEm: agora.AddSeconds(1)) };
            checar(Kds.Reconciliar(corrida, Foto(true), agora.AddSeconds(2)).Count == 0,
                "G4: o card nascido DEPOIS da foto nao e derrubado por ela");

            // ticket antigo, gravado antes da coluna existir (visto_em nulo)
            var semCarimbo = new[] { TicketLocal("sem-carimbo", Kds.Recebido, vistoEm: null) };
            checar(Kds.Reconciliar(semCarimbo, Foto(true), agora).Count == 1,
                "ticket sem visto_em (gravado antes de 04/09) nao ganha graca: e a limpeza do deploy");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  6. G5 — origem que o feed nao representa nunca e tocada
        // ═══════════════════════════════════════════════════════════════════
        {
            var locais = new[]
            {
                TicketLocal("venda-1", Kds.Recebido,   origem: "balcao"),
                TicketLocal("venda-2", Kds.Preparando, origem: "balcao"),
                TicketLocal("enc-1",   Kds.Recebido,   origem: "encomenda"),
                TicketLocal("ifood-1", Kds.Recebido),
            };
            var m = Kds.Reconciliar(locais, Foto(true), DateTime.Now);
            checar(m.All(x => !x.RefId.StartsWith("venda-") && !x.RefId.StartsWith("enc-")),
                "G5: balcao e encomenda NUNCA aparecem na lista de mudancas");
            checar(m.Count == 1 && m[0].RefId == "ifood-1",
                "G5: e o delivery, no mesmo quadro, cai normalmente");

            // qualquer foto: feed cheio, vazio, com falha
            foreach (var f in new[] { Foto(true), Foto(false),
                                      Foto(true, abertos: new[] { "ifood-1" }),
                                      Foto(true, statusOk: false) })
                checar(Kds.Reconciliar(locais, f, DateTime.Now)
                          .All(x => !x.RefId.StartsWith("venda-")),
                    "G5: com qualquer foto, o balcao continua intocado");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  7. Teto de 100: o excedente FICA
        // ═══════════════════════════════════════════════════════════════════
        {
            var locais = Enumerable.Range(1, 150)
                .Select(i => TicketLocal("lote-" + i.ToString("000"), Kds.Recebido)).ToList();
            var respondidos = locais.Take(100)
                .ToDictionary(t => t.RefId, _ => "concluido");

            var m = Kds.Reconciliar(locais,
                Foto(true, status: respondidos, loteCompleto: false), DateTime.Now);

            checar(m.Count == 100,
                $"teto de 100: caem os 100 respondidos e ficam os 50 excedentes (mediu {m.Count})");
            checar(m.All(x => x.Destino == DestinoDoCard.Cancelar),
                "os 100 respondidos com CONCLUIDO caem todos");
            checar(!m.Any(x => x.RefId == "lote-150"),
                "o 150o, que nunca foi perguntado, FICA para o ciclo seguinte");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  8. G6 — o teste VERMELHO da morte permanente
        // ═══════════════════════════════════════════════════════════════════
        // Hoje a expiracao de 4h/12h grava 'cancelado' de forma IRREVERSIVEL: no
        // ciclo seguinte o feed traz o pedido vivo, mas Criar e idempotente e nao
        // ressuscita, e o UPDATE do re-sync exige status IN (recebido,preparando).
        // O pedido sumia do quadro para SEMPRE.
        {
            var seisHoras = new[] { TicketLocal("vivo-6h", Kds.Recebido) };
            checar(Kds.Reconciliar(seisHoras,
                       Foto(true, status: new Dictionary<string, string> { ["vivo-6h"] = "aberto" }),
                       DateTime.Now).Count == 0,
                "G6: ticket de 6 horas com a nuvem dizendo ABERTO continua no quadro");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  9..12 — os que precisam de banco e da costura inteira
        // ═══════════════════════════════════════════════════════════════════
        var arquivo = Path.Combine(Path.GetTempPath(), $"kdsrec_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);

            // ── 9. O INCIDENTE, PONTA A PONTA ──────────────────────────────
            // 71 tickets locais abertos, feed confiavel VAZIO, pdv_kds_status
            // respondendo 'concluido' para os 71: o quadro zera em UM ciclo.
            {
                using var cx = Banco.Abrir();
                cx.Execute("DELETE FROM kds_ticket");
                var ontem = DateTime.Now.AddDays(-1).ToString("o");
                for (var i = 1; i <= 71; i++)
                    cx.Execute(
                        @"INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json,
                                                  status, criado_em, visto_em)
                          VALUES (@i,'ifood',@r,@r,'x','[]','recebido',@q,@q)",
                        new { i = "t" + i, r = "inc-" + i, q = ontem });

                var nuvem = new FeedFalso { FeedConfiavel = true, Pedidos = new() };
                for (var i = 1; i <= 71; i++) nuvem.Status["inc-" + i] = "concluido";

                Kds.PuxarDaNuvemAsync(nuvem, "Loja").GetAwaiter().GetResult();

                checar(Kds.Abertos().Count == 0,
                    $"O INCIDENTE: 71 cards de pedido ja concluido zeram em UM ciclo "
                    + $"(sobraram {Kds.Abertos().Count})");
                checar(nuvem.UltimoLotePerguntado.Count == 71,
                    $"os 71 orfaos foram PERGUNTADOS, um a um (perguntou {nuvem.UltimoLotePerguntado.Count})");
            }

            // ── 9b. O MESMO cenario com o feed CAIDO ───────────────────────
            {
                using var cx = Banco.Abrir();
                cx.Execute("DELETE FROM kds_ticket");
                var ontem = DateTime.Now.AddDays(-1).ToString("o");
                for (var i = 1; i <= 71; i++)
                    cx.Execute(
                        @"INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json,
                                                  status, criado_em, visto_em)
                          VALUES (@i,'ifood',@r,@r,'x','[]','recebido',@q,@q)",
                        new { i = "t" + i, r = "inc-" + i, q = ontem });

                var nuvem = new FeedFalso { FeedConfiavel = false };
                for (var i = 1; i <= 71; i++) nuvem.Status["inc-" + i] = "concluido";

                var novos = Kds.PuxarDaNuvemAsync(nuvem, "Loja").GetAwaiter().GetResult();

                checar(Kds.Abertos().Count == 71,
                    $"feed CAIDO: os 71 permanecem, mesmo com a resposta por pedido pronta "
                    + $"(sobraram {Kds.Abertos().Count})");
                checar(novos == 0 && nuvem.UltimoLotePerguntado.Count == 0,
                    "feed caido nem chega a perguntar: nao se conclui nada de uma foto que nao existe");
            }

            // ── 10. ORDEM DAS OPERACOES: a rede de seguranca nao roda antes ──
            // Hoje a limpeza de 4h/12h e a PRIMEIRA coisa do sincronizador e roda
            // mesmo com o feed vazio por falha: internet fora por quatro horas
            // matava todos os 'recebido' sem que nada tivesse chegado da nuvem.
            {
                using var cx = Banco.Abrir();
                cx.Execute("DELETE FROM kds_ticket");
                var cincoHoras = DateTime.Now.AddHours(-5).ToString("o");
                cx.Execute(
                    @"INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json,
                                              status, criado_em, visto_em)
                      VALUES ('t-5h','ifood','velho-5h','5h','x','[]','recebido',@q,@q)",
                    new { q = cincoHoras });

                var caiu = new FeedFalso { FeedConfiavel = false };
                Kds.PuxarDaNuvemAsync(caiu, "Loja").GetAwaiter().GetResult();
                checar(Kds.Abertos().Any(t => t.RefId == "velho-5h"),
                    "G7: com o feed CAIDO a rede de seguranca de 4 h nao roda (internet fora nao mata o quadro)");

                // e com o feed de pe, dizendo que ele esta ABERTO, tambem nao mata
                var vivo = new FeedFalso { FeedConfiavel = true };
                vivo.Status["velho-5h"] = "aberto";
                Kds.PuxarDaNuvemAsync(vivo, "Loja").GetAwaiter().GetResult();
                checar(Kds.Abertos().Any(t => t.RefId == "velho-5h"),
                    "G6: a nuvem diz ABERTO e a expiracao de 4 h respeita (era a morte permanente)");

                // e o mesmo pedido, agora com a nuvem em silencio sobre ele, expira
                var mudo = new FeedFalso { FeedConfiavel = true, StatusConfiavel = false };
                Kds.PuxarDaNuvemAsync(mudo, "Loja").GetAwaiter().GetResult();
                checar(Kds.Abertos().All(t => t.RefId != "velho-5h"),
                    "a rede de seguranca continua existindo para quem a nuvem nao afirma vivo");
            }

            // ── 11. COMPATIBILIDADE nas duas direcoes ──────────────────────
            {
                // (a) FEED ANTIGO: terminais MISTURADOS no feed, como e hoje.
                //     O exe novo tem que se comportar como o de hoje.
                using (var cx = Banco.Abrir()) cx.Execute("DELETE FROM kds_ticket");
                var antigo = new FeedFalso
                {
                    FeedConfiavel = true,
                    Pedidos = new()
                    {
                        new PedidoDelivery("v-1", "0001", null, "[{\"qtd\":1,\"descricao\":\"DONUT\"}]", "faturado"),
                        new PedidoDelivery("v-2", "0002", null, "[{\"qtd\":1,\"descricao\":\"DONUT\"}]", "concluido"),
                        new PedidoDelivery("v-3", "0003", null, "[{\"qtd\":1,\"descricao\":\"DONUT\"}]", "cancelado"),
                        new PedidoDelivery("v-4", "0004", null, "[{\"qtd\":1,\"descricao\":\"DONUT\"}]", "pronto"),
                    },
                };
                Kds.PuxarDaNuvemAsync(antigo, "Loja").GetAwaiter().GetResult();
                var quadroAntigo = Kds.Abertos();
                checar(quadroAntigo.Any(t => t.RefId == "v-1" && t.Status == Kds.Recebido),
                    "RPC ANTIGA: 'faturado' vira card a preparar, como sempre foi");
                checar(quadroAntigo.All(t => t.RefId != "v-2") && quadroAntigo.All(t => t.RefId != "v-3"),
                    "RPC ANTIGA: concluido e cancelado no feed derrubam o card, como sempre foi");
                checar(quadroAntigo.Any(t => t.RefId == "v-4" && t.Status == Kds.Pronto),
                    "RPC ANTIGA: 'pronto' no feed vai pra coluna de coleta, como sempre foi");

                // (b) FEED NOVO: so abertos, com a palavra nova. Mesmo resultado
                //     para quem esta vivo, e nada de terminal para atrapalhar.
                using (var cx = Banco.Abrir()) cx.Execute("DELETE FROM kds_ticket");
                var novo = new FeedFalso
                {
                    FeedConfiavel = true,
                    Pedidos = new()
                    {
                        new PedidoDelivery("n-1", "0001", null, "[{\"qtd\":1,\"descricao\":\"DONUT\"}]", "faturado"),
                        new PedidoDelivery("n-2", "0002", null, "[{\"qtd\":1,\"descricao\":\"DONUT\"}]", "pronto"),
                    },
                };
                Kds.PuxarDaNuvemAsync(novo, "Loja").GetAwaiter().GetResult();
                var quadroNovo = Kds.Abertos();
                checar(quadroNovo.Count == 2
                       && quadroNovo.Any(t => t.RefId == "n-1" && t.Status == Kds.Recebido)
                       && quadroNovo.Any(t => t.RefId == "n-2" && t.Status == Kds.Pronto),
                    "RPC NOVA: o espelho do aberto monta o quadro exato (a preparar + coleta)");

                // (c) o servidor ANTIGO respondendo 'faturado' na pergunta por
                //     pedido NAO pode fechar nada: e a compatibilidade que
                //     dispensa o exe de detectar versao.
                using (var cx = Banco.Abrir())
                {
                    cx.Execute("DELETE FROM kds_ticket");
                    var meiaHora = DateTime.Now.AddMinutes(-30).ToString("o");
                    cx.Execute(
                        @"INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json,
                                                  status, criado_em, visto_em)
                          VALUES ('t-fat','ifood','orf-fat','9','x','[]','recebido',@q,@q)",
                        new { q = meiaHora });
                }
                var servidorVelho = new FeedFalso { FeedConfiavel = true };
                servidorVelho.Status["orf-fat"] = "faturado";
                Kds.PuxarDaNuvemAsync(servidorVelho, "Loja").GetAwaiter().GetResult();
                checar(Kds.Abertos().Any(t => t.RefId == "orf-fat"),
                    "exe NOVO + servidor VELHO: 'faturado' na reconciliacao mantem o card (zero fechamento falso)");
            }

            // ── 12. visto_em e carimbado na INSERCAO, nao na chegada ───────
            {
                using (var cx = Banco.Abrir()) cx.Execute("DELETE FROM kds_ticket");
                var ontemIso = DateTimeOffset.Now.AddDays(-1).ToString("o");
                Kds.SincronizarDelivery(new[]
                {
                    new PedidoDelivery("reing-1", "0001", null,
                        "[{\"qtd\":1,\"descricao\":\"DONUT\"}]", "faturado", ontemIso),
                });
                var t = Kds.Abertos().First(x => x.RefId == "reing-1");
                checar(t.CriadoEm < DateTime.Now.AddHours(-20),
                    "criado_em segue a CHEGADA no iFood (ontem), como sempre");
                checar(t.VistoEm is { } v && (DateTime.Now - v) < TimeSpan.FromMinutes(1),
                    "visto_em e AGORA: o relogio da graca e a insercao NESTA maquina");
                checar(Kds.Reconciliar(new[] { t }, Foto(true), DateTime.Now).Count == 0,
                    "e por isso o card recem-reingerido nao e derrubado no mesmo ciclo");
            }
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }
}
