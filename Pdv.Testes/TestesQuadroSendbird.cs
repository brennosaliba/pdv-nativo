using System.Text.Json.Nodes;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// RESGATE PELO CHAT DO iFOOD (07/10/2026, desenho "resgate-final", tarefa P12): o LEITOR DO QUADRO.
///
/// O formato real do quadro MESG do Sendbird só a loja prova (seção 16). Aqui se prova o que dá
/// sem a página logada: só MESG vira fala, o autor sai do guest_id, o tipo de usuário sai do
/// metadata (na raiz ou no user, em objeto ou em texto), sussurro é marcado, JSON quebrado não
/// derruba nada, e o canal do pedido é lido com a mesma regra do ERP.
///
/// E o diagnóstico: o prefixo MESG não pode mais esconder o quadro, o teto é por conexão, e os
/// contadores do sinal são só chaves e contagens.
/// </summary>
public static class TestesQuadroSendbird
{
    public const string Pedido = "3f2a8c1e-1111-4a2b-9c3d-0123456789ab";
    public const string MerchantSavassi = "ff493e25-f413-42e5-a46a-96c0b788813f";
    public const string MerchantCastelo = "c35f0a1b-2222-4c3d-8e4f-abcdefabcdef";
    public const string Canal = "sendbird_gc_cm_" + Pedido + "_" + MerchantSavassi;

    /// <summary>Um quadro MESG no formato da família Sendbird.</summary>
    public static string Mesg(string texto, string autor = "cli-77", string? userType = "CUSTOMER",
        long? msgId = 8812736123, long criadoMs = 1790000000000, string canal = Canal, string extra = "")
    {
        var meta = userType is null ? "" : $",\"metadata\":{{\"userType\":\"{userType}\"}}";
        var id = msgId is null ? "" : $"\"msg_id\":{msgId},";
        return "MESG{" + id + "\"req_id\":\"\",\"message\":" + System.Text.Json.JsonSerializer.Serialize(texto)
               + ",\"channel_url\":\"" + canal + "\",\"created_at\":" + criadoMs
               + ",\"user\":{\"guest_id\":\"" + autor + "\",\"name\":\"Ana\"" + meta + "}" + extra + "}";
    }

    public static void Rodar(Action<bool, string> checar)
    {
        OLeitor(checar);
        OCanal(checar);
        AUrl(checar);
        ODiagnostico(checar);
    }

    // ── QuadroSendbird.Ler ───────────────────────────────────────────────────
    private static void OLeitor(Action<bool, string> checar)
    {
        var m = QuadroSendbird.Ler(Mesg("oi, ganhei na raspadinha AD-7KQ2MX"));
        checar(m is not null && m.Comando == "MESG" && m.Texto == "oi, ganhei na raspadinha AD-7KQ2MX",
            "QS-1 o quadro MESG vira fala, com o texto inteiro");
        checar(m?.Canal == Canal && m.MsgId == "8812736123" && m.ReqId is null && m.CriadoEmMs == 1790000000000,
            "QS-2 canal, msg_id e created_at saem do quadro (req_id vazio vira nulo)");
        checar(m?.AutorId == "cli-77", "QS-3 o autor sai do user.guest_id");
        checar(m?.UserType == "CUSTOMER" && m.Whisper == false, "QS-4 o tipo de usuário sai do metadata do user");

        foreach (var cmd in new[] { "ADMM", "FILE", "READ", "PING", "LOGI" })
            checar(QuadroSendbird.Ler(cmd + Mesg("AD-7KQ2MX")[4..]) is null,
                $"QS-5 {cmd} não vira fala (só MESG é o cliente escrevendo)");
        checar(QuadroSendbird.Ler(Mesg("AD-7KQ2MX")[4..]) is null, "QS-6 JSON sem o comando não é quadro do Sendbird");
        checar(QuadroSendbird.Ler("mesg" + Mesg("x")[4..]) is null, "QS-7 comando em minúscula não vale");

        // autor: guest_id primeiro, user_id como reserva
        var soUserId = QuadroSendbird.Ler("""MESG{"message":"oi","channel_url":"c","user":{"user_id":"u-9"},"created_at":1}""");
        checar(soUserId?.AutorId == "u-9", "QS-8 sem guest_id, o autor sai do user.user_id");
        var osDois = QuadroSendbird.Ler("""MESG{"message":"oi","user":{"user_id":"u-9","guest_id":"g-1"}}""");
        checar(osDois?.AutorId == "g-1", "QS-9 com os dois, vale o guest_id");

        // userType: na raiz e no user, em objeto e em texto JSON
        var raiz = QuadroSendbird.Ler("""MESG{"message":"oi","metadata":{"userType":"merchant"},"user":{"guest_id":"g"}}""");
        checar(raiz?.UserType == "MERCHANT", "QS-10 metadata.userType na raiz vale (e sai em maiúsculas)");
        var texto = QuadroSendbird.Ler("""MESG{"message":"oi","user":{"guest_id":"g","metadata":"{\"userType\":\"CUSTOMER\"}"}}""");
        checar(texto?.UserType == "CUSTOMER", "QS-11 metadata em texto JSON também é lido");
        checar(QuadroSendbird.Ler("""MESG{"message":"oi","user":{"guest_id":"g"}}""")?.UserType is null,
            "QS-12 sem metadata o tipo fica desconhecido (nulo), sem palpite");

        // sussurro
        var sus = QuadroSendbird.Ler(Mesg("oi", extra: ",\"data\":\"{\\\"whisperMode\\\":true}\""));
        checar(sus is not null && sus.Whisper, "QS-13 data.whisperMode ligado marca o sussurro (a triagem descarta)");
        var naoSus = QuadroSendbird.Ler(Mesg("oi", extra: ",\"data\":\"{\\\"whisperMode\\\":false}\""));
        checar(naoSus is not null && !naoSus.Whisper, "QS-14 whisperMode desligado não é sussurro");

        // msg_id em texto, zero e ausente
        checar(QuadroSendbird.Ler("""MESG{"message":"oi","msg_id":"123","req_id":"rq-1"}""") is { MsgId: "123", ReqId: "rq-1" },
            "QS-15 msg_id e req_id em texto também são lidos");
        checar(QuadroSendbird.Ler("""MESG{"message":"oi","msg_id":0}""")?.MsgId is null, "QS-16 msg_id zero é ausência");
        checar(QuadroSendbird.Ler(Mesg("oi", msgId: null))?.MsgId is null, "QS-17 sem msg_id fica nulo");

        // quebrado não lança
        foreach (var lixo in new[] { null, "", "MESG", "MESG{", "MESG{\"message\":", "MESG[1,2]", "MESG{\"sem\":\"texto\"}", "não é nada" })
        {
            var ok = true;
            try { ok = QuadroSendbird.Ler(lixo) is null; } catch { ok = false; }
            checar(ok, $"QS-18 quadro quebrado não vira fala nem exceção ({lixo ?? "null"})");
        }
        checar(QuadroSendbird.Comando("MESG{}") == "MESG" && QuadroSendbird.Comando("{}") is null
               && QuadroSendbird.Comando("AB") is null, "QS-19 o comando é só 4 letras maiúsculas no começo");
    }

    // ── CanalIfood.Ler ───────────────────────────────────────────────────────
    private static void OCanal(Action<bool, string> checar)
    {
        var c = CanalIfood.Ler(Canal);
        checar(c is { } x && x.OrderId == Pedido && x.MerchantId == MerchantSavassi,
            "CI-1 o canal do pedido dá o pedido e a loja (merchant)");
        checar(CanalIfood.Ler(Canal.ToUpperInvariant()) is null,
            "CI-2 maiúscula não passa: é a mesma regra do ERP, que recusaria como canal_invalido");
        checar(CanalIfood.Ler("sendbird_gc_" + Pedido + "_" + MerchantSavassi) is null, "CI-3 sem _cm_ não é conversa de pedido");
        checar(CanalIfood.Ler(Pedido) is null, "CI-4 uuid puro não é canal");
        checar(CanalIfood.Ler("sendbird_group_channel_12345") is null, "CI-5 outro canal do Sendbird não é pedido");
        checar(CanalIfood.Ler(Canal + "_x") is null && CanalIfood.Ler("x" + Canal) is null,
            "CI-6 sobra antes ou depois não passa (a regra é do começo ao fim)");
        checar(CanalIfood.Ler(null) is null && CanalIfood.Ler("") is null, "CI-7 vazio não explode");
        checar(CanalIfood.Ler(Canal + "\n") is null,
            "CI-8 quebra de linha no fim não passa (o $ do .NET aceitaria, o Postgres recusa)");
    }

    // ── o user_id da URL do WebSocket ────────────────────────────────────────
    private static void AUrl(Action<bool, string> checar)
    {
        checar(QuadroSendbird.UserIdDaUrl("wss://ws-abc.sendbird.com/?p=JS&ai=APP&user_id=loja-1&access_token=xyz") == "loja-1",
            "WS-1 o user_id da URL do WebSocket é o id da loja naquela conexão");
        checar(QuadroSendbird.UserIdDaUrl("wss://ws-abc.sendbird.com/?user_id=loja%2D1") == "loja-1", "WS-2 vem decodificado");
        checar(QuadroSendbird.UserIdDaUrl("wss://ws-abc.sendbird.com/?p=JS") is null
               && QuadroSendbird.UserIdDaUrl("wss://firefly/x") is null && QuadroSendbird.UserIdDaUrl(null) is null,
            "WS-3 sem user_id fica nulo");
        checar(QuadroSendbird.UserIdDaUrl("wss://x/?xuser_id=a&user_id=b") == "b", "WS-4 o nome do parâmetro é exato");
    }

    // ── o diagnóstico (ChatCaptura) ──────────────────────────────────────────
    private static void ODiagnostico(Action<bool, string> checar)
    {
        var q = Mesg("meu codigo AD-7KQ2MX", extra: ",\"access_token\":\"segredo-que-nao-pode-sair\"");
        checar(ChatCaptura.DescreverShape(q).StartsWith("MESG { msg_id:number", StringComparison.Ordinal),
            $"DG-1 o shape do quadro MESG aparece (era '(não-JSON)'): {ChatCaptura.DescreverShape(q)[..30]}");
        var masc = ChatCaptura.MascararJson(q);
        checar(masc.StartsWith("MESG{", StringComparison.Ordinal) && !masc.Contains("segredo-que-nao-pode-sair"),
            "DG-2 o quadro MESG sai mascarado, com o prefixo e sem o segredo");
        checar(ChatCaptura.MascararJson("não-json").Contains("omitido") && ChatCaptura.DescreverShape("MESGxyz") == "(não-JSON)",
            "DG-3 o que não é JSON continua omitido");

        var a = new ChatCaptura.Acumulador();
        for (var i = 0; i < 60; i++) a.RegistrarFrame("""{"pulso":1}""", false, "firefly");
        a.RegistrarFrame(Mesg("oi"), false, "sendbird");
        checar(a.QuadrosDaConexao("firefly") == ChatCaptura.Acumulador.TetoFrames,
            $"DG-4 o teto de quadros é por conexão ({a.QuadrosDaConexao("firefly")})");
        checar(a.QuadrosDaConexao("sendbird") == 1,
            "DG-5 a conexão barulhenta não tira a vaga do Sendbird (o MESG entra no diagnóstico)");
        checar(a.MontarDiagnostico().Contains("MESG {", StringComparison.Ordinal), "DG-6 e o diagnóstico mostra o shape do MESG");

        // contadores do sinal: só chaves e contagens
        var c = new ChatCaptura.ContadoresSendbird();
        var agora = new DateTime(2026, 10, 7, 19, 0, 0);
        c.Registrar(Mesg("AD-7KQ2MX", autor: "cli-77"), "loja-1", agora);
        c.Registrar(Mesg("Oi, Ana! Recebemos o código", autor: "loja-1", userType: "MERCHANT", msgId: 2), "loja-1", agora);
        c.Registrar(Mesg("psiu", extra: ",\"data\":\"{\\\"whisperMode\\\":true}\"", msgId: 3), "loja-1", agora);
        c.Registrar("ADMM" + Mesg("aviso do sistema")[4..], "loja-1", agora);
        c.Registrar("PING", "loja-1", agora);
        var j = c.Quadros().ToJsonString();
        checar((int?)c.Quadros()["mesg"] == 3, "DG-7 conta os MESG");
        checar(j.Contains("\"CUSTOMER\":2") && j.Contains("\"MERCHANT\":1"), "DG-8 conta os tipos de usuário");
        checar((int?)c.Quadros()["autor_igual_ws"] == 1 && (int?)c.Quadros()["autor_diferente_ws"] == 2,
            "DG-9 conta quantas falas têm o autor igual ao id da loja no WebSocket");
        checar((int?)c.Quadros()["whisper"] == 1, "DG-10 conta os sussurros");
        checar(j.Contains("\"MESG\":3") && j.Contains("\"ADMM\":1") && j.Contains("\"PING\":1"), "DG-11 conta os comandos");
        checar(j.Contains("\"channel_url\"") && j.Contains("\"guest_id\"") && j.Contains("\"metadata\""),
            "DG-12 lista os NOMES dos campos da raiz e do user");
        checar(!j.Contains("7KQ2MX") && !j.Contains("cli-77") && !j.Contains("loja-1") && !j.Contains("Recebemos")
               && !j.Contains(Pedido) && !j.Contains("Ana"),
            "DG-13 nenhum texto e nenhum id saem nos contadores");
        checar(c.CanaisPorMerchant().TryGetValue("ff49", out var n) && n == 1,
            "DG-14 conta as conversas de pedido por merchant (4 primeiros caracteres)");
        checar(c.UltimoQuadro == agora, "DG-15 guarda a hora do último quadro (ws vivo)");
        c.Registrar("""{"pulso":1}""", "loja-1", agora.AddMinutes(5));
        checar(c.UltimoQuadro == agora,
            "DG-16 o pulso de outra conexão (firefly, JSON puro) não diz que o WebSocket do chat está vivo");
        c.Registrar("PONG", "loja-1", agora.AddMinutes(6));
        checar(c.UltimoQuadro == agora.AddMinutes(6), "DG-17 o PONG do Sendbird diz");
    }
}
