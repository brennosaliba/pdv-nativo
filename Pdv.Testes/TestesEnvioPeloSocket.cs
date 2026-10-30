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
    internal const string Loja = "31994412725";
    internal const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJ1c2VyIjoiMzE5OTQ0MTI3MjUifQ.c2VncmVkby1kby1zb2NrZXQ";
    internal const string Chave = "Zk9QbW1hY2hhdmVTZWNyZXRhRG9Tb2NrZXQxMjM0NTY";
    // a URL como o navegador a guarda em ws.url (com a barra) e como o CDP a viu no webSocketCreated (sem a barra)
    internal const string UrlLoja = "wss://ws-6913ab.sendbird.com/?p=JS&sv=4.19.9&ai=6913AB&user_id=" + Loja + "&access_token=" + Jwt;
    internal const string UrlLojaCdp = "wss://ws-6913ab.sendbird.com?p=JS&sv=4.19.9&ai=6913AB&user_id=" + Loja + "&access_token=" + Jwt;
    internal const string UrlEntregador = "wss://ws-6913ab.sendbird.com/?p=JS&sv=4.19.9&ai=6913AB&user_id=entregador-77&key=" + Chave;
    internal const string UrlOutro = "wss://chat.ifood.com.br/socket?token=" + Chave;
    // 1.0.40: o SDK JS abre cada conexão com a query DIFERENTE (Request-Sent-Timestamp, key da sessão, token renovado)
    internal const string UrlLoja2 = "wss://ws-6913ab.sendbird.com/?p=JS&sv=4.19.9&ai=6913AB&user_id=" + Loja + "&key=" + Chave + "&Request-Sent-Timestamp=1760543650999";
    internal const string UrlLoja2Cdp = "wss://ws-6913ab.sendbird.com?p=JS&sv=4.19.9&ai=6913AB&user_id=" + Loja + "&key=" + Chave + "&Request-Sent-Timestamp=1760543650999";
    internal const string UrlSemUsuario = "wss://ws-6913ab.sendbird.com/?p=JS&sv=4.19.9&ai=6913AB&key=" + Chave;
    private const string CanalEntregador = "sendbird_gc_" + Pedido + "_entregador-77";
    private static readonly DateTime T0 = new(2026, 10, 15, 12, 54, 10);

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        OQuadro(checar);
        OReqId(checar);
        AEscolha(checar);
        AEscolhaPelaAtividade(checar);
        OAprendizadoDaLoja(checar);
        ODiagnosticoDosSockets(checar);
        OLivroDasConexoes(checar);
        ODiagAoVivo(checar);
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

    // ── 1.0.40: a escolha pela ATIVIDADE (duas conexões do mesmo usuário, query diferente) ──
    private static DateTime?[] Ativ(params int?[] s) => s.Select(x => x is int v ? (DateTime?)T0.AddSeconds(v) : null).ToArray();

    private static void AEscolhaPelaAtividade(Action<bool, string> checar)
    {
        var duasDaLoja = new string?[] { UrlLoja, UrlLoja2 };
        checar(EnvioPeloSocket.Escolher(duasDaLoja, null, null) == (0, "unico")
               && EnvioPeloSocket.Escolher(duasDaLoja, null, null, Ativ(10, 50)) == (1, "unico")
               && EnvioPeloSocket.Escolher(duasDaLoja, null, null, Ativ(50, 10)) == (0, "unico"),
            "SK-61 duas URLs do MESMO usuário com a query diferente são um só usuário: único, o mais recentemente ativo (sem atividade, a ordem da heap)");
        checar(EnvioPeloSocket.Escolher(duasDaLoja, null, null, Ativ(null, 10)) == (1, "unico")
               && EnvioPeloSocket.Escolher(duasDaLoja, null, null, Ativ(10, 10)) == (0, "unico")
               && EnvioPeloSocket.Escolher(duasDaLoja, null, null, Ativ(null, null)) == (0, "unico"),
            "SK-62 atividade desconhecida perde para a conhecida; empate ou tudo desconhecido: a ordem da heap");
        var tres = new string?[] { UrlEntregador, UrlLoja, UrlLoja2 };
        checar(EnvioPeloSocket.Escolher(tres, null, Loja, Ativ(90, 10, 40)) == (2, "usuario")
               && EnvioPeloSocket.Escolher(tres, null, Loja, Ativ(90, 40, 10)) == (1, "usuario")
               && EnvioPeloSocket.Escolher(tres, null, Loja) == (1, "usuario"),
            "SK-63 (b) com o usuário da loja conhecido, o mais ativo ENTRE os sockets da loja (o do entregador mais ativo não conta)");
        checar(EnvioPeloSocket.Escolher(tres, UrlLojaCdp, Loja, Ativ(90, 10, 40)) == (1, "canal"),
            "SK-64 (a) o canal vem antes da atividade");
        checar(EnvioPeloSocket.Escolher(new string?[] { UrlSemUsuario }, null, Loja) == (-1, "ambiguo")
               && EnvioPeloSocket.Escolher(new string?[] { UrlSemUsuario, UrlLoja }, null, Loja) == (1, "usuario")
               && EnvioPeloSocket.Escolher(new string?[] { UrlSemUsuario }, UrlSemUsuario, Loja) == (0, "canal"),
            "SK-65 socket SEM user_id na URL com a loja conhecida: só entra pela pista (a); sozinho é ambíguo (nunca mandar como outro usuário)");
        checar(EnvioPeloSocket.Escolher(new string?[] { UrlSemUsuario }, null, null) == (0, "unico")
               && EnvioPeloSocket.Escolher(new string?[] { UrlSemUsuario, UrlSemUsuario }, null, null) == (-1, "ambiguo")
               && EnvioPeloSocket.Escolher(new string?[] { UrlSemUsuario, UrlLoja }, null, null) == (1, "unico"),
            "SK-66 sem user_id em nenhum, loja desconhecida e exatamente um aberto: único (como hoje); dois sem user_id: ambíguo; um com e um sem: o com user_id");
        checar(EnvioPeloSocket.Escolher(new string?[] { UrlEntregador, UrlLoja2 }, null, null, Ativ(10, 90)) == (-1, "ambiguo")
               && EnvioPeloSocket.Escolher(new string?[] { UrlLoja, UrlLoja2, UrlEntregador }, null, null, Ativ(10, 90, 95)) == (-1, "ambiguo"),
            "SK-67 usuários diferentes sem a loja conhecida: ambíguo, por mais ativo que um seja");
        checar(EnvioPeloSocket.Escolher(new string?[] { UrlLoja, UrlLoja2 }, null, "entregador-77", Ativ(10, 90)) == (-1, "ambiguo"),
            "SK-68 todos do mesmo usuário mas a loja é OUTRO: ambíguo (falha fechada)");

        // a atividade por URL: a URL da heap (ws.url, com a barra) casa com a do CDP (sem a barra) pela mesma chave
        var conexoes = new[] { (UrlLojaCdp, T0.AddSeconds(5)), (UrlLoja2Cdp, T0.AddSeconds(30)), (UrlEntregador, T0.AddSeconds(1)) };
        var a = EnvioPeloSocket.AtividadeDosAbertos(new string?[] { UrlLoja2, UrlLoja, UrlOutro, null }, conexoes);
        checar(a.Count == 4 && a[0] == T0.AddSeconds(30) && a[1] == T0.AddSeconds(5) && a[2] is null && a[3] is null,
            "SK-69 a atividade de cada socket aberto vem da conexão do CDP com a mesma URL (barra e maiúsculas não separam); sem conexão, desconhecida");
        var repetida = EnvioPeloSocket.AtividadeDosAbertos(new string?[] { UrlLoja }, new[] { (UrlLojaCdp, T0.AddSeconds(5)), (UrlLoja, T0.AddSeconds(9)) });
        checar(repetida[0] == T0.AddSeconds(9), "SK-70 duas conexões com a mesma URL: vale a mais recente");
    }

    // ── 1.0.40: a loja aprendida por qualquer quadro RECEBIDO de canal de cliente ──
    private static void OAprendizadoDaLoja(Action<bool, string> checar)
    {
        var read = "READ{\"channel_url\":\"" + Canal + "\",\"user\":{\"user_id\":\"" + Loja + "\"},\"ts\":1760543650100}";
        var brdm = "BRDM{\"channel_url\":\"" + Canal + "\",\"cat\":10000,\"ts\":1760543650100}";
        var dlvr = "DLVR{\"channel_url\":\"" + Canal + "\",\"updated\":{}}";
        checar(ServicoConversaChat.UsuarioDaLojaNoQuadro(read, Loja) == Loja && ServicoConversaChat.UsuarioDaLojaNoQuadro(brdm, Loja) == Loja
               && ServicoConversaChat.UsuarioDaLojaNoQuadro(dlvr, Loja) == Loja
               && ServicoConversaChat.UsuarioDaLojaNoQuadro(TestesQuadroSendbird.Mesg("oi", "cliente-1", "CUSTOMER", 1, 1, Canal), Loja) == Loja,
            "SK-71 READ, BRDM, DLVR ou MESG de canal de CLIENTE numa conexão com user_id ensinam o usuário da loja");
        var readEntregador = "READ{\"channel_url\":\"" + CanalEntregador + "\",\"ts\":1760543650100}";
        checar(ServicoConversaChat.UsuarioDaLojaNoQuadro(readEntregador, "entregador-77") is null
               && ServicoConversaChat.UsuarioDaLojaNoQuadro(TestesQuadroSendbird.Mesg("oi", "entregador-77", "DRIVER", 1, 1, CanalEntregador), "entregador-77") is null,
            "SK-72 o canal do entregador (sem sendbird_gc_cm_) NUNCA ensina a loja (regra do 1.0.26)");
        checar(ServicoConversaChat.UsuarioDaLojaNoQuadro(read, null) is null && ServicoConversaChat.UsuarioDaLojaNoQuadro(read, " ") is null
               && ServicoConversaChat.UsuarioDaLojaNoQuadro("PONG{}", Loja) is null && ServicoConversaChat.UsuarioDaLojaNoQuadro("LOGI{\"key\":\"x\"}", Loja) is null
               && ServicoConversaChat.UsuarioDaLojaNoQuadro(null, Loja) is null && ServicoConversaChat.UsuarioDaLojaNoQuadro("lixo", Loja) is null,
            "SK-73 conexão sem user_id, quadro sem canal (PONG, LOGI), lixo ou nulo: continua desconhecido");
    }

    // ── 1.0.40: o diagnóstico dos sockets no sinal, sem id, URL ou token ──
    private static void ODiagnosticoDosSockets(Action<bool, string> checar)
    {
        var abertos = new string?[] { UrlEntregador, UrlLoja, UrlLoja2, UrlSemUsuario, UrlOutro };
        var resumo = EnvioPeloSocket.Resumir(abertos, 2, "usuario", Ativ(1, 2, 40, null, null));
        checar(resumo.Abertos == 4 && resumo.Escolha == "usuario" && resumo.Usuarios == 2 && resumo.SemUsuario == 1 && resumo.AtivoEm == T0.AddSeconds(40),
            "SK-74 o resumo: 4 abertos do Sendbird, 2 usuários distintos, 1 sem user_id, a atividade do escolhido");
        var semEscolha = EnvioPeloSocket.Resumir(abertos, -1, "ambiguo", Ativ(1, 2, 40, null, null));
        checar(semEscolha.AtivoEm is null && semEscolha.Usuarios == 2, "SK-75 sem socket escolhido não há atividade do escolhido");
        var o = new JsonObject();
        EnvioPeloSocket.DiagDosSockets(o, resumo, T0.AddSeconds(77));
        checar((int?)o["sockets_sb_abertos"] == 4 && (string?)o["socket_escolha"] == "usuario" && (int?)o["sockets_usuarios"] == 2
               && (int?)o["sockets_sem_usuario"] == 1 && (int?)o["socket_ativo_s"] == 37 && Limpo(o.ToJsonString()),
            $"SK-76 o diag leva as contagens e os segundos desde o último quadro do escolhido, nunca id, URL ou token ({o.ToJsonString()})");
        var o2 = new JsonObject();
        EnvioPeloSocket.DiagDosSockets(o2, semEscolha, T0.AddSeconds(77));
        checar(o2.ContainsKey("socket_ativo_s") && o2["socket_ativo_s"] is null && (string?)o2["socket_escolha"] == "ambiguo",
            "SK-77 sem atividade do escolhido, socket_ativo_s é null");
        o["achou"] = false;
        var e = JsonNode.Parse(ConversaRaspadinha.EstadoDoSinal("logado", true, T0, 0, null, o, null))!["sdk"]!.AsObject();
        checar((int?)e["sockets_usuarios"] == 2 && (int?)e["sockets_sem_usuario"] == 1 && (int?)e["socket_ativo_s"] == 37 && Limpo(e.ToJsonString()),
            "SK-78 a lista branca do sinal deixa passar sockets_usuarios, sockets_sem_usuario e socket_ativo_s");
        o2["achou"] = false;
        var e2 = JsonNode.Parse(ConversaRaspadinha.EstadoDoSinal("logado", true, T0, 0, null, o2, null))!["sdk"]!.AsObject();
        checar(e2.ContainsKey("socket_ativo_s") && e2["socket_ativo_s"] is null, "SK-79 o null do socket_ativo_s passa como null");
        var sujo = new JsonObject { ["sockets_usuarios"] = "dois", ["sockets_sem_usuario"] = -4, ["socket_ativo_s"] = UrlLoja };
        var e3 = JsonNode.Parse(ConversaRaspadinha.EstadoDoSinal("logado", true, T0, 0, null, sujo, null))!["sdk"]!.AsObject();
        checar(!e3.ContainsKey("sockets_usuarios") && (int?)e3["sockets_sem_usuario"] == 0 && !e3.ContainsKey("socket_ativo_s"),
            "SK-80 texto no lugar de contagem cai; negativo vira 0");
    }

    // ── 1.0.40 (revisão): o livro das conexões vive do tamanho dos sockets vivos ──
    private static void OLivroDasConexoes(Action<bool, string> checar)
    {
        // o Gestor numa rede instável reconecta o dia todo: 205 sockets abertos e fechados com DUAS conexões vivas o tempo
        // todo. Antes, o socket 201 apagava os dicionários inteiros e as duas vivas ficavam sem URL, sem atividade e sem user_id.
        var livro = new ConexoesDoSocket();
        checar(livro.Abriu("A", UrlLojaCdp) && livro.Abriu("B", UrlLoja2Cdp), "SK-85 abrir um socket do Sendbird devolve true");
        checar(livro.Recebeu("A", T0.AddSeconds(1)) && livro.Recebeu("B", T0.AddSeconds(2)), "SK-86 um quadro numa conexão conhecida é atividade");
        for (var i = 0; i < 205; i++)
        {
            var r = "x" + i;
            livro.Abriu(r, UrlEntregador);
            livro.Recebeu(r, T0.AddSeconds(3));
            livro.Fechou(r);
        }
        checar(livro.Vivas == 2 && livro.Url("A") == UrlLojaCdp && livro.Url("B") == UrlLoja2Cdp && livro.Usuario("A") == Loja && livro.Usuario("B") == Loja
               && livro.AtividadePorUrl(UrlLoja) == T0.AddSeconds(1) && livro.AtividadePorUrl(UrlLoja2) == T0.AddSeconds(2) && livro.Ativas().Count == 2,
            "SK-87 205 sockets criados e fechados com 2 vivos: a URL, o user_id e a atividade dos 2 vivos continuam conhecidos (o fechamento limpa, não o teto)");
        livro.Fechou("A");
        checar(livro.Vivas == 1 && livro.Url("A") is null && livro.Usuario("A") is null && livro.AtividadePorUrl(UrlLoja) is null
               && !livro.Recebeu("A", T0.AddSeconds(9)) && livro.AtividadePorUrl(UrlLoja) is null && livro.Url("B") == UrlLoja2Cdp,
            "SK-88 o fechamento tira a conexão dos três (URL, usuário, atividade) e um quadro atrasado não a ressuscita");
        checar(!livro.Abriu("C", UrlOutro) && livro.Vivas == 1 && livro.Url("C") is null && !livro.Recebeu("C", T0)
               && livro.Abriu("D", UrlSemUsuario) && livro.Usuario("D") is null && livro.Recebeu("D", T0.AddSeconds(4)) && livro.AtividadePorUrl(UrlSemUsuario) == T0.AddSeconds(4)
               && !livro.Abriu(null, UrlLojaCdp) && !livro.Abriu("", UrlLojaCdp) && !livro.Recebeu(null, T0) && livro.Url(null) is null && livro.Usuario(null) is null,
            "SK-89 WebSocket de outro host não entra no livro nem tem atividade; socket do Sendbird sem user_id entra (usuário nulo); nulo e vazio passam reto");
        livro.Fechou(null);
        livro.Limpar();
        checar(livro.Vivas == 0 && livro.Url("B") is null && livro.Ativas().Count == 0, "SK-90 limpar (a navegação) esvazia o livro");
        // o teto de emergência: só quando o fechamento nunca chega (nunca em operação normal)
        var semFechar = new ConexoesDoSocket();
        for (var i = 0; i < 250; i++) semFechar.Abriu("s" + i, UrlLojaCdp);
        checar(semFechar.Vivas <= ConexoesDoSocket.Teto && semFechar.Vivas >= 1 && semFechar.Url("s249") == UrlLojaCdp && ConexoesDoSocket.Teto == 200,
            "SK-91 250 sockets sem nenhum fechamento: o teto de 200 segura a memória e o último aberto continua conhecido");
    }

    // ── 1.0.40 (revisão): socket_ativo_s é desde o ÚLTIMO quadro do escolhido, na hora do sinal ──
    private static void ODiagAoVivo(Action<bool, string> checar)
    {
        // a contagem congelou AtivoEm em T; o socket escolhido recebeu PONG em T+100; o sinal sai em T+110: 10 s, não 110
        var resumo = new ResumoDosSockets(2, "unico", 1, 0, T0);
        var vivo = EnvioPeloSocket.AoVivo(resumo, T0.AddSeconds(100));
        var o = new JsonObject();
        EnvioPeloSocket.DiagDosSockets(o, vivo, T0.AddSeconds(110));
        checar((int?)o["socket_ativo_s"] == 10 && vivo.AtivoEm == T0.AddSeconds(100) && vivo.Abertos == 2 && vivo.Escolha == "unico",
            "SK-92 o diag recalcula a atividade do escolhido na hora: 10 s, e não os 110 desde a contagem");
        checar(EnvioPeloSocket.AoVivo(resumo, null) == resumo && EnvioPeloSocket.AoVivo(resumo, T0.AddSeconds(-5)) == resumo,
            "SK-93 sem atividade ao vivo (a conexão fechou) ou mais velha que a da contagem: fica a da contagem (nunca anda para trás)");
        var semEscolha = new ResumoDosSockets(2, "ambiguo", 2, 0, null);
        checar(EnvioPeloSocket.AoVivo(semEscolha, null) == semEscolha && EnvioPeloSocket.AoVivo(semEscolha, T0.AddSeconds(100)).AtivoEm == T0.AddSeconds(100),
            "SK-94 sem escolhido o null continua null; com atividade ao vivo ela entra");
        // o livro + o resumo, como a tela faz no sinal: a URL escolhida do envio casa com a conexão do CDP (barra a mais ou a menos)
        var livro = new ConexoesDoSocket();
        livro.Abriu("A", UrlLojaCdp);
        livro.Recebeu("A", T0.AddSeconds(100));
        var o2 = new JsonObject();
        EnvioPeloSocket.DiagDosSockets(o2, EnvioPeloSocket.AoVivo(resumo, livro.AtividadePorUrl(UrlLoja)), T0.AddSeconds(110));
        checar((int?)o2["socket_ativo_s"] == 10 && Limpo(o2.ToJsonString()), "SK-95 pelo livro: a URL do escolhido (ws.url, com a barra) acha a conexão do CDP (sem a barra) e o diag sai certo, sem URL");
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

        // 1.0.40: o pedido traz a atividade por URL; o envio vai no socket mais ativo do usuário e o resultado resume os sockets
        var ecosA = new EcosDoSocket();
        var pA = new Portas { Urls = { UrlLoja, UrlLoja2, UrlEntregador }, Ecos = ecosA, Responder = req => Eco(req, 77) };
        var pedA = Pedir(urlDoCanal: null, usuario: Loja) with { Atividade = u => u == UrlLoja2 ? T0.AddSeconds(40) : u == UrlLoja ? T0.AddSeconds(2) : null };
        var rA = await Rodar(pA, ecosA, pedA);
        checar(rA.Ok && pA.Enviados.Count == 1 && pA.Enviados[0].Indice == 1 && rA.Escolha == "usuario"
               && rA.Sockets is { Abertos: 3, Usuarios: 2, SemUsuario: 0 } && rA.Sockets.AtivoEm == T0.AddSeconds(40),
            "SK-81 o envio escolhe o socket mais ativo do usuário da loja e o resultado resume os sockets (abertos, usuários, sem usuário, atividade)");
        // 1.0.40 (revisão): o resultado leva a URL do socket escolhido SÓ em memória (a tela recalcula a atividade dele no sinal); nunca no detalhe nem na linha
        checar(rA.UrlEscolhida == UrlLoja2 && r.UrlEscolhida == UrlLoja && Limpo(rA.Detalhe.ToJsonString()) && Limpo(EnvioPeloSocket.Linha(rA)),
            "SK-96 o resultado diz qual URL foi a escolhida (só memória) e ela não aparece no detalhe nem no rastro");

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
               && p5.Enviados.Count == 0 && p5.Liberacoes == 1 && r5.UrlEscolhida is null,
            "SK-26 socket ambíguo: falha socket_ambiguo e NADA sai (e nenhuma URL escolhida)");

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
                Versao = "1.0.40",
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
        // 1.0.40: a atividade por conexão (qualquer quadro recebido, antes de ler o payload), limpa com os irmãos; a loja
        // aprendida por qualquer quadro recebido; o diag dos sockets pelo núcleo
        // revisão: o livro das conexões (ConexoesDoSocket) no lugar dos três dicionários irmãos; o fechamento limpa os três
        var recebido = tela.IndexOf("\"Network.webSocketFrameReceived\"", StringComparison.Ordinal);
        var ativ = recebido < 0 ? -1 : tela.IndexOf("_conexoes.Recebeu(conexao, DateTime.Now)", recebido, StringComparison.Ordinal);
        var payloadNulo = recebido < 0 ? -1 : tela.IndexOf("if (p is null) return;", recebido, StringComparison.Ordinal);
        var fechado = tela.IndexOf("\"Network.webSocketClosed\"", StringComparison.Ordinal);
        var fechou = fechado < 0 ? -1 : tela.IndexOf("_conexoes.Fechou(req);", fechado, StringComparison.Ordinal);
        checar(ativ > recebido && payloadNulo > ativ && fechou > fechado && fechou < recebido
               && tela.Contains("private readonly ConexoesDoSocket _conexoes = new();", StringComparison.Ordinal)
               && tela.Contains("if (_conexoes.Abriu(req, u.GetString()))", StringComparison.Ordinal)
               && !tela.Contains("_wsAtividadePorConexao", StringComparison.Ordinal) && !tela.Contains("_wsUrlPorConexao", StringComparison.Ordinal)
               && !tela.Contains("_wsUserPorConexao", StringComparison.Ordinal),
            "SK-82 a tela marca a atividade de cada conexão no livro (qualquer comando, antes de ler o payload) e o fechamento tira a conexão do livro inteiro (URL, usuário, atividade)");
        checar(tela.Contains("ServicoConversaChat.Quadro(p, enviado: false, conexao, WsUser(conexao));", StringComparison.Ordinal)
               && servico.Contains("if (!enviado && UsuarioDaLojaNoQuadro(payload, wsUserId) is { } aprendido) _wsUserId = aprendido;", StringComparison.Ordinal)
               && tela.Contains("var u = _conexoes.Usuario(conexao);", StringComparison.Ordinal),
            "SK-83 a tela entrega todo quadro recebido ao serviço com o user_id do livro, e o serviço aprende a loja por qualquer um de canal de cliente (só os recebidos)");
        checar(tela.Contains("EnvioPeloSocket.AtividadeDosAbertos(urls, _conexoes.Ativas())", StringComparison.Ordinal)
               && tela.Contains("Atividade: _conexoes.AtividadePorUrl", StringComparison.Ordinal),
            "SK-84 a tela casa a atividade com as URLs da heap pelo livro e manda a atividade no pedido");
        // revisão: socket_ativo_s é desde o último quadro do escolhido NA HORA do sinal (não desde a contagem)
        checar(tela.Contains("EnvioPeloSocket.DiagDosSockets(r, EnvioPeloSocket.AoVivo(sk, _conexoes.AtividadePorUrl(_socketEscolhido)), DateTime.Now);", StringComparison.Ordinal)
               && tela.Contains("_socketEscolhido = r.UrlEscolhida;", StringComparison.Ordinal)
               && tela.Contains("_socketEscolhido = indice >= 0 ? urls[indice] : null;", StringComparison.Ordinal)
               && tela.IndexOf("_sockets = null;", StringComparison.Ordinal) is var zera && zera > 0
               && tela.IndexOf("_socketEscolhido = null;", zera, StringComparison.Ordinal) is var zeraEscolhido && zeraEscolhido > zera && zeraEscolhido - zera < 80,
            "SK-97 o diag recalcula a atividade do socket escolhido pelo livro na hora do sinal; o escolhido vem do envio e da contagem; a navegação zera");
        checar(Ler("Pdv.csproj").Contains("<Version>1.0.60</Version>", StringComparison.Ordinal), "SK-59 a versão do caixa é 1.0.60 (chat sai da ajuda presa)");
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
