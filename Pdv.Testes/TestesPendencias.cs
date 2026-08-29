using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;
// Dialogo.Encaixar é a metade do conserto que mora na TELA: a quebra de linha do
// relatório. É código puro (nenhum WPF dentro), então a suíte prova a regra sem
// abrir janela — que é a única forma de o corte do print não voltar em silêncio.
using Pdv.Telas;

namespace Pdv.Testes;

/// <summary>
/// O AVISO DE PENDÊNCIA TEM QUE TER SAÍDA.
///
/// O QUE QUEBRA NA LOJA SE ISTO QUEBRAR: o operador aperta Sincronizar, o caixa
/// responde "16 venda(s) que o servidor não tem — R$ 102.626,50", ele chama o
/// gerente, o gerente arruma o cadastro no painel, o operador aperta de novo — e o
/// número continua 16. Aviso que não se apaga é aviso que se aprende a ignorar; no
/// dia em que forem 17 porque uma venda REAL não subiu, ninguém vai olhar.
///
/// Cenário real (banco do caixa, 29/08/2026): 26 linhas do outbox estavam em
/// dead-letter com <c>enviado_em</c> carimbado por um build antigo — 16 vendas,
/// R$ 102.626,50, todas recusadas com HTTP 409 ("Key (operator_id)=(003e0aa7…) is
/// not present in table employees"). A drenagem não as reprocessava (o WHERE exige
/// enviado_em IS NULL), o contador as somava para sempre, e NENHUMA linha de código
/// do PDV era capaz de tirar uma delas daquele estado. Beco sem saída.
///
/// As quatro regras que este arquivo vigia:
///  1. tratado o motivo, um toque em Sincronizar reenvia e o aviso ZERA;
///  2. o que falha de forma definitiva volta a um estado terminal COM MOTIVO —
///     uma tentativa por toque, nunca um laço;
///  3. venda de TESTE (homologação) não conta como pendência nem sobe no reenvio —
///     senão o roteiro da PayGo vira faturamento na DRE;
///  4. o aviso diz O QUE FAZER, não só um número.
/// </summary>
public static class TestesPendencias
{
    private const int Porta = 4671;
    private const int PortaCetico = 4672;

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"pendencias_teste_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir(arquivo);

            var op = new Operador("op-pend", "Bia", "operador");
            Operadores.Salvar(cx, op.Id, op.Nome, "4321", "operador");
            var sessao = Caixa.Abrir(cx, op, Dinheiro.DeReais(100));

            using var fake = new FakePostgrest(Porta);
            var nuvem = new Nuvem(fake.Url);
            checar(await nuvem.EntrarAsync("pend@teste.com", "x"), "nuvem fake autentica");
            using var dren = new Drenagem(nuvem, fake.Url);

            // ── 1. TRÊS VENDAS REAIS QUE O PAINEL RECUSA (o 409 do incidente) ────
            var recusadas = new List<string>();
            foreach (var reais in new[] { 6.00m, 30.50m, 12.00m })
                recusadas.Add(Vender(cx, sessao, op, reais).ClientKey);
            foreach (var k in recusadas) fake.FalhaPorChave[k] = (409, 9999);

            for (var i = 0; i < Drenagem.MaxTentativas + 2; i++) await dren.DrenarAsync();

            var paradas = Sincronizacao.VendasNaoEntregues();
            checar(paradas.Desistidas == 3 && paradas.Valor.Centavos == 4850,
                $"as 3 recusadas viraram dead-letter e somam R$ 48,50 (viu {paradas.Desistidas} / {paradas.Valor.Formatado()})");

            // ── 2. O AVISO INSTRUI ──────────────────────────────────────────────
            // "3 vendas paradas" é um número solto. Quem está no balcão precisa da
            // causa (que ele não pode adivinhar do 23503) e do próximo passo.
            var aviso = paradas.Resumo ?? "";
            checar(aviso.Contains("48,50", StringComparison.Ordinal),
                "o aviso leva o VALOR parado, não só a contagem");
            checar(paradas.Motivo is { Length: > 0 } && !paradas.Motivo.Contains("23503", StringComparison.Ordinal),
                $"o aviso traduz o motivo para quem está no balcão (viu: {paradas.Motivo ?? "<nulo>"})");
            checar(aviso.Contains("O QUE FAZER", StringComparison.OrdinalIgnoreCase),
                "o aviso diz o que fazer — número solto na tela não é instrução");
            checar(aviso.Contains("Sincronizar", StringComparison.OrdinalIgnoreCase),
                "o aviso nomeia o botão que resolve depois de o motivo ser tratado");

            // O rastro REAL do caixa da loja, byte a byte. Se a tradução não pegar
            // ESTA string, o dono lê JSON de Postgres na tela do balcão.
            const string RastroDaLoja =
                """desistido após 12 tentativas — HTTP 409: {"code":"23503","details":"Key """
                + """(operator_id)=(003e0aa7-99e2-4453-98ea-8cb129a4b0e9) is not present in table \"employees\".""";
            checar(Sincronizacao.MotivoHumano(RastroDaLoja) is string traduzido
                   && traduzido.Contains("operador", StringComparison.OrdinalIgnoreCase)
                   && traduzido.Contains("painel", StringComparison.OrdinalIgnoreCase)
                   && !traduzido.Contains("23503", StringComparison.Ordinal),
                $"o 409 real do caixa vira uma frase acionável (viu: {Sincronizacao.MotivoHumano(RastroDaLoja)})");

            // ── 2b. O AVISO NÃO PODE PARECER QUE A VENDA FALHOU ─────────────────
            // O print de 29/08 abria com "3 venda(s) que o servidor não tem", e o dono
            // leu o que estava escrito: "3 vendas não se concretizaram". Não é isso —
            // a venda ACONTECEU, o cliente levou o produto, o dinheiro está na gaveta;
            // o que ficou para trás é o REGISTRO dela no painel. Quem lê no susto
            // cancela venda certa e mexe em caixa fechado: o aviso sai mais caro que
            // o problema que ele denuncia.
            checar(aviso.StartsWith("NENHUMA VENDA FOI PERDIDA", StringComparison.Ordinal),
                $"a PRIMEIRA linha mata o susto (viu: {aviso.Split('\n')[0]})");
            checar(aviso.IndexOf("PERDIDA", StringComparison.Ordinal) < aviso.IndexOf("R$", StringComparison.Ordinal),
                "o susto morre ANTES de o primeiro número aparecer na tela");
            checar(aviso.Contains("gaveta", StringComparison.Ordinal),
                "o aviso diz onde o dinheiro está: na gaveta");
            checar(aviso.Contains("REGISTRO", StringComparison.Ordinal),
                "…e nomeia o que de fato não subiu: o REGISTRO, não a venda");
            checar(aviso.Contains("faturamento", StringComparison.Ordinal)
                   && aviso.Contains("DRE", StringComparison.Ordinal),
                "o aviso diz o que isso afeta DE VERDADE: faturamento e DRE do painel");
            checar(aviso.Contains("NÃO MUDA", StringComparison.Ordinal)
                   && aviso.Contains("cupom", StringComparison.Ordinal),
                "…e o que NÃO afeta (venda, caixa, cupom) — senão o operador imagina o pior");

            // ── 2c. O AVISO NOMEIA AS VENDAS ────────────────────────────────────
            // A outra pergunta do dono foi "que vendas são essas?". Sem o número que
            // ele grita no balcão não dá para conferir nem para contar ao gerente.
            checar(paradas.Lista is { Count: 3 } && paradas.Lista.All(v => v.Desistiu),
                $"a consulta traz QUAIS vendas são (viu {paradas.Lista?.Count.ToString() ?? "<nula>"})");
            checar(aviso.Contains("nº 1, 2 e 3", StringComparison.Ordinal),
                $"o aviso chama as vendas pelo número do balcão (viu: {aviso})");
            // O dia vai junto porque numero_local reinicia a cada dia operacional —
            // "nº 3" sozinho é ambíguo depois da virada das 05h.
            var diaEsperado = DateTime.Parse(Caixa.DiaOperacional()).ToString("dd/MM");
            checar(aviso.Contains("(hoje)", StringComparison.Ordinal)
                   || aviso.Contains($"({diaEsperado})", StringComparison.Ordinal),
                "…e de que dia elas são (o número reinicia a cada dia operacional)");

            // ── 3. VENDA DE TESTE NÃO É PENDÊNCIA ───────────────────────────────
            // Réplica exata das 3 linhas de 24/08 do caixa: venda de homologação que
            // um build antigo enfileirou. Ela nunca vai subir (e não DEVE subir), então
            // contá-la só engorda um alarme que ninguém consegue zerar.
            Vendas.GravarConfig(cx, "homologacao", "1");
            var teste = Vender(cx, sessao, op, 990.00m);
            Vendas.GravarConfig(cx, "homologacao", "0");
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE ref_id = @V", new { V = teste.Id }) == 0,
                "venda de homologação nem entra na fila (regra que já existia)");
            EnfileirarLegado(cx, teste, "desistido após 12 tentativas — HTTP 409: legado");
            fake.FalhaPorChave[teste.ClientKey] = (409, 9999);

            var comTeste = Sincronizacao.VendasNaoEntregues();
            checar(comTeste.Desistidas == 3 && comTeste.Valor.Centavos == 4850,
                $"venda de TESTE fica fora do contador de pendências (viu {comTeste.Desistidas} / {comTeste.Valor.Formatado()})");

            // ── 4. O MOTIVO FOI TRATADO: O AVISO TEM QUE ZERAR ──────────────────
            // É o gesto do dono: gerente cadastra o operador no painel, operador
            // aperta Sincronizar. Antes desta correção o número não se mexia — nada
            // no PDV sabia tirar uma linha do dead-letter.
            foreach (var k in recusadas) fake.FalhaPorChave[k] = (409, 0);
            await Sincronizacao.ExecutarAsync(nuvem, null, dren, null, default, reenviarDesistidas: true);

            checar(recusadas.All(fake.Vendas.ContainsKey),
                "tratado o motivo, um toque em Sincronizar reenvia as 3 vendas desistidas");
            var depois = Sincronizacao.VendasNaoEntregues();
            checar(depois.Total == 0 && depois.Resumo is null,
                $"o aviso SOME quando não há mais pendência (viu {depois.Total}: {depois.Resumo})");
            checar(cx.ExecuteScalar<int>($"""
                SELECT COUNT(*) FROM outbox
                 WHERE ref_id IN (SELECT id FROM venda WHERE client_key IN @Keys)
                   AND (COALESCE(ultimo_erro,'') LIKE 'desistido%'
                     OR COALESCE(ultimo_erro,'') LIKE 'reaberto%')
                """, new { Keys = recusadas }) == 0,
                "o rastro 'desistido' não sobrevive ao envio confirmado (senão o contador nunca zera)");

            // ── 5. VENDA DE TESTE NÃO SOBE NEM NO REENVIO ───────────────────────
            checar(!fake.Vendas.ContainsKey(teste.ClientKey),
                "o reenvio NÃO ressuscita venda de homologação (ela viraria receita na DRE)");
            checar(cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM outbox WHERE ref_id = @V "
                + "AND COALESCE(ultimo_erro,'') LIKE 'desistido%'", new { V = teste.Id }) == 1,
                "a linha da venda de teste fica intocada, terminal e auditável — e fora do aviso");

            // ── 6. FALHA DEFINITIVA: TERMINAL COM MOTIVO, UMA TENTATIVA POR TOQUE ─
            // O perigo do conserto é trocar "nunca tenta" por "tenta para sempre":
            // 16 linhas mortas batendo no servidor a cada 45 s, em silêncio.
            var eterna = Vender(cx, sessao, op, 7.50m);
            fake.FalhaPorChave[eterna.ClientKey] = (409, 9999);
            for (var i = 0; i < Drenagem.MaxTentativas + 2; i++) await dren.DrenarAsync();

            long Tentativas() => cx.ExecuteScalar<long>(
                "SELECT tentativas FROM outbox WHERE ref_id = @V AND tipo = 'venda'", new { V = eterna.Id });
            int Restam() => fake.FalhaPorChave[eterna.ClientKey].Restam;

            checar(cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM outbox WHERE ref_id = @V AND desistido_em IS NOT NULL "
                + "AND COALESCE(ultimo_erro,'') <> ''", new { V = eterna.Id }) == 1,
                "falha permanente vira estado terminal VISÍVEL, com motivo gravado");

            var batidasAntes = Restam();
            for (var i = 0; i < 5; i++) await dren.DrenarAsync();
            checar(Restam() == batidasAntes,
                "o ciclo automático não reprocessa dead-letter (nada de laço silencioso)");

            var tentativasAntes = Tentativas();
            await Sincronizacao.ExecutarAsync(nuvem, null, dren, null, default, reenviarDesistidas: true);
            checar(batidasAntes - Restam() == 1,
                $"um toque em Sincronizar = UMA tentativa a mais, não um novo ciclo de {Drenagem.MaxTentativas}");
            checar(Tentativas() == tentativasAntes + 1,
                "a tentativa extra é contada (o teto não é reiniciado pelo reenvio manual)");
            checar(cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM outbox WHERE ref_id = @V AND desistido_em IS NOT NULL", new { V = eterna.Id }) == 1,
                "falhou de novo: volta ao estado terminal na hora, com o motivo novo");
            checar(Sincronizacao.VendasNaoEntregues().Desistidas == 1,
                "e continua visível no aviso enquanto o motivo real não for resolvido");

            // ── 7. 3 NÚMEROS CABEM; 40 NÃO — E O QUE SOBRA TEM QUE SER DITO ─────
            // Listar 40 números seguidos é a mesma doença de outro jeito: vira parede
            // de texto e ninguém confere. Mas cortar em silêncio é pior — o dono
            // acharia que são só 6, e sumiriam 34 vendas do radar dele.
            var muitas = Enumerable.Range(1, 40)
                .Select(i => new VendaParada(100 + i, i <= 20 ? "2026-08-27" : "2026-08-28", true))
                .ToList();
            var lotado = new VendasParadas(0, 40, Dinheiro.DeReais(3122.45m),
                "o operador que fez a venda não está cadastrado no painel", muitas).Resumo ?? "";
            var quais = lotado.Split('\n').FirstOrDefault(x => x.StartsWith("Quais:", StringComparison.Ordinal)) ?? "";
            checar(quais.Contains("nº 101, 102, 103, 104, 105 e 106", StringComparison.Ordinal),
                $"com 40 paradas, o aviso nomeia as 6 primeiras (viu: {quais})");
            checar(!quais.Contains("140", StringComparison.Ordinal),
                "e NÃO despeja os 40 números — parede de texto não se lê");
            checar(quais.Contains("e mais 34", StringComparison.Ordinal),
                $"as que não couberam são DITAS, não cortadas em silêncio (viu: {quais})");
            checar(quais.Contains("27/08", StringComparison.Ordinal) && quais.Contains("28/08", StringComparison.Ordinal),
                "…com os dias delas, que é por onde o gerente procura no painel");

            // ── 8. E TUDO ISSO PRECISA CABER NA TELA ────────────────────────────
            // O defeito nº 1 do print: o corpo do relatório era NoWrap (para não
            // estragar o alinhamento da tabela) e a frase longa morria na borda —
            // "…Em 3 delas o envio D". Dialogo.Encaixar quebra SÓ quem não cabe.
            const int Colunas = 82;   // Consolas 14 na janela de 720 px (medido)
            var tabela = new[]
            {
                "Cardápio:  sem novidade",
                "Fotos:     nenhuma nova",
                "Notas:     nenhuma para enviar",
            };
            var corpo = Dialogo.Encaixar(string.Join("\n", tabela.Append("⚠ " + aviso)), Colunas);
            var saida = corpo.Split('\n');
            checar(saida.All(x => x.Length <= Colunas),
                $"nenhuma linha estoura a largura da tela (a maior tem {saida.Max(x => x.Length)} de {Colunas})");
            checar(tabela.All(saida.Contains),
                "a linha da tabela sai byte a byte igual — quem já cabia não é tocado, e é isso que preserva o alinhamento");
            checar(saida.Any(x => x.StartsWith("   ", StringComparison.Ordinal)),
                "a continuação da frase longa entra RECUADA, para não se disfarçar de item novo da lista");
            // Desfazendo a quebra (continuação recuada volta a ser um espaço) tem que
            // sair EXATAMENTE o texto que entrou: nada de caractere perdido na borda,
            // que é o defeito do print.
            checar(corpo.Replace("\n   ", " ") == string.Join("\n", tabela) + "\n⚠ " + aviso,
                "nenhum caractere se perde na quebra — o aviso chega inteiro na tela");

            checar(Dialogo.Encaixar("a\n\nb", Colunas) == "a\n\nb",
                "linha em branco (separador de parágrafo) sobrevive à quebra");

            // A linha mais larga do FECHAMENTO tem 76 caracteres e a janela antiga
            // dava 69: o relatório de fechamento também vinha cortando valor em
            // silêncio. Na janela nova ela cabe — e sai intacta, com o padding das
            // colunas preservado.
            var maiorDoFechamento =
                $"{"dinheiro",-9} {"contou",-7} {"R$ 102.626,50",11}  esperado {"R$ 205.253,00",11}  FALTA R$ 102.626,50";
            checar(maiorDoFechamento.Length <= Colunas,
                $"a linha mais larga do fechamento cabe ({maiorDoFechamento.Length} de {Colunas} colunas)");
            checar(Dialogo.Encaixar(maiorDoFechamento, Colunas) == maiorDoFechamento,
                "…e passa intocada pelo Encaixar (padding de coluna é alinhamento, não texto)");

            // Palavra sozinha maior que a linha inteira — rastro cru de erro, URL.
            // Sem o corte no osso o laço não termina: o PDV congelaria ao mostrar
            // um motivo que a tradução não reconheceu.
            var gigante = new string('x', 300);
            var picado = Dialogo.Encaixar(gigante, Colunas);
            checar(picado.Split('\n').All(x => x.Length <= Colunas)
                   && picado.Replace("\n", "").Replace(" ", "").Length == 300,
                "palavra maior que a linha é partida — nem perdida, nem em laço infinito");

            // Medida impossível (janela minúscula, fonte que não mediu): devolve o
            // texto cru. Errar a medida tem que degradar para feio, nunca para cortado
            // — o TextWrapping.Wrap do TextBlock segura a linha nesse caso.
            checar(Dialogo.Encaixar("linha comprida qualquer", 0) == "linha comprida qualquer",
                "sem medida confiável o texto sai inteiro (o Wrap da tela é a rede)");

            // ── 6. A RAIZ: DOIS OPERADORES, MESMO CPF, IDS DIFERENTES ───────────
            // O 409 de cima não é situação de operação, é DEFEITO. O assistente cria o
            // primeiro operador no passo 1, com um id que só existe nesta máquina;
            // o pareamento é o passo 5, e só na sincronização seguinte a MESMA pessoa
            // desce do painel, com outro id. Os dois ficavam vivos lado a lado e o
            // LOCAL continuava sendo quem logava — então toda venda saía assinada por
            // um id que `employees` não tem. Cadastrar mais funcionário não resolvia:
            // o servidor não pergunta "existe algum?", pergunta "existe ESTE id?".
            const string CpfDele = "529.982.247-25";
            const string PinDoCaixa = "4477";       // o que a loja digita todo dia
            Operadores.Salvar(cx, "nasceu-no-caixa", "Brenno", PinDoCaixa, "gerente", CpfDele);

            var (hashPainel, saltPainel) = Operadores.GerarHash("918273");
            fake.OperadoresDoPainel.Add(new FakePostgrest.OperadorDoPainel(
                "id-do-painel", "brenno", hashPainel, saltPainel, "gerente", CpfDele));
            // Segundo funcionário do painel SEM CPF: serve de controle. Dois operadores
            // sem documento são duas pessoas — vazio não casa com vazio.
            var (hashSemCpf, saltSemCpf) = Operadores.GerarHash("606060");
            fake.OperadoresDoPainel.Add(new FakePostgrest.OperadorDoPainel(
                "id-sem-cpf", "Sem documento", hashSemCpf, saltSemCpf, "operador", null));

            checar(await nuvem.BaixarOperadoresAsync(cx) == 2, "o painel entregou os dois funcionários");

            // ── O PISO: a sincronização não pode trancar a loja fora do caixa ──────
            //
            // Com a reconciliação, o operador criado NO CAIXA vira lápide e a única
            // identidade viva passa a ser a do painel. Isso conserta as vendas
            // recusadas — e abre um risco pior, que o cético mediu: se o painel
            // responder 200 com LISTA VAZIA (não é rede caindo, que nem commita), ou
            // se alguém desligar o dono lá, a descida desativa todo mundo e ninguém
            // abre o turno amanhã. A saída de emergência fechou junto: o _admin_ nasce
            // inativo e recadastrar pelo CPF esbarra na guarda nova.
            //
            // Ninguém no balcão resolve isso às 7h. Demissão vale no caixa, MENOS a
            // última: se a desativação zeraria o acesso, o mais recente volta a valer.
            {
                var guardados = fake.OperadoresDoPainel.ToList();
                // Chega no caso REAL: depois da fusão, os únicos ativos são do painel.
                // Sem isto um operador local sobrevivente segura a loja de pé e o piso
                // nem é exercitado — o teste passaria sem provar nada.
                // Guarda quem eu vou desativar, para DEVOLVER o estado no fim: este
                // bloco vive no meio de um roteiro maior, e mexer no cenário dos outros
                // faz o teste seguinte falhar por culpa deste.
                var locaisAtivos = cx.Query<string>(
                    "SELECT id FROM operador WHERE ativo=1 AND da_nuvem=0").ToList();
                cx.Execute("UPDATE operador SET ativo=0 WHERE da_nuvem=0");
                checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM operador WHERE ativo=1 AND da_nuvem=0") == 0,
                    "piso: (montagem) só sobraram identidades do painel, como depois da fusão");
                fake.OperadoresDoPainel.Clear();          // o painel responde 200 e lista VAZIA
                await nuvem.BaixarOperadoresAsync(cx);

                checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM operador WHERE ativo=1") > 0,
                    "piso: lista vazia do painel NÃO deixa a loja sem ninguém que abra o caixa");
                checar(cx.ExecuteScalar<int>(
                           "SELECT COUNT(*) FROM auditoria WHERE evento='operador_piso_reerguido'") == 1,
                    "piso: e o socorro fica na auditoria — não é para passar despercebido");

                // O piso é EXCEÇÃO, não regra: com o painel respondendo de novo, ele
                // volta a mandar em quem está ativo.
                foreach (var o in guardados) fake.OperadoresDoPainel.Add(o);
                checar(await nuvem.BaixarOperadoresAsync(cx) == 2,
                    "piso: painel respondendo de novo, a descida volta ao normal");

                // Devolve o cenário como estava, inclusive desfazendo o socorro do piso
                // (que reergueu uma lápide) — senão as asserções seguintes medem o
                // estado que ESTE bloco deixou, não o que elas montaram.
                cx.Execute("UPDATE operador SET ativo=0 WHERE da_nuvem=0");
                foreach (var id in locaisAtivos)
                    cx.Execute("UPDATE operador SET ativo=1 WHERE id=@Id", new { Id = id });
            }

            // O CPF desce como o painel escreveu (com pontuação). Guardado assim, o
            // login por CPF — que limpa a pontuação antes de consultar — nunca acharia
            // a linha, e a identidade forte morreria em silêncio.
            checar(cx.ExecuteScalar<string?>("SELECT cpf FROM operador WHERE id='id-do-painel'") == "52998224725",
                "o CPF que desce da nuvem é normalizado para só dígitos");

            checar(cx.ExecuteScalar<int>(
                       "SELECT COUNT(*) FROM operador WHERE ativo=1 " +
                       "AND replace(replace(cpf,'.',''),'-','')='52998224725'") == 1,
                "uma pessoa, UMA identidade ativa — a duplicata não sobrevive à descida");
            checar(cx.ExecuteScalar<string?>(
                       "SELECT id FROM operador WHERE ativo=1 AND cpf='52998224725'") == "id-do-painel",
                "quem fica de pé é o id do PAINEL (é o único que existe em employees)");

            // O QUE NÃO PODE QUEBRAR: a loja abre amanhã com o PIN de sempre. A senha
            // do painel pode ser outra e ninguém na loja a conhece.
            checar(Operadores.EntrarComCpf(cx, CpfDele, PinDoCaixa)?.Id == "id-do-painel",
                "o PIN que a loja usa continua entrando — e já entra COMO o id do painel");

            // A linha antiga não é apagada nem tem o id trocado: venda, sessão de caixa,
            // movimento e auditoria apontam para ela. Ela vira LÁPIDE — inativa, e
            // dizendo de quem ela é.
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM operador WHERE id='nasceu-no-caixa'") == 1,
                "a linha local continua existindo — o histórico aponta para ela e não pode ficar órfão");
            checar(cx.ExecuteScalar<string?>(
                       "SELECT mesmo_que FROM operador WHERE id='nasceu-no-caixa'") == "id-do-painel",
                "…e ela diz que é a MESMA PESSOA do id do painel");

            // Vazio não casa com vazio: a Bia (local, sem CPF) e o "Sem documento" do
            // painel são duas pessoas, e continuam duas.
            checar(cx.ExecuteScalar<string?>($"SELECT mesmo_que FROM operador WHERE id='{op.Id}'") is null
                   && cx.ExecuteScalar<long>($"SELECT ativo FROM operador WHERE id='{op.Id}'") == 1,
                "operador local SEM CPF não é fundido com operador da nuvem sem CPF");

            // ── 6b. O TURNO QUE JÁ ESTAVA ABERTO ────────────────────────────────
            // Quem logou às 8h carrega o id velho na memória; a sincronização das 10h
            // não desloga ninguém (fila no balcão). Sem isto, o resto do dia inteiro
            // continuaria nascendo com o id errado — o defeito de novo, por um turno.
            var turnoJaAberto = new Operador("nasceu-no-caixa", "Brenno", "gerente");
            var vendaDoTurno = Vender(cx, sessao, turnoJaAberto, 7.00m);
            checar(cx.ExecuteScalar<string?>("SELECT operador_id FROM venda WHERE id=@i",
                       new { i = vendaDoTurno.Id }) == "id-do-painel",
                "venda feita com a identidade velha na memória grava o id do PAINEL");
            checar(cx.ExecuteScalar<string?>("SELECT payload FROM outbox WHERE ref_id=@i",
                       new { i = vendaDoTurno.Id })?.Contains("\"p_operator_id\":\"id-do-painel\"") == true,
                "…e o que vai para a nuvem também — é o payload que o servidor confere");

            // A DUPLA ASSINATURA NÃO PODE CAIR NA MESMA ARMADILHA. Quem opera com a
            // identidade velha e "autoriza" com a do painel é a MESMA PESSOA: comparar
            // os ids crus abriria justo a sangria para autorização de si mesmo.
            var sozinho = "";
            try
            {
                Caixa.Movimentar(cx, sessao, "sangria", Dinheiro.DeReais(1), "teste de dupla assinatura",
                    turnoJaAberto, autorizadoPor: "id-do-painel");
            }
            catch (InvalidOperationException ex) { sozinho = ex.Message; }
            checar(sozinho.Contains("própria sangria", StringComparison.Ordinal),
                $"identidade velha + autorização do painel = MESMA pessoa: sangria barrada (viu: {sozinho})");

            // ── 6c. NÃO DEIXAR NASCER DE NOVO ───────────────────────────────────
            var recusou = "";
            try { Operadores.Salvar(cx, "sosia", "Outro Brenno", "5566", "operador", CpfDele); }
            catch (InvalidOperationException ex) { recusou = ex.Message; }
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM operador WHERE id='sosia'") == 0,
                "operador local com CPF que o painel já governa não é criado");
            checar(recusou.Contains("painel", StringComparison.OrdinalIgnoreCase),
                $"…e a recusa manda para o PAINEL, que é onde se resolve (viu: {recusou})");

            // ── 6d. SINCRONIZAR DE NOVO NÃO PODE TRANCAR A LOJA ─────────────────
            // A descida regrava o operador a cada ciclo. Se ela reescrevesse a senha
            // por cima, o conserto duraria até a próxima sincronização — e aí ninguém
            // entra no caixa.
            checar(await nuvem.BaixarOperadoresAsync(cx) == 2, "segunda descida entrega os mesmos dois");
            checar(Operadores.EntrarComCpf(cx, CpfDele, PinDoCaixa)?.Id == "id-do-painel",
                "o PIN da loja sobrevive à sincronização seguinte");

            // Mas quando alguém MUDA a senha no painel, isso é um ato deliberado e vale:
            // senão o painel perderia para sempre o poder de trocar a senha dessa pessoa.
            var (hashNovo, saltNovo) = Operadores.GerarHash("135790");
            fake.OperadoresDoPainel[0] = fake.OperadoresDoPainel[0] with { PinHash = hashNovo, PinSalt = saltNovo };
            checar(await nuvem.BaixarOperadoresAsync(cx) == 2, "terceira descida, agora com senha nova no painel");
            checar(Operadores.EntrarComCpf(cx, CpfDele, "135790")?.Id == "id-do-painel"
                   && Operadores.EntrarComCpf(cx, CpfDele, PinDoCaixa) is null,
                "senha trocada NO PAINEL passa a valer, e a antiga para de valer");

            // ── 6e. O ASSISTENTE NÃO PODE FAZER NASCER O SEGUNDO CADASTRO ───────
            // O passo 1 pede o operador, o passo 5 pareia — e o pareamento agora traz a
            // lista do painel. Chegando no Salvar com a pessoa já conhecida, o certo é
            // ADOTAR a identidade do painel, não criar outra. Recusar seria pior: o
            // instalador ficaria sem ninguém para abrir o caixa hoje.
            var quantosAntes = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM operador");
            var adm = Operadores.SalvarAdministrador(cx, null, "Brenno Dono", "7788", CpfDele);
            checar(adm.AdotadoDoPainel && adm.Id == "id-do-painel",
                $"o assistente ADOTA quem o painel já governa (viu: {adm.Id}, adotado={adm.AdotadoDoPainel})");
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM operador") == quantosAntes,
                "…e nenhuma linha nova nasce — é isso que evitaria o 409 na origem");
            checar(Operadores.EntrarComCpf(cx, CpfDele, "7788")?.Id == "id-do-painel",
                "a senha escolhida no assistente é a que abre o caixa (a do painel ninguém na loja conhece)");
            checar(await nuvem.BaixarOperadoresAsync(cx) == 2
                   && Operadores.EntrarComCpf(cx, CpfDele, "7788")?.Id == "id-do-painel",
                "…e ela sobrevive à sincronização seguinte");
            checar(cx.ExecuteScalar<int>(
                       "SELECT COUNT(*) FROM auditoria WHERE evento='admin_adotado_do_painel'") == 1,
                "a adoção fica no rastro de auditoria (quem instalou precisa poder explicar depois)");

            // Painel que ainda não conhece esta pessoa (ou instalação sem rede no
            // pareamento): o cadastro local NASCE mesmo assim — o caixa tem que abrir
            // hoje —, e a reconciliação na descida funde os dois quando ele descer.
            var novo = Operadores.SalvarAdministrador(cx, null, "Sócia", "8899", "111.444.777-35");
            checar(!novo.AdotadoDoPainel && Operadores.EntrarComCpf(cx, "111.444.777-35", "8899")?.Id == novo.Id,
                "sem ninguém do painel com aquele CPF, o cadastro local nasce e entra");
            checar(cx.ExecuteScalar<string?>("SELECT cpf FROM operador WHERE id=@i", new { i = novo.Id })
                       == "11144477735",
                "o CPF é gravado só com dígitos (é assim que o login por CPF procura)");
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }

        await CeticoAsync(checar);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // A RECONCILIAÇÃO SOB DESCONFIANÇA — cenário isolado, banco próprio.
    //
    // O bloco 6 acima prova que a fusão acontece. Este aqui não pergunta se ela
    // acontece: pergunta o que ela QUEBRA. Reencena o caso real do caixa da Savassi
    // (CPF 095.952.706-01 — com o zero à esquerda que campo numérico e planilha
    // comem) com turno ABERTO, venda, sangria e auditoria já apontando para o id que
    // nasceu na máquina, e cobra as quatro coisas caras: a loja abre amanhã, nada
    // fica órfão, sincronizar de novo não muda nada, e ninguém legítimo é barrado.
    // ══════════════════════════════════════════════════════════════════════════
    private static async Task CeticoAsync(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"cetico_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        using var fake = new FakePostgrest(PortaCetico);
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir(arquivo);
            var nuvem = new Nuvem(fake.Url);
            checar(await nuvem.EntrarAsync("cetico@teste.com", "x"), "cético: nuvem fake autentica");

            // O CPF do caso real. Escolhido de propósito: começa com ZERO, e é o zero
            // perdido que faz "a mesma pessoa" deixar de casar consigo mesma.
            const string CpfReal = "09595270601";
            const string PinDaLoja = "1357";        // o que o balcão digita todo dia
            const string PinDoPainel = "998877";    // o que ninguém na loja conhece

            // ── O ESTADO ANTES: nascido NO CAIXA, com turno de pé e histórico ────
            Operadores.Salvar(cx, "local-brenno", "Brenno", PinDaLoja, "gerente", "095.952.706-01");
            var outro = new Operador("local-ana", "Ana", "gerente");
            Operadores.Salvar(cx, outro.Id, outro.Nome, "2468", "gerente", "111.444.777-35");

            var eu = new Operador("local-brenno", "Brenno", "gerente");
            var turno = Caixa.Abrir(cx, eu, Dinheiro.DeReais(200));
            var vendaVelha = Vender(cx, turno, eu, 25.00m);
            Caixa.Movimentar(cx, turno, "sangria", Dinheiro.DeReais(50), "malote", eu, autorizadoPor: outro.Id);
            var auditoriaAntes = cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM auditoria WHERE operador_id='local-brenno'");
            checar(auditoriaAntes > 0 && cx.ExecuteScalar<string?>(
                       "SELECT operador_id FROM caixa_sessao WHERE id=@i", new { i = turno.Id }) == "local-brenno",
                "cético: antes da descida, turno/venda/auditoria estão no id que nasceu na máquina");

            // ── O PAINEL MANDA A MESMA PESSOA, com o zero comido pelo campo ──────
            var (hPainel, sPainel) = Operadores.GerarHash(PinDoPainel);
            fake.OperadoresDoPainel.Add(new FakePostgrest.OperadorDoPainel(
                "painel-brenno", "brenno", hPainel, sPainel, "gerente", "9595270601"));
            await nuvem.BaixarOperadoresAsync(cx);

            // ── 1. O PIN. Se este cair, a loja não abre amanhã. ──────────────────
            var entrou = Operadores.EntrarComCpf(cx, CpfReal, PinDaLoja);
            checar(entrou?.Id == "painel-brenno",
                $"cético/PIN: a senha da LOJA entra, e já entra como o id do painel (viu: {entrou?.Id ?? "<recusado>"})");
            checar(Operadores.EntrarComCpf(cx, "095.952.706-01", PinDaLoja)?.Id == "painel-brenno",
                "cético/PIN: com pontuação também entra");
            checar(Operadores.EntrarComCpf(cx, CpfReal, PinDoPainel) is null,
                "cético/PIN: a senha do painel (que ninguém na loja conhece) não passa a valer sozinha");
            checar(Operadores.CpfChave("9595270601") == CpfReal,
                "cético/CPF: o zero à esquerda comido pelo painel é reposto antes de comparar");

            // ── 2. ÓRFÃOS. O que apontava para o id velho ainda resolve? ─────────
            checar(!cx.Query("PRAGMA foreign_key_check").Any(),
                "cético/órfão: nenhuma chave estrangeira quebrada depois da fusão");
            checar(cx.ExecuteScalar<int>("""
                    SELECT COUNT(*) FROM caixa_sessao s
                     WHERE NOT EXISTS (SELECT 1 FROM operador o WHERE o.id = s.operador_id)
                    """) == 0,
                "cético/órfão: a sessão de caixa continua achando o operador dela");
            checar(cx.ExecuteScalar<string?>("SELECT operador_id FROM venda WHERE id=@i",
                       new { i = vendaVelha.Id }) == "local-brenno",
                "cético/órfão: a venda JÁ FECHADA não foi reescrita — histórico é histórico");
            checar(Operadores.IdCanonico(cx, "local-brenno") == "painel-brenno",
                "cético/órfão: …e o id velho RESOLVE para o do painel (a lápide liga os dois)");
            checar(cx.ExecuteScalar<int>(
                       "SELECT COUNT(*) FROM auditoria WHERE operador_id='local-brenno'") == auditoriaAntes,
                "cético/órfão: a auditoria antiga continua no nome de quem realmente fez");
            checar(cx.ExecuteScalar<long>("SELECT ativo FROM operador WHERE id='local-brenno'") == 0
                   && cx.ExecuteScalar<int>(
                       "SELECT COUNT(*) FROM operador WHERE ativo=1 AND cpf=@c", new { c = CpfReal }) == 1,
                "cético/órfão: uma identidade ATIVA só, e a lápide fora do caminho do login");

            // ── 3. IDEMPOTÊNCIA. A descida roda a cada ciclo, a vida toda. ───────
            string Retrato() => string.Join("|", cx.Query<string>(
                "SELECT id||';'||nome||';'||perfil||';'||ativo||';'||da_nuvem||';'||" +
                "COALESCE(cpf,'-')||';'||COALESCE(mesmo_que,'-')||';'||pin_hash FROM operador ORDER BY id"));
            var depoisDe1 = Retrato();
            await nuvem.BaixarOperadoresAsync(cx);
            await nuvem.BaixarOperadoresAsync(cx);
            checar(Retrato() == depoisDe1,
                "cético/idempotência: rodar 3x deixa a tabela EXATAMENTE como depois da 1ª");
            checar(Operadores.EntrarComCpf(cx, CpfReal, PinDaLoja)?.Id == "painel-brenno",
                "cético/idempotência: e a senha da loja continua entrando depois da 3ª");
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM operador WHERE mesmo_que IS NOT NULL") == 1,
                "cético/idempotência: uma lápide só — a fusão não se repete a cada ciclo");

            // ── 4. TURNO ABERTO na hora da fusão: o resto do dia nasce certo? ────
            var vendaDepois = Vender(cx, turno, eu, 33.00m);   // `eu` = identidade velha na memória
            checar(cx.ExecuteScalar<string?>("SELECT operador_id FROM venda WHERE id=@i",
                       new { i = vendaDepois.Id }) == "painel-brenno",
                "cético/turno: venda do MESMO turno, com o objeto antigo em memória, nasce com o id do painel");
            var payload = cx.ExecuteScalar<string?>("SELECT payload FROM outbox WHERE ref_id=@i",
                new { i = vendaDepois.Id }) ?? "";
            checar(payload.Contains("p_operator_id\":\"painel-brenno", StringComparison.Ordinal),
                "cético/turno: e o PAYLOAD — o que o servidor confere contra employees — também");
            var payloadVelho = cx.ExecuteScalar<string?>("SELECT payload FROM outbox WHERE ref_id=@i",
                new { i = vendaVelha.Id }) ?? "";
            checar(payloadVelho.Contains("p_operator_id\":\"local-brenno", StringComparison.Ordinal),
                "cético/turno: o payload de ANTES continua com o id velho — a fusão NÃO repara a fila já gravada");
            var sessaoNaFila = cx.ExecuteScalar<string?>(
                "SELECT payload FROM outbox WHERE tipo='caixa_sessao' AND ref_id=@i", new { i = turno.Id }) ?? "";
            checar(sessaoNaFila.Contains("local-brenno", StringComparison.Ordinal),
                "cético/turno: idem a sessão de caixa enfileirada ANTES da fusão (sobe com o id velho)");
            var fechado = Caixa.Fechar(cx, turno, new Dictionary<string, Dinheiro>(), eu,
                Dinheiro.DeReais(9999));
            checar(fechado.Count >= 0 && cx.ExecuteScalar<string?>(
                       "SELECT fechado_por FROM caixa_sessao WHERE id=@i", new { i = turno.Id }) == "local-brenno",
                "cético/turno: `fechado_por` NÃO é canonizado — fica no id velho (só histórico local, sem FK)");

            // ── 5. CASOS DE BORDA DO CPF ────────────────────────────────────────
            // Sem documento não há identidade: dois operadores sem CPF são duas pessoas.
            Operadores.Salvar(cx, "local-sem-cpf", "Sem doc", "3690", "operador");
            var (hSem, sSem) = Operadores.GerarHash("4590");
            fake.OperadoresDoPainel.Add(new FakePostgrest.OperadorDoPainel(
                "painel-sem-cpf", "Outro sem doc", hSem, sSem, "operador", null));
            // CPF que NÃO EXISTE (dígitos repetidos) dos dois lados: também não casa.
            cx.Execute("INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,cpf,ativo,da_nuvem,atualizado) " +
                       "VALUES ('local-lixo','Legado',@H,@S,'operador','00000000000',1,0,@Em)",
                new { H = hSem, S = sSem, Em = DateTime.Now.ToString("o") });
            var (hLixo, sLixo) = Operadores.GerarHash("7412");
            fake.OperadoresDoPainel.Add(new FakePostgrest.OperadorDoPainel(
                "painel-lixo", "Lixo", hLixo, sLixo, "operador", "000.000.000-00"));
            await nuvem.BaixarOperadoresAsync(cx);
            checar(cx.ExecuteScalar<string?>("SELECT mesmo_que FROM operador WHERE id='local-sem-cpf'") is null
                   && cx.ExecuteScalar<long>("SELECT ativo FROM operador WHERE id='local-sem-cpf'") == 1,
                "cético/CPF: vazio não casa com vazio — os dois sem documento continuam duas pessoas");
            checar(cx.ExecuteScalar<string?>("SELECT mesmo_que FROM operador WHERE id='local-lixo'") is null,
                "cético/CPF: 000.000.000-00 não funde dois estranhos (CPF inválido não é identidade)");

            // ── 6. A GUARDA BARRA ALGUÉM LEGÍTIMO? ──────────────────────────────
            // Feito AQUI, antes de existir um gêmeo no painel: editar o próprio
            // cadastro é o caso legítimo que uma guarda de CPF mal escrita quebra
            // primeiro — ela acharia a própria linha e diria "esse CPF já é seu".
            var erroEdicao = "";
            try { Operadores.Salvar(cx, "painel-brenno", "brenno", "5150", "gerente", CpfReal); }
            catch (InvalidOperationException ex) { erroEdicao = ex.Message; }
            checar(erroEdicao.Length == 0,
                $"cético/guarda: editar o PRÓPRIO cadastro (mesmo CPF, mesmo id) não é barrado (viu: {erroEdicao})");
            var erroOutro = "";
            try { Operadores.Salvar(cx, "novo-legitimo", "Colega", "6042", "operador", "111.444.777-35"); }
            catch (InvalidOperationException ex) { erroOutro = ex.Message; }
            checar(erroOutro.Contains("já é de", StringComparison.Ordinal)
                   && !erroOutro.Contains("PAINEL", StringComparison.Ordinal),
                $"cético/guarda: CPF de outro LOCAL continua barrado com a mensagem antiga (viu: {erroOutro})");
            var erroNovo = "";
            try { Operadores.Salvar(cx, "novo-qualquer", "Novato", "7295", "operador", "529.982.247-25"); }
            catch (InvalidOperationException ex) { erroNovo = ex.Message; }
            checar(erroNovo.Length == 0
                   && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM operador WHERE id='novo-qualquer'") == 1,
                $"cético/guarda: quem o painel NÃO conhece continua sendo cadastrável (viu: {erroNovo})");

            // Dois operadores da NUVEM com o mesmo CPF: a fusão não pode encadear
            // lápide em lápide nem entrar em laço.
            var (hGemeo, sGemeo) = Operadores.GerarHash("321654");
            fake.OperadoresDoPainel.Add(new FakePostgrest.OperadorDoPainel(
                "painel-gemeo", "Brenno de novo", hGemeo, sGemeo, "gerente", CpfReal));
            await nuvem.BaixarOperadoresAsync(cx);
            checar(cx.ExecuteScalar<string?>("SELECT mesmo_que FROM operador WHERE id='local-brenno'") == "painel-brenno",
                "cético/CPF: com dois do painel no mesmo CPF, a lápide não é reapontada nem encadeada");
            checar(Operadores.IdCanonico(cx, "local-brenno") == "painel-brenno",
                "cético/CPF: …e resolver o id velho continua terminando (sem laço)");
            var duplicadosNaNuvem = cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM operador WHERE ativo=1 AND da_nuvem=1 AND cpf=@c", new { c = CpfReal });
            checar(duplicadosNaNuvem == 2,
                $"cético/CPF: dois cadastros do PAINEL no mesmo CPF sobrevivem os dois — o caixa não tem como escolher (viu {duplicadosNaNuvem})");

            // Laço proposital: se alguém gravar A→B→A, resolver não pode travar a VENDA.
            cx.Execute("UPDATE operador SET mesmo_que='local-brenno' WHERE id='painel-brenno'");
            var terminou = false;
            var tarefa = Task.Run(() => { Operadores.IdCanonico(cx, "local-brenno"); terminou = true; });
            checar(tarefa.Wait(TimeSpan.FromSeconds(5)) && terminou,
                "cético/CPF: encadeamento circular resolve mesmo assim — nunca trava a gravação da venda");
            cx.Execute("UPDATE operador SET mesmo_que=NULL WHERE id='painel-brenno'");

            // LINHA LEGADA COM PONTUAÇÃO: o build antigo gravava o CPF do painel do
            // jeito que foi digitado lá, e toda loja que ATUALIZA o PDV já tem essas
            // linhas no banco. Contra o HEAD este é o furo que deixava a duplicata
            // nascer: a guarda comparava "52998224725" (digitado) com "529.982.247-25"
            // (gravado) e não via nada. Encenado direto no banco porque é assim que a
            // máquina da loja está antes da primeira descida do build novo.
            cx.Execute("INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,cpf,ativo,da_nuvem,atualizado) " +
                       "VALUES ('painel-legado','Legado do painel',@H,@S,'operador','123.456.789-09',1,1,@Em)",
                new { H = hSem, S = sSem, Em = DateTime.Now.ToString("o") });
            var erroLegado = "";
            try { Operadores.Salvar(cx, "duplicata", "Legado do caixa", "9911", "operador", "12345678909"); }
            catch (InvalidOperationException ex) { erroLegado = ex.Message; }
            checar(erroLegado.Contains("PAINEL", StringComparison.Ordinal)
                   && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM operador WHERE id='duplicata'") == 0,
                $"cético/guarda: CPF gravado COM PONTUAÇÃO por build antigo também barra (viu: {erroLegado})");

            // …e a fusão também enxerga através da PONTUAÇÃO, pelo caminho REAL: o
            // painel manda "123.456.789-09" e o local foi digitado do mesmo jeito. A
            // ordem imita a vida — o local nasce primeiro (assistente, antes do
            // pareamento) e o do painel desce depois.
            cx.Execute("DELETE FROM operador WHERE id='painel-legado'");
            Operadores.Salvar(cx, "local-legado", "Legado", "6273", "operador", "123.456.789-09");
            var (hLeg, sLeg) = Operadores.GerarHash("505050");
            fake.OperadoresDoPainel.Add(new FakePostgrest.OperadorDoPainel(
                "painel-legado", "Legado do painel", hLeg, sLeg, "operador", "123.456.789-09"));
            await nuvem.BaixarOperadoresAsync(cx);
            checar(cx.ExecuteScalar<string?>("SELECT mesmo_que FROM operador WHERE id='local-legado'")
                       == "painel-legado",
                "cético/guarda: CPF com pontuação nos dois lados funde igual (é o que o painel manda de verdade)");
            checar(cx.ExecuteScalar<string?>("SELECT cpf FROM operador WHERE id='painel-legado'") == "12345678909",
                "cético/guarda: …e a linha do painel fica gravada só com dígitos, que é como o login procura");
            checar(Operadores.EntrarComCpf(cx, "123.456.789-09", "6273")?.Id == "painel-legado",
                "cético/guarda: a senha da loja entra pelo id do painel mesmo com o CPF pontuado no painel");

            // Demitido no painel (da_nuvem=1, ativo=0) e recontratado na loja:
            // a guarda barra — e é preciso saber disso, porque antes deixava passar.
            fake.OperadoresDoPainel.RemoveAll(o => o.Id == "painel-gemeo");
            fake.OperadoresDoPainel.Add(new FakePostgrest.OperadorDoPainel(
                "painel-demitido", "Demitido", hGemeo, sGemeo, "operador", "390.533.447-05", Ativo: false));
            await nuvem.BaixarOperadoresAsync(cx);
            var erroDemitido = "";
            try { Operadores.Salvar(cx, "recontratado", "Voltou", "8531", "operador", "390.533.447-05"); }
            catch (InvalidOperationException ex) { erroDemitido = ex.Message; }
            checar(erroDemitido.Contains("PAINEL", StringComparison.Ordinal),
                $"cético/guarda: CPF de DEMITIDO no painel é barrado e manda resolver lá (viu: {erroDemitido})");
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }

    private static VendaGravada Vender(SqliteConnection cx, Sessao sessao, Operador op, decimal reais)
    {
        var total = Dinheiro.DeReais(reais);
        return Vendas.Finalizar(cx, sessao, op,
            new[] { new LinhaVenda(null, "SKU", "COMBO", Quantidade.Um, total, total,
                                   "UN", "19053100", null, "102", null, 0) },
            new[] { new PagamentoVenda("dinheiro", total, Dinheiro.Zero) },
            null, "Loja", null);
    }

    /// <summary>
    /// A linha que o build antigo deixava no banco da loja: dead-letter marcado em
    /// <c>enviado_em</c> (mentindo que a nuvem recebeu) e o motivo só no texto do
    /// <c>ultimo_erro</c>. É esse estado que o caixa da Savassi carrega hoje.
    /// </summary>
    private static void EnfileirarLegado(SqliteConnection cx, VendaGravada v, string erro)
        => cx.Execute("""
            INSERT INTO outbox (tipo, ref_id, client_key, payload, tentativas, ultimo_erro,
                                criado_em, enviado_em)
            VALUES ('venda', @Ref, @Key, '{"p_client_key":"' || @Key || '"}', 12, @Erro, @Em, @Em)
            """, new { Ref = v.Id, Key = v.ClientKey, Erro = erro, Em = DateTime.Now.ToString("o") });
}
