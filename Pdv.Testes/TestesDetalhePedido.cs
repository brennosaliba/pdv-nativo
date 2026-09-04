using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A tela de DETALHE do pedido no KDS (Pdv.Nucleo/DetalhePedido), pedida pelo dono em
/// 04/09 olhando o quadro na loja: "um pop-up que quando clica ele visualiza o pedido,
/// com uma tela melhor, assim como é o iFood".
///
/// O que se prova aqui, sem WPF:
///   1. O MODELO: itens com o de dentro do combo e a observação, os rótulos ("Feito às
///      16:53", "Agendado para 18:30", "Retirada no balcão") e as seções da nuvem que
///      SOMEM quando o dado não veio e APARECEM quando veio.
///   2. A REGRA DO TOQUE: o cabeçalho do card abre o detalhe e NUNCA avança etapa, em
///      qualquer coluna e origem; só o rodapé avança. O dono já sofreu com toque
///      acidental, e o detalhe não podia virar mais um jeito de avançar.
///   3. O PARSER da RPC pdv_kds_pedido_detalhe: completo, nulos, lista vazia, lixo.
///   4. Sem preço e sem travessão em texto nenhum da tela.
/// </summary>
public static class TestesDetalhePedido
{
    public static void Rodar(Action<bool, string> checar)
    {
        Modelo(checar);
        Nuvem(checar);
        Toque(checar);
        Parser(checar);
        FonteDaTela(checar);
    }

    // O pedido da foto do dono: um combo com sabores e observação, um item simples
    // com quantidade 2 e um fracionado (peso), em FAZENDO com prazo do iFood.
    private static readonly DateTime Hoje = new(2026, 9, 4, 17, 0, 0);

    private static Ticket Pedido(string status = Kds.Preparando, string origem = "ifood",
                                 string numero = "5077", string? cliente = "Rafael Andrade")
    {
        var itens = new List<TicketItem>
        {
            new("Combo 1 Cookies - 4 unidades", 1000, "sem castanha",
                new[] { "Clássicos: 2x Cookie Tradicional", "Premium: 2x Cookie Pistache" }),
            new("Donut Ninho", 2000, null),
            new("Bolo de Cenoura", 1500, null),
        };
        return new Ticket("t1", origem, "order-1", numero, cliente, JsonSerializer.Serialize(itens), status,
            Hoje.AddMinutes(-7),
            status == Kds.Recebido ? null : Hoje.AddMinutes(-5),
            status == Kds.Pronto ? Hoje.AddMinutes(-1) : null,
            PreparoAte: origem == "ifood" ? Hoje.AddMinutes(13) : null);
    }

    private static void Modelo(Action<bool, string> checar)
    {
        var d = DetalhePedido.De(Pedido(), Hoje);

        checar(d.Numero == "#5077", "o número sai com o cerquilha: '#5077'");
        checar(d.Etapa == "FAZENDO", "a etapa tem o MESMO nome da coluna do quadro (FAZENDO)");
        checar(d.Canal == "via iFood", "origem ifood vira 'via iFood', como o badge do Gestor");
        checar(d.Cliente == "Rafael Andrade", "o nome do cliente sai como veio");
        checar(d.FeitoAs == "Feito às 16:53", $"a hora de chegada vira 'Feito às 16:53' (saiu: {d.FeitoAs})");
        checar(d.Prazo == "Preparar até 17:13", $"o prazo do iFood vira 'Preparar até 17:13' (saiu: {d.Prazo})");
        checar(d.Comecou == "Começou às 16:55", $"o carimbo de preparo vira 'Começou às 16:55' (saiu: {d.Comecou})");
        checar(d.ProntoAs is null, "em FAZENDO não existe 'Pronto às'");
        checar(d.Modalidade == "Entrega", "delivery sem retirada é 'Entrega'");
        checar(d.Agendado is null, "pedido imediato não tem linha de agendado");

        // ── os itens: nada some, tudo já normalizado pela regra do card ────
        checar(d.Itens.Count == 3, "os três itens do pedido estão no detalhe");
        var combo = d.Itens[0];
        checar(combo.Qtd == "1", "a quantidade vai SEM o ×: ela mora num círculo sozinha");
        checar(combo.Nome == "Combo 1 Cookies",
            $"a cauda redundante do combo sai aqui como no card (saiu: {combo.Nome})");
        checar(combo.Escolhas.Select(e => e.Texto).SequenceEqual(
                   new[] { "2× Clássicos: Cookie Tradicional", "2× Premium: Cookie Pistache" }),
            "o de dentro do combo sai linha a linha, com a quantidade na frente e ×");
        checar(combo.Observacao == "sem castanha", "a observação do item vem junto do item");
        var donut = d.Itens[1];
        checar(donut.Qtd == "2" && donut.Nome == "Donut Ninho", "item simples: '2' e 'Donut Ninho'");
        checar(donut.Escolhas.Count == 0 && donut.Observacao is null,
            "item simples sem observação não inventa escolha nem observação");
        var sep = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        checar(d.Itens[2].Qtd == "1" + sep + "5", $"quantidade fracionada sai como '1{sep}5' no círculo");

        // ── as seções da nuvem SOMEM quando não vieram ─────────────────────
        checar(d.Localizador is null && d.CodigoColeta is null && d.Observacoes is null,
            "sem complemento da nuvem: localizador, código de coleta e observação são nulos");
        checar(d.AgrupadoCom.Count == 0 && d.AgrupadoTexto is null,
            "sem complemento: 'agrupado com' não existe (nem lista nem texto)");
        checar(!d.TemComplemento, "sem complemento, TemComplemento é falso");

        // ── outras etapas, origens e horas ─────────────────────────────────
        var fila = DetalhePedido.De(Pedido(Kds.Recebido), Hoje);
        checar(fila.Etapa == "NA FILA" && fila.Comecou is null,
            "NA FILA: etapa com o nome da coluna e sem 'Começou às'");
        var pronto = DetalhePedido.De(Pedido(Kds.Pronto), Hoje);
        checar(pronto.Etapa == "PRONTO" && pronto.ProntoAs == "Pronto às 16:59",
            $"PRONTO: etapa e 'Pronto às 16:59' (saiu: {pronto.ProntoAs})");

        var balcao = DetalhePedido.De(Pedido(origem: "balcao", numero: "37", cliente: null), Hoje);
        checar(balcao.Canal == "Balcão", "origem balcao vira 'Balcão'");
        checar(balcao.Modalidade is null, "no balcão não há entrega nem retirada: a linha some");
        checar(balcao.Prazo is null, "balcão sem prazo não inventa 'Preparar até'");
        checar(balcao.Cliente is null, "pedido sem cliente não vira nome vazio");

        var cardapio = DetalhePedido.De(Pedido(numero: "CD-2246"), Hoje);
        checar(cardapio.Canal == "via Cardápio" && cardapio.Numero == "#CD-2246",
            "número CD- é o cardápio próprio: 'via Cardápio'");

        var ontem = Pedido() with { CriadoEm = new DateTime(2026, 9, 3, 23, 10, 0) };
        checar(DetalhePedido.De(ontem, Hoje).FeitoAs == "Feito 03/09 às 23:10",
            "pedido de outro dia leva a data: 'Feito 03/09 às 23:10'");

        // ── agendado e retirada ────────────────────────────────────────────
        var ag = Pedido(Kds.Recebido) with
        {
            Retirada = true, Agendado = true,
            AgendadoPara = Hoje.AddHours(1).AddMinutes(30), AgendadoAte = Hoje.AddHours(2),
        };
        var da = DetalhePedido.De(ag, Hoje);
        checar(da.Agendado == "Agendado para 18:30 a 19:00",
            $"agendado com faixa: 'Agendado para 18:30 a 19:00' (saiu: {da.Agendado})");
        checar(da.Modalidade == "Retirada no balcão", "retirada vira 'Retirada no balcão'");
        var agAmanha = ag with { AgendadoPara = Hoje.AddDays(1).Date.AddHours(10), AgendadoAte = null };
        checar(DetalhePedido.De(agAmanha, Hoje).Agendado == "Agendado para 05/09 10:00",
            "agendado para outro dia leva a data, como na comanda");
        var agSemHora = ag with { AgendadoPara = null, AgendadoAte = null };
        checar(DetalhePedido.De(agSemHora, Hoje).Agendado is null,
            "agendado SEM hora marcada não vira linha (não existe para o quadro)");

        // ── sem preço, sem travessão ───────────────────────────────────────
        foreach (var (nome, detalhe) in new[] { ("imediato", d), ("agendado", da), ("balcão", balcao) })
        {
            var textos = Textos(detalhe);
            checar(!textos.Any(s => s.Contains("R$") || s.Contains('—') || s.Contains('–')),
                $"nenhum texto do detalhe {nome} tem preço nem travessão");
        }
    }

    private static void Nuvem(Action<bool, string> checar)
    {
        var d = DetalhePedido.De(Pedido(), Hoje);
        // A lista vem como o Gestor manda: com o PRÓPRIO número, repetido, com e sem
        // cerquilha, com um vazio no meio. O entregador é um id opaco.
        var nuvem = new DetalheNuvem("order-1", " 3121 4455 ", "0807", "Deixar na portaria", null,
                                     "IMMEDIATE", "wk-9f3a", new[] { "3788", "#9002", "5077", "3788", "", " " });

        var c = d.ComNuvem(nuvem);
        checar(c.Localizador == "3121 4455", "localizador aparece, sem os espaços das pontas");
        checar(c.CodigoColeta == "0807", "código de coleta aparece");
        checar(c.Observacoes == "Deixar na portaria", "observação do pedido aparece");
        checar(c.AgrupadoCom.SequenceEqual(new[] { "#3788", "#9002" }),
            "agrupado: cerquilha normalizada, repetido sai, vazio sai e o PRÓPRIO pedido sai " +
            $"(saiu: {string.Join(" ", c.AgrupadoCom)})");
        checar(c.AgrupadoTexto == "Agrupado com #3788 #9002",
            $"a faixa diz 'Agrupado com #3788 #9002' (saiu: {c.AgrupadoTexto})");
        checar(c.TemComplemento, "com complemento, TemComplemento é verdadeiro");
        checar(!Textos(c).Any(s => s.Contains("wk-9f3a")),
            "o id do entregador NÃO aparece em texto nenhum da tela");
        checar(c.Itens.Count == d.Itens.Count && c.Numero == d.Numero && c.FeitoAs == d.FeitoAs,
            "completar com a nuvem não mexe no que já estava (itens, número, hora)");

        // O mesmo resultado quando o complemento já vem na abertura (modo foto).
        var direto = DetalhePedido.De(Pedido(), Hoje, nuvem);
        checar(direto.Localizador == c.Localizador && direto.CodigoColeta == c.CodigoColeta
               && direto.Observacoes == c.Observacoes && direto.AgrupadoCom.SequenceEqual(c.AgrupadoCom),
            "complemento na abertura e complemento depois dão a mesma tela");

        // Parcial: só o que veio aparece.
        var soColeta = d.ComNuvem(new DetalheNuvem("order-1", null, "0807", "  ", null, null, null,
                                                   Array.Empty<string>()));
        checar(soColeta.CodigoColeta == "0807" && soColeta.Localizador is null
               && soColeta.Observacoes is null && soColeta.AgrupadoTexto is null,
            "complemento parcial: só o código de coleta aparece; observação em branco some");
        var soEle = d.ComNuvem(new DetalheNuvem("order-1", null, null, null, null, null, null, new[] { "5077" }));
        checar(soEle.AgrupadoTexto is null && !soEle.TemComplemento,
            "agrupado só consigo mesmo é o mesmo que não agrupado: a faixa some");
        checar(!c.ComNuvem(null).TemComplemento, "complemento nulo limpa as seções de novo");
        checar(!Textos(c).Any(s => s.Contains('—') || s.Contains('–')),
            "as seções da nuvem também saem sem travessão");
    }

    /// <summary>
    /// A REGRA DO TOQUE, por zona. O card era um botão só e qualquer toque avançava;
    /// o dono marcou dois pedidos como FAZENDO sem querer. Agora: cabeçalho abre o
    /// detalhe, corpo não faz nada, rodapé avança. Em TODAS as combinações.
    /// </summary>
    private static void Toque(Action<bool, string> checar)
    {
        var estados = new[] { Kds.Recebido, Kds.Preparando, Kds.Pronto };
        var origens = new[] { "ifood", "balcao" };

        checar(estados.SelectMany(s => origens.Select(o => CardKds.AcaoDoToque(s, o, ZonaCard.Cabecalho)))
                      .All(a => a == ToqueKds.AbrirDetalhe),
            "cabeçalho abre o detalhe em TODAS as colunas e origens (nunca avança)");
        checar(estados.SelectMany(s => origens.Select(o => CardKds.AcaoDoToque(s, o, ZonaCard.Corpo)))
                      .All(a => a == ToqueKds.Nada),
            "o corpo do card (os itens) não faz nada: é onde o dedo encosta por acidente");

        // O rodapé mantém a regra que sempre valeu para o card inteiro.
        checar(CardKds.AcaoDoToque(Kds.Recebido, "ifood", ZonaCard.Rodape) == ToqueKds.Assumir
               && CardKds.AcaoDoToque(Kds.Recebido, "balcao", ZonaCard.Rodape) == ToqueKds.Assumir,
            "rodapé em NA FILA assume o pedido");
        checar(CardKds.AcaoDoToque(Kds.Preparando, "ifood", ZonaCard.Rodape) == ToqueKds.ConfirmarPronto
               && CardKds.AcaoDoToque(Kds.Preparando, "balcao", ZonaCard.Rodape) == ToqueKds.ConfirmarPronto,
            "rodapé em FAZENDO passa pela confirmação de pronto");
        checar(CardKds.AcaoDoToque(Kds.Pronto, "balcao", ZonaCard.Rodape) == ToqueKds.Entregar,
            "rodapé em PRONTO de balcão entrega ao cliente");
        checar(CardKds.AcaoDoToque(Kds.Pronto, "ifood", ZonaCard.Rodape) == ToqueKds.Nada,
            "rodapé em PRONTO de delivery não faz nada: a coleta é declarada pela API");
        checar(CardKds.AcaoDoToque(Kds.Cancelado, "ifood", ZonaCard.Rodape) == ToqueKds.Nada,
            "status fora do quadro não avança nada");
    }

    private static void Parser(Action<bool, string> checar)
    {
        // 1. completo, como a RPC devolve (tabela de uma linha)
        var completo = Pdv.Nucleo.Nuvem.LerDetalhePedido("""
            [{"order_id":"abc-123","localizador":"3121 4455","codigo_coleta":"0807",
              "observacoes":"Deixar na portaria","preparo_inicio":"2026-09-04T19:55:00+00:00",
              "order_timing":"SCHEDULED","entregador":"wk-9f3a","agrupado_com":["3788","9002",3340]}]
            """);
        checar(completo is not null, "linha completa é lida");
        checar(completo?.OrderId == "abc-123" && completo.Localizador == "3121 4455"
               && completo.CodigoColeta == "0807" && completo.Observacoes == "Deixar na portaria"
               && completo.PreparoInicio == "2026-09-04T19:55:00+00:00" && completo.OrderTiming == "SCHEDULED"
               && completo.Entregador == "wk-9f3a",
            "todos os campos de texto descem como vieram");
        checar(completo is not null && completo.AgrupadoCom.SequenceEqual(new[] { "3788", "9002", "3340" }),
            "agrupado_com aceita texto E número na mesma lista");

        // 2. tudo nulo (pedido sem complemento nenhum): a linha existe, os campos não
        var nulos = Pdv.Nucleo.Nuvem.LerDetalhePedido("""
            [{"order_id":"abc-123","localizador":null,"codigo_coleta":null,"observacoes":null,
              "preparo_inicio":null,"order_timing":null,"entregador":null,"agrupado_com":null}]
            """);
        checar(nulos is not null, "linha com tudo nulo ainda é uma linha");
        checar(nulos is { Localizador: null, CodigoColeta: null, Observacoes: null, Entregador: null }
               && nulos.AgrupadoCom.Count == 0,
            "campos nulos viram null e agrupado nulo vira lista vazia");
        checar(Pdv.Nucleo.Nuvem.LerDetalhePedido("""[{"order_id":"x","codigo_coleta":"   "}]""")?.CodigoColeta is null,
            "campo só com espaços é o mesmo que nulo");
        checar(Pdv.Nucleo.Nuvem.LerDetalhePedido("""[{"order_id":"x"}]""") is { AgrupadoCom.Count: 0 },
            "RPC sem o campo agrupado_com (versão antiga) devolve lista vazia, não exceção");

        // 3. lista vazia: pedido de balcão/cardápio, ou que a nuvem não conhece
        checar(Pdv.Nucleo.Nuvem.LerDetalhePedido("[]") is null, "lista vazia devolve null (sem seção)");
        checar(Pdv.Nucleo.Nuvem.LerDetalhePedido("""[{"localizador":"x"}]""") is null,
            "linha sem order_id não vale: null");

        // 4. lixo e formas alternativas
        checar(Pdv.Nucleo.Nuvem.LerDetalhePedido("{lixo") is null, "JSON quebrado devolve null, não exceção");
        checar(Pdv.Nucleo.Nuvem.LerDetalhePedido("") is null && Pdv.Nucleo.Nuvem.LerDetalhePedido("null") is null,
            "resposta vazia ou 'null' devolve null");
        checar(Pdv.Nucleo.Nuvem.LerDetalhePedido("""{"order_id":"solto","codigo_coleta":"1234"}""")?.CodigoColeta == "1234",
            "objeto solto (sem a lista em volta) também é lido");

        // 5. do parser à tela: o que a RPC mandou é o que o detalhe mostra
        var tela = DetalhePedido.De(Pedido(), Hoje, completo);
        checar(tela.CodigoColeta == "0807" && tela.Localizador == "3121 4455"
               && tela.AgrupadoTexto == "Agrupado com #3788 #9002 #3340",
            "a linha da RPC vira as seções do detalhe");
    }

    /// <summary>
    /// O que a suíte NÃO alcança é o WPF. O que dá para travar no FONTE da tela: o
    /// avanço de etapa (Assumir/Liberar/Entregar) só existe dentro do clique do
    /// RODAPÉ, e o clique do cabeçalho pergunta à regra com ZonaCard.Cabecalho. Se
    /// alguém religar o avanço no cabeçalho ou no card inteiro, cai aqui.
    /// </summary>
    private static void FonteDaTela(Action<bool, string> checar)
    {
        var arquivo = AchaFonte(Path.Combine("Telas", "Kds.xaml.cs"));
        if (arquivo is null)
        {
            checar(false, "não achei Telas/Kds.xaml.cs para conferir as zonas de toque");
            return;
        }
        var fonte = File.ReadAllText(arquivo);
        var rodape = Regex.Match(fonte, @"rodape\.Click \+= .*?Grid\.SetRow\(rodape", RegexOptions.Singleline).Value;
        checar(rodape.Length > 0, "achei o clique do rodapé na tela do KDS");
        foreach (var avanco in new[] { "Nucleo.Kds.Assumir(", "Nucleo.Kds.Liberar(", "Nucleo.Kds.Entregar(" })
        {
            var total = Regex.Matches(fonte, Regex.Escape(avanco)).Count;
            var noRodape = Regex.Matches(rodape, Regex.Escape(avanco)).Count;
            checar(total >= 1 && total == noRodape,
                $"{avanco} só é chamado dentro do clique do rodapé ({noRodape} de {total})");
        }
        checar(rodape.Contains("ZonaCard.Rodape", StringComparison.Ordinal),
            "o rodapé pergunta à regra com ZonaCard.Rodape");
        var cabecalho = Regex.Match(fonte, @"btnCab\.Click \+= .*?\};", RegexOptions.Singleline).Value;
        checar(cabecalho.Contains("ZonaCard.Cabecalho", StringComparison.Ordinal)
               && cabecalho.Contains("AbrirDetalhe(t)", StringComparison.Ordinal)
               && cabecalho.Contains("e.Handled = true", StringComparison.Ordinal),
            "o cabeçalho pergunta com ZonaCard.Cabecalho, abre o detalhe e mata o clique ali");

        var painel = AchaFonte(Path.Combine("Telas", "DetalhePedidoKds.cs"));
        if (painel is null) { checar(false, "não achei Telas/DetalhePedidoKds.cs"); return; }
        var codigo = File.ReadAllText(painel);
        // Só o que vira TEXTO DE TELA (as strings), não os comentários.
        var literais = Regex.Matches(codigo, "\"(?:[^\"\\\\]|\\\\.)*\"").Select(m => m.Value).ToList();
        checar(literais.Count > 0 && !literais.Any(s => s.Contains('—') || s.Contains('–')),
            "o painel do detalhe não tem travessão nem meia-risca em texto de tela");
        checar(!Regex.IsMatch(codigo, @"Nucleo\.Kds\.(Assumir|Liberar|Entregar)\("),
            "o painel do detalhe não avança etapa nenhuma");
    }

    /// <summary>Todo texto que a tela pode pintar, para as provas de "não contém".</summary>
    private static List<string> Textos(DetalhePedido d)
    {
        var t = new List<string?>
        {
            d.Numero, d.Etapa, d.Canal, d.Cliente, d.FeitoAs, d.Agendado, d.Modalidade, d.Prazo,
            d.Comecou, d.ProntoAs, d.Localizador, d.CodigoColeta, d.Observacoes, d.AgrupadoTexto,
        };
        t.AddRange(d.AgrupadoCom);
        foreach (var i in d.Itens)
        {
            t.Add(i.Qtd); t.Add(i.Nome); t.Add(i.Observacao);
            t.AddRange(i.Escolhas.Select(e => e.Texto));
        }
        return t.Where(s => s is not null).Select(s => s!).ToList();
    }

    /// <summary>Sobe do binário do teste até achar o arquivo pedido no repositório.</summary>
    private static string? AchaFonte(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidato = Path.Combine(dir.FullName, relativo);
            if (File.Exists(candidato)) return candidato;
        }
        return null;
    }
}
