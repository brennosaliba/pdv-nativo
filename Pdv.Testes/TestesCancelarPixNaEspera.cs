using System.Diagnostics;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// Esc durante a espera do Pix (passo 55 do roteiro v20260819), medido em 09/09/2026.
///
/// O que o log da PGWebLib mostrou nas tres tentativas do dono (REQNUM 283068, 283108 e 283151):
/// o caixa chamava PW_iPPAbort na hora, mas a biblioteca NAO encerrava a espera do host. Ou
/// seguia pedindo exibicao (PWDAT_DSPCHECKOUT, 0x7F17) a cada 0,7 s por 20 a 40 s, e cada pedido
/// reiniciava o relogio de execucao do caixa, ate pedir RETIRE O CARTAO; ai o caixa abortava DE
/// NOVO e PW_iPPEventLoop devolvia PWRET_TRNNOTINIT (-2488), que virava "erro". Ou devolvia
/// PWRET_MOREDATA com nove pedidos zerados (tipo 0), que virava "captura que o caixa nao
/// suporta". Nos dois casos a venda saia desfeita, mas 40 segundos depois e como ERRO, e o
/// roteiro cobra venda NEGADA com "OPERACAO CANCELADA".
///
/// O conserto: o pedido de exibicao seguinte ao Esc ja encerra cancelado; o cancelamento tem
/// prazo proprio que nenhum pedido reinicia; RETIRE O CARTAO nao se aborta; e erro do pinpad
/// depois do abort e cancelamento, nao defeito.
/// </summary>
public static class TestesCancelarPixNaEspera
{
    private sealed class Andamentos : IProgress<AndamentoTef>
    {
        private readonly Action<AndamentoTef> _ao;
        public Andamentos(Action<AndamentoTef> ao) { _ao = ao; }
        public void Report(AndamentoTef v) => _ao(v);
    }

    private static ProvedorPGWebLib Provedor(FakePGWebLib f, Action<string>? atualizar = null, List<string>? aud = null, int prazoCancelMs = 5_000)
        => new(f, TestesPGWebLib.PastaTeste, new OpcoesPGWebLib("Pdv.AmericanDay", "0.8.10", "American Day", RedePix: "PIX C6 BANK", RedeCartao: "C6 PAY"))
        {
            IntervaloPollMs = 5,
            TempoMaxExecMs = 3000,
            TempoMaxCapturaMs = 3000,
            TempoPerguntaMs = 500,
            TempoMaxCancelamentoMs = prazoCancelMs,
            Guardar = _ => true,
            Exibir = (_, _) => Task.FromResult(true),
            AtualizarExibicao = atualizar,
            Auditar = aud is null ? null : aud.Add,
        };

    public static void Rodar(Action<bool, string> checar)
    {
        Directory.CreateDirectory(TestesPGWebLib.PastaTeste);

        // ── A. a biblioteca segue consultando o host depois do abort (20:57, REQNUM 283151) ──
        {
            var f = new FakePGWebLib { ExibirEnquantoEspera = true, AbortNaEspera = FakePGWebLib.ReacaoAoAbort.ContinuaEsperando, ComSenha = false };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.NuncaTermina);
            var cts = new CancellationTokenSource();
            var exibicoes = 0;
            var p = Provedor(f, atualizar: _ => { if (++exibicoes == 2) cts.Cancel(); });
            var relogio = Stopwatch.StartNew();
            var d = p.CobrarAsync(TipoTef.Pix, Dinheiro.DeReais(50m), null, 1, null, cts.Token).GetAwaiter().GetResult();
            relogio.Stop();
            checar(d.Situacao == SituacaoTef.Cancelado && d.Codigo == CodigoTef.Cancelado,
                $"Esc na espera do Pix: cancelada, nao erro ({d.Situacao} / {d.Motivo})");
            checar(d.Motivo == "cobrança cancelada pelo operador", "com a frase de cancelamento, e nao 'pinpad devolveu' nem 'nao suporta': " + d.Motivo);
            checar(relogio.ElapsedMilliseconds < 1500, "e em menos de 1,5 s, nao 40: " + relogio.ElapsedMilliseconds + " ms");
            checar(f.Abortos == 1, "um PW_iPPAbort so (" + f.Abortos + ")");
            checar(d.Desfeita, "e desfeita: o caixa nao fica devendo nada a rede");
            checar(exibicoes >= 2, "a tela chegou a receber os pedidos de exibicao antes do Esc (" + exibicoes + ")");
        }

        // ── B. nove pedidos zerados depois do abort (20:56, REQNUM 283068) ──────────
        {
            var f = new FakePGWebLib { AbortNaEspera = FakePGWebLib.ReacaoAoAbort.TipoZero, ComSenha = false };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.NuncaTermina);
            var cts = new CancellationTokenSource();
            var p = Provedor(f);
            _ = Task.Delay(100).ContinueWith(_ => cts.Cancel());
            var d = p.CobrarAsync(TipoTef.Pix, Dinheiro.DeReais(50m), null, 1, null, cts.Token).GetAwaiter().GetResult();
            checar(d.Situacao == SituacaoTef.Cancelado && d.Motivo == "cobrança cancelada pelo operador",
                "pedido zerado depois do abort e cancelamento, nao 'captura que o caixa nao suporta (tipo 0)': " + d.Motivo);
            checar(d.Desfeita, "e desfeita");
        }

        // ── C. o prazo do cancelamento e proprio: nem exibicao nem NOTHING o reiniciam ──
        {
            var f = new FakePGWebLib { AbortNaEspera = FakePGWebLib.ReacaoAoAbort.ContinuaEsperando, ComSenha = false };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.NuncaTermina);
            var cts = new CancellationTokenSource();
            var p = Provedor(f, prazoCancelMs: 300);
            _ = Task.Delay(100).ContinueWith(_ => cts.Cancel());
            var relogio = Stopwatch.StartNew();
            var d = p.CobrarAsync(TipoTef.Pix, Dinheiro.DeReais(50m), null, 1, null, cts.Token).GetAwaiter().GetResult();
            relogio.Stop();
            checar(d.Situacao == SituacaoTef.Cancelado && d.Desfeita, "biblioteca muda depois do abort: o prazo encerra cancelada e desfeita (" + d.Motivo + ")");
            checar(relogio.ElapsedMilliseconds is >= 300 and < 1500, "no prazo de TempoMaxCancelamentoMs (300 ms), nao nos 3 s do laco: " + relogio.ElapsedMilliseconds + " ms");
        }

        // ── D. PWRET_TRNNOTINIT depois do abort e cancelamento, nao erro ────────────
        {
            var f = new FakePGWebLib { EventLoopAposAbort = PW.PWRET_TRNNOTINIT, ComSenha = false };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var cts = new CancellationTokenSource();
            var prog = new Andamentos(a => { if (a.Mensagem == "APROXIME O CARTAO") cts.Cancel(); });
            var d = Provedor(f).CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(10m), null, 1, prog, cts.Token).GetAwaiter().GetResult();
            checar(d.Situacao == SituacaoTef.Cancelado && d.Motivo == "cobrança cancelada pelo operador",
                "PWRET_TRNNOTINIT depois do abort e cancelamento, nao 'pinpad devolveu PWRET_TRNNOTINIT': " + d.Motivo);
            checar(f.Abortos == 1, "um abort (" + f.Abortos + ")");

            // Sem abort, o mesmo retorno continua sendo erro do pinpad: nao se esconde defeito.
            var f2 = new FakePGWebLib { ComSenha = false };
            f2.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var d2 = Provedor(f2).CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(10m), null, 1, null, CancellationToken.None).GetAwaiter().GetResult();
            checar(d2.Pago, "sem Esc a venda segue aprovada (" + d2.Motivo + ")");
        }

        // ── E. RETIRE O CARTAO nao se aborta: e a biblioteca terminando ─────────────
        {
            var f = new FakePGWebLib { ComSenha = false, PedirRemocao = true };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var cts = new CancellationTokenSource();
            var prog = new Andamentos(a => { if (a.Fase == FaseTef.Encerrando) cts.Cancel(); });
            var d = Provedor(f).CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(10m), null, 1, prog, cts.Token).GetAwaiter().GetResult();
            checar(f.Abortos == 0, "cancelar em RETIRE O CARTAO nao chama PW_iPPAbort (abortos=" + f.Abortos + ")");
            checar(f.Confirmadas.Count == 1 && f.Confirmadas[0].Resultado == PW.PWCNF_REV_ABORT,
                "a rede tinha aprovado e o operador desistiu: REV_ABORT, como sempre (" + (f.Confirmadas.Count > 0 ? f.Confirmadas[0].Resultado : 0) + ")");
            checar(d.Situacao == SituacaoTef.Cancelado && d.Desfeita, "desfecho cancelado e desfeito (" + d.Situacao + ")");
        }
    }
}
