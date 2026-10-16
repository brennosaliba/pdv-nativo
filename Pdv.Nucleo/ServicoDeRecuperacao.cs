using System.Net.Http;

namespace Pdv.Nucleo;

/// <summary>
/// 1.0.41: O SERVIÇO DA RECUPERAÇÃO, com portas (a bateria o roda inteiro com HTTP de mentira e relógio
/// falso; a tela liga nas de verdade pelo ServicoRecuperacaoChat). Junta a sessão (<see cref="SessaoSendbird"/>),
/// a varredura (<see cref="Varredura"/>), os gatilhos e a reentrância:
///  (1) Ligar: varredura em 3 s se já há credencial;
///  (2) credencial nova ou de valor diferente: varredura em 2 s;
///  (3) o Gestor recarregou (NavigationCompleted) ou (4) um socket da loja abriu de novo: varredura 5 s
///      depois do próximo LOGI, ou 15 s sem LOGI se já há credencial;
///  (5) o relógio de 10 s (o dono, 16/10: "delivery é agilidade: a cada 10 segundos").
/// Duas rodadas nunca correm juntas (WaitAsync(0)); o portão é conferido a cada rodada; nunca lança.
/// A chave de sessão nunca sai daqui: toda linha de diag passa por <see cref="ChatCaptura.MascararTexto"/>.
/// </summary>
public sealed class ServicoDeRecuperacao : IDisposable
{
    public static readonly TimeSpan EsperaAoLigar = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan EsperaDaCredencial = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan EsperaDepoisDoLogi = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan EsperaSemLogi = TimeSpan.FromSeconds(15);
    /// <summary>Quanto tempo depois da recarga um LOGI ainda conta como "o LOGI da recarga".</summary>
    public static readonly TimeSpan JanelaDoLogi = TimeSpan.FromSeconds(60);

    public required Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Http { get; init; }
    public Func<DateTime> Agora { get; init; } = () => DateTime.Now;
    public required Action<string> Entregar { get; init; }
    public Action<string> Diag { get; init; } = _ => { };
    public Func<bool> Portao { get; init; } = () => true;
    /// <summary>O user_id da loja aprendido pelo canal de cliente (ServicoConversaChat.UsuarioDaLoja), ou null.</summary>
    public Func<string?> UsuarioDaLoja { get; init; } = () => null;
    /// <summary>
    /// Revisão 1.0.41: a loja foi eleita pela lista de canais (o usuário cuja lista traz canal de cliente), para o
    /// resto do caixa aprender (ServicoConversaChat.AprenderUsuarioDaLoja). Nunca lança.
    /// </summary>
    public Action<string> LojaDescoberta { get; init; } = _ => { };
    /// <summary>false na bateria: nenhum relógio de verdade; os gatilhos só ficam anotados em <see cref="Agendada"/>.</summary>
    public bool ComRelogios { get; init; } = true;
    public TimeSpan PrazoDaChamada { get; init; } = RecuperacaoSendbird.PrazoDaChamada;
    public TimeSpan PrazoDaVarredura { get; init; } = RecuperacaoSendbird.PrazoDaVarredura;

    public SessaoSendbird Sessao { get; } = new();
    private Varredura? _varredura;
    private readonly SemaphoreSlim _umaPorVez = new(1, 1);
    private readonly object _trava = new();
    private System.Threading.Timer? _relogio, _gatilho;
    private DateTime _recargaEm = DateTime.MinValue;
    private TimeSpan? _agendada;
    private int _ligado, _puladas;
    private string? _ultimoMotivo;

    /// <summary>A varredura (as marcas, o dedup e o recuo moram nela). Existe depois de <see cref="Ligar"/>.</summary>
    public Varredura Varredura => _varredura ?? throw new InvalidOperationException("ligue o serviço antes");

    /// <summary>O último atraso agendado para uma varredura (só a bateria lê), ou null.</summary>
    public TimeSpan? Agendada { get { lock (_trava) return _agendada; } }

    /// <summary>Quantas rodadas o WaitAsync(0) pulou porque outra estava em curso.</summary>
    public int Puladas => Volatile.Read(ref _puladas);

    public bool Ligado => Volatile.Read(ref _ligado) == 1;

    /// <summary>Liga: monta a varredura com as portas, o relógio de 10 s e, com credencial, a primeira em 3 s. Idempotente.</summary>
    public void Ligar()
    {
        if (Interlocked.Exchange(ref _ligado, 1) == 1) return;
        _varredura = new Varredura
        {
            Http = Http, Agora = Agora, Entregar = Entregar, Diag = Diag, Portao = Portao,
            Credencial = () => Sessao.Atual(UsuarioDaLoja()),
            Candidatas = () => Sessao.Candidatas(),
            UsuarioDaLoja = UsuarioDaLoja,
            LojaDescoberta = u => { try { LojaDescoberta(u); } catch { } },
            Cabecalhos = () => Sessao.Cabecalhos,
            Invalidar = (m, chave) => Sessao.Invalidar(m, chave),
            PrazoDaChamada = PrazoDaChamada, PrazoDaVarredura = PrazoDaVarredura,
        };
        if (ComRelogios)
        {
            _gatilho = new System.Threading.Timer(_ => _ = VarrerAsync("gatilho"), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _relogio = new System.Threading.Timer(_ => _ = VarrerAsync("relogio"), null, RecuperacaoSendbird.Intervalo, RecuperacaoSendbird.Intervalo);
        }
        if (Sessao.Atual(UsuarioDaLoja()) is not null) Agendar(EsperaAoLigar);
        Diag("recuperacao ligada");
    }

    /// <summary>Um quadro recebido pelo WebSocket: a marca d'água e o dedup aprendem com ele. Nunca lança.</summary>
    public void Viu(string? payload)
    {
        try { _varredura?.Viu(payload); } catch { }
    }

    /// <summary>
    /// Uma credencial vista pela tela (URL, LOGI ou cabeçalho). Quando a melhor muda (nasce ou troca de
    /// valor), varredura em 2 s. Um LOGI depois de uma recarga: varredura em 5 s. Devolve se mudou. Nunca lança.
    /// </summary>
    public bool OferecerCredencial(CredencialSendbird? c)
    {
        try
        {
            if (c is null) return false;
            var mudou = Sessao.Oferecer(c);
            if (mudou)
            {
                Diag($"recuperacao sessao fonte={c.Fonte} (nova)");
                Agendar(EsperaDaCredencial);
            }
            if (c.Fonte == SessaoSendbird.FonteLogi) LogiVisto();
            return mudou;
        }
        catch { return false; }
    }

    public void OferecerCabecalhos(CabecalhosDoSdk? cab)
    {
        try { Sessao.OferecerCabecalhos(cab); } catch { }
    }

    /// <summary>
    /// A chave <paramref name="chave"/> não vale (401/403 numa chamada do próprio SDK que a levou, ou da varredura).
    /// Só ela é condenada; sem a chave, nada é condenado (revisão 1.0.41: o 401 do entregador não derruba a loja).
    /// </summary>
    public void Invalidar(string? motivo, string? chave)
    {
        try
        {
            Sessao.Invalidar(motivo, chave);
            Diag("recuperacao sessao invalidada (" + (ConversaRaspadinha.NomeSeguroDoDiag(motivo) ?? "?") + (SessaoSendbird.ChaveValida(chave) ? "" : ", sem chave: nada condenado") + ")");
        }
        catch { }
    }

    /// <summary>
    /// O Gestor recarregou (NavigationCompleted ok) ou um socket da loja abriu de novo: espera o LOGI
    /// (5 s depois dele); sem LOGI, 15 s se já há credencial válida.
    /// </summary>
    public void GestorRecarregou()
    {
        try
        {
            lock (_trava) _recargaEm = Agora();
            if (Sessao.Atual(UsuarioDaLoja()) is not null) Agendar(EsperaSemLogi);
        }
        catch { }
    }

    /// <summary>Um LOGI chegou (mesmo sem key): se havia recarga esperando, varredura em 5 s.</summary>
    public void LogiVisto()
    {
        try
        {
            bool esperando;
            lock (_trava)
            {
                esperando = _recargaEm != DateTime.MinValue && Agora() - _recargaEm <= JanelaDoLogi;
                if (esperando) _recargaEm = DateTime.MinValue;
            }
            if (esperando) Agendar(EsperaDepoisDoLogi);
        }
        catch { }
    }

    private void Agendar(TimeSpan atraso)
    {
        lock (_trava) _agendada = atraso;
        if (!ComRelogios) return;
        try { _gatilho?.Change(atraso, Timeout.InfiniteTimeSpan); } catch { }
    }

    /// <summary>
    /// UMA varredura agora. Null quando outra está em curso (dois tiques juntos = uma rodada) ou o serviço
    /// não está ligado. Nunca lança.
    /// </summary>
    public async Task<ResultadoDaVarredura?> VarrerAsync(string motivo = "manual")
    {
        if (_varredura is not { } v) return null;
        if (!await _umaPorVez.WaitAsync(0).ConfigureAwait(false)) { Interlocked.Increment(ref _puladas); return null; }
        try
        {
            lock (_trava) { _ultimoMotivo = motivo; _agendada = null; }
            return await v.ExecutarAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { Diag("recuperacao varrer: " + ex.GetType().Name); return null; }
        finally { _umaPorVez.Release(); }
    }

    /// <summary>O estado para o sinal: a fonte é a da sessão de AGORA (não a da última rodada).</summary>
    public EstadoDaRecuperacao Estado()
    {
        var e = _varredura?.Estado() ?? new EstadoDaRecuperacao(SessaoSendbird.FonteNenhuma, null, null, 0, 0, 0, null, 0, 0);
        return e with { Fonte = Sessao.Fonte(UsuarioDaLoja()) };
    }

    public string? UltimoMotivo { get { lock (_trava) return _ultimoMotivo; } }

    public void Dispose()
    {
        try { _relogio?.Dispose(); _gatilho?.Dispose(); } catch { }
    }
}
