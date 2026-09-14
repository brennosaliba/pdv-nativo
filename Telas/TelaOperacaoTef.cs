using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// A TELA QUE ACOMPANHA UMA OPERAÇÃO DEMORADA DO TEF (a instalação do ponto de captura).
///
/// 14/09/2026, Castelo: o dono tocou em "Instalar ponto de captura", a tela ficou parada mais
/// de cinco minutos, sem relógio, sem recado e sem botão que funcionasse, e ele matou o caixa
/// pelo Gerenciador de Tarefas. Duas vezes.
///
/// O que esta tela garante:
///  · cronômetro que anda ("Instalando... 1 min 20 s");
///  · a última mensagem que a biblioteca mandou, e o aviso do cabo quando a chamada está presa;
///  · o prazo máximo em texto simples;
///  · Cancelar que funciona NA HORA: devolve a tela ao operador e pede à biblioteca para parar.
///    A chamada que já está presa dentro da DLL não se interrompe por fora (não há função
///    documentada para isso); ela termina sozinha ou quando o cabo do pinpad sai, e o
///    resultado é descartado.
///
/// Sem porcentagem: a biblioteca não informa progresso. Os textos moram em
/// <see cref="AcompanhamentoTef"/>, que a bateria confere.
/// </summary>
public static class TelaOperacaoTef
{
    private static Brush R(string chave) => (Brush)Application.Current.Resources[chave];

    /// <summary>
    /// Roda a operação com a tela aberta. Devolve o desfecho, ou (null, true) quando o operador
    /// tocou em Cancelar. Modal: quem chama continua quando a tela fecha.
    /// </summary>
    public static (DesfechoTef? Desfecho, bool Cancelou) Acompanhar(Window dono, string titulo, string verbo,
        ProvedorPGWebLib pg, TimeSpan prazo, Func<CancellationToken, Task<DesfechoTef>> operacao)
    {
        DesfechoTef? desfecho = null;
        var cancelou = false;
        var terminou = false;
        // Sem using de propósito: a operação pode seguir depois de a tela fechar (chamada presa na
        // DLL) e ainda vai olhar o token.
        var cts = new CancellationTokenSource();

        var janela = Dialogo.Base(dono, 540);
        var pilha = new StackPanel();
        pilha.Children.Add(new TextBlock
        {
            Text = titulo, FontSize = 22, FontWeight = FontWeights.Bold,
            Foreground = R("Texto"), TextWrapping = TextWrapping.Wrap,
        });
        var cronometro = new TextBlock
        {
            FontSize = 28, FontWeight = FontWeights.Bold, Foreground = R("Texto"),
            Margin = new Thickness(0, 14, 0, 2),
        };
        pilha.Children.Add(cronometro);
        var prazoTexto = new TextBlock
        {
            Text = AcompanhamentoTef.Prazo(prazo), FontSize = 14, Foreground = R("TextoFraco"),
            TextWrapping = TextWrapping.Wrap,
        };
        pilha.Children.Add(prazoTexto);
        var recado = new TextBlock
        {
            FontSize = 15, Foreground = R("Texto"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 18), MinHeight = 44,
        };
        pilha.Children.Add(recado);

        var cancelar = PedirValor.Botao("Cancelar", false);
        pilha.Children.Add(cancelar);

        var relogio = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        void Pintar()
        {
            cronometro.Text = AcompanhamentoTef.Cronometro(verbo, relogio.Elapsed);
            recado.Text = AcompanhamentoTef.Recado(pg.UltimoRecado, pg.EmVoo, DateTime.UtcNow);
            if (AcompanhamentoTef.PassouDoPrazo(relogio.Elapsed, prazo))
            {
                prazoTexto.Text = AcompanhamentoTef.TextoPassouDoPrazo;
                prazoTexto.Foreground = R("Erro");
            }
        }

        timer.Tick += (_, _) => Pintar();
        cancelar.Click += (_, _) =>
        {
            cancelou = true;
            try { cts.Cancel(); } catch { /* já cancelado */ }
            janela.Close();
        };
        // Alt+F4 não fecha calado: ou termina, ou é o Cancelar.
        janela.Closing += (_, e) => { if (!terminou && !cancelou) e.Cancel = true; };
        janela.Closed += (_, _) => timer.Stop();
        janela.Loaded += async (_, _) =>
        {
            Pintar();
            timer.Start();
            try { desfecho = await operacao(cts.Token); }
            catch (Exception ex)
            {
                desfecho = new DesfechoTef(SituacaoTef.Erro, null, null, null,
                    "Não consegui falar com a maquininha. Detalhe: " + ex.Message, false);
            }
            terminou = true;
            if (!cancelou) janela.Close();
        };

        janela.Content = Dialogo.Moldura(pilha);
        janela.ShowDialog();
        return cancelou ? (null, true) : (desfecho, false);
    }
}
