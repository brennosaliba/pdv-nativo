using System.Windows;
using System.Windows.Controls;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// O lado de WPF da autorização de estorno: é isto que a máquina de estados do
/// núcleo (<see cref="Autorizacao.ResolverAsync"/>) chama quando precisa de
/// janela. Fica separado para a decisão continuar testável sem abrir tela.
///
/// Regra que atravessa os quatro métodos: o operador SEMPRE enxerga uma saída.
/// Estorno é conversa com o cliente no balcão — tela sem botão de saída vira
/// telefonema para o dono, e o dono está no meio da homologação.
/// </summary>
public sealed class TelaAutorizacao : ITelaAutorizacao
{
    private readonly Window _dono;

    public TelaAutorizacao(Window dono) => _dono = dono;

    public IDisposable Aguardando(string mensagem) => new Espera(_dono, mensagem);

    public Task<RespostaCodigo> PedirCodigoAsync(RespostaSolicitacao pedido, string? aviso)
        => Task.FromResult(PedirCodigo.Mostrar(_dono, pedido, aviso));

    public Task<EscolhaAposFalha> EscolherAposFalhaAsync(string mensagem)
    {
        var i = Escolher(_dono, "Autorização", mensagem + " O que você quer fazer?", new[]
        {
            "Pedir um código novo",
            "Usar o PIN do supervisor",
            "Desistir do estorno",
        });
        return Task.FromResult(i switch
        {
            0 => EscolhaAposFalha.NovoCodigo,
            1 => EscolhaAposFalha.Pin,
            _ => EscolhaAposFalha.Desistir,
        });
    }

    /// <summary>
    /// A saída de emergência. O aviso ANTES do PIN é de propósito: o operador
    /// precisa saber que está saindo do caminho normal e que o estorno vai
    /// aparecer, com o nome dele, na lista dos que não passaram pela gerência.
    /// </summary>
    public Task<Operador?> PedirPinAsync(string motivo)
    {
        // Título vale para os DOIS motivos de cair aqui (a nuvem falhou, ou o
        // operador escolheu o PIN): "WhatsApp indisponível" seria mentira no
        // segundo caso, e aviso que mente é aviso que o operador aprende a ignorar.
        var seguir = Dialogo.Confirmar(_dono, "Estorno sem aprovação da gerência",
            $"Motivo: {motivo}.\n\nDá para seguir com o PIN do supervisor, mas este estorno vai ficar " +
            "registrado como AUTORIZADO SEM APROVAÇÃO DA GERÊNCIA.",
            "Usar o PIN do supervisor", "Voltar");
        if (!seguir) return Task.FromResult<Operador?>(null);

        var pin = PedirSenha.Mostrar(_dono, "Autorização", "PIN do supervisor");
        if (pin is null) return Task.FromResult<Operador?>(null);

        using var cx = Banco.Abrir();
        var sup = Operadores.AutorizarSupervisor(cx, pin);
        if (sup is null)
            Dialogo.Avisar(_dono, "Não autorizado", "O PIN não confere ou não é de um supervisor.", "erro");
        return Task.FromResult(sup);
    }

    /// <summary>Lista de botões grandes (mesmo desenho do menu do TEF).</summary>
    private static int Escolher(Window dono, string titulo, string mensagem, string[] opcoes)
    {
        var escolhido = -1;
        var janela = Dialogo.Base(dono, 460);
        var painel = new StackPanel();
        painel.Children.Add(new TextBlock
        {
            Text = titulo, FontSize = 22, FontWeight = FontWeights.Bold,
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Texto"],
            TextWrapping = TextWrapping.Wrap,
        });
        painel.Children.Add(new TextBlock
        {
            Text = mensagem, FontSize = 15,
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextoFraco"],
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 6),
        });
        for (var i = 0; i < opcoes.Length; i++)
        {
            var idx = i;
            var b = new Button
            {
                Content = opcoes[i],
                Style = (Style)Application.Current.Resources["BotaoBase"],
                Margin = new Thickness(0, 8, 0, 0), MinHeight = 58, FontSize = 16,
            };
            b.Click += (_, _) => { escolhido = idx; janela.Close(); };
            painel.Children.Add(b);
        }
        janela.Content = Dialogo.Moldura(painel);
        janela.ShowDialog();
        return escolhido;
    }
}
