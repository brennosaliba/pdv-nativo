using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// A tela de preparo do quiosque. Fonte de verdade é o SQLite local (kds_ticket);
/// a nuvem só ABASTECE a fila com os pedidos de delivery — se a internet cair,
/// quem está produzindo continua vendo e tocando a fila local.
///
/// Toque 1 no card = assumir (recebido → preparando).
/// Toque 2 = liberar (preparando → pronto, sai da tela).
/// As transições exigem o estado anterior lá no Kds (Núcleo): dois dedos rápidos
/// não pulam etapa nem reescrevem carimbo de tempo.
/// </summary>
public partial class Kds : UserControl
{
    public event Action? Voltou;

    private readonly string _loja;
    private DispatcherTimer? _timer;
    private int _batidas;
    private bool _puxando;

    public Kds(string loja)
    {
        InitializeComponent();
        _loja = loja;
        Pintar();
        _ = PuxarAsync();

        // 10 s repinta o local (os relógios de espera andam); a cada 3ª batida
        // (30 s) busca a nuvem. Delivery não precisa de menos — o aceite é da
        // ponte no servidor, aqui é PRODUÇÃO.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) =>
        {
            Pintar();
            if (++_batidas % 3 == 0) _ = PuxarAsync();
        };
        _timer.Start();
        Unloaded += (_, _) => { _timer?.Stop(); _timer = null; };
        Aparencia.Mudou += Pintar;
        Unloaded += (_, _) => Aparencia.Mudou -= Pintar;
    }

    private void Voltar(object sender, RoutedEventArgs e) => Voltou?.Invoke();

    private void Atualizar(object sender, RoutedEventArgs e) => _ = PuxarAsync();

    private async Task PuxarAsync()
    {
        if (_puxando) return;
        _puxando = true;
        TxtIconeAtualiza.Text = "⟲";
        try
        {
            var novos = await Nucleo.Kds.PuxarDaNuvemAsync(Servicos.Nuvem(), _loja);
            TxtStatus.Text = $"nuvem ok · {DateTime.Now:HH:mm:ss}" + (novos > 0 ? $" · {novos} novo(s)" : "");
        }
        catch
        {
            // Sem rede o quiosque segue com a fila local — igual ao resto do PDV.
            TxtStatus.Text = $"sem nuvem · fila local · {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            _puxando = false;
            TxtIconeAtualiza.Text = "⟳";
            Pintar();
        }
    }

    // ── pintura ─────────────────────────────────────────────────────────────
    private void Pintar()
    {
        var abertos = Nucleo.Kds.Abertos();
        TxtContagem.Text = abertos.Count == 1 ? "1 na fila" : $"{abertos.Count} na fila";
        PainelVazio.Visibility = abertos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        PainelCards.Children.Clear();
        foreach (var t in abertos) PainelCards.Children.Add(Card(t));
    }

    /// <summary>
    /// O card é UM botão inteiro: em pé, com pressa e farinha na mão, o alvo é
    /// "o pedido", não um botãozinho dentro dele.
    /// </summary>
    private Button Card(Ticket t)
    {
        var preparando = t.Status == Nucleo.Kds.Preparando;

        var b = new Button
        {
            Style = (Style)Application.Current.Resources["BotaoBase"],
            Width = 330, MinHeight = 210, Margin = new Thickness(7),
            Padding = new Thickness(0), Tag = t.Id,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        if (preparando)
            b.SetResourceReference(Button.BorderBrushProperty, "Ciano");

        var raiz = new Grid();
        raiz.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        raiz.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        raiz.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── cabeçalho: número + origem + espera ────────────────────────────
        var cab = new Grid { Margin = new Thickness(14, 10, 14, 6) };
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var esq = new StackPanel { Orientation = Orientation.Horizontal };
        var numero = new TextBlock
        {
            Text = "#" + t.Numero, FontSize = 26, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        numero.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
        esq.Children.Add(numero);
        esq.Children.Add(Chip(t.Origem == "ifood" ? "iFOOD" : "BALCÃO",
                              t.Origem == "ifood" ? "Ciano" : "Rosa"));
        cab.Children.Add(esq);

        // espera pinta o card de urgência: verde <10 min, amarelo <20, vermelho dali pra frente
        var min = (int)t.Espera.TotalMinutes;
        var corEspera = min < 10 ? "Ok" : min < 20 ? "Amarelo" : "Erro";
        var espera = new TextBlock
        {
            Text = min < 1 ? "agora" : $"{min} min",
            FontSize = 15, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        espera.SetResourceReference(TextBlock.ForegroundProperty, corEspera);
        Grid.SetColumn(espera, 1);
        cab.Children.Add(espera);
        raiz.Children.Add(cab);

        // ── corpo: cliente + itens ──────────────────────────────────────────
        var corpo = new StackPanel { Margin = new Thickness(14, 0, 14, 6) };
        if (t.Cliente is { Length: > 0 })
        {
            var cli = new TextBlock
            {
                Text = t.Cliente, FontSize = 13, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 4),
            };
            cli.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
            corpo.Children.Add(cli);
        }
        foreach (var i in t.Itens)
        {
            var qtd = i.Qtd % 1000 == 0 ? (i.Qtd / 1000).ToString() : (i.Qtd / 1000m).ToString("0.###");
            var linha = new TextBlock
            {
                Text = $"{qtd}× {i.Descricao}", FontSize = 16,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1),
            };
            linha.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
            corpo.Children.Add(linha);
            if (i.Observacao is { Length: > 0 })
            {
                var obs = new TextBlock
                {
                    Text = "· " + i.Observacao, FontSize = 13, FontStyle = FontStyles.Italic,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 0, 0, 2),
                };
                obs.SetResourceReference(TextBlock.ForegroundProperty, "Amarelo");
                corpo.Children.Add(obs);
            }
        }
        Grid.SetRow(corpo, 1);
        raiz.Children.Add(corpo);

        // ── rodapé: o que o próximo toque faz ───────────────────────────────
        var rodape = new Border
        {
            Padding = new Thickness(0, 9, 0, 10), CornerRadius = new CornerRadius(0, 0, 13, 13),
        };
        rodape.SetResourceReference(Border.BackgroundProperty, preparando ? "ChipOkFundo" : "ChipInfoFundo");
        var acao = new TextBlock
        {
            Text = preparando ? "TOCAR PARA LIBERAR ✓" : "TOCAR PARA ASSUMIR",
            FontSize = 13, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
        };
        acao.SetResourceReference(TextBlock.ForegroundProperty, preparando ? "Ok" : "Ciano");
        rodape.Child = acao;
        Grid.SetRow(rodape, 2);
        raiz.Children.Add(rodape);

        b.Content = raiz;
        b.Click += (_, _) =>
        {
            if (preparando) Nucleo.Kds.Liberar(t.Id);
            else Nucleo.Kds.Assumir(t.Id);
            Pintar();
        };
        return b;
    }

    private FrameworkElement Chip(string texto, string cor)
    {
        var tb = new TextBlock { Text = texto, FontSize = 11, FontWeight = FontWeights.Bold };
        tb.SetResourceReference(TextBlock.ForegroundProperty, cor);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2, 8, 3),
            Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = tb,
        };
        chip.SetResourceReference(Border.BackgroundProperty, cor == "Ciano" ? "ChipInfoFundo" : "ChipErroFundo");
        chip.SetResourceReference(Border.BorderBrushProperty, cor == "Ciano" ? "ChipInfoBorda" : "ChipErroBorda");
        chip.BorderThickness = new Thickness(1);
        return chip;
    }
}
