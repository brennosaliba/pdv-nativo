using System.Windows;
using System.Windows.Controls;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// A entrada do operador, SO DIGITAL (1.0.54, decisao do dono de 27/10/2026).
///
/// O caixa nao usa mais senha de operador para entrar nem para fechar. A tela e uma linha e um
/// botao; a regra inteira mora no nucleo (PortaoDigital.EntrarAsync), onde a suite roda todos
/// os caminhos sem leitor e sem janela:
///  · digital reconhecida: entra;
///  · problema no leitor (ausente, sem driver, 3 leituras sem reconhecer, dedo machucado): a
///    reserva, o funcionario digita o CPF e o gerente geral ou o dono autoriza com o codigo,
///    auditada "pela reserva";
///  · primeira vez de cada funcionario neste caixa: o funcionario digita o CPF, o gerente
///    autoriza com o codigo e o caixa le o dedo 3 vezes; dali em diante so digital;
///  · biometria DESLIGADA no painel da loja: nao ha senha, entao toda entrada e pela reserva.
///
/// A tela de CPF e senha e o PIN legado sairam daqui (os dados continuam no banco). O usuario
/// master da rede nao e operador de turno: o botao Configuracao no rodape continua pedindo a
/// senha dele (MainWindow.AcessoDoMaster, UsuarioMaster.Conferir), como antes.
/// </summary>
public partial class Login : UserControl
{
    public event Action<Operador>? Entrou;
    public event Action? PediuConfig;

    /// <summary>A chave da loja (pdv_loja_config.biometria_turno, nasce ligada).</summary>
    private bool _ligada = true;
    private bool _entrando;

    public Login(string nomeLoja)
    {
        InitializeComponent();
        TxtLoja.Text = nomeLoja;
        Loaded += (_, _) =>
        {
            Focus();
            // Enquanto ninguem encosta o dedo, o caixa vai buscando quem entrou na folha: o CPF
            // da reserva e do primeiro cadastro confere contra os operadores desta loja.
            _ = Task.Run(async () => { try { await BaixarOperadoresAsync(); } catch { } });
            using (var cx = Banco.Abrir()) _ligada = Biometria.Ligada(cx);
            Pintar();
            // com a digital, a leitura ja abre; sem ela, o codigo espera o toque no botao
            if (_ligada) _ = EntrarAsync();
        };
        Pintar();
    }

    private void Pintar()
    {
        TxtEtapa.Text = _ligada ? Biometria.TituloEntrada : Biometria.TituloSemBiometria;
        BtnDigital.Content = _ligada ? Biometria.TituloEntrada : Biometria.BotaoSemBiometria;
    }

    /// <summary>
    /// Pergunta ao painel quem entrou na folha. ESTATICA de proposito: a trava de "uma busca por
    /// vez" so vale se for a MESMA entre uma tela de login e a seguinte (ver <see cref="BuscaNoPainel"/>).
    /// </summary>
    private static readonly BuscaNoPainel Painel = new(async () =>
    {
        using var cx2 = Banco.Abrir();
        return await Servicos.Nuvem().BaixarOperadoresAsync(cx2).ConfigureAwait(false);
    });

    private static Task<int> BaixarOperadoresAsync() => Painel.BuscarAsync();

    /// <summary>O botao da tela (e a abertura dela, com a digital ligada).</summary>
    private void LerDigital(object sender, RoutedEventArgs e) => _ = EntrarAsync();

    /// <summary>
    /// A ENTRADA. Quem entrou deixa a assinatura (via, hash do CPF, motivo da reserva ou do
    /// primeiro cadastro) em EntradaDigital para a tela de abertura gravar no turno.
    /// </summary>
    private async Task EntrarAsync()
    {
        if (_entrando) return;
        var dono = Window.GetWindow(this);
        if (dono is null) return;
        _entrando = true;
        try
        {
            TxtErro.Text = "";
            var d = await PortaoDigitalDaTela.EntrarAsync(dono, _ligada);
            if (!d.Entrou)
            {
                if (!d.Avisado) TxtErro.Text = d.Motivo;
                return;
            }
            EntradaDigital.Guardar(AssinaturaDigital.De(d));
            using var cx = Banco.Abrir();
            // nome, hash curto do CPF, "pela reserva: motivo" e quem autorizou (nivel do codigo)
            Caixa.Auditar(cx, null, "login", d.Operador!.Id, null, d.LinhaDeAuditoria);
            Entrou?.Invoke(d.Operador);
        }
        catch (Exception ex)
        {
            TxtErro.Text = "A entrada falhou (" + ex.Message + ").";
        }
        finally { _entrando = false; }
    }

    private void AbrirConfig(object sender, RoutedEventArgs e) => PediuConfig?.Invoke();
}
