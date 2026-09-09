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

    /// <summary>
    /// A tabela onde o caixa anota o roteiro. Criada na hora: ela so existe na
    /// maquina que esta homologando, e nao vale uma migracao para as lojas.
    /// </summary>
    /// <summary>
    /// A integracao a que uma anotacao pertence.
    ///
    /// ⚠️ EXISTE POR UM SUSTO (09/09/2026). A tabela ja tinha 30 linhas da
    /// homologacao do CONTROLPAY, de 24 e 25 de agosto, com resultado "ok". Elas
    /// entraram na planilha da PGWebLib como se fossem desta rodada. Entregar isso
    /// seria afirmar para a PayGo que passos foram aprovados numa integracao em que
    /// nunca foram executados.
    ///
    /// Anotacao de homologacao pertence a UMA integracao. Linha sem provedor e de
    /// antes desta regra: aparece na conta de "outra rodada", nunca nesta.
    /// </summary>
    public const string Provedor = "pgweblib";

    private static void Garantir(Microsoft.Data.Sqlite.SqliteConnection cx)
    {
        Dapper.SqlMapper.Execute(cx,
            "CREATE TABLE IF NOT EXISTS homolog_passo (numero TEXT PRIMARY KEY, intencao TEXT, resultado TEXT, quando TEXT, reqnum TEXT)");
        // ALTER separados: banco de quem ja rodava o roteiro antes destas colunas
        // tem a tabela sem elas, e CREATE TABLE IF NOT EXISTS nao as adiciona.
        foreach (var col in new[] { "reqnum TEXT", "provedor TEXT" })
            try { Dapper.SqlMapper.Execute(cx, "ALTER TABLE homolog_passo ADD COLUMN " + col); } catch { }
    }

    /// <summary>Anota o desfecho de um passo, com o REQNUM que a planilha exige.</summary>
    public static void Anotar(int numero, string resultado, string? reqnum)
    {
        try
        {
            using var cx = Banco.Abrir();
            Garantir(cx);
            Dapper.SqlMapper.Execute(cx, """
                INSERT INTO homolog_passo (numero, resultado, quando, reqnum, provedor) VALUES (@N,@R,@Q,@X,@P)
                ON CONFLICT(numero) DO UPDATE SET resultado=excluded.resultado,
                    quando=excluded.quando, reqnum=COALESCE(excluded.reqnum, reqnum),
                    provedor=excluded.provedor
                """,
                new { N = numero.ToString(), R = resultado, Q = DateTime.Now.ToString("o"), X = reqnum, P = Provedor });
        }
        catch { /* o roteiro nao pode cair por causa do registro */ }
    }

    /// <summary>O que ja foi anotado, por numero de passo.</summary>
    public static IReadOnlyDictionary<int, PassoFeito> Anotados()
    {
        var d = new Dictionary<int, PassoFeito>();
        try
        {
            using var cx = Banco.Abrir();
            Garantir(cx);
            // SO desta integracao: linha de outra rodada nao entra no placar nem na planilha.
            foreach (var r in Dapper.SqlMapper.Query(cx,
                "SELECT numero, resultado, quando, reqnum FROM homolog_passo WHERE provedor = @P",
                new { P = Provedor }))
            {
                if (!int.TryParse((string)r.numero, out var n)) continue;
                DateTime.TryParse((string?)r.quando, out var q);
                d[n] = new PassoFeito(n, (string?)r.resultado ?? "", r.reqnum as string, q);
            }
        }
        catch { }
        return d;
    }

    /// <summary>Quantas anotacoes existem de OUTRA rodada, que esta ficando de fora.</summary>
    public static int DeOutraRodada()
    {
        try
        {
            using var cx = Banco.Abrir();
            Garantir(cx);
            return Dapper.SqlMapper.ExecuteScalar<int>(cx,
                "SELECT COUNT(*) FROM homolog_passo WHERE provedor IS NULL OR provedor <> @P",
                new { P = Provedor });
        }
        catch { return 0; }
    }

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
