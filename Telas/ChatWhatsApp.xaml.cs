using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Pdv.Telas;

/// <summary>
/// O WHATSAPP DA LOJA dentro do caixa (11/09/2026, pedido do dono). O WhatsApp Web
/// mora num WebView2 com perfil próprio em ProgramData: o QR é lido uma vez e o
/// login sobrevive a reinício e a atualização do exe. A camada vive no MainWindow e
/// nunca é destacada: trocar de aba não derruba a conversa nem o observador.
///
/// O que o caixa lê da página é só o TÍTULO ("(3) WhatsApp"), que é o que menos
/// muda ali, com um plano B pelos selos das conversas; o número vai para o
/// <see cref="ServicoWhatsApp"/>, e é ele que acende o selo e toca na venda.
///
/// Quiosque: sem DevTools, sem menu de contexto, sem janela nova, e as
/// notificações do navegador ficam negadas (o aviso é o do caixa, não o do Windows).
/// Máquina sem o runtime WebView2 não derruba o caixa: a tela explica e o resto segue.
/// </summary>
public partial class ChatWhatsApp : UserControl
{
    public event Action? Voltou;

    private const string UrlWhatsApp = "https://web.whatsapp.com/";
    /// <summary>Subpasta do perfil em ProgramData\PdvNativo. Separada da do chat do iFood.</summary>
    public const string PastaPerfil = "webview-whatsapp";

    private bool _pronto, _iniciando;
    private DispatcherTimer? _poll;

    public ChatWhatsApp() { InitializeComponent(); }

    /// <summary>Pré-aquece o WebView2 para o selo acender na venda antes de alguém abrir a aba.</summary>
    public async Task PreAquecerAsync() { if (!_pronto) await IniciarAsync(); }

    private async Task IniciarAsync()
    {
        if (_iniciando || _pronto) return;
        _iniciando = true;
        try
        {
            TxtEstado.Text = "carregando…";
            var perfil = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PdvNativo", PastaPerfil);
            Directory.CreateDirectory(perfil);

            var ambiente = await CoreWebView2Environment.CreateAsync(null, perfil);
            await Web.EnsureCoreWebView2Async(ambiente);
            var core = Web.CoreWebView2;

            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            // Notificação, câmera, microfone e localização: negadas. O aviso de mensagem é
            // o do caixa; o resto não tem lugar num PC de balcão.
            core.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind is CoreWebView2PermissionKind.Notifications
                    or CoreWebView2PermissionKind.Camera
                    or CoreWebView2PermissionKind.Microphone
                    or CoreWebView2PermissionKind.Geolocation)
                    e.State = CoreWebView2PermissionState.Deny;
            };
            // Link que abriria outra janela fica nesta mesma tela.
            core.NewWindowRequested += (_, e) => e.Handled = true;

            core.NavigationCompleted += (_, e) =>
                TxtEstado.Text = e.IsSuccess ? "WhatsApp Web" : "sem conexão: toque em Recarregar";
            core.WebMessageReceived += OnWebMessage;

            await core.AddScriptToExecuteOnDocumentCreatedAsync(ScriptContador);

            Web.Source = new Uri(UrlWhatsApp);
            Web.Visibility = Visibility.Visible;
            PainelErro.Visibility = Visibility.Collapsed;
            _pronto = true;

            // rede de segurança: reconta a cada 5 s mesmo se o observador falhar
            _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _poll.Tick += async (_, _) =>
            {
                try { await core.ExecuteScriptAsync("window.pdvContarWa && window.pdvContarWa()"); }
                catch { /* navegação em curso: o próximo tick tenta de novo */ }
            };
            _poll.Start();
        }
        catch (Exception ex)
        {
            Web.Visibility = Visibility.Collapsed;
            PainelErro.Visibility = Visibility.Visible;
            TxtEstado.Text = "";
            TxtErro.Text =
                "O componente de navegação do Windows (WebView2) não está disponível " +
                "nesta máquina. Instale o \"WebView2 Runtime\" da Microsoft e abra o " +
                "WhatsApp de novo. O restante do caixa segue funcionando normalmente.\n\n" +
                "Detalhe técnico: " + ex.Message;
        }
        finally { _iniciando = false; }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? txt;
        try { txt = e.TryGetWebMessageAsString(); }
        catch { return; }
        if (string.IsNullOrEmpty(txt)) return;
        try
        {
            using var doc = JsonDocument.Parse(txt);
            if ((doc.RootElement.TryGetProperty("tipo", out var t) ? t.GetString() : null) != "naolidas") return;
            if (doc.RootElement.TryGetProperty("total", out var n) && n.TryGetInt32(out var total))
                ServicoWhatsApp.Reportar(total);
            else if (doc.RootElement.TryGetProperty("titulo", out var tit))
                ServicoWhatsApp.ReportarTitulo(tit.GetString());
        }
        catch { /* mensagem malformada não derruba nada */ }
    }

    private void Recarregar(object sender, RoutedEventArgs e)
    {
        ServicoWhatsApp.Recomecar();
        if (!_pronto) { _ = IniciarAsync(); return; }
        try { Web.CoreWebView2?.Reload(); } catch { _ = IniciarAsync(); }
    }

    private void Voltar(object sender, RoutedEventArgs e) => Voltou?.Invoke();

    /// <summary>
    /// Roda a cada carga da página. Lê o título ("(3) WhatsApp") e, sem número nele,
    /// soma os selos de não lidas da lista de conversas. Manda só quando muda.
    /// </summary>
    private const string ScriptContador = """
        (function () {
          if (window.__pdvWa) return; window.__pdvWa = true;
          function envia(o){ try { window.chrome.webview.postMessage(JSON.stringify(o)); } catch (e) {} }
          var ultimo = null;
          window.pdvContarWa = function () {
            try {
              var t = document.title || '';
              var m = t.match(/^\s*\((\d+)\)/);
              var n = m ? parseInt(m[1], 10) : 0;
              if (!m) {
                var soma = 0;
                document.querySelectorAll('span[aria-label*="não lida"], span[aria-label*="nao lida"], span[aria-label*="unread"]')
                  .forEach(function (b) {
                    var x = parseInt((b.getAttribute('aria-label') || '').replace(/\D+/g, ''), 10);
                    if (!isNaN(x)) soma += x;
                  });
                if (soma > 0) n = soma;
              }
              if (n !== ultimo) { ultimo = n; envia({ tipo: 'naolidas', total: n }); }
            } catch (e) {}
          };
          var pend = null;
          function agenda(){ if (pend) return; pend = setTimeout(function(){ pend = null; window.pdvContarWa(); }, 300); }
          function liga(){
            try { new MutationObserver(agenda).observe(document.querySelector('title') || document.head, { childList: true, subtree: true, characterData: true }); } catch (e) {}
            try { new MutationObserver(agenda).observe(document.body, { childList: true, subtree: true }); } catch (e) {}
            setInterval(window.pdvContarWa, 3000);
            window.pdvContarWa();
          }
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', liga); else liga();
        })();
        """;
}
