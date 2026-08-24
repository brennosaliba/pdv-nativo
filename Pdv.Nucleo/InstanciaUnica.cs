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
/// </summary>
public sealed class InstanciaUnica : IDisposable
{
    public const string NomePadrao = "PdvNativo.Terminal";

    private Mutex? _mutex;

    /// <summary>
    /// Onde a trava foi criada: <c>Global\</c> (vale para a máquina inteira, inclusive
    /// outra sessão do Windows), <c>Local\</c> (só esta sessão) ou <c>""</c> quando o SO
    /// não deixou criar nenhuma das duas — aí o caixa abre SEM guarda, de propósito.
    /// </summary>
    public string Escopo { get; }

    private InstanciaUnica(Mutex? mutex, string escopo) { _mutex = mutex; Escopo = escopo; }

    /// <summary>
    /// Pega a trava do terminal. Devolve <c>null</c> quando JÁ EXISTE um PDV aberto
    /// nesta máquina — quem chama deve sair sem tocar em nada.
    /// </summary>
    /// <param name="nome">Só os testes passam algo aqui (para não brigar com o PDV de verdade da máquina).</param>
    public static InstanciaUnica? Tentar(string? nome = null)
    {
        var n = nome ?? NomePadrao;
        // Global\ primeiro (pega até a 2ª instância em outra sessão do Windows, e o banco
        // do caixa é um só para a máquina). Usuário padrão sem SeCreateGlobalPrivilege
        // não consegue criar objeto no namespace global: aí vale a trava da sessão.
        foreach (var escopo in new[] { @"Global\", @"Local\" })
        {
            Mutex m;
            bool criado;
            // O sinal é a EXISTÊNCIA do mutex, não a posse dele. Posse é de THREAD:
            // valeria para excluir dois trechos de código, e aqui o que se quer excluir
            // é outro PROCESSO. E existência resolve sozinha a queda de energia — o
            // handle morre com o processo, então o PDV seguinte cria de novo e sobe
            // (posse deixaria o mutex ABANDONADO, com a loja sem caixa até o reboot).
            try { m = new Mutex(false, escopo + n, out criado); }
            catch { continue; }

            if (!criado) { m.Dispose(); return null; }
            return new InstanciaUnica(m, escopo);
        }

        // Nem Global\ nem Local\: isso é problema de SO, não é 2ª instância. Abrir o
        // caixa sem guarda é ruim; deixar a loja parada é pior. Segue sem trava.
        return new InstanciaUnica(null, "");
    }

    public void Dispose()
    {
        // Fechar o handle destrói o mutex nomeado (não sobrou nenhum outro aberto),
        // e o próximo PDV a subir volta a criá-lo.
        Interlocked.Exchange(ref _mutex, null)?.Dispose();
    }
}
