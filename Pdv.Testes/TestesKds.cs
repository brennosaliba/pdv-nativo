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
        var arquivo = Path.Combine(Path.GetTempPath(), $"kds_teste_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);

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
