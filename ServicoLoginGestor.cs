using Pdv.Nucleo;

namespace Pdv;

/// <summary>
/// O LOGIN DO GESTOR SOZINHO, DO LADO DO CAIXA (1.0.44, 17/10/2026, pedido do dono; 1.0.50, 25/10: a tela real do
/// Gestor 9.350, o Vigia e um aparelho por vez com a TV).
///
/// A tela do chat (<see cref="Telas.ChatIfood"/>) liga este servico quando o WebView2 nasce, com a nuvem
/// (edge gestor-login, pela sessao do terminal), a pagina (ExecuteScriptAsync) e o diag. A cada leitura da
/// pagina (5 s) ela diz se o Gestor pede login; com o Gestor pedindo e nada em andamento, o roteiro
/// (<see cref="FluxoLoginGestor"/>, Nucleo) roda em segundo plano. Os tetos moram no Nucleo
/// (<see cref="LoginDoGestor"/>): 1 tentativa a cada 10 min; 3 falhas seguidas = 2 h de pausa e aviso ao dono;
/// captcha ou cadastro da verificacao em 2 etapas = pausa na hora e aviso.
///
/// O estado sobrevive a recriacao do controle do chat (e estatico); a tentativa em andamento e cancelada
/// quando o controle e trocado (a pagina velha morreu), sem contar falha.
///
/// O "Entrar agora" (Configuracao) e o comando <c>relogar_gestor</c> do Vigia entram por
/// <see cref="LoginDoGestor.EntrarAgora"/>, que este servico pendura ao ligar.
///
/// Nada de e-mail, senha ou codigo no diag: o roteiro ja mascara, e aqui mascara de novo (dupla garantia).
/// </summary>
public static class ServicoLoginGestor
{
    private static readonly object _trava = new();
    private static readonly LoginDoGestor _estado = new();
    private static FluxoLoginGestor? _fluxo;
    private static CancellationTokenSource? _cts;
    private static Action<string>? _diag;
    private static string? _ultimaLinhaRepetida;

    /// <summary>A linha da Configuracao: "Login do Gestor: automatico (ERP) | ultimo: entrou 10:12".</summary>
    public static string Linha => _estado.LinhaDaConfiguracao();
    public static bool Rodando => _fluxo?.Rodando == true;
    /// <summary>Para o sinal do Vigia: o Gestor pede login desde quando (nulo = nao pede).</summary>
    public static DateTime? PedeLoginDesde { get; private set; }

    /// <summary>A tela do chat liga (ou religa, com o controle novo) a nuvem, a pagina e o diag.</summary>
    public static void Ligar(string terminal,
        Func<string, Task<(int Status, string Corpo)>> nuvem,
        Func<string, Task<string>> pagina,
        Action<string> diag)
    {
        lock (_trava)
        {
            try { _cts?.Cancel(); } catch { }
            _cts = new CancellationTokenSource();
            _diag = diag;
            // a hora e a LOCAL do PC corrigida pelo servidor (RelogioCorrigido): e a que o caixa mostra e a que o ERP
            // entende no pausado_ate do usado (hora de Brasilia, sem fuso)
            _fluxo = new FluxoLoginGestor(_estado, terminal, nuvem, pagina, Anotar, () => RelogioCorrigido.AgoraCorrigido);
            _ultimaLinhaRepetida = null;
            // o ponto de entrada de fora (Vigia: relogar_gestor; Configuracao: Entrar agora)
            LoginDoGestor.ComandoEntrarAgora = EntrarAgoraAsync;
        }
    }

    /// <summary>O controle do chat morreu: a tentativa em andamento (se houver) e cancelada, sem contar falha.</summary>
    public static void Desligar()
    {
        lock (_trava)
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
            _fluxo = null;
            PedeLoginDesde = null;
        }
    }

    /// <summary>
    /// A leitura da pagina, a cada 5 s: o Gestor pede login? Com o Gestor logado nada acontece. Com ele pedindo
    /// login e nada em andamento, o roteiro comeca (os tetos decidem se e hora).
    /// </summary>
    public static void Observar(bool pedeLogin)
    {
        if (!pedeLogin) { _ultimaLinhaRepetida = null; PedeLoginDesde = null; return; }
        PedeLoginDesde ??= RelogioCorrigido.AgoraCorrigido;
        FluxoLoginGestor? f; CancellationToken ct;
        lock (_trava) { f = _fluxo; ct = _cts?.Token ?? CancellationToken.None; }
        if (f is null || f.Rodando) return;
        _ = Task.Run(() => f.TentarAsync(false, ct));
    }

    /// <summary>"Entrar agora" na Configuracao (e o relogar_gestor do Vigia): passa pelo teto e pela pausa. Devolve a linha nova.</summary>
    public static async Task<string> EntrarAgoraAsync()
    {
        FluxoLoginGestor? f; CancellationToken ct;
        lock (_trava) { f = _fluxo; ct = _cts?.Token ?? CancellationToken.None; }
        if (f is null) return "o chat do iFood ainda nao abriu";
        if (f.Rodando) return "ja esta entrando";
        _ultimaLinhaRepetida = null;
        await Task.Run(() => f.TentarAsync(true, ct)).ConfigureAwait(false);
        return Linha;
    }

    /// <summary>
    /// Anota no diag do chat, mascarado. As linhas "nao agora" que se repetem a cada 5 s (teto, pausa, pagina
    /// carregando) saem uma vez so, ate mudar.
    /// </summary>
    private static void Anotar(string texto)
    {
        var linha = LoginDoGestor.Mascarar(texto);
        var repetivel = linha.Contains("nao agora", StringComparison.Ordinal) || linha.Contains("ja esta logado", StringComparison.Ordinal);
        lock (_trava)
        {
            if (repetivel && linha == _ultimaLinhaRepetida) return;
            _ultimaLinhaRepetida = repetivel ? linha : null;
        }
        try { _diag?.Invoke(linha); } catch { }
    }
}
