using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Pdv.Nucleo;

namespace Pdv;

/// <summary>
/// O Wi-Fi do Windows pelo wlanapi (1.0.58, barra do Windows no caixa). É a mesma API que o ícone da
/// barra de tarefas usa, e não depende do Explorer: funciona com o PDV como shell. Usuário comum
/// conecta e grava perfil (é o padrão do Windows). As regras moram em Pdv.Nucleo/BarraDoWindows.
/// Nada aqui lança: erro vira texto para a tela.
/// </summary>
public static class WifiDoWindows
{
    private const int Ok = 0;
    private const uint FlagConectada = 1, FlagTemPerfil = 2;
    private const int TamanhoRede = 628;          // WLAN_AVAILABLE_NETWORK
    private const int TamanhoPlaca = 532;         // WLAN_INTERFACE_INFO
    private const int OpcodeRadio = 4;            // wlan_intf_opcode_radio_state
    private const int OpcodeConexao = 7;          // wlan_intf_opcode_current_connection

    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint versao, IntPtr r, out uint negociada, out IntPtr h);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr h, IntPtr r);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr p);
    [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr h, IntPtr r, out IntPtr lista);
    [DllImport("wlanapi.dll")] private static extern int WlanScan(IntPtr h, ref Guid placa, IntPtr ssid, IntPtr ie, IntPtr r);
    [DllImport("wlanapi.dll")] private static extern int WlanGetAvailableNetworkList(IntPtr h, ref Guid placa, uint flags, IntPtr r, out IntPtr lista);
    [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(IntPtr h, ref Guid placa, int opcode, IntPtr r, out uint tamanho, out IntPtr dados, IntPtr tipo);
    [DllImport("wlanapi.dll")] private static extern int WlanSetInterface(IntPtr h, ref Guid placa, int opcode, uint tamanho, IntPtr dados, IntPtr r);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern int WlanSetProfile(IntPtr h, ref Guid placa, uint flags, string xml, string? seguranca, bool sobrescrever, IntPtr r, out uint motivo);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern int WlanDeleteProfile(IntPtr h, ref Guid placa, string perfil, IntPtr r);
    [DllImport("wlanapi.dll")] private static extern int WlanConnect(IntPtr h, ref Guid placa, ref ParametrosConexao p, IntPtr r);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ParametrosConexao
    {
        public int Modo;                                   // wlan_connection_mode_profile = 0
        [MarshalAs(UnmanagedType.LPWStr)] public string Perfil;
        public IntPtr Ssid, Bssids;
        public int TipoBss;                                // dot11_BSS_type_infrastructure = 1
        public uint Flags;
    }

    private sealed class Sessao : IDisposable
    {
        public readonly IntPtr H;
        public Sessao() { if (WlanOpenHandle(2, IntPtr.Zero, out _, out H) != Ok) H = IntPtr.Zero; }
        public void Dispose() { if (H != IntPtr.Zero) WlanCloseHandle(H, IntPtr.Zero); }
    }

    private static (Guid Id, int Estado)? PrimeiraPlaca(IntPtr h)
    {
        if (WlanEnumInterfaces(h, IntPtr.Zero, out var lista) != Ok) return null;
        try
        {
            var n = Marshal.ReadInt32(lista);
            if (n <= 0) return null;
            var item = lista + 8;
            var id = Marshal.PtrToStructure<Guid>(item);
            var estado = Marshal.ReadInt32(item + 16 + 512);
            return (id, estado);
        }
        finally { WlanFreeMemory(lista); }
    }

    /// <summary>O rádio do Wi-Fi está ligado? Null = o Windows não disse.</summary>
    private static bool? RadioLigado(IntPtr h, Guid placa)
    {
        if (WlanQueryInterface(h, ref placa, OpcodeRadio, IntPtr.Zero, out _, out var dados, IntPtr.Zero) != Ok) return null;
        try
        {
            var fases = Marshal.ReadInt32(dados);
            if (fases <= 0) return null;
            // WLAN_PHY_RADIO_STATE: índice, estado do software, estado do hardware (1 = ligado)
            for (var i = 0; i < fases; i++)
            {
                var sw = Marshal.ReadInt32(dados + 4 + i * 12 + 4);
                var hw = Marshal.ReadInt32(dados + 4 + i * 12 + 8);
                if (sw == 1 && hw == 1) return true;
            }
            return false;
        }
        finally { WlanFreeMemory(dados); }
    }

    /// <summary>A rede e o sinal da conexão de agora (WLAN_CONNECTION_ATTRIBUTES).</summary>
    private static (string? Rede, int Sinal) ConexaoAtual(IntPtr h, Guid placa)
    {
        if (WlanQueryInterface(h, ref placa, OpcodeConexao, IntPtr.Zero, out _, out var dados, IntPtr.Zero) != Ok) return (null, 0);
        try
        {
            // estado(4) modo(4) perfil(512) | associação: DOT11_SSID(36) tipo(4) bssid(6+2) phy(4) índice(4) sinal(4)
            var assoc = dados + 4 + 4 + 512;
            var tam = Math.Clamp(Marshal.ReadInt32(assoc), 0, 32);
            var ssid = new byte[tam];
            Marshal.Copy(assoc + 4, ssid, 0, tam);
            var sinal = Marshal.ReadInt32(assoc + 36 + 4 + 8 + 4 + 4);
            return (BarraDoWindows.NomeDoSsid(ssid), Math.Clamp(sinal, 0, 100));
        }
        finally { WlanFreeMemory(dados); }
    }

    private static bool CaboLigado()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>O estado para a barra. Roda fora da thread da tela.</summary>
    public static BarraDoWindows.EstadoWifi Estado()
    {
        try
        {
            using var s = new Sessao();
            if (s.H == IntPtr.Zero || PrimeiraPlaca(s.H) is not { } placa)
                return new(BarraDoWindows.SituacaoWifi.SemPlaca, null, 0, CaboLigado());
            if (RadioLigado(s.H, placa.Id) == false)
                return new(BarraDoWindows.SituacaoWifi.Desligado, null, 0, false);
            var situacao = BarraDoWindows.SituacaoDaPlaca(placa.Estado);
            if (situacao != BarraDoWindows.SituacaoWifi.Conectado) return new(situacao, null, 0, false);
            var (rede, sinal) = ConexaoAtual(s.H, placa.Id);
            return new(situacao, rede, sinal, false);
        }
        catch { return new(BarraDoWindows.SituacaoWifi.SemPlaca, null, 0, CaboLigado()); }
    }

    /// <summary>Pede ao Windows uma busca nova de redes (o resultado chega em uns 2 a 4 s).</summary>
    public static void Procurar()
    {
        try
        {
            using var s = new Sessao();
            if (s.H == IntPtr.Zero || PrimeiraPlaca(s.H) is not { } placa) return;
            var id = placa.Id;
            WlanScan(s.H, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    /// <summary>As redes que o Windows enxerga agora, já organizadas para a tela.</summary>
    public static IReadOnlyList<BarraDoWindows.Rede> Redes()
    {
        try
        {
            using var s = new Sessao();
            if (s.H == IntPtr.Zero || PrimeiraPlaca(s.H) is not { } placa) return Array.Empty<BarraDoWindows.Rede>();
            var id = placa.Id;
            if (WlanGetAvailableNetworkList(s.H, ref id, 0, IntPtr.Zero, out var lista) != Ok) return Array.Empty<BarraDoWindows.Rede>();
            try
            {
                var n = Marshal.ReadInt32(lista);
                var redes = new List<BarraDoWindows.Rede>(n);
                for (var i = 0; i < n; i++)
                {
                    var p = lista + 8 + i * TamanhoRede;
                    var perfil = Marshal.PtrToStringUni(p, 256).TrimEnd('\0');
                    var tam = Math.Clamp(Marshal.ReadInt32(p + 512), 0, 32);
                    var ssid = new byte[tam];
                    Marshal.Copy(p + 516, ssid, 0, tam);
                    // depois do DOT11_SSID (36): tipo, nº de bssids, conectável, motivo, nº de phys, phys[8], mais phys
                    var q = p + 512 + 36 + 4 + 4 + 4 + 4 + 4 + 32 + 4;
                    var sinal = Marshal.ReadInt32(q);
                    var segura = Marshal.ReadInt32(q + 4) != 0;
                    var auth = Marshal.ReadInt32(q + 8);
                    var cifra = Marshal.ReadInt32(q + 12);
                    var flags = (uint)Marshal.ReadInt32(q + 16);
                    redes.Add(new BarraDoWindows.Rede(ssid, Math.Clamp(sinal, 0, 100), segura, auth, cifra,
                        (flags & FlagTemPerfil) != 0 && perfil.Length > 0 ? perfil : null, (flags & FlagConectada) != 0));
                }
                return BarraDoWindows.Organizar(redes);
            }
            finally { WlanFreeMemory(lista); }
        }
        catch { return Array.Empty<BarraDoWindows.Rede>(); }
    }

    /// <summary>
    /// Conecta e espera até 20 s o Windows dizer que entrou nessa rede. Com <paramref name="perfilXml"/>
    /// grava a rede antes (rede nova ou senha nova); se não entrar, apaga o perfil novo, para a senha
    /// errada não ficar salva. Devolve null quando conectou, ou a frase para a tela.
    /// </summary>
    public static async Task<string?> ConectarAsync(BarraDoWindows.Rede rede, string? perfilXml)
    {
        try
        {
            using var s = new Sessao();
            if (s.H == IntPtr.Zero || PrimeiraPlaca(s.H) is not { } placa) return "O caixa não achou a placa de Wi-Fi.";
            var id = placa.Id;
            var perfil = rede.Perfil;
            var gravou = false;
            if (perfilXml is not null)
            {
                var r = WlanSetProfile(s.H, ref id, 0, perfilXml, null, true, IntPtr.Zero, out _);
                if (r != Ok) return "O Windows não aceitou salvar essa rede (código " + r + ").";
                perfil = rede.Nome;
                gravou = true;
            }
            if (string.IsNullOrEmpty(perfil)) return "Essa rede ainda não está salva neste caixa.";
            var p = new ParametrosConexao { Modo = 0, Perfil = perfil, TipoBss = 1 };
            var c = WlanConnect(s.H, ref id, ref p, IntPtr.Zero);
            if (c != Ok)
            {
                if (gravou) WlanDeleteProfile(s.H, ref id, perfil, IntPtr.Zero);
                return "O Windows não conseguiu conectar (código " + c + ").";
            }
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(500).ConfigureAwait(false);
                var e = Estado();
                if (e.Situacao == BarraDoWindows.SituacaoWifi.Conectado && e.Rede == rede.Nome) return null;
            }
            if (gravou) WlanDeleteProfile(s.H, ref id, perfil, IntPtr.Zero);
            return rede.Segura ? "Não conectou. Confira a senha e tente de novo." : "Não conectou nessa rede.";
        }
        catch (Exception ex) { return "Não deu para conectar (" + ex.GetType().Name + ")."; }
    }

    /// <summary>Liga o rádio do Wi-Fi pelo software. Null = ligou; senão, a frase para a tela.</summary>
    public static string? LigarRadio()
    {
        try
        {
            using var s = new Sessao();
            if (s.H == IntPtr.Zero || PrimeiraPlaca(s.H) is not { } placa) return "O caixa não achou a placa de Wi-Fi.";
            var id = placa.Id;
            // WLAN_PHY_RADIO_STATE { índice 0, software ligado, hardware (ignorado) }
            var dados = Marshal.AllocHGlobal(12);
            try
            {
                Marshal.WriteInt32(dados, 0);
                Marshal.WriteInt32(dados + 4, 1);
                Marshal.WriteInt32(dados + 8, 1);
                var r = WlanSetInterface(s.H, ref id, OpcodeRadio, 12, dados, IntPtr.Zero);
                return r == Ok ? null : "O Windows não deixou ligar o Wi-Fi (código " + r + "). Pode ser o botão do próprio computador.";
            }
            finally { Marshal.FreeHGlobal(dados); }
        }
        catch (Exception ex) { return "Não deu para ligar o Wi-Fi (" + ex.GetType().Name + ")."; }
    }

    /// <summary>A barra de tarefas do Windows existe agora? (Explorer rodando como shell)</summary>
    public static bool WindowsComBarra() => FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string classe, string? titulo);
}
