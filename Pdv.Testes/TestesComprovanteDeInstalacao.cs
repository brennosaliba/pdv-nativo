using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O PAPEL DA INSTALACAO, QUE A BIBLIOTECA NAO MANDA.
///
/// O dono, 09/09/2026: "ao instalar ele da uma msg mas nao imprime". O passo 1 do
/// roteiro exige o recibo da instalacao saindo na impressora.
///
/// Fui ao log antes de mexer: a biblioteca NAO devolve via nenhuma nesta operacao.
/// O que volta e 0x23, o REQNUM e a mensagem vazia. O caixa, corretamente, nao
/// tinha o que imprimir. Entao o papel e composto aqui.
/// </summary>
public static class TestesComprovanteDeInstalacao
{
    public static void Rodar(Action<bool, string> checar)
    {
        var quando = new DateTime(2026, 9, 9, 14, 42, 31);

        var l = ComprovanteDeInstalacao.Linhas(
            loja: "American Day Savassi", cnpj: "62.177.839/0001-57",
            pontoCaptura: "115998", terminal: "PDV01", reqnum: "0000275742",
            versaoAutomacao: "0.7.4", homologacao: true, quando: quando);
        var txt = string.Join("\n", l);

        // ── O QUE A ANALISE DA PAYGO PROCURA ────────────────────────────────
        checar(l[0].Contains("INSTALACAO"), "o titulo diz o que e o papel");
        checar(txt.Contains("115998"), "o ponto de captura sai no papel");
        checar(txt.Contains("62.177.839/0001-57"), "o CNPJ tambem");
        checar(txt.Contains("09/09/2026 14:42:31"), "a data e a hora, com segundos");
        checar(txt.Contains("0000275742"), "e o REQNUM, que e o que a planilha exige");
        checar(txt.Contains(RoteiroTef.RetornoExigido),
            "o REQNUM sai com o nome do campo, para nao virar numero solto");
        // ⚠️ O primeiro papel saiu com "Biblioteca: 0.7.4", que e a versao do CAIXA e
        // nao a 4.1.50.24 da PGWebLib. Rotulo que promete um dado e entrega outro e
        // pior do que rotulo nenhum numa analise de homologacao.
        checar(txt.Contains("Automacao: 0.7.4"), "a versao sai rotulada como AUTOMACAO");
        checar(!txt.Contains("Biblioteca"), "e nao se chama biblioteca, que seria mentira");

        // ── PAPEL DE TESTE NAO PODE PARECER PAPEL DE PRODUCAO ───────────────
        checar(txt.Contains("SEM VALOR FINANCEIRO"),
            "em homologacao o papel diz que e teste");
        var producao = string.Join("\n", ComprovanteDeInstalacao.Linhas(
            "American Day Savassi", "62.177.839/0001-57", "115998", "PDV01",
            "0000275742", "0.7.4", homologacao: false, quando: quando));
        checar(!producao.Contains("SEM VALOR FINANCEIRO"),
            "e em producao nao diz, senao o aviso perde o sentido");

        // ── CAMPO VAZIO SOME, NAO SAI COMO ROTULO SOLTO ─────────────────────
        // Papel de homologacao com "CNPJ:" e nada do lado vira duvida na analise.
        var incompleto = string.Join("\n", ComprovanteDeInstalacao.Linhas(
            loja: "Loja", cnpj: null, pontoCaptura: "", terminal: "   ",
            reqnum: null, versaoAutomacao: null, homologacao: false, quando: quando));
        foreach (var rotulo in new[] { "CNPJ:", "Ponto de captura:", "Terminal:", "Automacao:" })
            checar(!incompleto.Contains(rotulo), $"rotulo sem valor nao sai no papel ({rotulo})");
        checar(incompleto.Contains("Loja: Loja"), "o que existe continua saindo");
        checar(incompleto.Contains("Data:"), "e a data sai sempre: sem ela o papel nao prova quando");

        // ── QUEM ESCREVEU ──────────────────────────────────────────────────
        checar(ComprovanteDeInstalacao.Rodape.Contains("automacao"),
            "o papel assume que foi a automacao que o escreveu, e nao a rede");

        // ── SEM TRAVESSAO ──────────────────────────────────────────────────
        checar(!txt.Contains('—') && !ComprovanteDeInstalacao.Rodape.Contains('—'),
            "sem travessao no papel");
    }
}
