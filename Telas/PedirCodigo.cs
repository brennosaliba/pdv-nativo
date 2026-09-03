using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// AVISO DE ESPERA enquanto a nuvem é chamada.
///
/// É uma janela SEPARADA e não-modal de propósito: `ShowDialog` bloquearia a
/// continuação do `await`, e o caixa ficaria com a tela congelada até a nuvem
/// responder — o mesmo motivo pelo qual a impressão do comprovante saiu do
/// caminho crítico. A janela dona fica desabilitada (o operador não sai clicando
/// em outra coisa no meio), mas a mensagem do Windows continua rodando.
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
    /// ResolverAsync) — e se algum dia a continuação daquele await voltar fora da
    /// thread da UI, `_dono.IsEnabled = true` e `_janela.Close()` estouram
    /// InvalidOperationException. Já aconteceu: um `ConfigureAwait(false)` no
    /// núcleo jogava o Dispose numa thread do pool, a exceção subia até o
    /// `async void` do menu do TEF e ENCERRAVA O PROCESSO com o cliente no balcão.
    /// A causa foi corrigida lá; isto aqui é para o estrago não voltar a ser esse
    /// se alguém reintroduzir o await errado. Marshalar custa nada quando já se
    /// está na thread certa (Invoke executa direto).
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
/// A tela do código de 6 dígitos que a gerente geral (ou o dono) recebeu no
/// WhatsApp.
///
/// Três coisas aqui não são enfeite:
///  · o TEMPO RESTANTE aparece na tela — sem ele o operador digita um código
///    morto e culpa o sistema;
///  · "Não recebi" e "Usar o PIN do supervisor" ficam VISÍVEIS o tempo todo: com
///    cliente no balcão, tela sem saída é pior que autorização fraca;
///  · o código digitado não é gravado em lugar nenhum — nem em log, nem na
///    auditoria. Ele vive nesta janela e morre com ela.
/// </summary>
public static class PedirCodigo
{
    public static RespostaCodigo Mostrar(Window dono, RespostaSolicitacao pedido, string? aviso)
    {
        var resposta = new RespostaCodigo(AcaoCodigo.Cancelar, null);
        var janela = Dialogo.Base(dono, 460);
        var painel = new StackPanel();
        painel.Children.Add(PedirValor.Cabecalho(janela, "Autorização da gerência"));

        var quem = pedido.Destinatarios.Count > 0
            ? string.Join(" e ", pedido.Destinatarios.Where(d => d.Enviado).Select(d => d.Nome))
            : "";
        painel.Children.Add(new TextBlock
        {
            // TOKEN REAPROVEITADO: a nuvem devolveu o token que JÁ existia (a
            // tentativa anterior deste mesmo estorno chegou a mandar a mensagem,
            // mesmo que o caixa tenha desistido de esperar e saído pelo PIN).
            // Nenhuma mensagem nova saiu — e se a tela não disser isso, o operador
            // fica olhando para o celular esperando um WhatsApp que não vem, e
            // depois aperta "não recebi" sem precisar.
            Text = pedido.Reaproveitado
                ? (quem.Length > 0
                    ? $"O código que JÁ foi enviado no WhatsApp para {quem} continua valendo: não saiu mensagem nova. Digite o código que a pessoa passar."
                    : "O código enviado antes no WhatsApp da gerência continua valendo: não saiu mensagem nova.")
                : quem.Length > 0
                ? $"Um código de 6 dígitos foi enviado no WhatsApp para {quem}. Digite o código que a pessoa passar."
                : "Digite o código de 6 dígitos enviado no WhatsApp da gerência.",
            FontSize = 14, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextoFraco"],
            Margin = new Thickness(0, 0, 0, 12),
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
        // Só dígito entra: código com espaço colado do WhatsApp queimaria uma das
        // 5 tentativas do token por nada.
        caixa.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        caixa.TextChanged += (_, _) =>
        {
            var limpo = new string(caixa.Text.Where(char.IsDigit).ToArray());
            if (limpo == caixa.Text) return;
            caixa.Text = limpo;
            caixa.CaretIndex = limpo.Length;
        };
        painel.Children.Add(caixa);

        var relogio = new TextBlock
        {
            FontSize = 14, Margin = new Thickness(0, 8, 0, 0), TextAlignment = TextAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextoFraco"],
        };
        painel.Children.Add(relogio);

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
        confirmar.Click += (_, _) =>
        {
            if (caixa.Text.Length != 6) { alerta.Text = "O código tem 6 dígitos."; alerta.Visibility = Visibility.Visible; return; }
            resposta = new RespostaCodigo(AcaoCodigo.Confirmar, caixa.Text);
            janela.Close();
        };
        Grid.SetColumn(cancelar, 0); Grid.SetColumn(confirmar, 1);
        linha.Children.Add(cancelar); linha.Children.Add(confirmar);
        painel.Children.Add(linha);

        var outro = PedirValor.Botao("Não recebi · enviar de novo", false);
        outro.Margin = new Thickness(0, 8, 0, 0);
        outro.FontSize = 15;
        outro.Click += (_, _) => { resposta = new RespostaCodigo(AcaoCodigo.NovoCodigo, null); janela.Close(); };
        painel.Children.Add(outro);

        var pin = PedirValor.Botao("Sem resposta? Usar o PIN do supervisor", false);
        pin.Margin = new Thickness(0, 8, 0, 0);
        pin.FontSize = 15;
        pin.Click += (_, _) => { resposta = new RespostaCodigo(AcaoCodigo.Pin, null); janela.Close(); };
        painel.Children.Add(pin);

        // Contagem regressiva ancorada no relógio DESTA máquina (o cliente já
        // converteu a validade que veio do servidor).
        var relogioTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void Atualizar()
        {
            var falta = pedido.ExpiraEm is { } exp ? exp - DateTime.Now : TimeSpan.Zero;
            if (falta > TimeSpan.Zero)
            {
                relogio.Text = $"O código vale por mais {falta.Minutes}:{falta.Seconds:D2}";
                relogio.Foreground = (Brush)Application.Current.Resources["TextoFraco"];
            }
            else
            {
                relogio.Text = "O código expirou. Toque em \"Não recebi\" para pedir outro.";
                relogio.Foreground = (Brush)Application.Current.Resources["Erro"];
            }
        }
        relogioTimer.Tick += (_, _) => Atualizar();
        Atualizar();
        relogioTimer.Start();

        janela.Content = Dialogo.Moldura(painel);
        janela.Loaded += (_, _) => caixa.Focus();
        janela.Closed += (_, _) => relogioTimer.Stop();
        janela.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) janela.Close();
            if (e.Key == Key.Enter && caixa.Text.Length == 6)
            {
                resposta = new RespostaCodigo(AcaoCodigo.Confirmar, caixa.Text);
                janela.Close();
            }
        };
        janela.ShowDialog();
        return resposta;
    }
}
