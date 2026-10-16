namespace Pdv.Nucleo;

/// <summary>
/// A GUARDA DE UMA RODADA COM DONO E HORA (1.0.41, 16/10/2026, Savassi). O sinal de 60 s do chat morreu em
/// silêncio duas vezes (12:32 e 13:26) com o PDV vivo: a rodada anterior ficou presa num await sem prazo e o
/// <c>SemaphoreSlim.WaitAsync(0)</c> de todas as seguintes voltava falso, sem uma linha de diag. O ERP então
/// alarmou "chat caiu", a equipe recriou o chat 6 vezes e 3 pedidos ficaram sem resposta.
///
/// Aqui: quem entra ganha uma GERAÇÃO; a rodada anotada a cada etapa; uma rodada presa há mais de 90 s é
/// ABANDONADA (a nova entra e ganha a geração seguinte); o <c>Sair</c> da abandonada não solta a guarda da
/// nova. Puro e seguro entre threads.
/// </summary>
public sealed class GuardaDaRodada
{
    public static readonly TimeSpan Abandono = TimeSpan.FromSeconds(90);
    /// <summary>Uma etapa que passa disto merece uma linha de diag.</summary>
    public static readonly TimeSpan EtapaLenta = TimeSpan.FromSeconds(5);

    /// <summary>A entrada concedida: a geração desta rodada e, quando houve, a etapa e há quanto tempo a anterior estava presa.</summary>
    public sealed record Entrada(int Geracao, string? EtapaAbandonada, TimeSpan PresaHa);

    private readonly object _trava = new();
    private int _geracao;
    private bool _emCurso;
    private DateTime _desde, _etapaDesde;
    private string _etapa = "";

    /// <summary>
    /// Tenta entrar. Null quando uma rodada está em curso há menos de 90 s (a chamada volta e tenta na
    /// próxima). Com a anterior presa há 90 s ou mais, abandona-a e concede a geração nova.
    /// </summary>
    public Entrada? Entrar(DateTime agora)
    {
        lock (_trava)
        {
            string? abandonada = null;
            var presa = TimeSpan.Zero;
            if (_emCurso)
            {
                presa = agora - _desde;
                if (presa < Abandono) return null;
                abandonada = _etapa.Length == 0 ? "inicio" : _etapa;
            }
            _geracao++;
            _emCurso = true;
            _desde = agora;
            _etapaDesde = agora;
            _etapa = "inicio";
            return new Entrada(_geracao, abandonada, presa);
        }
    }

    /// <summary>
    /// Anota a etapa em que a rodada <paramref name="geracao"/> está. Devolve (etapa anterior, quanto levou)
    /// para o diag dizer "etapa X levou N s"; null quando a geração não é a atual (rodada abandonada).
    /// </summary>
    public (string Etapa, TimeSpan Levou)? Etapa(int geracao, string etapa, DateTime agora)
    {
        lock (_trava)
        {
            if (!_emCurso || geracao != _geracao) return null;
            var anterior = (_etapa, agora - _etapaDesde);
            _etapa = etapa;
            _etapaDesde = agora;
            return anterior;
        }
    }

    /// <summary>Sai. Só solta a guarda se a geração ainda é a atual; a rodada abandonada devolve false e não toca em nada.</summary>
    public bool Sair(int geracao)
    {
        lock (_trava)
        {
            if (!_emCurso || geracao != _geracao) return false;
            _emCurso = false;
            _etapa = "";
            return true;
        }
    }

    /// <summary>O que está em curso: a etapa e desde quando (null quando nada).</summary>
    public (string Etapa, DateTime Desde)? EmCurso
    {
        get { lock (_trava) return _emCurso ? (_etapa, _desde) : null; }
    }

    public int Geracao { get { lock (_trava) return _geracao; } }
}
