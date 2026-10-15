using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Pdv.Nucleo;

/// <summary>Um processo do navegador do WebView2 (o principal, sem <c>--type=</c>) e quem o abriu.</summary>
/// <param name="Linha">A linha de comando inteira (vazia quando o Windows não deixou ler).</param>
public sealed record NavegadorNoPerfil(int Pid, int PaiPid, bool PaiVivo, DateTime? Inicio, string Linha)
{
    /// <summary>O exe que abriu este navegador (<c>--webview-exe-name</c>), por exemplo Pdv.exe.</summary>
    public string? Exe => NavegadorDoPerfil.Valor(Linha, "--webview-exe-name");
    /// <summary>A versão do exe que abriu (<c>--webview-exe-version</c>), por exemplo 1.0.32.0.</summary>
    public string? Versao => NavegadorDoPerfil.Valor(Linha, "--webview-exe-version");
}

// ════════════════════════════════════════════════════════════════════════════
//  QUEM SEGURA O PERFIL DO NAVEGADOR (1.0.35, Savassi, 14/10/2026)
//
//  Às 21:21 o caixa da Savassi se atualizou de 1.0.32 para 1.0.34 e o chat do iFood e o
//  WhatsApp ficaram em "não abriu agora" até o PC reiniciar. A 1.0.33 mudou as opções do
//  navegador dos dois perfis (ProgramData\PdvNativo\webview e webview-whatsapp). Provado
//  com o runtime de verdade: enquanto um navegador com as opções ANTIGAS está vivo no
//  perfil, o WebView2 recusa o ambiente com as novas no EnsureCoreWebView2Async, com
//  COMException 0x8007139F (ERROR_INVALID_STATE). Com as mesmas opções, entra e divide.
//
//  Aqui mora o que o caixa e o instalador precisam para lidar com isso: achar o navegador
//  que está no perfil (pela linha de comando dele), saber se quem o abriu ainda vive,
//  escolher com que opções dá para entrar nele e encerrar o que ficou para trás. A
//  decisão é pura e testada (TestesPerfilEmUso, PE-*); a leitura dos processos é do
//  Windows e também é testada de verdade, com um navegador segurando um perfil (RP-*).
//  Quem usa: Telas/HospedeWebView2.cs (o caixa) e Pdv.Instalador/App.xaml.cs (a troca).
// ════════════════════════════════════════════════════════════════════════════
public static class NavegadorDoPerfil
{
    /// <summary>O nome do processo do navegador do WebView2.</summary>
    public const string Processo = "msedgewebview2";

    /// <summary>A pasta que o WebView2 cria dentro da pasta de dados que o caixa dá a ele.</summary>
    public const string SubpastaDoWebView2 = "EBWebView";

    /// <summary>Argumentos que o próprio WebView2 põe em todo navegador: não dizem nada sobre as opções do caixa.</summary>
    private static readonly string[] DoProprioWebView2 =
    {
        "--embedded-browser-webview", "--webview-exe-name", "--webview-exe-version", "--user-data-dir", "--noerrdialogs",
        "--embedded-browser-webview-dpi-awareness", "--mojo-named-platform-channel-pipe", "--lang",
    };

    // ── A LEITURA DA LINHA DE COMANDO (pura) ────────────────────────────────

    /// <summary>Os pedaços da linha de comando, como o Windows os separa (aspas juntam, as aspas saem).</summary>
    public static IReadOnlyList<string> Pedacos(string? linha)
    {
        var lista = new List<string>();
        if (string.IsNullOrWhiteSpace(linha)) return lista;
        var atual = new System.Text.StringBuilder();
        var dentro = false;
        var tem = false;
        foreach (var c in linha)
        {
            if (c == '"') { dentro = !dentro; tem = true; continue; }
            if (char.IsWhiteSpace(c) && !dentro)
            {
                if (tem || atual.Length > 0) lista.Add(atual.ToString());
                atual.Clear(); tem = false;
                continue;
            }
            atual.Append(c);
        }
        if (tem || atual.Length > 0) lista.Add(atual.ToString());
        return lista;
    }

    /// <summary>O valor de <c>--chave=valor</c> na linha (sem aspas), ou null.</summary>
    public static string? Valor(string? linha, string chave)
    {
        var prefixo = chave + "=";
        foreach (var p in Pedacos(linha))
            if (p.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase)) return p[prefixo.Length..];
        return null;
    }

    /// <summary>É o processo PRINCIPAL de um navegador do WebView2 (os filhos têm <c>--type=</c>)?</summary>
    public static bool EhNavegadorPrincipal(string? linha)
    {
        var pedacos = Pedacos(linha);
        return pedacos.Any(p => p.StartsWith("--embedded-browser-webview", StringComparison.OrdinalIgnoreCase))
               && pedacos.Any(p => p.StartsWith("--user-data-dir=", StringComparison.OrdinalIgnoreCase))
               && !pedacos.Any(p => p.StartsWith("--type=", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A pasta de dados do navegador, normalizada (sem o <c>\\?\</c> e sem barra no fim), ou null.</summary>
    public static string? PastaDeDados(string? linha)
    {
        var v = Valor(linha, "--user-data-dir");
        return string.IsNullOrWhiteSpace(v) ? null : Normalizar(v);
    }

    /// <summary>O navegador desta linha é o do perfil <paramref name="pastaPerfil"/> (a pasta que o caixa dá ao WebView2)?</summary>
    public static bool EhDoPerfil(string? linha, string pastaPerfil)
        => EhNavegadorPrincipal(linha) && PastaDeDados(linha) is { } dados
           && string.Equals(dados, Normalizar(Path.Combine(pastaPerfil, SubpastaDoWebView2)), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// O navegador desta linha tem a pasta de dados DENTRO de <paramref name="raiz"/> (o instalador:
    /// ProgramData\PdvNativo)? Com <paramref name="exe"/>, só o que esse programa abriu (o instalador: Pdv.exe).
    /// </summary>
    public static bool EhDentroDe(string? linha, string raiz, string? exe = null)
    {
        if (!EhNavegadorPrincipal(linha) || PastaDeDados(linha) is not { } dados) return false;
        if (exe is not null && !string.Equals(Valor(linha, "--webview-exe-name"), exe, StringComparison.OrdinalIgnoreCase)) return false;
        var r = Normalizar(raiz);
        return r.Length > 3 && dados.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalizar(string caminho)
    {
        var c = caminho.Trim().Trim('"').Replace('/', '\\');
        if (c.StartsWith(@"\\?\", StringComparison.Ordinal)) c = c[4..];
        return c.TrimEnd('\\');
    }

    /// <summary>Os argumentos de uma opção do caixa (AdditionalBrowserArguments), um por pedaço. Null = nenhum.</summary>
    public static IReadOnlyList<string> Argumentos(string? adicionais)
        => Pedacos(adicionais).Where(p => p.Length > 0).ToList();

    /// <summary>Os argumentos da linha que vieram de quem abriu o navegador (tira os que o WebView2 põe sempre).</summary>
    public static IReadOnlyList<string> ArgumentosDeQuemAbriu(string? linha)
        => Pedacos(linha).Skip(1)
            .Where(p => p.StartsWith("--", StringComparison.Ordinal)
                        && !DoProprioWebView2.Any(d => p.Equals(d, StringComparison.OrdinalIgnoreCase)
                                                      || p.StartsWith(d + "=", StringComparison.OrdinalIgnoreCase)))
            .ToList();

    // ── A DECISÃO (pura) ────────────────────────────────────────────────────

    /// <summary>
    /// Com que opções tentar quando o perfil recusou as de índice <paramref name="recusada"/>. Os
    /// índices são de <paramref name="opcoes"/> (cada uma é um AdditionalBrowserArguments; null =
    /// sem opção nenhuma). Com a linha do navegador que segura o perfil, só entram as opções que
    /// casam EXATAMENTE com o que ele tem entre os argumentos que o caixa já usou (um argumento a
    /// mais ou a menos o WebView2 recusa de novo). Sem linha nenhuma (o Windows não deixou ler, ou o
    /// dono sumiu no meio), todas as outras, na ordem dada.
    /// </summary>
    public static IReadOnlyList<int> OrdemDasOpcoes(IReadOnlyList<string?> opcoes, int recusada, IReadOnlyList<string> linhasDosDonos)
    {
        var outras = Enumerable.Range(0, opcoes.Count).Where(i => i != recusada).ToList();
        var linhas = linhasDosDonos.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (linhas.Count == 0) return outras;
        var conhecidos = new HashSet<string>(opcoes.SelectMany(o => Argumentos(o)), StringComparer.OrdinalIgnoreCase);
        var doDono = new HashSet<string>(Pedacos(linhas[0]).Where(conhecidos.Contains), StringComparer.OrdinalIgnoreCase);
        return outras.Where(i => doDono.SetEquals(Argumentos(opcoes[i]))).ToList();
    }

    /// <summary>Quem segura o perfil: ninguém, só navegadores órfãos (quem os abriu morreu) ou algum com dono vivo.</summary>
    public enum Dono { Nenhum, Orfao, Vivo }

    public static Dono Classificar(IReadOnlyList<NavegadorNoPerfil> donos)
        => donos.Count == 0 ? Dono.Nenhum : donos.All(d => !d.PaiVivo) ? Dono.Orfao : Dono.Vivo;

    /// <summary>Uma linha para o diagnóstico: PID, quem abriu (vivo ou não), o exe e a versão, e os argumentos dele.</summary>
    public static string Descrever(NavegadorNoPerfil n)
    {
        var args = string.Join(" ", ArgumentosDeQuemAbriu(n.Linha));
        if (args.Length > 200) args = args[..200] + "…";
        var linha = string.IsNullOrEmpty(n.Linha) ? " (a linha de comando não abriu)" : "";
        return $"navegador pid={n.Pid} aberto por pid={n.PaiPid} ({(n.PaiVivo ? "vivo" : "morto")}) "
             + $"exe={n.Exe ?? "?"} {n.Versao ?? "?"} argumentos=[{args}]{linha}";
    }

    /// <summary>Todos os donos numa linha só (para o diagnóstico).</summary>
    public static string Descrever(IReadOnlyList<NavegadorNoPerfil> donos)
        => donos.Count == 0 ? "nenhum navegador no perfil" : string.Join(" | ", donos.Select(Descrever));

    // ── O WINDOWS ───────────────────────────────────────────────────────────

    /// <summary>Os navegadores (principais) do WebView2 do perfil <paramref name="pastaPerfil"/>. Nunca lança.</summary>
    public static IReadOnlyList<NavegadorNoPerfil> Listar(string pastaPerfil)
        => Varrer(linha => EhDoPerfil(linha, pastaPerfil));

    /// <summary>Os navegadores (principais) do WebView2 com a pasta de dados dentro de <paramref name="raiz"/> (e abertos por <paramref name="exe"/>, se dado). Nunca lança.</summary>
    public static IReadOnlyList<NavegadorNoPerfil> ListarDentroDe(string raiz, string? exe = null)
        => Varrer(linha => EhDentroDe(linha, raiz, exe));

    /// <summary>Todos os navegadores (principais) do WebView2 desta máquina, de qualquer programa. Nunca lança.</summary>
    public static IReadOnlyList<NavegadorNoPerfil> ListarTodos()
        => Varrer(EhNavegadorPrincipal);

    private static IReadOnlyList<NavegadorNoPerfil> Varrer(Func<string, bool> serve)
    {
        var achados = new List<NavegadorNoPerfil>();
        Process[] processos;
        try { processos = Process.GetProcessesByName(Processo); }
        catch { return achados; }
        foreach (var p in processos)
        {
            try
            {
                var pid = p.Id;
                using var h = Abrir(pid, ConsultaLimitada);
                if (h is null) continue;
                var linha = LinhaDeComando(h) ?? "";
                if (!serve(linha)) continue;
                var inicio = Inicio(h);
                var pai = Pai(h);
                achados.Add(new NavegadorNoPerfil(pid, pai, PaiVivo(pai, inicio), inicio, linha));
            }
            catch { /* processo que saiu no meio da varredura */ }
            finally { p.Dispose(); }
        }
        return achados;
    }

    /// <summary>
    /// Encerra ESTE navegador e espera ele sair. Confere a linha de comando pelo mesmo handle que
    /// encerra (o PID pode ter ido para outro processo no meio). Devolve se saiu. Nunca lança.
    /// </summary>
    public static bool Encerrar(NavegadorNoPerfil n, TimeSpan espera)
    {
        try
        {
            using var h = Abrir(n.Pid, ConsultaLimitada | Terminar | Sincronizar);
            if (h is null) return !Existe(n.Pid);
            if (!string.Equals(LinhaDeComando(h) ?? "", n.Linha, StringComparison.Ordinal)) return false;
            if (!TerminateProcess(h, 1) && WaitForSingleObject(h, 0) != 0) return false;
            return WaitForSingleObject(h, (uint)Math.Clamp(espera.TotalMilliseconds, 0, int.MaxValue)) == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// A TROCA DE VERSÃO (o instalador, depois que o caixa anterior saiu): espera até
    /// <paramref name="espera"/> os navegadores com a pasta de dados em <paramref name="raiz"/>,
    /// abertos por <paramref name="exe"/>, saírem sozinhos e encerra os que ficarem. Devolve uma
    /// linha para o diagnóstico. Nunca lança.
    /// </summary>
    public static string EncerrarSobras(string raiz, TimeSpan espera, string? exe)
    {
        try
        {
            var relogio = Stopwatch.StartNew();
            var vivos = ListarDentroDe(raiz, exe);
            if (vivos.Count == 0) return "navegadores do caixa anterior: nenhum";
            var antes = vivos.Count;
            while (vivos.Count > 0 && relogio.Elapsed < espera)
            {
                Thread.Sleep(250);
                vivos = ListarDentroDe(raiz, exe);
            }
            if (vivos.Count == 0)
                return $"navegadores do caixa anterior: {antes} saíram sozinhos em {relogio.Elapsed.TotalSeconds:0.0} s";
            var partes = vivos.Select(v => (Encerrar(v, TimeSpan.FromSeconds(5)) ? "encerrado " : "NÃO saiu ") + Descrever(v));
            return $"navegadores do caixa anterior: {vivos.Count} de {antes} ficaram depois de {espera.TotalSeconds:0} s: " + string.Join(" | ", partes);
        }
        catch (Exception ex) { return "navegadores do caixa anterior: a conferência falhou: " + ex.GetType().Name; }
    }

    // ── P/Invoke (só leitura do processo, e o encerramento acima) ───────────

    private const uint ConsultaLimitada = 0x1000;   // PROCESS_QUERY_LIMITED_INFORMATION
    private const uint Terminar = 0x0001;           // PROCESS_TERMINATE
    private const uint Sincronizar = 0x00100000;    // SYNCHRONIZE
    private const int AindaVivo = 259;              // STILL_ACTIVE

    private sealed class Handle : IDisposable
    {
        public readonly IntPtr Valor;
        public Handle(IntPtr v) => Valor = v;
        public void Dispose() { if (Valor != IntPtr.Zero) CloseHandle(Valor); }
        public static implicit operator IntPtr(Handle h) => h.Valor;
    }

    private static Handle? Abrir(int pid, uint acesso)
    {
        var h = OpenProcess(acesso, false, pid);
        return h == IntPtr.Zero ? null : new Handle(h);
    }

    private static bool Existe(int pid)
    {
        var h = OpenProcess(ConsultaLimitada, false, pid);
        if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5;   // acesso negado: existe
        try { return GetExitCodeProcess(h, out var c) && c == AindaVivo; }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// Quem abriu ainda vive? O PID do pai pode ter sido reaproveitado por outro processo depois
    /// que o pai morreu: pai que nasceu DEPOIS do filho não é o pai. Na dúvida (sem acesso), vivo:
    /// navegador com dono vivo nunca é encerrado pelo caixa.
    /// </summary>
    private static bool PaiVivo(int paiPid, DateTime? inicioDoFilho)
    {
        if (paiPid <= 0) return false;
        var h = OpenProcess(ConsultaLimitada, false, paiPid);
        if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5;
        try
        {
            if (!GetExitCodeProcess(h, out var c) || c != AindaVivo) return false;
            if (inicioDoFilho is { } f && InicioDe(h) is { } p && p > f) return false;
            return true;
        }
        finally { CloseHandle(h); }
    }

    private static DateTime? Inicio(Handle h) => InicioDe(h.Valor);

    private static DateTime? InicioDe(IntPtr h)
    {
        try { return GetProcessTimes(h, out var criado, out _, out _, out _) ? DateTime.FromFileTimeUtc(criado) : null; }
        catch { return null; }
    }

    private static int Pai(Handle h)
    {
        var tamanho = Marshal.SizeOf<InformacaoBasica>();
        var buf = Marshal.AllocHGlobal(tamanho);
        try
        {
            if (NtQueryInformationProcess(h, 0, buf, tamanho, out _) != 0) return 0;
            return (int)Marshal.PtrToStructure<InformacaoBasica>(buf).InheritedFromUniqueProcessId.ToInt64();
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>A linha de comando pelo ProcessCommandLineInformation (60): só pede consulta limitada.</summary>
    private static string? LinhaDeComando(Handle h)
    {
        var tamanho = 4096;
        for (var vez = 0; vez < 3; vez++)
        {
            var buf = Marshal.AllocHGlobal(tamanho);
            try
            {
                var st = NtQueryInformationProcess(h, 60, buf, tamanho, out var preciso);
                if (st == unchecked((int)0xC0000004) || st == unchecked((int)0x80000005) || st == unchecked((int)0xC0000023))
                {
                    tamanho = Math.Max(preciso, tamanho * 2);
                    continue;
                }
                if (st != 0) return null;
                var u = Marshal.PtrToStructure<TextoUnicode>(buf);
                return u.Buffer == IntPtr.Zero || u.Length == 0 ? "" : Marshal.PtrToStringUni(u.Buffer, u.Length / 2);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InformacaoBasica
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextoUnicode
    {
        public ushort Length, MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint acesso, bool herdar, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr h, out int codigo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr h, uint codigo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr h, out long criado, out long saida, out long kernel, out long usuario);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr h, int classe, IntPtr info, int tamanho, out int devolvido);
}
