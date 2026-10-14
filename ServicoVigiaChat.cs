using Pdv.Nucleo;

namespace Pdv;

/// <summary>
/// O AVISO DO CHAT DO iFOOD FORA, DA CAMADA DO CHAT PARA A VENDA (14/10/2026, PDV 1.0.33).
///
/// Quem decide é a <see cref="VigiaChatIfood"/> (Núcleo), dentro da tela do chat. Este serviço só
/// leva o resultado para a venda (aviso vermelho fixo e som) e traz de volta o que o operador
/// tocou ("Depois", "Reabrir"). É o gêmeo da vigia do WhatsApp (<see cref="ServicoWhatsApp"/>):
/// mesmo lugar na tela, mesmo comportamento, eventos na thread da tela.
///
/// O ERP também manda o "chat fora" (vigia do servidor, SQL 171) como aviso do sinal
/// (chat_fora/chat_voltou). Ele cai aqui também, no MESMO aviso, para o operador nunca ver dois.
/// </summary>
public static class ServicoVigiaChat
{
    private static readonly object _trava = new();
    private static EstadoChatIfood? _motivo;
    private static DateTime? _desde;
    private static string? _textoDoServidor;

    /// <summary>Hora de avisar (1ª vez ou repetição): motivo (Login ou Parado) e desde quando.</summary>
    public static event Action<EstadoChatIfood?, DateTime?>? Caiu;
    /// <summary>O chat voltou: o aviso some.</summary>
    public static event Action? Voltou;

    /// <summary>A venda chama "Depois" e "Reabrir" por aqui; a tela do chat liga quem executa.</summary>
    public static Action? AoAdiar { get; set; }
    public static Func<Task>? AoReabrir { get; set; }

    /// <summary>O chat está fora agora (pela vigia do caixa ou pelo aviso do ERP).</summary>
    public static bool Fora { get { lock (_trava) return _desde is not null || _textoDoServidor is not null; } }
    public static EstadoChatIfood? Motivo { get { lock (_trava) return _motivo; } }
    public static DateTime? Desde { get { lock (_trava) return _desde; } }

    /// <summary>A vigia do caixa decidiu avisar.</summary>
    public static void Avisar(EstadoChatIfood? motivo, DateTime? desde)
    {
        lock (_trava) { _motivo = motivo; _desde = desde ?? DateTime.Now; }
        Caiu?.Invoke(motivo, desde);
    }

    /// <summary>A vigia do caixa viu o chat de volta.</summary>
    public static void AvisarVoltou()
    {
        lock (_trava) { _motivo = null; _desde = null; _textoDoServidor = null; }
        Voltou?.Invoke();
    }

    /// <summary>O ERP disse que o chat desta loja está fora (aviso chat_fora do sinal).</summary>
    public static void DoServidor(string texto) { lock (_trava) _textoDoServidor = texto; }

    /// <summary>O ERP disse que voltou: só apaga se a vigia do caixa também acha que está tudo bem.</summary>
    public static bool ServidorVoltou()
    {
        lock (_trava)
        {
            _textoDoServidor = null;
            return _desde is null;
        }
    }

    public static void Adiar() { try { AoAdiar?.Invoke(); } catch { /* o aviso some de qualquer jeito */ } }

    public static async Task ReabrirAsync()
    {
        try { if (AoReabrir is { } r) await r(); }
        catch { /* a tela do chat anota no diagnóstico */ }
    }

    /// <summary>Só para a bateria: volta ao estado de processo recém-aberto.</summary>
    public static void ZerarParaTeste()
    {
        lock (_trava) { _motivo = null; _desde = null; _textoDoServidor = null; }
    }
}
