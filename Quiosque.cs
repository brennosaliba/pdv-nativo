using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Pdv;

/// <summary>
/// MODO QUIOSQUE (12/09/2026, pedido do dono): "tem como adicionar o PDV como serviço,
/// iniciar no startup e aparecer somente ele no Windows? Nem carregar mais nada, nem
/// Spotify nem nada, somente o PDV."
///
/// Serviço não serve (serviço do Windows não tem tela). O que faz isso é o PDV virar o
/// SHELL do usuário do caixa: o Windows entra e, em vez da área de trabalho, da barra e
/// dos programas de inicialização, abre o PDV. É um valor no registro DO USUÁRIO
/// (HKCU\...\Winlogon\Shell), sem UAC, e vale na próxima entrada no Windows. Desligar
/// apaga o valor e o Windows volta ao normal.
///
/// O que o dono ainda precisa fazer uma vez: o Windows entrar sem pedir senha (netplwiz,
/// desmarcar "os usuários devem digitar..."). Senha é dele; o PDV não mexe.
///
/// Cuidados que moram aqui:
///  · Fechar o PDV como shell deixaria a tela preta: o Fechar do MainWindow oferece
///    "Reiniciar o PDV" ou "Sair para o Windows" (abre o Explorer).
///  · Se o PDV morrer (exceção fatal), o App abre o Explorer antes de cair.
///  · A atualização continua igual: o instalador troca o exe e reabre o PDV.
/// </summary>
public static class Quiosque
{
    private const string ChaveWinlogon = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    public const string Argumento = "--quiosque";

    /// <summary>O executável de verdade (no publish em arquivo único é o host, que é o próprio Pdv.exe).</summary>
    public static string ExeAtual => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Pdv.exe");

    /// <summary>O valor gravado em Shell: o exe entre aspas e a marca do modo.</summary>
    public static string ValorShell(string exe) => "\"" + exe + "\" " + Argumento;

    /// <summary>Esse valor de Shell é o nosso PDV (este exe)? Explorer, vazio ou outro programa: não.</summary>
    public static bool EhNosso(string? valorShell, string exe)
        => !string.IsNullOrWhiteSpace(valorShell) && !string.IsNullOrWhiteSpace(exe)
           && valorShell.Contains(exe, StringComparison.OrdinalIgnoreCase);

    public static bool ArgumentoPresente(IEnumerable<string> args) => args.Any(a => a == Argumento);

    public static bool Ligado
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(ChaveWinlogon);
                return EhNosso(k?.GetValue("Shell") as string, ExeAtual);
            }
            catch { return false; }
        }
    }

    /// <summary>Liga: grava o Shell do usuário atual. Devolve a mensagem de erro, ou null.</summary>
    public static string? Ligar()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(ChaveWinlogon);
            if (k is null) return "não consegui abrir a chave do Windows";
            k.SetValue("Shell", ValorShell(ExeAtual), RegistryValueKind.String);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Desliga: apaga o valor (o Windows volta ao Explorer). Só apaga se for o nosso.</summary>
    public static string? Desligar()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(ChaveWinlogon, writable: true);
            if (k is null) return null;
            if (EhNosso(k.GetValue("Shell") as string, ExeAtual)) k.DeleteValue("Shell", throwOnMissingValue: false);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Abre a área de trabalho do Windows (Explorer) por cima de nada: para sair do quiosque.</summary>
    public static void AbrirExplorer()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>
    /// Reabre o PDV depois que este processo sair. Espera 2 s (a trava de instância única
    /// e o PW_End da PGWebLib precisam do processo velho fora) e chama o exe de novo.
    /// </summary>
    public static void ReiniciarPdv()
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/c ping -n 3 127.0.0.1 >nul & start \"\" \"" + ExeAtual + "\" " + Argumento)
            {
                CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch { }
    }
}
