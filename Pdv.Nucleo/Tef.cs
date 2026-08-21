using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pdv.Nucleo;

/// <summary>Formas que passam pela maquininha. Dinheiro NÃO entra aqui (a edge devolve 400 para "dinheiro").</summary>
public enum TipoTef { Credito, Debito, Pix }

public static class TipoTefExtensoes
{
    /// <summary>
    /// String que a edge `tef-pagar` aceita no campo `tipo`. Ela também aceita as variantes
    /// acentuadas e em inglês, mas mandar sempre a mesma grafia deixa o log do gateway legível
    /// e é a mesma string usada em `venda_pagamento.forma` (contrato duro do fechamento de caixa).
    /// </summary>
    public static string Codigo(this TipoTef t) => t switch
    {
        TipoTef.Credito => "credito",
        TipoTef.Debito => "debito",
        TipoTef.Pix => "pix",
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, "Forma sem mapeamento no TEF."),
    };

    /// <summary>Volta da string gravada (ex.: `tef_transacao.tipo`) para o enum. Null quando não é forma de TEF.</summary>
    public static TipoTef? Analisar(string? forma) => (forma ?? "").Trim().ToLowerInvariant() switch
    {
        "credito" or "crédito" or "credit" => TipoTef.Credito,
        "debito" or "débito" or "debit" => TipoTef.Debito,
        "pix" => TipoTef.Pix,
        _ => null,
    };
}

public enum SituacaoTef { Pago, Recusado, Cancelado, Timeout, Erro }

/// <summary>
/// Dados do cartão que voltam do gateway. `CAut` e `Cnpj` são os únicos que viram XML
/// (grupo &lt;card&gt;); `Nsu` existe para conciliar com a adquirente depois.
///
/// `Valor` é o que a ADQUIRENTE registrou, e não é decoração: é o único número desta resposta
/// que dá para conferir contra o total da venda. Sem ele, uma cobrança concluída por outro valor
/// (erro de digitação na maquininha, cobrança antiga da mesma sessão sendo lida) viraria NFC-e com
/// o vNF que o PDV *acha* que recebeu — divergência que só aparece na conciliação, dias depois.
/// </summary>
public sealed record CartaoTef(string? CAut, string? Cnpj, string? TBand, string? Bandeira,
    string? Adquirente, string? Nsu, int? Parcelas, string? Terminal, decimal? Valor = null)
{
    // `Valor` entra no teste de vazio de propósito: um `card` que só traga o valor ainda precisa
    // chegar ao chamador para a conferência de valor acontecer. Tratá-lo como vazio devolveria
    // null e a conferência simplesmente não rodaria — falha silenciosa, a pior espécie.
    public bool Vazio => CAut is null && Cnpj is null && TBand is null && Bandeira is null
                         && Adquirente is null && Nsu is null && Terminal is null && Valor is null;

    /// <summary>
    /// Só vira &lt;card&gt; na NFC-e com cAut preenchido E CNPJ de 14 dígitos. Sem os dois, o motor
    /// emite `tpIntegra=2` — que é honesto e válido; forçar campo inventado é rejeição na certa.
    /// </summary>
    public bool ServeParaXml => !string.IsNullOrWhiteSpace(CAut) && (Cnpj?.Length ?? 0) == 14;
}

/// <summary>Códigos estáveis de desfecho — a tela decide o que mostrar por ELES, não pelo texto da mensagem.</summary>
public static class CodigoTef
{
    public const string Pago = "pago";
    public const string Recusado = "recusado";
    public const string Cancelado = "cancelado";
    public const string Timeout = "timeout";
    public const string SessaoExpirada = "sessao_expirada";   // 401: relogar resolve
    public const string SemPermissao = "sem_permissao";       // 403: papel da conta — é erro de CONFIGURAÇÃO
    public const string SemRede = "sem_rede";
    public const string Gateway = "gateway";                  // HTTP 200 com ok:false
    public const string Plataforma = "plataforma";            // HTTP não-2xx com {"error"} e sem `ok`

    /// <summary>
    /// A cobrança FOI CONCLUÍDA, mas por um valor diferente do da venda. Não é "pago" (não dá para
    /// emitir nota com um vNF que a adquirente não registrou) e não é "não pago" (o dinheiro entrou):
    /// é caso de conferência/estorno no balcão, com a transação gravada como órfã.
    /// </summary>
    public const string ValorDivergente = "valor_divergente";

    /// <summary>PayGo: `Resp\intpos.sts` não veio — o PayGo Windows não está rodando ou a pasta está errada.</summary>
    public const string TefNaoResponde = "tef_nao_responde";

    /// <summary>PayGo: há transação pendente de CNF/NCN; nenhuma cobrança nova sai antes de resolvê-la.</summary>
    public const string Pendencia = "pendencia";

    /// <summary>PayGo: outra operação de TEF está em andamento neste terminal.</summary>
    public const string Ocupado = "ocupado";
}

/// <summary>
/// O que a tela de pagamento precisa de um TEF — e só isso. Hoje há dois: o da nuvem
/// (<see cref="ClienteTef"/>, Smart TEF via edge function) e o PayGo Windows
/// (<c>PayGo.ClientePayGo</c>, troca de arquivos local). A tela não sabe qual está
/// atrás: cobra, recebe um <see cref="DesfechoTef"/>, grava `tef_transacao` pelos
/// reports de andamento. Quem escolhe o provedor é <c>Servicos.Tef()</c>, por config.
/// </summary>
public interface IProvedorTef
{
    /// <summary>`nuvem` ou `paygo` — vai para auditoria e para a tela de configuração.</summary>
    string Nome { get; }

    /// <inheritdoc cref="ClienteTef.CobrarAsync"/>
    Task<DesfechoTef> CobrarAsync(TipoTef tipo, Dinheiro valor, string? documento,
        int parcelas, IProgress<AndamentoTef>? andamento, CancellationToken ct);
}

/// <summary>
/// Desfecho de uma cobrança. `Motivo` é texto para gente; `Codigo` é para o código decidir.
/// </summary>
public sealed record DesfechoTef(SituacaoTef Situacao, string? PaymentIdentifier, string? ChargeId,
    CartaoTef? Cartao, string? Motivo, bool PosPodeTerFicadoOcupado)
{
    public string? Codigo { get; init; }

    /// <summary>Último `payment_status` visto — vai direto para `tef_transacao.payment_status`.</summary>
    public string? PaymentStatus { get; init; }

    /// <summary>
    /// A transação chegou a ser aprovada e foi DESFEITA (PayGo: NCN) — não existe cobrança
    /// nenhuma. A tela não pode oferecer "registrar como POS" para isto: o operador registraria
    /// um cartão que o cliente não pagou.
    /// </summary>
    public bool Desfeita { get; init; }

    public bool Pago => Situacao == SituacaoTef.Pago;
    public bool SessaoExpirada => Codigo == CodigoTef.SessaoExpirada;
    public bool SemPermissao => Codigo == CodigoTef.SemPermissao;

    /// <summary>Situação a gravar em `tef_transacao.situacao`. Cobrança não liberada no POS é ÓRFÃ, não "cancelada".</summary>
    public string SituacaoGravada => Situacao switch
    {
        SituacaoTef.Pago => "pago",
        // Valor divergente = dinheiro entrou e venda não saiu. Isso é órfão por definição:
        // alguém precisa conferir com a adquirente e estornar. Gravar "erro" esconderia dinheiro.
        _ when Codigo == CodigoTef.ValorDivergente => "orfa",
        _ when PosPodeTerFicadoOcupado => "orfa",
        SituacaoTef.Recusado => "recusado",
        SituacaoTef.Cancelado => "cancelado",
        _ => "erro",
    };

    /// <summary>
    /// Mensagem pronta para a tela, já com o aviso da maquininha quando não deu para liberar.
    /// O sufixo NÃO entra em `Motivo` de propósito: `Motivo` é o que vai para o banco/auditoria.
    ///
    /// Em venda PAGA devolve vazio: o caminho de sucesso não preenche `Motivo`, e sem o teste de
    /// `Pago` qualquer binding genérico da tela mostraria "não foi possível concluir o pagamento"
    /// numa venda aprovada — exatamente a frase que faz o operador cobrar de novo.
    /// </summary>
    public string MensagemParaTela => Pago
        ? ""
        : (Motivo ?? "não foi possível concluir o pagamento") + (PosPodeTerFicadoOcupado ? ClienteTef.SufixoPosOcupado : "");
}

/// <summary>
/// Fases reportadas por <see cref="ClienteTef.CobrarAsync"/>. `Criando` e `Aguardando` são
/// OBRIGAÇÕES DE BANCO (§3.2 do plano: INSERT/UPDATE em `tef_transacao`); `Recado` é só texto
/// para a tela e nunca deve virar `tef_transacao.situacao`.
/// </summary>
public static class FaseTef
{
    /// <summary>Antes do POST de criação: já dá para gravar `tef_transacao (situacao='criando')`.</summary>
    public const string Criando = "criando";

    /// <summary>Já existe `payment_identifier`: gravar `situacao='aguardando', payment_identifier=…`.</summary>
    public const string Aguardando = "aguardando";

    /// <summary>Só mensagem de progresso. Não mexa no banco por causa dela.</summary>
    public const string Recado = "recado";
}

/// <summary>
/// Progresso da cobrança. Existe porque o `payment_identifier` precisa chegar ao chamador
/// ENQUANTO a cobrança está viva, não só no desfecho: são até 180 s em que o caixa pode travar,
/// faltar energia ou o operador fechar o app. Sem o pid gravado nesse intervalo, a cobrança fica
/// armada na maquininha sem nenhuma linha em `tef_transacao` — não dá para cancelar, estornar nem
/// reconciliar, e o cliente paga uma cobrança que o PDV esqueceu.
/// </summary>
/// <param name="Fase">Uma das constantes de <see cref="FaseTef"/>.</param>
/// <param name="ChargeId">Identidade da cobrança do nosso lado — a mesma em todos os reports desta chamada.</param>
/// <param name="PaymentIdentifier">Só existe a partir de <see cref="FaseTef.Aguardando"/>.</param>
/// <param name="Mensagem">Texto para a tela (o mesmo de antes, para a UI não mudar).</param>
public sealed record AndamentoTef(string Fase, string ChargeId, string? PaymentIdentifier, string Mensagem);

/// <summary>
/// Desfecho da tentativa de liberar a maquininha. Tri-estado porque BOOLEANO NÃO CABE:
/// "não liberei" e "não liberei porque o cliente acabou de pagar" pedem reações OPOSTAS —
/// a primeira manda avisar para cancelar na maquininha, a segunda manda concluir a venda.
/// Colapsar as duas em `false` é o que fazia o operador cobrar de novo um cliente que já pagou.
/// </summary>
public enum LimpezaPos
{
    /// <summary>Confirmado: a cobrança saiu de "em andamento". A maquininha está livre.</summary>
    Liberado,

    /// <summary>O gateway reporta `pago`. O dinheiro ENTROU — a venda tem que ser concluída, nunca recobrada.</summary>
    PagoNoUltimoSegundo,

    /// <summary>Não deu para provar nada. Trate como órfã e avise para conferir na maquininha.</summary>
    NaoConfirmado,
}

/// <summary>
/// Resultado da limpeza. Carrega o corpo útil do `status` porque em
/// <see cref="LimpezaPos.PagoNoUltimoSegundo"/> ele é a prova do pagamento (cAut, CNPJ, valor):
/// descartar isso obrigaria a consultar de novo — e é justamente a informação que estava
/// sendo jogada fora quando este método devolvia `bool`.
/// </summary>
public sealed record ResultadoLimpeza(LimpezaPos Estado, CartaoTef? Cartao, string? PaymentStatus,
    string? PaymentIdentifier);

/// <summary>
/// Cliente do TEF (Smart TEF) pela edge `tef-pagar`.
///
/// Por que `HttpClient` cru e não um SDK: a MESMA função devolve dois contratos de erro —
/// erro de plataforma/permissão vem com HTTP real (400/401/403/405/500) e `{"error":"..."}`
/// SEM o campo `ok`; erro do gateway vem com HTTP 200 e `{"ok":false,"error":"..."}`. Quem olha
/// só o status HTTP, ou só o `ok`, trata metade das falhas como sucesso. Por isso o corpo é lido
/// SEMPRE, inclusive em resposta não-2xx.
///
/// GENERATION GUARD: não mora aqui. Quem cancela é quem chamou — este cliente só respeita o
/// CancellationToken que recebe. A tela é dona do `_tefGen`/`CancellationTokenSource` porque só
/// ela sabe qual tentativa está na frente do operador; se o guard vivesse aqui, o desfecho de uma
/// cobrança abandonada continuaria "válido" e voltaria por cima da tentativa nova.
///
/// Renovação de sessão também não mora aqui: o token é de fora (`obterToken`) e quem renova é o
/// delegate `GarantirSessao`, injetado pela tela. Este tipo só sabe pedir "garanta a sessão".
/// </summary>
public sealed class ClienteTef : IProvedorTef
{
    public string Nome => "nuvem";

    /// <summary>Cadência do polling. Consultar mais rápido não adianta: quem demora é o cliente na maquininha.</summary>
    public const int IntervaloMs = 2_000;

    /// <summary>Orçamento total da cobrança. Depois disso o cliente não vai mais concluir — e a maquininha precisa ser liberada.</summary>
    public const int TempoTotalMs = 180_000;

    public const string SufixoPosOcupado =
        " · ⚠️ a cobrança pode ter ficado na maquininha — cancele por lá antes de tentar de novo";

    private const int TempoCriarMs = 25_000;      // armar o POS pode demorar
    private const int TempoStatusMs = 10_000;     // consulta curta: ela roda a cada 2 s
    private const int TempoCancelarMs = 15_000;

    /// <summary>
    /// Envelope da limpeza inteira. Precisa cobrir o PIOR caminho, que tem TRÊS chamadas:
    /// quando o pid nunca chegou, a limpeza faz status de descoberta (10 s) + cancelar (15 s)
    /// + status de confirmação (10 s) = 35 s.
    ///
    /// E é justamente esse caminho que acontece quando a cobrança está VIVA na maquininha —
    /// ou seja, quando a limpeza mais importa. Envelope curto corta a consulta final, a
    /// transação vira órfã e o operador leva o aviso de "cancele por lá" sem prova nenhuma.
    /// Aviso que aparece sem motivo é aviso que o operador aprende a ignorar, e aí ele
    /// também ignora quando é verdade.
    /// </summary>
    private const int TempoLimpezaMs = 40_000;

    private const int Max401Seguidos = 3;

    /// <summary>
    /// Folga na conferência do valor pago (2 centavos) — a mesma tolerância da RPC
    /// `pdv_registrar_venda`. Serve para arredondamento, não para "quase certo".
    /// </summary>
    private const decimal ToleranciaValor = 0.02m;

    /// <summary>Quantas vezes perguntar por `charge_id` quando a criação não devolveu resposta.</summary>
    private const int TentativasResgate = 2;

    private const string MsgSessao = "sessão expirada — saia e entre no sistema novamente";
    private const string MsgSemPermissao = "esta conta não tem permissão para operar o TEF";
    private const string MsgSemRede = "sem conexão com o TEF";
    private const string MsgTimeout = "tempo esgotado — o cliente não concluiu na maquininha";
    private const string MsgCancelado = "cobrança cancelada pelo operador";
    private const string MsgRecusado = "pagamento não aprovado";

    private static readonly JsonSerializerOptions Json =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// HttpClient COMPARTILHADO do processo — o mesmo do <see cref="Fiscal"/>. Não é "o cliente do
    /// fiscal": é o cliente HTTP do app, que hoje mora lá.
    ///
    /// POR QUE NÃO TER O NOSSO: um segundo <c>SocketsHttpHandler</c> aqui criava DUAS fontes de
    /// proxy no mesmo processo (o parâmetro do construtor e o `Fiscal.ProxyUrl` estático).
    /// Configurar só uma deixa a outra saindo direto — e o dono opera às vezes da China, onde
    /// "saindo direto" quer dizer que o TEF (ou a emissão) para de funcionar sem nenhuma mensagem
    /// que explique o motivo. Fonte única: `config['proxy_url']` → <see cref="Fiscal.ProxyUrl"/>,
    /// definido no boot ANTES da primeira chamada HTTP. De quebra some o handler que nunca era
    /// descartado e o `UseProxy` no default `true`, que herdava o proxy do WinINET sem querer.
    ///
    /// O `Timeout` dele é infinito de propósito: quem limita cada chamada é o CTS de
    /// <see cref="EnviarAsync"/> (25 s para criar, 10 s para consultar). Um timeout global num
    /// cliente compartilhado seria o menor de todos, para todo mundo.
    /// </summary>
    private static HttpClient Http => Fiscal.Http;

    private readonly string _endpoint;
    private readonly string _anonKey;
    private readonly Func<CancellationToken, Task<string?>> _obterToken;

    /// <summary>
    /// Renovação/relogin da sessão Supabase — implementada FORA daqui (`Nuvem` hoje não guarda
    /// refresh_token). Chamada antes de armar a maquininha e uma única vez por 401. Sem ela, o
    /// token de ~1 h vence com o caixa logado o dia inteiro e o cartão para de funcionar à tarde.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? GarantirSessao { get; init; }

    /// <summary>Serial da maquininha (`config['tef_serial_pos']`). Null = a edge escolhe o terminal padrão.</summary>
    public string? SerialPos { get; init; }

    /// <param name="obterToken">Devolve o access_token do usuário REAL. A anon key no Authorization passa no verify_jwt mas dá 401 no getUser().</param>
    /// <param name="urlBase">Default: <see cref="Nuvem.UrlPadrao"/>.</param>
    /// <param name="anonKey">Default: <see cref="Nuvem.AnonKey"/> — vai no header `apikey`.</param>
    /// <remarks>
    /// NÃO tem parâmetro de proxy aqui, e isso é de propósito: proxy é <see cref="Fiscal.ProxyUrl"/>,
    /// fonte única do processo (ver o comentário de <see cref="Http"/>). Duas fontes = uma delas
    /// sai direto e falha sem explicação.
    /// </remarks>
    public ClienteTef(Func<CancellationToken, Task<string?>> obterToken, string? urlBase = null,
        string? anonKey = null)
    {
        _obterToken = obterToken ?? throw new ArgumentNullException(nameof(obterToken));
        _endpoint = (urlBase ?? Nuvem.UrlPadrao).TrimEnd('/') + "/functions/v1/tef-pagar";
        _anonKey = anonKey ?? Nuvem.AnonKey;
    }

    // ------------------------------------------------------------------ cobrança

    /// <summary>
    /// Cria a cobrança e acompanha até o desfecho. Nunca lança por erro de rede — falha de
    /// transporte é estado esperado num caixa de loja e volta como <see cref="SituacaoTef.Erro"/>.
    ///
    /// Cancelamento é responsabilidade do chamador: cancele o `ct` e o desfecho volta
    /// <see cref="SituacaoTef.Cancelado"/> já com a maquininha liberada (ou com o aviso de que não deu).
    ///
    /// `andamento` NÃO é enfeite de UI: os reports `criando`/`aguardando` são o único momento em que
    /// o `charge_id` e o `payment_identifier` existem antes do desfecho. Grave `tef_transacao` neles
    /// (§3.2 do plano) — sem isso, queda de energia ou app fechado no meio dos 180 s deixa a cobrança
    /// viva na maquininha sem nenhuma linha no banco para cancelar, estornar ou reconciliar.
    /// </summary>
    public async Task<DesfechoTef> CobrarAsync(TipoTef tipo, Dinheiro valor, string? documento,
        int parcelas, IProgress<AndamentoTef>? andamento, CancellationToken ct)
    {
        var chargeId = NovoChargeId();

        if (!valor.Positivo)
            return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "valor da cobrança tem que ser maior que zero");

        // Sessão ANTES de armar a maquininha: descobrir que o token venceu com o cartão já
        // inserido é o pior momento possível.
        try
        {
            if (GarantirSessao is not null && !await RenovarAsync(ct).ConfigureAwait(false))
                return Falha(SituacaoTef.Erro, chargeId, CodigoTef.SessaoExpirada, MsgSessao);
        }
        catch (OperationCanceledException)
        {
            return Falha(SituacaoTef.Cancelado, chargeId, CodigoTef.Cancelado, MsgCancelado);
        }

        // Documento parcial nunca vaza para o TEF nem para a nota. A validação de dígito
        // verificador é da tela; aqui só recusamos o que nem tamanho de CPF/CNPJ tem.
        var doc = SoDigitos(documento);
        if (doc is not null && doc.Length != 11 && doc.Length != 14) doc = null;

        // `parcelas` só existe em crédito: em débito/PIX a Smart TEF devolve 400 com o campo presente.
        // O teto real de parcelas é do adquirente/política da loja — não é decisão deste arquivo.
        var parc = tipo == TipoTef.Credito ? Math.Max(1, parcelas) : 1;

        // ANTES do POST, de propósito: a partir daqui a cobrança pode existir do lado de lá mesmo
        // que a resposta nunca chegue. Quem grava `tef_transacao (situacao='criando')` neste report
        // tem como achar a cobrança depois; quem espera o desfecho não tem.
        andamento?.Report(new AndamentoTef(FaseTef.Criando, chargeId, null,
            "Enviando a cobrança para a maquininha…"));

        Resposta r;
        try
        {
            r = await EnviarAsync(new
            {
                acao = "criar",
                valor = valor.Reais,
                tipo = tipo.Codigo(),
                parcelas = parc,
                charge_id = chargeId,
                serial_pos = SerialPos,
                documento = doc,
            }, TempoCriarMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // O pedido já estava no ar quando o operador cancelou. Sem `payment_identifier` ninguém
            // manda `cancelar`, MAS dá para perguntar por `charge_id` — e é o que EncerrarAsync faz.
            // Se a cobrança tiver sido paga nesse meio-tempo, ele promove para Pago em vez de mandar
            // o operador cobrar de novo; se não der para saber, o aviso da maquininha volta igual.
            return await EncerrarAsync(SituacaoTef.Cancelado, null, chargeId, valor, null,
                CodigoTef.Cancelado, MsgCancelado, null, andamento).ConfigureAwait(false);
        }

        if (!r.Transportou)
        {
            // Falha de conexão (DNS/recusa) quase sempre é "nem saiu" — avisar sempre treinaria
            // o operador a ignorar o aviso, que é justamente quando ele importa.
            if (!r.Expirou)
                return Falha(SituacaoTef.Erro, chargeId, CodigoTef.SemRede, MsgSemRede);

            // Expirou = o pedido SAIU e a resposta se perdeu: a cobrança pode estar armada, e o
            // cliente pode estar pagando agora. Perguntar antes de desistir (ver ResgatarCriacaoAsync).
            return await ResgatarCriacaoAsync(tipo, valor, chargeId, andamento, ct).ConfigureAwait(false);
        }

        if (r.PlataformaFalhou)
        {
            var (cod, msg) = TraduzirPlataforma(r);
            return Falha(SituacaoTef.Erro, chargeId, cod, msg);
        }

        if (r.Ok == false)
            // Gateway recusou a criação: não existe cobrança e não existe pid — nada a limpar.
            return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Gateway, r.Erro ?? "o TEF não aceitou a cobrança");

        var pid = Texto(r.Corpo, "payment_identifier");
        if (r.Ok != true || string.IsNullOrWhiteSpace(pid))
            return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Gateway,
                r.Erro ?? "o TEF não devolveu identificador da cobrança");

        var ultimoStatus = Texto(r.Corpo, "payment_status");

        // O `charge_id` que vale daqui para a frente é o NOSSO, e o eco do corpo é ignorado de
        // propósito: a linha de `tef_transacao` já foi criada com ele no report "criando", e trocar
        // a identidade no meio deixaria essa linha órfã justamente na hora de reconciliar. A edge só
        // inventa um charge_id quando não mandamos nenhum — e nós sempre mandamos.

        andamento?.Report(new AndamentoTef(FaseTef.Aguardando, chargeId, pid, TextoDeEspera(tipo)));

        return await AcompanharAsync(tipo, valor, pid!, chargeId, ultimoStatus, andamento, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Laço de acompanhamento da cobrança até pago/recusado/timeout. Separado de
    /// <see cref="CobrarAsync"/> porque há DOIS caminhos que chegam aqui: a criação normal e o
    /// resgate por `charge_id` (quando a resposta da criação se perdeu).
    /// </summary>
    private async Task<DesfechoTef> AcompanharAsync(TipoTef tipo, Dinheiro valor, string pid, string chargeId,
        string? ultimoStatus, IProgress<AndamentoTef>? andamento, CancellationToken ct)
    {
        var relogio = Stopwatch.StartNew();
        var seguidas401 = 0;

        while (true)
        {
            // DORME ANTES da primeira consulta: perguntar na hora 0 só devolve "PDT" e gasta
            // uma chamada — o cliente ainda nem encostou na maquininha.
            try { await Task.Delay(IntervaloMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                return await EncerrarAsync(SituacaoTef.Cancelado, pid, chargeId, valor, null,
                    CodigoTef.Cancelado, MsgCancelado, ultimoStatus, andamento).ConfigureAwait(false);
            }

            if (relogio.ElapsedMilliseconds >= TempoTotalMs)
                return await EncerrarAsync(SituacaoTef.Timeout, pid, chargeId, valor, null,
                    CodigoTef.Timeout, MsgTimeout, ultimoStatus, andamento).ConfigureAwait(false);

            Resposta s;
            try
            {
                s = await ConsultarAsync(pid, chargeId, TempoStatusMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return await EncerrarAsync(SituacaoTef.Cancelado, pid, chargeId, valor, null,
                    CodigoTef.Cancelado, MsgCancelado, ultimoStatus, andamento).ConfigureAwait(false);
            }

            if (!s.Transportou || s.Ok == false)
            {
                // Erro de transporte e `ok:false` na consulta são TRANSITÓRIOS: a cobrança segue
                // viva na maquininha. Abortar aqui deixaria o cliente pagando uma cobrança que o
                // PDV já esqueceu — o pior desfecho possível.
                //
                // Zera o contador de 401: numa rede instável a sequência 401 → erro de rede → 401
                // não é "três 401 seguidos", é intermitência. Sem este zero, a cobrança abortava com
                // "sessão expirada" enquanto a maquininha ainda estava com o cliente.
                seguidas401 = 0;
                andamento?.Report(Recado(chargeId, pid, "Sem resposta do TEF — tentando de novo…"));
                continue;
            }

            if (s.PlataformaFalhou)
            {
                // 403 é papel da conta: não se resolve esperando, e esperar 180 s esconderia a
                // causa real atrás de um "tempo esgotado".
                if (s.Http == 403)
                {
                    var (_, msg403) = TraduzirPlataforma(s);
                    return await EncerrarAsync(SituacaoTef.Erro, pid, chargeId, valor, null,
                        CodigoTef.SemPermissao, msg403, ultimoStatus, andamento).ConfigureAwait(false);
                }

                // 401 já teve a renovação tentada lá dentro. Insistir algumas vezes cobre o 401
                // transitório do servidor; teimar os 180 s inteiros com token morto não cobre nada.
                if (s.Http == 401)
                {
                    if (++seguidas401 >= Max401Seguidos)
                        return await EncerrarAsync(SituacaoTef.Erro, pid, chargeId, valor, null,
                            CodigoTef.SessaoExpirada, MsgSessao, ultimoStatus, andamento).ConfigureAwait(false);
                }
                else
                {
                    seguidas401 = 0;   // 500/404 no meio de 401s quebra a sequência — ver acima
                }

                andamento?.Report(Recado(chargeId, pid, "Sem resposta do TEF — tentando de novo…"));
                continue;
            }

            seguidas401 = 0;
            ultimoStatus = Texto(s.Corpo, "payment_status") ?? ultimoStatus;

            if (Bool(s.Corpo, "pago"))
            {
                var cartao = LerCartao(s.Corpo);

                // Conferir o VALOR antes de dizer "pago": emitir NFC-e com um vNF que a adquirente
                // não registrou é divergência que só aparece na conciliação, dias depois. Melhor
                // travar no balcão, onde ainda dá para conferir a maquininha.
                if (ValorDivergente(cartao, valor, out var cobrado))
                    return DivergenciaDeValor(pid, chargeId, cartao, cobrado, valor, ultimoStatus);

                return new DesfechoTef(SituacaoTef.Pago, pid, chargeId, cartao, null, false)
                { Codigo = CodigoTef.Pago, PaymentStatus = ultimoStatus };
            }

            if (Bool(s.Corpo, "recusado"))
                return await EncerrarAsync(SituacaoTef.Recusado, pid, chargeId, valor, LerCartao(s.Corpo),
                    CodigoTef.Recusado, Texto(s.Corpo, "motivo") ?? MsgRecusado, ultimoStatus, andamento)
                    .ConfigureAwait(false);

            // Nem pago nem recusado — e aqui está a ARMADILHA CENTRAL deste contrato: quando a
            // lista do gateway volta vazia, `payment_status` vem "" e os TRÊS booleanos vêm false.
            // Isso NÃO é recusa, é "não sei ainda". Quem ler `!pago ⇒ recusou` libera o cliente
            // que ainda vai pagar. Segue o laço até o timeout.
        }
    }

    /// <summary>
    /// A criação SAIU e a resposta não voltou. Desistir aqui jogava fora uma cobrança que pode
    /// estar armada — ou já paga — sem sequer perguntar: o contrato aceita `{"acao":"status",
    /// "charge_id":"…"}` (§1.2) e o `charge_id` é nosso, sempre enviado. Duas tentativas espaçadas
    /// pelo mesmo intervalo do polling; só depois é que se desiste com o aviso da maquininha.
    /// </summary>
    private async Task<DesfechoTef> ResgatarCriacaoAsync(TipoTef tipo, Dinheiro valor, string chargeId,
        IProgress<AndamentoTef>? andamento, CancellationToken ct)
    {
        andamento?.Report(Recado(chargeId, null, "Sem resposta do TEF — conferindo se a cobrança foi armada…"));

        for (var tentativa = 0; tentativa < TentativasResgate; tentativa++)
        {
            try { await Task.Delay(IntervaloMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            Resposta s;
            try { s = await ConsultarAsync(null, chargeId, TempoStatusMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            if (!s.Transportou || s.PlataformaFalhou || s.Ok == false) continue;

            var pid = Texto(s.Corpo, "payment_identifier");
            var status = Texto(s.Corpo, "payment_status");

            if (Bool(s.Corpo, "pago"))
            {
                var cartao = LerCartao(s.Corpo);
                if (ValorDivergente(cartao, valor, out var cobrado))
                    return DivergenciaDeValor(pid, chargeId, cartao, cobrado, valor, status);

                return new DesfechoTef(SituacaoTef.Pago, pid, chargeId, cartao, null, false)
                { Codigo = CodigoTef.Pago, PaymentStatus = status };
            }

            if (!string.IsNullOrWhiteSpace(pid))
            {
                // A cobrança EXISTE e tem identificador: segue o fluxo normal como se a criação
                // tivesse respondido. O relógio dos 180 s recomeça aqui — os poucos segundos
                // gastos no resgate são baratos perto de perder a cobrança de vista.
                andamento?.Report(new AndamentoTef(FaseTef.Aguardando, chargeId, pid, TextoDeEspera(tipo)));
                return await AcompanharAsync(tipo, valor, pid!, chargeId, status, andamento, ct)
                    .ConfigureAwait(false);
            }

            // Respondeu sem pid: pode ser a lista vazia do gateway ("não sei", §1.2). Tenta de novo.
        }

        // Não deu para saber se armou. `posOcupado: true` de propósito: aqui o pedido comprovadamente
        // saiu, então o aviso da maquininha é informação, não ruído.
        return Falha(SituacaoTef.Erro, chargeId, CodigoTef.SemRede, MsgSemRede, posOcupado: true);
    }

    // ------------------------------------------------------------------ liberar a maquininha

    /// <summary>
    /// Cancela a cobrança E CONFIRMA por consulta o que sobrou dela. HTTP 200 sozinho não prova
    /// nada: a edge responde 200 e o gateway pode ter ignorado, deixando a cobrança na tela da
    /// maquininha — onde o cliente seguinte paga a cobrança do cliente anterior.
    ///
    /// ⚠️ NÃO "SIMPLIFIQUE" ISTO DE VOLTA PARA UM `bool`, E NÃO PARE QUANDO O `cancelar` FOR
    /// RECUSADO. A consulta de status roda SEMPRE — inclusive (principalmente) quando o cancelar
    /// volta `ok:false`, não transporta ou falha na plataforma. O motivo é o desfecho mais caro
    /// deste módulo: o gateway recusa cancelar uma cobrança JÁ PAGA. Ou seja, o `cancelar` recusado
    /// é o SINAL de que o cliente concluiu no último segundo, não motivo para parar. A versão
    /// antiga saía antes da consulta e devolvia `false`; a tela então dizia "cancele na maquininha",
    /// o operador cobrava de novo e o CLIENTE PAGAVA DUAS VEZES — com a resposta do gateway na mão
    /// do código dizendo `pago: true`.
    /// </summary>
    /// <param name="paymentIdentifier">Identificador da cobrança. Pode ser null se houver `chargeId`.</param>
    /// <param name="chargeId">Alternativa aceita pelo contrato (§1.2) quando o pid nunca chegou.</param>
    public async Task<ResultadoLimpeza> LimparNoPosAsync(string? paymentIdentifier, string? chargeId,
        CancellationToken ct)
    {
        var pid = string.IsNullOrWhiteSpace(paymentIdentifier) ? null : paymentIdentifier;
        if (pid is null && string.IsNullOrWhiteSpace(chargeId))
            return new ResultadoLimpeza(LimpezaPos.NaoConfirmado, null, null, null);

        // Sem pid não dá para mandar `cancelar` (o contrato só aceita payment_identifier), mas dá
        // para PERGUNTAR por charge_id — e a pergunta pode revelar o pid ou um pagamento já feito.
        if (pid is null)
        {
            var descoberta = await EtapaAsync(null, chargeId, ehCancelamento: false, ct).ConfigureAwait(false);
            if (descoberta is null || !RespostaUtil(descoberta))
                return new ResultadoLimpeza(LimpezaPos.NaoConfirmado, null, null, null);

            var achado = LerLimpeza(descoberta, null, cancelarAceito: false);
            if (achado.Estado != LimpezaPos.NaoConfirmado || achado.PaymentIdentifier is null)
                return achado;

            pid = achado.PaymentIdentifier;
        }

        // O desfecho do cancelar é GUARDADO, não usado para sair mais cedo (ver o aviso acima).
        var cancelamento = await EtapaAsync(pid, chargeId, ehCancelamento: true, ct).ConfigureAwait(false);
        var cancelarAceito = cancelamento is not null && RespostaUtil(cancelamento) && cancelamento.Ok != false;

        var consulta = await EtapaAsync(pid, chargeId, ehCancelamento: false, ct).ConfigureAwait(false);
        if (consulta is null || !RespostaUtil(consulta) || consulta.Ok == false)
            return new ResultadoLimpeza(LimpezaPos.NaoConfirmado, null, null, pid);

        return LerLimpeza(consulta, pid, cancelarAceito);
    }

    /// <summary>
    /// Lê um corpo de `status` e decide o estado da maquininha, do sinal mais forte para o mais fraco.
    /// </summary>
    private static ResultadoLimpeza LerLimpeza(Resposta resposta, string? identificador, bool cancelarAceito)
    {
        var status = Texto(resposta.Corpo, "payment_status");
        identificador ??= Texto(resposta.Corpo, "payment_identifier");

        // 1) Pago vence tudo: o dinheiro entrou. Quem receber isto conclui a venda, não recobra.
        if (Bool(resposta.Corpo, "pago"))
            return new ResultadoLimpeza(LimpezaPos.PagoNoUltimoSegundo, LerCartao(resposta.Corpo), status, identificador);

        // 2) Ainda em andamento: a cobrança continua viva na tela da maquininha.
        if (Bool(resposta.Corpo, "andamento"))
            return new ResultadoLimpeza(LimpezaPos.NaoConfirmado, null, status, identificador);

        // 3) Nem pago nem em andamento. Só vira "liberado" se soubermos ALGO: `payment_status` vazio
        //    é o "não sei" do contrato (lista do gateway veio vazia, §1.2), e "não sei" só conta como
        //    liberado quando o próprio cancelar foi aceito. Ler "não sei" como "liberado" gravaria
        //    'cancelado' numa cobrança que talvez ainda esteja armada.
        var sabemos = !string.IsNullOrWhiteSpace(status) || cancelarAceito;
        return new ResultadoLimpeza(sabemos ? LimpezaPos.Liberado : LimpezaPos.NaoConfirmado, null, status, identificador);
    }

    /// <summary>
    /// Uma etapa da limpeza com orçamento próprio. Devolve null quando nem deu para perguntar.
    /// O `catch` é por ETAPA de propósito: um `cancelar` que estourou o relógio não pode levar
    /// junto a consulta de status, que é a etapa que descobre se o cliente pagou.
    /// </summary>
    private async Task<Resposta?> EtapaAsync(string? pid, string? chargeId, bool ehCancelamento, CancellationToken ct)
    {
        try
        {
            return ehCancelamento
                ? await EnviarAsync(new { acao = "cancelar", payment_identifier = pid }, TempoCancelarMs, ct)
                    .ConfigureAwait(false)
                : await ConsultarAsync(pid, chargeId, TempoStatusMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
    }

    private static bool RespostaUtil(Resposta r) => r.Transportou && !r.PlataformaFalhou;

    /// <summary>
    /// Estorno de cobrança JÁ PAGA (o caminho de "NFC-e rejeitada e o dinheiro já entrou").
    /// Não dá para provar o desfecho como no <see cref="LimparNoPosAsync"/>: estorno passa por
    /// SOL_EST/PROC_EST, que contam como "em andamento". A confirmação real vem depois, por status.
    /// </summary>
    public async Task<bool> EstornarAsync(string paymentIdentifier, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(paymentIdentifier)) return false;
        try
        {
            var r = await EnviarAsync(new { acao = "estornar", payment_identifier = paymentIdentifier },
                TempoCancelarMs, ct).ConfigureAwait(false);

            // `Ok == true` EXIGIDO, não `Ok != false`: estorno é dinheiro saindo. `Resposta.Do` só
            // preenche `Ok` quando o JSON traz o campo — uma edge derrubada devolvendo HTML/texto
            // com HTTP 200 cai no catch de JSON e deixa `Ok == null`, que passava no `!= false`.
            // A tela então dizia ao operador que o dinheiro voltou para o cliente sem nada ter sido
            // estornado, justo no caminho da NFC-e rejeitada com cartão já pago (§3.3.h).
            return r.Transportou && !r.PlataformaFalhou && r.Ok == true;
        }
        catch (OperationCanceledException) { return false; }
    }

    // ------------------------------------------------------------------ diagnóstico

    /// <summary>
    /// Terminais cadastrados na conta — diagnóstico de setup ("o serial que está no config existe?").
    /// Lista vazia significa "não consegui perguntar OU não há terminal": é tela de configuração,
    /// não caminho de venda, então não vale explodir por causa de rede.
    /// </summary>
    public async Task<IReadOnlyList<string>> TerminaisAsync(CancellationToken ct)
    {
        try
        {
            var r = await EnviarAsync(new { acao = "terminais" }, TempoCancelarMs, ct).ConfigureAwait(false);
            if (!r.Transportou || r.PlataformaFalhou || r.Ok == false) return Array.Empty<string>();
            if (r.Corpo.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
            if (!r.Corpo.TryGetProperty("terminais", out var lista) || lista.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var saida = new List<string>();
            foreach (var t in lista.EnumerateArray())
            {
                // INCERTO: o contrato não fixa o formato de cada terminal (string ou objeto).
                // Aceitamos os dois e, no que não reconhecer, mostramos o JSON cru — esconder o
                // que veio derrota o propósito de um diagnóstico.
                var s = t.ValueKind switch
                {
                    JsonValueKind.String => t.GetString(),
                    JsonValueKind.Object => Texto(t, "serial", "serial_pos", "serial_number", "terminal", "id", "nome", "name")
                                            ?? t.GetRawText(),
                    _ => t.GetRawText(),
                };
                if (!string.IsNullOrWhiteSpace(s)) saida.Add(s!);
            }
            return saida;
        }
        catch (OperationCanceledException) { return Array.Empty<string>(); }
    }

    // ------------------------------------------------------------------ interno

    /// <summary>
    /// Fecha um desfecho NÃO pago liberando a maquininha antes — e, se a limpeza descobrir que a
    /// cobrança foi paga, PROMOVE o desfecho para pago. Timeout e cancelamento não são provas de
    /// que o cliente não pagou: são provas de que o PDV parou de olhar.
    /// </summary>
    private async Task<DesfechoTef> EncerrarAsync(SituacaoTef situacao, string? pid, string chargeId,
        Dinheiro valor, CartaoTef? cartao, string codigo, string motivo, string? paymentStatus,
        IProgress<AndamentoTef>? andamento)
    {
        var ocupado = false;
        // A limpeza roda com pid OU com charge_id: sem pid a versão antiga pulava tudo, e era
        // exatamente o caso em que a cobrança podia estar armada sem ninguém sabendo.
        if (situacao != SituacaoTef.Pago && (!string.IsNullOrWhiteSpace(pid) || !string.IsNullOrWhiteSpace(chargeId)))
        {
            andamento?.Report(Recado(chargeId, pid, "Liberando a maquininha…"));
            // Token PRÓPRIO: no caminho de cancelamento o token do chamador já está cancelado, e
            // liberar o POS é justamente o que não pode deixar de acontecer.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(TempoLimpezaMs));
            var limpeza = await LimparNoPosAsync(pid, chargeId, cts.Token).ConfigureAwait(false);

            if (limpeza.Estado == LimpezaPos.PagoNoUltimoSegundo)
            {
                // O gateway diz que o dinheiro ENTROU. A venda existe: promover para Pago é a única
                // saída honesta — inclusive quando o operador clicou "cancelar cobrança", porque a
                // alternativa (mandar cobrar de novo) faz o cliente pagar duas vezes. Se ele
                // realmente desistiu, o caminho é ESTORNAR, que é uma decisão consciente do balcão.
                var pagoPid = limpeza.PaymentIdentifier ?? pid;
                if (ValorDivergente(limpeza.Cartao, valor, out var cobrado))
                    return DivergenciaDeValor(pagoPid, chargeId, limpeza.Cartao, cobrado, valor,
                        limpeza.PaymentStatus ?? paymentStatus);

                return new DesfechoTef(SituacaoTef.Pago, pagoPid, chargeId, limpeza.Cartao, null, false)
                { Codigo = CodigoTef.Pago, PaymentStatus = limpeza.PaymentStatus ?? paymentStatus };
            }

            // Só "Liberado" prova que a maquininha está livre; "NaoConfirmado" mantém o aviso.
            ocupado = limpeza.Estado != LimpezaPos.Liberado;
            pid ??= limpeza.PaymentIdentifier;
            paymentStatus = limpeza.PaymentStatus ?? paymentStatus;
        }
        return new DesfechoTef(situacao, pid, chargeId, cartao, motivo, ocupado)
        { Codigo = codigo, PaymentStatus = paymentStatus };
    }

    private static DesfechoTef Falha(SituacaoTef situacao, string chargeId, string codigo, string motivo,
        bool posOcupado = false)
        => new(situacao, null, chargeId, null, motivo, posOcupado) { Codigo = codigo };

    private static AndamentoTef Recado(string chargeId, string? pid, string mensagem)
        => new(FaseTef.Recado, chargeId, pid, mensagem);

    private static string TextoDeEspera(TipoTef tipo) => tipo == TipoTef.Pix
        ? "Peça ao cliente para ler o QR na maquininha…"
        : "Aproxime, insira ou passe o cartão…";

    /// <summary>
    /// Consulta de status. O contrato aceita os dois identificadores (§1.2): `payment_identifier`
    /// quando existe, `charge_id` quando a criação nunca devolveu o pid.
    /// </summary>
    private Task<Resposta> ConsultarAsync(string? pid, string? chargeId, int timeoutMs, CancellationToken ct)
    {
        object corpo = !string.IsNullOrWhiteSpace(pid)
            ? (object)new { acao = "status", payment_identifier = pid }
            : new { acao = "status", charge_id = chargeId };
        return EnviarAsync(corpo, timeoutMs, ct);
    }

    /// <summary>
    /// O valor que a adquirente registrou bate com o da venda? Sem `valor` na resposta a conferência
    /// não roda — inventar divergência travaria venda boa no balcão, que é pior que não conferir.
    /// </summary>
    private static bool ValorDivergente(CartaoTef? cartao, Dinheiro esperado, out decimal cobrado)
    {
        cobrado = cartao?.Valor ?? 0m;
        return cartao?.Valor is { } v && Math.Abs(v - esperado.Reais) > ToleranciaValor;
    }

    /// <summary>
    /// Pago, mas por outro valor. NÃO é `Pago` (não dá para emitir nota com vNF que a adquirente não
    /// registrou) e NÃO leva o aviso de POS ocupado: a cobrança concluiu, a maquininha está livre —
    /// o que sobrou é dinheiro entrado sem venda, que a tela precisa mandar conferir/estornar.
    /// </summary>
    private static DesfechoTef DivergenciaDeValor(string? pid, string chargeId, CartaoTef? cartao,
        decimal cobrado, Dinheiro esperado, string? paymentStatus)
        => new(SituacaoTef.Erro, pid, chargeId, cartao,
            $"a maquininha concluiu {Dinheiro.DeReais(cobrado).Formatado()} e a venda é de " +
            $"{esperado.Formatado()} — NÃO emita a nota: confira e estorne na maquininha" +
            (string.IsNullOrWhiteSpace(pid) ? "" : $" (id {pid})"),
            false)
        { Codigo = CodigoTef.ValorDivergente, PaymentStatus = paymentStatus };

    /// <summary>
    /// Erro de plataforma (HTTP não-2xx com `{"error"}` e sem `ok`). 401 e 403 são coisas
    /// diferentes e a tela reage diferente: 401 o operador resolve relogando; 403 é o papel da
    /// conta do terminal — erro de configuração, que relogar não conserta.
    /// </summary>
    private static (string codigo, string mensagem) TraduzirPlataforma(Resposta r)
    {
        var detalhe = string.IsNullOrWhiteSpace(r.Erro) ? "" : " · " + r.Erro;
        return r.Http switch
        {
            401 => (CodigoTef.SessaoExpirada, MsgSessao),
            403 => (CodigoTef.SemPermissao, MsgSemPermissao + detalhe),
            >= 500 => (CodigoTef.Plataforma, "o serviço de TEF falhou" + detalhe),
            _ => (CodigoTef.Plataforma, string.IsNullOrWhiteSpace(r.Erro) ? $"erro {r.Http} no serviço de TEF" : r.Erro!),
        };
    }

    private async Task<Resposta> EnviarAsync(object corpo, int timeoutMs, CancellationToken ct,
        bool segundaTentativa = false)
    {
        var token = await TokenAsync(ct, segundaTentativa).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            return Resposta.DePlataforma(401, "sem sessão com o servidor");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            req.Headers.TryAddWithoutValidation("apikey", _anonKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(JsonSerializer.Serialize(corpo, Json), Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            // O corpo é lido SEMPRE: em não-2xx é justamente onde mora a mensagem que a tela precisa.
            var texto = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if ((int)resp.StatusCode == 401 && !segundaTentativa && GarantirSessao is not null
                && await RenovarAsync(ct).ConfigureAwait(false))
                return await EnviarAsync(corpo, timeoutMs, ct, true).ConfigureAwait(false);

            return Resposta.Do((int)resp.StatusCode, texto);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // quem cancelou foi o chamador — decisão dele, sobe
        }
        catch (OperationCanceledException)
        {
            return Resposta.Expirada();   // estourou o NOSSO relógio: o pedido pode ter chegado
        }
        catch (HttpRequestException) { return Resposta.SemRede(); }
        catch (IOException) { return Resposta.SemRede(); }
    }

    private async Task<string?> TokenAsync(CancellationToken ct, bool jaRenovou)
    {
        string? t;
        try { t = await _obterToken(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { t = null; }   // provedor quebrado não derruba a venda: vira "sem sessão"

        if (!string.IsNullOrWhiteSpace(t) || jaRenovou || GarantirSessao is null) return t;
        if (!await RenovarAsync(ct).ConfigureAwait(false)) return t;

        try { return await _obterToken(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<bool> RenovarAsync(CancellationToken ct)
    {
        if (GarantirSessao is null) return true;
        try { return await GarantirSessao(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    /// <summary>
    /// `charge_id` é a identidade da cobrança na Smart TEF (máx. 60 chars). Epoch sozinho colide
    /// entre dois caixas no mesmo milissegundo, e cobrança colidida é dinheiro trocado de venda.
    /// </summary>
    private static string NovoChargeId()
        => $"pdv-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}-{Guid.NewGuid().ToString("N")[..8]}";

    private static CartaoTef? LerCartao(JsonElement corpo)
    {
        if (corpo.ValueKind != JsonValueKind.Object) return null;
        if (!corpo.TryGetProperty("card", out var c) || c.ValueKind != JsonValueKind.Object) return null;

        var cartao = new CartaoTef(
            // "autorization_code" (sem o "h") é como está escrito na API da adquirente — sic.
            Recortar(Texto(c, "cAut", "caut", "autorization_code", "authorization_code"), 20),
            SoDigitos(Texto(c, "CNPJ", "cnpj")),
            Texto(c, "tBand", "tband"),
            Texto(c, "bandeira", "brand"),
            Texto(c, "adquirente", "acquirer"),
            Texto(c, "nsu"),
            Inteiro(c, "parcelas", "installments"),
            Texto(c, "terminal"),
            Fracionario(c, "valor", "amount", "value"));

        return cartao.Vazio ? null : cartao;
    }

    /// <summary>
    /// Decimal do gateway. `NumberStyles.Float` (sem AllowThousands) de propósito: com
    /// `NumberStyles.Any`, um "25,00" em formato pt-BR seria lido como 2500 pelo InvariantCulture
    /// e travaria uma venda boa por "valor divergente". Não conseguir ler devolve null — a
    /// conferência simplesmente não roda, que é o lado inofensivo do erro.
    /// </summary>
    private static decimal? Fracionario(JsonElement e, params string[] nomes)
        => decimal.TryParse(Texto(e, nomes), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;

    private static string? Recortar(string? s, int max)
        => s is null ? null : (s.Length <= max ? s : s[..max]);

    private static string? SoDigitos(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var d = new string(s.Where(char.IsDigit).ToArray());
        return d.Length == 0 ? null : d;
    }

    private static bool Bool(JsonElement e, string nome)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Texto(JsonElement e, params string[] nomes)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in nomes)
        {
            if (!e.TryGetProperty(n, out var v)) continue;
            var s = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(s)) return s!.Trim();
        }
        return null;
    }

    private static int? Inteiro(JsonElement e, params string[] nomes)
        => int.TryParse(Texto(e, nomes), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>
    /// Uma resposta da edge, com os dois contratos de erro separados:
    /// `Ok == null` ⇒ o corpo nem trouxe `ok` (contrato de plataforma, olhe o <c>Http</c> abaixo);
    /// `Ok == false` ⇒ HTTP 200 e o gateway recusou.
    /// </summary>
    private sealed class Resposta
    {
        public bool Transportou;   // foi e voltou, mesmo com HTTP de erro
        public bool Expirou;       // estourou o tempo desta chamada — o pedido PODE ter chegado
        public int Http;
        public bool? Ok;
        public string? Erro;
        public JsonElement Corpo;

        public bool PlataformaFalhou => Transportou && (Http < 200 || Http > 299);

        public static Resposta SemRede() => new();
        public static Resposta Expirada() => new() { Expirou = true };
        public static Resposta DePlataforma(int http, string erro)
            => new() { Transportou = true, Http = http, Erro = erro };

        public static Resposta Do(int http, string? texto)
        {
            var r = new Resposta { Transportou = true, Http = http };
            if (string.IsNullOrWhiteSpace(texto)) return r;
            try
            {
                using var doc = JsonDocument.Parse(texto);
                r.Corpo = doc.RootElement.Clone();   // Clone: o JsonElement morre junto com o JsonDocument
                if (r.Corpo.ValueKind == JsonValueKind.Object)
                {
                    if (r.Corpo.TryGetProperty("ok", out var ok)
                        && (ok.ValueKind == JsonValueKind.True || ok.ValueKind == JsonValueKind.False))
                        r.Ok = ok.GetBoolean();
                    r.Erro = Texto(r.Corpo, "error", "erro", "message", "msg");
                }
            }
            catch (JsonException)
            {
                // Edge derrubada devolve HTML/texto puro. Guardar um pedaço ajuda no diagnóstico.
                r.Erro = texto!.Trim().Replace('\n', ' ').Replace('\r', ' ');
                if (r.Erro.Length > 200) r.Erro = r.Erro[..200];
            }
            return r;
        }
    }
}
