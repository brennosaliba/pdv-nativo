namespace Pdv.Nucleo;

// TEF PayGo Windows pela BIBLIOTECA PGWebLib (terceiro provedor; os outros dois são a troca
// de arquivos em PayGo.cs e o WebService em ControlPay.cs).
//
// Este arquivo é só o CONTRATO: a superfície da DLL em C# idiomático (`IPGWebLib`), as
// constantes com os números da especificação pública (paygodev.readme.io, levantada em
// 05/09/2026) e o `PwGetData` gerenciado. Quem fala com a DLL de verdade é
// `PGWebLibNativa` (P/Invoke, assinaturas copiadas do exemplo oficial C# da PayGo); quem fala com a
// bateria é `FakePGWebLib` em Pdv.Testes. O provedor (`ProvedorPGWebLib`) só conhece a
// interface, e é por isso que a bateria roda sem a DLL instalada.

/// <summary>Constantes da PGWebLib com os nomes do header C e os números da especificação pública.</summary>
public static class PW
{
    // ── retornos (PWRET_*) ───────────────────────────────────────────────
    public const short PWRET_OK = 0;
    public const short PWRET_INVPARAM = -2499;
    public const short PWRET_NOTINST = -2498;
    public const short PWRET_MOREDATA = -2497;
    public const short PWRET_NODATA = -2496;
    public const short PWRET_DISPLAY = -2495;
    public const short PWRET_INVCALL = -2494;
    public const short PWRET_NOTHING = -2493;
    public const short PWRET_BUFOVFLW = -2492;
    public const short PWRET_CANCEL = -2491;
    public const short PWRET_TIMEOUT = -2490;
    public const short PWRET_PPNOTFOUND = -2489;
    public const short PWRET_TRNNOTINIT = -2488;
    public const short PWRET_DLLNOTINIT = -2487;
    public const short PWRET_FALLBACK = -2486;
    public const short PWRET_WRITERR = -2485;
    public const short PWRET_PPCOMERR = -2484;
    public const short PWRET_NOMANDATORY = -2483;
    public const short PWRET_INVALIDTRN = -2482;
    /// <summary>Faixa "recusado pelo host" (-2599..-2596): a transação foi até o autorizador e voltou negada.</summary>
    public const short PWRET_FROMHOST_INICIO = -2599;
    public const short PWRET_FROMHOST_FIM = -2596;
    public const short PWRET_HOSTTIMEOUT = -2585;
    public const short PWRET_HOSTCONNERR = -2583;
    public const short PWRET_PINPADERR = -2580;

    // ── operações (PWOPER_*) ─────────────────────────────────────────────
    public const byte PWOPER_INSTALL = 1;
    public const byte PWOPER_PARAMUPD = 2;
    public const byte PWOPER_REPRINT = 16;
    public const byte PWOPER_RPTTRUNC = 17;
    public const byte PWOPER_RPTDETAIL = 18;
    public const byte PWOPER_ADMIN = 32;
    public const byte PWOPER_SALE = 33;
    public const byte PWOPER_SALEVOID = 34;
    public const byte PWOPER_PREPAID = 35;
    public const byte PWOPER_VOID = 57;
    public const byte PWOPER_VERSION = 252;
    public const byte PWOPER_CONFIG = 253;
    public const byte PWOPER_MAINTENANCE = 254;

    // ── informações (PWINFO_*): entrada ──────────────────────────────────
    public const ushort PWINFO_AUTNAME = 21;
    public const ushort PWINFO_AUTVER = 22;
    public const ushort PWINFO_AUTDEV = 23;
    public const ushort PWINFO_AUTCAP = 36;
    public const ushort PWINFO_TOTAMNT = 37;
    public const ushort PWINFO_CURRENCY = 38;
    public const ushort PWINFO_CURREXP = 39;
    public const ushort PWINFO_FISCALREF = 40;
    public const ushort PWINFO_CARDTYPE = 41;
    public const ushort PWINFO_AUTHSYST = 53;
    public const ushort PWINFO_VIRTMERCH = 54;
    public const ushort PWINFO_FINTYPE = 59;
    public const ushort PWINFO_INSTALLMENTS = 60;
    public const ushort PWINFO_PAYMNTTYPE = 7969;
    public const ushort PWINFO_USINGPINPAD = 32513;
    public const ushort PWINFO_PPCOMMPORT = 32514;
    // cancelamento (PWOPER_SALEVOID)
    public const ushort PWINFO_TRNORIGDATE = 87;
    public const ushort PWINFO_TRNORIGNSU = 88;
    public const ushort PWINFO_TRNORIGAMNT = 96;
    public const ushort PWINFO_TRNORIGAUTH = 98;
    public const ushort PWINFO_TRNORIGREQNUM = 114;
    public const ushort PWINFO_TRNORIGTIME = 115;

    // ── informações (PWINFO_*): confirmação e pendência ──────────────────
    public const ushort PWINFO_REQNUM = 50;
    public const ushort PWINFO_CNFREQ = 67;
    public const ushort PWINFO_AUTLOCREF = 68;
    public const ushort PWINFO_AUTEXTREF = 69;
    public const ushort PWINFO_PNDAUTHSYST = 32517;
    public const ushort PWINFO_PNDVIRTMERCH = 32518;
    public const ushort PWINFO_PNDREQNUM = 32519;
    public const ushort PWINFO_PNDAUTLOCREF = 32520;
    public const ushort PWINFO_PNDAUTEXTREF = 32521;

    // ── informações (PWINFO_*): saída ────────────────────────────────────
    public const ushort PWINFO_RESULTMSG = 66;
    public const ushort PWINFO_AUTHCODE = 70;
    public const ushort PWINFO_AUTRESPCODE = 71;
    public const ushort PWINFO_AUTDATETIME = 72;
    public const ushort PWINFO_CARDNAME = 75;
    public const ushort PWINFO_RCPTFULL = 82;
    public const ushort PWINFO_RCPTMERCH = 83;
    public const ushort PWINFO_RCPTCHOLDER = 84;
    public const ushort PWINFO_RCPTCHSHORT = 85;
    public const ushort PWINFO_CARDENTMODE = 192;
    /// <summary>PAN completo. NUNCA lido pela automação (PCI): existe aqui só para o teste provar que não é lido.</summary>
    public const ushort PWINFO_CARDFULLPAN = 193;
    public const ushort PWINFO_CARDNAMESTD = 196;
    public const ushort PWINFO_CARDPARCPAN = 200;
    public const ushort PWINFO_RCPTPRN = 244;
    public const ushort PWINFO_DUEAMNT = 0xBF06;
    /// <summary>Tempo do último PW_iIdleProc, formato YYMMDDhhmmss (Enums.cs oficial).</summary>
    public const ushort PWINFO_IDLEPROCTIME = 32516;   // Enums.cs oficial (YYMMDDhhmmss)

    // ── tipos de captura (PWDAT_*) ───────────────────────────────────────
    public const ushort PWDAT_MENU = 1;
    public const ushort PWDAT_TYPED = 2;
    public const ushort PWDAT_CARDINF = 3;
    public const ushort PWDAT_PPENTRY = 5;
    public const ushort PWDAT_PPENCPIN = 6;
    public const ushort PWDAT_CARDOFF = 9;
    public const ushort PWDAT_CARDONL = 10;
    public const ushort PWDAT_PPCONF = 11;
    public const ushort PWDAT_BARCODE = 12;
    public const ushort PWDAT_PPREMCRD = 13;
    public const ushort PWDAT_PPGENCMD = 14;
    public const ushort PWDAT_PPDATAPOSCNF = 16;
    /// <summary>Mensagem para o display da automação (Enums.cs oficial).</summary>
    public const ushort PWDAT_DSPCHECKOUT = 18;
    /// <summary>QR Code para o display da automação (Enums.cs oficial). PW_iPPAbort cancela.</summary>
    public const ushort PWDAT_DSPQRCODE = 20;
    public const ushort PWDAT_USERAUTH = 17;

    // ── confirmação (PWCNF_*) ────────────────────────────────────────────
    public const uint PWCNF_CNF_AUTO = 289;
    public const uint PWCNF_CNF_MANU_AUT = 12833;
    public const uint PWCNF_REV_MANU_AUT = 12849;
    public const uint PWCNF_REV_PRN_AUT = 78129;
    public const uint PWCNF_REV_DISP_AUT = 143665;
    public const uint PWCNF_REV_COMM_AUT = 209201;
    public const uint PWCNF_REV_AUTO_ABORT = 262449;
    public const uint PWCNF_REV_ABORT = 274737;
    public const uint PWCNF_REV_OTHER_AUT = 471345;
    public const uint PWCNF_REV_PWR_AUT = 536881;
    public const uint PWCNF_REV_FISC_AUT = 602417;

    // ── bits de PWINFO_AUTCAP ────────────────────────────────────────────
    public const int CAP_TROCO = 1;
    public const int CAP_DESCONTO = 2;
    public const int CAP_VALOR_FIXO = 4;
    public const int CAP_VIAS_DIFERENCIADAS = 8;
    public const int CAP_VIA_REDUZIDA = 16;
    public const int CAP_SALDO_VOUCHER = 32;
    public const int CAP_REMOVER_CARTAO = 64;
    public const int CAP_MSG_CHECKOUT = 128;
    public const int CAP_QR = 256;
    public const int CAP_DCC = 512;

    // ── valores de campo ─────────────────────────────────────────────────
    public const string PAYMNTTYPE_CARTAO = "1";
    public const string PAYMNTTYPE_DINHEIRO = "2";
    public const string PAYMNTTYPE_CHEQUE = "4";
    public const string PAYMNTTYPE_CARTEIRA_DIGITAL = "8";
    public const string FINTYPE_AVISTA = "1";
    public const string FINTYPE_PARCELADO_EMISSOR = "2";
    public const string FINTYPE_PARCELADO_LOJA = "4";
    public const string CARDTYPE_CREDITO = "1";
    public const string CARDTYPE_DEBITO = "2";
    public const string CARDTYPE_VOUCHER = "4";
    public const string CARDTYPE_PRIVATE_LABEL = "8";
    public const string CARDTYPE_FROTA = "16";
    public const string CARDTYPE_OUTROS = "128";

    public static bool EhRecusaDoHost(short ret) => ret is >= PWRET_FROMHOST_INICIO and <= PWRET_FROMHOST_FIM;

    /// <summary>Nome legível do retorno para auditoria ("PWRET_CANCEL (-2491)").</summary>
    public static string Nome(short ret) => ret switch
    {
        PWRET_OK => "PWRET_OK",
        PWRET_INVPARAM => "PWRET_INVPARAM",
        PWRET_NOTINST => "PWRET_NOTINST",
        PWRET_MOREDATA => "PWRET_MOREDATA",
        PWRET_NODATA => "PWRET_NODATA",
        PWRET_DISPLAY => "PWRET_DISPLAY",
        PWRET_INVCALL => "PWRET_INVCALL",
        PWRET_NOTHING => "PWRET_NOTHING",
        PWRET_BUFOVFLW => "PWRET_BUFOVFLW",
        PWRET_CANCEL => "PWRET_CANCEL",
        PWRET_TIMEOUT => "PWRET_TIMEOUT",
        PWRET_PPNOTFOUND => "PWRET_PPNOTFOUND",
        PWRET_TRNNOTINIT => "PWRET_TRNNOTINIT",
        PWRET_DLLNOTINIT => "PWRET_DLLNOTINIT",
        PWRET_FALLBACK => "PWRET_FALLBACK",
        PWRET_WRITERR => "PWRET_WRITERR",
        PWRET_PPCOMERR => "PWRET_PPCOMERR",
        PWRET_NOMANDATORY => "PWRET_NOMANDATORY",
        PWRET_INVALIDTRN => "PWRET_INVALIDTRN",
        PWRET_HOSTTIMEOUT => "PWRET_HOSTTIMEOUT",
        PWRET_HOSTCONNERR => "PWRET_HOSTCONNERR",
        PWRET_PINPADERR => "PWRET_PINPADERR",
        _ when EhRecusaDoHost(ret) => "PWRET_FROMHOST",
        _ => "PWRET_?",
    } + $" ({ret})";
}

/// <summary>Uma opção de menu (PWDAT_MENU): o texto que o operador vê e o valor devolvido em PW_iAddParam.</summary>
public sealed record PwOpcaoMenu(string Texto, string Valor);

/// <summary>
/// `PW_GetData` gerenciado: um dado que a biblioteca pede à automação (PWRET_MOREDATA).
/// Os campos nativos (PW_GetData) vivem em PGWebLibNativa; este record carrega só o que
/// o provedor usa para decidir (tipo, identificador, prompt, opções, tamanhos).
/// </summary>
/// <param name="Tipo">Uma das constantes PWDAT_*.</param>
/// <param name="Identificador">PWINFO_* do dado pedido; é ele que vai em PW_iAddParam(id, valor).</param>
public sealed record PwGetData(ushort Tipo, ushort Identificador, string Prompt,
    IReadOnlyList<PwOpcaoMenu>? Opcoes = null, uint TamanhoMinimo = 0, uint TamanhoMaximo = 0,
    string? Mascara = null, string? ValorInicial = null, bool Ocultar = false, bool AceitaNulo = false)
{
    public bool EhMenu => Tipo == PW.PWDAT_MENU;
    public bool EhDigitado => Tipo is PW.PWDAT_TYPED or PW.PWDAT_BARCODE or PW.PWDAT_USERAUTH;
    public bool EhPinpad => Tipo is PW.PWDAT_CARDINF or PW.PWDAT_PPENTRY or PW.PWDAT_PPENCPIN or PW.PWDAT_CARDOFF
        or PW.PWDAT_CARDONL or PW.PWDAT_PPCONF or PW.PWDAT_PPREMCRD or PW.PWDAT_PPGENCMD or PW.PWDAT_PPDATAPOSCNF;
}

/// <summary>Uma operação devolvida por PW_iGetOperations (menu administrativo).</summary>
public sealed record PwOperacao(byte Codigo, string Texto, string Valor);

/// <summary>
/// Superfície da PGWebLib em C#. Cada método devolve o PWRET_* cru; quem interpreta é o
/// provedor. Strings são ASCII 0x20-0x7E terminadas em NUL do lado nativo; do lado gerenciado
/// são strings normais. Uma implementação por processo (a DLL guarda estado global).
/// </summary>
public interface IPGWebLib
{
    /// <summary>PW_iInit(pszWorkingDir). PWRET_OK, ou PWRET_INVCALL se já iniciada.</summary>
    short Init(string diretorioTrabalho);

    /// <summary>PW_iNewTransac(bOper). PWRET_OK, PWRET_DLLNOTINIT, PWRET_NOTINST (precisa PWOPER_INSTALL).</summary>
    short NewTransac(byte operacao);

    /// <summary>PW_iAddParam(wParam, pszValue). PWRET_OK, PWRET_INVPARAM, PWRET_TRNNOTINIT.</summary>
    short AddParam(ushort info, string valor);

    /// <summary>
    /// PW_iExecTransac. PWRET_OK (concluída), PWRET_MOREDATA (`pedidos` preenchidos: capturar cada
    /// um e chamar de novo), PWRET_NOTHING (chamar de novo), PWRET_NOMANDATORY, ou erro.
    /// </summary>
    short ExecTransac(out IReadOnlyList<PwGetData> pedidos);

    /// <summary>PW_iGetResult(iInfo). PWRET_OK com o valor, PWRET_NODATA (valor vazio), PWRET_BUFOVFLW.</summary>
    short GetResult(ushort info, out string valor);

    /// <summary>PW_iConfirmation(ulResult, ...). PWRET_INVALIDTRN se já confirmada.</summary>
    short Confirmation(uint resultado, string reqNum, string locRef, string extRef, string virtMerch, string authSyst);

    /// <summary>PW_iWaitConfirmation(): aguarda a confirmação chegar ao concentrador.</summary>
    short WaitConfirmation();

    /// <summary>PW_iIdleProc(): rotina ociosa, no horário de PWINFO_IDLEPROCTIME.</summary>
    short IdleProc();

    /// <summary>PW_iGetOperations(bOperType): lista de operações administrativas disponíveis.</summary>
    short GetOperations(byte tipoOperacao, out IReadOnlyList<PwOperacao> operacoes);

    // Captura no pinpad (chamadas para os PWDAT_PP*; `indice` = posição do PwGetData na lista).
    short PPGetCard(ushort indice);
    short PPGetPIN(ushort indice);
    short PPGetData(ushort indice);
    short PPGoOnChip(ushort indice);
    short PPFinishChip(ushort indice);
    short PPConfirmData(ushort indice);
    /// <summary>PW_iPPPositiveConfirmation(uiIndex): confirmação positiva de dado (PWDAT_PPDATAPOSCNF). Exemplo oficial.</summary>
    short PPPositiveConfirmation(ushort indice);
    short PPRemoveCard();
    short PPGenericCMD(ushort indice);

    /// <summary>
    /// PW_iPPEventLoop(pszDisplay). PWRET_NOTHING (continuar), PWRET_DISPLAY (`display` para o
    /// operador), PWRET_OK (captura concluída: voltar ao ExecTransac), PWRET_CANCEL, PWRET_TIMEOUT,
    /// PWRET_FALLBACK.
    /// </summary>
    short PPEventLoop(out string display);

    /// <summary>PW_iPPAbort(): interrompe a captura no pinpad.</summary>
    short PPAbort();

    /// <summary>PW_iTransactionInquiry(xml req) -> xml resp.</summary>
    short TransactionInquiry(string xmlRequisicao, out string xmlResposta);
}
