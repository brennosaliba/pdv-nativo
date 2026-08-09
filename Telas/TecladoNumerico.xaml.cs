using System.Windows;
using System.Windows.Controls;

namespace Pdv.Telas;

/// <summary>
/// Teclado numérico da tela. Existe por dois motivos: o teclado do Windows (TabTip)
/// abre por cima da aplicação e some com o layout, e nem todo caixa tem teclado
/// físico. Este é sempre visível, do tamanho certo pro dedo.
/// </summary>
public partial class TecladoNumerico : UserControl
{
    public TecladoNumerico() => InitializeComponent();

    /// <summary>Dígito tocado.</summary>
    public event Action<string>? Digitou;
    /// <summary>Apagar o último.</summary>
    public event Action? Apagou;
    /// <summary>Limpar tudo.</summary>
    public event Action? Limpou;

    private void Tecla(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Content is string d) Digitou?.Invoke(d);
    }

    private void Apagar(object sender, RoutedEventArgs e) => Apagou?.Invoke();
    private void Limpar(object sender, RoutedEventArgs e) => Limpou?.Invoke();
}
