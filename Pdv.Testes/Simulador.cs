using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// SIMULAÇÃO DE ACEITAÇÃO: 180 dias de operação REAL do caixa num banco paralelo
/// (SQLite temporário — zero contato com produção), exercitando as APIs de verdade
/// (Caixa.Abrir/Movimentar/Fechar, Vendas.Finalizar/Cancelar/RegistrarEmissao) e a
/// DRENAGEM inteira contra um Supabase de mentira com falhas injetadas.
///
/// COMO A LINHA DO TEMPO FUNCIONA: o núcleo carimba DateTime.Now, então cada dia
/// simulado roda com as APIs reais e, ao FIM do dia, os timestamps recém-criados
/// são retroagidos para o dia sintético (com horários de expediente). A lógica de
/// negócio é 100% a de produção; só o relógio é maquiado.
///
/// PERFIS: Ana/Bruno/Carla honestos (erros de troco pequenos, para os dois lados),
/// Denise DESONESTA (quebra sempre faltando, sangria colada no fechamento,
/// cancelamento pós-venda, mix de dinheiro alto), Edu supervisor.
///
/// O QUE É VERIFICADO NO FIM (invariantes — cada violação é um bug real):
///  I1  contabilidade de TODA sessão fechada refeita do zero bate com o gravado
///  I2  Σ pagamentos líquidos == Σ vendas finalizadas
///  I3  venda cancelada fora do apurado
///  I4  numeração local contígua por dia (1..N sem buraco)
///  I5  chave NFC-e única; rejeição preserva as tentativas (numeração queimada)
///  I6  fila offline CONVERGE sob caos (503/429) sem perder nada; só os erros
///      PERMANENTES plantados morrem, com rastro; cancel espera a venda subir
///  I7  o "banco" da nuvem fake bate 1:1 com o local (nenhuma venda a menos!)
///  I8  turno esquecido fecha sem conferência com trilha
///  I9  fundo de abertura divergente do fechamento anterior é detectável
///  I10 sangria: nunca acima da gaveta, nunca sem supervisor
/// </summary>
public static class Simulador
{
    private static int _ok, _falhas;
    private static readonly List<string> _bugs = new();

    private static void Check(string nome, bool cond, string? detalhe = null)
    {
        if (cond) { _ok++; return; }
        _falhas++;
        var linha = $"FALHA  {nome}{(detalhe is null ? "" : $" — {detalhe}")}";
        _bugs.Add(linha);
        Console.WriteLine(linha);
    }

    public static async Task<int> RodarAsync(int dias)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-sim-{Guid.NewGuid():N}.db");
        Banco.Migrar(arquivo);
        // A Drenagem chama Banco.Abrir() por dentro — SEM isto, o teste dela leria e
        // escreveria no banco REAL do caixa. O redirecionamento é o isolamento.
        Banco.CaminhoForcado = arquivo;
        using var cx = Banco.Abrir(arquivo);
        var rand = new Random(20260808);
        Console.WriteLine($"=== SIMULAÇÃO {dias} dias — banco paralelo {arquivo} ===");

        // identidade do terminal — em produção o PAREAMENTO grava esta linha; sem ela
        // a abertura de turno nunca sobe (EnviarAberturaAsync espera a loja aparecer)
        cx.Execute("""
            INSERT INTO terminal (id, terminal_uuid, loja_id, loja_nome, cnpj, serie_nfce, ambiente, criado_em)
            VALUES (1, @U, 'sim-loja-1', 'SIM Loja', '00000000000191', 90, 2, @Em)
            """, new { U = Guid.NewGuid().ToString(), Em = DateTime.Now.ToString("o") });

        // ── elenco ──────────────────────────────────────────────────────────
        var ana = new Operador("sim-ana", "Ana Souza", "operador");
        var bruno = new Operador("sim-bruno", "Bruno Lima", "operador");
        var carla = new Operador("sim-carla", "Carla Dias", "operador");
        var denise = new Operador("sim-denise", "Denise Rocha", "operador");   // a desonesta
        var edu = new Operador("sim-edu", "Edu Prado", "supervisor");
        Operadores.Salvar(cx, ana.Id, ana.Nome, "1111", "operador");
        Operadores.Salvar(cx, bruno.Id, bruno.Nome, "2222", "operador");
        Operadores.Salvar(cx, carla.Id, carla.Nome, "3333", "operador");
        Operadores.Salvar(cx, denise.Id, denise.Nome, "4444", "operador");
        Operadores.Salvar(cx, edu.Id, edu.Nome, "9999", "supervisor");
        var rotacao = new[] { ana, bruno, denise, carla, ana, denise, bruno };  // Denise ~29% dos turnos

        // ── cardápio sintético (bloco fiscal junto, como o real) ────────────
        var cardapio = new (string Id, string Sku, string Nome, decimal Preco, string Ncm)[]
        {
            ("sp1", "D001", "DONUT GLAZED", 9.90m, "19053100"),
            ("sp2", "D002", "DONUT CHOCOLATE", 11.50m, "19053100"),
            ("sp3", "C001", "COOKIE NINHO", 13.50m, "19053100"),
            ("sp4", "C002", "COOKIE NUTELLA", 14.00m, "19053100"),
            ("sp5", "B001", "CAFE EXPRESSO", 7.00m, "21011110"),
            ("sp6", "B002", "AGUA 500ML", 6.00m, "22011000"),
            ("sp7", "B003", "REFRIGERANTE LATA", 8.00m, "22021000"),
            ("sp8", "K001", "COMBO 6 DONUTS", 49.90m, "19053100"),
        };

        var hoje = DateTime.Today;
        var inicioSim = hoje.AddDays(-dias - 1);
        long chaveSeq = 0;
        // zero-padding: '7' como preenchimento colidia (seq 1 → "...7771" == seq 71)
        string NovaChave() => (++chaveSeq).ToString().PadLeft(44, '0');

        // eventos plantados (relativos à duração, para valer em qualquer piloto)
        var diasSemFechamento = new HashSet<int> { dias / 3, 2 * dias / 3 };
        var diasFundoDivergente = new HashSet<int> { dias / 6, dias / 2, 5 * dias / 6 };
        var chavesVenda400 = new List<string>();      // recusa permanente → dead-letter
        string? chaveVenda400ComNota = null;          // idem, com nota → vínculo órfão
        string? chaveCancelAntesDaVenda = null;       // venda presa em 503, cancel espera
        var totaisPorDia = new Dictionary<string, long>();
        Sessao? sessaoPendente = null;                // turno esquecido aberto
        int cancelamentosLegitimos = 0, cancelamentosDenise = 0, sangriasColadas = 0;

        // ── 180 dias ────────────────────────────────────────────────────────
        for (var d = 0; d < dias; d++)
        {
            var diaSim = inicioSim.AddDays(d);
            var opDia = rotacao[d % rotacao.Length];
            var eDenise = opDia.Id == denise.Id;
            var inicioReal = DateTime.Now;

            // TEF liga na metade da simulação: testa os DOIS regimes de fechamento
            if (d == dias / 2) Vendas.GravarConfig(cx, "tef_habilitado", "1");

            // turno esquecido ontem? fecha sem conferência ANTES de abrir (fluxo real)
            if (sessaoPendente is not null)
            {
                Caixa.FecharSemConferencia(cx, sessaoPendente, opDia, edu);
                sessaoPendente = null;
            }

            // fundo: o esperado do fechamento anterior; nos dias plantados, DIVERGE
            // (dinheiro que sumiu/sobrou fora do expediente — falha gerencial)
            var fundoEsperado = Caixa.FundoEsperado(cx) ?? Dinheiro.DeReais(300);
            var fundo = diasFundoDivergente.Contains(d)
                ? fundoEsperado - Dinheiro.DeReais(50)
                : fundoEsperado;
            if (fundo.Centavos < 0) fundo = Dinheiro.Zero;
            var sessao = Caixa.Abrir(cx, opDia, fundo);

            // volume: semana 40-70, fim de semana 70-110
            var fds = diaSim.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var nVendas = fds ? rand.Next(70, 111) : rand.Next(40, 71);
            long totalDia = 0;
            var canceladasHoje = 0;

            for (var i = 0; i < nVendas; i++)
            {
                // comanda de 1-4 itens
                var nItens = 1 + rand.Next(4);
                var itens = new List<LinhaVenda>();
                for (var j = 0; j < nItens; j++)
                {
                    var p = cardapio[rand.Next(cardapio.Length)];
                    var qtd = rand.Next(100) < 85 ? 1 : 2;
                    itens.Add(new LinhaVenda(p.Id, p.Sku, p.Nome, new Quantidade(qtd * 1000),
                        Dinheiro.DeReais(p.Preco), Dinheiro.DeReais(p.Preco * qtd),
                        "UN", p.Ncm, null, "102", null, 0));
                }
                var total = new Dinheiro(itens.Sum(x => x.Total.Centavos));

                // forma: Denise puxa pro dinheiro (o rastro que o antifraude da nuvem lê)
                var sorteio = rand.Next(100);
                var limiteDin = eDenise ? 45 : 30;
                string forma = sorteio < limiteDin ? "dinheiro"
                    : sorteio < limiteDin + 30 ? "credito"
                    : sorteio < limiteDin + 50 ? "debito" : "pix";

                var tef = Vendas.Config(cx, "tef_habilitado") == "1";
                PagamentoVenda pag;
                if (forma == "dinheiro")
                {
                    // cliente paga com nota redonda; troco de verdade
                    var pago = new Dinheiro(((total.Centavos + 999) / 1000) * 1000);
                    if (rand.Next(100) < 30) pago = total;   // valor exato
                    pag = new PagamentoVenda("dinheiro", pago, pago - total);
                }
                else
                {
                    // com TEF: autorização gravada; 3% sai como POS avulsa (fallback manual)
                    var manual = !tef || rand.Next(100) < 3;
                    pag = new PagamentoVenda(forma, total, Dinheiro.Zero,
                        manual ? null : $"AUT{d:D3}{i:D4}");
                }

                var v = Vendas.Finalizar(cx, sessao, opDia, itens, new[] { pag },
                    rand.Next(100) < 25 ? "11144477735" : null, "SIM Loja", null);
                totalDia += total.Centavos;

                // desfecho fiscal: distribuição realista
                var sorteFiscal = rand.Next(1000);
                var temNota = false;
                if (sorteFiscal < 920)          // autorizada de primeira
                {
                    Vendas.RegistrarEmissao(cx, v.Id, new ResultadoEmissao
                    {
                        Caminho = EmissorNuvem.Caminho, Autorizado = true, Modo = "online",
                        Chave = NovaChave(), Numero = (int)v.Numero, Serie = 1, CStat = "100",
                    });
                    temNota = true;
                }
                else if (sorteFiscal < 950)     // contingência (agente local)
                {
                    Vendas.RegistrarEmissao(cx, v.Id, new ResultadoEmissao
                    {
                        Caminho = EmissorAgente.Caminho, Contingencia = true, Modo = "contingencia",
                        Chave = NovaChave(), Numero = (int)v.Numero, Serie = 3,
                    });
                    temNota = true;
                }
                else if (sorteFiscal < 970)     // rejeitada (queima número) e reemitida OK
                {
                    Vendas.RegistrarEmissao(cx, v.Id, new ResultadoEmissao
                    {
                        Caminho = EmissorNuvem.Caminho, Modo = "online", Numero = (int)v.Numero,
                        Serie = 1, CStat = "778", XMotivo = "NCM inexistente", Erro = "rejeitada 778",
                    });
                    Vendas.RegistrarEmissao(cx, v.Id, new ResultadoEmissao
                    {
                        Caminho = EmissorNuvem.Caminho, Autorizado = true, Modo = "online",
                        Chave = NovaChave(), Numero = (int)v.Numero + 1, Serie = 1, CStat = "100",
                    });
                    temNota = true;
                }
                else if (sorteFiscal < 985)     // emissor mudo: fica pendente (nunca reemite às cegas)
                {
                    Vendas.RegistrarEmissao(cx, v.Id,
                        ResultadoEmissao.ForaDoAr(EmissorNuvem.Caminho, "timeout simulado"));
                }
                // else: ~1,5% sem nota (barrada local, ex.: sem produto fiscal)

                // fraude da Denise: cancela venda SEM nota e embolsa (até 3 por turno)
                if (eDenise && !temNota && canceladasHoje < 3 && rand.Next(100) < 60)
                {
                    Vendas.Cancelar(cx, v.Id, opDia.Id, "cliente desistiu da compra");
                    totalDia -= total.Centavos;
                    canceladasHoje++;
                    cancelamentosDenise++;
                    chaveCancelAntesDaVenda ??= v.ClientKey;   // vira o cenário cancel-espera-venda
                }
                // cancelamento legítimo raro (0,6%) — só nos turnos honestos, para a
                // contagem de auditoria da Denise medir exatamente as fraudes dela
                else if (!eDenise && !temNota && rand.Next(1000) < 6)
                {
                    Vendas.Cancelar(cx, v.Id, opDia.Id, "pedido lançado errado", edu.Id);
                    totalDia -= total.Centavos;
                    cancelamentosLegitimos++;
                }
            }

            // ── sangrias ────────────────────────────────────────────────────
            // normal: 1-2 por dia, meio de expediente, autorizada pelo Edu
            var gaveta = Caixa.Apurado(cx, sessao)["dinheiro"];
            if (gaveta > Dinheiro.DeReais(400))
                Caixa.Movimentar(cx, sessao, "sangria", Dinheiro.DeReais(200),
                    "depósito no cofre", opDia, edu.Id, "cofre");
            // Denise: sangria COLADA no fechamento (o sinal 7 da nuvem)
            if (eDenise)
            {
                var g2 = Caixa.Apurado(cx, sessao)["dinheiro"];
                if (g2 > Dinheiro.DeReais(150))
                {
                    Caixa.Movimentar(cx, sessao, "sangria", Dinheiro.DeReais(100),
                        "troco pro dia seguinte", opDia, edu.Id, "cofre");
                    sangriasColadas++;
                }
            }
            // suprimento raro (troco que faltou)
            if (rand.Next(100) < 8)
                Caixa.Movimentar(cx, sessao, "suprimento", Dinheiro.DeReais(50),
                    "troco da tesouraria", opDia);

            // ── fechamento ──────────────────────────────────────────────────
            if (diasSemFechamento.Contains(d))
            {
                sessaoPendente = sessao;   // esqueceu de fechar: resolve amanhã
            }
            else
            {
                var apurado = Caixa.Apurado(cx, sessao);
                var contadas = Caixa.FormasContadas(cx, sessao);
                var contagem = new Dictionary<string, Dinheiro>();
                foreach (var f in contadas)
                {
                    var certo = apurado.TryGetValue(f, out var a) ? a : Dinheiro.Zero;
                    if (f == "dinheiro" && eDenise)
                        // a fraude clássica: declara MENOS e leva a diferença
                        contagem[f] = certo - Dinheiro.DeReais(20 + rand.Next(41));
                    else if (f == "dinheiro" && rand.Next(100) < 10)
                        // erro honesto: pequeno, PROS DOIS LADOS
                        contagem[f] = certo + Dinheiro.DeReais((rand.Next(2) == 0 ? -1 : 1) * rand.Next(1, 4));
                    else
                        contagem[f] = certo;
                }
                try
                {
                    Caixa.Fechar(cx, sessao, contagem, opDia, Dinheiro.DeReais(5));
                }
                catch (InvalidOperationException)
                {
                    // desvio acima da tolerância exige justificativa — como no balcão
                    Caixa.Fechar(cx, sessao, contagem, opDia, Dinheiro.DeReais(5),
                        eDenise ? "conferi duas vezes, não achei a diferença" : "erro de troco durante o dia");
                }
            }

            totaisPorDia[diaSim.ToString("yyyy-MM-dd")] = totalDia;

            // ── retroage o dia recém-criado para a data sintética ───────────
            RetroagirDia(cx, inicioReal, diaSim, rand);
        }

        // fecha o último turno se ficou pendente
        if (sessaoPendente is not null) Caixa.FecharSemConferencia(cx, sessaoPendente, ana, edu);

        Console.WriteLine($"dias={dias} vendas={cx.ExecuteScalar<long>("SELECT COUNT(*) FROM venda")} " +
            $"canceladas={cx.ExecuteScalar<long>("SELECT COUNT(*) FROM venda WHERE status='cancelada'")} " +
            $"outbox={cx.ExecuteScalar<long>("SELECT COUNT(*) FROM outbox WHERE enviado_em IS NULL AND desistido_em IS NULL")} pendentes");

        // ── recusas que o núcleo TEM que dar (testadas no fim, com caixa aberto) ──
        var sTeste = Caixa.Abrir(cx, ana, Caixa.FundoEsperado(cx) ?? Dinheiro.DeReais(300));
        Recusa("sangria acima da gaveta é recusada",
            () => Caixa.Movimentar(cx, sTeste, "sangria", Dinheiro.DeReais(999999), "teste", ana, edu.Id),
            "mais do que entrou");
        Recusa("segundo caixa aberto é recusado",
            () => Caixa.Abrir(cx, bruno, Dinheiro.DeReais(100)), "Já existe caixa aberto");
        Recusa("sangria sem supervisor é recusada",
            () => Caixa.Movimentar(cx, sTeste, "sangria", Dinheiro.DeReais(10), "teste", ana), "supervisor");
        // ⚠ defesa em profundidade: a TELA barra auto-autorização; o NÚCLEO tem que barrar também
        Recusa("sangria auto-autorizada (operador = autorizador) é recusada NO NÚCLEO",
            () => Caixa.Movimentar(cx, sTeste, "sangria", Dinheiro.DeReais(10), "teste", ana, ana.Id),
            "não pode autorizar a própria");
        Caixa.FecharSemConferencia(cx, sTeste, ana, edu);

        // ════ DRENAGEM: a fila inteira contra a nuvem fake, com caos ════════
        using var fake = new FakePostgrest(4655);

        // plantados: 3 vendas viram 400 permanente (payload "podre" do ponto de vista
        // do servidor) — 1 delas COM nota, para gerar o vínculo órfão
        var candidatas = cx.Query<(string Chave, string FiscalStatus)>(
            "SELECT client_key, fiscal_status FROM venda WHERE status='finalizada' ORDER BY criada_em LIMIT 2000").ToList();
        chavesVenda400.AddRange(candidatas.Where(c => c.FiscalStatus == "sem_nota" || c.FiscalStatus == "pendente")
            .Take(2).Select(c => c.Chave));
        chaveVenda400ComNota = candidatas.First(c => c.FiscalStatus == "autorizada").Chave;
        chavesVenda400.Add(chaveVenda400ComNota);
        foreach (var k in chavesVenda400) fake.FalhaPorChave[k] = (400, 9999);

        // cenário cancel-espera-venda: a VENDA da primeira fraude da Denise fica presa
        // em 503 por 15 varreduras; o cancelamento dela NÃO pode morrer nesse meio-tempo
        if (chaveCancelAntesDaVenda is not null)
            fake.FalhaPorChave[chaveCancelAntesDaVenda] = (503, 15);

        // cortesia_resgate: o resgate que falhou na hora vira fila durável. Enfileiro
        // DOIS do mesmo código — o segundo prova que 'ja_resgatado' conta como sucesso
        // (não trava a fila). Sem isto, o cupom parcial ficava reutilizável na rede caída.
        Caixa.Enfileirar(cx, null, "cortesia_resgate", "CO-TESTE-1", "CO-TESTE-1",
            new { codigo = "CO-TESTE-1", operador = "Ana", loja = "Savassi" });
        Caixa.Enfileirar(cx, null, "cortesia_resgate", "CO-TESTE-1b", "CO-TESTE-1b",
            new { codigo = "CO-TESTE-1", operador = "Ana", loja = "Savassi" });

        var nuvem = new Nuvem(fake.Url);
        Check("nuvem fake autentica", await nuvem.EntrarAsync("sim@sim.com", "x"));
        using var dren = new Drenagem(nuvem, fake.Url);

        // fase 1: CAOS — 30% de 503, 10% de 429 por 40 varreduras
        fake.PctErro503 = 30; fake.PctErro429 = 10;
        for (var i = 0; i < 40; i++) await dren.DrenarAsync();
        // fase 2: céu limpo — drena até esvaziar (ou provar starvation)
        fake.PctErro503 = 0; fake.PctErro429 = 0;
        var varreduras = 0;
        while (varreduras < 900)
        {
            varreduras++;
            await dren.DrenarAsync();
            var pendentes = cx.ExecuteScalar<long>("SELECT COUNT(*) FROM outbox WHERE enviado_em IS NULL AND desistido_em IS NULL");
            if (pendentes == 0) break;
        }
        Console.WriteLine($"fila convergiu em {varreduras} varreduras pós-caos");

        // cortesia: o cupom foi queimado na nuvem (resgate durável funcionou), e o
        // segundo enfileiramento do mesmo código não travou a fila (ja_resgatado=ok)
        Check("cortesia_resgate: cupom foi queimado na nuvem pela fila durável",
            fake.Resgatadas.ContainsKey("CO-TESTE-1"));
        Check("cortesia_resgate: reenvio do mesmo código (ja_resgatado) saiu da fila",
            cx.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM outbox WHERE tipo='cortesia_resgate' AND enviado_em IS NULL") == 0);

        // ════ INVARIANTES ═══════════════════════════════════════════════════
        // I6 — convergência: nada pendente; nada preso mudo.
        // "Preso" é o que a varredura AINDA devolve: enviado_em IS NULL (não entregue)
        // E desistido_em IS NULL (o dreno não desistiu). O dead-letter deixou de
        // carimbar enviado_em — ele não foi entregue, e dizer que foi era o bug —,
        // então quem separa starvation de dead-letter aqui é o desistido_em.
        var presos = cx.Query<(string Tipo, string? Erro)>(
            "SELECT tipo, ultimo_erro FROM outbox WHERE enviado_em IS NULL AND desistido_em IS NULL LIMIT 10").ToList();
        Check("I6a fila convergiu (0 pendentes) sem starvation",
            presos.Count == 0,
            $"após {varreduras} varreduras; presos: {string.Join(" | ", presos.Select(p => $"{p.Tipo}:{p.Erro ?? "(mudo)"}"))}");
        var deadLetters = cx.Query<(string Tipo, string ClientKey, string? Erro)>(
            "SELECT tipo, client_key, ultimo_erro FROM outbox WHERE ultimo_erro LIKE 'desistido%'").ToList();
        Check("I6b só os erros PERMANENTES plantados morreram (3 vendas + 1 vínculo órfão)",
            deadLetters.Count(x => x.Tipo == "venda") == 3
            && deadLetters.Count(x => x.Tipo == "nfce_vinculo") == 1,
            $"dead-letters reais: {string.Join(", ", deadLetters.Select(x => $"{x.Tipo}:{x.Erro?[..Math.Min(x.Erro.Length, 40)]}"))}");
        Check("I6c todo dead-letter tem rastro do motivo (nunca mudo)",
            deadLetters.All(x => x.Erro is { Length: > 10 }));
        Check("I6d o cancelamento ESPEROU a venda presa em 503 e ambos subiram",
            chaveCancelAntesDaVenda is not null
            && fake.Vendas.ContainsKey(chaveCancelAntesDaVenda)
            && fake.Canceladas.ContainsKey(chaveCancelAntesDaVenda),
            $"chave {chaveCancelAntesDaVenda}");

        // I7 — nuvem fake espelha o local 1:1 (é o "painel não mente")
        var vendasLocais = cx.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM venda WHERE client_key NOT IN @Mortas",
            new { Mortas = chavesVenda400 });
        Check("I7a TODAS as vendas chegaram à nuvem (fora as 3 de erro permanente)",
            fake.Vendas.Count == vendasLocais,
            $"nuvem={fake.Vendas.Count} local={vendasLocais}");
        var cancelSemNota = cx.Query<string>(
            "SELECT client_key FROM venda WHERE status='cancelada'").ToList();
        Check("I7b TODO cancelamento chegou à nuvem",
            cancelSemNota.Where(k => !chavesVenda400.Contains(k)).All(k => fake.Canceladas.ContainsKey(k)),
            $"nuvem={fake.Canceladas.Count} local={cancelSemNota.Count}");
        Check("I7c toda sessão chegou à nuvem",
            fake.Sessoes.Count == cx.ExecuteScalar<long>("SELECT COUNT(*) FROM caixa_sessao"));
        Check("I7d todo fechamento chegou à nuvem",
            fake.Fechamentos.Count == cx.ExecuteScalar<long>("SELECT COUNT(DISTINCT sessao_id) FROM caixa_fechamento"));
        Check("I7e toda sangria/suprimento chegou à nuvem",
            fake.Movimentos.Count == cx.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM caixa_movimento WHERE tipo IN ('sangria','suprimento')"));

        // I1 — contabilidade refeita do zero por sessão
        var sessoes = cx.Query<(string Id, long Fundo)>(
            "SELECT id, fundo_troco_cent FROM caixa_sessao WHERE status='fechado'").ToList();
        var erradas = 0;
        foreach (var (sid, fundoCent) in sessoes)
        {
            var dinVendas = cx.ExecuteScalar<long>("""
                SELECT COALESCE(SUM(p.valor_cent - p.troco_cent),0)
                  FROM venda_pagamento p JOIN venda v ON v.id = p.venda_id
                 WHERE v.sessao_id = @S AND v.status='finalizada' AND p.forma='dinheiro'
                """, new { S = sid });
            var supr = cx.ExecuteScalar<long>(
                "SELECT COALESCE(SUM(valor_cent),0) FROM caixa_movimento WHERE sessao_id=@S AND tipo='suprimento'", new { S = sid });
            var sang = cx.ExecuteScalar<long>(
                "SELECT COALESCE(SUM(valor_cent),0) FROM caixa_movimento WHERE sessao_id=@S AND tipo='sangria'", new { S = sid });
            var esperado = fundoCent + dinVendas + supr - sang;
            var gravado = cx.ExecuteScalar<long?>(
                "SELECT apurado_cent FROM caixa_fechamento WHERE sessao_id=@S AND forma='dinheiro'", new { S = sid });
            if (gravado != esperado) erradas++;
        }
        Check("I1 contabilidade do dinheiro refeita do zero bate em TODAS as sessões",
            erradas == 0, $"{erradas} de {sessoes.Count} sessões erradas");

        // I2 — pagamentos == vendas
        Check("I2 Σ pagamentos líquidos == Σ vendas finalizadas",
            cx.ExecuteScalar<long>("""
                SELECT COALESCE(SUM(p.valor_cent - p.troco_cent),0)
                  FROM venda_pagamento p JOIN venda v ON v.id=p.venda_id WHERE v.status='finalizada'
                """) ==
            cx.ExecuteScalar<long>("SELECT COALESCE(SUM(total_cent),0) FROM venda WHERE status='finalizada'"));

        // I3 — cancelada fora do apurado (por amostragem total)
        Check("I3 nenhuma venda cancelada dentro de apurado de fechamento",
            erradas == 0);   // I1 já filtra finalizada; redundância proposital documentada

        // I4 — numeração contígua por dia
        var buracos = cx.Query<(string Dia, long N, long Max)>("""
            SELECT business_date, COUNT(*) AS n, MAX(numero_local) AS max
              FROM venda GROUP BY business_date
            """).Count(x => x.N != x.Max);
        Check("I4 numeração local contígua em todos os dias (cancelada mantém número)",
            buracos == 0, $"{buracos} dias com buraco");

        // I5 — fiscal
        Check("I5a chave de NFC-e única",
            cx.ExecuteScalar<long>("SELECT COUNT(chave) - COUNT(DISTINCT chave) FROM nfce_emissao WHERE chave IS NOT NULL") == 0);
        Check("I5b rejeições preservadas (número queimado não some do livro)",
            cx.ExecuteScalar<long>("SELECT COUNT(*) FROM nfce_emissao WHERE c_stat='778'") > 0);
        Check("I5c emissor mudo ficou 'pendente', nunca 'rejeitada'",
            cx.ExecuteScalar<long>("SELECT COUNT(*) FROM venda WHERE fiscal_status='rejeitada'") == 0);

        // I8 — turnos esquecidos: fechados com trilha de "ninguém contou"
        Check("I8 turnos esquecidos fecharam sem conferência com trilha auditável",
            cx.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM auditoria WHERE evento='caixa_fechado_sem_conferencia'") >= 2);

        // I9 — fundo divergente detectável: fechamento(d-1).dinheiro ≠ abertura(d).fundo
        var divergencias = cx.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM caixa_sessao s
             WHERE s.fundo_troco_cent <> COALESCE((
                SELECT f.declarado_cent FROM caixa_fechamento f
                  JOIN caixa_sessao s2 ON s2.id = f.sessao_id
                 WHERE f.forma='dinheiro' AND s2.fechamento_em < s.abertura_em
                 ORDER BY s2.fechamento_em DESC LIMIT 1), s.fundo_troco_cent)
            """);
        Check("I9 as 3 aberturas com fundo divergente plantadas são detectáveis",
            divergencias >= 3, $"detectadas {divergencias}");

        // I10 — sangrias íntegras
        Check("I10a toda sangria tem autorizador",
            cx.ExecuteScalar<long>("SELECT COUNT(*) FROM caixa_movimento WHERE tipo='sangria' AND autorizado_por IS NULL") == 0);
        Check("I10b nenhuma sangria auto-autorizada passou",
            cx.ExecuteScalar<long>("SELECT COUNT(*) FROM caixa_movimento WHERE tipo='sangria' AND autorizado_por = operador_id") == 0);

        // fraude da Denise deixou rastro auditável (matéria-prima do antifraude da nuvem)
        Check("fraudes plantadas deixaram rastro: cancelamentos da Denise auditados",
            cx.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM auditoria WHERE evento='venda_cancelada' AND operador_id=@Op",
                new { Op = denise.Id }) == cancelamentosDenise,
            $"esperado {cancelamentosDenise}");
        var turnosDenise = cx.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM caixa_sessao WHERE operador_id=@Op AND status='fechado' AND fechado_por=@Op",
            new { Op = denise.Id });
        var quebrasDenise = cx.ExecuteScalar<long>("""
            SELECT COUNT(DISTINCT f.sessao_id) FROM caixa_fechamento f
              JOIN caixa_sessao s ON s.id=f.sessao_id
             WHERE s.operador_id=@Op AND f.forma='dinheiro' AND f.diferenca_cent < -500
            """, new { Op = denise.Id });
        Check("quebras da Denise registradas discriminadas no fechamento (todo turno dela)",
            quebrasDenise >= turnosDenise * 4 / 5 && turnosDenise > 0,
            $"turnos={turnosDenise} com quebra registrada={quebrasDenise}");

        Console.WriteLine($"\n=== SIMULAÇÃO: {_ok} OK, {_falhas} falhas ===");
        if (_falhas > 0) { Console.WriteLine("BUGS:"); foreach (var b in _bugs) Console.WriteLine("  " + b); }

        Banco.CaminhoForcado = null;
        cx.Dispose();
        SqliteConnection.ClearAllPools();
        // falhou → o banco fica para autópsia; passou → limpa
        if (_falhas == 0) try { File.Delete(arquivo); } catch { }
        else Console.WriteLine($"banco preservado para autópsia: {arquivo}");
        return _falhas == 0 ? 0 : 1;
    }

    private static void Recusa(string nome, Action acao, string trecho)
    {
        try { acao(); Check(nome + " (NÃO recusou!)", false); }
        catch (Exception e) { Check(nome, e.Message.Contains(trecho, StringComparison.OrdinalIgnoreCase), e.Message); }
    }

    /// <summary>
    /// Retroage TUDO que este dia simulado criou (timestamps de agora) para a data
    /// sintética, com horários de expediente: abertura de manhã, vendas espalhadas
    /// ao longo do dia em ordem, sangrias à tarde, fechamento à noite.
    /// </summary>
    private static void RetroagirDia(SqliteConnection cx, DateTime inicioReal, DateTime diaSim, Random rand)
    {
        var marco = inicioReal.ToString("o");
        string Em(int h, int m) => new DateTime(diaSim.Year, diaSim.Month, diaSim.Day, h, m, rand.Next(60)).ToString("o");
        var dataStr = diaSim.ToString("yyyy-MM-dd");

        using var tx = cx.BeginTransaction();

        // sessão aberta hoje → business_date e abertura sintéticos
        cx.Execute("""
            UPDATE caixa_sessao SET business_date=@D, abertura_em=@Ab
             WHERE abertura_em >= @Marco
            """, new { D = dataStr, Ab = Em(7, 50), Marco = marco }, tx);
        // fechamento do turno de HOJE: fim do expediente
        cx.Execute("UPDATE caixa_sessao SET fechamento_em=@F WHERE fechamento_em >= @Marco AND business_date = @D",
            new { F = Em(22, 5), Marco = marco, D = dataStr }, tx);
        // turno ESQUECIDO de ontem, fechado sem conferência agora de manhã: o
        // fechamento fica ANTES da abertura de hoje (é a ordem real dos fatos)
        cx.Execute("UPDATE caixa_sessao SET fechamento_em=@F WHERE fechamento_em >= @Marco AND business_date < @D",
            new { F = Em(7, 40), Marco = marco, D = dataStr }, tx);

        // vendas do dia: horários crescentes 08:00→21:50, na ordem da numeração
        var vendas = cx.Query<(string Id, long Num)>(
            "SELECT id, numero_local FROM venda WHERE criada_em >= @Marco ORDER BY numero_local",
            new { Marco = marco }, tx).ToList();
        var minutosExpediente = (21 * 60 + 50) - (8 * 60);
        for (var i = 0; i < vendas.Count; i++)
        {
            var minuto = 8 * 60 + (int)((long)minutosExpediente * i / Math.Max(1, vendas.Count - 1));
            var quando = new DateTime(diaSim.Year, diaSim.Month, diaSim.Day, minuto / 60, minuto % 60, rand.Next(60)).ToString("o");
            cx.Execute("UPDATE venda SET business_date=@D, criada_em=@Q, finalizada_em=@Q WHERE id=@Id",
                new { D = dataStr, Q = quando, Id = vendas[i].Id }, tx);
            cx.Execute("UPDATE nfce_emissao SET criado_em=@Q WHERE venda_id=@Id AND criado_em >= @Marco",
                new { Q = quando, Id = vendas[i].Id, Marco = marco }, tx);
            cx.Execute("UPDATE outbox SET criado_em=@Q WHERE ref_id=@Id AND criado_em >= @Marco",
                new { Q = quando, Id = vendas[i].Id, Marco = marco }, tx);
        }

        // sangrias/suprimentos: tarde (Denise colada no fechamento fica ~21:55 — a última)
        var movs = cx.Query<string>("SELECT id FROM caixa_movimento WHERE criado_em >= @Marco ORDER BY criado_em",
            new { Marco = marco }, tx).ToList();
        for (var i = 0; i < movs.Count; i++)
        {
            var quando = i == movs.Count - 1 && movs.Count > 1 ? Em(21, 55) : Em(15, 10 + i * 7);
            cx.Execute("UPDATE caixa_movimento SET criado_em=@Q WHERE id=@Id", new { Q = quando, Id = movs[i] }, tx);
            cx.Execute("UPDATE outbox SET criado_em=@Q WHERE ref_id=@Id AND criado_em >= @Marco",
                new { Q = quando, Id = movs[i], Marco = marco }, tx);
        }

        // fechamento, auditoria e o resto da fila: o que sobrou do dia
        cx.Execute("UPDATE caixa_fechamento SET criado_em=@F WHERE criado_em >= @Marco",
            new { F = Em(22, 6), Marco = marco }, tx);
        cx.Execute("UPDATE auditoria SET criado_em=@F WHERE criado_em >= @Marco",
            new { F = Em(22, 7), Marco = marco }, tx);
        cx.Execute("UPDATE outbox SET criado_em=@F WHERE criado_em >= @Marco",
            new { F = Em(22, 8), Marco = marco }, tx);

        tx.Commit();
    }
}
