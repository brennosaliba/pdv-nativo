using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// RESGATE PELO CHAT DO iFOOD, LADO DO CAIXA (07/10/2026, desenho "resgate-final", tarefa P12).
///
/// O caixa LÊ e EXECUTA; quem decide é o ERP. O WebView2, o CDP e o SDK do Sendbird não rodam
/// daqui, então tudo que decide alguma coisa foi para função pura (ConversaRaspadinha) e é
/// exercitado contra fixtures copiadas do contrato (seção 4 do desenho), com relógio falso e
/// servidor falso:
///  · de que lado veio a fala, e a chave igual em todos os terminais;
///  · a triagem (canal, loja, recência, eco) e o agrupamento de 6 s;
///  · a leitura de TODA ação e de todo campo da resposta, e do sinal;
///  · o PORTÃO: sombra e operador nunca chamam o script, sinal velho não envia, eco e limite, token
///    de uso único, sem confirmação vira "incerta" e nunca volta;
///  · a política do papel (quem imprime, reserva, impresso só depois do papel);
///  · a fila sem rede;
///  · e, pelo fonte, que a regra do código e as palavras-chave não moram no caixa.
/// </summary>
public static class TestesConversaChat
{
    private const string Canal = TestesQuadroSendbird.Canal;
    private const string Pedido = TestesQuadroSendbird.Pedido;
    private const string Savassi = TestesQuadroSendbird.MerchantSavassi;
    private const string Castelo = TestesQuadroSendbird.MerchantCastelo;

    private static readonly DateTime Agora = new(2026, 10, 7, 19, 40, 0);

    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();

    private static MensagemSendbird Fala(string texto, string autor = "cli-77", string? userType = "CUSTOMER",
        long? msgId = 8812736123, DateTime? quando = null, string canal = Canal)
        => QuadroSendbird.Ler(TestesQuadroSendbird.Mesg(texto, autor, userType, msgId, Ms(quando ?? Agora.AddSeconds(-20)), canal))!;

    /// <summary>O sinal de uma loja em <paramref name="modo"/> com a Savassi como merchant.</summary>
    private static string SinalJson(string modo, string envio = "sdk", string? pausado = null,
        string avisos = "[]", string saidas = "[]", string comandas = "[]")
        => "{\"ok\":true,\"agora\":\"2026-10-07T22:40:00Z\",\"modo\":\"" + modo + "\",\"palavra_modo\":\"desligado\","
           + "\"envio\":\"" + envio + "\",\"comanda_onde\":\"kds\",\"merchant_ids\":[\"" + Savassi + "\"],"
           + "\"intervalo_s\":60,\"pausado\":" + (pausado is null ? "null" : "\"" + pausado + "\"") + ","
           + "\"avisos\":" + avisos + ",\"saidas\":" + saidas + ",\"comandas\":" + comandas + "}";

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        OLado(checar);
        AChave(checar);
        ATriagem(checar);
        OAgrupador(checar);
        OCorpo(checar);
        AResposta(checar);
        OSinal(checar);
        OPortao(checar);
        AConfirmacao(checar);
        OEstado(checar);
        await OPapel(checar);
        await ABancoEAFila(checar);
        Fonte(checar);
    }

    // ── LADO ─────────────────────────────────────────────────────────────────
    private static void OLado(Action<bool, string> checar)
    {
        checar(ConversaRaspadinha.Lado(Fala("oi"), "loja-1", null) == LadoFala.Cliente, "LD-1 CUSTOMER é cliente");
        checar(ConversaRaspadinha.Lado(Fala("oi", autor: "x", userType: "MERCHANT"), "loja-1", null) == LadoFala.Loja,
            "LD-2 MERCHANT é loja");
        checar(ConversaRaspadinha.Lado(Fala("oi", autor: "loja-1"), "loja-1", null) == LadoFala.Loja,
            "LD-3 autor igual ao user_id do WebSocket é loja, mesmo com CUSTOMER no quadro");
        checar(ConversaRaspadinha.Lado(Fala("oi", autor: "SDK-9"), null, "sdk-9") == LadoFala.Loja,
            "LD-4 autor igual ao usuário do SDK é loja (sem diferença de maiúscula)");
        foreach (var tipo in new[] { "SUPPORT", "ADMIN", "IFOOD", "LOGISTIC", "DRIVER" })
            checar(ConversaRaspadinha.Lado(Fala("oi", userType: tipo), "loja-1", null) == LadoFala.Desconhecido,
                $"LD-5 userType {tipo} não é cliente (suporte e sistema ficam de fora)");
        checar(ConversaRaspadinha.Lado(Fala("oi", userType: null), "loja-1", "sdk-1") == LadoFala.Desconhecido,
            "LD-6 sem tipo e com autor diferente da loja: desconhecido (fala de autor desconhecido não entra)");
        checar(ConversaRaspadinha.Lado(Fala("oi", userType: null), null, null) == LadoFala.Desconhecido
               && ConversaRaspadinha.Lado(null, "a", "b") == LadoFala.Desconhecido,
            "LD-7 sem dados nenhum: desconhecido");
    }

    // ── CHAVE ────────────────────────────────────────────────────────────────
    private static void AChave(Action<bool, string> checar)
    {
        checar(ConversaRaspadinha.Chave(Fala("oi")) == "sb:8812736123", "CV-1 com msg_id a chave é sb:<msg_id>");
        var semId = new MensagemSendbird("MESG", Canal, null, null, "cli-77", "CUSTOMER", "AD-7KQ2MX", 1790000000000, false);
        checar(ConversaRaspadinha.Chave(semId) == "h:e18940e34c5d8c3820036adade7a6865e165d9c2c1b7e01bb88f0afbeca4466b",
            "CV-2 sem msg_id a chave é h:<sha256(canal|autor|created_at_ms|texto)>, ESTÁVEL entre execuções e terminais");
        checar(ConversaRaspadinha.Chave(semId) != ConversaRaspadinha.Chave(semId with { Texto = "AD-7KQ2MY" })
               && ConversaRaspadinha.Chave(semId) != ConversaRaspadinha.Chave(semId with { CriadoEmMs = 1790000000001 }),
            "CV-3 texto ou instante diferente dá outra chave");
        checar(!ConversaRaspadinha.Chave(semId).Contains("7KQ2MX"), "CV-4 a chave não carrega o texto");
    }

    // ── TRIAGEM ──────────────────────────────────────────────────────────────
    private static void ATriagem(Action<bool, string> checar)
    {
        var sinal = ConversaRaspadinha.LerSinal(SinalJson("sombra"));
        FalaTriada T(MensagemSendbird? m, CacheDeCanais? c = null, MemoriaDeEco? e = null, SinalConversa? s = null)
            => ConversaRaspadinha.Triagem(m, s ?? sinal, c ?? new CacheDeCanais(), e ?? new MemoriaDeEco(), "loja-1", null, Agora);

        var ok = T(Fala("oi, ganhei AD-7KQ2MX"));
        checar(ok.Entra && ok.Canal!.OrderId == Pedido && ok.Canal.MerchantId == Savassi && ok.Fala!.Lado == LadoFala.Cliente,
            $"TR-1 a fala do cliente numa conversa de pedido desta loja entra ({ok.Motivo})");
        checar(T(Fala("oi"), s: SinalConversa.Desligado).Motivo == "desligado",
            "TR-2 com a loja desligada nada entra (o caixa só manda o sinal)");
        checar(T(Fala("oi"), s: ConversaRaspadinha.LerSinal(SinalJson("desligado"))).Motivo == "desligado",
            "TR-3 o sinal que diz desligado também cala");
        var psiu = QuadroSendbird.Ler(TestesQuadroSendbird.Mesg("psiu", criadoMs: Ms(Agora.AddSeconds(-5)),
            extra: ",\"data\":\"{\\\"whisperMode\\\":true}\""));
        checar(T(psiu).Motivo == "sussurro", "TR-4 sussurro é descartado");
        checar(T(Fala("oi", canal: "sendbird_group_channel_1")).Motivo == "canal_fora_do_padrao",
            "TR-5 canal que não é de pedido fica de fora");

        var cache = new CacheDeCanais();
        var deFora = Fala("oi", canal: "sendbird_gc_cm_" + Pedido + "_" + Castelo);
        checar(T(deFora, cache).Motivo == "merchant_de_outra_loja", "TR-6 conversa do Castelo no caixa da Savassi fica de fora");
        checar(cache.EhDeFora(deFora.Canal!, Agora.AddHours(11)) && !cache.EhDeFora(deFora.Canal!, Agora.AddHours(13)),
            "TR-7 e o canal fica marcado como de fora por 12 h");
        checar(T(deFora, cache).Motivo == "de_fora", "TR-8 a segunda fala do canal de fora sai antes de qualquer conta");
        checar(T(Fala("oi"), s: sinal with { MerchantIds = Array.Empty<string>() }).Motivo == "sem_merchants",
            "TR-9 sinal sem a lista de merchants não deixa nada entrar (sem saber a loja, não arrisca)");

        checar(T(Fala("   ")).Motivo == "sem_texto", "TR-10 fala vazia fica de fora");
        checar(T(Fala("AD-7KQ2MX", quando: Agora.AddMinutes(-31))).Motivo == "antiga", "TR-11 fala de mais de 30 min fica de fora");
        checar(T(Fala("AD-7KQ2MX", quando: Agora.AddMinutes(-29))).Entra, "TR-12 fala de 29 min ainda entra");
        checar(T(Fala("AD-7KQ2MX", quando: Agora.AddMinutes(5))).Motivo == "antiga", "TR-13 fala no futuro (relógio torto) fica de fora");
        checar(T(Fala("oi", userType: "SUPPORT")).Motivo == "autor_desconhecido", "TR-14 autor desconhecido não entra");

        var loja = Fala("Oi, Ana! Recebemos o código", autor: "loja-1", msgId: 77);
        var c2 = new CacheDeCanais();
        checar(T(loja, c2).Motivo == "loja_fora_de_conversa", "TR-15 fala da loja fora de conversa viva não vai");
        c2.MarcarEmConversa(Canal, Agora.AddMinutes(-5));
        var lojaEm = T(loja, c2);
        checar(lojaEm.Entra && lojaEm.Fala!.Lado == LadoFala.Loja, "TR-16 fala da loja numa conversa viva vai (é o eco e o 'loja escreveu')");
        checar(!c2.EmConversa(Canal, Agora.AddHours(3)), "TR-17 a conversa viva vale 2 h");

        var eco = new MemoriaDeEco();
        eco.Registrar(Canal, "Anotado! Seu prêmio vai junto com este pedido", Agora.AddMinutes(-1));
        checar(T(Fala("anotado!  seu prêmio vai junto com este pedido"), e: eco).Motivo == "eco",
            "TR-18 fala do cliente igual ao que este caixa mandou é eco");
        checar(T(null).Motivo == "nao_e_fala", "TR-19 quadro nulo não é fala");
    }

    // ── AGRUPADOR ────────────────────────────────────────────────────────────
    private static void OAgrupador(Action<bool, string> checar)
    {
        var a = new Agrupador();
        a.Adicionar("c1", "k1", Agora);
        a.Adicionar("c1", "k2", Agora.AddSeconds(2));
        a.Adicionar("c2", "j1", Agora.AddSeconds(3));
        checar(a.Prontos(Agora.AddSeconds(5)).Count == 0, "AG-1 antes de 6 s nada sai");
        var p = a.Prontos(Agora.AddSeconds(6));
        checar(p.Count == 1 && p[0].Canal == "c1" && p[0].Chaves.SequenceEqual(new[] { "k1", "k2" }),
            "AG-2 aos 6 s da primeira fala o lote do canal sai junto, numa chamada");
        checar(a.Prontos(Agora.AddSeconds(9)).Single().Canal == "c2", "AG-3 cada canal tem o seu relógio");
        checar(a.Prontos(Agora.AddSeconds(30)).Count == 0, "AG-4 o que saiu não volta");

        var b = new Agrupador();
        var cheio = false;
        for (var i = 0; i < 12; i++) cheio |= b.Adicionar("c", "k" + i, Agora);
        checar(cheio, "AG-5 a décima fala avisa que o lote encheu");
        var lote = b.Prontos(Agora);
        checar(lote.Single().Chaves.Count == 10, "AG-6 no máximo 10 falas por chamada");
        checar(b.Esperando == 2 && b.Prontos(Agora.AddSeconds(6)).Single().Chaves.Count == 2,
            "AG-7 o resto espera o próximo lote");
        var d = new Agrupador();
        d.Adicionar("c", "k", Agora); d.Adicionar("c", "k", Agora);
        checar(d.Prontos(Agora.AddSeconds(6)).Single().Chaves.Count == 1, "AG-8 a mesma chave não entra duas vezes no lote");
    }

    // ── CORPO ────────────────────────────────────────────────────────────────
    private static void OCorpo(Action<bool, string> checar)
    {
        var t = ConversaRaspadinha.Triagem(Fala("Ã é ç AD-7KQ2MX"), ConversaRaspadinha.LerSinal(SinalJson("sombra")),
            new CacheDeCanais(), new MemoriaDeEco(), "loja-1", null, Agora);
        var (corpo, chaves) = ConversaRaspadinha.CorpoMensagens("CAIXA-01", t.Canal!, new[] { t.Fala! });
        var j = JsonNode.Parse(corpo)!.AsObject();
        checar((string?)j["acao"] == "chat_mensagens" && (string?)j["terminal"] == "CAIXA-01" && (string?)j["canal"] == Canal
               && (string?)j["ifood_order_id"] == Pedido && (string?)j["merchant_id"] == Savassi,
            "CO-1 o corpo leva ação, terminal, canal, pedido e merchant (seção 4.6)");
        var m = j["mensagens"]![0]!;
        checar((string?)m["chave"] == "sb:8812736123" && (string?)m["msg_id"] == "8812736123" && (string?)m["lado"] == "cliente"
               && (string?)m["autor_id"] == "cli-77" && (string?)m["texto"] == "Ã é ç AD-7KQ2MX"
               && ((string?)m["quando"])!.EndsWith("Z", StringComparison.Ordinal),
            "CO-2 cada fala leva chave, msg_id, lado, autor, texto e quando (UTC)");
        checar(!j.ContainsKey("loja"), "CO-3 a loja NÃO vai no corpo (vem da porta, na borda)");
        checar(corpo.Contains("Ã é ç"), "CO-4 acento vai em UTF-8, não em \\u (o corpo tem teto de 16 KB)");
        checar(chaves.Single() == "sb:8812736123", "CO-5 devolve as chaves que couberam");

        var muitas = Enumerable.Range(0, 14).Select(i => t.Fala! with { Chave = "k" + i, Texto = new string('x', 1500) }).ToList();
        var (grande, cabem) = ConversaRaspadinha.CorpoMensagens(new string('T', 90), t.Canal!, muitas);
        checar(cabem.Count <= 10 && System.Text.Encoding.UTF8.GetByteCount(grande) <= ConversaRaspadinha.TetoCorpoBytes,
            $"CO-6 até 10 falas e corpo abaixo de 16 KB ({cabem.Count} falas, {System.Text.Encoding.UTF8.GetByteCount(grande)} bytes)");
        var jg = JsonNode.Parse(grande)!.AsObject();
        checar(((string?)jg["terminal"])!.Length == 60 && ((string?)jg["mensagens"]![0]!["texto"])!.Length == 1000,
            "CO-7 terminal até 60 e texto até 1000 caracteres");

        var sinal = JsonNode.Parse(ConversaRaspadinha.CorpoSinal("CAIXA-01", "1.0.19", """{"fila":2}"""))!.AsObject();
        checar((string?)sinal["acao"] == "chat_sinal" && (string?)sinal["versao"] == "1.0.19" && (int?)sinal["estado"]!["fila"] == 2,
            "CO-8 o sinal leva terminal, versão e estado");
        var saida = JsonNode.Parse(ConversaRaspadinha.CorpoSaida("CAIXA-01", 4412, "enviada", null, "991"))!.AsObject();
        checar((string?)saida["acao"] == "chat_saida" && (long?)saida["saida_id"] == 4412 && (string?)saida["resultado"] == "enviada"
               && (string?)saida["sendbird_msg_id"] == "991" && !saida.ContainsKey("erro"),
            "CO-9 o resultado da saída leva id, resultado e o id do Sendbird");
        checar(ConversaRaspadinha.CorpoSaida("c", 1, "inventado", "x", null).Contains("\"resultado\":\"incerta\""),
            "CO-10 resultado fora do contrato vira 'incerta' (nunca um valor solto)");
        checar(ConversaRaspadinha.CorpoComanda(ConversaRaspadinha.AcaoReservar, "b1", "CAIXA-01").Contains("\"acao\":\"comanda_reservar\"")
               && ConversaRaspadinha.CorpoComanda(ConversaRaspadinha.AcaoLiberar, "b1", "CAIXA-01").Contains("\"acao\":\"comanda_liberar\"")
               && ConversaRaspadinha.CorpoImpresso("b1", "CAIXA-01").Contains("\"acao\":\"impresso\""),
            "CO-11 reservar, liberar e impresso usam as ações do contrato");
    }

    // ── RESPOSTA ─────────────────────────────────────────────────────────────
    private const string RespostaPergunta = """
        {"ok":true,"modo":"automatico","prova":false,"acao":"perguntar_sabor","motivo":null,"repetida":false,
         "resultados":[{"chave":"sb:8812736123","acao":"perguntar_sabor","motivo":null,"repetida":false}],
         "pedido":{"ifood_order_id":"3f2a8c1e-1111-4a2b-9c3d-0123456789ab","numero":"5971","cliente":"Ana Paula Souza","entrega_status":null},
         "conversa":{"id":"9b1c","estado":"aguardando_sabor","sombra":false},
         "bonus":null,"comanda":null,
         "saidas":[{"id":4412,"etapa":"pergunta_sabor","ordem":1,
           "texto":"Oi, Ana! Recebemos o código da sua raspadinha. Seu prêmio é 1 Donut Clássico. Qual sabor você quer? 1) Banoffee, 2) Boston Cream.",
           "como":"sdk","estado":"reservada",
           "canal":"sendbird_gc_cm_3f2a8c1e-1111-4a2b-9c3d-0123456789ab_ff493e25-f413-42e5-a46a-96c0b788813f",
           "ifood_order_id":"3f2a8c1e-1111-4a2b-9c3d-0123456789ab","pedido_numero":"5971"}],
         "aviso":{"tipo":"aguardando","texto":"#5971: Ana está escolhendo o prêmio no chat.","ifood_order_id":"3f2a8c1e-1111-4a2b-9c3d-0123456789ab","pedido_numero":"5971"}}
        """;

    private const string RespostaResgate = """
        {"ok":true,"modo":"automatico","prova":true,"acao":"resgatou","motivo":null,"repetida":false,
         "resultados":[{"chave":"sb:2","acao":"resgatou","motivo":null,"repetida":false}],
         "pedido":{"ifood_order_id":"3f2a8c1e-1111-4a2b-9c3d-0123456789ab","numero":5971,"cliente":"Ana Paula Souza","entrega_status":"pronto"},
         "conversa":{"id":"9b1c","estado":"resgatado","sombra":false},
         "bonus":{"id":"b1","codigo":"AD-7KQ2MX","loja":"American Day Savassi","premio_nome":"1 Donut Clássico","premio_emoji":"🍩",
                  "cliente_nome":"Ana Paula Souza","pedido_numero":"5971","ifood_order_id":"3f2a8c1e-1111-4a2b-9c3d-0123456789ab",
                  "origem":"automatico","itens":[{"nome":"Donut Homer","qtd":1},{"nome":"Donut Churros","qtd":2}],"sabores":["Homer","Churros"],
                  "assinatura":"Automatizado","comanda_onde":"caixa","criado_em":"2026-10-07T22:41:00Z"},
         "comanda":{"imprimir_aqui":true},
         "saidas":[{"id":4413,"etapa":"confirmado_sabor","ordem":1,"texto":"Anotado! Seu prêmio vai junto com este pedido: 1 Donut Homer. Aproveite!",
                    "como":"operador","estado":"operador","canal":"sendbird_gc_cm_3f2a8c1e-1111-4a2b-9c3d-0123456789ab_ff493e25-f413-42e5-a46a-96c0b788813f",
                    "ifood_order_id":"3f2a8c1e-1111-4a2b-9c3d-0123456789ab","pedido_numero":"5971"}],
         "aviso":{"tipo":"resgatou","texto":"Resgate no #5971: 1 Donut Homer para Ana.","ifood_order_id":"3f2a8c1e-1111-4a2b-9c3d-0123456789ab","pedido_numero":"5971"}}
        """;

    private static void AResposta(Action<bool, string> checar)
    {
        var r = ConversaRaspadinha.LerResposta(200, RespostaPergunta, Agora);
        checar(r.Status == StatusConversa.Ok && r.Modo == "automatico" && !r.Prova && r.Acao == "perguntar_sabor"
               && r.Motivo is null && !r.Repetida, "RP-1 ok, modo, prova, ação, motivo e repetida são lidos");
        checar(r.Resultados.Single() is { Chave: "sb:8812736123", Acao: "perguntar_sabor", Repetida: false },
            "RP-2 o resultado por fala é lido");
        checar(r.Pedido is { IfoodOrderId: Pedido, Numero: "5971", Cliente: "Ana Paula Souza", EntregaStatus: null },
            "RP-3 o pedido é lido");
        checar(r.Conversa is { Id: "9b1c", Estado: "aguardando_sabor", Sombra: false }, "RP-4 a conversa é lida");
        checar(r.Bonus is null && !r.ImprimirAqui, "RP-5 sem bônus, nada de papel");
        var s = r.Saidas.Single();
        checar(s is { Id: 4412, Etapa: "pergunta_sabor", Ordem: 1, Como: "sdk", Estado: "reservada", Canal: Canal, IfoodOrderId: Pedido, PedidoNumero: "5971" }
               && s.Texto.StartsWith("Oi, Ana!", StringComparison.Ordinal), "RP-6 a saída é lida inteira");
        checar(r.Aviso is { Tipo: "aguardando", PedidoNumero: "5971" } && r.Aviso.Texto.Contains("escolhendo"),
            "RP-7 o aviso é lido");

        var g = ConversaRaspadinha.LerResposta(200, RespostaResgate, Agora);
        checar(g.Prova && g.Acao == "resgatou" && g.Pedido?.Numero == "5971" && g.Pedido.EntregaStatus == "pronto",
            "RP-8 prova e número que chega como número também são lidos");
        var b = g.Bonus!;
        checar(b is { Id: "b1", Codigo: "AD-7KQ2MX", Premio: "1 Donut Clássico", PremioEmoji: "🍩", Cliente: "Ana Paula Souza",
                      Pedido: "5971", Loja: "American Day Savassi", IfoodOrderId: Pedido, Origem: "automatico",
                      Assinatura: "Automatizado", ComandaOnde: "caixa" },
            "RP-9 o bônus é lido com origem automatico, assinatura e quem imprime (nome inteiro do cliente)");
        checar(b.Itens!.Count == 2 && b.Itens[0] == new ItemBonus("Donut Homer", 1) && b.Itens[1] == new ItemBonus("Donut Churros", 2),
            "RP-10 os itens com o sabor e a quantidade são lidos");
        var ambos = ConversaRaspadinha.LerResposta(200,
            RespostaResgate.Replace("\"comanda_onde\":\"caixa\"", "\"comanda_onde\":\"ambos\""), Agora);
        checar(ambos.Bonus is { ComandaOnde: "caixa" } && ambos.ImprimirAqui,
            "RP-10b comanda_onde=ambos (cozinha e caixa) é a cópia do caixa: imprime aqui, como 'caixa'");
        checar(b.Quando == new DateTimeOffset(2026, 10, 7, 22, 41, 0, TimeSpan.Zero).LocalDateTime, "RP-11 a hora do bônus é a do ERP");
        checar(g.ImprimirAqui && ConversaRaspadinha.CabecalhoDaResposta(g) == "normal",
            "RP-12 comanda.imprimir_aqui dá o papel a ESTE terminal");
        checar(g.Saidas.Single().Como == "operador" && g.Aviso?.Tipo == "resgatou", "RP-13 saída de operador e aviso de resgate");

        foreach (var acao in new[] { "ignorar", "eco", "resgatou", "perguntar_sabor", "perguntar_escolha", "perguntar_codigo",
                     "perguntar_sim", "recusou", "guardou", "humano" })
        {
            var x = ConversaRaspadinha.LerResposta(200,
                "{\"ok\":true,\"modo\":\"sombra\",\"acao\":\"" + acao + "\",\"motivo\":\"m\",\"repetida\":true,\"saidas\":[],\"conversa\":null}", Agora);
            checar(x.Status == StatusConversa.Ok && x.Acao == acao && x.Motivo == "m" && x.Repetida && x.Modo == "sombra"
                   && x.Saidas.Count == 0 && x.Conversa is null,
                $"RP-14 a ação '{acao}' é lida e não inventa saída");
        }

        var desligado = ConversaRaspadinha.LerResposta(200, """{"ok":true,"modo":"desligado","acao":"ignorar","motivo":"desligado"}""", Agora);
        checar(desligado.Status == StatusConversa.Ok && desligado.Modo == "desligado" && desligado.Motivo == "desligado",
            "RP-15 'desligado' é desfecho, não erro (a fala sai da fila)");
        var recusa = ConversaRaspadinha.LerResposta(400, """{"ok":false,"motivo":"corpo_grande"}""", Agora);
        checar(recusa.Status == StatusConversa.Recusado && recusa.MotivoCru == "corpo_grande", "RP-16 recusa da borda é recusa");
        var antiga = ConversaRaspadinha.LerResposta(200, """{"ok":false,"motivo":"sem_codigo"}""", Agora);
        checar(antiga.Status == StatusConversa.Recusado, "RP-17 a borda antiga (sem a ação nova) responde como recusa, não como resgate");

        checar(ConversaRaspadinha.LerResposta(-1, "", Agora).Status == StatusConversa.SemRede, "RP-18 nada saiu: sem rede");
        checar(ConversaRaspadinha.LerResposta(0, "", Agora).Status == StatusConversa.NaoConfirmou, "RP-19 sem resposta: não confirmou");
        foreach (var st in new[] { 500, 502, 408, 425, 429 })
            checar(ConversaRaspadinha.LerResposta(st, "", Agora).Status == StatusConversa.NaoConfirmou, $"RP-20 HTTP {st} é tente depois");
        checar(ConversaRaspadinha.LerResposta(404, "", Agora).Status == StatusConversa.NuvemSemRecurso, "RP-21 404 espera a borda");
        checar(ConversaRaspadinha.LerResposta(403, "", Agora).Status == StatusConversa.SemPermissao, "RP-22 403 é caixa barrado");
        checar(ConversaRaspadinha.LerResposta(200, "nada", Agora).Status == StatusConversa.NaoConfirmou,
            "RP-23 corpo ilegível num 200 NÃO é recusa (o ERP pode ter decidido)");

        var semId = ConversaRaspadinha.LerResposta(200, """{"ok":true,"acao":"resgatou","bonus":{"codigo":"AD-1"},"saidas":[{"texto":"x"},{"id":5}]}""", Agora);
        checar(semId.Bonus is null && semId.Saidas.Count == 0, "RP-24 bônus sem id e saída sem id ou texto são ignorados");
    }

    // ── SINAL ────────────────────────────────────────────────────────────────
    private static void OSinal(Action<bool, string> checar)
    {
        var s = ConversaRaspadinha.LerSinal(SinalJson("assistido", envio: "operador",
            avisos: """[{"tipo":"expirou","texto":"#5971: Ana não respondeu sobre o prêmio.","ifood_order_id":"x","pedido_numero":"5971"}]""",
            comandas: """[{"bonus":{"id":"b9","codigo":"AD-1","pedido_numero":"5980","premio_nome":"1 Coxinha","itens":[{"nome":"Coxinha","qtd":1}],"comanda_onde":"kds"},"cabecalho":"reserva_do_caixa"}]"""));
        checar(s.Legivel && s.Ativo && s.Modo == "assistido" && s.PalavraModo == "desligado" && s.Envio == "operador"
               && s.ComandaOnde == "kds" && s.MerchantIds.Single() == Savassi && s.IntervaloS == 60 && s.Pausado is null,
            "SN-1 o sinal é lido inteiro");
        checar(s.Avisos.Single() is { Tipo: "expirou", PedidoNumero: "5971" }, "SN-2 os avisos dos outros terminais vêm junto");
        var c = s.Comandas.Single();
        checar(c.Cabecalho == "reserva_do_caixa" && c.Bonus.Cabecalho == "reserva_do_caixa" && c.Bonus.ComandaOnde == "kds"
               && c.Bonus.Itens!.Single().Nome == "Coxinha", "SN-3 a comanda reservada vem com o cabeçalho da reserva do caixa");
        var sa = ConversaRaspadinha.LerSinal(SinalJson("assistido", envio: "operador",
            comandas: """[{"bonus":{"id":"b8","codigo":"AD-2","pedido_numero":"5981","premio_nome":"1 Coxinha","itens":[{"nome":"Coxinha","qtd":1}],"comanda_onde":"ambos"},"cabecalho":"normal"}]""")
            .Replace("\"comanda_onde\":\"kds\",\"merchant_ids\"", "\"comanda_onde\":\"ambos\",\"merchant_ids\""));
        checar(sa.ComandaOnde == "caixa" && sa.Comandas.Single() is { Cabecalho: "normal", Bonus.ComandaOnde: "caixa" },
            "SN-3b com comanda_onde=ambos o caixa trata a loja e o bônus como 'caixa' (a cópia dele, sem reserva do KDS)");

        foreach (var (nome, corpo) in new (string, string?)[]
                 {
                     ("nulo", null), ("vazio", ""), ("lixo", "<html>"), ("borda antiga", """{"ok":false,"motivo":"sem_codigo"}"""),
                     ("sem modo", """{"ok":true}"""), ("modo inventado", """{"ok":true,"modo":"turbo"}"""), ("lista", "[1]"),
                 })
            checar(!ConversaRaspadinha.LerSinal(corpo).Ativo, $"SN-4 sinal ilegível conta como DESLIGADO ({nome})");
        checar(!ConversaRaspadinha.LerSinal(SinalJson("desligado")).Ativo && ConversaRaspadinha.LerSinal(SinalJson("desligado")).Legivel,
            "SN-5 desligado é legível e não ativo");
        checar(ConversaRaspadinha.LerSinal(SinalJson("sombra", envio: "turbo")).Envio == "nenhum", "SN-6 envio fora do contrato é 'nenhum'");
        checar(ConversaRaspadinha.LerSinal(SinalJson("automatico", pausado: "teto_hora")).Pausado == "teto_hora",
            "SN-7 a pausa automática é lida");
        checar(ConversaRaspadinha.LerSinal("""{"ok":true,"modo":"sombra","intervalo_s":1}""").IntervaloS == 30,
            "SN-8 o intervalo tem piso (o caixa não martela o ERP)");
        checar(TimeSpan.FromSeconds(ConversaRaspadinha.LerSinal("""{"ok":true,"modo":"sombra","intervalo_s":900}""").IntervaloS)
                   < PortaoDeEnvio.SinalValido,
            "SN-8b e teto abaixo dos 5 min de validade do sinal (senão o envio para entre um sinal e outro)");

        var sr = ConversaRaspadinha.LerSaidaResultado(200,
            """{"ok":true,"estado":"operador","reserva":{"como":"operador","texto":"Anotado!","pedido_numero":"5971","ifood_order_id":"x"}}""");
        checar(sr.Ok && sr.Estado == "operador" && sr.Reserva is { Texto: "Anotado!", PedidoNumero: "5971", IfoodOrderId: "x" },
            "SN-9 o resultado da saída devolve a reserva para a pessoa colar");
        checar(ConversaRaspadinha.LerSaidaResultado(200, """{"ok":true,"estado":"enviada","reserva":null}""").Reserva is null,
            "SN-10 saída enviada não tem reserva");

        checar(ConversaRaspadinha.LerReserva(200, """{"ok":true}""").Desfecho == DesfechoReserva.Ok, "SN-11 reserva aceita");
        foreach (var m in new[] { "ja_impresso", "reservada", "cancelado", "outra_loja", "nao_achado" })
            checar(ConversaRaspadinha.LerReserva(200, "{\"ok\":false,\"motivo\":\"" + m + "\"}") == (DesfechoReserva.Recusada, m),
                $"SN-12 reserva recusada por {m}");
        checar(ConversaRaspadinha.LerReserva(0, null).Desfecho == DesfechoReserva.SemResposta
               && ConversaRaspadinha.LerReserva(200, """{"ok":false,"motivo":"sem_codigo"}""").Desfecho == DesfechoReserva.SemResposta,
            "SN-13 sem resposta (ou motivo desconhecido) não recusa o papel: melhor dobrado que nenhum");
    }

    // ── PORTÃO ───────────────────────────────────────────────────────────────
    private static SaidaConversa Saida(long id = 4412, string como = "sdk", string estado = "reservada", string? texto = null,
        string canal = Canal, string? order = Pedido)
        => new(id, "pergunta_sabor", 1, texto ?? "Oi, Ana! Qual sabor você quer? 1) Banoffee ou 2) Homer.", como, estado,
            canal, order, "5971");

    private static void OPortao(Action<bool, string> checar)
    {
        var sinal = ConversaRaspadinha.LerSinal(SinalJson("automatico"));
        PortaoDeEnvio Novo() => new(new MemoriaDeEco(), new LimiteDeEnvio());
        var p = Novo();

        var (perm, m) = p.Decidir(Saida(), sinal, Agora.AddSeconds(-30), Agora.AddSeconds(-2), Agora);
        checar(perm is not null && m == MotivoPortao.Liberado && perm.Canal == Canal && perm.OrderUuid == Pedido,
            "PT-1 saída sdk reservada, sinal novo e envio sdk: liberada");
        var tk = perm!.ConsumirToken();
        checar(tk is { Length: 32 } && perm.ConsumirToken() is null, "PT-2 o token é de uso único");

        checar(Novo().Decidir(Saida(como: "sombra", estado: "sombra"), sinal, Agora, Agora, Agora) is (null, MotivoPortao.Sombra),
            "PT-3 sombra nunca chama o script");
        checar(Novo().Decidir(Saida(como: "operador", estado: "operador"), sinal, Agora, Agora, Agora) is (null, MotivoPortao.Operador),
            "PT-4 'operador' nunca chama o script (a pessoa cola)");
        checar(Novo().Decidir(Saida(estado: "enviada"), sinal, Agora, Agora, Agora) is (null, MotivoPortao.NaoReservada),
            "PT-5 só a saída RESERVADA a este terminal vai");
        checar(Novo().Decidir(Saida(), sinal, Agora.AddMinutes(-6), Agora, Agora) is (null, MotivoPortao.SinalVelho),
            "PT-6 sinal com mais de 5 min não envia");
        checar(Novo().Decidir(Saida(), sinal, null, Agora, Agora) is (null, MotivoPortao.SemSinal)
               && Novo().Decidir(Saida(), SinalConversa.Desligado, Agora, Agora, Agora) is (null, MotivoPortao.SemSinal),
            "PT-7 sem sinal (ou sinal ilegível) não envia");
        checar(Novo().Decidir(Saida(), ConversaRaspadinha.LerSinal(SinalJson("assistido", envio: "operador")), Agora, Agora, Agora)
                   is (null, MotivoPortao.EnvioNaoSdk),
            "PT-8 loja no Assistido (envio 'operador') não manda pelo SDK");
        checar(Novo().Decidir(Saida(), ConversaRaspadinha.LerSinal(SinalJson("automatico", pausado: "canal_errado")), Agora, Agora, Agora)
                   is (null, MotivoPortao.Pausado),
            "PT-9 loja pausada não envia");
        checar(Novo().Decidir(Saida(), sinal, Agora, Agora.AddSeconds(-55), Agora) is (null, MotivoPortao.ReservaVencida),
            "PT-10 reserva de 60 s quase vencida não envia (vai virar incerta no ERP)");
        checar(Novo().Decidir(Saida(canal: "sendbird_group_channel_1"), sinal, Agora, Agora, Agora) is (null, MotivoPortao.CanalInvalido)
               && Novo().Decidir(Saida(order: "outro-pedido"), sinal, Agora, Agora, Agora) is (null, MotivoPortao.CanalInvalido),
            "PT-11 canal fora do padrão, ou de outro pedido, não envia");
        checar(Novo().Decidir(Saida(texto: "Oi — tudo bem"), sinal, Agora, Agora, Agora) is (null, MotivoPortao.TextoInvalido)
               && Novo().Decidir(Saida(texto: "  "), sinal, Agora, Agora, Agora) is (null, MotivoPortao.TextoInvalido),
            "PT-12 texto com travessão ou vazio não sai");

        // eco: o mesmo texto na mesma conversa não sai duas vezes (incerta nunca é reenviada)
        checar(p.Decidir(Saida(id: 4499), sinal, Agora, Agora, Agora.AddSeconds(1)) is (null, MotivoPortao.Eco),
            "PT-13 o mesmo texto na mesma conversa não vai de novo (eco local de 2 h)");
        checar(p.Decidir(Saida(id: 4499, texto: "Outro texto"), sinal, Agora, Agora, Agora.AddSeconds(1)).Permissao is not null,
            "PT-14 texto diferente na mesma conversa vai");

        // limite: 20 por minuto
        var lim = Novo();
        var liberadas = Enumerable.Range(0, 25)
            .Count(i => lim.Decidir(Saida(id: i, texto: "texto " + i), sinal, Agora, Agora, Agora).Permissao is not null);
        checar(liberadas == LimiteDeEnvio.PorMinuto, $"PT-15 no máximo 20 por minuto ({liberadas})");
        var limite2 = new LimiteDeEnvio();
        for (var i = 0; i < 20; i++) limite2.Registrar(Agora);
        checar(!limite2.Pode(Agora.AddSeconds(30)) && limite2.Pode(Agora.AddSeconds(61)), "PT-17 o limite abre de novo depois de 1 min");

        checar(PortaoDeEnvio.Relato(MotivoPortao.Sombra) is null && PortaoDeEnvio.Relato(MotivoPortao.Operador) is null,
            "PT-18 sombra e operador não têm o que relatar ao ERP");
        checar(PortaoDeEnvio.Relato(MotivoPortao.Eco) is ("incerta", _) && PortaoDeEnvio.Relato(MotivoPortao.ReservaVencida) is ("incerta", _),
            "PT-19 eco e reserva vencida viram 'incerta' (nunca reenvio)");
        checar(PortaoDeEnvio.Relato(MotivoPortao.SinalVelho) is ("falhou", "portao_sinalvelho"),
            "PT-20 o resto vira 'falhou' e o ERP passa a resposta para a pessoa");
        checar(typeof(PermissaoDeEnvio).GetConstructors().Length == 0,
            "PT-21 ninguém fora do núcleo cria uma permissão de envio (o construtor é interno)");
    }

    // ── CONFIRMAÇÃO ──────────────────────────────────────────────────────────
    private static void AConfirmacao(Action<bool, string> checar)
    {
        var c = new ConfirmacaoDeEnvio();
        c.Aguardar(1, Canal, "Anotado! Seu prêmio vai junto", Agora);
        c.Aguardar(2, Canal, "Outro texto que nunca volta", Agora);
        c.Aguardar(3, Canal, "Terceiro texto", Agora);
        var saindo = QuadroSendbird.Ler("MESG{\"message\":\"Anotado!  Seu prêmio vai junto\",\"channel_url\":\"" + Canal + "\"}");
        checar(c.Ver(saindo, enviado: true) is null && c.Pendente(1), "CF-1 o quadro que SAI marca, mas espera a volta com o id");
        var volta = QuadroSendbird.Ler(TestesQuadroSendbird.Mesg("Anotado! Seu prêmio vai junto", autor: "loja-1", msgId: 991));
        checar(c.Ver(volta, enviado: false) is (1, "991") && !c.Pendente(1), "CF-2 a volta confirma na hora, com o msg_id");
        var saindo3 = QuadroSendbird.Ler("MESG{\"message\":\"Terceiro texto\",\"channel_url\":\"" + Canal + "\"}");
        c.Ver(saindo3, enviado: true);
        checar(c.Vencidas(Agora.AddSeconds(14)).Count == 0, "CF-3 antes de 15 s nada vence");
        var v = c.Vencidas(Agora.AddSeconds(15));
        checar(v.Count == 2 && v.Contains((2, false)) && v.Contains((3, true)),
            "CF-4 aos 15 s: a que foi vista saindo vira 'enviada', a outra vira 'incerta'");
        checar(c.Vencidas(Agora.AddSeconds(60)).Count == 0 && !c.Pendente(2),
            "CF-5 vencida sai da espera e não volta (incerta nunca é reenviada)");
        var outra = QuadroSendbird.Ler(TestesQuadroSendbird.Mesg("Anotado! Seu prêmio vai junto", canal: "sendbird_gc_cm_" + Pedido + "_" + Castelo));
        var c2 = new ConfirmacaoDeEnvio();
        c2.Aguardar(7, Canal, "Anotado! Seu prêmio vai junto", Agora);
        checar(c2.Ver(outra, false) is null, "CF-6 o mesmo texto em OUTRA conversa não confirma");

        // o script disse que não mandou: só sem quadro nenhum é "falhou"
        var c3 = new ConfirmacaoDeEnvio();
        c3.Aguardar(10, Canal, "Texto dez", Agora);
        c3.Aguardar(11, Canal, "Texto onze", Agora);
        c3.Aguardar(12, Canal, "Texto doze", Agora);
        c3.Ver(QuadroSendbird.Ler("MESG{\"message\":\"Texto onze\",\"channel_url\":\"" + Canal + "\"}"), enviado: true);
        c3.Ver(QuadroSendbird.Ler(TestesQuadroSendbird.Mesg("Texto doze", autor: "loja-1", msgId: 992)), enviado: false);
        checar(c3.Cancelar(10) == false && !c3.Pendente(10), "CF-7 erro do script sem quadro nenhum: sai da espera como 'não foi' (falhou)");
        checar(c3.Cancelar(11) == true, "CF-8 erro do script com o quadro visto saindo: dúvida, vira incerta (nunca reenvio)");
        checar(c3.Cancelar(12) is null, "CF-9 o quadro já confirmou antes do script responder: vale o quadro");
        checar(c3.Vencidas(Agora.AddMinutes(1)).Count == 0, "CF-10 cancelada não vence depois (não relata duas vezes)");
    }

    // ── ESTADO DO SINAL ──────────────────────────────────────────────────────
    private static void OEstado(Action<bool, string> checar)
    {
        var sdk = new JsonObject
        {
            ["achou"] = true, ["instancias"] = 2, ["user_id_igual_ws"] = true, ["tem_order_uuid"] = true, ["congeladas"] = 3,
            ["uid"] = "usuario-secreto-da-loja", ["texto"] = "AD-7KQ2MX", ["canal"] = Canal, ["achou_x"] = "mentira",
        };
        var quadros = new JsonObject { ["mesg"] = 42 };
        var canais = new Dictionary<string, int> { ["ff49"] = 18, ["c35f"] = 7, ["nao-hex-longo"] = 1 };
        var e = ConversaRaspadinha.EstadoDoSinal("logado", true, new DateTime(2026, 10, 7, 19, 39, 0), 0, quadros, sdk, canais);
        var j = JsonNode.Parse(e)!.AsObject();
        checar((string?)j["gestor"] == "logado" && (bool?)j["ws_vivo"] == true && (int?)j["fila"] == 0
               && (int?)j["quadros"]!["mesg"] == 42 && ((string?)j["ultimo_quadro_em"])!.EndsWith("Z"),
            "ES-1 o estado leva gestor, ws_vivo, último quadro, fila e quadros");
        checar((bool?)j["sdk"]!["achou"] == true && (int?)j["sdk"]!["instancias"] == 2 && (int?)j["sdk"]!["congeladas"] == 3
               && (int?)j["sdk"]!["canais_cm_por_merchant"]!["ff49"] == 18, "ES-2 e as contagens do SDK");
        checar(!e.Contains("usuario-secreto") && !e.Contains("7KQ2MX") && !e.Contains(Pedido) && !e.Contains("mentira")
               && !e.Contains("nao-hex-longo"),
            "ES-3 nunca texto nem id: o que a página mandou fora da lista branca não sai");
        checar(JsonNode.Parse(ConversaRaspadinha.EstadoDoSinal("qualquer", false, null, -3, null, null, null))!["gestor"]!.ToString() == "ausente",
            "ES-4 estado do gestor fora do contrato vira 'ausente'");
    }

    // ── O PAPEL ──────────────────────────────────────────────────────────────
    private static async Task OPapel(Action<bool, string> checar)
    {
        var bonusNovo = new BonusRaspadinha("b1", "AD-7KQ2MX", "1 Donut Clássico", null, "Ana Paula Souza", "5971",
            "American Day Savassi", Pedido, "automatico", Agora, new[] { new ItemBonus("Donut Homer", 1) }, "Automatizado", "caixa", "normal");

        async Task<(ResultadoPapel R, List<string> Ordem)> Rodar(BonusRaspadinha b, bool repetida, string? erroPapel,
            (int, string?)? reserva = null)
        {
            var ordem = new List<string>();
            var r = await ConversaRaspadinha.TirarPapelAsync(b, repetida, "CAIXA-01",
                () => { ordem.Add("papel"); return Task.FromResult(erroPapel); },
                (nome, corpo) =>
                {
                    var acao = (string?)JsonNode.Parse(corpo)!["acao"];
                    ordem.Add(acao!);
                    return Task.FromResult(acao == "comanda_reservar" && reserva is { } x ? x : (200, (string?)"{\"ok\":true}"));
                });
            return (r, ordem);
        }

        var (ok, o1) = await Rodar(bonusNovo, false, null);
        checar(ok.Desfecho == DesfechoPapel.Saiu && ok.ImpressoAvisado && o1.SequenceEqual(new[] { "papel", "impresso" }),
            $"PP-1 o 'impresso' só vai DEPOIS do papel ({string.Join(",", o1)})");
        var (falhou, o2) = await Rodar(bonusNovo, false, "sem bobina");
        checar(falhou.Desfecho == DesfechoPapel.NaoSaiu && o2.SequenceEqual(new[] { "papel", "comanda_liberar" }),
            $"PP-2 papel que não saiu não avisa 'impresso' e libera a reserva para outro ({string.Join(",", o2)})");
        var (rep, o3) = await Rodar(bonusNovo, true, null);
        checar(rep.Desfecho == DesfechoPapel.Saiu && o3.SequenceEqual(new[] { "comanda_reservar", "papel", "impresso" }),
            "PP-3 nova tentativa reserva de novo antes do papel (a reserva vale 2 min)");
        var (outro, o4) = await Rodar(bonusNovo, true, null, (200, "{\"ok\":false,\"motivo\":\"ja_impresso\"}"));
        checar(outro.Desfecho == DesfechoPapel.NaoEraMeu && outro.Erro == "ja_impresso" && !o4.Contains("papel"),
            "PP-4 reserva recusada (o KDS já imprimiu): nenhum papel aqui");
        var (semRede, o5) = await Rodar(bonusNovo, true, null, (0, null));
        checar(semRede.Desfecho == DesfechoPapel.Saiu && o5.Contains("papel"),
            "PP-5 sem resposta na reserva o papel sai assim mesmo (melhor dobrado que nenhum)");
        var antigo = bonusNovo with { ComandaOnde = null, Cabecalho = null, Itens = null, Assinatura = null, Origem = "caixa" };
        var (_, o6) = await Rodar(antigo, true, "x");
        checar(o6.SequenceEqual(new[] { "papel" }), "PP-6 bônus do fluxo antigo não reserva nem libera (só o papel, como sempre)");
    }

    // ── BANCO, POLÍTICA DE IMPRESSÃO E FILA ─────────────────────────────────
    private static async Task ABancoEAFila(Action<bool, string> checar)
    {
        var db = Path.Combine(Path.GetTempPath(), "conversa-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var antes = Banco.CaminhoForcado;
        Banco.CaminhoForcado = db;
        try
        {
            Banco.Migrar(db);
            using var cx = Banco.Abrir(db);
            Isolamento.SemNuvem(cx);

            // ── política de impressão ────────────────────────────────────
            BonusRaspadinha B(string id, string? onde) => new(id, "AD-" + id, "1 Coxinha", null, "Bruno", "5980", "Savassi", Pedido,
                "automatico", Agora, new[] { new ItemBonus("Coxinha", 1) }, "Automatizado", onde);

            var kds = ConversaRaspadinha.LerResposta(200,
                RespostaResgate.Replace("\"comanda_onde\":\"caixa\"", "\"comanda_onde\":\"kds\"").Replace("\"imprimir_aqui\":true", "\"imprimir_aqui\":false"), Agora);
            ConversaRaspadinha.Aplicar(cx, new[] { "nada" }, kds, Agora);
            checar(ChatRaspadinha.Bonus("b1") is { ComandaOnde: "kds", Cabecalho: null }
                   && ChatRaspadinha.ParaImprimir().All(b => b.Id != "b1"),
                "PI-1 com comanda_onde=kds o caixa NÃO imprime na hora (o papel é do KDS)");

            ConversaRaspadinha.GravarBonus(cx, B("b2", "caixa"), ConversaRaspadinha.CabecalhoNormal, Agora);
            checar(ChatRaspadinha.ParaImprimir().Any(b => b.Id == "b2"), "PI-2 com caixa e imprimir_aqui, imprime aqui");
            ConversaRaspadinha.GravarBonus(cx, B("b3", "caixa"), null, Agora);
            checar(ChatRaspadinha.ParaImprimir().All(b => b.Id != "b3"),
                "PI-3 sem a reserva (imprimir_aqui falso: a comanda é de outro caixa) não imprime");
            var semPapel = ChatRaspadinha.SemPapelHoje(Agora);
            checar(semPapel.Any(b => b.Id == "b2") && semPapel.All(b => b.Id != "b3" && b.Id != "b1"),
                "PI-3b o Reimprimir do caixa não tira de novo a comanda que é do KDS ou de outro caixa (só a daqui)");
            ConversaRaspadinha.GravarBonus(cx, B("b4", "caixa"), ConversaRaspadinha.CabecalhoNormal, Agora);
            ChatRaspadinha.MarcarDeOutro("b4", "ja_impresso");
            checar(ChatRaspadinha.SemPapelHoje(Agora).All(b => b.Id != "b4"),
                "PI-3c nem a que a reserva recusou (já impressa em outro lugar, ou desfeita)");

            var sinalComanda = ConversaRaspadinha.LerSinal(SinalJson("sombra",
                comandas: """[{"bonus":{"id":"b1","codigo":"AD-7KQ2MX","pedido_numero":"5971","comanda_onde":"kds"},"cabecalho":"reserva_do_caixa"}]"""));
            foreach (var c in sinalComanda.Comandas) ConversaRaspadinha.GravarBonus(cx, c.Bonus, c.Cabecalho, Agora);
            var reserva = ChatRaspadinha.ParaImprimir().FirstOrDefault(b => b.Id == "b1");
            checar(reserva is { Cabecalho: "reserva_do_caixa" }, "PI-4 aos 90 s o sinal dá a comanda ao caixa como reserva, e aí ela sai");
            checar(reserva is not null && ChatRaspadinha.ComandaLinhas(reserva, 40).Select(LinhaEscala.Limpa).First().Contains("RESGATE (RESERVA DO CAIXA)"),
                "PI-5 e sai com o título de reserva do caixa");
            checar(reserva is { Itens.Count: 2, Assinatura: "Automatizado" }, "PI-6 a reserva não apaga os itens e a assinatura que já estavam aqui");

            // ── 07/10 (revisão): o claim volta com a recusa, e o papel tirado na mão conta ──
            ConversaRaspadinha.GravarBonus(cx, B("b5", "kds"), ConversaRaspadinha.CabecalhoReservaDoCaixa, Agora);
            checar(ChatRaspadinha.ReivindicarImpressao("b5"), "PI-7a o caixa reivindica a reserva do caixa");
            ChatRaspadinha.MarcarDeOutro("b5", "reservada");
            checar(ChatRaspadinha.ParaImprimir().All(b => b.Id != "b5") && ChatRaspadinha.SemPapelHoje(Agora).All(b => b.Id != "b5"),
                "PI-7 recusada pela reserva (o KDS pegou): sai das listas deste caixa");
            ConversaRaspadinha.GravarBonus(cx, B("b5", "kds"), ConversaRaspadinha.CabecalhoReservaDoCaixa, Agora);
            checar(ChatRaspadinha.ParaImprimir().Any(b => b.Id == "b5"),
                "PI-8 o KDS também falhou e o sinal entrega de novo a este caixa: a comanda volta (o claim voltou com a recusa)");

            ConversaRaspadinha.GravarBonus(cx, B("b6", "caixa"), ConversaRaspadinha.CabecalhoNormal, Agora);
            ChatRaspadinha.ReivindicarImpressao("b6");
            ChatRaspadinha.AnotarFalhaDeImpressao("b6", "sem papel");
            checar(ChatRaspadinha.ParaImprimir().Any(b => b.Id == "b6"), "PI-9a sem papel: a comanda volta para a varredura");
            ChatRaspadinha.MarcarImpressoAqui("b6");
            checar(ChatRaspadinha.ParaImprimir().All(b => b.Id != "b6") && ChatRaspadinha.SemPapelHoje(Agora).All(b => b.Id != "b6"),
                "PI-9 saiu pelo Reimprimir: a varredura não tira a mesma comanda de novo neste caixa");

            // ── o fluxo antigo para com o chat novo ligado ──────────────
            Vendas.GravarConfig(cx, ChatRaspadinha.ChaveConfigLoja, "1");
            checar(ChatRaspadinha.LigadoNaLoja(cx), "AN-1 sem sinal, a chave antiga continua valendo como antes");
            ConversaRaspadinha.GravarSinal(cx, SinalJson("sombra"), Agora);
            checar(!ChatRaspadinha.LigadoNaLoja(cx), "AN-2 com o chat novo em Sombra, o fluxo antigo para (nem grava)");
            ConversaRaspadinha.GravarSinal(cx, SinalJson("desligado"), Agora);
            checar(ChatRaspadinha.LigadoNaLoja(cx), "AN-3 sinal desligado devolve o fluxo antigo");
            ConversaRaspadinha.GravarSinal(cx, "{lixo", Agora);
            checar(ChatRaspadinha.LigadoNaLoja(cx), "AN-4 sinal ilegível conta como desligado");
            checar(ChatRaspadinha.LerMotivo("chat_automatico_ativo") == MotivoChat.LojaDesligada
                   && ChatRaspadinha.TextoDeTela(ChatRaspadinha.LerResposta(200, """{"ok":false,"motivo":"chat_automatico_ativo"}""", Agora)) is null,
                "AN-5 a recusa 'chat_automatico_ativo' do SQL 149 é silêncio no fluxo antigo");

            // ── a fila sem rede ──────────────────────────────────────────
            ConversaRaspadinha.GravarSinal(cx, SinalJson("sombra"), Agora);
            var canal = new CanalDaFala(Canal, Pedido, Savassi);
            FalaConversa F(string chave, string texto, DateTime quando)
                => new(chave, chave.StartsWith("sb:") ? chave[3..] : null, LadoFala.Cliente, "cli-77", texto, new DateTimeOffset(quando));
            string Estado(string k) => cx.ExecuteScalar<string>("SELECT estado FROM raspadinha_conversa_fala WHERE chave = @K", new { K = k })!;
            string? Texto(string k) => cx.ExecuteScalar<string?>("SELECT texto FROM raspadinha_conversa_fala WHERE chave = @K", new { K = k });

            var criado = Agora.AddMinutes(-5);
            checar(ConversaRaspadinha.Registrar(cx, canal, F("sb:1", "AD-7KQ2MX", criado), criado), "FS-1 a fala é gravada");
            checar(!ConversaRaspadinha.Registrar(cx, canal, F("sb:1", "AD-7KQ2MX", criado), criado),
                "FS-2 a mesma fala não vira segunda linha nem segunda chamada");
            checar(cx.ExecuteScalar<long>("SELECT COUNT(*) FROM outbox WHERE tipo = @T AND ref_id = @C",
                       new { T = ConversaRaspadinha.TipoNaFila, C = Canal }) == 1,
                "FS-3 e enfileirada na mesma transação, com o canal como referência");
            ConversaRaspadinha.Registrar(cx, canal, F("sb:2", "homer", criado.AddSeconds(3)), criado);

            var chamadas = new List<string>();
            Func<string, string, Task<(int, string?)>> Servidor(int st, string? corpo)
                => (nome, c) => { chamadas.Add(c); return Task.FromResult((st, corpo)); };

            var r1 = await ConversaRaspadinha.ResolverNaFilaAsync("sb:1", Servidor(0, null), Agora);
            checar(r1.Ok is null && Estado("sb:1") == "pendente" && Texto("sb:1") == "AD-7KQ2MX",
                "FS-4 sem rede a fala ESPERA na fila, com o texto (nada se perde)");
            var r1b = await ConversaRaspadinha.ResolverNaFilaAsync("sb:1", Servidor(404, ""), Agora);
            checar(r1b.Ok is null && r1b.Erro!.Contains("raspadinha-chat"), "FS-5 borda sem publicar é espera, com o motivo legível");
            checar(JsonNode.Parse(chamadas[^1])!["mensagens"]!.AsArray().Count == 2,
                "FS-6 a fila manda TODAS as falas pendentes do canal numa chamada só");

            var eventos = new List<RespostaConversa>();
            void Ouvir(RespostaConversa r) => eventos.Add(r);
            ConversaRaspadinha.ResolvidaNaFila += Ouvir;
            try
            {
                var r2 = await ConversaRaspadinha.ResolverNaFilaAsync("sb:2", Servidor(200, RespostaResgate), Agora);
                checar(r2.Ok == true && Estado("sb:1") == "enviada" && Estado("sb:2") == "enviada",
                    "FS-7 quando a rede volta as falas são mandadas e saem da fila");
                checar(Texto("sb:1") is null && Texto("sb:2") is null, "FS-8 o texto da pessoa sai assim que a fala tem desfecho");
                checar(eventos.Count == 1 && eventos[0].Bonus?.Id == "b1",
                    "FS-9 a resposta resolvida pela fila chega ao serviço vivo (papel, envio e aviso)");
            }
            finally { ConversaRaspadinha.ResolvidaNaFila -= Ouvir; }
            var r3 = await ConversaRaspadinha.ResolverNaFilaAsync("sb:1", Servidor(200, RespostaResgate), Agora);
            checar(r3.Ok == true && r3.Erro!.Contains("entregue"), "FS-10 a linha da fila de uma fala já entregue sai sem chamada");

            // fala de mais de 2 h: vai, e quem descarta é o servidor
            var velha = Agora.AddHours(-3);
            ConversaRaspadinha.Registrar(cx, canal, F("sb:3", "AD-9ZZ9ZZ", velha), Agora.AddMinutes(-10));
            Func<string, string, Task<(int, string?)>> ServidorDeVerdade = (nome, corpo) =>
            {
                var q = DateTimeOffset.Parse((string)JsonNode.Parse(corpo)!["mensagens"]![0]!["quando"]!);
                var antiga = q < new DateTimeOffset(Agora).AddHours(-2);
                return Task.FromResult((200, (string?)("{\"ok\":true,\"modo\":\"sombra\",\"acao\":\"ignorar\",\"motivo\":\""
                    + (antiga ? "antiga" : "sem_gatilho") + "\",\"saidas\":[]}")));
            };
            var r4 = await ConversaRaspadinha.ResolverNaFilaAsync("sb:3", ServidorDeVerdade, Agora);
            checar(r4.Ok == true && Estado("sb:3") == "enviada" && Texto("sb:3") is null
                   && ChatRaspadinha.ParaImprimir().All(b => b.Codigo != "AD-9ZZ9ZZ"),
                "FS-11 fala de mais de 2 h é descartada PELO SERVIDOR ('antiga'), sem papel e sem texto guardado");

            // a tela ainda está juntando: a fila não mexe
            ConversaRaspadinha.Registrar(cx, canal, F("sb:4", "oi", Agora), Agora.AddSeconds(-20));
            var antesDe = chamadas.Count;
            var r5 = await ConversaRaspadinha.ResolverNaFilaAsync("sb:4", Servidor(200, RespostaResgate), Agora);
            checar(r5.Ok is null && chamadas.Count == antesDe, "FS-12 fala que a tela ainda está juntando não é tocada pela fila");

            // a loja desligou: o que esperava sai sem chamada
            ConversaRaspadinha.GravarSinal(cx, SinalJson("desligado"), Agora);
            var r6 = await ConversaRaspadinha.ResolverNaFilaAsync("sb:4", Servidor(200, RespostaResgate), Agora.AddMinutes(5));
            checar(r6.Ok == true && chamadas.Count == antesDe && Estado("sb:4") == "descartada",
                "FS-13 loja desligada: a fala pendente é descartada sem chamada");

            // recusa e caixa barrado
            ConversaRaspadinha.GravarSinal(cx, SinalJson("sombra"), Agora);
            ConversaRaspadinha.Registrar(cx, canal, F("sb:5", "oi", Agora), Agora.AddMinutes(-5));
            var r7 = await ConversaRaspadinha.ResolverNaFilaAsync("sb:5", Servidor(403, ""), Agora);
            checar(r7.Ok == false && Estado("sb:5") == "descartada", "FS-14 caixa barrado é recusa permanente");
            checar(ConversaRaspadinha.Pendentes(cx) == 0, "FS-15 nada fica pendente à toa");
            checar((await ConversaRaspadinha.ResolverNaFilaAsync("sb:nao-existe", Servidor(200, ""), Agora)).Ok == true,
                "FS-16 fala que sumiu não trava a fila");

            var apagadas = ConversaRaspadinha.Faxina(cx, Agora.AddDays(3));
            checar(apagadas >= 5 && cx.ExecuteScalar<long>("SELECT COUNT(*) FROM raspadinha_conversa_fala") == 0,
                $"FS-17 a faxina de 2 dias apaga as falas ({apagadas})");

            checar(Drenagem.TiposComHandler.Contains(ConversaRaspadinha.TipoNaFila)
                   && Drenagem.JanelaPropria.Any(j => j.Tipo == ConversaRaspadinha.TipoNaFila && j.Janela is > 0 and <= 10),
                "FS-18 o tipo novo está na drenagem, com janela própria pequena (não disputa com a venda)");
            checar(Drenagem.PrazoDoTransitorio(ConversaRaspadinha.TipoNaFila) == TimeSpan.FromHours(6),
                "FS-19 e desiste em 6 h, como a mensagem do chat antigo");
        }
        finally
        {
            Banco.CaminhoForcado = antes;
            SqliteConnection.ClearAllPools();
            try { File.Delete(db); } catch { }
        }
    }

    // ── PELO FONTE ───────────────────────────────────────────────────────────
    private static void Fonte(Action<bool, string> checar)
    {
        var raiz = Raiz();
        string Ler(params string[] partes)
        {
            if (raiz is null) return "";
            var p = Path.Combine(new[] { raiz }.Concat(partes).ToArray());
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }
        var quadro = Ler("Pdv.Nucleo", "QuadroSendbird.cs");
        var conversa = Ler("Pdv.Nucleo", "ConversaRaspadinha.cs");
        var chat = Ler("Pdv.Nucleo", "ChatRaspadinha.cs");
        var servico = Ler("ServicoConversaChat.cs");
        var tela = Ler("Telas", "ChatIfood.xaml.cs");
        var venda = Ler("Telas", "Venda.xaml.cs");
        var impressao = Ler("Impressao.cs");
        var nucleoENovo = new[] { quadro, conversa, chat, servico };

        checar(nucleoENovo.All(s => s.Length > 0), "FN-0 os arquivos do resgate pelo chat existem");
        // 07/10 (revisão): o papel que sai pelo Reimprimir conta como papel (claim local e, se é deste caixa, o ERP)
        var servicoRaspadinha = Ler("ServicoRaspadinhaChat.cs");
        checar(System.Text.RegularExpressions.Regex.Matches(servicoRaspadinha, @"await PapelSaiuNaMaoAsync\(b\)").Count == 2
               && servicoRaspadinha.Contains("ChatRaspadinha.MarcarImpressoAqui(b.Id);")
               && servicoRaspadinha.Contains("if (b.ComandaOnde is null || b.Cabecalho is null) return;")
               && servicoRaspadinha.Contains("LembrarImpresso(b.Id);"),
            "FN-0b o Reimprimir (todos e um só) grava o claim e avisa o ERP só do papel deste caixa");
        // FT-2 de novo, nos arquivos novos: o caixa não conhece o formato do código
        checar(nucleoENovo.All(s => !s.Contains("\"AD-\"", StringComparison.Ordinal)
                                     && !Regex.IsMatch(s, @"ABCDEFGHJKMNPQRSTUVWXYZ|AD-\[A-Z|AD\[-")),
            "FN-1 nenhum formato de código no núcleo nem no serviço (FT-2 continua): quem acha é o ERP");
        // FT nova: nenhuma lista de palavras-chave
        var palavras = new Regex("\"(ganhei|brinde|brindes|resgat|regat|premio|premiad|rapadinha|raspinha|raspaidnha|raspadinhas|"
                                 + "depois|outro dia|proximo pedido|proxima vez|nao quero|guardar|guarda|agora nao|deixa pra la|"
                                 + "sim|nao|uhum|bora|homer|quero)\"");
        // "premio" é nome de campo do JSON e "brinde" é rótulo de papel: um termo solto não é lista.
        // Três ou mais termos do vocabulário do ERP no mesmo arquivo já são uma lista.
        var maisTermos = nucleoENovo.Select(s => palavras.Matches(s).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().Count()).Max();
        checar(maisTermos < 3,
            $"FN-2 nenhuma lista de palavras-chave, negação ou sabor no caixa: a regra mora no ERP, numa cópia só ({maisTermos})");

        // o script de envio num lugar só
        var arquivos = raiz is null ? Array.Empty<string>() : Directory.GetFiles(raiz, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "publish" + Path.DirectorySeparatorChar)
                        && !f.Contains("Pdv.Testes")).ToArray();
        var comEnvio = arquivos.Where(f => File.ReadAllText(f).Contains("sendUserMessage", StringComparison.Ordinal)).ToList();
        var iScript = tela.IndexOf("private const string ScriptSendbird = \"\"\"", StringComparison.Ordinal);
        var fimScript = iScript < 0 ? -1 : tela.IndexOf("\"\"\";", iScript + 40, StringComparison.Ordinal);
        var todasDentro = Regex.Matches(tela, "sendUserMessage").All(m => m.Index > iScript && m.Index < fimScript);
        checar(comEnvio.Count == 1 && comEnvio[0].EndsWith("ChatIfood.xaml.cs", StringComparison.Ordinal) && iScript > 0 && todasDentro,
            $"FN-3 o script de envio existe num lugar só ({string.Join(", ", comEnvio.Select(Path.GetFileName))})");

        // o ExecuteScriptAsync do envio só atrás do portão
        var chamadas = arquivos.Sum(f => Regex.Matches(File.ReadAllText(f), @"pdvSb\.enviar\(").Count);
        var iMetodo = tela.IndexOf("Task<ResultadoDoScript> EnviarPeloSdkAsync(PermissaoDeEnvio permissao)", StringComparison.Ordinal);
        var iChamada = tela.IndexOf("pdvSb.enviar(", StringComparison.Ordinal);
        var entre = iMetodo > 0 && iChamada > iMetodo ? tela[iMetodo..iChamada] : "";
        checar(chamadas == 1 && entre.Length > 0 && entre.Length < 2500 && !Regex.IsMatch(entre, @"\n    (private|public|internal) ")
               && entre.Contains("permissao.ConsumirToken()", StringComparison.Ordinal),
            "FN-4 o envio pela página só acontece dentro do método que recebe a permissão do portão, com o token dela");
        checar(tela.Contains("window.__pdvEnvioToken = {JsonSerializer.Serialize(token)}; window.pdvSb ? window.pdvSb.enviar(", StringComparison.Ordinal),
            "FN-5 o token é posto na MESMA execução que chama o envio");
        var js = iScript > 0 && fimScript > iScript ? tela[iScript..fimScript] : "";
        checar(js.Contains("window.__pdvEnvioToken = null", StringComparison.Ordinal) && js.Contains("esperado !== a.token", StringComparison.Ordinal),
            "FN-6 a página recusa sem o token e o apaga (uso único)");
        checar(!js.Contains(".focus(", StringComparison.Ordinal) && !js.Contains("location.", StringComparison.Ordinal),
            "FN-7 o envio não mexe no foco nem navega");
        checar(js.Contains("orderUuid !== a.orderUuid", StringComparison.Ordinal) && js.Contains("isFrozen", StringComparison.Ordinal)
               && js.Contains("'CUSTOMER'", StringComparison.Ordinal) && js.Contains("enviados.length >= 20", StringComparison.Ordinal),
            "FN-8 a página confere o pedido do canal, se não está congelado, se tem cliente, e o limite de 20 por minuto");
        foreach (var erro in new[] { "sdk_ausente", "canal_errado", "congelada", "sem_cliente", "erro_envio" })
            checar(js.Contains("'" + erro + "'", StringComparison.Ordinal), $"FN-9 a página devolve o erro do contrato '{erro}'");

        // abrir e colar: pelo uuid, nunca pela busca
        var iColar = tela.IndexOf("window.pdvAbrirConversaDoPedido = function", StringComparison.Ordinal);
        var colar = iColar > 0 ? tela[iColar..Math.Min(tela.Length, iColar + 3500)] : "";
        checar(colar.Contains("location.hash = '#/home/order-display/expedition/' + u", StringComparison.Ordinal)
               && colar.Contains("ifdl-icon-chat", StringComparison.Ordinal) && colar.Contains("respColar(", StringComparison.Ordinal)
               && !colar.Contains("pdvBuscarConversa", StringComparison.Ordinal),
            "FN-10 'Abrir e colar' abre pelo uuid e confere o número antes de colar; nunca usa a busca por número");
        checar(colar.Contains("var semHolofoteAntes = !!window.__pdvSemHolofote;", StringComparison.Ordinal)
               && colar.Contains("window.__pdvSemHolofote = semHolofoteAntes;", StringComparison.Ordinal),
            "FN-10b no fim, o 'Abrir e colar' devolve o modo de antes (não deixa o holofote desligado para sempre)");

        // quadros: recebidos e enviados vão para o serviço novo; só o recebido vira fala
        checar(tela.Contains("ServicoConversaChat.Quadro(p, enviado: false, conexao, WsUser(conexao))", StringComparison.Ordinal)
               && tela.Contains("ServicoConversaChat.Quadro(p, enviado: true, conexao, WsUser(conexao))", StringComparison.Ordinal)
               && servico.Contains("if (enviado) return;", StringComparison.Ordinal),
            "FN-11 o quadro que sai da página só confirma envio; nunca vira fala");
        checar(tela.Contains("QuadroSendbird.UserIdDaUrl(u.GetString())", StringComparison.Ordinal),
            "FN-12 o id da loja vem do user_id da URL do WebSocket, por conexão");
        checar(Regex.IsMatch(venda, @"ServicoConversaChat\.Avisou\s*\+=") && venda.Contains("Abrir", StringComparison.Ordinal)
               && venda.Contains("Clipboard.SetText", StringComparison.Ordinal),
            "FN-13 a tela de venda mostra os avisos com Abrir e colar e Copiar");

        // a espera da confirmação nasce ANTES do script: o SDK só responde depois que o quadro de
        // volta já passou pelo CDP, e esperando depois nada casava (toda resposta virava "incerta")
        var iEnviarUma = servico.IndexOf("private static async Task EnviarUmaAsync(", StringComparison.Ordinal);
        var iAguardar = iEnviarUma < 0 ? -1 : servico.IndexOf("Confirmacoes.Aguardar(", iEnviarUma, StringComparison.Ordinal);
        var iScriptChamado = iEnviarUma < 0 ? -1 : servico.IndexOf("ponte.EnviarPeloSdk(", iEnviarUma, StringComparison.Ordinal);
        checar(iEnviarUma > 0 && iAguardar > iEnviarUma && iScriptChamado > iAguardar,
            "FN-19 a confirmação pelo quadro começa a esperar antes de o script mandar");

        // o rastro nunca grava o texto de ninguém
        checar(!Regex.IsMatch(servico, @"Diag\([^;]*\{[^}]*\.Texto\}", RegexOptions.Singleline),
            "FN-14 o rastro do serviço grava o TAMANHO do texto, nunca a fala");

        // nada com travessão em texto de tela, mensagem ou impressão
        var iVenda = venda.IndexOf("RESGATE PELO CHAT DO iFOOD: os avisos do chat novo", StringComparison.Ordinal);
        var fVenda = iVenda < 0 ? -1 : venda.IndexOf("WhatsApp da loja: selo", iVenda, StringComparison.Ordinal);
        var vendaNova = iVenda > 0 && fVenda > iVenda ? venda[iVenda..fVenda] : "";
        checar(vendaNova.Length > 0, "FN-15a o trecho novo da tela de venda existe");
        checar(Regex.IsMatch(vendaNova, @"_avisoConversa\?\.TextoParaColar[\s\S]{0,200}AvisoLeve\(a\.Texto\);\s*return;"),
            "FN-15b aviso que só informa não cobre o toast com Abrir e colar (o texto para colar não some)");
        var textos = new[] { conversa, servico, vendaNova, quadro }
            .SelectMany(s => Regex.Matches(s, "\"([^\"\\n]*)\"").Select(m => m.Groups[1].Value)).ToList();
        checar(textos.All(t => !t.Contains('—') && !t.Contains('–')), "FN-15 nenhum texto novo tem travessão");
        checar(!(ConversaRaspadinha.TextoMandar("5971") + ConversaRaspadinha.TextoFalhaEnvio("5971") + ConversaRaspadinha.TextoAbraECole("5971"))
                   .Any(ch => ch is '—' or '–')
               && ConversaRaspadinha.TextoMandar("5971") == "#5971: mande esta resposta no chat."
               && ConversaRaspadinha.TextoFalhaEnvio("5971") == "#5971: não consegui mandar a resposta. Mande pelo chat."
               && ConversaRaspadinha.TextoAbraECole("#5971") == "Abra a conversa do #5971 e cole",
            "FN-16 as linhas do caixa são as da seção 2.2, palavra por palavra");

        // Impressao.cs não é tocado por esta entrega (ele tem mudança alheia em andamento: o diff de
        // git não separa as duas, então a prova é pelo conteúdo)
        checar(impressao.Length > 0 && !Regex.IsMatch(impressao, @"ConversaRaspadinha|ServicoConversaChat|comanda_onde|RESGATE|ItemBonus"),
            "FN-17 Impressao.cs não tem nada desta entrega");
        checar(Ler("Pdv.csproj").Contains("<Version>1.0.20</Version>", StringComparison.Ordinal), "FN-18 a versão é 1.0.20");
    }

    private static string? Raiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj"))) return dir.FullName;
        return null;
    }
}
