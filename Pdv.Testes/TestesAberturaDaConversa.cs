using System.Text.Json;
using System.Text.Json.Nodes;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A PRIMEIRA MENSAGEM PARA QUEM NUNCA ABRIU O CHAT (1.0.62, 30/10/2026). O dono: as boas-vindas e o "saiu para
/// entrega" so chegavam a quem ja tinha escrito no chat. O canal do pedido so existe depois da primeira mensagem; o
/// envio pelo SDK voltava 900500 (canal_inexistente) e a mensagem morria. Agora a mensagem do pedido que traz o
/// cliente (SQL 246, cliente_ifood_id) abre a conversa pela API do proprio Gestor, como o botao do Gestor faz.
///
///  AB-1..6  a parte pura: quais APIs dao o token, o que cabe, o script (tudo em JSON, sem cookie), a leitura
///  AB-7..12 o laco: canal inexistente com cliente abre e relata enviada; sem cliente, sem a porta ou com a abertura
///           recusada fica como antes (canal_inexistente); uma vez so; resposta do robo nunca abre
///  AB-13..16 a fiacao: a leitura do cliente_ifood_id, a ponte da tela, o token so em memoria, o Runtime.evaluate
/// </summary>
public static class TestesAberturaDaConversa
{
    private static readonly DateTime T0 = new(2026, 10, 30, 15, 0, 0);
    private const string Loja = "ff490000-0000-4000-8000-000000000001";
    private const string Pedido = "11111111-2222-4333-8444-555555555555";
    private const string Cliente = "9a1f3c2e-4b5d-4e6f-8a7b-0c1d2e3f4a5b";
    private const string Canal = "sendbird_gc_cm_" + Pedido + "_" + Loja;

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        APartePura(checar);
        await OLacoAsync(checar);
        AFiacao(checar);
    }

    private static void APartePura(Action<bool, string> checar)
    {
        checar(AberturaDaConversa.EhApiDoGestor("https://order-manager-api.ifood.com.br/orderManager/v1.0/orders?x=1")
               && AberturaDaConversa.EhApiDoGestor("https://merchant-api.ifood.com.br/chat/v1.0/auth")
               && !AberturaDaConversa.EhApiDoGestor("https://api-ef74.sendbird.com/v3/x")
               && !AberturaDaConversa.EhApiDoGestor("http://order-manager-api.ifood.com.br/x")
               && !AberturaDaConversa.EhApiDoGestor("https://order-manager-api.ifood.com.br.evil.com/x")
               && !AberturaDaConversa.EhApiDoGestor(null),
            "AB-1 o token so e aprendido das APIs do Gestor (order-manager e merchant), em https, nunca de outro host");
        checar(AberturaDaConversa.Cabe(Pedido, Cliente, "Oi!") && !AberturaDaConversa.Cabe(Pedido, null, "Oi!")
               && !AberturaDaConversa.Cabe(Pedido, "c403-ana", "Oi!") && !AberturaDaConversa.Cabe("5826", Cliente, "Oi!")
               && !AberturaDaConversa.Cabe(Pedido, Cliente, "  "),
            "AB-2 so abre com o pedido e o cliente em uuid e texto de verdade");
        var script = AberturaDaConversa.Script(Pedido.ToUpperInvariant(), Cliente, "Oi, Ana! ❤️🍩\n\nObrigado' \"x\" </script>", "tok.en");
        checar(script.Contains("\"https://order-manager-api.ifood.com.br/orderManager/v1.0/chat/orders/" + Pedido + "\"")
               && script.Contains("method: 'POST'") && script.Contains("credentials: 'omit'") && script.Contains("'app-platform': 'web'")
               && script.Contains("\"Bearer tok.en\""),
            "AB-3 o script faz o mesmo POST do Gestor (pedido em minusculas, Bearer, sem cookie)");
        // o corpo vai como uma string JSON dentro do script: o texto do dono nunca vira codigo
        var i = script.IndexOf("body: ", StringComparison.Ordinal) + 6;
        var literal = script[i..script.IndexOf(" });", i, StringComparison.Ordinal)];
        var corpo = JsonSerializer.Deserialize<string>(literal) ?? "";
        var obj = JsonNode.Parse(corpo)!;
        checar(obj["customerId"]?.GetValue<string>() == Cliente && obj["message"]?.GetValue<string>() == "Oi, Ana! ❤️🍩\n\nObrigado' \"x\" </script>",
            "AB-4 o corpo e { customerId, message } com o texto inteiro (quebra de linha, emoji, aspas), so como dado");
        var ok = AberturaDaConversa.Ler("{\"result\":{\"type\":\"object\",\"value\":{\"status\":200,\"corpo\":\"{\\\"chatId\\\":\\\"sendbird_gc_cm_x\\\"}\"}}}");
        var nao = AberturaDaConversa.Ler("{\"result\":{\"type\":\"object\",\"value\":{\"status\":401,\"corpo\":\"unauthorized\"}}}");
        var zero = AberturaDaConversa.Ler("{\"result\":{\"type\":\"object\",\"value\":{\"status\":0,\"corpo\":\"Failed to fetch\"}}}");
        checar(ok.Ok && ok.ChatId == "sendbird_gc_cm_x" && ok.Detalhe["via"]?.GetValue<string>() == "abriu_conversa"
               && !nao.Ok && nao.Erro == "abrir_http_401" && !zero.Ok && zero.Erro == "abrir_http_0",
            "AB-5 2xx abriu (com o chatId); 401 e a chamada que nem saiu viram abrir_http_NNN");
        checar(!AberturaDaConversa.Ler(null).Ok && AberturaDaConversa.Ler("lixo").Erro == "abrir_ilegivel",
            "AB-6 resposta ilegivel nao abre e nao lanca");
    }

    private static JsonObject Eror900500() => new()
    {
        ["etapa"] = "falhou_socket", ["via"] = "socket", ["escolha"] = "usuario", ["abertos"] = 2, ["codigo"] = 900500, ["msg"] = "Channel not found.",
    };

    private static async Task OLacoAsync(Action<bool, string> checar)
    {
        var pedido = new SaidaConversa(1000000000500, "msg_pedido_obrigado", 1, "Oi, Ana! Obrigado pelo pedido!", "sdk", "reservada",
            Canal, Pedido, "4932", true, Cliente);

        var c1 = new Cena { Saida = pedido, Abrir = (_, _, _) => new AberturaResultado(true, null, "sendbird_gc_cm_novo", new JsonObject { ["via"] = "abriu_conversa", ["status"] = 200 }) };
        await c1.Rodar();
        checar(c1.Aberturas.Count == 1 && c1.Aberturas[0] == (Pedido, Cliente, "Oi, Ana! Obrigado pelo pedido!"),
            $"AB-7 o canal inexistente da mensagem do pedido abre a conversa com o pedido, o cliente e o texto (aberturas={c1.Aberturas.Count})");
        checar(c1.Relatos.Count == 1 && c1.Relatos[0].Resultado == "enviada" && c1.Relatos[0].Erro is null
               && c1.Relatos[0].Detalhe?["via"]?.GetValue<string>() == "abriu_conversa",
            $"AB-8 abriu: o relato e 'enviada', com o detalhe abriu_conversa ({string.Join(",", c1.Relatos.Select(r => r.Resultado + "/" + r.Erro))})");
        checar(c1.Enviados.Count == 1 && c1.Esperas == 0, "AB-9 uma tentativa pelo SDK so, sem respiro: o resto foi a abertura");

        var c2 = new Cena { Saida = pedido, Abrir = (_, _, _) => new AberturaResultado(false, "abrir_http_401", null, new JsonObject { ["via"] = "abriu_conversa" }) };
        await c2.Rodar();
        checar(c2.Aberturas.Count == 1 && c2.Relatos.Count == 1 && c2.Relatos[0].Resultado == "falhou" && c2.Relatos[0].Erro == "canal_inexistente"
               && c2.Relatos[0].Detalhe?["abrir"]?.GetValue<string>() == "abrir_http_401",
            "AB-10 a abertura recusada fica como antes (falhou/canal_inexistente, que nao pausa o robo) e conta o porque no detalhe");

        var semCliente = new Cena { Saida = pedido with { ClienteIfoodId = null } };
        await semCliente.Rodar();
        var semPorta = new Cena { Saida = pedido, SemPorta = true };
        await semPorta.Rodar();
        checar(semCliente.Aberturas.Count == 0 && semCliente.Relatos.Single().Erro == "canal_inexistente"
               && semPorta.Relatos.Single().Erro == "canal_inexistente",
            "AB-11 sem o cliente (SQL 246 ainda nao rodou) ou sem a porta da tela: o canal inexistente de sempre");

        var resposta = new SaidaConversa(500, "pergunta_sabor", 1, "Qual sabor?", "sdk", "reservada", Canal, Pedido, "4932", false, Cliente);
        var c3 = new Cena { Saida = resposta, Abrir = (_, _, _) => new AberturaResultado(true, null, null, new JsonObject()) };
        await c3.Rodar();
        checar(c3.Aberturas.Count == 0, "AB-12 resposta do robo nunca abre conversa (o canal dela existe; 900500 ali e outra coisa)");
    }

    private static void AFiacao(Action<bool, string> checar)
    {
        var s = ConversaRaspadinha.LerSinal("{\"ok\":true,\"modo\":\"automatico\",\"palavra_modo\":\"desligado\",\"envio\":\"sdk\",\"comanda_onde\":\"kds\","
            + "\"merchant_ids\":[],\"intervalo_s\":60,\"pausado\":null,\"avisos\":[],\"comandas\":[],\"saidas\":[{\"id\":1000000000501,\"etapa\":\"msg_pedido_obrigado\","
            + "\"ordem\":1,\"texto\":\"Oi!\",\"como\":\"sdk\",\"estado\":\"reservada\",\"canal\":null,\"ifood_order_id\":\"" + Pedido + "\",\"pedido_numero\":\"4932\","
            + "\"pelo_dono\":true,\"so_sdk\":true,\"origem\":\"pedido\",\"cliente_ifood_id\":\"" + Cliente + "\"},{\"id\":1000000000502,\"etapa\":\"msg_pedido_saiu\","
            + "\"ordem\":1,\"texto\":\"Saiu!\",\"como\":\"sdk\",\"estado\":\"reservada\",\"ifood_order_id\":\"" + Pedido + "\",\"cliente_ifood_id\":null}]}");
        checar(s.Saidas.Count == 2 && s.Saidas[0].ClienteIfoodId == Cliente && s.Saidas[1].ClienteIfoodId is null,
            "AB-13 o sinal le o cliente_ifood_id do SQL 246 (e o nulo fica nulo)");
        var tela = Ler("Telas/ChatIfood.xaml.cs");
        checar(tela.Contains("AbrirConversaComMensagem = AbrirConversaComMensagemAsync,"), "AB-14 a tela liga a porta nova na ponte");
        checar(tela.Contains("AberturaDaConversa.EhApiDoGestor(url)") && tela.Contains("_tokenDoGestor = tg;")
               && !tela.Contains("Diag(\"token") && !System.Text.RegularExpressions.Regex.IsMatch(tela, @"Diag[A-Za-z]*\([^;]*_tokenDoGestor"),
            "AB-15 o token do Gestor e aprendido das chamadas dele e so fica em memoria (nunca no diag)");
        checar(tela.Contains("awaitPromise = true") && tela.Contains("AberturaDaConversa.Script(orderUuid, clienteId, texto, token)")
               && tela.Contains("AberturaDaConversa.IdadeMaximaDoToken"),
            "AB-16 a chamada espera a resposta da pagina (Runtime.evaluate com awaitPromise) e nao usa token velho");
        foreach (var arq in new[] { "Pdv.Nucleo/AberturaDaConversa.cs", "Pdv.Testes/TestesAberturaDaConversa.cs" })
        {
            var t = Ler(arq);
            checar(t.Length > 0 && !t.Contains((char)0x2014) && !t.Contains((char)0x2013), "AB-17 sem travessao: " + arq);
        }
    }

    private sealed class Cena
    {
        public DateTime Agora = T0.AddSeconds(1);
        public readonly ConfirmacaoDeEnvio Confirmacoes = new();
        public readonly PortaoDeEnvio Portao = new(new MemoriaDeEco(), new LimiteDeEnvio());
        public readonly List<string> Enviados = new();
        public readonly List<(string Pedido, string Cliente, string Texto)> Aberturas = new();
        public readonly List<(string Resultado, string? Erro, JsonObject? Detalhe)> Relatos = new();
        public int Esperas;
        public bool SemPorta;
        public Func<string, string, string, AberturaResultado>? Abrir;
        public required SaidaConversa Saida;

        public Task Rodar()
        {
            var sinal = ConversaRaspadinha.LerSinal("{\"ok\":true,\"modo\":\"automatico\",\"palavra_modo\":\"desligado\",\"envio\":\"sdk\","
                + "\"comanda_onde\":\"kds\",\"merchant_ids\":[\"" + Loja + "\"],\"intervalo_s\":60,\"pausado\":null,\"avisos\":[],\"saidas\":[],\"comandas\":[]}");
            var ponte = new PonteDoChat
            {
                EnviarPeloSdk = p => { Enviados.Add(p.Texto); Agora += TimeSpan.FromSeconds(2); return Task.FromResult(new ResultadoDoScript(false, "erro_envio", null, Eror900500())); },
                AbrirEColar = (_, _, _) => Task.FromResult(false),
                DiagSdk = (_, _) => Task.FromResult<JsonObject?>(null),
                Gestor = () => "logado",
                AquecerConversa = (_, _) => Task.FromResult(true),
                AbrirConversaComMensagem = SemPorta ? null : (o, c, t) =>
                {
                    Aberturas.Add((o, c, t));
                    return Task.FromResult(Abrir?.Invoke(o, c, t) ?? new AberturaResultado(false, "abrir_sem_teste", null, new JsonObject()));
                },
            };
            return ServicoConversaChat.EnviarComTentativasAsync(Saida, T0, new LacoDeEnvio
            {
                Portao = Portao,
                Confirmacoes = Confirmacoes,
                Sinal = () => (sinal, T0),
                Ponte = () => ponte,
                Relatar = (_, resultado, erro, _, detalhe) => { Relatos.Add((resultado, erro, detalhe)); return Task.CompletedTask; },
                Esperar = t => { Esperas++; Agora += t; return Task.CompletedTask; },
                Agora = () => Agora,
                Diag = _ => { },
                Versao = "1.0.62",
            });
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
