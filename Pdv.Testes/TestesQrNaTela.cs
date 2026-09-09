using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O QR do Pix na tela do caixa (PWDAT_DSPQRCODE).
///
/// O roteiro de homologação v20260819 conta com isto. O passo 55, "Operação cancelada durante venda
/// PIX", manda "realizar uma venda e na tela de exibição do QRCode pressionar a tecla Esc em uma
/// solução Windows", e espera a mensagem "OPERAÇÃO CANCELADA". Ou seja: a tela do QR é do caixa, e o
/// Esc dela tem que virar cancelamento, não erro.
///
/// Antes deste conserto o provedor caía no ramo final de AtenderAsync e matava a venda com "TEF
/// pediu captura que o caixa não suporta (tipo 20)".
///
/// Duas coisas aqui são leitura do contrato oficial (PGWebLib.h) e ainda não foram confrontadas com
/// o host, porque a instalação do terminal depende do dono:
///   1. o conteúdo do QR vem de PW_iGetResult(PWINFO_AUTHPOSQRCODE), e não do szPrompt, que tem 84
///      caracteres e não comporta um payload de Pix;
///   2. a resposta de um pedido de exibição é PW_iAddParam do mesmo identificador com valor vazio.
/// Os dois aparecem na auditoria em toda venda de Pix, então o passo 11 confirma ou desmente.
/// </summary>
public static class TestesQrNaTela
{
    private sealed class Andamentos : IProgress<AndamentoTef>
    {
        private readonly Action<AndamentoTef> _ao;
        public Andamentos(Action<AndamentoTef> ao) { _ao = ao; }
        public void Report(AndamentoTef v) => _ao(v);
    }

    public static void Rodar(Action<bool, string> checar)
    {
        var pasta = TestesPGWebLib.PastaTeste;
        Directory.CreateDirectory(pasta);

        static OpcoesPGWebLib Op() => new("Pdv.AmericanDay", "0.5.9", "American Day", RedePix: "PIX C6 BANK", RedeCartao: "REDE");

        ProvedorPGWebLib Provedor(FakePGWebLib f, Func<ExibicaoTef, CancellationToken, Task<bool>>? exibir,
            List<string>? auditoria = null, Action? fechar = null)
            => new(f, pasta, Op())
            {
                IntervaloPollMs = 5,
                TempoMaxExecMs = 2000,
                TempoMaxCapturaMs = 2000,
                TempoPerguntaMs = 500,
                Guardar = _ => true,
                Exibir = exibir,
                FecharExibicao = fechar,
                Auditar = auditoria is null ? null : auditoria.Add,
            };

        static DesfechoTef Cobrar(ProvedorPGWebLib p)
            => p.CobrarAsync(TipoTef.Pix, Dinheiro.DeReais(500m), null, 1, null, CancellationToken.None)
                .GetAwaiter().GetResult();

        // ── 1. o caminho feliz: o caixa mostra o QR e a venda sai ────────────
        {
            var mostrados = new List<ExibicaoTef>();
            var f = new FakePGWebLib { PedirQrNaTela = true, ComSenha = false, PedirRemocao = false };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var p = Provedor(f, (e, _) => { mostrados.Add(e); return Task.FromResult(true); });
            var d = Cobrar(p);

            checar(d.Pago, "venda de Pix com QR na tela sai aprovada");
            checar(mostrados.Count == 1, "a tela foi chamada uma vez, não a cada volta do laço: " + mostrados.Count);
            checar(mostrados.Count == 1 && mostrados[0].EhQrCode && mostrados[0].QrCode == f.QrCode,
                "o conteúdo do QR é o que a biblioteca devolveu em PWINFO_AUTHPOSQRCODE");
            checar(mostrados.Count == 1 && mostrados[0].QrCode!.Length > 84,
                "o payload passa de 84 caracteres, ou seja, NÃO caberia no prompt: " + (mostrados.Count == 1 ? mostrados[0].QrCode!.Length : 0));
            checar(mostrados.Count == 1 && mostrados[0].Titulo.Contains("Pix") && !mostrados[0].Titulo.Contains('—'),
                "o título é curto, humano e sem travessão: " + (mostrados.Count == 1 ? mostrados[0].Titulo : ""));
            checar(f.Ultima is not null && f.Ultima.Params.ContainsKey(PW.PWINFO_AUTHPOSQRCODE)
                   && f.Ultima.Params[PW.PWINFO_AUTHPOSQRCODE] == "",
                "a automação avisa que mostrou com PW_iAddParam do mesmo identificador, valor vazio");
        }

        // ── 2. o Esc do passo 55: cancela, não dá erro ───────────────────────
        {
            var f = new FakePGWebLib { PedirQrNaTela = true, ComSenha = false, PedirRemocao = false };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var p = Provedor(f, (_, _) => Task.FromResult(false));
            var d = Cobrar(p);

            checar(!d.Pago && d.Situacao == SituacaoTef.Cancelado && d.Codigo == CodigoTef.Cancelado,
                "Esc na tela do QR: venda cancelada, não erro (passo 55)");
            checar(d.Motivo is not null && d.Motivo.Contains("cancelada"),
                "e o motivo diz cancelada: " + d.Motivo);
            checar(f.Confirmadas.Count == 0 || f.Confirmadas.All(c => c.Resultado != 0),
                "nada foi confirmado como venda boa depois do cancelamento");
        }

        // ── 3. o caixa que não sabe mostrar não morre com jargão ─────────────
        {
            var f = new FakePGWebLib { PedirQrNaTela = true, ComSenha = false, PedirRemocao = false };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var p = Provedor(f, null);
            var d = Cobrar(p);

            checar(!d.Pago, "sem tela de QR a venda de Pix não sai");
            checar(d.Motivo is not null && d.Motivo.Contains("QR") && !d.Motivo.Contains("tipo 20")
                   && !d.Motivo.Contains('—'),
                "e o motivo é uma frase que o operador entende, sem número de tipo: " + d.Motivo);
        }

        // ── 4. QR pedido sem conteúdo: não mostra quadrado vazio ─────────────
        {
            var mostrados = new List<ExibicaoTef>();
            var f = new FakePGWebLib { PedirQrNaTela = true, QrSemConteudo = true, ComSenha = false, PedirRemocao = false };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var p = Provedor(f, (e, _) => { mostrados.Add(e); return Task.FromResult(true); });
            var d = Cobrar(p);

            checar(!d.Pago, "biblioteca pediu QR e não mandou o código: a venda para");
            checar(mostrados.Count == 0, "e a tela NÃO chega a ser aberta com um QR vazio");
            checar(d.Motivo is not null && !d.Motivo.Contains('—'), "com motivo legível: " + d.Motivo);
        }

        // ── 5. a auditoria registra o que foi lido e o que foi respondido ────
        {
            var aud = new List<string>();
            var f = new FakePGWebLib { PedirQrNaTela = true, ComSenha = false, PedirRemocao = false };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var p = Provedor(f, (_, _) => Task.FromResult(true), aud);
            Cobrar(p);

            checar(aud.Any(a => a.Contains("exibir") && a.Contains("caracteres")),
                "a auditoria diz o tipo, o identificador e o tamanho do QR, para o passo 11 confirmar o contrato");
        }

        // ── 6. a tela FECHA com qualquer desfecho ────────────────────────────
        foreach (var (desfecho, rotulo) in new[]
                 {
                     (FakePGWebLib.Desfecho.Aprovar, "aprovada"),
                     (FakePGWebLib.Desfecho.Recusar, "recusada"),
                     (FakePGWebLib.Desfecho.HostFora, "host fora"),
                 })
        {
            var fechou = 0;
            var f = new FakePGWebLib { PedirQrNaTela = true, ComSenha = false, PedirRemocao = false };
            f.Roteiro.Enqueue(desfecho);
            var p = Provedor(f, (_, _) => Task.FromResult(true), fechar: () => fechou++);
            Cobrar(p);
            checar(fechou == 1, $"venda {rotulo}: a tela do QR fecha uma vez (fechou {fechou})");
        }

        // ── 7. Esc de verdade: cancela pelo token, e a tela fecha ────────────
        {
            var fechou = 0;
            var f = new FakePGWebLib { PedirQrNaTela = true, ComSenha = false, PedirRemocao = false };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.NuncaTermina);
            var cts = new CancellationTokenSource();
            var p = new ProvedorPGWebLib(f, pasta, Op())
            {
                IntervaloPollMs = 5,
                TempoMaxExecMs = 3000,
                TempoMaxCapturaMs = 3000,
                TempoPerguntaMs = 500,
                Guardar = _ => true,
                // a tela abre e, como o operador aperta Esc, ela cancela a venda
                Exibir = (_, _) => { cts.Cancel(); return Task.FromResult(true); },
                FecharExibicao = () => fechou++,
            };
            var d = p.CobrarAsync(TipoTef.Pix, Dinheiro.DeReais(500m), null, 1, null, cts.Token)
                     .GetAwaiter().GetResult();
            checar(!d.Pago && d.Situacao == SituacaoTef.Cancelado,
                "Esc cancelando pelo token da venda: desfecho cancelado, e nao erro");
            checar(f.Abortos >= 1, "e o provedor chamou PW_iPPAbort para a biblioteca encerrar");
            checar(fechou == 1, "a tela do QR fechou (fechou " + fechou + ")");
        }

        // ── 8. a capacidade só entra quando a loja pede ──────────────────────
        {
            var padrao = ProvedorPGWebLib.CapacidadesPadrao;
            checar((padrao & PW.CAP_QR) == 0 && (padrao & PW.CAP_MSG_CHECKOUT) == 0,
                "sem configuração o caixa NÃO promete desenhar QR: o Pix continua no pinpad, como nas lojas hoje");

            var com = ConfigPGWebLib.CapacidadesCom(padrao, true);
            checar((com & PW.CAP_QR) != 0 && (com & PW.CAP_MSG_CHECKOUT) != 0 && (com & padrao) == padrao,
                "ligado, entram CAP_QR e CAP_MSG_CHECKOUT juntas, sem perder as antigas");

            checar(ConfigPGWebLib.QrNaTela(c => c == ConfigPGWebLib.ChaveQrNaTela ? "1" : null)
                && !ConfigPGWebLib.QrNaTela(_ => null)
                && !ConfigPGWebLib.QrNaTela(c => c == ConfigPGWebLib.ChaveQrNaTela ? "0" : null),
                "tef_pgweb_qr_na_tela: só \"1\" liga");

            var op = ConfigPGWebLib.Opcoes(c => c == ConfigPGWebLib.ChaveQrNaTela ? "1" : null, "0.5.9");
            checar((op.Capacidades & PW.CAP_QR) != 0,
                "e a opção que o provedor recebe já vem com a capacidade");
        }

        // ── 9. venda de cartão continua sem passar por tela nenhuma ──────────
        {
            var mostrados = new List<ExibicaoTef>();
            var f = new FakePGWebLib { ComSenha = false, PedirRemocao = false };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var p = Provedor(f, (e, _) => { mostrados.Add(e); return Task.FromResult(true); });
            var d = p.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(10m), null, 1, null, CancellationToken.None)
                     .GetAwaiter().GetResult();

            checar(d.Pago && mostrados.Count == 0, "cartão não abre tela de QR: o caminho antigo não mudou");
        }

        // ── 10. a tela do QR fecha quando a biblioteca passa para o pinpad, nao so no fim ──
        // MEDIDO em 09/09/2026 as 18:59 (comms_260909.log, REQNUM 280555): o host aprovou o
        // Pix, a biblioteca pediu RETIRE O CARTAO, e a janela do QR continuou aberta com o
        // botao "Cancelar cobranca". O dono clicou nele, e um Pix PAGO virou desfeito.
        {
            var fechou = 0;
            var fechadaQuandoOPinpadFalou = -1;
            var f = new FakePGWebLib { PedirQrNaTela = true, ComSenha = false, PedirRemocao = false };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var prog = new Andamentos(a =>
            {
                if (a.Fase == FaseTef.Recado && fechadaQuandoOPinpadFalou < 0) fechadaQuandoOPinpadFalou = fechou;
            });
            var p = new ProvedorPGWebLib(f, pasta, Op())
            {
                IntervaloPollMs = 5,
                TempoMaxExecMs = 2000,
                TempoMaxCapturaMs = 2000,
                TempoPerguntaMs = 500,
                Guardar = _ => true,
                Exibir = (_, _) => Task.FromResult(true),
                FecharExibicao = () => fechou++,
            };
            var d = p.CobrarAsync(TipoTef.Pix, Dinheiro.DeReais(500m), null, 1, prog, CancellationToken.None)
                     .GetAwaiter().GetResult();
            checar(d.Pago, "a venda de Pix sai aprovada");
            checar(fechadaQuandoOPinpadFalou == 1,
                "a tela do QR ja estava fechada quando o pinpad mandou o primeiro recado (" + fechadaQuandoOPinpadFalou + ")");
            checar(fechou == 1, "e fechou uma vez so, sem fechar de novo no fim (" + fechou + ")");
        }

        // ── 11. onde o QR sai: PWINFO_DSPQRPREF so quando a loja pede ─────────
        // MEDIDO em 09/09/2026: com CAP_QR declarada e sem a preferencia, a biblioteca gerou o
        // QR no pinpad nas 10 vendas e nunca pediu a tela tipo 20. O valor vem do ACBr
        // (IfThen(qreExibirNoCheckOut, '2', '1')); o cabecalho oficial nao documenta.
        {
            ProvedorPGWebLib Com(FakePGWebLib fake, string? pref) => new(fake, pasta, Op() with { PreferenciaQr = pref })
            {
                IntervaloPollMs = 5,
                TempoMaxExecMs = 2000,
                TempoMaxCapturaMs = 2000,
                TempoPerguntaMs = 500,
                Guardar = _ => true,
                Exibir = (_, _) => Task.FromResult(true),
            };

            var f = new FakePGWebLib { PedirQrNaTela = true, ComSenha = false, PedirRemocao = false };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            Cobrar(Com(f, null));
            checar(f.Ultima is not null && !f.Ultima.Params.ContainsKey(PW.PWINFO_DSPQRPREF),
                "sem preferencia gravada o caixa nao manda DSPQRPREF: a biblioteca decide, como sempre fez");

            var f2 = new FakePGWebLib { PedirQrNaTela = true, ComSenha = false, PedirRemocao = false };
            f2.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            Cobrar(Com(f2, PW.DSPQRPREF_TELA));
            checar(f2.Ultima is not null && f2.Ultima.Params.TryGetValue(PW.PWINFO_DSPQRPREF, out var pref) && pref == "2",
                "com tef_pgweb_qr_onde=tela vai PWINFO_DSPQRPREF=2, o valor do checkout");

            var f3 = new FakePGWebLib { ComSenha = false, PedirRemocao = false };
            f3.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            Com(f3, PW.DSPQRPREF_TELA).CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(10m), null, 1, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            checar(f3.Ultima is not null && !f3.Ultima.Params.ContainsKey(PW.PWINFO_DSPQRPREF),
                "cartao nunca manda preferencia de QR");

            static string? Cfg(string chave, string? onde, string? naTela)
                => chave == ConfigPGWebLib.ChaveQrOnde ? onde : chave == ConfigPGWebLib.ChaveQrNaTela ? naTela : null;
            checar(ConfigPGWebLib.PreferenciaQr(c => Cfg(c, "tela", "1")) == PW.DSPQRPREF_TELA, "\"tela\" com o QR na tela ligado = 2");
            checar(ConfigPGWebLib.PreferenciaQr(c => Cfg(c, "tela", null)) is null,
                "\"tela\" SEM o QR na tela ligado nao manda nada: sem CAP_QR a biblioteca nao teria a quem entregar o QR");
            checar(ConfigPGWebLib.PreferenciaQr(c => Cfg(c, "pinpad", null)) == PW.DSPQRPREF_PINPAD, "\"pinpad\" = 1");
            checar(ConfigPGWebLib.PreferenciaQr(c => Cfg(c, null, "1")) is null,
                "em branco nao manda nada, mesmo com o QR na tela ligado: e o caminho de hoje, o que passou na homologacao");
            checar(ConfigPGWebLib.Opcoes(c => Cfg(c, "tela", "1"), "0.8.7").PreferenciaQr == PW.DSPQRPREF_TELA,
                "e a preferencia chega ao provedor pela config");
        }

        // ── 12. a rede respondeu: o provedor avisa a tela ANTES do RETIRE O CARTAO ─
        // A revisao de 09/09/2026 mostrou que fechar a janela do QR nao bastava: a tela de
        // pagamento tem o proprio "Cancelar cobranca", e ele continuava armado durante o
        // RETIRE O CARTAO de um Pix ja pago. O provedor passa a anunciar FaseTef.Encerrando
        // no instante em que a biblioteca pede a retirada; a tela decide o que fazer.
        {
            var fases = new List<(string Fase, string Msg)>();
            var f = new FakePGWebLib { PedirQrNaTela = true, ComSenha = false, PedirRemocao = true };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var p = new ProvedorPGWebLib(f, pasta, Op())
            {
                IntervaloPollMs = 5,
                TempoMaxExecMs = 2000,
                TempoMaxCapturaMs = 2000,
                TempoPerguntaMs = 500,
                Guardar = _ => true,
                Exibir = (_, _) => Task.FromResult(true),
            };
            var d = p.CobrarAsync(TipoTef.Pix, Dinheiro.DeReais(500m), null, 1, new Andamentos(a => fases.Add((a.Fase, a.Mensagem))), CancellationToken.None)
                     .GetAwaiter().GetResult();
            checar(d.Pago, "a venda de Pix com retirada sai aprovada");
            var encerrando = fases.FindIndex(x => x.Fase == FaseTef.Encerrando);
            var retire = fases.FindIndex(x => x.Msg == "RETIRE O CARTAO");
            checar(encerrando >= 0, "o provedor anuncia FaseTef.Encerrando");
            checar(encerrando >= 0 && retire > encerrando,
                "e anuncia ANTES do recado RETIRE O CARTAO, para a tela tirar o botao a tempo");
            checar(fases.Count(x => x.Fase == FaseTef.Encerrando) == 1, "uma vez so por cobranca");

            var semRetirada = new List<string>();
            var f2 = new FakePGWebLib { PedirQrNaTela = true, ComSenha = false, PedirRemocao = false };
            f2.Roteiro.Enqueue(FakePGWebLib.Desfecho.Aprovar);
            var p2 = new ProvedorPGWebLib(f2, pasta, Op())
            {
                IntervaloPollMs = 5, TempoMaxExecMs = 2000, TempoMaxCapturaMs = 2000, TempoPerguntaMs = 500,
                Guardar = _ => true, Exibir = (_, _) => Task.FromResult(true),
            };
            p2.CobrarAsync(TipoTef.Pix, Dinheiro.DeReais(500m), null, 1, new Andamentos(a => semRetirada.Add(a.Fase)), CancellationToken.None)
              .GetAwaiter().GetResult();
            checar(!semRetirada.Contains(FaseTef.Encerrando), "sem pedido de retirada nao ha o anuncio: ele e do RETIRE O CARTAO, nao da aprovacao");
        }

        // ── 13. a tela de pagamento: Pix respondido perde o Cancelar; cartao mantem ──
        {
            var raiz = AcharRaiz();
            checar(raiz is not null, "achei a raiz do repositorio para ler a tela de pagamento");
            if (raiz is not null)
            {
                var tela = File.ReadAllText(Path.Combine(raiz, "Telas", "Pagamento.xaml.cs"));
                var i = tela.IndexOf("var andamento = new Progress<AndamentoTef>(a =>", StringComparison.Ordinal);
                var j = i < 0 ? -1 : tela.IndexOf("});", i, StringComparison.Ordinal);
                var trecho = i < 0 || j < 0 ? "" : tela[i..j];
                checar(trecho.Contains("a.Fase == FaseTef.Encerrando && forma == \"pix\"", StringComparison.Ordinal),
                    "no Encerrando de um PIX a tela troca o estado (e so no Pix: no cartao o botao e o desfazimento manual dos passos 39 e 40)");
                checar(trecho.Contains("Estado(", StringComparison.Ordinal) && !trecho.Contains("Cancelar cobran", StringComparison.Ordinal),
                    "e o estado novo vem SEM o botao Cancelar cobranca");
                checar(tela.Contains("(\"Cancelar cobrança\", CancelarCobrancaNoTef)", StringComparison.Ordinal),
                    "o botao continua existindo no inicio da cobranca, para o cartao e para o Esc do passo 55");
                checar(!trecho.Contains('—'), "sem travessao no texto novo");
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
