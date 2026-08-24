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
        var args = e.Args;
        if (args.Length > 0 && (args[0] == "--cupom-teste" || args[0] == "--imprimir-teste"))
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
            if (args[0] == "--cupom-teste")
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
            Shutdown(erro is null ? 0 : 1);
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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Agente.Encerrar();
        _trava?.Dispose();
        base.OnExit(e);
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
                    ShowWindow(p.MainWindowHandle, SwRestore);
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
}
