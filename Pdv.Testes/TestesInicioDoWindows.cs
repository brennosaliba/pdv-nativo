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
        var iSp = app.IndexOf("SpotifyNoCaixa.Iniciar();", StringComparison.Ordinal);
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
               && qui.Contains("Registry.CurrentUser.CreateSubKey(chave)") && !qui.Contains("Registry.LocalMachine")
               && cfg.Contains("var travarQuer = ChkTravarAtalhos.IsChecked == true && Quiosque.Ligado;")
               && cfgXaml.Contains("x:Name=\"ChkTravarAtalhos\""),
            "IW-12 a trava do Ctrl+Alt+Del grava só as 4 políticas do usuário (HKCU, nunca a máquina), só com o quiosque ligado, e desligar o quiosque desfaz");
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
