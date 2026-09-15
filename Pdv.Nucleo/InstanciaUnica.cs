using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pdv.Nucleo;

/// <summary>
/// UM PDV POR MÁQUINA.
///
/// O caixa não tinha trava nenhuma: o operador que achava que a tela "travou" (o
/// pinpad estava com o cliente) clicava de novo no ícone e subia um SEGUNDO Pdv.exe.
/// Ele attacha no mesmo turno aberto e, no boot, roda o religamento do TEF — que
/// decide o que é cobrança abandonada comparando `criado_em` com o START DO PRÓPRIO
/// PROCESSO (`Servicos.ResolverPendenciasTefAsync`, `ControlPay.ReconciliarAsync`).
/// Para a 2ª instância, a cobrança VIVA da 1ª nasceu antes do boot dela: é sempre
/// "abandonada". A cobrança que estava no pinpad — possivelmente já APROVADA — vira
/// `orfa` com "confira no PayGo e estorne se aprovou", e o operador estorna dinheiro
/// que era da loja.
///
/// Nenhuma das defesas de hoje atravessa a fronteira do processo (o `SemaphoreSlim`
/// do ClienteControlPay é campo de instância; o `StartTime` é de quem reconcilia).
/// A trava tem que ser do SISTEMA OPERACIONAL — daí o mutex nomeado.
///
/// ⚠️ ACESSO NEGADO É CAIXA ABERTO (14/09/2026, loja Castelo: Pdv 1512 e 10848 ao mesmo
/// tempo, brigando pelo pinpad). O instalador roda como administrador e abre o caixa no fim;
/// aquele Pdv.exe elevado cria o mutex Global\ com a DACL padrão de um token de
/// administrador, que o usuário comum não abre. O clique seguinte no ícone, sem elevação,
/// tomava ACESSO NEGADO, a trava lia isso como "o Windows não deixou criar", caía no
/// Local\ (livre, porque o elevado só criou o Global\) e subia o 2º caixa. Duas correções:
/// o mutex nasce com DACL aberta (qualquer conta da máquina enxerga), e acesso negado ao
/// abrir o mutex conta como caixa aberto, que é a única coisa que ele pode significar.
/// </summary>
public sealed class InstanciaUnica : IDisposable
{
    public const string NomePadrao = "PdvNativo.Terminal";

    /// <summary>
    /// DACL da trava: Todos (WD) e o Sistema (SY) com MUTEX_ALL_ACCESS. Não é segredo nenhum:
    /// o mutex só diz "há um caixa aberto", e ele PRECISA ser visto por qualquer conta desta
    /// máquina, elevada ou não, senão cada nível de acesso abre o seu caixa.
    /// </summary>
    public const string DaclAberta = "D:(A;;0x001F0001;;;WD)(A;;0x001F0001;;;SY)";

    private const int ErroAcessoNegado = 5;
    private const int ErroJaExiste = 183;

    /// <summary>O que a tentativa de criar o mutex quer dizer.</summary>
    public enum Resposta
    {
        /// <summary>Criou agora: este é o caixa.</summary>
        Nossa,
        /// <summary>Há um caixa aberto (o mutex já existia, ou existe e esta conta não o abre).</summary>
        JaExiste,
        /// <summary>Erro do Windows que não diz nada sobre caixa aberto: tenta o próximo escopo.</summary>
        TentarOutroEscopo,
    }

    /// <summary>A decisão, separada da chamada ao Windows para ser testada.</summary>
    public static Resposta Classificar(bool abriu, int erroWin32)
        => abriu
            ? (erroWin32 == ErroJaExiste ? Resposta.JaExiste : Resposta.Nossa)
            : (erroWin32 == ErroAcessoNegado ? Resposta.JaExiste : Resposta.TentarOutroEscopo);

    private SafeWaitHandle? _handle;

    /// <summary>
    /// Onde a trava foi criada: <c>Global\</c> (vale para a máquina inteira, inclusive
    /// outra sessão do Windows), <c>Local\</c> (só esta sessão) ou <c>""</c> quando o SO
    /// não deixou criar nenhuma das duas — aí o caixa abre SEM guarda, de propósito.
    /// </summary>
    public string Escopo { get; }

    private InstanciaUnica(SafeWaitHandle? handle, string escopo) { _handle = handle; Escopo = escopo; }

    /// <summary>
    /// Pega a trava do terminal. Devolve <c>null</c> quando JÁ EXISTE um PDV aberto
    /// nesta máquina — quem chama deve sair sem tocar em nada.
    /// </summary>
    /// <param name="nome">Só os testes passam algo aqui (para não brigar com o PDV de verdade da máquina).</param>
    public static InstanciaUnica? Tentar(string? nome = null)
    {
        var n = nome ?? NomePadrao;
        // Global\ primeiro (pega até a 2ª instância em outra sessão do Windows, e o banco
        // do caixa é um só para a máquina). Local\ só quando o Global\ falha por um motivo
        // que NÃO é "já existe" nem "acesso negado".
        foreach (var escopo in new[] { @"Global\", @"Local\" })
        {
            SafeWaitHandle h;
            int erro;
            // O sinal é a EXISTÊNCIA do mutex, não a posse dele. Posse é de THREAD:
            // valeria para excluir dois trechos de código, e aqui o que se quer excluir
            // é outro PROCESSO. E existência resolve sozinha a queda de energia — o
            // handle morre com o processo, então o PDV seguinte cria de novo e sobe
            // (posse deixaria o mutex ABANDONADO, com a loja sem caixa até o reboot).
            try { h = Criar(escopo + n, DaclAberta, out erro); }
            catch { continue; }

            switch (Classificar(!h.IsInvalid, erro))
            {
                case Resposta.Nossa:
                    return new InstanciaUnica(h, escopo);
                case Resposta.JaExiste:
                    h.Dispose();
                    return null;
                default:
                    h.Dispose();
                    continue;
            }
        }

        // Nem Global\ nem Local\: isso é problema de SO, não é 2ª instância. Abrir o
        // caixa sem guarda é ruim; deixar a loja parada é pior. Segue sem trava.
        return new InstanciaUnica(null, "");
    }

    /// <summary>CreateMutexW com a DACL dada (SDDL). Devolve o handle (inválido na falha) e o GetLastError.</summary>
    internal static SafeWaitHandle Criar(string nome, string sddl, out int erro)
    {
        var sd = IntPtr.Zero;
        try
        {
            var sa = new SecurityAttributes { nLength = Marshal.SizeOf<SecurityAttributes>() };
            // SDDL que não monta (não deveria acontecer) cai na DACL padrão: trava com a DACL de
            // antes é melhor que trava nenhuma.
            if (ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out sd, out _))
                sa.lpSecurityDescriptor = sd;
            var h = CreateMutexW(ref sa, false, nome);
            erro = Marshal.GetLastWin32Error();
            return h;
        }
        finally
        {
            if (sd != IntPtr.Zero) LocalFree(sd);
        }
    }

    public void Dispose()
    {
        // Fechar o handle destrói o mutex nomeado (não sobrou nenhum outro aberto),
        // e o próximo PDV a subir volta a criá-lo.
        Interlocked.Exchange(ref _handle, null)?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateMutexW(ref SecurityAttributes sa, [MarshalAs(UnmanagedType.Bool)] bool inicial, string nome);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revisao, out IntPtr sd, out uint tamanho);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr h);
}
