using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Pdv.Nucleo;

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

    // ── A TRAVA DO CTRL+ALT+DEL (1.0.33, 14/10/2026, pedido do dono) ──────────
    // Nenhum programa bloqueia o Ctrl+Alt+Del em si (é a tecla segura do Windows). O que dá é
    // esvaziar a tela dele pelas políticas DO USUÁRIO: sem Gerenciador de Tarefas, sem Bloquear,
    // sem Trocar senha e sem Sair. Sobra o Cancelar (e o botão de desligar, para a loja poder
    // desligar o PC). Desligar o quiosque desfaz a trava. Saída de emergência: a Configuração do
    // PDV (senha de administrador) e o AnyDesk, que roda como serviço.
    //
    // 1.0.36 (14/10/2026, Savassi): a 1.0.33 dizia "HKCU, sem UAC" e estava ERRADO. A chave
    // Policies do usuário é protegida: o usuário só lê, e só o SYSTEM e os Administradores
    // ELEVADOS escrevem. Com o UAC ligado a trava nunca gravou ("Access to the registry key ...
    // is denied" na tela). Agora: tenta direto (UAC desligado ou PDV já elevado) e, negado, o
    // PDV chama a si mesmo como administrador (--travas on|off --sid SID), que grava em
    // HKEY_USERS\SID. A regra pura e a ordem moram em Nucleo/TravaDoCtrlAltDel.cs.

    /// <summary>(chave, valor) que a trava grava como 1 (a lista mora no núcleo).</summary>
    public static (string Chave, string Valor)[] Travas => TravaDoCtrlAltDel.Travas;

    /// <summary>A trava está gravada neste usuário (todas as quatro)?</summary>
    public static bool Travado
    {
        get
        {
            try { return LerTravas(Registry.CurrentUser).Todas; }
            catch { return false; }
        }
    }

    /// <summary>Alguma das quatro políticas está gravada? (para desfazer mesmo a trava pela metade)</summary>
    public static bool AlgumaTrava
    {
        get
        {
            try { return LerTravas(Registry.CurrentUser).Alguma; }
            catch { return false; }
        }
    }

    /// <summary>Lê as quatro políticas embaixo de uma raiz (o HKCU no caixa; a chave de teste na bateria).</summary>
    public static (bool Todas, bool Alguma) LerTravas(RegistryKey raiz)
    {
        bool todas = true, alguma = false;
        foreach (var (chave, valor) in Travas)
        {
            using var k = raiz.OpenSubKey(chave);
            var v = k?.GetValue(valor);
            if (v is not null) alguma = true;
            if (v is not int i || i != 1) todas = false;
        }
        return (todas, alguma);
    }

    /// <summary>
    /// O GRAVADOR: liga (cria a chave e grava DWORD 1) ou desliga (apaga o valor, sem criar chave
    /// vazia e sem abrir para escrita o que não tem nada para apagar) as quatro políticas embaixo
    /// de <paramref name="raiz"/>. Lança em erro. A raiz vem de fora: HKCU (direto), HKEY_USERS\SID
    /// (ajudante elevado) ou a chave isolada da bateria.
    /// </summary>
    public static void GravarTravas(RegistryKey raiz, bool travar)
    {
        foreach (var (chave, valor) in Travas)
        {
            if (travar)
            {
                using var k = raiz.CreateSubKey(chave, writable: true)
                    ?? throw new IOException("o Windows não abriu " + chave);
                k.SetValue(valor, 1, RegistryValueKind.DWord);
            }
            else
            {
                bool tem;
                using (var leitura = raiz.OpenSubKey(chave)) tem = leitura?.GetValue(valor) is not null;
                if (!tem) continue;
                using var k = raiz.OpenSubKey(chave, writable: true)
                    ?? throw new IOException("o Windows não abriu " + chave);
                k.DeleteValue(valor, throwOnMissingValue: false);
            }
        }
    }

    /// <summary>
    /// Tenta mudar a trava com a permissão que o PDV já tem (UAC desligado, ou PDV aberto como
    /// administrador). NUNCA abre o aviso do Windows: é o que a abertura do PDV usa. Devolve a
    /// exceção (o detalhe técnico vai só para a auditoria), ou null quando gravou.
    /// </summary>
    public static Exception? TentarDireto(bool travar)
    {
        try { GravarTravas(Registry.CurrentUser, travar); return null; }
        catch (Exception ex) { return ex; }
    }

    /// <summary>
    /// A Configuração muda a trava: direto e, negado, pelo aviso do Windows (o ajudante elevado).
    /// Só a partir do toque em Salvar, com a janela do PDV como dona do aviso: sem dono e fora do
    /// primeiro plano o Windows pode deixar o aviso piscando numa barra que o quiosque não tem.
    /// O resultado vem de reler o registro; o detalhe técnico é para a auditoria.
    /// </summary>
    public static (ResultadoDaTrava Resultado, string Detalhe) MudarTrava(bool travar, IntPtr janela)
    {
        var exe = ExeAtual;
        var sid = SidDoProcesso();
        string? naoEleva = !TravaDoCtrlAltDel.SidDeUsuario(sid) ? "a conta do Windows não é de pessoa (" + (sid ?? "sem SID") + ")"
            : !TravaDoCtrlAltDel.PodeElevar(exe, PastasDeProgramas()) ? "o PDV não está na pasta de programas (" + exe + ")"
            : null;
        var (resultado, detalhe) = TravaDoCtrlAltDel.Mudar(
            () => TentarDireto(travar),
            () => travar ? Travado : !AlgumaTrava,
            naoEleva,
            () => PedirAoWindows(exe, travar, sid!, janela));
        return (resultado, naoEleva is null ? detalhe + " [" + TravaDoCtrlAltDel.CaminhoNoHku(sid!, TravaDoCtrlAltDel.ChaveSistema) + "]" : detalhe);
    }

    /// <summary>As pastas que só o administrador troca (Arquivos de Programas, 64 e 32 bits).</summary>
    private static IEnumerable<string?> PastasDeProgramas()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    }

    /// <summary>
    /// Chama este mesmo exe como administrador e espera ele gravar. O Não no aviso chega aqui como
    /// Win32Exception 1223 e sobe (TravaDoCtrlAltDel.Mudar transforma em Cancelou). Devolve o
    /// código de saída, ou null se o ajudante não terminou em 20 s.
    /// </summary>
    private static int? PedirAoWindows(string exe, bool travar, string sid, IntPtr janela)
    {
        // UseShellExecute + runas: é o que faz o Windows mostrar o aviso (mesmo molde do Atualizar,
        // AtualizarCaixa.EntregarAoInstalador). ErrorDialog liga o ErrorDialogParentHandle: o .NET
        // só passa a janela dona ao Windows com ele ligado.
        using var p = Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = TravaDoCtrlAltDel.MontarArgumentos(travar, sid),
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            ErrorDialog = janela != IntPtr.Zero,
            ErrorDialogParentHandle = janela,
        });
        if (p is null) return null;
        if (!p.WaitForExit(20000)) return null;
        try { return p.ExitCode; }
        catch { return null; }
    }

    /// <summary>
    /// O AJUDANTE (Pdv.exe --travas on|off --sid SID, rodando como administrador). Grava só as
    /// quatro políticas, só no registro de quem pediu (HKEY_USERS\SID), e sai. Não lê arquivo, não
    /// abre banco, não abre janela. Códigos de saída em TravaDoCtrlAltDel.Saida*.
    /// </summary>
    public static int RodarAjudante(IReadOnlyList<string> args)
        => RodarAjudante(args, sid => Registry.Users.OpenSubKey(sid, writable: true), SidDoProcesso(), SidDaSessao());

    /// <summary>
    /// O ajudante com a raiz e as identidades de fora (a bateria usa uma chave isolada).
    /// <paramref name="abrirRaiz"/> recebe o SID já conferido e devolve a raiz aberta para escrita
    /// (writable é obrigatório: o CreateSubKey do .NET recusa raiz aberta só para leitura), ou null
    /// quando o registro do usuário não está carregado (ele não está logado).
    /// </summary>
    public static int RodarAjudante(IReadOnlyList<string> args, Func<string, RegistryKey?> abrirRaiz,
        string? sidDoProcesso, string? sidDaSessao)
    {
        if (LinhaDeComando.LerTravas(args) is not { } pedido) return TravaDoCtrlAltDel.SaidaArgumento;
        if (!TravaDoCtrlAltDel.SidAceito(pedido.Sid, sidDoProcesso, sidDaSessao)) return TravaDoCtrlAltDel.SaidaOutraPessoa;
        try
        {
            using var raiz = abrirRaiz(pedido.Sid);
            if (raiz is null) return TravaDoCtrlAltDel.SaidaUsuarioFora;
            GravarTravas(raiz, pedido.Travar);
            return TravaDoCtrlAltDel.SaidaOk;
        }
        catch (Exception ex) when (TravaDoCtrlAltDel.AcessoNegado(ex)) { return TravaDoCtrlAltDel.SaidaSemAdministrador; }
        catch { return TravaDoCtrlAltDel.SaidaErro; }
    }

    /// <summary>O SID da conta deste processo (S-1-5-21-...), ou null.</summary>
    private static string? SidDoProcesso()
    {
        try
        {
            using var eu = WindowsIdentity.GetCurrent();
            return eu.User?.Value;
        }
        catch { return null; }
    }

    /// <summary>
    /// O SID de quem está logado na sessão do Windows deste processo. Com usuário comum, o ajudante
    /// roda como o administrador que digitou a senha, mas na MESMA sessão de quem pediu: é por aqui
    /// que ele confere que o SID do argumento é o de quem está no caixa.
    /// </summary>
    private static string? SidDaSessao()
    {
        try
        {
            int sessao;
            using (var eu = Process.GetCurrentProcess()) sessao = eu.SessionId;
            var usuario = InfoDaSessao(sessao, WtsUserName);
            if (string.IsNullOrEmpty(usuario)) return null;
            var dominio = InfoDaSessao(sessao, WtsDomainName);
            var conta = string.IsNullOrEmpty(dominio) ? new NTAccount(usuario) : new NTAccount(dominio, usuario);
            return ((SecurityIdentifier)conta.Translate(typeof(SecurityIdentifier))).Value;
        }
        catch { return null; }
    }

    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    private static string? InfoDaSessao(int sessao, int classe)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessao, classe, out var buf, out _)) return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { WTSFreeMemory(buf); }
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr servidor, int sessao, int classe, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memoria);

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
