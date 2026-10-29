using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// A BARRA DO WINDOWS NO CAIXA (1.0.58, 29/10/2026, pedido do dono): "ver a barra de tarefa sem menu
/// iniciar nem nada, somente configuração de wifi e som". Uma faixa fina embaixo da tela, com o leque
/// do Wi-Fi e o alto-falante à direita, como no Windows. Tocar abre a janelinha de cada um.
///
/// Quando aparece: Pdv.Nucleo/BarraDoWindows.Mostrar (escolha da Configuração; sem escolha, só com o
/// Windows sem a barra dele, que é o quiosque). Reavaliada a cada 5 s, então abrir o Explorer pela
/// Configuração esconde esta e fechar a mostra de novo.
///
/// Qualquer operador usa: trocar de rede quando a internet cai é coisa do balcão. Nenhum botão pega
/// o foco do teclado (o leitor de código de barras continua bipando na venda).
/// </summary>
public sealed class BarraWindows : Border
{
    private readonly DispatcherTimer _relogio = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Path[] _arcosBarra;
    private readonly TextBlock _txtWifi = Texto(14);
    private readonly Path _altoFalante;
    private readonly Path[] _ondasBarra;
    private readonly Path _mudoBarra;
    private readonly TextBlock _txtSom = Texto(14);
    private readonly Button _btnWifi, _btnSom;

    private readonly Popup _popWifi = NovoPopup();
    private readonly StackPanel _listaRedes = new();
    private readonly TextBlock _avisoWifi = Texto(13, fraco: true);
    private readonly Button _btnLigarWifi;

    private readonly Popup _popSom = NovoPopup();
    private readonly Slider _slider = new() { Minimum = 0, Maximum = 100, SmallChange = 5, LargeChange = 10, IsMoveToPointEnabled = true, Focusable = false };
    private readonly TextBlock _txtSomGrande = Texto(26, negrito: true);
    private readonly Button _btnMudo;

    private bool _conectando;
    private bool _pintandoSlider;
    private int _consulta;

    public BarraWindows()
    {
        Visibility = Visibility.Collapsed;
        Height = 40;
        BorderThickness = new Thickness(0, 1, 0, 0);
        SetResourceReference(BackgroundProperty, "Painel");
        SetResourceReference(BorderBrushProperty, "Borda");

        var (leque, arcos) = Leque(1.0);
        _arcosBarra = arcos;
        _btnWifi = BotaoDaBarra(leque, _txtWifi, "Wi-Fi");
        _btnWifi.Click += (_, _) => AbrirWifi();

        (_altoFalante, _ondasBarra, _mudoBarra, var falante) = AltoFalante();
        _btnSom = BotaoDaBarra(falante, _txtSom, "Volume");
        _btnSom.Click += (_, _) => AbrirSom();

        var direita = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        direita.Children.Add(_btnWifi);
        direita.Children.Add(_btnSom);
        Child = direita;

        _btnLigarWifi = BotaoDoPopup("Ligar o Wi-Fi");
        _btnLigarWifi.Click += async (_, _) =>
        {
            var erro = await Task.Run(WifiDoWindows.LigarRadio);
            _avisoWifi.Text = erro ?? "Wi-Fi ligado. Procurando redes...";
            await PreencherRedesAsync(procurar: true);
        };
        _popWifi.Child = MolduraDoPopup(MontarWifi());
        _popWifi.PlacementTarget = _btnWifi;

        _btnMudo = BotaoDoPopup("Sem som");
        _btnMudo.Click += (_, _) => { var l = SomDoWindows.Ler(); SomDoWindows.Mudo(!l.Mudo); PintarSom(); };
        _popSom.Child = MolduraDoPopup(MontarSom());
        _popSom.PlacementTarget = _btnSom;

        _relogio.Tick += (_, _) => Reavaliar();
        Loaded += (_, _) => { Reavaliar(); _relogio.Start(); };
        Unloaded += (_, _) => _relogio.Stop();
    }

    /// <summary>Mostra ou esconde pela regra e, se visível, atualiza Wi-Fi e som. A Configuração chama ao salvar.</summary>
    public void Reavaliar()
    {
        string? opcao = null;
        try { using var cx = Banco.Abrir(); opcao = Vendas.Config(cx, BarraDoWindows.Chave); } catch { }
        var mostrar = BarraDoWindows.Mostrar(opcao, !WifiDoWindows.WindowsComBarra());
        Visibility = mostrar ? Visibility.Visible : Visibility.Collapsed;
        if (!mostrar) { _popWifi.IsOpen = false; _popSom.IsOpen = false; return; }
        PintarSom();
        if (!_conectando) _ = PintarWifiAsync();
    }

    // ── Wi-Fi ──────────────────────────────────────────────────────────────────

    private async Task PintarWifiAsync()
    {
        var e = await Task.Run(WifiDoWindows.Estado);
        if (_conectando) return;
        _txtWifi.Text = BarraDoWindows.TextoDoWifi(e);
        Acender(_arcosBarra, e.Situacao == BarraDoWindows.SituacaoWifi.Conectado ? BarraDoWindows.Arcos(e.Sinal) : 0,
                e.Situacao == BarraDoWindows.SituacaoWifi.Conectado);
    }

    private UIElement MontarWifi()
    {
        var p = new StackPanel { Width = 360 };
        var topo = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        topo.ColumnDefinitions.Add(new ColumnDefinition());
        topo.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titulo = Texto(20, negrito: true);
        titulo.Text = "Wi-Fi";
        titulo.VerticalAlignment = VerticalAlignment.Center;
        var procurar = BotaoDoPopup("Procurar de novo");
        procurar.MinHeight = 40;
        procurar.Click += async (_, _) => await PreencherRedesAsync(procurar: true);
        Grid.SetColumn(procurar, 1);
        topo.Children.Add(titulo);
        topo.Children.Add(procurar);
        p.Children.Add(topo);
        _avisoWifi.TextWrapping = TextWrapping.Wrap;
        _avisoWifi.Margin = new Thickness(0, 0, 0, 8);
        p.Children.Add(_avisoWifi);
        _btnLigarWifi.Visibility = Visibility.Collapsed;
        _btnLigarWifi.Margin = new Thickness(0, 0, 0, 8);
        p.Children.Add(_btnLigarWifi);
        p.Children.Add(new ScrollViewer
        {
            MaxHeight = 380,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
            Content = _listaRedes,
        });
        return p;
    }

    private void AbrirWifi()
    {
        _popSom.IsOpen = false;
        _popWifi.IsOpen = !_popWifi.IsOpen;
        if (_popWifi.IsOpen) _ = PreencherRedesAsync(procurar: true);
    }

    /// <summary>Mostra o que o Windows já sabe e, com <paramref name="procurar"/>, pede uma busca e mostra de novo.</summary>
    private async Task PreencherRedesAsync(bool procurar)
    {
        var minha = ++_consulta;
        var estado = await Task.Run(WifiDoWindows.Estado);
        if (minha != _consulta) return;
        if (estado.Situacao == BarraDoWindows.SituacaoWifi.SemPlaca)
        {
            _btnLigarWifi.Visibility = Visibility.Collapsed;
            _listaRedes.Children.Clear();
            _avisoWifi.Text = estado.Cabo ? "Este caixa não tem Wi-Fi. A internet chega pelo cabo." : "Este caixa não tem Wi-Fi e o cabo de rede está solto.";
            return;
        }
        if (estado.Situacao == BarraDoWindows.SituacaoWifi.Desligado)
        {
            _btnLigarWifi.Visibility = Visibility.Visible;
            _listaRedes.Children.Clear();
            _avisoWifi.Text = "O Wi-Fi está desligado.";
            return;
        }
        _btnLigarWifi.Visibility = Visibility.Collapsed;
        PintarRedes(await Task.Run(WifiDoWindows.Redes), procurando: procurar);
        if (!procurar) return;
        await Task.Run(WifiDoWindows.Procurar);
        await Task.Delay(3500);
        if (minha != _consulta || !_popWifi.IsOpen) return;
        PintarRedes(await Task.Run(WifiDoWindows.Redes), procurando: false);
    }

    private void PintarRedes(IReadOnlyList<BarraDoWindows.Rede> redes, bool procurando)
    {
        _listaRedes.Children.Clear();
        _avisoWifi.Text = procurando ? "Procurando redes..." : redes.Count == 0 ? "Nenhuma rede por perto." : "Toque na rede para conectar.";
        foreach (var r in redes) _listaRedes.Children.Add(LinhaDaRede(r));
    }

    private Button LinhaDaRede(BarraDoWindows.Rede r)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var (leque, arcos) = Leque(1.1);
        Acender(arcos, BarraDoWindows.Arcos(r.Sinal), true);
        leque.Margin = new Thickness(0, 0, 12, 0);
        var nome = Texto(16, negrito: r.Conectada);
        nome.Text = r.Nome;
        nome.TextTrimming = TextTrimming.CharacterEllipsis;
        nome.VerticalAlignment = VerticalAlignment.Center;
        var rotulo = Texto(13, fraco: !r.Conectada);
        rotulo.Text = BarraDoWindows.Rotulo(r);
        rotulo.VerticalAlignment = VerticalAlignment.Center;
        rotulo.Margin = new Thickness(10, 0, 0, 0);
        if (r.Conectada) rotulo.SetResourceReference(TextBlock.ForegroundProperty, "Ok");
        Grid.SetColumn(nome, 1); Grid.SetColumn(rotulo, 2);
        g.Children.Add(leque); g.Children.Add(nome); g.Children.Add(rotulo);

        var b = BotaoDoPopup("");
        b.Content = g;
        b.MinHeight = 52;
        b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        b.Margin = new Thickness(0, 0, 0, 6);
        b.IsHitTestVisible = !r.Conectada;   // a conectada nao e botao, mas nao fica apagada
        b.Click += async (_, _) => await ConectarAsync(r);
        return b;
    }

    private async Task ConectarAsync(BarraDoWindows.Rede r, bool pedirSenhaNova = false)
    {
        _popWifi.IsOpen = false;
        var dono = Window.GetWindow(this);
        var como = BarraDoWindows.Conexao(r.Autenticacao, r.Cifra);
        string? xml = null;
        if (!r.Salva || pedirSenhaNova)
        {
            if (como.Motivo is not null) { if (dono is not null) Dialogo.Avisar(dono, r.Nome, como.Motivo); return; }
            string? senha = null;
            if (como.PedeSenha)
            {
                if (dono is null) return;
                senha = SenhaDoWifi(dono, r.Nome);
                if (senha is null) return;
            }
            xml = BarraDoWindows.PerfilXml(r.SsidBytes, como, senha);
        }

        _conectando = true;
        _txtWifi.Text = "Conectando em " + r.Nome + "...";
        Acender(_arcosBarra, 0, false);
        string? erro;
        try { erro = await WifiDoWindows.ConectarAsync(r, xml); }
        finally { _conectando = false; }
        await PintarWifiAsync();
        if (erro is null || dono is null) return;

        // rede salva que não entrou: quase sempre a senha do roteador mudou
        if (r.Salva && xml is null && como.PedeSenha && como.Motivo is null)
        {
            if (Dialogo.Escolher(dono, r.Nome, "Não conectou. A senha do Wi-Fi pode ter mudado.", "Digitar a senha") == 0)
                await ConectarAsync(r, pedirSenhaNova: true);
            return;
        }
        Dialogo.Avisar(dono, r.Nome, erro, "erro");
    }

    /// <summary>A senha da rede: pelo teclado do caixa (o Wi-Fi tem letras), com "Mostrar a senha".</summary>
    private static string? SenhaDoWifi(Window dono, string rede)
    {
        string? resultado = null;
        var janela = Dialogo.Base(dono, 480);
        var painel = new StackPanel();
        painel.Children.Add(PedirValor.Cabecalho(janela, rede));
        var rotulo = new TextBlock
        {
            Text = "Senha do Wi-Fi",
            Style = (Style)Application.Current.Resources["Rotulo"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        painel.Children.Add(rotulo);
        var oculta = new PasswordBox { FontSize = 20, Padding = new Thickness(12, 10, 12, 10) };
        var aberta = new TextBox { FontSize = 20, Padding = new Thickness(12, 10, 12, 10), Visibility = Visibility.Collapsed };
        painel.Children.Add(oculta);
        painel.Children.Add(aberta);
        var mostrar = new CheckBox { Content = "Mostrar a senha", FontSize = 15, Margin = new Thickness(0, 10, 0, 0) };
        mostrar.SetResourceReference(Control.ForegroundProperty, "Texto");
        mostrar.Checked += (_, _) => { aberta.Text = oculta.Password; oculta.Visibility = Visibility.Collapsed; aberta.Visibility = Visibility.Visible; aberta.Focus(); aberta.CaretIndex = aberta.Text.Length; };
        mostrar.Unchecked += (_, _) => { oculta.Password = aberta.Text; aberta.Visibility = Visibility.Collapsed; oculta.Visibility = Visibility.Visible; oculta.Focus(); };
        painel.Children.Add(mostrar);

        var linha = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        var cancelar = PedirValor.Botao("Cancelar", false);
        var ok = PedirValor.Botao("Conectar", true);
        cancelar.Margin = new Thickness(0, 0, 5, 0);
        ok.Margin = new Thickness(5, 0, 0, 0);
        cancelar.Click += (_, _) => janela.Close();
        void Confirmar()
        {
            var senha = mostrar.IsChecked == true ? aberta.Text : oculta.Password;
            if (BarraDoWindows.ProblemaNaSenha(senha) is { } problema)
            {
                rotulo.Text = problema;
                rotulo.SetResourceReference(TextBlock.ForegroundProperty, "Erro");
                return;
            }
            resultado = senha;
            janela.Close();
        }
        ok.Click += (_, _) => Confirmar();
        Grid.SetColumn(cancelar, 0); Grid.SetColumn(ok, 1);
        linha.Children.Add(cancelar); linha.Children.Add(ok);
        painel.Children.Add(linha);

        janela.Content = Dialogo.Moldura(painel);
        janela.Loaded += (_, _) =>
        {
            oculta.Focus();
            janela.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)PedirTexto.AbrirTecladoVirtualSeTouch);
        };
        // fecha fora do processamento da tecla (o mesmo cuidado do PedirSenha)
        janela.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                e.Handled = true;
                janela.Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)janela.Close);
            }
            else if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                janela.Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)Confirmar);
            }
        };
        janela.ShowDialog();
        return resultado;
    }

    // ── Som ────────────────────────────────────────────────────────────────────

    private UIElement MontarSom()
    {
        var p = new StackPanel { Width = 320 };
        var topo = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        topo.ColumnDefinitions.Add(new ColumnDefinition());
        topo.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titulo = Texto(20, negrito: true);
        titulo.Text = "Som";
        titulo.VerticalAlignment = VerticalAlignment.Center;
        _txtSomGrande.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_txtSomGrande, 1);
        topo.Children.Add(titulo);
        topo.Children.Add(_txtSomGrande);
        p.Children.Add(topo);

        var linha = new Grid();
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var menos = BotaoDoPopup("−");
        var mais = BotaoDoPopup("+");
        foreach (var b in new[] { menos, mais }) { b.Width = 52; b.MinHeight = 52; b.FontSize = 24; b.Padding = new Thickness(0); }
        menos.Click += (_, _) => MudarVolume(BarraDoWindows.Passo(SomDoWindows.Ler().Porcentagem, -10));
        mais.Click += (_, _) => MudarVolume(BarraDoWindows.Passo(SomDoWindows.Ler().Porcentagem, +10));
        _slider.VerticalAlignment = VerticalAlignment.Center;
        _slider.Margin = new Thickness(12, 0, 12, 0);
        _slider.ValueChanged += (_, e) => { if (!_pintandoSlider) MudarVolume((int)Math.Round(e.NewValue)); };
        Grid.SetColumn(_slider, 1); Grid.SetColumn(mais, 2);
        linha.Children.Add(menos); linha.Children.Add(_slider); linha.Children.Add(mais);
        p.Children.Add(linha);

        _btnMudo.Margin = new Thickness(0, 12, 0, 0);
        p.Children.Add(_btnMudo);
        return p;
    }

    private void AbrirSom()
    {
        _popWifi.IsOpen = false;
        _popSom.IsOpen = !_popSom.IsOpen;
        PintarSom();
    }

    private void MudarVolume(int porcentagem)
    {
        SomDoWindows.Volume(porcentagem);
        PintarSom();
    }

    private void PintarSom()
    {
        var l = SomDoWindows.Ler();
        _txtSom.Text = BarraDoWindows.TextoDoSom(l.Achou, l.Porcentagem, l.Mudo);
        _txtSomGrande.Text = _txtSom.Text;
        var ondas = l.Achou ? BarraDoWindows.Ondas(l.Porcentagem, l.Mudo) : -1;
        for (var i = 0; i < _ondasBarra.Length; i++) _ondasBarra[i].Visibility = i < ondas ? Visibility.Visible : Visibility.Collapsed;
        _mudoBarra.Visibility = ondas < 0 ? Visibility.Visible : Visibility.Collapsed;
        _altoFalante.Opacity = l.Achou ? 1 : 0.4;
        _btnMudo.Content = l.Mudo ? "Ligar o som" : "Sem som";
        _btnMudo.IsEnabled = l.Achou;
        _slider.IsEnabled = l.Achou;
        _pintandoSlider = true;
        try { _slider.Value = l.Porcentagem; }
        finally { _pintandoSlider = false; }
    }

    // ── desenho ────────────────────────────────────────────────────────────────

    /// <summary>O leque do Wi-Fi: o ponto e três arcos (raios 4,5, 8,5 e 12,5 a partir de baixo).</summary>
    private static (Canvas Leque, Path[] Arcos) Leque(double escala)
    {
        var c = new Canvas { Width = 20, Height = 16, VerticalAlignment = VerticalAlignment.Center,
                             LayoutTransform = new ScaleTransform(escala, escala) };
        var ponto = new Ellipse { Width = 3.4, Height = 3.4 };
        Canvas.SetLeft(ponto, 8.3); Canvas.SetTop(ponto, 12.3);
        ponto.SetResourceReference(Shape.FillProperty, "Texto");
        c.Children.Add(ponto);
        var arcos = new[] { 4.5, 8.5, 12.5 }.Select(r =>
        {
            var d = r * 0.7071;
            var a = new Path
            {
                Data = Geometry.Parse(FormattableString.Invariant($"M {10 - d:0.##},{15 - d:0.##} A {r},{r} 0 0 1 {10 + d:0.##},{15 - d:0.##}")),
                StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            a.SetResourceReference(Shape.StrokeProperty, "Texto");
            c.Children.Add(a);
            return a;
        }).ToArray();
        return (c, arcos);
    }

    /// <summary>Acende os <paramref name="quantos"/> arcos de dentro; o ponto some junto quando não há conexão.</summary>
    private static void Acender(Path[] arcos, int quantos, bool conectado)
    {
        for (var i = 0; i < arcos.Length; i++) arcos[i].Opacity = i < quantos ? 1 : 0.28;
        if (arcos.Length > 0 && arcos[0].Parent is Canvas c && c.Children[0] is Ellipse ponto) ponto.Opacity = conectado ? 1 : 0.28;
    }

    private static (Path Corpo, Path[] Ondas, Path Mudo, Canvas Desenho) AltoFalante()
    {
        var c = new Canvas { Width = 20, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        var corpo = new Path { Data = Geometry.Parse("M 1,5.5 H 4.5 L 9,1.5 V 14.5 L 4.5,10.5 H 1 Z") };
        corpo.SetResourceReference(Shape.FillProperty, "Texto");
        c.Children.Add(corpo);
        var ondas = new[] { "M 11.5,5.5 A 3,3 0 0 1 11.5,10.5", "M 13.5,2.5 A 6.5,6.5 0 0 1 13.5,13.5" }.Select(d =>
        {
            var o = new Path { Data = Geometry.Parse(d), StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            o.SetResourceReference(Shape.StrokeProperty, "Texto");
            c.Children.Add(o);
            return o;
        }).ToArray();
        var mudo = new Path { Data = Geometry.Parse("M 12,5.5 L 17,10.5 M 17,5.5 L 12,10.5"), StrokeThickness = 1.8,
                              StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Visibility = Visibility.Collapsed };
        mudo.SetResourceReference(Shape.StrokeProperty, "Erro");
        c.Children.Add(mudo);
        return (corpo, ondas, mudo, c);
    }

    private static TextBlock Texto(double tamanho, bool negrito = false, bool fraco = false)
    {
        var t = new TextBlock { FontSize = tamanho, FontWeight = negrito ? FontWeights.Bold : FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, fraco ? "TextoFraco" : "Texto");
        return t;
    }

    private static Button BotaoDaBarra(UIElement icone, TextBlock texto, string dica)
    {
        texto.Margin = new Thickness(8, 0, 0, 0);
        texto.VerticalAlignment = VerticalAlignment.Center;
        texto.MaxWidth = 220;
        texto.TextTrimming = TextTrimming.CharacterEllipsis;
        var conteudo = new StackPanel { Orientation = Orientation.Horizontal };
        conteudo.Children.Add(icone);
        conteudo.Children.Add(texto);
        var b = new Button
        {
            Content = conteudo,
            Style = (Style)Application.Current.Resources["BotaoBase"],
            MinHeight = 38, Height = 38, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(0, 0, 4, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Focusable = false, ToolTip = dica,
        };
        return b;
    }

    private static Button BotaoDoPopup(string texto)
    {
        var b = new Button
        {
            Content = texto,
            Style = (Style)Application.Current.Resources["BotaoBase"],
            MinHeight = 46, FontSize = 15, Padding = new Thickness(14, 0, 14, 0),
            Focusable = false,
        };
        b.SetResourceReference(BackgroundProperty, "PainelAlto");
        return b;
    }

    private static Popup NovoPopup() => new()
    {
        Placement = PlacementMode.Top,
        StaysOpen = false,
        AllowsTransparency = true,
        PopupAnimation = PopupAnimation.Fade,
        VerticalOffset = -6,
    };

    private static Border MolduraDoPopup(UIElement conteudo)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(8),
            Child = conteudo,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35, Color = Colors.Black },
        };
        b.SetResourceReference(BackgroundProperty, "Painel");
        b.SetResourceReference(BorderBrushProperty, "Borda");
        return b;
    }
}
