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
