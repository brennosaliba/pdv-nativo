using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O TEXTO do card do quadro de preparo (Pdv.Nucleo/CardKds).
///
/// Nasceu de uma FOTO. O dono mandou um card real da cozinha, estreito, com tudo
/// quebrando linha, e pediu diferenciação entre item, subitem e o rodapé de espera.
/// A cor é assunto do tema (TestesTema mede); o que se prova aqui é o texto:
///
///   #8998  13 min
///   Cassia Nery Lucas Si...
///   1× Combo Box 4un
///       - 2x Donut Homer
///       - 1x Donut Morango c/ Ninho
///       - 1x Donut Ninho c/ Nutella
///       - 1x Donut Calabresa
///   1× Combo 1 Cookies - 4 unidades
///       - 2x Cookie Tradicional
///       - 2x Cookie Brigadeiro
///   AGUARDANDO O ENTREGADOR
///
/// Dois "x" diferentes no mesmo card e 14 caracteres repetindo o que já está
/// listado embaixo. A metade dos testes daqui é do lado do CORTE; a outra metade
/// é do lado do NÃO CORTE, que é o que importa: nome mutilado na cozinha custa
/// mais caro que nome comprido.
/// </summary>
public static class TestesCardKds
{
    // Os dois combos da foto, com os componentes exatamente como a foto mostrava.
    private static readonly string[] ComboBox =
    {
        "2x Donut Homer", "1x Donut Morango c/ Ninho",
        "1x Donut Ninho c/ Nutella", "1x Donut Calabresa",
    };
    private static readonly string[] ComboCookies =
    {
        "2x Cookie Tradicional", "2x Cookie Brigadeiro",
    };

    public static void Rodar(Action<bool, string> checar)
    {
        // ── quantidade do item ──────────────────────────────────────────────
        checar(CardKds.Quantidade(1000) == "1", "1000 milésimos vira '1'");
        checar(CardKds.Quantidade(2000) == "2", "2000 milésimos vira '2'");
        var sep = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        checar(CardKds.Quantidade(1500) == "1" + sep + "5",
            $"quantidade fracionada usa o separador da máquina: 1500 -> '1{sep}5'");
        checar(CardKds.Quantidade(500) == "0" + sep + "5", "meia unidade vira '0,5', não some");

        // ── marcador de quantidade: UM SÓ no card ───────────────────────────
        // Era o defeito nº 1 da foto: item com "×" e subitem com "x".
        var item = CardKds.ItemPrincipal(1000, "Donut Homer", null);
        checar(item.Qtd == "1×", "item principal marca a quantidade com ×");
        checar(item.Texto == "1× Donut Homer", "item simples sai inteiro: '1× Donut Homer'");

        var sub = CardKds.SubItem("2x Donut Homer");
        checar(sub.Qtd == "2×", "subitem também marca com × (vinha com x minúsculo do JSON)");
        checar(sub.Nome == "Donut Homer", "subitem separa nome da quantidade");
        checar(sub.Texto == "2× Donut Homer", "subitem inteiro: '2× Donut Homer'");
        checar(CardKds.SubItem("2 x Donut Homer").Texto == "2× Donut Homer",
            "espaço antes do x também normaliza");
        checar(CardKds.SubItem(CardKds.SubItem("2x Donut Homer").Texto).Texto == "2× Donut Homer",
            "normalizar de novo não muda nada (idempotente)");

        // grupo do combo: a quantidade sobe para a frente, o grupo fica no nome —
        // a lista só funciona como coluna se TODA linha começar por número
        var comGrupo = CardKds.SubItem("Clássicos: 2x Donut Ninho");
        checar(comGrupo.Qtd == "2×", "escolha com grupo mantém a quantidade na frente");
        checar(comGrupo.Nome == "Clássicos: Donut Ninho", "o grupo continua na linha, sem se perder");

        // escolha sem quantidade legível: não inventa "1×"
        var crua = CardKds.SubItem("Sem cebola");
        checar(crua.Qtd == "" && crua.Nome == "Sem cebola",
            "escolha sem número sai como veio, sem '1×' inventado");
        checar(CardKds.SubItem("3x4 Bolo").Texto == "3x4 Bolo",
            "'3x4' não é quantidade: sem espaço depois do x, nada é reescrito");
        checar(CardKds.SubItem(null).Texto == "" && CardKds.SubItem("  ").Texto == "",
            "escolha nula ou em branco não vira lixo na tela");

        // ── a cauda redundante: os dois combos da foto ──────────────────────
        // "Combo Box 4un" com os 4 sabores DA FOTO: eles somam CINCO donuts, não quatro.
        // O 4 do nome só casa com a CONTAGEM de linhas, e contagem não é prova de nada.
        // Cortar aqui apagaria a única pista de que uma caixa de 4 está saindo com 5.
        var box = CardKds.ItemPrincipal(1000, "Combo Box 4un", ComboBox);
        checar(box.Texto == "1× Combo Box 4un",
            $"cauda que DISCORDA do que está embaixo (4un x 5 donuts) fica na tela (saiu: {box.Texto})");

        // "Combo 1 Cookies - 4 unidades" com 2x + 2x embaixo: o 4 é a SOMA.
        var cookies = CardKds.ItemPrincipal(1000, "Combo 1 Cookies - 4 unidades", ComboCookies);
        checar(cookies.Texto == "1× Combo 1 Cookies",
            $"'Combo 1 Cookies - 4 unidades' com 2+2 embaixo vira '1× Combo 1 Cookies' (saiu: {cookies.Texto})");
        checar(CardKds.SemCaudaRedundante("Caixa Mista (6 unidades)",
                   new[] { "3x Donut Ninho", "3x Donut Nutella" }) == "Caixa Mista",
            "cauda entre parênteses também sai, com o parêntese junto");
        checar(CardKds.SemCaudaRedundante("Combo Duo 2 pçs",
                   new[] { "1x Donut", "1x Cookie" }) == "Combo Duo",
            "'pçs' conta como palavra de unidade");

        // ── e agora o que NÃO pode ser tocado ───────────────────────────────
        checar(CardKds.ItemPrincipal(1000, "Combo Box 4un", null).Texto == "1× Combo Box 4un",
            "sem componentes listados o número FICA: é a única medida de tamanho que sobra");
        checar(CardKds.SemCaudaRedundante("Caixa Donut 6un", Array.Empty<string>()) == "Caixa Donut 6un",
            "lista de componentes vazia também deixa o nome em paz");
        checar(CardKds.SemCaudaRedundante("Combo Família 12un",
                   new[] { "2x Donut Homer", "2x Donut Ninho", "2x Donut Nutella" }) == "Combo Família 12un",
            "12 não é a soma (6) nem a contagem (3): não está provado, não corta");
        checar(CardKds.SemCaudaRedundante("Combo Box 2un",
                   new[] { "Donut Homer", "Donut Ninho" }) == "Combo Box 2un",
            "componente sem quantidade legível derruba a prova inteira: nome intacto");
        checar(CardKds.SemCaudaRedundante("Kit 12 un Mini",
                   Enumerable.Repeat("1x Donut", 12).ToArray()) == "Kit 12 un Mini",
            "a contagem tem que estar no FIM do nome; no meio ela é parte do nome");
        checar(CardKds.SemCaudaRedundante("Combo 4",
                   new[] { "1x A", "1x B", "1x C", "1x D" }) == "Combo 4",
            "número solto no fim é linha de produto, não contagem: fica");
        checar(CardKds.SemCaudaRedundante("Combo 3 Unicórnios",
                   new[] { "1x A", "1x B", "1x C" }) == "Combo 3 Unicórnios",
            "'Unicórnios' não é 'uni': palavra que só começa igual não é unidade");
        checar(CardKds.SemCaudaRedundante("Kit 4un",
                   new[] { "1x A", "1x B", "1x C", "1x D" }) == "Kit 4un",
            "sobraria só 'Kit': curto demais para continuar sendo nome, então não corta");
        checar(CardKds.SemCaudaRedundante("Combo c/ 4 unidades",
                   new[] { "1x A", "1x B", "1x C", "1x D" }) == "Combo c/ 4 unidades",
            "'Combo c/' seria nome mutilado: a trava do último pedaço segura o corte");
        checar(CardKds.SemCaudaRedundante("Donut Homer", ComboBox) == "Donut Homer",
            "nome que não tem cauda nenhuma passa inteiro");
        checar(CardKds.SemCaudaRedundante("", ComboBox) == "" &&
               CardKds.SemCaudaRedundante("   ", ComboBox) == "",
            "nome vazio não quebra a pintura do card");

        // ── nada do que o cliente pediu some do card ────────────────────────
        // A regra do dono é explícita: o card CRESCE, não esconde. Aqui isso vira
        // asserção — cada componente do combo continua legível linha a linha.
        foreach (var esc in ComboBox)
        {
            var nome = esc[(esc.IndexOf(' ') + 1)..];
            checar(CardKds.SubItem(esc).Nome == nome,
                $"o sabor '{nome}' continua inteiro na linha do subitem");
        }
        checar(CardKds.QtdDaEscolha("1,5x Bolo") == 1.5m && CardKds.QtdDaEscolha("1.5x Bolo") == 1.5m,
            "quantidade fracionada da escolha lê com vírgula E com ponto");
        checar(CardKds.QtdDaEscolha("Bolo") is null, "escolha sem número devolve null, não zero");

        // ── o card da foto, do primeiro item ao último ──────────────────────
        var itens = new List<TicketItem>
        {
            new("Combo Box 4un", 1000, null, ComboBox),
            new("Combo 1 Cookies - 4 unidades", 1000, null, ComboCookies),
        };
        var linhas = new List<string>();
        foreach (var i in itens)
        {
            linhas.Add(CardKds.ItemPrincipal(i).Texto);
            foreach (var e in i.Escolhas!) linhas.Add(CardKds.SubItem(e).Texto);
        }
        var esperado = new[]
        {
            // O "4un" FICA: os sabores somam 5. Só a segunda cauda ("- 4 unidades",
            // com 2+2 = 4 embaixo) é redundante de verdade e sai.
            "1× Combo Box 4un",
            "2× Donut Homer", "1× Donut Morango c/ Ninho",
            "1× Donut Ninho c/ Nutella", "1× Donut Calabresa",
            "1× Combo 1 Cookies",
            "2× Cookie Tradicional", "2× Cookie Brigadeiro",
        };
        checar(linhas.SequenceEqual(esperado),
            "o card inteiro da foto sai como esperado: " + string.Join(" | ", linhas));
        checar(linhas.Count == esperado.Length,
            "o card continua com 8 linhas: normalizar texto não some com item nenhum");
        // O defeito nº 1 da foto, virado asserção: nenhuma linha do card marca
        // quantidade com "x" minúsculo, e toda linha começa pela quantidade.
        checar(linhas.All(l => Regex.IsMatch(l, @"^\d+(?:[.,]\d+)?× ")),
            "toda linha do card começa com a quantidade seguida de ×");
        checar(!linhas.Any(l => Regex.IsMatch(l, @"^\d+(?:[.,]\d+)?\s*x\s")),
            "nenhuma linha do card ainda marca quantidade com x minúsculo");

        Densidade(checar);
        AlinhamentoDoCard(checar);
        Tempo(checar);
        Entregador(checar);
    }

    /// <summary>
    /// O RELÓGIO DO CARD (04/09, foto do dono). Lado a lado: o Gestor do iFood dizia
    /// "Preparar em 4min" para o #2835 e o nosso card dizia "17 min". "Por que essa
    /// diferença?" Relógios diferentes: o Gestor conta o que FALTA do prazo; o card, sem
    /// prazo, o que PASSOU; e com prazo ele contava o que falta com o MESMO texto do
    /// decorrido, então ninguém sabia qual estava lendo. Agora o texto diz.
    ///
    /// Os quatro casos da foto, a virada do minuto (nunca "faltam 0 min", nunca "atrasado
    /// 0 min"), o congelamento em PRONTO e a prova de que o mesmo prazo muda de texto um
    /// minuto depois (quem repinta é o timer de 10 s do quadro).
    /// </summary>
    private static void Tempo(Action<bool, string> checar)
    {
        var agora = new DateTime(2026, 9, 4, 18, 12, 0);
        var chegou = agora.AddMinutes(-17);
        (string Texto, TomTempo Tom) T(DateTime? prazo, DateTime? pronto = null)
            => CardKds.TempoDoCard(chegou, prazo, agora, pronto);

        // ── os quatro casos da foto ─────────────────────────────────────────
        checar(T(agora.AddMinutes(4)) == ("faltam 4 min", TomTempo.Normal),
            "prazo daqui a 4 min: 'faltam 4 min', cor normal (o que o Gestor mostra)");
        checar(T(agora.AddMinutes(2)) == ("faltam 2 min", TomTempo.Atencao),
            "prazo daqui a 2 min: 'faltam 2 min' em atenção (3 ou menos)");
        checar(T(agora.AddMinutes(-3)) == ("atrasado 3 min", TomTempo.Atraso),
            "prazo há 3 min: 'atrasado 3 min' em vermelho");
        checar(T(null) == ("17 min", TomTempo.Atencao),
            "sem preparo_ate continua o decorrido de sempre: '17 min'");

        // ── o limiar da atenção e o truncamento ─────────────────────────────
        checar(T(agora.AddMinutes(3)) == ("faltam 3 min", TomTempo.Atencao),
            "3 min já é atenção (limiar inclusivo)");
        checar(T(agora.AddMinutes(4).AddSeconds(50)) == ("faltam 4 min", TomTempo.Normal),
            "4 min e 50 s são 'faltam 4 min': trunca, como o Gestor, nunca arredonda a favor");
        checar(T(agora.AddMinutes(3).AddSeconds(59)) == ("faltam 3 min", TomTempo.Atencao),
            "3 min e 59 s ainda é atenção: a cor vira junto com o texto, não antes");
        checar(CardKds.MinutosDeAtencao == 3, "o limiar de atenção continua em 3 min, como o dono pediu");

        // ── a virada do minuto: 'faltam 0 min' e 'atrasado 0 min' não existem ─
        checar(T(agora.AddSeconds(59)) == ("agora", TomTempo.Atencao),
            "faltando 59 s o card diz 'agora', não 'faltam 0 min'");
        checar(T(agora) == ("agora", TomTempo.Atencao), "prazo exatamente agora: 'agora'");
        checar(T(agora.AddSeconds(-1)) == ("atrasado 1 min", TomTempo.Atraso),
            "1 s depois do prazo: 'atrasado 1 min', nunca 'atrasado 0 min'");
        checar(T(agora.AddSeconds(-60)) == ("atrasado 1 min", TomTempo.Atraso),
            "60 s depois: ainda 'atrasado 1 min'");
        checar(T(agora.AddSeconds(-61)) == ("atrasado 2 min", TomTempo.Atraso),
            "61 s depois: 'atrasado 2 min' (o atraso arredonda para cima: errar para a pressa)");
        checar(T(agora.AddMinutes(1)) == ("falta 1 min", TomTempo.Atencao),
            "um minuto é singular: 'falta 1 min'");
        checar(T(agora.AddSeconds(90)) == ("falta 1 min", TomTempo.Atencao),
            "90 s: 'falta 1 min' (trunca)");

        // ── o decorrido, intacto ────────────────────────────────────────────
        checar(CardKds.TempoDoCard(agora.AddSeconds(-30), null, agora) == ("agora", TomTempo.Normal),
            "chegou há 30 s: 'agora', em cor normal (era assim)");
        checar(CardKds.TempoDoCard(agora.AddMinutes(-9), null, agora) == ("9 min", TomTempo.Normal),
            "9 min de espera: normal");
        checar(CardKds.TempoDoCard(agora.AddMinutes(-10), null, agora) == ("10 min", TomTempo.Atencao),
            "10 min de espera: atenção (degrau de sempre)");
        checar(CardKds.TempoDoCard(agora.AddMinutes(-20), null, agora) == ("20 min", TomTempo.Atraso),
            "20 min de espera: vermelho (degrau de sempre)");
        checar(CardKds.TempoDoCard(agora.AddMinutes(5), null, agora) == ("agora", TomTempo.Normal),
            "relógio da máquina atrás da nuvem (chegada no futuro) não vira número negativo");

        // ── PRONTO congela o relógio, em passado ────────────────────────────
        checar(T(agora.AddMinutes(4), pronto: agora.AddMinutes(-1)) == ("no prazo", TomTempo.Normal),
            "pronto antes do prazo: 'no prazo', e não 'faltam 5 min' num pedido que já acabou");
        checar(T(agora.AddMinutes(-3), pronto: agora.AddMinutes(-1)) == ("atrasou 2 min", TomTempo.Atraso),
            "ficou pronto 2 min depois do prazo: 'atrasou 2 min', congelado no momento do pronto");
        checar(T(agora.AddMinutes(-3), pronto: agora.AddMinutes(-3)) == ("no prazo", TomTempo.Normal),
            "pronto em cima do prazo é no prazo");
        checar(T(agora.AddMinutes(-3), pronto: agora.AddMinutes(-3).AddSeconds(1)) == ("atrasou 1 min", TomTempo.Atraso),
            "1 s depois do prazo já é 'atrasou 1 min' (para cima, como o atrasado)");
        checar(T(null, pronto: agora.AddMinutes(-1)) == ("16 min", TomTempo.Atencao),
            "sem prazo, o pronto congela o decorrido em 16 min (como Ticket.Espera sempre fez)");

        // ── o texto anda sozinho ────────────────────────────────────────────
        var prazo = agora.AddMinutes(4);
        checar(CardKds.TempoDoCard(chegou, prazo, agora).Texto == "faltam 4 min"
               && CardKds.TempoDoCard(chegou, prazo, agora.AddMinutes(1)).Texto == "faltam 3 min"
               && CardKds.TempoDoCard(chegou, prazo, agora.AddMinutes(4)).Texto == "agora"
               && CardKds.TempoDoCard(chegou, prazo, agora.AddMinutes(5)).Texto == "atrasado 1 min",
            "o mesmo prazo, minuto a minuto: faltam 4, faltam 3, agora, atrasado 1 (o timer de 10 s repinta)");

        // ── cor é pedida ao tema pela chave, nunca escolhida na tela ────────
        checar(CardKds.PincelDoTom(TomTempo.Normal) == "Ok" && CardKds.PincelDoTom(TomTempo.Atencao) == "Amarelo"
               && CardKds.PincelDoTom(TomTempo.Atraso) == "Erro",
            "os três tons mapeiam para Ok / Amarelo / Erro, chaves que existem nos dois temas");

        // ── curto e sem travessão: é lido a 2 m, de pé ──────────────────────
        foreach (var (txt, _) in new[]
        {
            T(agora.AddMinutes(4)), T(agora.AddMinutes(-3)), T(agora), T(null),
            T(agora.AddMinutes(4), agora.AddMinutes(-1)), T(agora.AddMinutes(-3), agora.AddMinutes(-1)),
        })
            checar(txt.Length <= 16 && !txt.Contains('—') && !txt.Contains('–'),
                $"'{txt}' cabe no cabeçalho do card e não tem travessão");
    }

    /// <summary>
    /// O ENTREGADOR NO CARD (04/09, a mesma foto): o Gestor dizia "Chega em 1min" e o
    /// card, nada. O que existe nos nossos dados é o ÚLTIMO EVENTO de logística
    /// (pdv_kds_entrega); o mapeamento código -> texto curto é puro e é isto que se
    /// prova. Códigos reais vistos em produção em 04/09: PLACED, CONFIRMED, ASSIGNED,
    /// GOING_TO_ORIGIN, ARRIVED_AT_ORIGIN, COLLECTED, DISPATCHED, CONCLUDED,
    /// READY_TO_PICKUP. Previsão em minutos NÃO existe e o texto não pode ter número.
    /// </summary>
    private static void Entregador(Action<bool, string> checar)
    {
        checar(CardKds.TextoEntregador("ASSIGNED") == "entregador designado", "ASSIGNED: 'entregador designado'");
        checar(CardKds.TextoEntregador("GOING_TO_ORIGIN") == "entregador a caminho", "GOING_TO_ORIGIN: 'entregador a caminho'");
        checar(CardKds.TextoEntregador("ARRIVED_AT_ORIGIN") == "entregador chegou", "ARRIVED_AT_ORIGIN: 'entregador chegou'");
        checar(CardKds.TextoEntregador("COLLECTED") == "saiu para entrega", "COLLECTED: 'saiu para entrega'");
        checar(CardKds.TextoEntregador("DISPATCHED") == "saiu para entrega", "DISPATCHED: 'saiu para entrega'");

        foreach (var mudo in new[] { "PLACED", "CONFIRMED", "READY_TO_PICKUP", "CONCLUDED", "XPTO_NOVO", "", "   ", null })
            checar(CardKds.TextoEntregador(mudo) is null && !CardKds.EntregadorChegou(mudo),
                $"'{mudo ?? "null"}' não vira texto nenhum no card");

        checar(CardKds.TextoEntregador(" arrived_at_origin ") == "entregador chegou",
            "o código é lido sem caixa e sem espaços: a ponte não escolhe cultura");
        checar(CardKds.EntregadorChegou("ARRIVED_AT_ORIGIN") && !CardKds.EntregadorChegou("GOING_TO_ORIGIN")
               && !CardKds.EntregadorChegou("COLLECTED"),
            "só ARRIVED_AT_ORIGIN é destaque: é o momento em que o pedido tem de estar pronto");

        // a faixa de baixo da coluna PRONTO
        checar(CardKds.RodapeEspera(false, null) == "AGUARDANDO O ENTREGADOR",
            "sem evento a faixa é a de sempre: AGUARDANDO O ENTREGADOR");
        checar(CardKds.RodapeEspera(false, "PLACED") == "AGUARDANDO O ENTREGADOR",
            "evento mudo (PLACED) também deixa a faixa de sempre");
        checar(CardKds.RodapeEspera(false, "GOING_TO_ORIGIN") == "ENTREGADOR A CAMINHO",
            "a caminho vira a faixa ENTREGADOR A CAMINHO");
        checar(CardKds.RodapeEspera(false, "ARRIVED_AT_ORIGIN") == "ENTREGADOR CHEGOU",
            "chegou vira a faixa ENTREGADOR CHEGOU");
        checar(CardKds.RodapeEspera(true, "ARRIVED_AT_ORIGIN") == "AGUARDANDO O CLIENTE RETIRAR",
            "RETIRADA ignora evento de entregador: é o cliente que vem, não motoboy");
        checar(CardKds.RodapeEspera(true, null) == "AGUARDANDO O CLIENTE RETIRAR", "retirada sem evento: idem");

        // nada de número: previsão em minutos não existe nos nossos dados
        foreach (var c in new[] { "ASSIGNED", "GOING_TO_ORIGIN", "ARRIVED_AT_ORIGIN", "COLLECTED" })
        {
            var txt = CardKds.TextoEntregador(c)!;
            checar(!Regex.IsMatch(txt, @"\d") && txt.Length <= 22 && !txt.Contains('—') && !txt.Contains('–'),
                $"'{txt}' é curto, sem número inventado e sem travessão");
        }
    }

    /// <summary>
    /// QUANTOS CARDS CABEM LADO A LADO (04/09, segunda reclamação do dono).
    ///
    /// "ainda nao esta bom o ux..talvez diminuir um poouco a fonte..aumentar o box".
    /// O quadro trazia DOIS cards por coluna, fixos no código. A 1024x768, que é a
    /// tela da Savassi, cada coluna do quadro tem ~307 px úteis: dois cards ali dão
    /// ~150 px cada, e a 16 px quase todo item quebra em duas linhas. Agora quem
    /// decide é a largura medida.
    ///
    /// As medidas abaixo saíram do modo --foto-kds nas resoluções reais, não de conta
    /// de cabeça: coluna do quadro = (largura - 24 de margem) / 3, menos 8 de margem,
    /// 2 de borda e 16 de recuo do ScrollViewer.
    /// </summary>
    private static void Densidade(Action<bool, string> checar)
    {
        checar(CardKds.CardsPorLinha(0) == 1,
            "largura ainda não medida responde 1 (a primeira pintura sai antes do layout)");
        checar(CardKds.CardsPorLinha(-500) == 1, "largura negativa não vira zero card por linha");
        checar(CardKds.CardsPorLinha(307) == 1,
            "a 1024x768 (a tela da Savassi) cabe UM card por coluna, não dois");
        checar(CardKds.CardsPorLinha(421) == 1, "a 1366x768 ainda é um card por coluna");
        checar(CardKds.CardsPorLinha(606) == 2, "a 1920x1080 cabem dois cards por coluna");
        checar(CardKds.CardsPorLinha(819) == 3, "num monitor de 2560 cabem três");
        checar(CardKds.CardsPorLinha(100000) == 3, "o teto de três vale mesmo em tela absurda");
        checar(CardKds.CardsPorLinha(CardKds.LarguraMinimaCard) == 1,
            "exatamente a largura mínima é UM card, não zero");
        checar(CardKds.CardsPorLinha(CardKds.LarguraMinimaCard - 1) == 1,
            "abaixo da largura mínima ainda é um card (nunca zero: o quadro ficaria vazio)");
        checar(CardKds.CardsPorLinha(CardKds.LarguraMinimaCard * 2) == 2,
            "o dobro da largura mínima é que volta a ser dois");
        checar(CardKds.CardsPorLinha(1000, teto: 1) == 1, "o teto pedido é respeitado");
        checar(CardKds.CardsPorLinha(1000, teto: 0) == 1, "teto zero não zera a coluna");
        // A régua: um card menor que isto quebra "Tortinha de Frango com Catupiry" em
        // duas linhas a 16 px. Encolher a FONTE seria a outra saída, e é a errada: a
        // cozinha lê o card a 1 ou 2 metros.
        checar(CardKds.LarguraMinimaCard is >= 250 and <= 320,
            "a largura mínima do card continua na faixa medida na foto (250 a 320 px)");
    }

    /// <summary>
    /// O CONTEÚDO DO CARD COLADO NO TOPO (04/09, terceira reclamação do dono).
    ///
    /// "alem de q o toque quando pronto ficar pronto nao esta alinhado..mesmo q na
    /// mesma linha". A causa não estava no card: estava no BotaoBase, cujo
    /// ContentPresenter tinha "Center/Center" CRAVADO no template. O card do KDS pede
    /// Stretch nos dois eixos e era ignorado — o conteúdo boiava no meio do botão e
    /// dois cards da mesma linha terminavam com o rodapé em alturas diferentes.
    ///
    /// Isto aqui é layout, e layout não roda na suíte. O que dá para travar é o
    /// TEXTO do estilo: se alguém cravar o alinhamento de novo, cai aqui e não na
    /// cozinha. O padrão do Button já é Center nos dois eixos, então o TemplateBinding
    /// não muda nada para o resto da tela (medido: a foto da venda a 1024x768 saiu
    /// pixel por pixel idêntica antes e depois).
    /// </summary>
    private static void AlinhamentoDoCard(Action<bool, string> checar)
    {
        var arquivo = AchaEstilos();
        if (arquivo is null)
        {
            checar(false, "não achei o Estilos.xaml para conferir o alinhamento do BotaoBase");
            return;
        }
        var xaml = File.ReadAllText(arquivo);
        // só o trecho do BotaoBase: os outros templates têm ContentPresenter próprio
        var corpo = Regex.Match(xaml, "x:Key=\"BotaoBase\".*?</Style>", RegexOptions.Singleline).Value;
        checar(corpo.Length > 0, "achei o estilo BotaoBase no Estilos.xaml");
        var apresentador = Regex.Match(corpo, "<ContentPresenter[^>]*>", RegexOptions.Singleline).Value;
        checar(apresentador.Contains("{TemplateBinding VerticalContentAlignment}", StringComparison.Ordinal),
            "o BotaoBase obedece ao VerticalContentAlignment do botão (era 'Center' cravado)");
        checar(apresentador.Contains("{TemplateBinding HorizontalContentAlignment}", StringComparison.Ordinal),
            "o BotaoBase obedece ao HorizontalContentAlignment do botão");
        checar(!Regex.IsMatch(apresentador, "VerticalAlignment=\"(Center|Top|Bottom|Stretch)\""),
            "o alinhamento vertical do BotaoBase não voltou a ser cravado no template");
    }

    /// <summary>Estilos.xaml a partir da pasta do teste, subindo até achar o fonte.</summary>
    private static string? AchaEstilos()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidato = Path.Combine(dir.FullName, "Estilos.xaml");
            if (File.Exists(candidato)) return candidato;
        }
        return null;
    }
}
