using System.Net.Http;
using Pdv.Nucleo;

namespace Pdv;

/// <summary>O que a tela do chat oferece à recuperação: quem recebe cada quadro recuperado (no formato do WebSocket).</summary>
public sealed class PonteDaRecuperacao
{
    public required Action<string> Entregar { get; init; }
}

/// <summary>
/// A RECUPERAÇÃO DO CHAT PELO HISTÓRICO DO SENDBIRD, O SERVIÇO VIVO DO CAIXA (1.0.41, 16/10/2026, Savassi,
/// pedido 2029). O robô só via o que chegava pelo WebSocket enquanto o Gestor estava aberto; com o chat morto
/// de 12:32 a 13:22 o cliente mandou 3 códigos e nada foi capturado. Agora, quando o chat sobe e a cada 10 s,
/// o caixa busca sozinho o histórico recente das conversas de pedido (REST api-&lt;appid&gt;.sendbird.com/v3,
/// com a sessão que o próprio Gestor abriu) e entrega ao mesmo funil de hoje cada mensagem de cliente que
/// ainda não viu, com origem "rest". GET apenas: nada de marcar lida, clicar em conversa ou tocar o DOM.
///
/// O portão: a captura ligada no ERP (ServicoRaspadinhaChat.Ligado) OU o chat novo ativo (o sinal) OU o
/// arquivo fora do silêncio. Só Ligado() não serve: com o chat novo ativo ele é FALSO (LigadoNaLoja = config
/// E NÃO SinalAtivo), e a recuperação nunca rodaria na Savassi. Em cache por 30 s, como o Ligado.
///
/// Nunca lança: chat não derruba caixa. Tudo que é regra mora em Nucleo (ServicoDeRecuperacao, Varredura,
/// SessaoSendbird, RecuperacaoSendbird).
/// </summary>
public static class ServicoRecuperacaoChat
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private static ServicoDeRecuperacao? _servico;
    private static PonteDaRecuperacao? _ponte;
    private static int _iniciado;
    private static bool _portao;
    private static DateTime _portaoAte = DateTime.MinValue;
    private static readonly object TravaPortao = new();

    /// <summary>Liga com a ponte da tela. Idempotente: a ponte é trocada quando a tela é recriada; o serviço nasce uma vez.</summary>
    public static void Ligar(PonteDaRecuperacao ponte)
    {
        _ponte = ponte;
        if (Interlocked.Exchange(ref _iniciado, 1) == 1) return;
        try
        {
            _servico = new ServicoDeRecuperacao
            {
                Http = (pedido, ct) => Http.SendAsync(pedido, HttpCompletionOption.ResponseContentRead, ct),
                Entregar = q => { try { _ponte?.Entregar(q); } catch (Exception ex) { Diag("entregar: " + ex.GetType().Name); } },
                Diag = Diag,
                Portao = PortaoAberto,
                UsuarioDaLoja = () => ServicoConversaChat.UsuarioDaLoja,
            };
            _servico.Ligar();
        }
        catch (Exception ex) { Diag("ligar: " + ex.GetType().Name); }
    }

    /// <summary>
    /// O portão, em cache por 30 s: a captura ligada no ERP, ou o chat novo ativo, ou o arquivo fora do
    /// silêncio. Fora dele a varredura não chama a rede.
    /// </summary>
    public static bool PortaoAberto()
    {
        lock (TravaPortao)
        {
            var agora = DateTime.Now;
            if (agora < _portaoAte) return _portao;
            bool ligado;
            try { ligado = ServicoRaspadinhaChat.Ligado(); } catch { ligado = false; }
            bool ativo;
            try { ativo = ServicoConversaChat.Sinal.Ativo; } catch { ativo = false; }
            _portao = ligado || ativo || !ChatArquivo.EmSilencio(agora);
            _portaoAte = agora.AddSeconds(30);
            return _portao;
        }
    }

    /// <summary>Um quadro recebido pelo WebSocket (a marca d'água e o dedup aprendem). Nunca lança.</summary>
    public static void Viu(string? payload) => _servico?.Viu(payload);

    public static void OferecerCredencial(CredencialSendbird? c) => _servico?.OferecerCredencial(c);
    public static void OferecerCabecalhos(CabecalhosDoSdk? cab) => _servico?.OferecerCabecalhos(cab);
    public static void Invalidar(string? motivo) => _servico?.Invalidar(motivo);
    public static void GestorRecarregou() => _servico?.GestorRecarregou();
    public static void LogiVisto() => _servico?.LogiVisto();

    /// <summary>O estado para o sinal (estado.recuperacao), ou null antes de ligar.</summary>
    public static EstadoDaRecuperacao? Estado()
    {
        try { return _servico?.Estado(); } catch { return null; }
    }

    /// <summary>
    /// ProgramData\PdvNativo\chat-raspadinha.txt, uma linha por acontecimento. ⚠️ Nunca a chave, nunca URL
    /// com query, nunca texto de cliente: a linha passa pela rede de segurança (MascararTexto) sempre.
    /// </summary>
    private static void Diag(string texto)
    {
        try { Telas.ChatIfood.DiagRaspadinha(ChatCaptura.MascararTexto(texto.StartsWith("recuperacao", StringComparison.Ordinal) ? texto : "recuperacao " + texto)); }
        catch { }
    }
}
