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
/// chama a si mesmo como administrador (<c>Pdv.exe --travas on|off --sid SID</c>), o Windows mostra
/// o aviso, e o ajudante grava em HKEY_USERS\SID, não no HKCU dele: com usuário comum quem digita a
/// senha é OUTRA conta, e o HKCU do processo elevado seria o dela. O resultado vem de reler o
/// registro, não do código de saída do ajudante.
///
/// Aqui mora só a regra pura (a bateria prova cada pedaço): o argumento, o SID, quando pedir
/// permissão, a ordem das tentativas e o texto da tela. O registro de verdade fica em Quiosque.cs.
/// </summary>
public static class TravaDoCtrlAltDel
{
    public const string Argumento = "--travas";
    public const string ArgumentoSid = "--sid";

    public const string ChaveSistema = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    public const string ChaveExplorer = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    /// <summary>(chave, valor) que a trava grava como DWORD 1. São só estes quatro, e o ajudante não aceita outros.</summary>
    public static readonly (string Chave, string Valor)[] Travas =
    {
        (ChaveSistema, "DisableTaskMgr"), (ChaveSistema, "DisableLockWorkstation"),
        (ChaveSistema, "DisableChangePassword"), (ChaveExplorer, "NoLogoff"),
    };

    // ── Códigos de saída do ajudante (só vão para o detalhe da auditoria) ────────
    public const int SaidaOk = 0;
    public const int SaidaArgumento = 2;
    public const int SaidaUsuarioFora = 3;
    public const int SaidaErro = 4;
    public const int SaidaSemAdministrador = 5;
    public const int SaidaOutraPessoa = 6;

    /// <summary>ERROR_CANCELLED do Windows: o Não no aviso do UAC.</summary>
    public const int ErroCancelado = 1223;

    // SID de CONTA DE PESSOA: S-1-5-21-a-b-c-d (conta local ou de domínio) ou S-1-12-1-a-b-c-d
    // (conta Microsoft Entra). Nada de S-1-5-18 (SYSTEM), S-1-5-32-544 (grupo Administradores),
    // ".DEFAULT" nem "SID_Classes": o ajudante só mexe no registro de uma pessoa.
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

    /// <summary>A linha de comando do ajudante. SID fora do formato não vira argumento.</summary>
    public static string MontarArgumentos(bool travar, string sid)
    {
        if (!SidDeUsuario(sid)) throw new ArgumentException("SID fora do formato de conta de pessoa", nameof(sid));
        return Argumento + " " + (travar ? "on" : "off") + " " + ArgumentoSid + " " + sid;
    }

    /// <summary>
    /// Lê exatamente <c>--travas on|off --sid SID</c>. Qualquer outra coisa (sem SID, SID de
    /// sistema, caminho escondido no SID, argumento a mais, ordem trocada) devolve null.
    /// </summary>
    public static (bool Travar, string Sid)? LerArgumentos(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count != 4) return null;
        if (args[0] != Argumento || args[2] != ArgumentoSid) return null;
        bool travar;
        if (args[1] == "on") travar = true;
        else if (args[1] == "off") travar = false;
        else return null;
        return SidDeUsuario(args[3]) ? (travar, args[3]) : null;
    }

    /// <summary>O caminho completo de uma chave da trava no registro do usuário que pediu.</summary>
    public static string CaminhoNoHku(string sid, string chave)
    {
        if (!SidDeUsuario(sid)) throw new ArgumentException("SID fora do formato de conta de pessoa", nameof(sid));
        return @"HKEY_USERS\" + sid + @"\" + chave;
    }

    /// <summary>
    /// O ajudante só mexe no registro de quem pediu. O SID do argumento tem que ser o da conta do
    /// próprio ajudante (administrador que elevou a si mesmo) ou o de quem está logado nesta sessão
    /// do Windows (usuário comum que chamou o administrador para digitar a senha). SID de outra
    /// pessoa: recusado, mesmo com administrador.
    /// </summary>
    public static bool SidAceito(string? sidPedido, string? sidDoProcesso, string? sidDaSessao)
        => SidDeUsuario(sidPedido) && (Igual(sidPedido!, sidDoProcesso) || Igual(sidPedido!, sidDaSessao));

    private static bool Igual(string a, string? b) => b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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
    /// Só eleva um exe que mora numa pasta que só o administrador troca (Arquivos de Programas).
    /// Fora de lá, elevar seria dar administrador a um arquivo que o próprio usuário pode trocar.
    /// </summary>
    public static bool PodeElevar(string? exe, IEnumerable<string?> pastasProtegidas)
    {
        if (string.IsNullOrWhiteSpace(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        string cheio;
        try { cheio = Path.GetFullPath(exe); }
        catch { return false; }
        // caminho relativo ou com ".." não passa: o que se eleva é exatamente o que está escrito
        if (!string.Equals(cheio, exe, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var p in pastasProtegidas)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            string raiz;
            try { raiz = Path.GetFullPath(p).TrimEnd('\\', '/') + "\\"; }
            catch { continue; }
            if (raiz.Length > 3 && cheio.StartsWith(raiz, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// A ORDEM, sem registro nem processo (a bateria troca cada passo por um falso):
    ///  1. tenta direto; deu e ficou como quer: pronto, sem aviso do Windows;
    ///  2. só ACESSO NEGADO segue para o administrador, e só se o exe pode ser elevado
    ///     (<paramref name="motivoParaNaoElevar"/> nulo);
    ///  3. Não no aviso (1223): Cancelou;
    ///  4. o resultado é o registro RELIDO, nunca o código de saída (que vai só para o detalhe).
    /// </summary>
    public static (ResultadoDaTrava Resultado, string Detalhe) Mudar(
        Func<Exception?> tentarDireto, Func<bool> ficouComoQuer, string? motivoParaNaoElevar, Func<int?> pedirAoWindows)
    {
        Exception? direto;
        try { direto = tentarDireto(); }
        catch (Exception ex) { direto = ex; }
        if (direto is null)
            return ficouComoQuer()
                ? (ResultadoDaTrava.Ok, "direto, sem aviso do Windows")
                : (ResultadoDaTrava.Falhou, "gravou direto, mas o Windows não ficou como pedido");

        var detalhe = "direto: " + Tecnico(direto);
        if (!AcessoNegado(direto)) return (ResultadoDaTrava.Falhou, detalhe);
        if (motivoParaNaoElevar is not null) return (ResultadoDaTrava.Falhou, detalhe + "; sem pedir ao Windows: " + motivoParaNaoElevar);

        int? saida;
        try { saida = pedirAoWindows(); }
        catch (Exception ex) when (Cancelado(ex))
        {
            return (ResultadoDaTrava.Cancelou, detalhe + "; o aviso do Windows foi respondido com Não");
        }
        catch (Exception ex) { return (ResultadoDaTrava.Falhou, detalhe + "; pedido ao Windows: " + Tecnico(ex)); }

        detalhe += "; administrador: " + DescreverSaida(saida);
        bool ficou;
        try { ficou = ficouComoQuer(); }
        catch { ficou = false; }
        return (ficou ? ResultadoDaTrava.Ok : ResultadoDaTrava.Falhou, detalhe);
    }

    /// <summary>O texto técnico de uma exceção, para a auditoria (nunca para a tela).</summary>
    public static string Tecnico(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    /// <summary>O código de saída do ajudante em palavras, para a auditoria.</summary>
    public static string DescreverSaida(int? codigo) => codigo switch
    {
        null => "não terminou em 20 s",
        SaidaOk => "gravou",
        SaidaArgumento => "argumento recusado",
        SaidaUsuarioFora => "o registro do usuário não está carregado",
        SaidaErro => "erro no registro",
        SaidaSemAdministrador => "rodou sem administrador",
        SaidaOutraPessoa => "SID de outra pessoa recusado",
        _ => "saiu com " + codigo.Value.ToString(CultureInfo.InvariantCulture),
    };

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
