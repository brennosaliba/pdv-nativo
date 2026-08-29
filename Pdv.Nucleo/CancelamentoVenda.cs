namespace Pdv.Nucleo;

// ════════════════════════════════════════════════════════════════════════════
//  CANCELAR UMA VENDA — três atos que estavam amarrados num botão só
//
//  Até 29/08/2026 cancelar venda, cancelar NFC-e e estornar cartão eram UMA
//  ação, escondida atrás do menu "Cartão" da barra, que por sua vez só abria
//  com TEF integrado (`Servicos.Operavel()`). Numa loja de MAQUININHA AVULSA
//  isso significava: não existe caminho. O operador lia "chame o gerente para
//  configurar" — conselho errado, porque ele não precisava de maquininha
//  nenhuma; precisava cancelar uma nota, com 30 minutos no relógio.
//
//  São três coisas diferentes e só a terceira precisa de TEF:
//    · cancelar a VENDA          → UPDATE no banco deste caixa
//    · cancelar a NFC-e (110111) → agente fiscal local, que tem o certificado A1
//    · estornar o cartão         → maquininha integrada (CNC)
//
//  Este arquivo é a DECISÃO, sem janela: o que dá para fazer, em que ordem, e o
//  que o operador tem que fazer com as próprias mãos. Fica no núcleo porque a
//  parte que erra é a de julgar (prazo vencido, nota em contingência, dinheiro
//  que não volta sozinho) — dentro de um .xaml.cs ela só seria exercitada por
//  gente clicando com o cliente no balcão.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>Um pagamento da venda que está sendo cancelada, já líquido de troco.</summary>
public sealed record PagamentoDaVenda(string Forma, long ValorCent);

/// <summary>Em que pé está a NFC-e da venda, para efeito de cancelamento.</summary>
public enum SituacaoDaNota
{
    /// <summary>Não existe documento na SEFAZ para matar: venda sem nota, ou nota
    /// REJEITADA (a SEFAZ recusou, então não há o que cancelar).</summary>
    SemNota,
    /// <summary>
    /// `pendente`: o emissor ficou MUDO. A nota PODE ter sido assinada e autorizada do
    /// outro lado — é "não sei", não "não tem".
    ///
    /// ⚠️ Isto já estava escrito no projeto, em Fiscal.cs:207-213: "Emissor MUDO vira
    /// 'pendente', não 'rejeitada': a nota PODE ter sido assinada e autorizada do outro
    /// lado. Quem consome 'pendente' tem que CONFERIR antes". Cancelar a venda aqui
    /// deixaria uma NFC-e possivelmente viva apontando para venda cancelada — e os 30
    /// minutos vencendo em silêncio, que é o pior desfecho desta tela.
    /// </summary>
    SemResposta,
    /// <summary>
    /// O 110111 já passou e a venda continua 'finalizada'. Estado REAL, não
    /// hipótese: é onde o caixa para quando cai entre cancelar a nota e cancelar
    /// a venda — e, até aqui, não tinha saída nenhuma no PDV.
    /// </summary>
    JaCancelada,
    /// <summary>Autorizada, com chave e protocolo, dentro dos 30 minutos.</summary>
    DentroDoPrazo,
    /// <summary>Autorizada, mas o relógio já virou: a SEFAZ quase certamente recusa.</summary>
    ForaDoPrazo,
    /// <summary>Contingência: nasceu sem nProt, e o evento 110111 exige um.</summary>
    SemProtocolo,
    /// <summary>Autorizada, mas a chave não está NESTE caixa (veio de outro terminal).</summary>
    SemDados,
}

/// <summary>
/// O que vai acontecer se o operador seguir. `Impedimento` não-nulo é o fim da
/// linha: nada a fazer daqui, e a tela diz por quê em vez de fechar em silêncio.
/// </summary>
public sealed record PlanoDeCancelamento(
    SituacaoDaNota Nota,
    TimeSpan? RestanteDaNota,
    string TextoDaNota,
    string? Impedimento,
    IReadOnlyList<string> Dinheiro)
{
    public bool PodeSeguir => Impedimento is null;

    /// <summary>Tem documento na SEFAZ para derrubar antes de mexer na venda.</summary>
    public bool CancelaNota => Nota is SituacaoDaNota.DentroDoPrazo or SituacaoDaNota.ForaDoPrazo;

    /// <summary>
    /// Só quando há nota: o motivo digitado VIRA o xJust do evento, e aí valem os
    /// 15..255 caracteres da SEFAZ. Sem nota, exigir 15 letras seria capricho.
    /// </summary>
    public bool PedeJustificativaFiscal => CancelaNota;

    /// <summary>A SEFAZ vai ser chamada sabendo que o prazo já era.</summary>
    public bool Arriscado => Nota == SituacaoDaNota.ForaDoPrazo;
}

public static class CancelamentoVenda
{
    /// <summary>
    /// A FRASE QUE EVITA O PREJUÍZO. Cancelar a nota não devolve um centavo a
    /// ninguém: sem isto na tela, o operador cancela, vê "pronto" e manda o
    /// cliente embora achando que o dinheiro voltou. Em maquininha avulsa o
    /// estorno é na maquininha, na mão dele.
    /// </summary>
    public const string AvisoDoDinheiro =
        "NENHUM dinheiro volta sozinho neste cancelamento — o PDV só registra que ele foi feito.";

    /// <summary>Como o cliente chama a forma de pagamento (é o que está no comprovante dele).</summary>
    public static string RotuloDaForma(string forma) => (forma ?? "").Trim().ToLowerInvariant() switch
    {
        "credito" => "Crédito",
        "debito" => "Débito",
        "pix" => "PIX",
        "dinheiro" => "dinheiro",
        var outra => outra.Length == 0 ? "?" : char.ToUpperInvariant(outra[0]) + outra[1..],
    };

    /// <summary>Formas que uma maquininha estorna eletronicamente (as demais voltam na mão).</summary>
    public static bool EstornoEletronico(string forma)
        => (forma ?? "").Trim().ToLowerInvariant() is "credito" or "debito" or "pix";

    /// <summary>Resumo para o rótulo da lista: "Dinheiro + Crédito".</summary>
    public static string ResumoDasFormas(IEnumerable<PagamentoDaVenda> pagamentos)
    {
        var formas = pagamentos.Select(p => RotuloDaForma(p.Forma)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return formas.Length == 0 ? "sem pagamento" : string.Join(" + ", formas);
    }

    /// <summary>
    /// QUEM DEVOLVE O QUÊ, uma linha por forma de pagamento — e nunca uma lista
    /// vazia quando houve pagamento: silêncio aqui é o operador supondo que o
    /// sistema devolveu.
    /// </summary>
    /// <param name="estornoPeloPdv">
    /// Este caixa tem maquininha INTEGRADA e no ar. Aí o cartão não se estorna
    /// por aqui: existe caminho melhor ("Estornar o cartão/PIX"), que devolve o
    /// dinheiro E cancela a venda no mesmo ato. Duplicar o CNC aqui criaria um
    /// segundo caminho de dinheiro — e dois caminhos de dinheiro discordam.
    /// </param>
    public static IReadOnlyList<string> ComoDevolver(
        IReadOnlyList<PagamentoDaVenda> pagamentos, bool estornoPeloPdv)
    {
        var linhas = new List<string>();
        foreach (var g in pagamentos
                     .GroupBy(p => (p.Forma ?? "").Trim().ToLowerInvariant())
                     .Select(g => new { Forma = g.Key, Valor = new Dinheiro(g.Sum(p => p.ValorCent)) }))
        {
            var rotulo = RotuloDaForma(g.Forma);
            var quanto = g.Valor.Formatado();
            if (!EstornoEletronico(g.Forma))
                linhas.Add($"{quanto} em {rotulo}: devolva ao cliente na mão, da gaveta.");
            else if (estornoPeloPdv)
                linhas.Add($"{quanto} em {rotulo}: a maquininha deste caixa estorna sozinha — volte e use " +
                           "\"Estornar o cartão/PIX\", que devolve o dinheiro e cancela a venda no mesmo ato.");
            else
                linhas.Add($"{quanto} em {rotulo}: ESTORNE NA MAQUININHA, na mão. O PDV não devolve isso — " +
                           "aqui ele só cancela a nota e a venda.");
        }
        return linhas;
    }

    /// <summary>
    /// Monta o plano. `notaAutorizadaEm` é a hora que a SEFAZ carimbou (dhRecbto);
    /// na falta dela vale a hora da venda, que é sempre ANTES — errar para o lado
    /// de "resta menos tempo" nunca faz o PDV prometer o que não pode.
    /// </summary>
    public static PlanoDeCancelamento Montar(
        string? fiscalStatus, string? chave, string? protocolo, DateTime? notaAutorizadaEm,
        IReadOnlyList<PagamentoDaVenda> pagamentos, bool estornoPeloPdv, DateTime agora)
    {
        var f = (fiscalStatus ?? "").Trim().ToLowerInvariant();
        TimeSpan? restante = null;
        SituacaoDaNota s;

        if (f == "cancelada") s = SituacaoDaNota.JaCancelada;
        else if (f == "contingencia") s = SituacaoDaNota.SemProtocolo;
        // ⚠️ "não sei" NÃO cai no mesmo balde de "não tem". Só o que a SEFAZ respondeu
        // recusando (ou a venda que nunca emitiu) é SemNota; 'pendente' e qualquer
        // valor que este código não reconheça viram SemResposta, que BLOQUEIA. Errar
        // para "bloqueia" custa uma ligação ao gerente; errar para "segue" deixa nota
        // viva em venda cancelada.
        else if (f.Length == 0 || f == "rejeitada" || f == "erro") s = SituacaoDaNota.SemNota;
        else if (f != "autorizada") s = SituacaoDaNota.SemResposta;
        else if ((chave ?? "").Trim().Length != 44) s = SituacaoDaNota.SemDados;
        else if (string.IsNullOrWhiteSpace(protocolo)) s = SituacaoDaNota.SemProtocolo;
        else if (notaAutorizadaEm is null) s = SituacaoDaNota.DentroDoPrazo;   // sem hora: quem decide é a SEFAZ
        else
        {
            restante = CancelamentoFiscal.RestanteDoPrazo(notaAutorizadaEm.Value, agora);
            // No minuto 30 cravado já conta como vencido: arredondar para o lado do
            // operador seria prometer um cancelamento que a SEFAZ já não faz.
            s = restante.Value > TimeSpan.Zero ? SituacaoDaNota.DentroDoPrazo : SituacaoDaNota.ForaDoPrazo;
        }

        var impedimento = s switch
        {
            SituacaoDaNota.SemProtocolo =>
                "A nota desta venda saiu sem aprovação da SEFAZ (contingência) e não tem protocolo — " +
                "o cancelamento exige um. A nota precisa ser resolvida primeiro: chame o gerente.",
            SituacaoDaNota.SemDados =>
                "Esta venda tem nota aprovada, mas os dados dela não estão neste caixa — o cancelamento " +
                "da nota não pode sair daqui. Chame o gerente para cancelar pelo sistema.",
            SituacaoDaNota.SemResposta =>
                "Não dá para saber se esta venda tem nota: o emissor não respondeu na hora da emissão, " +
                "e a nota PODE ter sido autorizada mesmo assim. Cancelar a venda agora deixaria uma nota " +
                "viva sem venda. Chame o gerente para conferir na SEFAZ antes — e depressa, porque o " +
                "prazo de cancelamento da nota corre a partir da autorização dela.",
            _ => null,
        };

        var texto = s switch
        {
            SituacaoDaNota.SemNota =>
                "Esta venda não gerou nota fiscal: só a venda será cancelada.",
            SituacaoDaNota.SemResposta =>
                "A emissão desta venda ficou sem resposta — não dá para afirmar que ela não tem nota.",
            SituacaoDaNota.JaCancelada =>
                "A nota desta venda JÁ está cancelada na SEFAZ — falta cancelar a venda.",
            SituacaoDaNota.DentroDoPrazo when restante is null =>
                "A nota vai ser cancelada na SEFAZ (evento 110111).",
            SituacaoDaNota.DentroDoPrazo =>
                $"A nota vai ser cancelada na SEFAZ (evento 110111). Restam cerca de {Duracao(restante!.Value)} " +
                "do prazo de 30 minutos.",
            SituacaoDaNota.ForaDoPrazo =>
                $"PRAZO VENCIDO: a nota foi autorizada há {Duracao(agora - notaAutorizadaEm!.Value)} e a SEFAZ " +
                "só aceita cancelamento de NFC-e até 30 minutos. Ela deve recusar — e nesse caso o caminho " +
                "é uma NOTA DE DEVOLUÇÃO com o contador, feita fora do PDV.",
            _ => impedimento!,
        };

        return new PlanoDeCancelamento(s, restante, texto, impedimento,
            ComoDevolver(pagamentos, estornoPeloPdv));
    }

    /// <summary>Duração em minutos inteiros, arredondada PARA BAIXO (nunca promete mais tempo do que há).</summary>
    private static string Duracao(TimeSpan t)
    {
        var m = (int)Math.Floor(t.Duration().TotalMinutes);
        return m <= 0 ? "menos de 1 minuto" : m == 1 ? "1 minuto" : $"{m} minutos";
    }
}
