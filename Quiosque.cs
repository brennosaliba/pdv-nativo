using System.Diagnostics;
using System.IO;
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
///  · A atualização: o instalador troca o exe e quem reabre o PDV é um cmd comum que o próprio
///    PDV deixa esperando (Nucleo/ReabrirOCaixa), nunca o instalador, que roda como administrador.
/// </summary>
public static class Quiosque
{
    // 1.0.36: a chave, a marca e o formato do Shell moram no núcleo (DesfazerQuiosque), porque o
    // desinstalador lê o mesmo valor para desfazer o quiosque. Um formato só, nos dois lados.
    private const string ChaveWinlogon = DesfazerQuiosque.ChaveWinlogon;
    public const string Argumento = DesfazerQuiosque.MarcaDoQuiosque;

    /// <summary>O executável de verdade (no publish em arquivo único é o host, que é o próprio Pdv.exe).</summary>
    public static string ExeAtual => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Pdv.exe");

    /// <summary>O valor gravado em Shell: o exe entre aspas e a marca do modo.</summary>
    public static string ValorShell(string exe) => DesfazerQuiosque.ShellDoPdv(exe);

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
    // is denied" na tela). Agora: tenta direto (UAC desligado ou PDV já elevado) e, negado, pede
    // ao Windows para rodar como administrador só o cmd.exe de System32 com reg.exe (as quatro e a marca), que
    // gravam em HKEY_USERS\SID. O Pdv.exe NUNCA sobe como administrador (ele extrai bibliotecas
    // numa pasta que o usuário grava). A regra pura e a ordem moram em Nucleo/TravaDoCtrlAltDel.cs.

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
            // 1.0.36: trava é só um DWORD que ESCONDE a opção (diferente de 0). Um DWORD 0
            // (o que as ferramentas de "reativar o Gerenciador de Tarefas" deixam) ou um valor
            // de outro tipo não é a nossa trava: não conta como "alguma", senão todo Salvar
            // num PC assim pediria permissão ao Windows à toa só para apagar o que não é nosso.
            if (v is int iv && iv != 0) alguma = true;
            if (v is not int i || i != 1) todas = false;
        }
        return (todas, alguma);
    }

    /// <summary>
    /// O GRAVADOR: liga (cria a chave e grava DWORD 1) ou desliga (apaga o valor, sem criar chave
    /// vazia e sem abrir para escrita o que não tem nada para apagar) as quatro políticas e a marca
    /// da trava embaixo de <paramref name="raiz"/>, na ordem do núcleo (marca primeiro ao ligar, por
    /// último ao desligar). Lança em erro. A raiz vem de fora: HKCU (direto) ou a chave isolada da
    /// bateria. O caminho com administrador não passa por aqui: é o reg.exe do Windows.
    /// </summary>
    public static void GravarTravas(RegistryKey raiz, bool travar)
    {
        foreach (var (chave, valor) in TravaDoCtrlAltDel.NaOrdem(travar))
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
    /// A Configuração muda a trava: direto e, negado, pelo aviso do Windows (o cmd de System32
    /// como administrador). Só a partir do toque em Salvar, com a janela do PDV como dona do aviso:
    /// sem dono e fora do primeiro plano o Windows pode deixar o aviso piscando numa barra que o
    /// quiosque não tem. O resultado vem de reler o registro; o detalhe técnico é para a auditoria.
    /// </summary>
    public static (ResultadoDaTrava Resultado, string Detalhe) MudarTrava(bool travar, IntPtr janela)
    {
        // O SID vem da conta deste processo (nunca de arquivo nem de texto digitado) e a pasta do
        // sistema vem do Windows (GetSystemDirectory, não variável de ambiente). Os dois passam pelo
        // formato estrito do núcleo antes de virar linha de comando.
        var sid = SidDoProcesso();
        var sistema = Environment.SystemDirectory;
        var naoPede = TravaDoCtrlAltDel.MotivoParaNaoPedir(sid, sistema, File.Exists);
        var (resultado, detalhe) = TravaDoCtrlAltDel.Mudar(
            () => TentarDireto(travar),
            // relê SEM o catch dos getters Travado/AlgumaTrava: uma leitura que lança tem que
            // chegar ao Relido e virar "não ficou". Com o getter AlgumaTrava (catch => false),
            // um destravar cuja releitura falha daria !false = Ok sem ter confirmado nada.
            () => { var (todas, alguma) = LerTravas(Registry.CurrentUser); return travar ? todas : !alguma; },
            naoPede,
            () => PedirAoWindows(TravaDoCtrlAltDel.MontarComando(travar, sid!, sistema), sistema, janela));
        return (resultado, naoPede is null ? detalhe + " [" + TravaDoCtrlAltDel.CaminhoNoHku(sid!, TravaDoCtrlAltDel.ChaveSistema) + "]" : detalhe);
    }

    /// <summary>
    /// Pede ao Windows para rodar como administrador o cmd.exe de System32 com a linha do núcleo
    /// (reg.exe de System32 para as quatro políticas e a marca, só constantes e o SID) e espera até 20 s. O Não no aviso chega
    /// aqui como Win32Exception 1223 e sobe (TravaDoCtrlAltDel.Mudar transforma em Cancelou).
    /// Devolve só se o cmd terminou: o código de saída de um processo elevado não volta para quem
    /// pediu, e quem diz se deu certo é o registro relido.
    /// </summary>
    private static bool PedirAoWindows(string comando, string sistema, IntPtr janela)
    {
        // UseShellExecute + runas: é o que faz o Windows mostrar o aviso. ErrorDialog liga o
        // ErrorDialogParentHandle: o .NET só passa a janela dona ao Windows com ele ligado.
        // WorkingDirectory = System32: nada da pasta do PDV (que o usuário pode alcançar) entra.
        using var p = Process.Start(new ProcessStartInfo(TravaDoCtrlAltDel.CaminhoDoCmd(sistema))
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = comando,
            WorkingDirectory = sistema,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = janela != IntPtr.Zero,
            ErrorDialogParentHandle = janela,
        });
        return p is not null && p.WaitForExit(20000);
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

    /// <summary>Abre a área de trabalho do Windows (Explorer) por cima de nada: para sair do quiosque.</summary>
    public static void AbrirExplorer()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>
    /// Reabre o PDV depois que este processo sair. A trava de instância única e o PW_End da PGWebLib
    /// precisam do processo velho fora. Fechamento da 1.0.36: um cmd comum espera ESTE processo sair de
    /// verdade (a mesma espera da atualização, Nucleo/ReabrirOCaixa) e só então abre o exe. Antes eram
    /// 2 s fixos: um caixa que demorava mais para sair fazia o novo morrer na trava, e no quiosque ficava
    /// a tela preta. Sem como provar a espera, volta aos 2 s de antes.
    /// </summary>
    public static void ReiniciarPdv()
    {
        try
        {
            var sistema = Environment.SystemDirectory;
            var exe = ExeAtual;
            if (ReabrirOCaixa.MotivoParaNaoReabrir(exe, sistema, File.Exists,
                    () => ReabrirOCaixa.EsperaAcha(sistema, Environment.ProcessId, exe)) is null)
            {
                Process.Start(new ProcessStartInfo(TravaDoCtrlAltDel.CaminhoDoCmd(sistema),
                    ReabrirOCaixa.MontarReinicio(Environment.ProcessId, Path.GetFileName(exe), exe, quiosque: true, sistema))
                {
                    CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = sistema,
                })?.Dispose();
                return;
            }
            Process.Start(new ProcessStartInfo(TravaDoCtrlAltDel.CaminhoDoCmd(sistema),
                "/c ping -n 3 127.0.0.1 >nul & start \"\" \"" + exe + "\" " + Argumento)
            {
                CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
            })?.Dispose();
        }
        catch { }
    }
}
