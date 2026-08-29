using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Telas;

public sealed record Produto(string Id, string? Plu, string Nome, string Categoria, Dinheiro Preco,
    string Unidade, string? Ncm, string? Cest, string? Csosn, int Origem, string? Foto);

public sealed class ItemComanda
{
    public required Produto Produto { get; init; }
    public Quantidade Qtd { get; set; } = Quantidade.Um;
    public Dinheiro Total => Produto.Preco.VezesQtd(Qtd.Milesimos);
}

/// <summary>
/// Tela de venda: categorias à esquerda, produtos no meio, a conta à direita.
///
/// Decisões que vieram do levantamento de PDV real:
///  - item novo entra no TOPO da comanda (o operador confere o que acabou de bipar);
///  - tocar de novo no mesmo produto INCREMENTA a linha em vez de criar outra;
///  - total em fonte grande e sempre visível;
///  - busca com foco permanente, pra leitor de código de barras funcionar sem clique;
///  - nada de diálogo bloqueante no caminho de alta frequência.
/// </summary>
public partial class Venda : UserControl
{
    private readonly Operador _operador;
    private readonly Sessao _sessao;
    private readonly List<Produto> _catalogo = new();
    private readonly List<ItemComanda> _comanda = new();
    private readonly Dictionary<string, int> _quantosPorCategoria = new();

    // Cortesia aplicada: cupom de erro de pedido resgatado no caixa. A cobertura é
    // por PRODUTO (nome normalizado → unidades grátis): reduz o que é cobrado e o
    // que vai pra nota fiscal. Cortesia é BRINDE, não venda — os itens cobertos
    // saem da NFC-e (evita o vDesc, que a EC2 ainda não distribui por item).
    private readonly Cortesias _cortesias = new();
    private string? _cortesiaCodigo;
    private readonly Dictionary<string, int> _cortesiaCobertura = new(StringComparer.Ordinal);
    private string _categoriaAtual = "";
    private string _loja = "";
    private string? _lojaId;
    private bool _modoLista;
    private double _larguraGrade;
    private DispatcherTimer? _relogio;

    public event Action? Deslogou;
    public event Action? FechouCaixa;
    public event Action? PediuKds;
    public event Action? PediuChat;
    public event Action? PediuConfig;

    /// <summary>
    /// Quantas colunas a grade de produtos usa. Calculado pela largura real da área,
    /// não fixado: com número fixo os cartões ficavam grudados e sobrava um vão morto
    /// à direita em tela larga.
    /// </summary>
    public static readonly DependencyProperty ColunasProperty =
        DependencyProperty.Register(nameof(Colunas), typeof(int), typeof(Venda), new PropertyMetadata(3));

    public int Colunas { get => (int)GetValue(ColunasProperty); set => SetValue(ColunasProperty, value); }

    /// <summary>2 por linha no dia a dia; 3 por linha (card mini) quando a rede
    /// tem categoria demais — o dono viu 23 no teste SaaS e pediu densidade.</summary>
    public static readonly DependencyProperty ColunasCategoriasProperty =
        DependencyProperty.Register(nameof(ColunasCategorias), typeof(int), typeof(Venda), new PropertyMetadata(2));

    public int ColunasCategorias
    { get => (int)GetValue(ColunasCategoriasProperty); set => SetValue(ColunasCategoriasProperty, value); }

    private bool _categoriaMini;

    public Venda(Operador operador, Sessao sessao)
    {
        InitializeComponent();
        _operador = operador;
        _sessao = sessao;
        TxtOperador.Text = operador.Nome;
        TxtInicial.Text = operador.Nome.Trim().Length > 0 ? operador.Nome.Trim()[..1].ToUpperInvariant() : "?";
        TxtSessao.Text = $"Caixa aberto às {sessao.AberturaEm:HH:mm} · {DateTime.Parse(sessao.BusinessDate):dd/MM}";
        CarregarIdentificacao();
        PintarModo();
        PintarBotaoTema();
        CarregarCatalogo();
        // O que esta tela desenha em C# (cards de categoria/produto, comanda) não
        // segue DynamicResource — quando o tema troca, os pintores rodam de novo.
        Aparencia.Mudou += TemaMudou;
        // sino: pedido novo chega por websocket e adianta a puxada — o tick de
        // 30 s continua embaixo como rede de segurança
        Servicos.Sino(_loja ?? "").Ping += SinoTocou;
        Servicos.Sino(_loja ?? "").CatalogoMudou += CatalogoTocou;
        Loaded += (_, _) => { IniciarRelogio(); PintarPendencias(); ProcurarAtualizacao(); OferecerRascunho(); };
        Unloaded += (_, _) =>
        {
            _relogio?.Stop(); _relogio = null;
            Aparencia.Mudou -= TemaMudou;
            Servicos.Sino(_loja ?? "").Ping -= SinoTocou;
            Servicos.Sino(_loja ?? "").CatalogoMudou -= CatalogoTocou;
        };
    }

    private int _batidasKds;
    private bool _puxandoKds;

    private bool _sincronizandoPainel;

    /// <summary>
    /// O painel publicou (catálogo ou promoção): baixa e recarrega SOZINHO.
    /// É o "webhook" do catálogo — ninguém mais precisa tocar em Sincronizar
    /// pra promoção de quinta valer na quinta.
    /// </summary>
    private void CatalogoTocou() => Dispatcher.Invoke(() =>
    {
        if (_sincronizandoPainel) return;
        _sincronizandoPainel = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await Sincronizacao.ExecutarAsync(
                    Servicos.Nuvem(), Servicos.Guarda(), Servicos.Dreno(), null)
                    .ConfigureAwait(false);
            }
            catch { /* o botão Sincronizar continua existindo */ }
            finally
            {
                _sincronizandoPainel = false;
                Dispatcher.Invoke(() =>
                {
                    RecarregarCatalogo();
                    TxtToastKds.Text = "Cardápio atualizado";
                    ToastKds.Visibility = Visibility.Visible;
                    _toastSome?.Stop();
                    _toastSome = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                    _toastSome.Tick += (_, _) => { ToastKds.Visibility = Visibility.Collapsed; _toastSome?.Stop(); };
                    _toastSome.Start();
                });
            }
        });
    });

    /// <summary>Sino (thread de fundo) → puxada imediata + notificação na UI.</summary>
    private void SinoTocou() => Dispatcher.Invoke(() =>
    {
        if (_puxandoKds) return;
        _puxandoKds = true;
        _ = Nucleo.Kds.PuxarDaNuvemAsync(Servicos.Nuvem(), _loja)
            .ContinueWith(tt =>
            {
                _puxandoKds = false;
                // A comanda sai com o pedido, mesmo com o KDS fechado.
                _ = Servicos.ImprimirComandasPendentesAsync();
                if (tt.Status == TaskStatus.RanToCompletion && tt.Result > 0)
                    Dispatcher.Invoke(() => NotificarPedidoNovo(tt.Result));
            });
    });

    private void TemaMudou()
    {
        PintarBotaoTema();
        PintarModo();
        // Os cards de categoria carregam degradê e glow com FATORES do tema,
        // resolvidos na criação — repintar não basta, é reconstruir. São ~10
        // botões; reconstruir é imperceptível e elimina a classe inteira de
        // "sobrou cor do tema velho".
        var cats = ListaCategorias.Items.Cast<Button>().Select(b => (string)b.Tag!).ToList();
        ListaCategorias.Items.Clear();
        foreach (var c in cats) ListaCategorias.Items.Add(BotaoCategoria(c));
        RepintarCategorias();
        PintarProdutos();
        PintarComanda();
        PintarPendencias();
    }

    private void PintarBotaoTema() =>
        BtnTema.Content = Aparencia.Atual == ModoTema.Claro ? "🌙 modo escuro" : "☀ modo claro";

    /// <summary>
    /// Atalho do rodapé: vira o tema AGORA e grava a escolha — inclusive por cima
    /// do modo automático, porque quem está no balcão sabe mais que o relógio.
    /// </summary>
    private void AbrirKds(object sender, RoutedEventArgs e) => PediuKds?.Invoke();

    private void AbrirChat(object sender, RoutedEventArgs e) => PediuChat?.Invoke();

    private DispatcherTimer? _toastSome;

    private void NotificarPedidoNovo(int quantos)
    {
        TxtToastKds.Text = quantos == 1 ? "Pedido novo do iFood" : $"{quantos} pedidos novos do iFood";
        ToastKds.Visibility = Visibility.Visible;
        Alerta.PedidoNovo();

        _toastSome?.Stop();
        _toastSome = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _toastSome.Tick += (_, _) => { ToastKds.Visibility = Visibility.Collapsed; _toastSome?.Stop(); };
        _toastSome.Start();
    }

    private void AbrirKdsPeloToast(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ToastKds.Visibility = Visibility.Collapsed;
        PediuKds?.Invoke();
    }

    private void AlternarTema(object sender, RoutedEventArgs e)
    {
        var novo = Aparencia.Atual == ModoTema.Claro ? ModoTema.Escuro : ModoTema.Claro;
        using var cx = Banco.Abrir();
        Vendas.GravarConfig(cx, "tema", novo == ModoTema.Claro ? "claro" : "escuro");
        Caixa.Auditar(cx, null, "tema_trocado", _operador.Id, null,
            novo == ModoTema.Claro ? "claro (manual)" : "escuro (manual)");
        Aparencia.Aplicar(novo);
    }

    /// <summary>Byte do tema atual (alphas dos véus de categoria).</summary>
    private static byte RB(string chave) => (byte)Application.Current.Resources[chave];

    /// <summary>Double do tema atual (fatores de degradê, opacidade de glow).</summary>
    private static double RD(string chave) => (double)Application.Current.Resources[chave];

    /// <summary>
    /// "1 nota" / "3 notas". Existe para o operador nunca ler "1 nota(s)": com fila
    /// no balcão, parêntese de plural é ruído que faz reler a frase inteira.
    /// </summary>
    private static string Conta(long quantos, string singular, string plural)
        => $"{quantos} {(quantos == 1 ? singular : plural)}";

    /// <summary>Nome da loja no topo e a ficha do terminal no rodapé.</summary>
    private void CarregarIdentificacao()
    {
        using var cx = Banco.Abrir();
        var t = cx.QueryFirstOrDefault("SELECT loja_nome, serie_nfce, ambiente FROM terminal LIMIT 1");
        var loja = (t?.loja_nome as string) ?? "PDV";
        _loja = loja;
        _lojaId = t?.loja_id as string;
        TxtLoja.Text = loja.ToUpperInvariant();

        // Logo do cliente, se ele largou um arquivo na pasta de dados. Sem isso o
        // nome da loja já resolve — o que não dá é embutir a marca de um cliente
        // num executável que vai rodar na loja de outro.
        foreach (var nome in new[] { "logo.png", "logo.jpg" })
        {
            // System.IO.Path por extenso: System.Windows.Shapes.Path também está no escopo
            var caminho = System.IO.Path.Combine(Fotos.PastaDados, nome);
            if (!System.IO.File.Exists(caminho)) continue;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(caminho);
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.DecodePixelHeight = 80;
                img.EndInit();
                img.Freeze();
                ImgLogo.Source = img;
                ImgLogo.Visibility = Visibility.Visible;
                TxtLoja.Visibility = Visibility.Collapsed;
                PontoLogo.Visibility = Visibility.Collapsed;
                break;
            }
            catch { /* logo ilegível não pode impedir a loja de vender */ }
        }

        var versao = typeof(Venda).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var serie = t is null ? "?" : Convert.ToString(t.serie_nfce);
        // Homologação precisa gritar: nota de teste não vale nada, e já vi caixa
        // rodando o dia inteiro em teste sem ninguém perceber.
        var amb = t is not null && Convert.ToInt64(t.ambiente) == 2 ? "  ·  ⚠ MODO TESTE — as notas não valem" : "";
        TxtRodape.Text = $"{loja}  ·  Série {serie}  ·  Versão {versao}{amb}";
        if (amb.Length > 0) TxtRodape.SetResourceReference(TextBlock.ForegroundProperty, "Amarelo");
    }

    private void IniciarRelogio()
    {
        void Bater()
        {
            TxtRelogio.Text = DateTime.Now.ToString("HH:mm  ·  dd/MM/yyyy");
            var online = NetworkInterface.GetIsNetworkAvailable();
            // Referência por CHAVE, não brush resolvido: quando o tema troca, o
            // WPF re-resolve sozinho — sem isso o chip fica com a cor do tema velho.
            var corChave = online ? "Ok" : "Erro";
            // De onde a nota sai importa pro operador saber: muda a série impressa no
            // cupom, e nota do agente local ainda não aparece na 2ª via do servidor.
            var caminho = Servicos.CaminhoDoEmissor() switch
            {
                "agente" => "  ·  nota sai deste PC",
                "nenhum" => "  ·  ⚠ SEM NOTA FISCAL",
                _ => "",
            };
            // Sem conta de nuvem, o XML da nota fica SÓ neste PC: não entra na 2ª via
            // nem no extrato do contador, e a guarda de 5 anos passa a depender de um
            // HD de loja. Isso precisa estar à vista, não escondido numa tela de config.
            if (!Servicos.TemContaDeNuvem()) caminho += "  ·  ⚠ NOTAS SÓ NESTE PC";
            TxtRede.Text = (online ? "ONLINE" : "OFFLINE") + caminho;
            TxtRede.SetResourceReference(TextBlock.ForegroundProperty, corChave);
            LuzRede.SetResourceReference(Shape.FillProperty, corChave);
            ChipRede.SetResourceReference(Border.BackgroundProperty, online ? "ChipOkFundo" : "ChipErroFundo");
            ChipRede.SetResourceReference(Border.BorderBrushProperty, online ? "ChipOkBorda" : "ChipErroBorda");

            // Badge do KDS: o que está esperando produção, à vista de quem cobra.
            var pendentes = Nucleo.Kds.Pendentes();
            TxtBadgeKds.Text = pendentes.ToString();
            BadgeKds.Visibility = pendentes > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Delivery desce a cada 4 batidas (60 s) mesmo com o KDS fechado: o
            // pedido do iFood precisa acender o badge ANTES de alguém abrir a tela.
            if (++_batidasKds % 2 == 0 && Servicos.TemContaDeNuvem() && !_puxandoKds)
            {
                _puxandoKds = true;
                _ = Nucleo.Kds.PuxarDaNuvemAsync(Servicos.Nuvem(), _loja)
                    .ContinueWith(tt =>
                    {
                        _puxandoKds = false;
                        // A comanda sai com o pedido, mesmo com o KDS fechado.
                        _ = Servicos.ImprimirComandasPendentesAsync();
                        // Pedido novo com o CAIXA aberto: toast + som. Ninguém fica
                        // olhando badge pequeno com fila no balcão.
                        if (tt.Status == TaskStatus.RanToCompletion && tt.Result > 0)
                            Dispatcher.Invoke(() => NotificarPedidoNovo(tt.Result));
                    });
            }

            // Promoção liga/desliga pelo RELÓGIO (meia-noite, janela de hora):
            // repinta a grade só quando algum preço efetivo mudou de verdade.
            var assinatura = AssinaturaPromos();
            if (assinatura != _assinaturaPromo)
            {
                _assinaturaPromo = assinatura;
                _promoVitrine = Nucleo.Promocoes.ProdutosEmPromocao(_promos, DateTime.Now);
                PintarProdutos();
            }

            // Modo automático: reavalia no mesmo tick do relógio. NUNCA com comanda
            // aberta — a tela mudar de cara no meio da venda desorienta o operador
            // (e Aplicar() já é no-op quando o tema não muda).
            if (_comanda.Count == 0)
            {
                using var cxTema = Banco.Abrir();
                if (Vendas.Config(cxTema, "tema") == "auto")
                    Aparencia.Aplicar(Aparencia.Resolver(cxTema));
            }

            // Versão nova: pega carona no relógio, mas com trava de 6 h lá dentro —
            // o caixa fica aberto o dia inteiro e ninguém reinicia para descobrir
            // que saiu release. Uma requisição de 200 bytes três vezes por dia.
            ProcurarAtualizacao();
        }
        Bater();
        _relogio = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _relogio.Tick += (_, _) => Bater();
        _relogio.Start();
    }

    /// <summary>
    /// PUXA do painel para o caixa: catálogo, preço, promoções e operadores.
    ///
    /// A outra direção (venda → painel) NÃO depende mais deste botão: ela sobe
    /// sozinha assim que a venda fecha, e a fila é varrida a cada 45 s. Enquanto
    /// dependia daqui, um dia sem ninguém apertar o botão virava um painel
    /// mostrando R$ 0,00 de faturamento.
    ///
    /// A subida continua acontecendo aqui também — de graça, já que é a mesma
    /// fila — e serve de rede: se algo ficou preso, o operador vê o número.
    /// </summary>
    private async void Sincronizar(object sender, RoutedEventArgs e)
    {
        var dono = Window.GetWindow(this)!;
        BtnSync.IsEnabled = false;
        var girando = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        var passo = 0;
        var quadros = new[] { "⟳", "⟲" };
        girando.Tick += (_, _) => TxtIconeSync.Text = quadros[++passo % quadros.Length];
        girando.Start();

        var etapa = "";
        var andamento = new Progress<string>(t => etapa = t);
        try
        {
            // reenviarDesistidas: TOCAR NO BOTÃO É O GESTO "eu tratei o motivo, tenta de
            // novo". Sem isto o aviso de venda desistida era um beco sem saída: o gerente
            // cadastrava o operador no painel, o operador apertava aqui, e o número não
            // se mexia — nada no PDV sabia tirar uma linha do dead-letter. Cada toque vale
            // UMA tentativa por linha; o ciclo automático de 45 s continua sem tocá-las.
            var r = await Sincronizacao.ExecutarAsync(Servicos.Nuvem(), Servicos.Guarda(), Servicos.Dreno(),
                andamento, reenviarDesistidas: true);

            if (!r.Ok)
            {
                var onde = etapa.Length == 0 ? "" : $"Parou em: {etapa.TrimEnd('…', '.')}. ";
                Dialogo.Avisar(dono, "Sem atualizar",
                    $"{r.Erro}\n\n{onde}As vendas deste caixa continuam guardadas aqui. " +
                    "Tente de novo em alguns minutos.", "erro");
            }
            else if (r.SemNovidade)
            {
                // Sem novidade o relatório detalhado só confunde: parecia estar
                // mostrando "a última sincronização" de novo.
                Dialogo.Avisar(dono, "Tudo em dia",
                    "O cardápio e os preços deste caixa já estão em dia. Nada novo para baixar.", "ok");
            }
            else
            {
                var linhas = new List<string>
                {
                    $"Cardápio:  {(r.CatalogoMudou ? $"atualizado ({Conta(r.ProdutosBaixados, "produto", "produtos")})" : "sem novidade")}",
                    $"Fotos:     {(r.FotosBaixadas == 0 ? "nenhuma nova" : Conta(r.FotosBaixadas, "nova", "novas"))}",
                    $"Notas:     {(r.NotasSubidas == 0 ? "nenhuma para enviar" : Conta(r.NotasSubidas, "enviada", "enviadas"))}",
                };
                if (r.NotasPendentes > 0)
                    linhas.Add($"\n⚠ {Conta(r.NotasPendentes, "nota", "notas")} ainda não " +
                        (r.NotasPendentes == 1 ? "foi enviada" : "foram enviadas") +
                        (Servicos.TemContaDeNuvem() ? ". Tente de novo mais tarde."
                                                    : ". Este caixa ainda não foi ligado ao painel — chame o gerente."));
                // O valor vem junto de propósito: "3 vendas na fila" não distingue
                // R$ 12,00 de R$ 2.493,00, e é o número em reais que faz alguém agir.
                if (r.Vendas.Resumo is string avisoVendas) linhas.Add("⚠ " + avisoVendas);

                Dialogo.Relatorio(dono, "Caixa atualizado", string.Join("\n", linhas), null);
                RecarregarCatalogo();
            }
        }
        finally
        {
            girando.Stop();
            TxtIconeSync.Text = "⟳";
            BtnSync.IsEnabled = true;
            PintarPendencias();
        }
    }

    /// <summary>
    /// Pendência invisível vira pendência eterna: o número fica no botão.
    ///
    /// A venda que o envio DESISTIU entra na conta e é dita com todas as letras: ela
    /// não sobe sozinha na próxima varredura, precisa de gente. Chamá-la de "esperando
    /// para subir" seria trocar o silêncio antigo por uma mentira mais tranquila.
    /// </summary>
    private void PintarPendencias()
    {
        var (notas, vendas) = Sincronizacao.Pendencias();
        var total = notas + vendas.Total;
        ChipPendencia.Visibility = total == 0 ? Visibility.Collapsed : Visibility.Visible;
        TxtPendencia.Text = total.ToString();
        // Só o que EXISTE entra no balão. "0 notas ainda não enviadas" em cima do aviso
        // que importa é ruído, e ruído é o que ensina o operador a não ler o balão.
        BtnSync.ToolTip = total == 0 ? "Tudo em dia"
            : string.Join("\n", new[]
              {
                  notas == 0 ? null : Conta(notas, "nota ainda não enviada", "notas ainda não enviadas"),
                  vendas.Resumo,
              }.Where(l => !string.IsNullOrWhiteSpace(l)));
    }

    // ── ATUALIZAR O CAIXA ─────────────────────────────────────────────────────

    /// <summary>
    /// "Toda vez ter que desinstalar e instalar?" — não. Este botão faz o ciclo
    /// inteiro: pergunta ao servidor se tem versão nova, baixa, PROVA que o que baixou
    /// é o instalador certo, chama o instalador (que já sabe trocar por cima
    /// preservando vendas e configuração) e fecha o PDV.
    ///
    /// A decisão toda mora em <see cref="Nucleo.Atualizacao"/> e a mecânica em
    /// <see cref="AtualizarCaixa"/> — daqui vai só o que só esta tela sabe: quantos
    /// itens tem na comanda e se a maquininha está ocupada. São os dois portões que
    /// não existem em lugar nenhum do banco, e são os que mais importam: caixa que
    /// reinicia com o cliente no balcão é pior do que caixa desatualizado.
    /// </summary>
    private async void AtualizarOCaixa(object sender, RoutedEventArgs e)
    {
        var dono = Window.GetWindow(this)!;
        BtnAtualizar.IsEnabled = false;
        // Mesmo vocabulário do Sincronizar: o ícone anima enquanto o botão trabalha.
        var girando = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        var passo = 0;
        var quadros = new[] { "⬆", "⇧" };
        girando.Tick += (_, _) => TxtIconeAtualizar.Text = quadros[++passo % quadros.Length];
        girando.Start();

        var estaFechando = false;
        try
        {
            estaFechando = await AtualizarCaixa.ExecutarAsync(dono, _comanda.Count, _tefOcupado);
        }
        catch (Exception ex)
        {
            // Nada aqui pode derrubar a frente de caixa: o pior desfecho aceitável é
            // "não atualizou e você continua vendendo".
            Dialogo.Avisar(dono, "A atualização não terminou",
                ex.Message + "\n\nO caixa NÃO foi alterado e continua funcionando.", "erro");
        }
        finally
        {
            girando.Stop();
            TxtIconeAtualizar.Text = "⬆";
            // Fechando: mexer na tela agora só produz exceção no caminho da saída.
            if (!estaFechando)
            {
                BtnAtualizar.IsEnabled = true;
                ProcurarAtualizacao(forcar: true);
            }
        }
    }

    /// <summary>Quando perguntar ao servidor de novo. 6 h: o suficiente para a loja
    /// saber no mesmo dia, e pouco o bastante para não virar tráfego de fundo.</summary>
    private DateTime _proximaChecagemVersao = DateTime.MinValue;

    /// <summary>
    /// O "tem atualização" do TeamViewer: o caixa vai perguntar sozinho e acende o
    /// selo. Silencioso por princípio — checagem automática que abre diálogo no meio
    /// do movimento seria exatamente o tipo de interrupção que a regra 1 proíbe.
    /// Falhou (sem rede, servidor fora)? Não acende nada e ninguém fica sabendo.
    /// </summary>
    private void ProcurarAtualizacao(bool forcar = false)
    {
        if (!forcar && DateTime.UtcNow < _proximaChecagemVersao) return;
        _proximaChecagemVersao = DateTime.UtcNow.AddHours(6);
        _ = Task.Run(async () =>
        {
            var m = await AtualizarCaixa.ProcurarNoSilencioAsync();
            // A tela pode ter sido descarregada (logout, fechamento) enquanto a
            // consulta corria: pintar aí é exceção no dispatcher, não é informação.
            try { Dispatcher.Invoke(() => { if (IsLoaded) PintarVersaoNova(m); }); }
            catch { }
        });
    }

    private void PintarVersaoNova(Nucleo.Atualizacao.Manifesto? m)
    {
        ChipVersaoNova.Visibility = m is null ? Visibility.Collapsed : Visibility.Visible;
        if (m is null)
        {
            BtnAtualizar.ToolTip = $"Este caixa está na versão {Nucleo.Atualizacao.VersaoInstalada()}";
            return;
        }
        TxtVersaoNova.Text = m.Versao;
        // Obrigatória pinta de vermelho e diz por quê. É só isso que ela muda na
        // tela — nada aqui reinicia o caixa sozinho: um campo de JSON servido pela
        // internet não decide na frente de quem está atendendo o cliente.
        var chave = m.Obrigatoria ? "ChipErro" : "ChipAlerta";
        ChipVersaoNova.SetResourceReference(Border.BackgroundProperty, chave + "Fundo");
        ChipVersaoNova.SetResourceReference(Border.BorderBrushProperty, chave + "Borda");
        TxtVersaoNova.SetResourceReference(TextBlock.ForegroundProperty, m.Obrigatoria ? "Erro" : "Amarelo");
        BtnAtualizar.ToolTip =
            (m.Obrigatoria ? "ATUALIZAÇÃO OBRIGATÓRIA: " : "Tem versão nova: ")
            + $"{m.Versao} (este caixa está na {Nucleo.Atualizacao.VersaoInstalada()})."
            + "\nToque para atualizar — as vendas e a configuração da loja não se perdem."
            + (m.Notas is { Length: > 0 } n ? "\n\n" + n : "");
    }

    /// <summary>Recarrega a grade depois de baixar catálogo novo, mantendo a categoria aberta.</summary>
    private void RecarregarCatalogo()
    {
        var antiga = _categoriaAtual;
        _catalogo.Clear();
        _quantosPorCategoria.Clear();
        ListaCategorias.Items.Clear();
        CarregarCatalogo();
        if (_catalogo.Any(p => p.Categoria == antiga))
        {
            _categoriaAtual = antiga;
            RepintarCategorias();
            PintarProdutos();
        }
    }

    private void GradeRedimensionou(object sender, SizeChangedEventArgs e)
    {
        _larguraGrade = e.NewSize.Width;
        AjustarColunas();
    }

    private void AjustarColunas()
    {
        if (_larguraGrade <= 0) return;
        // ~178px por cartão: abaixo disso nome de produto longo quebra em 3 linhas.
        // Cartão menor = mais linha por tela = menos rolagem, que é o que trava a fila.
        // Na lista o item é uma faixa larga, então 2 colunas só a partir de tela grande.
        var util = _larguraGrade - 20;
        _colunasProdutos = _modoLista
            ? Math.Clamp((int)(util / 420), 1, 3)
            : Math.Clamp((int)(util / 178), 2, 8);
        // aba PROMOCAO: secoes lado a lado (1-3 pela largura, pedido do dono
        // no teste SaaS com 13 promocoes); a grade interna reparte o que sobra
        if (_categoriaAtual == CategoriaPromo)
        {
            Colunas = Math.Clamp((int)(util / 520), 1, 3);
            _colunasProdutosSecao = Math.Clamp((int)((util / Colunas - 28) / 178), 1, 4);
        }
        else
        {
            Colunas = _colunasProdutos;
        }
    }

    private int _colunasProdutosSecao = 2;

    private int _colunasProdutos = 3;

    // ── CATÁLOGO ────────────────────────────────────────────────────────────
    // O nome mora no Núcleo porque é lá que está a regra de "esta categoria não entra
    // no alfabeto, fica sempre em primeiro" — e é lá que a suíte consegue prová-la.
    private const string CategoriaPromo = Nucleo.Categorias.Promocao;

    private List<Nucleo.Promocoes.Promo> _promos = new();
    private Dictionary<string, Nucleo.Promocoes.ProdutoPromo> _promoVitrine = new();
    private string _assinaturaPromo = "";

    /// <summary>Preço efetivo AGORA (motor de promoções). Base intacta quando
    /// nada se aplica; entre promoções vale a melhor pro cliente.</summary>
    private (Dinheiro Preco, string? Promo) PrecoDe(Produto p)
    {
        var (cent, nome) = Nucleo.Promocoes.PrecoEfetivoCent(
            _promos, p.Id, p.Categoria, p.Preco.Centavos, DateTime.Now);
        return (new Dinheiro(cent), nome);
    }

    private string AssinaturaPromos()
        => string.Join("|", _catalogo.Select(p => p.Id + ":" + PrecoDe(p).Preco.Centavos));

    private void CarregarCatalogo()
    {
        using var cx = Banco.Abrir();
        _promos = Nucleo.Promocoes.Carregar(cx);
        _promoVitrine = Nucleo.Promocoes.ProdutosEmPromocao(_promos, DateTime.Now);
        // Sem ORDER BY: quem ordena é o Núcleo, em pt-BR. O SQLite compara texto por BYTE,
        // e com isso ÁGUA MINERAL COM GÁS aparecia no FIM de Bebidas, depois de SUCO UVA
        // (defeito que o dono viu no balcão) — ver Pdv.Nucleo/Categorias.cs.
        var lidos = new List<Produto>();
        foreach (var r in cx.Query("""
            SELECT id, plu, nome, categoria, preco_cent, unidade, ncm, cest, csosn, origem, foto_local
              FROM produto WHERE ativo = 1
            """))
        {
            lidos.Add(new Produto((string)r.id, r.plu as string, (string)r.nome,
                (r.categoria as string) ?? "Outros", new Dinheiro((long)r.preco_cent),
                (r.unidade as string) ?? "UN", r.ncm as string, r.cest as string,
                r.csosn as string, (int)(long)r.origem, r.foto_local as string));
        }
        // Ordenado UMA vez aqui: a grade é pintada a cada toque em categoria, e ela só
        // filtra esta lista — ordenar na pintura seria refazer o mesmo trabalho por clique.
        _catalogo.AddRange(Nucleo.Categorias.OrdenarPorNome(lidos, p => p.Nome));

        // vitrine de PROMOÇÃO no topo: só existe quando alguma promoção vigente
        // menciona produto do catálogo — categoria vazia é pior que nenhuma
        var emPromo = _catalogo.Count(pp => _promoVitrine.ContainsKey(pp.Id));
        var nomesCat = _catalogo.Select(p => p.Categoria).ToList();
        if (emPromo > 0) nomesCat.Add(CategoriaPromo);

        var cats = Nucleo.Categorias.Ordenar(nomesCat, CategoriaPromo);
        foreach (var c in cats) _quantosPorCategoria[c] = _catalogo.Count(p => p.Categoria == c);
        // PROMOÇÃO não é categoria de produto — a contagem dela é quantos produtos do
        // catálogo alguma promoção vigente alcança, não quantos têm essa categoria (zero).
        if (emPromo > 0) _quantosPorCategoria[CategoriaPromo] = emPromo;
        _categoriaMini = cats.Count > 12;
        ColunasCategorias = _categoriaMini ? 3 : 2;
        _categoriaAtual = cats.FirstOrDefault() ?? "";
        foreach (var c in cats) ListaCategorias.Items.Add(BotaoCategoria(c));
        RepintarCategorias();
        PintarProdutos();
        PintarComanda();
    }

    /// <summary>
    /// Ícone e cor por categoria. A cor faz mais diferença que o desenho: com fila,
    /// o operador acha "a azul" antes de ler qualquer palavra.
    /// </summary>
    private static (string icone, Color cor) Visual(string categoria)
    {
        var c = categoria.ToLowerInvariant();
        if (c.Contains("promo")) return ("🏷️", Color.FromRgb(0xF2, 0x76, 0xA5));
        if (c.Contains("donut") || c.Contains("rosquin")) return ("🍩", Color.FromRgb(0xE8, 0x6A, 0x92));
        if (c.Contains("cookie") || c.Contains("biscoit")) return ("🍪", Color.FromRgb(0xC1, 0x8A, 0x4E));
        if (c.Contains("café") || c.Contains("cafe")) return ("☕", Color.FromRgb(0x8D, 0x6E, 0x5C));
        if (c.Contains("bebida") || c.Contains("refri") || c.Contains("suco")) return ("🥤", Color.FromRgb(0x3D, 0x9B, 0xD1));
        if (c.Contains("combo")) return ("🎁", Color.FromRgb(0x9B, 0x6D, 0xD4));
        if (c.Contains("salgad") || c.Contains("empada") || c.Contains("pao") || c.Contains("pão")) return ("🥐", Color.FromRgb(0xD4, 0x9A, 0x3C));
        if (c.Contains("bolo") || c.Contains("torta")) return ("🍰", Color.FromRgb(0xE0, 0x7A, 0x5F));
        if (c.Contains("sorvete") || c.Contains("açaí") || c.Contains("acai")) return ("🍦", Color.FromRgb(0x6C, 0x8E, 0xE0));
        if (c.Contains("choc")) return ("🍫", Color.FromRgb(0x7B, 0x52, 0x3B));
        if (c.Contains("agua") || c.Contains("água")) return ("💧", Color.FromRgb(0x3E, 0xB5, 0xC4));
        if (c.Contains("kit") || c.Contains("cesta")) return ("🧺", Color.FromRgb(0x5E, 0xA8, 0x6B));
        return ("🏷️", Color.FromRgb(0x5A, 0x77, 0x82));
    }

    /// <summary>
    /// Botão de categoria: quase quadrado, ícone pequeno e nome grande. O nome é o que
    /// o operador lê; o ícone só serve de âncora visual, então não precisa ocupar
    /// metade do botão. O contador ajuda a perceber quando o catálogo veio incompleto.
    /// </summary>
    private Button BotaoCategoria(string categoria)
    {
        var (icone, cor) = Visual(categoria);
        var b = new Button
        {
            Style = (Style)Application.Current.Resources["BotaoBase"],
            Margin = new Thickness(_categoriaMini ? 3 : 4),
            MinHeight = _categoriaMini ? 88 : 124, Height = _categoriaMini ? 88 : 124,
            Padding = new Thickness(4, _categoriaMini ? 5 : 8, 4, _categoriaMini ? 5 : 8),
            Tag = categoria,
        };
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new Border
        {
            Width = _categoriaMini ? 24 : 34, Height = _categoriaMini ? 24 : 34,
            CornerRadius = new CornerRadius(17),
            Background = Degrade(cor, RD("FatorDegradeClaro"), RD("FatorDegradeEscuro")),
            HorizontalAlignment = HorizontalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = cor, BlurRadius = 16, ShadowDepth = 0, Opacity = RD("GlowCategoriaOpacidade"),
            },
            Child = new TextBlock
            {
                Text = icone, FontSize = _categoriaMini ? 12 : 17,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        // Estes cards vivem a sessão INTEIRA (só o fundo é repintado na troca de
        // categoria/tema) — então o texto precisa de referência VIVA ao recurso.
        // Brush resolvido na criação congela a cor do tema de nascença: virar pro
        // claro deixava o nome BRANCO sobre véu claro, ilegível. Bug real de balcão.
        var nome = new TextBlock
        {
            // 16 e nao caixa alta: maiuscula "parece" maior mas le pior e come
            // largura — corpo maior e o que aumenta a leitura de verdade
            Text = Capitalizar(categoria),
            FontSize = _categoriaMini ? 11.5 : 16, FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, _categoriaMini ? 4 : 7, 0, 0),
            MaxHeight = _categoriaMini ? 30 : 44,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = _categoriaMini ? 14 : 19,
        };
        nome.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
        sp.Children.Add(nome);

        var contador = new TextBlock
        {
            Text = _quantosPorCategoria.GetValueOrDefault(categoria).ToString(),
            FontSize = 12, FontWeight = FontWeights.Bold,
        };
        contador.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
        var selo = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 1, 9, 2),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
            Child = contador,
        };
        selo.SetResourceReference(Border.BackgroundProperty, "VeuElevado");
        if (!_categoriaMini) sp.Children.Add(selo);
        b.Content = sp;
        AutomationProperties.SetName(b, categoria);
        b.Click += (_, _) => { _categoriaAtual = categoria; RepintarCategorias(); PintarProdutos(); };
        return b;
    }

    /// <summary>
    /// Cada categoria carrega um véu da própria cor — é o que deixa a coluna legível
    /// de relance. A aberta acende: mesma cor, mais forte, com borda.
    /// </summary>
    private void RepintarCategorias()
    {
        foreach (Button b in ListaCategorias.Items)
        {
            var cat = (string?)b.Tag ?? "";
            var (_, cor) = Visual(cat);
            var ativa = cat == _categoriaAtual;
            // Alphas vêm do tema: os do escuro lavam sobre creme, os do claro
            // estouram sobre grafite. Byte por chave, calibrado por paleta.
            b.Background = new LinearGradientBrush(
                Color.FromArgb(RB(ativa ? "AlfaCatAtivaTopo" : "AlfaCatInativaTopo"), cor.R, cor.G, cor.B),
                Color.FromArgb(RB(ativa ? "AlfaCatAtivaBase" : "AlfaCatInativaBase"), cor.R, cor.G, cor.B),
                new Point(0, 0), new Point(0, 1));
            b.BorderBrush = new SolidColorBrush(Color.FromArgb(RB(ativa ? "AlfaCatBordaAtiva" : "AlfaCatBordaInativa"), cor.R, cor.G, cor.B));
            b.BorderThickness = new Thickness(ativa ? 2 : 1);
        }
    }

    /// <summary>Degradê claro→escuro da mesma cor. É o que dá relevo sem virar 3D.</summary>
    private static LinearGradientBrush Degrade(Color c, double claro, double escuro)
    {
        static byte Ajusta(byte v, double f) => (byte)Math.Clamp(v * f, 0, 255);
        return new LinearGradientBrush(
            Color.FromRgb(Ajusta(c.R, claro), Ajusta(c.G, claro), Ajusta(c.B, claro)),
            Color.FromRgb(Ajusta(c.R, escuro), Ajusta(c.G, escuro), Ajusta(c.B, escuro)),
            new Point(0, 0), new Point(1, 1));
    }

    /// <summary>CAIXA ALTA em bloco cansa a leitura; "Cookies Premium" lê mais rápido.</summary>
    private static string Capitalizar(string s) =>
        string.Join(' ', s.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Length <= 2 && p != "kg" ? p : char.ToUpperInvariant(p[0]) + p[1..]));

    private void PintarProdutos()
    {
        AjustarColunas();
        var lista = _categoriaAtual == CategoriaPromo
            ? _catalogo.Where(p => _promoVitrine.ContainsKey(p.Id)).ToList()
            : _catalogo.Where(p => p.Categoria == _categoriaAtual).ToList();

        var (icone, cor) = Visual(_categoriaAtual);
        TxtIconeCategoria.Text = icone;
        SeloCategoria.Background = Degrade(cor, RD("FatorDegradeClaro"), RD("FatorDegradeEscuro"));
        TxtCategoriaAberta.Text = Capitalizar(_categoriaAtual);
        TxtContagem.Text = lista.Count == 1 ? "1 item" : $"{lista.Count} itens";

        ListaProdutos.Items.Clear();
        if (_categoriaAtual == CategoriaPromo)
        {
            // SEGMENTADO por promoção (pedido do dono): cabeçalho com nome e
            // regra, itens da promoção embaixo — nada de misturar vitrines.
            // Ativas primeiro; as fora de dia/horário descem, em cinza.
            var grupos = lista
                .GroupBy(p => _promoVitrine[p.Id].Nome)
                .OrderByDescending(g => g.Any(p => _promoVitrine[p.Id].AtivaAgora))
                .ThenBy(g => g.Key);
            foreach (var g in grupos)
            {
                var info = _promoVitrine[g.First().Id];
                ListaProdutos.Items.Add(SecaoPromo(g.Key, info, g.ToList()));
            }
        }
        else
        {
            foreach (var p in lista)
                ListaProdutos.Items.Add(_modoLista ? LinhaProduto(p) : CartaoProduto(p));
        }
        TxtSemProduto.Visibility = ListaProdutos.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Uma promoção = uma seção: cabeçalho (nome + quando + estado) e a grade
    /// dos produtos dela embaixo. Fora do dia/horário: tudo em cinza, sem vender.
    /// </summary>
    private StackPanel SecaoPromo(string nomePromo, Nucleo.Promocoes.ProdutoPromo info,
                                  List<Produto> produtos)
    {
        var sec = new StackPanel { Margin = new Thickness(2, 6, 2, 10) };

        var cab = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 8, 14, 9),
            Margin = new Thickness(2, 0, 2, 6), BorderThickness = new Thickness(1),
        };
        // VERDE quando valendo: o vermelho da 1a versao lia como "negativo/
        // erro" (reclamacao do dono) - promocao ativa e coisa BOA acontecendo
        cab.SetResourceReference(Border.BackgroundProperty,
            info.AtivaAgora ? "ChipOkFundo" : "VeuElevado");
        cab.SetResourceReference(Border.BorderBrushProperty,
            info.AtivaAgora ? "ChipOkBorda" : "Borda");
        var linhaCab = new StackPanel { Orientation = Orientation.Horizontal };
        var titulo = new TextBlock
        {
            Text = "🏷️ " + Capitalizar(nomePromo), FontSize = 17, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titulo.SetResourceReference(TextBlock.ForegroundProperty,
            info.AtivaAgora ? "Ok" : "TextoFraco");
        linhaCab.Children.Add(titulo);
        var detalhe = new TextBlock
        {
            Text = info.AtivaAgora ? $"  ·  {info.Quando}" : $"  ·  não vale agora — só {info.Quando}",
            FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
        };
        detalhe.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
        linhaCab.Children.Add(detalhe);
        cab.Child = linhaCab;
        sec.Children.Add(cab);

        var grade = new System.Windows.Controls.Primitives.UniformGrid { Columns = Math.Max(1, _colunasProdutosSecao) };
        foreach (var p in produtos) grade.Children.Add(ItemVitrine(p, info.AtivaAgora, info));
        sec.Children.Add(grade);
        return sec;
    }

    /// <summary>Card da vitrine: normal quando a promoção vale AGORA; cinza,
    /// desabilitado e com a regra escrita quando fora do dia/horário.</summary>
    private UIElement ItemVitrine(Produto p, bool ativa, Nucleo.Promocoes.ProdutoPromo info)
    {
        var el = _modoLista ? LinhaProduto(p) : CartaoProduto(p);
        if (ativa) return el;

        el.IsEnabled = false;
        el.ToolTip = $"{info.Nome} — só vale {info.Quando}";
        var moldura = new Grid();
        moldura.Children.Add(el);
        var faixa = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(0, 3, 0, 4),
            CornerRadius = new CornerRadius(0, 0, 13, 13),
            IsHitTestVisible = false,
        };
        faixa.SetResourceReference(Border.BackgroundProperty, "VeuElevado");
        var aviso = new TextBlock
        {
            Text = $"só vale {info.Quando}",
            FontSize = 11, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        aviso.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
        faixa.Child = aviso;
        moldura.Children.Add(faixa);
        return moldura;
    }

    // ── GRADE x LISTA ───────────────────────────────────────────────────────
    private void VerEmGrade(object sender, RoutedEventArgs e) => TrocarModo(false);
    private void VerEmLista(object sender, RoutedEventArgs e) => TrocarModo(true);

    private void TrocarModo(bool lista)
    {
        _modoLista = lista;
        AjustarColunas();
        PintarModo();
        PintarProdutos();
    }

    private void PintarModo()
    {
        var ativo = (Brush)Application.Current.Resources["Ciano"];
        var neutro = (Brush)Application.Current.Resources["TextoFraco"];
        BtnGrade.Foreground = _modoLista ? neutro : ativo;
        BtnLista.Foreground = _modoLista ? ativo : neutro;
        BtnGrade.Background = _modoLista ? Brushes.Transparent : (Brush)Application.Current.Resources["PainelAlto"];
        BtnLista.Background = _modoLista ? (Brush)Application.Current.Resources["PainelAlto"] : Brushes.Transparent;
        BtnGrade.BorderThickness = new Thickness(0);
        BtnLista.BorderThickness = new Thickness(0);
    }

    /// <summary>
    /// Linha compacta: quem já decorou o cardápio vende mais rápido sem foto, porque
    /// cabe o dobro de itens na tela e não precisa rolar.
    /// </summary>
    private Button LinhaProduto(Produto p)
    {
        var (icone, cor) = Visual(p.Categoria);
        var b = new Button
        {
            Style = (Style)Application.Current.Resources["BotaoBase"],
            Height = 62, MinHeight = 62, Margin = new Thickness(5, 4, 5, 4),
            Padding = new Thickness(12, 0, 14, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = (Brush)Application.Current.Resources["PainelDegrade"],
        };

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var selo = new Grid { Width = 38, Height = 38, VerticalAlignment = VerticalAlignment.Center };
        selo.Children.Add(new Ellipse
        {
            Width = 38, Height = 38,
            Fill = new SolidColorBrush(Color.FromArgb(0x2E, cor.R, cor.G, cor.B)),
        });
        var arquivo = Fotos.JaBaixada(p.Foto);
        var bmp = arquivo is null ? null : CarregarFoto(arquivo, 76);
        if (bmp is not null)
            selo.Children.Add(new Ellipse { Width = 34, Height = 34, Fill = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill } });
        else
            selo.Children.Add(new TextBlock
            {
                Text = icone, FontSize = 17,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        Grid.SetColumn(selo, 0);
        g.Children.Add(selo);

        var nome = new TextBlock
        {
            Text = p.Nome.ToUpperInvariant(), FontSize = 13.5, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(13, 0, 10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["Texto"],
        };
        Grid.SetColumn(nome, 1);
        g.Children.Add(nome);

        var (precoEf, promoNome) = PrecoDe(p);
        var preco = new TextBlock
        {
            Text = precoEf.Formatado(), FontSize = 17, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources[promoNome is null ? "Ciano" : "Rosa"],
            ToolTip = promoNome is null ? null : "Promoção: " + promoNome,
        };
        Grid.SetColumn(preco, 2);
        g.Children.Add(preco);

        b.Content = g;
        AutomationProperties.SetName(b, p.Nome);
        b.Click += (_, _) => Adicionar(p);
        return b;
    }

    /// <summary>Foto do disco, já congelada — sem isso cada cartão segura o arquivo aberto.</summary>
    private static BitmapImage? CarregarFoto(string caminho, int larguraPx)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(caminho);
            img.CacheOption = BitmapCacheOption.OnLoad;      // libera o arquivo depois de ler
            img.DecodePixelWidth = larguraPx;                 // não guarda a imagem inteira na memória
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    /// <summary>
    /// Cartão do produto: foto redonda sobre um véu da cor da categoria, nome no meio,
    /// preço embaixo em destaque. Sem largura fixa — quem manda no tamanho é a grade,
    /// pra não sobrar vão morto à direita nem colar um cartão no outro.
    ///
    /// Sem foto entra o ícone da categoria no mesmo disco: um buraco cinza no meio da
    /// grade some da vista, o disco colorido continua sendo um alvo reconhecível.
    /// </summary>
    private Button CartaoProduto(Produto p)
    {
        var (icone, cor) = Visual(p.Categoria);
        var b = new Button
        {
            Style = (Style)Application.Current.Resources["BotaoBase"],
            Height = 168, Margin = new Thickness(6), Padding = new Thickness(5, 11, 5, 9),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = (Brush)Application.Current.Resources["PainelDegrade"],
        };

        var grade = new Grid();
        grade.RowDefinitions.Add(new RowDefinition { Height = new GridLength(78) });
        grade.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grade.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── foto redonda sobre um brilho da cor da categoria ────────────────
        var moldura = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        moldura.Children.Add(new Ellipse
        {
            Width = 78, Height = 78,
            // brilho: forte no centro, dissolvendo na borda. É o que separa o produto
            // do fundo sem precisar de moldura desenhada.
            Fill = new RadialGradientBrush(
                Color.FromArgb(0x4E, cor.R, cor.G, cor.B),
                Color.FromArgb(0x00, cor.R, cor.G, cor.B)),
        });

        var bmp = CarregarFoto(Fotos.JaBaixada(p.Foto) ?? "", 160);
        if (bmp is not null)
        {
            moldura.Children.Add(new Ellipse
            {
                Width = 70, Height = 70,
                Fill = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill },
            });
        }
        else
        {
            moldura.Children.Add(new TextBlock
            {
                Text = icone, FontSize = 30, Opacity = 0.92,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        Grid.SetRow(moldura, 0);
        grade.Children.Add(moldura);

        var nome = new TextBlock
        {
            Text = p.Nome.ToUpperInvariant(), FontSize = 11.5, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["Texto"],
            Margin = new Thickness(4, 5, 4, 0), MaxHeight = 32, LineHeight = 14,
            // Sem BlockLineHeight o LineHeight é só um piso: a linha real fica ~15,3px,
            // duas linhas passam do limite e o nome comprido some num "..." de uma linha.
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetRow(nome, 1);
        grade.Children.Add(nome);

        var (precoEf2, promoNome2) = PrecoDe(p);
        var preco = new TextBlock
        {
            Text = precoEf2.Formatado(), FontSize = 18, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
            Foreground = (Brush)Application.Current.Resources[promoNome2 is null ? "Ciano" : "Rosa"],
            ToolTip = promoNome2 is null ? null : "Promoção: " + promoNome2,
        };
        Grid.SetRow(preco, 2);
        grade.Children.Add(preco);

        b.Content = grade;
        // Sem isso o botão não tem nome nenhum pra leitor de tela nem pra teste
        // automatizado — o conteúdo é uma grade de imagem e texto, não um rótulo.
        AutomationProperties.SetName(b, p.Nome);
        b.Click += (_, _) => Adicionar(p);
        return b;
    }

    // ── COMANDA ─────────────────────────────────────────────────────────────
    private void Adicionar(Produto p)
    {
        // preço da comanda = preço efetivo NO MOMENTO do toque (promoção do
        // dia, %, valor). O card já mostrou este preço; cobrar outro seria
        // exatamente o "promoção não vinculada" que o dono viu na quinta.
        var (efetivo, _) = PrecoDe(p);
        if (efetivo.Centavos != p.Preco.Centavos) p = p with { Preco = efetivo };

        // segundo toque no mesmo produto INCREMENTA — não cria linha nova
        var existente = _comanda.FirstOrDefault(i => i.Produto.Id == p.Id);
        if (existente is not null) existente.Qtd = new Quantidade(existente.Qtd.Milesimos + 1000);
        else _comanda.Insert(0, new ItemComanda { Produto = p });   // novo entra no TOPO
        PintarComanda();
    }

    /// <summary>Nome normalizado pra casar item do cupom com item da comanda (maiúsc., sem acento, espaço colapsado).</summary>
    private static string NormalizarNome(string s)
        => new string(s.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray())
            .ToUpperInvariant().Trim() is var t ? System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ") : "";

    /// <summary>Quantas UNIDADES deste item a cortesia cobre (limitado ao que há na linha).</summary>
    private int CoberturaDe(ItemComanda item)
        => _cortesiaCobertura.TryGetValue(NormalizarNome(item.Produto.Nome), out var n)
            ? (int)Math.Min(n, item.Qtd.Milesimos / 1000) : 0;

    private void PintarComanda()
    {
        ListaComanda.Items.Clear();
        foreach (var item in _comanda) ListaComanda.Items.Add(LinhaComanda(item));

        // Total COBRADO: preço × unidades não cobertas pela cortesia.
        var totalCent = _comanda.Sum(i =>
            i.Produto.Preco.Centavos * ((i.Qtd.Milesimos / 1000) - CoberturaDe(i))
            + i.Produto.Preco.VezesQtd(i.Qtd.Milesimos % 1000).Centavos);
        var total = new Dinheiro(Math.Max(0, totalCent));
        TxtTotal.Text = total.Formatado();

        var qtd = _comanda.Sum(i => i.Qtd.Milesimos) / 1000m;
        TxtQtdItens.Text = Rascunho.TextoItens(qtd);
        ChipItens.Visibility = _comanda.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PainelVazio.Visibility = _comanda.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnFinalizar.IsEnabled = _comanda.Count > 0;
        BtnLimpar.IsEnabled = _comanda.Count > 0;
        BtnCortesia.IsEnabled = _comanda.Count > 0 && _cortesiaCodigo is null;
        BtnCortesia.Visibility = _cortesiaCodigo is null ? Visibility.Visible : Visibility.Collapsed;
        SalvarRascunho();
    }

    // ── RASCUNHO DA COMANDA (sobreviver à queda de energia) ──────────────────

    /// <summary>
    /// Só depois que o rascunho guardado foi oferecido é que esta tela pode gravar
    /// por cima dele. Sem esta trava, a primeira pintura (catálogo carregando, comanda
    /// ainda vazia) apagaria a comanda que o religamento tinha para devolver.
    /// </summary>
    private bool _rascunhoOferecido;

    /// <summary>
    /// Encosta a comanda no disco a cada mudança — bipe, +/−, lixeira, cortesia.
    /// Medido em 1,7 ms por bipe (uma linha, sobrescrita no lugar), então não há
    /// debounce: o que está na tela está no disco, sem janela de perda.
    ///
    /// Nunca derruba a venda: disco cheio ou banco travado no máximo custa o rascunho,
    /// e perder o rascunho é voltar ao que já era — nunca perder o bipe.
    ///
    /// COM O PAGAMENTO NO AR, NÃO GRAVA. Depois do commit da venda a tela ainda vive
    /// dezenas de segundos (NFC-e, impressão, e o "Reimprimir" parado até alguém tocar
    /// se a bobina entalou), e `_comanda` só é esvaziada no fim disso, no `Encerrou`.
    /// Nessa janela um repintar de fundo — push de catálogo do painel, troca de tema —
    /// regravaria a comanda de uma venda JÁ PAGA, e a queda de energia ali devolveria
    /// no religamento um rascunho que o operador cobraria de novo. Nada se perde: o
    /// painel de pagamento cobre a tela inteira, então a comanda não muda enquanto ele
    /// está visível.
    /// </summary>
    private void SalvarRascunho()
    {
        if (!_rascunhoOferecido) return;
        if (PainelPagamento.Visibility == Visibility.Visible) return;
        try
        {
            using var cx = Banco.Abrir();
            Rascunho.Gravar(cx, _sessao, _operador,
                _comanda.Select(i => new ItemRascunho(
                    i.Produto.Id, i.Produto.Plu, i.Produto.Nome, i.Produto.Categoria,
                    i.Produto.Preco.Centavos, i.Qtd.Milesimos, i.Produto.Unidade,
                    i.Produto.Ncm, i.Produto.Cest, i.Produto.Csosn, i.Produto.Origem,
                    i.Produto.Foto)).ToList(),
                Dinheiro.Zero, _cortesiaCodigo, _cortesiaCobertura);
        }
        catch { /* rascunho é conforto, não dinheiro: nunca atrapalha a venda */ }
    }

    /// <summary>
    /// O caixa desligou com comanda em andamento: devolve os ITENS, com o operador
    /// decidindo. Restaurar não é realizar a venda — nenhuma linha em `venda` nasce
    /// aqui, e o cartão que porventura ficou armado na maquininha segue o destino
    /// dele (órfão, pela reconciliação do TEF), sem vir junto.
    ///
    /// O que o diálogo NÃO pode fazer é jurar que nada foi cobrado: a cobrança nasce
    /// antes da venda, então o aviso só sai depois de <see cref="Caixa.CobrancaSemVenda"/>.
    ///
    /// Roda uma vez por tela: voltar do KDS ou do chat não recria a Venda, e
    /// perguntar de novo com a comanda viva na mão seria só atrapalhar.
    /// </summary>
    private void OferecerRascunho()
    {
        if (_rascunhoOferecido) return;
        _rascunhoOferecido = true;

        ComandaRascunho? r = null;
        Dinheiro? cobrado = null;   // null = não deu para conferir a maquininha
        try
        {
            using var cx = Banco.Abrir();
            r = Rascunho.Ler(cx, _sessao.Id);
            if (r is not null) cobrado = Caixa.CobrancaSemVenda(cx, _sessao);
        }
        catch { if (r is null) return; }
        if (r is null || r.Itens.Count == 0) return;

        var unidades = r.Itens.Sum(i => i.QtdMilesimos) / 1000m;
        var total = new Dinheiro(r.Itens.Sum(i => new Dinheiro(i.PrecoCent).VezesQtd(i.QtdMilesimos).Centavos));
        var quando = r.AtualizadoEm.ToString("HH:mm");

        // A COBRANÇA NASCE ANTES DA VENDA: existe a janela em que o cartão JÁ PASSOU e
        // não há venda — e ela é exatamente esta, a da queda de energia que deixou o
        // rascunho. O que se pode afirmar sobre o dinheiro sai de Rascunho.AvisoDeCobranca,
        // que é testado; a tela só mostra.
        var aviso = Rascunho.AvisoDeCobranca(cobrado);

        var recuperar = Dialogo.Confirmar(Window.GetWindow(this)!, "Comanda aberta",
            $"O caixa parou às {quando} e esta comanda ficou aberta:\n\n" +
            $"{Rascunho.TextoItens(unidades)} · {total.Formatado()}\n\n" +
            aviso + "\n\n" +
            (unidades == 1m ? "Ao continuar, o item volta" : "Ao continuar, os itens voltam") +
            " para a tela. Confira o pedido com o cliente antes de finalizar.",
            "Continuar comanda", "Descartar comanda");

        if (!recuperar)
        {
            try
            {
                using var cx = Banco.Abrir();
                Rascunho.Apagar(cx);
                Caixa.Auditar(cx, null, "rascunho_descartado", _operador.Id, null,
                    $"{Rascunho.TextoItens(unidades)} · {total.Formatado()} · parada às {quando}");
            }
            catch { }
            return;
        }

        _comanda.Clear();
        foreach (var i in r.Itens)
            _comanda.Add(new ItemComanda
            {
                Produto = new Produto(i.ProdutoId, i.Plu, i.Nome, i.Categoria,
                    new Dinheiro(i.PrecoCent), i.Unidade, i.Ncm, i.Cest, i.Csosn, i.Origem, i.Foto),
                Qtd = new Quantidade(i.QtdMilesimos),
            });

        // A cortesia volta junto: sem ela o operador cobraria o que já tinha sido
        // dado de graça, e o cupom continuaria queimando na próxima tentativa.
        _cortesiaCodigo = r.CortesiaCodigo;
        _cortesiaCobertura.Clear();
        foreach (var kv in r.CortesiaCobertura) _cortesiaCobertura[kv.Key] = kv.Value;
        if (_cortesiaCodigo is not null)
        {
            TxtCortesiaTitulo.Text = $"Cortesia {_cortesiaCodigo} aplicada";
            TxtCortesiaItens.Text = string.Join(", ",
                _comanda.Where(i => CoberturaDe(i) > 0)
                        .Select(i => $"{CoberturaDe(i)}× {i.Produto.Nome} grátis"));
            CaixaCortesia.Visibility = Visibility.Visible;
        }

        PintarComanda();
        try
        {
            using var cx = Banco.Abrir();
            Caixa.Auditar(cx, null, "rascunho_restaurado", _operador.Id, null,
                $"{Rascunho.TextoItens(unidades)} · {total.Formatado()} · parada às {quando}");
        }
        catch { }
    }

    /// <summary>
    /// Linha da comanda em duas alturas: em cima nome e total da linha, embaixo o
    /// preço unitário e os controles. Espremer nome e botões na mesma linha deixa o
    /// nome com um terço da largura — e é o nome que o cliente confere em voz alta.
    /// </summary>
    private Border LinhaComanda(ItemComanda item)
    {
        var borda = new Border
        {
            Background = (Brush)Application.Current.Resources["PainelDegrade"],
            BorderBrush = (Brush)Application.Current.Resources["Borda"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13, 11, 11, 11),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nome = new TextBlock
        {
            Text = item.Produto.Nome.ToUpperInvariant(), FontSize = 13, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, LineHeight = 16,
            Foreground = (Brush)Application.Current.Resources["Texto"],
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetRow(nome, 0); Grid.SetColumn(nome, 0);
        g.Children.Add(nome);

        var total = new TextBlock
        {
            Text = item.Total.Formatado(), FontSize = 16, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = (Brush)Application.Current.Resources["Texto"],
        };
        Grid.SetRow(total, 0); Grid.SetColumn(total, 1);
        g.Children.Add(total);

        var unitario = new TextBlock
        {
            Text = $"{item.Qtd.Formatada()} × {item.Produto.Preco.Formatado()}",
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextoFraco"],
            Margin = new Thickness(0, 10, 0, 0),
        };
        Grid.SetRow(unitario, 1); Grid.SetColumn(unitario, 0);
        g.Children.Add(unitario);

        // Padding zerado de propósito: o BotaoBase reserva 18px de cada lado, o que
        // num botão de 42px não deixa espaço nenhum e o símbolo some.
        Button Redondo(string txt, double fonte, Brush? cor = null)
        {
            var b = new Button
            {
                Content = txt, Width = 42, Height = 42, MinHeight = 42, FontSize = fonte,
                Padding = new Thickness(0), Style = (Style)Application.Current.Resources["BotaoBase"],
                Background = (Brush)Application.Current.Resources["PainelAlto"],
            };
            if (cor is not null) b.Foreground = cor;
            return b;
        }

        Button Passo(string txt, int delta)
        {
            var b = Redondo(txt, 21);
            b.Click += (_, _) =>
            {
                var novo = item.Qtd.Milesimos + delta * 1000;
                if (novo <= 0) _comanda.Remove(item);       // chegou a zero: sai da comanda
                else item.Qtd = new Quantidade(novo);
                PintarComanda();
            };
            return b;
        }

        var controles = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var menos = Passo("−", -1);
        AutomationProperties.SetName(menos, $"Diminuir {item.Produto.Nome}");
        controles.Children.Add(menos);

        controles.Children.Add(new TextBlock
        {
            Text = item.Qtd.Formatada(), FontSize = 17, FontWeight = FontWeights.Bold,
            Width = 44, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["Texto"],
        });

        var mais = Passo("+", 1);
        AutomationProperties.SetName(mais, $"Aumentar {item.Produto.Nome}");
        controles.Children.Add(mais);

        // Lixeira: tirar 3 unidades tocando "−" três vezes é lento e dá erro. Um toque resolve.
        var lixeira = Redondo("🗑", 16, (Brush)Application.Current.Resources["Erro"]);
        lixeira.Margin = new Thickness(10, 0, 0, 0);
        lixeira.ToolTip = "Remover item";
        AutomationProperties.SetName(lixeira, $"Remover {item.Produto.Nome}");
        lixeira.Click += (_, _) => { _comanda.Remove(item); PintarComanda(); };
        controles.Children.Add(lixeira);

        Grid.SetRow(controles, 1); Grid.SetColumn(controles, 1);
        g.Children.Add(controles);

        borda.Child = g;
        return borda;
    }

    private void LimparComanda(object sender, RoutedEventArgs e)
    {
        if (_comanda.Count == 0) return;
        if (!Dialogo.Confirmar(Window.GetWindow(this)!, "Limpar comanda",
                _comanda.Count == 1 ? "Tirar o item da comanda?" : $"Tirar os {_comanda.Count} itens da comanda?",
                "Limpar comanda", "Voltar", perigo: true)) return;
        _comanda.Clear();
        PintarComanda();
    }

    /// <summary>
    /// Abre o pagamento POR CIMA da comanda, sem destruí-la: se o operador desistir ou
    /// o cartão for recusado, ele volta para a mesma comanda em vez de refazer a venda
    /// item a item com o cliente esperando.
    /// </summary>
    // ── CORTESIA (cupom de erro de pedido, resgatado no caixa) ──────────────
    private async void AplicarCortesia(object sender, RoutedEventArgs e)
    {
        if (_comanda.Count == 0 || _cortesiaCodigo is not null) return;
        var dono = Window.GetWindow(this)!;

        var codigo = PedirTexto.Mostrar(dono, "Cortesia",
            "Código do cupom que o cliente trouxe (ex.: AD-XXXXXX)", "");
        if (string.IsNullOrWhiteSpace(codigo)) return;

        BtnCortesia.IsEnabled = false;
        Cortesia c;
        try { c = await _cortesias.ValidarAsync(codigo.Trim().ToUpperInvariant()); }
        finally { BtnCortesia.IsEnabled = _comanda.Count > 0 && _cortesiaCodigo is null; }

        if (!c.Ok)
        {
            Dialogo.Avisar(dono, "Cupom não vale", c.Erro switch
            {
                "not_found" => "Não achei esse código. Confira as letras e os números e digite de novo.",
                "expired" => "Este cupom venceu. Siga a venda sem a cortesia.",
                "already_redeemed" => "Este cupom já foi usado. Siga a venda sem a cortesia.",
                "cancelled" => "Este cupom foi cancelado. Siga a venda sem a cortesia.",
                "code_required" => "Digite o código do cupom.",
                "sem_rede" => "Sem internet para conferir o cupom agora. Tente de novo em alguns segundos.",
                _ => "Não deu para conferir o cupom agora. Tente de novo.",
            }, "erro");
            return;
        }

        // Trava de loja no balcão: cortesia de outra loja não vale aqui (o servidor
        // também recusa no resgate, mas avisar cedo evita aplicar e falhar no fim).
        if (!string.IsNullOrWhiteSpace(c.Loja) && !string.IsNullOrWhiteSpace(_loja)
            && !string.Equals(c.Loja, _loja, StringComparison.OrdinalIgnoreCase))
        {
            Dialogo.Avisar(dono, "Cupom de outra loja",
                $"Este cupom é da loja {c.Loja}. Só pode ser usado lá.", "erro");
            return;
        }

        // Casa os itens do cupom com a comanda: cobre até a quantidade do cupom,
        // limitada ao que o cliente realmente levou.
        var cobertura = new Dictionary<string, int>(StringComparer.Ordinal);
        var faltando = new List<string>();
        foreach (var itemCupom in c.Itens)
        {
            var norm = NormalizarNome(itemCupom.Nome);
            var disponivel = (int)_comanda.Where(i => NormalizarNome(i.Produto.Nome) == norm)
                .Sum(i => i.Qtd.Milesimos / 1000);
            var cobre = Math.Min(itemCupom.Quantidade, disponivel);
            if (cobre <= 0) { faltando.Add(itemCupom.Nome); continue; }
            cobertura[norm] = cobre;
        }

        if (cobertura.Count == 0)
        {
            Dialogo.Avisar(dono, "Faltam itens do cupom",
                "Passe primeiro estes produtos na comanda e aplique o cupom depois:\n" +
                string.Join(", ", c.Itens.Select(i => $"{i.Quantidade}× {i.Nome}")), "erro");
            return;
        }

        _cortesiaCodigo = c.Codigo ?? codigo.Trim().ToUpperInvariant();
        _cortesiaCobertura.Clear();
        foreach (var kv in cobertura) _cortesiaCobertura[kv.Key] = kv.Value;

        TxtCortesiaTitulo.Text = $"Cortesia {_cortesiaCodigo} aplicada";
        TxtCortesiaItens.Text = string.Join(", ",
            _comanda.Where(i => CoberturaDe(i) > 0)
                    .Select(i => $"{CoberturaDe(i)}× {i.Produto.Nome} grátis"));
        if (faltando.Count > 0)
            TxtCortesiaItens.Text += $"  ·  fora da comanda: {string.Join(", ", faltando)}";
        CaixaCortesia.Visibility = Visibility.Visible;
        PintarComanda();
    }

    private void RemoverCortesia(object sender, RoutedEventArgs e)
    {
        _cortesiaCodigo = null;
        _cortesiaCobertura.Clear();
        CaixaCortesia.Visibility = Visibility.Collapsed;
        PintarComanda();
    }

    private void Finalizar(object sender, RoutedEventArgs e)
    {
        if (_comanda.Count == 0) return;
        var dono = Window.GetWindow(this)!;
        if (TefEmAndamento(dono)) return;

        // Monta os itens COBRADOS: a quantidade coberta pela cortesia sai (brinde,
        // não venda — não vai pra NFC-e). Item totalmente coberto some da nota.
        var itens = new List<LinhaVenda>();
        foreach (var i in _comanda)
        {
            var unidades = i.Qtd.Milesimos / 1000;
            var fracao = i.Qtd.Milesimos % 1000;
            var cobradas = unidades - CoberturaDe(i);
            if (cobradas <= 0 && fracao == 0) continue;   // tudo cortesia: fora da nota
            var qtdCobrada = new Quantidade(cobradas * 1000 + fracao);
            var totalLinha = i.Produto.Preco.VezesQtd(qtdCobrada.Milesimos);
            itens.Add(new LinhaVenda(
                i.Produto.Id, i.Produto.Plu ?? i.Produto.Id, i.Produto.Nome,
                qtdCobrada, i.Produto.Preco, totalLinha, i.Produto.Unidade,
                i.Produto.Ncm, i.Produto.Cest, i.Produto.Csosn, null, i.Produto.Origem));
        }

        // Cortesia cobrindo a comanda INTEIRA: não há venda a cobrar nem nota a
        // emitir (é um brinde). Só resgata o cupom e limpa.
        if (itens.Count == 0)
        {
            if (!Dialogo.Confirmar(dono, "Cortesia",
                    "A comanda toda é cortesia: não há nada a cobrar. Ao entregar, o cupom é usado e não vale mais.",
                    "Entregar os itens", "Voltar")) return;
            _ = ResgatarEConcluirBrindeAsync();
            return;
        }

        var cortesiaAplicada = _cortesiaCodigo;
        var tela = new Pagamento(_operador, _sessao, itens,
            Servicos.Emissor(), Servicos.Tef(), _loja, _lojaId);
        tela.Encerrou += desfecho =>
        {
            PainelPagamento.Content = null;
            PainelPagamento.Visibility = Visibility.Collapsed;
            if (desfecho == DesfechoVenda.Concluida)
            {
                // Cupom só morre com a venda concluída (dinheiro entrou). Falha no
                // resgate não desfaz a venda — a cortesia já foi dada; loga e segue.
                if (cortesiaAplicada is not null) _ = ResgatarSilenciosoAsync(cortesiaAplicada);
                // Esvazia ANTES de tirar a cortesia: RemoverCortesia repinta, e repintar
                // grava o rascunho. Na ordem inversa a comanda já vendida era regravada
                // por um instante — e uma queda ali deixaria um rascunho órfão de venda
                // PAGA, que o operador restauraria e cobraria de novo.
                _comanda.Clear();
                RemoverCortesia(this, new RoutedEventArgs());
            }
        };
        PainelPagamento.Content = tela;
        PainelPagamento.Visibility = Visibility.Visible;
    }

    private async Task ResgatarSilenciosoAsync(string codigo)
    {
        // O resgate não pode derrubar a venda já concluída — mas também não pode
        // ser "dispara e esquece": se a rede caiu, o cupom continuava ATIVO no
        // servidor e o cliente já tinha levado os itens (cupom reutilizável).
        // Tenta na hora; se falhar, enfileira para a fila durável reprocessar,
        // como o resto (venda, fechamento, cancelamento).
        try
        {
            var r = await _cortesias.ResgatarAsync(codigo, _operador.Nome, _loja);
            if (r.Ok) return;
        }
        catch { /* cai no enfileiramento abaixo */ }

        try
        {
            using var cx = Banco.Abrir();
            Caixa.Enfileirar(cx, null, "cortesia_resgate", codigo, codigo,
                new { codigo, operador = _operador.Nome, loja = _loja });
        }
        catch { /* último recurso: nada mais a fazer sem derrubar a venda */ }
    }

    private async Task ResgatarEConcluirBrindeAsync()
    {
        var dono = Window.GetWindow(this)!;
        var r = await _cortesias.ResgatarAsync(_cortesiaCodigo!, _operador.Nome, _loja);
        if (!r.Ok)
        {
            Dialogo.Avisar(dono, "Cortesia não saiu", r.Erro switch
            {
                "already_redeemed" => "O cupom já tinha sido usado. Não entregue de graça: cobre a venda normal.",
                "loja_errada" => "O cupom é de outra loja. Só pode ser usado lá.",
                "sem_rede" => "Sem internet para usar o cupom agora. Tente de novo em alguns segundos.",
                _ => "Não deu para usar o cupom agora. Tente de novo.",
            }, "erro");
            return;
        }
        Caixa.Auditar(Banco.Abrir(), null, "cortesia_entregue", _operador.Id, null,
            $"cupom={_cortesiaCodigo} (comanda inteira em cortesia)");
        _comanda.Clear();                                   // esvazia antes de repintar (ver Finalizar)
        RemoverCortesia(this, new RoutedEventArgs());
        Dialogo.Avisar(dono, "Cortesia entregue", "O cupom foi usado e não vale mais.", "ok");
    }

    // ── CICLO DO DINHEIRO ───────────────────────────────────────────────────
    private void Sangria(object sender, RoutedEventArgs e) => Movimento("sangria");

    /// <summary>
    /// Troca a impressora do cupom SEM voltar na configuração (que exige senha de
    /// admin): bobina acabou/entalou no meio do expediente, o operador aponta pra
    /// outra e segue vendendo. Escolha operacional, não fiscal — não precisa de senha.
    /// </summary>
    private async void TrocarImpressora(object sender, RoutedEventArgs e)
    {
        var dono = Window.GetWindow(this)!;
        List<string> nomes;
        try { nomes = (await Impressao.ImpressorasAsync()).ToList(); }
        catch (Exception ex)
        {
            Dialogo.Avisar(dono, "Impressoras",
                "Não consegui ler a lista de impressoras deste computador. Tente de novo; " +
                "se continuar assim, chame o suporte.\n\nDetalhe: " + ex.Message, "erro");
            return;
        }

        using var cx = Banco.Abrir();
        var atual = Vendas.Config(cx, "impressora");

        var janela = new Window
        {
            Title = "Impressora do cupom",
            Owner = dono,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height,
            Width = 420,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)Application.Current.Resources["Fundo"],
        };
        var painel = new StackPanel { Margin = new Thickness(20) };
        painel.Children.Add(new TextBlock
        {
            Text = "Pra onde sai o cupom?",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["Texto"],
            Margin = new Thickness(0, 0, 0, 10),
        });
        var combo = new ComboBox { FontSize = 16, Padding = new Thickness(10, 8, 10, 8) };
        combo.Items.Add("(padrão do Windows)");
        foreach (var n in nomes) combo.Items.Add(n);
        combo.SelectedIndex = atual is { Length: > 0 } && combo.Items.Contains(atual)
            ? combo.Items.IndexOf(atual) : 0;
        painel.Children.Add(combo);
        // Impressão automática mora aqui também (decisão operacional, sem admin):
        // balcão com fila desliga, quem quer papel sempre liga.
        var chkAuto = new CheckBox
        {
            Content = "Imprimir o cupom sozinho ao fim de cada venda",
            IsChecked = Vendas.Config(cx, "imprimir_automatico", "1") != "0",
            FontSize = 14,
            Foreground = (Brush)Application.Current.Resources["Texto"],
            Margin = new Thickness(0, 12, 0, 0),
        };
        painel.Children.Add(chkAuto);
        var ok = new Button
        {
            Content = "Usar esta impressora",
            Style = (Style)Application.Current.Resources["BotaoPrincipal"],
            Margin = new Thickness(0, 14, 0, 0),
        };
        ok.Click += (_, _) => { janela.DialogResult = true; };
        painel.Children.Add(ok);
        janela.Content = painel;

        if (janela.ShowDialog() != true) return;
        var escolhida = combo.SelectedItem as string;
        if (escolhida is null || escolhida.StartsWith("(padrão"))
            cx.Execute("DELETE FROM config WHERE chave='impressora'");
        else Vendas.GravarConfig(cx, "impressora", escolhida);
        Vendas.GravarConfig(cx, "imprimir_automatico", chkAuto.IsChecked == false ? "0" : "1");
        Caixa.Auditar(cx, null, "impressora_trocada", _operador.Id, null,
            (escolhida ?? "padrão do Windows") + (chkAuto.IsChecked == false ? " · impressão automática OFF" : ""));
        Dialogo.Avisar(dono, "Impressora",
            $"Os cupons passam a sair em {escolhida ?? "impressora padrão do Windows"}.", "ok");
    }
    // ── PÓS-VENDA: cancelar a venda, cancelar a nota, estornar, reimprimir ────

    /// <summary>
    /// Menu do botão "Cancelar venda" da barra. TRÊS ATOS DIFERENTES, e só o
    /// terceiro precisa de maquininha:
    ///
    ///  · cancelar a VENDA                  → UPDATE no banco deste caixa;
    ///  · cancelar a NFC-e (evento 110111)  → agente fiscal local (127.0.0.1), que
    ///    é quem tem o certificado A1 da loja;
    ///  · estornar o cartão/PIX (CNC)       → maquininha INTEGRADA.
    ///
    /// Até 29/08/2026 os três moravam dentro do estorno e o menu INTEIRO abria só
    /// com `Servicos.Operavel()` não-nulo. Em loja de MAQUININHA AVULSA o operador
    /// lia "chame o gerente para configurar" — conselho errado: ele não precisava
    /// de maquininha, precisava cancelar uma nota, e a NFC-e morre em 30 minutos.
    /// Não existia caminho nenhum, e a janela fechava em silêncio.
    ///
    /// A porta continua sendo esta (mesmo botão da barra, mesmo menu de opções):
    /// inventar uma navegação nova para o caixa aprender seria trocar um problema
    /// por outro. O que mudou é que o TEF barra só o que é dele.
    /// </summary>
    private async void MenuCancelamento(object sender, RoutedEventArgs e)
    {
        var dono = Window.GetWindow(this)!;
        if (_comanda.Count > 0)
        {
            Dialogo.Avisar(dono, "Comanda aberta",
                "Termine ou limpe a comanda antes de cancelar, estornar ou reimprimir.", "erro");
            return;
        }
        if (TefEmAndamento(dono)) return;

        // O menu se monta pela CONFIG (`tef_habilitado`), não por o provedor estar de
        // pé neste segundo: num caixa com maquininha integrada e o PayGo fechado,
        // sumir com as opções do cartão esconderia justamente o que o operador
        // precisa entender. Quem confere se ela responde é a opção do estorno.
        // (E ler a config não constrói o cliente do TEF — construir aqui disputaria
        // a pasta do PayGo com uma cobrança em voo.)
        bool temTef;
        using (var cx = Banco.Abrir()) temTef = Vendas.Config(cx, "tef_habilitado") == "1";

        // Menu administrativo do PayGo e roteiro de homologação saíram (28/08): o ADM
        // é o painel web da PayGo, e a homologação terminou — opção que ninguém usa
        // só aumenta a chance de tocar na errada com o cliente esperando.
        var opcoes = new List<string> { "Cancelar uma venda (e a nota fiscal)" };
        if (temTef)
        {
            opcoes.Add("Estornar o cartão/PIX de uma venda");
            opcoes.Add("Reimprimir o último comprovante");
        }
        // Maquininha avulsa tem UMA opção: perguntar "o que você quer fazer?" com uma
        // única resposta possível é um toque a mais com o cliente no balcão.
        var escolha = opcoes.Count == 1
            ? 0
            : EscolherOpcao(dono, "Cancelar venda", "O que você quer fazer?", opcoes.ToArray());
        if (escolha < 0) return;
        // Enquanto isto corre (o PayGo pode ficar com a tela/pinpad, e a autorização
        // acende o celular da gerência), a venda não pode seguir por baixo:
        // Finalizar/Fechar caixa/Sair conferem _tefOcupado.
        BtnCancelar.IsEnabled = false;
        _tefOcupado = true;
        try
        {
            switch (escolha)
            {
                case 0:
                    await CancelarVendaAsync(dono);
                    break;
                case 1:
                    // O ÚNICO ato que precisa de maquininha — e o aviso, quando ela não
                    // responde, aponta para o caminho que continua aberto.
                    var cli = Servicos.Operavel();
                    if (cli is null)
                        Dialogo.Avisar(dono, "Maquininha",
                            "A maquininha integrada não respondeu — sem ela o PDV não devolve o cartão. " +
                            "Confira se ela está ligada e com o programa dela aberto.\n\n" +
                            "Para cancelar a VENDA e a NOTA você não precisa dela: volte e escolha " +
                            "\"Cancelar uma venda\".", "erro");
                    else await EstornarTefAsync(dono, cli);
                    break;
                case 2:
                    await ReimprimirComprovanteAsync(dono);
                    break;
            }
        }
        finally { _tefOcupado = false; BtnCancelar.IsEnabled = true; }
    }

    private static void GuardarPasso(string numero, string? intencao, string resultado)
    {
        try
        {
            using var cx = Banco.Abrir();
            cx.Execute("CREATE TABLE IF NOT EXISTS homolog_passo (numero TEXT PRIMARY KEY, intencao TEXT, resultado TEXT, quando TEXT)");
            cx.Execute("""
                INSERT INTO homolog_passo (numero, intencao, resultado, quando) VALUES (@N,@I,@R,@Q)
                ON CONFLICT(numero) DO UPDATE SET intencao=COALESCE(excluded.intencao, intencao), resultado=excluded.resultado, quando=excluded.quando
                """, new { N = numero, I = intencao, R = resultado, Q = DateTime.Now.ToString("o") });
        }
        catch { /* o roteiro não pode cair por causa do registro */ }
    }

    private static Dictionary<string, (string? Intencao, string Resultado)> PassosFeitos()
    {
        var d = new Dictionary<string, (string?, string)>(StringComparer.Ordinal);
        try
        {
            using var cx = Banco.Abrir();
            cx.Execute("CREATE TABLE IF NOT EXISTS homolog_passo (numero TEXT PRIMARY KEY, intencao TEXT, resultado TEXT, quando TEXT)");
            foreach (var l in cx.Query("SELECT numero, intencao, resultado FROM homolog_passo"))
                d[(string)l.numero] = ((string?)l.intencao, (string?)l.resultado ?? "");
        }
        catch { }
        return d;
    }

    /// <summary>Reconstrói a transação de uma intenção (para estornar) a partir de `tef_transacao`.</summary>
    private static TransacaoPayGo? TransacaoDaIntencao(string intencao)
    {
        using var cx = Banco.Abrir();
        var l = cx.QueryFirstOrDefault("""
            SELECT id, identificacao, tipo, valor_cent, parcelas, resposta_txt
              FROM tef_transacao
             WHERE identificacao = @I AND situacao = 'pago'
             ORDER BY criado_em DESC LIMIT 1
            """, new { I = intencao });
        if (l is null) return null;
        return new TransacaoPayGo((string)l.id, (string)l.identificacao,
            TipoTefExtensoes.Analisar((string?)l.tipo) ?? TipoTef.Credito,
            (long)l.valor_cent, (int)(long)l.parcelas, "pago", RespostaPayGo.Analisar((string?)l.resposta_txt));
    }

    /// <summary>
    /// Estorno OU cancelamento em curso: nada de vender, fechar caixa ou sair por
    /// baixo dele. Vale para os dois porque o cancelamento também tem passo que
    /// não se interrompe (o evento 110111 já mandado à SEFAZ).
    /// </summary>
    private bool _tefOcupado;

    private bool TefEmAndamento(Window dono)
    {
        if (!_tefOcupado) return false;
        Dialogo.Avisar(dono, "Espere terminar",
            "Tem um cancelamento ou estorno em andamento. Termine ele antes de continuar aqui.", "erro");
        return true;
    }

    /// <summary>Diálogo de opções (um botão por linha, rolável). Devolve o índice escolhido ou -1.</summary>
    private static int EscolherOpcao(Window dono, string titulo, string pergunta, params string[] opcoes)
    {
        var janela = new Window
        {
            Title = titulo,
            Owner = dono,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height,
            Width = 560,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)Application.Current.Resources["Fundo"],
        };
        var painel = new StackPanel { Margin = new Thickness(20) };
        painel.Children.Add(new TextBlock
        {
            Text = pergunta,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["Texto"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        var escolhido = -1;
        for (var i = 0; i < opcoes.Length; i++)
        {
            var idx = i;
            var b = new Button
            {
                Content = opcoes[i],
                Style = (Style)Application.Current.Resources["BotaoBase"],
                Margin = new Thickness(0, 6, 0, 0),
                MinHeight = 60,
                FontSize = 16,
            };
            b.Click += (_, _) => { escolhido = idx; janela.DialogResult = true; };
            painel.Children.Add(b);
        }
        var voltar = new Button
        {
            Content = "Voltar",
            Style = (Style)Application.Current.Resources["BotaoBase"],
            Margin = new Thickness(0, 14, 0, 0),
            MinHeight = 52,
            FontSize = 15,
        };
        voltar.Click += (_, _) => { janela.DialogResult = false; };
        painel.Children.Add(voltar);
        janela.Content = new ScrollViewer { Content = painel, MaxHeight = 680, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return janela.ShowDialog() == true ? escolhido : -1;
    }

    /// <summary>
    /// Estorno = CNC no PayGo E cancelamento da venda no PDV, na mesma ação (regra documentada
    /// em docs/TEF_PAYGO_homologacao.md): a linha do TEF vira 'estornada' e sai da soma do
    /// fechamento — se a venda continuasse 'finalizada', o caixa acusaria um cartão que não
    /// existe mais. Ordem: pré-checar (nota fiscal, turno) → motivo e confirmação →
    /// AUTORIZAÇÃO (código de 6 dígitos no WhatsApp da gerência; PIN do supervisor como
    /// saída quando a nuvem não responde) → CNC → só com o CNC aprovado cancela a venda.
    /// Venda com mais de um cartão só é cancelada no último.
    ///
    /// A autorização inteira mora em <see cref="Autorizacao.ResolverAsync"/> — inclusive a
    /// regra do modo de homologação. Aqui em cima só entra o que a decisão devolveu.
    /// </summary>
    private async Task EstornarTefAsync(Window dono, IProvedorTefOperavel cli)
    {
        List<dynamic> linhas;
        using (var cx = Banco.Abrir())
            linhas = cx.Query("""
                SELECT v.id AS venda_id, v.numero_local, v.finalizada_em, v.fiscal_status,
                       v.nfce_chave, v.nfce_protocolo,
                       p.forma, p.valor_cent, p.tef_nsu,
                       t.id AS tef_id, t.identificacao, t.valor_cent AS tef_valor, t.parcelas, t.resposta_txt
                  FROM venda v
                  JOIN venda_pagamento p ON p.venda_id = v.id AND p.tef_nsu IS NOT NULL
                  -- NSU (012) é contador curto e repete entre dias/redes: casar só no turno, pelo
                  -- mesmo valor e forma — senão o CNC pode sair para a transação ERRADA.
                  JOIN tef_transacao t ON t.provedor IN ('paygo','controlpay') AND t.situacao = 'pago' AND t.nsu = p.tef_nsu
                                      AND t.criado_em >= @Desde AND t.valor_cent = p.valor_cent AND t.tipo = p.forma
                 WHERE v.sessao_id = @Ses AND v.status = 'finalizada'
                 ORDER BY v.finalizada_em DESC, t.criado_em DESC LIMIT 12
                """, new { Ses = _sessao.Id, Desde = _sessao.AberturaEm.ToString("o") }).ToList();
        if (linhas.Count == 0)
        {
            Dialogo.Avisar(dono, "Estorno",
                "Nenhuma venda deste turno foi paga na maquininha. Só dá para estornar cartão ou PIX daqui — " +
                "venda em dinheiro você devolve na mão.", "erro");
            return;
        }

        static string Forma(string f) => f switch { "credito" => "Crédito", "debito" => "Débito", "pix" => "PIX", _ => f };
        static string Hora(object? fe) => fe is string s && DateTime.TryParse(s, out var dt) ? dt.ToString("HH:mm") : "--:--";
        var rotulos = linhas.Select(l =>
            $"Venda #{l.numero_local} · {Hora(l.finalizada_em)} · {Forma((string)l.forma)} {new Dinheiro((long)l.valor_cent).Formatado()} · NSU {l.tef_nsu}")
            .ToArray();
        // O NSU é a única coisa aqui que também está no papel do cliente: é por ele
        // que se confere se a linha escolhida é a do comprovante em cima do balcão.
        var i = EscolherOpcao(dono, "Estorno",
            "Qual pagamento você vai estornar? O NSU está no comprovante do cliente.", rotulos);
        if (i < 0) return;
        var l = linhas[i];
        string vendaId = (string)l.venda_id, nsu = (string)l.tef_nsu;
        var valor = new Dinheiro((long)l.valor_cent);
        var numero = (long)l.numero_local;

        // NOTA FISCAL. Autorizada com protocolo: o PDV CANCELA na SEFAZ antes de
        // devolver o dinheiro (28/08 — antes mandava fazer no ERP).
        //
        // 'contingencia' continua sendo RECUSA e nao e descuido: nota offline
        // (tpEmis 9) ainda nao foi autorizada, entao nao tem protocolo, e o
        // evento 110111 exige um. Sem ele o agente devolve 400 — recusar aqui,
        // com o motivo certo, e melhor que falhar la na frente.
        var fiscal = (string?)l.fiscal_status;
        var chaveNfce = (string?)l.nfce_chave;
        var protNfce = (string?)l.nfce_protocolo;
        if (fiscal == "contingencia")
        {
            Dialogo.Avisar(dono, "Nota pendente",
                $"A nota da venda #{numero} saiu sem aprovação e ainda está pendente — sem isso o estorno " +
                "não pode sair daqui. Chame o gerente para resolver a nota primeiro.", "erro");
            return;
        }
        var precisaCancelarNota = fiscal == "autorizada";
        if (precisaCancelarNota && (chaveNfce ?? "").Trim().Length != 44)
        {
            Dialogo.Avisar(dono, "Nota sem os dados",
                $"A venda #{numero} tem nota aprovada, mas os dados dela não estão neste caixa — " +
                "o estorno não pode sair daqui. Chame o gerente para cancelar a nota pelo sistema.", "erro");
            return;
        }

        // O motivo VIRA A JUSTIFICATIVA da SEFAZ quando ha nota: 15 a 255 chars
        // e regra dela, nao capricho nosso.
        var pedeJust = precisaCancelarNota;
        var motivo = PedirTexto.Mostrar(dono, "Estorno",
            pedeJust ? $"Por que está estornando? Escreva pelo menos {CancelamentoFiscal.JustificativaMinima} " +
                       "letras — isso vai junto no cancelamento da nota."
                     : "Por que está estornando? (obrigatório)",
            "venda cancelada por desistência do cliente");
        if (string.IsNullOrWhiteSpace(motivo)) return;
        if (pedeJust && !CancelamentoFiscal.JustificativaValida(motivo))
        {
            Dialogo.Avisar(dono, "Motivo curto",
                $"Escreva o motivo com {CancelamentoFiscal.JustificativaMinima} a " +
                $"{CancelamentoFiscal.JustificativaMaxima} letras. É o que vai junto no cancelamento da nota.", "erro");
            return;
        }

        if (!Dialogo.Confirmar(dono, "Estorno",
                $"Venda #{numero} · {valor.Formatado()} em {Forma((string)l.forma)}.\n\n" +
                (precisaCancelarNota ? "A nota vai ser cancelada, o valor volta " : "O valor volta ") +
                "no cartão do cliente e a venda sai do caixa.\n\n" +
                "A maquininha pode pedir a senha da loja e o cartão do cliente.",
                "Estornar agora", "Voltar", perigo: true)) return;

        // ── AUTORIZAÇÃO ──────────────────────────────────────────────────────
        // Vem DEPOIS do motivo e da confirmação de propósito: quem aprova recebe um
        // WhatsApp de verdade, e a edge só deixa 5 pedidos por caixa a cada 10 min.
        // Pedir antes acenderia o celular da gerente para todo estorno que o operador
        // abre e desiste — e o aviso que acende à toa é o aviso que ninguém lê.
        //
        // O modo de HOMOLOGAÇÃO continua liberando sem PIN e sem token (é decidido
        // dentro de Autorizacao.ResolverAsync): os passos 20/21/22/54 do roteiro
        // PayGo são estornos, rodados com a loja fechada e sem gerente para aprovar.
        DesfechoAutorizacao aut;
        // Fica FORA do using: o pedido é a identidade deste estorno (referência,
        // valor, NSU, venda, loja) e é ele que viaja para a nuvem lá embaixo, se o
        // estorno acabar saindo sem aprovação remota.
        PedidoAutorizacao pedidoAut;
        using (var cxa = Banco.Abrir())
        {
            pedidoAut = new PedidoAutorizacao(
                Autorizacao.NomeDoTerminal(cxa),
                Autorizacao.Referencia((string)l.tef_id, nsu, valor.Centavos, numero),
                valor.Centavos,
                Loja: cxa.ExecuteScalar<string?>("SELECT loja_nome FROM terminal LIMIT 1"),
                Operador: _operador.Nome,
                Venda: numero.ToString(),
                Forma: Forma((string)l.forma),
                Nsu: nsu);
            var telaAut = new TelaAutorizacao(dono);
            try
            {
                aut = await Autorizacao.ResolverAsync(cxa, Servicos.Autorizador(), pedidoAut,
                    _operador, telaAut);
            }
            catch (Exception ex)
            {
                // ÚLTIMA REDE ANTES DE O PROCESSO MORRER. Este método é chamado de
                // MenuTef, que é `async void` e tem finally mas NÃO tem catch, e o
                // App não registra DispatcherUnhandledException: qualquer exceção
                // que escape daqui encerra o Pdv.exe no meio do atendimento — e o
                // operador nem chega a ver a saída do PIN. Já aconteceu por um
                // ConfigureAwait(false) no núcleo (corrigido; teste UI-1/UI-2/UI-3),
                // e nenhuma falha da autorização vale o caixa fechando sozinho.
                //
                // A degradação é a que o dono já decidiu para quando a nuvem não
                // colabora: PIN do supervisor, com o estorno marcado como SEM
                // APROVAÇÃO REMOTA na auditoria. Se nem o PIN sair, o estorno é
                // recusado — mas o caixa continua de pé.
                aut = new DesfechoAutorizacao(ViaAutorizacao.Recusada, null, null, null,
                    "o pedido de aprovação não saiu deste caixa (" + ex.Message + ")");
                try
                {
                    var supEmergencia = await telaAut.PedirPinAsync(
                        "o pedido de aprovação por WhatsApp não saiu deste caixa");
                    aut = supEmergencia is null
                        ? aut with { Avisado = true }
                        : new DesfechoAutorizacao(ViaAutorizacao.Pin, supEmergencia, null, null,
                            "o pedido de aprovação por WhatsApp não saiu deste caixa");
                }
                catch { /* nem o PIN deu: recusa o estorno, mas não derruba o PDV */ }
            }
        }
        if (!aut.Autorizado)
        {
            if (!aut.Avisado)
                Dialogo.Avisar(dono, "Estorno não autorizado",
                    aut.Motivo + ".\n\nNada foi estornado. Chame o gerente.", "erro");
            return;
        }
        var sup = aut.Supervisor;
        // Diferente da sangria, o próprio supervisor PODE autorizar pelo PIN (loja de uma
        // pessoa só não teria como estornar) — o PayGo ainda exige a senha do lojista no
        // CNC, e a auditoria marca a auto-autorização em destaque. Aprovado por WhatsApp
        // não existe auto-autorização: quem aprovou está fora da loja.
        var autoAutorizado = sup is not null && sup.Id == _operador.Id ? " [AUTO-AUTORIZADO]" : "";
        var trilha = Autorizacao.Trilha(aut);

        // ── NOTA FISCAL PRIMEIRO ─────────────────────────────────────────────
        // A ordem e o coracao deste fluxo: cancela a NOTA, so entao devolve o
        // dinheiro. Ao contrario, um processo que morre no meio deixa o cliente
        // reembolsado com uma NFC-e valida — que e o caso que nao pode existir.
        // Se o cancelamento falhar, nada aconteceu ainda: dinheiro, nota e venda
        // seguem intactos, e o operador tenta de novo.
        if (precisaCancelarNota)
        {
            using (var esperando = new Espera(dono, "Cancelando a nota fiscal…"))
            {
                var rc = await CancelamentoFiscal.CancelarAsync(
                    Servicos.AgenteUrl(), chaveNfce!, protNfce, motivo!);
                if (!rc.Ok)
                {
                    using (var cxn = Banco.Abrir())
                        Caixa.Auditar(cxn, null, "nfce_cancelamento_negado", _operador.Id, null,
                            $"venda={numero} chave={chaveNfce} — {rc.Mensagem}");
                    esperando.Dispose();
                    Dialogo.Avisar(dono,
                        rc.Indisponivel ? "Nota sem resposta" : "Nota não cancelada",
                        rc.Indisponivel
                            ? $"{rc.Mensagem}.\n\nNada foi estornado. A nota pode ter sido cancelada mesmo sem a " +
                              "resposta chegar — chame o gerente para conferir antes de tentar de novo."
                            : $"{rc.Mensagem}.\n\nNada mudou: a nota, o dinheiro e a venda seguem como estavam. " +
                              "Tente de novo; se continuar, chame o gerente.",
                        "erro");
                    return;
                }

                // Sucesso: gravar ANTES do CNC. Se o caixa morrer aqui, a nota esta
                // cancelada e o PDV sabe — no retry o pre-check ve 'cancelada' e vai
                // direto ao dinheiro. Gravar DEPOIS abriria a janela proibida.
                using (var cxn = Banco.Abrir())
                {
                    cxn.Execute("UPDATE venda SET fiscal_status = 'cancelada' WHERE id = @Id",
                        new { Id = (string)l.venda_id });
                    Caixa.Auditar(cxn, null, "nfce_cancelada_sefaz", _operador.Id, null,
                        $"venda={numero} chave={chaveNfce} — {rc.Mensagem}");
                }
            }
        }

        var original = new TransacaoPayGo((string)l.tef_id, (string?)l.identificacao ?? "",
            TipoTefExtensoes.Analisar((string)l.forma) ?? TipoTef.Credito, (long)l.tef_valor, (int)(long)l.parcelas, "pago",
            RespostaPayGo.Analisar((string?)l.resposta_txt));

        DesfechoTef d;
        try { d = await cli.CancelarAsync(original, CancellationToken.None); }
        catch (Exception ex)
        {
            Dialogo.Avisar(dono, "Maquininha sem resposta",
                "A maquininha não respondeu. Confira se ela está ligada e o programa dela aberto, " +
                "e veja no comprovante se o estorno saiu antes de tentar de novo.\n\nDetalhe: " + ex.Message, "erro");
            return;
        }
        if (!d.Pago)
        {
            // Estorno NEGADO pela rede: nenhum dinheiro voltou, então isto não entra na
            // lista dos estornos que escaparam do token — mas a trilha fica registrada.
            using var cxn = Banco.Abrir();
            Caixa.Auditar(cxn, null, "tef_estorno_negado", _operador.Id, aut.Autorizador,
                $"venda={numero} nsu={nsu} {d.Motivo}{autoAutorizado}{trilha}");
            Dialogo.Avisar(dono, "Estorno negado",
                $"A maquininha não aprovou o estorno: {d.MensagemParaTela}.\n\n" +
                "O dinheiro não voltou para o cliente." +
                (precisaCancelarNota ? " A nota fiscal já foi cancelada — chame o gerente." : " Tente de novo."),
                "erro");
            return;
        }

        // CNC aprovado = o cartão foi devolvido. O cliente já marca a original 'estornada'; isto é
        // cinto e suspensório (se aquele Guardar falhou, a contagem abaixo mentiria).
        Servicos.MarcarEstornada((string)l.tef_id, "estornada por " + d.ChargeId);

        // A venda sai do caixa no MESMO ato. Só cancela quando não sobrou outro cartão/PIX nela
        // (mesma janela/valor/forma do JOIN da lista, e nunca o que acabou de ser estornado);
        // dinheiro, o operador devolve em espécie.
        using (var cx = Banco.Abrir())
        {
            var restantes = cx.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM venda_pagamento p
                  JOIN tef_transacao t ON t.provedor IN ('paygo','controlpay') AND t.situacao = 'pago' AND t.nsu = p.tef_nsu
                                      AND t.criado_em >= @Desde AND t.valor_cent = p.valor_cent AND t.tipo = p.forma
                 WHERE p.venda_id = @V AND t.id <> @TefId AND p.tef_nsu <> @Nsu
                """, new { V = vendaId, TefId = (string)l.tef_id, Nsu = nsu, Desde = _sessao.AberturaEm.ToString("o") });
            var dinheiro = cx.ExecuteScalar<long>(
                "SELECT COALESCE(SUM(valor_cent - troco_cent), 0) FROM venda_pagamento WHERE venda_id = @V AND forma = 'dinheiro'",
                new { V = vendaId });
            var detalhe = $"venda={numero} nsu={nsu} valor={valor.Formatado()} — {motivo}{autoAutorizado}{trilha}";

            // O DINHEIRO JÁ VOLTOU (o CNC foi aprovado): é aqui, e só aqui, que o estorno
            // vira fato consumado. Se este estorno escapou do token, a linha própria sai
            // AGORA — antes disso a lista do dono encheria de estorno que nem aconteceu.
            // Sai em DOIS lugares na mesma transação: a auditoria local e a FILA da nuvem
            // (a tabela `auditoria` nunca sobe, então sem a fila a lista do dono ficaria
            // presa no disco deste caixa — exatamente no cenário de internet caída).
            Autorizacao.AuditarSemAprovacaoRemota(cx, aut, _operador.Id,
                $"venda={numero} nsu={nsu} valor={valor.Formatado()}", pedidoAut, vendaId);

            if (restantes > 0)
            {
                Caixa.Auditar(cx, null, "tef_estorno", _operador.Id, aut.Autorizador, detalhe + $" (venda segue: restam {restantes} cartão/PIX)");
                Dialogo.Avisar(dono, "Cartão estornado",
                    $"{valor.Formatado()} voltou para o cliente. A venda #{numero} ainda tem " +
                    $"{Conta(restantes, "outro pagamento", "outros pagamentos")} na maquininha — " +
                    "estorne também, senão a venda continua aberta.", "ok");
                return;
            }
            try
            {
                Vendas.Cancelar(cx, vendaId, _operador.Id, $"estorno TEF NSU {nsu}: {motivo}", aut.Autorizador);
            }
            catch (Exception ex)
            {
                Caixa.Auditar(cx, null, "tef_estorno", _operador.Id, aut.Autorizador, detalhe + " — CARTÃO ESTORNADO, venda não cancelada: " + ex.Message);
                Dialogo.Avisar(dono, "Venda ainda aberta",
                    $"O dinheiro voltou para o cliente, mas a venda #{numero} continua no caixa. " +
                    "Chame o gerente para cancelar a venda.\n\nDetalhe: " + ex.Message, "erro");
                return;
            }
            Caixa.Auditar(cx, null, "tef_estorno", _operador.Id, aut.Autorizador, detalhe);
            Dialogo.Avisar(dono, "Estorno feito",
                $"{valor.Formatado()} voltou para o cliente (NSU {nsu}) e a venda #{numero} foi cancelada." +
                (dinheiro > 0 ? $" Essa venda também tinha {new Dinheiro(dinheiro).Formatado()} em dinheiro — devolva na mão." : ""),
                "ok");
        }
    }

    /// <summary>Reimprime as vias do último comprovante do PayGo (venda ou estorno) — a partir do .001 guardado.</summary>
    private async Task ReimprimirComprovanteAsync(Window dono)
    {
        string? txt, impressora;
        using (var cx = Banco.Abrir())
        {
            txt = cx.ExecuteScalar<string?>("""
                SELECT resposta_txt FROM tef_transacao
                 WHERE provedor IN ('paygo','controlpay') AND situacao IN ('pago','estornado','cnf_sem_ack') AND resposta_txt IS NOT NULL
                 ORDER BY atualizado_em DESC LIMIT 1
                """);
            impressora = Vendas.Config(cx, "impressora");
        }
        if (txt is null)
        {
            Dialogo.Avisar(dono, "Reimpressão",
                "Este caixa ainda não tem nenhum comprovante de cartão para reimprimir.", "erro");
            return;
        }
        // ViasParaImprimir devolve na ordem em que saem: [cliente, estabelecimento].
        // Reimprimir as duas sempre gastava papel à toa — quase sempre só uma se
        // perdeu (a do cliente rasgou, ou a da loja foi pro cliente por engano).
        var todas = Servicos.ViasParaImprimir(RespostaPayGo.Analisar(txt));
        if (todas.Count == 0)
        {
            Dialogo.Avisar(dono, "Reimpressão",
                "O último pagamento não gerou comprovante para imprimir.", "erro");
            return;
        }
        IReadOnlyList<IReadOnlyList<string>> blocos;
        if (todas.Count == 1)
        {
            blocos = todas;   // via única: perguntar seria pergunta sem resposta
        }
        else
        {
            var qual = EscolherOpcao(dono, "Reimprimir comprovante", "Qual via você precisa?",
                new[] { "Via do cliente", "Via da loja", "As duas" });
            if (qual < 0) return;
            blocos = qual switch
            {
                0 => new[] { todas[0] },
                1 => new[] { todas[1] },
                _ => todas,
            };
        }
        var erro = await Impressao.ImprimirTextoAsync("Comprovante TEF (reimpressão)", blocos, impressora);
        Dialogo.Avisar(dono, "Reimpressão",
            erro is null
                ? (blocos.Count > 1 ? "As duas vias foram para a impressora." : "A via foi para a impressora.")
                : erro + "\n\nConfira se a impressora está ligada e com papel, e tente de novo.",
            erro is null ? "ok" : "erro");
    }

    /// <summary>
    /// CANCELAR UMA VENDA — com ou sem maquininha. Faz DUAS coisas, nesta ordem:
    ///   1. cancela a NFC-e na SEFAZ (evento 110111, pelo agente fiscal local);
    ///   2. cancela a VENDA no caixa.
    ///
    /// A ORDEM É INEGOCIÁVEL e já era regra do núcleo: <see cref="Vendas.Cancelar"/>
    /// RECUSA venda com nota viva, porque documento válido para venda que não existe
    /// mais é divergência que aparece na apuração do contador, não no caixa. Se o
    /// passo 1 falhar, nada aconteceu — nota, venda e dinheiro seguem como estavam,
    /// e o operador tenta de novo.
    ///
    /// O QUE ELA NÃO FAZ É DEVOLVER DINHEIRO, e a tela repete isso duas vezes (na
    /// confirmação e no fim). Em maquininha AVULSA o estorno é na maquininha, na mão
    /// do operador; o PDV só registra que a venda caiu. Operador que cancela a nota e
    /// acha que o cliente foi reembolsado custa dinheiro de verdade. Com maquininha
    /// integrada existe caminho melhor — o estorno, que devolve e cancela no mesmo
    /// ato — e a tela manda usar ele em vez de abrir um segundo caminho de dinheiro.
    /// </summary>
    private async Task CancelarVendaAsync(Window dono)
    {
        // SÓ O TURNO ABERTO. Venda de turno fechado tem caixa já apurado e conferido;
        // derrubar uma por aqui mudaria um fechamento que alguém assinou. E o prazo de
        // 30 min da NFC-e praticamente garante que o alvo está no turno de agora.
        List<dynamic> linhas;
        var pagsPorVenda = new Dictionary<string, List<PagamentoDaVenda>>(StringComparer.Ordinal);
        bool temTef;
        using (var cx = Banco.Abrir())
        {
            linhas = cx.Query("""
                SELECT v.id AS venda_id, v.numero_local, v.finalizada_em, v.total_cent,
                       v.fiscal_status, v.nfce_chave, v.nfce_protocolo,
                       -- dhRecbto é a hora que a SEFAZ carimbou: é DELA que correm os
                       -- 30 minutos, não da venda nem do pagamento.
                       (SELECT n.dh_recbto FROM nfce_emissao n
                         WHERE n.venda_id = v.id AND n.chave = v.nfce_chave
                         ORDER BY n.tentativa DESC LIMIT 1) AS nota_em
                  FROM venda v
                 WHERE v.sessao_id = @Ses AND v.status = 'finalizada'
                 ORDER BY v.finalizada_em DESC LIMIT 12
                """, new { Ses = _sessao.Id }).ToList();
            if (linhas.Count > 0)
                foreach (var p in cx.Query("""
                    SELECT venda_id, forma, valor_cent - troco_cent AS liquido
                      FROM venda_pagamento WHERE venda_id IN @Ids
                    """, new { Ids = linhas.Select(x => (string)x.venda_id).ToArray() }))
                {
                    var id = (string)p.venda_id;
                    if (!pagsPorVenda.TryGetValue(id, out var lista))
                        pagsPorVenda[id] = lista = new List<PagamentoDaVenda>();
                    lista.Add(new PagamentoDaVenda((string)p.forma, (long)p.liquido));
                }
            // "Este caixa TEM maquininha integrada" é config, não o provedor no ar:
            // é só para escolher o texto do dinheiro, e construir o cliente do TEF
            // aqui disputaria a pasta do PayGo à toa.
            temTef = Vendas.Config(cx, "tef_habilitado") == "1";
        }
        if (linhas.Count == 0)
        {
            Dialogo.Avisar(dono, "Cancelar venda",
                "Nenhuma venda deste turno para cancelar.", "erro");
            return;
        }

        static DateTime? Data(object? v) =>
            v is string s && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeLocal, out var dt) ? dt.ToLocalTime() : null;
        static string Hora(object? fe) => Data(fe) is { } d ? d.ToString("HH:mm") : "--:--";
        static string Etiqueta(PlanoDeCancelamento p) => p.Nota switch
        {
            SituacaoDaNota.SemNota => "sem nota",
            SituacaoDaNota.JaCancelada => "nota já cancelada",
            SituacaoDaNota.ForaDoPrazo => "nota: PRAZO VENCIDO",
            SituacaoDaNota.SemProtocolo => "nota em contingência",
            SituacaoDaNota.SemDados => "nota sem dados aqui",
            _ => p.RestanteDaNota is { } r
                ? $"nota: {Math.Max(0, (int)r.TotalMinutes)} min para cancelar"
                : "nota autorizada",
        };

        var agora = DateTime.Now;
        var planos = new List<PlanoDeCancelamento>();
        var rotulos = new List<string>();
        foreach (var l in linhas)
        {
            var pags = pagsPorVenda.TryGetValue((string)l.venda_id, out var lp) ? lp : new List<PagamentoDaVenda>();
            var p = CancelamentoVenda.Montar((string?)l.fiscal_status, (string?)l.nfce_chave,
                (string?)l.nfce_protocolo, Data(l.nota_em) ?? Data(l.finalizada_em),
                pags, estornoPeloPdv: temTef, agora);
            planos.Add(p);
            // O RELÓGIO APARECE JÁ NA LISTA: se o operador precisa abrir uma por uma
            // para descobrir de qual dá tempo, o prazo vence enquanto ele procura.
            rotulos.Add($"Venda #{l.numero_local} · {Hora(l.finalizada_em)} · " +
                        $"{new Dinheiro((long)l.total_cent).Formatado()} · " +
                        $"{CancelamentoVenda.ResumoDasFormas(pags)} · {Etiqueta(p)}");
        }

        var i = EscolherOpcao(dono, "Cancelar venda", "Qual venda você vai cancelar?", rotulos.ToArray());
        if (i < 0) return;
        var alvo = linhas[i];
        var plano = planos[i];
        var vendaId = (string)alvo.venda_id;
        var numero = (long)alvo.numero_local;
        var total = new Dinheiro((long)alvo.total_cent);
        var chaveNfce = (string?)alvo.nfce_chave;
        var protNfce = (string?)alvo.nfce_protocolo;
        var pagsAlvo = pagsPorVenda.TryGetValue(vendaId, out var pv) ? pv : new List<PagamentoDaVenda>();

        // Fim da linha com o MOTIVO na tela — nunca a janela fechando em silêncio,
        // que era o que acontecia com maquininha avulsa.
        if (!plano.PodeSeguir)
        {
            Dialogo.Avisar(dono, "Não dá para cancelar daqui", plano.Impedimento!, "erro");
            return;
        }

        // Havendo nota, o motivo VIRA o xJust do evento: 15..255 é regra da SEFAZ,
        // não capricho nosso. Sem nota, exigir 15 letras seria capricho.
        var motivo = PedirTexto.Mostrar(dono, "Cancelar venda",
            plano.PedeJustificativaFiscal
                ? $"Por que está cancelando? Escreva pelo menos {CancelamentoFiscal.JustificativaMinima} letras — " +
                  "isso vai junto no cancelamento da nota, para a SEFAZ."
                : "Por que está cancelando? (obrigatório)",
            "venda cancelada por desistência do cliente");
        if (string.IsNullOrWhiteSpace(motivo)) return;
        if (plano.PedeJustificativaFiscal && !CancelamentoFiscal.JustificativaValida(motivo))
        {
            Dialogo.Avisar(dono, "Motivo curto",
                $"Escreva o motivo com {CancelamentoFiscal.JustificativaMinima} a " +
                $"{CancelamentoFiscal.JustificativaMaxima} letras. É o que vai junto no cancelamento da nota.", "erro");
            return;
        }

        // A CONFIRMAÇÃO DIZ O QUE ACONTECE COM O DINHEIRO, linha por forma de
        // pagamento. Sem isto o operador cancela, lê "pronto" e manda o cliente
        // embora achando que foi reembolsado.
        var oQueAcontece = new System.Text.StringBuilder();
        oQueAcontece.AppendLine($"Venda #{numero} · {total.Formatado()}");
        oQueAcontece.AppendLine();
        oQueAcontece.AppendLine(plano.TextoDaNota);
        oQueAcontece.AppendLine();
        oQueAcontece.AppendLine(CancelamentoVenda.AvisoDoDinheiro);
        foreach (var linha in plano.Dinheiro) oQueAcontece.AppendLine("• " + linha);
        if (!Dialogo.Confirmar(dono,
                plano.Arriscado ? "Prazo da nota vencido" : "Cancelar venda",
                oQueAcontece.ToString().TrimEnd(),
                plano.Arriscado ? "Tentar mesmo assim" : "Cancelar a venda", "Voltar", perigo: true)) return;

        // ── AUTORIZAÇÃO ──────────────────────────────────────────────────────
        // Cancelar nota é ato fiscal: passa pelo MESMO caminho do estorno (token de
        // 6 dígitos no WhatsApp da gerência, PIN do supervisor como saída quando a
        // nuvem não responde). Vem depois do motivo e da confirmação pelo motivo de
        // sempre: o aviso que acende à toa é o aviso que ninguém lê.
        DesfechoAutorizacao aut;
        PedidoAutorizacao pedidoAut;
        using (var cxa = Banco.Abrir())
        {
            pedidoAut = new PedidoAutorizacao(
                Autorizacao.NomeDoTerminal(cxa),
                // Referência PRÓPRIA (prefixo "cancelamento:"): um token aprovado para
                // esta venda não pode servir para cancelar outra — nem para um estorno.
                Autorizacao.ReferenciaCancelamento(vendaId, numero, total.Centavos),
                total.Centavos,
                Loja: cxa.ExecuteScalar<string?>("SELECT loja_nome FROM terminal LIMIT 1"),
                Operador: _operador.Nome,
                Venda: numero.ToString(),
                // Quem aprova pelo WhatsApp precisa saber COMO a venda foi paga: é o que
                // deixa claro que não existe estorno automático nenhum nesta aprovação.
                Forma: CancelamentoVenda.ResumoDasFormas(pagsAlvo),
                Nsu: null);
            var telaAut = new TelaAutorizacao(dono);
            try
            {
                aut = await Autorizacao.ResolverAsync(cxa, Servicos.Autorizador(), pedidoAut,
                    _operador, telaAut);
            }
            catch (Exception ex)
            {
                // MESMA REDE DO ESTORNO, e pelo mesmo motivo: este método é chamado de
                // um `async void` sem catch, e o App não registra
                // DispatcherUnhandledException — exceção que escape daqui ENCERRA o
                // Pdv.exe no meio do atendimento. Nenhuma falha de autorização vale o
                // caixa fechando sozinho.
                aut = new DesfechoAutorizacao(ViaAutorizacao.Recusada, null, null, null,
                    "o pedido de aprovação não saiu deste caixa (" + ex.Message + ")");
                try
                {
                    var supEmergencia = await telaAut.PedirPinAsync(
                        "o pedido de aprovação por WhatsApp não saiu deste caixa");
                    aut = supEmergencia is null
                        ? aut with { Avisado = true }
                        : new DesfechoAutorizacao(ViaAutorizacao.Pin, supEmergencia, null, null,
                            "o pedido de aprovação por WhatsApp não saiu deste caixa");
                }
                catch { /* nem o PIN deu: recusa o cancelamento, mas não derruba o PDV */ }
            }
        }
        if (!aut.Autorizado)
        {
            if (!aut.Avisado)
                Dialogo.Avisar(dono, "Cancelamento não autorizado",
                    aut.Motivo + ".\n\nNada foi cancelado. Chame o gerente.", "erro");
            return;
        }

        var trilha = Autorizacao.Trilha(aut);
        var autoAutorizado = aut.Supervisor is { } quem && quem.Id == _operador.Id ? " [AUTO-AUTORIZADO]" : "";
        // A auditoria diz COMO a venda foi paga e que a devolução é MANUAL: quem for
        // conferir isto depois (o dono, o contador) precisa saber que o PDV não
        // devolveu nada — senão o cancelamento parece um estorno que nunca houve.
        var detalhe = $"venda={numero} total={total.Formatado()} pago em " +
                      $"{CancelamentoVenda.ResumoDasFormas(pagsAlvo)} (devolução do dinheiro é MANUAL, " +
                      $"fora do PDV) — {motivo}{autoAutorizado}{trilha}";

        // A linha "escapou do token" sai UMA vez, no PRIMEIRO ato irreversível — que
        // aqui é a nota (evento 110111 registrado não volta atrás), e só na falta
        // dela é a venda. Sem o guarda ela sairia duas vezes e o painel do dono
        // contaria dois cancelamentos onde houve um.
        var registrou = false;
        void RegistrarEscape()
        {
            if (registrou) return;
            registrou = true;
            using var c = Banco.Abrir();
            Autorizacao.AuditarSemAprovacaoRemota(c, aut, _operador.Id,
                $"cancelamento de venda={numero} total={total.Formatado()}", pedidoAut, vendaId);
        }

        // ── A NOTA PRIMEIRO ──────────────────────────────────────────────────
        if (plano.CancelaNota)
        {
            using (var esperando = new Espera(dono, "Cancelando a nota fiscal…"))
            {
                var rc = await CancelamentoFiscal.CancelarAsync(
                    Servicos.AgenteUrl(), chaveNfce!, protNfce, motivo!);
                if (!rc.Ok)
                {
                    using (var cxn = Banco.Abrir())
                        Caixa.Auditar(cxn, null, "nfce_cancelamento_negado", _operador.Id, aut.Autorizador,
                            $"venda={numero} chave={chaveNfce} — {rc.Mensagem}{trilha}");
                    esperando.Dispose();
                    // "NÃO SEI" NUNCA VIRA "CANCELADA": nem na tela, nem no banco. A
                    // SEFAZ pode ter registrado o evento com a resposta perdida na
                    // volta — carimbar 'cancelada' aqui deixaria a venda cancelada
                    // com uma nota que talvez ainda esteja viva.
                    Dialogo.Avisar(dono,
                        rc.Indisponivel ? "Nota sem resposta" : "Nota não cancelada",
                        rc.Indisponivel
                            ? $"{rc.Mensagem}.\n\nNADA foi cancelado. A nota PODE ter sido cancelada mesmo sem a " +
                              "resposta chegar — chame o gerente para conferir antes de tentar de novo."
                            : $"{rc.Mensagem}.\n\nNada mudou: a nota e a venda seguem como estavam." +
                              (plano.Arriscado
                                  ? " O prazo de 30 minutos venceu, então daqui não sai mais: o caminho agora é " +
                                    "uma NOTA DE DEVOLUÇÃO com o contador."
                                  : " Tente de novo; se continuar, chame o gerente."),
                        "erro");
                    return;
                }

                // Sucesso: gravar ANTES de mexer na venda. Se o caixa morrer aqui, a
                // nota está cancelada e o PDV sabe — no retry a venda aparece como
                // "nota já cancelada" e só falta fechá-la.
                using (var cxn = Banco.Abrir())
                {
                    cxn.Execute("UPDATE venda SET fiscal_status = 'cancelada' WHERE id = @Id",
                        new { Id = vendaId });
                    Caixa.Auditar(cxn, null, "nfce_cancelada_sefaz", _operador.Id, aut.Autorizador,
                        $"venda={numero} chave={chaveNfce} — {rc.Mensagem}{trilha}");
                }
                RegistrarEscape();
            }
        }

        // ── E SÓ ENTÃO A VENDA ───────────────────────────────────────────────
        using (var cx = Banco.Abrir())
        {
            try
            {
                Vendas.Cancelar(cx, vendaId, _operador.Id, motivo!, aut.Autorizador);
            }
            catch (Exception ex)
            {
                Caixa.Auditar(cx, null, "venda_cancelamento_falhou", _operador.Id, aut.Autorizador,
                    detalhe + " — nota resolvida, venda NÃO cancelada: " + ex.Message);
                Dialogo.Avisar(dono, "Venda ainda aberta",
                    $"A nota foi resolvida, mas a venda #{numero} continua no caixa. " +
                    "Chame o gerente.\n\nDetalhe: " + ex.Message, "erro");
                return;
            }
            RegistrarEscape();
            // Linha separada da que `Vendas.Cancelar` grava: é esta que carrega a
            // TRILHA (quem aprovou, ou o aviso em maiúsculas de que ninguém de fora
            // aprovou). O motivo que sobe para a nuvem fica limpo do lado de lá.
            Caixa.Auditar(cx, null, "cancelamento_autorizado", _operador.Id, aut.Autorizador, detalhe);
        }

        // O RECADO DO DINHEIRO DE NOVO, agora que o operador vai virar para o
        // cliente. É a última chance de ele não mandar embora quem não recebeu nada.
        var recado = new System.Text.StringBuilder();
        recado.AppendLine($"A venda #{numero} foi cancelada" +
                          (plano.CancelaNota ? " e a nota foi cancelada na SEFAZ." : "."));
        recado.AppendLine();
        recado.AppendLine(CancelamentoVenda.AvisoDoDinheiro);
        foreach (var linha in plano.Dinheiro) recado.AppendLine("• " + linha);
        Dialogo.Avisar(dono, "Venda cancelada", recado.ToString().TrimEnd(), "ok");
    }

    private void Suprimento(object sender, RoutedEventArgs e) => Movimento("suprimento");

    private void Movimento(string tipo)
    {
        var dono = Window.GetWindow(this)!;
        var sangria = tipo == "sangria";
        var titulo = sangria ? "Sangria" : "Suprimento";
        var valor = PedirValor.Mostrar(dono, titulo,
            sangria ? "Quanto está saindo da gaveta?" : "Quanto está entrando na gaveta?");
        if (valor is null || !valor.Value.Positivo) return;

        var motivo = PedirTexto.Mostrar(dono, titulo,
            sangria ? "Para onde vai o dinheiro? (obrigatório)" : "De onde vem o dinheiro? (obrigatório)",
            sangria ? "envio ao cofre" : "reforço de troco");
        if (string.IsNullOrWhiteSpace(motivo)) return;

        string? autorizador = null;
        if (tipo == "sangria")
        {
            // Supervisor autoriza com o PIN DELE — e o nome fica no registro.
            // Autorização sem nome não serve de nada numa auditoria.
            using var cxa = Banco.Abrir();
            Operador? sup;
            {
                // "gerente" na tela: é como o balcão chama quem autoriza, e é a
                // mesma palavra da abertura de caixa. O perfil no banco (supervisor
                // ou gerente) segue igual — quem decide é ESupervisor.
                var pin = PedirSenha.Mostrar(dono, "Autorização", "PIN do gerente");
                if (pin is null) return;
                sup = Operadores.AutorizarSupervisor(cxa, pin);
            }
            if (sup is null)
            {
                Dialogo.Avisar(dono, "PIN não confere",
                    "Esse PIN não é de gerente, ou saiu errado. Peça para o gerente digitar de novo.", "erro");
                return;
            }
            // Segundo par de olhos NÃO pode ser o próprio: um gerente que opera o
            // caixa não autoriza a própria sangria — isso é sangria fantasma com
            // aval de si mesmo, o furto que a autorização existe pra impedir.
            if (sup.Id == _operador.Id)
            {
                Dialogo.Avisar(dono, "Precisa de outro gerente",
                    "Você não pode autorizar a sua própria sangria. Chame OUTRO gerente para digitar o PIN dele.", "erro");
                return;
            }
            autorizador = sup.Id;
        }

        try
        {
            using var cx = Banco.Abrir();
            Caixa.Movimentar(cx, _sessao, tipo, valor.Value, motivo!, _operador, autorizador,
                tipo == "sangria" ? "cofre" : null);
            // "Suprimento registrada" saía errado: o gênero acompanha a palavra, não o rótulo.
            Dialogo.Avisar(dono, sangria ? "Sangria registrada" : "Suprimento registrado",
                $"{valor.Value.Formatado()} — {motivo}", "ok");
        }
        catch (Exception ex)
        {
            Dialogo.Avisar(dono, sangria ? "Sangria não saiu" : "Suprimento não entrou",
                ex.Message + "\n\nNada foi registrado. Confira o valor e tente de novo.", "erro");
        }
    }

    /// <summary>
    /// Fechamento CEGO: o operador declara o que contou, forma por forma, SEM ver o
    /// esperado. Só depois o sistema mostra a diferença. Se ele visse antes, digitaria
    /// o esperado e a conferência não significaria nada.
    /// </summary>
    private void FecharCaixa(object sender, RoutedEventArgs e)
    {
        var dono = Window.GetWindow(this)!;
        if (TefEmAndamento(dono)) return;
        if (_comanda.Count > 0)
        {
            Dialogo.Avisar(dono, "Comanda aberta",
                "Termine ou limpe a comanda antes de fechar o caixa.", "erro");
            return;
        }

        // Só o que se conta de verdade — e isso muda com o caixa: com TEF, cartão e PIX
        // fecham sozinhos pelo valor do sistema; com POS avulsa, o operador digita o
        // total do FECHAMENTO DA MAQUININHA, que é fonte independente de verdade.
        var contagem = new Dictionary<string, Dinheiro>();
        string[] formasContadas;
        // olha o TURNO: mesmo com TEF, venda que saiu como POS avulsa traz a forma
        // de volta pra contagem — o total da maquininha é quem confere as duas
        using (var cxf = Banco.Abrir()) formasContadas = Caixa.FormasContadas(cxf, _sessao);
        foreach (var f in formasContadas)
        {
            var pergunta = f == "dinheiro"
                ? "Quanto você contou em dinheiro? Conte a gaveta inteira, com o fundo de troco."
                : $"Quanto deu em {Rotulo(f)} no fechamento da maquininha?";
            var v = PedirValor.Mostrar(dono, "Fechamento de caixa", pergunta);
            if (v is null) return;                 // desistiu no meio: não fecha nada
            contagem[f] = v.Value;
        }

        var tolerancia = new Dinheiro(200);        // R$ 2,00
        try
        {
            using var cx = Banco.Abrir();
            var divergencias = Caixa.DivergenciasTef(cx, _sessao);
            var resumoTeste = Caixa.ResumoDeTeste(cx, _sessao);
            MostrarResultado(dono, Caixa.Fechar(cx, _sessao, contagem, _operador, tolerancia), null, divergencias, resumoTeste);
            FechouCaixa?.Invoke();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Justifique"))
        {
            // A mensagem do Núcleo já termina pedindo a descrição; repetir
            // "O que aconteceu?" só fazia ler a mesma pergunta duas vezes.
            var just = PedirTexto.Mostrar(dono, "Diferença no caixa", ex.Message, "");
            if (string.IsNullOrWhiteSpace(just)) return;
            try
            {
                using var cx = Banco.Abrir();
                var divergencias = Caixa.DivergenciasTef(cx, _sessao);
                var resumoTeste = Caixa.ResumoDeTeste(cx, _sessao);
                MostrarResultado(dono, Caixa.Fechar(cx, _sessao, contagem, _operador, tolerancia, just), just, divergencias, resumoTeste);
                FechouCaixa?.Invoke();
            }
            catch (Exception e2)
            {
                Dialogo.Avisar(dono, "Caixa não fechou",
                    e2.Message + "\n\nO caixa continua aberto. Anote os valores que você contou e tente de novo; " +
                    "se continuar, chame o gerente.", "erro");
            }
        }
        catch (Exception ex)
        {
            Dialogo.Avisar(dono, "Caixa não fechou",
                ex.Message + "\n\nO caixa continua aberto. Anote os valores que você contou e tente de novo; " +
                "se continuar, chame o gerente.", "erro");
        }
    }

    private static void MostrarResultado(Window dono, List<LinhaFechamento> linhas, string? justificativa,
        List<DivergenciaTef> divergencias, string? resumoTeste = null)
    {
        var texto = string.Join("\n", linhas.Select(l =>
        {
            var dif = l.Situacao switch
            {
                "confere" => "confere",
                "sobra" => "SOBRA " + l.Diferenca.Abs.Formatado(),
                _ => "FALTA " + l.Diferenca.Abs.Formatado(),
            };
            // "TEF" não diz nada a quem está fechando a gaveta: o que importa é se o
            // valor foi contado à mão ou veio da maquininha.
            var origem = l.Contada ? "contou" : "máquina";
            // "esperado", e não "sistema", é a mesma palavra que a abertura usa na
            // conferência do fundo — é o valor com que a contagem tem que bater.
            return $"{Rotulo(l.Forma),-9} {origem,-7} {l.Declarado.Formatado(),11}  esperado {l.Apurado.Formatado(),11}  {dif}";
        }));

        // O desvio é a soma dos módulos. O líquido esconderia falta num lugar
        // compensada por sobra em outro, que é justamente o que se quer enxergar.
        var desvio = new Dinheiro(linhas.Sum(l => l.Diferenca.Abs.Centavos));
        var corpo = texto + $"\n\nDiferença total: {desvio.Formatado()}";

        if (linhas.Any(l => l.Situacao == "sobra"))
            corpo += "\n\nSobrou dinheiro na gaveta. Costuma ser venda feita fora do caixa\n" +
                     "— e venda assim não baixou estoque nem gerou nota.";

        // O turno teve venda de TESTE: ela ficou fora dos totais acima, e o operador
        // precisa ler isso aqui. Número que some sem explicação é número que vira
        // desconfiança do fechamento inteiro.
        if (resumoTeste is not null) corpo += "\n\n" + resumoTeste;

        if (divergencias.Count > 0)
            corpo += "\n\nMaquininha x caixa:\n" + string.Join("\n", divergencias.Select(d =>
                $"{Rotulo(d.Forma),-9} máquina {d.NoTef.Formatado(),11}  caixa {d.NaVenda.Formatado(),11}" +
                $"  diferença {d.Diferenca.Abs.Formatado()}"))
                + "\n\nA maquininha aprovou uma cobrança que não virou venda aqui (queda de\n" +
                  "energia, programa fechado ou tempo esgotado). Confira o extrato da\n" +
                  "maquininha antes de estornar qualquer coisa.";

        Dialogo.Relatorio(dono, "Caixa fechado", corpo,
            justificativa is null ? null : $"Justificativa: {justificativa}");
    }

    private static string Rotulo(string forma) => forma switch
    {
        "dinheiro" => "Dinheiro",
        "debito" => "Débito",
        "credito" => "Crédito",
        "pix" => "PIX",
        _ => forma,
    };

    /// <summary>
    /// Configuração sem sair do caixa. A senha de administrador continua sendo pedida
    /// (quem trata é a MainWindow); a comanda em andamento não se perde porque o
    /// rascunho é gravado a cada mudança — ao voltar, o caixa oferece continuar.
    /// </summary>
    private void AbrirConfiguracao(object sender, RoutedEventArgs e)
    {
        if (TefEmAndamento(Window.GetWindow(this)!)) return;
        if (_comanda.Count > 0 && !Dialogo.Confirmar(Window.GetWindow(this)!, "Configuração",
                "A comanda fica guardada. Quando você voltar, o caixa pergunta se quer continuar com ela.",
                "Abrir configuração", "Voltar"))
            return;
        PediuConfig?.Invoke();
    }

    private void Sair(object sender, RoutedEventArgs e)
    {
        if (TefEmAndamento(Window.GetWindow(this)!)) return;
        if (_comanda.Count > 0 && !Dialogo.Confirmar(Window.GetWindow(this)!, "Sair do caixa",
                "A comanda aberta vai ser jogada fora — ela não volta depois.",
                "Descartar e sair", "Voltar", perigo: true))
            return;
        // Descartar é descartar: o rascunho não pode voltar oferecendo esta comanda
        // no próximo login do turno, quando o cliente já foi embora.
        _comanda.Clear();
        PintarComanda();
        Deslogou?.Invoke();
    }
}
