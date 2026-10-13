using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// O DIALOGO "COMANDAS" (13/10/2026): a lista das comandas abertas e a face "Nova comanda".
/// Aberto pelo botao Comandas do cabecalho da coluna da direita (Venda.Comanda.cs).
///
/// Duas faces na mesma janela (molde Dialogo.Base + Dialogo.Moldura, largura 640, botoes de 58 px,
/// fonte 17):
///  · LISTA, so quando ha o que listar: uma linha por comanda aberta do modo atual, "12 · Mesa 5" |
///    "3 itens · R$ 45,00" | "cozinha 2 de 3" | selo ("na tela", "na TV", "⚠ fila", "⚠ não foi" com
///    [Mandar de novo], "⚠ confira a maquininha", "em uso no outro caixa"). Rodape: [Nova comanda]
///    [Cancelar comanda 12] (so com conta na tela) [Voltar]. Rola a partir de 8 linhas.
///  · NOVA: chips "Mesa 1" a "Mesa N" e "Balcão" (um toque preenche o nome), campo "Nome ou mesa"
///    (teclado virtual no caixa touch), "Na loja" / "Para viagem", a linha "O carrinho tem 3 itens."
///    e [Abrir com os 3 itens] [Abrir vazia] ou [Abrir], mais [Voltar]. Com ZERO abertas o dialogo
///    abre direto nesta face.
///
/// O dialogo NAO decide nada: devolve uma <see cref="Decisao"/> e quem age e a tela de venda (nome
/// duplicado, carrinho cheio, PIN do cancelamento). A unica acao daqui de dentro e "Mandar de novo",
/// que repinta a lista. Os textos vem de ContasAbertas.Textos; as linhas de ContasAbertas.LinhasDaLista.
/// </summary>
public static class DialogoComandas
{
    private static Brush R(string chave) => (Brush)Application.Current.Resources[chave];

    /// <summary>O que o operador decidiu. Null = Voltar.</summary>
    public abstract record Decisao;
    /// <summary>Toque numa linha: carregar esta conta (a que ja esta na tela so fecha o dialogo).</summary>
    public sealed record Carregar(string ContaId) : Decisao;
    /// <summary>"Abrir": conta nova com nome (ou sem), consumo e, se pedido, o carrinho solto.</summary>
    public sealed record Nova(string? Nome, DestinoCozinha Consumo, bool ComCarrinho) : Decisao;
    /// <summary>"Cancelar comanda N" da conta na tela.</summary>
    public sealed record CancelarConta(string ContaId) : Decisao;

    /// <summary>Largura da janela; os botoes do rodape dividem o que sobra da moldura.</summary>
    public const int Largura = 640;
    /// <summary>A lista rola a partir desta linha (a 1024x768 cabem 7 inteiras).</summary>
    public const int LinhasSemRolagem = 7;

    /// <param name="linhas">Le as linhas de novo a cada pintura (depois de Mandar de novo).</param>
    /// <param name="mesas">Quantos chips "Mesa N" (config mesas_quantidade).</param>
    /// <param name="itensNoCarrinho">Itens do carrinho SOLTO (zero com conta na tela).</param>
    /// <param name="naTela">A conta carregada, se houver (o rodape ganha Cancelar comanda N).</param>
    /// <param name="mandarDeNovo">Reenvia um lote que nao foi; devolve o erro ou null.</param>
    public static Decisao? Mostrar(Window dono, Func<IReadOnlyList<LinhaDaLista>> linhas, int mesas,
        int itensNoCarrinho, ContaAberta? naTela, Func<string, string?> mandarDeNovo)
    {
        Decisao? resultado = null;
        var janela = Dialogo.Base(dono, Largura);
        var alturaDono = dono.ActualHeight > 0 ? dono.ActualHeight : SystemParameters.PrimaryScreenHeight;
        janela.MaxHeight = Math.Max(400, alturaDono * 0.92);
        var moldura = Dialogo.Moldura(new StackPanel());
        janela.Content = moldura;
        var atuais = linhas();

        void Fechar(Decisao? d) { resultado = d; janela.Close(); }

        // ── face LISTA ──────────────────────────────────────────────────────
        void MostrarLista()
        {
            atuais = linhas();
            var pilha = new StackPanel();
            pilha.Children.Add(Titulo(ContasAbertas.Textos.Comandas));

            var itens = new StackPanel();
            foreach (var l in atuais) itens.Children.Add(Linha(l, janela, mandarDeNovo, Fechar, MostrarLista));
            var rolagem = new ScrollViewer
            {
                Content = itens, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly,
                MaxHeight = Math.Max(200, janela.MaxHeight - 210),
            };
            AutomationProperties.SetName(rolagem, "ListaComandas");
            pilha.Children.Add(rolagem);

            var rodape = new UniformGrid { Rows = 1, Margin = new Thickness(0, 14, 0, 0) };
            var nova = Botao(ContasAbertas.Textos.NovaComanda, true);
            nova.Click += (_, _) => MostrarNova();
            rodape.Children.Add(nova);
            if (naTela is not null)
            {
                var cancelar = Botao(ContasAbertas.Textos.CancelarComanda(naTela.Numero), false, R("Erro"));
                cancelar.Foreground = R("SobreMarca");
                cancelar.Margin = new Thickness(8, 0, 0, 0);
                cancelar.Click += (_, _) => Fechar(new CancelarConta(naTela.Id));
                rodape.Children.Add(cancelar);
            }
            var voltar = Botao("Voltar", false);
            voltar.Margin = new Thickness(8, 0, 0, 0);
            voltar.Click += (_, _) => Fechar(null);
            rodape.Children.Add(voltar);
            rodape.Columns = rodape.Children.Count;
            pilha.Children.Add(rodape);
            moldura.Child = pilha;
        }

        // ── face NOVA ───────────────────────────────────────────────────────
        void MostrarNova()
        {
            var pilha = new StackPanel();
            pilha.Children.Add(Titulo(ContasAbertas.Textos.NovaComanda));

            var caixa = new TextBox
            {
                Tag = ContasAbertas.Textos.NomeOuMesa, FontSize = 18, MinHeight = 54, MaxLength = 60,
                Margin = new Thickness(0, 6, 0, 0),
            };
            AutomationProperties.SetName(caixa, ContasAbertas.Textos.NomeOuMesa);

            // chips: um toque preenche o nome (Mesa 1 a Mesa N e Balcão)
            var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 0) };
            for (var i = 1; i <= mesas; i++) chips.Children.Add(Chip(ContasAbertas.Textos.Mesa(i), caixa));
            chips.Children.Add(Chip(ContasAbertas.Textos.Balcao, caixa));
            pilha.Children.Add(chips);
            pilha.Children.Add(caixa);

            // consumo: Na loja (padrao) ou Para viagem
            var consumo = DestinoCozinha.Loja;
            var linhaConsumo = new UniformGrid { Rows = 1, Columns = 2, Margin = new Thickness(0, 10, 0, 0) };
            var naLoja = Botao(ContasAbertas.Textos.NaLoja, true);
            var viagem = Botao(ContasAbertas.Textos.ParaViagem, false);
            naLoja.Margin = new Thickness(0, 0, 4, 0);
            viagem.Margin = new Thickness(4, 0, 0, 0);
            naLoja.MinHeight = viagem.MinHeight = 52;
            void PintarConsumo()
            {
                Marcar(naLoja, consumo == DestinoCozinha.Loja);
                Marcar(viagem, consumo == DestinoCozinha.Viagem);
            }
            naLoja.Click += (_, _) => { consumo = DestinoCozinha.Loja; PintarConsumo(); };
            viagem.Click += (_, _) => { consumo = DestinoCozinha.Viagem; PintarConsumo(); };
            PintarConsumo();
            linhaConsumo.Children.Add(naLoja);
            linhaConsumo.Children.Add(viagem);
            pilha.Children.Add(linhaConsumo);

            if (itensNoCarrinho > 0)
            {
                var aviso = new TextBlock
                {
                    Text = ContasAbertas.Textos.CarrinhoTem(itensNoCarrinho), FontSize = 15, FontWeight = FontWeights.SemiBold,
                    Foreground = R("Texto"), Margin = new Thickness(0, 14, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                };
                AutomationProperties.SetName(aviso, "Carrinho");
                pilha.Children.Add(aviso);
            }

            string? Nome() => string.IsNullOrWhiteSpace(caixa.Text) ? null : caixa.Text.Trim();
            var rodape = new UniformGrid { Rows = 1, Margin = new Thickness(0, 14, 0, 0) };
            if (itensNoCarrinho > 0)
            {
                var comItens = Botao(ContasAbertas.Textos.AbrirComItens(itensNoCarrinho), true);
                comItens.Click += (_, _) => Fechar(new Nova(Nome(), consumo, ComCarrinho: true));
                rodape.Children.Add(comItens);
                var vazia = Botao(ContasAbertas.Textos.AbrirVazia, false);
                vazia.Margin = new Thickness(8, 0, 0, 0);
                vazia.Click += (_, _) => Fechar(new Nova(Nome(), consumo, ComCarrinho: false));
                rodape.Children.Add(vazia);
            }
            else
            {
                var abrir = Botao(ContasAbertas.Textos.Abrir, true);
                abrir.Click += (_, _) => Fechar(new Nova(Nome(), consumo, ComCarrinho: false));
                rodape.Children.Add(abrir);
            }
            var voltar = Botao("Voltar", false);
            voltar.Margin = new Thickness(8, 0, 0, 0);
            // com lista, Voltar volta para ela; sem lista (abriu direto aqui), Voltar fecha
            voltar.Click += (_, _) => { if (atuais.Count > 0) MostrarLista(); else Fechar(null); };
            rodape.Children.Add(voltar);
            rodape.Columns = rodape.Children.Count;
            pilha.Children.Add(rodape);

            caixa.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                // Enter = o botao principal (Abrir, ou Abrir com os itens)
                ((Button)rodape.Children[0]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            };
            moldura.Child = pilha;
            // o teclado virtual, so no caixa touch (sem teclado fisico), depois de a face assentar
            janela.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, (Action)(() =>
            {
                caixa.Focus();
                PedirTexto.AbrirTecladoVirtualSeTouch();
            }));
        }

        if (atuais.Count == 0) MostrarNova(); else MostrarLista();
        janela.KeyDown += (_, e) => { if (e.Key == Key.Escape) Fechar(null); };
        janela.ShowDialog();
        return resultado;
    }

    /// <summary>
    /// Uma linha da lista, como a tela desenha: titulo em negrito, itens e valor, a contagem da
    /// cozinha, o selo e, no lote que nao foi, o botao Mandar de novo. Separado para a suite medir.
    /// </summary>
    internal static Button Linha(LinhaDaLista l, Window? janela, Func<string, string?> mandarDeNovo,
        Action<Decisao?> fechar, Action repintar)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.Resources["BotaoBase"],
            MinHeight = 58, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(12, 0, 10, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsEnabled = !l.EmUso,
            Tag = l.Conta.Id,
        };
        if (l.NaTela)
        {
            b.SetResourceReference(Control.BackgroundProperty, "ChipInfoFundo");
            b.SetResourceReference(Control.BorderBrushProperty, "ChipInfoBorda");
        }
        AutomationProperties.SetName(b, $"Comanda {l.Conta.Numero}");

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titulo = new TextBlock
        {
            Text = l.Titulo, FontSize = 17, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 190, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 12, 0),
        };
        titulo.SetResourceReference(TextBlock.ForegroundProperty, l.NaTela ? "Ciano" : "Texto");
        AutomationProperties.SetName(titulo, "Titulo");
        Grid.SetColumn(titulo, 0);
        g.Children.Add(titulo);

        var itens = new TextBlock
        {
            Text = l.Itens, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0),
        };
        itens.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
        AutomationProperties.SetName(itens, "Itens");
        Grid.SetColumn(itens, 1);
        g.Children.Add(itens);

        if (l.Cozinha.Length > 0)
        {
            var cozinha = new TextBlock
            {
                Text = l.Cozinha, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            };
            cozinha.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
            AutomationProperties.SetName(cozinha, "Cozinha");
            Grid.SetColumn(cozinha, 2);
            g.Children.Add(cozinha);
        }

        if (l.Selo.Length > 0)
        {
            var alerta = l.Selo.StartsWith("⚠", StringComparison.Ordinal);
            var chip = new Border
            {
                CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 2, 8, 3), BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            chip.SetResourceReference(Border.BackgroundProperty, alerta ? "ChipAlertaFundo" : l.NaTela ? "ChipInfoFundo" : "ChipOkFundo");
            chip.SetResourceReference(Border.BorderBrushProperty, alerta ? "ChipAlertaBorda" : l.NaTela ? "ChipInfoBorda" : "ChipOkBorda");
            var txt = new TextBlock { Text = l.Selo, FontSize = 12, FontWeight = FontWeights.Bold };
            txt.SetResourceReference(TextBlock.ForegroundProperty, alerta ? "Amarelo" : l.NaTela ? "Ciano" : "Ok");
            AutomationProperties.SetName(txt, "Selo");
            chip.Child = txt;
            Grid.SetColumn(chip, 3);
            g.Children.Add(chip);
        }

        if (l.LoteParaMandarDeNovo is { } loteId)
        {
            var reenviar = new Button
            {
                Content = ContasAbertas.Textos.MandarDeNovo, Style = (Style)Application.Current.Resources["BotaoPrincipal"],
                MinHeight = 40, FontSize = 13, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(reenviar, $"{ContasAbertas.Textos.MandarDeNovo} {l.Conta.Numero}");
            reenviar.Click += (_, e) =>
            {
                e.Handled = true;
                var erro = mandarDeNovo(loteId);
                if (erro is not null && janela is not null) Dialogo.Avisar(janela, ContasAbertas.Textos.MandarDeNovo, erro, "erro");
                repintar();
            };
            Grid.SetColumn(reenviar, 4);
            g.Children.Add(reenviar);
        }

        b.Content = g;
        b.Click += (_, _) => fechar(new Carregar(l.Conta.Id));
        return b;
    }

    private static TextBlock Titulo(string texto) => new()
    {
        Text = texto, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = R("Texto"),
        Margin = new Thickness(0, 0, 0, 12),
    };

    /// <summary>Chip de mesa: 46 px de alto, um toque preenche o nome.</summary>
    private static Button Chip(string texto, TextBox alvo)
    {
        var b = new Button
        {
            Content = texto, Style = (Style)Application.Current.Resources["BotaoBase"],
            MinHeight = 46, Height = 46, FontSize = 15, Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 0, 6, 6),
        };
        AutomationProperties.SetName(b, texto);
        b.Click += (_, _) => { alvo.Text = texto; alvo.CaretIndex = alvo.Text.Length; };
        return b;
    }

    private static void Marcar(Button b, bool marcado)
    {
        b.Style = (Style)Application.Current.Resources[marcado ? "BotaoPrincipal" : "BotaoBase"];
        b.Background = marcado ? R("RosaDegrade") : R("PainelAlto");
    }

    /// <summary>Mesmo alvo de toque dos outros dialogos da casa (58 px, fonte 17).</summary>
    private static Button Botao(string texto, bool destaque, Brush? cor = null)
    {
        var b = new Button
        {
            Content = texto,
            Style = (Style)Application.Current.Resources[destaque ? "BotaoPrincipal" : "BotaoBase"],
            MinHeight = 58, FontSize = 17, Padding = new Thickness(8, 0, 8, 0),
            Background = cor ?? (destaque ? R("RosaDegrade") : R("PainelAlto")),
        };
        AutomationProperties.SetName(b, texto);
        return b;
    }
}
