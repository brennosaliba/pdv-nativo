using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>
/// Uma linha da comanda que ainda não virou venda.
///
/// Guarda o SNAPSHOT do produto no momento do bipe — inclusive o preço já com a
/// promoção do dia aplicada. Restaurar pelo catálogo atual traria o preço de tabela
/// e mudaria o total que o cliente já ouviu em voz alta.
/// </summary>
public sealed record ItemRascunho(
    string ProdutoId, string? Plu, string Nome, string Categoria,
    long PrecoCent, long QtdMilesimos, string Unidade,
    string? Ncm, string? Cest, string? Csosn, int Origem, string? Foto);

/// <summary>
/// A comanda em andamento, do jeito que ela volta depois do religamento.
///
/// Repare no que NÃO tem aqui: pagamento, NSU, autorização, cobrança de TEF. É
/// proposital e o teste trava por reflexão — ver <see cref="Rascunho"/>.
/// </summary>
public sealed record ComandaRascunho(
    string SessaoId, string OperadorId, IReadOnlyList<ItemRascunho> Itens,
    Dinheiro Desconto, string? CortesiaCodigo,
    IReadOnlyDictionary<string, int> CortesiaCobertura, DateTime AtualizadoEm);

/// <summary>
/// A COMANDA EM ANDAMENTO NO DISCO.
///
/// Antes disto os itens viviam só na lista da tela: queda de energia no meio do
/// atendimento e o operador rebipava tudo com o cliente esperando no balcão.
///
/// Três limites que este tipo faz valer:
///  - RASCUNHO NÃO É VENDA. Nada aqui vira `venda` sozinho: o que volta é o trabalho
///    de digitação. O passo 24 da homologação (desligar no meio e a venda não se
///    realizar) continua valendo com o rascunho ligado.
///  - RASCUNHO NÃO HERDA PAGAMENTO. A cobrança que ficou armada na maquininha tem
///    destino próprio (a reconciliação a declara órfã). Se o rascunho a trouxesse
///    junto, a venda nova nasceria "paga" por dinheiro que ninguém sabe se entrou.
///  - RASCUNHO É DO TURNO. Ler com outra sessão de caixa não devolve nada e ainda
///    apaga o que sobrou: comanda de um caixa que já fechou não pode aparecer no
///    turno seguinte e entrar na venda de outra pessoa.
/// </summary>
public static class Rascunho
{
    /// <summary>
    /// QUEM ESTÁ ESCREVENDO: o processo, não o turno.
    ///
    /// `Ler` compara `sessao_id`, e sessão não separa dois Pdv.exe abertos na mesma
    /// máquina — os dois attacham no MESMO turno (MainWindow.Roteia → Caixa.SessaoAberta).
    /// A linha é uma só (`id = 1`), então sem esta marca o segundo grava por cima do
    /// primeiro, e a tela que ficou com o `_sessao` de um turno já fechado faz `Ler`
    /// APAGAR a comanda do caixa que está de fato aberto.
    ///
    /// PID + início do processo: PID sozinho o Windows recicla, e depois do religamento
    /// o número antigo pode estar de pé em qualquer outro programa — o que faria a
    /// comanda da queda de energia parecer "de um PDV vivo" e nunca mais voltar.
    /// </summary>
    private static readonly string Eu = QuemSouEu();

    private static string QuemSouEu()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return $"{p.Id}:{p.StartTime.Ticks}";
        }
        catch { return ""; }   // sem identidade: grava `dono` nulo e tudo volta a ser como era
    }

    /// <summary>
    /// true SÓ quando a linha é de OUTRO processo que AINDA ESTÁ DE PÉ.
    ///
    /// Na dúvida responde false, e é de propósito: coluna vazia (banco anterior a ela),
    /// formato estranho, processo que não dá para inspecionar — em todos, o caminho
    /// seguro é gravar/apagar como sempre. Recusar apagar por engano deixaria um
    /// rascunho sobreviver à venda paga, e aí o operador cobra os mesmos itens duas
    /// vezes; recusar gravar por engano custa, no máximo, rebipar a comanda.
    /// </summary>
    private static bool DeOutroPdvVivo(string? dono)
    {
        if (string.IsNullOrEmpty(dono) || dono == Eu) return false;
        var corte = dono.IndexOf(':');
        if (corte <= 0
            || !int.TryParse(dono[..corte], out var pid)
            || !long.TryParse(dono[(corte + 1)..], out var inicio)) return false;
        try
        {
            using var outro = Process.GetProcessById(pid);
            return outro.StartTime.Ticks == inicio;
        }
        catch { return false; }   // não existe mais: era a queda de energia, e a comanda é para voltar
    }

    /// <summary>A comanda no disco está sendo digitada por outro PDV que continua vivo?</summary>
    private static bool OcupadoPorOutroPdv(SqliteConnection cx, SqliteTransaction? tx = null)
        => DeOutroPdvVivo(cx.ExecuteScalar<string?>(
            "SELECT dono FROM comanda_rascunho WHERE id = 1", transaction: tx));

    /// <summary>
    /// Grava (ou substitui) a comanda do caixa. Uma linha só, sobrescrita no lugar:
    /// isto roda a cada bipe do leitor, e tabela que cresce por bipe vira lixo.
    ///
    /// Comanda vazia não deixa rascunho — tirar o último item é limpar a comanda.
    /// </summary>
    public static void Gravar(SqliteConnection cx, Sessao sessao, Operador operador,
        IReadOnlyList<ItemRascunho> itens, Dinheiro desconto,
        string? cortesiaCodigo = null, IReadOnlyDictionary<string, int>? cortesiaCobertura = null)
    {
        if (itens.Count == 0) { Apagar(cx); return; }

        // A comanda no disco é de outro PDV que continua digitando: não é minha para
        // sobrescrever. Perco a proteção contra queda de energia NESTA tela — e é o preço
        // certo: o outro caixa não fica sem a comanda dele por causa da minha.
        if (OcupadoPorOutroPdv(cx)) return;

        cx.Execute("""
            INSERT INTO comanda_rascunho (id, sessao_id, operador_id, itens_json, desconto_cent,
                                          cortesia_codigo, cortesia_json, atualizado_em, dono)
            VALUES (1,@Ses,@Op,@Itens,@Desc,@Cod,@Cob,@Em,@Dono)
            ON CONFLICT(id) DO UPDATE SET sessao_id=@Ses, operador_id=@Op, itens_json=@Itens,
                 desconto_cent=@Desc, cortesia_codigo=@Cod, cortesia_json=@Cob, atualizado_em=@Em,
                 dono=@Dono
            """,
            new
            {
                Ses = sessao.Id,
                Op = operador.Id,
                Itens = JsonSerializer.Serialize(itens),
                Desc = desconto.Centavos,
                Cod = cortesiaCodigo,
                Cob = cortesiaCobertura is { Count: > 0 } ? JsonSerializer.Serialize(cortesiaCobertura) : null,
                Em = DateTime.Now.ToString("o"),
                Dono = Eu.Length == 0 ? null : Eu,
            });
    }

    /// <summary>
    /// A comanda guardada, se ela for DESTA sessão de caixa. Rascunho de outro turno
    /// não volta — e some, para não achar uma brecha depois.
    /// </summary>
    public static ComandaRascunho? Ler(SqliteConnection cx, string sessaoId)
    {
        var l = cx.QueryFirstOrDefault(
            "SELECT sessao_id, operador_id, itens_json, desconto_cent, cortesia_codigo, "
          + "cortesia_json, atualizado_em, dono FROM comanda_rascunho WHERE id = 1");
        if (l is null) return null;
        // Comanda de outro PDV ainda de pé: não é minha para devolver — o cliente dela
        // está no outro balcão — nem para apagar, que é o estrago pior dos dois.
        if (DeOutroPdvVivo((string?)l.dono)) return null;
        if ((string)l.sessao_id != sessaoId) { Apagar(cx); return null; }

        try
        {
            var itens = JsonSerializer.Deserialize<List<ItemRascunho>>((string)l.itens_json);
            if (itens is not { Count: > 0 }) { Apagar(cx); return null; }

            var cobertura = (string?)l.cortesia_json is { Length: > 0 } j
                ? JsonSerializer.Deserialize<Dictionary<string, int>>(j) ?? new()
                : new Dictionary<string, int>();

            return new ComandaRascunho(
                (string)l.sessao_id, (string)l.operador_id, itens,
                new Dinheiro((long)l.desconto_cent), (string?)l.cortesia_codigo, cobertura,
                DateTime.TryParse((string)l.atualizado_em, out var em) ? em : DateTime.Now);
        }
        catch (JsonException)
        {
            // Rascunho ilegível é rascunho que não existe. Ele não prova nada e não é
            // dinheiro — deixar um JSON corrompido derrubar a abertura da tela de venda
            // trocaria "rebipar a comanda" por "o caixa não abre".
            Apagar(cx);
            return null;
        }
    }

    /// <summary>
    /// O QUE O DIÁLOGO DO RASCUNHO PODE AFIRMAR SOBRE O DINHEIRO.
    ///
    /// A cobrança nasce ANTES da venda: a tela de pagamento manda o TEF cobrar e só
    /// depois grava a venda. Existe, portanto, a janela em que o cartão JÁ PASSOU e
    /// nenhuma venda existe — e ela é exatamente a janela do rascunho (queda de energia
    /// no meio do atendimento). Jurar ali "nada foi cobrado" tranquiliza o operador na
    /// hora em que ele precisava desconfiar, e o cliente paga duas vezes.
    ///
    /// Por isso a frase mora aqui, e não solta na tela: ela é dinheiro, e dinheiro se
    /// testa. <paramref name="cobrado"/> vem de <c>Caixa.CobrancaSemVenda</c>; null é
    /// "não deu para conferir" — e aí também não se afirma nada.
    /// </summary>
    public static string AvisoDeCobranca(Dinheiro? cobrado) =>
        cobrado is null
            ? "NÃO CONSEGUI CONFERIR a maquininha agora — veja no PayGo se esta compra já "
            + "foi paga ANTES de cobrar de novo."
            : cobrado.Value.Positivo
                ? $"⚠️ ATENÇÃO: o TEF tem {cobrado.Value.Formatado()} cobrado NESTE TURNO sem venda gravada. "
                + "Confira no PayGo antes de finalizar: se o cliente já pagou, NÃO cobre de novo — "
                + "estorne em TEF → Estornar."
                : "Nada foi cobrado e nenhuma venda foi gravada.";

    /// <summary>
    /// Apaga o rascunho. Chamado quando a comanda é limpa e, dentro da MESMA transação,
    /// quando a venda é gravada — rascunho que sobrevive à venda paga faz o operador
    /// cobrar os mesmos itens duas vezes.
    /// </summary>
    public static void Apagar(SqliteConnection cx, SqliteTransaction? tx = null)
    {
        // Limpar a MINHA comanda (ou finalizar a MINHA venda) não pode levar junto a que
        // outro PDV está digitando agora. Só é minha para apagar se o dono for eu, se não
        // houver dono (banco anterior à coluna) ou se o dono já morreu.
        if (OcupadoPorOutroPdv(cx, tx)) return;
        cx.Execute("DELETE FROM comanda_rascunho", transaction: tx);
    }
}
