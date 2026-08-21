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
        Loaded += (_, _) => { IniciarRelogio(); PintarPendencias(); };
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
                    TxtToastKds.Text = "Catálogo atualizado pelo painel";
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
        TxtToastKds.Text = quantos == 1 ? "Pedido novo do iFood!" : $"{quantos} pedidos novos do iFood!";
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
        var amb = t is not null && Convert.ToInt64(t.ambiente) == 2 ? "  ·  ⚠ HOMOLOGAÇÃO" : "";
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
                "agente" => "  ·  emissor local",
                "nenhum" => "  ·  SEM EMISSOR",
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
            var r = await Sincronizacao.ExecutarAsync(Servicos.Nuvem(), Servicos.Guarda(), Servicos.Dreno(), andamento);

            if (!r.Ok)
            {
                Dialogo.Avisar(dono, "Não consegui sincronizar",
                    $"{r.Erro}\n\n(parou em: {etapa})", "erro");
            }
            else if (r.SemNovidade)
            {
                // Sem novidade o relatório detalhado só confunde: parecia estar
                // mostrando "a última sincronização" de novo.
                Dialogo.Avisar(dono, "Tudo em dia",
                    "Não há novas atualizações — o caixa já está igual ao painel.", "ok");
            }
            else
            {
                var linhas = new List<string>
                {
                    $"Catálogo:  {(r.CatalogoMudou ? $"atualizado ({r.ProdutosBaixados} produtos)" : "sem mudanças")}",
                    $"Fotos:     {r.FotosBaixadas} novas",
                    $"Notas:     {r.NotasSubidas} enviadas ao servidor",
                };
                if (r.NotasPendentes > 0)
                    linhas.Add($"\n⚠ {r.NotasPendentes} nota(s) ainda não subiram" +
                        (Servicos.TemContaDeNuvem() ? "." : " — este caixa ainda não foi pareado ao servidor."));
                if (r.VendasPendentes > 0)
                    linhas.Add($"⚠ {r.VendasPendentes} venda(s) na fila para o servidor.");

                Dialogo.Relatorio(dono, "Sincronizado", string.Join("\n", linhas), null);
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

    /// <summary>Pendência invisível vira pendência eterna: o número fica no botão.</summary>
    private void PintarPendencias()
    {
        var (notas, vendas) = Sincronizacao.Pendencias();
        var total = notas + vendas;
        ChipPendencia.Visibility = total == 0 ? Visibility.Collapsed : Visibility.Visible;
        TxtPendencia.Text = total.ToString();
        BtnSync.ToolTip = total == 0 ? "Tudo sincronizado"
            : $"{notas} nota(s) e {vendas} venda(s) esperando para subir";
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
    private const string CategoriaPromo = "promoção";

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
        foreach (var r in cx.Query("""
            SELECT id, plu, nome, categoria, preco_cent, unidade, ncm, cest, csosn, origem, foto_local
              FROM produto WHERE ativo = 1 ORDER BY categoria, nome
            """))
        {
            _catalogo.Add(new Produto((string)r.id, r.plu as string, (string)r.nome,
                (r.categoria as string) ?? "Outros", new Dinheiro((long)r.preco_cent),
                (r.unidade as string) ?? "UN", r.ncm as string, r.cest as string,
                r.csosn as string, (int)(long)r.origem, r.foto_local as string));
        }

        var cats = _catalogo.Select(p => p.Categoria).Distinct().OrderBy(c => c).ToList();
        foreach (var c in cats) _quantosPorCategoria[c] = _catalogo.Count(p => p.Categoria == c);

        // vitrine de PROMOÇÃO no topo: só existe quando alguma promoção vigente
        // menciona produto do catálogo — categoria vazia é pior que nenhuma
        var emPromo = _catalogo.Count(pp => _promoVitrine.ContainsKey(pp.Id));
        if (emPromo > 0)
        {
            cats.Insert(0, CategoriaPromo);
            _quantosPorCategoria[CategoriaPromo] = emPromo;
        }
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
        cab.SetResourceReference(Border.BackgroundProperty,
            info.AtivaAgora ? "ChipErroFundo" : "VeuElevado");
        cab.SetResourceReference(Border.BorderBrushProperty,
            info.AtivaAgora ? "ChipErroBorda" : "Borda");
        var linhaCab = new StackPanel { Orientation = Orientation.Horizontal };
        var titulo = new TextBlock
        {
            Text = "🏷️ " + Capitalizar(nomePromo), FontSize = 17, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titulo.SetResourceReference(TextBlock.ForegroundProperty,
            info.AtivaAgora ? "Rosa" : "TextoFraco");
        linhaCab.Children.Add(titulo);
        var detalhe = new TextBlock
        {
            Text = info.AtivaAgora ? $"  ·  {info.Quando}" : $"  ·  fora do dia/horário — vale {info.Quando}",
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
        el.ToolTip = $"{info.Nome} — válida {info.Quando}";
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
            Text = $"fora do horário · {info.Quando}",
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
            ToolTip = promoNome is null ? null : "PROMO: " + promoNome,
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
            ToolTip = promoNome2 is null ? null : "PROMO: " + promoNome2,
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
        TxtQtdItens.Text = $"{qtd:0.###} {(qtd == 1 ? "item" : "itens")}";
        ChipItens.Visibility = _comanda.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PainelVazio.Visibility = _comanda.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnFinalizar.IsEnabled = _comanda.Count > 0;
        BtnLimpar.IsEnabled = _comanda.Count > 0;
        BtnCortesia.IsEnabled = _comanda.Count > 0 && _cortesiaCodigo is null;
        BtnCortesia.Visibility = _cortesiaCodigo is null ? Visibility.Visible : Visibility.Collapsed;
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
        if (!Dialogo.Confirmar(Window.GetWindow(this)!, "Limpar comanda", $"Remover os {_comanda.Count} itens da comanda?", "Limpar", "Voltar", perigo: true)) return;
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
            "Código do cupom (ex.: AD-XXXXXX) — do QR ou digitado", "");
        if (string.IsNullOrWhiteSpace(codigo)) return;

        BtnCortesia.IsEnabled = false;
        Cortesia c;
        try { c = await _cortesias.ValidarAsync(codigo.Trim().ToUpperInvariant()); }
        finally { BtnCortesia.IsEnabled = _comanda.Count > 0 && _cortesiaCodigo is null; }

        if (!c.Ok)
        {
            Dialogo.Avisar(dono, "Cupom não vale", c.Erro switch
            {
                "not_found" => "Código não encontrado. Confira as letras e números.",
                "expired" => "Este cupom já venceu.",
                "already_redeemed" => "Este cupom já foi usado.",
                "cancelled" => "Este cupom foi cancelado.",
                "code_required" => "Digite o código do cupom.",
                "sem_rede" => "Sem internet para validar o cupom agora.",
                _ => "Não foi possível validar o cupom.",
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
            Dialogo.Avisar(dono, "Itens do cupom não estão na comanda",
                "Adicione à comanda os produtos do cupom antes de aplicar a cortesia:\n" +
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
            TxtCortesiaItens.Text += $"  ·  (não estavam na comanda: {string.Join(", ", faltando)})";
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
            if (!Dialogo.Confirmar(dono, "Entregar cortesia",
                    "A comanda toda é cortesia — nada a cobrar. Entregar os itens e queimar o cupom?",
                    "Entregar", "Voltar")) return;
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
                RemoverCortesia(this, new RoutedEventArgs());
                _comanda.Clear();
                PintarComanda();
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
            Dialogo.Avisar(dono, "Não deu para resgatar", r.Erro switch
            {
                "already_redeemed" => "O cupom já tinha sido usado.",
                "loja_errada" => "O cupom é de outra loja.",
                "sem_rede" => "Sem internet para resgatar agora.",
                _ => "Tente de novo.",
            }, "erro");
            return;
        }
        Caixa.Auditar(Banco.Abrir(), null, "cortesia_entregue", _operador.Id, null,
            $"cupom={_cortesiaCodigo} (comanda inteira em cortesia)");
        RemoverCortesia(this, new RoutedEventArgs());
        _comanda.Clear();
        PintarComanda();
        Dialogo.Avisar(dono, "Cortesia entregue", "Cupom queimado. Bom atendimento!", "ok");
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
            Dialogo.Avisar(dono, "Impressoras", "Não consegui listar as impressoras: " + ex.Message, "erro");
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
            Content = "Imprimir automaticamente ao concluir a venda",
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
            $"Cupons passam a sair em: {escolhida ?? "impressora padrão do Windows"}.", "ok");
    }
    private void Suprimento(object sender, RoutedEventArgs e) => Movimento("suprimento");

    private void Movimento(string tipo)
    {
        var dono = Window.GetWindow(this)!;
        var titulo = tipo == "sangria" ? "Sangria" : "Suprimento";
        var valor = PedirValor.Mostrar(dono, titulo, "Valor");
        if (valor is null || !valor.Value.Positivo) return;

        var motivo = PedirTexto.Mostrar(dono, titulo, "Motivo (obrigatório)",
            tipo == "sangria" ? "envio ao cofre" : "reforço de troco");
        if (string.IsNullOrWhiteSpace(motivo)) return;

        string? autorizador = null;
        if (tipo == "sangria")
        {
            // Supervisor autoriza com o PIN DELE — e o nome fica no registro.
            // Autorização sem nome não serve de nada numa auditoria.
            var pin = PedirSenha.Mostrar(dono, "Autorização", "PIN do supervisor");
            if (pin is null) return;
            using var cxa = Banco.Abrir();
            var sup = Operadores.AutorizarSupervisor(cxa, pin);
            if (sup is null)
            {
                Dialogo.Avisar(dono, "Não autorizado", "O PIN não confere ou não é de um supervisor.", "erro");
                return;
            }
            // Segundo par de olhos NÃO pode ser o próprio: um gerente que opera o
            // caixa não autoriza a própria sangria — isso é sangria fantasma com
            // aval de si mesmo, o furto que a autorização existe pra impedir.
            if (sup.Id == _operador.Id)
            {
                Dialogo.Avisar(dono, "Não autorizado",
                    "A sangria precisa ser autorizada por OUTRO supervisor — você não pode autorizar a sua própria.", "erro");
                return;
            }
            autorizador = sup.Id;
        }

        try
        {
            using var cx = Banco.Abrir();
            Caixa.Movimentar(cx, _sessao, tipo, valor.Value, motivo!, _operador, autorizador,
                tipo == "sangria" ? "cofre" : null);
            Dialogo.Avisar(dono, $"{titulo} registrada",
                $"{valor.Value.Formatado()} — {motivo}", "ok");
        }
        catch (Exception ex)
        {
            Dialogo.Avisar(dono, "Não foi possível registrar", ex.Message, "erro");
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
        if (_comanda.Count > 0)
        {
            Dialogo.Avisar(dono, "Venda em andamento",
                "Finalize ou limpe a comanda antes de fechar o caixa.", "erro");
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
                ? "Quanto você contou em Dinheiro? (conte a gaveta inteira, incluindo o fundo de troco)"
                : $"Quanto deu {Rotulo(f)} no fechamento da maquininha?";
            var v = PedirValor.Mostrar(dono, "Fechamento de caixa", pergunta);
            if (v is null) return;                 // desistiu no meio: não fecha nada
            contagem[f] = v.Value;
        }

        var tolerancia = new Dinheiro(200);        // R$ 2,00
        try
        {
            using var cx = Banco.Abrir();
            var divergencias = Caixa.DivergenciasTef(cx, _sessao);
            MostrarResultado(dono, Caixa.Fechar(cx, _sessao, contagem, _operador, tolerancia), null, divergencias);
            FechouCaixa?.Invoke();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Justifique"))
        {
            var just = PedirTexto.Mostrar(dono, "Diferença no caixa", ex.Message + "\n\nO que aconteceu?", "");
            if (string.IsNullOrWhiteSpace(just)) return;
            try
            {
                using var cx = Banco.Abrir();
                var divergencias = Caixa.DivergenciasTef(cx, _sessao);
                MostrarResultado(dono, Caixa.Fechar(cx, _sessao, contagem, _operador, tolerancia, just), just, divergencias);
                FechouCaixa?.Invoke();
            }
            catch (Exception e2)
            {
                Dialogo.Avisar(dono, "Não foi possível fechar", e2.Message, "erro");
            }
        }
        catch (Exception ex)
        {
            Dialogo.Avisar(dono, "Não foi possível fechar", ex.Message, "erro");
        }
    }

    private static void MostrarResultado(Window dono, List<LinhaFechamento> linhas, string? justificativa,
        List<DivergenciaTef> divergencias)
    {
        var texto = string.Join("\n", linhas.Select(l =>
        {
            var dif = l.Situacao switch
            {
                "confere" => "confere",
                "sobra" => "SOBRA " + l.Diferenca.Abs.Formatado(),
                _ => "FALTA " + l.Diferenca.Abs.Formatado(),
            };
            var origem = l.Contada ? "contou" : "  TEF ";
            return $"{Rotulo(l.Forma),-9} {origem} {l.Declarado.Formatado(),11}  sistema {l.Apurado.Formatado(),11}  {dif}";
        }));

        // O desvio é a soma dos módulos. O líquido esconderia falta num lugar
        // compensada por sobra em outro, que é justamente o que se quer enxergar.
        var desvio = new Dinheiro(linhas.Sum(l => l.Diferenca.Abs.Centavos));
        var corpo = texto + $"\n\nDesvio total: {desvio.Formatado()}";

        if (linhas.Any(l => l.Situacao == "sobra"))
            corpo += "\n\nSobrou dinheiro na gaveta. Isso costuma ser venda que não passou\n" +
                     "pelo PDV — e venda assim também não baixou estoque nem gerou nota.";

        if (divergencias.Count > 0)
            corpo += "\n\nTEF x PDV:\n" + string.Join("\n", divergencias.Select(d =>
                $"{Rotulo(d.Forma),-9} maquininha {d.NoTef.Formatado(),11}  no PDV {d.NaVenda.Formatado(),11}" +
                $"  diferença {d.Diferenca.Abs.Formatado()}"))
                + "\n\nA maquininha aprovou uma cobrança que não virou venda aqui — o PDV\n" +
                  "perdeu o desfecho (queda de energia, app fechado ou tempo esgotado).\n" +
                  "Confira no extrato da adquirente antes de estornar.";

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

    private void Sair(object sender, RoutedEventArgs e)
    {
        if (_comanda.Count > 0 && !Dialogo.Confirmar(Window.GetWindow(this)!, "Sair do caixa",
                "Há uma venda em andamento e ela será descartada.", "Sair mesmo assim", "Voltar", perigo: true))
            return;
        Deslogou?.Invoke();
    }
}
