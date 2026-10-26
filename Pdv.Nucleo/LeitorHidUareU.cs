using System.Runtime.InteropServices;
using System.Text;

namespace Pdv.Nucleo;

// ════════════════════════════════════════════════════════════════════════════
//  O SEGUNDO LEITOR: O RUNTIME DA HID (U.are.U RTE), caixa 1.0.53, 26/10/2026
//
//  Na loja, com o U.are.U 4500 plugado e o driver instalado, o caixa 1.0.52 dizia que
//  nao havia leitor. O driver era o da HID (o "Non-WBF", ou o que vem com o U.are.U
//  RTE/SDK), e com ele o Windows Biometric Framework nao enumera nada. Este leitor
//  fala direto com a API C do runtime da HID:
//   · dpfpdd.dll (captura): acha o aparelho, abre, captura a imagem (FID ISO 19794-4);
//   · dpfj.dll (extracao e comparacao): tira o molde (FMD ISO 19794-2), junta as
//     leituras do cadastro e compara dois moldes.
//
//  As DLLs NAO vao no instalador do caixa: vem do U.are.U RTE (ou SDK) instalado no PC.
//  Procuramos no SDK (Program Files\DigitalPersona\U.are.U SDK\Windows\Lib\x64), nas
//  pastas do RTE, no System32 e no PATH (ver ApiHidUareU.PastasCandidatas). O caixa e
//  x64, entao so serve a DLL de 64 bits.
//
//  Diferente do WinBio, aqui o MOLDE e nosso: ele vai para a guarda local (tabela
//  digital, coluna molde_cifrado, cifrado pelo DPAPI da maquina, motor = 'hid'), ligado
//  ao CPF. Identificar = capturar, extrair e comparar com os moldes da guarda (ate 10 por
//  caixa); entra o de menor distancia, se ela for MENOR que o limiar de falso aceite de
//  1 em 100000 da propria dpfj (DPFJ_PROBABILITY_ONE / 100000).
//
//  ── AS ASSINATURAS USADAS (fonte: dpfpdd.h e dpfj.h do U.are.U SDK da HID, versao 3.x,
//  ── conforme a documentacao publica do SDK; NAO houve header nem leitor nesta maquina
//  ── para provar: tudo abaixo esta "a conferir com o leitor"). DPAPICALL = __stdcall,
//  ── que no x64 e a unica convencao (nao muda nada aqui).
//
//  dpfpdd.h
//    int dpfpdd_version(DPFPDD_VERSION* ver);                       // opcional, so o diagnostico
//    int dpfpdd_init(void);
//    int dpfpdd_query_devices(unsigned int* dev_cnt, DPFPDD_DEV_INFO* dev_infos);
//    int dpfpdd_open(char* dev_name, DPFPDD_DEV* pdev);             // DPFPDD_DEV = ponteiro opaco
//    int dpfpdd_close(DPFPDD_DEV dev);
//    int dpfpdd_cancel(DPFPDD_DEV dev);
//    int dpfpdd_get_device_capabilities(DPFPDD_DEV dev, DPFPDD_DEV_CAPS* caps);
//    int dpfpdd_capture(DPFPDD_DEV dev, DPFPDD_CAPTURE_PARAM* param, unsigned int timeout_cnt,
//                       DPFPDD_CAPTURE_RESULT* result, unsigned int* image_size, unsigned char* image_data);
//    DPFPDD_VER_INFO   { int major, minor, maintenance; }                       12 bytes
//    DPFPDD_VERSION    { unsigned size; VER_INFO lib_ver; VER_INFO api_ver; }   28 bytes
//    DPFPDD_DEV_INFO   { unsigned size; char name[1024];
//                        HW_DESCR { char vendor_name[128], product_name[128], serial_num[128]; }
//                        HW_ID { unsigned short vendor_id, product_id; }
//                        HW_VERSION { VER_INFO hw_ver, fw_ver; unsigned short bcd_rev; }
//                        unsigned modality; unsigned technology; }           1452 bytes
//    DPFPDD_DEV_CAPS   { unsigned size; int can_capture_image, can_stream_image, can_extract_features,
//                        can_match, can_identify, has_fp_storage; unsigned indicator_type;
//                        int has_pwr_mgmt, has_calibration, piv_compliant;
//                        unsigned resolution_cnt; unsigned resolutions[]; } resolution_cnt em 44
//    DPFPDD_CAPTURE_PARAM  { unsigned size, image_fmt, image_proc, image_res; }   16 bytes
//    DPFPDD_IMAGE_INFO     { unsigned size, width, height, res, bpp; }           20 bytes
//    DPFPDD_CAPTURE_RESULT { unsigned size; int success; unsigned quality, score; IMAGE_INFO info; } 36 bytes
//    DPFPDD_IMG_FMT_ISOIEC19794 = 0x01010007, DPFPDD_IMG_PROC_DEFAULT = 0, DPFPDD_TIMEOUT_INFINITE = -1
//    qualidade: 0 GOOD, 1 TIMED_OUT, 2 CANCELED, 4 NO_FINGER, 8 FAKE_FINGER, 16..512 posicao,
//               1024.. passada, 32768 READER_DIRTY
//    erros: DPERROR(n) = (0x05BA << 16) | n; 0x0d MORE_DATA, 0x14 INVALID_PARAMETER,
//           0x15 INVALID_DEVICE, 0x1e DEVICE_BUSY, 0x1f DEVICE_FAILURE
//
//  dpfj.h
//    int dpfj_create_fmd_from_fid(DPFJ_FID_FORMAT fid_type, const unsigned char* fid, unsigned int fid_size,
//                                 DPFJ_FMD_FORMAT fmd_type, unsigned char* fmd, unsigned int* fmd_size);
//    int dpfj_compare(DPFJ_FMD_FORMAT fmd1_type, unsigned char* fmd1, unsigned int fmd1_size, unsigned int fmd1_view_idx,
//                     DPFJ_FMD_FORMAT fmd2_type, unsigned char* fmd2, unsigned int fmd2_size, unsigned int fmd2_view_idx,
//                     unsigned int* score);
//    int dpfj_start_enrollment(DPFJ_FMD_FORMAT fmd_type);
//    int dpfj_add_to_enrollment(DPFJ_FMD_FORMAT fmd_type, unsigned char* fmd, unsigned int fmd_size, unsigned int fmd_view_idx);
//                     // SUCCESS = ja da para fechar; MORE_DATA = precisa de mais leituras
//    int dpfj_create_enrollment_fmd(unsigned char* fmd, unsigned int* fmd_size);
//    int dpfj_finish_enrollment(void);
//    DPFJ_FID_ISO_19794_4_2005 = 0x01010007, DPFJ_FMD_ISO_19794_2_2005 = 0x01010001
//    DPFJ_PROBABILITY_ONE = 0x7FFFFFFF: score e a DISSIMILARIDADE (0 = igual); taxa de falso
//    aceite = score / PROBABILITY_ONE. Limiar para 1/100000 = PROBABILITY_ONE / 100000.
//    erros: mesma facility; 0x65 INVALID_FID, 0x66 TOO_SMALL_AREA, 0xc9 INVALID_FMD,
//           0x12d..0x130 enrollment
//
//  Testes: Pdv.Testes/TestesLeitorHid.cs (API falsa; a real so prova que carrega sem lancar).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>Um leitor que o runtime da HID listou.</summary>
public sealed record DispositivoHid(string Nome, string Fabricante, string Produto, string Serie, ushort Vid, ushort Pid)
{
    public override string ToString()
        => $"{(Produto.Length > 0 ? Produto : "leitor")} (VID {Vid:X4} PID {Pid:X4}" + (Serie.Length > 0 ? $", serie {Serie}" : "") + ")";
}

/// <summary>O runtime da HID: carregou? de onde? qual versao? ou o que faltou.</summary>
public sealed record RuntimeHid(bool Carregado, string? Caminho, string? Versao, string? Erro);

public enum QualidadeHid { Boa, Cancelada, SemDedo, Ruim, Falha }

/// <summary>Uma captura: a imagem (FID ISO) so quando Boa.</summary>
public sealed record CapturaHid(QualidadeHid Qualidade, byte[]? Fid, string? Motivo = null);

/// <summary>Um molde da guarda: a chave (hid:...) e o FMD decifrado.</summary>
public sealed record MoldeGuardado(string Chave, byte[] Fmd);

/// <summary>
/// A API C da HID atras de uma interface: a suite roda tudo sem a DLL e sem o leitor. A
/// implementacao real e <see cref="ApiHidUareU"/>. Nada aqui lanca para fora.
/// </summary>
public interface IApiHid
{
    /// <summary>Carrega dpfpdd.dll e dpfj.dll e chama dpfpdd_init (uma vez por processo).</summary>
    RuntimeHid Carregar();

    /// <summary>dpfpdd_query_devices. Lista vazia + erro quando nao deu.</summary>
    IReadOnlyList<DispositivoHid> Dispositivos(out string? erro);

    /// <summary>Abre, captura uma imagem (bloqueia ate o dedo) e fecha. Cancelar pelo token (dpfpdd_cancel).</summary>
    CapturaHid Capturar(string dispositivo, CancellationToken ct);

    /// <summary>dpfj_create_fmd_from_fid. null + erro quando nao tirou molde.</summary>
    byte[]? Extrair(byte[] fid, out string? erro);

    /// <summary>
    /// dpfj_start/add/create/finish_enrollment com as amostras dadas. Devolve o molde do
    /// cadastro, ou null com <paramref name="faltaAmostra"/> = true quando o runtime quer mais.
    /// </summary>
    byte[]? JuntarCadastro(IReadOnlyList<byte[]> fmds, out bool faltaAmostra, out string? erro);

    /// <summary>dpfj_compare: a distancia (0 = igual). Erro = uint.MaxValue (nunca entra).</summary>
    uint Comparar(byte[] a, byte[] b);
}

/// <summary>O leitor pelo runtime da HID (dpfpdd + dpfj). Ver o cabecalho deste arquivo.</summary>
public sealed class LeitorHidUareU : ILeitorDigital
{
    /// <summary>DPFJ_PROBABILITY_ONE (dpfj.h).</summary>
    public const uint ProbabilidadeUm = 0x7FFFFFFF;

    /// <summary>Falso aceite de 1 em 100000: entra so distancia MENOR que isto.</summary>
    public const uint Limiar = ProbabilidadeUm / 100000;

    /// <summary>O valor da coluna digital.motor e o prefixo da chave.</summary>
    public const string MotorTexto = "hid";
    public const string Prefixo = "hid:";

    /// <summary>Teto de leituras de um cadastro (o runtime costuma fechar com 4).</summary>
    public const int MaxLeiturasDoCadastro = 12;

    private readonly IApiHid _api;
    private readonly Func<IReadOnlyList<MoldeGuardado>> _moldes;

    public LeitorHidUareU(IApiHid api, Func<IReadOnlyList<MoldeGuardado>> moldes)
    {
        _api = api;
        _moldes = moldes;
    }

    public string Nome => "HID";

    public Task<SondaLeitor> SondarAsync(CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            var rt = _api.Carregar();
            if (!rt.Carregado)
                return new SondaLeitor(EstadoLeitor.SemLeitor, "U.are.U RTE nao carregou (" + (rt.Erro ?? "?") + ")", Array.Empty<string>());
            var lista = _api.Dispositivos(out var erro);
            var nomes = lista.Select(d => d.ToString()).ToList();
            if (lista.Count == 0)
                return new SondaLeitor(EstadoLeitor.SemLeitor,
                    $"U.are.U RTE {rt.Versao ?? "?"}: nenhum leitor" + (erro is null ? "" : $" ({erro})"), nomes);
            return new SondaLeitor(EstadoLeitor.Pronto, $"U.are.U RTE {rt.Versao ?? "?"}: {lista.Count} leitor(es): {string.Join("; ", nomes)}", nomes);
        }
        catch (Exception ex) { return new SondaLeitor(EstadoLeitor.SemLeitor, "U.are.U RTE: " + ex.Message, Array.Empty<string>()); }
    }, CancellationToken.None);

    /// <summary>O primeiro leitor que o runtime lista, ou null.</summary>
    private DispositivoHid? Aparelho()
    {
        if (!_api.Carregar().Carregado) return null;
        var lista = _api.Dispositivos(out _);
        return lista.Count > 0 ? lista[0] : null;
    }

    public Task<Captura> IdentificarAsync(CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            var dev = Aparelho();
            if (dev is null) return new Captura(ResultadoCaptura.SemLeitor);
            var cap = _api.Capturar(dev.Nome, ct);
            if (ct.IsCancellationRequested) return Captura.Cancelada;
            switch (cap.Qualidade)
            {
                case QualidadeHid.Cancelada: return Captura.Cancelada;
                case QualidadeHid.Falha: return Captura.Falhou(cap.Motivo ?? "HID: falha");
                case QualidadeHid.SemDedo:
                case QualidadeHid.Ruim: return new Captura(ResultadoCaptura.NaoReconhecido, null, "leitura ruim (" + (cap.Motivo ?? "?") + ")");
            }
            var fmd = _api.Extrair(cap.Fid!, out var erro);
            if (fmd is null) return new Captura(ResultadoCaptura.NaoReconhecido, null, "leitura ruim (" + (erro ?? "sem molde") + ")");
            var (chave, _) = Melhor(_api, fmd, _moldes());
            return chave is null ? Captura.NaoReconhecida : Captura.Reconhecida(chave);
        }
        catch (Exception ex) { return Captura.Falhou("HID: " + ex.Message); }
    }, CancellationToken.None);

    /// <summary>O molde mais parecido abaixo do limiar (estritamente menor), ou (null, menor distancia vista).</summary>
    public static (string? Chave, uint Distancia) Melhor(IApiHid api, byte[] fmd, IReadOnlyList<MoldeGuardado> moldes)
    {
        string? melhor = null;
        var menor = uint.MaxValue;
        foreach (var m in moldes)
        {
            var d = api.Comparar(fmd, m.Fmd);
            if (d < menor) { menor = d; melhor = m.Chave; }
        }
        return menor < Limiar ? (melhor, menor) : (null, menor);
    }

    public Task<Cadastro> CadastrarAsync(int vaga, IProgress<int>? amostra, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            var dev = Aparelho();
            if (dev is null) return new Cadastro(ResultadoCadastro.SemLeitor, null, 0, "U.are.U RTE sem leitor");
            var fmds = new List<byte[]>();
            string? ultimoErro = null;
            for (var leitura = 1; leitura <= MaxLeiturasDoCadastro; leitura++)
            {
                var cap = _api.Capturar(dev.Nome, ct);
                if (ct.IsCancellationRequested || cap.Qualidade == QualidadeHid.Cancelada) return Cadastro.Cancelado;
                if (cap.Qualidade == QualidadeHid.Falha) return Cadastro.Falhou(cap.Motivo ?? "HID: falha");
                if (cap.Qualidade != QualidadeHid.Boa) { ultimoErro = cap.Motivo; continue; }      // dedo mal encostado
                var fmd = _api.Extrair(cap.Fid!, out var erro);
                if (fmd is null) { ultimoErro = erro; continue; }
                fmds.Add(fmd);
                amostra?.Report(fmds.Count);
                // o dono pediu 3 leituras: antes disso nem pergunta ao runtime
                if (fmds.Count < Biometria.LeiturasDoCadastro) continue;
                var molde = _api.JuntarCadastro(fmds, out var falta, out erro);
                if (molde is not null)
                {
                    var (parecida, _) = Melhor(_api, molde, _moldes());
                    return Cadastro.Feito(Prefixo + Guid.NewGuid().ToString("N"), fmds.Count) with
                    {
                        Motor = MotorTexto, Molde = molde, ChaveParecida = parecida,
                    };
                }
                if (!falta) return Cadastro.Falhou("cadastro: " + (erro ?? "o runtime recusou as leituras"));
            }
            return Cadastro.Falhou($"{MaxLeiturasDoCadastro} leituras sem fechar o cadastro" + (ultimoErro is null ? "" : $" ({ultimoErro})"));
        }
        catch (Exception ex) { return Cadastro.Falhou("HID: " + ex.Message); }
    }, CancellationToken.None);

    /// <summary>O molde mora na guarda (quem apaga e Digitais.Apagar): no aparelho nao ha nada.</summary>
    public Task<bool> ApagarAsync(string chave, CancellationToken ct) => Task.FromResult(true);
}

/// <summary>
/// A API C de verdade (dpfpdd.dll + dpfj.dll do U.are.U RTE), por NativeLibrary e
/// delegates: o Pdv.Nucleo ja tem um DllImportResolver (PGWebLibNativa) e o .NET so aceita
/// um por assembly; e assim o caminho de onde carregou vai para o diagnostico.
/// </summary>
public sealed class ApiHidUareU : IApiHid
{
    // ── formatos e codigos (dpfpdd.h, dpfj.h) ─────────────────────────────────────
    public const int FidIso = 0x01010007;          // DPFPDD_IMG_FMT_ISOIEC19794 = DPFJ_FID_ISO_19794_4_2005
    public const int FmdIso = 0x01010001;          // DPFJ_FMD_ISO_19794_2_2005
    public const uint ImgProcDefault = 0;          // DPFPDD_IMG_PROC_DEFAULT
    public const uint TimeoutInfinito = 0xFFFFFFFF; // DPFPDD_TIMEOUT_INFINITE (-1)
    public const int Facilidade = 0x05BA;
    public static int ErroHid(int n) => (Facilidade << 16) | n;
    private static readonly int EMaisDados = ErroHid(0x0d);
    private const uint QualidadeCancelada = 2;

    // ── layout das estruturas (ver o cabecalho; a conferir com o leitor) ────────────
    public const int TamanhoDevInfo = 1452;
    public const int TamanhoNomeDev = 1024, TamanhoTextoHw = 128;
    public const int DeslocNome = 4, DeslocFabricante = 1028, DeslocProduto = 1156, DeslocSerie = 1284, DeslocamentoVid = 1412, DeslocPid = 1414;
    private const int TamanhoCaps = 256;            // a base tem 52; sobra para a lista de resolucoes
    private const int DeslocQtdResolucoes = 44, DeslocResolucao0 = 48;

    [StructLayout(LayoutKind.Sequential)] private struct VerInfo { public int Major, Minor, Maintenance; }
    [StructLayout(LayoutKind.Sequential)] private struct Versao { public uint Size; public VerInfo Lib; public VerInfo Api; }
    [StructLayout(LayoutKind.Sequential)] private struct CaptureParam { public uint Size, ImageFmt, ImageProc, ImageRes; }
    [StructLayout(LayoutKind.Sequential)] private struct ImageInfo { public uint Size, Width, Height, Res, Bpp; }
    [StructLayout(LayoutKind.Sequential)] private struct CaptureResult { public uint Size; public int Success; public uint Quality, Score; public ImageInfo Info; }

    /// <summary>Espelho de DPFPDD_DEV_INFO so para a suite conferir o tamanho e o deslocamento (a leitura e por bytes).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DevInfoEspelho
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = TamanhoNomeDev)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = TamanhoTextoHw)] public string Vendor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = TamanhoTextoHw)] public string Product;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = TamanhoTextoHw)] public string Serial;
        public ushort VendorId, ProductId;
        public VerInfo HwVer, FwVer;
        public ushort BcdRev;
        public uint Modality, Technology;
    }

    public static int TamanhoDevInfoMarshal => Marshal.SizeOf<DevInfoEspelho>();
    public static int TamanhoCaptureParam => Marshal.SizeOf<CaptureParam>();
    public static int TamanhoCaptureResult => Marshal.SizeOf<CaptureResult>();
    public static int TamanhoVersao => Marshal.SizeOf<Versao>();

    // ── as funcoes (delegates sobre GetExport) ──────────────────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnSemArg();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnVersao(ref Versao v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnQuery(ref uint qtd, [In, Out] byte[]? infos);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnOpen([MarshalAs(UnmanagedType.LPStr)] string nome, out IntPtr dev);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnDev(IntPtr dev);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnCaps(IntPtr dev, [In, Out] byte[] caps);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnCapture(IntPtr dev, ref CaptureParam p, uint timeout, ref CaptureResult r, ref uint tamanho, [In, Out] byte[]? imagem);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnFmdDeFid(int fidTipo, byte[] fid, uint fidTam, int fmdTipo, [In, Out] byte[] fmd, ref uint fmdTam);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnComparar(int t1, byte[] f1, uint s1, uint v1, int t2, byte[] f2, uint s2, uint v2, out uint score);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnTipo(int fmdTipo);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnAddEnroll(int fmdTipo, byte[] fmd, uint tam, uint vista);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnCriarEnroll([In, Out] byte[] fmd, ref uint tam);

    private sealed class Carga
    {
        public RuntimeHid Info = new(false, null, null, null);
        public FnQuery Query = null!;
        public FnOpen Open = null!;
        public FnDev Close = null!, Cancel = null!;
        public FnCaps Caps = null!;
        public FnCapture Capture = null!;
        public FnFmdDeFid FmdDeFid = null!;
        public FnComparar Comparar = null!;
        public FnTipo IniciarCadastro = null!;
        public FnAddEnroll AddCadastro = null!;
        public FnCriarEnroll CriarCadastro = null!;
        public FnSemArg FimCadastro = null!;
    }

    private static readonly object Trava = new();
    private static readonly object TravaCadastro = new();
    private static Carga? _carga;

    /// <summary>
    /// Onde o U.are.U RTE/SDK costuma por as DLLs de 64 bits, em ordem: o SDK, o RTE, o
    /// System32 e o PATH. So pastas que existem entram na busca (a lista inteira vai para o
    /// diagnostico quando nada carrega).
    /// </summary>
    public static IReadOnlyList<string> PastasCandidatas()
    {
        var pf = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var lista = new List<string>
        {
            Path.Combine(pf, @"DigitalPersona\U.are.U SDK\Windows\Lib\x64"),
            Path.Combine(pf, @"DigitalPersona\U.are.U RTE\Windows\Lib\x64"),
            Path.Combine(pf, @"DigitalPersona\U.are.U RTE"),
            Path.Combine(pf, @"DigitalPersona\Bin"),
            Path.Combine(pf, @"HID Global\U.are.U SDK\Windows\Lib\x64"),
            Path.Combine(pf, @"HID Global\U.are.U RTE"),
            Environment.SystemDirectory,
        };
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!lista.Contains(p, StringComparer.OrdinalIgnoreCase)) lista.Add(p);
        return lista;
    }

    public RuntimeHid Carregar()
    {
        lock (Trava)
        {
            if (_carga is not null) return _carga.Info;
            // so o sucesso fica guardado: instalar o RTE com o caixa aberto e tocar em Conferir de novo funciona
            var (carga, info) = Montar();
            if (carga is not null) _carga = carga;
            return info;
        }
    }

    private static (Carga?, RuntimeHid) Montar()
    {
        if (!OperatingSystem.IsWindows()) return (null, new RuntimeHid(false, null, null, "nao e Windows"));
        if (!Environment.Is64BitProcess) return (null, new RuntimeHid(false, null, null, "o caixa nao e 64 bits"));
        string? caminhoDd = null;
        foreach (var pasta in PastasCandidatas())
        {
            try { if (File.Exists(Path.Combine(pasta, "dpfpdd.dll"))) { caminhoDd = Path.Combine(pasta, "dpfpdd.dll"); break; } } catch { }
        }
        if (caminhoDd is null)
            return (null, new RuntimeHid(false, null, null, @"dpfpdd.dll nao achada no System32, no PATH nem em Program Files\DigitalPersona"));
        var pastaDd = Path.GetDirectoryName(caminhoDd)!;
        var caminhoDj = Path.Combine(pastaDd, "dpfj.dll");
        if (!File.Exists(caminhoDj))
            caminhoDj = PastasCandidatas().Select(p => Path.Combine(p, "dpfj.dll")).FirstOrDefault(File.Exists) ?? "";
        if (caminhoDj.Length == 0)
            return (null, new RuntimeHid(false, caminhoDd, null, "dpfj.dll nao achada (a extracao vem com o U.are.U RTE)"));

        IntPtr dd, dj;
        try { dd = NativeLibrary.Load(caminhoDd); }
        catch (BadImageFormatException) { return (null, new RuntimeHid(false, caminhoDd, null, "dpfpdd.dll de 32 bits: instale o U.are.U RTE de 64 bits")); }
        catch (Exception ex) { return (null, new RuntimeHid(false, caminhoDd, null, "dpfpdd.dll nao carregou: " + ex.Message)); }
        try { dj = NativeLibrary.Load(caminhoDj); }
        catch (BadImageFormatException) { return (null, new RuntimeHid(false, caminhoDj, null, "dpfj.dll de 32 bits: instale o U.are.U RTE de 64 bits")); }
        catch (Exception ex) { return (null, new RuntimeHid(false, caminhoDj, null, "dpfj.dll nao carregou: " + ex.Message)); }

        var c = new Carga();
        var faltando = new List<string>();
        T? F<T>(IntPtr lib, string nome) where T : Delegate
        {
            if (NativeLibrary.TryGetExport(lib, nome, out var p)) return Marshal.GetDelegateForFunctionPointer<T>(p);
            faltando.Add(nome);
            return null;
        }
        var init = F<FnSemArg>(dd, "dpfpdd_init");
        c.Query = F<FnQuery>(dd, "dpfpdd_query_devices")!;
        c.Open = F<FnOpen>(dd, "dpfpdd_open")!;
        c.Close = F<FnDev>(dd, "dpfpdd_close")!;
        c.Cancel = F<FnDev>(dd, "dpfpdd_cancel")!;
        c.Caps = F<FnCaps>(dd, "dpfpdd_get_device_capabilities")!;
        c.Capture = F<FnCapture>(dd, "dpfpdd_capture")!;
        c.FmdDeFid = F<FnFmdDeFid>(dj, "dpfj_create_fmd_from_fid")!;
        c.Comparar = F<FnComparar>(dj, "dpfj_compare")!;
        c.IniciarCadastro = F<FnTipo>(dj, "dpfj_start_enrollment")!;
        c.AddCadastro = F<FnAddEnroll>(dj, "dpfj_add_to_enrollment")!;
        c.CriarCadastro = F<FnCriarEnroll>(dj, "dpfj_create_enrollment_fmd")!;
        c.FimCadastro = F<FnSemArg>(dj, "dpfj_finish_enrollment")!;
        if (faltando.Count > 0)
            return (null, new RuntimeHid(false, caminhoDd, null, "o runtime nao tem " + string.Join(", ", faltando) + " (versao antiga do RTE?)"));

        string? versao = null;
        try
        {
            if (NativeLibrary.TryGetExport(dd, "dpfpdd_version", out var pv))
            {
                var v = new Versao { Size = (uint)Marshal.SizeOf<Versao>() };
                if (Marshal.GetDelegateForFunctionPointer<FnVersao>(pv)(ref v) == 0)
                    versao = $"{v.Lib.Major}.{v.Lib.Minor}.{v.Lib.Maintenance}";
            }
        }
        catch { }
        try
        {
            var r = init!();
            if (r != 0) return (null, new RuntimeHid(false, caminhoDd, versao, "dpfpdd_init: " + Texto(r)));
        }
        catch (Exception ex) { return (null, new RuntimeHid(false, caminhoDd, versao, "dpfpdd_init: " + ex.Message)); }
        c.Info = new RuntimeHid(true, caminhoDd, versao ?? "?", null);
        return (c, c.Info);
    }

    private static Carga? Pronta() { lock (Trava) return _carga; }

    public IReadOnlyList<DispositivoHid> Dispositivos(out string? erro)
    {
        erro = null;
        var c = Pronta();
        if (c is null) { erro = "runtime nao carregado"; return Array.Empty<DispositivoHid>(); }
        try
        {
            uint qtd = 0;
            var r = c.Query(ref qtd, null);
            if (r != 0 && r != EMaisDados) { erro = Texto(r); return Array.Empty<DispositivoHid>(); }
            for (var volta = 0; volta < 3 && qtd > 0; volta++)
            {
                var buf = new byte[qtd * TamanhoDevInfo];
                for (var i = 0; i < qtd; i++) BitConverter.GetBytes((uint)TamanhoDevInfo).CopyTo(buf, i * TamanhoDevInfo);
                var n = qtd;
                r = c.Query(ref n, buf);
                if (r == EMaisDados && n > qtd) { qtd = n; continue; }    // plugaram outro no meio
                if (r != 0) { erro = Texto(r) + (r == ErroHid(0x14) ? $" (tamanho da DPFPDD_DEV_INFO {TamanhoDevInfo} diferente do runtime)" : ""); return Array.Empty<DispositivoHid>(); }
                return LerDispositivos(buf, (int)Math.Min(n, qtd));
            }
            return Array.Empty<DispositivoHid>();
        }
        catch (Exception ex) { erro = ex.Message; return Array.Empty<DispositivoHid>(); }
    }

    /// <summary>Le <paramref name="n"/> DPFPDD_DEV_INFO do vetor de bytes. Puro (a suite prova).</summary>
    public static IReadOnlyList<DispositivoHid> LerDispositivos(byte[] buf, int n)
    {
        var saida = new List<DispositivoHid>();
        for (var i = 0; i < n && (i + 1) * TamanhoDevInfo <= buf.Length; i++)
        {
            var b = i * TamanhoDevInfo;
            saida.Add(new DispositivoHid(
                TextoC(buf, b + DeslocNome, TamanhoNomeDev), TextoC(buf, b + DeslocFabricante, TamanhoTextoHw),
                TextoC(buf, b + DeslocProduto, TamanhoTextoHw), TextoC(buf, b + DeslocSerie, TamanhoTextoHw),
                BitConverter.ToUInt16(buf, b + DeslocamentoVid), BitConverter.ToUInt16(buf, b + DeslocPid)));
        }
        return saida;
    }

    private static string TextoC(byte[] buf, int ini, int max)
    {
        var fim = Array.IndexOf(buf, (byte)0, ini, max);
        return Encoding.Latin1.GetString(buf, ini, (fim < 0 ? ini + max : fim) - ini).Trim();
    }

    public CapturaHid Capturar(string dispositivo, CancellationToken ct)
    {
        var c = Pronta();
        if (c is null) return new CapturaHid(QualidadeHid.Falha, null, "runtime nao carregado");
        if (ct.IsCancellationRequested) return new CapturaHid(QualidadeHid.Cancelada, null);
        IntPtr dev;
        try
        {
            var r = c.Open(dispositivo, out dev);
            if (r != 0) return new CapturaHid(QualidadeHid.Falha, null, "abrir o leitor: " + Texto(r));
        }
        catch (Exception ex) { return new CapturaHid(QualidadeHid.Falha, null, "abrir o leitor: " + ex.Message); }
        try
        {
            using var reg = ct.Register(() => { try { c.Cancel(dev); } catch { } });
            var p = new CaptureParam { Size = (uint)TamanhoCaptureParam, ImageFmt = FidIso, ImageProc = ImgProcDefault, ImageRes = Resolucao(c, dev) };
            uint tam = 400_000;    // o 4500 da ~120 KB a 500 dpi; MORE_DATA diz o certo
            for (var volta = 0; volta < 2; volta++)
            {
                var img = new byte[tam];
                var cr = new CaptureResult { Size = (uint)TamanhoCaptureResult, Info = new ImageInfo { Size = (uint)Marshal.SizeOf<ImageInfo>() } };
                var t = tam;
                var r = c.Capture(dev, ref p, TimeoutInfinito, ref cr, ref t, img);
                if (ct.IsCancellationRequested || (cr.Quality & QualidadeCancelada) != 0) return new CapturaHid(QualidadeHid.Cancelada, null);
                if (r == EMaisDados && t > tam) { tam = t; continue; }
                if (r != 0) return new CapturaHid(QualidadeHid.Falha, null, Texto(r));
                if (cr.Success == 0 || cr.Quality != 0)
                    return new CapturaHid((cr.Quality & 5) != 0 ? QualidadeHid.SemDedo : QualidadeHid.Ruim, null, TextoQualidade(cr.Quality));
                return new CapturaHid(QualidadeHid.Boa, img[..(int)Math.Min(t, tam)]);
            }
            return new CapturaHid(QualidadeHid.Falha, null, "imagem maior que o esperado");
        }
        catch (Exception ex) { return new CapturaHid(QualidadeHid.Falha, null, "captura: " + ex.Message); }
        finally { try { c.Close(dev); } catch { } }
    }

    /// <summary>A primeira resolucao do aparelho (dpfpdd_get_device_capabilities); 500 dpi se nao disser.</summary>
    private static uint Resolucao(Carga c, IntPtr dev)
    {
        try
        {
            var caps = new byte[TamanhoCaps];
            BitConverter.GetBytes((uint)TamanhoCaps).CopyTo(caps, 0);
            if (c.Caps(dev, caps) == 0 && BitConverter.ToUInt32(caps, DeslocQtdResolucoes) > 0)
            {
                var r = BitConverter.ToUInt32(caps, DeslocResolucao0);
                if (r is >= 250 and <= 2000) return r;
            }
        }
        catch { }
        return 500;
    }

    public byte[]? Extrair(byte[] fid, out string? erro)
    {
        erro = null;
        var c = Pronta();
        if (c is null) { erro = "runtime nao carregado"; return null; }
        try
        {
            uint tam = 4096;
            for (var volta = 0; volta < 2; volta++)
            {
                var fmd = new byte[tam];
                var t = tam;
                var r = c.FmdDeFid(FidIso, fid, (uint)fid.Length, FmdIso, fmd, ref t);
                if (r == EMaisDados && t > tam) { tam = t; continue; }
                if (r != 0) { erro = Texto(r); return null; }
                return fmd[..(int)Math.Min(t, tam)];
            }
            erro = "molde maior que o esperado";
            return null;
        }
        catch (Exception ex) { erro = ex.Message; return null; }
    }

    public byte[]? JuntarCadastro(IReadOnlyList<byte[]> fmds, out bool faltaAmostra, out string? erro)
    {
        faltaAmostra = false;
        erro = null;
        var c = Pronta();
        if (c is null) { erro = "runtime nao carregado"; return null; }
        lock (TravaCadastro)    // o cadastro da dpfj e estado global da DLL
        {
            var comecou = false;
            try
            {
                var r = c.IniciarCadastro(FmdIso);
                if (r != 0) { erro = "inicio: " + Texto(r); return null; }
                comecou = true;
                var pronto = false;
                foreach (var f in fmds)
                {
                    r = c.AddCadastro(FmdIso, f, (uint)f.Length, 0);
                    if (r == 0) { pronto = true; break; }
                    if (r == EMaisDados) continue;
                    erro = "amostra: " + Texto(r);
                    return null;
                }
                if (!pronto) { faltaAmostra = true; return null; }
                uint tam = 4096;
                for (var volta = 0; volta < 2; volta++)
                {
                    var molde = new byte[tam];
                    var t = tam;
                    r = c.CriarCadastro(molde, ref t);
                    if (r == EMaisDados && t > tam) { tam = t; continue; }
                    if (r != 0) { erro = "molde: " + Texto(r); return null; }
                    return molde[..(int)Math.Min(t, tam)];
                }
                erro = "molde maior que o esperado";
                return null;
            }
            catch (Exception ex) { erro = ex.Message; return null; }
            finally { if (comecou) { try { c.FimCadastro(); } catch { } } }
        }
    }

    public uint Comparar(byte[] a, byte[] b)
    {
        var c = Pronta();
        if (c is null) return uint.MaxValue;
        try { return c.Comparar(FmdIso, a, (uint)a.Length, 0, FmdIso, b, (uint)b.Length, 0, out var s) == 0 ? s : uint.MaxValue; }
        catch { return uint.MaxValue; }
    }

    /// <summary>O codigo da HID em uma linha: os conhecidos com nome, o resto em hex.</summary>
    public static string Texto(int codigo)
    {
        if (codigo == 0) return "HID ok";
        if ((codigo >> 16) != Facilidade) return $"HID 0x{unchecked((uint)codigo):X8}";
        return (codigo & 0xFFFF) switch
        {
            0x0a => "HID NOT_IMPLEMENTED",
            0x0b => "HID FAILURE",
            0x0c => "HID NO_DATA",
            0x0d => "HID MORE_DATA",
            0x14 => "HID INVALID_PARAMETER",
            0x15 => "HID INVALID_DEVICE (leitor desplugado?)",
            0x1e => "HID DEVICE_BUSY (leitor ocupado por outro programa)",
            0x1f => "HID DEVICE_FAILURE",
            0x65 => "HID INVALID_FID",
            0x66 => "HID TOO_SMALL_AREA (pouco dedo no vidro)",
            0xc9 => "HID INVALID_FMD",
            0x12d => "HID ENROLLMENT_IN_PROGRESS",
            0x12e => "HID ENROLLMENT_NOT_STARTED",
            0x12f => "HID ENROLLMENT_NOT_READY",
            0x130 => "HID ENROLLMENT_INVALID_SET",
            _ => $"HID 0x{unchecked((uint)codigo):X8}",
        };
    }

    /// <summary>Os bits de DPFPDD_QUALITY em palavras.</summary>
    public static string TextoQualidade(uint q)
    {
        if (q == 0) return "boa";
        var partes = new List<string>();
        if ((q & 1) != 0) partes.Add("tempo esgotado");
        if ((q & 2) != 0) partes.Add("cancelada");
        if ((q & 4) != 0) partes.Add("sem dedo");
        if ((q & 8) != 0) partes.Add("dedo falso");
        if ((q & 0x1F0) != 0) partes.Add("dedo fora do centro");
        if ((q & 0x7E00) != 0) partes.Add("passada ruim");
        if ((q & 0x8000) != 0) partes.Add("leitor sujo");
        if ((q & ~0xFFFFu) != 0) partes.Add($"0x{q:X}");
        return string.Join(", ", partes);
    }
}
