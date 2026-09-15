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
        // AS DUAS GRAFIAS, e nao e descuido: para o PayGo sao redes DIFERENTES.
        //
        // 09/09/2026, medido no log da homologacao: `C6 PAY` aprovou quatro vezes
        // (13:52, 14:28, 14:35, 14:45) e `C6PAY` devolveu [NA A116] SERVICO NAO
        // HABILITADO nas duas em que foi tentada. Quem escolhe a rede no pinpad manda
        // `C6 PAY`, que e o nome que o terminal do sandbox conhece.
        //
        // A nota do topo deste arquivo ja dizia isso desde a homologacao do ControlPay.
        // A lista mesmo assim so tinha a sem espaco, entao a caixa de selecao oferecia
        // exatamente a grafia que este terminal recusa, e o dono nao tinha como
        // escolher a certa: ele nao digita, escolhe.
        //
        // Nenhuma das duas sai da lista: instalacao de producao pode estar na outra.
        "C6 PAY",
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

    /// <summary>A chave onde ficam as redes que o terminal ja ofereceu.</summary>
    public const string ChaveVistas = "tef_redes_do_terminal";

    /// <summary>
    /// Guarda as redes que o TERMINAL ofereceu no menu.
    ///
    /// So acrescenta, nunca substitui: um menu que veio curto (porque a loja
    /// encurtou, ou porque a rede estava fora do ar) nao pode apagar o que ja se
    /// sabia deste terminal.
    /// </summary>
    public static void GuardarVistas(IReadOnlyList<string> redes) => GuardarVistas(redes, pix: false);

    /// <summary>
    /// Idem, na lista do cartão ou na do PIX. As redes que o menu mostrou numa cobrança PIX vão
    /// para <see cref="ChaveVistasPix"/> (14/09/2026, Castelo): misturadas, uma rede de Pix subiria
    /// para o campo do cartão, que é justamente a troca que a rede recusa.
    /// </summary>
    public static void GuardarVistas(IReadOnlyList<string> redes, bool pix)
    {
        try
        {
            using var cx = Banco.Abrir();
            var chave = pix ? ChaveVistasPix : ChaveVistas;
            if (Acrescentar(Vendas.Config(cx, chave), redes) is { } novo) Vendas.GravarConfig(cx, chave, novo);
        }
        catch { /* saber as redes e conforto: nunca derruba a cobranca */ }
    }

    /// <summary>
    /// A lista gravada ("A|B") com as redes novas no fim, ou null quando não há nada novo (nada a
    /// gravar). Só acrescenta: menu curto não apaga o que já se sabia do terminal.
    /// </summary>
    public static string? Acrescentar(string? atual, IReadOnlyList<string>? redes)
    {
        var lista = Lista(atual).ToList();
        var mudou = false;
        foreach (var r in redes ?? Array.Empty<string>())
        {
            var v = (r ?? "").Trim();
            if (v.Length == 0 || lista.Contains(v, StringComparer.Ordinal)) continue;
            lista.Add(v);
            mudou = true;
        }
        return mudou ? string.Join("|", lista) : null;
    }

    /// <summary>As redes que este terminal ja ofereceu no CARTÃO, na ordem em que apareceram.</summary>
    public static IReadOnlyList<string> Vistas(Func<string, string?> config) => Lista(config(ChaveVistas));

    /// <summary>As redes que este terminal ja ofereceu numa cobrança PIX, na ordem em que apareceram.</summary>
    public static IReadOnlyList<string> VistasPix(Func<string, string?> config) => Lista(config(ChaveVistasPix));

    private static IReadOnlyList<string> Lista(string? bruto)
    {
        if (string.IsNullOrWhiteSpace(bruto)) return Array.Empty<string>();
        return bruto.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Opções do campo de CARTÃO, já contando o valor gravado (mesmo fora da lista).</summary>
    public static IReadOnlyList<OpcaoRede> OpcoesCartao(string? gravado = null)
        => Opcoes(Cartao, Pix, "Pix", gravado);

    /// <summary>
    /// Opcoes do campo de CARTAO com as redes que ESTE TERMINAL ja ofereceu na frente.
    ///
    /// A lista escrita a mao continua embaixo, porque terminal recem instalado nunca
    /// abriu menu de rede e nao tem o que oferecer. O que muda e a ordem e o rotulo:
    /// quem o terminal ja mostrou vem primeiro, dito como tal.
    /// </summary>
    public static IReadOnlyList<OpcaoRede> OpcoesCartao(string? gravado, IReadOnlyList<string> vistas)
    {
        var basica = Opcoes(Cartao, Pix, "Pix", gravado);
        // Rede de Pix que apareceu no menu não sobe para o campo do cartão (14/09/2026).
        var daqui = Filtrar(vistas, nome => CanonicoPix(nome) is null && !nome.StartsWith("PIX ", StringComparison.OrdinalIgnoreCase));
        if (daqui.Count == 0) return basica;

        // As vistas primeiro, na ordem em que o terminal as mostrou; depois o resto.
        var (naFrente, fora, resto) = Separar(basica, daqui);
        return naFrente.Concat(fora).Concat(resto).ToList();
    }

    /// <summary>
    /// Opções do campo de PIX com as redes que ESTE TERMINAL ofereceu numa cobrança Pix logo
    /// depois do automático (14/09/2026, loja Castelo: "PIX ITAU" copiado da Savassi voltou
    /// "MODALIDADE DE PAGAMENTO INVALIDA"). O automático fica em PRIMEIRO no Pix: é o padrão de
    /// instalação nova e o que resolve quando a rede fixada não vale para o terminal.
    /// </summary>
    public static IReadOnlyList<OpcaoRede> OpcoesPix(string? gravado, IReadOnlyList<string> vistas)
    {
        var basica = Opcoes(Pix, Cartao, "cartão", gravado);
        var daqui = Filtrar(vistas, nome => CanonicoCartao(nome) is null);
        if (daqui.Count == 0) return basica;

        var (naFrente, fora, resto) = Separar(basica, daqui);
        return resto.Where(o => o.Automatica)
            .Concat(naFrente).Concat(fora)
            .Concat(resto.Where(o => !o.Automatica))
            .ToList();
    }

    private const string MarcaDoTerminal = "  (este terminal oferece)";

    private static IReadOnlyList<string> Filtrar(IReadOnlyList<string>? vistas, Func<string, bool> daLista)
        => (vistas ?? Array.Empty<string>())
            .Select(v => (v ?? "").Trim())
            .Where(v => v.Length > 0 && daLista(v))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>As vistas que a lista conhece (com a marca), as que ela não conhece, e o resto da lista.</summary>
    private static (List<OpcaoRede> NaFrente, List<OpcaoRede> Fora, List<OpcaoRede> Resto) Separar(
        IReadOnlyList<OpcaoRede> basica, IReadOnlyList<string> vistas)
    {
        var naFrente = new List<OpcaoRede>();
        var fora = new List<OpcaoRede>();
        foreach (var nome in vistas)
        {
            var achada = basica.FirstOrDefault(o => string.Equals(o.Valor, nome, StringComparison.Ordinal));
            if (achada is not null) naFrente.Add(achada with { Rotulo = achada.Rotulo + MarcaDoTerminal });
            else fora.Add(new OpcaoRede(nome, nome + MarcaDoTerminal, Conhecida: true));
        }
        var resto = basica.Where(o => !naFrente.Any(f => string.Equals(f.Valor, o.Valor, StringComparison.Ordinal))).ToList();
        return (naFrente, fora, resto);
    }

    /// <summary>Opções do campo de PIX, já contando o valor gravado (mesmo fora da lista).</summary>
    public static IReadOnlyList<OpcaoRede> OpcoesPix(string? gravado = null)
        => Opcoes(Pix, Cartao, "cartão", gravado);

    /// <summary>A chave onde ficam as redes que o terminal ofereceu numa cobrança PIX.</summary>
    public const string ChaveVistasPix = "tef_redes_pix_do_terminal";

    /// <summary>Nome oficial correspondente ao valor gravado/digitado, ou null se não é da lista de cartão.</summary>
    public static string? CanonicoCartao(string? valor) => Achar(Cartao, valor);

    /// <summary>Nome oficial correspondente ao valor gravado/digitado, ou null se não é da lista de Pix.</summary>
    public static string? CanonicoPix(string? valor) => Achar(Pix, valor);

    /// <summary>
    /// Os dois nomes falam da MESMA credenciadora? Compara pela <see cref="Chave"/>: sem acento,
    /// sem diferença de maiúscula e minúscula, sem espaço nas pontas. Vazio não casa com nada.
    ///
    /// Vale para nome que NÃO está na lista oficial: é isso que a lista de redes do menu
    /// (`tef_pgweb_redes`) precisa, porque quem nomeia as opções do menu é a biblioteca, e o
    /// terminal pode listar credenciadora que a lista daqui ainda não conhece. O espaço INTERNO
    /// continua contando: `C6 PAY` e `C6PAY` são redes diferentes para o PayGo (nota do topo).
    /// </summary>
    public static bool Mesma(string? a, string? b)
    {
        var chave = Chave(a);
        return chave.Length > 0 && chave == Chave(b);
    }

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
        // Nada gravado é o AUTOMÁTICO, esteja ele onde estiver na lista. Com as redes do terminal
        // na frente (cartão), o índice 0 é uma rede, e o Salvar a fixaria sem o dono escolher.
        if (bruto.Length == 0) return IndiceAutomatico(opcoes);
        // Texto exato primeiro: é ele que sobrevive como opção "fora da lista".
        for (var i = 0; i < opcoes.Count; i++)
            if (string.Equals(opcoes[i].Valor, bruto, StringComparison.Ordinal)) return i;
        // Depois a comparação tolerante ("cielo" gravado seleciona CIELO da lista).
        for (var i = 0; i < opcoes.Count; i++)
            if (opcoes[i].Valor.Length > 0 && Chave(opcoes[i].Valor) == Chave(bruto)) return i;
        return IndiceAutomatico(opcoes);
    }

    private static int IndiceAutomatico(IReadOnlyList<OpcaoRede> opcoes)
    {
        for (var i = 0; i < opcoes.Count; i++)
            if (opcoes[i].Automatica) return i;
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
