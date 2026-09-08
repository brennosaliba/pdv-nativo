namespace Pdv.Nucleo;

/// <summary>
/// A ida ao painel na hora do login, com as duas travas que a loja exige: NÃO PERGUNTA
/// DE NOVO ENQUANTO NÃO ADIANTA, e nunca duas ao mesmo tempo.
///
/// POR QUE EXISTE (08/09/2026). A busca nasceu para resolver o caso da Savassi:
/// funcionário novo no painel, caixa desatualizado, internet OK. Mas medindo o caminho
/// com a internet CAÍDA apareceu o custo:
///
///  · link caído com roteador vivo (o caso comum na loja): o POST de autenticação
///    demora 21 s até o Windows desistir do SYN. Com o teto de 8 s da tela, o operador
///    espera 8 s a CADA erro de digitação, e a busca nunca chega a terminar dentro da
///    janela: não vale nem para a tentativa seguinte;
///  · cada tentativa deixava uma busca correndo. Operador nervoso apertando Entrar
///    empilhava tarefas de 21 s, cada uma segurando uma conexão do banco, todas em fila
///    no mesmo semáforo da nuvem.
///
/// Ou seja: offline, a busca só somava espera. Aqui ela aprende com a queda.
///  · <b>Descanso:</b> falhou (ou não trouxe ninguém), não se pergunta de novo pelo
///    tempo do descanso. O primeiro erro de digitação custa o teto; os seguintes são
///    instantâneos, como eram antes de tudo isto.
///  · <b>Uma só:</b> quem chegar enquanto uma busca corre recebe a MESMA busca. Nunca
///    duas conexões, nunca fila no semáforo.
///
/// Sem WPF e sem relógio de parede: <see cref="Agora"/> é injetável para a suíte poder
/// andar no tempo em vez de dormir.
/// </summary>
public sealed class BuscaNoPainel
{
    private readonly Func<Task<int>> _baixar;
    private readonly TimeSpan _descanso;
    private readonly object _trava = new();
    private Task<int>? _emVoo;
    private DateTime _naoAntesDe = DateTime.MinValue;

    /// <param name="baixar">Quem vai à nuvem. Devolve quantos operadores desceram.</param>
    /// <param name="descanso">
    /// Quanto tempo sem insistir depois de uma busca que não trouxe ninguém. Um minuto
    /// é o bastante para o operador digitar de novo sem esperar, e curto o bastante
    /// para o funcionário recém-cadastrado entrar assim que a rede volta.
    /// </param>
    public BuscaNoPainel(Func<Task<int>> baixar, TimeSpan? descanso = null)
    {
        _baixar = baixar;
        _descanso = descanso ?? TimeSpan.FromMinutes(1);
    }

    /// <summary>O relógio. Só a suíte troca.</summary>
    public Func<DateTime> Agora { get; init; } = () => DateTime.Now;

    /// <summary>Vale a pena perguntar agora? False enquanto o descanso não passa.</summary>
    public bool Vale => Agora() >= _naoAntesDe;

    /// <summary>
    /// Pergunta ao painel. Devolve 0 na hora quando está em descanso (sem tocar na
    /// rede) e a MESMA busca para quem chegar durante uma que já corre.
    /// </summary>
    public Task<int> BuscarAsync()
    {
        lock (_trava)
        {
            if (_emVoo is { IsCompleted: false }) return _emVoo;
            if (!Vale) return Task.FromResult(0);
            _emVoo = CorrerAsync();
            return _emVoo;
        }
    }

    private async Task<int> CorrerAsync()
    {
        var quantos = 0;
        try { quantos = await _baixar().ConfigureAwait(false); }
        catch { quantos = 0; }
        finally
        {
            lock (_trava)
            {
                // Trouxe gente: a rede está de pé e o cadastro andou. Pode perguntar de
                // novo à vontade. Não trouxe: descansa, seja porque caiu, seja porque
                // não havia novidade — nos dois casos insistir agora não muda nada.
                _naoAntesDe = quantos > 0 ? DateTime.MinValue : Agora() + _descanso;
            }
        }
        return quantos;
    }
}
