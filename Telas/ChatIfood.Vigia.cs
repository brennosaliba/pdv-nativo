using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// O LADO DO APARELHO DO VIGIA NA TELA DO CHAT (1.0.50, 25/10/2026, SQL 220; docs/VIGIA_CONTRATO.md do ERP).
/// Esta metade da classe so expoe ao <see cref="ServicoVigia"/> o que ele precisa da pagina do chat: ler a tela
/// (casca, login, cortina, conversa), tirar o print, recarregar (simples ou forte), mostrar o Gestor inteiro ou so a
/// conversa, gravar o diagnostico, sair da conta do Gestor. Tudo na thread da tela. Os erros do console do WebView2
/// vao para o sinal (mascarados pelo Nucleo).
/// </summary>
public partial class ChatIfood
{
    /// <summary>O controle do chat vivo (o MainWindow mantem um so). O Vigia fala com ele por aqui.</summary>
    public static ChatIfood? Instancia { get; private set; }

    private CoreWebView2DevToolsProtocolEventReceiver? _logDoConsole;

    /// <summary>Liga o Vigia quando o WebView2 do chat nasce: os erros do console pelo CDP e o relogio do sinal.</summary>
    private void LigarVigia(CoreWebView2 core)
    {
        Instancia = this;
        try
        {
            _logDoConsole = core.GetDevToolsProtocolEventReceiver("Log.entryAdded");
            _logDoConsole.DevToolsProtocolEventReceived += (_, e) => ServicoVigia.AnotarErroDoConsole(e.ParameterObjectAsJson);
            _ = core.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
        }
        catch (Exception ex) { Diag("vigia: o log do console nao ligou (" + ex.GetType().Name + ")"); }
        ServicoVigia.Ligar();
    }

    internal bool VigiaPronto => _pronto && Web?.CoreWebView2 is not null;
    internal bool VigiaPedeLogin => _gestorPedeLogin;
    internal bool VigiaSemRecorte => _semRecorte;
    internal bool VigiaGestorInteiro => _gestorInteiro;
    internal string VigiaNavegacao => _navegacao;

    /// <summary>A leitura da pagina (casca, login, cortina, conversa), como JSON com aspas por fora.</summary>
    internal Task<string> VigiaLerTelaAsync() => HospedeWebView2.NaTela(Dispatcher, async () =>
    {
        if (!_pronto || Web?.CoreWebView2 is not { } c) return "\"{}\"";
        return await c.ExecuteScriptAsync(VigiaDoAparelho.JsLeituraDaTela);
    });

    /// <summary>O print do chat (JPEG). Nulo sem navegador ou quando a captura falha.</summary>
    internal Task<byte[]?> VigiaPrintAsync() => HospedeWebView2.NaTela(Dispatcher, async () =>
    {
        try
        {
            if (Web?.CoreWebView2 is not { } c) return null;
            using var ms = new MemoryStream();
            await c.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, ms);
            return ms.ToArray();
        }
        catch (Exception ex) { Diag("vigia: print falhou (" + ex.GetType().Name + ")"); return null; }
    });

    /// <summary>recarregar (Reload) ou recarga_forte (cache e service worker limpos, o mesmo da 1.0.47).</summary>
    internal Task VigiaRecarregarAsync(bool forte, string motivo) => HospedeWebView2.NaTela(Dispatcher, async () =>
    {
        if (Web?.CoreWebView2 is not { } c) { _ = RecriarAsync("vigia: " + motivo + " sem navegador"); return; }
        if (forte) await RecarregarForteAsync("vigia: " + motivo);
        else { Diag("vigia: recarregar (" + motivo + ")"); c.Reload(); }
    });

    /// <summary>gestor_inteiro / so_a_conversa, sem a senha (quem manda e o servidor ou o dono pelo ERP).</summary>
    internal Task<bool> VigiaModoAsync(bool inteiro) => HospedeWebView2.NaTela(Dispatcher, async () =>
    {
        if (Web?.CoreWebView2 is not { } c) return false;
        _gestorInteiro = inteiro;
        TxtGestorInteiro.Text = inteiro ? "Só o chat" : "Gestor inteiro";
        await c.ExecuteScriptAsync(inteiro ? JsGestorInteiro : JsSoOChat);
        TxtEstado.Text = inteiro ? "Gestor inteiro (toque em Só o chat para voltar)" : "painel do chat";
        Diag("vigia: " + (inteiro ? "gestor inteiro" : "so a conversa"));
        return true;
    });

    /// <summary>coletar_diagnostico: o mesmo arquivo do botao Diagnostico, sem dialogo. Devolve o caminho e o texto.</summary>
    internal Task<(string? Caminho, string? Texto)> VigiaDiagnosticoAsync() => HospedeWebView2.NaTela(Dispatcher, async () =>
    {
        try
        {
            if (Web?.CoreWebView2 is not { } c) return ((string?)null, (string?)"o chat ainda nao abriu");
            var bruto = await c.ExecuteScriptAsync(ScriptDiagnostico);
            var texto = JsonSerializer.Deserialize<string>(bruto) ?? "";
            var caminho = Path.Combine(Banco.Pasta, $"gestor-diagnostico-{DateTime.Now:HHmmss}.txt");
            string runtime;
            try { runtime = c.Environment.BrowserVersionString; } catch { runtime = "?"; }
            var completo = $"url: {c.Source}\ntitulo: {c.DocumentTitle}\nquando: {DateTime.Now:dd/MM/yyyy HH:mm:ss} (pelo vigia)\n"
                + $"caixa: {Atualizacao.VersaoInstalada() ?? "?"} runtime WebView2: {runtime} navegacao: {_navegacao} modo: {(_gestorInteiro ? "gestor_inteiro" : "so_chat")}{(_semRecorte ? " (sem o recorte)" : "")}\n\n{texto}";
            File.WriteAllText(caminho, completo);
            Diag("vigia: diagnostico gravado em " + caminho);
            return (caminho, completo);
        }
        catch (Exception ex) { Diag("vigia: diagnostico falhou (" + ex.GetType().Name + ")"); return (null, "nao gravou (" + ex.GetType().Name + ")"); }
    });

    /// <summary>relogar_gestor com o Gestor logado: sai da conta (a rota de logout do Gestor); o login sozinho entra em seguida.</summary>
    internal Task VigiaSairDoGestorAsync() => HospedeWebView2.NaTela(Dispatcher, async () =>
    {
        if (Web?.CoreWebView2 is not { } c) return;
        Diag("vigia: saindo da conta do Gestor para entrar de novo");
        c.Navigate("https://gestordepedidos.ifood.com.br/#/logout");
        await Task.CompletedTask;
    });
}
