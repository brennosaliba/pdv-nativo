using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// PREÇO NOVO EM COMANDA JÁ ABERTA (09/09/2026).
///
/// O dono trocou PRODUTO TESTE de R$ 0,25 para R$ 0,10 no painel, sincronizou, e
/// contou o que viu: "ele grava, atualiza o preço do produto no menu, mas caso o
/// produto esteja na comanda com preço antigo ele permanece com preço antigo".
///
/// A causa está em Pdv.Nucleo/Reprecificacao.cs: a linha da comanda aponta para o
/// OBJETO do produto, e o Sincronizar troca os objetos do catálogo sem tocar nos
/// que a comanda segura.
///
/// SE ESTES TESTES QUEBRAREM, é isso que volta: o caixa cobra um preço que não é
/// mais o da tabela, e ninguém na loja fica sabendo.
/// </summary>
public static class TestesReprecificacao
{
    public static void Rodar(Action<bool, string> checar)
    {
        static Reprecificacao.Linha L(string id, string nome, long cent)
            => new(id, nome, cent);

        // ── O CASO DO DONO, em números ──────────────────────────────────────
        var comanda = new[] { L("p1", "PRODUTO TESTE", 25) };
        var catalogo = new Dictionary<string, long> { ["p1"] = 10 };

        var trocas = Reprecificacao.Trocas(comanda, catalogo);
        checar(trocas.Count == 1, $"o item da comanda é reprecificado ({trocas.Count} trocas)");
        checar(trocas[0].DeCent == 25 && trocas[0].ParaCent == 10,
            $"de 25 para 10 centavos ({trocas[0].DeCent} para {trocas[0].ParaCent})");
        checar(!trocas[0].Subiu, "0,10 é menor que 0,25, então não subiu");

        // ── Preço igual não é troca ─────────────────────────────────────────
        // Sem isto, TODA sincronização abriria um aviso, e aviso que aparece
        // sempre deixa de ser lido.
        var igual = Reprecificacao.Trocas(
            new[] { L("p1", "COOKIE", 1200) },
            new Dictionary<string, long> { ["p1"] = 1200 });
        checar(igual.Count == 0, "preço que não mudou não vira aviso");
        checar(Reprecificacao.Aviso(igual) is null, "sem troca, sem caixa de aviso");

        // ── PRODUTO QUE SUMIU DO CATÁLOGO ───────────────────────────────────
        // Desativado no painel no meio do atendimento. A linha JÁ FOI PEDIDA pelo
        // cliente: apagá-la ou zerá-la é trocar um problema pequeno por um grande.
        var sumiu = Reprecificacao.Trocas(
            new[] { L("p9", "DONUT FORA DE LINHA", 800) },
            new Dictionary<string, long> { ["p1"] = 1200 });
        checar(sumiu.Count == 0, "produto desativado no painel mantém o preço da comanda");

        // ── A MESMA LINHA REPETIDA FALA UMA VEZ SÓ ──────────────────────────
        // Item com 8 unidades não pode encher a tela com a mesma frase 8 vezes.
        var repetido = Reprecificacao.Trocas(
            new[] { L("p1", "COOKIE", 1200), L("p1", "COOKIE", 1200), L("p1", "COOKIE", 1200) },
            new Dictionary<string, long> { ["p1"] = 1000 });
        checar(repetido.Count == 1, $"produto repetido aparece uma vez ({repetido.Count})");

        // ── O AVISO MOSTRA OS DOIS VALORES ──────────────────────────────────
        // "Preço atualizado" sozinho não deixa o operador conferir nada, e é
        // conferir que evita a discussão no balcão.
        var aviso = Reprecificacao.Aviso(trocas)!;
        checar(aviso.Contains("PRODUTO TESTE"), "o aviso diz qual produto");
        checar(aviso.Contains("0,25") && aviso.Contains("0,10"),
            $"o aviso mostra de quanto para quanto ({aviso})");

        // ── PREÇO QUE SOBE TEM AVISO MAIS FORTE ─────────────────────────────
        // É a única situação em que ficar calado gera briga no caixa: o operador
        // já falou o total em voz alta para o cliente.
        var subiu = Reprecificacao.Trocas(
            new[] { L("p1", "COOKIE", 1000) },
            new Dictionary<string, long> { ["p1"] = 1500 });
        checar(subiu[0].Subiu, "1500 é mais que 1000, então subiu");
        var avisoSubiu = Reprecificacao.Aviso(subiu)!;
        checar(avisoSubiu.Contains("Confira com o cliente"),
            $"preço que sobe manda conferir com o cliente ({avisoSubiu})");
        checar(!aviso.Contains("Confira com o cliente"),
            "preço que só cai não precisa de conferência no balcão");

        // ── PLURAL CERTO ────────────────────────────────────────────────────
        var doisItens = Reprecificacao.Trocas(
            new[] { L("p1", "COOKIE", 1000), L("p2", "DONUT", 500) },
            new Dictionary<string, long> { ["p1"] = 1100, ["p2"] = 400 });
        checar(doisItens.Count == 2, "dois produtos, duas trocas");
        checar(Reprecificacao.Aviso(doisItens)!.Contains("2 itens"), "plural no cabeçalho");
        checar(aviso.StartsWith("Um item"), "singular no cabeçalho");

        // ── NADA DE TRAVESSÃO ───────────────────────────────────────────────
        // O dono lê travessão como texto de robô.
        checar(!avisoSubiu.Contains('—') && !aviso.Contains('—'),
            "nenhum aviso usa travessão");

        // ── ENTRADA ESTRAGADA NÃO DERRUBA O CAIXA ───────────────────────────
        // Rascunho antigo pode trazer linha sem id. Nada aqui vale uma exceção no
        // meio do atendimento.
        var sujo = Reprecificacao.Trocas(
            new[] { L("", "SEM ID", 100), L("p1", "COOKIE", 1000) },
            new Dictionary<string, long> { ["p1"] = 900 });
        checar(sujo.Count == 1 && sujo[0].ProdutoId == "p1",
            "linha sem id é ignorada sem quebrar o resto");

        var vazio = Reprecificacao.Trocas(
            Array.Empty<Reprecificacao.Linha>(),
            new Dictionary<string, long> { ["p1"] = 900 });
        checar(vazio.Count == 0, "comanda vazia não gera troca");

        // ── QUEM PARA A TELA E QUEM SÓ APARECE NA FAIXA ─────────────────────
        // A tela de venda tem uma regra escrita no topo dela: nada de diálogo
        // bloqueante no caminho de alta frequência. Preço que cai respeita isso.
        // Preço que sobe não pode: o operador já falou o total para o cliente.
        checar(!Reprecificacao.PrecisaParar(trocas), "preço que cai não trava a tela");
        checar(Reprecificacao.PrecisaParar(subiu), "preço que sobe trava a tela");
        checar(!Reprecificacao.PrecisaParar(igual), "sem troca, nada trava");

        var faixa = Reprecificacao.Faixa(trocas)!;
        checar(faixa.Contains("PRODUTO TESTE") && faixa.Contains("0,10"),
            $"a faixa de um item diz o produto e o preço novo ({faixa})");
        checar(!faixa.Contains('\n'), "a faixa cabe em UMA linha");
        checar(Reprecificacao.Faixa(doisItens)!.Contains("2 itens"),
            "com vários itens a faixa conta quantos, sem listar");
        checar(Reprecificacao.Faixa(igual) is null, "sem troca, sem faixa");
        checar(!faixa.Contains('—'), "a faixa não usa travessão");
    }
}
