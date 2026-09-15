using System.IO;
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
        if (VersaoDoRuntime(PastaDoNavegador) is null)
            throw new WebView2RuntimeNotFoundException("Couldn't find a compatible Webview2 Runtime installation to host WebViews.");

        var geracao = _geracao;
        if (_ambiente is null)
        {
            var ambiente = await CoreWebView2Environment.CreateAsync(PastaDoNavegador, _perfil, _opcoes?.Invoke());
            if (geracao != _geracao) throw new OperationCanceledException("o controle foi trocado enquanto o ambiente nascia");
            _ambiente = ambiente;
            AmbientesCriados++;
            _diag($"ambiente ok: runtime {ambiente.BrowserVersionString} thread={Environment.CurrentManagedThreadId}");
        }

        var web = _atual();
        _ensureChamado = true; _ensureTerminou = false;
        await web.EnsureCoreWebView2Async(_ambiente);
        if (geracao != _geracao) throw new OperationCanceledException("o controle foi trocado no meio da inicialização");
        _ensureTerminou = true;
        return web.CoreWebView2 ?? throw new InvalidOperationException("o WebView2 iniciou sem CoreWebView2");
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
            if (!_tela.CheckAccess()) return tipo;
            var (recriar, descartar) = FalhaWebView2.AposFalha(_ensureChamado, _ensureTerminou, tipo);
            if (descartar) _ambiente = null;
            if (recriar) Recriar(onde, descartar);
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
    /// </summary>
    public WebView2 Recriar(string motivo, bool descartarAmbiente)
    {
        _tela.VerifyAccess();
        var velho = _atual();
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
        if (descartarAmbiente) _ambiente = null;
        Recriacoes++;
        try { velho.Dispose(); } catch (Exception ex) { _diag($"{_nome} recriar: descartar o velho: {ex.GetType().Name}"); }
        _diag($"{_nome}: controle recriado ({motivo}){(descartarAmbiente ? ", ambiente novo" : "")} thread={Environment.CurrentManagedThreadId}");
        return novo;
    }

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
        if (_recriacaoAgendada) return;
        _recriacaoAgendada = true;
        var alvo = _atual();
        _tela.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _recriacaoAgendada = false;
            // o "Tentar de novo" (ou outra recuperação) já trocou este controle: não troca o novo
            if (!ReferenceEquals(_atual(), alvo)) return;
            try { Recriar(motivo, descartarAmbiente: true); }
            catch (Exception ex) { try { _diag($"{_nome} recriar depois: {ex.GetType().Name} {ex.Message}"); } catch { } }
        });
    }

    /// <summary>
    /// O controle atual está quebrado? Init em curso ou falho, ou o núcleo lança ao ser tocado
    /// (navegador morto). Controle nunca iniciado não está quebrado: não tem controller.
    /// </summary>
    public bool ControleQuebrado()
    {
        if (!_tela.CheckAccess()) return false;
        if (!_ensureChamado) return false;
        if (!_ensureTerminou) return true;
        try
        {
            var core = _atual().CoreWebView2;
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
