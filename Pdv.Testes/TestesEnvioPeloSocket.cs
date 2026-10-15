using System.Text.Json;
using System.Text.Json.Nodes;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// 1.0.39 (15/10/2026, Savassi, pedido 5403): A CAMADA C DO ENVIO, PELO SOCKET.
///
/// Na Savassi o robô não respondeu ninguém desde 12:54: o caixa 1.0.38 não achava o SDK do Sendbird
/// (ele nasce num chunk que o webpack, o React, o singleton, a heap e o berço não alcançam), mas a
/// conexão do Gestor estava VIVA (6 sockets abertos, quadros chegando). A camada C não procura mais o
/// SDK: acha os WebSocket abertos do Sendbird pela heap (só API padrão do navegador), escolhe UM sem
/// chute, manda UM quadro MESG e confirma SÓ pelo eco do servidor (o mesmo req_id). Prova que:
///  · o quadro MESG tem o formato exato, sem campo indefinido, e o req_id muda a cada tentativa;
///  · a escolha do socket: pelo canal, pelo usuário da loja, o único, e ambíguo NÃO envia;
///  · o eco MESG com o req_id confirma com o msg_id; o EROR com o req_id é falha certa;
///  · sem eco no prazo é "incerta" e NUNCA há reenvio sozinho;
///  · com o SDK achado a camada C nem roda;
///  · nada de URL com chave, token ou texto do cliente no detalhe, no log ou no diagnóstico;
///  · o grupo de objetos do CDP é liberado sempre, também com exceção.
/// </summary>
public static class TestesEnvioPeloSocket
{
    private const string Canal = TestesQuadroSendbird.Canal;
    private const string Pedido = TestesQuadroSendbird.Pedido;
    private const string Savassi = TestesQuadroSendbird.MerchantSavassi;
    private const string Texto = "Oi, Ana! Vale só um prêmio por pedido. Responda 1) ou 2).";
    private const string Loja = "31994412725";
    private const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJ1c2VyIjoiMzE5OTQ0MTI3MjUifQ.c2VncmVkby1kby1zb2NrZXQ";
    private const string Chave = "Zk9QbW1hY2hhdmVTZWNyZXRhRG9Tb2NrZXQxMjM0NTY";
    // a URL como o navegador a guarda em ws.url (com a barra) e como o CDP a viu no webSocketCreated (sem a barra)
    private const string UrlLoja = "wss://ws-6913ab.sendbird.com/?p=JS&sv=4.19.9&ai=6913AB&user_id=" + Loja + "&access_token=" + Jwt;
    private const string UrlLojaCdp = "wss://ws-6913ab.sendbird.com?p=JS&sv=4.19.9&ai=6913AB&user_id=" + Loja + "&access_token=" + Jwt;
    private const string UrlEntregador = "wss://ws-6913ab.sendbird.com/?p=JS&sv=4.19.9&ai=6913AB&user_id=entregador-77&key=" + Chave;
    private const string UrlOutro = "wss://chat.ifood.com.br/socket?token=" + Chave;
    private static readonly DateTime T0 = new(2026, 10, 15, 12, 54, 10);

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        OQuadro(checar);
        OReqId(checar);
        AEscolha(checar);
        OEco(checar);
        await OEnvio(checar);
        await OGrupoLiberado(checar);
        await SdkAchadoNaoRoda(checar);
        AConfirmacao(checar);
        await OLaco(checar);
        OMascaramento(checar);
        ODiagnostico(checar);
        PeloFonte(checar);
    }

    // ── o quadro MESG e o req_id ─────────────────────────────────────────────
    private static void OQuadro(Action<bool, string> checar)
    {
        var q = EnvioPeloSocket.QuadroMesg(Canal, Texto, "pdv92-1-1760543650000");
        var esperado = "MESG{\"channel_url\":\"" + Canal + "\",\"message\":\"" + Texto + "\",\"data\":\"\",\"custom_type\":\"\","
                       + "\"mention_type\":\"users\",\"mentioned_user_ids\":[],\"req_id\":\"pdv92-1-1760543650000\"}\n";
        checar(q == esperado, "SK-1 o quadro MESG tem o formato exato (channel_url, message, data, custom_type, mention_type, mentioned_user_ids, req_id) e termina em \\n");
        using var d = JsonDocument.Parse(q[4..].TrimEnd('\n'));
        var nomes = d.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        checar(nomes.SequenceEqual(new[] { "channel_url", "message", "data", "custom_type", "mention_type", "mentioned_user_ids", "req_id" })
               && d.RootElement.EnumerateObject().All(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Array)
               && !q.Contains("undefined", StringComparison.Ordinal) && !q.Contains("null", StringComparison.Ordinal),
            "SK-2 o quadro não tem campo indefinido nem nulo, só os 7 campos, nessa ordem");
        var dificil = "Ele disse \"oi\"\ne foi\\embora";
        var lido = QuadroSendbird.Ler(EnvioPeloSocket.QuadroMesg(Canal, dificil, "pdv1-1-1"));
        checar(lido is not null && lido.Canal == Canal && lido.Texto == dificil && lido.ReqId == "pdv1-1-1" && lido.Comando == "MESG",
            "SK-3 aspas, quebra de linha e barra viajam escapadas: o leitor do caixa lê o mesmo canal, texto e req_id");
    }

    private static void OReqId(Action<bool, string> checar)
    {
        var a = EnvioPeloSocket.ReqId(92, 1, 1760543650000);
        var b = EnvioPeloSocket.ReqId(92, 2, 1760543650000);
        var c = EnvioPeloSocket.ReqId(92, 1, 1760543650001);
        checar(a == "pdv92-1-1760543650000" && a != b && a != c && b != c,
            "SK-4 o req_id é pdv + saída + tentativa + ms, e muda a cada tentativa");
        checar(EnvioPeloSocket.EhNosso(a) && !EnvioPeloSocket.EhNosso("1760543650000") && !EnvioPeloSocket.EhNosso(null) && !EnvioPeloSocket.EhNosso(""),
            "SK-5 o quadro do caixa é reconhecido pelo req_id (o do Gestor não)");
    }

    // ── a escolha do socket ──────────────────────────────────────────────────
    private static void AEscolha(Action<bool, string> checar)
    {
        var dois = new string?[] { UrlEntregador, UrlLoja };
        checar(EnvioPeloSocket.Escolher(dois, UrlLojaCdp, null) == (1, "canal"),
            "SK-6 (a) o socket do requestId que recebeu o último quadro daquele canal (a URL do CDP sem a barra casa com ws.url)");
        checar(EnvioPeloSocket.Escolher(dois, UrlLoja, "entregador-77") == (1, "canal"),
            "SK-7 o canal vem antes do usuário");
        checar(EnvioPeloSocket.Escolher(dois, null, Loja) == (1, "usuario")
               && EnvioPeloSocket.Escolher(dois, "wss://ws-6913ab.sendbird.com/?user_id=outro", Loja) == (1, "usuario"),
            "SK-8 (b) sem o canal, o socket do usuário da loja já aprendido");
        checar(EnvioPeloSocket.Escolher(new string?[] { UrlLoja }, null, null) == (0, "unico")
               && EnvioPeloSocket.Escolher(new string?[] { UrlOutro, UrlLoja }, null, null) == (1, "unico"),
            "SK-9 (c) sem pista, o único socket aberto do Sendbird (o que não é do Sendbird não conta)");
        checar(EnvioPeloSocket.Escolher(new string?[] { UrlLoja, UrlLoja }, null, null) == (0, "unico"),
            "SK-10 dois sockets com a mesma URL são do mesmo usuário: um só, o primeiro");
        checar(EnvioPeloSocket.Escolher(dois, null, null) == (-1, "ambiguo")
               && EnvioPeloSocket.Escolher(dois, "wss://ws-6913ab.sendbird.com/?user_id=outro", null) == (-1, "ambiguo"),
            "SK-11 (d) dois usuários sem pista: ambíguo, não escolhe nenhum");
        checar(EnvioPeloSocket.Escolher(new string?[] { UrlEntregador }, null, Loja) == (-1, "ambiguo"),
            "SK-12 o único socket aberto é de OUTRO usuário (o da loja é conhecido): não manda como outro usuário");
        checar(EnvioPeloSocket.Escolher(Array.Empty<string?>(), UrlLoja, Loja) == (-1, "nenhum")
               && EnvioPeloSocket.Escolher(new string?[] { UrlOutro, null, "" }, null, null) == (-1, "nenhum"),
            "SK-13 sem socket aberto do Sendbird: nenhum");
    }

    // ── o eco ────────────────────────────────────────────────────────────────
    private static string Eco(string reqId, long msgId = 9911223344)
        => "MESG{\"msg_id\":" + msgId + ",\"req_id\":\"" + reqId + "\",\"message\":" + JsonSerializer.Serialize(Texto)
           + ",\"channel_url\":\"" + Canal + "\",\"created_at\":1760543650100,\"user\":{\"guest_id\":\"" + Loja + "\",\"name\":\"Loja\"}}";

    private static string Eror(string reqId, int codigo = 900050, string msg = "Channel is frozen.")
        => "EROR{\"code\":" + codigo + ",\"message\":" + JsonSerializer.Serialize(msg) + ",\"req_id\":\"" + reqId + "\"}";

    private static void OEco(Action<bool, string> checar)
    {
        var e = EnvioPeloSocket.LerEco(Eco("pdv92-1-5"));
        checar(e is { } x && x.ReqId == "pdv92-1-5" && x.Eco.Deu && x.Eco.MsgId == "9911223344",
            "SK-14 o eco MESG com o req_id: deu, com o msg_id");
        var r = EnvioPeloSocket.LerEco(Eror("pdv92-1-5"));
        checar(r is { } y && y.ReqId == "pdv92-1-5" && !y.Eco.Deu && y.Eco.Codigo == 900050 && y.Eco.MsgId is null,
            "SK-15 o EROR com o req_id: não deu, com o código do Sendbird");
        checar(EnvioPeloSocket.LerEco(TestesQuadroSendbird.Mesg("oi")) is null && EnvioPeloSocket.LerEco("READ{\"req_id\":\"pdv1-1-1\"}") is null
               && EnvioPeloSocket.LerEco("EROR{\"code\":1}") is null && EnvioPeloSocket.LerEco("lixo") is null && EnvioPeloSocket.LerEco(null) is null,
            "SK-16 quadro sem req_id, outro comando ou lixo não é eco");

        var ecos = new EcosDoSocket();
        var t = ecos.Aguardar("pdv92-1-5");
        var outro = ecos.Ver(Eco("pdv77-1-5"));
        var gestor = ecos.Ver(TestesQuadroSendbird.Mesg(Texto, Loja, "MERCHANT", 1, 1, Canal));
        checar(!outro && !gestor && !t.IsCompleted, "SK-17 o eco de outro req_id (ou o MESG do próprio Gestor) não confirma");
        var meu = ecos.Ver(Eco("pdv92-1-5", 991));
        checar(meu && t.IsCompleted && t.Result.Deu && t.Result.MsgId == "991" && ecos.Esperando == 0,
            "SK-18 o eco com o mesmo req_id confirma e sai da espera");
    }

    // ── o envio (portas falsas do CDP) ───────────────────────────────────────
    private sealed class Portas : IPortasDoSocket
    {
        public List<string?> Urls = new();
        public readonly List<(int Indice, string Quadro)> Enviados = new();
        public int Listagens, Liberacoes;
        public Exception? FalhaAoListar, FalhaAoEnviar;
        public bool Aberto = true;
        /// <summary>O que o servidor devolve depois do envio (o quadro recebido), pelo req_id do quadro mandado.</summary>
        public Func<string, string?>? Responder;
        public EcosDoSocket? Ecos;

        public Task<IReadOnlyList<string?>> AbertosAsync()
        {
            Listagens++;
            if (FalhaAoListar is { } f) throw f;
            return Task.FromResult<IReadOnlyList<string?>>(Urls);
        }

        public Task<bool> EnviarAsync(int indice, string quadro)
        {
            if (!Aberto) return Task.FromResult(false);
            Enviados.Add((indice, quadro));
            var req = QuadroSendbird.Ler(quadro)?.ReqId ?? "";
            if (Responder?.Invoke(req) is { } volta) Ecos?.Ver(volta);
            if (FalhaAoEnviar is { } f) throw f;
            return Task.FromResult(true);
        }

        public Task LiberarAsync() { Liberacoes++; return Task.CompletedTask; }
    }

    private static PedidoPeloSocket Pedir(string reqId = "pdv92-1-1760543650000", string? urlDoCanal = UrlLojaCdp, string? usuario = null,
        string canal = Canal, string pedido = Pedido)
        => new(canal, pedido, Texto, reqId, urlDoCanal, usuario);

    private static Task<ResultadoDoSocket> Rodar(Portas p, EcosDoSocket ecos, PedidoPeloSocket ped, Action? antes = null)
        => EnvioPeloSocket.EnviarAsync(p, ecos, ped, TimeSpan.FromSeconds(12), _ => Task.CompletedTask, antes);

    private static string? Etapa(JsonObject? d) => d?["etapa"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Limpo(string s)
        => !s.Contains("sendbird.com", StringComparison.OrdinalIgnoreCase) && !s.Contains("access_token", StringComparison.Ordinal)
           && !s.Contains("eyJ", StringComparison.Ordinal) && !s.Contains(Chave[..12], StringComparison.Ordinal)
           && !s.Contains(Loja, StringComparison.Ordinal) && !s.Contains("Ana", StringComparison.Ordinal)
           && !s.Contains(Canal, StringComparison.Ordinal) && !s.Contains(Pedido, StringComparison.Ordinal)
           && !s.Contains("entregador-77", StringComparison.Ordinal);

    private static async Task OEnvio(Action<bool, string> checar)
    {
        // eco MESG: deu, com o msg_id
        var ecos = new EcosDoSocket();
        var p = new Portas { Urls = { UrlEntregador, UrlLoja }, Ecos = ecos, Responder = req => Eco(req, 991) };
        var antes = 0;
        var r = await EnvioPeloSocket.EnviarAsync(p, ecos, Pedir(), TimeSpan.FromSeconds(12), t => Task.Delay(t), () => antes++);
        checar(r.Ok && r.MsgId == "991" && r.Erro is null && p.Enviados.Count == 1 && p.Enviados[0].Indice == 1
               && p.Enviados[0].Quadro == EnvioPeloSocket.QuadroMesg(Canal, Texto, "pdv92-1-1760543650000") && antes == 1,
            "SK-19 eco MESG com o req_id: deu, com o msg_id; UM quadro, no socket escolhido pelo canal");
        checar(r.Escolha == "canal" && r.Abertos == 2 && (string?)r.Detalhe["via"] == "socket" && (string?)r.Detalhe["escolha"] == "canal"
               && (int?)r.Detalhe["abertos"] == 2 && p.Liberacoes == 1 && ecos.Esperando == 0,
            "SK-20 o resultado diz a via (socket), a escolha e quantos sockets abertos; o grupo foi liberado");

        // EROR: falha certa, com o código e a mensagem limpa
        var ecos2 = new EcosDoSocket();
        var sujo = "Bad session " + UrlLoja + " msg: " + Texto + " key " + Chave;
        var p2 = new Portas { Urls = { UrlLoja }, Ecos = ecos2, Responder = req => Eror(req, 400302, sujo) };
        var r2 = await Rodar(p2, ecos2, Pedir(urlDoCanal: null));
        checar(!r2.Ok && r2.Erro == "erro_envio" && Etapa(r2.Detalhe) == "falhou_socket" && (int?)r2.Detalhe["codigo"] == 400302
               && (string?)r2.Detalhe["via"] == "socket" && p2.Enviados.Count == 1 && p2.Liberacoes == 1,
            "SK-21 EROR com o req_id: falha certa (falhou_socket) com o código do Sendbird, um quadro só");
        checar(Limpo(r2.Detalhe.ToJsonString()) && Limpo(EnvioPeloSocket.Linha(r2)),
            $"SK-22 a mensagem do EROR não leva URL, chave, token, usuário nem texto do cliente ({r2.Detalhe.ToJsonString()})");

        // sem eco no prazo: incerta, e nunca de novo
        var ecos3 = new EcosDoSocket();
        var p3 = new Portas { Urls = { UrlLoja }, Ecos = ecos3 };
        var r3 = await Rodar(p3, ecos3, Pedir());
        checar(!r3.Ok && r3.Erro == "sem_eco" && Etapa(r3.Detalhe) == "sem_eco" && p3.Enviados.Count == 1 && ecos3.Esperando == 0 && p3.Liberacoes == 1,
            "SK-23 sem eco no prazo: incerta (sem_eco), UM quadro só, a espera sai e o grupo é liberado");
        var tarde = ecos3.Ver(Eco("pdv92-1-1760543650000"));
        checar(!tarde, "SK-24 o eco que chega depois do prazo não ressuscita a espera");

        // eco de OUTRO req_id não confirma
        var ecos4 = new EcosDoSocket();
        var p4 = new Portas { Urls = { UrlLoja }, Ecos = ecos4, Responder = _ => Eco("pdv77-9-1") };
        var r4 = await Rodar(p4, ecos4, Pedir());
        checar(!r4.Ok && r4.Erro == "sem_eco", "SK-25 o eco de outro req_id não vale como confirmação");

        // ambíguo: não envia
        var ecos5 = new EcosDoSocket();
        var p5 = new Portas { Urls = { UrlEntregador, UrlLoja }, Ecos = ecos5, Responder = req => Eco(req) };
        var r5 = await Rodar(p5, ecos5, Pedir(urlDoCanal: null));
        checar(!r5.Ok && r5.Erro == "sdk_ausente" && Etapa(r5.Detalhe) == "socket_ambiguo" && (string?)r5.Detalhe["escolha"] == "ambiguo"
               && p5.Enviados.Count == 0 && p5.Liberacoes == 1,
            "SK-26 socket ambíguo: falha socket_ambiguo e NADA sai");

        // nenhum socket do Sendbird aberto
        var p6 = new Portas { Urls = { UrlOutro } };
        var r6 = await Rodar(p6, new EcosDoSocket(), Pedir());
        checar(!r6.Ok && r6.Erro == "sdk_ausente" && Etapa(r6.Detalhe) == "sem_socket" && p6.Enviados.Count == 0 && r6.Abertos == 0,
            "SK-27 sem socket aberto do Sendbird: sem_socket, nada sai");

        // o socket fechou entre achar e mandar: nada saiu (certo)
        var ecos7 = new EcosDoSocket();
        var p7 = new Portas { Urls = { UrlLoja }, Aberto = false, Ecos = ecos7 };
        var r7 = await Rodar(p7, ecos7, Pedir());
        checar(!r7.Ok && r7.Erro == "sdk_ausente" && Etapa(r7.Detalhe) == "sem_socket" && ecos7.Esperando == 0 && p7.Liberacoes == 1,
            "SK-28 o socket fechou antes do send: sem_socket (certo que nada saiu), sem esperar eco");

        // canal que não é o do pedido: nem lista
        var p8 = new Portas { Urls = { UrlLoja } };
        var r8 = await Rodar(p8, new EcosDoSocket(), Pedir(pedido: "00000000-0000-4000-8000-000000000000"));
        var r8b = await Rodar(p8, new EcosDoSocket(), Pedir(canal: "sendbird_group_channel_123"));
        checar(!r8.Ok && r8.Erro == "canal_errado" && !r8b.Ok && r8b.Erro == "canal_errado" && p8.Listagens == 0 && p8.Enviados.Count == 0,
            "SK-29 canal fora do padrão do iFood, ou de outro pedido: canal_errado, sem nem procurar socket");
    }

    // ── o grupo do CDP é liberado mesmo com exceção ──────────────────────────
    private static async Task OGrupoLiberado(Action<bool, string> checar)
    {
        var p = new Portas { Urls = { UrlLoja }, FalhaAoListar = new InvalidOperationException("cdp caiu " + UrlLoja) };
        var r = await Rodar(p, new EcosDoSocket(), Pedir());
        checar(!r.Ok && r.Erro == "sdk_ausente" && Etapa(r.Detalhe) == "sem_socket" && p.Liberacoes == 1 && p.Enviados.Count == 0
               && Limpo(r.Detalhe.ToJsonString()) && Limpo(EnvioPeloSocket.Linha(r)),
            "SK-30 exceção ao procurar os sockets: nada saiu (sem_socket), o grupo é liberado e a exceção não vaza URL");

        var ecos = new EcosDoSocket();
        var p2 = new Portas { Urls = { UrlLoja }, Ecos = ecos, FalhaAoEnviar = new InvalidOperationException("callFunctionOn") };
        var r2 = await Rodar(p2, ecos, Pedir());
        checar(!r2.Ok && r2.Erro == "sem_eco" && p2.Liberacoes == 1 && p2.Enviados.Count == 1 && ecos.Esperando == 0,
            "SK-31 exceção no send (pode ter saído): espera o eco; sem eco é incerta, nunca de novo; o grupo é liberado");

        var ecos3 = new EcosDoSocket();
        var p3 = new Portas { Urls = { UrlLoja }, Ecos = ecos3, FalhaAoEnviar = new InvalidOperationException("x"), Responder = req => Eco(req, 5) };
        var r3 = await Rodar(p3, ecos3, Pedir());
        checar(r3.Ok && r3.MsgId == "5" && p3.Liberacoes == 1, "SK-32 exceção no send mas o eco chegou: deu (o eco é a prova)");
    }

    // ── a camada C só roda quando o SDK não foi achado ───────────────────────
    private static async Task SdkAchadoNaoRoda(Action<bool, string> checar)
    {
        var chamadas = 0;
        Task<ResultadoDoSocket> CamadaC()
        {
            chamadas++;
            return Task.FromResult(new ResultadoDoSocket(true, null, "1", new JsonObject { ["via"] = "socket" }, "unico", 1));
        }
        var comSdk = new[]
        {
            await EnvioPeloSocket.SeCouberAsync(null, null, CamadaC),
            await EnvioPeloSocket.SeCouberAsync("canal_errado", new JsonObject { ["etapa"] = "canal_errado", ["instancias"] = 2 }, CamadaC),
            await EnvioPeloSocket.SeCouberAsync("erro_envio", new JsonObject { ["etapa"] = "falhou_sdk", ["instancias"] = 1 }, CamadaC),
            await EnvioPeloSocket.SeCouberAsync("congelada", new JsonObject { ["etapa"] = "congelada" }, CamadaC),
            await EnvioPeloSocket.SeCouberAsync("token", new JsonObject { ["etapa"] = "token" }, CamadaC),
            await EnvioPeloSocket.SeCouberAsync("sem_resposta", null, CamadaC),
            await EnvioPeloSocket.SeCouberAsync("sdk_ausente", null, CamadaC),
        };
        checar(comSdk.All(x => x is null) && chamadas == 0,
            "SK-33 SDK achado (ou recusa do SDK, token, sem resposta, script ausente): a camada C nem roda");
        var semSdk = await EnvioPeloSocket.SeCouberAsync("sdk_ausente", new JsonObject { ["etapa"] = "sdk_ausente", ["instancias"] = 0 }, CamadaC);
        checar(semSdk is { Ok: true } && chamadas == 1, "SK-34 só a lista vazia do script (sdk_ausente na etapa sdk_ausente) chama a camada C, uma vez");
    }

    // ── a confirmação pelo quadro com o envio pelo socket ────────────────────
    private static void AConfirmacao(Action<bool, string> checar)
    {
        var quadro = QuadroSendbird.Ler(EnvioPeloSocket.QuadroMesg(Canal, Texto, "pdv1-1-1"));
        var c = new ConfirmacaoDeEnvio();
        c.Aguardar(1, Canal, Texto, T0);
        c.Ver(quadro, enviado: true);
        c.PeloSocket(1, T0.AddSeconds(3));
        checar(c.Vencidas(T0.AddSeconds(15)).Count == 0, "SK-35 o envio pelo socket estende a espera da confirmação (o eco tem 12 s depois do send)");
        var v = c.Vencidas(T0.AddSeconds(18));
        checar(v.Count == 1 && v[0] == (1L, false),
            "SK-36 pelo socket, o quadro visto SAINDO sem eco vence como incerta (nunca enviada sem prova)");
        var hoje = new ConfirmacaoDeEnvio();
        hoje.Aguardar(2, Canal, Texto, T0);
        hoje.Ver(quadro, enviado: true);
        var v2 = hoje.Vencidas(T0.AddSeconds(15));
        checar(v2.Count == 1 && v2[0] == (2L, true), "SK-37 pelo SDK continua como hoje (visto saindo vence como enviada)");
        var eco = new ConfirmacaoDeEnvio();
        eco.Aguardar(3, Canal, Texto, T0);
        eco.PeloSocket(3, T0);
        var conf = eco.Ver(QuadroSendbird.Ler(Eco("pdv1-1-1", 4455)), enviado: false);
        checar(conf is { } cf && cf.SaidaId == 3 && cf.MsgId == "4455" && !eco.Pendente(3),
            "SK-38 o eco recebido confirma a saída pelo socket com o msg_id (vira sendbird_msg_id)");
        var y = new ConfirmacaoDeEnvio();
        y.Aguardar(4, Canal, Texto, T0);
        y.PeloSocket(4, T0);
        var primeiro = y.Cancelar(4);
        var segundo = y.Cancelar(4);
        checar(primeiro is not null && segundo is null && y.Vencidas(T0.AddMinutes(1)).Count == 0,
            "SK-38b o eco pelo req_id tira a saída da espera uma vez só (quem tirar primeiro relata, nunca os dois)");
        var x = new ConfirmacaoDeEnvio();
        x.PeloSocket(99, T0);
        checar(x.Vencidas(T0.AddMinutes(5)).Count == 0, "SK-39 marcar pelo socket uma saída que não espera não cria espera");
    }

    // ── o laço de envio com a camada C ───────────────────────────────────────
    private sealed class Cena
    {
        public DateTime Agora = T0.AddSeconds(1);
        public readonly ConfirmacaoDeEnvio Confirmacoes = new();
        public readonly PortaoDeEnvio Portao = new(new MemoriaDeEco(), new LimiteDeEnvio());
        public readonly List<string> Enviados = new();
        public readonly List<(string Resultado, string? Erro, JsonObject? Detalhe)> Relatos = new();
        public int Recargas;
        public Func<int, ResultadoDoScript> Script = _ => new ResultadoDoScript(true, null, "991");
        /// <summary>O quadro do caixa passa pelo CDP saindo da página (o socket mandou).</summary>
        public bool QuadroSai = true;
        public SaidaConversa Saida = new(92, "pergunta_sabor", 1, Texto, "sdk", "reservada", Canal, Pedido, "5403");

        public Task Rodar()
        {
            var sinal = ConversaRaspadinha.LerSinal("{\"ok\":true,\"modo\":\"automatico\",\"palavra_modo\":\"desligado\",\"envio\":\"sdk\","
                + "\"comanda_onde\":\"kds\",\"merchant_ids\":[\"" + Savassi + "\"],\"intervalo_s\":60,\"pausado\":null,"
                + "\"avisos\":[],\"saidas\":[],\"comandas\":[]}");
            var ponte = new PonteDoChat
            {
                EnviarPeloSdk = p =>
                {
                    Enviados.Add(p.Texto);
                    Agora += TimeSpan.FromSeconds(2);
                    var r = Script(Enviados.Count);
                    if (QuadroSai && r.Detalhe?["via"] is JsonValue) Confirmacoes.Ver(QuadroSendbird.Ler(EnvioPeloSocket.QuadroMesg(p.Canal, p.Texto, "pdv92-" + Enviados.Count + "-1")), enviado: true);
                    return Task.FromResult(r);
                },
                AbrirEColar = (_, _, _) => Task.FromResult(false),
                DiagSdk = (_, _) => Task.FromResult<JsonObject?>(null),
                Gestor = () => "logado",
                AquecerConversa = (_, _) => Task.FromResult(true),
                RecarregarParaEnvio = (_, _) => { Recargas++; return Task.FromResult(PlanoBDoEnvio.Recarregou); },
            };
            var laco = new LacoDeEnvio
            {
                Portao = Portao,
                Confirmacoes = Confirmacoes,
                Sinal = () => (sinal, T0),
                Ponte = () => ponte,
                Relatar = (_, resultado, erro, _, detalhe) => { Relatos.Add((resultado, erro, detalhe)); return Task.CompletedTask; },
                Esperar = t => { Agora += t; return Task.CompletedTask; },
                Agora = () => Agora,
                Versao = "1.0.39",
            };
            return ServicoConversaChat.EnviarComTentativasAsync(Saida, T0, laco);
        }
    }

    private static ResultadoDoScript PeloSocket(string erro, string etapa, int? codigo = null)
    {
        var d = new JsonObject { ["etapa"] = etapa, ["via"] = "socket", ["escolha"] = "canal", ["abertos"] = 6, ["ms"] = 300 };
        if (codigo is int c) d["codigo"] = c;
        return new ResultadoDoScript(false, erro, null, d);
    }

    private static async Task OLaco(Action<bool, string> checar)
    {
        var c1 = new Cena { Script = _ => PeloSocket("sem_eco", "sem_eco") };
        await c1.Rodar();
        checar(c1.Enviados.Count == 1 && c1.Relatos.Count == 1 && c1.Relatos[0].Resultado == "incerta" && c1.Relatos[0].Erro == "sem_confirmacao"
               && Etapa(c1.Relatos[0].Detalhe) == "sem_eco" && !c1.Confirmacoes.Pendente(92) && c1.Recargas == 0,
            $"SK-40 sem eco: o laço relata incerta UMA vez e NUNCA reenvia, mesmo com o quadro visto saindo ({string.Join(",", c1.Relatos.Select(r => r.Resultado))})");

        var c2 = new Cena { Script = _ => PeloSocket("erro_envio", "falhou_socket", 900050) };
        await c2.Rodar();
        checar(c2.Relatos.All(r => r.Resultado != "incerta") && c2.Relatos.Count == 1 && c2.Relatos[0].Resultado == "falhou"
               && c2.Relatos[0].Erro == "erro_envio" && Etapa(c2.Relatos[0].Detalhe) == "falhou_socket" && c2.Recargas == 0
               && !c2.Confirmacoes.Pendente(92),
            $"SK-41 EROR do servidor: falha certa (nunca incerta, mesmo com o quadro visto saindo) e sem recarga do plano B ({string.Join(",", c2.Relatos.Select(r => r.Resultado))})");

        var c3 = new Cena { Script = _ => new ResultadoDoScript(true, null, "991", new JsonObject { ["via"] = "socket" }) };
        await c3.Rodar();
        checar(c3.Enviados.Count == 1 && c3.Relatos.Count == 0, "SK-42 deu pelo socket: o laço para, quem relata é a confirmação pelo eco");

        var c4 = new Cena { Script = _ => PeloSocket("sdk_ausente", "socket_ambiguo"), QuadroSai = false };
        await c4.Rodar();
        checar(c4.Relatos.Count == 1 && c4.Relatos[0].Resultado == "falhou" && c4.Relatos[0].Erro == "sdk_ausente"
               && Etapa(c4.Relatos[0].Detalhe) == "socket_ambiguo" && c4.Recargas == 0,
            "SK-43 socket ambíguo em todas as tentativas: falhou (sdk_ausente), com a etapa no detalhe");
    }

    // ── o mascaramento por FORMATO ───────────────────────────────────────────
    private static void OMascaramento(Action<bool, string> checar)
    {
        var sujo = "falhou em " + UrlLoja + " token " + Jwt + " chave " + Chave + " sessao=" + Chave.ToLowerInvariant() + "x9";
        var m = EnvioPeloSocket.Mascarar(sujo);
        checar(!m.Contains("eyJ", StringComparison.Ordinal) && !m.Contains(Chave[..12], StringComparison.Ordinal)
               && !m.Contains(Chave.ToLowerInvariant()[..12], StringComparison.Ordinal) && !m.Contains("access_token=ey", StringComparison.Ordinal)
               && m.Contains("falhou em", StringComparison.Ordinal),
            $"SK-44 JWT, chave longa e URL com chave saem pelo FORMATO, não só pelo nome do campo ({m})");
        var limpa = ConversaRaspadinha.MsgDoErroLimpa("invalid session key " + Chave + " jwt " + Jwt);
        checar(limpa is not null && !limpa.Contains(Chave[..12], StringComparison.Ordinal) && !limpa.Contains("eyJ", StringComparison.Ordinal)
               && limpa.StartsWith("invalid session key", StringComparison.Ordinal),
            $"SK-45 a mensagem do erro que vai ao ERP também perde segredo opaco e JWT pelo formato ({limpa})");
        checar(ConversaRaspadinha.MsgDoErroLimpa("Channel is frozen.") == "Channel is frozen."
               && ConversaRaspadinha.MsgDoErroLimpa("Not a member of the channel.") == "Not a member of the channel.",
            "SK-46 mensagem comum do Sendbird passa como está");
    }

    // ── o diagnóstico novo ───────────────────────────────────────────────────
    private static void ODiagnostico(Action<bool, string> checar)
    {
        var sdk = new JsonObject
        {
            ["achou"] = false, ["via_socket"] = true, ["sockets_sb_abertos"] = 6, ["socket_escolha"] = "canal",
            ["mesg_campos_gestor"] = new JsonArray("channel_url", "message", "req_id", "data", "custom_type"),
        };
        var e = JsonNode.Parse(ConversaRaspadinha.EstadoDoSinal("logado", true, T0, 0, null, sdk, null))!["sdk"]!.AsObject();
        checar((bool?)e["via_socket"] == true && (int?)e["sockets_sb_abertos"] == 6 && (string?)e["socket_escolha"] == "canal"
               && e["mesg_campos_gestor"] is JsonArray a && a.Count == 5 && (string?)a[0] == "channel_url",
            "SK-47 o sinal leva sdk.via_socket, sdk.sockets_sb_abertos, sdk.socket_escolha e os nomes dos campos do MESG do Gestor");
        var sujo = new JsonObject
        {
            ["via_socket"] = "sim", ["sockets_sb_abertos"] = 9_999_999, ["socket_escolha"] = "canal; DROP",
            ["mesg_campos_gestor"] = new JsonArray("message", Jwt, UrlLoja, "Ana Paula", 3, "req_id"),
            ["socket_url"] = UrlLoja,
        };
        var e2 = JsonNode.Parse(ConversaRaspadinha.EstadoDoSinal("logado", true, T0, 0, null, sujo, null))!["sdk"]!.AsObject();
        checar(!e2.ContainsKey("via_socket") && (int?)e2["sockets_sb_abertos"] == 100_000 && !e2.ContainsKey("socket_escolha")
               && !e2.ContainsKey("socket_url") && e2["mesg_campos_gestor"] is JsonArray a2 && a2.Count == 2 && Limpo(e2.ToJsonString()),
            $"SK-48 a lista branca: escolha fora do enum cai, nome de campo que não é nome cai, URL não passa ({e2.ToJsonString()})");
        var gestor = "MESG{\"channel_url\":\"" + Canal + "\",\"message\":\"Oi Ana, seu pedido saiu\",\"data\":\"{}\",\"custom_type\":\"\","
                     + "\"mention_type\":\"users\",\"mentioned_user_ids\":[],\"req_id\":\"1760543650000\",\"" + Jwt + "\":1}\n";
        var campos = EnvioPeloSocket.CamposDoMesg(gestor);
        checar(campos is { } cs && cs.SequenceEqual(new[] { "channel_url", "custom_type", "data", "mention_type", "mentioned_user_ids", "message", "req_id" })
               && EnvioPeloSocket.CamposDoMesg("READ{\"req_id\":\"1\"}") is null && EnvioPeloSocket.CamposDoMesg("lixo") is null,
            "SK-49 do MESG que o Gestor mandou ficam SÓ os nomes dos campos (nunca valores; nome com cara de segredo cai)");
        checar(EnvioPeloSocket.CanalDoQuadro("READ{\"channel_url\":\"" + Canal + "\",\"req_id\":\"1\"}") == Canal
               && EnvioPeloSocket.CanalDoQuadro(Eco("pdv1-1-1")) == Canal && EnvioPeloSocket.CanalDoQuadro("PONG{}") is null
               && EnvioPeloSocket.CanalDoQuadro(null) is null,
            "SK-50 o canal de qualquer quadro recebido (para escolher o socket pelo canal)");

        // o detalhe da saída pela lista branca do ERP
        using var d = JsonDocument.Parse("{\"etapa\":\"falhou_socket\",\"via\":\"socket\",\"escolha\":\"usuario\",\"abertos\":6,\"codigo\":900050,\"url\":\"" + UrlLoja + "\"}");
        var det = ConversaRaspadinha.DetalheDoErro(d.RootElement)!;
        checar((string?)det["etapa"] == "falhou_socket" && (string?)det["via"] == "socket" && (string?)det["escolha"] == "usuario"
               && (int?)det["abertos"] == 6 && !det.ContainsKey("url"),
            "SK-51 o detalhe da saída aceita a etapa nova, a via e a escolha do socket; URL não passa");
        foreach (var etapa in new[] { "sem_socket", "socket_ambiguo", "falhou_socket", "sem_eco" })
            checar(ConversaRaspadinha.EtapasDoErro.Contains(etapa), $"SK-52 a etapa {etapa} está no contrato do detalhe");
        using var d2 = JsonDocument.Parse("{\"via\":\"rede\",\"escolha\":\"chute\"}");
        checar(ConversaRaspadinha.DetalheDoErro(d2.RootElement) is null, "SK-53 via e escolha fora do enum caem");
        var daPagina = new JsonObject { ["etapa"] = "falhou_socket", ["via"] = "socket", ["escolha"] = "canal", ["abertos"] = 2, ["codigo"] = 900100 };
        var semMarca = EnvioPeloSocket.SemMarcaDaPagina(daPagina)!;
        checar((string?)semMarca["etapa"] == "geral" && !semMarca.ContainsKey("via") && !semMarca.ContainsKey("escolha") && !semMarca.ContainsKey("abertos")
               && (int?)semMarca["codigo"] == 900100 && (string?)daPagina["etapa"] == "falhou_socket"
               && (string?)EnvioPeloSocket.SemMarcaDaPagina(new JsonObject { ["etapa"] = "sdk_ausente" })!["etapa"] == "sdk_ausente"
               && EnvioPeloSocket.SemMarcaDaPagina(null) is null,
            "SK-53b a página não se passa pela camada C: via, escolha e etapa do socket saem do detalhe do script");
        checar(ConversaRaspadinha.LinhaDoDetalhe(det).Contains(" via=socket", StringComparison.Ordinal)
               && ConversaRaspadinha.LinhaDoDetalhe(det).Contains(" escolha=usuario", StringComparison.Ordinal),
            "SK-54 o rastro local diz a via e a escolha");
    }

    // ── pelo fonte: a tela liga a camada C só onde deve ─────────────────────
    private static void PeloFonte(Action<bool, string> checar)
    {
        var raiz = Raiz();
        string Ler(params string[] partes) { try { return raiz is null ? "" : File.ReadAllText(Path.Combine(new[] { raiz }.Concat(partes).ToArray())); } catch { return ""; } }
        var tela = Ler("Telas", "ChatIfood.xaml.cs");
        var servico = Ler("ServicoConversaChat.cs");
        var nucleo = Ler("Pdv.Nucleo", "EnvioPeloSocket.cs");
        checar(tela.Contains("Detalhe = EnvioPeloSocket.SemMarcaDaPagina(ConversaRaspadinha.DetalheSemSegredos(tcs.Task.Result.Detalhe, segredos)),", StringComparison.Ordinal),
            "SK-55b o resultado do script passa pelo SemMarcaDaPagina antes de decidir a camada C e o laço");
        checar(tela.Contains("EnvioPeloSocket.SeCouberAsync(", StringComparison.Ordinal)
               && tela.Contains("\"WebSocket.prototype\"", StringComparison.Ordinal)
               && tela.Contains("readyState === 1", StringComparison.Ordinal) && tela.Contains("'.sendbird.com'", StringComparison.Ordinal)
               && tela.Contains("\"pdvsb-sock-\"", StringComparison.Ordinal),
            "SK-55 a tela chama a camada C só pelo SeCouberAsync e acha os sockets pela heap (WebSocket.prototype, readyState 1, .sendbird.com, grupo próprio)");
        var portas = tela.IndexOf("class PortasDoSocketCdp", StringComparison.Ordinal);
        var liberar = portas < 0 ? -1 : tela.IndexOf("Runtime.releaseObjectGroup", portas, StringComparison.Ordinal);
        checar(portas > 0 && liberar > portas, "SK-56 as portas do CDP liberam o grupo de objetos (Runtime.releaseObjectGroup)");
        checar(tela.Contains("_ecosSocket.Ver(p)", StringComparison.Ordinal) && tela.Contains("ServicoConversaChat.PeloSocket(", StringComparison.Ordinal)
               && tela.Contains("EnvioPeloSocket.CamposDoMesg(", StringComparison.Ordinal) && tela.Contains("EnvioPeloSocket.CanalDoQuadro(", StringComparison.Ordinal)
               && tela.Contains("if (r.Ok) ServicoConversaChat.ConfirmadaPeloSocket(saidaId, r.MsgId);", StringComparison.Ordinal)
               && servico.Contains("if (Confirmacoes.Cancelar(saidaId) is not null) _ = RelatarAsync(saidaId, \"enviada\", null, msgId, null);", StringComparison.Ordinal),
            "SK-57 o quadro recebido alimenta o eco; o envio marca a confirmação pelo socket; o eco pelo req_id confirma (uma vez só); o MESG do Gestor só dá os nomes; o canal aprende o socket");
        checar(servico.Contains("\"sem_eco\"", StringComparison.Ordinal) && servico.Contains("\"falhou_socket\"", StringComparison.Ordinal),
            "SK-58 o laço conhece o sem_eco (incerta) e o falhou_socket (falha certa)");
        checar(Ler("Pdv.csproj").Contains("<Version>1.0.39</Version>", StringComparison.Ordinal), "SK-59 a versão do caixa é 1.0.39 (a camada C, pelo socket)");
        var novos = new[] { nucleo, Ler("Pdv.Testes", "TestesEnvioPeloSocket.cs") };
        checar(novos.All(t => t.Length > 0 && !t.Contains((char)0x2014) && !t.Contains((char)0x2013)),
            "SK-60 nada com travessão nos arquivos novos");
    }

    private static string? Raiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj"))) return dir.FullName;
        return null;
    }
}
