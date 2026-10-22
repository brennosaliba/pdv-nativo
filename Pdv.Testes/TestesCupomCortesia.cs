using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// CUPOM DE CORTESIA GERADO NO PAINEL (SQL 212, 22/10/2026).
///
/// Pedido do dono: em /pdv/promocoes ele gera um código para a cliente (a caixinha de 4 donuts veio
/// ruim, libera 4 donuts). Ela leva o código na loja, o operador digita no caixa e os itens saem a
/// R$ 0,00. Decisão do dono: o cupom libera PRODUTOS ou uma CATEGORIA com quantidade (o operador
/// escolhe os sabores); só zera o que bate com o cupom, até a quantidade, e o brinde nunca vale mais
/// que o liberado.
///
/// O caixa já tinha o botão "Aplicar cupom de cortesia" (casava pelo nome). Esta suíte prova:
///  1. a cobertura (pura): produto pelo id ou nome, categoria pelo grupo e pelo MAIS BARATO, produto
///     antes da categoria, nunca além da quantidade nem do que a cliente levou;
///  2. a leitura da resposta nova (categoria, motivo, cliente) e da antiga;
///  3. contra o Supabase de mentira: conferir e usar pela SESSÃO do terminal, o que foi zerado e a
///     chave do uso no corpo, a mesma chave devolve ok, outra chave recusa; sem o SQL 212 (404) cai
///     nas portas antigas;
///  4. a fila: o uso que falhou na hora sobe pela porta nova com a mesma chave;
///  5. a tela de verdade: 4 donuts clássicos de sabores escolhidos saem de graça e o premium é
///     cobrado; cupom de outra loja e categoria ausente dão recado de uma linha; a comanda toda em
///     cortesia queima o cupom no servidor com o que foi zerado.
/// </summary>
public static class TestesCupomCortesia
{
    private const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;
    private const int PortaCliente = 4791;
    private const int PortaTela = 4792;
    private const string Ninho = "p-ninho", Brig = "p-brig", Choco = "p-choco", Pistache = "p-pist", Agua = "p-agua";

    private static string Norm(string s) => (string)typeof(Pdv.Telas.Venda)
        .GetMethod("NormalizarNome", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { s })!;

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        Cobertura(checar);
        Leitura(checar);
        await Cliente(checar);
        await Fila(checar);
        NaTela(checar);
    }

    // ── 1. cobertura pura ───────────────────────────────────────────────────
    private static void Cobertura(Action<bool, string> checar)
    {
        LinhaParaCortesia L(string id, string nome, string cat, long preco, int un) => new(id, nome, cat, preco, un);
        var cat4 = new[] { new ItemCortesia("Donuts Clássicos", 4, "Donuts Clássicos") };

        var r = CoberturaCortesia.Calcular(cat4, new[]
        {
            L(Ninho, "DONUT NINHO", "Donuts Clássicos", 1290, 2), L(Brig, "DONUT BRIGADEIRO", "Donuts Clássicos", 1190, 2),
            L(Pistache, "DONUT PISTACHE", "Donuts Premium", 1890, 1),
        }, Norm);
        checar(r.PorNome.GetValueOrDefault(Norm("DONUT NINHO")) == 2 && r.PorNome.GetValueOrDefault(Norm("DONUT BRIGADEIRO")) == 2
               && !r.PorNome.ContainsKey(Norm("DONUT PISTACHE")) && r.Faltando.Count == 0,
            "CC-1 o caso do dono: 4 da categoria cobrem 2 ninho + 2 brigadeiro; o premium de outra categoria fica fora");

        r = CoberturaCortesia.Calcular(cat4, new[]
        {
            L(Ninho, "DONUT NINHO", "Donuts Clássicos", 1290, 2), L(Brig, "DONUT BRIGADEIRO", "Donuts Clássicos", 1190, 2),
            L(Choco, "DONUT CHOCOLATE", "Donuts Clássicos", 990, 2),
        }, Norm);
        checar(r.PorNome.GetValueOrDefault(Norm("DONUT CHOCOLATE")) == 2 && r.PorNome.GetValueOrDefault(Norm("DONUT BRIGADEIRO")) == 2
               && !r.PorNome.ContainsKey(Norm("DONUT NINHO")) && r.PorNome.Values.Sum() == 4,
            "CC-2 seis donuts da categoria num cupom de 4: cobre os quatro mais baratos, nunca mais que 4");

        var misto = new[] { new ItemCortesia("DONUT BRIGADEIRO", 2, null, Brig), new ItemCortesia("Donuts Clássicos", 2, "Donuts Clássicos") };
        r = CoberturaCortesia.Calcular(misto, new[]
        {
            L(Ninho, "DONUT NINHO", "Donuts Clássicos", 1290, 1), L(Brig, "DONUT BRIGADEIRO", "Donuts Clássicos", 1190, 3),
        }, Norm);
        checar(r.PorNome.GetValueOrDefault(Norm("DONUT BRIGADEIRO")) == 3 && r.PorNome.GetValueOrDefault(Norm("DONUT NINHO")) == 1,
            "CC-3 misto: 2 brigadeiro pelo produto e a categoria leva o 3o brigadeiro e o ninho (4 no total)");

        r = CoberturaCortesia.Calcular(new[] { new ItemCortesia("Donut Ninho", 3) }, new[] { L(Ninho, "DONUT NINHO", "Donuts Clássicos", 1290, 1) }, Norm);
        checar(r.PorNome.GetValueOrDefault(Norm("DONUT NINHO")) == 1 && r.Faltando.Count == 0,
            "CC-4 cupom antigo (sem id) casa pelo nome sem acento e maiuscula, e so cobre o que a cliente levou (1 de 3)");

        r = CoberturaCortesia.Calcular(new[] { new ItemCortesia("DONUT NINHO", 1, null, "outro-id") }, new[] { L(Ninho, "DONUT NINHO", "Donuts Clássicos", 1290, 1) }, Norm);
        checar(r.PorNome.Count == 0 && r.Faltando.Count == 1, "CC-5 cupom novo casa pelo ID: mesmo nome com outro id nao sai de graca");

        r = CoberturaCortesia.Calcular(new[] { new ItemCortesia("Donuts Premium", 1, "Donuts Premium") }, new[] { L(Ninho, "DONUT NINHO", "Donuts Clássicos", 1290, 2) }, Norm);
        checar(r.PorNome.Count == 0 && r.Faltando.Single().Rotulo == "1× Donuts Premium (sabor à escolha)",
            "CC-6 categoria que nao esta na comanda: nada coberto e o rotulo diz 'sabor à escolha'");

        r = CoberturaCortesia.Calcular(cat4, new[] { L(Ninho, "DONUT NINHO", "Donuts Clássicos", 1290, 1), L(Ninho, "DONUT NINHO", "Donuts Clássicos", 1290, 2) }, Norm);
        checar(r.PorNome.GetValueOrDefault(Norm("DONUT NINHO")) == 3, "CC-7 duas linhas do mesmo produto somam (3 de 4)");
    }

    // ── 2. leitura ──────────────────────────────────────────────────────────
    private static void Leitura(Action<bool, string> checar)
    {
        var ler = typeof(Cortesias).GetMethod("LerCortesia", BindingFlags.NonPublic | BindingFlags.Static)!;
        Cortesia Ler(string json) => (Cortesia)ler.Invoke(null, new object?[] { JsonDocument.Parse(json).RootElement.Clone() })!;
        var c = Ler("""{"ok":true,"code":"ADS-7KQ2M9X","store":"American Day Savassi","reason":"donut ruim","customer_name":"Maria","items":[{"categoria":"Donuts Clássicos","quantity":4},{"name":"COOKIE","quantity":1,"product_id":"p-c"}]}""");
        checar(c.Ok && c.Itens.Count == 2 && c.Itens[0].EhCategoria && c.Itens[0].Quantidade == 4 && c.Itens[1].ProdutoId == "p-c"
               && c.Motivo == "donut ruim" && c.Cliente == "Maria" && c.Loja == "American Day Savassi",
            "LC-1 a resposta nova traz categoria, produto com id, motivo e cliente");
        var v = Ler("""{"ok":true,"code":"AD-ABC123","items":[{"name":"AFOGATTO","quantity":1}]}""");
        checar(v.Ok && v.Itens.Single().Nome == "AFOGATTO" && !v.Itens[0].EhCategoria && v.Itens[0].ProdutoId is null,
            "LC-2 a resposta antiga (so nome) continua lida");
        var corpo = JsonDocument.Parse(Cortesias.CorpoDoUso("ADS-1", new[] { new ItemCoberto(Ninho, 2) }, "chave-1", "Ana", "ck-venda")).RootElement;
        checar(corpo.GetProperty("_cobertura")[0].GetProperty("product_id").GetString() == Ninho
               && corpo.GetProperty("_cobertura")[0].GetProperty("quantidade").GetInt32() == 2
               && corpo.GetProperty("_ref").GetString() == "chave-1" && corpo.GetProperty("_venda").GetString() == "ck-venda",
            "LC-3 o corpo do uso leva o que foi zerado (product_id, quantidade), a chave e a venda, nos nomes da RPC");
        foreach (var t in new[] { new ItemCortesia("X", 2).Rotulo, new ItemCortesia("Donuts", 4, "Donuts").Rotulo })
            checar(!t.Contains('—') && !t.Contains('–'), $"LC-4 rotulo sem travessao ({t})");
    }

    // ── 3. cliente contra o Supabase de mentira ─────────────────────────────
    private static async Task Cliente(Action<bool, string> checar)
    {
        using var fake = new FakePostgrest(PortaCliente);
        var nuvem = new Nuvem(fake.Url);
        await nuvem.EntrarAsync("caixa@savassi", "x");
        var cortesias = new Cortesias(nuvem, fake.Url);
        fake.CuponsCortesia["ADS-CAT0004"] = """{"ok":true,"code":"ADS-CAT0004","store":"American Day Savassi","items":[{"categoria":"Donuts Clássicos","quantity":4}]}""";

        var c = await cortesias.ValidarAsync("ADS-CAT0004");
        checar(c.Ok && c.Itens.Single().EhCategoria && fake.ChamadasPorRpc.GetValueOrDefault("pdv_cupom_cortesia_conferir") == 1
               && fake.BearerCupom["pdv_cupom_cortesia_conferir"] == "Bearer tok-fake",
            "CL-1 conferir vai pela porta nova com a SESSAO do terminal (nao a chave publica)");

        var cob = new[] { new ItemCoberto(Ninho, 2), new ItemCoberto(Brig, 2) };
        var u = await cortesias.UsarAsync("ADS-CAT0004", cob, "chave-A", "Ana", "American Day Savassi", "ck-1");
        var corpo = JsonDocument.Parse(fake.UsosCupom["ADS-CAT0004"]).RootElement;
        checar(u.Ok && corpo.GetProperty("_ref").GetString() == "chave-A" && corpo.GetProperty("_cobertura").GetArrayLength() == 2
               && corpo.GetProperty("_venda").GetString() == "ck-1" && corpo.GetProperty("_operador").GetString() == "Ana"
               && fake.BearerCupom["pdv_cupom_cortesia_usar"] == "Bearer tok-fake",
            "CL-2 usar manda o que foi zerado, a chave, o operador e a venda pela sessao do terminal");
        var de_novo = await cortesias.UsarAsync("ADS-CAT0004", cob, "chave-A", "Ana", "American Day Savassi", "ck-1");
        var outro = await cortesias.UsarAsync("ADS-CAT0004", cob, "chave-B", "Bia", "American Day Savassi", "ck-2");
        checar(de_novo.Ok && !outro.Ok && outro.Erro == "already_redeemed",
            "CL-3 a mesma chave devolve ok (reenvio); outro caixa com outra chave leva 'ja usado'");

        // servidor sem o SQL 212: as portas antigas, como na 1.0.43
        fake.CupomAusente = true;
        fake.CuponsCortesia["AD-VELHO1"] = """{"ok":true,"code":"AD-VELHO1","items":[{"name":"DONUT NINHO","quantity":1}]}""";
        var velho = await cortesias.ValidarAsync("AD-VELHO1");
        var usadoVelho = await cortesias.UsarAsync("AD-VELHO1", new[] { new ItemCoberto(Ninho, 1) }, "chave-V", "Ana", "American Day Savassi", null);
        checar(velho.Ok && fake.ChamadasPorRpc.GetValueOrDefault("courtesy_validate") == 1 && fake.BearerCupom["courtesy_validate"] != "Bearer tok-fake"
               && usadoVelho.Ok && fake.Resgatadas.ContainsKey("AD-VELHO1"),
            "CL-4 sem o SQL 212 (404) o caixa cai no courtesy_validate e no courtesy_redeem antigos");
    }

    // ── 4. a fila ───────────────────────────────────────────────────────────
    private static async Task Fila(Action<bool, string> checar)
    {
        checar(Drenagem.TiposComHandler.Contains("cortesia_resgate"), "CF-0 'cortesia_resgate' continua com handler na fila");
        var arquivo = Path.Combine(Path.GetTempPath(), $"cupom-fila-{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir(arquivo);
            cx.Execute("""
                INSERT INTO terminal (id, terminal_uuid, loja_id, loja_nome, cnpj, serie_nfce, ambiente, api_base, criado_em)
                VALUES (1, 'term-cupom', 'loja-1', 'American Day Savassi', '00000000000000', 1, 2, 'http://127.0.0.1:9', @a)
                """, new { a = DateTime.Now.ToString("o") });
            Isolamento.SemNuvem(cx);
            using var fake = new FakePostgrest(PortaCliente + 10);
            var nuvem = new Nuvem(fake.Url);
            await nuvem.EntrarAsync("caixa@savassi", "x");
            using var dren = new Drenagem(nuvem, fake.Url);
            fake.CuponsCortesia["ADS-FILA001"] = """{"ok":true,"code":"ADS-FILA001","items":[{"categoria":"Donuts Clássicos","quantity":4}]}""";
            // exatamente o que ResgatarSilenciosoAsync enfileira quando a rede caiu na hora
            var cobertura = new List<ItemCoberto> { new(Ninho, 4) };
            Caixa.Enfileirar(cx, null, "cortesia_resgate", "ADS-FILA001", "ADS-FILA001",
                new { codigo = "ADS-FILA001", operador = "Ana", loja = "American Day Savassi", cobertura, chave = "chave-fila", venda = "ck-fila" });
            await dren.DrenarAsync();
            var enviado = cx.ExecuteScalar<string?>("SELECT enviado_em FROM outbox WHERE tipo='cortesia_resgate' AND client_key='ADS-FILA001'");
            var corpo = fake.UsosCupom.TryGetValue("ADS-FILA001", out var b) ? JsonDocument.Parse(b).RootElement : default;
            checar(enviado is not null && corpo.ValueKind == JsonValueKind.Object && corpo.GetProperty("_ref").GetString() == "chave-fila"
                   && corpo.GetProperty("_cobertura")[0].GetProperty("quantidade").GetInt32() == 4 && corpo.GetProperty("_venda").GetString() == "ck-fila",
                "CF-1 a fila sobe o uso pela porta nova com a MESMA chave, o que foi zerado e a venda");

            // fila de caixa velho (sem cobertura) e servidor sem o SQL: courtesy_redeem, como sempre
            Caixa.Enfileirar(cx, null, "cortesia_resgate", "AD-ANTIGO", "AD-ANTIGO", new { codigo = "AD-ANTIGO", operador = "Ana", loja = "American Day Savassi" });
            await dren.DrenarAsync();
            checar(fake.Resgatadas.ContainsKey("AD-ANTIGO") && !fake.UsosCupom.ContainsKey("AD-ANTIGO"),
                "CF-2 item antigo da fila (sem cobertura) continua indo pelo courtesy_redeem");
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }

    // ── 5. a tela de verdade ────────────────────────────────────────────────
    private static void NaTela(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"cupom_tela_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        Exception? erro = null;
        try
        {
            Banco.Migrar(arquivo);
            using (var cx = Banco.Abrir(arquivo))
            {
                var agora = DateTime.Now.ToString("o");
                cx.Execute("""
                    INSERT INTO terminal (id, terminal_uuid, loja_id, loja_nome, cnpj, serie_nfce, ambiente, api_base, criado_em)
                    VALUES (1, 'term-cupom-tela', 'loja-1', 'American Day Savassi', '00000000000000', 1, 2, 'http://127.0.0.1:9', @a)
                    """, new { a = agora });
                Isolamento.SemNuvem(cx);
                Operadores.Salvar(cx, "op-cupom", "Cleide", "2468", "operador");
                foreach (var (id, nome, cat, preco) in new[]
                {
                    (Ninho, "DONUT NINHO", "Donuts Clássicos", 1290L), (Brig, "DONUT BRIGADEIRO", "Donuts Clássicos", 1190L),
                    (Pistache, "DONUT PISTACHE", "Donuts Premium", 1890L), (Agua, "AGUA 500ML", "Bebidas", 500L),
                })
                    cx.Execute("""
                        INSERT INTO produto (id, plu, nome, categoria, preco_cent, unidade, ativo, atualizado, csosn, ncm)
                        VALUES (@id, @id, @nome, @cat, @preco, 'UN', 1, @a, '102', '19053100')
                        """, new { id, nome, cat, preco, a = agora });
                Vendas.GravarConfig(cx, "modo_fiscal", "recibo");
            }
            try { HostWpf.Executar(() => Passos(checar)); }
            catch (Exception ex) { erro = ex; }
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
        checar(erro is null, "CT-0 a tela de venda com o cupom subiu e os passos rodaram (" + (erro?.ToString() ?? "ok") + ")");
    }

    private static void Passos(Action<bool, string> checar)
    {
        var host = new Window
        {
            Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0,
        };
        host.Show();
        Operador op; Sessao sessao;
        using (var cx = Banco.Abrir())
        {
            op = Operadores.PorId(cx, "op-cupom")!;
            sessao = Caixa.Abrir(cx, op, Dinheiro.DeReais(100));
        }
        var tela = new Pdv.Telas.Venda(op, sessao);
        host.Content = tela;
        host.UpdateLayout();
        Definir(tela, "_rascunhoOferecido", true);
        var catalogo = Campo<System.Collections.IList>(tela, "_catalogo").Cast<Pdv.Telas.Produto>().ToList();
        var comanda = Campo<List<Pdv.Telas.ItemComanda>>(tela, "_comanda");
        Pdv.Telas.Produto Prod(string id) => catalogo.First(p => p.Id == id);
        void Por(string id, int n) { for (var k = 0; k < n; k++) Invocar(tela, "Adicionar", Prod(id)); }
        int Cob(string id) => comanda.Where(i => i.Produto.Id == id).Sum(i => (int)Invocar(tela, "CoberturaDe", i)!);
        string Total() => Campo<TextBlock>(tela, "TxtTotal").Text;
        (string Titulo, string Texto)? Aplicar(Cortesia c) => ((string, string)?)Invocar(tela, "AplicarCupomNaComanda", c, c.Codigo ?? "");
        void Tirar() => Invocar(tela, "RemoverCortesia", tela, new RoutedEventArgs());

        var cat4 = new Cortesia(true, null, "ADS-TELA001", "American Day Savassi",
            new[] { new ItemCortesia("Donuts Clássicos", 4, "Donuts Clássicos") }, "donut ruim", "Maria");

        // o caso do dono: ela escolheu 2 ninho e 2 brigadeiro e levou 1 pistache pago
        Por(Ninho, 2); Por(Brig, 2); Por(Pistache, 1);
        host.UpdateLayout();
        var recado = Aplicar(cat4);
        host.UpdateLayout();
        checar(recado is null && Cob(Ninho) == 2 && Cob(Brig) == 2 && Cob(Pistache) == 0,
            $"CT-1 4 donuts classicos de sabores escolhidos saem de graca; o premium nao ({Cob(Ninho)}/{Cob(Brig)}/{Cob(Pistache)})");
        checar(Total() == new Dinheiro(1890).Formatado(), $"CT-1 a venda cobra so o pistache ({Total()})");
        var itens = Campo<TextBlock>(tela, "TxtCortesiaItens").Text;
        checar(Campo<TextBlock>(tela, "TxtCortesiaTitulo").Text == "Cortesia ADS-TELA001 aplicada" && itens.Contains("grátis")
               && !itens.Contains('—'), $"CT-1 a caixa da cortesia diz o codigo e os gratis ({itens})");
        var paraServidor = (List<ItemCoberto>)Invocar(tela, "CoberturaParaOServidor")!;
        checar(paraServidor.Count == 2 && paraServidor.Sum(x => x.Quantidade) == 4 && paraServidor.All(x => x.ProdutoId is Ninho or Brig),
            "CT-2 o que vai para o servidor conferir: 2 ninho e 2 brigadeiro, por id");
        Tirar();

        recado = Aplicar(cat4 with { Loja = "American Day Castelo" });
        checar(recado is { Titulo: "Cupom de outra loja" } r1 && r1.Texto.Split('\n').Length == 1 && Cob(Ninho) == 0,
            "CT-3 cupom de outra loja: recado de uma linha e nada sai de graca");

        recado = Aplicar(new Cortesia(true, null, "ADS-TELA002", "American Day Savassi",
            new[] { new ItemCortesia("Bebidas", 1, "Bebidas") }));
        checar(recado is { Titulo: "Faltam itens do cupom" } r2 && r2.Texto.Contains("1× Bebidas (sabor à escolha)") && r2.Texto.Split('\n').Length <= 4,
            "CT-4 categoria que nao esta na comanda: pede para passar o item antes");

        recado = Aplicar(new Cortesia(false, "loja_errada", null, "American Day Castelo", Array.Empty<ItemCortesia>()));
        checar(recado is { } r3 && r3.Texto == "Este cupom é da loja American Day Castelo. Só pode ser usado lá.",
            "CT-5 o servidor recusou pela loja: o caixa diz qual loja");

        // comanda toda em cortesia: o cupom e queimado no servidor com o que foi zerado
        Invocar(tela, "EsvaziarComanda");
        Por(Ninho, 1); Por(Brig, 1);
        using var fake = new FakePostgrest(PortaTela);
        var nuvem = new Nuvem(fake.Url);
        Task.Run(() => nuvem.EntrarAsync("caixa@savassi", "x")).GetAwaiter().GetResult();
        Definir(tela, "_cortesias", new Cortesias(nuvem, fake.Url));
        fake.CuponsCortesia["ADS-TELA003"] = """{"ok":true,"code":"ADS-TELA003","items":[{"categoria":"Donuts Clássicos","quantity":2}]}""";
        recado = Aplicar(new Cortesia(true, null, "ADS-TELA003", "American Day Savassi", new[] { new ItemCortesia("Donuts Clássicos", 2, "Donuts Clássicos") }));
        host.UpdateLayout();
        checar(recado is null && Total() == new Dinheiro(0).Formatado(), $"CT-6 comanda toda em cortesia: total zero ({Total()})");
        Task? resgate = null;
        Roteiro(host, d => Clicar(Descendentes<Button>(d).First()));
        var quadro = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke((Action)(() =>
        {
            resgate = (Task)Invocar(tela, "ResgatarEConcluirBrindeAsync")!;
            resgate.ContinueWith(_ => quadro.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
        }));
        var limite = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        limite.Tick += (_, _) => { limite.Stop(); quadro.Continue = false; };
        limite.Start();
        Dispatcher.PushFrame(quadro);
        limite.Stop();
        var corpo = fake.UsosCupom.TryGetValue("ADS-TELA003", out var b) ? JsonDocument.Parse(b).RootElement : default;
        checar(resgate?.IsCompleted == true && corpo.ValueKind == JsonValueKind.Object && corpo.GetProperty("_cobertura").GetArrayLength() == 2
               && corpo.GetProperty("_venda").ValueKind == JsonValueKind.Null && comanda.Count == 0,
            "CT-6 o cupom foi usado no servidor com ninho e brigadeiro, sem venda, e a comanda limpou");
        host.Close();
    }

    // ── ajudantes (mesmo jeito de TestesComandaTela) ───────────────────────
    private static T Campo<T>(object alvo, string nome) => (T)alvo.GetType().GetField(nome, P)!.GetValue(alvo)!;
    private static void Definir(object alvo, string nome, object valor) => alvo.GetType().GetField(nome, P)!.SetValue(alvo, valor);
    private static object? Invocar(object alvo, string metodo, params object?[] args)
        => alvo.GetType().GetMethods(P).First(m => m.Name == metodo && m.GetParameters().Length == args.Length).Invoke(alvo, args);
    private static void Clicar(Button b) => b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private static IEnumerable<T> Descendentes<T>(DependencyObject raiz) where T : DependencyObject
    {
        var fila = new Queue<DependencyObject>();
        fila.Enqueue(raiz);
        while (fila.Count > 0)
        {
            var d = fila.Dequeue();
            if (d is T t) yield return t;
            var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
            for (var k = 0; k < n; k++) fila.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(d, k));
        }
    }

    /// <summary>Clica no primeiro botao de cada dialogo novo que abrir sobre o host (aviso final).</summary>
    private static void Roteiro(Window host, params Action<Window>[] passos)
    {
        var vistos = new HashSet<Window>();
        var i = 0;
        var tentativas = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            if (i >= passos.Length || ++tentativas > 160) { timer.Stop(); return; }
            var d = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w != host && w.IsVisible && !vistos.Contains(w));
            if (d is null) return;
            vistos.Add(d);
            d.UpdateLayout();
            passos[i++](d);
        };
        timer.Start();
    }
}
