using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// AVISO DE ESPERA enquanto a nuvem é chamada.
///
/// É uma janela SEPARADA e não-modal de propósito: `ShowDialog` bloquearia a
/// continuação do `await`, e o caixa ficaria com a tela congelada até a nuvem
/// responder. A janela dona fica desabilitada (o operador não sai clicando em
/// outra coisa no meio), mas a mensagem do Windows continua rodando.
/// </summary>
public sealed class Espera : IDisposable
{
    private readonly Window _janela;
    private readonly Window _dono;
    private bool _fechada;

    public Espera(Window dono, string mensagem)
    {
        _dono = dono;
        _janela = Dialogo.Base(dono, 420);
        var pilha = new StackPanel();
        pilha.Children.Add(new TextBlock
        {
            Text = mensagem, FontSize = 18, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["Texto"],
        });
        pilha.Children.Add(new TextBlock
        {
            Text = "Não feche o caixa nem desligue o PDV.",
            FontSize = 13, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextoFraco"],
        });
        _janela.Content = Dialogo.Moldura(pilha);
        _dono.IsEnabled = false;
        _janela.Show();
    }

    /// <summary>
    /// CINTO E SUSPENSÓRIO DE THREAD. Quem fecha este aviso é o `Dispose` de um
    /// `using` no meio de uma máquina de estados com `await` (Autorizacao.
    /// ResolverAsync). Se algum dia a continuação daquele await voltar fora da
    /// thread da UI, `_dono.IsEnabled = true` e `_janela.Close()` estouram
    /// InvalidOperationException. Já aconteceu: um `ConfigureAwait(false)` no
    /// núcleo jogava o Dispose numa thread do pool, a exceção subia até o
    /// `async void` do menu e ENCERRAVA O PROCESSO com o cliente no balcão.
    /// A causa foi corrigida lá; isto aqui é para o estrago não voltar a ser esse.
    /// </summary>
    public void Dispose()
    {
        if (_fechada) return;
        _fechada = true;
        try
        {
            _dono.Dispatcher.Invoke(() =>
            {
                _dono.IsEnabled = true;
                try { _janela.Close(); } catch { }
            });
        }
        catch { /* app encerrando: não há janela para reabilitar */ }
    }
}

/// <summary>
/// A tela do código de 6 dígitos do autenticador do dono (Google Authenticator).
///
/// Duas coisas aqui não são enfeite:
///  · o código digitado não é gravado em lugar nenhum: nem em log, nem na
///    auditoria. Ele vive nesta janela e morre com ela;
///  · não há botão de "novo código" nem de senha local: o código muda sozinho a
///    cada 30 s no celular do dono, e não existe outro caminho para o estorno.
/// </summary>
public static class PedirCodigo
{
    /// <summary>Devolve o código de 6 dígitos, ou null se o operador cancelou.</summary>
    public static string? Mostrar(Window dono, string? aviso)
    {
        string? resposta = null;
        var janela = Dialogo.Base(dono, 460);
        var painel = new StackPanel();
        painel.Children.Add(PedirValor.Cabecalho(janela, "Autorização do dono"));

        painel.Children.Add(new TextBlock
        {
            Text = "Código do autenticador do dono",
            FontSize = 15, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextoFraco"],
            Margin = new Thickness(0, 0, 0, 10),
        });

        var alerta = new TextBlock
        {
            Text = aviso ?? "", FontSize = 14, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["Erro"],
            Margin = new Thickness(0, 0, 0, 10),
            Visibility = string.IsNullOrWhiteSpace(aviso) ? Visibility.Collapsed : Visibility.Visible,
        };
        painel.Children.Add(alerta);

        var caixa = new TextBox
        {
            FontSize = 34, MaxLength = 6, TextAlignment = TextAlignment.Center,
            FontFamily = new FontFamily("Consolas"), Padding = new Thickness(10),
            Background = (Brush)Application.Current.Resources["Painel"],
            Foreground = (Brush)Application.Current.Resources["Texto"],
            BorderBrush = (Brush)Application.Current.Resources["Borda"],
        };
        // Só dígito entra: o Authenticator mostra "287 082" com espaço no meio.
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
        teclado.Digitou += d => { if (caixa.Text.Length < 6) caixa.Text += d; };
        teclado.Apagou += () => { if (caixa.Text.Length > 0) caixa.Text = caixa.Text[..^1]; };
        teclado.Limpou += () => caixa.Text = "";
        painel.Children.Add(teclado);

        var linha = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        var cancelar = PedirValor.Botao("Cancelar", false);
        var confirmar = PedirValor.Botao("Confirmar", true);
        cancelar.Margin = new Thickness(0, 0, 5, 0);
        confirmar.Margin = new Thickness(5, 0, 0, 0);
        cancelar.Click += (_, _) => janela.Close();
        void Confirmar()
        {
            if (caixa.Text.Length != 6) { alerta.Text = "O código tem 6 dígitos."; alerta.Visibility = Visibility.Visible; return; }
            resposta = caixa.Text;
            janela.Close();
        }
        confirmar.Click += (_, _) => Confirmar();
        Grid.SetColumn(cancelar, 0); Grid.SetColumn(confirmar, 1);
        linha.Children.Add(cancelar); linha.Children.Add(confirmar);
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
