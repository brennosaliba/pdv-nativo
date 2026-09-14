using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Pdv.Nucleo;

/// <summary>
/// As portas seriais DESTE computador, lidas do Windows, e a abertura de uma delas para o
/// teste do pinpad. Sem programa externo e sem pacote novo: registro para a lista, Win32
/// (CreateFile, SetCommState, WriteFile, ReadFile) para a conversa.
///
/// De onde vem cada coisa:
///  · as portas que existem agora: HKLM\HARDWARE\DEVICEMAP\SERIALCOMM (a mesma lista que o
///    Gerenciador de Dispositivos mostra em "Portas (COM e LPT)");
///  · o nome do aparelho: o FriendlyName do dispositivo cujo "Device Parameters\PortName" é
///    aquela COM ("Gertec PIN Pad PPC (COM3)", medido em 09/09/2026);
///  · a conversa: 19200 8N1, como o protocolo ABECS do pinpad pede. Em USB a velocidade nem
///    é usada, mas numa porta serial de verdade é ela que faz o pinpad entender o sinal.
///
/// A bateria nunca instancia esta classe: ela simula <see cref="IAcessoSerial"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SerialWindows : IAcessoSerial
{
    public IReadOnlyList<PortaSerial> Listar()
    {
        var coms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (k is not null)
                foreach (var v in k.GetValueNames())
                    if (k.GetValue(v) is string com && com.Trim().StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                        coms.Add(com.Trim().ToUpperInvariant());
        }
        catch { /* sem leitura do registro: lista vazia, e a tela diz que não achou pinpad */ }

        var nomes = NomesAmigaveis(coms);
        return coms
            .Select(c => new PortaSerial(c, nomes.GetValueOrDefault(c, "")))
            .OrderBy(p => int.TryParse(p.Numero, out var n) ? n : int.MaxValue)
            .ToList();
    }

    private static Dictionary<string, string> NomesAmigaveis(IReadOnlySet<string> coms)
    {
        var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (coms.Count == 0) return r;
        try
        {
            using var raiz = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
            if (raiz is null) return r;
            foreach (var enumerador in raiz.GetSubKeyNames())
            {
                using var e = Sub(raiz, enumerador);
                if (e is null) continue;
                foreach (var dispositivo in e.GetSubKeyNames())
                {
                    using var d = Sub(e, dispositivo);
                    if (d is null) continue;
                    foreach (var instancia in d.GetSubKeyNames())
                    {
                        using var i = Sub(d, instancia);
                        using var p = i is null ? null : Sub(i, "Device Parameters");
                        if (i is null || p?.GetValue("PortName") is not string porta) continue;
                        porta = porta.Trim().ToUpperInvariant();
                        if (!coms.Contains(porta) || r.ContainsKey(porta)) continue;
                        var nome = i.GetValue("FriendlyName") as string ?? i.GetValue("DeviceDesc") as string ?? "";
                        r[porta] = LimparNome(nome, porta);
                        if (r.Count == coms.Count) return r;
                    }
                }
            }
        }
        catch { /* nome é conforto: sem ele a porta aparece só com o número */ }
        return r;
    }

    private static RegistryKey? Sub(RegistryKey pai, string nome)
    {
        try { return pai.OpenSubKey(nome); }
        catch { return null; }
    }

    /// <summary>"@oem12.inf,%x%;Gertec PIN Pad PPC (COM3)" vira "Gertec PIN Pad PPC".</summary>
    public static string LimparNome(string? nome, string com)
    {
        var n = (nome ?? "").Trim();
        var ponto = n.LastIndexOf(';');
        if (n.StartsWith('@') && ponto >= 0) n = n[(ponto + 1)..].Trim();
        var sufixo = $"({com})";
        if (n.EndsWith(sufixo, StringComparison.OrdinalIgnoreCase)) n = n[..^sufixo.Length].Trim();
        return n;
    }

    public AberturaPorta Abrir(string com, out IPortaAberta? porta)
    {
        porta = null;
        var h = CreateFileW(@"\\.\" + com.Trim(), GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var erro = Marshal.GetLastWin32Error();
            h.Dispose();
            return erro switch
            {
                ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND => AberturaPorta.NaoExiste,
                ERROR_ACCESS_DENIED or ERROR_SHARING_VIOLATION => AberturaPorta.Ocupada,
                _ => AberturaPorta.Falhou,
            };
        }
        try
        {
            SetupComm(h, 1024, 1024);
            var dcb = new DCB { DCBlength = (uint)Marshal.SizeOf<DCB>() };
            if (GetCommState(h, ref dcb))
            {
                dcb.BaudRate = 19200;
                dcb.ByteSize = 8;
                dcb.Parity = 0;      // NOPARITY
                dcb.StopBits = 0;    // ONESTOPBIT
                // fBinary (bit 0) + fDtrControl=ENABLE (bits 4-5) + fRtsControl=ENABLE (bits 12-13); sem controle de fluxo.
                dcb.Flags = 1u | (1u << 4) | (1u << 12);
                SetCommState(h, ref dcb);   // porta virtual às vezes recusa: segue e tenta assim mesmo
            }
        }
        catch { /* configuração é melhor esforço */ }
        porta = new Aberta(h);
        return AberturaPorta.Abriu;
    }

    public IReadOnlyList<string> ProgramasDeMaquininhaAbertos() => ProgramasDeMaquininha.AbertosAgora();

    private sealed class Aberta : IPortaAberta
    {
        private readonly SafeFileHandle _h;
        public Aberta(SafeFileHandle h) => _h = h;

        public bool EnviarEsperar(byte enviar, byte esperado, int prazoMs)
        {
            if (prazoMs <= 0) return false;
            PurgeComm(_h, PURGE_RXCLEAR | PURGE_TXCLEAR);
            var fim = Environment.TickCount64 + prazoMs;
            // Vigia: se o driver não respeitar o tempo da porta, cancela a leitura presa.
            using var vigia = new Timer(_ => { try { CancelIoEx(_h, IntPtr.Zero); } catch { } }, null, prazoMs + 500, Timeout.Infinite);
            var escrita = new COMMTIMEOUTS { WriteTotalTimeoutConstant = 1_000, ReadTotalTimeoutConstant = (uint)prazoMs };
            SetCommTimeouts(_h, ref escrita);
            if (!WriteFile(_h, new[] { enviar }, 1, out var escritos, IntPtr.Zero) || escritos != 1) return false;
            var um = new byte[1];
            while (true)
            {
                var resta = fim - Environment.TickCount64;
                if (resta <= 0) return false;
                var to = new COMMTIMEOUTS { WriteTotalTimeoutConstant = 1_000, ReadTotalTimeoutConstant = (uint)resta };
                SetCommTimeouts(_h, ref to);
                if (!ReadFile(_h, um, 1, out var lidos, IntPtr.Zero) || lidos == 0) return false;
                if (um[0] == esperado) return true;
            }
        }

        public void Dispose() => _h.Dispose();
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_SHARING_VIOLATION = 32;
    private const uint PURGE_TXCLEAR = 0x0004;
    private const uint PURGE_RXCLEAR = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct DCB
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags;
        public ushort wReserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EvtChar;
        public ushort wReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COMMTIMEOUTS
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string nome, uint acesso, uint compartilhar, IntPtr seguranca, uint criacao, uint flags, IntPtr modelo);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetupComm(SafeFileHandle h, uint entrada, uint saida);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetCommState(SafeFileHandle h, ref DCB dcb);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetCommState(SafeFileHandle h, ref DCB dcb);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetCommTimeouts(SafeFileHandle h, ref COMMTIMEOUTS t);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PurgeComm(SafeFileHandle h, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle h, byte[] buffer, uint quantos, out uint escritos, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle h, byte[] buffer, uint quantos, out uint lidos, IntPtr overlapped);
}
