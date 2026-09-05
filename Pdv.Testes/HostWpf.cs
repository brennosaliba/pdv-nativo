using System.Windows;

namespace Pdv.Testes;

/// <summary>
/// UM Application WPF para a bateria inteira. O WPF nao deixa criar um segundo
/// Application no mesmo processo (nem depois do Shutdown do primeiro): a suite que
/// vinha depois do TestesPos morria com "Nao e possivel criar mais de uma instancia
/// de System.Windows.Application". Aqui o Application nasce uma vez numa thread STA
/// de fundo, com os MESMOS dicionarios do App.xaml ([0] paleta, [1] estilos), e cada
/// suite de tela roda os seus passos dentro dele por Dispatcher.Invoke. Dialogos
/// modais (ShowDialog) e DispatcherTimer funcionam normalmente ali: e a mesma
/// thread, o mesmo laco de mensagens.
/// </summary>
internal static class HostWpf
{
    private static Application? _app;
    private static Exception? _erroInicial;
    private static readonly ManualResetEventSlim _pronto = new();
    private static readonly object _trava = new();

    /// <summary>Roda `passos` na thread do Application (criando-o na primeira chamada). Excecao vem de volta para quem chamou.</summary>
    public static void Executar(Action passos)
    {
        Iniciar();
        if (_erroInicial is not null) throw _erroInicial;
        Exception? erro = null;
        _app!.Dispatcher.Invoke(() =>
        {
            try { passos(); }
            catch (Exception ex) { erro = ex; }
        });
        if (erro is not null) throw erro;
    }

    private static void Iniciar()
    {
        lock (_trava)
        {
            if (_app is not null) return;
            var t = new Thread(() =>
            {
                try
                {
                    static ResourceDictionary Dic(string caminho) => new()
                    {
                        Source = new Uri($"pack://application:,,,/Pdv;component/{caminho}", UriKind.Absolute),
                    };
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.Resources.MergedDictionaries.Add(Dic("Temas/Claro.xaml"));
                    app.Resources.MergedDictionaries.Add(Dic("Estilos.xaml"));
                    _app = app;
                    app.Startup += (_, _) => _pronto.Set();
                    app.Run();
                }
                catch (Exception ex) { _erroInicial = ex; _pronto.Set(); }
            }) { IsBackground = true, Name = "HostWpf" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
        _pronto.Wait();
    }
}
