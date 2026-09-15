using System.IO;
using System.Windows;
using Dapper;
using Pdv.Nucleo;

namespace Pdv;

public partial class App : Application
{
    /// <summary>
    /// Modos de linha de comando, para instalação e suporte. Rodam e saem — nenhum
    /// deles abre a frente de caixa.
    ///
    ///   Pdv.exe --cupom-teste [arquivo.png]   desenha o cupom de exemplo numa imagem
    ///   Pdv.exe --imprimir-teste ["Impressora"] manda o cupom de exemplo para o papel
    ///
    /// Os dois existem para separar problema de LAYOUT de problema de EMISSÃO: cupom
    /// torto descoberto junto com a primeira nota real vira dois problemas confundidos
    /// num só, e ainda gasta numeração fiscal para descobrir.
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        // ── O CAIXA NÃO MORRE POR UMA EXCEÇÃO DE TELA ───────────────────────
        //
        // 09/09/2026: o dono tocou em "exportar logs" no menu do PayGo e o exe
        // FECHOU. Não havia guarda nenhuma: qualquer exceção não tratada na thread
        // da tela derrubava o processo. No meio de uma homologação isso custa a
        // sequência; no balcão, custa a venda e o cliente esperando.
        //
        // Continuar vivo com um erro à vista é melhor que morrer calado. O dinheiro
        // já tem defesa própria: a venda grava em transação, o TEF confirma ou
        // desfaz, e o rascunho traz a comanda de volta. O que faltava era a tela
        // não levar tudo junto quando ela mesma tropeça.
        // 09/09/2026: a primeira versao deste guarda auditava com conexao NULA (a
        // chamada lancava e o catch engolia), entao o erro que o dono viu na Configuracao
        // ficou sem rastro nenhum. E o PDV caiu MESMO com o dialogo, porque so a thread da
        // tela estava coberta. Agora: pilha completa em erros.log, auditoria com conexao
        // aberta, e as excecoes de thread de fundo tambem registradas antes de derrubar.
        static void Registrar(string origem, Exception ex)
        {
            try
            {
                var pasta = Nucleo.Banco.Pasta;
                System.IO.Directory.CreateDirectory(pasta);
                System.IO.File.AppendAllText(System.IO.Path.Combine(pasta, "erros.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + origem + "] " + ex
                    + Environment.NewLine + Environment.NewLine);
            }
            catch { }
            try
            {
                var primeira = (ex.StackTrace ?? "").Split(Environment.NewLine).FirstOrDefault()?.Trim() ?? "";
                using var cx = Nucleo.Banco.Abrir();
                Nucleo.Caixa.Auditar(cx, null, "erro_de_tela", null, null,
                    origem + ": " + ex.GetType().Name + ": " + ex.Message + " @ " + primeira);
            }
            catch { }
        }
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            Registrar("fundo", a.ExceptionObject as Exception ?? new Exception(a.ExceptionObject?.ToString() ?? "?"));
            // MODO QUIOSQUE: o PDV e o shell do Windows; morrendo, a tela ficaria preta.
            // Abre o Explorer antes de cair, para o dono ter por onde mexer.
            if (a.IsTerminating) { try { if (Quiosque.Ligado) Quiosque.AbrirExplorer(); } catch { } }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Registrar("task", a.Exception);
            a.SetObserved();
        };
        // TECLA QUE O WINDOWS DEIXOU CAIR NAO E MOTIVO PARA CAIXA DE AVISO (09/09/2026).
        // NullReferenceException em System.Windows.Input.TextServicesContext.Keystroke: o
        // QueryInterface do gerenciador de texto do Windows falhou no meio de uma tecla
        // (WebView2 e teclado virtual tambem provocam, dotnet/wpf#6463). O WPF lanca ANTES
        // de qualquer handler rodar: a unica consequencia e essa tecla se perder. Uma
        // MessageBox aqui abre outro laco modal dentro da tecla meio processada, por cima
        // do dialogo do TEF, e foi o que o dono viu como "erro no passo 18". Fica no log.
        static bool TeclaPerdidaDoWindows(Exception ex)
            => ex is NullReferenceException
               && (ex.StackTrace ?? "").Contains("TextServicesContext.Keystroke", StringComparison.Ordinal);
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            Registrar("tela", args.Exception);
            if (TeclaPerdidaDoWindows(args.Exception)) return;
            try
            {
                MessageBox.Show(
                    "Alguma coisa falhou nesta tela e eu segurei o caixa de pe."
                    + Environment.NewLine + Environment.NewLine + args.Exception.Message
                    + Environment.NewLine + Environment.NewLine
                    + "A venda e o turno continuam como estavam. O detalhe ficou em erros.log.",
                    "O caixa continua aberto", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
        };

        var args = e.Args;
        // MODO DE FERRAMENTA (14/09/2026, Castelo). O instalador roda `Pdv.exe --cupom-teste` para
        // conferir se o caixa abre, com o caixa da loja às vezes já aberto. Este modo não pega a
        // trava, não é barrado por ela e NUNCA abre a frente de caixa. Até aqui abria: o App.xaml
        // tinha StartupUri, e o WPF construía a MainWindow no primeiro await abaixo. Deu dois
        // Pdv.exe na loja, o segundo sem trava e disputando o pinpad. A regra é LinhaDeComando.
        var modo = LinhaDeComando.Modo(args);
        if (!LinhaDeComando.AbreOCaixa(modo))
        {
            Banco.Migrar();
            using var cx = Banco.Abrir();
            var t = cx.QueryFirstOrDefault("SELECT loja_nome, cnpj, serie_nfce FROM terminal LIMIT 1");
            var dados = Servicos.CupomDeExemplo(
                (t?.loja_nome as string) ?? "",
                (t?.cnpj as string) ?? "",
                t is null ? 0 : Convert.ToInt32(t.serie_nfce));

            string? erro;
            string onde;
            if (modo == ModoDoExe.CupomTeste)
            {
                onde = args.Length > 1 ? args[1]
                    : Path.Combine(Path.GetTempPath(), "cupom-teste.png");
                erro = await Impressao.PreVisualizarAsync(dados, onde);
            }
            else
            {
                onde = args.Length > 1 ? args[1] : (Impressao.ImpressoraPadrao() ?? "(padrão)");
                erro = await Impressao.ImprimirAsync(dados, args.Length > 1 ? args[1] : null);
            }

            // console anexado: WinExe não tem stdout próprio, mas herda o do terminal
            // que o chamou — sem isso o comando roda em silêncio e ninguém sabe o resultado
            Console.WriteLine(erro is null ? $"ok: {onde}" : $"FALHOU: {erro}");
            Console.Out.Flush();
            // Environment.Exit, e não Shutdown: o OnExit é do CAIXA (marca atividade para a
            // retomada sem login, solta a trava, encerra o TEF) e nada disso é da ferramenta.
            Environment.Exit(erro is null ? 0 : 1);
            return;
        }

        // UM PDV POR MÁQUINA — antes de tocar no banco e antes do religamento do TEF.
        //
        // O pinpad fica com o cliente, a tela parece parada, o operador acha que "travou"
        // e clica de novo no ícone. Sem esta trava sobe um 2º Pdv.exe, que attacha no
        // mesmo turno aberto (MainWindow.Roteia) e roda o religamento do TEF. E o
        // religamento chama de abandonada toda cobrança nascida antes do START DESTE
        // PROCESSO — ou seja, a cobrança que está no pinpad da 1ª instância. Ela vira
        // 'orfa' com "confira no PayGo e estorne se aprovou": o operador estorna dinheiro
        // que era da loja. Nenhuma defesa de dentro do processo cobre isso.
        //
        // ⚠️ SAI COM Environment.Exit, NÃO com Shutdown(): medido num app de teste, o WPF
        // constrói o StartupUri (MainWindow) DEPOIS de OnStartup mesmo com Shutdown() já
        // chamado — Shutdown só posta a saída no dispatcher. Com Shutdown, a 2ª instância
        // ainda entraria no MainWindow.Roteia, abriria o banco do caixa e attacharia no
        // turno aberto antes de morrer. Que é exatamente o que a trava existe para impedir.
        _trava = InstanciaUnica.Tentar();
        if (_trava is null)
        {
            TrazerPdvAbertoParaFrente();
            Environment.Exit(0);
            return;
        }

        // Tema antes da primeira janela: se a config manda claro (ou o horário
        // manda, no modo auto), o operador não vê a tela piscar de escuro pra
        // claro na abertura. Config ilegível não derruba o caixa — fica o escuro.
        try
        {
            Banco.Migrar();
            using var cx = Banco.Abrir();
            Aparencia.Aplicar(Aparencia.Resolver(cx));
            // Bobina escolhida na configuracao. Sem esta linha o valor gravado so
            // valeria enquanto a tela de config estivesse aberta: reabrir o PDV
            // voltava para 80 mm, e uma loja de 58 mm imprimiria cortado sem
            // ninguem entender por que "estava certo ontem".
            Impressao.PapelMm = Vendas.Config(cx, "papel_mm");
            // SPOTIFY ESCONDIDO (12/09/2026): com a opção ligada, o PDV abre o app do Spotify
            // e o mantém minimizado, para este PC aparecer na lista de aparelhos da Música.
            // Falha aqui não derruba nada: a janela da música diz se o caixa está na lista.
            if (Vendas.Config(cx, SpotifyNoCaixa.Chave) == "1") SpotifyNoCaixa.Iniciar();
        }
        catch { /* banco indisponível aqui vira erro de verdade logo adiante, com mensagem melhor */ }

        // O emissor fiscal local nasce com o PDV e morre com ele. Janela solta de
        // terminal, alguém fecha — e a loja fica sem nota sem ninguém saber por quê.
        Agente.IniciarVigia();

        // TEF PayGo: transação aprovada que ficou sem CNF/NCN (queda de energia, app
        // fechado) é resolvida AQUI, sozinha — venda gravada → confirma; sem venda →
        // desfaz. A spec proíbe deixar o operador decidir o status.
        // Falha AQUI em silêncio foi como uma cobrança de cartão ficou 'aguardando' por horas sem
        // ninguém saber. O religamento continua assíncrono e sem derrubar o caixa — mas deixa rastro.
        _ = Task.Run(async () =>
        {
            try { await Servicos.ResolverPendenciasTefAsync(); }
            catch (Exception ex)
            {
                try
                {
                    using var cxt = Banco.Abrir();
                    Caixa.Auditar(cxt, null, "tef_religamento_falhou", null, null, ex.Message);
                }
                catch { /* nem o banco respondeu: não há mais onde registrar */ }
            }
        });
        base.OnStartup(e);

        // A JANELA DO CAIXA NASCE AQUI, depois da trava (14/09/2026). Era o StartupUri do
        // App.xaml, e o WPF o construía também nos modos de ferramenta, sem trava nenhuma.
        try
        {
            var janela = new MainWindow();
            MainWindow = janela;
            janela.Show();
        }
        catch (Exception ex)
        {
            Registrar("abertura", ex);
            try
            {
                MessageBox.Show("O caixa não abriu nesta máquina: " + ex.Message
                    + Environment.NewLine + Environment.NewLine
                    + "Feche e abra de novo. Se repetir, chame o suporte (o detalhe ficou em erros.log).",
                    "O caixa não abriu", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            // Sem janela o processo ficaria vivo, invisível e com a trava: o próximo clique no
            // ícone ouviria "já está aberto" de um caixa que ninguém vê.
            Environment.Exit(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Fechou (para atualizar, ou pelo botão): a última atividade é agora, e a retomada
        // sem login vale nos 15 min seguintes.
        if (_trava is not null)
            try { using var cx = Banco.Abrir(); Caixa.MarcarAtividade(cx); } catch { }
        Agente.Encerrar();
        _trava?.Dispose();
        base.OnExit(e);
        // POR ÚLTIMO, com tudo o mais já encerrado: PW_End na PGWebLib, se ela foi usada.
        // Medido em 07/09/2026 (4.1.50.24, x86): a DLL iniciada e não encerrada aborta o
        // processo no DLL_PROCESS_DETACH (0xC0000409) depois do Main devolver 0, e o operador
        // vê "o caixa fechou com erro". PW_End é a mesma rotina, chamada com o processo de pé:
        // termina limpa e o detach vira no-op. Fica por último porque é a única chamada nativa
        // daqui; se ela cair, nada da casa ficou por gravar (o banco grava na hora, a fila e
        // a auditoria também).
        Servicos.EncerrarTef();
    }

    /// <summary>
    /// Trava do terminal. Campo estático porque ela tem que viver enquanto o processo
    /// viver: coletada pelo GC, o handle fecha e o mutex some — a 2ª instância entraria.
    /// </summary>
    private static InstanciaUnica? _trava;

    /// <summary>
    /// O 2º clique no ícone não pode virar "não aconteceu nada" — senão o operador
    /// clica mais três vezes. Traz a janela do PDV que já está aberto para a frente;
    /// só quando não acha nenhuma é que fala.
    /// </summary>
    private static void TrazerPdvAbertoParaFrente()
    {
        try
        {
            using var eu = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(eu.ProcessName))
            {
                using (p)
                {
                    if (p.Id == eu.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                    // Só restaura se estiver minimizado: SW_RESTORE numa janela maximizada tira o
                    // caixa da tela cheia.
                    if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, SwRestore);
                    SetForegroundWindow(p.MainWindowHandle);
                    return;
                }
            }
        }
        catch { /* sem permissão de enumerar processos: cai na mensagem */ }

        MessageBox.Show("O PDV já está aberto nesta máquina.", "PDV",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private const int SwRestore = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
