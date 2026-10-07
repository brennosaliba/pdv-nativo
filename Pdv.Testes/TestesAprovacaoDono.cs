using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A APROVAÇÃO PELO WHATSAPP DO DONO, LADO DO CAIXA (1.0.20, desenho de 07/10/2026, seções 4.5,
/// 4.8 e 7.3).
///
/// O dono aprova pelo WhatsApp pessoal e a resposta sai sozinha no chat do iFood. O caixa:
///  · ignora a proposta que espera o dono (<c>como='dono'</c>), como o 1.0.19 já ignorava;
///  · manda a aprovada que o sinal entrega (<c>sdk</c>, <c>reservada</c>, <c>pelo_dono</c>) pelo MESMO
///    portão e com a MESMA confirmação pelo eco de hoje;
///  · nunca transforma a saída do dono em texto para colar, nem quando o envio falha, nem sem rede;
///  · com a aprovação valendo para todos (<c>aprovacao='dono'</c>) fica mudo para o chat: sem som de
///    resgate, sem som de mensagem, sem aviso, sem "Abrir e colar". A comanda continua saindo.
/// O serviço vivo (ServicoConversaChat) fala com a nuvem de verdade, então a regra mora em funções
/// puras do núcleo, exercitadas aqui contra o JSON do contrato; o fio do serviço é conferido pelo fonte.
/// </summary>
public static class TestesAprovacaoDono
{
    private const string Canal = TestesQuadroSendbird.Canal;
    private const string Pedido = TestesQuadroSendbird.Pedido;
    private const string Savassi = TestesQuadroSendbird.MerchantSavassi;
    private static readonly DateTime Agora = new(2026, 10, 7, 19, 40, 0);

    private const string TextoAprovado =
        "Oi, Ana! Recebemos o código da sua raspadinha. Seu prêmio vai junto com este pedido: 1 Donut Homer. Aproveite!";

    /// <summary>A saída aprovada como o sinal da 152 a entrega (seção 4.3: sdk, reservada, pelo_dono).</summary>
    private static string SaidaAprovada(long id = 9001, string texto = TextoAprovado, string? peloDono = "true")
        => "{\"id\":" + id + ",\"etapa\":\"confirmado\",\"ordem\":1,\"texto\":\"" + texto + "\",\"como\":\"sdk\","
           + "\"estado\":\"reservada\",\"canal\":\"" + Canal + "\",\"ifood_order_id\":\"" + Pedido + "\",\"pedido_numero\":\"4821\""
           + (peloDono is null ? "" : ",\"pelo_dono\":" + peloDono) + "}";

    /// <summary>O sinal do contrato (seção 4.5): aprovação, intervalo 30, avisos vazios com 'dono'.</summary>
    private static string SinalJson(string? aprovacao = "dono", string modo = "assistido", string envio = "sdk",
        string saidas = "[]", string avisos = "[]", int intervalo = 30, string? pausado = null, string? donoFora = null)
        => "{\"ok\":true,\"modo\":\"" + modo + "\",\"palavra_modo\":\"assistido\",\"envio\":\"" + envio + "\","
           + "\"comanda_onde\":\"ambos\",\"merchant_ids\":[\"" + Savassi + "\"],\"intervalo_s\":" + intervalo + ","
           + "\"pausado\":" + (pausado is null ? "null" : "\"" + pausado + "\"") + ","
           + "\"avisos\":" + avisos + ",\"saidas\":" + saidas + ",\"comandas\":[]"
           + (aprovacao is null ? "" : ",\"aprovacao\":\"" + aprovacao + "\"")
           + (donoFora is null ? "" : ",\"dono_fora\":" + donoFora) + "}";

    private static SaidaConversa S(string como, string estado, bool peloDono = false, long id = 9001, string texto = TextoAprovado)
        => new(id, "confirmado", 1, texto, como, estado, Canal, Pedido, "4821", peloDono);

    public static void Rodar(Action<bool, string> checar)
    {
        OSinal(checar);
        ADestino(checar);
        OPortao(checar);
        AFalha(checar);
        OEstado(checar);
        Fonte(checar);
    }

    // ── O SINAL E A SAÍDA ────────────────────────────────────────────────────
    private static void OSinal(Action<bool, string> checar)
    {
        var s = ConversaRaspadinha.LerSinal(SinalJson(saidas: "[" + SaidaAprovada() + "]"));
        checar(s.Legivel && s.Ativo && s.Modo == "assistido" && s.Aprovacao == "dono" && s.IntervaloS == 30 && s.Avisos.Count == 0,
            "AP-1 o sinal da aprovação é lido: modo assistido, aprovacao=dono, intervalo 30 e avisos vazios");
        var sd = s.Saidas.Single();
        checar(sd is { Id: 9001, Como: "sdk", Estado: "reservada", PeloDono: true, Canal: Canal, IfoodOrderId: Pedido, PedidoNumero: "4821" }
               && sd.Texto == TextoAprovado,
            "AP-2 a aprovada vem como sdk reservada com pelo_dono=true, inteira");
        checar(ConversaRaspadinha.LerSinal(SinalJson("prova")).Aprovacao == "prova"
               && ConversaRaspadinha.LerSinal(SinalJson(" PROVA ")).Aprovacao == "prova",
            "AP-3 aprovacao=prova é lida (sem diferença de maiúscula e espaço)");
        checar(ConversaRaspadinha.LerSinal(SinalJson("turbo")).Aprovacao == "nenhuma"
               && ConversaRaspadinha.LerSinal(SinalJson(null)).Aprovacao == "nenhuma"
               && ConversaRaspadinha.LerSinal(SinalJson(null)).Ativo,
            "AP-4 aprovacao desconhecida ou ausente (ERP sem a 152) é 'nenhuma', e o sinal continua valendo");
        checar(SinalConversa.Desligado.Aprovacao == "nenhuma" && !SinalConversa.Desligado.CaixaMudo,
            "AP-5 sinal ilegível ou ausente: aprovação nenhuma e caixa falando como hoje");
        var semCampo = ConversaRaspadinha.LerSinal(SinalJson(saidas: "[" + SaidaAprovada(peloDono: null) + "," + SaidaAprovada(9002, peloDono: "false") + "]"));
        checar(semCampo.Saidas.Count == 2 && semCampo.Saidas.All(x => !x.PeloDono),
            "AP-6 saída sem o campo pelo_dono (ou com false) não é do dono");

        // a resposta do chat_mensagens com a proposta: o 1.0.20 lê e não faz nada com ela
        var r = ConversaRaspadinha.LerResposta(200, """
            {"ok":true,"modo":"assistido","acao":"resgatou","saidas":[{"id":9100,"etapa":"confirmado","ordem":1,
             "texto":"Oi, Ana! Seu prêmio vai junto com este pedido.","como":"dono","estado":"aguardando_dono","pelo_dono":true,
             "canal":"sendbird_gc_cm_3f2a8c1e-1111-4a2b-9c3d-0123456789ab_ff493e25-f413-42e5-a46a-96c0b788813f",
             "ifood_order_id":"3f2a8c1e-1111-4a2b-9c3d-0123456789ab","pedido_numero":"4821"}],"aviso":null}
            """, Agora);
        var prop = r.Saidas.Single();
        checar(prop is { Como: "dono", Estado: "aguardando_dono", PeloDono: true } && r.Aviso is null,
            "AP-7 a proposta esperando o dono chega como como=dono, aguardando_dono, pelo_dono, sem aviso");

        var mudo = ConversaRaspadinha.LerSinal(SinalJson("dono"));
        checar(mudo.CaixaMudo, "AP-8 com aprovacao=dono o caixa fica mudo para o chat");
        checar(!ConversaRaspadinha.LerSinal(SinalJson("prova")).CaixaMudo && !ConversaRaspadinha.LerSinal(SinalJson("nenhuma")).CaixaMudo,
            "AP-9 com prova (só a lista de prova passa pelo dono) ou nenhuma, o caixa continua falando");
        checar(!ConversaRaspadinha.LerSinal(SinalJson("dono", modo: "desligado")).CaixaMudo,
            "AP-10 loja desligada não conta como mudo (não há chat novo)");

        checar(ConversaRaspadinha.IntervaloDoSinal(ConversaRaspadinha.LerSinal(SinalJson("dono", intervalo: 60))) == 30
               && ConversaRaspadinha.IntervaloDoSinal(ConversaRaspadinha.LerSinal(SinalJson("prova", intervalo: 240))) == 30
               && ConversaRaspadinha.IntervaloDoSinal(ConversaRaspadinha.LerSinal(SinalJson("dono", intervalo: 30))) == 30,
            "AP-11 com a aprovação ligada o sinal vai de 30 em 30 s, mesmo se o ERP mandar mais");
        checar(ConversaRaspadinha.IntervaloDoSinal(ConversaRaspadinha.LerSinal(SinalJson("nenhuma", intervalo: 60))) == 60,
            "AP-12 sem aprovação o intervalo é o do ERP, como hoje");

        // revisão 07/10: com o WhatsApp do dono fora (o sinal diz dono_fora), os pedidos de ajuda
        // voltam ao caixa; o caixa deixa de ficar mudo (a aprovação continua ligada no ERP)
        var fora = ConversaRaspadinha.LerSinal(SinalJson("dono", donoFora: "true",
            avisos: "[{\"tipo\":\"humano\",\"texto\":\"Chat do iFood: #4821 quer falar com alguém.\",\"pedido_numero\":\"4821\"}]"));
        checar(fora.DonoFora && !fora.CaixaMudo && fora.Aprovacao == "dono" && fora.Avisos.Count == 1,
            "AP-33 o sinal com dono_fora=true é lido, e com o WhatsApp do dono fora o caixa não fica mudo (o aviso chega)");
        checar(!ConversaRaspadinha.LerSinal(SinalJson("dono", donoFora: "false")).DonoFora
               && ConversaRaspadinha.LerSinal(SinalJson("dono", donoFora: "false")).CaixaMudo
               && ConversaRaspadinha.LerSinal(SinalJson("dono", donoFora: "\"sim\"")).CaixaMudo
               && !SinalConversa.Desligado.DonoFora,
            "AP-34 dono_fora falso, ausente ou torto: o caixa continua mudo com aprovacao=dono");
    }

    // ── O QUE O CAIXA FAZ COM CADA SAÍDA ─────────────────────────────────────
    private static void ADestino(Action<bool, string> checar)
    {
        var nenhuma = ConversaRaspadinha.LerSinal(SinalJson("nenhuma"));
        var prova = ConversaRaspadinha.LerSinal(SinalJson("prova"));
        var dono = ConversaRaspadinha.LerSinal(SinalJson("dono"));
        var todos = new[] { nenhuma, prova, dono };

        checar(todos.All(x => ConversaRaspadinha.Destino(S("dono", "aguardando_dono", peloDono: true), x) == DestinoDaSaida.Nada)
               && todos.All(x => ConversaRaspadinha.Destino(S("dono", "aguardando_dono"), x) == DestinoDaSaida.Nada)
               && todos.All(x => ConversaRaspadinha.Destino(S("dono", "aprovada", peloDono: true), x) == DestinoDaSaida.Nada),
            "AP-13 o 1.0.20 ignora como='dono' (aguardando ou aprovada): nem envio, nem texto para colar");
        checar(ConversaRaspadinha.Destino(S("sdk", "reservada", peloDono: true), dono) == DestinoDaSaida.Enviar
               && ConversaRaspadinha.Destino(S("sdk", "reservada", peloDono: true), prova) == DestinoDaSaida.Enviar,
            "AP-14 a aprovada entregue pelo sinal (sdk reservada, pelo_dono) vai para a fila de envio pelo SDK");
        checar(ConversaRaspadinha.Destino(S("sdk", "reservada"), nenhuma) == DestinoDaSaida.Enviar
               && ConversaRaspadinha.Destino(S("sdk", "reservada"), prova) == DestinoDaSaida.Enviar,
            "AP-15 sem a aprovação valendo para todos, a sdk reservada segue o caminho de hoje");
        checar(ConversaRaspadinha.Destino(S("sdk", "reservada"), dono) == DestinoDaSaida.Nada,
            "AP-16 com o caixa mudo, sdk reservada SEM pelo_dono não sai (seria texto ao cliente sem o OK do dono)");
        checar(ConversaRaspadinha.Destino(S("operador", "operador"), nenhuma) == DestinoDaSaida.Colar
               && ConversaRaspadinha.Destino(S("operador", "operador"), prova) == DestinoDaSaida.Colar,
            "AP-17 'operador' continua virando texto para colar (assistido de hoje e os clientes de fora da prova)");
        // revisão 07/10: com aprovacao=dono o ERP nunca cria 'operador'; quando cria, a aprovação foi
        // desligada (152d) depois do último sinal deste caixa, e a resposta só chega ao cliente se a
        // pessoa colar (o sinal seguinte não entrega 'operador' de novo)
        checar(ConversaRaspadinha.Destino(S("operador", "operador"), dono) == DestinoDaSaida.Colar,
            "AP-18 'operador' que o ERP mandou vira texto para colar mesmo com o último sinal dizendo dono (a aprovação foi desligada)");
        checar(todos.All(x => ConversaRaspadinha.Destino(S("operador", "operador", peloDono: true), x) == DestinoDaSaida.Nada)
               && todos.All(x => ConversaRaspadinha.Destino(S("sdk", "falhou", peloDono: true), x) == DestinoDaSaida.Nada),
            "AP-19 a saída do dono nunca vira texto para colar, em estado nenhum");
        checar(todos.All(x => ConversaRaspadinha.Destino(S("sombra", "sombra"), x) == DestinoDaSaida.Nada)
               && todos.All(x => ConversaRaspadinha.Destino(S("sdk", "enviada"), x) == DestinoDaSaida.Nada)
               && todos.All(x => ConversaRaspadinha.Destino(S("inventado", "x"), x) == DestinoDaSaida.Nada),
            "AP-20 sombra, sdk já enviada e valor desconhecido ficam quietos");
    }

    // ── O PORTÃO E A CONFIRMAÇÃO, OS DE HOJE ─────────────────────────────────
    private static void OPortao(Action<bool, string> checar)
    {
        PortaoDeEnvio Novo() => new(new MemoriaDeEco(), new LimiteDeEnvio());
        var dono = ConversaRaspadinha.LerSinal(SinalJson("dono", saidas: "[" + SaidaAprovada() + "]"));
        var aprovada = dono.Saidas.Single();

        var (perm, m) = Novo().Decidir(aprovada, dono, Agora.AddSeconds(-3), Agora.AddSeconds(-1), Agora);
        checar(perm is not null && m == MotivoPortao.Liberado && perm.Canal == Canal && perm.OrderUuid == Pedido && perm.Texto == TextoAprovado,
            "AP-21 a aprovada pelo dono passa pelo MESMO portão de hoje e é liberada (envio sdk, sinal novo)");
        checar(Novo().Decidir(S("dono", "aguardando_dono", peloDono: true), dono, Agora, Agora, Agora) is (null, MotivoPortao.NaoReservada),
            "AP-22 a proposta esperando o dono nunca chama o script (o portão segura)");
        checar(Novo().Decidir(aprovada, ConversaRaspadinha.LerSinal(SinalJson("dono", envio: "operador")), Agora, Agora, Agora)
                   is (null, MotivoPortao.EnvioNaoSdk)
               && Novo().Decidir(aprovada, ConversaRaspadinha.LerSinal(SinalJson("dono", envio: "nenhum")), Agora, Agora, Agora)
                   is (null, MotivoPortao.EnvioNaoSdk),
            "AP-23 o portão continua barrando o sinal com envio diferente de sdk, mesmo para a aprovada");
        checar(Novo().Decidir(aprovada, ConversaRaspadinha.LerSinal(SinalJson("dono", pausado: "canal_errado")), Agora, Agora, Agora)
                   is (null, MotivoPortao.Pausado)
               && Novo().Decidir(aprovada, dono, Agora.AddMinutes(-6), Agora, Agora) is (null, MotivoPortao.SinalVelho)
               && Novo().Decidir(aprovada, dono, Agora, Agora.AddSeconds(-55), Agora) is (null, MotivoPortao.ReservaVencida),
            "AP-24 loja pausada, sinal velho e reserva vencida seguram a aprovada como seguram as outras");
        var corrigida = S("sdk", "reservada", peloDono: true, texto: "Oi " + (char)0x2014 + " Ana, seu prêmio vai junto");
        checar(Novo().Decidir(corrigida, dono, Agora, Agora, Agora) is (null, MotivoPortao.TextoInvalido),
            "AP-25 travessão no texto do dono não sai (o portão de hoje vale para a correção também)");

        // a correção do dono confirma pelo eco do quadro, com o texto novo
        var c = new ConfirmacaoDeEnvio();
        const string Corrigido = "Oi, Ana! O seu donut já vai junto com o pedido 4821.";
        c.Aguardar(9003, Canal, Corrigido, Agora);
        var volta = QuadroSendbird.Ler(TestesQuadroSendbird.Mesg(Corrigido, autor: "loja-1", msgId: 77881));
        checar(c.Ver(volta, enviado: false) is (9003, "77881"),
            "AP-26 a confirmação pelo eco de hoje vale para a aprovada (texto corrigido pelo dono incluído)");
    }

    // ── A FALHA NÃO VOLTA PARA A PESSOA ──────────────────────────────────────
    private static void AFalha(Action<bool, string> checar)
    {
        var nenhuma = ConversaRaspadinha.LerSinal(SinalJson("nenhuma"));
        var prova = ConversaRaspadinha.LerSinal(SinalJson("prova"));
        var dono = ConversaRaspadinha.LerSinal(SinalJson("dono"));
        checar(!ConversaRaspadinha.FalhaVaiParaPessoa(true, nenhuma) && !ConversaRaspadinha.FalhaVaiParaPessoa(true, prova)
               && !ConversaRaspadinha.FalhaVaiParaPessoa(true, dono),
            "AP-27 a saída do dono que falhou (sem rede ou com reserva do ERP) não vira texto para colar");
        checar(!ConversaRaspadinha.FalhaVaiParaPessoa(false, dono),
            "AP-28 com o caixa mudo nenhuma falha vira texto para colar");
        checar(ConversaRaspadinha.FalhaVaiParaPessoa(false, nenhuma) && ConversaRaspadinha.FalhaVaiParaPessoa(false, prova),
            "AP-29 fora disso, a falha de uma saída comum continua indo para a pessoa, como hoje");
        var rr = ConversaRaspadinha.LerSaidaResultado(200,
            """{"ok":true,"estado":"falhou","reserva":{"como":"operador","texto":"Oi, Ana!","pedido_numero":"4821"}}""");
        checar(rr.Reserva is not null && !ConversaRaspadinha.FalhaVaiParaPessoa(true, prova),
            "AP-30 mesmo que o ERP devolva uma reserva para a saída do dono, o caixa não mostra o texto");
        var fora = ConversaRaspadinha.LerSinal(SinalJson("dono", donoFora: "true"));
        checar(!ConversaRaspadinha.FalhaVaiParaPessoa(true, fora) && ConversaRaspadinha.FalhaVaiParaPessoa(false, fora),
            "AP-35 com o WhatsApp do dono fora, a saída do dono continua sem texto para colar; a comum volta para a pessoa");
    }

    // ── O ESTADO DO SINAL (as contagens novas do script) ─────────────────────
    private static void OEstado(Action<bool, string> checar)
    {
        var sdk = new JsonObject
        {
            ["achou"] = true, ["user_id_igual_ws"] = true, ["instancias"] = 1, ["via_webpack"] = true, ["via_react"] = false,
            ["modulos"] = 2, ["runtimes"] = 3, ["uid"] = "segredo-da-loja",
        };
        var e = JsonNode.Parse(ConversaRaspadinha.EstadoDoSinal("logado", true, Agora, 0, null, sdk, null))!["sdk"]!;
        checar((bool?)e["via_webpack"] == true && (bool?)e["via_react"] == false && (int?)e["modulos"] == 2 && (int?)e["runtimes"] == 3,
            "AP-31 o estado leva por onde o SDK foi achado e as contagens do webpack (para o teste na loja)");
        var sujo = new JsonObject { ["via_webpack"] = "sim", ["modulos"] = "dois", ["runtimes"] = 99999999 };
        var e2 = JsonNode.Parse(ConversaRaspadinha.EstadoDoSinal("logado", true, Agora, 0, null, sujo, null))!["sdk"]!.AsObject();
        checar(!e2.ContainsKey("via_webpack") && !e2.ContainsKey("modulos") && (int?)e2["runtimes"] == 100_000
               && !e.ToJsonString().Contains("segredo"),
            "AP-32 e só bool e número, com teto; texto da página não passa");
    }

    // ── O FIO DO SERVIÇO (pelo fonte) ────────────────────────────────────────
    private static void Fonte(Action<bool, string> checar)
    {
        var raiz = Raiz();
        string Ler(params string[] partes)
        {
            if (raiz is null) return "";
            var p = Path.Combine(new[] { raiz }.Concat(partes).ToArray());
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }
        var servico = Ler("ServicoConversaChat.cs");
        checar(servico.Length > 0, "AF-0 o serviço do chat existe");

        checar(Regex.Matches(servico, @"Alerta\.Resgate\(\)").Count == 1
               && servico.Contains("if (!r.Repetida && r.Acao == \"resgatou\" && !_ultimo.Sinal.CaixaMudo) Alerta.Resgate();", StringComparison.Ordinal),
            "AF-1 com Aprovacao == dono o serviço não toca Alerta.Resgate() (uma chamada só, com a guarda)");

        var iEmitir = servico.IndexOf("private static void Emitir(AvisoDoChat a, bool doErp = false)", StringComparison.Ordinal);
        var iMudo = iEmitir < 0 ? -1 : servico.IndexOf("if (_ultimo.Sinal.CaixaMudo && !doErp) {", iEmitir, StringComparison.Ordinal);
        var iSom = iEmitir < 0 ? -1 : servico.IndexOf("Alerta.MensagemChat();", iEmitir, StringComparison.Ordinal);
        var iAvisou = iEmitir < 0 ? -1 : servico.IndexOf("Avisou?.Invoke(a)", iEmitir, StringComparison.Ordinal);
        checar(iEmitir > 0 && iMudo > iEmitir && iSom > iMudo && iAvisou > iMudo
               && Regex.Matches(servico, @"Alerta\.MensagemChat\(\)").Count == 1
               && Regex.Matches(servico, @"Avisou\?\.Invoke\(").Count == 1,
            "AF-2 com o caixa mudo, Emitir sai antes do som de mensagem e do aviso na tela (e é o único caminho dos dois)");

        var iTratar = servico.IndexOf("private static bool TratarSaida(", StringComparison.Ordinal);
        var tratar = iTratar < 0 ? "" : servico[iTratar..Math.Min(servico.Length, iTratar + 1400)];
        checar(tratar.Contains("switch (ConversaRaspadinha.Destino(s, _ultimo.Sinal))", StringComparison.Ordinal)
               && !Regex.IsMatch(tratar, @"s\.Como == ConversaRaspadinha\.ComoOperador\)\s*\{"),
            "AF-3 o serviço decide cada saída pela regra pura do núcleo (Destino), sem regra própria");
        checar(Regex.IsMatch(tratar, @"case DestinoDaSaida\.Enviar:\s*if \(s\.PeloDono\) LembrarDoDono\(s\.Id, agora\);\s*FilaDeEnvio\.Writer\.TryWrite"),
            "AF-4 a aprovada vai para a MESMA fila de envio (portão e eco de hoje), lembrada como do dono");
        checar(Regex.Matches(servico, @"FilaDeEnvio\.Writer\.TryWrite\(").Count == 1,
            "AF-5 e essa é a única porta da fila de envio");

        var iRel = servico.IndexOf("private static async Task RelatarAsync(", StringComparison.Ordinal);
        var iRelFim = iRel < 0 ? -1 : servico.IndexOf("// ── 5. O SINAL", iRel, StringComparison.Ordinal);
        var rel = iRel > 0 && iRelFim > iRel ? servico[iRel..iRelFim] : "";
        checar(rel.Contains("var doDono = s?.PeloDono == true || EraDoDono(saidaId);", StringComparison.Ordinal)
               && Regex.Matches(rel, @"ConversaRaspadinha\.FalhaVaiParaPessoa\(doDono, _ultimo\.Sinal\)").Count == 2,
            "AF-6 RelatarAsync passa a reserva do ERP e o aviso sem rede pela mesma regra (saída do dono e caixa mudo)");
        checar(Regex.IsMatch(rel, @"if \(resultado == ""falhou"" && s is not null && ConversaRaspadinha\.FalhaVaiParaPessoa\(doDono, _ultimo\.Sinal\)\)\s*Emitir\(new AvisoDoChat\(""falha_envio"""),
            "AF-7 sem rede, a saída PeloDono que falhou NÃO vira aviso local com texto para colar");
        checar(Regex.Matches(rel, @"Emitir\(").Count == 2,
            "AF-8 RelatarAsync não tem outro caminho para a tela");

        checar(servico.Contains("proximo = TimeSpan.FromSeconds(ConversaRaspadinha.IntervaloDoSinal(s));", StringComparison.Ordinal),
            "AF-9 o próximo sinal usa o intervalo da aprovação (30 s com ela ligada)");
        checar(servico.Contains("var doDono = r.Saidas.Any(s => s.PeloDono || s.Como == ConversaRaspadinha.ComoDono);", StringComparison.Ordinal)
               && servico.Contains("!(a.Tipo == \"mandar\" && (mandou || doDono))", StringComparison.Ordinal),
            "AF-10 a proposta do dono não vira 'mande esta resposta' na tela");
        checar(servico.Contains("if (r.ImprimirAqui) _ = ServicoRaspadinhaChat.ImprimirPendentesAsync();", StringComparison.Ordinal),
            "AF-11 a comanda de resgate continua saindo com o caixa mudo (só o som e o aviso calam)");

        // revisão 07/10: o que o ERP manda na resposta da fala passa pela trava do mudo (com
        // aprovacao=dono o ERP não manda aviso nem 'operador'; quando manda, é porque a aprovação foi
        // desligada depois do último sinal, ou porque o WhatsApp do dono está fora). Os avisos do
        // sinal continuam pela trava: o próprio sinal diz dono_fora.
        checar(Regex.IsMatch(servico, @"Emitir\(new AvisoDoChat\(a\.Tipo, a\.Texto, null, a\.IfoodOrderId, a\.PedidoNumero\), doErp: true\);")
               && Regex.IsMatch(tratar, @"case DestinoDaSaida\.Colar:\s*Emitir\(new AvisoDoChat\(""mandar"",[^;]*s\.IfoodOrderId, s\.PedidoNumero\), doErp: true\);")
               && servico.Contains("foreach (var a in s.Avisos) Emitir(new AvisoDoChat(a.Tipo, a.Texto, null, a.IfoodOrderId, a.PedidoNumero));", StringComparison.Ordinal)
               && Regex.Matches(servico, @"doErp: true").Count == 2,
            "AF-16 o aviso e o 'operador' que vêm na resposta do ERP aparecem mesmo com o sinal antigo dizendo dono; os do sinal seguem a trava");

        // o envio pelo SDK é o de hoje: nada mudou em enviar(), no token ou no EnviarPeloSdkAsync
        var tela = Ler("Telas", "ChatIfood.xaml.cs");
        var js = TestesScriptSendbird.ExtrairRaw(tela, "ScriptSendbird") ?? "";
        var iEnviar = js.IndexOf("function enviar(a){", StringComparison.Ordinal);
        var enviar = iEnviar < 0 ? "" : js[iEnviar..];
        checar(enviar.Contains("var esperado = window.__pdvEnvioToken;", StringComparison.Ordinal)
               && enviar.Contains("if (enviados.length >= 20)", StringComparison.Ordinal)
               && enviar.Contains("md.orderUuid !== a.orderUuid", StringComparison.Ordinal)
               && enviar.Contains("if (ch.isFrozen)", StringComparison.Ordinal),
            "AF-12 o enviar() da página é o de hoje (token, 20 por minuto, orderUuid, congelada)");
        checar(!js.Contains("!req.c", StringComparison.Ordinal) && !js.Contains("= req.c", StringComparison.Ordinal)
               && js.Contains("reqs[nome].m", StringComparison.Ordinal)
               && js.Contains("achados.push(", StringComparison.Ordinal) && js.Contains("shadowRoot", StringComparison.Ordinal),
            "AF-13 o script procura pelas fábricas (req.m), guarda o achado e desce em shadowRoot; não depende mais de req.c");

        var csproj = Ler("Pdv.csproj");
        checar(csproj.Contains("<Version>1.0.20</Version>", StringComparison.Ordinal), "AF-14 a versão do caixa é 1.0.20");
        var testes = new[] { Ler("Pdv.Testes", "TestesAprovacaoDono.cs"), Ler("Pdv.Testes", "TestesScriptSendbird.cs") };
        var novos = new[] { servico, js }.Concat(testes).ToArray();
        checar(testes.All(t => t.Length > 0) && novos.All(t => !t.Contains((char)0x2014) && !t.Contains((char)0x2013)),
            "AF-15 nada com travessão no serviço, no script e nos testes desta entrega");
    }

    private static string? Raiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj"))) return dir.FullName;
        return null;
    }
}
