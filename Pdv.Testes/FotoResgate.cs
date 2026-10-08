using System.IO;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// FOTOGRAFA a janela de resgate manual da raspadinha (Telas/ResgateRaspadinha.cs, caixa 1.0.22)
/// nos tres tamanhos de tela da rede, sem monitor daquele tamanho. Irma do FotoVenda e do FotoKds,
/// pela mesma regra da casa: layout novo nao sai no instalador sem as fotos de 1024x768, 1366x768
/// e 1920x1080.
///
/// Uso: Pdv.Testes.exe --foto-resgate pasta [claro|escuro]
///
/// Sai, em cada largura (1024, 1366 e 1920):
///   resgate-conferido-L.png    depois do Conferir: a linha do premio, os codigos vivos da conversa,
///                              os pedidos abertos (#6066 sugerido e marcado, #6283, Balcao), os 11
///                              chips de sabor do Cookie Super Premium e o "Avisar o cliente no chat"
///   resgate-rolado-L.png       SO se o miolo precisar rolar (o que a regra da casa proibe: vira aviso e
///                              saida 1): rolado ate o fim, o que ficava escondido atras da barra
///   resgate-sabor-L.png        um sabor tocado (Red Velvet): o chip marcado e o Resgatar habilitado
///   resgate-confirmacao-L.png  a pergunta de confirmacao (Dialogo.Confirmar) por cima da janela
///   resgate-resgatado-L.png    depois do resgate: a linha do resultado, Fechar e Desfazer
///   resgate-donut15-L.png      o pior caso: "1 Donut Super Premium" com as 15 opcoes do SQL 156, os
///                              mesmos pedidos e o balcao, logo depois do Conferir
/// Com "escuro", os mesmos nomes com o sufixo -escuro.
/// Revisao 08/10: a primeira rodada (janela de 560, linha do resultado dentro da rolagem) provou que a
/// 1024x768 o sabor ficava escondido e o "Resgatado" saia fora da tela; a janela foi refeita e estas
/// fotos sao a prova de que coube.
///
/// A janela e a de verdade (mesmo codigo, mesmo tema, mesmos estilos); o que e de mentira e o
/// SERVIDOR, o mesmo truque da suite (TestesResgateManual.NaJanela): um delegate devolve as respostas
/// do resgate_conferir e do resgate_manual com dados de exemplo (cliente Renata Alves, premio
/// "1 Cookie Super Premium" com as 11 opcoes de hoje, SQL 156), e o executar da resposta e trocado
/// por um que so le o JSON, sem papel e sem nuvem.
///
/// Seguranca: nao encosta no banco do caixa. O Banco e apontado para um SQLite temporario vazio
/// (a janela nem abre banco, mas o redirecionamento e barato e fecha a porta), e nenhuma chamada
/// sai para a rede.
///
/// A confirmacao e um ShowDialog (modal): a foto dela sai por um timer armado ANTES do toque em
/// Resgatar, que dispara dentro do laco aninhado do dialogo, fotografa as tres janelas compostas
/// (fundo, janela de resgate, pergunta) e toca no "Resgatar" do dialogo para o resgate seguir.
/// </summary>
public static class FotoResgate
{
    private const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;

    private const string Codigo = "AD-7KQ2MX";
    private const string CodigoDonut = "AD-DN15XX";
    private const string Oid6066 = "60660000-0000-4000-8000-000000006066";
    private const string Oid6283 = "62830000-0000-4000-8000-000000006283";
    private const string Bonus = "b1540000-0000-4000-8000-000000000154";
    private const string Operador = "Ingrid";
    private const string Sabor = "Red Velvet";

    /// <summary>As 11 opcoes do Cookie Super Premium depois do SQL 156: as dele, as Premium e as Classicas.</summary>
    private static readonly string[] Opcoes =
    {
        "Pistache", "Nutelludo",
        "Brigadeiro", "Churros", "Red Velvet", "Ninho", "Ninho com Nutella", "Pink Lemonade",
        "New York", "Tradicional", "Triplo",
    };

    /// <summary>As 15 do Donut Super Premium depois do SQL 156: as dele, as Premium e as Classicas. O pior caso da janela.</summary>
    private static readonly string[] OpcoesDonut =
    {
        "Calabresa", "4 Queijos", "Bueno", "Nutelludo",
        "Morango com Ninho", "Ninho com Nutella", "Rocher", "Morango com Nutella",
        "Banoffee", "Boston Cream", "Churros", "Homer", "Ovomaltine", "Red Velvet", "Brigadeiro Gourmet",
    };

    private static readonly (int W, int H)[] Tamanhos = { (1024, 768), (1366, 768), (1920, 1080) };

    public static int Rodar(string[] args)
    {
        var dir = Path.GetFullPath(args[1]);
        var tema = args.Skip(2).FirstOrDefault(a => a is "claro" or "escuro") ?? "claro";
        Directory.CreateDirectory(dir);

        // banco temporario e vazio: a janela nao abre banco, mas nada daqui pode cair no pdv.db real
        var fixture = Path.Combine(Path.GetTempPath(), "foto-resgate-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        Banco.Migrar(fixture);
        Banco.CaminhoForcado = fixture;

        var codigo = 1;
        var t = new Thread(() =>
        {
            try { codigo = Fotografar(dir, tema); }
            catch (Exception ex) { Console.Error.WriteLine("foto-resgate: " + ex); codigo = 1; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { File.Delete(fixture); } catch { /* temporario */ }
        return codigo;
    }

    private static int Fotografar(string dir, string tema)
    {
        // mesmos dicionarios do Pdv.exe, na mesma ordem (contrato: [0] paleta, [1] estilos)
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

        var codigo = 1;
        // um passo por vez, com await, dentro de uma operacao do Dispatcher (e assim que o HostWpf
        // da suite roda os passos assincronos das telas): cada Task.Delay volta para esta thread
        app.Startup += (_, _) => _ = app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                foreach (var (w, h) in Tamanhos)
                    await UmTamanhoAsync(dir, tema, w, h, erros);
                foreach (var e in erros.Distinct()) Console.WriteLine("  aviso: " + e);
                codigo = erros.Count == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("foto-resgate: " + ex);
                codigo = 1;
            }
            app.Shutdown();
        });
        // um travamento qualquer nao pode prender a esteira: 90 s para as 12 fotos e desiste
        var limite = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        limite.Tick += (_, _) => { Console.Error.WriteLine("foto-resgate: tempo esgotado"); codigo = 3; app.Shutdown(); };
        limite.Start();
        app.Run();
        return codigo;
    }

    /// <summary>Os quatro momentos num tamanho de tela: abre o host, a janela, e vai tirando as fotos.</summary>
    private static async Task UmTamanhoAsync(string dir, string tema, int w, int h, List<string> erros)
    {
        // o "monitor": uma janela do tamanho da tela da loja, com o fundo do app, que faz o papel da
        // tela de venda como dona do dialogo (a altura dela e o que limita a janela de resgate)
        var host = new Window
        {
            Title = "foto-resgate", Width = w, Height = h,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0,
            ShowInTaskbar = false, ShowActivated = false,
            Content = new Border { Background = R("Fundo") },
        };
        host.Show();
        await Task.Delay(300);
        host.UpdateLayout();

        var tela = new Pdv.Telas.ResgateRaspadinha(host, Servidor, Operador, "a0420449-0000-4000-8000-000000000001",
            Codigo, Oid6066, "6066", chatAtivo: true, Executar);
        var janela = tela.Janela;
        janela.ShowActivated = false;
        janela.Show();
        // o Loaded confere o codigo inicial no servidor falso (sincrono); uma folga para o layout assentar
        await Task.Delay(900);
        janela.UpdateLayout();
        var pre = $"{w}x{h}";
        if (!tela.Linha.StartsWith("🍪", StringComparison.Ordinal))
            erros.Add($"{pre}: a conferencia nao pintou o premio (linha=[{tela.Linha}])");
        Medidas(pre, tela, janela, host);
        Foto(dir, "conferido", w, h, tema, host, janela);

        // [rolado] so quando o miolo nao cabe: o que a pessoa so ve depois de rolar ate o fim, que e de
        // onde ela toca no sabor. Daqui em diante a rolagem fica onde ela deixou, como na loja. Pela
        // regra da casa isto NAO pode acontecer em nenhum dos tres tamanhos: a foto sai como prova e o
        // modo termina com aviso (saida 1).
        var rolagem = Rolagem(tela);
        if (rolagem is { ScrollableHeight: > 0 })
        {
            erros.Add($"{pre}: depois do Conferir o miolo precisou rolar {rolagem.ScrollableHeight:0} px (cookie, 11 opcoes)");
            rolagem.ScrollToEnd();
            await Task.Delay(300);
            janela.UpdateLayout();
            Foto(dir, "rolado", w, h, tema, host, janela);
        }

        // [sabor] toca num chip: fica marcado e o Resgatar habilita
        var chip = Campo<WrapPanel>(tela, "_sabores").Children.OfType<Button>().FirstOrDefault(b => (string)b.Tag == Sabor);
        if (chip is null) erros.Add($"{pre}: o chip '{Sabor}' nao esta na janela");
        else chip.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Task.Delay(300);
        janela.UpdateLayout();
        if (!Campo<Button>(tela, "_btnResgatar").IsEnabled) erros.Add($"{pre}: o Resgatar nao habilitou com o sabor");
        Foto(dir, "sabor", w, h, tema, host, janela);

        // [confirmacao] o Dialogo.Confirmar e modal: a foto sai por um timer armado antes do toque
        var foto = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        foto.Tick += (_, _) =>
        {
            foto.Stop();
            var dialogo = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.Owner == janela && x.IsVisible);
            if (dialogo is null) { erros.Add($"{pre}: a pergunta de confirmacao nao abriu"); return; }
            dialogo.UpdateLayout();
            Console.WriteLine($"  {pre}: confirmacao {dialogo.ActualWidth:0}x{dialogo.ActualHeight:0} em ({dialogo.Left - host.Left:0},{dialogo.Top - host.Top:0})");
            Foto(dir, "confirmacao", w, h, tema, host, janela, dialogo);
            var sim = Descendentes(dialogo).OfType<Button>().FirstOrDefault(b => b.Content as string == "Resgatar");
            if (sim is null) { erros.Add($"{pre}: o botao Resgatar da confirmacao nao foi achado"); dialogo.Close(); }
            else sim.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        };
        foto.Start();
        await tela.ResgatarAsync();

        // [resgatado] o servidor falso disse ok e o executar falso leu a resposta
        await Task.Delay(300);
        janela.UpdateLayout();
        if (tela.BonusId is null) erros.Add($"{pre}: o resgate nao concluiu (linha=[{tela.Linha}])");
        if (rolagem is not null)
            Console.WriteLine($"  {pre}: depois do resgate a janela tem {janela.ActualHeight:0} px e o miolo esta em "
                              + $"{rolagem.VerticalOffset:0} de {rolagem.ScrollableHeight:0} px de rolagem");
        Foto(dir, "resgatado", w, h, tema, host, janela);
        janela.Close();

        // [donut15] o pior caso: o Donut Super Premium com as 15 opcoes do SQL 156, os mesmos dois
        // pedidos e o balcao, logo depois do Conferir. Tem de caber sem rolar em todos os tamanhos.
        var telaDonut = new Pdv.Telas.ResgateRaspadinha(host, Servidor, Operador, "a0420449-0000-4000-8000-000000000001",
            CodigoDonut, Oid6066, "6066", chatAtivo: true, Executar);
        var jDonut = telaDonut.Janela;
        jDonut.ShowActivated = false;
        jDonut.Show();
        await Task.Delay(900);
        jDonut.UpdateLayout();
        if (!telaDonut.Linha.StartsWith("🍩", StringComparison.Ordinal))
            erros.Add($"{pre}: a conferencia do donut nao pintou o premio (linha=[{telaDonut.Linha}])");
        Medidas(pre + " donut15", telaDonut, jDonut, host);
        var rolDonut = Rolagem(telaDonut);
        if (rolDonut is { ScrollableHeight: > 0 })
            erros.Add($"{pre}: o donut com 15 opcoes precisou rolar {rolDonut.ScrollableHeight:0} px");
        Foto(dir, "donut15", w, h, tema, host, jDonut);
        jDonut.Close();

        host.Close();
        await Task.Delay(200);
    }

    /// <summary>A regua do layout: tamanho e posicao da janela, teto, e se o miolo precisou rolar.</summary>
    /// <summary>O ScrollViewer do MIOLO, pelo campo: o primeiro da arvore visual seria o de dentro do TextBox do codigo.</summary>
    private static ScrollViewer? Rolagem(Pdv.Telas.ResgateRaspadinha tela)
        => tela.GetType().GetField("_rolagem", P)?.GetValue(tela) as ScrollViewer;

    private static void Medidas(string pre, Pdv.Telas.ResgateRaspadinha tela, Window janela, Window host)
    {
        var rolagem = Rolagem(tela);
        var sabores = Campo<WrapPanel>(tela, "_sabores");
        var linhas = sabores.Children.OfType<Button>().Select(b => Math.Round(b.TranslatePoint(new Point(0, 0), sabores).Y)).Distinct().Count();
        Console.WriteLine($"  {pre}: janela {janela.ActualWidth:0}x{janela.ActualHeight:0} em ({janela.Left - host.Left:0},{janela.Top - host.Top:0}), "
                          + $"teto {janela.MaxHeight:0}, miolo rola {(rolagem is null ? "?" : rolagem.ScrollableHeight.ToString("0"))} px, "
                          + $"{sabores.Children.Count} chips em {linhas} linha(s)");
    }

    private static void Foto(string dir, string momento, int w, int h, string tema, Window host, params Window[] janelas)
    {
        var saida = Path.Combine(dir, $"resgate-{momento}-{w}{(tema == "claro" ? "" : "-" + tema)}.png");
        Compor(saida, w, h, host, janelas);
        Console.WriteLine($"foto-resgate: {saida} ({w}x{h}, tema {tema}, {momento})");
    }

    /// <summary>
    /// Grava o fundo e, por cima, cada janela na posicao em que ela esta. Cada dialogo e uma Window
    /// propria: renderizar so o host deixaria a janela de resgate (e a pergunta) de fora da foto.
    /// </summary>
    private static void Compor(string saida, int w, int h, Window host, IEnumerable<Window> janelas)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var fundo = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            fundo.Render((Visual)host.Content);
            dc.DrawImage(fundo, new Rect(0, 0, w, h));
            foreach (var j in janelas)
            {
                var jw = Math.Max(1, (int)Math.Ceiling(j.ActualWidth));
                var jh = Math.Max(1, (int)Math.Ceiling(j.ActualHeight));
                var bmp = new RenderTargetBitmap(jw, jh, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(j);
                dc.DrawImage(bmp, new Rect(j.Left - host.Left, j.Top - host.Top, jw, jh));
            }
        }
        var final = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        final.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(final));
        Directory.CreateDirectory(Path.GetDirectoryName(saida)!);
        using var fs = File.Create(saida);
        enc.Save(fs);
    }

    // ── o servidor de mentira ─────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Responde o conferir e o resgatar com os dados de exemplo (o donut pelo codigo dele); qualquer outra acao e 500.</summary>
    private static Task<(int, string?)> Servidor(string nome, string corpo)
    {
        string acao, codigo;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            acao = doc.RootElement.TryGetProperty("acao", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
            codigo = doc.RootElement.TryGetProperty("codigo", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
        }
        catch { acao = ""; codigo = ""; }
        return Task.FromResult<(int, string?)>(acao switch
        {
            ResgateManual.AcaoConferir => (200, Conferido(codigo == CodigoDonut)),
            ResgateManual.AcaoResgatar => (200, Resgatado()),
            _ => (500, null),
        });
    }

    /// <summary>O executar da resposta sem papel e sem nuvem: so le o JSON e diz que a comanda saiu.</summary>
    private static Task<(RespostaConversa, bool, bool)> Executar(int st, string? corpo)
        => Task.FromResult((ConversaRaspadinha.LerResposta(st, corpo, DateTime.Now), true, false));

    /// <summary>A resposta do resgate_conferir: o premio com escolha de sabor (cookie com 11 ou donut com 15), dois pedidos abertos e os codigos vivos.</summary>
    private static string Conferido(bool donut) => JsonSerializer.Serialize(new
    {
        ok = true, ligado = true, modo = "automatico", chat_ativo = true,
        codigo = new
        {
            codigo = donut ? CodigoDonut : Codigo, scratch_id = "s-foto", slug = donut ? "donut_super_premium" : "cookie_super_premium",
            premio = donut ? "1 Donut Super Premium" : "1 Cookie Super Premium",
            nome_curto = donut ? "Donut Super Premium" : "Cookie Super Premium", emoji = donut ? "🍩" : "🍪",
            // sem fuso de proposito: a tela le como hora local, e a foto nao pode depender da maquina
            vence_em = "2026-10-22T23:59:59", motivo = "ok",
            loja_codigo = (string?)null, usado_em = (string?)null, usado_por = (string?)null,
            cliente_nome = "RENATA ALVES",
            escolhas = 1, repete = false, opcoes = donut ? OpcoesDonut : Opcoes, padrao = "", tem_catalogo = true, itens_fixos = Array.Empty<string>(),
        },
        frase = (string?)null,
        pedidos = new object[]
        {
            new
            {
                ifood_order_id = Oid6066, numero = "6066", cliente = "Renata Alves", entrega_status = "pronto",
                recebido_em = "2026-10-08T19:12:00", sugerido = true, premio_codigo = (string?)null, conversa_estado = "humano",
            },
            new
            {
                ifood_order_id = Oid6283, numero = "6283", cliente = "Paola Martins", entrega_status = (string?)null,
                recebido_em = "2026-10-08T19:31:00", sugerido = false, premio_codigo = (string?)null, conversa_estado = (string?)null,
            },
        },
        pedido_informado = new { ifood_order_id = Oid6066, numero = "6066", cliente = "Renata Alves", entrega_status = "pronto", saiu = false, cancelado = false },
        candidatos = new[] { donut ? CodigoDonut : Codigo, "AD-RB684S" },
    }, Json);

    /// <summary>A resposta ok do resgate_manual, no shape do contrato do 154 (a mesma da suite, com estes nomes).</summary>
    private static string Resgatado() => JsonSerializer.Serialize(new
    {
        ok = true, modo = "automatico", prova = false, acao = "resgatou", motivo = (string?)null, repetida = false, destino = "ifood",
        pedido = new { ifood_order_id = Oid6066, numero = "6066", cliente = "Renata Alves", entrega_status = "pronto" },
        conversa = new { id = "9b1c", estado = "resgatado", sombra = false },
        bonus = new
        {
            id = Bonus, codigo = Codigo, loja = "American Day Savassi", premio_nome = "1 Cookie Super Premium", premio_emoji = "🍪",
            cliente_nome = "Renata Alves", pedido_numero = "6066", ifood_order_id = Oid6066,
            origem = "caixa", itens = new[] { new { nome = "Cookie " + Sabor, qtd = 1 } }, sabores = new[] { Sabor },
            assinatura = Operador, comanda_onde = "ambos", criado_em = "2026-10-08T22:12:00Z",
        },
        itens = new[] { new { nome = "Cookie " + Sabor, qtd = 1 } },
        comanda = new { imprimir_aqui = true },
        saidas = new[]
        {
            new
            {
                id = 4413, etapa = "confirmado", ordem = 1,
                texto = "Oi, Renata! Recebemos o código da sua raspadinha. Seu prêmio vai junto com este pedido: 1 Cookie " + Sabor + ". Aproveite!",
                como = "sdk", estado = "reservada", canal = "sendbird_gc_cm_" + Oid6066 + "_ff493e25-f413-42e5-a46a-96c0b788813f",
                ifood_order_id = Oid6066, pedido_numero = "6066",
            },
        },
        aviso = new { tipo = "resgatou", texto = "Resgate no #6066: 1 Cookie " + Sabor + " para Renata.", ifood_order_id = Oid6066, pedido_numero = "6066" },
        pelo_dono = false, frase = "Resgatado: 1 Cookie " + Sabor + " para Renata no #6066.", desfazer_ate = "2026-10-09T00:12:00Z",
    }, Json);

    // ── miudezas ─────────────────────────────────────────────────────────────

    private static Brush R(string chave) => (Brush)Application.Current.Resources[chave];

    private static T Campo<T>(object alvo, string nome) => (T)alvo.GetType().GetField(nome, P)!.GetValue(alvo)!;

    private static IEnumerable<DependencyObject> Descendentes(DependencyObject o)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
        {
            var c = VisualTreeHelper.GetChild(o, i);
            yield return c;
            foreach (var n in Descendentes(c)) yield return n;
        }
    }
}
