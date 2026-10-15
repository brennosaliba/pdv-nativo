using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Pdv.Instalador;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// FECHAMENTO DA 1.0.36 (15/10/2026): os achados da revisão da troca de versão, no instalador e na entrega
/// do caixa. Os do cmd que reabre o caixa estão em TestesReabrirOCaixa (RC-3c..9) e o do navegador do caixa
/// que ficou aberto em TestesPerfilEmUso (RP-10b/c).
///  TV-1  reiniciar para abrir: depois do "Reinicie o computador" a tela do quiosque ficava preta, sem dizer
///        como reiniciar. Agora é uma pergunta, e o Sim reinicia (shutdown de System32)
///  TV-2  a troca que falha: "continua na versão anterior" só antes da cópia; o caixa antigo é reaberto
///        também depois da falha (antes ninguém reabria)
///  TV-3  o assistente instala onde o caixa está (a Savassi mora em "PDV MMTech"); a migração não apaga a
///        pasta para onde aponta o Shell do quiosque
///  TV-4  o pacote é extraído numa pasta só de administrador (antes, no TEMP do usuário) e o instalador
///        prende o próprio exe enquanto vive
///  TV-5  o instalador baixado fica preso e é conferido de novo até o Windows abri-lo
///  TV-6  desinstalar com o usuário do caixa fora do Windows (outra conta de administrador): o quiosque é
///        desfeito pelo NTUSER.DAT dele, aberto como hive particular. Nunca cria um NTUSER.DAT
/// Nada aqui reinicia o PC, pede administrador nem mexe no registro de verdade: o hive dos testes é um
/// arquivo numa pasta temporária, e a HKEY_USERS de mentira é uma chave isolada em HKCU\Software\PdvNativoTeste.
/// </summary>
public static class TestesTrocaDeVersao
{
    private const string SidCaixa = "S-1-5-21-1111111111-2222222222-3333333333-2001";
    private const string SidLogado = "S-1-5-21-1111111111-2222222222-3333333333-2002";
    private const string SidSemArquivo = "S-1-5-21-1111111111-2222222222-3333333333-2003";

    public static void Rodar(Action<bool, string> checar)
    {
        var raiz = Path.Combine(Path.GetTempPath(), "pdv-testes-troca-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(raiz);
            Reiniciar(checar);
            Falha(checar, raiz);
            OndeInstalar(checar);
            Extracao(checar, raiz);
            InstaladorPreso(checar, raiz);
            PerfilFora(checar, raiz);
        }
        catch (Exception ex) { checar(false, "TV a suíte rodou até o fim, mas ESCAPOU " + ex.GetType().Name + ": " + ex.Message); }
        finally
        {
            for (var i = 0; i < 20 && Directory.Exists(raiz); i++)
            {
                try { Directory.Delete(raiz, recursive: true); }
                catch { Thread.Sleep(150); }
            }
        }
        checar(!Directory.Exists(raiz), "TV a pasta dos ensaios foi apagada");
    }

    // ── TV-1 reiniciar para abrir ─────────────────────────────────────────────────────────────
    private static void Reiniciar(Action<bool, string> checar)
    {
        var textos = new[] { Instalacao.ReinicieParaAbrir, Instalacao.ReinicieDepoisDaFalha, Instalacao.ComoReiniciar };
        checar(Instalacao.ReinicieParaAbrir.EndsWith('?') && Instalacao.ReinicieDepoisDaFalha.EndsWith('?')
               && Instalacao.ComoReiniciar.Contains("Ctrl+Alt+Del", StringComparison.Ordinal)
               && textos.All(t => t.Length <= 90 && !t.Contains('—') && !t.Contains('–')
                   && !System.Text.RegularExpressions.Regex.IsMatch(t, "(?i)administrador|explorer|elevad|uac|shutdown|quiosque")),
            "TV-1 a pergunta de reiniciar e o como reiniciar são curtos, sem travessão e sem termo técnico: " + string.Join(" | ", textos));

        var psi = Instalacao.PsiDoReinicio();
        checar(psi.FileName == Path.Combine(Environment.SystemDirectory, "shutdown.exe") && File.Exists(psi.FileName)
               && psi.ArgumentList.SequenceEqual(new[] { "/r", "/t", "0" }) && !psi.UseShellExecute && psi.CreateNoWindow,
            "TV-1b o reinício é o shutdown.exe de System32 (nunca pelo nome), /r /t 0, sem janela (só montado aqui, nunca rodado)");

        var app = Fonte("Pdv.Instalador", "App.xaml.cs");
        var pedir = Corpo(app, "private static void PedirParaReiniciar(string pergunta)");
        var iPergunta = pedir.IndexOf("MessageBox.Show(pergunta, \"Atualizar o caixa\", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);", StringComparison.Ordinal);
        var iReinicia = pedir.IndexOf("if (sim == MessageBoxResult.Yes && Instalacao.Reiniciar()) return;", StringComparison.Ordinal);
        var iComo = pedir.IndexOf("MessageBox.Show(Instalacao.ComoReiniciar,", StringComparison.Ordinal);
        checar(iPergunta > 0 && iReinicia > iPergunta && iComo > iReinicia,
            "TV-1c o instalador pergunta (Sim como padrão), reinicia só no Sim, e no Não (ou se o reinício não sai) diz como reiniciar");
        var inicio = Corpo(app, "protected override void OnStartup(StartupEventArgs e)");
        checar(inicio.Contains("PedirParaReiniciar(falha is null ? Instalacao.ReinicieParaAbrir : Instalacao.ReinicieDepoisDaFalha);", StringComparison.Ordinal)
               && inicio.IndexOf("!Instalacao.AbrirCaixa(exe)", StringComparison.Ordinal) < inicio.IndexOf("PedirParaReiniciar(", StringComparison.Ordinal)
               && !inicio.Contains("MessageBoxButton.OK, MessageBoxImage.Information);\n            Shutdown(falha", StringComparison.Ordinal),
            "TV-1d a pergunta de reiniciar só aparece quando o caixa não abriu sem administrador");
        var inst = Fonte("Pdv.Instalador", "Instalacao.cs");
        var reiniciar = Corpo(inst, "public static bool Reiniciar()");
        checar(reiniciar.Contains("Process.Start(PsiDoReinicio())", StringComparison.Ordinal) && reiniciar.Contains("p.ExitCode == 0", StringComparison.Ordinal),
            "TV-1e o Reiniciar usa o shutdown montado acima e só diz que deu quando o Windows aceitou");
    }

    // ── TV-2 a troca que falha ────────────────────────────────────────────────────────────────
    private static void Falha(Action<bool, string> checar, string raiz)
    {
        var antes = Instalacao.FraseDaFalha("o caixa não está instalado nesta máquina", mexeuNoPrograma: false);
        var depois = Instalacao.FraseDaFalha(Instalacao.AvaliarConferencia(false, -1, "", @"C:\x\Pdv.exe")!, mexeuNoPrograma: true);
        checar(antes == "Não consegui atualizar: o caixa não está instalado nesta máquina\nO caixa continua na versão anterior."
               && depois.StartsWith("Não consegui atualizar: O caixa foi copiado", StringComparison.Ordinal)
               && !depois.Contains("versão anterior", StringComparison.Ordinal),
            "TV-2 \"continua na versão anterior\" só antes da cópia; depois dela a frase não diz isso (" + depois.Replace("\n", " / ") + ")");

        var origem = Path.Combine(raiz, "origem");
        var destino = Path.Combine(raiz, "destino");
        Directory.CreateDirectory(origem);
        File.WriteAllText(Path.Combine(origem, "Pdv.exe"), "programa");
        File.WriteAllText(Path.Combine(origem, "e_sqlite3.dll"), "nativa");
        var semOrigem = Instalacao.Instalar(Sandbox(Path.Combine(raiz, "nao-existe"), destino), null, out var mexeuSemOrigem);
        var ok = Instalacao.Instalar(Sandbox(origem, destino), null, out var mexeuOk);
        // a cópia que falha no meio: no destino, uma PASTA com o nome de um dos arquivos
        var destinoRuim = Path.Combine(raiz, "destino-ruim");
        Directory.CreateDirectory(Path.Combine(destinoRuim, "e_sqlite3.dll"));
        var ruim = Instalacao.Instalar(Sandbox(origem, destinoRuim), null, out var mexeuRuim);
        checar(semOrigem is not null && !mexeuSemOrigem && ok is null && mexeuOk && ruim is not null && mexeuRuim,
            $"TV-2b o Instalar diz se chegou a mexer no programa: origem que não existe ({mexeuSemOrigem}), troca inteira ({mexeuOk}), cópia que falhou no meio ({mexeuRuim}: {ruim})");

        var app = Fonte("Pdv.Instalador", "App.xaml.cs");
        var inicio = Corpo(app, "protected override void OnStartup(StartupEventArgs e)");
        var iFalha = inicio.IndexOf("MessageBox.Show(Instalacao.FraseDaFalha(falha, mexeu),", StringComparison.Ordinal);
        var iAbre = inicio.IndexOf("if (Pdv.Nucleo.ReabrirOCaixa.InstaladorAbre(e.Args) && exe is not null && File.Exists(exe)", StringComparison.Ordinal);
        checar(inicio.Contains("var falha = AtualizarSilencioso(out var pastaDoCaixa, out var mexeu);", StringComparison.Ordinal)
               && iFalha > 0 && iAbre > iFalha && !inicio.Contains("else if (Pdv.Nucleo.ReabrirOCaixa.InstaladorAbre", StringComparison.Ordinal)
               && !inicio.Contains("O caixa continua na versão anterior", StringComparison.Ordinal),
            "TV-2c depois da falha o instalador reabre o caixa antigo do mesmo jeito (antes o \"else\" deixava a tela preta só com o erro)");
        var silencioso = Corpo(app, "private static string? AtualizarSilencioso(out string? pasta, out bool mexeu)");
        checar(silencioso.Contains("AtalhoAreaTrabalho: true), null, out mexeu);", StringComparison.Ordinal),
            "TV-2d a atualização silenciosa pega do Instalar se mexeu no programa");
    }

    // ── TV-3 onde instalar ────────────────────────────────────────────────────────────────────
    private static void OndeInstalar(Action<bool, string> checar)
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var savassi = Path.Combine(pf, "PDV MMTech");
        checar(Instalacao.EscolherDestino(null, Instalacao.PastaDestinoPadrao) == Instalacao.PastaDestinoPadrao
               && Instalacao.EscolherDestino(" ", Instalacao.PastaDestinoPadrao) == Instalacao.PastaDestinoPadrao
               && Instalacao.EscolherDestino(savassi, Instalacao.PastaDestinoPadrao) == savassi,
            "TV-3 o assistente instala onde o caixa já está; só sem caixa nesta máquina vai para a pasta do nome de hoje");
        var janela = Fonte("Pdv.Instalador", "JanelaInstalador.xaml.cs");
        checar(janela.Contains("_destino = Instalacao.PastaParaInstalar();", StringComparison.Ordinal)
               && janela.Contains("_atualizacao = File.Exists(Path.Combine(_destino, \"Pdv.exe\"));", StringComparison.Ordinal)
               && janela.Contains("PastaDestino: _destino,", StringComparison.Ordinal)
               && janela.Contains("Instalacao.InstalarAgente(_pastaAgente, _destino, Progresso)", StringComparison.Ordinal)
               && janela.Contains("Instalacao.AbrirCaixa(Path.Combine(_destino, \"Pdv.exe\"))", StringComparison.Ordinal)
               && !janela.Contains("Instalacao.PastaDestinoPadrao", StringComparison.Ordinal),
            "TV-3b o assistente instala, põe o agente, reabre e diz se é atualização pela MESMA pasta, a do caixa (nada mais em MMFood fixo)");
        var inst = Fonte("Pdv.Instalador", "Instalacao.cs");
        checar(Corpo(inst, "public static string PastaParaInstalar()").Contains("EscolherDestino(PastaInstalada(), PastaDestinoPadrao)", StringComparison.Ordinal),
            "TV-3c a pasta do assistente é a PastaInstalada, a mesma regra da atualização silenciosa");

        // a migração não apaga a pasta do Shell do quiosque
        var exeSavassi = Path.Combine(savassi, "Pdv.exe");
        checar(!Instalacao.PastaAntigaPodeSair(savassi, new[] { exeSavassi })
               && !Instalacao.PastaAntigaPodeSair(savassi + @"\", new[] { exeSavassi.ToUpperInvariant() })
               && !Instalacao.PastaAntigaPodeSair(savassi, null)
               && Instalacao.PastaAntigaPodeSair(savassi, Array.Empty<string>())
               && Instalacao.PastaAntigaPodeSair(savassi, new[] { Path.Combine(pf, "MMFood", "Pdv.exe") })
               && Instalacao.PastaAntigaPodeSair(savassi, new[] { Path.Combine(pf, "PDV MMTech 2", "Pdv.exe") }),
            "TV-3d a pasta antiga com o Shell do quiosque apontando para dentro dela NÃO sai; sem ler os perfis, nenhuma sai; outra pasta (até com o mesmo começo de nome) não segura");
        var migrar = Corpo(inst, "public static void MigrarInstalacaoAntiga(string pastaNova)");
        checar(migrar.Contains("try { shells = QuiosqueDoCaixa.ExesNosShells(Registry.Users); }", StringComparison.Ordinal)
               && migrar.Contains("catch { shells = null; }", StringComparison.Ordinal)
               && migrar.IndexOf("PastaAntigaPodeSair(antiga, shells)", StringComparison.Ordinal) < migrar.IndexOf("Directory.Delete(antiga, recursive: true)", StringComparison.Ordinal)
               && migrar.Contains("PastaAntigaPodeSair(antiga, shells)", StringComparison.Ordinal),
            "TV-3e a migração lê os Shells dos perfis (só leitura) antes de apagar qualquer pasta antiga");

        // os Shells numa HKEY_USERS de mentira, isolada
        const string Pai = @"Software\PdvNativoTeste";
        var nome = Pai + @"\" + Guid.NewGuid().ToString("N");
        try
        {
            void Shell(string sid, string valor)
            {
                using var k = Registry.CurrentUser.CreateSubKey(nome + @"\" + sid + @"\" + DesfazerQuiosque.ChaveWinlogon);
                k.SetValue(DesfazerQuiosque.ValorShell, valor, RegistryValueKind.String);
            }
            Shell(SidCaixa, DesfazerQuiosque.ShellDoPdv(exeSavassi));
            Shell(SidLogado, "explorer.exe");
            Shell(".DEFAULT", DesfazerQuiosque.ShellDoPdv(Path.Combine(pf, "Outro", "Pdv.exe")));
            Shell(SidCaixa + "_Classes", DesfazerQuiosque.ShellDoPdv(Path.Combine(pf, "Classes", "Pdv.exe")));
            Shell(SidSemArquivo, DesfazerQuiosque.ShellDoPdv(exeSavassi) + " --mais");
            using var raizFalsa = Registry.CurrentUser.OpenSubKey(nome)!;
            var achados = QuiosqueDoCaixa.ExesNosShells(raizFalsa);
            checar(achados.Count == 1 && achados[0] == exeSavassi,
                "TV-3f os Shells de quiosque dos perfis de pessoa, só no formato do PDV: " + string.Join(" | ", achados));
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(nome, throwOnMissingSubKey: false); } catch { }
            try
            {
                bool vazio;
                using (var pai = Registry.CurrentUser.OpenSubKey(Pai)) vazio = pai is not null && pai.SubKeyCount == 0 && pai.ValueCount == 0;
                if (vazio) Registry.CurrentUser.DeleteSubKey(Pai, throwOnMissingSubKey: false);
            }
            catch { }
        }
    }

    // ── TV-4 a extração e o exe do instalador ──────────────────────────────────────────────────
    private static void Extracao(Action<bool, string> checar, string raiz)
    {
        var caixa = Path.Combine(raiz, "Program Files", "MMFood");
        var a = Instalacao.PastaDeExtracao(caixa);
        var b = Instalacao.PastaDeExtracao(caixa);
        checar(Path.GetDirectoryName(a) == Path.GetFullPath(caixa) && Path.GetFileName(a).StartsWith(Instalacao.PrefixoDaExtracao, StringComparison.Ordinal)
               && Path.GetFileName(a).Length == Instalacao.PrefixoDaExtracao.Length + 8 && a != b,
            "TV-4 o pacote é extraído DENTRO da pasta do caixa, com nome único (" + Path.GetFileName(a) + ")");
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        checar(Instalacao.PastaDeExtracao(Path.Combine(pf, "PDV MMTech")).StartsWith(Path.Combine(pf, "PDV MMTech") + @"\", StringComparison.OrdinalIgnoreCase),
            "TV-4b no caixa instalado a extração fica em Program Files (só administrador grava ou renomeia)");

        // as sobras de uma troca interrompida saem, e só elas
        Directory.CreateDirectory(Path.Combine(caixa, ".extracao-aaaa1111", "pdv"));
        File.WriteAllText(Path.Combine(caixa, ".extracao-aaaa1111", "pdv", "Pdv.exe"), "x");
        Directory.CreateDirectory(Path.Combine(caixa, ".extracao-bbbb2222"));
        Directory.CreateDirectory(Path.Combine(caixa, "agent"));
        Directory.CreateDirectory(Path.Combine(caixa, ".conferencia"));
        Directory.CreateDirectory(Path.Combine(caixa, "extracao-sem-ponto"));
        File.WriteAllText(Path.Combine(caixa, "Pdv.exe"), "programa");
        Instalacao.LimparExtracoesAntigas(caixa);
        Instalacao.LimparExtracoesAntigas(Path.Combine(raiz, "nao-existe"));
        checar(!Directory.Exists(Path.Combine(caixa, ".extracao-aaaa1111")) && !Directory.Exists(Path.Combine(caixa, ".extracao-bbbb2222"))
               && Directory.Exists(Path.Combine(caixa, "agent")) && Directory.Exists(Path.Combine(caixa, ".conferencia"))
               && Directory.Exists(Path.Combine(caixa, "extracao-sem-ponto")) && File.Exists(Path.Combine(caixa, "Pdv.exe")),
            "TV-4c as extrações que sobraram saem; o programa, o agente e o resto da pasta ficam (pasta que não existe: nada, sem erro)");

        var app = Fonte("Pdv.Instalador", "App.xaml.cs");
        var janela = Fonte("Pdv.Instalador", "JanelaInstalador.xaml.cs");
        var silencioso = Corpo(app, "private static string? AtualizarSilencioso(out string? pasta, out bool mexeu)");
        var iLimpa = silencioso.IndexOf("Instalacao.LimparExtracoesAntigas(pastaAtual);", StringComparison.Ordinal);
        var iPasta = silencioso.IndexOf("temporaria = Instalacao.PastaDeExtracao(pastaAtual);", StringComparison.Ordinal);
        var iExtrai = silencioso.IndexOf("Pacote.Extrair(temporaria,", StringComparison.Ordinal);
        var jLimpa = janela.IndexOf("Instalacao.LimparExtracoesAntigas(_destino);", StringComparison.Ordinal);
        var jPasta = janela.IndexOf("_pastaTemporaria = Instalacao.PastaDeExtracao(_destino);", StringComparison.Ordinal);
        var jExtrai = janela.IndexOf("Pacote.Extrair(_pastaTemporaria,", StringComparison.Ordinal);
        checar(iLimpa > 0 && iPasta > iLimpa && iExtrai > iPasta && jLimpa > 0 && jPasta > jLimpa && jExtrai > jPasta
               && !app.Contains("\"pdv-atualizar-\"", StringComparison.Ordinal) && !janela.Contains("\"pdv-instalador-\"", StringComparison.Ordinal),
            "TV-4d a atualização e o assistente extraem na pasta do caixa (nunca mais no TEMP do usuário)");

        var inicio = Corpo(app, "protected override void OnStartup(StartupEventArgs e)");
        var iTrava = inicio.IndexOf("_euMesmo = new FileStream(eu, FileMode.Open, FileAccess.Read, FileShare.Read);", StringComparison.Ordinal);
        checar(iTrava > 0 && iTrava < inicio.IndexOf("ReabrirOCaixa.ArgumentoAtualizar", StringComparison.Ordinal)
               && app.Contains("private static FileStream? _euMesmo;", StringComparison.Ordinal),
            "TV-4e o instalador prende o próprio exe (só leitura, FileShare.Read) antes de qualquer coisa e até sair");

        // de verdade: um exe RODANDO, preso assim, não é renomeado nem sobrescrito; sem o handle, é renomeado
        var pasta = Path.Combine(raiz, "eu-mesmo");
        Directory.CreateDirectory(pasta);
        var exe = Path.Combine(pasta, "eu-mesmo-teste.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);
        Process? rodando = null;
        try
        {
            rodando = Process.Start(new ProcessStartInfo(exe, "-n 20 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            _ = rodando?.StandardOutput.ReadToEndAsync();
            bool renomeouPreso, gravouPreso, renomeouSolto;
            using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                renomeouPreso = Tenta(() => File.Move(exe, exe + ".trocado"));
                gravouPreso = Tenta(() => File.WriteAllText(exe, "outro"));
            }
            renomeouSolto = Tenta(() => File.Move(exe, exe + ".trocado"));
            checar(rodando is { HasExited: false } && !renomeouPreso && !gravouPreso && renomeouSolto,
                $"TV-4f um exe rodando e preso só para leitura não é renomeado ({renomeouPreso}) nem sobrescrito ({gravouPreso}); solto, o Windows deixa renomear ({renomeouSolto}): é o handle que protege");
        }
        finally
        {
            try { if (rodando is { HasExited: false }) rodando.Kill(); rodando?.WaitForExit(5_000); } catch { }
            rodando?.Dispose();
        }
    }

    // ── TV-5 o instalador baixado preso até abrir ───────────────────────────────────────────────
    private static void InstaladorPreso(Action<bool, string> checar, string raiz)
    {
        var pasta = Path.Combine(raiz, "pdv-atualizacao");
        Directory.CreateDirectory(pasta);
        // sem "instal" no nome: o Windows nunca trata o ensaio como instalador (nada de aviso de administrador)
        var exe = Path.Combine(pasta, "baixado-9.9.9.exe");
        // um executável de verdade, grande o bastante para a conferência (o ping com 1,1 MB de sobra no fim)
        var bytes = File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "ping.exe")).Concat(new byte[1_100_000]).ToArray();
        File.WriteAllBytes(exe, bytes);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var m = new Atualizacao.Manifesto("9.9.9", "https://pdv.mmtech.software/download/InstalarPdv-9.9.9.exe", null, false, sha, bytes.Length);

        using (var preso = Atualizacao.TravarConferido(exe, m, out var motivo))
        {
            var apagou = Tenta(() => File.Delete(exe));
            var renomeou = Tenta(() => File.Move(exe, exe + ".outro"));
            var gravou = Tenta(() => File.WriteAllBytes(exe, new byte[10]));
            int? codigo = null;
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "-n 1 127.0.0.1") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                if (p is not null && p.WaitForExit(15_000)) codigo = p.ExitCode;
            }
            catch { codigo = null; }
            checar(preso is not null && motivo is null && !apagou && !renomeou && !gravou && codigo == 0,
                $"TV-5 o instalador conferido fica preso: ninguém apaga ({apagou}), renomeia ({renomeou}) nem grava ({gravou}), e o Windows abre ele assim mesmo pelo shell (saída {codigo?.ToString() ?? "nenhuma"})");
        }
        checar(Tenta(() => File.Move(exe, exe + ".solto")) && Tenta(() => File.Move(exe + ".solto", exe)),
            "TV-5b solto o handle, o arquivo volta a ser do usuário (o caixa não segura o arquivo depois de entregar)");

        var errado = m with { Sha256 = new string('0', 64) };
        var naoConfere = Atualizacao.TravarConferido(exe, errado, out var porque);
        var sumiu = Atualizacao.TravarConferido(Path.Combine(pasta, "nao-existe.exe"), m, out var porqueSumiu);
        checar(naoConfere is null && porque?.Contains("NÃO confere", StringComparison.Ordinal) == true && sumiu is null && porqueSumiu is not null
               && Tenta(() => File.Delete(exe)),
            "TV-5c sha que não confere ou arquivo que sumiu: nada é devolvido para abrir, e o arquivo não fica preso (" + porque + ")");

        var atualizar = Fonte("Telas", "AtualizarCaixa.cs");
        var entregar = Corpo(atualizar, "public static string? EntregarAoInstalador(string exe, Atualizacao.Manifesto m, out bool fechar)");
        var iTrava = entregar.IndexOf("using var conferido = Atualizacao.TravarConferido(exe, m, out var naoConfere);", StringComparison.Ordinal);
        var iAbre = entregar.IndexOf("p = Process.Start(new ProcessStartInfo", StringComparison.Ordinal);
        checar(iTrava > 0 && iAbre > iTrava, "TV-5d o caixa prende e confere o instalador ANTES de pedir ao Windows para abri-lo, e segura até a entrega acabar");
    }

    // ── TV-6 desinstalar com o usuário do caixa fora do Windows ────────────────────────────────
    private static void PerfilFora(Action<bool, string> checar, string raiz)
    {
        var exeHoje = @"C:\Program Files\MMFood\Pdv.exe";
        var perfis = Path.Combine(raiz, "Users");
        var hiveCaixa = Path.Combine(perfis, "caixa", "NTUSER.DAT");
        var hiveLogado = Path.Combine(perfis, "logado", "NTUSER.DAT");
        var hiveSistema = Path.Combine(perfis, "sistema", "NTUSER.DAT");
        var hiveSemArquivo = Path.Combine(perfis, "sem-arquivo", "NTUSER.DAT");
        Directory.CreateDirectory(Path.GetDirectoryName(hiveSemArquivo)!);
        foreach (var h in new[] { hiveCaixa, hiveLogado, hiveSistema })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(h)!);
            using var k = CriarHive(h);
            if (k is null) { checar(false, "TV-6 não deu para criar o hive de teste em " + h); return; }
            using (var w = k.CreateSubKey(DesfazerQuiosque.ChaveWinlogon))
            {
                w.SetValue(DesfazerQuiosque.ValorShell, DesfazerQuiosque.ShellDoPdv(exeHoje), RegistryValueKind.String);
                w.SetValue("AutoRestartShell", 1, RegistryValueKind.DWord);
            }
            foreach (var (chave, valor) in TravaDoCtrlAltDel.Travas.Append(TravaDoCtrlAltDel.Marca))
                using (var p = k.CreateSubKey(chave)) p.SetValue(valor, 1, RegistryValueKind.DWord);
        }

        checar(QuiosqueDoCaixa.AbrirHive(null) is null && QuiosqueDoCaixa.AbrirHive("") is null
               && QuiosqueDoCaixa.AbrirHive(@"Users\caixa\NTUSER.DAT") is null && QuiosqueDoCaixa.AbrirHive(hiveSemArquivo) is null
               && !File.Exists(hiveSemArquivo),
            "TV-6 vazio, relativo ou arquivo que não existe: não abre e NUNCA cria um NTUSER.DAT (o usuário entraria com um perfil em branco)");

        var lista = new (string, string)[]
        {
            (SidCaixa, hiveCaixa), (SidLogado, hiveLogado), ("S-1-5-18", hiveSistema), (SidSemArquivo, hiveSemArquivo),
        };
        var feitos = QuiosqueDoCaixa.DesfazerNosPerfisFora(lista, new[] { SidLogado, ".DEFAULT" }, new[] { exeHoje }, _ => true);
        checar(feitos.Count == 1 && feitos[0] is { Sid: SidCaixa, TirouShell: true, TirouMarca: true, ShellFicou: false, TravaFicou: false }
               && feitos[0].Politicas == TravaDoCtrlAltDel.Travas.Length && !File.Exists(hiveSemArquivo),
            "TV-6b o usuário do caixa fora do Windows: Shell, as políticas da trava e a marca saem pelo NTUSER.DAT dele; o perfil já carregado (vai pelo HKEY_USERS), o do sistema e o sem arquivo ficam de fora ["
            + string.Join(", ", feitos.Select(f => f.Sid[^4..] + ":" + (f.TirouShell ? "S" : "-") + f.Politicas + (f.TirouMarca ? "M" : "-"))) + "]");

        (string? Shell, int Politicas, object? Outro) Ler(string hive)
        {
            using var k = QuiosqueDoCaixa.AbrirHive(hive);
            if (k is null) return ("(não abriu)", -1, null);
            using var w = k.OpenSubKey(DesfazerQuiosque.ChaveWinlogon);
            var n = TravaDoCtrlAltDel.Travas.Append(TravaDoCtrlAltDel.Marca).Count(t =>
            {
                using var p = k.OpenSubKey(t.Chave);
                return p?.GetValue(t.Valor) is not null;
            });
            return (w?.GetValue(DesfazerQuiosque.ValorShell) as string, n, w?.GetValue("AutoRestartShell"));
        }
        var caixa = Ler(hiveCaixa);
        var logado = Ler(hiveLogado);
        checar(caixa.Shell is null && caixa.Politicas == 0 && caixa.Outro is 1
               && logado.Shell == DesfazerQuiosque.ShellDoPdv(exeHoje) && logado.Politicas == TravaDoCtrlAltDel.Travas.Length + 1,
            $"TV-6c relido do arquivo: no caixa saíram o Shell, a trava e a marca e o resto do Winlogon ficou; o perfil carregado não foi tocado (caixa: {caixa.Shell ?? "sem Shell"}/{caixa.Politicas}; logado: {logado.Politicas})");

        // um NTUSER.DAT em uso (o Windows segura o arquivo): não abre, não lança, segue
        bool lancou = false;
        IReadOnlyList<QuiosqueDoCaixa.Desfeito> emUso = Array.Empty<QuiosqueDoCaixa.Desfeito>();
        FileStream? segura = null;
        for (var i = 0; i < 20 && segura is null; i++)
        {
            try { segura = new FileStream(hiveLogado, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch { Thread.Sleep(100); /* o hive relido acima sai da memória em instantes */ }
        }
        using (segura)
        {
            try { emUso = QuiosqueDoCaixa.DesfazerNosPerfisFora(new[] { (SidLogado, hiveLogado) }, Array.Empty<string>(), new[] { exeHoje }, _ => true); }
            catch { lancou = true; }
        }
        checar(segura is not null && !lancou && emUso.Count == 0, "TV-6d NTUSER.DAT em uso: fica de fora, sem lançar");

        var meusPerfis = Instalacao.PerfisDoWindows();
        var euSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        checar(meusPerfis.Count > 0 && meusPerfis.Any(p => p.Sid == euSid && p.Hive.EndsWith(@"\NTUSER.DAT", StringComparison.OrdinalIgnoreCase)),
            $"TV-6e a lista de perfis do Windows (só leitura) tem este usuário com o NTUSER.DAT dele ({meusPerfis.Count} perfis)");

        var inst = Fonte("Pdv.Instalador", "Instalacao.cs");
        var desfazer = Corpo(inst, "public static DesfazerQuiosque.Resumo DesfazerQuiosqueDoCaixa()");
        var iCarregados = desfazer.IndexOf("QuiosqueDoCaixa.DesfazerNosPerfis(Registry.Users, exes, File.Exists)", StringComparison.Ordinal);
        var iFora = desfazer.IndexOf("QuiosqueDoCaixa.DesfazerNosPerfisFora(PerfisDoWindows(), Registry.Users.GetSubKeyNames(), exes, File.Exists)", StringComparison.Ordinal);
        checar(iCarregados > 0 && iFora > iCarregados && desfazer.Contains("return QuiosqueDoCaixa.Resumir(feitos);", StringComparison.Ordinal),
            "TV-6f o desinstalador desfaz nos perfis carregados e depois nos de fora, e o resumo conta os dois (Shell preso fora também segura a desinstalação)");
        var qdc = Fonte("Pdv.Instalador", "QuiosqueDoCaixa.cs");
        var abrir = Corpo(qdc, "public static RegistryKey? AbrirHive(string? arquivo)");
        checar(abrir.IndexOf("!File.Exists(arquivo)", StringComparison.Ordinal) > 0
               && abrir.IndexOf("!File.Exists(arquivo)", StringComparison.Ordinal) < abrir.IndexOf("RegLoadAppKey(", StringComparison.Ordinal),
            "TV-6g o hive só é aberto depois de conferir que o arquivo existe");
    }

    // ── apoio ───────────────────────────────────────────────────────────────────────────────────

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegLoadAppKey(string arquivo, out IntPtr chave, int acesso, int opcoes, int reservado);

    /// <summary>Cria (ou abre) um hive de teste num arquivo: só o ensaio cria hive, o desinstalador nunca.</summary>
    private static RegistryKey? CriarHive(string arquivo)
        => RegLoadAppKey(arquivo, out var h, 0xF003F, 0, 0) == 0 && h != IntPtr.Zero
            ? RegistryKey.FromHandle(new SafeRegistryHandle(h, ownsHandle: true))
            : null;

    private static Instalacao.Opcoes Sandbox(string origem, string destino) => new(
        OrigemPasta: origem, PastaDestino: destino, IniciarComWindows: false, AtalhoAreaTrabalho: false,
        AjustarAcl: false, GravarRegistro: false, MigrarAntiga: false, ConferirQueAbre: false);

    private static bool Tenta(Action a)
    {
        try { a(); return true; }
        catch { return false; }
    }

    private static string Corpo(string fonte, string assinatura)
    {
        var i = fonte.IndexOf(assinatura, StringComparison.Ordinal);
        if (i < 0) return "";
        var fim = fonte.Length;
        foreach (var marca in new[] { "\n    private ", "\n    public ", "\n    internal ", "\n    protected ", "\n    /// <summary>", "\n    // ──" })
        {
            var j = fonte.IndexOf(marca, i + assinatura.Length, StringComparison.Ordinal);
            if (j >= 0 && j < fim) fim = j;
        }
        return fonte[i..fim];
    }

    private static string Fonte(params string[] partes)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Pdv.csproj")))
            {
                var c = Path.Combine(new[] { d.FullName }.Concat(partes).ToArray());
                return File.Exists(c) ? File.ReadAllText(c).Replace("\r\n", "\n") : "";
            }
        return "";
    }
}
