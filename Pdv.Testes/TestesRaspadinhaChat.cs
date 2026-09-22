using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// CÓDIGO DA RASPADINHA NO CHAT DO iFOOD (22/09/2026, pedido do dono): "capturar quando o cliente
/// enviar o código de resgate da raspadinha, validar e gerar uma comanda avisando do bônus".
///
/// O WebView2, o CDP e o DOM do Gestor não são testáveis daqui (precisam da página logada com a
/// sessão da loja). Então tudo que decide alguma coisa foi extraído para função pura e é
/// exercitado aqui contra FIXTURES: quadros de WebSocket em formatos conhecidos, o pacote que o
/// script do painel manda, e as respostas da borda.
///
/// O QUE ESTA SUÍTE PROTEGE, em uma linha cada:
///  · o caixa NÃO valida código (quem decide é o servidor) e não sabe nem o formato dele;
///  · a fala da LOJA não vira chamada;
///  · a mesma mensagem não vira duas chamadas, e o mesmo bônus não vira duas comandas;
///  · sem internet a mensagem espera na fila, e a comanda sai quando a rede volta;
///  · desligado na loja é silêncio total, inclusive para o que já estava na fila;
///  · a comanda cabe na bobina estreita e nenhum texto de tela tem travessão.
/// </summary>
public static class TestesRaspadinhaChat
{
    public static async Task RodarAsync(Action<bool, string> checar)
    {
        QuemFalou(checar);
        AChave(checar);
        OCorpo(checar);
        AResposta(checar);
        OsTextos(checar);
        AComanda(checar);
        ODom(checar);
        ARecencia(checar);
        await OBanco(checar);
        AFila(checar);
        Fonte(checar);
    }

    // ── A IDADE DA FALA ──────────────────────────────────────────────────────
    // Nada no caixa comparava a idade da mensagem com o relógio, e o leitor de DOM manda a
    // rolagem VISÍVEL inteira a cada 7 s: abrir hoje uma conversa de três dias atrás resgatava
    // um código velho e tirava comanda de um pedido que já tinha ido embora.
    private static void ARecencia(Action<bool, string> checar)
    {
        var agora = new DateTime(2026, 9, 22, 19, 0, 0);
        MensagemChat Com(DateTimeOffset? q) => new("conv-1", "cliente-77", "AD-WKJNRF", q, false);

        checar(ChatRaspadinha.Recente(Com(new DateTimeOffset(agora.AddMinutes(-3), DateTimeOffset.Now.Offset)), agora),
            "RC-1 a fala de agora entra");
        checar(!ChatRaspadinha.Recente(Com(new DateTimeOffset(agora.AddDays(-3), DateTimeOffset.Now.Offset)), agora),
            "RC-2 a fala de três dias atrás NÃO entra (a raspadinha vive 14 dias: o código ainda valeria)");
        checar(!ChatRaspadinha.Recente(Com(new DateTimeOffset(agora.AddHours(-2), DateTimeOffset.Now.Offset)), agora),
            "RC-3 a fala de duas horas atrás NÃO entra");
        checar(!ChatRaspadinha.Recente(Com(null), agora),
            "RC-4 fala sem hora não entra (mesma regra conservadora da autoria)");
        checar(!ChatRaspadinha.Recente(Com(new DateTimeOffset(agora.AddHours(2), DateTimeOffset.Now.Offset)), agora),
            "RC-5 fala no futuro não entra (é hora de outro dia ancorada em hoje)");
        checar(!ChatRaspadinha.Recente(null, agora), "RC-6 mensagem nula não entra");

        // ⚠️ O BALÃO SÓ MOSTRA A HORA, NUNCA O DIA. Ancorar "23:50" em HOJE jogava a fala no
        // futuro, e futuro passava por recente. Agora ela é de ontem, que é o mais novo que ela
        // pode ser, e aí a janela decide com a idade de verdade.
        const string Balao2350 =
            """{"tipo":"chatmsgs","mensagens":[{"texto":"meu codigo e ad wkjnrf","minha":false,"hora":"23:50"}]}""";

        var deManha = new DateTime(2026, 9, 22, 6, 0, 0);
        var velha = ChatRaspadinha.MensagensDoDom(Balao2350, deManha).Mensagens.Single();
        checar(velha.Quando is { } q1 && q1.LocalDateTime == new DateTime(2026, 9, 21, 23, 50, 0),
            $"RC-7 a hora que ainda não chegou hoje é de ONTEM, não do futuro ({velha.Quando})");
        checar(!ChatRaspadinha.Recente(velha, deManha),
            "RC-8 e, sendo de ontem à noite, ela não resgata nada de manhã");

        // a virada do dia continua funcionando: 23:50 lido às 00:10 foi mesmo há 20 minutos
        var madrugada = new DateTime(2026, 9, 22, 0, 10, 0);
        var recemVirou = ChatRaspadinha.MensagensDoDom(Balao2350, madrugada).Mensagens.Single();
        checar(ChatRaspadinha.Recente(recemVirou, madrugada),
            "RC-8b mas a fala de vinte minutos atrás, do outro lado da meia-noite, continua valendo");

        var deHoje = ChatRaspadinha.MensagensDoDom(
            """{"tipo":"chatmsgs","mensagens":[{"texto":"meu codigo e ad wkjnrf","minha":false,"hora":"18:50"}]}""",
            agora).Mensagens.Single();
        checar(ChatRaspadinha.Recente(deHoje, agora), "RC-9 a fala de dez minutos atrás continua entrando");
    }

    // ── A JANELA DA FILA ─────────────────────────────────────────────────────
    private static void AFila(Action<bool, string> checar)
    {
        // ⚠️ A VENDA NÃO PODE FICAR ATRÁS DO CHAT. A janela é ORDER BY id LIMIT 50 e transitório
        // não conta tentativa: com a borda ainda não publicada, uma conversa aberta vira até
        // dezenas de mensagens presas com id baixo, e a venda gravada depois nunca entra na janela.
        checar(!Drenagem.JanelaPropria.Select(j => j.Tipo).Contains("venda"),
            "FL-1 a venda continua na janela principal");
        checar(Drenagem.JanelaPropria.Any(j => j.Tipo == ChatRaspadinha.TipoNaFila && j.Janela is > 0 and <= 10),
            "FL-2 a mensagem do chat tem janela própria e pequena (não disputa o LIMIT 50 da venda)");

        var agora = new DateTime(2026, 9, 22, 19, 0, 0);
        checar(Drenagem.PrazoDoTransitorio(ChatRaspadinha.TipoNaFila) < TimeSpan.FromDays(1),
            "FL-3 a mensagem do chat desiste em horas, não nos 7 dias da venda");
        checar(Drenagem.PrazoDoTransitorio("venda") == TimeSpan.FromDays(Drenagem.DiasParaDesistir),
            "FL-4 a venda continua com o orçamento inteiro de dias");
        checar(Drenagem.DecidirFila(null, 0, agora.AddHours(-12), agora,
                   Drenagem.PrazoDoTransitorio(ChatRaspadinha.TipoNaFila)) == Drenagem.AcaoFila.ExpiraVelho,
            "FL-5 mensagem de chat presa há meio dia sai da fila com rastro");
        checar(Drenagem.DecidirFila(null, 0, agora.AddHours(-12), agora,
                   Drenagem.PrazoDoTransitorio("venda")) == Drenagem.AcaoFila.Aguarda,
            "FL-6 a venda presa há meio dia continua esperando");

        // ⚠️ O CENÁRIO INTEIRO, no banco. O dono liga a coluna no painel antes de a borda estar no
        // ar (são dois deploys), o operador abre duas conversas, o leitor de DOM manda dezenas de
        // falas de uma vez e cada uma leva 404, que é transitório e não conta tentativa. Depois
        // disso o caixa fecha uma venda. Antes desta correção a venda ficava fora da janela até as
        // linhas do chat expirarem, o caixa seguia vendendo e o painel mostrava faturamento parado.
        var db = Path.Combine(Path.GetTempPath(), "raspa-fila-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var antes = Banco.CaminhoForcado;
        Banco.CaminhoForcado = db;
        try
        {
            Banco.Migrar(db);
            using var cx = Banco.Abrir(db);
            Isolamento.SemNuvem(cx);
            for (var i = 0; i < 80; i++)
                cx.Execute("INSERT INTO outbox (tipo, ref_id, client_key, payload, criado_em) VALUES (@T,@R,@C,'{}',@Em)",
                    new { T = ChatRaspadinha.TipoNaFila, R = "msg-" + i, C = "msg-" + i, Em = DateTime.Now.ToString("o") });
            cx.Execute("INSERT INTO outbox (tipo, ref_id, client_key, payload, criado_em) VALUES ('venda','v-1','v-1','{}',@Em)",
                new { Em = DateTime.Now.ToString("o") });

            var janela = Drenagem.JanelaDaFila(cx);
            var tipos = janela.Select(l => (string)l.tipo).ToList();
            checar(tipos.Contains("venda"),
                $"FL-7 com 80 mensagens de chat presas na frente, a VENDA entra na janela mesmo assim ({tipos.Count} itens)");
            checar(tipos.Count(t => t == ChatRaspadinha.TipoNaFila) <= 10,
                $"FL-8 e o chat leva só a janelinha dele ({tipos.Count(t => t == ChatRaspadinha.TipoNaFila)})");
            checar(tipos.IndexOf("venda") < tipos.IndexOf(ChatRaspadinha.TipoNaFila),
                "FL-9 o dinheiro vai na frente do conforto");
        }
        finally
        {
            Banco.CaminhoForcado = antes;
            SqliteConnection.ClearAllPools();
            try { File.Delete(db); } catch { }
        }
    }

    // ── FIXTURES: quadros de WebSocket em formatos conhecidos ────────────────
    // O formato REAL do chat do iFood só se vê com a página logada (a captura de rede
    // grava o shape mascarado em ProgramData para o dono mandar). Estes são os formatos
    // de família conhecida que o normalizador tem de aguentar sem cair, e é isso que a
    // suíte prova: o resto o dono confirma na loja, pelo chat-raspadinha.txt.

    /// <summary>Estilo Sendbird: comando de 4 letras e o JSON colado.</summary>
    private const string FrameSendbird =
        """MESG{"msg_id":9911,"message":"meu codigo e ad wkjnrf","user":{"user_id":"cliente-77"},"created_at":1790000000000,"channel_url":"canal-5592"}""";

    /// <summary>Estilo "envelope": o texto aninhado em payload/data, com o instante em segundos.</summary>
    private const string FrameEnvelope =
        """{"type":"message","payload":{"data":{"conversationId":"conv-1","senderId":"cliente-77","text":"AD-WKJNRF.","timestamp":1790000000}}}""";

    /// <summary>O mesmo envelope, mas a mensagem é da LOJA (o protocolo diz).</summary>
    private const string FrameDaLoja =
        """{"type":"message","payload":{"data":{"conversationId":"conv-1","senderId":"loja-1","text":"oi, ja estamos preparando","timestamp":1790000001,"mine":true}}}""";

    /// <summary>Quadro de controle: não é mensagem nenhuma.</summary>
    private const string FramePulso = """{"event":"heartbeat","ts":1790000002}""";

    /// <summary>O pacote que o script do painel manda quando o chat está aberto.</summary>
    private const string PacoteDom = """
        {"tipo":"chatmsgs","conversa":"pedido-5592","pedido":"5592","cliente":null,
         "mensagens":[{"texto":"boa tarde","minha":false,"hora":"14:30"},
                      {"texto":"ja estamos preparando","minha":true,"hora":"14:31"},
                      {"texto":"adwkjnrf","minha":false,"hora":"14:32"},
                      {"texto":"nao deu para decidir de quem e","minha":null,"hora":"14:33"},
                      {"texto":"   ","minha":false,"hora":"14:34"}]}
        """;

    // ── 1. QUEM FALOU ────────────────────────────────────────────────────────
    private static void QuemFalou(Action<bool, string> checar)
    {
        var m = ChatCaptura.NormalizarFrame(FrameSendbird);
        checar(m is not null && m.Texto == "meu codigo e ad wkjnrf" && m.Autor == "cliente-77"
               && m.ConversaId == "canal-5592",
            $"QF-1 o quadro com prefixo de comando (MESG{{...}}) vira mensagem ({m?.Texto ?? "null"})");

        var e = ChatCaptura.NormalizarFrame(FrameEnvelope);
        checar(e is not null && e.Texto == "AD-WKJNRF." && e.ConversaId == "conv-1",
            "QF-2 o quadro com o texto aninhado em payload/data também vira mensagem");

        checar(ChatCaptura.NormalizarFrame(FramePulso) is null, "QF-3 quadro de controle não vira mensagem");
        checar(ChatCaptura.NormalizarFrame("PING") is null, "QF-4 quadro sem JSON dentro não vira mensagem");

        checar(ChatRaspadinha.DoCliente(e, "loja-1"), "QF-5 fala de outro autor é do cliente");
        checar(!ChatRaspadinha.DoCliente(ChatCaptura.NormalizarFrame(FrameDaLoja), "loja-1"),
            "QF-6 fala da loja (mine=true) NÃO é do cliente");
        checar(!ChatRaspadinha.DoCliente(e with { Autor = "loja-1" }, "loja-1"),
            "QF-7 autor igual ao usuário desta loja NÃO é do cliente");
        checar(!ChatRaspadinha.DoCliente(e with { Autor = "LOJA-1" }, "loja-1"),
            "QF-8 o mesmo usuário em caixa diferente continua sendo a loja");
        // ⚠️ CONTRATO MUDADO (22/09/2026). Antes, sem saber o usuário da loja o quadro recebido
        // contava como fala do cliente. Com o PDV reiniciado e a sessão do Gestor já quente o token
        // do chat nunca é refeito, o usuário fica desconhecido o dia inteiro e TODO quadro recebido
        // com texto virava chamada: o eco do que a loja manda e as respostas prontas junto.
        checar(!ChatRaspadinha.DoCliente(e with { Minha = null }, null),
            "QF-9 sem lado no quadro e sem saber o usuário da loja, a mensagem NÃO entra");
        checar(ChatRaspadinha.DoCliente(e with { Minha = false }, null),
            "QF-9b quadro que DIZ que não é da loja entra mesmo sem o usuário da loja");
        checar(!ChatRaspadinha.DoCliente(e with { Minha = null, Autor = null }, "loja-1"),
            "QF-9c sem lado e sem autor não entra, nem sabendo quem é a loja");
        checar(!ChatRaspadinha.DoCliente(e with { Texto = "   " }, null), "QF-10 mensagem só com espaço não conta");
        checar(!ChatRaspadinha.DoCliente(null, null), "QF-11 mensagem nula não conta");

        // o token traz o usuário desta loja; o diagnóstico continua vendo mascarado
        var jwt = Jwt("""{"u":"loja-1","v":2,"e":1790000000}""");
        checar(ChatCaptura.UserIdDoToken(jwt) == "loja-1", "QF-12 o claim u do token diz quem é a loja");
        checar(ChatCaptura.LerFormatoToken(jwt)?.UserIdMascarado == "XXXX",
            "QF-13 o diagnóstico continua mascarando o mesmo claim");
        checar(ChatCaptura.UserIdDoToken("não é jwt") is null && ChatCaptura.UserIdDoToken(null) is null,
            "QF-14 token estranho não explode");
    }

    private static string Jwt(string payloadJson)
    {
        static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return B64("""{"alg":"HS256","typ":"JWT"}""") + "." + B64(payloadJson) + ".assinatura";
    }

    // ── 2. A CHAVE (não repetir) ─────────────────────────────────────────────
    private static void AChave(Action<bool, string> checar)
    {
        var a = ChatCaptura.NormalizarFrame(FrameEnvelope)!;
        checar(ChatRaspadinha.Chave(a) == ChatRaspadinha.Chave(a), "CH-1 a mesma mensagem dá a mesma chave");
        checar(ChatRaspadinha.Chave(a) == ChatRaspadinha.Chave(a with { Texto = "  AD-WKJNRF.  " }),
            "CH-2 espaço a mais não muda a chave (o texto é normalizado antes)");
        checar(ChatRaspadinha.Chave(a) != ChatRaspadinha.Chave(a with { Texto = "AD-OUTRO1" }),
            "CH-3 texto diferente dá chave diferente");
        checar(ChatRaspadinha.Chave(a) != ChatRaspadinha.Chave(a with { ConversaId = "conv-2" }),
            "CH-4 outra conversa dá chave diferente");
        checar(ChatRaspadinha.Chave(a) != ChatRaspadinha.Chave(a with { Autor = "cliente-99" }),
            "CH-5 outro autor dá chave diferente");
        checar(ChatRaspadinha.Chave(a).Length == 24 && ChatRaspadinha.Chave(a).All(Uri.IsHexDigit),
            "CH-6 a chave é curta e só hexadecimal (cabe em índice e em log)");
        checar(!ChatRaspadinha.Chave(a).Contains("WKJNRF", StringComparison.OrdinalIgnoreCase),
            "CH-7 a chave não carrega o texto do cliente");

        checar(ChatRaspadinha.CortarTexto("  a\r\n  b  ") == "a b", "CH-8 o texto vira uma linha só");
        checar(ChatRaspadinha.CortarTexto(new string('x', 900)).Length == ChatRaspadinha.TetoTexto,
            "CH-9 texto gigante é cortado no teto");
        checar(ChatRaspadinha.CortarTexto(null) == "" && ChatRaspadinha.CortarTexto("   ") == "",
            "CH-10 texto nulo ou vazio vira vazio");
    }

    // ── 3. O QUE VAI ─────────────────────────────────────────────────────────
    private static void OCorpo(Action<bool, string> checar)
    {
        var corpo = ChatRaspadinha.CorpoDoPedido("  meu codigo e ad wkjnrf  ", "American Day Savassi",
            " 5592 ", null, "MARIA", ChatRaspadinha.OrigemCaixa);
        checar(corpo.Contains("\"texto\":\"meu codigo e ad wkjnrf\"", StringComparison.Ordinal),
            "CP-1 o texto vai CRU (o caixa não extrai código nenhum)");
        checar(corpo.Contains("\"loja\":\"American Day Savassi\"") && corpo.Contains("\"pedido\":\"5592\"")
               && corpo.Contains("\"cliente\":\"MARIA\"") && corpo.Contains("\"origem\":\"caixa\""),
            "CP-2 loja, pedido, cliente e origem vão no corpo");
        checar(corpo.Contains("\"ifood_order_id\":null"), "CP-3 o que o caixa não sabe vai nulo, não chutado");
        checar(!Regex.IsMatch(corpo, @"""(codigo|code|redeem)""", RegexOptions.IgnoreCase),
            "CP-4 o corpo NÃO leva campo de código: quem acha o código é o servidor");

        var ext = ChatRaspadinha.CorpoDoPedido("x", null, null, null, null, ChatRaspadinha.OrigemExtensao);
        checar(ext.Contains("\"origem\":\"extensao\"") && ext.Contains("\"loja\":null"),
            "CP-5 a extensão do Chrome usa o mesmo corpo, com a própria origem");
        checar(ChatRaspadinha.CorpoDoPedido("x", null, null, null, null, "inventada").Contains("\"origem\":\"caixa\""),
            "CP-6 origem desconhecida vira 'caixa' (o servidor nunca recebe um valor solto)");
        checar(ChatRaspadinha.CorpoDoPedido("x", "   ", "   ", "   ", "   ", null).Contains("\"loja\":null"),
            "CP-7 campo só com espaço vira nulo");
    }

    // ── 4. O QUE VOLTA ───────────────────────────────────────────────────────
    private const string RespostaComBonus = """
        {"ok":true,"bonus":{"id":"bn-1","scratch_id":"sc-1","redeem_code":"AD-WKJNRF","loja":"American Day Savassi",
         "premio_nome":"Cookie Classico","premio_emoji":"🍪","cliente_nome":"MARIA DA SILVA","pedido_numero":"5592",
         "ifood_order_id":"ord-9","origem":"caixa","criado_em":"2026-09-22T14:32:00-03:00"}}
        """;

    private static void AResposta(Action<bool, string> checar)
    {
        var agora = new DateTime(2026, 9, 22, 14, 40, 0);

        var b = ChatRaspadinha.LerResposta(200, RespostaComBonus, agora);
        checar(b.Desfecho == DesfechoChat.Bonus && b.Bonus?.Id == "bn-1" && b.Bonus?.Codigo == "AD-WKJNRF"
               && b.Bonus?.Premio == "Cookie Classico" && b.Bonus?.Pedido == "5592",
            "RS-1 a resposta com bônus vira bônus");
        checar(b.Bonus?.Cliente == "Maria", "RS-2 o balcão vê só o primeiro nome do cliente");
        checar(b.Bonus?.Quando.Hour == 14 && b.Bonus?.Quando.Minute == 32,
            "RS-3 a hora do bônus é a do servidor, não a do caixa");

        var raiz = ChatRaspadinha.LerResposta(200,
            """{"ok":true,"id":"bn-2","redeem_code":"AD-AAA111","premio":"Brownie","pedido":"70"}""", agora);
        checar(raiz.Desfecho == DesfechoChat.Bonus && raiz.Bonus?.Id == "bn-2",
            "RS-4 bônus com os campos na raiz também é lido");

        var lista = ChatRaspadinha.LerResposta(200, "[" + RespostaComBonus + "]", agora);
        checar(lista.Desfecho == DesfechoChat.Bonus, "RS-5 resposta em lista (estilo PostgREST) também é lida");

        var ja = ChatRaspadinha.LerResposta(200,
            """{"ok":true,"ja_registrado":true,"bonus":{"id":"bn-1","redeem_code":"AD-WKJNRF"}}""", agora);
        checar(ja.Desfecho == DesfechoChat.Bonus && ja.JaRegistrado,
            "RS-6 o mesmo código de novo devolve o bônus que já existe, não erro");

        var sem = ChatRaspadinha.LerResposta(200, """{"ok":false,"motivo":"sem_codigo"}""", agora);
        checar(sem.Desfecho == DesfechoChat.SemCodigo && ChatRaspadinha.TextoDeTela(sem) is null,
            "RS-7 mensagem sem código é SILÊNCIO: nem papel, nem aviso");

        var okSemBonus = ChatRaspadinha.LerResposta(200, """{"ok":true}""", agora);
        checar(okSemBonus.Desfecho == DesfechoChat.SemCodigo && okSemBonus.Bonus is null,
            "RS-8 'ok' sem bônus não inventa comanda: mesmo silêncio");

        foreach (var (cru, esperado) in new (string, MotivoChat)[]
                 {
                     ("expired", MotivoChat.Venceu), ("vencido", MotivoChat.Venceu),
                     ("already_redeemed", MotivoChat.JaUsado), ("ja_resgatado", MotivoChat.JaUsado),
                     ("not_found", MotivoChat.NaoAchou), ("formato_invalido", MotivoChat.NaoAchou),
                     ("not_scratched", MotivoChat.NaoRaspou), ("outra_loja", MotivoChat.OutraLoja),
                     ("sem_codigo", MotivoChat.SemCodigo), ("sem_permissao", MotivoChat.SemPermissao),
                 })
            checar(ChatRaspadinha.LerMotivo(cru) == esperado, $"RS-9 motivo '{cru}' é lido como {esperado}");
        checar(ChatRaspadinha.LerMotivo("motivo_que_ainda_nao_existe") == MotivoChat.Desconhecido,
            "RS-10 motivo novo não explode: cai em desconhecido");

        var venceu = ChatRaspadinha.LerResposta(200, """{"ok":false,"motivo":"expired"}""", agora);
        checar(venceu.Desfecho == DesfechoChat.Recusado && venceu.Motivo == MotivoChat.Venceu && venceu.Bonus is null,
            "RS-11 código vencido é recusa, sem papel");

        // rede e servidor
        checar(ChatRaspadinha.LerResposta(-1, "", agora).Desfecho == DesfechoChat.SemRede,
            "RS-12 nada saiu do caixa (-1) é sem rede");
        checar(ChatRaspadinha.LerResposta(0, "", agora).Desfecho == DesfechoChat.NaoConfirmou,
            "RS-13 saiu e ficou sem resposta (0) é 'não confirmou' (o servidor pode ter feito)");
        foreach (var st in new[] { 500, 502, 408, 425, 429 })
            checar(ChatRaspadinha.LerResposta(st, "", agora).Desfecho == DesfechoChat.NaoConfirmou,
                $"RS-14 HTTP {st} é 'tente depois', não recusa");
        checar(ChatRaspadinha.LerResposta(404, "", agora).Desfecho == DesfechoChat.NuvemSemRecurso,
            "RS-15 404 é a borda ainda não publicada: espera o deploy");
        foreach (var st in new[] { 401, 403 })
            checar(ChatRaspadinha.LerResposta(st, "", agora).Desfecho == DesfechoChat.SemPermissao,
                $"RS-16 HTTP {st} é este caixa barrado");
        checar(ChatRaspadinha.LerResposta(200, "isso não é json", agora).Desfecho == DesfechoChat.NaoConfirmou,
            "RS-17 corpo ilegível num 2xx NÃO é recusa: o servidor pode ter registrado");
        checar(ChatRaspadinha.LerResposta(400, "isso não é json", agora).Desfecho == DesfechoChat.Recusado,
            "RS-18 corpo ilegível num 400 é recusa (a chamada não rodou)");
    }

    // ── 5. OS TEXTOS DE TELA ─────────────────────────────────────────────────
    private static void OsTextos(Action<bool, string> checar)
    {
        var agora = new DateTime(2026, 9, 22, 14, 40, 0);
        var b = ChatRaspadinha.LerResposta(200, RespostaComBonus, agora);
        var linha = ChatRaspadinha.TextoDeTela(b)!;
        checar(linha.Contains("Cookie Classico") && linha.Contains("Maria") && linha.Contains("#5592"),
            $"TX-1 a linha do bônus diz o prêmio, para quem e de que pedido ({linha})");

        var todas = new List<string>();
        foreach (var r in new[]
                 {
                     b,
                     ChatRaspadinha.LerResposta(200, """{"ok":false,"motivo":"expired"}""", agora),
                     ChatRaspadinha.LerResposta(200, """{"ok":false,"motivo":"already_redeemed"}""", agora),
                     ChatRaspadinha.LerResposta(200, """{"ok":false,"motivo":"not_found"}""", agora),
                     ChatRaspadinha.LerResposta(200, """{"ok":false,"motivo":"not_scratched"}""", agora),
                     ChatRaspadinha.LerResposta(200, """{"ok":false,"motivo":"outra_loja"}""", agora),
                     ChatRaspadinha.LerResposta(200, """{"ok":false,"motivo":"coisa_nova"}""", agora),
                     ChatRaspadinha.LerResposta(-1, "", agora),
                     ChatRaspadinha.LerResposta(0, "", agora),
                     ChatRaspadinha.LerResposta(401, "", agora),
                 })
            if (ChatRaspadinha.TextoDeTela(r) is { } t) todas.Add(t);

        checar(todas.Count >= 9, $"TX-2 cada desfecho que importa tem a sua frase ({todas.Count})");
        checar(todas.All(t => !t.Contains('—') && !t.Contains('–')),
            "TX-3 nenhuma frase de tela tem travessão");
        checar(todas.All(t => !t.Contains('\n') && t.Length <= 110),
            "TX-4 toda frase é de uma linha e curta");
        checar(todas.All(t => !Regex.IsMatch(t, @"\b(HTTP|null|erro \d|PGRST|exception)\b", RegexOptions.IgnoreCase)),
            "TX-5 nenhuma frase mostra código de erro ou jargão");
        checar(todas.Distinct().Count() >= 7, "TX-6 as recusas não dizem todas a mesma coisa");

        // silêncio de verdade
        foreach (var mudo in new[]
                 {
                     ChatRaspadinha.LerResposta(200, """{"ok":false,"motivo":"sem_codigo"}""", agora),
                     ChatRaspadinha.LerResposta(404, "", agora),
                     ChatRaspadinha.LerResposta(200, """{"ok":false,"motivo":"loja_sem_raspadinha"}""", agora),
                 })
            checar(ChatRaspadinha.TextoDeTela(mudo) is null,
                $"TX-7 {mudo.Desfecho}/{mudo.Motivo} não vira ruído no balcão");

        // a frase do servidor só entra se obedecer a regra da tela
        var comFrase = ChatRaspadinha.LerResposta(200,
            """{"ok":false,"motivo":"coisa_nova","mensagem":"Peça ao cliente para mandar o código de novo."}""", agora);
        checar(ChatRaspadinha.TextoDeTela(comFrase) == "Peça ao cliente para mandar o código de novo.",
            "TX-8 motivo que esta versão não conhece usa a frase do servidor");
        var comTravessao = ChatRaspadinha.LerResposta(200,
            """{"ok":false,"motivo":"coisa_nova","mensagem":"Código inválido — tente de novo"}""", agora);
        checar(ChatRaspadinha.TextoDeTela(comTravessao) is { } f && !f.Contains('—'),
            "TX-9 frase do servidor com travessão é recusada, e fica a do caixa");

        // ⚠️ A FRASE DE OUTRA LOJA NÃO ACUSA O CLIENTE. A loja mandada na chamada é a do TERMINAL,
        // e a mesma conta do Gestor enxerga Savassi e Castelo: o código bom do Castelo chega aqui
        // julgado contra a Savassi. Quem está na loja errada é o caixa, não quem mandou o código.
        var outra = ChatRaspadinha.TextoDeTela(
            ChatRaspadinha.LerResposta(200, """{"ok":false,"motivo":"outra_loja"}""", agora))!;
        checar(!outra.StartsWith("O cliente", StringComparison.OrdinalIgnoreCase)
               && !outra.Contains("o cliente mandou", StringComparison.OrdinalIgnoreCase),
            $"TX-10 a recusa por outra loja não acusa quem mandou o código ({outra})");
        checar(outra.Contains("loja", StringComparison.OrdinalIgnoreCase) && outra.Length <= 90,
            "TX-11 e ainda diz, em uma linha, o que o operador tem de conferir");
    }

    // ── 6. A COMANDA ─────────────────────────────────────────────────────────
    private static void AComanda(Action<bool, string> checar)
    {
        var b = new BonusRaspadinha("bn-1", "AD-WKJNRF", "Cookie Classico", "🍪", "Maria", "5592",
            "American Day Savassi", "ord-9", ChatRaspadinha.OrigemCaixa, new DateTime(2026, 9, 22, 14, 32, 0));
        var linhas = ChatRaspadinha.ComandaLinhas(b, 40, new DateTime(2026, 9, 22, 14, 32, 0));
        var limpas = linhas.Select(LinhaEscala.Limpa).ToList();
        var texto = string.Join("\n", limpas);

        checar(texto.Contains("BRINDE DA RASPADINHA"), "CM-1 o papel diz o que é logo no cabeçalho");
        checar(texto.Contains("COOKIE CLASSICO"), "CM-2 o prêmio sai no papel, em maiúsculas");
        checar(texto.Contains("Cliente: Maria"), "CM-3 o cliente sai no papel");
        checar(texto.Contains("Pedido: #5592"), "CM-4 o pedido sai no papel");
        checar(texto.Contains("Codigo: AD-WKJNRF"), "CM-5 o código queimado sai no papel (é o que o gerente confere)");
        checar(texto.Contains("Hora: 14:32"), "CM-6 a hora sai no papel");
        checar(!texto.Contains('—') && !texto.Contains('–'), "CM-7 o papel não tem travessão");

        var escalaDoPremio = linhas.Select(LinhaEscala.Le).Where(x => x.Texto.Contains("COOKIE CLASSICO"))
            .Select(x => x.Escala).FirstOrDefault();
        checar(escalaDoPremio >= 2.0, $"CM-8 o prêmio sai grande, para ser lido de longe ({escalaDoPremio})");

        // bobina estreita: encolhe junto, não sai cortado
        foreach (var colunas in new[] { 32, 40, 48 })
        {
            var L = Nucleo.Kds.ColunasComanda(colunas);
            var estreitas = ChatRaspadinha.ComandaLinhas(b, colunas).Select(LinhaEscala.Limpa).ToList();
            checar(estreitas.All(l => l.Length <= L),
                $"CM-9 em {colunas} colunas nenhuma linha passa de {L} (nada sai cortado)");
        }

        // prêmio comprido: quebra em vez de sumir
        var comprido = ChatRaspadinha.ComandaLinhas(b with { Premio = "CAIXA COM 6 DONUTS SORTIDOS DA CASA E BROWNIE" }, 32)
            .Select(LinhaEscala.Limpa).ToList();
        checar(string.Concat(comprido.Select(l => l.Trim())).Contains("BROWNIE"),
            "CM-10 prêmio comprido quebra em mais linhas em vez de perder o fim");

        // o que não existe não vira linha vazia
        var magro = ChatRaspadinha.ComandaLinhas(
            new BonusRaspadinha("bn-2", null, null, null, null, null, null, null, ChatRaspadinha.OrigemExtensao,
                new DateTime(2026, 9, 22, 15, 0, 0)), 40, new DateTime(2026, 9, 22, 15, 0, 0))
            .Select(LinhaEscala.Limpa).ToList();
        var textoMagro = string.Join("\n", magro);
        checar(textoMagro.Contains("BRINDE DA RASPADINHA") && textoMagro.Contains("BRINDE"),
            "CM-11 bônus sem prêmio ainda tira papel (alguém tem de saber que existe)");
        checar(!textoMagro.Contains("Cliente:") && !textoMagro.Contains("Codigo:"),
            "CM-12 sem cliente ou código, o papel não imprime rótulo vazio");
        // O NÚMERO DO PEDIDO É O QUE FAZ O PAPEL SERVIR. Sem ele a linha não some: ela DIZ que
        // falta, senão quem pega a comanda com oito sacolas na bancada não sabe nem o que procurar.
        checar(textoMagro.Contains("Pedido: confira no chat"),
            "CM-14 sem o número do pedido, o papel manda conferir no chat em vez de omitir a linha");
        checar(textoMagro.Contains("Hora: 15:00"), "CM-13 a hora sai sempre");
    }

    // ── 7. O DOM (complemento) ───────────────────────────────────────────────
    private static void ODom(Action<bool, string> checar)
    {
        var l = ChatRaspadinha.MensagensDoDom(PacoteDom);
        checar(l.Pedido == "5592" && l.ConversaId == "pedido-5592",
            "DM-1 o número do pedido do cabeçalho vem junto (é o que liga o bônus ao pedido)");
        checar(l.Mensagens.Count == 3,
            $"DM-2 entram só as falas com autoria decidida e com texto ({l.Mensagens.Count})");
        checar(l.Mensagens.Count(m => m.Minha == true) == 1, "DM-3 a fala da loja vem marcada como dela");
        checar(l.Mensagens.Count(m => ChatRaspadinha.DoCliente(m, null)) == 2,
            "DM-4 sobram as duas falas do cliente");
        checar(!l.Mensagens.Any(m => m.Texto.Contains("nao deu para decidir")),
            "DM-5 fala sem autoria decidida fica de FORA (mandar a fala da loja é o que o desenho proíbe)");
        checar(l.Mensagens.Any(m => m.Quando is { } q && q.Hour == 14 && q.Minute == 32),
            "DM-6 a hora do balão ('14:32') vira instante de hoje");

        var semHora = ChatRaspadinha.MensagensDoDom(
            """{"tipo":"chatmsgs","mensagens":[{"texto":"oi","minha":false,"hora":"25:99"}]}""");
        checar(semHora.Mensagens.Count == 1 && semHora.Mensagens[0].Quando is null,
            "DM-7 hora impossível vira sem hora, não exceção");

        foreach (var lixo in new[] { null, "", "   ", "não é json", "[1,2,3]", """{"tipo":"chatmsgs"}""" })
            checar(ChatRaspadinha.MensagensDoDom(lixo).Mensagens.Count == 0,
                $"DM-8 pacote estranho vira leitura vazia, nunca exceção ({lixo ?? "null"})");

        // a mesma mensagem vista pelo WebSocket e pelo DOM: o servidor devolve o mesmo bônus, e
        // é a linha do bônus (não a chave da mensagem) que impede a segunda comanda. Ver BC-6.
        var doDom = l.Mensagens.First(m => m.Texto == "adwkjnrf");
        var doWs = ChatCaptura.NormalizarFrame(FrameSendbird)!;
        checar(ChatRaspadinha.Chave(doDom) != ChatRaspadinha.Chave(doWs),
            "DM-9 os dois caminhos podem dar chaves diferentes: a defesa da comanda dobrada é outra");
    }

    // ── 8. O BANCO E A FILA ──────────────────────────────────────────────────
    private static async Task OBanco(Action<bool, string> checar)
    {
        var db = Path.Combine(Path.GetTempPath(), "raspa-chat-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var antes = Banco.CaminhoForcado;
        Banco.CaminhoForcado = db;
        try
        {
            Banco.Migrar(db);
            using var cx = Banco.Abrir(db);
            Isolamento.SemNuvem(cx);

            MensagemDoChat Msg(string chave, string texto) =>
                new(chave, texto, "American Day Savassi", "5592", null, "MARIA", ChatRaspadinha.OrigemCaixa);

            long Linhas() => cx.ExecuteScalar<long>("SELECT COUNT(*) FROM raspadinha_chat");
            long NaFila() => cx.ExecuteScalar<long>("SELECT COUNT(*) FROM outbox WHERE tipo = @T",
                new { T = ChatRaspadinha.TipoNaFila });
            string Situacao(string k) => cx.ExecuteScalar<string>(
                "SELECT situacao FROM raspadinha_chat WHERE chave = @K", new { K = k })!;

            // DESLIGADO NA LOJA: nem grava
            checar(!ChatRaspadinha.LigadoNaLoja(cx), "BC-1 a captura nasce desligada (config ausente)");
            checar(!ChatRaspadinha.Registrar(cx, Msg("k-off", "adwkjnrf")) && Linhas() == 0 && NaFila() == 0,
                "BC-2 com a loja desligada nada é gravado e nada vai para a fila");

            // o painel liga
            var linhaPainel = new ConfigLojaPainel.Linha("American Day Savassi", null, null, null, null,
                null, null, null, null, null, null, true);
            var mudou = ConfigLojaPainel.Aplicar(cx, linhaPainel, new DateTime(2026, 9, 22, 9, 0, 0));
            checar(mudou.Contains("código da raspadinha no chat") && ChatRaspadinha.LigadoNaLoja(cx),
                $"BC-3 o painel liga a captura por loja ({mudou})");
            var desliga = ConfigLojaPainel.Aplicar(cx, linhaPainel with { RaspadinhaNoChat = null },
                new DateTime(2026, 9, 22, 9, 0, 0));
            checar(desliga.Contains("código da raspadinha no chat") && !ChatRaspadinha.LigadoNaLoja(cx),
                "BC-4 campo ausente no painel DESLIGA (o painel é a verdade)");
            ConfigLojaPainel.Aplicar(cx, linhaPainel, new DateTime(2026, 9, 22, 9, 0, 0));

            // grava + enfileira na mesma transação, e não repete
            checar(ChatRaspadinha.Registrar(cx, Msg("k-1", "meu codigo e ad wkjnrf")) && Linhas() == 1 && NaFila() == 1,
                "BC-5 a mensagem é gravada e enfileirada na mesma transação");
            checar(!ChatRaspadinha.Registrar(cx, Msg("k-1", "meu codigo e ad wkjnrf")) && Linhas() == 1 && NaFila() == 1,
                "BC-6 a MESMA mensagem não vira segunda linha nem segunda chamada");
            checar(!ChatRaspadinha.Registrar(cx, Msg("k-vazia", "   ")) && Linhas() == 1,
                "BC-7 mensagem sem texto não entra");

            // a fila resolve: o servidor devolve bônus
            var chamadas = new List<string>();
            Func<string, string, Task<(int, string?)>> Responde(int st, string? corpo) =>
                (nome, corpo2) => { chamadas.Add(nome + "|" + corpo2); return Task.FromResult((st, corpo)); };

            var agora = new DateTime(2026, 9, 22, 14, 40, 0);
            var r1 = await ChatRaspadinha.ResolverNaFilaAsync("k-1", Responde(200, RespostaComBonus), agora);
            checar(r1.Ok == true && Situacao("k-1") == "bonus", $"BC-8 a fila resolve a mensagem com bônus ({r1.Erro})");
            checar(chamadas.Count == 1 && chamadas[0].StartsWith(ChatRaspadinha.Edge + "|", StringComparison.Ordinal),
                "BC-9 a fila chama a BORDA (raspadinha-chat), não uma RPC");
            checar(ChatRaspadinha.ParaImprimir().Count == 1, "BC-10 nasce UM bônus para imprimir");

            // a mesma raspadinha chegando por OUTRA mensagem: mesmo bônus, UMA comanda
            ChatRaspadinha.Registrar(cx, Msg("k-2", "ad-wkjnrf"));
            var r2 = await ChatRaspadinha.ResolverNaFilaAsync("k-2",
                Responde(200, """{"ok":true,"ja_registrado":true,"bonus":{"id":"bn-1","redeem_code":"AD-WKJNRF","premio_nome":"Cookie Classico"}}"""),
                agora);
            checar(r2.Ok == true && ChatRaspadinha.ParaImprimir().Count == 1,
                "BC-11 o mesmo bônus por outra mensagem NÃO vira segunda comanda");

            // o claim da impressão é de um só
            var bonus = ChatRaspadinha.ParaImprimir()[0];
            checar(ChatRaspadinha.ReivindicarImpressao(bonus.Id), "BC-12 o primeiro a reivindicar leva o papel");
            checar(!ChatRaspadinha.ReivindicarImpressao(bonus.Id), "BC-13 o segundo não imprime de novo");
            checar(ChatRaspadinha.ParaImprimir().Count == 0, "BC-14 impresso sai da lista de pendentes");
            checar(ChatRaspadinha.Bonus(bonus.Id)?.Codigo == "AD-WKJNRF" && ChatRaspadinha.UltimoBonus() is not null,
                "BC-15 o bônus continua achável para a reimpressão manual");

            // sem código: resolve em silêncio
            ChatRaspadinha.Registrar(cx, Msg("k-3", "boa tarde, ja saiu?"));
            var r3 = await ChatRaspadinha.ResolverNaFilaAsync("k-3",
                Responde(200, """{"ok":false,"motivo":"sem_codigo"}"""), agora);
            checar(r3.Ok == true && Situacao("k-3") == "sem_codigo" && ChatRaspadinha.ParaImprimir().Count == 0,
                "BC-16 mensagem sem código sai da fila em silêncio, sem papel");

            // SEM INTERNET: fica na fila
            ChatRaspadinha.Registrar(cx, Msg("k-4", "ad-zzz999"));
            var r4 = await ChatRaspadinha.ResolverNaFilaAsync("k-4", Responde(0, null), agora);
            checar(r4.Ok is null && Situacao("k-4") == "na_fila",
                "BC-17 sem resposta a mensagem CONTINUA na fila (nada se perde)");
            var r4b = await ChatRaspadinha.ResolverNaFilaAsync("k-4", Responde(404, ""), agora);
            checar(r4b.Ok is null && r4b.Erro!.Contains(ChatRaspadinha.Edge),
                "BC-18 borda ainda não publicada é espera, com o motivo legível");
            var r4c = await ChatRaspadinha.ResolverNaFilaAsync("k-4", Responde(200, RespostaComBonus), agora);
            checar(r4c.Ok == true && Situacao("k-4") == "bonus",
                "BC-19 quando a rede volta a mesma mensagem é mandada e o bônus aparece");

            // a tela ainda está mandando: a fila não mexe
            ChatRaspadinha.Registrar(cx, Msg("k-5", "ad-aaa111"));
            cx.Execute("UPDATE raspadinha_chat SET situacao='enviando', tentado_em=@Em WHERE chave='k-5'",
                new { Em = agora.ToString("o") });
            var antesDasChamadas = chamadas.Count;
            var r5 = await ChatRaspadinha.ResolverNaFilaAsync("k-5", Responde(200, RespostaComBonus), agora);
            checar(r5.Ok is null && chamadas.Count == antesDasChamadas,
                "BC-20 linha que a tela está mandando agora não é tocada pela fila");
            var r5b = await ChatRaspadinha.ResolverNaFilaAsync("k-5", Responde(200, RespostaComBonus),
                agora + ChatRaspadinha.EsperaDaTela + TimeSpan.FromMinutes(1));
            checar(r5b.Ok == true, "BC-21 passado o prazo da tela a fila assume (o caixa pode ter caído)");

            // caixa barrado: recusa permanente
            ChatRaspadinha.Registrar(cx, Msg("k-6", "ad-bbb222"));
            var r6 = await ChatRaspadinha.ResolverNaFilaAsync("k-6", Responde(403, ""), agora);
            checar(r6.Ok == false && Situacao("k-6") == "nao_enviado",
                "BC-22 caixa barrado é recusa permanente (não fica martelando)");

            // A LOJA DESLIGOU depois: o que estava na fila NÃO é mandado
            ChatRaspadinha.Registrar(cx, Msg("k-7", "ad-ccc333"));
            ConfigLojaPainel.Aplicar(cx, linhaPainel with { RaspadinhaNoChat = false },
                new DateTime(2026, 9, 22, 16, 0, 0));
            var chamadasAntes = chamadas.Count;
            var r7 = await ChatRaspadinha.ResolverNaFilaAsync("k-7", Responde(200, RespostaComBonus), agora);
            checar(r7.Ok == true && chamadas.Count == chamadasAntes,
                "BC-23 desligada na loja, nem o que já estava na fila é mandado");

            // linha que sumiu não trava a fila
            var r8 = await ChatRaspadinha.ResolverNaFilaAsync("k-nao-existe", Responde(200, RespostaComBonus), agora);
            checar(r8.Ok == true, "BC-24 mensagem local que sumiu sai da fila sem chamada");

            checar(Drenagem.TiposComHandler.Contains(ChatRaspadinha.TipoNaFila),
                "BC-25 o tipo novo está no filtro da drenagem (senão a linha existiria e nunca sairia)");

            // ── O TEXTO DA PESSOA NÃO SOBREVIVE AO DESFECHO ──────────────────
            // A tabela guardava o texto de TODA mensagem capturada, para sempre e sem limpeza:
            // reclamação, endereço, telefone digitado no chat, seis meses no SQLite do balcão.
            var textos = cx.Query<string>("SELECT texto FROM raspadinha_chat WHERE situacao <> 'na_fila'").ToList();
            checar(textos.Count > 0 && textos.All(string.IsNullOrEmpty),
                $"BC-26 mensagem com desfecho não guarda mais o texto do cliente ({textos.Count} linhas)");
            var resolvidasAntes = cx.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM raspadinha_chat WHERE resolvido_em IS NOT NULL");
            var apagadas = ChatRaspadinha.Faxina(cx, DateTime.Now + ChatRaspadinha.GuardaDaMensagem + TimeSpan.FromDays(1));
            var resolvidasDepois = cx.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM raspadinha_chat WHERE resolvido_em IS NOT NULL");
            checar(resolvidasAntes > 0 && apagadas == resolvidasAntes && resolvidasDepois == 0,
                $"BC-27 a faxina apaga a linha resolvida e velha ({resolvidasAntes} antes, {apagadas} apagadas, {resolvidasDepois} depois)");
            checar(ChatRaspadinha.Bonus("bn-1") is not null,
                "BC-28 a faxina NÃO apaga o bônus (ele é o rastro do brinde entregue)");

            // ── COMANDA DOBRADA ENTRE DOIS CAIXAS DA MESMA LOJA ──────────────
            // O terminal B vê o mesmo quadro do WebSocket, chama a borda e recebe o bônus que o
            // terminal A já registrou. A PK do raspadinha_bonus é local a cada SQLite, então em B
            // a linha nasce nova, entra na lista de imprimir e sai a SEGUNDA comanda do mesmo
            // brinde: a loja entrega dois cookies.
            ConfigLojaPainel.Aplicar(cx, linhaPainel, new DateTime(2026, 9, 22, 17, 0, 0));   // BC-23 desligou
            checar(ChatRaspadinha.LigadoNaLoja(cx), "BC-29a a captura volta a ficar ligada para o resto da suíte");
            ChatRaspadinha.Registrar(cx, Msg("k-outro", "ad-nnn444"));
            var rOutro = await ChatRaspadinha.ResolverNaFilaAsync("k-outro",
                Responde(200, """{"ok":true,"ja_registrado":true,"bonus":{"id":"bn-9","redeem_code":"AD-NNN444","premio_nome":"Brownie"}}"""),
                agora);
            checar(rOutro.Ok == true && ChatRaspadinha.Bonus("bn-9") is not null,
                "BC-29 o bônus de outro terminal é gravado (o operador precisa saber que existe)");
            checar(ChatRaspadinha.ParaImprimir().All(b => b.Id != "bn-9"),
                "BC-30 mas ele NÃO tira papel sozinho: a comanda é de quem resgatou");
            checar(ChatRaspadinha.SemPapelHoje(agora).Any(b => b.Id == "bn-9"),
                "BC-31 e continua achável pelo Reimprimir, para o caixa que precisar do papel");

            // Reenviar a MINHA mensagem é diferente: o servidor devolve ja_registrado do MEU
            // resgate, e esse papel tem de sair (é a promessa da comanda quando a rede volta).
            ChatRaspadinha.Registrar(cx, Msg("k-meu", "ad-ppp555"));
            await ChatRaspadinha.ResolverNaFilaAsync("k-meu", Responde(0, null), agora);
            var rMeu = await ChatRaspadinha.ResolverNaFilaAsync("k-meu",
                Responde(200, """{"ok":true,"ja_registrado":true,"bonus":{"id":"bn-10","redeem_code":"AD-PPP555","premio_nome":"Cookie"}}"""),
                agora);
            checar(rMeu.Ok == true && ChatRaspadinha.ParaImprimir().Any(b => b.Id == "bn-10"),
                "BC-32 o reenvio da própria mensagem tira papel (a resposta é que se perdeu, não o resgate)");

            // ── A BOBINA ACABOU ──────────────────────────────────────────────
            // O claim era gravado antes do papel e nunca voltava: com a bobina acabada, todo bônus
            // menos o último ficava sem comanda e sem botão que o trouxesse.
            checar(ChatRaspadinha.ReivindicarImpressao("bn-10"), "BC-33 o claim sai antes do papel");
            ChatRaspadinha.AnotarFalhaDeImpressao("bn-10", "a impressora não respondeu");
            checar(ChatRaspadinha.ParaImprimir().Any(b => b.Id == "bn-10"),
                "BC-34 o papel que não saiu VOLTA para a lista de pendentes (bobina acabada não engole o brinde)");
            for (var i = 0; i < ChatRaspadinha.TetoDeImpressao + 1; i++)
            {
                ChatRaspadinha.ReivindicarImpressao("bn-10");
                ChatRaspadinha.AnotarFalhaDeImpressao("bn-10", "a impressora não respondeu");
            }
            checar(ChatRaspadinha.ParaImprimir().All(b => b.Id != "bn-10"),
                "BC-35 passado o teto ele para de tentar sozinho (impressora morta não vira metralhadora)");
            checar(ChatRaspadinha.SemPapelHoje(agora).Any(b => b.Id == "bn-10"),
                "BC-36 e aparece na lista que o botão Reimprimir oferece");
        }
        finally
        {
            Banco.CaminhoForcado = antes;
            SqliteConnection.ClearAllPools();
            try { File.Delete(db); } catch { }
        }
    }

    // ── 9. PELO FONTE ────────────────────────────────────────────────────────
    private static void Fonte(Action<bool, string> checar)
    {
        var raiz = Raiz();
        string Ler(params string[] partes) => raiz is null ? "" : File.ReadAllText(Path.Combine(new[] { raiz }.Concat(partes).ToArray()));
        var nucleo = Ler("Pdv.Nucleo", "ChatRaspadinha.cs");
        var servico = Ler("ServicoRaspadinhaChat.cs");
        var dren = Ler("Pdv.Nucleo", "Drenagem.cs");
        var tela = Ler("Telas", "ChatIfood.xaml.cs");

        checar(nucleo.Length > 0 && !Regex.IsMatch(nucleo, @"Vendas\.Finalizar|LinhaVenda|Fiscal\.|Emissor|AnonKey"),
            "FT-1 ChatRaspadinha.cs não conhece venda, nota nem a chave pública");
        // O CAIXA NÃO VALIDA CÓDIGO. Se um dia alguém puser aqui o alfabeto da raspadinha ou o
        // prefixo "AD-", a regra passa a existir em dois lugares e volta a divergir.
        checar(nucleo.Length > 0 && !nucleo.Contains("\"AD-\"", StringComparison.Ordinal)
               && !Regex.IsMatch(nucleo, @"ABCDEFGHJKMNPQRSTUVWXYZ|AD-\[A-Z"),
            "FT-2 ChatRaspadinha.cs não conhece o formato do código: quem acha e valida é o servidor");
        checar(servico.Length > 0 && !Regex.IsMatch(servico, @"NormalizarCodigo|raspadinha_redeem|validate_code"),
            "FT-3 o serviço não resgata nem valida nada por conta própria");
        checar(dren.Contains("ChatRaspadinha.TipoNaFila => await ChatRaspadinha.ResolverNaFilaAsync(", StringComparison.Ordinal),
            "FT-4 a Drenagem manda a mensagem para a resolução que respeita a loja desligada");
        checar(servico.Contains("ReivindicarImpressao(b.Id)", StringComparison.Ordinal),
            "FT-5 o papel só sai depois do claim (comanda dobrada é brinde dobrado)");
        checar(tela.Contains("OuvirMensagem(ChatCaptura.NormalizarFrame(p)", StringComparison.Ordinal)
               && !Regex.IsMatch(tela, @"_wsSent[\s\S]{0,400}OuvirMensagem"),
            "FT-6 só o quadro RECEBIDO vira mensagem; o que a loja envia não");
        // O diagnóstico da loja não pode gravar a mensagem do cliente.
        // A varredura é no ARQUIVO INTEIRO: o diagnóstico da loja não pode gravar a mensagem de
        // ninguém por caminho nenhum, e limitar a busca à vizinhança do DiagRaspadinha deixava a
        // garantia depender de onde o método estivesse escrito.
        checar(tela.Length > 0 && !Regex.IsMatch(tela, @"texto=\{m\.Texto|texto=\{txt|texto=\"" \+ m\.Texto"),
            "FT-7 o diagnóstico grava o TAMANHO do texto, nunca a mensagem do cliente");

        // ⚠️ O LEITOR DE DOM LÊ DE TRÁS PARA FRENTE. querySelectorAll devolve ordem de documento,
        // que num chat vai do mais antigo para o mais novo: parando nos primeiros candidatos, o
        // laço ficava com os balões velhos e o código, que é sempre a última fala, nunca era lido.
        var iLer = tela.IndexOf("window.pdvLerMensagens = function", StringComparison.Ordinal);
        var corpoLer = iLer > 0 ? tela[iLer..Math.Min(tela.Length, iLer + 3000)] : "";
        checar(corpoLer.Contains("for (var i = cands.length - 1; i >= 0", StringComparison.Ordinal),
            "FT-8 o leitor de DOM varre do FIM para o começo (a fala com o código é a última)");
        checar(corpoLer.Contains("vistos[assinatura]", StringComparison.Ordinal),
            "FT-9 o leitor de DOM não reenvia a cada 7 s o que já mandou");
        checar(tela.Contains("rolaSozinho(no)", StringComparison.Ordinal),
            "FT-10 o painel da conversa é o que ROLA sozinho, não o primeiro ancestral grande (a lista de conversas não entra)");

        // O aviso do bônus na tela do chat: o evento existia e ninguém assinava.
        checar(Regex.IsMatch(tela, @"ServicoRaspadinhaChat\.BonusNovo\s*\+="),
            "FT-11 a tela do chat assina o aviso do bônus novo");
        // A fala já vista sai antes de qualquer log e de qualquer abertura de banco.
        checar(tela.Contains("if (!_vistas.Add(chave)) return;", StringComparison.Ordinal),
            "FT-12 mensagem já entregue sai antes do log e do banco");
        // A comanda do brinde respeita a mesma política da comanda de cozinha.
        checar(servico.Contains("Impressoes.Politica(cx, Impressoes.Comanda)", StringComparison.Ordinal)
               && servico.Contains("politica == PoliticaImpressao.Nao", StringComparison.Ordinal),
            "FT-13 a loja que escolheu não imprimir não vê a comanda do brinde sair (e o claim não queima)");
        checar(!servico.Contains("politica != PoliticaImpressao.Automatico", StringComparison.Ordinal),
            "FT-13b mas 'perguntar' continua imprimindo: o brinde não tem card com botão, e é o pedido do dono");
        // O número curto do iFood é conferido contra os pedidos DESTA loja.
        checar(servico.Contains("PedidoDestaLoja(pedido)", StringComparison.Ordinal)
               && servico.Contains("origem = 'ifood'", StringComparison.Ordinal),
            "FT-14 o número do pedido só vai quando esta loja tem esse pedido");
        // A janela da fila: a venda não pode ficar atrás da mensagem de chat.
        checar(dren.Contains("JanelaPropria", StringComparison.Ordinal)
               && Regex.IsMatch(dren, @"tipo IN \('\{string\.Join\(""','"", principais\)\}'\)"),
            "FT-15 a janela principal da fila exclui os tipos com janela própria");
    }

    private static string? Raiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj"))) return dir.FullName;
        return null;
    }
}
