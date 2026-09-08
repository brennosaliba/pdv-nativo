using System.Globalization;

namespace Pdv.Nucleo;

/// <summary>
/// O MENU DO TEF do caixa de homologação (07/09/2026, pedido do dono: "menu tef igual tinhamos
/// no outro antigo de homologacao pra seguirmos todos passos e ter configuracao").
///
/// Por que existe: as operações do PayGo estavam espalhadas. Testar, Instalar e ADM moravam
/// dentro do assistente de Configuração, cancelamento e reimpressão no menu Cancelar / Imprimir,
/// e o dono precisou perguntar onde ficava cada uma. Gravar 58 passos assim é caçar botão pela
/// tela com o cronômetro correndo.
///
/// Quatro coisas mandam aqui:
///
///   1. o menu SÓ existe no caixa de homologação (`homologacao` = 1). Com a chave desligada não
///      há botão, não há tela, não há nada: numa loja, um menu que roda operação de terminal ao
///      lado do botão de vender é convite a estrago;
///   2. a lista de operações vem da BIBLIOTECA (PW_iGetOperations), não daqui. Cada terminal
///      oferece o que oferece, e escrever a lista na mão seria uma segunda verdade que envelhece
///      no primeiro terminal diferente. O que este arquivo faz é dar NOME e ÍCONE ao que a
///      biblioteca lista, e cair no texto dela quando não conhece o código;
///   3. venda, cancelamento e recarga NÃO saem daqui (<see cref="PW.EhOperacaoDeValor"/>). Elas
///      têm valor e dono no caixa: a venda sai da comanda, o cancelamento sai do estorno, e são
///      esses dois caminhos que gravam a linha em `tef_transacao`. O menu mostra o item e diz
///      onde ele mora, em vez de fingir que faz;
///   4. o estado do terminal e a última frase da biblioteca ficam À VISTA. Quase todo passo do
///      roteiro cobra a frase exata que o operador viu (PWINFO_RESULTMSG) e em que ambiente o
///      terminal estava; ter que abrir o assistente para conferir é como se perde um passo.
///
/// Puro de propósito: quem desenha é <c>Telas/TelaMenuTef</c>, e quem fala com a biblioteca é o
/// MESMO <see cref="ProvedorPGWebLib"/> que a Configuração usa (Servicos.PGWebLib()).
/// </summary>
public static class MenuTef
{
    /// <summary>Rótulo do botão na barra da tela de venda. Diz o que faz, em duas palavras.</summary>
    public const string Rotulo = "Menu do TEF";

    /// <summary>Título da tela do menu.</summary>
    public const string Titulo = "Menu do TEF";

    /// <summary>Mesmo alvo de dedo dos outros menus da barra.</summary>
    public const double AlturaItem = MenuBarra.AlturaItem;

    /// <summary>
    /// O botão existe nesta tela? SÓ no caixa de homologação, e por nada mais.
    ///
    /// Repare no que NÃO entra na conta: se o TEF está ligado, se o provedor é a biblioteca, se
    /// o terminal já foi instalado. É de propósito. O passo 01 do roteiro é justamente instalar
    /// o ponto de captura, e um menu que só aparece com o terminal pronto seria um menu que
    /// nunca aparece quando é preciso.
    /// </summary>
    public static bool Aparece(bool homologacao) => homologacao;

    // ── ações ────────────────────────────────────────────────────────────────

    /// <summary>Atalho para a Configuração do caixa ("e ter configuracao", nas palavras do dono).</summary>
    public const string AcaoConfiguracao = "tef-configuracao";

    /// <summary>Prefixo das ações que são uma operação da biblioteca; o resto da chave é o PWOPER_*.</summary>
    public const string PrefixoOperacao = "tef-op-";

    public static string AcaoDaOperacao(byte oper) => PrefixoOperacao + oper.ToString(CultureInfo.InvariantCulture);

    /// <summary>O PWOPER_* de uma ação de operação, ou null se a ação for outra coisa.</summary>
    public static byte? OperacaoDaAcao(string? acao)
    {
        if (acao is null || !acao.StartsWith(PrefixoOperacao, StringComparison.Ordinal)) return null;
        return byte.TryParse(acao[PrefixoOperacao.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : null;
    }

    /// <summary>
    /// Um item do menu.
    /// </summary>
    /// <param name="Rotulo">O nome curto que o operador lê.</param>
    /// <param name="Icone">Um caractere, como nos outros menus da barra.</param>
    /// <param name="Acao">A chave que a tela recebe de volta.</param>
    /// <param name="Detalhe">
    /// Como a BIBLIOTECA chamou a operação ("REIMPRESSAO"), quando é diferente do rótulo. O
    /// roteiro fala com as palavras dela; sem isto o operador tem que adivinhar a equivalência.
    /// </param>
    /// <param name="Onde">
    /// Preenchido = esta operação NÃO roda aqui, e a frase diz onde ela mora. Null = o menu roda.
    /// </param>
    public sealed record Item(string Rotulo, string Icone, string Acao, string? Detalhe = null, string? Onde = null)
    {
        /// <summary>O menu executa esta operação (contra: ela mora em outro lugar do caixa).</summary>
        public bool Roda => Onde is null;
    }

    /// <summary>Frases de onde mora cada operação de valor. Curtas: é um recado, não um manual.</summary>
    public const string OndeVenda = "A venda sai pela comanda: toque em Valor do teste e depois em Finalizar.";
    public const string OndeCancelamento = "O cancelamento sai pelo Estornar, no menu Cancelar / Imprimir.";
    public const string OndeRecarga = "A recarga tem valor, como a venda: ela sai pela comanda.";

    /// <summary>Nome, ícone e (quando não roda aqui) onde mora, para os PWOPER_* que conhecemos.</summary>
    private static (string Rotulo, string Icone, string? Onde)? Conhecida(byte oper) => oper switch
    {
        PW.PWOPER_INSTALL => ("Instalar o ponto de captura", "🔧", null),
        PW.PWOPER_PARAMUPD => ("Atualizar parâmetros", "🔄", null),
        PW.PWOPER_REPRINT => ("Reimprimir comprovante", "🧾", null),
        PW.PWOPER_RPTTRUNC => ("Relatório resumido", "📄", null),
        PW.PWOPER_RPTDETAIL => ("Relatório detalhado", "📋", null),
        PW.PWOPER_ADMIN => ("Menu administrativo", "⚙", null),
        PW.PWOPER_SALE => ("Venda", "💳", OndeVenda),
        PW.PWOPER_SALEVOID => ("Cancelar venda", "↩", OndeCancelamento),
        PW.PWOPER_PREPAID => ("Recarga", "📱", OndeRecarga),
        PW.PWOPER_VOID => ("Cancelar operação", "↩", OndeCancelamento),
        PW.PWOPER_VERSION => ("Versão da biblioteca", "ℹ", null),
        PW.PWOPER_CONFIG => ("Configuração do PayGo", "🧰", null),
        PW.PWOPER_MAINTENANCE => ("Manutenção", "🩺", null),
        _ => null,
    };

    /// <summary>
    /// As operações que valem quando a biblioteca não lista nada (não iniciou, terminal mudo).
    /// Instalação e menu administrativo: são as duas saídas de um terminal que ainda não é um
    /// terminal, e sem elas o passo 01 do roteiro não teria por onde começar.
    /// </summary>
    private static readonly byte[] Reserva = { PW.PWOPER_INSTALL, PW.PWOPER_ADMIN };

    /// <summary>A biblioteca chegou a listar alguma coisa? (Só para a tela dizer de onde veio a lista.)</summary>
    public static bool ListouOperacoes(IReadOnlyList<PwOperacao>? operacoes) => operacoes is { Count: > 0 };

    /// <summary>
    /// Os itens do menu, na ORDEM em que a biblioteca listou as operações, mais o atalho da
    /// Configuração no fim. Código repetido (PW_iGetOperations com as duas listas juntas pode
    /// trazer o mesmo duas vezes) entra uma vez só, na primeira posição em que apareceu.
    /// </summary>
    public static IReadOnlyList<Item> Itens(IReadOnlyList<PwOperacao>? operacoes)
    {
        var itens = new List<Item>();
        var vistas = new HashSet<byte>();
        foreach (var op in ListouOperacoes(operacoes) ? operacoes! : Reserva.Select(b => new PwOperacao(b, "", "")))
        {
            if (!vistas.Add(op.Codigo)) continue;
            itens.Add(Montar(op));
        }
        itens.Add(new Item("Configuração do caixa", "🛠", AcaoConfiguracao));
        return itens;
    }

    private static Item Montar(PwOperacao op)
    {
        var daLib = (op.Texto ?? "").Trim();
        var c = Conhecida(op.Codigo);
        // Código que não conhecemos sai com o nome da biblioteca. Terminal novo não pode virar
        // um item sem nome só porque este arquivo é de antes dele.
        var rotulo = c?.Rotulo ?? (daLib.Length > 0 ? daLib : "Operação " + op.Codigo.ToString(CultureInfo.InvariantCulture));
        var icone = c?.Icone ?? "▸";
        // Operação de valor nunca roda daqui, conheçamos o código ou não.
        var onde = c?.Onde ?? (PW.EhOperacaoDeValor(op.Codigo) ? OndeVenda : null);
        var detalhe = daLib.Length > 0 && !string.Equals(daLib, rotulo, StringComparison.OrdinalIgnoreCase) ? daLib : null;
        return new Item(rotulo, icone, AcaoDaOperacao(op.Codigo), detalhe, onde);
    }

    // ── estado do terminal ───────────────────────────────────────────────────

    /// <summary>Uma linha do quadro de estado: o que é, e como está.</summary>
    public sealed record Linha(string Rotulo, string Valor);

    /// <summary>
    /// Como está o terminal, a partir da lista de operações de VENDA que a biblioteca devolveu.
    /// Quem decide se está instalado é <see cref="ProvedorPGWebLib.InstaladoPelaLista"/>, a MESMA
    /// regra que o caixa usa para oferecer cartão na hora de cobrar: o quadro não pode dizer
    /// "instalado" para um terminal que a venda vai recusar.
    /// </summary>
    public static string Terminal(short retornoDaVenda, int quantasOperacoesDeVenda)
        => ProvedorPGWebLib.InstaladoPelaLista(retornoDaVenda, quantasOperacoesDeVenda) ? "Instalado"
         : retornoDaVenda == PW.PWRET_NOTINST ? "Não instalado"
         : retornoDaVenda == PW.PWRET_OK ? "Sem operação de venda no terminal"
         // O retorno cru fica à vista de propósito: é o que o roteiro manda anotar quando um
         // passo não sai, e é a primeira coisa que a PayGo pergunta.
         : "Não respondeu: " + PW.Nome(retornoDaVenda);

    /// <summary>
    /// O quadro que poupa o dono de abrir o assistente só para conferir: terminal, ambiente,
    /// redes, porta do pinpad e as duas pastas. Sai das opções COM QUE O PROVEDOR ESTÁ RODANDO,
    /// não de uma releitura da config: o que interessa é o que está no ar.
    /// </summary>
    public static IReadOnlyList<Linha> Estado(OpcoesPGWebLib op, string pastaTrabalho, string? pastaDll, string terminal)
        => new List<Linha>
        {
            new("Terminal", terminal),
            new("Ambiente", ConfigPGWebLib.RotuloAmbiente(op.Ambiente)),
            new("Rede do cartão", Rede(op.RedeCartao)),
            new("Rede do Pix", Rede(op.RedePix)),
            new("Redes no menu", op.RedesPermitidas is { Count: > 0 } r ? string.Join(", ", r) : "todas as do terminal"),
            new("Porta do pinpad", op.PortaPinpad?.Trim() is null or "" or "0" ? "automática" : op.PortaPinpad!.Trim()),
            new("Pasta da biblioteca", string.IsNullOrWhiteSpace(pastaDll) ? "o Windows procura sozinho" : pastaDll!.Trim()),
            new("Pasta de trabalho", pastaTrabalho),
        };

    /// <summary>Rede em branco não é falta de informação: é o menu aparecendo para o operador escolher.</summary>
    private static string Rede(string? v) => string.IsNullOrWhiteSpace(v) ? "o caixa escolhe no menu" : v!.Trim();

    // ── a última frase da biblioteca ─────────────────────────────────────────

    public const string SemResposta = "a biblioteca ainda não respondeu nada neste caixa";

    /// <summary>
    /// PWINFO_RESULTMSG como o roteiro cobra: a frase da rede, inteira, com a hora em que ela
    /// chegou. Quase todo passo pede "Mensagem para o operador: TRANSACAO APROVADA", e quem
    /// escreve essa frase é a biblioteca, não o caixa. Aqui ela não é resumida nem traduzida.
    /// </summary>
    public static string UltimaResposta(string? mensagem, DateTime? quando)
    {
        var m = mensagem?.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.IsNullOrEmpty(m)) return SemResposta;
        return quando is { } q ? $"{m} (às {q:HH:mm})" : m;
    }
}
