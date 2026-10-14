namespace Pdv.Nucleo;

/// <summary>O que o chat do iFood (o Gestor de Pedidos dentro do caixa) está fazendo agora.</summary>
public enum EstadoChatIfood
{
    /// <summary>O WebView2 está nascendo ou sendo recriado: ainda não dá para dizer.</summary>
    Abrindo,
    /// <summary>A página fala com o caixa e o Gestor está logado.</summary>
    Ok,
    /// <summary>A tela de login do Gestor está aberta: só uma pessoa resolve.</summary>
    Login,
    /// <summary>A página parou de falar (renderer travado, rede caída, tela branca) ou não abriu.</summary>
    Parado,
}

/// <summary>
/// A VIGIA DO CHAT DO iFOOD NO CAIXA (14/10/2026, PDV 1.0.33).
///
/// Em 13/10 às 20:41 o Gestor da Savassi pediu login e o chat ficou parado até 14/10 16:10. O
/// caixa continuou dizendo "logado" para o ERP: o login só era lido dentro do diagnóstico do
/// chat, que sem o chat carregado estourava o prazo e nunca respondia. Ninguém na loja soube.
///
/// Puro e testável, no molde de <see cref="SessaoWhatsApp"/>: a tela entrega a leitura a cada
/// 15 s e esta classe decide o que fazer. As regras, com o motivo:
///  · A página fala a cada 5 s (o contador de não lidas). Muda há <see cref="Silencio"/>, está
///    PARADA. O Gestor pedindo login é LOGIN. Abrindo há mais de <see cref="AberturaLonga"/> sem
///    ficar pronto também é PARADA (controle que não nasceu).
///  · Parada se cura sozinha: uma recarga aos <see cref="RecarregarAos"/>, e um controle novo
///    aos <see cref="RecriarAos"/>, no máximo <see cref="TetoRecriarPorHora"/> por hora e com
///    recuo de 3, 6 e 12 min entre um e outro. Nada disso com o operador na tela do chat (pode
///    estar digitando no Gestor) e nada disso no LOGIN (recarregar não faz ninguém logar).
///  · O caixa avisa (aviso vermelho fixo e som com a comanda vazia) quando o chat está fora há
///    <see cref="Carencia"/> (combinado com o dono em 14/10: 2 min no caixa, 5 min no WhatsApp
///    dele, que é a vigia do ERP). Repete a cada <see cref="IntervaloAviso"/>; "Depois" cala por
///    <see cref="Adiamento"/>.
///  · Só OK fecha a queda. Abrindo no meio da queda (o controle sendo recriado) não cura nada.
/// </summary>
public sealed class VigiaChatIfood
{
    public static readonly TimeSpan Silencio = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan AberturaLonga = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan RecarregarAos = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan RecriarAos = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan Carencia = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan IntervaloAviso = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Adiamento = TimeSpan.FromMinutes(30);
    public const int TetoRecriarPorHora = 4;
    private static readonly TimeSpan[] Recuo = { TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(12) };

    /// <summary>O que a tela viu agora.</summary>
    /// <param name="Pronto">O WebView2 nasceu e a página foi pedida.</param>
    /// <param name="PedeLogin">A última fala da página disse que a tela de login do Gestor está aberta.</param>
    /// <param name="PaginaFalouEm">A última vez que a página mandou qualquer mensagem ao caixa.</param>
    /// <param name="OperadorNaTela">A camada do chat está na frente (alguém pode estar digitando).</param>
    public readonly record struct Leitura(bool Pronto, bool PedeLogin, DateTime? PaginaFalouEm, bool OperadorNaTela);

    /// <summary>
    /// <c>Avisar</c>: aviso vermelho + som, agora. <c>Voltou</c>: apague o aviso. <c>Recarregar</c>:
    /// recarregue a página uma vez. <c>Recriar</c>: troque o controle por um novo.
    /// </summary>
    public readonly record struct Resultado(EstadoChatIfood Estado, bool Avisar, bool Voltou, bool Recarregar, bool Recriar);

    public EstadoChatIfood Estado { get; private set; } = EstadoChatIfood.Abrindo;
    /// <summary>Desde quando o chat está fora (null = ok ou ainda abrindo pela primeira vez).</summary>
    public DateTime? ForaDesde { get; private set; }
    /// <summary>O motivo da queda aberta (Login ou Parado), para o texto do aviso.</summary>
    public EstadoChatIfood? Motivo { get; private set; }
    public DateTime? UltimoAviso { get; private set; }

    private DateTime _abrindoDesde;
    private DateTime? _prontoEm;
    private DateTime? _caladoAte;
    private bool _recarregouNestaQueda;
    private DateTime? _ultimaRecriacao;
    private int _recriacoesNestaQueda;
    private readonly List<DateTime> _recriacoes = new();

    public VigiaChatIfood(DateTime agora) => _abrindoDesde = agora;

    /// <summary>A última fala da página que vale para o controle de agora (a de um controle anterior não conta).</summary>
    private DateTime? FalaValida(Leitura l)
        => l.PaginaFalouEm is { } f && (_prontoEm is not { } p || f >= p) ? f : null;

    /// <summary>O estado da leitura, sem efeito nenhum.</summary>
    public EstadoChatIfood Ler(Leitura l, DateTime agora)
    {
        if (!l.Pronto) return agora - _abrindoDesde >= AberturaLonga ? EstadoChatIfood.Parado : EstadoChatIfood.Abrindo;
        var fala = FalaValida(l);
        if (agora - (fala ?? _prontoEm ?? agora) >= Silencio) return EstadoChatIfood.Parado;
        if (fala is null) return EstadoChatIfood.Abrindo;
        return l.PedeLogin ? EstadoChatIfood.Login : EstadoChatIfood.Ok;
    }

    /// <summary>O relógio da tela bateu (15 s).</summary>
    public Resultado Bater(Leitura l, DateTime agora)
    {
        // o que conta para o silêncio é o controle de agora: pronto desde quando, abrindo desde quando
        if (!l.Pronto) { if (_prontoEm is not null) { _prontoEm = null; _abrindoDesde = agora; } }
        else _prontoEm ??= agora;

        var estado = Ler(l, agora);
        Estado = estado;
        // há quanto tempo a página deste controle não fala (zero enquanto o controle abre)
        var mudaDesde = l.Pronto ? FalaValida(l) ?? _prontoEm ?? agora : _abrindoDesde;
        var mudaHa = l.Pronto ? agora - mudaDesde : TimeSpan.Zero;

        if (estado == EstadoChatIfood.Ok && mudaHa < RecarregarAos)
        {
            var voltou = ForaDesde is not null && UltimoAviso is not null;
            ForaDesde = null; Motivo = null; UltimoAviso = null; _caladoAte = null;
            _recarregouNestaQueda = false; _recriacoesNestaQueda = 0;
            return new Resultado(estado, false, voltou, false, false);
        }

        // a queda: Parado conta desde a última fala (ou desde que abriu); Login, desde agora
        if (estado == EstadoChatIfood.Parado) { ForaDesde ??= mudaDesde; Motivo = estado; }
        else if (estado == EstadoChatIfood.Login) { ForaDesde ??= agora; Motivo = estado; }

        // a cura sozinha: só com a página que já abriu (a que nunca abre tem o vigia da
        // inicialização), nunca no login e nunca com o operador na tela do chat. A recarga vem
        // aos 90 s de silêncio, antes de a página contar como parada (2 min)
        var recarregar = false; var recriar = false;
        if ((estado is EstadoChatIfood.Parado or EstadoChatIfood.Ok) && l.Pronto && !l.OperadorNaTela && mudaHa >= RecarregarAos)
        {
            if (!_recarregouNestaQueda)
            {
                _recarregouNestaQueda = true;
                recarregar = true;
            }
            else if (mudaHa >= RecriarAos && PodeRecriar(agora))
            {
                recriar = true;
                _ultimaRecriacao = agora;
                _recriacoesNestaQueda++;
                _recriacoes.Add(agora);
                _recarregouNestaQueda = false;
                _prontoEm = null; _abrindoDesde = agora;
            }
        }

        var avisar = ForaDesde is { } desde && agora - desde >= Carencia
                     && (_caladoAte is not { } c || agora >= c)
                     && (UltimoAviso is not { } u || agora - u >= IntervaloAviso);
        if (avisar) UltimoAviso = agora;
        return new Resultado(estado, avisar, false, recarregar, recriar);
    }

    /// <summary>Teto por hora e recuo de 3, 6 e 12 min entre um controle novo e o próximo.</summary>
    private bool PodeRecriar(DateTime agora)
    {
        _recriacoes.RemoveAll(t => agora - t > TimeSpan.FromHours(1));
        if (_recriacoes.Count >= TetoRecriarPorHora) return false;
        if (_ultimaRecriacao is { } u && _recriacoesNestaQueda > 0
            && agora - u < Recuo[Math.Min(_recriacoesNestaQueda - 1, Recuo.Length - 1)]) return false;
        return true;
    }

    /// <summary>Quantos controles novos a vigia pediu na última hora (para o sinal do ERP).</summary>
    public int RecriacoesNaHora(DateTime agora) => _recriacoes.Count(t => agora - t <= TimeSpan.FromHours(1));

    /// <summary>O operador tocou em "Depois": cala o aviso por 30 min.</summary>
    public void Adiar(DateTime agora) => _caladoAte = agora + Adiamento;

    /// <summary>O operador tocou em "Reabrir": a contagem do novo controle recomeça agora.</summary>
    public void Reabriu(DateTime agora)
    {
        _prontoEm = null; _abrindoDesde = agora; _recarregouNestaQueda = false;
    }

    /// <summary>O estado da vigia para o sinal do ERP (só a fase, nunca texto).</summary>
    public string Fase => ForaDesde is null ? (Estado == EstadoChatIfood.Ok ? "ok" : "abrindo")
        : Motivo == EstadoChatIfood.Login ? "login"
        : UltimoAviso is null ? "parado" : "aviso";

    // ── o gestor para o sinal do ERP (contrato: ausente, login, logado) ──────────

    /// <summary>
    /// "logado" só com a página falando: antes o caixa dizia "logado" com a página morta, porque
    /// o login só era lido no diagnóstico que estourava o prazo. Página muda há 2 min é "ausente".
    /// </summary>
    public static string GestorParaOSinal(bool pronto, bool pedeLogin, DateTime? paginaFalouEm, DateTime agora)
    {
        if (!pronto || paginaFalouEm is not { } f || agora - f >= Silencio) return "ausente";
        return pedeLogin ? "login" : "logado";
    }

    // ── textos de tela (curtos, sem travessão; a bateria vigia) ────────────────

    /// <summary>Linha do aviso na venda: o que aconteceu e, na segunda, o que fazer.</summary>
    public static (string Titulo, string Acao) Aviso(EstadoChatIfood? motivo) => motivo switch
    {
        EstadoChatIfood.Login => ("Chat do iFood: o Gestor pediu login", "toque aqui e entre com a conta da loja"),
        _ => ("Chat do iFood parado", "toque em Reabrir"),
    };
}
