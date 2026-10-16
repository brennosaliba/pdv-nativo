using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A FALA JÁ RESPONDIDA PELA EQUIPE (1.0.42, 16/10/2026, Savassi 17:47, pedido 0002). A recuperação pela REST entregou
/// ao robô os códigos que a cliente mandou às 17:02 e que a equipe JÁ TINHA ATENDIDO à mão no chat às 17:05 ("um
/// brownie nutella vai junto ao seu pedido"); o robô respondeu 45 min depois "seu pedido já saiu da loja, então não
/// dá mais para colocar o prêmio nele", contradizendo a equipe. Regra: a fala de cliente recuperada que tem, no MESMO
/// canal, uma mensagem da EQUIPE (humana: não é req_id "pdv..." nem texto igual a uma saída nossa) DEPOIS dela vai SÓ
/// para o arquivo (ifood-chat-arquivo), nunca para o robô. Sem resposta da loja depois dela, segue indo ao robô.
///  RP  a regra pura (RespondidasPelaEquipe, EhDaEquipe, IdDaMensagem)
///  RV  a varredura: entrega pela porta certa e conta no diag
///  RF  o fonte: o caixa liga as portas (só o arquivo; o texto do robô); sem travessão
/// </summary>
public static class TestesRecuperacaoRespondida
{
    private static readonly DateTime T0 = new(2026, 10, 16, 17, 47, 0);
    private const string Loja = "loja-ff49";
    private const string AppId = "6913AB";
    private const string Pedido = "00020000-1111-4222-8333-444444444444";
    private const string Savassi = "ff490000-0000-4000-8000-000000000001";
    private const string Canal = "sendbird_gc_cm_" + Pedido + "_" + Savassi;
    private const string Pedido2 = "00030000-1111-4222-8333-444444444444";
    private const string Canal2 = "sendbird_gc_cm_" + Pedido2 + "_" + Savassi;
    private const string Pedido3 = "00040000-1111-4222-8333-444444444444";
    private const string Canal3 = "sendbird_gc_cm_" + Pedido3 + "_" + Savassi;
    private const string TextoDoRobo = "Oba! Seu codigo vale 1 Cookie Classico. Qual sabor voce quer?";

    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();

    private static JsonElement J(string json)
    {
        using var d = JsonDocument.Parse(json);
        return d.RootElement.Clone();
    }

    private static string Rest(string tipo, string texto, string autor = "cli-2", string? userType = "CUSTOMER",
        long? id = 1, long criadoMs = 0, string canal = Canal, string reqId = "")
        => "{\"type\":\"" + tipo + "\"," + (id is null ? "" : "\"message_id\":" + id + ",")
           + "\"message\":" + JsonSerializer.Serialize(texto) + ",\"channel_url\":\"" + canal + "\",\"created_at\":" + criadoMs
           + ",\"req_id\":\"" + reqId + "\",\"custom_type\":\"\",\"data\":\"\",\"mention_type\":\"users\""
           + ",\"user\":{\"user_id\":\"" + autor + "\",\"nickname\":\"Ana\",\"profile_url\":\"\",\"metadata\":"
           + (userType is null ? "{}" : "{\"userType\":\"" + userType + "\"}") + "}}";

    private static string Mensagens(params string[] itens) => "{\"messages\":[" + string.Join(",", itens) + "]}";

    private static string CanalDaLista(string canal, long ultimaMs, long msgId)
        => "{\"channel_url\":\"" + canal + "\",\"name\":\"\",\"unread_message_count\":1,\"last_message\":{\"type\":\"MESG\",\"message_id\":" + msgId
           + ",\"created_at\":" + ultimaMs + ",\"message\":\"x\",\"user\":{\"user_id\":\"cli-2\",\"metadata\":{\"userType\":\"CUSTOMER\"}}}}";

    private static string Lista(params string[] canais) => "{\"channels\":[" + string.Join(",", canais) + "],\"next\":\"\"}";

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        ARegra(checar);
        await AVarreduraAsync(checar);
        PeloFonte(checar);
    }

    // o canal do pedido 0002: os códigos às 17:02, a equipe às 17:05 e 17:08
    private static string Canal0002() => Mensagens(
        Rest("MESG", "AD-7KQ2MX", id: 501, criadoMs: Ms(T0.AddMinutes(-45))),
        Rest("MESG", "AD-9PL3ZZ", id: 502, criadoMs: Ms(T0.AddMinutes(-44))),
        Rest("MESG", "Oi! um brownie nutella vai junto ao seu pedido", autor: Loja, userType: "MERCHANT", id: 503, criadoMs: Ms(T0.AddMinutes(-42))),
        Rest("MESG", "obrigada!!", id: 504, criadoMs: Ms(T0.AddMinutes(-41))),
        Rest("MESG", "de nada, bom apetite", autor: Loja, userType: "MERCHANT", id: 505, criadoMs: Ms(T0.AddMinutes(-39))));

    private static void ARegra(Action<bool, string> checar)
    {
        var r = RecuperacaoSendbird.RespondidasPelaEquipe(J(Canal0002()), Loja, 0, null);
        checar(r.SetEquals(new[] { "501", "502", "504" }), $"RP-1 fala do cliente 17:02 + loja humana 17:05 depois: as três falas da cliente estão respondidas ({string.Join(",", r)})");

        // o canal com a fala às 13:21 e nada da loja depois: vai ao robô
        var semResposta = Mensagens(
            Rest("MESG", "já vou ver", autor: Loja, userType: "MERCHANT", id: 601, criadoMs: Ms(T0.AddHours(-5))),
            Rest("MESG", "AD-1AB2CD", id: 602, criadoMs: Ms(T0.AddHours(-4).AddMinutes(-26)), canal: Canal2));
        checar(RecuperacaoSendbird.RespondidasPelaEquipe(J(semResposta), Loja, 0, null).Count == 0, "RP-2 a fala sem resposta da loja DEPOIS dela (a loja só falou antes) não está respondida: vai ao robô mesmo com horas");

        // a loja depois, mas é o robô: pelo req_id "pdv..." ou pelo texto igual a uma saída nossa
        var soRobo = Mensagens(
            Rest("MESG", "AD-1AB2CD", id: 701, criadoMs: Ms(T0.AddMinutes(-10)), canal: Canal3),
            Rest("MESG", TextoDoRobo, autor: Loja, userType: "MERCHANT", id: 702, criadoMs: Ms(T0.AddMinutes(-9)), canal: Canal3, reqId: "pdv1000-1-1760630000000"),
            Rest("MESG", TextoDoRobo, autor: Loja, userType: "MERCHANT", id: 703, criadoMs: Ms(T0.AddMinutes(-8)), canal: Canal3));
        checar(RecuperacaoSendbird.RespondidasPelaEquipe(J(soRobo), Loja, 0, t => t == TextoDoRobo).Count == 0,
            "RP-3 fala + saída do próprio robô depois (req_id pdv..., ou o texto de uma saída nossa): NÃO é resposta humana, vai ao robô");
        checar(RecuperacaoSendbird.RespondidasPelaEquipe(J(soRobo), Loja, 0, null).SetEquals(new[] { "701" }),
            "RP-3b sem a porta do texto, a mensagem 703 (sem req_id) conta como humana: a porta é o que separa");

        var m = J(Rest("MESG", "oi", autor: Loja, userType: "MERCHANT", id: 9, criadoMs: 5));
        checar(RecuperacaoSendbird.EhDaEquipe(m, Loja, null) && !RecuperacaoSendbird.EhDaEquipe(J(Rest("MESG", "oi", id: 9, criadoMs: 5)), Loja, null),
            "RP-4 EhDaEquipe: da loja sim, do cliente não");
        checar(!RecuperacaoSendbird.EhDaEquipe(J(Rest("MESG", "oi", autor: Loja, userType: "MERCHANT", id: 9, criadoMs: 5, reqId: "pdv77-1-1")), Loja, null)
               && !RecuperacaoSendbird.EhDaEquipe(m, Loja, _ => true) && RecuperacaoSendbird.EhDaEquipe(m, Loja, _ => throw new Exception("x")),
            "RP-4b o req_id do caixa e o texto do robô tiram da equipe; a porta que lança é ignorada (conta como humana)");
        checar(RecuperacaoSendbird.IdDaMensagem(m) == "9" && RecuperacaoSendbird.IdDaMensagem(J(Rest("MESG", "x", id: null, criadoMs: 77))) == "ts:77"
               && RecuperacaoSendbird.IdDaMensagem(J("[1]")) is null,
            "RP-5 o id da mensagem é o message_id, senão ts:created_at; sem objeto, null");
        var quadro = RecuperacaoSendbird.QuadroDoHistorico(J(Rest("MESG", "AD-7KQ2MX", id: 501, criadoMs: Ms(T0.AddMinutes(-45)))));
        checar(quadro is not null && RecuperacaoSendbird.IdDoQuadro(quadro) == "501" && RecuperacaoSendbird.IdDoQuadro("MESG{lixo") is null && RecuperacaoSendbird.IdDoQuadro(null) is null,
            "RP-6 o id do quadro montado casa com o da mensagem (501); lixo e nulo dão null");
        // a marca: a fala abaixo da marca não entra na conta (já foi entregue antes)
        checar(RecuperacaoSendbird.RespondidasPelaEquipe(J(Canal0002()), Loja, Ms(T0.AddMinutes(-41)), null).SetEquals(new[] { "504" }),
            "RP-7 só a fala acima da marca d'água entra na conta (a de antes já teve o destino dela)");
        checar(RecuperacaoSendbird.RespondidasPelaEquipe(J("{\"messages\":\"x\"}"), Loja, 0, null).Count == 0 && RecuperacaoSendbird.RespondidasPelaEquipe(J("[]"), null, 0, null).Count == 0,
            "RP-8 lixo e vazio: nada, sem lançar");
    }

    private static async Task AVarreduraAsync(Action<bool, string> checar)
    {
        var lista = Lista(CanalDaLista(Canal, Ms(T0.AddMinutes(-39)), 505), CanalDaLista(Canal2, Ms(T0.AddHours(-4).AddMinutes(-26)), 602), CanalDaLista(Canal3, Ms(T0.AddMinutes(-8)), 703));
        var semResposta = Mensagens(Rest("MESG", "AD-1AB2CD", id: 602, criadoMs: Ms(T0.AddHours(-4).AddMinutes(-26)), canal: Canal2));
        var soRobo = Mensagens(
            Rest("MESG", "AD-1AB2CD", id: 701, criadoMs: Ms(T0.AddMinutes(-10)), canal: Canal3),
            Rest("MESG", TextoDoRobo, autor: Loja, userType: "MERCHANT", id: 703, criadoMs: Ms(T0.AddMinutes(-8)), canal: Canal3));
        var robo = new List<string>(); var arquivo = new List<string>(); var diag = new List<string>();
        var v = new Varredura
        {
            Http = (r, _) =>
            {
                var url = r.RequestUri!.ToString();
                var corpo = !url.Contains("/messages?") ? lista : url.Contains(Pedido2) ? semResposta : url.Contains(Pedido3) ? soRobo : Canal0002();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") });
            },
            Agora = () => T0,
            Entregar = q => { lock (robo) robo.Add(q); },
            EntregarSoArquivo = q => { lock (arquivo) arquivo.Add(q); },
            TextoDoRobo = t => t == TextoDoRobo,
            Diag = l => { lock (diag) diag.Add(l); },
            Credencial = () => new CredencialSendbird(AppId, Loja, "k1k1k1k1k1k1k1k1k1k1k1k1k1k1", SessaoSendbird.FonteLogi, T0),
            UsuarioDaLoja = () => Loja,
        };
        var r = await v.ExecutarAsync();
        var idsRobo = robo.Select(RecuperacaoSendbird.IdDoQuadro).ToList();
        var idsArquivo = arquivo.Select(RecuperacaoSendbird.IdDoQuadro).ToList();
        checar(r.Erro is null && r.Lidos == 3, $"RV-1 a varredura leu os três canais (erro={r.Erro}, lidos={r.Lidos})");
        checar(idsArquivo.OrderBy(x => x).SequenceEqual(new[] { "501", "502", "504" }), $"RV-2 as falas do pedido 0002 (já atendidas pela equipe) foram SÓ para o arquivo ({string.Join(",", idsArquivo)})");
        checar(idsRobo.OrderBy(x => x).SequenceEqual(new[] { "602", "701" }), $"RV-3 a fala das 13:21 sem resposta e a fala seguida só do robô foram ao robô ({string.Join(",", idsRobo)})");
        checar(diag.Any(l => l == "recuperacao: 3 falas ja respondidas pela equipe (so arquivo)"), $"RV-4 o diag diz quantas ({string.Join(" | ", diag)})");
        checar(diag.Any(l => l.Contains("novas=2", StringComparison.Ordinal)), "RV-5 as já respondidas não contam como novas do robô");
        checar(!v.Dedup.Nova("sb:501") && !v.Dedup.Nova("sb:602"), "RV-6 as duas portas passam pelo dedup: nenhuma sai duas vezes");

        // sem a porta do arquivo, a fala respondida não vai a lugar nenhum (nunca ao robô)
        var robo2 = new List<string>();
        var v2 = new Varredura
        {
            Http = v.Http, Agora = () => T0, Entregar = q => { lock (robo2) robo2.Add(q); }, Credencial = v.Credencial, UsuarioDaLoja = () => Loja,
        };
        await v2.ExecutarAsync();
        var ids2 = robo2.Select(RecuperacaoSendbird.IdDoQuadro).OrderBy(x => x).ToList();
        checar(ids2.SequenceEqual(new[] { "602" }), $"RV-7 sem as portas: as respondidas não vão a lugar nenhum (nunca ao robô); a 703 (da loja, sem req_id e sem a porta do texto) conta como humana e segura a 701 ({string.Join(",", ids2)})");
    }

    private static void PeloFonte(Action<bool, string> checar)
    {
        var servico = Ler("ServicoRecuperacaoChat.cs");
        checar(servico.Contains("EntregarSoArquivo = q => { try { _ponte?.EntregarSoArquivo?.Invoke(q); }") && servico.Contains("TextoDoRobo = ServicoConversaChat.TextoDoRobo,"),
            "RF-1 o serviço liga as duas portas: só o arquivo e o texto do robô");
        var tela = Ler("Telas/ChatIfood.xaml.cs");
        var i = tela.IndexOf("private void EntregarRecuperadoSoArquivo(string payload)", StringComparison.Ordinal);
        var corpo = i < 0 ? "" : tela[i..Math.Min(tela.Length, i + 600)];
        checar(corpo.Contains("ServicoArquivoChat.Quadro(payload, enviado: false, conexao: \"rest\"") && !corpo.Contains("QuadroRecuperado") && !corpo.Contains("OuvirMensagem"),
            "RF-2 a tela entrega a fala respondida SÓ ao arquivo (nem o robô nem o leitor antigo)");
        checar(tela.Contains("EntregarSoArquivo = EntregarRecuperadoSoArquivo"), "RF-3 a ponte da recuperação leva a porta");
        var conversa = Ler("ServicoConversaChat.cs");
        checar(conversa.Contains("LembrarTextoDoRobo(s.Texto);") && conversa.Contains("public static bool TextoDoRobo(string? texto)"),
            "RF-4 o caixa lembra o texto de cada saída que põe na fila de envio");
        checar(ServicoConversaChat.TextoDoRobo(null) == false, "RF-5 TextoDoRobo nunca lança com nulo");
        ServicoConversaChat.LembrarTextoDoRobo("  Oba!  Seu codigo vale 1 Cookie Classico. Qual sabor voce quer? ");
        checar(ServicoConversaChat.TextoDoRobo(TextoDoRobo) && !ServicoConversaChat.TextoDoRobo("um brownie nutella vai junto ao seu pedido"),
            "RF-6 o texto lembrado casa pelo hash normalizado (espaços e caixa não contam); o da equipe não casa");
        foreach (var arq in new[] { "Pdv.Testes/TestesRecuperacaoRespondida.cs" })
        {
            var t = Ler(arq);
            checar(t.Length > 0 && !t.Contains((char)0x2014) && !t.Contains((char)0x2013), "RF-7 sem travessão: " + arq);
        }
    }

    private static string Ler(string relativo)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var caminho = Path.Combine(dir, relativo);
            if (File.Exists(caminho)) return File.ReadAllText(caminho);
            dir = Path.GetDirectoryName(dir);
        }
        return "";
    }
}
