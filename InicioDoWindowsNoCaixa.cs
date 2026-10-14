using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Pdv.Nucleo;

namespace Pdv;

/// <summary>
/// Lê os programas da inicialização deste Windows e, no MODO QUIOSQUE, abre os marcados junto
/// com o PDV (14/10/2026, pedido do dono: Spotify e AnyDesk). A regra mora no Núcleo
/// (<see cref="InicioDoWindows"/>); aqui só o registro, as pastas e o Process.Start. Nunca lança.
/// </summary>
public static class InicioDoWindowsNoCaixa
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Aprovado = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    /// <summary>O que o Windows abriria ao entrar (só o que está ligado no Gerenciador de Tarefas), mais o AnyDesk instalado.</summary>
    public static IReadOnlyList<ProgramaDoInicio> Ler()
    {
        var lista = new List<ProgramaDoInicio>();
        try
        {
            LerRun(lista, Registry.CurrentUser, Run, "Run");
            LerRun(lista, Registry.LocalMachine, Run, "Run");
            LerRun(lista, Registry.LocalMachine, Run32, "Run32");
            LerPasta(lista, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser);
            LerPasta(lista, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine);
            // o AnyDesk instalado costuma rodar como serviço, sem entrada na inicialização: entra na
            // lista para a pessoa poder marcar (e abre a janelinha dele junto com o caixa)
            foreach (var pasta in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            {
                if (string.IsNullOrWhiteSpace(pasta)) continue;
                var any = Path.Combine(pasta, "AnyDesk", "AnyDesk.exe");
                if (File.Exists(any) && !lista.Any(p => InicioDoWindows.Processo(p.Exe) == "anydesk"))
                    lista.Add(new ProgramaDoInicio("AnyDesk", any, "--tray", "instalado"));   // sem janela por cima do caixa
            }
        }
        catch (Exception ex) { Anotar("ler: " + ex.GetType().Name); }
        return InicioDoWindows.Unicos(lista, Quiosque.ExeAtual);
    }

    private static void LerRun(List<ProgramaDoInicio> lista, RegistryKey raiz, string caminho, string aprovadoEm)
    {
        try
        {
            using var k = raiz.OpenSubKey(caminho);
            if (k is null) return;
            using var a = raiz.OpenSubKey(Aprovado + aprovadoEm);
            foreach (var nome in k.GetValueNames())
            {
                if (InicioDoWindows.DesligadoNoGerenciador(a?.GetValue(nome) as byte[])) continue;
                var sep = InicioDoWindows.Separar(k.GetValue(nome) as string, Environment.ExpandEnvironmentVariables);
                if (sep is { } s) lista.Add(new ProgramaDoInicio(nome, s.Exe, s.Argumentos, "run"));
            }
        }
        catch (Exception ex) { Anotar($"run {caminho}: " + ex.GetType().Name); }
    }

    private static void LerPasta(List<ProgramaDoInicio> lista, string pasta, RegistryKey raiz)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pasta) || !Directory.Exists(pasta)) return;
            using var a = raiz.OpenSubKey(Aprovado + "StartupFolder");
            foreach (var arq in Directory.EnumerateFiles(pasta))
            {
                var ext = Path.GetExtension(arq).ToLowerInvariant();
                if (ext is not (".lnk" or ".exe" or ".url")) continue;
                var nome = Path.GetFileName(arq);
                if (InicioDoWindows.DesligadoNoGerenciador(a?.GetValue(nome) as byte[])) continue;
                lista.Add(new ProgramaDoInicio(Path.GetFileNameWithoutExtension(arq), arq, "", "pasta"));
            }
        }
        catch (Exception ex) { Anotar("pasta: " + ex.GetType().Name); }
    }

    /// <summary>
    /// No quiosque, abre os marcados que ainda não estão abertos, um a cada 2 s, depois de
    /// <paramref name="espera"/> (o Spotify escondido abre primeiro). Fora do quiosque não faz nada.
    /// </summary>
    public static void AbrirNoQuiosque(string? ligado, string? marcados, TimeSpan espera)
    {
        try
        {
            if (!Quiosque.Ligado) return;
            if (!InicioDoWindows.Ligado(ligado)) { Anotar("desligado na Configuração"); return; }
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(espera);
                    // só os processos DESTA sessão: o AnyDesk de serviço (sessão 0) não é a janelinha dele
                    var sessao = Process.GetCurrentProcess().SessionId;
                    var rodando = new HashSet<string>(Process.GetProcesses()
                        .Where(p => { try { return p.SessionId == sessao; } catch { return false; } })
                        .Select(p => { try { return p.ProcessName.ToLowerInvariant(); } catch { return ""; } }));
                    var abrir = InicioDoWindows.AAbrir(Ler(), marcados, rodando, Quiosque.ExeAtual);
                    Anotar($"abrir: {(abrir.Count == 0 ? "nada" : string.Join(", ", abrir.Select(p => p.Nome)))}");
                    foreach (var p in abrir)
                    {
                        try
                        {
                            var psi = new ProcessStartInfo(p.Exe, p.Argumentos) { UseShellExecute = true };
                            var dir = Path.GetDirectoryName(p.Exe);
                            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
                            Process.Start(psi);
                        }
                        catch (Exception ex) { Anotar($"{p.Nome}: {ex.GetType().Name}"); }
                        await Task.Delay(TimeSpan.FromSeconds(2));
                    }
                }
                catch (Exception ex) { Anotar("abrir: " + ex.GetType().Name); }
            });
        }
        catch (Exception ex) { Anotar("abrir: " + ex.GetType().Name); }
    }

    /// <summary>Uma linha datada em ProgramData\PdvNativo\inicio-windows.txt (nomes de programa, nada mais).</summary>
    private static void Anotar(string texto)
    {
        try
        {
            var pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PdvNativo");
            Directory.CreateDirectory(pasta);
            var arq = Path.Combine(pasta, "inicio-windows.txt");
            if (File.Exists(arq) && new FileInfo(arq).Length > 200_000) File.Delete(arq);
            File.AppendAllText(arq, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {texto}{Environment.NewLine}");
        }
        catch { /* diagnóstico nunca derruba nada */ }
    }
}
