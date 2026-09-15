using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// Processos-filho que a bateria sobe para provar coisas que só existem ENTRE processos:
/// o Pdv.App de verdade num modo de linha de comando, um programa preso e um programa que
/// deixa um filho segurando a saída.
/// </summary>
internal static class Sondas
{
    /// <summary>Com esta variável de ambiente em "1", o Pdv.Testes.exe vira o Pdv.App (ver <see cref="RodarApp"/>).</summary>
    public const string VariavelApp = "PDV_SONDA_APP";

    /// <summary>O banco que o Pdv.App da sonda usa, para nunca encostar no pdv.db de verdade desta máquina.</summary>
    public const string VariavelBanco = "PDV_SONDA_APP_BANCO";

    /// <summary>Linha que a sonda escreve quando uma janela WPF nasce no processo.</summary>
    public const string MarcaJanela = "JANELA:";

    /// <summary>O próprio Pdv.Testes como processo-filho, com os argumentos dados.</summary>
    public static ProcessStartInfo Psi(params string[] argumentos)
    {
        var exe = Environment.ProcessPath ?? "dotnet";
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        // `dotnet Pdv.Testes.dll` em vez do apphost: o .dll entra como 1º argumento.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "");
        foreach (var a in argumentos) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>
    /// O Pdv.App DE VERDADE, com os argumentos da linha de comando deste processo. Escreve
    /// <see cref="MarcaJanela"/> se qualquer janela nascer: um modo de ferramenta que abre a
    /// frente de caixa é o defeito da Castelo.
    /// </summary>
    public static int RodarApp()
    {
        var codigo = 99;
        var t = new Thread(() =>
        {
            try
            {
                Banco.CaminhoForcado = Environment.GetEnvironmentVariable(VariavelBanco);
                // O ResourceAssembly NÃO é trocado: o WPF só aceita isso quando não há assembly de
                // entrada gerenciado, e aqui há (Pdv.Testes). O App.xaml carrega por
                // "/Pdv;component/...", então sobe igual. O que não sobe é um StartupUri relativo:
                // ele seria procurado no Pdv.Testes. Por isso a tentativa de abrir a MainWindow pelo
                // StartupUri também conta como janela (ver o tratador abaixo).
                var viu = false;
                void Viu(string como)
                {
                    if (viu) return;
                    viu = true;
                    Console.WriteLine(MarcaJanela + " " + como);
                    Console.Out.Flush();
                }
                EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                    new RoutedEventHandler((s, _) => Viu(s.GetType().Name)));
                var app = new global::Pdv.App();
                // Registrado ANTES do tratador do próprio App (que só nasce no OnStartup): a sonda
                // responde primeiro e sai, sem caixa de mensagem e sem escrever no erros.log real.
                app.DispatcherUnhandledException += (_, e) =>
                {
                    var msg = e.Exception.Message;
                    Console.WriteLine(msg.Contains("mainwindow", StringComparison.OrdinalIgnoreCase)
                        ? MarcaJanela + " o WPF tentou abrir a MainWindow (StartupUri): " + msg
                        : "ERRO DE TELA: " + e.Exception.GetType().Name + ": " + msg);
                    Console.Out.Flush();
                    e.Handled = true;
                    Environment.Exit(97);
                };
                app.InitializeComponent();
                // A MainWindow entra em Application.Windows no construtor, antes de aparecer.
                _ = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Send,
                    (_, _) => { if (app.Windows.Count > 0) Viu(app.Windows[0].GetType().Name); }, app.Dispatcher);
                codigo = app.Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine("SONDA FALHOU: " + ex.GetType().Name + ": " + ex.Message);
                codigo = 98;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return codigo;
    }

    /// <summary>Programa preso: dorme e sai sozinho depois de <paramref name="segundos"/>.</summary>
    public static int Presa(string segundos)
    {
        Thread.Sleep(TimeSpan.FromSeconds(int.TryParse(segundos, out var s) ? s : 30));
        return 0;
    }

    /// <summary>
    /// Programa que dá certo e sai com 0, mas antes sobe um filho que HERDA a saída dele e
    /// continua vivo. É o que um exe faz quando abre WebView2, Spotify ou o agente: quem lê a
    /// saída até o fim fica esperando o neto.
    /// </summary>
    public static int Neto(string segundos, string arquivoPid)
    {
        var psi = Psi("--sonda-presa", segundos);
        // Redirecionar só a ENTRADA liga STARTF_USESTDHANDLES, e aí o .NET entrega ao neto a
        // saída e o erro DESTE processo, que são o cano de quem nos chamou.
        psi.RedirectStandardInput = true;
        using var neto = Process.Start(psi)!;
        File.WriteAllText(arquivoPid, neto.Id.ToString());
        Console.WriteLine("ok: abri e deixei um filho");
        return 0;
    }
}
