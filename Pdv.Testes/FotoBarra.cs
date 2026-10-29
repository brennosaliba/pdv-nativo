using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// FOTOGRAFA a barra do Wi-Fi e do som (1.0.58) e as duas janelinhas abertas, com o Wi-Fi e o
/// volume DESTE PC. Banco temporário com a opção marcada: não encosta no caixa. Grava
/// barra-&lt;tema&gt;.png, barra-wifi-&lt;tema&gt;.png e barra-som-&lt;tema&gt;.png na pasta pedida.
///   Pdv.Testes.exe --foto-barra docs/barra-fotos [claro|escuro] [largura]
/// </summary>
public static class FotoBarra
{
    public static int Rodar(string[] args)
    {
        var pasta = args[1];
        var tema = args.Length > 2 ? args[2] : "escuro";
        var largura = args.Length > 3 && int.TryParse(args[3], out var l) ? l : 1024;
        var banco = Path.Combine(Path.GetTempPath(), "foto-barra-" + Guid.NewGuid().ToString("N") + ".db");
        Banco.Migrar(banco);
        using (var cx = Banco.Abrir(banco)) Vendas.GravarConfig(cx, BarraDoWindows.Chave, "1");
        Banco.CaminhoForcado = banco;
        var codigo = 1;
        var t = new Thread(() =>
        {
            try { codigo = Fotografar(pasta, tema, largura); }
            catch (Exception ex) { Console.Error.WriteLine("foto-barra: " + ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Banco.CaminhoForcado = null;
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(banco); } catch { }
        return codigo;
    }

    private static int Fotografar(string pasta, string tema, int largura)
    {
        static ResourceDictionary Dic(string caminho) => new()
        {
            Source = new Uri($"pack://application:,,,/Pdv;component/{caminho}", UriKind.Absolute),
        };
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(Dic(tema == "claro" ? "Temas/Claro.xaml" : "Temas/Escuro.xaml"));
        app.Resources.MergedDictionaries.Add(Dic("Estilos.xaml"));

        var barra = new Pdv.Telas.BarraWindows();
        var janela = new Window
        {
            Title = "foto-barra", Width = largura, Height = 40, SizeToContent = SizeToContent.Manual,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 600,
            ShowInTaskbar = false, ShowActivated = false, Content = barra,
        };
        janela.SetResourceReference(Window.BackgroundProperty, "Fundo");
        const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;
        var tipo = typeof(Pdv.Telas.BarraWindows);
        var codigo = 1;
        var etapa = 0;
        var relogio = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        relogio.Tick += (_, _) =>
        {
            etapa++;
            try
            {
                switch (etapa)
                {
                    case 1:
                        Console.WriteLine($"barra visível: {barra.Visibility}, altura {barra.ActualHeight}");
                        Salvar(Path.Combine(pasta, $"barra-{tema}.png"), barra);
                        tipo.GetMethod("AbrirWifi", P)!.Invoke(barra, null);
                        break;
                    case 4:   // a busca de redes leva uns 3,5 s
                        Salvar(Path.Combine(pasta, $"barra-wifi-{tema}.png"), (FrameworkElement)((Popup)tipo.GetField("_popWifi", P)!.GetValue(barra)!).Child);
                        tipo.GetMethod("AbrirSom", P)!.Invoke(barra, null);
                        break;
                    case 5:
                        Salvar(Path.Combine(pasta, $"barra-som-{tema}.png"), (FrameworkElement)((Popup)tipo.GetField("_popSom", P)!.GetValue(barra)!).Child);
                        codigo = 0;
                        relogio.Stop();
                        janela.Close();
                        app.Shutdown();
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("foto-barra: " + ex);
                relogio.Stop();
                janela.Close();
                app.Shutdown();
            }
        };
        janela.Show();
        relogio.Start();
        app.Run();
        return codigo;
    }

    private static void Salvar(string saida, FrameworkElement alvo)
    {
        var w = Math.Max(1, (int)Math.Ceiling(alvo.ActualWidth + alvo.Margin.Left + alvo.Margin.Right));
        var h = Math.Max(1, (int)Math.Ceiling(alvo.ActualHeight + alvo.Margin.Top + alvo.Margin.Bottom));
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.Resources["Fundo"], null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(alvo), null, new Rect(alvo.Margin.Left, alvo.Margin.Top, alvo.ActualWidth, alvo.ActualHeight));
        }
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(saida))!);
        using var fs = File.Create(saida);
        enc.Save(fs);
        Console.WriteLine("gravou " + saida);
    }
}
