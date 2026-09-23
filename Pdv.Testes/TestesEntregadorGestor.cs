using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O NOME DO ENTREGADOR, LIDO DO GESTOR DE PEDIDOS (23/09/2026, pedido do dono): "ter o nome do
/// entregador de cada pedido, para conferir o print da avaliação que o cliente manda".
///
/// O Gestor logado dentro do caixa não é testável daqui (precisa da sessão da loja), então tudo
/// que decide alguma coisa virou função pura e é exercitado contra FIXTURES do objeto do pedido,
/// no formato medido em 23/09 em três pedidos reais.
///
/// O QUE ESTA SUÍTE PROTEGE, em uma linha cada:
///  · achar o ASSIGN_DRIVER dentro do objeto do pedido, inclusive quando houve reatribuição;
///  · o caixa não interpreta o pedido na página: ela entrega o objeto cru;
///  · do pedido só sai o que o cliente já vê (nome do entregador), nunca telefone nem endereço;
///  · o mesmo fato não vira duas chamadas, e o que o servidor aceitou não volta nunca mais;
///  · sem internet o lote espera na fila e sobe quando a rede voltar;
///  · o servidor manda descansar e o caixa descansa.
/// </summary>
public static class TestesEntregadorGestor
{
    public static async Task RodarAsync(Action<bool, string> checar)
    {
        OEvento(checar);
        OPacote(checar);
        OLote(checar);
        AResposta(checar);
        ONaoRepetir(checar);
        await OBanco(checar);
        Fonte(checar);
    }

    // ── AS FIXTURES ──────────────────────────────────────────────────────────
    // Formato medido em 23/09/2026 em pedidos reais do Gestor: history.events.ASSIGN_DRIVER
    // com metaData.workerName ("Luis R.", igual ao print do cliente) e metaData.workerExternalUuid
    // (o MESMO identificador que já chega em ifood_entrega_eventos.worker_id).

    private const string WorkerUuid = "3eef288d-5a41-4f0b-9c22-77aa1b3c4d5e";

    private const string PedidoComEntregador = """
    {
      "id": "a1b2c3d4-1111-2222-3333-444455556666",
      "displayId": "7729",
      "merchant": { "id": "3f2b9c10-7d44-4a55-8e66-1122334455aa", "name": "American Day Savassi" },
      "customer": { "name": "Tatiane Ferreira", "phone": { "number": "31999998888" } },
      "details": { "shortReference": "7729", "deliveryMode": "DELIVERY",
                   "deliveryAddress": { "streetName": "Rua Antonio de Albuquerque", "streetNumber": "700" } },
      "items": [ { "name": "Cookie Ninho", "quantity": 2 } ],
      "history": {
        "lastStatusEventCode": "DISPATCHED",
        "events": {
          "PLACED": { "createdAt": "2026-09-23T17:50:10.000Z" },
          "CONFIRMED": { "createdAt": "2026-09-23T17:51:02.000Z" },
          "ASSIGN_DRIVER": {
            "createdAt": "2026-09-23T18:12:31.000Z",
            "metaData": {
              "workerName": "Luis R.",
              "workerExternalUuid": "3eef288d-5a41-4f0b-9c22-77aa1b3c4d5e",
              "workerVehicleType": "MOTORCYCLE",
              "deliveryId": "a66e4b35-9999-8888-7777-666655554444"
            }
          }
        }
      }
    }
    """;

    private const string PedidoSemEntregador = """
    {
      "id": "b0b0b0b0-1111-2222-3333-444455556666",
      "displayId": "5164",
      "merchant": { "id": "3f2b9c10-7d44-4a55-8e66-1122334455aa" },
      "customer": { "name": "Maria Clara Souza" },
      "details": { "shortReference": "5164" },
      "history": { "lastStatusEventCode": "PREPARATION_STARTED",
                   "events": { "PLACED": {}, "CONFIRMED": {}, "PREPARATION_STARTED": {} } }
    }
    """;

    // Reatribuição: o primeiro entregador desistiu e o pedido foi para outro. O nome que o
    // cliente viu (e que vai estar no print) é o do ÚLTIMO.
    private const string PedidoComTroca = """
    {
      "id": "cccccccc-1111-2222-3333-444455556666",
      "displayId": "7730",
      "history": { "events": { "ASSIGN_DRIVER": [
        { "createdAt": "2026-09-23T18:00:00.000Z",
          "metaData": { "workerName": "Ana P.", "workerExternalUuid": "11111111-0000-0000-0000-000000000001" } },
        { "createdAt": "2026-09-23T18:20:00.000Z",
          "metaData": { "workerName": "Gustavo S.", "workerExternalUuid": "22222222-0000-0000-0000-000000000002" } }
      ] } }
    }
    """;

    // Formato tolerado: events como LISTA, com o código dentro de cada evento.
    private const string PedidoEventosEmLista = """
    {
      "displayId": "7731",
      "history": { "events": [
        { "code": "PLACED", "createdAt": "2026-09-23T17:00:00.000Z" },
        { "code": "ASSIGN_DRIVER", "createdAt": "2026-09-23T17:30:00.000Z",
          "metadata": { "workerName": "  Joao   B.  ", "workerExternalUuid": "33333333-0000-0000-0000-000000000003" } }
      ] }
    }
    """;

    // ── 1. ACHAR O ASSIGN_DRIVER ─────────────────────────────────────────────
    private static void OEvento(Action<bool, string> checar)
    {
        var e = EntregadorGestor.DoPedido(PedidoComEntregador);
        checar(e is not null && e.Nome == "Luis R." && e.WorkerId == WorkerUuid,
            $"EG-1 o nome e o identificador do entregador saem do ASSIGN_DRIVER ({e?.Nome} / {e?.WorkerId})");
        checar(e is not null && e.Pedido == "7729" && e.OrderId == "a1b2c3d4-1111-2222-3333-444455556666",
            "EG-2 o número curto e o uuid do pedido vêm junto (é o que liga o nome ao pedido)");
        checar(e?.Quando is { } q && q.ToUniversalTime() == new DateTime(2026, 9, 23, 18, 12, 31, DateTimeKind.Utc),
            $"EG-3 o instante da atribuição é lido ({e?.Quando:o})");
        checar(e?.MerchantId == "3f2b9c10-7d44-4a55-8e66-1122334455aa",
            "EG-4 a loja do pedido segundo o próprio objeto vai junto (quem confere é o servidor)");

        checar(EntregadorGestor.DoPedido(PedidoSemEntregador) is null,
            "EG-5 pedido sem entregador atribuído não vira fato nenhum (é o caso mais comum)");
        checar(EntregadorGestor.DoPedido("{isso nao e json") is null
               && EntregadorGestor.DoPedido("") is null
               && EntregadorGestor.DoPedido(null) is null
               && EntregadorGestor.DoPedido("[1,2,3]") is null,
            "EG-6 objeto ilegível vira null, nunca exceção (o iFood muda o formato deles sem avisar)");
        checar(EntregadorGestor.DoPedido("""{"history":{"events":{"ASSIGN_DRIVER":{"metaData":{"workerVehicleType":"MOTORCYCLE"}}}}}""") is null,
            "EG-7 evento sem nome e sem identificador não vira fato (não se inventa nome)");
        checar(EntregadorGestor.DoPedido("""{"id":"x","history":{"events":{"ASSIGN_DRIVER":{"metaData":{"workerName":"Luis R."}}}}}""") is null,
            "EG-8 nome sem identificador não serve: é o identificador que cola no que já temos");

        var troca = EntregadorGestor.DoPedido(PedidoComTroca);
        checar(troca?.Nome == "Gustavo S." && troca?.WorkerId == "22222222-0000-0000-0000-000000000002",
            $"EG-9 com reatribuição vale o ÚLTIMO entregador, que é o do print ({troca?.Nome})");

        var lista = EntregadorGestor.DoPedido(PedidoEventosEmLista, "order/eeee1111-2222-3333-4444-555566667777");
        checar(lista?.Nome == "Joao B." && lista?.Pedido == "7731",
            $"EG-10 events como lista também é lido, e o nome sai sem espaço dobrado ({lista?.Nome})");

        var semId = EntregadorGestor.DoPedido(PedidoEventosEmLista, "order/ddddeeee-1111-2222-3333-444455556666");
        checar(semId?.OrderId == "ddddeeee-1111-2222-3333-444455556666",
            "EG-11 objeto sem id usa o uuid da chave do localStorage");

        checar(EntregadorGestor.Nome("   ") is null && EntregadorGestor.Nome(new string('a', 200))!.Length == EntregadorGestor.TetoDoNome,
            "EG-12 nome vazio não passa e nome absurdo é cortado no teto");
        checar(EntregadorGestor.Chave("ABC", "XYZ") == EntregadorGestor.Chave("abc", "xyz")
               && EntregadorGestor.Chave("a", "b") != EntregadorGestor.Chave("a", "c"),
            "EG-13 a chave é pedido + entregador, e não depende de caixa alta");
    }

    // ── 2. O PACOTE QUE A TELA MANDA ─────────────────────────────────────────
    private static void OPacote(Action<bool, string> checar)
    {
        var pacote = JsonSerializer.Serialize(new
        {
            tipo = "entregadores",
            itens = new object[]
            {
                new { chave = "order/a1b2c3d4-1111-2222-3333-444455556666", bruto = PedidoComEntregador },
                new { chave = "order/b0b0b0b0-1111-2222-3333-444455556666", bruto = PedidoSemEntregador },
                new { chave = "order/a1b2c3d4-1111-2222-3333-444455556666", bruto = PedidoComEntregador },
                new { chave = "order/cccccccc-1111-2222-3333-444455556666", bruto = PedidoComTroca },
            },
        });

        var lidos = EntregadorGestor.DoPacote(pacote);
        checar(lidos.Count == 2, $"EG-14 o pacote da tela vira só os pedidos COM entregador ({lidos.Count})");
        checar(lidos.Select(x => x.Chave).Distinct().Count() == lidos.Count,
            "EG-15 o mesmo pedido lido duas vezes no mesmo pacote entra uma vez só");
        checar(EntregadorGestor.DoPacote("{\"tipo\":\"entregadores\"}").Count == 0
               && EntregadorGestor.DoPacote("nao e json").Count == 0
               && EntregadorGestor.DoPacote(null).Count == 0,
            "EG-16 pacote vazio ou malformado vira lista vazia, nunca exceção");

        var muitos = JsonSerializer.Serialize(new
        {
            tipo = "entregadores",
            itens = Enumerable.Range(0, EntregadorGestor.TetoDoLote + 20).Select(i => new
            {
                chave = $"order/0000{i:0000}-1111-2222-3333-444455556666",
                bruto = PedidoComTroca.Replace("\"cccccccc-1111-2222-3333-444455556666\"", $"\"ped-{i}\""),
            }).ToArray(),
        });
        checar(EntregadorGestor.DoPacote(muitos).Count == EntregadorGestor.TetoDoLote,
            "EG-17 o pacote respeita o teto do lote (chamada gigante estoura o prazo)");

        // ⚠️ RECONHECER SEM PARSEAR. A mensagem do WebView2 chega na thread que desenha a
        // venda, e este é o único pacote que carrega os objetos CRUS dos pedidos. Se o
        // reconhecimento voltar a depender de ler o documento inteiro, o operador vê a tela
        // parar de cinco em cinco minutos, mesmo sem ninguém na tela do chat.
        checar(EntregadorGestor.EhPacote(pacote), "EG-17b o pacote dos entregadores é reconhecido pelo começo");
        checar(!EntregadorGestor.EhPacote("{\"tipo\":\"chatmsgs\",\"mensagens\":[]}")
               && !EntregadorGestor.EhPacote("{\"tipo\":\"naolidas\",\"texto\":\"3\"}")
               && !EntregadorGestor.EhPacote("{\"tipo\":\"ajuda\"}")
               && !EntregadorGestor.EhPacote("") && !EntregadorGestor.EhPacote(null),
            "EG-17c nenhum outro pacote é confundido com o dos entregadores");
        // A prova de que NÃO houve parse: o resto da string nem é JSON válido, e mesmo assim
        // o começo é reconhecido. Um reconhecimento por JsonDocument.Parse devolveria false.
        checar(EntregadorGestor.EhPacote("{\"tipo\":\"entregadores\",\"itens\":[{isso nao fecha"),
            "EG-17d o reconhecimento olha só o começo: não lê o pacote inteiro para decidir");
        // E um pacote grande de verdade continua sendo reconhecido na mesma comparação curta.
        var gigante = "{\"tipo\":\"entregadores\",\"itens\":[{\"chave\":\"order/x\",\"bruto\":\""
                      + new string('x', 2_000_000) + "\"}]}";
        checar(EntregadorGestor.EhPacote(gigante), "EG-17e pacote de megabytes é reconhecido do mesmo jeito");
        // "entregadores" longe do começo (dentro do objeto de um pedido, por exemplo) não conta.
        checar(!EntregadorGestor.EhPacote("{\"tipo\":\"chatmsgs\",\"texto\":\"" + new string(' ', 80)
                                          + "\\\"tipo\\\":\\\"entregadores\\\"\"}"),
            "EG-17f a palavra no meio do pacote não engana o reconhecimento");
    }

    // ── 3. O LOTE QUE VAI ────────────────────────────────────────────────────
    private static void OLote(Action<bool, string> checar)
    {
        var itens = new[] { EntregadorGestor.DoPedido(PedidoComEntregador)! };
        var corpo = EntregadorGestor.CorpoDoLote(itens, "American Day Savassi");

        using var doc = JsonDocument.Parse(corpo);
        var r = doc.RootElement;
        checar(r.GetProperty("acao").GetString() == EntregadorGestor.Acao
               && r.GetProperty("origem").GetString() == "caixa"
               && r.GetProperty("loja").GetString() == "American Day Savassi",
            "EG-18 o corpo diz a ação, a origem e a loja do terminal");

        var item = r.GetProperty("itens")[0];
        var campos = item.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        checar(campos.SequenceEqual(new[] { "chave", "delivery_id", "merchant_id", "order_id", "pedido",
                                            "quando", "veiculo", "worker_id", "worker_nome" }),
            $"EG-19 o item do lote tem SÓ os nove campos previstos ({string.Join(",", campos)})");
        checar(item.GetProperty("veiculo").GetString() == "MOTORCYCLE"
               && item.GetProperty("delivery_id").GetString() == "a66e4b35-9999-8888-7777-666655554444",
            "EG-19b o veículo e o identificador da entrega vão junto (o servidor guarda os dois)");
        // ⚠️ O objeto do pedido tem telefone, endereço e a lista de itens da pessoa. Nada disso
        // tem a ver com o nome do entregador, e nada disso pode sair do caixa por este caminho.
        checar(!corpo.Contains("31999998888", StringComparison.Ordinal)
               && !corpo.Contains("Antonio de Albuquerque", StringComparison.Ordinal)
               && !corpo.Contains("Tatiane", StringComparison.Ordinal)
               && !corpo.Contains("Cookie Ninho", StringComparison.Ordinal),
            "EG-20 telefone, endereço, nome do cliente e itens do pedido NÃO saem no lote");

        checar(EntregadorGestor.ChavesDoCorpo(corpo).SequenceEqual(new[] { itens[0].Chave }),
            "EG-21 as chaves do lote são legíveis a partir do corpo (é o que a fila tem na mão)");
        checar(EntregadorGestor.ChavesDoCorpo("nao e json").Count == 0,
            "EG-22 corpo ilegível não trava a fila");

        var semLoja = EntregadorGestor.CorpoDoLote(itens, "   ");
        checar(JsonDocument.Parse(semLoja).RootElement.GetProperty("loja").ValueKind == JsonValueKind.Null,
            "EG-23 terminal sem loja manda loja nula (quem sabe a loja é o servidor)");

        var agora = new DateTime(2026, 9, 23, 18, 30, 0);
        checar(EntregadorGestor.ChaveDoLote(itens, agora) == EntregadorGestor.ChaveDoLote(itens, agora)
               && EntregadorGestor.ChaveDoLote(itens, agora) != EntregadorGestor.ChaveDoLote(
                   new[] { EntregadorGestor.DoPedido(PedidoComTroca)! }, agora),
            "EG-24 a client_key do lote é estável e muda com o conteúdo");
    }

    // ── 4. O QUE VOLTA ───────────────────────────────────────────────────────
    private static void AResposta(Action<bool, string> checar)
    {
        var enviadas = new[] { "k1", "k2", "k3" };

        var ok = EntregadorGestor.LerResposta(200,
            """{"ok":true,"aprendidos":1,"guardar":["k1","k2"],"repetir":["k3"]}""", enviadas);
        checar(ok.Desfecho == DesfechoLote.Aceito && ok.Guardar.Count == 2 && ok.Repetir.SequenceEqual(new[] { "k3" }),
            "EG-25 o servidor diz o que guardar e o que ainda não deu para tratar");

        var okMudo = EntregadorGestor.LerResposta(200, """{"ok":true}""", enviadas);
        checar(okMudo.Desfecho == DesfechoLote.Aceito && okMudo.Guardar.Count == 3,
            "EG-26 servidor que só diz ok encerra o lote inteiro (senão seria uma chamada por varredura, para sempre)");

        checar(EntregadorGestor.LerResposta(-1, null, enviadas).Desfecho == DesfechoLote.SemRede,
            "EG-27 nada saiu do caixa: o lote fica na fila");
        checar(EntregadorGestor.LerResposta(0, null, enviadas).Desfecho == DesfechoLote.NaoConfirmou,
            "EG-28 saiu e a resposta se perdeu: o lote fica na fila (reenviar não estraga nada)");
        checar(EntregadorGestor.LerResposta(404, "", enviadas).Desfecho == DesfechoLote.NuvemSemRecurso,
            "EG-29 borda ainda não publicada é espera, não recusa");
        checar(EntregadorGestor.LerResposta(403, "", enviadas).Desfecho == DesfechoLote.SemPermissao
               && EntregadorGestor.LerResposta(401, "", enviadas).Desfecho == DesfechoLote.SemPermissao,
            "EG-30 caixa barrado não fica martelando");
        checar(EntregadorGestor.LerResposta(503, "", enviadas).Desfecho == DesfechoLote.NaoConfirmou
               && EntregadorGestor.LerResposta(429, "", enviadas).Desfecho == DesfechoLote.NaoConfirmou,
            "EG-31 servidor fora do ar é transitório");
        checar(EntregadorGestor.LerResposta(200, "isso nao e json", enviadas).Desfecho == DesfechoLote.NaoConfirmou,
            "EG-32 corpo ilegível num 200 não é recusa: o servidor pode ter aprendido");
        checar(EntregadorGestor.LerResposta(200, """{"ok":false,"motivo":"desligado"}""", enviadas).Desfecho == DesfechoLote.Desligado,
            "EG-33 a rede pode desligar esta leitura, e aí o caixa descansa");
        var recusa = EntregadorGestor.LerResposta(200, """{"ok":false,"motivo":"lote_invalido"}""", enviadas);
        checar(recusa.Desfecho == DesfechoLote.Recusado && recusa.Guardar.Count == 0,
            "EG-34 recusa com motivo é permanente e não marca nada como aceito");
    }

    // ── 5. NÃO MANDAR DE NOVO ────────────────────────────────────────────────
    private static void ONaoRepetir(Action<bool, string> checar)
    {
        var a = EntregadorGestor.DoPedido(PedidoComEntregador)!;
        var b = EntregadorGestor.DoPedido(PedidoComTroca)!;

        var agora = new DateTime(2026, 9, 23, 18, 40, 0);
        EntregadorGestor.EstadoLocal Estado(bool aceito, int tentativas, DateTime? fila = null)
            => new(aceito, tentativas, fila);

        var aceito = new Dictionary<string, EntregadorGestor.EstadoLocal> { [a.Chave] = Estado(true, 0) };
        checar(EntregadorGestor.Novos(new[] { a, b }, aceito, agora).SequenceEqual(new[] { b }),
            "EG-35 o que o servidor já aceitou nunca mais é mandado");

        var cansado = new Dictionary<string, EntregadorGestor.EstadoLocal>
            { [b.Chave] = Estado(false, EntregadorGestor.TetoDeTentativas) };
        checar(EntregadorGestor.Novos(new[] { a, b }, cansado, agora).SequenceEqual(new[] { a }),
            "EG-36 chave que o servidor nunca conseguiu tratar para de voltar no teto de tentativas");
        checar(EntregadorGestor.Novos(new[] { a, b }, new Dictionary<string, EntregadorGestor.EstadoLocal>(), agora).Count == 2,
            "EG-37 disco limpo manda os dois");

        // JÁ ESTÁ NA FILA: o lote anterior ainda não teve resposta. Sem esta regra, a cada cinco
        // minutos nasceria um lote novo com o mesmo fato dentro, e a fila só cresceria.
        var esperando = new Dictionary<string, EntregadorGestor.EstadoLocal>
            { [a.Chave] = Estado(false, 0, agora.AddMinutes(-10)) };
        checar(EntregadorGestor.Novos(new[] { a, b }, esperando, agora).SequenceEqual(new[] { b }),
            "EG-37b fato que já está esperando na fila não entra em lote novo");
        checar(EntregadorGestor.Novos(new[] { a }, esperando, agora + EntregadorGestor.EsperaDaFila).Count == 1,
            "EG-37c passada a espera da fila (a linha lá já desistiu), o fato volta a ser mandado");
    }

    // ── 6. O BANCO E A FILA ──────────────────────────────────────────────────
    private static async Task OBanco(Action<bool, string> checar)
    {
        var db = Path.Combine(Path.GetTempPath(), "entregador-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var antes = Banco.CaminhoForcado;
        Banco.CaminhoForcado = db;
        try
        {
            Banco.Migrar(db);
            using var cx = Banco.Abrir(db);
            Isolamento.SemNuvem(cx);

            var agora = new DateTime(2026, 9, 23, 18, 40, 0);
            var a = EntregadorGestor.DoPedido(PedidoComEntregador)!;
            var b = EntregadorGestor.DoPedido(PedidoComTroca)!;

            long Linhas() => cx.ExecuteScalar<long>("SELECT COUNT(*) FROM ifood_entregador_visto");
            long NaFila() => cx.ExecuteScalar<long>("SELECT COUNT(*) FROM outbox WHERE tipo = @T",
                new { T = EntregadorGestor.TipoNaFila });
            string? Payload() => cx.ExecuteScalar<string?>(
                "SELECT payload FROM outbox WHERE tipo = @T ORDER BY id DESC LIMIT 1",
                new { T = EntregadorGestor.TipoNaFila });

            checar(EntregadorGestor.Registrar(cx, new[] { a, b }, "American Day Savassi", agora) == 2
                   && Linhas() == 2 && NaFila() == 1,
                "EG-38 o lote inteiro é UMA linha na fila, com uma linha local por fato");
            checar(EntregadorGestor.Registrar(cx, new[] { a, b }, "American Day Savassi", agora) == 0
                   && NaFila() == 1,
                "EG-39 a mesma leitura outra vez não enfileira nada (nem antes de o servidor responder)");
            checar(EntregadorGestor.Registrar(cx, Array.Empty<EntregadorNoPedido>(), "American Day Savassi", agora) == 0,
                "EG-40 leitura sem entregador nenhum não toca na fila");

            var chamadas = new List<string>();
            Func<string, string, Task<(int, string?)>> Responde(int st, string? corpo) =>
                (nome, corpoEnviado) => { chamadas.Add(nome + "|" + corpoEnviado); return Task.FromResult((st, corpo)); };

            var payload = Payload()!;
            var r1 = await EntregadorGestor.ResolverNaFilaAsync(payload,
                Responde(200, $$"""{"ok":true,"guardar":["{{a.Chave}}"],"repetir":["{{b.Chave}}"]}"""), agora);
            checar(r1.Ok == true && chamadas.Count == 1 && chamadas[0].StartsWith(EntregadorGestor.Edge + "|", StringComparison.Ordinal),
                $"EG-41 a fila manda o lote para a BORDA ({r1.Erro})");

            var vistos = EntregadorGestor.JaVistos(cx);
            checar(vistos[a.Chave].Aceito && !vistos[b.Chave].Aceito && vistos[b.Chave].Tentativas == 1,
                "EG-42 o aceito vira assunto encerrado e o que falta tratar conta uma tentativa");

            // uma leitura nova, com os dois fatos: só o que ainda não foi aceito volta
            checar(EntregadorGestor.Registrar(cx, new[] { a, b }, "American Day Savassi", agora.AddMinutes(5)) == 1,
                "EG-43 na leitura seguinte só volta o que o servidor ainda não aceitou");

            // SEM INTERNET
            var payload2 = Payload()!;
            var r2 = await EntregadorGestor.ResolverNaFilaAsync(payload2, Responde(0, null), agora);
            checar(r2.Ok is null && !EntregadorGestor.JaVistos(cx)[b.Chave].Aceito,
                "EG-44 sem resposta o lote CONTINUA na fila e nada é marcado como aceito");
            var r2b = await EntregadorGestor.ResolverNaFilaAsync(payload2, Responde(404, ""), agora);
            checar(r2b.Ok is null && r2b.Erro!.Contains(EntregadorGestor.Edge, StringComparison.Ordinal),
                "EG-45 borda ainda não publicada é espera, com o motivo legível");
            var r2c = await EntregadorGestor.ResolverNaFilaAsync(payload2, Responde(200, """{"ok":true}"""), agora);
            checar(r2c.Ok == true && EntregadorGestor.JaVistos(cx)[b.Chave].Aceito,
                "EG-46 quando a rede volta o mesmo lote sobe e o fato é encerrado");

            // caixa barrado
            var c2 = EntregadorGestor.DoPedido(PedidoEventosEmLista, "order/aaaa9999-2222-3333-4444-555566667777")!;
            EntregadorGestor.Registrar(cx, new[] { c2 }, "American Day Savassi", agora);
            var payloadBarrado = Payload()!;
            var r3 = await EntregadorGestor.ResolverNaFilaAsync(payloadBarrado, Responde(403, ""), agora);
            checar(r3.Ok == false, "EG-47 caixa barrado é recusa permanente (não fica martelando)");
            // ⚠️ A chave errada é problema NOSSO, não daquele nome: arrumada a chave, os nomes
            // daquele dia ainda têm de subir. Gastar tentativa aqui os apagaria em silêncio.
            checar(EntregadorGestor.JaVistos(cx)[c2.Chave].Tentativas == 0,
                "EG-47b recusa de configuração não gasta as tentativas dos fatos");
            var r3b = await EntregadorGestor.ResolverNaFilaAsync(payloadBarrado,
                Responde(200, """{"ok":false,"motivo":"lista_invalida"}"""), agora);
            checar(r3b.Ok == false && EntregadorGestor.JaVistos(cx)[c2.Chave].Tentativas == 1,
                "EG-47c recusa do que foi MANDADO gasta tentativa (senão a leitura repetiria para sempre)");

            // o teto de tentativas: chave que o servidor nunca consegue tratar para de voltar
            var c = EntregadorGestor.DoPedido(PedidoEventosEmLista, "order/eeee1111-2222-3333-4444-555566667777")!;
            EntregadorGestor.Registrar(cx, new[] { c }, "American Day Savassi", agora);
            var payloadC = Payload()!;
            for (var i = 0; i < EntregadorGestor.TetoDeTentativas; i++)
                await EntregadorGestor.ResolverNaFilaAsync(payloadC,
                    Responde(200, $$"""{"ok":true,"guardar":[],"repetir":["{{c.Chave}}"]}"""), agora);
            checar(EntregadorGestor.Novos(new[] { c }, EntregadorGestor.JaVistos(cx), agora).Count == 0,
                "EG-48 pedido que o servidor nunca reconhece para de ser mandado (não vira chamada eterna)");

            // A REDE DESLIGOU: o caixa descansa
            var d = EntregadorGestor.DoPedido(PedidoComEntregador, "order/ffff1111-2222-3333-4444-555566667777")!;
            EntregadorGestor.Registrar(cx, new[] { d }, "American Day Savassi", agora);
            var payloadD = Payload()!;
            var rd = await EntregadorGestor.ResolverNaFilaAsync(payloadD,
                Responde(200, """{"ok":false,"motivo":"desligado"}"""), agora);
            checar(rd.Ok == true && EntregadorGestor.Pausado(cx, agora),
                "EG-49 'desligado' tira o lote da fila e o caixa fica quieto");
            var antesDoDescanso = NaFila();
            checar(EntregadorGestor.Registrar(cx, new[] { a, b }, "American Day Savassi", agora.AddMinutes(10)) == 0
                   && NaFila() == antesDoDescanso,
                "EG-50 no descanso a leitura nem chega a virar lote");
            checar(!EntregadorGestor.Pausado(cx, agora + EntregadorGestor.Descanso + TimeSpan.FromMinutes(1)),
                "EG-51 passado o descanso a leitura volta sozinha");

            // lote sem itens não trava a fila
            var r4 = await EntregadorGestor.ResolverNaFilaAsync("""{"acao":"entregadores","itens":[]}""",
                Responde(200, """{"ok":true}"""), agora + EntregadorGestor.Descanso + TimeSpan.FromMinutes(1));
            checar(r4.Ok == true, "EG-52 lote sem itens sai da fila sem chamada");

            checar(Drenagem.TiposComHandler.Contains(EntregadorGestor.TipoNaFila),
                "EG-53 o tipo novo está no filtro da drenagem (senão a linha existiria e nunca sairia)");
            checar(Drenagem.JanelaPropria.Any(j => j.Tipo == EntregadorGestor.TipoNaFila),
                "EG-54 e tem janela PRÓPRIA: a venda nunca fica atrás do nome de entregador");
            checar(Drenagem.PrazoDoTransitorio(EntregadorGestor.TipoNaFila) < TimeSpan.FromDays(1),
                "EG-55 lote velho desiste rápido: a leitura seguinte traz o mesmo fato de novo");

            var apagadas = EntregadorGestor.Faxina(cx, agora + EntregadorGestor.Guarda + TimeSpan.FromDays(1));
            checar(apagadas > 0 && Linhas() == 0,
                $"EG-56 a faxina limpa a lista local velha ({apagadas} apagadas)");
        }
        finally
        {
            Banco.CaminhoForcado = antes;
            SqliteConnection.ClearAllPools();
            try { File.Delete(db); } catch { }
        }
    }

    // ── 7. PELO FONTE ────────────────────────────────────────────────────────
    private static void Fonte(Action<bool, string> checar)
    {
        var raiz = Raiz();
        string Ler(params string[] partes) => raiz is null ? "" : File.ReadAllText(Path.Combine(new[] { raiz }.Concat(partes).ToArray()));
        var nucleo = Ler("Pdv.Nucleo", "EntregadorGestor.cs");
        var servico = Ler("ServicoEntregadorGestor.cs");
        var tela = Ler("Telas", "ChatIfood.xaml.cs");
        var dren = Ler("Pdv.Nucleo", "Drenagem.cs");

        checar(nucleo.Length > 0 && !Regex.IsMatch(nucleo, @"Vendas\.Finalizar|LinhaVenda|Fiscal\.|Emissor|AnonKey"),
            "EG-57 o núcleo da leitura não conhece venda, nota nem a chave pública");
        checar(dren.Contains("EntregadorGestor.TipoNaFila => await EntregadorGestor.ResolverNaFilaAsync(", StringComparison.Ordinal),
            "EG-58 a Drenagem manda o lote pela resolução que respeita o descanso e o teto");

        // ⚠️ A PÁGINA NÃO INTERPRETA O PEDIDO. Se o script começar a ler workerName sozinho, a
        // regra passa a existir em dois lugares (um deles sem teste) e volta a divergir, que é
        // exatamente o defeito que a raspadinha no chat já pagou caro.
        var i = tela.IndexOf("window.pdvLerEntregadores = function", StringComparison.Ordinal);
        var corpo = i > 0 ? tela[i..Math.Min(tela.Length, i + 3400)] : "";
        checar(corpo.Length > 0 && !corpo.Contains("workerName", StringComparison.Ordinal)
               && !corpo.Contains("metaData", StringComparison.Ordinal),
            "EG-59 o script do painel entrega o pedido CRU: quem acha o ASSIGN_DRIVER é o núcleo");
        checar(corpo.Contains("pdvOrdersMandados[k] === raw.length", StringComparison.Ordinal),
            "EG-60 o script não remanda o mesmo pedido a cada leitura (e remanda quando ele muda)");
        // ⚠️ A RODADA CONTINUA DE ONDE PAROU. Enquanto o laço recomeçava do índice 0 e o mapa
        // era zerado por um contador de rodadas, a página tinha um teto de 240 pedidos: com o
        // Gestor aberto por dias, do 241º em diante NADA era oferecido, e (o Chrome percorre o
        // localStorage em ordem de inserção) eram justamente os pedidos MAIS NOVOS, os que o
        // dono quer conferir. Reoferecer continua acontecendo, mas a cada VOLTA completa.
        checar(corpo.Contains("var i = pdvEntregadoresCursor;", StringComparison.Ordinal)
               && corpo.Contains("pdvEntregadoresCursor = i;", StringComparison.Ordinal),
            "EG-60b a rodada continua de onde a anterior parou (cursor), nunca do índice 0");
        checar(!Regex.IsMatch(corpo, @"%\s*12\s*===\s*0") && !corpo.Contains("pdvEntregadoresRodada", StringComparison.Ordinal),
            "EG-60c o mapa só é esquecido quando a volta termina, nunca por relógio de rodadas");
        checar(corpo.Contains("if (i >= total) { i = 0; pdvOrdersMandados = {}; }", StringComparison.Ordinal),
            "EG-60d ao virar a volta a página recomeça e reoferece tudo (o lote pode não ter virado nada lá)");
        // Vinte pedidos de até 400 mil caracteres davam um pacote de milhões de caracteres de uma
        // vez só atravessando o WebView2, a cada cinco minutos, com ou sem alguém na tela.
        checar(corpo.Contains("soma + raw.length > 600000", StringComparison.Ordinal),
            "EG-60e o pacote tem orçamento de tamanho: o que não couber vai na próxima rodada");
        // SÓ LÊ: nada é escrito no navegador do Gestor.
        checar(!Regex.IsMatch(tela, @"localStorage\.(setItem|removeItem|clear)"),
            "EG-61 a tela do chat NUNCA escreve no localStorage do Gestor");
        // O rastro no disco da loja não guarda nome de entregador.
        checar(!Regex.IsMatch(tela, @"DiagEntregador\([^)]*\.Nome|DiagEntregador\([^)]*WorkerId"),
            "EG-62 o diagnóstico grava contagem, nunca o nome de quem entregou");
        // ⚠️ O PACOTE NÃO PASSA PELO JsonDocument.Parse DA TELA. Antes o parse vinha primeiro,
        // só para descobrir o campo "tipo", e o pacote inteiro (os objetos crus dos pedidos)
        // era lido na thread do Dispatcher, a mesma de todas as janelas do caixa.
        var pos = tela.IndexOf("private void OnWebMessage", StringComparison.Ordinal);
        var msg = pos > 0 ? tela[pos..Math.Min(tela.Length, pos + 3000)] : "";
        var ondeEh = msg.IndexOf("EntregadorGestor.EhPacote(txt)", StringComparison.Ordinal);
        var ondeParse = msg.IndexOf("JsonDocument.Parse(txt)", StringComparison.Ordinal);
        checar(ondeEh > 0 && ondeParse > 0 && ondeEh < ondeParse,
            "EG-65b o pacote dos entregadores sai antes do parse: a tela não lê megabytes no Dispatcher");
        checar(!Regex.IsMatch(tela, @"tipo\s*==\s*""entregadores"""),
            "EG-65c o pacote dos entregadores tem UM caminho só (não sobrou o desvio pelo campo tipo)");

        checar(servico.Contains("Servicos.Dreno()?.Cutucar()", StringComparison.Ordinal),
            "EG-63 depois de gravar, o serviço cutuca a fila (o lote não espera o relógio de 45 s)");
        // Isto roda em silêncio: nem toast, nem papel, nem tela nova.
        checar(servico.Length > 0 && !Regex.IsMatch(servico, @"Impressao\.|Avisou|MessageBox|Dialogo\."),
            "EG-64 a leitura roda em silêncio: não imprime, não avisa e não abre tela");

        var textos = nucleo + servico;
        checar(!textos.Contains('—'), "EG-65 nenhum texto desta parte tem travessão");
    }

    private static string? Raiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj"))) return dir.FullName;
        return null;
    }
}
