namespace Pdv.Nucleo;

/// <summary>
/// O COMPROVANTE DA INSTALAÇÃO DO PONTO DE CAPTURA.
///
/// POR QUE ELE É NOSSO (09/09/2026). O dono: "ao instalar ele da uma msg mas nao
/// imprime". O passo 1 do roteiro exige o recibo da instalação saindo na
/// impressora, e ele não saía.
///
/// Fui ao log antes de mexer, e a biblioteca não está engolindo nada: ela
/// simplesmente NÃO devolve comprovante nesta operação. O que volta da instalação,
/// medido nas duas que rodaram hoje, é só isto:
///
///     0x23=ul
///     0x32=0000275742     (PWINFO_REQNUM)
///     0x42=               (mensagem de resultado, vazia)
///
/// Nenhum campo de via (RCPTCHOLDER, RCPTMERCH, RCPTCHSHORT, RCPTFULL). O caixa,
/// corretamente, não tinha o que imprimir.
///
/// Então quem compõe o papel é a automação, com o que ela sabe: o terminal, o
/// ponto de captura, o CNPJ, a data e o REQNUM. Ele não é comprovante fiscal nem
/// de transação: é a prova de que a instalação rodou e quando, que é exatamente o
/// que o passo 1 pede.
/// </summary>
public static class ComprovanteDeInstalacao
{
    /// <summary>
    /// As linhas do papel, prontas para a impressora.
    ///
    /// Campo vazio some da lista em vez de sair como rótulo sem valor: papel de
    /// homologação com "CNPJ:" e nada do lado vira dúvida na análise.
    /// </summary>
    public static IReadOnlyList<string> Linhas(
        string? loja, string? cnpj, string? pontoCaptura, string? terminal,
        string? reqnum, string? versaoBiblioteca, bool homologacao, DateTime quando)
    {
        var l = new List<string> { "COMPROVANTE DE INSTALACAO", "PONTO DE CAPTURA", "" };

        void Par(string rotulo, string? valor)
        {
            var v = (valor ?? "").Trim();
            if (v.Length > 0) l.Add(rotulo + ": " + v);
        }

        Par("Loja", loja);
        Par("CNPJ", cnpj);
        Par("Ponto de captura", pontoCaptura);
        Par("Terminal", terminal);
        Par("Biblioteca", versaoBiblioteca);
        l.Add("Data: " + quando.ToString("dd/MM/yyyy HH:mm:ss"));
        Par(RoteiroTef.RetornoExigido, reqnum);

        if (homologacao)
        {
            l.Add("");
            // A propria PayGo carimba isso na tela dela. O papel repete, porque papel
            // de teste que parece papel de producao e o tipo de coisa que aparece numa
            // conferencia meses depois sem ninguem saber de onde veio.
            l.Add("APLICACAO DE TESTE");
            l.Add("SEM VALOR FINANCEIRO");
        }

        l.Add("");
        return l;
    }

    /// <summary>
    /// O comprovante é composto pela automação, e não veio da rede. Vai escrito no
    /// papel: quem analisa a homologação precisa saber que este texto é nosso.
    /// </summary>
    public const string Rodape = "Emitido pela automacao do caixa";
}
