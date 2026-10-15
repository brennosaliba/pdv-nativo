using System.Diagnostics;
using Pdv.Instalador;

namespace Pdv.Testes;

/// <summary>
/// O INSTALADOR DA CASTELO (14/09/2026).
///
///   1. Ficou parado em "Conferindo se o caixa abre nesta máquina". A conferência lia a saída
///      inteira do Pdv.exe ANTES de esperar com prazo: ReadToEnd só volta quando o programa (e
///      todo filho que herdou a saída dele) fecha. O prazo de 90 s nunca valia.
///   2. A tela mostrava "versão 2.0.0.0": no instalador empacotado não há Pdv.exe ao lado, e a
///      conta caía na versão do PRÓPRIO instalador.
///   3. O caixa aberto pelo instalador herdava o administrador, e a trava dele não era vista
///      pelo clique normal no ícone.
///   4. (1.0.36, revisão de segurança) No quiosque, sem Explorer, o instalador ainda abria o caixa
///      direto, como administrador. Elevado, agora ele nunca abre direto; a conferência extrai as
///      nativas ao lado do exe; programas do Windows vão pelo caminho absoluto.
/// </summary>
public static class TestesInstaladorCastelo
{
    public static void Rodar(Action<bool, string> checar)
    {
        ProgramaPreso(checar);
        FilhoSegurandoASaida(checar);
        VersaoDoCaixa(checar);
        AbrirSemAdministrador(checar);
    }

    private static void ProgramaPreso(Action<bool, string> checar)
    {
        Process? vizinho = null;
        try
        {
            // Outro processo igual, que já estava aberto: é o "outro Pdv.exe" que o instalador
            // não pode derrubar.
            vizinho = Process.Start(Sondas.Psi("--sonda-presa", "60"));
            var relogio = Stopwatch.StartNew();
            var r = Instalacao.RodarComPrazo(Sondas.Psi("--sonda-presa", "20"), TimeSpan.FromSeconds(2));
            relogio.Stop();
            checar(relogio.ElapsedMilliseconds < 8_000,
                $"programa preso: a conferência desiste no prazo ({relogio.ElapsedMilliseconds} ms com prazo de 2 s)");
            checar(!r.Terminou && r.Matou, $"programa preso: sai como não terminou e o processo foi encerrado (terminou={r.Terminou}, matou={r.Matou})");
            checar(vizinho is { HasExited: false },
                "e o outro processo igual que já estava aberto continua vivo: o instalador só encerra o que ele mesmo abriu");

            var fonte = Fonte("Pdv.Instalador", "Instalacao.cs") ?? "";
            var i = fonte.IndexOf("public static string? ConferirQueOProgramaAbre(", StringComparison.Ordinal);
            var fim = i < 0 ? -1 : fonte.IndexOf("public static string? AvaliarConferencia(", i, StringComparison.Ordinal);
            var corpo = i < 0 || fim < 0 ? "" : fonte[i..fim];
            checar(corpo.Contains("RodarComPrazo(", StringComparison.Ordinal),
                "a conferência do instalador passa pela execução com prazo testada aqui");
            checar(!corpo.Contains("ReadToEnd()", StringComparison.Ordinal),
                "e não lê a saída inteira antes de esperar (era isso que tirava o prazo)");
        }
        finally
        {
            try { vizinho?.Kill(); } catch { }
            vizinho?.Dispose();
        }
    }

    private static void FilhoSegurandoASaida(Action<bool, string> checar)
    {
        var arquivoPid = Path.Combine(Path.GetTempPath(), $"neto_{Guid.NewGuid():N}.pid");
        try
        {
            var relogio = Stopwatch.StartNew();
            var r = Instalacao.RodarComPrazo(Sondas.Psi("--sonda-neto", "20", arquivoPid), TimeSpan.FromSeconds(30));
            relogio.Stop();
            checar(r.Terminou && r.Codigo == 0 && relogio.ElapsedMilliseconds < 8_000,
                $"programa que abriu e deixou um filho com a saída: a conferência volta quando ELE termina "
                + $"({relogio.ElapsedMilliseconds} ms, terminou={r.Terminou}, código {r.Codigo})");
        }
        finally
        {
            try
            {
                if (File.Exists(arquivoPid) && int.TryParse(File.ReadAllText(arquivoPid), out var pid))
                    using (var neto = Process.GetProcessById(pid)) neto.Kill();
            }
            catch { /* já saiu */ }
            try { File.Delete(arquivoPid); } catch { }
        }
    }

    private static void VersaoDoCaixa(Action<bool, string> checar)
    {
        checar(Instalacao.RotuloVersao("1.0.10.0") == "versão 1.0.10", "rótulo: 1.0.10.0 aparece como versão 1.0.10");
        checar(Instalacao.RotuloVersao("1.2.0.0") == "versão 1.2.0", "rótulo: só o 4º número some");
        checar(Instalacao.RotuloVersao(null) == "" && Instalacao.RotuloVersao("  ") == "",
            "rótulo: versão desconhecida não mostra número nenhum (antes caía na do instalador, 2.0.0.0)");

        var raiz = Path.Combine(Path.GetTempPath(), "pdv-versao-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var origem = Path.Combine(raiz, "pdv");
            Directory.CreateDirectory(origem);
            var exeDeVerdade = Path.Combine(AppContext.BaseDirectory, "Pdv.exe");
            if (!File.Exists(exeDeVerdade)) exeDeVerdade = Environment.ProcessPath ?? "";
            File.Copy(exeDeVerdade, Path.Combine(origem, "Pdv.exe"));
            var esperado = FileVersionInfo.GetVersionInfo(exeDeVerdade).FileVersion;

            var exeBase = Path.Combine(raiz, "base.exe");
            File.WriteAllBytes(exeBase, Enumerable.Repeat((byte)0x4D, 4096).ToArray());
            var saida = Path.Combine(raiz, "InstalarPdv.exe");
            checar(Pacote.Empacotar(exeBase, origem, null, saida) is null, "versão: empacotei um caixa com versão");
            var lida = Pacote.LerVersaoDoCaixa(saida);
            checar(esperado is { Length: > 0 } && lida == esperado,
                $"versão: o pacote leva a versão do CAIXA ({esperado}) e o instalador lê de lá ('{lida}')");

            var aberto = Path.Combine(raiz, "aberto");
            checar(Pacote.Extrair(aberto, null, saida) is null
                   && Instalacao.ConferirOrigem(Path.Combine(aberto, "pdv")) is null
                   && !Instalacao.ArquivosParaCopiar(Path.Combine(aberto, "pdv")).Any(a => a.Contains("versao", StringComparison.OrdinalIgnoreCase)),
                "versão: o que sai do pacote continua instalável e a anotação da versão não vai para a pasta do caixa");
        }
        finally { try { Directory.Delete(raiz, true); } catch { } }

        var janela = Fonte("Pdv.Instalador", "JanelaInstalador.xaml.cs") ?? "";
        checar(janela.Length > 0 && !janela.Contains("GetVersionInfo(Environment.ProcessPath", StringComparison.Ordinal),
            "a tela do instalador não mostra mais a versão do próprio instalador como se fosse a do caixa");
        checar(janela.Contains("Pacote.LerVersaoDoCaixa(", StringComparison.Ordinal),
            "e no instalador empacotado a versão vem de dentro do pacote");
    }

    private static void AbrirSemAdministrador(Action<bool, string> checar)
    {
        checar(Instalacao.DecidirComoAbrir(elevado: true, explorerAberto: true) == Instalacao.ComoAbrir.PeloExplorer,
            "instalador como administrador abre o caixa como o usuário da área de trabalho, sem herdar o administrador");
        // 1.0.36 (revisão de segurança): elevado NUNCA abre direto. No quiosque o caixa aberto daqui
        // passava o turno inteiro como administrador (WebView2, trava gravando sem aviso, bibliotecas
        // extraídas na pasta do usuário carregadas elevadas).
        checar(Instalacao.DecidirComoAbrir(elevado: true, explorerAberto: false) == Instalacao.ComoAbrir.NaoAbre,
            "elevado e sem área de trabalho (quiosque) NÃO abre o caixa: quem chamou reabre, ou a pessoa reinicia");
        checar(Instalacao.DecidirComoAbrir(elevado: false, explorerAberto: true) == Instalacao.ComoAbrir.Direto
               && Instalacao.DecidirComoAbrir(elevado: false, explorerAberto: false) == Instalacao.ComoAbrir.Direto,
            "sem administrador não há o que tirar: abre direto");
        checar(new[] { true, false }.All(ex => Instalacao.DecidirComoAbrir(elevado: true, explorerAberto: ex) != Instalacao.ComoAbrir.Direto),
            "elevado nunca abre Direto, com ou sem área de trabalho");

        var inst = Fonte("Pdv.Instalador", "Instalacao.cs") ?? "";
        var abrir = Corpo(inst, "public static bool AbrirCaixa(string exe)");
        var direto = abrir.IndexOf("case ComoAbrir.Direto:", StringComparison.Ordinal);
        var explorer = abrir.IndexOf("case ComoAbrir.PeloExplorer:", StringComparison.Ordinal);
        var semDireto = explorer < 0 ? "" : abrir[explorer..];
        checar(abrir.Length > 0 && direto > 0 && explorer > direto
               && Contar(abrir, "UseShellExecute = true") == 1 && !semDireto.Contains("UseShellExecute = true", StringComparison.Ordinal)
               && semDireto.Contains("return false;", StringComparison.Ordinal)
               && abrir.Contains("new ProcessStartInfo(CaminhoDoExplorer())", StringComparison.Ordinal)
               && !abrir.Contains("\"explorer.exe\"", StringComparison.Ordinal),
            "o Explorer que não mostrou o caixa NÃO cai para o direto (seria o caixa como administrador), e o Explorer vai pelo caminho da pasta do Windows");
        checar(Instalacao.CaminhoDoExplorer() == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")
               && File.Exists(Instalacao.CaminhoDoExplorer()),
            "o Explorer é o da pasta do Windows (" + Instalacao.CaminhoDoExplorer() + ")");

        var app = Fonte("Pdv.Instalador", "App.xaml.cs") ?? "";
        var janela = Fonte("Pdv.Instalador", "JanelaInstalador.xaml.cs") ?? "";
        checar(app.Contains("Instalacao.AbrirCaixa(", StringComparison.Ordinal) && janela.Contains("Instalacao.AbrirCaixa(", StringComparison.Ordinal),
            "os dois caminhos que reabrem o caixa (fim da instalação e atualização) passam pela mesma regra");
        checar(!app.Contains("Path.Combine(Instalacao.PastaDestinoPadrao, \"Pdv.exe\")", StringComparison.Ordinal),
            "a atualização reabre o caixa onde ele ESTÁ instalado, e não na pasta do nome novo");
        checar(app.Contains("Pdv.Nucleo.ReabrirOCaixa.InstaladorAbre(e.Args)", StringComparison.Ordinal)
               && app.Contains("MessageBox.Show(Instalacao.ReinicieParaAbrir", StringComparison.Ordinal)
               && janela.Contains("if (!Instalacao.AbrirCaixa(", StringComparison.Ordinal) && janela.Contains("Instalacao.AbraPeloAtalho", StringComparison.Ordinal),
            "com --sem-abrir o instalador não abre nada (o caixa se reabre); sem ele e sem como abrir sem administrador, a frase diz para reiniciar ou usar o atalho");
        checar(Instalacao.ReinicieParaAbrir.Length <= 80 && Instalacao.AbraPeloAtalho.Length <= 100
               && new[] { Instalacao.ReinicieParaAbrir, Instalacao.AbraPeloAtalho }.All(t => !t.Contains('—') && !t.Contains('–')
                   && !System.Text.RegularExpressions.Regex.IsMatch(t, "(?i)administrador|explorer|elevad|uac")),
            "as frases de quando o caixa não abre sozinho são curtas, sem travessão e sem termo técnico");

        // ── A CONFERÊNCIA NÃO CARREGA BIBLIOTECA DA PASTA DO USUÁRIO ──
        // O instalador roda elevado e a conferência (Pdv.exe --cupom-teste) nasce elevada com ele. Sem
        // a variável, o Pdv.exe de arquivo único extrai e carrega as nativas de %TEMP%\.net, que o
        // usuário grava. Com ela, a extração vai para uma pasta ao lado do exe (Program Files).
        var exeFalso = Path.Combine(Path.GetTempPath(), "pdv-conf-" + Guid.NewGuid().ToString("N")[..8], "Pdv.exe");
        var psi = Instalacao.PsiDaConferencia(exeFalso, "x.png");
        checar(Instalacao.VariavelDaExtracao == "DOTNET_BUNDLE_EXTRACT_BASE_DIR"
               && psi.Environment.TryGetValue(Instalacao.VariavelDaExtracao, out var base_) && base_ == Instalacao.PastaDaConferencia(exeFalso)
               && Instalacao.PastaDaConferencia(exeFalso) == Path.Combine(Path.GetDirectoryName(exeFalso)!, ".conferencia")
               && psi.FileName == exeFalso && psi.ArgumentList.SequenceEqual(new[] { "--cupom-teste", "x.png" }) && !psi.UseShellExecute,
            "a conferência extrai as bibliotecas nativas AO LADO DO EXE (DOTNET_BUNDLE_EXTRACT_BASE_DIR), com a proteção do próprio exe, nunca em %TEMP%\\.net");
        var instalado = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MMFood", "Pdv.exe");
        checar(Instalacao.PastaDaConferencia(instalado).StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase),
            "no caixa instalado a pasta da extração fica em Program Files (só administrador grava): " + Instalacao.PastaDaConferencia(instalado));
        var conferir = Corpo(inst, "public static string? ConferirQueOProgramaAbre(string exe, TimeSpan prazo)");
        checar(conferir.Contains("RodarComPrazo(PsiDaConferencia(exe, png), prazo)", StringComparison.Ordinal)
               && conferir.Contains("Directory.Delete(PastaDaConferencia(exe), recursive: true)", StringComparison.Ordinal),
            "a conferência usa esse processo e apaga a pasta da extração no fim");
        // de verdade: uma pasta .conferencia que sobrou some depois da conferência
        var pastaFalsa = Path.GetDirectoryName(exeFalso)!;
        try
        {
            Directory.CreateDirectory(Path.Combine(Instalacao.PastaDaConferencia(exeFalso), "Pdv", "abc"));
            File.WriteAllText(Path.Combine(Instalacao.PastaDaConferencia(exeFalso), "Pdv", "abc", "x.dll"), "x");
            File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exeFalso, overwrite: true);
            var reprovou = Instalacao.ConferirQueOProgramaAbre(exeFalso, TimeSpan.FromSeconds(20)) is not null;
            checar(reprovou && !Directory.Exists(Instalacao.PastaDaConferencia(exeFalso)),
                "a pasta da extração da conferência é apagada no fim (processo real)");
        }
        finally { try { Directory.Delete(pastaFalsa, recursive: true); } catch { } }

        // ── PROGRAMAS DO WINDOWS PELO CAMINHO ABSOLUTO ──
        // Pelo nome ("icacls", "wscript", "explorer.exe") o Windows procura antes na pasta do
        // instalador e na pasta atual (na atualização, o TEMP do usuário): um homônimo plantado ali
        // rodaria como administrador.
        var fontes = new[] { "Instalacao.cs", "PayGo.cs", "WebView2Runtime.cs", "Pacote.cs", "App.xaml.cs", "JanelaInstalador.xaml.cs", "QuiosqueDoCaixa.cs" }
            .Select(n => (Nome: n, Texto: Fonte("Pdv.Instalador", n) ?? "")).ToList();
        var peloNome = new System.Text.RegularExpressions.Regex(@"(FileName\s*=\s*""[^""\\/]*""|new ProcessStartInfo\(\s*""[^""\\/]*""|Process\.Start\(\s*""[^""\\/]*"")");
        var achados = fontes.SelectMany(f => peloNome.Matches(f.Texto).Select(m => f.Nome + ": " + m.Value)).ToList();
        checar(fontes.All(f => f.Texto.Length > 0) && achados.Count == 0,
            "o instalador não chama programa nenhum pelo nome (sem caminho)" + (achados.Count == 0 ? "" : " [" + string.Join(" | ", achados) + "]"));
        checar(Instalacao.ProgramaDoSistema("icacls.exe") == Path.Combine(Environment.SystemDirectory, "icacls.exe")
               && File.Exists(Instalacao.ProgramaDoSistema("icacls.exe")) && File.Exists(Instalacao.ProgramaDoSistema("wscript.exe"))
               && inst.Contains("FileName = ProgramaDoSistema(\"icacls.exe\")", StringComparison.Ordinal)
               && inst.Contains("FileName = ProgramaDoSistema(\"wscript.exe\")", StringComparison.Ordinal)
               && (fontes.First(f => f.Nome == "PayGo.cs").Texto).Contains("FileName = Instalacao.ProgramaDoSistema(\"icacls.exe\")", StringComparison.Ordinal),
            "icacls e wscript saem de System32, pelo caminho absoluto");
        var atalho = Corpo(inst, "private static void CriarAtalho(string lnk, string alvo)");
        var script = Instalacao.ScriptDoAtalho(instalado);
        checar(atalho.Contains("var vbs = ScriptDoAtalho(alvo);", StringComparison.Ordinal) && !atalho.Contains("GetTempPath", StringComparison.Ordinal)
               && Path.GetDirectoryName(script) == Path.GetDirectoryName(instalado) && Path.GetFileName(script).StartsWith("atalho-", StringComparison.Ordinal)
               && script != Instalacao.ScriptDoAtalho(instalado),
            "o VBScript do atalho nasce na pasta do programa (só administrador grava), com nome único, e não no TEMP do usuário");
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
        {
            var alvo = Path.Combine(new[] { d.FullName }.Concat(partes).ToArray());
            if (File.Exists(alvo)) return File.ReadAllText(alvo);
        }
        return null;
    }
}
