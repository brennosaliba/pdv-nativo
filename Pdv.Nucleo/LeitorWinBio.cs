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
/// e USB VID 05BA PID 000A. Sem o driver WBF o Windows o lista como "dispositivo USB" (ou
/// com o driver antigo da DigitalPersona, classe propria) e o WinBio nao o enumera. Aqui
/// lemos o registro (HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_05BA&amp;PID_xxxx): a
/// subchave volatil `Control` so existe com o aparelho presente; a classe "Biometric" e a
/// marca do driver WBF. A regra e pura (Avaliar); o registro e so a leitura.
/// </summary>
public static class DriverDigitalPersona
{
    public const string Vid = "VID_05BA";
    public const string PidUareU4500 = "PID_000A";
    public const string ClasseBiometrica = "Biometric";
    public const string GuidClasseBiometrica = "{53D29EF7-377C-4D14-864B-EB3A85769359}";

    /// <summary>
    /// O passo que a loja faz quando falta o driver (vai no relatorio e na Configuracao).
    /// </summary>
    public const string PassoDoDriver =
        "Instale o driver WBF: Windows Update > Atualizacoes opcionais > Atualizacoes de driver > " +
        "\"DigitalPersona, Inc. - Biometric\" (ou o pacote \"U.are.U WBF driver\" do site da HID Global). Depois reinicie o PC.";

    /// <summary>Um aparelho DigitalPersona visto no registro.</summary>
    public sealed record Aparelho(string Pid, string Classe, bool Presente);

    public sealed record Diagnostico(EstadoLeitor Estado, string Detalhe);

    /// <summary>
    /// A regra: nenhum aparelho = SemLeitor; aparelho presente com classe Biometric = o WBF
    /// existe (se o WinBio nao listou, e o servico); presente sem a classe = SemDriver;
    /// aparelho so no historico (nao presente) = SemLeitor com a dica.
    /// </summary>
    public static Diagnostico Avaliar(IReadOnlyList<Aparelho> aparelhos)
    {
        if (aparelhos.Count == 0) return new Diagnostico(EstadoLeitor.SemLeitor, "");
        var presentes = aparelhos.Where(a => a.Presente).ToList();
        if (presentes.Count == 0)
            return new Diagnostico(EstadoLeitor.SemLeitor, "o leitor DigitalPersona ja esteve neste PC, mas nao esta plugado agora");
        if (presentes.Any(a => string.Equals(a.Classe, ClasseBiometrica, StringComparison.OrdinalIgnoreCase)))
            return new Diagnostico(EstadoLeitor.SemServico, "leitor DigitalPersona plugado com driver WBF, mas o Windows nao o listou: confira o servico Windows Biometric Service");
        return new Diagnostico(EstadoLeitor.SemDriver,
            $"leitor DigitalPersona plugado ({presentes[0].Pid}, classe \"{presentes[0].Classe}\"), sem o driver WBF. " + PassoDoDriver);
    }

    /// <summary>Le o registro desta maquina. Nunca lanca; fora do Windows, lista vazia.</summary>
    public static Diagnostico Ler() => Avaliar(LerAparelhos());

    [SupportedOSPlatform("windows")]
    private static List<Aparelho> LerAparelhosWindows()
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
                    saida.Add(new Aparelho(pid, classe, presente));
                }
            }
        }
        catch { /* sem direito de ler o registro: diagnostico vazio, nunca excecao */ }
        return saida;
    }

    public static List<Aparelho> LerAparelhos()
        => OperatingSystem.IsWindows() ? LerAparelhosWindows() : new List<Aparelho>();

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
