using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// FOTOGRAFA o quadro do KDS numa resolução dada, sem monitor daquele tamanho.
///
/// Irmão do <see cref="FotoVenda"/>, e pelo mesmo motivo: em 04/09 o dono olhou a
/// 0.5.3 na Savassi e reclamou de três coisas de LAYOUT (card sem desfazer à vista,
/// conteúdo boiando longe do topo, item quebrando em duas linhas). Nada disso
/// aparece no monitor de quem programa, porque o quadro divide a tela em três e a
/// largura do card depende da resolução — a 1024x768 cada card fica com ~150 px.
/// Sem foto, "melhorei o layout" é opinião.
///
/// Uso: Pdv.Testes.exe --foto-kds saida.png largura altura [claro|escuro]
///
/// Segurança: NÃO usa o banco do caixa. Monta um SQLite temporário do zero com um
/// quadro fabricado (os mesmos cards da foto que o dono mandou) e a nuvem apontada
/// para um endereço morto. Nada é lido nem gravado na operação real.
/// </summary>
public static class FotoKds
{
    public static int Rodar(string[] args)
    {
        var saida = Path.GetFullPath(args[1]);
        var w = int.Parse(args[2]);
        var h = int.Parse(args[3]);
        var tema = args.Length > 4 ? args[4] : "claro";

        var fixture = Path.Combine(Path.GetTempPath(), "foto-kds-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        Banco.Migrar(fixture);
        Banco.CaminhoForcado = fixture;
        Semear();

        var codigo = 1;
        var t = new Thread(() =>
        {
            try { codigo = Fotografar(saida, w, h, tema); }
            catch (Exception ex) { Console.Error.WriteLine("foto-kds: " + ex); codigo = 1; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { File.Delete(fixture); } catch { /* temporário */ }
        return codigo;
    }

    /// <summary>
    /// O quadro da foto do dono (04/09), reconstruído: FAZENDO com o #5077 de dez
    /// itens ao lado do #9507 de um combo com quatro sabores — o par que mostrava os
    /// dois rodapés em alturas diferentes. Mais um card em cada uma das outras
    /// colunas, para os outros rodapés aparecerem na mesma foto.
    /// </summary>
    private static void Semear()
    {
        using var cx = Banco.Abrir();
        // a comanda NÃO pode sair de verdade na máquina de quem tira a foto
        Vendas.GravarConfig(cx, Impressoes.Chave(Impressoes.Comanda),
                            Impressoes.Texto(PoliticaImpressao.Perguntar));
        cx.Execute("UPDATE terminal SET api_base = 'http://127.0.0.1:9'");

        var agora = DateTime.Now;
        void Card(string numero, string origem, string status, int minutosAtras,
                  string? cliente, object[] itens, bool retirada = false)
        {
            var id = Guid.NewGuid().ToString();
            cx.Execute(
                @"INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json, status,
                                          criado_em, preparo_em, pronto_em, impresso_em, retirada)
                  VALUES (@id, @o, @r, @n, @c, @j, @s, @t, @pe, @pr, @im, @ret)",
                new
                {
                    id, o = origem, r = "ref-" + numero, n = numero, c = cliente,
                    j = System.Text.Json.JsonSerializer.Serialize(itens),
                    s = status,
                    t = agora.AddMinutes(-minutosAtras).ToString("o"),
                    pe = status == Kds.Recebido ? null : agora.AddMinutes(-minutosAtras + 2).ToString("o"),
                    pr = status == Kds.Pronto ? agora.AddMinutes(-1).ToString("o") : null,
                    // já impresso: o timer da tela não pode disparar papel na foto
                    im = agora.ToString("o"),
                    ret = retirada ? 1 : 0,
                });
        }

        static object Item(string nome, int qtd, string? obs = null, string[]? escolhas = null)
            => new { Descricao = nome, Qtd = qtd * 1000, Observacao = obs, Escolhas = escolhas };

        // NA FILA
        Card("5610", "ifood", Kds.Recebido, 3, "Marcela Prado", new[]
        {
            Item("Donut Ninho com Nutella", 2),
            Item("Cookie Duplo Chocolate", 1, "sem castanha"),
        });

        // FAZENDO — o par da foto
        Card("5077", "ifood", Kds.Preparando, 14, "Rafael Andrade", new[]
        {
            Item("Donut Ovomaltine", 1),
            Item("Tortinha de Frango com Catupiry", 1),
            Item("Donut Banoffee", 3),
            Item("Donut Ninho", 1),
            Item("Cookie Duplo", 2),
            Item("Pão de Queijo Recheado", 1),
            Item("Donut Doce de Leite", 1),
            Item("Coxinha de Frango", 2),
            Item("Suco de Laranja 500ml", 1),
            Item("Café Coado 300ml", 1),
        });
        Card("9507", "ifood", Kds.Preparando, 9, "Juliana Ferreira", new[]
        {
            Item("Combo 1 Cookies - 4 unidades", 1, null, new[]
            {
                "Clássicos: 1x Cookie Duplo Chocolate",
                "Clássicos: 1x Cookie Red Velvet",
                "Premium: 1x Cookie Pistache",
                "Premium: 1x Cookie Ninho com Nutella",
            }),
        });

        // PRONTO
        Card("4218", "ifood", Kds.Pronto, 21, "Bruno Carvalho", new[]
        {
            Item("Donut Ovomaltine", 2),
            Item("Tortinha de Frango com Catupiry", 1),
        });
    }

    private static int Fotografar(string saida, int w, int h, string tema)
    {
        // mesmos dicionários do Pdv.exe, na mesma ordem (contrato: [0] paleta, [1] estilos)
        static ResourceDictionary Dic(string caminho) => new()
        {
            Source = new Uri($"pack://application:,,,/Pdv;component/{caminho}", UriKind.Absolute),
        };
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(Dic(tema == "claro" ? "Temas/Claro.xaml" : "Temas/Escuro.xaml"));
        app.Resources.MergedDictionaries.Add(Dic("Estilos.xaml"));

        var erros = new List<string>();
        app.DispatcherUnhandledException += (_, e)
            => { erros.Add(e.Exception.GetType().Name + ": " + e.Exception.Message); e.Handled = true; };

        var tela = new Pdv.Telas.Kds("foto");
        var janela = new Window
        {
            Title = "foto-kds", Width = w, Height = h,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0,
            ShowInTaskbar = false, ShowActivated = false, Content = tela,
        };

        var codigo = 1;
        var etapa = 0;
        var relogio = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        relogio.Tick += (_, _) =>
        {
            etapa++;
            // 3 batidas: a primeira puxada da nuvem morta tem que terminar (ela
            // repinta o quadro no finally) antes de a foto sair.
            if (etapa < 3) return;
            relogio.Stop();
            try
            {
                var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                bmp.Render((Visual)janela.Content);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                Directory.CreateDirectory(Path.GetDirectoryName(saida)!);
                using (var fs = File.Create(saida)) enc.Save(fs);
                Console.WriteLine($"foto-kds: {saida} ({w}x{h}, tema {tema})");
                foreach (var e in erros.Distinct()) Console.WriteLine("  aviso: " + e);
                codigo = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("foto-kds: " + ex); codigo = 1; }
            janela.Close();
            app.Shutdown();
        };
        janela.Loaded += (_, _) => relogio.Start();
        var limite = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        limite.Tick += (_, _) => { Console.Error.WriteLine("foto-kds: tempo esgotado"); codigo = 3; app.Shutdown(); };
        limite.Start();
        janela.Show();
        app.Run();
        return codigo;
    }
}
