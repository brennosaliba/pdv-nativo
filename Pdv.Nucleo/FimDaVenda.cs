namespace Pdv.Nucleo;

/// <summary>
/// A TELA DE "VENDA CONCLUÍDA" VOLTA SOZINHA, OU ESPERA?
///
/// O dono, 09/09/2026: "apos venda concluida acho q pode voltar pro dash principal
/// ao inves de colocar botao de nova venda".
///
/// Ele está certo no caso comum. Venda de cartão que imprimiu, o cliente já foi
/// embora e o operador toca "Nova venda" só para poder atender o próximo. É um
/// toque por venda que não decide nada.
///
/// ⚠️ MAS NEM TODA VENDA TERMINA IGUAL, e é aí que voltar sozinho vira defeito:
///
///   · TEM TROCO. O valor a devolver está NESSA tela. Ela sumir enquanto o operador
///     conta a nota é como o troco some da vista: ou ele devolve errado, ou para
///     tudo para reabrir a venda e conferir.
///   · O PAPEL NÃO SAIU. O aviso de recibo entalado está nessa tela, com o botão de
///     reimprimir do lado. Voltar sozinho é engolir o problema: a próxima venda
///     começa e ninguém soube que o cliente ficou sem comprovante.
///
/// Nos dois casos quem fecha é gente. Nos outros, a tela sai da frente.
/// </summary>
public static class FimDaVenda
{
    /// <summary>
    /// Segundos na tela de sucesso quando nao ha nada exigindo atencao.
    ///
    /// Tres segundos: da para ler "Venda concluida" e o numero da venda sem correr,
    /// e nao segura a fila. Menos que isso pisca.
    /// </summary>
    public const int SegundosNormal = 3;

    /// <summary>
    /// Segundos quando ha troco a devolver ou papel que nao saiu.
    ///
    /// Quinze, escolha do dono em 09/09/2026: "troco voltar pra tela e papel tb, ou
    /// time de 15 segundos q eh suficiente". E o tempo de contar uma nota e conferir
    /// a impressora sem a tela sumir na mao, e sem prender o caixa esperando clique.
    ///
    /// A diferenca de tres para quinze e o unico lugar onde o sistema decide que
    /// alguma coisa precisa ser LIDA antes de seguir.
    /// </summary>
    public const int SegundosComPendencia = 15;

    /// <summary>
    /// A venda terminou limpa, sem nada que precise ser lido antes de seguir?
    ///
    /// ⚠️ Isto NÃO decide mais se a tela sai: ela sempre sai. Decide se sai rápido ou
    /// se dá tempo de ler. Prender o caixa esperando clique era o que o dono não
    /// queria, e ele tem razão: a tela pode sumir, a informação é que não pode sumir
    /// antes de ser vista.
    /// </summary>
    /// <param name="trocoCent">Troco a devolver, em centavos.</param>
    /// <param name="problemaNaImpressao">Recibo ou cupom que não saiu.</param>
    public static bool VoltaSozinho(long trocoCent, bool problemaNaImpressao)
        => trocoCent <= 0 && !problemaNaImpressao;

    /// <summary>Quanto tempo esta tela fica antes de sair sozinha.</summary>
    public static int SegundosAteVoltar(long trocoCent, bool problemaNaImpressao)
        => VoltaSozinho(trocoCent, problemaNaImpressao) ? SegundosNormal : SegundosComPendencia;

    /// <summary>
    /// O rótulo do botão de fechar. Quando a tela vai sair sozinha, o botão continua
    /// existindo para quem tem pressa: some a espera, não a saída.
    /// </summary>
    public static string RotuloDoBotao(bool voltaSozinho)
        => voltaSozinho ? "Continuar" : "Nova venda";

    /// <summary>O aviso do rodape, com o tempo, para o operador saber que a tela vai sair.</summary>
    public static string AvisoDeSaida(long trocoCent, bool problemaNaImpressao)
    {
        var s = SegundosAteVoltar(trocoCent, problemaNaImpressao);
        var motivo = PorQueEsperando(trocoCent, problemaNaImpressao);
        return motivo is null
            ? $"Volto para a venda em {s} segundos."
            : $"{motivo} Volto para a venda em {s} segundos.";
    }

    /// <summary>
    /// O motivo de a tela estar esperando, para o operador saber que a bola está com
    /// ele. `null` quando ela vai sair sozinha e não há nada a dizer.
    /// </summary>
    public static string? PorQueEsperando(long trocoCent, bool problemaNaImpressao)
    {
        if (problemaNaImpressao && trocoCent > 0)
            return "Devolva o troco e resolva a impressão antes de seguir.";
        if (problemaNaImpressao) return "Resolva a impressão antes de seguir.";
        if (trocoCent > 0) return "Devolva o troco antes de seguir.";
        return null;
    }
}
