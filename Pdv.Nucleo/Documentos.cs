namespace Pdv.Nucleo;

/// <summary>
/// CPF e CNPJ do consumidor na nota.
///
/// A validação acontece no caixa, antes de enviar, porque o emissor rejeita documento
/// inválido com erro genérico — e aí o operador fica olhando "erro 400" sem saber que
/// digitou um dígito errado, com o cliente esperando. Documento parcial nunca pode
/// vazar para a nota: ou está válido, ou não vai.
/// </summary>
public static class Documentos
{
    public static string SoDigitos(string? s) =>
        new(( s ?? "").Where(char.IsDigit).ToArray());

    /// <summary>Mantém dígitos e letras (o CNPJ alfanumérico usa letras nos 12 primeiros).</summary>
    private static string SoAlfanumerico(string? s) =>
        new((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static bool CpfValido(string? entrada)
    {
        var d = SoDigitos(entrada);
        if (d.Length != 11) return false;
        // 111.111.111-11 e afins passam na conta dos dígitos verificadores, mas não existem
        if (d.All(c => c == d[0])) return false;

        for (var casa = 9; casa <= 10; casa++)
        {
            var soma = 0;
            for (var i = 0; i < casa; i++) soma += (d[i] - '0') * (casa + 1 - i);
            var dv = soma * 10 % 11 % 10;
            if (dv != d[casa] - '0') return false;
        }
        return true;
    }

    public static bool CnpjValido(string? entrada)
    {
        var c = SoAlfanumerico(entrada);
        if (c.Length != 14) return false;
        if (c.All(x => x == c[0])) return false;
        // os dois dígitos verificadores continuam sendo numéricos mesmo no CNPJ alfanumérico
        if (!char.IsDigit(c[12]) || !char.IsDigit(c[13])) return false;

        // Peso do caractere = valor ASCII − 48. Para '0'..'9' isso dá o próprio dígito,
        // então a mesma conta serve para o CNPJ numérico de sempre e para o alfanumérico
        // que a Receita passou a emitir — não são dois algoritmos, é um só.
        static int Valor(char x) => x - 48;

        var pesos = new[] { 6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 };
        for (var casa = 12; casa <= 13; casa++)
        {
            var soma = 0;
            var deslocamento = 13 - casa;       // o 1º DV usa 12 caracteres, o 2º usa 13
            for (var i = 0; i < casa; i++) soma += Valor(c[i]) * pesos[deslocamento + i];
            var resto = soma % 11;
            var dv = resto < 2 ? 0 : 11 - resto;
            if (dv != c[casa] - '0') return false;
        }
        return true;
    }

    /// <summary>Aceita CPF ou CNPJ. É o que a tela pergunta: "CPF/CNPJ na nota?"</summary>
    public static bool Valido(string? entrada)
    {
        var c = SoAlfanumerico(entrada);
        return c.Length switch { 11 => CpfValido(c), 14 => CnpjValido(c), _ => false };
    }

    /// <summary>
    /// O que vai para a nota: sem pontuação. CPF só dígitos; CNPJ preserva letras,
    /// porque no alfanumérico elas fazem parte do número.
    /// </summary>
    public static string? ParaNota(string? entrada)
    {
        var c = SoAlfanumerico(entrada);
        return Valido(c) ? c : null;
    }

    /// <summary>Formatado para o cupom e para a tela. Documento inválido volta como veio.</summary>
    public static string Formatar(string? entrada)
    {
        var c = SoAlfanumerico(entrada);
        return c.Length switch
        {
            11 => $"{c[..3]}.{c[3..6]}.{c[6..9]}-{c[9..]}",
            14 => $"{c[..2]}.{c[2..5]}.{c[5..8]}/{c[8..12]}-{c[12..]}",
            _ => entrada ?? "",
        };
    }
}
