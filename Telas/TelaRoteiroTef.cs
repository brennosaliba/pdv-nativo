using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// O ROTEIRO DE HOMOLOGAÇÃO, PASSO A PASSO, DENTRO DO CAIXA.
///
/// O dono, 09/09/2026, logo depois da primeira transação aprovada no sandbox:
/// "cria o menu tef de homologação com cada passo, cada valor".
///
/// O que ele estava fazendo até aqui: lendo o passo num PDF, digitando o valor
/// exato à mão na tela de venda, e depois caçando o REQNUM no log da biblioteca
/// para preencher a planilha. São 39 passos obrigatórios. Digitar
/// R$ 1.000,01 trinta e nove vezes é errar pelo menos uma, e passo com centavo
/// errado volta inteiro.
///
/// Esta tela não fala com a maquininha. Ela mostra o que fazer, cobra o valor
/// certo pela tela de venda de sempre, e anota o desfecho com o REQNUM. Quem
/// executa continua sendo o mesmo caminho que a loja usa todo dia: roteiro que
/// testa um caminho especial não prova nada sobre o caminho de verdade.
///
/// ⚠️ NÃO existe botão "aprovar". Quem diz se o passo passou é a PayGo, olhando
/// o comprovante. Aqui se anota o que aconteceu.
/// </summary>
public static class TelaRoteiroTef
{
    private static Brush R(string chave) => (Brush)Application.Current.Resources[chave];

    /// <summary>Onde o CSV da planilha é gravado quando o operador exporta.</summary>
    public static string CaminhoDaPlanilha =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     "homologacao-tef.csv");

    /// <summary>
    /// Abre o roteiro. Devolve o passo que o operador escolheu executar, ou `null`
    /// quando ele só olhou e fechou.
    /// </summary>
    public static PassoTef? Mostrar(Window dono)
    {
        PassoTef? escolhido = null;

        var janela = Dialogo.Base(dono, 760);
        var pilha = new StackPanel();
        pilha.Children.Add(PedirValor.Cabecalho(janela, "Roteiro de homologação do TEF"));

        // ⚠️ LE DO BANCO A CADA DESENHO, e nao uma vez ao abrir (09/09/2026).
        //
        // A primeira versao guardava a lista numa variavel na abertura. Anotar gravava
        // no banco, mas o placar continuava mostrando o retrato antigo E o botao de
        // exportar escrevia esse mesmo retrato. O dono anotou a venda de R$ 100.000,00,
        // nao viu mudar nada, e a planilha saiu com dado de agosto. Parecia que nao
        // salvava; salvava, e a tela e o arquivo e que mentiam.
        static IReadOnlyList<LinhaDoPlacar> Ler()
            => PlacarHomologacao.Montar(RoteiroTef.ParaBibliotecaWindows(), PlacarHomologacao.Anotados());

        var linhas = Ler();
        var (feitos, total) = PlacarHomologacao.Progresso(linhas);
        var proximo = PlacarHomologacao.Proximo(linhas);

        // ── o placar, e o que ele NÃO conta ─────────────────────────────────
        var resumo = new StackPanel();
        var placar = new TextBlock
        {
            Text = $"{feitos} de {total} passos obrigatórios",
            FontSize = 22, FontWeight = FontWeights.Bold, Foreground = R("Texto"),
        };
        resumo.Children.Add(placar);
        resumo.Children.Add(new TextBlock
        {
            Text = proximo is null
                ? "Todos os obrigatórios foram anotados. Confira a planilha antes de entregar."
                : $"Próximo: passo {proximo.Numero}. {proximo.Titulo}",
            FontSize = 14, Foreground = R("TextoFraco"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });
        if (PlacarHomologacao.DeOutraRodada() is > 0 and var outras)
            resumo.Children.Add(new TextBlock
            {
                Text = $"{outras} anotações de outra homologação (ControlPay) estão fora desta conta e fora da planilha.",
                FontSize = 13, Foreground = R("TextoFraco"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });
        if (PlacarHomologacao.AvisoDeReqnumRepetido(linhas) is { } repetido)
            resumo.Children.Add(new TextBlock
            {
                Text = "⚠ " + repetido, FontSize = 13, Foreground = R("Erro"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });
        if (PlacarHomologacao.AvisoDeReqnumFaltando(linhas) is { } falta)
            resumo.Children.Add(new TextBlock
            {
                Text = "⚠ " + falta, FontSize = 13, Foreground = R("Aviso"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });
        pilha.Children.Add(new Border
        {
            Background = R("PainelAlto"), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10),
            Child = resumo,
        });

        // ── a lista ─────────────────────────────────────────────────────────
        var lista = new StackPanel();
        Action? redesenhar = null;
        void Pintar()
        {
            lista.Children.Clear();
            foreach (var l in Ler()) lista.Children.Add(Cartao(l, janela, p => { escolhido = p; }, redesenhar));
        }
        redesenhar = () =>
        {
            Pintar();
            var (f2, t2) = PlacarHomologacao.Progresso(Ler());
            placar.Text = $"{f2} de {t2} passos obrigatórios";
        };
        Pintar();

        pilha.Children.Add(new ScrollViewer
        {
            Content = lista, MaxHeight = 460,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 10),
        });

        // ── exportar ────────────────────────────────────────────────────────
        var exportar = new Button
        {
            Content = "Gerar a planilha no Desktop", Style = (Style)Application.Current.Resources["BotaoBase"],
            MinHeight = 46, FontSize = 15, Margin = new Thickness(0, 0, 0, 6),
        };
        exportar.Click += (_, _) =>
        {
            try
            {
                // Do BANCO, nao do retrato: e o que o dono acabou de anotar que vai
                // para a planilha, e nao o estado de quando a tela abriu.
                File.WriteAllText(CaminhoDaPlanilha,
                    PlacarHomologacao.Csv(Ler()), System.Text.Encoding.UTF8);
                Dialogo.Avisar(janela, "Planilha gerada",
                    $"Salvei em {CaminhoDaPlanilha}.\nA coluna do {RoteiroTef.RetornoExigido} já vai preenchida.", "ok");
            }
            catch (Exception ex)
            {
                Dialogo.Avisar(janela, "Não consegui gravar",
                    $"O Windows respondeu: {ex.Message}", "erro");
            }
        };
        pilha.Children.Add(exportar);

        var fechar = new Button
        {
            Content = "Fechar", Style = (Style)Application.Current.Resources["BotaoBase"],
            MinHeight = 46, FontSize = 15,
        };
        fechar.Click += (_, _) => janela.Close();
        pilha.Children.Add(fechar);

        janela.Content = new Border { Padding = new Thickness(16), Child = pilha };
        janela.ShowDialog();
        return escolhido;
    }

    /// <summary>Um passo na lista: o que é, quanto vale, e o que já foi anotado.</summary>
    private static Border Cartao(LinhaDoPlacar l, Window janela, Action<PassoTef> escolher, Action? redesenhar)
    {
        var p = l.Passo;
        var corpo = new StackPanel();

        var titulo = new TextBlock
        {
            Text = $"{p.Numero}. {p.Titulo}", FontSize = 15, FontWeight = FontWeights.Bold,
            Foreground = R("Texto"), TextWrapping = TextWrapping.Wrap,
        };
        corpo.Children.Add(titulo);

        // O VALOR EXATO em destaque: é o que o roteiro cobra e o que se digita errado.
        var etiquetas = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        if (p.Valor.Length > 0)
            etiquetas.Children.Add(Etiqueta("R$ " + p.Valor, R("Primaria")));
        if (p.Obrigatoriedade == "OPCIONAL")
            etiquetas.Children.Add(Etiqueta("opcional", R("TextoFraco")));
        if (l.Feito is not null)
            etiquetas.Children.Add(Etiqueta(
                l.Feito.Resultado + (string.IsNullOrWhiteSpace(l.Feito.Reqnum) ? " (sem REQNUM)" : " · " + l.Feito.Reqnum),
                l.Ok ? R("Sucesso") : R("Aviso")));
        if (etiquetas.Children.Count > 0) corpo.Children.Add(etiquetas);

        corpo.Children.Add(new TextBlock
        {
            Text = p.OQueFazer, FontSize = 13, Foreground = R("TextoFraco"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });

        var botoes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        if (RoteiroTef.ValorCent(p) is not null)
        {
            var executar = new Button
            {
                Content = "Cobrar este valor", Style = (Style)Application.Current.Resources["BotaoBase"],
                MinHeight = 40, FontSize = 14, MinWidth = 170, Margin = new Thickness(0, 0, 8, 0),
            };
            executar.Click += (_, _) => { escolher(p); janela.Close(); };
            botoes.Children.Add(executar);
        }

        var conferir = new Button
        {
            Content = "O que conferir", Style = (Style)Application.Current.Resources["BotaoBase"],
            MinHeight = 40, FontSize = 14, MinWidth = 150, Margin = new Thickness(0, 0, 8, 0),
        };
        conferir.Click += (_, _) => Dialogo.Relatorio(janela, $"Passo {p.Numero}",
            p.OQueConferir.Length > 0 ? p.OQueConferir : "O roteiro não detalha a conferência deste passo.", null);
        botoes.Children.Add(conferir);

        var anotar = new Button
        {
            Content = "Anotar resultado", Style = (Style)Application.Current.Resources["BotaoBase"],
            MinHeight = 40, FontSize = 14, MinWidth = 160,
        };
        anotar.Click += (_, _) =>
        {
            // Sem botão "aprovar" sozinho: o operador diz o que a maquininha fez.
            var ok = Dialogo.Confirmar(janela, $"Passo {p.Numero}",
                "A maquininha aprovou este passo?", "Aprovou", "Não aprovou");

            // O REQNUM da ultima transacao entra AQUI, e nao no "Cobrar este valor"
            // apenas. Sem isto, quem vendia pelo caminho normal e anotava depois
            // gravava a linha com a coluna vazia, e a planilha ia para a PayGo sem o
            // unico dado que ela exige nela.
            //
            // OFERECIDO, nunca imposto: numero errado na planilha e pior do que
            // coluna vazia, e so quem estava na frente do pinpad sabe se aquela
            // transacao e a deste passo.
            string? req = null;
            // Os ja carimbados saem do banco na hora: REQNUM que ja esta em outro
            // passo e prova de que nao e deste, e nem chega a ser oferecido.
            var usados = PlacarHomologacao.Anotados().Values
                .Select(f => f.Reqnum)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim())
                .ToHashSet(StringComparer.Ordinal);
            if (PlacarHomologacao.ReqnumParaOferecer(DateTime.Now, usados) is { } candidato)
            {
                if (Dialogo.Confirmar(janela, $"Passo {p.Numero}",
                        $"A última transação do TEF foi a {candidato}.\nÉ a deste passo?",
                        "Sim, é esta", "Não"))
                    req = candidato;
            }

            PlacarHomologacao.Anotar(p.Numero,
                ok ? PlacarHomologacao.Aprovado : PlacarHomologacao.Recusado, req);
            // Redesenha na hora. Mandar "reabra o roteiro" e empurrar para o operador
            // o trabalho que a tela devia fazer, e foi o que fez o dono achar que a
            // anotacao nao tinha salvado.
            redesenhar?.Invoke();
        };
        botoes.Children.Add(anotar);

        corpo.Children.Add(botoes);

        return new Border
        {
            Background = l.Ok ? R("Fundo") : R("PainelAlto"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = corpo,
        };
    }

    private static Border Etiqueta(string texto, Brush cor) => new()
    {
        Background = R("Fundo"), CornerRadius = new CornerRadius(8),
        Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0),
        Child = new TextBlock { Text = texto, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = cor },
    };
}
