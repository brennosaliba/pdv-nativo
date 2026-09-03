using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// Pede um valor em dinheiro com teclado na tela. Digitação em CENTAVOS, da direita
/// pra esquerda (igual maquininha): teclar 1-5-0-0 vira R$ 15,00. Ponto decimal em
/// tela touch é a principal fonte de erro de casa decimal — e num caixa, errar a
/// casa decimal é errar por 10x.
///
/// Os três diálogos deste arquivo usam a MESMA moldura do <see cref="Dialogo"/>
/// (sem chrome do Windows): a barra de título do sistema, com ícone e ✕ antigos,
/// quebrava a linguagem visual e os alvos dela são pequenos demais pro dedo.
/// </summary>
public static class PedirValor
{
    public static Dinheiro? Mostrar(Window dono, string titulo, string rotulo)
    {
        long centavos = 0;
        Dinheiro? resultado = null;

        var janela = Dialogo.Base(dono, 440);
        var painel = new StackPanel();
        painel.Children.Add(Cabecalho(janela, titulo));
        painel.Children.Add(new TextBlock
        {
            Text = rotulo,
            Style = (Style)Application.Current.Resources["Rotulo"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var visor = new TextBlock
        {
            Text = Dinheiro.Zero.Formatado(),
            FontSize = 40, FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["Marca"],
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        };
        painel.Children.Add(visor);

        void Pinta() => visor.Text = new Dinheiro(centavos).Formatado();

        var teclado = new TecladoNumerico();
        teclado.Digitou += d => { if (centavos < 99_999_99) { centavos = centavos * 10 + (d[0] - '0'); Pinta(); } };
        teclado.Apagou += () => { centavos /= 10; Pinta(); };
        teclado.Limpou += () => { centavos = 0; Pinta(); };
        painel.Children.Add(teclado);

        var linha = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        var cancelar = Botao("Cancelar", false);
        var ok = Botao("Confirmar", true);
        cancelar.Margin = new Thickness(0, 0, 6, 0);
        ok.Margin = new Thickness(6, 0, 0, 0);
        cancelar.Click += (_, _) => janela.Close();
        ok.Click += (_, _) => { resultado = new Dinheiro(centavos); janela.Close(); };
        Grid.SetColumn(cancelar, 0); Grid.SetColumn(ok, 1);
        linha.Children.Add(cancelar); linha.Children.Add(ok);
        painel.Children.Add(linha);

        janela.KeyDown += (_, e) =>
        {
            var d = e.Key is >= Key.D0 and <= Key.D9 ? e.Key - Key.D0
                  : e.Key is >= Key.NumPad0 and <= Key.NumPad9 ? e.Key - Key.NumPad0 : -1;
            if (d >= 0 && centavos < 99_999_99) { centavos = centavos * 10 + d; Pinta(); }
            else if (e.Key == Key.Back) { centavos /= 10; Pinta(); }
            else if (e.Key == Key.Enter) { resultado = new Dinheiro(centavos); janela.Close(); }
            else if (e.Key == Key.Escape) janela.Close();
        };

        janela.Content = Dialogo.Moldura(painel);
        janela.ShowDialog();
        return resultado;
    }

    /// <summary>Título dentro da moldura + ✕ grande — substitui a barra de título do Windows.</summary>
    internal static Grid Cabecalho(Window janela, string titulo)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new TextBlock
        {
            Text = titulo, FontSize = 22, FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["Texto"],
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        var fechar = new Button
        {
            Content = "✕", Width = 46, Height = 46, FontSize = 18,
            Style = (Style)Application.Current.Resources["BotaoBase"],
            VerticalAlignment = VerticalAlignment.Top,
        };
        fechar.Click += (_, _) => janela.Close();
        Grid.SetColumn(t, 0); Grid.SetColumn(fechar, 1);
        g.Children.Add(t); g.Children.Add(fechar);
        return g;
    }

    internal static Button Botao(string texto, bool destaque) => new()
    {
        Content = texto,
        Style = (Style)Application.Current.Resources[destaque ? "BotaoPrincipal" : "BotaoBase"],
        MinHeight = 58, FontSize = 17,
    };
}

/// <summary>
/// Pede o CPF/CNPJ do consumidor com teclado numérico NA TELA (o caixa é touch — sem
/// isso o operador simplesmente não consegue digitar). Formata enquanto digita e valida
/// o dígito verificador ao vivo: o Confirmar só habilita com documento que fecha, então
/// o erro aparece ANTES do operador achar que terminou.
///
/// Devolve: null = desistiu · "" = sem documento (limpa) · dígitos = documento válido.
/// </summary>
public static class PedirDocumento
{
    public static string? Mostrar(Window dono, string atual)
    {
        var digitos = new string((atual ?? "").Where(char.IsDigit).ToArray());
        string? resultado = null;

        var janela = Dialogo.Base(dono, 440);
        var painel = new StackPanel();
        painel.Children.Add(PedirValor.Cabecalho(janela, "CPF/CNPJ na nota"));

        var visor = new TextBlock
        {
            Text = "___.___.___-__",
            FontSize = 34, FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["Texto"],
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var estado = new TextBlock
        {
            FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10), MinHeight = 20,
        };
        painel.Children.Add(visor);
        painel.Children.Add(estado);

        var ok = PedirValor.Botao("Confirmar", true);

        void Pinta()
        {
            visor.Text = digitos.Length == 0 ? "___.___.___-__" : Mascara(digitos);
            var completo = digitos.Length is 11 or 14;
            var valido = completo && Documentos.ParaNota(digitos) is not null;
            ok.IsEnabled = valido;

            if (digitos.Length == 0)
            {
                estado.Text = "digite o CPF (11) ou CNPJ (14)";
                estado.Foreground = (Brush)Application.Current.Resources["TextoFraco"];
            }
            else if (valido)
            {
                estado.Text = digitos.Length == 11 ? "CPF válido ✓" : "CNPJ válido ✓";
                estado.Foreground = (Brush)Application.Current.Resources["Ok"];
            }
            else if (completo)
            {
                estado.Text = "o dígito verificador não fecha: confira os números";
                estado.Foreground = (Brush)Application.Current.Resources["Erro"];
            }
            else
            {
                estado.Text = $"{digitos.Length} de {(digitos.Length <= 11 ? 11 : 14)} dígitos";
                estado.Foreground = (Brush)Application.Current.Resources["TextoFraco"];
            }
        }

        var teclado = new TecladoNumerico();
        teclado.Digitou += d => { if (digitos.Length < 14) { digitos += d[0]; Pinta(); } };
        teclado.Apagou += () => { if (digitos.Length > 0) { digitos = digitos[..^1]; Pinta(); } };
        teclado.Limpou += () => { digitos = ""; Pinta(); };
        painel.Children.Add(teclado);

        var linha = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        var cancelar = PedirValor.Botao("Cancelar", false);
        var sem = PedirValor.Botao("Sem CPF", false);
        cancelar.Margin = new Thickness(0, 0, 4, 0);
        sem.Margin = new Thickness(4, 0, 4, 0);
        ok.Margin = new Thickness(4, 0, 0, 0);
        cancelar.Click += (_, _) => janela.Close();
        sem.Click += (_, _) => { resultado = ""; janela.Close(); };
        ok.Click += (_, _) => { resultado = digitos; janela.Close(); };
        Grid.SetColumn(cancelar, 0); Grid.SetColumn(sem, 1); Grid.SetColumn(ok, 2);
        linha.Children.Add(cancelar); linha.Children.Add(sem); linha.Children.Add(ok);
        painel.Children.Add(linha);

        janela.KeyDown += (_, e) =>
        {
            var d = e.Key is >= Key.D0 and <= Key.D9 ? (char)('0' + (e.Key - Key.D0))
                  : e.Key is >= Key.NumPad0 and <= Key.NumPad9 ? (char)('0' + (e.Key - Key.NumPad0)) : '\0';
            if (d != '\0' && digitos.Length < 14) { digitos += d; Pinta(); }
            else if (e.Key == Key.Back && digitos.Length > 0) { digitos = digitos[..^1]; Pinta(); }
            else if (e.Key == Key.Enter && ok.IsEnabled) { resultado = digitos; janela.Close(); }
            else if (e.Key == Key.Escape) janela.Close();
        };

        Pinta();
        janela.Content = Dialogo.Moldura(painel);
        janela.ShowDialog();
        return resultado;
    }

    /// <summary>Máscara progressiva: até 11 dígitos desenha como CPF; 12+ vira CNPJ.</summary>
    private static string Mascara(string d)
    {
        if (d.Length <= 11)
        {
            var s = d;
            if (s.Length > 3) s = s.Insert(3, ".");
            if (s.Length > 7) s = s.Insert(7, ".");
            if (s.Length > 11) s = s.Insert(11, "-");
            return s;
        }
        var c = d;
        c = c.Insert(2, ".");
        c = c.Insert(6, ".");
        c = c.Insert(10, "/");
        if (c.Length > 15) c = c.Insert(15, "-");
        return c;
    }
}

/// <summary>
/// Pede um texto curto (motivo, justificativa). Motivo em branco não passa.
/// Num caixa touch não há teclado físico: ao focar o campo, chamamos o teclado
/// virtual do Windows (TabTip) — sem ele a justificativa seria impossível de digitar.
/// </summary>
public static class PedirTexto
{
    public static string? Mostrar(Window dono, string titulo, string rotulo, string sugestao)
    {
        string? resultado = null;
        var janela = Dialogo.Base(dono, 480);
        var painel = new StackPanel();
        painel.Children.Add(PedirValor.Cabecalho(janela, titulo));
        painel.Children.Add(new TextBlock
        {
            Text = rotulo,
            Style = (Style)Application.Current.Resources["Rotulo"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        var caixa = new TextBox { Text = sugestao, FontSize = 18, MinHeight = 54, TextWrapping = TextWrapping.Wrap };
        painel.Children.Add(caixa);

        var linha = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        var cancelar = PedirValor.Botao("Cancelar", false);
        var ok = PedirValor.Botao("Confirmar", true);
        cancelar.Margin = new Thickness(0, 0, 5, 0);
        ok.Margin = new Thickness(5, 0, 0, 0);
        cancelar.Click += (_, _) => janela.Close();
        ok.Click += (_, _) => { resultado = caixa.Text.Trim(); janela.Close(); };
        Grid.SetColumn(cancelar, 0); Grid.SetColumn(ok, 1);
        linha.Children.Add(cancelar); linha.Children.Add(ok);
        painel.Children.Add(linha);

        caixa.GotFocus += (_, _) => AbrirTecladoVirtual();
        janela.Content = Dialogo.Moldura(painel);
        janela.Loaded += (_, _) => { caixa.Focus(); caixa.SelectAll(); };
        janela.KeyDown += (_, e) => { if (e.Key == Key.Escape) janela.Close(); };
        janela.ShowDialog();
        return string.IsNullOrWhiteSpace(resultado) ? null : resultado;
    }

    /// <summary>Melhor esforço: se o TabTip não existir/estiver bloqueado, segue sem ele.</summary>
    private static void AbrirTecladoVirtual()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = @"C:\Program Files\Common Files\microsoft shared\ink\TabTip.exe",
                UseShellExecute = true,
            });
        }
        catch { /* sem teclado virtual disponível — teclado físico ainda funciona */ }
    }
}
