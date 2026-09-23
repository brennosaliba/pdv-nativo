using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>Um entregador atribuído a UM pedido, lido do Gestor de Pedidos.</summary>
/// <param name="Chave">pedido + entregador. É por ela que o mesmo fato não é mandado duas vezes.</param>
/// <param name="OrderId">o uuid do pedido no iFood (o mesmo da chave order/&lt;uuid&gt;).</param>
/// <param name="Pedido">o número curto (displayId), quando o objeto do pedido o traz.</param>
/// <param name="WorkerId">workerExternalUuid: o identificador que a nossa integração JÁ recebe.</param>
/// <param name="Nome">workerName, do jeito que o Gestor escreve ("Luis R."), que é o do print do cliente.</param>
/// <param name="Quando">createdAt do evento ASSIGN_DRIVER, quando dá para ler.</param>
/// <param name="MerchantId">a loja do pedido, segundo o próprio objeto. Quem confere é o servidor.</param>
/// <param name="Veiculo">workerVehicleType (MOTORCYCLE, BIKE...), que o servidor guarda junto.</param>
/// <param name="DeliveryId">o identificador da entrega no iFood, para cruzar com os eventos.</param>
public sealed record EntregadorNoPedido(string Chave, string OrderId, string? Pedido, string WorkerId,
    string Nome, DateTime? Quando, string? MerchantId, string? Veiculo = null, string? DeliveryId = null);

/// <summary>O que aconteceu com UM lote mandado para a borda.</summary>
public enum DesfechoLote
{
    /// <summary>O servidor recebeu e disse o que guardar.</summary>
    Aceito,
    /// <summary>Nada saiu do caixa (sem sessão, sem conexão): o lote fica na fila.</summary>
    SemRede,
    /// <summary>Saiu e a resposta se perdeu: fica na fila (mandar de novo não estraga nada).</summary>
    NaoConfirmou,
    /// <summary>A borda ainda não está publicada: espera o deploy.</summary>
    NuvemSemRecurso,
    /// <summary>O servidor barrou este caixa (401/403): não adianta insistir.</summary>
    SemPermissao,
    /// <summary>O servidor disse que a rede não quer esta leitura agora.</summary>
    Desligado,
    /// <summary>Recusa permanente com motivo (lote malformado, por exemplo).</summary>
    Recusado,
}

/// <summary>A resposta da borda no vocabulário do caixa.</summary>
/// <param name="Guardar">chaves com desfecho: não se manda de novo.</param>
/// <param name="Repetir">chaves que o servidor ainda não conseguiu tratar (o pedido não chegou lá).</param>
public sealed record RespostaLote(DesfechoLote Desfecho, IReadOnlyList<string> Guardar,
    IReadOnlyList<string> Repetir, string? MotivoCru);

/// <summary>
/// O NOME DO ENTREGADOR, LIDO DO GESTOR DE PEDIDOS (23/09/2026, pedido do dono).
///
/// "Ter o nome do entregador de cada pedido, para conferir o print da avaliação que o cliente
/// manda." O print mostra "Gustavo S."; o iFood nos manda o identificador do entregador, nunca o
/// nome. Medido em 23/09: o evento ASSIGN_DRIVER NUNCA chegou à nossa integração (0 de 40.291
/// eventos), mas ele está guardado dentro do navegador do Gestor, em
/// <c>localStorage['order/&lt;uuid&gt;']</c>, com o nome E o mesmo identificador
/// (<c>workerExternalUuid</c>) que já chega em ARRIVED_AT_ORIGIN e COLLECTED. É esse
/// identificador que cola as duas pontas.
///
/// O QUE MORA AQUI, e por quê:
///  · achar o ASSIGN_DRIVER dentro do objeto do pedido (<see cref="DoPedido"/>), que é a única
///    regra de verdade desta leitura e a que quebra quando o iFood mexer no formato deles;
///  · a CHAVE do fato (pedido + entregador), que impede mandar duas vezes a mesma coisa;
///  · o LOTE que vai para a borda e a leitura da resposta;
///  · a lista local do que já foi aceito, e a fila para quando falta internet.
///
/// ⚠️ ISTO SÓ LÊ. Nada é escrito no Gestor, e nada aqui decide se um nome vale: quem aprende o
/// nome é o servidor, que antes confere se aquele pedido é mesmo da loja de quem mandou. O caixa
/// manda o que leu e guarda o que o servidor disse para guardar.
///
/// ⚠️ NOME DE PESSOA NÃO É SEGREDO NOSSO, MAS TAMBÉM NÃO É PARA ESPALHAR. Só sai daqui o que o
/// cliente já vê na tela dele: o primeiro nome com a inicial. Nada de telefone, endereço, token
/// ou qualquer outro campo do objeto do pedido.
/// </summary>
public static class EntregadorGestor
{
    /// <summary>A borda que recebe o lote. A mesma porta do código da raspadinha no chat.</summary>
    public const string Edge = ChatRaspadinha.Edge;

    /// <summary>
    /// A ação que a borda entende neste corpo. Do outro lado dela está
    /// <c>public.ifood_entregador_registrar</c> (SQL 49), que grava quem levou cada pedido depois
    /// de conferir se o pedido é mesmo da loja de quem mandou. A resposta dele diz, por chave, o
    /// que a ponta pode esquecer (<c>guardar</c>) e o que deve mandar de novo (<c>repetir</c>: só
    /// o pedido que ainda não chegou à integração, porque o entregador é atribuído na tela ANTES
    /// de o pedido cair aqui).
    /// </summary>
    public const string Acao = "entregadores";

    /// <summary>Tipo da linha na fila (Drenagem.TiposComHandler).</summary>
    public const string TipoNaFila = "ifood_entregador";

    /// <summary>O evento do Gestor que traz o nome. Ver o resumo da classe.</summary>
    public const string Evento = "ASSIGN_DRIVER";

    public const string OrigemCaixa = ChatRaspadinha.OrigemCaixa;

    /// <summary>Quantos fatos cabem num lote. Lote gigante é chamada que estoura o prazo.</summary>
    public const int TetoDoLote = 40;

    /// <summary>Teto do nome que viaja. O Gestor escreve "Luis R."; o resto é defeito de leitura.</summary>
    public const int TetoDoNome = 60;

    /// <summary>
    /// Quantas vezes a mesma chave pode voltar para o servidor sem ele conseguir tratá-la. O caso
    /// real é o pedido que a nossa integração ainda não recebeu: esperar é certo, esperar para
    /// sempre é uma leitura que nunca termina e uma chamada a cada varredura, para sempre.
    /// </summary>
    public const int TetoDeTentativas = 6;

    /// <summary>Quanto tempo o caixa fica quieto depois de o servidor dizer que isto está desligado.</summary>
    public static readonly TimeSpan Descanso = TimeSpan.FromHours(1);

    /// <summary>
    /// Quanto tempo um fato já enfileirado é considerado "a caminho", e por isso não entra num
    /// lote novo. Tem de ser MAIOR que o prazo do transitório da fila (6 h): assim, se a linha da
    /// fila desistir por falta de rede, a leitura seguinte traz o mesmo fato outra vez em vez de
    /// perdê-lo. Menor que isso e a cada cinco minutos nasceria um lote novo com o que já estava
    /// esperando.
    /// </summary>
    public static readonly TimeSpan EsperaDaFila = TimeSpan.FromHours(8);

    /// <summary>A chave da config local que guarda esse descanso.</summary>
    public const string ChavePausa = "entregador_gestor_pausa_ate";

    private static readonly Regex Espacos = new(@"\s+", RegexOptions.Compiled);

    // ── ACHAR O ASSIGN_DRIVER (puro) ─────────────────────────────────────────

    /// <summary>
    /// O entregador de UM pedido, a partir do objeto que o Gestor guarda em
    /// <c>localStorage['order/&lt;uuid&gt;']</c>. Devolve null quando aquele pedido ainda não tem
    /// entregador atribuído, que é o caso mais comum.
    ///
    /// TOLERANTE DE PROPÓSITO, como todo leitor do iFood: campo que falta vira null, JSON estranho
    /// vira null, e nada disso lança. Medido em 23/09 em três pedidos reais, o formato é
    /// <c>history.events.ASSIGN_DRIVER.metaData</c> com <c>workerName</c> e
    /// <c>workerExternalUuid</c>, e o <c>createdAt</c> no próprio evento. Os outros formatos aceitos
    /// aqui (events como lista, metadata em caixa baixa, reatribuição com mais de um evento) são
    /// barato de aceitar e caro de descobrir em produção.
    ///
    /// <paramref name="chaveDoStorage"/> é a chave inteira ("order/&lt;uuid&gt;") ou só o uuid: é
    /// de onde sai o pedido quando o objeto não traz o próprio id.
    /// </summary>
    public static EntregadorNoPedido? DoPedido(string? json, string? chaveDoStorage = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var raiz = doc.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object) return null;

            var evento = AcharEvento(raiz);
            if (evento is not { } e) return null;

            var meta = Objeto(e, "metaData") ?? Objeto(e, "metadata") ?? Objeto(e, "meta");
            if (meta is not { } m) return null;

            var workerId = Texto(m, "workerExternalUuid") ?? Texto(m, "workerExternalId")
                           ?? Texto(m, "workerId") ?? Texto(m, "worker_id");
            var nome = Nome(Texto(m, "workerName") ?? Texto(m, "worker_name"));
            if (workerId is null || nome is null) return null;

            var orderId = Texto(raiz, "id") ?? Texto(raiz, "orderId") ?? UuidDaChave(chaveDoStorage);
            if (orderId is null) return null;

            var detalhes = Objeto(raiz, "details");
            var pedido = Texto(raiz, "displayId")
                         ?? (detalhes is { } d ? Texto(d, "shortReference") : null);
            var merchant = Objeto(raiz, "merchant") is { } mc ? Texto(mc, "id") : null;
            merchant ??= Texto(raiz, "merchantId") ?? Texto(raiz, "restaurantId");

            return new EntregadorNoPedido(Chave(orderId, workerId), orderId, pedido, workerId, nome,
                Instante(e, m), merchant,
                Texto(m, "workerVehicleType") ?? Texto(m, "vehicleType"),
                Texto(m, "deliveryId"));
        }
        catch { return null; }
    }

    /// <summary>
    /// O evento ASSIGN_DRIVER dentro de <c>history.events</c>. Quando o pedido foi reatribuído
    /// (mais de um evento), vale o ÚLTIMO: é o entregador que de fato coletou, e é o nome que o
    /// cliente viu no fim.
    /// </summary>
    private static JsonElement? AcharEvento(JsonElement raiz)
    {
        var historico = Objeto(raiz, "history");
        if (historico is not { } h) return null;
        if (!h.TryGetProperty("events", out var eventos)) return null;

        if (eventos.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in eventos.EnumerateObject())
            {
                if (!string.Equals(p.Name, Evento, StringComparison.OrdinalIgnoreCase)) continue;
                return Ultimo(p.Value);
            }
            return null;
        }
        if (eventos.ValueKind == JsonValueKind.Array)
        {
            JsonElement? achado = null;
            foreach (var x in eventos.EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.Object) continue;
                var codigo = Texto(x, "code") ?? Texto(x, "eventCode") ?? Texto(x, "name") ?? Texto(x, "type");
                if (string.Equals(codigo, Evento, StringComparison.OrdinalIgnoreCase)) achado = x;
            }
            return achado;
        }
        return null;
    }

    /// <summary>O evento pode vir sozinho ou numa lista (reatribuição). Vale o último da lista.</summary>
    private static JsonElement? Ultimo(JsonElement valor)
    {
        if (valor.ValueKind == JsonValueKind.Object) return valor;
        if (valor.ValueKind != JsonValueKind.Array) return null;
        JsonElement? ultimo = null;
        foreach (var x in valor.EnumerateArray())
            if (x.ValueKind == JsonValueKind.Object) ultimo = x;
        return ultimo;
    }

    /// <summary>A chave do fato: ESTE pedido com ESTE entregador. Minúscula para não depender de caixa.</summary>
    public static string Chave(string orderId, string workerId)
        => (orderId.Trim() + "|" + workerId.Trim()).ToLowerInvariant();

    /// <summary>"Luis R." como o Gestor escreveu, sem espaço dobrado e com teto. Vazio vira null.</summary>
    public static string? Nome(string? bruto)
    {
        var n = Espacos.Replace(bruto ?? "", " ").Trim();
        if (n.Length == 0) return null;
        return n.Length <= TetoDoNome ? n : n[..TetoDoNome].Trim();
    }

    private static string? UuidDaChave(string? chave)
    {
        var c = (chave ?? "").Trim();
        if (c.StartsWith("order/", StringComparison.OrdinalIgnoreCase)) c = c["order/".Length..];
        return c.Length == 0 ? null : c;
    }

    private static DateTime? Instante(JsonElement evento, JsonElement meta)
    {
        foreach (var (el, campo) in new[] { (evento, "createdAt"), (evento, "created_at"), (evento, "date"),
                                            (meta, "createdAt"), (meta, "assignedAt") })
        {
            var t = Texto(el, campo);
            if (t is not null && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                return dto.LocalDateTime;
        }
        return null;
    }

    private static JsonElement? Objeto(JsonElement pai, string nome)
        => pai.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Texto(JsonElement pai, string nome)
    {
        if (!pai.TryGetProperty(nome, out var v)) return null;
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
    }

    // ── O PACOTE QUE A TELA MANDA (puro) ─────────────────────────────────────

    /// <summary>
    /// Lê o pacote do script do painel (<c>{tipo:'entregadores', itens:[{chave, bruto}]}</c>). Cada
    /// item é o valor CRU de uma chave order/&lt;uuid&gt; do localStorage do Gestor: a página não
    /// interpreta nada, só entrega, e quem procura o ASSIGN_DRIVER é o <see cref="DoPedido"/>.
    ///
    /// Pedido sem entregador some aqui, e repetido também: o mesmo entregador no mesmo pedido
    /// aparece uma vez só, com a leitura mais recente.
    /// </summary>
    /// <summary>
    /// O pacote que a página manda é dos entregadores? Olha só o COMEÇO da string.
    ///
    /// ⚠️ ISTO EXISTE PARA NÃO PARSEAR O PACOTE NA THREAD DA TELA. A mensagem do WebView2
    /// chega no Dispatcher, que é a MESMA thread de todas as janelas do caixa; este pacote é o
    /// único que carrega os objetos CRUS dos pedidos do Gestor, e um <c>JsonDocument.Parse</c>
    /// dele só para descobrir o campo "tipo" fazia o operador ver a tela parar no meio de uma
    /// venda. A página monta o objeto com <c>tipo</c> primeiro, então ele está sempre nos
    /// primeiros caracteres: comparar custa uma passada curta, parsear custa o pacote inteiro.
    /// </summary>
    public static bool EhPacote(string? txt)
    {
        if (string.IsNullOrEmpty(txt)) return false;
        const string marca = "\"tipo\":\"entregadores\"";
        var inicio = txt.AsSpan(0, Math.Min(64, txt.Length));
        return inicio.IndexOf(marca.AsSpan(), StringComparison.Ordinal) >= 0;
    }

    public static IReadOnlyList<EntregadorNoPedido> DoPacote(string? json)
    {
        var saida = new List<EntregadorNoPedido>();
        if (string.IsNullOrWhiteSpace(json)) return saida;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return saida;
            if (!r.TryGetProperty("itens", out var itens) || itens.ValueKind != JsonValueKind.Array) return saida;

            var vistos = new HashSet<string>(StringComparer.Ordinal);
            foreach (var it in itens.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;
                var bruto = Texto(it, "bruto") ?? Texto(it, "pedido");
                var chave = Texto(it, "chave");
                var e = DoPedido(bruto, chave);
                if (e is null || !vistos.Add(e.Chave)) continue;
                saida.Add(e);
                if (saida.Count >= TetoDoLote) break;
            }
        }
        catch { /* pacote malformado não derruba a tela nem vira lote pela metade */ }
        return saida;
    }

    // ── O LOTE QUE VAI (puro) ────────────────────────────────────────────────

    /// <summary>
    /// O corpo da chamada da borda, como objeto (é ele que vai para a fila e para a rede).
    ///
    /// A LOJA vai só para quem atende a rede inteira (o dono e o gerente geral): para o terminal
    /// comum o servidor usa a loja do cadastro e ignora esta. E é o servidor que confere, pedido
    /// por pedido, se aquele pedido é mesmo da loja de quem mandou.
    /// </summary>
    public static Dictionary<string, object?> ObjetoDoLote(IReadOnlyList<EntregadorNoPedido> itens,
        string? loja, string origem = OrigemCaixa)
        => new()
        {
            ["acao"] = Acao,
            ["loja"] = string.IsNullOrWhiteSpace(loja) ? null : loja!.Trim(),
            ["origem"] = origem == ChatRaspadinha.OrigemExtensao ? ChatRaspadinha.OrigemExtensao : OrigemCaixa,
            ["itens"] = itens.Take(TetoDoLote).Select(e => new Dictionary<string, object?>
            {
                ["chave"] = e.Chave,
                ["order_id"] = e.OrderId,
                ["pedido"] = e.Pedido,
                ["worker_id"] = e.WorkerId,
                ["worker_nome"] = e.Nome,
                ["quando"] = e.Quando?.ToString("o"),
                ["merchant_id"] = e.MerchantId,
                ["veiculo"] = e.Veiculo,
                ["delivery_id"] = e.DeliveryId,
            }).ToList(),
        };

    /// <summary>O mesmo lote em texto.</summary>
    public static string CorpoDoLote(IReadOnlyList<EntregadorNoPedido> itens, string? loja,
        string origem = OrigemCaixa)
        => JsonSerializer.Serialize(ObjetoDoLote(itens, loja, origem));

    /// <summary>As chaves de um corpo de lote já montado (é o que a fila tem na mão).</summary>
    public static IReadOnlyList<string> ChavesDoCorpo(string? corpo)
    {
        var saida = new List<string>();
        if (string.IsNullOrWhiteSpace(corpo)) return saida;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return saida;
            if (!doc.RootElement.TryGetProperty("itens", out var itens) || itens.ValueKind != JsonValueKind.Array)
                return saida;
            foreach (var it in itens.EnumerateArray())
                if (it.ValueKind == JsonValueKind.Object && Texto(it, "chave") is { } k) saida.Add(k);
        }
        catch { /* corpo ilegível: a fila trata como lote sem chave */ }
        return saida;
    }

    // ── O QUE VOLTA (puro) ───────────────────────────────────────────────────

    /// <summary>
    /// A resposta da borda. Status: -1 = nada saiu do caixa, 0 = saiu e ficou sem resposta.
    ///
    /// O servidor pode dizer o que GUARDAR (chaves com desfecho) e o que REPETIR (o pedido ainda
    /// não chegou à nossa integração, então o nome ainda não tem onde se prender). Quando ele não
    /// diz nada e respondeu ok, guarda-se tudo o que foi mandado: repetir para sempre um lote que
    /// o servidor aceitou seria uma chamada por varredura, para sempre.
    /// </summary>
    public static RespostaLote LerResposta(int status, string? corpo, IReadOnlyList<string> enviadas)
    {
        var nada = Array.Empty<string>();
        if (status == -1) return new RespostaLote(DesfechoLote.SemRede, nada, enviadas, "nada saiu do caixa");
        if (status == 404)
            return new RespostaLote(DesfechoLote.NuvemSemRecurso, nada, enviadas, "a borda ainda não está no ar");
        if (status is 401 or 403)
            return new RespostaLote(DesfechoLote.SemPermissao, nada, enviadas, $"HTTP {status}");
        if (status == 0 || status >= 500 || status is 408 or 425 or 429)
            return new RespostaLote(DesfechoLote.NaoConfirmou, nada, enviadas,
                status == 0 ? "sem resposta" : $"HTTP {status}");

        try
        {
            using var doc = JsonDocument.Parse(corpo ?? "");
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0) r = r[0];
            if (r.ValueKind != JsonValueKind.Object)
                return new RespostaLote(DesfechoLote.NaoConfirmou, nada, enviadas, "resposta sem objeto");

            var ok = r.TryGetProperty("ok", out var okv) && okv.ValueKind == JsonValueKind.True;
            var motivo = Texto(r, "motivo") ?? Texto(r, "erro");
            if (!ok)
            {
                // "desligado" não é recusa da leitura: é a rede dizendo que agora não. O lote sai
                // da fila e o caixa descansa; o que não foi guardado volta na próxima leitura.
                if (string.Equals(motivo, "desligado", StringComparison.OrdinalIgnoreCase))
                    return new RespostaLote(DesfechoLote.Desligado, nada, nada, motivo);
                return new RespostaLote(DesfechoLote.Recusado, nada, nada, motivo ?? "recusado");
            }

            var guardar = Lista(r, "guardar");
            var repetir = Lista(r, "repetir");
            if (guardar.Count == 0 && repetir.Count == 0) guardar = enviadas.ToList();
            return new RespostaLote(DesfechoLote.Aceito, guardar, repetir, motivo);
        }
        catch
        {
            // Corpo ilegível num 2xx: o servidor PODE ter aprendido. Não é recusa, e mandar de
            // novo não estraga nada (aprender o mesmo nome duas vezes é o mesmo nome).
            return status is >= 200 and < 300
                ? new RespostaLote(DesfechoLote.NaoConfirmou, nada, enviadas, "resposta ilegível")
                : new RespostaLote(DesfechoLote.Recusado, nada, nada, $"HTTP {status}");
        }
    }

    /// <summary>
    /// A recusa é problema de CONFIGURAÇÃO (a chave da loja, a loja que o servidor não reconhece,
    /// a leitura desligada) e não do que foi mandado? Isso não gasta as tentativas daqueles fatos:
    /// arrumada a configuração, eles ainda têm de subir.
    /// </summary>
    public static bool EhConfiguracao(string? motivo) => (motivo ?? "").Trim().ToLowerInvariant() switch
    {
        "sem_permissao" or "nao_autorizado" or "loja_desconhecida" or "sem_loja" or "desligado"
            or "http 401" or "http 403" => true,
        _ => false,
    };

    private static List<string> Lista(JsonElement r, string nome)
    {
        var saida = new List<string>();
        if (!r.TryGetProperty(nome, out var v) || v.ValueKind != JsonValueKind.Array) return saida;
        foreach (var x in v.EnumerateArray())
            if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s) saida.Add(s);
        return saida;
    }

    // ── O QUE AINDA NÃO FOI ACEITO (puro) ────────────────────────────────────

    /// <summary>O que este disco já sabe de um fato.</summary>
    /// <param name="Aceito">o servidor encerrou o assunto: nunca mais se manda.</param>
    /// <param name="Tentativas">vezes em que o servidor recebeu e ainda não conseguiu tratar.</param>
    /// <param name="EnfileiradoEm">quando entrou num lote e ainda não houve resposta.</param>
    public sealed record EstadoLocal(bool Aceito, int Tentativas, DateTime? EnfileiradoEm);

    /// <summary>
    /// O que vale mandar. Três recusas, nesta ordem, e todas com o mesmo motivo de existir:
    /// nenhuma chamada repetida à toa.
    ///  · já ACEITO pelo servidor: assunto encerrado, nunca mais;
    ///  · já esperando NA FILA: o lote anterior ainda não teve resposta (senão, a cada cinco
    ///    minutos nasceria um lote novo com o mesmo fato dentro);
    ///  · já tentado demais: o pedido que a nossa integração nunca recebeu não pode virar uma
    ///    chamada por varredura, para sempre.
    /// Separada do banco de propósito, para ser exercitada sem ele.
    /// </summary>
    public static IReadOnlyList<EntregadorNoPedido> Novos(IEnumerable<EntregadorNoPedido> itens,
        IReadOnlyDictionary<string, EstadoLocal> jaVistos, DateTime agora)
    {
        var saida = new List<EntregadorNoPedido>();
        foreach (var e in itens)
        {
            if (jaVistos.TryGetValue(e.Chave, out var ja))
            {
                if (ja.Aceito || ja.Tentativas >= TetoDeTentativas) continue;
                if (ja.EnfileiradoEm is { } q && agora - q < EsperaDaFila && agora >= q) continue;
            }
            saida.Add(e);
            if (saida.Count >= TetoDoLote) break;
        }
        return saida;
    }

    // ── A LISTA LOCAL E A FILA ───────────────────────────────────────────────

    /// <summary>O que este disco já sabe de cada fato.</summary>
    public static Dictionary<string, EstadoLocal> JaVistos(SqliteConnection cx)
    {
        var mapa = new Dictionary<string, EstadoLocal>(StringComparer.Ordinal);
        foreach (var l in cx.Query("SELECT chave, tentativas, aceito_em, enfileirado_em FROM ifood_entregador_visto"))
        {
            var t = l.tentativas is null ? 0 : (int)(long)l.tentativas;
            DateTime? fila = l.enfileirado_em is string s
                && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var q) ? q : null;
            mapa[(string)l.chave] = new EstadoLocal(l.aceito_em is not null, t, fila);
        }
        return mapa;
    }

    /// <summary>
    /// Grava o que vai ser mandado e enfileira O LOTE, na mesma transação. Devolve quantos fatos
    /// entraram (0 quando não havia nada novo: aí nada vai à rede).
    ///
    /// O lote inteiro é UMA linha da fila: é uma chamada só, e o servidor responde item a item.
    /// A linha local nasce ANTES da chamada, como a mensagem do chat: se o caixa cair no meio, a
    /// fila descobre o desfecho depois.
    /// </summary>
    public static int Registrar(SqliteConnection cx, IReadOnlyList<EntregadorNoPedido> lidos, string? loja,
        DateTime? agora = null)
    {
        if (lidos.Count == 0) return 0;
        var quando = agora ?? DateTime.Now;
        if (Pausado(cx, quando)) return 0;

        var novos = Novos(lidos, JaVistos(cx), quando);
        if (novos.Count == 0) return 0;

        var corpo = ObjetoDoLote(novos, loja);
        using var tx = cx.BeginTransaction();
        foreach (var e in novos)
            cx.Execute("""
                INSERT INTO ifood_entregador_visto
                       (chave, order_id, pedido, worker_id, nome, tentativas, criado_em, enfileirado_em)
                VALUES (@K, @O, @P, @W, @N, 0, @Em, @Em)
                ON CONFLICT(chave) DO UPDATE SET enfileirado_em = @Em
                """, new { K = e.Chave, O = e.OrderId, P = e.Pedido, W = e.WorkerId, N = e.Nome,
                           Em = quando.ToString("o") }, tx);
        var clientKey = ChaveDoLote(novos, quando);
        Caixa.Enfileirar(cx, tx, TipoNaFila, clientKey, clientKey, corpo);
        tx.Commit();
        FaxinaDeVezEmQuando(cx, quando);
        return novos.Count;
    }

    /// <summary>Mesmo que o de cima, abrindo o banco.</summary>
    public static int Registrar(IReadOnlyList<EntregadorNoPedido> lidos, string? loja, DateTime? agora = null)
    {
        using var cx = Banco.Abrir();
        return Registrar(cx, lidos, loja, agora);
    }

    /// <summary>A client_key do lote: as chaves dele e o minuto. Mesmo lote, mesma chave.</summary>
    public static string ChaveDoLote(IReadOnlyList<EntregadorNoPedido> itens, DateTime agora)
    {
        var bruto = agora.ToString("yyyyMMddHHmm") + "|" + string.Join(",", itens.Select(i => i.Chave).OrderBy(x => x, StringComparer.Ordinal));
        return "ent-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bruto)), 0, 10).ToLowerInvariant();
    }

    /// <summary>Marca as chaves que o servidor disse para guardar: elas nunca mais são mandadas.</summary>
    public static void MarcarAceitos(SqliteConnection cx, IEnumerable<string> chaves, DateTime? agora = null)
    {
        var em = (agora ?? DateTime.Now).ToString("o");
        foreach (var k in chaves)
            cx.Execute("UPDATE ifood_entregador_visto SET aceito_em = @Em WHERE chave = @K AND aceito_em IS NULL",
                new { Em = em, K = k });
    }

    /// <summary>
    /// Conta uma tentativa das chaves que o servidor recebeu mas ainda não conseguiu tratar (o
    /// caso real: o pedido ainda não chegou à integração dele). O fato sai do estado "a caminho",
    /// então a leitura seguinte pode mandá-lo de novo, até o teto de tentativas.
    /// </summary>
    public static void ContarTentativa(SqliteConnection cx, IEnumerable<string> chaves)
    {
        foreach (var k in chaves)
            cx.Execute("""
                UPDATE ifood_entregador_visto SET tentativas = tentativas + 1, enfileirado_em = NULL
                 WHERE chave = @K AND aceito_em IS NULL
                """, new { K = k });
    }

    /// <summary>A rede mandou descansar (ou a leitura está desligada lá): até quando.</summary>
    public static bool Pausado(SqliteConnection cx, DateTime agora)
    {
        var ate = Vendas.Config(cx, ChavePausa);
        return DateTime.TryParse(ate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var q)
               && agora < q;
    }

    /// <summary>Descansa uma hora. Usado quando o servidor diz que esta leitura está desligada.</summary>
    public static void Pausar(SqliteConnection cx, DateTime agora)
        => Vendas.GravarConfig(cx, ChavePausa, (agora + Descanso).ToString("o"));

    /// <summary>
    /// O que a Drenagem faz com uma linha <see cref="TipoNaFila"/>. Desfecho no contrato da fila:
    /// (true) resolvido, (false) recusa permanente, (null) transitório.
    ///
    /// Reenviar é seguro: esta chamada não entrega nada e não cria nada no caixa. Ela conta ao
    /// servidor o nome que o Gestor mostrou; aprender o mesmo nome duas vezes é o mesmo nome.
    /// </summary>
    public static async Task<(bool? Ok, string? Erro)> ResolverNaFilaAsync(string payload,
        Func<string, string, Task<(int Status, string? Corpo)>> funcao, DateTime agora)
    {
        var chaves = ChavesDoCorpo(payload);
        if (chaves.Count == 0) return (true, "lote sem itens: sai da fila sem chamada");

        using (var cx = Banco.Abrir())
            if (Pausado(cx, agora)) return (null, "leitura de entregador em descanso");

        var (st, corpo) = await funcao(Edge, payload).ConfigureAwait(false);
        var r = LerResposta(st, corpo, chaves);

        using (var cx = Banco.Abrir())
        {
            if (r.Guardar.Count > 0) MarcarAceitos(cx, r.Guardar, agora);
            if (r.Desfecho == DesfechoLote.Aceito && r.Repetir.Count > 0) ContarTentativa(cx, r.Repetir);
            // Recusa do LOTE INTEIRO por causa do QUE FOI MANDADO (lista malformada, lote grande
            // demais): a linha da fila morre aqui, mas os fatos continuam no localStorage do Gestor
            // e voltariam na leitura seguinte, para sempre. Contar a tentativa é o que dá fim a
            // esse laço.
            //
            // ⚠️ RECUSA DE CONFIGURAÇÃO NÃO CONTA (é a lição do código da raspadinha no chat): a
            // chave da loja errada e a loja que o servidor não reconhece são problema NOSSO, não
            // daquele nome. Contar tentativa ali gastaria o orçamento dos fatos e, arrumada a
            // configuração, os nomes daquele dia nunca mais seriam mandados.
            if ((r.Desfecho == DesfechoLote.Recusado || r.Desfecho == DesfechoLote.SemPermissao)
                && !EhConfiguracao(r.MotivoCru))
                ContarTentativa(cx, chaves);
            if (r.Desfecho == DesfechoLote.Desligado) Pausar(cx, agora);
        }

        return r.Desfecho switch
        {
            DesfechoLote.Aceito => (true, null),
            DesfechoLote.Desligado => (true, "a rede desligou a leitura do nome do entregador"),
            DesfechoLote.SemPermissao => (false, $"este caixa não pode mandar o nome do entregador ({r.MotivoCru})"),
            DesfechoLote.Recusado => (false, r.MotivoCru),
            DesfechoLote.NuvemSemRecurso => (null, $"a borda {Edge} ainda não está no ar"),
            _ => (null, r.MotivoCru),
        };
    }

    /// <summary>
    /// Faxina da lista local: fato aceito há muito tempo não precisa mais ocupar disco. A chave só
    /// serve para não repetir, e o pedido some do Gestor muito antes disso.
    /// </summary>
    public static readonly TimeSpan Guarda = TimeSpan.FromDays(45);

    public static int Faxina(SqliteConnection cx, DateTime agora)
        => cx.Execute("DELETE FROM ifood_entregador_visto WHERE criado_em < @Limite",
            new { Limite = (agora - Guarda).ToString("o") });

    private static DateTime _proximaFaxina = DateTime.MinValue;

    /// <summary>
    /// Uma faxina por hora, no máximo, presa à gravação. Sem isto a lista local só cresce: são
    /// dezenas de fatos por dia, e nenhuma tela mostra essa tabela para alguém lembrar dela.
    /// </summary>
    private static void FaxinaDeVezEmQuando(SqliteConnection cx, DateTime agora)
    {
        if (agora < _proximaFaxina) return;
        _proximaFaxina = agora.AddHours(1);
        try { Faxina(cx, agora); } catch { /* faxina é conforto */ }
    }
}
