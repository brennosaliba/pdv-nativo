using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// Estorno de venda cuja resposta guardada NAO tem data (09/09/2026).
///
/// O caixa 0.8.7 nao guardava o carimbo da biblioteca (952-000) nem a data da rede (022-000),
/// porque a PGWebLib devolve a data em PWINFO_DATETIME (0x31) e o caixa so lia AUTDATETIME.
/// Resultado: no estorno faltava TRNORIGDATE e a biblioteca parava para o operador digitar
/// "090926" na mao, com a venda ja selecionada na tela ("eu seleciono a transacao e mesmo
/// assim tenho que digitar valor, data, ref").
///
/// O 0.8.8 passou a guardar o carimbo nas vendas novas. Para as ANTIGAS, o relogio do caixa
/// na hora da venda (tef_transacao.criado_em) vira a data: e o mesmo dia, e a rede confere
/// a data, nao a hora. So a data entra; hora do caixa nao e hora da rede.
///
/// E o REQNUM do estorno passa a aparecer na tela e a ir para o placar: os passos 44, 46 e 57
/// do roteiro sao estornos, e o dono ficou sem numero para anotar ("nao apareceu numero na tela").
/// </summary>
public static class TestesEstornoDataDoCaixa
{
    public static void Rodar(Action<bool, string> checar)
    {
        Directory.CreateDirectory(TestesPGWebLib.PastaTeste);
        var quando = new DateTime(2026, 9, 9, 20, 17, 25);

        // ── 1. a resposta ganha a DATA do caixa, e so quando nao tem nenhuma ─────
        {
            var sem = RespostaPayGo.Analisar("000-000 = CRT\n009-000 = 0\n027-000 = 0000282973\n012-000 = 621892\n010-000 = C6 PAY\n");
            checar(sem.Data is null && !sem.Campos.ContainsKey("952-000"), "resposta guardada pelo 0.8.7: sem data (022) e sem carimbo (952)");
            var com = sem.ComDataSeFaltar(quando);
            checar(com.Data == "09092026" && com.Hora is null, "ganha a DATA do relogio do caixa (DDMMAAAA) e NAO ganha hora: " + com.Data + "/" + (com.Hora ?? "null"));
            checar(com.CodigoControle == "0000282973" && com.Nsu == "621892" && com.Rede == "C6 PAY" && com.Aprovada, "e o resto continua igual");
            checar(ReferenceEquals(sem.ComDataSeFaltar(null), sem), "sem relogio, nada muda");
            var com952 = RespostaPayGo.Analisar("000-000 = CRT\n952-000 = 20260909201743\n");
            checar(ReferenceEquals(com952.ComDataSeFaltar(quando), com952), "com o carimbo cru da biblioteca, nada muda");
            var com022 = RespostaPayGo.Analisar("000-000 = CRT\n022-000 = 05092026\n023-000 = 143000\n");
            checar(com022.ComDataSeFaltar(quando).Data == "05092026", "com a data da rede, a do caixa nao entra");
            checar(RespostaPayGo.Analisar("").ComDataSeFaltar(quando).Data == "09092026", "resposta vazia tambem ganha a data (venda velha sem resposta_txt)");
        }

        // ── 2. o estorno manda TRNORIGDATE a partir dela, e nada de hora ─────────
        {
            var f = new FakePGWebLib { ComSenha = false };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var guardadas = new List<TransacaoPayGo>();
            var p = new ProvedorPGWebLib(f, TestesPGWebLib.PastaTeste, new OpcoesPGWebLib("Pdv.AmericanDay", "0.8.9", "American Day", RedeCartao: "C6 PAY"))
            {
                IntervaloPollMs = 5, TempoMaxExecMs = 2000, TempoMaxCapturaMs = 2000, TempoPerguntaMs = 500,
                Guardar = t => { guardadas.Add(t); return true; },
                Perguntar = (_, _) => Task.FromResult<string?>("1234"),   // a senha do lojista que o fake pede no cancelamento
            };
            var d = p.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(2m), null, 1, null, CancellationToken.None).GetAwaiter().GetResult();
            checar(d.Pago, "venda de R$ 2,00 aprovada (" + d.Motivo + ")");
            var paga = guardadas.Last(g => g.Situacao == "pago");
            // Tira o que o 0.8.7 nao guardava.
            var linhas = paga.Resposta!.Texto.Split('\n')
                .Where(l => !l.StartsWith("022-000", StringComparison.Ordinal) && !l.StartsWith("023-000", StringComparison.Ordinal) && !l.StartsWith("952-000", StringComparison.Ordinal));
            var velha = RespostaPayGo.Analisar(string.Join("\n", linhas));
            checar(velha.Data is null && !velha.Campos.ContainsKey("952-000") && velha.CodigoControle == paga.CodigoControle, "a resposta 'velha' esta sem data e com o resto");

            var e = p.CancelarAsync(paga with { Resposta = velha.ComDataSeFaltar(quando) }, CancellationToken.None).GetAwaiter().GetResult();
            checar(e.Pago, "estorno aprovado (" + e.Motivo + ")");
            var pr = f.Ultima?.Params;
            checar(pr is not null && pr.GetValueOrDefault(PW.PWINFO_TRNORIGDATE) == "090926",
                "TRNORIGDATE saiu DDMMAA do relogio do caixa: " + (pr?.GetValueOrDefault(PW.PWINFO_TRNORIGDATE) ?? "null"));
            checar(pr is not null && !pr.ContainsKey(PW.PWINFO_TRNORIGTIME), "e TRNORIGTIME nao saiu: a hora do caixa nao e a da rede");
            checar(pr is not null && pr.GetValueOrDefault(PW.PWINFO_TOTAMNT) == "200" && pr.GetValueOrDefault(PW.PWINFO_CARDTYPE) == PW.CARDTYPE_CREDITO,
                "e o que o caixa ja sabe foi junto: valor em centavos e tipo do cartao");
            checar(e.Reqnum is { Length: > 0 }, "o estorno tem REQNUM para a tela");

            // Estorno NEGADO pelo host (passo 57, revisao de 09/09/2026): o REQNUM sobe do mesmo jeito.
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Recusar);
            var d2 = p.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(3m), null, 1, null, CancellationToken.None).GetAwaiter().GetResult();
            checar(d2.Pago, "outra venda aprovada (" + d2.Motivo + ")");
            var paga2 = guardadas.Last(g => g.Situacao == "pago");
            var neg = p.CancelarAsync(paga2, CancellationToken.None).GetAwaiter().GetResult();
            checar(!neg.Pago && neg.Situacao == SituacaoTef.Recusado, "o host negou o estorno (" + neg.Motivo + ")");
            checar(neg.Reqnum is { Length: > 0 } && neg.Reqnum == f.UltimoReqNum,
                "e mesmo negado o estorno traz o REQNUM (passo 57): " + (neg.Reqnum ?? "null") + " / " + f.UltimoReqNum);
        }

        // ── 3. a tela do estorno usa a data do caixa e mostra o REQNUM ──────────
        {
            var raiz = AcharRaiz();
            checar(raiz is not null, "achei a raiz do repositorio");
            if (raiz is not null)
            {
                var venda = File.ReadAllText(Path.Combine(raiz, "Telas", "Venda.xaml.cs"));
                checar(venda.Contains("t.criado_em AS tef_criado_em", StringComparison.Ordinal), "a lista de estorno traz o relogio da venda");
                checar(venda.Contains(".ComDataSeFaltar(quandoVendeu)", StringComparison.Ordinal), "e a venda original ganha a data dele quando falta");
                checar(venda.Contains("PlacarHomologacao.GuardarUltimo(d.Reqnum, (long)l.tef_valor,", StringComparison.Ordinal), "o REQNUM do estorno vai para o placar");
                var n = venda.Split("+ reqnumTxt").Length - 1;
                checar(n >= 4, "e aparece nos quatro avisos do estorno (feito, negado, cartao estornado, venda ainda aberta): " + n);
                checar(venda.Contains("_homologacao && !string.IsNullOrWhiteSpace(d.Reqnum)", StringComparison.Ordinal), "so na homologacao: em loja o numero nao interessa");
            }
        }
    }

    private static string? AcharRaiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "Telas")) && File.Exists(Path.Combine(dir.FullName, "Servicos.cs")))
                return dir.FullName;
        return null;
    }
}
