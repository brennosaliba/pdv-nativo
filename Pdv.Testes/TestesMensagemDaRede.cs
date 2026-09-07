using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A FRASE DA REDE chegando ao operador, que é o que o roteiro v20260819 cobra em quase metade
/// dos passos. O verbo do roteiro é sempre o mesmo: "Mensagem para o operador: TRANSAÇÃO
/// APROVADA", "Mensagem de erro para a Automação: OPERAÇÃO CANCELADA". Quem escreve essa frase é
/// a biblioteca, em PWINFO_RESULTMSG; o caixa não pode inventar outra nem engolir a dela.
///
/// A venda já fazia isso (ProvedorPGWebLib.CobrarAsync devolve `r.Mensagem`, e a tela de
/// pagamento mostra em TxtRecadoTef). Faltavam três caminhos, e cada um deles é um passo do
/// roteiro que não teria como ser gravado:
///
///   · CANCELAMENTO aprovado (passos 44 e 46, e o recibo dos 22 a 25): CancelarAsync devolvia
///     `Motivo: null` no sucesso, então a frase da rede morria no provedor e a tela mostrava só
///     "Estorno feito". Os dois passos pedem "TRANSAÇÃO APROVADA" no cancelamento, com todas as
///     letras;
///   · ESC durante a exibição do QR (passo 55): o laço de execução, quando o cancelamento partiu
///     do operador (`abortado`), descartava o PWINFO_RESULTMSG de propósito e escrevia a frase da
///     casa por cima. O passo cobra "OPERAÇÃO CANCELADA" — e o passo 05, que é o mesmo desfecho
///     por outro caminho, já respeitava a frase da biblioteca desde o conserto do menu de redes;
///   · INSTALAÇÃO (passos 01 e 18): a Configuração jogava fora o `Motivo` do desfecho e mostrava
///     só o texto da casa. O mesmo botão, no ramo administrativo logo abaixo, já anexava.
///
/// Em todos eles a frase da casa continua existindo como PADRÃO: biblioteca que não mandou
/// mensagem não deixa o operador sem recado. O que muda é a ordem — a rede primeiro.
/// </summary>
public static class TestesMensagemDaRede
{
    public static void Rodar(Action<bool, string> checar)
    {
        var pasta = TestesPGWebLib.PastaTeste;
        Directory.CreateDirectory(pasta);

        static OpcoesPGWebLib Op() => new("Pdv.AmericanDay", "0.5.9", "American Day", RedeCartao: "REDE", RedePix: "PIX ITAU");

        ProvedorPGWebLib Provedor(FakePGWebLib f, Func<TransacaoPayGo, bool>? guardar = null, int tetoExecMs = 2000)
            => new(f, pasta, Op())
            {
                IntervaloPollMs = 5,
                TempoMaxExecMs = tetoExecMs,
                TempoMaxCapturaMs = 2000,
                TempoPerguntaMs = 500,
                Guardar = guardar ?? (_ => true),
                // O SALEVOID da biblioteca pede a senha do lojista (PWDAT_USERAUTH) antes de
                // mandar ao host. Na loja quem digita é o gerente; aqui a suíte responde.
                Perguntar = (_, _) => Task.FromResult<string?>("1234"),
            };

        // ── 1. passos 44 e 46: o CANCELAMENTO aprovado também tem frase da rede ──
        //
        // "Realizar o cancelamento do passo anterior. Resultado esperado: cancelamento aprovado.
        //  Verificar: Mensagem para o operador: TRANSAÇÃO APROVADA."
        {
            var f = new FakePGWebLib { ComSenha = false, PedirRemocao = false };
            var guardadas = new List<TransacaoPayGo>();
            var p = Provedor(f, t => { guardadas.Add(t); return true; });

            var venda = p.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(1017m), null, 1, null, CancellationToken.None)
                         .GetAwaiter().GetResult();
            checar(venda.Pago && venda.Motivo == "TRANSACAO AUTORIZADA",
                "passo 43: a venda aprovada já devolve a frase da rede (" + (venda.Motivo ?? "null") + ")");

            var original = guardadas.Last(g => g.Situacao == "pago");
            var e = p.CancelarAsync(original, CancellationToken.None).GetAwaiter().GetResult();

            checar(e.Pago && e.PaymentStatus == "estornado",
                "passo 44: o cancelamento é aprovado (" + e.Situacao + "/" + e.PaymentStatus + ")");
            checar(e.Motivo == "CANCELAMENTO AUTORIZADO",
                "passo 44: e a frase da REDE sobe com ele, em vez de morrer no provedor: " + (e.Motivo ?? "null"));
            checar(e.MensagemParaTela == "CANCELAMENTO AUTORIZADO",
                "passo 44: é ela que a tela do estorno tem para mostrar: '" + e.MensagemParaTela + "'");
        }

        // ── 2. cancelamento com a biblioteca CALADA ─────────────────────────────
        //
        // O que este bloco garante: um estorno aprovado nunca mostra o texto de falha na tela
        // ("não foi possível concluir o pagamento" com o dinheiro devolvido faria o operador
        // estornar de novo). Isso vale.
        //
        // ⚠️ E o que ele DEIXA À VISTA, de propósito: com a biblioteca calada o caixa não fica
        // mudo — ele ESCREVE "TRANSACAO AUTORIZADA" no lugar da resposta da rede. São dois
        // padrões empilhados, ProvedorPGWebLib.RespostaDaLib (campo 030 do intpos) e
        // RespostaPayGo.Mensagem, e o 030 vai para `tef_transacao.resposta_txt`, que é a trilha
        // que o analista da PayGo lê. Ou seja: uma frase da casa gravada como se fosse da rede.
        // Não foi mexido aqui porque o mesmo padrão atende o PayGo por troca de arquivos, onde o
        // campo 030 é obrigatório e a falta dele é que seria o defeito; a decisão é do dono, e
        // mudar isso no meio da janela de homologação mexeria nos três provedores de uma vez.
        // Este teste existe para que a escolha apareça na bateria toda vez, e não só quando
        // alguém for ler o código.
        {
            var f = new FakePGWebLib { ComSenha = false, PedirRemocao = false, SemResultMsg = true };
            var guardadas = new List<TransacaoPayGo>();
            var p = Provedor(f, t => { guardadas.Add(t); return true; });
            p.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(10m), null, 1, null, CancellationToken.None).GetAwaiter().GetResult();
            var e = p.CancelarAsync(guardadas.Last(g => g.Situacao == "pago"), CancellationToken.None).GetAwaiter().GetResult();

            checar(e.Pago, "biblioteca calada: o cancelamento continua aprovado (" + e.Situacao + ")");
            checar(e.MensagemParaTela != "não foi possível concluir o pagamento" && e.MensagemParaTela.Length > 0,
                "e a tela não escorrega para o texto de falha num estorno aprovado: '" + e.MensagemParaTela + "'");
            checar(e.Motivo == "TRANSACAO AUTORIZADA",
                "⚠️ hoje o caixa PREENCHE a frase da rede quando ela não veio (campo 030 do intpos, " +
                "gravado em resposta_txt): '" + (e.Motivo ?? "null") + "'. Decisão do dono, não defeito novo");
        }

        // ── 3. passo 55: Esc na tela do QR, com a frase que a biblioteca escreveu ──
        //
        // "Realizar uma venda e na tela de exibição do QRCode, pressionar a tecla 'Esc'.
        //  Verificar: mensagem de erro para a Automação: OPERAÇÃO CANCELADA."
        //
        // O caminho é PW_iPPAbort e o PW_iExecTransac seguinte devolvendo PWRET_CANCEL. O que se
        // prova aqui é só isto: a mensagem que a biblioteca deixou em PWINFO_RESULTMSG CHEGA à
        // automação, em vez de ser jogada fora porque quem cancelou foi o operador.
        {
            var fechou = 0;
            var f = new FakePGWebLib
            {
                PedirQrNaTela = true, ComSenha = false, PedirRemocao = false,
                MensagemAoAbortar = "OPERACAO CANCELADA",
            };
            f.Roteiro.Enqueue(FakePGWebLib.Desfecho.NuncaTermina);
            var cts = new CancellationTokenSource();
            var p = new ProvedorPGWebLib(f, pasta, Op())
            {
                IntervaloPollMs = 5,
                TempoMaxExecMs = 3000,
                TempoMaxCapturaMs = 3000,
                TempoPerguntaMs = 500,
                Guardar = _ => true,
                Exibir = (_, _) => { cts.Cancel(); return Task.FromResult(true); },   // o Esc do operador
                FecharExibicao = () => fechou++,
            };
            var d = p.CobrarAsync(TipoTef.Pix, Dinheiro.DeReais(500m), null, 1, null, cts.Token)
                     .GetAwaiter().GetResult();

            checar(!d.Pago && d.Situacao == SituacaoTef.Cancelado,
                "passo 55: venda negada, e não erro (" + d.Situacao + ")");
            checar(d.Motivo == "OPERACAO CANCELADA",
                "passo 55: a frase da biblioteca chega à automação: " + (d.Motivo ?? "null"));
            checar(fechou == 1 && f.Abortos >= 1, "e a tela do QR fechou depois do PW_iPPAbort");
        }

        // ── 4. biblioteca calada no Esc: a frase da casa continua valendo ────────
        //
        // Este é o controle do item 3, e é o que mantém o conserto seguro se a DLL de verdade não
        // escrever RESULTMSG ao abortar: sem frase da rede, o operador lê a nossa.
        {
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
                Exibir = (_, _) => { cts.Cancel(); return Task.FromResult(true); },
                FecharExibicao = () => { },
            };
            var d = p.CobrarAsync(TipoTef.Pix, Dinheiro.DeReais(500m), null, 1, null, cts.Token)
                     .GetAwaiter().GetResult();
            checar(!d.Pago && d.Situacao == SituacaoTef.Cancelado && d.Motivo == "cobrança cancelada pelo operador",
                "sem RESULTMSG, o operador continua lendo a frase da casa: " + (d.Motivo ?? "null"));
        }

        // ── 5. passos 01 e 18: a instalação mostra o que a rede respondeu ────────
        //
        // "Realize uma instalação com os dados enviados pela PayGo. Verificar: mensagem para o
        //  operador: TRANSAÇÃO APROVADA."
        //
        // O provedor já devolvia a frase (OperacaoAsync termina com `r.Mensagem`); quem a perdia
        // era a tela. Como a Configuração precisa de WPF, banco e DPAPI para existir, o que se
        // confere aqui é a fonte — mesma receita das outras suítes.
        {
            var fonte = Fonte("Telas", "Configuracao.xaml.cs");
            if (fonte is null) checar(false, "achei Telas/Configuracao.xaml.cs");
            else
            {
                // Só o ramo do SUCESSO: o do erro já mostrava `di.Motivo`, e olhar os dois juntos
                // deixaria o teste passar sem que a instalação aprovada dissesse nada.
                var i = fonte.IndexOf("Ponto de captura instalado", StringComparison.Ordinal);
                var fim = i < 0 ? -1 : fonte.IndexOf("Instalação não concluída", i, StringComparison.Ordinal);
                var sucesso = i < 0 || fim < 0 ? "" : fonte[i..fim];
                checar(i >= 0 && fim > i, "achei os dois ramos da instalação na Configuração");
                checar(sucesso.Contains("di.Motivo", StringComparison.Ordinal),
                    "passos 01 e 18: a instalação APROVADA mostra a frase da rede, como o ramo administrativo já fazia");
            }
        }

        // ── 6. passo 32: a mensagem de 80 caracteres cabe na tela ───────────────
        //
        // "Venda aprovada com a mensagem de tamanho máximo (80 caracteres). Verificar: mensagem
        //  para o operador, ou parte da mensagem: TRANSAÇÃO DE TESTE APROVADA. CÓDIGO AUTORIZAÇAO
        //  13456789 TRANSACAO NAO PRODUTIVA."
        //
        // TextBlock do WPF não quebra linha sozinho: sem TextWrapping a frase é cortada na
        // largura da coluna e some o resto, sem nem reticências.
        {
            var xaml = Fonte("Telas", "Pagamento.xaml");
            if (xaml is null) checar(false, "achei Telas/Pagamento.xaml");
            else
            {
                var i = xaml.IndexOf("TxtRecadoTef", StringComparison.Ordinal);
                var fim = i < 0 ? -1 : xaml.IndexOf("/>", i, StringComparison.Ordinal);
                var tag = i < 0 || fim < 0 ? "" : xaml[i..fim];
                checar(i >= 0, "achei o recado do TEF na tela de pagamento");
                checar(tag.Contains("TextWrapping=\"Wrap\"", StringComparison.Ordinal),
                    "passo 32: a frase de 80 caracteres quebra linha em vez de ser cortada");
            }
        }
    }

    /// <summary>Leitura de fonte: mesma receita de TestesNotaNaHomologacao e das outras suítes de tela.</summary>
    private static string? Fonte(params string[] partes)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var alvo = Path.Combine(new[] { d.FullName }.Concat(partes).ToArray());
            if (File.Exists(alvo)) return File.ReadAllText(alvo);
            d = d.Parent;
        }
        return null;
    }
}
