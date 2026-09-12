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
        var c = Math.Max(24, colunas);
        string Centro(string s) { s = Corta(s, c); var sobra = (c - s.Length) / 2; return new string(' ', sobra) + s; }
        string Par(string rotulo, string valor)
        {
            valor = Corta(valor, c);
            var espaco = c - valor.Length - 1;
            rotulo = Corta(rotulo, Math.Max(0, espaco));
            return rotulo + new string(' ', Math.Max(1, c - rotulo.Length - valor.Length)) + valor;
        }
        var traco = new string('-', c);
        var dia = DateTime.TryParseExact(diaDoTurno, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("dd/MM/yyyy") : diaDoTurno;
        return new[]
        {
            Centro("RETIRADA PARA O COFRE"),
            Centro(loja),
            traco,
            Par("Data", quando.ToString("dd/MM/yyyy HH:mm")),
            Par("Turno", dia),
            Par("Operador", operador),
            traco,
            Par("Contado na gaveta", contado.Formatado()),
            Par("Fica no caixa (troco)", fica.Formatado()),
            Par("RETIRADO", retirada.Formatado()),
            traco,
            "",
            "Assinatura:",
            "",
            Corta("________________________________", c),
            "",
            "Conferido por:",
            "",
            Corta("________________  em ___/___/______", c),
            "",
            Centro("Guarde este papel com o dinheiro."),
        };
    }

    private static string Corta(string s, int max) => s.Length <= max ? s : s[..Math.Max(0, max)];
}
