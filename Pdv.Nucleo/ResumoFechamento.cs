namespace Pdv.Nucleo;

/// <summary>
/// Uma linha do resumo do fechamento, já partida do jeito que o operador lê.
///
/// <paramref name="Situacao"/> usa as MESMAS palavras de <see cref="LinhaFechamento.Situacao"/>
/// ("confere", "sobra", "falta", "sem_conferencia"), para a tela e a nuvem não falarem
/// línguas diferentes. <paramref name="SemConferencia"/> é separado da situação porque
/// uma linha pode mostrar FALTA e ainda assim ter parte que ninguém conferiu (PIX com a
/// maquininha muda e a parte avulsa contada a menos).
/// </summary>
public sealed record LinhaDoResumo(string Rotulo, string Origem, Dinheiro Declarado, Dinheiro Esperado,
    string Situacao, Dinheiro Diferenca, bool SemConferencia)
{
    /// <summary>O fim da linha: "confere", "SOBRA R$ x", "FALTA R$ x" ou "sem conferência".</summary>
    public string Fim => Situacao switch
    {
        "confere" => "confere",
        "sobra" => "SOBRA " + Diferenca.Abs.Formatado(),
        // linha que ninguém pôde conferir NÃO pode sair como falta de R$ 0,00:
        // isso é o desvio inventado voltando pela porta do relatório
        "sem_conferencia" => "sem conferência",
        _ => "FALTA " + Diferenca.Abs.Formatado(),
    };

    /// <summary>
    /// A linha pronta para o Consolas do relatório. "esperado", e não "sistema", é a
    /// mesma palavra que a abertura usa na conferência do fundo.
    /// </summary>
    public string Texto =>
        $"{Rotulo,-ResumoFechamento.LarguraRotulo} {Origem,-7} {Declarado.Formatado(),11}  esperado {Esperado.Formatado(),11}  {Fim}";
}

/// <summary>
/// O RESUMO do fechamento de caixa, montado num lugar só para as duas telas (o fechamento
/// normal e o do caixa esquecido). Antes cada tela tinha a sua cópia do formato da linha.
///
/// 13/09/2026, pedido do dono: "relatório de fechamento de caixa tem como segmentar
/// crédito em Crédito TEF e Crédito POS. Caso POS seja 0, mostra 0,00. O mesmo com
/// débito." Então crédito e débito saem SEMPRE em duas linhas cada, mesmo sem venda.
///
/// A conta da partição usa só o que <see cref="LinhaFechamento"/> já tem:
///  · parte TEF: o que a maquininha integrada liquidou (<see cref="LinhaFechamento.PeloTef"/>).
///    Não se declara, então nunca tem sobra nem falta; só pode ficar sem conferência;
///  · parte POS: o resto (<see cref="LinhaFechamento.AContar"/>). Como o declarado é
///    PeloTef + o que o operador contou, a diferença INTEIRA é desta parte, e a
///    "Diferença total" da tela continua batendo com a do Núcleo sem mudar nada.
///
/// Dinheiro, PIX e Refeição seguem numa linha só.
/// </summary>
public static class ResumoFechamento
{
    /// <summary>"Crédito TEF" e "Crédito POS" têm 11 letras: é a coluna do rótulo.</summary>
    public const int LarguraRotulo = 11;

    /// <summary>As formas que o resumo parte em TEF e POS, sempre, com ou sem venda.</summary>
    public static readonly string[] FormasPartidas = { "credito", "debito" };

    private static readonly string[] Ordem = { "dinheiro", "credito", "debito", "pix", "voucher" };

    /// <summary>Forma de pagamento em formato de gente ("Débito", não "debito").</summary>
    public static string Rotulo(string forma) => forma switch
    {
        "dinheiro" => "Dinheiro",
        "debito" => "Débito",
        "credito" => "Crédito",
        "pix" => "PIX",
        "voucher" => "Refeição",
        _ => forma,
    };

    /// <summary>
    /// O rótulo da parte que a maquininha do caixa liquidou: "Crédito TEF", "Débito TEF".
    /// Forma que o resumo não parte (PIX, Refeição) fica com o rótulo de sempre.
    /// </summary>
    public static string RotuloTef(string forma)
        => FormasPartidas.Contains(forma) ? Rotulo(forma) + " TEF" : Rotulo(forma);

    /// <summary>
    /// As linhas do resumo, em ordem fixa: Dinheiro, Crédito TEF, Crédito POS, Débito TEF,
    /// Débito POS, PIX, Refeição, e depois qualquer forma desconhecida.
    ///
    /// <paramref name="tefDisponivel"/> é a maquininha respondendo na hora de fechar. Ele
    /// só decide o caso que a linha sozinha não diz: forma que ninguém contou, com parte
    /// fora do TEF, em que <c>Conferida=false</c> tanto pode ser "TEF mudo" quanto "POS não
    /// contado". Nos outros casos a própria linha já diz se a maquininha respondeu.
    /// </summary>
    public static List<LinhaDoResumo> Linhas(IEnumerable<LinhaFechamento> linhas, bool tefDisponivel = true)
    {
        var porForma = linhas.GroupBy(l => l.Forma).ToDictionary(g => g.Key, g => g.Last());
        var formas = Ordem.Where(f => FormasPartidas.Contains(f) || porForma.ContainsKey(f))
            .Concat(porForma.Keys.Where(f => !Ordem.Contains(f)).OrderBy(f => f, StringComparer.Ordinal));

        var saida = new List<LinhaDoResumo>();
        foreach (var f in formas)
        {
            porForma.TryGetValue(f, out var l);
            if (FormasPartidas.Contains(f))
            {
                l ??= new LinhaFechamento(f, Dinheiro.Zero, Dinheiro.Zero, Contada: false);
                saida.Add(ParteTef(l, tefDisponivel));
                saida.Add(PartePos(l));
            }
            else if (l is not null)
            {
                // "TEF" não diz nada a quem está fechando a gaveta: o que importa é se o
                // valor foi contado à mão ou veio da maquininha.
                saida.Add(new LinhaDoResumo(Rotulo(f), l.Contada ? "contou" : "máquina",
                    l.Declarado, l.Apurado, l.Situacao, l.Diferenca, !l.Conferida));
            }
        }
        return saida;
    }

    /// <summary>O resumo como texto, uma linha por parte.</summary>
    public static string Texto(IEnumerable<LinhaFechamento> linhas, bool tefDisponivel = true)
        => string.Join("\n", Linhas(linhas, tefDisponivel).Select(r => r.Texto));

    /// <summary>
    /// Os rótulos que ficaram sem conferência ("Crédito TEF", "PIX"...), na ordem do
    /// resumo. É a lista que a tela nomeia embaixo da tabela.
    /// </summary>
    public static List<string> SemConferencia(IEnumerable<LinhaFechamento> linhas, bool tefDisponivel = true)
        => Linhas(linhas, tefDisponivel).Where(r => r.SemConferencia).Select(r => r.Rotulo).ToList();

    private static LinhaDoResumo ParteTef(LinhaFechamento l, bool tefDisponivel)
    {
        // A maquininha ficou muda? Com a linha contada, ou sem nada fora do TEF, o próprio
        // Conferida responde (Caixa.Fechar só o desliga por causa do TEF nesses casos).
        // Sobra o caso ambíguo, que o chamador resolve dizendo se o TEF respondeu.
        var tefMudo = l.PeloTef.Centavos != 0
                      && (!tefDisponivel || (!l.Conferida && (l.Contada || l.AContar.Centavos == 0)));
        return new LinhaDoResumo(RotuloTef(l.Forma), "máquina", l.PeloTef, l.PeloTef,
            tefMudo ? "sem_conferencia" : "confere", Dinheiro.Zero, tefMudo);
    }

    private static LinhaDoResumo PartePos(LinhaFechamento l)
    {
        var rotulo = Rotulo(l.Forma) + " POS";
        if (l.Contada)
        {
            // Declarado = PeloTef + o que o operador contou: tirando o TEF sobra o contado,
            // e a diferença inteira fica aqui.
            var dif = l.Diferenca;
            var situacao = dif.Centavos switch { > 0 => "sobra", < 0 => "falta", _ => "confere" };
            return new LinhaDoResumo(rotulo, "contou", l.Declarado - l.PeloTef, l.AContar, situacao, dif, false);
        }
        // Ninguém contou: fecha pelo que o sistema registrou. Zero é zero (não havia o que
        // contar); valor diferente de zero que ninguém contou fica sem conferência.
        var semConf = l.AContar.Centavos != 0;
        return new LinhaDoResumo(rotulo, "", l.AContar, l.AContar,
            semConf ? "sem_conferencia" : "confere", Dinheiro.Zero, semConf);
    }
}
