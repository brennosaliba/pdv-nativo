using System.Text;

namespace Pdv.Nucleo;

/// <summary>
/// O DESENHO DE UM PAPEL DE BOBINA, num lugar só.
///
/// A impressora da loja tem 32 colunas em 58 mm e 48 em 80 mm, e todo papel que o caixa
/// escreve à mão (o da retirada para o cofre, o da abertura, o do fechamento) precisa
/// centralizar título, alinhar rótulo à esquerda com valor à direita, e nunca estourar a
/// largura. Isso morava dentro de <see cref="RetiradaCofre.Papel"/> como função local, e a
/// segunda folha que precisasse disso faria uma cópia. Duas cópias da mesma regra de
/// largura divergem no primeiro dia: uma ganha um caso e a outra não.
///
/// Tudo aqui é puro: entra texto e largura, sai texto. Sem banco, sem WPF, sem impressora.
/// </summary>
public static class PapelTexto
{
    /// <summary>A largura de verdade. Abaixo de 24 colunas não existe bobina, e um número
    /// pequeno vindo de configuração errada quebraria todas as contas de espaço.</summary>
    public static int Largura(int colunas) => Math.Max(24, colunas);

    /// <summary>Corta no limite. É a última linha de defesa: nada sai mais largo que a bobina.</summary>
    public static string Corta(string s, int max) => s.Length <= max ? s : s[..Math.Max(0, max)];

    /// <summary>Texto centralizado, para título e nome da loja.</summary>
    public static string Centro(string s, int colunas)
    {
        var c = Largura(colunas);
        s = Corta(s, c);
        var sobra = (c - s.Length) / 2;
        return new string(' ', sobra) + s;
    }

    /// <summary>
    /// Rótulo à esquerda, valor à direita, na mesma linha.
    ///
    /// ⚠️ Quando os dois não cabem juntos, quem encolhe é o RÓTULO, em silêncio. É por isso
    /// que existe <see cref="Campo"/>: valor grande com rótulo longo vira
    /// "Fica no caixa (troco" no papel, e ninguém percebe até a bobina sair.
    /// </summary>
    public static string Par(string rotulo, string valor, int colunas)
    {
        var c = Largura(colunas);
        valor = Corta(valor, c);
        var espaco = c - valor.Length - 1;
        rotulo = Corta(rotulo, Math.Max(0, espaco));
        return rotulo + new string(' ', Math.Max(1, c - rotulo.Length - valor.Length)) + valor;
    }

    /// <summary>
    /// O par que NÃO mutila o rótulo: cabendo, sai numa linha; não cabendo, sai em duas,
    /// com o rótulo inteiro em cima e o valor indentado embaixo.
    ///
    /// Preferir isto a <see cref="Par"/> sempre que o valor puder crescer (um fechamento
    /// de R$ 99.999,99 num rótulo longo), porque papel assinado com rótulo pela metade
    /// não serve como comprovante.
    /// </summary>
    public static IReadOnlyList<string> Campo(string rotulo, string valor, int colunas)
    {
        var c = Largura(colunas);
        rotulo = (rotulo ?? "").TrimEnd();
        valor = valor ?? "";
        if (rotulo.Length + 1 + valor.Length <= c) return new[] { Par(rotulo, valor, c) };
        return new[] { Corta(rotulo + ":", c), Corta("  " + valor, c) };
    }

    /// <summary>A linha de separação, na largura exata.</summary>
    public static string Traco(int colunas) => new('-', Largura(colunas));

    /// <summary>A linha de assinar: só o traço, ocupando a bobina inteira.</summary>
    public static string Risco(int colunas) => new('_', Largura(colunas));

    /// <summary>
    /// A linha de quem confere depois: traço para o nome e um espaço para a data.
    ///
    /// Montada A PARTIR da largura, nunca cortada de um literal. O literal que existia
    /// tinha 35 caracteres e em 32 colunas perdia o fim do ano, virando "___/___/___":
    /// quem conferiu não tinha onde escrever 2026.
    /// </summary>
    public static string Assinatura(int colunas)
    {
        var c = Largura(colunas);
        const string cauda = "  em __/__/____";       // 15 caracteres
        return new string('_', Math.Max(8, c - cauda.Length)) + cauda;
    }

    /// <summary>
    /// O bloco de assinar inteiro, do jeito que os três papéis do caixa usam.
    /// </summary>
    public static IReadOnlyList<string> BlocoDeAssinatura(int colunas) => new[]
    {
        "",
        "Assinatura:",
        "",
        Risco(colunas),
        "",
        "Conferido por:",
        "",
        Assinatura(colunas),
    };

    /// <summary>
    /// Texto corrido quebrado por PALAVRA, para justificativa escrita pelo operador.
    /// Palavra maior que a linha é partida no limite, senão ela sumiria pela borda.
    /// </summary>
    public static IReadOnlyList<string> Quebrar(string? texto, int colunas)
    {
        var c = Largura(colunas);
        var saida = new List<string>();
        foreach (var bruta in (texto ?? "").Replace("\r", "").Split('\n'))
        {
            var linha = new StringBuilder();
            foreach (var palavra in bruta.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = palavra;
                while (p.Length > c)
                {
                    if (linha.Length > 0) { saida.Add(linha.ToString()); linha.Clear(); }
                    saida.Add(p[..c]);
                    p = p[c..];
                }
                if (linha.Length == 0) linha.Append(p);
                else if (linha.Length + 1 + p.Length <= c) linha.Append(' ').Append(p);
                else { saida.Add(linha.ToString()); linha.Clear(); linha.Append(p); }
            }
            if (linha.Length > 0) saida.Add(linha.ToString());
        }
        return saida;
    }

    /// <summary>
    /// A data do turno, que vem gravada como "2026-09-18", escrita do jeito que se lê.
    /// Turno NÃO é o dia de hoje: o dia operacional vira às 05:00, e um fechamento de
    /// madrugada pertence ao dia anterior.
    /// </summary>
    public static string DiaDoTurno(string? businessDate)
    {
        return DateTime.TryParseExact(businessDate ?? "", "yyyy-MM-dd",
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.None, out var d)
            ? d.ToString("dd/MM/yyyy")
            : (businessDate ?? "");
    }
}
