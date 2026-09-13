using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// INVESTIGACAO 13/09/2026 (dono, Savassi): "PROMOÇÃO FUNCIONÁRIO ATIVA E NÃO APARECE NO PDV".
/// Reproduz com o payload REAL que pdv_promocoes_ativas('American Day Savassi', true) devolve
/// hoje em producao. So le e mede: nao muda comportamento nenhum.
/// </summary>
public static class TestesPromoFuncionarioSavassi
{
    private const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;

    // copiado de producao em 13/09/2026 (to_jsonb(pdv_promocoes) - 'criada_em')
    private const string PayloadReal = """
        {"id": "3037fa4b-0518-4879-817e-eb42fdf8331b", "fim": null, "alvo": "todos", "leve": null, "nome": "DESCONTO FUNCIONARIO", "tipo": "percentual", "ativa": true, "combo": null, "lojas": ["American Day Savassi"], "pague": null, "store": "American Day Savassi", "config": {"autorizacao": "gerente"}, "inicio": "2026-09-08", "hora_fim": null, "categorias": null, "percentual": 30, "dias_semana": null, "hora_inicio": null, "produto_ids": null, "regras_semana": null, "valor_desconto_cent": null}
        """;
    private const string IdFunc = "3037fa4b-0518-4879-817e-eb42fdf8331b";

    public static void Rodar(Action<bool, string> checar)
    {
        var agora = DateTime.Now;
        var func = Promocoes.Parsear(PayloadReal);
        checar(func is not null, "payload real lido pelo motor");
        if (func is null) return;
        checar(func.ExigeAutorizacao && func.Autorizacao == Promocoes.NivelGerente,
            $"exige codigo do gerente (Autorizacao={func.Autorizacao})");
        checar(func.Alvo == "todos" && func.Percentual == 30m && func.Inicio == new DateOnly(2026, 9, 8),
            $"alvo={func.Alvo} pct={func.Percentual} inicio={func.Inicio}");
        checar(Promocoes.Vigente(func, agora), $"vigente agora pelo relogio local ({agora:dd/MM HH:mm})");

        var vitrine = Promocoes.ProdutosEmPromocao(new[] { func }, agora);
        Console.WriteLine($"      [medido] ProdutosEmPromocao so com a promo funcionario: {vitrine.Count} produto(s)");
        checar(vitrine.Count == 0, "ela NAO entra na vitrine da categoria PROMOCAO");

        var ctx = new Promocoes.ContextoAutorizacao();
        var vazio = Promocoes.AvaliarCarrinho(new[] { func }, Array.Empty<Promocoes.ItemCarrinho>(), agora, ctx);
        Console.WriteLine($"      [medido] comanda vazia: Pendentes={vazio.Pendentes.Count}");
        checar(vazio.Pendentes.Count == 0, "comanda vazia: nada pendente (nenhum botao)");

        var um = Promocoes.AvaliarCarrinho(new[] { func },
            new[] { new Promocoes.ItemCarrinho("d-ninho", "Donuts", 900, 1000) }, agora, ctx);
        Console.WriteLine($"      [medido] 1 donut R$ 9,00: Pendentes={um.Pendentes.Count} desconto={(um.Pendentes.Count > 0 ? um.Pendentes[0].DescontoCent : 0)} aplicado={um.TotalCent}");
        checar(um.Pendentes.Count == 1 && um.Pendentes[0].DescontoCent == 270 && um.TotalCent == 0,
            "com 1 item: pendente de R$ 2,70 e NADA aplicado sem codigo");

        // uma promocao livre que da mais desconto que os 30% esconde o botao (Ganha na etapa 2)
        const string Leve2Pague1 = """
            {"id":"duo","nome":"promocao duo gourmet","tipo":"leve_x_pague_y","alvo":"produtos","leve":2,"pague":1,
             "produto_ids":["d-ninho"],"config":{"lxpy":{"gratis_mais_barato":true}},"inicio":"2026-08-20"}
            """;
        var duo = Promocoes.Parsear(Leve2Pague1)!;
        var dois = Promocoes.AvaliarCarrinho(new[] { func, duo },
            new[] { new Promocoes.ItemCarrinho("d-ninho", "Donuts", 900, 2000) }, agora, ctx);
        Console.WriteLine($"      [medido] 2 donuts com leve2pague1 livre: vencedora={dois.PromoNome} Pendentes={dois.Pendentes.Count}");
        checar(dois.Pendentes.Count == 0, "promo livre maior vence: o botao da promo com senha some");

        // ── A TELA DE VENDA DE VERDADE, 1024x768 ─────────────────────────────
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-promo-func-sav-{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        Exception? erro = null;
        try
        {
            Banco.Migrar(arquivo);
            using (var cx = Banco.Abrir(arquivo)) Semear(cx);
            try { HostWpf.Executar(() => NaTela(checar)); }
            catch (Exception ex) { erro = ex; }
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
        checar(erro is null, "tela: os passos rodaram (" + (erro?.ToString() ?? "ok") + ")");
    }

    private static void NaTela(Action<bool, string> checar)
    {
        var host = new Window
        {
            Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0,
        };
        host.Show();
        var fecha = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        fecha.Tick += (_, _) =>
        {
            foreach (var d in Application.Current.Windows.OfType<Window>()
                         .Where(w => w != host && w.Owner == host && w.IsVisible).ToList()) d.Close();
        };
        fecha.Start();

        var op = new Operador("op-real", "Maria", "operador");
        var turno = new Sessao("sessao-real", Caixa.DiaOperacional(), op.Id, op.Nome, DateTime.Now, Dinheiro.DeReais(300m));
        var venda = new Pdv.Telas.Venda(op, turno);
        host.Content = venda;
        host.UpdateLayout();

        var promos = Campo<List<Promocoes.Promo>>(venda, "_promos");
        Console.WriteLine($"      [medido] promos carregadas na tela: {string.Join(", ", promos.Select(p => p.Nome))}");
        checar(promos.Any(p => p.Id == IdFunc), "a tela carregou a DESCONTO FUNCIONARIO da tabela local promo");

        var catPromo = (string)typeof(Pdv.Telas.Venda).GetField("CategoriaPromo", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        var contagem = Campo<Dictionary<string, int>>(venda, "_quantosPorCategoria");
        Console.WriteLine($"      [medido] categoria '{catPromo}' existe: {contagem.ContainsKey(catPromo)} (itens {contagem.GetValueOrDefault(catPromo)})");

        // o que o dono viu: abrir a categoria PROMOCAO
        typeof(Pdv.Telas.Venda).GetField("_categoriaAtual", P)!.SetValue(venda, catPromo);
        Invocar(venda, "PintarProdutos");
        host.UpdateLayout();
        var lista = Campo<ItemsControl>(venda, "ListaProdutos");
        var textos = lista.Items.OfType<DependencyObject>().SelectMany(Textos).ToList();
        Console.WriteLine($"      [medido] categoria PROMOCAO mostra {lista.Items.Count} secao(oes): {string.Join(" / ", textos)}");
        // 13/09/2026: era o defeito medido ("nao lista"). Com a correcao, o card aparece.
        checar(textos.Any(t => t.Contains("Funcionario", StringComparison.OrdinalIgnoreCase)),
            "categoria PROMOCAO lista a Desconto Funcionario (card com chave)");

        var botao = Campo<Button>(venda, "BtnPromoComSenha");
        var rotulo = Campo<TextBlock>(venda, "TxtPromoComSenha");
        checar(botao.Visibility != Visibility.Visible, "comanda vazia: botao escondido");

        Invocar(venda, "Adicionar", Produto());
        host.UpdateLayout();
        Bombear(600);
        host.UpdateLayout();
        var pos = botao.TranslatePoint(new Point(0, 0), host);
        var fin = Campo<Button>(venda, "BtnFinalizar");
        var posFin = fin.TranslatePoint(new Point(0, 0), host);
        Console.WriteLine($"      [medido] 1 item: botao visivel={botao.Visibility} texto='{rotulo.Text}' em x={pos.X:0} y={pos.Y:0} {botao.ActualWidth:0}x{botao.ActualHeight:0}; Finalizar y={posFin.Y:0}; total={Campo<TextBlock>(venda, "TxtTotal").Text}");
        var ft = new System.Windows.Media.FormattedText(rotulo.Text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new System.Windows.Media.Typeface(rotulo.FontFamily, rotulo.FontStyle, rotulo.FontWeight, rotulo.FontStretch),
            rotulo.FontSize, System.Windows.Media.Brushes.Black, 1.0);
        Console.WriteLine($"      [medido] texto do botao: precisa {ft.WidthIncludingTrailingWhitespace:0.0}px, ganhou {rotulo.ActualWidth:0.0}px => {(ft.WidthIncludingTrailingWhitespace > rotulo.ActualWidth + 0.5 ? "CORTADO com reticencias" : "inteiro")}");
        checar(botao.Visibility == Visibility.Visible && rotulo.Text == "Aplicar Desconto Funcionario",
            "com 1 item: botao 'Aplicar Desconto Funcionario' aparece");
        checar(pos.X >= 0 && pos.Y >= 0 && pos.X + botao.ActualWidth <= 1024 && pos.Y + botao.ActualHeight <= 768 && botao.ActualHeight > 0,
            "o botao cabe dentro de 1024x768");

        fecha.Stop();
        host.Content = null;
        host.Close();
    }

    private static IEnumerable<string> Textos(DependencyObject o)
    {
        if (o is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text)) yield return tb.Text;
        foreach (var c in LogicalTreeHelper.GetChildren(o).OfType<DependencyObject>())
            foreach (var t in Textos(c)) yield return t;
    }

    private static void Bombear(int ms)
    {
        var fim = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < fim)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(20);
        }
    }

    private static Pdv.Telas.Produto Produto()
    {
        using var cx = Banco.Abrir();
        var p = cx.QueryFirst("SELECT id, plu, nome, categoria, preco_cent, unidade, csosn FROM produto WHERE id='d-ninho'");
        return new Pdv.Telas.Produto((string)p.id, (string?)p.plu, (string)p.nome, (string)p.categoria,
            new Dinheiro((long)p.preco_cent), (string)p.unidade, null, null, (string?)p.csosn, 0, null);
    }

    private static void Semear(SqliteConnection cx)
    {
        var agora = DateTime.Now.ToString("o");
        cx.Execute("""
            INSERT INTO terminal (id, terminal_uuid, loja_id, loja_nome, cnpj, serie_nfce, ambiente, api_base, criado_em)
            VALUES (1, 'term-func-sav', 'loja-1', 'American Day Savassi', '00000000000000', 1, 2, 'http://127.0.0.1:9', @a)
            """, new { a = agora });
        Isolamento.SemNuvem(cx);
        var (h, s) = Operadores.GerarHash("4321");
        cx.Execute("INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,ativo,atualizado) VALUES ('op-real','Maria',@H,@S,'operador',1,@a)",
            new { H = h, S = s, a = agora });
        cx.Execute("""
            INSERT INTO produto (id, plu, nome, categoria, preco_cent, unidade, ativo, atualizado, csosn)
            VALUES ('d-ninho','4','DONUT NINHO','Donuts',900,'UN',1,@a,'102')
            """, new { a = agora });
        cx.Execute("INSERT INTO promo (id, payload, atualizado_em) VALUES (@Id, @P, @a)",
            new { Id = IdFunc, P = PayloadReal.Trim(), a = agora });
        // "donuts do dia" como no print: produto por id, regra num dia que NAO e hoje
        var outroDia = Promocoes.DiaIso(DateTime.Now) % 7 + 1;
        cx.Execute("INSERT INTO promo (id, payload, atualizado_em) VALUES ('donuts-dia', @P, @a)", new
        {
            a = agora,
            P = "{\"id\":\"donuts-dia\",\"nome\":\"donuts do dia\",\"tipo\":\"percentual\",\"alvo\":\"produtos\",\"percentual\":20,"
              + "\"produto_ids\":[\"d-ninho\"],\"inicio\":\"2026-08-06\",\"regras_semana\":[{\"dias\":[" + outroDia + "],\"produto_ids\":[\"d-ninho\"],\"percentual\":20}]}",
        });
        Vendas.GravarConfig(cx, "modo_fiscal", "recibo");
    }

    private static T Campo<T>(object alvo, string nome)
        => (T)alvo.GetType().GetField(nome, P)!.GetValue(alvo)!;

    private static void Invocar(object alvo, string metodo, params object?[] args)
        => alvo.GetType().GetMethods(P).First(m => m.Name == metodo && m.GetParameters().Length == args.Length)
            .Invoke(alvo, args);
}
