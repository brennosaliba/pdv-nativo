using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>De que lado veio a mensagem do chat, para o arquivo (mais largo que o do resgate: o iFood entra).</summary>
public enum LadoArquivo { Cliente, Loja, Ifood, Desconhecido }

/// <summary>
/// Uma mensagem do chat do iFood como o ARQUIVO a le: qualquer quadro que seja mensagem (MESG, BRDM,
/// ADMM, AEDI, FILE), de qualquer lado (cliente, loja, iFood, desconhecido). O texto aqui e o CRU
/// (como veio no quadro): a mascara e a normalizacao acontecem na hora de gravar.
/// </summary>
public sealed record MensagemArquivo(
    string Comando, string Canal, string? MsgId, string? ReqId, string? AutorId, string? AutorTipo,
    LadoArquivo Lado, string? CustomType, string Texto, long? CriadoEmMs, bool Sussurro, bool TemMidia);

/// <summary>Uma mensagem depois da triagem: a mensagem e o canal, ou o motivo de ter ficado de fora.</summary>
public sealed record MensagemTriada(MensagemArquivo? Mensagem, CanalDaFala? Canal, string Motivo)
{
    public bool Entra => Mensagem is not null && Canal is not null;
}

public enum StatusDoArquivo { Ok, Recusado, SemPermissao, NuvemSemRecurso, SemResposta }

/// <summary>O que a edge <c>ifood-chat-arquivo</c> respondeu a um lote. Nunca traz texto.</summary>
public sealed record RespostaDoArquivo(StatusDoArquivo Status, bool Captura, int Guardadas, int Repetidas,
    IReadOnlyList<(string Chave, string Motivo)> Recusadas, string? MotivoCru);

/// <summary>Uma linha pendente do arquivo local, pronta para ir no lote.</summary>
public sealed record LinhaArquivo(string Chave, string Canal, string IfoodOrderId, string MerchantId, string Lado, string? AutorTipo,
    string? AutorId, string TipoQuadro, string? CustomType, string? MsgId, string? Texto, bool Sussurro, bool TemMidia, string Quando);

/// <summary>
/// O BANCO DE CONVERSAS DO CHAT DO iFOOD, LADO DO CAIXA (08/10/2026, SQL 155, desenho "banco-de-conversas").
///
/// O caixa continua sendo so um LEITOR. Este e o segundo ouvinte dos quadros do WebSocket do Sendbird,
/// independente do resgate: le TUDO que e mensagem (o cliente, a loja, as mensagens automaticas do
/// iFood, a foto), mascara no balcao (telefone, CPF, e-mail, cartao), guarda no SQLite com a fila e
/// manda em lote para a edge <c>ifood-chat-arquivo</c>. Se o arquivo cair, o resgate nem percebe; se
/// o resgate cair, o arquivo continua.
///
/// O arquivo e de MAO UNICA: o caixa nao le classe, texto nem decisao da resposta. So contagens e a
/// chave <c>captura</c>: com <c>captura:false</c> a loja esta desligada no ERP, o caixa descarta o que
/// leu e fica 10 min sem mandar (o proximo lote vai como sonda).
///
/// ⚠️ ESTE ARQUIVO NAO CONHECE CLASSE NENHUMA nem palavra nenhuma: quem classifica e o ERP.
/// Tudo aqui e puro ou de banco local; nunca lanca para a tela.
/// </summary>
public static class ChatArquivo
{
    public const string Edge = "ifood-chat-arquivo";
    public const string Acao = "arquivar";

    /// <summary>Tipo da linha na fila (Drenagem.TiposComHandler). ref_id = "arquivo", client_key = a chave da mensagem.</summary>
    public const string TipoNaFila = "ifood_chat_lote";

    /// <summary>Ate 50 mensagens por lote, texto de ate 1000, corpo abaixo de 48 KB (folga de 2 KB).</summary>
    public const int TetoMensagens = 50;
    public const int TetoTexto = 1000;
    public const int TetoCorpoBytes = 46 * 1024;
    public const int TetoTerminal = 60;
    /// <summary>Depois de 20 tentativas sem resposta a linha e descartada (o ERP deduplica; reenviar e seguro).</summary>
    public const int TetoTentativas = 20;

    /// <summary>O lote junta ate 50 mensagens por ate 20 s; cada chamada tem 12 s.</summary>
    public static readonly TimeSpan Janela = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan Prazo = TimeSpan.FromSeconds(12);
    /// <summary>Folga grande de proposito: o Sendbird reentrega mensagem velha na reconexao e o ERP deduplica.</summary>
    public static readonly TimeSpan Recencia = TimeSpan.FromDays(30);
    public static readonly TimeSpan ToleranciaDeRelogio = TimeSpan.FromMinutes(2);
    /// <summary>A linha local some depois disto, com desfecho ou sem.</summary>
    public static readonly TimeSpan GuardaLocal = TimeSpan.FromDays(2);
    /// <summary>Com captura:false o caixa fica este tempo sem mandar.</summary>
    public static readonly TimeSpan Silencio = TimeSpan.FromMinutes(10);
    /// <summary>Quanto tempo a fila espera antes de mexer numa mensagem que o servico ainda esta juntando.</summary>
    public static readonly TimeSpan EsperaDoServico = TimeSpan.FromMinutes(2);

    /// <summary>Os comandos do Sendbird que viram mensagem no arquivo.</summary>
    public static readonly IReadOnlySet<string> Comandos = new HashSet<string>(StringComparer.Ordinal) { "MESG", "BRDM", "ADMM", "AEDI", "FILE" };

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ── O LEITOR (puro) ─────────────────────────────────────────────────────

    /// <summary>
    /// A mensagem do quadro, ou null quando o quadro nao e mensagem (READ, DLVR, MACK, SYEV, LOGI,
    /// PING, PONG, TPST, TPEN) ou nao da para ler. O lado: autor igual ao user_id do WebSocket ou do SDK
    /// = loja; <c>userType</c> CUSTOMER = cliente; MERCHANT = loja; BOT (ou <c>is_bot</c>) = iFood; BRDM,
    /// ADMM e AEDI = iFood; o resto = desconhecido (e entra, marcado). Nunca lanca.
    /// </summary>
    public static MensagemArquivo? LerQuadro(string? payload, string? wsUserId, string? sdkUserId)
    {
        try
        {
            var cmd = QuadroSendbird.Comando(payload);
            if (cmd is null || !Comandos.Contains(cmd)) return null;
            using var doc = JsonDocument.Parse(payload!.AsMemory(4));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var canal = Texto(r, "channel_url");
            if (string.IsNullOrWhiteSpace(canal)) return null;

            string? autor = null, userType = null;
            var bot = false;
            if (r.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                autor = TextoOuNumero(u, "guest_id") ?? TextoOuNumero(u, "user_id") ?? TextoOuNumero(u, "userId") ?? TextoOuNumero(u, "id");
                userType = UserType(u);
                bot = Ligado(u, "is_bot") || Ligado(u, "is_ai_bot");
            }
            autor ??= TextoOuNumero(r, "user_id") ?? TextoOuNumero(r, "guest_id");
            userType ??= UserType(r);

            var texto = Texto(r, "message") ?? "";
            var temMidia = false;
            if (cmd == "FILE")
            {
                temMidia = true;
                var mime = Texto(r, "type") ?? "";
                texto = mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "[foto]" : "[arquivo]";
            }
            else if (cmd == "AEDI")
            {
                // o card de negociacao vem com <br /> entre as linhas
                texto = Regex.Replace(texto, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
                texto = Regex.Replace(texto, @"<[^>]{1,40}>", " ");
            }
            if (cmd != "FILE" && string.IsNullOrWhiteSpace(texto)) return null;

            var lado = cmd is "BRDM" or "ADMM" or "AEDI"
                ? LadoArquivo.Ifood
                : Lado(autor, userType, bot, wsUserId, sdkUserId);

            var msgId = TextoOuNumero(r, "msg_id");
            if (msgId is "0") msgId = null;
            var custom = Texto(r, "custom_type");
            if (custom is not null)
            {
                custom = Regex.Replace(custom, "[^A-Za-z0-9_-]", "");
                if (custom.Length > 40) custom = custom[..40];
                if (custom.Length == 0) custom = null;
            }

            return new MensagemArquivo(cmd, canal, msgId, TextoOuNumero(r, "req_id"), autor, userType, lado, custom, texto,
                Numero(r, "created_at") ?? Numero(r, "ts"), Sussurro(r), temMidia);
        }
        catch { return null; }
    }

    private static LadoArquivo Lado(string? autor, string? userType, bool bot, string? wsUserId, string? sdkUserId)
    {
        var a = autor?.Trim();
        if (!string.IsNullOrEmpty(a) && (Igual(a, wsUserId) || Igual(a, sdkUserId))) return LadoArquivo.Loja;
        var t = (userType ?? "").Trim().ToUpperInvariant();
        if (t == "CUSTOMER") return LadoArquivo.Cliente;
        if (t == "MERCHANT") return LadoArquivo.Loja;
        if (t == "BOT" || bot) return LadoArquivo.Ifood;
        return LadoArquivo.Desconhecido;

        static bool Igual(string a, string? b)
            => !string.IsNullOrWhiteSpace(b) && string.Equals(a, b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static string LadoTexto(LadoArquivo l) => l switch
    {
        LadoArquivo.Cliente => "cliente",
        LadoArquivo.Loja => "loja",
        LadoArquivo.Ifood => "ifood",
        _ => "desconhecido",
    };

    // ── A MASCARA (pura, idempotente; a MESMA lista do ERP, _ifood_chat_mascarar) ──

    private static readonly Regex ReCartao = new(@"(?<![0-9])[0-9]{4}([ .-]?[0-9]{4}){3}([ .-]?[0-9]{1,3})?(?![0-9])", RegexOptions.CultureInvariant);
    private static readonly Regex ReCpf = new(@"(?<![0-9])([0-9]{3}\.[0-9]{3}\.[0-9]{3}-?[0-9]{2}|[0-9]{3} [0-9]{3} [0-9]{3} [0-9]{2})(?![0-9])", RegexOptions.CultureInvariant);
    private static readonly Regex ReEmail = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z0-9.-]+", RegexOptions.CultureInvariant);
    private static readonly Regex ReTelefone = new(@"(?<![0-9])(\+?55[ .-]?)?\(?0?[0-9]{2}\)?[ .-]?9?[0-9]{4}[ .-]?[0-9]{4}(?![0-9])", RegexOptions.CultureInvariant);
    private static readonly Regex ReOitoDigitos = new(@"(?<![0-9])[0-9]([ .()/-]*[0-9]){7,}(?![0-9])", RegexOptions.CultureInvariant);
    private static readonly Regex ReUrl = new(@"(https?://[^ ?#]+)\?[^ ]*", RegexOptions.CultureInvariant);
    // revisao 08/10: o hifen "inteligente" (U+2010 a U+2015, o sinal de menos U+2212) e o espaco duro
    // (U+00A0, U+202F) viram '-' e ' ' ANTES da mascara: "31 99999" + meia-risca + "1234" passava em claro.
    // A mesma normalizacao do ERP (_ifood_chat_mascarar) e da tela (semNumeroLongo).
    private static readonly Regex ReHifenEsperto = new(@"[\u2010-\u2015\u2212]", RegexOptions.CultureInvariant);
    private static readonly Regex ReEspacoDuro = new(@"[\u00a0\u202f]", RegexOptions.CultureInvariant);

    /// <summary>
    /// Telefone, CPF, e-mail e cartao viram rotulos; a query da URL some; o codigo da raspadinha
    /// (AD-XXXXXX) e o numero do pedido ficam. A segunda passada da o mesmo texto. O ERP mascara de
    /// novo com a mesma regra; o caixa mascara primeiro porque o SQLite fica no balcao.
    /// </summary>
    public static string Mascarar(string? texto)
    {
        var t = ReEspacoDuro.Replace(ReHifenEsperto.Replace(texto ?? "", "-"), " ");
        t = ReCartao.Replace(t, "[cartao]");
        t = ReCpf.Replace(t, "[cpf]");
        t = ReEmail.Replace(t, "[email]");
        t = ReTelefone.Replace(t, "[telefone]");
        t = ReOitoDigitos.Replace(t, "[telefone]");
        t = ReUrl.Replace(t, "$1");
        return t;
    }

    /// <summary>O texto como viaja: uma linha, sem espaco dobrado, cortado em 1000 e mascarado.</summary>
    public static string TextoParaGuardar(string? texto) => Mascarar(ConversaRaspadinha.CortarTexto(texto));

    /// <summary>
    /// A chave, IGUAL a do resgate para o mesmo quadro (ConversaRaspadinha.Chave): <c>sb:&lt;msg_id&gt;</c>
    /// quando vem; senao <c>h:&lt;sha256(canal|autor|created_at_ms|texto)&gt;</c>. E ela que liga a
    /// mensagem arquivada ao evento do resgate no ERP, por (loja, chave).
    /// </summary>
    public static string Chave(MensagemArquivo m)
        => m.MsgId is { Length: > 0 } id
            ? "sb:" + id.Trim()
            : "h:" + Sha256(string.Join('|', m.Canal, m.AutorId ?? "",
                m.CriadoEmMs?.ToString(CultureInfo.InvariantCulture) ?? "", m.Texto));

    private static string Sha256(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    // ── A TRIAGEM (pura) ────────────────────────────────────────────────────

    /// <summary>
    /// Decide se a mensagem entra no arquivo. Na ordem: so quadro RECEBIDO (o que sai da pagina volta
    /// como MESG do servidor; sem eco dobrado); e mensagem; o canal e de pedido do iFood; com os
    /// merchants da loja no sinal, o merchant tem de ser desta loja (sem merchants a mensagem entra e
    /// fica guardada esperando o sinal: "ok_sem_merchants"); tem texto ou e foto; tem hora, de ate 30
    /// dias atras e ate 2 min no futuro. Nao depende do modo do chat nem de conversa aberta.
    /// </summary>
    public static MensagemTriada Triagem(MensagemArquivo? m, bool enviado, IReadOnlyList<string> merchantIds, DateTime agora)
    {
        if (enviado) return Fora("enviado");
        if (m is null) return Fora("nao_e_mensagem");
        if (CanalIfood.Ler(m.Canal) is not { } ids) return Fora("canal_fora_do_padrao");
        var canal = new CanalDaFala(m.Canal, ids.OrderId, ids.MerchantId);
        var semMerchants = merchantIds.Count == 0;
        if (!semMerchants && !merchantIds.Contains(canal.MerchantId, StringComparer.OrdinalIgnoreCase)) return Fora("merchant_de_outra_loja");
        if (!m.TemMidia && ConversaRaspadinha.CortarTexto(m.Texto).Length == 0) return Fora("sem_texto");
        if (m.CriadoEmMs is not { } ms) return Fora("sem_hora");
        DateTime local;
        try { local = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime; }
        catch { return Fora("sem_hora"); }
        if (local < agora - Recencia || local > agora + ToleranciaDeRelogio) return Fora("fora_do_prazo");
        return new MensagemTriada(m, canal, semMerchants ? "ok_sem_merchants" : "ok");

        static MensagemTriada Fora(string motivo) => new(null, null, motivo);
    }

    // ── O BANCO LOCAL E A FILA ──────────────────────────────────────────────

    /// <summary>
    /// Grava a mensagem (ja mascarada) e a enfileira NA MESMA TRANSACAO. Devolve false quando a chave
    /// ja era conhecida: dois caixas da mesma loja, ou o mesmo caixa reiniciado, nao viram duas linhas.
    /// </summary>
    public static bool Registrar(SqliteConnection cx, CanalDaFala c, MensagemArquivo m, DateTime agora)
    {
        var chave = Chave(m);
        using var tx = cx.BeginTransaction();
        var n = cx.Execute("""
            INSERT OR IGNORE INTO ifood_chat_arquivo
                   (chave, canal, ifood_order_id, merchant_id, lado, autor_tipo, autor_id, tipo_quadro, custom_type, msg_id,
                    texto, sussurro, tem_midia, quando, estado, tentativas, criado_em)
            VALUES (@K, @C, @O, @M, @L, @AT, @A, @T, @CT, @I, @X, @S, @F, @Q, 'pendente', 0, @Em)
            """, new
        {
            K = chave, C = c.Canal, O = c.OrderId, M = c.MerchantId, L = LadoTexto(m.Lado), AT = m.AutorTipo, A = m.AutorId,
            T = m.Comando, CT = m.CustomType, I = m.MsgId, X = TextoParaGuardar(m.Texto), S = m.Sussurro ? 1 : 0, F = m.TemMidia ? 1 : 0,
            Q = QuandoIso(m.CriadoEmMs), Em = agora.ToString("o", CultureInfo.InvariantCulture),
        }, tx);
        if (n == 0) { tx.Rollback(); return false; }
        Caixa.Enfileirar(cx, tx, TipoNaFila, "arquivo", chave, new { chave });
        tx.Commit();
        return true;
    }

    private static string QuandoIso(long? ms)
    {
        try { return (ms is { } v ? DateTimeOffset.FromUnixTimeMilliseconds(v) : DateTimeOffset.Now).ToString("o", CultureInfo.InvariantCulture); }
        catch { return DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture); }
    }

    /// <summary>
    /// As pendentes, da mais antiga para a mais nova, ate <paramref name="limite"/>. SEM merchants (o
    /// sinal do chat ainda nao veio) devolve vazio: nada sai, tudo espera. Com merchants, a linha de
    /// outra loja e descartada aqui (o ERP conferiria de novo).
    /// </summary>
    public static IReadOnlyList<LinhaArquivo> Pendentes(SqliteConnection cx, IReadOnlyList<string> merchantIds, int limite = TetoMensagens)
    {
        if (merchantIds.Count == 0) return Array.Empty<LinhaArquivo>();
        var linhas = cx.Query("""
            SELECT chave, canal, ifood_order_id, merchant_id, lado, autor_tipo, autor_id, tipo_quadro, custom_type, msg_id,
                   texto, sussurro, tem_midia, quando
              FROM ifood_chat_arquivo
             WHERE estado = 'pendente'
             ORDER BY criado_em, chave
             LIMIT @N
            """, new { N = Math.Max(1, limite) }).ToList();
        var saida = new List<LinhaArquivo>();
        foreach (var l in linhas)
        {
            var merchant = (string)l.merchant_id;
            if (!merchantIds.Contains(merchant, StringComparer.OrdinalIgnoreCase))
            {
                cx.Execute("UPDATE ifood_chat_arquivo SET estado = 'descartada', texto = NULL WHERE chave = @K", new { K = (string)l.chave });
                continue;
            }
            saida.Add(new LinhaArquivo((string)l.chave, (string)l.canal, (string)l.ifood_order_id, merchant, (string)l.lado,
                l.autor_tipo as string, l.autor_id as string, (string)l.tipo_quadro, l.custom_type as string, l.msg_id as string,
                l.texto as string, Convert.ToInt64(l.sussurro) != 0, Convert.ToInt64(l.tem_midia) != 0, (string)l.quando));
        }
        return saida;
    }

    /// <summary>Quantas mensagens esperam a rede.</summary>
    public static int Pendentes(SqliteConnection cx)
        => cx.ExecuteScalar<int>("SELECT COUNT(*) FROM ifood_chat_arquivo WHERE estado = 'pendente'");

    /// <summary>Apaga a linha velha (2 dias), com desfecho ou sem.</summary>
    public static int Faxina(SqliteConnection cx, DateTime agora)
        => cx.Execute("DELETE FROM ifood_chat_arquivo WHERE criado_em < @L",
            new { L = (agora - GuardaLocal).ToString("o", CultureInfo.InvariantCulture) });

    // ── O CORPO E A RESPOSTA (puros) ────────────────────────────────────────

    /// <summary>
    /// O corpo do <c>arquivar</c> e as chaves que couberam: ate 50 mensagens e o corpo abaixo de 46 KB
    /// (a que nao coube fica pendente e vai no proximo). A loja NUNCA vai no corpo: vem da porta.
    /// </summary>
    public static (string Corpo, IReadOnlyList<string> Chaves) CorpoLote(string? terminal, string? versao, IEnumerable<LinhaArquivo> linhas)
    {
        var lista = linhas.Take(TetoMensagens).ToList();
        var term = string.IsNullOrWhiteSpace(terminal) ? ConversaRaspadinha.NomeDoTerminal() : terminal.Trim();
        if (term.Length > TetoTerminal) term = term[..TetoTerminal];
        while (true)
        {
            var corpo = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["acao"] = Acao,
                ["terminal"] = term,
                ["versao"] = versao,
                ["mensagens"] = lista.Select(l => new Dictionary<string, object?>
                {
                    ["chave"] = l.Chave,
                    ["canal"] = l.Canal,
                    ["ifood_order_id"] = l.IfoodOrderId,
                    ["merchant_id"] = l.MerchantId,
                    ["lado"] = l.Lado,
                    ["autor_tipo"] = l.AutorTipo,
                    ["autor_id"] = l.AutorId,
                    ["tipo_quadro"] = l.TipoQuadro,
                    ["custom_type"] = l.CustomType,
                    ["msg_id"] = l.MsgId,
                    ["texto"] = l.Texto ?? "",
                    ["sussurro"] = l.Sussurro,
                    ["tem_midia"] = l.TemMidia,
                    ["quando"] = l.Quando,
                }).ToList(),
            }, Json);
            if (lista.Count <= 1 || Encoding.UTF8.GetByteCount(corpo) <= TetoCorpoBytes)
                return (corpo, lista.Select(l => l.Chave).ToList());
            lista.RemoveAt(lista.Count - 1);
        }
    }

    /// <summary>Le a resposta da edge. 2xx: ok e captura; 401/403: SemPermissao; 404: a edge ou o SQL ainda nao publicados; 5xx/408/425/429/0: SemResposta; outro 4xx: Recusado.</summary>
    public static RespostaDoArquivo LerRespostaLote(int status, string? corpo)
    {
        if (status is 401 or 403) return new RespostaDoArquivo(StatusDoArquivo.SemPermissao, false, 0, 0, Array.Empty<(string, string)>(), Motivo(corpo) ?? $"http {status}");
        if (status == 404) return new RespostaDoArquivo(StatusDoArquivo.NuvemSemRecurso, false, 0, 0, Array.Empty<(string, string)>(), Motivo(corpo) ?? "http 404");
        if (status is 408 or 425 or 429 or >= 500 or <= 0) return new RespostaDoArquivo(StatusDoArquivo.SemResposta, false, 0, 0, Array.Empty<(string, string)>(), status <= 0 ? "sem resposta" : $"http {status}");
        if (status is >= 400) return new RespostaDoArquivo(StatusDoArquivo.Recusado, false, 0, 0, Array.Empty<(string, string)>(), Motivo(corpo) ?? $"http {status}");
        try
        {
            using var doc = JsonDocument.Parse(corpo ?? "");
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return new RespostaDoArquivo(StatusDoArquivo.SemResposta, false, 0, 0, Array.Empty<(string, string)>(), "resposta ilegivel");
            if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
                return new RespostaDoArquivo(StatusDoArquivo.Recusado, false, 0, 0, Array.Empty<(string, string)>(), Texto(r, "motivo") ?? "recusado");
            var recusadas = new List<(string, string)>();
            if (r.TryGetProperty("recusadas", out var rec) && rec.ValueKind == JsonValueKind.Array)
                foreach (var x in rec.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.Object && Texto(x, "chave") is { } k) recusadas.Add((k, Texto(x, "motivo") ?? "recusada"));
            var captura = r.TryGetProperty("captura", out var c) && c.ValueKind == JsonValueKind.True;
            return new RespostaDoArquivo(StatusDoArquivo.Ok, captura, (int)(Numero(r, "guardadas") ?? 0), (int)(Numero(r, "repetidas") ?? 0), recusadas, null);
        }
        catch { return new RespostaDoArquivo(StatusDoArquivo.SemResposta, false, 0, 0, Array.Empty<(string, string)>(), "resposta ilegivel"); }

        static string? Motivo(string? corpo)
        {
            try
            {
                using var d = JsonDocument.Parse(corpo ?? "");
                return d.RootElement.ValueKind == JsonValueKind.Object ? Texto(d.RootElement, "motivo") : null;
            }
            catch { return null; }
        }
    }

    // ── O SILENCIO (captura desligada no ERP) ───────────────────────────────

    private static readonly object TravaSilencio = new();
    private static DateTime _silenciadaAte = DateTime.MinValue;

    /// <summary>Com captura:false o caixa para de mandar por 10 min; o proximo lote vai como sonda.</summary>
    public static bool EmSilencio(DateTime agora) { lock (TravaSilencio) return agora < _silenciadaAte; }
    public static void Silenciar(DateTime agora) { lock (TravaSilencio) _silenciadaAte = agora + Silencio; }
    /// <summary>So os testes: zera o silencio.</summary>
    internal static void EsquecerSilencio() { lock (TravaSilencio) _silenciadaAte = DateTime.MinValue; }

    /// <summary>
    /// Aplica a resposta as chaves do lote. Ok com captura: guardadas e repetidas viram enviada (texto
    /// sai na hora), recusadas viram descartada. Ok sem captura: tudo descartada, texto sai, e o
    /// silencio de 10 min comeca. SemPermissao e Recusado: descartada. SemResposta e NuvemSemRecurso:
    /// conta tentativa e, no teto, descarta.
    /// </summary>
    public static void Aplicar(SqliteConnection cx, IReadOnlyList<string> chaves, RespostaDoArquivo r, DateTime agora)
    {
        if (chaves.Count == 0) return;
        switch (r.Status)
        {
            case StatusDoArquivo.Ok when !r.Captura:
                Silenciar(agora);
                Marcar(cx, chaves, "descartada");
                return;
            case StatusDoArquivo.Ok:
                var recusadas = r.Recusadas.Select(x => x.Chave).ToHashSet(StringComparer.Ordinal);
                Marcar(cx, chaves.Where(k => recusadas.Contains(k)).ToList(), "descartada");
                Marcar(cx, chaves.Where(k => !recusadas.Contains(k)).ToList(), "enviada");
                return;
            case StatusDoArquivo.SemPermissao:
            case StatusDoArquivo.Recusado:
                Marcar(cx, chaves, "descartada");
                return;
            default:
                cx.Execute("UPDATE ifood_chat_arquivo SET tentativas = tentativas + 1 WHERE chave IN @K AND estado = 'pendente'", new { K = chaves });
                cx.Execute("UPDATE ifood_chat_arquivo SET estado = 'descartada', texto = NULL WHERE chave IN @K AND estado = 'pendente' AND tentativas >= @T",
                    new { K = chaves, T = TetoTentativas });
                return;
        }

        static void Marcar(SqliteConnection cx, IReadOnlyList<string> chaves, string estado)
        {
            if (chaves.Count == 0) return;
            cx.Execute("UPDATE ifood_chat_arquivo SET estado = @E, texto = NULL WHERE chave IN @K", new { E = estado, K = chaves });
        }
    }

    /// <summary>
    /// O que a Drenagem faz com uma linha <see cref="TipoNaFila"/>, no contrato da fila: (true) resolvido,
    /// (false) recusa permanente, (null) transitorio. Manda TODAS as pendentes (ate 50) numa chamada so;
    /// reenviar e seguro porque o ERP deduplica pela chave. Sem o sinal do chat (sem merchants) espera.
    /// Em silencio (captura desligada no ERP) a linha e descartada sem chamada.
    /// </summary>
    public static async Task<(bool? Ok, string? Erro)> ResolverNaFilaAsync(string chave,
        Func<string, string, Task<(int Status, string? Corpo)>> funcao, DateTime agora, string? terminal = null, string? versao = null)
    {
        string corpo;
        IReadOnlyList<string> chaves;
        using (var cx = Banco.Abrir())
        {
            var linha = cx.QueryFirstOrDefault("SELECT estado, criado_em FROM ifood_chat_arquivo WHERE chave = @K", new { K = chave });
            if (linha is null) return (true, "a mensagem local sumiu: nada a mandar");
            var estado = (string)linha.estado;
            if (estado == "enviada") return (true, "a mensagem ja foi entregue");
            if (estado == "descartada") return (true, "a mensagem foi descartada");
            if (DateTime.TryParse(linha.criado_em as string, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime em)
                && em > agora - EsperaDoServico)
                return (null, "o servico ainda esta juntando este lote");
            if (EmSilencio(agora))
            {
                cx.Execute("UPDATE ifood_chat_arquivo SET estado = 'descartada', texto = NULL WHERE chave = @K", new { K = chave });
                return (true, "a captura do chat esta desligada no ERP");
            }
            var merchants = ConversaRaspadinha.SinalGravado(cx).Sinal.MerchantIds;
            if (merchants.Count == 0) return (null, "sem o sinal do chat (os merchants da loja) ainda");
            var linhas = Pendentes(cx, merchants);
            if (linhas.Count == 0) return (true, "nada pendente");
            (corpo, chaves) = CorpoLote(terminal, versao, linhas);
        }

        var (st, resp) = await funcao(Edge, corpo).ConfigureAwait(false);
        var r = LerRespostaLote(st, resp);
        using (var cx = Banco.Abrir()) Aplicar(cx, chaves, r, agora);

        return r.Status switch
        {
            StatusDoArquivo.Ok => (true, r.Captura ? null : "a captura do chat esta desligada no ERP"),
            StatusDoArquivo.Recusado => (true, $"o servidor recusou ({r.MotivoCru})"),
            StatusDoArquivo.SemPermissao => (false, $"este caixa foi barrado ({r.MotivoCru})"),
            StatusDoArquivo.NuvemSemRecurso => (null, $"a nuvem ainda nao tem a funcao {Edge} (publicar a borda ou o SQL 155)"),
            _ => (null, $"sem desfecho ao mandar o lote: {r.MotivoCru}"),
        };
    }

    // ── json ────────────────────────────────────────────────────────────────

    private static string? UserType(JsonElement o)
    {
        if (!o.TryGetProperty("metadata", out var md)) return null;
        if (md.ValueKind == JsonValueKind.String)
        {
            var s = md.GetString();
            if (string.IsNullOrWhiteSpace(s) || !s.TrimStart().StartsWith('{')) return null;
            try { using var d = JsonDocument.Parse(s); return DoObjeto(d.RootElement); }
            catch { return null; }
        }
        return DoObjeto(md);

        static string? DoObjeto(JsonElement m)
        {
            if (m.ValueKind != JsonValueKind.Object) return null;
            var v = Texto(m, "userType") ?? Texto(m, "user_type") ?? Texto(m, "usertype");
            return v?.Trim().ToUpperInvariant();
        }
    }

    private static bool Sussurro(JsonElement r)
    {
        if (Ligado(r, "whisperMode")) return true;
        if (!r.TryGetProperty("data", out var d)) return false;
        if (d.ValueKind == JsonValueKind.Object) return Ligado(d, "whisperMode");
        if (d.ValueKind != JsonValueKind.String) return false;
        var s = d.GetString();
        if (string.IsNullOrWhiteSpace(s) || !s.TrimStart().StartsWith('{')) return false;
        try
        {
            using var dd = JsonDocument.Parse(s);
            return dd.RootElement.ValueKind == JsonValueKind.Object && Ligado(dd.RootElement, "whisperMode");
        }
        catch { return false; }
    }

    private static bool Ligado(JsonElement o, string k)
    {
        if (!o.TryGetProperty(k, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => v.TryGetDouble(out var n) && n != 0,
            JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() is { Length: > 0 } t && t is not ("false" or "0" or "none" or "off" or "no"),
            _ => false,
        };
    }

    private static string? Texto(JsonElement o, string k)
        => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s ? s : null;

    private static string? TextoOuNumero(JsonElement o, string k)
    {
        if (!o.TryGetProperty(k, out var v)) return null;
        var s = v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static long? Numero(JsonElement o, string k)
    {
        if (!o.TryGetProperty(k, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var m)) return m;
        return null;
    }
}

/// <summary>O lote do arquivo: junta ate 50 mensagens por ate 20 s (o resgate junta 10 por 6 s; aqui pode esperar).</summary>
public sealed class AgrupadorDoArquivo
{
    private readonly object _trava = new();
    private int _quantas;
    private DateTime? _primeira;

    /// <summary>Mais uma mensagem gravada. Devolve true quando o lote encheu (manda ja).</summary>
    public bool Adicionar(DateTime agora)
    {
        lock (_trava)
        {
            _primeira ??= agora;
            _quantas++;
            return _quantas >= ChatArquivo.TetoMensagens;
        }
    }

    /// <summary>O lote ja pode sair (cheio, ou com a primeira de 20 s atras)? Saindo, zera.</summary>
    public bool Pronto(DateTime agora)
    {
        lock (_trava)
        {
            if (_quantas == 0 || _primeira is null) return false;
            if (_quantas < ChatArquivo.TetoMensagens && agora - _primeira.Value < ChatArquivo.Janela) return false;
            _quantas = 0; _primeira = null;
            return true;
        }
    }

    public int Esperando { get { lock (_trava) return _quantas; } }
}
