using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
    /// <param name="embalagem">Palavras de embalagem da loja (<see cref="EtiquetaKds.PalavrasEmbalagem"/>); null = padrão.</param>
    public static Task<string?> ImprimirEtiquetaKdsAsync(Ticket ticket, string? impressora, int giro,
        string descricao, PrioridadeImpressao prioridade = PrioridadeImpressao.Alta,
        IReadOnlyList<string>? embalagem = null)
    {
        Etiqueta e;
        try { e = EtiquetaKds.Montar(ticket, embalagem: embalagem); }
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
        var etiqueta = MontarEtiqueta(e, out _);
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
    private static FrameworkElement MontarEtiqueta(Etiqueta e, out TamanhoItens tamanho)
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

        // ── itens (06/10, dono): o MAIOR tamanho que faça todos caberem entre a régua do
        // cabeçalho e a do QR. Teto de 2x o tamanho base (pedido curto não vira cartaz) e piso
        // legível; abaixo do piso o QR desce de 40 para 35 mm, e só então o miolo encolhe
        // (Viewbox só para baixo) em vez de cortar item. Cabeçalho e QR não mudam de tamanho
        // por causa dos itens.
        double Altura(FrameworkElement el)
        {
            el.Measure(new Size(util, double.PositiveInfinity));
            return el.DesiredSize.Height;
        }
        var fixo = Altura(topo) + Altura(r1);
        var tam = EscolherTamanho(e, util,
            qrMm => EtiquetaKds.AlturaMm * MM - 2 * margem - fixo - Altura(Rodape(e, qrMm)) - 2);
        tamanho = tam;

        var itens = new Viewbox
        {
            Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Child = ListaItens(e, tam.Escala, util),
        };
        Grid.SetRow(itens, 4);
        miolo.Children.Add(itens);

        var pe = Rodape(e, tam.QrMm);
        Grid.SetRow(pe, 5);
        miolo.Children.Add(pe);

        return raiz;
    }

    /// <summary>Tamanho base da linha de item (escala 1).</summary>
    private const double FonteItemBase = 19;
    /// <summary>Teto: pedido curto sai no dobro do base, não maior.</summary>
    public const double EscalaItensMax = 2.0;
    /// <summary>Piso legível (≈ 14 px, 3,8 mm de letra). Abaixo disso o miolo encolhe pelo Viewbox.</summary>
    public const double EscalaItensMin = 0.75;

    /// <summary>O tamanho escolhido para os itens: escala, fonte, lado do quadradinho e o QR.</summary>
    public readonly record struct TamanhoItens(double Escala, double Fonte, double LadoCaixa, double Traco, double QrMm);

    /// <summary>
    /// Mede a etiqueta de um ticket e devolve o tamanho que os itens ganharam (para a suíte:
    /// pedido curto tem letra maior que pedido grande; o quadradinho acompanha a letra).
    /// </summary>
    public static Task<TamanhoItens> MedirItensAsync(Ticket ticket)
    {
        var tcs = new TaskCompletionSource<TamanhoItens>(TaskCreationOptions.RunContinuationsAsynchronously);
        var th = new Thread(() =>
        {
            try { MontarEtiqueta(EtiquetaKds.Montar(ticket), out var t); tcs.TrySetResult(t); }
            catch (Exception ex) { tcs.TrySetException(ex); }
            finally { try { Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch { } }
        }) { IsBackground = true };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Busca binária da maior escala em que a lista cabe na altura disponível. Não cabe nem
    /// no piso com QR de 40 mm: tenta com 35 mm (o mínimo do dono). Não cabe nem assim: fica
    /// no piso e o Viewbox encolhe o resto.
    /// </summary>
    private static TamanhoItens EscolherTamanho(Etiqueta e, double largura, Func<double, double> disponivel)
    {
        double Altura(double s)
        {
            var l = ListaItens(e, s, largura);
            l.Measure(new Size(largura, double.PositiveInfinity));
            return l.DesiredSize.Height;
        }
        TamanhoItens Com(double s, double qr)
        {
            var f = FonteItemBase * s;
            return new TamanhoItens(s, f, LadoDaCaixa(f), TracoDaCaixa(f), qr);
        }

        var qrMm = EtiquetaKds.QrLadoMm;
        var livre = disponivel(qrMm);
        if (Altura(EscalaItensMin) > livre)
        {
            var livre35 = disponivel(EtiquetaKds.QrLadoMinMm);
            qrMm = EtiquetaKds.QrLadoMinMm;
            livre = livre35;
            if (Altura(EscalaItensMin) > livre) return Com(EscalaItensMin, qrMm);
        }
        if (Altura(EscalaItensMax) <= livre) return Com(EscalaItensMax, qrMm);
        double lo = EscalaItensMin, hi = EscalaItensMax;
        for (var k = 0; k < 14; k++)
        {
            var meio = (lo + hi) / 2;
            if (Altura(meio) <= livre) lo = meio; else hi = meio;
        }
        return Com(lo, qrMm);
    }

    /// <summary>Lado do quadradinho ≈ altura das maiúsculas da Segoe UI (0,7 em).</summary>
    public static double LadoDaCaixa(double fonte) => fonte * 0.72;
    /// <summary>Traço do quadradinho: grosso o bastante para a térmica (≥ 2 pontos a 203 dpi) e proporcional.</summary>
    public static double TracoDaCaixa(double fonte) => Math.Max(1.6, fonte * 0.09);

    private static StackPanel ListaItens(Etiqueta e, double escala, double largura)
    {
        var lista = new StackPanel { Width = largura };
        foreach (var l in e.Linhas) lista.Children.Add(LinhaItem(l, escala));
        return lista;
    }

    /// <summary>Rodapé: régua, QR do lado pedido e o código em texto pequeno embaixo.</summary>
    private static FrameworkElement Rodape(Etiqueta e, double qrMm)
    {
        var pe = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        pe.Children.Add(ReguaEtq());
        if (Qr(e.Qr, qrMm * MM) is { } qr) pe.Children.Add(qr);
        else pe.Children.Add(TextoEtq("QR indisponível: marque pronto pelo quadro", 12, negrito: true, centro: true, quebra: true));
        pe.Children.Add(TextoEtq(e.Qr, 8, centro: true, quebra: true, fonte: Mono));
        return pe;
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

    /// <summary>
    /// Uma linha do miolo na escala escolhida: quadradinho desenhado (retângulo, não
    /// caractere) do tamanho das maiúsculas, centrado na PRIMEIRA linha do texto; a quebra
    /// do nome continua alinhada sob o texto, não sob o quadradinho (DockPanel). Observação
    /// em itálico um pouco menor, recuada até o texto do item.
    /// </summary>
    private static FrameworkElement LinhaItem(LinhaEtiqueta l, double escala)
    {
        var fonteItem = FonteItemBase * escala;
        var lado = LadoDaCaixa(fonteItem);
        var vao = fonteItem * 0.4;
        var obs = l.Tipo == TipoLinhaEtiqueta.Observacao;
        // EMBALAGEM (06/10): recuada até o texto, como a observação, um pouco menor e sem
        // quadradinho. Vai na sacola, mas não se confere como sabor.
        var emb = l.Tipo == TipoLinhaEtiqueta.Embalagem;
        var fonte = obs || emb ? fonteItem * 0.82 : fonteItem;
        var linha = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(obs || emb ? lado + vao : 0, obs ? 0 : 3 * escala, 0, 1 * escala),
        };
        if (l.Caixa)
        {
            var alturaLinha = Sans.LineSpacing * fonte;
            var caixa = new Border
            {
                Width = lado, Height = lado, BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(TracoDaCaixa(fonte)), Background = Brushes.White,
                Margin = new Thickness(0, Math.Max(0, (alturaLinha - lado) / 2), vao, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            DockPanel.SetDock(caixa, Dock.Left);
            linha.Children.Add(caixa);
        }
        var tb = new TextBlock
        {
            FontFamily = Sans, FontSize = fonte, Foreground = Brushes.Black,
            TextWrapping = TextWrapping.Wrap,
        };
        if (obs)
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
