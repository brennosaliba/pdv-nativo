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
    /// Quanto tempo a tela de sucesso fica antes de sair sozinha.
    ///
    /// Três segundos: dá para ler "Venda concluída" e o número da venda sem ter que
    /// correr, e não segura a fila. Menos que isso pisca; mais que isso, o operador
    /// toca no botão antes e o automático não serve para nada.
    /// </summary>
    public const int SegundosAteVoltar = 3;

    /// <summary>
    /// A tela pode sair sozinha depois da venda concluída?
    /// </summary>
    /// <param name="trocoCent">Troco a devolver, em centavos. Maior que zero segura.</param>
    /// <param name="problemaNaImpressao">Recibo ou cupom que não saiu. Segura.</param>
    public static bool VoltaSozinho(long trocoCent, bool problemaNaImpressao)
        => trocoCent <= 0 && !problemaNaImpressao;

    /// <summary>
    /// O rótulo do botão de fechar. Quando a tela vai sair sozinha, o botão continua
    /// existindo para quem tem pressa: some a espera, não a saída.
    /// </summary>
    public static string RotuloDoBotao(bool voltaSozinho)
        => voltaSozinho ? "Continuar" : "Nova venda";

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
