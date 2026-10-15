using System.Globalization;
using System.Text;
using Dapper;

namespace Pdv.Nucleo;

/// <summary>Os três recortes da lista dos que já saíram, pelo dia em que o pedido CHEGOU.</summary>
public enum PeriodoFinalizados { Hoje, Ontem, SeteDias }

/// <summary>O que a lista diz de cada pedido que saiu do quadro.</summary>
public enum EstadoFinalizado
{
    /// <summary>Saiu com o entregador (balcão tocou, Gestor despachou ou concluiu, a nuvem confirmou).</summary>
    Saiu,
    /// <summary>O mesmo, em pedido de retirada ou de balcão: o cliente levou.</summary>
    Retirado,
    /// <summary>Cancelamento de verdade: o iFood cancelou, ou a venda do balcão foi cancelada.</summary>
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
/// <param name="Saiu">"Saiu 14:32" ou "Cancelado 14:32"; null quando a hora não foi anotada.</param>
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
/// honesto: só aparece o que este caixa viu.
///
/// O CUIDADO PRINCIPAL é o rótulo. O status local 'cancelado' junta quatro histórias, e
/// medido em 14/10 só 1 em 60 era cancelamento de verdade (57 estavam concluídos no
/// iFood). Por isso a tela nunca mostra o status cru: mostra <see cref="Estado"/>, que lê
/// o MOTIVO anotado na saída (colunas encerrado_em e encerrado_motivo, desta versão).
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
    /// <summary>O iFood cancelou o pedido.</summary>
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

    // ── O RÓTULO DE CADA LINHA ──────────────────────────────────────────────

    /// <summary>
    /// O estado que a lista MOSTRA. Nunca o status cru: 'cancelado' sem motivo certo não
    /// vira CANCELADO na tela, porque quase nunca é.
    /// </summary>
    public static EstadoFinalizado Estado(Ticket t)
    {
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

    /// <summary>A hora em que o pedido saiu, quando foi anotada.</summary>
    public static DateTime? HoraDaSaida(Ticket t) => t.EncerradoEm ?? t.EntregueEm;

    /// <summary>A régua da ordem: a saída, ou o último carimbo que se tem quando ela não foi anotada.</summary>
    public static DateTime Ordem(Ticket t) => t.EncerradoEm ?? t.EntregueEm ?? t.ProntoEm ?? t.PreparoEm ?? t.CriadoEm;

    // ── PERÍODO ──────────────────────────────────────────────────────────────

    /// <summary>[desde, até) pela CHEGADA no dia local: hoje, ontem, ou hoje e os seis dias antes.</summary>
    public static (DateTime Desde, DateTime Ate) Janela(PeriodoFinalizados p, DateTime agora)
    {
        var hoje = agora.Date;
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
    /// hora de SAÍDA (sem ela, pelo último carimbo); até <paramref name="teto"/> linhas.
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
        string? saiu = HoraDaSaida(t) is { } h
            ? (estado == EstadoFinalizado.Cancelado ? "Cancelado " : "Saiu ") + TextoHora(h, agora)
            : null;
        return new LinhaFinalizado(t, estado, Rotulo(estado), "#" + t.Numero, Origem(t),
            t.Cliente is { Length: > 0 } c ? c.Trim() : null, Resumo(t),
            "Chegou " + TextoHora(t.CriadoEm, agora), saiu);
    }

    public static string Origem(Ticket t)
        => t.Origem != "ifood" ? "BALCÃO"
         : t.Numero.StartsWith("CD-", StringComparison.OrdinalIgnoreCase) ? "CARDÁPIO" : "iFOOD";

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

    /// <summary>"14:32" hoje, "ontem 14:32", "12/10 14:32" nos outros dias.</summary>
    public static string TextoHora(DateTime quando, DateTime agora)
        => quando.Date == agora.Date ? quando.ToString("HH:mm")
         : quando.Date == agora.Date.AddDays(-1) ? "ontem " + quando.ToString("HH:mm")
         : quando.ToString("dd/MM HH:mm");

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
    public const string TextoGuardado = "Guardado neste caixa. Funciona sem internet.";
    public const string TextoSemInternet = "Sem internet: estado guardado neste caixa.";
    public const string DicaBuscaQuadro = "nº do pedido";
    public const string DicaBuscaLista = "nº ou nome";
    public const string TextoEtiquetaNaBusca = "Etiqueta lida dentro da busca. Toque no ✕ e bipe de novo.";

    // O "Comanda de novo" do detalhe de quem já saiu: a resposta aparece NO PRÓPRIO BOTÃO
    // por 3 s, porque a linha de aviso do cabeçalho não aparece a 1024x768.
    public const string BotaoComandaDeNovo = "Comanda de novo";
    public const string TextoComandaSaiu = "Saiu na impressora";
    public const string TextoComandaNaoSaiu = "Não saiu. Confira o papel";

    public static string TextoCortada(int teto = Teto) => $"Mostrando os {N(teto)} mais novos. Procure pelo número ou nome.";

    /// <summary>
    /// A lista vazia diz o que fazer: sem busca, que nada saiu; com busca fora dos 7 dias,
    /// onde procurar a seguir.
    /// </summary>
    public static string TextoVazio(PeriodoFinalizados p, string? busca)
    {
        var b = (busca ?? "").Trim();
        if (b.Length == 0)
            return p switch
            {
                PeriodoFinalizados.Ontem => "Nenhum pedido saiu ontem neste caixa.",
                PeriodoFinalizados.SeteDias => "Nenhum pedido saiu nos últimos 7 dias neste caixa.",
                _ => "Nenhum pedido saiu hoje neste caixa.",
            };
        return p switch
        {
            PeriodoFinalizados.Ontem => $"Nada com \"{b}\" ontem. Toque em 7 dias.",
            PeriodoFinalizados.SeteDias => $"Nada com \"{b}\" nos últimos 7 dias neste caixa.",
            _ => $"Nada com \"{b}\" hoje. Toque em 7 dias.",
        };
    }

    /// <summary>
    /// A faixa que aparece abaixo do cabeçalho do QUADRO quando há busca: quantos estão no
    /// quadro e quantos já saíram nos últimos 7 dias. O quadro só filtra por número; com
    /// nome, a faixa diz que o nome só vale na lista dos que saíram.
    /// </summary>
    public static string TextoFaixaBusca(string? busca, int noQuadro, int jaSairam)
    {
        var sairam = jaSairam == 0 ? "nenhum" : N(jaSairam);
        if (!BuscaPorNumero(busca)) return $"O quadro procura só por número. Com esse nome já saíram: {sairam}.";
        var quadro = noQuadro == 0 ? "nenhum" : N(noQuadro);
        return $"No quadro: {quadro}. Já saíram: {sairam}.";
    }

    // ── O REFORÇO DA NUVEM (quando há internet) ─────────────────────────────

    /// <summary>
    /// Os pedidos da lista que a nuvem pode esclarecer: delivery que saiu sem motivo certo
    /// (SAIU DO QUADRO). Até 100, o teto da RPC pdv_kds_status.
    /// </summary>
    public static IReadOnlyList<string> ParaConfirmarNaNuvem(IEnumerable<Ticket> tickets)
        => tickets.Where(t => t.Origem == "ifood" && Saiu(t.Status) && Estado(t) == EstadoFinalizado.SaiuDoQuadro)
                  .Select(t => t.RefId).Where(r => r.Length > 0)
                  .Distinct(StringComparer.Ordinal).Take(100).ToList();

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

    /// <summary>Os que saíram, chegados no período. A régua do SQL é a do texto ISO (o mesmo
    /// formato em toda gravação), e a lógica pura confere de novo pela data de verdade.</summary>
    public static List<Ticket> Ler(PeriodoFinalizados p, DateTime agora)
    {
        var (desde, ate) = Janela(p, agora);
        using var cx = Banco.Abrir();
        return cx.Query(
                @"SELECT * FROM kds_ticket
                   WHERE status IN ('entregue','cancelado')
                     AND criado_em >= @desde AND criado_em < @ate",
                new { desde = desde.ToString("yyyy-MM-dd"), ate = ate.ToString("yyyy-MM-dd") })
            .Select(r => (Ticket)Kds.Ler(r))
            .Where(t => NoPeriodo(t, p, agora))
            .ToList();
    }

    /// <summary>"Saíram hoje": o número do botão, relido a cada 10 s (índice ix_kds_criado).</summary>
    public static int ContarHoje(DateTime agora)
    {
        var (desde, ate) = Janela(PeriodoFinalizados.Hoje, agora);
        using var cx = Banco.Abrir();
        return cx.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM kds_ticket
               WHERE status IN ('entregue','cancelado')
                 AND criado_em >= @desde AND criado_em < @ate",
            new { desde = desde.ToString("yyyy-MM-dd"), ate = ate.ToString("yyyy-MM-dd") });
    }

    /// <summary>O mais recente que saiu com este número (o modo foto abre o detalhe por aqui).</summary>
    public static Ticket? PorNumero(string numero)
    {
        using var cx = Banco.Abrir();
        return cx.Query(
                @"SELECT * FROM kds_ticket
                   WHERE numero = @n AND status IN ('entregue','cancelado')
                   ORDER BY criado_em DESC LIMIT 1", new { n = numero })
            .Select(r => (Ticket)Kds.Ler(r)).FirstOrDefault();
    }

    /// <summary>
    /// Grava o que a nuvem respondeu, SÓ onde o motivo é "não sei" (vazio, expirou, o
    /// cardápio largou). Nada que este caixa sabe de certo é trocado. Devolve quantos mudaram.
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
        }
        return mudou;
    }
}
