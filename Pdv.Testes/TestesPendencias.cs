using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

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
