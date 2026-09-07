using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// VALOR DE TESTE NA COMANDA (07/09/2026) — o buraco do passo 21 do roteiro do TEF.
///
/// O passo 21 manda "realizar uma venda de R$ 12.345,67, utilizar C6PAY". A tela de venda
/// só sabia montar comanda por CARDÁPIO: o item entra pelo preço de tabela
/// (Telas/Venda.xaml.cs, Adicionar) e a quantidade só anda de um em um (o "+" da linha).
/// No cardápio desta máquina os preços vão de R$ 0,25 a R$ 153,00 e quase todos andam de
/// 25 em 25 centavos, então R$ 12.345,67 não sai de soma nenhuma que um operador consiga
/// fazer: a conta mais curta que existe precisa de 87 itens, e ela morre no dia em que o
/// ERP mudar um preço. Pelo lado do pagamento também não dava: o cartão nunca passa do que
/// falta na comanda (Telas/Pagamento.xaml.cs, "cartão não passa do restante").
///
/// O conserto é um botão na comanda que pede o valor no MESMO teclado da sangria
/// (Telas/PedirValor.cs, dígitos em centavos) e põe a linha na comanda. Daí para a frente
/// nada muda: Finalizar, pagamento, maquininha, confirmação e recibo são os de sempre — é
/// isso que faz a evidência do roteiro valer.
///
/// O botão SÓ EXISTE com o modo de homologação ligado (config `homologacao` = 1). É o mesmo
/// interruptor que já mantém a venda de teste fora da nuvem e fora do fechamento do caixa.
/// Na loja ele não é desenhado, e mesmo se alguém disparar o clique o lançamento é recusado:
/// preço digitado no balcão não é recurso, é furo de caixa.
///
/// O que se prova aqui: o texto e o estado inicial no XAML; a loja SEM o botão (e o cinto do
/// handler); o caixa de homologação com o botão; digitar 1-2-3-4-5-6-7 virar R$ 12.345,67 na
/// linha e no TOTAL; desistir não lançar nada; dois valores serem duas linhas; o valor exato
/// chegar ao pagamento (é ele que vai para o cartão); e a auditoria guardar o lançamento.
/// </summary>
public static class TestesValorDeTeste
{
    private const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>O valor do passo 21, em centavos.</summary>
    private const long Passo21 = 1234567;

    public static void Rodar(Action<bool, string> checar)
    {
        Aritmetica(checar);
        Fonte(checar);
        Tela(checar);
    }

    // ── 1. por que o cardápio não fecha o valor ────────────────────────────
    // Não é opinião: os preços ativos desta loja são múltiplos de 25 centavos (fora quatro
    // que terminam em 99). Soma de múltiplos de 25 é múltiplo de 25, e 1.234.567 não é.
    private static void Aritmetica(Action<bool, string> checar)
    {
        var precos = new long[] { 25, 200, 500, 600, 700, 750, 900, 1050, 1150, 1250, 1450,
                                  1550, 1650, 1750, 1850, 2050, 2250, 5700, 8400, 11000, 15300 };
        checar(precos.All(p => p % 25 == 0) && Passo21 % 25 != 0,
            "cardápio: preços de 25 em 25 centavos não somam R$ 12.345,67 de jeito nenhum (1.234.567 não é múltiplo de 25)");
        checar(Passo21 > precos.Max() * 80,
            "e mesmo com os quatro preços quebrados a conta mais curta passa de 80 itens: não é caminho de operador");
    }

    // ── 2. o botão no XAML: nasce escondido e fala português ────────────────
    private static void Fonte(Action<bool, string> checar)
    {
        var xaml = Arquivo("Telas", "Venda.xaml");
        if (xaml is null) { checar(false, "achei Telas/Venda.xaml"); return; }

        var bloco = Trecho(xaml, "x:Name=\"BtnValorLivre\"", "</Button>");
        checar(bloco.Length > 0, "a comanda tem o botão do valor de teste (BtnValorLivre)");
        checar(bloco.Contains("Click=\"LancarValorDeTeste\""), "o botão chama LancarValorDeTeste");
        checar(bloco.Contains("Visibility=\"Collapsed\""),
            "o botão NASCE escondido no XAML: quem não ligar a homologação nunca o vê");
        checar(bloco.Contains("AutomationProperties.Name=\"Valor do teste\""),
            "o botão tem nome de automação (leitor de tela e teste)");

        var cs = Arquivo("Telas", "Venda.xaml.cs");
        if (cs is null) { checar(false, "achei Telas/Venda.xaml.cs"); return; }
        var handler = Trecho(cs, "private void LancarValorDeTeste", "private void AdicionarValorDeTeste");
        checar(handler.Contains("if (!_homologacao) return;"),
            "o handler recusa fora da homologação (não basta esconder o botão)");

        // texto de tela: o dono lê tudo e reprova travessão
        foreach (var (nome, texto) in new[]
                 {
                     ("rótulo do botão", "Valor do teste"),
                     ("título do teclado", "Valor do teste"),
                     ("pergunta do teclado", "Quanto o roteiro pede nesta venda"),
                     ("nome da linha", "Venda de teste"),
                 })
        {
            checar(cs.Contains(texto) || xaml.Contains(texto), $"o texto de tela existe: {nome}");
            checar(!texto.Contains('—') && !texto.Contains('–'), $"sem travessão no {nome}");
        }
    }

    // ── 3. a tela, em STA, com banco próprio ───────────────────────────────
    private static void Tela(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-valor-teste-{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        Exception? erro = null;
        try
        {
            Banco.Migrar(arquivo);
            Semear(arquivo);
            try { HostWpf.Executar(() => Passos(checar)); }
            catch (Exception ex) { erro = ex; }
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
        checar(erro is null, "tela: a venda subiu e os passos rodaram (" + (erro?.ToString() ?? "ok") + ")");
    }

    private static void Semear(string arquivo)
    {
        using var cx = Banco.Abrir(arquivo);
        var agora = DateTime.Now.ToString("o");
        cx.Execute("""
            INSERT INTO terminal (id, terminal_uuid, loja_id, loja_nome, cnpj, serie_nfce, ambiente, api_base, criado_em)
            VALUES (1, 'term-valor', 'loja-1', 'Loja Teste', '00000000000000', 1, 2, 'http://127.0.0.1:9', @a)
            """, new { a = agora });
        cx.Execute("INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,atualizado) VALUES ('op-vt','Tela','x','y','operador',@a)",
            new { a = agora });
        cx.Execute("""
            INSERT INTO caixa_sessao (id,business_date,operador_id,operador_nome,abertura_em,fundo_troco_cent)
            VALUES ('sessao-vt',@d,'op-vt','Tela',@a,0)
            """, new { d = Caixa.DiaOperacional(), a = agora });
        // Um donut de R$ 9,00: é o cardápio inteiro que o operador tem à mão, e ele
        // não fecha R$ 12.345,67 nem com paciência.
        cx.Execute("""
            INSERT INTO produto (id, plu, nome, categoria, preco_cent, unidade, ativo, atualizado, csosn)
            VALUES ('d-ninho','4','DONUT NINHO','Donuts',900,'UN',1,@a,'102')
            """, new { a = agora });
        Vendas.GravarConfig(cx, "modo_fiscal", "recibo");
    }

    private static void Passos(Action<bool, string> checar)
    {
        var host = new Window
        {
            Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0,
        };
        host.Show();
        var op = new Operador("op-vt", "Tela", "operador");
        var sessao = new Sessao("sessao-vt", Caixa.DiaOperacional(), op.Id, op.Nome, DateTime.Now, Dinheiro.Zero);

        // ── (a) A LOJA: homologação desligada ───────────────────────────────
        Config("homologacao", "0");
        var loja = new Pdv.Telas.Venda(op, sessao);
        host.Content = loja;
        host.UpdateLayout();
        var botaoLoja = Botao(loja, "Valor do teste");
        checar(botaoLoja is not null && botaoLoja.Visibility != Visibility.Visible,
            "loja (homologacao=0): o botão do valor de teste existe na tela, mas fica escondido");

        // e o cinto: mesmo disparando o clique na marra, nada entra na comanda
        var comandaLoja = Campo<List<Pdv.Telas.ItemComanda>>(loja, "_comanda");
        var abriu = false;
        // A vigia fica armada só durante o clique: se um teclado abrisse, o Clicar
        // ficaria preso no ShowDialog para sempre. Desarmada logo depois — vigia
        // esquecida ligada rouba o PRÓXIMO diálogo e o teste passa a medir outra coisa.
        var vigia = QuandoAbrir(host, d => { abriu = true; d.Close(); });
        if (botaoLoja is not null) Clicar(botaoLoja);
        vigia.Stop();
        checar(!abriu && comandaLoja.Count == 0,
            "loja: disparar o clique na marra não abre o teclado nem põe linha na comanda");
        host.Content = null;

        // ── (b) O CAIXA DE HOMOLOGAÇÃO ─────────────────────────────────────
        Config("homologacao", "1");
        var tela = new Pdv.Telas.Venda(op, sessao);
        host.Content = tela;
        host.UpdateLayout();
        Definir(tela, "_rascunhoOferecido", true);   // igual a TestesCombos: liga a gravação do rascunho
        var comanda = Campo<List<Pdv.Telas.ItemComanda>>(tela, "_comanda");
        var botao = Botao(tela, "Valor do teste");
        checar(botao is not null && botao.Visibility == Visibility.Visible,
            "homologação: o botão do valor de teste aparece na comanda");
        if (botao is null) { host.Close(); return; }

        // desistir não lança nada
        QuandoAbrir(host, d => Clicar(BotaoDe(d, "Cancelar")));
        Clicar(botao);
        checar(comanda.Count == 0, "desistir no teclado não põe linha na comanda");

        // 1-2-3-4-5-6-7 no teclado da casa = R$ 12.345,67 (dígitos em centavos)
        QuandoAbrir(host, d =>
        {
            foreach (var t in "1234567") Clicar(BotaoDe(d, t.ToString()));
            Clicar(BotaoDe(d, "Confirmar"));
        });
        Clicar(botao);
        checar(comanda.Count == 1 && comanda[0].Produto.Preco.Centavos == Passo21
               && comanda[0].Qtd.Milesimos == 1000,
            $"passo 21: digitar 1234567 põe UMA linha de R$ 12.345,67 na comanda (veio {comanda.Count} linha(s), "
            + $"{(comanda.Count == 1 ? comanda[0].Produto.Preco.Formatado() : "-")})");
        checar(comanda.Count == 1 && comanda[0].Produto.Nome == "Venda de teste",
            "a linha se chama 'Venda de teste': ninguém confunde com produto do cardápio");
        checar(comanda.Count == 1 && comanda[0].DescontoCent == 0 && !comanda[0].EhCombo,
            "a linha não pega promoção nem vira combo");

        var total = Campo<TextBlock>(tela, "TxtTotal").Text;
        checar(total == new Dinheiro(Passo21).Formatado(),
            $"o TOTAL da comanda lê {new Dinheiro(Passo21).Formatado()} (veio '{total}')");

        var nomeNaLinha = TextoDaComanda(tela);
        checar(nomeNaLinha.Contains("VENDA DE TESTE"),
            $"a comanda mostra a linha em maiúsculas, como qualquer outra (veio '{nomeNaLinha}')");

        // segundo valor = OUTRA linha (id novo). Com id fixo o segundo toque viraria "2 ×"
        // do primeiro, e o operador cobraria o dobro do que digitou.
        QuandoAbrir(host, d =>
        {
            foreach (var t in "200") Clicar(BotaoDe(d, t.ToString()));
            Clicar(BotaoDe(d, "Confirmar"));
        });
        Clicar(botao);
        checar(comanda.Count == 2 && comanda[0].Produto.Preco.Centavos == 200
               && comanda[1].Produto.Preco.Centavos == Passo21
               && comanda[0].Produto.Id != comanda[1].Produto.Id,
            "um segundo valor entra como OUTRA linha (id novo), não dobra a primeira");

        // o rascunho guarda o valor digitado (o caixa cai e a comanda volta inteira)
        using (var cx = Banco.Abrir())
        {
            var r = Rascunho.Ler(cx, sessao.Id);
            checar(r is not null && r.Itens.Any(i => i.PrecoCent == Passo21),
                "o rascunho guarda o valor digitado: queda de energia não perde a comanda do teste");
        }

        // ── (c) o valor chega ao pagamento, que é quem fala com a maquininha ─
        comanda.RemoveAt(0);                       // fica só a linha do passo 21
        Invocar(tela, "PintarComanda");
        Invocar(tela, "Finalizar", null, new RoutedEventArgs());
        var painel = Campo<ContentControl>(tela, "PainelPagamento");
        var pagamento = painel.Content as Pdv.Telas.Pagamento;
        var itens = pagamento is null ? null : Campo<IReadOnlyList<LinhaVenda>>(pagamento, "_itens");
        checar(pagamento is not null && painel.Visibility == Visibility.Visible,
            "Finalizar abre o pagamento com a linha do valor de teste");
        checar(itens is { Count: 1 } && itens[0].Total.Centavos == Passo21,
            $"o pagamento recebe R$ 12.345,67 exatos (veio {(itens is null ? "nada" : new Dinheiro(itens.Sum(i => i.Total.Centavos)).Formatado())}) "
            + "— é este valor que vai para o cartão");
        var falta = Campo<TextBlock>(pagamento!, "TxtTotal").Text;
        checar(falta == new Dinheiro(Passo21).Formatado(),
            $"e a tela do pagamento mostra o mesmo valor (veio '{falta}')");

        // ── (d) rastro ─────────────────────────────────────────────────────
        using (var cx = Banco.Abrir())
        {
            var detalhes = cx.Query<string>(
                "SELECT detalhe FROM auditoria WHERE evento = 'venda_teste_valor_lancado' ORDER BY id").ToList();
            checar(detalhes.Count == 2, $"a auditoria guarda cada valor lançado (achei {detalhes.Count})");
            checar(detalhes.Any(d => d.Contains(new Dinheiro(Passo21).Formatado())),
                "e o registro traz o valor por extenso, para quem for conferir a gravação depois");
        }

        host.Content = null;
        host.Close();
    }

    // ── ajudantes ──────────────────────────────────────────────────────────
    private static void Config(string chave, string valor)
    {
        using var cx = Banco.Abrir();
        Vendas.GravarConfig(cx, chave, valor);
    }

    private static Button? Botao(DependencyObject raiz, string nome)
        => Descendentes<Button>(raiz).FirstOrDefault(b => AutomationProperties.GetName(b) == nome);

    /// <summary>Botão pelo nome de automação OU pelo Content textual (as teclas e o Confirmar).</summary>
    private static Button BotaoDe(DependencyObject raiz, string nome)
        => Descendentes<Button>(raiz).First(b => AutomationProperties.GetName(b) == nome || b.Content as string == nome);

    /// <summary>Todo o texto das linhas da comanda, junto.</summary>
    private static string TextoDaComanda(Pdv.Telas.Venda tela)
        => string.Join(" | ", Descendentes<TextBlock>(Campo<ItemsControl>(tela, "ListaComanda")).Select(t => t.Text));

    /// <summary>Age no primeiro diálogo modal que abrir. Devolve o timer para quem precisar desarmar.</summary>
    private static DispatcherTimer QuandoAbrir(Window host, Action<Window> acao)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        var tentativas = 0;
        timer.Tick += (_, _) =>
        {
            var d = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w != host && w.Owner == host && w.IsVisible);
            if (d is null)
            {
                if (++tentativas > 50) timer.Stop();
                return;
            }
            timer.Stop();
            acao(d);
        };
        timer.Start();
        return timer;
    }

    private static T Campo<T>(object alvo, string nome)
        => (T)alvo.GetType().GetField(nome, P)!.GetValue(alvo)!;

    private static void Definir(object alvo, string nome, object valor)
        => alvo.GetType().GetField(nome, P)!.SetValue(alvo, valor);

    private static void Invocar(object alvo, string metodo, params object?[] args)
        => alvo.GetType().GetMethods(P).First(m => m.Name == metodo && m.GetParameters().Length == args.Length)
            .Invoke(alvo, args);

    private static void Clicar(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static IEnumerable<T> Descendentes<T>(DependencyObject raiz) where T : DependencyObject
    {
        if (raiz is FrameworkElement fe) fe.ApplyTemplate();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(raiz); i++)
        {
            var f = VisualTreeHelper.GetChild(raiz, i);
            if (f is T t) yield return t;
            foreach (var n in Descendentes<T>(f)) yield return n;
        }
    }

    // ── leitura de fonte (mesma receita das outras suítes) ─────────────────
    private static string? Arquivo(params string[] partes)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var alvo = Path.Combine(new[] { d.FullName }.Concat(partes).ToArray());
            if (File.Exists(alvo)) return File.ReadAllText(alvo);
            d = d.Parent;
        }
        return null;
    }

    private static string Trecho(string fonte, string de, string ate)
    {
        var i = fonte.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var j = fonte.IndexOf(ate, i, StringComparison.Ordinal);
        return j < 0 ? fonte[i..] : fonte[i..(j + ate.Length)];
    }
}
