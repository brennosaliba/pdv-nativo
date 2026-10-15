using System.Globalization;
using System.IO;

namespace Pdv.Nucleo;

/// <summary>
/// QUEM REABRE O CAIXA DEPOIS DA ATUALIZAÇÃO (1.0.36, 14/10/2026, revisão de segurança da trava).
///
/// O caixa se atualiza chamando o instalador com --atualizar, e o instalador roda como administrador
/// (requireAdministrator). No fim, era ELE quem reabria o caixa. Com a área de trabalho aberta, pelo
/// Explorer (como o usuário comum); no quiosque, sem Explorer, ele abria o Pdv.exe direto, e o caixa
/// passava o turno inteiro como administrador: WebView2 do chat elevado, arquivos com a permissão de
/// administrador, a trava do Ctrl+Alt+Del gravando sem aviso, e as bibliotecas que o Pdv.exe extrai
/// em %TEMP%\.net (pasta que o usuário grava) carregadas por um processo elevado.
///
/// Agora quem reabre é o próprio caixa, sem administrador: antes de fechar, ele deixa um cmd COMUM
/// (filho dele, mesma conta, sem elevação) esperando o instalador terminar e então abre o Pdv.exe
/// novo. O instalador recebe <see cref="ArgumentoSemAbrir"/> e não abre nada. Um caixa antigo (sem
/// este argumento) continua sendo reaberto pelo instalador, só que nunca direto como administrador:
/// pelo Explorer, ou a frase de reiniciar o computador (Instalacao.AbrirCaixa).
///
/// A linha do cmd é montada só de constantes, do número do processo do instalador e do caminho do
/// próprio Pdv.exe (que passa por <see cref="CaminhoAceito"/>: nada que o cmd interprete). Os
/// programas (tasklist, find, ping) vão pelo caminho absoluto de System32, para nenhum homônimo do
/// PATH (o find.exe do Git, por exemplo) mudar a conta. Roda com /d (sem AutoRun) e /v:off.
///
/// A espera: a cada ~1 s o tasklist procura o processo do instalador; sumiu, abre o caixa e sai. O
/// instalador que falha mostra a mensagem dele e, depois do OK, o caixa volta na versão anterior. Um
/// instalador preso mais de ~1 h: o cmd desiste sem abrir (abrir no meio da cópia seria pior).
/// </summary>
public static class ReabrirOCaixa
{
    public const string ArgumentoAtualizar = "--atualizar";

    /// <summary>O caixa avisa o instalador: "quem reabre sou eu, não abra nada".</summary>
    public const string ArgumentoSemAbrir = "--sem-abrir";

    /// <summary>Quantas voltas de ~1 s o cmd espera o instalador (perto de uma hora).</summary>
    public const int Voltas = 3600;

    /// <summary>Os programas de System32 que a espera usa (o cmd e os três da linha).</summary>
    public static readonly string[] ProgramasDaEspera = { "cmd.exe", "tasklist.exe", "find.exe", "ping.exe" };

    /// <summary>Os argumentos do instalador: com <see cref="ArgumentoSemAbrir"/> quando o caixa se reabre sozinho.</summary>
    public static string ArgumentosDoInstalador(bool caixaReabre)
        => caixaReabre ? ArgumentoAtualizar + " " + ArgumentoSemAbrir : ArgumentoAtualizar;

    /// <summary>O instalador deve abrir o caixa no fim? Só quando quem chamou não disse que reabre.</summary>
    public static bool InstaladorAbre(IEnumerable<string> argumentos) => !argumentos.Contains(ArgumentoSemAbrir);

    private static readonly char[] Proibidos = { '"', '%', '!', '^', '&', '|', '<', '>' };

    /// <summary>
    /// O caminho do caixa pode entrar na linha do cmd? Absoluto, local (sem \\rede), sem aspas e sem
    /// nada que o cmd interprete (% ! ^ &amp; | &lt; &gt;) nem caractere de controle.
    /// </summary>
    public static bool CaminhoAceito(string? alvo)
    {
        if (string.IsNullOrWhiteSpace(alvo) || alvo.Length > 400) return false;
        if (alvo.IndexOfAny(Proibidos) >= 0 || alvo.Any(c => c < ' ')) return false;
        if (alvo.StartsWith(@"\\", StringComparison.Ordinal) || alvo.StartsWith("//", StringComparison.Ordinal)) return false;
        try { return Path.IsPathFullyQualified(alvo) && Path.GetFileName(alvo).Length > 0; }
        catch { return false; }
    }

    /// <summary>
    /// Por que o caixa NÃO pode se reabrir sozinho (null = pode): caminho do caixa fora do formato, pasta
    /// do sistema fora do formato, ou faltando o caixa ou um dos programas da espera. Aí o caixa chama o
    /// instalador sem <see cref="ArgumentoSemAbrir"/>, como antes.
    /// </summary>
    public static string? MotivoParaNaoReabrir(string? alvo, string? pastaDoSistema, Func<string, bool> existe)
    {
        if (!CaminhoAceito(alvo)) return "caminho do caixa fora do formato (" + (alvo ?? "nenhum") + ")";
        if (!TravaDoCtrlAltDel.PastaDoSistema(pastaDoSistema)) return "pasta do sistema fora do esperado (" + (pastaDoSistema ?? "nenhuma") + ")";
        try
        {
            if (!existe(alvo!)) return "o caixa não está em " + alvo;
            foreach (var p in ProgramasDaEspera)
                if (!existe(pastaDoSistema + @"\" + p)) return p + " não está em " + pastaDoSistema;
        }
        catch (Exception ex) { return "não deu para conferir os arquivos: " + ex.GetType().Name; }
        return null;
    }

    /// <summary>
    /// A linha do cmd que espera o instalador e reabre o caixa:
    /// <c>/d /v:off /s /c "for /l %i in (1,1,3600) do @(("...\tasklist.exe" /fi "PID eq N" /nh | "...\find.exe" "N" &gt;nul) || (start "" /b "C:\...\Pdv.exe" --quiosque &amp; exit) &amp; "...\ping.exe" -n 2 127.0.0.1 &gt;nul)"</c>.
    /// Número, caminho ou pasta fora do formato: lança, e nada é montado.
    /// </summary>
    public static string MontarComando(int pidDoInstalador, string alvo, bool quiosque, string pastaDoSistema)
    {
        if (pidDoInstalador <= 0) throw new ArgumentOutOfRangeException(nameof(pidDoInstalador), "número de processo inválido");
        if (!CaminhoAceito(alvo)) throw new ArgumentException("caminho do caixa fora do formato", nameof(alvo));
        if (!TravaDoCtrlAltDel.PastaDoSistema(pastaDoSistema)) throw new ArgumentException("pasta do sistema fora do formato", nameof(pastaDoSistema));
        var pid = pidDoInstalador.ToString(CultureInfo.InvariantCulture);
        string DoSistema(string programa) => "\"" + pastaDoSistema + @"\" + programa + "\"";
        var abrir = "start \"\" /b \"" + alvo + "\"" + (quiosque ? " " + DesfazerQuiosque.MarcaDoQuiosque : "");
        return TravaDoCtrlAltDel.ChavesDoCmd + " \"for /l %i in (1,1," + Voltas.ToString(CultureInfo.InvariantCulture) + ") do @(("
            + DoSistema("tasklist.exe") + " /fi \"PID eq " + pid + "\" /nh | " + DoSistema("find.exe") + " \"" + pid + "\" >nul) || ("
            + abrir + " & exit) & " + DoSistema("ping.exe") + " -n 2 127.0.0.1 >nul)\"";
    }
}
