using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace Pdv.Telas;

/// <summary>
/// O Gestor de Pedidos do iFood dentro do PDV, pelo WebView2 (o Chromium que
/// já vem no Windows). É a única forma de ter o chat: o widget não tem API
/// pública de envio — quem tenta automatizar por fora quebra a cada deploy
/// deles.
///
/// Perfil persistente em ProgramData: o gerente loga UMA vez e a sessão
/// sobrevive a reinício e a atualização do exe. Máquina sem o runtime
/// WebView2 não derruba o caixa: mostra o aviso com o caminho de instalação
/// e o resto do PDV segue intacto.
/// </summary>
public partial class ChatIfood : UserControl
{
    public event Action? Voltou;

    private const string UrlGestor = "https://gestordepedidos.ifood.com.br/";
    private bool _pronto;

    public ChatIfood()
    {
        InitializeComponent();
        Loaded += async (_, _) => { if (!_pronto) await IniciarAsync(); };
    }

    private async Task IniciarAsync()
    {
        try
        {
            TxtEstado.Text = "carregando…";
            // perfil em ProgramData, NUNCA na pasta do exe: atualização de versão
            // troca o executável e o login tem que continuar de pé
            var perfil = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PdvNativo", "webview");
            Directory.CreateDirectory(perfil);

            var ambiente = await CoreWebView2Environment.CreateAsync(null, perfil);
            await Web.EnsureCoreWebView2Async(ambiente);

            // quiosque: sem DevTools nem menu de contexto pro operador se perder
            Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;

            Web.CoreWebView2.NavigationCompleted += (_, e) =>
                TxtEstado.Text = e.IsSuccess ? "" : "sem conexão: toque em Recarregar";

            Web.Source = new Uri(UrlGestor);
            Web.Visibility = Visibility.Visible;
            PainelErro.Visibility = Visibility.Collapsed;
            _pronto = true;
        }
        catch (Exception ex)
        {
            Web.Visibility = Visibility.Collapsed;
            PainelErro.Visibility = Visibility.Visible;
            TxtEstado.Text = "";
            TxtErro.Text =
                "O componente de navegação do Windows (WebView2) não está disponível " +
                "nesta máquina. Instale o \"WebView2 Runtime\" da Microsoft e abra o " +
                "chat de novo. O restante do PDV segue funcionando normalmente.\n\n" +
                "Detalhe técnico: " + ex.Message;
        }
    }

    private void Recarregar(object sender, RoutedEventArgs e)
    {
        if (_pronto) Web.Reload();
        else _ = IniciarAsync();
    }

    private void Voltar(object sender, RoutedEventArgs e) => Voltou?.Invoke();
}
