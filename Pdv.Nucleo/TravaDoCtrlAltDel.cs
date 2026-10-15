using System.ComponentModel;
using System.Globalization;
using System.Security;
using System.Text.RegularExpressions;

namespace Pdv.Nucleo;

/// <summary>Como terminou o pedido de mudar a trava do Ctrl+Alt+Del.</summary>
public enum ResultadoDaTrava
{
    /// <summary>O Windows ficou como o dono pediu (relido do registro).</summary>
    Ok,
    /// <summary>Responderam Não no aviso do Windows (erro 1223).</summary>
    Cancelou,
    /// <summary>Não deu por outro motivo. O detalhe técnico vai só para a auditoria.</summary>
    Falhou,
}

/// <summary>
/// A TRAVA DO CTRL+ALT+DEL PRECISA DO ADMINISTRADOR DO WINDOWS (14/10/2026, Savassi, caixa 1.0.34).
///
/// O dono marcou a trava e viu "Não consegui gravar no Windows: Access to the registry key
/// 'HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\...' is denied". A 1.0.33
/// supunha que as políticas DO USUÁRIO gravavam sem UAC. Não gravam: a chave Policies do HKCU é
/// protegida (não herda de CurrentVersion), o usuário só lê, e só o SYSTEM e os Administradores
/// ELEVADOS escrevem nela. O quiosque (Winlogon\Shell) grava sem UAC porque aquela chave o usuário
/// controla. Com o UAC ligado (o padrão do Windows), a trava nunca funcionou.
///
/// O desenho: o PDV tenta direto (vale com o UAC desligado ou com o PDV já elevado). Negado, ele
/// pede ao Windows para rodar como administrador UM programa do próprio Windows: o cmd.exe de
/// System32, com quatro reg.exe (de System32) que gravam ou apagam as quatro políticas em
/// HKEY_USERS\SID. Um aviso só. HKEY_USERS\SID e não HKCU: com usuário comum quem digita a senha é
/// OUTRA conta, e o HKCU do processo elevado seria o dela. O resultado vem de reler o registro: o
/// código de saída de um processo elevado não chega de volta a quem pediu.
///
/// POR QUE NÃO O PRÓPRIO Pdv.exe (revisão de segurança da 1.0.36, antes de ir para a loja). A
/// primeira versão relançava o Pdv.exe como administrador (--travas on|off --sid SID). O Pdv.exe é
/// publicado em arquivo único e extrai bibliotecas nativas numa pasta que o usuário grava
/// (%TEMP%\.net). Elevar o Pdv.exe era dar administrador a código que o usuário comum consegue
/// plantar. Agora o que sobe é só programa do Windows, por caminho absoluto de System32
/// (Environment.SystemDirectory), com a linha de comando montada só de constantes e do SID (formato
/// estrito, vindo da conta do processo, nunca de arquivo nem de texto digitado). O Pdv.exe não tem
/// mais modo de administrador nenhum.
///
/// Aqui mora só a regra pura (a bateria prova cada pedaço): o SID, a pasta do sistema, a linha do
/// cmd, quando pedir permissão, a ordem das tentativas e o texto da tela. O registro e o processo
/// de verdade ficam em Quiosque.cs.
/// </summary>
public static class TravaDoCtrlAltDel
{
    public const string ChaveSistema = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    public const string ChaveExplorer = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    /// <summary>(chave, valor) que a trava grava como DWORD 1. São só estes quatro, e a linha do cmd não conhece outros.</summary>
    public static readonly (string Chave, string Valor)[] Travas =
    {
        (ChaveSistema, "DisableTaskMgr"), (ChaveSistema, "DisableLockWorkstation"),
        (ChaveSistema, "DisableChangePassword"), (ChaveExplorer, "NoLogoff"),
    };

    /// <summary>ERROR_CANCELLED do Windows: o Não no aviso do UAC.</summary>
    public const int ErroCancelado = 1223;

    /// <summary>Os dois programas que sobem como administrador. Só estes, e só de System32.</summary>
    public const string ProgramaCmd = "cmd.exe";
    public const string ProgramaReg = "reg.exe";

    /// <summary>
    /// As chaves do cmd: /d desliga o AutoRun (HKCU\Software\Microsoft\Command Processor\AutoRun:
    /// o usuário grava ali, e sem o /d um comando dele rodaria junto, como administrador); /v:off
    /// desliga a expansão atrasada (o !nome! que o registro do usuário também pode ligar); /s /c roda
    /// o que está entre as aspas de fora, como está.
    /// </summary>
    public const string ChavesDoCmd = "/d /v:off /s /c";

    // SID de CONTA DE PESSOA: S-1-5-21-a-b-c-d (conta local ou de domínio) ou S-1-12-1-a-b-c-d
    // (conta Microsoft Entra). Nada de S-1-5-18 (SYSTEM), S-1-5-32-544 (grupo Administradores),
    // ".DEFAULT" nem "SID_Classes": só o registro de uma pessoa.
    // [0-9] e não \d (\d aceita dígito árabe); \z e não $ ($ aceita um \n no fim); sem zero à esquerda.
    private static readonly Regex FormatoSid = new(
        @"^S-1-(?:5-21|12-1)-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})\z",
        RegexOptions.CultureInvariant);

    /// <summary>É o SID de uma conta de pessoa, no formato exato que o Windows escreve?</summary>
    public static bool SidDeUsuario(string? sid)
    {
        if (sid is null) return false;
        var m = FormatoSid.Match(sid);
        if (!m.Success) return false;
        for (var i = 1; i <= 4; i++)
            if (!uint.TryParse(m.Groups[i].Value, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return false;
        return true;
    }

    // A pasta do sistema como o Windows devolve (C:\WINDOWS\system32): disco local, UMA pasta (a do
    // Windows) e system32 ou SysWOW64. Sem espaço, sem aspas, sem %, &, ^, |, <, > ou !, sem "..":
    // nada que o cmd interprete e nada fora da pasta do Windows (um ...\Temp\system32 não passa).
    private static readonly Regex FormatoPastaDoSistema = new(
        @"^[A-Za-z]:\\[A-Za-z0-9_][A-Za-z0-9_.\-]*\\(?:system32|SysWOW64)\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>É a pasta do sistema do Windows, num formato que pode entrar na linha do cmd?</summary>
    public static bool PastaDoSistema(string? pasta) => pasta is not null && FormatoPastaDoSistema.IsMatch(pasta);

    /// <summary>O caminho absoluto do cmd.exe que o Windows eleva. Pasta fora do formato: lança.</summary>
    public static string CaminhoDoCmd(string pastaDoSistema)
    {
        if (!PastaDoSistema(pastaDoSistema)) throw new ArgumentException("pasta do sistema fora do formato", nameof(pastaDoSistema));
        return pastaDoSistema + @"\" + ProgramaCmd;
    }

    /// <summary>
    /// A LINHA DO CMD ELEVADO, só de constantes e do SID:
    /// <c>/d /v:off /s /c ""C:\WINDOWS\system32\reg.exe" add "HKU\SID\...\Policies\System" /v DisableTaskMgr /t REG_DWORD /d 1 /f &amp; ..."</c>
    /// (ou <c>delete ... /v Valor /f</c> para desligar). Os quatro com <c>&amp;</c>, não <c>&amp;&amp;</c>:
    /// ao desligar a trava pela metade, o delete do valor que não existe falha e os outros seguem.
    /// SID ou pasta fora do formato: lança, e nada sobe como administrador.
    /// </summary>
    public static string MontarComando(bool travar, string sid, string pastaDoSistema)
    {
        if (!SidDeUsuario(sid)) throw new ArgumentException("SID fora do formato de conta de pessoa", nameof(sid));
        if (!PastaDoSistema(pastaDoSistema)) throw new ArgumentException("pasta do sistema fora do formato", nameof(pastaDoSistema));
        var reg = "\"" + pastaDoSistema + @"\" + ProgramaReg + "\"";
        var partes = Travas.Select(t => travar
            ? reg + " add \"HKU\\" + sid + "\\" + t.Chave + "\" /v " + t.Valor + " /t REG_DWORD /d 1 /f"
            : reg + " delete \"HKU\\" + sid + "\\" + t.Chave + "\" /v " + t.Valor + " /f");
        var comando = ChavesDoCmd + " \"" + string.Join(" & ", partes) + "\"";
        // cinto e suspensório: o que sai daqui só tem o que as constantes e o SID têm
        if (!ComandoLimpo(comando)) throw new InvalidOperationException("a linha do cmd saiu com caractere fora do esperado");
        return comando;
    }

    /// <summary>
    /// A linha só tem letra, número, espaço, \ : . " / _ - e o &amp; que separa os reg. Nada de %
    /// (variável), ^ (escape), |, &lt;, &gt; (redirecionamento), ! (expansão) nem quebra de linha.
    /// </summary>
    public static bool ComandoLimpo(string? comando)
        => comando is not null && Regex.IsMatch(comando, @"^[A-Za-z0-9 \\:.""/_&\-]+\z", RegexOptions.CultureInvariant);

    /// <summary>O caminho completo de uma chave da trava no registro do usuário que pediu.</summary>
    public static string CaminhoNoHku(string sid, string chave)
    {
        if (!SidDeUsuario(sid)) throw new ArgumentException("SID fora do formato de conta de pessoa", nameof(sid));
        return @"HKEY_USERS\" + sid + @"\" + chave;
    }

    /// <summary>
    /// Por que NÃO pedir ao Windows (null = pode pedir): a conta do processo não é de pessoa, a pasta
    /// do sistema está fora do formato, ou o cmd.exe e o reg.exe não estão nela.
    /// </summary>
    public static string? MotivoParaNaoPedir(string? sid, string? pastaDoSistema, Func<string, bool> existe)
    {
        if (!SidDeUsuario(sid)) return "a conta do Windows não é de pessoa (" + (sid ?? "sem SID") + ")";
        if (!PastaDoSistema(pastaDoSistema)) return "pasta do sistema fora do esperado (" + (pastaDoSistema ?? "nenhuma") + ")";
        bool tem;
        try { tem = existe(pastaDoSistema + @"\" + ProgramaCmd) && existe(pastaDoSistema + @"\" + ProgramaReg); }
        catch { tem = false; }
        return tem ? null : "cmd.exe ou reg.exe não estão em " + pastaDoSistema;
    }

    /// <summary>A Configuração precisa mexer? Desmarcado sempre destrava, até a trava pela metade.</summary>
    public static bool PrecisaMudar(bool querTravar, bool travadoAgora, bool algumaTrava)
        => querTravar != travadoAgora || (!querTravar && algumaTrava);

    /// <summary>
    /// Pedir permissão ao Windows só resolve ACESSO NEGADO. Outro erro (chave corrompida, disco)
    /// não melhora com administrador, e o aviso do Windows à toa só confunde quem está no caixa.
    /// </summary>
    public static bool AcessoNegado(Exception? ex) => ex is UnauthorizedAccessException or SecurityException;

    /// <summary>O Não no aviso do Windows chega como Win32Exception 1223. Não é falha: é a resposta.</summary>
    public static bool Cancelado(Exception? ex) => ex is Win32Exception { NativeErrorCode: ErroCancelado };

    /// <summary>
    /// A ORDEM, sem registro nem processo (a bateria troca cada passo por um falso):
    ///  1. tenta direto; deu e ficou como quer: pronto, sem aviso do Windows;
    ///  2. só ACESSO NEGADO segue para o administrador, e só se dá para pedir
    ///     (<paramref name="motivoParaNaoPedir"/> nulo);
    ///  3. Não no aviso (1223): Cancelou;
    ///  4. o resultado é o registro RELIDO. <paramref name="pedirAoWindows"/> devolve só se o cmd
    ///     terminou em 20 s, e isso vai para o detalhe: o código de saída de um processo elevado não
    ///     volta para quem pediu, e mesmo que voltasse, quem manda é o registro.
    /// </summary>
    public static (ResultadoDaTrava Resultado, string Detalhe) Mudar(
        Func<Exception?> tentarDireto, Func<bool> ficouComoQuer, string? motivoParaNaoPedir, Func<bool> pedirAoWindows)
    {
        Exception? direto;
        try { direto = tentarDireto(); }
        catch (Exception ex) { direto = ex; }
        if (direto is null)
            return Relido(ficouComoQuer)
                ? (ResultadoDaTrava.Ok, "direto, sem aviso do Windows")
                : (ResultadoDaTrava.Falhou, "gravou direto, mas o Windows não ficou como pedido");

        var detalhe = "direto: " + Tecnico(direto);
        if (!AcessoNegado(direto)) return (ResultadoDaTrava.Falhou, detalhe);
        if (motivoParaNaoPedir is not null) return (ResultadoDaTrava.Falhou, detalhe + "; sem pedir ao Windows: " + motivoParaNaoPedir);

        bool terminou;
        try { terminou = pedirAoWindows(); }
        catch (Exception ex) when (Cancelado(ex))
        {
            return (ResultadoDaTrava.Cancelou, detalhe + "; o aviso do Windows foi respondido com Não");
        }
        catch (Exception ex) { return (ResultadoDaTrava.Falhou, detalhe + "; pedido ao Windows: " + Tecnico(ex)); }

        detalhe += "; cmd como administrador: " + (terminou ? "terminou" : "não terminou em 20 s");
        return (Relido(ficouComoQuer) ? ResultadoDaTrava.Ok : ResultadoDaTrava.Falhou, detalhe);
    }

    /// <summary>Reler o registro nunca derruba o Salvar: leitura que lança conta como "não ficou".</summary>
    private static bool Relido(Func<bool> ficouComoQuer)
    {
        try { return ficouComoQuer(); }
        catch { return false; }
    }

    /// <summary>O texto técnico de uma exceção, para a auditoria (nunca para a tela).</summary>
    public static string Tecnico(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    /// <summary>A frase da tela. Uma linha, sem termo técnico e sem nada em inglês.</summary>
    public static string Frase(ResultadoDaTrava resultado, bool travar) => resultado switch
    {
        ResultadoDaTrava.Ok => travar ? "A trava do Ctrl+Alt+Del foi ligada." : "A trava do Ctrl+Alt+Del foi desligada.",
        ResultadoDaTrava.Cancelou => travar
            ? "Sem a permissão do Windows, a trava do Ctrl+Alt+Del não foi ligada. O resto foi salvo."
            : "Sem a permissão do Windows, a trava do Ctrl+Alt+Del continua ligada. O resto foi salvo.",
        _ => "Não consegui mudar a trava do Ctrl+Alt+Del. O resto foi salvo.",
    };
}
