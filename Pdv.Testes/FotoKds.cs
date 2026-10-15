using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// FOTOGRAFA o quadro do KDS numa resolução dada, sem monitor daquele tamanho.
///
/// Irmão do <see cref="FotoVenda"/>, e pelo mesmo motivo: em 04/09 o dono olhou a
/// 0.5.3 na Savassi e reclamou de três coisas de LAYOUT (card sem desfazer à vista,
/// conteúdo boiando longe do topo, item quebrando em duas linhas). Nada disso
/// aparece no monitor de quem programa, porque o quadro divide a tela em três e a
/// largura do card depende da resolução — a 1024x768 cada card fica com ~150 px.
/// Sem foto, "melhorei o layout" é opinião.
///
/// Uso: Pdv.Testes.exe --foto-kds saida.png largura altura [claro|escuro] [--detalhe N] [--nuvem]
///                     [--finalizados hoje|ontem|7dias] [--busca X] [--sem-finalizados]
///
///   --detalhe N  abre o DETALHE do pedido #N por cima do quadro antes da foto
///                (04/09: é a prova visual do painel a 1024x768 sem cortar item).
///   --nuvem      junto com --detalhe: finge a resposta da RPC de detalhe (localizador,
///                código de coleta, observação do pedido, agrupado com) para as seções
///                da nuvem saírem na foto. Sem ele, elas ficam de fora — que é o que
///                a loja vê quando a nuvem não responde.
///   --finalizados P  (14/10/2026) abre a LISTA DOS QUE JÁ SAÍRAM no período P antes da foto.
///                Com --detalhe N, o detalhe abre por cima da lista. Com --nuvem, a consulta
///                pdv_kds_status responde (fabricada); sem ele, a lista diz "Sem internet".
///   --busca X    digita X na busca do cabeçalho: no quadro, aparece a faixa da busca; na
///                lista, ela filtra por número ou nome.
///   --sem-finalizados  não semeia pedido nenhum que já saiu (a lista vazia).
///
/// Segurança: NÃO usa o banco do caixa. Monta um SQLite temporário do zero com um
/// quadro fabricado (os mesmos cards da foto que o dono mandou) e a nuvem apontada
/// para um endereço morto. Nada é lido nem gravado na operação real.
/// </summary>
public static class FotoKds
{
    public static int Rodar(string[] args)
    {
        var saida = Path.GetFullPath(args[1]);
        var w = int.Parse(args[2]);
        var h = int.Parse(args[3]);
        var tema = "claro";
        string? detalhe = null;
        var comNuvem = false;
        PeriodoFinalizados? finalizados = null;
        string? busca = null;
        var semFinalizados = false;
        for (var i = 4; i < args.Length; i++)
        {
            if (args[i] == "--detalhe" && i + 1 < args.Length) detalhe = args[++i];
            else if (args[i] == "--nuvem") comNuvem = true;
            else if (args[i] == "--finalizados" && i + 1 < args.Length)
                finalizados = args[++i].ToLowerInvariant() switch
                {
                    "ontem" => PeriodoFinalizados.Ontem,
                    "7dias" or "7" => PeriodoFinalizados.SeteDias,
                    _ => PeriodoFinalizados.Hoje,
                };
            else if (args[i] == "--busca" && i + 1 < args.Length) busca = args[++i];
            else if (args[i] == "--sem-finalizados") semFinalizados = true;
            else if (args[i] is "claro" or "escuro") tema = args[i];
        }

        var fixture = Path.Combine(Path.GetTempPath(), "foto-kds-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        Banco.Migrar(fixture);
        Banco.CaminhoForcado = fixture;
        Semear();
        if (!semFinalizados) SemearFinalizados();

        // O complemento FABRICADO da nuvem. O próprio número vai na lista de propósito:
        // o Gestor manda assim, e a foto tem que provar que ele não aparece.
        var complemento = detalhe is not null && comNuvem
            ? new DetalheNuvem("ref-" + detalhe, "3121 4455", "0807",
                               "Deixar na portaria e ligar quando chegar", null, "SCHEDULED", "wk-opaco",
                               new[] { "9002", "3340", detalhe })
            : null;

        var codigo = 1;
        var t = new Thread(() =>
        {
            try { codigo = Fotografar(saida, w, h, tema, detalhe, complemento, finalizados, busca, comNuvem); }
            catch (Exception ex) { Console.Error.WriteLine("foto-kds: " + ex); codigo = 1; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { File.Delete(fixture); } catch { /* temporário */ }
        return codigo;
    }

    /// <summary>
    /// O quadro da foto do dono (04/09), reconstruído: FAZENDO com o #5077 de dez
    /// itens ao lado do #9507 de um combo com quatro sabores — o par que mostrava os
    /// dois rodapés em alturas diferentes. Mais um card em cada uma das outras
    /// colunas, para os outros rodapés aparecerem na mesma foto.
    ///
    /// Desde a segunda foto do dono (Gestor "Preparar em 4min" x KDS "17 min"), cada
    /// card também encena um estado do RELÓGIO e do ENTREGADOR: prazo no futuro (normal
    /// e em atenção), prazo vencido, pronto no prazo, pronto atrasado, balcão sem prazo
    /// (decorrido), e os eventos do entregador de "designado" a "chegou". Os prazos
    /// levam folga de segundos porque a foto sai 2 a 3 s depois da semente, e o texto
    /// do card não pode virar de minuto entre uma coisa e outra.
    /// </summary>
    private static void Semear()
    {
        using var cx = Banco.Abrir();
        // a comanda NÃO pode sair de verdade na máquina de quem tira a foto
        Vendas.GravarConfig(cx, Impressoes.Chave(Impressoes.Comanda),
                            Impressoes.Texto(PoliticaImpressao.Perguntar));
        cx.Execute("UPDATE terminal SET api_base = 'http://127.0.0.1:9'");
        // A NUVEM DE VERDADE fica fora da foto. Servicos.Nuvem() lê a URL desta chave
        // (não do api_base acima, que é o servidor fiscal), e a credencial do terminal
        // pode existir na máquina de quem fotografa: sem isto a puxada do entregador
        // chegaria no Supabase real, responderia "ninguém tem evento" para pedidos
        // fabricados e apagaria a foto semeada abaixo. Endereço morto = falha rápida =
        // a foto anterior fica, que é o contrato.
        Vendas.GravarConfig(cx, "supabase_url", "http://127.0.0.1:9");

        var agora = DateTime.Now;
        void Card(string numero, string origem, string status, int minutosAtras,
                  string? cliente, object[] itens, bool retirada = false,
                  DateTime? agendadoPara = null, DateTime? agendadoAte = null, DateTime? preparoAte = null)
        {
            var id = Guid.NewGuid().ToString();
            cx.Execute(
                @"INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json, status,
                                          criado_em, preparo_em, pronto_em, impresso_em, retirada,
                                          agendado, agendado_para, agendado_ate, preparo_ate)
                  VALUES (@id, @o, @r, @n, @c, @j, @s, @t, @pe, @pr, @im, @ret, @ag, @ap, @aa, @pa)",
                new
                {
                    id, o = origem, r = "ref-" + numero, n = numero, c = cliente,
                    j = System.Text.Json.JsonSerializer.Serialize(itens),
                    s = status,
                    t = agora.AddMinutes(-minutosAtras).ToString("o"),
                    pe = status == Kds.Recebido ? null : agora.AddMinutes(-minutosAtras + 2).ToString("o"),
                    pr = status == Kds.Pronto ? agora.AddMinutes(-1).ToString("o") : null,
                    // já impresso: o timer da tela não pode disparar papel na foto
                    im = agora.ToString("o"),
                    ret = retirada ? 1 : 0,
                    ag = agendadoPara is null ? 0 : 1,
                    ap = agendadoPara?.ToString("o"),
                    aa = agendadoAte?.ToString("o"),
                    pa = preparoAte?.ToString("o"),
                });
        }

        static object Item(string nome, int qtd, string? obs = null, string[]? escolhas = null)
            => new { Descricao = nome, Qtd = qtd * 1000, Observacao = obs, Escolhas = escolhas };

        // NA FILA — o AGENDADO de retirada, com tudo que o detalhe sabe mostrar
        // (combo com sabores, observação por item, hora marcada). É o card que o
        // --detalhe fotografa com --nuvem: uma foto só com todas as seções.
        Card("3788", "ifood", Kds.Recebido, 95, "Ana Beatriz Souza", new[]
        {
            Item("Combo Box 4un", 1, "sem granulado no Homer", new[]
            {
                "2x Donut Homer", "1x Donut Morango c/ Ninho", "1x Donut Calabresa",
            }),
            Item("Donut Ninho com Nutella", 2, "embalar separado"),
            Item("Café Coado 300ml", 1),
        }, retirada: true, agendadoPara: agora.AddMinutes(85), agendadoAte: agora.AddMinutes(115));

        // NA FILA — o PEDIDO REAL iFood #6066 (Castelo, 06/10/2026), pelo MESMO ItensDeJson
        // da sincronização: Combo Box 4un com 4 donuts e a Caixinha Extra.
        Card("6066", "ifood", Kds.Recebido, 2, "coxa killer",
            Kds.ItensDeJson(TestesEtiquetaKds.Itens6066).Cast<object>().ToArray());

        // NA FILA: item NORMAL com adicional (nao e combo): o donut fica com o nome.
        Card("7002", "ifood", Kds.Recebido, 1, "Teste Adicional",
            Kds.ItensDeJson("[{\"qtd\":1,\"descricao\":\"Donut Ninho\",\"complements\":[{\"qtd\":1,\"nome\":\"Cobertura extra\"}]}]").Cast<object>().ToArray());

        // NA FILA — prazo daqui a 2 min e pouco: "faltam 2 min" em atenção, com o
        // entregador já a caminho (o pedido nem começou: é o card que mais precisa
        // da linha).
        Card("5610", "ifood", Kds.Recebido, 3, "Marcela Prado", new[]
        {
            Item("Donut Ninho com Nutella", 2),
            Item("Cookie Duplo Chocolate", 1, "sem castanha"),
        }, preparoAte: agora.AddSeconds(170));

        // NA FILA — balcão, sem prazo nenhum: o decorrido de sempre ("5 min").
        Card("42", "balcao", Kds.Recebido, 5, null, new[]
        {
            Item("Cookie Tradicional", 2),
            Item("Café Coado 300ml", 1),
        });

        // FAZENDO — o par da foto. O 5077 tem prazo folgado ("faltam 11 min") e o
        // ENTREGADOR CHEGOU: é a faixa amarela no alto do corpo. É o de dez itens,
        // o que prova que a lista do detalhe ROLA a 1024x768 em vez de cortar.
        Card("5077", "ifood", Kds.Preparando, 14, "Rafael Andrade", new[]
        {
            Item("Donut Ovomaltine", 1),
            Item("Tortinha de Frango com Catupiry", 1),
            Item("Donut Banoffee", 3),
            Item("Donut Ninho", 1),
            Item("Cookie Duplo", 2),
            Item("Pão de Queijo Recheado", 1),
            Item("Donut Doce de Leite", 1),
            Item("Coxinha de Frango", 2),
            Item("Suco de Laranja 500ml", 1),
            Item("Café Coado 300ml", 1),
        }, preparoAte: agora.AddSeconds(11 * 60 + 30));
        // O 9507 estourou o prazo há quase 3 min: "atrasado 3 min" em vermelho, com
        // entregador designado.
        Card("9507", "ifood", Kds.Preparando, 9, "Juliana Ferreira", new[]
        {
            Item("Combo 1 Cookies - 4 unidades", 1, null, new[]
            {
                "Clássicos: 1x Cookie Duplo Chocolate",
                "Clássicos: 1x Cookie Red Velvet",
                "Premium: 1x Cookie Pistache",
                "Premium: 1x Cookie Ninho com Nutella",
            }),
        }, preparoAte: agora.AddSeconds(-170));

        // PRONTO — o relógio congela no pronto: "no prazo" (ficou pronto com folga),
        // e a faixa de baixo vira ENTREGADOR CHEGOU, em amarelo.
        Card("4218", "ifood", Kds.Pronto, 21, "Bruno Carvalho", new[]
        {
            Item("Donut Ovomaltine", 2),
            Item("Tortinha de Frango com Catupiry", 1),
        }, preparoAte: agora.AddMinutes(5));
        // PRONTO — ficou pronto 2 min e meio depois do prazo: "atrasou 3 min",
        // entregador a caminho na faixa de baixo.
        Card("4219", "ifood", Kds.Pronto, 25, "Carla Menezes", new[]
        {
            Item("Donut Homer", 2),
            Item("Cookie Red Velvet", 1),
        }, preparoAte: agora.AddMinutes(-3.5));

        // Os eventos do entregador, semeados direto na foto em memória: a nuvem está
        // num endereço morto, então a puxada do quadro devolve null e a foto FICA (é o
        // contrato de Nucleo.Kds.AtualizarEntregasAsync). O 42 é balcão e o 3788 é
        // retirada: nenhum dos dois é consultado nem ganha linha.
        Kds.AplicarEntregas(new[]
        {
            new EventoEntrega("ref-5610", "GOING_TO_ORIGIN", null),
            new EventoEntrega("ref-5077", "ARRIVED_AT_ORIGIN", null),
            new EventoEntrega("ref-9507", "ASSIGNED", null),
            new EventoEntrega("ref-4218", "ARRIVED_AT_ORIGIN", null),
            new EventoEntrega("ref-4219", "GOING_TO_ORIGIN", null),
            new EventoEntrega("ref-3788", "ARRIVED_AT_ORIGIN", null),   // retirada: tem de ser ignorado
        });
    }

    /// <summary>
    /// OS QUE JÁ SAÍRAM (14/10/2026): um dia de pedidos que saíram do quadro, com um de cada
    /// história (o Gestor despachou, retirada, balcão, cardápio próprio, o iFood cancelou,
    /// expirou, pedido de antes desta versão sem motivo), ontem, e 320 linhas espalhadas
    /// pelos 7 dias para a lista bater no teto de 300. O #4719 é o do --detalhe: combo com
    /// sabores e observação, como o do quadro.
    /// </summary>
    private static void SemearFinalizados()
    {
        using var cx = Banco.Abrir();
        var agora = DateTime.Now;
        static object Item(string nome, int qtd, string? obs = null, string[]? escolhas = null)
            => new { Descricao = nome, Qtd = qtd * 1000, Observacao = obs, Escolhas = escolhas };
        void Saiu(string numero, string origem, string status, DateTime chegou, DateTime? saiu, string? motivo,
                  string? cliente, object[] itens, bool retirada = false, bool soEntregue = false)
            => cx.Execute(
                @"INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json, status, criado_em,
                                          preparo_em, pronto_em, entregue_em, encerrado_em, encerrado_motivo,
                                          retirada, impresso_em)
                  VALUES (@id, @o, @r, @n, @c, @j, @s, @t, @pe, @pr, @en, @ee, @m, @ret, @t)",
                new
                {
                    id = Guid.NewGuid().ToString(), o = origem, r = "ref-" + numero, n = numero, c = cliente,
                    j = System.Text.Json.JsonSerializer.Serialize(itens), s = status,
                    t = chegou.ToString("o"),
                    pe = chegou.AddMinutes(2).ToString("o"),
                    pr = status == Kds.Entregue ? chegou.AddMinutes(14).ToString("o") : null,
                    en = status == Kds.Entregue ? saiu?.ToString("o") : null,
                    // soEntregue: pedido de antes desta versão, só com a hora antiga
                    ee = soEntregue ? null : saiu?.ToString("o"),
                    m = motivo, ret = retirada ? 1 : 0,
                });

        // ── hoje: uma linha de cada história ──
        Saiu("4719", "ifood", Kds.Entregue, agora.AddMinutes(-41), agora.AddMinutes(-12), FinalizadosKds.MotivoGestor,
             "Ana Beatriz Souza", new[]
             {
                 Item("Combo Box 4un", 1, "sem granulado no Homer", new[] { "2x Donut Homer", "1x Donut Morango c/ Ninho", "1x Donut Calabresa" }),
                 Item("Donut Ninho com Nutella", 2, "embalar separado"),
                 Item("Café Coado 300ml", 1),
             });
        Saiu("4711", "ifood", Kds.Entregue, agora.AddMinutes(-48), agora.AddMinutes(-19), FinalizadosKds.MotivoGestor,
             "Patrícia Gomes", new[] { Item("Donut Homer", 2), Item("Cookie Red Velvet", 1) });
        Saiu("4702", "ifood", Kds.Entregue, agora.AddMinutes(-52), agora.AddMinutes(-26), FinalizadosKds.MotivoGestor,
             "Fernanda Lima", new[] { Item("Donut Ovomaltine", 3) }, retirada: true);
        Saiu("77", "balcao", Kds.Entregue, agora.AddMinutes(-35), agora.AddMinutes(-28), FinalizadosKds.MotivoClienteLevou,
             null, new[] { Item("Cookie Tradicional", 2), Item("Café Coado 300ml", 1) });
        Saiu("4698", "ifood", Kds.Cancelado, agora.AddMinutes(-58), agora.AddMinutes(-33), FinalizadosKds.MotivoGestor,
             "Lucas Martins", new[] { Item("Tortinha de Frango com Catupiry", 1), Item("Donut Banoffee", 2) });
        Saiu("CD-2246", "ifood", Kds.Entregue, agora.AddMinutes(-66), agora.AddMinutes(-38), FinalizadosKds.MotivoGestor,
             "Asani Vasconcelos", new[] { Item("Cookie Duplo", 3, "sem castanha") });
        Saiu("4690", "ifood", Kds.Cancelado, agora.AddMinutes(-71), agora.AddMinutes(-66), FinalizadosKds.MotivoIfoodCancelou,
             "Roberto Dias", new[] { Item("Donut Doce de Leite", 4) });
        Saiu("4655", "ifood", Kds.Cancelado, agora.AddMinutes(-290), agora.AddMinutes(-50), FinalizadosKds.MotivoExpirou,
             "Juliana Ferreira", new[] { Item("Pão de Queijo Recheado", 2) });
        // antes desta versão: 'cancelado' sem motivo e sem hora (a nuvem esclarece com --nuvem)
        Saiu("4640", "ifood", Kds.Cancelado, agora.AddMinutes(-95), null, null,
             "Rafael Andrade", new[] { Item("Donut Ninho", 6) });
        // antes desta versão: entregue, só com a hora antiga
        Saiu("4633", "ifood", Kds.Entregue, agora.AddMinutes(-110), agora.AddMinutes(-80), null,
             "Carla Menezes", new[] { Item("Coxinha de Frango", 2), Item("Suco de Laranja 500ml", 1) }, soEntregue: true);
        var nomes = new[] { "Bruno Carvalho", "Marcela Prado", "Thiago Rocha", "Beatriz Nunes", "Gustavo Pires", "Larissa Alves" };
        for (var i = 0; i < 6; i++)
            Saiu((4600 + i * 3).ToString(), "ifood", Kds.Entregue, agora.AddMinutes(-130 - i * 12), agora.AddMinutes(-100 - i * 12),
                 FinalizadosKds.MotivoGestor, nomes[i], new[] { Item("Donut Homer", 1 + i % 3), Item("Cookie Red Velvet", 1) });

        // ── ontem ──
        var ontem = agora.Date.AddDays(-1).AddHours(19);
        for (var i = 0; i < 8; i++)
            Saiu((4400 + i * 7).ToString(), "ifood", Kds.Entregue, ontem.AddMinutes(i * 17), ontem.AddMinutes(i * 17 + 26),
                 FinalizadosKds.MotivoGestor, nomes[i % nomes.Length], new[] { Item("Donut Ovomaltine", 2), Item("Café Coado 300ml", 1) });

        // ── 7 dias: 320 linhas, para a lista bater no teto de 300 ──
        for (var i = 0; i < 320; i++)
        {
            var dia = agora.Date.AddDays(-2 - i % 5).AddHours(10).AddMinutes(i * 2 % 700);
            Saiu((3000 + i).ToString(), "ifood", Kds.Entregue, dia, dia.AddMinutes(25), FinalizadosKds.MotivoGestor,
                 nomes[i % nomes.Length], new[] { Item("Donut Homer", 2) });
        }
    }

    private static int Fotografar(string saida, int w, int h, string tema,
                                  string? detalhe, DetalheNuvem? complemento,
                                  PeriodoFinalizados? finalizados = null, string? busca = null, bool comNuvem = false)
    {
        // mesmos dicionários do Pdv.exe, na mesma ordem (contrato: [0] paleta, [1] estilos)
        static ResourceDictionary Dic(string caminho) => new()
        {
            Source = new Uri($"pack://application:,,,/Pdv;component/{caminho}", UriKind.Absolute),
        };
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(Dic(tema == "claro" ? "Temas/Claro.xaml" : "Temas/Escuro.xaml"));
        app.Resources.MergedDictionaries.Add(Dic("Estilos.xaml"));

        var erros = new List<string>();
        app.DispatcherUnhandledException += (_, e)
            => { erros.Add(e.Exception.GetType().Name + ": " + e.Exception.Message); e.Handled = true; };

        var tela = new Pdv.Telas.Kds("foto");
        // A consulta da nuvem da lista dos que saíram (14/10/2026). Com --nuvem, responde
        // como a pdv_kds_status: o pedido sem motivo foi concluído; os outros, "pronto" (não
        // prova nada, ficam como estão). Sem --nuvem, a de verdade, que bate no endereço
        // morto e devolve null: a lista diz "Sem internet".
        if (comNuvem)
            tela.ConsultaNuvem = ids => Task.FromResult<List<(string OrderId, string Status, string? PreparoAte)>?>(
                ids.Select(id => (id, id == "ref-4640" ? "concluido" : "pronto", (string?)null)).ToList());
        var janela = new Window
        {
            Title = "foto-kds", Width = w, Height = h,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0,
            ShowInTaskbar = false, ShowActivated = false, Content = tela,
        };

        var codigo = 1;
        var etapa = 0;
        var abriuDetalhe = false;
        var abriuLista = false;
        var esperaNuvem = 0;
        var relogio = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        relogio.Tick += (_, _) =>
        {
            etapa++;
            // 3 batidas: a primeira puxada da nuvem morta tem que terminar (ela
            // repinta o quadro no finally) antes de a foto sair.
            if (etapa < 3) return;
            // A busca e a lista dos que saíram antes do detalhe, cada uma com uma batida
            // inteira para assentar (a consulta da nuvem da lista responde nesse meio tempo).
            if (!abriuLista && (busca is not null || finalizados is not null))
            {
                abriuLista = true;
                if (busca is not null) tela.Buscar(busca);
                if (finalizados is { } p) tela.AbrirFinalizados(p);
                return;
            }
            // Batidas a mais para a consulta da nuvem da lista responder. A de verdade bate no
            // endereço morto e volta null, e é ela que escreve "Sem internet" na barra: no
            // Windows a porta fechada leva uns 2 s por tentativa para recusar, então espera ~6 s.
            if (finalizados is not null && esperaNuvem++ < (comNuvem ? 1 : 9)) return;
            // O detalhe abre DEPOIS do quadro pronto e ganha uma batida inteira para
            // o layout assentar (a lista decide se rola só depois de medida).
            if (detalhe is not null && !abriuDetalhe)
            {
                abriuDetalhe = true;
                if (!tela.AbrirDetalhe(detalhe, complemento))
                    erros.Add($"o pedido #{detalhe} não está no quadro fabricado nem entre os que saíram");
                return;
            }
            relogio.Stop();
            try
            {
                var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                bmp.Render((Visual)janela.Content);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                Directory.CreateDirectory(Path.GetDirectoryName(saida)!);
                using (var fs = File.Create(saida)) enc.Save(fs);
                Console.WriteLine($"foto-kds: {saida} ({w}x{h}, tema {tema}" +
                                  (finalizados is null ? "" : $", finalizados {finalizados}") +
                                  (busca is null ? "" : $", busca \"{busca}\"") +
                                  (detalhe is null ? ")" : $", detalhe #{detalhe}{(complemento is null ? "" : " com nuvem")})"));
                foreach (var e in erros.Distinct()) Console.WriteLine("  aviso: " + e);
                codigo = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("foto-kds: " + ex); codigo = 1; }
            janela.Close();
            app.Shutdown();
        };
        janela.Loaded += (_, _) => relogio.Start();
        var limite = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        limite.Tick += (_, _) => { Console.Error.WriteLine("foto-kds: tempo esgotado"); codigo = 3; app.Shutdown(); };
        limite.Start();
        janela.Show();
        app.Run();
        return codigo;
    }
}
