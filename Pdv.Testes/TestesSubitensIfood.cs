using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// SABORES DO COMBO DO iFOOD NO CAIXA (12/09/2026, pedido 7673, real).
///
/// "Leve 5 brownies, pague só 4" com 2 Brownie American Day e 3 Brownie Nutella. O dono
/// viu num KDS só a linha do combo, sem os sabores, e pediu conferência em todos os
/// subitens. Aqui a prova do CAIXA com o JSON exatamente como a nuvem entrega (o shape
/// da ponte, `complements: [{nome, qtd}]`): card, detalhe e comanda mostram os dois
/// sabores com a quantidade certa, e o nome do combo não é mutilado.
/// </summary>
public static class TestesSubitensIfood
{
    private const string Itens7673 =
        "[{\"complements\":[],\"descricao\":\"Cookie Pink Limonade\",\"qtd\":1,\"valor_unitario\":22.9}," +
        "{\"complements\":[{\"nome\":\"Brownie American Day\",\"qtd\":2},{\"nome\":\"Brownie Nutella\",\"qtd\":3}]," +
        "\"descricao\":\"Leve 5 brownies, pague só 4 - do melhor brownie da vida!\",\"qtd\":1,\"valor_unitario\":67.6}]";

    public static void Rodar(Action<bool, string> checar)
    {
        var itens = Kds.ItensDeJson(Itens7673);
        checar(itens.Count == 2, "o pedido tem dois itens (cookie e combo)");
        var combo = itens[1];
        checar(combo.Descricao == "Leve 5 brownies, pague só 4 - do melhor brownie da vida!" && combo.Qtd == 1000,
            "o combo entra com o nome inteiro e quantidade 1");
        checar(combo.Escolhas is { Count: 2 } && combo.Escolhas[0] == "2x Brownie American Day" && combo.Escolhas[1] == "3x Brownie Nutella",
            $"os dois sabores viram escolhas com a quantidade ({string.Join(" | ", combo.Escolhas ?? new List<string>())})");
        checar(itens[0].Escolhas is null, "item sem sabor não ganha lista vazia");

        // o card: principal + duas linhas de subitem, quantidade na frente
        var principal = CardKds.ItemPrincipal(combo);
        checar(principal.Nome.Contains("Leve 5 brownies") && principal.Nome.Contains("pague só 4"),
            $"o nome do combo não é cortado no card ({principal.Nome})");
        var sub1 = CardKds.SubItem(combo.Escolhas![0]);
        var sub2 = CardKds.SubItem(combo.Escolhas![1]);
        checar(sub1.Qtd.StartsWith("2") && sub1.Nome == "Brownie American Day", $"subitem 1: {sub1.Qtd} {sub1.Nome}");
        checar(sub2.Qtd.StartsWith("3") && sub2.Nome == "Brownie Nutella", $"subitem 2: {sub2.Qtd} {sub2.Nome}");

        // o detalhe (pop-up do KDS). O ticket guarda os itens JÁ PARSEADOS (o mesmo
        // JsonSerializer.Serialize(itens) que a ingestão grava em kds_ticket.itens_json):
        // o JSON cru da nuvem só existe na porta de entrada.
        var t = new Ticket("t7673", "ifood", "905b2b4e-7991-4fe5-813a-8aefdba877a4", "7673", "Cliente",
            System.Text.Json.JsonSerializer.Serialize(itens),
            Kds.Recebido, new DateTime(2026, 9, 12, 17, 30, 0), null, null);
        var det = DetalhePedido.De(t, new DateTime(2026, 9, 12, 17, 40, 0));
        var itemDet = det.Itens.FirstOrDefault(i => i.Nome.Contains("Leve 5 brownies"));
        checar(itemDet is not null && itemDet.Escolhas.Count == 2
               && itemDet.Escolhas.Any(e => e.Nome == "Brownie American Day") && itemDet.Escolhas.Any(e => e.Nome == "Brownie Nutella"),
            $"o detalhe do pedido lista os dois sabores ({det.Itens.Count} itens: {string.Join(" / ", det.Itens.Select(i => i.Qtd + " " + i.Nome + " [" + string.Join(", ", i.Escolhas.Select(e => e.Qtd + e.Nome)) + "]"))})");

        // a comanda impressa: um quadradinho por sabor
        var comanda = Kds.ComandaLinhas(t, 40, new DateTime(2026, 9, 12));
        var texto = string.Join("\n", comanda);
        checar(texto.Contains("2x Brownie American Day") && texto.Contains("3x Brownie Nutella"),
            $"a comanda imprime os dois sabores com a quantidade ({comanda.Count} linhas: {string.Join(" / ", comanda.Take(14).Select(l => l.Trim()))})");
        checar(comanda.Count(l => l.Contains("[ ]") && l.Contains("Brownie")) == 2,
            "cada sabor tem o próprio quadradinho de conferência");
        checar(texto.Contains("Cookie Pink Limonade"), "o cookie avulso continua na comanda");
    }
}
