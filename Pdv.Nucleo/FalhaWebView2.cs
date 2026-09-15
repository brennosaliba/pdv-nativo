using System.Runtime.InteropServices;

namespace Pdv.Nucleo;

/// <summary>Que tipo de falha uma camada WebView2 (chat do iFood, WhatsApp) levou.</summary>
public enum TipoFalhaWeb
{
    /// <summary>O runtime da Microsoft não está nesta máquina (WebView2RuntimeNotFoundException).</summary>
    RuntimeAusente,
    /// <summary>O processo do navegador morreu (ou o controller já está fechado): o controle é inútil.</summary>
    NavegadorCaiu,
    /// <summary>O controle já passou do Ensure com OUTRO ambiente (a nova tentativa antiga fazia isso).</summary>
    JaIniciadoComOutroAmbiente,
    /// <summary>Qualquer outra coisa (inclusive a E_NOINTERFACE do controller, a frase do Castelo).</summary>
    Outra,
}

// ════════════════════════════════════════════════════════════════════════════
//  A REGRA DAS CAMADAS WEBVIEW2 (15/09/2026, PC novo do Castelo, PDV 1.0.10)
//
//  O dono viu o painel "instale o WebView2 Runtime" e depois a caixa "Alguma coisa
//  falhou nesta tela e eu segurei o caixa de pé" com a frase "CoreWebView2Controller
//  members can only be accessed from the UI thread.". A varredura (só leitura, IL do SDK
//  1.0.4129.50 + sonda WPF) provou que o PDV não toca o WebView2 fora da thread da tela:
//  o SDK escreve essa frase para QUALQUER E_NOINTERFACE do controller, em qualquer thread.
//  A causa provável é a inicialização que falha no meio e deixa o controller gravado
//  ("meio vivo"); qualquer mexida de visível, posição ou foco lança dali no Dispatcher.
//
//  O que mora aqui é o que dá para decidir sem WebView2 nenhum (o Núcleo não referencia
//  o SDK): o que é falha de WebView2, que tipo, a frase do painel, quando recriar o
//  controle e descartar o ambiente. Quem executa é Telas/HospedeWebView2.cs.
//  Testes: Pdv.Testes/TestesCamadaWebView2.cs (FW-*).
// ════════════════════════════════════════════════════════════════════════════
public static class FalhaWebView2
{
    /// <summary>A frase do SDK para E_NOINTERFACE no controller (CoreWebView2Controller.get_IsVisible e irmãos).</summary>
    public const string MensagemDoController = "CoreWebView2Controller members can only be accessed from the UI thread.";

    private const string TrechoNavegadorCaiu = "browser process crashed";
    private const string TrechoOutroAmbiente = "already initialized with a different CoreWebView2Environment";
    private const string EspacoDoSdk = "Microsoft.Web.WebView2.";
    private const int ControllerFechado = unchecked((int)0x8007139F);

    /// <summary>O botão do painel de erro das camadas.</summary>
    public const string TentarDeNovo = "Tentar de novo";

    /// <summary>
    /// A exceção veio do WebView2? Olha a corrente inteira (InnerException e AggregateException):
    /// tipo do SDK, pilha que passa pelo SDK, ou uma das frases que só o SDK escreve. Exceção do
    /// próprio caixa continua sendo do caixa: essa ainda merece a caixa de aviso.
    /// </summary>
    public static bool EhDoWebView2(Exception? ex)
    {
        foreach (var e in Corrente(ex))
        {
            if ((e.GetType().FullName ?? "").StartsWith(EspacoDoSdk, StringComparison.Ordinal)) return true;
            if ((e.StackTrace ?? "").Contains(EspacoDoSdk, StringComparison.Ordinal)) return true;
            var m = e.Message ?? "";
            if (m.Contains(MensagemDoController, StringComparison.Ordinal)
                || m.Contains(TrechoNavegadorCaiu, StringComparison.Ordinal)
                || m.Contains(TrechoOutroAmbiente, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Que tipo de falha é. Sem exceção nenhuma, "outra".</summary>
    public static TipoFalhaWeb Classificar(Exception? ex)
    {
        var corrente = Corrente(ex).ToList();
        if (corrente.Any(e => e.GetType().Name == "WebView2RuntimeNotFoundException")) return TipoFalhaWeb.RuntimeAusente;
        if (corrente.Any(e => (e.Message ?? "").Contains(TrechoOutroAmbiente, StringComparison.Ordinal))) return TipoFalhaWeb.JaIniciadoComOutroAmbiente;
        if (corrente.Any(e => (e.Message ?? "").Contains(TrechoNavegadorCaiu, StringComparison.Ordinal) || e.HResult == ControllerFechado))
            return TipoFalhaWeb.NavegadorCaiu;
        return TipoFalhaWeb.Outra;
    }

    /// <summary>
    /// A frase do painel de erro, em UMA linha: o que fazer. Sem código de erro, sem "detalhe
    /// técnico" (o detalhe vai para o diagnóstico). Só o runtime ausente fala do componente: o
    /// painel antigo mandava instalar para QUALQUER erro, e no Castelo o runtime podia estar lá.
    /// Revisão 15/09: a mesma falta tinha três instruções (painel, aviso na venda, cabeçalho da
    /// aba), e "rode o instalador" não serve para quem atualiza pelo botão. Agora é uma só, igual à
    /// de <see cref="SessaoWhatsApp"/>.
    /// </summary>
    /// <param name="oQue">"O chat" ou "O WhatsApp".</param>
    public static string Painel(TipoFalhaWeb tipo, string oQue) => tipo switch
    {
        TipoFalhaWeb.RuntimeAusente => "Falta um componente da Microsoft neste PC. Chame o suporte.",
        _ => $"{oQue} não abriu agora. Toque em {TentarDeNovo}.",
    };

    /// <summary>
    /// Depois de uma falha na inicialização: recriar o controle? descartar o ambiente?
    ///  · controle que já recebeu EnsureCoreWebView2Async NUNCA é reaproveitado: se o Ensure
    ///    falhou no meio o controller ficou gravado (caminho A); se passou, uma nova tentativa com
    ///    outro ambiente lança ArgumentException (caminho C);
    ///  · o ambiente só fica guardado quando o Ensure terminou com ele e o navegador não caiu.
    /// </summary>
    public static (bool RecriarControle, bool DescartarAmbiente) AposFalha(bool ensureChamado, bool ensureTerminou, TipoFalhaWeb tipo)
        => (ensureChamado, !ensureTerminou || tipo is TipoFalhaWeb.NavegadorCaiu or TipoFalhaWeb.RuntimeAusente);

    /// <summary>
    /// Uma linha para o arquivo de diagnóstico: onde, tipo, exceção, HResult, a frase (curta),
    /// a thread que lançou, a thread da tela e a versão do runtime. É o que responde "foi
    /// thread? foi o runtime?" sem pedir para reproduzir na loja.
    /// </summary>
    public static string Linha(string onde, Exception? ex, int thread, int threadDaTela, string? runtime)
    {
        var raiz = Corrente(ex).LastOrDefault() ?? ex;
        var msg = (raiz?.Message ?? "").Replace('\r', ' ').Replace('\n', ' ');
        if (msg.Length > 160) msg = msg[..160] + "…";
        var linha = $"{onde}: {Classificar(ex)} {raiz?.GetType().Name ?? "?"} 0x{(raiz?.HResult ?? 0):X8} \"{msg}\" "
                  + $"thread={thread} tela={threadDaTela} runtime={(string.IsNullOrWhiteSpace(runtime) ? "ausente" : runtime)}";
        return linha.Length <= 400 ? linha : linha[..400];
    }

    private static IEnumerable<Exception> Corrente(Exception? ex)
    {
        var fila = new Queue<Exception>();
        if (ex is not null) fila.Enqueue(ex);
        var vistos = 0;
        while (fila.Count > 0 && vistos++ < 16)
        {
            var e = fila.Dequeue();
            yield return e;
            if (e is AggregateException ag) foreach (var i in ag.InnerExceptions) fila.Enqueue(i);
            else if (e.InnerException is not null) fila.Enqueue(e.InnerException);
        }
    }
}

/// <summary>
/// No máximo N por hora (janela que começa no primeiro uso depois de vencida). É o teto da
/// recuperação automática das camadas: recarregar e recriar sem teto vira moedor num PC fraco.
/// </summary>
public sealed class TetoPorHora
{
    private readonly int _maximo;
    private DateTime _inicio = DateTime.MinValue;
    private int _usados;

    public TetoPorHora(int maximo) => _maximo = maximo;

    public bool Permitir(DateTime agoraUtc)
    {
        if (agoraUtc - _inicio > TimeSpan.FromHours(1)) { _inicio = agoraUtc; _usados = 0; }
        return ++_usados <= _maximo;
    }
}

/// <summary>
/// A mesma falha em rajada vai N vezes por janela para o log; as outras só contam. Uma falha de
/// WebView2 que nasce no layout se repete a cada passada de layout, e o erros.log não pode
/// encher o disco do PC da loja.
/// </summary>
public sealed class FiltroDeRepeticao
{
    private readonly int _porJanela;
    private readonly TimeSpan _janela;
    private readonly Dictionary<string, (DateTime Inicio, int Vezes)> _vistos = new();
    private readonly object _trava = new();

    public FiltroDeRepeticao(int porJanela, TimeSpan janela) { _porJanela = porJanela; _janela = janela; }

    /// <summary>Quantas ficaram de fora do log desde que o processo abriu.</summary>
    public int Silenciadas { get; private set; }

    public bool Registrar(string assinatura, DateTime agoraUtc)
    {
        lock (_trava)
        {
            if (!_vistos.TryGetValue(assinatura, out var v) || agoraUtc - v.Inicio > _janela) v = (agoraUtc, 0);
            v.Vezes++;
            _vistos[assinatura] = v;
            if (v.Vezes <= _porJanela) return true;
            Silenciadas++;
            return false;
        }
    }
}
