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
///
/// MODO DE HOMOLOGAÇÃO (config `homologacao` = 1, ver Pdv.Nucleo/ModoHomologacao):
/// login e abertura de caixa saem do caminho e a casca vai direto para a VENDA, com o
/// operador de teste e um turno de teste. É o roteiro do TEF que roda aqui, e nenhum
/// dos 58 passos fala de caixa ou de operador. Com a config desligada nada disso
/// existe: a ordem acima é a da loja, e continua sendo.
/// </summary>
public partial class MainWindow : Window
{
    private Operador? _operador;
    private Sessao? _sessao;

    public MainWindow()
    {
        InitializeComponent();
        Banco.Migrar();
        // A vigia do WhatsApp lembra "este caixa já esteve conectado" no banco do caixa.
        // Injetado aqui (e não Banco.Abrir() dentro do serviço) para a bateria de testes
        // exercitar o serviço sem tocar no pdv.db de quem compila.
        ServicoWhatsApp.Ligar(
            chave => { using var c = Banco.Abrir(); return Vendas.Config(c, chave); },
            (chave, valor) => { using var c = Banco.Abrir(); Vendas.GravarConfig(c, chave, valor); });
        // O WebView2 do WhatsApp nasce fora da tela segundos depois do login e costuma
        // levar o foco do teclado ao nascer: o leitor de código de barras bipava no vazio.
        CamadaWhatsApp.Iniciou += () => { if (!CamadaWhatsApp.IsHitTestVisible) DevolverTecladoAoCaixa(); };
        // NO CAIXA DE HOMOLOGACAO, JANELA COMUM (09/09/2026, pedido do dono: "tem como
        // tirar full screen desse modo de homologacao?"). Quem homologa tem o log da
        // biblioteca, a planilha e o PayGo abertos do lado; quiosque em tela cheia e o
        // certo para a loja e um estorvo para o roteiro. A senha continua ligada: o que
        // muda e so a moldura da janela.
        try
        {
            using var cxH = Banco.Abrir();
            if (Pdv.Nucleo.ModoHomologacao.Ligado(cxH))
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                WindowState = WindowState.Normal;
                Width = 1280; Height = 800;
            }
        }
        catch { /* sem banco, fica o quiosque de sempre */ }
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

        // A faixa do modo de homologação antes de tudo: ela vale para QUALQUER tela,
        // inclusive a de configuração, e quem liga o caixa tem que ver na hora.
        PintarFaixaHomologacao(cx);

        // 1ª execução: sem terminal configurado ou sem nenhum operador cadastrado
        var configurado = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM terminal") > 0;
        if (!configurado || !Operadores.ExisteAlgum(cx)) { MostrarConfiguracao(); return; }

        // ── MODO DE HOMOLOGAÇÃO ─────────────────────────────────────────────
        // Desligado (a loja): EncerrarSobras e EntradaDireta não fazem nada e o caixa
        // segue para o login como sempre. Ligado: entra direto na venda, com o
        // operador de teste e o turno de teste.
        // Turno de teste que ficou aberto depois de o modo sair do ar não pode
        // segurar a abertura do caixa da loja (só existe UM turno aberto por vez).
        ModoHomologacao.EncerrarSobras(cx);
        if (ModoHomologacao.EntradaDireta(cx) is { } teste)
        {
            _operador = teste.Operador;
            _sessao = teste.Sessao;
            MostrarVenda();
            return;
        }

        // A entrada direta não vale mais (o modo foi desligado com o PDV aberto, ou um
        // turno de gente apareceu), mas quem ficou na mão é o operador de TESTE. Ele não
        // segue: sem esta linha a tela de abertura viria com o nome dele no alto e o dia
        // da loja sairia assinado por um operador que não é gente. Volta para o login.
        if (ModoHomologacao.EhOperadorDeTeste(_operador?.Id)) _operador = null;

        // RETOMADA SEM LOGIN (11/09/2026, pedido do dono): o caixa fechou para atualizar (ou
        // caiu) com o turno aberto e em uso há pouco. Quem abriu o turno continua nele sem
        // digitar o PIN de novo. A regra e a janela moram em Caixa.PodeRetomar.
        if (_operador is null && Caixa.SessaoAberta(cx) is { } aberta
            && Caixa.PodeRetomar(aberta, Caixa.UltimaAtividade(cx), DateTime.Now)
            && Operadores.PorId(cx, aberta.OperadorId) is { } dono)
        {
            _operador = dono; _sessao = aberta;
            Caixa.Auditar(cx, null, "sessao_retomada", dono.Id, null,
                $"turno de {aberta.OperadorNome} retomado sem login: última atividade {Caixa.UltimaAtividade(cx):HH:mm}");
            MostrarVenda(); return;
        }

        if (_operador is null) { MostrarLogin(cx); return; }

        _sessao = Caixa.SessaoAberta(cx);
        // caixa de outro dia aberto: a tela de abertura explica e obriga a fechar antes
        if (_sessao is null || _sessao.BusinessDate != Caixa.DiaOperacional()) { MostrarAbertura(); return; }

        MostrarVenda();
    }

    /// <summary>
    /// A faixa do modo de homologação, no alto da janela, em toda tela. O texto sai do
    /// Núcleo para a tela e a suíte lerem a mesma frase.
    ///
    /// Com um turno de gente aberto o modo não tira o login do caminho, e a faixa diz
    /// isso com todas as letras: senão quem está com o roteiro na mão vê "modo de
    /// homologação", leva o login na cara e conclui que a mudança não funcionou.
    /// </summary>
    private void PintarFaixaHomologacao(Microsoft.Data.Sqlite.SqliteConnection cx)
    {
        TxtFaixaHomologacao.Text = ModoHomologacao.TituloFaixa;
        TxtFaixaHomologacaoDetalhe.Text = ModoHomologacao.BloqueadoPorTurnoDeVerdade(cx)
            ? ModoHomologacao.DetalheBloqueado
            : ModoHomologacao.DetalheFaixa;
        FaixaHomologacao.Visibility = ModoHomologacao.Ligado(cx) ? Visibility.Visible : Visibility.Collapsed;
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
        // Comparado no id CANÔNICO: o turno pode ter sido aberto com a identidade que
        // nasceu só nesta máquina e o login de agora já devolver a do painel (ou o
        // contrário). São a mesma pessoa — perguntar "quer assumir o caixa de outro?"
        // para quem abriu o próprio caixa treina o operador a dizer sim sem ler.
        using var cx = Banco.Abrir();
        if (Operadores.IdCanonico(cx, _sessao!.OperadorId) != Operadores.IdCanonico(cx, _operador!.Id))
        {
            var assume = Dialogo.Confirmar(this, "Caixa de outro operador",
                $"Este caixa foi aberto por {_sessao.OperadorNome} às {_sessao.AberturaEm:HH:mm} " +
                "e continua aberto. Ao entrar, você passa a operar o turno dele, e a " +
                "conferência do fechamento também vira responsabilidade sua.",
                "Assumir este caixa", "Voltar ao login");
            if (!assume) { _operador = null; Roteia(); return; }
            Caixa.Auditar(cx, null, "caixa_assumido", _operador.Id, null,
                $"turno aberto por {_sessao.OperadorNome} ({_sessao.OperadorId}) às {_sessao.AberturaEm:HH:mm}");
        }

        var t = new Venda(_operador!, _sessao!);
        t.Deslogou += () => { _operador = null; _telaVenda = null; Roteia(); };
        t.FechouCaixa += () => { _operador = null; _sessao = null; _telaVenda = null; Roteia(); };
        t.PediuKds += MostrarKds;
        t.PediuChat += MostrarChat;
        t.PediuWhatsApp += MostrarWhatsApp;
        t.PediuConfig += AbrirConfigProtegida;
        _telaVenda = t;
        Conteudo.Content = t;

        // Pré-aquece o chat: com o caixa aberto, o WebView2 do chat já começa a
        // observar as não lidas em segundo plano para o selo acender na venda
        // antes de alguém abrir o chat (idempotente; a própria tela se protege
        // de inicializar duas vezes).
        _ = CamadaChat.PreAquecerAsync();
        // O WhatsApp da loja idem: observa as não lidas em segundo plano desde a abertura.
        _ = CamadaWhatsApp.PreAquecerAsync();
    }

    private bool _chatLigado;

    /// <summary>
    /// Chat do iFood: a CAMADA (CamadaChat) vive na árvore desde o início e nunca
    /// é destacada — trocar de aba não derruba o login do Gestor, a conversa
    /// aberta nem o observador de não lidas. Mostrar/esconder é só Visibility.
    ///
    /// Na 1ª vez, liga o "Voltar" (esconde a camada) e pré-aquece o WebView2 para
    /// o selo de não lidas já acender na venda. Se a plataforma não inicializar o
    /// WebView2 enquanto a camada está oculta, o observador liga aqui, na 1ª
    /// abertura — fallback seguro, o chat nunca fica inacessível.
    /// </summary>
    private void MostrarChat()
    {
        if (!_chatLigado)
        {
            _chatLigado = true;
            CamadaChat.Voltou += () => CamadaChat.Visibility = Visibility.Collapsed;
        }
        EsconderWhatsApp();                                 // uma camada de cada vez
        CamadaChat.Visibility = Visibility.Visible;
        _ = CamadaChat.PreAquecerAsync();
    }

    private bool _whatsAppLigado;

    /// <summary>O WhatsApp da loja: mesma regra do chat (camada viva, some por Visibility).</summary>
    private void MostrarWhatsApp()
    {
        if (!_whatsAppLigado)
        {
            _whatsAppLigado = true;
            CamadaWhatsApp.Voltou += EsconderWhatsApp;
        }
        CamadaChat.Visibility = Visibility.Collapsed;       // uma camada de cada vez
        CamadaWhatsApp.RenderTransform = System.Windows.Media.Transform.Identity;
        CamadaWhatsApp.IsHitTestVisible = true;
        _ = CamadaWhatsApp.PreAquecerAsync();
    }

    /// <summary>
    /// Empurra a camada do WhatsApp para fora da janela em vez de recolhê-la: para o
    /// Chromium a página segue VISÍVEL (é assim que o WhatsApp Web toca o toque original
    /// dele quando chega mensagem). E devolve o teclado ao caixa: o HWND do WebView2 fora
    /// da tela seguraria as teclas do leitor de código de barras e do PIN.
    /// </summary>
    private void EsconderWhatsApp()
    {
        CamadaWhatsApp.RenderTransform = new System.Windows.Media.TranslateTransform(30000, 0);
        CamadaWhatsApp.IsHitTestVisible = false;
        DevolverTecladoAoCaixa();
    }

    /// <summary>O teclado (leitor de código de barras, PIN) volta para a tela que está na frente.</summary>
    private void DevolverTecladoAoCaixa()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            try { Keyboard.ClearFocus(); (Conteudo.Content as UIElement)?.Focus(); Focus(); } catch { }
        });
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
        // "Fale com o iFood" do detalhe: abre a aba do chat já procurando o pedido
        k.PediuAjudaIfood += numero => { MostrarChat(); _ = CamadaChat.FaleComIfoodAsync(numero); };
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
        if (Quiosque.Ligado)
        {
            // MODO QUIOSQUE (12/09/2026): o Windows abre direto no PDV. Fechar e mais nada
            // deixaria a tela preta; o operador escolhe o que vem depois.
            var o = Dialogo.Escolher(this, "Fechar o PDV",
                "O Windows abre direto no PDV (modo quiosque). O turno aberto continua salvo.",
                "Reiniciar o PDV", "Sair para o Windows", "Voltar");
            if (o == 0) { Quiosque.ReiniciarPdv(); Application.Current.Shutdown(); }
            else if (o == 1)
            {
                // Sair do quiosque é ação de admin (15/09/2026): só o usuário master da rede.
                if (!AcessoDoMaster("Sair para o Windows", "saida_windows", "saida_windows_negada")) return;
                Quiosque.AbrirExplorer(); Application.Current.Shutdown();
            }
            return;
        }
        if (Dialogo.Confirmar(this, "Fechar o PDV",
                "O caixa vai fechar (o turno aberto continua salvo). Fechar mesmo?",
                "Fechar o PDV", "Voltar", perigo: true))
            Application.Current.Shutdown();
    }

    /// <summary>
    /// Reconfigurar exige a SENHA DE ADMINISTRADOR. Quem entra aqui muda serie
    /// fiscal, ambiente da NFC-e e TEF — erra e a nota sai errada por dias sem
    /// ninguem perceber.
    ///
    /// O 2FA por WhatsApp saiu (28/08): num produto SaaS a senha e do DONO da
    /// loja, escolhida por ele no painel — depender do celular de alguem para
    /// abrir a configuracao trava o suporte de madrugada. Numerica, porque o
    /// caixa e de toque e o teclado da tela e numerico.
    /// </summary>
    private void AbrirConfigProtegida()
    {
        if (!AcessoDoMaster("Configuração do PDV", "config_aberta", "config_negada")) return;
        MostrarConfiguracao();
    }

    /// <summary>
    /// A PORTA DE TODA AÇÃO DE ADMIN (15/09/2026): Configuração (e por ela trocar maquininha,
    /// fiscal, quiosque) e sair para o Windows. A regra mora em UsuarioMaster.Conferir: com o
    /// usuário master da rede guardado, só ele; sem master, a senha criada na instalação.
    /// Operador, gerente ou não, nunca passa aqui (o Lucas abria a Configuração do Castelo
    /// com a própria senha).
    /// </summary>
    private bool AcessoDoMaster(string titulo, string eventoLiberado, string eventoNegado)
    {
        // conexão fechada enquanto a caixa de senha está aberta: o Atualizar pode gravar por trás
        string rotulo;
        using (var cx0 = Banco.Abrir()) rotulo = UsuarioMaster.Rotulo(cx0);
        var senha = PedirSenha.Mostrar(this, titulo, rotulo);
        if (senha is null) return false;

        using var cx = Banco.Abrir();
        var c = UsuarioMaster.Conferir(cx, senha);
        if (!c.Liberado)
        {
            Caixa.Auditar(cx, null, eventoNegado, null, null, UsuarioMaster.Detalhe(c) + ": senha incorreta");
            Dialogo.Avisar(this, "Senha incorreta", UsuarioMaster.NaoConfere(cx), "erro");
            return false;
        }
        Caixa.Auditar(cx, null, eventoLiberado, null, null, UsuarioMaster.Detalhe(c));
        return true;
    }
}
