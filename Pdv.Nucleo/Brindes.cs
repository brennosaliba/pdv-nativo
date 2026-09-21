using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>Por que o brinde não saiu (ou não deu para saber). A tela traduz em uma linha.</summary>
public enum ErroBrinde
{
    Nenhum,
    /// <summary>formato_invalido: o código digitado não tem forma de AD-XXXXXX.</summary>
    FormatoInvalido,
    /// <summary>not_found.</summary>
    NaoAchou,
    /// <summary>not_scratched: a raspadinha existe, mas o cliente ainda não raspou.</summary>
    NaoRaspou,
    /// <summary>expired.</summary>
    Venceu,
    /// <summary>already_redeemed (a resposta traz quando e por quem).</summary>
    JaUsado,
    /// <summary>premio_sem_produtos: o prêmio ainda não tem regra de produto no painel.</summary>
    PremioSemProdutos,
    /// <summary>loja_sem_raspadinha: a loja do terminal não ligou a raspadinha no caixa.</summary>
    LojaSemRaspadinha,
    /// <summary>sem_permissao, ou 401/403 do PostgREST.</summary>
    SemPermissao,
    /// <summary>itens_fora_da_regra: produto que o prêmio não aceita.</summary>
    ItensForaDaRegra,
    /// <summary>quantidade_errada: a soma de uma regra não bate com o prêmio.</summary>
    QuantidadeErrada,
    /// <summary>homologacao (servidor) ou o modo de homologação ligado neste caixa.</summary>
    Homologacao,
    /// <summary>Nada saiu do caixa: sem sessão ou sem conexão. Certeza de que o servidor não fez nada.</summary>
    SemRede,
    /// <summary>A chamada saiu e a resposta se perdeu: o servidor pode ter feito ou não.</summary>
    NaoConfirmou,
    /// <summary>404 PGRST202: a nuvem ainda não tem a RPC (migration do ERP não aplicada).</summary>
    NuvemSemRecurso,
    /// <summary>A mesma chave devolveu um brinde que o painel já CANCELOU (brinde_cancelar): não entregue.</summary>
    Cancelado,
    Desconhecido,
}

/// <summary>Um produto que o prêmio aceita (pdv_products.id), com o nome e o preço da loja.</summary>
public sealed record OpcaoBrinde(string PdvProductId, string Nome, decimal? Preco);

/// <summary>
/// Uma regra do prêmio (raspadinha_premio_regras): "Cookie clássico: escolha 1" com as opções já
/// expandidas pela loja do terminal. Mesma forma de pdv_combo_regras, e por isso desce como grupo de
/// combo (<see cref="Brindes.ParaCombo"/>).
/// </summary>
public sealed record RegraBrinde(string Id, string Descricao, int Quantidade, IReadOnlyList<OpcaoBrinde> Opcoes);

/// <summary>O que brinde_raspadinha_conferir respondeu. <see cref="Ok"/> falso traz o erro tipado e o cru.</summary>
/// <param name="Mensagem">A linha que o servidor manda junto ('mensagem'). O caixa usa os textos dele
/// (versionados com o exe e vigiados pela suíte); esta só vale para erro que esta versão não conhece.</param>
public sealed record ConferenciaBrinde(bool Ok, ErroBrinde Erro, string? ErroCru, string? Codigo,
    string? Premio, string? PremioEmoji, string? Cliente, DateTime? ValidoAte,
    IReadOnlyList<RegraBrinde> Regras, DateTime? UsadoEm = null, string? UsadoPor = null, string? Mensagem = null);

/// <summary>Um item escolhido: o PRODUTO REAL (pdv_product_id), nunca o nome do prêmio.</summary>
public sealed record ItemBrinde(string PdvProductId, int Qtd, string? RegraId, string? Descricao = null);

/// <summary>O desfecho de uma tentativa de entrega, do ponto de vista de quem está no balcão.</summary>
public enum DesfechoBrinde
{
    /// <summary>O servidor confirmou (novo ou idempotente): pode entregar.</summary>
    Entregue,
    /// <summary>O servidor recusou (ou o caixa nem mandou, por item fora da regra): não entregue.</summary>
    Recusado,
    /// <summary>Saiu e a resposta se perdeu: não entregue ainda; "Tentar de novo" reusa a MESMA chave.</summary>
    Incerto,
    /// <summary>Nada saiu do caixa (sem internet, sem sessão, RPC ausente): não entregue.</summary>
    NaoEnviado,
    /// <summary>O caixa barrou antes de gravar qualquer coisa (modo de homologação).</summary>
    Bloqueado,
}

public sealed record EntregaBrinde(DesfechoBrinde Desfecho, ErroBrinde Erro, string? ErroCru,
    string? ClientKey, string? BrindeId, bool Idempotente, int Falhas, string? Mensagem = null);

/// <summary>
/// BRINDE DA RASPADINHA NO CAIXA (21/09/2026, pedido do dono).
///
/// "Cliente ganha um cookie clássico, promoção é validada, cliente escolhe qual quer, funcionário
/// coloca no PDV e ele não emite NF pois é promoção, mas abate do estoque." E, no mesmo dia: "pode
/// sair sem documento porque ele não tem venda pelo iFood".
///
/// O BRINDE NÃO É VENDA. Não entra na comanda, no rascunho, na NFC-e, no turno nem no pdv_sales.
/// É um registro próprio no servidor (tabela brindes), criado pela RPC brinde_raspadinha_entregar,
/// que queima o código e baixa o estoque pelo mesmo núcleo da venda, numa transação só. Este arquivo
/// não conhece a linha de venda nem a finalização da venda, e a suíte vigia isso.
///
/// AS REGRAS QUE OS JUÍZES DO DESENHO EXIGIRAM (21/09), e onde cada uma mora aqui:
///  · SEM INTERNET NÃO SE ENTREGA. Conferir e entregar só pela rede, na hora. Não existe entrega
///    offline: <see cref="DesfechoBrinde.Incerto"/> e <see cref="DesfechoBrinde.NaoEnviado"/> dizem
///    "não entregue".
///  · A client_key nasce e é GRAVADA no caixa (tabela brinde + fila) ANTES da chamada.
///    "Tentar de novo" reusa a MESMA chave e os MESMOS itens, e o servidor é idempotente por ela.
///  · A fila (tipo <see cref="TipoNaFila"/>) nunca cria brinde: ver <see cref="ResolverNaFilaAsync"/>.
///  · O caixa manda o produto real (pdv_product_id). O nome do prêmio não vai como item.
///  · Tudo com a sessão do TERMINAL (<see cref="Nuvem.RpcAsync"/>), nunca a chave pública, que é
///    como a cortesia fala (Cortesias.cs). A loja quem decide é o servidor, pelo usuário da sessão:
///    nenhuma chamada daqui manda loja.
///  · Modo de homologação bloqueia, antes de gravar ou chamar qualquer coisa.
/// </summary>
public sealed class Brindes
{
    public const string RpcConferir = "brinde_raspadinha_conferir";
    public const string RpcEntregar = "brinde_raspadinha_entregar";

    /// <summary>Tipo da linha na fila (Drenagem.TiposComHandler).</summary>
    public const string TipoNaFila = "brinde_entregar";

    /// <summary>Config local que liga o cartão da raspadinha na aba Promoções (vem do painel).</summary>
    public const string ChaveConfigLoja = "raspadinha_no_caixa";

    /// <summary>
    /// Quanto a fila espera por uma linha que a TELA ainda está enviando. A chamada da tela desiste
    /// em <see cref="PrazoEntregar"/>; passado isto sem desfecho, o caixa caiu no meio e a fila
    /// assume (sem nunca criar brinde, ver <see cref="ResolverNaFilaAsync"/>).
    /// </summary>
    public static readonly TimeSpan EsperaDaTela = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan PrazoConferir = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PrazoEntregar = TimeSpan.FromSeconds(15);

    private readonly Nuvem _nuvem;

    public Brindes(Nuvem nuvem) => _nuvem = nuvem;

    // ── A LOJA LIGOU? ────────────────────────────────────────────────────────

    /// <summary>A loja ligou a raspadinha no caixa (pdv_loja_config.raspadinha_no_caixa, copiada no Atualizar)?</summary>
    public static bool LigadoNaLoja(SqliteConnection cx) => Vendas.Config(cx, ChaveConfigLoja) == "1";

    // ── O CÓDIGO ─────────────────────────────────────────────────────────────

    /// <summary>
    /// O código como o servidor guarda: "AD-" e o sufixo, em maiúsculas. Aceita com ou sem "AD-",
    /// minúsculas, espaços e pontuação ("ad yjm5ab", "AD-YJM5AB", "yjm5ab"). Null quando não sobra
    /// forma de código (vazio, curto demais ou comprido demais): aí nem vale chamar a rede.
    ///
    /// Espelha _raspadinha_normalize_code (migration 20260531000000) com UMA diferença, de propósito:
    /// seis caracteres sem hífen são sempre o sufixo. O gerador (_raspadinha_redeem_short) faz
    /// sufixo de 6, e o servidor leria "ADXY23" como "AD-XY23" (código que não existe), quando é o
    /// sufixo de "AD-ADXY23". O servidor normaliza de novo e mantém o que chega com hífen.
    /// </summary>
    public static string? NormalizarCodigo(string? bruto)
    {
        if (string.IsNullOrWhiteSpace(bruto)) return null;
        var sb = new StringBuilder(bruto.Length);
        foreach (var ch in bruto.Trim().ToUpperInvariant())
            if (ch is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-') sb.Append(ch);
        var limpo = sb.ToString().Trim('-');
        if (limpo.Length == 0) return null;

        string codigo;
        if (limpo.StartsWith("AD-", StringComparison.Ordinal)) codigo = "AD-" + limpo[3..].Replace("-", "");
        else
        {
            var semHifen = limpo.Replace("-", "");
            if (semHifen.Length == 6) codigo = "AD-" + semHifen;
            else if (semHifen.StartsWith("AD", StringComparison.Ordinal) && semHifen.Length - 2 is >= 4 and <= 8)
                codigo = "AD-" + semHifen[2..];
            else codigo = "AD-" + semHifen;
        }
        var sufixo = codigo[3..];
        return sufixo.Length is >= 4 and <= 8 ? codigo : null;
    }

    // ── LEITURA DAS RESPOSTAS (pura; a suíte prova sem rede) ────────────────

    /// <summary>O texto de erro da RPC no tipo do caixa. Aceita os nomes em inglês do validador web.</summary>
    public static ErroBrinde LerErro(string? erro) => (erro ?? "").Trim().ToLowerInvariant() switch
    {
        "" => ErroBrinde.Desconhecido,
        "formato_invalido" or "invalid_format" or "code_required" => ErroBrinde.FormatoInvalido,
        "not_found" or "nao_encontrado" => ErroBrinde.NaoAchou,
        "not_scratched" or "nao_raspado" => ErroBrinde.NaoRaspou,
        "expired" or "vencido" => ErroBrinde.Venceu,
        "already_redeemed" or "ja_resgatado" or "ja_usado" => ErroBrinde.JaUsado,
        "premio_sem_produtos" => ErroBrinde.PremioSemProdutos,
        "loja_sem_raspadinha" => ErroBrinde.LojaSemRaspadinha,
        "sem_permissao" or "forbidden" => ErroBrinde.SemPermissao,
        "itens_fora_da_regra" => ErroBrinde.ItensForaDaRegra,
        "quantidade_errada" => ErroBrinde.QuantidadeErrada,
        "homologacao" => ErroBrinde.Homologacao,
        _ => ErroBrinde.Desconhecido,
    };

    /// <summary>404 com PGRST202: a função não existe no schema cache (migration do ERP ainda não aplicada).</summary>
    public static bool RpcAusente(int status, string? corpo)
        => status == 404 && corpo is not null && corpo.Contains("PGRST202", StringComparison.Ordinal);

    /// <summary>
    /// A resposta de brinde_raspadinha_conferir. Status: -1 = nada saiu (sem sessão/conexão),
    /// 0 = sem resposta. Tolerante a campo ausente, e aos nomes do validador web (code, prize_name,
    /// customer_name, expires_at, redeemed_at, redeemed_by_name). Prêmio que volta ok SEM regra, ou
    /// com regra sem opção, vira <see cref="ErroBrinde.PremioSemProdutos"/>: não há o que escolher,
    /// e o servidor recusaria a entrega.
    /// </summary>
    public static ConferenciaBrinde LerConferencia(int status, string? corpo)
    {
        if (status is -1 or 0 || status >= 500 || status is 408 or 425 or 429)
            return Falha(ErroBrinde.SemRede, status == 0 ? "sem resposta" : $"HTTP {status}");
        if (RpcAusente(status, corpo)) return Falha(ErroBrinde.NuvemSemRecurso, "PGRST202");
        if (status is 401 or 403) return Falha(ErroBrinde.SemPermissao, $"HTTP {status}");
        if (status is < 200 or >= 300) return Falha(ErroBrinde.Desconhecido, $"HTTP {status}: {Corta(corpo)}");

        try
        {
            using var doc = JsonDocument.Parse(corpo ?? "");
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0) r = r[0];
            if (r.ValueKind != JsonValueKind.Object) return Falha(ErroBrinde.Desconhecido, "resposta sem objeto");

            var ok = r.TryGetProperty("ok", out var okv) && okv.ValueKind == JsonValueKind.True;
            var erroCru = Texto(r, "erro") ?? Texto(r, "error");
            var codigo = Texto(r, "codigo") ?? Texto(r, "code");
            var premio = Texto(r, "premio") ?? Texto(r, "prize_name");
            var emoji = Texto(r, "premio_emoji") ?? Texto(r, "prize_emoji");
            var cliente = PrimeiroNome(Texto(r, "cliente") ?? Texto(r, "customer_name"));
            // 'venceu_em' é como o SQL 37 manda a data na recusa 'expired'
            var validoAte = Data(r, "valido_ate") ?? Data(r, "venceu_em") ?? Data(r, "expires_at");
            var usadoEm = Data(r, "quando") ?? Data(r, "usado_em") ?? Data(r, "redeemed_at");
            var usadoPor = Texto(r, "por") ?? Texto(r, "usado_por") ?? Texto(r, "quem") ?? Texto(r, "redeemed_by_name");
            var regras = LerRegras(r);
            var mensagem = Texto(r, "mensagem");

            if (!ok)
                return new ConferenciaBrinde(false, LerErro(erroCru), erroCru, codigo, premio, emoji, cliente,
                    validoAte, regras, usadoEm, usadoPor, mensagem);
            if (regras.Count == 0 || regras.Any(g => g.Opcoes.Count == 0))
                return new ConferenciaBrinde(false, ErroBrinde.PremioSemProdutos, "premio_sem_produtos", codigo,
                    premio, emoji, cliente, validoAte, regras);
            return new ConferenciaBrinde(true, ErroBrinde.Nenhum, null, codigo, premio, emoji, cliente, validoAte, regras);
        }
        catch { return Falha(ErroBrinde.Desconhecido, "resposta ilegível"); }

        static ConferenciaBrinde Falha(ErroBrinde e, string cru)
            => new(false, e, cru, null, null, null, null, null, Array.Empty<RegraBrinde>());
    }

    private static List<RegraBrinde> LerRegras(JsonElement r)
    {
        var regras = new List<RegraBrinde>();
        if (!r.TryGetProperty("regras", out var arr) || arr.ValueKind != JsonValueKind.Array) return regras;
        foreach (var g in arr.EnumerateArray())
        {
            if (g.ValueKind != JsonValueKind.Object) continue;
            var id = Texto(g, "id");
            if (id is null) continue;
            var qtd = Inteiro(g, "quantidade") ?? 1;
            if (qtd < 1) qtd = 1;
            var opcoes = new List<OpcaoBrinde>();
            if (g.TryGetProperty("opcoes", out var ops) && ops.ValueKind == JsonValueKind.Array)
                foreach (var o in ops.EnumerateArray())
                {
                    if (o.ValueKind != JsonValueKind.Object) continue;
                    var pid = Texto(o, "pdv_product_id") ?? Texto(o, "produto_id") ?? Texto(o, "id");
                    if (pid is null || opcoes.Any(x => x.PdvProductId == pid)) continue;
                    opcoes.Add(new OpcaoBrinde(pid, Texto(o, "nome") ?? Texto(o, "name") ?? "PRODUTO", Decimal(o, "preco")));
                }
            regras.Add(new RegraBrinde(id, Texto(g, "descricao") ?? "Escolha", qtd, opcoes));
        }
        return regras;
    }

    /// <summary>
    /// A resposta de brinde_raspadinha_entregar, no desfecho do balcão:
    ///  · -1 (nada saiu), 401/403 (o PostgREST barrou antes de rodar) e 404 PGRST202 (a função não
    ///    existe): <see cref="DesfechoBrinde.NaoEnviado"/>, certeza de que nada foi gravado;
    ///  · 0, 5xx, 408, 425, 429 e corpo ilegível: <see cref="DesfechoBrinde.Incerto"/>, a chamada
    ///    pode ter rodado; a tela diz "não entregue ainda" e o "Tentar de novo" reusa a chave;
    ///  · outros 4xx (ex.: 400 de tipo): <see cref="DesfechoBrinde.Recusado"/> (a transação não rodou);
    ///  · 2xx com ok: <see cref="DesfechoBrinde.Entregue"/> (novo ou idempotente); sem ok: Recusado.
    /// </summary>
    public static EntregaBrinde LerEntrega(int status, string? corpo, string? clientKey)
    {
        EntregaBrinde D(DesfechoBrinde d, ErroBrinde e, string? cru) => new(d, e, cru, clientKey, null, false, 0);

        if (status == -1) return D(DesfechoBrinde.NaoEnviado, ErroBrinde.SemRede, "nada saiu do caixa");
        if (RpcAusente(status, corpo)) return D(DesfechoBrinde.NaoEnviado, ErroBrinde.NuvemSemRecurso, "PGRST202");
        if (status is 401 or 403) return D(DesfechoBrinde.NaoEnviado, ErroBrinde.SemPermissao, $"HTTP {status}");
        if (status == 0 || status >= 500 || status is 408 or 425 or 429)
            return D(DesfechoBrinde.Incerto, ErroBrinde.NaoConfirmou, status == 0 ? "sem resposta" : $"HTTP {status}");
        if (status is < 200 or >= 300) return D(DesfechoBrinde.Recusado, ErroBrinde.Desconhecido, $"HTTP {status}: {Corta(corpo)}");

        try
        {
            using var doc = JsonDocument.Parse(corpo ?? "");
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0) r = r[0];
            if (r.ValueKind != JsonValueKind.Object) return D(DesfechoBrinde.Incerto, ErroBrinde.NaoConfirmou, "resposta sem objeto");
            var ok = r.TryGetProperty("ok", out var okv) && okv.ValueKind == JsonValueKind.True;
            if (!ok)
            {
                var cru = Texto(r, "erro") ?? Texto(r, "error");
                return D(DesfechoBrinde.Recusado, LerErro(cru), cru ?? Corta(corpo)) with { Mensagem = Texto(r, "mensagem") };
            }
            var idempotente = r.TryGetProperty("idempotente", out var idv) && idv.ValueKind == JsonValueKind.True;
            // A mesma chave devolve o brinde como ele está AGORA. Cancelado no painel (a resposta
            // tinha se perdido e o gerente cancelou antes do "Tentar de novo"): não entregue.
            if (idempotente && string.Equals(Texto(r, "status"), "cancelado", StringComparison.OrdinalIgnoreCase))
                return new EntregaBrinde(DesfechoBrinde.Recusado, ErroBrinde.Cancelado, "cancelado", clientKey,
                    Texto(r, "brinde_id"), true, 0);
            var falhas = 0;
            if (r.TryGetProperty("falhas", out var f))
                falhas = f.ValueKind switch
                {
                    JsonValueKind.Number when f.TryGetInt32(out var n) => n,
                    JsonValueKind.Array => f.GetArrayLength(),
                    _ => 0,
                };
            return new EntregaBrinde(DesfechoBrinde.Entregue, ErroBrinde.Nenhum, null, clientKey,
                Texto(r, "brinde_id"), idempotente, falhas);
        }
        catch { return D(DesfechoBrinde.Incerto, ErroBrinde.NaoConfirmou, "resposta ilegível"); }
    }

    // ── O PRÊMIO COMO COMBO (o diálogo de combo do caixa é reaproveitado) ────

    /// <summary>
    /// O prêmio no formato que o <c>DialogoCombo</c> já desenha: uma regra = um grupo, com mínimo e
    /// máximo iguais à quantidade, e as opções como lista fixa. "Cookie clássico: escolha 1" mostra
    /// New York, Tradicional e Triplo Chocolate; "Caixa com 2 Donuts" pede 2 dos donuts clássicos.
    /// </summary>
    public static Combos.ComboDef ParaCombo(ConferenciaBrinde c)
        => new("brinde:" + (c.Codigo ?? ""), null, string.IsNullOrWhiteSpace(c.Premio) ? "Brinde" : c.Premio!,
            c.Regras.Select(g => new Combos.GrupoDef(g.Id, g.Descricao, g.Quantidade, g.Quantidade,
                new Combos.Fonte("itens", null,
                    g.Opcoes.Select(o => new Combos.ItemFonte(o.PdvProductId, null, o.Nome)).ToList()))).ToList());

    /// <summary>
    /// Quando toda regra tem UMA opção só ("Duo de Brownie": os dois fixos), não há o que escolher:
    /// as escolhas já estão decididas e a tela pula o diálogo. Null quando alguma regra pede escolha.
    /// </summary>
    public static List<Escolha>? EscolhasFixas(ConferenciaBrinde c)
    {
        if (c.Regras.Count == 0 || c.Regras.Any(g => g.Opcoes.Count != 1)) return null;
        return c.Regras.Select(g => new Escolha(g.Opcoes[0].PdvProductId, null, g.Opcoes[0].Nome, g.Id,
            g.Quantidade, g.Descricao)).ToList();
    }

    /// <summary>As escolhas do diálogo como itens da RPC: produto real, quantidade e regra.</summary>
    public static List<ItemBrinde> ItensDe(IEnumerable<Escolha> escolhas)
        => escolhas.Where(e => e.Qtd > 0)
            .GroupBy(e => (e.ProdutoId, e.GrupoId))
            .Select(g => new ItemBrinde(g.Key.ProdutoId, g.Sum(e => e.Qtd), g.Key.GrupoId, g.First().Nome))
            .ToList();

    /// <summary>
    /// Confere os itens contra as regras ANTES de chamar (o servidor confere de novo): produto que a
    /// regra não lista é <see cref="ErroBrinde.ItensForaDaRegra"/>; soma que não fecha a quantidade é
    /// <see cref="ErroBrinde.QuantidadeErrada"/>. Nenhum item também é quantidade errada.
    /// </summary>
    public static ErroBrinde ConferirItens(ConferenciaBrinde c, IReadOnlyList<ItemBrinde> itens)
    {
        if (itens.Count == 0) return ErroBrinde.QuantidadeErrada;
        foreach (var i in itens)
        {
            if (i.Qtd <= 0) return ErroBrinde.QuantidadeErrada;
            var regra = c.Regras.FirstOrDefault(g => g.Id == i.RegraId);
            if (regra is null || regra.Opcoes.All(o => o.PdvProductId != i.PdvProductId)) return ErroBrinde.ItensForaDaRegra;
        }
        foreach (var g in c.Regras)
            if (itens.Where(i => i.RegraId == g.Id).Sum(i => i.Qtd) != g.Quantidade) return ErroBrinde.QuantidadeErrada;
        return ErroBrinde.Nenhum;
    }

    // ── TEXTOS DE TELA (uma linha cada; teto de 4 linhas vigiado pela suíte) ─

    public const string TextoEntregue = "Brinde entregue. Não passe na comanda.";
    public const string TextoIncerto = "Não deu para confirmar. Não entregue ainda. Tente de novo.";
    public const string TextoHomologacao = "Modo de homologação: brinde não sai.";
    public const string TextoSemCodigo = "Digite o código do cliente.";

    /// <summary>A tentativa deste caixa que ficou sem resposta, quando o mesmo código volta a ser conferido.</summary>
    public static string TextoPendente(string? resumo)
        => string.IsNullOrWhiteSpace(resumo)
            ? "Ficou sem resposta. Não entregue ainda."
            : $"Ficou sem resposta: {resumo}. Não entregue ainda.";

    /// <summary>A linha do prêmio que vale: "🍪 1 Cookie Clássico · Maria · vale até 05/10".</summary>
    public static string LinhaDoPremio(ConferenciaBrinde c)
    {
        var partes = new List<string>();
        var premio = $"{c.PremioEmoji} {c.Premio}".Trim();
        partes.Add(premio.Length > 0 ? premio : "Brinde");
        if (!string.IsNullOrWhiteSpace(c.Cliente)) partes.Add(c.Cliente!);
        if (c.ValidoAte is { } ate) partes.Add($"vale até {ate:dd/MM}");
        return string.Join(" · ", partes);
    }

    /// <summary>A recusa da conferência, em uma linha.</summary>
    public static string TextoDaConferencia(ConferenciaBrinde c) => c.Erro switch
    {
        ErroBrinde.Nenhum => LinhaDoPremio(c),
        ErroBrinde.FormatoInvalido => "Código incompleto. Confira e digite de novo.",
        // Cortesia e raspadinha usam o mesmo formato AD-XXXXXX (juiz, 21/09): quem não achou aqui
        // pode estar com um cupom de cortesia na mão.
        ErroBrinde.NaoAchou => "Não achei esse código. Se for cortesia, use o botão da comanda.",
        ErroBrinde.NaoRaspou => "O cliente ainda não raspou.",
        ErroBrinde.Venceu => c.ValidoAte is { } d ? $"Este código venceu em {d:dd/MM}." : "Este código venceu.",
        ErroBrinde.JaUsado => TextoJaUsado(c.UsadoEm, c.UsadoPor),
        ErroBrinde.PremioSemProdutos => "Este prêmio ainda não tem produtos no painel.",
        ErroBrinde.LojaSemRaspadinha => "A raspadinha não está ligada nesta loja.",
        ErroBrinde.SemPermissao => "Este caixa não pode conferir brinde. Fale com o gerente.",
        ErroBrinde.Homologacao => TextoHomologacao,
        ErroBrinde.SemRede => "Sem internet para conferir. Tente de novo.",
        ErroBrinde.NuvemSemRecurso => "O painel ainda não tem o brinde. Fale com o suporte.",
        _ => DoServidor(c.Mensagem) ?? "Não deu para conferir agora. Tente de novo.",
    };

    /// <summary>
    /// A linha do servidor, só para erro que esta versão não conhece, e só se ela obedece a regra
    /// da tela (uma linha curta, sem travessão). Senão, o texto de sempre do caixa.
    /// </summary>
    private static string? DoServidor(string? m)
        => m is { Length: > 0 and <= 90 } && !m.Contains('\n') && !m.Contains('—') && !m.Contains('–') ? m : null;

    private static string TextoJaUsado(DateTime? quando, string? por)
    {
        if (quando is not { } u) return "Este código já foi usado.";
        // 21/09 (revisão): o 'por' vem de raspadinha_validate_code, que prefere o E-MAIL de
        // auth.users quando o resgate foi pelo ERP (redeemed_by preenchido). E-mail de funcionário
        // não vai para a tela do balcão: aí a linha sai sem o "por".
        var quem = string.IsNullOrWhiteSpace(por) || por.Contains('@') ? "" : $" por {PrimeiroNome(por)}";
        return $"Este código já foi usado em {u:dd/MM} às {u:HH:mm}{quem}.";
    }

    /// <summary>O desfecho da entrega, em uma linha. Nunca "entregue" numa resposta de erro.</summary>
    public static string TextoDaEntrega(EntregaBrinde e) => e.Desfecho switch
    {
        DesfechoBrinde.Entregue => TextoEntregue,
        DesfechoBrinde.Incerto => TextoIncerto,
        DesfechoBrinde.Bloqueado => TextoHomologacao,
        DesfechoBrinde.NaoEnviado => e.Erro switch
        {
            ErroBrinde.NuvemSemRecurso => "O painel ainda não tem o brinde. Não entregue.",
            ErroBrinde.SemPermissao => "Este caixa não pode entregar brinde. Não entregue.",
            _ => "Sem internet. Não entregue.",
        },
        _ => e.Erro switch
        {
            ErroBrinde.JaUsado => "Este código acabou de ser usado. Não entregue.",
            ErroBrinde.Venceu => "Este código venceu. Não entregue.",
            ErroBrinde.ItensForaDaRegra => "Esse produto não vale para este prêmio. Escolha de novo.",
            ErroBrinde.QuantidadeErrada => "A quantidade não bate com o prêmio. Escolha de novo.",
            ErroBrinde.Homologacao => TextoHomologacao,
            ErroBrinde.LojaSemRaspadinha => "A raspadinha não está ligada nesta loja. Não entregue.",
            ErroBrinde.PremioSemProdutos => "Este prêmio ainda não tem produtos no painel. Não entregue.",
            ErroBrinde.Cancelado => "Este brinde foi cancelado no painel. Não entregue.",
            // erro que esta versão não conhece: a linha do servidor, desde que não diga "entregue"
            _ => DoServidor(e.Mensagem) is { } m
                 && (!m.Contains("entregue", StringComparison.OrdinalIgnoreCase) || m.Contains("Não entregue", StringComparison.Ordinal))
                ? m : "O painel recusou o brinde. Não entregue.",
        },
    };

    /// <summary>"1 COOKIE NEW YORK" / "1 BROWNIE AMERICAN DAY e 1 BROWNIE NUTELLA".</summary>
    public static string Resumo(IEnumerable<Escolha> escolhas)
    {
        var partes = escolhas.Where(e => e.Qtd > 0).GroupBy(e => e.ProdutoId)
            .Select(g => $"{g.Sum(e => e.Qtd)} {g.First().Nome.Trim()}").ToList();
        return partes.Count switch
        {
            0 => "",
            1 => partes[0],
            _ => string.Join(", ", partes.Take(partes.Count - 1)) + " e " + partes[^1],
        };
    }

    /// <summary>A pergunta de uma linha antes de entregar.</summary>
    public static string PerguntaDeEntrega(IEnumerable<Escolha> escolhas)
        => $"Entregar {Resumo(escolhas)} de brinde? O código deixa de valer.";

    // ── CONFERIR E ENTREGAR (rede, com a sessão do terminal) ────────────────

    /// <summary>Confere o código (não queima nada). Sem rede devolve <see cref="ErroBrinde.SemRede"/>.</summary>
    public async Task<ConferenciaBrinde> ConferirAsync(string? digitado, CancellationToken ct = default)
    {
        var codigo = NormalizarCodigo(digitado);
        if (codigo is null)
            return new ConferenciaBrinde(false, ErroBrinde.FormatoInvalido, "formato_invalido", null, null, null, null,
                null, Array.Empty<RegraBrinde>());
        using (var cx = Banco.Abrir())
            if (ModoHomologacao.Ligado(cx))
                return new ConferenciaBrinde(false, ErroBrinde.Homologacao, "homologacao", codigo, null, null, null,
                    null, Array.Empty<RegraBrinde>());
        var (st, corpo) = await _nuvem.RpcAsync(RpcConferir, JsonSerializer.Serialize(new { _code = codigo }),
            PrazoConferir, ct).ConfigureAwait(false);
        var c = LerConferencia(st, corpo);
        return c with { Codigo = c.Codigo ?? codigo };
    }

    /// <summary>
    /// Entrega o brinde conferido. Na ordem: bloqueios locais (homologação, itens fora da regra) sem
    /// gravar nada; depois GRAVA a linha do brinde e a da fila com a client_key nova, numa transação;
    /// só então chama a RPC. O desfecho vai para a linha local e para a auditoria.
    /// </summary>
    public async Task<EntregaBrinde> EntregarAsync(ConferenciaBrinde c, IReadOnlyList<Escolha> escolhas,
        Operador operador, string? sessaoId, CancellationToken ct = default)
    {
        if (!c.Ok || string.IsNullOrWhiteSpace(c.Codigo))
            return new EntregaBrinde(DesfechoBrinde.Recusado, c.Erro == ErroBrinde.Nenhum ? ErroBrinde.Desconhecido : c.Erro,
                "conferência sem ok", null, null, false, 0);
        var itens = ItensDe(escolhas);

        string clientKey;
        using (var cx = Banco.Abrir())
        {
            if (ModoHomologacao.Ligado(cx))
                return new EntregaBrinde(DesfechoBrinde.Bloqueado, ErroBrinde.Homologacao, "homologacao", null, null, false, 0);
            var erroItens = ConferirItens(c, itens);
            if (erroItens != ErroBrinde.Nenhum)
                return new EntregaBrinde(DesfechoBrinde.Recusado, erroItens, "conferido no caixa", null, null, false, 0);

            clientKey = Guid.NewGuid().ToString();
            var terminal = cx.ExecuteScalar<string?>("SELECT terminal_uuid FROM terminal LIMIT 1");
            var payload = new Dictionary<string, object?>
            {
                ["_code"] = c.Codigo,
                ["_itens"] = itens.Select(i => new Dictionary<string, object?>
                {
                    ["pdv_product_id"] = i.PdvProductId, ["qtd"] = i.Qtd, ["regra_id"] = i.RegraId,
                }).ToList(),
                // _operador_id é uuid na RPC: operador criado só neste caixa (id local) vai nulo,
                // senão o PostgREST recusa o corpo inteiro (22P02) e o brinde nem roda.
                ["_operador_id"] = Guid.TryParse(operador.Id, out _) ? operador.Id : null,
                ["_operador_nome"] = operador.Nome,
                ["_terminal_uuid"] = terminal,
            };
            var resumo = Resumo(escolhas);
            var agora = DateTime.Now.ToString("o");
            using var tx = cx.BeginTransaction();
            cx.Execute("""
                INSERT INTO brinde (client_key, codigo, premio, payload, resumo, operador_id, sessao_id,
                                    situacao, criado_em, tentado_em)
                VALUES (@K, @C, @P, @J, @R, @O, @S, 'enviando', @Em, @Em)
                """, new { K = clientKey, C = c.Codigo, P = c.Premio, J = JsonSerializer.Serialize(payload),
                           R = resumo, O = operador.Id, S = sessaoId, Em = agora }, tx);
            // A fila nasce JUNTO, na mesma transação (outbox transacional): se o caixa cair no meio
            // da chamada, a linha já existe e a fila descobre o desfecho sem criar nada.
            Caixa.Enfileirar(cx, tx, TipoNaFila, clientKey, clientKey, new { codigo = c.Codigo });
            Caixa.Auditar(cx, tx, "brinde_pedido", operador.Id, null,
                $"codigo={c.Codigo} client_key={clientKey} premio={c.Premio} itens={resumo}");
            tx.Commit();
        }
        return await EnviarAsync(clientKey, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// "Tentar de novo": a MESMA client_key e os MESMOS itens gravados antes. Se a primeira chamada
    /// tinha chegado, o servidor devolve o mesmo brinde (idempotente); se não, entrega agora.
    /// </summary>
    public Task<EntregaBrinde> TentarDeNovoAsync(string clientKey, CancellationToken ct = default)
        => EnviarAsync(clientKey, ct);

    private async Task<EntregaBrinde> EnviarAsync(string clientKey, CancellationToken ct)
    {
        string payload;
        using (var cx = Banco.Abrir())
        {
            var linha = cx.QueryFirstOrDefault("SELECT payload, situacao, brinde_id FROM brinde WHERE client_key = @K",
                new { K = clientKey });
            if (linha is null)
                return new EntregaBrinde(DesfechoBrinde.Recusado, ErroBrinde.Desconhecido, "brinde local sumiu", clientKey, null, false, 0);
            if ((string)linha.situacao == "entregue")
                return new EntregaBrinde(DesfechoBrinde.Entregue, ErroBrinde.Nenhum, null, clientKey, linha.brinde_id as string, true, 0);
            payload = (string)linha.payload;
            // 'enviando' antes de sair: a fila vê que a tela está com a linha e não mexe nela.
            cx.Execute("UPDATE brinde SET situacao = 'enviando', tentado_em = @Em WHERE client_key = @K",
                new { Em = DateTime.Now.ToString("o"), K = clientKey });
        }

        var (st, resp) = await _nuvem.RpcAsync(RpcEntregar, CorpoEntregar(payload, clientKey), PrazoEntregar, ct)
            .ConfigureAwait(false);
        var e = LerEntrega(st, resp, clientKey);
        using (var cx = Banco.Abrir()) Registrar(cx, e, "tela");
        return e;
    }

    /// <summary>O corpo da RPC: o payload gravado mais a client_key (a chave é sempre a da linha).</summary>
    internal static string CorpoEntregar(string payload, string clientKey)
    {
        var corpo = System.Text.Json.Nodes.JsonNode.Parse(payload)?.AsObject() ?? new System.Text.Json.Nodes.JsonObject();
        corpo["_client_key"] = clientKey;
        return corpo.ToJsonString();
    }

    /// <summary>Grava o desfecho na linha local e na auditoria. Não escreve na fila (só o laço da Drenagem escreve lá).</summary>
    private static void Registrar(SqliteConnection cx, EntregaBrinde e, string quem)
    {
        var situacao = e.Desfecho switch
        {
            DesfechoBrinde.Entregue => "entregue",
            DesfechoBrinde.Incerto => "incerto",
            DesfechoBrinde.NaoEnviado => "nao_enviado",
            _ => "recusado",
        };
        var agora = DateTime.Now.ToString("o");
        cx.Execute("""
            UPDATE brinde SET situacao = @S, erro = @E, brinde_id = COALESCE(@B, brinde_id),
                              resolvido_em = CASE WHEN @S = 'incerto' THEN NULL ELSE @Em END
             WHERE client_key = @K
            """, new { S = situacao, E = e.ErroCru, B = e.BrindeId, Em = agora, K = e.ClientKey });
        var evento = e.Desfecho switch
        {
            DesfechoBrinde.Entregue => "brinde_entregue",
            DesfechoBrinde.Incerto => "brinde_sem_resposta",
            DesfechoBrinde.NaoEnviado => "brinde_nao_enviado",
            _ => "brinde_recusado",
        };
        var codigo = cx.ExecuteScalar<string?>("SELECT codigo FROM brinde WHERE client_key = @K", new { K = e.ClientKey });
        // Sem desfecho de novo (um "Tentar de novo" que também se perdeu): precisa de uma linha VIVA
        // na fila para esta chave. A da entrega pode já ter saído, resolvida quando a tela tinha
        // desfecho. Inserir é de quem cria o fato (como a venda); atualizar continua só com o laço.
        if (e.Desfecho == DesfechoBrinde.Incerto && e.ClientKey is not null
            && cx.ExecuteScalar<long>("""
                SELECT COUNT(*) FROM outbox WHERE tipo = @T AND client_key = @K
                   AND enviado_em IS NULL AND desistido_em IS NULL AND descartado_em IS NULL
                """, new { T = TipoNaFila, K = e.ClientKey }) == 0)
            Caixa.Enfileirar(cx, null, TipoNaFila, e.ClientKey, e.ClientKey, new { codigo });
        Caixa.Auditar(cx, null, evento, null, null,
            $"{quem}: codigo={codigo} client_key={e.ClientKey} brinde_id={e.BrindeId} idempotente={e.Idempotente} "
            + $"falhas={e.Falhas} erro={e.ErroCru}");
    }

    /// <summary>A tentativa que ficou sem desfecho para este código, se houver (a tela oferece "Tentar de novo").</summary>
    public static (string ClientKey, string Resumo)? Pendente(SqliteConnection cx, string codigo)
    {
        var r = cx.QueryFirstOrDefault<(string ClientKey, string? Resumo)>("""
            SELECT client_key, resumo FROM brinde
             WHERE codigo = @C AND situacao IN ('incerto','enviando')
             ORDER BY criado_em DESC LIMIT 1
            """, new { C = codigo });
        return r.ClientKey is null ? null : (r.ClientKey, r.Resumo ?? "");
    }

    // ── A FILA: descobre o desfecho, NUNCA cria brinde ──────────────────────

    /// <summary>
    /// O que a Drenagem faz com uma linha <see cref="TipoNaFila"/>. Desfecho no contrato da fila:
    /// (true) resolvido, (false) recusa permanente, (null) transitório.
    ///
    /// ⚠️ A FILA NÃO PODE CRIAR BRINDE. Sem entrega offline, a tela disse "não entregue ainda" quando
    /// a resposta se perdeu. Se a primeira chamada NÃO tinha chegado ao servidor, reenviar a chave
    /// daqui a meia hora queimaria o código e baixaria o estoque de um prêmio que o cliente não
    /// levou, e ele voltaria amanhã para ouvir "já foi usado". Então a fila CONFERE antes:
    ///  · o código ainda vale (ou venceu, ou nem foi raspado, ou o prêmio está sem produto): a primeira
    ///    chamada não chegou. Nada é reenviado; a linha local vira 'nao_entregue', mas só depois de
    ///    <see cref="EsperaDaTela"/> desde a chamada (antes disso ela pode estar rodando no servidor);
    ///  · a loja desligou a raspadinha (o conferir nem olha o código): a mesma chave é reenviada,
    ///    que a RPC devolve o brinde dela ou recusa pela loja antes de criar;
    ///  · o código já foi usado: só aí a MESMA chave é reenviada. Com o código queimado, a RPC não tem
    ///    como criar nada: ou devolve o brinde desta chave (idempotente, e a linha vira 'entregue'), ou
    ///    recusa (foi usado em outro lugar, e a linha vira 'nao_entregue').
    /// Linha que a tela já resolveu sai da fila sem chamada; linha que a tela ainda está enviando espera.
    /// </summary>
    public static async Task<(bool? Ok, string? Erro)> ResolverNaFilaAsync(string clientKey,
        Func<string, string, Task<(int Status, string? Corpo)>> rpc, DateTime agora)
    {
        dynamic? linha;
        using (var cx = Banco.Abrir())
            linha = cx.QueryFirstOrDefault("SELECT codigo, payload, situacao, erro, tentado_em FROM brinde WHERE client_key = @K",
                new { K = clientKey });
        if (linha is null) return (false, "brinde local sumiu: nada a reenviar");

        var situacao = (string)linha.situacao;
        // quando a última chamada da tela saiu (EnviarAsync carimba antes de chamar)
        var recente = DateTime.TryParse(linha.tentado_em as string, CultureInfo.InvariantCulture,
                          DateTimeStyles.RoundtripKind, out DateTime em) && em > agora - EsperaDaTela;
        switch (situacao)
        {
            case "entregue": return (true, "brinde confirmado na tela");
            case "recusado": return (true, $"recusado na tela ({linha.erro as string}): nada a reenviar");
            case "nao_enviado": return (true, "não saiu do caixa: nada a reenviar");
            case "nao_entregue": return (true, "já resolvido: não entregue");
            case "enviando":
                if (recente) return (null, "a tela ainda está enviando este brinde");
                break;
        }

        var codigo = (string)linha.codigo;
        var (st, corpo) = await rpc(RpcConferir, JsonSerializer.Serialize(new { _code = codigo })).ConfigureAwait(false);
        if (RpcAusente(st, corpo)) return (null, $"a nuvem ainda não tem {RpcConferir} (aplicar a migration do ERP)");
        if (st is < 200 or >= 300) return Drenagem.DesfechoDeStatus(st, corpo);

        var c = LerConferencia(st, corpo);
        // Se a primeira chamada tivesse chegado, o código estaria 'redeemed' e a conferência diria
        // "já usado". Qualquer outra resposta de código (vale, venceu, não raspou, não existe, prêmio
        // sem produto) prova que ela não chegou.
        if (c.Ok || c.Erro is ErroBrinde.Venceu or ErroBrinde.NaoRaspou or ErroBrinde.PremioSemProdutos
                or ErroBrinde.NaoAchou or ErroBrinde.FormatoInvalido)
        {
            // 21/09 (revisão): "não chegou" só depois do prazo da tela. Logo depois da resposta
            // perdida a primeira chamada pode AINDA estar rodando no servidor (a rede caiu no meio e
            // voltou, e a volta da rede cutuca a fila na hora): o conferir leria o código sem uso, a
            // linha viraria 'nao_entregue' e, com o commit logo depois, o código ficaria queimado e o
            // estoque baixado para um brinde que o caixa deu como não entregue. Até o prazo a linha
            // continua pendente (a tela segue oferecendo "Tentar de novo" com a mesma chave).
            if (recente) return (null, "esperando o prazo da tela antes de dizer que a primeira chamada não chegou");
            Resolver(clientKey, "nao_entregue", "a primeira chamada não chegou: o código continua sem uso", null,
                "brinde_fila_nao_chegou");
            return (true, "a primeira chamada não chegou; nada reenviado");
        }
        // 21/09 (revisão): loja desligada no painel depois da resposta perdida. O conferir recusa
        // pela loja ANTES de olhar o código e não diz mais nada; a fila desistia com a linha
        // 'incerto' para sempre. O reenvio da MESMA chave é seguro aqui: no SQL 37 a idempotência
        // vem antes da chave da loja (devolve o brinde gravado) e, com a loja desligada, a RPC
        // recusa antes de criar qualquer coisa.
        if (c.Erro is not (ErroBrinde.JaUsado or ErroBrinde.LojaSemRaspadinha))
            return (false, $"conferir recusou: {c.ErroCru}");

        var (st2, corpo2) = await rpc(RpcEntregar, CorpoEntregar((string)linha.payload, clientKey)).ConfigureAwait(false);
        var e = LerEntrega(st2, corpo2, clientKey);
        switch (e.Desfecho)
        {
            case DesfechoBrinde.Entregue:
                Resolver(clientKey, "entregue", null, e.BrindeId,
                    e.Idempotente ? "brinde_confirmado_pela_fila" : "brinde_criado_pela_fila");
                // Não idempotente com o código já queimado só acontece se alguém reabriu o código
                // (brinde_cancelar com devolver) entre as duas chamadas. Fica dito na auditoria.
                return (true, e.Idempotente ? null : "a nuvem criou o brinde agora (o código tinha sido reaberto)");
            case DesfechoBrinde.Recusado when e.Erro == ErroBrinde.Cancelado:
                Resolver(clientKey, "nao_entregue", "o brinde desta chave foi cancelado no painel", e.BrindeId,
                    "brinde_fila_cancelado");
                return (true, "o brinde desta chave foi cancelado no painel");
            case DesfechoBrinde.Recusado when e.Erro == ErroBrinde.JaUsado:
                Resolver(clientKey, "nao_entregue", $"o código foi usado fora deste caixa ({e.ErroCru})", null,
                    "brinde_fila_usado_fora");
                return (true, "o código foi usado fora deste caixa: nada foi criado aqui");
            case DesfechoBrinde.Recusado:
                // a mesma chave não achou brinde e a RPC recusou antes de criar (ex.: loja desligada)
                Resolver(clientKey, "nao_entregue", $"a mesma chave não tem brinde na nuvem ({e.ErroCru})", null,
                    "brinde_fila_recusado");
                return (true, $"a mesma chave não tem brinde na nuvem ({e.ErroCru}): nada foi criado");
            case DesfechoBrinde.NaoEnviado when e.Erro == ErroBrinde.NuvemSemRecurso:
                return (null, $"a nuvem ainda não tem {RpcEntregar} (aplicar a migration do ERP)");
            case DesfechoBrinde.NaoEnviado when e.Erro == ErroBrinde.SemPermissao:
                return (false, $"reenvio recusado: {e.ErroCru}");
            default:
                return (null, $"sem resposta ao reenviar: {e.ErroCru}");
        }
    }

    /// <summary>A fila só escreve na linha que ninguém mais está mexendo ('incerto' ou 'enviando' parado).</summary>
    private static void Resolver(string clientKey, string situacao, string? erro, string? brindeId, string evento)
    {
        using var cx = Banco.Abrir();
        var n = cx.Execute("""
            UPDATE brinde SET situacao = @S, erro = COALESCE(@E, erro), brinde_id = COALESCE(@B, brinde_id), resolvido_em = @Em
             WHERE client_key = @K AND situacao IN ('incerto','enviando')
            """, new { S = situacao, E = erro, B = brindeId, Em = DateTime.Now.ToString("o"), K = clientKey });
        if (n > 0)
            Caixa.Auditar(cx, null, evento, null, null, $"fila: client_key={clientKey} situacao={situacao} brinde_id={brindeId} {erro}");
    }

    // ── json ─────────────────────────────────────────────────────────────────
    private static string? Texto(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
           && v.GetString() is { } s && s.Trim().Length > 0 ? s.Trim() : null;

    private static int? Inteiro(JsonElement e, string k)
    {
        if (!e.TryGetProperty(k, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static decimal? Decimal(JsonElement e, string k)
    {
        if (!e.TryGetProperty(k, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    /// <summary>Data da resposta em hora local: aceita "2026-10-05", timestamptz com fuso e sem fuso.</summary>
    private static DateTime? Data(JsonElement e, string k)
    {
        var s = Texto(e, k);
        if (s is null) return null;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
            return s.Length <= 10 ? dto.DateTime : dto.LocalDateTime;
        return null;
    }

    /// <summary>"MARIA DA SILVA" vira "Maria": a tela do balcão não mostra o nome inteiro do cliente.</summary>
    public static string? PrimeiroNome(string? nome)
    {
        if (string.IsNullOrWhiteSpace(nome)) return null;
        var p = nome.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return p.Length == 0 ? null : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant();
    }

    private static string Corta(string? t) => string.IsNullOrEmpty(t) ? "" : t.Length <= 160 ? t : t[..160];
}
