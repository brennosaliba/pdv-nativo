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
    DateTime? PreparoAte = null,
    /// <summary>true = o CLIENTE vem buscar; false = sai com entregador.</summary>
    bool Retirada = false,
    /// <summary>O cliente marcou HORA no iFood (orderTiming = SCHEDULED). O card entra
    /// no quadro assim que a nuvem sabe dele, mas a cozinha não monta agora.</summary>
    bool Agendado = false,
    /// <summary>Início da faixa marcada, em hora LOCAL da loja.</summary>
    DateTime? AgendadoPara = null,
    /// <summary>Fim da faixa marcada (null = um instante só).</summary>
    DateTime? AgendadoAte = null)
{
    /// <summary>Há quanto tempo esse pedido está esperando. É o que decide a cor do card.</summary>
    public TimeSpan Espera => (ProntoEm ?? DateTime.Now) - CriadoEm;

    /// <summary>Quanto falta para a hora marcada (negativo = passou). Só para agendado.</summary>
    public TimeSpan? AgendadoRestante => Agendado && AgendadoPara is { } a ? a - DateTime.Now : null;

    /// <summary>Quanto falta até o prazo que o iFood prometeu (negativo = estourou).
    /// É O MESMO relógio do Gestor — pedido do dono: os dois painéis não podem
    /// contar tempos diferentes do mesmo pedido.</summary>
    public TimeSpan? PrazoRestante => PreparoAte is { } p ? p - DateTime.Now : null;

    public IReadOnlyList<TicketItem> Itens =>
        JsonSerializer.Deserialize<List<TicketItem>>(ItensJson) ?? new();
}

/// <param name="Qtd">Em milésimos, igual a venda_item.qtd_milesimo — 1000 = 1 unidade.</param>
/// <param name="Escolhas">
/// O que o cliente montou dentro de um combo ("2x Donut Ninho", "1x Cookie Duplo").
/// Sem isto a cozinha lê "1x Combo Box 4un" e não sabe o que produzir — o combo
/// vira um pedido sem conteúdo. Vazio para item simples.
/// </param>
public sealed record TicketItem(string Descricao, int Qtd, string? Observacao,
    IReadOnlyList<string>? Escolhas = null);

/// <summary>Um pedido de delivery como veio da nuvem (ifood_orders).</summary>
/// <param name="RecebidoEm">Chegada REAL no iFood (timestamptz ISO). O relógio do
/// card conta DAQUI, não da hora em que o PDV importou — senão pedido de 20 min
/// nasce mostrando "agora" e a cozinha prioriza errado.</param>
/// <param name="Agendado">O cliente marcou hora. RPC antiga não manda o campo:
/// ausência = imediato, que é o que sempre foi.</param>
/// <param name="AgendadoPara">Início da faixa marcada (timestamptz ISO).</param>
/// <param name="AgendadoAte">Fim da faixa marcada (timestamptz ISO), se houver.</param>
public sealed record PedidoDelivery(string OrderId, string Numero, string? Cliente,
                                    string ItensJson, string Status, string? RecebidoEm = null,
                                    string? PreparoAte = null, bool Retirada = false,
                                    bool Agendado = false, string? AgendadoPara = null,
                                    string? AgendadoAte = null);

/// <summary>
/// O COMPLEMENTO de um pedido de delivery, como a nuvem devolve na RPC
/// <c>pdv_kds_pedido_detalhe</c> (04/09). É o que o Gestor do iFood mostra na tela de
/// detalhe e o feed do quadro NÃO carrega: localizador, código de coleta, observação
/// do pedido inteiro e os outros pedidos agrupados na mesma entrega.
///
/// Todo campo pode vir nulo, e a tela esconde a seção quando vem. Pedido de balcão
/// e do cardápio próprio não têm linha nenhuma (a RPC devolve vazio).
/// </summary>
/// <param name="Localizador">O telefone-localizador do cliente, o mesmo que o Gestor
/// mostra ao lado de "Feito às".</param>
/// <param name="CodigoColeta">O que o entregador informa no balcão ("0807"): é o que
/// a loja confere antes de entregar a sacola ao motoboy.</param>
/// <param name="Observacoes">Observação do PEDIDO inteiro (a por item já vem no
/// <see cref="TicketItem.Observacao"/>).</param>
/// <param name="PreparoInicio">timestamptz ISO de quando a nuvem viu o preparo começar.</param>
/// <param name="OrderTiming">IMMEDIATE ou SCHEDULED, cru como veio.</param>
/// <param name="Entregador">Id OPACO do entregador (worker_id). Não é nome: NÃO vai
/// para a tela — serve só para casar pedidos do mesmo entregador no servidor.</param>
/// <param name="AgrupadoCom">Números (display_id) dos OUTROS pedidos no mesmo grupo de
/// entrega do iFood (delivery_group_id). Vazio quando o pedido vai sozinho.</param>
public sealed record DetalheNuvem(string OrderId, string? Localizador, string? CodigoColeta,
                                  string? Observacoes, string? PreparoInicio, string? OrderTiming,
                                  string? Entregador, IReadOnlyList<string> AgrupadoCom);

/// <summary>
/// O ÚLTIMO evento de entrega de um pedido, como a RPC <c>pdv_kds_entrega</c> devolve
/// (04/09): o código do iFood (ASSIGNED, GOING_TO_ORIGIN, ARRIVED_AT_ORIGIN, COLLECTED,
/// DISPATCHED...) e quando aconteceu (timestamptz ISO). Só o código vira texto no card
/// (<see cref="CardKds.TextoEntregador"/>); não há previsão em minutos aqui nem em lugar nenhum.
/// </summary>
public sealed record EventoEntrega(string OrderId, string Code, string? Em);

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
            @"SELECT descricao, qtd_milesimo, gratis_milesimo FROM venda_item
               WHERE venda_id = @id AND cancelado = 0 ORDER BY seq", new { id = vendaId })
            .Select(i => new TicketItem((string)i.descricao, (int)(long)i.qtd_milesimo,
                (long)i.gratis_milesimo > 0 ? "brinde" : null))
            .ToList();
        if (itens.Count == 0) return null;

        return Criar("balcao", vendaId, ((long)v.numero_local).ToString(), null, itens);
    }

    /// <summary>Cria (ou reaproveita) o ticket de um pedido de delivery.</summary>
    public static string? DoDelivery(string orderId, string numeroVisivel,
                                     string? cliente, IEnumerable<TicketItem> itens,
                                     DateTime? chegadaReal = null, DateTime? preparoAte = null,
                                     bool retirada = false, bool agendado = false,
                                     DateTime? agendadoPara = null, DateTime? agendadoAte = null)
        => Criar("ifood", orderId, numeroVisivel, cliente, itens.ToList(), chegadaReal, preparoAte, retirada,
                 agendado, agendadoPara, agendadoAte);

    private static string? Criar(string origem, string refId, string numero,
                                 string? cliente, List<TicketItem> itens,
                                 DateTime? chegadaReal = null, DateTime? preparoAte = null,
                                 bool retirada = false, bool agendado = false,
                                 DateTime? agendadoPara = null, DateTime? agendadoAte = null)
    {
        if (itens.Count == 0) return null;

        using var cx = Banco.Abrir();
        var existente = cx.QueryFirstOrDefault<string>(
            "SELECT id FROM kds_ticket WHERE origem = @o AND ref_id = @r",
            new { o = origem, r = refId });
        if (existente is not null) return existente;

        var id = Guid.NewGuid().ToString();
        cx.Execute(
            @"INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json, status, criado_em, preparo_ate, retirada,
                                      agendado, agendado_para, agendado_ate)
              VALUES (@id, @o, @r, @n, @c, @j, @s, @t, @pa, @ret, @ag, @ap, @aa)
              ON CONFLICT(origem, ref_id) DO NOTHING",
            new
            {
                id, o = origem, r = refId, n = numero, c = cliente,
                j = JsonSerializer.Serialize(itens), s = Recebido,
                // o relógio do card conta da CHEGADA no iFood quando conhecida
                t = (chegadaReal ?? DateTime.Now).ToString("o"),
                pa = preparoAte?.ToString("o"),
                ret = retirada ? 1 : 0,
                // agendado SEM hora não existe para o quadro: vira imediato
                ag = agendado && agendadoPara is not null ? 1 : 0,
                ap = agendado ? agendadoPara?.ToString("o") : null,
                aa = agendado ? agendadoAte?.ToString("o") : null,
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

    /// <summary>
    /// A ordem da coluna A PREPARAR: AGENDADOS primeiro, do horário mais próximo ao
    /// mais distante; depois os imediatos na ordem de chegada (a fila de sempre).
    ///
    /// No TOPO, e não no fim, porque é onde o olho da cozinha começa: o agendado que
    /// está chegando na hora fica ao lado do primeiro da fila, e o de daqui a seis
    /// horas é uma linha roxa que se pula em meio segundo. No fim da coluna ele só
    /// seria visto com rolagem, e no dia cheio nunca. Agendado é raro (o 5592 foi o
    /// primeiro que o dono viu), então o custo de empurrar a fila é pequeno.
    /// </summary>
    public static List<Ticket> OrdenarFila(IEnumerable<Ticket> tickets)
        => tickets
            .OrderBy(t => t.Agendado && t.AgendadoPara is not null ? 0 : 1)
            .ThenBy(t => t.Agendado && t.AgendadoPara is { } p ? p : t.CriadoEm)
            .ThenBy(t => t.CriadoEm)
            .ToList();

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

    // ── comanda de cozinha impressa (28/08 — pedido do dono) ────────────────
    // A comanda automática vale pro DELIVERY (origem 'ifood' cobre iFood E o
    // cardápio próprio — a nuvem entrega os dois pelo mesmo feed; o número
    // "CD-xxxx" distingue na impressão). Venda de balcão já tem o cupom dela.

    /// <summary>
    /// Tickets de delivery ainda sem comanda no papel E cuja comanda já pode sair
    /// (<see cref="ComandaPodeSair"/>): o agendado fica de fora até faltar
    /// <see cref="ChaveComandaAgendadaMin"/> minutos para a hora marcada.
    /// </summary>
    public static List<Ticket> ParaImprimir(DateTime? agora = null)
    {
        using var cx = Banco.Abrir();
        var antes = MinutosAntesDaComandaAgendada(Vendas.Config(cx, ChaveComandaAgendadaMin));
        var t0 = agora ?? DateTime.Now;
        return cx.Query(
            @"SELECT * FROM kds_ticket
               WHERE origem = 'ifood' AND impresso_em IS NULL
                 AND status IN ('recebido','preparando','pronto')
               ORDER BY criado_em")
            .Select(Ler)
            .Where(t => ComandaPodeSair(t, t0, antes))
            .ToList();
    }

    // ── comanda do pedido AGENDADO (04/09 — relato do 5592) ────────────────
    // O cliente marcou hora. Imprimir na chegada seria papel na bancada às 08:00
    // para um pedido das 18:00: ninguém monta agora e a comanda se perde antes da
    // hora. A regra: sai sozinha quando faltar X minutos (padrão 30, config
    // `kds_comanda_agendado_min`) ou quando alguém tocar no 🖨 do card. O timer
    // de 10 s do quadro (e o de 60 s do caixa) reavaliam a cada puxada, então o
    // papel sai no primeiro ciclo depois do limiar. O claim (impresso_em) só é
    // feito quando a comanda de fato sai: até lá o ticket continua "sem papel".

    /// <summary>Config: quantos minutos ANTES da hora marcada a comanda do agendado sai sozinha.</summary>
    public const string ChaveComandaAgendadaMin = "kds_comanda_agendado_min";
    public const int ComandaAgendadaMinPadrao = 30;

    /// <summary>Lê a config; ausente ou lixo = 30. Teto de 12 h: acima disso é "na chegada".</summary>
    public static int MinutosAntesDaComandaAgendada(string? valorConfig)
        => int.TryParse((valorConfig ?? "").Trim(), out var m) ? Math.Clamp(m, 0, 720) : ComandaAgendadaMinPadrao;

    /// <summary>
    /// A comanda deste ticket já pode sair sozinha? Imediato: sempre. Agendado:
    /// só quando faltar <paramref name="minutosAntes"/> ou menos para a hora
    /// marcada (ou ela já passou). O 🖨 do card NÃO passa por aqui: dedo humano
    /// imprime quando quiser.
    /// </summary>
    public static bool ComandaPodeSair(Ticket t, DateTime agora, int minutosAntes)
        => !t.Agendado || t.AgendadoPara is not { } para
           || para - agora <= TimeSpan.FromMinutes(minutosAntes);

    /// <summary>
    /// "10:00", "10:00 a 10:30" ou, quando não é hoje, "05/09 10:00". Um lugar só
    /// para o card, a comanda e o aviso dizerem a mesma coisa.
    /// </summary>
    public static string TextoHorario(DateTime para, DateTime? ate, DateTime hoje)
    {
        var dia = para.Date == hoje.Date ? "" : para.ToString("dd/MM") + " ";
        var faixa = ate is { } a && a > para ? $" a {a:HH:mm}" : "";
        return $"{dia}{para:HH:mm}{faixa}";
    }

    /// <summary>
    /// Reivindica a impressão ANTES de mandar pro papel. Atômico: o timer de
    /// 10s e o sino se sobrepõem, e sem isto o mesmo pedido sairia duas vezes.
    /// Se a impressora falhar DEPOIS do claim, o pedido NÃO volta pra fila
    /// sozinho (impressora morta viraria metralhadora de tentativas a cada
    /// 10s) — o botão de reimprimir no card é o caminho de recuperação.
    /// </summary>
    public static bool ReivindicarImpressao(string ticketId)
    {
        using var cx = Banco.Abrir();
        return cx.Execute(
            @"UPDATE kds_ticket SET impresso_em = @em
               WHERE id = @id AND impresso_em IS NULL",
            new { id = ticketId, em = DateTime.Now.ToString("o") }) == 1;
    }

    /// <summary>
    /// Largura em que a comanda sempre foi montada: 40 colunas, folgadas dentro das 48
    /// da bobina de 80 mm — a folga é o que dá espaço às linhas ampliadas (o número do
    /// pedido sai em 2x). É o TETO, não a medida: numa bobina mais estreita quem manda
    /// é o papel (ver <see cref="ColunasComanda"/>).
    /// </summary>
    public const int ColunasPadrao = 40;

    /// <summary>
    /// Quantas colunas a comanda pode usar numa bobina de <paramref name="colunasDoPapel"/>.
    ///
    /// Existe desde 29/08, quando a comanda do delivery ganhou impressora e LARGURA
    /// próprias: com as 40 colunas fixas no código, a comanda mandada para a térmica de
    /// 58 mm da expedição (32 colunas) saía cortada no fim da linha — e é lá que fica a
    /// quantidade do item. Bobina mais larga que 80 mm não estica a comanda de propósito:
    /// o layout foi desenhado para 40 colunas e esticar só afastaria o item do quadradinho.
    /// </summary>
    public static int ColunasComanda(int colunasDoPapel)
        => Math.Clamp(Math.Min(ColunasPadrao, colunasDoPapel), 20, ColunasPadrao);

    /// <summary>
    /// A comanda em texto monoespaçado, no contrato de <c>Impressao.ImprimirTextoAsync</c>.
    /// Número GRANDE não existe em texto puro — o destaque vem de moldura e respiro.
    /// Observação por item sai indentada logo abaixo do item: é a instrução da cozinha
    /// ("sem granulado"), perder isso no papel é refazer donut.
    /// </summary>
    /// <param name="colunas">
    /// Largura da bobina em caracteres — passe <see cref="ColunasComanda"/> da bobina que
    /// a comanda vai usar. O padrão mantém as 40 colunas de 80 mm, que é o que a loja
    /// imprime desde sempre: quem chama sem escolher não vê diferença nenhuma no papel.
    /// </param>
    /// <param name="hoje">
    /// O "hoje" de quem imprime: decide se a hora marcada do agendado sai com a data.
    /// Só os testes cravam; a operação usa o relógio.
    /// </param>
    public static IReadOnlyList<string> ComandaLinhas(Ticket t, int colunas = ColunasPadrao, DateTime? hoje = null)
    {
        var L = ColunasComanda(colunas);
        var eCardapio = t.Numero.StartsWith("CD-", StringComparison.OrdinalIgnoreCase);
        var linhas = new List<string>
        {
            new string('=', L),
            Esc(Centro("COMANDA DE COZINHA", L), 1.2),
            Centro(eCardapio ? "CARDAPIO WEB" : "iFOOD", L),
            new string('=', L),
            Esc(Centro($"PEDIDO  #{t.Numero}", L), 2.0),
        };
        // AGENDADO logo abaixo do número, grande: quem pega o papel tem que saber
        // ANTES de ler o item que este não é para agora.
        if (t.Agendado && t.AgendadoPara is { } marcado)
            linhas.Add(Esc(Centro("AGENDADO para " + TextoHorario(marcado, t.AgendadoAte, hoje ?? DateTime.Now), L), 1.5));
        linhas.Add("");
        if (t.Cliente is { Length: > 0 })
            linhas.Add(Corta("Cliente: " + t.Cliente, L));
        // "Impresso" saiu (28/08): o que a cozinha usa é a hora que o pedido CHEGOU,
        // e duas horas na mesma linha só competiam pela atenção.
        linhas.Add($"Chegou: {t.CriadoEm:HH:mm}");
        linhas.Add(new string('-', L));
        foreach (var i in t.Itens)
        {
            var qtd = i.Qtd % 1000 == 0 ? (i.Qtd / 1000).ToString() : (i.Qtd / 1000m).ToString("0.###");
            // Quadradinho pra conferência: quem monta risca item a item antes de
            // fechar a sacola. É o que evita pedido sair faltando uma unidade.
            linhas.Add(Esc(Corta($"[ ] {qtd}x {i.Descricao}", L), 1.5));
            // O QUE o cliente montou dentro do combo. Sem estas linhas a cozinha
            // lê "1x Combo Box 4un" e não tem o que produzir.
            // Quadradinho no SABOR tambem: quem monta a caixa confere donut a
            // donut, nao "o combo" — item so no pai deixa a conferencia pela
            // metade justamente onde ela importa (combo de 4 sabores).
            if (i.Escolhas is { Count: > 0 })
                foreach (var esc in i.Escolhas)
                {
                    var primeira = true;
                    foreach (var parte in Quebra(esc, L - 8))
                    {
                        linhas.Add(Esc("    " + (primeira ? "[ ] " : "    ") + parte, 1.2));
                        primeira = false;
                    }
                }
            if (i.Observacao is { Length: > 0 })
                foreach (var parte in Quebra(">> " + i.Observacao, L - 6))
                    linhas.Add(Esc("      " + parte, 1.3));
        }
        linhas.Add(new string('-', L));
        linhas.Add("");
        return linhas;

        static string Esc(string s, double escala) => LinhaEscala.Com(s, escala);

        static string Centro(string s, int larg) =>
            s.Length >= larg ? s[..larg] : s.PadLeft((larg + s.Length) / 2).PadRight(larg);
        static string Corta(string s, int larg) => s.Length <= larg ? s : s[..(larg - 1)] + "…";
        static IEnumerable<string> Quebra(string s, int larg)
        {
            for (var i = 0; i < s.Length; i += larg)
                yield return s.Substring(i, Math.Min(larg, s.Length - i));
        }
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
                r.Add(new TicketItem(nome, (int)Math.Round(qtd * 1000), obs, Escolhas(e)));
            }
        }
        catch { /* JSON quebrado: devolve o que deu — nunca some com o pedido inteiro */ }
        return r;

        // Escolhas do combo. TOLERANTE como o resto: o cardápio manda "escolhas"
        // (monta_itens_v2) e o iFood manda "options"/"subItems"/"complements" —
        // qualquer um deles é conteúdo que a cozinha PRECISA ver na comanda.
        static List<string>? Escolhas(JsonElement e)
        {
            foreach (var chave in new[] { "escolhas", "options", "subItems", "complements", "opcoes" })
            {
                if (!e.TryGetProperty(chave, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                var saida = new List<string>();
                foreach (var o in arr.EnumerateArray())
                {
                    if (o.ValueKind == JsonValueKind.String)
                    {
                        if (o.GetString() is { Length: > 0 } sx) saida.Add(sx);
                        continue;
                    }
                    if (o.ValueKind != JsonValueKind.Object) continue;
                    var nomeEsc = Texto(o, "nome") ?? Texto(o, "name") ?? Texto(o, "descricao");
                    if (nomeEsc is null) continue;
                    var q = Numero(o, "qtd") ?? Numero(o, "quantity") ?? Numero(o, "quantidade") ?? 1m;
                    // grupo na frente ("Clássicos: 2x Donut Ninho") só quando existe:
                    // combo de um grupo só não ganha ruído.
                    var grupo = Texto(o, "grupo_nome") ?? Texto(o, "groupName");
                    var prefixo = grupo is { Length: > 0 } ? grupo + ": " : "";
                    saida.Add($"{prefixo}{(q % 1 == 0 ? ((int)q).ToString() : q.ToString("0.###"))}x {nomeEsc}");
                }
                if (saida.Count > 0) return saida;
            }
            return null;
        }

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
        {
            // AGENDADO conta da hora MARCADA (fim da faixa), não da chegada: ele
            // entra no quadro de manhã para as 18:00 e não pode "expirar" às 13:00.
            cxLimpa.Execute(
                @"UPDATE kds_ticket SET status = @s
                   WHERE origem = 'ifood' AND status = 'recebido'
                     AND CASE WHEN agendado = 1 THEN coalesce(agendado_ate, agendado_para, criado_em)
                              ELSE criado_em END < @limite",
                new { s = Cancelado, limite = DateTime.Now.AddHours(-4).ToString("o") });
            // SO 'recebido' nas 4h: expirar quem esta 'preparando' cancelaria um
            // pedido que o cozinheiro ACABOU de assumir (chegou as 3h58 do limite e
            // foi pego) - o card sumiria da tela no meio da producao.

            // MAS 'preparando'/'pronto' precisavam de um teto proprio, senao NUNCA
            // expiravam: um pedido que chegou a 'pronto' ficava no quadro para
            // sempre. Aconteceu de verdade - card de 22/08 ainda no quadro em 28/08,
            // ja cancelado no iFood, com "+9847 min" na tela. Card morto que nao sai
            // faz o operador parar de confiar no quadro inteiro, que e pior do que
            // nao ter quadro. 12h e folgado para qualquer preparo real e mata o
            // fantasma no primeiro sync do dia seguinte.
            cxLimpa.Execute(
                @"UPDATE kds_ticket SET status = @s
                   WHERE origem = 'ifood' AND status IN ('preparando','pronto')
                     AND CASE WHEN agendado = 1 THEN coalesce(agendado_ate, agendado_para, criado_em)
                              ELSE criado_em END < @limite",
                new { s = Cancelado, limite = DateTime.Now.AddHours(-12).ToString("o") });
        }

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
                               ChegadaLocal(p.RecebidoEm), ChegadaLocal(p.PreparoAte), p.Retirada,
                               p.Agendado, ChegadaLocal(p.AgendadoPara), ChegadaLocal(p.AgendadoAte));
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
                               ChegadaLocal(p.RecebidoEm), ChegadaLocal(p.PreparoAte), p.Retirada,
                               p.Agendado, ChegadaLocal(p.AgendadoPara), ChegadaLocal(p.AgendadoAte)) is not null)
                    novos++;
            }
            else
            {
                // Ticket que ainda não saiu do forno acompanha a nuvem: um parser
                // corrigido (ou pedido editado no iFood) tem que consertar o card
                // na tela — sem isso, "(item sem nome)" gravado fica errado pra
                // sempre, porque a criação é idempotente de propósito.
                // O agendamento segue a nuvem SEM coalesce: remarcar a hora (ou a
                // RPC deixar de dizer agendado) tem que refletir no card.
                var agPara = p.Agendado ? ChegadaLocal(p.AgendadoPara) : null;
                cx.Execute(
                    @"UPDATE kds_ticket
                         SET itens_json = @j, cliente = @c, numero = @n,
                             criado_em = coalesce(@em, criado_em),
                             preparo_ate = coalesce(@pa, preparo_ate),
                             agendado = @ag, agendado_para = @ap, agendado_ate = @aa
                       WHERE origem = 'ifood' AND ref_id = @r
                         AND status IN ('recebido','preparando')",
                    new { j = System.Text.Json.JsonSerializer.Serialize(itens),
                          c = p.Cliente, n = p.Numero, r = p.OrderId,
                          em = ChegadaLocal(p.RecebidoEm)?.ToString("o"),
                          pa = ChegadaLocal(p.PreparoAte)?.ToString("o"),
                          ag = agPara is not null ? 1 : 0,
                          ap = agPara?.ToString("o"),
                          aa = agPara is not null ? ChegadaLocal(p.AgendadoAte)?.ToString("o") : null });
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

        // Na MESMA puxada, sem timer novo: o entregador dos pedidos de entrega abertos.
        await AtualizarEntregasAsync(nuvem).ConfigureAwait(false);

        return novos;
    }

    // ── O ENTREGADOR (04/09, foto do dono) ──────────────────────────────────
    // A foto dos últimos eventos de entrega fica em MEMÓRIA, não no SQLite: é estado do
    // mundo lá fora, muda a cada puxada e não vale nada depois que o card sai do quadro.
    // Coluna no banco só carregaria um dado que vence em 10 s.
    //
    // A foto é trocada INTEIRA a cada resposta da nuvem. Resposta nenhuma (rede, RPC
    // fora do ar, JSON ilegível) mantém a anterior: o entregador não volta de "chegou"
    // para "a caminho", e uma linha que pisca a cada soluço de wi-fi ensina a cozinha a
    // ignorá-la. Resposta VAZIA é resposta: ninguém tem evento, a foto esvazia.

    private static volatile IReadOnlyDictionary<string, EventoEntrega> _entregas =
        new Dictionary<string, EventoEntrega>(StringComparer.Ordinal);

    /// <summary>O último evento de entrega conhecido do pedido, ou null quando não há o que dizer.</summary>
    public static EventoEntrega? EntregaDe(string orderId)
        => _entregas.TryGetValue(orderId, out var e) ? e : null;

    /// <summary>
    /// Substitui a foto pelos eventos que a nuvem mandou. Um por pedido: a RPC já manda
    /// só o mais novo, e se mandar dois o primeiro vale (ela ordena do mais novo para o
    /// mais velho). O modo --foto-kds semeia por aqui.
    /// </summary>
    public static void AplicarEntregas(IEnumerable<EventoEntrega> eventos)
    {
        var nova = new Dictionary<string, EventoEntrega>(StringComparer.Ordinal);
        foreach (var e in eventos)
            if (e.OrderId is { Length: > 0 } && !nova.ContainsKey(e.OrderId)) nova[e.OrderId] = e;
        _entregas = nova;
    }

    /// <summary>
    /// Pergunta à nuvem o último evento dos pedidos de ENTREGA abertos no quadro (retirada
    /// não tem entregador; balcão não tem nuvem). Sem pedido aberto não consulta nada e
    /// esvazia a foto. Falha mantém a foto anterior (ver o bloco acima).
    /// </summary>
    public static async Task AtualizarEntregasAsync(Nuvem nuvem)
    {
        List<string> ids;
        using (var cx = Banco.Abrir())
            ids = cx.Query<string>(
                @"SELECT ref_id FROM kds_ticket
                   WHERE origem = 'ifood' AND retirada = 0
                     AND status IN ('recebido','preparando','pronto')")
                .Take(100).ToList();
        if (ids.Count == 0) { AplicarEntregas(Array.Empty<EventoEntrega>()); return; }

        var eventos = await nuvem.EntregaPedidosAsync(ids).ConfigureAwait(false);
        if (eventos is not null) AplicarEntregas(eventos);
    }

    private static Ticket Ler(dynamic r) => new(
        (string)r.id, (string)r.origem, (string)r.ref_id, (string)r.numero,
        r.cliente as string, (string)r.itens_json, (string)r.status,
        DateTime.Parse((string)r.criado_em),
        r.preparo_em is string p ? DateTime.Parse(p) : null,
        r.pronto_em  is string q ? DateTime.Parse(q) : null,
        r.preparo_ate is string pa ? DateTime.Parse(pa) : (DateTime?)null,
        // coluna nova: banco antigo nao tem, e a leitura tolera (null = entrega)
        r.retirada is long rt && rt == 1,
        // agendado (04/09): idem, ausencia = imediato
        r.agendado is long ag && ag == 1,
        r.agendado_para is string ap ? DateTime.Parse(ap) : (DateTime?)null,
        r.agendado_ate  is string aa ? DateTime.Parse(aa) : (DateTime?)null);
}
