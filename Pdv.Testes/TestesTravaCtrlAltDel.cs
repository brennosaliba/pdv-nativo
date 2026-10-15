using System.ComponentModel;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A TRAVA DO CTRL+ALT+DEL COM O ADMINISTRADOR DO WINDOWS (1.0.36, 14/10/2026, Savassi, caixa 1.0.34).
/// O dono marcou a trava e viu "Access to the registry key ... Policies ... is denied": a chave
/// Policies do usuário só aceita escrita de administrador elevado. Agora o PDV pede ao Windows e
/// um ajudante (o próprio Pdv.exe --travas on|off --sid SID) grava em HKEY_USERS\SID.
///
///  TC-1..3   o argumento do ajudante: válido, inválido, o caminho em HKEY_USERS
///  TC-4      SID de outra pessoa recusado (só o de quem pediu)
///  TC-5..10  o ajudante e o gravador numa chave ISOLADA (HKCU\Software\PdvNativoTeste\guid),
///            criada e apagada aqui; as políticas de verdade deste PC não são tocadas
///  TC-11..19 a ordem: direto, aviso do Windows, o Não (1223) vira frase curta, o registro relido manda
///  TC-20..   o fonte: a abertura nunca chama o aviso, o ajudante sai antes de tudo, a tela sem inglês
/// </summary>
public static class TestesTravaCtrlAltDel
{
    private const string Eu = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string Outra = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private const string Admin = "S-1-5-21-1111111111-2222222222-3333333333-500";
    private const string Entra = "S-1-12-1-1234567890-1234567890-1234567890-1234567890";

    public static void Rodar(Action<bool, string> checar)
    {
        // ── TC-1 argumento válido: on, off, conta Entra, e a ida e volta do que o PDV monta ──
        var on = LinhaDeComando.LerTravas(new[] { "--travas", "on", "--sid", Eu });
        var off = LinhaDeComando.LerTravas(new[] { "--travas", "off", "--sid", Eu });
        var entra = LinhaDeComando.LerTravas(new[] { "--travas", "on", "--sid", Entra });
        checar(on is { Travar: true, Sid: Eu } && off is { Travar: false, Sid: Eu } && entra is { Travar: true, Sid: Entra },
            "TC-1 --travas on|off --sid SID é aceito (conta local, de domínio ou Entra)");
        var montado = TravaDoCtrlAltDel.MontarArgumentos(false, Eu).Split(' ');
        checar(string.Join(" ", montado) == "--travas off --sid " + Eu
               && LinhaDeComando.LerTravas(montado) is { Travar: false, Sid: Eu }
               && LinhaDeComando.Modo(montado) == ModoDoExe.Travas
               && !LinhaDeComando.PegaATrava(ModoDoExe.Travas) && !LinhaDeComando.AbreOCaixa(ModoDoExe.Travas),
            "TC-1b o que o PDV monta é o que o ajudante lê; o modo não pega a trava de instância nem abre o caixa");

        // ── TC-2 argumento inválido: nada passa ──
        string[][] ruins =
        {
            new[] { "--travas", "on" },                                         // sem SID
            new[] { "--travas", "on", "--sid" },
            new[] { "--travas", "on", "--sid", "" },
            new[] { "--travas", "on", "--sid", "S-1-5-18" },                    // SYSTEM
            new[] { "--travas", "on", "--sid", "S-1-5-32-544" },                // grupo Administradores
            new[] { "--travas", "on", "--sid", ".DEFAULT" },
            new[] { "--travas", "on", "--sid", Eu + "_Classes" },
            new[] { "--travas", "on", "--sid", Eu + @"\..\S-1-5-18" },          // caminho escondido no SID
            new[] { "--travas", "on", "--sid", @"..\" + Eu },
            new[] { "--travas", "on", "--sid", Eu + @"\Software" },
            new[] { "--travas", "on", "--sid", Eu + "\n" },
            new[] { "--travas", "on", "--sid", " " + Eu },
            new[] { "--travas", "on", "--sid", "s" + Eu[1..] },                 // minúsculo
            new[] { "--travas", "on", "--sid", "S-1-5-21-4294967296-1-2-1001" }, // passa de 32 bits
            new[] { "--travas", "on", "--sid", "S-1-5-21-01-2-3-1001" },         // zero à esquerda
            new[] { "--travas", "on", "--sid", "S-1-5-21-1-2-1001" },            // número a menos
            new[] { "--travas", "on", "--sid", "S-1-5-21-1-2-3-4-1001" },        // número a mais
            new[] { "--travas", "on", "--sid", "S-1-5-21-\u0661-2-3-1001" },     // dígito árabe
            new[] { "--travas", "on", "--sid", Eu, "--mais" },                   // argumento a mais
            new[] { "--travas", "ON", "--sid", Eu },
            new[] { "--travas", "1", "--sid", Eu },
            new[] { "--travas", "on", "--SID", Eu },
            new[] { "--sid", Eu, "--travas", "on" },                             // ordem trocada
            new[] { "--quiosque", "on", "--sid", Eu },
        };
        var passaram = ruins.Where(x => LinhaDeComando.LerTravas(x) is not null).Select(x => string.Join(" ", x)).ToList();
        checar(passaram.Count == 0 && LinhaDeComando.LerTravas(null) is null && LinhaDeComando.LerTravas(Array.Empty<string>()) is null,
            "TC-2 recusa sem SID, SID de sistema ou de grupo, caminho no SID, maiúscula, número fora do formato e argumento a mais"
            + (passaram.Count == 0 ? "" : " [passaram: " + string.Join(" | ", passaram) + "]"));
        checar(Lanca(() => TravaDoCtrlAltDel.MontarArgumentos(true, "S-1-5-18"))
               && Lanca(() => TravaDoCtrlAltDel.MontarArgumentos(true, Eu + " --mais")),
            "TC-2b SID fora do formato nunca vira argumento do administrador");

        // ── TC-3 o caminho em HKEY_USERS e só as quatro políticas ──
        checar(TravaDoCtrlAltDel.CaminhoNoHku(Eu, TravaDoCtrlAltDel.ChaveSistema)
                   == @"HKEY_USERS\" + Eu + @"\Software\Microsoft\Windows\CurrentVersion\Policies\System"
               && TravaDoCtrlAltDel.CaminhoNoHku(Eu, TravaDoCtrlAltDel.ChaveExplorer)
                   == @"HKEY_USERS\" + Eu + @"\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"
               && Lanca(() => TravaDoCtrlAltDel.CaminhoNoHku(@"S-1-5-18", TravaDoCtrlAltDel.ChaveSistema)),
            "TC-3 o caminho é HKEY_USERS\\SID\\...\\Policies\\System e \\Explorer, e só para SID de pessoa");
        checar(TravaDoCtrlAltDel.Travas.Select(t => t.Valor).SequenceEqual(new[] { "DisableTaskMgr", "DisableLockWorkstation", "DisableChangePassword", "NoLogoff" })
               && TravaDoCtrlAltDel.Travas.All(t => t.Chave == TravaDoCtrlAltDel.ChaveSistema || t.Chave == TravaDoCtrlAltDel.ChaveExplorer)
               && ReferenceEquals(Quiosque.Travas, TravaDoCtrlAltDel.Travas),
            "TC-3b o ajudante só conhece as quatro políticas (e a lista é uma só)");

        // ── TC-4 SID de outra pessoa ──
        checar(TravaDoCtrlAltDel.SidAceito(Eu, Eu, null),
            "TC-4 administrador que elevou a si mesmo: o SID do argumento é o da conta do ajudante");
        checar(TravaDoCtrlAltDel.SidAceito(Eu, Admin, Eu),
            "TC-4b usuário comum com a senha do administrador: o SID é o de quem está logado na sessão");
        checar(!TravaDoCtrlAltDel.SidAceito(Outra, Admin, Eu) && !TravaDoCtrlAltDel.SidAceito(Outra, Eu, Eu)
               && !TravaDoCtrlAltDel.SidAceito(Outra, null, null) && !TravaDoCtrlAltDel.SidAceito(null, Eu, Eu)
               && !TravaDoCtrlAltDel.SidAceito("S-1-5-18", "S-1-5-18", "S-1-5-18"),
            "TC-4c SID de outra pessoa é recusado, mesmo com administrador (e SYSTEM nunca)");

        // ── TC-5..10 o ajudante e o gravador numa chave isolada ──
        // Nunca Registry.CurrentUser direto: tudo embaixo de HKCU\Software\PdvNativoTeste\<guid>,
        // criada aqui e apagada no finally. As políticas de verdade deste PC são lidas antes e depois.
        var antes = (Quiosque.Travado, Quiosque.AlgumaTrava);
        const string Pai = @"Software\PdvNativoTeste";
        var nome = Pai + @"\" + Guid.NewGuid().ToString("N");
        try
        {
            using (Registry.CurrentUser.CreateSubKey(nome)) { }
            RegistryKey? Abrir(string _) => Registry.CurrentUser.OpenSubKey(nome, writable: true);
            bool isolada;
            using (var r = Abrir(""))
                isolada = r is not null && r.Name.StartsWith(@"HKEY_CURRENT_USER\Software\PdvNativoTeste\", StringComparison.OrdinalIgnoreCase);
            checar(isolada, "TC-5 a chave de teste existe e é isolada (HKCU\\Software\\PdvNativoTeste\\guid)");
            if (isolada)
            {
                // TC-6 ligar pelo ajudante: as quatro como DWORD 1
                var s1 = Quiosque.RodarAjudante(new[] { "--travas", "on", "--sid", Eu }, Abrir, Eu, null);
                using (var r = Abrir("")!)
                    checar(s1 == TravaDoCtrlAltDel.SaidaOk && Quiosque.LerTravas(r) == (true, true) && QuatroDwords(r),
                        $"TC-6 o ajudante liga: as quatro políticas como DWORD 1 (saída {s1})");

                // TC-7 outra pessoa, argumento ruim e usuário fora: saem sem mexer em nada
                var s2 = Quiosque.RodarAjudante(new[] { "--travas", "off", "--sid", Outra }, Abrir, Eu, Eu);
                var s3 = Quiosque.RodarAjudante(new[] { "--travas", "off", "--sid", "S-1-5-18" }, Abrir, "S-1-5-18", null);
                var s4 = Quiosque.RodarAjudante(new[] { "--travas", "off", "--sid", Eu }, _ => null, Eu, null);
                using (var r = Abrir("")!)
                    checar(s2 == TravaDoCtrlAltDel.SaidaOutraPessoa && s3 == TravaDoCtrlAltDel.SaidaArgumento
                           && s4 == TravaDoCtrlAltDel.SaidaUsuarioFora && Quiosque.LerTravas(r) == (true, true),
                        $"TC-7 SID de outra pessoa ({s2}), argumento ruim ({s3}) e usuário fora ({s4}) não mexem em nada");

                // TC-9 desligar pelo ajudante: nenhuma sobra; desligar de novo não lança
                var s6 = Quiosque.RodarAjudante(new[] { "--travas", "off", "--sid", Eu }, Abrir, Admin, Eu);
                var s7 = Quiosque.RodarAjudante(new[] { "--travas", "off", "--sid", Eu }, Abrir, Admin, Eu);
                using (var r = Abrir("")!)
                    checar(s6 == TravaDoCtrlAltDel.SaidaOk && s7 == TravaDoCtrlAltDel.SaidaOk && Quiosque.LerTravas(r) == (false, false),
                        $"TC-9 o ajudante desliga as quatro (pedido do usuário da sessão), e desligar de novo é inofensivo ({s6}/{s7})");

                // TC-8 raiz aberta só para leitura (o ajudante sem administrador): ligar é recusado com
                // acesso negado, sai com o código 5 e nada muda (o CreateSubKey do .NET exige raiz gravável)
                var s5 = Quiosque.RodarAjudante(new[] { "--travas", "on", "--sid", Eu }, _ => Registry.CurrentUser.OpenSubKey(nome), Eu, null);
                using (var r = Abrir("")!)
                    checar(s5 == TravaDoCtrlAltDel.SaidaSemAdministrador && Quiosque.LerTravas(r) == (false, false),
                        $"TC-8 sem permissão de escrita o ajudante sai com 'sem administrador' e não muda nada ({s5})");

                // TC-10 o gravador direto: desligar não cria chave; a trava pela metade é vista e desfeita
                var limpo = nome + @"\limpo";
                using (Registry.CurrentUser.CreateSubKey(limpo)) { }
                using (var r = Registry.CurrentUser.OpenSubKey(limpo, writable: true)!)
                {
                    Quiosque.GravarTravas(r, false);
                    var semChave = r.SubKeyCount == 0;
                    using (var k = r.CreateSubKey(TravaDoCtrlAltDel.ChaveSistema)) k.SetValue("DisableTaskMgr", 1, RegistryValueKind.DWord);
                    var metade = Quiosque.LerTravas(r);
                    Quiosque.GravarTravas(r, false);
                    var depois = Quiosque.LerTravas(r);
                    Quiosque.GravarTravas(r, true);
                    var ligada = Quiosque.LerTravas(r);
                    checar(semChave && metade == (false, true) && depois == (false, false) && ligada == (true, true) && QuatroDwords(r),
                        "TC-10 desligar sem nada gravado não cria chave; a trava pela metade aparece como 'alguma' e desligar tira; ligar grava as quatro");
                }
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
        checar(!sobrou, "TC-10b a chave de teste foi apagada no fim");
        checar((Quiosque.Travado, Quiosque.AlgumaTrava) == antes, "TC-10c as políticas de verdade deste PC não mudaram");

        // ── TC-11..19 a ordem das tentativas (sem registro nem processo) ──
        var negado = new UnauthorizedAccessException(@"Access to the registry key 'HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\System' is denied.");
        var pedidos = 0;
        int? WindowsGrava() { pedidos++; return TravaDoCtrlAltDel.SaidaOk; }
        int? WindowsNao() { pedidos++; throw new Win32Exception(TravaDoCtrlAltDel.ErroCancelado); }

        var rDireto = TravaDoCtrlAltDel.Mudar(() => null, () => true, null, WindowsGrava);
        checar(rDireto.Resultado == ResultadoDaTrava.Ok && pedidos == 0,
            "TC-11 com permissão (UAC desligado ou PDV elevado) grava direto e não chama o aviso do Windows");

        pedidos = 0;
        var rNegado = TravaDoCtrlAltDel.Mudar(() => negado, () => pedidos > 0, null, WindowsGrava);
        checar(rNegado.Resultado == ResultadoDaTrava.Ok && pedidos == 1 && rNegado.Detalhe.Contains("is denied", StringComparison.Ordinal),
            "TC-12 acesso negado: pede ao Windows uma vez, e o texto técnico fica no detalhe (auditoria)");

        pedidos = 0;
        var rNao = TravaDoCtrlAltDel.Mudar(() => negado, () => false, null, WindowsNao);
        var fraseNao = TravaDoCtrlAltDel.Frase(rNao.Resultado, true);
        checar(rNao.Resultado == ResultadoDaTrava.Cancelou && pedidos == 1
               && fraseNao == "Sem a permissão do Windows, a trava do Ctrl+Alt+Del não foi ligada. O resto foi salvo.",
            $"TC-13 Não no aviso (Win32Exception 1223) vira Cancelou e uma frase curta: '{fraseNao}'");
        checar(TravaDoCtrlAltDel.Frase(ResultadoDaTrava.Cancelou, false) == "Sem a permissão do Windows, a trava do Ctrl+Alt+Del continua ligada. O resto foi salvo.",
            "TC-13b Não ao desligar: a frase diz que a trava continua");

        pedidos = 0;
        var rSeg = TravaDoCtrlAltDel.Mudar(() => new SecurityException("Requested registry access is not allowed."), () => true, null, WindowsGrava);
        checar(rSeg.Resultado == ResultadoDaTrava.Ok && pedidos == 1, "TC-14 SecurityException (o outro jeito do acesso negado) também pede ao Windows");

        pedidos = 0;
        var rIo = TravaDoCtrlAltDel.Mudar(() => new IOException("disco"), () => false, null, WindowsGrava);
        checar(rIo.Resultado == ResultadoDaTrava.Falhou && pedidos == 0,
            "TC-15 erro que não é acesso negado não abre o aviso do Windows à toa (Falhou)");

        pedidos = 0;
        var rFora = TravaDoCtrlAltDel.Mudar(() => negado, () => false, "o PDV não está na pasta de programas", WindowsGrava);
        checar(rFora.Resultado == ResultadoDaTrava.Falhou && pedidos == 0 && rFora.Detalhe.Contains("pasta de programas", StringComparison.Ordinal),
            "TC-16 exe fora da pasta de programas: não pede administrador, e o motivo vai para a auditoria");

        pedidos = 0;
        var rRelido = TravaDoCtrlAltDel.Mudar(() => negado, () => false, null, WindowsGrava);
        var rCodigo = TravaDoCtrlAltDel.Mudar(() => negado, () => true, null, () => TravaDoCtrlAltDel.SaidaSemAdministrador);
        checar(rRelido.Resultado == ResultadoDaTrava.Falhou && rCodigo.Resultado == ResultadoDaTrava.Ok && rCodigo.Detalhe.Contains("rodou sem administrador", StringComparison.Ordinal),
            "TC-17 o resultado é o registro relido, não o código de saída (que só vai para o detalhe)");

        var rOutro = TravaDoCtrlAltDel.Mudar(() => negado, () => false, null, () => throw new Win32Exception(2));
        var rLento = TravaDoCtrlAltDel.Mudar(() => negado, () => false, null, () => null);
        var rLanca = TravaDoCtrlAltDel.Mudar(() => throw negado, () => true, null, () => TravaDoCtrlAltDel.SaidaOk);
        pedidos = 0;
        var rReler = TravaDoCtrlAltDel.Mudar(() => null, () => throw new IOException("leitura"), null, WindowsGrava);
        checar(rReler.Resultado == ResultadoDaTrava.Falhou && pedidos == 0,
            "TC-18b reler o registro que lança não derruba o Salvar (Falhou, sem aviso do Windows)");
        checar(rOutro.Resultado == ResultadoDaTrava.Falhou && rLento.Resultado == ResultadoDaTrava.Falhou
               && rLento.Detalhe.Contains("não terminou em 20 s", StringComparison.Ordinal) && rLanca.Resultado == ResultadoDaTrava.Ok,
            "TC-18 outro erro do Windows não é Cancelou; ajudante que não termina é Falhou; tentativa direta que lança é tratada como recusa");

        checar(TravaDoCtrlAltDel.Cancelado(new Win32Exception(1223)) && !TravaDoCtrlAltDel.Cancelado(new Win32Exception(5))
               && !TravaDoCtrlAltDel.Cancelado(negado) && !TravaDoCtrlAltDel.Cancelado(null)
               && TravaDoCtrlAltDel.AcessoNegado(negado) && TravaDoCtrlAltDel.AcessoNegado(new SecurityException())
               && !TravaDoCtrlAltDel.AcessoNegado(new IOException()) && !TravaDoCtrlAltDel.AcessoNegado(null),
            "TC-19 só o 1223 é o Não; só acesso negado pede administrador");

        // ── TC-20 as frases da tela: uma linha, sem travessão, sem nada do Windows em inglês ──
        var frases = new[] { ResultadoDaTrava.Ok, ResultadoDaTrava.Cancelou, ResultadoDaTrava.Falhou }
            .SelectMany(r => new[] { TravaDoCtrlAltDel.Frase(r, true), TravaDoCtrlAltDel.Frase(r, false) }).ToList();
        var feias = frases.Where(t => t.Length > 100 || t.Contains('\u2014') || t.Contains('\u2013') || t.Contains('\n')
            || Regex.IsMatch(t, "(?i)access|denied|registry|exception|hkey|polic|runas|uac|1223|sid")).ToList();
        checar(feias.Count == 0 && TravaDoCtrlAltDel.Frase(ResultadoDaTrava.Falhou, true) == "Não consegui mudar a trava do Ctrl+Alt+Del. O resto foi salvo.",
            "TC-20 frases curtas, de uma linha, sem travessão e sem texto do Windows" + (feias.Count == 0 ? "" : " [" + string.Join(" | ", feias) + "]"));

        // ── TC-21 só eleva o exe que mora numa pasta que só o administrador troca ──
        var pf = new[] { @"C:\Program Files", @"C:\Program Files (x86)" };
        checar(TravaDoCtrlAltDel.PodeElevar(@"C:\Program Files\MMFood\Pdv.exe", pf)
               && TravaDoCtrlAltDel.PodeElevar(@"C:\Program Files (x86)\MMFood\Pdv.exe", pf)
               && !TravaDoCtrlAltDel.PodeElevar(@"C:\Users\caixa\Downloads\Pdv.exe", pf)
               && !TravaDoCtrlAltDel.PodeElevar(@"C:\Program Files\..\Users\caixa\Pdv.exe", pf)
               && !TravaDoCtrlAltDel.PodeElevar(@"C:\Program FilesX\Pdv.exe", pf)
               && !TravaDoCtrlAltDel.PodeElevar("Pdv.exe", pf)
               && !TravaDoCtrlAltDel.PodeElevar(@"C:\Program Files\MMFood\Pdv.dll", pf)
               && !TravaDoCtrlAltDel.PodeElevar(null, pf)
               && !TravaDoCtrlAltDel.PodeElevar(@"C:\Program Files\MMFood\Pdv.exe", new string?[] { null, "", @"C:\" }),
            "TC-21 eleva só Pdv.exe dentro de Arquivos de Programas (nunca Downloads, '..', pasta parecida, caminho relativo ou a raiz do disco)");

        // ── TC-22 quando a Configuração mexe ──
        checar(TravaDoCtrlAltDel.PrecisaMudar(true, false, false) && TravaDoCtrlAltDel.PrecisaMudar(true, false, true)
               && !TravaDoCtrlAltDel.PrecisaMudar(true, true, true) && TravaDoCtrlAltDel.PrecisaMudar(false, true, true)
               && TravaDoCtrlAltDel.PrecisaMudar(false, false, true) && !TravaDoCtrlAltDel.PrecisaMudar(false, false, false),
            "TC-22 só mexe se mudou; desmarcado tira até a trava pela metade; nada gravado e nada pedido não chama o Windows");

        // ── TC-23.. o fonte ──
        var app = Fonte("App.xaml.cs") ?? "";
        var q = Fonte("Quiosque.cs") ?? "";
        var cfg = Fonte("Telas", "Configuracao.xaml.cs") ?? "";
        var cfgXaml = Fonte("Telas", "Configuracao.xaml") ?? "";
        var nucleo = Fonte("Pdv.Nucleo", "TravaDoCtrlAltDel.cs") ?? "";

        var iAjudante = app.IndexOf("ModoDoExe.Travas", StringComparison.Ordinal);
        checar(iAjudante > 0
               && iAjudante < app.IndexOf("AppDomain.CurrentDomain.UnhandledException +=", StringComparison.Ordinal)
               && iAjudante < app.IndexOf("InstanciaUnica.Tentar()", StringComparison.Ordinal)
               && iAjudante < app.IndexOf("Banco.Migrar()", StringComparison.Ordinal)
               && iAjudante < app.IndexOf("Agente.IniciarVigia()", StringComparison.Ordinal)
               && iAjudante < app.IndexOf("ResolverPendenciasTefAsync", StringComparison.Ordinal)
               && app.Contains("Environment.Exit(Quiosque.RodarAjudante(e.Args)); return;", StringComparison.Ordinal),
            "TC-23 o ajudante sai antes das guardas de tela, da trava de instância, do banco, do agente e do TEF");
        checar(app.IndexOf("var args = e.Args;", StringComparison.Ordinal) > app.IndexOf("DispatcherUnhandledException +=", StringComparison.Ordinal),
            "TC-23b o 'var args = e.Args;' continua depois das guardas (TW-11 corta o texto entre os dois)");
        checar(!app.Contains("runas", StringComparison.OrdinalIgnoreCase) && !app.Contains("MudarTrava(", StringComparison.Ordinal)
               && !app.Contains("PedirAoWindows", StringComparison.Ordinal) && !app.Contains("Quiosque.Travar(", StringComparison.Ordinal)
               && app.Contains("Quiosque.TentarDireto(false)", StringComparison.Ordinal) && app.Contains("\"ctrl_alt_del_ficou\"", StringComparison.Ordinal),
            "TC-24 a abertura do PDV nunca chama o aviso do Windows: só tenta direto e, sem permissão, deixa rastro");
        checar(app.Contains("else if (modo == ModoDoExe.ImprimirTeste)", StringComparison.Ordinal),
            "TC-25 só o --imprimir-teste imprime (modo novo nunca cai no papel)");

        var direto = Corpo(q, "public static Exception? TentarDireto(");
        checar(direto.Contains("Registry.CurrentUser", StringComparison.Ordinal) && !direto.Contains("runas", StringComparison.Ordinal)
               && !direto.Contains("Process.Start", StringComparison.Ordinal) && !direto.Contains("PedirAoWindows", StringComparison.Ordinal),
            "TC-26 TentarDireto grava no HKCU e nunca abre o aviso do Windows");
        checar(q.Contains("Registry.Users.OpenSubKey(sid, writable: true)", StringComparison.Ordinal)
               && Corpo(q, "public static int RodarAjudante(IReadOnlyList<string> args, Func<string, RegistryKey?> abrirRaiz,").Contains("TravaDoCtrlAltDel.SidAceito(", StringComparison.Ordinal)
               && !Corpo(q, "public static int RodarAjudante(IReadOnlyList<string> args, Func<string, RegistryKey?> abrirRaiz,").Contains("Registry.CurrentUser", StringComparison.Ordinal),
            "TC-27 o ajudante grava em HKEY_USERS\\SID (aberto para escrita), confere o SID e não usa o HKCU dele");
        checar(q.Contains("Verb = \"runas\"", StringComparison.Ordinal) && q.Contains("ErrorDialogParentHandle = janela", StringComparison.Ordinal)
               && q.Contains("WaitForExit(20000)", StringComparison.Ordinal) && q.Contains("TravaDoCtrlAltDel.PodeElevar(exe, PastasDeProgramas())", StringComparison.Ordinal)
               && !q.Contains("Registry.LocalMachine", StringComparison.Ordinal),
            "TC-28 o pedido ao Windows usa a janela do PDV como dona, espera no máximo 20 s, só para exe em Arquivos de Programas, e nunca toca na máquina");

        var trava = Trecho(cfg, "var travarQuer =", "ChkTravarAtalhos.IsChecked = Quiosque.Travado;");
        var avisoTrava = Trecho(trava, "Dialogo.Avisar(", ";");
        checar(trava.Contains("Quiosque.MudarTrava(travarQuer,", StringComparison.Ordinal)
               && trava.Contains("WindowInteropHelper(Window.GetWindow(this)!).Handle", StringComparison.Ordinal)
               && avisoTrava.Contains("TravaDoCtrlAltDel.Frase(resultadoTrava, travarQuer)", StringComparison.Ordinal)
               && !avisoTrava.Contains("comoTrava", StringComparison.Ordinal) && !trava.Contains("ex.Message", StringComparison.Ordinal),
            "TC-29 a Configuração muda a trava pelo MudarTrava com a janela do PDV, e a tela mostra a frase curta (o detalhe só na auditoria)");
        checar(!cfg.Contains("\"Não consegui gravar no Windows: \" + erroTrava", StringComparison.Ordinal)
               && !cfg.Contains("\"Não consegui gravar no Windows: \" + erroQuiosque", StringComparison.Ordinal)
               && cfg.Contains("\"Não consegui mudar o modo quiosque no Windows. O resto foi salvo.\"", StringComparison.Ordinal),
            "TC-30 nenhum texto do Windows (em inglês, com caminho de registro) vai para a tela, nem da trava nem do quiosque");
        checar(cfgXaml.Contains("Ao salvar, o Windows pede permissão de administrador: toque em Sim.", StringComparison.Ordinal),
            "TC-31 o texto da opção avisa que o Windows vai pedir permissão");

        var novos = new[] { trava, Trecho(cfg, "var quiosqueQuer =", "var travarQuer ="), Trecho(cfgXaml, "x:Name=\"ChkTravarAtalhos\"", "x:Name=\"BtnSairWindows\""), nucleo };
        checar(novos.All(t => t.Length > 0 && !t.Contains('\u2014') && !t.Contains('\u2013')),
            "TC-32 sem travessão nos textos novos (tela, opção e o núcleo da trava)");
    }

    /// <summary>As quatro políticas embaixo da raiz são DWORD 1?</summary>
    private static bool QuatroDwords(RegistryKey raiz)
    {
        foreach (var (chave, valor) in TravaDoCtrlAltDel.Travas)
        {
            using var k = raiz.OpenSubKey(chave);
            if (k is null || k.GetValueKind(valor) != RegistryValueKind.DWord || k.GetValue(valor) is not int v || v != 1) return false;
        }
        return true;
    }

    private static bool Lanca(Action a)
    {
        try { a(); return false; }
        catch { return true; }
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

    private static string Trecho(string todo, string de, string ate)
    {
        var i = todo.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var f = todo.IndexOf(ate, i, StringComparison.Ordinal);
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
