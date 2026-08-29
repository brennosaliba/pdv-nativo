using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A ordem da coluna de categorias e da grade de produtos na tela de venda.
///
/// O caso que motivou: em "Bebidas", ÁGUA MINERAL COM GÁS era o ÚLTIMO produto da grade,
/// depois de SUCO UVA. A ordem vinha do `ORDER BY nome` do SQLite, que compara texto por BYTE
/// — "Á" em UTF-8 é 0xC3 0x81, maior que qualquer letra ASCII, então toda palavra acentuada
/// desce para o fim. O operador não procura água no rodapé da lista: ele conclui que o
/// produto sumiu do cardápio e chama o gerente no meio da fila.
///
/// SE ESTES TESTES QUEBRAREM, é isso que volta a acontecer na loja: acento e caixa passam a
/// mandar na ordem em vez do alfabeto, ou a vitrine de PROMOÇÃO sai do topo e o desconto que
/// o caixa acabou de anunciar para o cliente vira uma caça no meio da coluna.
///
/// Os testes olham a LISTA QUE APARECE NA TELA. Se a ordenação passar a ser feita em SQL, com
/// outro comparador ou em outro lugar, eles continuam valendo — o que não pode mudar é o que
/// o operador lê de cima para baixo.
/// </summary>
public static class TestesCategorias
{
    public static void Rodar(Action<bool, string> checar)
    {
        static string Em(IEnumerable<string> xs) => string.Join(" | ", xs);

        // ── alfabeto simples ────────────────────────────────────────────────
        var simples = Categorias.Ordenar(new[] { "Salgados", "Bebidas", "Combos", "Donuts" });
        checar(Em(simples) == "Bebidas | Combos | Donuts | Salgados",
            $"alfabeto simples ({Em(simples)})");

        // ── ACENTO é a mesma letra, não o fim da lista ──────────────────────
        // Este é o defeito original em forma de teste: por byte, "Açaí" e "Águas" viriam
        // DEPOIS de "Zebra", porque 0xC3 > 'Z'. Em português elas pertencem ao A.
        var acento = Categorias.Ordenar(new[] { "Zebra", "Bebidas", "Açaí", "Águas", "Étnicos" });
        checar(Em(acento) == "Açaí | Águas | Bebidas | Étnicos | Zebra",
            $"acento entra no alfabeto, não no fim ({Em(acento)})");
        checar(acento.IndexOf("Açaí") < acento.IndexOf("Bebidas"),
            "Açaí vem antes de Bebidas");
        checar(acento[^1] == "Zebra",
            "quem fecha a lista é Zebra — nenhuma acentuada foi jogada para o fim");
        checar(acento.IndexOf("Étnicos") < acento.IndexOf("Zebra"),
            "Étnicos fica no E, antes de Zebra (o É não vira uma letra depois do Z)");

        // Prova de que o comparador NÃO é ordinal: se alguém trocar por
        // StringComparer.OrdinalIgnoreCase, a linha acima passa a devolver Águas e Étnicos
        // depois de Zebra. Cravado aqui para o erro aparecer como teste vermelho, não como
        // reclamação no balcão.
        var comoSeriaOrdinal = new[] { "Zebra", "Bebidas", "Açaí", "Águas", "Étnicos" }
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        checar(Em(acento) != Em(comoSeriaOrdinal),
            "a ordem entregue é diferente da ordinal (é ela que erra o acento)");

        // ── CAIXA não separa a lista em duas ────────────────────────────────
        // O cardápio chega da nuvem com grafia mista de verdade ("ENCOMENDAS" ao lado de
        // "Donuts sem Recheio"): a coluna precisa ser lida como uma lista só.
        var caixa = Categorias.Ordenar(new[] { "donuts", "ENCOMENDAS", "Bebidas", "cookies", "Açaí" });
        checar(Em(caixa) == "Açaí | Bebidas | cookies | donuts | ENCOMENDAS",
            $"maiúscula e minúscula na mesma lista alfabética ({Em(caixa)})");
        checar(caixa.IndexOf("ENCOMENDAS") == caixa.Count - 1,
            "ENCOMENDAS fica no E (por byte, MAIÚSCULA subiria para o topo)");

        // Caixa diferente = categorias DIFERENTES no banco, e a grade filtra produto por
        // igualdade exata. Colapsar as duas aqui faria os produtos de uma delas sumirem da
        // tela sem erro nenhum — some o botão, some a venda.
        var duasCaixas = Categorias.Ordenar(new[] { "bebidas", "Bebidas" });
        checar(duasCaixas.Count == 2, "\"bebidas\" e \"Bebidas\" continuam sendo duas categorias");
        checar(Em(duasCaixas) == "Bebidas | bebidas",
            $"e o desempate entre elas é fixo, não a ordem de leitura do banco ({Em(duasCaixas)})");

        // ── a SENTINELA: PROMOÇÃO em primeiro, fora do alfabeto ─────────────
        var comPromo = Categorias.Ordenar(
            new[] { "Salgados", "Bebidas", Categorias.Promocao, "Açaí" }, Categorias.Promocao);
        checar(comPromo[0] == Categorias.Promocao,
            $"PROMOÇÃO abre a coluna (veio {comPromo[0]})");
        checar(Em(comPromo.Skip(1)) == "Açaí | Bebidas | Salgados",
            $"e o resto da coluna continua alfabético ({Em(comPromo.Skip(1))})");
        checar(comPromo.IndexOf("Açaí") == 1,
            "a sentinela não empurra o alfabeto para fora: Açaí é a primeira categoria de verdade");

        // Sem promoção vigente a tela NÃO acrescenta a vitrine — categoria vazia é pior que
        // categoria faltando, e o teste garante que ninguém a inventa aqui.
        var semPromo = Categorias.Ordenar(new[] { "Salgados", "Bebidas" }, Categorias.Promocao);
        checar(!semPromo.Contains(Categorias.Promocao),
            "sem promoção vigente a coluna não ganha uma vitrine vazia");
        checar(Em(semPromo) == "Bebidas | Salgados", $"e segue alfabética ({Em(semPromo)})");

        // Sem sentinela declarada, "promoção" é só mais uma palavra com P.
        var promoSemSentinela = Categorias.Ordenar(new[] { "Salgados", "Bebidas", Categorias.Promocao });
        checar(promoSemSentinela[0] == "Bebidas",
            "sem sentinela declarada, promoção entra no alfabeto pelo P");

        // A sentinela é reconhecida como a coluna a mostra: "Promoção" com maiúscula vinda do
        // cardápio da nuvem é a MESMA vitrine, não uma segunda categoria no meio da lista.
        var promoDaNuvem = Categorias.Ordenar(
            new[] { "Salgados", "Promoção", "Bebidas" }, Categorias.Promocao);
        checar(promoDaNuvem[0] == "Promoção",
            $"'Promoção' com maiúscula também abre a coluna (veio {promoDaNuvem[0]})");

        // ── o cardápio REAL da loja, do jeito que está no banco hoje ────────
        var reais = new[]
        {
            "Donuts sem Recheio", "ENCOMENDAS", "Bebidas", "Cookies Super Premium", "Combos",
            "Bebidas Quentes", "Outros", "Cookies Clássicos", "Donuts Premium", "Salgados",
            "Donuts Super Premium e Especialidades", "Cookies Premium", "Donuts Clássicos",
        };
        var loja = Categorias.Ordenar(reais, Categorias.Promocao);
        checar(Em(loja) ==
            "Bebidas | Bebidas Quentes | Combos | Cookies Clássicos | Cookies Premium | " +
            "Cookies Super Premium | Donuts Clássicos | Donuts Premium | Donuts sem Recheio | " +
            "Donuts Super Premium e Especialidades | ENCOMENDAS | Outros | Salgados",
            $"o cardápio da loja sai na ordem que o dono espera ({Em(loja)})");
        checar(loja.IndexOf("Donuts sem Recheio") < loja.IndexOf("Donuts Super Premium e Especialidades"),
            "\"sem Recheio\" vem antes de \"Super Premium\" — o s minúsculo não vai para o fim");

        // Embaralhar a entrada não pode mudar a saída: a coluna é a mesma toda vez que o
        // catálogo é recarregado, senão o operador perde a memória de onde fica cada botão.
        var embaralhado = Categorias.Ordenar(reais.Reverse().ToArray(), Categorias.Promocao);
        checar(Em(embaralhado) == Em(loja), "a ordem não depende de como o banco devolveu");

        // ── ruído não derruba o caixa ───────────────────────────────────────
        var sujo = Categorias.Ordenar(new[] { "Bebidas", null, "", "   ", "Açaí", "Bebidas" });
        checar(Em(sujo) == "Açaí | Bebidas", $"nulo, vazio e repetido saem da coluna ({Em(sujo)})");
        checar(Categorias.Ordenar(Array.Empty<string>()).Count == 0,
            "catálogo vazio devolve lista vazia, não exceção");

        // ── a GRADE de produtos usa a mesma régua ───────────────────────────
        // Bebidas como está na loja: por byte, ÁGUA MINERAL COM GÁS era o último item.
        var bebidas = new[]
        {
            "SUCO UVA DEL VALE LATA 290ML", "ÁGUA MINERAL COM GÁS", "COCA COLA LATA 350ML",
            "AGUA MINERAL 500ML", "SPRITE LATA 350ML",
        };
        var grade = Categorias.OrdenarPorNome(bebidas, n => n);
        checar(grade[^1] == "SUCO UVA DEL VALE LATA 290ML",
            $"ÁGUA não fecha mais a grade de Bebidas (último: {grade[^1]})");
        checar(grade[0] == "AGUA MINERAL 500ML" && grade[1] == "ÁGUA MINERAL COM GÁS",
            $"as duas águas ficam juntas no topo ({grade[0]} / {grade[1]})");
        checar(grade.IndexOf("ÁGUA MINERAL COM GÁS") < grade.IndexOf("COCA COLA LATA 350ML"),
            "ÁGUA vem antes de COCA — acento não muda a letra");

        // O seletor de nome é o que ordena, não a posição na coleção: a grade recebe
        // registros de produto, não strings soltas.
        var itens = new[] { ("z1", "Zebra"), ("a1", "Açaí"), ("b1", "Bebida") };
        var ordenados = Categorias.OrdenarPorNome(itens, p => p.Item2);
        checar(string.Join(",", ordenados.Select(p => p.Item1)) == "a1,b1,z1",
            "OrdenarPorNome ordena pelo nome, e devolve o registro inteiro");
    }
}
