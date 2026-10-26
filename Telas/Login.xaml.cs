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

    // 1.0.52: a digital no lugar da senha. _modoDigital = a chave da loja esta ligada (nasce ligada);
    // _primeiraVez = o operador tocou em "Primeira vez" e volta ao CPF e senha, que cadastra a digital.
    private bool _modoDigital;
    private bool _primeiraVez;
    private bool _lendoDigital;


    public Login(string nomeLoja)
    {
        InitializeComponent();
        TxtLoja.Text = nomeLoja;
        Teclado.Digitou += d => { if (Buffer.Length < Maximo) { Buffer += d; Pintar(); } };
        Teclado.Apagou += Apagar;
        Teclado.Limpou += () => { Buffer = ""; Pintar(); };
        Loaded += (_, _) =>
        {
            Focus();
            // Enquanto ninguém digita, o caixa vai buscando quem entrou na folha. Sem
            // esperar e sem avisar: se chegar a tempo, o funcionário novo nem percebe
            // que faltava alguma coisa. Se não chegar, o Entrar pergunta de novo.
            _ = Task.Run(async () => { try { await BaixarOperadoresAsync(); } catch { } });
            // 1.0.52: a digital no lugar da senha (ligada por padrao; o painel desliga por loja)
            bool digital;
            using (var cx = Banco.Abrir()) digital = Biometria.Ligada(cx);
            if (digital && !_modoDigital)
            {
                _modoDigital = true;
                Pintar();
                _ = LerDigitalAsync();
            }
        };
        Pintar();

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
        // 1.0.52: no modo digital (sem a primeira vez) a tela e uma linha e um botao
        var soDigital = _modoDigital && !_primeiraVez;
        Teclado.Visibility = soDigital ? Visibility.Collapsed : Visibility.Visible;
        TxtPin.Visibility = soDigital ? Visibility.Collapsed : Visibility.Visible;
        BtnEntrar.Visibility = soDigital ? Visibility.Collapsed : Visibility.Visible;
        BtnDigital.Visibility = soDigital ? Visibility.Visible : Visibility.Collapsed;
        if (soDigital)
        {
            TxtEtapa.Text = Biometria.TituloEntrada;
            TxtErro.Text = "";
            return;
        }
        if (_etapa == Etapa.Cpf)
        {
            TxtEtapa.Text = _modoDigital ? "Primeira vez: digite seu CPF" : "Digite seu CPF (ou o PIN antigo)";
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

    /// <summary>
    /// Quanto a tela espera o painel antes de desistir e recusar o login. Curto de
    /// propósito: é balcão com fila, e a busca ainda pode terminar sozinha e valer na
    /// próxima tentativa.
    /// </summary>
    private const int SegundosDeBusca = 8;

    private bool _buscando;

    /// <summary>
    /// Pergunta ao painel quem entrou na folha. Conexão PRÓPRIA: a busca pode terminar
    /// depois de a tela ter fechado a dela, e escrever num banco já disposto derrubaria
    /// a única tela que a loja tem para entrar.
    /// </summary>
    ///
    /// ESTÁTICA de propósito: a trava de descanso e a de "uma busca por vez" (ver
    /// <see cref="BuscaNoPainel"/>) só valem se forem as MESMAS entre uma tela de login
    /// e a seguinte. Cada logout monta uma tela nova, e uma busca por tela era
    /// exatamente como as chamadas empilhavam.
    private static readonly BuscaNoPainel Painel = new(async () =>
    {
        using var cx2 = Banco.Abrir();
        return await Servicos.Nuvem().BaixarOperadoresAsync(cx2).ConfigureAwait(false);
    });

    private static Task<int> BaixarOperadoresAsync() => Painel.BuscarAsync();

    /// <summary>O botao "Encoste o dedo no leitor" (e a abertura da tela): a leitura da digital.</summary>
    private void LerDigital(object sender, RoutedEventArgs e) => _ = LerDigitalAsync();

    /// <summary>
    /// A ENTRADA PELA DIGITAL (1.0.52). A maquina de estados mora em PortaoDigital.IdentificarAsync:
    /// reconheceu = entra (sem senha); 3 sem reconhecer, sem leitor ou sem driver = reserva (codigo
    /// do gerente ou do dono + lista); Primeira vez = volta ao CPF e senha, que cadastra a digital
    /// (ver Entrar). A assinatura (via, hash do CPF, motivo) fica em EntradaDigital para a tela de
    /// abertura gravar no turno.
    /// </summary>
    private async Task LerDigitalAsync()
    {
        if (_lendoDigital || !_modoDigital) return;
        var dono = Window.GetWindow(this);
        if (dono is null) return;
        _lendoDigital = true;
        try
        {
            _primeiraVez = false;
            Pintar();
            var d = await PortaoDigitalDaTela.IdentificarAsync(dono, "entrada", ofertaPrimeiraVez: true);
            if (d.PrimeiraVez)
            {
                _primeiraVez = true;
                _etapa = Etapa.Cpf; _cpf = ""; _senha = "";
                Pintar();
                return;
            }
            if (!d.Entrou)
            {
                if (!d.Avisado) TxtErro.Text = d.Motivo;
                return;
            }
            using var cx = Banco.Abrir();
            EntradaDigital.Guardar(AssinaturaDigital.De(d));
            Entrou4(cx, d.Operador!, d.Via == ViaDigital.Digital ? "digital" : d.Motivo);
        }
        catch (Exception ex)
        {
            TxtErro.Text = "A leitura da digital falhou (" + ex.Message + ").";
        }
        finally { _lendoDigital = false; }
    }

    /// <summary>
    /// A PRIMEIRA VEZ (1.0.52): a senha provou quem e; agora o caixa colhe a digital 3 vezes, liga
    /// ao CPF e entra. Quem ja tem digital neste caixa nao entra mais pela senha (so pela digital
    /// ou pela reserva): senao a senha copiada continuaria valendo.
    /// </summary>
    private async Task<bool> PrimeiraVezAsync(Operador op, string cpf)
    {
        var dono = Window.GetWindow(this);
        if (dono is null) return false;
        using (var cx = Banco.Abrir())
            if (Digitais.PorCpf(cx, cpf) is not null)
            {
                TxtErro.Text = Biometria.AvisoJaTemDigital;
                return false;
            }
        var guardada = await PortaoDigitalDaTela.CadastrarAsync(dono, op, Biometria.TituloPrimeiraVez, null);
        if (guardada is null) { TxtErro.Text = Biometria.AvisoCadastroCancelado; return false; }
        EntradaDigital.Guardar(new AssinaturaDigital(Biometria.ViaDigitalTexto, guardada.CpfHash, "primeira vez: senha e cadastro da digital"));
        return true;
    }

    private async void Entrar(object sender, RoutedEventArgs e)
    {
        if (_buscando) return;
        var espera = _bloqueadoAte - DateTime.Now;
        if (espera > TimeSpan.Zero)
        {
            TxtErro.Text = $"Espere {(int)Math.Ceiling(espera.TotalSeconds)}s e tente de novo.";
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
                    TxtErro.Text = "Esse CPF não confere. Confira os números.";
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
                TxtErro.Text = "Esse PIN está cadastrado para duas pessoas. Chame o gerente.";
                Caixa.Auditar(cx, null, "login_pin_duplicado", null, null, null);
                return;
            }
            if (legado is null) { Errou(cx, "PIN errado."); return; }
            Entrou4(cx, legado, "pin");
            return;
        }

        // NÃO BATEU AQUI? PERGUNTA AO PAINEL, UMA VEZ.
        //
        // Savassi, 08/09/2026: três operadores cadastrados no painel de madrugada e
        // nenhum conseguia entrar, com CPF e senha certos. O caixa só aprendia quem
        // entrou na folha pelo botão Sincronizar e pelo aviso de catálogo novo, os dois
        // na tela de VENDA, que só existe depois do login. Caixa sem ninguém logado não
        // tinha como sair disso. Cobre também a senha trocada no painel.
        Operador? op;
        var estava = TxtErro.Text;
        try
        {
            _buscando = true;
            BtnEntrar.IsEnabled = false;
            TxtErro.Text = "Conferindo no painel…";
            // O teto mora no núcleo (Operadores.EntrarComCpfAsync), onde a suíte prova
            // que rede pendurada não segura o caixa.
            (op, _) = await Operadores.EntrarComCpfAsync(cx, _cpf, _senha, BaixarOperadoresAsync,
                TimeSpan.FromSeconds(SegundosDeBusca));
        }
        catch { op = null; }
        finally
        {
            _buscando = false;
            if (TxtErro.Text == "Conferindo no painel…") TxtErro.Text = estava;
            BtnEntrar.IsEnabled = true;
        }

        if (op is null)
        {
            // volta pra senha vazia, não pro CPF: o CPF provavelmente está certo
            _senha = "";
            Pintar();
            Errou(cx, "Senha errada.");
            return;
        }
        // 1.0.52: com a digital ligada, a senha so vale na primeira vez, e cadastra a digital
        if (_modoDigital)
        {
            var cpf = _cpf;
            var entrou = await PrimeiraVezAsync(op, cpf);
            if (!entrou) { _senha = ""; Pintar(); return; }
            Entrou4(cx, op, $"cpf={cpf} · primeira vez: cadastrou a digital");
            return;
        }
        Entrou4(cx, op, $"cpf={_cpf}");
    }

    /// <summary>
    /// <paramref name="oQueHouve"/> diz SÓ o que deu errado ("Senha errada.") —
    /// a saída é colada aqui, porque ela muda quando a espera entra. Antes as
    /// duas metades vinham juntas do chamador e a tela se contradizia:
    /// "tente de novo. Aguarde 15s."
    /// </summary>
    private void Errou(Microsoft.Data.Sqlite.SqliteConnection cx, string oQueHouve)
    {
        _erros++;
        if (_etapa == Etapa.Cpf) { _cpf = ""; Pintar(); }
        if (_erros >= 3)
        {
            var seg = Math.Min(30, 5 * (_erros - 2));   // 5s, 10s, 15s… até 30s
            _bloqueadoAte = DateTime.Now.AddSeconds(seg);
            TxtErro.Text = $"{oQueHouve} Espere {seg}s e tente de novo.";
            Caixa.Auditar(cx, null, "login_bloqueado", null, null, $"{_erros} tentativas");
        }
        else TxtErro.Text = $"{oQueHouve} Digite de novo.";
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
