using System.Diagnostics;
using System.Globalization;

namespace Pdv.Nucleo;

// TEF PayGo Windows pela PGWebLib (biblioteca). Terceiro provedor; segue o MESMO contrato dos
// outros dois (troca de arquivos em PayGo.cs, WebService em ControlPay.cs): a tela cobra, recebe
// um DesfechoTef, grava `tef_transacao` pelos reports e pelo hook Guardar. O que é inegociável
// aqui vem da spec da PGWebLib e é o mesmo two-phase do PayGo por arquivos:
//
//   1. PW_iExecTransac é um LAÇO: PWRET_MOREDATA pede dados (menu/digitado, ou captura no
//      pinpad via PW_iPP* + PW_iPPEventLoop) e PWRET_NOTHING pede paciência. Nada aqui trava
//      sem teto: cada laço tem relógio e o operador pode abortar (PW_iPPAbort);
//   2. ao terminar, ler IMEDIATAMENTE PWINFO_CNFREQ; se "1", GRAVAR (REQNUM, AUTLOCREF,
//      AUTEXTREF, VIRTMERCH, AUTHSYST) em tef_transacao e SÓ ENTÃO PW_iConfirmation
//      (PWCNF_CNF_AUTO) ou desfazer (PWCNF_REV_*). Sem gravar não há confirmação;
//   3. transação não confirmada BLOQUEIA o ponto de captura: no religamento os PWINFO_PND*
//      descrevem a pendência e o PDV resolve sozinho (CNF se conhece como paga, REV se não),
//      nunca perguntando ao operador;
//   4. confirmação sem ack fica 'cnf_sem_ack'/'ncn_sem_ack' e é reenviada antes do próximo
//      comando e no religamento; PWRET_INVALIDTRN NÃO é ack (a biblioteca só diz que não tem
//      a transação pendente): a linha vira 'orfa', nunca 'pago' nem 'desfeita';
//   5. CNFREQ=0 é aprovação DEFINITIVA: não existe REV. Falha ao gravar ou saída do operador
//      depois dela nunca dizem "desfeita" nem "cancelado": é paga ou órfã com aviso;
//   6. PWINFO_CARDFULLPAN (193) NUNCA é lido. Nem para log.

/// <summary>Identidade da automação (AUTNAME/AUTVER/AUTDEV), capacidades (AUTCAP) e redes pré-selecionadas.</summary>
/// <param name="RedeCartao">Valor para o menu PWINFO_AUTHSYST em cartão (ex.: `REDE`). Null = a biblioteca mostra o menu.</param>
/// <param name="RedePix">Idem para Pix. Null = menu.</param>
/// <param name="PortaPinpad">PWINFO_PPCOMMPORT; "0" = automática.</param>
public sealed record OpcoesPGWebLib(string NomeAutomacao, string VersaoAutomacao, string Desenvolvedor,
    int Capacidades = ProvedorPGWebLib.CapacidadesPadrao, string? RedeCartao = null, string? RedePix = null,
    string PortaPinpad = "0", string Moeda = "986");

/// <summary>
/// Provedor de TEF sobre <see cref="IPGWebLib"/>. Uma instância por processo; as chamadas são
/// serializadas por um semáforo porque a biblioteca guarda UMA transação corrente.
/// </summary>
public sealed class ProvedorPGWebLib : IProvedorTefOperavel, IDisposable
{
    public string Nome => "pgweblib";

    /// <summary>4 (valor fixo) + 8 (vias diferenciadas) + 16 (via reduzida).</summary>
    public const int CapacidadesPadrao = PW.CAP_VALOR_FIXO + PW.CAP_VIAS_DIFERENCIADAS + PW.CAP_VIA_REDUZIDA;

    /// <summary>Diretório de trabalho em branco: o da casa (ConfigPGWebLib.DirPadrao), fora de C:\PAYGO de propósito.</summary>
    public const string PastaPadrao = ConfigPGWebLib.DirPadrao;

    public const string MsgTefNaoResponde = "TEF não responde: a PGWebLib não iniciou. Confira o PayGo Windows";
    public const string MsgNaoInstalado = "PayGo não instalado neste terminal: faça a instalação pelo menu do TEF";

    /// <summary>CNFREQ=0 (já definitiva na rede) e o caixa não gravou: não existe REV; o cliente JÁ pagou.</summary>
    public static string MsgNaoGravada(string? nsu) => $"Cobrança aprovada (NSU {nsu ?? "-"}) mas não gravada no caixa. Não cobre de novo: confira no PayGo";
    public static string MsgCancelamentoNaoGravado(string? nsu) => $"Cancelamento aprovado (NSU {nsu ?? "-"}) mas não gravado no caixa. Não repita: confira no PayGo";
    /// <summary>PWRET_INVALIDTRN: a biblioteca não tem esta transação pendente; não diz se confirmou ou desfez.</summary>
    public static string MsgNaoReconhece(string? nsu) => $"PayGo não reconhece esta transação (NSU {nsu ?? "-"}). Confira no relatório antes de cobrar de novo";

    /// <summary>Cadência dos laços (PWRET_NOTHING e PW_iPPEventLoop).</summary>
    public int IntervaloPollMs { get; init; } = 100;

    /// <summary>Teto de PWRET_NOTHING seguidos antes de abortar (a biblioteca está com o host ou o pinpad).</summary>
    public int TempoMaxExecMs { get; init; } = 600_000;

    /// <summary>Teto de uma captura no pinpad (PW_iPPEventLoop) antes de PW_iPPAbort.</summary>
    public int TempoMaxCapturaMs { get; init; } = 300_000;

    /// <summary>Teto para a tela responder um menu/dado digitado (PWDAT_MENU/TYPED).</summary>
    public int TempoPerguntaMs { get; init; } = 120_000;

    /// <summary>
    /// Cadência de segurança do PW_iIdleProc: vale quando a biblioteca não informa
    /// PWINFO_IDLEPROCTIME (vazio ou inválido). <see cref="IniciarIdle"/> grava o intervalo do timer aqui.
    /// </summary>
    public int IntervaloIdleMs { get; set; } = 60_000;

    /// <summary>Grava a transação em disco. Chamado ANTES da confirmação; false ou exceção = desfaz (REV).</summary>
    public Func<TransacaoPayGo, bool> Guardar { get; init; } = _ => true;

    /// <summary>CNPJ da credenciadora pelo nome da rede (AUTHSYST). Null = tpIntegra=2 na NFC-e.</summary>
    public Func<string, string?>? CnpjDaRede { get; init; }

    /// <summary>Pendência que a biblioteca descreve (PND*): este caixa conhece o REQNUM como pago? true = CNF; false = REV.</summary>
    public Func<string, bool>? ConhecidaConfirmada { get; init; }

    public Action<string>? Auditar { get; init; }

    /// <summary>Imprime as vias ANTES da confirmação; false = desfaz (PWCNF_REV_PRN_AUT). Null = terminal sem impressão de TEF.</summary>
    public Func<TransacaoPayGo, Task<bool>>? ImprimirComprovante { get; init; }

    /// <summary>
    /// Pergunta à tela um dado que a automação não sabe (menu de redes sem rede pré-selecionada,
    /// dado digitado, senha do lojista). Devolve o valor (para menu: o `Valor` da opção) ou null
    /// para cancelar. Null aqui = nunca pergunta (cancela a captura).
    /// </summary>
    public Func<PwGetData, CancellationToken, Task<string?>>? Perguntar { get; init; }

    private readonly IPGWebLib _lib;
    private readonly string _pasta;
    private readonly OpcoesPGWebLib _op;
    private readonly SemaphoreSlim _um = new(1, 1);
    private bool _iniciada;
    private Timer? _idle;

    /// <summary>Horário (local) da próxima PW_iIdleProc, lido de PWINFO_IDLEPROCTIME. Null = não informado.</summary>
    public DateTime? ProximoIdle { get; private set; }

    /// <summary>CNF/REV que a biblioteca não acusou: reenviados antes do próximo comando e no religamento.</summary>
    private readonly List<(TransacaoPayGo Tx, uint Resultado, string Depois)> _reenvios = new();

    public ProvedorPGWebLib(IPGWebLib lib, string pastaTrabalho, OpcoesPGWebLib opcoes)
    {
        _lib = lib ?? throw new ArgumentNullException(nameof(lib));
        _pasta = string.IsNullOrWhiteSpace(pastaTrabalho) ? PastaPadrao : pastaTrabalho;
        _op = opcoes ?? throw new ArgumentNullException(nameof(opcoes));
    }

    public string PastaTrabalho => _pasta;
    public bool Ocupado => _um.CurrentCount == 0;
    public string Descricao => "PayGo Windows (PGWebLib) em " + _pasta;

    // ------------------------------------------------------------------ init / ativo

    /// <summary>PW_iInit uma vez por processo. PWRET_INVCALL = já iniciada (por nós ou por outro módulo) e serve.</summary>
    private bool Iniciar()
    {
        if (_iniciada) return true;
        short ret;
        try { ret = _lib.Init(_pasta); }
        catch (Exception ex)
        {
            // DllNotFoundException, BadImageFormatException (bitness), AccessViolation da convenção errada…
            Auditar?.Invoke("pgweblib: PW_iInit lançou: " + ex.GetType().Name + " " + ex.Message);
            return false;
        }
        _iniciada = ret is PW.PWRET_OK or PW.PWRET_INVCALL;
        if (!_iniciada) { Auditar?.Invoke("pgweblib: PW_iInit devolveu " + PW.Nome(ret)); return false; }
        // Spec: depois do PW_iInit (e de cada PW_iIdleProc) ler PWINFO_IDLEPROCTIME para saber
        // quando chamar o próximo PW_iIdleProc.
        AgendarIdle(Ler(PW.PWINFO_IDLEPROCTIME), "PW_iInit");
        return true;
    }

    public async Task<bool> AtivoAsync(CancellationToken ct)
    {
        await _um.WaitAsync(ct).ConfigureAwait(false);
        try { return Iniciar(); }
        finally { _um.Release(); }
    }

    // ------------------------------------------------------------------ venda

    public async Task<DesfechoTef> CobrarAsync(TipoTef tipo, Dinheiro valor, string? documento, int parcelas,
        IProgress<AndamentoTef>? andamento, CancellationToken ct)
    {
        var id = ClientePayGo.NovaIdentificacao();
        var chargeId = "pgweb-" + id;
        if (!valor.Positivo)
            return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "valor da cobrança tem que ser maior que zero");
        var parc = tipo == TipoTef.Credito ? Math.Max(1, parcelas) : 1;
        andamento?.Report(new AndamentoTef(FaseTef.Criando, chargeId, null, "Enviando a cobrança para o TEF…"));

        try { await _um.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Falha(SituacaoTef.Cancelado, chargeId, CodigoTef.Cancelado, "cobrança cancelada pelo operador"); }
        try
        {
            var prep = await PrepararAsync(PW.PWOPER_SALE, chargeId).ConfigureAwait(false);
            if (prep is not null) return prep;

            var ctx = new Contexto(chargeId, id, "CRT", tipo, valor.Centavos, parc, andamento, ct);
            ParametrosDeIdentidade(ctx);
            Param(ctx, PW.PWINFO_TOTAMNT, valor.Centavos.ToString(CultureInfo.InvariantCulture));
            Param(ctx, PW.PWINFO_CURRENCY, _op.Moeda);
            Param(ctx, PW.PWINFO_CURREXP, "2");
            if (!string.IsNullOrWhiteSpace(documento)) Param(ctx, PW.PWINFO_FISCALREF, documento!);
            if (tipo == TipoTef.Pix)
            {
                Param(ctx, PW.PWINFO_PAYMNTTYPE, PW.PAYMNTTYPE_CARTEIRA_DIGITAL);
                if (!string.IsNullOrWhiteSpace(_op.RedePix)) Param(ctx, PW.PWINFO_AUTHSYST, _op.RedePix!);
            }
            else
            {
                Param(ctx, PW.PWINFO_PAYMNTTYPE, PW.PAYMNTTYPE_CARTAO);
                Param(ctx, PW.PWINFO_CARDTYPE, tipo switch
                {
                    TipoTef.Credito => PW.CARDTYPE_CREDITO,
                    TipoTef.Debito => PW.CARDTYPE_DEBITO,
                    _ => PW.CARDTYPE_VOUCHER,
                });
                Param(ctx, PW.PWINFO_FINTYPE, parc > 1 ? PW.FINTYPE_PARCELADO_LOJA : PW.FINTYPE_AVISTA);
                if (parc > 1) Param(ctx, PW.PWINFO_INSTALLMENTS, parc.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrWhiteSpace(_op.RedeCartao)) Param(ctx, PW.PWINFO_AUTHSYST, _op.RedeCartao!);
            }
            Param(ctx, PW.PWINFO_USINGPINPAD, "1");
            Param(ctx, PW.PWINFO_PPCOMMPORT, _op.PortaPinpad);

            // A partir daqui a biblioteca pode ir ao host: linha 'aguardando' antes de executar.
            Guardar(new TransacaoPayGo(chargeId, id, tipo, valor.Centavos, parc, "aguardando", null));
            andamento?.Report(new AndamentoTef(FaseTef.Aguardando, chargeId, id,
                tipo == TipoTef.Pix ? "Peça ao cliente para ler o QR no pinpad…" : "Aproxime, insira ou passe o cartão no pinpad…"));

            var fim = await ExecutarAsync(ctx).ConfigureAwait(false);
            var r = RespostaDaLib("CRT", id, valor.Centavos, tipo, parc, fim.Aprovada, fim.Resultados);

            if (!fim.Aprovada)
            {
                // Terminou sem aprovação: se a biblioteca marcou CNFREQ=1 (aprovou e o operador
                // interrompeu no meio, ou o host aprovou e a captura caiu) desfaz antes de sair.
                if (fim.RequerConfirmacao)
                    await DesfazerSeRequeridoAsync(ctx, fim, r).ConfigureAwait(false);   // grava 'aprovada' -> 'desfeita'
                else
                {
                    // Sem prova de que o pinpad parou (PosOcupado) é ÓRFÃ: alguém confere no PayGo.
                    var sit = fim.Situacao switch
                    {
                        _ when fim.PosOcupado => "orfa",
                        SituacaoTef.Cancelado or SituacaoTef.Timeout => "cancelado",
                        SituacaoTef.Recusado => "recusado",
                        _ => "erro",
                    };
                    Guardar(new TransacaoPayGo(chargeId, id, tipo, valor.Centavos, parc, sit, r, fim.Motivo));
                }
                return new DesfechoTef(fim.Situacao, id, chargeId, null, fim.Motivo, fim.PosOcupado)
                { Codigo = fim.Codigo, Desfeita = fim.Desfeita };
            }

            var tx = new TransacaoPayGo(chargeId, id, tipo, valor.Centavos, parc, "aprovada", r);
            var cartao = Cartao(r);
            if (fim.Nota is not null)
                Auditar?.Invoke($"pgweblib: {chargeId} aprovada e definitiva (CNFREQ=0); saída depois da aprovação ignorada: {fim.Nota}");

            // MEMÓRIA NÃO VOLÁTIL ANTES DA CONFIRMAÇÃO. Não gravou = desfaz (REV): cliente cobrado
            // sem o PDV saber é o pior desfecho possível. Com CNFREQ=0 NÃO existe REV: a rede já
            // efetivou; a linha vira 'orfa' e a tela manda conferir, nunca "cobre de novo".
            if (!GuardarSeguro(tx))
            {
                if (!fim.RequerConfirmacao) return Orfa(tx, cartao, MsgNaoGravada(r.Nsu));
                if (Desfazer(tx with { Motivo = "não gravou no caixa (REV)" }, PW.PWCNF_REV_OTHER_AUT, "desfeita") == Ack.Desconhecida)
                    return Orfa(tx, cartao, MsgNaoReconhece(r.Nsu), gravar: false);
                return new DesfechoTef(SituacaoTef.Erro, id, chargeId, cartao,
                    "não consegui gravar a transação no caixa: transação desfeita, cobre de novo", false)
                { Codigo = CodigoTef.Plataforma, Desfeita = true };
            }

            // Operador desistiu depois da aprovação: só dá para atender com REV (CNFREQ=1). Com
            // CNFREQ=0 o dinheiro já andou; a venda segue como paga.
            if (fim.RequerConfirmacao && ctx.Ct.IsCancellationRequested)
            {
                if (Desfazer(tx with { Motivo = "cancelada pelo operador (REV)" }, PW.PWCNF_REV_ABORT, "desfeita") == Ack.Desconhecida)
                    return Orfa(tx, cartao, MsgNaoReconhece(r.Nsu), gravar: false);
                return new DesfechoTef(SituacaoTef.Cancelado, id, chargeId, null, "cobrança cancelada pelo operador: transação desfeita", false)
                { Codigo = CodigoTef.Cancelado, Desfeita = true };
            }

            var situacao = "pago";
            if (fim.RequerConfirmacao)
            {
                // Comprovante ANTES da confirmação (spec: a impressão decide o commit).
                if (!await ImprimirSeguroAsync(tx).ConfigureAwait(false))
                {
                    if (Desfazer(tx with { Motivo = "comprovante não impresso (REV)" }, PW.PWCNF_REV_PRN_AUT, "desfeita") == Ack.Desconhecida)
                        return Orfa(tx, cartao, MsgNaoReconhece(r.Nsu), gravar: false);
                    Auditar?.Invoke($"pgweblib: {chargeId} desfeita (comprovante não saiu)");
                    return new DesfechoTef(SituacaoTef.Cancelado, id, chargeId, cartao,
                        ClientePayGo.MsgCancelada(r.Rede, r.Nsu, r.ValorCent ?? valor.Centavos), false)
                    { Codigo = CodigoTef.Cancelado, Desfeita = true };
                }
                switch (Confirmar(tx, "pago"))
                {
                    case Ack.Ok: break;
                    case Ack.SemAck: situacao = "cnf_sem_ack"; break;
                    default: return Orfa(tx, cartao, MsgNaoReconhece(r.Nsu), gravar: false);
                }
            }
            else
            {
                Guardar(tx with { Situacao = "pago" });
                if (!await ImprimirSeguroAsync(tx).ConfigureAwait(false))
                    Auditar?.Invoke($"pgweblib: {chargeId} paga sem confirmação (CNFREQ=0) com comprovante não impresso");
            }

            return new DesfechoTef(SituacaoTef.Pago, id, chargeId, cartao, null, false)
            { Codigo = CodigoTef.Pago, PaymentStatus = situacao };
        }
        finally { _um.Release(); }
    }

    // ------------------------------------------------------------------ cancelamento (SALEVOID)

    public async Task<DesfechoTef> CancelarAsync(TransacaoPayGo original, CancellationToken ct)
    {
        var r0 = original.Resposta;
        var id = ClientePayGo.NovaIdentificacao();
        var chargeId = "pgweb-cnc-" + id;
        if (r0 is null || string.IsNullOrWhiteSpace(r0.Nsu))
            return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "transação original sem NSU: cancele pelo menu do PayGo");

        await _um.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var prep = await PrepararAsync(PW.PWOPER_SALEVOID, chargeId).ConfigureAwait(false);
            if (prep is not null) return prep;

            var ctx = new Contexto(chargeId, id, "CNC", original.Tipo, original.ValorCent, original.Parcelas, null, ct);
            ParametrosDeIdentidade(ctx);
            Param(ctx, PW.PWINFO_CURRENCY, _op.Moeda);
            Param(ctx, PW.PWINFO_CURREXP, "2");
            Param(ctx, PW.PWINFO_TRNORIGNSU, r0.Nsu!);
            Param(ctx, PW.PWINFO_TRNORIGAMNT, original.ValorCent.ToString(CultureInfo.InvariantCulture));
            if (r0.Autorizacao is not null) Param(ctx, PW.PWINFO_TRNORIGAUTH, r0.Autorizacao);
            if (r0.CodigoControle is not null) Param(ctx, PW.PWINFO_TRNORIGREQNUM, r0.CodigoControle);
            var (data, hora) = DataHoraOriginal(r0);
            if (data is not null) Param(ctx, PW.PWINFO_TRNORIGDATE, data);
            if (hora is not null) Param(ctx, PW.PWINFO_TRNORIGTIME, hora);
            if (r0.Rede is not null) Param(ctx, PW.PWINFO_AUTHSYST, r0.Rede);
            Param(ctx, PW.PWINFO_USINGPINPAD, "1");
            Param(ctx, PW.PWINFO_PPCOMMPORT, _op.PortaPinpad);

            var fim = await ExecutarAsync(ctx).ConfigureAwait(false);
            var r = RespostaDaLib("CNC", id, original.ValorCent, original.Tipo, original.Parcelas, fim.Aprovada, fim.Resultados);
            if (!fim.Aprovada)
            {
                await DesfazerSeRequeridoAsync(ctx, fim, r).ConfigureAwait(false);
                return new DesfechoTef(fim.Situacao, id, chargeId, null, fim.Motivo, fim.PosOcupado) { Codigo = fim.Codigo, Desfeita = fim.Desfeita };
            }

            var tx = new TransacaoPayGo(chargeId, id, original.Tipo, original.ValorCent, original.Parcelas, "aprovada", r, "cancelamento de " + original.ChargeId);
            if (fim.Nota is not null)
                Auditar?.Invoke($"pgweblib: {chargeId} cancelamento aprovado e definitivo (CNFREQ=0); saída depois da aprovação ignorada: {fim.Nota}");
            if (!GuardarSeguro(tx))
            {
                // Sem confirmação pendente não há REV: o cancelamento já vale na rede. Órfã, nunca "desfeito".
                if (!fim.RequerConfirmacao) return Orfa(tx, Cartao(r), MsgCancelamentoNaoGravado(r.Nsu));
                if (Desfazer(tx with { Motivo = "não gravou o cancelamento (REV)" }, PW.PWCNF_REV_OTHER_AUT, "desfeita") == Ack.Desconhecida)
                    return Orfa(tx, Cartao(r), MsgNaoReconhece(r.Nsu), gravar: false);
                return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "não consegui gravar o cancelamento (desfeito)");
            }
            var sit = "estornado";
            if (fim.RequerConfirmacao)
            {
                if (!await ImprimirSeguroAsync(tx).ConfigureAwait(false))
                {
                    if (Desfazer(tx with { Motivo = "comprovante do cancelamento não impresso (REV)" }, PW.PWCNF_REV_PRN_AUT, "desfeita") == Ack.Desconhecida)
                        return Orfa(tx, Cartao(r), MsgNaoReconhece(r.Nsu), gravar: false);
                    return new DesfechoTef(SituacaoTef.Cancelado, id, chargeId, Cartao(r),
                        ClientePayGo.MsgCancelada(r.Rede, r.Nsu, r.ValorCent ?? original.ValorCent), false)
                    { Codigo = CodigoTef.Cancelado, Desfeita = true };
                }
                switch (Confirmar(tx, "estornado"))
                {
                    case Ack.Ok: break;
                    case Ack.SemAck: sit = "cnf_sem_ack"; break;
                    default: return Orfa(tx, Cartao(r), MsgNaoReconhece(r.Nsu), gravar: false);
                }
            }
            else
            {
                Guardar(tx with { Situacao = "estornado" });
                await ImprimirSeguroAsync(tx).ConfigureAwait(false);
            }
            Guardar(original with { Situacao = "estornada", Motivo = "estornada por " + chargeId });
            return new DesfechoTef(SituacaoTef.Pago, id, chargeId, Cartao(r), null, false) { Codigo = CodigoTef.Pago, PaymentStatus = sit };
        }
        finally { _um.Release(); }
    }

    // ------------------------------------------------------------------ administrativa / reimpressão / instalação

    public Task<DesfechoTef> AdministrativaAsync(CancellationToken ct) => OperacaoAsync(PW.PWOPER_ADMIN, "pgweb-adm-", "administrativa", ct);

    /// <summary>PWOPER_REPRINT: a biblioteca reimprime a última (ou a escolhida no menu). As vias guardadas continuam no menu Reimpressão.</summary>
    public Task<DesfechoTef> ReimprimirAsync(CancellationToken ct) => OperacaoAsync(PW.PWOPER_REPRINT, "pgweb-rep-", "reimpressão", ct);

    /// <summary>PWOPER_INSTALL: ativação do ponto de captura (CNPJ + PdC). Só uma vez por terminal.</summary>
    public Task<DesfechoTef> InstalarAsync(CancellationToken ct) => OperacaoAsync(PW.PWOPER_INSTALL, "pgweb-inst-", "instalação", ct);

    private async Task<DesfechoTef> OperacaoAsync(byte oper, string prefixo, string rotulo, CancellationToken ct)
    {
        var id = ClientePayGo.NovaIdentificacao();
        var chargeId = prefixo + id;
        await _um.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var prep = await PrepararAsync(oper, chargeId).ConfigureAwait(false);
            if (prep is not null) return prep;
            var ctx = new Contexto(chargeId, id, "ADM", TipoTef.Credito, 0, 1, null, ct);
            ParametrosDeIdentidade(ctx);
            Param(ctx, PW.PWINFO_USINGPINPAD, "1");
            Param(ctx, PW.PWINFO_PPCOMMPORT, _op.PortaPinpad);

            var fim = await ExecutarAsync(ctx).ConfigureAwait(false);
            var r = RespostaDaLib("ADM", id, 0, TipoTef.Credito, 1, fim.Aprovada, fim.Resultados);
            if (!fim.Aprovada)
            {
                await DesfazerSeRequeridoAsync(ctx, fim, r).ConfigureAwait(false);
                return new DesfechoTef(fim.Situacao, id, chargeId, null, fim.Motivo, fim.PosOcupado) { Codigo = fim.Codigo, Desfeita = fim.Desfeita };
            }
            // 'adm', nunca 'pago': o valor de uma administrativa (ex.: cancelamento pelo menu) não é venda.
            var tx = new TransacaoPayGo(chargeId, id, TipoTef.Credito, r.ValorCent ?? 0, 1, "aprovada", r, rotulo);
            var sit = "adm";
            if (fim.RequerConfirmacao)
            {
                if (!GuardarSeguro(tx))
                {
                    if (Desfazer(tx with { Motivo = "não gravou (REV)" }, PW.PWCNF_REV_OTHER_AUT, "desfeita") == Ack.Desconhecida)
                        return Orfa(tx, null, MsgNaoReconhece(r.Nsu), gravar: false);
                    return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "não consegui gravar a operação (desfeita)");
                }
                if (!await ImprimirSeguroAsync(tx).ConfigureAwait(false))
                {
                    if (Desfazer(tx with { Motivo = "comprovante não impresso (REV)" }, PW.PWCNF_REV_PRN_AUT, "desfeita") == Ack.Desconhecida)
                        return Orfa(tx, null, MsgNaoReconhece(r.Nsu), gravar: false);
                    return new DesfechoTef(SituacaoTef.Cancelado, id, chargeId, null, ClientePayGo.MsgCancelada(r.Rede, r.Nsu, r.ValorCent), false)
                    { Codigo = CodigoTef.Cancelado, Desfeita = true };
                }
                switch (Confirmar(tx, "adm"))
                {
                    case Ack.Ok: break;
                    case Ack.SemAck: sit = "cnf_sem_ack"; break;
                    default: return Orfa(tx, null, MsgNaoReconhece(r.Nsu), gravar: false);
                }
            }
            else
                await ImprimirSeguroAsync(tx).ConfigureAwait(false);
            return new DesfechoTef(SituacaoTef.Pago, id, chargeId, null, r.Mensagem, false) { Codigo = CodigoTef.Pago, PaymentStatus = sit };
        }
        finally { _um.Release(); }
    }

    // ------------------------------------------------------------------ pendências (religamento)

    /// <summary>
    /// Varredura do boot: (a) reenvia CNF/REV sem ack e decide as 'aprovada' pelo que o caixa
    /// sabe (venda concluída → CNF; sem venda → REV); (b) lê PWINFO_PND* da biblioteca: se ela
    /// ainda descreve uma pendência, confirma se este caixa a conhece como paga, senão desfaz
    /// (PWCNF_REV_PWR_AUT). Quem decide é o PDV, nunca o operador. Devolve quantas resolveu.
    /// </summary>
    public async Task<int> ResolverPendenciasAsync(IReadOnlyList<(TransacaoPayGo Tx, bool VendaConcluida)> pendentes)
    {
        var n = 0;
        await _um.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Iniciar()) { Auditar?.Invoke("pgweblib: religamento sem PW_iInit; pendências ficam para o próximo boot"); return 0; }
            Reenviar();
            var vistas = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (tx, concluida) in pendentes)
            {
                if (tx.Resposta is null || !vistas.Add(tx.ChargeId)) continue;
                if (tx.Situacao is not ("aprovada" or "cnf_sem_ack" or "ncn_sem_ack")) continue;
                if (string.IsNullOrWhiteSpace(tx.CodigoControle))
                {
                    Auditar?.Invoke($"pgweblib: pendência {tx.ChargeId} sem REQNUM, não dá para CNF/REV; marcada órfã");
                    GuardarSeguro(tx with { Situacao = "orfa", Motivo = "aprovada sem REQNUM; confira no PayGo" });
                    n++;
                    continue;
                }
                switch (tx.Situacao)
                {
                    case "aprovada" when concluida:
                    case "cnf_sem_ack":
                        Confirmar(tx, Final(tx)); n++; break;
                    case "aprovada":
                        Desfazer(tx with { Motivo = "sem venda concluída no religamento (REV)" }, PW.PWCNF_REV_PWR_AUT, "desfeita"); n++; break;
                    case "ncn_sem_ack":
                        Desfazer(tx, PW.PWCNF_REV_PWR_AUT, "desfeita"); n++; break;
                }
            }

            // O que a BIBLIOTECA ainda segura (bloqueia o ponto de captura até resolver).
            var pnd = LerPendenciaDaLib();
            if (pnd is not null)
            {
                var conhecida = pendentes.Any(p => p.Tx.CodigoControle == pnd.ReqNum && (p.VendaConcluida || p.Tx.Situacao is "cnf_sem_ack" or "pago"))
                                || ConhecidaSegura(pnd.ReqNum);
                var ret = ConfirmacaoCrua(conhecida ? PW.PWCNF_CNF_AUTO : PW.PWCNF_REV_PWR_AUT, pnd.ReqNum, pnd.LocRef, pnd.ExtRef, pnd.VirtMerch, pnd.AuthSyst);
                Auditar?.Invoke($"pgweblib: pendência da biblioteca REQNUM {pnd.ReqNum} {(conhecida ? "confirmada" : "desfeita (REV_PWR)")}: {PW.Nome(ret)}");
                n++;
            }
        }
        finally { _um.Release(); }
        return n;
    }

    // ------------------------------------------------------------------ idle

    /// <summary>
    /// Roda PW_iIdleProc se PWINFO_IDLEPROCTIME já passou e nada está em voo (o semáforo é o
    /// mesmo das transações: nunca por cima de uma). Depois relê PWINFO_IDLEPROCTIME para
    /// agendar o próximo; sem horário válido, daqui a <see cref="IntervaloIdleMs"/>.
    /// </summary>
    public async Task<bool> IdleSeDevidoAsync()
    {
        if (ProximoIdle is not { } quando || quando > DateTime.Now) return false;
        if (!await _um.WaitAsync(0).ConfigureAwait(false)) return false;
        try
        {
            if (!Iniciar()) return false;
            short ret;
            try { ret = _lib.IdleProc(); }
            catch (Exception ex)
            {
                Auditar?.Invoke("pgweblib: PW_iIdleProc lançou: " + ex.Message);
                AgendarIdle(null, "PW_iIdleProc");
                return false;
            }
            // Só o desvio vai para a auditoria: com o intervalo de segurança isto roda o dia inteiro.
            if (ret != PW.PWRET_OK) Auditar?.Invoke("pgweblib: PW_iIdleProc " + PW.Nome(ret));
            AgendarIdle(Ler(PW.PWINFO_IDLEPROCTIME), "PW_iIdleProc");
            return ret == PW.PWRET_OK;
        }
        finally { _um.Release(); }
    }

    /// <summary>
    /// PWINFO_IDLEPROCTIME (YYMMDDhhmmss, hora local) vira <see cref="ProximoIdle"/>. Vazio ou
    /// inválido cai em agora + <see cref="IntervaloIdleMs"/>: a rotina nunca fica sem próxima vez.
    /// </summary>
    private void AgendarIdle(string? cru, string origem)
    {
        var v = cru?.Trim();
        if (!string.IsNullOrEmpty(v) && DateTime.TryParseExact(v, "yyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var quando))
        {
            ProximoIdle = quando;
            return;
        }
        ProximoIdle = DateTime.Now.AddMilliseconds(IntervaloIdleMs);
        if (!string.IsNullOrEmpty(v) && v != _idleInvalidoVisto)
        {
            // Lixo no campo: uma linha por valor diferente, não uma por rodada.
            _idleInvalidoVisto = v;
            Auditar?.Invoke($"pgweblib: PWINFO_IDLEPROCTIME inválido após {origem} ({v}); próximo PW_iIdleProc em {IntervaloIdleMs} ms");
        }
    }
    private string? _idleInvalidoVisto;

    /// <summary>Timer barato que chama <see cref="IdleSeDevidoAsync"/>; o intervalo também é a cadência de segurança (<see cref="IntervaloIdleMs"/>).</summary>
    public void IniciarIdle(int intervaloMs)
    {
        if (_descartado) return;
        IntervaloIdleMs = Math.Max(1, intervaloMs);
        _idle?.Dispose();
        _idle = new Timer(_ => { _ = IdleSeDevidoAsync(); }, null, IntervaloIdleMs, IntervaloIdleMs);
    }

    private bool _descartado;

    /// <summary>Para o timer do idle. Idempotente; a instância trocada em Servicos.RecarregarTef() passa por aqui.</summary>
    public void Dispose()
    {
        _descartado = true;
        var t = Interlocked.Exchange(ref _idle, null);
        t?.Dispose();
    }

    // ------------------------------------------------------------------ o laço

    private sealed class Contexto
    {
        public Contexto(string chargeId, string id, string cmd, TipoTef tipo, long valorCent, int parcelas, IProgress<AndamentoTef>? andamento, CancellationToken ct)
        { ChargeId = chargeId; Id = id; Cmd = cmd; Tipo = tipo; ValorCent = valorCent; Parcelas = parcelas; Andamento = andamento; Ct = ct; }
        public string ChargeId { get; }
        public string Id { get; }
        public string Cmd { get; }
        public TipoTef Tipo { get; }
        public long ValorCent { get; }
        public int Parcelas { get; }
        public IProgress<AndamentoTef>? Andamento { get; }
        public CancellationToken Ct { get; }
        /// <summary>Tudo que já foi passado à biblioteca: é a resposta pronta se ela pedir de novo por MOREDATA.</summary>
        public Dictionary<ushort, string> Conhecidos { get; } = new();
        public string? UltimoDisplay;
    }

    private sealed class Fim
    {
        public bool Aprovada;
        public bool RequerConfirmacao;
        public SituacaoTef Situacao = SituacaoTef.Erro;
        public string Codigo = CodigoTef.Plataforma;
        public string? Motivo;
        public bool PosOcupado;
        public bool Desfeita;
        /// <summary>Saída (cancel/timeout/erro) que aconteceu DEPOIS de uma aprovação definitiva (CNFREQ=0) e foi ignorada; só para auditoria.</summary>
        public string? Nota;
        public Dictionary<ushort, string> Resultados = new();
    }

    /// <summary>O que a biblioteca respondeu a um CNF/REV.</summary>
    private enum Ack
    {
        /// <summary>PWRET_OK: acusado.</summary>
        Ok,
        /// <summary>Sem ack (WRITERR, exceção…): ficou 'cnf_sem_ack'/'ncn_sem_ack' e vai ser reenviado.</summary>
        SemAck,
        /// <summary>PWRET_INVALIDTRN: a biblioteca não reconhece a transação; a linha ficou 'orfa'.</summary>
        Desconhecida,
    }

    private sealed record Pendencia(string ReqNum, string LocRef, string ExtRef, string VirtMerch, string AuthSyst);

    /// <summary>Init + reenvios + pendência da biblioteca + PW_iNewTransac. Null = pode seguir; senão o desfecho que impede.</summary>
    private async Task<DesfechoTef?> PrepararAsync(byte oper, string chargeId)
    {
        if (!Iniciar()) return Falha(SituacaoTef.Erro, chargeId, CodigoTef.TefNaoResponde, MsgTefNaoResponde);
        Reenviar();
        if (oper != PW.PWOPER_INSTALL && LerPendenciaDaLib() is { } pnd)
        {
            // Pendência bloqueia o ponto de captura: resolver antes, pelo que o caixa sabe.
            var conhecida = ConhecidaSegura(pnd.ReqNum);
            var ret = ConfirmacaoCrua(conhecida ? PW.PWCNF_CNF_AUTO : PW.PWCNF_REV_PWR_AUT, pnd.ReqNum, pnd.LocRef, pnd.ExtRef, pnd.VirtMerch, pnd.AuthSyst);
            Auditar?.Invoke($"pgweblib: pendência REQNUM {pnd.ReqNum} antes de {chargeId}: {(conhecida ? "CNF" : "REV_PWR")} {PW.Nome(ret)}");
        }
        short nt;
        try { nt = _lib.NewTransac(oper); }
        catch (Exception ex) { Auditar?.Invoke("pgweblib: PW_iNewTransac lançou: " + ex.Message); return Falha(SituacaoTef.Erro, chargeId, CodigoTef.TefNaoResponde, MsgTefNaoResponde); }
        if (nt == PW.PWRET_DLLNOTINIT) { _iniciada = false; if (Iniciar()) nt = _lib.NewTransac(oper); }
        await Task.CompletedTask.ConfigureAwait(false);
        return nt switch
        {
            PW.PWRET_OK => null,
            PW.PWRET_NOTINST => Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, MsgNaoInstalado),
            PW.PWRET_DLLNOTINIT => Falha(SituacaoTef.Erro, chargeId, CodigoTef.TefNaoResponde, MsgTefNaoResponde),
            _ => Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "TEF recusou a operação: " + PW.Nome(nt)),
        };
    }

    private void ParametrosDeIdentidade(Contexto ctx)
    {
        Param(ctx, PW.PWINFO_AUTNAME, _op.NomeAutomacao);
        Param(ctx, PW.PWINFO_AUTVER, _op.VersaoAutomacao);
        Param(ctx, PW.PWINFO_AUTDEV, _op.Desenvolvedor);
        Param(ctx, PW.PWINFO_AUTCAP, _op.Capacidades.ToString(CultureInfo.InvariantCulture));
    }

    private void Param(Contexto ctx, ushort info, string valor)
    {
        var v = ArquivoIntpos.Ascii(valor);
        ctx.Conhecidos[info] = v;
        var ret = _lib.AddParam(info, v);
        if (ret != PW.PWRET_OK) Auditar?.Invoke($"pgweblib: PW_iAddParam({info}) {PW.Nome(ret)}");
    }

    private async Task<Fim> ExecutarAsync(Contexto ctx)
    {
        var fim = new Fim();
        var relogio = Stopwatch.StartNew();
        var abortado = false;
        while (true)
        {
            if (ctx.Ct.IsCancellationRequested && !abortado)
            {
                abortado = true;
                try { _lib.PPAbort(); } catch { }
                ctx.Andamento?.Report(new AndamentoTef(FaseTef.Recado, ctx.ChargeId, ctx.Id, "Cancelamento pedido: aguardando o pinpad…"));
                relogio.Restart();
            }
            short ret;
            IReadOnlyList<PwGetData> pedidos;
            try { ret = _lib.ExecTransac(out pedidos); }
            catch (Exception ex)
            {
                Auditar?.Invoke($"pgweblib: PW_iExecTransac lançou em {ctx.ChargeId}: {ex.Message}");
                return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, "TEF falhou no meio da operação: confira no PayGo", posOcupado: true);
            }
            switch (ret)
            {
                case PW.PWRET_OK:
                    // PWRET_OK É a aprovação (spec): a transação terminou e foi autorizada. AUTRESPCODE
                    // e RESULTMSG são informativos e vão só para a auditoria: cada rede escreve o
                    // código aprovado do seu jeito ("00", "000", "0000" ou nada), e recusa chega
                    // pelo RETORNO (PWRET_FROMHOST), nunca por este campo.
                    LerResultados(fim);
                    fim.RequerConfirmacao = fim.Resultados.TryGetValue(PW.PWINFO_CNFREQ, out var c) && c.Trim() == "1";
                    Auditar?.Invoke($"pgweblib: {ctx.ChargeId} PWRET_OK; AUTRESPCODE={(fim.Resultados.TryGetValue(PW.PWINFO_AUTRESPCODE, out var rc) ? rc.Trim() : "-")}; CNFREQ={(c ?? "-").Trim()}; {Mensagem(fim, "sem RESULTMSG")}");
                    fim.Aprovada = true;
                    fim.Situacao = SituacaoTef.Pago;
                    fim.Codigo = CodigoTef.Pago;
                    return fim;
                case PW.PWRET_MOREDATA:
                    var a = await AtenderAsync(ctx, pedidos, fim).ConfigureAwait(false);
                    if (a is not null) return a;
                    relogio.Restart();
                    continue;
                case PW.PWRET_NOTHING:
                    if (abortado && relogio.ElapsedMilliseconds >= Math.Min(TempoMaxExecMs, 5_000))
                        return Encerrar(fim, SituacaoTef.Cancelado, CodigoTef.Cancelado, "cobrança cancelada pelo operador", desfeita: true, ler: true);
                    if (relogio.ElapsedMilliseconds >= TempoMaxExecMs)
                    {
                        try { _lib.PPAbort(); } catch { }
                        return Encerrar(fim, SituacaoTef.Timeout, CodigoTef.Timeout, "o TEF não respondeu a tempo: confira no PayGo", posOcupado: true, ler: true);
                    }
                    await Task.Delay(IntervaloPollMs).ConfigureAwait(false);
                    continue;
                case PW.PWRET_CANCEL:
                    // Depois de PW_iPPAbort a biblioteca encerra o fluxo com PWRET_CANCEL: foi o
                    // operador. Sem abort, foi cancelado no pinpad ou pela própria biblioteca.
                    LerResultados(fim);
                    return Encerrar(fim, SituacaoTef.Cancelado, CodigoTef.Cancelado, abortado ? "cobrança cancelada pelo operador" : Mensagem(fim, "operação cancelada"));
                case PW.PWRET_TIMEOUT:
                    LerResultados(fim);
                    return Encerrar(fim, SituacaoTef.Timeout, CodigoTef.Timeout, Mensagem(fim, "tempo esgotado no pinpad"));
                case PW.PWRET_HOSTCONNERR:
                case PW.PWRET_HOSTTIMEOUT:
                    LerResultados(fim);
                    return Encerrar(fim, SituacaoTef.Erro, CodigoTef.SemRede, Mensagem(fim, "sem comunicação com o host do TEF"));
                case PW.PWRET_NOMANDATORY:
                    LerResultados(fim);
                    return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, Mensagem(fim, "faltou parâmetro obrigatório para o TEF"));
                case PW.PWRET_NOTINST:
                    return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, MsgNaoInstalado);
                default:
                    LerResultados(fim);
                    if (PW.EhRecusaDoHost(ret))
                        return Encerrar(fim, SituacaoTef.Recusado, CodigoTef.Recusado, Mensagem(fim, "transação não autorizada"));
                    return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, Mensagem(fim, "TEF devolveu " + PW.Nome(ret)));
            }
        }
    }

    /// <summary>Atende os pedidos de PWRET_MOREDATA. Null = tudo capturado, voltar ao ExecTransac; senão o Fim que interrompe.</summary>
    private async Task<Fim?> AtenderAsync(Contexto ctx, IReadOnlyList<PwGetData> pedidos, Fim fim)
    {
        for (var i = 0; i < pedidos.Count; i++)
        {
            var p = pedidos[i];
            if (p.EhMenu || p.EhDigitado)
            {
                var valor = Predefinido(ctx, p);
                if (valor is null)
                {
                    var resposta = await PerguntarSeguroAsync(ctx, p).ConfigureAwait(false);
                    // A tela pode devolver o texto da opção ("RELATORIO") ou o valor em outra caixa
                    // ("cielo"): o que vai para a biblioteca é sempre o VALOR da opção.
                    valor = resposta is null ? null : (Casar(p, resposta) ?? resposta);
                }
                if (valor is null)
                    return Encerrar(fim, SituacaoTef.Cancelado, CodigoTef.Cancelado, "operação cancelada pelo operador", desfeita: true, ler: true);
                var ret = _lib.AddParam(p.Identificador, ArquivoIntpos.Ascii(valor));
                if (ret != PW.PWRET_OK)
                    return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, $"TEF não aceitou o dado {p.Identificador}: {PW.Nome(ret)}", ler: true);
                ctx.Conhecidos[p.Identificador] = valor;
                continue;
            }
            if (p.EhPinpad)
            {
                var r = await CapturarNoPinpadAsync(ctx, (ushort)i, p, fim).ConfigureAwait(false);
                if (r is not null) return r;
                continue;
            }
            return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, $"TEF pediu captura que o caixa não suporta (tipo {p.Tipo})", ler: true);
        }
        return null;
    }

    private async Task<Fim?> CapturarNoPinpadAsync(Contexto ctx, ushort indice, PwGetData p, Fim fim)
    {
        short ret;
        try
        {
            ret = p.Tipo switch
            {
                PW.PWDAT_CARDINF => _lib.PPGetCard(indice),
                PW.PWDAT_PPENTRY => _lib.PPGetData(indice),
                PW.PWDAT_PPENCPIN => _lib.PPGetPIN(indice),
                PW.PWDAT_CARDOFF => _lib.PPGoOnChip(indice),
                PW.PWDAT_CARDONL => _lib.PPFinishChip(indice),
                PW.PWDAT_PPCONF => _lib.PPConfirmData(indice),
                PW.PWDAT_PPDATAPOSCNF => _lib.PPPositiveConfirmation(indice),  // PW_iPPPositiveConfirmation (exemplo oficial)
                PW.PWDAT_PPREMCRD => _lib.PPRemoveCard(),
                PW.PWDAT_PPGENCMD => _lib.PPGenericCMD(indice),
                _ => PW.PWRET_INVCALL,
            };
        }
        catch (Exception ex)
        {
            Auditar?.Invoke($"pgweblib: PW_iPP* ({p.Tipo}) lançou: {ex.Message}");
            return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, "falha ao falar com o pinpad", ler: true);
        }
        if (ret is PW.PWRET_PPNOTFOUND or PW.PWRET_PPCOMERR or PW.PWRET_PINPADERR)
            return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, "pinpad não encontrado: confira o cabo e o PayGo", ler: true);
        if (ret != PW.PWRET_OK)
            return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, "pinpad recusou a captura: " + PW.Nome(ret), ler: true);

        var relogio = Stopwatch.StartNew();
        var abortado = false;
        while (true)
        {
            if (ctx.Ct.IsCancellationRequested && !abortado)
            {
                abortado = true;
                try { _lib.PPAbort(); } catch { }
                relogio.Restart();
            }
            short ev;
            string display;
            try { ev = _lib.PPEventLoop(out display); }
            catch (Exception ex)
            {
                Auditar?.Invoke("pgweblib: PW_iPPEventLoop lançou: " + ex.Message);
                return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, "falha ao falar com o pinpad", ler: true);
            }
            switch (ev)
            {
                case PW.PWRET_OK:
                case PW.PWRET_FALLBACK:
                    return null;
                case PW.PWRET_DISPLAY:
                    if (!string.IsNullOrWhiteSpace(display) && display != ctx.UltimoDisplay)
                    {
                        ctx.UltimoDisplay = display;
                        ctx.Andamento?.Report(new AndamentoTef(FaseTef.Recado, ctx.ChargeId, ctx.Id, display.Replace('\r', ' ').Trim()));
                    }
                    break;
                case PW.PWRET_NOTHING:
                    break;
                case PW.PWRET_CANCEL:
                    return Encerrar(fim, SituacaoTef.Cancelado, CodigoTef.Cancelado, abortado ? "cobrança cancelada pelo operador" : "operação cancelada no pinpad", ler: true);
                case PW.PWRET_TIMEOUT:
                    return Encerrar(fim, SituacaoTef.Timeout, CodigoTef.Timeout, "tempo esgotado no pinpad", ler: true);
                default:
                    return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, "pinpad devolveu " + PW.Nome(ev), ler: true);
            }
            var teto = abortado ? Math.Min(TempoMaxCapturaMs, 5_000) : TempoMaxCapturaMs;
            if (relogio.ElapsedMilliseconds >= teto)
            {
                if (!abortado) { try { _lib.PPAbort(); } catch { } }
                return Encerrar(fim, abortado ? SituacaoTef.Cancelado : SituacaoTef.Timeout, abortado ? CodigoTef.Cancelado : CodigoTef.Timeout,
                    abortado ? "cobrança cancelada pelo operador" : "o pinpad não respondeu a tempo", ler: true, posOcupado: !abortado);
            }
            await Task.Delay(IntervaloPollMs).ConfigureAwait(false);
        }
    }

    /// <summary>Valor que a automação já sabe para o dado pedido (o que mandou em AddParam, ou a rede pré-selecionada).</summary>
    private string? Predefinido(Contexto ctx, PwGetData p)
    {
        if (!ctx.Conhecidos.TryGetValue(p.Identificador, out var v) || string.IsNullOrWhiteSpace(v))
        {
            // Menu com uma opção só não precisa de operador.
            if (p.EhMenu && p.Opcoes is { Count: 1 }) return p.Opcoes[0].Valor;
            return null;
        }
        if (!p.EhMenu || p.Opcoes is null || p.Opcoes.Count == 0) return v;
        var casado = Casar(p, v);
        if (casado is null)
            Auditar?.Invoke($"pgweblib: '{v}' não está no menu {p.Identificador} ({string.Join("|", p.Opcoes.Select(o => o.Valor))}); perguntando à tela");
        return casado;
    }

    /// <summary>Opção do menu que corresponde ao texto dado (pelo valor ou pelo texto, sem caixa). Null = não está no menu.</summary>
    private static string? Casar(PwGetData p, string v)
    {
        if (!p.EhMenu || p.Opcoes is null || p.Opcoes.Count == 0) return null;
        var op = p.Opcoes.FirstOrDefault(o => string.Equals(o.Valor, v, StringComparison.OrdinalIgnoreCase))
              ?? p.Opcoes.FirstOrDefault(o => string.Equals(o.Texto.Trim(), v.Trim(), StringComparison.OrdinalIgnoreCase));
        return op?.Valor;
    }

    private async Task<string?> PerguntarSeguroAsync(Contexto ctx, PwGetData p)
    {
        if (Perguntar is null) { Auditar?.Invoke($"pgweblib: sem quem responder o dado {p.Identificador} ({p.Prompt})"); return null; }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
        cts.CancelAfter(TempoPerguntaMs);
        try { return await Perguntar(p, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Auditar?.Invoke("pgweblib: Perguntar lançou: " + ex.Message); return null; }
    }

    private Fim Encerrar(Fim fim, SituacaoTef s, string codigo, string motivo, bool desfeita = false, bool posOcupado = false, bool ler = false)
    {
        if (ler) LerResultados(fim);
        fim.Aprovada = false;
        fim.Situacao = s;
        fim.Codigo = codigo;
        fim.Motivo = motivo;
        fim.Desfeita = desfeita;
        fim.PosOcupado = posOcupado;
        fim.RequerConfirmacao = fim.Resultados.TryGetValue(PW.PWINFO_CNFREQ, out var c) && c.Trim() == "1";
        if (!fim.RequerConfirmacao && s != SituacaoTef.Recusado && AprovadaDefinitiva(fim))
        {
            // O host aprovou e CNFREQ=0: a transação é DEFINITIVA, não existe REV que a desfaça.
            // A saída depois disso (operador cancelou em RETIRE O CARTAO, pinpad deu timeout)
            // não muda o dinheiro. Gravar 'cancelado' aqui diria à tela "o cliente não foi
            // cobrado" com o cliente cobrado. Segue como paga; a saída fica só na auditoria.
            fim.Nota = motivo;
            fim.Aprovada = true;
            fim.Situacao = SituacaoTef.Pago;
            fim.Codigo = CodigoTef.Pago;
            fim.Motivo = null;
            fim.Desfeita = false;
            fim.PosOcupado = false;
        }
        return fim;
    }

    /// <summary>Prova de aprovação lida da biblioteca: AUTRESPCODE aprovado e REQNUM (ou NSU) presentes.</summary>
    private static bool AprovadaDefinitiva(Fim fim)
        => fim.Resultados.TryGetValue(PW.PWINFO_AUTRESPCODE, out var rc) && CodigoAprovado(rc)
           && (fim.Resultados.TryGetValue(PW.PWINFO_REQNUM, out var req) && !string.IsNullOrWhiteSpace(req)
               || fim.Resultados.TryGetValue(PW.PWINFO_AUTEXTREF, out var nsu) && !string.IsNullOrWhiteSpace(nsu));

    /// <summary>AUTRESPCODE de aprovação: só zeros, em qualquer largura ("0", "00", "000", "0000"); cada rede escreve de um jeito.</summary>
    private static bool CodigoAprovado(string? rc)
    {
        var v = rc?.Trim();
        return !string.IsNullOrEmpty(v) && v.All(ch => ch == '0');
    }

    private static string Mensagem(Fim fim, string padrao)
        => fim.Resultados.TryGetValue(PW.PWINFO_RESULTMSG, out var m) && !string.IsNullOrWhiteSpace(m) ? m.Replace('\r', ' ').Trim() : padrao;

    /// <summary>Tudo que a automação lê ao fim. CARDFULLPAN (193) NÃO está aqui, de propósito.</summary>
    private static readonly ushort[] InfosLidas =
    {
        PW.PWINFO_CNFREQ, PW.PWINFO_REQNUM, PW.PWINFO_AUTLOCREF, PW.PWINFO_AUTEXTREF, PW.PWINFO_VIRTMERCH, PW.PWINFO_AUTHSYST,
        PW.PWINFO_AUTHCODE, PW.PWINFO_AUTRESPCODE, PW.PWINFO_AUTDATETIME, PW.PWINFO_CARDNAME, PW.PWINFO_CARDNAMESTD,
        PW.PWINFO_CARDPARCPAN, PW.PWINFO_CARDTYPE, PW.PWINFO_CARDENTMODE, PW.PWINFO_FINTYPE, PW.PWINFO_INSTALLMENTS,
        PW.PWINFO_TOTAMNT, PW.PWINFO_DUEAMNT, PW.PWINFO_RCPTFULL, PW.PWINFO_RCPTMERCH, PW.PWINFO_RCPTCHOLDER,
        PW.PWINFO_RCPTCHSHORT, PW.PWINFO_RCPTPRN, PW.PWINFO_RESULTMSG, PW.PWINFO_IDLEPROCTIME,
    };

    private void LerResultados(Fim fim)
    {
        if (fim.Resultados.Count > 0) return;
        foreach (var info in InfosLidas)
        {
            var v = Ler(info);
            if (v is not null) fim.Resultados[info] = v;
        }
        // A biblioteca também informa o horário ao fim de uma transação: aproveita.
        if (fim.Resultados.TryGetValue(PW.PWINFO_IDLEPROCTIME, out var idle)) AgendarIdle(idle, "transação");
    }

    private string? Ler(ushort info)
    {
        try
        {
            var ret = _lib.GetResult(info, out var v);
            return ret == PW.PWRET_OK && !string.IsNullOrEmpty(v) ? v : null;
        }
        catch { return null; }
    }

    private Pendencia? LerPendenciaDaLib()
    {
        var req = Ler(PW.PWINFO_PNDREQNUM);
        if (string.IsNullOrWhiteSpace(req)) return null;
        return new Pendencia(req!, Ler(PW.PWINFO_PNDAUTLOCREF) ?? "", Ler(PW.PWINFO_PNDAUTEXTREF) ?? "",
            Ler(PW.PWINFO_PNDVIRTMERCH) ?? "", Ler(PW.PWINFO_PNDAUTHSYST) ?? "");
    }

    // ------------------------------------------------------------------ confirmação

    private static (string Req, string Loc, string Ext, string Vm, string As) Tupla(RespostaPayGo? r)
        => (r?.CodigoControle ?? "", r?.Campos.GetValueOrDefault("950-000") ?? "", r?.Nsu ?? "",
            r?.Campos.GetValueOrDefault("951-000") ?? "", r?.Rede ?? "");

    private short ConfirmacaoCrua(uint resultado, string req, string loc, string ext, string vm, string authSyst)
    {
        try
        {
            var ret = _lib.Confirmation(resultado, req, loc, ext, vm, authSyst);
            if (ret == PW.PWRET_OK) { try { _lib.WaitConfirmation(); } catch (Exception ex) { Auditar?.Invoke("pgweblib: PW_iWaitConfirmation lançou: " + ex.Message); } }
            return ret;
        }
        catch (Exception ex)
        {
            Auditar?.Invoke("pgweblib: PW_iConfirmation lançou: " + ex.Message);
            return PW.PWRET_WRITERR;
        }
    }

    /// <summary>CNF_AUTO. Ok = acusado e linha `depois`; SemAck = 'cnf_sem_ack' para reenvio; Desconhecida = INVALIDTRN, linha 'orfa'.</summary>
    private Ack Confirmar(TransacaoPayGo tx, string depois)
    {
        var (req, loc, ext, vm, aut) = Tupla(tx.Resposta);
        var ret = ConfirmacaoCrua(PW.PWCNF_CNF_AUTO, req, loc, ext, vm, aut);
        if (ret == PW.PWRET_OK)
        {
            GuardarSeguro(tx with { Situacao = depois });
            Auditar?.Invoke($"pgweblib: CNF {tx.ChargeId} REQNUM {req} {PW.Nome(ret)} -> {depois}");
            return Ack.Ok;
        }
        if (ret == PW.PWRET_INVALIDTRN && NaoReconhecida(tx, "CNF", req)) return Ack.Desconhecida;
        GuardarSeguro(tx with { Situacao = "cnf_sem_ack", Motivo = "confirmação sem ack: " + PW.Nome(ret) });
        _reenvios.RemoveAll(x => x.Tx.ChargeId == tx.ChargeId);
        _reenvios.Add((tx, PW.PWCNF_CNF_AUTO, depois));
        Auditar?.Invoke($"pgweblib: CNF {tx.ChargeId} sem ack ({PW.Nome(ret)}); reenvio agendado");
        return Ack.SemAck;
    }

    /// <summary>REV_*. Sempre fala com a biblioteca: quem chama só desfaz transação com CNFREQ=1 (sem confirmação pendente não existe REV).</summary>
    private Ack Desfazer(TransacaoPayGo tx, uint resultado, string depois)
    {
        var (req, loc, ext, vm, aut) = Tupla(tx.Resposta);
        var ret = ConfirmacaoCrua(resultado, req, loc, ext, vm, aut);
        if (ret == PW.PWRET_OK)
        {
            GuardarSeguro(tx with { Situacao = depois });
            Auditar?.Invoke($"pgweblib: REV {tx.ChargeId} REQNUM {req} ({resultado}) {PW.Nome(ret)} -> {depois}");
            return Ack.Ok;
        }
        if (ret == PW.PWRET_INVALIDTRN && NaoReconhecida(tx, "REV", req)) return Ack.Desconhecida;
        GuardarSeguro(tx with { Situacao = "ncn_sem_ack", Motivo = "desfazimento sem ack: " + PW.Nome(ret) });
        _reenvios.RemoveAll(x => x.Tx.ChargeId == tx.ChargeId);
        _reenvios.Add((tx, resultado, depois));
        Auditar?.Invoke($"pgweblib: REV {tx.ChargeId} sem ack ({PW.Nome(ret)}); reenvio agendado");
        return Ack.SemAck;
    }

    /// <summary>
    /// PWRET_INVALIDTRN só diz que a biblioteca não tem ESTA transação pendente; não diz se
    /// ela foi confirmada ou desfeita. Cruza com PWINFO_PNDREQNUM: se a biblioteca ainda
    /// descreve este REQNUM é contradição (false: trata como sem ack e reenvia); senão a linha
    /// vira 'orfa' e alguém confere no relatório. Nunca 'pago' nem 'desfeita' sem ack.
    /// </summary>
    private bool NaoReconhecida(TransacaoPayGo tx, string oque, string req)
    {
        var pnd = Ler(PW.PWINFO_PNDREQNUM)?.Trim();
        if (!string.IsNullOrEmpty(pnd) && pnd == req) return false;
        GuardarSeguro(tx with { Situacao = "orfa", Motivo = MsgNaoReconhece(tx.Resposta?.Nsu) });
        _reenvios.RemoveAll(x => x.Tx.ChargeId == tx.ChargeId);
        Auditar?.Invoke($"pgweblib: {oque} {tx.ChargeId} REQNUM {req} {PW.Nome(PW.PWRET_INVALIDTRN)}; pendência da biblioteca: {(string.IsNullOrEmpty(pnd) ? "nenhuma" : pnd)} -> orfa");
        return true;
    }

    /// <summary>Terminou sem aprovação mas CNFREQ=1 (operador interrompeu no meio): desfaz. Grava a linha antes.</summary>
    private Task DesfazerSeRequeridoAsync(Contexto ctx, Fim fim, RespostaPayGo r)
    {
        if (!fim.RequerConfirmacao) return Task.CompletedTask;
        var tx = new TransacaoPayGo(ctx.ChargeId, ctx.Id, ctx.Tipo, ctx.ValorCent, ctx.Parcelas, "aprovada", r, fim.Motivo);
        GuardarSeguro(tx);
        var motivo = fim.Situacao switch
        {
            SituacaoTef.Cancelado => PW.PWCNF_REV_ABORT,
            SituacaoTef.Timeout => PW.PWCNF_REV_AUTO_ABORT,
            _ => PW.PWCNF_REV_OTHER_AUT,
        };
        if (Desfazer(tx with { Motivo = (fim.Motivo ?? "") + " (REV)" }, motivo, "desfeita") == Ack.Desconhecida)
        {
            // A biblioteca não reconhece o REV: pode estar confirmada e cobrada. Órfã com aviso.
            fim.Situacao = SituacaoTef.Erro;
            fim.Codigo = CodigoTef.Plataforma;
            fim.Motivo = MsgNaoReconhece(r.Nsu);
            fim.PosOcupado = true;
            fim.Desfeita = false;
            return Task.CompletedTask;
        }
        fim.Desfeita = true;
        return Task.CompletedTask;
    }

    private void Reenviar()
    {
        if (_reenvios.Count == 0) return;
        foreach (var (tx, resultado, depois) in _reenvios.ToList())
        {
            var (req, loc, ext, vm, aut) = Tupla(tx.Resposta);
            var ret = ConfirmacaoCrua(resultado, req, loc, ext, vm, aut);
            if (ret == PW.PWRET_OK)
            {
                GuardarSeguro(tx with { Situacao = depois });
                _reenvios.RemoveAll(x => x.Tx.ChargeId == tx.ChargeId);
                Auditar?.Invoke($"pgweblib: reenvio de {tx.ChargeId} ({resultado}) acusado -> {depois}");
            }
            else if (ret == PW.PWRET_INVALIDTRN)
                NaoReconhecida(tx, "reenvio de " + (resultado == PW.PWCNF_CNF_AUTO ? "CNF" : "REV"), req);   // tira da fila e grava 'orfa'
        }
    }

    /// <summary>
    /// Desfecho de transação ÓRFÃ (dinheiro pode ter entrado sem venda): Erro com o aviso de
    /// conferir na maquininha, nunca Desfeita. `gravar` = false quando a linha 'orfa' já foi gravada.
    /// </summary>
    private DesfechoTef Orfa(TransacaoPayGo tx, CartaoTef? cartao, string motivo, bool gravar = true)
    {
        if (gravar) GuardarSeguro(tx with { Situacao = "orfa", Motivo = motivo });
        Auditar?.Invoke($"pgweblib: {tx.ChargeId} órfã: {motivo}");
        return new DesfechoTef(SituacaoTef.Erro, tx.Identificacao, tx.ChargeId, cartao, motivo, true) { Codigo = CodigoTef.Plataforma };
    }

    /// <summary>Estado final ao confirmar: só venda vira `pago`.</summary>
    private static string Final(TransacaoPayGo tx)
        => tx.EhCancelamento ? "estornado"
         : tx.ChargeId.Contains("-adm-", StringComparison.Ordinal) || tx.ChargeId.Contains("-rep-", StringComparison.Ordinal) || tx.ChargeId.Contains("-inst-", StringComparison.Ordinal) ? "adm"
         : "pago";

    // ------------------------------------------------------------------ resposta no formato intpos

    /// <summary>
    /// Monta uma <see cref="RespostaPayGo"/> (formato intpos, o que `tef_transacao.resposta_txt`,
    /// o religamento e a reimpressão já entendem) a partir dos PWINFO_* lidos. Mapa:
    /// AUTHSYST→010 (rede), AUTEXTREF→012 (NSU), AUTHCODE→013, REQNUM→027 (código de controle),
    /// AUTDATETIME→022/023, CARDNAMESTD→040, CARDNAME→748, CARDPARCPAN→740, CNFREQ→729 (1→2, 0→1),
    /// RCPTPRN→737, RCPTCHOLDER→713, RCPTMERCH→715, RCPTCHSHORT→711, RCPTFULL→029 (só sem as
    /// diferenciadas). Extras da PGWebLib em 950 (AUTLOCREF), 951 (VIRTMERCH), 952 (AUTDATETIME cru).
    /// </summary>
    public static RespostaPayGo RespostaDaLib(string cmd, string id, long valorCent, TipoTef tipo, int parcelas, bool aprovada,
        IReadOnlyDictionary<ushort, string> r)
    {
        var c = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["000-000"] = cmd,
            ["001-000"] = id,
            ["009-000"] = aprovada ? "0" : "1",
        };
        void Def(string k, ushort info) { if (r.TryGetValue(info, out var v) && !string.IsNullOrWhiteSpace(v)) c[k] = v.Replace('\r', ' ').Trim(); }
        var tot = r.TryGetValue(PW.PWINFO_TOTAMNT, out var t) && long.TryParse(t.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var tc) ? tc : valorCent;
        if (tot > 0) c["003-000"] = tot.ToString(CultureInfo.InvariantCulture);
        c["004-000"] = "0";
        Def("010-000", PW.PWINFO_AUTHSYST);
        Def("012-000", PW.PWINFO_AUTEXTREF);
        Def("013-000", PW.PWINFO_AUTHCODE);
        Def("027-000", PW.PWINFO_REQNUM);
        Def("030-000", PW.PWINFO_RESULTMSG);
        if (!c.ContainsKey("030-000")) c["030-000"] = aprovada ? "TRANSACAO AUTORIZADA" : "TRANSACAO NAO AUTORIZADA";
        Def("040-000", PW.PWINFO_CARDNAMESTD);
        Def("748-000", PW.PWINFO_CARDNAME);
        if (!c.ContainsKey("040-000") && c.TryGetValue("748-000", out var nome)) c["040-000"] = nome;
        Def("740-000", PW.PWINFO_CARDPARCPAN);
        Def("950-000", PW.PWINFO_AUTLOCREF);
        Def("951-000", PW.PWINFO_VIRTMERCH);
        Def("952-000", PW.PWINFO_AUTDATETIME);
        if (r.TryGetValue(PW.PWINFO_AUTDATETIME, out var dt) && dt.Trim().Length >= 14)
        {
            var s = dt.Trim();
            c["022-000"] = s.Substring(6, 2) + s.Substring(4, 2) + s.Substring(0, 4);   // DDMMYYYY
            c["023-000"] = s.Substring(8, 6);                                              // hhmmss
        }
        c["731-000"] = tipo switch { TipoTef.Credito => "1", TipoTef.Debito => "2", TipoTef.Voucher => "3", _ => "0" };
        c["732-000"] = parcelas > 1 ? "3" : "1";
        if (parcelas > 1) c["018-000"] = parcelas.ToString(CultureInfo.InvariantCulture);
        c["729-000"] = r.TryGetValue(PW.PWINFO_CNFREQ, out var cnf) && cnf.Trim() == "1" ? "2" : "1";
        if (r.TryGetValue(PW.PWINFO_RCPTPRN, out var prn) && int.TryParse(prn.Trim(), out var p) && p is >= 0 and <= 3)
            c["737-000"] = p.ToString(CultureInfo.InvariantCulture);
        Vias(c, "712", "713", r.GetValueOrDefault(PW.PWINFO_RCPTCHOLDER));
        Vias(c, "714", "715", r.GetValueOrDefault(PW.PWINFO_RCPTMERCH));
        Vias(c, "710", "711", r.GetValueOrDefault(PW.PWINFO_RCPTCHSHORT));
        if (!c.ContainsKey("712-000") && !c.ContainsKey("714-000")) Vias(c, "028", "029", r.GetValueOrDefault(PW.PWINFO_RCPTFULL));
        if (!c.ContainsKey("737-000"))
            c["737-000"] = c.ContainsKey("712-000") && c.ContainsKey("714-000") ? "3" : c.ContainsKey("712-000") ? "1" : c.ContainsKey("714-000") ? "2" : "0";
        return RespostaPayGo.Analisar(ArquivoIntpos.Serializar(c));
    }

    /// <summary>Comprovante da PGWebLib: 40 colunas, linhas separadas por 0Dh (tolera CRLF/LF).</summary>
    private static void Vias(Dictionary<string, string> c, string contador, string prefixo, string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return;
        var linhas = texto.Replace("\r\n", "\r").Replace('\n', '\r').Split('\r').Select(l => l.TrimEnd()).ToList();
        while (linhas.Count > 0 && linhas[0].Trim().Length == 0) linhas.RemoveAt(0);
        while (linhas.Count > 0 && linhas[^1].Trim().Length == 0) linhas.RemoveAt(linhas.Count - 1);
        if (linhas.Count == 0) return;
        c[contador + "-000"] = linhas.Count.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < linhas.Count; i++) c[$"{prefixo}-{i + 1:000}"] = "\"" + linhas[i] + "\"";
    }

    /// <summary>TRNORIGDATE (DDMMAA) e TRNORIGTIME (hhmmss) a partir da resposta original (952 cru ou 022/023).</summary>
    private static (string? Data, string? Hora) DataHoraOriginal(RespostaPayGo r0)
    {
        var cru = r0.Campos.GetValueOrDefault("952-000")?.Trim();
        if (cru is { Length: >= 14 }) return (cru.Substring(6, 2) + cru.Substring(4, 2) + cru.Substring(2, 2), cru.Substring(8, 6));
        var d = r0.Data;
        return (d is { Length: 8 } ? d.Substring(0, 4) + d.Substring(6, 2) : null, r0.Hora);
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
            Valor: r.ValorCent is { } cc ? cc / 100m : null);
    }

    private async Task<bool> ImprimirSeguroAsync(TransacaoPayGo tx)
    {
        if (ImprimirComprovante is null || tx.Resposta is null || !tx.Resposta.TemVias) return true;
        try { return await ImprimirComprovante(tx).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Auditar?.Invoke($"pgweblib: impressão do comprovante {tx.ChargeId} lançou: {ex.Message}");
            return false;
        }
    }

    private bool GuardarSeguro(TransacaoPayGo t)
    {
        try { return Guardar(t); }
        catch { return false; }
    }

    private bool ConhecidaSegura(string reqNum)
    {
        try { return ConhecidaConfirmada?.Invoke(reqNum) == true; }
        catch { return false; }
    }

    private static DesfechoTef Falha(SituacaoTef s, string chargeId, string codigo, string motivo, bool posOcupado = false)
        => new(s, null, chargeId, null, motivo, posOcupado) { Codigo = codigo };
}
