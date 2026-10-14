namespace Pdv.Nucleo;

/// <summary>Um programa que o Windows abriria ao entrar (chave Run ou pasta Inicializar).</summary>
/// <param name="Nome">O nome que a pessoa reconhece ("AnyDesk", "Spotify").</param>
/// <param name="Exe">O executável (ou o atalho .lnk da pasta Inicializar).</param>
/// <param name="Argumentos">O resto da linha de comando ("--autostart --minimized").</param>
/// <param name="Origem">De onde veio: "run", "pasta" ou "instalado" (achado no disco, fora da inicialização).</param>
public sealed record ProgramaDoInicio(string Nome, string Exe, string Argumentos, string Origem);

/// <summary>
/// OS PROGRAMAS DA INICIALIZAÇÃO DO WINDOWS NO MODO QUIOSQUE (14/10/2026, PDV 1.0.33).
///
/// O dono: "coloca no PDV para abrir automaticamente junto do PDV os apps que estão na
/// inicialização do Windows, ou me dê uma opção, porque quero que inicialize o Spotify e o
/// AnyDesk junto". No quiosque o PDV é a área de trabalho do Windows (<c>Quiosque.cs</c>), e o
/// Windows só abre os programas da inicialização quando a área de trabalho é o Explorer: por
/// isso nada abria.
///
/// A regra (pura, testada em TestesInicioDoWindows):
///  · só no quiosque (fora dele, quem abre é o próprio Windows, e abriria duas vezes);
///  · só o que está ligado no Gerenciador de Tarefas (StartupApproved) e MARCADO na Configuração
///    do caixa (<see cref="ChaveMarcados"/>). Antes de alguém salvar a lista, vêm marcados só o
///    Spotify e o AnyDesk: um PC com Edge, OneDrive e afins na inicialização não abre tudo isso
///    por cima do caixa. Programa novo na inicialização entra desmarcado;
///  · nunca o próprio PDV, o agente dele nem o Explorer; nunca o que já está aberto.
/// </summary>
public static class InicioDoWindows
{
    /// <summary>Config local: "0" = não abre nada; ausente ou "1" = abre os marcados (no quiosque).</summary>
    public const string ChaveLigado = "inicio_windows_ligado";
    /// <summary>Config local: os nomes marcados na Configuração, separados por "|" (ausente = o padrão).</summary>
    public const string ChaveMarcados = "inicio_windows_marcados";

    public static bool Ligado(string? valor) => valor?.Trim() != "0";

    /// <summary>O que vem marcado antes de alguém salvar a lista: o que o dono pediu (14/10).</summary>
    public static bool MarcadoPorPadrao(ProgramaDoInicio p)
        => Processo(p.Exe) is "spotify" or "anydesk"
           || p.Nome.Contains("spotify", StringComparison.OrdinalIgnoreCase) || p.Nome.Contains("anydesk", StringComparison.OrdinalIgnoreCase);

    /// <summary>Está marcado? Sem lista salva (null), vale o padrão; com lista, só o que está nela.</summary>
    public static bool Marcado(ProgramaDoInicio p, string? marcados)
        => marcados is null ? MarcadoPorPadrao(p) : Lista(marcados).Contains(p.Nome);

    public static IReadOnlySet<string> Lista(string? valor)
        => new HashSet<string>((valor ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                               StringComparer.OrdinalIgnoreCase);

    public static string EscreverLista(IEnumerable<string> nomes)
        => string.Join("|", nomes.Select(n => n.Replace("|", " ").Trim()).Where(n => n.Length > 0)
                                 .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Separa a linha de comando de uma chave Run em executável e argumentos. Aceita o exe entre
    /// aspas ("C:\Program Files\X\x.exe" --a) e sem aspas com espaço no caminho (C:\Program
    /// Files\X\x.exe --a: corta logo depois do .exe). Variáveis (%LOCALAPPDATA%) passam por
    /// <paramref name="expandir"/>. Linha vazia ou sem executável: null.
    /// </summary>
    public static (string Exe, string Argumentos)? Separar(string? comando, Func<string, string>? expandir = null)
    {
        var c = (comando ?? "").Trim();
        if (c.Length == 0) return null;
        if (expandir is not null) { try { c = expandir(c).Trim(); } catch { } }
        string exe, args;
        if (c.StartsWith('"'))
        {
            var fim = c.IndexOf('"', 1);
            if (fim <= 1) return null;
            exe = c[1..fim];
            args = c[(fim + 1)..].Trim();
        }
        else
        {
            var i = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) { exe = c[..(i + 4)]; args = c[(i + 4)..].Trim(); }
            else
            {
                var sp = c.IndexOf(' ');
                exe = sp < 0 ? c : c[..sp];
                args = sp < 0 ? "" : c[(sp + 1)..].Trim();
            }
        }
        exe = exe.Trim();
        return exe.Length == 0 ? null : (exe, args);
    }

    /// <summary>
    /// A entrada está DESLIGADA no Gerenciador de Tarefas? (StartupApproved: o primeiro byte par,
    /// 02 ou 06, é ligado; ímpar, 03 ou 07, é desligado. Sem valor = ligado.)
    /// </summary>
    public static bool DesligadoNoGerenciador(byte[]? aprovado) => aprovado is { Length: > 0 } && (aprovado[0] & 1) == 1;

    /// <summary>O nome do processo que um executável vira ("C:\x\AnyDesk.exe" → "anydesk").</summary>
    public static string Processo(string exe)
    {
        try { return Path.GetFileNameWithoutExtension(exe.Trim().Trim('"')).ToLowerInvariant(); }
        catch { return ""; }
    }

    /// <summary>O próprio PDV, o agente dele, o instalador, o Explorer: nunca abrem por aqui.</summary>
    public static bool DoCaixa(string exe, string exeDoPdv)
    {
        var p = Processo(exe);
        if (p.Length == 0) return true;
        if (p is "explorer" or "pdv" or "pdvagente" or "pdv.agente" || p.StartsWith("instalarpdv", StringComparison.Ordinal)) return true;
        try { return string.Equals(Path.GetFullPath(exe), Path.GetFullPath(exeDoPdv), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>
    /// O que abrir agora: um por executável, só o marcado (<paramref name="marcados"/>, ver
    /// <see cref="Marcado"/>), sem o que é do caixa e sem o que já está aberto
    /// (<paramref name="rodando"/>: nomes de processo em minúsculas).
    /// </summary>
    public static IReadOnlyList<ProgramaDoInicio> AAbrir(IEnumerable<ProgramaDoInicio> todos, string? marcados,
        IReadOnlySet<string> rodando, string exeDoPdv)
    {
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var saida = new List<ProgramaDoInicio>();
        foreach (var p in todos)
        {
            if (DoCaixa(p.Exe, exeDoPdv)) continue;
            if (!Marcado(p, marcados)) continue;
            var proc = Processo(p.Exe);
            if (!vistos.Add(proc)) continue;
            // atalho .lnk da pasta Inicializar: o processo não tem o nome do atalho, então só o nome conta
            if (rodando.Contains(proc) || rodando.Contains(p.Nome.ToLowerInvariant())) continue;
            saida.Add(p);
        }
        return saida;
    }

    /// <summary>Um por nome, na ordem em que apareceram (a lista da Configuração).</summary>
    public static IReadOnlyList<ProgramaDoInicio> Unicos(IEnumerable<ProgramaDoInicio> todos, string exeDoPdv)
    {
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return todos.Where(p => !DoCaixa(p.Exe, exeDoPdv) && vistos.Add(p.Nome)).ToList();
    }
}
