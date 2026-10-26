using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Pdv.Nucleo;

/// <summary>
/// O LEITOR PELO WINDOWS BIOMETRIC FRAMEWORK (winbio.dll, P/Invoke, 1.0.52).
///
/// Por que o WBF e nao o SDK da DigitalPersona: o SDK e pago e preso ao fabricante; o WBF
/// vem com o Windows 10/11, atende o U.are.U 4500 com o driver WBF que o Windows Update
/// entrega ("DigitalPersona U.are.U 4500 Fingerprint Reader"), e guarda os moldes no
/// mesmo cofre do Windows Hello. O caixa nunca ve a digital: pede ao Windows "quem e?" e
/// recebe uma identidade (a conta do Windows) mais o DEDO (1..10). Como todos os
/// funcionarios usam a mesma conta do Windows do caixa, e o dedo que distingue um do
/// outro: cada funcionario ocupa uma "vaga" (um dedo da conta), dez por caixa.
///
/// Pool do SISTEMA de proposito (WINBIO_POOL_SYSTEM): o pool privado exige registrar um
/// banco de moldes proprio no registro, com elevacao, e nem todo driver o atende; o do
/// sistema funciona com o driver de caixa. Se o modelo exigir o SDK, outra classe entra no
/// lugar desta (ILeitorDigital), sem mexer na maquina de estados.
///
/// Tudo que encosta em winbio.dll roda numa thread de trabalho (as chamadas bloqueiam ate
/// o dedo chegar) e cancela por WinBioCancel. Nenhum HRESULT vira excecao para fora:
/// vira Captura/Cadastro/Sonda com o codigo no motivo.
/// </summary>
public sealed class LeitorWinBio : ILeitorDigital
{
    public string Nome => "WinBio";

    // ── as constantes do winbio.h que usamos ──────────────────────────────────────
    private const uint WINBIO_TYPE_FINGERPRINT = 0x00000008;
    private const uint WINBIO_POOL_SYSTEM = 1;
    private const uint WINBIO_FLAG_DEFAULT = 0;
    private const byte WINBIO_SUBTYPE_NO_INFORMATION = 0;
    private const int S_OK = 0;
    private const int WINBIO_I_MORE_DATA = 0x00090001;
    private const uint WINBIO_E_UNKNOWN_ID = 0x80098003;
    private const uint WINBIO_E_CANCELED = 0x80098004;
    private const uint WINBIO_E_NO_MATCH = 0x80098005;
    private const uint WINBIO_E_CAPTURE_ABORTED = 0x80098006;
    private const uint WINBIO_E_BAD_CAPTURE = 0x80098008;
    private const uint WINBIO_E_DEVICE_BUSY = 0x8009800F;

    /// <summary>Leituras do cadastro, no maximo: o Windows costuma pedir 3 a 8 no 4500.</summary>
    private const int MaxLeiturasDoCadastro = 12;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINBIO_UNIT_SCHEMA
    {
        public uint UnitId, PoolType, BiometricFactor, SensorSubType, Capabilities;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DeviceInstanceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Manufacturer;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Model;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string SerialNumber;
        public uint FirmwareMajor, FirmwareMinor;
    }

    /// <summary>WINBIO_IDENTITY: Type (3 = SID da conta) e a uniao de 72 bytes (Size + Data[68]).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WINBIO_IDENTITY
    {
        public uint Type;
        public uint Size;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 68)] public byte[] Data;
    }

    [DllImport("winbio.dll")] private static extern int WinBioEnumBiometricUnits(uint factor, out IntPtr unitSchemaArray, out IntPtr unitCount);
    [DllImport("winbio.dll")] private static extern int WinBioFree(IntPtr address);
    [DllImport("winbio.dll")] private static extern int WinBioOpenSession(uint factor, uint poolType, uint flags, IntPtr unitArray, IntPtr unitCount, IntPtr databaseId, out uint sessionHandle);
    [DllImport("winbio.dll")] private static extern int WinBioCloseSession(uint sessionHandle);
    [DllImport("winbio.dll")] private static extern int WinBioCancel(uint sessionHandle);
    [DllImport("winbio.dll")] private static extern int WinBioIdentify(uint sessionHandle, out uint unitId, ref WINBIO_IDENTITY identity, out byte subFactor, out uint rejectDetail);
    [DllImport("winbio.dll")] private static extern int WinBioEnrollBegin(uint sessionHandle, byte subFactor, uint unitId);
    [DllImport("winbio.dll")] private static extern int WinBioEnrollCapture(uint sessionHandle, out uint rejectDetail);
    [DllImport("winbio.dll")] private static extern int WinBioEnrollCommit(uint sessionHandle, ref WINBIO_IDENTITY identity, out byte isNewTemplate);
    [DllImport("winbio.dll")] private static extern int WinBioEnrollDiscard(uint sessionHandle);
    [DllImport("winbio.dll")] private static extern int WinBioDeleteTemplate(uint sessionHandle, uint unitId, ref WINBIO_IDENTITY identity, byte subFactor);

    /// <summary>Uma unidade que o Windows listou.</summary>
    public sealed record Unidade(uint Id, string Descricao, string Fabricante, string Modelo, string Instancia)
    {
        public override string ToString() => $"#{Id} {Descricao}".Trim() + (string.IsNullOrWhiteSpace(Fabricante) ? "" : $" ({Fabricante} {Modelo})".TrimEnd());
    }

    /// <summary>
    /// A ENUMERACAO, sem sessao: e o que prova que o P/Invoke carrega e e o que o diagnostico
    /// da Configuracao mostra. Nunca lanca. Sem leitor = lista vazia (hr 0 ou o codigo do
    /// Windows no segundo valor).
    /// </summary>
    public static (IReadOnlyList<Unidade> Unidades, int Hr, string? Erro) Enumerar()
    {
        if (!OperatingSystem.IsWindows()) return (Array.Empty<Unidade>(), -1, "nao e Windows");
        var saida = new List<Unidade>();
        IntPtr arr = IntPtr.Zero;
        try
        {
            var hr = WinBioEnumBiometricUnits(WINBIO_TYPE_FINGERPRINT, out arr, out var n);
            if (hr != S_OK) return (saida, hr, Texto(hr));
            var tamanho = Marshal.SizeOf<WINBIO_UNIT_SCHEMA>();
            for (var i = 0; i < (long)n; i++)
            {
                var u = Marshal.PtrToStructure<WINBIO_UNIT_SCHEMA>(arr + i * tamanho);
                saida.Add(new Unidade(u.UnitId, u.Description ?? "", u.Manufacturer ?? "", u.Model ?? "", u.DeviceInstanceId ?? ""));
            }
            return (saida, S_OK, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (saida, -2, "winbio.dll nao carregou: " + ex.GetType().Name);
        }
        catch (Exception ex) { return (saida, -3, ex.Message); }
        finally { if (arr != IntPtr.Zero) { try { WinBioFree(arr); } catch { } } }
    }

    public Task<SondaLeitor> SondarAsync(CancellationToken ct) => Task.Run(() =>
    {
        var (unidades, hr, erro) = Enumerar();
        var nomes = unidades.Select(u => u.ToString()).ToList();
        if (unidades.Count > 0) return new SondaLeitor(EstadoLeitor.Pronto, $"{unidades.Count} leitor(es): {string.Join("; ", nomes)}", nomes);
        if (hr != S_OK && hr != unchecked((int)0x80098001) /* WINBIO_E_UNSUPPORTED_FACTOR */)
            return new SondaLeitor(EstadoLeitor.SemServico, erro ?? Texto(hr), nomes);
        var usb = DriverDigitalPersona.Ler();
        return usb.Estado == EstadoLeitor.SemDriver
            ? new SondaLeitor(EstadoLeitor.SemDriver, usb.Detalhe, nomes)
            : new SondaLeitor(EstadoLeitor.SemLeitor, "nenhum leitor de digital no Windows" + (usb.Detalhe.Length > 0 ? " (" + usb.Detalhe + ")" : ""), nomes);
    }, ct);

    private readonly object _trava = new();
    private uint _sessaoViva;

    /// <summary>Abre a sessao do pool do sistema. Devolve 0 e o hr quando nao abre.</summary>
    private static (uint Sessao, int Hr) Abrir()
    {
        var hr = WinBioOpenSession(WINBIO_TYPE_FINGERPRINT, WINBIO_POOL_SYSTEM, WINBIO_FLAG_DEFAULT, IntPtr.Zero, IntPtr.Zero,
            new IntPtr(1) /* WINBIO_DB_DEFAULT */, out var s);
        return hr == S_OK ? (s, S_OK) : (0, hr);
    }

    private void Guardar(uint sessao) { lock (_trava) _sessaoViva = sessao; }

    private void Fechar(uint sessao)
    {
        lock (_trava) if (_sessaoViva == sessao) _sessaoViva = 0;
        try { WinBioCloseSession(sessao); } catch { }
    }

    /// <summary>O cancelamento vem de outra thread (o botao da tela): WinBioCancel acorda a chamada presa.</summary>
    private void Cancelar()
    {
        uint s;
        lock (_trava) s = _sessaoViva;
        if (s != 0) { try { WinBioCancel(s); } catch { } }
    }

    public Task<Captura> IdentificarAsync(CancellationToken ct) => Task.Run(() =>
    {
        if (!OperatingSystem.IsWindows()) return Captura.Falhou("nao e Windows");
        var (sessao, hrAbrir) = Abrir();
        if (sessao == 0) return hrAbrir == unchecked((int)0x80098001) ? new Captura(ResultadoCaptura.SemLeitor) : Captura.Falhou(Texto(hrAbrir));
        Guardar(sessao);
        using var reg = ct.Register(Cancelar);
        try
        {
            var id = new WINBIO_IDENTITY { Data = new byte[68] };
            var hr = WinBioIdentify(sessao, out _, ref id, out var dedo, out var rejeicao);
            if (ct.IsCancellationRequested) return Captura.Cancelada;
            if (hr == S_OK) return Captura.Reconhecida(Chave(id, dedo));
            return unchecked((uint)hr) switch
            {
                WINBIO_E_UNKNOWN_ID or WINBIO_E_NO_MATCH => Captura.NaoReconhecida,
                WINBIO_E_BAD_CAPTURE => new Captura(ResultadoCaptura.NaoReconhecido, null, "leitura ruim " + Rejeicao(rejeicao)),
                WINBIO_E_CANCELED or WINBIO_E_CAPTURE_ABORTED => Captura.Cancelada,
                _ => Captura.Falhou(Texto(hr)),
            };
        }
        catch (Exception ex) { return Captura.Falhou(ex.Message); }
        finally { Fechar(sessao); }
    }, ct);

    public Task<Cadastro> CadastrarAsync(int vaga, IProgress<int>? amostra, CancellationToken ct) => Task.Run(() =>
    {
        if (!OperatingSystem.IsWindows()) return Cadastro.Falhou("nao e Windows");
        if (vaga is < 1 or > Biometria.Vagas) return Cadastro.Falhou($"vaga {vaga} fora de 1..{Biometria.Vagas}");
        var (unidades, _, erro) = Enumerar();
        if (unidades.Count == 0) return new Cadastro(ResultadoCadastro.SemLeitor, null, 0, erro);
        var (sessao, hrAbrir) = Abrir();
        if (sessao == 0) return Cadastro.Falhou(Texto(hrAbrir));
        Guardar(sessao);
        using var reg = ct.Register(Cancelar);
        var comecou = false;
        try
        {
            var hr = WinBioEnrollBegin(sessao, (byte)vaga, unidades[0].Id);
            if (hr != S_OK) return Cadastro.Falhou("inicio do cadastro: " + Texto(hr));
            comecou = true;
            var aceitas = 0;
            for (var leitura = 1; leitura <= MaxLeiturasDoCadastro; leitura++)
            {
                hr = WinBioEnrollCapture(sessao, out var rejeicao);
                if (ct.IsCancellationRequested) return Cadastro.Cancelado;
                if (hr == S_OK || hr == WINBIO_I_MORE_DATA)
                {
                    if (rejeicao == 0) { aceitas++; amostra?.Report(aceitas); }
                    // o dono pediu 3 leituras: mesmo que o Windows se de por satisfeito antes, pedimos as 3
                    if (hr == S_OK && aceitas >= Biometria.LeiturasDoCadastro) break;
                    continue;
                }
                var u = unchecked((uint)hr);
                if (u is WINBIO_E_CANCELED or WINBIO_E_CAPTURE_ABORTED) return Cadastro.Cancelado;
                if (u is WINBIO_E_BAD_CAPTURE) continue;        // dedo mal encostado: tenta de novo
                return Cadastro.Falhou("leitura: " + Texto(hr));
            }
            var id = new WINBIO_IDENTITY { Data = new byte[68] };
            hr = WinBioEnrollCommit(sessao, ref id, out _);
            if (hr != S_OK) return Cadastro.Falhou("gravacao: " + Texto(hr));
            comecou = false;
            return Cadastro.Feito(Chave(id, (byte)vaga), aceitas);
        }
        catch (Exception ex) { return Cadastro.Falhou(ex.Message); }
        finally
        {
            if (comecou) { try { WinBioEnrollDiscard(sessao); } catch { } }
            Fechar(sessao);
        }
    }, ct);

    public Task<bool> ApagarAsync(string chave, CancellationToken ct) => Task.Run(() =>
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!LerChave(chave, out var id, out var dedo)) return false;
        var (unidades, _, _) = Enumerar();
        if (unidades.Count == 0) return false;
        var (sessao, _) = Abrir();
        if (sessao == 0) return false;
        try { return WinBioDeleteTemplate(sessao, unidades[0].Id, ref id, dedo) == S_OK; }
        catch { return false; }
        finally { Fechar(sessao); }
    }, ct);

    // ── a chave: identidade do Windows + dedo, em texto ──────────────────────────
    /// <summary>"winbio:3:&lt;hex do SID&gt;:dedo:7". O tipo e o tamanho entram para a volta ser exata.</summary>
    private static string Chave(WINBIO_IDENTITY id, byte dedo)
    {
        var n = (int)Math.Min(id.Size, 68u);
        var dados = id.Type == 3 /* SID */ && n > 0 ? id.Data[..n] : id.Data;
        return $"winbio:{id.Type}:{id.Size}:{Convert.ToHexString(dados).ToLowerInvariant()}:dedo:{dedo}";
    }

    private static bool LerChave(string chave, out WINBIO_IDENTITY id, out byte dedo)
    {
        id = new WINBIO_IDENTITY { Data = new byte[68] };
        dedo = 0;
        var p = (chave ?? "").Split(':');
        if (p.Length != 6 || p[0] != "winbio" || p[4] != "dedo") return false;
        if (!uint.TryParse(p[1], out id.Type) || !uint.TryParse(p[2], out id.Size) || !byte.TryParse(p[5], out dedo)) return false;
        try
        {
            var bytes = Convert.FromHexString(p[3]);
            Array.Copy(bytes, id.Data, Math.Min(bytes.Length, 68));
            return true;
        }
        catch { return false; }
    }

    /// <summary>A chave sem o SID inteiro, para diagnostico: "winbio dedo 7".</summary>
    public static string ChaveCurta(string chave)
    {
        var p = (chave ?? "").Split(':');
        return p.Length == 6 ? $"winbio dedo {p[5]}" : "?";
    }

    private static string Rejeicao(uint r) => r switch
    {
        1 => "(muito alto)", 2 => "(muito baixo)", 3 => "(muito a esquerda)", 4 => "(muito a direita)",
        5 => "(muito rapido)", 6 => "(muito devagar)", 7 => "(pouca qualidade)", 8 => "(muito torto)",
        9 => "(muito curto)", 10 => "(juntou dedos)", _ => "",
    };

    /// <summary>O codigo do Windows em uma linha: os conhecidos com nome, o resto em hex.</summary>
    public static string Texto(int hr) => unchecked((uint)hr) switch
    {
        0x80098001 => "WinBio: sem leitor de digital (WINBIO_E_UNSUPPORTED_FACTOR)",
        0x80098002 => "WinBio: unidade invalida",
        WINBIO_E_UNKNOWN_ID => "WinBio: digital desconhecida",
        WINBIO_E_CANCELED => "WinBio: cancelado",
        WINBIO_E_NO_MATCH => "WinBio: nao reconheceu",
        WINBIO_E_CAPTURE_ABORTED => "WinBio: leitura interrompida",
        0x80098007 => "WinBio: outro cadastro em andamento",
        WINBIO_E_BAD_CAPTURE => "WinBio: leitura ruim",
        WINBIO_E_DEVICE_BUSY => "WinBio: leitor ocupado",
        0x80070005 => "WinBio: acesso negado (politica do Windows ou sem direito na conta)",
        0x80070422 => "WinBio: o servico de biometria do Windows esta desativado",
        0x800706D9 or 0x800706BA => "WinBio: o servico de biometria do Windows nao esta rodando",
        _ => $"WinBio 0x{unchecked((uint)hr):X8}",
    };
}

/// <summary>
/// O LEITOR ESTA PLUGADO MAS O WINDOWS NAO O ENXERGA? (pedido do dono, 26/10): o U.are.U 4500
/// e USB VID 05BA PID 000A. Sem o driver WBF o Windows o lista como "dispositivo USB" ou com o
/// driver da HID que nao e WBF (o "Non-WBF" ou o que vem com o U.are.U RTE/SDK), e o WinBio nao
/// o enumera. Aqui lemos o registro (HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_05BA&amp;PID_xxxx):
/// a subchave volatil `Control` so existe com o aparelho presente; a classe e o servico (o
/// driver) dizem qual driver esta la. WBF = classe Biometric com o servico WUDFRd (UMDF). A
/// regra e pura (Avaliar); o registro e so a leitura. 1.0.53: o servico, a descricao, a versao
/// e o fornecedor do driver entram no diagnostico.
/// </summary>
public static class DriverDigitalPersona
{
    public const string Vid = "VID_05BA";
    public const string PidUareU4500 = "PID_000A";
    public const string ClasseBiometrica = "Biometric";
    public const string GuidClasseBiometrica = "{53D29EF7-377C-4D14-864B-EB3A85769359}";
    /// <summary>O servico do driver WBF da HID (UMDF). Outro servico na classe Biometric = driver que o WinBio nao usa.</summary>
    public const string ServicoWbf = "WUDFRd";

    /// <summary>
    /// O passo que a loja faz quando falta o driver (vai no relatorio e na Configuracao).
    /// </summary>
    public const string PassoDoDriver =
        "Passo: instale o \"HID DigitalPersona 4500 WBF (Hello) Driver\" (site da HID, hidglobal.com, Drivers; " +
        "ou Windows Update > Atualizações opcionais > Atualizações de driver) OU o U.are.U RTE de 64 bits. Depois reinicie o PC.";

    /// <summary>
    /// Um aparelho DigitalPersona visto no registro. <paramref name="Servico"/> e o driver
    /// (WUDFRd = WBF); versao e fornecedor vem da chave da classe do driver.
    /// </summary>
    public sealed record Aparelho(string Pid, string Classe, bool Presente, string Servico = "", string Descricao = "",
        string VersaoDriver = "", string Fornecedor = "", string InstanciaId = "");

    public sealed record Diagnostico(EstadoLeitor Estado, string Detalhe);

    /// <summary>Classe Biometric com o servico WUDFRd (ou servico nao lido): o driver WBF.</summary>
    public static bool EhWbf(Aparelho a)
        => string.Equals(a.Classe, ClasseBiometrica, StringComparison.OrdinalIgnoreCase)
           && (a.Servico.Length == 0 || string.Equals(a.Servico, ServicoWbf, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// O driver PROPRIO da HID (nao WBF): na loja (26/10) o Get-PnpDevice mostrou classe
    /// "Authentication Devices", status OK. Com ele o WinBio nao ve nada; quem fala com o
    /// leitor e o runtime da HID (dpfpdd/dpfj). Classe Biometric com servico que nao e o
    /// WUDFRd tambem conta (o driver antigo da DigitalPersona).
    /// </summary>
    public static bool EhDriverDaHid(Aparelho a)
        => a.Classe.Contains("Authentication", StringComparison.OrdinalIgnoreCase)
           || (string.Equals(a.Classe, ClasseBiometrica, StringComparison.OrdinalIgnoreCase) && !EhWbf(a));

    /// <summary>
    /// A regra: nenhum aparelho = SemLeitor; aparelho presente com o driver WBF (classe
    /// Biometric, servico WUDFRd ou nao lido) = o WBF existe (se o WinBio nao listou, e o
    /// servico); presente com outro driver = SemDriver; so no historico = SemLeitor com a dica.
    /// </summary>
    public static Diagnostico Avaliar(IReadOnlyList<Aparelho> aparelhos)
    {
        if (aparelhos.Count == 0) return new Diagnostico(EstadoLeitor.SemLeitor, "");
        var presentes = aparelhos.Where(a => a.Presente).ToList();
        if (presentes.Count == 0)
            return new Diagnostico(EstadoLeitor.SemLeitor, "o leitor DigitalPersona ja esteve neste PC, mas nao esta plugado agora");
        if (presentes.Any(EhWbf))
            return new Diagnostico(EstadoLeitor.SemServico, "leitor DigitalPersona plugado com driver WBF, mas o Windows nao o listou: confira o servico Windows Biometric Service");
        var p = presentes[0];
        if (presentes.FirstOrDefault(EhDriverDaHid) is { } daHid)
            return new Diagnostico(EstadoLeitor.SemDriver,
                $"leitor DigitalPersona plugado com o driver da HID ({daHid.Pid}, classe \"{daHid.Classe}\"" + (daHid.Servico.Length > 0 ? $", driver {daHid.Servico}" : "") +
                "), que nao e o WBF: o Windows nao o usa, quem le e o runtime da HID (U.are.U RTE).");
        return new Diagnostico(EstadoLeitor.SemDriver,
            $"leitor DigitalPersona plugado ({p.Pid}, classe \"{p.Classe}\"" + (p.Servico.Length > 0 ? $", driver {p.Servico}" : "") +
            "), sem o driver WBF. " + PassoDoDriver);
    }

    /// <summary>Uma entrada USB em uma linha: "PID_000A, classe Biometric, driver WUDFRd 5.0.0.5 (HID Global), "U.are.U 4500", plugado".</summary>
    public static string Linha(Aparelho a)
    {
        var driver = (a.Servico.Length == 0 && a.VersaoDriver.Length == 0 ? "driver nao informado" : "driver " + (a.Servico.Length > 0 ? a.Servico : "?"))
            + (a.VersaoDriver.Length > 0 ? " " + a.VersaoDriver : "")
            + (a.Fornecedor.Length > 0 ? $" ({a.Fornecedor})" : "");
        return $"{a.Pid}, classe {(a.Classe.Length > 0 ? a.Classe : "?")}, {driver}"
               + (a.Descricao.Length > 0 ? $", \"{a.Descricao}\"" : "")
               + (a.Presente ? ", plugado" : ", desplugado")
               + (a.InstanciaId.Length > 0 ? $", id {a.InstanciaId}" : "");
    }

    /// <summary>Le o registro desta maquina. Nunca lanca; fora do Windows, lista vazia.</summary>
    public static Diagnostico Ler() => Avaliar(LerAparelhos());

    /// <summary>"@oem12.inf,%desc%;U.are.U 4500" vira "U.are.U 4500".</summary>
    private static string Legivel(object? v)
    {
        var s = v as string ?? "";
        var i = s.LastIndexOf(';');
        return (i >= 0 ? s[(i + 1)..] : s).Trim();
    }

    [SupportedOSPlatform("windows")]
    private static List<Aparelho> LerDoRegistro()
    {
        var saida = new List<Aparelho>();
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb is null) return saida;
            foreach (var nome in usb.GetSubKeyNames())
            {
                if (!nome.StartsWith(Vid, StringComparison.OrdinalIgnoreCase)) continue;
                using var dev = usb.OpenSubKey(nome);
                if (dev is null) continue;
                var pid = nome.Split('&').FirstOrDefault(x => x.StartsWith("PID_", StringComparison.OrdinalIgnoreCase)) ?? nome;
                foreach (var inst in dev.GetSubKeyNames())
                {
                    using var k = dev.OpenSubKey(inst);
                    if (k is null) continue;
                    var classe = k.GetValue("Class") as string ?? "";
                    if (classe.Length == 0 && string.Equals(k.GetValue("ClassGUID") as string, GuidClasseBiometrica, StringComparison.OrdinalIgnoreCase))
                        classe = ClasseBiometrica;
                    bool presente;
                    using (var c = k.OpenSubKey("Control")) presente = c is not null;
                    var servico = k.GetValue("Service") as string ?? "";
                    var descricao = Legivel(k.GetValue("FriendlyName") ?? k.GetValue("DeviceDesc"));
                    string versao = "", fornecedor = "";
                    try
                    {
                        // "Driver" = "{guid da classe}\0003": a chave do driver instalado
                        if (k.GetValue("Driver") is string drv && drv.Length > 0)
                        {
                            using var kd = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + drv);
                            versao = kd?.GetValue("DriverVersion") as string ?? "";
                            fornecedor = kd?.GetValue("ProviderName") as string ?? "";
                        }
                    }
                    catch { }
                    saida.Add(new Aparelho(pid, classe, presente, servico, descricao, versao, fornecedor, "USB\\" + nome + "\\" + inst));
                }
            }
        }
        catch { /* sem direito de ler o registro: diagnostico vazio, nunca excecao */ }
        return saida;
    }

    /// <summary>
    /// Os aparelhos 05BA desta maquina. PLUGADO vem do SetupAPI (SetupDiGetClassDevs com
    /// DIGCF_PRESENT), a mesma fonte do Gerenciador de Dispositivos e do Get-PnpDevice, com a
    /// classe real (Biometric, Authentication Devices). O registro so completa versao e
    /// fornecedor do driver e lista o que ja esteve aqui. 1.0.53: na loja (26/10) o leitor
    /// estava plugado, Status OK, e a 1.0.52 dizia "ja esteve aqui, mas nao esta": a subchave
    /// volatil Control nao aparecia. Ela so vale agora se o SetupAPI nao responder.
    /// </summary>
    public static List<Aparelho> LerAparelhos()
    {
        if (!OperatingSystem.IsWindows()) return new List<Aparelho>();
        var registro = LerDoRegistro();
        var presentes = LerPresentes();
        return presentes is null ? registro : Juntar(registro, presentes);
    }

    /// <summary>Quantos aparelhos presentes o SetupAPI lista nesta maquina (qualquer fabricante): prova que a chamada funciona. -1 = nao respondeu.</summary>
    /// <summary>Os presentes pelo SetupAPI com o filtro no id da instancia (o --sondar-biometria VID_xxxx usa, para conferir a leitura em qualquer aparelho).</summary>
    public static IReadOnlyList<Aparelho> PresentesPeloSetupApi(string filtro)
        => OperatingSystem.IsWindows() ? (IReadOnlyList<Aparelho>?)LerPresentes(filtro) ?? Array.Empty<Aparelho>() : Array.Empty<Aparelho>();

    public static int ContarPresentesNoSistema()
        => OperatingSystem.IsWindows() ? (LerPresentes("\\")?.Count ?? -1) : -1;

    /// <summary>
    /// A regra da mescla (pura, a suite prova): todo aparelho que o SetupAPI diz presente entra
    /// como plugado, com a classe e o driver que ele diz (o registro completa o que faltar,
    /// pelo id da instancia); o que so o registro conhece entra como desplugado.
    /// </summary>
    public static List<Aparelho> Juntar(IReadOnlyList<Aparelho> doRegistro, IReadOnlyList<Aparelho> presentes)
    {
        var saida = new List<Aparelho>();
        foreach (var p in presentes)
        {
            var r = doRegistro.FirstOrDefault(x => string.Equals(x.InstanciaId, p.InstanciaId, StringComparison.OrdinalIgnoreCase));
            saida.Add(p with
            {
                Presente = true,
                Classe = p.Classe.Length > 0 ? p.Classe : r?.Classe ?? "",
                Servico = p.Servico.Length > 0 ? p.Servico : r?.Servico ?? "",
                Descricao = p.Descricao.Length > 0 ? p.Descricao : r?.Descricao ?? "",
                VersaoDriver = p.VersaoDriver.Length > 0 ? p.VersaoDriver : r?.VersaoDriver ?? "",
                Fornecedor = p.Fornecedor.Length > 0 ? p.Fornecedor : r?.Fornecedor ?? "",
            });
        }
        foreach (var r in doRegistro)
            if (!presentes.Any(p => string.Equals(p.InstanciaId, r.InstanciaId, StringComparison.OrdinalIgnoreCase)))
                saida.Add(r with { Presente = false });
        return saida;
    }

    // ── SetupAPI (setupapi.dll): os aparelhos PRESENTES ─────────────────────────────
    private const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;
    private const uint SPDRP_DEVICEDESC = 0x0, SPDRP_SERVICE = 0x4, SPDRP_CLASS = 0x7, SPDRP_DRIVER = 0x9, SPDRP_FRIENDLYNAME = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public uint CbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint indice, ref SP_DEVINFO_DATA dado);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA dado, char[] id, int tamanho, out int necessario);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA dado, uint prop, out uint tipo, byte[]? buf, int tamanho, out int necessario);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    private static string Propriedade(IntPtr set, ref SP_DEVINFO_DATA d, uint prop)
    {
        var buf = new byte[1024];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref d, prop, out _, buf, buf.Length, out var n) || n <= 0) return "";
        return System.Text.Encoding.Unicode.GetString(buf, 0, Math.Min(n, buf.Length)).TrimEnd('\0').Trim();
    }

    /// <summary>Os 05BA presentes pelo SetupAPI. null = o SetupAPI nao respondeu (cai no registro).</summary>
    [SupportedOSPlatform("windows")]
    private static List<Aparelho>? LerPresentes(string filtro = Vid)
    {
        IntPtr set = new(-1);
        try
        {
            set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == new IntPtr(-1)) return null;
            var saida = new List<Aparelho>();
            var d = new SP_DEVINFO_DATA { CbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var id = new char[512];
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
            {
                if (!SetupDiGetDeviceInstanceIdW(set, ref d, id, id.Length, out _)) continue;
                var inst = new string(id).TrimEnd('\0');
                var fim = inst.IndexOf('\0');
                if (fim >= 0) inst = inst[..fim];
                if (inst.IndexOf(filtro, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var pid = inst.Split('\\', '&').FirstOrDefault(x => x.StartsWith("PID_", StringComparison.OrdinalIgnoreCase)) ?? inst;
                var desc = Propriedade(set, ref d, SPDRP_FRIENDLYNAME);
                if (desc.Length == 0) desc = Propriedade(set, ref d, SPDRP_DEVICEDESC);
                string versao = "", fornecedor = "";
                var drv = Propriedade(set, ref d, SPDRP_DRIVER);
                if (drv.Length > 0)
                {
                    try
                    {
                        using var kd = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + drv);
                        versao = kd?.GetValue("DriverVersion") as string ?? "";
                        fornecedor = kd?.GetValue("ProviderName") as string ?? "";
                    }
                    catch { }
                }
                saida.Add(new Aparelho(pid.ToUpperInvariant(), Propriedade(set, ref d, SPDRP_CLASS), true, Propriedade(set, ref d, SPDRP_SERVICE),
                    desc, versao, fornecedor, inst));
            }
            return saida;
        }
        catch { return null; }
        finally { if (set != new IntPtr(-1) && set != IntPtr.Zero) { try { SetupDiDestroyDeviceInfoList(set); } catch { } } }
    }

    /// <summary>
    /// As linhas da Configuracao: as unidades do WinBio e, sem nenhuma, o que o registro diz
    /// do leitor USB. E o que o dono manda para o suporte.
    /// </summary>
    public static IReadOnlyList<string> LinhasDeDiagnostico(IReadOnlyList<string> unidadesWinBio, int hrEnumeracao, string? erroEnumeracao,
        IReadOnlyList<Aparelho> aparelhos)
    {
        var linhas = new List<string>();
        if (unidadesWinBio.Count > 0)
        {
            linhas.Add($"Leitor pronto. Unidades do Windows: {unidadesWinBio.Count}.");
            linhas.AddRange(unidadesWinBio.Select(u => "  " + u));
            return linhas;
        }
        linhas.Add(hrEnumeracao == 0 ? "Nenhuma unidade de digital no Windows." : $"O Windows nao listou leitores ({erroEnumeracao}).");
        var d = Avaliar(aparelhos);
        if (d.Detalhe.Length > 0) linhas.Add(d.Estado == EstadoLeitor.SemDriver ? "Leitor encontrado, falta o driver do Windows (DigitalPersona WBF)." : d.Detalhe);
        if (d.Estado == EstadoLeitor.SemDriver) linhas.Add(PassoDoDriver);
        if (aparelhos.Count == 0) linhas.Add("Nenhum leitor DigitalPersona (USB 05BA) apareceu neste PC. Plugue o leitor e espere o Windows instalar o driver.");
        return linhas;
    }
}
