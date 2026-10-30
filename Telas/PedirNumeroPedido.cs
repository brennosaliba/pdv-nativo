using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Pdv.Telas;

/// <summary>
/// O NÚMERO DO PEDIDO DO iFOOD (1.0.61, SQL 245): uma linha e o teclado da tela, no mesmo
/// desenho da tela do código do dono (PedirCodigo). Serve para reimprimir a nota do iFood e
/// para copiar o link dela. É o número curto que o cliente vê no app (ex.: 4851).
/// </summary>
public static class PedirNumeroPedido
{
    /// <summary>Até 8 dígitos: o número do iFood tem 4, e "#0055" também vale.</summary>
    public const int MaximoDigitos = 8;

    /// <summary>Devolve o número digitado (só dígitos), ou null se o operador cancelou.</summary>
    public static string? Mostrar(Window dono, string titulo, string confirmar)
    {
        string? resposta = null;
        var janela = Dialogo.Base(dono, 460);
        var painel = new StackPanel();
        painel.Children.Add(PedirValor.Cabecalho(janela, titulo));

        painel.Children.Add(new TextBlock
        {
            Text = "O número que aparece no pedido do iFood",
            FontSize = 15, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextoFraco"],
            Margin = new Thickness(0, 0, 0, 10),
        });

        var alerta = new TextBlock
        {
            FontSize = 14, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["Erro"],
            Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed,
        };
        painel.Children.Add(alerta);

        var caixa = new TextBox
        {
            FontSize = 34, MaxLength = MaximoDigitos, TextAlignment = TextAlignment.Center,
            FontFamily = new FontFamily("Consolas"), Padding = new Thickness(10),
            Background = (Brush)Application.Current.Resources["Painel"],
            Foreground = (Brush)Application.Current.Resources["Texto"],
            BorderBrush = (Brush)Application.Current.Resources["Borda"],
        };
        // só dígito entra: "#4851" colado vira "4851"
        caixa.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        caixa.TextChanged += (_, _) =>
        {
            var limpo = new string(caixa.Text.Where(char.IsDigit).ToArray());
            if (limpo == caixa.Text) return;
            caixa.Text = limpo;
            caixa.CaretIndex = limpo.Length;
        };
        painel.Children.Add(caixa);

        var teclado = new TecladoNumerico { Margin = new Thickness(0, 12, 0, 0) };
        teclado.Digitou += d => { if (caixa.Text.Length < MaximoDigitos) caixa.Text += d; };
        teclado.Apagou += () => { if (caixa.Text.Length > 0) caixa.Text = caixa.Text[..^1]; };
        teclado.Limpou += () => caixa.Text = "";
        painel.Children.Add(teclado);

        var linha = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        var cancelar = PedirValor.Botao("Cancelar", false);
        var ok = PedirValor.Botao(confirmar, true);
        cancelar.Margin = new Thickness(0, 0, 5, 0);
        ok.Margin = new Thickness(5, 0, 0, 0);
        cancelar.Click += (_, _) => janela.Close();
        void Confirmar()
        {
            if (caixa.Text.Length == 0) { alerta.Text = "Digite o número do pedido."; alerta.Visibility = Visibility.Visible; return; }
            resposta = caixa.Text;
            janela.Close();
        }
        ok.Click += (_, _) => Confirmar();
        Grid.SetColumn(cancelar, 0); Grid.SetColumn(ok, 1);
        linha.Children.Add(cancelar); linha.Children.Add(ok);
        painel.Children.Add(linha);

        janela.Content = Dialogo.Moldura(painel);
        janela.Loaded += (_, _) => caixa.Focus();
        janela.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) janela.Close();
            if (e.Key == Key.Enter) Confirmar();
        };
        janela.ShowDialog();
        return resposta;
    }
}
