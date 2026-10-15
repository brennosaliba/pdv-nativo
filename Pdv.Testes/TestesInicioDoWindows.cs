using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// 1.0.33 (14/10/2026): no quiosque o PDV abre os programas da inicialização do Windows marcados
/// na Configuração (o dono quer o Spotify e o AnyDesk junto com o caixa).
///  IW-1..7  a regra pura (separar a linha de comando, Gerenciador de Tarefas, o que abrir)
///  IW-8..11 o código: só no quiosque, depois do Spotify, nunca lança, a Configuração grava
/// </summary>
public static class TestesInicioDoWindows
{
    public static void Rodar(Action<bool, string> checar)
    {
        // IW-1 a linha de comando com aspas, sem aspas com espaço no caminho, e com variável
        var a = InicioDoWindows.Separar("\"C:\\Users\\caixa\\AppData\\Roaming\\Spotify\\Spotify.exe\" --autostart --minimized");
        var b = InicioDoWindows.Separar("C:\\Program Files (x86)\\AnyDesk\\AnyDesk.exe --tray");
        var c = InicioDoWindows.Separar("%LOCALAPPDATA%\\Programa\\x.exe", t => t.Replace("%LOCALAPPDATA%", "C:\\L"));
        checar(a is { Exe: "C:\\Users\\caixa\\AppData\\Roaming\\Spotify\\Spotify.exe", Argumentos: "--autostart --minimized" }
               && b is { Exe: "C:\\Program Files (x86)\\AnyDesk\\AnyDesk.exe", Argumentos: "--tray" }
               && c is { Exe: "C:\\L\\Programa\\x.exe", Argumentos: "" }
               && InicioDoWindows.Separar("") is null && InicioDoWindows.Separar("\"\"") is null,
            "IW-1 separa exe e argumentos (com aspas, sem aspas com espaço no caminho, com %VARIAVEL%); vazio não é programa");
        // IW-2 o Gerenciador de Tarefas: 02/06 ligado, 03/07 desligado, sem valor ligado
        checar(!InicioDoWindows.DesligadoNoGerenciador(new byte[] { 2, 0, 0 }) && !InicioDoWindows.DesligadoNoGerenciador(new byte[] { 6 })
               && InicioDoWindows.DesligadoNoGerenciador(new byte[] { 3, 0 }) && InicioDoWindows.DesligadoNoGerenciador(new byte[] { 7 })
               && !InicioDoWindows.DesligadoNoGerenciador(null) && !InicioDoWindows.DesligadoNoGerenciador(Array.Empty<byte>()),
            "IW-2 desligado no Gerenciador de Tarefas (StartupApproved ímpar) não abre; sem marca, abre");
        // IW-3 o próprio caixa, o agente, o instalador e o Explorer nunca abrem por aqui
        var pdv = "C:\\PdvNativo\\Pdv.exe";
        checar(InicioDoWindows.DoCaixa("C:\\PdvNativo\\Pdv.exe", pdv) && InicioDoWindows.DoCaixa("C:\\Windows\\explorer.exe", pdv)
               && InicioDoWindows.DoCaixa("C:\\x\\PdvAgente.exe", pdv) && InicioDoWindows.DoCaixa("C:\\x\\InstalarPdv-1.0.33.exe", pdv)
               && !InicioDoWindows.DoCaixa("C:\\Program Files (x86)\\AnyDesk\\AnyDesk.exe", pdv),
            "IW-3 o PDV, o agente, o instalador e o Explorer ficam de fora");
        // IW-4 o que abrir: sem o desmarcado, sem o que já está aberto, um por executável
        var todos = new[]
        {
            new ProgramaDoInicio("Spotify", "C:\\S\\Spotify.exe", "--autostart", "run"),
            new ProgramaDoInicio("AnyDesk", "C:\\A\\AnyDesk.exe", "", "instalado"),
            new ProgramaDoInicio("OneDrive", "C:\\O\\OneDrive.exe", "/background", "run"),
            new ProgramaDoInicio("Spotify Web Helper", "C:\\S\\Spotify.exe", "", "run"),
            new ProgramaDoInicio("Pdv", pdv, "--quiosque", "run"),
        };
        var abrir = InicioDoWindows.AAbrir(todos, null, new HashSet<string> { "anydesk" }, pdv);
        var abrir2 = InicioDoWindows.AAbrir(todos, "OneDrive|AnyDesk", new HashSet<string>(), pdv);
        checar(abrir.Select(p => p.Nome).SequenceEqual(new[] { "Spotify" }) && abrir2.Select(p => p.Nome).SequenceEqual(new[] { "AnyDesk", "OneDrive" }),
            $"IW-4 sem lista salva abre só Spotify e AnyDesk (o que já está aberto não); com lista, só os marcados ({string.Join(",", abrir.Select(p => p.Nome))} / {string.Join(",", abrir2.Select(p => p.Nome))})");
        // IW-5 atalho da pasta Inicializar: o nome do atalho também conta como aberto
        var lnk = new[] { new ProgramaDoInicio("Programa X", "C:\\Users\\caixa\\Start Menu\\Startup\\Programa X.lnk", "", "pasta") };
        checar(InicioDoWindows.AAbrir(lnk, "Programa X", new HashSet<string> { "programa x" }, pdv).Count == 0
               && InicioDoWindows.AAbrir(lnk, "Programa X", new HashSet<string>(), pdv).Count == 1
               && InicioDoWindows.AAbrir(lnk, null, new HashSet<string>(), pdv).Count == 0,
            "IW-5 atalho .lnk marcado abre, não abre de novo quem já está aberto, e sem lista salva não abre (não é Spotify nem AnyDesk)");
        // IW-6 a opção: ausente ou "1" abre; só "0" desliga. Desmarcados: gravados em ordem, sem repetir
        checar(InicioDoWindows.Ligado(null) && InicioDoWindows.Ligado("1") && !InicioDoWindows.Ligado("0")
               && InicioDoWindows.EscreverLista(new[] { "OneDrive", "Teams", "onedrive", " " }) == "OneDrive|Teams"
               && InicioDoWindows.Lista("OneDrive|Teams").Contains("teams")
               && InicioDoWindows.Marcado(todos[0], null) && InicioDoWindows.Marcado(todos[1], null) && !InicioDoWindows.Marcado(todos[2], null)
               && !InicioDoWindows.Marcado(todos[0], "") && InicioDoWindows.Marcado(todos[2], "OneDrive"),
            "IW-6 ligado por padrão (o dono pediu); só '0' desliga; sem lista salva só Spotify e AnyDesk vêm marcados; lista salva vazia não abre nada");
        // IW-7 a lista da Configuração: um por nome, sem o caixa
        checar(InicioDoWindows.Unicos(todos, pdv).Select(p => p.Nome).SequenceEqual(new[] { "Spotify", "AnyDesk", "OneDrive", "Spotify Web Helper" }),
            "IW-7 a lista da Configuração tem um por nome e não mostra o próprio PDV");

        var app = Fonte("App.xaml.cs") ?? "";
        var no = Fonte("InicioDoWindowsNoCaixa.cs") ?? "";
        var cfg = Fonte(Path.Combine("Telas", "Configuracao.xaml.cs")) ?? "";
        var cfgXaml = Fonte(Path.Combine("Telas", "Configuracao.xaml")) ?? "";
        checar(no.Contains("if (!Quiosque.Ligado) return;") && no.Contains("if (!InicioDoWindows.Ligado(ligado))"),
            "IW-8 só abre no quiosque (fora dele o Windows já abre) e com a opção ligada");
        var iSp = app.IndexOf("SpotifyNoCaixa.AoAbrirOPdv(", StringComparison.Ordinal);
        var iIw = app.IndexOf("InicioDoWindowsNoCaixa.AbrirNoQuiosque(", StringComparison.Ordinal);
        checar(iSp > 0 && iIw > iSp && app.Contains("TimeSpan.FromSeconds(8));"),
            "IW-9 abre depois do Spotify escondido, com 8 s de espera (o Spotify já estará aberto e não abre duas vezes)");
        checar(no.Contains("catch (Exception ex) { Anotar(\"abrir: \" + ex.GetType().Name); }") && no.Contains("await Task.Delay(TimeSpan.FromSeconds(2));"),
            "IW-10 nunca lança (anota em inicio-windows.txt) e abre um a cada 2 s");
        checar(cfgXaml.Contains("x:Name=\"ChkInicioWindows\"") && cfgXaml.Contains("x:Name=\"ListaInicioWindows\"")
               && cfg.Contains("Vendas.GravarConfig(cx, InicioDoWindows.ChaveLigado,") && cfg.Contains("Vendas.GravarConfig(cx, InicioDoWindows.ChaveMarcados,")
               && !cfgXaml.Contains("Nada mais carrega: nem Spotify nem outros programas."),
            "IW-11 a Configuração tem a opção e a lista, grava as duas chaves, e o texto do quiosque não diz mais que nada carrega");
        // IW-12 a trava do Ctrl+Alt+Del: as quatro políticas do usuário, só com o quiosque, desfeita ao desligar
        var qui = Fonte("Quiosque.cs") ?? "";
        checar(Quiosque.Travas.Select(t => t.Valor).SequenceEqual(new[] { "DisableTaskMgr", "DisableLockWorkstation", "DisableChangePassword", "NoLogoff" })
               && Quiosque.Travas.All(t => t.Chave.StartsWith(@"Software\Microsoft\Windows\CurrentVersion\Policies\", StringComparison.Ordinal))
               && qui.Contains("raiz.CreateSubKey(chave, writable: true)") && !qui.Contains("Registry.LocalMachine")
               && cfg.Contains("var travarQuer = ChkTravarAtalhos.IsChecked == true && Quiosque.Ligado;")
               && cfgXaml.Contains("x:Name=\"ChkTravarAtalhos\""),
            "IW-12 a trava do Ctrl+Alt+Del grava só as 4 políticas do usuário (nunca a máquina), só com o quiosque ligado, e desligar o quiosque desfaz");
        // IW-13 a saída de emergência: caixa que não abre no quiosque abre a área de trabalho; trava sem
        // quiosque é desfeita ao abrir o PDV; desmarcar sempre destrava (até a trava pela metade)
        checar(app.Contains("try { if (Quiosque.Ligado) Quiosque.AbrirExplorer(); } catch { }")
               && app.IndexOf("Quiosque.AbrirExplorer(); } catch { }", StringComparison.Ordinal) < app.IndexOf("Environment.Exit(1);", StringComparison.Ordinal)
               && app.Contains("if (!Quiosque.Ligado && Quiosque.AlgumaTrava && Quiosque.TentarDireto(false) is { } naoTirou)")
               && cfg.Contains("if (TravaDoCtrlAltDel.PrecisaMudar(travarQuer, Quiosque.Travado, Quiosque.AlgumaTrava))"),
            "IW-13 caixa que não abre no quiosque abre a área de trabalho antes de sair; trava sem quiosque é desfeita (só direto, sem aviso do Windows); desmarcar destrava até a trava pela metade");
        checar(no.Contains("p.SessionId == sessao"),
            "IW-14 o AnyDesk de serviço (outra sessão) não conta como aberto: a janelinha dele abre");
        // IW-15 (1.0.37) com o Spotify escondido ligado, a lista não abre o Spotify: um dono só, sem duplicata
        var comSp = InicioDoWindows.AAbrir(todos, null, new HashSet<string>(), pdv, pularSpotify: true);
        var lojaSp = new ProgramaDoInicio("SpotifyStartupTask", "C:\\L\\WindowsApps\\SpotifyStartupTask.exe", "--autostart", "run");
        checar(comSp.Select(p => p.Nome).SequenceEqual(new[] { "AnyDesk" })
               && InicioDoWindows.AAbrir(new[] { lojaSp }, "SpotifyStartupTask", new HashSet<string>(), pdv, pularSpotify: true).Count == 0
               && InicioDoWindows.AAbrir(todos, null, new HashSet<string>(), pdv).Any(p => p.Nome == "Spotify")
               && app.Contains("Vendas.Config(cx, SpotifyNoCaixa.Chave) == \"1\", TimeSpan.FromSeconds(8));")
               && no.Contains("pularSpotify: spotifyComDono"),
            "IW-15 com o Spotify escondido ligado a lista pula qualquer Spotify (quem abre é o PDV); desligado, abre como antes");
        // IW-16 TeamViewer: com serviço fica de fora (o serviço já abre na sessão); sem serviço entra desmarcado e abre minimizado
        var tv = InicioDoWindows.TeamViewer("C:\\P\\TeamViewer\\TeamViewer.exe", temServico: false);
        checar(InicioDoWindows.TeamViewer("C:\\P\\TeamViewer\\TeamViewer.exe", temServico: true) is null && InicioDoWindows.TeamViewer(null, false) is null
               && tv is { Nome: "TeamViewer", Origem: "instalado" } && !InicioDoWindows.Marcado(tv, null)
               && InicioDoWindows.AbreMinimizado(tv) && !InicioDoWindows.AbreMinimizado(todos[1])
               && no.Contains("if (InicioDoWindows.AbreMinimizado(p)) psi.WindowStyle = ProcessWindowStyle.Minimized;"),
            "IW-16 TeamViewer com serviço não abre janela; sem serviço entra desmarcado e abre minimizado; o AnyDesk segue com --tray");
        // IW-16b (revisão 15/10) só o serviço automático conta: manual ou desativado não sobe no boot, então o TeamViewer entra na lista
        checar(InicioDoWindows.ServicoSobeSozinho(2) && !InicioDoWindows.ServicoSobeSozinho(3) && !InicioDoWindows.ServicoSobeSozinho(4)
               && !InicioDoWindows.ServicoSobeSozinho(-1) && !InicioDoWindows.ServicoSobeSozinho(null)
               && InicioDoWindows.TeamViewer("C:\\P\\TeamViewer\\TeamViewer.exe", InicioDoWindows.ServicoSobeSozinho(3)) is { Nome: "TeamViewer" }
               && no.Contains("InicioDoWindows.TeamViewer(tv, InicioDoWindows.ServicoSobeSozinho(InicioDoServico(\"TeamViewer\")))")
               && !no.Contains("InicioDoServico(\"TeamViewer\") is not null"),
            "IW-16b TeamViewer com serviço manual ou desativado não sobe no boot: entra na lista (desmarcado); só o automático fica de fora");
        // IW-17 o estado dos serviços de suporte no log
        checar(InicioDoWindows.Servico(null, false) == "sem serviço" && InicioDoWindows.Servico(2, true) == "serviço automático, rodando"
               && InicioDoWindows.Servico(4, false) == "serviço desativado, parado" && no.Contains("Anotar($\"suporte: AnyDesk "),
            "IW-17 o log diz se o AnyDesk e o TeamViewer têm serviço e se ele está rodando");
        // só informação: o que este PC tem na inicialização (não é teste)
        try { Console.WriteLine("  (info) inicialização deste PC: " + string.Join(", ", InicioDoWindowsNoCaixa.Ler().Select(p => $"{p.Nome} [{p.Origem}]"))); }
        catch { }
    }

    private static string? Fonte(string relativo)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var c = Path.Combine(d.FullName, relativo);
            if (File.Exists(c) && File.Exists(Path.Combine(d.FullName, "Pdv.csproj"))) return File.ReadAllText(c).Replace("\r\n", "\n");
            d = d.Parent;
        }
        return null;
    }
}
