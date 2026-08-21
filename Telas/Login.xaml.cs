using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// Entrada do operador em duas etapas: CPF (quem é) e senha (prova).
///
/// O CPF é a identidade porque "foi ele que abriu o caixa" precisa apontar para um
/// documento — numa discussão trabalhista ou numa auditoria, PIN é "um número que
/// alguém sabia"; CPF+senha é uma pessoa. O CPF aparece na tela (não é segredo);
/// a senha vira bolinhas.
///
/// Transição sem tranca: quem AINDA não tem CPF cadastrado entra só com o PIN de
/// antes — digitou 4-6 dígitos e mandou Entrar, é tratado como PIN legado. Assim que
/// o CPF da pessoa é cadastrado, o caminho fraco fecha sozinho para ela.
///
/// Bloqueio progressivo por tentativa errada: senha curta é adivinhável por força
/// bruta se nada segurar. Não tranca o caixa (isso pararia a loja) — só atrasa.
/// </summary>
public partial class Login : UserControl
{
    private enum Etapa { Cpf, Senha }

    private Etapa _etapa = Etapa.Cpf;
    private string _cpf = "";
    private string _senha = "";
    private int _erros;
    private DateTime _bloqueadoAte = DateTime.MinValue;

    public event Action<Operador>? Entrou;
    public event Action? PediuConfig;

    public Login(string nomeLoja)
    {
        InitializeComponent();
        TxtLoja.Text = nomeLoja;
        Teclado.Digitou += d => { if (Buffer.Length < Maximo) { Buffer += d; Pintar(); } };
        Teclado.Apagou += Apagar;
        Teclado.Limpou += () => { Buffer = ""; Pintar(); };
        Loaded += (_, _) => Focus();
        Pintar();

        // Modo de homologação: entra direto como o primeiro operador ativo (gerente/supervisor
        // primeiro) — sem CPF/PIN. Só vale com a config 'homologacao' ligada.
        Loaded += (_, _) =>
        {
            try
            {
                using var cx = Banco.Abrir();
                if (!Vendas.Homologacao(cx)) return;
                var op = Operadores.PrimeiroAtivo(cx);
                if (op is null) return;
                Caixa.Auditar(cx, null, "login", op.Id, null, $"{op.Nome} (homologação, sem senha)");
                Dispatcher.BeginInvoke(() => Entrou?.Invoke(op));
            }
            catch { /* sem modo homologação, login normal */ }
        };
    }

    private int Maximo => _etapa == Etapa.Cpf ? 11 : 6;

    private string Buffer
    {
        get => _etapa == Etapa.Cpf ? _cpf : _senha;
        set { if (_etapa == Etapa.Cpf) _cpf = value; else _senha = value; }
    }

    private void Apagar()
    {
        // apagar com a senha vazia volta para a etapa do CPF — errou o CPF, corrige
        if (_etapa == Etapa.Senha && _senha.Length == 0) { _etapa = Etapa.Cpf; Pintar(); return; }
        if (Buffer.Length > 0) { Buffer = Buffer[..^1]; Pintar(); }
    }

    private void Pintar()
    {
        if (_etapa == Etapa.Cpf)
        {
            TxtEtapa.Text = "CPF do funcionário  (ou o PIN de quem ainda não tem CPF)";
            TxtPin.Text = FormatarCpfParcial(_cpf);
            TxtPin.FontSize = 34;
            BtnEntrar.IsEnabled = _cpf.Length == 11 || Operadores.PinValido(_cpf);
        }
        else
        {
            TxtEtapa.Text = $"Senha de {Documentos.Formatar(_cpf)}";
            TxtPin.Text = new string('●', _senha.Length);
            TxtPin.FontSize = 44;
            BtnEntrar.IsEnabled = Operadores.PinValido(_senha);
        }
        TxtErro.Text = "";
    }

    /// <summary>CPF vai aparecendo pontuado enquanto digita — não é segredo, é conferência.</summary>
    private static string FormatarCpfParcial(string d)
    {
        if (d.Length == 0) return "";
        var s = d.Length > 3 ? d[..3] + "." + d[3..] : d;
        if (d.Length > 6) s = s[..7] + "." + s[7..];
        if (d.Length > 9) s = s[..11] + "-" + s[11..];
        return s;
    }

    /// <summary>Teclado físico funciona igual — muitos caixas têm um.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var d = e.Key is >= Key.D0 and <= Key.D9 ? (e.Key - Key.D0).ToString()
              : e.Key is >= Key.NumPad0 and <= Key.NumPad9 ? (e.Key - Key.NumPad0).ToString() : null;
        if (d is not null && Buffer.Length < Maximo) { Buffer += d; Pintar(); e.Handled = true; }
        else if (e.Key == Key.Back) { Apagar(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Buffer = ""; Pintar(); e.Handled = true; }
        else if (e.Key == Key.Enter && BtnEntrar.IsEnabled) { Entrar(this, new RoutedEventArgs()); e.Handled = true; }
    }

    private void Entrar(object sender, RoutedEventArgs e)
    {
        var espera = _bloqueadoAte - DateTime.Now;
        if (espera > TimeSpan.Zero)
        {
            TxtErro.Text = $"Aguarde {Math.Ceiling(espera.TotalSeconds)}s para tentar de novo.";
            return;
        }

        using var cx = Banco.Abrir();

        if (_etapa == Etapa.Cpf)
        {
            // 11 dígitos = CPF; 4-6 = PIN legado de quem ainda não tem CPF
            if (_cpf.Length == 11)
            {
                if (!Documentos.CpfValido(_cpf))
                {
                    TxtErro.Text = "CPF inválido — confira os dígitos.";
                    return;
                }
                _etapa = Etapa.Senha;
                _senha = "";
                Pintar();
                return;
            }

            var legado = Operadores.Entrar(cx, _cpf, out var motivo);
            if (motivo == Operadores.Recusa.PinDuplicado)
            {
                _cpf = "";
                Pintar();
                TxtErro.Text = "Esse PIN está cadastrado para duas pessoas. Procure o gerente.";
                Caixa.Auditar(cx, null, "login_pin_duplicado", null, null, null);
                return;
            }
            if (legado is null) { Errou(cx, "PIN incorreto."); return; }
            Entrou4(cx, legado, "pin");
            return;
        }

        var op = Operadores.EntrarComCpf(cx, _cpf, _senha);
        if (op is null)
        {
            // volta pra senha vazia, não pro CPF: o CPF provavelmente está certo
            _senha = "";
            Pintar();
            Errou(cx, "Senha incorreta.");
            return;
        }
        Entrou4(cx, op, $"cpf={_cpf}");
    }

    private void Errou(Microsoft.Data.Sqlite.SqliteConnection cx, string mensagem)
    {
        _erros++;
        if (_etapa == Etapa.Cpf) { _cpf = ""; Pintar(); }
        if (_erros >= 3)
        {
            var seg = Math.Min(30, 5 * (_erros - 2));   // 5s, 10s, 15s… até 30s
            _bloqueadoAte = DateTime.Now.AddSeconds(seg);
            TxtErro.Text = $"{mensagem} Aguarde {seg}s.";
            Caixa.Auditar(cx, null, "login_bloqueado", null, null, $"{_erros} tentativas");
        }
        else TxtErro.Text = mensagem;
    }

    private void Entrou4(Microsoft.Data.Sqlite.SqliteConnection cx, Operador op, string como)
    {
        _erros = 0;
        _cpf = "";
        _senha = "";
        _etapa = Etapa.Cpf;
        Pintar();
        Caixa.Auditar(cx, null, "login", op.Id, null, $"{op.Nome} ({como})");
        Entrou?.Invoke(op);
    }

    private void AbrirConfig(object sender, RoutedEventArgs e) => PediuConfig?.Invoke();
}
