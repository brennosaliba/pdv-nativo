using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// Abertura do turno. O operador CONTA a gaveta e declara o valor.
///
/// O campo começa zerado de propósito: se o sistema sugerisse "o de sempre", o
/// operador confirmaria sem contar, e o fundo declarado deixaria de ser um fato
/// verificado. Como o fechamento faz esperado = fundo + vendas + suprimentos −
/// sangrias, um fundo errado contamina a conferência do dia inteiro e o operador
/// ganha um álibi permanente ("já abri com falta").
///
/// Digitação em centavos, da direita pra esquerda (igual maquininha): teclar 1-5-0-0
/// vira R$ 15,00. Ponto decimal em tela touch é fonte de erro de casa decimal.
/// </summary>
public partial class AberturaCaixa : UserControl
{
    private readonly Operador _operador;
    private long _centavos;

    public event Action<Sessao>? Abriu;
    public event Action? Saiu;

    public AberturaCaixa(Operador operador)
    {
        InitializeComponent();
        _operador = operador;
        TxtOperador.Text = operador.Nome;
        Teclado.Digitou += d =>
        {
            if (_centavos < 99_999_99) { _centavos = _centavos * 10 + (d[0] - '0'); Pintar(); }
        };
        Teclado.Apagou += () => { _centavos /= 10; Pintar(); };
        Teclado.Limpou += () => { _centavos = 0; Pintar(); };
        Loaded += (_, _) => { Focus(); Avisar(); };
    }

    private void Pintar()
    {
        TxtValor.Text = new Dinheiro(_centavos).Formatado();
        Aviso("");
        Avisar();
    }

    /// <summary>Aviso na caixa de destaque; texto vazio esconde a caixa inteira.</summary>
    private void Aviso(string texto)
    {
        TxtErro.Text = texto;
        CaixaAviso.Visibility = string.IsNullOrEmpty(texto) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Data operacional em formato de gente: 05/08/2026, não 2026-08-05.</summary>
    private static string DataBr(string businessDate)
        => DateTime.TryParse(businessDate, out var d) ? d.ToString("dd/MM/yyyy") : businessDate;

    /// <summary>
    /// Forma de pagamento em formato de gente: "Débito", não "debito". A chave
    /// crua vazava direto pra pergunta do fechamento ("Quanto deu debito...").
    /// </summary>
    private static string FormaBr(string forma) => forma switch
    {
        "dinheiro" => "Dinheiro",
        "debito" => "Débito",
        "credito" => "Crédito",
        "pix" => "PIX",
        _ => forma,
    };

    /// <summary>
    /// Se um turno ficou aberto (inclusive de outro dia), avisa E DÁ O CAMINHO.
    /// Sempre começa limpando: era daqui que o aviso "ficou aberto" continuava na tela
    /// DEPOIS de o caixa antigo ser fechado — o retorno cedo pulava a limpeza do texto.
    /// </summary>
    private void Avisar()
    {
        using var cx = Banco.Abrir();
        var aberta = Caixa.SessaoAberta(cx);
        BtnFecharAntigo.Visibility = Visibility.Collapsed;
        Aviso("");
        if (aberta is null) return;

        if (aberta.BusinessDate == Caixa.DiaOperacional())
        {
            Aviso($"O caixa de hoje já está aberto por {aberta.OperadorNome}. " +
                  "Esse caixa precisa fechar antes de você abrir o seu.");
            return;
        }

        Aviso($"O caixa de {DataBr(aberta.BusinessDate)} ficou aberto ({aberta.OperadorNome}). " +
              "Feche esse caixa antes de abrir o de hoje, senão as vendas dos dois dias se misturam.");
        BtnFecharAntigo.Content = $"Fechar o caixa de {DataBr(aberta.BusinessDate)}";
        BtnFecharAntigo.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// O fechamento estourou. A causa vem do Núcleo e pode ser técnica; o que o
    /// operador precisa saber é que o caixa NÃO fechou (segue aberto, a contagem
    /// não foi gravada) e que ele não resolve isso sozinho no balcão.
    /// </summary>
    private static void NaoFechou(Window dono, Exception ex)
        => Dialogo.Avisar(dono, "Caixa não fechou",
            ex.Message + "\n\nO caixa continua aberto. Anote os valores que você contou e tente de novo; " +
            "se continuar, chame o gerente.", "erro");

    /// <summary>
    /// Fecha o turno esquecido, aqui mesmo. É o MESMO fechamento cego da tela de venda:
    /// o operador conta a gaveta de agora (o dinheiro de ontem continua nela), declara
    /// forma a forma, e o sistema mostra a diferença só depois.
    /// </summary>
    private void FecharAntigo(object sender, RoutedEventArgs e)
    {
        var dono = Window.GetWindow(this)!;
        using var cx = Banco.Abrir();
        var antiga = Caixa.SessaoAberta(cx);
        if (antiga is null || antiga.BusinessDate == Caixa.DiaOperacional()) { Avisar(); return; }

        var contagem = new Dictionary<string, Dinheiro>();
        foreach (var f in Caixa.FormasContadas(cx, antiga))
        {
            var pergunta = f == "dinheiro"
                ? "Quanto tem em dinheiro na gaveta agora? O dinheiro daquele dia continua lá."
                : $"Quanto deu em {FormaBr(f)} no fechamento da maquininha daquele dia?";
            var v = PedirValor.Mostrar(dono, $"Fechamento de {DataBr(antiga.BusinessDate)}", pergunta);
            if (v is null) return;                 // desistiu: nada fechado
            contagem[f] = v.Value;
        }

        var tolerancia = new Dinheiro(200);
        try
        {
            Concluir(Caixa.Fechar(cx, antiga, contagem, _operador, tolerancia), antiga, null);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Justifique"))
        {
            // O texto do Núcleo já termina pedindo a descrição — repetir
            // "O que aconteceu?" aqui só fazia o operador ler a mesma coisa duas vezes.
            var just = PedirTexto.Mostrar(dono, "Diferença no caixa", ex.Message, "");
            if (string.IsNullOrWhiteSpace(just)) return;
            try { Concluir(Caixa.Fechar(cx, antiga, contagem, _operador, tolerancia, just), antiga, just); }
            catch (Exception e2) { NaoFechou(dono, e2); }
        }
        catch (Exception ex)
        {
            NaoFechou(dono, ex);
        }

        void Concluir(List<LinhaFechamento> linhas, Sessao sessao, string? justificativa)
        {
            var texto = string.Join("\n", linhas.Select(l =>
            {
                var dif = l.Situacao switch
                {
                    "confere" => "confere",
                    "sobra" => "SOBRA " + l.Diferenca.Abs.Formatado(),
                    _ => "FALTA " + l.Diferenca.Abs.Formatado(),
                };
                // "esperado", não "sistema": é o valor com que a contagem tem que
                // bater, e é assim que a divergência da abertura chama a mesma coisa.
                return $"{FormaBr(l.Forma),-9} contou {l.Declarado.Formatado(),11}  esperado {l.Apurado.Formatado(),11}  {dif}";
            }));
            // Venda de teste fica fora dos totais — mas aparece rotulada, aqui também.
            if (Caixa.ResumoDeTeste(cx, sessao) is string teste) texto += "\n\n" + teste;
            Dialogo.Relatorio(dono, $"Caixa de {DataBr(sessao.BusinessDate)} fechado", texto,
                justificativa is null ? null : $"Justificativa: {justificativa}");
            Avisar();   // some o botão; a abertura de hoje fica livre
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var d = e.Key is >= Key.D0 and <= Key.D9 ? e.Key - Key.D0
              : e.Key is >= Key.NumPad0 and <= Key.NumPad9 ? e.Key - Key.NumPad0 : -1;
        if (d >= 0 && _centavos < 99_999_99) { _centavos = _centavos * 10 + d; Pintar(); e.Handled = true; }
        else if (e.Key == Key.Back) { _centavos /= 10; Pintar(); e.Handled = true; }
        else if (e.Key == Key.Enter) { Abrir(this, new RoutedEventArgs()); e.Handled = true; }
    }

    private void Abrir(object sender, RoutedEventArgs e)
    {
        try
        {
            using var cx = Banco.Abrir();

            // Caixa de OUTRO DIA preso aberto: o "Abrir" confronta em vez de só falhar.
            // O caminho normal é fechar aquele turno (contagem cega de sempre); pular a
            // contagem existe, mas custa o PIN do gerente e fica auditado com nome —
            // porque pular contagem é exatamente onde quebra de caixa se esconde.
            var presa = Caixa.SessaoAberta(cx);
            if (presa is not null && presa.BusinessDate != Caixa.DiaOperacional())
            {
                var dono = Window.GetWindow(this)!;
                if (Dialogo.Confirmar(dono, $"Caixa de {DataBr(presa.BusinessDate)} em aberto",
                        $"O caixa de {DataBr(presa.BusinessDate)} ficou aberto ({presa.OperadorNome}). " +
                        "Se abrir o de hoje por cima, as vendas dos dois dias se misturam.\n\n" +
                        "O certo é fechar aquele caixa agora, contando a gaveta. Fechar sem contar " +
                        "precisa do PIN do gerente e fica registrado no nome dele.",
                        // Rótulos curtos de propósito: o botão do diálogo é fixo em
                        // ~200px e texto de botão CORTA (não quebra linha).
                        "Contar e fechar", "Fechar sem contar"))
                {
                    FecharAntigo(sender, e);
                    return;
                }

                // Pular o fechamento de um caixa antigo SEMPRE exige o PIN do gerente
                // (o modo de homologacao, que dispensava, saiu com a operacao no ar).
                Operador? sup;
                {
                    // "sem contar a gaveta" é o que de fato acontece aqui — o caixa
                    // FECHA; o que se pula é a contagem. O texto antigo ("pular o
                    // fechamento") descrevia outra coisa.
                    var pin = PedirSenha.Mostrar(dono, "Autorização do gerente",
                        $"PIN do gerente para fechar o caixa de {DataBr(presa.BusinessDate)} sem contar a gaveta");
                    if (pin is null) return;
                    sup = Operadores.AutorizarSupervisor(cx, pin);
                }
                if (sup is null)
                {
                    Dialogo.Avisar(dono, "PIN não confere",
                        "Esse PIN não é de gerente, ou saiu errado. Peça para o gerente digitar de novo.", "erro");
                    return;
                }
                Caixa.FecharSemConferencia(cx, presa, _operador, sup);
                Avisar();   // some o aviso do caixa preso; a abertura segue normal
            }

            // A abertura tem que bater com o fechamento anterior: fechou com 300 e
            // ninguém sangrou, abre com 300. Divergência não BLOQUEIA (a loja precisa
            // abrir), mas exige confirmação consciente e fica auditada com os dois
            // valores — é o buraco fora do expediente, que a conferência do turno
            // sozinha nunca enxerga.
            var esperado = Caixa.FundoEsperado(cx);
            if (esperado is { } exp && exp.Centavos != _centavos)
            {
                var dif = new Dinheiro(_centavos - exp.Centavos);
                // Tom de CONFERÊNCIA, nunca de acusação: quem está contando agora
                // muitas vezes nem estava aqui quando a diferença nasceu (turno
                // anterior, sangria fora de hora, troco reposto). O controle é o
                // registro com os dois valores — não a bronca na tela.
                var texto = dif.Centavos > 0
                    ? $"{dif.Formatado()} a mais"
                    : $"{dif.Abs.Formatado()} a menos";
                // Sem sujeito na frase da contagem ("a contagem de agora deu"), e sem
                // promessa de que ninguém vai ser cobrado — isso o sistema não decide.
                // O que ele garante é o registro dos dois valores; é só isso que a tela diz.
                if (!Dialogo.Confirmar(Window.GetWindow(this)!, "Conferência do fundo de troco",
                        $"O último fechamento deixou {exp.Formatado()} na gaveta e a contagem " +
                        $"de agora deu {new Dinheiro(_centavos).Formatado()} — {texto}.\n\n" +
                        "Pode ser troco reposto, sangria de ontem ou erro na contagem. " +
                        "Vale conferir uma vez; os dois valores ficam anotados de qualquer jeito.",
                        "Registrar e abrir", "Recontar"))
                    return;
                Caixa.Auditar(cx, null, "abertura_divergente", _operador.Id, null,
                    $"esperado={exp.Formatado()} contado={new Dinheiro(_centavos).Formatado()}");
            }

            var s = Caixa.Abrir(cx, _operador, new Dinheiro(_centavos));
            Abriu?.Invoke(s);
        }
        catch (Exception ex)
        {
            Aviso(ex.Message);
        }
    }

    private void Sair(object sender, RoutedEventArgs e) => Saiu?.Invoke();
}
