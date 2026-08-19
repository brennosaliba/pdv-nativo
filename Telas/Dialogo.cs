using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Pdv.Telas;

/// <summary>
/// Diálogos no visual do app. O MessageBox do Windows é cinza, quadrado e com fonte
/// pequena — parece outro programa, e num caixa touch os botões dele são pequenos
/// demais pro dedo. Estes seguem a mesma linguagem das outras telas: escuro, cantos
/// arredondados e alvo de toque grande.
/// </summary>
public static class Dialogo
{
    private static Brush R(string chave) => (Brush)Application.Current.Resources[chave];

    internal static Window Base(Window dono, int largura)
    {
        return new Window
        {
            Owner = dono,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Width = largura,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
    }

    internal static Border Moldura(UIElement conteudo) => new()
    {
        Background = R("Painel"),
        CornerRadius = new CornerRadius(18),
        BorderBrush = R("Borda"),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(26, 22, 26, 22),
        Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Color = Colors.Black,
            Opacity = (double)Application.Current.Resources["SombraDialogoOpacidade"] },
        Child = conteudo,
    };

    private static Button Botao(string texto, bool destaque, Brush? cor = null) => new()
    {
        Content = texto,
        Style = (Style)Application.Current.Resources[destaque ? "BotaoPrincipal" : "BotaoBase"],
        MinHeight = 58,
        FontSize = 17,
        Background = cor ?? (destaque ? R("RosaDegrade") : R("PainelAlto")),
    };

    /// <summary>Pergunta sim/não. `perigo` pinta a ação de vermelho (é destrutiva).</summary>
    public static bool Confirmar(Window dono, string titulo, string mensagem,
        string textoSim = "Confirmar", string textoNao = "Cancelar", bool perigo = false)
    {
        var resposta = false;
        var janela = Base(dono, 460);
        var pilha = new StackPanel();

        pilha.Children.Add(new TextBlock
        {
            Text = titulo, FontSize = 22, FontWeight = FontWeights.Bold,
            Foreground = R("Texto"), TextWrapping = TextWrapping.Wrap,
        });
        pilha.Children.Add(new TextBlock
        {
            Text = mensagem, FontSize = 15, Foreground = R("TextoFraco"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 22),
        });

        var linha = new Grid();
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        var nao = Botao(textoNao, false);
        var sim = Botao(textoSim, true, perigo ? R("Erro") : null);
        nao.Margin = new Thickness(0, 0, 6, 0);
        sim.Margin = new Thickness(6, 0, 0, 0);
        nao.Click += (_, _) => janela.Close();
        sim.Click += (_, _) => { resposta = true; janela.Close(); };
        Grid.SetColumn(nao, 0); Grid.SetColumn(sim, 1);
        linha.Children.Add(nao); linha.Children.Add(sim);
        pilha.Children.Add(linha);

        janela.Content = Moldura(pilha);
        janela.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) janela.Close();
            if (e.Key == System.Windows.Input.Key.Enter) { resposta = true; janela.Close(); }
        };
        janela.ShowDialog();
        return resposta;
    }

    /// <summary>Aviso simples. `tom`: "ok" (verde), "erro" (vermelho) ou null (neutro).</summary>
    public static void Avisar(Window dono, string titulo, string mensagem, string? tom = null)
    {
        var janela = Base(dono, 460);
        var pilha = new StackPanel();
        pilha.Children.Add(new TextBlock
        {
            Text = titulo, FontSize = 22, FontWeight = FontWeights.Bold,
            Foreground = tom switch { "ok" => R("Ok"), "erro" => R("Erro"), _ => R("Texto") },
            TextWrapping = TextWrapping.Wrap,
        });
        pilha.Children.Add(new TextBlock
        {
            Text = mensagem, FontSize = 15, Foreground = R("TextoFraco"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 22),
        });
        var ok = Botao("Entendi", true);
        ok.Click += (_, _) => janela.Close();
        pilha.Children.Add(ok);
        janela.Content = Moldura(pilha);
        janela.KeyDown += (_, e) =>
        {
            if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Escape) janela.Close();
        };
        janela.ShowDialog();
    }

    /// <summary>Texto monoespaçado (relatório de fechamento) — alinha as colunas.</summary>
    public static void Relatorio(Window dono, string titulo, string corpo, string? rodape = null)
    {
        var janela = Base(dono, 620);
        var pilha = new StackPanel();
        pilha.Children.Add(new TextBlock
        {
            Text = titulo, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = R("Texto"),
        });
        pilha.Children.Add(new Border
        {
            Background = R("Fundo"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 14, 0, 14),
            Child = new TextBlock
            {
                Text = corpo, FontFamily = new FontFamily("Consolas"), FontSize = 14,
                Foreground = R("Texto"), TextWrapping = TextWrapping.NoWrap,
            },
        });
        if (rodape is not null)
            pilha.Children.Add(new TextBlock
            {
                Text = rodape, FontSize = 14, Foreground = R("TextoFraco"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
            });
        var ok = Botao("Fechar", true);
        ok.Click += (_, _) => janela.Close();
        pilha.Children.Add(ok);
        janela.Content = Moldura(pilha);
        janela.ShowDialog();
    }
}
