using System.Globalization;
using System.Text;
using Dapper;

namespace Pdv.Nucleo;

/// <summary>Os três recortes da lista dos que já saíram, pelo DIA DE OPERAÇÃO em que o pedido CHEGOU.</summary>
public enum PeriodoFinalizados { Hoje, Ontem, SeteDias }

/// <summary>O que a lista diz de cada pedido que saiu do quadro.</summary>
public enum EstadoFinalizado
{
    /// <summary>Saiu com o entregador (balcão tocou, Gestor despachou ou concluiu, a nuvem confirmou).</summary>
    Saiu,
    /// <summary>O mesmo, em pedido de retirada ou de balcão: o cliente levou.</summary>
    Retirado,
    /// <summary>Cancelamento de verdade: o iFood cancelou (até depois de despachado), ou a venda do balcão foi cancelada.</summary>
    Cancelado,
    /// <summary>Saiu sem que este caixa saiba como (expirou, ou saiu antes desta versão).</summary>
    SaiuDoQuadro,
}

/// <summary>Uma linha da lista, com todo texto já pronto.</summary>
/// <param name="Numero">"#4719", com a cerquilha.</param>
/// <param name="Origem">"iFOOD", "CARDÁPIO" ou "BALCÃO".</param>
/// <param name="Cliente">Nome do cliente; null quando o pedido não trouxe.</param>
/// <param name="Resumo">"2× Donut Homer, 1× Cookie Red Velvet": a tela corta com reticências.</param>
/// <param name="Chegou">"Chegou 14:05" ("ontem 14:05", "12/10 14:05").</param>
/// <param name="Saiu">"Saiu 14:32" ou "Cancelado 14:32"; null quando a hora não é conhecida.</param>
public sealed record LinhaFinalizado(Ticket Ticket, EstadoFinalizado Estado, string Rotulo,
                                     string Numero, string Origem, string? Cliente, string Resumo,
                                     string Chegou, string? Saiu);

/// <summary>A lista montada: no máximo <see cref="FinalizadosKds.Teto"/> linhas, e quantas havia.</summary>
public sealed record ListaFinalizados(IReadOnlyList<LinhaFinalizado> Linhas, int Total)
{
    /// <summary>Havia mais do que o teto: a tela diz que mostra só os mais novos.</summary>
    public bool Cortada => Total > Linhas.Count;
}

/// <summary>
/// OS PEDIDOS QUE JÁ SAÍRAM DO QUADRO (14/10/2026, pedido do dono na Savassi: "opção também
/// de procurar pedidos finalizados no KDS do PDV"). Pedido que saiu, foi concluído ou
/// cancelado sumia do quadro e não havia onde achar.
///
/// A FONTE é o banco DESTE caixa (kds_ticket), que guarda todo pedido que já passou por ele
/// e não apaga nunca. Funciona sem internet, e não precisa de nada novo no ERP. O limite é
/// honesto: só aparece o que este caixa viu, e da loja em que ele está.
///
/// O CUIDADO PRINCIPAL é o rótulo. O status local 'cancelado' junta quatro histórias (o
/// iFood cancelou, o Gestor despachou antes de o card andar aqui, expirou, venda cancelada).
/// Por isso a tela nunca mostra o status cru: mostra <see cref="Estado"/>, que lê o MOTIVO
/// anotado na saída (encerrado_em, encerrado_motivo) e o CANCELAMENTO, anotado à parte
/// (cancelado_certo, cancelado_em).
///
/// O CANCELAMENTO DEPOIS DA SAÍDA (revisão de 14/10): o feed e a pdv_kds_status devolvem o
/// estado da ENTREGA antes do status do pedido. Medido em produção: na Savassi, de 63 pedidos
/// cancelados em 30 dias, 59 chegavam ao caixa como despachado, concluído ou pronto; no
/// Castelo, 16 de 16. O caixa pergunta à parte (Nuvem.CanceladosAsync): ao abrir a lista e,
/// a cada 2 min, pelos que saíram nas últimas 12 h.
///
/// Lógica pura onde dá (período, busca, ordem, teto, rótulos, textos), para a suíte provar;
/// as leituras do SQLite ficam no fim, finas.
/// </summary>
public static class FinalizadosKds
{
    /// <summary>Teto da lista: "Hoje" cabe inteiro (até uns 250 na Savassi); 7 dias passa de mil.</summary>
    public const int Teto = 300;

    // ── POR QUE O PEDIDO SAIU (coluna encerrado_motivo) ─────────────────────
    /// <summary>O balcão tocou "cliente levou" no card PRONTO.</summary>
    public const string MotivoClienteLevou = "cliente_levou";
    /// <summary>O Gestor do iFood despachou ou concluiu (feed ou reconciliação).</summary>
    public const string MotivoGestor = "gestor";
    /// <summary>O iFood cancelou o pedido enquanto ele estava no quadro.</summary>
    public const string MotivoIfoodCancelou = "ifood_cancelou";
    /// <summary>Ficou parado além do teto (4 h na fila, 12 h fazendo ou pronto) e o quadro largou.</summary>
    public const string MotivoExpirou = "expirou";
    /// <summary>A venda de balcão foi cancelada.</summary>
    public const string MotivoVendaCancelada = "venda_cancelada";
    /// <summary>O cardápio próprio mandou "cancelado", que ali quer dizer qualquer estado
    /// que o quadro não conhece (inclusive "coletado"): não prova cancelamento.</summary>
    public const string MotivoCardapioLargou = "cardapio_largou";
    /// <summary>A nuvem confirmou depois, ao abrir a lista, que o pedido foi concluído.</summary>
    public const string MotivoNuvemConcluiu = "nuvem_concluiu";

    /// <summary>Motivo que é "não sei": a nuvem ou um cancelamento certo podem trocá-lo.</summary>
    public static bool MotivoIncerto(string? motivo)
        => motivo is null || motivo == MotivoExpirou || motivo == MotivoCardapioLargou;

    /// <summary>O status de quem já saiu do quadro.</summary>
    public static bool Saiu(string? status) => status is Kds.Entregue or Kds.Cancelado;

    /// <summary>Pedido do cardápio próprio (número "CD-"): ali "cancelado" não prova cancelamento.</summary>
    public static bool ECardapio(Ticket t) => t.Numero.StartsWith("CD-", StringComparison.OrdinalIgnoreCase);

    // ── O RÓTULO DE CADA LINHA ──────────────────────────────────────────────

    /// <summary>
    /// O estado que a lista MOSTRA. Nunca o status cru: 'cancelado' sem motivo certo não
    /// vira CANCELADO na tela, porque quase nunca é. E o cancelamento certo ganha de tudo,
    /// até da saída: pedido despachado e depois cancelado pelo iFood é CANCELADO.
    /// </summary>
    public static EstadoFinalizado Estado(Ticket t)
    {
        if (t.CanceladoCerto) return EstadoFinalizado.Cancelado;
        var levou = t.Origem != "ifood" || t.Retirada;
        var saiu = levou ? EstadoFinalizado.Retirado : EstadoFinalizado.Saiu;
        if (t.Status == Kds.Entregue) return saiu;
        return t.EncerradoMotivo switch
        {
            MotivoGestor or MotivoNuvemConcluiu or MotivoClienteLevou => saiu,
            MotivoIfoodCancelou or MotivoVendaCancelada => EstadoFinalizado.Cancelado,
            _ => EstadoFinalizado.SaiuDoQuadro,
        };
    }

    public static string Rotulo(EstadoFinalizado e) => e switch
    {
        EstadoFinalizado.Saiu => "SAIU",
        EstadoFinalizado.Retirado => "RETIRADO",
        EstadoFinalizado.Cancelado => "CANCELADO",
        _ => "SAIU DO QUADRO",
    };

    /// <summary>A cor do rótulo (chaves do tema): texto, fundo e borda.</summary>
    public static (string Cor, string Fundo, string Borda) CoresDoRotulo(EstadoFinalizado e) => e switch
    {
        EstadoFinalizado.Saiu or EstadoFinalizado.Retirado => ("Ok", "ChipOkFundo", "ChipOkBorda"),
        EstadoFinalizado.Cancelado => ("Erro", "ChipErroFundo", "ChipErroBorda"),
        _ => ("TextoEspera", "EsperaFundo", "Borda"),
    };

    /// <summary>
    /// A hora em que o pedido SAIU DE VERDADE: o balcão tocou "levou", ou o Gestor despachou
    /// ou concluiu com o card aqui. Expirou, o cardápio largou ou a nuvem confirmou depois: a
    /// hora anotada é a de o QUADRO largar o card, não a da saída, e a tela não a mostra (era
    /// "Saiu 21:27" de pedido que saiu de manhã). Null = não se sabe.
    /// </summary>
    public static DateTime? HoraDaSaida(Ticket t)
        => t.Status == Kds.Entregue ? t.EncerradoEm ?? t.EntregueEm
         : t.EncerradoMotivo is MotivoGestor or MotivoClienteLevou ? t.EncerradoEm
         : null;

    /// <summary>A régua da ordem: a saída de verdade, o cancelamento, ou o último carimbo real do card.</summary>
    public static DateTime Ordem(Ticket t)
        => HoraDaSaida(t) ?? t.CanceladoEm ?? t.ProntoEm ?? t.PreparoEm ?? t.CriadoEm;

    // ── PERÍODO ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A virada do dia da lista: 05:00, a MESMA do turno do caixa (<see cref="Caixa.DiaOperacional"/>).
    /// O pedido que chegou às 23:40 e saiu às 00:10 continua em "Hoje" até as 05:00 (o Castelo
    /// recebe pedido até perto da meia-noite).
    /// </summary>
    public const int HoraDaVirada = 5;

    /// <summary>Quando começou o dia de operação de <paramref name="agora"/>.</summary>
    public static DateTime InicioDoDia(DateTime agora)
        => (agora.Hour < HoraDaVirada ? agora.Date.AddDays(-1) : agora.Date).AddHours(HoraDaVirada);

    /// <summary>[desde, até) pela CHEGADA no dia de operação: hoje, ontem, ou hoje e os seis dias antes.</summary>
    public static (DateTime Desde, DateTime Ate) Janela(PeriodoFinalizados p, DateTime agora)
    {
        var hoje = InicioDoDia(agora);
        return p switch
        {
            PeriodoFinalizados.Ontem => (hoje.AddDays(-1), hoje),
            PeriodoFinalizados.SeteDias => (hoje.AddDays(-6), hoje.AddDays(1)),
            _ => (hoje, hoje.AddDays(1)),
        };
    }

    public static bool NoPeriodo(Ticket t, PeriodoFinalizados p, DateTime agora)
    {
        var (desde, ate) = Janela(p, agora);
        return t.CriadoEm >= desde && t.CriadoEm < ate;
    }

    // ── BUSCA ───────────────────────────────────────────────────────────────

    /// <summary>Minúsculas, sem acento e com os espaços apertados: "  JOÃO  da Silva" vira "joao da silva".</summary>
    public static string Normalizar(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder();
        foreach (var c in s.Trim().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return string.Join(' ', sb.ToString().Normalize(NormalizationForm.FormC)
                                  .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// A busca é por NÚMERO quando, tirado um "CD" do começo, só sobram dígitos e os
    /// separadores que se digitam junto ("#4719", "47 19", "CD-123"). Qualquer outra
    /// letra faz dela uma busca por NOME.
    /// </summary>
    public static bool BuscaPorNumero(string? busca)
    {
        var b = Normalizar(busca);
        if (b.StartsWith("cd", StringComparison.Ordinal)) b = b[2..];
        b = b.Trim(' ', '-', '#');
        return b.Length > 0 && b.All(c => char.IsDigit(c) || c is ' ' or '-' or '#');
    }

    /// <summary>
    /// Número: os dígitos, como a busca do quadro ("47" acha 4719 e 1470). Nome: um pedaço
    /// do nome do cliente, sem diferença de maiúscula nem de acento ("joao" acha "João").
    /// Busca vazia = todos.
    /// </summary>
    public static bool CasaBusca(Ticket t, string? busca)
    {
        var b = Normalizar(busca);
        if (b.Length == 0) return true;
        if (BuscaPorNumero(b)) return Kds.CasaBusca(t.Numero, b);
        return Normalizar(t.Cliente).Contains(b, StringComparison.Ordinal);
    }

    // ── A LISTA ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Só quem saiu, no período, que casa com a busca; do mais novo para o mais velho pela
    /// hora de SAÍDA (sem ela, pelo cancelamento ou pelo último carimbo); até <paramref name="teto"/> linhas.
    /// </summary>
    public static ListaFinalizados Montar(IEnumerable<Ticket> tickets, PeriodoFinalizados p, string? busca,
                                          DateTime agora, int teto = Teto)
    {
        var todos = tickets
            .Where(t => Saiu(t.Status) && NoPeriodo(t, p, agora) && CasaBusca(t, busca))
            .OrderByDescending(Ordem)
            .ThenByDescending(t => t.CriadoEm)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .ToList();
        var linhas = todos.Take(Math.Max(0, teto)).Select(t => Linha(t, agora)).ToList();
        return new ListaFinalizados(linhas, todos.Count);
    }

    /// <summary>Quantos casam, sem montar linha (os números dos botões de período).</summary>
    public static int Contar(IEnumerable<Ticket> tickets, PeriodoFinalizados p, string? busca, DateTime agora)
        => tickets.Count(t => Saiu(t.Status) && NoPeriodo(t, p, agora) && CasaBusca(t, busca));

    public static LinhaFinalizado Linha(Ticket t, DateTime agora)
    {
        var estado = Estado(t);
        // Cancelado mostra a hora do cancelamento; sem ela, a da saída (o pedido saiu e depois
        // foi cancelado: "Saiu 19:21" com o rótulo CANCELADO). Hora que não se sabe não aparece.
        string? hora = estado == EstadoFinalizado.Cancelado && t.CanceladoEm is { } c ? "Cancelado " + TextoHora(c, agora)
                     : HoraDaSaida(t) is { } h ? "Saiu " + TextoHora(h, agora)
                     : null;
        return new LinhaFinalizado(t, estado, Rotulo(estado), "#" + t.Numero, Origem(t),
            t.Cliente is { Length: > 0 } nome ? nome.Trim() : null, Resumo(t),
            "Chegou " + TextoHora(t.CriadoEm, agora), hora);
    }

    public static string Origem(Ticket t)
        => t.Origem != "ifood" ? "BALCÃO" : ECardapio(t) ? "CARDÁPIO" : "iFOOD";

    /// <summary>Os itens numa linha só, com as MESMAS linhas do card ("2× Donut Homer").</summary>
    public static string Resumo(Ticket t)
    {
        try
        {
            return string.Join(", ", EtiquetaKds.Linhas(t)
                .Where(l => l.Tipo is TipoLinhaEtiqueta.Item or TipoLinhaEtiqueta.Subitem)
                .Select(l => l.Qtd.Length > 0 ? l.Qtd + " " + l.Texto : l.Texto));
        }
        catch { return ""; }   // itens_json ilegível: a linha aparece mesmo assim
    }

    /// <summary>"14:32" hoje, "ontem 14:32", "12/10 14:32" nos outros dias (dia do calendário: a hora diz a verdade).</summary>
    public static string TextoHora(DateTime quando, DateTime agora)
        => quando.Date == agora.Date ? quando.ToString("HH:mm")
         : quando.Date == agora.Date.AddDays(-1) ? "ontem " + quando.ToString("HH:mm")
         : quando.ToString("dd/MM HH:mm");

    // ── O QUE O DETALHE DE QUEM SAIU OFERECE ────────────────────────────────

    /// <summary>
    /// "Comanda de novo": só pedido do iFood que saiu e NÃO foi cancelado. Comanda de pedido
    /// cancelado na cozinha é convite para produzir o que ninguém vai buscar.
    /// </summary>
    public static bool PodeComandaDeNovo(Ticket t)
        => Saiu(t.Status) && t.Origem == "ifood" && Estado(t) != EstadoFinalizado.Cancelado;

    /// <summary>
    /// "Fale com o iFood": a regra de sempre e, em pedido que já saiu, só o que chegou HOJE. O
    /// Gestor procura pelo número de 4 dígitos na tela do dia; o número de outro dia acha outro
    /// pedido, ou nenhum.
    /// </summary>
    public static bool PodeFalarComIfood(Ticket t, DateTime agora)
        => AjudaIfood.PodePedirAjuda(t.Origem, t.Numero)
           && (!Saiu(t.Status) || NoPeriodo(t, PeriodoFinalizados.Hoje, agora));

    /// <summary>
    /// A marca no topo da comanda tirada de novo, no papel: a cozinha sabe que é segunda via
    /// de um pedido que já saiu, e não um pedido novo. ASCII, porque a bobina térmica não
    /// garante acento na página de código dela.
    /// </summary>
    public static string MarcaSegundaVia(Ticket t, DateTime agora)
        => HoraDaSaida(t) is { } h
            ? $"2a VIA (SAIU {(h.Date == agora.Date ? h.ToString("HH:mm") : h.ToString("dd/MM HH:mm"))})"
            : "2a VIA";

    // ── TEXTOS DE TELA (uma linha cada, sem travessão; teto de 4 linhas vigiado pela suíte) ─

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static string N(int n) => n.ToString("N0", PtBr);

    /// <summary>O botão no cabeçalho da coluna PRONTO.</summary>
    public static string BotaoSairamHoje(int n) => $"Saíram hoje: {N(n)}";

    /// <summary>O botão de cada período na barra da lista: "Hoje (143)".</summary>
    public static string BotaoPeriodo(PeriodoFinalizados p, int n) => $"{NomePeriodo(p)} ({N(n)})";

    public static string NomePeriodo(PeriodoFinalizados p) => p switch
    {
        PeriodoFinalizados.Ontem => "Ontem",
        PeriodoFinalizados.SeteDias => "7 dias",
        _ => "Hoje",
    };

    public const string BotaoVoltarAoQuadro = "Voltar ao quadro";
    public const string BotaoVerOsQueSairam = "Ver os que saíram";
    public const string TextoGuardado = "Pedidos que passaram por este caixa.";
    public const string TextoSemInternet = "Sem internet. Mostrando o que este caixa guardou.";
    public const string DicaBuscaQuadro = "nº do pedido";
    public const string DicaBuscaLista = "nº ou nome";
    public const string TextoEtiquetaNaBusca = "Etiqueta lida dentro da busca. Toque no ✕ e bipe de novo.";

    // O "Comanda de novo" do detalhe de quem já saiu: a resposta aparece NO PRÓPRIO BOTÃO
    // por 3 s, porque a linha de aviso do cabeçalho não aparece a 1024x768. Curtos: os três
    // botões dividem a linha, e "Não saiu. Confira o papel" era cortado nas duas pontas.
    public const string BotaoComandaDeNovo = "Comanda de novo";
    public const string TextoComandaSaiu = "Saiu na impressora";
    public const string TextoComandaNaoSaiu = "Não imprimiu";

    public static string TextoCortada(int teto = Teto) => $"Mostrando os {N(teto)} mais novos. Procure pelo número ou nome.";

    /// <summary>
    /// Pedido novo chegando com a lista aberta: as três colunas estão escondidas, e o som
    /// sozinho não diz onde olhar. A faixa inteira é o botão de volta.
    /// </summary>
    public static string TextoPedidosNovos(int n)
        => n == 1 ? "Chegou 1 pedido novo. Toque para ver o quadro."
                  : $"Chegaram {N(n)} pedidos novos. Toque para ver o quadro.";

    /// <summary>
    /// A lista vazia diz o que fazer: sem busca, que nada saiu; com busca, onde procurar a
    /// seguir, e só se lá houver alguma coisa (<paramref name="seteDias"/> = quantos casam
    /// nos 7 dias: "Toque em 7 dias" com "7 dias (0)" no botão era um toque inútil).
    /// </summary>
    public static string TextoVazio(PeriodoFinalizados p, string? busca, int seteDias = 0)
    {
        var b = (busca ?? "").Trim();
        if (b.Length == 0)
            return p switch
            {
                PeriodoFinalizados.Ontem => "Nenhum pedido saiu ontem neste caixa.",
                PeriodoFinalizados.SeteDias => "Nenhum pedido saiu nos últimos 7 dias neste caixa.",
                _ => "Nenhum pedido saiu hoje neste caixa.",
            };
        if (p == PeriodoFinalizados.SeteDias || seteDias <= 0)
            return $"Nada com \"{b}\" nos últimos 7 dias neste caixa.";
        return p == PeriodoFinalizados.Ontem
            ? $"Nada com \"{b}\" ontem. Toque em 7 dias."
            : $"Nada com \"{b}\" hoje. Toque em 7 dias.";
    }

    /// <summary>
    /// A faixa que aparece abaixo do cabeçalho do QUADRO quando há busca: quantos estão no
    /// quadro e quantos já saíram nos últimos 7 dias. O quadro só filtra por número; com
    /// nome, a faixa diz que o nome só vale na lista dos que saíram.
    /// </summary>
    public static string TextoFaixaBusca(string? busca, int noQuadro, int jaSairam)
    {
        if (!BuscaPorNumero(busca))
            return "O quadro só procura por número. Com esse nome, "
                 + (jaSairam == 0 ? "nenhum saiu." : jaSairam == 1 ? "1 já saiu." : $"{N(jaSairam)} já saíram.");
        var sairam = jaSairam == 0 ? "nenhum" : N(jaSairam);
        var quadro = noQuadro == 0 ? "nenhum" : N(noQuadro);
        return $"No quadro: {quadro}. Já saíram: {sairam}.";
    }

    // ── O REFORÇO DA NUVEM (quando há internet) ─────────────────────────────

    /// <summary>
    /// Os pedidos da lista que a pdv_kds_status pode esclarecer: delivery que saiu sem motivo
    /// certo (SAIU DO QUADRO). Até 100, o teto da RPC.
    /// </summary>
    public static IReadOnlyList<string> ParaConfirmarNaNuvem(IEnumerable<Ticket> tickets)
        => tickets.Where(t => t.Origem == "ifood" && Saiu(t.Status) && Estado(t) == EstadoFinalizado.SaiuDoQuadro)
                  .Select(t => t.RefId).Where(r => r.Length > 0)
                  .Distinct(StringComparer.Ordinal).Take(100).ToList();

    /// <summary>
    /// Os pedidos que podem ter sido cancelados DEPOIS de sair: todo pedido do iFood (não do
    /// cardápio) que saiu e ainda não tem cancelamento anotado, SAIU ou SAIU DO QUADRO. Sem
    /// teto: quem chama manda em lotes de <see cref="LoteNuvem"/>.
    /// </summary>
    public static IReadOnlyList<string> ParaConferirCancelamento(IEnumerable<Ticket> tickets)
        => tickets.Where(t => t.Origem == "ifood" && !ECardapio(t) && Saiu(t.Status) && !t.CanceladoCerto)
                  .Select(t => t.RefId).Where(r => r.Length > 0)
                  .Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Quantos pedidos por consulta à nuvem (o teto da pdv_kds_status).</summary>
    public const int LoteNuvem = 100;

    /// <summary>
    /// O motivo que a resposta da nuvem prova. Concluído e despachado: saiu. Cancelado: só no
    /// iFood, porque no cardápio próprio "cancelado" é qualquer estado que o quadro não
    /// conhece. Outro estado (pronto, faturado): não prova nada, fica como está.
    /// </summary>
    public static string? MotivoDaNuvem(string? status, bool eCardapio)
        => (status ?? "").Trim().ToLowerInvariant() switch
        {
            "concluido" or "despachado" => MotivoNuvemConcluiu,
            "cancelado" when !eCardapio => MotivoIfoodCancelou,
            _ => null,
        };

    // ── LEITURAS DO BANCO DO CAIXA ──────────────────────────────────────────

    /// <summary>
    /// Só os pedidos da LOJA em que o caixa está (o nome do terminal quando o pedido entrou;
    /// sem loja anotada, aparece). Caixa reconfigurado para outra loja não mostra os de antes.
    /// </summary>
    private const string DaLoja = "(loja IS NULL OR loja = (SELECT loja_nome FROM terminal WHERE id = 1))";

    /// <summary>A régua do SQL: o texto ISO por dia, com folga de um dia de cada lado. A data de verdade
    /// confere depois (NoPeriodo), porque a virada é às 05:00 e não à meia-noite.</summary>
    private static object Folga(PeriodoFinalizados p, DateTime agora)
    {
        var (desde, ate) = Janela(p, agora);
        return new { desde = desde.Date.AddDays(-1).ToString("yyyy-MM-dd"), ate = ate.Date.AddDays(1).ToString("yyyy-MM-dd") };
    }

    /// <summary>Os que saíram, chegados no período, desta loja.</summary>
    public static List<Ticket> Ler(PeriodoFinalizados p, DateTime agora)
    {
        using var cx = Banco.Abrir();
        return cx.Query(
                $@"SELECT * FROM kds_ticket
                    WHERE status IN ('entregue','cancelado')
                      AND criado_em >= @desde AND criado_em < @ate AND {DaLoja}",
                Folga(p, agora))
            .Select(r => (Ticket)Kds.Ler(r))
            .Where(t => NoPeriodo(t, p, agora))
            .ToList();
    }

    /// <summary>"Saíram hoje": o número do botão, relido a cada 10 s (índice ix_kds_criado; só a hora de chegada).</summary>
    public static int ContarHoje(DateTime agora)
    {
        var (desde, ate) = Janela(PeriodoFinalizados.Hoje, agora);
        using var cx = Banco.Abrir();
        return cx.Query<string>(
                $@"SELECT criado_em FROM kds_ticket
                    WHERE status IN ('entregue','cancelado')
                      AND criado_em >= @desde AND criado_em < @ate AND {DaLoja}",
                Folga(PeriodoFinalizados.Hoje, agora))
            .Count(c => DateTime.TryParse(c, out var d) && d >= desde && d < ate);
    }

    /// <summary>O mais recente que saiu com este número, desta loja (o modo foto abre o detalhe por aqui).</summary>
    public static Ticket? PorNumero(string numero)
    {
        using var cx = Banco.Abrir();
        return cx.Query(
                $@"SELECT * FROM kds_ticket
                    WHERE numero = @n AND status IN ('entregue','cancelado') AND {DaLoja}
                    ORDER BY criado_em DESC LIMIT 1", new { n = numero })
            .Select(r => (Ticket)Kds.Ler(r)).FirstOrDefault();
    }

    /// <summary>
    /// Grava o que a pdv_kds_status respondeu. A SAÍDA só onde o motivo é "não sei" (vazio,
    /// expirou, o cardápio largou): nada que este caixa sabe de certo é trocado. "cancelado"
    /// do iFood (não do cardápio) marca também o cancelamento, sem hora (a RPC não diz).
    /// Devolve quantas linhas mudaram.
    /// </summary>
    public static int AplicarNuvem(IEnumerable<(string OrderId, string Status, string? PreparoAte)> respostas)
    {
        var mudou = 0;
        using var cx = Banco.Abrir();
        foreach (var (id, status, _) in respostas)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            var numero = cx.QueryFirstOrDefault<string>(
                "SELECT numero FROM kds_ticket WHERE origem = 'ifood' AND ref_id = @r", new { r = id });
            if (numero is null) continue;
            var motivo = MotivoDaNuvem(status, numero.StartsWith("CD-", StringComparison.OrdinalIgnoreCase));
            if (motivo is null) continue;
            mudou += cx.Execute(
                @"UPDATE kds_ticket SET encerrado_motivo = @m
                   WHERE origem = 'ifood' AND ref_id = @r AND status IN ('entregue','cancelado')
                     AND (encerrado_motivo IS NULL OR encerrado_motivo IN (@exp, @cd))",
                new { m = motivo, r = id, exp = MotivoExpirou, cd = MotivoCardapioLargou });
            if (motivo == MotivoIfoodCancelou)
                mudou += cx.Execute(
                    @"UPDATE kds_ticket SET cancelado_certo = 1
                       WHERE origem = 'ifood' AND ref_id = @r AND status IN ('entregue','cancelado')
                         AND cancelado_certo = 0",
                    new { r = id });
        }
        return mudou;
    }

    /// <summary>
    /// Grava os cancelamentos que a nuvem provou (Nuvem.CanceladosAsync): pedido do iFood, nunca
    /// do cardápio. A hora do evento entra só onde não havia hora; o cancelamento não se desfaz.
    /// Devolve quantas linhas mudaram.
    /// </summary>
    public static int AplicarCancelamentos(IEnumerable<(string OrderId, DateTime? Em)> cancelados)
    {
        var mudou = 0;
        using var cx = Banco.Abrir();
        foreach (var (id, em) in cancelados)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            mudou += cx.Execute(
                @"UPDATE kds_ticket SET cancelado_certo = 1, cancelado_em = coalesce(cancelado_em, @em)
                   WHERE origem = 'ifood' AND ref_id = @r AND upper(numero) NOT LIKE 'CD-%'
                     AND (cancelado_certo = 0 OR (cancelado_em IS NULL AND @em IS NOT NULL))",
                new { r = id, em = em?.ToString("o") });
        }
        return mudou;
    }

    /// <summary>
    /// Pergunta à nuvem, em lotes, quais destes pedidos foram cancelados e grava. Devolve
    /// quantas linhas mudaram, ou null se algum lote ficou sem resposta (o que respondeu já
    /// foi gravado). <paramref name="lotes"/> = teto de consultas (a lista de 300 faz 3).
    /// </summary>
    public static async Task<int?> ConferirCancelamentosAsync(
        Func<IReadOnlyList<string>, Task<List<(string OrderId, DateTime? Em)>?>> consulta,
        IReadOnlyList<string> ids, int lotes = 3)
    {
        var mudou = 0;
        var falhou = false;
        foreach (var lote in ids.Chunk(LoteNuvem).Take(lotes))
        {
            var r = await consulta(lote).ConfigureAwait(false);
            if (r is null) { falhou = true; continue; }
            mudou += AplicarCancelamentos(r);
        }
        return falhou ? null : mudou;
    }

    /// <summary>Os que saíram nas últimas 12 h e ainda não têm cancelamento anotado (o vigia de 2 em 2 min).</summary>
    public static List<string> RecentesParaConferir(DateTime agora)
    {
        using var cx = Banco.Abrir();
        return cx.Query<string>(
                @"SELECT ref_id FROM kds_ticket
                   WHERE origem = 'ifood' AND upper(numero) NOT LIKE 'CD-%'
                     AND status IN ('entregue','cancelado') AND cancelado_certo = 0
                     AND coalesce(encerrado_em, entregue_em, criado_em) >= @desde
                   ORDER BY criado_em DESC LIMIT @n",
                new { desde = agora.AddHours(-12).ToString("o"), n = LoteNuvem })
            .ToList();
    }

    /// <summary>Intervalo do vigia dos cancelamentos depois da saída.</summary>
    public static readonly TimeSpan IntervaloConferencia = TimeSpan.FromMinutes(2);
    private static DateTime _proximaConferencia = DateTime.MinValue;

    /// <summary>
    /// O VIGIA, na puxada do quadro (Kds.PuxarDaNuvemAsync), de 2 em 2 min: o iFood cancelou
    /// algum dos que saíram nas últimas 12 h? Anotado enquanto a nuvem ainda sabe (o evento de
    /// entrega só fica 12 h na pdv_kds_entrega), quem procurar o pedido amanhã, em "Ontem", lê
    /// CANCELADO. Nunca lança: o quadro não pode cair por causa disto.
    /// </summary>
    public static async Task ConferirRecentesAsync(Nuvem nuvem, DateTime agora)
    {
        if (agora < _proximaConferencia) return;
        _proximaConferencia = agora + IntervaloConferencia;
        try
        {
            var ids = RecentesParaConferir(agora);
            if (ids.Count == 0) return;
            await ConferirCancelamentosAsync(nuvem.CanceladosAsync, ids, lotes: 1).ConfigureAwait(false);
        }
        catch { /* sem nuvem ou sem banco: fica para a próxima */ }
    }

    /// <summary>Só a suíte: o próximo ConferirRecentesAsync roda sem esperar os 2 min.</summary>
    internal static void ZerarVigia() => _proximaConferencia = DateTime.MinValue;
}
