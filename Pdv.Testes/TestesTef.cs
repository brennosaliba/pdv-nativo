using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// Testes do caminho do cartão contra a maquininha de mentira (<see cref="FakeTef"/>).
///
/// Este caminho nunca tinha sido exercitado: o TEF real exige hardware e credencial da
/// Rede Smart. E é justamente por ele que o dinheiro entra sem passar pela gaveta — uma
/// cobrança que o sistema lê errado não aparece como falta no fechamento, aparece como
/// venda que existe ou não existe.
/// </summary>
public static class TestesTef
{
    public static int Rodar(Action<bool, string> checar)
    {
        var antes = 0;

        // ── crédito aprovado ────────────────────────────────────────────────
        {
            using var tef = new FakeTef();
            tef.Roteiro.Enqueue(FakeTef.Desfecho.Aprovar);
            var cli = Cliente(tef);

            var d = cli.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(150.00m), null, 3, null,
                                    CancellationToken.None).GetAwaiter().GetResult();

            checar(d.Situacao == SituacaoTef.Pago, "crédito aprovado volta como Pago");
            checar(d.Codigo == CodigoTef.Pago, "crédito aprovado usa o código estável 'pago'");
            checar(d.Cartao?.CAut == "123456", "cAut do cartão chega para virar <card> na NFC-e");
            checar(d.Cartao?.ServeParaXml == true, "cartão com cAut e CNPJ de 14 dígitos serve para o XML");
            checar(d.PaymentIdentifier is not null, "cobrança aprovada carrega o identificador para conciliar");

            var criar = tef.Chamadas.FirstOrDefault(c => c.Contains("\"criar\""));
            checar(criar is not null && criar.Contains("\"parcelas\":3"),
                   "crédito manda o número de parcelas que o operador escolheu");
        }

        // ── débito aprovado (parcelas sempre 1) ─────────────────────────────
        {
            using var tef = new FakeTef();
            tef.Roteiro.Enqueue(FakeTef.Desfecho.Aprovar);
            var cli = Cliente(tef);

            var d = cli.CobrarAsync(TipoTef.Debito, Dinheiro.DeReais(89.90m), null, 5, null,
                                    CancellationToken.None).GetAwaiter().GetResult();

            checar(d.Situacao == SituacaoTef.Pago, "débito aprovado volta como Pago");

            var criar = tef.Chamadas.FirstOrDefault(c => c.Contains("\"criar\""));
            checar(criar is not null && criar.Contains("\"parcelas\":1"),
                   "débito força 1 parcela mesmo se a tela mandar 5 (a Smart TEF recusa o contrário)");
            checar(criar is not null && criar.Contains("\"tipo\":\"debito\""),
                   "débito vai com a grafia do contrato de fechamento de caixa");
        }

        // ── cartão recusado ─────────────────────────────────────────────────
        {
            using var tef = new FakeTef();
            tef.Roteiro.Enqueue(FakeTef.Desfecho.Recusar);
            var cli = Cliente(tef);

            var d = cli.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(50m), null, 1, null,
                                    CancellationToken.None).GetAwaiter().GetResult();

            checar(d.Situacao == SituacaoTef.Recusado, "cartão negado volta como Recusado, não como erro");
            checar(!d.Pago, "recusado nunca conta como pago");
            checar(d.Cartao is null || d.Cartao.Vazio, "recusado não traz dados de cartão para o XML");
        }

        // ── valor zero é barrado antes de armar a maquininha ────────────────
        {
            using var tef = new FakeTef();
            var cli = Cliente(tef);

            var d = cli.CobrarAsync(TipoTef.Credito, Dinheiro.Zero, null, 1, null,
                                    CancellationToken.None).GetAwaiter().GetResult();

            checar(d.Situacao == SituacaoTef.Erro, "cobrança de R$ 0,00 é recusada localmente");
            checar(tef.Chamadas.IsEmpty, "cobrança inválida não chega a tocar a maquininha");
        }

        // ── gateway recusa a própria criação ────────────────────────────────
        {
            using var tef = new FakeTef();
            tef.Roteiro.Enqueue(FakeTef.Desfecho.RecusarCriacao);
            var cli = Cliente(tef);

            var d = cli.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(30m), null, 1, null,
                                    CancellationToken.None).GetAwaiter().GetResult();

            checar(d.Situacao == SituacaoTef.Erro, "criação recusada pelo gateway vira Erro");
            checar(!d.Pago, "criação recusada não pode virar venda paga");
        }

        // ── sessão expirada é distinguível (relogar resolve) ────────────────
        {
            using var tef = new FakeTef { SessaoExpirada = true };
            tef.Roteiro.Enqueue(FakeTef.Desfecho.Aprovar);
            var cli = Cliente(tef);

            var d = cli.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(30m), null, 1, null,
                                    CancellationToken.None).GetAwaiter().GetResult();

            checar(!d.Pago, "401 no TEF nunca vira pagamento");
            checar(d.Situacao == SituacaoTef.Erro, "sessão expirada é erro, não recusa de cartão");
        }

        // ── o caso traiçoeiro: operador cancela e o cliente já tinha pago ───
        {
            using var tef = new FakeTef();
            tef.Roteiro.Enqueue(FakeTef.Desfecho.PagarNoUltimoSegundo);
            var cli = Cliente(tef);

            // cria a cobrança e a deixa pendurada
            var criar = cli.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(200m), null, 1, null,
                                        new CancellationTokenSource(1500).Token)
                          .GetAwaiter().GetResult();

            var pid = criar.PaymentIdentifier ?? tef.Cobrancas.Keys.FirstOrDefault();
            checar(pid is not null, "mesmo cancelando, o identificador da cobrança é conhecido");

            var limpeza = cli.LimparNoPosAsync(pid, null, CancellationToken.None)
                             .GetAwaiter().GetResult();

            checar(limpeza.Estado == LimpezaPos.PagoNoUltimoSegundo,
                   "cliente que pagou durante o cancelamento é detectado — a venda conclui, não recobra");
            checar(limpeza.Cartao?.CAut == "123456",
                   "o pagamento de último segundo traz a prova (cAut) para a nota");
        }

        // ── cancelamento normal libera a maquininha ─────────────────────────
        {
            using var tef = new FakeTef();
            tef.Roteiro.Enqueue(FakeTef.Desfecho.Pendurar);
            var cli = Cliente(tef);

            cli.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(70m), null, 1, null,
                            new CancellationTokenSource(1200).Token).GetAwaiter().GetResult();

            var pid = tef.Cobrancas.Keys.First();
            var limpeza = cli.LimparNoPosAsync(pid, null, CancellationToken.None)
                             .GetAwaiter().GetResult();

            checar(limpeza.Estado == LimpezaPos.Liberado,
                   "cobrança cancelada de verdade libera a maquininha para a próxima venda");
            checar(tef.Cobrancas[pid].Cancelada, "o cancelamento chegou ao gateway");
        }

        // ── estorno de cobrança já paga ─────────────────────────────────────
        {
            using var tef = new FakeTef();
            tef.Roteiro.Enqueue(FakeTef.Desfecho.Aprovar);
            var cli = Cliente(tef);

            var d = cli.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(120m), null, 1, null,
                                    CancellationToken.None).GetAwaiter().GetResult();
            checar(d.Pago, "cobrança paga antes do estorno");

            var ok = cli.EstornarAsync(d.PaymentIdentifier!, CancellationToken.None)
                        .GetAwaiter().GetResult();

            checar(ok, "estorno de cobrança paga é aceito");
            checar(tef.Cobrancas[d.PaymentIdentifier!].Estornada, "o estorno chegou ao gateway");

            var vazio = cli.EstornarAsync("", CancellationToken.None).GetAwaiter().GetResult();
            checar(!vazio, "estorno sem identificador é recusado localmente (não sai chamada)");
        }

        // ── uma cobrança = uma criação (não recobra o cliente) ──────────────
        {
            using var tef = new FakeTef();
            tef.Roteiro.Enqueue(FakeTef.Desfecho.Aprovar);
            var cli = Cliente(tef);

            cli.CobrarAsync(TipoTef.Debito, Dinheiro.DeReais(45m), null, 1, null,
                            CancellationToken.None).GetAwaiter().GetResult();

            checar(tef.Contagem.GetValueOrDefault("criar") == 1,
                   "uma venda gera UMA cobrança — recobrar o cliente é o pior defeito possível aqui");
            checar(tef.Cobrancas.Count == 1, "o gateway registrou exatamente uma cobrança");
        }

        // ── CPF só vai para o TEF se tiver tamanho válido ───────────────────
        {
            using var tef = new FakeTef();
            tef.Roteiro.Enqueue(FakeTef.Desfecho.Aprovar);
            var cli = Cliente(tef);

            cli.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(60m), "123.456", 1, null,
                            CancellationToken.None).GetAwaiter().GetResult();

            var criar = tef.Chamadas.First(c => c.Contains("\"criar\""));
            checar(!criar.Contains("123456") && !criar.Contains("123.456"),
                   "CPF pela metade não vaza para o TEF nem para a nota");
        }

        return antes;
    }

    private static ClienteTef Cliente(FakeTef tef) =>
        new(_ => Task.FromResult<string?>("token-de-teste"), tef.Url, "anon-de-teste");
}
