using Pdv.Nucleo;

namespace Pdv;

/// <summary>
/// Serviço VIVO do chat, no mesmo espírito do "sino" Realtime do KDS: um por
/// processo, guarda quantas mensagens não lidas há e avisa quando SOBE. Quem
/// alimenta é a tela do chat (o observador do DOM dentro do WebView2); quem
/// escuta é a tela de venda (selo no botão + toast/som).
///
/// A DECISÃO é pura e testada no Núcleo (ChatContagem/ChatAviso). Aqui só mora o
/// estado vivo e os eventos — para o WebView2 do chat, que fica vivo no
/// MainWindow, acender o selo na venda mesmo com o operador vendendo.
/// </summary>
public static class ServicoChat
{
    private static readonly object _trava = new();
    private static readonly ChatAviso _aviso = new();
    private static int _total;

    /// <summary>Não lidas conhecidas agora (0 antes da primeira leitura).</summary>
    public static int NaoLidas { get { lock (_trava) return _total; } }

    /// <summary>O total mudou (para o SELO). Disparado na thread do WebView2 (UI).</summary>
    public static event Action<int>? Mudou;

    /// <summary>SUBIU: chegou mensagem nova (para toast + som). Só na subida.</summary>
    public static event Action<int>? MensagemNova;

    /// <summary>
    /// A tela reporta o TEXTO cru do DOM (aria-label/badge). A leitura do número
    /// é pura (Núcleo), então o que quebra a cada mudança do iFood é testável.
    /// </summary>
    public static void ReportarTexto(string? textoDom) => Reportar(ChatContagem.Ler(textoDom));

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

    /// <summary>Recarregou o chat: a próxima leitura vira linha de base de novo.</summary>
    public static void Recomecar()
    {
        lock (_trava) { _aviso.Zerar(); _total = 0; }
        Mudou?.Invoke(0);
    }
}
