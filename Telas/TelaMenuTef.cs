using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// A TELA do menu do TEF (só no caixa de homologação; a regra de quando ele existe está em
/// <see cref="MenuTef.Aparece"/>). Fina de propósito: quem diz o que entra na lista, com que
/// nome, e o que vai no quadro de estado é <see cref="MenuTef"/>, que a bateria prova; quem
/// fala com a maquininha é o MESMO provedor que a Configuração usa (Servicos.PGWebLib()).
///
/// Fica ABERTA entre uma operação e outra: são 58 passos, e reabrir menu a cada passo é como
/// se perde a sequência. Depois de cada operação o quadro e a frase da biblioteca se atualizam
/// sozinhos, que é o que o roteiro manda anotar.
/// </summary>
public static class TelaMenuTef
{
    private static Brush R(string chave) => (Brush)Application.Current.Resources[chave];

    /// <summary>
    /// Abre o menu. Devolve true quando o operador escolheu "Configuração do caixa": quem abre a
    /// Configuração é a tela de venda, com as travas dela (comanda aberta, TEF em andamento).
    /// </summary>
    public static bool Mostrar(Window dono)
    {
        var pediuConfiguracao = false;
        var janela = Dialogo.Base(dono, 640);
        var pilha = new StackPanel();
        pilha.Children.Add(PedirValor.Cabecalho(janela, MenuTef.Titulo));

        // ── quadro de estado ────────────────────────────────────────────────
        var quadro = new StackPanel();
        pilha.Children.Add(new Border
        {
            Background = R("Fundo"), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10),
            Child = quadro,
        });

        // ── a última frase da biblioteca ────────────────────────────────────
        var rotuloResposta = new TextBlock
        {
            Text = "Última resposta da biblioteca", FontSize = 12, Foreground = R("TextoFraco"),
            Margin = new Thickness(0, 0, 0, 2),
        };
        var resposta = new TextBlock
        {
            Text = MenuTef.SemResposta, FontSize = 16, FontWeight = FontWeights.Bold,
            Foreground = R("Texto"), TextWrapping = TextWrapping.Wrap,
        };
        var caixaResposta = new StackPanel();
        caixaResposta.Children.Add(rotuloResposta);
        caixaResposta.Children.Add(resposta);
        pilha.Children.Add(new Border
        {
            Background = R("PainelAlto"), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 0, 0, 10),
            Child = caixaResposta,
        });

        var status = new TextBlock
        {
            FontSize = 14, Foreground = R("TextoFraco"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10), MinHeight = 20,
        };
        pilha.Children.Add(status);

        // ── as operações ────────────────────────────────────────────────────
        var lista = new StackPanel();
        pilha.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 360, Content = lista, Margin = new Thickness(0, 0, 0, 12),
        });

        var fechar = PedirValor.Botao("Fechar", true);
        fechar.Click += (_, _) => janela.Close();
        pilha.Children.Add(fechar);

        var ocupado = false;

        void Dizer(string texto, string tom)
        {
            status.Text = texto;
            status.Foreground = R(tom);
        }

        void PintarResposta(ProvedorPGWebLib? pg)
            => resposta.Text = MenuTef.UltimaResposta(pg?.UltimaMensagem, pg?.UltimaMensagemEm);

        void PintarQuadro(IReadOnlyList<MenuTef.Linha> linhas)
        {
            quadro.Children.Clear();
            foreach (var l in linhas)
            {
                var linha = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                linha.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                linha.ColumnDefinitions.Add(new ColumnDefinition());
                var r = new TextBlock { Text = l.Rotulo, FontSize = 13, Foreground = R("TextoFraco") };
                var v = new TextBlock
                {
                    Text = l.Valor, FontSize = 14, Foreground = R("Texto"),
                    TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold,
                };
                Grid.SetColumn(r, 0); Grid.SetColumn(v, 1);
                linha.Children.Add(r); linha.Children.Add(v);
                quadro.Children.Add(linha);
            }
        }

        void Travar(bool travado)
        {
            ocupado = travado;
            foreach (var b in lista.Children.OfType<Button>()) b.IsEnabled = !travado;
            fechar.IsEnabled = !travado;
        }

        // Uma operação escolhida no menu: chama o provedor e conta o que voltou, com a frase da
        // biblioteca em primeiro lugar (é ela que o roteiro cobra em quase todo passo).
        async void Rodar(MenuTef.Item item, byte oper)
        {
            if (ocupado) return;
            if (Servicos.PGWebLib() is not { } pg) { SemBiblioteca(Dizer); return; }
            Travar(true);
            Dizer(item.Rotulo + ": responda o que a maquininha pedir.", "TextoFraco");
            try
            {
                var d = await pg.OperacaoDoMenuAsync(oper, item.Rotulo.ToLowerInvariant(), CancellationToken.None);
                PintarResposta(pg);
                Dizer(d.Pago
                        ? item.Rotulo + " concluída." + (d.Motivo is { Length: > 0 } m ? " " + m : "")
                        : item.Rotulo + " não concluída. " + (d.Motivo ?? "A maquininha não explicou o motivo."),
                    d.Pago ? "Ok" : "Erro");
            }
            catch (Exception ex)
            {
                Dizer("Não consegui falar com a maquininha. " + ex.Message, "Erro");
            }
            finally { Travar(false); }
            await AtualizarAsync(quieto: true);
        }

        void Desenhar(IReadOnlyList<MenuTef.Item> itens)
        {
            lista.Children.Clear();
            foreach (var item in itens)
            {
                var b = new Button
                {
                    Style = (Style)Application.Current.Resources["BotaoBase"],
                    MinHeight = MenuTef.AlturaItem, FontSize = 16,
                    Margin = new Thickness(0, 0, 0, 6),
                    Padding = new Thickness(14, 0, 14, 0),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };
                var linha = new Grid();
                linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                linha.ColumnDefinitions.Add(new ColumnDefinition());
                linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var icone = new TextBlock
                {
                    Text = item.Icone, FontSize = 16, Width = 30,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var nome = new TextBlock
                {
                    Text = item.Rotulo, VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    // Item que não roda aqui fica apagado: continua na lista (o roteiro cita o
                    // nome dele) mas não se parece com botão que resolve.
                    Foreground = item.Roda ? R("Texto") : R("TextoFraco"),
                };
                // Como a BIBLIOTECA chama a operação, à direita e em letra pequena: o roteiro
                // fala com as palavras dela.
                var detalhe = new TextBlock
                {
                    Text = item.Detalhe ?? "", FontSize = 12, Foreground = R("TextoFraco"),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
                };
                Grid.SetColumn(icone, 0); Grid.SetColumn(nome, 1); Grid.SetColumn(detalhe, 2);
                linha.Children.Add(icone); linha.Children.Add(nome); linha.Children.Add(detalhe);
                b.Content = linha;
                AutomationProperties.SetName(b, item.Rotulo);

                if (item.Acao == MenuTef.AcaoConfiguracao)
                    b.Click += (_, _) => { pediuConfiguracao = true; janela.Close(); };
                else if (!item.Roda)
                    b.Click += (_, _) => Dizer(item.Onde!, "TextoFraco");
                else if (MenuTef.OperacaoDaAcao(item.Acao) is { } oper)
                    b.Click += (_, _) => Rodar(item, oper);

                lista.Children.Add(b);
            }
        }

        // Lê da biblioteca o que ela oferece e como o terminal está. `quieto` = depois de uma
        // operação, quando o status já está contando outra coisa.
        async Task AtualizarAsync(bool quieto = false)
        {
            if (Servicos.PGWebLib() is not { } pg)
            {
                PintarQuadro(new[] { new MenuTef.Linha("Maquininha", "não é o PayGo pela biblioteca") });
                Desenhar(new[] { new MenuTef.Item("Configuração do caixa", "🛠", MenuTef.AcaoConfiguracao) });
                SemBiblioteca(Dizer);
                return;
            }
            if (!quieto) Dizer("Perguntando à biblioteca o que este terminal faz.", "TextoFraco");
            var (retVenda, deVenda) = await pg.OperacoesAsync(PW.OPERACOES_DE_VENDA, CancellationToken.None);
            var (_, todas) = await pg.OperacoesAsync(PW.OPERACOES_TODAS, CancellationToken.None);
            string? pastaDll;
            using (var cx = Banco.Abrir()) pastaDll = ConfigPGWebLib.PastaDll(c => Vendas.Config(cx, c));
            PintarQuadro(MenuTef.Estado(pg.Opcoes, pg.PastaTrabalho, pastaDll,
                MenuTef.Terminal(retVenda, deVenda.Count)));
            Desenhar(MenuTef.Itens(todas));
            PintarResposta(pg);
            if (!quieto)
                Dizer(MenuTef.ListouOperacoes(todas)
                        ? "Escolha o passo do roteiro."
                        : "A biblioteca não listou as operações deste terminal. Ficaram as duas de sempre.",
                    "TextoFraco");
        }

        janela.KeyDown += (_, e) => { if (e.Key == Key.Escape && !ocupado) janela.Close(); };
        janela.Content = Dialogo.Moldura(pilha);
        janela.Loaded += async (_, _) => await AtualizarAsync();
        janela.ShowDialog();
        return pediuConfiguracao;
    }

    private static void SemBiblioteca(Action<string, string> dizer)
        => dizer("A maquininha deste caixa não está no PayGo pela biblioteca. Abra a Configuração do caixa e escolha PayGo (biblioteca).", "Erro");
}
