namespace Pdv.Nucleo;

/// <summary>
/// Os dois MENUS da barra de cima da tela de venda (04/09/2026, pedido do dono:
/// "juntar o menu impressora com cancelar venda, e o sair com o fechar caixa").
///
/// Quatro botões viraram dois. O que cada menu lista, em que ORDEM, que ação cada
/// item dispara e o tamanho mínimo do alvo de toque moram AQUI, sem WPF, para a
/// suíte provar. A tela (Telas/Venda.xaml.cs) só desenha o cartão e reencaminha
/// cada chave para o handler que JÁ existia: nada de cancelamento, estorno,
/// reimpressão, impressora, fechamento ou saída foi reescrito.
/// </summary>
public static class MenuBarra
{
    /// <summary>Um item do menu: rótulo curto (as palavras do dono), ícone e a chave da ação.</summary>
    public sealed record Item(string Rotulo, string Icone, string Acao);

    /// <summary>
    /// Altura mínima de um item, em px. O alvo de dedo da casa é 44; 56 dá folga
    /// para quem toca com pressa e mantém quatro itens dentro de qualquer tela.
    /// </summary>
    public const double AlturaItem = 56;

    /// <summary>
    /// Rótulos dos dois botões da barra. Dizem as DUAS ações que moram dentro, com
    /// as mesmas palavras que os botões antigos usavam: quem procurava "Cancelar
    /// venda" ou "Impressora" acha os dois no mesmo botão sem aprender nome novo.
    /// </summary>
    public const string RotuloCancelarImprimir = "Cancelar / Imprimir";
    public const string RotuloFecharSair = "Fechar / Sair";

    // chaves de ação: a tela faz `switch` nelas
    public const string Cancelar = "cancelar";
    public const string Estornar = "estornar";
    public const string Reimprimir = "reimprimir";
    public const string Impressora = "impressora";
    public const string FecharCaixa = "fechar";
    public const string Sair = "sair";

    /// <summary>
    /// Menu "Cancelar / Imprimir": cancelar venda, estornar, reimpressão e a
    /// configuração da impressora, NESTA ordem.
    ///
    /// Estornar e reimprimir só existem com maquininha INTEGRADA (é a regra que
    /// já valia no menu antigo: em loja de maquininha avulsa o estorno é na mão e
    /// não há comprovante do PDV para reimprimir). Sem TEF eles saem da lista e a
    /// ordem dos que ficam não muda.
    /// </summary>
    public static IReadOnlyList<Item> CancelarImprimir(bool temTef)
    {
        var itens = new List<Item> { new("Cancelar venda", "↩", Cancelar) };
        if (temTef)
        {
            itens.Add(new("Estornar", "💳", Estornar));
            itens.Add(new("Reimpressão", "🧾", Reimprimir));
        }
        itens.Add(new("Configuração da impressora", "🖨", Impressora));
        return itens;
    }

    /// <summary>Menu "Fechar / Sair": o pop-up de fechamento de caixa e o sair.</summary>
    public static IReadOnlyList<Item> FecharSair() => new List<Item>
    {
        new("Fechamento de caixa", "🔒", FecharCaixa),
        new("Sair", "🚪", Sair),
    };
}
