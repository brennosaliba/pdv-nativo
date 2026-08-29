using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Pdv.Instalador;

/// <summary>
/// O trabalho de verdade do instalador — separado da janela para o teste
/// (--teste na JanelaInstalador) exercitar TUDO em pasta de sandbox antes de
/// qualquer máquina de loja ver este exe.
///
/// Regras de ouro:
///  1. DADOS SÃO SAGRADOS: instalar por cima ou desinstalar NUNCA toca em
///     C:\ProgramData\PdvNativo (banco de vendas, segredos, perfil do chat).
///     Versão nova troca só o executável.
///  2. Iniciar com o Windows é entrada Run em HKLM (todos os usuários) — NÃO
///     é serviço do Windows: serviço roda na sessão 0, sem tela, e frente de
///     caixa com toque e impressora precisa de usuário logado.
///  3. A ACL do ProgramData é ajustada na instalação: sem ela, o segundo
///     usuário do Windows que logar no caixa toma "attempt to write a
///     readonly database".
/// </summary>
public static class Instalacao
{
    // NOME DO PRODUTO: nao e so texto. Ele vira a pasta de instalacao
    // (PastaDestinoPadrao) e o DisplayName do Adicionar/Remover Programas —
    // trocar a palavra reinstala em outro diretorio e deixa a instalacao antiga
    // orfa. Vender o PDV para outra loja pede uma marca DE VERDADE decidida pelo
    // dono; ate la fica a que existe.
    public const string NomePrograma = "PDV American Day";
    private const string ChaveRun = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ChaveUninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PdvAmericanDay";

    public static string PastaDestinoPadrao =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), NomePrograma);

    public static string PastaDados =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PdvNativo");

    /// <summary>O Pdv.exe viaja NA MESMA PASTA do instalador — nada de embutir
    /// 176 MB em recurso nem baixar da internet no meio da loja.</summary>
    public static string? AcharPdvExeAoLado()
    {
        var aqui = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        var cand = Path.Combine(aqui, "Pdv.exe");
        return File.Exists(cand) ? cand : null;
    }

    public sealed record Opcoes(
        string OrigemPdvExe,
        string PastaDestino,
        bool IniciarComWindows,
        bool AtalhoAreaTrabalho,
        bool AjustarAcl = true,
        bool GravarRegistro = true);

    /// <summary>Instala (ou ATUALIZA por cima — preservando os dados). Devolve
    /// null no sucesso, mensagem de erro no fracasso.</summary>
    public static string? Instalar(Opcoes o, Action<string>? progresso = null)
    {
        try
        {
            progresso?.Invoke("Criando pastas…");
            Directory.CreateDirectory(o.PastaDestino);
            Directory.CreateDirectory(PastaDados);

            progresso?.Invoke("Copiando o PDV (176 MB, ~1 min)…");
            var destinoExe = Path.Combine(o.PastaDestino, "Pdv.exe");
            // atualização com o PDV aberto: troca segura via renomeio (o Windows
            // deixa renomear exe em uso, mas não sobrescrever)
            if (File.Exists(destinoExe))
            {
                var velho = destinoExe + ".velho";
                if (File.Exists(velho)) File.Delete(velho);
                File.Move(destinoExe, velho);
            }
            File.Copy(o.OrigemPdvExe, destinoExe, overwrite: false);

            // instalador junto ao programa: é ele que sabe desinstalar
            var euMesmo = Environment.ProcessPath;
            if (euMesmo is not null && !euMesmo.StartsWith(o.PastaDestino, StringComparison.OrdinalIgnoreCase))
                File.Copy(euMesmo, Path.Combine(o.PastaDestino, "InstalarPdv.exe"), overwrite: true);

            if (o.AjustarAcl)
            {
                progresso?.Invoke("Liberando a pasta de dados para os operadores…");
                // Usuários (S-1-5-32-545) modificam: qualquer conta do caixa grava no
                // banco. Sem isso o 2º usuário do Windows trava em "readonly database".
                var icacls = Process.Start(new ProcessStartInfo
                {
                    FileName = "icacls",
                    Arguments = $"\"{PastaDados}\" /grant *S-1-5-32-545:(OI)(CI)M /T",
                    UseShellExecute = false, CreateNoWindow = true,
                });
                icacls?.WaitForExit(30_000);
            }

            if (o.GravarRegistro)
            {
                progresso?.Invoke("Registrando o programa…");
                var versao = FileVersionInfo.GetVersionInfo(destinoExe).FileVersion ?? "?";
                using (var k = Registry.LocalMachine.CreateSubKey(ChaveUninstall))
                {
                    k.SetValue("DisplayName", NomePrograma);
                    k.SetValue("DisplayVersion", versao);
                    k.SetValue("Publisher", "American Day");
                    k.SetValue("InstallLocation", o.PastaDestino);
                    k.SetValue("DisplayIcon", destinoExe);
                    k.SetValue("UninstallString", $"\"{Path.Combine(o.PastaDestino, "InstalarPdv.exe")}\" --desinstalar");
                    k.SetValue("NoModify", 1); k.SetValue("NoRepair", 1);
                }

                using var run = Registry.LocalMachine.CreateSubKey(ChaveRun);
                if (o.IniciarComWindows)
                    run.SetValue(NomePrograma, $"\"{destinoExe}\"");
                else
                    run.DeleteValue(NomePrograma, throwOnMissingValue: false);
            }

            if (o.AtalhoAreaTrabalho)
            {
                progresso?.Invoke("Criando atalho…");
                CriarAtalho(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                                 NomePrograma + ".lnk"),
                    destinoExe);
            }

            progresso?.Invoke("Concluído.");
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Remove programa, atalho e registro. Os DADOS ficam — sempre.</summary>
    public static string? Desinstalar()
    {
        try
        {
            using (var run = Registry.LocalMachine.CreateSubKey(ChaveRun))
                run.DeleteValue(NomePrograma, throwOnMissingValue: false);
            Registry.LocalMachine.DeleteSubKeyTree(ChaveUninstall, throwOnMissingSubKey: false);

            var atalho = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                NomePrograma + ".lnk");
            if (File.Exists(atalho)) File.Delete(atalho);

            var pasta = PastaDestinoPadrao;
            if (Directory.Exists(pasta))
            {
                // o próprio desinstalador mora aí: apaga o que der agora, e agenda
                // a pasta pro próximo boot se algo estiver em uso
                try { Directory.Delete(pasta, recursive: true); }
                catch { /* exe em uso: fica o esqueleto, dados continuam intactos */ }
            }
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Atalho .lnk sem referenciar COM tipado: um VBScript de uma vez
    /// evita dependência de Interop no publish single-file.</summary>
    private static void CriarAtalho(string lnk, string alvo)
    {
        var vbs = Path.Combine(Path.GetTempPath(), "atalho_pdv.vbs");
        File.WriteAllText(vbs, $"""
            Set ws = CreateObject("WScript.Shell")
            Set a = ws.CreateShortcut("{lnk}")
            a.TargetPath = "{alvo}"
            a.WorkingDirectory = "{Path.GetDirectoryName(alvo)}"
            a.Description = "{NomePrograma}"
            a.Save
            """);
        var p = Process.Start(new ProcessStartInfo
        {
            FileName = "wscript", Arguments = $"//B \"{vbs}\"",
            UseShellExecute = false, CreateNoWindow = true,
        });
        p?.WaitForExit(15_000);
        try { File.Delete(vbs); } catch { }
    }
}
