using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// O quadro de preparo do quiosque, no fluxo do KDS da Savassi:
///
///   A PREPARAR → EM PREPARO → PRONTO · AGUARDANDO COLETA → (entregue, sai)
///
/// Fonte de verdade é o SQLite local (kds_ticket); a nuvem só ABASTECE a
/// coluna da esquerda com os pedidos do delivery. Sem internet, quem produz
/// continua movendo cards — igual ao resto do PDV.
///
/// Toque avança a etapa. As transições exigem o estado anterior lá no Núcleo:
/// dois dedos rápidos não pulam coluna nem reescrevem carimbo de tempo.
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
        // (30 s) busca a nuvem. O aceite é da ponte no servidor — aqui é produção.
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
            TxtStatus.Text = $"sem nuvem · fila local · {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            _puxando = false;
            TxtIconeAtualiza.Text = "⟳";
            Pintar();
        }
    }

    // ── pintura do quadro ───────────────────────────────────────────────────
    private void Pintar()
    {
        var abertos = Nucleo.Kds.Abertos();

        Encher(ColPreparar, abertos.Where(t => t.Status == Nucleo.Kds.Recebido));
        Encher(ColPreparo,  abertos.Where(t => t.Status == Nucleo.Kds.Preparando));
        Encher(ColPronto,   abertos.Where(t => t.Status == Nucleo.Kds.Pronto));

        TxtQtdPreparar.Text = ColPreparar.Children.Count.ToString();
        TxtQtdPreparo.Text  = ColPreparo.Children.Count.ToString();
        TxtQtdPronto.Text   = ColPronto.Children.Count.ToString();
    }

    private void Encher(StackPanel coluna, IEnumerable<Ticket> tickets)
    {
        coluna.Children.Clear();
        foreach (var t in tickets) coluna.Children.Add(Card(t));
    }

    /// <summary>
    /// O card é UM botão: com farinha na mão, o alvo é "o pedido", não um
    /// botãozinho dentro dele. O rodapé diz o que o toque faz NESTA coluna.
    /// </summary>
    private Button Card(Ticket t)
    {
        var (acaoTexto, acaoCor, acaoFundo) = t.Status switch
        {
            Nucleo.Kds.Preparando => ("TOCAR QUANDO FICAR PRONTO", "Ok", "ChipOkFundo"),
            Nucleo.Kds.Pronto     => ("TOCAR NA COLETA ✓", "Texto", "VeuElevado"),
            _                     => ("TOCAR PARA COMEÇAR", "Amarelo", "ChipAlertaFundo"),
        };

        var b = new Button
        {
            Style = (Style)Application.Current.Resources["BotaoBase"],
            MinHeight = 150, Margin = new Thickness(4, 4, 4, 6),
            Padding = new Thickness(0), Tag = t.Id,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        var raiz = new Grid();
        raiz.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        raiz.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        raiz.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── cabeçalho: número + origem + espera ─────────────────────────────
        var cab = new Grid { Margin = new Thickness(12, 8, 12, 4) };
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var esq = new StackPanel { Orientation = Orientation.Horizontal };
        var numero = new TextBlock
        {
            Text = "#" + t.Numero, FontSize = 24, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        numero.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
        esq.Children.Add(numero);
        esq.Children.Add(Chip(t.Origem == "ifood" ? "iFOOD" : "BALCÃO",
                              t.Origem == "ifood" ? "Ciano" : "Rosa"));
        cab.Children.Add(esq);

        // a espera pinta a urgência: verde <10 min, amarelo <20, vermelho depois
        var min = (int)t.Espera.TotalMinutes;
        var corEspera = min < 10 ? "Ok" : min < 20 ? "Amarelo" : "Erro";
        var espera = new TextBlock
        {
            Text = min < 1 ? "agora" : $"{min} min",
            FontSize = 14, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        espera.SetResourceReference(TextBlock.ForegroundProperty, corEspera);
        Grid.SetColumn(espera, 1);

        var dir = new StackPanel { Orientation = Orientation.Horizontal };
        dir.Children.Add(espera);
        if (t.Status == Nucleo.Kds.Preparando)
        {
            // Desfazer: pegou o card errado com a cozinha cheia — volta pra fila
            // sem drama. Botão próprio (o Click interno não vaza pro card).
            var desfaz = new Button
            {
                Content = "↩", FontSize = 16, MinHeight = 40, MinWidth = 44,
                Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(0),
                Style = (Style)Application.Current.Resources["BotaoBase"],
                ToolTip = "Devolver para A PREPARAR",
            };
            desfaz.Click += (_, _) => { Nucleo.Kds.Desassumir(t.Id); Pintar(); };
            dir.Children.Add(desfaz);
        }
        Grid.SetColumn(dir, 1);
        cab.Children.Add(dir);
        raiz.Children.Add(cab);

        // ── corpo: cliente + itens ──────────────────────────────────────────
        var corpo = new StackPanel { Margin = new Thickness(12, 0, 12, 4) };
        if (t.Cliente is { Length: > 0 })
        {
            var cli = new TextBlock
            {
                Text = t.Cliente, FontSize = 12, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 3),
            };
            cli.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
            corpo.Children.Add(cli);
        }
        foreach (var i in t.Itens)
        {
            var qtd = i.Qtd % 1000 == 0 ? (i.Qtd / 1000).ToString() : (i.Qtd / 1000m).ToString("0.###");
            var linha = new TextBlock
            {
                Text = $"{qtd}× {i.Descricao}", FontSize = 15,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1),
            };
            linha.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
            corpo.Children.Add(linha);
            if (i.Observacao is { Length: > 0 })
            {
                var obs = new TextBlock
                {
                    Text = "· " + i.Observacao, FontSize = 12, FontStyle = FontStyles.Italic,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 0, 2),
                };
                obs.SetResourceReference(TextBlock.ForegroundProperty, "Amarelo");
                corpo.Children.Add(obs);
            }
        }
        Grid.SetRow(corpo, 1);
        raiz.Children.Add(corpo);

        // ── rodapé: o que o toque faz aqui ──────────────────────────────────
        var rodape = new Border { Padding = new Thickness(0, 8, 0, 9), CornerRadius = new CornerRadius(0, 0, 13, 13) };
        rodape.SetResourceReference(Border.BackgroundProperty, acaoFundo);
        var acao = new TextBlock
        {
            Text = acaoTexto, FontSize = 12, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        acao.SetResourceReference(TextBlock.ForegroundProperty, acaoCor);
        rodape.Child = acao;
        Grid.SetRow(rodape, 2);
        raiz.Children.Add(rodape);

        b.Content = raiz;
        b.Click += (_, _) =>
        {
            switch (t.Status)
            {
                case Nucleo.Kds.Recebido:
                    Nucleo.Kds.Assumir(t.Id);
                    break;

                case Nucleo.Kds.Preparando:
                    // PRONTO é declaração para fora: quando a ponte ligar o
                    // readyToPickup, isso aciona o entregador no iFood. Toque
                    // acidental aqui vira motoboy na porta sem donut na caixa —
                    // por isso a confirmação explícita.
                    var dono = Window.GetWindow(this)!;
                    var aviso = t.Origem == "ifood"
                        ? $"O pedido #{t.Numero} vai constar como PRONTO para coleta no iFood — " +
                          "o entregador pode ser acionado. Confirma que está tudo embalado?"
                        : $"Marcar o pedido #{t.Numero} como PRONTO para entrega ao cliente?";
                    if (Dialogo.Confirmar(dono, "Pedido pronto?", aviso,
                                          "Sim, está pronto", "Ainda não"))
                        Nucleo.Kds.Liberar(t.Id);
                    break;

                case Nucleo.Kds.Pronto:
                    Nucleo.Kds.Entregar(t.Id);
                    break;
            }
            Pintar();
        };
        return b;
    }

    private FrameworkElement Chip(string texto, string cor)
    {
        var tb = new TextBlock { Text = texto, FontSize = 10, FontWeight = FontWeights.Bold };
        tb.SetResourceReference(TextBlock.ForegroundProperty, cor);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(7), Padding = new Thickness(7, 1, 7, 2),
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = tb, BorderThickness = new Thickness(1),
        };
        chip.SetResourceReference(Border.BackgroundProperty, cor == "Ciano" ? "ChipInfoFundo" : "ChipErroFundo");
        chip.SetResourceReference(Border.BorderBrushProperty, cor == "Ciano" ? "ChipInfoBorda" : "ChipErroBorda");
        return chip;
    }
}
