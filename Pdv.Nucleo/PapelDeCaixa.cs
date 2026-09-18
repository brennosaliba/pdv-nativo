namespace Pdv.Nucleo;

/// <summary>
/// OS PAPÉIS DA ABERTURA E DO FECHAMENTO DO CAIXA (18/09/2026, pedido do dono:
/// "na abertura e fechamento de caixa, imprimir o relatório automático do fechamento e da
/// abertura, com campo para assinatura e os valores e data").
///
/// São dois comprovantes de bobina: um sai quando o turno abre, outro quando ele fecha.
/// Cada um leva a loja, a data com hora, o turno, quem estava no caixa, os valores e um
/// espaço para assinar e para o gerente conferir depois. É o mesmo desenho do papel da
/// retirada para o cofre (<see cref="RetiradaCofre.Papel"/>), que já vinha sendo assinado
/// e guardado desde 12/09.
///
/// Tudo aqui é PURO: entra o que já foi apurado, sai texto. A conta do fechamento não é
/// refeita em lugar nenhum deste arquivo. O resumo chega pronto em
/// <see cref="ResumoFechamento.Linhas"/> e o desvio chega pronto de quem fechou, porque
/// uma terceira conta do mesmo número é uma terceira chance de divergir: o papel
/// assinado tem que dizer exatamente o que a tela disse.
///
/// ⚠️ O papel do fechamento NÃO usa <see cref="ResumoFechamento.Texto"/>: aquela linha tem
/// largura fixa de 55 colunas, feita para a tela, e sairia destruída numa bobina de 32.
/// </summary>
public static class PapelDeCaixa
{
    /// <summary>Sai sozinho, sem ninguém pedir. Turno de teste nunca imprime: papel de
    /// homologação assinado vira comprovante de um dia que não existiu.</summary>
    public static bool SaiSozinho(PoliticaImpressao p, bool turnoDeTeste)
        => !turnoDeTeste && p == PoliticaImpressao.Automatico;

    /// <summary>A loja escolheu decidir na hora: o caixa pergunta antes de imprimir.</summary>
    public static bool PrecisaPerguntar(PoliticaImpressao p, bool turnoDeTeste)
        => !turnoDeTeste && p == PoliticaImpressao.Perguntar;

    /// <summary>
    /// O papel da ABERTURA: quem abriu, quando, e quanto de troco entrou na gaveta.
    ///
    /// <paramref name="esperado"/> é o que o fechamento anterior deixou. Ele só aparece no
    /// papel quando NÃO bate com o que o operador contou. Com a contagem batendo, imprimir
    /// o esperado ensinaria o número do dia seguinte para quem pega o papel perto da
    /// gaveta, e o caixa é feito de propósito para o operador contar às cegas. Quando
    /// diverge, o número já foi mostrado na tela e já foi auditado: aí ele precisa de
    /// assinatura, que é justamente o que faltava para cobrar depois.
    /// </summary>
    public static IReadOnlyList<string> Abertura(string loja, DateTime quando, Sessao sessao,
        Dinheiro? esperado, int colunas)
    {
        var c = PapelTexto.Largura(colunas);
        var l = new List<string>
        {
            PapelTexto.Centro("ABERTURA DE CAIXA", c),
            PapelTexto.Centro(loja, c),
            PapelTexto.Traco(c),
        };
        l.AddRange(PapelTexto.Campo("Data", quando.ToString("dd/MM/yyyy HH:mm"), c));
        l.AddRange(PapelTexto.Campo("Turno", PapelTexto.DiaDoTurno(sessao.BusinessDate), c));
        l.AddRange(PapelTexto.Campo("Operador", sessao.OperadorNome, c));
        l.Add(PapelTexto.Traco(c));
        l.AddRange(PapelTexto.Campo("Fundo declarado", sessao.FundoTroco.Formatado(), c));

        if (esperado is { } esp && esp != sessao.FundoTroco)
        {
            var dif = sessao.FundoTroco - esp;
            l.AddRange(PapelTexto.Campo("Esperado na gaveta", esp.Formatado(), c));
            l.AddRange(PapelTexto.Campo(dif.Centavos > 0 ? "Diferença (a mais)" : "Diferença (a menos)",
                                        dif.Abs.Formatado(), c));
        }

        l.AddRange(PapelTexto.BlocoDeAssinatura(c));
        l.Add("");
        l.Add(PapelTexto.Centro("Guarde com o fechamento do dia.", c));
        return l;
    }

    /// <summary>
    /// O papel do FECHAMENTO: o turno inteiro numa folha, pronta para assinar.
    ///
    /// <paramref name="resumo"/> vem de <see cref="ResumoFechamento.Linhas"/>, o mesmo que
    /// a tela mostra. <paramref name="desvio"/> é a soma dos módulos já calculada por quem
    /// fechou. <paramref name="semContagem"/> marca o caixa esquecido, fechado sem ninguém
    /// contar a gaveta: aí o papel diz isso com todas as letras e leva o nome de quem
    /// autorizou, porque um fechamento sem contagem assinado como se fosse normal é pior
    /// que nenhum papel.
    /// </summary>
    public static IReadOnlyList<string> Fechamento(
        string loja, DateTime quando, Sessao sessao, string quemFechou,
        IReadOnlyList<LinhaDoResumo> resumo, Dinheiro desvio, IReadOnlyList<string> semConferencia,
        Dinheiro? fica, Dinheiro? retirada, string? justificativa,
        bool semContagem, string? autorizador, int colunas)
    {
        var c = PapelTexto.Largura(colunas);
        var l = new List<string>
        {
            PapelTexto.Centro("FECHAMENTO DE CAIXA", c),
            PapelTexto.Centro(loja, c),
            PapelTexto.Traco(c),
        };
        l.AddRange(PapelTexto.Campo("Data", quando.ToString("dd/MM/yyyy HH:mm"), c));
        l.AddRange(PapelTexto.Campo("Turno", PapelTexto.DiaDoTurno(sessao.BusinessDate), c));
        l.AddRange(PapelTexto.Campo("Abertura", sessao.AberturaEm.ToString("HH:mm"), c));
        l.AddRange(PapelTexto.Campo("Abriu", sessao.OperadorNome, c));
        l.AddRange(PapelTexto.Campo("Fechou", quemFechou, c));
        l.Add(PapelTexto.Traco(c));
        l.AddRange(PapelTexto.Campo("Fundo de troco", sessao.FundoTroco.Formatado(), c));
        l.Add(PapelTexto.Traco(c));

        // Uma forma por bloco: o que foi declarado, o que era esperado, e o desfecho só
        // quando ele não é "confere". O desfecho vem pronto do resumo (LinhaDoResumo.Fim):
        // é o que impede uma linha sem conferência de sair impressa como FALTA de R$ 0,00.
        foreach (var r in resumo ?? new List<LinhaDoResumo>())
        {
            l.AddRange(PapelTexto.Campo(r.Rotulo, r.Declarado.Formatado(), c));
            l.AddRange(PapelTexto.Campo("  esperado", r.Esperado.Formatado(), c));
            if (r.Situacao != "confere") l.Add(PapelTexto.Corta("  " + r.Fim, c));
        }

        l.Add(PapelTexto.Traco(c));
        l.AddRange(PapelTexto.Campo("Diferença total", desvio.Formatado(), c));

        if (semConferencia is { Count: > 0 })
        {
            l.Add("Sem conferência:");
            foreach (var rotulo in semConferencia) l.Add(PapelTexto.Corta("  " + rotulo, c));
        }

        if (fica is { } ficou)
        {
            l.Add(PapelTexto.Traco(c));
            l.AddRange(PapelTexto.Campo("Fica na gaveta", ficou.Formatado(), c));
            if (retirada is { } r) l.AddRange(PapelTexto.Campo("Retirado para o cofre", r.Formatado(), c));
        }

        if (semContagem)
        {
            l.Add(PapelTexto.Traco(c));
            l.Add(PapelTexto.Corta("FECHADO SEM CONFERÊNCIA", c));
            l.Add(PapelTexto.Corta("Ninguém contou a gaveta.", c));
            if (!string.IsNullOrWhiteSpace(autorizador))
                l.AddRange(PapelTexto.Campo("Autorizado por", autorizador!.Trim(), c));
        }
        else if (!string.IsNullOrWhiteSpace(justificativa))
        {
            l.Add(PapelTexto.Traco(c));
            l.Add("Justificativa:");
            l.AddRange(PapelTexto.Quebrar(justificativa, c));
        }

        l.AddRange(PapelTexto.BlocoDeAssinatura(c));
        l.Add("");
        l.Add(PapelTexto.Centro("Assine e entregue ao gerente.", c));
        return l;
    }
}
