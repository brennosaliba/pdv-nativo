using System.Globalization;

namespace Pdv.Nucleo;

/// <summary>
/// RETIRADA DE FECHAMENTO PARA O COFRE (12/09/2026, regra do dono):
/// "após o fechamento do dinheiro, anotar quanto está sendo retirado e quanto está
/// ficando no caixa. Essa retirada imprime um papel com nome do operador, valor e data,
/// que junto do dinheiro retirado vai para o cofre para conferência depois."
///
/// O que mora aqui é puro: a conta (contado menos o que fica), a pergunta que o caixa
/// faz e o texto do papel. O registro no banco é feito por Caixa.Fechar (a retirada é
/// uma sangria com destino "cofre", na mesma transação do fechamento), e a abertura do
/// dia seguinte passa a esperar na gaveta o que FICOU, não o que foi contado.
/// </summary>
public static class RetiradaCofre
{
    public const string Destino = "cofre";
    public const string Motivo = "Retirada de fechamento";

    /// <summary>Quanto sai da gaveta: o contado menos o que fica. Erro em texto quando a conta não fecha.</summary>
    public static (Dinheiro Retirada, string? Erro) Calcular(Dinheiro contado, Dinheiro fica)
    {
        if (fica.Centavos < 0) return (Dinheiro.Zero, "O valor que fica no caixa não pode ser negativo.");
        if (fica > contado)
            return (Dinheiro.Zero, $"Não dá para deixar {fica.Formatado()} no caixa: você contou {contado.Formatado()}.");
        return (contado - fica, null);
    }

    /// <summary>A pergunta do fechamento, depois da contagem do dinheiro.</summary>
    public static string Pergunta(Dinheiro contado)
        => $"Você contou {contado.Formatado()} em dinheiro. Quanto fica na gaveta para o troco de amanhã? O resto vai para o cofre.";

    /// <summary>
    /// O papel que vai para o cofre junto com o dinheiro. Uma via, na bobina do cupom.
    /// Cabe em 32 colunas (58 mm) e em 48 (80 mm): cada linha é cortada na largura.
    /// </summary>
    public static IReadOnlyList<string> Papel(string loja, DateTime quando, string operador, string diaDoTurno,
        Dinheiro contado, Dinheiro fica, Dinheiro retirada, int colunas)
    {
        var c = PapelTexto.Largura(colunas);
        var linhas = new List<string>
        {
            PapelTexto.Centro("RETIRADA PARA O COFRE", c),
            PapelTexto.Centro(loja, c),
            PapelTexto.Traco(c),
        };
        linhas.AddRange(PapelTexto.Campo("Data", quando.ToString("dd/MM/yyyy HH:mm"), c));
        linhas.AddRange(PapelTexto.Campo("Turno", PapelTexto.DiaDoTurno(diaDoTurno), c));
        linhas.AddRange(PapelTexto.Campo("Operador", operador, c));
        linhas.Add(PapelTexto.Traco(c));
        linhas.AddRange(PapelTexto.Campo("Contado na gaveta", contado.Formatado(), c));
        linhas.AddRange(PapelTexto.Campo("Fica no caixa (troco)", fica.Formatado(), c));
        linhas.AddRange(PapelTexto.Campo("RETIRADO", retirada.Formatado(), c));
        linhas.Add(PapelTexto.Traco(c));
        linhas.AddRange(PapelTexto.BlocoDeAssinatura(c));
        linhas.Add("");
        linhas.Add(PapelTexto.Centro("Guarde este papel com o dinheiro.", c));
        return linhas;
    }
}
