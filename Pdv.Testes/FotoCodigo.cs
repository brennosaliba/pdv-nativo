using System.IO;
using System.Windows;
using System.Windows.Threading;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// FOTOGRAFA a tela do código do autenticador do dono (Telas/PedirCodigo.cs) por
/// cima da tela de venda, numa resolução dada, sem monitor daquele tamanho.
///
/// Uso: Pdv.Testes.exe --foto-codigo saida.png largura altura [claro|escuro] [aviso] [gerente]
///
/// O `aviso` é o texto vermelho de "Código inválido. Tente de novo." (a segunda
/// tentativa). Sem ele, é a tela da primeira tentativa. `-` = sem aviso.
/// `gerente` (05/09, qualquer posição) fotografa a tela com o rótulo do autenticador
/// do gerente (promoção com 2FA de gerente).
///
/// Mesma segurança do --foto-venda: cópia do banco do caixa, nuvem apontada para
/// endereço morto, nada é gravado no banco de verdade. O diálogo é modal: a foto
/// sai por um timer armado ANTES de abrir, que dispara dentro do laço do
/// ShowDialog, fotografa as duas janelas compostas e fecha o diálogo.
/// </summary>
public static class FotoCodigo
{
    public static int Rodar(string[] args)
    {
        var gerente = args.Any(a => a.Equals("gerente", StringComparison.OrdinalIgnoreCase));
        args = args.Where(a => !a.Equals("gerente", StringComparison.OrdinalIgnoreCase)).ToArray();
        var saida = Path.GetFullPath(args[1]);
        var w = int.Parse(args[2]);
        var h = int.Parse(args[3]);
        var tema = args.Length > 4 ? args[4] : "claro";
        var aviso = args.Length > 5 && args[5] != "-" ? args[5] : null;
        var nivel = gerente ? Autorizacao.NivelGerente : Autorizacao.NivelDono;

        var origem = Banco.Arquivo;
        if (!File.Exists(origem))
        {
            Console.Error.WriteLine($"foto-codigo: não achei o banco do caixa em {origem}");
            return 2;
        }
        var fixture = Path.Combine(Path.GetTempPath(), "foto-codigo-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        File.Copy(origem, fixture);
        Banco.CaminhoForcado = fixture;
        string opId, opNome;
        using (var cx = Banco.Abrir())
        {
            cx.Execute("UPDATE terminal SET api_base = 'http://127.0.0.1:9'");
            var op = cx.QueryFirstOrDefault<(string? id, string? nome)>("SELECT id, nome FROM operador ORDER BY nome LIMIT 1");
            opId = op.id ?? "op-foto";
            opNome = op.nome ?? "Teste";
        }
        var operador = new Operador(opId, opNome, "operador");
        var sessao = new Sessao("sessao-foto", Caixa.DiaOperacional(), opId, opNome,
            DateTime.Now.Date.AddHours(8), Dinheiro.Zero);

        var codigo = 1;
        var t = new Thread(() =>
        {
            try { codigo = Fotografar(saida, w, h, tema, aviso, nivel, operador, sessao); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("foto-codigo: " + ex);
                codigo = 1;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { File.Delete(fixture); } catch { /* temporário */ }
        return codigo;
    }

    private static int Fotografar(string saida, int w, int h, string tema, string? aviso, string nivel,
                                  Operador operador, Sessao sessao)
    {
        static ResourceDictionary Dic(string caminho) => new()
        {
            Source = new Uri($"pack://application:,,,/Pdv;component/{caminho}", UriKind.Absolute),
        };
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(Dic(tema == "claro" ? "Temas/Claro.xaml" : "Temas/Escuro.xaml"));
        app.Resources.MergedDictionaries.Add(Dic("Estilos.xaml"));

        var erros = new List<string>();
        app.DispatcherUnhandledException += (_, e) =>
        {
            erros.Add(e.Exception.GetType().Name + ": " + e.Exception.Message);
            e.Handled = true;
        };

        var tela = new Pdv.Telas.Venda(operador, sessao);
        var janela = new Window
        {
            Title = "foto-codigo", Width = w, Height = h,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0,
            ShowInTaskbar = false, ShowActivated = false, Content = tela,
        };

        var codigo = 1;
        var salvo = false;
        var abrir = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        abrir.Tick += (_, _) =>
        {
            abrir.Stop();
            try
            {
                var foto = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                foto.Tick += (_, _) =>
                {
                    foto.Stop();
                    var dialogo = Application.Current.Windows.OfType<Window>()
                        .FirstOrDefault(x => x != janela && x.Owner == janela && x.IsVisible);
                    if (dialogo is null) erros.Add("a tela do código não abriu");
                    FotoVenda.Salvar(saida, w, h, janela, dialogo);
                    salvo = true;
                    Console.WriteLine($"foto-codigo: {saida} ({w}x{h}, tema {tema}, nivel {nivel}, aviso {(aviso is null ? "nenhum" : "'" + aviso + "'")})");
                    dialogo?.Close();
                };
                foto.Start();
                // Bloqueia no ShowDialog até o timer acima fechar o diálogo.
                var digitado = Pdv.Telas.PedirCodigo.Mostrar(janela, aviso, nivel);
                if (digitado is not null) erros.Add("o diálogo devolveu um código sem ninguém digitar");
                foreach (var e in erros.Distinct()) Console.WriteLine("  aviso: " + e);
                codigo = salvo ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("foto-codigo: " + ex);
                codigo = 1;
            }
            janela.Close();
            app.Shutdown();
        };
        janela.Loaded += (_, _) => abrir.Start();
        var limite = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        limite.Tick += (_, _) => { Console.Error.WriteLine("foto-codigo: tempo esgotado"); codigo = 3; app.Shutdown(); };
        limite.Start();
        janela.Show();
        app.Run();
        return codigo;
    }
}
