using System.Runtime.InteropServices;
using System.Text;

namespace Pdv.Nucleo;

/// <summary>
/// Binding P/Invoke da PGWebLib.dll (PayGo Windows, "Biblioteca Windows (DLL)").
///
/// FONTE DE CADA ASSINATURA: o exemplo oficial da PayGo em C#
/// (github.com/PGPagamentos/pdvWindowsPayGoLibC_CSharp, Exemplo CSharp/PGWLib/Interop.cs
/// e CustomObjects.cs), lido em 05/09/2026. Convenção StdCall, retornos short, strings ANSI,
/// PW_GetData sequencial com os tamanhos exatos do exemplo. A ÚNICA exceção é PW_End, que o
/// exemplo não declara: veio da tabela de exports da 4.1.50.24 e do desmontado (sem argumentos,
/// `ret` simples, a mesma rotina que o DLL_PROCESS_DETACH chama). Medido em 07/09/2026.
///
/// A DLL NÃO é distribuída com o PDV: ela vem com o PayGo Windows instalado na máquina da
/// loja (em 32 e 64 bits; o Pdv.exe é x64, logo carrega a de 64). Por isso o carregamento é
/// resolvido em tempo de execução a partir da pasta configurada (tef_pgweb_dll), nunca pelo
/// PATH: dois PayGo na mesma máquina (o de teste e o da loja) carregariam a DLL errada em
/// silêncio.
///
/// Na bateria esta classe nunca é tocada (o fake cobre o contrato); em produção,
/// DllNotFoundException aqui vira "PayGo Windows não encontrado" na tela de configuração.
/// </summary>
public sealed class PGWebLibNativa : IPGWebLib
{
    private const string Dll = "PGWebLib.dll";
    private const CallingConvention Conv = CallingConvention.StdCall;   // Interop.cs oficial
    /// <summary>Vetor que a DLL preenche em PW_iExecTransac; o exemplo oficial usa 10.</summary>
    public const int MaxPedidos = 10;

    private static string? _pasta;
    private static bool _resolverLigado;

    /// <summary>
    /// Pasta onde o PayGo Windows deixou a PGWebLib.dll. Chamar ANTES do primeiro uso.
    /// Vazio = deixa o Windows procurar (pasta do exe e PATH).
    /// </summary>
    public static void UsarPasta(string? pasta)
    {
        _pasta = string.IsNullOrWhiteSpace(pasta) ? null : pasta.Trim();
        if (_resolverLigado) return;
        _resolverLigado = true;
        NativeLibrary.SetDllImportResolver(typeof(PGWebLibNativa).Assembly, (nome, _, _) =>
        {
            if (!string.Equals(nome, Dll, StringComparison.OrdinalIgnoreCase) || _pasta is null) return IntPtr.Zero;
            return NativeLibrary.TryLoad(Path.Combine(_pasta, Dll), out var h) ? h : IntPtr.Zero;
        });
    }

    // ── structs com o layout EXATO do exemplo oficial (CustomObjects.cs) ────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct TextoMenu
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 41)] public string szTextoMenu;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct ValorMenu
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szValorMenu;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PW_GetData
    {
        public ushort wIdentificador;
        public byte bTipoDeDado;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 84)] public string szPrompt;
        public byte bNumOpcoesMenu;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)] public TextoMenu[] vszTextoMenu;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)] public ValorMenu[] vszValorMenu;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 41)] public string szMascaraDeCaptura;
        public byte bTiposEntradaPermitidos;
        public byte bTamanhoMinimo;
        public byte bTamanhoMaximo;
        public int ulValorMinimo;
        public int ulValorMaximo;
        public byte bOcultarDadosDigitados;
        public byte bValidacaoDado;
        public byte bAceitaNulo;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 41)] public string szValorInicial;
        public byte bTeclasDeAtalho;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 84)] public string szMsgValidacao;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 84)] public string szMsgConfirmacao;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 84)] public string szMsgDadoMaior;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 84)] public string szMsgDadoMenor;
        public byte bCapturarDataVencCartao;
        public int ulTipoEntradaCartao;
        public byte bItemInicial;
        public byte bNumeroCapturas;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 84)] public string szMsgPrevia;
        public byte bTipoEntradaCodigoBarras;
        public byte bOmiteMsgAlerta;
        public byte bIniciaPelaEsquerda;
        public byte bNotificarCancelamento;

        /// <summary>Converte para o record gerenciado que o provedor entende.</summary>
        public PwGetData ParaGerenciado()
        {
            var opcoes = new List<PwOpcaoMenu>();
            for (var k = 0; k < bNumOpcoesMenu && k < 40; k++)
                opcoes.Add(new PwOpcaoMenu(vszTextoMenu?[k].szTextoMenu ?? "", vszValorMenu?[k].szValorMenu ?? ""));
            return new PwGetData(
                Tipo: bTipoDeDado, Identificador: wIdentificador, Prompt: szPrompt ?? "",
                Opcoes: opcoes.Count > 0 ? opcoes : null,
                TamanhoMinimo: bTamanhoMinimo, TamanhoMaximo: bTamanhoMaximo,
                Mascara: string.IsNullOrEmpty(szMascaraDeCaptura) ? null : szMascaraDeCaptura,
                ValorInicial: string.IsNullOrEmpty(szValorInicial) ? null : szValorInicial,
                Ocultar: bOcultarDadosDigitados != 0, AceitaNulo: bAceitaNulo != 0);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PW_Operations
    {
        public byte bOperType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string szText;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string szValue;
    }

    // ── P/Invoke: nomes, tipos e convenção iguais ao Interop.cs oficial ─────────

    [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)] private static extern short PW_iInit(string pszWorkingDir);
    // Sem argumentos e sem retorno (eax é lixo): com zero argumentos StdCall e Cdecl são o mesmo `ret`.
    [DllImport(Dll, CallingConvention = Conv)] private static extern void PW_End();
    [DllImport(Dll, CallingConvention = Conv)] private static extern short PW_iNewTransac(byte bOper);
    [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)] private static extern short PW_iAddParam(ushort wParam, string pszValue);
    [DllImport(Dll, CallingConvention = Conv)] private static extern short PW_iExecTransac([Out] PW_GetData[] vstParam, ref short piNumParam);
    // O exemplo declara iInfo como short; PWINFO_DUEAMNT (0xBF06) só cabe reinterpretando os
    // 16 bits: por isso o cast unchecked no wrapper, e não um ushort aqui (mesma ABI de 16 bits).
    [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)] private static extern short PW_iGetResult(short iInfo, StringBuilder pszData, uint ulDataSize);
    [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)] private static extern short PW_iConfirmation(uint ulResult, string pszReqNum, string pszLocRef, string pszExtRef, string pszVirtMerch, string pszAuthSyst);
    [DllImport(Dll, CallingConvention = Conv)] private static extern short PW_iWaitConfirmation();
    [DllImport(Dll, CallingConvention = Conv)] private static extern short PW_iIdleProc();
    [DllImport(Dll, CallingConvention = Conv)] private static extern short PW_iGetOperations(byte bOperType, [Out] PW_Operations[] vstOperations, ref short piNumOperations);
    // PGWebLib.h: extern Int16 PW_EXPORT PW_iSetEnvironment (Int16 iEnv);
    // Existe no kit avulso 4.1.50.924 (conferido com TryGetExport nas duas arquiteturas).
    [DllImport(Dll, CallingConvention = Conv)] private static extern short PW_iSetEnvironment(short iEnv);
    [DllImport(Dll, CallingConvention = Conv)] private static extern short PW_iPPAbort();
    [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)] private static extern short PW_iPPEventLoop(StringBuilder pszDisplay, uint ulDisplaySize);
    [DllImport(Dll, CallingConvention = Conv)] private static extern short PW_iPPGetCard(ushort uiIndex);
    [DllImport(Dll, CallingConvention = Conv)] private static extern short PW_iPPGetPIN(ushort uiIndex);
    [DllImport(Dll, CallingConvention = Conv)] private static extern short PW_iPPGetData(ushort uiIndex);
    [DllImport(Dll, CallingConvention = Conv)] private static extern short PW_iPPGoOnChip(ushort uiIndex);
    [DllImport(Dll, CallingConvention = Conv)] private static extern short PW_iPPFinishChip(ushort uiIndex);
    [DllImport(Dll, CallingConvention = Conv)] private static extern short PW_iPPConfirmData(ushort uiIndex);
    [DllImport(Dll, CallingConvention = Conv)] private static extern short PW_iPPPositiveConfirmation(ushort uiIndex);
    [DllImport(Dll, CallingConvention = Conv)] private static extern short PW_iPPRemoveCard();
    [DllImport(Dll, CallingConvention = Conv)] private static extern short PW_iPPGenericCMD(ushort uiIndex);
    [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)] private static extern short PW_iTransactionInquiry(string pszXmlRequest, StringBuilder pszXmlResponse, uint ulXmlResponseLen);

    // ── IPGWebLib ───────────────────────────────────────────────────────────────

    public short Init(string diretorioTrabalho) => PW_iInit(diretorioTrabalho);
    public void End() => PW_End();
    public short NewTransac(byte operacao) => PW_iNewTransac(operacao);

    /// <summary>A PGWebLib.dll está neste processo (alguém já chamou a DLL). É o que decide se há PW_End a fazer no fechamento.</summary>
    public static bool Carregada() => GetModuleHandleW(Dll) != IntPtr.Zero;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string nome);
    public short AddParam(ushort info, string valor) => PW_iAddParam(info, ArquivoIntpos.Ascii(valor));

    public short ExecTransac(out IReadOnlyList<PwGetData> pedidos)
    {
        var vet = new PW_GetData[MaxPedidos];
        short n = MaxPedidos;
        var ret = PW_iExecTransac(vet, ref n);
        var lista = new List<PwGetData>();
        if (ret == PW.PWRET_MOREDATA)
            for (var i = 0; i < n && i < vet.Length; i++) lista.Add(vet[i].ParaGerenciado());
        pedidos = lista;
        return ret;
    }

    public short GetResult(ushort info, out string valor)
    {
        // Vias (RCPTFULL etc.) passam de 1 KB; 10 KB é o que o exemplo oficial reserva.
        var sb = new StringBuilder(10240);
        var ret = PW_iGetResult(unchecked((short)info), sb, (uint)sb.Capacity);
        valor = ret == PW.PWRET_OK ? sb.ToString() : "";
        return ret;
    }

    public short Confirmation(uint resultado, string reqNum, string locRef, string extRef, string virtMerch, string authSyst)
        => PW_iConfirmation(resultado, reqNum ?? "", locRef ?? "", extRef ?? "", virtMerch ?? "", authSyst ?? "");
    public short WaitConfirmation() => PW_iWaitConfirmation();
    public short IdleProc() => PW_iIdleProc();

    public short GetOperations(byte tipoOperacao, out IReadOnlyList<PwOperacao> operacoes)
    {
        var vet = new PW_Operations[50];
        short n = (short)vet.Length;
        var ret = PW_iGetOperations(tipoOperacao, vet, ref n);
        var lista = new List<PwOperacao>();
        if (ret == PW.PWRET_OK)
            for (var i = 0; i < n && i < vet.Length; i++)
                lista.Add(new PwOperacao(vet[i].bOperType, vet[i].szText ?? "", vet[i].szValue ?? ""));
        operacoes = lista;
        return ret;
    }

    public short SetEnvironment(short ambiente) => PW_iSetEnvironment(ambiente);

    public short PPGetCard(ushort indice) => PW_iPPGetCard(indice);
    public short PPGetPIN(ushort indice) => PW_iPPGetPIN(indice);
    public short PPGetData(ushort indice) => PW_iPPGetData(indice);
    public short PPGoOnChip(ushort indice) => PW_iPPGoOnChip(indice);
    public short PPFinishChip(ushort indice) => PW_iPPFinishChip(indice);
    public short PPConfirmData(ushort indice) => PW_iPPConfirmData(indice);
    public short PPPositiveConfirmation(ushort indice) => PW_iPPPositiveConfirmation(indice);
    public short PPRemoveCard() => PW_iPPRemoveCard();
    public short PPGenericCMD(ushort indice) => PW_iPPGenericCMD(indice);

    public short PPEventLoop(out string display)
    {
        var sb = new StringBuilder(1000);   // tamanho que o exemplo oficial usa
        var ret = PW_iPPEventLoop(sb, (uint)sb.Capacity);
        display = ret == PW.PWRET_DISPLAY ? sb.ToString() : "";
        return ret;
    }

    public short PPAbort() => PW_iPPAbort();

    public short TransactionInquiry(string xmlRequisicao, out string xmlResposta)
    {
        var sb = new StringBuilder(64 * 1024);
        var ret = PW_iTransactionInquiry(xmlRequisicao ?? "", sb, (uint)sb.Capacity);
        xmlResposta = ret == PW.PWRET_OK ? sb.ToString() : "";
        return ret;
    }
}
