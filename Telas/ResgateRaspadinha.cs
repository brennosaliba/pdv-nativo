using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// A TELA DE RESGATE MANUAL DA RASPADINHA (08/10/2026, SQL 154), montada em código como o
/// SeletorComanda e o DetalhePedidoKds. Cinco passos numa janela só, com rolagem só no miolo:
///   [1] o código do cliente e o Conferir (com os códigos vivos da conversa, quando a tela abriu
///       por um pedido);  [2] a linha do prêmio ou a recusa;  [3] para onde vai (os pedidos
///   abertos da loja, o sugerido primeiro, e o balcão);  [4] o sabor, só se o prêmio pede;
///   [5] avisar o cliente no chat (só pedido do iFood com o chat da loja ligado).
/// Depois do resgate a janela fica aberta com o resultado e o botão Desfazer (2 h, com o código
/// do autenticador do dono).
///
/// A tela NÃO decide nada: o servidor confere, resgata e desfaz; esta janela só pergunta e
/// mostra (Pdv.Nucleo/ResgateManual tem o que dá para provar sem WPF). A resposta do resgate é
/// executada pelo caixa pelo MESMO caminho do chat automático
/// (<see cref="ServicoConversaChat.ExecutarRespostaManualAsync"/>: papel pela reserva do terminal,
/// envio pelo portão, aviso). Resgatar nunca fica na fila: sem resposta, nada foi feito.
///
/// A suíte abre esta janela fora da tela com um servidor falso e troca os diálogos modais
/// (confirmação e código do dono) por delegates: ver TestesResgateManual.
/// </summary>
public sealed class ResgateRaspadinha
{
    private static Brush R(string chave) => (Brush)Application.Current.Resources[chave];

    /// <summary>Resultado de uma chamada ao servidor: (status, corpo).</summary>
    public delegate Task<(int Status, string? Corpo)> Servidor(string nome, string corpo);

    private readonly Window _janela;
    private readonly Servidor _servidor;
    private readonly Func<int, string?, Task<(RespostaConversa Resposta, bool ComandaSaiu, bool TemTextoParaColar)>> _executar;
    private readonly string _operador;
    private readonly string? _terminalUuid;
    private readonly string? _orderIdInicial;
    private readonly string? _numeroInicial;
    private readonly bool _chatAtivo;

    // os diálogos modais, trocáveis pela suíte
    private Func<string, bool> _confirmar;
    private Func<string?, string?> _pedirCodigo;

    private readonly TextBox _txtCodigo;
    private readonly Button _btnConferir;
    private readonly WrapPanel _chips;
    private readonly TextBlock _txtLinha;
    private readonly StackPanel _blocoDestino;
    private readonly StackPanel _destinos;
    private readonly StackPanel _blocoSabor;
    private readonly WrapPanel _sabores;
    private readonly TextBlock _txtApoioSabor;
    private readonly CheckBox _chkAvisar;
    private readonly Button _btnResgatar;
    private readonly Button _btnVoltar;
    private readonly Button _btnDesfazer;
    private readonly Button _btnColar;
    private readonly TextBlock _txtOperador;

    private ConferenciaManual? _conferencia;
    private CodigoConferido? _codigo;
    private List<string> _escolhidos = new();
    private bool _ocupado;
    private string? _bonusId;
    private DateTime _resgatadoEm;
    private string? _textoParaColar;
    private string? _orderIdResgatado;
    private string? _numeroResgatado;

    /// <summary>O último texto da linha de resultado (a suíte lê).</summary>
    public string Linha => _txtLinha.Text;
    /// <summary>O bônus resgatado nesta janela, ou nulo.</summary>
    public string? BonusId => _bonusId;

    public ResgateRaspadinha(Window dono, Servidor servidor, string operadorNome, string? terminalUuid,
        string? codigoInicial, string? orderIdInicial, string? numeroInicial, bool chatAtivo,
        Func<int, string?, Task<(RespostaConversa Resposta, bool ComandaSaiu, bool TemTextoParaColar)>>? executar = null)
    {
        _servidor = servidor;
        _executar = executar ?? ServicoConversaChat.ExecutarRespostaManualAsync;
        _operador = ResgateManual.Operador(operadorNome);
        _terminalUuid = terminalUuid;
        _orderIdInicial = string.IsNullOrWhiteSpace(orderIdInicial) ? null : orderIdInicial.Trim();
        _numeroInicial = numeroInicial;
        _chatAtivo = chatAtivo;
        _confirmar = pergunta => Dialogo.Confirmar(_janela!, ResgateManual.Titulo, pergunta, "Resgatar", "Voltar");
        _pedirCodigo = aviso => PedirCodigo.Mostrar(_janela!, aviso ?? ResgateManual.TextoPedirCodigoDono);

        _janela = Dialogo.Base(dono, 560);
        _janela.MaxHeight = Math.Max(400, AlturaDaTela(dono) * 0.92);

        // três faixas: cabeçalho fixo, o miolo que rola, os botões sempre à vista
        var raiz = new Grid();
        raiz.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        raiz.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        raiz.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var cab = new StackPanel();
        cab.Children.Add(PedirValor.Cabecalho(_janela, ResgateManual.Titulo));
        _txtOperador = Texto("Operador: " + _operador, 14, "TextoFraco");
        _txtOperador.Margin = new Thickness(0, -6, 0, 10);
        cab.Children.Add(_txtOperador);
        Grid.SetRow(cab, 0);
        raiz.Children.Add(cab);

        var miolo = new StackPanel();

        // [1] o código
        miolo.Children.Add(Rotulo("CÓDIGO DO CLIENTE"));
        var linhaCod = new Grid();
        linhaCod.ColumnDefinitions.Add(new ColumnDefinition());
        linhaCod.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _txtCodigo = new TextBox
        {
            Text = (codigoInicial ?? "").Trim().ToUpperInvariant(), FontSize = 22, MinHeight = 52, MaxLength = ResgateManual.MaxCodigo,
            CharacterCasing = CharacterCasing.Upper, Padding = new Thickness(12, 8, 12, 8), VerticalContentAlignment = VerticalAlignment.Center,
            Background = R("Painel"), Foreground = R("Texto"), BorderBrush = R("Borda"),
        };
        _txtCodigo.GotFocus += (_, _) => PedirTexto.AbrirTecladoVirtualSeTouch();
        _txtCodigo.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = ConferirAsync(); } };
        _txtCodigo.TextChanged += (_, _) => { if (_codigo is not null && _txtCodigo.Text.Trim() != _codigo.Codigo) LimparResultado(); };
        _btnConferir = Botao("Conferir", true);
        _btnConferir.MinWidth = 140;
        _btnConferir.Margin = new Thickness(10, 0, 0, 0);
        _btnConferir.Click += (_, _) => _ = ConferirAsync();
        Grid.SetColumn(_txtCodigo, 0); Grid.SetColumn(_btnConferir, 1);
        linhaCod.Children.Add(_txtCodigo); linhaCod.Children.Add(_btnConferir);
        miolo.Children.Add(linhaCod);
        _chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        miolo.Children.Add(_chips);

        // [2] a linha do prêmio ou da recusa
        _txtLinha = Texto("", 16, "Texto");
        _txtLinha.FontWeight = FontWeights.SemiBold;
        _txtLinha.Margin = new Thickness(0, 12, 0, 0);
        miolo.Children.Add(_txtLinha);

        // [3] para onde vai
        _blocoDestino = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
        _blocoDestino.Children.Add(Rotulo("PARA ONDE VAI"));
        _destinos = new StackPanel();
        _blocoDestino.Children.Add(_destinos);
        miolo.Children.Add(_blocoDestino);

        // [4] o sabor
        _blocoSabor = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
        _blocoSabor.Children.Add(Rotulo("SABOR"));
        _sabores = new WrapPanel();
        _blocoSabor.Children.Add(_sabores);
        _txtApoioSabor = Texto("", 13, "TextoFraco");
        _blocoSabor.Children.Add(_txtApoioSabor);
        miolo.Children.Add(_blocoSabor);

        // [5] avisar o cliente
        _chkAvisar = new CheckBox
        {
            Content = "Avisar o cliente no chat", IsChecked = true, FontSize = 15, Margin = new Thickness(0, 14, 0, 0),
            Foreground = R("Texto"), Visibility = Visibility.Collapsed,
        };
        miolo.Children.Add(_chkAvisar);

        var rolagem = new ScrollViewer
        {
            Content = miolo, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 4, 0),
        };
        Grid.SetRow(rolagem, 1);
        raiz.Children.Add(rolagem);

        // o rodapé
        var rodape = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var linhaBotoes = new Grid();
        linhaBotoes.ColumnDefinitions.Add(new ColumnDefinition());
        linhaBotoes.ColumnDefinitions.Add(new ColumnDefinition());
        _btnVoltar = Botao("Voltar", false);
        _btnVoltar.Margin = new Thickness(0, 0, 6, 0);
        _btnVoltar.Click += (_, _) => _janela.Close();
        _btnResgatar = Botao("Resgatar", true);
        _btnResgatar.Margin = new Thickness(6, 0, 0, 0);
        _btnResgatar.IsEnabled = false;
        _btnResgatar.Click += (_, _) => _ = ResgatarAsync();
        Grid.SetColumn(_btnVoltar, 0); Grid.SetColumn(_btnResgatar, 1);
        linhaBotoes.Children.Add(_btnVoltar); linhaBotoes.Children.Add(_btnResgatar);
        rodape.Children.Add(linhaBotoes);
        var linhaDepois = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        linhaDepois.ColumnDefinitions.Add(new ColumnDefinition());
        linhaDepois.ColumnDefinitions.Add(new ColumnDefinition());
        _btnDesfazer = Botao("Desfazer", false);
        _btnDesfazer.Margin = new Thickness(0, 0, 6, 0);
        _btnDesfazer.Visibility = Visibility.Collapsed;
        _btnDesfazer.Click += (_, _) => _ = DesfazerAsync();
        _btnColar = Botao("Abrir e colar", false);
        _btnColar.Margin = new Thickness(6, 0, 0, 0);
        _btnColar.Visibility = Visibility.Collapsed;
        _btnColar.Click += (_, _) => _ = ColarAsync();
        Grid.SetColumn(_btnDesfazer, 0); Grid.SetColumn(_btnColar, 1);
        linhaDepois.Children.Add(_btnDesfazer); linhaDepois.Children.Add(_btnColar);
        rodape.Children.Add(linhaDepois);
        Grid.SetRow(rodape, 2);
        raiz.Children.Add(rodape);

        _janela.Content = Dialogo.Moldura(raiz);
        _janela.KeyDown += (_, e) => { if (e.Key == Key.Escape && !_ocupado) _janela.Close(); };
        _janela.Loaded += (_, _) =>
        {
            if (_txtCodigo.Text.Length > 0) _ = ConferirAsync();
            else if (_orderIdInicial is not null) _ = ConferirAsync();
            _txtCodigo.Focus();
        };
    }

    /// <summary>Abre a janela (modal) e devolve o bônus resgatado, ou nulo.</summary>
    public string? Mostrar()
    {
        _janela.ShowDialog();
        return _bonusId;
    }

    /// <summary>A janela, para a suíte (abre fora da tela, sem ShowDialog).</summary>
    public Window Janela => _janela;

    // ── [1] CONFERIR ─────────────────────────────────────────────────────────

    /// <summary>Confere o código digitado (não queima nada). Sem código e com pedido: só os pedidos e os candidatos.</summary>
    public async Task ConferirAsync()
    {
        if (_ocupado || _bonusId is not null) return;
        var codigo = _txtCodigo.Text.Trim();
        if (codigo.Length == 0 && _orderIdInicial is null)
        {
            Mostrar(ResgateManual.TextoSemCodigo, "erro");
            _txtCodigo.Focus();
            return;
        }
        Ocupar(true);
        Mostrar(ResgateManual.TextoConferindo, "fraco");
        int st = -1; string? corpo = null;
        try { (st, corpo) = await _servidor(ResgateManual.Edge, ResgateManual.CorpoConferir(null, codigo, _orderIdInicial)); }
        catch { st = -1; }
        finally { Ocupar(false); }
        var c = ResgateManual.LerConferencia(st, corpo);
        Diag(ResgateManual.Diag("conferir", codigo, _orderIdInicial is null ? "-" : "pedido", st, c.Ok ? c.Codigo?.Motivo ?? "sem_codigo" : c.Motivo, null));
        if (!c.Ok)
        {
            Mostrar(ResgateManual.LinhaDaRecusa(c.Motivo, null, _numeroInicial, c.Frase), "erro");
            return;
        }
        _conferencia = c;
        PintarChips(c.Candidatos);
        if (!c.Ligado)
        {
            Mostrar(ResgateManual.TextoDesligado, "erro");
            return;
        }
        if (c.Codigo is null)
        {
            // a tela abriu pelo pedido e ainda não tem código: mostra os pedidos para a pessoa ver onde vai
            PintarDestinos(c);
            Mostrar(c.Candidatos.Count > 0 ? "Toque num código do cliente ou digite outro." : ResgateManual.TextoSemCodigo, "fraco");
            return;
        }
        if (!c.Codigo.Ok)
        {
            _codigo = null;
            Mostrar(ResgateManual.LinhaDaRecusa(c.Codigo.Motivo, c.Codigo, _numeroInicial, c.Frase), "erro");
            _blocoDestino.Visibility = Visibility.Collapsed;
            _blocoSabor.Visibility = Visibility.Collapsed;
            _sabores.Children.Clear();
            _escolhidos.Clear();
            _chkAvisar.Visibility = Visibility.Collapsed;
            Habilitar();
            return;
        }
        _codigo = c.Codigo;
        _txtCodigo.Text = c.Codigo.Codigo;
        Mostrar(ResgateManual.LinhaDoPremio(c.Codigo), "ok");
        PintarDestinos(c);
        PintarSabores(c.Codigo);
        Habilitar();
    }

    private void PintarChips(IReadOnlyList<string> candidatos)
    {
        _chips.Children.Clear();
        foreach (var cand in candidatos)
        {
            var b = new Button
            {
                Content = cand, Style = (Style)Application.Current.Resources["BotaoBase"], MinHeight = 44, FontSize = 15,
                Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 6), Tag = cand,
            };
            b.Click += (_, _) => { _txtCodigo.Text = cand; _ = ConferirAsync(); };
            _chips.Children.Add(b);
        }
    }

    private void PintarDestinos(ConferenciaManual c)
    {
        _destinos.Children.Clear();
        var primeiro = true;
        foreach (var p in c.Pedidos)
        {
            var rb = new RadioButton
            {
                Content = ResgateManual.LinhaDoPedido(p), GroupName = "destino", Tag = p, FontSize = 16, MinHeight = 52,
                Style = (Style)Application.Current.Resources["BotaoOpcao"],
                IsEnabled = p.PremioCodigo is null,
                Opacity = p.PremioCodigo is null ? 1 : 0.55,
                IsChecked = primeiro && p.PremioCodigo is null && (p.Sugerido || c.Pedidos.Count == 1),
            };
            if (rb.IsChecked == true) primeiro = false;
            rb.Checked += (_, _) => Habilitar();
            _destinos.Children.Add(rb);
        }
        var balcao = new RadioButton
        {
            Content = ResgateManual.TextoBalcao, GroupName = "destino", Tag = null, FontSize = 16, MinHeight = 52,
            Style = (Style)Application.Current.Resources["BotaoOpcao"],
            IsChecked = c.Pedidos.Count == 0 || c.Pedidos.All(x => x.PremioCodigo is not null),
        };
        balcao.Checked += (_, _) => Habilitar();
        _destinos.Children.Add(balcao);
        if (c.Pedidos.Count == 0)
        {
            var aviso = Texto(ResgateManual.TextoSemPedido, 13, "TextoFraco");
            aviso.Margin = new Thickness(0, 2, 0, 0);
            _destinos.Children.Add(aviso);
        }
        _blocoDestino.Visibility = Visibility.Visible;
        AjustarAvisar();
    }

    private void PintarSabores(CodigoConferido cod)
    {
        _sabores.Children.Clear();
        _escolhidos = ResgateManual.PadraoMarcado(cod).ToList();
        if (cod.Escolhas <= 0 || cod.Opcoes.Count == 0)
        {
            _blocoSabor.Visibility = Visibility.Collapsed;
            return;
        }
        foreach (var o in cod.Opcoes)
        {
            // um Button (o estilo BotaoBase e de Button): o "marcado" e pintado a mao em PintarChipsDeSabor
            var chip = new Button
            {
                Content = o, Tag = o, MinHeight = 48, FontSize = 15, Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 8), Style = (Style)Application.Current.Resources["BotaoBase"],
            };
            chip.Click += (_, _) => { _escolhidos = ResgateManual.Tocar(cod, _escolhidos, o).ToList(); PintarChipsDeSabor(); Habilitar(); };
            _sabores.Children.Add(chip);
        }
        _txtApoioSabor.Text = ResgateManual.LinhaDeApoioDosSabores(cod);
        _blocoSabor.Visibility = Visibility.Visible;
        PintarChipsDeSabor();
    }

    private void PintarChipsDeSabor()
    {
        foreach (var chip in _sabores.Children.OfType<Button>())
        {
            var n = _escolhidos.Count(x => x == (string)chip.Tag);
            chip.Content = n > 1 ? $"{chip.Tag} x{n}" : (string)chip.Tag;
            chip.Background = n > 0 ? R("RosaDegrade") : R("PainelAlto");
            chip.FontWeight = n > 0 ? FontWeights.Bold : FontWeights.Normal;
            chip.BorderBrush = n > 0 ? R("Marca") : R("Borda");
        }
    }

    /// <summary>O pedido escolhido (nulo = balcão); nulo com <c>escolheu</c> falso quando nada está marcado.</summary>
    private (bool Escolheu, PedidoAberto? Pedido) Destino()
    {
        var rb = _destinos.Children.OfType<RadioButton>().FirstOrDefault(x => x.IsChecked == true);
        return rb is null ? (false, null) : (true, rb.Tag as PedidoAberto);
    }

    private void AjustarAvisar()
    {
        var (escolheu, pedido) = Destino();
        _chkAvisar.Visibility = escolheu && pedido is not null && _chatAtivo ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Habilitar()
    {
        AjustarAvisar();
        var (escolheu, _) = Destino();
        _btnResgatar.IsEnabled = !_ocupado && _bonusId is null && _codigo is { Ok: true } && escolheu
                                 && ResgateManual.SaboresCompletos(_codigo, _escolhidos);
    }

    private void LimparResultado()
    {
        _codigo = null;
        _blocoSabor.Visibility = Visibility.Collapsed;
        _sabores.Children.Clear();
        _escolhidos.Clear();
        if (_conferencia is null || _conferencia.Pedidos.Count == 0) _blocoDestino.Visibility = Visibility.Collapsed;
        Mostrar("", "fraco");
        Habilitar();
    }

    // ── [RESGATAR] ───────────────────────────────────────────────────────────

    /// <summary>Confirma em uma linha e resgata. A resposta é executada pelo caminho do chat.</summary>
    public async Task ResgatarAsync()
    {
        if (_ocupado || _bonusId is not null || _codigo is not { Ok: true } cod) return;
        var (escolheu, pedido) = Destino();
        if (!escolheu || !ResgateManual.SaboresCompletos(cod, _escolhidos)) return;
        if (!_confirmar(ResgateManual.PerguntaDeConfirmacao(cod, _escolhidos, pedido))) return;

        Ocupar(true);
        Mostrar(ResgateManual.TextoResgatando, "fraco");
        var avisar = pedido is not null && _chkAvisar.Visibility == Visibility.Visible && _chkAvisar.IsChecked == true;
        var corpoPedido = ResgateManual.CorpoResgatar(null, _operador, cod.Codigo, pedido is null, pedido?.IfoodOrderId, _escolhidos, avisar);
        int st = -1; string? corpo = null;
        try { (st, corpo) = await _servidor(ResgateManual.Edge, corpoPedido); }
        catch { st = -1; }
        var recusa = ResgateManual.LerRecusa(st, corpo);
        if (recusa is { } rec)
        {
            Ocupar(false);
            Diag(ResgateManual.Diag("resgatar", cod.Codigo, pedido is null ? "balcao" : "ifood", st, rec.Motivo, null));
            Mostrar(ResgateManual.LinhaDaRecusa(rec.Motivo, rec.Codigo ?? cod, pedido?.Numero, rec.Frase), "erro");
            // o código morreu neste pedido (já tem prêmio, já saiu): a pessoa escolhe outro destino
            if (rec.Motivo is "pedido_ja_tem" or "pedido_saiu" or "pedido_cancelado") Habilitar();
            return;
        }
        (RespostaConversa r, bool comandaSaiu, bool temTexto) = (null!, false, false);
        try { (r, comandaSaiu, temTexto) = await _executar(st, corpo); }
        catch { r = null!; }
        Ocupar(false);
        if (r is null || r.Status != StatusConversa.Ok || r.Bonus is null)
        {
            Diag(ResgateManual.Diag("resgatar", cod.Codigo, pedido is null ? "balcao" : "ifood", st, "resposta_ilegivel", null));
            Mostrar(ResgateManual.TextoSemRede, "erro");
            return;
        }
        var (frase, desfazerAte, _) = ResgateManual.LerExtrasDoResgate(corpo);
        _bonusId = r.Bonus.Id;
        _resgatadoEm = DateTime.Now;
        _orderIdResgatado = r.Pedido?.IfoodOrderId ?? pedido?.IfoodOrderId;
        _numeroResgatado = r.Pedido?.Numero ?? pedido?.Numero;
        _textoParaColar = temTexto ? r.Saidas.FirstOrDefault(s => s.Como == ConversaRaspadinha.ComoOperador)?.Texto : null;
        Diag(ResgateManual.Diag("resgatar", cod.Codigo, pedido is null ? "balcao" : "ifood", st, r.Acao, _bonusId));
        Mostrar(ResgateManual.TextoResgatado(r.ImprimirAqui, comandaSaiu, temTexto), comandaSaiu || !r.ImprimirAqui ? "ok" : "erro");
        _btnResgatar.Visibility = Visibility.Collapsed;
        _btnVoltar.Content = "Fechar";
        _btnDesfazer.Visibility = Visibility.Visible;
        _btnColar.Visibility = temTexto && _orderIdResgatado is not null ? Visibility.Visible : Visibility.Collapsed;
        _txtCodigo.IsReadOnly = true;
        _btnConferir.IsEnabled = false;
        foreach (var rb in _destinos.Children.OfType<RadioButton>()) rb.IsEnabled = false;
        foreach (var chip in _sabores.Children.OfType<Button>()) chip.IsEnabled = false;
        _chkAvisar.IsEnabled = false;
        _ = frase; _ = desfazerAte;
    }

    // ── [DESFAZER] ───────────────────────────────────────────────────────────

    /// <summary>Pede o código do autenticador do dono e desfaz na mesma transação do servidor.</summary>
    public async Task DesfazerAsync()
    {
        if (_ocupado || _bonusId is not { } bonus) return;
        if (ResgateManual.MotivoParaNaoDesfazer(_resgatadoEm, DateTime.Now, null) is { } nao)
        {
            Mostrar(nao, "erro");
            return;
        }
        var codigo = _pedirCodigo(null);
        if (codigo is null) return;
        Ocupar(true);
        Mostrar("Desfazendo...", "fraco");
        int st = -1; string? corpo = null;
        try { (st, corpo) = await _servidor(ResgateManual.Edge, ResgateManual.CorpoDesfazer(null, _terminalUuid, _operador, bonus, "desfeito no caixa", codigo)); }
        catch { st = -1; }
        Ocupar(false);
        var d = ResgateManual.LerDesfazer(st, corpo);
        Diag(ResgateManual.Diag("desfazer", d.Codigo, "-", st, d.Ok ? "desfeito" : d.Motivo, bonus));
        Mostrar(ResgateManual.LinhaDoDesfazer(d), d.Ok ? "ok" : "erro");
        if (!d.Ok) return;
        _btnDesfazer.Visibility = Visibility.Collapsed;
        _btnColar.Visibility = Visibility.Collapsed;
        Desfeito?.Invoke(bonus);
    }

    /// <summary>O resgate desta janela foi desfeito (a tela de venda tira o aviso, se tiver).</summary>
    public event Action<string>? Desfeito;

    private async Task ColarAsync()
    {
        if (_textoParaColar is not { Length: > 0 } texto || _orderIdResgatado is null) return;
        PediuChat?.Invoke();
        try { await ServicoConversaChat.AbrirEColarAsync(_orderIdResgatado, _numeroResgatado, texto); } catch { }
        _janela.Close();
    }

    /// <summary>"Abrir e colar": a tela de venda traz a camada do chat para a frente.</summary>
    public event Action? PediuChat;

    // ── miudezas ─────────────────────────────────────────────────────────────

    private void Ocupar(bool sim)
    {
        _ocupado = sim;
        _btnConferir.IsEnabled = !sim && _bonusId is null;
        _txtCodigo.IsReadOnly = sim || _bonusId is not null;
        _btnVoltar.IsEnabled = !sim;
        _btnDesfazer.IsEnabled = !sim;
        Habilitar();
    }

    private void Mostrar(string texto, string tom)
    {
        _txtLinha.Text = ResgateManual.Limpa(texto);
        _txtLinha.Foreground = tom switch { "ok" => R("Ok"), "erro" => R("Erro"), _ => R("TextoFraco") };
    }

    private static void Diag(string linha)
    {
        try { HospedeWebView2.Anotar("chat-conversa.txt", linha, TimeSpan.Zero); } catch { }
    }

    private static double AlturaDaTela(Window dono)
    {
        var area = SystemParameters.WorkArea.Height;
        return dono.ActualHeight > 0 ? Math.Min(area, dono.ActualHeight) : area;
    }

    private static TextBlock Texto(string texto, double tamanho, string cor) => new()
    {
        Text = texto, FontSize = tamanho, Foreground = R(cor), TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock Rotulo(string texto) => new()
    {
        Text = texto, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = R("Marca"), Margin = new Thickness(0, 0, 0, 6),
    };

    private static Button Botao(string texto, bool destaque) => new()
    {
        Content = texto,
        Style = (Style)Application.Current.Resources[destaque ? "BotaoPrincipal" : "BotaoBase"],
        MinHeight = 52, FontSize = 17,
        Background = destaque ? R("RosaDegrade") : R("PainelAlto"),
    };
}
