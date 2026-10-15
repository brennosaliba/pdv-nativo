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
/// desfaz só o que é nosso. É o caso de quem desinstala pela área de trabalho do caixa ("Sair para
/// o Windows"): o perfil do caixa está carregado.
///
/// A REGRA, pura (a bateria prova cada pedaço):
///  · só perfil de pessoa (S-1-5-21-..., S-1-12-1-...); .DEFAULT, SYSTEM e os SID_Classes ficam;
///  · Shell nosso = exatamente o formato que o PDV grava, E apontando para um Pdv.exe que este
///    desinstalador remove ou que já não existe (tela preta). Explorer, vazio ou outro programa:
///    não é nosso, fica;
///  · Shell de OUTRO Pdv.exe que continua no disco (outra instalação): o perfil é dela, nada sai;
///  · as políticas só saem com a MARCA da trava (TravaDoCtrlAltDel.Marca, DWORD 1 na mesma chave
///    protegida, que só administrador grava): sem ela, um DisableTaskMgr = 1 pode ser da TI ou de
///    uma GPO local, e fica. Com ela, saem as que estão como a trava grava (DWORD 1) e depois a
///    marca. A marca vale mesmo sem o Shell nosso: o quiosque desligado por fora deixava a trava;
///  · A ORDEM (revisão do desinstalador): o Shell sai PRIMEIRO, porque é ele que evita a tela preta;
///    depois as políticas, uma a uma; a marca por último e só se todas saíram, para uma nova
///    tentativa (o Salvar do caixa, ou outra desinstalação) ainda reconhecer o que sobrou;
///  · Shell nosso que não saiu SEGURA a desinstalação (<see cref="ImpedeDesinstalar"/>): apagar o
///    Pdv.exe com o Shell apontando para ele é a tela preta, e o desinstalador se apaga junto, então
///    não haveria como rodar de novo. Esse perfil fica inteiro como estava (quiosque e trava).
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

    /// <summary>
    /// O Shell é de OUTRO Pdv.exe, que este desinstalador não remove e que continua no disco (ou que
    /// não deu para conferir)? Aí o perfil é daquela instalação: nem o Shell nem a trava saem.
    /// </summary>
    public static bool ShellDeOutroPdv(string? valor, IEnumerable<string> exesRemovidos, Func<string, bool> existe)
        => ExeDoShell(valor) is not null && !ShellDoCaixa(valor, exesRemovidos, existe);

    private static bool MesmoCaminho(string? a, string b)
    {
        if (string.IsNullOrWhiteSpace(a)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>A política (ou a marca) está como a trava grava (DWORD 1)? 0, texto, QWORD ou outro número: não é nossa.</summary>
    public static bool PoliticaDaTrava(object? valor) => valor is int i && i == 1;

    /// <summary>Em HKEY_USERS, a chave é o perfil de uma pessoa? (.DEFAULT, S-1-5-18/19/20 e SID_Classes não.)</summary>
    public static bool PerfilDePessoa(string? nome) => TravaDoCtrlAltDel.SidDeUsuario(nome);

    /// <summary>
    /// O que fazer num perfil: tirar o Shell? Quais políticas tirar? Tirar a marca (depois delas)?
    /// </summary>
    public sealed record Plano(bool TirarShell, IReadOnlyList<(string Chave, string Valor)> Politicas, bool TirarMarca)
    {
        public static readonly Plano Nada = new(false, Array.Empty<(string, string)>(), false);
        public bool AlgumaCoisa => TirarShell || TirarMarca || Politicas.Count > 0;
    }

    /// <summary>
    /// A DECISÃO de um perfil. <paramref name="ler"/> lê (chave, valor) do perfil; leitura que lança
    /// deixa aquele valor como está (não dá para provar que é nosso).
    /// </summary>
    public static Plano Decidir(string? shell, Func<string, string, object?> ler,
        IEnumerable<string> exesRemovidos, Func<string, bool> existe)
    {
        var removidos = exesRemovidos as IReadOnlyCollection<string> ?? exesRemovidos.ToList();
        if (ShellDeOutroPdv(shell, removidos, existe)) return Plano.Nada;
        var tirarShell = ShellDoCaixa(shell, removidos, existe);

        object? Ler(string chave, string valor)
        {
            try { return ler(chave, valor); }
            catch { return null; }
        }

        var marca = PoliticaDaTrava(Ler(TravaDoCtrlAltDel.Marca.Chave, TravaDoCtrlAltDel.Marca.Valor));
        var tirar = new List<(string, string)>();
        if (marca)
            foreach (var (chave, valor) in TravaDoCtrlAltDel.Travas)
                if (PoliticaDaTrava(Ler(chave, valor))) tirar.Add((chave, valor));
        return new Plano(tirarShell, tirar, marca);
    }

    /// <summary>Como foi a desinstalação nos perfis: quantos desfeitos, e em quantos algo nosso ficou.</summary>
    /// <param name="Desfeitos">Perfis em que o Shell, a trava ou a marca saíram.</param>
    /// <param name="ShellFicou">Perfis em que o Shell era nosso e não saiu (segura a desinstalação).</param>
    /// <param name="TravaFicou">Perfis em que uma política ou a marca da trava não saiu.</param>
    public sealed record Resumo(int Desfeitos, int ShellFicou, int TravaFicou)
    {
        public static readonly Resumo Nenhum = new(0, 0, 0);
    }

    /// <summary>Shell nosso que não saiu: a desinstalação para antes de apagar o Pdv.exe.</summary>
    public static bool ImpedeDesinstalar(Resumo r) => r.ShellFicou > 0;

    /// <summary>
    /// O motivo de não desinstalar (vai depois de "Não consegui remover: "). Uma linha, sem termo
    /// técnico, e com o que fazer.
    /// </summary>
    public const string MotivoShellFicou =
        "o modo quiosque não saiu de um usuário do Windows. Desligue o quiosque na Configuração do caixa e tente de novo.";

    /// <summary>O que o desinstalador diz no fim sobre o quiosque. Curto, sem termo técnico; vazio quando não houve nada.</summary>
    public static string Aviso(Resumo r)
    {
        var aviso = r.Desfeitos > 0 && r.ShellFicou == 0
            ? " O modo quiosque foi desfeito: na próxima entrada, o Windows abre a área de trabalho normal."
            : "";
        if (r.TravaFicou > 0) aviso += " A trava do Ctrl+Alt+Del não saiu de um usuário do Windows: chame o suporte.";
        return aviso;
    }
}
