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
        // A largura do card acompanha a coluna: monitor girado, janela
        // redimensionada ou tela cheia recalculam sozinhos.
        foreach (var col in new[] { ColPreparar, ColPreparo, ColPronto })
        {
            col.SizeChanged += (s, _) => LarguraDosCards((WrapPanel)s);
            LarguraDosCards(col);
        }
        _loja = loja;
        Pintar();
        _ = PuxarAsync();

        // A cada 10 s: repinta o local E busca a nuvem (o _puxando impede
        // sobreposição). Pedido novo tem que APARECER, não esperar dedo no botão —
        // o Atualizar vira só o "quero agora".
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) =>
        {
            Pintar();
            _ = PuxarAsync();
        };
        _timer.Start();
        Unloaded += (_, _) => { _timer?.Stop(); _timer = null; };
        Aparencia.Mudou += Pintar;
        Unloaded += (_, _) => Aparencia.Mudou -= Pintar;
        Servicos.Sino(loja).Ping += SinoTocou;
        Unloaded += (_, _) => Servicos.Sino(loja).Ping -= SinoTocou;
    }

    private void SinoTocou() => Dispatcher.Invoke(() => _ = PuxarAsync());

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
            if (novos > 0) Alerta.PedidoNovo();
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
            _ = ImprimirComandasAsync();
        }
    }

    // ── comanda automática (28/08 — pedido do dono) ─────────────────────────
    // Pedido de delivery que chega sai NO PAPEL sem dedo de ninguém, na
    // impressora escolhida na Configuração (chave própria da cozinha —
    // pode ser outra bobina, não a do caixa). Opt-in: nasce desligado.
    private bool _imprimindo;

    private async Task ImprimirComandasAsync()
    {
        if (_imprimindo) return;
        _imprimindo = true;
        try
        {
            string? impressora; bool auto;
            using (var cx = Banco.Abrir())
            {
                auto = Vendas.Config(cx, "kds_comanda_auto") == "1";
                impressora = Vendas.Config(cx, "kds_comanda_impressora");
                if (impressora is { Length: 0 }) impressora = null; // "" = padrão do Windows
            }
            if (!auto) return;

            foreach (var t in Nucleo.Kds.ParaImprimir())
            {
                // claim ANTES do papel: sino + timer se sobrepõem, e comanda
                // dupla é donut duplo. Falhou depois do claim → status avisa e
                // o botão 🖨 do card reimprime (impressora morta não pode virar
                // metralhadora de tentativas a cada 10 s).
                if (!Nucleo.Kds.ReivindicarImpressao(t.Id)) continue;
                var erro = await Impressao.ImprimirTextoAsync(
                    $"Comanda cozinha #{t.Numero}",
                    new[] { Nucleo.Kds.ComandaLinhas(t) }, impressora);
                if (erro is not null)
                {
                    TxtStatus.Text = $"comanda #{t.Numero} NÃO imprimiu — use 🖨 no card";
                    Alerta.PedidoNovo(); // chama atenção: papel não saiu
                }
            }
        }
        catch { /* imprimir é conforto; o quadro na tela é a fonte de verdade */ }
        finally { _imprimindo = false; }
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

    /// <summary>
    /// Cards em 3 por linha DENTRO da coluna de status. A largura e calculada da
    /// largura real da coluna (nao ha "3 colunas" no WrapPanel): assim o quadro
    /// se adapta ao monitor da cozinha, que varia de loja pra loja.
    /// </summary>
    private const int CardsPorLinha = 3;

    private static void LarguraDosCards(WrapPanel p)
    {
        // -1 para nao empatar com a largura disponivel por arredondamento e
        // jogar o terceiro card pra linha de baixo.
        var w = Math.Max(140, (p.ActualWidth / CardsPorLinha) - 1);
        if (Math.Abs(p.ItemWidth - w) > 0.5) p.ItemWidth = w;
    }

    private void Encher(Panel coluna, IEnumerable<Ticket> tickets)
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
        // PRONTO de iFood NAO sai no toque: a coleta e fato do MUNDO — quem
        // declara e o entregador, e a noticia chega pela API (DISPATCHED ->
        // espelho -> reconciliacao). Dedo no card nao inventa coleta. O toque
        // de entrega fica so pro BALCAO, onde nao existe evento externo.
        var (acaoTexto, acaoCor, acaoFundo) = t.Status switch
        {
            Nucleo.Kds.Preparando => ("TOCAR QUANDO FICAR PRONTO", "Ok", "ChipOkFundo"),
            Nucleo.Kds.Pronto when t.Origem == "ifood"
                                  => ("AGUARDANDO ENTREGADOR · sai na coleta", "TextoFraco", "VeuElevado"),
            Nucleo.Kds.Pronto     => ("TOCAR NA RETIRADA ✓", "Texto", "VeuElevado"),
            _                     => ("TOCAR PARA COMEÇAR", "Amarelo", "ChipAlertaFundo"),
        };

        var b = new Button
        {
            Style = (Style)Application.Current.Resources["BotaoBase"],
            MinHeight = 104, Margin = new Thickness(3, 3, 3, 4),
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
        var cab = new Grid { Margin = new Thickness(8, 6, 8, 2) };
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var esq = new StackPanel { Orientation = Orientation.Horizontal };
        var numero = new TextBlock
        {
            Text = "#" + t.Numero, FontSize = 18, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        numero.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
        esq.Children.Add(numero);
        esq.Children.Add(Chip(t.Origem == "ifood" ? "iFOOD" : "BALCÃO",
                              t.Origem == "ifood" ? "Ciano" : "Rosa"));
        cab.Children.Add(esq);

        // O relógio é O MESMO do Gestor do iFood: o PRAZO (dueAt). "12 min"
        // = falta isso pro prometido; "+3 min" = estourou. Pedido sem prazo
        // (balcão) volta ao decorrido. Dois painéis, um relógio só.
        string txtEspera; string corEspera;
        if (t.PrazoRestante is { } prazo)
        {
            var m = (int)prazo.TotalMinutes;
            if (m >= 0)
            {
                txtEspera = $"{m} min";
                corEspera = m > 5 ? "Ok" : "Amarelo";
            }
            else
            {
                txtEspera = $"+{-m} min";
                corEspera = "Erro";
            }
        }
        else
        {
            var min = (int)t.Espera.TotalMinutes;
            txtEspera = min < 1 ? "agora" : $"{min} min";
            corEspera = min < 10 ? "Ok" : min < 20 ? "Amarelo" : "Erro";
        }
        var espera = new TextBlock
        {
            Text = txtEspera,
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
            // 44px perdia pro dedo: o toque pegava o CARD e abria o pop-up
            // de PRONTO. Alvo de verdade (76×56) + texto — o padrão da casa é 64px.
            var desfaz = new Button
            {
                Content = "↩", FontSize = 13, MinHeight = 40, MinWidth = 40,
                Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(8, 0, 8, 0),
                Style = (Style)Application.Current.Resources["BotaoBase"],
                ToolTip = "Devolver para A PREPARAR",
            };
            desfaz.Click += (_, e) =>
            {
                Nucleo.Kds.Desassumir(t.Id);
                Pintar();
                // Click e roteado e BORBULHA: sem isto o clique segue pro card e
                // abre a confirmacao de PRONTO - exatamente o bug que o dono viu.
                e.Handled = true;
            };
            dir.Children.Add(desfaz);
        }
        if (t.Origem == "ifood")
        {
            // Reimprimir a comanda: papel atolou/acabou, ou a automática falhou.
            // Imprime DIRETO (sem claim — reimpressão é decisão de gente).
            var imprime = new Button
            {
                Content = "🖨", FontSize = 14, MinHeight = 40, MinWidth = 40,
                Margin = new Thickness(10, 0, 0, 0),
                Style = (Style)Application.Current.Resources["BotaoBase"],
                ToolTip = "Imprimir a comanda deste pedido",
            };
            imprime.Click += async (_, e) =>
            {
                e.Handled = true; // não deixa o clique borbulhar e avançar etapa
                string? imp;
                using (var cx = Banco.Abrir())
                {
                    imp = Vendas.Config(cx, "kds_comanda_impressora");
                    if (imp is { Length: 0 }) imp = null;
                }
                var erro = await Impressao.ImprimirTextoAsync(
                    $"Comanda cozinha #{t.Numero} (manual)",
                    new[] { Nucleo.Kds.ComandaLinhas(t) }, imp);
                TxtStatus.Text = erro is null
                    ? $"comanda #{t.Numero} impressa"
                    : $"comanda #{t.Numero} NÃO imprimiu: {erro}";
            };
            dir.Children.Add(imprime);
        }
        Grid.SetColumn(dir, 1);
        cab.Children.Add(dir);
        raiz.Children.Add(cab);

        // ── corpo: cliente + itens ──────────────────────────────────────────
        var corpo = new StackPanel { Margin = new Thickness(8, 0, 8, 3) };
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
                Text = $"{qtd}× {i.Descricao}", FontSize = 13,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1),
            };
            linha.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
            corpo.Children.Add(linha);
            // As escolhas do combo aparecem aqui pelo mesmo motivo da comanda: sem
            // elas o card diz "1x Combo Box 4un" e o cozinheiro não sabe o que fazer.
            if (i.Escolhas is { Count: > 0 })
                foreach (var esc in i.Escolhas)
                {
                    var sub = new TextBlock
                    {
                        Text = "    - " + esc, FontSize = 13,
                        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 1),
                    };
                    sub.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
                    corpo.Children.Add(sub);
                }
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
        b.Click += (_, e) =>
        {
            // CINTO além do e.Handled do botão interno: Click borbulha, e o
            // Source denuncia de onde o clique nasceu — clique que não nasceu
            // NO card não avança etapa. (O dono viu o desfazer abrir o pop-up
            // de PRONTO em tela touch; nunca mais.)
            if (!ReferenceEquals(e.Source, b)) return;
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
                        ? $"O pedido #{t.Numero} vai constar como PRONTO para coleta no iFood " +
                          "e o entregador VAI ser acionado. Confirma que está tudo embalado?"
                        : $"Marcar o pedido #{t.Numero} como PRONTO para entrega ao cliente?";
                    if (Dialogo.Confirmar(dono, "Pedido pronto?", aviso,
                                          "Sim, está pronto", "Ainda não"))
                        Nucleo.Kds.Liberar(t.Id);
                    break;

                case Nucleo.Kds.Pronto:
                    // balcao: cliente retirou no caixa. iFood: nada — a API manda.
                    if (t.Origem != "ifood") Nucleo.Kds.Entregar(t.Id);
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
