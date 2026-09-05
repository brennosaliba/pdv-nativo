using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// POS AVULSO como forma de primeira classe (04/09/2026, pedido do dono).
///
/// "Quando o PayGo estava instável utilizamos POS": a loja passa o cartão numa
/// maquininha SEM integração e registra a venda com a forma real (crédito, débito,
/// PIX, refeição) sem disparar o TEF. O caminho já existia como fallback
/// (RegistrarComoPos, depois de o TEF falhar); aqui ele vira botão na grade.
///
/// O que se prova:
///  · modelo: POS avulso é o cartão SEM carimbo do TEF (Aut e NSU nulos) — uma
///    definição só, em PagamentoVenda, espelhada no SQL do fechamento;
///  · nota: sai com o tPag da forma real e SEM grupo card (tpIntegra=2);
///  · nuvem: a forma real sobe com `origem: "pos"`;
///  · fechamento: POS não vira divergência falsa e NÃO esconde cartão TEF que sumiu;
///  · tela: o tile só aparece com TEF, o diálogo é de uma linha, o POS nunca chama
///    a maquininha integrada, e o chip/cupom dizem "Crédito POS".
/// </summary>
public static class TestesPos
{
    public static void Rodar(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-teste-pos-{Guid.NewGuid():N}.db");
        Banco.Migrar(arquivo);
        var anterior = Banco.CaminhoForcado;
        try
        {
            Modelo(checar);
            Fiscal(checar);
            using (var cx = Banco.Abrir(arquivo))
            {
                var (op, s) = Fechamento(cx, checar);
                Gravacao(cx, op, s, checar);
            }
            Tela(arquivo, checar);
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }

    // ── modelo: o que É um POS avulso ─────────────────────────────────────────
    private static void Modelo(Action<bool, string> checar)
    {
        var pos = new PagamentoVenda("credito", Dinheiro.DeReais(10), Dinheiro.Zero);
        checar(pos.PosAvulso && !pos.Integrado && pos.Origem == "pos",
            "cartao sem carimbo do TEF e POS avulso (origem \"pos\")");

        var tef = new PagamentoVenda("credito", Dinheiro.DeReais(10), Dinheiro.Zero,
            Aut: "123456", CnpjCredenciadora: "01027058000191", Bandeira: "01", Nsu: "000123");
        checar(tef.Integrado && !tef.PosAvulso && tef.Origem == "tef",
            "cartao com cAut do TEF e integrado (origem \"tef\")");

        var pixNsu = new PagamentoVenda("pix", Dinheiro.DeReais(10), Dinheiro.Zero, Aut: "NSU:9", Nsu: "9");
        checar(pixNsu.Integrado && pixNsu.Origem == "tef", "pix do TEF (carimbo NSU, sem cAut) e integrado");
        checar(new PagamentoVenda("debito", Dinheiro.DeReais(10), Dinheiro.Zero, Nsu: "9").Integrado,
            "NSU sozinho ja e carimbo do TEF");

        var din = new PagamentoVenda("dinheiro", Dinheiro.DeReais(10), Dinheiro.Zero);
        checar(!din.PosAvulso && !din.Integrado && din.Origem is null,
            "dinheiro nao e POS nem TEF (origem nula)");

        // A regra em SQL do fechamento tem que ser a MESMA dos dois carimbos
        checar(Caixa.SqlIntegrado.Contains("tef_aut IS NOT NULL") && Caixa.SqlIntegrado.Contains("tef_nsu IS NOT NULL"),
            "o SQL do fechamento usa os mesmos dois carimbos (cAut ou NSU)");
    }

    // ── nota fiscal: tPag da forma real, sem <card> ───────────────────────────
    private static void Fiscal(Action<bool, string> checar)
    {
        var pos = new PagamentoVenda("credito", Dinheiro.DeReais(10), Dinheiro.Zero);
        var fPos = PagamentoFiscal.De(pos);
        checar(fPos.TPag == "03" && fPos.Card is null && fPos.Valor == 10m,
            "POS credito: tPag 03 da forma real, sem card (tpIntegra=2)");
        checar(PagamentoFiscal.De(new PagamentoVenda("debito", Dinheiro.DeReais(10), Dinheiro.Zero)) is { TPag: "04", Card: null },
            "POS debito: tPag 04 sem card");
        checar(PagamentoFiscal.De(new PagamentoVenda("pix", Dinheiro.DeReais(10), Dinheiro.Zero)) is { TPag: "17", Card: null },
            "POS pix: tPag 17 sem card");
        checar(PagamentoFiscal.De(new PagamentoVenda("voucher", Dinheiro.DeReais(10), Dinheiro.Zero)) is { TPag: "11", Card: null },
            "POS refeicao: tPag 11 sem card");

        var tef = new PagamentoVenda("credito", Dinheiro.DeReais(10), Dinheiro.Zero,
            Aut: "123456", CnpjCredenciadora: "01027058000191", Bandeira: "01", Nsu: "000123");
        var fTef = PagamentoFiscal.De(tef);
        checar(fTef.Card is { CAut: "123456", Cnpj: "01027058000191", TBand: "01" },
            "controle: cartao TEF com cAut + CNPJ leva o card (tpIntegra=1)");
        checar(PagamentoFiscal.De(new PagamentoVenda("pix", Dinheiro.DeReais(10), Dinheiro.Zero, Aut: "NSU:9", Nsu: "9")).Card is null,
            "pix do TEF sem CNPJ da credenciadora sai sem card (como ja saia)");
        checar(PagamentoFiscal.De(new PagamentoVenda("dinheiro", Dinheiro.DeReais(30), Dinheiro.DeReais(4.50m))).Valor == 25.50m,
            "o valor da nota e o APLICADO (valor - troco)");

        // Pelo emissor de verdade, contra o agente falso: o corpo do POS nao leva "card".
        var itens = new List<ItemFiscal>
        {
            new("TST001", "TESTE Cookie", "19059090", null, "500", "5102", "UN", 1m, 10.00m),
        };
        using var sefaz = new FakeSefaz();
        sefaz.Roteiro.Enqueue(FakeSefaz.Desfecho.Autorizar);
        sefaz.Roteiro.Enqueue(FakeSefaz.Desfecho.Autorizar);
        var emissor = new EmissorAgente(sefaz.Url);

        var rPos = emissor.EmitirAsync(itens, new[] { fPos }, null, CancellationToken.None).GetAwaiter().GetResult();
        var corpoPos = sefaz.Chamadas.Single();
        checar(rPos.Autorizado, "POS avulso emite e autoriza (nada exige retorno do TEF)");
        checar(corpoPos.Contains("\"tPag\":\"03\"") && !corpoPos.Contains("\"card\""),
            "corpo fiscal do POS: tPag 03 e SEM grupo card (o motor emite tpIntegra=2)");

        emissor.EmitirAsync(itens, new[] { fTef }, null, CancellationToken.None).GetAwaiter().GetResult();
        var corpoTef = sefaz.Chamadas.First(c => c != corpoPos);
        checar(corpoTef.Contains("\"card\"") && corpoTef.Contains("\"cAut\":\"123456\""),
            "controle: o cartao TEF continua levando card com cAut");
    }

    // ── fechamento: TEF x PDV com POS avulso no mesmo turno ───────────────────
    private static (Operador, Sessao) Fechamento(Microsoft.Data.Sqlite.SqliteConnection cx, Action<bool, string> checar)
    {
        var op = new Operador("pos-op", "Pos", "operador");
        Operadores.Salvar(cx, op.Id, op.Nome, "1111", "operador");
        Vendas.GravarConfig(cx, "tef_habilitado", "1");
        var s = Caixa.Abrir(cx, op, Dinheiro.Zero);

        string Venda(long cent)
        {
            var id = Guid.NewGuid().ToString();
            cx.Execute("""
                INSERT INTO venda (id, client_key, sessao_id, business_date, numero_local, operador_id,
                                   subtotal_cent, total_cent, status, criada_em, finalizada_em)
                VALUES (@Id,@K,@S,@Bd,@N,@Op,@T,@T,'finalizada',@Em,@Em)
                """,
                new { Id = id, K = id, S = s.Id, Bd = s.BusinessDate,
                      N = cx.ExecuteScalar<int>("SELECT COALESCE(MAX(numero_local),0)+1 FROM venda WHERE business_date=@B", new { B = s.BusinessDate }),
                      Op = op.Id, T = cent, Em = DateTime.Now.ToString("o") });
            return id;
        }
        void Pagamento(string venda, string forma, long cent, string? aut, string? nsu)
            => cx.Execute("INSERT INTO venda_pagamento (id,venda_id,forma,valor_cent,troco_cent,tef_aut,tef_nsu) VALUES (@i,@v,@f,@c,0,@a,@n)",
                new { i = Guid.NewGuid().ToString(), v = venda, f = forma, c = cent, a = aut, n = nsu });
        void TefPago(string tipo, long cent, string nsu)
            => cx.Execute("""
                INSERT INTO tef_transacao (id, venda_id, charge_id, provedor, tipo, valor_cent, nsu, situacao, criado_em, atualizado_em)
                VALUES (@Id, NULL, @Id, 'paygo', @T, @V, @Nsu, 'pago', @Em, @Em)
                """, new { Id = "tef-" + nsu, T = tipo, V = cent, Nsu = nsu, Em = DateTime.Now.ToString("o") });
        List<DivergenciaTef> Div() => Caixa.DivergenciasTef(cx, s);

        // (1) só POS avulso no turno: nada em tef_transacao, nada a acusar
        Pagamento(Venda(5000), "credito", 5000, null, null);
        checar(Div().Count == 0, "POS avulso sozinho nao vira divergencia TEF x PDV");
        checar(Caixa.FormasContadas(cx, s).Contains("credito"), "POS avulso traz a forma para a contagem do fechamento");
        checar(Caixa.CobrancaSemVenda(cx, s).Centavos == 0, "POS avulso nao conta como cobranca sem venda");

        // (2) TEF integrado R$ 100 + POS R$ 50 na MESMA forma: a maquininha integrada
        //     cobrou 100 e o PDV tem 100 integrados — os 50 do POS ficam FORA da conta
        Pagamento(Venda(10000), "credito", 10000, "AUT100", "N100");
        TefPago("credito", 10000, "N100");
        checar(Div().All(d => d.Forma != "credito"),
            "TEF R$100 + POS R$50 no mesmo turno: sem divergencia (o POS nao entra na conta do TEF)");
        checar(Caixa.ApuradoIntegrado(cx, s)["credito"].Centavos == 10000 && Caixa.Apurado(cx, s)["credito"].Centavos == 15000,
            "o apurado do turno tem os 150 (fechamento); so 100 sao integrados (conferencia TEF)");

        // (3) cobranca ORFA (aprovada, sem venda) NAO pode ser escondida pelo POS
        TefPago("credito", 5000, "N-orfa");
        checar(Div().Any(d => d.Forma == "credito" && d.Diferenca.Centavos == 5000),
            "TEF orfao de R$50 continua acusando R$50 mesmo com POS de R$50 na mesma forma");
        checar(Caixa.CobrancaSemVenda(cx, s).Centavos == 5000, "a orfa conta como cobranca sem venda (POS nao a compensa)");

        // (4) venda de cartao INTEGRADO sem linha no TEF (o PDV perdeu a transacao) tem
        //     que aparecer mesmo quando ha POS na mesma forma
        var vd = Venda(3000);
        Pagamento(vd, "debito", 3000, "AUT-D", "N-D");      // integrado, sem tef_transacao
        Pagamento(Venda(2000), "debito", 2000, null, null); // POS avulso na mesma forma
        checar(Div().Any(d => d.Forma == "debito" && d.Diferenca.Centavos == -3000),
            "cartao integrado sem tef_transacao acusa -R$30 mesmo com POS de debito no turno");

        // (5) sem TEF nada disso existe: POS e a unica maneira de cartao, e nao ha alarme
        Vendas.GravarConfig(cx, "tef_habilitado", "0");
        checar(Caixa.FormasContadas(cx, s).Contains("credito") && Caixa.FormasContadas(cx, s).Contains("pix"),
            "sem TEF todas as formas sao contadas (POS e o unico cartao)");
        Vendas.GravarConfig(cx, "tef_habilitado", "1");
        return (op, s);
    }

    // ── gravação da venda e o que sobe para a nuvem ───────────────────────────
    private static void Gravacao(Microsoft.Data.Sqlite.SqliteConnection cx, Operador op, Sessao s, Action<bool, string> checar)
    {
        var itens = new List<LinhaVenda>
        {
            new("p1", "SKU1", "DONUT", Quantidade.Um, Dinheiro.DeReais(10), Dinheiro.DeReais(10),
                "UN", "19053100", null, "102", null, 0),
        };
        string Payload(string vendaId)
            => cx.ExecuteScalar<string>("SELECT payload FROM outbox WHERE tipo='venda' AND ref_id=@v", new { v = vendaId }) ?? "";

        var vPos = Vendas.Finalizar(cx, s, op, itens,
            new[] { new PagamentoVenda("credito", Dinheiro.DeReais(10), Dinheiro.Zero) }, null, "Loja", null);
        var linha = cx.QueryFirst("SELECT forma, tef_aut, tef_nsu, tef_cnpj_cred FROM venda_pagamento WHERE venda_id=@v", new { v = vPos.Id });
        checar((string)linha.forma == "credito" && linha.tef_aut is null && linha.tef_nsu is null,
            "POS grava a forma REAL (credito), sem carimbo do TEF");
        var pPos = Payload(vPos.Id);
        checar(pPos.Contains("\"metodo\":\"credito\"") && pPos.Contains("\"origem\":\"pos\""),
            "nuvem: metodo credito + origem \"pos\" (o servidor ignora, o painel pode ler)");

        var vTef = Vendas.Finalizar(cx, s, op, itens,
            new[] { new PagamentoVenda("debito", Dinheiro.DeReais(10), Dinheiro.Zero, Aut: "A1", Nsu: "N1") }, null, "Loja", null);
        checar(Payload(vTef.Id).Contains("\"metodo\":\"debito\"") && Payload(vTef.Id).Contains("\"origem\":\"tef\""),
            "nuvem: cartao integrado sobe com origem \"tef\"");

        var vDin = Vendas.Finalizar(cx, s, op, itens,
            new[] { new PagamentoVenda("dinheiro", Dinheiro.DeReais(10), Dinheiro.Zero) }, null, "Loja", null);
        checar(Payload(vDin.Id).Contains("\"metodo\":\"dinheiro\"") && Payload(vDin.Id).Contains("\"origem\":null"),
            "nuvem: dinheiro sobe com origem nula");
    }

    // ── tela: WPF de verdade, em STA ──────────────────────────────────────────
    private static void Tela(string arquivo, Action<bool, string> checar)
    {
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        Exception? erro = null;
        // no Application compartilhado da bateria (HostWpf): o WPF so deixa criar um
        try { HostWpf.Executar(() => Passos(checar)); }
        catch (Exception ex) { erro = ex; }
        Banco.CaminhoForcado = anterior;
        checar(erro is null, "tela: a tela de pagamento subiu e os passos rodaram (" + (erro?.ToString() ?? "ok") + ")");
    }

    private const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void Passos(Action<bool, string> checar)
    {
        var op = new Operador("pos-ui", "Tela", "operador");
        var sessao = new Sessao("sessao-ui", Caixa.DiaOperacional(), op.Id, op.Nome, DateTime.Now, Dinheiro.Zero);
        var itens = new List<LinhaVenda>
        {
            new("p1", "SKU1", "DONUT", Quantidade.Um, Dinheiro.DeReais(10), Dinheiro.DeReais(10), "UN", null, null, null, null, 0),
        };
        var emissor = new EmissorMudo();

        // (1) sem TEF: a grade NAO tem POS (todo cartao ja e manual)
        var semTef = new Pdv.Telas.Pagamento(op, sessao, itens, emissor, null, "Loja", null);
        var nomes = Tiles(semTef);
        checar(!nomes.Contains("POS"), "sem TEF a grade nao tem POS");
        checar(nomes.SequenceEqual(new[] { "Dinheiro", "Débito", "Crédito", "PIX", "Refeição" }),
            "sem TEF: as 5 formas de sempre, na mesma ordem");

        // (2) com TEF: POS entra por ultimo, 6 formas em 3 colunas
        var tef = new TefQueConta();
        var host = new Window
        {
            Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0,
        };
        var tela = new Pdv.Telas.Pagamento(op, sessao, itens, emissor, tef, "Loja", null);
        host.Content = tela;
        host.Show();
        nomes = Tiles(tela);
        checar(nomes.Count == 6 && nomes.Last() == "POS", "com TEF a grade ganha o tile POS (6 formas)");
        checar(Grade(tela).Columns == 3, "6 formas = 3 colunas (2 linhas cheias)");

        // (3) o dialogo de UMA linha: opcoes lado a lado; tocar em PIX devolve 2
        var linhaUnica = false;
        var comVoltar = false;
        QuandoAbrir(host, d =>
        {
            var grade = Descendentes<UniformGrid>(d).FirstOrDefault(g => g.Rows == 1);
            linhaUnica = grade is not null && grade.Children.Count == 4 && Opcoes(d).SequenceEqual(new[] { "Crédito", "Débito", "PIX", "Refeição" });
            comVoltar = Descendentes<Button>(d).Any(b => (string)b.Content == "Voltar");
            Clicar(Descendentes<Button>(d).First(b => (string)b.Content == "PIX"));
        });
        var escolhido = Pdv.Telas.Dialogo.Escolher(host, "Maquininha avulsa", "Como o cliente pagou no POS?", "Crédito", "Débito", "PIX", "Refeição");
        checar(escolhido == 2, "Dialogo.Escolher devolve o indice da opcao tocada (PIX = 2)");
        checar(linhaUnica, "as opcoes ficam numa linha so, na ordem credito, debito, PIX, refeicao");
        checar(comVoltar, "o dialogo tem Voltar (alvo de dedo para desistir)");

        // (4) tocar no POS: pergunta a forma real e entra na tela de valor marcada como POS
        QuandoAbrir(host, d => Clicar(Descendentes<Button>(d).First(b => (string)b.Content == "Débito")));
        Invocar(tela, "Escolheu", "pos");
        checar(Campo<string>(tela, "_formaEmEdicao") == "debito" && Campo<bool>(tela, "_posAvulso"),
            "POS > Debito: a forma em edicao e a REAL (debito) com a marca de POS avulso");
        checar(Texto(tela, "TxtRotuloEntrada") == "COBRAR NO DÉBITO POS" && Texto(tela, "TxtEtapa") == "Quanto cobrar no Débito POS?",
            "a tela de valor diz 'Debito POS'");

        // voucher desligado: o dialogo do POS nao oferece Refeicao
        using (var cx = Banco.Abrir()) Vendas.GravarConfig(cx, "forma_voucher", "0");
        string[] opcoesSemVoucher = Array.Empty<string>();
        QuandoAbrir(host, d => { opcoesSemVoucher = Opcoes(d); Clicar(Descendentes<Button>(d).First(b => (string)b.Content == "Voltar")); });
        Invocar(tela, "Escolheu", "pos");
        checar(opcoesSemVoucher.SequenceEqual(new[] { "Crédito", "Débito", "PIX" }),
            "com forma_voucher desligado o POS oferece so credito, debito e PIX");
        using (var cx = Banco.Abrir()) Vendas.GravarConfig(cx, "forma_voucher", "1");

        // (5) confirmar R$ 4,00 em Credito POS: pede a aprovacao na tela e NUNCA chama o TEF
        QuandoAbrir(host, d => Clicar(Descendentes<Button>(d).First(b => (string)b.Content == "Crédito")));
        Invocar(tela, "Escolheu", "pos");
        Definir(tela, "_digitado", "400");
        Invocar(tela, "PintarDinheiro");
        string? textoConfirmacao = null;
        QuandoAbrir(host, d =>
        {
            textoConfirmacao = string.Join(" | ", Descendentes<TextBlock>(d).Select(t => t.Text));
            Clicar(Descendentes<Button>(d).First(b => ((string)b.Content).StartsWith("Aprovado", StringComparison.Ordinal)));
        });
        Invocar(tela, "ConfirmarDinheiro", null, new RoutedEventArgs());
        var partes = Campo<List<PagamentoVenda>>(tela, "_partes");
        checar(tef.Cobrancas == 0, "POS avulso NAO arma a maquininha integrada");
        checar(textoConfirmacao is not null && textoConfirmacao.Contains("Crédito") && textoConfirmacao.Contains("R$ 4,00"),
            "a confirmacao pede para passar R$ 4,00 em Credito na maquininha");
        checar(partes.Count == 1 && partes[0].Forma == "credito" && partes[0].PosAvulso && partes[0].Valor.Centavos == 400,
            "a parte lancada e credito de R$ 4,00, POS avulso");
        checar(Chips(tela).SequenceEqual(new[] { "✓ Crédito POS R$ 4,00" }), "o chip diz 'Credito POS'");

        // (6) controle: cartao normal com TEF continua armando a maquininha
        Invocar(tela, "Escolheu", "debito");
        checar(!Campo<bool>(tela, "_posAvulso") && Texto(tela, "TxtRotuloEntrada") == "COBRAR NO DÉBITO",
            "escolher Debito depois do POS limpa a marca: 'COBRAR NO DEBITO', sem POS");
        Definir(tela, "_digitado", "600");
        Invocar(tela, "PintarDinheiro");
        Invocar(tela, "ConfirmarDinheiro", null, new RoutedEventArgs());
        checar(tef.Cobrancas == 1, "controle: o debito normal arma o TEF (1 cobranca)");

        // (7) tirar a parte POS: e permitido (nao passou pelo TEF), com o rotulo certo no aviso
        string? textoTirar = null;
        QuandoAbrir(host, d =>
        {
            textoTirar = string.Join(" | ", Descendentes<TextBlock>(d).Select(t => t.Text));
            Clicar(Descendentes<Button>(d).First(b => (string)b.Content == "Tirar"));
        });
        Invocar(tela, "TirarParte", partes[0]);
        checar(textoTirar is not null && textoTirar.Contains("Crédito POS"), "o aviso de tirar nomeia 'Credito POS'");
        checar(Campo<List<PagamentoVenda>>(tela, "_partes").Count == 0, "a parte POS sai (nao ha estorno a fazer no TEF)");

        host.Close();
    }

    // ── ajudantes da tela ─────────────────────────────────────────────────────

    private static UniformGrid Grade(Pdv.Telas.Pagamento tela)
        => (UniformGrid)typeof(Pdv.Telas.Pagamento).GetField("GradeFormas", P)!.GetValue(tela)!;

    private static List<string> Tiles(Pdv.Telas.Pagamento tela)
        => Grade(tela).Children.OfType<Button>().Select(AutomationProperties.GetName).ToList();

    private static List<string> Chips(Pdv.Telas.Pagamento tela)
    {
        var painel = (Panel)typeof(Pdv.Telas.Pagamento).GetField("PainelPartes", P)!.GetValue(tela)!;
        return painel.Children.OfType<Border>()
            .Select(b => Descendentes<TextBlock>(b).First().Text).ToList();
    }

    private static string Texto(Pdv.Telas.Pagamento tela, string campo)
        => ((TextBlock)typeof(Pdv.Telas.Pagamento).GetField(campo, P)!.GetValue(tela)!).Text;

    private static T Campo<T>(object alvo, string nome)
        => (T)alvo.GetType().GetField(nome, P)!.GetValue(alvo)!;

    private static void Definir(object alvo, string nome, object valor)
        => alvo.GetType().GetField(nome, P)!.SetValue(alvo, valor);

    private static void Invocar(object alvo, string metodo, params object?[] args)
        => alvo.GetType().GetMethods(P).First(m => m.Name == metodo && m.GetParameters().Length == args.Length)
            .Invoke(alvo, args);

    /// <summary>Opções do diálogo de escolha (todos os botões menos o Voltar), na ordem.</summary>
    private static string[] Opcoes(Window d)
        => Descendentes<Button>(d).Select(b => (string)b.Content).Where(c => c != "Voltar").ToArray();

    private static void Clicar(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    /// <summary>
    /// Espera o próximo diálogo modal abrir sobre o host e age nele. Roda num timer do
    /// Dispatcher porque ShowDialog bloqueia quem chamou: o laço aninhado dele é o que
    /// dispara o timer, e o teste continua quando o diálogo fecha.
    /// </summary>
    private static void QuandoAbrir(Window host, Action<Window> acao)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        var tentativas = 0;
        timer.Tick += (_, _) =>
        {
            var d = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w != host && w.Owner == host && w.IsVisible);
            if (d is null)
            {
                if (++tentativas > 50) { timer.Stop(); }
                return;
            }
            timer.Stop();
            acao(d);
        };
        timer.Start();
    }

    private static IEnumerable<T> Descendentes<T>(DependencyObject raiz) where T : DependencyObject
    {
        foreach (var filho in LogicalTreeHelper.GetChildren(raiz))
        {
            if (filho is not DependencyObject d) continue;
            if (d is T t) yield return t;
            foreach (var neto in Descendentes<T>(d)) yield return neto;
        }
    }

    /// <summary>TEF que só conta quantas vezes foi armado e recusa na hora.</summary>
    private sealed class TefQueConta : IProvedorTef
    {
        public int Cobrancas;
        public string Nome => "teste";
        public Task<DesfechoTef> CobrarAsync(TipoTef tipo, Dinheiro valor, string? documento,
            int parcelas, IProgress<AndamentoTef>? andamento, CancellationToken ct)
        {
            Cobrancas++;
            return Task.FromResult(new DesfechoTef(SituacaoTef.Recusado, null, "chg-teste", null, "recusado no teste", false)
            { Codigo = CodigoTef.Recusado });
        }
    }

    /// <summary>Emissor que nunca é chamado nestes passos; existe porque a tela exige um.</summary>
    private sealed class EmissorMudo : IEmissorFiscal
    {
        public Task<SaudeEmissor> SondarAsync(CancellationToken ct)
            => Task.FromResult(new SaudeEmissor(false, null, null, null, 0, null, "mudo"));
        public Task<ResultadoEmissao> EmitirAsync(IReadOnlyList<ItemFiscal> itens,
            IReadOnlyList<PagamentoFiscal> pagamentos, string? documento, CancellationToken ct)
            => Task.FromResult(ResultadoEmissao.ForaDoAr("teste", "mudo"));
    }
}
