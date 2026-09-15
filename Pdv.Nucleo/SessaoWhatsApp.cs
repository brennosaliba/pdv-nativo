namespace Pdv.Nucleo;

/// <summary>O que a página do WhatsApp Web está mostrando, lido do DOM pela tela.</summary>
public enum EstadoWa
{
    /// <summary>Nada reconhecido ainda (página em branco, layout novo).</summary>
    Desconhecido,
    /// <summary>"Carregando suas conversas", barra de progresso.</summary>
    Carregando,
    /// <summary>Lista de conversas na tela: a sessão está de pé.</summary>
    Conectado,
    /// <summary>A tela do QR: o celular desvinculou este caixa. É o único estado que AVISA.</summary>
    PedindoQr,
    /// <summary>"Telefone sem conexão": passageiro, a sessão continua. Não avisa.</summary>
    TelefoneSemConexao,
    /// <summary>Este PC está sem internet. A venda já mostra OFFLINE; aqui não grita.</summary>
    PcSemInternet,
    /// <summary>O WebView2 não abriu nesta máquina: não há QR para ler.</summary>
    SemComponente,
    /// <summary>A página parou de responder às leituras (renderer caiu, tela travada).</summary>
    SemLeitura,
}

/// <summary>
/// A VIGIA DA SESSÃO DO WHATSAPP (11/09/2026, pedido do dono: "uma trava de
/// notificação caso o QR code do WhatsApp Web desconecte do PDV; verificar de tempos
/// em tempos se está conectado, porque às vezes desloga sem motivo").
///
/// Puro e testável: a tela lê o DOM e entrega um <see cref="EstadoWa"/> a cada 5 s;
/// esta classe decide QUANDO avisar. As regras, uma a uma, com o motivo:
///  · Só avisa depois de ARMADA: o WhatsApp já esteve conectado neste caixa (duas
///    leituras seguidas de Conectado, ou a memória gravada em config). A loja que
///    nunca leu o QR não ganha selo vermelho nem som por uma função que não usa.
///  · Só a tela do QR avisa, e só depois de ficar 60 s seguidos na tela: uma
///    recarga passa pelo QR por um instante e "Telefone sem conexão" é o Wi-Fi do
///    celular piscando (em multi-device o Web segue funcionando por dias).
///  · Repete a cada <see cref="IntervaloAviso"/> enquanto continuar caído, porque
///    um aviso que apareceu às 09:00 e ninguém viu é o mesmo que nenhum. "Depois"
///    cala por 2 h. Passados 7 dias caído (contados desde a queda, que fica GRAVADA e
///    sobrevive ao reinício do PDV) a vigia DESARMA: alguém decidiu não usar mais o
///    WhatsApp neste caixa; selo e som somem, e só uma conexão nova rearma.
///  · Só CONECTADO cura a queda. Carregando, sem leitura, PC sem internet e telefone
///    sem conexão no meio da queda não mudam a hora em que ela começou: a página
///    recarregando e o Wi-Fi piscando passam por eles antes de mostrar o QR de novo.
///  · Voltou? O aviso some na hora. E a contagem de não lidas recomeça do zero: sem
///    isso o título voltava de "WhatsApp" para "(7) WhatsApp" e o caixa tocava "7
///    mensagens novas" para mensagens velhas.
/// A primeira leitura de QR numa máquina armada AVISA (depois da carência). É o
/// oposto da contagem, em que a primeira leitura é linha de base: por isso são duas
/// máquinas separadas, e Recomecar() da contagem não encosta nesta.
/// </summary>
public sealed class SessaoWhatsApp
{
    public static readonly TimeSpan CarenciaQr = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan IntervaloAviso = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Silencio = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan Adiamento = TimeSpan.FromHours(2);
    public static readonly TimeSpan DesisteDepoisDe = TimeSpan.FromDays(7);

    /// <summary>Chave de config: quando este caixa viu o WhatsApp conectado pela 1ª vez (ISO).</summary>
    public const string ChaveConectouEm = "whatsapp_conectou_em";
    /// <summary>Chave de config: desde quando a tela do QR está na tela (ISO). Sai ao reconectar.</summary>
    public const string ChaveCaidoDesde = "whatsapp_caido_desde";

    public EstadoWa Estado { get; private set; } = EstadoWa.Desconhecido;
    public bool Armada { get; private set; }
    /// <summary>Desde quando a tela do QR está na tela (null quando não está).</summary>
    public DateTime? CaidoDesde { get; private set; }
    public DateTime? UltimoAviso { get; private set; }
    public DateTime? UltimaLeitura { get; private set; }
    private DateTime? _caladoAte;
    private int _conectadoSeguidas;
    private bool _avisouSemComponente;

    /// <param name="jaConectou">A memória gravada: este caixa já esteve conectado.</param>
    /// <param name="caidoDesde">A queda gravada antes do reinício do PDV, se houver.</param>
    public SessaoWhatsApp(bool jaConectou, DateTime? caidoDesde = null)
    {
        Armada = jaConectou;
        CaidoDesde = jaConectou ? caidoDesde : null;
    }

    /// <summary>
    /// O que a tela deve fazer depois de cada leitura ou batida do relógio.
    /// <c>Armou</c>: grave a memória (aconteceu a 1ª conexão). <c>Desarmou</c>: apague-a
    /// (7 dias caído). <c>Avisar</c>: selo + aviso + som, agora. <c>Voltou</c>: apague o
    /// aviso. <c>ZerarContagem</c>: a contagem de não lidas recomeça (linha de base nova).
    /// </summary>
    public readonly record struct Resultado(EstadoWa Estado, bool Armou, bool Avisar, bool Voltou, bool ZerarContagem, bool Desarmou = false);

    /// <summary>Uma leitura do DOM chegou.</summary>
    public Resultado Observar(EstadoWa lido, DateTime agora)
    {
        UltimaLeitura = agora;
        var anterior = Estado;
        var armou = false; var voltou = false; var zerar = false;

        if (lido == EstadoWa.Conectado)
        {
            _conectadoSeguidas++;
            if (!Armada && _conectadoSeguidas >= 2) { Armada = true; armou = true; }
            if (CaidoDesde is not null || anterior is EstadoWa.SemLeitura or EstadoWa.SemComponente)
            {
                // voltou de uma queda: o aviso (se houve) some e a contagem recomeça do zero
                voltou = UltimoAviso is not null;
                zerar = true;
            }
            CaidoDesde = null; UltimoAviso = null; _caladoAte = null;
        }
        else
        {
            _conectadoSeguidas = 0;
            // Só a tela do QR abre uma queda, e só Conectado a fecha: Carregando,
            // Desconhecido, SemLeitura, PC e telefone sem conexão no meio dela não
            // "curam" nada (a página recarregando passa por eles antes do QR voltar).
            if (lido == EstadoWa.PedindoQr) CaidoDesde ??= agora;
        }
        Estado = lido;
        var (avisar, desarmou) = Decidir(agora);
        if (avisar) UltimoAviso = agora;
        return new Resultado(Estado, armou, avisar, voltou, zerar, desarmou);
    }

    /// <summary>
    /// O relógio bateu (a cada 5 s, na thread da tela). Sem leitura há mais de
    /// <see cref="Silencio"/> o estado vira SemLeitura; e é aqui que a repetição do
    /// aviso acontece, sem timer novo.
    /// </summary>
    public Resultado Bater(DateTime agora)
    {
        if (UltimaLeitura is { } u && agora - u > Silencio && Estado is not (EstadoWa.SemLeitura or EstadoWa.SemComponente))
        {
            Estado = EstadoWa.SemLeitura;
            return new Resultado(Estado, false, false, false, false);
        }
        var (avisar, desarmou) = Decidir(agora);
        if (avisar) UltimoAviso = agora;
        return new Resultado(Estado, false, avisar, false, false, desarmou);
    }

    /// <summary>O operador tocou em "Depois": cala por 2 h. O selo continua.</summary>
    public void Adiar(DateTime agora) => _caladoAte = agora + Adiamento;

    /// <summary>A tela vai recarregar: a leitura seguinte pode ser Carregando/QR por um instante.</summary>
    public void Recarregando(DateTime agora) { UltimaLeitura = agora; }

    /// <summary>O WebView2 não abriu (sem runtime): avisa UMA vez, sem som repetido.</summary>
    public Resultado SemComponente(DateTime agora)
    {
        UltimaLeitura = agora;
        Estado = EstadoWa.SemComponente;
        var avisar = Armada && !_avisouSemComponente;
        _avisouSemComponente = true;
        if (avisar) UltimoAviso = agora;
        return new Resultado(Estado, false, avisar, false, false);
    }

    /// <summary>Avisar agora? E a queda já passou dos 7 dias (desarma)?</summary>
    private (bool Avisar, bool Desarmou) Decidir(DateTime agora)
    {
        if (!Armada || CaidoDesde is not { } desde) return (false, false);
        if (agora - desde > DesisteDepoisDe)
        {
            // 7 dias na tela do QR: ninguém vai ler. Desarma de vez (selo e som somem);
            // uma conexão nova rearma.
            Armada = false; CaidoDesde = null; UltimoAviso = null; _caladoAte = null;
            return (false, true);
        }
        if (Estado != EstadoWa.PedindoQr) return (false, false);
        if (agora - desde < CarenciaQr) return (false, false);
        if (_caladoAte is { } c && agora < c) return (false, false);
        if (UltimoAviso is { } u && agora - u < IntervaloAviso) return (false, false);
        return (true, false);
    }

    // ── o que a página diz → EstadoWa ──────────────────────────────────────────

    /// <summary>
    /// Nunca lança: aceita a palavra crua ("qr"), a palavra JSON-codificada ("\"qr\""),
    /// "null", vazio e lixo (→ Desconhecido).
    /// </summary>
    public static EstadoWa Ler(string? texto)
    {
        var t = (texto ?? "").Trim().Trim('"').Trim().ToLowerInvariant();
        return t switch
        {
            "conectado" or "lista" or "ok" => EstadoWa.Conectado,
            "qr" or "login" => EstadoWa.PedindoQr,
            "carregando" or "loading" => EstadoWa.Carregando,
            "telefone" or "phone" => EstadoWa.TelefoneSemConexao,
            "pc" or "offline" => EstadoWa.PcSemInternet,
            "semcomponente" => EstadoWa.SemComponente,
            "semleitura" => EstadoWa.SemLeitura,
            _ => EstadoWa.Desconhecido,
        };
    }

    // ── textos de tela (curtos, sem travessão; a bateria vigia) ────────────────

    /// <summary>Selo vermelho no botão WhatsApp da venda.</summary>
    public const string Selo = "QR";

    /// <summary>Linha do aviso na venda. Uma frase, o que fazer na segunda.</summary>
    public static (string Titulo, string Acao) Aviso(EstadoWa e) => e switch
    {
        EstadoWa.SemComponente => ("O WhatsApp não abre neste PC", "falta um componente da Microsoft: chame o suporte"),
        _ => ("WhatsApp pediu o QR de novo", "toque para ler com o celular da loja"),
    };

    /// <summary>Texto do cabeçalho da aba do WhatsApp, por estado.</summary>
    public static string Cabecalho(EstadoWa e) => e switch
    {
        EstadoWa.Conectado => "conectado",
        EstadoWa.PedindoQr => "desconectado: leia o QR com o celular da loja e deixe marcado Manter conectado",
        EstadoWa.Carregando => "carregando…",
        EstadoWa.TelefoneSemConexao => "celular da loja sem internet: as mensagens chegam quando ele voltar",
        EstadoWa.PcSemInternet => "este PC está sem internet",
        EstadoWa.SemComponente => "falta um componente da Microsoft neste PC: chame o suporte",
        EstadoWa.SemLeitura => "a página parou de responder: toque em Recarregar",
        _ => "abrindo…",
    };
}
