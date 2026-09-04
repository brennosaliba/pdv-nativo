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
/// Uso: Pdv.Testes.exe --foto-kds saida.png largura altura [claro|escuro] [--detalhe N] [--nuvem]
///
///   --detalhe N  abre o DETALHE do pedido #N por cima do quadro antes da foto
///                (04/09: é a prova visual do painel a 1024x768 sem cortar item).
///   --nuvem      junto com --detalhe: finge a resposta da RPC de detalhe (localizador,
///                código de coleta, observação do pedido, agrupado com) para as seções
///                da nuvem saírem na foto. Sem ele, elas ficam de fora — que é o que
///                a loja vê quando a nuvem não responde.
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
        var tema = "claro";
        string? detalhe = null;
        var comNuvem = false;
        for (var i = 4; i < args.Length; i++)
        {
            if (args[i] == "--detalhe" && i + 1 < args.Length) detalhe = args[++i];
            else if (args[i] == "--nuvem") comNuvem = true;
            else if (args[i] is "claro" or "escuro") tema = args[i];
        }

        var fixture = Path.Combine(Path.GetTempPath(), "foto-kds-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        Banco.Migrar(fixture);
        Banco.CaminhoForcado = fixture;
        Semear();

        // O complemento FABRICADO da nuvem. O próprio número vai na lista de propósito:
        // o Gestor manda assim, e a foto tem que provar que ele não aparece.
        var complemento = detalhe is not null && comNuvem
            ? new DetalheNuvem("ref-" + detalhe, "3121 4455", "0807",
                               "Deixar na portaria e ligar quando chegar", null, "SCHEDULED", "wk-opaco",
                               new[] { "9002", "3340", detalhe })
            : null;

        var codigo = 1;
        var t = new Thread(() =>
        {
            try { codigo = Fotografar(saida, w, h, tema, detalhe, complemento); }
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
                  string? cliente, object[] itens, bool retirada = false,
                  DateTime? agendadoPara = null, DateTime? agendadoAte = null, string? preparoAte = null)
        {
            var id = Guid.NewGuid().ToString();
            cx.Execute(
                @"INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json, status,
                                          criado_em, preparo_em, pronto_em, impresso_em, retirada,
                                          agendado, agendado_para, agendado_ate, preparo_ate)
                  VALUES (@id, @o, @r, @n, @c, @j, @s, @t, @pe, @pr, @im, @ret, @ag, @ap, @aa, @pa)",
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
                    ag = agendadoPara is null ? 0 : 1,
                    ap = agendadoPara?.ToString("o"),
                    aa = agendadoAte?.ToString("o"),
                    pa = preparoAte,
                });
        }

        static object Item(string nome, int qtd, string? obs = null, string[]? escolhas = null)
            => new { Descricao = nome, Qtd = qtd * 1000, Observacao = obs, Escolhas = escolhas };

        // NA FILA — o AGENDADO de retirada, com tudo que o detalhe sabe mostrar
        // (combo com sabores, observação por item, hora marcada). É o card que o
        // --detalhe fotografa com --nuvem: uma foto só com todas as seções.
        Card("3788", "ifood", Kds.Recebido, 95, "Ana Beatriz Souza", new[]
        {
            Item("Combo Box 4un", 1, "sem granulado no Homer", new[]
            {
                "2x Donut Homer", "1x Donut Morango c/ Ninho", "1x Donut Calabresa",
            }),
            Item("Donut Ninho com Nutella", 2, "embalar separado"),
            Item("Café Coado 300ml", 1),
        }, retirada: true, agendadoPara: agora.AddMinutes(85), agendadoAte: agora.AddMinutes(115));

        Card("5610", "ifood", Kds.Recebido, 3, "Marcela Prado", new[]
        {
            Item("Donut Ninho com Nutella", 2),
            Item("Cookie Duplo Chocolate", 1, "sem castanha"),
        });

        // FAZENDO — o par da foto. O 5077 tem prazo: é o de dez itens, o que prova
        // que a lista do detalhe ROLA a 1024x768 em vez de cortar.
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
        }, preparoAte: agora.AddMinutes(11).ToString("o"));
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

    private static int Fotografar(string saida, int w, int h, string tema,
                                  string? detalhe, DetalheNuvem? complemento)
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
        var abriuDetalhe = false;
        var relogio = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        relogio.Tick += (_, _) =>
        {
            etapa++;
            // 3 batidas: a primeira puxada da nuvem morta tem que terminar (ela
            // repinta o quadro no finally) antes de a foto sair.
            if (etapa < 3) return;
            // O detalhe abre DEPOIS do quadro pronto e ganha uma batida inteira para
            // o layout assentar (a lista decide se rola só depois de medida).
            if (detalhe is not null && !abriuDetalhe)
            {
                abriuDetalhe = true;
                if (!tela.AbrirDetalhe(detalhe, complemento))
                    erros.Add($"o pedido #{detalhe} não está no quadro fabricado");
                return;
            }
            relogio.Stop();
            try
            {
                var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                bmp.Render((Visual)janela.Content);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                Directory.CreateDirectory(Path.GetDirectoryName(saida)!);
                using (var fs = File.Create(saida)) enc.Save(fs);
                Console.WriteLine($"foto-kds: {saida} ({w}x{h}, tema {tema}" +
                                  (detalhe is null ? ")" : $", detalhe #{detalhe}{(complemento is null ? "" : " com nuvem")})"));
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
