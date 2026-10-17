using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pdv.Nucleo;

/// <summary>Onde o login automatico do Gestor esta.</summary>
public enum FaseLogin
{
    /// <summary>Nada em andamento (nunca tentou, ou a ultima tentativa ja terminou e foi registrada).</summary>
    Ocioso,
    /// <summary>Pedindo as credenciais ao ERP.</summary>
    Credenciais,
    /// <summary>E-mail e senha preenchidos e enviados; esperando a tela seguinte.</summary>
    Preencheu,
    /// <summary>A escolha do canal apareceu e o e-mail foi escolhido.</summary>
    CanalEmail,
    /// <summary>A tela do codigo apareceu; pedindo o codigo ao ERP (que o pede ao dono).</summary>
    PedirCodigo,
    /// <summary>Consultando o codigo a cada 5 s, por ate 15 min.</summary>
    EsperandoCodigo,
    /// <summary>O codigo foi digitado e confirmado; esperando o Gestor entrar.</summary>
    Digitou,
    /// <summary>Entrou.</summary>
    Entrou,
    /// <summary>Falhou (o motivo fica em <see cref="LoginDoGestor.Ultimo"/>).</summary>
    Falhou,
}

/// <summary>
/// O LOGIN DO GESTOR DE PEDIDOS SOZINHO (1.0.44, 17/10/2026, pedido do dono): "caso caia, fazer o login
/// automatico; na tela do codigo escolher 'enviar por e-mail'; eu recebo a solicitacao no meu WhatsApp, respondo
/// com o codigo e fica automatico, sem o funcionario fazer nada".
///
/// Esta classe e a parte PURA: a maquina de estados, os tetos, o contrato com a edge <c>gestor-login</c> do ERP
/// e o mascaramento. Quem fala com a nuvem e com a pagina e <see cref="FluxoLoginGestor"/>; quem a injeta na tela
/// e o ServicoLoginGestor do caixa.
///
/// Os tetos, combinados na espec:
///  · 1 tentativa a cada <see cref="EntreTentativas"/> por caixa (contada do comeco da tentativa);
///  · <see cref="FalhasParaPausar"/> falhas seguidas = o automatico para por <see cref="Pausa"/> e o ERP avisa o
///    dono (o <c>usado</c> leva <c>pausado_ate</c>);
///  · o codigo e consultado a cada <see cref="IntervaloConsulta"/> por ate <see cref="PrazoDoCodigo"/>;
///  · "Entrar agora" na Configuracao (forcado) passa pelo teto e pela pausa, mas continua contando as falhas.
/// Desligado no ERP, sem rede e cancelado NAO sao falhas: nao contam para a pausa.
/// </summary>
public sealed class LoginDoGestor
{
    public const string Edge = "gestor-login";
    /// <summary>Prazo de cada chamada da borda (o mesmo da raspadinha-chat).</summary>
    public static readonly TimeSpan Prazo = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan IntervaloConsulta = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PrazoDoCodigo = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan EntreTentativas = TimeSpan.FromMinutes(10);
    /// <summary>Quanto esperar a tela seguinte (depois de enviar a senha, ou o codigo).</summary>
    public static readonly TimeSpan EsperaDaTela = TimeSpan.FromSeconds(30);
    public const int FalhasParaPausar = 3;
    public static readonly TimeSpan Pausa = TimeSpan.FromHours(2);

    public FaseLogin Fase { get; private set; } = FaseLogin.Ocioso;
    public int FalhasSeguidas { get; private set; }
    public DateTime? PausadoAte { get; private set; }
    public DateTime? UltimaTentativa { get; private set; }
    public DateTime? EsperandoDesde { get; private set; }
    public string? PedidoId { get; private set; }
    /// <summary>O resultado da ultima tentativa, para a Configuracao: "entrou 10:12", "falhou 10:12 (motivo)", "sem rede 10:12".</summary>
    public string? Ultimo { get; private set; }
    /// <summary>A ultima resposta de <c>credenciais</c> disse que o automatico esta desligado no ERP.</summary>
    public bool DesligadoNoErp { get; private set; }

    public bool EmAndamento => Fase is not (FaseLogin.Ocioso or FaseLogin.Entrou or FaseLogin.Falhou);

    /// <summary>Por que NAO comecar uma tentativa agora; nulo = pode. <paramref name="forcado"/> e o "Entrar agora".</summary>
    public string? PorQueNao(DateTime agora, bool forcado)
    {
        if (EmAndamento) return "ja esta entrando";
        if (forcado) return null;
        if (PausadoAte is { } p && agora < p) return $"pausado ate {p:HH\\:mm} ({FalhasParaPausar} falhas seguidas)";
        if (UltimaTentativa is { } u && agora - u < EntreTentativas)
            return $"ja tentei ha {(int)(agora - u).TotalMinutes} min (1 a cada {EntreTentativas.TotalMinutes:0} min)";
        return null;
    }

    public void Comecar(DateTime agora)
    {
        Fase = FaseLogin.Credenciais;
        UltimaTentativa = agora;
        EsperandoDesde = null;
        PedidoId = null;
    }

    public void Preencheu() => Fase = FaseLogin.Preencheu;
    public void CanalEmail() => Fase = FaseLogin.CanalEmail;
    public void PedirCodigo() => Fase = FaseLogin.PedirCodigo;

    public void EsperandoCodigo(string id, DateTime agora)
    {
        Fase = FaseLogin.EsperandoCodigo;
        PedidoId = id;
        EsperandoDesde = agora;
    }

    public bool CodigoExpirou(DateTime agora) => EsperandoDesde is { } d && agora - d > PrazoDoCodigo;

    public void Digitou() => Fase = FaseLogin.Digitou;

    public void Entrou(DateTime agora)
    {
        Fase = FaseLogin.Entrou;
        FalhasSeguidas = 0;
        PausadoAte = null;
        DesligadoNoErp = false;
        Ultimo = $"entrou {agora:HH\\:mm}";
        PedidoId = null;
        EsperandoDesde = null;
    }

    /// <summary>Uma falha de verdade (senha, codigo, tela). Devolve true quando esta falha pausou o automatico.</summary>
    public bool Falhou(string motivo, DateTime agora)
    {
        Fase = FaseLogin.Falhou;
        FalhasSeguidas++;
        Ultimo = $"falhou {agora:HH\\:mm} ({motivo})";
        PedidoId = null;
        EsperandoDesde = null;
        if (FalhasSeguidas < FalhasParaPausar) return false;
        PausadoAte = agora + Pausa;
        return true;
    }

    /// <summary>O ERP disse que o automatico esta desligado: nao e falha, mas respeita o teto (nao pergunta a cada 5 s).</summary>
    public void Desligado(DateTime agora)
    {
        Fase = FaseLogin.Ocioso;
        DesligadoNoErp = true;
        Ultimo = $"desligado no ERP {agora:HH\\:mm}";
        PedidoId = null;
        EsperandoDesde = null;
    }

    /// <summary>A nuvem nao respondeu (sem rede, sem sessao, borda fora do ar): nao e falha.</summary>
    public void SemNuvem(string motivo, DateTime agora)
    {
        Fase = FaseLogin.Ocioso;
        Ultimo = $"{motivo} {agora:HH\\:mm}";
        PedidoId = null;
        EsperandoDesde = null;
    }

    /// <summary>A tentativa foi cancelada no meio (o controle do chat foi recriado): nao e falha e nao fica registro.</summary>
    public void Cancelado()
    {
        Fase = FaseLogin.Ocioso;
        PedidoId = null;
        EsperandoDesde = null;
    }

    /// <summary>O Gestor ja esta logado: nada muda, nada e contado.</summary>
    public void JaLogado() => Fase = Fase is FaseLogin.Ocioso or FaseLogin.Entrou or FaseLogin.Falhou ? Fase : FaseLogin.Ocioso;

    /// <summary>A linha da Configuracao: "Login do Gestor: automatico (ERP) | ultimo: entrou 10:12".</summary>
    public string LinhaDaConfiguracao()
    {
        var modo = DesligadoNoErp ? "desligado no ERP" : "automatico (ERP)";
        var linha = $"Login do Gestor: {modo} | ultimo: {Ultimo ?? "nenhum"}";
        if (PausadoAte is { } p) linha += $" | pausado ate {p:HH\\:mm}";
        return linha;
    }

    // ── o contrato com a edge gestor-login ────────────────────────────────────
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string CorpoCredenciais(string terminal) => JsonSerializer.Serialize(new Dictionary<string, object?> { ["acao"] = "credenciais", ["terminal"] = terminal }, Json);
    public static string CorpoPedirCodigo(string terminal) => JsonSerializer.Serialize(new Dictionary<string, object?> { ["acao"] = "pedir_codigo", ["terminal"] = terminal }, Json);
    public static string CorpoCodigo(string terminal, string id) => JsonSerializer.Serialize(new Dictionary<string, object?> { ["acao"] = "codigo", ["terminal"] = terminal, ["id"] = id }, Json);

    /// <summary>
    /// <c>usado</c>: o fim da tentativa, deu certo ou nao. <paramref name="pausadoAte"/> preenchido = 3 falhas seguidas,
    /// e o ERP avisa o dono pelo mesmo caminho dos avisos.
    /// </summary>
    public static string CorpoUsado(string terminal, string? id, bool ok, string? motivo, DateTime? pausadoAte)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["acao"] = "usado",
            ["terminal"] = terminal,
            ["id"] = id,
            ["ok"] = ok,
            ["motivo"] = motivo,
            ["pausado_ate"] = pausadoAte?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        }, Json);

    /// <summary>A resposta de <c>credenciais</c>. <c>SemNuvem</c> = a nuvem nao respondeu de verdade (nao conta falha).</summary>
    public sealed record Credenciais(bool Ok, string? Email, string? Senha, string? Motivo, bool Desligado = false, bool SemNuvem = false);

    public static Credenciais LerCredenciais(int status, string corpo)
    {
        if (MotivoDoStatus(status, corpo) is { } m) return new Credenciais(false, null, null, m.Motivo, SemNuvem: m.SemNuvem);
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return new Credenciais(false, null, null, "resposta invalida");
            if (r.TryGetProperty("ligado", out var lg) && lg.ValueKind == JsonValueKind.False) return new Credenciais(false, null, null, "desligado no ERP", Desligado: true);
            if (r.TryGetProperty("erro", out var er) && er.ValueKind == JsonValueKind.String) return new Credenciais(false, null, null, er.GetString() ?? "erro");
            var email = r.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            var senha = r.TryGetProperty("senha", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(senha)) return new Credenciais(false, null, null, "credenciais incompletas");
            return new Credenciais(true, email.Trim(), senha, null);
        }
        catch (JsonException) { return new Credenciais(false, null, null, "resposta invalida"); }
    }

    public static (string? Id, string? Motivo, bool SemNuvem) LerPedido(int status, string corpo)
    {
        if (MotivoDoStatus(status, corpo) is { } m) return (null, m.Motivo, m.SemNuvem);
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("erro", out var er) && er.ValueKind == JsonValueKind.String) return (null, er.GetString() ?? "erro", false);
            var id = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("id", out var i) ? (i.ValueKind == JsonValueKind.String ? i.GetString() : i.ToString()) : null;
            return string.IsNullOrWhiteSpace(id) ? (null, "pedido sem id", false) : (id, null, false);
        }
        catch (JsonException) { return (null, "resposta invalida", false); }
    }

    public sealed record RespostaCodigo(string? Codigo, bool Esperando, bool Expirado, string? Motivo);

    private static readonly Regex FormatoDoCodigo = new(@"^\d{4,8}$", RegexOptions.Compiled);

    /// <summary>Uma consulta do codigo. Sem resposta da nuvem = so esperar (a proxima consulta vem em 5 s).</summary>
    public static RespostaCodigo LerCodigo(int status, string corpo)
    {
        if (MotivoDoStatus(status, corpo) is { } m) return new RespostaCodigo(null, m.SemNuvem, false, m.Motivo);
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return new RespostaCodigo(null, false, false, "resposta invalida");
            if (r.TryGetProperty("codigo", out var c) && c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString()))
            {
                var cod = c.GetString()!.Trim();
                return FormatoDoCodigo.IsMatch(cod) ? new RespostaCodigo(cod, false, false, null) : new RespostaCodigo(null, false, false, "codigo fora do formato");
            }
            if (r.TryGetProperty("expirado", out var ex) && ex.ValueKind == JsonValueKind.True) return new RespostaCodigo(null, false, true, null);
            if (r.TryGetProperty("erro", out var er) && er.ValueKind == JsonValueKind.String) return new RespostaCodigo(null, false, false, er.GetString() ?? "erro");
            if (r.TryGetProperty("esperando", out var es) && es.ValueKind == JsonValueKind.True) return new RespostaCodigo(null, true, false, null);
            return new RespostaCodigo(null, false, false, "resposta sem codigo");
        }
        catch (JsonException) { return new RespostaCodigo(null, false, false, "resposta invalida"); }
    }

    private static (string Motivo, bool SemNuvem)? MotivoDoStatus(int status, string corpo)
    {
        switch (status)
        {
            case -1: return ("sem rede", true);
            case 0: return ("sem resposta da nuvem", true);
            case 401: return ("sem sessao na nuvem", true);
            case 404: return ($"a borda {Edge} ainda nao esta no ar", true);
            case >= 200 and < 300: return null;
            default:
                var erro = ErroDoCorpo(corpo);
                return (erro ?? $"http {status}", false);
        }
    }

    private static string? ErroDoCorpo(string corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return null;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("erro", out var er) && er.ValueKind == JsonValueKind.String
                ? er.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    // ── o mascaramento ────────────────────────────────────────────────────────
    private static readonly Regex Emails = new(@"[\w.+-]+@[\w-]+(\.[\w-]+)+", RegexOptions.Compiled);
    private static readonly Regex Digitos = new(@"(?<!\d)\d{4,8}(?!\d)", RegexOptions.Compiled);

    /// <summary>
    /// Nenhum e-mail, senha ou codigo em linha de log ou diag. Primeiro os segredos pelo VALOR (a senha nao tem
    /// formato), depois pelo FORMATO: e-mail vira email@####, qualquer numero de 4 a 8 digitos vira ####.
    /// Horas (10:12) e numeros curtos ficam.
    /// </summary>
    public static string Mascarar(string? texto, params string?[]? segredos)
    {
        if (string.IsNullOrEmpty(texto)) return "";
        var t = texto;
        if (segredos is not null)
            foreach (var s in segredos.Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => s!.Length))
                t = t.Replace(s!, "***", StringComparison.Ordinal);
        t = Emails.Replace(t, "email@####");
        t = Digitos.Replace(t, "####");
        return t;
    }
}

/// <summary>
/// O ROTEIRO do login sozinho, de ponta a ponta, com a nuvem, a pagina e o relogio INJETADOS (a suite roda tudo com
/// uma nuvem e uma pagina de mentira e o relogio parado). A tela do caixa liga o de verdade: a nuvem e
/// <c>Servicos.Nuvem().FuncaoAsync("gestor-login", corpo, prazo)</c>, a pagina e o <c>ExecuteScriptAsync</c> do
/// WebView2 do chat (que devolve o JSON do valor), a espera e o Task.Delay.
///
/// O que a pagina responde (ScriptLogin, Telas/ChatIfood.xaml.cs): <c>pdvLoginTela()</c> = login | canal | codigo |
/// logado | carregando | outra; <c>pdvLoginPreencher(email, senha)</c> = enviado | sem_campos; <c>pdvLoginEscolherEmail()</c>
/// = true | false; <c>pdvLoginDigitar(codigo)</c> = enviado | sem_campo | codigo_invalido.
/// </summary>
public sealed class FluxoLoginGestor
{
    private readonly LoginDoGestor _estado;
    private readonly string _terminal;
    private readonly Func<string, Task<(int Status, string Corpo)>> _nuvem;
    private readonly Func<string, Task<string>> _pagina;
    private readonly Action<string> _diag;
    private readonly Func<DateTime> _agora;
    private readonly Func<TimeSpan, CancellationToken, Task> _esperar;
    private readonly List<string?> _segredos = new();
    private int _rodando;

    public FluxoLoginGestor(LoginDoGestor estado, string terminal,
        Func<string, Task<(int Status, string Corpo)>> nuvem,
        Func<string, Task<string>> pagina,
        Action<string> diag, Func<DateTime> agora,
        Func<TimeSpan, CancellationToken, Task>? esperar = null)
    {
        _estado = estado;
        _terminal = terminal;
        _nuvem = nuvem;
        _pagina = pagina;
        _diag = diag;
        _agora = agora;
        _esperar = esperar ?? ((ts, ct) => Task.Delay(ts, ct));
    }

    public LoginDoGestor Estado => _estado;
    public bool Rodando => Volatile.Read(ref _rodando) == 1;

    /// <summary>Uma tentativa inteira. Devolve true se entrou. Nunca lanca (cancelado inclusive).</summary>
    public async Task<bool> TentarAsync(bool forcado, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _rodando, 1, 0) != 0) { Diag("nao agora (ja esta entrando)"); return false; }
        try
        {
            if (_estado.PorQueNao(_agora(), forcado) is { } nao) { Diag($"nao agora ({nao})"); return false; }
            var tela = await TelaAsync().ConfigureAwait(false);
            if (tela == "logado") { _estado.JaLogado(); Diag("o Gestor ja esta logado"); return false; }
            if (tela is not ("login" or "canal" or "codigo")) { Diag($"nao agora (a pagina diz {tela})"); return false; }

            _estado.Comecar(_agora());
            Diag("pedido");
            if (tela == "login")
            {
                var (st, corpo) = await _nuvem(LoginDoGestor.CorpoCredenciais(_terminal)).ConfigureAwait(false);
                var cr = LoginDoGestor.LerCredenciais(st, corpo);
                if (cr.Desligado) { _estado.Desligado(_agora()); Diag("desligado no ERP"); return false; }
                if (cr.SemNuvem) { _estado.SemNuvem(cr.Motivo!, _agora()); Diag(cr.Motivo!); return false; }
                if (!cr.Ok) return await FalharAsync(cr.Motivo ?? "sem credenciais", ct).ConfigureAwait(false);
                _segredos.Add(cr.Email); _segredos.Add(cr.Senha);
                var r = await _pagina("window.pdvLoginPreencher ? window.pdvLoginPreencher(" + JsonSerializer.Serialize(cr.Email) + ", " + JsonSerializer.Serialize(cr.Senha) + ") : 'sem_script'").ConfigureAwait(false);
                r = Limpar(r);
                if (r != "enviado") return await FalharAsync($"a pagina diz {r}", ct).ConfigureAwait(false);
                _estado.Preencheu();
                tela = await EsperarTelaAsync("login", ct).ConfigureAwait(false);
            }
            if (tela == "canal")
            {
                var esc = Limpar(await _pagina("window.pdvLoginEscolherEmail ? window.pdvLoginEscolherEmail() : false").ConfigureAwait(false));
                if (esc != "true") return await FalharAsync("nao achei a opcao e-mail", ct).ConfigureAwait(false);
                _estado.CanalEmail();
                tela = await EsperarTelaAsync("canal", ct).ConfigureAwait(false);
            }
            if (tela == "logado") return await EntrarAsync(null, ct).ConfigureAwait(false);
            if (tela != "codigo") return await FalharAsync(tela == "login" ? "ainda na tela de login" : $"a pagina diz {tela}", ct).ConfigureAwait(false);

            _estado.PedirCodigo();
            var (stP, corpoP) = await _nuvem(LoginDoGestor.CorpoPedirCodigo(_terminal)).ConfigureAwait(false);
            var (id, motivoP, semNuvemP) = LoginDoGestor.LerPedido(stP, corpoP);
            if (id is null)
            {
                if (semNuvemP) { _estado.SemNuvem(motivoP!, _agora()); Diag(motivoP!); return false; }
                return await FalharAsync(motivoP ?? "pedido do codigo recusado", ct).ConfigureAwait(false);
            }
            _estado.EsperandoCodigo(id, _agora());
            Diag("codigo pedido ao dono");

            string? codigo = null;
            while (codigo is null)
            {
                await _esperar(LoginDoGestor.IntervaloConsulta, ct).ConfigureAwait(false);
                if (_estado.CodigoExpirou(_agora())) return await FalharAsync("codigo nao chegou em 15 min", ct).ConfigureAwait(false);
                var (stC, corpoC) = await _nuvem(LoginDoGestor.CorpoCodigo(_terminal, id)).ConfigureAwait(false);
                var rc = LoginDoGestor.LerCodigo(stC, corpoC);
                if (rc.Codigo is not null) { codigo = rc.Codigo; break; }
                if (rc.Expirado) return await FalharAsync("codigo expirou no ERP", ct).ConfigureAwait(false);
                if (!rc.Esperando) return await FalharAsync(rc.Motivo ?? "consulta do codigo recusada", ct).ConfigureAwait(false);
                // alguem pode ter entrado na mao enquanto esperavamos
                if (await TelaAsync().ConfigureAwait(false) == "logado") return await EntrarAsync("alguem entrou na mao", ct).ConfigureAwait(false);
            }
            _segredos.Add(codigo);
            Diag("codigo recebido");
            var dig = Limpar(await _pagina("window.pdvLoginDigitar ? window.pdvLoginDigitar(" + JsonSerializer.Serialize(codigo) + ") : 'sem_script'").ConfigureAwait(false));
            if (dig != "enviado") return await FalharAsync($"a pagina diz {dig}", ct).ConfigureAwait(false);
            _estado.Digitou();
            tela = await EsperarTelaAsync("codigo", ct).ConfigureAwait(false);
            if (tela == "logado") return await EntrarAsync(null, ct).ConfigureAwait(false);
            return await FalharAsync(tela == "codigo" ? "codigo recusado" : $"a pagina diz {tela}", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { _estado.Cancelado(); Diag("cancelado"); return false; }
        catch (Exception ex)
        {
            try { _estado.Falhou(ex.GetType().Name, _agora()); } catch { }
            Diag("falhou (" + ex.GetType().Name + ")");
            return false;
        }
        finally { Volatile.Write(ref _rodando, 0); }
    }

    private async Task<bool> EntrarAsync(string? como, CancellationToken ct)
    {
        var id = _estado.PedidoId;
        _estado.Entrou(_agora());
        Diag(como is null ? "entrou" : $"entrou ({como})");
        await UsadoAsync(id, true, null, null, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> FalharAsync(string motivo, CancellationToken ct)
    {
        var id = _estado.PedidoId;
        var pausou = _estado.Falhou(motivo, _agora());
        Diag(pausou ? $"falhou ({motivo}), automatico pausado por 2 h, dono avisado" : $"falhou ({motivo})");
        await UsadoAsync(id, false, motivo, pausou ? _estado.PausadoAte : null, ct).ConfigureAwait(false);
        return false;
    }

    private async Task UsadoAsync(string? id, bool ok, string? motivo, DateTime? pausadoAte, CancellationToken ct)
    {
        try
        {
            var (st, _) = await _nuvem(LoginDoGestor.CorpoUsado(_terminal, id, ok, motivo, pausadoAte)).ConfigureAwait(false);
            if (st is < 200 or >= 300) Diag($"usado: http {st}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Diag("usado: " + ex.GetType().Name); }
    }

    /// <summary>Espera a tela mudar (ate <see cref="LoginDoGestor.EsperaDaTela"/>), lendo a cada segundo.</summary>
    private async Task<string> EsperarTelaAsync(string atual, CancellationToken ct)
    {
        var inicio = _agora();
        var tela = await TelaAsync().ConfigureAwait(false);
        while (tela == atual && _agora() - inicio < LoginDoGestor.EsperaDaTela)
        {
            await _esperar(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            tela = await TelaAsync().ConfigureAwait(false);
        }
        return tela;
    }

    private async Task<string> TelaAsync()
    {
        try { return Limpar(await _pagina("window.pdvLoginTela ? window.pdvLoginTela() : 'sem_script'").ConfigureAwait(false)); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return "erro " + ex.GetType().Name; }
    }

    /// <summary>O ExecuteScriptAsync devolve o JSON do valor: "login" com aspas, true sem.</summary>
    private static string Limpar(string? bruto)
    {
        var s = (bruto ?? "").Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        return string.IsNullOrEmpty(s) || s == "null" || s == "undefined" ? "sem_script" : s;
    }

    private void Diag(string texto) => _diag("login do gestor: " + LoginDoGestor.Mascarar(texto, _segredos.ToArray()));
}
