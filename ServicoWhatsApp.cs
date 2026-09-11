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

    /// <summary>Recarregou a página: a próxima leitura vira linha de base de novo.</summary>
    public static void Recomecar()
    {
        lock (_trava) { _aviso.Zerar(); _total = 0; }
        Mudou?.Invoke(0);
    }
}
