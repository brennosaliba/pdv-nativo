using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// O PAINEL de detalhe do pedido, montado a partir de <see cref="DetalhePedido"/>
/// (que é onde mora a lógica; aqui é só pintura). Vive dentro do véu do quadro
/// (Kds.xaml), nunca numa janela à parte.
///
/// LEGÍVEL A UM METRO: o item sai a 27 px (o card usa 16), a quantidade num círculo
/// de 46 px e a observação do item em faixa amarela, porque observação é o que mais
/// erra na cozinha. Sem preço em lugar nenhum.
///
/// TRÊS FAIXAS: cabeçalho fixo (número, cliente, hora, código de coleta, agrupado),
/// a lista de itens que ROLA quando não cabe (a 1024x768 um pedido de dez itens não
/// cabe, e cortar item é pior que rolar) e o botão Fechar, grande, sempre à vista.
///
/// As seções da NUVEM chegam depois de o painel já estar aberto (RPC de detalhe,
/// ~1 s): <see cref="Completar"/> preenche os lugares reservados sem remontar a
/// lista, para o que o operador já estava lendo não pular.
/// </summary>
public sealed class DetalhePedidoKds : Border
{
    /// <summary>O que está na tela agora (com ou sem o complemento da nuvem).</summary>
    public DetalhePedido Detalhe { get; private set; }

    private readonly TextBlock _meta;            // "Feito às 16:53 · Entrega · Localizador …"
    private readonly StackPanel _secoesCab;      // código de coleta, agrupado com
    private readonly StackPanel _obsPedido;      // observação do pedido inteiro, acima dos itens
    private readonly Button _fechar;

    public DetalhePedidoKds(DetalhePedido d, Action fechar)
    {
        Detalhe = d;

        CornerRadius = new CornerRadius(18);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "Painel");
        SetResourceReference(BorderBrushProperty, "Borda");
        Effect = new DropShadowEffect
        {
            BlurRadius = 28, ShadowDepth = 6, Color = Colors.Black,
            Opacity = (double)Application.Current.Resources["SombraDialogoOpacidade"],
        };

        var raiz = new Grid();
        raiz.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        raiz.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        raiz.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── cabeçalho fixo ─────────────────────────────────────────────────
        var cab = new StackPanel { Margin = new Thickness(26, 20, 26, 12) };

        var linha1 = new Grid();
        linha1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        linha1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        linha1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var numero = Texto(d.Numero, 42, FontWeights.Bold, "Texto");
        numero.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(numero, 0);
        linha1.Children.Add(numero);

        var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var canalIfood = d.Canal != "Balcão";
        chips.Children.Add(Chip(d.Canal, canalIfood ? "Ciano" : "Rosa",
                                canalIfood ? "ChipInfoFundo" : "ChipErroFundo",
                                canalIfood ? "ChipInfoBorda" : "ChipErroBorda"));
        if (d.Agendado is not null)
            chips.Children.Add(Chip("AGENDADO", "Agendado", "ChipAgendadoFundo", "ChipAgendadoBorda"));
        Grid.SetColumn(chips, 1);
        linha1.Children.Add(chips);

        // A coluna de onde o card veio, com a cor dela: o detalhe abre de qualquer
        // coluna, e quem olha tem que saber em qual está sem fechar o painel.
        var (corEtapa, fundoEtapa, bordaEtapa) = d.Etapa switch
        {
            "FAZENDO" => ("Amarelo", "ChipAlertaFundo", "ChipAlertaBorda"),
            "PRONTO" => ("Ok", "ChipOkFundo", "ChipOkBorda"),
            _ => ("Ciano", "ChipInfoFundo", "ChipInfoBorda"),
        };
        var etapa = Chip(d.Etapa, corEtapa, fundoEtapa, bordaEtapa);
        etapa.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(etapa, 2);
        linha1.Children.Add(etapa);
        cab.Children.Add(linha1);

        if (d.Cliente is not null)
        {
            var cliente = Texto(d.Cliente, 24, FontWeights.SemiBold, "Texto");
            cliente.Margin = new Thickness(0, 4, 0, 0);
            cab.Children.Add(cliente);
        }

        _meta = Texto("", 17, FontWeights.Normal, "TextoFraco");
        _meta.Margin = new Thickness(0, 6, 0, 0);
        cab.Children.Add(_meta);

        if (d.Agendado is not null)
        {
            var ag = Texto(d.Agendado, 21, FontWeights.Bold, "Agendado");
            ag.Margin = new Thickness(0, 8, 0, 0);
            cab.Children.Add(ag);
        }

        _secoesCab = new StackPanel();
        cab.Children.Add(_secoesCab);
        Grid.SetRow(cab, 0);
        raiz.Children.Add(cab);

        // ── itens: a parte que rola ────────────────────────────────────────
        var lista = new StackPanel { Margin = new Thickness(26, 4, 26, 8) };
        _obsPedido = new StackPanel();
        lista.Children.Add(_obsPedido);
        for (var n = 0; n < d.Itens.Count; n++)
        {
            if (n > 0)
            {
                var risco = new Border { Height = 1, Opacity = 0.45, Margin = new Thickness(0, 2, 0, 2) };
                risco.SetResourceReference(BackgroundProperty, "Borda");
                lista.Children.Add(risco);
            }
            lista.Children.Add(Item(d.Itens[n]));
        }
        var rol = new ScrollViewer
        {
            Content = lista, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var moldura = new Border { BorderThickness = new Thickness(0, 1, 0, 1), Child = rol };
        moldura.SetResourceReference(BorderBrushProperty, "Borda");
        Grid.SetRow(moldura, 1);
        raiz.Children.Add(moldura);

        // ── fechar: grande, sempre à vista ─────────────────────────────────
        _fechar = new Button
        {
            Content = "Fechar",
            Style = (Style)Application.Current.Resources["BotaoPrincipal"],
            MinHeight = 60, FontSize = 20, Margin = new Thickness(26, 12, 26, 20),
        };
        _fechar.Click += (_, _) => fechar();
        Grid.SetRow(_fechar, 2);
        raiz.Children.Add(_fechar);

        Child = raiz;
        PintarNuvem();
    }

    /// <summary>Esc e Enter têm que chegar em alguém: o Fechar é o único foco do painel.</summary>
    public void FocarFechar() => _fechar.Focus();

    /// <summary>A nuvem respondeu: preenche as seções reservadas, sem mexer na lista.</summary>
    public void Completar(DetalhePedido d)
    {
        Detalhe = d;
        PintarNuvem();
    }

    /// <summary>
    /// As partes que dependem do complemento da nuvem. Chamado na abertura (com o
    /// que já havia) e de novo quando a RPC responde. Seção sem dado não existe:
    /// nem rótulo vazio, nem "Localizador: -".
    /// </summary>
    private void PintarNuvem()
    {
        var d = Detalhe;

        // A linha de metadados é UMA frase separada por pontos: hora, entrega ou
        // retirada, prazo, começo, pronto, localizador. Só o que existe entra.
        var partes = new List<string> { d.FeitoAs };
        if (d.Modalidade is not null) partes.Add(d.Modalidade);
        if (d.Prazo is not null) partes.Add(d.Prazo);
        if (d.Comecou is not null) partes.Add(d.Comecou);
        if (d.ProntoAs is not null) partes.Add(d.ProntoAs);
        if (d.Localizador is not null) partes.Add("Localizador " + d.Localizador);
        _meta.Text = string.Join(" · ", partes);

        _secoesCab.Children.Clear();
        if (d.CodigoColeta is not null)
        {
            // Em destaque: é o que o motoboy diz no balcão, e é o que a loja confere
            // antes de entregar a sacola. Rótulo pequeno, código grande.
            var faixa = Faixa("ChipInfoFundo", "ChipInfoBorda");
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var rotulo = Texto("Código de coleta", 16, FontWeights.SemiBold, "TextoFraco");
            rotulo.VerticalAlignment = VerticalAlignment.Center;
            var codigo = Texto(d.CodigoColeta, 30, FontWeights.Bold, "Ciano");
            codigo.Margin = new Thickness(16, 0, 0, 0);
            codigo.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(codigo, 1);
            g.Children.Add(rotulo);
            g.Children.Add(codigo);
            faixa.Child = g;
            _secoesCab.Children.Add(faixa);
        }
        if (d.AgrupadoTexto is { } agrupado)
        {
            var faixa = Faixa("EsperaFundo", "Borda");
            faixa.Child = Texto(agrupado, 18, FontWeights.SemiBold, "TextoEspera");
            _secoesCab.Children.Add(faixa);
        }

        _obsPedido.Children.Clear();
        if (d.Observacoes is not null)
        {
            var obs = Observacao("Obs. do pedido: " + d.Observacoes, 20);
            obs.Margin = new Thickness(0, 8, 0, 6);
            _obsPedido.Children.Add(obs);
        }
    }

    /// <summary>Um item: quantidade no círculo, nome grande, o de dentro do combo e a observação.</summary>
    private static FrameworkElement Item(ItemDetalhe i)
    {
        var linha = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Círculo invertido (texto sobre fundo de texto): é o contraste mais alto que
        // o tema tem, nos dois temas, e a quantidade é o que o olho procura primeiro.
        var circulo = new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(23),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0),
        };
        circulo.SetResourceReference(BackgroundProperty, "Texto");
        var qtd = Texto(i.Qtd, i.Qtd.Length > 2 ? 16 : 22, FontWeights.Bold, "Painel");
        qtd.HorizontalAlignment = HorizontalAlignment.Center;
        qtd.VerticalAlignment = VerticalAlignment.Center;
        circulo.Child = qtd;
        linha.Children.Add(circulo);

        var direita = new StackPanel { Margin = new Thickness(14, 4, 0, 0) };
        direita.Children.Add(Texto(i.Nome, 27, FontWeights.SemiBold, "Texto"));

        if (i.Escolhas.Count > 0)
        {
            // A mesma régua do card: a linha vertical amarra o grupo ao item de cima.
            var dentro = new StackPanel();
            foreach (var e in i.Escolhas)
            {
                var sub = new TextBlock
                {
                    FontSize = 21, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2),
                };
                if (e.Qtd.Length > 0)
                    sub.Inlines.Add(new Run(e.Qtd + " ") { FontWeight = FontWeights.SemiBold });
                sub.Inlines.Add(new Run(e.Nome));
                sub.SetResourceReference(TextBlock.ForegroundProperty, "TextoSubItem");
                dentro.Children.Add(sub);
            }
            var regua = new Border
            {
                BorderThickness = new Thickness(2, 0, 0, 0),
                Margin = new Thickness(2, 5, 0, 2), Padding = new Thickness(10, 0, 0, 0),
                Child = dentro,
            };
            regua.SetResourceReference(BorderBrushProperty, "TextoFraco");
            direita.Children.Add(regua);
        }

        if (i.Observacao is not null)
        {
            var obs = Observacao("Obs: " + i.Observacao, 20);
            obs.Margin = new Thickness(0, 7, 0, 2);
            direita.Children.Add(obs);
        }

        Grid.SetColumn(direita, 1);
        linha.Children.Add(direita);
        return linha;
    }

    /// <summary>Observação em faixa amarela: é a instrução que a cozinha não pode perder.</summary>
    private static Border Observacao(string texto, double tamanho)
    {
        var faixa = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 6, 12, 7),
            BorderThickness = new Thickness(1),
        };
        faixa.SetResourceReference(BackgroundProperty, "AvisoFundo");
        faixa.SetResourceReference(BorderBrushProperty, "AvisoBorda");
        faixa.Child = Texto(texto, tamanho, FontWeights.Bold, "Amarelo");
        return faixa;
    }

    private static Border Faixa(string fundo, string borda)
    {
        var faixa = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 8, 16, 9),
            Margin = new Thickness(0, 10, 0, 0), BorderThickness = new Thickness(1),
        };
        faixa.SetResourceReference(BackgroundProperty, fundo);
        faixa.SetResourceReference(BorderBrushProperty, borda);
        return faixa;
    }

    private static TextBlock Texto(string texto, double tamanho, FontWeight peso, string cor)
    {
        var tb = new TextBlock
        {
            Text = texto, FontSize = tamanho, FontWeight = peso, TextWrapping = TextWrapping.Wrap,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, cor);
        return tb;
    }

    private static Border Chip(string texto, string cor, string fundo, string borda)
    {
        var tb = new TextBlock { Text = texto, FontSize = 13, FontWeight = FontWeights.Bold };
        tb.SetResourceReference(TextBlock.ForegroundProperty, cor);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 2, 9, 3),
            Margin = new Thickness(0, 0, 6, 0), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center, Child = tb,
        };
        chip.SetResourceReference(BackgroundProperty, fundo);
        chip.SetResourceReference(BorderBrushProperty, borda);
        return chip;
    }
}
