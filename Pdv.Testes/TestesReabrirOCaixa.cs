using System.Diagnostics;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// QUEM REABRE O CAIXA DEPOIS DA ATUALIZAÇÃO (1.0.36, revisão de segurança da trava).
/// O instalador roda como administrador; no quiosque (sem Explorer) ele abria o Pdv.exe direto e o
/// caixa passava o turno inteiro como administrador. Agora o próprio caixa deixa um cmd COMUM
/// esperando o instalador sair e se reabre sozinho; o instalador recebe --sem-abrir.
///
///  RC-1..4  a regra pura (Pdv.Nucleo/ReabrirOCaixa): os argumentos do instalador, o caminho do
///           caixa aceito, quando NÃO dá para reabrir, a linha do cmd
///  RC-5     a linha de VERDADE num cmd comum: um processo de mentira no lugar do instalador e um
///           .cmd de mentira no lugar do caixa; só abre depois que o "instalador" sai, com --quiosque
///  RC-6..7  o fonte: o caixa passa --sem-abrir só quando deixa a espera de pé, nunca pede
///           administrador para ela; o instalador só abre sem o --sem-abrir
/// Nenhuma prova abre o aviso do Windows nem abre o caixa de verdade.
/// </summary>
public static class TestesReabrirOCaixa
{
    private const string Sys = @"C:\WINDOWS\system32";
    private const string Caixa = @"C:\Program Files\MMFood\Pdv.exe";

    public static void Rodar(Action<bool, string> checar)
    {
        // ── RC-1 os argumentos do instalador ──
        checar(ReabrirOCaixa.ArgumentosDoInstalador(true) == "--atualizar --sem-abrir"
               && ReabrirOCaixa.ArgumentosDoInstalador(false) == "--atualizar"
               && !ReabrirOCaixa.InstaladorAbre(new[] { "--atualizar", "--sem-abrir" })
               && ReabrirOCaixa.InstaladorAbre(new[] { "--atualizar" }) && ReabrirOCaixa.InstaladorAbre(Array.Empty<string>()),
            "RC-1 o caixa que se reabre manda --atualizar --sem-abrir; o instalador só abre o caixa sem o --sem-abrir (caixa antigo)");

        // ── RC-2 o caminho do caixa que pode entrar na linha do cmd ──
        string?[] ruins =
        {
            null, "", " ", "Pdv.exe", @"Pdv\Pdv.exe", @"C:Pdv.exe", @"\\servidor\pdv\Pdv.exe", "//servidor/pdv/Pdv.exe",
            @"C:\Pro""gram\Pdv.exe", @"C:\100%\Pdv.exe", @"C:\a!b\Pdv.exe", @"C:\a^b\Pdv.exe", @"C:\a&calc\Pdv.exe",
            @"C:\a|b\Pdv.exe", @"C:\a<b\Pdv.exe", @"C:\a>b\Pdv.exe", "C:\\a\nb\\Pdv.exe", "C:\\a\tb\\Pdv.exe", @"C:\" + new string('a', 420) + @"\Pdv.exe",
        };
        var passaram = ruins.Where(ReabrirOCaixa.CaminhoAceito).Select(c => (c ?? "null").Replace("\n", "\\n")).ToList();
        checar(passaram.Count == 0
               && ReabrirOCaixa.CaminhoAceito(Caixa) && ReabrirOCaixa.CaminhoAceito(@"C:\Program Files (x86)\PDV MMTech\Pdv.exe")
               && ReabrirOCaixa.CaminhoAceito(@"D:\Caixa São José\Pdv.exe"),
            "RC-2 só caminho absoluto, local e sem nada que o cmd interprete (aspas, % ! ^ & | < >, controle)"
            + (passaram.Count == 0 ? "" : " [passaram: " + string.Join(" | ", passaram) + "]"));

        // ── RC-3 quando o caixa NÃO pode se reabrir sozinho (aí o instalador abre, como antes) ──
        var tudo = new Func<string, bool>(_ => true);
        checar(ReabrirOCaixa.MotivoParaNaoReabrir(Caixa, Sys, tudo) is null
               && ReabrirOCaixa.MotivoParaNaoReabrir(@"C:\a&b\Pdv.exe", Sys, tudo) is not null
               && ReabrirOCaixa.MotivoParaNaoReabrir(null, Sys, tudo) is not null
               && ReabrirOCaixa.MotivoParaNaoReabrir(Caixa, @"C:\Users\caixa\system32", tudo) is not null
               && ReabrirOCaixa.MotivoParaNaoReabrir(Caixa, Sys, c => c != Caixa) is not null
               && ReabrirOCaixa.MotivoParaNaoReabrir(Caixa, Sys, c => !c.EndsWith(@"\find.exe", StringComparison.Ordinal))?.Contains("find.exe", StringComparison.Ordinal) == true
               && ReabrirOCaixa.MotivoParaNaoReabrir(Caixa, Sys, _ => throw new IOException("disco")) is not null
               && ReabrirOCaixa.ProgramasDaEspera.SequenceEqual(new[] { "cmd.exe", "tasklist.exe", "find.exe", "ping.exe" }),
            "RC-3 caminho ou pasta do sistema fora do formato, caixa ou cmd/tasklist/find/ping faltando, ou conferência que lança: não se reabre sozinho");
        checar(ReabrirOCaixa.MotivoParaNaoReabrir(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Environment.SystemDirectory, File.Exists) is null,
            "RC-3b neste PC os programas da espera estão em " + Environment.SystemDirectory);

        // ── RC-4 a linha do cmd, montada à mão aqui ──
        var esperado = "/d /v:off /s /c \"for /l %i in (1,1,3600) do @((\"" + Sys + "\\tasklist.exe\" /fi \"PID eq 4321\" /nh | \""
            + Sys + "\\find.exe\" \"4321\" >nul) || (start \"\" /b \"" + Caixa + "\" --quiosque & exit) & \"" + Sys + "\\ping.exe\" -n 2 127.0.0.1 >nul)\"";
        var linha = ReabrirOCaixa.MontarComando(4321, Caixa, quiosque: true, Sys);
        checar(linha == esperado, "RC-4 a linha: a cada ~1 s procura o instalador pelo número; sumiu, abre o caixa e sai" + (linha == esperado ? "" : " [saiu: " + linha + "]"));
        var semQuiosque = ReabrirOCaixa.MontarComando(4321, Caixa, quiosque: false, Sys);
        checar(semQuiosque.Contains("start \"\" /b \"" + Caixa + "\" & exit", StringComparison.Ordinal) && !semQuiosque.Contains("--quiosque", StringComparison.Ordinal),
            "RC-4b fora do quiosque reabre sem o --quiosque");
        checar(Lanca(() => ReabrirOCaixa.MontarComando(0, Caixa, true, Sys)) && Lanca(() => ReabrirOCaixa.MontarComando(-5, Caixa, true, Sys))
               && Lanca(() => ReabrirOCaixa.MontarComando(1, @"C:\a&calc\Pdv.exe", true, Sys)) && Lanca(() => ReabrirOCaixa.MontarComando(1, @"C:\x%PATH%\Pdv.exe", true, Sys))
               && Lanca(() => ReabrirOCaixa.MontarComando(1, Caixa, true, @"C:\Users\caixa\system32")) && Lanca(() => ReabrirOCaixa.MontarComando(1, Caixa, true, "C:\\Win&x\\system32")),
            "RC-4c número inválido, caminho com & ou % e pasta do sistema fora do formato: lança, nada é montado");
        checar(linha.StartsWith(TravaDoCtrlAltDel.ChavesDoCmd + " \"", StringComparison.Ordinal) && linha.IndexOfAny(new[] { '!', '^', '\n', '\r' }) < 0
               && Contar(linha, "%") == 1 && Contar(linha, " | ") == 1 && Contar(linha, " || ") == 1 && Contar(linha, "|") == 3 && Contar(linha, ">nul") == 2,
            "RC-4d sem AutoRun (/d), sem expansão atrasada (/v:off), um só % (a variável do for), um só pipe e as duas saídas para nul");

        // ── RC-5 a linha de VERDADE num cmd comum (sem administrador), com um instalador e um caixa de mentira ──
        var sys = Environment.SystemDirectory;
        var pasta = Path.Combine(Path.GetTempPath(), "pdv-reabrir-" + Guid.NewGuid().ToString("N")[..8]);
        Process? instalador = null, espera = null;
        try
        {
            Directory.CreateDirectory(pasta);
            var alvo = Path.Combine(pasta, "caixa de mentira.cmd");
            var marca = Path.Combine(pasta, "abriu.txt");
            File.WriteAllText(alvo, "@echo [%~1]>\"%~dp0abriu.txt\"\r\n");
            // o "instalador": um ping que vive ~3 s
            instalador = Process.Start(new ProcessStartInfo(Path.Combine(sys, "ping.exe"), "-n 4 127.0.0.1")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            if (instalador is null || ReabrirOCaixa.MotivoParaNaoReabrir(alvo, sys, File.Exists) is { } motivo)
            {
                checar(false, "RC-5 não deu para montar o ensaio neste PC");
            }
            else
            {
                _ = instalador.StandardOutput.ReadToEndAsync();
                espera = Process.Start(new ProcessStartInfo(TravaDoCtrlAltDel.CaminhoDoCmd(sys), ReabrirOCaixa.MontarComando(instalador.Id, alvo, true, sys))
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = sys });
                Thread.Sleep(1200);
                var cedo = File.Exists(marca);
                var vivo = !instalador.HasExited;
                instalador.WaitForExit(15_000);
                var abriu = false;
                for (var i = 0; i < 60 && !abriu; i++) { Thread.Sleep(250); abriu = File.Exists(marca); }
                var texto = abriu ? File.ReadAllText(marca).Trim() : "";
                var esperaSaiu = espera?.WaitForExit(10_000) == true;
                checar(vivo && !cedo, "RC-5 com o instalador de pé, a espera NÃO abre o caixa");
                checar(abriu && texto == "[--quiosque]" && esperaSaiu,
                    "RC-5b o instalador saiu: a espera abre o caixa com --quiosque e sai (" + (abriu ? texto : "não abriu") + ")");
            }
        }
        finally
        {
            try { if (espera is { HasExited: false }) espera.Kill(entireProcessTree: true); } catch { }
            try { if (instalador is { HasExited: false }) instalador.Kill(); } catch { }
            espera?.Dispose();
            instalador?.Dispose();
            for (var i = 0; i < 20 && Directory.Exists(pasta); i++)
            {
                try { Directory.Delete(pasta, recursive: true); }
                catch { Thread.Sleep(100); }
            }
        }
        checar(!Directory.Exists(pasta), "RC-5c a pasta do ensaio foi apagada");

        // ── RC-6 o fonte do caixa ──
        var atualizar = Fonte("Telas", "AtualizarCaixa.cs") ?? "";
        var entregar = Corpo(atualizar, "public static string? EntregarAoInstalador(string exe, out bool fechar)");
        var iMotivo = entregar.IndexOf("ReabrirOCaixa.MotivoParaNaoReabrir(caixa, sistema, System.IO.File.Exists)", StringComparison.Ordinal);
        var iInstalador = entregar.IndexOf("Arguments = ReabrirOCaixa.ArgumentosDoInstalador(caixaReabre: naoReabre is null)", StringComparison.Ordinal);
        var iEspera = entregar.IndexOf("ReabrirOCaixa.MontarComando(p.Id, caixa!, Quiosque.Ligado, sistema)", StringComparison.Ordinal);
        checar(iMotivo > 0 && iInstalador > iMotivo && iEspera > iInstalador
               && entregar.Contains("new ProcessStartInfo(TravaDoCtrlAltDel.CaminhoDoCmd(sistema),", StringComparison.Ordinal)
               && entregar.Contains("var sistema = Environment.SystemDirectory;", StringComparison.Ordinal)
               && entregar.Contains("var caixa = Environment.ProcessPath;", StringComparison.Ordinal)
               && entregar.Contains("fechar = espera is not null;", StringComparison.Ordinal),
            "RC-6 o caixa decide antes se pode se reabrir (e só então manda --sem-abrir); a espera é o cmd de System32 com a linha do núcleo, e só com ela de pé o caixa fecha");
        var espera_ = entregar.Length == 0 ? "" : entregar[Math.Max(0, iEspera - 400)..];
        checar(!entregar.Contains("runas", StringComparison.OrdinalIgnoreCase) && Contar(entregar, "UseShellExecute = true") == 1
               && espera_.Contains("UseShellExecute = false", StringComparison.Ordinal) && espera_.Contains("CreateNoWindow = true", StringComparison.Ordinal),
            "RC-6b a espera roda como o próprio caixa (UseShellExecute = false, sem runas, escondida): só o instalador pede o administrador, pelo manifesto dele");
        var iManual = atualizar.IndexOf("var erro = EntregarAoInstalador(baixa.Caminho!, out var fechar);", StringComparison.Ordinal);
        var manual = iManual < 0 ? "" : atualizar[iManual..Math.Min(atualizar.Length, iManual + 2500)];
        var iSozinho = atualizar.IndexOf("var erro = EntregarAoInstalador(pronto!, out var fechar);", StringComparison.Ordinal);
        var sozinho = iSozinho < 0 ? "" : atualizar[iSozinho..Math.Min(atualizar.Length, iSozinho + 3000)];
        checar(manual.Contains("if (!fechar)", StringComparison.Ordinal) && manual.IndexOf("if (!fechar)", StringComparison.Ordinal) < manual.IndexOf("Application.Current.Shutdown();", StringComparison.Ordinal)
               && manual.Contains("Dialogo.Avisar(dono, \"Atualização\", FraseSemReabrir);", StringComparison.Ordinal)
               && sozinho.Contains("if (fechar) Application.Current?.Dispatcher.Invoke(() => Application.Current.Shutdown());", StringComparison.Ordinal),
            "RC-6c sem a espera de pé o caixa NÃO fecha (o manual avisa numa linha; a atualização sozinha só fica aberta)");
        checar(AtualizarCaixaFrase().Length <= 90 && !AtualizarCaixaFrase().Contains('\u2014') && !AtualizarCaixaFrase().Contains('\u2013'),
            "RC-6d a frase de quando o caixa fica aberto é curta e sem travessão: '" + AtualizarCaixaFrase() + "'");

        // ── RC-7 o instalador só abre o caixa sem o --sem-abrir ──
        var app = Fonte("Pdv.Instalador", "App.xaml.cs") ?? "";
        var csproj = Fonte("Pdv.Instalador", "Pdv.Instalador.csproj") ?? "";
        var iAtualizar = app.IndexOf("e.Args[0] == Pdv.Nucleo.ReabrirOCaixa.ArgumentoAtualizar", StringComparison.Ordinal);
        var iAbre = app.IndexOf("Pdv.Nucleo.ReabrirOCaixa.InstaladorAbre(e.Args)", StringComparison.Ordinal);
        var iAbrir = app.IndexOf("Instalacao.AbrirCaixa(", StringComparison.Ordinal);
        checar(iAtualizar > 0 && iAbre > iAtualizar && iAbrir > iAbre
               && csproj.Contains(@"Include=""..\Pdv.Nucleo\ReabrirOCaixa.cs""", StringComparison.Ordinal),
            "RC-7 o --atualizar só chama o AbrirCaixa quando quem chamou não mandou --sem-abrir (a regra é o MESMO arquivo dos dois lados)");
    }

    private static string AtualizarCaixaFrase() => global::Pdv.Telas.AtualizarCaixa.FraseSemReabrir;

    private static bool Lanca(Action a)
    {
        try { a(); return false; }
        catch { return true; }
    }

    private static int Contar(string texto, string pedaco)
    {
        var n = 0;
        for (var i = texto.IndexOf(pedaco, StringComparison.Ordinal); i >= 0; i = texto.IndexOf(pedaco, i + pedaco.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string Corpo(string fonte, string assinatura)
    {
        var i = fonte.IndexOf(assinatura, StringComparison.Ordinal);
        if (i < 0) return "";
        var fim = fonte.Length;
        foreach (var marca in new[] { "\n    private ", "\n    public ", "\n    internal ", "\n    /// <summary>", "\n    // ──" })
        {
            var j = fonte.IndexOf(marca, i + assinatura.Length, StringComparison.Ordinal);
            if (j >= 0 && j < fim) fim = j;
        }
        return fonte[i..fim];
    }

    private static string? Fonte(params string[] partes)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Pdv.csproj")))
            {
                var c = Path.Combine(new[] { d.FullName }.Concat(partes).ToArray());
                return File.Exists(c) ? File.ReadAllText(c).Replace("\r\n", "\n") : null;
            }
        return null;
    }
}
