using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// FOTOGRAFA a tela de venda numa resolução dada, sem monitor daquele tamanho.
///
/// 03/09/2026: a Savassi (1024x768) recebeu duas versões "corrigidas" para tela
/// estreita e as duas pioraram (cabeçalho em três linhas, nome do produto na
/// comanda com uma letra por linha). O motivo é que o layout era ajustado no
/// XAML e conferido só no monitor grande de quem programa. Este modo monta a
/// tela de verdade (mesmo XAML, mesmo tema, catálogo real desta máquina) numa
/// janela do tamanho pedido e grava um PNG: o que a loja vê, antes de publicar.
///
/// Uso: Pdv.Testes.exe --foto-venda saida.png largura altura [categoria] [claro|escuro] [item;item] [pagar:4,00] [menu:cancelar|menu:fechar] [tef|tef+pos|semtef]
///
/// `menu:cancelar` / `menu:fechar` (04/09): abre um dos dois menus da barra de cima
/// antes de fotografar. Dá para pôr em qualquer posição: é tirado dos argumentos
/// antes da leitura posicional. A foto sempre imprime a altura da barra de cima
/// (Cabecalho), medida no layout de verdade, para a régua do "diminuir o topo".
///
/// `tef` / `tef+pos` (04/09, também em qualquer posição): liga um TEF de mentira
/// (PayGo numa pasta temporária) para a grade de pagamento mostrar o tile POS e abre
/// o pagamento; `tef+pos` além disso toca no POS e fotografa o diálogo de escolha da
/// forma ABERTO por cima da tela (a foto compõe as duas janelas: o diálogo é uma
/// Window própria, que o RenderTargetBitmap da tela não vê).
///
/// Segurança: roda contra uma CÓPIA do banco do caixa, com a nuvem apontada para
/// um endereço morto (api_base). Nada do que a tela faz ao abrir (sino, catálogo,
/// versão) chega à nuvem, e nada é gravado no banco de verdade.
/// </summary>
public static class FotoVenda
{
    public static int Rodar(string[] args)
    {
        var menu = args.FirstOrDefault(a => a.StartsWith("menu:", StringComparison.OrdinalIgnoreCase))?[5..].ToLowerInvariant();
        args = args.Where(a => !a.StartsWith("menu:", StringComparison.OrdinalIgnoreCase)).ToArray();
        // "tef" = maquininha integrada de mentira (o tile POS só existe com TEF);
        // "tef+pos" = também toca no POS e fotografa o diálogo aberto.
        // "semtef" = desliga o TEF na cópia (máquina de desenvolvimento costuma ter TEF ligado).
        var modo = args.FirstOrDefault(a => a.Equals("tef", StringComparison.OrdinalIgnoreCase)
                                         || a.Equals("tef+pos", StringComparison.OrdinalIgnoreCase)
                                         || a.Equals("semtef", StringComparison.OrdinalIgnoreCase))?.ToLowerInvariant();
        args = args.Where(a => a != modo).ToArray();
        var comTef = modo is "tef" or "tef+pos";
        var abrirPos = modo == "tef+pos";
        var semTef = modo == "semtef";
        var saida = Path.GetFullPath(args[1]);
        var w = int.Parse(args[2]);
        var h = int.Parse(args[3]);
        var categoria = args.Length > 4 ? args[4] : "";
        var tema = args.Length > 5 ? args[5] : "claro";
        var itens = args.Length > 6
            ? args[6].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
        // "pagar:4,00" abre o pagamento e lança uma parte em dinheiro (foto da tela de pagamento)
        decimal? pagar = args.Length > 7 && args[7].StartsWith("pagar:", StringComparison.OrdinalIgnoreCase)
            ? decimal.Parse(args[7][6..].Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture) : null;

        // ── banco: cópia do banco real desta máquina, nuvem morta ───────────────
        var origem = Banco.Arquivo;
        if (!File.Exists(origem))
        {
            Console.Error.WriteLine($"foto-venda: não achei o banco do caixa em {origem}");
            return 2;
        }
        var fixture = Path.Combine(Path.GetTempPath(), "foto-venda-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        File.Copy(origem, fixture);
        Banco.CaminhoForcado = fixture;
        string opId, opNome;
        using (var cx = Banco.Abrir())
        {
            cx.Execute("UPDATE terminal SET api_base = 'http://127.0.0.1:9'");
            var op = cx.QueryFirstOrDefault<(string? id, string? nome)>(
                "SELECT id, nome FROM operador ORDER BY nome LIMIT 1");
            opId = op.id ?? "op-foto";
            opNome = op.nome ?? "Teste";
            if (comTef)
            {
                // PayGo por troca de arquivos numa pasta vazia: o construtor não encosta no
                // disco, e a foto nunca cobra — só precisa de `Servicos.Tef()` não-nulo.
                Vendas.GravarConfig(cx, "tef_habilitado", "1");
                Vendas.GravarConfig(cx, "tef_provedor", "paygo");
                Vendas.GravarConfig(cx, "tef_paygo_pasta", Path.Combine(Path.GetTempPath(), "foto-venda-paygo"));
            }
            if (semTef) Vendas.GravarConfig(cx, "tef_habilitado", "0");
        }
        var operador = new Operador(opId, opNome, "operador");
        var sessao = new Sessao("sessao-foto", Caixa.DiaOperacional(), opId, opNome,
            DateTime.Now.Date.AddHours(8), Dinheiro.Zero);

        var codigo = 1;
        var t = new Thread(() =>
        {
            try
            {
                codigo = Fotografar(saida, w, h, categoria, tema, itens, operador, sessao, pagar, menu, comTef, abrirPos);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("foto-venda: " + ex);
                codigo = 1;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { File.Delete(fixture); } catch { /* temporário */ }
        return codigo;
    }

    private static int Fotografar(string saida, int w, int h, string categoria, string tema,
                                  string[] itens, Operador operador, Sessao sessao, decimal? pagar,
                                  string? menu = null, bool abrirPagamento = false, bool abrirPos = false)
    {
        // a tela de pagamento abre com "pagar:" (lança uma parte) ou com tef/tef+pos (só abre)
        abrirPagamento |= pagar is not null;
        var fim = abrirPagamento ? 5 : 3;
        var salvo = false;
        // Os recursos (tema + estilos) vivem no App.xaml do Pdv.exe. Sem passar pelo
        // App de verdade (trava de instância única, migração, serviços), montamos
        // um Application cru com os MESMOS dicionários, na MESMA ordem (contrato:
        // [0] paleta, [1] estilos).
        // URIs absolutas com o nome do assembly: Application.ResourceAssembly ja vem
        // definido (entry assembly = Pdv.Testes) e nao aceita troca, entao o
        // "Temas/Claro.xaml" relativo de Aparencia.Aplicar nao resolveria daqui.
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

        var tela = new Pdv.Telas.Venda(operador, sessao);
        var janela = new Window
        {
            Title = "foto-venda", Width = w, Height = h,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0,
            ShowInTaskbar = false, ShowActivated = false, Content = tela,
        };

        var codigo = 1;
        var etapa = 0;
        var relogio = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        relogio.Tick += (_, _) =>
        {
            etapa++;
            try
            {
                if (etapa == 1)
                {
                    // catálogo já carregado no construtor; agora a categoria e a comanda
                    var tipo = typeof(Pdv.Telas.Venda);
                    const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;
                    if (categoria.Length > 0)
                    {
                        // o nome vem como o operador lê ("Promoção"); a constante interna
                        // pode ter outra caixa. Casa com o Tag dos botões de categoria.
                        var lista = (System.Windows.Controls.ItemsControl)tipo.GetField("ListaCategorias", P)!.GetValue(tela)!;
                        var tags = lista.Items.Cast<System.Windows.Controls.Button>().Select(b => (string)b.Tag!).ToList();
                        var real = tags.FirstOrDefault(t => string.Equals(t, categoria, StringComparison.OrdinalIgnoreCase))
                                   ?? tags.FirstOrDefault(t => t.Contains(categoria, StringComparison.OrdinalIgnoreCase));
                        if (real is null) erros.Add($"categoria não achada: {categoria} (tem: {string.Join(", ", tags)})");
                        tipo.GetField("_categoriaAtual", P)!.SetValue(tela, real ?? categoria);
                        tipo.GetMethod("RepintarCategorias", P)!.Invoke(tela, null);
                        tipo.GetMethod("PintarProdutos", P)!.Invoke(tela, null);
                    }
                    if (itens.Length > 0)
                    {
                        var catalogo = (System.Collections.IEnumerable)tipo.GetField("_catalogo", P)!.GetValue(tela)!;
                        var adicionar = tipo.GetMethods(P).First(m => m.Name == "Adicionar" && m.GetParameters().Length == 1);
                        foreach (var nome in itens)
                        {
                            object? achado = null;
                            foreach (var p in catalogo)
                            {
                                var n = (string)p.GetType().GetProperty("Nome")!.GetValue(p)!;
                                if (n.Contains(nome, StringComparison.OrdinalIgnoreCase)) { achado = p; break; }
                            }
                            if (achado is null) erros.Add("item não achado no catálogo: " + nome);
                            else adicionar.Invoke(tela, new[] { achado });
                        }
                    }
                }
                else if (abrirPagamento && etapa == 2)
                {
                    // abre o pagamento como o botão Finalizar faria
                    var tipo = typeof(Pdv.Telas.Venda);
                    const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;
                    tipo.GetMethod("Finalizar", P)!.Invoke(tela, new object?[] { null, new RoutedEventArgs() });
                }
                else if (menu is not null && etapa == 2)
                {
                    // abre um dos menus da barra como o toque no botão faria; o menu é
                    // um véu DENTRO da tela (não é janela), por isso sai na foto
                    var tipo = typeof(Pdv.Telas.Venda);
                    const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;
                    var nome = menu switch { "cancelar" => "MenuCancelamento", "fechar" => "MenuFecharSair", _ => null };
                    var metodo = nome is null ? null : tipo.GetMethod(nome, P);
                    if (metodo is null) erros.Add($"menu não achado: {menu} (use menu:cancelar ou menu:fechar)");
                    else metodo.Invoke(tela, new object?[] { null, new RoutedEventArgs() });
                }
                else if (abrirPagamento && etapa == 3)
                {
                    var pg = Pagamento(tela);
                    if (pg is null) erros.Add("a tela de pagamento não abriu");
                    else if (pagar is decimal valorParte && valorParte > 0)
                        typeof(Pdv.Telas.Pagamento).GetMethod("AdicionarParte", BindingFlags.NonPublic | BindingFlags.Instance)!
                            .Invoke(pg, new object[] { new PagamentoVenda("dinheiro", Dinheiro.DeReais(valorParte), Dinheiro.Zero) });
                }
                else if (abrirPos && etapa == 4)
                {
                    // Toca no POS. O diálogo é modal: este tick fica preso no ShowDialog.
                    // A foto sai por um timer PRÓPRIO, armado antes do toque: o DispatcherTimer
                    // só se re-arma quando o handler do Tick retorna, então o `relogio` não
                    // dispararia de novo enquanto o diálogo estiver aberto — mas um timer
                    // armado antes dispara normalmente no laço aninhado do ShowDialog.
                    var pg = Pagamento(tela);
                    var grade = pg is null ? null
                        : (System.Windows.Controls.Primitives.UniformGrid)typeof(Pdv.Telas.Pagamento)
                            .GetField("GradeFormas", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pg)!;
                    var pos = grade?.Children.OfType<System.Windows.Controls.Button>()
                        .FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == "POS");
                    if (pos is null) { erros.Add("o tile POS não está na grade (TEF desligado?)"); return; }

                    var foto = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                    foto.Tick += (_, _) =>
                    {
                        foto.Stop();
                        var dialogo = Application.Current.Windows.OfType<Window>()
                            .FirstOrDefault(x => x != janela && x.Owner == janela && x.IsVisible);
                        if (dialogo is null) erros.Add("o diálogo do POS não abriu");
                        Salvar(saida, w, h, janela, dialogo);
                        salvo = true;
                        Console.WriteLine($"foto-venda: {saida} ({w}x{h}, tema {tema}, diálogo do POS {(dialogo is null ? "ausente" : "aberto")})");
                        dialogo?.Close();
                    };
                    foto.Start();
                    pos.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                }
                else if (etapa >= fim)
                {
                    relogio.Stop();
                    if (!salvo)
                    {
                        Salvar(saida, w, h, janela, null);
                        Console.WriteLine($"foto-venda: {saida} ({w}x{h}, tema {tema}, categoria '{categoria}', {itens.Length} item(ns))");
                    }
                    // a régua do topo: altura real da barra de cima, medida no layout
                    var cab = typeof(Pdv.Telas.Venda)
                        .GetField("Cabecalho", BindingFlags.NonPublic | BindingFlags.Instance)?
                        .GetValue(tela) as FrameworkElement;
                    if (cab is not null) Console.WriteLine($"  topo: {cab.ActualHeight:0} px (barra de cima, Cabecalho)");
                    foreach (var e in erros.Distinct()) Console.WriteLine("  aviso: " + e);
                    codigo = 0;
                    janela.Close();
                    app.Shutdown();
                }
            }
            catch (Exception ex)
            {
                relogio.Stop();
                Console.Error.WriteLine("foto-venda: " + ex);
                codigo = 1;
                app.Shutdown();
            }
        };
        janela.Loaded += (_, _) => relogio.Start();
        // rede morta pode segurar o Loaded? não: Loaded é do layout. Mas um travamento
        // qualquer não pode prender a esteira: 25 s e desiste.
        var limite = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        limite.Tick += (_, _) => { Console.Error.WriteLine("foto-venda: tempo esgotado"); codigo = 3; app.Shutdown(); };
        limite.Start();
        janela.Show();
        app.Run();
        return codigo;
    }

    private static Pdv.Telas.Pagamento? Pagamento(Pdv.Telas.Venda tela)
    {
        var painel = (System.Windows.Controls.ContentControl)typeof(Pdv.Telas.Venda)
            .GetField("PainelPagamento", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tela)!;
        return painel.Content as Pdv.Telas.Pagamento;
    }

    /// <summary>
    /// Grava a tela e, se houver, o diálogo modal por cima, na posição em que ele está.
    /// O diálogo é outra Window: renderizar só o conteúdo da janela principal o deixaria
    /// de fora, e a foto mentiria que ele não existe.
    /// </summary>
    internal static void Salvar(string saida, int w, int h, Window janela, Window? dialogo)
    {
        var principal = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        principal.Render((Visual)janela.Content);

        BitmapSource final = principal;
        if (dialogo is not null)
        {
            var dw = Math.Max(1, (int)Math.Ceiling(dialogo.ActualWidth));
            var dh = Math.Max(1, (int)Math.Ceiling(dialogo.ActualHeight));
            var bmpDialogo = new RenderTargetBitmap(dw, dh, 96, 96, PixelFormats.Pbgra32);
            bmpDialogo.Render(dialogo);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(principal, new Rect(0, 0, w, h));
                dc.DrawImage(bmpDialogo, new Rect(dialogo.Left - janela.Left, dialogo.Top - janela.Top, dw, dh));
            }
            var composta = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            composta.Render(dv);
            final = composta;
        }

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(final));
        Directory.CreateDirectory(Path.GetDirectoryName(saida)!);
        using var fs = File.Create(saida);
        enc.Save(fs);
    }
}
