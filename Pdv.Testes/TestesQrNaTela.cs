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
    }
}
