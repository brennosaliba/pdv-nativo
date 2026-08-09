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
        // Alt+F4 não pode fechar um caixa por acidente no meio da venda.
        // Ctrl+M minimiza — a janela quiosque não tem barra de título.
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

    private void MostrarVenda()
    {
        var t = new Venda(_operador!, _sessao!);
        t.Deslogou += () => { _operador = null; Roteia(); };
        t.FechouCaixa += () => { _operador = null; _sessao = null; Roteia(); };
        Conteudo.Content = t;
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
    /// Reconfigurar exige senha de administrador. Sem isso qualquer um trocaria a
    /// série fiscal ou o ambiente no meio do expediente — e a nota sai errada sem
    /// ninguém perceber, até o contador reclamar.
    /// </summary>
    private void AbrirConfigProtegida()
    {
        var senha = PedirSenha.Mostrar(this, "Configuração do PDV", "Senha de administrador");
        if (senha is null) return;
        using var cx = Banco.Abrir();
        if (!Configuracao.SenhaAdminConfere(cx, senha))
        {
            Caixa.Auditar(cx, null, "config_negada", null, null, "senha de administrador incorreta");
            Dialogo.Avisar(this, "Senha incorreta", "A senha de administrador não confere.", "erro");
            return;
        }
        Caixa.Auditar(cx, null, "config_aberta", null, null, null);
        MostrarConfiguracao();
    }
}
