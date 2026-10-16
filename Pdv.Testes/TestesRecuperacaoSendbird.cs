using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A RECUPERAÇÃO DO CHAT PELO HISTÓRICO DO SENDBIRD (1.0.41, 16/10/2026, Savassi, pedido 2029).
///
/// O robô só via as mensagens que chegavam pelo WebSocket enquanto o Gestor estava aberto dentro do
/// caixa. Com o chat morto de 12:32 a 13:22 (atualização para a 1.0.40) o cliente mandou 3 códigos às
/// 13:21 e NADA foi capturado. Agora o caixa busca sozinho o histórico recente (REST api-&lt;appid&gt;
/// .sendbird.com/v3, com a sessão que o próprio Gestor abriu) e entrega ao mesmo funil de hoje.
///
///  RS  a sessão (pura): as três fontes, a prioridade, o entregador ignorado, a invalidação, o segredo
///  RQ  o quadro reconstruído do histórico dá os MESMOS campos e a MESMA chave do quadro do WebSocket
///  RJ  a janela, o filtro (nunca a fala da loja), os tetos, a marca d'água, o dedup e o recuo
///  RH  o serviço com HTTP de mentira: cabeçalhos e URLs exatos, 429, 401, timeout, prazo, reentrância,
///      portão, sem sessão
///  R5  o cenário do pedido 2029: o caixa sobe sem sessão, a sessão chega, a varredura entrega as 3 falas
///      uma vez só, o WebSocket depois não dobra, e nenhuma linha de diag leva a chave nem o texto
/// Nenhuma prova fala com a rede: o HTTP é um handler falso que grava cada pedido.
/// </summary>
public static class TestesRecuperacaoSendbird
{
    private const string Loja = TestesEnvioPeloSocket.Loja;
    private const string Chave = TestesEnvioPeloSocket.Chave;
    private const string Jwt = TestesEnvioPeloSocket.Jwt;
    private const string AppId = "6913AB";
    private const string Pedido = TestesQuadroSendbird.Pedido;
    private const string Savassi = TestesQuadroSendbird.MerchantSavassi;
    private const string Canal = TestesQuadroSendbird.Canal;
    private const string CanalEntregador = "sendbird_gc_" + Pedido + "_entregador-77";
    private const string Pedido2 = "4a1b2c3d-2222-4e5f-8a9b-aabbccddeeff";
    private const string Canal2 = "sendbird_gc_cm_" + Pedido2 + "_" + Savassi;
    private const string UrlApi = "https://api-6913ab.sendbird.com/v3/users/" + Loja + "/my_group_channels?limit=100";
    private const string Texto = "oi, ganhei na raspadinha AD-7KQ2MX";
    private static readonly DateTime T0 = new(2026, 10, 16, 13, 22, 0);

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        ASessao(checar);
        OQuadro(checar);
        AJanelaEOFiltro(checar);
        await OServicoAsync(checar);
        await OCenario2029Async(checar);
        PeloFonte(checar);
    }

    // ── ajudantes ────────────────────────────────────────────────────────────

    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();

    private static JsonElement J(string json)
    {
        using var d = JsonDocument.Parse(json);
        return d.RootElement.Clone();
    }

    /// <summary>Uma mensagem como o histórico REST a devolve (message_id, user.user_id, nickname, metadata objeto).</summary>
    private static string Rest(string tipo, string texto, string autor = "cli-77", string? userType = "CUSTOMER",
        long? id = 8812736123, long criadoMs = 1790000000000, string canal = Canal, string reqId = "", string customType = "",
        string data = "\"\"", string extra = "", string campoId = "message_id")
        => "{\"type\":\"" + tipo + "\"," + (id is null ? "" : "\"" + campoId + "\":" + id + ",")
           + "\"message\":" + JsonSerializer.Serialize(texto) + ",\"channel_url\":\"" + canal + "\",\"created_at\":" + criadoMs
           + ",\"req_id\":\"" + reqId + "\",\"custom_type\":\"" + customType + "\",\"data\":" + data + ",\"mention_type\":\"users\""
           + ",\"user\":{\"user_id\":\"" + autor + "\",\"nickname\":\"Ana\",\"profile_url\":\"\",\"metadata\":"
           + (userType is null ? "{}" : "{\"userType\":\"" + userType + "\"}") + "}" + extra + "}";

    private static string Mensagens(params string[] itens) => "{\"messages\":[" + string.Join(",", itens) + "]}";

    private static string CanalDaLista(string canal, long ultimaMs, long msgId, string autor, string? userType, int unread)
        => "{\"channel_url\":\"" + canal + "\",\"name\":\"\",\"unread_message_count\":" + unread + ",\"last_message\":{\"type\":\"MESG\",\"message_id\":" + msgId
           + ",\"created_at\":" + ultimaMs + ",\"message\":\"x\",\"user\":{\"user_id\":\"" + autor + "\",\"metadata\":"
           + (userType is null ? "{}" : "{\"userType\":\"" + userType + "\"}") + "}}}";

    private static string Lista(string next, params string[] canais) => "{\"channels\":[" + string.Join(",", canais) + "],\"next\":\"" + next + "\"}";

    private static string SinalJson(string modo)
        => "{\"ok\":true,\"agora\":\"2026-10-16T16:22:00Z\",\"modo\":\"" + modo + "\",\"palavra_modo\":\"desligado\",\"envio\":\"sdk\",\"comanda_onde\":\"kds\","
           + "\"merchant_ids\":[\"" + Savassi + "\"],\"intervalo_s\":60,\"pausado\":null,\"avisos\":[],\"saidas\":[],\"comandas\":[]}";

    /// <summary>O HTTP de mentira: grava cada pedido (URL e cabeçalhos, antes de o serviço descartá-lo) e responde o que o teste manda.</summary>
    private sealed class HttpFalso
    {
        public sealed record Pedido(string Url, IReadOnlyDictionary<string, string> Cabecalhos, string Metodo);
        public readonly List<Pedido> Pedidos = new();
        public Func<string, (int Status, string Corpo, int? RetryAfterS)> Responder = _ => (200, "{\"channels\":[]}", null);
        public TimeSpan Demora = TimeSpan.Zero;
        public IReadOnlyList<string> Urls { get { lock (Pedidos) return Pedidos.Select(p => p.Url).ToList(); } }
        public int Quantos { get { lock (Pedidos) return Pedidos.Count; } }

        public async Task<HttpResponseMessage> Enviar(HttpRequestMessage r, CancellationToken ct)
        {
            var cab = r.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var url = r.RequestUri!.ToString();
            lock (Pedidos) Pedidos.Add(new Pedido(url, cab, r.Method.Method));
            if (Demora > TimeSpan.Zero) await Task.Delay(Demora, ct);
            var (st, corpo, ra) = Responder(url);
            var resp = new HttpResponseMessage((HttpStatusCode)st) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };
            if (ra is { } s) resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
            return resp;
        }
    }

    private static (ServicoDeRecuperacao Servico, HttpFalso Http, List<string> Entregues, List<string> Diag, Func<DateTime> Relogio, Action<DateTime> Acertar)
        Montar(bool portao = true, string? usuarioDaLoja = null, TimeSpan? prazoChamada = null, TimeSpan? prazoVarredura = null)
    {
        var http = new HttpFalso();
        var entregues = new List<string>();
        var diag = new List<string>();
        var relogio = T0;
        var s = new ServicoDeRecuperacao
        {
            Http = http.Enviar,
            Agora = () => relogio,
            Entregar = q => { lock (entregues) entregues.Add(q); },
            Diag = l => { lock (diag) diag.Add(l); },
            Portao = () => portao,
            UsuarioDaLoja = () => usuarioDaLoja,
            ComRelogios = false,
            PrazoDaChamada = prazoChamada ?? RecuperacaoSendbird.PrazoDaChamada,
            PrazoDaVarredura = prazoVarredura ?? RecuperacaoSendbird.PrazoDaVarredura,
        };
        s.Ligar();
        return (s, http, entregues, diag, () => relogio, t => relogio = t);
    }

    private static CredencialSendbird CredUrl(DateTime em) => SessaoSendbird.DaUrlDoSocket(TestesEnvioPeloSocket.UrlLoja2, em)!;

    // ── RS: a sessão ─────────────────────────────────────────────────────────
    private static void ASessao(Action<bool, string> checar)
    {
        var c1 = SessaoSendbird.DaUrlDoSocket(TestesEnvioPeloSocket.UrlLoja2, T0);
        var c2 = SessaoSendbird.DaUrlDoSocket(TestesEnvioPeloSocket.UrlLoja2Cdp, T0);
        checar(c1 is { AppId: AppId, UserId: Loja, SessionKey: Chave, Fonte: "url" } && c2 is { AppId: AppId, UserId: Loja, SessionKey: Chave },
            "RS-1 a URL da reconexão do SDK (com e sem a barra) dá ai, user_id e key, fonte url");
        var semUsuario = SessaoSendbird.DaUrlDoSocket(TestesEnvioPeloSocket.UrlSemUsuario, T0);
        checar(semUsuario is { UserId: null } && SessaoSendbird.Melhor(new[] { semUsuario }, null) is null,
            "RS-1b a URL sem user_id não serve sozinha (sem a loja conhecida, a melhor é nenhuma)");
        checar(SessaoSendbird.DaUrlDoSocket(TestesEnvioPeloSocket.UrlOutro, T0) is null && SessaoSendbird.DaUrlDoSocket(null, T0) is null
               && SessaoSendbird.DaUrlDoSocket("lixo sem url", T0) is null,
            "RS-1c socket que não é do Sendbird, nulo ou lixo: nada, sem exceção");

        checar(SessaoSendbird.DaUrlDoSocket(TestesEnvioPeloSocket.UrlLoja, T0) is null,
            "RS-2 a primeira conexão (só access_token) NÃO vira credencial: o access_token nunca é a chave");
        checar(SessaoSendbird.DaUrlDoSocket("wss://ws-6913ab.sendbird.com/?ai=6913AB&user_id=" + Loja + "&key=" + Jwt, T0) is null
               && !SessaoSendbird.ChaveValida(Jwt) && !SessaoSendbird.ChaveValida("curta") && SessaoSendbird.ChaveValida(Chave),
            "RS-2b um JWT em key= também não vira chave; chave curta não vale");

        var logi = SessaoSendbird.DoLogi("LOGI{\"key\":\"" + Chave + "\",\"user_id\":\"" + Loja + "\",\"ekey\":\"outra\"}", "entregador-77", AppId, T0);
        checar(logi is { SessionKey: Chave, UserId: Loja, Fonte: "logi", AppId: AppId }, "RS-3 o LOGI dá key e user_id (o do próprio LOGI vence o da URL)");
        var logiSemUsuario = SessaoSendbird.DoLogi("LOGI{\"key\":\"" + Chave + "\"}", Loja, AppId, T0);
        checar(logiSemUsuario is { SessionKey: Chave, UserId: Loja }, "RS-3b LOGI sem user_id usa o user_id da URL daquela conexão");
        checar(SessaoSendbird.DoLogi("LOGI{\"user_id\":\"" + Loja + "\"}", Loja, AppId, T0) is null
               && SessaoSendbird.DoLogi("MESG{\"key\":\"" + Chave + "\"}", Loja, AppId, T0) is null
               && SessaoSendbird.DoLogi("LOGI{lixo", Loja, AppId, T0) is null && SessaoSendbird.DoLogi(null, Loja, AppId, T0) is null
               && SessaoSendbird.DoLogi("LOGI{\"key\":\"" + Chave + "\"}", Loja, null, T0) is null,
            "RS-3c LOGI sem key, MESG com key, lixo, nulo ou sem app id: null, sem exceção");

        var cab = J("{\"session-key\":\"" + Chave + "\",\"app-id\":\"" + AppId + "\",\"sb-user-agent\":\"JS/c4.19.9//\",\"SendBird\":\"JS,4.19.9," + AppId + "\",\"SB-SDK-User-Agent\":\"main_sdk_info=chat/js/4.19.9\",\"Accept\":\"application/json\",\"Cookie\":\"a=b\"}");
        var dc = SessaoSendbird.DosCabecalhos(UrlApi, cab, T0);
        checar(dc is { Credencial: { SessionKey: Chave, AppId: AppId, Fonte: "cabecalho", UserId: null } }
               && dc.Value.Cabecalhos is { SbUserAgent: "JS/c4.19.9//", SendBird: "JS,4.19.9," + AppId, SbSdkUserAgent: "main_sdk_info=chat/js/4.19.9", Accept: "application/json" },
            "RS-4 a chamada do SDK a api-<appid>.sendbird.com dá Session-Key e App-Id (nomes sem distinguir caixa) e guarda SB-User-Agent/SendBird/SB-SDK-User-Agent");
        checar(SessaoSendbird.DosCabecalhos("https://api-x.sendbird.com.evil/v3/x", cab, T0) is null
               && SessaoSendbird.DosCabecalhos("https://apix.sendbird.com/v3/x", cab, T0) is null
               && SessaoSendbird.DosCabecalhos("https://api-6913ab.sendbird.com.evil.example/v3/x", cab, T0) is null
               && SessaoSendbird.DosCabecalhos("https://gestordepedidos.ifood.com.br/chat/v1.0/auth", cab, T0) is null,
            "RS-4b só o host EXATAMENTE api-<appid>.sendbird.com (api-x.sendbird.com.evil e apix.sendbird.com ficam fora)");
        checar(SessaoSendbird.DosCabecalhos(UrlApi, J("{\"App-Id\":\"" + AppId + "\",\"Accept\":\"*/*\"}"), T0) is null
               && SessaoSendbird.DosCabecalhos(UrlApi, J("[1,2]"), T0) is null,
            "RS-4c sem Session-Key (ou headers que não são objeto): null");
        var semAppId = SessaoSendbird.DosCabecalhos("https://api-abc12.sendbird.com/v3/x", J("{\"Session-Key\":\"" + Chave + "\"}"), T0);
        checar(semAppId is { Credencial.AppId: "abc12" }, "RS-4d sem App-Id no cabeçalho, o app id sai do host");

        var url = new CredencialSendbird(AppId, Loja, "chave-url-00000001", "url", T0);
        var lg = new CredencialSendbird(AppId, Loja, "chave-logi-0000001", "logi", T0.AddSeconds(1));
        var cb = new CredencialSendbird(AppId, null, "chave-cab-00000001", "cabecalho", T0.AddSeconds(2));
        var cbVelho = new CredencialSendbird(AppId, null, "chave-cab-velha-001", "cabecalho", T0.AddSeconds(-5));
        var entregador = new CredencialSendbird(AppId, "entregador-77", "chave-entreg-00001", "logi", T0.AddSeconds(9));
        var m = SessaoSendbird.Melhor(new[] { url, lg, cb, cbVelho, entregador }, Loja);
        checar(m is { SessionKey: "chave-cab-00000001", UserId: Loja, Fonte: "cabecalho" },
            "RS-5 prioridade cabeçalho > logi > url; na mesma fonte a mais recente; o cabeçalho ganha o user_id da loja");
        checar(SessaoSendbird.Melhor(new[] { url, lg, entregador }, Loja) is { SessionKey: "chave-logi-0000001" }
               && SessaoSendbird.Melhor(new[] { entregador }, Loja) is null,
            "RS-5b a sessão de OUTRO usuário (o entregador) é ignorada, mesmo sendo a mais nova");
        checar(SessaoSendbird.Melhor(new[] { cb }, null) is null && SessaoSendbird.Melhor(new[] { url }, null) is { UserId: Loja },
            "RS-5c sem a loja conhecida, só credencial que diz o user_id serve (o cabeçalho sozinho não)");
        checar(SessaoSendbird.Melhor(new CredencialSendbird?[] { null, new(AppId, Loja, "x", "url", T0), new("", Loja, Chave, "url", T0) }, Loja) is null,
            "RS-5d nulo, chave curta e app id vazio não entram, sem exceção");

        var sessao = new SessaoSendbird();
        checar(sessao.Oferecer(url) && sessao.Atual(Loja) is { Fonte: "url" } && !sessao.Oferecer(url),
            "RS-6 oferecer a primeira credencial muda a melhor; a mesma de novo não muda");
        checar(sessao.Oferecer(cb) && sessao.Atual(Loja) is { Fonte: "cabecalho" } && sessao.Fonte(Loja) == "cabecalho", "RS-6b o cabeçalho que chega depois passa a valer");
        sessao.Invalidar("http_401");
        checar(sessao.Atual(Loja) is null && sessao.Fonte(Loja) == "nenhuma" && sessao.MotivoDaInvalidacao == "http_401",
            "RS-6c Invalidar zera a sessão e guarda só o motivo curto");
        checar(!sessao.Oferecer(cb) && !sessao.Oferecer(url) && sessao.Atual(Loja) is null,
            "RS-6d a chave invalidada não volta a entrar (nem pelo cabeçalho, nem pela URL)");
        var nova = new CredencialSendbird(AppId, Loja, "chave-nova-00000001", "url", T0.AddMinutes(1));
        checar(sessao.Oferecer(nova) && sessao.Atual(Loja) is { SessionKey: "chave-nova-00000001" }, "RS-6e uma chave de valor diferente reabre");
        sessao.Invalidar("<script>" + Chave);
        checar(sessao.MotivoDaInvalidacao == "invalidada", "RS-6f motivo fora do formato vira 'invalidada' (nunca carrega a chave)");
        var s2 = new SessaoSendbird();
        s2.Oferecer(url);
        var entregadorUrl = new CredencialSendbird(AppId, "entregador-77", "chave-entreg-url-01", "url", T0.AddSeconds(5));
        checar(s2.Oferecer(entregadorUrl) && s2.Atual(Loja) is { SessionKey: "chave-url-00000001" } && s2.Atual("entregador-77") is { SessionKey: "chave-entreg-url-01" },
            "RS-6g a URL do socket do entregador (outro usuário, mesma fonte) não sobrescreve a credencial da loja");
        checar(!s2.Oferecer(new CredencialSendbird(AppId, Loja, "chave-url-00000001", "logi", T0.AddSeconds(6))) && s2.Atual(Loja) is { Fonte: "logi" },
            "RS-6h a mesma chave vinda por outra fonte (o LOGI depois da URL) não é mudança, mas passa a valer pela fonte de mais prioridade");

        var str = CredUrl(T0).ToString();
        checar(!str.Contains(Chave) && str.Contains("fonte=url") && str.Contains("appid=" + AppId), "RS-7 o ToString da credencial não mostra a chave");
        var linha = "recuperacao sessao key=" + Chave + " token=" + Jwt + " fonte=url";
        var mascarada = ChatCaptura.MascararTexto(linha);
        checar(!mascarada.Contains(Chave) && !mascarada.Contains(Jwt) && mascarada.Contains("fonte=url"),
            "RS-7b a rede de segurança (MascararTexto) apaga a chave e o JWT de qualquer linha de diag");
        checar(!EnvioPeloSocket.Mascarar(Chave).Contains(Chave), "RS-7c EnvioPeloSocket.Mascarar também cobre a chave");
        var excecao = "";
        try
        {
            var sz = new SessaoSendbird();
            sz.Oferecer(null); sz.Oferecer(new CredencialSendbird("", "", "", "zzz", T0)); sz.Invalidar(null); sz.OferecerCabecalhos(null); _ = sz.Atual(null);
            _ = SessaoSendbird.DosCabecalhos(UrlApi, default, T0); _ = SessaoSendbird.AppIdDaUrl("::nada::"); _ = SessaoSendbird.EhApiDoSendbird("::nada::");
        }
        catch (Exception ex) { excecao = ex.ToString(); }
        checar(excecao.Length == 0, "RS-7d nada da sessão lança (nulo, lixo, fonte desconhecida)");
    }

    // ── RQ: o quadro reconstruído ────────────────────────────────────────────
    private static void OQuadro(Action<bool, string> checar)
    {
        var rest = RecuperacaoSendbird.QuadroDoHistorico(J(Rest("MESG", Texto)));
        var ws = TestesQuadroSendbird.Mesg(Texto);
        var a = QuadroSendbird.Ler(rest);
        var b = QuadroSendbird.Ler(ws);
        checar(rest is not null && rest.StartsWith("MESG{", StringComparison.Ordinal) && !rest.Contains('\n'),
            "RQ-1 a mensagem do histórico vira o quadro MESG{...} sem quebra de linha");
        checar(a is not null && b is not null && a.MsgId == b.MsgId && a.AutorId == b.AutorId && a.UserType == b.UserType && a.Canal == b.Canal
               && a.CriadoEmMs == b.CriadoEmMs && a.Texto == b.Texto && a.Whisper == b.Whisper,
            $"RQ-1b QuadroSendbird.Ler dá MsgId/AutorId/UserType/Canal/CriadoEmMs/Texto iguais aos do quadro do WebSocket ({a?.MsgId}/{a?.AutorId}/{a?.UserType})");
        var arqA = ChatArquivo.LerQuadro(rest, Loja, null);
        var arqB = ChatArquivo.LerQuadro(ws, Loja, null);
        checar(arqA is not null && arqB is not null && arqA.Lado == arqB.Lado && arqA.MsgId == arqB.MsgId && arqA.Texto == arqB.Texto && arqA.AutorId == arqB.AutorId,
            "RQ-1c ChatArquivo.LerQuadro também dá o mesmo lado, id, autor e texto");
        var nA = ChatCaptura.NormalizarFrame(rest);
        var nB = ChatCaptura.NormalizarFrame(ws);
        checar(nA is not null && nB is not null && nA.Texto == nB.Texto && nA.ConversaId == nB.ConversaId && nA.Autor == nB.Autor,
            "RQ-1d e o normalizador do fluxo legado lê o mesmo texto, conversa e autor");

        checar(a is not null && b is not null && ConversaRaspadinha.Chave(a) == "sb:8812736123" && ConversaRaspadinha.Chave(a) == ConversaRaspadinha.Chave(b)
               && arqA is not null && arqB is not null && ChatArquivo.Chave(arqA) == ChatArquivo.Chave(arqB) && ChatArquivo.Chave(arqA) == "sb:8812736123"
               && RecuperacaoSendbird.ChaveDoQuadro(rest!, Loja) == "sb:8812736123",
            "RQ-2 a chave é a MESMA nos dois caminhos: sb:<msg_id> (é ela que o ERP deduplica)");

        var comMsgId = RecuperacaoSendbird.QuadroDoHistorico(J(Rest("MESG", Texto, campoId: "msg_id", id: 77)));
        checar(QuadroSendbird.Ler(comMsgId)?.MsgId == "77", "RQ-3 msg_id no histórico também é aceito");
        var zero = RecuperacaoSendbird.QuadroDoHistorico(J(Rest("MESG", Texto, id: 0)));
        var semId = QuadroSendbird.Ler(zero);
        var semIdWs = QuadroSendbird.Ler(TestesQuadroSendbird.Mesg(Texto, msgId: null));
        checar(semId is { MsgId: null } && semIdWs is not null && ConversaRaspadinha.Chave(semId).StartsWith("h:", StringComparison.Ordinal)
               && ConversaRaspadinha.Chave(semId) == ConversaRaspadinha.Chave(semIdWs),
            "RQ-3b message_id 0 vira nulo, a chave cai em h: e continua igual à do WebSocket sem msg_id");

        var file = RecuperacaoSendbird.QuadroDoHistorico(J("{\"type\":\"FILE\",\"message_id\":78,\"channel_url\":\"" + Canal + "\",\"created_at\":1790000000500,\"message\":\"\",\"file\":{\"url\":\"https://file.sendbird.com/x.jpg?auth=SEGREDO\",\"name\":\"IMG_1.jpg\",\"type\":\"image/jpeg\",\"size\":1234},\"user\":{\"user_id\":\"cli-77\",\"nickname\":\"Ana\",\"metadata\":{\"userType\":\"CUSTOMER\"}}}"));
        var foto = ChatArquivo.LerQuadro(file, Loja, null);
        checar(file is not null && file.StartsWith("FILE{", StringComparison.Ordinal) && foto is { Comando: "FILE", TemMidia: true, Texto: "[foto]", MsgId: "78", Lado: LadoArquivo.Cliente },
            "RQ-4 FILE com file aninhado espelha url/name/type na raiz: o arquivo vê [foto] com tem_midia");

        var brdm = RecuperacaoSendbird.QuadroDoHistorico(J(Rest("BRDM", "Quero adicionar observação ao pedido", autor: "ifood-bot", userType: null, id: 79,
            customType: "summary", data: JsonSerializer.Serialize("{\"fields\":[{\"type\":\"input\",\"initialValue\":\"sem cebola, por favor\"}]}"))));
        var obs = QuadroSendbird.ObservacaoNoCartao(brdm);
        checar(brdm is not null && ChatArquivo.LerQuadro(brdm, Loja, null) is { Comando: "BRDM", Lado: LadoArquivo.Ifood, CustomType: "summary" }
               && obs is { Texto: "sem cebola, por favor", Origem: "initialValue", OrderId: Pedido },
            "RQ-5 BRDM do histórico: lado iFood no arquivo e ObservacaoNoCartao continua achando o initialValue");
        foreach (var cmd in new[] { "ADMM", "AEDI" })
            checar(RecuperacaoSendbird.QuadroDoHistorico(J(Rest(cmd, "aviso", autor: "adm", userType: null, id: 80))) is { } q && ChatArquivo.LerQuadro(q, Loja, null) is { Lado: LadoArquivo.Ifood },
                $"RQ-5b {cmd} do histórico é lado iFood");

        var whisper = RecuperacaoSendbird.QuadroDoHistorico(J(Rest("MESG", "psiu", id: 81, data: JsonSerializer.Serialize("{\"whisperMode\":true}"))));
        checar(QuadroSendbird.Ler(whisper) is { Whisper: true }, "RQ-6 o sussurro (data.whisperMode) é preservado");

        checar(RecuperacaoSendbird.QuadroDoHistorico(J(Rest("READ", "x"))) is null
               && RecuperacaoSendbird.QuadroDoHistorico(J("{\"type\":\"MESG\",\"message_id\":1,\"message\":\"x\"}")) is null
               && RecuperacaoSendbird.QuadroDoHistorico(J("{\"message_id\":1,\"message\":\"x\",\"channel_url\":\"c\"}")) is null
               && RecuperacaoSendbird.QuadroDoHistorico(J("[1,2]")) is null && RecuperacaoSendbird.QuadroDoHistorico(J("\"texto\"")) is null,
            "RQ-7 tipo desconhecido, sem channel_url, sem type, array ou texto: null, sem exceção");
        var shape = ChatCaptura.DescreverShape(rest);
        checar(shape.StartsWith("MESG { ", StringComparison.Ordinal) && shape.Contains("msg_id:number") && !shape.Contains("7KQ2MX"),
            "RQ-8 o shape do quadro reconstruído sai sem valores (a linha única de chat-raspadinha.txt)");
    }

    // ── RJ: janela, filtro, marca, dedup, recuo ──────────────────────────────
    private static void AJanelaEOFiltro(Action<bool, string> checar)
    {
        var agora = T0;
        var marcas = new MarcasDAgua();
        var lista = J(Lista("",
            CanalDaLista(Canal, Ms(agora.AddMinutes(-5)), 101, "cli-77", "CUSTOMER", 3),
            CanalDaLista(CanalEntregador, Ms(agora.AddMinutes(-1)), 102, "entregador-77", null, 5),
            CanalDaLista(Canal2, Ms(agora.AddMinutes(-2)), 103, Loja, "MERCHANT", 0),
            CanalDaLista("sendbird_gc_cm_" + Pedido2.Replace('4', '5') + "_" + Savassi, Ms(agora.AddHours(-13)), 104, "cli-9", "CUSTOMER", 1),
            "{\"channel_url\":\"" + Canal2.Replace('4', '6') + "\",\"unread_message_count\":1}"));
        var plano = RecuperacaoSendbird.Planejar(lista, marcas, Loja, agora);
        checar(plano.Count == 1 && plano[0].Canal == Canal && plano[0].UltimoMsgId == "101",
            $"RJ-1 só o canal de pedido com last_message de cliente dentro da janela entra; entregador, loja sem não lidas, fora da janela e sem last_message ficam ({plano.Count})");
        checar(plano[0].MessageTs == Ms(agora) - 12 * 3600 * 1000L, "RJ-2 na primeira vez (sem marca) a leitura começa em agora - 12 h");
        var listaLojaNaoLida = J(Lista("", CanalDaLista(Canal2, Ms(agora.AddMinutes(-2)), 103, Loja, "MERCHANT", 2)));
        checar(RecuperacaoSendbird.Planejar(listaLojaNaoLida, marcas, Loja, agora).Count == 1,
            "RJ-1b a conversa cuja última fala é da loja mas tem não lidas também entra");

        marcas.Avancar(Canal, Ms(agora.AddMinutes(-5)), "101", agora);
        checar(RecuperacaoSendbird.Planejar(lista, marcas, Loja, agora).Count == 0, "RJ-1c com a marca na última mensagem, nada a ler");
        marcas.Avancar(Canal, Ms(agora.AddMinutes(-9)), "90", agora);
        checar(marcas.Marca(Canal) is { MsgId: "101" }, "RJ-1d a marca nunca anda para trás");
        var marcasVelhas = new MarcasDAgua();
        marcasVelhas.Avancar(Canal, Ms(agora.AddMinutes(-9)), "90", agora);
        var p2 = RecuperacaoSendbird.Planejar(lista, marcasVelhas, Loja, agora);
        checar(p2.Count == 1 && p2[0].MessageTs == Ms(agora.AddMinutes(-9)), "RJ-1e com marca anterior, a leitura começa na marca (e o include=true cobre a própria)");

        var msgs = J(Mensagens(
            Rest("MESG", "terceira", id: 203, criadoMs: Ms(agora.AddSeconds(-20))),
            Rest("MESG", "da loja pelo user", autor: Loja, userType: "MERCHANT", id: 201, criadoMs: Ms(agora.AddSeconds(-50))),
            Rest("MESG", "do caixa pelo req_id", autor: "outro", userType: null, id: 204, reqId: "pdv-abc-123", criadoMs: Ms(agora.AddSeconds(-30))),
            Rest("MESG", "merchant sem id igual", autor: "gerente", userType: "MERCHANT", id: 205, criadoMs: Ms(agora.AddSeconds(-25))),
            Rest("MESG", "primeira", id: 200, criadoMs: Ms(agora.AddSeconds(-55))),
            Rest("BRDM", "Quero adicionar observação ao pedido", autor: "bot", userType: null, id: 199, customType: "summary", criadoMs: Ms(agora.AddSeconds(-60))),
            Rest("MESG", "segunda", id: 202, criadoMs: Ms(agora.AddSeconds(-40))),
            Rest("MESG", "antes da marca", id: 100, criadoMs: Ms(agora.AddMinutes(-30))),
            Rest("READ", "x", id: 300, criadoMs: Ms(agora))));
        var quadros = RecuperacaoSendbird.FiltrarEOrdenar(msgs, Loja, Ms(agora.AddMinutes(-20)));
        var ids = quadros.Select(q => QuadroSendbird.Ler(q)?.MsgId ?? ChatArquivo.LerQuadro(q, Loja, null)?.MsgId).ToList();
        checar(ids.SequenceEqual(new[] { "199", "200", "202", "203" }),
            $"RJ-3 fica de fora a fala da loja (user_id), a do caixa (req_id pdv...), a MERCHANT e a anterior à marca; o resto sai em ordem de created_at ({string.Join(",", ids)})");

        var muitas = J(Mensagens(Enumerable.Range(0, 150).Select(i => Rest("MESG", "m" + i, id: 1000 + i, criadoMs: Ms(agora.AddSeconds(-300 + i)))).ToArray()));
        checar(RecuperacaoSendbird.FiltrarEOrdenar(muitas, Loja, 0).Count == 100, "RJ-4 teto de 100 mensagens por canal por varredura");
        var muitos = J(Lista("", Enumerable.Range(0, 25).Select(i => CanalDaLista("sendbird_gc_cm_" + Guid.NewGuid() + "_" + Savassi, Ms(agora.AddMinutes(-i)), 500 + i, "cli", "CUSTOMER", 1)).ToArray()));
        checar(RecuperacaoSendbird.Planejar(muitos, new MarcasDAgua(), Loja, agora).Count == 20, "RJ-4b teto de 20 canais por varredura (o resto fica para a próxima)");

        var v = new Varredura
        {
            Http = (_, _) => throw new InvalidOperationException("não deve chamar"),
            Agora = () => agora, Entregar = _ => { }, Credencial = () => CredUrl(T0),
        };
        v.Viu(TestesQuadroSendbird.Mesg("vi pelo ws", msgId: 101, criadoMs: Ms(agora.AddMinutes(-5))));
        checar(v.Marcas.Marca(Canal) is { MsgId: "101" } && v.Dedup.Conhece("sb:101"), "RJ-5 o quadro do WebSocket avança a marca do canal e ensina o dedup");
        checar(RecuperacaoSendbird.Planejar(lista, v.Marcas, Loja, agora).Count == 0, "RJ-5b e a varredura seguinte não pede nada daquele canal");
        v.Viu("PING"); v.Viu(null); v.Viu("MESG{lixo");
        checar(v.Marcas.Quantas == 1, "RJ-5c quadro que não é mensagem (ou lixo) não cria marca nem lança");

        checar(v.Dedup.Nova("sb:555") && !v.Dedup.Nova("sb:555") && !v.Dedup.Nova("sb:101"), "RJ-6 o dedup: a mesma chave entra uma vez só, venha do WS ou do REST");
        var d = new DedupDeMensagens();
        for (var i = 0; i < DedupDeMensagens.Teto + 10; i++) d.Nova("sb:" + i);
        checar(d.Quantas == DedupDeMensagens.Teto && !d.Conhece("sb:0") && d.Conhece("sb:" + (DedupDeMensagens.Teto + 9)), "RJ-6b a fila do dedup tem teto de 5000 (as mais velhas saem)");
        var mm = new MarcasDAgua();
        for (var i = 0; i < MarcasDAgua.Teto + 5; i++) mm.Avancar("sendbird_gc_cm_" + i + "_x", 1, "1", agora);
        checar(mm.Quantas <= MarcasDAgua.Teto, "RJ-6c a marca d'água tem teto de 2000 canais");
        mm.Avancar("velho", 1, "1", agora.AddHours(-25));
        mm.Faxina(agora);
        checar(mm.Marca("velho") is null, "RJ-6d a faxina esquece canal sem novidade há mais de 24 h");

        var r = new Recuo();
        var seq = Enumerable.Range(0, 7).Select(_ => r.Falhou(agora)).ToList();
        checar(seq.SequenceEqual(new[] { 10, 20, 40, 80, 160, 300, 300 }), $"RJ-7 o recuo é 10, 20, 40, 80, 160, 300 e para em 300 ({string.Join(",", seq)})");
        checar(r.Ativo(agora.AddSeconds(299)) && !r.Ativo(agora.AddSeconds(301)) && r.SegundosRestantes(agora.AddSeconds(100)) == 200, "RJ-7b o recuo vale até o prazo e diz quanto falta");
        r.Deu();
        checar(r.Falhou(agora) == 10 && !r.Ativo(agora.AddSeconds(11)), "RJ-7c o primeiro 2xx zera: a próxima falha volta a 10 s");
        checar(r.Falhou(agora, 120) == 120 && r.Falhou(agora, 5) == 40, "RJ-7d Retry-After maior que o degrau é respeitado; menor, vale o degrau");
    }

    // ── RH: o serviço com HTTP de mentira ────────────────────────────────────
    private static async Task OServicoAsync(Action<bool, string> checar)
    {
        var canalPlanejado = Lista("", CanalDaLista(Canal, Ms(T0.AddMinutes(-3)), 301, "cli-77", "CUSTOMER", 3));
        string Trio() => Mensagens(
            Rest("MESG", "um", id: 301, criadoMs: Ms(T0.AddMinutes(-5))),
            Rest("MESG", "dois", id: 302, criadoMs: Ms(T0.AddMinutes(-4))),
            Rest("MESG", "três", id: 303, criadoMs: Ms(T0.AddMinutes(-3))));
        (int, string, int?) Padrao(string url) => url.Contains("/messages?") ? (200, Trio(), null) : (200, canalPlanejado, null);

        // RH-1 cabeçalhos
        {
            var (s, http, _, _, _, _) = Montar();
            s.OferecerCabecalhos(new CabecalhosDoSdk("JS/c4.19.9//", "JS,4.19.9," + AppId, "main_sdk_info=chat/js/4.19.9", "application/json"));
            s.OferecerCredencial(CredUrl(T0));
            http.Responder = Padrao;
            var r = await s.VarrerAsync();
            var p = http.Pedidos.FirstOrDefault();
            var esperados = new[] { "App-Id", "Session-Key", "Accept", "SB-User-Agent", "SendBird", "SB-SDK-User-Agent", "Request-Sent-Timestamp" };
            checar(r is { Erro: null, Chamou: true } && p is not null && p.Metodo == "GET"
                   && p.Cabecalhos.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(esperados.OrderBy(k => k, StringComparer.Ordinal))
                   && p.Cabecalhos["Session-Key"] == Chave && p.Cabecalhos["App-Id"] == AppId && p.Cabecalhos["Accept"] == "application/json"
                   && p.Cabecalhos["SB-User-Agent"] == "JS/c4.19.9//" && p.Cabecalhos["SendBird"] == "JS,4.19.9," + AppId
                   && p.Cabecalhos["SB-SDK-User-Agent"] == "main_sdk_info=chat/js/4.19.9" && long.TryParse(p.Cabecalhos["Request-Sent-Timestamp"], out var ts) && ts > 1_700_000_000_000,
                $"RH-1 GET com EXATAMENTE os cabeçalhos do SDK (App-Id, Session-Key, Accept, SB-User-Agent, SendBird, SB-SDK-User-Agent, Request-Sent-Timestamp) e nenhum outro ({string.Join(",", p?.Cabecalhos.Keys ?? Array.Empty<string>())})");
            checar(p is not null && !p.Cabecalhos.ContainsKey("Authorization") && !p.Cabecalhos.ContainsKey("Cookie") && !p.Cabecalhos.ContainsKey("Origin"),
                "RH-1b sem Authorization, sem cookie, sem Origin");
            var (s2, http2, _, _, _, _) = Montar();
            s2.OferecerCredencial(CredUrl(T0));
            http2.Responder = Padrao;
            await s2.VarrerAsync();
            var p2 = http2.Pedidos.First();
            checar(!p2.Cabecalhos.ContainsKey("SB-SDK-User-Agent") && p2.Cabecalhos["SB-User-Agent"] == "JS/c4.19.9//" && p2.Cabecalhos["SendBird"] == "JS,4.19.9," + AppId,
                "RH-1c sem captura do SDK: o mínimo (SB-User-Agent JS/c4.19.9//, SendBird JS,4.19.9,<appid>) e sem SB-SDK-User-Agent");
            s.Dispose(); s2.Dispose();
        }

        // RH-2 URLs
        {
            var (s, http, entregues, _, _, _) = Montar();
            s.OferecerCredencial(CredUrl(T0));
            http.Responder = Padrao;
            await s.VarrerAsync();
            var urls = http.Urls;
            var l = "https://api-6913ab.sendbird.com/v3/users/" + Loja + "/my_group_channels?limit=100&order=latest_last_message&show_member=false&show_empty=false&unread_filter=unread_message";
            var m = "https://api-6913ab.sendbird.com/v3/group_channels/" + Uri.EscapeDataString(Canal) + "/messages?message_ts=" + (Ms(T0) - 12 * 3600 * 1000L) + "&prev_limit=0&next_limit=100&include=true&reverse=false";
            checar(urls.Count == 2 && urls[0] == l && urls[1] == m, $"RH-2 L e M com a URL e os parâmetros exatos (L depois M) [{string.Join(" | ", urls.Select(ChatCaptura.MascararUrl))}]");
            checar(RecuperacaoSendbird.UrlLista(AppId, "loja/1 2", false).Contains("/users/loja%2F1%202/my_group_channels?") && !RecuperacaoSendbird.UrlLista(AppId, Loja, false).Contains("unread_filter")
                   && RecuperacaoSendbird.UrlLista(AppId, Loja, true, "tok/1").EndsWith("&token=tok%2F1"),
                "RH-2b user_id, channel_url e o token da página vão codificados; sem o unread_filter quando o servidor o recusou");
            checar(entregues.Count == 3 && entregues.Select(q => QuadroSendbird.Ler(q)!.MsgId).SequenceEqual(new[] { "301", "302", "303" }),
                "RH-3 lista + mensagens: Entregar é chamado 3 vezes, na ordem");
            var antes = http.Quantos;
            await s.VarrerAsync();
            checar(http.Quantos == antes + 1 && entregues.Count == 3 && http.Urls.Last() == l, "RH-3b a varredura seguinte só lista (a marca avançou): nada a ler, nada entregue");
            // o unread_filter recusado com 400: repete sem ele e lembra
            var (s3, http3, _, diag3, _, _) = Montar();
            s3.OferecerCredencial(CredUrl(T0));
            http3.Responder = u => u.Contains("unread_filter") ? (400, "{\"message\":\"bad\",\"code\":400108}", null) : Padrao(u);
            var r3 = await s3.VarrerAsync();
            checar(r3 is { Erro: null } && http3.Urls.Count == 3 && http3.Urls[0].Contains("unread_filter") && !http3.Urls[1].Contains("unread_filter") && diag3.Any(d => d.Contains("sem unread_filter")),
                "RH-3c 400 no unread_filter: repete a lista sem ele na hora e anota");
            await s3.VarrerAsync();
            checar(!http3.Urls.Last().Contains("unread_filter"), "RH-3d e lembra na sessão que ele não é aceito");
            s.Dispose(); s3.Dispose();
        }

        // RH-4 429
        {
            var (s, http, _, diag, _, acertar) = Montar();
            s.OferecerCredencial(CredUrl(T0));
            http.Responder = _ => (429, "{\"message\":\"too many\"}", null);
            var r = await s.VarrerAsync();
            checar(r is { Erro: "http_429" } && s.Estado() is { Erro: "http_429", RecuoS: 10 } && diag.Any(d => d.Contains("erro=http_429 recuo=10s")),
                "RH-4 429: erro http_429 e recuo de 10 s no estado e no diag");
            acertar(T0.AddSeconds(5));
            var antes = http.Quantos;
            var r2 = await s.VarrerAsync();
            checar(r2 is { Erro: "recuo", Chamou: false } && http.Quantos == antes, "RH-4b antes do prazo do recuo nenhuma chamada sai");
            acertar(T0.AddSeconds(11));
            await s.VarrerAsync();
            checar(http.Quantos == antes + 1 && s.Estado().RecuoS == 20, "RH-4c passado o prazo chama de novo; a segunda falha dobra para 20 s");
            http.Responder = _ => (503, "", 90);
            acertar(T0.AddSeconds(40));
            await s.VarrerAsync();
            checar(s.Estado() is { Erro: "http_5xx", RecuoS: 90 }, "RH-4d 5xx com Retry-After maior que o degrau: espera o Retry-After");
            s.Dispose();
        }

        // RH-5 401
        {
            var (s, http, _, diag, _, acertar) = Montar();
            s.OferecerCredencial(CredUrl(T0));
            http.Responder = _ => (401, "{\"message\":\"Unauthorized\",\"code\":400302}", null);
            var r = await s.VarrerAsync();
            checar(r is { Erro: "sessao_invalida" } && s.Estado() is { Erro: "sessao_invalida", Fonte: "nenhuma" } && s.Sessao.Atual(null) is null,
                "RH-5 401: a sessão é invalidada na hora");
            acertar(T0.AddMinutes(1));
            var antes = http.Quantos;
            var r2 = await s.VarrerAsync();
            s.OferecerCredencial(CredUrl(T0.AddMinutes(1)));
            var r3 = await s.VarrerAsync();
            checar(r2 is { Erro: "sem_sessao", Chamou: false } && r3 is { Erro: "sem_sessao", Chamou: false } && http.Quantos == antes,
                "RH-5b zero chamadas até chegar credencial NOVA (a mesma chave, por URL de novo, não reabre)");
            s.OferecerCredencial(new CredencialSendbird(AppId, Loja, "chave-nova-depois-401", "logi", T0.AddMinutes(2)));
            var agendada = s.Agendada;
            http.Responder = Padrao;
            var r4 = await s.VarrerAsync();
            checar(r4 is { Erro: null, Chamou: true } && http.Pedidos.Last().Cabecalhos["Session-Key"] == "chave-nova-depois-401" && agendada == ServicoDeRecuperacao.EsperaDaCredencial,
                "RH-5c o LOGI novo reabre (varredura agendada em 2 s) e a chamada sai com a chave nova");
            var (s5, http5, _, _, _, _) = Montar();
            s5.OferecerCredencial(CredUrl(T0));
            http5.Responder = _ => (200, "{\"message\":\"Session key expired\",\"code\":400310}", null);
            var r5 = await s5.VarrerAsync();
            checar(r5 is { Erro: "sessao_invalida" } && s5.Sessao.Atual(null) is null, "RH-5d 2xx com corpo {code:4003xx} também invalida a sessão");
            checar(diag.All(d => !d.Contains(Chave)), "RH-5e nenhuma linha de diag do 401 leva a chave");
            s.Dispose(); s5.Dispose();
        }

        // RH-6 timeout por chamada
        {
            var (s, http, _, diag, _, _) = Montar(prazoChamada: TimeSpan.FromMilliseconds(250));
            s.OferecerCredencial(CredUrl(T0));
            http.Demora = TimeSpan.FromSeconds(3);
            var relogio = System.Diagnostics.Stopwatch.StartNew();
            var r = await s.VarrerAsync();
            checar(r is { Erro: "timeout" } && relogio.ElapsedMilliseconds < 2500 && s.Estado() is { Erro: "timeout", RecuoS: 10 } && diag.Any(d => d.Contains("erro=timeout")),
                $"RH-6 chamada que demora mais que o prazo: erro timeout, a varredura termina na hora e entra em recuo ({relogio.ElapsedMilliseconds} ms)");
            s.Dispose();
        }

        // RH-7 prazo da varredura inteira
        {
            var (s, http, entregues, diag, _, acertar) = Montar(prazoVarredura: TimeSpan.FromMilliseconds(450));
            s.OferecerCredencial(CredUrl(T0));
            var canais = Enumerable.Range(0, 5).Select(i => "sendbird_gc_cm_" + Guid.NewGuid() + "_" + Savassi).ToList();
            http.Responder = u => u.Contains("/messages?")
                ? (200, Mensagens(Rest("MESG", "oi", id: 400 + canais.FindIndex(c => u.Contains(Uri.EscapeDataString(c))), criadoMs: Ms(T0.AddMinutes(-2)), canal: canais.First(c => u.Contains(Uri.EscapeDataString(c))))), null)
                : (200, Lista("", canais.Select((c, i) => CanalDaLista(c, Ms(T0.AddMinutes(-2)), 400 + i, "cli", "CUSTOMER", 1)).ToArray()), null);
            http.Demora = TimeSpan.FromMilliseconds(150);
            var r = await s.VarrerAsync();
            checar(r is { Completa: false, Erro: null } && entregues.Count is >= 1 and < 5 && diag.Any(d => d.Contains("parou no prazo da varredura")),
                $"RH-7 a varredura inteira para no prazo (30 s na loja) com {entregues.Count} de 5 canais lidos e anota");
            http.Demora = TimeSpan.Zero;
            acertar(T0.AddSeconds(10));
            var r2 = await s.VarrerAsync();
            checar(r2 is { Completa: true, Erro: null } && entregues.Count == 5 && entregues.Select(q => QuadroSendbird.Ler(q)!.MsgId).Distinct().Count() == 5,
                "RH-7b a próxima varredura retoma o que faltou, sem repetir o que já saiu");
            s.Dispose();
        }

        // RH-8 reentrância
        {
            var (s, http, _, _, _, _) = Montar();
            s.OferecerCredencial(CredUrl(T0));
            http.Responder = Padrao;
            http.Demora = TimeSpan.FromMilliseconds(300);
            var a = s.VarrerAsync("relogio");
            var b = s.VarrerAsync("gatilho");
            var ra = await a; var rb = await b;
            checar((ra is null) != (rb is null) && s.Puladas == 1 && http.Quantos == 2, "RH-8 dois tiques juntos = uma varredura só (o outro volta sem chamar)");
            s.Dispose();
        }

        // RH-9 portão fechado
        {
            var (s, http, _, _, _, _) = Montar(portao: false);
            s.OferecerCredencial(CredUrl(T0));
            var r = await s.VarrerAsync();
            checar(r is { Erro: "portao", Chamou: false } && http.Quantos == 0 && s.Estado().Varreduras == 0, "RH-9 com o portão fechado (captura desligada, sinal inativo, arquivo em silêncio) zero chamadas");
            s.Dispose();
        }

        // RH-10 sem credencial
        {
            var (s, http, _, _, _, _) = Montar();
            var r = await s.VarrerAsync();
            checar(r is { Erro: "sem_sessao", Chamou: false } && http.Quantos == 0 && s.Estado() is { Erro: "sem_sessao", Fonte: "nenhuma" },
                "RH-10 sem credencial: zero chamadas e erro sem_sessao no estado");
            s.OferecerCredencial(new CredencialSendbird(AppId, null, Chave, "cabecalho", T0));
            var r2 = await s.VarrerAsync();
            checar(r2 is { Erro: "sem_sessao", Chamou: false }, "RH-10b só o cabeçalho (sem o user_id da loja conhecido) ainda não dá para listar");
            var (s2, http2, _, _, _, _) = Montar(usuarioDaLoja: Loja);
            s2.OferecerCredencial(new CredencialSendbird(AppId, null, Chave, "cabecalho", T0));
            http2.Responder = Padrao;
            var r3 = await s2.VarrerAsync();
            checar(r3 is { Erro: null, Chamou: true } && http2.Urls[0].Contains("/users/" + Loja + "/"), "RH-10c com a loja aprendida pelo canal de cliente, o cabeçalho sozinho basta");
            s.Dispose(); s2.Dispose();
        }

        // o estado do sinal (lista branca)
        {
            var (s, http, _, _, _, _) = Montar();
            s.OferecerCredencial(CredUrl(T0));
            http.Responder = Padrao;
            await s.VarrerAsync();
            var e = s.Estado() with { RestRecebidas = 3, RestEntregues = 3 };
            var json = System.Text.Json.Nodes.JsonNode.Parse(ConversaRaspadinha.EstadoDoSinal("logado", true, null, 0, null, null, null, recuperacao: e))!;
            var rc = json["recuperacao"]!;
            checar((string?)rc["fonte"] == "url" && (int?)rc["canais"] == 1 && (int?)rc["canais_lidos"] == 1 && (int?)rc["mensagens_novas"] == 3 && rc["erro"] is null
                   && (int?)rc["varreduras"] == 1 && (int?)rc["recuo_s"] == 0 && rc["ultima_ok_em"] is not null && (int?)rc["rest_recebidas"] == 3,
                "RH-11 estado.recuperacao no sinal: fonte, canais, canais_lidos, mensagens_novas, erro, recuo_s, varreduras, ultima_ok_em");
            var estadoTexto = json.ToJsonString();
            checar(!estadoTexto.Contains(Chave) && !estadoTexto.Contains(Pedido) && !estadoTexto.Contains("sendbird_gc"),
                "RH-11b nunca a chave, o pedido nem o canal no sinal");
            var ruim = new EstadoDaRecuperacao("segredo", null, null, 1, 1, 1, "x" + Chave, 0, 1);
            var rc2 = System.Text.Json.Nodes.JsonNode.Parse(ConversaRaspadinha.EstadoDoSinal("logado", true, null, 0, null, null, null, recuperacao: ruim))!["recuperacao"]!;
            checar((string?)rc2["fonte"] == "nenhuma" && rc2["erro"] is null, "RH-11c fonte e erro fora da lista branca viram nenhuma/null");
            s.Dispose();
        }
    }

    // ── R5: o cenário do pedido 2029 ─────────────────────────────────────────
    private static async Task OCenario2029Async(Action<bool, string> checar)
    {
        var db = Path.Combine(Path.GetTempPath(), "recuperacao-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var caminhoAntes = Banco.CaminhoForcado;
        var merchantsAntes = ServicoArquivoChat.Merchants;
        var chamadaAntes = ServicoArquivoChat.Chamada;
        Banco.CaminhoForcado = db;
        try
        {
            Banco.Migrar(db);
            using var cx = Banco.Abrir(db);
            Isolamento.SemNuvem(cx);
            ConversaRaspadinha.GravarSinal(cx, SinalJson("sombra"), DateTime.Now);
            ServicoConversaChat.LigarSoOFunil(ConversaRaspadinha.LerSinal(SinalJson("sombra")));
            ServicoArquivoChat.Merchants = () => new[] { Savassi };
            ServicoArquivoChat.Chamada = (_, _) => Task.FromResult((0, ""));

            // o relógio: a triagem do robô usa a hora de verdade (30 min), então "13:22" é agora e "12:32" é 50 min atrás
            var agoraReal = DateTime.Now;
            var h1322 = agoraReal;
            var h1232 = agoraReal.AddMinutes(-50);
            var m1 = h1322.AddSeconds(-55); var m2 = h1322.AddSeconds(-40); var m3 = h1322.AddSeconds(-20);
            var brdmEm = h1322.AddSeconds(-70); var lojaEm = h1322.AddMinutes(-72);
            const long Id1 = 20290001, Id2 = 20290002, Id3 = 20290003, IdBrdm = 20290000, IdLoja = 20289990;
            var canal2029 = Canal;
            var listaL = Lista("", CanalDaLista(canal2029, Ms(m3), Id3, "cli-2029", "CUSTOMER", 3));
            var corpoM = Mensagens(
                Rest("MESG", "Oi! Já está saindo.", autor: Loja, userType: "MERCHANT", id: IdLoja, criadoMs: Ms(lojaEm), canal: canal2029),
                Rest("MESG", "AD-7KQ2MX", autor: "cli-2029", id: Id3, criadoMs: Ms(m3), canal: canal2029),
                Rest("BRDM", "Quero adicionar observação ao pedido", autor: "ifood", userType: null, id: IdBrdm, criadoMs: Ms(brdmEm), canal: canal2029, customType: "summary"),
                Rest("MESG", "ganhei na raspadinha AD-7KQ2MX", autor: "cli-2029", id: Id1, criadoMs: Ms(m1), canal: canal2029),
                Rest("MESG", "meu codigo e AD-7KQ2MX", autor: "cli-2029", id: Id2, criadoMs: Ms(m2), canal: canal2029));

            var http = new HttpFalso();
            var entregues = new List<string>();
            var diag = new List<string>();
            var relogio = h1232;
            var s = new ServicoDeRecuperacao
            {
                Http = http.Enviar, Agora = () => relogio, ComRelogios = false,
                Entregar = q =>
                {
                    lock (entregues) entregues.Add(q);
                    // EXATAMENTE o que a ponte da tela faz (EntregarRecuperado), sem a thread da tela
                    ServicoConversaChat.QuadroRecuperado(q, Loja);
                    ServicoArquivoChat.Quadro(q, enviado: false, conexao: "rest", Loja);
                },
                Diag = l => { lock (diag) diag.Add(l); },
                Portao = () => true,
                UsuarioDaLoja = () => ServicoConversaChat.UsuarioDaLoja,
            };
            s.Ligar();
            http.Responder = u => u.Contains("/messages?") ? (200, corpoM, null) : (200, listaL, null);

            // 12:32 a 13:22: o caixa sobe sem nenhuma conexão do Sendbird
            var resultados = new List<ResultadoDaVarredura?>();
            for (var t = h1232; t < h1322; t = t.AddMinutes(10)) { relogio = t; resultados.Add(await s.VarrerAsync("relogio")); }
            checar(resultados.Count == 5 && resultados.All(r => r is { Erro: "sem_sessao", Chamou: false }) && http.Quantos == 0 && s.Agendada is null,
                "R5-1 12:32 a 13:22 sem LOGI nem cabeçalho: as varreduras não chamam nada (sem_sessao)");

            // 13:22: o socket da loja abre com key= na URL e o LOGI chega
            relogio = h1322;
            var mudou = s.OferecerCredencial(SessaoSendbird.DaUrlDoSocket(TestesEnvioPeloSocket.UrlLoja2, relogio));
            s.OferecerCredencial(SessaoSendbird.DoLogi("LOGI{\"key\":\"" + Chave + "\",\"user_id\":\"" + Loja + "\"}", Loja, AppId, relogio));
            checar(mudou && s.Agendada == ServicoDeRecuperacao.EsperaDaCredencial && s.Sessao.Fonte(null) == "logi",
                "R5-2 a credencial chega (URL e LOGI): varredura agendada em 2 s, fonte logi");

            relogio = h1322.AddSeconds(2);
            var r1 = await s.VarrerAsync("gatilho");
            checar(r1 is { Erro: null, Novas: 4, Lidos: 1, Mudados: 1 } && http.Urls.Count == 2,
                $"R5-3 a varredura lista e lê o canal do 2029 (novas={r1?.Novas}, lidos={r1?.Lidos})");
            var ids = entregues.Select(q => QuadroSendbird.Ler(q)?.MsgId ?? ChatArquivo.LerQuadro(q, Loja, null)?.MsgId).ToList();
            checar(ids.SequenceEqual(new[] { IdBrdm.ToString(), Id1.ToString(), Id2.ToString(), Id3.ToString() }),
                $"R5-4 Entregar recebe 4 quadros na ordem (BRDM, 3 MESG); a fala da loja das 12:10 não ({string.Join(",", ids)})");
            var t3 = ConversaRaspadinha.Triagem(QuadroSendbird.Ler(entregues.Last()), ConversaRaspadinha.LerSinal(SinalJson("sombra")), new CacheDeCanais(), new MemoriaDeEco(), Loja, null, agoraReal);
            checar(t3.Entra && t3.Fala!.Chave == "sb:" + Id3 && t3.Fala.Lado == LadoFala.Cliente,
                "R5-5 cada MESG passa na Triagem com o sinal ativo e o merchant da Savassi: Entra, chave sb:<id>, lado cliente (13:21 está nos 30 min de 13:22)");

            long Falas() => cx.ExecuteScalar<long>("SELECT COUNT(*) FROM raspadinha_conversa_fala WHERE chave IN ('sb:20290001','sb:20290002','sb:20290003')");
            long Arquivo() => cx.ExecuteScalar<long>("SELECT COUNT(*) FROM ifood_chat_arquivo WHERE chave IN ('sb:20290000','sb:20290001','sb:20290002','sb:20290003')");
            var fim = DateTime.Now.AddSeconds(15);
            while ((Falas() < 3 || ServicoArquivoChat.NaFila > 0 || Arquivo() < 4) && DateTime.Now < fim) await Task.Delay(50);
            checar(Falas() == 3, $"R5-6 raspadinha_conversa_fala ganha 3 linhas (uma por código), pelo QuadroRecuperado ({Falas()})");
            checar(Arquivo() == 4 && cx.ExecuteScalar<long>("SELECT COUNT(*) FROM raspadinha_conversa_fala WHERE chave = 'sb:" + IdLoja + "'") == 0,
                $"R5-7 o arquivo ganha 4 (BRDM + 3 MESG) e a fala da loja não entra em lugar nenhum ({Arquivo()})");
            checar(ServicoConversaChat.Rest == (4, 3), $"R5-7b o sinal conta rest_recebidas=4 (BRDM + 3 MESG) e rest_entregues=3 ({ServicoConversaChat.Rest})");

            // 10 s depois, a mesma lista: nada novo
            relogio = h1322.AddSeconds(12);
            var r2 = await s.VarrerAsync("relogio");
            checar(r2 is { Erro: null, Novas: 0, Lidos: 0 } && entregues.Count == 4 && http.Urls.Count == 3,
                "R5-8 a segunda varredura, 10 s depois, com a mesma lista: só a listagem, zero entregas");

            // o mesmo MESG das 13:21:40 chega depois pelo WebSocket: nem o robô nem a recuperação dobram
            var ws = TestesQuadroSendbird.Mesg("AD-7KQ2MX", autor: "cli-2029", msgId: Id3, criadoMs: Ms(m3), canal: canal2029);
            ServicoConversaChat.Quadro(ws, enviado: false, "c1", Loja);
            s.Viu(ws);
            await Task.Delay(300);
            checar(Falas() == 3 && s.Varredura.Dedup.Conhece("sb:" + Id3) && !s.Varredura.Dedup.Nova("sb:" + Id3),
                "R5-9 o WebSocket trazendo a mesma msg_id depois: a memória do robô recusa (mesma chave) e o dedup da recuperação também");

            // a ordem inversa: o WS primeiro, o REST depois, zero entregas
            var ws4 = TestesQuadroSendbird.Mesg("mais um", autor: "cli-2029", msgId: 20290004, criadoMs: Ms(h1322.AddSeconds(5)), canal: canal2029);
            s.Viu(ws4);
            var listaL2 = Lista("", CanalDaLista(canal2029, Ms(h1322.AddSeconds(5)), 20290004, "cli-2029", "CUSTOMER", 1));
            http.Responder = u => u.Contains("/messages?") ? (200, Mensagens(Rest("MESG", "mais um", autor: "cli-2029", id: 20290004, criadoMs: Ms(h1322.AddSeconds(5)), canal: canal2029)), null) : (200, listaL2, null);
            relogio = h1322.AddSeconds(22);
            var r3 = await s.VarrerAsync("relogio");
            checar(r3 is { Erro: null, Novas: 0 } && entregues.Count == 4, "R5-10 msg_id vista pelo WebSocket antes nunca é entregue pelo REST");

            var texto = string.Join("\n", diag);
            checar(diag.Any(d => d.Contains("novas=4") && d.Contains("fonte=logi") && d.Contains("lidos=1")) && diag.Any(d => d.StartsWith("recuperacao shape=", StringComparison.Ordinal)),
                "R5-11 o diag tem a linha da varredura (fonte, canais, lidos, novas) e UMA linha de shape");
            checar(!texto.Contains(Chave) && !texto.Contains("7KQ2MX") && !texto.Contains("ganhei") && !texto.Contains(Pedido) && !texto.Contains("message_ts=")
                   && diag.Count(d => d.StartsWith("recuperacao shape=", StringComparison.Ordinal)) == 1,
                "R5-12 nenhuma linha de diag leva a chave, o texto do cliente, o pedido ou a URL com query; o shape sai uma vez só");
            checar(ChatCaptura.MascararTexto(texto) == texto, "R5-13 as linhas já saem limpas: a rede de segurança não precisa apagar nada");
            s.Dispose();
        }
        finally
        {
            ServicoArquivoChat.Merchants = merchantsAntes;
            ServicoArquivoChat.Chamada = chamadaAntes;
            Banco.CaminhoForcado = caminhoAntes;
            try { File.Delete(db); } catch { }
        }
    }

    // ── o fonte: a tela liga nos lugares certos ──────────────────────────────
    private static void PeloFonte(Action<bool, string> checar)
    {
        var tela = Ler("Telas/ChatIfood.xaml.cs");
        var servico = Ler("ServicoRecuperacaoChat.cs");
        var conversa = Ler("ServicoConversaChat.cs");
        checar(tela.Contains("ServicoRecuperacaoChat.Ligar(new PonteDaRecuperacao { Entregar = EntregarRecuperado })")
               && tela.IndexOf("ServicoConversaChat.Ligar(", StringComparison.Ordinal) < tela.IndexOf("ServicoRecuperacaoChat.Ligar(", StringComparison.Ordinal),
            "FT-1 a tela liga a recuperação logo depois do ServicoConversaChat.Ligar");
        checar(tela.Contains("SessaoSendbird.DaUrlDoSocket(u.GetString(), DateTime.Now)") && tela.Contains("SessaoSendbird.DoLogi(p, WsUser(conexao)")
               && tela.Contains("SessaoSendbird.DosCabecalhos(url, hs, DateTime.Now)") && tela.Contains("ServicoRecuperacaoChat.Invalidar(\"sdk_\" + status)")
               && tela.Contains("if (doSendbird) ServicoRecuperacaoChat.Viu(p);") && tela.Contains("if (e.IsSuccess) ServicoRecuperacaoChat.GestorRecarregou();"),
            "FT-2 as três fontes da credencial (URL, LOGI, cabeçalho), o 401 do SDK, a marca d'água por todo quadro e a recarga do Gestor estão ligados na captura");
        var i = tela.IndexOf("private void EntregarRecuperado(string payload)", StringComparison.Ordinal);
        var corpo = i < 0 ? "" : tela[i..Math.Min(tela.Length, i + 900)];
        checar(corpo.Contains("ServicoConversaChat.QuadroRecuperado(payload, loja)") && corpo.Contains("ServicoArquivoChat.Quadro(payload, enviado: false, conexao: \"rest\", loja)")
               && corpo.Contains("OuvirMensagem(ChatCaptura.NormalizarFrame(payload), \"rest\", null, null)")
               && !corpo.Contains("_conexoes.") && !corpo.Contains("_ecosSocket") && !corpo.Contains("_conexaoPorCanal") && !corpo.Contains("Contadores"),
            "FT-3 a ponte entrega ao MESMO funil (robô, arquivo, fluxo legado com origem rest) e não toca no que é da conexão");
        var q = conversa.IndexOf("public static void QuadroRecuperado(", StringComparison.Ordinal);
        var fimQ = conversa.IndexOf("// ── 1.0.38 (caso #0434)", StringComparison.Ordinal);
        var corpoQ = q < 0 || fimQ < q ? "" : conversa[q..fimQ];
        checar(corpoQ.Length > 0 && !corpoQ.Contains("Contadores.") && !corpoQ.Contains("_wsUserId =") && corpoQ.Contains("Triagem(") && corpoQ.Contains("Nova(t.Fala!.Chave)"),
            "FT-4 QuadroRecuperado passa pela triagem e pela memória, SEM Contadores.Registrar (ws_vivo) e SEM aprender o usuário da loja");
        checar(servico.Contains("ServicoRaspadinhaChat.Ligado()") && servico.Contains("ServicoConversaChat.Sinal.Ativo") && servico.Contains("!ChatArquivo.EmSilencio(agora)")
               && servico.Contains("_portaoAte = agora.AddSeconds(30)"),
            "FT-5 o portão é Ligado() OU o sinal ativo OU o arquivo fora do silêncio, em cache de 30 s (só o Ligado() desligaria a recuperação na Savassi)");
        checar(servico.Contains("Telas.ChatIfood.DiagRaspadinha(ChatCaptura.MascararTexto(") && servico.Contains("Timeout = Timeout.InfiniteTimeSpan"),
            "FT-6 toda linha de diag passa pela rede de segurança e o HttpClient não tem prazo próprio (o prazo é o da varredura)");
        checar(conversa.Contains("ServicoRecuperacaoChat.Estado()") && conversa.Contains("RestRecebidas = Rest.Recebidas"),
            "FT-7 o sinal de 60 s leva estado.recuperacao com as contagens do funil");
        foreach (var arq in new[] { "Pdv.Nucleo/SessaoSendbird.cs", "Pdv.Nucleo/RecuperacaoSendbird.cs", "Pdv.Nucleo/ServicoDeRecuperacao.cs", "ServicoRecuperacaoChat.cs", "Pdv.Testes/TestesRecuperacaoSendbird.cs" })
        {
            var t = Ler(arq);
            checar(t.Length > 0 && !t.Contains((char)0x2014) && !t.Contains((char)0x2013), "FT-8 sem travessão: " + arq);
        }
        var nucleo = Ler("Pdv.Nucleo/RecuperacaoSendbird.cs") + Ler("Pdv.Nucleo/ServicoDeRecuperacao.cs");
        var codigo = nucleo.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)).ToList();
        checar(!codigo.Any(l => l.Contains("HttpMethod.Post") || l.Contains("HttpMethod.Put") || l.Contains("mark_as_read")) && codigo.Any(l => l.Contains("HttpMethod.Get")),
            "FT-9 GET apenas: nunca POST/PUT, nunca mark_as_read (fora dos comentários)");
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
