using Pdv.Nucleo;

namespace Pdv;

/// <summary>
/// O gêmeo do <see cref="ServicoChat"/> para o WHATSAPP DA LOJA (11/09/2026, pedido
/// do dono: "o WhatsApp dentro do caixa, com aviso quando chegar mensagem, para o PC
/// rodar só o PDV"). Um por processo: guarda quantas mensagens não lidas há e avisa
/// quando SOBE. Quem alimenta é a tela do WhatsApp (o WebView2 lendo o título da
/// página); quem escuta é a tela de venda (selo no botão + aviso + toque).
///
/// Serviço separado de propósito: dois contadores, dois selos, dois sons. Somar os
/// dois num só faria uma mensagem do iFood "virar" mensagem do WhatsApp na tela.
///
/// Desde 11/09 (tarde) também carrega a VIGIA DA SESSÃO (<see cref="SessaoWhatsApp"/>):
/// a mesma tela reporta o que a página mostra (conectado, QR, carregando...) e este
/// serviço avisa a venda quando o QR aparece numa máquina que já esteve conectada.
/// São duas máquinas de estado separadas: <see cref="Recomecar"/> zera só a contagem.
///
/// Tudo aqui nasce e morre na thread da tela (WebMessageReceived e o DispatcherTimer
/// da tela do WhatsApp): sem timer novo, sem thread nova. É o que permite à venda
/// pintar direto nos handlers.
/// </summary>
public static class ServicoWhatsApp
{
    private static readonly object _trava = new();
    private static readonly ChatAviso _aviso = new();
    private static int _total;

    /// <summary>Não lidas conhecidas agora (0 antes da primeira leitura).</summary>
    public static int NaoLidas { get { lock (_trava) return _total; } }

    /// <summary>O total mudou (para o SELO). Disparado na thread do WebView2 (UI).</summary>
    public static event Action<int>? Mudou;

    /// <summary>SUBIU: chegou mensagem nova (para aviso + som). Só na subida.</summary>
    public static event Action<int>? MensagemNova;

    /// <summary>A tela reporta o TÍTULO da aba; a leitura do número é pura (Núcleo).</summary>
    public static void ReportarTitulo(string? titulo) => Reportar(WhatsAppContagem.LerTitulo(titulo));

    public static void Reportar(int total)
    {
        ChatAviso.Resultado r;
        lock (_trava)
        {
            r = _aviso.Observar(total);
            _total = r.Total;
        }
        Mudou?.Invoke(r.Total);
        if (r.Avisar) MensagemNova?.Invoke(r.Total);
    }

    /// <summary>Recarregou a página: a próxima leitura vira linha de base de novo. Só a CONTAGEM.</summary>
    public static void Recomecar()
    {
        lock (_trava) { _aviso.Zerar(); _total = 0; }
        Mudou?.Invoke(0);
    }

    // ── o som: o da própria página vale; o nosso é a reserva ─────────────────

    private static DateTime _paginaTocouEm = DateTime.MinValue;

    /// <summary>A página do WhatsApp começou a emitir áudio (IsDocumentPlayingAudio).</summary>
    public static void PaginaTocou() { lock (_trava) _paginaTocouEm = DateTime.UtcNow; }

    /// <summary>
    /// O dono quer o toque ORIGINAL do WhatsApp, e ele existe: é o que a página toca
    /// quando chega mensagem. Então a regra é: chegou mensagem, espera um instante; se
    /// a página tocou por conta própria (pouco antes ou logo depois), o caixa fica
    /// quieto; se ela ficou calada (som desligado nas configurações do WhatsApp Web,
    /// página em estado que não toca), o caixa toca o toque de reserva. Nunca os dois,
    /// nunca nenhum. A decisão é pura (<see cref="SomWhatsApp.PaginaCobre"/>).
    /// </summary>
    public static void TocarSeAPaginaCalar(Action tocarReserva, TimeSpan? espera = null)
    {
        var mensagemEm = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(espera ?? SomWhatsApp.EsperaPelaPagina);
                DateTime tocou; lock (_trava) tocou = _paginaTocouEm;
                if (!SomWhatsApp.PaginaCobre(tocou, mensagemEm)) tocarReserva();
            }
            catch { /* som de reserva nunca derruba nada */ }
        });
    }

    // ── a vigia da sessão ─────────────────────────────────────────────────────

    private static SessaoWhatsApp _sessao = new(jaConectou: false);
    private static Func<string, string?>? _lerConfig;
    private static Action<string, string>? _gravarConfig;

    /// <summary>O estado atual da página, para quem entra na venda depois do fato.</summary>
    public static EstadoWa Sessao { get { lock (_trava) return _sessao.Estado; } }
    public static DateTime? CaidoDesde { get { lock (_trava) return _sessao.CaidoDesde; } }
    public static bool Armada { get { lock (_trava) return _sessao.Armada; } }

    /// <summary>O estado mudou (para o selo e o cabeçalho da aba).</summary>
    public static event Action<EstadoWa>? SessaoMudou;
    /// <summary>Hora de avisar (primeira vez ou repetição): selo vermelho, aviso e som.</summary>
    public static event Action<EstadoWa, DateTime?>? Caiu;
    /// <summary>A sessão voltou: o aviso some.</summary>
    public static event Action? Voltou;

    /// <summary>
    /// Liga a memória "já conectou" ao banco do caixa. Injetado (e não Banco.Abrir()
    /// aqui dentro) para a bateria de testes exercitar o serviço sem tocar no pdv.db
    /// de quem compila. Chamado uma vez no MainWindow.
    /// </summary>
    public static void Ligar(Func<string, string?> lerConfig, Action<string, string> gravarConfig)
    {
        _lerConfig = lerConfig; _gravarConfig = gravarConfig;
        bool jaConectou; DateTime? caidoDesde = null;
        try
        {
            jaConectou = !string.IsNullOrWhiteSpace(lerConfig(SessaoWhatsApp.ChaveConectouEm));
            // a queda gravada sobrevive ao reinício: é o que faz "7 dias caído" existir
            // numa loja que desliga o PC toda noite
            if (DateTime.TryParse(lerConfig(SessaoWhatsApp.ChaveCaidoDesde), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var d)) caidoDesde = d;
        }
        catch { jaConectou = false; }
        lock (_trava) _sessao = new SessaoWhatsApp(jaConectou, caidoDesde);
    }

    /// <summary>A tela reporta a palavra crua que o script leu do DOM.</summary>
    public static void ReportarSessao(string? texto) => Aplicar(s => s.Observar(SessaoWhatsApp.Ler(texto), DateTime.Now));

    /// <summary>O relógio da tela bateu (5 s): silêncio e repetição são decididos aqui.</summary>
    public static void Bater() => Aplicar(s => s.Bater(DateTime.Now));

    /// <summary>O WebView2 não abriu nesta máquina.</summary>
    public static void SemComponente() => Aplicar(s => s.SemComponente(DateTime.Now));

    /// <summary>A tela vai recarregar a página.</summary>
    public static void Recarregando() { lock (_trava) _sessao.Recarregando(DateTime.Now); }

    /// <summary>"Depois": cala o aviso por 2 h. O selo continua.</summary>
    public static void Adiar() { lock (_trava) _sessao.Adiar(DateTime.Now); }

    private static void Aplicar(Func<SessaoWhatsApp, SessaoWhatsApp.Resultado> passo)
    {
        SessaoWhatsApp.Resultado r; EstadoWa antes; DateTime? desdeAntes, desde;
        lock (_trava)
        {
            antes = _sessao.Estado; desdeAntes = _sessao.CaidoDesde;
            r = passo(_sessao);
            desde = _sessao.CaidoDesde;
            if (r.ZerarContagem) { _aviso.Zerar(); _total = 0; }
        }
        // A memória no banco, FORA do lock: a 1ª conexão, a queda (para os 7 dias
        // sobreviverem ao reinício) e o desarme. Gravar "" é apagar.
        try
        {
            if (r.Armou) _gravarConfig?.Invoke(SessaoWhatsApp.ChaveConectouEm, DateTime.Now.ToString("o"));
            if (r.Desarmou)
            {
                _gravarConfig?.Invoke(SessaoWhatsApp.ChaveConectouEm, "");
                _gravarConfig?.Invoke(SessaoWhatsApp.ChaveCaidoDesde, "");
            }
            else if (desde != desdeAntes) _gravarConfig?.Invoke(SessaoWhatsApp.ChaveCaidoDesde, desde?.ToString("o") ?? "");
        }
        catch { /* a memória é conforto, não é a regra */ }
        // eventos FORA do lock (a venda lê NaoLidas/Sessao de dentro deles)
        if (r.ZerarContagem) Mudou?.Invoke(0);
        if (r.Estado != antes || r.Desarmou) SessaoMudou?.Invoke(r.Estado);
        if (r.Voltou) Voltou?.Invoke();
        if (r.Avisar) Caiu?.Invoke(r.Estado, desde);
    }

    /// <summary>Só para a bateria: volta ao estado de processo recém-aberto.</summary>
    public static void RecomecarSessaoParaTeste(bool jaConectou = false)
    {
        lock (_trava) { _sessao = new SessaoWhatsApp(jaConectou); _paginaTocouEm = DateTime.MinValue; }
    }
}
