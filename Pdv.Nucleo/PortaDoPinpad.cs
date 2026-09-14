namespace Pdv.Nucleo;

/// <summary>Uma porta serial que existe nesta máquina, com o nome que o Windows dá a ela.</summary>
/// <param name="Com">"COM3".</param>
/// <param name="Descricao">"Gertec PIN Pad PPC", ou vazio quando o Windows não sabe.</param>
public sealed record PortaSerial(string Com, string Descricao)
{
    /// <summary>Só o número: "COM3" vira "3". É assim que a PWINFO_PPCOMMPORT quer.</summary>
    public string Numero
    {
        get
        {
            var d = new string(Com.Where(char.IsDigit).ToArray());
            return d.Length > 0 ? d.TrimStart('0') is { Length: > 0 } t ? t : "0" : "";
        }
    }
}

/// <summary>
/// QUAL PORTA SERIAL É O PINPAD.
///
/// O QUE ACONTECEU (09/09/2026). O dono passou uma hora atrás de "PROBLEMA ARQUIVO
/// DE PARAMETROS" no meio da homologação do TEF. Não era arquivo nenhum: o PDV
/// estava configurado na COM5, e esta máquina só tem COM1 e COM3. O pinpad é um
/// "Gertec PIN Pad PPC (COM3)".
///
/// O log da biblioteca repetia, dezenas de vezes:
///
///     PP_iOpen CommPort (05)
///     PP_iOpen - PP_Open_=[30]
///     PP_iOpen Error #3! 30
///
/// e no fim mandava para a tela o texto genérico dela, PROBLEMA ARQUIVO DE
/// PARAMETROS, que manda a pessoa caçar exatamente a coisa errada.
///
/// Duas coisas nascem daqui, e a segunda é a que importa:
///
///  1. ACHAR o pinpad sozinho, pelo nome que o Windows dá ao aparelho;
///  2. AVISAR quando a porta configurada não existe. Porta que não existe é um
///     fato que o PDV tem na mão e escondeu. Nenhuma varredura automática
///     substitui dizer a verdade sobre o que está configurado.
///
/// ⚠️ Este arquivo NÃO lê o Windows: recebe a lista pronta. Assim a regra é
/// testável sem máquina com pinpad, que é o único jeito de ela ser testada.
/// </summary>
public static class PortaDoPinpad
{
    /// <summary>"0" e vazio querem dizer "deixa a biblioteca procurar".</summary>
    public const string Automatica = "0";

    /// <summary>
    /// Marcas e palavras que aparecem no nome que o Windows dá a um pinpad.
    /// "PIN PAD" e "PINPAD" cobrem o genérico; o resto são os fabricantes que
    /// aparecem em loja no Brasil.
    /// </summary>
    private static readonly string[] Pistas =
    {
        "pinpad", "pin pad", "gertec", "ingenico", "verifone", "pax ", "sunmi", "positivo",
    };

    /// <summary>O nome do aparelho parece de pinpad?</summary>
    public static bool PareceDePinpad(string? descricao)
    {
        var d = (descricao ?? "").ToLowerInvariant();
        if (d.Length == 0) return false;
        return Pistas.Any(p => d.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// A porta do pinpad, quando dá para ter certeza.
    ///
    /// Devolve `null` quando NENHUMA parece pinpad (não há o que adivinhar) e
    /// também quando DUAS ou mais parecem: escolher uma no chute e sair mandando
    /// comando para o aparelho errado é pior do que deixar a biblioteca procurar.
    /// </summary>
    public static PortaSerial? Detectar(IReadOnlyList<PortaSerial> portas)
    {
        var candidatas = (portas ?? Array.Empty<PortaSerial>())
            .Where(p => p is not null && PareceDePinpad(p.Descricao))
            .ToList();
        return candidatas.Count == 1 ? candidatas[0] : null;
    }

    /// <summary>
    /// O que a tela precisa dizer sobre a porta configurada. `null` = está tudo bem
    /// e não há nada a dizer.
    ///
    /// A ordem das perguntas é a ordem do estrago: porta que não existe trava tudo
    /// e mente na mensagem; porta certa mas apontando para outro aparelho é aviso.
    /// </summary>
    public static string? Aviso(string? configurada, IReadOnlyList<PortaSerial> portas)
    {
        var lista = portas ?? Array.Empty<PortaSerial>();
        var alvo = (configurada ?? "").Trim();

        if (lista.Count == 0)
            return "Nenhuma porta serial encontrada nesta máquina. O pinpad está ligado e o cabo conectado?";

        var achada = Detectar(lista);

        if (alvo.Length == 0 || alvo == Automatica)
        {
            // Automática é uma escolha legítima: só conta o que foi encontrado.
            return achada is null ? null : $"Automática. O pinpad encontrado foi {achada.Com} ({achada.Descricao}).";
        }

        var existe = lista.FirstOrDefault(p => p.Numero == alvo);
        if (existe is null)
        {
            var quais = string.Join(", ", lista.Select(p => p.Com));
            var dica = achada is null
                ? " Escolha uma da lista ou deixe em Automática."
                : $" O pinpad parece estar na {achada.Com} ({achada.Descricao}).";
            return $"A porta COM{alvo} não existe nesta máquina. Portas disponíveis: {quais}.{dica}";
        }

        if (achada is not null && achada.Numero != alvo)
            return $"A COM{alvo} existe, mas quem parece pinpad é a {achada.Com} ({achada.Descricao}).";

        return null;
    }

    /// <summary>
    /// A porta configurada está impedindo o TEF de funcionar? Só quando ela não
    /// existe: aí toda operação vai falhar na abertura da porta, sempre, e a
    /// mensagem que a pessoa vê não vai falar de porta nenhuma.
    /// </summary>
    public static bool ImpedeFuncionar(string? configurada, IReadOnlyList<PortaSerial> portas)
    {
        var alvo = (configurada ?? "").Trim();
        if (alvo.Length == 0 || alvo == Automatica) return false;
        var lista = portas ?? Array.Empty<PortaSerial>();
        if (lista.Count == 0) return false;   // sem lista não se acusa ninguém
        return !lista.Any(p => p.Numero == alvo);
    }

    /// <summary>Como a porta aparece na lista da tela.</summary>
    public static string Rotulo(PortaSerial porta)
        => porta.Descricao.Trim().Length == 0
            ? porta.Com
            : $"{porta.Com} · {porta.Descricao.Trim()}";

    /// <summary>
    /// O número que a PWINFO_PPCOMMPORT quer, venha como vier: "COM2", "com02" e " 2 " viram
    /// "2"; vazio, nulo ou texto sem número viram "0" (automática).
    ///
    /// 14/09/2026, Castelo: o campo era texto livre e ia para a biblioteca do jeito que foi
    /// digitado. "COM3" chegava cru. Não foi a causa daquele dia, mas é a mesma armadilha.
    /// </summary>
    public static string Normalizar(string? porta)
    {
        var d = new string((porta ?? "").Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
        return d.Length == 0 ? Automatica : d;
    }

    /// <summary>Uma linha da lista de portas da Configuração. <see cref="Valor"/> vazio = automática.</summary>
    public sealed record Opcao(string Valor, string Rotulo)
    {
        public override string ToString() => Rotulo;
    }

    public const string RotuloAutomatica = "Automática (recomendado)";

    /// <summary>
    /// A lista da tela: "Automática" primeiro, depois as portas que o Windows mostra, com o nome
    /// do aparelho. A porta gravada que não existe mais continua aparecendo, marcada, para
    /// ninguém achar que a configuração sumiu sozinha.
    /// </summary>
    public static IReadOnlyList<Opcao> Opcoes(IReadOnlyList<PortaSerial>? portas, string? gravada)
    {
        var lista = new List<Opcao> { new("", RotuloAutomatica) };
        foreach (var p in portas ?? Array.Empty<PortaSerial>())
            if (p is not null && p.Numero.Length > 0 && lista.All(o => o.Valor != p.Numero))
                lista.Add(new Opcao(p.Numero, Rotulo(p)));
        var alvo = Normalizar(gravada);
        if (alvo != Automatica && lista.All(o => o.Valor != alvo))
            lista.Add(new Opcao(alvo, $"COM{alvo} (não encontrada neste computador)"));
        return lista;
    }

    /// <summary>Qual linha da lista está gravada. Nunca -1: sem casar, é a automática.</summary>
    public static int Indice(IReadOnlyList<Opcao> opcoes, string? gravada)
    {
        var alvo = Normalizar(gravada);
        if (alvo == Automatica) return 0;
        for (var i = 0; i < opcoes.Count; i++) if (opcoes[i].Valor == alvo) return i;
        return 0;
    }
}
