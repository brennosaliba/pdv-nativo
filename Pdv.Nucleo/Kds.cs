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
    DateTime? ProntoEm)
{
    /// <summary>Há quanto tempo esse pedido está esperando. É o que decide a cor do card.</summary>
    public TimeSpan Espera => (ProntoEm ?? DateTime.Now) - CriadoEm;

    public IReadOnlyList<TicketItem> Itens =>
        JsonSerializer.Deserialize<List<TicketItem>>(ItensJson) ?? new();
}

/// <param name="Qtd">Em milésimos, igual a venda_item.qtd_milesimo — 1000 = 1 unidade.</param>
public sealed record TicketItem(string Descricao, int Qtd, string? Observacao);

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
                                     string? cliente, IEnumerable<TicketItem> itens)
        => Criar("ifood", orderId, numeroVisivel, cliente, itens.ToList());

    private static string? Criar(string origem, string refId, string numero,
                                 string? cliente, List<TicketItem> itens)
    {
        if (itens.Count == 0) return null;

        using var cx = Banco.Abrir();
        var existente = cx.QueryFirstOrDefault<string>(
            "SELECT id FROM kds_ticket WHERE origem = @o AND ref_id = @r",
            new { o = origem, r = refId });
        if (existente is not null) return existente;

        var id = Guid.NewGuid().ToString();
        cx.Execute(
            @"INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json, status, criado_em)
              VALUES (@id, @o, @r, @n, @c, @j, @s, @t)
              ON CONFLICT(origem, ref_id) DO NOTHING",
            new
            {
                id, o = origem, r = refId, n = numero, c = cliente,
                j = JsonSerializer.Serialize(itens), s = Recebido,
                t = DateTime.Now.ToString("o"),
            });

        // O ON CONFLICT pode ter engolido o insert numa corrida com o polling do
        // delivery; devolve o id que REALMENTE está no banco, não o que eu gerei.
        return cx.QueryFirstOrDefault<string>(
            "SELECT id FROM kds_ticket WHERE origem = @o AND ref_id = @r",
            new { o = origem, r = refId });
    }

    /// <summary>O que está na tela agora: o que chegou e o que está no forno.</summary>
    public static List<Ticket> Abertos()
    {
        using var cx = Banco.Abrir();
        return cx.Query(
            @"SELECT * FROM kds_ticket
               WHERE status IN ('recebido','preparando')
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

    /// <summary>Segundo toque: saiu do forno, pode entregar.</summary>
    public static bool Liberar(string ticketId) =>
        Avancar(ticketId, de: Preparando, para: Pronto, carimbo: "pronto_em");

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

    private static Ticket Ler(dynamic r) => new(
        (string)r.id, (string)r.origem, (string)r.ref_id, (string)r.numero,
        r.cliente as string, (string)r.itens_json, (string)r.status,
        DateTime.Parse((string)r.criado_em),
        r.preparo_em is string p ? DateTime.Parse(p) : null,
        r.pronto_em  is string q ? DateTime.Parse(q) : null);
}
