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
    private readonly bool _configuracao;

    /// <param name="configuracao">
    /// true quando o que está sendo liberado é a TELA DE CONFIGURAÇÃO, e não um
    /// estorno. Muda os textos (falar em "estorno" ao abrir a configuração só
    /// confunde quem está no caixa) e, principalmente, muda a SAÍDA: configuração
    /// cai na senha de administrador; estorno cai no PIN do supervisor.
    /// </param>
    public TelaAutorizacao(Window dono, bool configuracao = false)
    {
        _dono = dono;
        _configuracao = configuracao;
    }

    public IDisposable Aguardando(string mensagem) => new Espera(_dono, mensagem);

    public Task<RespostaCodigo> PedirCodigoAsync(RespostaSolicitacao pedido, string? aviso)
        => Task.FromResult(PedirCodigo.Mostrar(_dono, pedido, aviso));

    public Task<EscolhaAposFalha> EscolherAposFalhaAsync(string mensagem)
    {
        var i = Escolher(_dono, "Autorização", mensagem + " O que você quer fazer?", new[]
        {
            "Pedir um código novo",
            _configuracao ? "Usar a senha de administrador" : "Usar o PIN do supervisor",
            _configuracao ? "Desistir" : "Desistir do estorno",
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
        var seguir = Dialogo.Confirmar(_dono,
            _configuracao ? "Configuração sem aprovação da gerência"
                          : "Estorno sem aprovação da gerência",
            $"Motivo: {motivo}.\n\n" + (_configuracao
                ? "Dá para entrar com a senha de administrador, mas esta abertura vai ficar "
                  + "registrada como SEM APROVAÇÃO DA GERÊNCIA."
                : "Dá para seguir com o PIN do supervisor, mas este estorno vai ficar "
                  + "registrado como AUTORIZADO SEM APROVAÇÃO DA GERÊNCIA."),
            _configuracao ? "Usar a senha de administrador" : "Usar o PIN do supervisor", "Voltar");
        if (!seguir) return Task.FromResult<Operador?>(null);

        var senha = PedirSenha.Mostrar(_dono, "Autorização",
            _configuracao ? "Senha de administrador" : "PIN do supervisor");
        if (senha is null) return Task.FromResult<Operador?>(null);

        using var cx = Banco.Abrir();
        if (_configuracao)
        {
            // A senha de administrador NÃO é de um operador que opera caixa (a linha
            // '_admin_' fica inativa de propósito). Devolvemos ela mesma como
            // "quem autorizou" para a auditoria ter um nome, e não um vazio.
            if (!Configuracao.SenhaAdminConfere(cx, senha))
            {
                Dialogo.Avisar(_dono, "Senha incorreta", "A senha de administrador não confere.", "erro");
                return Task.FromResult<Operador?>(null);
            }
            return Task.FromResult<Operador?>(new Operador("_admin_", "Administrador", "gerente"));
        }

        var sup = Operadores.AutorizarSupervisor(cx, senha);
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
