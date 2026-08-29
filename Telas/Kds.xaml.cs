using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// O quadro de preparo do quiosque, no fluxo do KDS da Savassi:
///
///   NA FILA → FAZENDO → PRONTO → (entregue, sai)
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
    private bool _puxando;

    public Kds(string loja)
    {
        InitializeComponent();
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
            // Plural escrito à mão: "3 novo(s)" no quadro é a nossa preguiça
            // aparecendo na parede da cozinha.
            var chegaram = novos switch
            {
                0 => "",
                1 => " · 1 pedido novo",
                _ => $" · {novos} pedidos novos",
            };
            TxtStatus.Text = $"Conectado · {DateTime.Now:HH:mm:ss}{chegaram}";
            if (novos > 0) Alerta.PedidoNovo();
        }
        catch
        {
            // O que muda pra quem está na cozinha: pedido de delivery para de
            // entrar. O quadro em si continua igual — por isso não é "erro".
            TxtStatus.Text = "Sem internet — pedido do delivery não entra. " +
                             $"Confira o wi-fi. {DateTime.Now:HH:mm:ss}";
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

    /// <summary>
    /// Delega para <see cref="Servicos.ImprimirComandasPendentesAsync"/> — a
    /// impressao nao mora mais aqui porque nao pode depender do quadro aberto.
    /// A tela so mostra o aviso quando o papel nao sai.
    /// </summary>
    private async Task ImprimirComandasAsync()
    {
        var falha = await Servicos.ImprimirComandasPendentesAsync();
        if (falha is null) return;
        TxtStatus.Text = falha + " — confira papel e impressora e toque no 🖨 do pedido.";
        Alerta.PedidoNovo();   // chama atencao: papel nao saiu
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
    /// Quantas colunas de card cabem DENTRO de cada coluna de status.
    /// </summary>
    private const int CardsPorLinha = 2;

    /// <summary>
    /// Distribui os cards num Grid de <see cref="CardsPorLinha"/> colunas.
    ///
    /// Grid e nao WrapPanel: no WrapPanel, alinhar os cards exigia altura FIXA —
    /// e altura fixa CORTA pedido comprido (o de 6 itens aparecia com 2). Aqui
    /// cada LINHA cresce ate o maior card dela e os vizinhos esticam junto:
    /// alinhado em cima e embaixo, sem esconder item nenhum.
    /// </summary>
    private void Encher(Grid coluna, IEnumerable<Ticket> tickets)
    {
        coluna.Children.Clear();
        coluna.RowDefinitions.Clear();
        coluna.ColumnDefinitions.Clear();
        for (var c = 0; c < CardsPorLinha; c++)
            coluna.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var i = 0;
        foreach (var t in tickets)
        {
            var linha = i / CardsPorLinha;
            if (linha >= coluna.RowDefinitions.Count)
                coluna.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var card = Card(t);
            Grid.SetRow(card, linha);
            Grid.SetColumn(card, i % CardsPorLinha);
            coluna.Children.Add(card);
            i++;
        }
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
            Nucleo.Kds.Preparando => ("TOQUE QUANDO FICAR PRONTO", "Ok", "ChipOkFundo"),
            // "sai sozinho" evita o operador caçando um botão que não existe:
            // este card só some quando o entregador declara a coleta lá fora.
            Nucleo.Kds.Pronto when t.Origem == "ifood"
                                  => ("ESPERANDO O ENTREGADOR · sai sozinho", "TextoFraco", "VeuElevado"),
            Nucleo.Kds.Pronto     => ("TOQUE QUANDO O CLIENTE LEVAR", "Texto", "VeuElevado"),
            _                     => ("TOQUE PARA COMEÇAR", "Amarelo", "ChipAlertaFundo"),
        };

        var b = new Button
        {
            Style = (Style)Application.Current.Resources["BotaoBase"],
            MinHeight = 116, Margin = new Thickness(3, 3, 3, 4),
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
        var cab = new Grid { Margin = new Thickness(11, 8, 11, 3) };
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var esq = new StackPanel { Orientation = Orientation.Horizontal };
        var numero = new TextBlock
        {
            Text = "#" + t.Numero, FontSize = 23, FontWeight = FontWeights.Bold,
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
                Content = "↩", FontSize = 15, MinHeight = 46, MinWidth = 46,
                Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(8, 0, 8, 0),
                Style = (Style)Application.Current.Resources["BotaoBase"],
                ToolTip = "Devolver para a fila",
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
                Content = "🖨", FontSize = 16, MinHeight = 46, MinWidth = 46,
                Margin = new Thickness(10, 0, 0, 0),
                Style = (Style)Application.Current.Resources["BotaoBase"],
                ToolTip = "Tirar a comanda deste pedido no papel de novo",
            };
            imprime.Click += async (_, e) =>
            {
                e.Handled = true; // não deixa o clique borbulhar e avançar etapa
                // MESMO destino da comanda automática (Servicos.DestinoDaComanda): a
                // reimpressão tem que sair na bobina em que a original sairia, senão o
                // 🖨 vira "saiu, mas noutra impressora" — que é pior que não sair.
                Impressao.Destino destino;
                using (var cx = Banco.Abrir()) destino = Servicos.DestinoDaComanda(cx);
                var erro = await Impressao.ImprimirTextoAsync(
                    $"Comanda cozinha #{t.Numero} (manual)",
                    new[] { Nucleo.Kds.ComandaLinhas(t, Nucleo.Kds.ColunasComanda(destino.Papel.Colunas)) },
                    destino);
                // Na falha, primeiro o que fazer; a causa técnica vai no fim,
                // entre parênteses, pra quem for atrás da impressora.
                TxtStatus.Text = erro is null
                    ? $"Comanda do #{t.Numero} saiu na impressora"
                    : $"A comanda do #{t.Numero} não saiu — confira papel e impressora " +
                      $"e toque no 🖨 de novo. ({erro})";
            };
            dir.Children.Add(imprime);
        }
        Grid.SetColumn(dir, 1);
        cab.Children.Add(dir);
        raiz.Children.Add(cab);

        // ── corpo: cliente + itens ──────────────────────────────────────────
        var corpo = new StackPanel { Margin = new Thickness(11, 0, 11, 4) };
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
                Text = $"{qtd}× {i.Descricao}", FontSize = 16,
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
                        Text = "    - " + esc, FontSize = 14,
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
                    var delivery = t.Origem == "ifood";
                    // O botão diz a consequência, não "Sim": quem toca sem ler
                    // ainda lê "Chamar o entregador" no dedo.
                    var aviso = delivery
                        ? $"O pedido #{t.Numero} vai para coleta e o entregador é chamado agora. " +
                          "Já está tudo embalado?"
                        : $"O pedido #{t.Numero} já pode ir para o cliente?";
                    if (Dialogo.Confirmar(dono, "Pedido pronto", aviso,
                                          delivery ? "Chamar entregador" : "Marcar pronto",
                                          "Ainda não"))
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
