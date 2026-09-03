using System.Globalization;
using System.Text;

namespace Pdv.Nucleo;

// Lista FECHADA das credenciadoras que o PayGo aceita como rede pré-selecionada — o campo
// `010-000` do arquivo intpos (PayGo.cs) e o campo `adquirente` do WebService (ControlPay.cs).
//
// Existe por causa de um prejuízo real: a rede era um campo de TEXTO LIVRE na tela de
// configuração e a loja entrou em produção com o `PIX C6 BANK` da homologação ainda gravado.
// O Pix de produção é ITAU, e toda cobrança voltava "MODALIDADE DE PAGAMENTO INVALIDA" com o
// cliente parado no balcão — ninguém procura o defeito na tela de configuração.
//
// Vale para os quatro campos de config que carregam rede (`tef_cpay_adquirente`,
// `tef_cpay_adquirente_pix`, `tef_paygo_rede`, `tef_paygo_rede_pix`):
//
//   1. VAZIO é a primeira opção e é o padrão recomendado em produção: sem rede no comando,
//      quem escolhe é o roteamento da PayGo. Fixar uma rede só faz sentido quando a loja tem
//      mais de um credenciamento e quer forçar um deles — ou na homologação, onde o roteiro
//      exige autorizador fixo. Fixado errado, é recusa garantida.
//   2. Cartão e Pix são listas SEPARADAS. Os dois campos são vizinhos na tela, e trocar um
//      pelo outro é justamente o erro que a rede recusa — por isso o valor que está na lista
//      ERRADA aparece com o aviso, não como opção normal.
//   3. O que está gravado no banco NUNCA some da tela (ver Opcoes). Uma caixa de seleção presa
//      só à lista mostraria VAZIO para um valor herdado de outra instalação, e vazio se lê
//      como "a PayGo escolhe" — a config errada continuaria lá, invisível, até a próxima
//      recusa; e o primeiro Salvar apagaria a evidência.
//   4. Nada de adivinhar nome parecido. Comparar dobra acento, caixa e espaço REPETIDO —
//      nunca o espaço interno: `C6 PAY` e `C6PAY` são strings diferentes para o PayGo (o
//      sandbox aceitou a COM espaço e devolveu "SERVICO NAO HABILITADOO" para a sem espaço,
//      docs/CONTROLPAY_status.md). Corrigir sozinho aqui seria trocar uma config que funciona
//      por uma que nega, sem o dono ver.

/// <summary>
/// Uma linha da caixa de seleção de rede. <see cref="Valor"/> é o que vai para a config e para
/// o TEF; <see cref="Rotulo"/> é o que o dono lê. `ToString` devolve o rótulo para a ComboBox
/// continuar legível mesmo sem `DisplayMemberPath`.
/// </summary>
public sealed record OpcaoRede(string Valor, string Rotulo, bool Conhecida)
{
    /// <summary>Valor vazio: sem pré-seleção, quem escolhe a rede é o roteamento da PayGo.</summary>
    public bool Automatica => Valor.Length == 0;

    public override string ToString() => Rotulo;
}

/// <summary>Credenciadoras do PayGo: a lista oficial, a validação do que veio do banco e o valor que vai para o TEF.</summary>
public static class RedesPayGo
{
    /// <summary>Rótulo da opção vazia — primeira da lista e a recomendada em produção.</summary>
    public const string RotuloAutomatico = "(automático: a PayGo escolhe a rede)";

    /// <summary>Credenciadoras de CARTÃO, com a grafia e a ordem da lista oficial da PayGo.</summary>
    public static readonly IReadOnlyList<string> Cartao = new[]
    {
        "BANESECARD/MULVI",
        "BANRISUL/VERO",
        "BIN",
        "CIELO",
        "CONDUCTOR/DOCK",
        "CREDISHOP",
        "CTF",
        "C6PAY",
        "DMCARD",
        "GETNET",
        "GLOBALPAYMENTS/ENTREPAYMENTS",
        "MERCADO PAGO",
        "PAGSEGURO",
        "PAGBANK",
        "REDE",
        "RV",
        "SAFRAPAY",
        "SIPAG",
        "STONE",
        "TICKETLOG",
    };

    /// <summary>Credenciadoras de PIX. Lista à parte: nome de Pix no campo do cartão é recusa na hora.</summary>
    public static readonly IReadOnlyList<string> Pix = new[]
    {
        "PIX C6 BANK",
        "PIX CIELO",
        "PIX ITAU",
        "PIX SICREDI",
        "PIX SIPAG",
        "PIX BRADESCO",
    };

    /// <summary>Opções do campo de CARTÃO, já contando o valor gravado (mesmo fora da lista).</summary>
    public static IReadOnlyList<OpcaoRede> OpcoesCartao(string? gravado = null)
        => Opcoes(Cartao, Pix, "Pix", gravado);

    /// <summary>Opções do campo de PIX, já contando o valor gravado (mesmo fora da lista).</summary>
    public static IReadOnlyList<OpcaoRede> OpcoesPix(string? gravado = null)
        => Opcoes(Pix, Cartao, "cartão", gravado);

    /// <summary>Nome oficial correspondente ao valor gravado/digitado, ou null se não é da lista de cartão.</summary>
    public static string? CanonicoCartao(string? valor) => Achar(Cartao, valor);

    /// <summary>Nome oficial correspondente ao valor gravado/digitado, ou null se não é da lista de Pix.</summary>
    public static string? CanonicoPix(string? valor) => Achar(Pix, valor);

    /// <summary>
    /// Valor que vai para o TEF (`010-000` / `adquirente`) no cartão. Vazio vira null — é assim
    /// que PayGo e ControlPay entendem "sem pré-seleção".
    /// </summary>
    public static string? ParaEnvioCartao(string? gravado) => ParaEnvio(Cartao, gravado);

    /// <summary>Idem para o Pix.</summary>
    public static string? ParaEnvioPix(string? gravado) => ParaEnvio(Pix, gravado);

    /// <summary>
    /// Índice da opção que corresponde ao valor gravado — para `ComboBox.SelectedIndex`.
    /// NUNCA devolve -1: caixa com -1 aparece VAZIA, e vazio na tela significa "a PayGo
    /// escolhe" — a leitura errada de uma config que tem rede fixada. Sem correspondência,
    /// cai no automático (que é o comportamento honesto de quem não tem nada gravado).
    /// </summary>
    public static int Indice(IReadOnlyList<OpcaoRede> opcoes, string? gravado)
    {
        var bruto = (gravado ?? "").Trim();
        if (bruto.Length == 0) return 0;
        // Texto exato primeiro: é ele que sobrevive como opção "fora da lista".
        for (var i = 0; i < opcoes.Count; i++)
            if (string.Equals(opcoes[i].Valor, bruto, StringComparison.Ordinal)) return i;
        // Depois a comparação tolerante ("cielo" gravado seleciona CIELO da lista).
        for (var i = 0; i < opcoes.Count; i++)
            if (opcoes[i].Valor.Length > 0 && Chave(opcoes[i].Valor) == Chave(bruto)) return i;
        return 0;
    }

    /// <summary>
    /// Monta a lista: automático + os nomes oficiais + (se preciso) o valor gravado que não é
    /// de nenhuma lista. O desconhecido entra com o texto EXATO do banco, para o dono ver o que
    /// está lá e decidir — e sai daqui marcado como não conhecido, para a tela poder destacá-lo.
    /// </summary>
    private static IReadOnlyList<OpcaoRede> Opcoes(
        IReadOnlyList<string> lista, IReadOnlyList<string> outra, string nomeDaOutra, string? gravado)
    {
        var ops = new List<OpcaoRede>(lista.Count + 2) { new("", RotuloAutomatico, true) };
        foreach (var nome in lista) ops.Add(new OpcaoRede(nome, nome, true));

        var bruto = (gravado ?? "").Trim();
        if (bruto.Length == 0) return ops;              // vazio já é a primeira opção
        if (Achar(lista, bruto) is not null) return ops; // reconhecido: a grafia oficial já está aí

        // Pista no rótulo: campo trocado é o erro mais provável (os dois ficam lado a lado na
        // tela) e o rótulo é o único lugar onde o dono vai reparar nisso.
        var pista = Achar(outra, bruto) is not null ? $"é rede de {nomeDaOutra}" : "confira";
        ops.Add(new OpcaoRede(bruto, $"{bruto}: fora da lista ({pista})", false));
        return ops;
    }

    /// <summary>Nome oficial equivalente ao valor, ou null. Comparação por <see cref="Chave"/>.</summary>
    private static string? Achar(IReadOnlyList<string> lista, string? valor)
    {
        var chave = Chave(valor);
        if (chave.Length == 0) return null;
        foreach (var nome in lista)
            if (Chave(nome) == chave) return nome;
        return null;
    }

    /// <summary>
    /// Nome reconhecido sai com a grafia oficial (conserta caixa, acento e espaço sobrando que
    /// o dono digitou); nome desconhecido sai COMO ESTÁ, só sem espaço nas pontas — pode ser
    /// credenciadora nova, e não é esta lista que vai recusar a cobrança da loja.
    /// </summary>
    private static string? ParaEnvio(IReadOnlyList<string> lista, string? gravado)
    {
        var bruto = (gravado ?? "").Trim();
        if (bruto.Length == 0) return null;
        return Achar(lista, bruto) ?? bruto;
    }

    /// <summary>
    /// Chave de comparação: sem acento, maiúscula, sem espaço nas pontas e com espaço repetido
    /// colapsado. O acento cai porque o arquivo do PayGo é ASCII puro — `ArquivoIntpos.Ascii` já
    /// dobra "Ú" em "U" na hora de gravar, então quem digita "PIX ITAÚ" quer dizer "PIX ITAU" e
    /// deve casar com a lista. O espaço INTERNO não se mexe: ver a nota do topo sobre C6 PAY.
    /// </summary>
    private static string Chave(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return "";
        var sb = new StringBuilder(valor.Length);
        var pendente = false;                          // espaço visto, só entra se vier letra depois
        foreach (var ch in valor.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(ch)) { pendente = sb.Length > 0; continue; }
            if (pendente) { sb.Append(' '); pendente = false; }
            sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }
}
