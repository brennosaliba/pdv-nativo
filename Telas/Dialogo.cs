using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Pdv.Telas;

/// <summary>
/// Diálogos no visual do app. O MessageBox do Windows é cinza, quadrado e com fonte
/// pequena — parece outro programa, e num caixa touch os botões dele são pequenos
/// demais pro dedo. Estes seguem a mesma linguagem das outras telas: escuro, cantos
/// arredondados e alvo de toque grande.
/// </summary>
public static class Dialogo
{
    private static Brush R(string chave) => (Brush)Application.Current.Resources[chave];

    internal static Window Base(Window dono, int largura)
    {
        return new Window
        {
            Owner = dono,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Width = largura,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
    }

    /// <summary>
    /// Padding lateral da moldura. É const porque o relatório PRECISA saber quantos
    /// caracteres cabem numa linha (ver <see cref="Encaixar"/>): se este número e a
    /// conta da largura útil saírem de sincronia, o texto volta a ser cortado.
    /// </summary>
    private const double PadMoldura = 26;

    /// <summary>Quanto a moldura come da largura da janela: padding dos dois lados + a borda.</summary>
    private const double MolduraLateral = 2 * PadMoldura + 2;

    internal static Border Moldura(UIElement conteudo) => new()
    {
        Background = R("Painel"),
        CornerRadius = new CornerRadius(18),
        BorderBrush = R("Borda"),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(PadMoldura, 22, PadMoldura, 22),
        Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Color = Colors.Black,
            Opacity = (double)Application.Current.Resources["SombraDialogoOpacidade"] },
        Child = conteudo,
    };

    private static Button Botao(string texto, bool destaque, Brush? cor = null) => new()
    {
        Content = texto,
        Style = (Style)Application.Current.Resources[destaque ? "BotaoPrincipal" : "BotaoBase"],
        MinHeight = 58,
        FontSize = 17,
        Background = cor ?? (destaque ? R("RosaDegrade") : R("PainelAlto")),
    };

    /// <summary>Pergunta sim/não. `perigo` pinta a ação de vermelho (é destrutiva).</summary>
    public static bool Confirmar(Window dono, string titulo, string mensagem,
        string textoSim = "Confirmar", string textoNao = "Cancelar", bool perigo = false)
    {
        var resposta = false;
        var janela = Base(dono, 460);
        var pilha = new StackPanel();

        pilha.Children.Add(new TextBlock
        {
            Text = titulo, FontSize = 22, FontWeight = FontWeights.Bold,
            Foreground = R("Texto"), TextWrapping = TextWrapping.Wrap,
        });
        pilha.Children.Add(new TextBlock
        {
            Text = mensagem, FontSize = 15, Foreground = R("TextoFraco"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 22),
        });

        var linha = new Grid();
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        var nao = Botao(textoNao, false);
        var sim = Botao(textoSim, true, perigo ? R("Erro") : null);
        nao.Margin = new Thickness(0, 0, 6, 0);
        sim.Margin = new Thickness(6, 0, 0, 0);
        nao.Click += (_, _) => janela.Close();
        sim.Click += (_, _) => { resposta = true; janela.Close(); };
        Grid.SetColumn(nao, 0); Grid.SetColumn(sim, 1);
        linha.Children.Add(nao); linha.Children.Add(sim);
        pilha.Children.Add(linha);

        janela.Content = Moldura(pilha);
        janela.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) janela.Close();
            if (e.Key == System.Windows.Input.Key.Enter) { resposta = true; janela.Close(); }
        };
        janela.ShowDialog();
        return resposta;
    }

    /// <summary>
    /// Uma pergunta e as opções numa LINHA SÓ (lado a lado, alvo de dedo). Devolve o
    /// índice da opção tocada, ou -1 em Voltar/Esc. É o diálogo do POS: "Crédito,
    /// Débito, PIX, Refeição" sem lista rolável nem texto explicativo.
    /// </summary>
    public static int Escolher(Window dono, string titulo, string mensagem, params string[] opcoes)
    {
        var escolhido = -1;
        // 118 px por opção: 4 opções cabem em 526, que ainda é menor que a tela de 1024.
        var janela = Base(dono, Math.Max(460, (int)(opcoes.Length * 118 + MolduraLateral)));
        var pilha = new StackPanel();

        pilha.Children.Add(new TextBlock
        {
            Text = titulo, FontSize = 22, FontWeight = FontWeights.Bold,
            Foreground = R("Texto"), TextWrapping = TextWrapping.Wrap,
        });
        pilha.Children.Add(new TextBlock
        {
            Text = mensagem, FontSize = 15, Foreground = R("TextoFraco"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 18),
        });

        var linha = new UniformGrid { Rows = 1, Columns = Math.Max(1, opcoes.Length) };
        for (var i = 0; i < opcoes.Length; i++)
        {
            var idx = i;
            var b = Botao(opcoes[i], false);
            b.MinHeight = 68;
            b.Margin = new Thickness(i == 0 ? 0 : 4, 0, i == opcoes.Length - 1 ? 0 : 4, 0);
            b.Click += (_, _) => { escolhido = idx; janela.Close(); };
            linha.Children.Add(b);
        }
        pilha.Children.Add(linha);

        var voltar = Botao("Voltar", false);
        voltar.MinHeight = 52;
        voltar.FontSize = 15;
        voltar.Margin = new Thickness(0, 12, 0, 0);
        voltar.Click += (_, _) => janela.Close();
        pilha.Children.Add(voltar);

        janela.Content = Moldura(pilha);
        janela.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) janela.Close();
        };
        janela.ShowDialog();
        return escolhido;
    }

    /// <summary>Aviso simples. `tom`: "ok" (verde), "erro" (vermelho) ou null (neutro).</summary>
    public static void Avisar(Window dono, string titulo, string mensagem, string? tom = null)
    {
        var janela = Base(dono, 460);
        var pilha = new StackPanel();
        pilha.Children.Add(new TextBlock
        {
            Text = titulo, FontSize = 22, FontWeight = FontWeights.Bold,
            Foreground = tom switch { "ok" => R("Ok"), "erro" => R("Erro"), _ => R("Texto") },
            TextWrapping = TextWrapping.Wrap,
        });
        pilha.Children.Add(new TextBlock
        {
            Text = mensagem, FontSize = 15, Foreground = R("TextoFraco"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 22),
        });
        var ok = Botao("Entendi", true);
        ok.Click += (_, _) => janela.Close();
        pilha.Children.Add(ok);
        janela.Content = Moldura(pilha);
        janela.KeyDown += (_, e) =>
        {
            if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Escape) janela.Close();
        };
        janela.ShowDialog();
    }

    // ── O RELATÓRIO NÃO PODE CORTAR TEXTO ─────────────────────────────────────
    // O corpo é monoespaçado porque é TABELA ("Cardápio:", "Fotos:", "Notas:" em
    // coluna; o fechamento com valores alinhados à direita). Era NoWrap justamente
    // para não estragar esse alinhamento — e o preço foi o dono recebendo, no dia
    // 29/08, um aviso cortado no meio: "…Em 3 delas o envio D".
    //
    // O que se escolheu, e por quê:
    //  · TextWrapping.Wrap sozinho JÁ resolveria o corte sem estragar coluna nenhuma:
    //    quem alinha é a fonte monoespaçada, não o NoWrap, e as linhas da tabela têm
    //    30 caracteres num espaço de 82 — nunca chegam perto da borda. Mas a
    //    continuação da frase longa voltaria à coluna 0, onde toda linha começa um
    //    fato novo: pareceria mais um item da lista.
    //  · Quebrar em DOIS blocos (tabela + parágrafo) exigiria o Dialogo ADIVINHAR,
    //    de uma string crua, o que é linha de tabela e o que é prosa — e este mesmo
    //    método desenha o fechamento, onde tabela e prosa se alternam de propósito.
    //    Um palpite errado reagruparia o que quem chamou compôs de propósito.
    //  · Fica então: um bloco só, e a quebra feita AQUI, na medida real da fonte,
    //    com recuo na continuação (ela fica pendurada sob a própria linha, sem se
    //    disfarçar de item novo). O Wrap continua ligado como rede de segurança:
    //    se a medida errar para mais, o texto quebra feio — mas nunca some.
    //
    // A largura subiu de 620 para 720 pelo mesmo defeito: a linha mais larga do
    // fechamento ("SOBRA R$ 102.626,50" no fim) tem 76 caracteres e só cabiam 69,
    // ou seja, o relatório de FECHAMENTO já vinha cortando valor em silêncio.
    private const int LarguraRelatorio = 720;
    private const double PadCorpo = 16;
    private const double CorpoFontSize = 14;

    /// <summary>Texto monoespaçado (relatório de fechamento) — alinha as colunas.</summary>
    public static void Relatorio(Window dono, string titulo, string corpo, string? rodape = null)
    {
        var janela = Base(dono, LarguraRelatorio);
        var pilha = new StackPanel();
        pilha.Children.Add(new TextBlock
        {
            Text = titulo, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = R("Texto"),
        });
        var fonte = new FontFamily("Consolas");
        var util = LarguraRelatorio - MolduraLateral - 2 * PadCorpo;
        pilha.Children.Add(new Border
        {
            Background = R("Fundo"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(PadCorpo),
            Margin = new Thickness(0, 14, 0, 14),
            Child = new TextBlock
            {
                Text = Encaixar(corpo, Colunas(janela, util, fonte, CorpoFontSize)),
                FontFamily = fonte, FontSize = CorpoFontSize,
                Foreground = R("Texto"), TextWrapping = TextWrapping.Wrap,
            },
        });
        if (rodape is not null)
            pilha.Children.Add(new TextBlock
            {
                Text = rodape, FontSize = 14, Foreground = R("TextoFraco"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
            });
        var ok = Botao("Fechar", true);
        ok.Click += (_, _) => janela.Close();
        pilha.Children.Add(ok);
        janela.Content = Moldura(pilha);
        janela.ShowDialog();
    }

    /// <summary>
    /// Quantos caracteres cabem em <paramref name="util"/> pixels — MEDIDOS, não
    /// chutados. Consolas é monoespaçada, então medir dez "M" e dividir por dez dá a
    /// largura exata de qualquer caractere, inclusive quando o Windows cai numa fonte
    /// substituta (máquina de loja sem Consolas) ou quando o DPI não é 100%.
    ///
    /// Devolve 0 quando a medição falha; <see cref="Encaixar"/> entende 0 como "não
    /// mexa no texto", e aí o TextWrapping.Wrap do TextBlock segura a linha sozinho.
    /// Errar a medida tem que degradar para feio, nunca para cortado.
    /// </summary>
    private static int Colunas(Window janela, double util, FontFamily fonte, double tamanho)
    {
        try
        {
            var medida = new FormattedText(
                new string('M', 10), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(fonte, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                tamanho, Brushes.Black, VisualTreeHelper.GetDpi(janela).PixelsPerDip);
            var largura = medida.WidthIncludingTrailingWhitespace / 10.0;
            return largura <= 0 ? 0 : (int)(util / largura);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Quebra em <paramref name="colunas"/> APENAS as linhas que não cabem, e recua a
    /// continuação. Linha curta sai byte a byte igual à que entrou — é isso que
    /// preserva o alinhamento da tabela: ninguém mexe em quem já cabia.
    ///
    /// A quebra é no último espaço que cabe, então o espaçamento INTERNO da linha
    /// sobrevive (as colunas do fechamento são padding de espaços; colapsá-las
    /// desalinharia a metade que ficou). Palavra maior que a linha inteira — o rastro
    /// cru de um erro, uma URL — é cortada no osso, senão o laço não termina.
    ///
    /// Público (e sem nada de WPF dentro) para a suíte poder provar a regra sem
    /// abrir janela.
    /// </summary>
    public static string Encaixar(string corpo, int colunas, string recuo = "   ")
    {
        // Medida ausente ou absurda (janela minúscula, fonte que não mediu): devolve
        // o texto cru em vez de picotar em pedaços ilegíveis.
        if (colunas < 24 || recuo.Length >= colunas) return corpo;

        var saida = new List<string>();
        foreach (var linha in corpo.Replace("\r\n", "\n").Split('\n'))
        {
            var resto = linha;
            var prefixo = "";
            while (resto.Length > colunas - prefixo.Length)
            {
                var largura = colunas - prefixo.Length;
                var corte = resto.LastIndexOf(' ', largura);
                if (corte <= 0) corte = largura;    // palavra sozinha maior que a linha
                saida.Add((prefixo + resto[..corte]).TrimEnd());
                resto = resto[corte..].TrimStart();
                prefixo = recuo;
            }
            // O que sobrou fecha a linha. Só se some com ele quando é o rabo vazio de
            // uma quebra (a linha original terminava em espaço) — linha em branco de
            // verdade, que separa parágrafos, tem que continuar existindo.
            if (resto.Length > 0 || prefixo.Length == 0) saida.Add(prefixo + resto);
        }
        return string.Join("\n", saida);
    }
}
