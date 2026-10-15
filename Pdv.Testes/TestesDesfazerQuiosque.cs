using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Pdv.Instalador;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// DESINSTALAR DESFAZ O QUIOSQUE DO USUÁRIO DO CAIXA (1.0.36, 14/10/2026, revisão da trava).
/// Antes, desinstalar apagava o Pdv.exe e deixava no registro do usuário do caixa o Shell apontando
/// para ele e as quatro políticas da trava: na próxima entrada, tela preta e um Ctrl+Alt+Del só com
/// Cancelar. Agora o desinstalador (já administrador) percorre os perfis carregados em HKEY_USERS e
/// desfaz só o que é nosso: o Shell do caixa que sai, e a trava só com a MARCA do PDV.
///
///  DQ-1..5   a regra pura (Pdv.Nucleo/DesfazerQuiosque): o formato do Shell, quem é do caixa, qual
///            política é nossa (a marca), o plano de um perfil, quais chaves de HKEY_USERS são de pessoa
///  DQ-6..7   o registro (Pdv.Instalador/QuiosqueDoCaixa) numa HKEY_USERS de mentira, ISOLADA
///            (HKCU\Software\PdvNativoTeste\guid), criada e apagada aqui; o Winlogon e as Policies de
///            verdade deste PC são lidos antes e depois e não mudam
///  DQ-11     o que não sai: valor que o Windows não deixa apagar (ACL de negação na chave isolada).
///            Shell nosso preso segura a desinstalação; política presa vira aviso e a marca fica
///  DQ-8..10  o fonte (a ordem no Desinstalar e no perfil, a raiz vinda de fora), os exes do caixa e o texto
/// </summary>
public static class TestesDesfazerQuiosque
{
    private const string Caixa = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string ComExplorer = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private const string ExeSumiu = "S-1-5-21-1111111111-2222222222-3333333333-1003";
    private const string CopiaViva = "S-1-5-21-1111111111-2222222222-3333333333-1004";
    private const string ComMais = "S-1-5-21-1111111111-2222222222-3333333333-1005";
    private const string SemMarca = "S-1-5-21-1111111111-2222222222-3333333333-1006";
    private const string SoTrava = "S-1-5-21-1111111111-2222222222-3333333333-1007";
    private const string PoliticaPresa = "S-1-5-21-1111111111-2222222222-3333333333-1008";
    private const string ShellPreso = "S-1-5-21-1111111111-2222222222-3333333333-1009";
    private const string Entra = "S-1-12-1-1234567890-1234567890-1234567890-1234567890";

    private const string ExeHoje = @"C:\Program Files\MMFood\Pdv.exe";
    private const string ExeAntigo = @"C:\Program Files\PDV MMTech\Pdv.exe";
    private const string ExeApagado = @"C:\Program Files\PDV American Day\Pdv.exe";
    private const string ExeCopia = @"D:\Copia\Pdv.exe";

    private static readonly string Marca = TravaDoCtrlAltDel.ValorDaMarca;

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
        checar(DesfazerQuiosque.ShellDeOutroPdv(DesfazerQuiosque.ShellDoPdv(ExeCopia), removidos, Existe)
               && DesfazerQuiosque.ShellDeOutroPdv(DesfazerQuiosque.ShellDoPdv(ExeCopia), removidos, _ => throw new IOException("disco"))
               && !DesfazerQuiosque.ShellDeOutroPdv(DesfazerQuiosque.ShellDoPdv(ExeHoje), removidos, Existe)
               && !DesfazerQuiosque.ShellDeOutroPdv(DesfazerQuiosque.ShellDoPdv(ExeApagado), removidos, Existe)
               && !DesfazerQuiosque.ShellDeOutroPdv("explorer.exe", removidos, Existe) && !DesfazerQuiosque.ShellDeOutroPdv(null, removidos, Existe),
            "DQ-2d Shell de outro Pdv.exe vivo (ou que não deu para conferir) é de outra instalação; o nosso, um apagado, o Explorer e o vazio não");

        // ── DQ-3 a política (e a marca) é nossa só como a trava grava ──
        checar(DesfazerQuiosque.PoliticaDaTrava(1)
               && !DesfazerQuiosque.PoliticaDaTrava(0) && !DesfazerQuiosque.PoliticaDaTrava(2) && !DesfazerQuiosque.PoliticaDaTrava("1")
               && !DesfazerQuiosque.PoliticaDaTrava(1L) && !DesfazerQuiosque.PoliticaDaTrava(null) && !DesfazerQuiosque.PoliticaDaTrava(new byte[] { 1 }),
            "DQ-3 só DWORD 1 é a trava; 0, 2, texto, QWORD ou binário não são nossos");

        // ── DQ-4 o plano de um perfil ──
        Func<string, string, object?> Ler(Dictionary<string, object?> d) => (_, v) => d.TryGetValue(v, out var x) ? x : null;
        var comMarca = new Dictionary<string, object?>
        {
            [Marca] = 1, ["DisableTaskMgr"] = 1, ["DisableLockWorkstation"] = 0, ["DisableChangePassword"] = "1", ["NoLogoff"] = 1,
        };
        var plano = DesfazerQuiosque.Decidir(DesfazerQuiosque.ShellDoPdv(ExeHoje), Ler(comMarca), removidos, Existe);
        checar(plano.TirarShell && plano.TirarMarca && plano.Politicas.Select(p => p.Valor).SequenceEqual(new[] { "DisableTaskMgr", "NoLogoff" }),
            "DQ-4 Shell do caixa com a marca: tira o Shell, só as políticas em DWORD 1 (o 0 e o texto ficam) e a marca");
        var todasSemMarca = new Dictionary<string, object?> { ["DisableTaskMgr"] = 1, ["DisableLockWorkstation"] = 1, ["DisableChangePassword"] = 1, ["NoLogoff"] = 1 };
        var semMarca = DesfazerQuiosque.Decidir(DesfazerQuiosque.ShellDoPdv(ExeHoje), Ler(todasSemMarca), removidos, Existe);
        checar(semMarca.TirarShell && !semMarca.TirarMarca && semMarca.Politicas.Count == 0,
            "DQ-4a Shell do caixa SEM a marca: tira só o Shell; as quatro em 1 podem ser da TI ou de uma GPO local e ficam");
        var semShell = DesfazerQuiosque.Decidir("explorer.exe", Ler(todasSemMarca), removidos, Existe);
        checar(!semShell.TirarShell && !semShell.TirarMarca && semShell.Politicas.Count == 0 && !semShell.AlgumaCoisa,
            "DQ-4b Shell que não é do caixa e sem marca: não toca em nada");
        var soTrava = DesfazerQuiosque.Decidir("explorer.exe", Ler(new Dictionary<string, object?>(todasSemMarca) { [Marca] = 1 }), removidos, Existe);
        checar(!soTrava.TirarShell && soTrava.TirarMarca && soTrava.Politicas.Count == 4,
            "DQ-4c quiosque desligado por fora com a trava e a marca: o Shell (do Explorer) fica, a trava e a marca saem");
        var lancou = DesfazerQuiosque.Decidir(DesfazerQuiosque.ShellDoPdv(ExeHoje),
            (_, v) => v == "DisableTaskMgr" ? throw new UnauthorizedAccessException() : 1, removidos, Existe);
        checar(lancou.TirarShell && lancou.TirarMarca
               && lancou.Politicas.Select(p => p.Valor).SequenceEqual(new[] { "DisableLockWorkstation", "DisableChangePassword", "NoLogoff" }),
            "DQ-4d política que não deu para ler fica como está; as outras, o Shell e a marca saem");
        var marcaIlegivel = DesfazerQuiosque.Decidir(DesfazerQuiosque.ShellDoPdv(ExeHoje),
            (_, v) => v == Marca ? throw new UnauthorizedAccessException() : 1, removidos, Existe);
        checar(marcaIlegivel.TirarShell && !marcaIlegivel.TirarMarca && marcaIlegivel.Politicas.Count == 0,
            "DQ-4e marca que não deu para ler conta como sem marca: só o Shell sai");
        var outroPdv = DesfazerQuiosque.Decidir(DesfazerQuiosque.ShellDoPdv(ExeCopia), Ler(new Dictionary<string, object?>(todasSemMarca) { [Marca] = 1 }), removidos, Existe);
        checar(!outroPdv.AlgumaCoisa,
            "DQ-4f perfil cujo Shell é de outro Pdv.exe vivo: nem o Shell nem a trava (com marca) saem, são daquela instalação");

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
        var falhas = nome + @"\falhas";
        var negadas = new List<string>();
        try
        {
            using (Registry.CurrentUser.CreateSubKey(usuarios)) { }
            bool isolada;
            using (var r = Registry.CurrentUser.OpenSubKey(usuarios))
                isolada = r is not null && r.Name.StartsWith(@"HKEY_CURRENT_USER\Software\PdvNativoTeste\", StringComparison.OrdinalIgnoreCase);
            checar(isolada, "DQ-6a a HKEY_USERS de teste existe e é isolada (HKCU\\Software\\PdvNativoTeste\\guid\\usuarios)");
            if (isolada)
            {
                // o caixa: Shell nosso, as quatro e a marca em 1, e coisas que NÃO são nossas ao lado
                Perfil(usuarios, Caixa, DesfazerQuiosque.ShellDoPdv(ExeHoje), 1, 1, 1, 1, marca: 1);
                using (var k = Registry.CurrentUser.CreateSubKey(usuarios + @"\" + Caixa + @"\" + DesfazerQuiosque.ChaveWinlogon))
                    k.SetValue("AutoRestartShell", 1, RegistryValueKind.DWord);
                using (var k = Registry.CurrentUser.CreateSubKey(usuarios + @"\" + Caixa + @"\" + TravaDoCtrlAltDel.ChaveSistema))
                    k.SetValue("DisableRegistryTools", 1, RegistryValueKind.DWord);
                Perfil(usuarios, ComExplorer, "explorer.exe", 1, 1, 1, 1, marca: null);                         // Shell de outro, sem marca: nada
                Perfil(usuarios, ExeSumiu, DesfazerQuiosque.ShellDoPdv(ExeApagado), 0, null, null, 1, marca: 1); // exe sumiu: Shell, NoLogoff e marca
                Perfil(usuarios, CopiaViva, DesfazerQuiosque.ShellDoPdv(ExeCopia), 1, 1, 1, 1, marca: 1);        // cópia viva: nada
                Perfil(usuarios, ComMais, DesfazerQuiosque.ShellDoPdv(ExeHoje) + " --mais", 1, 1, 1, 1, marca: null); // formato diferente: nada
                Perfil(usuarios, Entra, DesfazerQuiosque.ShellDoPdv(@"c:\program files\mmfood\PDV.EXE"), 1, 1, 1, 1, marca: 1);
                Perfil(usuarios, SemMarca, DesfazerQuiosque.ShellDoPdv(ExeHoje), 1, 1, 1, 1, marca: null);       // só o Shell sai
                Perfil(usuarios, SoTrava, "explorer.exe", 1, 1, 1, 1, marca: 1);                                  // só a trava sai
                Perfil(usuarios, Caixa + "_Classes", DesfazerQuiosque.ShellDoPdv(ExeHoje), 1, 1, 1, 1, marca: 1); // não é perfil
                Perfil(usuarios, ".DEFAULT", DesfazerQuiosque.ShellDoPdv(ExeHoje), 1, 1, 1, 1, marca: 1);
                Perfil(usuarios, "S-1-5-18", DesfazerQuiosque.ShellDoPdv(ExeHoje), 1, 1, 1, 1, marca: 1);

                IReadOnlyList<QuiosqueDoCaixa.Desfeito> feitos;
                using (var raiz = Registry.CurrentUser.OpenSubKey(usuarios)!)
                    feitos = QuiosqueDoCaixa.DesfazerNosPerfis(raiz, removidos, Existe);
                var porSid = feitos.ToDictionary(f => f.Sid);
                bool Foi(string sid, bool shell, int politicas, bool marca)
                    => porSid.TryGetValue(sid, out var f) && f.TirouShell == shell && f.Politicas == politicas && f.TirouMarca == marca && !f.ShellFicou && !f.TravaFicou;
                checar(porSid.Count == 5 && Foi(Caixa, true, 4, true) && Foi(Entra, true, 4, true) && Foi(ExeSumiu, true, 1, true)
                       && Foi(SemMarca, true, 0, false) && Foi(SoTrava, false, 4, true),
                    "DQ-6 desfaz o caixa, a conta Entra do caixa, o Shell de exe que sumiu, o Shell sem marca (só ele) e a trava com marca sem o Shell ["
                    + string.Join(", ", feitos.Select(f => f.Sid[^4..] + ":" + (f.TirouShell ? "S" : "-") + f.Politicas + (f.TirouMarca ? "M" : "-"))) + "]");
                checar(QuiosqueDoCaixa.Resumir(feitos) == new DesfazerQuiosque.Resumo(5, 0, 0),
                    "DQ-6 o resumo: cinco perfis desfeitos, nada preso");

                checar(Shell(usuarios, Caixa) is null && Politicas(usuarios, Caixa).All(v => v is null) && MarcaDe(usuarios, Caixa) is null
                       && Valor(usuarios, Caixa, DesfazerQuiosque.ChaveWinlogon, "AutoRestartShell") is 1
                       && Valor(usuarios, Caixa, TravaDoCtrlAltDel.ChaveSistema, "DisableRegistryTools") is 1,
                    "DQ-6b no caixa saem o Shell, as quatro políticas e a marca; o resto do Winlogon e uma política que não é da trava ficam");
                checar(Shell(usuarios, ExeSumiu) is null && Politicas(usuarios, ExeSumiu).SequenceEqual(new object?[] { 0, null, null, null }) && MarcaDe(usuarios, ExeSumiu) is null,
                    "DQ-6c Shell de exe que sumiu sai com a marca; o DWORD 0 (não é nosso) fica");
                checar(Shell(usuarios, SemMarca) is null && Politicas(usuarios, SemMarca).All(v => v is 1),
                    "DQ-6c2 Shell nosso SEM a marca: o Shell sai e as quatro políticas em 1 ficam (o PDV não provou que eram dele)");
                checar(Shell(usuarios, SoTrava) == "explorer.exe" && Politicas(usuarios, SoTrava).All(v => v is null) && MarcaDe(usuarios, SoTrava) is null,
                    "DQ-6c3 a trava com a marca sai mesmo sem o Shell nosso (quiosque desligado por fora); o Shell do Explorer fica");
                var intactos = new[] { ComExplorer, CopiaViva, ComMais, Caixa + "_Classes", ".DEFAULT", "S-1-5-18" }
                    .Where(s => Shell(usuarios, s) is null || !Politicas(usuarios, s).All(v => v is 1)).ToList();
                checar(intactos.Count == 0 && MarcaDe(usuarios, CopiaViva) is 1 && MarcaDe(usuarios, ".DEFAULT") is 1,
                    "DQ-6d Shell de outro programa, cópia viva (com marca), formato diferente, SID_Classes, .DEFAULT e SYSTEM: nada mudou"
                    + (intactos.Count == 0 ? "" : " [mudaram: " + string.Join(", ", intactos) + "]"));

                IReadOnlyList<QuiosqueDoCaixa.Desfeito> deNovo;
                using (var raiz = Registry.CurrentUser.OpenSubKey(usuarios)!)
                    deNovo = QuiosqueDoCaixa.DesfazerNosPerfis(raiz, removidos, Existe);
                checar(deNovo.Count == 0, "DQ-6e rodar de novo não acha nada (e não lança)");

                // metade feita: as políticas e a marca já saíram e o Shell ficou. Rodar de novo tira o Shell.
                Perfil(usuarios, Caixa, DesfazerQuiosque.ShellDoPdv(ExeHoje), null, null, null, null, marca: null);
                using (var raiz = Registry.CurrentUser.OpenSubKey(usuarios)!)
                    deNovo = QuiosqueDoCaixa.DesfazerNosPerfis(raiz, removidos, Existe);
                checar(deNovo.Count == 1 && deNovo[0] is { Sid: Caixa, TirouShell: true, Politicas: 0, TirouMarca: false } && Shell(usuarios, Caixa) is null,
                    "DQ-6f com a trava já fora e o Shell ainda lá, rodar de novo tira o Shell");

                // a outra metade: o Shell já saiu e a trava ficou COM a marca. Rodar de novo termina pela marca.
                Perfil(usuarios, Caixa, null, 1, 1, 1, 1, marca: 1);
                using (var raiz = Registry.CurrentUser.OpenSubKey(usuarios)!)
                    deNovo = QuiosqueDoCaixa.DesfazerNosPerfis(raiz, removidos, Existe);
                checar(deNovo.Count == 1 && deNovo[0] is { Sid: Caixa, TirouShell: false, Politicas: 4, TirouMarca: true }
                       && Politicas(usuarios, Caixa).All(v => v is null) && MarcaDe(usuarios, Caixa) is null,
                    "DQ-6f2 com o Shell já fora e a trava com a marca, rodar de novo tira a trava e a marca");

                // um perfil sem Winlogon nenhum (perfil novo) e uma raiz vazia: nada a fazer, sem lançar
                using (Registry.CurrentUser.CreateSubKey(usuarios + @"\S-1-5-21-1-2-3-1999")) { }
                using (Registry.CurrentUser.CreateSubKey(nome + @"\vazia")) { }
                IReadOnlyList<QuiosqueDoCaixa.Desfeito> semNada, vazia;
                using (var raiz = Registry.CurrentUser.OpenSubKey(usuarios)!) semNada = QuiosqueDoCaixa.DesfazerNosPerfis(raiz, removidos, Existe);
                using (var raiz = Registry.CurrentUser.OpenSubKey(nome + @"\vazia")!) vazia = QuiosqueDoCaixa.DesfazerNosPerfis(raiz, removidos, Existe);
                checar(semNada.Count == 0 && vazia.Count == 0, "DQ-6g perfil sem Winlogon e HKEY_USERS vazia: nada a fazer, sem erro");

                // ── DQ-11 o que o Windows não deixa apagar (ACL de negação só na chave isolada) ──
                Perfil(falhas, PoliticaPresa, DesfazerQuiosque.ShellDoPdv(ExeHoje), 1, 1, 1, 1, marca: 1);
                Perfil(falhas, ShellPreso, DesfazerQuiosque.ShellDoPdv(ExeHoje), 1, 1, 1, 1, marca: 1);
                var presaExplorer = falhas + @"\" + PoliticaPresa + @"\" + TravaDoCtrlAltDel.ChaveExplorer;
                var presoWinlogon = falhas + @"\" + ShellPreso + @"\" + DesfazerQuiosque.ChaveWinlogon;
                Negar(presaExplorer, negadas);
                Negar(presoWinlogon, negadas);
                checar(negadas.Count == 2 && Lanca(() => { using var k = Registry.CurrentUser.OpenSubKey(presaExplorer, writable: true); }),
                    "DQ-11a a chave isolada com negação de escrita recusa abrir para gravar (o caso do valor que não sai)");

                IReadOnlyList<QuiosqueDoCaixa.Desfeito> presos;
                using (var raiz = Registry.CurrentUser.OpenSubKey(falhas)!)
                    presos = QuiosqueDoCaixa.DesfazerNosPerfis(raiz, removidos, Existe);
                var porSidPreso = presos.ToDictionary(f => f.Sid);
                checar(porSidPreso.TryGetValue(PoliticaPresa, out var pp) && pp is { TirouShell: true, Politicas: 3, TirouMarca: false, ShellFicou: false, TravaFicou: true }
                       && Shell(falhas, PoliticaPresa) is null
                       && Politicas(falhas, PoliticaPresa).SequenceEqual(new object?[] { null, null, null, 1 }) && MarcaDe(falhas, PoliticaPresa) is 1,
                    "DQ-11b política presa: o Shell sai PRIMEIRO (sem tela preta), as outras três saem, a presa e a marca ficam (a próxima tentativa ainda reconhece)");
                checar(porSidPreso.TryGetValue(ShellPreso, out var sp) && sp is { TirouShell: false, Politicas: 0, TirouMarca: false, ShellFicou: true }
                       && Shell(falhas, ShellPreso) == DesfazerQuiosque.ShellDoPdv(ExeHoje)
                       && Politicas(falhas, ShellPreso).All(v => v is 1) && MarcaDe(falhas, ShellPreso) is 1,
                    "DQ-11c Shell preso: o perfil fica inteiro como estava (quiosque e trava), nada pela metade");
                var resumoPreso = QuiosqueDoCaixa.Resumir(presos);
                checar(resumoPreso == new DesfazerQuiosque.Resumo(1, 1, 1) && DesfazerQuiosque.ImpedeDesinstalar(resumoPreso)
                       && !DesfazerQuiosque.ImpedeDesinstalar(new DesfazerQuiosque.Resumo(1, 0, 1)),
                    "DQ-11d Shell nosso preso SEGURA a desinstalação (o Pdv.exe fica); só a trava presa não segura, vira aviso");

                // liberado, rodar de novo termina os dois
                Liberar(negadas);
                using (var raiz = Registry.CurrentUser.OpenSubKey(falhas)!)
                    presos = QuiosqueDoCaixa.DesfazerNosPerfis(raiz, removidos, Existe);
                checar(QuiosqueDoCaixa.Resumir(presos) == new DesfazerQuiosque.Resumo(2, 0, 0)
                       && new[] { PoliticaPresa, ShellPreso }.All(s => Shell(falhas, s) is null && Politicas(falhas, s).All(v => v is null) && MarcaDe(falhas, s) is null),
                    "DQ-11e sem a negação, rodar de novo tira o que ficou nos dois (pela marca e pelo Shell)");
            }
        }
        finally
        {
            Liberar(negadas);
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
        checar(!sobrou, "DQ-7 a chave de teste foi apagada no fim (inclusive as chaves com negação)");
        checar(Verdade() == antes, "DQ-7b o Shell, as políticas e a marca de verdade deste PC não mudaram");

        // ── DQ-8 o fonte ──
        var inst = Fonte("Pdv.Instalador", "Instalacao.cs") ?? "";
        var qdc = Fonte("Pdv.Instalador", "QuiosqueDoCaixa.cs") ?? "";
        var appInst = Fonte("Pdv.Instalador", "App.xaml.cs") ?? "";
        var csprojInst = Fonte("Pdv.Instalador", "Pdv.Instalador.csproj") ?? "";
        var desinstalar = Trecho(inst, "public static string? Desinstalar(out DesfazerQuiosque.Resumo quiosque)", "public static IReadOnlyList<string> ExesDoCaixa()");
        var iDesfaz = desinstalar.IndexOf("quiosque = DesfazerQuiosqueDoCaixa();", StringComparison.Ordinal);
        var iSegura = desinstalar.IndexOf("if (DesfazerQuiosque.ImpedeDesinstalar(quiosque)) return DesfazerQuiosque.MotivoShellFicou;", StringComparison.Ordinal);
        var iChave = desinstalar.IndexOf("DeleteSubKeyTree(ChaveUninstall,", StringComparison.Ordinal);
        var iPasta = desinstalar.IndexOf("Directory.Delete(pasta", StringComparison.Ordinal);
        var iTry = desinstalar.IndexOf("try", StringComparison.Ordinal);
        checar(iDesfaz > 0 && iSegura > iDesfaz && iChave > iSegura && iPasta > iSegura && iTry > iSegura
               && inst.Contains("public static string? Desinstalar() => Desinstalar(out _);", StringComparison.Ordinal),
            "DQ-8 o Desinstalar desfaz o quiosque PRIMEIRO e, com o Shell nosso preso, para antes de apagar qualquer coisa (chave, atalho, Pdv.exe)");
        var desfazer = Trecho(inst, "public static DesfazerQuiosque.Resumo DesfazerQuiosqueDoCaixa()", "\n    }");
        checar(desfazer.Contains("var exes = ExesDoCaixa();", StringComparison.Ordinal)
               && desfazer.Contains("QuiosqueDoCaixa.DesfazerNosPerfis(Registry.Users, exes, File.Exists)", StringComparison.Ordinal)
               && desfazer.Contains("return QuiosqueDoCaixa.Resumir(feitos);", StringComparison.Ordinal)
               && desfazer.Contains("catch { return DesfazerQuiosque.Resumo.Nenhum; }", StringComparison.Ordinal),
            "DQ-8b na loja a raiz é HKEY_USERS, os exes são os que o desinstalador remove, e um erro ao percorrer não segura a desinstalação");
        checar(qdc.Length > 0 && !qdc.Contains("Registry.LocalMachine", StringComparison.Ordinal) && !qdc.Contains("Registry.CurrentUser", StringComparison.Ordinal)
               && !qdc.Contains("Registry.Users", StringComparison.Ordinal) && !qdc.Contains("Process.Start", StringComparison.Ordinal),
            "DQ-8c o registro do desinstalador recebe a raiz de fora (nunca HKLM nem o HKCU de quem desinstala) e não roda processo");
        checar(appInst.Contains("Instalacao.Desinstalar(out var quiosque)", StringComparison.Ordinal)
               && appInst.Contains("DesfazerQuiosque.Aviso(quiosque)", StringComparison.Ordinal)
               && csprojInst.Contains(@"Include=""..\Pdv.Nucleo\TravaDoCtrlAltDel.cs""", StringComparison.Ordinal)
               && csprojInst.Contains(@"Include=""..\Pdv.Nucleo\DesfazerQuiosque.cs""", StringComparison.Ordinal),
            "DQ-8d o --desinstalar usa o Desinstalar que desfaz o quiosque e avisa; o instalador compila a MESMA regra do caixa");
        var noPerfil = Trecho(qdc, "public static Desfeito? DesfazerNoPerfil(", "/// <summary>O Shell do perfil como está gravado");
        var iShell = noPerfil.IndexOf("w.DeleteValue(DesfazerQuiosque.ValorShell", StringComparison.Ordinal);
        var iPreso = noPerfil.IndexOf("if (!tirouShell) return new Desfeito(", StringComparison.Ordinal);
        var iPoliticas = noPerfil.IndexOf("foreach (var (chave, valor) in plano.Politicas)", StringComparison.Ordinal);
        var iMarca = noPerfil.IndexOf("DeleteValue(TravaDoCtrlAltDel.Marca.Valor", StringComparison.Ordinal);
        checar(iShell > 0 && iPreso > iShell && iPoliticas > iPreso && iMarca > iPoliticas
               && noPerfil.Contains("if (plano.TirarMarca && !travaFicou)", StringComparison.Ordinal),
            "DQ-8e no perfil: o Shell sai primeiro (preso, o perfil fica inteiro), depois cada política, e a marca por último e só com todas fora");

        // ── DQ-9 os exes do caixa ──
        var exes = Instalacao.ExesDoCaixa();
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        checar(exes.Contains(Path.Combine(Instalacao.PastaDestinoPadrao, "Pdv.exe"), StringComparer.OrdinalIgnoreCase)
               && Instalacao.NomesAntigos.All(n => exes.Contains(Path.Combine(pf, n, "Pdv.exe"), StringComparer.OrdinalIgnoreCase))
               && exes.All(e => Path.GetFileName(e) == "Pdv.exe" && Path.IsPathFullyQualified(e))
               && exes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == exes.Count,
            "DQ-9 os exes do caixa são o Pdv.exe da pasta de hoje e das pastas dos nomes antigos (caminho absoluto, sem repetir)");

        // ── DQ-10 os textos ──
        var aviso = DesfazerQuiosque.Aviso(new DesfazerQuiosque.Resumo(1, 0, 0));
        var avisoTrava = DesfazerQuiosque.Aviso(new DesfazerQuiosque.Resumo(1, 0, 1));
        var textos = new[] { aviso, avisoTrava, DesfazerQuiosque.MotivoShellFicou };
        checar(DesfazerQuiosque.Aviso(DesfazerQuiosque.Resumo.Nenhum) == "" && aviso.Length is > 0 and <= 110
               && avisoTrava.Contains("A trava do Ctrl+Alt+Del não saiu", StringComparison.Ordinal) && avisoTrava.Length <= 200
               && DesfazerQuiosque.MotivoShellFicou.Length <= 130
               && DesfazerQuiosque.Aviso(new DesfazerQuiosque.Resumo(1, 1, 0)) == ""
               && textos.All(t => !t.Contains('\u2014') && !t.Contains('\u2013')
                                  && !System.Text.RegularExpressions.Regex.IsMatch(t, "(?i)shell|registro|hkey|polic|sid|winlogon")),
            "DQ-10 os avisos do desinstalador são curtos, sem travessão e sem termo técnico: '" + aviso.Trim() + "' / '" + DesfazerQuiosque.MotivoShellFicou + "'");
    }

    /// <summary>Monta um perfil de mentira: Shell (null = sem), as quatro políticas e a marca (null = sem o valor).</summary>
    private static void Perfil(string usuarios, string sid, string? shell, object? taskMgr, object? lockWs, object? senha, object? logoff, object? marca)
    {
        var raiz = usuarios + @"\" + sid;
        using (var w = Registry.CurrentUser.CreateSubKey(raiz + @"\" + DesfazerQuiosque.ChaveWinlogon))
        {
            if (shell is null) w.DeleteValue(DesfazerQuiosque.ValorShell, throwOnMissingValue: false);
            else w.SetValue(DesfazerQuiosque.ValorShell, shell, RegistryValueKind.String);
        }
        var valores = new[] { taskMgr, lockWs, senha, logoff, marca };
        var alvos = TravaDoCtrlAltDel.Travas.Append(TravaDoCtrlAltDel.Marca).ToArray();
        for (var i = 0; i < alvos.Length; i++)
        {
            var (chave, valor) = alvos[i];
            using var k = Registry.CurrentUser.CreateSubKey(raiz + @"\" + chave);
            switch (valores[i])
            {
                case null: k.DeleteValue(valor, throwOnMissingValue: false); break;
                case int n: k.SetValue(valor, n, RegistryValueKind.DWord); break;
                case string s: k.SetValue(valor, s, RegistryValueKind.String); break;
            }
        }
    }

    /// <summary>Nega ao usuário atual gravar valores na chave de TESTE (o dono continua podendo mudar a ACL e apagar).</summary>
    private static void Negar(string caminho, List<string> negadas)
    {
        using var k = Registry.CurrentUser.OpenSubKey(caminho, RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ReadPermissions | RegistryRights.ChangePermissions);
        if (k is null) return;
        var acl = k.GetAccessControl();
        acl.AddAccessRule(RegraDeNegacao());
        k.SetAccessControl(acl);
        negadas.Add(caminho);
    }

    /// <summary>Tira as negações postas pela bateria (para rodar de novo e para apagar a chave de teste no fim).</summary>
    private static void Liberar(List<string> negadas)
    {
        foreach (var caminho in negadas.ToList())
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(caminho, RegistryKeyPermissionCheck.ReadWriteSubTree,
                    RegistryRights.ReadPermissions | RegistryRights.ChangePermissions);
                if (k is not null)
                {
                    var acl = k.GetAccessControl();
                    acl.RemoveAccessRule(RegraDeNegacao());
                    k.SetAccessControl(acl);
                }
                negadas.Remove(caminho);
            }
            catch { /* o finally tenta de novo antes de apagar a chave de teste */ }
        }
    }

    private static RegistryAccessRule RegraDeNegacao()
    {
        using var eu = WindowsIdentity.GetCurrent();
        return new RegistryAccessRule(eu.User!, RegistryRights.SetValue, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
    }

    private static bool Lanca(Action a)
    {
        try { a(); return false; }
        catch { return true; }
    }

    private static string? Shell(string usuarios, string sid)
        => Valor(usuarios, sid, DesfazerQuiosque.ChaveWinlogon, DesfazerQuiosque.ValorShell) as string;

    private static object?[] Politicas(string usuarios, string sid)
        => TravaDoCtrlAltDel.Travas.Select(t => Valor(usuarios, sid, t.Chave, t.Valor)).ToArray();

    private static object? MarcaDe(string usuarios, string sid)
        => Valor(usuarios, sid, TravaDoCtrlAltDel.Marca.Chave, TravaDoCtrlAltDel.Marca.Valor);

    private static object? Valor(string usuarios, string sid, string chave, string valor)
    {
        using var k = Registry.CurrentUser.OpenSubKey(usuarios + @"\" + sid + @"\" + chave);
        return k?.GetValue(valor);
    }

    /// <summary>O Shell, as quatro políticas e a marca DE VERDADE deste usuário (só leitura), para comparar antes e depois.</summary>
    private static string Verdade()
    {
        string? shell;
        try
        {
            using var w = Registry.CurrentUser.OpenSubKey(DesfazerQuiosque.ChaveWinlogon);
            shell = w?.GetValue(DesfazerQuiosque.ValorShell) as string;
        }
        catch { shell = "(não li)"; }
        var pol = TravaDoCtrlAltDel.Travas.Append(TravaDoCtrlAltDel.Marca).Select(t =>
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
