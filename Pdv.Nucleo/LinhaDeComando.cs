namespace Pdv.Nucleo;

/// <summary>O que o Pdv.exe foi chamado para fazer.</summary>
public enum ModoDoExe
{
    /// <summary>Abrir a frente de caixa. Sem argumento é isto, e argumento desconhecido também.</summary>
    Caixa,
    /// <summary><c>--cupom-teste [arquivo.png]</c>: desenha o cupom de exemplo numa imagem e sai.</summary>
    CupomTeste,
    /// <summary><c>--imprimir-teste ["Impressora"]</c>: manda o cupom de exemplo para o papel e sai.</summary>
    ImprimirTeste,
}

/// <summary>
/// A REGRA DE QUEM PEGA A TRAVA E QUEM ABRE O CAIXA (14/09/2026, loja Castelo).
///
/// O instalador confere se o caixa abre rodando <c>Pdv.exe --cupom-teste</c>. Esse modo rodava
/// ANTES da trava de instância única (certo: ele não pode ser barrado por um caixa aberto) e
/// sem abrir janela nenhuma (errado: o App.xaml tinha StartupUri, e o WPF construía a
/// MainWindow no primeiro await do modo). Resultado medido na loja: dois Pdv.exe abertos, o
/// segundo sem trava nenhuma, disputando o pinpad, e o instalador parado em "Conferindo se o
/// caixa abre nesta máquina".
///
/// Daqui para frente a regra mora num lugar só e é testada: modo de ferramenta não pega a
/// trava, não é barrado por ela e NUNCA abre a frente de caixa. Só o modo Caixa faz as duas coisas.
/// </summary>
public static class LinhaDeComando
{
    public static ModoDoExe Modo(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0) return ModoDoExe.Caixa;
        return args[0] switch
        {
            "--cupom-teste" => ModoDoExe.CupomTeste,
            "--imprimir-teste" => ModoDoExe.ImprimirTeste,
            _ => ModoDoExe.Caixa,
        };
    }

    /// <summary>Este modo pega a trava de instância única? Só o caixa.</summary>
    public static bool PegaATrava(ModoDoExe modo) => modo == ModoDoExe.Caixa;

    /// <summary>Este modo abre a frente de caixa (MainWindow)? Só o caixa.</summary>
    public static bool AbreOCaixa(ModoDoExe modo) => modo == ModoDoExe.Caixa;
}
