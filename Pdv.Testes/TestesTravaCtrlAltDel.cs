using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A TRAVA DO CTRL+ALT+DEL COM O ADMINISTRADOR DO WINDOWS (1.0.36, 14/10/2026, Savassi, caixa 1.0.34).
/// O dono marcou a trava e viu "Access to the registry key ... Policies ... is denied": a chave
/// Policies do usuário só aceita escrita de administrador elevado. Agora o PDV pede ao Windows para
/// rodar como administrador só o cmd.exe de System32 com quatro reg.exe (de System32), que gravam
/// em HKEY_USERS\SID. Revisão de segurança: o Pdv.exe NUNCA sobe como administrador (ele extrai
/// bibliotecas numa pasta que o usuário grava, %TEMP%\.net).
///
///  TC-1..4   a linha do cmd: o que sai para ligar e desligar, SID e pasta do sistema recusados,
///            quando NÃO pedir ao Windows
///  TC-5..10  o gravador numa chave ISOLADA (HKCU\Software\PdvNativoTeste\guid), criada e apagada
///            aqui; as políticas de verdade deste PC não são tocadas
///  TC-11..19 a ordem: direto, aviso do Windows, o Não (1223) vira frase curta, o registro relido manda
///  TC-20..35 o fonte: a abertura nunca chama o aviso, o Pdv.exe não tem modo de administrador, a tela
///  TC-36..37 a linha de VERDADE rodada num cmd comum (sem administrador) contra a chave isolada, e o
///            que a linha pode conter
/// Nenhuma prova abre o aviso do Windows nem grava Policies ou Winlogon de verdade.
/// </summary>
public static class TestesTravaCtrlAltDel
{
    private const string Eu = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string Entra = "S-1-12-1-1234567890-1234567890-1234567890-1234567890";
    private const string Sys = @"C:\WINDOWS\system32";

    public static void Rodar(Action<bool, string> checar)
    {
        // ── TC-1 a linha do cmd, ligar e desligar, montada à mão aqui (não copiada do núcleo) ──
        const string Reg = "\"" + Sys + @"\reg.exe" + "\"";
        const string HkuSys = "\"HKU\\" + Eu + @"\Software\Microsoft\Windows\CurrentVersion\Policies\System" + "\"";
        const string HkuExp = "\"HKU\\" + Eu + @"\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" + "\"";
        var esperadoOn = "/d /v:off /s /c \""
            + Reg + " add " + HkuSys + " /v DisableTaskMgr /t REG_DWORD /d 1 /f & "
            + Reg + " add " + HkuSys + " /v DisableLockWorkstation /t REG_DWORD /d 1 /f & "
            + Reg + " add " + HkuSys + " /v DisableChangePassword /t REG_DWORD /d 1 /f & "
            + Reg + " add " + HkuExp + " /v NoLogoff /t REG_DWORD /d 1 /f\"";
        var esperadoOff = "/d /v:off /s /c \""
            + Reg + " delete " + HkuSys + " /v DisableTaskMgr /f & "
            + Reg + " delete " + HkuSys + " /v DisableLockWorkstation /f & "
            + Reg + " delete " + HkuSys + " /v DisableChangePassword /f & "
            + Reg + " delete " + HkuExp + " /v NoLogoff /f\"";
        var on = TravaDoCtrlAltDel.MontarComando(true, Eu, Sys);
        var off = TravaDoCtrlAltDel.MontarComando(false, Eu, Sys);
        checar(on == esperadoOn, "TC-1 ligar: um cmd /d /v:off /s /c com quatro reg.exe add de System32 em HKU\\SID (DWORD 1)" + (on == esperadoOn ? "" : " [saiu: " + on + "]"));
        checar(off == esperadoOff, "TC-1b desligar: os quatro reg.exe delete, separados por & (a trava pela metade não para no primeiro)" + (off == esperadoOff ? "" : " [saiu: " + off + "]"));
        checar(TravaDoCtrlAltDel.MontarComando(true, Entra, @"D:\WINNT\SysWOW64").Contains("\"HKU\\" + Entra + "\\", StringComparison.Ordinal)
               && TravaDoCtrlAltDel.CaminhoDoCmd(Sys) == Sys + @"\cmd.exe",
            "TC-1c conta Entra e Windows em outro disco também; o que o Windows eleva é o cmd.exe de System32, por caminho absoluto");

        // ── TC-2 SID fora do formato nunca vira linha de comando ──
        string[] sidsRuins =
        {
            "", "S-1-5-18", "S-1-5-19", "S-1-5-32-544", ".DEFAULT", Eu + "_Classes",
            Eu + @"\..\S-1-5-18", @"..\" + Eu, Eu + @"\Software", Eu + "\n", " " + Eu,
            "s" + Eu[1..], "S-1-5-21-4294967296-1-2-1001", "S-1-5-21-01-2-3-1001", "S-1-5-21-1-2-1001",
            "S-1-5-21-1-2-3-4-1001", "S-1-5-21-\u0661-2-3-1001",
            Eu + " & calc", Eu + "\" & calc & \"", Eu + "%PATH%", Eu + "^", Eu + "|more", Eu + ">x",
        };
        var passaramSid = sidsRuins.Where(s => TravaDoCtrlAltDel.SidDeUsuario(s) || !Lanca(() => TravaDoCtrlAltDel.MontarComando(true, s, Sys))).ToList();
        checar(passaramSid.Count == 0 && !TravaDoCtrlAltDel.SidDeUsuario(null) && TravaDoCtrlAltDel.SidDeUsuario(Eu) && TravaDoCtrlAltDel.SidDeUsuario(Entra),
            "TC-2 recusa SID de sistema ou de grupo, caminho no SID, maiúscula, número fora do formato e qualquer & \" % ^ | > (nada vira comando)"
            + (passaramSid.Count == 0 ? "" : " [passaram: " + string.Join(" | ", passaramSid.Select(s => s.Replace("\n", "\\n"))) + "]"));

        // ── TC-2b a pasta do sistema: só X:\<pasta do Windows>\system32 ou SysWOW64 ──
        string[] pastasRuins =
        {
            "", "system32", @"C:\system32", @"C:\Users\caixa\AppData\Local\Temp\system32", @"C:\Windows\..\system32",
            @"C:\..\system32", @"C:\Windows\system32\", @"\\servidor\c$\Windows\system32", "C:/Windows/system32",
            @"C:\Win dows\system32", @"C:\Windows\System", @"C:\Win%x%\system32", @"C:\Win&x\system32",
            "C:\\Windows\"\\system32", @"C:\Windows\system32x", "C:\\Windows\\system32\n",
        };
        var passaramPasta = pastasRuins.Where(p => TravaDoCtrlAltDel.PastaDoSistema(p) || !Lanca(() => TravaDoCtrlAltDel.MontarComando(true, Eu, p))
                                                   || !Lanca(() => TravaDoCtrlAltDel.CaminhoDoCmd(p))).ToList();
        checar(passaramPasta.Count == 0 && !TravaDoCtrlAltDel.PastaDoSistema(null)
               && TravaDoCtrlAltDel.PastaDoSistema(@"C:\WINDOWS\system32") && TravaDoCtrlAltDel.PastaDoSistema(@"C:\Windows\System32")
               && TravaDoCtrlAltDel.PastaDoSistema(@"D:\WINNT\SysWOW64"),
            "TC-2b só eleva de X:\\<Windows>\\system32 (ou SysWOW64): nunca Temp, rede, '..', espaço, aspas, % ou &"
            + (passaramPasta.Count == 0 ? "" : " [passaram: " + string.Join(" | ", passaramPasta.Select(s => s.Replace("\n", "\\n"))) + "]"));
        checar(TravaDoCtrlAltDel.PastaDoSistema(Environment.SystemDirectory),
            "TC-2c a pasta do sistema DESTE PC (" + Environment.SystemDirectory + ") passa no formato: o caminho de verdade não fica de fora");

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
            "TC-3b a linha só conhece as quatro políticas (e a lista é uma só)");

        // ── TC-4 quando NÃO pedir ao Windows ──
        var todosExistem = new Func<string, bool>(_ => true);
        checar(TravaDoCtrlAltDel.MotivoParaNaoPedir(Eu, Sys, todosExistem) is null,
            "TC-4 conta de pessoa, pasta do sistema certa e cmd/reg no lugar: pode pedir");
        var semSid = TravaDoCtrlAltDel.MotivoParaNaoPedir("S-1-5-18", Sys, todosExistem);
        var semPasta = TravaDoCtrlAltDel.MotivoParaNaoPedir(Eu, @"C:\Users\caixa\system32", todosExistem);
        var semReg = TravaDoCtrlAltDel.MotivoParaNaoPedir(Eu, Sys, c => !c.EndsWith(@"\reg.exe", StringComparison.Ordinal));
        var lancaExiste = TravaDoCtrlAltDel.MotivoParaNaoPedir(Eu, Sys, _ => throw new IOException("disco"));
        checar(semSid?.Contains("não é de pessoa", StringComparison.Ordinal) == true && TravaDoCtrlAltDel.MotivoParaNaoPedir(null, Sys, todosExistem) is not null
               && semPasta?.Contains("pasta do sistema", StringComparison.Ordinal) == true
               && semReg?.Contains("reg.exe", StringComparison.Ordinal) == true && lancaExiste is not null,
            "TC-4b SYSTEM ou sem SID, pasta fora do formato, reg.exe faltando ou conferência que lança: não pede (o motivo vai para a auditoria)");

        // ── TC-5..10 o gravador numa chave isolada ──
        // Nunca Registry.CurrentUser direto: tudo embaixo de HKCU\Software\PdvNativoTeste\<guid>,
        // criada aqui e apagada no finally. As políticas de verdade deste PC são lidas antes e depois.
        var antes = (Quiosque.Travado, Quiosque.AlgumaTrava);
        const string Pai = @"Software\PdvNativoTeste";
        var nome = Pai + @"\" + Guid.NewGuid().ToString("N");
        try
        {
            using (Registry.CurrentUser.CreateSubKey(nome)) { }
            RegistryKey? Abrir() => Registry.CurrentUser.OpenSubKey(nome, writable: true);
            bool isolada;
            using (var r = Abrir())
                isolada = r is not null && r.Name.StartsWith(@"HKEY_CURRENT_USER\Software\PdvNativoTeste\", StringComparison.OrdinalIgnoreCase);
            checar(isolada, "TC-5 a chave de teste existe e é isolada (HKCU\\Software\\PdvNativoTeste\\guid)");
            if (isolada)
            {
                // TC-6 ligar: as quatro como DWORD 1
                using (var r = Abrir()!)
                {
                    Quiosque.GravarTravas(r, true);
                    checar(Quiosque.LerTravas(r) == (true, true) && QuatroDwords(r), "TC-6 o gravador liga: as quatro políticas como DWORD 1");
                }

                // TC-9 desligar: nenhuma sobra; desligar de novo não lança
                using (var r = Abrir()!)
                {
                    Quiosque.GravarTravas(r, false);
                    var deNovo = !Lanca(() => Quiosque.GravarTravas(r, false));
                    checar(Quiosque.LerTravas(r) == (false, false) && deNovo, "TC-9 o gravador desliga as quatro, e desligar de novo é inofensivo");
                }

                // TC-8 raiz aberta só para leitura (sem permissão de escrita): ligar lança acesso
                // negado (o CreateSubKey do .NET exige raiz gravável), que é o que leva ao aviso do
                // Windows, e nada muda
                Exception? negadoDeVerdade = null;
                using (var soLeitura = Registry.CurrentUser.OpenSubKey(nome)!)
                {
                    try { Quiosque.GravarTravas(soLeitura, true); }
                    catch (Exception ex) { negadoDeVerdade = ex; }
                }
                using (var r = Abrir()!)
                    checar(TravaDoCtrlAltDel.AcessoNegado(negadoDeVerdade) && Quiosque.LerTravas(r) == (false, false),
                        "TC-8 sem permissão de escrita o gravador lança acesso negado (" + (negadoDeVerdade?.GetType().Name ?? "nada") + ") e não muda nada");

                // TC-10 desligar não cria chave; a trava pela metade é vista e desfeita
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

                // TC-10d um DWORD 0 (o que as ferramentas de "reativar o Gerenciador de Tarefas"
                // deixam) ou um valor de outro tipo NÃO é a nossa trava: não é "alguma", senão todo
                // Salvar num PC assim pediria permissão ao Windows à toa (PrecisaMudar(false,false,·)).
                var zero = nome + @"\zero";
                using (Registry.CurrentUser.CreateSubKey(zero)) { }
                using (var r = Registry.CurrentUser.OpenSubKey(zero, writable: true)!)
                {
                    using (var k = r.CreateSubKey(TravaDoCtrlAltDel.ChaveSistema))
                    {
                        k.SetValue("DisableTaskMgr", 0, RegistryValueKind.DWord);
                        k.SetValue("DisableLockWorkstation", "1", RegistryValueKind.String); // tipo errado: não é nossa
                    }
                    var lido = Quiosque.LerTravas(r);
                    checar(lido == (false, false)
                           && !TravaDoCtrlAltDel.PrecisaMudar(false, lido.Todas, lido.Alguma),
                        "TC-10d DWORD 0 (ou valor de outro tipo) não conta como trava, e um Salvar comum não pede nada ao Windows");
                }

                // ── TC-36 a linha de VERDADE, rodada num cmd COMUM (sem runas, sem administrador)
                // contra a chave isolada: só a raiz HKU\SID é trocada pela chave de teste. Prova que
                // as aspas, o /s /c e os & funcionam no cmd e no reg.exe deste Windows. ──
                var sys = Environment.SystemDirectory;
                if (TravaDoCtrlAltDel.MotivoParaNaoPedir(Eu, sys, File.Exists) is null)
                {
                    var raizCmd = nome + @"\cmd";
                    using (Registry.CurrentUser.CreateSubKey(raizCmd)) { }
                    string NaChaveDeTeste(string c) => c.Replace("\"HKU\\" + Eu + "\\", "\"HKCU\\" + raizCmd + "\\", StringComparison.Ordinal);
                    var linhaOn = NaChaveDeTeste(TravaDoCtrlAltDel.MontarComando(true, Eu, sys));
                    var linhaOff = NaChaveDeTeste(TravaDoCtrlAltDel.MontarComando(false, Eu, sys));
                    var trocou = !linhaOn.Contains("HKU\\", StringComparison.Ordinal) && Contar(linhaOn, "\"HKCU\\" + raizCmd + "\\") == 4
                                 && !linhaOff.Contains("HKU\\", StringComparison.Ordinal) && Contar(linhaOff, "\"HKCU\\" + raizCmd + "\\") == 4;
                    checar(trocou, "TC-36a a linha de teste só troca a raiz (as quatro HKU\\SID viram a chave isolada; nenhuma sobra)");
                    if (trocou)
                    {
                        var terminouOn = RodarCmdComum(sys, linhaOn);
                        bool ligouPeloCmd, quatro;
                        using (var r = Registry.CurrentUser.OpenSubKey(raizCmd)!) { ligouPeloCmd = Quiosque.LerTravas(r) == (true, true); quatro = QuatroDwords(r); }
                        checar(terminouOn && ligouPeloCmd && quatro,
                            "TC-36 o cmd /d /v:off /s /c com os quatro reg.exe add grava as quatro políticas como DWORD 1 (cmd comum, chave isolada)");

                        // metade: tira uma à mão; o desligar com & tem que tirar as outras três mesmo
                        // com o primeiro delete falhando
                        using (var k = Registry.CurrentUser.OpenSubKey(raizCmd + @"\" + TravaDoCtrlAltDel.ChaveSistema, writable: true)!)
                            k.DeleteValue("DisableTaskMgr");
                        var terminouOff = RodarCmdComum(sys, linhaOff);
                        bool desligouPeloCmd;
                        using (var r = Registry.CurrentUser.OpenSubKey(raizCmd)!) desligouPeloCmd = Quiosque.LerTravas(r) == (false, false);
                        checar(terminouOff && desligouPeloCmd,
                            "TC-36b o desligar com & tira as outras três mesmo com o primeiro delete falhando (trava pela metade)");
                    }
                }
                else checar(false, "TC-36 este PC não tem cmd.exe e reg.exe em " + sys + " no formato esperado");
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
        bool WindowsGrava() { pedidos++; return true; }
        bool WindowsNao() { pedidos++; throw new Win32Exception(TravaDoCtrlAltDel.ErroCancelado); }

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
        var rFora = TravaDoCtrlAltDel.Mudar(() => negado, () => false, "cmd.exe ou reg.exe não estão em X", WindowsGrava);
        checar(rFora.Resultado == ResultadoDaTrava.Falhou && pedidos == 0 && rFora.Detalhe.Contains("reg.exe não estão", StringComparison.Ordinal),
            "TC-16 sem como pedir (SID, pasta do sistema ou cmd/reg): não pede administrador, e o motivo vai para a auditoria");

        var rRelido = TravaDoCtrlAltDel.Mudar(() => negado, () => false, null, () => true);
        var rSemFim = TravaDoCtrlAltDel.Mudar(() => negado, () => true, null, () => false);
        checar(rRelido.Resultado == ResultadoDaTrava.Falhou && rSemFim.Resultado == ResultadoDaTrava.Ok
               && rSemFim.Detalhe.Contains("não terminou em 20 s", StringComparison.Ordinal) && rRelido.Detalhe.Contains("terminou", StringComparison.Ordinal),
            "TC-17 o resultado é o registro relido, não o fim do cmd (terminou ou não, isso só vai para o detalhe)");

        var rOutro = TravaDoCtrlAltDel.Mudar(() => negado, () => false, null, () => throw new Win32Exception(2));
        var rLanca = TravaDoCtrlAltDel.Mudar(() => throw negado, () => true, null, () => true);
        pedidos = 0;
        var rReler = TravaDoCtrlAltDel.Mudar(() => null, () => throw new IOException("leitura"), null, WindowsGrava);
        checar(rReler.Resultado == ResultadoDaTrava.Falhou && pedidos == 0,
            "TC-18b reler o registro que lança não derruba o Salvar (Falhou, sem aviso do Windows)");
        var rRelerAdm = TravaDoCtrlAltDel.Mudar(() => negado, () => throw new IOException("leitura"), null, () => true);
        checar(rOutro.Resultado == ResultadoDaTrava.Falhou && rLanca.Resultado == ResultadoDaTrava.Ok && rRelerAdm.Resultado == ResultadoDaTrava.Falhou,
            "TC-18 outro erro do Windows não é Cancelou; tentativa direta que lança é tratada como recusa; reler que lança depois do cmd é Falhou");

        checar(TravaDoCtrlAltDel.Cancelado(new Win32Exception(1223)) && !TravaDoCtrlAltDel.Cancelado(new Win32Exception(5))
               && !TravaDoCtrlAltDel.Cancelado(negado) && !TravaDoCtrlAltDel.Cancelado(null)
               && TravaDoCtrlAltDel.AcessoNegado(negado) && TravaDoCtrlAltDel.AcessoNegado(new SecurityException())
               && !TravaDoCtrlAltDel.AcessoNegado(new IOException()) && !TravaDoCtrlAltDel.AcessoNegado(null),
            "TC-19 só o 1223 é o Não; só acesso negado pede administrador");

        // ── TC-20 as frases da tela: uma linha, sem travessão, sem nada do Windows em inglês ──
        var frases = new[] { ResultadoDaTrava.Ok, ResultadoDaTrava.Cancelou, ResultadoDaTrava.Falhou }
            .SelectMany(r => new[] { TravaDoCtrlAltDel.Frase(r, true), TravaDoCtrlAltDel.Frase(r, false) }).ToList();
        var feias = frases.Where(t => t.Length > 100 || t.Contains('\u2014') || t.Contains('\u2013') || t.Contains('\n')
            || Regex.IsMatch(t, "(?i)access|denied|registry|exception|hkey|polic|runas|uac|1223|sid|cmd")).ToList();
        checar(feias.Count == 0 && TravaDoCtrlAltDel.Frase(ResultadoDaTrava.Falhou, true) == "Não consegui mudar a trava do Ctrl+Alt+Del. O resto foi salvo.",
            "TC-20 frases curtas, de uma linha, sem travessão e sem texto do Windows" + (feias.Count == 0 ? "" : " [" + string.Join(" | ", feias) + "]"));

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
        var linha = Fonte("Pdv.Nucleo", "LinhaDeComando.cs") ?? "";

        // TC-23 o Pdv.exe não tem modo de administrador: nem --travas, nem ajudante, nem a guarda dele
        checar(app.Length > 0 && linha.Length > 0
               && !app.Contains("--travas", StringComparison.Ordinal) && !app.Contains("ModoDoExe.Travas", StringComparison.Ordinal)
               && !app.Contains("RodarAjudante", StringComparison.Ordinal) && !app.Contains("TravaDoCtrlAltDel.Argumento", StringComparison.Ordinal)
               && !linha.Contains("--travas", StringComparison.Ordinal) && !linha.Contains("LerTravas", StringComparison.Ordinal)
               && !Enum.GetNames<ModoDoExe>().Contains("Travas")
               && LinhaDeComando.Modo(new[] { "--travas", "on", "--sid", Eu }) == ModoDoExe.Caixa,
            "TC-23 o Pdv.exe não tem mais modo de administrador (--travas sumiu do App, da linha de comando e do enum)");
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

        // TC-27 o que sobe como administrador é o cmd de System32, nunca o Pdv.exe
        var pedir = Corpo(q, "private static bool PedirAoWindows(");
        var mudar = Corpo(q, "public static (ResultadoDaTrava Resultado, string Detalhe) MudarTrava(");
        checar(pedir.Contains("new ProcessStartInfo(TravaDoCtrlAltDel.CaminhoDoCmd(sistema))", StringComparison.Ordinal)
               && pedir.Contains("Verb = \"runas\"", StringComparison.Ordinal) && pedir.Contains("WorkingDirectory = sistema", StringComparison.Ordinal)
               && !pedir.Contains("ExeAtual", StringComparison.Ordinal) && !pedir.Contains("ProcessPath", StringComparison.Ordinal)
               && mudar.Contains("var sistema = Environment.SystemDirectory;", StringComparison.Ordinal)
               && mudar.Contains("TravaDoCtrlAltDel.MontarComando(travar, sid!, sistema)", StringComparison.Ordinal)
               && mudar.Contains("TravaDoCtrlAltDel.MotivoParaNaoPedir(sid, sistema, File.Exists)", StringComparison.Ordinal)
               && !mudar.Contains("ExeAtual", StringComparison.Ordinal)
               && !q.Contains("RodarAjudante", StringComparison.Ordinal) && !q.Contains("Registry.Users", StringComparison.Ordinal)
               && Contar(q, "\"runas\"") == 1,
            "TC-27 o que o Windows eleva é o cmd.exe de System32 (Environment.SystemDirectory) com a linha do núcleo; o Pdv.exe nunca, e o PDV não grava HKEY_USERS sozinho");
        var elevaEmOutroLugar = FontesDoCaixa().Where(f => !f.Nome.EndsWith("Quiosque.cs", StringComparison.Ordinal) && f.Texto.Contains("\"runas\"", StringComparison.Ordinal)).Select(f => f.Nome).ToList();
        checar(elevaEmOutroLugar.Count == 0,
            "TC-27b nenhum outro fonte do caixa pede administrador ao Windows" + (elevaEmOutroLugar.Count == 0 ? "" : " [" + string.Join(", ", elevaEmOutroLugar) + "]"));
        checar(pedir.Contains("ErrorDialogParentHandle = janela", StringComparison.Ordinal) && pedir.Contains("WaitForExit(20000)", StringComparison.Ordinal)
               && pedir.Contains("WindowStyle = ProcessWindowStyle.Hidden", StringComparison.Ordinal)
               && !q.Contains("Registry.LocalMachine", StringComparison.Ordinal),
            "TC-28 o pedido ao Windows usa a janela do PDV como dona, esconde a janela do cmd, espera no máximo 20 s e nunca toca na máquina");

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
        checar(cfgXaml.Contains("Ao salvar, o Windows pode pedir uma permissão de administrador, com o nome Processador de Comandos do Windows.", StringComparison.Ordinal)
               // "pode" (nem sempre pede: UAC desligado ou PDV já elevado), sem "toque em Sim" (o
               // usuário comum digita a senha de um administrador) e com o nome que o aviso mostra
               && !cfgXaml.Contains("toque em Sim", StringComparison.Ordinal),
            "TC-31 o texto da opção avisa que o Windows PODE pedir permissão e com que nome o aviso aparece, sem prometer um Sim");

        var novos = new[] { trava, Trecho(cfg, "var quiosqueQuer =", "var travarQuer ="), Trecho(cfgXaml, "x:Name=\"ChkTravarAtalhos\"", "x:Name=\"BtnSairWindows\""), nucleo };
        checar(novos.All(t => t.Length > 0 && !t.Contains('\u2014') && !t.Contains('\u2013')),
            "TC-32 sem travessão nos textos novos (tela, opção e o núcleo da trava)");

        // ── TC-33 o reler do destravar não engole a leitura que lança ──
        // MudarTrava relê com LerTravas direto (sem o catch dos getters Travado/AlgumaTrava): uma
        // leitura que falha tem que virar "não ficou", senão um destravar daria Ok sem confirmar.
        checar(mudar.Contains("LerTravas(Registry.CurrentUser)", StringComparison.Ordinal)
               && mudar.Contains("travar ? todas : !alguma", StringComparison.Ordinal)
               && !mudar.Contains("!AlgumaTrava", StringComparison.Ordinal),
            "TC-33 o reler do MudarTrava usa LerTravas direto (não o getter que engole a exceção), então um destravar com leitura que falha é Falhou, não Ok");

        // ── TC-34 nada no caixa passa a rodar o Pdv.exe com argumento de trava ──
        checar(!q.Contains("--travas", StringComparison.Ordinal) && !q.Contains("--sid", StringComparison.Ordinal)
               && !nucleo.Contains("\"--travas\"", StringComparison.Ordinal) && !nucleo.Contains("LerArgumentos", StringComparison.Ordinal)
               && !nucleo.Contains("PodeElevar", StringComparison.Ordinal),
            "TC-34 o núcleo e o Quiosque não montam nem leem mais --travas/--sid (o ajudante e o PodeElevar saíram)");

        // ── TC-35 o quiosque e a trava vêm por último, depois de gravar segredos e o certificado ──
        var iSegredos = cfg.IndexOf("GravarSegredos(seg)", StringComparison.Ordinal);
        var iCert = cfg.IndexOf("File.Copy(_pfxEscolhido, ArqCert", StringComparison.Ordinal);
        var iQuiosque = cfg.IndexOf("var quiosqueQuer =", StringComparison.Ordinal);
        var iTrava = cfg.IndexOf("Quiosque.MudarTrava(travarQuer,", StringComparison.Ordinal);
        var iConcluiu = iTrava < 0 ? -1 : cfg.IndexOf("Concluiu?.Invoke();", iTrava, StringComparison.Ordinal);
        checar(iSegredos > 0 && iCert > 0 && iQuiosque > iSegredos && iQuiosque > iCert && iTrava > iQuiosque && iConcluiu > iTrava,
            "TC-35 o quiosque e a trava do Ctrl+Alt+Del são o último passo do Salvar (depois de TEF, certificado e segredos), então 'O resto foi salvo' é verdade");

        // ── TC-37 o que a linha do cmd pode conter ──
        foreach (var (rotulo, c) in new[] { ("ligar", on), ("desligar", off) })
        {
            var miolo = c.StartsWith(TravaDoCtrlAltDel.ChavesDoCmd + " \"", StringComparison.Ordinal) && c.EndsWith("\"", StringComparison.Ordinal)
                ? c[(TravaDoCtrlAltDel.ChavesDoCmd.Length + 2)..^1] : "";
            var partes = miolo.Split(" & ");
            var forma = new Regex("^\"" + Regex.Escape(Sys) + @"\\reg\.exe"" (add|delete) ""HKU\\" + Regex.Escape(Eu) + @"\\([A-Za-z\\]+)"" /v ([A-Za-z]+)( /t REG_DWORD /d 1)? /f\z");
            var lidas = partes.Select(p => forma.Match(p)).ToList();
            checar(TravaDoCtrlAltDel.ComandoLimpo(c) && partes.Length == 4 && lidas.All(m => m.Success)
                   && lidas.Select(m => (m.Groups[2].Value, m.Groups[3].Value)).SequenceEqual(TravaDoCtrlAltDel.Travas)
                   && lidas.All(m => m.Groups[1].Value == (rotulo == "ligar" ? "add" : "delete") && m.Groups[4].Success == (rotulo == "ligar"))
                   && c.IndexOfAny(new[] { '%', '^', '|', '<', '>', '!', '\n', '\r', '(', ')' }) < 0
                   && c.Count(ch => ch == '"') % 2 == 0,
                $"TC-37 ({rotulo}) a linha é só /d /v:off /s /c e quatro reg.exe de System32 em HKU\\SID, nas quatro políticas, sem % ^ | < > ! nem quebra de linha");
        }
        checar(!TravaDoCtrlAltDel.ComandoLimpo(on + " & calc%x%") && !TravaDoCtrlAltDel.ComandoLimpo(on + "|more") && !TravaDoCtrlAltDel.ComandoLimpo(on + "\n")
               && !TravaDoCtrlAltDel.ComandoLimpo(on + "^") && !TravaDoCtrlAltDel.ComandoLimpo(on + "!x!") && !TravaDoCtrlAltDel.ComandoLimpo(null),
            "TC-37b a conferência final da linha recusa %, |, quebra de linha, ^ e !");
        checar(TravaDoCtrlAltDel.ChavesDoCmd == "/d /v:off /s /c",
            "TC-37c o cmd sobe sem AutoRun (/d: o usuário grava o AutoRun do HKCU) e sem expansão atrasada (/v:off)");
    }

    /// <summary>
    /// Roda a linha num cmd COMUM (UseShellExecute=false, sem runas: nada de aviso do Windows nem
    /// administrador), escondido, e espera até 20 s. Só a bateria usa, e só contra a chave isolada.
    /// </summary>
    private static bool RodarCmdComum(string sys, string linha)
    {
        using var p = Process.Start(new ProcessStartInfo(TravaDoCtrlAltDel.CaminhoDoCmd(sys), linha)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = sys,
            RedirectStandardOutput = true, RedirectStandardError = true,
        });
        if (p is null) return false;
        var saida = p.StandardOutput.ReadToEndAsync();
        var erro = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } return false; }
        p.WaitForExit();
        _ = saida.Result; _ = erro.Result;
        return true;
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

    private static int Contar(string texto, string pedaco)
    {
        var n = 0;
        for (var i = texto.IndexOf(pedaco, StringComparison.Ordinal); i >= 0; i = texto.IndexOf(pedaco, i + pedaco.Length, StringComparison.Ordinal)) n++;
        return n;
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

    private static string? Raiz()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Pdv.csproj"))) return d.FullName;
        return null;
    }

    private static string? Fonte(params string[] partes)
    {
        if (Raiz() is not { } raiz) return null;
        var c = Path.Combine(new[] { raiz }.Concat(partes).ToArray());
        return File.Exists(c) ? File.ReadAllText(c).Replace("\r\n", "\n") : null;
    }

    /// <summary>Os .cs do caixa (o Pdv.exe): a raiz e Telas, sem testes, instalador, núcleo nem bin/obj.</summary>
    private static IEnumerable<(string Nome, string Texto)> FontesDoCaixa()
    {
        if (Raiz() is not { } raiz) yield break;
        var pastas = new[] { raiz, Path.Combine(raiz, "Telas") };
        foreach (var pasta in pastas.Where(Directory.Exists))
            foreach (var f in Directory.GetFiles(pasta, "*.cs", SearchOption.TopDirectoryOnly))
                yield return (Path.GetRelativePath(raiz, f), File.ReadAllText(f));
    }
}
