using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// FOTOGRAFA A COMANDA ABERTA (13/10/2026) nos tres tamanhos de tela da rede, sem monitor daquele
/// tamanho. Irma do FotoVenda (a mesma tela de venda de verdade, o mesmo tema, a copia do banco do
/// caixa com a nuvem morta) e do FotoResgate (uma chamada gera os tres tamanhos e acusa o que nao
/// coube como saida 1). Regra da casa: layout novo nao sai no instalador sem as fotos de 1024x768,
/// 1366x768 e 1920x1080 conferidas.
///
/// Uso: Pdv.Testes.exe --foto-comanda docs/comanda-fotos [claro|escuro]
///
/// Sai, em cada largura (1024, 1366 e 1920; com "escuro", so 1024, com o sufixo -escuro):
///   venda-balcao-L.png        o balcao de sempre com 3 itens e o botao Comandas no cabecalho (nada regrediu)
///   venda-comanda-L.png       a comanda "12 · Mesa 5" carregada: 3 linhas, uma com "2 na cozinha", faixa
///                             ciano, Guardar, "Cozinha 2", "Fechar conta" e o toast "Pedido na fila da cozinha."
///   comandas-lista-L.png      o dialogo Comandas com 7 linhas e todos os selos (na tela, na TV, fila, nao foi
///                             com Mandar de novo, confira a maquininha, 0 itens, em uso no outro caixa)
///   comandas-nova-L.png       a face Nova comanda (zero abertas) com os chips, o campo, o consumo e
///                             "O carrinho tem 3 itens." com os dois Abrir
///   comanda-pagamento-L.png   o Pagamento da comanda: "Comanda 12 · Mesa 5 · ..." e o bloco da cozinha
///                             pre-marcado (ha 1 pendente)
///   comanda-pin-L.png         a janela "Item já pedido" (PIN de outro gerente) sobre a comanda
///
/// OS CINCO CRITERIOS DA FOTO, medidos nos elementos (saida 1 quando algum falha):
///   (1) a 1024 o numero da comanda esta inteiro no titulo, com o botao Comandas ao lado e sem o chip;
///   (2) a lista da comanda a 1024 mostra tantas linhas inteiras quanto a venda de balcao (3);
///   (3) "Fechar conta" cabe inteiro dentro do botao;
///   (4) nenhum toast cobre a coluna da direita (o ToastAviso termina antes da ColunaComanda);
///   (5) nenhum dialogo precisa rolar a 1024 e nenhum passa da tela.
/// O sexto (sem travessao nos textos) e da suite (TestesComandaTela).
///
/// Seguranca: roda contra uma COPIA do banco do caixa, com a nuvem apontada para um endereco morto.
/// Tudo que se semeia (contas, lotes, linhas da outbox) vai na copia, que e apagada no fim.
/// </summary>
public static class FotoComanda
{
    private const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly (int W, int H)[] Tamanhos = { (1024, 768), (1366, 768), (1920, 1080) };

    public static int Rodar(string[] args)
    {
        var dir = Path.GetFullPath(args[1]);
        var tema = args.Skip(2).FirstOrDefault(a => a is "claro" or "escuro") ?? "claro";
        Directory.CreateDirectory(dir);

        var origem = Banco.Arquivo;
        if (!File.Exists(origem))
        {
            Console.Error.WriteLine($"foto-comanda: não achei o banco do caixa em {origem}");
            return 2;
        }
        var fixture = Path.Combine(Path.GetTempPath(), "foto-comanda-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        File.Copy(origem, fixture);
        Banco.CaminhoForcado = fixture;
        Banco.Migrar(fixture);   // a copia pode vir de um exe anterior (sem conta_aberta)
        string opId, opNome;
        using (var cx = Banco.Abrir())
        {
            cx.Execute("UPDATE terminal SET api_base = 'http://127.0.0.1:9'");
            Isolamento.SemNuvem(cx);
            Vendas.GravarConfig(cx, "homologacao", "0");
            Vendas.GravarConfig(cx, PedidoCozinha.ChaveToken, "chave-da-foto-0123456789abcdef");
            Vendas.GravarConfig(cx, PedidoCozinha.ChaveUrl, Isolamento.UrlMorta);
            var op = cx.QueryFirstOrDefault<(string? id, string? nome)>("SELECT id, nome FROM operador WHERE ativo = 1 ORDER BY nome LIMIT 1");
            opId = op.id ?? "op-foto";
            opNome = op.nome ?? "Teste";
        }
        var operador = new Operador(opId, opNome, "operador");
        var sessao = new Sessao("sessao-foto", Caixa.DiaOperacional(), opId, opNome, DateTime.Now.Date.AddHours(8), Dinheiro.Zero);

        var codigo = 1;
        var t = new Thread(() =>
        {
            try { codigo = Fotografar(dir, tema, operador, sessao); }
            catch (Exception ex) { Console.Error.WriteLine("foto-comanda: " + ex); codigo = 1; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { File.Delete(fixture); } catch { /* temporario */ }
        return codigo;
    }

    private static int Fotografar(string dir, string tema, Operador operador, Sessao sessao)
    {
        static ResourceDictionary Dic(string caminho) => new()
        {
            Source = new Uri($"pack://application:,,,/Pdv;component/{caminho}", UriKind.Absolute),
        };
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(Dic(tema == "claro" ? "Temas/Claro.xaml" : "Temas/Escuro.xaml"));
        app.Resources.MergedDictionaries.Add(Dic("Estilos.xaml"));

        var erros = new List<string>();
        app.DispatcherUnhandledException += (_, e) =>
        {
            erros.Add(e.Exception.GetType().Name + ": " + e.Exception.Message);
            e.Handled = true;
        };

        var codigo = 1;
        app.Startup += (_, _) => _ = app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var tamanhos = tema == "claro" ? Tamanhos : Tamanhos.Take(1).ToArray();
                foreach (var (w, h) in tamanhos)
                    await UmTamanhoAsync(dir, tema, w, h, operador, sessao, erros);
                foreach (var e in erros.Distinct()) Console.WriteLine("  aviso: " + e);
                codigo = erros.Count == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("foto-comanda: " + ex);
                codigo = 1;
            }
            app.Shutdown();
        });
        var limite = new DispatcherTimer { Interval = TimeSpan.FromSeconds(180) };
        limite.Tick += (_, _) => { Console.Error.WriteLine("foto-comanda: tempo esgotado"); codigo = 3; app.Shutdown(); };
        limite.Start();
        app.Run();
        return codigo;
    }

    /// <summary>Os seis momentos num tamanho de tela, cada um numa tela de venda nova.</summary>
    private static async Task UmTamanhoAsync(string dir, string tema, int w, int h, Operador operador, Sessao sessao, List<string> erros)
    {
        var pre = $"{w}x{h}";
        var catalogo = Catalogo();
        if (catalogo.Count < 3) { erros.Add("o catalogo da copia tem menos de 3 produtos"); return; }

        // ── venda-balcao: 3 itens no carrinho solto, o botao Comandas no cabecalho ──
        var linhasBalcao = 0;
        await ComTela(w, h, operador, sessao, async (tela, janela) =>
        {
            Limpar();
            foreach (var p in catalogo.Take(3)) Invocar(tela, "Adicionar", ProdutoDaTela(tela, p.Id));
            await Assentar(janela);
            linhasBalcao = LinhasInteiras(tela);
            var titulo = Campo<TextBlock>(tela, "TxtTituloComanda");
            Console.WriteLine($"  {pre} balcao: titulo '{titulo.Text}' {titulo.ActualWidth:0} px, chip {Campo<Border>(tela, "ChipItens").Visibility}, "
                              + $"botao Comandas {Campo<Button>(tela, "BtnComandas").ActualWidth:0} px, {linhasBalcao} linhas inteiras na lista");
            if (Campo<Border>(tela, "ChipItens").Visibility != Visibility.Visible) erros.Add($"{pre} balcao: o chip 'N itens' sumiu");
            if (Campo<TextBlock>(tela, "TxtFinalizarRotulo").Text != "Finalizar") erros.Add($"{pre} balcao: o botao nao diz Finalizar");
            Foto(dir, "venda-balcao", w, h, tema, janela, null);
        });

        // ── venda-comanda: "12 · Mesa 5" com 3 itens, 2 na cozinha, toast forcado ──
        await ComTela(w, h, operador, sessao, async (tela, janela) =>
        {
            Limpar();
            var id = SemearMesa5(sessao, operador, catalogo, comPendente: true);
            Invocar(tela, "CarregarConta", id);
            Invocar(tela, "AvisoLeve", ContasAbertas.Textos.PedidoNaFila, "neutro");
            await Assentar(janela);
            var titulo = Campo<TextBlock>(tela, "TxtTituloComanda");
            var natural = Largura(titulo);
            var rotulo = Campo<TextBlock>(tela, "TxtFinalizarRotulo");
            var botao = Campo<Button>(tela, "BtnFinalizar");
            var toast = Campo<Border>(tela, "ToastAviso");
            var coluna = Campo<Border>(tela, "ColunaComanda");
            var raiz = Campo<Grid>(tela, "Raiz");
            var linhas = LinhasInteiras(tela);
            var toastDireita = toast.TranslatePoint(new Point(toast.ActualWidth, 0), raiz).X;
            var colunaEsquerda = coluna.TranslatePoint(new Point(0, 0), raiz).X;
            Console.WriteLine($"  {pre} comanda: titulo '{titulo.Text}' {titulo.ActualWidth:0} de {natural:0} px, chip {Campo<Border>(tela, "ChipItens").Visibility}, "
                              + $"'{rotulo.Text}' {Largura(rotulo):0} px no botao de {botao.ActualWidth:0}, cozinha '{Campo<TextBlock>(tela, "TxtCozinhaRotulo").Text}', "
                              + $"{linhas} linhas inteiras (balcao {linhasBalcao}), toast ate x={toastDireita:0} e a coluna comeca em x={colunaEsquerda:0}");
            // (1) o numero inteiro, com o botao ao lado e sem o chip
            if (titulo.ActualWidth + 1 < natural) erros.Add($"{pre} comanda: o titulo '{titulo.Text}' nao coube ({titulo.ActualWidth:0} de {natural:0} px)");
            if (!titulo.Text.StartsWith("12 · Mesa 5", StringComparison.Ordinal)) erros.Add($"{pre} comanda: o titulo deveria ser '12 · Mesa 5' (veio '{titulo.Text}')");
            if (Campo<Border>(tela, "ChipItens").Visibility == Visibility.Visible) erros.Add($"{pre} comanda: o chip 'N itens' deveria estar oculto");
            if (Campo<Button>(tela, "BtnComandas").ActualWidth < 40) erros.Add($"{pre} comanda: o botao Comandas sumiu");
            // (2) a lista nao perde linha em relacao ao balcao
            if (linhas < linhasBalcao) erros.Add($"{pre} comanda: a lista mostra {linhas} linhas inteiras, o balcao mostra {linhasBalcao}");
            // (3) "Fechar conta" inteiro dentro do botao
            if (rotulo.Text != ContasAbertas.Textos.FecharConta) erros.Add($"{pre} comanda: o botao deveria dizer '{ContasAbertas.Textos.FecharConta}'");
            if (rotulo.ActualWidth + 1 < Largura(rotulo) || Largura(rotulo) > botao.ActualWidth - 12) erros.Add($"{pre} comanda: '{rotulo.Text}' nao cabe no botao ({Largura(rotulo):0} em {botao.ActualWidth:0})");
            if (Campo<TextBlock>(tela, "TxtSetaFinalizar").Visibility == Visibility.Visible && w < 1500) erros.Add($"{pre} comanda: a seta do Fechar conta deveria sumir na tela estreita");
            // (4) nenhum toast sobre a coluna da direita
            if (toast.Visibility != Visibility.Visible) erros.Add($"{pre} comanda: o toast nao apareceu");
            else if (toastDireita > colunaEsquerda) erros.Add($"{pre} comanda: o toast cobre a coluna da direita (termina em {toastDireita:0}, coluna em {colunaEsquerda:0})");
            var cozinha = Campo<TextBlock>(tela, "TxtCozinhaRotulo").Text;
            if (cozinha != ContasAbertas.Textos.CozinhaN(2)) erros.Add($"{pre} comanda: o botao da cozinha deveria dizer 'Cozinha 2' (veio '{cozinha}')");
            foreach (var b in new[] { Campo<Button>(tela, "BtnGuardar"), Campo<Button>(tela, "BtnCozinha"), Campo<Button>(tela, "BtnCortesia") })
                if (b.Visibility == Visibility.Visible && b.TranslatePoint(new Point(b.ActualWidth, 0), raiz).X > w + 0.5) erros.Add($"{pre} comanda: um botao da linha do cupom saiu da tela");
            Foto(dir, "venda-comanda", w, h, tema, janela, null);
        });

        // ── comandas-lista: 7 contas com todos os selos, a 12 na tela ──
        await ComTela(w, h, operador, sessao, async (tela, janela) =>
        {
            Limpar();
            var id = SemearMesa5(sessao, operador, catalogo, comPendente: false);
            using var outro = new ProcessoVivo();
            SemearOutras(sessao, operador, catalogo, outro.Dono);
            Invocar(tela, "CarregarConta", id);
            await Assentar(janela);
            await ComDialogo(janela, () => Invocar(tela, "AbrirDialogoComandas"), d =>
            {
                var rolagem = Descendentes<ScrollViewer>(d).FirstOrDefault(s => System.Windows.Automation.AutomationProperties.GetName(s) == "ListaComandas");
                var linhas = Descendentes<Button>(d).Count(b => (System.Windows.Automation.AutomationProperties.GetName(b) ?? "").StartsWith("Comanda "));
                var selos = Descendentes<TextBlock>(d).Where(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Selo").Select(t => t.Text).ToList();
                Console.WriteLine($"  {pre} lista: dialogo {d.ActualWidth:0}x{d.ActualHeight:0}, {linhas} linhas, rola {(rolagem is null ? "?" : rolagem.ScrollableHeight.ToString("0"))} px, selos: {string.Join(" / ", selos)}");
                if (linhas != 7) erros.Add($"{pre} lista: esperava 7 linhas, veio {linhas}");
                if (rolagem is { ScrollableHeight: > 0 }) erros.Add($"{pre} lista: a lista de 7 precisou rolar {rolagem.ScrollableHeight:0} px");
                if (d.ActualHeight > h) erros.Add($"{pre} lista: o dialogo passa da tela ({d.ActualHeight:0} > {h})");
                foreach (var selo in new[] { ContasAbertas.Textos.NaTela, ContasAbertas.Textos.NaTv, ContasAbertas.Textos.SeloFila, ContasAbertas.Textos.SeloNaoFoi, ContasAbertas.Textos.ConfiraMaquininha, ContasAbertas.Textos.EmUsoNoOutroCaixa })
                    if (!selos.Contains(selo)) erros.Add($"{pre} lista: faltou o selo '{selo}'");
                if (!Descendentes<Button>(d).Any(b => b.Content as string == ContasAbertas.Textos.MandarDeNovo)) erros.Add($"{pre} lista: faltou o [Mandar de novo]");
                Foto(dir, "comandas-lista", w, h, tema, janela, d);
            });
        });

        // ── comandas-nova: zero abertas, carrinho solto com 3 itens ──
        await ComTela(w, h, operador, sessao, async (tela, janela) =>
        {
            Limpar();
            foreach (var p in catalogo.Take(3)) Invocar(tela, "Adicionar", ProdutoDaTela(tela, p.Id));
            await Assentar(janela);
            await ComDialogo(janela, () => Invocar(tela, "AbrirDialogoComandas"), d =>
            {
                var chips = Descendentes<Button>(d).Count(b => (b.Content as string ?? "").StartsWith("Mesa "));
                var carrinho = Descendentes<TextBlock>(d).FirstOrDefault(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Carrinho")?.Text;
                Console.WriteLine($"  {pre} nova: dialogo {d.ActualWidth:0}x{d.ActualHeight:0}, {chips} chips de mesa, '{carrinho}'");
                if (chips != ContasAbertas.MesasPadrao) erros.Add($"{pre} nova: esperava {ContasAbertas.MesasPadrao} chips de mesa, veio {chips}");
                if (carrinho != ContasAbertas.Textos.CarrinhoTem(3)) erros.Add($"{pre} nova: faltou 'O carrinho tem 3 itens.' (veio '{carrinho}')");
                if (!Descendentes<Button>(d).Any(b => b.Content as string == ContasAbertas.Textos.AbrirComItens(3))) erros.Add($"{pre} nova: faltou o [Abrir com os 3 itens]");
                if (d.ActualHeight > h) erros.Add($"{pre} nova: o dialogo passa da tela ({d.ActualHeight:0} > {h})");
                Foto(dir, "comandas-nova", w, h, tema, janela, d);
            });
        });

        // ── comanda-pagamento: Fechar conta com 1 pendente ──
        await ComTela(w, h, operador, sessao, async (tela, janela) =>
        {
            Limpar();
            var id = SemearMesa5(sessao, operador, catalogo, comPendente: true);
            Invocar(tela, "CarregarConta", id);
            await Assentar(janela);
            Invocar(tela, "Finalizar", null, new RoutedEventArgs());
            await Assentar(janela);
            var painel = Campo<ContentControl>(tela, "PainelPagamento");
            if (painel.Content is not Pdv.Telas.Pagamento pg) { erros.Add($"{pre} pagamento: o Pagamento nao abriu"); return; }
            var resumo = Campo<TextBlock>(pg, "TxtResumo");
            var bloco = Campo<Border>(pg, "BlocoCozinha");
            Console.WriteLine($"  {pre} pagamento: '{resumo.Text}' ({resumo.ActualWidth:0} de {Largura(resumo):0} px), bloco da cozinha {bloco.Visibility}");
            if (!resumo.Text.StartsWith("Comanda 12 · Mesa 5 · ", StringComparison.Ordinal)) erros.Add($"{pre} pagamento: o resumo deveria comecar com 'Comanda 12 · Mesa 5 ·' (veio '{resumo.Text}')");
            if (resumo.ActualWidth + 1 < Largura(resumo)) erros.Add($"{pre} pagamento: o resumo foi cortado");
            if (bloco.Visibility != Visibility.Visible) erros.Add($"{pre} pagamento: o bloco 'Para a cozinha' deveria aparecer (ha 1 pendente)");
            if (Campo<Button>(pg, "BtnCozinhaLoja").Style != (Style)Application.Current.Resources["BotaoPrincipal"]) erros.Add($"{pre} pagamento: 'Consumo na loja' deveria vir pre-marcado");
            Foto(dir, "comanda-pagamento", w, h, tema, janela, null);
        });

        // ── comanda-pin: o menos cruzando o pedido abre "Item já pedido" ──
        await ComTela(w, h, operador, sessao, async (tela, janela) =>
        {
            Limpar();
            var id = SemearMesa5(sessao, operador, catalogo, comPendente: false);
            Invocar(tela, "CarregarConta", id);
            await Assentar(janela);
            var lista = Campo<ItemsControl>(tela, "ListaComanda");
            var pedida = Campo<List<Pdv.Telas.ItemComanda>>(tela, "_comanda").First(i => i.CozinhaMilesimos == i.Qtd.Milesimos);
            var menos = Descendentes<Button>(lista).First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Diminuir " + pedida.Produto.Nome);
            await ComDialogo(janela, () => menos.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)), d =>
            {
                var titulo = Descendentes<TextBlock>(d).Select(t => t.Text).FirstOrDefault(t => t == ContasAbertas.Textos.ItemJaPedido);
                Console.WriteLine($"  {pre} pin: dialogo {d.ActualWidth:0}x{d.ActualHeight:0}, titulo '{titulo}'");
                if (titulo is null) erros.Add($"{pre} pin: a janela nao diz '{ContasAbertas.Textos.ItemJaPedido}'");
                if (d.ActualHeight > h) erros.Add($"{pre} pin: a janela passa da tela ({d.ActualHeight:0} > {h})");
                Foto(dir, "comanda-pin", w, h, tema, janela, d);
            });
        });
    }

    // ── a tela e os dialogos ────────────────────────────────────────────────

    /// <summary>Sobe uma tela de venda nova numa janela do tamanho pedido, roda os passos e fecha.</summary>
    private static async Task ComTela(int w, int h, Operador operador, Sessao sessao, Func<Pdv.Telas.Venda, Window, Task> passos)
    {
        var tela = new Pdv.Telas.Venda(operador, sessao);
        var janela = new Window
        {
            Title = "foto-comanda", Width = w, Height = h,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0,
            ShowInTaskbar = false, ShowActivated = false, Content = tela,
        };
        janela.Show();
        await Task.Delay(900);
        janela.UpdateLayout();
        Definir(tela, "_rascunhoOferecido", true);
        await passos(tela, janela);
        janela.Content = null;
        janela.Close();
        await Task.Delay(200);
    }

    private static async Task Assentar(Window janela)
    {
        await Task.Delay(350);
        janela.UpdateLayout();
    }

    /// <summary>
    /// Abre um dialogo modal (ShowDialog prende este laco) e age nele por um timer armado ANTES, que
    /// dispara dentro do laco aninhado; depois fecha o dialogo e a chamada volta.
    /// </summary>
    private static async Task ComDialogo(Window janela, Action abrir, Action<Window> agir)
    {
        var feito = new TaskCompletionSource<bool>();
        var foto = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        foto.Tick += (_, _) =>
        {
            foto.Stop();
            var d = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x != janela && x.Owner == janela && x.IsVisible);
            try
            {
                if (d is null) Console.WriteLine("  aviso: o dialogo nao abriu");
                else { d.UpdateLayout(); agir(d); }
            }
            finally { d?.Close(); feito.TrySetResult(true); }
        };
        foto.Start();
        abrir();
        await feito.Task;
        await Task.Delay(200);
    }

    // ── o que se semeia na copia ────────────────────────────────────────────

    private sealed record ProdutoFoto(string Id, string? Plu, string Nome, string Categoria, long PrecoCent, string Unidade,
        string? Ncm, string? Cest, string? Csosn, int Origem, string? Foto);

    /// <summary>Tres produtos do catalogo da copia (donut, cookie e agua quando existem; senao os primeiros).</summary>
    private static List<ProdutoFoto> Catalogo()
    {
        using var cx = Banco.Abrir();
        var todos = cx.Query("SELECT id, plu, nome, categoria, preco_cent, unidade, ncm, cest, csosn, origem, foto_local FROM produto WHERE ativo = 1 AND preco_cent > 0 ORDER BY nome")
            .Select(r => new ProdutoFoto((string)r.id, r.plu as string, (string)r.nome, (r.categoria as string) ?? "Outros", (long)r.preco_cent,
                (r.unidade as string) ?? "UN", r.ncm as string, r.cest as string, r.csosn as string, (int)(long)r.origem, r.foto_local as string))
            .ToList();
        var escolhidos = new List<ProdutoFoto>();
        foreach (var fragmento in new[] { "DONUT", "COOKIE", "AGUA" })
        {
            var p = todos.FirstOrDefault(x => x.Nome.Contains(fragmento, StringComparison.OrdinalIgnoreCase) && !x.Nome.Contains("COMBO", StringComparison.OrdinalIgnoreCase)
                                              && !escolhidos.Contains(x));
            if (p is not null) escolhidos.Add(p);
        }
        foreach (var p in todos) { if (escolhidos.Count >= 3) break; if (!escolhidos.Contains(p) && !p.Nome.Contains("COMBO", StringComparison.OrdinalIgnoreCase)) escolhidos.Add(p); }
        return escolhidos;
    }

    private static ItemRascunho Item(ProdutoFoto p, long qtd)
        => new(p.Id, p.Plu, p.Nome, p.Categoria, p.PrecoCent, qtd, p.Unidade, p.Ncm, p.Cest, p.Csosn, p.Origem, p.Foto);

    /// <summary>Apaga tudo que a foto semeou (so na copia): contas, lotes e as linhas da cozinha na fila.</summary>
    private static void Limpar()
    {
        using var cx = Banco.Abrir();
        cx.Execute("DELETE FROM outbox WHERE tipo IN (@c, @e)", new { c = PedidoCozinha.TipoNaFila, e = ContasAbertas.TipoEvento });
        cx.Execute("DELETE FROM conta_lote");
        cx.Execute("DELETE FROM conta_aberta");
        cx.Execute("DELETE FROM comanda_rascunho");
    }

    /// <summary>
    /// A comanda "12 · Mesa 5": 3 linhas, a primeira com 3 unidades e 2 na cozinha (lote 1 ja na TV),
    /// a segunda pedida inteira, a terceira nunca pedida. Com <paramref name="comPendente"/> falso a
    /// terceira tambem vai (tudo pedido). O 12 vem de uma conta 11 cancelada hoje.
    /// </summary>
    private static string SemearMesa5(Sessao sessao, Operador operador, List<ProdutoFoto> catalogo, bool comPendente)
    {
        using var cx = Banco.Abrir();
        var agora = DateTime.Now.ToString("o");
        cx.Execute("""
            INSERT INTO conta_aberta (id, numero, nome, consumo, status, itens_json, operador_id, sessao_abertura, business_date,
                                      aberta_em, atualizado_em, ultimo_toque_em, cancelada_em, cancel_motivo, cancel_automatico)
            VALUES ('foto-11', 11, 'Mesa 1', 'loja', 'cancelada', '[]', @op, @ses, @bd, @a, @a, @a, @a, 'foto', 1)
            """, new { op = operador.Id, ses = sessao.Id, bd = sessao.BusinessDate, a = agora });
        var itens = new List<ItemRascunho> { Item(catalogo[0], 2000), Item(catalogo[1], 1000) };
        if (!comPendente) itens.Add(Item(catalogo[2], 1000));
        var conta = ContasAbertas.Abrir(cx, sessao, operador, "Mesa 5", DestinoCozinha.Loja, itens);
        var lote = ContasAbertas.PedirCozinha(cx, conta.Id, null, operador, false)!;
        cx.Execute("UPDATE outbox SET enviado_em = @e WHERE tipo = @t AND ref_id = @r", new { e = agora, t = PedidoCozinha.TipoNaFila, r = lote.Id });
        var depois = ContasAbertas.Ler(cx, conta.Id)!.Itens.ToList();
        depois[0] = depois[0] with { QtdMilesimos = 3000 };   // 3 x, 2 na cozinha
        if (comPendente) depois.Add(Item(catalogo[2], 1000));
        ContasAbertas.Gravar(cx, conta.Id, depois, null, null, operador);
        ContasAbertas.Soltar(cx, conta.Id);
        return conta.Id;
    }

    /// <summary>As outras linhas da lista: 13 Joana (na TV), 14 Mesa 2 (fila), 15 Balcão (nao foi), 16 Mesa 7 (maquininha), 17 (0 itens), 18 Mesa 1 (em uso).</summary>
    private static void SemearOutras(Sessao sessao, Operador operador, List<ProdutoFoto> catalogo, string donoVivo)
    {
        using var cx = Banco.Abrir();
        var agora = DateTime.Now.ToString("o");
        var joana = ContasAbertas.Abrir(cx, sessao, operador, "Joana", DestinoCozinha.Viagem, new List<ItemRascunho> { Item(catalogo[1], 2000) });
        var l1 = ContasAbertas.PedirCozinha(cx, joana.Id, null, operador, false)!;
        cx.Execute("UPDATE outbox SET enviado_em = @e WHERE tipo = @t AND ref_id = @r", new { e = agora, t = PedidoCozinha.TipoNaFila, r = l1.Id });
        var mesa2 = ContasAbertas.Abrir(cx, sessao, operador, "Mesa 2", DestinoCozinha.Loja, new List<ItemRascunho> { Item(catalogo[0], 1000), Item(catalogo[2], 2000) });
        ContasAbertas.PedirCozinha(cx, mesa2.Id, null, operador, false);
        var balcao = ContasAbertas.Abrir(cx, sessao, operador, ContasAbertas.Textos.Balcao, DestinoCozinha.Loja, new List<ItemRascunho> { Item(catalogo[2], 1000) });
        var l3 = ContasAbertas.PedirCozinha(cx, balcao.Id, null, operador, false)!;
        cx.Execute("UPDATE outbox SET desistido_em = @e, ultimo_erro = 'descartado sem enviar: pedido da cozinha com mais de 3 h' WHERE tipo = @t AND ref_id = @r",
            new { e = agora, t = PedidoCozinha.TipoNaFila, r = l3.Id });
        var mesa7 = ContasAbertas.Abrir(cx, sessao, operador, "Mesa 7", DestinoCozinha.Loja, new List<ItemRascunho> { Item(catalogo[0], 1000) });
        ContasAbertas.MarcarFechando(cx, mesa7.Id);
        ContasAbertas.Abrir(cx, sessao, operador, null);
        var mesa1 = ContasAbertas.Abrir(cx, sessao, operador, "Mesa 1", DestinoCozinha.Loja, new List<ItemRascunho> { Item(catalogo[1], 1000) });
        cx.Execute("UPDATE conta_aberta SET dono = @d WHERE id = @id", new { d = donoVivo, id = mesa1.Id });
        foreach (var id in new[] { joana.Id, mesa2.Id, balcao.Id, mesa7.Id }) ContasAbertas.Soltar(cx, id);
    }

    // ── medidas ─────────────────────────────────────────────────────────────

    /// <summary>Quantas linhas da comanda estao INTEIRAS dentro da area visivel da lista.</summary>
    private static int LinhasInteiras(Pdv.Telas.Venda tela)
    {
        var lista = Campo<ItemsControl>(tela, "ListaComanda");
        var rolagem = Campo<ScrollViewer>(tela, "ScrollComanda");
        var n = 0;
        foreach (var item in lista.Items.OfType<FrameworkElement>())
        {
            var topo = item.TranslatePoint(new Point(0, 0), rolagem).Y;
            if (topo >= -0.5 && topo + item.ActualHeight <= rolagem.ViewportHeight + 0.5) n++;
        }
        return n;
    }

    /// <summary>A largura natural do texto de um TextBlock (o que ele mediria sem limite), para saber se cortou.</summary>
    private static double Largura(TextBlock t)
    {
        var ft = new FormattedText(t.Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch), t.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(t).PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace;
    }

    private static void Foto(string dir, string momento, int w, int h, string tema, Window janela, Window? dialogo)
    {
        var saida = Path.Combine(dir, $"{momento}-{w}{(tema == "claro" ? "" : "-" + tema)}.png");
        FotoVenda.Salvar(saida, w, h, janela, dialogo);
        Console.WriteLine($"foto-comanda: {saida} ({w}x{h}, tema {tema}, {momento})");
    }

    // ── miudezas ─────────────────────────────────────────────────────────────

    private static Pdv.Telas.Produto ProdutoDaTela(Pdv.Telas.Venda tela, string id)
        => Campo<System.Collections.IList>(tela, "_catalogo").Cast<Pdv.Telas.Produto>().First(p => p.Id == id);

    private static T Campo<T>(object alvo, string nome) => (T)alvo.GetType().GetField(nome, P)!.GetValue(alvo)!;

    private static void Definir(object alvo, string nome, object valor) => alvo.GetType().GetField(nome, P)!.SetValue(alvo, valor);

    private static object? Invocar(object alvo, string metodo, params object?[] args)
        => alvo.GetType().GetMethods(P).First(m => m.Name == metodo && m.GetParameters().Length == args.Length).Invoke(alvo, args);

    private static IEnumerable<T> Descendentes<T>(DependencyObject raiz) where T : DependencyObject
    {
        foreach (var filho in LogicalTreeHelper.GetChildren(raiz))
        {
            if (filho is not DependencyObject d) continue;
            if (d is T t) yield return t;
            foreach (var neto in Descendentes<T>(d)) yield return neto;
        }
    }

    /// <summary>Um processo vivo que nao e este (um ping de 2 min), para a linha "em uso no outro caixa".</summary>
    private sealed class ProcessoVivo : IDisposable
    {
        private readonly System.Diagnostics.Process _p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ping", "127.0.0.1 -n 120")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        public string Dono => $"{_p.Id}:{_p.StartTime.Ticks}";
        public void Dispose() { try { _p.Kill(); } catch { } _p.Dispose(); }
    }
}
