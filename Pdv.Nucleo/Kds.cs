using System.Text.Json;
using Dapper;

namespace Pdv.Nucleo;

/// <summary>
/// Um pedido esperando para ser produzido, do jeito que ele aparece no monitor.
/// </summary>
public sealed record Ticket(
    string Id,
    string Origem,          // balcao | ifood | encomenda
    string RefId,
    string Numero,
    string? Cliente,
    string ItensJson,
    string Status,          // recebido | preparando | pronto | cancelado
    DateTime CriadoEm,
    DateTime? PreparoEm,
    DateTime? ProntoEm,
    DateTime? PreparoAte = null)
{
    /// <summary>Há quanto tempo esse pedido está esperando. É o que decide a cor do card.</summary>
    public TimeSpan Espera => (ProntoEm ?? DateTime.Now) - CriadoEm;

    /// <summary>Quanto falta até o prazo que o iFood prometeu (negativo = estourou).
    /// É O MESMO relógio do Gestor — pedido do dono: os dois painéis não podem
    /// contar tempos diferentes do mesmo pedido.</summary>
    public TimeSpan? PrazoRestante => PreparoAte is { } p ? p - DateTime.Now : null;

    public IReadOnlyList<TicketItem> Itens =>
        JsonSerializer.Deserialize<List<TicketItem>>(ItensJson) ?? new();
}

/// <param name="Qtd">Em milésimos, igual a venda_item.qtd_milesimo — 1000 = 1 unidade.</param>
public sealed record TicketItem(string Descricao, int Qtd, string? Observacao);

/// <summary>Um pedido de delivery como veio da nuvem (ifood_orders).</summary>
/// <param name="RecebidoEm">Chegada REAL no iFood (timestamptz ISO). O relógio do
/// card conta DAQUI, não da hora em que o PDV importou — senão pedido de 20 min
/// nasce mostrando "agora" e a cozinha prioriza errado.</param>
public sealed record PedidoDelivery(string OrderId, string Numero, string? Cliente,
                                    string ItensJson, string Status, string? RecebidoEm = null,
                                    string? PreparoAte = null);

/// <summary>
/// A fila de preparo do balcão.
///
/// Por que ela mora no SQLite local e não na nuvem: o monitor fica ao lado do
/// forno, e é exatamente quando a internet cai que a loja não pode parar de
/// produzir. O ticket nasce da venda que ACABOU de ser fechada nesta máquina —
/// não há ida à rede no caminho crítico.
///
/// Os pedidos de delivery entram por outro caminho (descem na sincronização),
/// mas viram o mesmo ticket: para quem está produzindo, um donut de balcão e um
/// donut de iFood são a mesma tarefa.
///
/// LIMITE CONHECIDO desta versão: o ticket é LOCAL. Os tempos de preparo ainda
/// não sobem para o painel — botar isso na outbox exige o outro lado saber
/// receber `tipo='kds_ticket'`, e mandar um tipo que o servidor não conhece faria
/// a fila inteira (que carrega VENDA) ficar tentando para sempre. Sync do KDS é
/// item separado, depois que o endpoint existir.
/// </summary>
public static class Kds
{
    public const string Recebido   = "recebido";
    public const string Preparando = "preparando";
    public const string Pronto     = "pronto";
    public const string Entregue   = "entregue";
    public const string Cancelado  = "cancelado";

    /// <summary>
    /// Cria o ticket de uma venda de balcão recém-fechada.
    /// Idempotente pela UNIQUE(origem, ref_id): chamar duas vezes para a mesma
    /// venda não coloca dois cards na tela de quem produz.
    /// </summary>
    public static string? DoBalcao(string vendaId)
    {
        using var cx = Banco.Abrir();

        var v = cx.QueryFirstOrDefault(
            // status = 'finalizada' e proposital: venda ABERTA nao pode mandar produzir.
            // Producao antes do pagamento e prejuizo se o cliente desistir no balcao.
            "SELECT numero_local, criada_em FROM venda WHERE id = @id AND status = 'finalizada'",
            new { id = vendaId });
        if (v is null) return null;

        var itens = cx.Query(
            @"SELECT descricao, qtd_milesimo FROM venda_item
               WHERE venda_id = @id AND cancelado = 0 ORDER BY seq", new { id = vendaId })
            .Select(i => new TicketItem((string)i.descricao, (int)(long)i.qtd_milesimo, null))
            .ToList();
        if (itens.Count == 0) return null;

        return Criar("balcao", vendaId, ((long)v.numero_local).ToString(), null, itens);
    }

    /// <summary>Cria (ou reaproveita) o ticket de um pedido de delivery.</summary>
    public static string? DoDelivery(string orderId, string numeroVisivel,
                                     string? cliente, IEnumerable<TicketItem> itens,
                                     DateTime? chegadaReal = null, DateTime? preparoAte = null)
        => Criar("ifood", orderId, numeroVisivel, cliente, itens.ToList(), chegadaReal, preparoAte);

    private static string? Criar(string origem, string refId, string numero,
                                 string? cliente, List<TicketItem> itens,
                                 DateTime? chegadaReal = null, DateTime? preparoAte = null)
    {
        if (itens.Count == 0) return null;

        using var cx = Banco.Abrir();
        var existente = cx.QueryFirstOrDefault<string>(
            "SELECT id FROM kds_ticket WHERE origem = @o AND ref_id = @r",
            new { o = origem, r = refId });
        if (existente is not null) return existente;

        var id = Guid.NewGuid().ToString();
        cx.Execute(
            @"INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json, status, criado_em, preparo_ate)
              VALUES (@id, @o, @r, @n, @c, @j, @s, @t, @pa)
              ON CONFLICT(origem, ref_id) DO NOTHING",
            new
            {
                id, o = origem, r = refId, n = numero, c = cliente,
                j = JsonSerializer.Serialize(itens), s = Recebido,
                // o relógio do card conta da CHEGADA no iFood quando conhecida
                t = (chegadaReal ?? DateTime.Now).ToString("o"),
                pa = preparoAte?.ToString("o"),
            });

        // O ON CONFLICT pode ter engolido o insert numa corrida com o polling do
        // delivery; devolve o id que REALMENTE está no banco, não o que eu gerei.
        return cx.QueryFirstOrDefault<string>(
            "SELECT id FROM kds_ticket WHERE origem = @o AND ref_id = @r",
            new { o = origem, r = refId });
    }

    /// <summary>
    /// O quadro inteiro: a preparar, em preparo e pronto aguardando coleta.
    /// Entregue e cancelado saem — quadro é presente, não histórico.
    /// </summary>
    public static List<Ticket> Abertos()
    {
        using var cx = Banco.Abrir();
        return cx.Query(
            @"SELECT * FROM kds_ticket
               WHERE status IN ('recebido','preparando','pronto')
               ORDER BY criado_em")
            .Select(Ler).ToList();
    }

    /// <summary>Quantos pedidos esperando — o número que vai no botão da barra.</summary>
    public static int Pendentes()
    {
        using var cx = Banco.Abrir();
        return cx.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM kds_ticket WHERE status IN ('recebido','preparando')");
    }

    /// <summary>Primeiro toque: alguém assumiu a produção deste pedido.</summary>
    public static bool Assumir(string ticketId) =>
        Avancar(ticketId, de: Recebido, para: Preparando, carimbo: "preparo_em");

    /// <summary>
    /// Segundo toque: saiu do forno — vai pra coluna de coleta. Pedido de
    /// DELIVERY também enfileira o aviso pra nuvem (outbox): a ponte lê o
    /// carimbo e dispara o readyToPickup no iFood. Fila, não chamada direta —
    /// internet caída no momento do toque não pode engolir o sinal.
    /// </summary>
    public static bool Liberar(string ticketId)
    {
        // UM commit para as duas coisas: virar "pronto" E enfileirar o aviso.
        // Separado (como na 1a versao), queda de energia entre os dois deixava
        // o card verde na coluna de coleta com o iFood nunca avisado - e o
        // segundo toque nao consertava, porque a transicao ja tinha acontecido.
        using var cx = Banco.Abrir();
        using var tx = cx.BeginTransaction();

        var mudou = cx.Execute("""
            UPDATE kds_ticket SET status = @para, pronto_em = @em
             WHERE id = @id AND status = @de
            """, new { id = ticketId, de = Preparando, para = Pronto,
                       em = DateTime.Now.ToString("o") }, tx) == 1;
        if (!mudou) { tx.Rollback(); return false; }

        var t = cx.QueryFirstOrDefault(
            "SELECT origem, ref_id FROM kds_ticket WHERE id = @id", new { id = ticketId }, tx);
        if (t is not null && (string)t.origem == "ifood")
        {
            // dedup por tipo+ref: liberar de novo (apos desfazer manual no banco,
            // por exemplo) nao gera segundo aviso
            cx.Execute("""
                INSERT INTO outbox (tipo, ref_id, client_key, payload, criado_em)
                SELECT 'kds_pronto', @r, 'kds_pronto:' || @r,
                       json_object('order_id', @r), @em
                WHERE NOT EXISTS (
                    SELECT 1 FROM outbox WHERE tipo = 'kds_pronto' AND ref_id = @r)
                """, new { r = (string)t.ref_id, em = DateTime.Now.ToString("o") }, tx);
        }
        tx.Commit();
        return true;
    }

    /// <summary>Terceiro toque: o entregador levou (ou o cliente retirou). Sai do quadro.</summary>
    public static bool Entregar(string ticketId) =>
        Avancar(ticketId, de: Pronto, para: Entregue, carimbo: "entregue_em");

    /// <summary>
    /// Desfazer o "assumir": o pedido volta para A PREPARAR. O carimbo de início
    /// é APAGADO — ele nunca foi preparado de verdade, e deixar a hora antiga lá
    /// faria o tempo de preparo mentir quando alguém assumir de novo.
    /// </summary>
    public static bool Desassumir(string ticketId)
    {
        using var cx = Banco.Abrir();
        return cx.Execute(
            @"UPDATE kds_ticket
                 SET status = @para, preparo_em = NULL
               WHERE id = @id AND status = @de",
            new { id = ticketId, de = Preparando, para = Recebido }) == 1;
    }

    /// <summary>
    /// A transição exige o status ANTERIOR na cláusula WHERE. Sem isso, dois
    /// toques rápidos no mesmo card (ou duas pessoas em dois monitores) reescrevem
    /// o carimbo e o tempo de preparo vira ficção.
    /// </summary>
    private static bool Avancar(string ticketId, string de, string para, string carimbo)
    {
        using var cx = Banco.Abrir();
        return cx.Execute(
            $@"UPDATE kds_ticket
                  SET status = @para, {carimbo} = @agora
                WHERE id = @id AND status = @de",
            new { id = ticketId, de, para, agora = DateTime.Now.ToString("o") }) == 1;
    }

    /// <summary>Venda cancelada não pode continuar pedindo produção.</summary>
    public static void CancelarPorVenda(string vendaId)
    {
        using var cx = Banco.Abrir();
        cx.Execute(
            @"UPDATE kds_ticket SET status = @s
               WHERE origem = 'balcao' AND ref_id = @r AND status <> 'pronto'",
            new { s = Cancelado, r = vendaId });
    }

    /// <summary>
    /// Itens do jsonb do iFood -> itens de ticket. TOLERANTE de propósito: o shape
    /// varia (name/nome, quantity/quantidade/qtd, observations/observacao/obs) e um
    /// pedido com JSON estranho precisa aparecer na tela MESMO ASSIM — pedido
    /// invisível é cliente esperando algo que ninguém está fazendo.
    /// </summary>
    public static List<TicketItem> ItensDeJson(string? itensJson)
    {
        var r = new List<TicketItem>();
        if (string.IsNullOrWhiteSpace(itensJson)) return r;
        try
        {
            using var doc = JsonDocument.Parse(itensJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return r;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                // "descricao" primeiro: e o shape REAL da ponte em producao
                // ({"qtd":1,"descricao":"Donut Homer"}), medido antes de confiar.
                var nome = Texto(e, "descricao") ?? Texto(e, "name") ?? Texto(e, "nome")
                        ?? Texto(e, "item") ?? "(item sem nome)";
                var qtd = Numero(e, "quantity") ?? Numero(e, "quantidade") ?? Numero(e, "qtd") ?? 1m;
                var obs = Texto(e, "observations") ?? Texto(e, "observacao") ?? Texto(e, "obs");
                r.Add(new TicketItem(nome, (int)Math.Round(qtd * 1000), obs));
            }
        }
        catch { /* JSON quebrado: devolve o que deu — nunca some com o pedido inteiro */ }
        return r;

        static string? Texto(JsonElement e, string k) =>
            e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            && v.GetString() is { Length: > 0 } s ? s : null;
        static decimal? Numero(JsonElement e, string k) =>
            e.TryGetProperty(k, out var v) ? v.ValueKind switch
            {
                JsonValueKind.Number => v.GetDecimal(),
                JsonValueKind.String when decimal.TryParse(v.GetString(), out var d) => d,
                _ => null,
            } : null;
    }

    /// <summary>
    /// Aplica a foto da nuvem na fila local. Idempotente: pedido repetido não
    /// duplica; cancelado na nuvem cancela aqui (menos se já saiu do forno — aí
    /// o produto existe e a divergência é problema de gente, não de tela).
    /// Devolve quantos tickets NOVOS nasceram.
    /// </summary>
    public static int SincronizarDelivery(IEnumerable<PedidoDelivery> pedidos)
    {
        // Quadro é PRESENTE, não histórico: ticket de delivery parado há mais de
        // 4h (o teto da janela do servidor) não vai mais ser preparado por
        // ninguém — expira sozinho, senão o quadro acumula card morto até
        // ninguém mais confiar no que ele mostra.
        using (var cxLimpa = Banco.Abrir())
            cxLimpa.Execute(
                @"UPDATE kds_ticket SET status = @s
                   WHERE origem = 'ifood' AND status = 'recebido'
                     AND criado_em < @limite",
                new { s = Cancelado, limite = DateTime.Now.AddHours(-4).ToString("o") });
        // SO 'recebido': expirar quem esta 'preparando' cancelaria um pedido
        // que o cozinheiro ACABOU de assumir (chegou as 3h58 do limite e foi
        // pego) - o card sumiria da tela no meio da producao.

        var novos = 0;
        foreach (var p in pedidos)
        {
            if (p.Status.Equals("cancelado", StringComparison.OrdinalIgnoreCase))
            {
                CancelarDelivery(p.OrderId);
                continue;
            }
            // A loja opera com o Gestor do iFood LADO A LADO: pedido despachado
            // ou concluído por lá tem que SAIR do quadro daqui — card pendurado
            // de pedido que já foi embora destrói a confiança na tela.
            if (p.Status.Equals("despachado", StringComparison.OrdinalIgnoreCase)
                || p.Status.Equals("concluido", StringComparison.OrdinalIgnoreCase))
            {
                DespacharDelivery(p.OrderId);
                continue;
            }
            // PRONTO no Gestor: a cozinha já terminou POR LÁ. Aqui o card pula
            // direto pra coluna de coleta — mostrar como "a preparar" era a
            // confusão gigante que o dono viu no quadro.
            if (p.Status.Equals("pronto", StringComparison.OrdinalIgnoreCase))
            {
                var itensPr = ItensDeJson(p.ItensJson);
                if (itensPr.Count > 0)
                    DoDelivery(p.OrderId, p.Numero, p.Cliente, itensPr,
                               ChegadaLocal(p.RecebidoEm), ChegadaLocal(p.PreparoAte));
                PromoverProntoDelivery(p.OrderId);
                continue;
            }
            var itens = ItensDeJson(p.ItensJson);
            if (itens.Count == 0) continue;

            using var cx = Banco.Abrir();
            var existia = cx.QueryFirstOrDefault<string>(
                "SELECT id FROM kds_ticket WHERE origem = 'ifood' AND ref_id = @r",
                new { r = p.OrderId }) is not null;
            if (!existia)
            {
                if (DoDelivery(p.OrderId, p.Numero, p.Cliente, itens,
                               ChegadaLocal(p.RecebidoEm), ChegadaLocal(p.PreparoAte)) is not null)
                    novos++;
            }
            else
            {
                // Ticket que ainda não saiu do forno acompanha a nuvem: um parser
                // corrigido (ou pedido editado no iFood) tem que consertar o card
                // na tela — sem isso, "(item sem nome)" gravado fica errado pra
                // sempre, porque a criação é idempotente de propósito.
                cx.Execute(
                    @"UPDATE kds_ticket
                         SET itens_json = @j, cliente = @c, numero = @n,
                             criado_em = coalesce(@em, criado_em),
                             preparo_ate = coalesce(@pa, preparo_ate)
                       WHERE origem = 'ifood' AND ref_id = @r
                         AND status IN ('recebido','preparando')",
                    new { j = System.Text.Json.JsonSerializer.Serialize(itens),
                          c = p.Cliente, n = p.Numero, r = p.OrderId,
                          em = ChegadaLocal(p.RecebidoEm)?.ToString("o"),
                          pa = ChegadaLocal(p.PreparoAte)?.ToString("o") });
            }
        }
        return novos;
    }

    /// <summary>timestamptz da nuvem (UTC) → hora LOCAL do balcão. Sem isto o
    /// relógio do card ganharia o fuso inteiro e tudo nasceria "atrasado".</summary>
    internal static DateTime? ChegadaLocal(string? recebidoEmIso)
    {
        if (string.IsNullOrWhiteSpace(recebidoEmIso)) return null;
        return DateTimeOffset.TryParse(recebidoEmIso, out var dto)
            ? dto.LocalDateTime : null;
    }

    /// <summary>
    /// O Gestor despachou/concluiu: o quadro larga o pedido. A preparar/em
    /// preparo vira cancelado (nunca foi produzido AQUI); PRONTO vira entregue
    /// (produzido e coletado — o tempo de preparo continua valendo).
    /// </summary>
    /// <summary>Gestor marcou pronto: recebido/preparando pulam pra coluna de
    /// coleta (a produção aconteceu do outro lado; a coleta ainda vai acontecer).</summary>
    public static void PromoverProntoDelivery(string orderId)
    {
        using var cx = Banco.Abrir();
        cx.Execute(
            @"UPDATE kds_ticket SET status = @p, pronto_em = coalesce(pronto_em, @em)
               WHERE origem = 'ifood' AND ref_id = @r AND status IN ('recebido','preparando')",
            new { p = Pronto, r = orderId, em = DateTime.Now.ToString("o") });
    }

    public static void DespacharDelivery(string orderId)
    {
        using var cx = Banco.Abrir();
        cx.Execute(
            @"UPDATE kds_ticket SET status = @e, entregue_em = @em
               WHERE origem = 'ifood' AND ref_id = @r AND status = 'pronto'",
            new { e = Entregue, r = orderId, em = DateTime.Now.ToString("o") });
        cx.Execute(
            @"UPDATE kds_ticket SET status = @c
               WHERE origem = 'ifood' AND ref_id = @r AND status IN ('recebido','preparando')",
            new { c = Cancelado, r = orderId });
    }

    public static void CancelarDelivery(string orderId)
    {
        using var cx = Banco.Abrir();
        cx.Execute(
            @"UPDATE kds_ticket SET status = @s
               WHERE origem = 'ifood' AND ref_id = @r AND status <> 'pronto'",
            new { s = Cancelado, r = orderId });
    }

    /// <summary>
    /// Aplica um status vindo da nuvem num ticket local. É o coração da
    /// reconciliação: o quadro NUNCA pode discordar do Gestor por mais que
    /// um ciclo, mesmo pra pedido velho.
    /// </summary>
    public static void AplicarStatusDaNuvem(string orderId, string status, DateTime? preparoAte)
    {
        switch (status.ToLowerInvariant())
        {
            case "cancelado": CancelarDelivery(orderId); break;
            case "despachado" or "concluido": DespacharDelivery(orderId); break;
            case "pronto": PromoverProntoDelivery(orderId); break;
        }
        if (preparoAte is { } pa)
        {
            using var cx = Banco.Abrir();
            cx.Execute(
                @"UPDATE kds_ticket SET preparo_ate = @p
                   WHERE origem = 'ifood' AND ref_id = @r AND preparo_ate IS NULL",
                new { p = pa.ToString("o"), r = orderId });
        }
    }

    /// <summary>
    /// Puxa os pedidos do dia e alimenta a fila; depois RECONCILIA os tickets
    /// abertos que ficaram FORA da janela do feed (o furo dos "12 cards de
    /// pedido já entregue": a nuvem sabia, o quadro era surdo pra pedido
    /// velho). Falha de rede é silenciosa — a fila local continua valendo.
    /// </summary>
    public static async Task<int> PuxarDaNuvemAsync(Nuvem nuvem, string loja)
    {
        var feed = await nuvem.BaixarPedidosDeliveryAsync(loja).ConfigureAwait(false);
        var novos = SincronizarDelivery(feed);

        var noFeed = feed.Select(p => p.OrderId).ToHashSet();
        List<string> orfaos;
        using (var cx = Banco.Abrir())
            orfaos = cx.Query<string>(
                @"SELECT ref_id FROM kds_ticket
                   WHERE origem = 'ifood' AND status IN ('recebido','preparando','pronto')")
                .Where(r => !noFeed.Contains(r)).Take(100).ToList();

        if (orfaos.Count > 0)
            foreach (var (id, status, prazoIso) in await nuvem.StatusPedidosAsync(orfaos).ConfigureAwait(false))
                AplicarStatusDaNuvem(id, status, ChegadaLocal(prazoIso));

        return novos;
    }

    private static Ticket Ler(dynamic r) => new(
        (string)r.id, (string)r.origem, (string)r.ref_id, (string)r.numero,
        r.cliente as string, (string)r.itens_json, (string)r.status,
        DateTime.Parse((string)r.criado_em),
        r.preparo_em is string p ? DateTime.Parse(p) : null,
        r.pronto_em  is string q ? DateTime.Parse(q) : null,
        r.preparo_ate is string pa ? DateTime.Parse(pa) : (DateTime?)null);
}
