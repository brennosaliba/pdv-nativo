namespace Pdv.Testes;

/// <summary>
/// MODO QUIOSQUE (12/09/2026, pedido do dono: "aparecer somente o PDV no Windows, nem
/// carregar mais nada"). A parte pura (o valor do Shell e o reconhecimento do nosso exe)
/// é provada pelo valor; o registro do Windows não é tocado pela suíte. O caminho na tela
/// (Configuração liga/desliga, Fechar oferece Reiniciar/Sair, App abre o Explorer se
/// morrer) é travado pelo fonte.
/// </summary>
public static class TestesQuiosque
{
    public static void Rodar(Action<bool, string> checar)
    {
        const string exe = @"C:\Program Files\MMFood\Pdv.exe";
        checar(Quiosque.ValorShell(exe) == "\"C:\\Program Files\\MMFood\\Pdv.exe\" --quiosque",
            "o Shell gravado é o exe entre aspas (tem espaço no caminho) com a marca --quiosque");
        checar(Quiosque.EhNosso(Quiosque.ValorShell(exe), exe), "o valor que gravamos é reconhecido como nosso");
        checar(Quiosque.EhNosso("\"c:\\program files\\mmfood\\pdv.exe\" --quiosque", exe), "sem diferenciar caixa (o Windows não diferencia)");
        checar(!Quiosque.EhNosso("explorer.exe", exe) && !Quiosque.EhNosso(null, exe) && !Quiosque.EhNosso("", exe),
            "Explorer, vazio ou nulo: não é o quiosque (Desligar não apaga o que não é nosso)");
        checar(!Quiosque.EhNosso(Quiosque.ValorShell(exe), ""), "sem caminho do exe nada é nosso");
        checar(Quiosque.ArgumentoPresente(new[] { "--quiosque" }) && !Quiosque.ArgumentoPresente(new[] { "--cupom-teste" }) && !Quiosque.ArgumentoPresente(Array.Empty<string>()),
            "a marca --quiosque é reconhecida nos argumentos");
        checar(Quiosque.ExeAtual.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), "o exe atual é um .exe (host do publish em arquivo único)");

        var conf = Fonte(Path.Combine("Telas", "Configuracao.xaml.cs")) ?? "";
        var confXaml = Fonte(Path.Combine("Telas", "Configuracao.xaml")) ?? "";
        var main = Fonte("MainWindow.xaml.cs") ?? "";
        var app = Fonte("App.xaml.cs") ?? "";
        checar(confXaml.Contains("x:Name=\"ChkQuiosque\"") && confXaml.Contains("netplwiz") && confXaml.Contains("Click=\"SairParaOWindows\""),
            "a Configuração tem a opção, explica o login automático (netplwiz, com o dono) e o botão de abrir a área de trabalho");
        checar(conf.Contains("ChkQuiosque.IsChecked = Quiosque.Ligado") && conf.Contains("quiosqueQuer ? Quiosque.Ligar() : Quiosque.Desligar()") && conf.Contains("\"quiosque_ligado\""),
            "a tela lê o estado do Windows (não do banco), só mexe se mudou, e audita");
        checar(main.Contains("if (Quiosque.Ligado)") && main.Contains("\"Reiniciar o PDV\", \"Sair para o Windows\", \"Voltar\"") && main.Contains("Quiosque.ReiniciarPdv()"),
            "no quiosque, Fechar oferece Reiniciar o PDV ou Sair para o Windows (nunca tela preta)");
        checar(app.Contains("if (a.IsTerminating) { try { if (Quiosque.Ligado) Quiosque.AbrirExplorer(); } catch { } }"),
            "se o PDV morrer como shell, o App abre o Explorer antes de cair");
        var q = Fonte("Quiosque.cs") ?? "";
        checar(q.Contains("Registry.CurrentUser") && !q.Contains("Registry.LocalMachine") && !q.Contains("AutoAdminLogon") && !q.Contains("DefaultPassword"),
            "só o registro do USUÁRIO (sem UAC) e nunca senha do Windows no registro");
    }

    private static string? Fonte(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj")))
            {
                var c = Path.Combine(dir.FullName, relativo);
                return File.Exists(c) ? File.ReadAllText(c) : null;
            }
        return null;
    }
}
