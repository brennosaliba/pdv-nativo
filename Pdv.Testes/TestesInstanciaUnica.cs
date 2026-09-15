using System.Diagnostics;
using System.Runtime.InteropServices;
using Dapper;
using Microsoft.Win32.SafeHandles;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A 2ª INSTÂNCIA DO PDV CONTRA A COBRANÇA QUE ESTÁ NO PINPAD.
///
/// Cenário real: o pinpad está esperando o cartão/senha do cliente, a tela do PDV
/// parece parada, o operador acha que "travou" e clica de novo no ícone. Sobe um
/// SEGUNDO Pdv.exe. Ele attacha no mesmo turno aberto (MainWindow.Roteia →
/// `Caixa.SessaoAberta` → `MostrarVenda`) e, no boot, roda o religamento do TEF.
///
/// E o religamento decide o que é "cobrança abandonada" comparando `criado_em` com
/// o START DESTE PROCESSO:
///   • Servicos.ResolverPendenciasTefAsync — `criado_em &lt; @Inicio`, com
///     `Inicio = Process.GetCurrentProcess().StartTime`, nos dois blocos (PayGo e ControlPay);
///   • ControlPay.ReconciliarAsync — `if (… em >= boot) continue;`, mesmo `boot`.
/// Para a 2ª instância, a cobrança VIVA da 1ª nasceu antes do boot dela: é sempre
/// "abandonada". O `SemaphoreSlim _um` do ClienteControlPay não salva — é campo de
/// instância, morre na fronteira do processo.
///
/// O desfecho é o pior possível: a cobrança legítima (que pode ter sido APROVADA no
/// pinpad) vira `orfa` com "confira no PayGo e estorne se aprovou" — e o operador
/// estorna dinheiro que era da loja.
///
/// A trava é INSTÂNCIA ÚNICA: o 2º Pdv.exe não boota. Este teste prova isso com DOIS
/// PROCESSOS DE VERDADE (a SONDA 4), não com duas chamadas no mesmo processo — o furo
/// é justamente que toda a defesa de hoje é intra-processo.
/// </summary>
public static class TestesInstanciaUnica
{
    /// <summary>Código de saída do processo-filho quando a trava recusou o boot.</summary>
    public const int Recusada = 3;

    public static void Rodar(Action<bool, string> checar)
    {
        // ── 1. A TRAVA EM SI ────────────────────────────────────────────────
        // Nome próprio por execução: o Pdv.exe de verdade pode estar aberto nesta
        // máquina (e vai estar, na loja) — o teste não pode brigar com ele.
        var nome = "PdvNativo.Teste." + Guid.NewGuid().ToString("N");
        var primeira = InstanciaUnica.Tentar(nome);
        checar(primeira is not null, "o 1º PDV pega a trava do terminal");

        var segunda = InstanciaUnica.Tentar(nome);
        checar(segunda is null, "com um PDV aberto, o 2º é RECUSADO pela trava");
        segunda?.Dispose();
        primeira?.Dispose();

        var religou = InstanciaUnica.Tentar(nome);
        checar(religou is not null, "fechado o 1º, o caixa religa normalmente (a trava não fica presa)");
        religou?.Dispose();

        // ── 2. SONDA 4: DOIS PROCESSOS DE VERDADE ───────────────────────────
        var arquivo = Path.Combine(Path.GetTempPath(), $"instancia_teste_{Guid.NewGuid():N}.db");
        var travaSonda = "PdvNativo.Teste." + Guid.NewGuid().ToString("N");
        try
        {
            Banco.Migrar(arquivo);
            SemearCobrancasVivas(arquivo);

            using (var dono = InstanciaUnica.Tentar(travaSonda))
            {
                checar(dono is not null, "sonda: a 1ª instância (cliente no pinpad) está com a trava");

                var (codigo, saida) = SubirSegundaInstancia(arquivo, travaSonda);
                checar(codigo == Recusada,
                    $"a 2ª instância sai sem bootar (código {codigo}, esperado {Recusada}){saida}");

                var (paygo, cpay) = Situacoes(arquivo);
                checar(paygo == "aguardando",
                    $"a cobrança PayGo de R$ 500,00 continua 'aguardando' no pinpad (ficou '{paygo}')");
                checar(cpay == "criando",
                    $"a intenção ControlPay em voo continua 'criando' (ficou '{cpay}')");
            }

            // ── 3. CONTROLE NEGATIVO ────────────────────────────────────────
            // Sem a trava na mão, o MESMO processo-filho, no MESMO banco, declara as
            // duas órfãs. É o que provava a SONDA 4 — e é o que o teste acima impede.
            // Sem esta parte, o teste passaria mesmo que o religamento tivesse virado
            // um no-op por acidente.
            {
                var (codigo, saida) = SubirSegundaInstancia(arquivo, travaSonda);
                checar(codigo == 0, $"solta a trava e a instância boota de verdade (código {codigo}){saida}");

                var (paygo, cpay) = Situacoes(arquivo);
                checar(paygo == "orfa" && cpay == "orfa",
                    $"controle negativo: SEM a trava o religamento carimba órfã a cobrança viva "
                    + $"(paygo='{paygo}', cpay='{cpay}') — é exatamente isto que a trava impede");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }

        // ── 4. O BOOT DO PDV REALMENTE PASSA PELA TRAVA ─────────────────────
        // A trava só vale se estiver no caminho do boot, ANTES de o processo mexer no
        // banco e de disparar o religamento do TEF. Isto é code-behind de WPF: não dá
        // para instanciar num teste, então se confere a fonte.
        {
            string? fonte = null;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                var alvo = Path.Combine(d.FullName, "App.xaml.cs");
                if (File.Exists(alvo)) { fonte = File.ReadAllText(alvo); break; }
            }
            checar(fonte is not null, "achei a fonte do boot (App.xaml.cs) para conferir a trava");

            var f = fonte ?? "";
            var trava = f.IndexOf("InstanciaUnica.Tentar", StringComparison.Ordinal);
            checar(trava >= 0, "o boot do PDV pega a trava de instância única");

            var religamento = f.IndexOf("ResolverPendenciasTefAsync", StringComparison.Ordinal);
            checar(trava >= 0 && religamento > trava,
                "a trava vem ANTES de o boot disparar o religamento do TEF");

            // O `--cupom-teste`/`--imprimir-teste` roda e sai sem abrir o caixa: aquele
            // Migrar pode conviver com o PDV aberto. O que a trava tem de cobrir é o
            // Migrar do caminho normal — o ÚLTIMO do arquivo.
            var migrar = f.LastIndexOf("Banco.Migrar()", StringComparison.Ordinal);
            checar(trava >= 0 && migrar > trava,
                "a trava vem ANTES de a 2ª instância tocar no banco do caixa");

            // RECUSADA TEM QUE SER MORTE, NÃO PEDIDO DE SAÍDA.
            // Medido num app WPF de teste: com `Shutdown(0); return;` dentro de OnStartup,
            // o WPF AINDA constrói o StartupUri (MainWindow) — Shutdown só posta a saída no
            // dispatcher. E o construtor da MainWindow chama Roteia(), que abre o banco do
            // caixa e attacha no turno aberto. Ou seja: com Shutdown a trava não trava nada.
            var recusada = f.IndexOf("if (_trava is null)", StringComparison.Ordinal);
            var fim = recusada < 0 ? -1 : f.IndexOf("\n        }", recusada, StringComparison.Ordinal);
            var corpo = recusada < 0 || fim < 0 ? "" : f[recusada..fim];
            checar(corpo.Contains("Environment.Exit", StringComparison.Ordinal),
                "a instância recusada MORRE ali (Environment.Exit) — não segue para a MainWindow");
            checar(corpo.Length > 0 && !corpo.Contains("Shutdown(", StringComparison.Ordinal),
                "a recusa não confia no Shutdown() do WPF, que ainda constrói a MainWindow");
        }

        // ── 5. CAIXA ABERTO COMO ADMINISTRADOR (OU POR OUTRA CONTA) ─────────
        // 14/09/2026, Castelo: dois Pdv.exe abertos. O instalador roda elevado e abre o caixa
        // ("Abrir o caixa e configurar a loja"); o mutex Global\ nasce com a DACL de quem é
        // administrador. O clique seguinte no ícone, sem elevação, toma ACESSO NEGADO ao abrir
        // esse mutex, a trava antiga lia isso como "não deu para criar", caía no Local\ (que
        // estava livre) e subia o 2º caixa. Aqui o mutex alheio é montado com uma DACL que só
        // deixa o SISTEMA abrir: para este processo, é exatamente aquele acesso negado.
        {
            checar(InstanciaUnica.Classificar(abriu: true, erroWin32: 0) == InstanciaUnica.Resposta.Nossa,
                "trava: criou agora, é nossa");
            checar(InstanciaUnica.Classificar(abriu: true, erroWin32: 183) == InstanciaUnica.Resposta.JaExiste,
                "trava: já existia (ERROR_ALREADY_EXISTS), há um caixa aberto");
            checar(InstanciaUnica.Classificar(abriu: false, erroWin32: 5) == InstanciaUnica.Resposta.JaExiste,
                "trava: acesso negado também é caixa aberto (de outra conta ou como administrador)");
            checar(InstanciaUnica.Classificar(abriu: false, erroWin32: 6) == InstanciaUnica.Resposta.TentarOutroEscopo,
                "trava: outro erro do Windows não é caixa aberto, tenta a trava da sessão");

            var nomeNegado = "PdvNativo.Teste." + Guid.NewGuid().ToString("N");
            using var alheia = MutexSoDoSistema(@"Global\" + nomeNegado, out var erroAlheia);
            checar(alheia is not null, $"sonda: montei a trava de um caixa que este usuário não consegue abrir (erro {erroAlheia})");
            if (alheia is not null)
            {
                var intrusa = InstanciaUnica.Tentar(nomeNegado);
                checar(intrusa is null,
                    "com o caixa aberto como administrador, o clique normal no ícone é RECUSADO (acesso negado não abre o 2º caixa)"
                    + (intrusa is null ? "" : $" [pegou a trava em '{intrusa.Escopo}']"));
                intrusa?.Dispose();
            }
        }

        // ── 6. MODO DE FERRAMENTA NÃO ABRE O CAIXA NEM PASSA PELA TRAVA ────
        {
            checar(LinhaDeComando.Modo(new[] { "--cupom-teste", "x.png" }) == ModoDoExe.CupomTeste
                   && LinhaDeComando.Modo(new[] { "--imprimir-teste" }) == ModoDoExe.ImprimirTeste,
                "linha de comando: --cupom-teste e --imprimir-teste são modos de ferramenta");
            checar(!LinhaDeComando.PegaATrava(ModoDoExe.CupomTeste) && !LinhaDeComando.AbreOCaixa(ModoDoExe.CupomTeste)
                   && !LinhaDeComando.PegaATrava(ModoDoExe.ImprimirTeste) && !LinhaDeComando.AbreOCaixa(ModoDoExe.ImprimirTeste),
                "linha de comando: ferramenta não pega a trava (nem é barrada por ela) e não abre o caixa");
            checar(LinhaDeComando.Modo(Array.Empty<string>()) == ModoDoExe.Caixa && LinhaDeComando.Modo(null) == ModoDoExe.Caixa
                   && LinhaDeComando.PegaATrava(ModoDoExe.Caixa) && LinhaDeComando.AbreOCaixa(ModoDoExe.Caixa),
                "linha de comando: sem argumento é o caixa, com trava");
            checar(LinhaDeComando.Modo(new[] { "--qualquer-coisa" }) == ModoDoExe.Caixa,
                "linha de comando: argumento desconhecido abre o caixa normal, com trava");

            var app = Fonte("App.xaml.cs") ?? "";
            var xaml = Fonte("App.xaml") ?? "";
            checar(xaml.Length > 0 && !xaml.Contains("StartupUri", StringComparison.Ordinal),
                "App.xaml não abre a MainWindow sozinho (StartupUri): era assim que o --cupom-teste abria um caixa sem trava");
            checar(app.Contains("LinhaDeComando.Modo(", StringComparison.Ordinal),
                "o boot decide o modo pela regra testada aqui");
            var trava = app.IndexOf("InstanciaUnica.Tentar", StringComparison.Ordinal);
            var janela = app.IndexOf("new MainWindow(", StringComparison.Ordinal);
            checar(trava >= 0 && janela > trava, "a janela do caixa nasce no boot, DEPOIS da trava");
        }

        // ── 7. O Pdv.App DE VERDADE NO --cupom-teste, COM O CAIXA ABERTO ────
        // Processo real, como o instalador faz. A trava do terminal fica na mão desta bateria
        // durante a sonda (ou já está com um PDV aberto nesta máquina): o modo de ferramenta
        // tem que desenhar o cupom e sair do mesmo jeito, sem abrir janela nenhuma.
        {
            var banco = Path.Combine(Path.GetTempPath(), $"cupom_sonda_{Guid.NewGuid():N}.db");
            var png = Path.Combine(Path.GetTempPath(), $"cupom_sonda_{Guid.NewGuid():N}.png");
            try
            {
                using var caixaAberto = InstanciaUnica.Tentar();
                var relogio = Stopwatch.StartNew();
                var (codigo, saida) = SubirPdvApp(banco, "--cupom-teste", png);
                relogio.Stop();
                var resumo = saida.Trim().Length == 0 ? "" : " | " + saida.Trim().Replace("\r", "").Replace("\n", " | ");
                checar(codigo == 0 && File.Exists(png) && new FileInfo(png).Length > 0,
                    $"--cupom-teste com o caixa aberto desenha o cupom e sai com 0 (código {codigo}, {relogio.ElapsedMilliseconds} ms){resumo}");
                checar(!saida.Contains(Sondas.MarcaJanela, StringComparison.Ordinal),
                    "--cupom-teste não abre janela nenhuma do caixa" + resumo);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var a in new[] { banco, banco + "-wal", banco + "-shm", png })
                    try { File.Delete(a); } catch { }
            }
        }

        // ── 8. FERRAMENTA QUE FALHA SAI COM 1, SEM AVISO E SEM FICAR VIVA ───
        // Revisão da onda 2 (14/09/2026): sem a MainWindow, uma exceção no --cupom-teste (aqui,
        // um banco que não abre) caía no aviso "segurei o caixa de pé" e deixava o processo vivo
        // e invisível até o instalador matá-lo aos 90 s. Tem que sair com 1 e escrever FALHOU.
        {
            var bloqueio = Path.Combine(Path.GetTempPath(), $"cupom_bloqueio_{Guid.NewGuid():N}");
            var png = Path.Combine(Path.GetTempPath(), $"cupom_sonda_{Guid.NewGuid():N}.png");
            try
            {
                File.WriteAllText(bloqueio, "arquivo no lugar da pasta: o banco nao abre");
                var (codigo, saida) = SubirPdvApp(Path.Combine(bloqueio, "pdv.db"), "--cupom-teste", png);
                var resumo = saida.Trim().Length == 0 ? "" : " | " + saida.Trim().Replace("\r", "").Replace("\n", " | ");
                checar(codigo == 1 && saida.Contains("FALHOU", StringComparison.Ordinal),
                    $"--cupom-teste com banco que não abre sai com 1 e escreve FALHOU, sem aviso de tela (código {codigo}){resumo}");
                checar(!saida.Contains(Sondas.MarcaJanela, StringComparison.Ordinal),
                    "--cupom-teste que falha não abre janela nenhuma do caixa" + resumo);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var a in new[] { bloqueio, png })
                    try { File.Delete(a); } catch { }
            }
        }
    }

    /// <summary>Sobe o Pdv.App de verdade (modo sonda do Pdv.Testes) com os argumentos do Pdv.exe.</summary>
    private static (int Codigo, string Saida) SubirPdvApp(string banco, params string[] argumentos)
    {
        var psi = Sondas.Psi(argumentos);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.Environment[Sondas.VariavelApp] = "1";
        psi.Environment[Sondas.VariavelBanco] = banco;
        using var p = Process.Start(psi)!;
        var saida = p.StandardOutput.ReadToEndAsync();
        var erro = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(90_000)) { try { p.Kill(true); } catch { } return (-1, "[o Pdv.App não terminou em 90 s]"); }
        Task.WaitAll(new Task[] { saida, erro }, 5_000);
        return (p.ExitCode, (saida.IsCompletedSuccessfully ? saida.Result : "") + (erro.IsCompletedSuccessfully ? erro.Result : ""));
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

    /// <summary>Um mutex nomeado com DACL que só o SISTEMA abre: o caixa elevado visto por um usuário comum.</summary>
    private static SafeWaitHandle? MutexSoDoSistema(string nome, out int erro)
    {
        erro = 0;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:(A;;GA;;;SY)", 1, out var sd, out _))
        {
            erro = Marshal.GetLastWin32Error();
            return null;
        }
        try
        {
            var sa = new SecurityAttributes { nLength = Marshal.SizeOf<SecurityAttributes>(), lpSecurityDescriptor = sd };
            var h = CreateMutexW(ref sa, false, nome);
            erro = Marshal.GetLastWin32Error();
            if (h.IsInvalid) { h.Dispose(); return null; }
            return h;
        }
        finally { LocalFree(sd); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateMutexW(ref SecurityAttributes sa, [MarshalAs(UnmanagedType.Bool)] bool inicial, string nome);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revisao, out IntPtr sd, out uint tamanho);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr h);

    /// <summary>
    /// Duas cobranças VIVAS, do jeito que ficam com o cliente no pinpad: uma do PayGo
    /// já 'aguardando' resposta e uma intenção do ControlPay ainda 'criando'. `criado_em`
    /// é AGORA — o que, para um processo que nasce depois, é sempre "antes do boot".
    /// </summary>
    private static void SemearCobrancasVivas(string arquivo)
    {
        using var cx = Banco.Abrir(arquivo);
        var agora = DateTime.Now.ToString("o");
        cx.Execute("""
            INSERT INTO tef_transacao (id, charge_id, identificacao, tipo, valor_cent, parcelas,
                                       situacao, provedor, criado_em, atualizado_em)
            VALUES ('t-paygo', 'paygo-viva', '167601', 'credito', 50000, 1, 'aguardando', 'paygo', @Em, @Em),
                   ('t-cpay',  'cpay-viva',  '167602', 'credito', 50000, 1, 'criando',    'controlpay', @Em, @Em)
            """, new { Em = agora });
    }

    private static (string Paygo, string Cpay) Situacoes(string arquivo)
    {
        SqliteConnection.ClearAllPools();
        using var cx = Banco.Abrir(arquivo);
        return (cx.ExecuteScalar<string>("SELECT situacao FROM tef_transacao WHERE id = 't-paygo'") ?? "?",
                cx.ExecuteScalar<string>("SELECT situacao FROM tef_transacao WHERE id = 't-cpay'") ?? "?");
    }

    /// <summary>
    /// Sobe um processo DE VERDADE que se comporta como um Pdv.exe recém-aberto
    /// (modo `--sonda-2a-instancia` do Program.cs). Devolve o código de saída e,
    /// para o diagnóstico, o que ele escreveu.
    /// </summary>
    private static (int Codigo, string Saida) SubirSegundaInstancia(string banco, string trava)
    {
        var exe = Environment.ProcessPath ?? "dotnet";
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // `dotnet Pdv.Testes.dll` em vez do apphost: o .dll entra como 1º argumento.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "");
        psi.ArgumentList.Add("--sonda-2a-instancia");
        psi.ArgumentList.Add(banco);
        psi.ArgumentList.Add(trava);

        using var p = Process.Start(psi)!;
        var saida = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(60_000)) { try { p.Kill(true); } catch { } return (-1, " [a 2ª instância não terminou]"); }
        return (p.ExitCode, saida.Trim().Length == 0 ? "" : " — " + saida.Trim().Replace("\r", "").Replace("\n", " | "));
    }
}
