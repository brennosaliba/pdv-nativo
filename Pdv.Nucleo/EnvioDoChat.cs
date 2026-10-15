using System.Text.Json.Nodes;

namespace Pdv.Nucleo;

/// <summary>
/// 1.0.35 (14/10/2026, caso #8039): o RASTRO de um envio pelo SDK, tentativa por tentativa, para o
/// detalhe que vai ao ERP junto do resultado da saída (ver <see cref="ConversaRaspadinha.MontarDetalhe"/>).
/// Guarda o detalhe da ÚLTIMA tentativa que a página contou, o código do Sendbird de cada uma, se o
/// caixa aqueceu o SDK no meio, o que o plano B fez e o erro cru do script quando o caixa o trocou por
/// erro_envio. Puro: não fala com a página nem com a rede.
/// </summary>
public sealed class RastroDoEnvio
{
    private readonly string? _versao;
    private readonly List<int?> _codigos = new();
    private JsonObject? _ultimo;
    private int _tentativa;

    public RastroDoEnvio(string? versao) => _versao = versao;

    /// <summary>Quantas tentativas chegaram à página.</summary>
    public int Tentativas => _codigos.Count;

    public bool Aqueceu { get; set; }

    /// <summary>recarregou, na_frente, login, teto, sem_sdk, sem_tempo ou null (não houve plano B).</summary>
    public string? PlanoB { get; set; }

    public string? ErroScript { get; private set; }

    /// <summary>Uma tentativa que a página respondeu (ou não). <paramref name="erroCru"/> é o erro como veio.</summary>
    public void Registrar(int tentativa, JsonObject? detalhe, string? erroCru)
    {
        _tentativa = tentativa;
        int? codigo = detalhe?["codigo"] is JsonValue v && v.TryGetValue<int>(out var c) ? c : null;
        _codigos.Add(codigo);
        if (detalhe is not null) _ultimo = detalhe;
        // o erro cru só importa quando o caixa o trocou por erro_envio (token, limite, sem_resposta)
        ErroScript = erroCru is not null && ConversaRaspadinha.ErrosDoScript.Contains(erroCru) ? erroCru : null;
    }

    /// <summary>O detalhe para o ERP (até 600 bytes), ou null quando nenhuma tentativa chegou à página.</summary>
    public JsonObject? Detalhe()
        => ConversaRaspadinha.MontarDetalhe(_ultimo, _tentativa, _codigos.Count, _codigos, Aqueceu, PlanoB, ErroScript, _versao);
}

/// <summary>
/// 1.0.35 (14/10/2026, caso #8039): o PLANO B do envio pelo SDK. Na Savassi, de 10/10 a 14/10, o SDK
/// recusou 41 de 42 respostas do robô sem nenhum quadro sair da página; a única que saiu foi 70 min
/// depois de o caixa reiniciar. A conexão nova vem só pela RECARGA da página do Gestor (o caixa nunca
/// chama connect, disconnect nem reconnect do SDK). É a mesma saída, a mesma reserva e o mesmo portão:
/// não é mensagem nova ao cliente. Só quando o iFood RECUSOU (onFailed ou promessa rejeitada) e nenhum
/// quadro foi visto saindo; nunca com o Gestor na frente do operador; no máximo 1 a cada 10 min e 3
/// por hora; e nunca depois de vencida a reserva da saída.
/// </summary>
public static class PlanoBDoEnvio
{
    public const string Recarregou = "recarregou";
    public const string NaFrente = "na_frente";
    public const string Login = "login";
    public const string Teto = "teto";
    public const string SemSdk = "sem_sdk";
    public const string SemTempo = "sem_tempo";
    /// <summary>A recarga lançou ou passou do prazo (só no rastro local; no detalhe vira sem_sdk).</summary>
    public const string Erro = "erro";

    /// <summary>O serviço espera a tela no máximo isto (recarga + SDK aberto + aquecer).</summary>
    public static readonly TimeSpan PrazoDaRecarga = TimeSpan.FromSeconds(40);
    /// <summary>A tela espera o SDK voltar aberto no máximo isto, olhando a cada 1,5 s.</summary>
    public static readonly TimeSpan EsperaDoSdk = TimeSpan.FromSeconds(25);
    public static readonly TimeSpan PassoDaEspera = TimeSpan.FromSeconds(1.5);
    /// <summary>Depois do SDK aberto, abrir a conversa do pedido leva no máximo isto.</summary>
    public static readonly TimeSpan Aquecimento = TimeSpan.FromSeconds(10);
    /// <summary>
    /// A guarda contra a mensagem dobrada: durante a recarga, um quadro desta resposta (o SDK que reenvia
    /// sozinho ao reconectar) conta como dela. Maior que o prazo da recarga e que a reserva (50 s).
    /// </summary>
    public static readonly TimeSpan GuardaDaRecarga = TimeSpan.FromSeconds(60);

    /// <summary>A recusa pede o plano B? Só erro_envio vindo do SDK (falhou_sdk ou promessa).</summary>
    public static bool Cabe(string? erro, JsonObject? detalhe)
        => erro == "erro_envio"
           && detalhe?["etapa"] is JsonValue v && v.TryGetValue<string>(out var e) && e is "falhou_sdk" or "promessa";

    /// <summary>
    /// O que impede a recarga AGORA, na ordem do contrato: tela sem o Gestor pronto (sem_sdk); a camada
    /// do chat na frente do operador ou um "Abrir e colar" em curso (na_frente); o Gestor pedindo login
    /// (login). Null = pode recarregar (falta só o teto).
    /// </summary>
    public static string? Impedimento(bool pronto, bool naFrente, bool colando, bool pedeLogin)
        => !pronto ? SemSdk : naFrente || colando ? NaFrente : pedeLogin ? Login : null;

    /// <summary>O diagnóstico da página depois da recarga: o SDK foi achado e a conexão está OPEN?</summary>
    public static bool SdkAberto(JsonObject? diag)
        => diag is not null
           && diag["achou"] is JsonValue a && a.TryGetValue<bool>(out var achou) && achou
           && diag["conexao"] is JsonValue c && c.TryGetValue<string>(out var cx) && cx == "OPEN";

    /// <summary>O diagnóstico diz que a página caiu na tela de login do Gestor?</summary>
    public static bool PedeLogin(JsonObject? diag)
        => diag?["login"] is JsonValue l && l.TryGetValue<bool>(out var b) && b;

    /// <summary>O que a tela devolveu, no vocabulário do contrato (o resto vira erro).</summary>
    public static string Normalizar(string? r)
        => r is Recarregou or NaFrente or Login or Teto or SemSdk ? r : Erro;

    /// <summary>O valor que vai no detalhe ao ERP (erro não é do contrato: vira sem_sdk).</summary>
    public static string ParaODetalhe(string r) => r == Erro ? SemSdk : r;
}

/// <summary>
/// 1.0.35: o teto PRÓPRIO da recarga do plano B, fora do teto de recriar o controle: no máximo 1 a
/// cada 10 min e 3 por hora. Numa loja onde o envio falha o tempo todo, o Gestor escondido não vira
/// moedor de recarga.
/// </summary>
public sealed class TetoDaRecargaDoEnvio
{
    public static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(10);
    public const int PorHora = 3;

    private readonly TetoPorHora _hora = new(PorHora);
    private readonly object _trava = new();
    private DateTime? _ultima;

    /// <summary>Pode recarregar agora? Quando pode, conta esta recarga.</summary>
    public bool Permitir(DateTime agoraUtc)
    {
        lock (_trava)
        {
            if (_ultima is { } u && agoraUtc - u < Intervalo) return false;
            if (!_hora.Permitir(agoraUtc)) return false;
            _ultima = agoraUtc;
            return true;
        }
    }
}
