using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>Para onde vai o pedido que o caixa manda para a cozinha.</summary>
public enum DestinoCozinha
{
    /// <summary>O cliente come na loja (bandeja).</summary>
    Loja,
    /// <summary>O cliente leva (sacola).</summary>
    Viagem,
}

/// <summary>
/// PEDIDO PARA A COZINHA (13/10/2026, pedido do dono): "adicione no PDV a possibilidade de pedido
/// para cozinha... cliente quer um donuts... caixa faz pedido PDV coloca se e consumo loja ou
/// viagem... e cai no KDS pra producao em cor de card diferente e com mencao Venda PDV".
///
/// O caixa marca "Para a cozinha" no pagamento e escolhe "Consumo na loja" ou "Para viagem". A
/// VENDA segue o caminho de sempre (gravada, fila "venda", nota ou recibo, baixa de estoque e
/// receita na nuvem pelo pdv_registrar_venda): o pedido da cozinha e SO producao. Ele nasce NA
/// MESMA TRANSACAO da venda (Vendas.Finalizar), depois do pagamento, e por isso:
///  · nao existe pedido sem venda paga (cliente que desiste antes de pagar nao manda produzir);
///  · nao existe segundo lancamento: o servidor do KDS grava o pedido e nao chama o ERP.
///
/// Sem internet a venda NAO trava: o pedido fica na fila (outbox, tipo <see cref="TipoNaFila"/>)
/// e a Drenagem manda quando a rede volta. O servidor deduplica pelo id da venda, entao o
/// reenvio nao vira card dobrado. Passadas <see cref="Validade"/> sem conseguir, desiste: o
/// cliente ja foi embora e um donut atrasado de horas na TV so atrapalha.
///
/// Configuracao (tela Configuracao, bloco "PEDIDO PARA A TV DA COZINHA"): a chave da loja, que o
/// servidor do KDS confere (PDV_KDS_TOKENS no .env de la). Sem chave o botao nao aparece.
/// </summary>
public static class PedidoCozinha
{
    public const string TipoNaFila = "kds_cozinha";
    public const string ChaveToken = "kds_cozinha_token";
    public const string ChaveUrl = "kds_cozinha_url";
    /// <summary>O servidor do KDS da TV (o mesmo do link do pronto da etiqueta).</summary>
    public const string UrlPadrao = "https://kds.americandaybrasil.com.br/homolog";
    public const string Rota = "/api/pdv/pedidos";

    /// <summary>Quanto o pedido espera a rede antes de desistir.</summary>
    public static readonly TimeSpan Validade = TimeSpan.FromHours(3);

    // ── textos da tela (curtos, uma linha, sem travessao) ──
    public const string Rotulo = "Para a cozinha";
    public const string TextoNao = "Não";
    public const string TextoLoja = "Consumo na loja";
    public const string TextoViagem = "Para viagem";

    public static string Texto(DestinoCozinha? d) => d switch
    {
        DestinoCozinha.Loja => TextoLoja,
        DestinoCozinha.Viagem => TextoViagem,
        _ => TextoNao,
    };

    public static string? Token(SqliteConnection cx)
        => Vendas.Config(cx, ChaveToken) is { } t && t.Trim().Length > 0 ? t.Trim() : null;

    public static string Url(SqliteConnection cx)
        => (Vendas.Config(cx, ChaveUrl) is { } u && u.Trim().Length > 0 ? u.Trim() : UrlPadrao).TrimEnd('/');

    /// <summary>O botao so aparece com a chave da loja gravada.</summary>
    public static bool Ligado(SqliteConnection cx) => Token(cx) is not null;

    /// <summary>
    /// O corpo do POST /api/pdv/pedidos. Todos os itens da venda vao (o caixa nao tem regra de
    /// categoria de producao); o combo leva os sabores POR UNIDADE (a TV multiplica pela
    /// quantidade, como faz com o iFood). Item pesavel vai como 1 com a quantidade no nome.
    /// </summary>
    public static Dictionary<string, object?> Corpo(string vendaId, long numero, IEnumerable<LinhaVenda> itens,
        DestinoCozinha destino, string? operador, string? cliente, DateTime criadoEm)
    {
        var lista = new List<Dictionary<string, object?>>();
        foreach (var i in itens)
        {
            var inteiro = i.Qtd.Milesimos % 1000 == 0;
            var qtd = inteiro ? (int)Math.Max(1, Math.Min(999, i.Qtd.Milesimos / 1000)) : 1;
            var nome = (i.Descricao ?? "").Trim();
            if (!inteiro) nome = $"{nome} {i.Qtd.Valor.ToString("0.###", CultureInfo.GetCultureInfo("pt-BR"))} {i.Unidade}".Trim();
            if (nome.Length == 0) continue;
            if (nome.Length > 120) nome = nome[..120];
            var item = new Dictionary<string, object?>
            {
                ["nome"] = nome,
                ["qtd"] = qtd,
                ["obs"] = i.GratisMilesimos > 0 ? "brinde" : null,
            };
            if (i.TemEscolhas)
                item["sabores"] = i.Escolhas!.Where(e => e.Qtd > 0)
                    .Select(e => new Dictionary<string, object?> { ["nome"] = e.Nome, ["qtd"] = e.Qtd })
                    .ToList();
            lista.Add(item);
        }
        return new Dictionary<string, object?>
        {
            ["vendaId"] = vendaId,
            ["numero"] = numero.ToString(CultureInfo.InvariantCulture),
            ["cliente"] = string.IsNullOrWhiteSpace(cliente) ? null : cliente.Trim(),
            ["consumo"] = destino == DestinoCozinha.Loja ? "loja" : "viagem",
            ["operador"] = string.IsNullOrWhiteSpace(operador) ? null : operador.Trim(),
            ["criadoEm"] = criadoEm.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["itens"] = lista,
        };
    }

    /// <summary>
    /// Poe o pedido na fila, NA TRANSACAO DA VENDA (quem chama passa o tx). A chave da loja NAO
    /// vai no payload: e lida na hora do envio (trocar a chave vale para o que ainda esta na fila).
    /// </summary>
    public static void Enfileirar(SqliteConnection cx, SqliteTransaction? tx, string vendaId, object corpo)
        => Caixa.Enfileirar(cx, tx, TipoNaFila, vendaId, "cozinha:" + vendaId, corpo);

    /// <summary>
    /// O que a Drenagem faz com uma linha <see cref="TipoNaFila"/>, no contrato da fila:
    /// (true) entregue, (false) recusa que nao muda tentando de novo, (null) espera e tenta depois.
    ///  · 200/201: entregue (201 = card novo; 200 = o servidor ja tinha, foi um reenvio);
    ///  · 400/422: o corpo nao serve; conta tentativa e vai para o dead-letter no teto;
    ///  · 401/403: chave recusada; espera (a loja corrige a chave e o pedido sai);
    ///  · 404: rota ainda nao publicada no servidor; espera;
    ///  · 408/429/5xx/sem resposta: rede; espera.
    /// Sem chave gravada tambem espera: quem desligou a chave com pedido na fila decide em 3 h.
    /// </summary>
    public static async Task<(bool? Ok, string? Erro)> ResolverNaFilaAsync(string payload, string? url, string? token,
        Func<string, string, string, Task<(int Status, string? Corpo)>> post, DateTime agora)
    {
        DateTime? criado;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            criado = doc.RootElement.TryGetProperty("criadoEm", out var c) && c.ValueKind == JsonValueKind.String
                     && DateTime.TryParse(c.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
                ? d : null;
        }
        catch { return (false, "pedido da cozinha ilegível na fila"); }
        // Pedido velho NAO vai para a TV: o cliente ja foi atendido (ou embora). Sai da fila
        // com o motivo, sem chamada. O relogio e o da VENDA, nao o da primeira falha: caixa que
        // ficou horas sem sessao de nuvem nao manda o donut das 9h as 15h.
        if (criado is { } cr && agora.ToUniversalTime() - cr > Validade)
            return (true, "descartado sem enviar: pedido da cozinha com mais de 3 h");
        if (string.IsNullOrWhiteSpace(token)) return (null, "sem a chave da TV da cozinha neste caixa");
        if (string.IsNullOrWhiteSpace(url)) return (null, "sem o endereço da TV da cozinha");

        var (status, corpo) = await post(url.TrimEnd('/') + Rota, token, payload).ConfigureAwait(false);
        var trecho = string.IsNullOrEmpty(corpo) ? "" : corpo.Length <= 120 ? corpo : corpo[..120];
        return status switch
        {
            200 or 201 => (true, null),
            401 or 403 => (null, $"HTTP {status}: chave da TV da cozinha recusada"),
            404 => (null, "HTTP 404: a TV da cozinha ainda não recebe pedido do caixa"),
            408 or 425 or 429 => (null, $"HTTP {status}: {trecho}"),
            >= 400 and < 500 => (false, $"HTTP {status}: {trecho}"),
            0 => (null, "sem resposta da TV da cozinha (rede)"),
            _ => (null, $"HTTP {status}: {trecho}"),
        };
    }
}
