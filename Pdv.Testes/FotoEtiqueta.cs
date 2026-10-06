using System.IO;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// FOTOGRAFA a comanda em etiqueta 10x15 (05/10/2026), pelo MESMO desenho que vai para a
/// Elgin (<see cref="Impressao.PreVisualizarEtiquetaAsync"/>), sem impressora e sem banco.
///
/// Uso: Pdv.Testes.exe --foto-etiqueta pasta
///
/// Sai:
///   etiqueta-combo.png         pedido do iFood com combo de sabores, observação e item simples
///   etiqueta-nome-longo.png    cardápio web, nome do cliente que não cabe em duas linhas
///   etiqueta-pedido-grande.png pedido comprido: o miolo encolhe, QR e número não
///   etiqueta-agendado.png      pedido agendado de retirada
///   etiqueta-giro-90.png       a primeira, como vai para o driver com o rolo deitado
/// </summary>
public static class FotoEtiqueta
{
    public static int Rodar(string pasta)
    {
        var dir = Path.GetFullPath(pasta);
        Directory.CreateDirectory(dir);
        var hoje = new DateTime(2026, 10, 5, 17, 40, 0);

        static string Json(params TicketItem[] itens) => System.Text.Json.JsonSerializer.Serialize(itens);

        var combo = new Ticket("t-8149", "ifood", "0b1f6c2e-5d1a-4c7e-9a40-8f2d6b13c8a1", "8149", "Ana Beatriz Souza",
            Json(
                new TicketItem("Combo Box 4un", 1000, "sem granulado no Homer",
                    new[] { "Clássicos: 2x Donut Homer", "Clássicos: 1x Donut Ninho com Nutella", "Premium: 1x Donut Pistache" }),
                new TicketItem("Cookie Duplo Chocolate", 2000, null),
                new TicketItem("Café Coado 300ml", 1000, "sem açúcar")),
            Kds.Recebido, hoje.AddMinutes(-3), null, null);

        var nomeLongo = combo with
        {
            Id = "t-cd", RefId = "5e7a1f00-1111-4222-8333-444455556666", Numero = "CD-2246",
            Cliente = "Maria Aparecida dos Santos Albuquerque Figueiredo",
            ItensJson = Json(
                new TicketItem("Combo 1 Cookies - 4 unidades", 1000, null,
                    new[] { "Clássicos: 1x Cookie Duplo Chocolate", "Clássicos: 1x Cookie Red Velvet",
                            "Premium: 1x Cookie Pistache", "Premium: 1x Cookie Ninho com Nutella" })),
        };

        var grande = combo with
        {
            Id = "t-5077", RefId = "9c0d7a55-2222-4333-8444-555566667777", Numero = "5077", Cliente = "Rafael Andrade",
            ItensJson = Json(
                new TicketItem("Combo Box 6un", 1000, "embalar separado",
                    new[] { "2x Donut Ovomaltine", "1x Donut Banoffee", "1x Donut Ninho", "1x Donut Doce de Leite", "1x Donut Homer" }),
                new TicketItem("Tortinha de Frango com Catupiry", 1000, null),
                new TicketItem("Cookie Duplo", 2000, "sem castanha"),
                new TicketItem("Pão de Queijo Recheado", 1000, null),
                new TicketItem("Coxinha de Frango", 2000, null),
                new TicketItem("Suco de Laranja 500ml", 1000, null),
                new TicketItem("Café Coado 300ml", 1000, null)),
        };

        var agendado = combo with
        {
            Id = "t-3788", RefId = "77aa0000-3333-4444-8555-666677778888", Numero = "3788", Cliente = "Bruno Carvalho",
            Retirada = true, Agendado = true, AgendadoPara = hoje.AddMinutes(80), AgendadoAte = hoje.AddMinutes(110),
            ItensJson = Json(new TicketItem("Donut Ninho com Nutella", 6000, null)),
        };

        // PEDIDO REAL iFood #6066 (Castelo, 06/10/2026): o JSON EXATO de ifood_orders.itens,
        // pelo MESMO ItensDeJson da sincronizacao. Combo Box 4un com 4 donuts + Caixinha Extra.
        var real6066 = combo with
        {
            Id = "t-6066", RefId = "8dbd41e5-551f-4842-ba2c-cf1e0d93d0d1", Numero = "6066", Cliente = "coxa killer",
            ItensJson = System.Text.Json.JsonSerializer.Serialize(Kds.ItensDeJson(TestesEtiquetaKds.Itens6066)),
        };

        var fotos = new (string Nome, Ticket T, int Giro)[]
        {
            ("etiqueta-6066.png", real6066, 0),
            ("etiqueta-combo.png", combo, 0),
            ("etiqueta-nome-longo.png", nomeLongo, 0),
            ("etiqueta-pedido-grande.png", grande, 0),
            ("etiqueta-agendado.png", agendado, 0),
            ("etiqueta-giro-90.png", combo, 90),
        };
        var codigo = 0;
        foreach (var (nome, t, giro) in fotos)
        {
            var saida = Path.Combine(dir, nome);
            var erro = Impressao.PreVisualizarEtiquetaAsync(t, saida, giro, 3.0, hoje).GetAwaiter().GetResult();
            if (erro is null) Console.WriteLine($"foto-etiqueta: {saida}");
            else { Console.Error.WriteLine($"foto-etiqueta: {nome}: {erro}"); codigo = 1; }
        }
        return codigo;
    }
}
