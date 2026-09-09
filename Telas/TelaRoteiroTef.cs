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
    /// <summary>
    /// Uma cor do tema, e NUNCA nulo.
    ///
    /// ⚠️ ISTO CUSTOU A TARDE (09/09/2026). Eu usei as chaves "Sucesso", "Aviso" e
    /// "Primaria", que NAO EXISTEM neste tema. `Resources["Sucesso"]` devolve null, o
    /// TextBlock fica com Foreground nulo, e o texto some sem erro nenhum: a tela
    /// desenha o fundo da etiqueta e o texto invisivel dentro. O dono viu retangulos
    /// cinzas onde deviam estar o titulo, o valor e o REQNUM, e as etiquetas nunca
    /// apareceram desde a primeira versao.
    ///
    /// Chave errada agora cai no texto normal, que e visivel. E TestesCoresDoRoteiro
    /// le este arquivo e reprova qualquer chave que o tema nao tenha, para o proximo
    /// erro de digitacao nao virar tela em branco de novo.
    /// </summary>
    private static Brush R(string chave)
        => Application.Current.Resources[chave] as Brush
           ?? Application.Current.Resources["Texto"] as Brush
           ?? Brushes.Black;

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
                Text = "⚠ " + falta, FontSize = 13, Foreground = R("Amarelo"),
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

    /// <summary>
    /// Este passo se cumpre fazendo uma VENDA no caixa?
    ///
    /// O roteiro tem passos de venda (com ou sem valor definido), passos de menu
    /// administrativo, de relatorio e de instalacao. So os de venda ganham o botao que
    /// abre a comanda: botao que nao leva a lugar nenhum ensina a ignorar botao.
    /// </summary>
    private static bool EhDeVenda(PassoTef p)
    {
        if (RoteiroTef.ValorCent(p) is not null) return true;
        var t = (p.Titulo + " " + p.OQueFazer).ToLowerInvariant();
        if (t.Contains("menu administrativo") || t.Contains("relatório") || t.Contains("relatorio")
            || t.Contains("instalação") || t.Contains("instalacao") || t.Contains("manutenção")
            || t.Contains("manutencao") || t.Contains("teste de comunicação")) return false;
        return t.Contains("venda") || t.Contains("cancelamento") || t.Contains("abre a venda");
    }

    /// <summary>Um passo na lista: o que é, quanto vale, e o que já foi anotado.</summary>
    private static Border Cartao(LinhaDoPlacar l, Window janela, Action<PassoTef> escolher, Action? redesenhar)
    {
        var p = l.Passo;
        var corpo = new StackPanel();

        // ✓ NO TITULO, e nao so uma etiqueta pequena embaixo. O dono anotou o passo 1
        // e disse "nem fica marcado como feito": o sinal existia, mas discreto demais
        // para quem esta correndo 39 passos.
        var titulo = new TextBlock
        {
            Text = (l.Ok ? "✓ " : l.Tentado ? "✗ " : "") + $"{p.Numero}. {p.Titulo}",
            FontSize = 15, FontWeight = FontWeights.Bold,
            Foreground = l.Ok ? R("Ok") : R("Texto"), TextWrapping = TextWrapping.Wrap,
        };
        corpo.Children.Add(titulo);

        // O VALOR EXATO em destaque: é o que o roteiro cobra e o que se digita errado.
        var etiquetas = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        if (p.Valor.Length > 0)
            etiquetas.Children.Add(Etiqueta("R$ " + p.Valor, R("Texto")));
        if (p.Obrigatoriedade == "OPCIONAL")
            etiquetas.Children.Add(Etiqueta("opcional", R("TextoFraco")));
        if (l.Feito is not null)
            etiquetas.Children.Add(Etiqueta(
                l.Feito.Resultado + (string.IsNullOrWhiteSpace(l.Feito.Reqnum) ? " (sem REQNUM)" : " · " + l.Feito.Reqnum),
                l.Ok ? R("Ok") : R("Amarelo")));
        if (etiquetas.Children.Count > 0) corpo.Children.Add(etiquetas);

        corpo.Children.Add(new TextBlock
        {
            Text = p.OQueFazer, FontSize = 13, Foreground = R("TextoFraco"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });

        var botoes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        // TODO PASSO DE VENDA GANHA O BOTAO (09/09/2026, pedido do dono: "nao tem como
        // criar cada venda ao lado do passo a passo?"). Antes so apareciam os que o
        // roteiro traz com valor exato, e passo de venda SEM valor definido (o 3, por
        // exemplo, e "venda de qualquer valor") ficava sem botao. Ai o dono vendia pelo
        // caminho normal e o numero nao vinha amarrado ao passo, que foi como o REQNUM
        // do passo 3 acabou carimbado no passo 2.
        if (EhDeVenda(p))
        {
            var temValor = RoteiroTef.ValorCent(p) is not null;
            var executar = new Button
            {
                Content = temValor ? "Cobrar este valor" : "Criar a venda deste passo",
                Style = (Style)Application.Current.Resources["BotaoBase"],
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

            // CONTRA-PROVA (ideia do dono, 09/09/2026). Ele digita o que LEU na tela de
            // aprovação; o caixa compara com o que registrou. Oferecer o número e
            // perguntar "é este?" convidava a dizer sim sem conferir, e foi assim que
            // quatro passos ficaram com o mesmo REQNUM e um passo ficou com o de outro.
            var doSistema = PlacarHomologacao.ReqnumParaOferecer(DateTime.Now,
                PlacarHomologacao.Anotados().Values
                    .Where(f => f.Numero != p.Numero)
                    .Select(f => f.Reqnum)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!.Trim())
                    .ToHashSet(StringComparer.Ordinal),
                p.Numero);

            var digitado = PedirReqnum(janela, p.Numero);
            string? req = null;
            var conferencia = PlacarHomologacao.Conferir(digitado, doSistema);
            switch (conferencia)
            {
                case PlacarHomologacao.Conferencia.Confere:
                case PlacarHomologacao.Conferencia.SemComparacao:
                    req = PlacarHomologacao.Normalizar(digitado);
                    break;
                case PlacarHomologacao.Conferencia.Difere:
                    // O que o operador LEU manda: ele estava na frente do pinpad. Mas a
                    // divergência não pode passar calada, porque ela quer dizer que
                    // alguma coisa está errada, e é agora que dá para descobrir.
                    var usar = Dialogo.Confirmar(janela, $"Passo {p.Numero}: os números não batem",
                        $"Você digitou {PlacarHomologacao.Normalizar(digitado)}."
                        + Environment.NewLine + $"O caixa registrou {doSistema}."
                        + Environment.NewLine + Environment.NewLine
                        + "Confira na tela de aprovação. Qual vale?",
                        "O que eu digitei", "O do caixa");
                    req = usar ? PlacarHomologacao.Normalizar(digitado) : doSistema;
                    break;
                case PlacarHomologacao.Conferencia.NadaDigitado:
                    req = null;   // anota o resultado, sem número
                    break;
            }

            PlacarHomologacao.Anotar(p.Numero,
                ok ? PlacarHomologacao.Aprovado : PlacarHomologacao.Recusado, req);
            redesenhar?.Invoke();

            // O RESULTADO DA CONFERENCIA, DITO (09/09/2026). O dono anotou o passo 1,
            // digitou o numero e nao recebeu nada: "nao da resultado positivo nem
            // negativo, nem que ta certo nem errado". Contra-prova que nao diz o que
            // conferiu nao e contra-prova, e so mais uma caixa para fechar.
            var marca = ok ? "✓" : "✗";
            var estado = ok ? "aprovado" : "não aprovado";
            var sobreNumero = req is null
                ? "Sem número: este passo não gerou transação."
                : conferencia == PlacarHomologacao.Conferencia.Confere
                    ? $"Número {req} conferido: bate com o que o caixa registrou."
                    : conferencia == PlacarHomologacao.Conferencia.SemComparacao
                        ? $"Número {req} gravado. O caixa não tinha número desta operação para conferir."
                        : $"Número {req} gravado, mas ele NÃO batia com o do caixa. Confira antes de entregar.";
            Dialogo.Avisar(janela, $"{marca} Passo {p.Numero} {estado}", sobreNumero,
                ok && conferencia != PlacarHomologacao.Conferencia.Difere ? "ok" : "erro");
        };
        botoes.Children.Add(anotar);

        corpo.Children.Add(botoes);

        return new Border
        {
            Background = R("PainelAlto"),
            // Faixa na lateral: passo feito se enxerga descendo a lista sem ler nada.
            BorderBrush = l.Ok ? R("Ok") : l.Tentado ? R("Amarelo") : R("PainelAlto"),
            BorderThickness = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = corpo,
        };
    }

    /// <summary>
    /// Pede o REQNUM que o operador leu na tela de aprovação.
    ///
    /// Campo livre de propósito: se o caixa preenchesse sozinho, não seria
    /// contra-prova nenhuma, seria a mesma resposta duas vezes.
    /// </summary>
    private static string? PedirReqnum(Window dono, int passo)
    {
        var janela = Dialogo.Base(dono, 460);
        var pilha = new StackPanel();
        pilha.Children.Add(PedirValor.Cabecalho(janela, $"Passo {passo}: confira o número"));
        pilha.Children.Add(new TextBlock
        {
            Text = "Digite o " + RoteiroTef.RetornoExigido + " que apareceu na tela de aprovação. "
                 + "Deixe em branco se este passo não gerou transação.",
            FontSize = 13, Foreground = R("TextoFraco"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        var caixa = new TextBox
        {
            FontSize = 22, MinHeight = 48, Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 10),
        };
        pilha.Children.Add(caixa);

        string? resposta = null;
        var confirmar = new Button
        {
            Content = "Confirmar", Style = (Style)Application.Current.Resources["BotaoBase"],
            MinHeight = 46, FontSize = 15, Margin = new Thickness(0, 0, 0, 6),
        };
        confirmar.Click += (_, _) => { resposta = caixa.Text; janela.Close(); };
        pilha.Children.Add(confirmar);

        var semNumero = new Button
        {
            Content = "Este passo não gerou transação",
            Style = (Style)Application.Current.Resources["BotaoBase"], MinHeight = 46, FontSize = 14,
        };
        semNumero.Click += (_, _) => { resposta = null; janela.Close(); };
        pilha.Children.Add(semNumero);

        janela.Content = new Border { Padding = new Thickness(16), Child = pilha };
        caixa.Loaded += (_, _) => caixa.Focus();
        caixa.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { resposta = caixa.Text; janela.Close(); } };
        janela.ShowDialog();
        return resposta;
    }

    private static Border Etiqueta(string texto, Brush cor) => new()
    {
        Background = R("Fundo"), CornerRadius = new CornerRadius(8),
        Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0),
        Child = new TextBlock { Text = texto, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = cor },
    };
}
