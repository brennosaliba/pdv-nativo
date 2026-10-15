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
/// A espera: a cada volta o tasklist procura o instalador e o PRÓPRIO caixa (pelo número e pelo nome
/// da imagem, para um número reaproveitado por outro programa não segurar a espera); os dois fora,
/// abre o caixa e sai. Esperar o caixa também é o que impede o caixa novo de morrer na trava de
/// instância única quando o anterior demora a sair (fechamento da 1.0.36). O instalador que falha
/// mostra a mensagem dele e, depois do OK, o caixa abre de novo. Cada volta leva de 1 a 2 s (o tasklist
/// mais 1 s de ping; medido 1,66 s): <see cref="Voltas"/> cobre uma noite com a mensagem do instalador
/// esperando alguém. Passado isso o cmd desiste sem abrir (abrir no meio da cópia seria pior).
///
/// O tasklist lê os processos pelo WMI. Num PC com o WMI quebrado ele não acha nada, e a espera
/// abriria o caixa na hora, com o instalador ainda copiando. Por isso, antes de mandar
/// <see cref="ArgumentoSemAbrir"/>, o caixa prova a espera nele mesmo (<see cref="EsperaAcha"/>): o
/// tasklist tem que achar o próprio caixa. Não achou, o instalador reabre, como num caixa antigo.
/// </summary>
public static class ReabrirOCaixa
{
    public const string ArgumentoAtualizar = "--atualizar";

    /// <summary>O caixa avisa o instalador: "quem reabre sou eu, não abra nada".</summary>
    public const string ArgumentoSemAbrir = "--sem-abrir";

    /// <summary>
    /// Quantas voltas o cmd espera o instalador e o caixa sair. Cada volta leva de 1 a 2 s (medido 1,66 s
    /// neste PC): 25 000 voltas dão de 7 a 14 h, uma noite inteira com a mensagem de erro do instalador
    /// esperando o OK. Eram 3 600 (o comentário dizia ~1 h; na prática ~99 min).
    /// </summary>
    public const int Voltas = 25000;

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
    /// O nome da imagem de um processo pode entrar no filtro do tasklist? Só um nome de arquivo .exe
    /// simples (letras, números, espaço, ponto, hífen, sublinhado), sem pasta e sem nada que o cmd interprete.
    /// </summary>
    public static bool ImagemAceita(string? imagem)
        => imagem is { Length: > 4 and <= 100 }
           && imagem.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
           && imagem.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '-' or '_')
           && imagem[0] != ' ' && imagem[^1] != ' ';

    /// <summary>
    /// Por que o caixa NÃO pode se reabrir sozinho (null = pode): caminho do caixa fora do formato, pasta
    /// do sistema fora do formato, faltando o caixa ou um dos programas da espera, ou a espera que não
    /// acha o próprio caixa (<paramref name="esperaAcha"/>: o tasklist sem WMI, ou o Windows que não deixa
    /// o cmd abrir). Aí o caixa chama o instalador sem <see cref="ArgumentoSemAbrir"/>, como antes.
    /// </summary>
    public static string? MotivoParaNaoReabrir(string? alvo, string? pastaDoSistema, Func<string, bool> existe, Func<bool>? esperaAcha = null)
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
        if (esperaAcha is not null)
        {
            try { if (!esperaAcha()) return "a espera não achou o próprio caixa (tasklist sem resposta)"; }
            catch (Exception ex) { return "a espera não abriu: " + ex.GetType().Name; }
        }
        return null;
    }

    /// <summary>
    /// A PROVA DA ESPERA, no próprio caixa: roda num cmd comum a MESMA procura que a espera faz (tasklist
    /// pelo número e pelo nome, find pelo número) para o processo <paramref name="pid"/> do exe
    /// <paramref name="exe"/>, e diz se achou. O caixa está vivo, então tem que achar: não achar é o
    /// tasklist sem WMI, e aí a espera abriria o caixa no meio da cópia. Até 15 s. Nunca lança.
    /// </summary>
    public static bool EsperaAcha(string pastaDoSistema, int pid, string? exe)
    {
        try
        {
            if (pid <= 0 || !TravaDoCtrlAltDel.PastaDoSistema(pastaDoSistema)) return false;
            var imagem = Path.GetFileName(exe ?? "");
            if (!ImagemAceita(imagem)) return false;
            var linha = TravaDoCtrlAltDel.ChavesDoCmd + " \"" + Procura(pid, imagem, pastaDoSistema) + "\"";
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(TravaDoCtrlAltDel.CaminhoDoCmd(pastaDoSistema), linha)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                WorkingDirectory = pastaDoSistema,
            });
            if (p is null) return false;
            if (!p.WaitForExit(15_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>A procura de um processo: tasklist pelo número (e pelo nome, quando dado) e find pelo número. Sai 0 quando acha.</summary>
    private static string Procura(int pid, string? imagem, string pastaDoSistema)
    {
        var n = pid.ToString(CultureInfo.InvariantCulture);
        return DoSistema(pastaDoSistema, "tasklist.exe") + " /fi \"PID eq " + n + "\""
            + (imagem is null ? "" : " /fi \"IMAGENAME eq " + imagem + "\"")
            + " /nh | " + DoSistema(pastaDoSistema, "find.exe") + " \"" + n + "\" >nul";
    }

    /// <summary>Um programa de System32 entre aspas, pelo caminho absoluto.</summary>
    private static string DoSistema(string pastaDoSistema, string programa) => "\"" + pastaDoSistema + @"\" + programa + "\"";

    /// <summary>
    /// A linha do cmd que espera o instalador E o próprio caixa saírem e reabre o caixa:
    /// <c>/d /v:off /s /c "for /l %i in (1,1,25000) do @(("...\tasklist.exe" /fi "PID eq I" /nh | "...\find.exe" "I" &gt;nul) || ("...\tasklist.exe" /fi "PID eq C" /fi "IMAGENAME eq Pdv.exe" /nh | "...\find.exe" "C" &gt;nul) || (start "" /b "C:\...\Pdv.exe" --quiosque &amp; exit) &amp; "...\ping.exe" -n 2 127.0.0.1 &gt;nul)"</c>.
    /// Número, nome, caminho ou pasta fora do formato: lança, e nada é montado.
    /// </summary>
    public static string MontarComando(int pidDoInstalador, int pidDoCaixa, string imagemDoCaixa, string alvo, bool quiosque, string pastaDoSistema)
        => Montar(new[] { (pidDoInstalador, (string?)null), (pidDoCaixa, imagemDoCaixa) }, alvo, quiosque, pastaDoSistema);

    /// <summary>
    /// O "Reiniciar o PDV" do quiosque (fechamento da 1.0.36): a mesma espera, só pelo próprio caixa. Antes
    /// eram 2 s fixos, e um caixa que demorava mais para sair (o encerramento do TEF) fazia o novo morrer
    /// na trava de instância única: tela preta.
    /// </summary>
    public static string MontarReinicio(int pidDoCaixa, string imagemDoCaixa, string alvo, bool quiosque, string pastaDoSistema)
        => Montar(new[] { (pidDoCaixa, (string?)imagemDoCaixa) }, alvo, quiosque, pastaDoSistema);

    private static string Montar(IReadOnlyList<(int Pid, string? Imagem)> esperar, string alvo, bool quiosque, string pastaDoSistema)
    {
        foreach (var (pid, imagem) in esperar)
        {
            if (pid <= 0) throw new ArgumentOutOfRangeException(nameof(esperar), "número de processo inválido");
            if (imagem is not null && !ImagemAceita(imagem)) throw new ArgumentException("nome do processo fora do formato", nameof(esperar));
        }
        if (!CaminhoAceito(alvo)) throw new ArgumentException("caminho do caixa fora do formato", nameof(alvo));
        if (!TravaDoCtrlAltDel.PastaDoSistema(pastaDoSistema)) throw new ArgumentException("pasta do sistema fora do formato", nameof(pastaDoSistema));
        var abrir = "start \"\" /b \"" + alvo + "\"" + (quiosque ? " " + DesfazerQuiosque.MarcaDoQuiosque : "");
        var procuras = string.Concat(esperar.Select(e => "(" + Procura(e.Pid, e.Imagem, pastaDoSistema) + ") || "));
        return TravaDoCtrlAltDel.ChavesDoCmd + " \"for /l %i in (1,1," + Voltas.ToString(CultureInfo.InvariantCulture) + ") do @("
            + procuras + "(" + abrir + " & exit) & " + DoSistema(pastaDoSistema, "ping.exe") + " -n 2 127.0.0.1 >nul)\"";
    }
}
