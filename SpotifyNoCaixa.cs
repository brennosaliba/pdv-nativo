using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;
using Pdv.Nucleo;

namespace Pdv;

/// <summary>
/// O SPOTIFY ESCONDIDO ATRÁS DO PDV (12/09/2026, pedido do dono).
///
/// A lista de aparelhos do painel (Música) é a lista do Spotify: só aparece quem está
/// com o app aberto e logado na conta da empresa. No modo quiosque o Windows abre
/// direto no PDV e nada mais carrega, então o PC do caixa nunca aparecia na lista e a
/// música tinha que tocar noutro aparelho. Com esta opção ligada, o PRÓPRIO PDV abre o
/// app do Spotify ao iniciar e o mantém escondido, sem barra e sem janela: na tela
/// continua só o PDV, e o caixa vira um aparelho na lista (com o nome deste computador).
///
/// 1.0.37 (15/10/2026): "com o PDV no lugar da área de trabalho, o Spotify só toca depois de
/// liberar o desktop". O PDV abria o atalho da Loja (que roda o SpotifyMigrator e sai) sem
/// conferir nada e sem log. Agora: abre a versão LOGADA (MusicaNoInicio), só depois da rede,
/// confere que o Spotify.exe subiu nesta sessão, anota tudo em ProgramData\PdvNativo\
/// inicio-windows.txt (linhas "[spotify]") e, com o PC recém ligado, toca a playlist da loja
/// neste caixa e só para quando o Spotify confirma. Nada aqui lança para o PDV.
///
/// O que o dono precisa fazer uma vez no PC da loja: instalar o Spotify e entrar com a
/// conta da empresa. Senha é dele; o PDV só abre o programa.
/// </summary>
public static class SpotifyNoCaixa
{
    /// <summary>Config local: "1" = o PDV abre e esconde o Spotify ao iniciar.</summary>
    public const string Chave = "spotify_escondido";

    // 1.0.33 (14/10/2026, o dono): "esconder o Spotify sem mostrar ele no minimizar do PDV nem no
    // Alt+Tab". Minimizada a janela continua no Alt+Tab; ESCONDIDA (SW_HIDE) ela some de tudo e o
    // app segue aberto e logado (o aparelho continua na lista da Música). É o mesmo estado de
    // quando o próprio Spotify vai para a bandeja.
    private const int SW_HIDE = 0, SW_SHOWNORMAL = 1, SW_SHOWMINNOACTIVE = 7;
    private const uint GW_OWNER = 4;
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);

    private static DispatcherTimer? _vigia;
    /// <summary>As janelas que o PDV escondeu: voltam (minimizadas) quando a opção é desligada.</summary>
    private static readonly HashSet<IntPtr> _escondidas = new();
    private static readonly object _trava = new();
    private static DateTime _ultimaAbertura = DateTime.MinValue;
    private static DateTime _mostrarAte = DateTime.MinValue;
    private static bool _viuRodando;
    /// <summary>A vigia só reabre depois da primeira abertura (que espera a rede); antes disso ela só esconde.</summary>
    private static volatile bool _primeiraAberturaFeita;
    private static int _abrindo, _tocando;
    private static CancellationTokenSource? _pararDeTocar;

    private static void Log(string texto) => InicioDoWindowsNoCaixa.Anotar("[spotify] " + texto);

    /// <summary>Qual Spotify este PC tem e se está logado (do prefs só se olha se a chave do login existe).</summary>
    public static MusicaNoInicio.Escolha Escolha()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var exeC = MusicaNoInicio.ExeClassico(roaming);
            var exeL = MusicaNoInicio.ExeLoja(local);
            return MusicaNoInicio.Escolher(File.Exists(exeC), LogadoEm(MusicaNoInicio.PrefsClassico(roaming)),
                                           File.Exists(exeL), LogadoEm(MusicaNoInicio.PrefsLoja(local)), exeC, exeL);
        }
        catch { return new MusicaNoInicio.Escolha(MusicaNoInicio.Versao.Nenhuma, null, "", false); }
    }

    private static bool LogadoEm(string prefs)
    {
        try
        {
            if (!File.Exists(prefs)) return false;
            using var fs = new FileStream(prefs, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            return MusicaNoInicio.Logado(Linhas(r));
        }
        catch { return false; }

        static IEnumerable<string> Linhas(StreamReader r) { string? l; while ((l = r.ReadLine()) is not null) yield return l; }
    }

    /// <summary>O caminho do Spotify que o PDV abre neste PC, ou null quando não está instalado.</summary>
    public static string? Caminho() => Escolha().Exe;

    public static bool Instalado => Caminho() is not null;

    private static int Sessao { get { try { using var p = Process.GetCurrentProcess(); return p.SessionId; } catch { return -1; } } }

    private static List<(string Nome, int Sessao)> Processos()
    {
        var l = new List<(string, int)>();
        foreach (var nome in new[] { "Spotify", "SpotifyMigrator", "SpotifyLauncher", "SpotifyStartupTask" })
            foreach (var p in Process.GetProcessesByName(nome))
            {
                try { l.Add((nome, p.SessionId)); } catch { }
                finally { p.Dispose(); }
            }
        return l;
    }

    private static HashSet<uint> PidsDoSpotify(int sessao)
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("Spotify"))
        {
            try { if (p.SessionId == sessao) pids.Add((uint)p.Id); } catch { }
            finally { p.Dispose(); }
        }
        return pids;
    }

    /// <summary>O Spotify (qualquer versão) está de pé NESTA sessão?</summary>
    public static bool Rodando
    {
        get { try { return MusicaNoInicio.Rodando(Processos(), Sessao); } catch { return false; } }
    }

    /// <summary>
    /// Chamado pelo App ao abrir o PDV. Nunca lança: falha do Spotify não derruba o caixa.
    /// Toca sozinho só com a sessão do Windows recém aberta (MusicaNoInicio.TocarAoAbrir).
    /// </summary>
    public static void AoAbrirOPdv(string? opcao)
    {
        try
        {
            if (opcao != "1") { Log("desligado na Configuração"); return; }
            var desde = TempoDesdeOLogon();
            var tocar = MusicaNoInicio.TocarAoAbrir(desde);
            Log($"PDV abrindo; sessão do Windows aberta há {(int)desde.TotalMinutes} min; quiosque: {(Quiosque.Ligado ? "sim" : "não")}; "
                + $"tocar sozinho: {(tocar ? "sim" : "não (só nos primeiros 15 min)")}");
            if (Iniciar(tocar) is { } erro) Log(erro);
        }
        catch (Exception ex) { Log("ao abrir o PDV: " + ex.GetType().Name); }
    }

    /// <summary>
    /// Há quanto tempo o usuário entrou no Windows: o sihost desta sessão nasce no logon. Com a
    /// inicialização rápida do Windows o relógio do sistema não zera ao desligar, o logon sim.
    /// </summary>
    private static TimeSpan TempoDesdeOLogon()
    {
        try
        {
            var sessao = Sessao;
            DateTime? inicio = null;
            foreach (var p in Process.GetProcessesByName("sihost"))
            {
                try { if (p.SessionId == sessao) inicio = p.StartTime; } catch { }
                finally { p.Dispose(); }
            }
            if (inicio is { } i) return DateTime.Now - i;
        }
        catch { }
        return TimeSpan.FromMilliseconds(Environment.TickCount64);
    }

    /// <summary>
    /// Liga: passa a vigiar a cada 5 s (janela visível é escondida; processo que morreu é reaberto,
    /// no máximo a cada 2 min) e, em segundo plano, abre o Spotify depois da rede e confere.
    /// Devolve a mensagem de erro, ou null.
    /// </summary>
    public static string? Iniciar(bool tocarSozinho = false)
    {
        var esc = Escolha();
        Log(MusicaNoInicio.Descrever(esc));
        if (esc.Exe is null) return "O Spotify não está instalado neste computador.";
        if (_vigia is null)
        {
            // 1.0.33: a cada 5 s (era 30): janela que o Spotify reabre some antes de alguém ver no Alt+Tab
            _vigia = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _vigia.Tick += (_, _) => Vigiar();
            _vigia.Start();
        }
        if (Interlocked.Exchange(ref _abrindo, 1) == 0)
            _ = Task.Run(async () =>
            {
                try { await AbrirDepoisDaRedeAsync(tocarSozinho); }
                catch (Exception ex) { Log("abrir: " + ex.GetType().Name); }
                finally { Volatile.Write(ref _abrindo, 0); }
            });
        return null;
    }

    private static async Task AbrirDepoisDaRedeAsync(bool tocarSozinho)
    {
        var sessao = Sessao;
        if (MusicaNoInicio.Rodando(Processos(), sessao)) Log($"já estava aberto nesta sessão ({sessao}); não abro outro");
        else
        {
            var rede = await EsperarRedeAsync(TimeSpan.FromSeconds(90));
            Log(rede is { } s ? $"rede pronta em {s:0} s" : "sem rede depois de 90 s; abro assim mesmo");
            if (Abrir("início")) await ConfirmarProcessoAsync(sessao, TimeSpan.FromSeconds(30));
        }
        _primeiraAberturaFeita = true;
        _viuRodando = Rodando;
        Log("saída de som padrão: " + SaidaDeSomPadrao());
        if (tocarSozinho) _ = Task.Run(TocarSozinhoAsync);
        await EsconderQuandoAparecerAsync();
    }

    /// <summary>Até <paramref name="max"/> pela rede (interface de pé e o nome da API do Spotify resolvendo). null = não veio.</summary>
    private static async Task<double?> EsperarRedeAsync(TimeSpan max)
    {
        var rel = Stopwatch.StartNew();
        while (rel.Elapsed < max)
        {
            try
            {
                if (NetworkInterface.GetIsNetworkAvailable())
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    if ((await Dns.GetHostAddressesAsync("api.spotify.com", cts.Token)).Length > 0) return rel.Elapsed.TotalSeconds;
                }
            }
            catch { }
            await Task.Delay(3000);
        }
        return null;
    }

    private static async Task ConfirmarProcessoAsync(int sessao, TimeSpan max)
    {
        var rel = Stopwatch.StartNew();
        while (rel.Elapsed < max)
        {
            await Task.Delay(1000);
            var pids = PidsDoSpotify(sessao);
            if (pids.Count > 0) { Log($"Spotify.exe de pé: pid {pids.Min()}, sessão {sessao}, em {rel.Elapsed.TotalSeconds:0} s"); return; }
        }
        Log($"Spotify.exe não apareceu nesta sessão em {max.TotalSeconds:0} s");
    }

    /// <summary>
    /// Desliga a vigia e devolve as janelas que o PDV escondeu (minimizadas, sem roubar o foco):
    /// sem isso, um Spotify pedindo login ficava escondido para sempre.
    /// </summary>
    public static void Parar()
    {
        _vigia?.Stop();
        _vigia = null;
        try { _pararDeTocar?.Cancel(); } catch { }
        lock (_escondidas)
        {
            foreach (var h in _escondidas) { try { ShowWindow(h, SW_SHOWMINNOACTIVE); } catch { } }
            _escondidas.Clear();
        }
        Log("desligado na Configuração: vigia parada, janelas devolvidas");
    }

    /// <summary>
    /// Abre o Spotify escolhido, se nada dele está de pé nesta sessão e a folga de 2 min passou.
    /// <paramref name="aVista"/>: sem "--minimized" (o "Mostrar o Spotify"). true = mandou abrir.
    /// </summary>
    private static bool Abrir(string motivo, bool aVista = false)
    {
        lock (_trava)
        {
            try
            {
                var procs = Processos();
                var sessao = Sessao;
                if (!MusicaNoInicio.PodeAbrir(MusicaNoInicio.Rodando(procs, sessao), MusicaNoInicio.Abrindo(procs, sessao), DateTime.Now, _ultimaAbertura))
                    return false;
                var esc = Escolha();
                if (esc.Exe is null) { Log($"não abri ({motivo}): não instalado"); return false; }
                _ultimaAbertura = DateTime.Now;
                // a clássica vai com "--minimized" (sobe na bandeja); a da Loja passa pelo Migrator, que não conhece argumento
                var args = aVista ? "" : esc.Argumentos;
                using var p = Process.Start(new ProcessStartInfo(esc.Exe, args) { UseShellExecute = true });
                Log($"abri ({motivo}): {MusicaNoInicio.Descrever(esc)}{(args.Length > 0 ? " " + args : "")}");
                return true;
            }
            catch (Exception ex) { Log($"não abri ({motivo}): {ex.GetType().Name} {ex.Message}"); return false; }
        }
    }

    private static async Task EsconderQuandoAparecerAsync()
    {
        for (var i = 0; i < 60; i++)          // até 30 s
        {
            await Task.Delay(500);
            if (Esconder()) return;
        }
    }

    private static string Classe(IntPtr h)
    {
        var sb = new StringBuilder(128);
        try { GetClassName(h, sb, sb.Capacity); } catch { }
        return sb.ToString();
    }

    private static string Titulo(IntPtr h)
    {
        var sb = new StringBuilder(64);
        try { GetWindowText(h, sb, sb.Capacity); } catch { }
        return sb.ToString();
    }

    /// <summary>
    /// Esconde (SW_HIDE) toda janela visível do Spotify DESTA sessão, inclusive a minimizada: fora
    /// da tela, do Alt+Tab e de trás do PDV minimizado. Nos 3 min do "Mostrar o Spotify" não esconde.
    /// true = achou e escondeu alguma. A primeira vez de cada janela vai para o log.
    /// </summary>
    public static bool Esconder()
    {
        if (!MusicaNoInicio.Escondendo(DateTime.Now, _mostrarAte)) return false;
        var alguma = false;
        try
        {
            var pids = PidsDoSpotify(Sessao);
            if (pids.Count == 0) return false;
            EnumWindows((h, _) =>
            {
                try
                {
                    GetWindowThreadProcessId(h, out var pid);
                    if (pids.Contains(pid) && IsWindowVisible(h))
                    {
                        ShowWindow(h, SW_HIDE); alguma = true;
                        bool nova;
                        lock (_escondidas) nova = _escondidas.Add(h);
                        if (nova) Log($"escondi uma janela (pid {pid}, {Classe(h)}, \"{Titulo(h)}\")");
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return alguma;
    }

    /// <summary>
    /// Configuração (admin): mostra a janela do Spotify por 3 min, para entrar na conta ou responder
    /// um aviso do app pelo AnyDesk; depois a vigia volta a esconder. Fechado, abre já. Nunca lança.
    /// </summary>
    public static string Mostrar()
    {
        try
        {
            _mostrarAte = DateTime.Now + MusicaNoInicio.TempoMostrando;
            var pids = PidsDoSpotify(Sessao);
            if (pids.Count == 0)
            {
                lock (_trava) _ultimaAbertura = DateTime.MinValue;
                var abriu = Abrir("mostrar", aVista: true);
                Log("mostrar: estava fechado; " + (abriu ? "abri" : "não abriu"));
                return abriu ? "Abrindo o Spotify. Ele fica à vista por 3 minutos." : "Não consegui abrir o Spotify neste PC.";
            }
            var principais = new List<IntPtr>();
            EnumWindows((h, _) =>
            {
                try
                {
                    GetWindowThreadProcessId(h, out var pid);
                    if (pids.Contains(pid) && GetWindow(h, GW_OWNER) == IntPtr.Zero && Titulo(h).Length > 0
                        && Classe(h).StartsWith("Chrome_WidgetWin", StringComparison.Ordinal))
                        principais.Add(h);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            foreach (var h in principais) ShowWindow(h, SW_SHOWNORMAL);
            if (principais.Count > 0) SetForegroundWindow(principais[0]);
            else if (Escolha().Exe is { } exe)
            {
                // sem janela com título: o Spotify aberto de novo mostra a dele (ele não abre outro)
                using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
            Log($"mostrar: {principais.Count} janela(s) à vista por 3 min");
            return "O Spotify fica à vista por 3 minutos. Depois o PDV esconde de novo.";
        }
        catch (Exception ex) { Log("mostrar: " + ex.GetType().Name); return "Não consegui mostrar o Spotify."; }
    }

    private static void Vigiar()
    {
        try
        {
            if (!Rodando)
            {
                if (!_primeiraAberturaFeita) return;   // a primeira abertura espera a rede, em segundo plano
                if (_viuRodando) { _viuRodando = false; Log("o Spotify fechou ou caiu; reabro (no máximo a cada 2 min)"); }
                if (Abrir("vigia")) _ = EsconderQuandoAparecerAsync();
                return;
            }
            _viuRodando = true;
            Esconder();
        }
        catch { }
    }

    // ── tocar sozinho ao ligar o PC ────────────────────────────────────────────────
    private static async Task<T> Prazo<T>(Func<CancellationToken, Task<T>> f, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        return await f(cts.Token).ConfigureAwait(false);
    }

    /// <summary>A playlist da loja neste caixa, confirmada pelo Spotify (MusicaNoInicio.TocarAteConfirmarAsync). Nunca lança.</summary>
    private static async Task TocarSozinhoAsync()
    {
        if (Interlocked.Exchange(ref _tocando, 1) == 1) return;
        using var parar = new CancellationTokenSource();
        _pararDeTocar = parar;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), parar.Token);   // o app ainda está entrando na conta e se anunciando no Connect
            var maquina = Environment.MachineName;
            string? nomePainel;
            using (var cx = Banco.Abrir()) nomePainel = Vendas.Config(cx, ConfigLojaPainel.ChaveDeviceNome);
            if (nomePainel is { Length: > 0 } && !string.Equals(nomePainel.Trim(), maquina, StringComparison.OrdinalIgnoreCase))
                Log($"o painel aponta outro aparelho ({nomePainel}); o toque sozinho usa este caixa ({maquina})");
            var nuvem = Servicos.Nuvem();
            var sp = new Nucleo.Spotify(nuvem.TokenSpotifyAsync, () => nuvem.UltimoErroSpotify, nuvem.EsquecerTokenSpotify);
            var amb = new MusicaNoInicio.Ambiente(
                ct => Prazo(c => sp.AparelhosAsync(c), ct),
                ct => Prazo(c => sp.EstadoAsync(c), ct),
                (dev, uri, ct) => Prazo(c => sp.TocarPlaylistComStatusAsync(dev, uri, c), ct),
                (v, dev, ct) => Prazo(c => sp.VolumeAsync(v, dev, c), ct),
                () =>
                {
                    using var cx = Banco.Abrir();
                    var vol = int.TryParse(Vendas.Config(cx, ConfigLojaPainel.ChaveVolume), out var n) ? n : (int?)null;
                    return (Vendas.Config(cx, ConfigLojaPainel.ChavePlaylistUri), vol);
                },
                (ts, ct) => Task.Delay(ts, ct),
                Log);
            if (await MusicaNoInicio.TocarAteConfirmarAsync(amb, maquina, parar.Token)) Log("saída de som padrão: " + SaidaDeSomPadrao());
            else if (parar.IsCancellationRequested) Log("tocar sozinho: parado (opção desligada na Configuração)");
        }
        catch (OperationCanceledException) { Log("tocar sozinho: parado (opção desligada na Configuração)"); }
        catch (Exception ex) { Log("tocar sozinho: " + ex.GetType().Name); }
        finally { _pararDeTocar = null; Volatile.Write(ref _tocando, 0); }
    }

    // ── a saída de som padrão (para o "tocando no ERP e sem som na loja") ─────────
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
        int OpenPropertyStore(int access, out IntPtr store);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }

    /// <summary>O nome da saída de som padrão (multimídia) deste Windows, ou o motivo de não saber. Nunca lança.</summary>
    public static string SaidaDeSomPadrao()
    {
        object? en = null;
        IMMDevice? dev = null;
        try
        {
            en = new MMDeviceEnumerator();
            var h = ((IMMDeviceEnumerator)en).GetDefaultAudioEndpoint(0, 1, out dev);   // eRender, eMultimedia
            if (h != 0 || dev is null) return "nenhuma (o Windows não tem saída de som padrão)";
            dev.GetId(out var id);
            var guid = id[(id.LastIndexOf("}.", StringComparison.Ordinal) + 2)..];
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + guid + @"\Properties");
            var nome = k?.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string;
            var placa = k?.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string;
            return string.IsNullOrEmpty(nome) ? guid : placa is { Length: > 0 } ? $"{nome} ({placa})" : nome;
        }
        catch (Exception ex) { return "não deu para ler (" + ex.GetType().Name + ")"; }
        finally
        {
            try { if (dev is not null) Marshal.ReleaseComObject(dev); } catch { }
            try { if (en is not null) Marshal.ReleaseComObject(en); } catch { }
        }
    }

    /// <summary>
    /// Este PC aparece na lista de aparelhos do Spotify? O app de computador se apresenta
    /// com o nome da máquina (o painel mostra "DESKTOP-7AJ1OD7"). Comparação sem caixa.
    /// </summary>
    public static bool EsteCaixaNaLista(IEnumerable<Nucleo.Spotify.Aparelho> aparelhos, string nomeDaMaquina)
        => aparelhos.Any(a => string.Equals(a.Nome, nomeDaMaquina, StringComparison.OrdinalIgnoreCase));

    /// <summary>Este caixa na lista de agora (com o id de agora), ou null.</summary>
    public static Nucleo.Spotify.Aparelho? AparelhoDesteCaixa(IEnumerable<Nucleo.Spotify.Aparelho> aparelhos)
        => Nucleo.Spotify.AparelhoPorNome(aparelhos, Environment.MachineName);
}
