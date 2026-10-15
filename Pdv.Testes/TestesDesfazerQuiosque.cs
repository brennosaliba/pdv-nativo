using Microsoft.Win32;
using Pdv.Instalador;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// DESINSTALAR DESFAZ O QUIOSQUE DO USUÁRIO DO CAIXA (1.0.36, 14/10/2026, revisão da trava).
/// Antes, desinstalar apagava o Pdv.exe e deixava no registro do usuário do caixa o Shell apontando
/// para ele e as quatro políticas da trava: na próxima entrada, tela preta e um Ctrl+Alt+Del só com
/// Cancelar. Agora o desinstalador (já administrador) percorre os perfis carregados em HKEY_USERS e
/// desfaz só onde o Shell é o nosso.
///
///  DQ-1..5  a regra pura (Pdv.Nucleo/DesfazerQuiosque): o formato do Shell, quem é do caixa, qual
///           política é nossa, o plano de um perfil, quais chaves de HKEY_USERS são de pessoa
///  DQ-6..7  o registro (Pdv.Instalador/QuiosqueDoCaixa) numa HKEY_USERS de mentira, ISOLADA
///           (HKCU\Software\PdvNativoTeste\guid), criada e apagada aqui; o Winlogon e as Policies de
///           verdade deste PC são lidos antes e depois e não mudam
///  DQ-8..10 o fonte (a ordem no Desinstalar, a raiz vinda de fora), os exes do caixa e o texto
/// </summary>
public static class TestesDesfazerQuiosque
{
    private const string Caixa = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string ComExplorer = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private const string ExeSumiu = "S-1-5-21-1111111111-2222222222-3333333333-1003";
    private const string CopiaViva = "S-1-5-21-1111111111-2222222222-3333333333-1004";
    private const string ComMais = "S-1-5-21-1111111111-2222222222-3333333333-1005";
    private const string Entra = "S-1-12-1-1234567890-1234567890-1234567890-1234567890";

    private const string ExeHoje = @"C:\Program Files\MMFood\Pdv.exe";
    private const string ExeAntigo = @"C:\Program Files\PDV MMTech\Pdv.exe";
    private const string ExeApagado = @"C:\Program Files\PDV American Day\Pdv.exe";
    private const string ExeCopia = @"D:\Copia\Pdv.exe";

    public static void Rodar(Action<bool, string> checar)
    {
        var removidos = new[] { ExeHoje, ExeAntigo };
        var vivos = new HashSet<string>(new[] { ExeHoje, ExeAntigo, ExeCopia }, StringComparer.OrdinalIgnoreCase);
        bool Existe(string exe) => vivos.Contains(exe);

        // ── DQ-1 o formato do Shell: um só, o que o PDV grava ──
        checar(Quiosque.ValorShell(ExeHoje) == DesfazerQuiosque.ShellDoPdv(ExeHoje)
               && Quiosque.ValorShell(ExeHoje) == "\"" + ExeHoje + "\" --quiosque"
               && Quiosque.Argumento == DesfazerQuiosque.MarcaDoQuiosque
               && DesfazerQuiosque.ExeDoShell(Quiosque.ValorShell(ExeHoje)) == ExeHoje
               && DesfazerQuiosque.ExeDoShell(Quiosque.ValorShell(@"\\servidor\pdv\Pdv.exe")) == @"\\servidor\pdv\Pdv.exe",
            "DQ-1 o Shell que o PDV grava é o que o desinstalador lê (um formato só, no núcleo)");
        string?[] naoSao =
        {
            null, "", "explorer.exe", "\"" + ExeHoje + "\"", "\"" + ExeHoje + "\" --quiosque --mais",
            ExeHoje + " --quiosque", "\"Pdv.exe\" --quiosque", "\"C:Pdv.exe\" --quiosque", "\"C:\\X\\Outro.exe\" --quiosque",
            "\"" + ExeHoje + "\"  --quiosque", "\"\" --quiosque", "\"" + ExeHoje + "\" --QUIOSQUE",
            "\"C:\\X\\Pdv.exe.bak\" --quiosque", "\"C:\\X\\\"Pdv.exe\" --quiosque", " \"" + ExeHoje + "\" --quiosque",
            "\"" + ExeHoje + "\" --quiosque\n", "cmd.exe /c \"" + ExeHoje + "\" --quiosque",
        };
        var passaram = naoSao.Where(v => DesfazerQuiosque.ExeDoShell(v) is not null).Select(v => (v ?? "null").Replace("\n", "\\n")).ToList();
        checar(passaram.Count == 0,
            "DQ-1b Explorer, vazio, outro programa, caminho relativo, sem a marca ou com algo a mais não são o Shell do PDV"
            + (passaram.Count == 0 ? "" : " [passaram: " + string.Join(" | ", passaram) + "]"));

        // ── DQ-2 o Shell é do caixa que está saindo? ──
        checar(DesfazerQuiosque.ShellDoCaixa(DesfazerQuiosque.ShellDoPdv(ExeHoje), removidos, Existe)
               && DesfazerQuiosque.ShellDoCaixa(DesfazerQuiosque.ShellDoPdv(@"c:\program files\mmfood\PDV.EXE"), removidos, Existe)
               && DesfazerQuiosque.ShellDoCaixa(DesfazerQuiosque.ShellDoPdv(ExeAntigo), removidos, Existe),
            "DQ-2 Shell no Pdv.exe que o desinstalador remove (pasta de hoje ou do nome antigo, maiúscula ou minúscula): é do caixa");
        checar(DesfazerQuiosque.ShellDoCaixa(DesfazerQuiosque.ShellDoPdv(ExeApagado), removidos, Existe),
            "DQ-2b Shell num Pdv.exe que já não existe (tela preta na próxima entrada): é do caixa");
        checar(!DesfazerQuiosque.ShellDoCaixa(DesfazerQuiosque.ShellDoPdv(ExeCopia), removidos, Existe)
               && !DesfazerQuiosque.ShellDoCaixa("explorer.exe", removidos, Existe)
               && !DesfazerQuiosque.ShellDoCaixa("\"C:\\Kiosk\\Outro.exe\" --quiosque", removidos, _ => false)
               && !DesfazerQuiosque.ShellDoCaixa(null, removidos, Existe)
               && !DesfazerQuiosque.ShellDoCaixa(DesfazerQuiosque.ShellDoPdv(ExeCopia), removidos, _ => throw new IOException("disco")),
            "DQ-2c Pdv.exe que continua vivo em outra pasta, Explorer, outro quiosque ou conferência que falha: não toca");

        // ── DQ-3 a política é a nossa só como a trava grava ──
        checar(DesfazerQuiosque.PoliticaDaTrava(1)
               && !DesfazerQuiosque.PoliticaDaTrava(0) && !DesfazerQuiosque.PoliticaDaTrava(2) && !DesfazerQuiosque.PoliticaDaTrava("1")
               && !DesfazerQuiosque.PoliticaDaTrava(1L) && !DesfazerQuiosque.PoliticaDaTrava(null) && !DesfazerQuiosque.PoliticaDaTrava(new byte[] { 1 }),
            "DQ-3 só DWORD 1 é a trava; 0, 2, texto, QWORD ou binário não são nossos");

        // ── DQ-4 o plano de um perfil ──
        var politicas = new Dictionary<string, object?>
        {
            ["DisableTaskMgr"] = 1, ["DisableLockWorkstation"] = 0, ["DisableChangePassword"] = "1", ["NoLogoff"] = 1,
        };
        var plano = DesfazerQuiosque.Decidir(DesfazerQuiosque.ShellDoPdv(ExeHoje), (_, v) => politicas[v], removidos, Existe);
        checar(plano.TirarShell && plano.Politicas.Select(p => p.Valor).SequenceEqual(new[] { "DisableTaskMgr", "NoLogoff" }),
            "DQ-4 Shell do caixa: tira o Shell e só as políticas em DWORD 1 (o 0 e o texto ficam)");
        var semShell = DesfazerQuiosque.Decidir("explorer.exe", (_, _) => 1, removidos, Existe);
        checar(!semShell.TirarShell && semShell.Politicas.Count == 0,
            "DQ-4b Shell que não é do caixa: não toca em nada, nem nas políticas (podem ser de uma GPO ou de outra pessoa)");
        var lancou = DesfazerQuiosque.Decidir(DesfazerQuiosque.ShellDoPdv(ExeHoje),
            (_, v) => v == "DisableTaskMgr" ? throw new UnauthorizedAccessException() : 1, removidos, Existe);
        checar(lancou.TirarShell && lancou.Politicas.Select(p => p.Valor).SequenceEqual(new[] { "DisableLockWorkstation", "DisableChangePassword", "NoLogoff" }),
            "DQ-4c política que não deu para ler fica como está; as outras e o Shell saem");

        // ── DQ-5 só perfil de pessoa ──
        checar(DesfazerQuiosque.PerfilDePessoa(Caixa) && DesfazerQuiosque.PerfilDePessoa(Entra)
               && !DesfazerQuiosque.PerfilDePessoa(Caixa + "_Classes") && !DesfazerQuiosque.PerfilDePessoa(".DEFAULT")
               && !DesfazerQuiosque.PerfilDePessoa("S-1-5-18") && !DesfazerQuiosque.PerfilDePessoa("S-1-5-19")
               && !DesfazerQuiosque.PerfilDePessoa("S-1-5-20") && !DesfazerQuiosque.PerfilDePessoa(null),
            "DQ-5 em HKEY_USERS só os perfis de pessoa contam (.DEFAULT, SYSTEM, serviço e SID_Classes ficam)");

        // ── DQ-6 o registro, numa HKEY_USERS de mentira e isolada ──
        var antes = Verdade();
        const string Pai = @"Software\PdvNativoTeste";
        var nome = Pai + @"\" + Guid.NewGuid().ToString("N");
        var usuarios = nome + @"\usuarios";
        try
        {
            using (Registry.CurrentUser.CreateSubKey(usuarios)) { }
            bool isolada;
            using (var r = Registry.CurrentUser.OpenSubKey(usuarios))
                isolada = r is not null && r.Name.StartsWith(@"HKEY_CURRENT_USER\Software\PdvNativoTeste\", StringComparison.OrdinalIgnoreCase);
            checar(isolada, "DQ-6a a HKEY_USERS de teste existe e é isolada (HKCU\\Software\\PdvNativoTeste\\guid\\usuarios)");
            if (isolada)
            {
                // o caixa: Shell nosso, as quatro em 1, e coisas que NÃO são nossas ao lado
                Perfil(usuarios, Caixa, DesfazerQuiosque.ShellDoPdv(ExeHoje), 1, 1, 1, 1);
                using (var k = Registry.CurrentUser.CreateSubKey(usuarios + @"\" + Caixa + @"\" + DesfazerQuiosque.ChaveWinlogon))
                    k.SetValue("AutoRestartShell", 1, RegistryValueKind.DWord);
                using (var k = Registry.CurrentUser.CreateSubKey(usuarios + @"\" + Caixa + @"\" + TravaDoCtrlAltDel.ChaveSistema))
                    k.SetValue("DisableRegistryTools", 1, RegistryValueKind.DWord);
                Perfil(usuarios, ComExplorer, "explorer.exe", 1, 1, 1, 1);                  // Shell de outro: nada
                Perfil(usuarios, ExeSumiu, DesfazerQuiosque.ShellDoPdv(ExeApagado), 0, null, null, 1); // exe sumiu: Shell e NoLogoff
                Perfil(usuarios, CopiaViva, DesfazerQuiosque.ShellDoPdv(ExeCopia), 1, 1, 1, 1);         // cópia viva: nada
                Perfil(usuarios, ComMais, DesfazerQuiosque.ShellDoPdv(ExeHoje) + " --mais", 1, 1, 1, 1); // formato diferente: nada
                Perfil(usuarios, Entra, DesfazerQuiosque.ShellDoPdv(@"c:\program files\mmfood\PDV.EXE"), 1, 1, 1, 1);
                Perfil(usuarios, Caixa + "_Classes", DesfazerQuiosque.ShellDoPdv(ExeHoje), 1, 1, 1, 1);  // não é perfil
                Perfil(usuarios, ".DEFAULT", DesfazerQuiosque.ShellDoPdv(ExeHoje), 1, 1, 1, 1);
                Perfil(usuarios, "S-1-5-18", DesfazerQuiosque.ShellDoPdv(ExeHoje), 1, 1, 1, 1);

                IReadOnlyList<QuiosqueDoCaixa.Desfeito> feitos;
                using (var raiz = Registry.CurrentUser.OpenSubKey(usuarios)!)
                    feitos = QuiosqueDoCaixa.DesfazerNosPerfis(raiz, removidos, Existe);
                var porSid = feitos.ToDictionary(f => f.Sid, f => f.Politicas);
                checar(porSid.Count == 3 && porSid.GetValueOrDefault(Caixa, -1) == 4 && porSid.GetValueOrDefault(Entra, -1) == 4
                       && porSid.GetValueOrDefault(ExeSumiu, -1) == 1,
                    "DQ-6 desfaz só o caixa, a conta Entra do caixa e o Shell de exe que sumiu ["
                    + string.Join(", ", feitos.Select(f => f.Sid[^4..] + ":" + f.Politicas)) + "]");

                checar(Shell(usuarios, Caixa) is null && Politicas(usuarios, Caixa).All(v => v is null)
                       && Valor(usuarios, Caixa, DesfazerQuiosque.ChaveWinlogon, "AutoRestartShell") is 1
                       && Valor(usuarios, Caixa, TravaDoCtrlAltDel.ChaveSistema, "DisableRegistryTools") is 1,
                    "DQ-6b no caixa saem o Shell e as quatro políticas; o resto do Winlogon e uma política que não é da trava ficam");
                checar(Shell(usuarios, ExeSumiu) is null && Politicas(usuarios, ExeSumiu).SequenceEqual(new object?[] { 0, null, null, null }),
                    "DQ-6c Shell de exe que sumiu sai; o DWORD 0 (não é nosso) fica");
                var intactos = new[] { ComExplorer, CopiaViva, ComMais, Caixa + "_Classes", ".DEFAULT", "S-1-5-18" }
                    .Where(s => Shell(usuarios, s) is null || !Politicas(usuarios, s).All(v => v is 1)).ToList();
                checar(intactos.Count == 0,
                    "DQ-6d Shell de outro programa, cópia viva, formato diferente, SID_Classes, .DEFAULT e SYSTEM: nada mudou"
                    + (intactos.Count == 0 ? "" : " [mudaram: " + string.Join(", ", intactos) + "]"));

                IReadOnlyList<QuiosqueDoCaixa.Desfeito> deNovo;
                using (var raiz = Registry.CurrentUser.OpenSubKey(usuarios)!)
                    deNovo = QuiosqueDoCaixa.DesfazerNosPerfis(raiz, removidos, Existe);
                checar(deNovo.Count == 0, "DQ-6e rodar de novo não acha nada (e não lança)");

                // metade feita: as políticas já saíram e o Shell ficou (algo falhou no meio). Rodar de
                // novo ainda acha o Shell e termina.
                Perfil(usuarios, Caixa, DesfazerQuiosque.ShellDoPdv(ExeHoje), null, null, null, null);
                using (var raiz = Registry.CurrentUser.OpenSubKey(usuarios)!)
                    deNovo = QuiosqueDoCaixa.DesfazerNosPerfis(raiz, removidos, Existe);
                checar(deNovo.Count == 1 && deNovo[0] is { Sid: Caixa, Politicas: 0 } && Shell(usuarios, Caixa) is null,
                    "DQ-6f com as políticas já fora e o Shell ainda lá, rodar de novo tira o Shell");

                // um perfil sem Winlogon nenhum (perfil novo) e uma raiz vazia: nada a fazer, sem lançar
                using (Registry.CurrentUser.CreateSubKey(usuarios + @"\S-1-5-21-1-2-3-1999")) { }
                using (Registry.CurrentUser.CreateSubKey(nome + @"\vazia")) { }
                IReadOnlyList<QuiosqueDoCaixa.Desfeito> semNada, vazia;
                using (var raiz = Registry.CurrentUser.OpenSubKey(usuarios)!) semNada = QuiosqueDoCaixa.DesfazerNosPerfis(raiz, removidos, Existe);
                using (var raiz = Registry.CurrentUser.OpenSubKey(nome + @"\vazia")!) vazia = QuiosqueDoCaixa.DesfazerNosPerfis(raiz, removidos, Existe);
                checar(semNada.Count == 0 && vazia.Count == 0, "DQ-6g perfil sem Winlogon e HKEY_USERS vazia: nada a fazer, sem erro");
            }
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
        bool sobrou;
        using (var k = Registry.CurrentUser.OpenSubKey(nome)) sobrou = k is not null;
        checar(!sobrou, "DQ-7 a chave de teste foi apagada no fim");
        checar(Verdade() == antes, "DQ-7b o Shell e as políticas de verdade deste PC não mudaram");

        // ── DQ-8 o fonte ──
        var inst = Fonte("Pdv.Instalador", "Instalacao.cs") ?? "";
        var qdc = Fonte("Pdv.Instalador", "QuiosqueDoCaixa.cs") ?? "";
        var appInst = Fonte("Pdv.Instalador", "App.xaml.cs") ?? "";
        var csprojInst = Fonte("Pdv.Instalador", "Pdv.Instalador.csproj") ?? "";
        var desinstalar = Trecho(inst, "public static string? Desinstalar(out int quiosquesDesfeitos)", "public static IReadOnlyList<string> ExesDoCaixa()");
        var iDesfaz = desinstalar.IndexOf("quiosquesDesfeitos = DesfazerQuiosqueDoCaixa();", StringComparison.Ordinal);
        var iChave = desinstalar.IndexOf("DeleteSubKeyTree(ChaveUninstall,", StringComparison.Ordinal);
        var iPasta = desinstalar.IndexOf("Directory.Delete(pasta", StringComparison.Ordinal);
        checar(iDesfaz > 0 && iChave > iDesfaz && iPasta > iDesfaz && desinstalar.IndexOf("try", StringComparison.Ordinal) > iDesfaz
               && inst.Contains("public static string? Desinstalar() => Desinstalar(out _);", StringComparison.Ordinal),
            "DQ-8 o Desinstalar desfaz o quiosque PRIMEIRO (antes de apagar a chave que acha o InstallLocation e antes de apagar o Pdv.exe)");
        var desfazer = Trecho(inst, "public static int DesfazerQuiosqueDoCaixa()", "\n    }");
        checar(desfazer.Contains("QuiosqueDoCaixa.DesfazerNosPerfis(Registry.Users, ExesDoCaixa(), File.Exists)", StringComparison.Ordinal)
               && desfazer.Contains("catch { return 0; }", StringComparison.Ordinal),
            "DQ-8b na loja a raiz é HKEY_USERS, os exes são os que o desinstalador remove, e um erro nunca segura a desinstalação");
        checar(qdc.Length > 0 && !qdc.Contains("Registry.LocalMachine", StringComparison.Ordinal) && !qdc.Contains("Registry.CurrentUser", StringComparison.Ordinal)
               && !qdc.Contains("Registry.Users", StringComparison.Ordinal) && !qdc.Contains("Process.Start", StringComparison.Ordinal),
            "DQ-8c o registro do desinstalador recebe a raiz de fora (nunca HKLM nem o HKCU de quem desinstala) e não roda processo");
        checar(appInst.Contains("Instalacao.Desinstalar(out var quiosques)", StringComparison.Ordinal)
               && appInst.Contains("DesfazerQuiosque.Aviso(quiosques)", StringComparison.Ordinal)
               && csprojInst.Contains(@"Include=""..\Pdv.Nucleo\TravaDoCtrlAltDel.cs""", StringComparison.Ordinal)
               && csprojInst.Contains(@"Include=""..\Pdv.Nucleo\DesfazerQuiosque.cs""", StringComparison.Ordinal),
            "DQ-8d o --desinstalar usa o Desinstalar que desfaz o quiosque e avisa; o instalador compila a MESMA regra do caixa");

        // ── DQ-9 os exes do caixa ──
        var exes = Instalacao.ExesDoCaixa();
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        checar(exes.Contains(Path.Combine(Instalacao.PastaDestinoPadrao, "Pdv.exe"), StringComparer.OrdinalIgnoreCase)
               && Instalacao.NomesAntigos.All(n => exes.Contains(Path.Combine(pf, n, "Pdv.exe"), StringComparer.OrdinalIgnoreCase))
               && exes.All(e => Path.GetFileName(e) == "Pdv.exe" && Path.IsPathFullyQualified(e))
               && exes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == exes.Count,
            "DQ-9 os exes do caixa são o Pdv.exe da pasta de hoje e das pastas dos nomes antigos (caminho absoluto, sem repetir)");

        // ── DQ-10 o texto ──
        var aviso = DesfazerQuiosque.Aviso(1);
        checar(DesfazerQuiosque.Aviso(0) == "" && aviso.Length is > 0 and <= 110 && !aviso.Contains('\u2014') && !aviso.Contains('\u2013')
               && !System.Text.RegularExpressions.Regex.IsMatch(aviso, "(?i)shell|registro|hkey|polic|sid|winlogon"),
            "DQ-10 o aviso do desinstalador é uma linha curta, sem travessão e sem termo técnico: '" + aviso.Trim() + "'");
    }

    /// <summary>Monta um perfil de mentira: Shell (null = sem) e as quatro políticas (null = sem o valor).</summary>
    private static void Perfil(string usuarios, string sid, string? shell, object? taskMgr, object? lockWs, object? senha, object? logoff)
    {
        var raiz = usuarios + @"\" + sid;
        using (var w = Registry.CurrentUser.CreateSubKey(raiz + @"\" + DesfazerQuiosque.ChaveWinlogon))
        {
            if (shell is null) w.DeleteValue(DesfazerQuiosque.ValorShell, throwOnMissingValue: false);
            else w.SetValue(DesfazerQuiosque.ValorShell, shell, RegistryValueKind.String);
        }
        var valores = new[] { taskMgr, lockWs, senha, logoff };
        for (var i = 0; i < 4; i++)
        {
            var (chave, valor) = TravaDoCtrlAltDel.Travas[i];
            using var k = Registry.CurrentUser.CreateSubKey(raiz + @"\" + chave);
            switch (valores[i])
            {
                case null: k.DeleteValue(valor, throwOnMissingValue: false); break;
                case int n: k.SetValue(valor, n, RegistryValueKind.DWord); break;
                case string s: k.SetValue(valor, s, RegistryValueKind.String); break;
            }
        }
    }

    private static string? Shell(string usuarios, string sid)
        => Valor(usuarios, sid, DesfazerQuiosque.ChaveWinlogon, DesfazerQuiosque.ValorShell) as string;

    private static object?[] Politicas(string usuarios, string sid)
        => TravaDoCtrlAltDel.Travas.Select(t => Valor(usuarios, sid, t.Chave, t.Valor)).ToArray();

    private static object? Valor(string usuarios, string sid, string chave, string valor)
    {
        using var k = Registry.CurrentUser.OpenSubKey(usuarios + @"\" + sid + @"\" + chave);
        return k?.GetValue(valor);
    }

    /// <summary>O Shell e as quatro políticas DE VERDADE deste usuário (só leitura), para comparar antes e depois.</summary>
    private static string Verdade()
    {
        string? shell;
        try
        {
            using var w = Registry.CurrentUser.OpenSubKey(DesfazerQuiosque.ChaveWinlogon);
            shell = w?.GetValue(DesfazerQuiosque.ValorShell) as string;
        }
        catch { shell = "(não li)"; }
        var pol = TravaDoCtrlAltDel.Travas.Select(t =>
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(t.Chave); return k?.GetValue(t.Valor)?.ToString() ?? "-"; }
            catch { return "(não li)"; }
        });
        return (shell ?? "-") + "|" + string.Join(",", pol);
    }

    private static string Trecho(string todo, string de, string ate)
    {
        var i = todo.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var f = todo.IndexOf(ate, i + de.Length, StringComparison.Ordinal);
        return f < 0 ? "" : todo[i..f];
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
