using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// OS PEDIDOS QUE JÁ SAÍRAM DO QUADRO (14/10/2026, pedido do dono na Savassi: "opção também de
/// procurar pedidos finalizados no KDS do PDV"). A lógica mora em Nucleo.FinalizadosKds; aqui:
///
///  FK-1..  período (hoje, ontem, 7 dias) pela chegada no dia local;
///  FK-10.. busca por número (só dígitos, aceita "CD-123") e por nome (sem maiúscula nem acento);
///  FK-20.. o rótulo de cada linha: 'cancelado' local NÃO vira CANCELADO na tela sem motivo certo;
///  FK-30.. ordem (mais novo primeiro pela saída) e teto de 300;
///  FK-40.. textos: uma linha, sem travessão, teto de 4 linhas;
///  FK-50.. cada saída do quadro anota hora e motivo, sem sobrescrever (SQLite de verdade);
///  FK-70.. o reforço da nuvem só troca o "não sei";
///  FK-80.. o bipe da etiqueta continua marcando PRONTO, e não chama de cancelado quem só saiu;
///  FK-90.. o detalhe aberto da lista: etapa própria e "Saiu às".
/// </summary>
public static class TestesFinalizadosKds
{
    private const string Itens = "[{\"Descricao\":\"Donut Homer\",\"Qtd\":2000,\"Observacao\":null},"
                               + "{\"Descricao\":\"Cookie Red Velvet\",\"Qtd\":1000,\"Observacao\":null}]";

    private static readonly DateTime Agora = new(2026, 10, 14, 15, 0, 0);

    private static Ticket T(string numero, string status, DateTime criado, string origem = "ifood",
                            bool retirada = false, DateTime? entregue = null, DateTime? encerrado = null,
                            string? motivo = null, string? cliente = null, DateTime? pronto = null, string? id = null)
        => new(id ?? "t-" + numero, origem, "ref-" + numero, numero, cliente, Itens, status, criado,
               null, pronto, Retirada: retirada, EntregueEm: entregue, EncerradoEm: encerrado, EncerradoMotivo: motivo);

    public static void Rodar(Action<bool, string> checar)
    {
        Periodo(checar);
        Busca(checar);
        Rotulos(checar);
        OrdemETeto(checar);
        Textos(checar);
        Nuvem(checar);
        Bipe(checar);
        Detalhe(checar);
        NoBanco(checar);
    }

    private static void Periodo(Action<bool, string> checar)
    {
        var hoje = Agora.Date;
        checar(FinalizadosKds.Janela(PeriodoFinalizados.Hoje, Agora) == (hoje, hoje.AddDays(1))
               && FinalizadosKds.Janela(PeriodoFinalizados.Ontem, Agora) == (hoje.AddDays(-1), hoje)
               && FinalizadosKds.Janela(PeriodoFinalizados.SeteDias, Agora) == (hoje.AddDays(-6), hoje.AddDays(1)),
            "FK-1 janelas: hoje, ontem, e hoje com os seis dias antes");
        var meiaNoite = T("1", Kds.Entregue, hoje);
        var antesDaMeiaNoite = T("2", Kds.Entregue, hoje.AddSeconds(-1));
        checar(FinalizadosKds.NoPeriodo(meiaNoite, PeriodoFinalizados.Hoje, Agora)
               && !FinalizadosKds.NoPeriodo(antesDaMeiaNoite, PeriodoFinalizados.Hoje, Agora)
               && FinalizadosKds.NoPeriodo(antesDaMeiaNoite, PeriodoFinalizados.Ontem, Agora),
            "FK-2 a virada do dia é pela CHEGADA local: 23:59:59 é ontem, 00:00 é hoje");
        checar(FinalizadosKds.NoPeriodo(T("3", Kds.Entregue, hoje.AddDays(-6)), PeriodoFinalizados.SeteDias, Agora)
               && !FinalizadosKds.NoPeriodo(T("4", Kds.Entregue, hoje.AddDays(-6).AddSeconds(-1)), PeriodoFinalizados.SeteDias, Agora)
               && FinalizadosKds.NoPeriodo(T("5", Kds.Entregue, hoje.AddHours(23)), PeriodoFinalizados.SeteDias, Agora),
            "FK-3 7 dias: de seis dias atrás até o fim de hoje; o sétimo dia para trás fica de fora");
        // um pedido chegou ontem às 23:50 e saiu hoje às 00:10: é de ONTEM (a cozinha lembra da chegada)
        var virou = T("6", Kds.Entregue, hoje.AddMinutes(-10), encerrado: hoje.AddMinutes(10));
        checar(FinalizadosKds.Montar(new[] { virou }, PeriodoFinalizados.Ontem, null, Agora).Total == 1
               && FinalizadosKds.Montar(new[] { virou }, PeriodoFinalizados.Hoje, null, Agora).Total == 0,
            "FK-4 chegou ontem e saiu depois da meia-noite: aparece em Ontem, não em Hoje");
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
        };
        var errados = tabela.Where(x => FinalizadosKds.Rotulo(FinalizadosKds.Estado(x.T)) != x.Esperado).ToList();
        checar(errados.Count == 0,
            "FK-20 a tabela de rótulos (" + tabela.Length + " casos)"
            + (errados.Count == 0 ? "" : ": erraram " + string.Join(", ", errados.Select(e => $"{e.Caso} deu {FinalizadosKds.Rotulo(FinalizadosKds.Estado(e.T))}"))));
        checar(FinalizadosKds.Estado(T("12", Kds.Cancelado, h)) != EstadoFinalizado.Cancelado,
            "FK-21 'cancelado' guardado sem motivo NUNCA vira CANCELADO na tela (57 de 60 eram concluídos)");
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
            .Select(i => T((1000 + i).ToString(), Kds.Entregue, Agora.Date.AddMinutes(i), encerrado: Agora.Date.AddMinutes(i + 5)))
            .ToList();
        var cheio = FinalizadosKds.Montar(muitos, PeriodoFinalizados.Hoje, null, Agora.Date.AddHours(23));
        checar(cheio.Linhas.Count == 300 && cheio.Total == 350 && cheio.Cortada
               && cheio.Linhas[0].Numero == "#1349" && cheio.Linhas[^1].Numero == "#1050",
            "FK-32 teto de 300: mostra os 300 mais novos e sabe que havia 350");
        checar(FinalizadosKds.Montar(muitos, PeriodoFinalizados.Hoje, "1349", Agora.Date.AddHours(23)).Linhas.Single().Numero == "#1349",
            "FK-33 a busca procura nos 350, não só nos 300 da tela");
        checar(FinalizadosKds.Contar(muitos, PeriodoFinalizados.Hoje, "13", Agora.Date.AddHours(23))
               == FinalizadosKds.Montar(muitos, PeriodoFinalizados.Hoje, "13", Agora.Date.AddHours(23)).Total,
            "FK-34 o número do botão de período é o mesmo total da lista");
    }

    private static void Textos(Action<bool, string> checar)
    {
        var l1 = FinalizadosKds.Linha(T("4719", Kds.Entregue, Agora.Date.AddHours(14).AddMinutes(5),
                                        encerrado: Agora.Date.AddHours(14).AddMinutes(32), cliente: " Ana "), Agora);
        checar(l1.Chegou == "Chegou 14:05" && l1.Saiu == "Saiu 14:32" && l1.Cliente == "Ana" && l1.Rotulo == "SAIU",
            $"FK-40 linha de hoje: 'Chegou 14:05', 'Saiu 14:32' ({l1.Chegou} / {l1.Saiu})");
        var ontem = Agora.Date.AddDays(-1).AddHours(20);
        var l2 = FinalizadosKds.Linha(T("1", Kds.Cancelado, ontem, encerrado: ontem.AddMinutes(9), motivo: FinalizadosKds.MotivoIfoodCancelou), Agora);
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
               && FinalizadosKds.TextoVazio(PeriodoFinalizados.Hoje, "4719") == "Nada com \"4719\" hoje. Toque em 7 dias."
               && FinalizadosKds.TextoCortada() == "Mostrando os 300 mais novos. Procure pelo número ou nome.",
            "FK-46 os textos de lista vazia e cortada do desenho");
        checar(FinalizadosKds.TextoFaixaBusca("4719", 0, 1) == "No quadro: nenhum. Já saíram: 1."
               && FinalizadosKds.TextoFaixaBusca("47", 2, 0) == "No quadro: 2. Já saíram: nenhum.",
            "FK-47 a faixa da busca no quadro: 'No quadro: nenhum. Já saíram: 1.'");
        checar(FinalizadosKds.TextoFaixaBusca("ana", 12, 3).Contains("só por número") && FinalizadosKds.TextoFaixaBusca("ana", 12, 3).EndsWith(": 3."),
            "FK-48 busca por nome no quadro: a faixa diz que o quadro procura só por número e quantos saíram com o nome");

        var todos = new List<string>
        {
            FinalizadosKds.BotaoSairamHoje(1150), FinalizadosKds.BotaoVoltarAoQuadro, FinalizadosKds.BotaoVerOsQueSairam,
            FinalizadosKds.TextoGuardado, FinalizadosKds.TextoSemInternet, FinalizadosKds.TextoEtiquetaNaBusca,
            FinalizadosKds.BotaoComandaDeNovo, FinalizadosKds.TextoComandaSaiu, FinalizadosKds.TextoComandaNaoSaiu,
            FinalizadosKds.TextoCortada(), FinalizadosKds.DicaBuscaLista, FinalizadosKds.DicaBuscaQuadro,
            FinalizadosKds.TextoFaixaBusca("ana maria da silva", 1150, 1150), FinalizadosKds.TextoFaixaBusca("4719", 1150, 1150),
        };
        foreach (var p in Enum.GetValues<PeriodoFinalizados>())
        {
            todos.Add(FinalizadosKds.BotaoPeriodo(p, 1150));
            todos.Add(FinalizadosKds.TextoVazio(p, null));
            todos.Add(FinalizadosKds.TextoVazio(p, "4719"));
        }
        foreach (var e in Enum.GetValues<EstadoFinalizado>()) todos.Add(FinalizadosKds.Rotulo(e));
        var ruins = todos.Where(t => t.Contains('\n') || t.Contains('—') || t.Contains('–') || t.Length > 90 || t.Trim().Length == 0).ToList();
        checar(ruins.Count == 0, $"FK-49 todo texto da lista é uma linha curta, sem travessão ({todos.Count} textos)"
                                 + (ruins.Count == 0 ? "" : ": " + string.Join(" | ", ruins)));
        checar(todos.All(t => t.Split('\n').Length <= 4), "FK-50 nenhum passa de 4 linhas (teto do caixa)");
    }

    private static void Nuvem(Action<bool, string> checar)
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
            "FK-73 só os SAIU DO QUADRO do delivery vão para a nuvem: " + string.Join(",", FinalizadosKds.ParaConfirmarNaNuvem(lista)));
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
    }

    private static void Detalhe(Action<bool, string> checar)
    {
        var hoje14 = Agora.Date.AddHours(14);
        var saiu = DetalhePedido.De(T("4719", Kds.Entregue, hoje14, encerrado: hoje14.AddMinutes(32)), Agora);
        checar(saiu.Etapa == "SAIU" && saiu.SaiuAs == "Saiu às 14:32" && saiu.JaSaiu,
            $"FK-90 detalhe de quem saiu: etapa SAIU e 'Saiu às 14:32' (era 'NA FILA') ({saiu.Etapa} / {saiu.SaiuAs})");
        var cancelado = DetalhePedido.De(T("4720", Kds.Cancelado, hoje14, encerrado: hoje14.AddMinutes(9), motivo: FinalizadosKds.MotivoIfoodCancelou), Agora);
        checar(cancelado.Etapa == "CANCELADO" && cancelado.SaiuAs == "Cancelado às 14:09",
            "FK-91 detalhe de cancelado de verdade: etapa CANCELADO e 'Cancelado às 14:09'");
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
            // Os pedidos "de hoje" chegam ao meio-dia de hoje: a bateria rodada perto da meia-noite
            // não pode empurrar um deles para ontem. Só os que precisam EXPIRAR contam do relógio.
            var meioDia = agora.Date.AddHours(12);
            void Inserir(string id, string origem, string numero, string status, DateTime criado, string? motivo = null,
                         string? encerrado = null, bool retirada = false)
                => cx.Execute(
                    @"INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json, status, criado_em,
                                              encerrado_motivo, encerrado_em, retirada)
                      VALUES (@id, @o, @r, @n, 'Cliente', @j, @s, @c, @m, @e, @ret)",
                    new { id, o = origem, r = "ref-" + id, n = numero, j = Itens, s = status, c = criado.ToString("o"),
                          m = motivo, e = encerrado, ret = retirada ? 1 : 0 });
            (string Status, string? Motivo, string? Em, string? Entregue) Ler(string id)
            {
                var r = cx.QueryFirst("SELECT status, encerrado_motivo, encerrado_em, entregue_em FROM kds_ticket WHERE id = @id", new { id });
                return ((string)r.status, r.encerrado_motivo as string, r.encerrado_em as string, r.entregue_em as string);
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
            checar(g2b.Em == g2.Em && g2b.Motivo == FinalizadosKds.MotivoGestor,
                "FK-57 o feed repete: a hora não é reescrita e o motivo certo não é trocado");

            // iFood cancelou um pedido no quadro
            Inserir("c1", "ifood", "5003", Kds.Preparando, meioDia.AddMinutes(-10));
            Kds.CancelarDelivery("ref-c1");
            var c1 = Ler("c1");
            checar(c1.Status == Kds.Cancelado && c1.Motivo == FinalizadosKds.MotivoIfoodCancelou && c1.Em is not null,
                "FK-58 o iFood cancelou: motivo ifood_cancelou e hora");
            Thread.Sleep(15);
            Kds.CancelarDelivery("ref-c1");
            checar(Ler("c1").Em == c1.Em, "FK-59 o cancelamento repetido pelo feed não mexe na hora");

            // cardápio próprio: 'cancelado' da nuvem não prova cancelamento
            Inserir("cd1", "ifood", "CD-321", Kds.Recebido, meioDia.AddMinutes(-10));
            Kds.CancelarDelivery("ref-cd1");
            checar(Ler("cd1").Motivo == FinalizadosKds.MotivoCardapioLargou,
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
            checar(e1b.Motivo == FinalizadosKds.MotivoIfoodCancelou && e1b.Em == e1.Em,
                "FK-63 o iFood cancelou o que expirou: o motivo certo ganha do 'não sei', a hora fica");

            // venda de balcão cancelada
            Inserir("v1", "balcao", "88", Kds.Recebido, meioDia.AddMinutes(-3));
            cx.Execute("UPDATE kds_ticket SET ref_id = 'venda-88' WHERE id = 'v1'");
            Kds.CancelarPorVenda("venda-88");
            var v1 = Ler("v1");
            checar(v1.Status == Kds.Cancelado && v1.Motivo == FinalizadosKds.MotivoVendaCancelada && v1.Em is not null,
                "FK-64 venda cancelada: motivo venda_cancelada e hora");

            // ── a lista lida do banco ──
            Inserir("old", "ifood", "4001", Kds.Cancelado, meioDia.AddMinutes(-50));           // antes desta versão
            Inserir("ontem", "ifood", "4002", Kds.Entregue, agora.Date.AddDays(-1).AddHours(12), FinalizadosKds.MotivoGestor);
            Inserir("velho", "ifood", "4003", Kds.Entregue, agora.Date.AddDays(-9), FinalizadosKds.MotivoGestor);
            var hoje = FinalizadosKds.Ler(PeriodoFinalizados.Hoje, agora);
            var esperadoHoje = new[] { "b1", "g1", "g2", "c1", "cd1", "v1", "old" }
                .Concat(agora.AddHours(-5).Date == agora.Date ? new[] { "e1" } : Array.Empty<string>())
                .Concat(agora.AddHours(-13).Date == agora.Date ? new[] { "e2" } : Array.Empty<string>())
                .OrderBy(x => x).ToList();
            checar(hoje.Select(t => t.Id).OrderBy(x => x).SequenceEqual(esperadoHoje),
                "FK-65 a lista de hoje lê do banco só quem saiu e chegou hoje (o pronto continua no quadro): " + string.Join(",", hoje.Select(t => t.Id).OrderBy(x => x)));
            checar(FinalizadosKds.ContarHoje(agora) == esperadoHoje.Count, $"FK-66 'Saíram hoje' conta o mesmo ({FinalizadosKds.ContarHoje(agora)})");
            checar(FinalizadosKds.Ler(PeriodoFinalizados.Ontem, agora).Any(t => t.Id == "ontem")
                   && !FinalizadosKds.Ler(PeriodoFinalizados.SeteDias, agora).Any(t => t.Id == "velho"),
                "FK-67 ontem acha o de ontem; 7 dias não acha o de 9 dias atrás");
            var lidoG2 = hoje.Single(t => t.Id == "g2");
            checar(lidoG2.EncerradoMotivo == FinalizadosKds.MotivoGestor && lidoG2.EncerradoEm is not null
                   && FinalizadosKds.Rotulo(FinalizadosKds.Estado(lidoG2)) == "SAIU",
                "FK-68 o ticket lido do banco traz hora e motivo: o despachado pelo Gestor sai como SAIU, não CANCELADO");
            checar(FinalizadosKds.PorNumero("5001")?.Id == "g1" && FinalizadosKds.PorNumero("5004") is null,
                "FK-69 achar pelo número só entre os que saíram");

            // ── o reforço da nuvem ──
            var mudou = FinalizadosKds.AplicarNuvem(new (string, string, string?)[]
            {
                ("ref-old", "concluido", null),   // sem motivo: vira saiu
                ("ref-g2", "cancelado", null),    // motivo certo (gestor): fica
                ("ref-e2", "concluido", null),    // expirou: vira saiu
                ("ref-cd1", "cancelado", null),   // cardápio: cancelado não prova
                ("ref-c1", "pronto", null),       // não prova nada
                ("ref-nao-existe", "concluido", null),
            });
            checar(mudou == 2 && Ler("old").Motivo == FinalizadosKds.MotivoNuvemConcluiu && Ler("e2").Motivo == FinalizadosKds.MotivoNuvemConcluiu
                   && Ler("g2").Motivo == FinalizadosKds.MotivoGestor && Ler("cd1").Motivo == FinalizadosKds.MotivoCardapioLargou
                   && Ler("c1").Motivo == FinalizadosKds.MotivoIfoodCancelou,
                $"FK-75 a nuvem só troca o 'não sei' ({mudou} mudaram)");
            checar(Ler("old").Em is null, "FK-76 a nuvem não inventa hora de saída");

            // ── o bipe de verdade, no banco: continua marcando PRONTO ──
            Inserir("bp", "ifood", "6066", Kds.Preparando, agora.AddMinutes(-4));
            var r = BipeKds.Padrao().Processar("ref-bp", agora);
            checar(r.Desfecho == DesfechoBipe.Pronto && Ler("bp").Status == Kds.Pronto
                   && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo='kds_pronto' AND ref_id='ref-bp'") == 1,
                "FK-85 o bipe da etiqueta continua marcando PRONTO e avisando o iFood");
            var rg = BipeKds.Padrao().Processar("ref-g2", agora);
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
