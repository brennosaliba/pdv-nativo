using System.IO;

namespace Pdv.Nucleo;

/// <summary>
/// O DESINSTALADOR DESFAZ O QUIOSQUE (1.0.36, 14/10/2026, revisão da trava do Ctrl+Alt+Del).
///
/// O quiosque grava no registro DO USUÁRIO do caixa o Shell do Windows apontando para o Pdv.exe
/// (Winlogon\Shell = "C:\...\Pdv.exe" --quiosque), e a trava grava quatro políticas que tiram do
/// Ctrl+Alt+Del o Gerenciador de Tarefas, o Bloquear, o Trocar senha e o Sair. Desinstalar apagava o
/// Pdv.exe e deixava os dois: na próxima entrada o Windows abria um Shell que não existe (tela preta)
/// e o Ctrl+Alt+Del não tinha nada além de Cancelar. O caixa ficava preso sem saída pela tela.
///
/// O instalador não sabe quem é o usuário do caixa: ele grava a máquina (Program Files, HKLM), e o
/// Adicionar/Remover Programas o abre como administrador, às vezes com OUTRA conta (o usuário comum
/// digita a senha de um administrador). Então ele percorre os perfis carregados em HKEY_USERS e
/// desfaz só onde o Shell é o nosso. É o caso de quem desinstala pela área de trabalho do caixa
/// ("Sair para o Windows"): o perfil do caixa está carregado.
///
/// A REGRA, pura (a bateria prova cada pedaço):
///  · só perfil de pessoa (S-1-5-21-..., S-1-12-1-...); .DEFAULT, SYSTEM e os SID_Classes ficam;
///  · Shell nosso = exatamente o formato que o PDV grava, E apontando para um Pdv.exe que este
///    desinstalador remove ou que já não existe (tela preta). Explorer, vazio, outro programa ou um
///    Pdv.exe que continua no disco em outra pasta: não é nosso caso, não toca;
///  · com o Shell nosso, tira só as políticas da trava que estão como a trava grava (DWORD 1). DWORD
///    0, texto ou outro número não são nossos. Sem o Shell nosso, NÃO toca nas políticas: aí elas
///    podem ser de uma GPO ou de outra pessoa;
///  · as políticas saem antes do Shell: se algo falhar no meio, rodar de novo ainda acha o Shell e
///    termina o serviço.
/// </summary>
public static class DesfazerQuiosque
{
    public const string ChaveWinlogon = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    public const string ValorShell = "Shell";
    public const string MarcaDoQuiosque = "--quiosque";
    public const string ExeDoPdv = "Pdv.exe";

    /// <summary>O valor que o PDV grava em Winlogon\Shell: o exe entre aspas e a marca do quiosque.</summary>
    public static string ShellDoPdv(string exe) => "\"" + exe + "\" " + MarcaDoQuiosque;

    /// <summary>
    /// O exe dentro de um Shell no formato EXATO do PDV (<c>"C:\...\Pdv.exe" --quiosque</c>), ou null.
    /// Caminho relativo, outro nome de exe, sem a marca ou com algo a mais: null.
    /// </summary>
    public static string? ExeDoShell(string? valor)
    {
        const string fim = "\" " + MarcaDoQuiosque;
        if (valor is null || valor.Length <= fim.Length + 1 || valor[0] != '"' || !valor.EndsWith(fim, StringComparison.Ordinal)) return null;
        var exe = valor[1..^fim.Length];
        if (exe.Contains('"') || exe.Contains('\n') || exe.Contains('\r')) return null;
        try
        {
            if (!Path.IsPathFullyQualified(exe)) return null;
            if (!string.Equals(Path.GetFileName(exe), ExeDoPdv, StringComparison.OrdinalIgnoreCase)) return null;
        }
        catch { return null; }
        return exe;
    }

    /// <summary>
    /// Esse Shell é do caixa que está saindo? Formato do PDV E (um dos exes que este desinstalador
    /// remove OU um Pdv.exe que não existe mais). Um Pdv.exe que segue vivo em outra pasta não é.
    /// </summary>
    public static bool ShellDoCaixa(string? valor, IEnumerable<string> exesRemovidos, Func<string, bool> existe)
    {
        var exe = ExeDoShell(valor);
        if (exe is null) return false;
        foreach (var r in exesRemovidos)
            if (MesmoCaminho(r, exe)) return true;
        try { return !existe(exe); }
        catch { return false; }
    }

    private static bool MesmoCaminho(string? a, string b)
    {
        if (string.IsNullOrWhiteSpace(a)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>A política está como a trava grava (DWORD 1)? 0, texto, QWORD ou outro número: não é nossa.</summary>
    public static bool PoliticaDaTrava(object? valor) => valor is int i && i == 1;

    /// <summary>Em HKEY_USERS, a chave é o perfil de uma pessoa? (.DEFAULT, S-1-5-18/19/20 e SID_Classes não.)</summary>
    public static bool PerfilDePessoa(string? nome) => TravaDoCtrlAltDel.SidDeUsuario(nome);

    /// <summary>O que fazer num perfil: tirar o Shell? Quais políticas tirar (antes do Shell)?</summary>
    public sealed record Plano(bool TirarShell, IReadOnlyList<(string Chave, string Valor)> Politicas)
    {
        public static readonly Plano Nada = new(false, Array.Empty<(string, string)>());
    }

    /// <summary>
    /// A DECISÃO de um perfil. <paramref name="lerPolitica"/> lê (chave, valor) do perfil; leitura que
    /// lança deixa aquela política como está (não dá para provar que é nossa).
    /// </summary>
    public static Plano Decidir(string? shell, Func<string, string, object?> lerPolitica,
        IEnumerable<string> exesRemovidos, Func<string, bool> existe)
    {
        if (!ShellDoCaixa(shell, exesRemovidos, existe)) return Plano.Nada;
        var tirar = new List<(string, string)>();
        foreach (var (chave, valor) in TravaDoCtrlAltDel.Travas)
        {
            object? v;
            try { v = lerPolitica(chave, valor); }
            catch { continue; }
            if (PoliticaDaTrava(v)) tirar.Add((chave, valor));
        }
        return new Plano(true, tirar);
    }

    /// <summary>O que o desinstalador diz quando desfez o quiosque de alguém. Uma linha, sem termo técnico.</summary>
    public static string Aviso(int perfisDesfeitos)
        => perfisDesfeitos > 0 ? " O modo quiosque foi desfeito: na próxima entrada, o Windows abre a área de trabalho normal." : "";
}
