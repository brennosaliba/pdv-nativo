using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Pdv.Testes;

/// <summary>
/// O CAIXA ANTERIOR QUE AINDA NÃO SAIU (1.0.35, Savassi, 14/10/2026). Um segundo processo que abre
/// um WebView2 DE VERDADE no perfil dado, com as opções dadas, escreve "PRONTO &lt;pid do navegador&gt;"
/// e fica de pé até quem o abriu fechar a entrada padrão (ou 3 min, para nunca sobrar).
///   Pdv.Testes.exe --sonda-perfil-webview &lt;perfil&gt; &lt;argumentos|-&gt;
/// Usado por TestesPerfilEmUso (RP-*).
/// </summary>
internal static class SondaPerfilWebView2
{
    public const string Modo = "--sonda-perfil-webview";
    public const string Pronto = "PRONTO ";

    /// <summary>O lado da sonda (este processo é o "caixa anterior").</summary>
    public static int Rodar(string perfil, string argumentos)
    {
        var codigo = 9;
        var t = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += async (_, _) =>
            {
                Window? janela = null;
                WebView2? web = null;
                try
                {
                    web = new WebView2();
                    janela = new Window
                    {
                        Width = 320, Height = 240, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None,
                        ShowInTaskbar = false, ShowActivated = false, Title = "sonda do perfil", Content = web,
                    };
                    janela.Show();
                    Directory.CreateDirectory(perfil);
                    var opcoes = argumentos == "-" ? null : new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = argumentos };
                    var ambiente = await CoreWebView2Environment.CreateAsync(null, perfil, opcoes);
                    await web.EnsureCoreWebView2Async(ambiente);
                    Console.WriteLine(Pronto + web.CoreWebView2.BrowserProcessId);
                    Console.Out.Flush();
                    var fim = Task.Run(() => { try { Console.In.ReadToEnd(); } catch { } });
                    await Task.WhenAny(fim, Task.Delay(TimeSpan.FromMinutes(3)));
                    codigo = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FALHOU {ex.GetType().Name} 0x{ex.HResult:X8}");
                    Console.Out.Flush();
                    codigo = 2;
                }
                finally
                {
                    try { web?.Dispose(); } catch { }
                    try { janela?.Close(); } catch { }
                    app.Shutdown();
                }
            };
            app.Run();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return codigo;
    }

    /// <summary>O lado da suíte: a sonda viva, o processo dela e o navegador que ela abriu.</summary>
    public sealed class Viva : IAsyncDisposable
    {
        public Process Processo { get; }
        public int Navegador { get; }

        private Viva(Process p, int navegador) { Processo = p; Navegador = navegador; }

        /// <summary>Abre a sonda e espera o navegador dela ficar pronto (null + o motivo, se não ficou).</summary>
        public static async Task<(Viva? Sonda, string Motivo)> AbrirAsync(string perfil, string? argumentos)
        {
            var psi = Sondas.Psi(Modo, perfil, argumentos ?? "-");
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            var p = Process.Start(psi);
            if (p is null) return (null, "o processo não abriu");
            _ = p.StandardError.ReadToEndAsync();
            var prazo = Stopwatch.StartNew();
            while (prazo.Elapsed < TimeSpan.FromSeconds(40))
            {
                var ler = p.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(ler, Task.Delay(TimeSpan.FromSeconds(40) - prazo.Elapsed)) != ler) break;
                var linha = await ler;
                if (linha is null) break;
                if (linha.StartsWith(Pronto, StringComparison.Ordinal) && int.TryParse(linha[Pronto.Length..].Trim(), out var nav))
                    return (new Viva(p, nav), "");
                if (linha.StartsWith("FALHOU", StringComparison.Ordinal))
                {
                    Matar(p);
                    return (null, linha);
                }
            }
            Matar(p);
            return (null, "a sonda não ficou pronta em 40 s");
        }

        /// <summary>
        /// O caixa anterior sai (fecha a entrada). Devolve se o PROCESSO saiu sozinho. O navegador
        /// pode ficar: quando o caixa novo entrou nele, ele é dos dois (ver <see cref="NavegadorSaiuAsync"/>).
        /// </summary>
        public async Task<bool> FecharAsync()
        {
            try { Processo.StandardInput.Close(); } catch { }
            var saiu = await Task.Run(() => Processo.WaitForExit(20_000));
            if (!saiu) Matar(Processo);
            return saiu;
        }

        public async ValueTask DisposeAsync()
        {
            if (!Processo.HasExited) await FecharAsync();
            Processo.Dispose();
        }

        private static void Matar(Process p)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <summary>O processo <paramref name="pid"/> saiu dentro do prazo?</summary>
    public static async Task<bool> NavegadorSaiuAsync(int pid, TimeSpan prazo)
    {
        var relogio = Stopwatch.StartNew();
        while (relogio.Elapsed < prazo)
        {
            if (!Vivo(pid)) return true;
            await Task.Delay(100);
        }
        return !Vivo(pid);
    }

    public static bool Vivo(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }
}
