using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pdv.Nucleo;

/// <summary>O que o servidor do Sendbird devolveu para um quadro do caixa: o eco (deu, com o msg_id) ou o EROR (código e mensagem crua).</summary>
public sealed record EcoDoSocket(bool Deu, string? MsgId, int? Codigo, string? Mensagem);

/// <summary>
/// 1.0.39: as portas da camada C para o navegador (na tela, o CDP; na bateria, falsas). Só API padrão do
/// navegador: os WebSocket abertos do Sendbird achados pela heap, o send de um deles e a liberação do grupo
/// de objetos do CDP.
/// </summary>
public interface IPortasDoSocket
{
    /// <summary>As URLs dos WebSocket do Sendbird ABERTOS (readyState 1), na ordem em que a heap os deu. O índice é o do envio.</summary>
    Task<IReadOnlyList<string?>> AbertosAsync();
    /// <summary>Manda o quadro no socket deste índice. true: o send foi chamado; false: o socket já não estava aberto (nada saiu). Exceção: não se sabe.</summary>
    Task<bool> EnviarAsync(int indice, string quadro);
    /// <summary>Solta o grupo de objetos do CDP (sempre, no finally).</summary>
    Task LiberarAsync();
}

/// <summary>O que a camada C precisa para UM envio. As URLs ficam só em memória: nunca saem em log nem diagnóstico.</summary>
/// <param name="UrlDoCanal">A URL do requestId do CDP que recebeu o último quadro daquele canal (pista a).</param>
/// <param name="UsuarioDaLoja">O user_id da loja já aprendido na conexão do chat de clientes (pista b).</param>
/// <param name="Atividade">1.0.40: a hora do último quadro RECEBIDO na conexão com aquela URL (null: desconhecida). Desempata entre sockets do mesmo usuário.</param>
/// <param name="Usuario">1.0.42: o usuário que o livro das conexões aprendeu para aquela URL (pelo LOGI ou pelo canal de cliente), quando a URL não diz.</param>
public sealed record PedidoPeloSocket(string Canal, string OrderUuid, string Texto, string ReqId, string? UrlDoCanal, string? UsuarioDaLoja,
    Func<string?, DateTime?>? Atividade = null, Func<string?, string?>? Usuario = null);

/// <summary>
/// 1.0.40: o resumo dos sockets abertos do Sendbird para o sinal: quantos, a escolha, quantos user_id
/// DISTINTOS, quantos SEM user_id na URL e a hora do último quadro do socket escolhido. Só contagens e uma hora:
/// nunca id, URL nem token.
/// </summary>
public sealed record ResumoDosSockets(int Abertos, string Escolha, int Usuarios, int SemUsuario, DateTime? AtivoEm);

/// <summary>
/// O resultado da camada C no vocabulário do envio de hoje: Ok com o msg_id do eco; ou Erro
/// "erro_envio" (EROR do servidor: falha certa), "sem_eco" (incerta: nunca reenviar), "sdk_ausente"
/// (nenhum socket, ou ambíguo: nada saiu) ou "canal_errado". O detalhe já vem pela lista branca.
/// 1.0.40: <paramref name="Sockets"/> é o resumo dos sockets vistos neste envio (para o sinal).
/// Revisão: <paramref name="UrlEscolhida"/> é a URL do socket que recebeu o quadro, SÓ em memória (a tela
/// recalcula a atividade dele a cada sinal); nunca vai ao detalhe, ao rastro nem ao diagnóstico.
/// </summary>
public sealed record ResultadoDoSocket(bool Ok, string? Erro, string? MsgId, JsonObject Detalhe, string Escolha, int Abertos,
    ResumoDosSockets? Sockets = null, string? UrlEscolhida = null);

/// <summary>
/// 1.0.40 (revisão, 16/10/2026): O LIVRO DAS CONEXÕES do Sendbird vistas pelo CDP, por requestId: a URL (só
/// dos sockets do Sendbird), o user_id da URL (de qualquer WebSocket, como sempre) e a hora do último quadro
/// RECEBIDO. Antes a tela tinha três dicionários irmãos que só cresciam (o fechamento só tirava a atividade) e
/// eram apagados INTEIROS ao passar de 200 criações: numa rede instável o Gestor reconecta o dia todo, o socket
/// 201 zerava tudo e os sockets VIVOS criados antes ficavam sem URL (fora da pista do canal), sem atividade (a
/// escolha do mais ativo virava a ordem da heap, o caso do socket meio-morto que o 1.0.40 quis evitar) e sem
/// user_id (a loja parava de ser aprendida até reiniciar o caixa). Agora o fechamento tira a conexão dos três
/// e o teto é só de emergência, nunca atingido em operação normal. Só memória: nada daqui vai a log,
/// diagnóstico ou rede. Seguro entre threads (os eventos do CDP chegam de qualquer uma).
/// </summary>
public sealed class ConexoesDoSocket
{
    /// <summary>O teto de emergência (só quando o fechamento nunca chega).</summary>
    public const int Teto = 200;

    private readonly object _trava = new();
    private readonly Dictionary<string, string> _url = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _usuario = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _atividade = new(StringComparer.Ordinal);

    /// <summary>
    /// Network.webSocketCreated: guarda o user_id de qualquer WebSocket e a URL só se for do Sendbird.
    /// Devolve true quando o socket é do Sendbird.
    /// </summary>
    public bool Abriu(string? req, string? url)
    {
        if (string.IsNullOrEmpty(req)) return false;
        var doSendbird = QuadroSendbird.EhSocketDoSendbird(url);
        lock (_trava)
        {
            if (_usuario.Count >= Teto && !_usuario.ContainsKey(req)) _usuario.Clear();
            _usuario[req] = QuadroSendbird.UserIdDaUrl(url);
            if (doSendbird)
            {
                if (_url.Count >= Teto && !_url.ContainsKey(req)) { _url.Clear(); _atividade.Clear(); }
                _url[req] = url!;
                _atividade.Remove(req);
            }
        }
        return doSendbird;
    }

    /// <summary>Network.webSocketClosed: a conexão sai dos três (URL, usuário e atividade).</summary>
    public void Fechou(string? req)
    {
        if (string.IsNullOrEmpty(req)) return;
        lock (_trava)
        {
            _url.Remove(req);
            _usuario.Remove(req);
            _atividade.Remove(req);
        }
    }

    /// <summary>
    /// Network.webSocketFrameReceived (qualquer comando, PONG e binário inclusive): atividade daquela conexão.
    /// Devolve true quando é uma conexão do Sendbird conhecida; uma que já fechou não ressuscita.
    /// </summary>
    public bool Recebeu(string? req, DateTime agora)
    {
        if (string.IsNullOrEmpty(req)) return false;
        lock (_trava)
        {
            if (!_url.ContainsKey(req)) return false;
            _atividade[req] = agora;
            return true;
        }
    }

    /// <summary>A URL da conexão (só sockets do Sendbird vivos). Só memória.</summary>
    public string? Url(string? req)
    {
        if (string.IsNullOrEmpty(req)) return null;
        lock (_trava) return _url.TryGetValue(req, out var u) ? u : null;
    }

    /// <summary>O user_id da URL daquela conexão (qualquer WebSocket vivo), ou null.</summary>
    public string? Usuario(string? req)
    {
        if (string.IsNullOrEmpty(req)) return null;
        lock (_trava) return _usuario.TryGetValue(req, out var u) ? u : null;
    }

    /// <summary>
    /// 1.0.42 (TV da Savassi 16/10, 2 sockets sem user_id depois das reconexões do Gestor: a reconexão do SDK vai com
    /// ...&amp;key=&lt;sessão&gt; sem user_id, e a escolha caía em "ambiguo"): o livro aprende o usuário de uma conexão
    /// VIVA do Sendbird também pelo que chega nela: o user_id do LOGI recebido (a verdade do servidor, vale por cima
    /// da URL) e, por qualquer quadro de canal de cliente, o usuário da loja (só quando a conexão ainda não tem
    /// usuário). Conexão desconhecida ou já fechada não aprende. Devolve true quando gravou.
    /// </summary>
    public bool Aprendeu(string? req, string? usuario, bool porCima = false)
    {
        if (string.IsNullOrEmpty(req) || string.IsNullOrWhiteSpace(usuario)) return false;
        var u = usuario.Trim();
        lock (_trava)
        {
            if (!_url.ContainsKey(req)) return false;
            if (!porCima && _usuario.TryGetValue(req, out var ja) && ja is not null) return false;
            _usuario[req] = u;
            return true;
        }
    }

    /// <summary>
    /// 1.0.42: o usuário conhecido para a URL de um socket achado pela heap (ws.url casa com a do CDP pela chave de
    /// sempre): o da URL, ou o aprendido pelo LOGI/canal. Duas conexões com a mesma URL: a que tem usuário. Null: desconhecido.
    /// </summary>
    public string? UsuarioPorUrl(string? url)
    {
        if (EnvioPeloSocket.ChaveDaUrl(url) is not { } k) return null;
        lock (_trava)
        {
            foreach (var (req, u) in _url)
                if (EnvioPeloSocket.ChaveDaUrl(u) == k && _usuario.TryGetValue(req, out var usuario) && usuario is not null) return usuario;
        }
        return null;
    }

    /// <summary>Quantas conexões do Sendbird estão no livro (abertas e ainda não fechadas).</summary>
    public int Vivas { get { lock (_trava) return _url.Count; } }

    /// <summary>A URL e a hora do último quadro recebido de cada conexão do Sendbird que já recebeu algo.</summary>
    public IReadOnlyList<(string Url, DateTime Em)> Ativas()
    {
        lock (_trava)
        {
            var lista = new List<(string, DateTime)>(_atividade.Count);
            foreach (var kv in _url)
                if (_atividade.TryGetValue(kv.Key, out var em)) lista.Add((kv.Value, em));
            return lista;
        }
    }

    /// <summary>A atividade de UM socket achado pela heap, pela URL (ws.url casa com a do CDP pela chave de sempre); null: desconhecida.</summary>
    public DateTime? AtividadePorUrl(string? url) => url is null ? null : EnvioPeloSocket.AtividadeDosAbertos(new[] { url }, Ativas())[0];

    /// <summary>A navegação: página nova, livro novo.</summary>
    public void Limpar()
    {
        lock (_trava)
        {
            _url.Clear();
            _usuario.Clear();
            _atividade.Clear();
        }
    }
}

/// <summary>
/// 1.0.39: as esperas pelo eco, por req_id. O quadro RECEBIDO (MESG ou EROR) com o req_id de um envio do
/// caixa resolve a espera dele; o resto passa reto. Seguro entre threads (a tela entrega os quadros do CDP).
/// </summary>
public sealed class EcosDoSocket
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<EcoDoSocket>> _esperas = new(StringComparer.Ordinal);

    public int Esperando => _esperas.Count;

    /// <summary>Começa a esperar o eco deste req_id (antes do send: o eco pode chegar antes de o send voltar).</summary>
    public Task<EcoDoSocket> Aguardar(string reqId)
    {
        if (_esperas.Count > 100) _esperas.Clear();   // teto: nada fica preso para sempre
        var tcs = new TaskCompletionSource<EcoDoSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        _esperas[reqId] = tcs;
        return tcs.Task;
    }

    /// <summary>Um quadro recebido. Devolve true quando era o eco de um envio que esperava.</summary>
    public bool Ver(string? payload)
    {
        try
        {
            if (_esperas.IsEmpty || string.IsNullOrEmpty(payload) || !payload.Contains("pdv", StringComparison.Ordinal)) return false;
            if (EnvioPeloSocket.LerEco(payload) is not { } e) return false;
            if (!_esperas.TryRemove(e.ReqId, out var tcs)) return false;
            tcs.TrySetResult(e.Eco);
            return true;
        }
        catch { return false; }
    }

    public void Cancelar(string reqId)
    {
        if (_esperas.TryRemove(reqId, out var tcs)) tcs.TrySetCanceled();
    }
}

/// <summary>
/// 1.0.39 (15/10/2026, Savassi, pedido 5403): A CAMADA C DO ENVIO, PELO SOCKET DO SENDBIRD.
///
/// Na Savassi o caixa 1.0.38 não achava o SDK (ele nasce num chunk que o webpack, o React, o singleton, a
/// heap e o berço não alcançam), com a conexão do Gestor VIVA: 6 sockets abertos e quadros chegando. Desde
/// 12:54 o robô não respondeu ninguém. Decisão do dono: não procurar mais o SDK dentro do Gestor. A camada C
/// usa só API padrão do navegador e o protocolo de fio do Sendbird, que o caixa já lê:
///  1. só quando as camadas de hoje não acham o SDK (a lista vazia do script, <see cref="Cabe"/>);
///  2. os WebSocket abertos do Sendbird pela heap (a tela, pelo CDP);
///  3. UM socket, sem chute (<see cref="Escolher"/>): o do canal, o do usuário da loja, o único, ou nenhum;
///  4. UM quadro MESG (<see cref="QuadroMesg"/>) com req_id único por tentativa;
///  5. a confirmação SÓ pelo eco: MESG com o mesmo req_id deu; EROR com o mesmo req_id é falha certa; sem
///     eco no prazo é incerta, e incerta NUNCA é reenviada (regra do 176/185).
/// Nenhum token, URL com chave, session key ou texto de cliente vai para log ou diagnóstico.
/// </summary>
public static class EnvioPeloSocket
{
    public const string Via = "socket";
    public const string PorCanal = "canal";
    public const string PorUsuario = "usuario";
    public const string Unico = "unico";
    public const string Ambiguo = "ambiguo";
    public const string Nenhum = "nenhum";

    /// <summary>As escolhas do socket, no contrato do diagnóstico e do detalhe.</summary>
    public static readonly IReadOnlySet<string> Escolhas = new HashSet<string>(StringComparer.Ordinal)
    {
        PorCanal, PorUsuario, Unico, Ambiguo, Nenhum,
    };

    /// <summary>Quanto o caixa espera o eco do servidor depois do send.</summary>
    public static readonly TimeSpan PrazoDoEco = TimeSpan.FromSeconds(12);

    private static readonly JsonSerializerOptions JsonDoQuadro = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// A camada C cabe? Só quando o script respondeu que a lista de instâncias do SDK está vazia
    /// (erro sdk_ausente com a etapa sdk_ausente). SDK achado, recusa do SDK, token, sem resposta ou
    /// script ausente: não.
    /// </summary>
    public static bool Cabe(string? erro, JsonObject? detalhe)
        => erro == "sdk_ausente"
           && detalhe?["etapa"] is JsonValue v && v.TryGetValue<string>(out var e) && e == "sdk_ausente";

    /// <summary>Roda a camada C só se <see cref="Cabe"/>; senão devolve null e não a chama.</summary>
    public static async Task<ResultadoDoSocket?> SeCouberAsync(string? erro, JsonObject? detalhe, Func<Task<ResultadoDoSocket>> camadaC)
        => Cabe(erro, detalhe) ? await camadaC().ConfigureAwait(false) : null;

    /// <summary>As etapas que só a camada C (o C#) produz.</summary>
    public static readonly IReadOnlySet<string> EtapasDoSocket = new HashSet<string>(StringComparer.Ordinal)
    {
        "sem_socket", "socket_ambiguo", "falhou_socket", "sem_eco",
    };

    /// <summary>
    /// O detalhe que veio da PÁGINA (dado não confiável) sem as marcas que só a camada C põe: via, escolha,
    /// abertos e as etapas do socket (viram "geral"). Sem isto, a página poderia dizer "falhou_socket" e
    /// fazer o laço tratar um quadro visto saindo como falha certa (e mandar de novo).
    /// </summary>
    public static JsonObject? SemMarcaDaPagina(JsonObject? d)
    {
        if (d is null) return null;
        var o = (JsonObject)d.DeepClone();
        o.Remove("via"); o.Remove("escolha"); o.Remove("abertos");
        if (o["etapa"] is JsonValue v && v.TryGetValue<string>(out var e) && EtapasDoSocket.Contains(e)) o["etapa"] = "geral";
        return o.Count == 0 ? null : o;
    }

    /// <summary>O req_id de uma tentativa: "pdv" + saída + "-" + tentativa + "-" + ms. Único por tentativa.</summary>
    public static string ReqId(long saidaId, int tentativa, long ms) => $"pdv{saidaId}-{tentativa}-{ms}";

    /// <summary>O req_id é de um quadro do caixa (e não do Gestor)?</summary>
    public static bool EhNosso(string? reqId) => reqId is { Length: > 3 } r && r.StartsWith("pdv", StringComparison.Ordinal);

    /// <summary>
    /// O quadro MESG: "MESG" + o JSON (channel_url, message, data, custom_type, mention_type,
    /// mentioned_user_ids, req_id, nessa ordem e sem campo indefinido) + "\n".
    /// </summary>
    public static string QuadroMesg(string canal, string texto, string reqId)
    {
        var o = new JsonObject
        {
            ["channel_url"] = canal,
            ["message"] = texto,
            ["data"] = "",
            ["custom_type"] = "",
            ["mention_type"] = "users",
            ["mentioned_user_ids"] = new JsonArray(),
            ["req_id"] = reqId,
        };
        return "MESG" + o.ToJsonString(JsonDoQuadro) + "\n";
    }

    /// <summary>
    /// Escolhe UM socket entre os abertos, sem chute. Devolve o índice (-1 quando não há) e a escolha.
    ///  (a) o da URL do canal (o requestId que recebeu o último quadro daquele canal), como sempre;
    ///  (b) com o usuário da loja conhecido (aprendido, ou o user_id da URL do canal): entre os sockets cujo
    ///      user_id é o da loja, o MAIS RECENTEMENTE ATIVO;
    ///  (c) "único": todos os sockets abertos COM user_id têm o MESMO user_id (e a loja, se conhecida, é esse
    ///      usuário): o mais ativo deles. Sockets SEM user_id na URL só entram pela pista (a); se só existem
    ///      sockets sem user_id, a loja é desconhecida e há exatamente um aberto, é o único (como sempre);
    ///  (d) qualquer outra combinação: ambíguo. Nunca mandar como outro usuário (falha fechada), também quando
    ///      a loja é conhecida e o socket não tem user_id.
    /// 1.0.40 (16/10/2026, Savassi): o SDK JS do Sendbird abre cada conexão com a query DIFERENTE
    /// (Request-Sent-Timestamp, key da sessão, token renovado), então "URLs iguais" nunca acontecia com as
    /// duas conexões vivas do Gestor (singleton_conexao=OUTRO) e tudo caía em ambíguo. Agora o que iguala é
    /// o user_id, e a atividade (o último quadro recebido naquela conexão) desempata. Empate ou atividade
    /// desconhecida: a ordem da heap.
    /// </summary>
    /// <param name="usuarios">
    /// 1.0.42: o usuário de cada socket aberto (mesma ordem), aprendido pelo livro das conexões (LOGI recebido, canal
    /// de cliente), para o socket cuja URL não diz o user_id (a reconexão do SDK vai só com key=). Null ou faltando:
    /// vale só a URL, como antes.
    /// </param>
    public static (int Indice, string Escolha) Escolher(IReadOnlyList<string?> abertos, string? urlDoCanal, string? usuarioDaLoja,
        IReadOnlyList<DateTime?>? atividade = null, IReadOnlyList<string?>? usuarios = null)
    {
        var validos = new List<int>();
        for (var i = 0; i < abertos.Count; i++)
            if (QuadroSendbird.EhSocketDoSendbird(abertos[i])) validos.Add(i);
        if (validos.Count == 0) return (-1, Nenhum);
        if (ChaveDaUrl(urlDoCanal) is { } kc)
            foreach (var i in validos)
                if (ChaveDaUrl(abertos[i]) == kc) return (i, PorCanal);

        var daLoja = !string.IsNullOrWhiteSpace(usuarioDaLoja) ? usuarioDaLoja.Trim() : QuadroSendbird.UserIdDaUrl(urlDoCanal);
        var comUsuario = validos.Select(i => (Indice: i, Usuario: UsuarioDoSocket(abertos, usuarios, i))).Where(x => x.Usuario is not null).ToList();
        if (daLoja is not null)
        {
            var dela = comUsuario.Where(x => x.Usuario == daLoja).Select(x => x.Indice).ToList();
            if (dela.Count > 0) return (MaisAtivo(dela, atividade), PorUsuario);
        }
        if (comUsuario.Count > 0)
        {
            var distintos = comUsuario.Select(x => x.Usuario!).Distinct(StringComparer.Ordinal).ToList();
            if (distintos.Count == 1 && (daLoja is null || daLoja == distintos[0]))
                return (MaisAtivo(comUsuario.Select(x => x.Indice).ToList(), atividade), Unico);
            return (-1, Ambiguo);
        }
        // só sockets sem user_id na URL: um só, com a loja desconhecida, é o único; o resto é ambíguo
        if (daLoja is null && validos.Count == 1) return (validos[0], Unico);
        return (-1, Ambiguo);
    }

    /// <summary>1.0.42: o usuário do socket i: o da URL; senão o que o livro aprendeu (LOGI, canal de cliente); senão null.</summary>
    public static string? UsuarioDoSocket(IReadOnlyList<string?> abertos, IReadOnlyList<string?>? usuarios, int i)
    {
        if (i < 0 || i >= abertos.Count) return null;
        var daUrl = QuadroSendbird.UserIdDaUrl(abertos[i]);
        if (!string.IsNullOrWhiteSpace(daUrl)) return daUrl.Trim();
        var aprendido = usuarios is not null && i < usuarios.Count ? usuarios[i] : null;
        return string.IsNullOrWhiteSpace(aprendido) ? null : aprendido.Trim();
    }

    /// <summary>1.0.42: os usuários dos sockets abertos pelo livro das conexões (mesma ordem de <paramref name="abertos"/>). Nunca lança.</summary>
    public static IReadOnlyList<string?> UsuariosDosAbertos(IReadOnlyList<string?> abertos, Func<string?, string?>? usuarioPorUrl)
    {
        var lista = new string?[abertos.Count];
        if (usuarioPorUrl is null) return lista;
        for (var i = 0; i < abertos.Count; i++)
        {
            try { lista[i] = usuarioPorUrl(abertos[i]); } catch { lista[i] = null; }
        }
        return lista;
    }

    /// <summary>O índice de atividade mais recente entre os candidatos; desconhecida perde; empate: o primeiro (ordem da heap).</summary>
    private static int MaisAtivo(IReadOnlyList<int> candidatos, IReadOnlyList<DateTime?>? atividade)
    {
        var melhor = candidatos[0];
        var melhorEm = Atividade(atividade, melhor);
        for (var k = 1; k < candidatos.Count; k++)
        {
            var em = Atividade(atividade, candidatos[k]);
            if (em > melhorEm) { melhor = candidatos[k]; melhorEm = em; }
        }
        return melhor;
    }

    private static DateTime Atividade(IReadOnlyList<DateTime?>? atividade, int i)
        => atividade is not null && i < atividade.Count && atividade[i] is { } d ? d : DateTime.MinValue;

    /// <summary>
    /// 1.0.40: a atividade de cada socket aberto (mesma ordem de <paramref name="abertos"/>), casando a URL da heap
    /// (ws.url) com a URL de cada conexão do CDP pela mesma chave de sempre (<see cref="ChaveDaUrl"/>). Duas conexões
    /// com a mesma URL: vale a mais recente. Sem conexão com aquela URL: desconhecida (null).
    /// </summary>
    public static IReadOnlyList<DateTime?> AtividadeDosAbertos(IReadOnlyList<string?> abertos, IEnumerable<(string Url, DateTime Em)> conexoes)
    {
        var porChave = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var (url, em) in conexoes)
            if (ChaveDaUrl(url) is { } k && (!porChave.TryGetValue(k, out var ja) || em > ja)) porChave[k] = em;
        var lista = new DateTime?[abertos.Count];
        for (var i = 0; i < abertos.Count; i++)
            lista[i] = ChaveDaUrl(abertos[i]) is { } k && porChave.TryGetValue(k, out var em) ? em : null;
        return lista;
    }

    /// <summary>1.0.40: o resumo dos sockets abertos para o sinal (contagens e a atividade do escolhido; nunca id ou URL).</summary>
    public static ResumoDosSockets Resumir(IReadOnlyList<string?> abertos, int indice, string escolha, IReadOnlyList<DateTime?>? atividade,
        IReadOnlyList<string?>? usuariosAprendidos = null)
    {
        // 1.0.42: o usuário de cada socket pela URL ou pelo livro (sockets_sem_usuario só conta o que ninguém sabe de quem é)
        var validos = Enumerable.Range(0, abertos.Count).Where(i => QuadroSendbird.EhSocketDoSendbird(abertos[i])).ToList();
        var dosValidos = validos.Select(i => UsuarioDoSocket(abertos, usuariosAprendidos, i)).ToList();
        var usuarios = dosValidos.Where(u => u is not null).Distinct(StringComparer.Ordinal).Count();
        var semUsuario = dosValidos.Count(u => u is null);
        DateTime? ativoEm = indice >= 0 && atividade is not null && indice < atividade.Count ? atividade[indice] : null;
        return new ResumoDosSockets(validos.Count, Escolhas.Contains(escolha) ? escolha : Ambiguo, usuarios, semUsuario, ativoEm);
    }

    /// <summary>
    /// 1.0.40 (revisão): o resumo com a atividade do escolhido NA HORA do sinal. A contagem (a cada 2 min) e o
    /// envio congelavam AtivoEm, e socket_ativo_s só crescia entre eles mesmo com PONG chegando no socket
    /// escolhido (o eco do envio chegava e o sinal dizia por 2 min que o socket estava parado). Sem atividade ao
    /// vivo (a conexão fechou) ou mais velha, fica a da contagem: nunca anda para trás.
    /// </summary>
    public static ResumoDosSockets AoVivo(ResumoDosSockets r, DateTime? atividadeAgora)
        => atividadeAgora is { } em && (r.AtivoEm is null || em > r.AtivoEm) ? r with { AtivoEm = em } : r;

    /// <summary>
    /// 1.0.40: escreve no diagnóstico do SDK (estado.sdk) sockets_sb_abertos, socket_escolha, sockets_usuarios,
    /// sockets_sem_usuario e socket_ativo_s (segundos desde o último quadro do socket escolhido, ou null).
    /// </summary>
    public static void DiagDosSockets(JsonObject alvo, ResumoDosSockets r, DateTime agora)
    {
        alvo["sockets_sb_abertos"] = Math.Clamp(r.Abertos, 0, 100_000);
        alvo["socket_escolha"] = r.Escolha;
        alvo["sockets_usuarios"] = Math.Clamp(r.Usuarios, 0, 100_000);
        alvo["sockets_sem_usuario"] = Math.Clamp(r.SemUsuario, 0, 100_000);
        alvo["socket_ativo_s"] = r.AtivoEm is { } em ? (JsonNode?)(int)Math.Clamp((agora - em).TotalSeconds, 0, 100_000) : null;
    }

    /// <summary>A URL normalizada para comparar a do CDP com a do ws.url (a barra do caminho, maiúsculas do host).</summary>
    public static string? ChaveDaUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try { return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) ? u.AbsoluteUri : url.Trim(); }
        catch { return url.Trim(); }
    }

    /// <summary>
    /// O eco de um quadro do caixa: MESG com req_id "pdv..." (deu, com o msg_id) ou EROR com req_id "pdv..."
    /// (não deu, com o código e a mensagem crua, que só sai daqui limpa). Qualquer outro quadro: null.
    /// </summary>
    public static (string ReqId, EcoDoSocket Eco)? LerEco(string? payload)
    {
        try
        {
            var cmd = QuadroSendbird.Comando(payload);
            if (cmd is not ("MESG" or "EROR")) return null;
            using var d = JsonDocument.Parse(Corpo(payload!));
            var r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var req = r.TryGetProperty("req_id", out var rq) && rq.ValueKind == JsonValueKind.String ? rq.GetString() : null;
            if (!EhNosso(req)) return null;
            if (cmd == "MESG")
            {
                string? id = null;
                if (r.TryGetProperty("msg_id", out var mi))
                    id = mi.ValueKind switch
                    {
                        JsonValueKind.Number => mi.GetRawText(),
                        JsonValueKind.String => mi.GetString(),
                        _ => null,
                    };
                return (req!, new EcoDoSocket(true, string.IsNullOrWhiteSpace(id) ? null : id, null, null));
            }
            int? codigo = r.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var ci) ? ci : null;
            var msg = r.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return (req!, new EcoDoSocket(false, null, codigo, msg));
        }
        catch { return null; }
    }

    /// <summary>O channel_url de qualquer quadro (MESG, READ, BRDM...), para saber qual conexão recebeu aquele canal.</summary>
    public static string? CanalDoQuadro(string? payload)
    {
        try
        {
            if (string.IsNullOrEmpty(payload) || payload.Length < 5 || !payload.Contains("\"channel_url\"", StringComparison.Ordinal)) return null;
            if (QuadroSendbird.Comando(payload) is null) return null;
            using var d = JsonDocument.Parse(Corpo(payload));
            return d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("channel_url", out var c)
                   && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } s ? s : null;
        }
        catch { return null; }
    }

    private static readonly Regex ReNomeDeCampo = new(@"^[a-z][a-z0-9_]{0,39}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Os NOMES dos campos de um MESG (nunca os valores), em ordem alfabética, até 30: para conferir o formato
    /// do quadro do Gestor com o do caixa. Nome que não tem cara de nome (segredo, URL, texto) cai. Não MESG: null.
    /// </summary>
    public static IReadOnlyList<string>? CamposDoMesg(string? payload)
    {
        try
        {
            if (QuadroSendbird.Comando(payload) != "MESG") return null;
            using var d = JsonDocument.Parse(Corpo(payload!));
            if (d.RootElement.ValueKind != JsonValueKind.Object) return null;
            return d.RootElement.EnumerateObject().Select(p => p.Name).Where(n => ReNomeDeCampo.IsMatch(n))
                .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).Take(30).ToList();
        }
        catch { return null; }
    }

    private static string Corpo(string payload) => payload[4..].TrimEnd('\n', '\r');

    // ── o mascaramento por FORMATO ───────────────────────────────────────────
    private static readonly Regex ReJwt = new(@"eyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}(?:\.[A-Za-z0-9_-]*)?", RegexOptions.CultureInvariant);
    private static readonly Regex ReUrl = new(@"[a-zA-Z][a-zA-Z0-9+.\-]*://\S*", RegexOptions.CultureInvariant);
    private static readonly Regex ReOpaco = new(@"[A-Za-z0-9%+/_=-]{24,}", RegexOptions.CultureInvariant);

    /// <summary>
    /// Tira de um texto livre (para log) o que tem CARA de segredo, venha com o nome que vier: JWT, URL
    /// inteira (o socket leva user_id e chave na query) e qualquer sequência opaca de 24 ou mais caracteres
    /// com dígito (session key, access token).
    /// </summary>
    public static string Mascarar(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return "";
        var t = ReJwt.Replace(texto, "#");
        t = ReUrl.Replace(t, "url");
        t = ReOpaco.Replace(t, m => m.Value.Any(char.IsDigit) ? "#" : m.Value);
        return t;
    }

    // ── o envio ──────────────────────────────────────────────────────────────

    /// <summary>
    /// UM envio pela camada C. Confere o canal (do iFood e do pedido), acha os sockets abertos, escolhe UM,
    /// manda UM quadro e espera o eco pelo req_id. O grupo do CDP é liberado sempre (finally). Nunca lança.
    /// <paramref name="antesDeEnviar"/> roda logo antes do send (a confirmação pelo quadro passa a valer pelo
    /// socket: sem eco, incerta).
    /// </summary>
    public static async Task<ResultadoDoSocket> EnviarAsync(IPortasDoSocket portas, EcosDoSocket ecos, PedidoPeloSocket p,
        TimeSpan? prazo = null, Func<TimeSpan, Task>? esperar = null, Action? antesDeEnviar = null)
    {
        var relogio = Stopwatch.StartNew();
        var abertos = 0;
        var escolha = Nenhum;
        var esperando = false;
        ResumoDosSockets? resumo = null;
        string? urlEscolhida = null;
        ResultadoDoSocket Fim(bool ok, string? erro, string? msgId, string? etapa, int? codigo = null, string? msg = null)
            => new(ok, erro, msgId, Detalhe(etapa, escolha, abertos, codigo, msg, (int)Math.Min(60_000, relogio.ElapsedMilliseconds), p), escolha, abertos, resumo, urlEscolhida);

        if (CanalIfood.Ler(p.Canal) is not { } ids || !string.Equals(ids.OrderId, p.OrderUuid, StringComparison.OrdinalIgnoreCase))
            return Fim(false, "canal_errado", null, "canal_errado");
        try
        {
            IReadOnlyList<string?> urls;
            try { urls = await portas.AbertosAsync().ConfigureAwait(false) ?? Array.Empty<string?>(); }
            catch { return Fim(false, "sdk_ausente", null, "sem_socket"); }
            abertos = urls.Count(QuadroSendbird.EhSocketDoSendbird);
            // 1.0.40: a atividade de cada socket (o último quadro recebido na conexão com aquela URL) desempata
            IReadOnlyList<DateTime?>? atividade = null;
            if (p.Atividade is { } ativ)
            {
                var lista = new DateTime?[urls.Count];
                for (var i = 0; i < urls.Count; i++) { try { lista[i] = ativ(urls[i]); } catch { lista[i] = null; } }
                atividade = lista;
            }
            // 1.0.42: o usuário aprendido pelo livro para o socket cuja URL não diz (a reconexão com key=)
            var usuarios = UsuariosDosAbertos(urls, p.Usuario);
            var (indice, esc) = Escolher(urls, p.UrlDoCanal, p.UsuarioDaLoja, atividade, usuarios);
            escolha = esc;
            resumo = Resumir(urls, indice, esc, atividade, usuarios);
            if (indice < 0) return Fim(false, "sdk_ausente", null, esc == Ambiguo ? "socket_ambiguo" : "sem_socket");
            urlEscolhida = urls[indice];   // só memória: a tela recalcula a atividade deste socket a cada sinal

            var quadro = QuadroMesg(p.Canal, p.Texto, p.ReqId);
            var eco = ecos.Aguardar(p.ReqId);
            esperando = true;
            try { antesDeEnviar?.Invoke(); } catch { }
            bool saiu;
            try { saiu = await portas.EnviarAsync(indice, quadro).ConfigureAwait(false); }
            catch { saiu = true; }   // pode ter saído: quem decide é o eco
            if (!saiu) return Fim(false, "sdk_ausente", null, "sem_socket");

            var espera = (esperar ?? (t => Task.Delay(t)))(prazo ?? PrazoDoEco);
            var fim = await Task.WhenAny(eco, espera).ConfigureAwait(false);
            if (fim != eco || !eco.IsCompletedSuccessfully) return Fim(false, "sem_eco", null, "sem_eco");
            var e = eco.Result;
            if (e.Deu) return Fim(true, null, e.MsgId, null);
            return Fim(false, "erro_envio", null, "falhou_socket", e.Codigo, e.Mensagem);
        }
        catch { return Fim(false, esperando ? "sem_eco" : "sdk_ausente", null, esperando ? "sem_eco" : "sem_socket"); }
        finally
        {
            if (esperando) ecos.Cancelar(p.ReqId);
            try { await portas.LiberarAsync().ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>
    /// O detalhe da camada C, já pela lista branca do ERP (etapa, via, escolha, abertos, código, mensagem limpa,
    /// ms). A mensagem do EROR perde os segredos deste envio (texto, canal, pedido, usuário, URLs) e tudo que
    /// tem cara de segredo, antes da limpeza de sempre.
    /// </summary>
    private static JsonObject Detalhe(string? etapa, string escolha, int abertos, int? codigo, string? msg, int ms, PedidoPeloSocket p)
    {
        var o = new JsonObject();
        if (etapa is not null) o["etapa"] = etapa;
        o["via"] = Via;
        o["escolha"] = escolha;
        o["abertos"] = Math.Clamp(abertos, 0, 20);
        if (codigo is int c) o["codigo"] = c;
        if (msg is not null)
        {
            var semSegredo = ConversaRaspadinha.SemSegredos(msg, new[] { p.Texto, p.Canal, p.OrderUuid, p.UsuarioDaLoja, p.UrlDoCanal });
            if (ConversaRaspadinha.MsgDoErroLimpa(Mascarar(semSegredo)) is { } limpa) o["msg"] = limpa;
        }
        o["ms"] = Math.Clamp(ms, 0, 60_000);
        return o;
    }

    /// <summary>A linha do rastro local: só a via, a escolha, as contagens, a etapa e o código (nunca URL, req_id cru do Gestor ou texto).</summary>
    public static string Linha(ResultadoDoSocket r)
        => $"socket: ok={(r.Ok ? "true" : "false")} erro={r.Erro ?? "-"} escolha={r.Escolha} abertos={r.Abertos}"
           + (r.MsgId is { } id && Regex.IsMatch(id, "^[0-9]{1,20}$") ? " msg_id=" + id : "")
           + ConversaRaspadinha.LinhaDoDetalhe(r.Detalhe);
}
