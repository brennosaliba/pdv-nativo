using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Pdv.Nucleo;

// TEF pelo ControlPay (PayGo — pagamento via WebService).
//
// Como funciona: o PDV cria uma INTENÇÃO DE VENDA na API (Venda/Vender) com
// iniciarTransacaoAutomaticamente=true; o ControlPay "empurra" a transação pela fila para o
// PayGo Windows instalado NESTA máquina (o terminal do PdC), que abre a janela dele e conduz
// cartão/senha/QR no pinpad. O PDV NÃO conversa com o pinpad: só consulta o status da
// intenção (IntencaoVenda/GetById) até um final — 10 Creditado (aprovada), 25 Recusada,
// 15 Expirada (o PayGo não pegou a transação em ~20 s), 20 Cancelada.
//
// O que é diferente do PayGo por arquivos (PayGo.cs), e por quê:
//   1. NÃO existe CNF/NCN: confirmação/desfazimento são tratados pelo próprio PayGo Windows; o
//      ControlPay "reflete o status final na intenção". Por isso aqui não há "imprimir decide":
//      o comprovante é impresso em melhor esforço DEPOIS da aprovação (e reimprime pelo menu TEF).
//   2. A chave de integração vai na QUERY STRING de toda chamada — e por isso NENHUMA mensagem,
//      auditoria ou exceção que saia daqui pode conter a URL completa (Sanitizar()).
//   3. Dados do cartão para a NFC-e vêm do "pagamento externo" (nsuTid, autorizacao, adquirente,
//      bandeira) e o comprovante vem em texto (comprovanteCliente/Estabelecimento) ou no dump
//      "NNN-NNN = valor" da respostaAdquirente — o MESMO formato dos arquivos do PayGo. Por isso
//      este cliente monta uma RespostaPayGo sintética e reaproveita TransacaoPayGo, Guardar, o
//      hook de impressão e a tela: a linha de tef_transacao é idêntica à do PayGo, só o
//      provedor muda ('controlpay') e a identificação é o id da intenção.
//   4. Cancelamento é outra intenção (Venda/CancelarVenda, com senha técnica) que o PayGo também
//      conduz na janela dele; o PDV acompanha até 20 Cancelado — ou até a venda voltar a
//      10 Creditado com um pagamento externo de cancelamento negado ("TRANSAÇÃO NEGADA PELO HOST").
//
// Sandbox: https://sandbox.controlpay.com.br/webapi/ ; produção: https://api.controlpay.com.br/
// (SEM /webapi). TLS 1.2+, User-Agent obrigatório, Content-Type application/json.

/// <summary>Identidade/ambiente do ControlPay. `Chave` e `SenhaTecnica` são segredos (vêm do cofre DPAPI, nunca da tabela config).</summary>
public sealed record OpcoesControlPay(
    string BaseUrl,
    string Chave,
    string SenhaTecnica,
    string TerminalId,
    string PessoaId,
    string UserAgent = "Pdv.AmericanDay/1.0",
    string? Adquirente = null,
    string? AdquirentePix = null)
{
    public const string SandboxUrl = "https://sandbox.controlpay.com.br/webapi/";
    public const string ProducaoUrl = "https://api.controlpay.com.br/";

    public static string UrlDoAmbiente(string? ambiente)
        => string.Equals(ambiente, "producao", StringComparison.OrdinalIgnoreCase) ? ProducaoUrl : SandboxUrl;

    public bool EhSandbox => BaseUrl.Contains("sandbox", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Status da intenção de venda (intencaoVendaStatus.id) — tabela oficial "Valores de status".</summary>
public static class StatusIntencao
{
    public const int Pendente = 5, EmPagamento = 6, Creditado = 10, Expirado = 15,
        CancelamentoIniciado = 18, EmCancelamento = 19, Cancelado = 20, Recusado = 25;

    public static bool Final(int s) => s is Creditado or Expirado or Cancelado or Recusado;
}

/// <summary>Forma de pagamento (formaPagamentoId) para TEF.</summary>
public static class FormaControlPay
{
    public const int TefCredito = 21, TefDebito = 22, TefVoucher = 23, TefOutros = 24, TefCarteiraDigital = 25;

    public static int De(TipoTef t) => t switch
    {
        TipoTef.Credito => TefCredito,
        TipoTef.Debito => TefDebito,
        _ => TefCarteiraDigital,
    };
}

public sealed class ClienteControlPay : IProvedorTefOperavel, IDisposable
{
    public string Nome => "controlpay";
    public string Descricao => $"ControlPay {(_op.EhSandbox ? "sandbox" : "produção")} · terminal {_op.TerminalId}";
    public bool Ocupado => _um.CurrentCount == 0;

    /// <summary>Cadência da consulta de status. A doc não impõe teto; 1,5 s é imperceptível ao operador e gentil com a API.</summary>
    public int IntervaloPollMs { get; init; } = 1_500;

    /// <summary>Teto do POST Venda/Vender: em modo ativo a API pode segurar até ~20 s esperando o PayGo pegar a transação.</summary>
    public int TempoHttpMs { get; init; } = 40_000;

    /// <summary>
    /// Sem timeout para a transação em si (quem está com o cliente é o pinpad/janela do PayGo).
    /// Única exceção: o operador pediu para cancelar e a intenção continua "em pagamento" por
    /// este tempo — o PDV desiste, grava ÓRFÃ e avisa; o religamento reconcilia pelo status.
    /// </summary>
    public int TempoDesistirAposCancelarMs { get; init; } = 90_000;

    /// <summary>
    /// Teto da cobrança: se em <see cref="TempoMaxEmPagamentoMs"/> a intenção não chegou a um
    /// desfecho, o PDV DEVOLVE A TELA ao operador (não fica preso). A intenção continua viva no
    /// ControlPay/PayGo: a linha fica `orfa` com aviso e o religamento/menu TEF resolvem pelo
    /// status real — nunca se declara pago o que não se sabe.
    /// </summary>
    public int TempoMaxEmPagamentoMs { get; init; } = 60_000;

    /// <summary>
    /// Teto do Pix/carteira digital. O QR fica na tela esperando o cliente abrir o app, ler e
    /// confirmar — 60 s derruba a venda no meio da leitura. Por isso o Pix tem prazo próprio.
    /// </summary>
    public int TempoMaxPixMs { get; init; } = 180_000;

    /// <summary>Teto do acompanhamento de um cancelamento/ADM (o PayGo pede senha lojista, cartão…).</summary>
    public int TempoMaxOperacaoMs { get; init; } = 10 * 60_000;

    /// <summary>Grava a linha de `tef_transacao` (mesma semântica do PayGo: memória não volátil antes de devolver "pago").</summary>
    public Func<TransacaoPayGo, bool> Guardar { get; init; } = _ => true;

    /// <summary>Imprime o comprovante (melhor esforço — não existe NCN no ControlPay). Null = não imprime.</summary>
    public Func<TransacaoPayGo, Task<bool>>? ImprimirComprovante { get; init; }

    /// <summary>CNPJ da credenciadora pelo nome da adquirente (VISANET/CIELO/REDE…) — vai no &lt;card&gt; da NFC-e.</summary>
    public Func<string, string?>? CnpjDaRede { get; init; }

    /// <summary>Trilha (sem chave!): intenção criada, status finais, cancelamentos, órfãs.</summary>
    public Action<string>? Auditar { get; init; }

    public const string MsgTefNaoResponde = "TEF não responde — o PayGo Windows não recebeu a cobrança. Confira se ele está aberto e com o TEF verde.";

    private readonly OpcoesControlPay _op;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _um = new(1, 1);
    private static long _ultimaRef;

    public ClienteControlPay(OpcoesControlPay opcoes, HttpMessageHandler? handler = null)
    {
        _op = opcoes ?? throw new ArgumentNullException(nameof(opcoes));
        if (string.IsNullOrWhiteSpace(_op.Chave)) throw new ArgumentException("chave de integração vazia", nameof(opcoes));
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;   // os tetos são por chamada (CancellationTokenSource)
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _op.UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void Dispose() => _http.Dispose();

    // ------------------------------------------------------------------ ativo / terminais

    public async Task<bool> AtivoAsync(CancellationToken ct)
    {
        try
        {
            var terminais = await ListarTerminaisAsync(ct).ConfigureAwait(false);
            return terminais.Any(t => t.Id == _op.TerminalId);
        }
        catch { return false; }
    }

    public sealed record TerminalControlPay(string Id, string Nome, bool AguardaTef, bool VendaPorValor, string? TerminalFisico, string? InstalacaoId);

    /// <summary>Terminais lógicos da pessoa (Terminal/GetByPessoaId). A tela usa para o operador escolher o terminal certo.</summary>
    public async Task<IReadOnlyList<TerminalControlPay>> ListarTerminaisAsync(CancellationToken ct)
    {
        using var doc = await ChamarAsync(HttpMethod.Get, $"Terminal/GetByPessoaId?pessoaId={Uri.EscapeDataString(_op.PessoaId)}", null, ct).ConfigureAwait(false);
        var lista = new List<TerminalControlPay>();
        if (doc.RootElement.TryGetProperty("terminais", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in arr.EnumerateArray())
            {
                var tf = t.TryGetProperty("terminalFisico", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;
                lista.Add(new TerminalControlPay(
                    Texto(t, "id") ?? "", Texto(t, "nome") ?? "",
                    Bool(t, "aguardaTef"), Bool(t, "vendaPorValor"),
                    tf.ValueKind == JsonValueKind.Object ? Texto(tf, "nome") : null,
                    tf.ValueKind == JsonValueKind.Object ? Texto(tf, "instalacaoId") : null));
            }
        }
        return lista;
    }

    // ------------------------------------------------------------------ venda

    public async Task<DesfechoTef> CobrarAsync(TipoTef tipo, Dinheiro valor, string? documento, int parcelas,
        IProgress<AndamentoTef>? andamento, CancellationToken ct)
    {
        var referencia = NovaReferencia();
        var chargeId = "cpay-" + referencia;
        if (!valor.Positivo)
            return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "valor da cobrança tem que ser maior que zero");
        var parc = tipo == TipoTef.Credito ? Math.Max(1, parcelas) : 1;

        andamento?.Report(new AndamentoTef(FaseTef.Criando, chargeId, null, "Enviando a cobrança para o ControlPay…"));

        try { await _um.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Falha(SituacaoTef.Cancelado, chargeId, CodigoTef.Cancelado, "cobrança cancelada pelo operador"); }
        try
        {
            // 1. intenção de venda, modo ATIVO (o ControlPay aciona o PayGo Windows deste terminal)
            var body = new Dictionary<string, object?>
            {
                ["formaPagamentoId"] = FormaControlPay.De(tipo),
                ["terminalId"] = _op.TerminalId,
                ["referencia"] = referencia,
                ["iniciarTransacaoAutomaticamente"] = true,
                ["aguardarTefIniciarTransacao"] = true,       // a doc usa os dois nomes; mandar ambos é inócuo
                ["quantidadeParcelas"] = parc,
                ["valorTotalVendido"] = ValorComVirgula(valor.Centavos),
                ["observacao"] = "PDV " + referencia + (string.IsNullOrEmpty(documento) ? "" : " doc " + documento),
            };
            // 1 parcela = À VISTA declarado (aVista): sem isso o pinpad ainda pergunta o
            // financiamento ao operador, e a loja não parcela. Parcelado (>1) vai pela LOJA.
            if (parc > 1) body["parcelamentoAdmin"] = false;
            else if (tipo is TipoTef.Credito or TipoTef.Debito) body["aVista"] = true;
            // Adquirente pré-selecionada: a doc recomenda deixar o roteamento decidir, mas na
            // HOMOLOGAÇÃO o roteiro exige um autorizador fixo (C6PAY / PIX C6 BANK) — sem isso o
            // PayGo abre o menu de redes e a venda cai na rede errada (que nega centavos).
            var adq = tipo == TipoTef.Pix ? (_op.AdquirentePix ?? _op.Adquirente) : _op.Adquirente;
            if (!string.IsNullOrWhiteSpace(adq)) body["adquirente"] = adq;

            JsonDocument resp;
            try { resp = await ChamarAsync(HttpMethod.Post, "Venda/Vender/", body, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Auditar?.Invoke("controlpay: Venda/Vender falhou — " + Sanitizar(ex.Message));
                return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "ControlPay indisponível: " + Sanitizar(ex.Message));
            }

            long intencaoId; int status; string statusNome;
            using (resp)
            {
                if (!resp.RootElement.TryGetProperty("intencaoVenda", out var iv) || iv.ValueKind != JsonValueKind.Object)
                    return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "ControlPay respondeu sem intenção de venda: " + Sanitizar(Mensagem(resp)));
                intencaoId = Longo(iv, "id") ?? 0;
                (status, statusNome) = StatusDe(iv);
            }
            if (intencaoId <= 0)
                return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "ControlPay não devolveu o id da intenção de venda");
            var ident = intencaoId.ToString(CultureInfo.InvariantCulture);
            Auditar?.Invoke($"controlpay: intenção {ident} criada ({valor.Formatado()} {tipo} {parc}x) status {status} {statusNome}");

            if (status == StatusIntencao.Expirado)
            {
                Guardar(new TransacaoPayGo(chargeId, ident, tipo, valor.Centavos, parc, "erro", null, MsgTefNaoResponde));
                return new DesfechoTef(SituacaoTef.Erro, ident, chargeId, null, MsgTefNaoResponde, false) { Codigo = CodigoTef.TefNaoResponde };
            }

            // Memória não volátil: a cobrança existe no ControlPay a partir daqui.
            Guardar(new TransacaoPayGo(chargeId, ident, tipo, valor.Centavos, parc, "aguardando", null));
            andamento?.Report(new AndamentoTef(FaseTef.Aguardando, chargeId, ident,
                tipo == TipoTef.Pix ? "Siga na janela do PayGo: peça ao cliente para ler o QR…" : "Siga na janela do PayGo: aproxime, insira ou passe o cartão no pinpad…"));

            // 2. acompanhar até o final
            var teto = tipo == TipoTef.Pix ? TempoMaxPixMs : TempoMaxEmPagamentoMs;
            var fim = await AcompanharAsync(ident, chargeId, andamento, ct, teto).ConfigureAwait(false);
            if (fim.Desistencia == "tempo")
            {
                // Passou do teto sem desfecho. A cobrança pode estar viva no pinpad: órfã +
                // aviso, e o operador decide (conferir no PayGo / estornar / registrar).
                var msg = $"a cobrança passou de {teto / 1000} s sem resposta — confira na janela do PayGo. " +
                          "Se o cliente concluiu, NÃO cobre de novo: estorne em TEF → Estornar.";
                Guardar(new TransacaoPayGo(chargeId, ident, tipo, valor.Centavos, parc, "orfa", null, msg));
                Auditar?.Invoke($"controlpay: intenção {ident} sem desfecho em {TempoMaxEmPagamentoMs / 1000} s — devolvida ao operador (órfã)");
                return new DesfechoTef(SituacaoTef.Timeout, ident, chargeId, null, msg, true) { Codigo = CodigoTef.Timeout };
            }
            if (fim.Desistencia is not null)
            {
                Guardar(new TransacaoPayGo(chargeId, ident, tipo, valor.Centavos, parc, "orfa", null, "operador cancelou e a intenção continua em pagamento no PayGo"));
                return new DesfechoTef(SituacaoTef.Cancelado, ident, chargeId, null,
                    "cobrança cancelada pelo operador — confira na janela do PayGo se o cliente concluiu", true) { Codigo = CodigoTef.Cancelado };
            }
            var (st, stNome, detalhe, _) = fim;

            if (st == StatusIntencao.Creditado)
            {
                var r = await RespostaDaIntencaoAsync(ident, valor.Centavos, tipo, 5).ConfigureAwait(false);
                var tx = new TransacaoPayGo(chargeId, ident, tipo, valor.Centavos, parc, "aprovada", r);
                if (!GuardarSeguro(tx))
                {
                    // Aprovada no ControlPay e o caixa não conseguiu gravar: não há NCN aqui. Fica
                    // registrada como órfã no que der e a tela avisa — o fechamento acusa o TEF sem venda.
                    Auditar?.Invoke($"controlpay: intenção {ident} APROVADA mas o caixa não gravou — conferir/estornar");
                    return new DesfechoTef(SituacaoTef.Erro, ident, chargeId, Cartao(r),
                        "o cartão foi APROVADO mas o caixa não conseguiu gravar a transação — NÃO cobre de novo; estorne pelo menu TEF ou registre como POS", true)
                    { Codigo = CodigoTef.Plataforma };
                }
                // 'pago' ANTES de imprimir e a impressão FORA do caminho da venda: no ControlPay
                // não há CNF/NCN — a transação já está efetivada e o comprovante é acessório.
                // Esperar a impressora (fila travada, sem papel, diálogo de retentativa) deixava
                // o caixa parado em "aguardando o cliente" com a venda já cobrada. Falhou? Fica
                // na auditoria e o operador reimprime em TEF → Reimprimir.
                Guardar(tx with { Situacao = "pago" });
                _ = ImprimirSeguroAsync(tx);
                Auditar?.Invoke($"controlpay: intenção {ident} APROVADA nsu={r.Nsu ?? "-"} aut={r.Autorizacao ?? "-"} rede={r.Rede ?? "-"}");
                return new DesfechoTef(SituacaoTef.Pago, ident, chargeId, Cartao(r), null, false) { Codigo = CodigoTef.Pago, PaymentStatus = "pago" };
            }

            if (st == StatusIntencao.Recusado)
            {
                var msg = string.IsNullOrWhiteSpace(detalhe) ? "pagamento não aprovado" : detalhe!;
                var cancelada = msg.ToUpperInvariant().Contains("CANCELAD");
                Guardar(new TransacaoPayGo(chargeId, ident, tipo, valor.Centavos, parc, cancelada ? "cancelado" : "recusado", null, msg));
                return cancelada
                    ? new DesfechoTef(SituacaoTef.Cancelado, ident, chargeId, null, msg, false) { Codigo = CodigoTef.Cancelado }
                    : new DesfechoTef(SituacaoTef.Recusado, ident, chargeId, null, msg, false) { Codigo = CodigoTef.Recusado };
            }

            if (st == StatusIntencao.Expirado)
            {
                Guardar(new TransacaoPayGo(chargeId, ident, tipo, valor.Centavos, parc, "erro", null, MsgTefNaoResponde));
                return new DesfechoTef(SituacaoTef.Erro, ident, chargeId, null, MsgTefNaoResponde, false) { Codigo = CodigoTef.TefNaoResponde };
            }

            // 20 Cancelado (ou qualquer outro final): não há cobrança
            Guardar(new TransacaoPayGo(chargeId, ident, tipo, valor.Centavos, parc, "cancelado", null, stNome));
            return new DesfechoTef(SituacaoTef.Cancelado, ident, chargeId, null, "cobrança cancelada (" + stNome + ")", false) { Codigo = CodigoTef.Cancelado };
        }
        finally { _um.Release(); }
    }

    /// <summary>
    /// Consulta a intenção até um status final. Devolve (status, nome, mensagem da adquirente) ou
    /// null se o operador cancelou e a intenção ficou em pagamento além de <see cref="TempoDesistirAposCancelarMs"/>.
    /// </summary>
    private async Task<(int Status, string Nome, string? Detalhe, string? Desistencia)> AcompanharAsync(string ident, string chargeId,
        IProgress<AndamentoTef>? andamento, CancellationToken ct, int tetoMs)
    {
        Stopwatch? desdeCancelamento = null;
        var falhasSeguidas = 0;
        var relogio = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var doc = await ChamarAsync(HttpMethod.Post, "IntencaoVenda/GetById/?intencaoVendaId=" + Uri.EscapeDataString(ident), null, CancellationToken.None).ConfigureAwait(false);
                falhasSeguidas = 0;
                if (doc.RootElement.TryGetProperty("intencaoVenda", out var iv) && iv.ValueKind == JsonValueKind.Object)
                {
                    var (st, nome) = StatusDe(iv);
                    if (StatusIntencao.Final(st))
                    {
                        string? detalhe = null;
                        if (st == StatusIntencao.Recusado) detalhe = await MensagemAdquirenteAsync(ident).ConfigureAwait(false);
                        return (st, nome, detalhe, null);
                    }
                }
            }
            catch (Exception ex)
            {
                // Rede oscilou: a intenção continua viva no ControlPay; insistir. Só audita de vez em quando.
                if (++falhasSeguidas % 10 == 1) Auditar?.Invoke($"controlpay: consulta da intenção {ident} falhou ({Sanitizar(ex.Message)}) — tentando de novo");
            }

            if (desdeCancelamento is null && ct.IsCancellationRequested)
            {
                desdeCancelamento = Stopwatch.StartNew();
                andamento?.Report(new AndamentoTef(FaseTef.Recado, chargeId, ident,
                    "Cancelamento pedido — cancele na janela do PayGo (Esc). O PDV aguarda o resultado…"));
            }
            if (desdeCancelamento is not null && desdeCancelamento.ElapsedMilliseconds >= TempoDesistirAposCancelarMs)
                return (0, "", null, "cancelou");
            if (tetoMs > 0 && relogio.ElapsedMilliseconds >= tetoMs)
                return (0, "", null, "tempo");
            await Task.Delay(IntervaloPollMs).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ cancelamento (estorno)

    public async Task<DesfechoTef> CancelarAsync(TransacaoPayGo original, CancellationToken ct)
    {
        var ident = original.Identificacao;
        var chargeId = "cpay-cnc-" + NovaReferencia();
        if (string.IsNullOrWhiteSpace(ident))
            return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "transação original sem id de intenção — cancele pelo menu do PayGo");

        await _um.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var body = new Dictionary<string, object?>
            {
                ["intencaoVendaId"] = ident,
                ["terminalId"] = _op.TerminalId,
                ["iniciarTransacaoAutomaticamente"] = true,
                ["aguardarTefIniciarTransacao"] = true,
                ["senhaTecnica"] = _op.SenhaTecnica,
            };
            JsonDocument resp;
            try { resp = await ChamarAsync(HttpMethod.Post, "Venda/CancelarVenda/", body, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Auditar?.Invoke($"controlpay: CancelarVenda {ident} falhou — " + Sanitizar(ex.Message));
                return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "ControlPay recusou o pedido de cancelamento: " + Sanitizar(ex.Message));
            }
            int st; string stNome;
            using (resp)
            {
                if (!resp.RootElement.TryGetProperty("intencaoVenda", out var iv) || iv.ValueKind != JsonValueKind.Object)
                    return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "ControlPay respondeu sem intenção: " + Sanitizar(Mensagem(resp)));
                (st, stNome) = StatusDe(iv);
            }
            Auditar?.Invoke($"controlpay: cancelamento da intenção {ident} iniciado (status {st} {stNome})");

            // Acompanhar: 20 = cancelado; 10 de volta com pagamento externo de cancelamento finalizado = negado.
            var relogio = Stopwatch.StartNew();
            long? peCancelamento = null;
            string? msgNegado = null;
            while (st != StatusIntencao.Cancelado)
            {
                if (relogio.ElapsedMilliseconds > TempoMaxOperacaoMs)
                    return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "o cancelamento não concluiu no PayGo — confira na janela dele e no menu TEF", true);
                await Task.Delay(IntervaloPollMs).ConfigureAwait(false);
                try
                {
                    using var doc = await ChamarAsync(HttpMethod.Post, "IntencaoVenda/GetByFiltros/", new Dictionary<string, object?> { ["intencaoVendaId"] = long.Parse(ident, CultureInfo.InvariantCulture) }, CancellationToken.None).ConfigureAwait(false);
                    var iv = PrimeiraIntencao(doc);
                    if (iv.ValueKind != JsonValueKind.Object) continue;
                    (st, stNome) = StatusDe(iv);
                    // pagamento externo tipo 10 (cancelamento) finalizado com mensagem = resultado do host
                    foreach (var pe in PagamentosExternos(iv))
                    {
                        if ((Inteiro(pe, "tipo") ?? 0) != 10) continue;
                        var peSt = pe.TryGetProperty("pagamentoExternoStatus", out var ps) ? Inteiro(ps, "id") ?? 0 : 0;
                        if (peSt == 15)
                        {
                            peCancelamento = Longo(pe, "id");
                            msgNegado = Texto(pe, "mensagemRespostaAdquirente");
                        }
                    }
                    if (st == StatusIntencao.Creditado && peCancelamento is not null) break;   // voltou a creditado = cancelamento negado
                    if (st == StatusIntencao.Recusado) break;
                }
                catch (Exception ex) { Auditar?.Invoke($"controlpay: consulta do cancelamento {ident} falhou ({Sanitizar(ex.Message)})"); }
            }

            if (st != StatusIntencao.Cancelado)
            {
                var msg = string.IsNullOrWhiteSpace(msgNegado) ? "cancelamento não autorizado" : msgNegado!;
                Auditar?.Invoke($"controlpay: cancelamento da intenção {ident} NEGADO — {msg}");
                return new DesfechoTef(msg.ToUpperInvariant().Contains("CANCELAD") ? SituacaoTef.Cancelado : SituacaoTef.Recusado,
                    ident, chargeId, null, msg, false) { Codigo = CodigoTef.Recusado };
            }

            var r = await RespostaDaIntencaoAsync(ident, original.ValorCent, original.Tipo, 10).ConfigureAwait(false);
            var tx = new TransacaoPayGo(chargeId, ident, original.Tipo, original.ValorCent, original.Parcelas, "aprovada", r, "cancelamento de " + original.ChargeId);
            GuardarSeguro(tx);
            _ = ImprimirSeguroAsync(tx);   // comprovante do estorno também não segura a tela
            Guardar(tx with { Situacao = "estornado" });
            Guardar(original with { Situacao = "estornada", Motivo = "estornada por " + chargeId });
            Auditar?.Invoke($"controlpay: intenção {ident} CANCELADA (estorno)");
            return new DesfechoTef(SituacaoTef.Pago, ident, chargeId, Cartao(r), null, false) { Codigo = CodigoTef.Pago, PaymentStatus = "estornado" };
        }
        finally { _um.Release(); }
    }

    // ------------------------------------------------------------------ administrativa

    public async Task<DesfechoTef> AdministrativaAsync(CancellationToken ct)
    {
        var chargeId = "cpay-adm-" + NovaReferencia();
        await _um.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            JsonDocument resp;
            try
            {
                resp = await ChamarAsync(HttpMethod.Post, "PagamentoExterno/InsertPagamentoExternoTipoAdmin/",
                    new Dictionary<string, object?> { ["terminalId"] = _op.TerminalId, ["senhaTecnica"] = _op.SenhaTecnica }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "ControlPay recusou a operação administrativa: " + Sanitizar(ex.Message)); }

            long peId;
            using (resp)
            {
                if (!resp.RootElement.TryGetProperty("pagamentoExterno", out var pe) || pe.ValueKind != JsonValueKind.Object)
                    return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "ControlPay respondeu sem pagamento externo: " + Sanitizar(Mensagem(resp)));
                peId = Longo(pe, "id") ?? 0;
            }
            var relogio = Stopwatch.StartNew();
            JsonElement final = default;
            while (true)
            {
                if (relogio.ElapsedMilliseconds > TempoMaxOperacaoMs)
                    return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "a operação administrativa não concluiu no PayGo", true);
                await Task.Delay(IntervaloPollMs).ConfigureAwait(false);
                try
                {
                    using var doc = await ChamarAsync(HttpMethod.Post, "PagamentoExterno/GetById/?pagamentoExternoId=" + peId, null, CancellationToken.None).ConfigureAwait(false);
                    if (!doc.RootElement.TryGetProperty("pagamentoExterno", out var pe) || pe.ValueKind != JsonValueKind.Object) continue;
                    var stId = pe.TryGetProperty("pagamentoExternoStatus", out var ps) ? Inteiro(ps, "id") ?? 0 : 0;
                    if (stId == 15) { final = pe.Clone(); break; }
                }
                catch (Exception ex) { Auditar?.Invoke($"controlpay: consulta da ADM {peId} falhou ({Sanitizar(ex.Message)})"); }
            }
            var msg = Texto(final, "mensagemRespostaAdquirente") ?? "operação concluída";
            var r = RespostaSintetica(final, 0, TipoTef.Credito, peId.ToString(CultureInfo.InvariantCulture));
            if (r.TemVias)
            {
                var tx = new TransacaoPayGo(chargeId, peId.ToString(CultureInfo.InvariantCulture), TipoTef.Credito, r.ValorCent ?? 0, 1, "adm", r, "administrativa");
                GuardarSeguro(tx);
                _ = ImprimirSeguroAsync(tx);
            }
            Auditar?.Invoke($"controlpay: operação administrativa {peId} concluída — {msg}");
            return new DesfechoTef(SituacaoTef.Pago, peId.ToString(CultureInfo.InvariantCulture), chargeId, null, msg, false) { Codigo = CodigoTef.Pago, PaymentStatus = "adm" };
        }
        finally { _um.Release(); }
    }

    // ------------------------------------------------------------------ religamento

    /// <summary>
    /// Linhas `controlpay` que ficaram 'aguardando'/'orfa' (PDV morreu no meio): consulta o status
    /// e fecha a linha. Aprovada sem venda vira 'pago' com motivo de órfã — o fechamento de caixa
    /// acusa (TEF sem venda) e o estorno sai pelo menu TEF. Devolve quantas mudaram.
    /// </summary>
    /// <param name="criadaEm">
    /// Quando cada linha nasceu (chave = ChargeId), para separar o que é de ANTES do boot do que
    /// está em voo agora. Sem isto, intenção sem status final é deixada como está.
    /// </param>
    public async Task<int> ReconciliarAsync(IReadOnlyList<TransacaoPayGo> pendentes,
                                            IReadOnlyDictionary<string, DateTime>? criadaEm = null)
    {
        var n = 0;
        var boot = Process.GetCurrentProcess().StartTime;
        await _um.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var tx in pendentes)
            {
                if (string.IsNullOrWhiteSpace(tx.Identificacao) || !long.TryParse(tx.Identificacao, out _)) continue;
                try
                {
                    using var doc = await ChamarAsync(HttpMethod.Post, "IntencaoVenda/GetById/?intencaoVendaId=" + tx.Identificacao, null, CancellationToken.None).ConfigureAwait(false);
                    if (!doc.RootElement.TryGetProperty("intencaoVenda", out var iv) || iv.ValueKind != JsonValueKind.Object) continue;
                    var (st, nome) = StatusDe(iv);
                    if (!StatusIntencao.Final(st))
                    {
                        // Status não-final no religamento (tipicamente 6 "Em Pagamento"): o PayGo
                        // Windows morreu no meio da cobrança e o ControlPay vai manter a intenção
                        // assim para sempre. Desistir aqui era deixar a linha 'aguardando' até o fim
                        // dos tempos — foi exatamente o que o passo 24 da homologação pegou. Vira
                        // ÓRFÃ (o fechamento acusa e o estorno sai pelo menu TEF); NUNCA 'pago',
                        // porque ninguém sabe se o dinheiro entrou.
                        //
                        // Só entra o que é ANTERIOR ao boot: uma cobrança criada depois pode estar
                        // com o pinpad na mão do operador NESTE instante, e declará-la órfã seria
                        // arrancar uma venda viva das mãos dele.
                        if (tx.Situacao == "orfa") continue;
                        if (criadaEm is null || !criadaEm.TryGetValue(tx.ChargeId, out var em) || em >= boot) continue;
                        GuardarSeguro(tx with { Situacao = "orfa", Motivo = "religamento: intenção ainda em pagamento no ControlPay — confira no PayGo e estorne se aprovou" });
                        Auditar?.Invoke($"controlpay: religamento — intenção {tx.Identificacao} continua '{nome}'; devolvida ao operador (órfã)");
                        n++;
                        continue;
                    }
                    if (st == StatusIntencao.Creditado)
                    {
                        var r = await RespostaDaIntencaoAsync(tx.Identificacao, tx.ValorCent, tx.Tipo, 5).ConfigureAwait(false);
                        GuardarSeguro(tx with { Situacao = "pago", Resposta = r, Motivo = "APROVADA SEM VENDA (religamento) — estornar pelo menu TEF" });
                        Auditar?.Invoke($"controlpay: religamento — intenção {tx.Identificacao} estava APROVADA sem venda; marcada para estorno");
                    }
                    else
                    {
                        GuardarSeguro(tx with { Situacao = st == StatusIntencao.Recusado ? "recusado" : st == StatusIntencao.Cancelado ? "cancelado" : "erro", Motivo = "religamento: " + nome });
                    }
                    n++;
                }
                catch (Exception ex) { Auditar?.Invoke($"controlpay: religamento da intenção {tx.Identificacao} falhou ({Sanitizar(ex.Message)})"); }
            }
        }
        finally { _um.Release(); }
        return n;
    }

    // ------------------------------------------------------------------ detalhes / comprovante

    /// <summary>Última mensagem da adquirente (pagamento externo de venda) — é o que o roteiro manda mostrar ao operador ("NEGADA 01", "OPERAÇÃO CANCELADA").</summary>
    private async Task<string?> MensagemAdquirenteAsync(string ident)
    {
        try
        {
            using var doc = await ChamarAsync(HttpMethod.Post, "IntencaoVenda/GetByFiltros/", new Dictionary<string, object?> { ["intencaoVendaId"] = long.Parse(ident, CultureInfo.InvariantCulture) }, CancellationToken.None).ConfigureAwait(false);
            var iv = PrimeiraIntencao(doc);
            if (iv.ValueKind != JsonValueKind.Object) return null;
            string? msg = null;
            foreach (var pe in PagamentosExternos(iv))
                msg = Texto(pe, "mensagemRespostaAdquirente") ?? msg;
            return msg;
        }
        catch { return null; }
    }

    /// <summary>
    /// Monta a RespostaPayGo (formato intpos) da intenção: pagamento externo do tipo pedido
    /// (5 venda / 10 cancelamento) — GetByFiltros traz nsuTid/autorizacao/adquirente/respostaAdquirente/
    /// comprovanteAdquirente; PagamentoExterno/GetById completa bandeira e vias cliente/estabelecimento.
    /// Nunca lança: sem detalhes, devolve uma resposta mínima (aprovada, sem vias).
    /// </summary>
    private async Task<RespostaPayGo> RespostaDaIntencaoAsync(string ident, long valorCent, TipoTef tipo, int tipoPagamento)
    {
        JsonElement pe = default;
        try
        {
            using var doc = await ChamarAsync(HttpMethod.Post, "IntencaoVenda/GetByFiltros/", new Dictionary<string, object?> { ["intencaoVendaId"] = long.Parse(ident, CultureInfo.InvariantCulture) }, CancellationToken.None).ConfigureAwait(false);
            var iv = PrimeiraIntencao(doc);
            if (iv.ValueKind == JsonValueKind.Object)
                foreach (var p in PagamentosExternos(iv))
                    if ((Inteiro(p, "tipo") ?? 5) == tipoPagamento) pe = p.Clone();
        }
        catch (Exception ex) { Auditar?.Invoke($"controlpay: detalhes da intenção {ident} indisponíveis ({Sanitizar(ex.Message)})"); }

        // completar (bandeira, vias diferenciadas) pelo pagamento externo
        if (pe.ValueKind == JsonValueKind.Object && Longo(pe, "id") is { } peId
            && (Texto(pe, "comprovanteCliente") is null || Texto(pe, "bandeira") is null))
        {
            try
            {
                using var doc2 = await ChamarAsync(HttpMethod.Post, "PagamentoExterno/GetById/?pagamentoExternoId=" + peId, null, CancellationToken.None).ConfigureAwait(false);
                if (doc2.RootElement.TryGetProperty("pagamentoExterno", out var pe2) && pe2.ValueKind == JsonValueKind.Object)
                    pe = Mesclar(pe, pe2);
            }
            catch { /* fica com o que já veio */ }
        }
        return RespostaSintetica(pe, valorCent, tipo, ident);
    }

    /// <summary>
    /// RespostaPayGo a partir de um pagamento externo: parte do dump `respostaAdquirente` (já é
    /// intpos) e completa o que faltar com os campos estruturados e os comprovantes em texto.
    /// </summary>
    public static RespostaPayGo RespostaSintetica(JsonElement pe, long valorCent, TipoTef tipo, string ident)
    {
        var campos = new Dictionary<string, string>(StringComparer.Ordinal);
        var dump = pe.ValueKind == JsonValueKind.Object ? Texto(pe, "respostaAdquirente") : null;
        if (!string.IsNullOrWhiteSpace(dump) && dump.Contains("000-000"))
            foreach (var (k, v) in ArquivoIntpos.Analisar(dump)) campos[k] = v;

        void Def(string k, string? v) { if (!string.IsNullOrWhiteSpace(v) && !campos.ContainsKey(k)) campos[k] = v!; }
        Def("000-000", "CRT");
        Def("001-000", ident);
        Def("009-000", "0");
        Def("003-000", valorCent > 0 ? valorCent.ToString(CultureInfo.InvariantCulture) : null);
        Def("731-000", tipo switch { TipoTef.Credito => "1", TipoTef.Debito => "2", _ => "0" });
        if (pe.ValueKind == JsonValueKind.Object)
        {
            Def("012-000", Texto(pe, "nsuTid"));
            Def("013-000", Texto(pe, "autorizacao"));
            Def("010-000", Texto(pe, "adquirente"));
            // bandeira estruturada ("VISA", "JCB") vale mais que o nome do cartão do dump
            // ("DEMOCARD"): é dela que sai o tBand da NFC-e. O nome do dump fica em 748 (produto).
            if (Texto(pe, "bandeira") is { } bandeira)
            {
                if (campos.TryGetValue("040-000", out var nomeDump) && !campos.ContainsKey("748-000")) campos["748-000"] = nomeDump;
                campos["040-000"] = bandeira;
            }
            Def("030-000", Texto(pe, "mensagemRespostaAdquirente"));
            Def("741-000", Texto(pe, "nomeTitularCartao"));
            // vias: só se o dump não trouxe
            var temCliente = campos.Keys.Any(k => k.StartsWith("713-", StringComparison.Ordinal));
            var temEstab = campos.Keys.Any(k => k.StartsWith("715-", StringComparison.Ordinal));
            var temUnica = campos.Keys.Any(k => k.StartsWith("029-", StringComparison.Ordinal));
            if (!temCliente) Vias(campos, "712", "713", Texto(pe, "comprovanteCliente"));
            if (!temEstab) Vias(campos, "714", "715", Texto(pe, "comprovanteEstabelecimento"));
            if (!temCliente && !temEstab && !temUnica) Vias(campos, "028", "029", Texto(pe, "comprovanteAdquirente"));
            if (!campos.ContainsKey("737-000"))
                campos["737-000"] = campos.Keys.Any(k => k.StartsWith("713-")) && campos.Keys.Any(k => k.StartsWith("715-")) ? "3"
                    : campos.Keys.Any(k => k.StartsWith("713-")) ? "1" : campos.Keys.Any(k => k.StartsWith("715-")) ? "2" : "0";
        }
        Def("729-000", "1");   // ControlPay: nada a confirmar pelo PDV
        return RespostaPayGo.Analisar(ArquivoIntpos.Serializar(campos));
    }

    private static void Vias(Dictionary<string, string> campos, string contador, string prefixo, string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return;
        var linhas = texto.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        while (linhas.Count > 0 && linhas[0].Trim().Length == 0) linhas.RemoveAt(0);
        while (linhas.Count > 0 && linhas[^1].Trim().Length == 0) linhas.RemoveAt(linhas.Count - 1);
        if (linhas.Count == 0) return;
        campos[contador + "-000"] = linhas.Count.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < linhas.Count; i++)
            campos[$"{prefixo}-{i + 1:000}"] = "\"" + linhas[i] + "\"";
    }

    private CartaoTef Cartao(RespostaPayGo r)
    {
        var rede = r.Rede;
        string? cnpj = null;
        if (!string.IsNullOrWhiteSpace(rede))
        {
            try { cnpj = CnpjDaRede?.Invoke(rede!); } catch { cnpj = null; }
            cnpj ??= ClientePayGo.CnpjConhecido(rede!);
        }
        var nome = r.NomeCartao ?? r.Produto;
        return new CartaoTef(CAut: r.Autorizacao, Cnpj: cnpj, TBand: ClientePayGo.TBand(nome), Bandeira: nome,
            Adquirente: rede, Nsu: r.Nsu, Parcelas: r.Parcelas, Terminal: r.Terminal,
            Valor: r.ValorCent is { } c ? c / 100m : null);
    }

    // ------------------------------------------------------------------ HTTP

    /// <summary>
    /// Chama a API. A chave vai na query (`key=`) — por contrato da PayGo — e NUNCA aparece em
    /// mensagens: toda exceção passa por <see cref="Sanitizar"/>. Devolve o JSON; lança em HTTP ≠ 2xx.
    /// </summary>
    private async Task<JsonDocument> ChamarAsync(HttpMethod metodo, string caminhoEQuery, object? body, CancellationToken ct)
    {
        var sep = caminhoEQuery.Contains('?') ? "&" : "?";
        var url = _op.BaseUrl.TrimEnd('/') + "/" + caminhoEQuery.TrimStart('/') + sep + "key=" + Uri.EscapeDataString(_op.Chave);
        using var req = new HttpRequestMessage(metodo, url);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TempoHttpMs);
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"ControlPay não respondeu em {TempoHttpMs / 1000} s ({Rota(caminhoEQuery)})"); }
        catch (HttpRequestException ex) { throw new HttpRequestException(Sanitizar(ex.Message), null, ex.StatusCode); }
        using (resp)
        {
            var texto = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string msg;
                try { using var d = JsonDocument.Parse(texto); msg = Mensagem(d); } catch { msg = texto.Length > 200 ? texto[..200] : texto; }
                if (resp.StatusCode == HttpStatusCode.Unauthorized) msg = "chave de integração recusada (401) — " + msg;
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode} em {Rota(caminhoEQuery)}: {Sanitizar(msg)}", null, resp.StatusCode);
            }
            try { return JsonDocument.Parse(texto); }
            catch (JsonException) { throw new HttpRequestException($"resposta não é JSON em {Rota(caminhoEQuery)}"); }
        }
    }

    private static string Rota(string caminhoEQuery) => caminhoEQuery.Split('?')[0].Trim('/');

    /// <summary>Tira a chave (codificada ou não) de qualquer texto antes de ele virar mensagem/auditoria.</summary>
    public string Sanitizar(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return texto ?? "";
        var t = texto.Replace(_op.Chave, "***", StringComparison.Ordinal)
                     .Replace(Uri.EscapeDataString(_op.Chave), "***", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(_op.SenhaTecnica)) t = t.Replace(_op.SenhaTecnica, "***", StringComparison.Ordinal);
        return t;
    }

    // ------------------------------------------------------------------ JSON helpers

    private static (int, string) StatusDe(JsonElement iv)
    {
        if (!iv.TryGetProperty("intencaoVendaStatus", out var s) || s.ValueKind != JsonValueKind.Object) return (0, "?");
        return (Inteiro(s, "id") ?? 0, Texto(s, "nome") ?? "?");
    }

    private static JsonElement PrimeiraIntencao(JsonDocument doc)
    {
        if (doc.RootElement.TryGetProperty("intencoesVendas", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            return arr[0];
        if (doc.RootElement.TryGetProperty("intencaoVenda", out var iv) && iv.ValueKind == JsonValueKind.Object) return iv;
        return default;
    }

    /// <summary>A doc grafa "pagamentosExternos" (atual) e "pagamentosExterno" (antiga) — aceitar os dois.</summary>
    private static IEnumerable<JsonElement> PagamentosExternos(JsonElement iv)
    {
        foreach (var nome in new[] { "pagamentosExternos", "pagamentosExterno" })
            if (iv.TryGetProperty(nome, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var p in arr.EnumerateArray()) yield return p;
    }

    private static JsonElement Mesclar(JsonElement a, JsonElement b)
    {
        // b completa a (sem sobrescrever o que a já tem com valor)
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in b.EnumerateObject()) dict[p.Name] = p.Value;
        foreach (var p in a.EnumerateObject())
            if (p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) dict[p.Name] = p.Value;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var (k, v) in dict) { w.WritePropertyName(k); v.WriteTo(w); }
            w.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    private static string Mensagem(JsonDocument d)
        => d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("message", out var m) ? m.ToString() : d.RootElement.ToString();

    private static string? Texto(JsonElement e, string nome)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(nome, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static long? Longo(JsonElement e, string nome)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(nome, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    private static int? Inteiro(JsonElement e, string nome) => Longo(e, nome) is { } l ? (int)l : null;
    private static bool Bool(JsonElement e, string nome)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString()?.ToLowerInvariant() == "true"));

    /// <summary>"12345,67" — string com vírgula, sem separador de milhar (é o que a doc mostra: "26,00").</summary>
    public static string ValorComVirgula(long centavos)
        => (centavos / 100m).ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>Referência única por cobrança (vai em `referencia` e no charge_id): epoch em ms + sequência.</summary>
    public static string NovaReferencia()
    {
        while (true)
        {
            var agora = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ultima = Interlocked.Read(ref _ultimaRef);
            var nova = Math.Max(agora, ultima + 1);
            if (Interlocked.CompareExchange(ref _ultimaRef, nova, ultima) == ultima)
                return nova.ToString(CultureInfo.InvariantCulture);
        }
    }

    private bool GuardarSeguro(TransacaoPayGo t)
    {
        try { return Guardar(t); }
        catch { return false; }
    }

    private async Task<bool> ImprimirSeguroAsync(TransacaoPayGo t)
    {
        if (ImprimirComprovante is null || t.Resposta is null || !t.Resposta.TemVias) return true;
        try { return await ImprimirComprovante(t).ConfigureAwait(false); }
        catch (Exception ex) { Auditar?.Invoke("controlpay: impressão do comprovante falhou — " + Sanitizar(ex.Message)); return false; }
    }

    private static DesfechoTef Falha(SituacaoTef s, string chargeId, string codigo, string motivo, bool posOcupado = false)
        => new(s, null, chargeId, null, motivo, posOcupado) { Codigo = codigo };
}
