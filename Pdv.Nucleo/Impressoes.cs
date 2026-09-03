using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>
/// O que o caixa faz com CADA papel que ele sabe imprimir.
///
/// <see cref="Automatico"/> sai sozinho, sem dedo de ninguém.
/// <see cref="Perguntar"/> NÃO sai sozinho e deixa o botão de imprimir à vista na tela
/// onde aquele papel aparece (o botão de sucesso da venda, o 🖨 do card do Delivery,
/// o menu TEF → Reimprimir).
/// <see cref="Nao"/> não sai e não mostra botão: a loja decidiu que aquele papel não existe.
///
/// A ordem é a dos rótulos da tela (ver <see cref="Impressoes.Rotulos"/>) e é usada como
/// índice de combo. Não reordenar.
/// </summary>
public enum PoliticaImpressao { Automatico, Perguntar, Nao }

/// <summary>
/// A política de impressão POR DOCUMENTO, gravada em texto na tabela <c>config</c>.
///
/// Pedido do dono (03/09): além de escolher a impressora, escolher para cada papel se
/// ele sai sozinho, se aparece botão na tela, ou se não sai. São quatro papéis: o cupom
/// da venda, a comanda do delivery, a via do CLIENTE do cartão e a via do
/// ESTABELECIMENTO do cartão.
///
/// ⚠️ COMPATIBILIDADE. Antes disto cada papel tinha um booleano próprio, com padrões
/// DIFERENTES entre si: o cupom nascia ligado (<c>imprimir_automatico</c> ausente = 1),
/// a comanda nascia desligada (<c>kds_comanda_auto</c> ausente = 0) e as vias nasciam
/// ligadas numa chave só (<c>tef_paygo_imprimir_vias</c> ausente = 1). Enquanto a chave
/// nova não existir, é o booleano antigo que manda — e o "0" antigo NUNCA vira
/// <see cref="PoliticaImpressao.Nao"/>: ele significava "não sai sozinho, o botão está
/// aí", que é <see cref="PoliticaImpressao.Perguntar"/>. Traduzir errado apagaria o
/// botão que as lojas usam hoje. Só um valor NOVO produz <c>Nao</c>.
///
/// A exceção é a via do cartão: <c>tef_paygo_imprimir_vias = 0</c> desligava a impressão
/// e não deixava botão nenhum na tela de venda, então ele vira <c>Nao</c> nas DUAS vias.
///
/// As regras moram AQUI, fora do WPF, porque as chaves têm mais de uma tela gravando
/// nelas (Configuração e o diálogo 🖨 da barra da venda) e duas cópias da mesma regra
/// divergem no primeiro dia.
/// </summary>
public static class Impressoes
{
    // ── os quatro papéis ────────────────────────────────────────────────────
    public const string Cupom = "cupom";
    public const string Comanda = "comanda";
    public const string ViaCliente = "via_cliente";
    public const string ViaEstabelecimento = "via_estabelecimento";

    /// <summary>Todos os documentos, na ordem em que aparecem na tela.</summary>
    public static readonly string[] Documentos = { Cupom, Comanda, ViaCliente, ViaEstabelecimento };

    /// <summary>Rótulos das três opções, na ordem do enum. É o que a tela mostra.</summary>
    public static readonly string[] Rotulos = { "Imprimir sozinho", "Perguntar na tela", "Não imprimir" };

    /// <summary>Chave NOVA em <c>config</c>, a que manda quando existe.</summary>
    public static string Chave(string documento) => documento switch
    {
        Cupom => "imp_cupom",
        Comanda => "imp_comanda",
        ViaCliente => "imp_via_cliente",
        ViaEstabelecimento => "imp_via_estabelecimento",
        _ => throw new ArgumentException($"documento desconhecido: {documento}", nameof(documento)),
    };

    /// <summary>
    /// Chave ANTIGA de onde a política vem enquanto a nova não existe. As vias do cartão
    /// dividem a mesma: era um interruptor só para as duas.
    /// </summary>
    public static string ChaveAntiga(string documento) => documento switch
    {
        Cupom => "imprimir_automatico",
        Comanda => "kds_comanda_auto",
        ViaCliente or ViaEstabelecimento => "tef_paygo_imprimir_vias",
        _ => throw new ArgumentException($"documento desconhecido: {documento}", nameof(documento)),
    };

    /// <summary>Texto gravado no banco. É o vocabulário que a tela e o núcleo compartilham.</summary>
    public static string Texto(PoliticaImpressao p) => p switch
    {
        PoliticaImpressao.Automatico => "auto",
        PoliticaImpressao.Perguntar => "perguntar",
        _ => "nao",
    };

    /// <summary>Lê o valor cru da chave nova. Null = a chave não responde nada (ausente ou lixo).</summary>
    public static PoliticaImpressao? De(string? valor) => (valor ?? "").Trim().ToLowerInvariant() switch
    {
        "auto" => PoliticaImpressao.Automatico,
        "perguntar" => PoliticaImpressao.Perguntar,
        "nao" or "não" => PoliticaImpressao.Nao,
        _ => null,
    };

    /// <summary>
    /// A regra, pura: a chave nova manda; sem ela, o booleano antigo decide.
    ///
    /// <paramref name="novo"/> é <c>config[Chave(documento)]</c> e <paramref name="antigo"/>
    /// é <c>config[ChaveAntiga(documento)]</c>. Separar os dois deixa a regra testável sem
    /// banco — é o molde de <c>Impressao.ComandaSeparada</c>.
    /// </summary>
    public static PoliticaImpressao Politica(string documento, string? novo, string? antigo)
    {
        if (De(novo) is { } escolhida) return escolhida;
        var cru = (antigo ?? "").Trim();
        return documento switch
        {
            // Ausente ou "1" = sai sozinho (é como o cupom nasce). "0" = o botão da tela.
            Cupom => cru == "0" ? PoliticaImpressao.Perguntar : PoliticaImpressao.Automatico,
            // Opt-in: ausente e "0" são a mesma coisa, e a comanda já tem o 🖨 do card.
            Comanda => cru == "1" ? PoliticaImpressao.Automatico : PoliticaImpressao.Perguntar,
            // A única que vira Nao: desligada, a via não saía e não havia botão na venda.
            ViaCliente or ViaEstabelecimento => cru == "0" ? PoliticaImpressao.Nao : PoliticaImpressao.Automatico,
            _ => throw new ArgumentException($"documento desconhecido: {documento}", nameof(documento)),
        };
    }

    /// <summary>A política valendo agora para este documento.</summary>
    public static PoliticaImpressao Politica(SqliteConnection cx, string documento)
        => Politica(documento,
            Vendas.Config(cx, Chave(documento)),
            Vendas.Config(cx, ChaveAntiga(documento)));

    /// <summary>
    /// Grava a chave NOVA e mantém a antiga em sincronia.
    ///
    /// A antiga continua sendo lida por código que ainda não passou por aqui (e por um
    /// PDV mais velho que abra o mesmo banco), então deixá-la para trás faria o caixa
    /// obedecer duas respostas diferentes para a mesma pergunta. Ela só tem dois estados,
    /// então <c>Nao</c> e <c>Perguntar</c> caem os dois em "0": quem lê a chave nova vê a
    /// diferença, quem lê a velha pelo menos não imprime sozinho.
    ///
    /// As duas vias dividem uma chave antiga só, e ela só desliga quando as DUAS estão em
    /// <c>Nao</c> — desligar por causa de uma apagaria a outra.
    /// </summary>
    public static void Gravar(SqliteConnection cx, string documento, PoliticaImpressao p)
    {
        Vendas.GravarConfig(cx, Chave(documento), Texto(p));
        if (documento is ViaCliente or ViaEstabelecimento)
        {
            var outro = documento == ViaCliente ? ViaEstabelecimento : ViaCliente;
            var ambasNao = p == PoliticaImpressao.Nao && Politica(cx, outro) == PoliticaImpressao.Nao;
            Vendas.GravarConfig(cx, ChaveAntiga(documento), ambasNao ? "0" : "1");
            return;
        }
        Vendas.GravarConfig(cx, ChaveAntiga(documento), p == PoliticaImpressao.Automatico ? "1" : "0");
    }

    // ── o que a tela faz com a política ─────────────────────────────────────

    /// <summary>
    /// O desfecho do cupom numa tela de sucesso de venda: sai papel? aparece botão?
    /// </summary>
    public readonly record struct DecisaoCupom(bool Imprime, bool MostraBotao);

    /// <summary>
    /// A decisão do cupom, uma vez só para os DOIS modos fiscais (recibo e NFC-e).
    ///
    /// Antes disto a regra estava escrita duas vezes em <c>Telas/Pagamento.xaml.cs</c>, uma
    /// por modo, e as duas já tinham divergido. Aqui ela é uma função pura, e o que a tela
    /// faz é obedecer.
    ///
    /// <paramref name="forcado"/> é o operador tendo tocado no botão (imprimir ou
    /// reimprimir): dedo humano imprime em qualquer política, inclusive <c>Nao</c>, senão o
    /// botão de reimprimir de um cupom entalado não teria efeito nenhum.
    /// </summary>
    public static DecisaoCupom DecidirCupom(PoliticaImpressao p, bool forcado)
        => new(Imprime: p == PoliticaImpressao.Automatico || forcado,
               MostraBotao: p == PoliticaImpressao.Perguntar && !forcado);

    /// <summary>
    /// O 🖨 do card do Delivery aparece? É o "perguntar na tela" da comanda, e ele também
    /// é o socorro de quando a automática falha — por isso vale em <c>Automatico</c>
    /// também. Só <c>Nao</c> apaga o botão.
    /// </summary>
    public static bool MostraBotaoComanda(PoliticaImpressao p) => p != PoliticaImpressao.Nao;

    // ── ponte com os combos da tela ─────────────────────────────────────────

    /// <summary>Índice do combo (a ordem do enum é a ordem dos rótulos).</summary>
    public static int Indice(PoliticaImpressao p) => (int)p;

    /// <summary>Escolha do combo. Índice fora da lista cai em <c>Automatico</c>, que é o que sempre saiu.</summary>
    public static PoliticaImpressao DeIndice(int indice) => indice switch
    {
        1 => PoliticaImpressao.Perguntar,
        2 => PoliticaImpressao.Nao,
        _ => PoliticaImpressao.Automatico,
    };
}
