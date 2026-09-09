using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// A tela que mostra o QR do Pix ao cliente enquanto o TEF espera o pagamento.
///
/// A PGWebLib manda a automação desenhar o QR (PWDAT_DSPQRCODE) em vez de mandar o cliente ler no
/// pinpad. O roteiro de homologação v20260819 conta com isso no passo 55: "realizar uma venda e na
/// tela de exibição do QRCode pressionar a tecla Esc em uma solução Windows", esperando
/// "OPERAÇÃO CANCELADA".
///
/// Por isso esta janela tem duas responsabilidades e nada mais:
///   1. mostrar o QR grande o suficiente para um celular ler de longe;
///   2. transformar o Esc (e o botão Cancelar) em cancelamento da VENDA, não em fechar a janela.
///
/// Ela não bloqueia: <see cref="Mostrar"/> abre e devolve na hora a ação que a fecha. Quem espera o
/// cliente pagar é o laço do provedor, que fica perguntando o desfecho ao host.
/// </summary>
public static class TelaQrTef
{
    private static Brush R(string chave) => (Brush)Application.Current.Resources[chave];

    /// <summary>
    /// Abre a tela e devolve a ação que a fecha. `aoCancelar` roda quando o operador aperta Esc ou
    /// o botão: é ela que cancela a venda no provedor.
    /// </summary>
    /// <summary>
    /// Abre a tela e devolve DUAS acoes: fechar, e atualizar o texto no lugar.
    ///
    /// A segunda existe por causa do contador (09/09/2026). A biblioteca manda
    /// "REALIZE A LEITURA DO QR CODE 06", depois "07", "08"... um pedido por segundo, e
    /// o caixa recriava a janela em cada um. O dono viu o QR piscando. Agora o texto
    /// muda dentro da mesma janela e o QR fica parado.
    /// </summary>
    public static (Action Fechar, Action<string> AtualizarTexto) Mostrar(Window dono, ExibicaoTef exibicao, Action aoCancelar)
    {
        var pilha = new StackPanel();

        pilha.Children.Add(new TextBlock
        {
            Text = exibicao.Titulo,
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = R("Texto"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        if (exibicao.EhQrCode && Impressao.QrParaTela(exibicao.QrCode, 340) is { } desenho)
        {
            // Fundo branco atrás do QR: leitor de celular não lê QR escuro sobre painel escuro.
            pilha.Children.Add(new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(18),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = desenho,
            });
        }

        // Sempre criado (escondido quando vazio): e ele que o contador atualiza no lugar.
        var mensagem = new TextBlock
        {
            Text = exibicao.Mensagem ?? "",
            FontSize = 20,
            Foreground = R("TextoFraco"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
            Visibility = string.IsNullOrWhiteSpace(exibicao.Mensagem) ? Visibility.Collapsed : Visibility.Visible,
        };
        pilha.Children.Add(mensagem);

        pilha.Children.Add(new TextBlock
        {
            Text = "Esc cancela a cobrança.",
            FontSize = 16,
            Foreground = R("TextoFraco"),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 18, 0, 0),
        });

        var botao = new Button
        {
            Content = "Cancelar cobrança",
            Style = (Style)Application.Current.Resources["BotaoBase"],
            MinHeight = 58,
            Margin = new Thickness(0, 18, 0, 0),
        };
        pilha.Children.Add(botao);

        var janela = Dialogo.Base(dono, 470);
        janela.Content = Dialogo.Moldura(pilha);

        var cancelou = false;
        var fechada = false;

        void Cancelar()
        {
            if (cancelou) return;
            cancelou = true;
            // Só avisa o provedor. Quem fecha a janela é o fim da cobrança, para o operador ver a
            // tela até o TEF confirmar que desistiu.
            try { aoCancelar(); } catch { /* cancelar nunca derruba a tela */ }
            botao.IsEnabled = false;
            botao.Content = "Cancelando…";
        }

        botao.Click += (_, _) => Cancelar();
        janela.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Cancelar();
        };
        // Fechar pelo X ou pelo Alt+F4 também é desistir: nunca deixa a venda correndo sem tela.
        janela.Closing += (_, e) =>
        {
            if (fechada) return;
            e.Cancel = true;
            Cancelar();
        };

        janela.Show();
        janela.Activate();

        void Fechar()
        {
            fechada = true;
            try { janela.Close(); } catch { /* já fechada */ }
        }
        void AtualizarTexto(string texto)
        {
            mensagem.Text = texto ?? "";
            mensagem.Visibility = string.IsNullOrWhiteSpace(texto) ? Visibility.Collapsed : Visibility.Visible;
        }
        return (Fechar, AtualizarTexto);
    }
}
