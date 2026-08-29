using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pdv.Telas;

/// <summary>
/// O seletor de impressora da COMANDA DO DELIVERY, aberto pelo botão do cabeçalho da
/// aba Delivery (29/08 — relato do dono: "apos instalado, nao mostra a impressora no
/// PDV na aba de delivery, somente no dash principal").
///
/// É uma janela, e não um Popup, pelo mesmo motivo do resto do app: caixa touch. Popup
/// fecha ao encostar fora, e encostar fora é o que o dedo faz o tempo todo em cima de um
/// quadro de cards — o operador perderia a escolha no meio.
///
/// NÃO grava nada: devolve a escolha e quem grava é a tela, nas MESMAS chaves do
/// assistente de Configuração. As regras (o que a lista oferece, o que cada escolha
/// significa) vivem em <see cref="Impressao"/>, fora do WPF, onde a suíte alcança.
/// </summary>
public static class SeletorComanda
{
    private static Brush R(string chave) => (Brush)Application.Current.Resources[chave];

    /// <summary>
    /// Mostra o seletor e devolve a escolha, ou <c>null</c> se o operador desistiu.
    /// <c>IndicePapel</c> indexa <see cref="AssistenteConfig.OpcoesPapel"/> e só vale
    /// quando a comanda ganhou impressora própria.
    /// </summary>
    public static (Impressao.OpcaoComanda Opcao, int IndicePapel)? Escolher(
        Window dono, IReadOnlyList<Impressao.OpcaoComanda> opcoes, int selecionada, int indicePapel)
    {
        (Impressao.OpcaoComanda, int)? resposta = null;
        var janela = Dialogo.Base(dono, 520);
        var pilha = new StackPanel();

        pilha.Children.Add(new TextBlock
        {
            Text = "Onde a comanda do delivery sai",
            FontSize = 22, FontWeight = FontWeights.Bold, Foreground = R("Texto"),
            TextWrapping = TextWrapping.Wrap,
        });
        pilha.Children.Add(new TextBlock
        {
            // Diz a CONSEQUÊNCIA de escolher outra: é a informação que faltava na tela.
            Text = "A comanda é o papel da COZINHA (itens e observações do pedido). O cupom "
                 + "fiscal e o recibo do cliente continuam saindo na impressora do caixa.",
            FontSize = 14, Foreground = R("TextoFraco"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 16),
        });

        var cboImpressora = new ComboBox
        {
            ItemsSource = opcoes, DisplayMemberPath = nameof(Impressao.OpcaoComanda.Rotulo),
            SelectedIndex = Math.Clamp(selecionada, 0, Math.Max(0, opcoes.Count - 1)),
            FontSize = 17, MinHeight = 52, Padding = new Thickness(12, 8, 12, 8),
        };
        pilha.Children.Add(cboImpressora);

        // ── LARGURA DA BOBINA ────────────────────────────────────────────────
        // Só aparece com impressora PRÓPRIA, e por dois motivos opostos que dão no
        // mesmo lugar. Com impressora própria ela é OBRIGATÓRIA: a bobina da expedição
        // costuma ser de 58 mm e a do balcão de 80, e mandar 40 colunas para 58 mm
        // imprime CORTADO no fim da linha — onde está a quantidade do item. Comanda
        // cortada é defeito que este projeto já viu no papel. Na "mesma do cupom" ela é
        // PROIBIDA: a largura ali é a do cupom (config['papel_mm']), e oferecê-la aqui
        // criaria uma segunda fonte de verdade sobre a mesma bobina.
        var blocoPapel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        blocoPapel.Children.Add(new TextBlock
        {
            Text = "LARGURA DA BOBINA DESSA IMPRESSORA", FontSize = 12,
            FontWeight = FontWeights.Bold, Foreground = R("Marca"),
        });
        var opcoesPapel = AssistenteConfig.OpcoesPapel();
        var cboPapel = new ComboBox
        {
            ItemsSource = opcoesPapel,
            SelectedIndex = Math.Clamp(indicePapel, 0, Math.Max(0, opcoesPapel.Count - 1)),
            FontSize = 17, MinHeight = 52, Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 6, 0, 6),
        };
        blocoPapel.Children.Add(cboPapel);
        var txtPapel = new TextBlock
        {
            FontSize = 13, Foreground = R("TextoFraco"), TextWrapping = TextWrapping.Wrap,
        };
        blocoPapel.Children.Add(txtPapel);
        pilha.Children.Add(blocoPapel);

        void PintarPapel()
        {
            blocoPapel.Visibility = (cboImpressora.SelectedItem is Impressao.OpcaoComanda o
                                     && o.Impressora is not null)
                ? Visibility.Visible : Visibility.Collapsed;
            if (cboPapel.SelectedItem is not OpcaoPapel p) return;
            // Mesma frase do assistente, de propósito: é a mesma decisão, e duas
            // redações do mesmo aviso soam como dois avisos diferentes.
            var colunas = Nucleo.Kds.ColunasComanda(p.Colunas);
            txtPapel.Text = $"A comanda sai com {colunas} caracteres por linha."
                + (colunas < Nucleo.Kds.ColunasPadrao
                    ? " Bobina estreita: nome de produto e escolhas do combo quebram em mais linhas."
                    : "");
        }
        cboImpressora.SelectionChanged += (_, _) => PintarPapel();
        cboPapel.SelectionChanged += (_, _) => PintarPapel();
        PintarPapel();

        pilha.Children.Add(new TextBlock
        {
            Text = "Vale no próximo pedido — não precisa reiniciar o caixa. Para conferir no "
                 + "papel antes, toque no 🖨 de um pedido do quadro.",
            FontSize = 13, Foreground = R("TextoFraco"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 20),
        });

        var linha = new Grid();
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        var nao = Botao("Cancelar", false);
        var sim = Botao("Usar esta impressora", true);
        nao.Margin = new Thickness(0, 0, 6, 0);
        sim.Margin = new Thickness(6, 0, 0, 0);
        nao.Click += (_, _) => janela.Close();
        sim.Click += (_, _) =>
        {
            if (cboImpressora.SelectedItem is Impressao.OpcaoComanda o)
                resposta = (o, cboPapel.SelectedIndex < 0 ? 0 : cboPapel.SelectedIndex);
            janela.Close();
        };
        Grid.SetColumn(nao, 0); Grid.SetColumn(sim, 1);
        linha.Children.Add(nao); linha.Children.Add(sim);
        pilha.Children.Add(linha);

        janela.Content = Dialogo.Moldura(pilha);
        janela.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) janela.Close();
        };
        janela.ShowDialog();
        return resposta;
    }

    /// <summary>Mesmo alvo de toque dos outros diálogos da casa (58 px, fonte 17).</summary>
    private static Button Botao(string texto, bool destaque) => new()
    {
        Content = texto,
        Style = (Style)Application.Current.Resources[destaque ? "BotaoPrincipal" : "BotaoBase"],
        MinHeight = 58, FontSize = 17,
        Background = destaque ? R("RosaDegrade") : R("PainelAlto"),
    };
}
