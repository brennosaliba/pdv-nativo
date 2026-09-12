using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Pdv;

/// <summary>
/// O SPOTIFY ESCONDIDO ATRÁS DO PDV (12/09/2026, pedido do dono).
///
/// A lista de aparelhos do painel (Música) é a lista do Spotify: só aparece quem está
/// com o app aberto e logado na conta da empresa. No modo quiosque o Windows abre
/// direto no PDV e nada mais carrega, então o PC do caixa nunca aparecia na lista e a
/// música tinha que tocar noutro aparelho. Com esta opção ligada, o PRÓPRIO PDV abre o
/// app do Spotify ao iniciar e o mantém minimizado, sem barra e sem janela: na tela
/// continua só o PDV, e o caixa vira um aparelho na lista (com o nome deste computador).
///
/// O que o dono precisa fazer uma vez no PC da loja: instalar o Spotify e entrar com a
/// conta da empresa. Senha é dele; o PDV só abre o programa.
/// </summary>
public static class SpotifyNoCaixa
{
    /// <summary>Config local: "1" = o PDV abre e esconde o Spotify ao iniciar.</summary>
    public const string Chave = "spotify_escondido";

    private const int SW_MINIMIZE = 6;
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    private static DispatcherTimer? _vigia;
    private static DateTime _ultimaAbertura = DateTime.MinValue;

    /// <summary>Os lugares onde o Spotify costuma estar no Windows, na ordem em que se procura.</summary>
    public static IEnumerable<string> Candidatos(string localAppData, string appData)
    {
        yield return Path.Combine(localAppData, "Microsoft", "WindowsApps", "Spotify.exe");   // versão da Loja (atalho de execução)
        yield return Path.Combine(appData, "Spotify", "Spotify.exe");                          // instalador clássico
    }

    /// <summary>O caminho do Spotify neste PC, ou null quando não está instalado.</summary>
    public static string? Caminho()
    {
        try
        {
            return Candidatos(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                              Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
                .FirstOrDefault(File.Exists);
        }
        catch { return null; }
    }

    public static bool Instalado => Caminho() is not null;

    /// <summary>Tem algum processo do Spotify de pé?</summary>
    public static bool Rodando
    {
        get { try { return Process.GetProcessesByName("Spotify").Length > 0; } catch { return false; } }
    }

    /// <summary>
    /// Liga: abre o Spotify (se não estiver aberto) e passa a vigiar a cada 30 s: janela
    /// visível vira minimizada; processo que morreu é reaberto (no máximo uma vez a cada
    /// 2 minutos, para não brigar com alguém fechando de propósito).
    /// Devolve a mensagem de erro, ou null.
    /// </summary>
    public static string? Iniciar()
    {
        var caminho = Caminho();
        if (caminho is null) return "O Spotify não está instalado neste computador.";
        var erro = Abrir(caminho);
        if (_vigia is null)
        {
            _vigia = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _vigia.Tick += (_, _) => Vigiar();
            _vigia.Start();
        }
        // a janela leva alguns segundos para nascer: esconde assim que aparecer
        _ = EsconderQuandoAparecerAsync();
        return erro;
    }

    /// <summary>Desliga a vigia (o Spotify que já está aberto fica como está).</summary>
    public static void Parar()
    {
        _vigia?.Stop();
        _vigia = null;
    }

    private static string? Abrir(string caminho)
    {
        if (Rodando) return null;
        if (DateTime.Now - _ultimaAbertura < TimeSpan.FromMinutes(2)) return null;
        _ultimaAbertura = DateTime.Now;
        try
        {
            // "--minimized" é o que o próprio Spotify usa quando inicia com o Windows; se
            // esta versão ignorar, a vigia minimiza a janela assim que ela aparecer.
            Process.Start(new ProcessStartInfo(caminho, "--minimized") { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) { return "Não consegui abrir o Spotify: " + ex.Message; }
    }

    private static async Task EsconderQuandoAparecerAsync()
    {
        for (var i = 0; i < 60; i++)          // até 30 s
        {
            await Task.Delay(500);
            if (Esconder()) return;
        }
    }

    /// <summary>Minimiza toda janela visível do Spotify. true = achou e minimizou alguma.</summary>
    public static bool Esconder()
    {
        var alguma = false;
        try
        {
            foreach (var p in Process.GetProcessesByName("Spotify"))
            {
                try
                {
                    var h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) continue;
                    if (IsWindowVisible(h) && !IsIconic(h)) { ShowWindow(h, SW_MINIMIZE); alguma = true; }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return alguma;
    }

    private static void Vigiar()
    {
        try
        {
            if (!Rodando) { var c = Caminho(); if (c is not null) { Abrir(c); _ = EsconderQuandoAparecerAsync(); } return; }
            Esconder();
        }
        catch { }
    }

    /// <summary>
    /// Este PC aparece na lista de aparelhos do Spotify? O app de computador se apresenta
    /// com o nome da máquina (o painel mostra "DESKTOP-7AJ1OD7"). Comparação sem caixa.
    /// </summary>
    public static bool EsteCaixaNaLista(IEnumerable<Nucleo.Spotify.Aparelho> aparelhos, string nomeDaMaquina)
        => aparelhos.Any(a => string.Equals(a.Nome, nomeDaMaquina, StringComparison.OrdinalIgnoreCase));
}
