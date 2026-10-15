using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// O DONO DO CONTROLE WEBVIEW2 DE UMA CAMADA (chat do iFood, WhatsApp). 15/09/2026, Castelo.
///
/// As duas telas tinham cada uma a sua inicialização, e as duas com os mesmos três furos
/// (ver <see cref="FalhaWebView2"/>): ambiente novo a cada tentativa, controle com init falho
/// reaproveitado, e "instale o WebView2 Runtime" para qualquer erro. Aqui mora uma cópia só:
///  · o AMBIENTE é criado uma vez e reaproveitado enquanto se provar bom;
///  · o CONTROLE que recebeu EnsureCoreWebView2Async e falhou sai da grade e dá lugar a um novo,
///    recolhido, no mesmo lugar (o SDK não desfaz o controller gravado);
///  · tudo roda na thread da tela: quem chama de fora passa por <see cref="NaTela"/>, e o que
///    chega aqui de outra thread é recusado na porta;
///  · cada falha vira uma linha de diagnóstico com a thread e a versão do runtime;
///  · falha de WebView2 que escapa para o Dispatcher (layout, foco) chega pelo App em
///    <see cref="AvisarFalhaForaDaCamada"/>, e só a camada cujo controle está quebrado recebe
///    <see cref="Quebrou"/>.
///
/// Testado DE VERDADE (runtime desta máquina, janela fora da tela) em TestesCamadaWebView2 (RW-*).
///
/// 1.0.35 (Savassi, 14/10/2026): o PERFIL EM USO. Ver <see cref="RecuperarPerfilEmUsoAsync"/> e
/// TestesPerfilEmUso (RP-*, com um segundo processo segurando o perfil de verdade).
/// </summary>
public sealed class HospedeWebView2
{
    private static readonly List<WeakReference<HospedeWebView2>> _vivos = new();

    private readonly string _nome;
    private readonly Dispatcher _tela;
    private readonly Func<WebView2> _atual;
    private readonly Action<WebView2> _trocar;
    private readonly string _perfil;
    private readonly Action<string> _diag;
    private readonly Func<CoreWebView2EnvironmentOptions?>? _opcoes;
    private CoreWebView2Environment? _ambiente;
    private bool _ensureChamado, _ensureTerminou;
    private int _geracao;
    private bool _recriacaoAgendada;
    /// <summary>O PDV está fechando (ver <see cref="Encerrar"/>): nada mais inicia, recria nem é dado como quebrado.</summary>
    private bool _encerrado;
    /// <summary>
    /// Recriar SOZINHO (falha na inicialização, falha que chegou ao Dispatcher) tem teto por hora
    /// (revisão 15/09). Sem teto, um PC em que o controle falha sempre recriava a cada abertura da
    /// aba. O "Tentar de novo" é gesto de gente e passa do teto.
    /// </summary>
    private readonly TetoPorHora _tetoAutomatico = new(6);
    /// <summary>O teto negou a troca: o controle usado fica parado até o "Tentar de novo".</summary>
    private bool _parado;
    /// <summary>
    /// 1.0.35: com que opções o ambiente nasce. 0 = as desta versão (<c>opcoes</c> do construtor);
    /// i > 0 = <see cref="ArgumentosAnteriores"/>[i - 1]. Muda só quando o perfil recusou as de agora
    /// e as anteriores entraram, e fica assim até o caixa fechar: no mesmo processo, um ambiente novo
    /// com outras opções seria recusado pelo navegador que este mesmo caixa já tem no perfil. A
    /// próxima abertura do caixa começa de novo pelas desta versão.
    /// </summary>
    private int _opcoesDaVez;
    /// <summary>1.0.35: a geração do controle que a recuperação do perfil espera encontrar (ela mesma troca o controle).</summary>
    private int _geracaoDaRecuperacao;

    /// <param name="nome">Só para o diagnóstico ("chat", "whatsapp").</param>
    /// <param name="atual">O controle que está na grade agora (o campo x:Name da tela).</param>
    /// <param name="trocar">Troca o campo da tela pelo controle novo.</param>
    /// <param name="pastaPerfil">A pasta do perfil (login do Gestor, sessão do WhatsApp) em ProgramData.</param>
    public HospedeWebView2(string nome, Dispatcher tela, Func<WebView2> atual, Action<WebView2> trocar,
        string pastaPerfil, Action<string> diag, Func<CoreWebView2EnvironmentOptions?>? opcoes = null)
    {
        _nome = nome; _tela = tela; _atual = atual; _trocar = trocar; _perfil = pastaPerfil; _diag = diag; _opcoes = opcoes;
        lock (_vivos) { _vivos.RemoveAll(w => !w.TryGetTarget(out _)); _vivos.Add(new WeakReference<HospedeWebView2>(this)); }
    }

    /// <summary>Pasta de um runtime fixo. null (o caixa) = o runtime instalado. A suíte aponta uma pasta vazia para simular "ausente".</summary>
    public string? PastaDoNavegador { get; set; }

    public int Recriacoes { get; private set; }
    public int AmbientesCriados { get; private set; }

    /// <summary>
    /// 1.0.35: as opções (AdditionalBrowserArguments) que ESTE perfil teve em versões anteriores do
    /// caixa; null = sem opção nenhuma. É com elas que o caixa entra no navegador do caixa anterior
    /// quando ele ainda está vivo no perfil.
    /// </summary>
    public IReadOnlyList<string?> ArgumentosAnteriores { get; set; } = Array.Empty<string?>();

    /// <summary>1.0.35: o ambiente atual nasceu com as opções de uma versão anterior (o perfil recusou as de agora).</summary>
    public bool ComOpcoesAnteriores => _opcoesDaVez > 0;

    /// <summary>1.0.35: desde quando o perfil está em uso sem a recuperação entrar (null = não está).</summary>
    public DateTime? PerfilEmUsoDesde { get; private set; }

    /// <summary>1.0.35: quantas inicializações seguidas o perfil em uso derrubou (zera quando abre).</summary>
    public int VezesPerfilEmUso { get; private set; }

    /// <summary>Há quanto tempo o perfil está em uso (zero quando não está). Para o painel.</summary>
    public TimeSpan EsperandoOPerfil(DateTime agora) => PerfilEmUsoDesde is { } d && agora > d ? agora - d : TimeSpan.Zero;

    /// <summary>Quem está no perfil (a suíte troca para simular um navegador órfão).</summary>
    public Func<string, IReadOnlyList<NavegadorNoPerfil>> ListarDonos { get; set; } = NavegadorDoPerfil.Listar;

    /// <summary>Encerra um navegador órfão do perfil (a suíte troca para contar).</summary>
    public Func<NavegadorNoPerfil, TimeSpan, bool> EncerrarDono { get; set; } = NavegadorDoPerfil.Encerrar;

    /// <summary>
    /// Entre a recusa e a próxima tentativa. Medido em 14/10/2026 (runtime 155): com 0 ms a troca de
    /// opções ainda é recusada, com 200 ms entra. Um segundo, com folga.
    /// </summary>
    public TimeSpan EsperaEntreTentativas { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Quanto esperar o navegador do caixa anterior sair sozinho (no fechamento normal ele some em menos de 1 s).</summary>
    public TimeSpan EsperaPeloDono { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>O controle atual já recebeu EnsureCoreWebView2Async (não serve para outra tentativa se falhar).</summary>
    public bool ControleUsado => _ensureChamado;

    /// <summary>Falha de WebView2 que chegou ao Dispatcher e é DESTE controle. Disparado na thread da tela.</summary>
    public event Action<Exception>? Quebrou;

    /// <summary>A versão do runtime disponível, ou null quando não há.</summary>
    public static string? VersaoDoRuntime(string? pastaDoNavegador = null)
    {
        try
        {
            var v = CoreWebView2Environment.GetAvailableBrowserVersionString(pastaDoNavegador);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch { return null; }
    }

    /// <summary>"origem thread=N runtime=X", para o erros.log e os diagnósticos.</summary>
    public static string Contexto(string origem)
        => $"{origem} thread={Environment.CurrentManagedThreadId} runtime={VersaoDoRuntime() ?? "ausente"}";

    /// <summary>Roda <paramref name="corpo"/> na thread da tela: direto se já está nela, senão por InvokeAsync.</summary>
    public static Task NaTela(Dispatcher tela, Func<Task> corpo)
        => tela.CheckAccess() ? corpo() : tela.InvokeAsync(corpo).Task.Unwrap();

    /// <inheritdoc cref="NaTela(Dispatcher, Func{Task})"/>
    public static Task<T> NaTela<T>(Dispatcher tela, Func<Task<T>> corpo)
        => tela.CheckAccess() ? corpo() : tela.InvokeAsync(corpo).Task.Unwrap();

    /// <summary>
    /// Inicializa o controle atual (na thread da tela). Sem runtime, lança ANTES de criar
    /// ambiente e de tocar no controle. Lança o que o SDK lançar: quem chama passa a exceção
    /// para <see cref="Falhou"/>.
    /// </summary>
    public async Task<CoreWebView2> IniciarControleAsync()
    {
        _tela.VerifyAccess();
        if (_encerrado) throw new OperationCanceledException("o PDV está fechando");
        // controle usado e parado pelo teto: nem encosta nele (outro Ensure num controle meio vivo
        // é justamente o que lança no layout depois)
        if (_parado)
            throw new InvalidOperationException("o controle ficou parado pelo teto de recriações da hora; falta o Tentar de novo");
        if (VersaoDoRuntime(PastaDoNavegador) is null)
            throw new WebView2RuntimeNotFoundException("Couldn't find a compatible Webview2 Runtime installation to host WebViews.");

        var ambienteNovo = _ambiente is null;
        CoreWebView2 core;
        try
        {
            core = await UmaTentativaAsync(_opcoesDaVez);
        }
        catch (Exception ex) when (ambienteNovo && !_encerrado && FalhaWebView2.EhRecusaDoPerfil(ex))
        {
            // 1.0.35: o 0x8007139F num ambiente que acabou de nascer é o perfil preso por outro
            // navegador com outras opções (o do caixa anterior). Antes virava "navegador caiu" e o
            // painel "não abriu agora" até o PC reiniciar (Savassi, 14/10/2026 21:21).
            core = await RecuperarPerfilEmUsoAsync(ex);
        }
        if (PerfilEmUsoDesde is { } desde)
            _diag($"{_nome}: abriu depois de {(DateTime.Now - desde).TotalSeconds:0} s com o perfil em uso");
        PerfilEmUsoDesde = null;
        VezesPerfilEmUso = 0;
        return core;
    }

    /// <summary>Uma tentativa: o ambiente (se ainda não há) com as opções de índice <paramref name="opcoes"/>, e o Ensure no controle atual.</summary>
    private async Task<CoreWebView2> UmaTentativaAsync(int opcoes)
    {
        var geracao = _geracao;
        if (_ambiente is null)
        {
            var ambiente = await CoreWebView2Environment.CreateAsync(PastaDoNavegador, _perfil, Opcoes(opcoes));
            if (geracao != _geracao) throw new OperationCanceledException("o controle foi trocado enquanto o ambiente nascia");
            _ambiente = ambiente;
            AmbientesCriados++;
            _diag($"ambiente ok: runtime {ambiente.BrowserVersionString} thread={Environment.CurrentManagedThreadId}"
                  + (opcoes == 0 ? "" : $" (opções anteriores: {Rotulo(opcoes)})"));
        }

        var web = _atual();
        _ensureChamado = true; _ensureTerminou = false;
        await web.EnsureCoreWebView2Async(_ambiente);
        if (geracao != _geracao) throw new OperationCanceledException("o controle foi trocado no meio da inicialização");
        _ensureTerminou = true;
        return web.CoreWebView2 ?? throw new InvalidOperationException("o WebView2 iniciou sem CoreWebView2");
    }

    /// <summary>Todas as opções que este perfil conhece: [0] as desta versão, depois as anteriores.</summary>
    private IReadOnlyList<string?> TodasAsOpcoes()
        => new[] { _opcoes?.Invoke()?.AdditionalBrowserArguments }.Concat(ArgumentosAnteriores).ToList();

    private CoreWebView2EnvironmentOptions? Opcoes(int indice)
    {
        if (indice == 0) return _opcoes?.Invoke();
        var argumentos = ArgumentosAnteriores[indice - 1];
        return argumentos is null ? null : new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = argumentos };
    }

    private string Rotulo(int indice)
    {
        var a = TodasAsOpcoes()[indice];
        return string.IsNullOrWhiteSpace(a) ? "nenhuma" : a;
    }

    /// <summary>
    /// O PERFIL EM USO (1.0.35, Savassi, 14/10/2026 21:21). O caixa 1.0.34 abriu com o navegador da
    /// 1.0.32 ainda vivo no perfil, e a 1.0.33 tinha mudado as opções dos dois perfis: o WebView2
    /// recusa o ambiente com opções diferentes enquanto aquele navegador vive (0x8007139F). Os dois
    /// perfis falharam juntos e só o reinício do PC resolveu. Aqui, na ordem:
    ///  1. o dono pode estar só saindo (caixa anterior fechando agora): espera <see cref="EsperaPeloDono"/>;
    ///  2. ÓRFÃO (quem abriu já morreu): encerra só esse navegador e tenta de novo com as opções de agora;
    ///  3. sem dono à vista: tenta de novo com as opções de agora;
    ///  4. DONO VIVO: entra no navegador dele com as opções que ele tem (as de uma versão anterior,
    ///     <see cref="ArgumentosAnteriores"/>). Medido: o controle segue vivo depois que o dono sai. O
    ///     diagnóstico anota, e a próxima abertura do caixa volta às opções desta versão;
    ///  5. nada entrou: <see cref="PerfilEmUsoException"/>. A tela mostra "esperando" e tenta de novo
    ///     sozinha (<see cref="FalhaWebView2.EsperaDoPerfil"/>), sem gastar o teto da hora.
    /// Cada tentativa troca o controle usado por um novo (controle com Ensure falho não se reaproveita)
    /// e espera <see cref="EsperaEntreTentativas"/>.
    /// </summary>
    private async Task<CoreWebView2> RecuperarPerfilEmUsoAsync(Exception recusa)
    {
        VezesPerfilEmUso++;
        PerfilEmUsoDesde ??= DateTime.Now;
        _geracaoDaRecuperacao = _geracao;
        var donos = await DonosAsync();
        _diag($"{_nome}: o perfil recusou as opções ({Rotulo(_opcoesDaVez)}) com 0x{recusa.HResult:X8}: {NavegadorDoPerfil.Descrever(donos)}");

        // 1. o caixa anterior pode estar só terminando de fechar
        if (donos.Count > 0 && EsperaPeloDono > TimeSpan.Zero)
        {
            var relogio = Stopwatch.StartNew();
            while (donos.Count > 0 && relogio.Elapsed < EsperaPeloDono)
            {
                await Task.Delay(250);
                ConferirRecuperacao();
                donos = await DonosAsync();
            }
            if (donos.Count == 0) _diag($"{_nome}: o navegador que segurava o perfil saiu em {relogio.Elapsed.TotalSeconds:0.0} s");
        }

        // 2. órfão: quem o abriu morreu, e ele ficou segurando o perfil sozinho
        if (NavegadorDoPerfil.Classificar(donos) == NavegadorDoPerfil.Dono.Orfao)
        {
            foreach (var d in donos)
            {
                var saiu = await Task.Run(() => EncerrarDono(d, TimeSpan.FromSeconds(5)));
                _diag($"{_nome}: navegador órfão no perfil {(saiu ? "encerrado" : "NÃO saiu")}: {NavegadorDoPerfil.Descrever(d)}");
            }
            ConferirRecuperacao();
            donos = await DonosAsync();
        }

        // 3. ninguém no perfil (ou o órfão saiu): de novo, com as opções de agora
        if (donos.Count == 0 && await TentarDeNovoAsync(_opcoesDaVez) is { } core) return core;

        // 4. dono vivo: entra no navegador dele, com as opções que ele tem
        if (donos.Count == 0) donos = await DonosAsync();
        foreach (var i in NavegadorDoPerfil.OrdemDasOpcoes(TodasAsOpcoes(), _opcoesDaVez, donos.Select(d => d.Linha).ToList()))
        {
            if (await TentarDeNovoAsync(i) is not { } entrou) continue;
            _diag($"{_nome}: abriu com as opções {(i == 0 ? "desta versão" : "anteriores")} ({Rotulo(i)}) no navegador que já estava no perfil; "
                  + "a próxima abertura do caixa tenta as desta versão de novo");
            _opcoesDaVez = i;
            return entrou;
        }

        // 5. nada entrou: a tela espera e tenta de novo sozinha
        donos = await DonosAsync();
        throw new PerfilEmUsoException(NavegadorDoPerfil.Descrever(donos), recusa);
    }

    /// <summary>Uma tentativa da recuperação: controle novo (o usado não serve), a espera, e o ambiente com as opções <paramref name="opcoes"/>. Null = o perfil recusou de novo.</summary>
    private async Task<CoreWebView2?> TentarDeNovoAsync(int opcoes)
    {
        ConferirRecuperacao();
        if (_ensureChamado) Recriar("perfil em uso", descartarAmbiente: true);
        else _ambiente = null;
        _geracaoDaRecuperacao = _geracao;
        await Task.Delay(EsperaEntreTentativas);
        ConferirRecuperacao();
        try
        {
            return await UmaTentativaAsync(opcoes);
        }
        catch (Exception ex) when (!_encerrado && FalhaWebView2.EhRecusaDoPerfil(ex))
        {
            _diag($"{_nome}: o perfil recusou de novo as opções ({Rotulo(opcoes)})");
            return null;
        }
    }

    /// <summary>
    /// A recuperação para se o PDV está fechando, ou se OUTRO caminho trocou o controle no meio (o
    /// Reabrir do aviso da venda recria e inicia por conta própria): a tentativa nova é a dona dele.
    /// </summary>
    private void ConferirRecuperacao()
    {
        if (_encerrado) throw new OperationCanceledException("o PDV está fechando");
        if (_geracao != _geracaoDaRecuperacao) throw new OperationCanceledException("o controle foi trocado enquanto o perfil esperava");
    }

    private async Task<IReadOnlyList<NavegadorNoPerfil>> DonosAsync()
    {
        try
        {
            var meu = Environment.ProcessId;
            var lista = await Task.Run(() => ListarDonos(_perfil));
            // o navegador deste mesmo caixa nunca é "o outro" (nem é encerrado como órfão)
            return lista.Where(d => d.PaiPid != meu).ToList();
        }
        catch (Exception ex)
        {
            _diag($"{_nome}: não consegui ver quem está no perfil: {ex.GetType().Name}");
            return Array.Empty<NavegadorNoPerfil>();
        }
    }

    /// <summary>
    /// A inicialização falhou: anota (thread, runtime), descarta o ambiente e troca o controle
    /// conforme <see cref="FalhaWebView2.AposFalha"/>. Nunca lança. Devolve o tipo, para o painel.
    /// </summary>
    public TipoFalhaWeb Falhou(string onde, Exception ex)
    {
        var tipo = FalhaWebView2.Classificar(ex);
        try
        {
            _diag(FalhaWebView2.Linha($"{_nome} {onde}", ex, Environment.CurrentManagedThreadId, _tela.Thread.ManagedThreadId,
                VersaoDoRuntime(PastaDoNavegador)));
            if (!_tela.CheckAccess() || _encerrado) return tipo;
            var (recriar, descartar) = FalhaWebView2.AposFalha(_ensureChamado, _ensureTerminou, tipo);
            if (descartar) _ambiente = null;
            if (recriar)
            {
                // 1.0.35: esperar o perfil não gasta o teto da hora (a tela tenta de novo em 10 s a 1 min)
                if (tipo == TipoFalhaWeb.PerfilEmUso || _tetoAutomatico.Permitir(DateTime.UtcNow)) Recriar(onde, descartar);
                else { _parado = true; _diag($"{_nome} {onde}: teto de recriações da hora; o controle fica parado até o Tentar de novo"); }
            }
        }
        catch (Exception e2)
        {
            try { _diag($"{_nome} {onde}: a recuperação falhou: {e2.GetType().Name} {e2.Message}"); } catch { }
        }
        return tipo;
    }

    /// <summary>
    /// Troca o controle por um novo, recolhido, no MESMO lugar da grade, e descarta o velho. O
    /// velho sai recolhido primeiro (cada passo com o seu try: um controller meio vivo lança
    /// justamente ao mudar visível) e o Dispose vem por último.
    ///
    /// O DISPOSE DO SDK PARA NO MEIO num controller meio vivo (revisão 15/09, IL do SDK 1.0.4129.50):
    /// WebView2.Dispose chama WebView2Base.Dispose e só DEPOIS HwndHost.Dispose, sem finally; o
    /// Uninitialize zera o controller e lança ao tirar os eventos do núcleo. A janela do controle
    /// ficava estacionada pelo WPF para sempre, uma a cada troca. A segunda passada do Dispose já
    /// não tem controller (sai limpa e destrói a janela); se nem assim, DestroyWindow direto.
    /// </summary>
    public WebView2 Recriar(string motivo, bool descartarAmbiente)
    {
        _tela.VerifyAccess();
        if (_encerrado) return _atual();
        var velho = _atual();
        var janelaVelha = IntPtr.Zero;
        try { janelaVelha = velho.Handle; } catch { }
        var novo = new WebView2 { Visibility = Visibility.Collapsed };
        Grid.SetRow(novo, Grid.GetRow(velho));
        Grid.SetColumn(novo, Grid.GetColumn(velho));
        var grade = velho.Parent as Panel;

        try { velho.Visibility = Visibility.Collapsed; } catch (Exception ex) { _diag($"{_nome} recriar: recolher o velho: {ex.GetType().Name}"); }
        var indice = grade?.Children.IndexOf(velho) ?? -1;
        if (grade is not null)
        {
            try { grade.Children.Remove(velho); } catch (Exception ex) { _diag($"{_nome} recriar: tirar o velho: {ex.GetType().Name}"); }
            if (grade.Children.Contains(velho)) try { grade.Children.Remove(velho); } catch { }
            grade.Children.Insert(Math.Clamp(indice, 0, grade.Children.Count), novo);
        }
        _trocar(novo);
        _geracao++;
        _ensureChamado = false; _ensureTerminou = false;
        _parado = false;
        if (descartarAmbiente) _ambiente = null;
        Recriacoes++;
        DescartarControle(velho, janelaVelha);
        _diag($"{_nome}: controle recriado ({motivo}){(descartarAmbiente ? ", ambiente novo" : "")} thread={Environment.CurrentManagedThreadId}");
        return novo;
    }

    private void DescartarControle(WebView2 velho, IntPtr janelaVelha)
    {
        try { velho.Dispose(); return; }
        catch (Exception ex) { _diag($"{_nome} recriar: descartar o velho: {ex.GetType().Name} (o Dispose do SDK parou no meio)"); }
        // segunda passada: o Uninitialize já zerou o controller, então esta chega ao HwndHost.Dispose
        try { velho.Dispose(); }
        catch (Exception ex) { _diag($"{_nome} recriar: segunda passada do Dispose: {ex.GetType().Name}"); }
        if (janelaVelha == IntPtr.Zero || !IsWindow(janelaVelha)) return;
        var destruiu = DestroyWindow(janelaVelha);
        _diag($"{_nome} recriar: a janela do controle velho ficou de pé; DestroyWindow={destruiu}");
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    /// <summary>Recria só se o controle atual já foi usado (o "Tentar de novo" do painel). Devolve se recriou.</summary>
    public bool RecriarSeUsado(string motivo)
    {
        if (!_ensureChamado) return false;
        Recriar(motivo, descartarAmbiente: !_ensureTerminou);
        return true;
    }

    /// <summary>Recria depois, com o Dispatcher livre (quem chama está dentro do tratamento de uma exceção).</summary>
    public void RecriarDepois(string motivo)
    {
        if (_recriacaoAgendada || _encerrado) return;
        _recriacaoAgendada = true;
        var alvo = _atual();
        _tela.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _recriacaoAgendada = false;
            if (_encerrado) return;
            // o "Tentar de novo" (ou outra recuperação) já trocou este controle: não troca o novo
            if (!ReferenceEquals(_atual(), alvo)) return;
            if (!_tetoAutomatico.Permitir(DateTime.UtcNow))
            {
                _parado = true;
                _diag($"{_nome} recriar depois ({motivo}): teto de recriações da hora; o controle fica parado até o Tentar de novo");
                return;
            }
            try { Recriar(motivo, descartarAmbiente: true); }
            catch (Exception ex) { try { _diag($"{_nome} recriar depois: {ex.GetType().Name} {ex.Message}"); } catch { } }
        });
    }

    /// <summary>
    /// O controle atual está quebrado? Controle nunca iniciado não está: não tem controller.
    ///
    /// A leitura de WebView2.CoreWebView2 PASSA PELO CONTROLLER (IL do SDK 1.0.4129.50:
    /// WebView2Base.get_CoreWebView2 confere thread, descarte e navegador morto e devolve
    /// CoreWebView2Controller?.CoreWebView2, que chama a interface nativa do controller sem catch).
    /// Então um controller meio vivo (a frase do Castelo) lança aqui, com a inicialização terminada
    /// ou não, e o navegador morto também.
    ///
    /// Init em curso com o controller ainda sem lançar NÃO é quebrado (revisão 15/09): a exceção
    /// que chegou é de outra camada, e se a inicialização desta falhar o catch dela cuida. Antes,
    /// qualquer falha de WebView2 no Dispatcher durante o pré-aquecimento derrubava a camada que
    /// ainda estava subindo.
    /// </summary>
    public bool ControleQuebrado()
    {
        if (!_tela.CheckAccess() || _encerrado) return false;
        if (!_ensureChamado) return false;
        try
        {
            var core = _atual().CoreWebView2;
            if (!_ensureTerminou) return false;
            if (core is null) return true;
            _ = core.BrowserProcessId;
            return false;
        }
        catch { return true; }
    }

    /// <summary>
    /// O App recebeu no Dispatcher uma exceção de WebView2 (ver App.xaml.cs). Cada camada viva
    /// confere se o controle DELA está quebrado; só essa recebe <see cref="Quebrou"/>.
    /// Chamado na thread da tela; nunca lança.
    /// </summary>
    public static void AvisarFalhaForaDaCamada(Exception ex)
    {
        List<HospedeWebView2> vivos;
        lock (_vivos) vivos = _vivos.Select(w => w.TryGetTarget(out var h) ? h : null).OfType<HospedeWebView2>().ToList();
        foreach (var h in vivos)
        {
            try
            {
                if (!h.ControleQuebrado()) continue;
                h._diag(FalhaWebView2.Linha($"{h._nome} fora da inicialização", ex, Environment.CurrentManagedThreadId,
                    h._tela.Thread.ManagedThreadId, VersaoDoRuntime(h.PastaDoNavegador)));
                h.Quebrou?.Invoke(ex);
            }
            catch { /* uma camada que tropeça aqui não pode levar a outra junto */ }
        }
    }

    /// <summary>Sai da lista de camadas avisadas (a suíte, ao terminar).</summary>
    public void Desligar()
    {
        lock (_vivos) _vivos.RemoveAll(w => !w.TryGetTarget(out var h) || ReferenceEquals(h, this));
    }

    /// <summary>A camada já foi encerrada (o PDV está fechando).</summary>
    public bool Encerrado => _encerrado;

    /// <summary>
    /// O PDV ESTÁ FECHANDO (erros.log do Castelo, 15/09/2026 13:57:32). Ao fechar a janela, o WPF tira a
    /// árvore visual e avisa cada elemento que ele deixou de ser visível; o controle WebView2 repassa isso
    /// ao controller (WebView2Base.SafeAccessController, CoreWebView2Controller.IsVisible). Com o controller
    /// meio vivo, esse aviso lançava a frase "members can only be accessed from the UI thread" no meio do
    /// fechamento, e a recuperação ainda tentava recriar o controle numa janela que estava sumindo.
    /// Encerrar descarta o controle ANTES e o tira da árvore (sem controller, o aviso não chega a lugar
    /// nenhum) e desliga tudo que recria. Na thread da tela; nunca lança; a segunda chamada não faz nada.
    /// </summary>
    public void Encerrar()
    {
        if (_encerrado || !_tela.CheckAccess()) return;
        _encerrado = true;
        try
        {
            var velho = _atual();
            var janela = IntPtr.Zero;
            try { janela = velho.Handle; } catch { }
            DescartarControle(velho, janela);
            // só DEPOIS do descarte: tirar da árvore um controle com controller meio vivo é, de novo, avisar
            // que ele deixou de ser visível
            if (velho.Parent is Panel pai) try { pai.Children.Remove(velho); } catch { }
            _diag($"{_nome}: encerrado (o PDV está fechando) thread={Environment.CurrentManagedThreadId}");
        }
        catch (Exception ex) { try { _diag($"{_nome} encerrar: {ex.GetType().Name}"); } catch { } }
        Desligar();
    }

    /// <summary>Encerra todas as camadas WebView2 vivas (MainWindow.OnClosing). Nunca lança.</summary>
    public static void EncerrarTodos()
    {
        List<HospedeWebView2> vivos;
        lock (_vivos) vivos = _vivos.Select(w => w.TryGetTarget(out var h) ? h : null).OfType<HospedeWebView2>().ToList();
        foreach (var h in vivos) { try { h.Encerrar(); } catch { } }
    }

    /// <summary>
    /// Uma linha datada num arquivo de diagnóstico em ProgramData\PdvNativo (recomeça em 1 MB,
    /// guardando o anterior como .1). Nunca atrapalha o caixa.
    /// </summary>
    public static void Anotar(string arquivo, string texto, TimeSpan desdeQueAbriu)
    {
        try
        {
            var caminho = Path.Combine(Banco.Pasta, arquivo);
            if (File.Exists(caminho) && new FileInfo(caminho).Length > 1_000_000)
                File.Move(caminho, Path.Combine(Banco.Pasta, Path.GetFileNameWithoutExtension(arquivo) + ".1" + Path.GetExtension(arquivo)), true);
            File.AppendAllText(caminho, $"{DateTime.Now:dd/MM HH:mm:ss}  +{desdeQueAbriu.TotalSeconds,7:0.0}s  {texto}{Environment.NewLine}");
        }
        catch { /* diagnóstico nunca atrapalha o caixa */ }
    }
}
