using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// O quadro de preparo do quiosque, no fluxo do KDS da Savassi:
///
///   NA FILA → FAZENDO → PRONTO → (entregue, sai)
///
/// Fonte de verdade é o SQLite local (kds_ticket); a nuvem só ABASTECE a
/// coluna da esquerda com os pedidos do delivery. Sem internet, quem produz
/// continua movendo cards — igual ao resto do PDV.
///
/// Toque avança a etapa. As transições exigem o estado anterior lá no Núcleo:
/// dois dedos rápidos não pulam coluna nem reescrevem carimbo de tempo.
/// </summary>
public partial class Kds : UserControl
{
    public event Action? Voltou;

    private readonly string _loja;
    private DispatcherTimer? _timer;
    private bool _puxando;

    public Kds(string loja)
    {
        InitializeComponent();
        _loja = loja;
        Pintar();
        PintarDestinoComanda();
        _ = PuxarAsync();

        // A cada 10 s: repinta o local E busca a nuvem (o _puxando impede
        // sobreposição). Pedido novo tem que APARECER, não esperar dedo no botão —
        // o Atualizar vira só o "quero agora".
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) =>
        {
            Pintar();
            _ = PuxarAsync();
        };
        _timer.Start();
        Unloaded += (_, _) => { _timer?.Stop(); _timer = null; };
        // A largura do card só existe depois que o WPF mede a tela, e a loja tem
        // monitores de tamanhos diferentes. Aqui é onde a decisão de quantos cards
        // cabem lado a lado ganha o número de verdade.
        SizeChanged += (_, _) => Repartir();
        Aparencia.Mudou += Pintar;
        Unloaded += (_, _) => Aparencia.Mudou -= Pintar;
        Servicos.Sino(loja).Ping += SinoTocou;
        Unloaded += (_, _) => Servicos.Sino(loja).Ping -= SinoTocou;
    }

    private void SinoTocou() => Dispatcher.Invoke(() => _ = PuxarAsync());

    // ── ONDE A COMANDA SAI (29/08 — relato do dono) ─────────────────────────
    // "apos instalado, nao mostra a impressora no PDV na aba de delivery, somente
    // no dash principal". A escolha existia só dentro do assistente de Configuração,
    // no passo "Impressora". Quem opera a cozinha procura isso AQUI, junto dos
    // pedidos — e, não achando, conclui que o PDV não deixa escolher.
    //
    // Este seletor NÃO tem chave própria: grava kds_comanda_separada,
    // kds_comanda_impressora e kds_comanda_papel_mm, as MESMAS do assistente. As
    // regras da escolha moram em Impressao, fora do WPF, onde a suíte alcança.

    /// <summary>
    /// As filas do Windows, pedidas JÁ na abertura do quadro. Enumerar impressora de
    /// rede trava no timeout de cada servidor de impressão fora do ar — segundos por
    /// servidor —, e isso não pode acontecer com o dedo do operador no botão. Quando
    /// ele toca, o resultado quase sempre já está aqui.
    /// </summary>
    private readonly Task<IReadOnlyList<string>> _impressoras = Impressao.ImpressorasAsync();

    /// <summary>
    /// Repinta o botão do cabeçalho com o destino que está valendo AGORA e devolve o
    /// texto. Ler do config toda vez, e não guardar num campo, porque o assistente de
    /// Configuração também escreve nessas chaves: campo em memória seria a terceira
    /// versão da verdade.
    /// </summary>
    private string PintarDestinoComanda()
    {
        using var cx = Banco.Abrir();
        var rotulo = Impressao.RotuloDestinoComanda(
            Vendas.Config(cx, "impressora"), Vendas.Config(cx, "papel_mm"),
            Vendas.Config(cx, "kds_comanda_separada"),
            Vendas.Config(cx, "kds_comanda_impressora"),
            Vendas.Config(cx, "kds_comanda_papel_mm"));
        TxtDestinoComanda.Text = "Comanda: " + rotulo;
        return rotulo;
    }

    private async void TrocarDestinoComanda(object sender, RoutedEventArgs e)
    {
        // Enquanto o spooler não responde, o botão diz o que está fazendo em vez de
        // parecer travado — e fica desligado pra não abrir dois seletores.
        var textoAntes = TxtDestinoComanda.Text;
        BtnDestinoComanda.IsEnabled = false;
        TxtDestinoComanda.Text = "procurando impressoras…";
        IReadOnlyList<string> filas;
        try { filas = await _impressoras; }
        catch { filas = Array.Empty<string>(); }   // spooler parado: sobra "mesma do cupom"
        finally { BtnDestinoComanda.IsEnabled = true; TxtDestinoComanda.Text = textoAntes; }

        string? impCupom, papelCupom, separada, impComanda, papelComanda;
        using (var cx = Banco.Abrir())
        {
            impCupom     = Vendas.Config(cx, "impressora");
            papelCupom   = Vendas.Config(cx, "papel_mm");
            separada     = Vendas.Config(cx, "kds_comanda_separada");
            impComanda   = Vendas.Config(cx, "kds_comanda_impressora");
            papelComanda = Vendas.Config(cx, "kds_comanda_papel_mm");
        }
        var (opcoes, selecionada) = Impressao.OpcoesComanda(filas, impCupom, separada, impComanda);

        // A bobina que aparece pré-escolhida é a da comanda; nunca tendo sido escolhida,
        // é a do cupom — que é a que a comanda usa hoje.
        var escolha = SeletorComanda.Escolher(Window.GetWindow(this)!, opcoes, selecionada,
            AssistenteConfig.IndicePapel(papelComanda ?? papelCupom));
        if (escolha is null) return;
        var (opcao, indicePapel) = escolha.Value;

        var (gravaSeparada, gravaImpressora) = Impressao.GravacaoComanda(opcao);
        using (var cx = Banco.Abrir())
        {
            Vendas.GravarConfig(cx, "kds_comanda_separada", gravaSeparada);
            // null = "não mexer": voltar para "mesma do cupom" não apaga a impressora
            // que a loja escolheu — religar tem que ser um toque, não uma redigitação.
            if (gravaImpressora is not null)
                Vendas.GravarConfig(cx, "kds_comanda_impressora", gravaImpressora);
            // A largura só é decisão quando a comanda tem impressora PRÓPRIA; na mesma
            // do cupom ela É a do cupom, e gravar aqui inventaria a segunda fonte de
            // verdade que este seletor existe justamente para não criar.
            if (gravaSeparada == "1")
                Vendas.GravarConfig(cx, "kds_comanda_papel_mm",
                    AssistenteConfig.TextoPapel(AssistenteConfig.OpcoesPapel()[indicePapel].Mm));
        }

        // Vale AGORA: quem imprime (Servicos.DestinoDaComanda) relê o config a cada
        // comanda. Sem reiniciar o caixa e sem fechar a tela.
        var agora = PintarDestinoComanda();
        TxtStatus.Text = $"Comanda do delivery agora sai em {agora}. Toque no 🖨 de um "
                       + "pedido para conferir no papel.";
    }

    private void Voltar(object sender, RoutedEventArgs e) => Voltou?.Invoke();

    private void Atualizar(object sender, RoutedEventArgs e) => _ = PuxarAsync();

    private async Task PuxarAsync()
    {
        if (_puxando) return;
        _puxando = true;
        TxtIconeAtualiza.Text = "⟲";
        try
        {
            var novos = await Nucleo.Kds.PuxarDaNuvemAsync(Servicos.Nuvem(), _loja);
            // Plural escrito à mão: "3 novo(s)" no quadro é a nossa preguiça
            // aparecendo na parede da cozinha.
            var chegaram = novos switch
            {
                0 => "",
                1 => " · 1 pedido novo",
                _ => $" · {novos} pedidos novos",
            };
            TxtStatus.Text = $"Conectado · {DateTime.Now:HH:mm:ss}{chegaram}";
            if (novos > 0) Alerta.PedidoNovo();
        }
        catch
        {
            // O que muda pra quem está na cozinha: pedido de delivery para de
            // entrar. O quadro em si continua igual — por isso não é "erro".
            TxtStatus.Text = "Sem internet: pedido do delivery não entra. " +
                             $"Confira o wi-fi. {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            _puxando = false;
            TxtIconeAtualiza.Text = "⟳";
            Pintar();
            _ = ImprimirComandasAsync();
        }
    }

    // ── comanda automática (28/08 — pedido do dono) ─────────────────────────
    // Pedido de delivery que chega sai NO PAPEL sem dedo de ninguém, na
    // impressora escolhida na Configuração (chave própria da cozinha —
    // pode ser outra bobina, não a do caixa). Opt-in: nasce desligado.

    /// <summary>
    /// Delega para <see cref="Servicos.ImprimirComandasPendentesAsync"/> — a
    /// impressao nao mora mais aqui porque nao pode depender do quadro aberto.
    /// A tela so mostra o aviso quando o papel nao sai.
    /// </summary>
    private async Task ImprimirComandasAsync()
    {
        var falha = await Servicos.ImprimirComandasPendentesAsync();
        if (falha is null) return;
        TxtStatus.Text = falha + ". Confira papel e impressora e toque no 🖨 do pedido.";
        Alerta.PedidoNovo();   // chama atencao: papel nao saiu
    }

    /// <summary>
    /// A política da comanda, relida a cada pintura do quadro. É ela que decide se o 🖨
    /// do card existe: em "não imprimir" a loja disse que este papel não sai, e um botão
    /// que a loja desligou não pode ficar convidando o toque. Lido uma vez por pintura,
    /// não por card: são vários cards por vez.
    /// </summary>
    private PoliticaImpressao _politicaComanda = PoliticaImpressao.Perguntar;

    // ── pintura do quadro ───────────────────────────────────────────────────
    private void Pintar()
    {
        using (var cxp = Banco.Abrir()) _politicaComanda = Impressoes.Politica(cxp, Impressoes.Comanda);
        _porLinha = CabemPorLinha();

        var abertos = Nucleo.Kds.Abertos();

        // A PREPARAR: agendados no topo (por hora marcada), depois a fila de chegada.
        var fila = Nucleo.Kds.OrdenarFila(abertos.Where(t => t.Status == Nucleo.Kds.Recebido));
        Encher(ColPreparar, fila, faixaAgendados: true);
        Encher(ColPreparo,  abertos.Where(t => t.Status == Nucleo.Kds.Preparando));
        Encher(ColPronto,   abertos.Where(t => t.Status == Nucleo.Kds.Pronto));

        // Conta TICKETS, não filhos do Grid: a faixa "AGENDADOS" é filho e não é pedido.
        TxtQtdPreparar.Text = fila.Count.ToString();
        TxtQtdPreparo.Text  = abertos.Count(t => t.Status == Nucleo.Kds.Preparando).ToString();
        TxtQtdPronto.Text   = abertos.Count(t => t.Status == Nucleo.Kds.Pronto).ToString();
    }

    /// <summary>
    /// Quantas colunas de card cabem DENTRO de cada coluna de status AGORA.
    ///
    /// Era 2 fixo, e era ele o "box pequeno" que o dono reclamou na 0.5.3: a 1024x768
    /// (a tela da Savassi) dois cards por coluna dão ~150 px cada, e quase todo item
    /// quebra em duas linhas. Agora quem decide é a largura de verdade, pela regra de
    /// <see cref="CardKds.CardsPorLinha"/> — que é onde a suíte alcança.
    ///
    /// 1 enquanto o WPF não mediu a tela (a primeira pintura sai do construtor, antes
    /// do layout): é o valor que nunca quebra nome. O <see cref="Repartir"/> repinta
    /// assim que a medida chega.
    /// </summary>
    private int _porLinha = 1;

    /// <summary>
    /// A conta, a partir da largura MEDIDA. As três colunas do quadro têm largura
    /// igual (Width="*"), então uma medida serve para as três.
    /// </summary>
    private int CabemPorLinha() => CardKds.CardsPorLinha(ColPreparar.ActualWidth);

    /// <summary>
    /// Repinta SÓ quando o número de cards por linha mudou. Sem o "só quando mudou",
    /// arrastar a janela repintaria o quadro inteiro (com ida ao banco) a cada pixel.
    /// O valor em si é reassumido a cada <see cref="Pintar"/>, então uma medida que
    /// chegue atrasada se conserta sozinha no próximo ciclo de 10 s.
    /// </summary>
    private void Repartir()
    {
        if (CabemPorLinha() != _porLinha) Pintar();
    }

    /// <summary>
    /// Distribui os cards num Grid de <see cref="_porLinha"/> colunas.
    ///
    /// Grid e nao WrapPanel: no WrapPanel, alinhar os cards exigia altura FIXA —
    /// e altura fixa CORTA pedido comprido (o de 6 itens aparecia com 2). Aqui
    /// cada LINHA cresce ate o maior card dela e os vizinhos esticam junto:
    /// alinhado em cima e embaixo, sem esconder item nenhum.
    ///
    /// Com <paramref name="faixaAgendados"/>, os AGENDADOS (que
    /// <see cref="Nucleo.Kds.OrdenarFila"/> já pôs no topo) ganham uma faixa
    /// "AGENDADOS" acima e os imediatos uma faixa "AGORA": sem isso os dois grupos
    /// se misturavam à vista, e é exatamente a mistura que o dono não quer. Sem
    /// agendado no quadro, nenhuma faixa aparece e a coluna é a de sempre.
    /// </summary>
    private void Encher(Grid coluna, IEnumerable<Ticket> tickets, bool faixaAgendados = false)
    {
        coluna.Children.Clear();
        coluna.RowDefinitions.Clear();
        coluna.ColumnDefinitions.Clear();
        var porLinha = Math.Max(1, _porLinha);
        for (var c = 0; c < porLinha; c++)
            coluna.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lista = tickets.ToList();
        var comFaixas = faixaAgendados && lista.Any(t => t.Agendado);
        var i = 0;   // próxima célula livre; linha = i / porLinha
        void GaranteLinha(int linha)
        {
            while (coluna.RowDefinitions.Count <= linha)
                coluna.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        void Faixa(string texto, string cor)
        {
            if (i % porLinha != 0) i += porLinha - i % porLinha;   // começa em linha nova
            var linha = i / porLinha;
            GaranteLinha(linha);
            var tb = new TextBlock
            {
                Text = texto, FontSize = 11, FontWeight = FontWeights.Bold,
                Margin = new Thickness(6, 6, 6, 1),
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, cor);
            Grid.SetRow(tb, linha);
            Grid.SetColumn(tb, 0);
            Grid.SetColumnSpan(tb, porLinha);
            coluna.Children.Add(tb);
            i += porLinha;                                                         // a faixa é a linha inteira
        }

        string? faixaAtual = null;
        foreach (var t in lista)
        {
            if (comFaixas)
            {
                var faixa = t.Agendado ? "AGENDADOS" : "AGORA";
                if (faixa != faixaAtual)
                {
                    Faixa(faixa, t.Agendado ? "Agendado" : "TextoFraco");
                    faixaAtual = faixa;
                }
            }
            var linha = i / porLinha;
            GaranteLinha(linha);
            var card = Card(t);
            Grid.SetRow(card, linha);
            Grid.SetColumn(card, i % porLinha);
            coluna.Children.Add(card);
            i++;
        }
    }

    /// <summary>
    /// O card é UM botão: com farinha na mão, o alvo é "o pedido", não um
    /// botãozinho dentro dele. O rodapé diz o que o toque faz NESTA coluna.
    /// </summary>
    private Button Card(Ticket t)
    {
        // PRONTO de iFood NAO sai no toque: a coleta e fato do MUNDO — quem
        // declara e o entregador, e a noticia chega pela API (DISPATCHED ->
        // espelho -> reconciliacao). Dedo no card nao inventa coleta. O toque
        // de entrega fica so pro BALCAO, onde nao existe evento externo.
        //
        // O QUARTO CAMPO É "ESPERA" (04/09, foto do dono). Rodapé que pede TOQUE e
        // rodapé que só INFORMA não podem ter a mesma cara. Antes os dois estados de
        // "aguardando" eram TextoFraco sobre VeuElevado — o mesmo cinza dos subitens
        // do combo e quase o mesmo fundo do card, então a faixa não existia à vista.
        // Agora: quem PEDE TOQUE é negrito sobre faixa colorida; quem só ESPERA é
        // semibold sobre a faixa cinza-azulada própria (EsperaFundo). A diferença é
        // vista antes de qualquer palavra ser lida.
        var (acaoTexto, acaoCor, acaoFundo, acaoEspera) = t.Status switch
        {
            Nucleo.Kds.Preparando => ("TOQUE QUANDO FICAR PRONTO", "Ok", "ChipOkFundo", false),
            // Agora o card SABE: a RPC pdv_kds_pedidos devolve `retirada`
            // (cardapio_digital_pedidos.modalidade e ifood_orders.payload->orderType),
            // o SQLite guarda, e o texto para de adivinhar. Antes ele escolhia so
            // pela ORIGEM e dizia "ESPERANDO O ENTREGADOR" em pedido de retirada,
            // onde nao existe entregador nenhum.
            // Em ambos os casos o card sai sozinho: a saida e fato do MUNDO — quem
            // declara e o entregador (via API) ou o balcao entregando ao cliente.
            Nucleo.Kds.Pronto when t.Origem == "ifood" && t.Retirada
                                  => ("AGUARDANDO O CLIENTE RETIRAR", "TextoEspera", "EsperaFundo", true),
            Nucleo.Kds.Pronto when t.Origem == "ifood"
                                  => ("AGUARDANDO O ENTREGADOR", "TextoEspera", "EsperaFundo", true),
            // Balcão pronto TAMBÉM é toque, e por isso ganhou faixa de ação: com o
            // VeuElevado ele era gêmeo visual do "aguardando" logo ao lado, na mesma
            // coluna, e só o texto separava os dois.
            Nucleo.Kds.Pronto     => ("TOQUE QUANDO O CLIENTE LEVAR", "Texto", "ChipOkFundo", false),
            // AGENDADO a preparar: rodapé roxo, mesma instrução — a cozinha pode
            // começar antes da hora se quiser (a comanda é que só sai perto dela).
            Nucleo.Kds.Recebido when t.Agendado
                                  => ("TOQUE PARA COMEÇAR", "Agendado", "ChipAgendadoFundo", false),
            _                     => ("TOQUE PARA COMEÇAR", "Amarelo", "ChipAlertaFundo", false),
        };

        var b = new Button
        {
            Style = (Style)Application.Current.Resources["BotaoBase"],
            MinHeight = 116, Margin = new Thickness(3, 3, 3, 4),
            Padding = new Thickness(0), Tag = t.Id,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        if (t.Agendado)
        {
            // BOX próprio (pedido do dono, 04/09): fundo e borda roxos nos dois temas
            // (Temas/*.xaml). Não é cor de estado (ok/alerta/erro contam tempo): é
            // IDENTIDADE do pedido, e vai com ele por todas as colunas.
            // SetResourceReference, não Resources[]: brush resolvido na criação
            // congela e não segue a troca de tema.
            b.SetResourceReference(Button.BackgroundProperty, "AgendadoFundo");
            b.SetResourceReference(Button.BorderBrushProperty, "Agendado");
            b.BorderThickness = new Thickness(2);
        }

        var raiz = new Grid();
        raiz.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        raiz.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        raiz.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── cabeçalho: número + origem + espera ─────────────────────────────
        // TRES colunas, e a do meio e a unica que encolhe.
        // Antes eram duas — numero+chip numa coluna elastica e relogio+botoes em
        // Auto. Em quadro estreito (o KDS divide a tela em 3, entao a 800x600 cada
        // coluna fica com ~266 px) a elastica era espremida e o NUMERO DO PEDIDO
        // sumia: sobrava so o "#" colado no relogio. O numero e a identidade do
        // pedido — e a unica coisa que o operador grita para o cliente. Ele vai
        // para Auto e nunca corta. Quem cede espaco e o CHIP de origem, decoracao.
        var cab = new Grid { Margin = new Thickness(11, 8, 11, 3) };
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var esq = new StackPanel { Orientation = Orientation.Horizontal };
        var numero = new TextBlock
        {
            Text = "#" + t.Numero, FontSize = 23, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        numero.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
        Grid.SetColumn(numero, 0);
        cab.Children.Add(numero);

        var chips = new StackPanel { Orientation = Orientation.Horizontal, ClipToBounds = true };
        chips.Children.Add(Chip(t.Origem == "ifood" ? "iFOOD" : "BALCAO",
                                t.Origem == "ifood" ? "Ciano" : "Rosa"));
        // TAG do agendado ao lado da origem: é a segunda coisa que o olho lê no
        // card, depois do número — e é o que diz "este não é para agora".
        if (t.Agendado)
            chips.Children.Add(Chip("AGENDADO", "Agendado", "ChipAgendadoFundo", "ChipAgendadoBorda"));
        Grid.SetColumn(chips, 1);
        cab.Children.Add(chips);

        // O relógio é O MESMO do Gestor do iFood: o PRAZO (dueAt). "12 min"
        // = falta isso pro prometido; "+3 min" = estourou. Pedido sem prazo
        // (balcão) volta ao decorrido. Dois painéis, um relógio só.
        string txtEspera; string corEspera;
        if (t.Agendado && t.AgendadoRestante is { } falta)
        {
            // O relógio do AGENDADO é a hora MARCADA — não o prazo do iFood nem a
            // espera desde a chegada (que às 09:00 diria "540 min" para um pedido
            // das 18:00 e pintaria o card de vermelho o dia inteiro). Longe (ou já
            // pronto): a hora, em roxo. Na última hora: contagem, em amarelo.
            // Passou da hora sem ficar pronto: vermelho.
            var m = (int)falta.TotalMinutes;
            if (m > 60 || t.Status == Nucleo.Kds.Pronto)
            {
                txtEspera = t.AgendadoPara!.Value.ToString("HH:mm");
                corEspera = "Agendado";
            }
            else if (m >= 0) { txtEspera = $"em {m} min"; corEspera = "Amarelo"; }
            else { txtEspera = $"+{-m} min"; corEspera = "Erro"; }
        }
        else if (t.PrazoRestante is { } prazo)
        {
            var m = (int)prazo.TotalMinutes;
            if (m >= 0)
            {
                txtEspera = $"{m} min";
                corEspera = m > 5 ? "Ok" : "Amarelo";
            }
            else
            {
                txtEspera = $"+{-m} min";
                corEspera = "Erro";
            }
        }
        else
        {
            var min = (int)t.Espera.TotalMinutes;
            txtEspera = min < 1 ? "agora" : $"{min} min";
            corEspera = min < 10 ? "Ok" : min < 20 ? "Amarelo" : "Erro";
        }
        var espera = new TextBlock
        {
            Text = txtEspera,
            FontSize = 14, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        espera.SetResourceReference(TextBlock.ForegroundProperty, corEspera);
        Grid.SetColumn(espera, 0);

        var dir = new StackPanel { Orientation = Orientation.Horizontal };
        dir.Children.Add(espera);
        // ── VOLTAR UMA ETAPA (04/09, primeira reclamação do dono na 0.5.3) ──
        // "pedido 9507 e 5077 foi clicado marcar fazendo porem nao tem como desfazer
        // caso tenha clicado errado". O botão EXISTIA desde a 0.4.x, e some: a 1024x768
        // o cabeçalho do card não cabia na largura de ~150 px e ↩ e 🖨 eram desenhados
        // PARA FORA do card. Provado no --foto-kds (a 1920 os dois aparecem; a 1024,
        // nenhum). O conserto de verdade é a largura (CardKds.CardsPorLinha); aqui só
        // o botão passa a valer também em PRONTO.
        //
        // Sem confirmação, de propósito: pedir "tem certeza?" para DESFAZER é pedir
        // confirmação da correção de um erro. O que é grave é avançar, e avançar já
        // pergunta. E as duas voltas não são iguais: FAZENDO → NA FILA é local, PRONTO
        // → FAZENDO pode já ter acionado o entregador. Quem sabe isso é o núcleo
        // (DesfazerKds), e ele recusa com motivo em vez de mentir que desfez.
        if (t.Status is Nucleo.Kds.Preparando or Nucleo.Kds.Pronto)
        {
            var desfaz = new Button
            {
                Content = "↩", FontSize = 15, MinHeight = 46, MinWidth = 46,
                // 8 e não 10: o cabeçalho do card é a linha mais disputada da tela.
                Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 0, 8, 0),
                Style = (Style)Application.Current.Resources["BotaoBase"],
                ToolTip = t.Status == Nucleo.Kds.Preparando
                    ? "Devolver para a fila" : "Voltar para FAZENDO",
            };
            desfaz.Click += (_, e) =>
            {
                // Click e roteado e BORBULHA: sem isto o clique segue pro card e
                // abre a confirmacao de PRONTO - exatamente o bug que o dono viu.
                e.Handled = true;
                // Textos de uma linha: quem lê está de pé, com pedido na mão.
                TxtStatus.Text = DesfazerKds.Voltar(t.Id) switch
                {
                    VoltaKds.IFoodJaAvisado => $"O #{t.Numero} não volta: o iFood já foi avisado.",
                    VoltaKds.ForaDaEtapa => $"O #{t.Numero} já tinha mudado de coluna.",
                    _ when t.Status == Nucleo.Kds.Pronto => $"Pedido #{t.Numero} voltou para FAZENDO.",
                    _ => TxtStatus.Text,
                };
                Pintar();
            };
            dir.Children.Add(desfaz);
        }
        if (t.Origem == "ifood" && Impressoes.MostraBotaoComanda(_politicaComanda))
        {
            // Reimprimir a comanda: papel atolou/acabou, ou a automática falhou.
            // Imprime DIRETO (sem claim — reimpressão é decisão de gente).
            // Este 🖨 é TAMBÉM o "perguntar na tela" da comanda: com a política em
            // perguntar ele é o único jeito de tirar o papel, e por isso continua
            // visível na política automática (socorro de quando ela falha). Só a
            // política "não imprimir" o apaga.
            var imprime = new Button
            {
                Content = "🖨", FontSize = 16, MinHeight = 46, MinWidth = 46,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)Application.Current.Resources["BotaoBase"],
                ToolTip = "Tirar a comanda deste pedido no papel de novo",
            };
            imprime.Click += async (_, e) =>
            {
                e.Handled = true; // não deixa o clique borbulhar e avançar etapa
                // MESMO destino da comanda automática (Servicos.DestinoDaComanda): a
                // reimpressão tem que sair na bobina em que a original sairia, senão o
                // 🖨 vira "saiu, mas noutra impressora" — que é pior que não sair.
                Impressao.Destino destino;
                using (var cx = Banco.Abrir()) destino = Servicos.DestinoDaComanda(cx);
                var erro = await Impressao.ImprimirTextoAsync(
                    $"Comanda cozinha #{t.Numero} (manual)",
                    new[] { Nucleo.Kds.ComandaLinhas(t, Nucleo.Kds.ColunasComanda(destino.Papel.Colunas)) },
                    destino);
                // Na falha, primeiro o que fazer; a causa técnica vai no fim,
                // entre parênteses, pra quem for atrás da impressora.
                TxtStatus.Text = erro is null
                    ? $"Comanda do #{t.Numero} saiu na impressora"
                    : $"A comanda do #{t.Numero} não saiu. Confira papel e impressora " +
                      $"e toque no 🖨 de novo. ({erro})";
            };
            dir.Children.Add(imprime);
        }
        Grid.SetColumn(dir, 2);
        cab.Children.Add(dir);
        raiz.Children.Add(cab);

        // ── corpo: cliente + itens ──────────────────────────────────────────
        var corpo = new StackPanel { Margin = new Thickness(11, 0, 11, 4) };
        if (t.Agendado && t.AgendadoPara is { } marcado)
        {
            // O AVISO que o dono pediu ("agendado pra 10h"), em texto corrido e
            // roxo: a tag diz o quê, esta linha diz QUANDO. Mesmo texto da comanda.
            var aviso = new TextBlock
            {
                Text = "Agendado para " + Nucleo.Kds.TextoHorario(marcado, t.AgendadoAte, DateTime.Now),
                FontSize = 14, FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3),
            };
            aviso.SetResourceReference(TextBlock.ForegroundProperty, "Agendado");
            corpo.Children.Add(aviso);
        }
        if (t.Cliente is { Length: > 0 })
        {
            var cli = new TextBlock
            {
                Text = t.Cliente, FontSize = 12, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 3),
            };
            cli.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
            corpo.Children.Add(cli);
        }
        // ── os três níveis do corpo (04/09, foto do dono) ───────────────────
        // 1. ITEM: quantidade em NEGRITO + nome em peso normal, cor Texto, 16 px.
        //    O negrito só na quantidade porque é ela que o cozinheiro varre antes
        //    de ler nome nenhum; o nome inteiro em negrito não destacava nada.
        // 2. SUBITEM: 14 px em TextoSubItem (cor PRÓPRIA, não mais o cinza do
        //    rodapé), recuado por MARGIN e marcado por uma régua vertical.
        // 3. OBSERVAÇÃO: amarelo itálico, como já era.
        //
        // O prefixo "    - " morreu aqui. Ele gastava ~6 caracteres de largura em
        // card de ~250 px, e era metade da causa das quebras que a foto mostrava
        // ("1× Combo 1 Cookies - 4 / unidades"). Recuo é geometria, não texto.
        foreach (var i in t.Itens)
        {
            var principal = CardKds.ItemPrincipal(i);
            var linha = new TextBlock
            {
                FontSize = 16, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 1),
            };
            linha.Inlines.Add(new Run(principal.Qtd + " ") { FontWeight = FontWeights.Bold });
            linha.Inlines.Add(new Run(principal.Nome));
            linha.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
            corpo.Children.Add(linha);
            // As escolhas do combo aparecem aqui pelo mesmo motivo da comanda: sem
            // elas o card diz "1× Combo Box" e o cozinheiro não sabe o que fazer.
            // Elas vão TODAS dentro de uma régua só: a linha vertical amarra o grupo
            // ao item de cima e diz "isto é o de dentro daquele" sem gastar largura.
            if (i.Escolhas is { Count: > 0 })
            {
                var dentro = new StackPanel();
                foreach (var esc in i.Escolhas)
                {
                    var s = CardKds.SubItem(esc);
                    var sub = new TextBlock
                    {
                        FontSize = 14, TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 1),
                    };
                    // Quantidade em semibold aqui também: o mesmo olhar que varre as
                    // quantidades do item varre as do combo. Semibold e não bold para
                    // o subitem não competir com o item que ele pertence.
                    if (s.Qtd.Length > 0)
                        sub.Inlines.Add(new Run(s.Qtd + " ") { FontWeight = FontWeights.SemiBold });
                    sub.Inlines.Add(new Run(s.Nome));
                    sub.SetResourceReference(TextBlock.ForegroundProperty, "TextoSubItem");
                    dentro.Children.Add(sub);
                }
                var regua = new Border
                {
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Margin = new Thickness(3, 1, 0, 3),
                    Padding = new Thickness(7, 0, 0, 0),
                    Child = dentro,
                };
                // A régua fica em TextoFraco de propósito: o cinza de legenda serve
                // bem para um traço de 2 px, e nesse papel ele não volta a se
                // confundir com texto nenhum.
                regua.SetResourceReference(Border.BorderBrushProperty, "TextoFraco");
                corpo.Children.Add(regua);
            }
            if (i.Observacao is { Length: > 0 })
            {
                var obs = new TextBlock
                {
                    Text = "· " + i.Observacao, FontSize = 12, FontStyle = FontStyles.Italic,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 0, 2),
                };
                obs.SetResourceReference(TextBlock.ForegroundProperty, "Amarelo");
                corpo.Children.Add(obs);
            }
        }
        Grid.SetRow(corpo, 1);
        raiz.Children.Add(corpo);

        // ── rodapé: o que o toque faz aqui ──────────────────────────────────
        var rodape = new Border { Padding = new Thickness(8, 8, 8, 9), CornerRadius = new CornerRadius(0, 0, 13, 13) };
        rodape.SetResourceReference(Border.BackgroundProperty, acaoFundo);
        var acao = new TextBlock
        {
            Text = acaoTexto, FontSize = 12,
            // Peso é o segundo sinal, além da cor e do fundo: negrito = o dedo
            // resolve; semibold = só o mundo resolve (o entregador, o cliente).
            FontWeight = acaoEspera ? FontWeights.SemiBold : FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            // Sem isto o texto vazava dos DOIS lados do card e o operador lia
            // "NDO O ENTREGADOR · sai". Num quadro de 3 colunas a 800x600 o card
            // tem ~250 px: instrucao de operacao tem que caber, nem que quebre.
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
        };
        acao.SetResourceReference(TextBlock.ForegroundProperty, acaoCor);
        rodape.Child = acao;
        Grid.SetRow(rodape, 2);
        raiz.Children.Add(rodape);

        b.Content = raiz;
        b.Click += (_, e) =>
        {
            // CINTO além do e.Handled do botão interno: Click borbulha, e o
            // Source denuncia de onde o clique nasceu — clique que não nasceu
            // NO card não avança etapa. (O dono viu o desfazer abrir o pop-up
            // de PRONTO em tela touch; nunca mais.)
            if (!ReferenceEquals(e.Source, b)) return;
            switch (t.Status)
            {
                case Nucleo.Kds.Recebido:
                    Nucleo.Kds.Assumir(t.Id);
                    break;

                case Nucleo.Kds.Preparando:
                    // PRONTO é declaração para fora: quando a ponte ligar o
                    // readyToPickup, isso aciona o entregador no iFood. Toque
                    // acidental aqui vira motoboy na porta sem donut na caixa —
                    // por isso a confirmação explícita.
                    var dono = Window.GetWindow(this)!;
                    var delivery = t.Origem == "ifood";
                    // O botão diz a consequência, não "Sim": quem toca sem ler
                    // ainda lê "Chamar o entregador" no dedo.
                    var aviso = delivery
                        ? $"O pedido #{t.Numero} vai para coleta e o entregador é chamado agora. " +
                          "Já está tudo embalado?"
                        : $"O pedido #{t.Numero} já pode ir para o cliente?";
                    if (Dialogo.Confirmar(dono, "Pedido pronto", aviso,
                                          delivery ? "Chamar entregador" : "Marcar pronto",
                                          "Ainda não"))
                        Nucleo.Kds.Liberar(t.Id);
                    break;

                case Nucleo.Kds.Pronto:
                    // balcao: cliente retirou no caixa. iFood: nada — a API manda.
                    if (t.Origem != "ifood") Nucleo.Kds.Entregar(t.Id);
                    break;
            }
            Pintar();
        };
        return b;
    }

    /// <param name="fundo">Chave do pincel de fundo; ausente = deduzido da cor (info/erro).</param>
    /// <param name="borda">Chave do pincel da borda; idem.</param>
    private FrameworkElement Chip(string texto, string cor, string? fundo = null, string? borda = null)
    {
        var tb = new TextBlock { Text = texto, FontSize = 10, FontWeight = FontWeights.Bold };
        tb.SetResourceReference(TextBlock.ForegroundProperty, cor);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(7), Padding = new Thickness(6, 1, 6, 2),
            // A folga da DIREITA (6) separa o chip do relógio. Ela mora na margem de
            // propósito: a coluna do chip é a elástica e é recortada (ClipToBounds), então
            // em card apertado o corte come esta folga ANTES de comer letra do chip.
            Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = tb, BorderThickness = new Thickness(1),
        };
        chip.SetResourceReference(Border.BackgroundProperty, fundo ?? (cor == "Ciano" ? "ChipInfoFundo" : "ChipErroFundo"));
        chip.SetResourceReference(Border.BorderBrushProperty, borda ?? (cor == "Ciano" ? "ChipInfoBorda" : "ChipErroBorda"));
        return chip;
    }
}
