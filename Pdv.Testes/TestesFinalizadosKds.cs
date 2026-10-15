using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// OS PEDIDOS QUE JÁ SAÍRAM DO QUADRO (14/10/2026, pedido do dono na Savassi: "opção também de
/// procurar pedidos finalizados no KDS do PDV"). A lógica mora em Nucleo.FinalizadosKds; aqui:
///
///  FK-1..  período (hoje, ontem, 7 dias) pela chegada no DIA DE OPERAÇÃO (vira às 05:00);
///  FK-10.. busca por número (só dígitos, aceita "CD-123") e por nome (sem maiúscula nem acento);
///  FK-20.. o rótulo de cada linha: 'cancelado' local NÃO vira CANCELADO na tela sem motivo certo;
///  FK-30.. ordem (mais novo primeiro pela saída) e teto de 300;
///  FK-40.. textos: uma linha, sem travessão, teto de 4 linhas;
///  FK-50.. cada saída do quadro anota hora e motivo, sem sobrescrever (SQLite de verdade);
///  FK-70.. o reforço da nuvem só troca o "não sei";
///  FK-80.. o bipe da etiqueta continua marcando PRONTO, e não chama de cancelado quem só saiu;
///  FK-90.. o detalhe aberto da lista: etapa própria e "Saiu às";
///  FK-100.. a tela pelo fonte;
///  FK-120.. REVISÃO de 14/10: cancelado depois de sair, segunda via, hora de quem expirou, a loja,
///           o "Fale com o iFood" de outro dia, a consulta dos cancelados na nuvem (fake);
///  FK-160.. a TELA DE VERDADE a 1024x768 (WPF): cabeçalho com busca, faixa de pedido novo,
///           etiqueta na busca da lista, botões do detalhe e o texto que cabe neles.
/// </summary>
public static class TestesFinalizadosKds
{
    private const string Itens = "[{\"Descricao\":\"Donut Homer\",\"Qtd\":2000,\"Observacao\":null},"
                               + "{\"Descricao\":\"Cookie Red Velvet\",\"Qtd\":1000,\"Observacao\":null}]";

    private static readonly DateTime Agora = new(2026, 10, 14, 15, 0, 0);

    private static Ticket T(string numero, string status, DateTime criado, string origem = "ifood",
                            bool retirada = false, DateTime? entregue = null, DateTime? encerrado = null,
                            string? motivo = null, string? cliente = null, DateTime? pronto = null, string? id = null,
                            bool cancelado = false, DateTime? canceladoEm = null)
        => new(id ?? "t-" + numero, origem, "ref-" + numero, numero, cliente, Itens, status, criado,
               null, pronto, Retirada: retirada, EntregueEm: entregue, EncerradoEm: encerrado, EncerradoMotivo: motivo,
               CanceladoCerto: cancelado || canceladoEm is not null, CanceladoEm: canceladoEm);

    public static void Rodar(Action<bool, string> checar)
    {
        Periodo(checar);
        Busca(checar);
        Rotulos(checar);
        OrdemETeto(checar);
        Textos(checar);
        ReforcoDaNuvem(checar);
        Bipe(checar);
        Detalhe(checar);
        NoBanco(checar);
        Tela(checar);
        RevisaoPura(checar);
        RevisaoNoBanco(checar);
        NaTela(checar);
    }

    private static void Periodo(Action<bool, string> checar)
    {
        var hoje = Agora.Date.AddHours(5);   // o dia de operação começa às 05:00
        checar(FinalizadosKds.Janela(PeriodoFinalizados.Hoje, Agora) == (hoje, hoje.AddDays(1))
               && FinalizadosKds.Janela(PeriodoFinalizados.Ontem, Agora) == (hoje.AddDays(-1), hoje)
               && FinalizadosKds.Janela(PeriodoFinalizados.SeteDias, Agora) == (hoje.AddDays(-6), hoje.AddDays(1)),
            "FK-1 janelas: hoje, ontem, e hoje com os seis dias antes, de 05:00 a 05:00");
        var virada = T("1", Kds.Entregue, hoje);
        var antesDaVirada = T("2", Kds.Entregue, hoje.AddSeconds(-1));
        checar(FinalizadosKds.NoPeriodo(virada, PeriodoFinalizados.Hoje, Agora)
               && !FinalizadosKds.NoPeriodo(antesDaVirada, PeriodoFinalizados.Hoje, Agora)
               && FinalizadosKds.NoPeriodo(antesDaVirada, PeriodoFinalizados.Ontem, Agora),
            "FK-2 a virada do dia é às 05:00, pela CHEGADA: 04:59:59 é ontem, 05:00 é hoje");
        checar(FinalizadosKds.NoPeriodo(T("3", Kds.Entregue, hoje.AddDays(-6)), PeriodoFinalizados.SeteDias, Agora)
               && !FinalizadosKds.NoPeriodo(T("4", Kds.Entregue, hoje.AddDays(-6).AddSeconds(-1)), PeriodoFinalizados.SeteDias, Agora)
               && FinalizadosKds.NoPeriodo(T("5", Kds.Entregue, Agora.Date.AddHours(23)), PeriodoFinalizados.SeteDias, Agora),
            "FK-3 7 dias: de seis dias atrás até o fim de hoje; o sétimo dia para trás fica de fora");
        // O CASO DO ACHADO (Castelo): chegou 23:40, saiu 00:10; às 00:15 o operador procura o que acabou de sair
        var meiaNoiteEQuinze = Agora.Date.AddDays(1).AddMinutes(15);
        var virou = T("6", Kds.Entregue, Agora.Date.AddHours(23).AddMinutes(40), encerrado: Agora.Date.AddDays(1).AddMinutes(10));
        checar(FinalizadosKds.Montar(new[] { virou }, PeriodoFinalizados.Hoje, null, meiaNoiteEQuinze).Total == 1
               && FinalizadosKds.Montar(new[] { virou }, PeriodoFinalizados.Ontem, null, meiaNoiteEQuinze).Total == 0,
            "FK-4 chegou 23:40 e saiu 00:10: às 00:15 está em HOJE (era 'Nenhum pedido saiu hoje')");
        var amostras = new[] { Agora.Date, Agora.Date.AddHours(4).AddMinutes(59), Agora.Date.AddHours(5), Agora.Date.AddHours(12), Agora.Date.AddHours(23).AddMinutes(59) };
        checar(amostras.All(m => FinalizadosKds.InicioDoDia(m).ToString("yyyy-MM-dd") == Caixa.DiaOperacional(m)),
            "FK-5 o dia da lista é o MESMO dia de operação do turno do caixa (Caixa.DiaOperacional)");
    }

    private static void Busca(Action<bool, string> checar)
    {
        var a = T("4719", Kds.Entregue, Agora.AddHours(-1), cliente: "João da Silva");
        var b = T("1470", Kds.Entregue, Agora.AddHours(-1), cliente: "Ana Beatriz Souza");
        var c = T("CD-123", Kds.Entregue, Agora.AddHours(-1), cliente: "MARIA ÂNGELA");
        var d = T("8146", Kds.Entregue, Agora.AddHours(-1));

        checar(FinalizadosKds.CasaBusca(a, "4719") && FinalizadosKds.CasaBusca(a, "#47 19")
               && FinalizadosKds.CasaBusca(a, "47") && FinalizadosKds.CasaBusca(b, "47") && !FinalizadosKds.CasaBusca(d, "47"),
            "FK-10 número: só os dígitos contam, e um pedaço acha (47 acha 4719 e 1470), como no quadro");
        checar(FinalizadosKds.CasaBusca(c, "CD-123") && FinalizadosKds.CasaBusca(c, "cd 12") && FinalizadosKds.CasaBusca(c, "123")
               && !FinalizadosKds.CasaBusca(a, "CD-123"),
            "FK-11 'CD-123' é busca por número: acha o pedido do cardápio e não acha o 4719");
        checar(FinalizadosKds.CasaBusca(a, "joao") && FinalizadosKds.CasaBusca(a, "SILVA") && FinalizadosKds.CasaBusca(a, "João  da")
               && FinalizadosKds.CasaBusca(c, "angela") && FinalizadosKds.CasaBusca(c, "Ângela") && !FinalizadosKds.CasaBusca(b, "joao"),
            "FK-12 nome: pedaço do nome, sem diferença de maiúscula, de acento nem de espaço a mais");
        checar(!FinalizadosKds.CasaBusca(d, "joao"), "FK-13 pedido sem nome não casa com busca por nome");
        checar(FinalizadosKds.CasaBusca(d, "") && FinalizadosKds.CasaBusca(d, null) && FinalizadosKds.CasaBusca(d, "   "),
            "FK-14 busca vazia: todos");
        checar(FinalizadosKds.BuscaPorNumero("4719") && FinalizadosKds.BuscaPorNumero("#4719") && FinalizadosKds.BuscaPorNumero("CD-123")
               && FinalizadosKds.BuscaPorNumero("cd 123") && !FinalizadosKds.BuscaPorNumero("ana") && !FinalizadosKds.BuscaPorNumero("ana 12")
               && !FinalizadosKds.BuscaPorNumero("cd") && !FinalizadosKds.BuscaPorNumero(""),
            "FK-15 qual busca é por número: dígitos e separadores, com ou sem 'CD' na frente; letra vira nome");
        checar(FinalizadosKds.Normalizar("  JOÃO   da Silva ") == "joao da silva", "FK-16 normalizar: minúscula, sem acento, espaços apertados");

        // A BUSCA DO QUADRO ABERTO CONTINUA IGUAL: só número, e nome não filtra nada.
        Ticket Aberto(string n) => T(n, Kds.Recebido, Agora, cliente: "Ana");
        var quadro = new[] { Aberto("4719"), Aberto("8146"), Aberto("1470") };
        checar(Kds.FiltrarPorNumero(quadro, "47").Select(t => t.Numero).SequenceEqual(new[] { "4719", "1470" })
               && Kds.FiltrarPorNumero(quadro, "ana").Count() == 3 && Kds.FiltrarPorNumero(quadro, "").Count() == 3,
            "FK-17 a busca do quadro aberto não mudou: número filtra, nome e vazio mostram tudo");
    }

    private static void Rotulos(Action<bool, string> checar)
    {
        var h = Agora.AddHours(-1);
        (Ticket T, string Esperado, string Caso)[] tabela =
        {
            (T("1", Kds.Entregue, h), "SAIU", "entregue, entrega"),
            (T("2", Kds.Entregue, h, retirada: true), "RETIRADO", "entregue, retirada"),
            (T("3", Kds.Entregue, h, origem: "balcao"), "RETIRADO", "entregue, balcão"),
            (T("4", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoGestor), "SAIU", "Gestor despachou antes do card andar"),
            (T("5", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoGestor, retirada: true), "RETIRADO", "Gestor concluiu retirada"),
            (T("6", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoIfoodCancelou), "CANCELADO", "o iFood cancelou"),
            (T("7", Kds.Cancelado, h, origem: "balcao", motivo: FinalizadosKds.MotivoVendaCancelada), "CANCELADO", "venda cancelada"),
            (T("8", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoExpirou), "SAIU DO QUADRO", "expirou"),
            (T("9", Kds.Cancelado, h), "SAIU DO QUADRO", "antigo, sem motivo"),
            (T("CD-10", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoCardapioLargou), "SAIU DO QUADRO", "cardápio largou"),
            (T("11", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoNuvemConcluiu), "SAIU", "a nuvem confirmou concluído"),
            // revisão de 14/10: o cancelamento certo ganha da saída
            (T("12", Kds.Entregue, h, motivo: FinalizadosKds.MotivoGestor, cancelado: true), "CANCELADO", "despachado e depois cancelado pelo iFood"),
            (T("13", Kds.Entregue, h, origem: "balcao", motivo: FinalizadosKds.MotivoClienteLevou, cancelado: true), "CANCELADO", "cliente levou e a venda foi cancelada"),
            (T("14", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoNuvemConcluiu, cancelado: true), "CANCELADO", "a nuvem disse concluído, o evento disse cancelado"),
        };
        var errados = tabela.Where(x => FinalizadosKds.Rotulo(FinalizadosKds.Estado(x.T)) != x.Esperado).ToList();
        checar(errados.Count == 0,
            "FK-20 a tabela de rótulos (" + tabela.Length + " casos)"
            + (errados.Count == 0 ? "" : ": erraram " + string.Join(", ", errados.Select(e => $"{e.Caso} deu {FinalizadosKds.Rotulo(FinalizadosKds.Estado(e.T))}"))));
        checar(FinalizadosKds.Estado(T("12", Kds.Cancelado, h)) != EstadoFinalizado.Cancelado,
            "FK-21 'cancelado' guardado sem motivo NUNCA vira CANCELADO na tela (o Gestor despachar também vira 'cancelado')");
        checar(FinalizadosKds.CoresDoRotulo(EstadoFinalizado.Cancelado).Cor == "Erro"
               && FinalizadosKds.CoresDoRotulo(EstadoFinalizado.Saiu).Cor == "Ok"
               && FinalizadosKds.CoresDoRotulo(EstadoFinalizado.SaiuDoQuadro).Fundo == "EsperaFundo",
            "FK-22 cor de cada rótulo: saiu verde, cancelado vermelho, não sei cinza");
        checar(FinalizadosKds.Origem(T("1", Kds.Entregue, h)) == "iFOOD" && FinalizadosKds.Origem(T("CD-2", Kds.Entregue, h)) == "CARDÁPIO"
               && FinalizadosKds.Origem(T("3", Kds.Entregue, h, origem: "balcao")) == "BALCÃO",
            "FK-23 etiqueta de origem: iFOOD, CARDÁPIO, BALCÃO");
        checar(FinalizadosKds.MotivoIncerto(null) && FinalizadosKds.MotivoIncerto(FinalizadosKds.MotivoExpirou)
               && FinalizadosKds.MotivoIncerto(FinalizadosKds.MotivoCardapioLargou)
               && !FinalizadosKds.MotivoIncerto(FinalizadosKds.MotivoGestor) && !FinalizadosKds.MotivoIncerto(FinalizadosKds.MotivoIfoodCancelou),
            "FK-24 'não sei' é vazio, expirou e cardápio largou; o resto é certeza");
        checar(FinalizadosKds.Saiu(Kds.Entregue) && FinalizadosKds.Saiu(Kds.Cancelado) && !FinalizadosKds.Saiu(Kds.Pronto)
               && !FinalizadosKds.Saiu(Kds.Recebido) && !FinalizadosKds.Saiu(Kds.Preparando),
            "FK-25 só entregue e cancelado estão na lista; quem está no quadro não");
    }

    private static void OrdemETeto(Action<bool, string> checar)
    {
        var lista = new[]
        {
            T("100", Kds.Entregue, Agora.AddHours(-3), encerrado: Agora.AddHours(-2)),
            T("200", Kds.Entregue, Agora.AddHours(-2), encerrado: Agora.AddMinutes(-10)),
            // antigo: sem encerrado_em, só entregue_em
            T("300", Kds.Entregue, Agora.AddHours(-4), entregue: Agora.AddMinutes(-30)),
            // antigo cancelado sem hora nenhuma: entra pelo último carimbo (pronto)
            T("400", Kds.Cancelado, Agora.AddHours(-5), pronto: Agora.AddMinutes(-60)),
            T("500", Kds.Pronto, Agora.AddHours(-1)),        // no quadro: fora
            T("600", Kds.Recebido, Agora.AddMinutes(-5)),     // no quadro: fora
            T("700", Kds.Entregue, Agora.AddDays(-1), encerrado: Agora.AddDays(-1).AddMinutes(20)),   // ontem: fora de hoje
        };
        var m = FinalizadosKds.Montar(lista, PeriodoFinalizados.Hoje, null, Agora);
        checar(m.Linhas.Select(l => l.Numero).SequenceEqual(new[] { "#200", "#300", "#400", "#100" }),
            "FK-30 do mais novo para o mais velho pela SAÍDA; sem hora de saída, pelo último carimbo; quem está no quadro e quem é de ontem ficam fora ("
            + string.Join(" ", m.Linhas.Select(l => l.Numero)) + ")");
        checar(m.Total == 4 && !m.Cortada, "FK-31 sem passar do teto, nada cortado");

        var muitos = Enumerable.Range(0, 350)
            .Select(i => T((1000 + i).ToString(), Kds.Entregue, Agora.Date.AddHours(5).AddMinutes(i), encerrado: Agora.Date.AddHours(5).AddMinutes(i + 5)))
            .ToList();
        var fimDoDia = Agora.Date.AddHours(23);
        var cheio = FinalizadosKds.Montar(muitos, PeriodoFinalizados.Hoje, null, fimDoDia);
        checar(cheio.Linhas.Count == 300 && cheio.Total == 350 && cheio.Cortada
               && cheio.Linhas[0].Numero == "#1349" && cheio.Linhas[^1].Numero == "#1050",
            "FK-32 teto de 300: mostra os 300 mais novos e sabe que havia 350");
        checar(FinalizadosKds.Montar(muitos, PeriodoFinalizados.Hoje, "1349", fimDoDia).Linhas.Single().Numero == "#1349",
            "FK-33 a busca procura nos 350, não só nos 300 da tela");
        checar(FinalizadosKds.Contar(muitos, PeriodoFinalizados.Hoje, "13", fimDoDia)
               == FinalizadosKds.Montar(muitos, PeriodoFinalizados.Hoje, "13", fimDoDia).Total,
            "FK-34 o número do botão de período é o mesmo total da lista");
    }

    private static void Textos(Action<bool, string> checar)
    {
        var l1 = FinalizadosKds.Linha(T("4719", Kds.Entregue, Agora.Date.AddHours(14).AddMinutes(5),
                                        encerrado: Agora.Date.AddHours(14).AddMinutes(32), cliente: " Ana "), Agora);
        checar(l1.Chegou == "Chegou 14:05" && l1.Saiu == "Saiu 14:32" && l1.Cliente == "Ana" && l1.Rotulo == "SAIU",
            $"FK-40 linha de hoje: 'Chegou 14:05', 'Saiu 14:32' ({l1.Chegou} / {l1.Saiu})");
        var ontem = Agora.Date.AddDays(-1).AddHours(20);
        var l2 = FinalizadosKds.Linha(T("1", Kds.Cancelado, ontem, encerrado: ontem.AddMinutes(9), motivo: FinalizadosKds.MotivoIfoodCancelou,
                                        canceladoEm: ontem.AddMinutes(9)), Agora);
        checar(l2.Chegou == "Chegou ontem 20:00" && l2.Saiu == "Cancelado ontem 20:09",
            $"FK-41 ontem diz 'ontem', e o cancelado diz 'Cancelado' ({l2.Chegou} / {l2.Saiu})");
        var l3 = FinalizadosKds.Linha(T("2", Kds.Cancelado, new DateTime(2026, 10, 12, 9, 7, 0)), Agora);
        checar(l3.Chegou == "Chegou 12/10 09:07" && l3.Saiu is null,
            "FK-42 outro dia leva a data; sem hora de saída anotada, a linha não inventa uma");
        checar(l1.Resumo == "2× Donut Homer, 1× Cookie Red Velvet", "FK-43 resumo dos itens com as mesmas linhas do card: " + l1.Resumo);

        checar(FinalizadosKds.BotaoSairamHoje(143) == "Saíram hoje: 143" && FinalizadosKds.BotaoSairamHoje(1150) == "Saíram hoje: 1.150",
            "FK-44 o botão da coluna PRONTO: 'Saíram hoje: 143', milhar com ponto");
        checar(FinalizadosKds.BotaoPeriodo(PeriodoFinalizados.Hoje, 143) == "Hoje (143)"
               && FinalizadosKds.BotaoPeriodo(PeriodoFinalizados.Ontem, 170) == "Ontem (170)"
               && FinalizadosKds.BotaoPeriodo(PeriodoFinalizados.SeteDias, 1150) == "7 dias (1.150)",
            "FK-45 botões de período: 'Hoje (143)', 'Ontem (170)', '7 dias (1.150)'");
        checar(FinalizadosKds.TextoVazio(PeriodoFinalizados.Hoje, null) == "Nenhum pedido saiu hoje neste caixa."
               && FinalizadosKds.TextoVazio(PeriodoFinalizados.Hoje, "4719", 2) == "Nada com \"4719\" hoje. Toque em 7 dias."
               && FinalizadosKds.TextoCortada() == "Mostrando os 300 mais novos. Procure pelo número ou nome.",
            "FK-46 os textos de lista vazia e cortada do desenho");
        checar(FinalizadosKds.TextoFaixaBusca("4719", 0, 1) == "No quadro: nenhum. Já saíram: 1."
               && FinalizadosKds.TextoFaixaBusca("47", 2, 0) == "No quadro: 2. Já saíram: nenhum.",
            "FK-47 a faixa da busca no quadro: 'No quadro: nenhum. Já saíram: 1.'");
        checar(FinalizadosKds.TextoFaixaBusca("ana", 12, 3) == "O quadro só procura por número. Com esse nome, 3 já saíram."
               && FinalizadosKds.TextoFaixaBusca("ana", 12, 1) == "O quadro só procura por número. Com esse nome, 1 já saiu."
               && FinalizadosKds.TextoFaixaBusca("ana", 12, 0) == "O quadro só procura por número. Com esse nome, nenhum saiu.",
            "FK-48 busca por nome no quadro: a faixa diz que o quadro só procura por número e quantos saíram com o nome");

        var todos = new List<string>
        {
            FinalizadosKds.BotaoSairamHoje(1150), FinalizadosKds.BotaoVoltarAoQuadro, FinalizadosKds.BotaoVerOsQueSairam,
            FinalizadosKds.TextoGuardado, FinalizadosKds.TextoSemInternet, FinalizadosKds.TextoEtiquetaNaBusca,
            FinalizadosKds.BotaoComandaDeNovo, FinalizadosKds.TextoComandaSaiu, FinalizadosKds.TextoComandaNaoSaiu,
            FinalizadosKds.TextoCortada(), FinalizadosKds.DicaBuscaLista, FinalizadosKds.DicaBuscaQuadro,
            FinalizadosKds.TextoFaixaBusca("ana maria da silva", 1150, 1150), FinalizadosKds.TextoFaixaBusca("4719", 1150, 1150),
            FinalizadosKds.TextoPedidosNovos(1), FinalizadosKds.TextoPedidosNovos(1150),
        };
        foreach (var p in Enum.GetValues<PeriodoFinalizados>())
        {
            todos.Add(FinalizadosKds.BotaoPeriodo(p, 1150));
            todos.Add(FinalizadosKds.TextoVazio(p, null));
            todos.Add(FinalizadosKds.TextoVazio(p, "4719", 0));
            todos.Add(FinalizadosKds.TextoVazio(p, "4719", 3));
        }
        foreach (var e in Enum.GetValues<EstadoFinalizado>()) todos.Add(FinalizadosKds.Rotulo(e));
        var ruins = todos.Where(t => t.Contains('\n') || t.Contains('—') || t.Contains('–') || t.Length > 90 || t.Trim().Length == 0).ToList();
        checar(ruins.Count == 0, $"FK-49 todo texto da lista é uma linha curta, sem travessão ({todos.Count} textos)"
                                 + (ruins.Count == 0 ? "" : ": " + string.Join(" | ", ruins)));
        checar(todos.All(t => t.Split('\n').Length <= 4), "FK-50 nenhum passa de 4 linhas (teto do caixa)");
    }

    private static void ReforcoDaNuvem(Action<bool, string> checar)
    {
        checar(FinalizadosKds.MotivoDaNuvem("concluido", false) == FinalizadosKds.MotivoNuvemConcluiu
               && FinalizadosKds.MotivoDaNuvem("DESPACHADO", false) == FinalizadosKds.MotivoNuvemConcluiu
               && FinalizadosKds.MotivoDaNuvem("cancelado", false) == FinalizadosKds.MotivoIfoodCancelou,
            "FK-70 a nuvem prova saída (concluído, despachado) e cancelamento no iFood");
        checar(FinalizadosKds.MotivoDaNuvem("cancelado", true) is null && FinalizadosKds.MotivoDaNuvem("concluido", true) == FinalizadosKds.MotivoNuvemConcluiu,
            "FK-71 no cardápio próprio 'cancelado' é qualquer estado desconhecido (até 'coletado'): não prova nada");
        checar(FinalizadosKds.MotivoDaNuvem("pronto", false) is null && FinalizadosKds.MotivoDaNuvem("faturado", false) is null
               && FinalizadosKds.MotivoDaNuvem(null, false) is null,
            "FK-72 pronto, faturado ou vazio não provam saída nenhuma");
        var h = Agora.AddHours(-1);
        var lista = new[]
        {
            T("1", Kds.Cancelado, h),                                                 // sem motivo: pergunta
            T("2", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoExpirou),           // expirou: pergunta
            T("3", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoGestor),            // certo: não pergunta
            T("4", Kds.Entregue, h),                                                  // entregue: não pergunta
            T("5", Kds.Cancelado, h, origem: "balcao"),                               // balcão: não tem nuvem
            T("6", Kds.Pronto, h),                                                    // no quadro
        };
        checar(FinalizadosKds.ParaConfirmarNaNuvem(lista).SequenceEqual(new[] { "ref-1", "ref-2" }),
            "FK-73 só os SAIU DO QUADRO do delivery vão para a pdv_kds_status: " + string.Join(",", FinalizadosKds.ParaConfirmarNaNuvem(lista)));
        var cento = Enumerable.Range(0, 150).Select(i => T(i.ToString(), Kds.Cancelado, h)).ToList();
        checar(FinalizadosKds.ParaConfirmarNaNuvem(cento).Count == 100, "FK-74 no máximo 100 por consulta (o teto da RPC)");
    }

    private static void Bipe(Action<bool, string> checar)
    {
        ResultadoBipe Bipar(Ticket t)
            => new BipeKds(_ => t, _ => throw new Exception("não pode marcar quem saiu")).Processar(t.RefId, Agora);
        var h = Agora.AddHours(-1);
        var gestor = Bipar(T("5077", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoGestor));
        checar(gestor.Desfecho == DesfechoBipe.JaSaiu && gestor.Mensagem == "5077 já saiu do quadro",
            "FK-80 bipe de pedido que o Gestor despachou: 'já saiu do quadro', e não 'foi cancelado' (" + gestor.Mensagem + ")");
        var expirou = Bipar(T("5078", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoExpirou));
        checar(expirou.Desfecho == DesfechoBipe.JaSaiu, "FK-81 bipe de pedido que expirou: já saiu do quadro");
        var cancelado = Bipar(T("5079", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoIfoodCancelou));
        checar(cancelado.Desfecho == DesfechoBipe.Cancelado && cancelado.Mensagem == "5079 foi cancelado",
            "FK-82 bipe de pedido que o iFood cancelou: 'foi cancelado', não vai para coleta");
        var antigo = Bipar(T("5080", Kds.Cancelado, h));
        checar(antigo.Desfecho == DesfechoBipe.Cancelado, "FK-83 pedido de antes desta versão (sem motivo): a frase de sempre");
        var entregue = Bipar(T("5081", Kds.Entregue, h, motivo: FinalizadosKds.MotivoGestor));
        checar(entregue.Desfecho == DesfechoBipe.JaSaiu, "FK-84 bipe de entregue: já saiu do quadro, como sempre");
        var depois = Bipar(T("5082", Kds.Entregue, h, motivo: FinalizadosKds.MotivoGestor, cancelado: true));
        checar(depois.Desfecho == DesfechoBipe.Cancelado && depois.Mensagem == "5082 foi cancelado",
            "FK-87 bipe de pedido que saiu e DEPOIS o iFood cancelou: 'foi cancelado' (" + depois.Mensagem + ")");
    }

    private static void Detalhe(Action<bool, string> checar)
    {
        var hoje14 = Agora.Date.AddHours(14);
        var saiu = DetalhePedido.De(T("4719", Kds.Entregue, hoje14, encerrado: hoje14.AddMinutes(32)), Agora);
        checar(saiu.Etapa == "SAIU" && saiu.SaiuAs == "Saiu às 14:32" && saiu.JaSaiu,
            $"FK-90 detalhe de quem saiu: etapa SAIU e 'Saiu às 14:32' (era 'NA FILA') ({saiu.Etapa} / {saiu.SaiuAs})");
        var cancelado = DetalhePedido.De(T("4720", Kds.Cancelado, hoje14, encerrado: hoje14.AddMinutes(9), motivo: FinalizadosKds.MotivoIfoodCancelou,
                                           canceladoEm: hoje14.AddMinutes(9)), Agora);
        checar(cancelado.Etapa == "CANCELADO" && cancelado.CanceladoAs == "Cancelado às 14:09" && cancelado.SaiuAs is null,
            "FK-91 detalhe de cancelado no quadro: etapa CANCELADO e 'Cancelado às 14:09', sem 'Saiu às'");
        var antigo = DetalhePedido.De(T("4721", Kds.Cancelado, hoje14), Agora);
        checar(antigo.Etapa == "SAIU DO QUADRO" && antigo.SaiuAs is null && antigo.JaSaiu,
            "FK-92 detalhe de pedido antigo sem motivo: SAIU DO QUADRO, sem hora inventada");
        var outroDia = DetalhePedido.De(T("4722", Kds.Entregue, new DateTime(2026, 10, 12, 14, 0, 0), retirada: true,
                                          encerrado: new DateTime(2026, 10, 12, 14, 32, 0)), Agora);
        checar(outroDia.Etapa == "RETIRADO" && outroDia.SaiuAs == "Saiu 12/10 às 14:32",
            "FK-93 retirada de outro dia: RETIRADO e 'Saiu 12/10 às 14:32'");
        var aberto = DetalhePedido.De(T("4723", Kds.Preparando, hoje14), Agora);
        checar(aberto.Etapa == "FAZENDO" && aberto.SaiuAs is null && !aberto.JaSaiu,
            "FK-94 detalhe de quem está no quadro continua igual (FAZENDO, sem 'Saiu às')");
        checar(saiu.ComNuvem(new DetalheNuvem("ref-4719", "3121 4455", "0807", null, null, null, null, Array.Empty<string>())) is { JaSaiu: true, SaiuAs: "Saiu às 14:32", Localizador: "3121 4455" },
            "FK-95 o complemento da nuvem continua vindo, sem perder o 'Saiu às'");
    }

    /// <summary>
    /// A TELA, pelo fonte (as fotos de docs/kds-fotos-finalizados são a prova visual; NaTela, mais
    /// abaixo, sobe a tela de verdade): os nomes que o código usa, o véu cobrindo as três linhas,
    /// a lista que não redesenha no timer, o detalhe de quem saiu sem raspadinha e com "Comanda de novo".
    /// </summary>
    private static void Tela(Action<bool, string> checar)
    {
        var xaml = Fonte(Path.Combine("Telas", "Kds.xaml")) ?? "";
        var cs = Fonte(Path.Combine("Telas", "Kds.xaml.cs")) ?? "";
        var det = Fonte(Path.Combine("Telas", "DetalhePedidoKds.cs")) ?? "";
        var foto = Fonte(Path.Combine("Pdv.Testes", "FotoKds.cs")) ?? "";
        checar(xaml.Length > 0 && cs.Length > 0 && det.Length > 0, "FK-100 achei Kds.xaml, Kds.xaml.cs e DetalhePedidoKds.cs");
        var nomes = new[] { "BtnSairamHoje", "TxtSairamHoje", "FaixaBusca", "TxtFaixaBusca", "BtnVerSairam", "Quadro",
                            "PainelFinalizados", "BtnVoltarQuadro", "BtnHoje", "BtnOntem", "Btn7Dias", "TxtFonteLista",
                            "TxtAvisoLista", "RolFinalizados", "ListaFinalizados", "BtnNovosNaLista", "TxtNovosNaLista" };
        var faltam = nomes.Where(n => !xaml.Contains($"x:Name=\"{n}\"")).ToList();
        checar(faltam.Count == 0, "FK-101 o quadro tem o botão 'Saíram hoje', a faixa da busca e o painel da lista"
                                  + (faltam.Count == 0 ? "" : ": faltam " + string.Join(", ", faltam)));
        checar(System.Text.RegularExpressions.Regex.IsMatch(xaml, "x:Name=\"Veu\"[^>]*Grid.RowSpan=\"3\""),
            "FK-102 o véu do detalhe cobre as três linhas (cabeçalho, faixa e quadro ou lista)");
        var periodos = new[] { "Tag=\"Hoje\"", "Tag=\"Ontem\"", "Tag=\"SeteDias\"" };
        checar(periodos.All(xaml.Contains) && Enum.GetNames<PeriodoFinalizados>().All(n => xaml.Contains($"Tag=\"{n}\"")),
            "FK-103 cada botão de período tem no Tag o nome do PeriodoFinalizados que o código lê");
        checar(cs.Contains("FinalizadosKds.Montar(sete, _periodo, _busca, agora)") && cs.Contains("FinalizadosKds.TextoVazio(_periodo, _busca, ")
               && cs.Contains("FinalizadosKds.TextoCortada()"),
            "FK-104 a lista sai da régua do núcleo, com os textos de vazia e cortada");
        var timer = Trecho(cs, "_timer.Tick += (_, _) =>", "};");
        checar(timer.Length > 0 && !timer.Contains("PintarFinalizados"),
            "FK-105 o timer de 10 s NÃO redesenha a lista (perderia a rolagem)");
        checar(cs.Contains("if (_finalizados) FecharFinalizados();") && Trecho(cs, "public void PiscarPronto(", "}").Contains("FecharFinalizados"),
            "FK-106 o bipe que marca PRONTO com a lista aberta volta para o quadro e pisca o card");
        checar(cs.Contains("!saiu && t.Origem == \"ifood\" && PediuResgate")
               && cs.Contains("FinalizadosKds.PodeComandaDeNovo(t) && Impressoes.MostraBotaoComanda(_politicaComanda)"),
            "FK-107 detalhe de quem saiu: sem raspadinha, e 'Comanda de novo' só em quem não foi cancelado, na regra do 🖨 do card");
        checar(det.Contains("FinalizadosKds.TextoComandaSaiu") && det.Contains("FinalizadosKds.TextoComandaNaoSaiu")
               && det.Contains("TimeSpan.FromSeconds(3)"),
            "FK-108 a resposta da 'Comanda de novo' aparece no próprio botão por 3 s");
        checar(cs.Contains("StatusPedidosOuNuloAsync") && cs.Contains("Task.Delay(TimeSpan.FromSeconds(5))")
               && cs.Contains("FinalizadosKds.TextoSemInternet") && cs.Contains("FinalizadosKds.AplicarNuvem(status)")
               && cs.Contains("FinalizadosKds.ConferirCancelamentosAsync(ConsultaCancelados, paraCancelamento)"),
            "FK-109 o reforço da nuvem (status e cancelados) espera no máximo 5 s; sem resposta, a barra diz 'Sem internet'");
        checar(cs.Contains("MinHeight = 64") && cs.Contains("AbrirDetalhe(l.Ticket)"),
            "FK-110 a linha inteira é o alvo do toque (64 px) e abre o detalhe que já existe");
        checar(cs.Contains("Nucleo.Kds.FiltrarPorNumero(todos, _busca)"),
            "FK-111 a busca do quadro aberto continua a mesma (só número)");
        checar(cs.Contains("FinalizadosKds.TextoEtiquetaNaBusca") && Trecho(cs, "private void PintarFaixaBusca(", "\n    }").Contains("_etiquetaNaBusca"),
            "FK-112 a etiqueta lida dentro da busca avisa na faixa, que aparece a 1024");
        var textosXaml = System.Text.RegularExpressions.Regex.Matches(xaml, "(?:Text|Content|ToolTip|Tag)=\"([^\"]*)\"")
            .Select(m => m.Groups[1].Value).ToList();
        checar(textosXaml.All(t => !t.Contains('\u2014') && !t.Contains('\u2013')),
            "FK-113 nenhum texto do Kds.xaml tem travessão");
        checar(foto.Contains("--finalizados") && foto.Contains("--busca") && foto.Contains("--sem-finalizados")
               && foto.Contains("--novos") && foto.Contains("--comanda-falhou") && foto.Contains("SemearFinalizados"),
            "FK-114 o modo --foto-kds fotografa a lista, a busca, a lista vazia, o pedido novo e a comanda que falhou");
    }

    // ── REVISÃO DE 14/10/2026 (achados confirmados) ─────────────────────────

    private static void RevisaoPura(Action<bool, string> checar)
    {
        var h = Agora.AddHours(-2);

        // ACHADO 1: o iFood cancelou depois do despacho (#1404 da Savassi)
        var depois = T("1404", Kds.Entregue, h, motivo: FinalizadosKds.MotivoGestor, encerrado: h.AddMinutes(30),
                       canceladoEm: h.AddMinutes(69));
        var l = FinalizadosKds.Linha(depois, Agora);
        checar(l.Rotulo == "CANCELADO" && l.Saiu == "Cancelado " + h.AddMinutes(69).ToString("HH:mm"),
            $"FK-120 despachado e depois cancelado: CANCELADO com a hora do cancelamento ({l.Rotulo} / {l.Saiu})");
        var semHora = FinalizadosKds.Linha(T("1405", Kds.Entregue, h, motivo: FinalizadosKds.MotivoGestor, encerrado: h.AddMinutes(30), cancelado: true), Agora);
        checar(semHora.Rotulo == "CANCELADO" && semHora.Saiu == "Saiu " + h.AddMinutes(30).ToString("HH:mm"),
            "FK-121 cancelado sem hora conhecida: CANCELADO, e a linha mostra a hora da saída (que é verdade)");
        var det = DetalhePedido.De(depois, Agora);
        checar(det.Etapa == "CANCELADO" && det.SaiuAs == "Saiu às " + h.AddMinutes(30).ToString("HH:mm")
               && det.CanceladoAs == "Cancelado às " + h.AddMinutes(69).ToString("HH:mm")
               && det.Meta.Replace((char)0xA0, ' ').Contains(det.SaiuAs!) && det.Meta.Replace((char)0xA0, ' ').Contains(det.CanceladoAs!),
            "FK-122 o detalhe mostra as duas horas: 'Saiu às' e 'Cancelado às' (" + det.Meta + ")");
        var candidatos = new[]
        {
            T("1", Kds.Entregue, h, motivo: FinalizadosKds.MotivoGestor),                 // SAIU: pergunta
            T("2", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoExpirou),               // SAIU DO QUADRO: pergunta
            T("3", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoNuvemConcluiu),         // SAIU pela nuvem: pergunta
            T("4", Kds.Entregue, h, motivo: FinalizadosKds.MotivoGestor, cancelado: true), // já sabido: não
            T("CD-5", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoCardapioLargou),     // cardápio: não
            T("6", Kds.Entregue, h, origem: "balcao"),                                    // balcão: não
            T("7", Kds.Pronto, h),                                                        // no quadro: não
        };
        checar(FinalizadosKds.ParaConferirCancelamento(candidatos).SequenceEqual(new[] { "ref-1", "ref-2", "ref-3" }),
            "FK-123 quem vai para a pergunta dos cancelados: todo iFood que saiu sem cancelamento anotado, sem cardápio, balcão nem quadro ("
            + string.Join(",", FinalizadosKds.ParaConferirCancelamento(candidatos)) + ")");

        // ACHADO 2: a comanda de novo
        checar(!FinalizadosKds.PodeComandaDeNovo(depois)
               && !FinalizadosKds.PodeComandaDeNovo(T("8", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoIfoodCancelou))
               && FinalizadosKds.PodeComandaDeNovo(T("9", Kds.Entregue, h, motivo: FinalizadosKds.MotivoGestor))
               && FinalizadosKds.PodeComandaDeNovo(T("10", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoExpirou))
               && !FinalizadosKds.PodeComandaDeNovo(T("11", Kds.Entregue, h, origem: "balcao"))
               && !FinalizadosKds.PodeComandaDeNovo(T("12", Kds.Pronto, h)),
            "FK-125 'Comanda de novo' nunca em pedido CANCELADO; sim em quem saiu do iFood (com a marca de 2a via)");
        var saiu1432 = T("13", Kds.Entregue, Agora.Date.AddHours(14), encerrado: Agora.Date.AddHours(14).AddMinutes(32));
        checar(FinalizadosKds.MarcaSegundaVia(saiu1432, Agora) == "2a VIA (SAIU 14:32)"
               && FinalizadosKds.MarcaSegundaVia(T("14", Kds.Entregue, new DateTime(2026, 10, 12, 9, 0, 0), encerrado: new DateTime(2026, 10, 12, 9, 40, 0)), Agora) == "2a VIA (SAIU 12/10 09:40)"
               && FinalizadosKds.MarcaSegundaVia(T("15", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoExpirou), Agora) == "2a VIA",
            "FK-126 a marca da segunda via: '2a VIA (SAIU 14:32)', com a data em outro dia, e sem hora quando não se sabe");
        checar(new[] { FinalizadosKds.MarcaSegundaVia(saiu1432, Agora) }.All(s => s.All(c => c < 128)),
            "FK-127 a marca é ASCII (a bobina térmica não garante acento)");
        var comum = Kds.ComandaLinhas(saiu1432, 40, Agora);
        var marcada = Kds.ComandaLinhas(saiu1432, 40, Agora, marca: FinalizadosKds.MarcaSegundaVia(saiu1432, Agora));
        var iPedido = marcada.ToList().FindIndex(x => LinhaEscala.Le(x).Texto.Contains("PEDIDO  #13"));
        checar(marcada.Count == comum.Count + 1 && iPedido >= 0 && LinhaEscala.Le(marcada[iPedido + 1]).Texto.Trim() == "2a VIA (SAIU 14:32)"
               && comum.SequenceEqual(marcada.Where((_, i) => i != iPedido + 1)),
            "FK-128 a bobina da segunda via leva '2a VIA (SAIU 14:32)' logo abaixo do número; o resto é a comanda de sempre");
        checar(EtiquetaKds.Montar(saiu1432, Agora, marca: "2a VIA (SAIU 14:32)").Marca == "2a VIA (SAIU 14:32)"
               && EtiquetaKds.Montar(saiu1432, Agora).Marca is null,
            "FK-129 a etiqueta 10x15 da segunda via leva a mesma marca; a de sempre não");

        // ACHADO 4: a hora de quem expirou não é hora de saída
        var expirou = T("16", Kds.Cancelado, Agora.Date.AddHours(9), pronto: Agora.Date.AddHours(9).AddMinutes(42),
                        encerrado: Agora.Date.AddHours(21).AddMinutes(36), motivo: FinalizadosKds.MotivoNuvemConcluiu);
        var le = FinalizadosKds.Linha(expirou, Agora);
        checar(FinalizadosKds.HoraDaSaida(expirou) is null && le.Saiu is null && le.Rotulo == "SAIU",
            "FK-130 expirou e a nuvem disse concluído: SAIU, mas SEM 'Saiu 21:36' (essa é a hora em que o quadro largou o card)");
        checar(FinalizadosKds.Ordem(expirou) == Agora.Date.AddHours(9).AddMinutes(42),
            "FK-131 e ele entra na ordem pelo último carimbo real (pronto 09:42), não pela expiração");
        checar(FinalizadosKds.HoraDaSaida(T("17", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoExpirou, encerrado: h)) is null
               && FinalizadosKds.HoraDaSaida(T("18", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoCardapioLargou, encerrado: h)) is null
               && FinalizadosKds.HoraDaSaida(T("19", Kds.Cancelado, h, motivo: FinalizadosKds.MotivoGestor, encerrado: h)) == h
               && FinalizadosKds.HoraDaSaida(T("20", Kds.Entregue, h, encerrado: h)) == h
               && FinalizadosKds.HoraDaSaida(T("21", Kds.Entregue, h, entregue: h)) == h,
            "FK-132 hora de saída só da saída de verdade (Gestor, cliente levou, entregue); expirou e cardápio largou não têm");
        checar(DetalhePedido.De(expirou, Agora).SaiuAs is null, "FK-133 o detalhe de quem expirou não diz 'Saiu às'");

        // ACHADO 6/13: a lista vazia não manda tocar num botão que também está vazio
        checar(FinalizadosKds.TextoVazio(PeriodoFinalizados.Hoje, "9999", 0) == "Nada com \"9999\" nos últimos 7 dias neste caixa."
               && FinalizadosKds.TextoVazio(PeriodoFinalizados.Ontem, "9999", 0) == "Nada com \"9999\" nos últimos 7 dias neste caixa."
               && FinalizadosKds.TextoVazio(PeriodoFinalizados.Ontem, "9999", 4) == "Nada com \"9999\" ontem. Toque em 7 dias.",
            "FK-134 'Toque em 7 dias' só quando os 7 dias têm resultado");

        // ACHADO 7: o "Fale com o iFood" de outro dia procuraria outro pedido
        checar(FinalizadosKds.PodeFalarComIfood(T("4719", Kds.Recebido, Agora.AddDays(-3)), Agora)
               && FinalizadosKds.PodeFalarComIfood(T("4720", Kds.Entregue, Agora.AddHours(-1)), Agora)
               && !FinalizadosKds.PodeFalarComIfood(T("4721", Kds.Entregue, Agora.AddDays(-1)), Agora)
               && !FinalizadosKds.PodeFalarComIfood(T("4722", Kds.Entregue, Agora.AddDays(-5)), Agora)
               && !FinalizadosKds.PodeFalarComIfood(T("CD-9", Kds.Entregue, Agora.AddHours(-1)), Agora)
               && !FinalizadosKds.PodeFalarComIfood(T("23", Kds.Entregue, Agora.AddHours(-1), origem: "balcao"), Agora),
            "FK-135 'Fale com o iFood' de quem saiu só para pedido de hoje (o Gestor procura pelo número do dia); no quadro, como sempre");

        // ACHADO 15: o localizador não quebra no meio
        var meta = DetalhePedido.De(saiu1432, Agora, new DetalheNuvem("ref-13", "3121 4455", "0807", null, null, null, null, Array.Empty<string>())).Meta;
        checar(meta.Contains("Localizador\u00A03121\u00A04455") && !meta.Contains("Localizador 3121"),
            "FK-136 o localizador vai com espaços inseparáveis: a quebra não cai no meio do número");
        var metaCancelado = det.ComNuvem(new DetalheNuvem("ref-1404", "3121 4455", null, null, null, null, null, Array.Empty<string>())).Meta;
        checar(metaCancelado.Split(" · ").Length >= 4 && metaCancelado.Split(" · ").All(p => !p.Contains(' ')),
            "FK-144 a linha do detalhe só quebra nos pontos: 'Cancelado às 14:20' e 'Saiu às' não se partem (" + metaCancelado.Replace('\u00A0', '_') + ")");

        // ACHADO 3: a faixa de pedido novo
        checar(FinalizadosKds.TextoPedidosNovos(1) == "Chegou 1 pedido novo. Toque para ver o quadro."
               && FinalizadosKds.TextoPedidosNovos(3) == "Chegaram 3 pedidos novos. Toque para ver o quadro.",
            "FK-137 a faixa de pedido novo na lista: singular e plural escritos à mão");
        var cs = Fonte(Path.Combine("Telas", "Kds.xaml.cs")) ?? "";
        checar(cs.Contains("if (novos > 0 && _finalizados) AvisarNovosNaLista(novos);")
               && Trecho(cs, "private void FecharFinalizados()", "\n    }").Contains("BtnNovosNaLista.Visibility = Visibility.Collapsed"),
            "FK-138 a puxada com pedido novo e a lista aberta acende a faixa; voltar ao quadro apaga");
        checar(Trecho(cs, "if (EtiquetaKds.PareceEtiqueta(TxtBusca.Text))", "return;").Contains("if (_finalizados) PintarFinalizados();"),
            "FK-139 etiqueta lida na busca com a lista aberta: a lista repinta com a busca zerada");
        checar(Trecho(cs, "private void LimparBusca(", "\n    }").Contains("!eraEtiqueta && !_finalizados"),
            "FK-140 limpar a busca com a lista aberta não devolve o foco ao campo (o teclado do Windows não reabre por cima)");
        checar(!Trecho(cs, "private void Pintar()", "\n    }").Contains("TxtStatus.Text"),
            "FK-141 a contagem da busca não vai mais para a linha de status do cabeçalho (a faixa diz)");
        var xaml = Fonte(Path.Combine("Telas", "Kds.xaml")) ?? "";
        checar(System.Text.RegularExpressions.Regex.IsMatch(xaml, "<Grid x:Name=\"CabecalhoEsquerda\"[\\s\\S]*?x:Name=\"TxtStatus\" Grid.Column=\"2\""),
            "FK-142 o status do cabeçalho mora numa coluna de Grid (no StackPanel a reticência nunca agia)");
        checar(System.Text.RegularExpressions.Regex.IsMatch(xaml, "x:Name=\"BtnLimparBusca\"[^>]*MinHeight=\"40\" MinWidth=\"40\""),
            "FK-143 o ✕ da busca tem 40 px (eram 28)");
    }

    private static void RevisaoNoBanco(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"kds_finalizados_rev_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir();
            var colunas = cx.Query<string>("SELECT name FROM pragma_table_info('kds_ticket')").ToList();
            checar(colunas.Contains("cancelado_certo") && colunas.Contains("cancelado_em") && colunas.Contains("loja"),
                "FK-145 o banco do caixa ganhou cancelado_certo, cancelado_em e loja");
            var agora = DateTime.Now;
            var meioDia = FinalizadosKds.InicioDoDia(agora).AddHours(7);
            var donut = Kds.ItensDeJson("[{\"qtd\":1,\"descricao\":\"DONUT\"}]");
            (string Status, string? Motivo, string? Em, long Certo, string? CanceladoEm) Ler(string refId)
            {
                var r = cx.QueryFirst("SELECT status, encerrado_motivo, encerrado_em, cancelado_certo, cancelado_em FROM kds_ticket WHERE ref_id = @r", new { r = refId });
                return ((string)r.status, r.encerrado_motivo as string, r.encerrado_em as string, (long)r.cancelado_certo, r.cancelado_em as string);
            }
            Ticket Ticket(string refId) => (Ticket)Kds.Ler(cx.QueryFirst("SELECT * FROM kds_ticket WHERE ref_id = @r", new { r = refId }));

            // ── ACHADO 1 no banco: o Gestor despacha, o iFood cancela depois ──
            Kds.DoDelivery("o-1404", "1404", "Cliente", donut, chegadaReal: meioDia);
            Kds.Assumir(cx.QueryFirst<string>("SELECT id FROM kds_ticket WHERE ref_id = 'o-1404'"));
            Kds.Liberar(cx.QueryFirst<string>("SELECT id FROM kds_ticket WHERE ref_id = 'o-1404'"));
            Kds.DespacharDelivery("o-1404");
            var saida = Ler("o-1404");
            Thread.Sleep(15);
            Kds.CancelarDelivery("o-1404");
            var depois = Ler("o-1404");
            checar(saida.Status == Kds.Entregue && depois.Motivo == FinalizadosKds.MotivoGestor && depois.Em == saida.Em
                   && depois.Certo == 1 && depois.CanceladoEm is not null,
                "FK-146 despachado e depois cancelado: a saída fica (motivo gestor, mesma hora) e o cancelamento é anotado à parte");
            checar(FinalizadosKds.Rotulo(FinalizadosKds.Estado(Ticket("o-1404"))) == "CANCELADO",
                "FK-147 e a lista diz CANCELADO (era SAIU)");
            var canc1 = depois.CanceladoEm;
            Thread.Sleep(15);
            Kds.CancelarDelivery("o-1404");
            checar(Ler("o-1404").CanceladoEm == canc1, "FK-148 o feed repete o cancelado: a hora do cancelamento não muda");

            // o Gestor despachou da FILA (vira 'cancelado' local com motivo gestor) e o iFood cancelou depois
            Kds.DoDelivery("o-1405", "1405", "Cliente", donut, chegadaReal: meioDia);
            Kds.DespacharDelivery("o-1405");
            checar(FinalizadosKds.Rotulo(FinalizadosKds.Estado(Ticket("o-1405"))) == "SAIU", "FK-149a despachado pelo Gestor da fila: SAIU");
            Kds.AplicarStatusDaNuvem("o-1405", "cancelado", null);
            checar(FinalizadosKds.Rotulo(FinalizadosKds.Estado(Ticket("o-1405"))) == "CANCELADO",
                "FK-149 a reconciliação diz 'cancelado' depois: CANCELADO");

            // balcão: cliente levou, depois a venda foi cancelada (era RETIRADO)
            var idBalcao = Guid.NewGuid().ToString();
            cx.Execute(@"INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json, status, criado_em)
                         VALUES (@id, 'balcao', 'venda-77', '77', NULL, @j, 'pronto', @c)",
                new { id = idBalcao, j = Itens, c = meioDia.ToString("o") });
            Kds.Entregar(idBalcao);
            Kds.CancelarPorVenda("venda-77");
            checar(FinalizadosKds.Rotulo(FinalizadosKds.Estado(Ticket("venda-77"))) == "CANCELADO",
                "FK-150 balcão: o cliente levou e depois a venda foi cancelada: CANCELADO (era RETIRADO)");

            // pronto cancelado no iFood continua no quadro, mas já sai como CANCELADO quando sair
            Kds.DoDelivery("o-1406", "1406", "Cliente", donut, chegadaReal: meioDia);
            Kds.PromoverProntoDelivery("o-1406");
            Kds.CancelarDelivery("o-1406");
            var p1 = Ler("o-1406");
            checar(p1.Status == Kds.Pronto && p1.Certo == 1, "FK-151 pronto cancelado no iFood fica no quadro (regra de sempre), já marcado cancelado");

            // cardápio próprio: 'cancelado' não prova cancelamento
            Kds.DoDelivery("cd-1", "CD-321", "Cliente", donut, chegadaReal: meioDia);
            Kds.CancelarDelivery("cd-1");
            checar(Ler("cd-1").Certo == 0 && FinalizadosKds.Rotulo(FinalizadosKds.Estado(Ticket("cd-1"))) == "SAIU DO QUADRO",
                "FK-152 cardápio próprio 'cancelado': nem motivo nem cancelamento certo (SAIU DO QUADRO)");

            // ── a resposta da nuvem (CanceladosAsync) gravada ──
            Kds.DoDelivery("o-2001", "2001", "Cliente", donut, chegadaReal: meioDia);
            Kds.DespacharDelivery("o-2001");
            var quando = meioDia.AddMinutes(40);
            var mudou = FinalizadosKds.AplicarCancelamentos(new (string, DateTime?)[] { ("o-2001", quando), ("cd-1", quando), ("nao-existe", quando), ("", null) });
            var r2001 = Ler("o-2001");
            checar(mudou == 1 && r2001.Certo == 1 && r2001.CanceladoEm is { } ce && DateTime.Parse(ce) == quando && Ler("cd-1").Certo == 0,
                $"FK-153 a nuvem provou o cancelamento: gravado com a hora do evento; o cardápio fica de fora ({mudou} mudou)");
            checar(FinalizadosKds.AplicarCancelamentos(new (string, DateTime?)[] { ("o-2001", quando.AddMinutes(5)) }) == 0
                   && DateTime.Parse(Ler("o-2001").CanceladoEm!) == quando,
                "FK-154 repetir não muda nada (nem a hora)");
            Kds.DoDelivery("o-2002", "2002", "Cliente", donut, chegadaReal: meioDia);
            Kds.DespacharDelivery("o-2002");
            FinalizadosKds.AplicarNuvem(new (string, string, string?)[] { ("o-2002", "cancelado", null) });
            var semHora = Ler("o-2002");
            FinalizadosKds.AplicarCancelamentos(new (string, DateTime?)[] { ("o-2002", quando) });
            checar(semHora.Certo == 1 && semHora.CanceladoEm is null && Ler("o-2002").CanceladoEm is not null,
                "FK-155 'cancelado' da pdv_kds_status marca sem hora; o evento depois traz a hora");

            // ── o vigia: os que saíram nas últimas 12 h ──
            var recentes = FinalizadosKds.RecentesParaConferir(DateTime.Now);
            checar(recentes.Contains("o-1405") == false && !recentes.Contains("cd-1") && !recentes.Contains("o-2001"),
                "FK-156 o vigia não pergunta de novo quem já está cancelado nem o cardápio");
            Kds.DoDelivery("o-3001", "3001", "Cliente", donut, chegadaReal: meioDia);
            Kds.DespacharDelivery("o-3001");
            var velho = Guid.NewGuid().ToString();
            cx.Execute(@"INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json, status, criado_em, encerrado_em, encerrado_motivo)
                         VALUES (@id, 'ifood', 'o-velho', '3002', 'C', @j, 'entregue', @c, @c, 'gestor')",
                new { id = velho, j = Itens, c = DateTime.Now.AddHours(-20).ToString("o") });
            recentes = FinalizadosKds.RecentesParaConferir(DateTime.Now);
            checar(recentes.Contains("o-3001") && !recentes.Contains("o-velho"),
                "FK-157 o vigia pergunta pelos que saíram nas últimas 12 h (o evento só fica 12 h na pdv_kds_entrega)");

            // ── a nuvem de mentira: sem a RPC nova cai no último evento; com ela, usa ela ──
            using var fake = new FakePostgrest(4766);
            var nuvem = new Nuvem(fake.Url);
            checar(nuvem.EntrarAsync("kds@teste.com", "x").GetAwaiter().GetResult(), "FK-158a nuvem fake autentica");
            fake.KdsEntrega = """[{"order_id":"o-3001","code":"CANCELLED","em":"2026-10-14T17:20:00+00:00"},{"order_id":"o-x","code":"DISPATCHED","em":null}]""";
            var r = nuvem.CanceladosAsync(new[] { "o-3001", "o-x" }).GetAwaiter().GetResult();
            checar(r is { Count: 1 } && r[0].OrderId == "o-3001" && r[0].Em == DateTimeOffset.Parse("2026-10-14T17:20:00+00:00").LocalDateTime
                   && fake.ChamadasPorRpc.GetValueOrDefault("pdv_kds_cancelados") == 1 && fake.ChamadasPorRpc.GetValueOrDefault("pdv_kds_entrega") == 1,
                "FK-158 servidor de hoje (sem pdv_kds_cancelados, 404): cai no último evento de entrega, só o CANCELLED, com a hora do evento");
            nuvem.CanceladosAsync(new[] { "o-3001" }).GetAwaiter().GetResult();
            checar(fake.ChamadasPorRpc.GetValueOrDefault("pdv_kds_cancelados") == 1,
                "FK-159 o 404 da RPC nova fica lembrado: não pergunta de novo a cada lista");
            var nuvem2 = new Nuvem(fake.Url);
            nuvem2.EntrarAsync("kds@teste.com", "x").GetAwaiter().GetResult();
            fake.KdsCancelados = """[{"order_id":"o-velho","cancelado_em":null},{"order_id":"o-3001","cancelado_em":"2026-10-14T17:20:00+00:00"},{"cancelado_em":null}]""";
            var r2 = nuvem2.CanceladosAsync(new[] { "o-velho", "o-3001" }).GetAwaiter().GetResult();
            checar(r2 is { Count: 2 } && r2[0] == ("o-velho", null) && r2[1].Em is not null
                   && fake.ChamadasPorRpc.GetValueOrDefault("pdv_kds_entrega") == 2,
                "FK-160 com a RPC nova no servidor: usa ela (pega até o de 20 h atrás, sem hora), sem tocar na de entrega");
            checar(Nuvem.LerCancelados("{lixo") is null && Nuvem.LerCancelados("{\"a\":1}") is null && Nuvem.LerCancelados("[]") is { Count: 0 },
                "FK-161 o parser: JSON ilegível ou objeto é falha (null); lista vazia é 'nenhum cancelado'");
            checar(new Nuvem("http://127.0.0.1:9").CanceladosAsync(new[] { "o-1" }).GetAwaiter().GetResult() is null,
                "FK-162 sem nuvem (sem sessão, endereço morto): null, e o caixa mantém o que sabia");

            // o vigia na puxada, de verdade: grava, e respeita os 2 min
            fake.KdsCancelados = null;
            var nuvem3 = new Nuvem(fake.Url);
            nuvem3.EntrarAsync("kds@teste.com", "x").GetAwaiter().GetResult();
            FinalizadosKds.ZerarVigia();
            var antes = fake.ChamadasPorRpc.GetValueOrDefault("pdv_kds_entrega");
            FinalizadosKds.ConferirRecentesAsync(nuvem3, DateTime.Now).GetAwaiter().GetResult();
            checar(Ler("o-3001").Certo == 1 && FinalizadosKds.Rotulo(FinalizadosKds.Estado(Ticket("o-3001"))) == "CANCELADO",
                "FK-163 o vigia da puxada achou o cancelamento depois do despacho e gravou: a lista de amanhã diz CANCELADO");
            var depoisVigia = fake.ChamadasPorRpc.GetValueOrDefault("pdv_kds_entrega");
            FinalizadosKds.ConferirRecentesAsync(nuvem3, DateTime.Now.AddSeconds(30)).GetAwaiter().GetResult();
            checar(depoisVigia == antes + 1 && fake.ChamadasPorRpc.GetValueOrDefault("pdv_kds_entrega") == depoisVigia,
                "FK-164 e não pergunta de novo antes de 2 min (a puxada roda a cada 10 s)");
            FinalizadosKds.ZerarVigia();

            // lotes de 100, e lote que falha não apaga o que respondeu
            var pedidos = new List<IReadOnlyList<string>>();
            var ids = Enumerable.Range(0, 250).Select(i => "lote-" + i).ToList();
            var mud = FinalizadosKds.ConferirCancelamentosAsync(lote =>
            {
                pedidos.Add(lote);
                return Task.FromResult<List<(string, DateTime?)>?>(pedidos.Count == 2 ? null : new());
            }, ids).GetAwaiter().GetResult();
            checar(pedidos.Count == 3 && pedidos.All(p => p.Count <= 100) && mud is null,
                "FK-165 a lista da tela vai em lotes de 100; um lote sem resposta faz a barra dizer 'Sem internet'");

            // ── ACHADO 5 no banco: a virada às 05:00 ──
            var amanha0015 = FinalizadosKds.InicioDoDia(agora).AddDays(1).AddMinutes(-285);   // 00:15 do dia seguinte
            var noite = FinalizadosKds.InicioDoDia(agora).AddHours(18).AddMinutes(40);        // 23:40 de hoje
            cx.Execute(@"INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json, status, criado_em, encerrado_em, encerrado_motivo)
                         VALUES (@id, 'ifood', 'o-noite', '6001', 'C', @j, 'entregue', @c, @e, 'gestor')",
                new { id = Guid.NewGuid().ToString(), j = Itens, c = noite.ToString("o"), e = noite.AddMinutes(30).ToString("o") });
            checar(FinalizadosKds.Ler(PeriodoFinalizados.Hoje, amanha0015).Any(t => t.RefId == "o-noite")
                   && !FinalizadosKds.Ler(PeriodoFinalizados.Ontem, amanha0015).Any(t => t.RefId == "o-noite")
                   && FinalizadosKds.ContarHoje(amanha0015) == FinalizadosKds.Ler(PeriodoFinalizados.Hoje, amanha0015).Count,
                "FK-166 no banco: chegou 23:40, às 00:15 está em Hoje e conta no 'Saíram hoje'");

            // ── ACHADO 8: a loja ──
            cx.Execute("DELETE FROM kds_ticket");
            cx.Execute(@"INSERT INTO terminal (id, terminal_uuid, loja_id, loja_nome, cnpj, serie_nfce, ambiente, criado_em)
                         VALUES (1, 't', 'American Day Castelo', 'American Day Castelo', '0', 1, 2, @a)", new { a = agora.ToString("o") });
            Kds.DoDelivery("o-castelo", "4100", "Cliente", donut, chegadaReal: meioDia);
            Kds.DespacharDelivery("o-castelo");
            cx.Execute("UPDATE terminal SET loja_nome = 'American Day Savassi' WHERE id = 1");
            Kds.DoDelivery("o-savassi", "4101", "Cliente", donut, chegadaReal: meioDia);
            Kds.DespacharDelivery("o-savassi");
            var lojas = cx.Query<(string RefId, string? Loja)>("SELECT ref_id, loja FROM kds_ticket").ToDictionary(x => x.RefId, x => x.Loja);
            checar(lojas["o-castelo"] == "American Day Castelo" && lojas["o-savassi"] == "American Day Savassi",
                "FK-167 cada pedido guarda a loja do terminal quando entrou (gatilho no INSERT)");
            var hoje = FinalizadosKds.Ler(PeriodoFinalizados.Hoje, agora);
            checar(hoje.Count == 1 && hoje[0].RefId == "o-savassi" && FinalizadosKds.ContarHoje(agora) == 1
                   && FinalizadosKds.PorNumero("4100") is null && FinalizadosKds.PorNumero("4101") is not null,
                "FK-168 caixa reconfigurado do Castelo para a Savassi: a lista, a contagem e o detalhe não mostram os do Castelo");
            cx.Execute("UPDATE kds_ticket SET loja = NULL WHERE ref_id = 'o-castelo'");
            Banco.Migrar(arquivo);
            checar(cx.ExecuteScalar<string>("SELECT loja FROM kds_ticket WHERE ref_id = 'o-castelo'") == "American Day Savassi",
                "FK-169 pedido de antes desta versão (sem loja) ganha a loja de agora, uma vez, no boot");
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }

    // ── A TELA DE VERDADE (WPF, 1024x768) ───────────────────────────────────

    private static void NaTela(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"kds_finalizados_tela_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        Exception? erro = null;
        try
        {
            Banco.Migrar(arquivo);
            using (var cx = Banco.Abrir())
            {
                Isolamento.SemNuvem(cx);
                // a comanda nunca sai de verdade daqui; o botão do detalhe aparece (Perguntar)
                Vendas.GravarConfig(cx, Impressoes.Chave(Impressoes.Comanda), Impressoes.Texto(PoliticaImpressao.Perguntar));
                var agora = DateTime.Now;
                var meioDia = FinalizadosKds.InicioDoDia(agora).AddHours(7);
                void Saiu(string numero, DateTime chegou, string motivo, bool cancelado = false, string status = Kds.Entregue)
                    => cx.Execute(@"INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json, status, criado_em,
                                                            encerrado_em, encerrado_motivo, cancelado_certo, cancelado_em)
                                    VALUES (@id, 'ifood', @r, @n, 'Cliente Teste', @j, @s, @c, @e, @m, @cc, @ce)",
                        new { id = "t-" + numero, r = "ref-" + numero, n = numero, j = Itens, s = status, c = chegou.ToString("o"),
                              e = chegou.AddMinutes(30).ToString("o"), m = motivo, cc = cancelado ? 1 : 0,
                              ce = cancelado ? chegou.AddMinutes(50).ToString("o") : null });
                Saiu("4719", meioDia, FinalizadosKds.MotivoGestor);
                Saiu("4690", meioDia.AddMinutes(-20), FinalizadosKds.MotivoIfoodCancelou, cancelado: true, status: Kds.Cancelado);
                Saiu("4400", meioDia.AddDays(-1), FinalizadosKds.MotivoGestor);
            }
            HostWpf.Executar(() => Passos(checar));
        }
        catch (Exception ex) { erro = ex; }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
        checar(erro is null, "FK-170 a tela do KDS subiu a 1024x768 e os passos rodaram (" + (erro?.ToString() ?? "ok") + ")");
    }

    private static void Passos(Action<bool, string> checar)
    {
        // O sino do realtime NÃO pode abrir conexão com a nuvem de verdade a partir da suíte:
        // um sino parado, apontado para o endereço morto, toma o lugar dele.
        var campoSino = typeof(Pdv.Servicos).GetField("_sino", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var sinoAntes = campoSino.GetValue(null);
        campoSino.SetValue(null, new RealtimeKds(Isolamento.UrlMorta, "x", () => Task.FromResult<string?>(null), "Loja Teste"));
        var host = new Window
        {
            Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0,
        };
        try
        {
            host.Show();
            var tela = new Pdv.Telas.Kds("Loja Teste")
            {
                ConsultaNuvem = ids => Task.FromResult<List<(string OrderId, string Status, string? PreparoAte)>?>(new()),
                ConsultaCancelados = ids => Task.FromResult<List<(string OrderId, DateTime? Em)>?>(new()),
            };
            host.Content = tela;
            host.UpdateLayout();
            T Nome<T>(string n) where T : class => (T)tela.FindName(n);

            // ── o cabeçalho a 1024 com busca (achados 12 e 14) ──
            tela.Buscar("4719");
            host.UpdateLayout();
            var titulo = Nome<TextBlock>("TxtTituloPedidos");
            var esquerda = Nome<Grid>("CabecalhoEsquerda");
            var status = Nome<TextBlock>("TxtStatus");
            var limpar = Nome<Button>("BtnLimparBusca");
            double Largura(string texto, Control c) => new FormattedText(texto, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(c.FontFamily, c.FontStyle, c.FontWeight, c.FontStretch), c.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(c).PixelsPerDip).WidthIncludingTrailingWhitespace;
            double LarguraTb(TextBlock tb) => new FormattedText(tb.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(tb).PixelsPerDip).WidthIncludingTrailingWhitespace;
            // A largura VISÍVEL do lado esquerdo é a da coluna * do cabeçalho (o filho maior que ela é recortado).
            var visivel = ((Grid)esquerda.Parent).ColumnDefinitions[0].ActualWidth;
            var precisaTitulo = esquerda.ColumnDefinitions[0].ActualWidth + titulo.Margin.Left + LarguraTb(titulo);
            checar(visivel + 0.5 >= precisaTitulo,
                $"FK-171 a 1024 com busca, o '🍩 PEDIDOS' cabe inteiro ({visivel:0.0} px visíveis para {precisaTitulo:0.0})");
            status.Text = "Conectado · 23:59:59 · 3 pedidos novos";
            host.UpdateLayout();
            var sobra = visivel - esquerda.ColumnDefinitions[0].ActualWidth - esquerda.ColumnDefinitions[1].ActualWidth;
            // O espaço que o WPF dá ao status (margens incluídas) tem de caber no que sobra da coluna visível:
            // no StackPanel de antes ele ganhava a largura do texto inteiro e o recorte deixava meia letra.
            var vaga = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(status).Width;
            checar(vaga <= Math.Max(0, sobra) + 0.5,
                $"FK-172 a linha de status encolhe dentro da coluna (reticência), não vaza meia letra ({vaga:0.0} de {sobra:0.0} px)");
            status.Text = "";
            checar(limpar.IsVisible && limpar.ActualWidth >= 40 && limpar.ActualHeight >= 40,
                $"FK-173 o ✕ da busca tem pelo menos 40x40 px ({limpar.ActualWidth:0}x{limpar.ActualHeight:0})");
            checar(Nome<Border>("FaixaBusca").IsVisible && Nome<TextBlock>("TxtFaixaBusca").Text == "No quadro: nenhum. Já saíram: 1.",
                "FK-174 a faixa da busca diz quantos estão no quadro e quantos já saíram: " + Nome<TextBlock>("TxtFaixaBusca").Text);
            tela.Buscar("");
            host.UpdateLayout();
            var campo = Nome<TextBox>("TxtBusca");
            var visivelVazio = ((Grid)esquerda.Parent).ColumnDefinitions[0].ActualWidth;
            var utilCampo = campo.ActualWidth - campo.Padding.Left - campo.Padding.Right;
            checar(visivelVazio + 0.5 >= precisaTitulo && Largura(FinalizadosKds.DicaBuscaQuadro, campo) <= utilCampo + 0.5,
                $"FK-184 sem busca, a 1024: 'PEDIDOS' inteiro e a dica '{FinalizadosKds.DicaBuscaQuadro}' cabe no campo ({utilCampo:0.0} px)");

            // ── a lista, a faixa de pedido novo (achado 3) ──
            tela.AbrirFinalizados(PeriodoFinalizados.Hoje);
            host.UpdateLayout();
            var novos = Nome<Button>("BtnNovosNaLista");
            checar(!novos.IsVisible && Nome<Grid>("Quadro").Visibility == Visibility.Collapsed,
                "FK-175 a lista aberta esconde as três colunas, sem faixa de pedido novo");
            tela.AvisarNovosNaLista(1);
            tela.AvisarNovosNaLista(1);
            host.UpdateLayout();
            checar(novos.IsVisible && Nome<TextBlock>("TxtNovosNaLista").Text == FinalizadosKds.TextoPedidosNovos(2) && novos.ActualHeight >= 52,
                "FK-176 chegaram 2 pedidos com a lista aberta: a faixa aparece e conta os dois (" + Nome<TextBlock>("TxtNovosNaLista").Text + ")");
            novos.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            host.UpdateLayout();
            checar(!novos.IsVisible && Nome<Grid>("Quadro").Visibility == Visibility.Visible && Nome<Border>("PainelFinalizados").Visibility == Visibility.Collapsed,
                "FK-177 o toque na faixa volta ao quadro, e a faixa some");

            // ── a etiqueta caída na busca com a lista aberta (achado 6a) ──
            tela.AbrirFinalizados(PeriodoFinalizados.Hoje);
            var busca = Nome<TextBox>("TxtBusca");
            busca.Text = "ADKD";
            host.UpdateLayout();
            var lista = Nome<StackPanel>("ListaFinalizados");
            bool TemVazio() => lista.Children.OfType<TextBlock>().Any(t => t.Text.StartsWith("Nada com", StringComparison.Ordinal));
            var filtrou = TemVazio();
            busca.Text = "ADKDS:ref-qualquer";
            host.UpdateLayout();
            checar(filtrou && !TemVazio() && lista.Children.OfType<Button>().Count() == 2,
                "FK-178 etiqueta lida na busca com a lista aberta: some o 'Nada com ADKD' e voltam os pedidos de hoje");
            busca.Text = "";

            // ── os botões do detalhe de quem saiu (achados 2, 7 e 11) ──
            Button? Botao(string nome) => FotoKds.Achar<Button>(Nome<ContentControl>("PainelDetalhe"), nome);
            tela.AbrirDetalhe("4719");
            host.UpdateLayout();
            var comanda = Botao("BtnComandaDeNovo");
            checar(comanda is not null && Botao("BtnFaleComIfood") is not null,
                "FK-179 detalhe de quem saiu hoje: 'Comanda de novo' e 'Fale com o iFood'");
            if (comanda is not null)
            {
                var util = comanda.ActualWidth - comanda.Padding.Left - comanda.Padding.Right
                         - comanda.BorderThickness.Left - comanda.BorderThickness.Right;
                var textos = new[] { "🖨 " + FinalizadosKds.BotaoComandaDeNovo, FinalizadosKds.TextoComandaSaiu, FinalizadosKds.TextoComandaNaoSaiu };
                var grandes = textos.Where(t => Largura(t, comanda) > util + 0.5).ToList();
                checar(grandes.Count == 0,
                    $"FK-180 os três textos do botão cabem nele a 1024 ({util:0.0} px úteis)" + (grandes.Count == 0 ? "" : ": não cabem " + string.Join(" | ", grandes)));
                checar(Largura("Não saiu. Confira o papel", comanda) > util,
                    "FK-181 e o texto antigo da falha NÃO caberia (a régua acima pega o corte)");
            }
            tela.AbrirDetalhe("4690");
            host.UpdateLayout();
            checar(Botao("BtnComandaDeNovo") is null,
                "FK-182 detalhe de pedido CANCELADO: sem 'Comanda de novo' (a cozinha produziria o que ninguém vai buscar)");
            tela.AbrirDetalhe("4400");
            host.UpdateLayout();
            checar(Botao("BtnComandaDeNovo") is not null && Botao("BtnFaleComIfood") is null,
                "FK-183 detalhe de quem saiu ONTEM: comanda sim, 'Fale com o iFood' não (o número de 4 dígitos acharia outro pedido)");
        }
        finally
        {
            host.Close();
            campoSino.SetValue(null, sinoAntes);
        }
    }

    private static string Trecho(string fonte, string inicio, string fim)
    {
        var i = fonte.IndexOf(inicio, StringComparison.Ordinal);
        if (i < 0) return "";
        var j = fonte.IndexOf(fim, i + inicio.Length, StringComparison.Ordinal);
        return j < 0 ? fonte[i..] : fonte[i..(j + fim.Length)];
    }

    private static string? Fonte(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj")))
            {
                var c = Path.Combine(dir.FullName, relativo);
                return File.Exists(c) ? File.ReadAllText(c).Replace("\r\n", "\n") : null;
            }
        return null;
    }

    private static void NoBanco(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"kds_finalizados_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir();
            var colunas = cx.Query<string>("SELECT name FROM pragma_table_info('kds_ticket')").ToList();
            checar(colunas.Contains("encerrado_em") && colunas.Contains("encerrado_motivo"),
                "FK-51 o banco do caixa ganhou encerrado_em e encerrado_motivo");
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_kds_criado'") == 1,
                "FK-52 índice em criado_em (a contagem do botão roda a cada 10 s)");
            Banco.Migrar(arquivo);
            checar(true, "FK-53 migrar de novo num banco que já tem as colunas não quebra o boot");

            var agora = DateTime.Now;
            // Os pedidos "de hoje" chegam ao meio-dia do dia de OPERAÇÃO: a bateria rodada perto da
            // virada (05:00) ou da meia-noite não pode empurrar um deles para ontem. Só os que
            // precisam EXPIRAR contam do relógio.
            var meioDia = FinalizadosKds.InicioDoDia(agora).AddHours(7);
            void Inserir(string id, string origem, string numero, string status, DateTime criado, string? motivo = null,
                         string? encerrado = null, bool retirada = false)
                => cx.Execute(
                    @"INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json, status, criado_em,
                                              encerrado_motivo, encerrado_em, retirada)
                      VALUES (@id, @o, @r, @n, 'Cliente', @j, @s, @c, @m, @e, @ret)",
                    new { id, o = origem, r = "ref-" + id, n = numero, j = Itens, s = status, c = criado.ToString("o"),
                          m = motivo, e = encerrado, ret = retirada ? 1 : 0 });
            (string Status, string? Motivo, string? Em, string? Entregue, long Certo) Ler(string id)
            {
                var r = cx.QueryFirst("SELECT status, encerrado_motivo, encerrado_em, entregue_em, cancelado_certo FROM kds_ticket WHERE id = @id", new { id });
                return ((string)r.status, r.encerrado_motivo as string, r.encerrado_em as string, r.entregue_em as string, (long)r.cancelado_certo);
            }

            // balcão: "cliente levou"
            Inserir("b1", "balcao", "77", Kds.Pronto, meioDia.AddMinutes(-20));
            checar(Kds.Entregar("b1"), "FK-54a o toque de 'cliente levou' continua tirando do quadro");
            var b1 = Ler("b1");
            checar(b1.Status == Kds.Entregue && b1.Motivo == FinalizadosKds.MotivoClienteLevou && b1.Em is not null && b1.Entregue is not null,
                "FK-54 'cliente levou' anota hora (encerrado_em e entregue_em) e motivo");
            checar(!Kds.Entregar("b1"), "FK-55 tocar de novo não reescreve nada (a transição exige PRONTO)");

            // Gestor despachou: do PRONTO vira entregue, da FILA vira cancelado; os dois com motivo gestor
            Inserir("g1", "ifood", "5001", Kds.Pronto, meioDia.AddMinutes(-30));
            Inserir("g2", "ifood", "5002", Kds.Recebido, meioDia.AddMinutes(-30));
            Kds.DespacharDelivery("ref-g1");
            Kds.DespacharDelivery("ref-g2");
            var g1 = Ler("g1"); var g2 = Ler("g2");
            checar(g1.Status == Kds.Entregue && g1.Motivo == FinalizadosKds.MotivoGestor && g1.Em is not null
                   && g2.Status == Kds.Cancelado && g2.Motivo == FinalizadosKds.MotivoGestor && g2.Em is not null,
                "FK-56 o Gestor despachou: pronto vira entregue, fila vira 'cancelado', os dois com motivo gestor e hora");
            Thread.Sleep(15);
            Kds.DespacharDelivery("ref-g2");
            Kds.CancelarDelivery("ref-g2");   // o iFood cancelou DEPOIS de o Gestor despachar
            var g2b = Ler("g2");
            checar(g2b.Em == g2.Em && g2b.Motivo == FinalizadosKds.MotivoGestor && g2b.Certo == 1,
                "FK-57 o feed repete: a hora e o motivo da SAÍDA ficam; o cancelamento depois dela é anotado à parte");

            // iFood cancelou um pedido no quadro
            Inserir("c1", "ifood", "5003", Kds.Preparando, meioDia.AddMinutes(-10));
            Kds.CancelarDelivery("ref-c1");
            var c1 = Ler("c1");
            checar(c1.Status == Kds.Cancelado && c1.Motivo == FinalizadosKds.MotivoIfoodCancelou && c1.Em is not null && c1.Certo == 1,
                "FK-58 o iFood cancelou: motivo ifood_cancelou, hora e cancelamento certo");
            Thread.Sleep(15);
            Kds.CancelarDelivery("ref-c1");
            checar(Ler("c1").Em == c1.Em, "FK-59 o cancelamento repetido pelo feed não mexe na hora");

            // cardápio próprio: 'cancelado' da nuvem não prova cancelamento
            Inserir("cd1", "ifood", "CD-321", Kds.Recebido, meioDia.AddMinutes(-10));
            Kds.CancelarDelivery("ref-cd1");
            checar(Ler("cd1").Motivo == FinalizadosKds.MotivoCardapioLargou && Ler("cd1").Certo == 0,
                "FK-60 cardápio próprio 'cancelado': motivo 'cardápio largou' (a tela diz SAIU DO QUADRO)");

            // PRONTO cancelado no iFood: fica no quadro (regra de sempre) e não ganha saída
            Inserir("p1", "ifood", "5004", Kds.Pronto, agora.AddMinutes(-10));
            Kds.CancelarDelivery("ref-p1");
            var p1 = Ler("p1");
            checar(p1.Status == Kds.Pronto && p1.Motivo is null && p1.Em is null,
                "FK-61 pronto cancelado no iFood continua no quadro, sem saída anotada (regra de sempre)");

            // expirou: motivo 'expirou'; um cancelamento certo depois troca o motivo, não a hora
            Inserir("e1", "ifood", "5005", Kds.Recebido, agora.AddHours(-5));
            Inserir("e2", "ifood", "5006", Kds.Pronto, agora.AddHours(-13));
            Kds.SincronizarDelivery(Array.Empty<PedidoDelivery>());
            var e1 = Ler("e1"); var e2 = Ler("e2");
            checar(e1.Status == Kds.Cancelado && e1.Motivo == FinalizadosKds.MotivoExpirou && e1.Em is not null
                   && e2.Status == Kds.Cancelado && e2.Motivo == FinalizadosKds.MotivoExpirou,
                "FK-62 expirou (4 h na fila, 12 h pronto): motivo 'expirou' e hora");
            Kds.CancelarDelivery("ref-e1");
            var e1b = Ler("e1");
            checar(e1b.Motivo == FinalizadosKds.MotivoIfoodCancelou && e1b.Em == e1.Em && e1b.Certo == 1,
                "FK-63 o iFood cancelou o que expirou: o motivo certo ganha do 'não sei', a hora fica");

            // venda de balcão cancelada
            Inserir("v1", "balcao", "88", Kds.Recebido, meioDia.AddMinutes(-3));
            cx.Execute("UPDATE kds_ticket SET ref_id = 'venda-88' WHERE id = 'v1'");
            Kds.CancelarPorVenda("venda-88");
            var v1 = Ler("v1");
            checar(v1.Status == Kds.Cancelado && v1.Motivo == FinalizadosKds.MotivoVendaCancelada && v1.Em is not null && v1.Certo == 1,
                "FK-64 venda cancelada: motivo venda_cancelada, hora e cancelamento certo");

            // ── a lista lida do banco ──
            Inserir("old", "ifood", "4001", Kds.Cancelado, meioDia.AddMinutes(-50));           // antes desta versão
            Inserir("ontem", "ifood", "4002", Kds.Entregue, FinalizadosKds.InicioDoDia(agora).AddDays(-1).AddHours(7), FinalizadosKds.MotivoGestor);
            Inserir("velho", "ifood", "4003", Kds.Entregue, agora.Date.AddDays(-9), FinalizadosKds.MotivoGestor);
            var hoje = FinalizadosKds.Ler(PeriodoFinalizados.Hoje, agora);
            bool DeHoje(DateTime d) => FinalizadosKds.InicioDoDia(d) == FinalizadosKds.InicioDoDia(agora);
            var esperadoHoje = new[] { "b1", "g1", "g2", "c1", "cd1", "v1", "old" }
                .Concat(DeHoje(agora.AddHours(-5)) ? new[] { "e1" } : Array.Empty<string>())
                .Concat(DeHoje(agora.AddHours(-13)) ? new[] { "e2" } : Array.Empty<string>())
                .OrderBy(x => x).ToList();
            checar(hoje.Select(t => t.Id).OrderBy(x => x).SequenceEqual(esperadoHoje),
                "FK-65 a lista de hoje lê do banco só quem saiu e chegou hoje (o pronto continua no quadro): " + string.Join(",", hoje.Select(t => t.Id).OrderBy(x => x)));
            checar(FinalizadosKds.ContarHoje(agora) == esperadoHoje.Count, $"FK-66 'Saíram hoje' conta o mesmo ({FinalizadosKds.ContarHoje(agora)})");
            checar(FinalizadosKds.Ler(PeriodoFinalizados.Ontem, agora).Any(t => t.Id == "ontem")
                   && !FinalizadosKds.Ler(PeriodoFinalizados.SeteDias, agora).Any(t => t.Id == "velho"),
                "FK-67 ontem acha o de ontem; 7 dias não acha o de 9 dias atrás");
            var lidoG1 = hoje.Single(t => t.Id == "g1");
            var lidoG2 = hoje.Single(t => t.Id == "g2");
            checar(lidoG1.EncerradoMotivo == FinalizadosKds.MotivoGestor && lidoG1.EncerradoEm is not null
                   && FinalizadosKds.Rotulo(FinalizadosKds.Estado(lidoG1)) == "SAIU"
                   && lidoG2.CanceladoCerto && FinalizadosKds.Rotulo(FinalizadosKds.Estado(lidoG2)) == "CANCELADO",
                "FK-68 o ticket lido do banco traz hora, motivo e cancelamento: despachado pelo Gestor é SAIU; despachado e cancelado depois é CANCELADO");
            checar(FinalizadosKds.PorNumero("5001")?.Id == "g1" && FinalizadosKds.PorNumero("5004") is null,
                "FK-69 achar pelo número só entre os que saíram");

            // ── o reforço da nuvem ──
            var mudou = FinalizadosKds.AplicarNuvem(new (string, string, string?)[]
            {
                ("ref-old", "concluido", null),   // sem motivo: vira saiu
                ("ref-g2", "cancelado", null),    // motivo certo (gestor): fica; o cancelamento já estava anotado
                ("ref-e2", "concluido", null),    // expirou: vira saiu
                ("ref-cd1", "cancelado", null),   // cardápio: cancelado não prova
                ("ref-c1", "pronto", null),       // não prova nada
                ("ref-nao-existe", "concluido", null),
            });
            checar(mudou == 2 && Ler("old").Motivo == FinalizadosKds.MotivoNuvemConcluiu && Ler("e2").Motivo == FinalizadosKds.MotivoNuvemConcluiu
                   && Ler("g2").Motivo == FinalizadosKds.MotivoGestor && Ler("cd1").Motivo == FinalizadosKds.MotivoCardapioLargou
                   && Ler("c1").Motivo == FinalizadosKds.MotivoIfoodCancelou && Ler("cd1").Certo == 0,
                $"FK-75 a nuvem só troca o 'não sei' ({mudou} mudaram)");
            checar(Ler("old").Em is null, "FK-76 a nuvem não inventa hora de saída");

            // ── o bipe de verdade, no banco: continua marcando PRONTO ──
            Inserir("bp", "ifood", "6066", Kds.Preparando, agora.AddMinutes(-4));
            var r = BipeKds.Padrao().Processar("ref-bp", agora);
            checar(r.Desfecho == DesfechoBipe.Pronto && Ler("bp").Status == Kds.Pronto
                   && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo='kds_pronto' AND ref_id='ref-bp'") == 1,
                "FK-85 o bipe da etiqueta continua marcando PRONTO e avisando o iFood");
            var rg = BipeKds.Padrao().Processar("ref-g1", agora);
            checar(rg.Desfecho == DesfechoBipe.JaSaiu, "FK-86 bipe no banco de pedido despachado pelo Gestor: já saiu do quadro");
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }
}
