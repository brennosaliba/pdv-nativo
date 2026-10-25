using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pdv.Nucleo;

/// <summary>
/// O LADO DO APARELHO DO VIGIA (25/10/2026, SQL 220, docs/VIGIA_CONTRATO.md do ERP). O caixa (e a TV, com a copia
/// deste arquivo) manda UM SINAL POR MINUTO para a edge <c>vigia</c>: o estado do chat do iFood (as chaves que o
/// sinal da raspadinha-chat ja leva, mais <c>tela_chat</c>, <c>casca_desde</c>, <c>recargas_hora</c>, <c>cpu_pct</c>,
/// <c>mem_mb</c>, <c>uptime_s</c>, <c>erros_console</c>, <c>suporta</c> e <c>venda_em_andamento</c>) e, quando a
/// tela do chat nao esta ok (ou a cada 10 min quando esta), um print JPEG de ate 1 MB. A resposta do sinal traz os
/// comandos pendentes; o aparelho executa um de cada vez, na ordem, e conta o resultado em <c>concluir</c>.
///
/// Esta classe e PURA (sem rede, sem tela): monta o corpo do sinal, le a resposta, decide se executa ou recusa um
/// comando (venda em andamento, pinpad ocupado, impressao na fila) e mascara os erros do console. Quem liga o relogio,
/// tira o print e executa de verdade e o ServicoVigia do caixa (o ChatCaptador na TV).
///
/// Nunca shell livre: so os comandos da tabela 4 do contrato. Nada de token, JWT ou segredo no estado.
/// </summary>
public static class VigiaDoAparelho
{
    public const string Edge = "vigia";
    public const string UrlEdge = "https://ctwjedradxhlmqmdmbif.supabase.co/functions/v1/vigia";
    public static readonly TimeSpan Cada = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Prazo = TimeSpan.FromSeconds(20);
    /// <summary>Com a tela do chat ok, um print a cada 10 min (para o dono ver como esta).</summary>
    public static readonly TimeSpan PrintQuandoOk = TimeSpan.FromMinutes(10);
    public const int PrintMaximoBytes = 1_000_000;
    public const int EstadoMaximoBytes = 8 * 1024;
    public const int ErrosGuardados = 20;
    public const int ErroMaximoLetras = 300;
    public const int ErroDoConcluirMaximo = 300;
    public const int ResultadoMaximoBytes = 4 * 1024;
    public const string PrefixoDaRecusa = "recusado: ";

    public const string Recarregar = "recarregar";
    public const string RecargaForte = "recarga_forte";
    public const string RelogarGestor = "relogar_gestor";
    public const string GestorInteiro = "gestor_inteiro";
    public const string SoAConversa = "so_a_conversa";
    public const string ReiniciarApp = "reiniciar_app";
    public const string ReiniciarPc = "reiniciar_pc";
    public const string Atualizar = "atualizar";
    public const string ColetarDiagnostico = "coletar_diagnostico";

    /// <summary>Os comandos que esta versao do caixa executa (vai em <c>suporta</c>; sem a lista o servidor nao manda relogar_gestor).</summary>
    public static readonly string[] ComandosDoCaixa =
    {
        Recarregar, RecargaForte, RelogarGestor, GestorInteiro, SoAConversa, ReiniciarApp, ReiniciarPc, Atualizar, ColetarDiagnostico,
    };
    /// <summary>Os comandos que a TV executa (a copia deste arquivo no kiosk usa esta lista).</summary>
    public static readonly string[] ComandosDaTv =
    {
        Recarregar, RecargaForte, RelogarGestor, GestorInteiro, SoAConversa, ReiniciarApp, ReiniciarPc, Atualizar, ColetarDiagnostico,
    };
    public static readonly string[] TelasDoChat = { "ok", "casca", "login", "cortina", "outro" };

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ── a leitura da pagina (o mesmo script no caixa e na TV; so le) ──────────────────────────────
    /// <summary>
    /// Le a pagina do chat: a casca do Gestor (#root vazio), o login (senha, kcContext do Keycloak ou o host
    /// partners-auth), a nossa cortina (#pdv-cortina) e a conversa (a gaveta de Conversas do Gestor). Nunca por classe.
    /// </summary>
    public const string JsLeituraDaTela =
        "(function(){try{var root=document.getElementById('root');var casca=!!root&&root.children.length===0;"
        + "var login=!!document.querySelector('input[type=\"password\"]')||!!window.kcContext||/partners-auth\\.ifood/i.test(String(location.host||''));"
        + "var cortina=!!document.getElementById('pdv-cortina');"
        + "var conversa=!!document.querySelector('[data-testid*=\"consumer-chat\"],[aria-label*=\"Conversas\"],[aria-label*=\"conversas\"]');"
        + "return JSON.stringify({casca:casca,login:login,cortina:cortina,conversa:conversa});}catch(e){return '{}';}})()";

    public sealed record LeituraDaTela(bool Casca, bool Login, bool Cortina, bool Conversa, bool Leu);

    /// <summary>O JSON (com aspas por fora, como o ExecuteScriptAsync devolve) vira a leitura; lixo = nao leu.</summary>
    public static LeituraDaTela LerTela(string? bruto)
    {
        try
        {
            var s = (bruto ?? "").Trim();
            if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = JsonSerializer.Deserialize<string>(s) ?? "";
            if (string.IsNullOrWhiteSpace(s) || s[0] != '{') return new LeituraDaTela(false, false, false, false, false);
            using var d = JsonDocument.Parse(s);
            var r = d.RootElement;
            bool B(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
            return new LeituraDaTela(B("casca"), B("login"), B("cortina"), B("conversa"), r.ValueKind == JsonValueKind.Object && r.EnumerateObject().Any());
        }
        catch { return new LeituraDaTela(false, false, false, false, false); }
    }

    /// <summary>
    /// <c>tela_chat</c> do contrato: ok (conversa visivel e viva), casca, login, cortina, outro. A ordem importa: o login
    /// ganha da casca (a pagina do Keycloak nao tem #root), a cortina so conta com o chat montado.
    /// </summary>
    public static string TelaChat(bool navegadorPronto, LeituraDaTela tela, bool wsVivo)
    {
        if (!navegadorPronto || !tela.Leu) return "outro";
        if (tela.Login) return "login";
        if (tela.Casca) return "casca";
        if (tela.Cortina) return "cortina";
        return tela.Conversa || wsVivo ? "ok" : "outro";
    }

    /// <summary>Print a cada sinal enquanto a tela nao esta ok; a cada 10 min quando esta; nunca na tela de pagamento.</summary>
    public static bool QuerPrint(string telaChat, DateTime? ultimoPrintEm, DateTime agora, bool emPagamento)
    {
        if (emPagamento) return false;
        if (telaChat != "ok") return true;
        return ultimoPrintEm is null || agora - ultimoPrintEm.Value >= PrintQuandoOk;
    }

    // ── os erros do console (sem segredo) ─────────────────────────────────────────────────────────
    private static readonly Regex Jwt = new(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", RegexOptions.Compiled);
    private static readonly Regex Bearer = new(@"(?i)bearer\s+[A-Za-z0-9._-]{8,}", RegexOptions.Compiled);
    private static readonly Regex ParametroSecreto = new(@"(?i)(token|key|secret|senha|password|session|authorization|x-chat-aparelho)(=|:|%3D)\s*[^&\s""']{4,}", RegexOptions.Compiled);
    private static readonly Regex HexLongo = new(@"\b[0-9a-fA-F]{32,}\b", RegexOptions.Compiled);
    private static readonly Regex Emails = new(@"[\w.+-]+@[\w-]+(\.[\w-]+)+", RegexOptions.Compiled);

    /// <summary>Um erro do console sem token, JWT, chave, senha, e-mail nem hex longo; uma linha, ate 300 letras.</summary>
    public static string MascararErro(string? texto)
    {
        var t = (texto ?? "").Replace('\r', ' ').Replace('\n', ' ');
        t = Jwt.Replace(t, "<jwt>");
        t = Bearer.Replace(t, "Bearer <segredo>");
        t = ParametroSecreto.Replace(t, m => m.Groups[1].Value + m.Groups[2].Value + "<segredo>");
        t = HexLongo.Replace(t, "<hex>");
        t = Emails.Replace(t, "email@####");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t.Length > ErroMaximoLetras ? t[..ErroMaximoLetras] : t;
    }

    /// <summary>A lista dos ultimos erros: mascara, descarta vazio e repetido seguido, guarda os 20 mais novos.</summary>
    public static IReadOnlyList<string> AnotarErro(IReadOnlyList<string> atuais, string? texto)
    {
        var m = MascararErro(texto);
        if (m.Length == 0) return atuais;
        if (atuais.Count > 0 && atuais[^1] == m) return atuais;
        var l = new List<string>(atuais) { m };
        if (l.Count > ErrosGuardados) l.RemoveRange(0, l.Count - ErrosGuardados);
        return l;
    }

    // ── o sinal ───────────────────────────────────────────────────────────────────────────────────
    /// <summary>O que o aparelho sabe de si na hora do sinal. <c>EstadoBaseJson</c> = o estado do sinal da raspadinha-chat (as chaves de sempre).</summary>
    public sealed record Leitura(
        string Versao,
        string? TerminalUuid,
        string TelaChat,
        DateTime? CascaDesde,
        bool? WsVivo,
        DateTime? UltimoQuadroEm,
        int RecargasHora,
        double? CpuPct,
        double? MemMb,
        long UptimeS,
        IReadOnlyList<string> ErrosConsole,
        bool? VendaEmAndamento,
        IReadOnlyList<string> Suporta,
        string? EstadoBaseJson,
        string? PrintB64);

    private static string? Utc(DateTime? d) => d?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>O objeto <c>estado</c> do sinal: as chaves de sempre (quando o JSON base e um objeto) mais as do Vigia. Ate 8 KB.</summary>
    public static JsonObject Estado(Leitura l)
    {
        JsonObject o;
        try { o = (!string.IsNullOrWhiteSpace(l.EstadoBaseJson) ? JsonNode.Parse(l.EstadoBaseJson) as JsonObject : null) ?? new JsonObject(); }
        catch { o = new JsonObject(); }
        o["tela_chat"] = Array.IndexOf(TelasDoChat, l.TelaChat) >= 0 ? l.TelaChat : "outro";
        o["casca_desde"] = l.TelaChat == "ok" ? null : Utc(l.CascaDesde);
        if (l.WsVivo is { } ws) o["ws_vivo"] = ws;
        if (l.UltimoQuadroEm is { } uq) o["ultimo_quadro_em"] = Utc(uq);
        o["recargas_hora"] = Math.Clamp(l.RecargasHora, 0, 1000);
        if (l.CpuPct is { } cpu && !double.IsNaN(cpu)) o["cpu_pct"] = Math.Round(Math.Clamp(cpu, 0, 100), 1);
        if (l.MemMb is { } mem && !double.IsNaN(mem)) o["mem_mb"] = Math.Round(Math.Max(0, mem), 1);
        o["uptime_s"] = Math.Max(0, l.UptimeS);
        var erros = new JsonArray();
        foreach (var e in l.ErrosConsole.TakeLast(ErrosGuardados)) erros.Add((JsonNode?)MascararErro(e));
        o["erros_console"] = erros;
        var sup = new JsonArray();
        foreach (var s in l.Suporta.Where(s => Regex.IsMatch(s, "^[a-z_]{3,40}$")).Distinct()) sup.Add((JsonNode?)s);
        o["suporta"] = sup;
        if (l.VendaEmAndamento is { } v) o["venda_em_andamento"] = v;
        // o teto de 8 KB: o que pesa sai primeiro (quadros, sdk), depois os erros
        foreach (var pesada in new[] { "quadros", "sdk", "erros_console" })
        {
            if (System.Text.Encoding.UTF8.GetByteCount(o.ToJsonString(Json)) <= EstadoMaximoBytes) break;
            if (pesada == "erros_console") o["erros_console"] = new JsonArray(); else o.Remove(pesada);
        }
        return o;
    }

    /// <summary>O corpo de <c>sinal</c>. O print entra so quando cabe (ate 1 MB de base64 decodificado).</summary>
    public static string CorpoSinal(Leitura l)
    {
        var o = new JsonObject
        {
            ["acao"] = "sinal",
            ["versao"] = (l.Versao ?? "?").Length > 40 ? l.Versao![..40] : (l.Versao ?? "?"),
            ["estado"] = Estado(l),
        };
        if (!string.IsNullOrWhiteSpace(l.TerminalUuid)) o["terminal_uuid"] = l.TerminalUuid;
        if (!string.IsNullOrEmpty(l.PrintB64) && PrintCabe(l.PrintB64)) o["print_b64"] = l.PrintB64;
        return o.ToJsonString(Json);
    }

    /// <summary>O base64 do print decodifica em ate 1 MB (o prefixo data:image/jpeg;base64, e aceito).</summary>
    public static bool PrintCabe(string b64)
    {
        var s = b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? b64[(b64.IndexOf(',') + 1)..] : b64;
        if (s.Length == 0) return false;
        var bytes = (long)s.Length * 3 / 4 - (s.EndsWith("==") ? 2 : s.EndsWith('=') ? 1 : 0);
        return bytes <= PrintMaximoBytes;
    }

    public sealed record Comando(string Id, string Nome, string? Motivo, DateTime? ExpiraEm);
    public sealed record RespostaDoSinal(bool Ok, string? Motivo, bool Pausado, IReadOnlyList<Comando> Comandos, int ProximoSinalS, bool SemSql220);

    /// <summary>A resposta do sinal. 404 sem_sql_220 = continuar mandando (nada quebra); 401 = token/sessao recusados.</summary>
    public static RespostaDoSinal LerSinal(int status, string corpo)
    {
        var vazio = Array.Empty<Comando>();
        if (status == 404) return new RespostaDoSinal(false, "sem_sql_220", false, vazio, 60, true);
        if (status == 401) return new RespostaDoSinal(false, "nao_autorizado", false, vazio, 60, false);
        if (status is < 200 or >= 300) return new RespostaDoSinal(false, status <= 0 ? "sem rede" : MotivoDoCorpo(corpo) ?? $"http {status}", false, vazio, 60, false);
        try
        {
            using var d = JsonDocument.Parse(corpo);
            var r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
                return new RespostaDoSinal(false, MotivoDoCorpo(corpo) ?? "resposta invalida", false, vazio, 60, false);
            var pausado = r.TryGetProperty("pausado", out var p) && p.ValueKind == JsonValueKind.True;
            var prox = r.TryGetProperty("proximo_sinal_s", out var ps) && ps.ValueKind == JsonValueKind.Number && ps.TryGetInt32(out var n) ? Math.Clamp(n, 30, 600) : 60;
            var lista = new List<Comando>();
            if (r.TryGetProperty("comandos", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var c in cs.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) continue;
                    var id = c.TryGetProperty("id", out var iv) && iv.ValueKind == JsonValueKind.String ? iv.GetString() : null;
                    var nome = c.TryGetProperty("comando", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(nome) || !Regex.IsMatch(nome, "^[a-z_]{3,40}$")) continue;
                    string? motivo = null;
                    if (c.TryGetProperty("args", out var av) && av.ValueKind == JsonValueKind.Object && av.TryGetProperty("motivo", out var mv) && mv.ValueKind == JsonValueKind.String)
                        motivo = MascararErro(mv.GetString());
                    DateTime? exp = null;
                    if (c.TryGetProperty("expira_em", out var ev) && ev.ValueKind == JsonValueKind.String && DateTime.TryParse(ev.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                        exp = dt;
                    lista.Add(new Comando(id!, nome!, motivo, exp));
                }
            return new RespostaDoSinal(true, null, pausado, lista, prox, false);
        }
        catch { return new RespostaDoSinal(false, "resposta invalida", false, vazio, 60, false); }
    }

    private static string? MotivoDoCorpo(string corpo)
    {
        try
        {
            using var d = JsonDocument.Parse(corpo);
            return d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("motivo", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        }
        catch { return null; }
    }

    // ── os comandos ───────────────────────────────────────────────────────────────────────────────
    public enum Decisao { Executar, Recusar, Desconhecido }

    /// <summary>O que o aparelho vive na hora de decidir: so o que pode barrar um comando.</summary>
    public sealed record Contexto(bool VendaEmAndamento, bool PinpadOcupado, bool ImpressaoNaFila, IReadOnlyList<string> Suporta, bool Expirado = false);

    /// <summary>
    /// Executa, recusa (com o motivo) ou nao conhece. Reiniciar o programa recusa com venda em andamento; reiniciar o PC
    /// recusa com venda, pinpad ocupado ou impressao na fila; comando fora de <c>suporta</c> e recusado; expirado nem roda.
    /// </summary>
    public static (Decisao Decisao, string? Motivo) Decidir(string comando, Contexto c)
    {
        if (!c.Suporta.Contains(comando)) return (Decisao.Desconhecido, "comando nao suportado");
        if (c.Expirado) return (Decisao.Recusar, "expirou antes de executar");
        if (comando is ReiniciarApp or ReiniciarPc or Atualizar)
        {
            if (c.VendaEmAndamento) return (Decisao.Recusar, "venda em andamento");
            if (comando is ReiniciarPc or Atualizar)
            {
                if (c.PinpadOcupado) return (Decisao.Recusar, "pinpad ocupado");
                if (c.ImpressaoNaFila) return (Decisao.Recusar, "impressao na fila");
            }
        }
        return (Decisao.Executar, null);
    }

    /// <summary>Quem mata o programa conclui ANTES de executar (com vai_executar=true).</summary>
    public static bool MataOPrograma(string comando) => comando is ReiniciarApp or ReiniciarPc;

    public static string ErroDeRecusa(string motivo) => PrefixoDaRecusa + motivo;

    /// <summary>O corpo de <c>concluir</c>: erro ate 300 letras (mascarado), resultado ate 4 KB (senao so o resumo).</summary>
    public static string CorpoConcluir(string? terminalUuid, string comandoId, JsonObject? resultado, string? erro)
    {
        var o = new JsonObject { ["acao"] = "concluir", ["comando_id"] = comandoId };
        if (!string.IsNullOrWhiteSpace(terminalUuid)) o["terminal_uuid"] = terminalUuid;
        var res = resultado ?? new JsonObject();
        if (System.Text.Encoding.UTF8.GetByteCount(res.ToJsonString(Json)) > ResultadoMaximoBytes)
            res = new JsonObject { ["resumo"] = "resultado grande demais; ver o diagnostico no aparelho" };
        o["resultado"] = res;
        o["erro"] = erro is null ? null : (MascararErro(erro) is var e && e.Length > ErroDoConcluirMaximo ? e[..ErroDoConcluirMaximo] : e);
        return o.ToJsonString(Json);
    }

    /// <summary>O resultado de concluir: ok, repetido (ja tinha concluido) ou o motivo da recusa da edge.</summary>
    public static (bool Ok, string? Motivo) LerConcluir(int status, string corpo)
    {
        if (status is < 200 or >= 300) return (false, status <= 0 ? "sem rede" : MotivoDoCorpo(corpo) ?? $"http {status}");
        try
        {
            using var d = JsonDocument.Parse(corpo);
            var r = d.RootElement;
            return r.ValueKind == JsonValueKind.Object && r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                ? (true, null) : (false, MotivoDoCorpo(corpo) ?? "resposta invalida");
        }
        catch { return (false, "resposta invalida"); }
    }

    /// <summary>As 40 primeiras linhas de um diagnostico, para o resultado de coletar_diagnostico (mascaradas).</summary>
    public static JsonObject ResultadoDoDiagnostico(string? caminho, string? texto)
    {
        var linhas = new JsonArray();
        foreach (var l in (texto ?? "").Split('\n').Take(40)) linhas.Add((JsonNode?)MascararErro(l));
        return new JsonObject { ["arquivo"] = caminho is null ? null : Path.GetFileName(caminho), ["linhas"] = linhas };
    }

    /// <summary>A hora do proximo sinal: o que a edge pediu (30 s a 10 min), ou 60 s.</summary>
    public static TimeSpan ProximoIntervalo(RespostaDoSinal? r) => TimeSpan.FromSeconds(r is { Ok: true } ? Math.Clamp(r.ProximoSinalS, 30, 600) : 60);

    /// <summary>Quantas recargas na ultima hora (a lista guarda as horas das recargas; as velhas saem).</summary>
    public static int RecargasNaHora(List<DateTime> recargas, DateTime agora)
    {
        recargas.RemoveAll(r => agora - r > TimeSpan.FromHours(2));
        return recargas.Count(r => agora - r <= TimeSpan.FromHours(1));
    }
}
