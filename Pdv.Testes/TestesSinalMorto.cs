using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O SINAL MORTO POR InvalidOperationException (1.0.42, 16/10/2026, Savassi 12:23 a 15:20). A cada minuto o diag
/// dizia "sinal: InvalidOperationException" e o ERP não recebia sinal nenhum (vigia sem_sinal). A causa, reproduzida
/// aqui: a partir do PRIMEIRO MESG que o próprio Gestor manda (o operador respondendo à mão), a tela guarda os nomes
/// dos campos (mesg_campos_gestor); o estado do sinal copiava esses nomes com <c>JsonArray.Add(string)</c>, que no
/// .NET 8 cria um JsonValueCustomized (serializado por JsonTypeInfo); ao serializar com as opções próprias do caixa
/// (só o Encoder, sem TypeInfoResolver) o nó marca as opções como somente leitura e lança
/// "JsonSerializerOptions instance must specify a TypeInfoResolver setting before being marked as read-only"
/// (ConversaRaspadinha.EstadoDoSinal, linha do nomes.Add / ToJsonString(Json)). Toda rodada seguinte morria no mesmo
/// ponto, antes da rede, até o PDV reiniciar.
///  IM  a reprodução e a correção: o estado com mesg_campos_gestor sai duas vezes sem lançar
///  MN  o sinal mínimo: quando a montagem do estado falha, o sinal sai assim mesmo, com o erro anotado
///  CI  o canal inexistente (EROR 900500 numa mensagem do pedido): falha definitiva, sem nova tentativa, sem pausar
///  FO  o fonte: a rodada registra etapa e tipo+mensagem; os nomes entram como JsonValue primitivo; sem travessão
/// </summary>
public static class TestesSinalMorto
{
    private static readonly DateTime T0 = new(2026, 10, 16, 12, 23, 38);
    private const string Loja = "ff490000-0000-4000-8000-000000000001";
    private const string Pedido = "11111111-2222-4333-8444-555555555555";
    private const string Canal = "sendbird_gc_cm_" + Pedido + "_" + Loja;

    private const string DiagDaPagina = """
        {"tipo":"pdvsbdiag","id":"abc","login":false,"achou":false,"instancias":0,"user_id_igual_ws":false,"via_webpack":false,"via_react":false,"modulos":1,"runtimes":1,"via_heap":false,"via_berco":false,"mf":false,"heap_inst":0,"heap_validas":0,"heap_abertas":0,"fabricas":2694,"fab_sendbird":19,"globais_wp":"webpackChunk","bundle":"app.js","versao_sdk":"4.19.9","singleton":"vazio","singleton_conexao":"OUTRO","dom_sendbird":4,"dom_fibras":3,"iframes":0,"iframes_outros":0,"esm":4,"conferidos":0}
        """;

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        AReproducao(checar);
        OSinalMinimo(checar);
        await OCanalInexistenteAsync(checar);
        PeloFonte(checar);
    }

    /// <summary>O diagnóstico como a tela o entrega ao serviço (a página + o que o C# acrescenta, inclusive os nomes do MESG do Gestor).</summary>
    private static JsonObject Diag()
    {
        var r = (JsonObject)JsonNode.Parse(DiagDaPagina)!;
        r["heap_protos"] = 1; r["heap_objetos"] = 0;
        r["ws_sb_cdp"] = 25; r["ws_sb_cdp_vivos"] = 2; r["ws_sb_iniciador"] = "6913.89e6f3f0.js";
        EnvioPeloSocket.DiagDosSockets(r, new ResumoDosSockets(2, "usuario", 1, 1, T0.AddSeconds(-12)), T0);
        r["via_socket"] = false;
        var campos = new List<string> { "channel_url", "mention_type", "mentioned_user_ids", "message", "pin_message", "reply_to_channel", "req_id" };
        r["mesg_campos_gestor"] = new JsonArray(campos.Select(x => (JsonNode?)x).ToArray());
        return r;
    }

    private static void AReproducao(Action<bool, string> checar)
    {
        var quadros = new JsonObject { ["mesg"] = 72, ["campos_raiz"] = new JsonArray("channel_url", "req_id"), ["comandos"] = new JsonObject { ["MESG"] = 72 } };
        var canais = new Dictionary<string, int> { ["ff49"] = 4 };
        string? estado = null; Exception? erro = null;
        for (var rodada = 1; rodada <= 3 && erro is null; rodada++)
        {
            try
            {
                estado = ConversaRaspadinha.EstadoDoSinal("logado", true, T0.AddSeconds(-5), 0, quadros, Diag(), canais,
                    new ConversaRaspadinha.SaudeDoChat(true, "ok", "ok", 0), new ConversaRaspadinha.ContagemDoEnvio(4, 0), new ConversaRaspadinha.ContagemDeCartoes(0, 0, 0));
                _ = ConversaRaspadinha.CorpoSinal("DESKTOP", "1.0.42", estado);
            }
            catch (Exception ex) { erro = ex; }
        }
        checar(erro is null, $"IM-1 o estado do sinal com mesg_campos_gestor sai três rodadas seguidas sem lançar ({erro?.GetType().Name}: {erro?.Message})");
        var sdk = estado is null ? null : JsonNode.Parse(estado)?["sdk"]?["mesg_campos_gestor"] as JsonArray;
        checar(sdk is { Count: 7 } && sdk[0]!.GetValue<string>() == "channel_url" && sdk[6]!.GetValue<string>() == "req_id",
            "IM-2 os sete nomes dos campos do MESG do Gestor chegam ao ERP como texto");

        // a prova da causa, no .NET desta máquina: o Add(string) generico + opções só com Encoder
        Exception? causa = null;
        try
        {
            var opcoes = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            var nomes = new JsonArray();
            nomes.Add("channel_url");
            _ = new JsonObject { ["sdk"] = new JsonObject { ["mesg_campos_gestor"] = nomes } }.ToJsonString(opcoes);
        }
        catch (Exception ex) { causa = ex; }
        checar(causa is InvalidOperationException && causa.Message.Contains("TypeInfoResolver", StringComparison.Ordinal),
            $"IM-3 a causa de verdade: JsonArray.Add(string) + opções sem TypeInfoResolver lança InvalidOperationException ao serializar ({causa?.GetType().Name})");
        // o mesmo nome, como o caixa faz agora (JsonValue primitivo), serializa com as mesmas opções
        Exception? agora = null;
        try
        {
            var opcoes = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            var nomes = new JsonArray { (JsonNode?)"channel_url" };
            _ = new JsonObject { ["sdk"] = new JsonObject { ["mesg_campos_gestor"] = nomes } }.ToJsonString(opcoes);
        }
        catch (Exception ex) { agora = ex; }
        checar(agora is null, "IM-4 o nome como JsonValue primitivo ((JsonNode?)texto) serializa com as mesmas opções");
    }

    private static void OSinalMinimo(Action<bool, string> checar)
    {
        var minimo = ConversaRaspadinha.EstadoMinimo("logado", true, T0.AddSeconds(-5), 3, "InvalidOperationException", "estado");
        var o = JsonNode.Parse(minimo) as JsonObject;
        checar(o is not null && o["gestor"]?.GetValue<string>() == "logado" && o["ws_vivo"]?.GetValue<bool>() == true && o["fila"]?.GetValue<int>() == 3,
            "MN-1 o sinal mínimo leva gestor, ws_vivo e fila");
        checar(o?["estado_erro"]?.GetValue<string>() == "InvalidOperationException" && o["estado_etapa"]?.GetValue<string>() == "estado"
               && o["sdk"] is JsonObject { Count: 0 } && o["quadros"] is JsonObject { Count: 0 },
            "MN-2 e diz que a montagem do estado falhou (tipo e etapa), com sdk e quadros vazios");
        var corpo = JsonNode.Parse(ConversaRaspadinha.CorpoSinal("DESKTOP", "1.0.42", minimo)) as JsonObject;
        checar(corpo?["estado"] is JsonObject e && e["estado_erro"] is not null, "MN-3 o corpo do sinal aceita o estado mínimo");
        var sujo = ConversaRaspadinha.EstadoMinimo("logado", false, null, -1, "Tipo Com Espaço; drop", "etapa\nquebrada");
        var os = JsonNode.Parse(sujo) as JsonObject;
        checar(os?["estado_erro"]?.GetValue<string>() == "TipoComEspaodrop" && os["estado_etapa"]?.GetValue<string>() == "etapaquebrada" && os["fila"]?.GetValue<int>() == 0,
            "MN-4 tipo e etapa só com letra e número (nada de texto solto no sinal), fila nunca negativa");
        checar(ConversaRaspadinha.ResumoDaExcecao(new InvalidOperationException("JsonSerializerOptions instance must specify a TypeInfoResolver setting before being marked as read-only.")) ==
               "InvalidOperationException: JsonSerializerOptions instance must specify a TypeInfoResolver setting before being marked as read-only.",
            "MN-5 o resumo da exceção para o diag é tipo + mensagem limpa");
        var resumo = ConversaRaspadinha.ResumoDaExcecao(new Exception("token eyJhbGciOiJIUzI1NiJ9.eyJ1c2VyIjoiMzE5OTQ0MTI3MjUifQ.c2VncmVkby1kby1zb2NrZXQ do cliente joao@x.com em https://api.x/y\ncom 123456789"));
        checar(!resumo.Contains("eyJhbGci") && !resumo.Contains("joao@") && !resumo.Contains("https://") && !resumo.Contains("123456789") && !resumo.Contains('\n'),
            "MN-6 a mensagem passa pela limpeza de sempre (sem token, e-mail, URL, número longo nem quebra de linha): " + resumo);
    }

    // ── o canal inexistente ──────────────────────────────────────────────────

    private static JsonObject Eror(int codigo, string etapa = "falhou_socket") => new()
    {
        ["etapa"] = etapa, ["via"] = "socket", ["escolha"] = "usuario", ["abertos"] = 2, ["codigo"] = codigo, ["msg"] = "Channel not found.", ["ms"] = 1466,
    };

    private static async Task OCanalInexistenteAsync(Action<bool, string> checar)
    {
        var pedido = new SaidaConversa(1000000000349, "msg_pedido_obrigado", 1, "Obrigado pelo pedido!", "sdk", "reservada", Canal, Pedido, "4932");
        var saiu = pedido with { Id = 1000000000350, Etapa = "msg_pedido_saiu" };
        var resposta = new SaidaConversa(349, "pergunta_sabor", 1, "Qual sabor?", "sdk", "reservada", Canal, Pedido, "4932");
        checar(ConversaRaspadinha.CanalInexistente(pedido, "erro_envio", Eror(900500)) && ConversaRaspadinha.CanalInexistente(saiu, "erro_envio", Eror(900500)),
            "CI-1 EROR 900500 pelo socket numa mensagem do pedido (id >= 1e12, etapa msg_pedido_*) é canal inexistente");
        checar(!ConversaRaspadinha.CanalInexistente(resposta, "erro_envio", Eror(900500)), "CI-2 a mesma recusa numa resposta do robô (id curto, etapa pergunta_sabor) NÃO é: o canal existe, é outra coisa");
        checar(!ConversaRaspadinha.CanalInexistente(pedido, "erro_envio", Eror(900100)) && !ConversaRaspadinha.CanalInexistente(pedido, "erro_envio", Eror(900500, "falhou_sdk"))
               && !ConversaRaspadinha.CanalInexistente(pedido, "sdk_ausente", Eror(900500)) && !ConversaRaspadinha.CanalInexistente(pedido, "erro_envio", null),
            "CI-3 outro código, outra etapa, outro erro ou sem detalhe: não");
        checar(!ConversaRaspadinha.ContaComoFalha("falhou", "canal_inexistente") && ConversaRaspadinha.ContaComoFalha("falhou", "erro_envio")
               && ConversaRaspadinha.ContaComoFalha("falhou", "sdk_ausente") && !ConversaRaspadinha.ContaComoFalha("incerta", "erro_envio"),
            "CI-4 canal_inexistente não entra nas falhas seguidas do caixa (erro_envio e sdk_ausente entram)");

        // o laço: a primeira tentativa dá EROR 900500 pelo socket; o relato é definitivo e não há segunda tentativa
        var cena = new Cena { Saida = pedido };
        cena.Script = (_, _) => new ResultadoDoScript(false, "erro_envio", null, Eror(900500));
        await cena.Rodar();
        checar(cena.Enviados.Count == 1 && cena.Relatos.Count == 1 && cena.Relatos[0].Resultado == "falhou" && cena.Relatos[0].Erro == "canal_inexistente",
            $"CI-5 o laço relata 'falhou' com canal_inexistente na primeira recusa, sem nova tentativa (envios={cena.Enviados.Count}, relatos={cena.Relatos.Count}: {string.Join(",", cena.Relatos.Select(r => r.Resultado + "/" + r.Erro))})");
        checar(cena.Esperas == 0 && cena.Aquecimentos == 0, "CI-6 sem respiro nem aquecimento (o canal só existe depois que o chat é aberto)");
        checar(cena.Relatos[0].Detalhe is JsonObject d && d["codigo"]?.GetValue<int>() == 900500 && d["etapa"]?.GetValue<string>() == "falhou_socket",
            "CI-7 o detalhe vai ao ERP com o código 900500 e a etapa falhou_socket");
        checar(cena.Diags.Any(l => l.Contains("canal inexistente", StringComparison.Ordinal) && l.Contains("saida=1000000000349", StringComparison.Ordinal)),
            "CI-8 o diag diz que o canal do pedido não existe ainda e que não tenta de novo");

        // a mesma recusa numa resposta do robô: o laço de hoje (tenta de novo)
        var cena2 = new Cena { Saida = resposta };
        cena2.Script = (_, _) => new ResultadoDoScript(false, "erro_envio", null, Eror(900500));
        await cena2.Rodar();
        checar(cena2.Enviados.Count == 3 && cena2.Relatos.Count == 1 && cena2.Relatos[0].Erro == "erro_envio",
            $"CI-9 numa resposta do robô a recusa 900500 segue o laço de hoje: 3 tentativas e erro_envio (envios={cena2.Enviados.Count})");
    }

    /// <summary>O caixa falso do laço (o mesmo molde do plano B, sem o plano B).</summary>
    private sealed class Cena
    {
        public DateTime Agora = T0.AddSeconds(1);
        public readonly ConfirmacaoDeEnvio Confirmacoes = new();
        public readonly PortaoDeEnvio Portao = new(new MemoriaDeEco(), new LimiteDeEnvio());
        public readonly List<string> Enviados = new();
        public readonly List<(string Resultado, string? Erro, JsonObject? Detalhe)> Relatos = new();
        public readonly List<string> Diags = new();
        public int Aquecimentos, Esperas;
        public Func<int, bool, ResultadoDoScript> Script = (_, _) => new ResultadoDoScript(true, null, "991");
        public required SaidaConversa Saida;

        public Task Rodar()
        {
            var sinal = ConversaRaspadinha.LerSinal("{\"ok\":true,\"modo\":\"automatico\",\"palavra_modo\":\"desligado\",\"envio\":\"sdk\","
                + "\"comanda_onde\":\"kds\",\"merchant_ids\":[\"" + Loja + "\"],\"intervalo_s\":60,\"pausado\":null,\"avisos\":[],\"saidas\":[],\"comandas\":[]}");
            var ponte = new PonteDoChat
            {
                EnviarPeloSdk = p => { Enviados.Add(p.Texto); Agora += TimeSpan.FromSeconds(2); return Task.FromResult(Script(Enviados.Count, false)); },
                AbrirEColar = (_, _, _) => Task.FromResult(false),
                DiagSdk = (_, _) => Task.FromResult<JsonObject?>(null),
                Gestor = () => "logado",
                AquecerConversa = (_, _) => { Aquecimentos++; return Task.FromResult(true); },
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
                Diag = Diags.Add,
                Versao = "1.0.42",
            });
        }
    }

    private static void PeloFonte(Action<bool, string> checar)
    {
        var nucleo = Ler("Pdv.Nucleo/ConversaRaspadinha.cs");
        checar(!nucleo.Contains("nomes.Add(nome);") && nucleo.Contains("nomes.Add((JsonNode?)nome);"), "FO-1 os nomes do MESG entram no estado como JsonValue primitivo, nunca pelo Add(string) genérico");
        checar(nucleo.Contains("TypeInfoResolver = new DefaultJsonTypeInfoResolver()"), "FO-2 as opções de JSON do núcleo declaram o TypeInfoResolver (um nó customizado nunca mais derruba a serialização)");
        var servico = Ler("ServicoConversaChat.cs");
        var i = servico.IndexOf("private static async Task SinalAsync()", StringComparison.Ordinal);
        var corpo = i < 0 ? "" : servico[i..];
        var fim = corpo.IndexOf("\n    }\n", StringComparison.Ordinal);
        if (fim > 0) corpo = corpo[..fim];
        checar(corpo.Contains("ConversaRaspadinha.EstadoMinimo(") && corpo.Contains("sinal: a montagem do estado falhou"),
            "FO-3 a rodada manda o sinal mínimo quando a montagem do estado falha, com a linha de diag");
        checar(corpo.Contains("catch (Exception ex) { Diag(\"sinal: \" + ConversaRaspadinha.ResumoDaExcecao(ex) + \" em \" + (GuardaDoSinal.EmCurso?.Etapa ?? \"?\")); }"),
            "FO-4 a exceção da rodada sai no diag com a etapa, o tipo e a mensagem limpa (antes: só o tipo)");
        checar(servico.Contains("ConversaRaspadinha.CanalInexistente(s, vez.Erro, vez.Detalhe)") && servico.Contains("\"canal_inexistente\""),
            "FO-5 o laço do envio trata o canal inexistente");
        checar(servico.Contains("ConversaRaspadinha.ContaComoFalha(resultado, erro)"), "FO-6 as falhas seguidas do caixa passam pela regra pura");
        foreach (var arq in new[] { "Pdv.Testes/TestesSinalMorto.cs" })
        {
            var t = Ler(arq);
            checar(t.Length > 0 && !t.Contains((char)0x2014) && !t.Contains((char)0x2013), "FO-7 sem travessão: " + arq);
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
