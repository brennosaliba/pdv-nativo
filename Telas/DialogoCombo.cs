using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// O dialogo dos sabores do combo (05/09/2026). Abre ao tocar num produto-combo e
/// devolve as escolhas, ou null se o operador voltou.
///
/// Um bloco por grupo: cabecalho de uma linha ("Donuts · 7 de 10") com barra fina,
/// cards em grade (toque = +1, com −/+ e contador no card), atalho "Tudo igual"
/// quando ha um unico sabor marcado. "Adicionar" so liga com todos os grupos no
/// minimo; enquanto falta, o rodape diz "Faltam 3 donuts". Em 1024 o dialogo ocupa
/// 90% da largura; os grupos rolam, titulo e rodape ficam fixos.
///
/// Reabertura depois de uma republicacao (os ids dos grupos mudaram no painel): a
/// escolha e realocada pela fonte; a que nenhum grupo aceita aparece no bloco
/// "Fora do combo" com o botao Tirar, e o Adicionar so liga depois de resolvida.
///
/// Toda a regra (minimo, maximo, tudo igual, textos) mora em <see cref="Combos.Estado"/>;
/// aqui so se desenha e se repinta.
/// </summary>
public static class DialogoCombo
{
    private static Brush R(string chave) => (Brush)Application.Current.Resources[chave];

    public static List<Escolha>? Abrir(Window dono, Combos.ComboDef def,
        IReadOnlyList<Combos.ProdutoLocal> catalogo, IReadOnlyList<Escolha>? atual = null)
    {
        var estado = new Combos.Estado(def, atual, catalogo);
        var larguraDono = dono.ActualWidth > 0 ? dono.ActualWidth : SystemParameters.PrimaryScreenWidth;
        var alturaDono = dono.ActualHeight > 0 ? dono.ActualHeight : SystemParameters.PrimaryScreenHeight;
        var largura = (int)Math.Min(960, Math.Max(460, larguraDono * 0.9));
        var janela = Dialogo.Base(dono, largura);
        janela.MaxHeight = Math.Max(400, alturaDono * 0.92);
        List<Escolha>? resultado = null;

        var raiz = new Grid();
        raiz.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // titulo
        raiz.RowDefinitions.Add(new RowDefinition());                               // grupos (rolam)
        raiz.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // rodape

        var titulo = new TextBlock
        {
            Text = Combos.Titulo(def), FontSize = 22, FontWeight = FontWeights.Bold,
            Foreground = R("Texto"), TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 10),
        };
        AutomationProperties.SetName(titulo, "TituloCombo");
        Grid.SetRow(titulo, 0);
        raiz.Children.Add(titulo);

        var pilha = new StackPanel();
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = pilha, PanningMode = PanningMode.VerticalOnly,
        };
        Grid.SetRow(scroll, 1);
        raiz.Children.Add(scroll);

        // ── rodape: "Faltam 3 donuts" | Voltar | Adicionar ─────────────────
        var rodape = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        rodape.ColumnDefinitions.Add(new ColumnDefinition());
        rodape.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rodape.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var txtFaltam = new TextBlock
        {
            FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = R("Erro"),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 12, 0),
        };
        AutomationProperties.SetName(txtFaltam, "Faltam");
        Grid.SetColumn(txtFaltam, 0);
        rodape.Children.Add(txtFaltam);

        var voltar = Botao("Voltar", false);
        voltar.MinWidth = 130;
        voltar.Margin = new Thickness(0, 0, 8, 0);
        voltar.Click += (_, _) => janela.Close();
        Grid.SetColumn(voltar, 1);
        rodape.Children.Add(voltar);

        var adicionar = Botao("Adicionar", true);
        adicionar.MinWidth = 170;
        adicionar.Click += (_, _) => { resultado = estado.Escolhas(); janela.Close(); };
        Grid.SetColumn(adicionar, 2);
        rodape.Children.Add(adicionar);
        Grid.SetRow(rodape, 2);
        raiz.Children.Add(rodape);

        // ── grupos ──────────────────────────────────────────────────────────
        // 5 colunas a partir de 900 px (o dialogo de 1024 ja tem 921): com 20 sabores
        // numa categoria, cabem 5 linhas na tela em vez de 4
        var colunas = largura >= 900 ? 5 : largura >= 700 ? 4 : 3;
        var cards = new List<Card>();
        var cabecalhos = new List<(Combos.GrupoDef g, TextBlock progresso, ColumnDefinition cheia, ColumnDefinition vazia, Button tudoIgual)>();

        // ── "Fora do combo": escolhas que nenhum grupo aceita (composicao mudou) ──
        // O operador tira (botao) ou troca (marca outro sabor); enquanto houver uma,
        // o Adicionar fica desligado. Bloco pequeno, reescrito a cada Atualizar().
        var fora = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        pilha.Children.Add(fora);
        void PintarFora()
        {
            fora.Children.Clear();
            fora.Visibility = estado.ForaDoCombo.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (estado.ForaDoCombo.Count == 0) return;
            fora.Children.Add(new TextBlock
            {
                Text = "Fora do combo", FontSize = 16, FontWeight = FontWeights.SemiBold,
                Foreground = R("Erro"), Margin = new Thickness(0, 0, 0, 6),
            });
            foreach (var e in estado.ForaDoCombo)
            {
                var linha = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                linha.ColumnDefinitions.Add(new ColumnDefinition());
                linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var txt = new TextBlock
                {
                    Text = $"{e.Qtd}x {Combos.Capitalizar(e.Nome)}", FontSize = 15, Foreground = R("Texto"),
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                };
                AutomationProperties.SetName(txt, "ForaDoCombo " + e.Nome);
                Grid.SetColumn(txt, 0);
                linha.Children.Add(txt);
                var tirar = Botao("Tirar", false);
                tirar.MinHeight = 40; tirar.FontSize = 14; tirar.Padding = new Thickness(14, 4, 14, 4);
                AutomationProperties.SetName(tirar, "Tirar " + e.Nome);
                var produtoId = e.ProdutoId;
                tirar.Click += (_, _) => { if (estado.TirarFora(produtoId)) Atualizar(); };
                Grid.SetColumn(tirar, 1);
                linha.Children.Add(tirar);
                fora.Children.Add(linha);
            }
        }

        void Atualizar()
        {
            PintarFora();
            foreach (var c in cards) c.Atualizar(estado);
            foreach (var (g, progresso, cheia, vazia, tudoIgual) in cabecalhos)
            {
                progresso.Text = estado.Progresso(g);
                var f = estado.Fracao(g);
                cheia.Width = new GridLength(Math.Max(0.0001, f), GridUnitType.Star);
                vazia.Width = new GridLength(Math.Max(0.0001, 1 - f), GridUnitType.Star);
                var unico = estado.UnicoMarcado(g);
                tudoIgual.Visibility = unico is not null && estado.PodeMais(g) ? Visibility.Visible : Visibility.Collapsed;
                tudoIgual.Tag = unico;
            }
            var faltam = estado.Faltam;
            txtFaltam.Text = faltam ?? "";
            txtFaltam.Visibility = faltam is null ? Visibility.Collapsed : Visibility.Visible;
            adicionar.IsEnabled = estado.Completo;
        }

        foreach (var g in def.Grupos)
        {
            var bloco = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

            var cab = new Grid();
            cab.ColumnDefinitions.Add(new ColumnDefinition());
            cab.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var progresso = new TextBlock
            {
                FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = R("Texto"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(progresso, "Progresso " + g.Nome);
            Grid.SetColumn(progresso, 0);
            cab.Children.Add(progresso);

            var tudoIgual = Botao("Tudo igual", false);
            tudoIgual.MinHeight = 40; tudoIgual.FontSize = 14;
            tudoIgual.Padding = new Thickness(14, 4, 14, 4);
            AutomationProperties.SetName(tudoIgual, "Tudo igual " + g.Nome);
            var grupo = g;
            tudoIgual.Click += (s, _) =>
            {
                if (((Button)s).Tag is Combos.ItemFonte item) { estado.TudoIgual(grupo, item); Atualizar(); }
            };
            Grid.SetColumn(tudoIgual, 1);
            cab.Children.Add(tudoIgual);
            bloco.Children.Add(cab);

            // barra fina de progresso: duas colunas em estrela (cheia | vazia)
            var barra = new Grid { Height = 4, Margin = new Thickness(0, 6, 0, 10) };
            var cheia = new ColumnDefinition(); var vazia = new ColumnDefinition();
            barra.ColumnDefinitions.Add(cheia); barra.ColumnDefinitions.Add(vazia);
            var bCheia = new Border { Background = R("Rosa"), CornerRadius = new CornerRadius(2) };
            var bVazia = new Border { Background = R("Borda"), CornerRadius = new CornerRadius(2) };
            Grid.SetColumn(bCheia, 0); Grid.SetColumn(bVazia, 1);
            barra.Children.Add(bCheia); barra.Children.Add(bVazia);
            bloco.Children.Add(barra);
            cabecalhos.Add((g, progresso, cheia, vazia, tudoIgual));

            var grade = new UniformGrid { Columns = colunas };
            foreach (var item in Combos.ResolverFonte(def, g, catalogo))
            {
                var card = new Card(g, item, estado, Atualizar);
                cards.Add(card);
                grade.Children.Add(card.Visual);
            }
            if (grade.Children.Count == 0)
                bloco.Children.Add(new TextBlock
                {
                    Text = "Nenhum produto disponível neste grupo", FontSize = 14,
                    Foreground = R("TextoFraco"), Margin = new Thickness(0, 4, 0, 4),
                });
            bloco.Children.Add(grade);
            pilha.Children.Add(bloco);
        }

        Atualizar();
        janela.Content = Dialogo.Moldura(raiz);
        janela.KeyDown += (_, e) => { if (e.Key == Key.Escape) janela.Close(); };
        janela.ShowDialog();
        return resultado;
    }

    private static Button Botao(string texto, bool destaque)
    {
        var b = new Button
        {
            Content = texto,
            Style = (Style)Application.Current.Resources[destaque ? "BotaoPrincipal" : "BotaoBase"],
            MinHeight = 58, FontSize = 17,
            Background = destaque ? R("RosaDegrade") : R("PainelAlto"),
        };
        AutomationProperties.SetName(b, texto);
        return b;
    }

    /// <summary>
    /// Um sabor na grade: o nome e um botao grande (toque = +1) e, embaixo, −, contador
    /// e +. Marcado ganha borda rosa. Atualizar() reescreve contador e habilitacao sem
    /// reconstruir a grade: a rolagem do operador nao pula para o topo a cada toque.
    /// </summary>
    private sealed class Card
    {
        private readonly Combos.GrupoDef _g;
        private readonly Combos.ItemFonte _item;
        private readonly Border _borda;
        private readonly Button _nome, _menos, _mais;
        private readonly TextBlock _contador;
        public FrameworkElement Visual => _borda;

        public Card(Combos.GrupoDef g, Combos.ItemFonte item, Combos.Estado estado, Action atualizar)
        {
            _g = g; _item = item;
            _borda = new Border
            {
                Background = R("PainelDegrade"), BorderBrush = R("Borda"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(8, 8, 8, 8),
            };
            var pilha = new StackPanel();
            _nome = new Button
            {
                Style = (Style)Application.Current.Resources["BotaoBase"],
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                MinHeight = 52, Padding = new Thickness(2), FontSize = 13,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new TextBlock
                {
                    // "Donut Ovomaltine" no grupo "Donuts" vira "Ovomaltine" (regra da sub-linha)
                    Text = Combos.NomeCurto(item.Nome, g.Nome), FontSize = 13, FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                    Foreground = R("Texto"), MaxHeight = 52, TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            AutomationProperties.SetName(_nome, item.Nome);
            _nome.Click += (_, _) => { if (estado.Mais(g, item)) atualizar(); };
            pilha.Children.Add(_nome);

            var controles = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            _menos = Redondo("−");
            AutomationProperties.SetName(_menos, "Menos " + item.Nome);
            _menos.Click += (_, _) => { if (estado.Menos(g, item.ProdutoId)) atualizar(); };
            controles.Children.Add(_menos);
            _contador = new TextBlock
            {
                FontSize = 17, FontWeight = FontWeights.Bold, Width = 40, TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, Foreground = R("Texto"),
            };
            AutomationProperties.SetName(_contador, "Contador " + item.Nome);
            controles.Children.Add(_contador);
            _mais = Redondo("+");
            AutomationProperties.SetName(_mais, "Mais " + item.Nome);
            _mais.Click += (_, _) => { if (estado.Mais(g, item)) atualizar(); };
            controles.Children.Add(_mais);
            pilha.Children.Add(controles);
            _borda.Child = pilha;
        }

        public void Atualizar(Combos.Estado estado)
        {
            var n = estado.Quantos(_g.Id, _item.ProdutoId);
            var pode = estado.PodeMais(_g);
            _contador.Text = n.ToString();
            _contador.Foreground = n > 0 ? R("Rosa") : R("TextoFraco");
            _mais.IsEnabled = pode;
            _nome.IsEnabled = pode;
            _menos.IsEnabled = n > 0;
            _borda.BorderBrush = n > 0 ? R("Rosa") : R("Borda");
            _borda.BorderThickness = new Thickness(n > 0 ? 2 : 1);
        }

        private static Button Redondo(string txt) => new()
        {
            Content = txt, Width = 42, Height = 42, MinHeight = 42, FontSize = 21,
            Padding = new Thickness(0), Style = (Style)Application.Current.Resources["BotaoBase"],
            Background = R("PainelAlto"), Margin = new Thickness(2, 4, 2, 0),
        };
    }
}
