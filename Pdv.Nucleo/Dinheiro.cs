using System.Globalization;

namespace Pdv.Nucleo;

/// <summary>
/// Dinheiro em CENTAVOS (inteiro). Nunca double/decimal solto no caminho da venda.
///
/// O motivo é prático: 0,1 + 0,2 em ponto flutuante não dá 0,3. Numa venda isso passa
/// despercebido; no fechamento do dia, com centenas de linhas, vira "quebra de caixa"
/// que ninguém explica e o operador leva a culpa. Em centavos inteiros a conta fecha
/// exata, sempre.
/// </summary>
public readonly record struct Dinheiro(long Centavos) : IComparable<Dinheiro>
{
    public static readonly Dinheiro Zero = new(0);

    public static Dinheiro DeReais(decimal reais) => new((long)Math.Round(reais * 100m, MidpointRounding.AwayFromZero));
    public decimal Reais => Centavos / 100m;

    public static Dinheiro operator +(Dinheiro a, Dinheiro b) => new(a.Centavos + b.Centavos);
    public static Dinheiro operator -(Dinheiro a, Dinheiro b) => new(a.Centavos - b.Centavos);
    public static Dinheiro operator -(Dinheiro a) => new(-a.Centavos);
    public static bool operator >(Dinheiro a, Dinheiro b) => a.Centavos > b.Centavos;
    public static bool operator <(Dinheiro a, Dinheiro b) => a.Centavos < b.Centavos;
    public static bool operator >=(Dinheiro a, Dinheiro b) => a.Centavos >= b.Centavos;
    public static bool operator <=(Dinheiro a, Dinheiro b) => a.Centavos <= b.Centavos;

    public bool Positivo => Centavos > 0;
    public Dinheiro Abs => new(Math.Abs(Centavos));
    public int CompareTo(Dinheiro outro) => Centavos.CompareTo(outro.Centavos);

    /// <summary>
    /// Multiplica por quantidade em MILÉSIMOS (1000 = 1 unidade), arredondando o
    /// centavo pra cima no 0,5. Milésimos existem por causa de produto por peso:
    /// 0,375 kg de pão precisa de 3 casas, e guardar isso como fração de unidade
    /// em ponto flutuante traria o mesmo problema do dinheiro.
    /// </summary>
    public Dinheiro VezesQtd(long qtdMilesimos)
    {
        var bruto = (decimal)Centavos * qtdMilesimos / 1000m;
        return new Dinheiro((long)Math.Round(bruto, MidpointRounding.AwayFromZero));
    }

    public string Formatado() => Centavos.ToString("N0", CultureInfo.InvariantCulture) is var _
        ? Reais.ToString("C2", new CultureInfo("pt-BR"))
        : "";

    public override string ToString() => Formatado();
}

/// <summary>Quantidade em milésimos — mesma lógica do dinheiro, sem ponto flutuante.</summary>
public readonly record struct Quantidade(long Milesimos)
{
    public static readonly Quantidade Um = new(1000);
    public static Quantidade DeUnidades(int n) => new(n * 1000L);
    public static Quantidade DeDecimal(decimal q) => new((long)Math.Round(q * 1000m, MidpointRounding.AwayFromZero));
    public decimal Valor => Milesimos / 1000m;
    public bool Inteira => Milesimos % 1000 == 0;

    /// <summary>Mostra "2" pra inteiro e "0,375" pra peso — o caixa não quer ver "2,000".</summary>
    public string Formatada() => Inteira
        ? (Milesimos / 1000).ToString(CultureInfo.InvariantCulture)
        : Valor.ToString("0.###", new CultureInfo("pt-BR"));
}
