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
    public static Action Mostrar(Window dono, ExibicaoTef exibicao, Action aoCancelar)
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

        if (!string.IsNullOrWhiteSpace(exibicao.Mensagem))
        {
            pilha.Children.Add(new TextBlock
            {
                Text = exibicao.Mensagem,
                FontSize = 20,
                Foreground = R("TextoFraco"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 16, 0, 0),
            });
        }

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

        return () =>
        {
            fechada = true;
            try { janela.Close(); } catch { /* já fechada */ }
        };
    }
}
