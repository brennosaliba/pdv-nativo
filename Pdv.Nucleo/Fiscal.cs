using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pdv.Nucleo;

// ─────────────────────────────────────────────────────────────────────────────
// EMISSÃO DA NFC-e — só o CLIENTE. Quem assina o XML, numera e conversa com a
// SEFAZ é a edge `nfce-emitir` (EC2) ou o agente local (http://127.0.0.1:4610).
//
// ⚠️ ORDEM DOS CAMINHOS (decisão do dono, 08/2026 — INVERTEU o que valia antes):
//   NUVEM PRIMEIRO. O agente local é CONTINGÊNCIA, só quando a rede não responde.
//   Motivos:
//     · a fibra da loja tem 99,99% de disponibilidade — o caminho raro é o local;
//     · emitindo pela nuvem o certificado A1 continua na EC2 e NÃO vai para o PC
//       do balcão — é isso que permite vender o PDV para cliente externo;
//     · nota emitida pelo agente local NÃO entra em `nfce_emitidas`, então some da
//       2ª via e do extrato contábil até a guarda subir os XMLs.
//   Quem inverter isto de volta reintroduz os três problemas de uma vez.
//
// A regra que manda em tudo neste arquivo:  SUCESSO = autorizado || contingencia.
// O agente devolve `"ok": true` HARDCODED (é literal no código dele) e responde
// HTTP 200 até quando a SEFAZ rejeita. Quem ler `ok` ou o status HTTP transforma
// rejeição em "venda com nota" e imprime cupom sem valor fiscal. Por isso
// ResultadoEmissao nem tem um campo `Ok`: o valor é ignorado de propósito.
//
// E a regra que separa "rede caiu" de "configuração errada":
//   A NUVEM RESPONDEU (qualquer HTTP) ⇒ a rede está de pé. Se ela disse 401/403, é
//   ERRO DE CONFIGURAÇÃO e tem que BLOQUEAR a venda — cair para o agente aqui
//   entra em contingência à toa, queima numeração da série local e gera trabalho de
//   sincronização. NÃO RESPONDEU ⇒ queda de rede de verdade ⇒ contingência local.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Item como a NFC-e enxerga. Aqui na fronteira fiscal vira <c>decimal</c> porque o
/// XML é decimal (qtd até 4 casas, vUnit até 10) — dentro do PDV continua sendo
/// centavos/milésimos. A conversão acontece num lugar só, em <see cref="De"/>.
/// </summary>
public sealed record ItemFiscal(
    string Codigo, string Descricao, string? Ncm, string? Cest, string? Csosn,
    string? Cfop, string Unidade, decimal Qtd, decimal VUnit, int? Origem = null,
    decimal VDesc = 0)
{
    /// <summary>
    /// Ponte a partir dos tipos da casa — use esta e não o construtor, pra a conversão nascer certa.
    ///
    /// <para><paramref name="totalLinha"/> é o total que o PDV vai gravar em
    /// <c>venda_item.total_cent</c>. Ele existe porque o motor recalcula o vProd por
    /// conta própria (<c>+(qtd*vUnit).toFixed(2)</c>) e, em item PESÁVEL, a conta dele
    /// não bate com <see cref="Dinheiro.VezesQtd"/>: 0,500 kg a R$ 2,01 dá R$ 1,01 aqui
    /// (arredonda AwayFromZero) e R$ 1,00 lá. Um centavo de diferença = Σ vPag ≠ vNF =
    /// rejeição da SEFAZ com nNF queimado e cliente no balcão. Passando o total da linha,
    /// o vUnit é derivado (até 10 casas, que é o que o XML aceita) de modo que
    /// <c>round(qtd × vUnit, 2)</c> reproduza EXATAMENTE o total do PDV.</para>
    ///
    /// <para>Para quantidade inteira o valor derivado é idêntico ao vUnit de entrada —
    /// nada muda no caminho comum.</para>
    /// </summary>
    public static ItemFiscal De(string codigo, string descricao, string? ncm, string? cest,
        string? csosn, string? cfop, string unidade, Quantidade qtd, Dinheiro vUnit,
        Dinheiro? totalLinha = null, int? origem = null, Dinheiro? desconto = null)
    {
        var total = totalLinha ?? vUnit.VezesQtd(qtd.Milesimos);
        return new(codigo, descricao, ncm, cest, csosn, cfop, unidade,
            qtd.Valor, UnitarioQueReproduz(total, qtd, vUnit), origem, desconto?.Reais ?? 0m);
    }

    /// <summary>
    /// Itens da nota a partir das linhas da venda (03/09/2026, promoção de carrinho).
    /// vdescPorItem = true: vProd cheio (preço de tabela) e vDesc por item, a forma
    /// canônica. false (transitório, enquanto o assinador não distribui vDesc por
    /// item): o unitário já sai líquido, como a promoção de preço sempre saiu.
    /// </summary>
    public static IReadOnlyList<ItemFiscal> DaVenda(IReadOnlyList<LinhaVenda> itens, bool vdescPorItem)
        => itens.Select(i => vdescPorItem && i.Desconto.Centavos > 0
            ? De(i.Codigo ?? i.ProdutoId ?? "", i.Descricao, i.Ncm, i.Cest, i.Csosn, i.Cfop,
                 i.Unidade, i.Qtd, i.Preco, i.Bruto, i.Origem, i.Desconto)
            : De(i.Codigo ?? i.ProdutoId ?? "", i.Descricao, i.Ncm, i.Cest, i.Csosn, i.Cfop,
                 i.Unidade, i.Qtd, i.Preco, i.Total, i.Origem)).ToList();

    /// <summary>
    /// vUnit em até 10 casas tal que <c>round(qtd × vUnit, 2) == total</c>.
    /// O ajuste fino de 1e-10 existe porque a divisão arredondada nem sempre volta
    /// no mesmo centavo; sem ele o erro só apareceria como rejeição da SEFAZ.
    /// Se nem assim fechar, devolvemos o quociente e a divergência é pega por
    /// <see cref="Fiscal.Conferir"/> ANTES do POST — recusa local não queima numeração.
    /// </summary>
    internal static decimal UnitarioQueReproduz(Dinheiro total, Quantidade qtd, Dinheiro vUnit)
    {
        if (qtd.Milesimos <= 0) return vUnit.Reais;   // qtd inválida: Conferir barra logo adiante

        var q = qtd.Valor;
        var alvo = total.Reais;
        var candidato = Math.Round(alvo / q, 10, MidpointRounding.AwayFromZero);
        if (Fiscal.TotalDaLinha(q, candidato) == alvo) return candidato;

        const decimal passo = 0.0000000001m;   // 1 unidade na 10ª casa, o limite do vUnCom
        for (var i = 1; i <= 3; i++)
        {
            var acima = candidato + passo * i;
            if (Fiscal.TotalDaLinha(q, acima) == alvo) return acima;

            var abaixo = candidato - passo * i;
            if (abaixo > 0 && Fiscal.TotalDaLinha(q, abaixo) == alvo) return abaixo;
        }
        return candidato;
    }
}

/// <summary>
/// Retorno do TEF que vira o grupo &lt;card&gt; da nota (tpIntegra=1). Sem ele o motor
/// emite tpIntegra=2, que é honesto e válido — é o desfecho de toda venda em que o
/// TEF não integrou.
/// </summary>
public sealed record CartaoFiscal(string CAut, string Cnpj, string? TBand);

/// <summary>Um &lt;detPag&gt;. <c>TPag</c> é o código NUMÉRICO da NFe, não o nome da forma.</summary>
public sealed record PagamentoFiscal(string TPag, decimal Valor, CartaoFiscal? Card)
{
    /// <summary>
    /// Ponte a partir da forma que a tela usa ("dinheiro|debito|credito|pix").
    /// Estoura de propósito em forma desconhecida — ver <see cref="Fiscal.TPagDaForma"/>.
    ///
    /// ⚠️ Em dinheiro, <paramref name="valor"/> é o TOTAL DA NOTA, nunca o recebido:
    /// o motor não emite &lt;vTroco&gt; e a SEFAZ valida Σ vPag − vTroco = vNF (plano 4.2).
    /// </summary>
    public static PagamentoFiscal De(string forma, Dinheiro valor, CartaoFiscal? card = null)
        => new(Fiscal.TPagDaForma(forma), valor.Reais, card);

    /// <summary>
    /// A partir de uma parte da venda: valor APLICADO (valor − troco) e o grupo &lt;card&gt;
    /// só quando o pagamento foi INTEGRADO e o TEF devolveu cAut + CNPJ da credenciadora.
    /// POS avulso (<see cref="PagamentoVenda.PosAvulso"/>) sai com o tPag da forma real e
    /// SEM card: o motor emite tpIntegra=2, que é a verdade e passa na SEFAZ.
    /// </summary>
    public static PagamentoFiscal De(PagamentoVenda p)
    {
        var card = p.Integrado && p.Aut is { Length: > 0 } && p.CnpjCredenciadora is { Length: > 0 }
            ? new CartaoFiscal(p.Aut, p.CnpjCredenciadora, p.Bandeira)
            : null;
        return De(p.Forma, new Dinheiro(p.Valor.Centavos - p.Troco.Centavos), card);
    }
}

/// <summary>Emitente como saiu na nota — o cupom tem que imprimir exatamente isto.</summary>
public sealed record EmitenteFiscal(string? Nome, string? Cnpj, string? Ie, string? Endereco);

/// <summary>
/// Desfecho de uma tentativa de emissão. Espelha o corpo do agente, mais o que o
/// caixa precisa saber pra decidir o que fazer na tela.
/// </summary>
public sealed record ResultadoEmissao
{
    /// <summary>"nuvem" | "agente" — qual caminho produziu este desfecho (vai pra nfce_emissao.caminho).</summary>
    public string Caminho { get; init; } = "";

    public bool Autorizado { get; init; }
    public bool Contingencia { get; init; }

    /// <summary>"online" | "contingencia". A edge não manda este campo (ela não faz offline); derivamos.</summary>
    public string? Modo { get; init; }

    public int? TpAmb { get; init; }
    public string? Chave { get; init; }
    public int? Numero { get; init; }
    public int? Serie { get; init; }
    public string? Protocolo { get; init; }

    /// <summary>
    /// ⚠️ Não derive sucesso daqui: 100, 150 (fora de prazo) e 120 são TODOS autorização
    /// válida. Tratar 150 como recusa faz o operador reemitir e gera nota duplicada.
    /// Quem decide é o <see cref="Autorizado"/> que o emissor mandou.
    /// </summary>
    public string? CStat { get; init; }

    public string? XMotivo { get; init; }

    /// <summary>URL já pronta pro QR do cupom. Não montar à mão — muda entre v2 (offline) e v3 (online).</summary>
    public string? QrCode { get; init; }

    public string? DhRecbto { get; init; }
    public decimal? VNF { get; init; }
    public EmitenteFiscal? Emit { get; init; }

    /// <summary>Só o caminho da nuvem devolve (id em nfce_emitidas) — é o p_nfce_id de pdv_vincular_nfce.</summary>
    public string? NfceId { get; init; }

    /// <summary>Motivo da recusa, ou da indisponibilidade. Vem preenchido sempre que não houve sucesso.</summary>
    public string? Erro { get; init; }

    /// <summary>
    /// Nem deu pra falar com o emissor (socket, timeout, sem sessão): NÃO sabemos se
    /// saiu nota. É diferente de rejeição — aqui não se oferece "concluir sem nota"
    /// sem antes tentar de novo, e NUNCA se reemite pelo outro caminho às cegas.
    /// </summary>
    public bool Indisponivel { get; init; }

    /// <summary>
    /// Recusa nascida AQUI, antes de sair da máquina (<see cref="NaoEmitida"/>): nenhum
    /// nNF foi consumido. Fiscalmente é o oposto de uma rejeição da SEFAZ, que queima
    /// numeração e cria obrigação de inutilizar a faixa pulada. Quem grava
    /// `nfce_emissao` NÃO deve gravar linha (ou grava com numero=null) quando isto é true:
    /// senão o livro conta tentativa e numeração que não existiram.
    /// </summary>
    public bool BarradaLocalmente { get; init; }

    /// <summary>
    /// A plataforma RESPONDEU e disse não (401 que sobreviveu à renovação de sessão, 403
    /// sem papel de emissão). Isso é ERRO DE CONFIGURAÇÃO, não queda de rede: não vale
    /// contingência, não se cai para o agente local e a venda para com mensagem clara.
    /// </summary>
    public bool Bloqueado { get; init; }

    /// <summary>
    /// O ÚNICO sinal de sucesso que existe.
    /// ⚠️ O agente manda `"ok": true` SEMPRE — hardcoded, sem significado fiscal — e
    /// responde HTTP 200 até em rejeição. Se alguém "consertar" isto pra usar `ok` ou
    /// `IsSuccessStatusCode`, o caixa passa a imprimir cupom de nota rejeitada. NÃO MEXA.
    /// </summary>
    public bool Sucesso => Autorizado || Contingencia;

    /// <summary>Recusa de verdade (cStat ou validação local): a venda vale, a nota não saiu.</summary>
    public bool Rejeitada => !Sucesso && !Indisponivel;

    /// <summary>
    /// A tentativa consumiu um nNF do outro lado (rejeição do motor/SEFAZ) e a faixa
    /// pulada vai precisar de `POST /nfce/inutilizar`. Recusa local não consome nada.
    /// </summary>
    public bool QueimouNumeracao => Rejeitada && !BarradaLocalmente;

    /// <summary>
    /// fiscal_status pra gravar em `venda` (seção 2.3 do plano).
    /// ⚠️ Emissor MUDO vira "pendente", não "rejeitada": a nota PODE ter sido assinada e
    /// autorizada do outro lado. Gravar 'rejeitada' aqui convida o operador a "tentar
    /// emitir de novo" e produz NOTA DUPLICADA, além de deixar o livro fiscal mentindo.
    /// Quem consome 'pendente' tem que CONFERIR (`/nfce/xml?chave=` ou `/fila`) antes de
    /// reemitir — nunca reemitir automático.
    /// </summary>
    public string StatusFiscal => BarradaLocalmente ? "sem_nota"
        : Indisponivel ? "pendente"
        : Autorizado ? "autorizada"
        : Contingencia ? "contingencia"
        : "rejeitada";

    /// <summary>Emissor mudo. Não afirma nada sobre a nota — porque não dá pra afirmar.</summary>
    public static ResultadoEmissao ForaDoAr(string caminho, string motivo)
        => new() { Caminho = caminho, Indisponivel = true, Erro = motivo };

    /// <summary>
    /// A plataforma respondeu e recusou (config/permissão). Continua <c>Indisponivel</c>
    /// de propósito — "não sei o que houve com a nota" é sempre a leitura conservadora —
    /// mas <see cref="Bloqueado"/> avisa o resolvedor que ISTO NÃO É QUEDA DE REDE e que
    /// o agente local não deve assumir.
    /// </summary>
    public static ResultadoEmissao Bloqueio(string caminho, string motivo)
        => new() { Caminho = caminho, Indisponivel = true, Bloqueado = true, Erro = motivo };

    /// <summary>
    /// Barrado AQUI, antes de sair da máquina: nenhum nNF foi queimado. É melhor
    /// desfecho que uma rejeição da SEFAZ, e por isso a validação local vem antes.
    /// </summary>
    public static ResultadoEmissao NaoEmitida(string caminho, string motivo)
        => new() { Caminho = caminho, BarradaLocalmente = true, Erro = "não emiti: " + motivo };
}

/// <summary>
/// Sonda do emissor. <c>Serie</c> e <c>TpAmb</c> existem pra serem comparados com
/// terminal.serie_nfce / terminal.ambiente no boot: dois emissores na mesma série =
/// Rejeição 539 em cascata, e o erro só aparece depois de queimar numeração; ambiente
/// trocado emite em PRODUÇÃO uma nota que não se desfaz (só se cancela em 30 min).
/// Quando não está Ok, a mensagem a mostrar é <c>ErroCertificado ?? Erro</c>.
/// </summary>
/// <param name="TpAmb">
/// 1 = produção, 2 = homologação. ⚠️ O `/health` do agente HOJE não manda este campo
/// (pdv-agent.cjs:111) — vem null até ele passar a mandar `tpAmb: Number(emitCfg.tpAmb)||2`.
/// Null = "não deu pra provar", e o que não se prova não bloqueia.
/// </param>
/// <param name="Bloqueio">
/// A plataforma respondeu e recusou (401/403, sem sessão com a rede de pé): erro de
/// CONFIGURAÇÃO. Diferente de "não respondeu", que é queda de rede e libera contingência.
/// </param>
public sealed record SaudeEmissor(bool Ok, int? Serie, string? Cnpj, int? ProximoNNF,
    int Pendentes, string? ErroCertificado, string? Erro, int? TpAmb = null, bool Bloqueio = false);

/// <summary>
/// Quem sabe emitir NFC-e. Nenhum método joga exceção por falha de rede — rede caída
/// é estado esperado no caixa, não acidente. Só cancelamento pedido pelo chamador sobe.
/// </summary>
public interface IEmissorFiscal
{
    Task<SaudeEmissor> SondarAsync(CancellationToken ct);

    Task<ResultadoEmissao> EmitirAsync(IReadOnlyList<ItemFiscal> itens,
        IReadOnlyList<PagamentoFiscal> pagamentos, string? documento, CancellationToken ct);

    /// <summary>
    /// Igual à de cima, com a REFERÊNCIA da venda (id local). A nuvem grava como
    /// pdv_ref em nfce_emitidas — é o que permite, depois de um timeout, responder
    /// "saiu nota pra ESTA venda?" com consulta em vez de adivinhação. Implementação
    /// default ignora a referência, então fakes de teste e o agente não mudam.
    /// </summary>
    Task<ResultadoEmissao> EmitirAsync(IReadOnlyList<ItemFiscal> itens,
        IReadOnlyList<PagamentoFiscal> pagamentos, string? documento, string? referencia, CancellationToken ct)
        => EmitirAsync(itens, pagamentos, documento, ct);
}

/// <summary>Infra compartilhada pelos emissores: HTTP, leitura tolerante de JSON e as regras de tPag.</summary>
public static class Fiscal
{
    // Formas que a fase 1 expõe. Fora deste mapa é ERRO EXPLÍCITO: tPag "99" exige
    // <xPag>, que o motor não emite, então o fallback silencioso garantiria rejeição
    // com nNF queimado. As strings da esquerda são contrato duro com o fechamento de
    // caixa (Caixa.Apurado agrupa por venda_pagamento.forma).
    //
    // ⚠️ StringComparer.Ordinal, e não OrdinalIgnoreCase: "PIX"/"Dinheiro"/"DEBITO"
    // TÊM que estourar aqui. O fechamento cego pergunta a contagem só de
    // `dinheiro|credito|debito|pix`; uma grafia diferente emite nota normalmente e vira
    // falta fantasma no fechamento do dia. A emissão é o único lugar onde esse erro
    // aparece cedo — aceitar de bom grado apaga a única denúncia.
    private static readonly Dictionary<string, string> _tPag = new(StringComparer.Ordinal)
    {
        ["dinheiro"] = "01", ["credito"] = "03", ["debito"] = "04", ["pix"] = "17",
        ["voucher"] = "11",   // vale-refeicao (03/09)
    };

    // Só estes levam <card> (03,04,10..19). Dinheiro nunca — mandar card em tPag 01 é rejeição.
    private static readonly HashSet<string> _aceitaCard = new(StringComparer.Ordinal)
    { "03", "04", "10", "11", "12", "13", "14", "15", "16", "17", "18", "19" };

    /// <summary>Códigos que a fase 1 sabe emitir. Ver seção 4.1 antes de acrescentar qualquer um.</summary>
    private static readonly HashSet<string> _tPagValidos = new(StringComparer.Ordinal) { "01", "03", "04", "11", "17" };

    private static readonly CultureInfo PtBr = new("pt-BR");

    /// <summary>
    /// Proxy opcional (ex.: "socks5://127.0.0.1:1080"). O dono opera às vezes da China,
    /// onde a saída é só por VPN/SOCKS5. Tem que ser definido ANTES da primeira chamada
    /// HTTP — o cliente é único e criado sob demanda.
    /// </summary>
    public static string? ProxyUrl { get; set; }

    private static readonly Lazy<HttpClient> _http =
        new Lazy<HttpClient>(Criar, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// UM HttpClient pro processo inteiro. Criar um por chamada esgota as portas
    /// efêmeras do Windows (ficam em TIME_WAIT) e o caixa começa a falhar depois de
    /// algumas horas de movimento — justo no pico.
    /// </summary>
    internal static HttpClient Http => _http.Value;

    private static HttpClient Criar()
    {
        var handler = new SocketsHttpHandler
        {
            // Reciclar conexão de tempos em tempos: a loja troca de saída de internet
            // (4G de contingência) e conexão presa a um caminho morto trava a venda.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseProxy = false,
        };
        var proxy = ProxyUrl;
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            // BypassProxyOnLocal é obrigatório: o agente fiscal é 127.0.0.1 e mandar
            // loopback pelo proxy mata a contingência local justamente quando ela entra.
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(proxy) { BypassProxyOnLocal = true };
        }

        // Timeout infinito de propósito: cada rota tem o seu (1,2 s na sonda, 25 s na
        // emissão) via CancellationTokenSource. Um timeout global no cliente
        // compartilhado seria o menor de todos, para todo mundo.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>forma da tela → tPag da NFe. Estoura em forma desconhecida (seção 4.1).</summary>
    public static string TPagDaForma(string forma)
        => _tPag.TryGetValue((forma ?? "").Trim(), out var t)
            ? t
            : throw new InvalidOperationException(
                $"Forma de pagamento '{forma}' não é emitível. Use exatamente dinheiro|credito|debito|pix " +
                "(minúsculas, sem acento: é contrato com o fechamento de caixa). " +
                "Cair em tPag 99 gera rejeição certa e queima numeração.");

    public static string Digitos(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var b = new StringBuilder(s.Length);
        foreach (var c in s) if (char.IsAsciiDigit(c)) b.Append(c);
        return b.ToString();
    }

    /// <summary>
    /// Documento do consumidor pronto pra nota: só dígitos, e só se tiver tamanho de
    /// CPF/CNPJ. A validação de DÍGITO VERIFICADOR é do chamador (seção 4.3) — aqui
    /// só evitamos que um documento pela metade vaze pra nota e derrube a emissão.
    /// </summary>
    internal static string? DocumentoParaNota(string? documento)
    {
        var d = Digitos(documento);
        return d.Length is 11 or 14 ? d : null;
    }

    internal static bool AceitaCard(string tPag) => _aceitaCard.Contains(tPag);

    /// <summary>
    /// vProd de uma linha pela MESMA regra do motor (<c>+(qtd*vUnit).toFixed(2)</c>),
    /// só que em decimal — em double a conta erra o centavo em item pesável.
    /// </summary>
    internal static decimal TotalDaLinha(decimal qtd, decimal vUnit)
        => Math.Round(qtd * vUnit, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// vNF como o motor vai calcular: soma dos vProd arredondados linha a linha.
    /// Público porque a tela precisa dele ANTES de cobrar (o total do PDV e o total da
    /// nota podem divergir por centavo em item pesável) e o cupom precisa imprimir o
    /// total DA NOTA, não o que o PDV somou.
    /// </summary>
    public static decimal TotalDaNota(IReadOnlyList<ItemFiscal> itens)
    {
        decimal total = 0;
        for (var i = 0; i < itens.Count; i++)
            total += TotalDaLinha(itens[i].Qtd, itens[i].VUnit) - Math.Round(itens[i].VDesc, 2, MidpointRounding.AwayFromZero);
        return total;
    }

    /// <summary>
    /// Recusa que dá pra detectar sem sair da máquina. Roda ANTES do POST porque
    /// falhar aqui não consome numeração — falhar na SEFAZ consome.
    /// </summary>
    internal static string? Conferir(IReadOnlyList<ItemFiscal>? itens, IReadOnlyList<PagamentoFiscal>? pagamentos)
    {
        if (itens is null || itens.Count == 0) return "a venda está sem itens";
        if (pagamentos is null || pagamentos.Count == 0) return "a venda está sem pagamento";

        foreach (var i in itens)
        {
            if (string.IsNullOrWhiteSpace(i.Codigo)) return $"item sem código ({i.Descricao})";
            if (string.IsNullOrWhiteSpace(i.Descricao)) return $"item {i.Codigo} sem descrição";
            if (i.Qtd <= 0) return $"quantidade inválida no item {i.Descricao}";
            if (i.VUnit < 0) return $"valor unitário inválido no item {i.Descricao}";
            if (i.VDesc < 0 || i.VDesc > TotalDaLinha(i.Qtd, i.VUnit))
                return $"desconto inválido no item {i.Descricao} ({Moeda(i.VDesc)} num item de {Moeda(TotalDaLinha(i.Qtd, i.VUnit))})";
        }

        decimal soma = 0;
        foreach (var p in pagamentos)
        {
            var t = (p.TPag ?? "").Trim().PadLeft(2, '0');
            if (!_tPagValidos.Contains(t))
                return $"forma de pagamento não suportada (tPag={p.TPag})";
            if (p.Valor <= 0) return "pagamento com valor zerado";
            soma += p.Valor;
        }

        // Σ vPag == vNF é regra ABSOLUTA da fase 1 (plano 4.2): o motor não emite
        // <vTroco> e a SEFAZ valida Σ vPag − vTroco = vNF. Este é o único ponto do
        // sistema que tem itens e pagamentos na mão ao mesmo tempo, então é aqui que a
        // conferência tem que morar — a tela não sabe o vNF, quem o calcula é o motor.
        // As duas falhas que isto pega, e que sem ele só apareceriam como rejeição com
        // nNF queimado e cliente no balcão:
        //   · centavo perdido em item pesável (PDV arredonda a linha, motor recalcula);
        //   · vPag = valor RECEBIDO em dinheiro em vez do total da nota.
        soma = Math.Round(soma, 2, MidpointRounding.AwayFromZero);
        var vNF = TotalDaNota(itens);
        if (soma != vNF)
            return $"a soma dos pagamentos ({Moeda(soma)}) não fecha com o total da nota ({Moeda(vNF)}). " +
                   "Em dinheiro, o pagamento da nota é o TOTAL, nunca o valor recebido: o troco vive só no " +
                   "cupom e em venda_pagamento.troco_cent.";

        return null;
    }

    private static string Moeda(decimal v) => "R$ " + v.ToString("N2", PtBr);

    /// <summary>Mensagem curta e em português pro operador — ele não tem o que fazer com stack trace.</summary>
    internal static string Motivo(Exception ex) => ex switch
    {
        OperationCanceledException => "o emissor não respondeu a tempo",
        HttpRequestException h => "não consegui falar com o emissor" +
            (h.InnerException is null ? "" : $" ({h.InnerException.Message})"),
        JsonException => "o emissor respondeu algo que não é JSON",
        _ => ex.Message,
    };

    /// <summary>
    /// Leitura do desfecho — a MESMA nos dois caminhos, e ela não olha `ok` nem o status HTTP.
    ///
    /// O critério é a presença do campo `autorizado`: se ele veio, o emissor chegou a
    /// falar de nota (mesmo em HTTP 502, como a edge faz quando a EC2 recusa — e nesse
    /// caso a numeração JÁ foi consumida, então é rejeição de verdade). Se não veio, é
    /// erro de plataforma/permissão/exceção: não sabemos o que houve com a nota, e
    /// "não sei" é <see cref="ResultadoEmissao.Indisponivel"/>, não rejeição.
    /// </summary>
    internal static ResultadoEmissao LerResposta(string corpo, int status, string caminho)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(corpo) ? "{}" : corpo);
            var r = doc.RootElement;

            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("autorizado", out _))
                return ResultadoEmissao.ForaDoAr(caminho,
                    Txt(r, "error") ?? Txt(r, "message") ?? $"o emissor respondeu HTTP {status} sem desfecho fiscal");

            var contingencia = Bool(r, "contingencia");
            var emit = Objeto(r, "emit");

            return new ResultadoEmissao
            {
                Caminho = caminho,
                Autorizado = Bool(r, "autorizado"),
                Contingencia = contingencia,
                Modo = Txt(r, "modo") ?? (contingencia ? "contingencia" : "online"),
                TpAmb = Inteiro(r, "tpAmb"),
                Chave = Txt(r, "chave"),
                Numero = Inteiro(r, "numero") ?? Inteiro(r, "nNF"),
                Serie = Inteiro(r, "serie"),
                Protocolo = Txt(r, "protocolo"),
                CStat = Txt(r, "cStat"),
                XMotivo = Txt(r, "xMotivo"),
                QrCode = Txt(r, "qrCode"),
                DhRecbto = Txt(r, "dhRecbto"),
                VNF = Valor(r, "vNF"),
                NfceId = Txt(r, "id"),
                Emit = emit is null ? null : new EmitenteFiscal(
                    Txt(emit.Value, "xNome") ?? Txt(emit.Value, "nome"),
                    Digitos(Txt(emit.Value, "cnpj")) is { Length: > 0 } c ? c : null,
                    Txt(emit.Value, "ie"),
                    Txt(emit.Value, "endereco")),
                Erro = Txt(r, "error") ?? Txt(r, "erro"),
            };
        }
        catch (Exception ex)
        {
            return ResultadoEmissao.ForaDoAr(caminho, Motivo(ex));
        }
    }

    /// <summary>
    /// O corpo chegou a falar de NOTA? Existe pra proteger o retry de 401: só se pode
    /// repetir um POST de emissão quando é CERTO que nenhuma nota nasceu. Se `autorizado`
    /// veio no corpo, a numeração já foi consumida e repetir criaria uma segunda nota.
    /// </summary>
    internal static bool TemDesfechoFiscal(string? corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return false;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("autorizado", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Leitura tolerante a tipo de propósito: o agente manda cStat como texto e a EC2
    // às vezes como número (e vice-versa em numero/vNF). Estourar exceção por causa
    // disso no meio da venda seria trocar um problema de contrato por um caixa travado.
    internal static string? Txt(JsonElement e, string nome)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(nome, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    internal static int? Inteiro(JsonElement e, string nome)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(nome, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String &&
            int.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var m)) return m;
        return null;
    }

    internal static decimal? Valor(JsonElement e, string nome)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(nome, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String &&
            decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var m)) return m;
        return null;
    }

    internal static bool Bool(JsonElement e, string nome)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(nome, out var v)) return false;
        return v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b);
    }

    internal static JsonElement? Objeto(JsonElement e, string nome)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v)
            && v.ValueKind == JsonValueKind.Object ? v : null;
}

/// <summary>
/// CONTINGÊNCIA: o agente Node que roda na própria máquina. Ele numera local, guarda
/// XML em disco, entra em contingência (tpEmis=9) sozinho quando a SEFAZ cai e
/// retransmite depois — nada disso depende de sessão na nuvem, então o caixa vende com
/// a internet no chão. Este cliente não decide nada fiscal: só pergunta e traduz.
///
/// ⚠️ Ele deixou de ser o caminho primário (ver cabeçalho do arquivo): nota emitida por
/// aqui NÃO entra em `nfce_emitidas` até a guarda subir o XML, some da 2ª via e do
/// extrato contábil, e exige o certificado A1 nesta máquina.
/// </summary>
public sealed class EmissorAgente : IEmissorFiscal
{
    public const string Caminho = "agente";
    public const string UrlPadrao = "http://127.0.0.1:4610";

    // 1,2 s na sonda porque ela roda no pré-voo, com o operador olhando; 25 s na
    // emissão porque a ida à SEFAZ é lenta e desistir antes gera nota "perdida"
    // (assinada e transmitida sem ninguém pra ler o protocolo).
    private static readonly TimeSpan TempoSonda = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan TempoEmissao = TimeSpan.FromSeconds(25);

    private readonly string _url;

    /// <param name="url">Só muda em teste ou se a porta do agent-config.json for outra.</param>
    public EmissorAgente(string? url = null) => _url = (url ?? UrlPadrao).TrimEnd('/');

    public async Task<SaudeEmissor> SondarAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TempoSonda);

            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_url}/health");
            using var resp = await Fiscal.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var corpo = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return new SaudeEmissor(false, null, null, null, 0, null,
                    $"o emissor local respondeu HTTP {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(corpo) ? "{}" : corpo);
            var r = doc.RootElement;

            // `cert` vira { "erro": ... } quando o .pfx não abre (vencido, senha errada,
            // arquivo movido). É bloqueio pra ESTE caminho — o A1 mora nesta máquina.
            var cert = Fiscal.Objeto(r, "cert");
            var erroCert = cert is null ? null : Fiscal.Txt(cert.Value, "erro") ?? Fiscal.Txt(cert.Value, "error");

            var serie = Fiscal.Inteiro(r, "serie");
            var cnpj = Fiscal.Digitos(Fiscal.Txt(r, "cnpj")) is { Length: > 0 } c ? c : null;

            // Qualquer coisa pode estar ouvindo na porta 4610 (outro processo, um proxy,
            // um `{}` de captive portal). O agente real SEMPRE manda série e CNPJ no
            // /health, então a ausência deles é prova de que não é ele. Sem esta guarda o
            // pré-voo ficava verde com Serie=null e a validação de boot comparava série
            // contra null em silêncio — que é justamente o cenário da Rejeição 539.
            var impostor = serie is null || cnpj is null;

            // O `ok` do /health também é ignorado, pelo mesmo motivo do /nfce/emitir.
            return new SaudeEmissor(
                Ok: erroCert is null && !impostor,
                Serie: serie,
                Cnpj: cnpj,
                ProximoNNF: Fiscal.Inteiro(r, "proximoNNF"),
                Pendentes: Pendentes(r),
                ErroCertificado: erroCert,
                Erro: impostor ? $"{_url} respondeu, mas não é o emissor fiscal (veio sem série/CNPJ)" : null,
                // Ainda vem null: o /health do agente não manda tpAmb hoje. Ver SaudeEmissor.
                TpAmb: Fiscal.Inteiro(r, "tpAmb"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;  // desistência do chamador não é falha do emissor
        }
        catch (Exception ex)
        {
            return new SaudeEmissor(false, null, null, null, 0, null, Fiscal.Motivo(ex));
        }
    }

    public async Task<ResultadoEmissao> EmitirAsync(IReadOnlyList<ItemFiscal> itens,
        IReadOnlyList<PagamentoFiscal> pagamentos, string? documento, CancellationToken ct)
    {
        var problema = Fiscal.Conferir(itens, pagamentos);
        if (problema is not null) return ResultadoEmissao.NaoEmitida(Caminho, problema);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TempoEmissao);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_url}/nfce/emitir");
            req.Content = new StringContent(Wire.CorpoAgente(itens, pagamentos, documento),
                Encoding.UTF8, "application/json");

            using var resp = await Fiscal.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var corpo = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return Fiscal.LerResposta(corpo, (int)resp.StatusCode, Caminho);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Socket morto / timeout: a nota PODE ter saído do outro lado. Por isso
            // Indisponivel, e não rejeição — quem trata isso não pode reemitir cego.
            return ResultadoEmissao.ForaDoAr(Caminho, Fiscal.Motivo(ex));
        }
    }

    /// <summary>
    /// Notas paradas em contingência. ⚠️ O formato do resumo da fila é INCERTO no plano
    /// (1.1.1 mostra só um comentário; 3.3.g fala em `fila.pendente`), então lemos os
    /// nomes conhecidos e paramos por aí — inventar esquema aqui viraria um badge
    /// mentindo sobre nota parada, que é o oposto do que o badge serve.
    /// </summary>
    private static int Pendentes(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("fila", out var fila)) return 0;
        if (fila.ValueKind == JsonValueKind.Number && fila.TryGetInt32(out var n)) return n;
        foreach (var nome in new[] { "pendente", "pendentes", "aguardando", "total" })
            if (Fiscal.Inteiro(fila, nome) is int v) return v;
        return 0;
    }
}

/// <summary>
/// CAMINHO PRIMÁRIO: a edge `nfce-emitir`, que numera no banco e emite pela EC2 (o A1 e
/// o CSC ficam lá, não nesta máquina) e já grava a nota em `nfce_emitidas` — 2ª via e
/// extrato contábil saem de graça. Diferenças que este cliente normaliza:
///  · o corpo dela leva `itens`/`pagamentos` na RAIZ, não dentro de `sale`;
///  · ela não tem contingência offline — quando falha por rede, quem assume é o agente;
///  · ela exige sessão de USUÁRIO com papel de operação (anon key não passa do getUser).
/// A numeração dela é OUTRO contador: a série da nfce_config TEM que ser diferente da
/// série do agente, senão os dois caminhos colidem e vira Rejeição 539 em cascata —
/// quem impõe isso é <see cref="EmissorResolvido.SerieNuvem"/>.
/// </summary>
public sealed class EmissorNuvem : IEmissorFiscal
{
    public const string Caminho = "nuvem";

    private const string MsgSemSessao = "sem sessão válida com a nuvem: entre no sistema de novo";
    private const string MsgSemPapel =
        "esta conta não tem permissão para emitir na nuvem (papel do usuário): é erro de configuração, relogar não resolve";

    private static readonly TimeSpan TempoSonda = TimeSpan.FromMilliseconds(1200);

    // 45 s, e não 25: a edge espera a EC2 por 30 s antes de gravar o desfecho. Cliente
    // que desiste ANTES dela transforma toda demora em "não sei se a nota saiu" — foi
    // exatamente o buraco de 06/08: a edge respondia "rejeitada" aos 30 s, o caixa já
    // tinha desistido aos 25, e cada retry consumia mais um número da série às cegas.
    private static readonly TimeSpan TempoEmissao = TimeSpan.FromSeconds(45);

    // A sonda-ping percorre a cadeia inteira (edge → EC2), então precisa de mais fôlego
    // que o 1,2 s do agente local — mas continua curta: roda no pré-voo, com o operador
    // olhando a tela, e o resultado vale 30 s no cache do resolvedor.
    private static readonly TimeSpan TempoSondaPing = TimeSpan.FromSeconds(4);

    // Orçamento da renovação de sessão DENTRO da sonda. A sonda roda no pré-voo, com o
    // operador olhando a tela: um refresh sem relógio próprio penduraria a venda por
    // tempo indeterminado, e o pré-voo existe justamente pra ser rápido.
    private static readonly TimeSpan TempoSessaoSonda = TimeSpan.FromSeconds(4);

    private readonly Func<CancellationToken, Task<string?>> _token;
    private readonly string _url;
    private readonly string? _cnpj;

    /// <summary>
    /// Renovação/relogin da sessão Supabase, implementada fora daqui (o dono do
    /// refresh_token é o <c>Nuvem.cs</c>). Mesmo contrato do <see cref="ClienteTef"/>.
    ///
    /// ⚠️ É isto que impede o PDV de entrar em contingência à toa: o caixa fica logado o
    /// dia inteiro, o token vale ~1 h e um 401 no meio da tarde NÃO é queda de internet.
    /// Sem este delegate, todo fim de token viraria emissão pela série local, com nota
    /// fora de `nfce_emitidas` e trabalho de sincronização atrás.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? GarantirSessao { get; init; }

    /// <param name="obterToken">
    /// De onde sai o access_token do Supabase. É delegate, e não um <see cref="Nuvem"/>
    /// concreto, porque o token é privado lá dentro — assim o dono do token continua
    /// sendo a Nuvem e este arquivo não muda quando o refresh entrar. Devolver
    /// null/vazio = "sem sessão".
    /// </param>
    /// <param name="url">Base do Supabase; por padrão a do projeto.</param>
    /// <param name="cnpj">
    /// Emitente. Omitido, a edge usa o default dela (loja Savassi). Mandar explícito é
    /// o que permite dois terminais de lojas diferentes usarem a mesma edge.
    /// </param>
    public EmissorNuvem(Func<CancellationToken, Task<string?>> obterToken, string? url = null, string? cnpj = null)
    {
        _token = obterToken;
        _url = (url ?? Nuvem.UrlPadrao).TrimEnd('/');
        _cnpj = Fiscal.Digitos(cnpj) is { Length: 14 } c ? c : null;
    }

    /// <summary>
    /// A edge não tem /health, e inventar um POST de mentira pra ela custaria uma
    /// invocação e sujaria o log com 400 de propósito. Então a sonda faz o mínimo
    /// honesto: bate na plataforma (GET /auth/v1/health, resposta minúscula, sem efeito
    /// colateral) e confere que EXISTE sessão.
    ///
    /// ⚠️ O que ela NÃO devolve mais é "Ok pra qualquer HTTP". Resposta 401/403, ou rede
    /// de pé sem sessão, é <b>Bloqueio</b>: o caminho existe e a plataforma disse NÃO.
    /// Liberar o pré-voo nesse caso faria o pior desfecho possível — cobrar no TEF e só
    /// então descobrir que não dá pra emitir (plano 3.3.f).
    ///
    /// O que continua fora do alcance dela: se a EC2 e a SEFAZ estão de pé, e se o
    /// token de usuário (não a anon key) ainda vale — isso só a emissão diz, e lá o 401
    /// é tratado com renovação + UMA repetição.
    /// Série e próximo nNF ficam null porque moram na nfce_config, com RLS fechada.
    /// </summary>
    public async Task<SaudeEmissor> SondarAsync(CancellationToken ct)
    {
        var (token, renovacaoExpirou) = await TokenComRelogioAsync(ct, TempoSessaoSonda).ConfigureAwait(false);

        // COM sessão a sonda percorre a CADEIA INTEIRA (edge → EC2), não só a
        // plataforma. Lição de 06/08: Supabase verde + EC2 sem rota pra SEFAZ = o
        // resolvedor insistia na nuvem, toda venda morria em timeout e a contingência
        // local, saudável, ficava parada olhando. Sonda que não percorre a cadeia
        // inteira é sonda que mente.
        if (!string.IsNullOrWhiteSpace(token))
            return await PingAsync(token!, ct).ConfigureAwait(false);

        var (http, erroRede) = await BaterNoCaminhoAsync(ct).ConfigureAwait(false);

        // Nem resposta: queda de rede de verdade. NÃO é bloqueio — é exatamente o caso
        // em que a contingência local tem que assumir e a venda continuar.
        if (http is null)
            return new SaudeEmissor(false, null, _cnpj, null, 0, null, erroRede ?? "a nuvem não respondeu");

        if (http is 401 or 403)
            return new SaudeEmissor(false, null, _cnpj, null, 0, null,
                http == 403 ? MsgSemPapel : MsgSemSessao, Bloqueio: true);

        if (string.IsNullOrWhiteSpace(token))
            // Só é BLOQUEIO quando a resposta é definitiva. Renovação que estourou o
            // relógio é "não sei" (rede ruim conta como queda), e "não sei" não pode
            // travar a loja — libera a contingência em vez de bloquear.
            //
            // Com resposta definitiva o raciocínio é o inverso: a plataforma respondeu, a
            // rede está de pé e mesmo assim não há sessão ⇒ login/configuração. Cair pra
            // contingência aqui queimaria numeração da série local por um problema que
            // relogar resolve em 10 segundos.
            return renovacaoExpirou
                ? new SaudeEmissor(false, null, _cnpj, null, 0, null,
                    "a renovação da sessão com a nuvem não respondeu a tempo")
                : new SaudeEmissor(false, null, _cnpj, null, 0, null, MsgSemSessao, Bloqueio: true);

        if (http is < 200 or > 299)
            // Plataforma de pé mas doente (5xx, 404 de proxy). Não é config do caixa:
            // deixa a contingência assumir em vez de travar a loja.
            return new SaudeEmissor(false, null, _cnpj, null, 0, null, $"a nuvem respondeu HTTP {http}");

        return new SaudeEmissor(true, null, _cnpj, null, 0, null, null);
    }

    /// <summary>
    /// O ping da cadeia: a edge autentica, confere papel e testa a EC2. Cada status
    /// conta uma história diferente — e duas merecem nota:
    ///  · 400 = edge ANTIGA (sem o ramo do ping) validou "itens vazios". Isso PROVA
    ///    plataforma+edge+sessão+papel de pé, que é tudo que a sonda velha provava —
    ///    então é Ok, senão o exe novo com edge velha mandaria toda venda pra
    ///    contingência à toa.
    ///  · 503 = a edge respondeu "minha EC2 não responde". Não é config do caixa, é o
    ///    caminho da nuvem fora do ar → contingência local assume.
    /// </summary>
    private async Task<SaudeEmissor> PingAsync(string token, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TempoSondaPing);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_url}/functions/v1/nfce-emitir");
            req.Headers.TryAddWithoutValidation("apikey", Nuvem.AnonKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent("{\"ping\":true}", Encoding.UTF8, "application/json");

            using var resp = await Fiscal.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var status = (int)resp.StatusCode;

            if (status is 200 or 400)
                return new SaudeEmissor(true, null, _cnpj, null, 0, null, null);
            if (status is 401 or 403)
                return new SaudeEmissor(false, null, _cnpj, null, 0, null,
                    status == 403 ? MsgSemPapel : MsgSemSessao, Bloqueio: true);

            return new SaudeEmissor(false, null, _cnpj, null, 0, null,
                $"a ponte fiscal da nuvem não está emitindo (HTTP {status})");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SaudeEmissor(false, null, _cnpj, null, 0, null, Fiscal.Motivo(ex));
        }
    }

    public Task<ResultadoEmissao> EmitirAsync(IReadOnlyList<ItemFiscal> itens,
        IReadOnlyList<PagamentoFiscal> pagamentos, string? documento, CancellationToken ct)
        => EmitirAsync(itens, pagamentos, documento, null, ct);

    public Task<ResultadoEmissao> EmitirAsync(IReadOnlyList<ItemFiscal> itens,
        IReadOnlyList<PagamentoFiscal> pagamentos, string? documento, string? referencia, CancellationToken ct)
    {
        var problema = Fiscal.Conferir(itens, pagamentos);
        if (problema is not null) return Task.FromResult(ResultadoEmissao.NaoEmitida(Caminho, problema));

        return PostarAsync(itens, pagamentos, documento, referencia, false, ct);
    }

    private async Task<ResultadoEmissao> PostarAsync(IReadOnlyList<ItemFiscal> itens,
        IReadOnlyList<PagamentoFiscal> pagamentos, string? documento, string? referencia,
        bool segundaTentativa, CancellationToken ct)
    {
        var (token, renovacaoExpirou) = await TokenComRelogioAsync(ct, TempoEmissao, segundaTentativa)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(token))
        {
            if (renovacaoExpirou)
                return ResultadoEmissao.ForaDoAr(Caminho, "a renovação da sessão com a nuvem não respondeu a tempo");

            // Documento fiscal não sai sem sessão. Mas o motivo importa: se a plataforma
            // responde, o problema é login (BLOQUEIA); se não responde, é rede (a
            // contingência local assume). Um GET minúsculo separa os dois.
            var (http, erroRede) = await BaterNoCaminhoAsync(ct).ConfigureAwait(false);
            return http is null
                ? ResultadoEmissao.ForaDoAr(Caminho, erroRede ?? "a nuvem não respondeu")
                : ResultadoEmissao.Bloqueio(Caminho, MsgSemSessao);
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TempoEmissao);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_url}/functions/v1/nfce-emitir");
            // apikey passa no verify_jwt da plataforma; o Bearer TEM que ser de usuário
            // real, senão o getUser() da edge devolve vazio e volta 401.
            req.Headers.TryAddWithoutValidation("apikey", Nuvem.AnonKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(Wire.CorpoNuvem(itens, pagamentos, documento, _cnpj, referencia),
                Encoding.UTF8, "application/json");

            using var resp = await Fiscal.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var corpo = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var status = (int)resp.StatusCode;

            // 401 = token vencido no meio do dia. Renova e repete UMA vez (mesmo padrão do
            // ClienteTef). Repetir um POST de emissão só é seguro porque o 401 nasce ANTES
            // da função rodar (verify_jwt/getUser), então nenhuma numeração foi consumida —
            // e ainda assim conferimos o corpo: se ele trouxer `autorizado`, a nota existe
            // e repetir criaria uma SEGUNDA nota. Nunca transforme isto num laço.
            if (status == 401 && !segundaTentativa && !Fiscal.TemDesfechoFiscal(corpo)
                && await RenovarAsync(ct).ConfigureAwait(false))
                return await PostarAsync(itens, pagamentos, documento, referencia, true, ct).ConfigureAwait(false);

            if ((status is 401 or 403) && !Fiscal.TemDesfechoFiscal(corpo))
                // Sobreviveu à renovação: é configuração (conta sem papel, projeto errado,
                // anon key trocada), não queda de rede. BLOQUEIA — não cai pro agente.
                return ResultadoEmissao.Bloqueio(Caminho, status == 403 ? MsgSemPapel : MsgSemSessao);

            // Mesmo aqui o `ok` do corpo é ignorado: a regra é uma só nos dois caminhos.
            return Fiscal.LerResposta(corpo, status, Caminho);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ResultadoEmissao.ForaDoAr(Caminho, Fiscal.Motivo(ex));
        }
    }

    /// <summary>
    /// Bate na plataforma e devolve o STATUS HTTP — ou null quando NEM RESPOSTA houve.
    /// Essa distinção é a espinha dorsal da escolha de caminho: respondeu = rede de pé
    /// (se disse não, é configuração e bloqueia); não respondeu = queda de rede, e aí
    /// sim o agente local assume.
    /// </summary>
    private async Task<(int? Http, string? Erro)> BaterNoCaminhoAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TempoSonda);

            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_url}/auth/v1/health");
            req.Headers.TryAddWithoutValidation("apikey", Nuvem.AnonKey);

            using var resp = await Fiscal.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            return ((int)resp.StatusCode, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, Fiscal.Motivo(ex));
        }
    }

    /// <summary>
    /// Token com relógio próprio. O segundo item da tupla é o que separa "a plataforma
    /// disse que não tenho sessão" (definitivo ⇒ bloqueio) de "a renovação não respondeu"
    /// (indefinido ⇒ rede, libera contingência). Confundir os dois trava a loja por causa
    /// de internet lenta, ou põe o caixa em contingência por causa de login vencido.
    /// </summary>
    private async Task<(string? Token, bool Expirou)> TokenComRelogioAsync(
        CancellationToken ct, TimeSpan limite, bool jaRenovou = false)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limite);
        try
        {
            return (await TokenAsync(cts.Token, jaRenovou).ConfigureAwait(false), false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // desistência do chamador sobe; o nosso relógio, não
        }
        catch (OperationCanceledException)
        {
            return (null, true);
        }
    }

    /// <summary>Token, com uma renovação quando ele vem vazio. Provedor quebrado vira "sem sessão", não exceção.</summary>
    private async Task<string?> TokenAsync(CancellationToken ct, bool jaRenovou = false)
    {
        string? t;
        try { t = await _token(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { t = null; }

        if (!string.IsNullOrWhiteSpace(t) || jaRenovou) return t;
        if (!await RenovarAsync(ct).ConfigureAwait(false)) return t;

        try { return await _token(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Devolve false quando não há delegate de renovação — de propósito: quem chama usa
    /// isto como porteiro do retry, e repetir sem ter renovado nada seria só gastar uma
    /// segunda invocação pra tomar o mesmo 401.
    /// </summary>
    private async Task<bool> RenovarAsync(CancellationToken ct)
    {
        if (GarantirSessao is null) return false;
        try { return await GarantirSessao(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }
}

/// <summary>Veredito da conferência de boot (<see cref="EmissorResolvido.ConferirBootAsync"/>).</summary>
/// <param name="PodeOperar">false = não abrir o caixa. O motivo está em <paramref name="Impedimento"/>.</param>
/// <param name="Impedimento">Erro que exige intervenção antes de vender (série colidindo, ambiente trocado, nenhum caminho).</param>
/// <param name="Aviso">Opera, mas com ressalva — tipicamente "sem contingência local se a internet cair".</param>
public sealed record ConferenciaDeBoot(bool PodeOperar, string? Impedimento, string? Aviso,
    SaudeEmissor Nuvem, SaudeEmissor? Agente);

/// <summary>
/// Decide por qual caminho a nota sai: <b>NUVEM PRIMEIRO</b>, agente local só quando a
/// rede realmente não responde (decisão do dono — ver cabeçalho do arquivo).
/// A escolha vale por <see cref="Intervalo"/> (30 s) — sondar a cada venda custaria
/// 1,2 s de espera no pré-voo de todo cliente na fila.
///
/// Três regras que parecem detalhe e não são:
///  1. <b>401/403 não é "sem internet".</b> Vem como <see cref="SaudeEmissor.Bloqueio"/> /
///     <see cref="ResultadoEmissao.Bloqueado"/> e PARA a venda com mensagem clara, em vez
///     de mandar o caixa pra contingência à toa (queimando numeração da série local e
///     gerando trabalho de sincronização).
///  2. <b>Trava de série.</b> O agente só é usado depois de provado que a série dele é
///     diferente da série da nuvem (<see cref="SerieNuvem"/>). Série igual nos dois
///     caminhos é Rejeição 539 em cascata, e ela só aparece na hora da venda.
///  3. <b>Nunca reemitir a mesma venda pelo outro caminho.</b> O fallback acontece na
///     ESCOLHA do caminho, não no meio de um POST que ficou sem resposta.
/// </summary>
public sealed class EmissorResolvido : IEmissorFiscal, IDisposable
{
    public const string SemCaminho = "nenhum";

    /// <summary>De quanto em quanto tempo a escolha é reavaliada. 30 s: rápido pra voltar sozinho, barato pro caixa.</summary>
    public static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(30);

    private readonly IEmissorFiscal _nuvem;
    private readonly IEmissorFiscal? _agente;
    private readonly TimeSpan _intervalo;
    private readonly SemaphoreSlim _trava = new(1, 1);

    private IEmissorFiscal? _escolhido;
    private SaudeEmissor? _ultima;

    // Relógio MONOTÔNICO (Environment.TickCount64), não DateTime.Now: um ajuste de NTP
    // para trás deixaria a diferença negativa, "negativo < intervalo" é verdadeiro, e a
    // escolha congelava — inclusive um caminho que já tinha voltado continuava marcado
    // como fora. TickCount64 não anda com o relógio de parede e não dá a volta.
    private long _sondadoEmMs;
    private volatile bool _sondaValida;
    private Timer? _sondaDeFundo;

    private volatile string _caminho = SemCaminho;
    private volatile string? _bloqueioDeBoot;

    /// <param name="nuvem">Caminho PRIMÁRIO. Obrigatório.</param>
    /// <param name="agenteLocal">
    /// Contingência. Null = só a nuvem; aí "cair pra contingência" simplesmente não existe
    /// e a venda para quando a internet cai.
    /// ⚠️ A ordem dos parâmetros INVERTEU em 08/2026 (era agente primeiro). Se você está
    /// portando código antigo, confira quem é quem — trocar os dois faz o PDV emitir pelo
    /// agente com a internet perfeita, que é justamente o que a decisão do dono proíbe.
    /// </param>
    public EmissorResolvido(IEmissorFiscal nuvem, IEmissorFiscal? agenteLocal = null, TimeSpan? intervaloSonda = null)
    {
        _nuvem = nuvem;
        _agente = agenteLocal;
        _intervalo = intervaloSonda ?? Intervalo;
    }

    /// <summary>
    /// Série em que a NUVEM numera (`nfce_config.serie`). É o outro lado da comparação que
    /// pega a Rejeição 539 em cascata (risco 12 do plano) ANTES de ela virar venda perdida.
    /// A sonda da nuvem não consegue ler isto sozinha (nfce_config tem RLS fechada, só o
    /// service_role lê), então vem de configuração.
    ///
    /// Não preencher NÃO desliga a contingência — desligar seria trocar um risco por outro
    /// pior: a loja parada toda vez que a internet cai. O que acontece é a conferência de
    /// boot avisar, alto, que está operando sem essa prova.
    /// </summary>
    public int? SerieNuvem { get; init; }

    /// <summary>
    /// Ambiente que este terminal espera (`terminal.ambiente`: 1 produção, 2 homologação).
    /// A armadilha real: `agent-config.json` está com tpAmb=1 (PRODUÇÃO) e o terminal com
    /// default 2. Nota emitida em produção por engano não se desfaz — só se cancela em 30 min.
    /// </summary>
    public int? TpAmbEsperado { get; init; }

    /// <summary>"nuvem" | "agente" | "nenhum" — pra gravar em nfce_emissao.caminho e mostrar no badge.</summary>
    public string CaminhoAtual => _caminho;

    /// <summary>Por que a nuvem está fora, quando estiver. É o texto do aviso "emitindo pela contingência local".</summary>
    public string? QuedaDaNuvem { get; private set; }

    /// <summary>
    /// Por que a contingência local NÃO pode assumir, quando não puder (agente fora, série
    /// colidindo, ambiente divergente). Só é reavaliada quando a nuvem cai ou em
    /// <see cref="ConferirBootAsync"/> — enquanto a nuvem responde, ninguém sonda o agente.
    /// </summary>
    public string? ContingenciaLocal { get; private set; }

    /// <summary>
    /// Conferência de boot (Passo 10 do plano). Sonda os dois caminhos e RECUSA operar
    /// quando a série do agente é igual à da nuvem ou quando o ambiente diverge — os dois
    /// erros só apareceriam na hora da venda, um como Rejeição 539 em cascata e o outro
    /// como nota emitida em produção sem querer.
    ///
    /// O impedimento fica TRAVADO aqui dentro: enquanto não passar uma conferência limpa,
    /// nem sondar nem emitir funcionam. Rodar de novo depois de corrigir destrava.
    /// </summary>
    public async Task<ConferenciaDeBoot> ConferirBootAsync(CancellationToken ct)
    {
        var nuvem = await _nuvem.SondarAsync(ct).ConfigureAwait(false);
        var agente = _agente is null ? null : await _agente.SondarAsync(ct).ConfigureAwait(false);

        var colisao = agente is null ? null : ColisaoDeSerie(agente);
        var barrada = agente is null
            ? "não há emissor local configurado neste terminal"
            : colisao ?? AgenteIndisponivel(agente);

        ContingenciaLocal = barrada;

        string? impedimento = null;
        if (colisao is not null)
            impedimento = colisao;
        else if (nuvem.Bloqueio)
            impedimento = nuvem.Erro ?? "a nuvem recusou este caixa";
        else if (!nuvem.Ok && barrada is not null)
            impedimento = $"nenhum caminho de emissão disponível (nuvem: {nuvem.Erro ?? "fora"} · " +
                          $"contingência local: {barrada})";

        // Trava só o que é PROVA de dano fiscal (série igual, ambiente trocado). Nuvem fora
        // por rede não pode travar o boot da loja: para isso existe a contingência.
        _bloqueioDeBoot = colisao;

        var naoConferida = agente is null ? null : SerieNaoConferida(agente);
        string? aviso = null;
        if (impedimento is not null)
            aviso = null;
        else if (barrada is not null)
            aviso = $"sem contingência local: {barrada}. Se a internet cair, a venda para.";
        else if (naoConferida is not null)
            aviso = $"contingência local ligada SEM conferência de série: {naoConferida}. " +
                    "Preencha SerieNuvem (nfce_config.serie): é o que impede Rejeição 539 em cascata.";

        return new ConferenciaDeBoot(impedimento is null, impedimento, aviso, nuvem, agente);
    }

    public async Task<SaudeEmissor> SondarAsync(CancellationToken ct)
    {
        if (_bloqueioDeBoot is { } travado)
            return new SaudeEmissor(false, null, null, null, 0, null, travado, Bloqueio: true);

        await _trava.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ultima is not null && Fresca) return _ultima;

            var saude = await _nuvem.SondarAsync(ct).ConfigureAwait(false);

            if (saude.Ok)
            {
                _escolhido = _nuvem;
                _caminho = EmissorNuvem.Caminho;
                QuedaDaNuvem = null;
            }
            else if (saude.Bloqueio)
            {
                // A nuvem RESPONDEU e disse não (401 que sobreviveu à renovação, 403, sem
                // sessão com a rede de pé). Isso é configuração, não queda: cair pro agente
                // aqui colocaria o caixa em contingência à toa, queimando a série local e
                // escondendo o erro justamente de quem pode consertá-lo.
                _escolhido = null;
                _caminho = SemCaminho;
                QuedaDaNuvem = saude.Erro;
            }
            else
            {
                QuedaDaNuvem = saude.Erro;
                saude = await CairParaContingenciaAsync(saude, ct).ConfigureAwait(false);
            }

            _ultima = saude;
            Volatile.Write(ref _sondadoEmMs, Environment.TickCount64);
            _sondaValida = true;
            return saude;
        }
        finally
        {
            _trava.Release();
        }
    }

    public Task<ResultadoEmissao> EmitirAsync(IReadOnlyList<ItemFiscal> itens,
        IReadOnlyList<PagamentoFiscal> pagamentos, string? documento, CancellationToken ct)
        => EmitirAsync(itens, pagamentos, documento, null, ct);

    public async Task<ResultadoEmissao> EmitirAsync(IReadOnlyList<ItemFiscal> itens,
        IReadOnlyList<PagamentoFiscal> pagamentos, string? documento, string? referencia, CancellationToken ct)
    {
        if (_bloqueioDeBoot is { } travado)
            return ResultadoEmissao.Bloqueio(SemCaminho, travado);

        var alvo = _escolhido is not null && Fresca ? _escolhido : await EscolherAsync(ct).ConfigureAwait(false);
        if (alvo is null)
        {
            var motivo = _ultima?.ErroCertificado ?? _ultima?.Erro ?? "nenhum emissor disponível";
            return _ultima?.Bloqueio == true
                ? ResultadoEmissao.Bloqueio(SemCaminho, motivo)
                : ResultadoEmissao.ForaDoAr(SemCaminho, motivo);
        }

        var r = await alvo.EmitirAsync(itens, pagamentos, documento, referencia, ct).ConfigureAwait(false);

        // Emissor mudo (ou plataforma dizendo não) no meio da emissão: a próxima venda
        // re-sonda e pode ir pro outro caminho.
        // ⚠️ O que NÃO se faz é reemitir ESTA MESMA venda pelo outro caminho agora: socket
        // morto não diz se a nota foi assinada e transmitida, e insistir do outro lado
        // produz DUAS notas pra uma venda — dano fiscal de verdade, bem pior que o operador
        // apertar "tentar de novo" sabendo o que está fazendo. O fallback da decisão do
        // dono é na ESCOLHA do caminho, não dentro de uma emissão já em voo.
        if (r.Indisponivel || r.Bloqueado) _sondaValida = false;

        return r;
    }

    /// <summary>
    /// Re-sonda em SEGUNDO PLANO, um pouco antes de a escolha vencer. Sem isto, a
    /// primeira venda depois de cada janela de 30 s pagava o pré-voo inteiro na frente
    /// do cliente — com a nuvem doente, até ~8 s parado em "Emitindo" só pra descobrir
    /// que o caminho é o agente. Com o relógio, quem espera a sonda é o processo, não a
    /// fila do balcão. Falha aqui nunca sobe: a sonda da própria venda continua sendo o
    /// plano B.
    /// </summary>
    public void LigarSondaDeFundo()
    {
        var folga = TimeSpan.FromSeconds(5);
        var periodo = _intervalo > folga * 2 ? _intervalo - folga : _intervalo;
        _sondaDeFundo ??= new Timer(_ => _ = RessondarAsync(), null, TimeSpan.Zero, periodo);
    }

    private async Task RessondarAsync()
    {
        try
        {
            _sondaValida = false;   // força a passada a re-sondar em vez de ler o cache
            await SondarAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* sonda de fundo não derruba nada */ }
    }

    public void Dispose()
    {
        _sondaDeFundo?.Dispose();
        _trava.Dispose();
    }

    /// <summary>Vale a escolha em cache? Relógio monotônico — ver o campo <c>_sondadoEmMs</c>.</summary>
    private bool Fresca => _sondaValida
        && Environment.TickCount64 - Volatile.Read(ref _sondadoEmMs) < (long)_intervalo.TotalMilliseconds;

    private async Task<IEmissorFiscal?> EscolherAsync(CancellationToken ct)
    {
        await SondarAsync(ct).ConfigureAwait(false);
        return _escolhido;
    }

    /// <summary>
    /// A nuvem não respondeu (rede). Este é o caso para o qual a contingência local existe:
    /// o agente assume e a venda continua — desde que a trava de série deixe.
    /// </summary>
    private async Task<SaudeEmissor> CairParaContingenciaAsync(SaudeEmissor nuvem, CancellationToken ct)
    {
        if (_agente is null)
        {
            _escolhido = null;
            _caminho = SemCaminho;
            ContingenciaLocal = "não há emissor local configurado neste terminal";
            return nuvem;
        }

        var agente = await _agente.SondarAsync(ct).ConfigureAwait(false);
        // Barra por PROVA de dano (série igual / ambiente trocado) ou porque o agente não
        // está apto. Não conseguir CONFERIR a série não barra aqui: isso é aviso da
        // conferência de boot. Barrar por falta de config deixaria a loja parada em toda
        // queda de internet, que é justamente o que a contingência existe pra evitar.
        var barrada = ColisaoDeSerie(agente) ?? AgenteIndisponivel(agente);
        ContingenciaLocal = barrada;

        if (barrada is null)
        {
            _escolhido = _agente;
            _caminho = EmissorAgente.Caminho;
            return agente;
        }

        // Os dois fora: o erro que interessa ao operador é o do caminho primário (a nuvem),
        // mas o motivo da contingência não assumir vai junto — sem ele o operador fica
        // olhando "sem internet" sem saber por que o agente, que está ligado, não emitiu.
        _escolhido = null;
        _caminho = SemCaminho;
        return nuvem with { Erro = $"{nuvem.Erro ?? "a nuvem não respondeu"} · contingência local: {barrada}" };
    }

    /// <summary>
    /// Erro de CONFIGURAÇÃO entre os dois caminhos. Não é "o agente está fora" — é "usar o
    /// agente vai causar dano fiscal". Por isso trava o boot.
    /// </summary>
    private string? ColisaoDeSerie(SaudeEmissor agente)
    {
        if (agente.Serie is int sa && SerieNuvem is int sn && sa == sn)
            return $"o emissor local e a nuvem estão na MESMA série ({sa}). Dois contadores na mesma série " +
                   "geram Rejeição 539 (duplicidade) em cascata, e o erro só aparece na hora da venda. " +
                   "Ajuste a série no agent-config.json ou na nfce_config antes de operar.";

        if (agente.TpAmb is int ta && TpAmbEsperado is int te && ta != te)
            return $"o emissor local está em {Ambiente(ta)} e este terminal está configurado para {Ambiente(te)}. " +
                   "Nota emitida em produção por engano não se desfaz: só se cancela, e em até 30 min.";

        return null;
    }

    /// <summary>O agente não está apto agora (fora, certificado ruim, impostor na porta). Nada foi danificado.</summary>
    private static string? AgenteIndisponivel(SaudeEmissor agente)
        => agente.Ok ? null : agente.ErroCertificado ?? agente.Erro ?? "o emissor local não respondeu";

    /// <summary>
    /// Faltou o dado pra comparar as séries. É AVISO, não bloqueio: sem prova de colisão,
    /// barrar a contingência custaria a loja parada em toda queda de internet. Quem tem que
    /// ver isto é o dono, no boot, com tempo de corrigir — não o operador com fila no caixa.
    /// </summary>
    private string? SerieNaoConferida(SaudeEmissor agente)
    {
        if (SerieNuvem is null)
            return "não sei em que série a nuvem numera (SerieNuvem não configurada), " +
                   "então não dá pra provar que ela é diferente da série do agente";

        if (agente.Serie is null) return "o emissor local não informou a série no /health";

        return null;
    }

    private static string Ambiente(int tpAmb) => tpAmb == 1 ? "PRODUÇÃO" : "homologação";
}

// ── FORMATO DE FIO ───────────────────────────────────────────────────────────
// Tipos `file`: só existem neste arquivo, então não colidem com nada do resto do
// projeto e ninguém serializa por engano usando eles.
//
// NENHUMA JsonNamingPolicy: os nomes são literais do contrato ("tPag", "CNPJ",
// "cAut", "vUnit") e case-sensitive dos dois lados — camelCase automático viraria
// "cnpj" e o motor perderia o grupo <card>. Por isso [JsonPropertyName] em tudo.

file sealed record ItemDto(
    [property: JsonPropertyName("codigo")] string Codigo,
    [property: JsonPropertyName("descricao")] string Descricao,
    [property: JsonPropertyName("unidade")] string Unidade,
    [property: JsonPropertyName("qtd")] decimal Qtd,
    [property: JsonPropertyName("vUnit")] decimal VUnit,
    [property: JsonPropertyName("ncm")] string? Ncm,
    [property: JsonPropertyName("cest")] string? Cest,
    [property: JsonPropertyName("csosn")] string? Csosn,
    [property: JsonPropertyName("cfop")] string? Cfop,
    // O motor lê `it.origem` por item e, sem ele, TODO item cai no default do emitente
    // (0 = nacional): item importado sairia com <orig>0</orig> no ICMSSN, divergindo do
    // que o PDV gravou em venda_item.origem. A edge ignora o campo — corpo continua válido.
    [property: JsonPropertyName("origem")] int? Origem,
    // 03/09: desconto por item (promoção de carrinho). Só vai quando > 0.
    [property: JsonPropertyName("vDesc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? VDesc = null);

file sealed record CardDto(
    [property: JsonPropertyName("cAut")] string CAut,
    [property: JsonPropertyName("CNPJ")] string Cnpj,
    [property: JsonPropertyName("tBand")] string? TBand);

file sealed record PagamentoDto(
    [property: JsonPropertyName("tPag")] string TPag,
    [property: JsonPropertyName("valor")] decimal Valor,
    [property: JsonPropertyName("card")] CardDto? Card);

file sealed record VendaDto(
    [property: JsonPropertyName("itens")] IReadOnlyList<ItemDto> Itens,
    [property: JsonPropertyName("pagamentos")] IReadOnlyList<PagamentoDto> Pagamentos);

/// <summary>Corpo do agente: `documento` na RAIZ — é ele que costura pra dentro de `sale`.</summary>
file sealed record CorpoAgenteDto(
    [property: JsonPropertyName("sale")] VendaDto Sale,
    [property: JsonPropertyName("documento")] string? Documento);

/// <summary>Corpo da edge: itens e pagamentos na RAIZ, sem `sale`. Não é engano — é o contrato dela.</summary>
file sealed record CorpoNuvemDto(
    [property: JsonPropertyName("itens")] IReadOnlyList<ItemDto> Itens,
    [property: JsonPropertyName("pagamentos")] IReadOnlyList<PagamentoDto> Pagamentos,
    [property: JsonPropertyName("documento")] string? Documento,
    [property: JsonPropertyName("cnpj")] string? Cnpj,
    // id da venda local → nfce_emitidas.pdv_ref: é o fio que liga um timeout à
    // pergunta "saiu nota pra esta venda?" respondível por consulta.
    [property: JsonPropertyName("pdv_ref")] string? PdvRef);

file static class Wire
{
    private static readonly JsonSerializerOptions Opcoes = new()
    {
        PropertyNamingPolicy = null,
        // Campo opcional vazio é campo OMITIDO, não `null`: o motor testa presença
        // (`it.cfop ? String(it.cfop) : undefined`), e null explícito em ncm/cest o
        // faz cair em caminho de erro em vez de usar o default do emitente.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string CorpoAgente(IReadOnlyList<ItemFiscal> itens,
        IReadOnlyList<PagamentoFiscal> pagamentos, string? documento)
        => JsonSerializer.Serialize(
            new CorpoAgenteDto(new VendaDto(Itens(itens), Pagamentos(pagamentos)),
                Fiscal.DocumentoParaNota(documento)), Opcoes);

    internal static string CorpoNuvem(IReadOnlyList<ItemFiscal> itens,
        IReadOnlyList<PagamentoFiscal> pagamentos, string? documento, string? cnpj, string? pdvRef)
        => JsonSerializer.Serialize(
            new CorpoNuvemDto(Itens(itens), Pagamentos(pagamentos),
                Fiscal.DocumentoParaNota(documento), cnpj, pdvRef), Opcoes);

    private static List<ItemDto> Itens(IReadOnlyList<ItemFiscal> itens)
    {
        var lista = new List<ItemDto>(itens.Count);
        foreach (var i in itens)
        {
            lista.Add(new ItemDto(
                Codigo: i.Codigo.Trim(),
                Descricao: i.Descricao.Trim(),
                Unidade: string.IsNullOrWhiteSpace(i.Unidade) ? "UN" : i.Unidade.Trim(),
                Qtd: i.Qtd,
                VUnit: i.VUnit,
                // O cadastro às vezes traz NCM/CEST com pontos ("1905.31.00"); a SEFAZ
                // só aceita dígitos, e é aqui que a limpeza tem que acontecer — depois
                // do XML montado já é rejeição.
                Ncm: Nulo(Fiscal.Digitos(i.Ncm)),
                Cest: Nulo(Fiscal.Digitos(i.Cest)),
                Csosn: Nulo(Fiscal.Digitos(i.Csosn)),
                Cfop: Nulo(Fiscal.Digitos(i.Cfop)),
                Origem: i.Origem,
                VDesc: i.VDesc > 0 ? i.VDesc : null));
        }
        return lista;
    }

    private static List<PagamentoDto> Pagamentos(IReadOnlyList<PagamentoFiscal> pagamentos)
    {
        var lista = new List<PagamentoDto>(pagamentos.Count);
        foreach (var p in pagamentos)
        {
            var tPag = p.TPag.Trim().PadLeft(2, '0');
            lista.Add(new PagamentoDto(tPag, p.Valor, Card(tPag, p.Card)));
        }
        return lista;
    }

    /// <summary>
    /// &lt;card&gt; só nasce completo: sem cAut ou sem CNPJ de 14 dígitos o motor emite
    /// tpIntegra=2, que é a verdade (o pagamento não integrou) e passa na SEFAZ. Meio
    /// card é que seria rejeição. tBand fica como veio — forçar "99" em PIX, como o web
    /// fazia, é informação inventada.
    /// </summary>
    private static CardDto? Card(string tPag, CartaoFiscal? card)
    {
        if (card is null || !Fiscal.AceitaCard(tPag)) return null;

        var cAut = (card.CAut ?? "").Trim();
        var cnpj = Fiscal.Digitos(card.Cnpj);
        if (cAut.Length == 0 || cnpj.Length != 14) return null;

        return new CardDto(cAut[..Math.Min(20, cAut.Length)], cnpj, Nulo(card.TBand?.Trim()));
    }

    private static string? Nulo(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
