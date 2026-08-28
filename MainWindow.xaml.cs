using System.Windows;
using System.Windows.Input;
using Dapper;
using Pdv.Nucleo;
using Pdv.Telas;

namespace Pdv;

/// <summary>
/// Casca do PDV. Decide o que mostrar, nesta ordem:
///
///   sem configuração  → tela de configuração (só na 1ª vez)
///   configurado       → LOGIN (é o que o operador vê ao ligar o caixa, sempre)
///   logado, sem caixa → ABERTURA DE CAIXA
///   caixa aberto      → VENDA
///
/// A configuração NÃO reaparece depois de feita: quem precisar mexer entra pelo
/// botão discreto no login, e ele exige senha de administrador.
/// </summary>
public partial class MainWindow : Window
{
    private Operador? _operador;
    private Sessao? _sessao;

    public MainWindow()
    {
        InitializeComponent();
        Banco.Migrar();
        // Quiosque em tela cheia, sempre (o XAML já nasce Maximized/WindowStyle=None).
        // O modo de homologação saiu quando a operação começou: ele abria a janela comum
        // E desligava as senhas, e num caixa de verdade isso é porta dos fundos.
        // Alt+F4 não pode fechar um caixa por acidente no meio da venda.
        // Ctrl+M minimiza — a janela quiosque não tem barra de título (o ─ da barra
        // própria faz o mesmo).
        PreviewKeyDown += (_, e) =>
        {
            if (e.SystemKey == Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) e.Handled = true;
            if (e.Key == Key.M && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                WindowState = WindowState.Minimized;
                e.Handled = true;
            }
        };
        Roteia();
    }

    private void Roteia()
    {
        using var cx = Banco.Abrir();

        // 1ª execução: sem terminal configurado ou sem nenhum operador cadastrado
        var configurado = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM terminal") > 0;
        if (!configurado || !Operadores.ExisteAlgum(cx)) { MostrarConfiguracao(); return; }

        if (_operador is null) { MostrarLogin(cx); return; }

        _sessao = Caixa.SessaoAberta(cx);
        // caixa de outro dia aberto: a tela de abertura explica e obriga a fechar antes
        if (_sessao is null || _sessao.BusinessDate != Caixa.DiaOperacional()) { MostrarAbertura(); return; }

        MostrarVenda();
    }

    private void MostrarLogin(Microsoft.Data.Sqlite.SqliteConnection cx)
    {
        var loja = cx.ExecuteScalar<string>("SELECT loja_nome FROM terminal LIMIT 1") ?? "";
        var t = new Login(loja);
        t.Entrou += op => { _operador = op; Roteia(); };
        t.PediuConfig += AbrirConfigProtegida;
        Conteudo.Content = t;
    }

    private void MostrarAbertura()
    {
        var t = new AberturaCaixa(_operador!);
        t.Abriu += s => { _sessao = s; Roteia(); };
        t.Saiu += () => { _operador = null; Roteia(); };
        Conteudo.Content = t;
    }

    private Venda? _telaVenda;

    private void MostrarVenda()
    {
        // Caixa aberto por OUTRO operador (foi embora sem fechar): quem entra
        // precisa saber que está assumindo o turno — e a conferência — de outra
        // pessoa. Sem este aviso, a diferença do fechamento cai em quem fechou,
        // que nem sabia que a gaveta não era dele.
        if (_sessao!.OperadorId != _operador!.Id)
        {
            var assume = Dialogo.Confirmar(this, "Caixa de outro operador",
                $"Este caixa foi aberto por {_sessao.OperadorNome} às {_sessao.AberturaEm:HH:mm} " +
                "e continua aberto. Ao entrar, você passa a operar o turno dele — e a " +
                "conferência do fechamento também vira responsabilidade sua.",
                "Assumir este caixa", "Voltar ao login");
            if (!assume) { _operador = null; Roteia(); return; }
            using var cx = Banco.Abrir();
            Caixa.Auditar(cx, null, "caixa_assumido", _operador.Id, null,
                $"turno aberto por {_sessao.OperadorNome} ({_sessao.OperadorId}) às {_sessao.AberturaEm:HH:mm}");
        }

        var t = new Venda(_operador!, _sessao!);
        t.Deslogou += () => { _operador = null; _telaVenda = null; Roteia(); };
        t.FechouCaixa += () => { _operador = null; _sessao = null; _telaVenda = null; Roteia(); };
        t.PediuKds += MostrarKds;
        t.PediuChat += MostrarChat;
        t.PediuConfig += AbrirConfigProtegida;
        _telaVenda = t;
        Conteudo.Content = t;
    }

    private Telas.ChatIfood? _telaChat;

    /// <summary>
    /// Chat do iFood: a tela nasce UMA vez e fica viva — trocar de aba não
    /// derruba o login do Gestor nem a conversa aberta.
    /// </summary>
    private void MostrarChat()
    {
        if (_telaChat is null)
        {
            _telaChat = new Telas.ChatIfood();
            _telaChat.Voltou += () => Conteudo.Content = _telaVenda;
        }
        Conteudo.Content = _telaChat;
    }

    /// <summary>
    /// KDS do quiosque: troca só o CONTEÚDO da janela — a tela de Venda fica viva
    /// atrás, com a comanda intacta. Voltar não recria nada.
    /// </summary>
    private void MostrarKds()
    {
        using var cx = Banco.Abrir();
        var loja = cx.ExecuteScalar<string>("SELECT loja_nome FROM terminal LIMIT 1") ?? "";
        var k = new Telas.Kds(loja);
        k.Voltou += () => Conteudo.Content = _telaVenda;
        Conteudo.Content = k;
    }

    private void MostrarConfiguracao()
    {
        var t = new Configuracao();
        t.Concluiu += () => Roteia();
        Conteudo.Content = t;
    }

    private void Minimizar(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>
    /// Fechar de verdade — com confirmação, porque no caixa da loja um toque perdido
    /// aqui derruba a frente de caixa com fila. O emissor fiscal morre junto (Agente).
    /// </summary>
    private void Fechar(object sender, RoutedEventArgs e)
    {
        if (Dialogo.Confirmar(this, "Fechar o PDV",
                "O caixa vai fechar (o turno aberto continua salvo). Fechar mesmo?",
                "Fechar o PDV", "Voltar", perigo: true))
            Application.Current.Shutdown();
    }

    /// <summary>
    /// Reconfigurar exige APROVAÇÃO DE FORA da loja: a nuvem manda um código de 6
    /// dígitos no WhatsApp do dono e da gerente (um código próprio para cada, para
    /// a auditoria saber quem liberou) e quem está no caixa digita.
    ///
    /// Por que não basta a senha: quem entra aqui muda série fiscal, ambiente da
    /// NFC-e e TEF — erra e a nota sai errada por dias sem ninguém perceber. Senha
    /// que mora no balcão vaza; código por WhatsApp exige o celular do dono.
    ///
    /// A SAÍDA é a senha de administrador, e ela existe de propósito: se a internet
    /// cair é exatamente quando alguém precisa entrar aqui para consertar o caixa.
    /// Toda entrada por essa porta fica marcada na auditoria.
    /// </summary>
    private async void AbrirConfigProtegida()
    {
        using (var cxT = Banco.Abrir())
        {
            var remota = Servicos.Autorizador();
            if (remota is not null)
            {
                var terminal = cxT.ExecuteScalar<string>("SELECT loja_nome FROM terminal LIMIT 1") ?? "PDV";
                var pedido = new PedidoAutorizacao(
                    Terminal: Autorizacao.NomeDoTerminal(cxT),
                    Referencia: "config-" + DateTime.Now.ToString("yyyyMMddHHmmss"),
                    ValorCent: 0,
                    Loja: terminal,
                    Operador: _operador?.Nome ?? "(sem login)")
                { Tipo = "configuracao" };

                var tela = new TelaAutorizacao(this);
                // Sem login (config aberta pela tela de login) o pedido ainda precisa
                // de um operador para a auditoria: o primeiro ativo serve de rótulo,
                // e quem de fato liberou é quem aprovou pelo WhatsApp.
                var quem = _operador ?? Operadores.PrimeiroAtivo(cxT);
                if (quem is null) return;
                DesfechoAutorizacao d;
                try
                {
                    d = await Autorizacao.ResolverAsync(cxT, remota, pedido, quem, tela, CancellationToken.None);
                }
                catch
                {
                    // Falha da autorização NUNCA pode derrubar o caixa (este método é
                    // async void): cai para a senha de administrador logo abaixo.
                    d = new DesfechoAutorizacao(ViaAutorizacao.Recusada, null, null, null, "falhou");
                }
                if (d.Autorizado)
                {
                    Caixa.Auditar(cxT, null, "config_aberta", _operador?.Id, d.AprovadoPor,
                        "liberada por código no WhatsApp" + Autorizacao.Trilha(d));
                    MostrarConfiguracao();
                    return;
                }
                if (d.Avisado) return;   // a tela já explicou (cancelou / código errado)
            }
        }

        // Saída: senha de administrador (nuvem fora do ar ou sem aprovação remota).
        var senha = PedirSenha.Mostrar(this, "Configuração do PDV", "Senha de administrador");
        if (senha is null) return;
        using var cx = Banco.Abrir();
        if (!Configuracao.SenhaAdminConfere(cx, senha))
        {
            Caixa.Auditar(cx, null, "config_negada", null, null, "senha de administrador incorreta");
            Dialogo.Avisar(this, "Senha incorreta", "A senha de administrador não confere.", "erro");
            return;
        }
        Caixa.Auditar(cx, null, "config_aberta", null, null, "pela senha de administrador (sem aprovação remota)");
        MostrarConfiguracao();
    }
}
