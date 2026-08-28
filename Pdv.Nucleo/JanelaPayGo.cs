using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pdv.Nucleo;

/// <summary>
/// Aperta Esc na janela do PayGo Windows a partir do PDV.
///
/// Por que existe: com o QR do Pix na tela, quem desiste é o PayGo — o ControlPay
/// não tem endpoint para abortar uma intenção pendente (as rotas são criar,
/// consultar e cancelar venda JÁ aprovada). Sem isto, o operador tinha que largar
/// o caixa, achar a janela do PayGo e apertar Esc lá, com o cliente esperando.
///
/// Como: PostMessage de Esc direto para as janelas de topo DO PROCESSO PayGo.
/// Não usa SendKeys nem rouba o foco de propósito — SendKeys entrega a tecla para
/// a janela que estiver em foco, e um Esc perdido no lugar errado é pior que não
/// cancelar. Mirando o handle, o pior caso é a tecla ser ignorada.
///
/// Isto NÃO decide o desfecho: quem diz se a cobrança morreu ou foi paga continua
/// sendo o status da intenção no ControlPay. O botão só evita a caminhada até a
/// outra janela — o PDV segue esperando a resposta real.
/// </summary>
public static class JanelaPayGo
{
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const int VK_ESCAPE = 0x1B;

    /// <summary>Nomes de processo do PayGo Windows (o launcher não recebe tecla).</summary>
    private static readonly string[] Processos = { "PayGo", "PGWebLib", "PayGoWeb" };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>
    /// Manda Esc para todas as janelas visíveis do PayGo. Devolve quantas
    /// receberam — 0 significa "não achei a janela", e a tela tem que dizer isso
    /// ao operador em vez de fingir que cancelou.
    /// </summary>
    public static int EnviarEsc()
    {
        var pids = ProcessosDoPayGo();
        if (pids.Count == 0) return 0;

        var enviadas = 0;
        try
        {
            EnumWindows((h, _) =>
            {
                try
                {
                    if (!IsWindowVisible(h)) return true;
                    GetWindowThreadProcessId(h, out var pid);
                    if (!pids.Contains(pid)) return true;
                    PostMessage(h, WM_KEYDOWN, new IntPtr(VK_ESCAPE), IntPtr.Zero);
                    PostMessage(h, WM_KEYUP, new IntPtr(VK_ESCAPE), IntPtr.Zero);
                    enviadas++;
                }
                catch { /* uma janela problemática não pode parar a varredura */ }
                return true;
            }, IntPtr.Zero);
        }
        catch { return enviadas; }
        return enviadas;
    }

    /// <summary>PIDs vivos do PayGo. Vazio = o PayGo Windows nem está aberto.</summary>
    private static HashSet<uint> ProcessosDoPayGo()
    {
        var pids = new HashSet<uint>();
        foreach (var nome in Processos)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(nome))
                    using (p) { try { pids.Add((uint)p.Id); } catch { } }
            }
            catch { /* nome inexistente não é erro */ }
        }
        return pids;
    }
}
