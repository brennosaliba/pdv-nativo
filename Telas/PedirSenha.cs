using System.Windows;
using System.Windows.Controls;

namespace Pdv.Telas;

/// <summary>
/// Caixa de senha simples, montada em código (não vale um XAML só pra isso).
/// Usada para senha de administrador e para autorização de supervisor.
/// Tem teclado numérico NA TELA porque o caixa é touch: sem ele, o gerente não
/// consegue autorizar nada no balcão. Senha com letras continua possível pelo
/// teclado físico — o campo é um PasswordBox normal.
/// </summary>
public static class PedirSenha
{
    public static string? Mostrar(Window dono, string titulo, string rotulo)
    {
        string? resultado = null;
        var janela = Dialogo.Base(dono, 440);
        var painel = new StackPanel();
        painel.Children.Add(PedirValor.Cabecalho(janela, titulo));
        painel.Children.Add(new TextBlock
        {
            Text = rotulo,
            Style = (Style)Application.Current.Resources["Rotulo"],
            Margin = new Thickness(0, 0, 0, 8),
        });

        var caixa = new PasswordBox
        {
            FontSize = 22,
            Padding = new Thickness(14, 12, 14, 12),
            Background = (System.Windows.Media.Brush)Application.Current.Resources["Painel"],
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Texto"],
            BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["Borda"],
        };
        painel.Children.Add(caixa);

        var teclado = new TecladoNumerico { Margin = new Thickness(0, 12, 0, 0) };
        teclado.Digitou += d => { caixa.Password += d; };
        teclado.Apagou += () => { if (caixa.Password.Length > 0) caixa.Password = caixa.Password[..^1]; };
        teclado.Limpou += () => caixa.Password = "";
        painel.Children.Add(teclado);

        var linha = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        var cancelar = PedirValor.Botao("Cancelar", false);
        var ok = PedirValor.Botao("Confirmar", true);
        cancelar.Margin = new Thickness(0, 0, 5, 0);
        ok.Margin = new Thickness(5, 0, 0, 0);
        cancelar.Click += (_, _) => janela.Close();
        ok.Click += (_, _) => { resultado = caixa.Password; janela.Close(); };
        // FECHA DEPOIS QUE A TECLA TERMINA DE SER PROCESSADA (09/09/2026). Fechar a janela
        // dentro do KeyDown destruia o HWND com o Enter ainda em voo; o WM_CHAR e o KeyUp
        // sobravam para a proxima janela (a pergunta seguinte do TEF) e o WPF caia em
        // TextServicesContext.Keystroke. Handled=true segura o '\r', e o Close vai para a
        // fila do Dispatcher, ja fora do processamento da tecla.
        caixa.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) resultado = caixa.Password;
            else if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            janela.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, (Action)janela.Close);
        };
        Grid.SetColumn(cancelar, 0);
        Grid.SetColumn(ok, 1);
        linha.Children.Add(cancelar);
        linha.Children.Add(ok);
        painel.Children.Add(linha);

        janela.Content = Dialogo.Moldura(painel);
        janela.Loaded += (_, _) => caixa.Focus();
        janela.ShowDialog();
        return string.IsNullOrEmpty(resultado) ? null : resultado;
    }
}
