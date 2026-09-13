namespace Pdv.Nucleo;

/// <summary>
/// COMBO POR TOTAL: a regra UNICA (pedido do dono 13/09/2026). Espelho de
/// src/lib/comboLimites.ts (ERP) e de combo_limite_validar (banco), com a MESMA
/// tabela de casos (Pdv.Testes/VetoresComboLimites.cs e copia de
/// src/lib/comboLimites.vetores.json).
///
/// O combo tem um TOTAL (a caixa de 4 leva 4) e cada grupo so um TETO. O
/// obrigatorio sai da conta: teto igual ao total nao limita; tetos que somam o
/// total obrigam cada grupo ("2 Premium e 2 Homer" e 2 e 2, nunca 1 e 3). Um
/// produto conta num grupo so: lista de produtos, depois categoria, depois
/// cardapio todo. Combo sem total e a regra antiga (minimo e maximo por grupo).
/// Tudo puro: sem WPF, sem banco.
/// </summary>
public static class ComboLimites
{
    /// <summary>Grupo da regra. Tipo: "categoria" (Id), "produto" (Id ou Ids) ou "todos".</summary>
    public sealed record Grupo(string Tipo, string? Id, IReadOnlyList<string>? Ids, string Nome, int Maximo, int Minimo = 0);

    /// <summary>Uma escolha de UMA unidade do combo.</summary>
    public sealed record EscolhaRegra(string Produto, IReadOnlyList<string>? Categorias, int Qtd, string? Nome = null);

    /// <summary>A palavra das frases: donut/donuts.</summary>
    public sealed record Unidade(string Singular, string Plural);

    /// <summary>Ok, ou o codigo e a frase curta do primeiro problema.</summary>
    public sealed record Resultado(bool Ok, string? Codigo, string? Erro, int? Grupo, int[] Somas, int Soma);

    public static readonly Unidade Padrao = new("item", "itens");

    private static int MinimoProprio(Grupo g) => Math.Max(0, g.Minimo);

    /// <summary>Faixa efetiva de cada grupo contando os outros e o total (a conta do obrigatorio).</summary>
    public static (int Min, int Max)[] FaixaEfetiva(IReadOnlyList<Grupo> grupos, int? tMin, int? tMax)
    {
        var tetos = grupos.Select(g => tMax is int m ? Math.Min(g.Maximo, m) : g.Maximo).ToArray();
        var somaTetos = tetos.Sum();
        var mins = grupos.Select(MinimoProprio).ToArray();
        var somaMins = mins.Sum();
        var r = new (int, int)[grupos.Count];
        for (var i = 0; i < grupos.Count; i++)
        {
            var min = mins[i];
            if (tMin is int tmin) min = Math.Max(min, tmin - (somaTetos - tetos[i]));
            var max = grupos[i].Maximo;
            if (tMax is int tmax) max = Math.Min(max, tmax - (somaMins - mins[i]));
            min = Math.Max(0, min);
            r[i] = (min, Math.Max(min, max));
        }
        return r;
    }

    /// <summary>Em que grupo a escolha conta (lista, categoria, todos). -1 = fora do combo.</summary>
    public static int GrupoDaEscolha(IReadOnlyList<Grupo> grupos, EscolhaRegra e)
    {
        for (var i = 0; i < grupos.Count; i++)
            if (grupos[i].Tipo == "produto" && (grupos[i].Id == e.Produto || (grupos[i].Ids?.Contains(e.Produto) ?? false)))
                return i;
        var cats = e.Categorias ?? Array.Empty<string>();
        for (var i = 0; i < grupos.Count; i++)
            if (grupos[i].Tipo == "categoria" && grupos[i].Id is { } id && cats.Contains(id))
                return i;
        for (var i = 0; i < grupos.Count; i++)
            if (grupos[i].Tipo == "todos") return i;
        return -1;
    }

    private static string QtdTexto(int n, Unidade u) => $"{n} {(n == 1 ? u.Singular : u.Plural)}";

    /// <summary>
    /// O que a montagem diz, com as somas ja separadas por grupo. Ordem: teto do grupo,
    /// total, grupo obrigatorio faltando, total faltando.
    /// </summary>
    public static Resultado Situacao(int? tMin, int? tMax, IReadOnlyList<Grupo> grupos, IReadOnlyList<int> somas, Unidade? unidade = null)
    {
        var u = unidade ?? Padrao;
        var s = grupos.Select((_, i) => i < somas.Count ? Math.Max(0, somas[i]) : 0).ToArray();
        var soma = s.Sum();
        Resultado Falha(string codigo, string erro, int? grupo) => new(false, codigo, erro, grupo, s, soma);

        for (var i = 0; i < grupos.Count; i++)
        {
            var g = grupos[i];
            var limita = tMax is null || g.Maximo < tMax;
            if (limita && s[i] > g.Maximo) return Falha("grupo_acima", $"No máximo {g.Maximo} {g.Nome}", i);
        }
        if (tMax is int max && soma > max) return Falha("total_acima", $"No máximo {QtdTexto(max, u)}", null);
        var faixas = FaixaEfetiva(grupos, tMin, tMax);
        for (var i = 0; i < grupos.Count; i++)
        {
            var min = faixas[i].Min;
            if (s[i] < min)
                return Falha("grupo_abaixo",
                    s[i] == 0 ? $"Escolha {min} {grupos[i].Nome}" : $"Escolha mais {min - s[i]} {grupos[i].Nome}", i);
        }
        if (tMin is int tmin && soma < tmin)
        {
            var falta = tmin - soma;
            return Falha("total_abaixo", $"{(falta == 1 ? "Falta" : "Faltam")} {QtdTexto(falta, u)}", null);
        }
        return new Resultado(true, null, null, null, s, soma);
    }

    /// <summary>A regra inteira de UMA caixa: separa por grupo (o mais especifico ganha) e confere.</summary>
    public static Resultado ValidarEscolha(int? tMin, int? tMax, IReadOnlyList<Grupo> grupos,
        IEnumerable<EscolhaRegra> escolhas, Unidade? unidade = null)
    {
        var somas = new int[grupos.Count];
        EscolhaRegra? fora = null;
        foreach (var e in escolhas)
        {
            if (e.Qtd <= 0) continue;
            var g = GrupoDaEscolha(grupos, e);
            if (g < 0) { fora ??= e; continue; }
            somas[g] += e.Qtd;
        }
        if (fora is not null)
            return new Resultado(false, "fora_do_combo", $"{fora.Nome ?? "Um item"} não faz parte do combo", null, somas, somas.Sum());
        return Situacao(tMin, tMax, grupos, somas, unidade);
    }

    /// <summary>
    /// Ainda cabe mais um neste grupo? Menor entre a vaga do grupo e a do total, sem encher
    /// um grupo a ponto de faltar lugar para o minimo proprio de outro.
    /// </summary>
    public static bool PodeMais(int? tMin, int? tMax, IReadOnlyList<Grupo> grupos, IReadOnlyList<int> somas, int idx)
    {
        var faixas = FaixaEfetiva(grupos, tMin, tMax);
        var atual = idx < somas.Count ? somas[idx] : 0;
        if (atual >= faixas[idx].Max) return false;
        if (tMax is not int max) return true;
        var precisa = 0;
        for (var i = 0; i < grupos.Count; i++)
        {
            var depois = (i < somas.Count ? somas[i] : 0) + (i == idx ? 1 : 0);
            precisa += Math.Max(depois, MinimoProprio(grupos[i]));
        }
        return precisa <= max;
    }

    /// <summary>"COMBO 4 DONUTS" fala em donut/donuts; sem numero seguido de palavra, o padrao.</summary>
    public static Unidade UnidadeDoNome(string? nome, Unidade? padrao = null)
    {
        var m = System.Text.RegularExpressions.Regex.Match(nome ?? "", @"(?:^|\s)\d{1,2}\s+([A-Za-zÀ-ÿ]{3,})");
        if (!m.Success) return padrao ?? Padrao;
        var p = m.Groups[1].Value.ToLowerInvariant();
        return p.EndsWith('s') ? new Unidade(p[..^1], p) : new Unidade(p, p + "s");
    }
}
