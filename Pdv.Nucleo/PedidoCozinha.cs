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
/// MESMA TRANSACAO da venda (Vendas.Finalizar), depois do pagamento. Nao existe segundo
/// lancamento: o servidor do KDS grava o pedido e nao chama o ERP.
///
/// DOIS CAMINHOS PARA A MESMA TV (13/10/2026, comanda aberta):
///  · VENDA DE BALCAO: um pedido por venda paga (<see cref="Corpo"/>), como acima. Cliente que
///    desiste antes de pagar nao manda produzir.
///  · COMANDA ABERTA (ContasAbertas.cs): a cozinha produz ANTES do dinheiro, de proposito. Cada
///    "Pedir a cozinha" e um LOTE imutavel (<see cref="CorpoDoLote"/>), com id "<contaId>-<n>",
///    so com o que ainda nao foi pedido. O que compensa a producao sem venda e o rastro
///    (conta_evento), o PIN de outro gerente para tirar item ja pedido e os avisos do fechamento.
///    Fechar a conta e UMA venda, e ela NUNCA manda de novo o que os lotes ja levaram.
///
/// Sem internet a venda NAO trava: o pedido fica na fila (outbox, tipo <see cref="TipoNaFila"/>)
/// e a Drenagem manda quando a rede volta. O servidor deduplica pelo id (venda ou lote), entao o
/// reenvio nao vira card dobrado. Passadas <see cref="Validade"/> sem conseguir, desiste: o
/// cliente ja foi embora e um donut atrasado de horas na TV so atrapalha.
///
/// A CHAVE DA LOJA VEM DA NUVEM (1.0.31, SQL 168 do ERP; pedido do dono: "coloca sincronizado na
/// nuvem com o KDS da propria loja"). O painel guarda uma chave por loja; o caixa baixa a da loja
/// dele no Atualizar (RPC <see cref="Rpc"/>) e guarda em <see cref="ChaveTokenNuvem"/>; o servidor
/// do KDS confere a mesma chave no banco. Ninguem digita nada. O campo da Configuracao continua
/// como sobrescrita opcional deste caixa (<see cref="ChaveToken"/>); se ele for igual a chave da
/// nuvem, sai sozinho (assim trocar a chave no painel chega a todos os caixas). Sem chave o
/// botao nao aparece.
/// </summary>
public static class PedidoCozinha
{
    public const string TipoNaFila = "kds_cozinha";
    /// <summary>Sobrescrita digitada neste caixa (opcional). Vale antes da chave da nuvem.</summary>
    public const string ChaveToken = "kds_cozinha_token";
    /// <summary>A chave da loja que desceu da nuvem no Atualizar.</summary>
    public const string ChaveTokenNuvem = "kds_cozinha_token_nuvem";
    /// <summary>A RPC que devolve a chave da loja do terminal (SQL 168 do ERP).</summary>
    public const string Rpc = "pdv_kds_cozinha_chave_caixa";
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

    /// <summary>A chave que vale: a digitada neste caixa, senao a da nuvem.</summary>
    public static string? Token(SqliteConnection cx) => TokenManual(cx) ?? TokenNuvem(cx);

    public static string? TokenManual(SqliteConnection cx) => Limpo(Vendas.Config(cx, ChaveToken));

    public static string? TokenNuvem(SqliteConnection cx) => Limpo(Vendas.Config(cx, ChaveTokenNuvem));

    private static string? Limpo(string? t) => t is not null && t.Trim().Length > 0 ? t.Trim() : null;

    /// <summary>As 4 ultimas letras (o que a tela mostra; a chave inteira nunca aparece).</summary>
    public static string? TerminaEm(string? chave)
        => Limpo(chave) is { } c ? (c.Length <= 4 ? c : c[^4..]) : null;

    /// <summary>A linha "Chave da TV da cozinha: ..." da Configuracao.</summary>
    public static string DescricaoOrigem(SqliteConnection cx)
    {
        var manual = TokenManual(cx);
        var nuvem = TokenNuvem(cx);
        if (manual is not null)
            return $"Chave da TV da cozinha: digitada neste caixa (termina em {TerminaEm(manual)})."
                   + (nuvem is not null ? $" A da nuvem termina em {TerminaEm(nuvem)}." : "");
        if (nuvem is not null) return $"Chave da TV da cozinha: vem da nuvem (termina em {TerminaEm(nuvem)}).";
        return "Chave da TV da cozinha: a nuvem ainda não mandou. Toque em Atualizar.";
    }

    /// <summary>Uma linha da RPC <see cref="Rpc"/>.</summary>
    public sealed record ChaveDaNuvem(string Store, string Chave);

    /// <summary>O JSON da RPC (array) em linhas; JSON estranho = lista vazia, nunca excecao.</summary>
    public static IReadOnlyList<ChaveDaNuvem> LerDaNuvem(string? json)
    {
        var saida = new List<ChaveDaNuvem>();
        if (string.IsNullOrWhiteSpace(json)) return saida;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return saida;
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                if (o.ValueKind != JsonValueKind.Object) continue;
                string? S(string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var store = S("store");
                var chave = Limpo(S("chave"));
                if (string.IsNullOrWhiteSpace(store) || chave is null || chave.Length < 24) continue;
                saida.Add(new ChaveDaNuvem(store.Trim(), chave));
            }
        }
        catch { /* JSON ilegivel: nada a aplicar */ }
        return saida;
    }

    /// <summary>A linha desta loja: pelo nome do terminal; se so ha uma ao alcance, e ela.</summary>
    public static ChaveDaNuvem? EscolherDaLoja(IReadOnlyList<ChaveDaNuvem> linhas, string? lojaNome)
    {
        var alvo = ConfigLojaPainel.Normalizar(lojaNome);
        if (alvo.Length > 0)
        {
            var exata = linhas.FirstOrDefault(l => ConfigLojaPainel.Normalizar(l.Store) == alvo);
            if (exata is not null) return exata;
            var contem = linhas.Where(l => ConfigLojaPainel.Normalizar(l.Store).Contains(alvo)
                                           || alvo.Contains(ConfigLojaPainel.Normalizar(l.Store))).ToList();
            if (contem.Count == 1) return contem[0];
        }
        return linhas.Count == 1 ? linhas[0] : null;
    }

    /// <summary>
    /// Guarda o que a nuvem respondeu (a nuvem e a verdade: sem linha da loja, a chave da nuvem
    /// sai). A sobrescrita igual a chave da nuvem sai junto (era a copia da 1.0.30). Audita so o
    /// final. Devolve o que mudou ("" = nada).
    /// </summary>
    public static string AplicarDaNuvem(SqliteConnection cx, ChaveDaNuvem? linha)
    {
        var antes = TokenNuvem(cx);
        var nova = linha?.Chave;
        if (nova is null) cx.Execute("DELETE FROM config WHERE chave=@C", new { C = ChaveTokenNuvem });
        else if (nova != antes) Vendas.GravarConfig(cx, ChaveTokenNuvem, nova);
        if (nova is not null && TokenManual(cx) == nova)
            cx.Execute("DELETE FROM config WHERE chave=@C", new { C = ChaveToken });
        if (nova == antes) return "";
        Caixa.Auditar(cx, null, "chave_cozinha_da_nuvem", null, null,
            nova is null ? "a nuvem nao tem chave da TV da cozinha para esta loja"
                         : $"chave da TV da cozinha da nuvem, termina em {TerminaEm(nova)}");
        return "chave da TV da cozinha";
    }

    /// <summary>
    /// O que a Configuracao grava do campo de sobrescrita: vazio ou igual a chave da nuvem =
    /// sem sobrescrita (vale a da nuvem).
    /// </summary>
    public static void GravarManual(SqliteConnection cx, string? digitada)
    {
        var d = Limpo(digitada);
        if (d is null || d == TokenNuvem(cx)) cx.Execute("DELETE FROM config WHERE chave=@C", new { C = ChaveToken });
        else Vendas.GravarConfig(cx, ChaveToken, d);
    }

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
        => Corpo(vendaId, numero.ToString(CultureInfo.InvariantCulture), itens, destino, operador, cliente, criadoEm);

    /// <summary>Marca do corpo de um LOTE de comanda aberta (campo opcional "origem"; a TV antiga ignora).</summary>
    public const string OrigemComanda = "comanda";

    /// <summary>O numero que a TV mostra para o lote: "C12-2" (cabe em ^[0-9A-Za-z-]{1,12}$ e nao se confunde com a venda 12 do dia).</summary>
    public static string NumeroDoLote(long numeroConta, int n) => $"C{numeroConta}-{n}";

    /// <summary>
    /// O corpo do POST de um LOTE da comanda aberta (13/10/2026). O mesmo formato de <see cref="Corpo"/>
    /// (vendaId = loteId, numero "C12-2", cliente = nome da conta, consumo da conta, criadoEm do lote,
    /// itens como sempre) mais dois campos NOVOS e opcionais que o servidor antigo descarta sem erro:
    /// origem = "comanda" e comanda = {id, numero, nome, lote}. O Corpo da venda fica byte a byte igual.
    /// </summary>
    public static Dictionary<string, object?> CorpoDoLote(string loteId, string contaId, long numeroConta, string? nome, int n,
        IEnumerable<ItemDoLote> itens, DestinoCozinha destino, string? operador, DateTime criadoEm)
    {
        var linhas = itens.Select(i => new LinhaVenda(null, null, i.Nome, new Quantidade(i.QtdMilesimos), Dinheiro.Zero, Dinheiro.Zero,
            i.Unidade, null, null, null, null, 0, GratisMilesimos: i.Brinde ? i.QtdMilesimos : 0, Escolhas: Combos.DeJson(i.EscolhasJson)));
        var corpo = Corpo(loteId, NumeroDoLote(numeroConta, n), linhas, destino, operador, nome, criadoEm);
        corpo["origem"] = OrigemComanda;
        corpo["comanda"] = new Dictionary<string, object?>
        {
            ["id"] = contaId,
            ["numero"] = numeroConta.ToString(CultureInfo.InvariantCulture),
            ["nome"] = string.IsNullOrWhiteSpace(nome) ? null : nome.Trim(),
            ["lote"] = n,
        };
        return corpo;
    }

    /// <summary>O id do lote quando o payload da fila e de uma comanda aberta (origem "comanda"); null na venda de balcao ou em JSON estranho.</summary>
    public static string? LoteDeComanda(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (!r.TryGetProperty("origem", out var o) || o.ValueKind != JsonValueKind.String || o.GetString() != OrigemComanda) return null;
            return r.TryGetProperty("vendaId", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>O mesmo corpo, com o numero em texto (a venda manda o numero_local; o lote manda "C12-2").</summary>
    public static Dictionary<string, object?> Corpo(string vendaId, string numero, IEnumerable<LinhaVenda> itens,
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
            ["numero"] = numero,
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
