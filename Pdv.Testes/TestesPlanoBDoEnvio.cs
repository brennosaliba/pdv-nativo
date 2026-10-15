using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// 1.0.35 (14/10/2026, caso #8039): O PLANO B DO ENVIO PELO SDK.
///
/// Na Savassi, de 10/10 a 14/10, o SDK do Sendbird recusou 41 de 42 respostas do robô sem nenhum quadro
/// sair da página (erro_envio, sem sendbird_msg_id), e a única que saiu foi 70 min depois de o caixa
/// reiniciar. O plano B: na primeira recusa do SDK (falhou_sdk ou promessa, sem quadro saindo), a tela
/// recarrega o Gestor ESCONDIDO e o caixa tenta UMA vez mais, com a mesma permissão e dentro do prazo da
/// reserva. Aqui o laço de verdade (<see cref="ServicoConversaChat.EnviarComTentativasAsync"/>) roda com
/// portas falsas: script, recarga, aquecimento, relógio e espera de mentira. Prova que:
///  · 3 recusas e sucesso depois de recarregar (as duas primeiras do SDK frio, a terceira do SDK);
///  · uma recusa, recarga e sucesso na segunda;
///  · com o Gestor na frente do operador a tela não recarrega e o laço de hoje segue;
///  · reserva vencida durante a recarga vira "falhou" com sem_tempo (nunca incerta, nunca envio);
///  · o quadro que passa durante a recarga (o SDK reenviando sozinho) impede a mensagem dobrada;
///  · canal_errado, congelada e quadro visto saindo nunca recarregam;
///  · o teto da recarga (1 a cada 10 min, 3 por hora) e a decisão da tela;
///  · (revisão) o SDK frio depois da recarga aquece e tenta de novo, e nunca vira canal_errado;
///  · (revisão) a reserva vencida no meio do laço, ou sem tempo para outra tentativa, vira "falhou", e a
///    saída que esperava na fila e perdeu a reserva também (nunca "incerta": nada saiu).
/// </summary>
public static class TestesPlanoBDoEnvio
{
    private const string Canal = TestesQuadroSendbird.Canal;
    private const string Pedido = TestesQuadroSendbird.Pedido;
    private const string Savassi = TestesQuadroSendbird.MerchantSavassi;
    private static readonly DateTime T0 = new(2026, 10, 14, 19, 50, 47);
    private const string Texto = "Oi, Heitor! Recebemos o código da sua raspadinha. Seu prêmio é 1 Cookie Clássico. Qual sabor você quer?";

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        ADecisaoDaTela(checar);
        OTeto(checar);
        await TresRecusasESucessoDepoisDeRecarregar(checar);
        await UmaRecusaRecargaESucesso(checar);
        await GestorNaFrente(checar);
        await ReservaVencidaNaRecarga(checar);
        await OQuadroDuranteARecarga(checar);
        await OQueNuncaRecarrega(checar);
        await AReservaNoLaco(checar);
        OSinal(checar);
        PeloFonte(checar);
    }

    // ── o sinal: a conexão do SDK e o bloco do envio, só enums e inteiros ────
    private static void OSinal(Action<bool, string> checar)
    {
        var sdk = JsonNode.Parse("""{"achou":true,"conexao":"OPEN","uid":"loja-secreta"}""")!.AsObject();
        var e = JsonNode.Parse(ConversaRaspadinha.EstadoDoSinal("logado", true, T0, 0, null, sdk, null, null,
            new ConversaRaspadinha.ContagemDoEnvio(9, 2), new ConversaRaspadinha.ContagemDeCartoes(3, 2, 2)))!.AsObject();
        checar((string?)e["sdk"]!["conexao"] == "OPEN" && (int?)e["envio"]!["falhas_seguidas"] == 9 && (int?)e["envio"]!["recargas_hora"] == 2
               && (int?)e["solicitacao"]!["cartoes"] == 3 && (int?)e["solicitacao"]!["com_data"] == 2 && (int?)e["solicitacao"]!["botoes_max"] == 2
               && !e.ToJsonString().Contains("loja-secreta"),
            $"PB-33 o sinal leva sdk.conexao, envio (falhas seguidas, recargas na hora) e solicitacao (so contagens) ({e.ToJsonString()})");
        var sujo = JsonNode.Parse("""{"achou":true,"conexao":"OPEN; DROP"}""")!.AsObject();
        var e2 = JsonNode.Parse(ConversaRaspadinha.EstadoDoSinal("logado", true, T0, 0, null, sujo, null))!.AsObject();
        checar(e2["sdk"] is JsonObject s2 && !s2.ContainsKey("conexao") && !e2.ContainsKey("envio") && !e2.ContainsKey("solicitacao"),
            "PB-34 conexao fora do enum cai; sem contagem, o sinal e o de hoje (sem envio nem solicitacao)");
    }

    // ── o cenário: um caixa falso ────────────────────────────────────────────
    private sealed class Cena
    {
        public DateTime Agora = T0.AddSeconds(1);
        public readonly ConfirmacaoDeEnvio Confirmacoes = new();
        public readonly PortaoDeEnvio Portao = new(new MemoriaDeEco(), new LimiteDeEnvio());
        public readonly List<string> Enviados = new();
        public readonly List<(string Resultado, string? Erro, JsonObject? Detalhe)> Relatos = new();
        public readonly List<string> Diags = new();
        public int Recargas, Aquecimentos, RecargasContadas;
        public bool Recarregou;
        /// <summary>O que o script responde em cada envio (1, 2, 3...).</summary>
        public Func<int, bool, ResultadoDoScript> Script = (_, _) => new ResultadoDoScript(true, null, "991");
        /// <summary>O que a tela responde ao plano B.</summary>
        public Func<Cena, string> Recarga = c => PlanoBDoEnvio.Recarregou;
        public TimeSpan TempoDaRecarga = TimeSpan.FromSeconds(18);
        /// <summary>A tela nunca responde ao plano B (o laço desiste pelo próprio prazo).</summary>
        public bool RecargaNuncaVolta;
        public TimeSpan PrazoDaRecarga = PlanoBDoEnvio.PrazoDaRecarga;
        /// <summary>Quanto o aquecimento do SDK leva (abrir a conversa no Gestor escondido).</summary>
        public TimeSpan TempoDoAquecimento = TimeSpan.Zero;

        public SaidaConversa Saida = new(84, "pergunta_sabor", 1, Texto, "sdk", "reservada", Canal, Pedido, "8039");

        public LacoDeEnvio Laco(bool comPlanoB = true)
        {
            var sinal = ConversaRaspadinha.LerSinal("{\"ok\":true,\"modo\":\"automatico\",\"palavra_modo\":\"desligado\",\"envio\":\"sdk\","
                + "\"comanda_onde\":\"kds\",\"merchant_ids\":[\"" + Savassi + "\"],\"intervalo_s\":60,\"pausado\":null,"
                + "\"avisos\":[],\"saidas\":[],\"comandas\":[]}");
            var ponte = new PonteDoChat
            {
                EnviarPeloSdk = p =>
                {
                    Enviados.Add(p.Texto);
                    // o tempo de cada tentativa no script (o SDK que recusa responde rápido)
                    Agora += TimeSpan.FromSeconds(2);
                    return Task.FromResult(Script(Enviados.Count, Recarregou));
                },
                AbrirEColar = (_, _, _) => Task.FromResult(false),
                DiagSdk = (_, _) => Task.FromResult<JsonObject?>(null),
                Gestor = () => "logado",
                AquecerConversa = (_, _) => { Aquecimentos++; Agora += TempoDoAquecimento; return Task.FromResult(true); },
                RecarregarParaEnvio = comPlanoB ? (_, _) =>
                {
                    Recargas++;
                    var r = Recarga(this);
                    Agora += TempoDaRecarga;
                    if (RecargaNuncaVolta) return new TaskCompletionSource<string>().Task;
                    if (r == PlanoBDoEnvio.Recarregou) Recarregou = true;
                    return Task.FromResult(r);
                } : null,
            };
            return new LacoDeEnvio
            {
                Portao = Portao,
                Confirmacoes = Confirmacoes,
                Sinal = () => (sinal, T0),
                Ponte = () => ponte,
                Relatar = (id, resultado, erro, _, detalhe) => { Relatos.Add((resultado, erro, detalhe)); return Task.CompletedTask; },
                Esperar = t => { Agora += t; return Task.CompletedTask; },
                Agora = () => Agora,
                Diag = Diags.Add,
                Versao = "1.0.36",
                Recarregou = () => RecargasContadas++,
                PrazoDaRecarga = PrazoDaRecarga,
            };
        }

        public Task Rodar(bool comPlanoB = true) => ServicoConversaChat.EnviarComTentativasAsync(Saida, T0, Laco(comPlanoB));
        public Task Rodar(SaidaConversa s, DateTime recebida) => ServicoConversaChat.EnviarComTentativasAsync(s, recebida, Laco());
    }

    private static ResultadoDoScript Recusa(string etapa = "falhou_sdk", int codigo = 900100, string erro = "erro_envio")
        => new(false, erro, null, new JsonObject
        {
            ["etapa"] = etapa, ["codigo"] = codigo, ["conexao"] = "OPEN", ["instancia"] = 0, ["instancias"] = 2, ["ms"] = 410,
            ["msg"] = "Not a member of the channel.",
        });

    private static ResultadoDoScript Frio() => new(false, "canal_errado", null, new JsonObject { ["etapa"] = "canal_errado", ["instancias"] = 2 });

    private static MensagemSendbird Quadro(long? msgId)
        => QuadroSendbird.Ler(TestesQuadroSendbird.Mesg(Texto, "loja-1", "MERCHANT", msgId,
            new DateTimeOffset(T0).ToUnixTimeMilliseconds(), Canal))!;

    private static int? Int(JsonObject? o, string k) => o?[k] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    private static string? Str(JsonObject? o, string k) => o?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ── a decisão da tela e o teto ───────────────────────────────────────────
    private static void ADecisaoDaTela(Action<bool, string> checar)
    {
        checar(PlanoBDoEnvio.Impedimento(true, false, false, false) is null, "PB-1 Gestor pronto, escondido, sem colagem e logado: pode recarregar");
        checar(PlanoBDoEnvio.Impedimento(true, true, false, false) == "na_frente",
            "PB-2 com a camada do chat na frente do operador a tela NAO recarrega (na_frente)");
        checar(PlanoBDoEnvio.Impedimento(true, false, true, false) == "na_frente", "PB-3 com um Abrir e colar em curso tambem nao (na_frente)");
        checar(PlanoBDoEnvio.Impedimento(true, false, false, true) == "login" && PlanoBDoEnvio.Impedimento(false, true, true, true) == "sem_sdk",
            "PB-4 o Gestor pedindo login: login; sem o Gestor pronto: sem_sdk (nessa ordem)");
        checar(PlanoBDoEnvio.Cabe("erro_envio", Recusa().Detalhe) && PlanoBDoEnvio.Cabe("erro_envio", Recusa("promessa").Detalhe)
               && !PlanoBDoEnvio.Cabe("erro_envio", Recusa("excecao").Detalhe) && !PlanoBDoEnvio.Cabe("erro_envio", null)
               && !PlanoBDoEnvio.Cabe("canal_errado", Frio().Detalhe) && !PlanoBDoEnvio.Cabe("erro_envio", Recusa("token", erro: "token").Detalhe),
            "PB-5 so a recusa do SDK (falhou_sdk ou promessa) pede conexao nova; canal_errado, token e excecao nao");
        var ok = JsonNode.Parse("""{"achou":true,"conexao":"OPEN"}""")!.AsObject();
        var fechado = JsonNode.Parse("""{"achou":true,"conexao":"CLOSED"}""")!.AsObject();
        var login = JsonNode.Parse("""{"achou":false,"login":true}""")!.AsObject();
        checar(PlanoBDoEnvio.SdkAberto(ok) && !PlanoBDoEnvio.SdkAberto(fechado) && !PlanoBDoEnvio.SdkAberto(null) && PlanoBDoEnvio.PedeLogin(login),
            "PB-6 depois da recarga so serve o SDK achado e OPEN; a tela de login e reconhecida");
        checar(PlanoBDoEnvio.Normalizar("recarregou") == "recarregou" && PlanoBDoEnvio.Normalizar("qualquer") == "erro" && PlanoBDoEnvio.Normalizar(null) == "erro"
               && PlanoBDoEnvio.ParaODetalhe("erro") == "sem_sdk" && ConversaRaspadinha.PlanosB.Contains(PlanoBDoEnvio.ParaODetalhe("teto")),
            "PB-7 o que a tela devolve fora do contrato vira erro (no detalhe ao ERP, sem_sdk)");
    }

    private static void OTeto(Action<bool, string> checar)
    {
        var t = new TetoDaRecargaDoEnvio();
        var u = new DateTime(2026, 10, 14, 22, 0, 0, DateTimeKind.Utc);
        var r = new[]
        {
            t.Permitir(u), t.Permitir(u.AddMinutes(5)), t.Permitir(u.AddMinutes(10)), t.Permitir(u.AddMinutes(20)),
            t.Permitir(u.AddMinutes(30)), t.Permitir(u.AddMinutes(45)), t.Permitir(u.AddMinutes(62)),
        };
        checar(r.SequenceEqual(new[] { true, false, true, true, false, false, true }),
            $"PB-8 o teto da recarga: 1 a cada 10 min e 3 por hora ({string.Join(",", r)})");
        checar(TetoDaRecargaDoEnvio.Intervalo == TimeSpan.FromMinutes(10) && TetoDaRecargaDoEnvio.PorHora == 3
               && PlanoBDoEnvio.PrazoDaRecarga == TimeSpan.FromSeconds(40) && PlanoBDoEnvio.EsperaDoSdk == TimeSpan.FromSeconds(25)
               && PlanoBDoEnvio.GuardaDaRecarga > PortaoDeEnvio.PrazoDaReserva && PlanoBDoEnvio.GuardaDaRecarga > PlanoBDoEnvio.PrazoDaRecarga,
            "PB-9 os prazos: a tela espera o SDK 25 s, o laco espera a tela 40 s, e a guarda dura mais que a reserva");
    }

    // ── os cenários do laço ──────────────────────────────────────────────────
    private static async Task TresRecusasESucessoDepoisDeRecarregar(Action<bool, string> checar)
    {
        var c = new Cena();
        // 1 e 2: o SDK frio (canal_errado); 3: o SDK recusa; depois da recarga, sai
        c.Script = (n, recarregou) => recarregou ? new ResultadoDoScript(true, null, "991") : n <= 2 ? Frio() : Recusa();
        await c.Rodar();
        checar(c.Enviados.Count == 4 && c.Recargas == 1 && c.Aquecimentos == 2 && c.Relatos.Count == 0 && c.RecargasContadas == 1,
            $"PB-10 3 recusas e sucesso depois de recarregar: 4 envios, 1 recarga, nenhum relato de falha ({c.Enviados.Count} envios, {c.Recargas} recargas, {c.Relatos.Count} relatos)");
        checar(c.Confirmacoes.Pendente(84) && c.Confirmacoes.Ver(Quadro(8812736123), enviado: false) is (84, "8812736123"),
            "PB-11 e quem confirma e o quadro de volta, como sempre (a saida 84 espera o eco com o msg_id)");
        checar(c.Enviados.All(t => t == Texto), "PB-12 a mesma resposta, palavra por palavra (nao e mensagem nova ao cliente)");
        checar(c.Diags.Any(d => d.Contains("plano b=recarregou em 18 s, tentativa 4: ok", StringComparison.Ordinal)),
            $"PB-13 o rastro diz o plano B, o tempo da recarga e a tentativa ({c.Diags.LastOrDefault()})");
    }

    private static async Task UmaRecusaRecargaESucesso(Action<bool, string> checar)
    {
        var c = new Cena();
        c.Script = (n, recarregou) => recarregou ? new ResultadoDoScript(true, null, "991") : Recusa("promessa");
        await c.Rodar();
        checar(c.Enviados.Count == 2 && c.Recargas == 1 && c.Aquecimentos == 0 && c.Relatos.Count == 0,
            "PB-14 a primeira recusa do SDK ja recarrega (sobra tempo na reserva): 2 envios, sem aquecer, sem relato de falha");

        // a recarga deu, mas o SDK recusa de novo: falhou, com o detalhe, sem mais tentativas
        var d = new Cena();
        d.Script = (_, _) => Recusa();
        await d.Rodar();
        var det = d.Relatos.SingleOrDefault().Detalhe;
        checar(d.Enviados.Count == 2 && d.Recargas == 1 && d.Relatos.Count == 1 && d.Relatos[0] is ("falhou", "erro_envio", _)
               && Str(det, "plano_b") == "recarregou" && Int(det, "tentativa") == 2 && Int(det, "tentativas") == 2
               && Str(det, "etapa") == "falhou_sdk" && Int(det, "codigo") == 900100 && Str(det, "versao") == "1.0.36",
            $"PB-15 recarregou e o SDK recusou de novo: falhou com o detalhe (plano_b recarregou, 2 tentativas), sem 3a tentativa ({det?.ToJsonString()})");
    }

    private static async Task GestorNaFrente(Action<bool, string> checar)
    {
        var c = new Cena();
        var naFrente = true;   // a camada do chat esta na frente do operador
        c.Recarga = _ => PlanoBDoEnvio.Impedimento(true, naFrente, false, false) ?? PlanoBDoEnvio.Recarregou;
        c.TempoDaRecarga = TimeSpan.Zero;
        c.Script = (_, _) => Recusa();
        await c.Rodar();
        var det = c.Relatos.SingleOrDefault().Detalhe;
        checar(!c.Recarregou && c.Recargas == 1 && c.Enviados.Count == 3 && c.Aquecimentos == 2,
            $"PB-16 Gestor na frente: a tela nao recarrega e o laco de hoje segue (3 tentativas, aquece 2 vezes) ({c.Enviados.Count} envios)");
        checar(c.Relatos.Count == 1 && c.Relatos[0] is ("falhou", "erro_envio", _) && Str(det, "plano_b") == "na_frente"
               && det?["codigos"] is JsonArray ca && ca.Count == 3 && ca.All(x => x is JsonValue v && v.GetValue<int>() == 900100)
               && det?["aqueceu"] is JsonValue aq && aq.GetValue<bool>() && Int(det, "tentativas") == 3,
            $"PB-17 e o relato leva plano_b na_frente, os 3 codigos do Sendbird e que aqueceu ({det?.ToJsonString()})");
        checar(c.RecargasContadas == 0 && !c.Confirmacoes.Pendente(84), "PB-18 sem recarga nao conta recarga no sinal, e a guarda sai");
    }

    private static async Task ReservaVencidaNaRecarga(Action<bool, string> checar)
    {
        var c = new Cena();
        c.Script = (n, recarregou) => recarregou ? new ResultadoDoScript(true, null, "991") : Recusa();
        c.TempoDaRecarga = TimeSpan.FromSeconds(55);   // a recarga passou dos 50 s da reserva
        await c.Rodar();
        var det = c.Relatos.SingleOrDefault().Detalhe;
        checar(c.Enviados.Count == 1 && c.Relatos.Count == 1 && c.Relatos[0] is ("falhou", "erro_envio", _) && Str(det, "plano_b") == "sem_tempo",
            $"PB-19 reserva vencida durante a recarga: falhou com plano_b sem_tempo, sem mandar depois da reserva ({c.Enviados.Count} envios, {det?.ToJsonString()})");
        checar(!c.Relatos.Any(r => r.Resultado == "incerta") && !c.Confirmacoes.Pendente(84),
            "PB-20 nunca incerta: a mensagem com certeza nao saiu e precisa ir para a pessoa");
    }

    private static async Task OQuadroDuranteARecarga(Action<bool, string> checar)
    {
        // o SDK reenviou sozinho ao reconectar e o eco voltou com o msg_id: nada sai de novo
        var c = new Cena();
        c.Script = (_, recarregou) => recarregou ? new ResultadoDoScript(true, null, "991") : Recusa();
        c.Recarga = x => { x.Confirmacoes.Ver(Quadro(777), enviado: false); return PlanoBDoEnvio.Recarregou; };
        await c.Rodar();
        checar(c.Enviados.Count == 1 && c.Relatos.Count == 0,
            "PB-21 o eco desta resposta chegou durante a recarga: ela nao sai de novo (vale o quadro)");

        // so o quadro saindo, sem a volta: duvida, vira incerta e nunca de novo
        var d = new Cena();
        d.Script = (_, recarregou) => recarregou ? new ResultadoDoScript(true, null, "991") : Recusa();
        d.Recarga = x => { x.Confirmacoes.Ver(Quadro(null), enviado: true); return PlanoBDoEnvio.Recarregou; };
        await d.Rodar();
        checar(d.Enviados.Count == 1 && d.Relatos.Count == 1 && d.Relatos[0] is ("incerta", "erro_envio", _) && !d.Confirmacoes.Pendente(84),
            "PB-22 um quadro desta resposta saiu durante a recarga: incerta, sem reenvio");
    }

    private static async Task OQueNuncaRecarrega(Action<bool, string> checar)
    {
        var frio = new Cena();
        frio.Script = (_, _) => Frio();
        await frio.Rodar();
        checar(frio.Recargas == 0 && frio.Enviados.Count == 3 && frio.Relatos.Count == 1 && frio.Relatos[0] is ("falhou", "canal_errado", _),
            "PB-23 canal_errado (SDK frio) nunca recarrega: aquece e tenta, como hoje");

        var congelada = new Cena();
        congelada.Script = (_, _) => new ResultadoDoScript(false, "congelada", null, new JsonObject { ["etapa"] = "congelada" });
        await congelada.Rodar();
        checar(congelada.Recargas == 0 && congelada.Enviados.Count == 1 && congelada.Relatos[0] is ("falhou", "congelada", _),
            "PB-24 congelada nao recarrega nem tenta de novo");

        var visto = new Cena();
        visto.Script = (_, _) => Recusa();
        var l = visto.Laco();
        var ponteOriginal = l.Ponte()!;
        var ponte = new PonteDoChat
        {
            EnviarPeloSdk = async p =>
            {
                // o quadro saiu da pagina e o script disse que nao: duvida
                visto.Confirmacoes.Ver(Quadro(null), enviado: true);
                return await ponteOriginal.EnviarPeloSdk(p);
            },
            AbrirEColar = ponteOriginal.AbrirEColar, DiagSdk = ponteOriginal.DiagSdk, Gestor = ponteOriginal.Gestor,
            AquecerConversa = ponteOriginal.AquecerConversa, RecarregarParaEnvio = ponteOriginal.RecarregarParaEnvio,
        };
        await ServicoConversaChat.EnviarComTentativasAsync(visto.Saida, T0, new LacoDeEnvio
        {
            Portao = l.Portao, Confirmacoes = l.Confirmacoes, Sinal = l.Sinal, Ponte = () => ponte, Relatar = l.Relatar,
            Esperar = l.Esperar, Agora = l.Agora, Diag = l.Diag, Versao = l.Versao,
        });
        checar(visto.Recargas == 0 && visto.Enviados.Count == 1 && visto.Relatos.Count == 1 && visto.Relatos[0] is ("incerta", "erro_envio", _),
            "PB-25 quadro visto saindo e o SDK dizendo erro: incerta, sem recarga e sem reenvio");

        var semPorta = new Cena();
        semPorta.Script = (_, _) => Recusa();
        await semPorta.Rodar(comPlanoB: false);
        checar(semPorta.Enviados.Count == 3 && semPorta.Relatos.Count == 1 && semPorta.Relatos[0] is ("falhou", "erro_envio", _)
               && !semPorta.Relatos[0].Detalhe!.ContainsKey("plano_b"),
            "PB-26 sem a porta da recarga (ponte antiga), o laco e o de hoje e o detalhe nao fala de plano B");
    }

    // ── revisão adversarial da 1.0.35: a reserva de 50 s dentro do laço (achados 2 a 5) ──
    // A mensagem que com certeza não saiu (recusa sem quadro) nunca vira "incerta" nem some: vai para
    // a pessoa como "falhou". O SDK frio que a própria recarga causa não vira canal_errado (que pausa o
    // robô da loja): o laço aquece e tenta de novo, como sempre fez. E nenhuma saída gasta a reserva
    // de quem espera na fila esperando um laço que não tem mais tempo de tentar.
    private static async Task AReservaNoLaco(Action<bool, string> checar)
    {
        var ok = new ResultadoDoScript(true, null, "991");
        static string Rel(Cena x) => string.Join(",", x.Relatos.Select(r => r.Resultado + "/" + r.Erro));

        // achado 3: depois da recarga o SDK chega frio (conversa nenhuma aberta) e a tentativa seguinte é canal_errado
        var a = new Cena();
        var depois = 0;
        a.Script = (_, recarregou) => !recarregou ? Recusa() : ++depois == 1 ? Frio() : ok;
        await a.Rodar();
        checar(a.Relatos.Count == 0 && a.Enviados.Count == 3 && a.Recargas == 1 && a.Aquecimentos == 1,
            $"PB-35 recusa, recarga e SDK frio na tentativa seguinte: o laco aquece e tenta de novo, e sai sem relato de falha "
            + $"({a.Enviados.Count} envios, {a.Aquecimentos} aquecimentos, relatos {Rel(a)})");

        var b = new Cena();
        b.Script = (_, recarregou) => recarregou ? Frio() : Recusa();
        await b.Rodar();
        var detB = b.Relatos.LastOrDefault().Detalhe;
        checar(b.Relatos.Count == 1 && b.Relatos[0] is ("falhou", "erro_envio", _) && !b.Relatos.Any(r => r.Erro == "canal_errado")
               && b.Enviados.Count == 4 && Str(detB, "plano_b") == "recarregou" && Str(detB, "etapa") == "canal_errado" && Int(detB, "tentativas") == 4,
            $"PB-36 recarga e o SDK frio ate o fim: falhou/erro_envio (a recusa que comecou tudo), nunca canal_errado, que pausaria o robo da loja "
            + $"({b.Enviados.Count} envios, relatos {Rel(b)}, {detB?.ToJsonString()})");

        // achados 2 e 4: o plano B não recarregou, mas demorou
        var c = new Cena { TempoDaRecarga = TimeSpan.FromSeconds(36) };
        c.Script = (_, _) => Recusa();
        c.Recarga = _ => PlanoBDoEnvio.SemSdk;
        await c.Rodar();
        var detC = c.Relatos.LastOrDefault().Detalhe;
        checar(c.Relatos.Count == 1 && c.Relatos[0] is ("falhou", "erro_envio", _) && Str(detC, "plano_b") == "sem_sdk" && c.Enviados.Count == 2
               && c.Agora - T0 <= PortaoDeEnvio.PrazoDaReserva,
            $"PB-37 plano B sem recarga em 36 s: mais uma tentativa e falhou/erro_envio com plano_b sem_sdk dentro da reserva, nunca incerta "
            + $"({c.Enviados.Count} envios, relatos {Rel(c)}, fim em {(c.Agora - T0).TotalSeconds:0} s)");

        var d = new Cena { TempoDaRecarga = TimeSpan.FromSeconds(45) };
        d.Script = (_, _) => Recusa();
        d.Recarga = _ => PlanoBDoEnvio.SemSdk;
        await d.Rodar();
        checar(d.Relatos.Count == 1 && d.Relatos[0] is ("falhou", "erro_envio", _) && d.Enviados.Count == 1 && d.Aquecimentos == 0
               && d.Agora == T0.AddSeconds(1 + 2 + 45) && d.Diags.Any(x => x.Contains("sem tempo", StringComparison.Ordinal)),
            $"PB-38 plano B sem recarga em 45 s: nao sobra tempo para outra tentativa, falhou na hora (sem aquecer nem esperar o respiro) "
            + $"({d.Enviados.Count} envios, {d.Aquecimentos} aquecimentos, relatos {Rel(d)}, fim em {(d.Agora - T0).TotalSeconds:0} s)");

        var e = new Cena { TempoDaRecarga = TimeSpan.FromSeconds(40), RecargaNuncaVolta = true, PrazoDaRecarga = TimeSpan.FromMilliseconds(200) };
        e.Script = (_, _) => Recusa();
        await e.Rodar();
        var detE = e.Relatos.LastOrDefault().Detalhe;
        checar(e.Relatos.Count == 1 && e.Relatos[0] is ("falhou", "erro_envio", _) && Str(detE, "plano_b") == "sem_sdk",
            $"PB-39 a tela nao respondeu o plano B no prazo: falhou/erro_envio com plano_b sem_sdk, nunca incerta (relatos {Rel(e)})");

        // achado 4: a reserva vence entre tentativas (o aquecimento demorou) depois de uma falha certa
        var f = new Cena { TempoDoAquecimento = TimeSpan.FromSeconds(45) };
        f.Script = (_, _) => Frio();
        await f.Rodar();
        checar(f.Relatos.Count == 1 && f.Relatos[0] is ("falhou", "erro_envio", _) && f.Enviados.Count == 1,
            $"PB-40 a reserva venceu depois de uma tentativa que com certeza nao saiu: falhou/erro_envio (para a pessoa), nunca incerta nem canal_errado "
            + $"(relatos {Rel(f)})");

        // achado 5: a saída que esperava na fila (um envio por vez, 3 s entre elas) e nunca foi tentada
        var g = new Cena { TempoDaRecarga = TimeSpan.FromSeconds(36) };
        var da85 = 0;
        g.Script = (_, _) => Recusa();
        g.Recarga = _ => PlanoBDoEnvio.SemSdk;
        await g.Rodar();
        var fim84 = g.Agora;
        g.Agora += TimeSpan.FromSeconds(3);
        g.Script = (_, _) => { da85++; return ok; };
        var s85 = g.Saida with { Id = 85, Texto = "Oi, Ana! Seu prêmio é 1 Brownie. Ele vai junto com o pedido." };
        await g.Rodar(s85, T0.AddSeconds(-2));
        checar(fim84 - T0 <= PortaoDeEnvio.PrazoDaReserva && da85 == 0 && g.Relatos.Count == 2 && g.Relatos[1] is ("falhou", "reserva_vencida", _),
            $"PB-41 a saida da fila que perdeu a reserva sem ser tentada vai para a pessoa (falhou/reserva_vencida), nunca incerta; e a da frente "
            + $"termina dentro da reserva ({(fim84 - T0).TotalSeconds:0} s; relatos {Rel(g)})");
    }

    // ── pelo fonte ───────────────────────────────────────────────────────────
    private static void PeloFonte(Action<bool, string> checar)
    {
        var raiz = Raiz();
        string Ler(params string[] p) { if (raiz is null) return ""; var f = Path.Combine(new[] { raiz }.Concat(p).ToArray()); return File.Exists(f) ? File.ReadAllText(f) : ""; }
        var tela = Ler("Telas", "ChatIfood.xaml.cs");
        var js = TestesScriptSendbird.ExtrairRaw(tela, "ScriptSendbird") ?? "";
        checar(js.Length > 1000 && !Regex.IsMatch(js, @"\.\s*(re)?connect\s*\(|\.\s*disconnect\s*\(|setReconnect|reconnect\b", RegexOptions.IgnoreCase),
            "PB-27 o script nunca chama connect, disconnect nem reconnect do SDK: a conexao nova vem so pela recarga da pagina");
        var i = tela.IndexOf("public async Task<string> RecarregarParaEnvioAsync(", StringComparison.Ordinal);
        var corpo = i < 0 ? "" : tela.Substring(i, Math.Min(4000, tela.Length - i));
        var iImp = corpo.IndexOf("PlanoBDoEnvio.Impedimento(_pronto, IsHitTestVisible, !_colagens.IsEmpty, _gestorPedeLogin)", StringComparison.Ordinal);
        var iTeto = corpo.IndexOf("_tetoRecargaEnvio.Permitir(", StringComparison.Ordinal);
        var iReload = corpo.IndexOf("core.Reload()", StringComparison.Ordinal);
        checar(iImp > 0 && iTeto > iImp && iReload > iTeto,
            "PB-28 a tela confere Gestor na frente, colagem e login, depois o teto, e so entao recarrega");
        checar(corpo.Contains("if (Volatile.Read(ref _navegacoes) == antes) continue;", StringComparison.Ordinal)
               && corpo.Contains("PlanoBDoEnvio.SdkAberto(d)", StringComparison.Ordinal) && corpo.Contains("AquecerConversaAsync(IsHitTestVisible ? null : numero, orderUuid)", StringComparison.Ordinal),
            "PB-29 o diagnostico so vale depois que a recarga navegou; espera o SDK aberto e entao aquece a conversa do pedido");
        checar(tela.Contains("RecarregarParaEnvio = RecarregarParaEnvioAsync,", StringComparison.Ordinal)
               && typeof(PonteDoChat).GetProperty("RecarregarParaEnvio") is { } pr
               && !pr.GetCustomAttributes(typeof(System.Runtime.CompilerServices.RequiredMemberAttribute), false).Any(),
            "PB-30 a ponte liga a recarga, e a porta e opcional (a ponte antiga continua valendo)");
        var servico = Ler("ServicoConversaChat.cs");
        checar(servico.Contains("private static Task EnviarUmaAsync(SaidaConversa s, DateTime recebida)", StringComparison.Ordinal)
               && servico.Contains("=> EnviarComTentativasAsync(s, recebida, new LacoDeEnvio", StringComparison.Ordinal)
               && servico.Contains("Relatar = (id, resultado, erro, saida, detalhe) => RelatarAsync(id, resultado, erro, null, saida, detalhe),", StringComparison.Ordinal),
            "PB-31 o servico usa o MESMO laco que a bateria exercita, ligado nas portas de verdade");
        checar(!servico.Contains((char)0x2014) && !servico.Contains((char)0x2013) && !Ler("Pdv.Nucleo", "EnvioDoChat.cs").Contains((char)0x2014),
            "PB-32 nada com travessao no servico e no nucleo do envio");
    }

    private static string? Raiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj"))) return dir.FullName;
        return null;
    }
}
