using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// COMANDA ABERTA NO PDV (13/10/2026). Pedido do dono: "agora vamos adicionar a funcao comanda no
/// PDV, abre a comanda e fecha pra pagamento.. e a comanda tambem permite solicitar itens da cozinha!"
///
/// No codigo a entidade se chama CONTA ABERTA (tabelas conta_aberta e conta_lote), porque "comanda"
/// ja e o nome do carrinho da tela (ItemComanda, comanda_rascunho). Esta suite cobre so o NUCLEO:
///  · abrir, gravar, soltar, carregar, numerar, cancelar;
///  · pedir a cozinha por LOTE (um pedido imutavel por toque, id "<contaId>-<n>", a TV grava
///    "pdv-<contaId>-<n>"), e o estado do lote lido da outbox;
///  · fechar para pagamento = Vendas.Finalizar de sempre: UMA venda, uma vez, e os itens que ja
///    foram para a cozinha NUNCA vao de novo (dupla contagem);
///  · o rastro que sai do terminal (conta_evento) e o turno de teste que nao cruza com o real;
///  · textos de uma linha, sem travessao.
/// A tela (Venda, DialogoComandas, Pagamento) vem na etapa seguinte.
/// </summary>
public static class TestesContaAberta
{
    private const int PortaKds = 4731;
    private const int PortaNuvem = 4732;

    // catalogo do teste
    private const string Ninho = "p-ninho", Homer = "p-homer", Agua = "p-agua", Combo = "p-combo4", Grupo = "g-donuts";

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        Textos(checar);
        ItemRascunhoAntigo(checar);

        var arquivo = Path.Combine(Path.GetTempPath(), $"conta_teste_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir(arquivo);
            var op = new Operador("op-conta", "Cleide", "operador");
            var gerente = new Operador("ger-conta", "Marcos", "gerente");
            var gerente2 = new Operador("ger-conta-2", "Paula", "gerente");
            Operadores.Salvar(cx, op.Id, op.Nome, "2468", "operador");
            Operadores.Salvar(cx, gerente.Id, gerente.Nome, "1357", "gerente");
            Operadores.Salvar(cx, gerente2.Id, gerente2.Nome, "9753", "gerente");
            SemearCatalogo(cx);
            var sessao = Caixa.Abrir(cx, op, Dinheiro.DeReais(100));

            Esquema(cx, checar);
            AbrirENumerar(cx, sessao, op, checar);
            GravarSoltarCarregar(cx, sessao, op, checar);
            RascunhoFicaVazio(cx, sessao, op, checar);
            PedirCozinha(cx, sessao, op, checar);
            Barreiras(cx, sessao, op, checar);
            EstadoDoLote(cx, sessao, op, checar);
            LotePerdido(cx, sessao, op, checar);
            Retirar(cx, sessao, op, gerente, gerente2, checar);
            Cancelar(cx, sessao, op, gerente, gerente2, checar);
            await FecharComoVendaAsync(cx, sessao, op, checar);
            Fechando(cx, sessao, op, checar);
            Cortesia(cx, sessao, op, checar);
            CortesiaInteiraFechaSemVenda(cx, sessao, op, gerente, checar);
            LotePerdidoNaoEProducao(cx, sessao, op, gerente, checar);
            TurnoDeTesteCruzado(cx, sessao, op, checar);
            FechamentoDoCaixa(cx, sessao, op, gerente, checar);
            await EventoNaFilaAsync(cx, sessao, op, gerente, checar);
            await DrenagemSemNuvemAsync(cx, sessao, op, gerente, checar);
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }

        TurnoDeTesteEncerrado(checar);
        Fontes(checar);
    }

    // ── CA-22: textos ───────────────────────────────────────────────────────
    private static void Textos(Action<bool, string> checar)
    {
        var todos = ContasAbertas.Textos.Todos();
        checar(todos.Count >= 30, $"CA-22 os textos da comanda estao reunidos em ContasAbertas.Textos ({todos.Count})");
        foreach (var t in todos)
            checar(!t.Contains((char)0x2014) && !t.Contains((char)0x2013) && !t.Contains('\n') && t.Length <= 80,
                $"CA-22 texto de uma linha, sem travessao e curto: \"{t}\"");
        checar(ContasAbertas.Textos.Comandas == "Comandas" && ContasAbertas.Textos.FecharConta == "Fechar conta"
               && ContasAbertas.Textos.Guardar == "Guardar" && ContasAbertas.Textos.NovaComanda == "Nova comanda"
               && ContasAbertas.Textos.ItemJaPedido == "Item já pedido" && ContasAbertas.Textos.MandarDeNovo == "Mandar de novo",
            "CA-22 os botoes sao os do desenho: Comandas, Nova comanda, Guardar, Fechar conta, Item já pedido, Mandar de novo");
        checar(ContasAbertas.Textos.ComandaAberta(12) == "Comanda 12 aberta."
               && ContasAbertas.Textos.CozinhaN(2) == "Cozinha 2"
               && ContasAbertas.Textos.PedidoNaFila == "Pedido na fila da cozinha."
               && ContasAbertas.Textos.CozinhaRecebeu == "Cozinha recebeu o pedido."
               && ContasAbertas.Textos.PedidoNaCozinhaGuardada(12) == "Pedido na cozinha. Comanda 12 guardada."
               && ContasAbertas.Textos.NadaNovo == "Nada novo para a cozinha."
               && ContasAbertas.Textos.JaFoiFechada(12) == "Comanda 12 já foi fechada."
               && ContasAbertas.Textos.ComandasAbertas(2, Dinheiro.DeReais(85)) == $"Comandas abertas: 2 ({Dinheiro.DeReais(85).Formatado()})"
               && ContasAbertas.Textos.CarrinhoTem(3) == "O carrinho tem 3 itens."
               && ContasAbertas.Textos.CarrinhoTem(1) == "O carrinho tem 1 item."
               && ContasAbertas.Textos.AbrirComItens(3) == "Abrir com os 3 itens"
               && ContasAbertas.Textos.CupomJaNaComanda(13) == "Esse cupom já está na comanda 13."
               && ContasAbertas.Textos.CancelarComanda(12) == "Cancelar comanda 12"
               && ContasAbertas.Textos.JaEstaAberta("Mesa 5") == "Mesa 5 já está aberta"
               && ContasAbertas.Textos.Mesa(3) == "Mesa 3",
            "CA-22 os textos com numero saem como o desenho pede");
        checar(ContasAbertas.Textos.NaTela == "na tela" && ContasAbertas.Textos.NaTv == "na TV"
               && ContasAbertas.Textos.SeloFila == "⚠ fila" && ContasAbertas.Textos.SeloNaoFoi == "⚠ não foi"
               && ContasAbertas.Textos.ConfiraMaquininha == "⚠ confira a maquininha"
               && ContasAbertas.Textos.EmUsoNoOutroCaixa == "em uso no outro caixa",
            "CA-22 os selos da lista: na tela, na TV, fila, nao foi, confira a maquininha, em uso no outro caixa");
        checar(ContasAbertas.Textos.Selo(EstadoLote.NaTv) == "na TV" && ContasAbertas.Textos.Selo(EstadoLote.NaFila) == "⚠ fila"
               && ContasAbertas.Textos.Selo(EstadoLote.NaoFoi) == "⚠ não foi" && ContasAbertas.Textos.Selo(EstadoLote.Teste) == "",
            "CA-22 Selo(estado) e o texto do selo; lote de teste nao tem selo (o rotulo 'chave' morreu: e sempre 'nao foi')");
    }

    // ── CA-1: o formato do item ─────────────────────────────────────────────
    private static void ItemRascunhoAntigo(Action<bool, string> checar)
    {
        const string antigo = """
            [{"ProdutoId":"p1","Plu":"4","Nome":"DONUT NINHO","Categoria":"Donuts","PrecoCent":1200,"QtdMilesimos":2000,"Unidade":"UN","Ncm":null,"Cest":null,"Csosn":"102","Origem":0,"Foto":null,"EscolhasJson":null}]
            """;
        var itens = JsonSerializer.Deserialize<List<ItemRascunho>>(antigo);
        checar(itens is { Count: 1 } && itens[0].LinhaId is null && itens[0].CozinhaMilesimos == 0,
            "CA-1 ItemRascunho de exe antigo (sem LinhaId e CozinhaMilesimos) desserializa com os dois no default");
        var novo = new ItemRascunho("p1", "4", "DONUT NINHO", "Donuts", 1200, 3000, "UN", null, null, "102", 0, null, null,
            LinhaId: "abc123", CozinhaMilesimos: 2000);
        var volta = JsonSerializer.Deserialize<List<ItemRascunho>>(JsonSerializer.Serialize(new[] { novo }))!;
        checar(volta[0].LinhaId == "abc123" && volta[0].CozinhaMilesimos == 2000,
            "CA-1 ItemRascunho novo: LinhaId e CozinhaMilesimos vao e voltam do disco");
    }

    // ── o esquema ────────────────────────────────────────────────────────────
    private static void Esquema(SqliteConnection cx, Action<bool, string> checar)
    {
        var tabelas = cx.Query<string>("SELECT name FROM sqlite_master WHERE type='table'").ToList();
        checar(tabelas.Contains("conta_aberta") && tabelas.Contains("conta_lote"),
            "esquema: conta_aberta e conta_lote existem (CREATE TABLE IF NOT EXISTS, sem ALTER em tabela velha)");
        var indices = cx.Query<string>("SELECT name FROM sqlite_master WHERE type='index'").ToList();
        checar(indices.Contains("uq_conta_aberta_numero") && indices.Contains("ix_conta_aberta_status"),
            "esquema: indice unico parcial do numero e indice por status");
        Banco.Migrar(Banco.CaminhoForcado);
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name='conta_aberta'") == 1,
            "esquema: Migrar de novo e idempotente");
        var colunas = cx.Query<string>("SELECT name FROM pragma_table_info('conta_aberta')").ToList();
        foreach (var c in new[] { "teste", "fechando_em", "ultimo_toque_em", "venda_id", "cancel_autorizador", "cancel_automatico", "dono", "cortesia_codigo" })
            checar(colunas.Contains(c), $"esquema: conta_aberta tem a coluna {c}");
        var colLote = cx.Query<string>("SELECT name FROM pragma_table_info('conta_lote')").ToList();
        foreach (var c in new[] { "itens_retirados_json", "substituido_por", "homologacao" })
            checar(colLote.Contains(c), $"esquema: conta_lote tem a coluna {c}");
    }

    // ── CA-2: abrir e numerar ───────────────────────────────────────────────
    private static void AbrirENumerar(SqliteConnection cx, Sessao sessao, Operador op, Action<bool, string> checar)
    {
        checar(ContasAbertas.ProximoNumero(cx) == 1, "CA-2 a primeira comanda do dia e a 1");
        var c1 = ContasAbertas.Abrir(cx, sessao, op, "Mesa 5");
        checar(c1.Numero == 1 && c1.Nome == "Mesa 5" && c1.Status == "aberta" && c1.Consumo == "loja" && !c1.Teste
               && c1.Itens.Count == 0 && c1.BusinessDate == sessao.BusinessDate && c1.SessaoAbertura == sessao.Id
               && c1.OperadorId == op.Id && c1.Dono is { Length: > 0 },
            "CA-2 Abrir grava aberta, numero curto, consumo na loja, teste pelo modo e dono = este processo");
        checar(Guid.TryParseExact(c1.Id, "N", out _), "CA-2 o id da conta e um Guid N (serve de prefixo do id do lote)");
        var c2 = ContasAbertas.Abrir(cx, sessao, op, "Joana", DestinoCozinha.Viagem);
        checar(c2.Numero == 2 && c2.Consumo == "viagem", "CA-2 a segunda e a 2, para viagem");
        checar(c1.Titulo == "1 · Mesa 5" && ContasAbertas.Abrir(cx, sessao, op, null).Titulo == "3",
            "CA-2 o titulo e 'numero · nome' (so o numero sem nome)");

        // comanda que atravessou a noite: numero ainda aberto nao se repete
        cx.Execute("UPDATE conta_aberta SET business_date = '2000-01-01' WHERE numero = 3");
        checar(ContasAbertas.ProximoNumero(cx) == 4, "CA-2 ProximoNumero pula o numero ainda aberto de outro dia");
        var dup = false;
        try
        {
            cx.Execute("""
                INSERT INTO conta_aberta (id, numero, status, itens_json, operador_id, sessao_abertura, business_date, aberta_em, atualizado_em, ultimo_toque_em)
                VALUES ('dup', 1, 'aberta', '[]', 'x', 'y', '2000-01-01', 'z', 'z', 'z')
                """);
        }
        catch (SqliteException) { dup = true; }
        checar(dup, "CA-2 o indice unico parcial recusa duas comandas ABERTAS com o mesmo numero");
        checar(ContasAbertas.Listar(cx, teste: false).Select(c => c.Numero).SequenceEqual(new long[] { 1, 2, 3 }),
            "CA-2 Listar devolve as abertas do modo, por numero");
        checar(ContasAbertas.Listar(cx, teste: true).Count == 0, "CA-2 Listar no modo de teste nao mostra comanda real");

        // nome duplicado (comparacao normalizada)
        checar(ContasAbertas.PorNome(cx, "  mesa 5 ", teste: false)?.Numero == 1 && ContasAbertas.PorNome(cx, "Mesa 9", teste: false) is null,
            "CA-2 PorNome acha 'Mesa 5' sem caixa, acento ou espaco duplo");
        checar(ContasAbertas.NormalizarNome("  Café   da  Manhã ") == "cafe da manha", "CA-2 NormalizarNome tira acento, caixa e espacos duplos");
        foreach (var c in ContasAbertas.Listar(cx, teste: false)) ContasAbertas.Cancelar(cx, c.Id, op, null, null);
        checar(ContasAbertas.ContarAbertas(cx, teste: false) == 0, "CA-2 ContarAbertas (o numero do botao Comandas) zera depois de cancelar as tres");
    }

    // ── CA-3: gravar, soltar, carregar ──────────────────────────────────────
    private static void GravarSoltarCarregar(SqliteConnection cx, Sessao sessao, Operador op, Action<bool, string> checar)
    {
        var c = ContasAbertas.Abrir(cx, sessao, op, "Balcão");
        ContasAbertas.Gravar(cx, c.Id, new List<ItemRascunho> { Item(Ninho, "DONUT NINHO", 1200, 2000), Item(Agua, "AGUA 500ML", 500, 1000) }, null, null, op);
        var lida = ContasAbertas.Ler(cx, c.Id)!;
        checar(lida.Itens.Count == 2 && lida.Itens.All(i => i.LinhaId is { Length: > 0 }) && lida.ValorTabela.Centavos == 2900,
            "CA-3 Gravar guarda os itens, cada linha ganha LinhaId, e o valor de tabela e preco x quantidade");
        ContasAbertas.Gravar(cx, c.Id, new List<ItemRascunho>(), null, null, op);
        lida = ContasAbertas.Ler(cx, c.Id)!;
        checar(lida.Status == "aberta" && lida.Itens.Count == 0, "CA-3 Gravar com lista vazia NAO cancela a comanda (vazia guardada fica na lista)");
        ContasAbertas.Soltar(cx, c.Id);
        checar(ContasAbertas.Ler(cx, c.Id)!.Dono is null, "CA-3 Soltar zera o dono");
        checar(ContasAbertas.Carregar(cx, c.Id)?.Dono is { Length: > 0 }, "CA-3 Carregar assume a conta (dono = este processo)");

        // outro Pdv.exe VIVO: nao carrega nem grava; processo morto libera
        using (var outro = OutroProcessoVivo())
        {
            cx.Execute("UPDATE conta_aberta SET dono = @d WHERE id = @id", new { d = outro.Dono, id = c.Id });
            var l = ContasAbertas.Ler(cx, c.Id)!;
            checar(ContasAbertas.EmUsoPorOutroPdv(l) && ContasAbertas.Carregar(cx, c.Id) is null,
                "CA-3 conta carregada noutro Pdv.exe vivo: 'em uso no outro caixa', nao carrega");
            var recusou = false;
            try { ContasAbertas.Gravar(cx, c.Id, new List<ItemRascunho> { Item(Agua, "AGUA", 500, 1000) }, null, null, op); }
            catch (InvalidOperationException) { recusou = true; }
            checar(recusou && ContasAbertas.Ler(cx, c.Id)!.Itens.Count == 0, "CA-3 Gravar numa conta de outro Pdv.exe vivo recusa (nao sobrescreve a conta dele)");
        }
        cx.Execute("UPDATE conta_aberta SET dono = '999999:1' WHERE id = @id", new { id = c.Id });
        checar(!ContasAbertas.EmUsoPorOutroPdv(ContasAbertas.Ler(cx, c.Id)!) && ContasAbertas.Carregar(cx, c.Id) is not null,
            "CA-3 dono morto (reinicio do caixa) libera a conta");
        checar(ContasAbertas.AbertaPorEngano(ContasAbertas.Ler(cx, c.Id)!, DateTime.Now) == false,
            "CA-3 conta com nome nao e 'aberta por engano'");
        var engano = ContasAbertas.Abrir(cx, sessao, op, null);
        checar(ContasAbertas.AbertaPorEngano(engano, DateTime.Now) && !ContasAbertas.AbertaPorEngano(engano, DateTime.Now.AddMinutes(2)),
            "CA-3 sem nome, sem item e guardada em menos de 1 min = aberta por engano (cancela sozinha); depois de 1 min fica");
        ContasAbertas.Cancelar(cx, engano.Id, op, null, null);
        ContasAbertas.Cancelar(cx, c.Id, op, null, null);
    }

    // ── CA-4: a conta carregada nao grava rascunho ──────────────────────────
    private static void RascunhoFicaVazio(SqliteConnection cx, Sessao sessao, Operador op, Action<bool, string> checar)
    {
        var c = ContasAbertas.Abrir(cx, sessao, op, "Mesa 7", itens: new List<ItemRascunho> { Item(Ninho, "DONUT NINHO", 1200, 1000) });
        checar(c.Itens.Count == 1 && Rascunho.Ler(cx, sessao.Id) is null,
            "CA-4 abrir com itens grava na conta e comanda_rascunho continua vazia (Rascunho.Ler devolve null)");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM comanda_rascunho") == 0, "CA-4 nada em comanda_rascunho com a conta carregada");
        ContasAbertas.Cancelar(cx, c.Id, op, "teste", OperadorPorId(cx, "ger-conta"));
    }

    // ── CA-5 / CA-6: pedir a cozinha por lote ───────────────────────────────
    private static void PedirCozinha(SqliteConnection cx, Sessao sessao, Operador op, Action<bool, string> checar)
    {
        Vendas.GravarConfig(cx, PedidoCozinha.ChaveToken, "chave-da-savassi-0123456789abcdef");
        var itens = new List<ItemRascunho>
        {
            Item(Ninho, "DONUT NINHO", 1200, 2000),
            Item(Combo, "COMBO 4 DONUTS", 4000, 1000, Combos.ParaJson(new[]
            {
                new Escolha(Ninho, "4", "DONUT NINHO", Grupo, 2, "Donuts"), new Escolha(Homer, "1", "DONUT HOMER", Grupo, 2, "Donuts"),
            })),
        };
        var c = ContasAbertas.Abrir(cx, sessao, op, "Mesa 5", itens: itens);
        checar(ContasAbertas.Pendentes(cx, c).Sum(p => p.Milesimos) == 3000, "CA-5 antes do primeiro pedido tudo esta pendente (3 unidades)");

        var lote1 = ContasAbertas.PedirCozinha(cx, c.Id, null, op, homologacao: false);
        checar(lote1 is not null && lote1.Id == $"{c.Id}-1" && lote1.N == 1 && lote1.Consumo == "loja" && lote1.Itens.Count == 2,
            "CA-5 o primeiro lote tem id '<contaId>-1' (a TV grava pdv-<contaId>-1) com os 2 itens pendentes");
        var depois = ContasAbertas.Ler(cx, c.Id)!;
        checar(depois.Itens.All(i => i.CozinhaMilesimos == i.QtdMilesimos) && ContasAbertas.Pendentes(cx, depois).Count == 0,
            "CA-5 depois de pedir, CozinhaMilesimos = Qtd em cada linha e nada fica pendente");
        var fila = cx.Query("SELECT ref_id, client_key, payload FROM outbox WHERE tipo = @t AND ref_id = @r",
            new { t = PedidoCozinha.TipoNaFila, r = lote1!.Id }).ToList();
        checar(fila.Count == 1 && (string)fila[0].client_key == "cozinha:" + lote1.Id,
            "CA-5 UMA linha kds_cozinha por lote, ref_id = loteId, client_key 'cozinha:'+loteId");
        var corpo = JsonDocument.Parse((string)fila[0].payload).RootElement;
        checar(corpo.GetProperty("vendaId").GetString() == lote1.Id
               && corpo.GetProperty("numero").GetString() == $"C{c.Numero}-1"
               && corpo.GetProperty("cliente").GetString() == "Mesa 5"
               && corpo.GetProperty("consumo").GetString() == "loja"
               && corpo.GetProperty("operador").GetString() == "Cleide"
               && corpo.GetProperty("origem").GetString() == "comanda"
               && corpo.GetProperty("comanda").GetProperty("lote").GetInt32() == 1
               && corpo.GetProperty("comanda").GetProperty("numero").GetString() == c.Numero.ToString()
               && corpo.GetProperty("comanda").GetProperty("id").GetString() == c.Id
               && corpo.GetProperty("comanda").GetProperty("nome").GetString() == "Mesa 5",
            "CA-5 corpo do lote: vendaId = loteId, numero 'C<n>-1' (cabe na regex da TV), cliente = nome, origem 'comanda' e o bloco comanda");
        var its = corpo.GetProperty("itens").EnumerateArray().ToList();
        var sabores = its[1].GetProperty("sabores").EnumerateArray().Select(s => $"{s.GetProperty("qtd").GetInt32()}x {s.GetProperty("nome").GetString()}").ToList();
        checar(its[0].GetProperty("qtd").GetInt32() == 2 && its[0].GetProperty("nome").GetString() == "DONUT NINHO"
               && its[1].GetProperty("qtd").GetInt32() == 1 && sabores.SequenceEqual(new[] { "2x DONUT NINHO", "2x DONUT HOMER" }),
            "CA-5 os itens vao no formato de hoje (combo com sabores por unidade)");
        checar(!((string)fila[0].payload).Contains("valor", StringComparison.OrdinalIgnoreCase) && !((string)fila[0].payload).Contains("chave-da-savassi"),
            "CA-5 o lote nao leva preco nem a chave: e so producao");
        checar(cx.ExecuteScalar<string>("SELECT detalhe FROM auditoria WHERE evento='conta_lote_pedido' ORDER BY id DESC LIMIT 1")!.Contains("lote=1"),
            "CA-5 auditoria conta_lote_pedido com o lote");

        // CA-6: pedir de novo sem item novo = nenhum lote
        checar(ContasAbertas.PedirCozinha(cx, c.Id, null, op, false) is null
               && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM conta_lote WHERE conta_id=@c", new { c = c.Id }) == 1,
            "CA-6 pedir de novo sem item novo nao cria lote ('Nada novo para a cozinha.')");

        // item novo: so ele vai, no lote 2
        var mais = depois.Itens.ToList();
        mais.Add(Item(Agua, "AGUA 500ML", 500, 3000));
        var linhaNinho = mais[0];
        mais[0] = linhaNinho with { QtdMilesimos = 3000 };   // +1 Ninho: 1 pendente da linha
        ContasAbertas.Gravar(cx, c.Id, mais, null, null, op);
        var pend = ContasAbertas.Pendentes(cx, ContasAbertas.Ler(cx, c.Id)!);
        checar(pend.Count == 2 && pend.Sum(p => p.Milesimos) == 4000, $"CA-5 pendentes = Qtd - CozinhaMilesimos (1 Ninho + 3 aguas = 4)");
        var lote2 = ContasAbertas.PedirCozinha(cx, c.Id, DestinoCozinha.Viagem, op, false)!;
        var corpo2 = JsonDocument.Parse(cx.ExecuteScalar<string>("SELECT payload FROM outbox WHERE tipo=@t AND ref_id=@r", new { t = PedidoCozinha.TipoNaFila, r = lote2.Id })!).RootElement;
        var its2 = corpo2.GetProperty("itens").EnumerateArray().Select(i => $"{i.GetProperty("qtd").GetInt32()}x {i.GetProperty("nome").GetString()}").ToList();
        checar(lote2.Id == $"{c.Id}-2" && lote2.N == 2 && its2.SequenceEqual(new[] { "1x DONUT NINHO", "3x AGUA 500ML" })
               && corpo2.GetProperty("numero").GetString() == $"C{c.Numero}-2" && corpo2.GetProperty("consumo").GetString() == "viagem",
            "CA-5 o lote 2 leva SO o que ficou pendente (1 Ninho e 3 aguas), id '<contaId>-2', destino escolhido");
        checar(ContasAbertas.Lotes(cx, c.Id).Select(l => l.N).SequenceEqual(new[] { 1, 2 }), "CA-5 Lotes lista os lotes da conta em ordem");

        // homologacao: lote no banco, nada na outbox
        var h = ContasAbertas.Abrir(cx, sessao, op, "Teste", itens: new List<ItemRascunho> { Item(Agua, "AGUA", 500, 1000) });
        var loteH = ContasAbertas.PedirCozinha(cx, h.Id, null, op, homologacao: true)!;
        checar(loteH.Homologacao && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo=@t AND ref_id=@r", new { t = PedidoCozinha.TipoNaFila, r = loteH.Id }) == 0
               && ContasAbertas.EstadoDoLote(cx, loteH.Id) == EstadoLote.Teste,
            "CA-6 homologacao ou turno de teste: o lote existe no banco (homologacao=1), nada entra na outbox e o estado e Teste");
        ContasAbertas.Cancelar(cx, h.Id, op, "teste", OperadorPorId(cx, "ger-conta"));
        ContasAbertas.Cancelar(cx, c.Id, op, "teste", OperadorPorId(cx, "ger-conta"));
    }

    // ── CA-7: barreiras ─────────────────────────────────────────────────────
    private static void Barreiras(SqliteConnection cx, Sessao sessao, Operador op, Action<bool, string> checar)
    {
        var incompleto = ContasAbertas.Abrir(cx, sessao, op, "Mesa 1", itens: new List<ItemRascunho>
        {
            Item(Combo, "COMBO 4 DONUTS", 4000, 1000, Combos.ParaJson(new[] { new Escolha(Ninho, "4", "DONUT NINHO", Grupo, 1, "Donuts") })),
        });
        var b = ContasAbertas.Barreira(cx, incompleto);
        checar(b is not null && b.Contains("sabores", StringComparison.Ordinal), $"CA-7 combo incompleto barra: {b}");
        var recusou = false;
        try { ContasAbertas.PedirCozinha(cx, incompleto.Id, null, op, false); }
        catch (InvalidOperationException e) { recusou = e.Message.Contains("sabores", StringComparison.Ordinal); }
        checar(recusou && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM conta_lote WHERE conta_id=@c", new { c = incompleto.Id }) == 0,
            "CA-7 PedirCozinha recusa o combo incompleto e nao cria lote");

        var sumiu = ContasAbertas.Abrir(cx, sessao, op, "Mesa 2", itens: new List<ItemRascunho> { Item("p-que-saiu", "DONUT ANTIGO", 900, 1000) });
        var b2 = ContasAbertas.Barreira(cx, sumiu);
        checar(b2 is not null && b2.Contains(ContasAbertas.Textos.SaiuDoCardapio, StringComparison.Ordinal),
            $"CA-7 linha fora do cardapio barra o pedido e o fechamento: {b2}");
        checar(ContasAbertas.ForaDoCardapio(cx, sumiu).Select(i => i.Nome).SequenceEqual(new[] { "DONUT ANTIGO" }),
            "CA-7 ForaDoCardapio lista a linha cujo produto nao esta mais no catalogo");
        var ok = ContasAbertas.Abrir(cx, sessao, op, "Mesa 3", itens: new List<ItemRascunho> { Item(Agua, "AGUA", 500, 1000) });
        checar(ContasAbertas.Barreira(cx, ok) is null, "CA-7 conta com item do cardapio e sem combo incompleto passa");
        foreach (var id in new[] { incompleto.Id, sumiu.Id, ok.Id }) ContasAbertas.Cancelar(cx, id, op, "teste", OperadorPorId(cx, "ger-conta"));
    }

    // ── CA-25: o estado do lote vem da outbox ───────────────────────────────
    private static void EstadoDoLote(SqliteConnection cx, Sessao sessao, Operador op, Action<bool, string> checar)
    {
        var c = ContasAbertas.Abrir(cx, sessao, op, "Estado", itens: new List<ItemRascunho> { Item(Agua, "AGUA", 500, 1000) });
        var lote = ContasAbertas.PedirCozinha(cx, c.Id, null, op, false)!;
        checar(ContasAbertas.EstadoDoLote(cx, lote.Id) == EstadoLote.NaFila, "CA-25 sem enviado_em = NaFila");
        cx.Execute("UPDATE outbox SET enviado_em=@e WHERE tipo=@t AND ref_id=@r", new { e = DateTime.Now.ToString("o"), t = PedidoCozinha.TipoNaFila, r = lote.Id });
        checar(ContasAbertas.EstadoDoLote(cx, lote.Id) == EstadoLote.NaTv, "CA-25 enviado_em sem 'descartado' = NaTv");
        cx.Execute("UPDATE outbox SET ultimo_erro='descartado sem enviar: pedido da cozinha com mais de 3 h' WHERE tipo=@t AND ref_id=@r", new { t = PedidoCozinha.TipoNaFila, r = lote.Id });
        checar(ContasAbertas.EstadoDoLote(cx, lote.Id) == EstadoLote.NaoFoi, "CA-25 enviado_em com 'descartado%' = NaoFoi");
        cx.Execute("UPDATE outbox SET enviado_em=NULL, ultimo_erro=NULL, desistido_em=@e WHERE tipo=@t AND ref_id=@r", new { e = DateTime.Now.ToString("o"), t = PedidoCozinha.TipoNaFila, r = lote.Id });
        checar(ContasAbertas.EstadoDoLote(cx, lote.Id) == EstadoLote.NaoFoi, "CA-25 desistido_em = NaoFoi");
        cx.Execute("UPDATE outbox SET desistido_em=NULL, ultimo_erro='HTTP 401: chave da TV da cozinha recusada' WHERE tipo=@t AND ref_id=@r", new { t = PedidoCozinha.TipoNaFila, r = lote.Id });
        checar(ContasAbertas.EstadoDoLote(cx, lote.Id) == EstadoLote.NaoFoi, "CA-25 401/403 na fila = NaoFoi (para o operador e sempre 'nao foi')");
        checar(ContasAbertas.EstadoDoLote(cx, "lote-que-nao-existe") == EstadoLote.Teste, "CA-25 sem linha na outbox = Teste");
        ContasAbertas.Cancelar(cx, c.Id, op, "teste", OperadorPorId(cx, "ger-conta"));
    }

    // ── CA-14: lote perdido e Mandar de novo ────────────────────────────────
    private static void LotePerdido(SqliteConnection cx, Sessao sessao, Operador op, Action<bool, string> checar)
    {
        var c = ContasAbertas.Abrir(cx, sessao, op, "Perdido", itens: new List<ItemRascunho> { Item(Ninho, "DONUT NINHO", 1200, 2000), Item(Agua, "AGUA", 500, 1000) });
        var lote1 = ContasAbertas.PedirCozinha(cx, c.Id, null, op, false)!;
        cx.Execute("UPDATE outbox SET enviado_em=@e, ultimo_erro='descartado sem enviar: pedido da cozinha com mais de 3 h' WHERE tipo=@t AND ref_id=@r",
            new { e = DateTime.Now.ToString("o"), t = PedidoCozinha.TipoNaFila, r = lote1.Id });
        var lida = ContasAbertas.Ler(cx, c.Id)!;
        var pend = ContasAbertas.Pendentes(cx, lida);
        checar(ContasAbertas.EstadoDoLote(cx, lote1.Id) == EstadoLote.NaoFoi && pend.Sum(p => p.Milesimos) == 3000,
            "CA-14 lote perdido: Pendentes conta os itens dele (o botao Cozinha N diz a verdade)");
        checar(ContasAbertas.UltimoLoteNaoChegou(cx, c.Id), "CA-14 UltimoLoteNaoChegou acusa (o botao Cozinha ganha o aviso)");

        var lote2 = ContasAbertas.ReenviarLote(cx, lote1.Id, op);
        checar(lote2.Id == $"{c.Id}-2" && lote2.Itens.Count == 2 && lote2.Itens.Sum(i => i.QtdMilesimos) == 3000,
            "CA-14 Mandar de novo cria o lote n+1 com os itens do lote perdido");
        var velho = ContasAbertas.LerLote(cx, lote1.Id)!;
        checar(velho.SubstituidoPor == lote2.Id, "CA-14 o lote velho fica marcado substituido_por");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo=@t AND ref_id=@r AND enviado_em IS NULL", new { t = PedidoCozinha.TipoNaFila, r = lote2.Id }) == 1,
            "CA-14 o lote novo entra na fila");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM auditoria WHERE evento='conta_lote_reenviado'") >= 1, "CA-14 auditoria conta_lote_reenviado");
        checar(ContasAbertas.Pendentes(cx, ContasAbertas.Ler(cx, c.Id)!).Count == 0, "CA-14 depois do reenvio nada fica pendente");
        checar(ContasAbertas.UltimoLoteNaoChegou(cx, c.Id) && ContasAbertas.EstadoDoLote(cx, lote2.Id) == EstadoLote.NaFila,
            "CA-14 o ultimo lote da conta esta na fila (ainda nao chegou a TV): o botao Cozinha segue com o aviso");
        cx.Execute("UPDATE outbox SET enviado_em=@e WHERE tipo=@t AND ref_id=@r", new { e = DateTime.Now.ToString("o"), t = PedidoCozinha.TipoNaFila, r = lote2.Id });
        checar(!ContasAbertas.UltimoLoteNaoChegou(cx, c.Id), "CA-14 com o ultimo lote na TV o aviso do botao some");
        var naTv = false;
        try { ContasAbertas.ReenviarLote(cx, lote2.Id, op); } catch (InvalidOperationException) { naTv = true; }
        checar(naTv, "CA-14 nao se manda de novo um lote que ainda esta na fila (so o que nao foi)");

        // o pedido pelo botao Cozinha tambem leva o lote perdido e o substitui
        var c2 = ContasAbertas.Abrir(cx, sessao, op, "Perdido 2", itens: new List<ItemRascunho> { Item(Agua, "AGUA", 500, 2000) });
        var l1 = ContasAbertas.PedirCozinha(cx, c2.Id, null, op, false)!;
        cx.Execute("UPDATE outbox SET desistido_em=@e WHERE tipo=@t AND ref_id=@r", new { e = DateTime.Now.ToString("o"), t = PedidoCozinha.TipoNaFila, r = l1.Id });
        var l2 = ContasAbertas.PedirCozinha(cx, c2.Id, null, op, false)!;
        checar(l2.N == 2 && l2.Itens.Sum(i => i.QtdMilesimos) == 2000 && ContasAbertas.LerLote(cx, l1.Id)!.SubstituidoPor == l2.Id,
            "CA-14 o botao Cozinha N leva o que ficou no lote perdido e marca o velho como substituido");
        ContasAbertas.Cancelar(cx, c.Id, op, "teste", OperadorPorId(cx, "ger-conta"));
        ContasAbertas.Cancelar(cx, c2.Id, op, "teste", OperadorPorId(cx, "ger-conta"));
    }

    // ── CA-18: tirar item ja pedido ─────────────────────────────────────────
    private static void Retirar(SqliteConnection cx, Sessao sessao, Operador op, Operador gerente, Operador gerente2, Action<bool, string> checar)
    {
        var c = ContasAbertas.Abrir(cx, sessao, op, "Retira", itens: new List<ItemRascunho> { Item(Ninho, "DONUT NINHO", 1200, 3000), Item(Agua, "AGUA", 500, 1000) });
        var lote = ContasAbertas.PedirCozinha(cx, c.Id, null, op, false)!;
        var lida = ContasAbertas.Ler(cx, c.Id)!;
        var ninho = lida.Itens.First(i => i.ProdutoId == Ninho);

        // acima do pedido: livre
        var livres = lida.Itens.Select(i => i.ProdutoId == Ninho ? i with { QtdMilesimos = 5000 } : i).ToList();
        ContasAbertas.Gravar(cx, c.Id, livres, null, null, op);
        livres = ContasAbertas.Ler(cx, c.Id)!.Itens.Select(i => i.ProdutoId == Ninho ? i with { QtdMilesimos = 3000 } : i).ToList();
        ContasAbertas.Gravar(cx, c.Id, livres, null, null, op);
        checar(ContasAbertas.Ler(cx, c.Id)!.Itens.First(i => i.ProdutoId == Ninho).CozinhaMilesimos == 3000,
            "CA-18 subir e descer ate o que ja foi pedido e livre, e CozinhaMilesimos sobrevive ao Gravar");

        // abaixo do pedido pelo Gravar: recusado (a regra mora no nucleo, nao so na tela)
        var abaixo = ContasAbertas.Ler(cx, c.Id)!.Itens.Select(i => i.ProdutoId == Ninho ? i with { QtdMilesimos = 1000 } : i).ToList();
        var recusou = false;
        try { ContasAbertas.Gravar(cx, c.Id, abaixo, null, null, op); }
        catch (InvalidOperationException e) { recusou = e.Message == ContasAbertas.Textos.ItemJaPedido; }
        checar(recusou && ContasAbertas.Ler(cx, c.Id)!.Itens.First(i => i.ProdutoId == Ninho).QtdMilesimos == 3000,
            "CA-18 Gravar abaixo do que ja foi pedido e recusado com 'Item já pedido' (so RetirarProduzido tira)");
        var semLinha = ContasAbertas.Ler(cx, c.Id)!.Itens.Where(i => i.ProdutoId != Ninho).ToList();
        recusou = false;
        try { ContasAbertas.Gravar(cx, c.Id, semLinha, null, null, op); } catch (InvalidOperationException) { recusou = true; }
        checar(recusou, "CA-18 tirar a linha inteira ja pedida pelo Gravar tambem e recusado");

        // RetirarProduzido: PIN de OUTRO gerente
        var proprio = false;
        try { ContasAbertas.RetirarProduzido(cx, c.Id, ninho.LinhaId!, 1000, gerente, gerente); }
        catch (InvalidOperationException) { proprio = true; }
        checar(proprio, "CA-18 o gerente que opera nao autoriza a propria retirada (sup.Id != operador, a regra da sangria)");
        var naoGerente = false;
        try { ContasAbertas.RetirarProduzido(cx, c.Id, ninho.LinhaId!, 1000, op, op); }
        catch (InvalidOperationException) { naoGerente = true; }
        checar(naoGerente, "CA-18 operador comum nao autoriza");

        ContasAbertas.RetirarProduzido(cx, c.Id, ninho.LinhaId!, 1000, op, gerente);
        var depois = ContasAbertas.Ler(cx, c.Id)!.Itens.First(i => i.ProdutoId == Ninho);
        checar(depois.QtdMilesimos == 1000 && depois.CozinhaMilesimos == 1000,
            "CA-18 autorizado: a linha fica com 1 e CozinhaMilesimos baixa junto");
        var loteLido = ContasAbertas.LerLote(cx, lote.Id)!;
        checar(loteLido.Retirados.Count == 1 && loteLido.Retirados[0].LinhaId == ninho.LinhaId && loteLido.Retirados[0].QtdMilesimos == 2000,
            "CA-18 lote ainda na fila: itens_retirados_json guarda {linhaId, 2000}");
        var aud = cx.ExecuteScalar<string>("SELECT detalhe FROM auditoria WHERE evento='conta_item_retirado_apos_cozinha' ORDER BY id DESC LIMIT 1") ?? "";
        var autz = cx.ExecuteScalar<string>("SELECT autorizador FROM auditoria WHERE evento='conta_item_retirado_apos_cozinha' ORDER BY id DESC LIMIT 1") ?? "";
        checar(aud.Contains("DONUT NINHO") && aud.Contains(Dinheiro.DeReais(24).Formatado()) && autz == gerente.Id,
            $"CA-18 auditoria com item, valor e autorizador: {aud}");
        var ev = cx.QueryFirstOrDefault("SELECT client_key, payload FROM outbox WHERE tipo=@t AND ref_id=@r ORDER BY id DESC LIMIT 1", new { t = ContasAbertas.TipoEvento, r = c.Id });
        checar(ev is not null && (string)ev.client_key == $"{c.Id}:conta_item_retirado_apos_cozinha:1"
               && ((string)ev.payload).Contains("\"evento\":\"conta_item_retirado_apos_cozinha\"", StringComparison.Ordinal),
            "CA-18 conta_evento enfileirado com client_key conta:evento:seq");

        // retirada com o lote JA NA TV: so o rastro (nada a refazer no corpo)
        cx.Execute("UPDATE outbox SET enviado_em=@e WHERE tipo=@t AND ref_id=@r", new { e = DateTime.Now.ToString("o"), t = PedidoCozinha.TipoNaFila, r = lote.Id });
        var agua = ContasAbertas.Ler(cx, c.Id)!.Itens.First(i => i.ProdutoId == Agua);
        ContasAbertas.RetirarProduzido(cx, c.Id, agua.LinhaId!, 0, op, gerente2);
        var l2 = ContasAbertas.LerLote(cx, lote.Id)!;
        checar(ContasAbertas.Ler(cx, c.Id)!.Itens.All(i => i.ProdutoId != Agua) && l2.Retirados.Count == 1,
            "CA-18 quantidade zero tira a linha; lote ja na TV nao ganha retirada (e aviso de boca)");
        checar(cx.ExecuteScalar<string>("SELECT client_key FROM outbox WHERE tipo=@t AND ref_id=@r ORDER BY id DESC LIMIT 1", new { t = ContasAbertas.TipoEvento, r = c.Id })
               == $"{c.Id}:conta_item_retirado_apos_cozinha:2",
            "CA-20 segundo evento do mesmo tipo na mesma conta ganha seq 2 (client_key unica)");

        // tirar linha nunca pedida: livre; com mais de 15 min de vida audita
        var nova = ContasAbertas.Ler(cx, c.Id)!.Itens.ToList();
        nova.Add(Item(Homer, "DONUT HOMER", 1200, 2000));
        ContasAbertas.Gravar(cx, c.Id, nova, null, null, op);
        var antesAud = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM auditoria WHERE evento='conta_item_retirado'");
        ContasAbertas.Gravar(cx, c.Id, ContasAbertas.Ler(cx, c.Id)!.Itens.Where(i => i.ProdutoId != Homer).ToList(), null, null, op);
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM auditoria WHERE evento='conta_item_retirado'") == antesAud,
            "CA-18 tirar linha nunca pedida de conta nova e livre e sem rastro");
        nova = ContasAbertas.Ler(cx, c.Id)!.Itens.ToList();
        nova.Add(Item(Homer, "DONUT HOMER", 1200, 2000));
        ContasAbertas.Gravar(cx, c.Id, nova, null, null, op);
        cx.Execute("UPDATE conta_aberta SET aberta_em=@a WHERE id=@id", new { a = DateTime.Now.AddMinutes(-20).ToString("o"), id = c.Id });
        ContasAbertas.Gravar(cx, c.Id, ContasAbertas.Ler(cx, c.Id)!.Itens.Where(i => i.ProdutoId != Homer).ToList(), null, null, op);
        var audLivre = cx.ExecuteScalar<string>("SELECT detalhe FROM auditoria WHERE evento='conta_item_retirado' ORDER BY id DESC LIMIT 1") ?? "";
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM auditoria WHERE evento='conta_item_retirado'") == antesAud + 1 && audLivre.Contains("DONUT HOMER"),
            $"CA-18 conta com mais de 15 min: tirar linha nunca pedida audita item e valor, sem PIN: {audLivre}");
        ContasAbertas.Cancelar(cx, c.Id, op, "teste", gerente);
    }

    // ── CA-17: cancelar ─────────────────────────────────────────────────────
    private static void Cancelar(SqliteConnection cx, Sessao sessao, Operador op, Operador gerente, Operador gerente2, Action<bool, string> checar)
    {
        var vazia = ContasAbertas.Abrir(cx, sessao, op, "Vazia");
        ContasAbertas.Cancelar(cx, vazia.Id, op, null, null);
        var v = ContasAbertas.Ler(cx, vazia.Id)!;
        checar(v.Status == "cancelada" && v.CanceladaEm is not null && v.CancelOperador == op.Id && !v.CancelAutomatico,
            "CA-17 conta vazia cancela sem PIN e sem motivo");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo=@t AND ref_id=@r", new { t = ContasAbertas.TipoEvento, r = vazia.Id }) == 0
               && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM auditoria WHERE evento='conta_cancelada_vazia'") >= 1,
            "CA-17 vazia: auditoria local, sem conta_evento");

        var cheia = ContasAbertas.Abrir(cx, sessao, op, "Cheia", itens: new List<ItemRascunho> { Item(Ninho, "DONUT NINHO", 1200, 2000) });
        var lote = ContasAbertas.PedirCozinha(cx, cheia.Id, null, op, false)!;
        string? erro = null;
        try { ContasAbertas.Cancelar(cx, cheia.Id, op, null, gerente); } catch (InvalidOperationException e) { erro = e.Message; }
        checar(erro is not null && ContasAbertas.Ler(cx, cheia.Id)!.Status == "aberta", $"CA-17 com item, sem motivo: recusa ({erro})");
        erro = null;
        try { ContasAbertas.Cancelar(cx, cheia.Id, op, "cliente foi embora", null); } catch (InvalidOperationException e) { erro = e.Message; }
        checar(erro is not null, $"CA-17 com item, sem autorizador: recusa ({erro})");
        erro = null;
        try { ContasAbertas.Cancelar(cx, cheia.Id, gerente, "cliente foi embora", gerente); } catch (InvalidOperationException e) { erro = e.Message; }
        checar(erro is not null, $"CA-17 o proprio gerente nao autoriza o cancelamento que ele opera ({erro})");
        erro = null;
        try { ContasAbertas.Cancelar(cx, cheia.Id, op, "cliente foi embora", op); } catch (InvalidOperationException e) { erro = e.Message; }
        checar(erro is not null, "CA-17 operador comum nao autoriza");

        ContasAbertas.Cancelar(cx, cheia.Id, op, "cliente foi embora", gerente2);
        var c = ContasAbertas.Ler(cx, cheia.Id)!;
        checar(c.Status == "cancelada" && c.CancelMotivo == "cliente foi embora" && c.CancelOperador == op.Id && c.CancelAutorizador == gerente2.Id && c.Dono is null,
            "CA-17 cancelada com motivo, operador e autorizador; o dono e solto");
        var aud = cx.QueryFirst("SELECT detalhe, autorizador FROM auditoria WHERE evento='conta_cancelada_com_itens' ORDER BY id DESC LIMIT 1");
        checar(((string)aud.detalhe).Contains("DONUT NINHO") && ((string)aud.detalhe).Contains(Dinheiro.DeReais(24).Formatado())
               && ((string)aud.detalhe).Contains("lote") && (string)aud.autorizador == gerente2.Id,
            $"CA-17 auditoria conta_cancelada_com_itens com itens, valor de tabela, lotes e autorizador: {aud.detalhe}");
        var ev = cx.QueryFirst("SELECT client_key, payload FROM outbox WHERE tipo=@t AND ref_id=@r ORDER BY id DESC LIMIT 1", new { t = ContasAbertas.TipoEvento, r = cheia.Id });
        var p = JsonDocument.Parse((string)ev.payload).RootElement;
        checar((string)ev.client_key == $"{cheia.Id}:conta_cancelada_com_itens:1"
               && p.GetProperty("evento").GetString() == "conta_cancelada_com_itens"
               && p.GetProperty("conta").GetProperty("numero").GetInt64() == cheia.Numero
               && p.GetProperty("itens").EnumerateArray().Count() == 1
               && p.GetProperty("itens")[0].GetProperty("produto_id").GetString() == Ninho
               && p.GetProperty("valor_tabela_cent").GetInt64() == 2400
               && p.GetProperty("lotes").EnumerateArray().Count() == 1
               && p.GetProperty("lotes")[0].GetProperty("estado").GetString() == "NaFila"
               && p.GetProperty("motivo").GetString() == "cliente foi embora"
               && p.GetProperty("autorizador").GetString() == gerente2.Nome,
            "CA-17 conta_evento: evento, conta, itens, valor de tabela, lotes com estado, motivo e autorizador");
        checar(ContasAbertas.Cancelar(cx, cheia.Id, op, "de novo", gerente2) == false, "CA-17 cancelar de novo e idempotente (devolve false, nao grava nada)");
        var fechar = false;
        try { ContasAbertas.PedirCozinha(cx, cheia.Id, null, op, false); } catch (InvalidOperationException) { fechar = true; }
        checar(fechar, "CA-17 conta cancelada nao pede mais cozinha");
    }

    // ── CA-9 / CA-10 / CA-11: fechar e UMA venda, uma vez ───────────────────
    private static async Task FecharComoVendaAsync(SqliteConnection cx, Sessao sessao, Operador op, Action<bool, string> checar)
    {
        Vendas.GravarConfig(cx, "kds_balcao", "1");
        var c = ContasAbertas.Abrir(cx, sessao, op, "Mesa 5", itens: new List<ItemRascunho> { Item(Ninho, "DONUT NINHO", 1200, 2000), Item(Agua, "AGUA 500ML", 500, 1000) });
        var lote1 = ContasAbertas.PedirCozinha(cx, c.Id, null, op, false)!;
        // item novo depois do lote: fica pendente
        var mais = ContasAbertas.Ler(cx, c.Id)!.Itens.ToList();
        mais.Add(Item(Homer, "DONUT HOMER", 1200, 1000));
        ContasAbertas.Gravar(cx, c.Id, mais, null, null, op);

        var linhas = LinhasDe(ContasAbertas.Ler(cx, c.Id)!);
        var total = new Dinheiro(linhas.Sum(l => l.Total.Centavos));
        var pagos = new[] { new PagamentoVenda("dinheiro", total, Dinheiro.Zero) };
        int Vendas_() => cx.ExecuteScalar<int>("SELECT COUNT(*) FROM venda");
        int FilaVenda(string id) => cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo IN ('venda','venda_composta') AND ref_id=@r", new { r = id });
        int FilaCozinha(string id) => cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo=@t AND ref_id=@r", new { t = PedidoCozinha.TipoNaFila, r = id });
        var vendasAntes = Vendas_();
        var cozinhaAntes = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo=@t", new { t = PedidoCozinha.TipoNaFila });

        // teste cruzado barra ANTES de qualquer INSERT (CA-8)
        Vendas.GravarConfig(cx, "homologacao", "1");
        var cruzado = false;
        try { Vendas.Finalizar(cx, sessao, op, linhas, pagos, null, "American Day Savassi", null, DestinoCozinha.Loja, "Mesa 5", new ContaParaFechar(c.Id, null)); }
        catch (InvalidOperationException e) { cruzado = e.Message == ContasAbertas.Textos.RealEmTurnoDeTeste; }
        Vendas.GravarConfig(cx, "homologacao", "0");
        checar(cruzado && Vendas_() == vendasAntes && ContasAbertas.Ler(cx, c.Id)!.Status == "aberta",
            "CA-8 conta real em turno de teste: Finalizar recusa antes de gravar e a conta continua aberta");

        // o fechamento: Finalizar com conta e destino dos pendentes
        var v = Vendas.Finalizar(cx, sessao, op, linhas, pagos, null, "American Day Savassi", null,
            DestinoCozinha.Loja, "Mesa 5", new ContaParaFechar(c.Id, DestinoCozinha.Loja));
        checar(Vendas_() == vendasAntes + 1 && FilaVenda(v.Id) == 1,
            "CA-9 fechar a comanda grava UMA venda e UMA linha venda na fila (estoque, receita, DRE e nota uma vez)");
        checar(FilaCozinha(v.Id) == 0, "CA-9 o corpo da venda NUNCA vai para a cozinha (cozinha forcado null, mesmo pedindo Loja)");
        var fechada = ContasAbertas.Ler(cx, c.Id)!;
        checar(fechada.Status == "fechada" && fechada.VendaId == v.Id && fechada.FechadaEm is not null && fechada.FechandoEm is null && fechada.Dono is null,
            "CA-9 a conta fica fechada com venda_id dentro da transacao");
        var lotes = ContasAbertas.Lotes(cx, c.Id);
        checar(lotes.Count == 2 && lotes[1].N == 2 && lotes[1].Itens.Count == 1 && lotes[1].Itens[0].Nome == "DONUT HOMER"
               && FilaCozinha(lotes[1].Id) == 1 && FilaCozinha(lote1.Id) == 1,
            "CA-10 com destino escolhido, o lote n+1 leva SO o pendente (o Homer); o lote 1 nao vai de novo");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo=@t", new { t = PedidoCozinha.TipoNaFila }) == cozinhaAntes + 1,
            "CA-9 dupla contagem: os lotes nao viram venda e a venda nao vira lote (uma linha a mais na cozinha, a do pendente)");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM kds_ticket WHERE origem='balcao' AND ref_id=@r", new { r = v.Id }) == 0,
            "CA-9 Kds.DoBalcao e pulado na venda de conta mesmo com kds_balcao=1 (producao dobrada)");
        var comum = Vendas.Finalizar(cx, sessao, op, linhas.Take(1).ToList(), new[] { new PagamentoVenda("dinheiro", linhas[0].Total, Dinheiro.Zero) }, null, "American Day Savassi", null);
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM kds_ticket WHERE origem='balcao' AND ref_id=@r", new { r = comum.Id }) == 1,
            "CA-9 (controle) a venda de balcao com kds_balcao=1 continua criando o ticket local");
        Vendas.GravarConfig(cx, "kds_balcao", "0");
        var audFech = cx.ExecuteScalar<string>("SELECT detalhe FROM auditoria WHERE evento='conta_fechada' ORDER BY id DESC LIMIT 1") ?? "";
        checar(audFech.Contains($"comanda={c.Numero}") && audFech.Contains($"venda={v.Numero}"), $"CA-9 auditoria conta_fechada: {audFech}");

        // CA-11: segundo Finalizar na mesma conta devolve a MESMA venda
        var v2 = Vendas.Finalizar(cx, sessao, op, linhas, pagos, null, "American Day Savassi", null,
            null, "Mesa 5", new ContaParaFechar(c.Id, DestinoCozinha.Loja));
        checar(v2.Id == v.Id && v2.ClientKey == v.ClientKey && v2.Numero == v.Numero && Vendas_() == vendasAntes + 2 && FilaVenda(v.Id) == 1
               && ContasAbertas.Lotes(cx, c.Id).Count == 2,
            "CA-11 'Tentar de novo' depois do commit: Finalizar devolve a MESMA VendaGravada, sem V2, sem segunda linha e sem lote novo");

        // CA-10: com "Nao", nenhum lote para os pendentes
        var c2 = ContasAbertas.Abrir(cx, sessao, op, "Vitrine", itens: new List<ItemRascunho> { Item(Agua, "AGUA 500ML", 500, 2000) });
        var l2 = LinhasDe(c2);
        var vNao = Vendas.Finalizar(cx, sessao, op, l2, new[] { new PagamentoVenda("dinheiro", new Dinheiro(1000), Dinheiro.Zero) }, null, "American Day Savassi", null,
            DestinoCozinha.Viagem, null, new ContaParaFechar(c2.Id, null));
        checar(ContasAbertas.Lotes(cx, c2.Id).Count == 0 && FilaCozinha(vNao.Id) == 0 && ContasAbertas.Ler(cx, c2.Id)!.Status == "fechada",
            "CA-10 com 'Nao', os pendentes so sao vendidos: nenhum lote, nada para a TV");

        // CA-11: conta cancelada lanca antes de qualquer INSERT
        var c3 = ContasAbertas.Abrir(cx, sessao, op, "Cancelada", itens: new List<ItemRascunho> { Item(Agua, "AGUA 500ML", 500, 1000) });
        var l3 = LinhasDe(c3);
        ContasAbertas.Cancelar(cx, c3.Id, op, "teste", OperadorPorId(cx, "ger-conta"));
        var antes3 = Vendas_();
        var recusou = false;
        try { Vendas.Finalizar(cx, sessao, op, l3, new[] { new PagamentoVenda("dinheiro", new Dinheiro(500), Dinheiro.Zero) }, null, "American Day Savassi", null, null, null, new ContaParaFechar(c3.Id, null)); }
        catch (InvalidOperationException) { recusou = true; }
        checar(recusou && Vendas_() == antes3, "CA-11 conta cancelada: Finalizar lanca antes de qualquer INSERT (count(venda) nao muda)");
        recusou = false;
        try { Vendas.Finalizar(cx, sessao, op, l3, new[] { new PagamentoVenda("dinheiro", new Dinheiro(500), Dinheiro.Zero) }, null, "American Day Savassi", null, null, null, new ContaParaFechar("nao-existe", null)); }
        catch (InvalidOperationException) { recusou = true; }
        checar(recusou && Vendas_() == antes3, "CA-11 conta inexistente: idem");

        // CA-9: rollback (falha forcada no INSERT da venda) mantem a conta aberta
        var c4 = ContasAbertas.Abrir(cx, sessao, op, "Rollback", itens: new List<ItemRascunho> { Item(Agua, "AGUA 500ML", 500, 1000) });
        var l4 = LinhasDe(c4);
        cx.Execute("CREATE TRIGGER trg_conta_rollback BEFORE INSERT ON venda_pagamento BEGIN SELECT RAISE(ABORT, 'falha forcada'); END");
        var falhou = false;
        try { Vendas.Finalizar(cx, sessao, op, l4, new[] { new PagamentoVenda("dinheiro", new Dinheiro(500), Dinheiro.Zero) }, null, "American Day Savassi", null, null, null, new ContaParaFechar(c4.Id, DestinoCozinha.Loja)); }
        catch (Exception) { falhou = true; }
        cx.Execute("DROP TRIGGER trg_conta_rollback");
        var c4Lida = ContasAbertas.Ler(cx, c4.Id)!;
        checar(falhou && c4Lida.Status == "aberta" && c4Lida.VendaId is null && Vendas_() == antes3 && ContasAbertas.Lotes(cx, c4.Id).Count == 0,
            "CA-9 rollback: a venda nao entra, a conta continua aberta, sem venda_id e sem lote");
        ContasAbertas.Cancelar(cx, c4.Id, op, "teste", OperadorPorId(cx, "ger-conta"));
        await Task.CompletedTask;
    }

    // ── CA-12: fechando_em e a cobranca sem venda ───────────────────────────
    private static void Fechando(SqliteConnection cx, Sessao sessao, Operador op, Action<bool, string> checar)
    {
        var c = ContasAbertas.Abrir(cx, sessao, op, "Mesa 8", itens: new List<ItemRascunho> { Item(Agua, "AGUA", 500, 1000) });
        checar(ContasAbertas.AvisoDeMaquininha(cx, sessao, c) is null, "CA-12 sem cobranca pendente e sem fechando_em: nenhum aviso");
        ContasAbertas.MarcarFechando(cx, c.Id);
        checar(ContasAbertas.Ler(cx, c.Id)!.FechandoEm is not null, "CA-12 MarcarFechando grava fechando_em (o Pagamento abriu)");
        ContasAbertas.LimparFechando(cx, c.Id);
        checar(ContasAbertas.Ler(cx, c.Id)!.FechandoEm is null, "CA-12 LimparFechando (Voltar para a comanda) limpa");
        ContasAbertas.MarcarFechando(cx, c.Id);
        cx.Execute("""
            INSERT INTO tef_transacao (id, charge_id, tipo, valor_cent, situacao, criado_em, atualizado_em)
            VALUES ('tef-conta', 'ch-1', 'credito', 500, 'aprovada', @e, @e)
            """, new { e = DateTime.Now.ToString("o") });
        var aviso = ContasAbertas.AvisoDeMaquininha(cx, sessao, ContasAbertas.Ler(cx, c.Id)!);
        checar(aviso == Rascunho.AvisoDeCobranca(new Dinheiro(500)) && aviso!.Contains("maquininha"),
            "CA-12 fechando_em de pe + cartao aprovado sem venda = AvisoDeCobranca obrigatorio (o texto de hoje)");
        checar(ContasAbertas.PrecisaConferirMaquininha(cx, sessao, ContasAbertas.Ler(cx, c.Id)!), "CA-12 a linha da lista ganha 'confira a maquininha'");
        cx.Execute("DELETE FROM tef_transacao WHERE id='tef-conta'");
        checar(ContasAbertas.AvisoDeMaquininha(cx, sessao, ContasAbertas.Ler(cx, c.Id)!) == Rascunho.AvisoDeCobranca(Dinheiro.Zero),
            "CA-12 fechando_em de pe sem cobranca: o aviso diz que nada foi cobrado (nunca cala)");
        ContasAbertas.LimparFechando(cx, c.Id);
        var linhas = LinhasDe(ContasAbertas.Ler(cx, c.Id)!);
        ContasAbertas.MarcarFechando(cx, c.Id);
        var v = Vendas.Finalizar(cx, sessao, op, linhas, new[] { new PagamentoVenda("dinheiro", new Dinheiro(500), Dinheiro.Zero) }, null, "American Day Savassi", null, null, null, new ContaParaFechar(c.Id, null));
        checar(ContasAbertas.Ler(cx, c.Id)!.FechandoEm is null && ContasAbertas.Ler(cx, c.Id)!.VendaId == v.Id, "CA-12 o commit do Finalizar limpa fechando_em");
    }

    // ── CA-15: cortesia por conta ───────────────────────────────────────────
    private static void Cortesia(SqliteConnection cx, Sessao sessao, Operador op, Action<bool, string> checar)
    {
        var a = ContasAbertas.Abrir(cx, sessao, op, "Cupom A", itens: new List<ItemRascunho> { Item(Agua, "AGUA", 500, 1000) });
        ContasAbertas.Gravar(cx, a.Id, a.Itens, "AD-ABC123", new Dictionary<string, int> { ["agua"] = 1 }, op);
        var lida = ContasAbertas.Ler(cx, a.Id)!;
        checar(lida.CortesiaCodigo == "AD-ABC123" && lida.CortesiaCobertura["agua"] == 1, "CA-15 a cortesia mora na conta (codigo e cobertura)");
        var b = ContasAbertas.Abrir(cx, sessao, op, "Cupom B");
        checar(ContasAbertas.CodigoEmOutraConta(cx, "ad-abc123", b.Id) == a.Numero, "CA-15 o mesmo codigo em outra conta aberta e recusado ('Esse cupom já está na comanda N.')");
        checar(ContasAbertas.CodigoEmOutraConta(cx, "AD-ABC123", a.Id) is null, "CA-15 a propria conta nao conta como 'outra'");
        checar(ContasAbertas.CodigoEmOutraConta(cx, "AD-OUTRO", b.Id) is null, "CA-15 codigo livre");
        ContasAbertas.Gravar(cx, a.Id, a.Itens, null, null, op);
        checar(ContasAbertas.Ler(cx, a.Id)!.CortesiaCodigo is null && ContasAbertas.CodigoEmOutraConta(cx, "AD-ABC123", b.Id) is null,
            "CA-15 tirar a cortesia (cupom morto no Fechar conta) libera o codigo");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM auditoria WHERE evento='cortesia_aplicada_em_comanda'") >= 1,
            "CA-15 auditoria cortesia_aplicada_em_comanda com o numero");
        ContasAbertas.Cancelar(cx, a.Id, op, "teste", OperadorPorId(cx, "ger-conta"));
        ContasAbertas.Cancelar(cx, b.Id, op, null, null);
    }

    // ── CA-15b (revisao 13/10): comanda inteira em cortesia fecha sem venda ──
    private static void CortesiaInteiraFechaSemVenda(SqliteConnection cx, Sessao sessao, Operador op, Operador gerente, Action<bool, string> checar)
    {
        var c = ContasAbertas.Abrir(cx, sessao, op, "Cortesia toda", itens: new List<ItemRascunho> { Item(Agua, "AGUA", 500, 1000) });
        ContasAbertas.Gravar(cx, c.Id, c.Itens, "AD-CORT01", new Dictionary<string, int> { ["agua"] = 1 }, op);
        var lote = ContasAbertas.PedirCozinha(cx, c.Id, null, op, false)!;
        var vendasAntes = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM venda");
        var fechou = ContasAbertas.FecharSemVenda(cx, c.Id, op, "cortesia AD-CORT01");
        var lida = ContasAbertas.Ler(cx, c.Id)!;
        checar(fechou && lida.Status == "fechada" && lida.VendaId is null && !lida.Aberta,
            "CA-15b comanda toda em cortesia fecha sem venda (status fechada, venda_id nulo)");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM venda") == vendasAntes, "CA-15b nenhuma venda nasce da cortesia inteira");
        checar(ContasAbertas.Listar(cx, teste: false).All(x => x.Id != c.Id), "CA-15b a comanda sai da lista de abertas");
        checar(ContasAbertas.CodigoEmOutraConta(cx, "AD-CORT01", null) is null, "CA-15b o codigo do cupom fica livre (nao preso numa comanda fechada)");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM auditoria WHERE evento='conta_fechada_sem_venda'") >= 1, "CA-15b auditoria conta_fechada_sem_venda");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo=@t AND client_key=@k",
                   new { t = ContasAbertas.TipoEvento, k = $"{c.Id}:conta_fechada_sem_venda:1" }) == 1,
            "CA-15b conta_evento conta_fechada_sem_venda sobe para o ERP (producao sem venda)");
        var prep = ContasAbertas.PrepararEnvio(cx, cx.ExecuteScalar<string>("SELECT payload FROM outbox WHERE tipo=@t AND ref_id=@r", new { t = PedidoCozinha.TipoNaFila, r = lote.Id })!);
        checar(!prep.Descartar, "CA-15b o lote ja pedido continua indo para a cozinha (fechada nao e cancelada)");
        checar(!ContasAbertas.FecharSemVenda(cx, c.Id, op, "de novo"), "CA-15b fechar de novo nao faz nada (idempotente)");
        var gravou = false;
        try { ContasAbertas.Gravar(cx, c.Id, Array.Empty<ItemRascunho>(), null, null, op); } catch (InvalidOperationException) { gravou = true; }
        checar(gravou, "CA-15b comanda fechada sem venda nao aceita gravar (a tela esvazia sem mexer nela)");
        var resumo = ContasAbertas.ResumoDoTurno(cx, sessao);
        checar(resumo.AbertasN == ContasAbertas.ContarAbertas(cx, teste: false), "CA-15b ResumoDoTurno nao conta a fechada como aberta");
    }

    // ── CA-14b (revisao 13/10): lote que NAO FOI nao e "na cozinha" ─────────
    private static void LotePerdidoNaoEProducao(SqliteConnection cx, Sessao sessao, Operador op, Operador gerente, Action<bool, string> checar)
    {
        var c = ContasAbertas.Abrir(cx, sessao, op, "Perdido lista", itens: new List<ItemRascunho> { Item(Ninho, "DONUT NINHO", 1200, 2000), Item(Agua, "AGUA", 500, 1000) });
        var lote = ContasAbertas.PedirCozinha(cx, c.Id, null, op, false)!;
        var linhaNinho = ContasAbertas.Ler(cx, c.Id)!.Itens.First(i => i.Nome == "DONUT NINHO").LinhaId!;
        LinhaDaLista Linha() => ContasAbertas.LinhasDaLista(cx, sessao, null, teste: false).First(l => l.Conta.Id == c.Id);
        checar(Linha().Cozinha == ContasAbertas.Textos.CozinhaDeN(3, 3) && ContasAbertas.PerdidosPorLinha(cx, c.Id).Count == 0,
            $"CA-14b lote na fila: a lista diz cozinha 3 de 3 e nada perdido ({Linha().Cozinha})");
        cx.Execute("UPDATE outbox SET desistido_em=@e WHERE tipo=@t AND ref_id=@r", new { e = DateTime.Now.ToString("o"), t = PedidoCozinha.TipoNaFila, r = lote.Id });
        checar(Linha().Cozinha == ContasAbertas.Textos.CozinhaDeN(0, 3),
            $"CA-14b lote que nao foi: a lista diz cozinha 0 de 3, nao 3 de 3 ({Linha().Cozinha})");
        var perdidos = ContasAbertas.PerdidosPorLinha(cx, c.Id);
        checar(perdidos.GetValueOrDefault(linhaNinho) == 2000 && perdidos.Values.Sum() == 3000,
            "CA-14b PerdidosPorLinha: a tela tira do 'N na cozinha' o que esta no lote perdido, linha a linha");
        checar(Linha().LoteParaMandarDeNovo == lote.Id, "CA-14b a linha oferece Mandar de novo o lote perdido");
        var novo = ContasAbertas.ReenviarLote(cx, lote.Id, op);
        checar(ContasAbertas.PerdidosPorLinha(cx, c.Id).Count == 0 && Linha().Cozinha == ContasAbertas.Textos.CozinhaDeN(3, 3),
            $"CA-14b depois de Mandar de novo nada fica perdido e a lista volta a 3 de 3 ({Linha().Cozinha})");
        checar(novo.N == 2, "CA-14b o reenvio e o lote 2");
        ContasAbertas.Cancelar(cx, c.Id, op, "teste", gerente);
    }

    // ── CA-8: turno de teste nao cruza ──────────────────────────────────────
    private static void TurnoDeTesteCruzado(SqliteConnection cx, Sessao sessao, Operador op, Action<bool, string> checar)
    {
        Vendas.GravarConfig(cx, "homologacao", "1");
        var t = ContasAbertas.Abrir(cx, sessao, op, "De teste", itens: new List<ItemRascunho> { Item(Agua, "AGUA", 500, 1000) });
        checar(t.Teste && ContasAbertas.Listar(cx, teste: true).Any(c => c.Id == t.Id) && ContasAbertas.Listar(cx, teste: false).All(c => c.Id != t.Id),
            "CA-8 conta aberta no modo de homologacao nasce de teste e so aparece na lista de teste");
        var lote = ContasAbertas.PedirCozinha(cx, t.Id, null, op, homologacao: Vendas.Homologacao(cx) || sessao.Teste)!;
        checar(lote.Homologacao && ContasAbertas.EstadoDoLote(cx, lote.Id) == EstadoLote.Teste, "CA-8 lote de conta de teste nao vai para a TV de verdade");
        Vendas.GravarConfig(cx, "homologacao", "0");
        var antes = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM venda");
        var recusou = false;
        try { Vendas.Finalizar(cx, sessao, op, LinhasDe(t), new[] { new PagamentoVenda("dinheiro", new Dinheiro(500), Dinheiro.Zero) }, null, "American Day Savassi", null, null, null, new ContaParaFechar(t.Id, null)); }
        catch (InvalidOperationException e) { recusou = e.Message == ContasAbertas.Textos.TesteEmTurnoReal; }
        checar(recusou && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM venda") == antes && ContasAbertas.Ler(cx, t.Id)!.Status == "aberta",
            "CA-8 conta de teste em turno real: Finalizar recusa ('Comanda de teste não fecha em turno real.') antes de gravar");
        checar(ContasAbertas.ContarAbertas(cx, teste: true) == 1, "CA-8 o contador do botao filtra pelo modo");
        ContasAbertas.Cancelar(cx, t.Id, op, "teste", OperadorPorId(cx, "ger-conta"));
    }

    // ── CA-19: o fechamento do caixa ────────────────────────────────────────
    private static void FechamentoDoCaixa(SqliteConnection cx, Sessao sessao, Operador op, Operador gerente, Action<bool, string> checar)
    {
        var vazia = ContasAbertas.Abrir(cx, sessao, op, "Mesa 1");
        // vazia MAS com lote: o item foi pedido e depois tirado com PIN; a cozinha produziu, a conta nao some sozinha
        var vaziaComLote = ContasAbertas.Abrir(cx, sessao, op, "Mesa 2", itens: new List<ItemRascunho> { Item(Agua, "AGUA", 500, 1000) });
        ContasAbertas.PedirCozinha(cx, vaziaComLote.Id, null, op, false);
        ContasAbertas.RetirarProduzido(cx, vaziaComLote.Id, ContasAbertas.Ler(cx, vaziaComLote.Id)!.Itens[0].LinhaId!, 0, op, gerente);
        var cheia = ContasAbertas.Abrir(cx, sessao, op, "Mesa 3", itens: new List<ItemRascunho> { Item(Ninho, "DONUT NINHO", 1200, 2000) });
        var apuradoAntes = Caixa.Apurado(cx, sessao)["dinheiro"].Centavos;

        var canceladas = ContasAbertas.CancelarVaziasAutomatico(cx, op);
        checar(canceladas == 1 && ContasAbertas.Ler(cx, vazia.Id)!.Status == "cancelada" && ContasAbertas.Ler(cx, vazia.Id)!.CancelAutomatico
               && ContasAbertas.Ler(cx, cheia.Id)!.Status == "aberta" && ContasAbertas.Ler(cx, vaziaComLote.Id)!.Status == "aberta",
            "CA-19 o fechamento cancela sozinho so as vazias SEM lote (auditoria conta_cancelada_automatica)");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM auditoria WHERE evento='conta_cancelada_automatica'") >= 1, "CA-19 auditoria conta_cancelada_automatica");

        var atravessam = ContasAbertas.RegistrarAtravessamento(cx, sessao, op);
        checar(atravessam.Count == 2 && atravessam.All(c => c.Status == "aberta"), "CA-19 as abertas atravessam o turno (nao bloqueia, nao transfere)");
        var eventos = cx.Query<string>("SELECT client_key FROM outbox WHERE tipo=@t AND client_key LIKE '%:conta_atravessou_fechamento:%'", new { t = ContasAbertas.TipoEvento }).ToList();
        checar(eventos.Count == 2 && eventos.Contains($"{cheia.Id}:conta_atravessou_fechamento:1"), "CA-19 cada conta que atravessa gera um conta_evento conta_atravessou_fechamento");
        checar(Caixa.Apurado(cx, sessao)["dinheiro"].Centavos == apuradoAntes, "CA-19 Apurado nao muda (comanda nao e dinheiro)");

        var resumo = ContasAbertas.ResumoDoTurno(cx, sessao);
        checar(resumo.AbertasN == 2 && resumo.Abertas.Centavos == 2400, $"CA-19 ResumoDoTurno: 2 abertas valendo R$ 24,00 ({resumo.AbertasN}, {resumo.Abertas.Formatado()})");
        checar(resumo.CanceladasN >= 1 && resumo.Canceladas.Centavos >= 2400, $"CA-19 ResumoDoTurno conta as canceladas do turno com o valor de tabela ({resumo.CanceladasN}, {resumo.Canceladas.Formatado()})");
        var linhas = ContasAbertas.LinhasDoFechamento(cx, sessao);
        checar(linhas.Count == 2 && linhas[0] == $"Comandas canceladas no turno: {resumo.CanceladasN} ({resumo.Canceladas.Formatado()})"
               && linhas[1] == $"Comandas abertas: 2 ({new Dinheiro(2400).Formatado()})",
            $"CA-19 as duas linhas do papel: {linhas[0]} / {linhas[1]}");
        checar(ContasAbertas.Textos.ComandasAbertas(2, new Dinheiro(2400)) == linhas[1], "CA-19 a linha do dialogo e a mesma do papel");
        ContasAbertas.Cancelar(cx, cheia.Id, op, "teste", OperadorPorId(cx, "ger-conta"));
        ContasAbertas.Cancelar(cx, vaziaComLote.Id, op, "teste", OperadorPorId(cx, "ger-conta"));
    }

    // ── CA-20: conta_evento na fila ─────────────────────────────────────────
    private static async Task EventoNaFilaAsync(SqliteConnection cx, Sessao sessao, Operador op, Operador gerente, Action<bool, string> checar)
    {
        checar(Drenagem.TiposComHandler.Contains(ContasAbertas.TipoEvento), "CA-20 conta_evento tem handler no dreno");
        checar(Drenagem.JanelaPropria.Any(j => j.Tipo == ContasAbertas.TipoEvento && j.Janela == 5), "CA-20 conta_evento tem janela propria de 5");
        checar(Drenagem.PrazoDoTransitorio(ContasAbertas.TipoEvento) >= TimeSpan.FromDays(365), "CA-20 conta_evento nao tem prazo de descarte (e registro, nao transitorio)");
        checar(ContasAbertas.RpcEvento == "pdv_conta_evento", "CA-20 a RPC e pdv_conta_evento");
        // a TV da cozinha nao pode ser a de verdade aqui: a varredura com sessao tambem leva kds_cozinha
        Vendas.GravarConfig(cx, PedidoCozinha.ChaveUrl, Isolamento.UrlMorta);

        // a funcao pura do desfecho
        var chamadas = new List<(string Rpc, string Corpo)>();
        Func<string, string, Task<(int, string?)>> Responde(int status, string corpo) => (r, c) => { chamadas.Add((r, c)); return Task.FromResult<(int, string?)>((status, corpo)); };
        var payload = JsonSerializer.Serialize(new { evento = "conta_cancelada_com_itens", loja = "American Day Savassi", conta = new { id = "x", numero = 12 } });
        var r404 = await ContasAbertas.ResolverEventoNaFilaAsync("x:conta_cancelada_com_itens:1", payload, Responde(404, """{"code":"PGRST202","message":"Could not find the function"}"""));
        checar(r404.Ok is null && (r404.Erro ?? "").Contains("pdv_conta_evento"), "CA-20 404 PGRST202 = espera ate o dono rodar o SQL (com o motivo)");
        var r200 = await ContasAbertas.ResolverEventoNaFilaAsync("x:conta_cancelada_com_itens:1", payload, Responde(200, "{\"ok\":true}"));
        var corpo = JsonDocument.Parse(chamadas[^1].Corpo).RootElement;
        checar(r200.Ok == true && chamadas[^1].Rpc == "pdv_conta_evento"
               && corpo.GetProperty("p_client_key").GetString() == "x:conta_cancelada_com_itens:1"
               && corpo.GetProperty("p_store").GetString() == "American Day Savassi"
               && corpo.GetProperty("p_evento").GetString() == "conta_cancelada_com_itens"
               && corpo.GetProperty("p_payload").GetProperty("conta").GetProperty("numero").GetInt32() == 12,
            "CA-20 200 = enviado; a RPC recebe p_client_key, p_store, p_evento e p_payload");
        checar((await ContasAbertas.ResolverEventoNaFilaAsync("k", payload, Responde(503, ""))).Ok is null, "CA-20 503 espera");
        checar((await ContasAbertas.ResolverEventoNaFilaAsync("k", payload, Responde(400, "x"))).Ok == false, "CA-20 400 conta tentativa");
        checar((await ContasAbertas.ResolverEventoNaFilaAsync("k", "nao e json", Responde(200, ""))).Ok == false, "CA-20 payload ilegivel nao fica eterno");

        // ponta a ponta pela Drenagem com a nuvem de mentira. A janelinha do conta_evento e de 5 por
        // varredura (ORDER BY id): os eventos das secoes anteriores, ja conferidos, saem da frente.
        cx.Execute("UPDATE outbox SET enviado_em=@e WHERE tipo=@t AND enviado_em IS NULL", new { e = DateTime.Now.ToString("o"), t = ContasAbertas.TipoEvento });
        using var fake = new FakePostgrest(PortaNuvem);
        var nuvem = new Nuvem(fake.Url);
        await nuvem.EntrarAsync("caixa@savassi", "x");
        using var dren = new Drenagem(nuvem, fake.Url);
        var c = ContasAbertas.Abrir(cx, sessao, op, "Evento", itens: new List<ItemRascunho> { Item(Agua, "AGUA", 500, 1000) });
        ContasAbertas.Cancelar(cx, c.Id, op, "cliente desistiu", gerente);
        var chave = $"{c.Id}:conta_cancelada_com_itens:1";
        fake.ContaEventoAusente = true;
        await dren.DrenarAsync();
        var linha = cx.QueryFirst("SELECT enviado_em, desistido_em, tentativas, ultimo_erro FROM outbox WHERE client_key=@k", new { k = chave });
        checar(linha.enviado_em is null && linha.desistido_em is null && (long)linha.tentativas == 0 && ((string?)linha.ultimo_erro ?? "").Contains("pdv_conta_evento"),
            $"CA-20 sem a RPC na nuvem a linha espera sem contar tentativa: {linha.ultimo_erro}");
        fake.ContaEventoAusente = false;
        await dren.DrenarAsync();
        linha = cx.QueryFirst("SELECT enviado_em FROM outbox WHERE client_key=@k", new { k = chave });
        checar(linha.enviado_em is string && fake.ContaEventos.ContainsKey(chave), "CA-20 com a RPC publicada, o evento sobe e sai da fila");
    }

    // ── CA-13 / CA-21: o handler da cozinha consulta a conta; drena sem nuvem ──
    private static async Task DrenagemSemNuvemAsync(SqliteConnection cx, Sessao sessao, Operador op, Operador gerente, Action<bool, string> checar)
    {
        checar(Drenagem.TiposSemNuvem.SequenceEqual(new[] { PedidoCozinha.TipoNaFila }), "CA-21 so kds_cozinha drena sem sessao de nuvem");
        using var kds = new FakeKds(PortaKds);
        Vendas.GravarConfig(cx, PedidoCozinha.ChaveUrl, kds.Url);
        cx.Execute("UPDATE outbox SET enviado_em=@e WHERE tipo=@t AND enviado_em IS NULL", new { e = DateTime.Now.ToString("o"), t = PedidoCozinha.TipoNaFila });
        var vendasPendentesAntes = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo='venda' AND enviado_em IS NULL AND ultimo_erro IS NULL");

        // a) lote na fila + conta cancelada = zero POST (descartado pelo handler)
        var cancelada = ContasAbertas.Abrir(cx, sessao, op, "Cancela", itens: new List<ItemRascunho> { Item(Agua, "AGUA", 500, 1000) });
        var loteC = ContasAbertas.PedirCozinha(cx, cancelada.Id, null, op, false)!;
        ContasAbertas.Cancelar(cx, cancelada.Id, op, "desistiu", gerente);
        var prep = ContasAbertas.PrepararEnvio(cx, cx.ExecuteScalar<string>("SELECT payload FROM outbox WHERE tipo=@t AND ref_id=@r", new { t = PedidoCozinha.TipoNaFila, r = loteC.Id })!);
        checar(prep.Descartar && (prep.Motivo ?? "").StartsWith("descartado", StringComparison.Ordinal), $"CA-13 PrepararEnvio: conta cancelada = descartado ({prep.Motivo})");

        // b) lote na fila + item retirado = corpo sem o item
        var retira = ContasAbertas.Abrir(cx, sessao, op, "Retira", itens: new List<ItemRascunho> { Item(Ninho, "DONUT NINHO", 1200, 2000), Item(Agua, "AGUA", 500, 1000) });
        var loteR = ContasAbertas.PedirCozinha(cx, retira.Id, null, op, false)!;
        var linhaAgua = ContasAbertas.Ler(cx, retira.Id)!.Itens.First(i => i.ProdutoId == Agua);
        ContasAbertas.RetirarProduzido(cx, retira.Id, linhaAgua.LinhaId!, 0, op, gerente);
        var prepR = ContasAbertas.PrepararEnvio(cx, cx.ExecuteScalar<string>("SELECT payload FROM outbox WHERE tipo=@t AND ref_id=@r", new { t = PedidoCozinha.TipoNaFila, r = loteR.Id })!);
        checar(!prepR.Descartar && prepR.Corpo is not null && !prepR.Corpo.Contains("AGUA") && prepR.Corpo.Contains("DONUT NINHO") && prepR.Corpo.Contains(loteR.Id),
            "CA-13 PrepararEnvio: item retirado some do corpo, o resto vai com o mesmo id");

        // c) lote que ficou vazio = descartado sem enviar
        var vazio = ContasAbertas.Abrir(cx, sessao, op, "Vazio", itens: new List<ItemRascunho> { Item(Agua, "AGUA", 500, 1000) });
        var loteV = ContasAbertas.PedirCozinha(cx, vazio.Id, null, op, false)!;
        var linhaV = ContasAbertas.Ler(cx, vazio.Id)!.Itens[0];
        ContasAbertas.RetirarProduzido(cx, vazio.Id, linhaV.LinhaId!, 0, op, gerente);
        var prepV = ContasAbertas.PrepararEnvio(cx, cx.ExecuteScalar<string>("SELECT payload FROM outbox WHERE tipo=@t AND ref_id=@r", new { t = PedidoCozinha.TipoNaFila, r = loteV.Id })!);
        checar(prepV.Descartar, $"CA-13 lote vazio = descartado sem enviar ({prepV.Motivo})");

        // d) venda comum (sem origem comanda) passa sem mudar nada
        var corpoVenda = JsonSerializer.Serialize(PedidoCozinha.Corpo("v-1", 7, new[] { new LinhaVenda("p", "1", "X", Quantidade.Um, new Dinheiro(100), new Dinheiro(100), "UN", null, null, null, null, 0) }, DestinoCozinha.Loja, "Cleide", null, DateTime.Now));
        var prepN = ContasAbertas.PrepararEnvio(cx, corpoVenda);
        checar(!prepN.Descartar && prepN.Corpo is null, "CA-13 pedido de venda (sem origem comanda) passa intacto");

        // e) substituido = descartado (o reenviado leva o Homer, para o corpo do 'retira' ser conferido sem a agua)
        var sub = ContasAbertas.Abrir(cx, sessao, op, "Sub", itens: new List<ItemRascunho> { Item(Homer, "DONUT HOMER", 1200, 1000) });
        var loteS = ContasAbertas.PedirCozinha(cx, sub.Id, null, op, false)!;
        cx.Execute("UPDATE outbox SET desistido_em=@e WHERE tipo=@t AND ref_id=@r", new { e = DateTime.Now.ToString("o"), t = PedidoCozinha.TipoNaFila, r = loteS.Id });
        var loteS2 = ContasAbertas.ReenviarLote(cx, loteS.Id, op);
        cx.Execute("UPDATE outbox SET desistido_em=NULL WHERE tipo=@t AND ref_id=@r", new { t = PedidoCozinha.TipoNaFila, r = loteS.Id });
        var prepS = ContasAbertas.PrepararEnvio(cx, cx.ExecuteScalar<string>("SELECT payload FROM outbox WHERE tipo=@t AND ref_id=@r", new { t = PedidoCozinha.TipoNaFila, r = loteS.Id })!);
        checar(prepS.Descartar && (prepS.Motivo ?? "").Contains("substitu"), $"CA-13 lote substituido = descartado ({prepS.Motivo})");

        // CA-21: a Drenagem SEM sessao de nuvem manda a cozinha e nao toca no resto
        var nuvemMorta = new Nuvem(Isolamento.UrlMorta);
        using var dren = new Drenagem(nuvemMorta, Isolamento.UrlMorta);
        await dren.DrenarAsync();
        string? Estado(string id) => cx.ExecuteScalar<string?>("SELECT COALESCE(enviado_em,'') || '|' || COALESCE(ultimo_erro,'') FROM outbox WHERE tipo=@t AND ref_id=@r", new { t = PedidoCozinha.TipoNaFila, r = id });
        var corpoR = kds.Corpos.FirstOrDefault(b => b.Contains(loteR.Id)) ?? "";
        checar(kds.Corpos.Count == 2 && corpoR.Contains("DONUT NINHO") && !corpoR.Contains("AGUA") && kds.Corpos.Any(b => b.Contains(loteS2.Id)),
            $"CA-21 sem sessao de nuvem a cozinha e drenada: 2 POSTs (o retirado sem a agua e o reenviado), zero para cancelada, vazio e substituido ({kds.Corpos.Count})");
        checar(ContasAbertas.EstadoDoLote(cx, loteR.Id) == EstadoLote.NaTv && ContasAbertas.EstadoDoLote(cx, loteS2.Id) == EstadoLote.NaTv,
            "CA-21 os dois que foram ficam NaTv");
        checar(ContasAbertas.EstadoDoLote(cx, loteC.Id) == EstadoLote.NaoFoi && (Estado(loteC.Id) ?? "").Contains("descartado")
               && ContasAbertas.EstadoDoLote(cx, loteV.Id) == EstadoLote.NaoFoi && ContasAbertas.EstadoDoLote(cx, loteS.Id) == EstadoLote.NaoFoi,
            "CA-13 os descartados saem da fila como 'descartado: ...' e o estado e NaoFoi");
        checar(kds.Chaves.All(k => k == "chave-da-savassi-0123456789abcdef"), "CA-21 o POST leva a chave da loja no x-pdv-token");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo='venda' AND enviado_em IS NULL AND ultimo_erro IS NULL") == vendasPendentesAntes,
            "CA-21 os outros tipos continuam exigindo a sessao de nuvem (a venda nao foi tocada)");
        foreach (var id in new[] { retira.Id, vazio.Id, sub.Id }) ContasAbertas.Cancelar(cx, id, op, "teste", gerente);
    }

    // ── CA-8 (fim): EncerrarTurnoDeTeste cancela as contas de teste ─────────
    private static void TurnoDeTesteEncerrado(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"conta_teste_turno_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir(arquivo);
            SemearCatalogo(cx);
            Vendas.GravarConfig(cx, "homologacao", "1");
            var entrada = ModoHomologacao.EntradaDireta(cx)!.Value;
            var (opT, sessaoT) = entrada;
            checar(sessaoT.Teste, "CA-8 o modo de homologacao abre o turno de teste");
            var t = ContasAbertas.Abrir(cx, sessaoT, opT, "Mesa de teste", itens: new List<ItemRascunho> { Item(Agua, "AGUA", 500, 1000) });
            checar(t.Teste, "CA-8 conta aberta no turno de teste nasce de teste");
            Vendas.GravarConfig(cx, "homologacao", "0");
            var n = ModoHomologacao.EncerrarSobras(cx);
            var lida = ContasAbertas.Ler(cx, t.Id)!;
            checar(n == 1 && lida.Status == "cancelada" && lida.CancelAutomatico && (lida.CancelMotivo ?? "").Contains("teste encerrado"),
                "CA-8 EncerrarTurnoDeTeste cancela as contas de teste abertas com 'teste encerrado'");
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox") == 0, "CA-8 nada do turno de teste vai para a fila (nem conta_evento)");
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }

    // ── vigias de fonte ─────────────────────────────────────────────────────
    private static void Fontes(Action<bool, string> checar)
    {
        var nucleo = Fonte(Path.Combine("Pdv.Nucleo", "ContasAbertas.cs")) ?? "";
        var vendas = Fonte(Path.Combine("Pdv.Nucleo", "Vendas.cs")) ?? "";
        var cozinha = Fonte(Path.Combine("Pdv.Nucleo", "PedidoCozinha.cs")) ?? "";
        var dren = Fonte(Path.Combine("Pdv.Nucleo", "Drenagem.cs")) ?? "";
        var banco = Fonte(Path.Combine("Pdv.Nucleo", "Banco.cs")) ?? "";
        var sql = Fonte(Path.Combine("aplicar-hoje", "pdv_conta_evento.sql")) ?? "";
        var suite = Fonte(Path.Combine("Pdv.Testes", "TestesContaAberta.cs")) ?? "";
        checar(nucleo.Length > 0 && sql.Length > 0 && suite.Length > 0, "CA-22 achei ContasAbertas.cs, o SQL da RPC e esta suite");
        foreach (var (nome, texto) in new[] { ("ContasAbertas.cs", nucleo), ("pdv_conta_evento.sql", sql), ("TestesContaAberta.cs", suite) })
            checar(!texto.Contains((char)0x2014) && !texto.Contains((char)0x2013), $"CA-22 nenhum travessao em {nome}");
        checar(!cozinha.Contains("nao existe pedido sem venda paga", StringComparison.Ordinal) && cozinha.Contains("CorpoDoLote", StringComparison.Ordinal),
            "CA-5 o comentario 'nao existe pedido sem venda paga' foi reescrito e CorpoDoLote existe (o Corpo da venda continua, TestesPedidoCozinha intacto)");
        var iFechar = vendas.IndexOf("ContasAbertas.FecharNaVenda(", StringComparison.Ordinal);
        var iInsert = vendas.IndexOf("INSERT INTO venda (", StringComparison.Ordinal);
        checar(iFechar > 0 && iInsert > iFechar, "CA-9 FecharNaVenda roda ANTES do INSERT da venda, na mesma transacao");
        checar(vendas.Contains("conta is null", StringComparison.Ordinal) && vendas.Contains("Kds.DoBalcao(vendaId)", StringComparison.Ordinal),
            "CA-9 Kds.DoBalcao so roda sem conta");
        checar(dren.Contains("ContasAbertas.PrepararEnvio(", StringComparison.Ordinal) && dren.Contains("TiposSemNuvem", StringComparison.Ordinal),
            "CA-13/21 o handler da cozinha consulta a conta antes do POST e a varredura separa o que nao depende da nuvem");
        checar(banco.Contains("CREATE TABLE IF NOT EXISTS conta_aberta", StringComparison.Ordinal) && banco.Contains("CREATE TABLE IF NOT EXISTS conta_lote", StringComparison.Ordinal)
               && !banco.Contains("ALTER TABLE conta_", StringComparison.Ordinal),
            "esquema: as tabelas novas entram no Esquema sem ALTER");
        checar(sql.Contains("pdv_conta_evento", StringComparison.Ordinal) && sql.Contains("p_client_key", StringComparison.Ordinal) && sql.Contains("p_payload", StringComparison.Ordinal),
            "CA-20 o SQL da RPC esta pronto para o dono (aplicar-hoje/pdv_conta_evento.sql)");
    }

    // ── apoio ───────────────────────────────────────────────────────────────
    private static ItemRascunho Item(string id, string nome, long preco, long qtd, string? escolhas = null)
        => new(id, null, nome, "Cat", preco, qtd, "UN", "19053100", null, "102", 0, null, escolhas);

    private static List<LinhaVenda> LinhasDe(ContaAberta c) => c.Itens.Select(i =>
        new LinhaVenda(i.ProdutoId, i.Plu, i.Nome, new Quantidade(i.QtdMilesimos), new Dinheiro(i.PrecoCent),
            new Dinheiro(i.PrecoCent).VezesQtd(i.QtdMilesimos), i.Unidade, i.Ncm, i.Cest, i.Csosn, null, i.Origem,
            Escolhas: Combos.DeJson(i.EscolhasJson))).ToList();

    private static Operador OperadorPorId(SqliteConnection cx, string id) => Operadores.PorId(cx, id)!;

    private static void SemearCatalogo(SqliteConnection cx)
    {
        var agora = DateTime.Now.ToString("o");
        foreach (var (id, plu, nome, cat, preco) in new[]
        {
            (Ninho, "4", "DONUT NINHO", "Donuts", 1200L), (Homer, "1", "DONUT HOMER", "Donuts", 1200L),
            (Agua, "56", "AGUA 500ML", "Bebidas", 500L), (Combo, "87", "COMBO 4 DONUTS", "Combos", 4000L),
        })
            cx.Execute("""
                INSERT INTO produto (id, plu, nome, categoria, preco_cent, unidade, ativo, atualizado, csosn, ncm)
                VALUES (@id, @plu, @nome, @cat, @preco, 'UN', 1, @a, '102', '19053100')
                """, new { id, plu, nome, cat, preco, a = agora });
        cx.Execute("INSERT INTO combo (produto_id, payload) VALUES (@i,@p)", new
        {
            i = Combo,
            p = $$$"""
                {"produto_id":"{{{Combo}}}","plu":"87","nome":"COMBO 4 DONUTS",
                 "grupos":[{"id":"{{{Grupo}}}","nome":"Donuts","min":4,"max":4,
                            "fonte":{"tipo":"categoria","grupo":"Donuts",
                                     "itens":[{"produto_id":"{{{Ninho}}}","plu":"4","nome":"DONUT NINHO"},
                                              {"produto_id":"{{{Homer}}}","plu":"1","nome":"DONUT HOMER"}]}}]}
                """,
        });
    }

    /// <summary>
    /// Um processo VIVO que nao e este, no formato do dono ("pid:inicio"). E um ping de 2 minutos
    /// sem janela, morto no Dispose: deterministico, em vez de apostar num processo alheio.
    /// </summary>
    private static ProcessoVivo OutroProcessoVivo() => new();

    private sealed class ProcessoVivo : IDisposable
    {
        private readonly Process _p = Process.Start(new ProcessStartInfo("ping", "127.0.0.1 -n 120")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        public string Dono => $"{_p.Id}:{_p.StartTime.Ticks}";
        public void Dispose() { try { _p.Kill(); } catch { } _p.Dispose(); }
    }

    private static string? Fonte(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj")))
            {
                var c = Path.Combine(dir.FullName, relativo);
                return File.Exists(c) ? File.ReadAllText(c) : null;
            }
        return null;
    }

    /// <summary>A TV da cozinha de mentira: guarda cada POST /api/pdv/pedidos e responde 201.</summary>
    private sealed class FakeKds : IDisposable
    {
        private readonly HttpListener _l = new();
        public string Url { get; }
        public List<string> Corpos { get; } = new();
        public List<string> Chaves { get; } = new();

        public FakeKds(int porta)
        {
            Url = $"http://127.0.0.1:{porta}";
            _l.Prefixes.Add(Url + "/");
            _l.Start();
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (_l.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _l.GetContextAsync(); } catch { return; }
                try
                {
                    string corpo;
                    using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) corpo = sr.ReadToEnd();
                    lock (Corpos) { Corpos.Add(corpo); Chaves.Add(ctx.Request.Headers["x-pdv-token"] ?? ""); }
                    var bytes = Encoding.UTF8.GetBytes("{\"criado\":true}");
                    ctx.Response.StatusCode = ctx.Request.Url!.AbsolutePath == PedidoCozinha.Rota ? 201 : 404;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch { }
            }
        }

        public void Dispose() { try { _l.Stop(); _l.Close(); } catch { } }
    }
}
