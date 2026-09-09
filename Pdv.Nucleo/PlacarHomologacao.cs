namespace Pdv.Nucleo;

/// <summary>O que o caixa registrou de um passo do roteiro.</summary>
/// <param name="Reqnum">PWINFO_REQNUM, que e o que a planilha oficial exige na coluna "Retorno do teste".</param>
public sealed record PassoFeito(int Numero, string Resultado, string? Reqnum, DateTime Quando);

/// <summary>Um passo com o que ja foi feito nele, do jeito que a tela lista.</summary>
public sealed record LinhaDoPlacar(PassoTef Passo, PassoFeito? Feito)
{
    public bool Ok => Feito is not null && Feito.Resultado == PlacarHomologacao.Aprovado;
    public bool Tentado => Feito is not null;
}

/// <summary>
/// O PLACAR DA HOMOLOGACAO DO TEF.
///
/// POR QUE ESTE ARQUIVO NASCEU (09/09/2026). O dono fez a primeira transacao
/// aprovada no sandbox e pediu: "nao eh melhor voce ja capturar e criar essa
/// planilha? alem disso cria o menu tef de homologacao com cada passo, cada valor".
///
/// O que existia antes: o roteiro num PDF, a planilha oficial num xlsx, os valores
/// exatos digitados a mao no caixa, e o REQNUM de cada transacao so no log da
/// biblioteca. Fechar a homologacao assim e copiar 36 numeros de um arquivo de
/// texto para uma planilha, sem errar linha.
///
/// O que passa a existir: o caixa registra o passo com o REQNUM na hora, e daqui
/// sai o texto que preenche a planilha.
///
/// ⚠️ ESTE ARQUIVO NAO DECIDE SE O PASSO PASSOU. Quem diz isso e a PayGo, olhando
/// o comprovante e o retorno. O caixa registra o que aconteceu; nao se auto-aprova.
/// </summary>
public static class PlacarHomologacao
{
    public const string Aprovado = "aprovado";
    public const string Recusado = "recusado";
    public const string Erro = "erro";

    /// <summary>Junta o roteiro com o que foi feito, na ordem do roteiro.</summary>
    public static IReadOnlyList<LinhaDoPlacar> Montar(
        IReadOnlyList<PassoTef> passos,
        IReadOnlyDictionary<int, PassoFeito> feitos)
    {
        var f = feitos ?? new Dictionary<int, PassoFeito>();
        return (passos ?? Array.Empty<PassoTef>())
            .OrderBy(p => p.Numero)
            .Select(p => new LinhaDoPlacar(p, f.TryGetValue(p.Numero, out var x) ? x : null))
            .ToList();
    }

    /// <summary>
    /// Quanto falta, contando so os OBRIGATORIOS.
    ///
    /// Passo opcional feito e bom, mas nao muda o que trava a homologacao, e somar
    /// os dois numa barra so faria o placar parecer melhor do que e.
    /// </summary>
    public static (int Feitos, int Total) Progresso(IReadOnlyList<LinhaDoPlacar> linhas)
    {
        var obr = (linhas ?? Array.Empty<LinhaDoPlacar>())
            .Where(l => l.Passo.Obrigatoriedade == "SIM").ToList();
        return (obr.Count(l => l.Ok), obr.Count);
    }

    /// <summary>O proximo passo obrigatorio ainda nao aprovado. `null` quando acabou.</summary>
    public static PassoTef? Proximo(IReadOnlyList<LinhaDoPlacar> linhas)
        => (linhas ?? Array.Empty<LinhaDoPlacar>())
            .FirstOrDefault(l => l.Passo.Obrigatoriedade == "SIM" && !l.Ok)?.Passo;

    /// <summary>
    /// A planilha, em CSV com ponto e virgula (o Excel em portugues abre assim sem
    /// perguntar nada).
    ///
    /// As colunas sao as da planilha oficial v20260819, na mesma ordem, para o
    /// preenchimento ser copiar e colar. O cabecalho repete o que ela exige em
    /// "Retorno do teste" nesta integracao, porque e o campo que ninguem lembra.
    /// </summary>
    public static string Csv(IReadOnlyList<LinhaDoPlacar> linhas)
    {
        static string C(string? s)
        {
            var v = (s ?? "").Replace("\"", "\"\"").Replace("\n", " ").Replace("\r", " ");
            return v.Contains(';') || v.Contains('"') ? "\"" + v + "\"" : v;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Teste;Obrigatoriedade;Retorno do teste (" + RoteiroTef.RetornoExigido + ");Observacoes;Controle interno");
        foreach (var l in linhas ?? Array.Empty<LinhaDoPlacar>())
        {
            var obs = l.Feito is null
                ? "nao executado"
                : l.Feito.Resultado + " em " + l.Feito.Quando.ToString("dd/MM/yyyy HH:mm");
            sb.AppendLine(string.Join(";",
                C("Passo " + l.Passo.Numero),
                C(l.Passo.Obrigatoriedade),
                C(l.Feito?.Reqnum ?? ""),
                C(obs),
                C(l.Passo.Titulo)));
        }
        return sb.ToString();
    }

    /// <summary>
    /// O aviso de quem ainda nao tem REQNUM gravado.
    ///
    /// Passo de venda aprovado SEM reqnum e uma linha que a PayGo vai devolver, e e
    /// melhor descobrir isso aqui do que na analise deles. `null` quando esta tudo
    /// preenchido.
    /// </summary>
    public static string? AvisoDeReqnumFaltando(IReadOnlyList<LinhaDoPlacar> linhas)
    {
        var sem = (linhas ?? Array.Empty<LinhaDoPlacar>())
            .Where(l => l.Ok && string.IsNullOrWhiteSpace(l.Feito!.Reqnum))
            .Select(l => l.Passo.Numero)
            .ToList();
        if (sem.Count == 0) return null;
        return sem.Count == 1
            ? $"O passo {sem[0]} foi aprovado mas ficou sem o {RoteiroTef.RetornoExigido}. Rode de novo antes de entregar."
            : $"{sem.Count} passos foram aprovados sem o {RoteiroTef.RetornoExigido} ({string.Join(", ", sem)}). Rode de novo antes de entregar.";
    }
}
