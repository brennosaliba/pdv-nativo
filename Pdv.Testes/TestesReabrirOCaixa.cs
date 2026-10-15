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
/// Fechamento da 1.0.36 (achados da revisão):
///  RC-3c    a prova da espera no próprio caixa (o tasklist tem que achar o caixa; sem WMI não acha, e
///           aí o caixa não manda --sem-abrir)
///  RC-4e/5d o "Reiniciar o PDV" do quiosque espera o processo velho de verdade (eram 2 s fixos)
///  RC-5     a espera também aguarda o PRÓPRIO caixa sair (senão o caixa novo morria na trava de
///           instância única), pelo número e pelo nome
///  RC-8..9  o fonte do Reiniciar o PDV, e as voltas que cobrem uma noite (o comentário dizia ~1 h)
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

        // ── RC-3c a prova da espera, de verdade: o tasklist acha este processo pelo número E pelo nome ──
        var eu = Environment.ProcessPath ?? "";
        var achaEu = ReabrirOCaixa.EsperaAcha(Environment.SystemDirectory, Environment.ProcessId, eu);
        int pidMorto;
        using (var curto = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 1 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
        {
            _ = curto.StandardOutput.ReadToEndAsync();
            curto.WaitForExit(10_000);
            pidMorto = curto.Id;
        }
        var achaMorto = ReabrirOCaixa.EsperaAcha(Environment.SystemDirectory, pidMorto, Path.Combine(Environment.SystemDirectory, "ping.exe"));
        var achaOutroNome = ReabrirOCaixa.EsperaAcha(Environment.SystemDirectory, Environment.ProcessId, Path.Combine(Environment.SystemDirectory, "ping.exe"));
        checar(achaEu && !achaMorto && !achaOutroNome
               && !ReabrirOCaixa.EsperaAcha(@"C:\Users\caixa\system32", Environment.ProcessId, eu)
               && !ReabrirOCaixa.EsperaAcha(Environment.SystemDirectory, 0, eu)
               && !ReabrirOCaixa.EsperaAcha(Environment.SystemDirectory, Environment.ProcessId, @"C:\a\Pdv&calc.exe"),
            $"RC-3c a prova da espera acha este processo pelo número e pelo nome ({achaEu}); um processo que saiu ({achaMorto}), outro nome no mesmo número ({achaOutroNome}), pasta ou nome fora do formato: não acha");
        checar(ReabrirOCaixa.MotivoParaNaoReabrir(Caixa, Sys, tudo, () => true) is null
               && ReabrirOCaixa.MotivoParaNaoReabrir(Caixa, Sys, tudo, () => false)?.Contains("tasklist", StringComparison.Ordinal) == true
               && ReabrirOCaixa.MotivoParaNaoReabrir(Caixa, Sys, tudo, () => throw new System.ComponentModel.Win32Exception(5)) is not null
               && ReabrirOCaixa.MotivoParaNaoReabrir(@"C:\a&b\Pdv.exe", Sys, tudo, () => throw new InvalidOperationException("não era para provar")) is not null,
            "RC-3d a espera que não acha o próprio caixa (tasklist sem WMI, cmd barrado): o caixa NÃO manda --sem-abrir, o instalador reabre");
        checar(ReabrirOCaixa.ImagemAceita("Pdv.exe") && ReabrirOCaixa.ImagemAceita("Pdv.Testes.exe") && ReabrirOCaixa.ImagemAceita("InstalarPdv-1.0.36.exe")
               && new[] { null, "", "Pdv", "Pdv.cmd", @"a\Pdv.exe", "Pdv&x.exe", "Pdv\".exe", "Pdv%x%.exe", " Pdv.exe", "Pdv|x.exe", new string('a', 120) + ".exe" }.All(i => !ReabrirOCaixa.ImagemAceita(i)),
            "RC-3e o nome do processo no filtro do tasklist: só nome de .exe simples, nada que o cmd interprete");

        // ── RC-4 a linha do cmd, montada à mão aqui ──
        var esperado = "/d /v:off /s /c \"for /l %i in (1,1,25000) do @((\"" + Sys + "\\tasklist.exe\" /fi \"PID eq 4321\" /nh | \""
            + Sys + "\\find.exe\" \"4321\" >nul) || (\"" + Sys + "\\tasklist.exe\" /fi \"PID eq 8765\" /fi \"IMAGENAME eq Pdv.exe\" /nh | \""
            + Sys + "\\find.exe\" \"8765\" >nul) || (start \"\" /b \"" + Caixa + "\" --quiosque & exit) & \"" + Sys + "\\ping.exe\" -n 2 127.0.0.1 >nul)\"";
        var linha = ReabrirOCaixa.MontarComando(4321, 8765, "Pdv.exe", Caixa, quiosque: true, Sys);
        checar(linha == esperado, "RC-4 a linha: a cada volta procura o instalador (número) e o próprio caixa (número e nome); os dois fora, abre o caixa e sai" + (linha == esperado ? "" : " [saiu: " + linha + "]"));
        var semQuiosque = ReabrirOCaixa.MontarComando(4321, 8765, "Pdv.exe", Caixa, quiosque: false, Sys);
        checar(semQuiosque.Contains("start \"\" /b \"" + Caixa + "\" & exit", StringComparison.Ordinal) && !semQuiosque.Contains("--quiosque", StringComparison.Ordinal),
            "RC-4b fora do quiosque reabre sem o --quiosque");
        checar(Lanca(() => ReabrirOCaixa.MontarComando(0, 8765, "Pdv.exe", Caixa, true, Sys)) && Lanca(() => ReabrirOCaixa.MontarComando(-5, 8765, "Pdv.exe", Caixa, true, Sys))
               && Lanca(() => ReabrirOCaixa.MontarComando(4321, 0, "Pdv.exe", Caixa, true, Sys)) && Lanca(() => ReabrirOCaixa.MontarComando(4321, 8765, "Pdv&calc.exe", Caixa, true, Sys))
               && Lanca(() => ReabrirOCaixa.MontarComando(4321, 8765, @"C:\x\Pdv.exe", Caixa, true, Sys)) && Lanca(() => ReabrirOCaixa.MontarComando(4321, 8765, "Pdv\".exe", Caixa, true, Sys))
               && Lanca(() => ReabrirOCaixa.MontarComando(1, 2, "Pdv.exe", @"C:\a&calc\Pdv.exe", true, Sys)) && Lanca(() => ReabrirOCaixa.MontarComando(1, 2, "Pdv.exe", @"C:\x%PATH%\Pdv.exe", true, Sys))
               && Lanca(() => ReabrirOCaixa.MontarComando(1, 2, "Pdv.exe", Caixa, true, @"C:\Users\caixa\system32")) && Lanca(() => ReabrirOCaixa.MontarComando(1, 2, "Pdv.exe", Caixa, true, "C:\\Win&x\\system32")),
            "RC-4c número inválido (do instalador ou do caixa), nome do caixa fora do formato, caminho com & ou % e pasta do sistema fora do formato: lança, nada é montado");
        checar(linha.StartsWith(TravaDoCtrlAltDel.ChavesDoCmd + " \"", StringComparison.Ordinal) && linha.IndexOfAny(new[] { '!', '^', '\n', '\r' }) < 0
               && Contar(linha, "%") == 1 && Contar(linha, " | ") == 2 && Contar(linha, " || ") == 2 && Contar(linha, "|") == 6 && Contar(linha, ">nul") == 3,
            "RC-4d sem AutoRun (/d), sem expansão atrasada (/v:off), um só % (a variável do for), um pipe por procura e as três saídas para nul");
        var reinicio = ReabrirOCaixa.MontarReinicio(8765, "Pdv.exe", Caixa, quiosque: true, Sys);
        var esperadoReinicio = "/d /v:off /s /c \"for /l %i in (1,1,25000) do @((\"" + Sys + "\\tasklist.exe\" /fi \"PID eq 8765\" /fi \"IMAGENAME eq Pdv.exe\" /nh | \""
            + Sys + "\\find.exe\" \"8765\" >nul) || (start \"\" /b \"" + Caixa + "\" --quiosque & exit) & \"" + Sys + "\\ping.exe\" -n 2 127.0.0.1 >nul)\"";
        checar(reinicio == esperadoReinicio && Lanca(() => ReabrirOCaixa.MontarReinicio(0, "Pdv.exe", Caixa, true, Sys))
               && Lanca(() => ReabrirOCaixa.MontarReinicio(8765, "Pdv|x.exe", Caixa, true, Sys)),
            "RC-4e o Reiniciar o PDV do quiosque: a mesma espera, só pelo próprio caixa (número e nome)" + (reinicio == esperadoReinicio ? "" : " [saiu: " + reinicio + "]"));

        // ── RC-5 a linha de VERDADE num cmd comum (sem administrador), com um instalador, um caixa velho e um caixa de mentira ──
        var sys = Environment.SystemDirectory;
        var pasta = Path.Combine(Path.GetTempPath(), "pdv-reabrir-" + Guid.NewGuid().ToString("N")[..8]);
        Process? instalador = null, caixaVelho = null, espera = null, reinicio_ = null, caixaReinicio = null;
        Process? Ping(int n)
        {
            var p = Process.Start(new ProcessStartInfo(Path.Combine(sys, "ping.exe"), "-n " + n + " 127.0.0.1")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            if (p is not null) _ = p.StandardOutput.ReadToEndAsync();
            return p;
        }
        try
        {
            Directory.CreateDirectory(pasta);
            var alvo = Path.Combine(pasta, "caixa de mentira.cmd");
            var marca = Path.Combine(pasta, "abriu.txt");
            File.WriteAllText(alvo, "@echo [%~1]>\"%~dp0abriu.txt\"\r\n");
            // o "instalador": um ping que vive ~3 s; o "caixa velho", lento para sair: outro que vive ~7 s
            instalador = Ping(4);
            caixaVelho = Ping(8);
            if (instalador is null || caixaVelho is null || ReabrirOCaixa.MotivoParaNaoReabrir(alvo, sys, File.Exists) is { } motivo)
            {
                checar(false, "RC-5 não deu para montar o ensaio neste PC");
            }
            else
            {
                espera = Process.Start(new ProcessStartInfo(TravaDoCtrlAltDel.CaminhoDoCmd(sys),
                    ReabrirOCaixa.MontarComando(instalador.Id, caixaVelho.Id, "ping.exe", alvo, true, sys))
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = sys });
                Thread.Sleep(1200);
                var cedo = File.Exists(marca);
                var vivo = !instalador.HasExited;
                instalador.WaitForExit(15_000);
                Thread.Sleep(2000);
                var comOVelho = File.Exists(marca);
                var velhoVivo = !caixaVelho.HasExited;
                caixaVelho.WaitForExit(15_000);
                var abriu = false;
                for (var i = 0; i < 60 && !abriu; i++) { Thread.Sleep(250); abriu = File.Exists(marca); }
                var texto = abriu ? File.ReadAllText(marca).Trim() : "";
                var esperaSaiu = espera?.WaitForExit(10_000) == true;
                checar(vivo && !cedo, "RC-5 com o instalador de pé, a espera NÃO abre o caixa");
                checar(velhoVivo && !comOVelho,
                    "RC-5a o instalador saiu mas o caixa velho ainda não: a espera NÃO abre (o novo morreria na trava de instância única)");
                checar(abriu && texto == "[--quiosque]" && esperaSaiu,
                    "RC-5b os dois saíram: a espera abre o caixa com --quiosque e sai (" + (abriu ? texto : "não abriu") + ")");

                // RC-5d o Reiniciar o PDV: só o próprio caixa
                File.Delete(marca);
                caixaReinicio = Ping(4);
                reinicio_ = Process.Start(new ProcessStartInfo(TravaDoCtrlAltDel.CaminhoDoCmd(sys),
                    ReabrirOCaixa.MontarReinicio(caixaReinicio!.Id, "ping.exe", alvo, true, sys))
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = sys });
                Thread.Sleep(1200);
                var cedoReinicio = File.Exists(marca);
                var vivoReinicio = !caixaReinicio.HasExited;
                caixaReinicio.WaitForExit(15_000);
                var abriuReinicio = false;
                for (var i = 0; i < 60 && !abriuReinicio; i++) { Thread.Sleep(250); abriuReinicio = File.Exists(marca); }
                checar(vivoReinicio && !cedoReinicio && abriuReinicio && reinicio_?.WaitForExit(10_000) == true,
                    "RC-5d o Reiniciar o PDV espera o processo velho sair de verdade e só então abre o caixa");
            }
        }
        finally
        {
            foreach (var p in new[] { espera, reinicio_ })
                try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch { }
            foreach (var p in new[] { instalador, caixaVelho, caixaReinicio })
                try { if (p is { HasExited: false }) p.Kill(); } catch { }
            foreach (var p in new[] { espera, reinicio_, instalador, caixaVelho, caixaReinicio }) p?.Dispose();
            for (var i = 0; i < 20 && Directory.Exists(pasta); i++)
            {
                try { Directory.Delete(pasta, recursive: true); }
                catch { Thread.Sleep(100); }
            }
        }
        checar(!Directory.Exists(pasta), "RC-5c a pasta do ensaio foi apagada");

        // ── RC-6 o fonte do caixa ──
        var atualizar = Fonte("Telas", "AtualizarCaixa.cs") ?? "";
        var entregar = Corpo(atualizar, "public static string? EntregarAoInstalador(string exe, Atualizacao.Manifesto m, out bool fechar)");
        var iMotivo = entregar.IndexOf("ReabrirOCaixa.MotivoParaNaoReabrir(caixa, sistema, System.IO.File.Exists,\n            () => ReabrirOCaixa.EsperaAcha(sistema, Environment.ProcessId, caixa!));", StringComparison.Ordinal);
        var iTrava = entregar.IndexOf("using var conferido = Atualizacao.TravarConferido(exe, m, out var naoConfere);", StringComparison.Ordinal);
        var iInstalador = entregar.IndexOf("Arguments = ReabrirOCaixa.ArgumentosDoInstalador(caixaReabre: naoReabre is null)", StringComparison.Ordinal);
        var iEspera = entregar.IndexOf("ReabrirOCaixa.MontarComando(p.Id, Environment.ProcessId, System.IO.Path.GetFileName(caixa!), caixa!, Quiosque.Ligado, sistema)", StringComparison.Ordinal);
        checar(iMotivo > 0 && iTrava > iMotivo && iInstalador > iTrava && iEspera > iInstalador
               && entregar.Contains("if (conferido is null) return naoConfere", StringComparison.Ordinal)
               && entregar.Contains("new ProcessStartInfo(TravaDoCtrlAltDel.CaminhoDoCmd(sistema),", StringComparison.Ordinal)
               && entregar.Contains("var sistema = Environment.SystemDirectory;", StringComparison.Ordinal)
               && entregar.Contains("var caixa = Environment.ProcessPath;", StringComparison.Ordinal)
               && entregar.Contains("fechar = espera is not null;", StringComparison.Ordinal),
            "RC-6 o caixa decide antes se pode se reabrir, provando a espera nele mesmo (e só então manda --sem-abrir); o instalador baixado fica preso e conferido até abrir; a espera é o cmd de System32 com a linha do núcleo, aguarda também este processo, e só com ela de pé o caixa fecha");
        var espera_ = entregar.Length == 0 ? "" : entregar[Math.Max(0, iEspera - 400)..];
        checar(!entregar.Contains("runas", StringComparison.OrdinalIgnoreCase) && Contar(entregar, "UseShellExecute = true") == 1
               && espera_.Contains("UseShellExecute = false", StringComparison.Ordinal) && espera_.Contains("CreateNoWindow = true", StringComparison.Ordinal),
            "RC-6b a espera roda como o próprio caixa (UseShellExecute = false, sem runas, escondida): só o instalador pede o administrador, pelo manifesto dele");
        var iManual = atualizar.IndexOf("var erro = EntregarAoInstalador(baixa.Caminho!, v.Manifesto!, out var fechar);", StringComparison.Ordinal);
        var manual = iManual < 0 ? "" : atualizar[iManual..Math.Min(atualizar.Length, iManual + 2500)];
        var iSozinho = atualizar.IndexOf("var erro = EntregarAoInstalador(pronto!, m, out var fechar);", StringComparison.Ordinal);
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

        // ── RC-8 o Reiniciar o PDV do quiosque usa a espera de verdade ──
        var quiosque = Fonte("Quiosque.cs") ?? "";
        var reiniciar = Corpo(quiosque, "public static void ReiniciarPdv()");
        var iProva = reiniciar.IndexOf("ReabrirOCaixa.MotivoParaNaoReabrir(exe, sistema, File.Exists,", StringComparison.Ordinal);
        var iMontar = reiniciar.IndexOf("ReabrirOCaixa.MontarReinicio(Environment.ProcessId, Path.GetFileName(exe), exe, quiosque: true, sistema)", StringComparison.Ordinal);
        var iAntigo = reiniciar.IndexOf("ping -n 3 127.0.0.1", StringComparison.Ordinal);
        checar(iProva > 0 && iMontar > iProva && iAntigo > iMontar && reiniciar.Contains("ReabrirOCaixa.EsperaAcha(sistema, Environment.ProcessId, exe)", StringComparison.Ordinal)
               && reiniciar.Contains("TravaDoCtrlAltDel.CaminhoDoCmd(sistema)", StringComparison.Ordinal) && !reiniciar.Contains("\"cmd.exe\"", StringComparison.Ordinal),
            "RC-8 o Reiniciar o PDV prova a espera e espera este processo sair de verdade; só sem a prova volta aos 2 s de antes (e o cmd é o de System32)");

        // ── RC-9 as voltas cobrem uma noite (uma volta leva de 1 a 2 s) ──
        var doc = Fonte("Pdv.Nucleo", "ReabrirOCaixa.cs") ?? "";
        checar(ReabrirOCaixa.Voltas >= 21_600 && !doc.Contains("perto de uma hora", StringComparison.Ordinal) && !doc.Contains("~1 h:", StringComparison.Ordinal),
            $"RC-9 a espera desiste só depois de {ReabrirOCaixa.Voltas} voltas (6 h ou mais, a 1 s por volta no mínimo); o comentário não promete ~1 h");
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
