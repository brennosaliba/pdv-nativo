using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Xps;
using Pdv.Nucleo;

namespace Pdv;

/// <summary>
/// A COMANDA EM ETIQUETA 10x15 (05/10/2026, pedido do dono). Mesma esteira do cupom e da
/// comanda de bobina (fila única, thread STA, prazo de 25 s, vigia do trabalho no
/// spooler), com uma diferença de fundo: aqui a página tem tamanho FIXO, 100 x 150 mm,
/// e o desenho é gráfico (nome grande, quadradinhos desenhados, QR), não texto de 40
/// colunas. O conteúdo é decidido em <see cref="EtiquetaKds"/> (Núcleo); aqui só se pinta.
///
/// O GIRO: a Elgin pode receber a folha em pé (100 x 150) ou deitada (150 x 100),
/// conforme o driver foi configurado, e em qual dos dois sentidos o rolo anda só o papel
/// prova. Por isso a folha e o giro do desenho dentro dela são escolha da tela de
/// Configuração (<see cref="EtiquetaKds.Folha"/>), com botão de teste.
/// </summary>
public static partial class Impressao
{
    /// <summary>
    /// Imprime a etiqueta da comanda. Mesmo contrato do resto: null = saiu; string =
    /// mensagem pronta para a tela. Nunca lança.
    /// </summary>
    /// <param name="impressora">Vazio/null = impressora padrão do Windows.</param>
    /// <param name="giro">0, 90, 180 ou 270 (ver <see cref="EtiquetaKds.Folha"/>).</param>
    public static Task<string?> ImprimirEtiquetaKdsAsync(Ticket ticket, string? impressora, int giro,
        string descricao, PrioridadeImpressao prioridade = PrioridadeImpressao.Alta)
    {
        Etiqueta e;
        try { e = EtiquetaKds.Montar(ticket); }
        catch (Exception ex) { return Task.FromResult<string?>($"Pedido inconsistente, não imprimi a etiqueta: {ex.Message}"); }
        var folha = EtiquetaKds.Folha(giro);
        // Nome único por tentativa: a vigia casa o trabalho pelo nome (ver Imprimir).
        var nome = $"{descricao} #{Environment.TickCount64:x}";
        return NaFilaAsync(prioridade,
            () => ComPrazoAsync(EmThreadStaAsync(() => ImprimirEtiquetaVisual(e, folha, nome, impressora))));
    }

    /// <summary>
    /// Desenha a etiqueta num PNG, do jeito que vai para o driver (com o giro). Serve à
    /// foto de conferência (Pdv.Testes --foto-etiqueta). <paramref name="escala"/> 3 =
    /// texto legível na tela.
    /// </summary>
    public static Task<string?> PreVisualizarEtiquetaAsync(Ticket ticket, string caminhoPng, int giro = 0,
        double escala = 3.0, DateTime? hoje = null)
        => EmThreadStaAsync(() =>
        {
            var e = EtiquetaKds.Montar(ticket, hoje);
            var folha = EtiquetaKds.Folha(giro);
            var visual = new Border
            {
                // Contorno cinza SÓ na foto: etiqueta branca em fundo branco não tem borda à vista.
                BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1),
                Child = FolhaGirada(e, folha),
            };
            visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            visual.Arrange(new Rect(new Point(0, 0), visual.DesiredSize));
            visual.UpdateLayout();
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(visual.DesiredSize.Width * escala),
                (int)Math.Ceiling(visual.DesiredSize.Height * escala),
                96 * escala, 96 * escala, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(caminhoPng))!);
            using var fs = System.IO.File.Create(caminhoPng);
            enc.Save(fs);
            return null;
        });

    private static string? ImprimirEtiquetaVisual(Etiqueta e, FolhaEtiqueta folha, string descricao, string? nomeImpressora)
    {
        LocalPrintServer? servidor = null;
        PrintQueue? fila = null;
        try
        {
            var pedida = nomeImpressora?.Trim();
            try
            {
                if (string.IsNullOrEmpty(pedida)) fila = LocalPrintServer.GetDefaultPrintQueue();
                else { servidor = new LocalPrintServer(); fila = servidor.GetPrintQueue(pedida); }
            }
            catch
            {
                return string.IsNullOrEmpty(pedida)
                    ? "Nenhuma impressora padrão configurada no Windows."
                    : $"Impressora '{pedida}' não encontrada.";
            }

            var visual = FolhaGirada(e, folha);
            var w = folha.LarguraMm * MM;
            var h = folha.AlturaMm * MM;
            visual.Measure(new Size(w, h));
            visual.Arrange(new Rect(0, 0, w, h));
            visual.UpdateLayout();

            var ticket = TicketDe(fila);
            // Página do tamanho da ETIQUETA, sempre: aqui não existe "altura do conteúdo".
            // Declarar menos corta o QR; declarar mais faz o driver avançar uma etiqueta em branco.
            ticket.PageMediaSize = new PageMediaSize(w, h);
            ticket.PageOrientation = PageOrientation.Portrait;
            try { fila.CurrentJobSettings.Description = descricao; }
            catch { /* driver que não aceita descrição: perde-se só o diagnóstico fino */ }

            PrintQueue.CreateXpsDocumentWriter(fila).Write(visual, ticket);
            return Vigiar(fila, descricao);
        }
        catch (Exception ex)
        {
            return Legivel(ex, nomeImpressora);
        }
        finally
        {
            fila?.Dispose();
            servidor?.Dispose();
        }
    }

    /// <summary>A etiqueta (sempre desenhada em pé, 100 x 150) girada para a folha que o driver espera.</summary>
    private static FrameworkElement FolhaGirada(Etiqueta e, FolhaEtiqueta folha)
    {
        var etiqueta = MontarEtiqueta(e);
        if (folha.Giro == 0) return etiqueta;
        return new Border
        {
            Background = Brushes.White,
            Width = folha.LarguraMm * MM,
            Height = folha.AlturaMm * MM,
            // LayoutTransform (e não Render): o giro entra na medida, e a etiqueta deitada
            // ocupa 150 x 100 de verdade em vez de vazar da página.
            Child = new Border { Child = etiqueta, LayoutTransform = new RotateTransform(folha.Giro) },
        };
    }

    /// <summary>
    /// O desenho da etiqueta, 100 x 150 mm, preto no branco:
    ///
    ///   ANA BEATRIZ          [iFOOD]
    ///   SOUZA           Chegou 17:37
    ///   (o maior que cabe)     #8149
    ///   ─────────────────────────────────
    ///   ☐ 2× Donut Ninho        (subitens do combo; o nome do combo não sai)
    ///   ☐ 1× Donut Homer
    ///       » sem granulado     (observação do combo, abaixo dos subitens)
    ///   ☐ 2× Cookie Duplo
    ///   ─────────────────────────────────
    ///             [ QR 40 mm ]
    ///          ADKDS:&lt;order_id&gt;
    ///
    /// O miolo dos itens encolhe sozinho (Viewbox só para baixo) quando o pedido é comprido:
    /// melhor letra menor do que item cortado no fim da etiqueta. O QR e o número nunca
    /// encolhem.
    /// </summary>
    private static FrameworkElement MontarEtiqueta(Etiqueta e)
    {
        const double margem = 4 * MM;
        var util = (EtiquetaKds.LarguraMm - 8) * MM;

        var raiz = new Grid
        {
            Width = EtiquetaKds.LarguraMm * MM,
            Height = EtiquetaKds.AlturaMm * MM,
            Background = Brushes.White,
        };
        var miolo = new Grid { Margin = new Thickness(margem) };
        for (var i = 0; i < 6; i++)
            miolo.RowDefinitions.Add(new RowDefinition
            {
                Height = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });
        // linhas: 0 cabeçalho, 1 régua, 2 cliente, 3 régua, 4 itens (*), 5 rodapé com QR
        raiz.Children.Add(miolo);

        // ── cabeçalho (06/10, esboço do dono): à ESQUERDA o nome do cliente no maior
        // tamanho que couber (fonte estreita e alta), ocupando ~2/3 da largura e a altura de
        // ~3 linhas; à DIREITA, empilhados, o selo da origem, "Chegou HH:MM" e o número
        // grande. Empilhados e não sobrepostos: no esboço o "Chegou" saía cortado.
        var largNome = util * 0.64;
        var largDir = util - largNome - 3 * MM;
        var cab = new Grid { Height = AlturaCabecalhoMm * MM };
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(largNome) });
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3 * MM) });
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(largDir) });

        if (e.Cliente is { } nome)
            cab.Children.Add(NomeGrande(nome, largNome, AlturaCabecalhoMm * MM));
        else
            // Sem nome (balcão): o lugar do nome fica com o número, que é o que se grita.
            cab.Children.Add(new Viewbox
            {
                Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Child = TextoEtq("#" + e.Numero, 60, negrito: true),
            });

        var dir = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        dir.Children.Add(new Border
        {
            Background = Brushes.Black, CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 3), HorizontalAlignment = HorizontalAlignment.Right,
            Child = new Viewbox
            {
                Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxWidth = largDir - 12,
                Child = TextoEtq(e.Origem, 14, negrito: true, cor: Brushes.White),
            },
        });
        dir.Children.Add(Ajustado(TextoEtq(e.Chegou, 13), largDir, HorizontalAlignment.Right, new Thickness(0, 4, 0, 0)));
        if (e.Retirada)
            dir.Children.Add(Ajustado(TextoEtq("RETIRADA", 13, negrito: true), largDir, HorizontalAlignment.Right, new Thickness(0)));
        if (e.Cliente is not null)
            dir.Children.Add(Ajustado(TextoEtq("#" + e.Numero, 40, negrito: true), largDir,
                                      HorizontalAlignment.Right, new Thickness(0, 2, 0, 0)));
        Grid.SetColumn(dir, 2);
        cab.Children.Add(dir);

        var topo = new StackPanel();
        topo.Children.Add(cab);
        if (e.Agendado is { } ag)
            topo.Children.Add(TextoEtq(ag, 15, negrito: true, quebra: true));
        Grid.SetRow(topo, 0);
        miolo.Children.Add(topo);

        var r1 = ReguaEtq();
        Grid.SetRow(r1, 1);
        miolo.Children.Add(r1);

        // ── itens ──
        var lista = new StackPanel { Width = util };
        foreach (var l in e.Linhas) lista.Children.Add(LinhaItem(l));
        var itens = new Viewbox
        {
            Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Child = lista,
        };
        Grid.SetRow(itens, 4);
        miolo.Children.Add(itens);

        // ── rodapé: QR grande + o mesmo código em texto pequeno ──
        var pe = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        pe.Children.Add(ReguaEtq());
        if (Qr(e.Qr, EtiquetaKds.QrLadoMm * MM) is { } qr) pe.Children.Add(qr);
        else pe.Children.Add(TextoEtq("QR indisponível: marque pronto pelo quadro", 12, negrito: true, centro: true, quebra: true));
        pe.Children.Add(TextoEtq(e.Qr, 8, centro: true, quebra: true, fonte: Mono));
        Grid.SetRow(pe, 5);
        miolo.Children.Add(pe);

        return raiz;
    }

    /// <summary>Altura reservada ao cabeçalho (nome grande + coluna da direita).</summary>
    private const double AlturaCabecalhoMm = 30;

    /// <summary>
    /// A fonte do nome: estreita e alta. Bahnschrift (Windows 10+) tem a largura Condensed
    /// embutida; sem ela, Arial Narrow; sem as duas, Segoe UI e o aperto horizontal faz o
    /// papel da fonte condensada (AjustarNome nunca passa de 0,6).
    /// </summary>
    private static readonly Lazy<Typeface> FonteNome = new(() =>
    {
        var nomes = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (nomes.Contains("Bahnschrift"))
            return new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.Bold, FontStretches.Condensed);
        if (nomes.Contains("Arial Narrow"))
            return new Typeface(new FontFamily("Arial Narrow"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        return new Typeface(Sans, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    });

    /// <summary>O ajuste do nome com a fonte real (exposto para a suíte conferir que cabe).</summary>
    public static AjusteNome AjusteDoNome(string nome, double largura, double altura)
    {
        var tf = FonteNome.Value;
        double LarguraPorEm(string s) => new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, tf, 100, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace / 100;
        return EtiquetaKds.AjustarNome(nome, largura, altura, LarguraPorEm, tf.FontFamily.LineSpacing);
    }

    /// <summary>O nome do cliente no maior tamanho que cabe em <paramref name="largura"/> x <paramref name="altura"/>.</summary>
    private static FrameworkElement NomeGrande(string nome, double largura, double altura)
    {
        var a = AjusteDoNome(nome, largura, altura);
        var tf = FonteNome.Value;
        var pilha = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            LayoutTransform = new ScaleTransform(a.EscalaX, 1),
        };
        foreach (var l in a.Linhas)
            pilha.Children.Add(new TextBlock
            {
                Text = l, FontFamily = tf.FontFamily, FontWeight = tf.Weight, FontStretch = tf.Stretch,
                FontSize = a.Tamanho, Foreground = Brushes.Black, TextWrapping = TextWrapping.NoWrap,
            });
        return pilha;
    }

    /// <summary>Texto que encolhe (nunca cresce) para caber na largura dada, sem cortar.</summary>
    private static FrameworkElement Ajustado(TextBlock tb, double largura, HorizontalAlignment lado, Thickness margem)
        => new Viewbox
        {
            Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            MaxWidth = largura, HorizontalAlignment = lado, Margin = margem, Child = tb,
        };

    /// <summary>Uma linha do miolo: quadradinho desenhado (retângulo, não caractere), quantidade em negrito e nome.</summary>
    private static FrameworkElement LinhaItem(LinhaEtiqueta l)
    {
        var (tam, recuo) = l.Tipo switch
        {
            // Subitem do combo é linha de conferência como o item (o pai não sai mais).
            TipoLinhaEtiqueta.Item or TipoLinhaEtiqueta.Subitem => (19.0, 0.0),
            _ => (15.0, 22.0),
        };
        var linha = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(recuo, l.Tipo == TipoLinhaEtiqueta.Observacao ? 0 : 3, 0, 1),
        };
        if (l.Caixa)
        {
            var lado = tam * 0.8;
            var caixa = new Border
            {
                Width = lado, Height = lado, BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1.8), Background = Brushes.White,
                Margin = new Thickness(0, tam * 0.18, 7, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            DockPanel.SetDock(caixa, Dock.Left);
            linha.Children.Add(caixa);
        }
        var tb = new TextBlock
        {
            FontFamily = Sans, FontSize = tam, Foreground = Brushes.Black,
            TextWrapping = TextWrapping.Wrap,
        };
        if (l.Tipo == TipoLinhaEtiqueta.Observacao)
        {
            tb.FontStyle = FontStyles.Italic;
            tb.Inlines.Add(new System.Windows.Documents.Run("» " + l.Texto) { FontWeight = FontWeights.SemiBold });
        }
        else
        {
            if (l.Qtd.Length > 0)
                tb.Inlines.Add(new System.Windows.Documents.Run(l.Qtd + " ") { FontWeight = FontWeights.Bold });
            tb.Inlines.Add(new System.Windows.Documents.Run(l.Texto));
        }
        linha.Children.Add(tb);
        return linha;
    }

    private static TextBlock TextoEtq(string conteudo, double tamanho, bool negrito = false, bool centro = false,
        bool quebra = false, Brush? cor = null, FontFamily? fonte = null) => new()
        {
            Text = conteudo,
            FontFamily = fonte ?? Sans,
            FontSize = tamanho,
            FontWeight = negrito ? FontWeights.Bold : FontWeights.Normal,
            Foreground = cor ?? Brushes.Black,
            TextAlignment = centro ? TextAlignment.Center : TextAlignment.Left,
            TextWrapping = quebra ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };

    private static Border ReguaEtq() => new()
    {
        Height = 2, Background = Brushes.Black, Margin = new Thickness(0, 4, 0, 4),
    };
}
