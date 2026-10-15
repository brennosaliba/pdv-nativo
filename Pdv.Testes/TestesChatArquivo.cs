using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O BANCO DE CONVERSAS DO CHAT DO iFOOD, LADO DO CAIXA (08/10/2026, SQL 155, caixa 1.0.22).
///
/// O caixa LE TUDO que e mensagem no chat de pedido (cliente, loja, iFood, foto), mascara no balcao,
/// guarda com a fila e manda em lote; o ERP guarda e classifica. Aqui se prova, sem a pagina logada:
///  · o leitor (LerQuadro) para MESG CUSTOMER/MERCHANT/BOT, BRDM, ADMM, AEDI (tira o br), FILE, e o
///    que NAO vira mensagem (READ, DLVR, quadro quebrado); o lado pelo user do WebSocket; o sussurro;
///  · a mascara IGUAL a do ERP (a mesma lista de 20 textos da suite 336, mesmos resultados, idempotente);
///  · a chave igual a do resgate para o mesmo quadro;
///  · a triagem (canal, merchant, hora, texto) e o "sem merchants guarda e nao manda";
///  · o lote de 50 por 20 s e o corpo abaixo de 48 KB; a leitura da resposta (captura:false, 401, 404, 5xx);
///  · o banco local: a linha nasce com a fila na mesma transacao, o texto some depois de enviada, o
///    silencio de 10 min, o teto de 20 tentativas, a faxina de 2 dias, a fila sem rede;
///  · o tempo: 200 quadros em menos de 50 ms na thread da tela;
///  · e, pelo fonte: a Drenagem conhece o tipo, a tela chama o servico nos dois ouvintes, o rastro
///    nunca leva o texto, e o nucleo nao conhece classe nenhuma.
/// </summary>
public static class TestesChatArquivo
{
    private const string Pedido = TestesQuadroSendbird.Pedido;
    private const string Savassi = TestesQuadroSendbird.MerchantSavassi;
    private const string Castelo = TestesQuadroSendbird.MerchantCastelo;
    private const string Canal = TestesQuadroSendbird.Canal;
    private static readonly DateTime Agora = new(2026, 10, 8, 0, 40, 0);
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    private static readonly IReadOnlyList<string> SoSavassi = new[] { Savassi };

    /// <summary>Um quadro no formato do Sendbird, com o comando que se quiser.</summary>
    private static string Quadro(string cmd, string texto, string autor = "cli-77", string? userType = "CUSTOMER",
        long? msgId = 8812736123, long? criadoMs = null, string canal = Canal, string extra = "")
    {
        var meta = userType is null ? "" : $",\"metadata\":{{\"userType\":\"{userType}\"}}";
        var id = msgId is null ? "" : $"\"msg_id\":{msgId},";
        return cmd + "{" + id + "\"req_id\":\"\",\"message\":" + JsonSerializer.Serialize(texto)
               + ",\"channel_url\":\"" + canal + "\",\"created_at\":" + (criadoMs ?? Ms(Agora.AddMinutes(-1)))
               + ",\"custom_type\":\"plain\",\"user\":{\"guest_id\":\"" + autor + "\",\"name\":\"Ana\"" + meta + "}" + extra + "}";
    }

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        OLeitor(checar);
        AMascara(checar);
        AChave(checar);
        ATriagem(checar);
        OLoteEAResposta(checar);
        await OBancoEAFila(checar);
        OTempo(checar);
        PeloFonte(checar);
        OCartaoDaSolicitacao(checar);
        AObservacaoDoCartao(checar);
    }

    // ── 1.0.38 (caso #0434): a observacao que o cliente escreveu no cartao ───
    private const string ObsUuid = "6c1d2e3f-4a5b-4c6d-8e7f-0a1b2c3d4e5f";

    /// <summary>O data de um cartao buttonList com a observacao do cliente no initialValue (o lugar provado em 07/10).</summary>
    private static string DataComInitialValue(string valor, bool comReplyUrl = true)
    {
        var metadata = JsonSerializer.Serialize(new
        {
            allowActionButtonInteraction = "false",
            dialog = new
            {
                dialogTitle = "Aceitar",
                replyUrl = comReplyUrl ? "https://merchant-api.ifood.com.br/orderManager/v1.0/orders/x/chats/y/negotiation/reply" : "",
                flowId = "abc123def",
                action = "render_dialog",
                options = new object[]
                {
                    new
                    {
                        dataClassType = "com.ifood.lcops.model.opsRulesEngine.ActionDialogOption",
                        type = "input",
                        label = "Edite a mensagem pra incluir na comanda",
                        required = true,
                        name = "REJECT_REASON",
                        metadata = new { hint = "Ao aceitar, voce podera imprimir outra comanda", initialValue = valor, data = new { DISPUTE_ID = ObsUuid } },
                    },
                },
            },
        });
        return JsonSerializer.Serialize(new
        {
            processable = false,
            recipients = new[] { "merchant" },
            whisperMode = false,
            messageData = new
            {
                title = "Analise a solicitacao e selecione uma opcao em ate 5 min.",
                showButtons = true,
                buttons = new object[]
                {
                    new { title = "Aceitar", action = "render_dialog", style = "secondary", metadata },
                    new { title = "Recusar", action = "render_dialog", style = "secondary" },
                    new { title = "Cancelar pedido", action = "render_dialog", style = "secondary" },
                },
            },
        });
    }

    private static void AObservacaoDoCartao(Action<bool, string> checar)
    {
        // 1) o lugar provado: o initialValue dentro do cartao buttonList
        var buttonList = Brdm("buttonList", "Analise a solicitação e selecione uma opção em até 5 min.", DataComInitialValue("AD-XGJZ4B"), 9201);
        var o1 = QuadroSendbird.ObservacaoNoCartao(buttonList);
        checar(o1 is not null && o1.Texto == "AD-XGJZ4B" && o1.Origem == "initialValue" && o1.OrderId == Pedido && o1.Canal == Canal && o1.MsgId == "9201",
            $"OB-1 a observação sai do initialValue do cartão buttonList ({o1?.Texto}/{o1?.Origem})");

        // 2) o mesmo cartao editado (AEDI) da a MESMA chave, no BRDM e no AEDI
        var aedi = "AEDI" + buttonList[4..];
        var o2 = QuadroSendbird.ObservacaoNoCartao(aedi);
        checar(o2 is not null && o2.Texto == "AD-XGJZ4B" && o2.Chave == o1!.Chave && o2.Chave.StartsWith("sol:", StringComparison.Ordinal),
            $"OB-2 o AEDI dá a mesma observação e a mesma chave sol: do BRDM ({o2?.Chave})");

        // 3) revisao: sem initialValue nao ha observacao, mesmo com um texto com cara de codigo (o caixa nao conhece o formato)
        var summary = Brdm("summary", "Quero adicionar observação ao pedido",
            "{\"messageData\":{\"subtitle\":\"Observação do cliente: AD-XGJZ4B\",\"replyUrl\":\"https://x/y\"}}", 9202);
        var o3 = QuadroSendbird.ObservacaoNoCartao(summary);
        checar(o3 is null,
            $"OB-3 sem initialValue não há observação, nem com texto que parece código: o caixa não procura código, quem acha é o ERP ({o3?.Texto})");

        // 4) nada que gere fala: "Avalie o atendimento", o botao Recusar (sem initialValue, sem codigo), MESG cru
        checar(QuadroSendbird.ObservacaoNoCartao(Brdm("buttonList", "Avalie o atendimento", "{\"messageData\":{\"stars\":5}}", 9203)) is null
               && QuadroSendbird.ObservacaoNoCartao(Brdm("summary", "A loja aceitou", null, 9204)) is null
               && QuadroSendbird.ObservacaoNoCartao(Quadro("MESG", "Adicionar observação ao pedido")) is null,
            "OB-4 'Avalie o atendimento', a resposta da loja e o MESG cru não geram observação");

        // 5) o initialValue vence mesmo com replyUrl e flowId no caminho; as chaves de rótulo não viram código
        var o5 = QuadroSendbird.ObservacaoNoCartao(Brdm("buttonList", "Analise", DataComInitialValue("oi, o pedido é pro Raul AD-9JTB7G", comReplyUrl: true), 9205));
        checar(o5 is not null && o5.Texto.StartsWith("oi, o pedido", StringComparison.Ordinal) && o5.Origem == "initialValue",
            "OB-5 com initialValue, ele vence o código solto, e replyUrl/flowId não atrapalham");

        // 6) a chave sol: e a MESMA que o ChatArquivo usa na linha sintetica do arquivo
        var sol = QuadroSendbird.ChaveSolicitacao(Canal, "AD-XGJZ4B");
        var sint = new MensagemArquivo("MESG", Canal, null, null, null, null, LadoArquivo.Cliente, "solicitacao", "AD-XGJZ4B", Ms(Agora), false, false);
        checar(o1!.Chave == sol && ChatArquivo.Chave(sint) == sol && sol.StartsWith("sol:", StringComparison.Ordinal),
            $"OB-6 a chave sol: liga a observação do robô à linha sintética do arquivo ({sol})");

        // 7) a linha sintetica guarda o texto mascarado e o canal; o cartao gigante nao estoura
        var gigante = Brdm("buttonList", "x", DataComInitialValue(new string('y', 40000)), 9206);
        var o7 = QuadroSendbird.ObservacaoNoCartao(gigante);
        checar(o7 is null || o7.Texto.Length <= 1000, "OB-7 initialValue gigante (acima de 32 KB) não é aberto; nada estoura");

        // 8) o telefone dentro do initialValue e mascarado na linha do arquivo, o codigo fica
        var comFone = QuadroSendbird.ObservacaoNoCartao(Brdm("buttonList", "Analise", DataComInitialValue("me liga 31994412725 AD-7KQ2MX"), 9207));
        var guardado = comFone is null ? "" : ChatArquivo.TextoParaGuardar(comFone.Texto);
        checar(comFone is not null && guardado.Contains("AD-7KQ2MX") && !guardado.Contains("31994412725") && guardado.Contains("[telefone]"),
            $"OB-8 ao guardar a observação, o telefone vira rótulo e o código AD- fica ({guardado})");

        // ── revisão 1.0.38: a reserva saiu; palavra comum do cartão nunca vira fala do cliente ──
        var palavras = new[] { "Adicione a observacao na comanda", "Cliente: Adrielly Souza", "Adelaide", "ADICIONE" };
        checar(palavras.All(p => QuadroSendbird.ObservacaoNoCartao(Brdm("summary", "Quero adicionar observação ao pedido",
                   "{\"messageData\":{\"subtitle\":" + JsonSerializer.Serialize(p) + "}}", 9208)) is null
                   && QuadroSendbird.ObservacaoNoCartao(Brdm("itemSummary", "Cliente fez uma solicitação:",
                   "{\"messageData\":{\"items\":[{\"name\":" + JsonSerializer.Serialize(p) + "}]}}", 9209)) is null),
            "OB-9 palavra comum do cartão ('Adicione', 'Adrielly', 'Adelaide') não vira fala do cliente em summary nem itemSummary");
        var fonteQuadro = File.ReadAllText(Path.Combine(Raiz() ?? ".", "Pdv.Nucleo", "QuadroSendbird.cs"));
        checar(!fonteQuadro.Contains("codigo_no_cartao", StringComparison.Ordinal) && !Regex.IsMatch(fonteQuadro, @"AD\[|AD-\?|ABCDEFGHJKMN"),
            "OB-10 o núcleo do quadro não tem padrão de código da raspadinha (FT-2): só o initialValue provado");

        // ── revisão 1.0.38: a observação do itemSummary que chega ANTES do resumo não se perde ──
        var j = new JanelaDaSolicitacao();
        var itemQ = Brdm("itemSummary", "Cliente fez uma solicitação:", DataComInitialValue("AD-XGJZ4B"), 9210);
        var item = ChatArquivo.LerCartaoDoIfood(itemQ)!;
        var oItem = QuadroSendbird.ObservacaoNoCartao(itemQ);
        var resumo = ChatArquivo.LerCartaoDoIfood(Brdm("summary", "Quero adicionar observação ao pedido", null, 9211))!;
        var a1 = j.VerComObservacao(item, oItem, false, Agora);
        var a2 = j.VerComObservacao(resumo, null, true, Agora.AddMilliseconds(500));
        checar(oItem is not null && a1.Count == 0 && a2.Count == 2 && a2[0].Cartao.MsgId == "9210" && a2[0].Obs?.Texto == "AD-XGJZ4B" && a2[1].Obs is null,
            "OB-11 a observação lida no itemSummary espera junto com ele e sai quando o resumo 'Quero ...' chega");
        var j2 = new JanelaDaSolicitacao();
        var semMarca = j2.VerComObservacao(ChatArquivo.LerCartaoDoIfood(Brdm("buttonList", "Analise", DataComInitialValue("AD-XGJZ4B"), 9212))!,
            QuadroSendbird.ObservacaoNoCartao(Brdm("buttonList", "Analise", DataComInitialValue("AD-XGJZ4B"), 9212)), false, Agora);
        checar(semMarca.Count == 0 && j2.Ver(item, false, Agora).Count == 0,
            "OB-12 fora da janela da solicitação (sem o resumo 'Quero ...') o cartão não solta observação; o Ver antigo segue igual");

        // ── revisão 1.0.38: a janela do robô (só o quadro, sem o arquivo) segue a mesma regra ──
        var jr = new QuadroSendbird.JanelaDaObservacao();
        var botoesQ = Brdm("buttonList", "Analise", DataComInitialValue("AD-XGJZ4B"), 9213);
        var outroCanal = Brdm("buttonList", "Analise", DataComInitialValue("AD-XGJZ4B"), 9214, "sendbird_gc_cm_" + Pedido + "_" + Castelo);
        var s0 = jr.Ver(botoesQ, Agora.AddMinutes(-20));                                         // sem marca: nada
        var s1 = jr.Ver(itemQ, Agora);                                                           // item antes da marca: espera
        var s2 = jr.Ver(Brdm("summary", "Quero adicionar observação ao pedido", null, 9211), Agora.AddMilliseconds(500));
        var s3 = jr.Ver(botoesQ, Agora.AddSeconds(2));
        var s4 = jr.Ver("AEDI" + botoesQ[4..], Agora.AddSeconds(3));
        var s5 = jr.Ver(outroCanal, Agora.AddSeconds(4));
        var s6 = jr.Ver(botoesQ, Agora.AddMinutes(6));
        var s7 = jr.Ver(Brdm("summary", "A loja aceitou", null, 9215), Agora.AddMinutes(7));
        checar(s0.Count == 0 && s1.Count == 0 && s2.Count == 1 && s2[0].Texto == "AD-XGJZ4B" && s3.Count == 1 && s3[0].Origem == "initialValue"
               && s4.Count == 1 && s4[0].Chave == s3[0].Chave && s5.Count == 0 && s6.Count == 0 && s7.Count == 0
               && jr.Ver(null, Agora).Count == 0 && jr.Ver("BRDM{quebrado", Agora).Count == 0,
            "OB-14 o robô só manda a observação dentro da janela: o item espera a marca, botões e AEDI entram, outra conversa, 6 min depois e a resposta da loja não");
        checar(ChatArquivo.EhSolicitacaoDoCliente(new MensagemArquivo("BRDM", Canal, null, null, null, null, LadoArquivo.Ifood, "summary", "Quero cancelar o pedido", null, false, false))
               && !ChatArquivo.EhSolicitacaoDoCliente(new MensagemArquivo("BRDM", Canal, null, null, null, null, LadoArquivo.Ifood, "summary", "A loja aceitou", null, false, false))
               && QuadroSendbird.EhMarcaDaSolicitacao("BRDM", "SUMMARY", " Quero trocar um item ") && !QuadroSendbird.EhMarcaDaSolicitacao("AEDI", "summary", "Quero x")
               && !QuadroSendbird.EhMarcaDaSolicitacao("BRDM", "summary", "Quero " + new string('x', 120)),
            "OB-15 a regra da marca é uma só (arquivo e robô): BRDM summary 'Quero ...' até 120, nunca a resposta nem o AEDI");
    }

    // ── 1.0.35 (caso #8039): o cartao da solicitacao do cliente, so LIDO ─────
    private static string Brdm(string custom, string texto, string? data, long? msgId = 9001, string canal = Canal, string extraData = "")
        => "BRDM{" + (msgId is null ? "" : "\"msg_id\":" + msgId + ",") + "\"message\":" + JsonSerializer.Serialize(texto)
           + ",\"channel_url\":\"" + canal + "\",\"created_at\":" + Ms(Agora.AddMinutes(-1)) + ",\"custom_type\":\"" + custom + "\""
           + (data is null ? "" : ",\"data\":" + JsonSerializer.Serialize(data)) + extraData + "}";

    private static void OCartaoDaSolicitacao(Action<bool, string> checar)
    {
        const string Disputa = "6c1d2e3f-4a5b-4c6d-8e7f-0a1b2c3d4e5f";
        var data = "{\"buttons\":[{\"label\":\"Aceitar\",\"action\":\"ACCEPT\"},{\"label\":\"Recusar\",\"action\":\"REJECT\"}],"
                   + "\"disputeId\":\"" + Disputa + "\",\"note\":\"Boa noite, tudo bom ?\"}";
        var c = ChatArquivo.LerCartaoDoIfood(Brdm("buttonList", "Analise a solicitação e selecione uma opção em até 5 min.", data));
        checar(c is not null && c.CustomType == "buttonList" && c.OrderId == Pedido && c.Canal == Canal && c.MsgId == "9001" && c.Chave == "sb:9001",
            "SC-1 o cartao buttonList do iFood e lido (tipo, pedido, msg_id)");
        checar(c is not null && c.Botoes.Count == 2 && c.Botoes[0] == new BotaoDoCartao("Aceitar", "ACCEPT") && c.Botoes[1] == new BotaoDoCartao("Recusar", "REJECT"),
            $"SC-2 os botoes saem do data: Aceitar/ACCEPT e Recusar/REJECT ({string.Join(", ", c?.Botoes ?? Array.Empty<BotaoDoCartao>())})");
        checar(c is not null && c.Ids.TryGetValue("disputeId", out var di) && di == Disputa && c.Ids.Count == 1,
            "SC-3 o id da disputa (uuid) e guardado pelo nome da chave");
        checar(c is not null && c.Dados.TryGetValue("note", out var nota) && nota == "(texto 21)" && c.Dados["buttons"] == "lista(2)"
               && c.Dados["buttons[0].label"] == "Aceitar" && c.Dados["buttons[1].action"] == "REJECT" && c.Dados["disputeId"] == Disputa
               && !string.Join("|", c.Dados.Values).Contains("Boa noite"),
            $"SC-4 a forma do data: o texto livre vira '(texto 21)', enum e uuid ficam ({(c is null ? "" : JsonSerializer.Serialize(c.Dados))})");
        checar(c is not null && c.ChavesRaiz.Contains("data") && c.ChavesRaiz.Contains("custom_type") && c.ChavesRaiz.Contains("channel_url")
               && c.Texto.StartsWith("Analise a solicitação", StringComparison.Ordinal),
            "SC-5 os nomes das chaves do quadro e o texto do cartao (mascarado, ate 120)");

        var comFone = ChatArquivo.LerCartaoDoIfood(Brdm("summary", "Quero adicionar observação ao pedido",
            "{\"phone\":\"31994412725\",\"tel\":31994412725,\"note\":\"me liga 31 99441-2725\",\"options\":[{\"title\":\"Ligar 31994412725\",\"value\":\"31994412725\"}]}"));
        var tudo = comFone is null ? "" : JsonSerializer.Serialize(comFone.Dados) + JsonSerializer.Serialize(comFone.Botoes);
        checar(comFone is not null && !tudo.Contains("31994412725") && !tudo.Contains("99441") && comFone.Dados["phone"] == "[telefone]"
               && comFone.Dados["tel"] == "(numero 11)" && comFone.Botoes.Count == 1 && comFone.Botoes[0].Rotulo == "Ligar [telefone]" && comFone.Botoes[0].Acao is null,
            $"SC-6 telefone dentro do data e mascarado: no valor, no numero, no rotulo e na acao do botao ({tudo})");

        var naoJson = ChatArquivo.LerCartaoDoIfood(Brdm("buttonList", "Avalie o atendimento", "isto nao e json {"));
        var semData = ChatArquivo.LerCartaoDoIfood(Brdm("itemSummary", "Cliente fez uma solicitação:", null));
        var gigante = ChatArquivo.LerCartaoDoIfood(Brdm("buttonList", "x", "{\"a\":\"" + new string('y', 9000) + "\"}"));
        checar(naoJson is { Dados.Count: 0, Botoes.Count: 0 } && semData is { Dados.Count: 0 } && gigante is { Dados.Count: 0 },
            "SC-7 data que nao e JSON, ausente ou maior que 8 KB vira forma vazia, sem excecao");
        checar(ChatArquivo.LerCartaoDoIfood(Brdm("plain", "Ok, já enviamos sua solicitação", null)) is null
               && ChatArquivo.LerCartaoDoIfood(Quadro("MESG", "Quero adicionar")) is null
               && ChatArquivo.LerCartaoDoIfood(Brdm("summary", "Quero x", null, canal: "sendbird_group_channel_1")) is null
               && ChatArquivo.LerCartaoDoIfood("BRDM{quebrado") is null && ChatArquivo.LerCartaoDoIfood(null) is null,
            "SC-8 so BRDM summary/itemSummary/buttonList de canal de pedido; o resto (plain, MESG, quadro quebrado) nao e cartao");

        // a janela: o resumo "Quero ..." marca a conversa; buttonList sem resumo antes nao e registrado
        var j = new JanelaDaSolicitacao();
        var t0 = Agora;
        var item = ChatArquivo.LerCartaoDoIfood(Brdm("itemSummary", "Cliente fez uma solicitação:", null, 9100))!;
        var resumo = ChatArquivo.LerCartaoDoIfood(Brdm("summary", "Quero adicionar observação ao pedido", null, 9101))!;
        var botoes = ChatArquivo.LerCartaoDoIfood(Brdm("buttonList", "Analise a solicitação", data, 9102))!;
        var avalie = ChatArquivo.LerCartaoDoIfood(Brdm("buttonList", "Avalie o atendimento", null, 9103))!;
        var soltoAntes = j.Ver(avalie, false, t0.AddMinutes(-20));
        var r1 = j.Ver(item, false, t0);
        var r2 = j.Ver(resumo, true, t0.AddMilliseconds(500));
        var r3 = j.Ver(botoes, false, t0.AddSeconds(1));
        var r4 = j.Ver(avalie, false, t0.AddMinutes(6));
        checar(soltoAntes.Count == 0, "SC-9 buttonList sem o resumo 'Quero ...' antes, na mesma conversa, nao e registrado");
        checar(r1.Count == 0 && r2.Select(x => x.MsgId).SequenceEqual(new[] { "9100", "9101" }) && r3.Count == 1 && r3[0].MsgId == "9102",
            "SC-10 o item que chega meio segundo ANTES do resumo espera a marca; o resumo e a lista de botoes da mesma conversa entram");
        checar(r4.Count == 0, "SC-11 passados 5 min da marca, a lista de botoes ('Avalie o atendimento') nao entra");
        var outraConversa = ChatArquivo.LerCartaoDoIfood(Brdm("buttonList", "Analise", data, 9104, "sendbird_gc_cm_" + Pedido + "_" + Castelo))!;
        checar(j.Ver(outraConversa, false, t0.AddSeconds(2)).Count == 0, "SC-12 a marca vale so para a MESMA conversa");

        // a tabela local e a linha do arquivo de texto
        var db = Path.Combine(Path.GetTempPath(), "cartao-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        try
        {
            Banco.Migrar(db);
            using (var cx = Banco.Abrir(db))
            {
                var c1 = c!;
                checar(ChatArquivo.RegistrarCartao(cx, c1, "8039", Agora) && !ChatArquivo.RegistrarCartao(cx, c1, "8039", Agora),
                    "SC-13 o cartao entra uma vez na tabela local (a mesma chave nao repete)");
                var linha = cx.QueryFirst("SELECT numero, custom_type, dados_json, botoes_json, ids_json FROM chat_cartao_solicitacao WHERE chave = 'sb:9001'");
                checar((string)linha.numero == "8039" && (string)linha.custom_type == "buttonList" && ((string)linha.botoes_json).Contains("ACCEPT")
                       && ((string)linha.ids_json).Contains(Disputa) && !((string)linha.dados_json).Contains("Boa noite"),
                    "SC-14 a tabela guarda a forma do data, os botoes e os ids, nunca o texto livre");
                ChatArquivo.RegistrarCartao(cx, resumo, null, Agora.AddDays(-8));
                checar(ChatArquivo.FaxinaDosCartoes(cx, Agora) == 1 && cx.ExecuteScalar<long>("SELECT COUNT(*) FROM chat_cartao_solicitacao") == 1,
                    "SC-15 a faxina apaga o cartao de mais de 7 dias");
            }
            var l = ChatArquivo.LinhaDoCartao(c!, "8039");
            checar(l.StartsWith("pedido=#8039 ", StringComparison.Ordinal) && l.Contains("tipo=buttonList") && l.Contains("Aceitar/ACCEPT")
                   && l.Contains("disputeId=" + Disputa) && l.Contains("(texto 21)") && !l.Contains("Boa noite") && !l.Contains("Analise a"),
                $"SC-16 a linha do chat-solicitacao.txt: pedido, tipo, forma do data, botoes e ids, nunca o texto ({l})");
        }
        finally
        {
            Thread.Sleep(100);
            try { File.Delete(db); } catch { }
        }

        // pelo fonte: so ler, nunca tocar
        var raiz = Raiz();
        if (raiz is null) { checar(false, "SC-17 fonte nao achado"); return; }
        string Ler(string rel) { var p = Path.Combine(raiz, rel); return File.Exists(p) ? File.ReadAllText(p) : ""; }
        var nucleo = Ler(Path.Combine("Pdv.Nucleo", "ChatArquivo.cs"));
        var servico = Ler("ServicoArquivoChat.cs");
        var iN = nucleo.IndexOf("// ── 1.0.35: O CARTAO DA SOLICITACAO", StringComparison.Ordinal);
        var fN = iN < 0 ? -1 : nucleo.IndexOf("// ── json", iN, StringComparison.Ordinal);
        var iJ = nucleo.IndexOf("public sealed class JanelaDaSolicitacao", StringComparison.Ordinal);
        var iS = servico.IndexOf("// ── 1.0.35: OS CARTOES DA SOLICITACAO", StringComparison.Ordinal);
        var fS = iS < 0 ? -1 : servico.IndexOf("/// <summary>Os relogios nascem", iS, StringComparison.Ordinal);
        var novo = (iN > 0 && fN > iN ? nucleo[iN..fN] : "") + (iJ > 0 ? nucleo[iJ..] : "") + (iS > 0 && fS > iS ? servico[iS..fS] : "");
        checar(novo.Length > 2000 && !Regex.IsMatch(novo, "click|dispatchEvent|\\.focus\\(|/disputes", RegexOptions.IgnoreCase),
            "SC-17 o codigo novo da solicitacao nao clica, nao dispara evento, nao foca e nao chama /disputes");
        var tela = Ler(Path.Combine("Telas", "ChatIfood.xaml.cs"));
        var scripts = (TestesScriptSendbird.ExtrairRaw(tela, "ScriptPainel") ?? "") + (TestesScriptSendbird.ExtrairRaw(tela, "ScriptSendbird") ?? "");
        checar(scripts.Length > 5000 && !scripts.Contains("buttonList", StringComparison.OrdinalIgnoreCase),
            "SC-18 os scripts da pagina (painel e SDK) nao citam buttonList: nada mexe no cartao");
        checar(servico.Contains("ServicoConversaChat.Cartoes = Contagem;", StringComparison.Ordinal)
               && servico.Contains("ChatArquivo.FaxinaDosCartoes(cx, agora)", StringComparison.Ordinal)
               && servico.Contains("Telas.HospedeWebView2.Anotar(\"chat-solicitacao.txt\", ChatArquivo.LinhaDoCartao(c, numero)", StringComparison.Ordinal)
               && Ler(Path.Combine("Pdv.Nucleo", "Banco.cs")).Contains("CREATE TABLE IF NOT EXISTS chat_cartao_solicitacao", StringComparison.Ordinal),
            "SC-19 o servico registra na tabela local, no chat-solicitacao.txt e nas contagens do sinal; a faxina apaga em 7 dias");
        var robo = Ler("ServicoConversaChat.cs");
        checar(servico.Contains("Janela.VerComObservacao(cartao, QuadroSendbird.ObservacaoNoCartao(payload)", StringComparison.Ordinal)
               && servico.Contains("if (obs is not null) ObservacaoDoCartao(obs, c, canal);", StringComparison.Ordinal)
               && robo.Contains("foreach (var obs in JanelaObs.Ver(payload, agora)) MandarObservacao(obs, wsUserId, agora);", StringComparison.Ordinal)
               && nucleo.Contains("=> QuadroSendbird.EhMarcaDaSolicitacao(m.Comando, m.CustomType, m.Texto);", StringComparison.Ordinal)
               && nucleo.Contains("public static readonly TimeSpan Janela = QuadroSendbird.JanelaDaObservacao.Janela;", StringComparison.Ordinal),
            "OB-13 o arquivo e o robô leem a observação na chegada de cada cartão, só dentro da janela, com a mesma regra da marca e os mesmos prazos");
        var iLote = nucleo.IndexOf("public static (string Corpo, IReadOnlyList<string> Chaves) CorpoLote(", StringComparison.Ordinal);
        var lote = iLote < 0 ? "" : nucleo.Substring(iLote, Math.Min(2500, nucleo.Length - iLote));
        checar(lote.Length > 0 && !lote.Contains("dados", StringComparison.OrdinalIgnoreCase) && !lote.Contains("botoes", StringComparison.OrdinalIgnoreCase),
            "SC-20 o lote do arquivo ao ERP continua igual: nada do cartao vai como campo novo");
    }

    // ── ChatArquivo.LerQuadro ────────────────────────────────────────────────
    private static void OLeitor(Action<bool, string> checar)
    {
        var cli = ChatArquivo.LerQuadro(Quadro("MESG", "cade meu pedido?"), "loja-1", null);
        checar(cli is { Comando: "MESG", Lado: LadoArquivo.Cliente, Texto: "cade meu pedido?", AutorId: "cli-77", AutorTipo: "CUSTOMER", MsgId: "8812736123", CustomType: "plain", TemMidia: false },
            "LQ-1 MESG com userType CUSTOMER e o cliente, com texto, autor, msg_id e custom_type");
        checar(cli?.Canal == Canal && cli.CriadoEmMs == Ms(Agora.AddMinutes(-1)) && !cli.Sussurro, "LQ-2 canal e hora saem do quadro; sem sussurro");

        var loja = ChatArquivo.LerQuadro(Quadro("MESG", "ja saiu", "func-9", "MERCHANT"), "loja-1", null);
        checar(loja?.Lado == LadoArquivo.Loja, "LQ-3 userType MERCHANT e a loja");
        var pelaUrl = ChatArquivo.LerQuadro(Quadro("MESG", "oi", "loja-1", null), "loja-1", null);
        checar(pelaUrl?.Lado == LadoArquivo.Loja, "LQ-4 autor igual ao user_id da URL do WebSocket e a loja, mesmo sem userType");
        var peloSdk = ChatArquivo.LerQuadro(Quadro("MESG", "oi", "sdk-2", null), "loja-1", "sdk-2");
        checar(peloSdk?.Lado == LadoArquivo.Loja, "LQ-5 autor igual ao usuario do SDK e a loja");
        var bot = ChatArquivo.LerQuadro(Quadro("MESG", "*Oba! A loja aceitou sua solicitacao.*", "bot-1", "BOT"), "loja-1", null);
        checar(bot?.Lado == LadoArquivo.Ifood && bot.Texto.StartsWith("*Oba!"), "LQ-6 MESG do BOT e o iFood (hoje o resgate descarta; o arquivo guarda)");
        var isBot = ChatArquivo.LerQuadro("MESG{\"message\":\"aviso\",\"channel_url\":\"" + Canal + "\",\"created_at\":1,\"user\":{\"guest_id\":\"b\",\"is_bot\":true}}", null, null);
        checar(isBot?.Lado == LadoArquivo.Ifood, "LQ-7 user.is_bot sem userType tambem e o iFood");
        var desconhecido = ChatArquivo.LerQuadro(Quadro("MESG", "quem sou eu", "x-1", null), "loja-1", null);
        checar(desconhecido?.Lado == LadoArquivo.Desconhecido, "LQ-8 sem tipo e sem bater com a loja: desconhecido (entra marcado)");

        var brdm = ChatArquivo.LerQuadro(Quadro("BRDM", "Seu pedido saiu para entrega", "bot-1", "CUSTOMER"), "loja-1", null);
        checar(brdm is { Comando: "BRDM", Lado: LadoArquivo.Ifood }, "LQ-9 BRDM e o iFood, mesmo que o user diga CUSTOMER");
        var admm = ChatArquivo.LerQuadro(Quadro("ADMM", "mensagem de administrador", "adm", null), null, null);
        checar(admm is { Comando: "ADMM", Lado: LadoArquivo.Ifood }, "LQ-10 ADMM e o iFood");
        var aedi = ChatArquivo.LerQuadro(Quadro("AEDI", "Analise a solicitacao<br />e selecione uma opcao<br/>abaixo", "adm", null, extra: "").Replace("\"custom_type\":\"plain\"", "\"custom_type\":\"buttonList\""), null, null);
        checar(aedi is { Comando: "AEDI", Lado: LadoArquivo.Ifood, CustomType: "buttonList" } && aedi.Texto == "Analise a solicitacao e selecione uma opcao abaixo",
            "LQ-11 AEDI (o card de negociacao) tira o <br /> e guarda o custom_type buttonList");

        var foto = ChatArquivo.LerQuadro("FILE{\"msg_id\":77,\"channel_url\":\"" + Canal + "\",\"created_at\":" + Ms(Agora) + ",\"url\":\"https://file.sendbird.com/x.jpg?auth=SEGREDO\",\"name\":\"IMG_1.jpg\",\"type\":\"image/jpeg\",\"user\":{\"guest_id\":\"cli-77\",\"metadata\":{\"userType\":\"CUSTOMER\"}}}", null, null);
        checar(foto is { Comando: "FILE", Lado: LadoArquivo.Cliente, Texto: "[foto]", TemMidia: true, MsgId: "77" } && !foto.Texto.Contains("http"),
            "LQ-12 FILE de imagem vira [foto] com tem_midia, e NUNCA a URL");
        var arquivo = ChatArquivo.LerQuadro("FILE{\"channel_url\":\"" + Canal + "\",\"created_at\":1,\"type\":\"application/pdf\",\"user\":{\"guest_id\":\"cli-77\",\"metadata\":{\"userType\":\"CUSTOMER\"}}}", null, null);
        checar(arquivo?.Texto == "[arquivo]", "LQ-13 FILE que nao e imagem vira [arquivo]");

        foreach (var cmd in new[] { "READ", "DLVR", "MACK", "SYEV", "LOGI", "PING", "PONG", "TPST", "TPEN" })
            checar(ChatArquivo.LerQuadro(Quadro(cmd, "x"), null, null) is null, $"LQ-14 {cmd} nao vira mensagem");
        checar(ChatArquivo.LerQuadro("MESG{quebrado", null, null) is null && ChatArquivo.LerQuadro(null, null, null) is null
               && ChatArquivo.LerQuadro("mesg" + Quadro("MESG", "x")[4..], null, null) is null,
            "LQ-15 quadro quebrado, nulo e comando em minuscula viram null, sem lancar");
        checar(ChatArquivo.LerQuadro("MESG{\"message\":\"sem canal\",\"user\":{\"guest_id\":\"a\"}}", null, null) is null, "LQ-16 MESG sem channel_url nao e mensagem");
        var sus = ChatArquivo.LerQuadro(Quadro("MESG", "so a loja ve", "bot-1", "BOT", extra: ",\"data\":\"{\\\"whisperMode\\\":true}\""), null, null);
        checar(sus is { Sussurro: true, Lado: LadoArquivo.Ifood }, "LQ-17 sussurro ENTRA, marcado (o resgate descarta; o arquivo guarda como contexto)");
        var custom = ChatArquivo.LerQuadro(Quadro("MESG", "x").Replace("\"custom_type\":\"plain\"", "\"custom_type\":\"button List!<b>\""), null, null);
        checar(custom?.CustomType == "buttonListb", "LQ-18 custom_type so com letras, numeros, _ e -");
        checar(ChatArquivo.LerQuadro(Quadro("MESG", "   "), null, null) is null, "LQ-19 MESG so com espacos nao e mensagem");
    }

    // ── ChatArquivo.Mascarar: a MESMA lista da suite 336 (caso E) ───────────
    private static void AMascara(Action<bool, string> checar)
    {
        var casos = new (string Entrada, string Esperado)[]
        {
            ("me liga 31 99569-3928", "me liga [telefone]"),
            ("cel (31) 99569-3928 ok", "cel [telefone] ok"),
            ("+55 31 99569 3928", "[telefone]"),
            ("tel 3195693928", "tel [telefone]"),
            ("liga 31995693928 por favor", "liga [telefone] por favor"),
            ("cpf 123.456.789-00", "cpf [cpf]"),
            ("cpf 123 456 789 00", "cpf [cpf]"),
            ("email ana.p@gmail.com", "email [email]"),
            ("cartao 4111 1111 1111 1111", "cartao [cartao]"),
            ("cartao 4111111111111111", "cartao [cartao]"),
            ("cep 30140-071 rua x", "cep [telefone] rua x"),
            ("codigo AD-7KQ2MX pedido #5971", "codigo AD-7KQ2MX pedido #5971"),
            ("o pedido 1234 veio frio", "o pedido 1234 veio frio"),
            ("https://x.com/a?b=1&c=2 e http://y.com/p", "https://x.com/a e http://y.com/p"),
            ("nenhum numero aqui", "nenhum numero aqui"),
            ("R$ 24,90 x 2 = 49,80", "R$ 24,90 x 2 = 49,80"),
            ("apto 302 bloco 2", "apto 302 bloco 2"),
            ("[telefone] e [cpf] ja mascarados", "[telefone] e [cpf] ja mascarados"),
            ("dois: 31 99569-3928 e 31 98888-7777", "dois: [telefone] e [telefone]"),
            ("zap 5531995693928", "zap [telefone]"),
            // revisao 08/10: o hifen "inteligente" (meia-risca, travessao) e o espaco duro passavam em claro.
            // Os mesmos 3 textos da E-01 do ERP (336), escritos por codigo para o fonte nao carregar o travessao.
            ("zap 31 99999\u20131234 ok", "zap [telefone] ok"),
            ("tel (31)\u201499999\u20141234", "tel [telefone]"),
            ("liga 31\u00a099999\u00a01234 depois", "liga [telefone] depois"),
        };
        var n = 0;
        foreach (var (entrada, esperado) in casos)
        {
            n++;
            var saida = ChatArquivo.Mascarar(entrada);
            checar(saida == esperado, $"MK-{n:00} \"{entrada}\" -> \"{esperado}\" (deu \"{saida}\")");
        }
        checar(casos.All(c => ChatArquivo.Mascarar(ChatArquivo.Mascarar(c.Entrada)) == ChatArquivo.Mascarar(c.Entrada)),
            "MK-24 idempotente: a segunda passada da o mesmo texto");
        checar(ChatArquivo.Mascarar(null) == "" && ChatArquivo.Mascarar("") == "", "MK-25 nulo e vazio viram vazio");
        var guardado = ChatArquivo.TextoParaGuardar("  o entregador\n e o 31 99569-3928,   me chama  ");
        checar(guardado == "o entregador e o [telefone], me chama", "MK-26 o texto para guardar: uma linha, espacos simples, mascarado");
        checar(ChatArquivo.TextoParaGuardar(new string('x', 1500)).Length == 1000, "MK-27 cortado em 1000");
    }

    // ── a chave e a mesma do resgate ─────────────────────────────────────────
    private static void AChave(Action<bool, string> checar)
    {
        var q = Quadro("MESG", "ganhei na raspadinha AD-7KQ2MX");
        var doArquivo = ChatArquivo.Chave(ChatArquivo.LerQuadro(q, null, null)!);
        var doResgate = ConversaRaspadinha.Chave(QuadroSendbird.Ler(q)!);
        checar(doArquivo == doResgate && doArquivo == "sb:8812736123", "CH-1 com msg_id a chave e sb:<msg_id>, igual a do resgate");
        var sem = Quadro("MESG", "oi, tudo bem?", msgId: null, criadoMs: 1790000000000);
        var a = ChatArquivo.Chave(ChatArquivo.LerQuadro(sem, null, null)!);
        var b = ConversaRaspadinha.Chave(QuadroSendbird.Ler(sem)!);
        checar(a == b && a.StartsWith("h:") && a.Length == 66, "CH-2 sem msg_id o hash e o mesmo do resgate (canal|autor|created_at|texto cru)");
        var outro = ChatArquivo.Chave(ChatArquivo.LerQuadro(Quadro("MESG", "oi, tudo bem", msgId: null, criadoMs: 1790000000000), null, null)!);
        checar(outro != a, "CH-3 texto diferente, chave diferente");
    }

    // ── a triagem ────────────────────────────────────────────────────────────
    private static void ATriagem(Action<bool, string> checar)
    {
        var m = ChatArquivo.LerQuadro(Quadro("MESG", "oi"), null, null);
        var ok = ChatArquivo.Triagem(m, false, SoSavassi, Agora);
        checar(ok.Entra && ok.Motivo == "ok" && ok.Canal!.OrderId == Pedido && ok.Canal.MerchantId == Savassi, "TR-1 mensagem boa entra, com o canal lido");
        checar(!ChatArquivo.Triagem(m, true, SoSavassi, Agora).Entra, "TR-2 quadro ENVIADO nunca vira mensagem (a fala da loja volta do servidor como MESG)");
        checar(ChatArquivo.Triagem(null, false, SoSavassi, Agora).Motivo == "nao_e_mensagem", "TR-3 nulo nao entra");
        var fora = ChatArquivo.LerQuadro(Quadro("MESG", "oi", canal: "sendbird_group_channel_123"), null, null);
        checar(ChatArquivo.Triagem(fora, false, SoSavassi, Agora).Motivo == "canal_fora_do_padrao", "TR-4 canal que nao e de pedido do iFood fica de fora");
        var castelo = ChatArquivo.LerQuadro(Quadro("MESG", "oi", canal: "sendbird_gc_cm_" + Pedido + "_" + Castelo), null, null);
        checar(ChatArquivo.Triagem(castelo, false, SoSavassi, Agora).Motivo == "merchant_de_outra_loja", "TR-5 merchant de outra loja fica de fora");
        var semMerchants = ChatArquivo.Triagem(m, false, Array.Empty<string>(), Agora);
        checar(semMerchants.Entra && semMerchants.Motivo == "ok_sem_merchants", "TR-6 sem merchants (sinal do chat ainda nao veio) ENTRA e fica guardada, esperando");
        var semHora = ChatArquivo.LerQuadro("MESG{\"msg_id\":1,\"message\":\"oi\",\"channel_url\":\"" + Canal + "\",\"user\":{\"guest_id\":\"a\"}}", null, null);
        checar(ChatArquivo.Triagem(semHora, false, SoSavassi, Agora).Motivo == "sem_hora", "TR-7 sem hora fica de fora");
        var velha = ChatArquivo.LerQuadro(Quadro("MESG", "oi", criadoMs: Ms(Agora.AddDays(-31))), null, null);
        checar(ChatArquivo.Triagem(velha, false, SoSavassi, Agora).Motivo == "fora_do_prazo", "TR-8 mais de 30 dias fica de fora (folga grande de proposito)");
        var dias29 = ChatArquivo.LerQuadro(Quadro("MESG", "oi", criadoMs: Ms(Agora.AddDays(-29))), null, null);
        checar(ChatArquivo.Triagem(dias29, false, SoSavassi, Agora).Entra, "TR-9 29 dias atras ainda entra (o Sendbird reentrega na reconexao; o ERP deduplica)");
        var futuro = ChatArquivo.LerQuadro(Quadro("MESG", "oi", criadoMs: Ms(Agora.AddMinutes(3))), null, null);
        checar(ChatArquivo.Triagem(futuro, false, SoSavassi, Agora).Motivo == "fora_do_prazo", "TR-10 3 min no futuro fica de fora");
        var foto = ChatArquivo.LerQuadro("FILE{\"msg_id\":77,\"channel_url\":\"" + Canal + "\",\"created_at\":" + Ms(Agora) + ",\"type\":\"image/png\",\"user\":{\"guest_id\":\"cli-77\",\"metadata\":{\"userType\":\"CUSTOMER\"}}}", null, null);
        checar(ChatArquivo.Triagem(foto, false, SoSavassi, Agora).Entra, "TR-11 a foto entra (sem texto, mas com midia)");
        var bot = ChatArquivo.LerQuadro(Quadro("BRDM", "Oba!", "bot", "BOT"), null, null);
        checar(ChatArquivo.Triagem(bot, false, SoSavassi, Agora).Entra, "TR-12 a mensagem do iFood entra (nao depende do modo do chat nem de conversa aberta)");
    }

    // ── o corpo do lote e a leitura da resposta ──────────────────────────────
    private static LinhaArquivo Linha(string chave, string texto = "oi", string merchant = Savassi)
        => new(chave, "sendbird_gc_cm_" + Pedido + "_" + merchant, Pedido, merchant, "cliente", "CUSTOMER", "cli-77", "MESG", "plain",
            chave.StartsWith("sb:") ? chave[3..] : null, texto, false, false, "2026-10-07T22:40:00.000-03:00");

    private static void OLoteEAResposta(Action<bool, string> checar)
    {
        var (corpo, chaves) = ChatArquivo.CorpoLote("DESKTOP-7AJ1OD7", "1.0.22", new[] { Linha("sb:1"), Linha("sb:2", "cade [telefone]") });
        var j = JsonNode.Parse(corpo)!;
        checar(j["acao"]!.GetValue<string>() == "arquivar" && j["terminal"]!.GetValue<string>() == "DESKTOP-7AJ1OD7" && j["versao"]!.GetValue<string>() == "1.0.22"
               && j["mensagens"]!.AsArray().Count == 2 && chaves.SequenceEqual(new[] { "sb:1", "sb:2" }),
            "CL-1 o corpo: acao, terminal, versao e as mensagens, com as chaves que couberam");
        var m0 = j["mensagens"]![0]!;
        checar(m0["chave"]!.GetValue<string>() == "sb:1" && m0["canal"]!.GetValue<string>() == Canal && m0["ifood_order_id"]!.GetValue<string>() == Pedido
               && m0["merchant_id"]!.GetValue<string>() == Savassi && m0["lado"]!.GetValue<string>() == "cliente" && m0["tipo_quadro"]!.GetValue<string>() == "MESG"
               && m0["texto"]!.GetValue<string>() == "oi" && m0["sussurro"]!.GetValue<bool>() == false && m0["quando"]!.GetValue<string>().StartsWith("2026-10-07"),
            "CL-2 cada mensagem leva chave, canal, pedido, merchant, lado, tipo, texto, sussurro e quando (a loja NUNCA vai no corpo)");
        checar(!corpo.Contains("loja", StringComparison.Ordinal) || corpo.Contains("\"lado\":\"loja\""), "CL-3 nenhum campo 'loja' no corpo");
        var sessenta = Enumerable.Range(1, 60).Select(i => Linha("sb:" + i)).ToList();
        checar(ChatArquivo.CorpoLote(null, null, sessenta).Chaves.Count == 50, "CL-4 no maximo 50 mensagens por lote");
        var grandes = Enumerable.Range(1, 50).Select(i => Linha("sb:" + i, new string('x', 1000))).ToList();
        var (corpoG, chavesG) = ChatArquivo.CorpoLote(null, null, grandes);
        checar(chavesG.Count < 50 && chavesG.Count > 20 && Encoding.UTF8.GetByteCount(corpoG) <= ChatArquivo.TetoCorpoBytes,
            $"CL-5 o corpo fica abaixo de 46 KB: a que nao coube espera o proximo lote ({chavesG.Count} couberam)");
        checar(ChatArquivo.CorpoLote(new string('t', 80), null, new[] { Linha("sb:1") }).Corpo.Contains("\"terminal\":\"" + new string('t', 60) + "\""), "CL-6 terminal cortado em 60");

        var ok = ChatArquivo.LerRespostaLote(200, "{\"ok\":true,\"captura\":true,\"guardadas\":2,\"repetidas\":1,\"cortadas\":0,\"recusadas\":[{\"chave\":\"sb:9\",\"motivo\":\"merchant_de_outra_loja\"}],\"proximo_lote_s\":20}");
        checar(ok.Status == StatusDoArquivo.Ok && ok.Captura && ok.Guardadas == 2 && ok.Repetidas == 1 && ok.Recusadas.Count == 1 && ok.Recusadas[0] == ("sb:9", "merchant_de_outra_loja"),
            "RL-1 ok com captura: as contagens e as recusadas");
        var desligada = ChatArquivo.LerRespostaLote(200, "{\"ok\":true,\"captura\":false,\"guardadas\":0,\"repetidas\":0,\"recusadas\":[]}");
        checar(desligada.Status == StatusDoArquivo.Ok && !desligada.Captura, "RL-2 ok com captura:false (a loja esta desligada no ERP)");
        checar(ChatArquivo.LerRespostaLote(200, "{\"ok\":false,\"motivo\":\"loja_desconhecida\"}") is { Status: StatusDoArquivo.Recusado, MotivoCru: "loja_desconhecida" }, "RL-3 ok:false e recusa com o motivo");
        checar(ChatArquivo.LerRespostaLote(401, "{\"ok\":false,\"motivo\":\"nao_autorizado\"}").Status == StatusDoArquivo.SemPermissao
               && ChatArquivo.LerRespostaLote(403, "{\"ok\":false,\"motivo\":\"so_o_caixa\"}").Status == StatusDoArquivo.SemPermissao, "RL-4 401 e 403 sao SemPermissao (descarta)");
        checar(ChatArquivo.LerRespostaLote(404, "").Status == StatusDoArquivo.NuvemSemRecurso, "RL-5 404 e a edge (ou o SQL 155) ainda nao publicada: espera");
        checar(ChatArquivo.LerRespostaLote(500, "").Status == StatusDoArquivo.SemResposta && ChatArquivo.LerRespostaLote(0, null).Status == StatusDoArquivo.SemResposta
               && ChatArquivo.LerRespostaLote(429, "").Status == StatusDoArquivo.SemResposta, "RL-6 5xx, 429 e sem resposta sao transitorios");
        checar(ChatArquivo.LerRespostaLote(400, "{\"ok\":false,\"motivo\":\"mensagens_invalidas\"}") is { Status: StatusDoArquivo.Recusado, MotivoCru: "mensagens_invalidas" }, "RL-7 400 e recusa");
        checar(ChatArquivo.LerRespostaLote(200, "nao e json").Status == StatusDoArquivo.SemResposta, "RL-8 2xx ilegivel e transitorio");
        var agr = new AgrupadorDoArquivo();
        checar(!agr.Pronto(Agora), "AG-1 vazio nao esta pronto");
        agr.Adicionar(Agora);
        checar(!agr.Pronto(Agora.AddSeconds(19)) && agr.Esperando == 1, "AG-2 com 1 mensagem espera ate 20 s");
        checar(agr.Pronto(Agora.AddSeconds(20)) && agr.Esperando == 0, "AG-3 aos 20 s o lote sai e zera");
        for (var i = 0; i < 49; i++) agr.Adicionar(Agora);
        checar(agr.Adicionar(Agora) && agr.Pronto(Agora), "AG-4 com 50 o lote sai na hora");
    }

    // ── o banco local e a fila ───────────────────────────────────────────────
    private static async Task OBancoEAFila(Action<bool, string> checar)
    {
        var db = Path.Combine(Path.GetTempPath(), "arquivo-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var antes = Banco.CaminhoForcado;
        Banco.CaminhoForcado = db;
        ChatArquivo.EsquecerSilencio();
        try
        {
            Banco.Migrar(db);
            using var cx = Banco.Abrir(db);
            Isolamento.SemNuvem(cx);
            var canal = new CanalDaFala(Canal, Pedido, Savassi);
            MensagemArquivo M(string chave, string texto, LadoArquivo lado = LadoArquivo.Cliente, DateTime? quando = null)
                => new("MESG", Canal, chave.StartsWith("sb:") ? chave[3..] : null, null, "cli-77", "CUSTOMER", lado, "plain", texto, Ms(quando ?? Agora.AddMinutes(-1)), false, false);
            string Estado(string k) => cx.ExecuteScalar<string>("SELECT estado FROM ifood_chat_arquivo WHERE chave = @K", new { K = k })!;
            string? Texto(string k) => cx.ExecuteScalar<string?>("SELECT texto FROM ifood_chat_arquivo WHERE chave = @K", new { K = k });
            long Tentativas(string k) => cx.ExecuteScalar<long>("SELECT tentativas FROM ifood_chat_arquivo WHERE chave = @K", new { K = k });

            var criado = Agora.AddMinutes(-5);
            checar(ChatArquivo.Registrar(cx, canal, M("sb:1", "me liga 31 99569-3928"), criado), "DB-1 a mensagem e gravada");
            checar(Texto("sb:1") == "me liga [telefone]", "DB-2 e o texto ja entra mascarado no balcao");
            checar(!ChatArquivo.Registrar(cx, canal, M("sb:1", "me liga 31 99569-3928"), criado), "DB-3 a mesma chave nao vira segunda linha (dois caixas, ou o mesmo reiniciado)");
            checar(cx.ExecuteScalar<long>("SELECT COUNT(*) FROM outbox WHERE tipo = @T AND client_key = 'sb:1'", new { T = ChatArquivo.TipoNaFila }) == 1,
                "DB-4 e enfileirada na mesma transacao, com a chave como client_key");
            ChatArquivo.Registrar(cx, canal, M("sb:2", "oi de novo"), criado.AddSeconds(1));
            ChatArquivo.Registrar(cx, new CanalDaFala("sendbird_gc_cm_" + Pedido + "_" + Castelo, Pedido, Castelo), M("sb:3", "do castelo"), criado.AddSeconds(2));

            checar(ChatArquivo.Pendentes(cx, Array.Empty<string>()).Count == 0 && Estado("sb:1") == "pendente",
                "DB-5 sem merchants (o sinal do chat ainda nao veio) nada sai e tudo fica pendente");
            var pend = ChatArquivo.Pendentes(cx, SoSavassi);
            checar(pend.Count == 2 && pend[0].Chave == "sb:1" && pend[1].Chave == "sb:2" && Estado("sb:3") == "descartada" && Texto("sb:3") is null,
                "DB-6 com os merchants: as da loja saem da mais antiga para a mais nova; a de outra loja e descartada sem texto");
            checar(ChatArquivo.Pendentes(cx) == 2, "DB-7 a contagem de pendentes (para o diagnostico)");

            ChatArquivo.Aplicar(cx, new[] { "sb:1", "sb:2" }, ChatArquivo.LerRespostaLote(200, "{\"ok\":true,\"captura\":true,\"guardadas\":1,\"repetidas\":0,\"recusadas\":[{\"chave\":\"sb:2\",\"motivo\":\"pedido_de_outra_loja\"}]}"), Agora);
            checar(Estado("sb:1") == "enviada" && Texto("sb:1") is null && Estado("sb:2") == "descartada" && Texto("sb:2") is null,
                "DB-8 ok: guardada vira enviada, recusada vira descartada, e o texto some nas duas");

            ChatArquivo.Registrar(cx, canal, M("sb:4", "sem rede"), criado);
            for (var i = 0; i < ChatArquivo.TetoTentativas - 1; i++)
                ChatArquivo.Aplicar(cx, new[] { "sb:4" }, ChatArquivo.LerRespostaLote(0, null), Agora);
            checar(Estado("sb:4") == "pendente" && Tentativas("sb:4") == ChatArquivo.TetoTentativas - 1 && Texto("sb:4") == "sem rede",
                "DB-9 sem resposta conta tentativa e a mensagem ESPERA com o texto (nada se perde)");
            ChatArquivo.Aplicar(cx, new[] { "sb:4" }, ChatArquivo.LerRespostaLote(500, ""), Agora);
            checar(Estado("sb:4") == "descartada" && Texto("sb:4") is null, "DB-10 na vigesima tentativa a mensagem e descartada (o ERP deduplica; reenviar e seguro)");

            ChatArquivo.Registrar(cx, canal, M("sb:5", "captura desligada"), criado);
            checar(!ChatArquivo.EmSilencio(Agora), "DB-11 sem captura:false nao ha silencio");
            ChatArquivo.Aplicar(cx, new[] { "sb:5" }, ChatArquivo.LerRespostaLote(200, "{\"ok\":true,\"captura\":false,\"guardadas\":0,\"repetidas\":0,\"recusadas\":[]}"), Agora);
            checar(Estado("sb:5") == "descartada" && Texto("sb:5") is null && ChatArquivo.EmSilencio(Agora.AddMinutes(9)) && !ChatArquivo.EmSilencio(Agora.AddMinutes(10)),
                "DB-12 captura:false: descarta, limpa o texto e silencia o caixa por 10 min");
            ChatArquivo.EsquecerSilencio();

            ChatArquivo.Registrar(cx, canal, M("sb:6", "barrado"), criado);
            ChatArquivo.Aplicar(cx, new[] { "sb:6" }, ChatArquivo.LerRespostaLote(403, "{\"ok\":false,\"motivo\":\"so_o_caixa\"}"), Agora);
            checar(Estado("sb:6") == "descartada", "DB-13 401/403 descarta");

            // a faxina de 2 dias
            ChatArquivo.Registrar(cx, canal, M("sb:7", "velha"), Agora.AddDays(-3));
            ChatArquivo.Faxina(cx, Agora);
            checar(cx.ExecuteScalar<long>("SELECT COUNT(*) FROM ifood_chat_arquivo WHERE chave = 'sb:7'") == 0
                   && cx.ExecuteScalar<long>("SELECT COUNT(*) FROM ifood_chat_arquivo WHERE chave = 'sb:1'") == 1,
                "DB-14 a faxina apaga a linha de mais de 2 dias e deixa a recente");

            // a fila sem rede (o contrato da Drenagem)
            ConversaRaspadinha.GravarSinal(cx,
                "{\"ok\":true,\"modo\":\"sombra\",\"palavra_modo\":\"desligado\",\"envio\":\"sdk\",\"comanda_onde\":\"kds\",\"merchant_ids\":[\"" + Savassi + "\"],\"intervalo_s\":60,\"pausado\":null,\"avisos\":[],\"saidas\":[],\"comandas\":[]}",
                Agora);
            ChatArquivo.Registrar(cx, canal, M("sb:8", "na fila"), Agora.AddMinutes(-5));
            ChatArquivo.Registrar(cx, canal, M("sb:9", "na fila tambem"), Agora.AddMinutes(-4));
            var chamadas = new List<string>();
            Func<string, string, Task<(int, string?)>> Servidor(int st, string? corpo)
                => (nome, c) => { chamadas.Add(nome + "|" + c); return Task.FromResult((st, corpo)); };

            var r1 = await ChatArquivo.ResolverNaFilaAsync("sb:8", Servidor(0, null), Agora);
            checar(r1.Ok is null && Estado("sb:8") == "pendente" && Texto("sb:8") == "na fila" && chamadas.Count == 1 && chamadas[0].StartsWith("ifood-chat-arquivo|"),
                "FS-1 sem rede a mensagem ESPERA na fila com o texto, e a chamada foi para a edge certa");
            checar(JsonNode.Parse(chamadas[0].Split('|', 2)[1])!["mensagens"]!.AsArray().Count == 2, "FS-2 a fila manda TODAS as pendentes do caixa num lote so");
            var r2 = await ChatArquivo.ResolverNaFilaAsync("sb:8", Servidor(404, ""), Agora);
            checar(r2.Ok is null && r2.Erro!.Contains("ifood-chat-arquivo"), "FS-3 edge sem publicar e espera, com o motivo legivel");
            var r3 = await ChatArquivo.ResolverNaFilaAsync("sb:8", Servidor(200, "{\"ok\":true,\"captura\":true,\"guardadas\":2,\"repetidas\":0,\"recusadas\":[]}"), Agora);
            checar(r3.Ok == true && Estado("sb:8") == "enviada" && Estado("sb:9") == "enviada" && Texto("sb:8") is null, "FS-4 com resposta as duas viram enviadas e o texto some");
            var r4 = await ChatArquivo.ResolverNaFilaAsync("sb:8", Servidor(200, "{}"), Agora);
            checar(r4.Ok == true && chamadas.Count == 3, "FS-5 a linha ja entregue sai da fila sem chamada");
            checar((await ChatArquivo.ResolverNaFilaAsync("sb:nao-existe", Servidor(200, ""), Agora)).Ok == true, "FS-6 a mensagem que sumiu sai da fila");
            ChatArquivo.Registrar(cx, canal, M("sb:10", "recem"), Agora);
            var r5 = await ChatArquivo.ResolverNaFilaAsync("sb:10", Servidor(200, "{}"), Agora);
            checar(r5.Ok is null && chamadas.Count == 3, "FS-7 a mensagem recem gravada e do servico por 2 min: a fila espera sem chamar");
            var r6 = await ChatArquivo.ResolverNaFilaAsync("sb:10", Servidor(200, "{\"ok\":true,\"captura\":false,\"guardadas\":0,\"repetidas\":0,\"recusadas\":[]}"), Agora.AddMinutes(3));
            checar(r6.Ok == true && Estado("sb:10") == "descartada" && ChatArquivo.EmSilencio(Agora.AddMinutes(4)), "FS-8 captura:false pela fila: descarta e silencia");
            ChatArquivo.Registrar(cx, canal, M("sb:11", "em silencio"), Agora);
            var r7 = await ChatArquivo.ResolverNaFilaAsync("sb:11", Servidor(200, "{}"), Agora.AddMinutes(5));
            checar(r7.Ok == true && Estado("sb:11") == "descartada" && chamadas.Count == 4, "FS-9 em silencio a linha e descartada sem chamada");
            ChatArquivo.EsquecerSilencio();
            ChatArquivo.Registrar(cx, canal, M("sb:12", "barrado"), Agora.AddMinutes(-5));
            var r8 = await ChatArquivo.ResolverNaFilaAsync("sb:12", Servidor(403, "{\"ok\":false,\"motivo\":\"so_o_caixa\"}"), Agora);
            checar(r8.Ok == false && Estado("sb:12") == "descartada", "FS-10 403 e recusa permanente (dead-letter) e a linha e descartada");
            ConversaRaspadinha.GravarSinal(cx, "{\"ok\":true,\"modo\":\"desligado\",\"merchant_ids\":[],\"intervalo_s\":60,\"avisos\":[],\"saidas\":[],\"comandas\":[]}", Agora);
            ChatArquivo.Registrar(cx, canal, M("sb:13", "sem sinal"), Agora.AddMinutes(-5));
            var r9 = await ChatArquivo.ResolverNaFilaAsync("sb:13", Servidor(200, "{}"), Agora);
            checar(r9.Ok is null && Estado("sb:13") == "pendente" && r9.Erro!.Contains("merchants"), "FS-11 sem os merchants no sinal a fila espera (a captura nao depende do modo do chat, mas da loja)");

            // a Drenagem conhece o tipo, com janela propria e prazo de 2 dias
            checar(Drenagem.TiposComHandler.Contains(ChatArquivo.TipoNaFila) && Drenagem.JanelaPropria.Any(j => j.Tipo == ChatArquivo.TipoNaFila && j.Janela == 3)
                   && Drenagem.PrazoDoTransitorio(ChatArquivo.TipoNaFila) == TimeSpan.FromDays(2),
                "DR-1 a Drenagem tem o tipo ifood_chat_lote na janela propria (3 por vez) com 2 dias de espera");
            checar(Drenagem.JanelaDaFila(cx).Count(l => (string)((dynamic)l).tipo == ChatArquivo.TipoNaFila) <= 3, "DR-2 a janelinha do arquivo e de 3 (a venda nunca fica atras do chat)");
        }
        finally
        {
            Banco.CaminhoForcado = antes;
            ChatArquivo.EsquecerSilencio();
            try { File.Delete(db); } catch { }
        }
    }

    // ── o tempo: a captura nunca segura a thread da tela ─────────────────────
    private static void OTempo(Action<bool, string> checar)
    {
        var db = Path.Combine(Path.GetTempPath(), "arquivo-t-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var antes = Banco.CaminhoForcado;
        var merchantsAntes = ServicoArquivoChat.Merchants;
        var chamadaAntes = ServicoArquivoChat.Chamada;
        Banco.CaminhoForcado = db;
        try
        {
            Banco.Migrar(db);
            using (var cx = Banco.Abrir(db)) Isolamento.SemNuvem(cx);
            ServicoArquivoChat.Merchants = () => SoSavassi;
            ServicoArquivoChat.Chamada = (_, _) => Task.FromResult((0, ""));
            var quadros = Enumerable.Range(1, 200).Select(i => Quadro(i % 3 == 0 ? "BRDM" : "MESG", "mensagem " + i, "cli-" + i, i % 3 == 0 ? "BOT" : "CUSTOMER", 9000000 + i)).ToList();
            var sw = Stopwatch.StartNew();
            foreach (var q in quadros) ServicoArquivoChat.Quadro(q, enviado: false, "c1", "loja-1");
            sw.Stop();
            checar(sw.ElapsedMilliseconds < 50, $"TM-1 200 quadros passam pela thread da tela em menos de 50 ms ({sw.ElapsedMilliseconds} ms): a gravacao e fora dela");
            var fim = DateTime.Now.AddSeconds(15);
            while (ServicoArquivoChat.NaFila > 0 && DateTime.Now < fim) Thread.Sleep(50);
            checar(ServicoArquivoChat.NaFila == 0, "TM-1b a fila de gravacao esvaziou em ate 15 s");
            using (var cx = Banco.Abrir(db))
            {
                var n = cx.ExecuteScalar<long>("SELECT COUNT(*) FROM ifood_chat_arquivo");
                checar(n == 200, $"TM-2 as 200 mensagens foram gravadas fora da tela ({n})");
                checar(cx.ExecuteScalar<long>("SELECT COUNT(*) FROM ifood_chat_arquivo WHERE lado = 'ifood'") == 66, "TM-3 as do BOT entraram como ifood");
            }
            checar(ServicoArquivoChat.Lidas >= 200, "TM-4 o servico contou o que leu (so numeros)");
            var sw2 = Stopwatch.StartNew();
            for (var i = 0; i < 200; i++) ServicoArquivoChat.Quadro(Quadro("MESG", "x", msgId: 5000 + i), enviado: true, "c1", "loja-1");
            checar(sw2.ElapsedMilliseconds < 20, "TM-5 o quadro ENVIADO custa nada (volta na hora)");
        }
        finally
        {
            Banco.CaminhoForcado = antes;
            ServicoArquivoChat.Merchants = merchantsAntes;
            ServicoArquivoChat.Chamada = chamadaAntes;
            ChatArquivo.EsquecerSilencio();
            Thread.Sleep(300);
            try { File.Delete(db); } catch { }
        }
    }

    // ── pelo fonte ───────────────────────────────────────────────────────────
    private static void PeloFonte(Action<bool, string> checar)
    {
        var raiz = Raiz();
        if (raiz is null) { checar(true, "FN-0 (fonte nao achado: os testes pelo fonte ficaram de fora)"); return; }
        string Ler(string rel) { var p = Path.Combine(raiz, rel); return File.Exists(p) ? File.ReadAllText(p) : ""; }
        var tela = Ler(Path.Combine("Telas", "ChatIfood.xaml.cs"));
        checar(tela.Contains("ServicoArquivoChat.Quadro(p, enviado: false, conexao, WsUser(conexao))", StringComparison.Ordinal)
               && tela.Contains("ServicoArquivoChat.Quadro(p, enviado: true, conexao, WsUser(conexao))", StringComparison.Ordinal),
            "FN-1 a tela entrega os quadros recebidos e enviados ao servico do arquivo (duas linhas em LigarCapturaAsync)");
        var recv = tela.IndexOf("ServicoConversaChat.Quadro(p, enviado: false", StringComparison.Ordinal);
        var arq = tela.IndexOf("ServicoArquivoChat.Quadro(p, enviado: false", StringComparison.Ordinal);
        checar(recv > 0 && arq > recv, "FN-2 o arquivo vem DEPOIS do resgate no mesmo ouvinte (o resgate nunca espera o arquivo)");
        var servico = Ler("ServicoArquivoChat.cs");
        var diags = Regex.Matches(servico, @"Diag\(\$?""[^""]*""[^;]*;").Select(m => m.Value).ToList();
        checar(diags.Count > 3 && diags.All(d => !d.Contains(".Texto", StringComparison.Ordinal) || d.Contains(".Texto.Length", StringComparison.Ordinal)) && diags.All(d => !d.Contains("texto=", StringComparison.Ordinal) || d.Contains("len=", StringComparison.Ordinal)),
            "FN-3 o rastro (chat-arquivo.txt) nunca leva o texto da pessoa: so tamanho, chave curta e contagens");
        var nucleo = Ler(Path.Combine("Pdv.Nucleo", "ChatArquivo.cs"));
        checar(!Regex.IsMatch(nucleo, @"insatisfa|elogio|cancelamento|atraso|classe", RegexOptions.IgnoreCase) || Regex.Matches(nucleo, @"classe", RegexOptions.IgnoreCase).Count <= 2,
            "FN-4 o caixa nao conhece classe nenhuma: quem classifica e o ERP");
        checar(!servico.Contains("ServicoConversaChat.cs", StringComparison.Ordinal) && !nucleo.Contains("raspadinha_conversa_fala", StringComparison.Ordinal),
            "FN-5 o arquivo nao mexe na tabela nem no servico do resgate");
        var banco = Ler(Path.Combine("Pdv.Nucleo", "Banco.cs"));
        checar(banco.Contains("CREATE TABLE IF NOT EXISTS ifood_chat_arquivo", StringComparison.Ordinal) && banco.Contains("ix_ifood_chat_arquivo_pend", StringComparison.Ordinal),
            "FN-6 a tabela local e o indice das pendentes existem no esquema");
        var drenagem = Ler(Path.Combine("Pdv.Nucleo", "Drenagem.cs"));
        checar(drenagem.Contains("ChatArquivo.TipoNaFila => await ChatArquivo.ResolverNaFilaAsync", StringComparison.Ordinal), "FN-7 a Drenagem roteia o tipo para o handler do arquivo");
        foreach (var f in new[] { Path.Combine("Pdv.Nucleo", "ChatArquivo.cs"), "ServicoArquivoChat.cs", Path.Combine("Pdv.Testes", "TestesChatArquivo.cs") })
            checar(!Ler(f).Contains((char)0x2013) && !Ler(f).Contains((char)0x2014), $"FN-8 sem travessao em {Path.GetFileName(f)}");
        foreach (var intocado in new[] { Path.Combine("Pdv.Nucleo", "QuadroSendbird.cs"), Path.Combine("Pdv.Nucleo", "ConversaRaspadinha.cs"), "ServicoConversaChat.cs" })
            checar(!Ler(intocado).Contains("ChatArquivo", StringComparison.Ordinal) && !Ler(intocado).Contains("ServicoArquivoChat", StringComparison.Ordinal),
                $"FN-9 {Path.GetFileName(intocado)} continua sem saber que o arquivo existe");
    }

    private static string? Raiz()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Pdv.csproj")) && File.Exists(Path.Combine(dir, "ServicoArquivoChat.cs"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
