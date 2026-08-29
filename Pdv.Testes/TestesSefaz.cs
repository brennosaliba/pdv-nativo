using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// Emissão e cancelamento de NFC-e contra o agente fiscal de mentira
/// (<see cref="FakeSefaz"/>): autorizada, rejeitada, contingência e agente fora do ar.
///
/// A cobertura que já existia parava no estado LOCAL da venda ("a nota nasce pendente",
/// "cancelar sem motivo é recusado"). Aqui o que se testa é a leitura da RESPOSTA — o
/// ponto em que uma nota autorizada vira chave e protocolo, e em que uma queda de rede
/// não pode virar "reemite".
///
/// ⚠️ O que este teste NÃO prova: que o XML gerado passa no schema da SEFAZ, que a
/// assinatura A1 está válida, ou que a numeração casa com o autorizador. Isso só o
/// ambiente real prova — aqui o autorizador é de mentira.
/// </summary>
public static class TestesSefaz
{
    /// <summary>
    /// "NAO SEI" DA EMISSAO NAO E "NAO TEM NOTA".
    ///
    /// fiscal_status='pendente' significa que o emissor ficou MUDO — a nota PODE ter
    /// sido assinada e autorizada do outro lado. O proprio projeto ja tinha isso escrito
    /// em Fiscal.cs:207-213 ("quem consome 'pendente' tem que CONFERIR antes"), e mesmo
    /// assim o cancelamento novo mandava 'pendente' para o balde de "esta venda nao gerou
    /// nota fiscal" e cancelava a venda sem consultar ninguem. Resultado: NFC-e viva
    /// apontando para venda cancelada, com os 30 minutos vencendo em silencio.
    ///
    /// A regra aqui e assimetrica de proposito: errar para "bloqueia" custa uma ligacao
    /// ao gerente; errar para "segue" custa uma nota que nao da mais para cancelar.
    /// </summary>
    private static void PendenteNaoEhSemNota(Action<bool, string> checar)
    {
        var agora = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Local);

        var pendente = CancelamentoVenda.Montar("pendente", null, null, null, Array.Empty<PagamentoDaVenda>(), false, agora);
        checar(pendente.Nota == SituacaoDaNota.SemResposta,
            "nfce: emissao 'pendente' e SEM RESPOSTA, nao 'sem nota'");
        checar(!pendente.PodeSeguir,
            "nfce: 'pendente' BLOQUEIA o cancelamento — nota viva em venda cancelada e o pior desfecho");
        checar(pendente.Impedimento?.Contains("conferir") == true,
            "nfce: e o impedimento manda CONFERIR na SEFAZ, nao adivinhar");

        // Valor que este codigo nao conhece tambem bloqueia: silencio nao vira permissao.
        var estranho = CancelamentoVenda.Montar("processando", null, null, null, Array.Empty<PagamentoDaVenda>(), false, agora);
        checar(estranho.Nota == SituacaoDaNota.SemResposta && !estranho.PodeSeguir,
            "nfce: fiscal_status desconhecido tambem bloqueia (falha para o lado seguro)");

        // O que REALMENTE nao tem nota continua passando — senao a tela travaria a loja
        // inteira de balcao, que nem emite.
        foreach (var f in new[] { "", "rejeitada", "erro" })
        {
            var p = CancelamentoVenda.Montar(f, null, null, null, Array.Empty<PagamentoDaVenda>(), false, agora);
            checar(p.Nota == SituacaoDaNota.SemNota && p.PodeSeguir,
                $"nfce: '{(f.Length == 0 ? "(vazio)" : f)}' e sem nota de verdade, e segue");
        }
    }

    public static void Rodar(Action<bool, string> checar)
    {
        PendenteNaoEhSemNota(checar);
        // ── CANCELAMENTO DA NFC-e (evento 110111) ─────────────────────────
        // Regras que vieram da SEFAZ e do agente, nao de gosto nosso. O que este
        // bloco protege: a JUSTIFICATIVA (15..255) e a leitura do cStat — errar
        // o segundo faz o caixa devolver dinheiro achando que a nota caiu.
        {
            // O limite exato: 15 passa, 14 nao. "cliente desistiu" (16) passaria —
            // mas o texto que o PDV sugere ja e bem maior, de proposito.
            checar(CancelamentoFiscal.JustificativaValida(new string('a', 15)),
                "15 caracteres (o minimo da SEFAZ) passa");
            checar(!CancelamentoFiscal.JustificativaValida(new string('a', 14)),
                "14 caracteres NAO passa — e a rejeicao que o agente devolveria como erro 500");
            checar(!CancelamentoFiscal.JustificativaValida("curto"),
                "justificativa curta e recusada ANTES de gastar a chamada");
            checar(!CancelamentoFiscal.JustificativaValida(null),
                "justificativa vazia e recusada");
            checar(!CancelamentoFiscal.JustificativaValida(new string(' ', 40)),
                "so espaco nao vale como justificativa");
            checar(CancelamentoFiscal.JustificativaValida("venda cancelada por desistencia do cliente"),
                "a justificativa padrao do dono passa");
            checar(!CancelamentoFiscal.JustificativaValida(new string('x', 256)),
                "acima de 255 a SEFAZ recusa — barrar aqui");

            // cStat: 135/136/155 sao sucesso; 573 e DUPLICIDADE (ja cancelada),
            // que para o caixa tambem e sucesso — senao o operador fica preso
            // depois de uma queda no meio do cancelamento.
            foreach (var ok in new[] { 135, 136, 155, 573 })
                checar(CancelamentoFiscal.Sucesso(ok), $"cStat {ok} e sucesso do cancelamento");
            foreach (var nao in new[] { 0, 101, 215, 501, 999 })
                checar(!CancelamentoFiscal.Sucesso(nao), $"cStat {nao} NAO e sucesso");

            // Guardas locais: nada disso pode virar chamada de rede.
            var semChave = CancelamentoFiscal.CancelarAsync("http://127.0.0.1:1", "123", "999", "venda cancelada por desistencia").GetAwaiter().GetResult();
            checar(!semChave.Ok && !semChave.Indisponivel && semChave.XMotivo!.Contains("chave"),
                "chave fora de 44 digitos e recusada localmente (sem rede)");
            var semProt = CancelamentoFiscal.CancelarAsync("http://127.0.0.1:1", new string('1', 44), null, "venda cancelada por desistencia").GetAwaiter().GetResult();
            checar(!semProt.Ok && !semProt.Indisponivel,
                "nota sem protocolo (contingencia) e recusada localmente — o evento 110111 exige nProt");

            // Agente fora do ar: INDISPONIVEL, nunca "recusado". A diferenca e o
            // que impede devolver dinheiro achando que a nota foi cancelada.
            var mudo = CancelamentoFiscal.CancelarAsync("http://127.0.0.1:1", new string('1', 44), "1234567890", "venda cancelada por desistencia do cliente").GetAwaiter().GetResult();
            checar(!mudo.Ok && mudo.Indisponivel,
                "agente mudo e INDISPONIVEL (nao sei), nunca recusa");
        }

        // ── CANCELAR A VENDA, COM OU SEM MAQUININHA ────────────────────────
        // O furo do dono (29/08): cancelar venda e cancelar nota moravam DENTRO do
        // estorno, e o estorno so abria com TEF integrado. Em maquininha AVULSA nao
        // existia caminho — e a NFC-e morre em 30 minutos.
        //
        // Aqui se testa a DECISAO (o que da para fazer, em que ordem, e o que o
        // operador faz com as maos), que e a parte que erra caro.
        {
            var agora = new DateTime(2026, 8, 29, 15, 0, 0, DateTimeKind.Local);
            var chave = new string('7', 44);
            static IReadOnlyList<PagamentoDaVenda> So(params PagamentoDaVenda[] p) => p;
            var cartao = So(new PagamentoDaVenda("credito", 4500));
            var especie = So(new PagamentoDaVenda("dinheiro", 3000));

            checar(CancelamentoFiscal.Prazo == TimeSpan.FromMinutes(30),
                "o prazo do evento 110111 e de 30 minutos da AUTORIZACAO");
            checar(CancelamentoFiscal.RestanteDoPrazo(agora.AddMinutes(-22), agora) == TimeSpan.FromMinutes(8),
                "22 minutos depois da nota, restam 8 do prazo");
            checar(CancelamentoFiscal.RestanteDoPrazo(agora.AddMinutes(-47), agora) < TimeSpan.Zero,
                "vencido nao satura em zero — a tela precisa dizer de quanto passou");

            // ── o caso do dono: nota viva, maquininha avulsa ────────────────
            var vivo = CancelamentoVenda.Montar("autorizada", chave, "135260", agora.AddMinutes(-22),
                cartao, estornoPeloPdv: false, agora);
            checar(vivo.PodeSeguir && vivo.Nota == SituacaoDaNota.DentroDoPrazo,
                "SEM maquininha integrada, a venda com nota viva PODE ser cancelada");
            checar(vivo.CancelaNota && vivo.PedeJustificativaFiscal,
                "havendo nota, o motivo digitado vira o xJust da SEFAZ (15..255)");
            checar(vivo.TextoDaNota.Contains("8 minutos") && vivo.TextoDaNota.Contains("110111"),
                "a tela diz quanto tempo resta do prazo");

            // ── passou dos 30 min: parar de prometer ────────────────────────
            var tarde = CancelamentoVenda.Montar("autorizada", chave, "135260", agora.AddMinutes(-47),
                cartao, estornoPeloPdv: false, agora);
            checar(tarde.Nota == SituacaoDaNota.ForaDoPrazo && tarde.Arriscado,
                "47 minutos depois, a nota esta FORA do prazo");
            checar(!tarde.TextoDaNota.Contains("vai ser cancelada"),
                "vencido o prazo, a tela NAO promete o cancelamento que a SEFAZ nao faz mais");
            checar(tarde.TextoDaNota.Contains("devolução", StringComparison.OrdinalIgnoreCase)
                   && tarde.TextoDaNota.Contains("contador"),
                "vencido o prazo, a tela diz o caminho real: nota de devolucao com o contador");
            checar(tarde.PodeSeguir,
                "prazo vencido AVISA, nao proibe: o relogio deste caixa nao e o da SEFAZ (e existe o cStat 155)");
            var cravado = CancelamentoVenda.Montar("autorizada", chave, "135260", agora.AddMinutes(-30),
                cartao, estornoPeloPdv: false, agora);
            checar(cravado.Nota == SituacaoDaNota.ForaDoPrazo,
                "no minuto 30 cravado ja conta como vencido (erra para o lado de nao prometer)");

            // ── O DINHEIRO. O mal-entendido que custa dinheiro de verdade ───
            checar(CancelamentoVenda.AvisoDoDinheiro.Contains("NENHUM dinheiro volta sozinho"),
                "o aviso do dinheiro diz, em letras, que nada volta sozinho");
            checar(vivo.Dinheiro.Count == 1 && vivo.Dinheiro[0].Contains("ESTORNE NA MAQUININHA")
                   && vivo.Dinheiro[0].Contains("na mão", StringComparison.OrdinalIgnoreCase),
                "cartao em maquininha avulsa: o estorno e NA MAQUININHA, na mao do operador");
            checar(!vivo.Dinheiro[0].Contains("volta para o cliente"),
                "nada na tela do cancelamento promete devolucao automatica");
            var comTef = CancelamentoVenda.Montar("autorizada", chave, "135260", agora.AddMinutes(-5),
                cartao, estornoPeloPdv: true, agora);
            checar(comTef.Dinheiro[0].Contains("Estornar o cartão"),
                "com maquininha integrada, a tela manda usar o estorno (que devolve e cancela no mesmo ato)");
            var mista = CancelamentoVenda.Montar(null, null, null, null,
                So(new PagamentoDaVenda("dinheiro", 3000), new PagamentoDaVenda("pix", 1500)),
                estornoPeloPdv: false, agora);
            checar(mista.Dinheiro.Count == 2,
                "uma linha por forma de pagamento — o operador nao pode esquecer metade");
            checar(mista.Dinheiro.All(l => l.Contains("na mão", StringComparison.OrdinalIgnoreCase)),
                "sem TEF, TODA forma volta na mao (lista vazia seria o operador supondo que o sistema devolveu)");
            checar(CancelamentoVenda.ComoDevolver(especie, false).Count == 1,
                "venda so em dinheiro tambem tem sua linha");
            checar(CancelamentoVenda.ResumoDasFormas(
                       So(new PagamentoDaVenda("dinheiro", 100), new PagamentoDaVenda("credito", 100)))
                   == "dinheiro + Crédito",
                "o rotulo da lista resume as formas da venda");

            // ── venda sem nota: cancela e pronto ────────────────────────────
            // ⚠️ O exemplo era "pendente", e estava ERRADO — nao a assercao, o valor.
            // 'pendente' e o emissor MUDO: a nota PODE ter sido autorizada do outro lado
            // (Fiscal.cs:207-213). Usa-lo aqui ensinava o codigo a tratar "nao sei" como
            // "nao tem", que e o defeito que PendenteNaoEhSemNota agora trava. Venda que
            // de fato nao emitiu tem fiscal_status vazio.
            var semNota = CancelamentoVenda.Montar("", null, null, null, especie, false, agora);
            checar(semNota.PodeSeguir && !semNota.CancelaNota && !semNota.PedeJustificativaFiscal,
                "venda sem nota nao chama a SEFAZ nem exige justificativa de 15 letras");

            // ── o caixa que caiu no meio: nota cancelada, venda de pe ───────
            // Estado REAL (o estorno grava a nota antes do CNC). Ate hoje nao tinha
            // saida nenhuma no PDV: a venda ficava aberta para sempre no caixa.
            var meio = CancelamentoVenda.Montar("cancelada", chave, "135260", agora.AddMinutes(-90), cartao, false, agora);
            checar(meio.PodeSeguir && meio.Nota == SituacaoDaNota.JaCancelada && !meio.CancelaNota,
                "nota ja cancelada e venda de pe: da para fechar a venda (mesmo fora do prazo da nota)");

            // ── o que NAO da para fazer daqui, dito com o motivo certo ──────
            var conting = CancelamentoVenda.Montar("contingencia", chave, null, agora.AddMinutes(-2), cartao, false, agora);
            checar(!conting.PodeSeguir && conting.Impedimento!.Contains("protocolo"),
                "contingencia (sem nProt) e recusada com o motivo certo, nao com a janela fechando");
            var semDados = CancelamentoVenda.Montar("autorizada", "123", "135260", agora, cartao, false, agora);
            checar(!semDados.PodeSeguir && semDados.Nota == SituacaoDaNota.SemDados,
                "nota autorizada sem a chave neste caixa nao cancela daqui — e a tela explica");
        }

        var itens = new List<ItemFiscal>
        {
            new("TST001", "TESTE Cookie", "19059090", "1700200", "500", "5102", "UN", 3m, 10.00m),
        };
        var pagDinheiro = new List<PagamentoFiscal> { new("01", 30.00m, null) };

        // ── autorizada: cStat 100 vira chave e protocolo ────────────────────
        {
            using var sefaz = new FakeSefaz();
            sefaz.Roteiro.Enqueue(FakeSefaz.Desfecho.Autorizar);
            var emissor = new EmissorAgente(sefaz.Url);

            var r = emissor.EmitirAsync(itens, pagDinheiro, null, CancellationToken.None)
                           .GetAwaiter().GetResult();

            checar(r.Autorizado, "nota autorizada volta como autorizada");
            checar(r.CStat == "100", "cStat 100 é o 'autorizado o uso da NF-e'");
            checar((r.Chave?.Length ?? 0) == 44, "chave de acesso tem os 44 dígitos");
            checar(!string.IsNullOrWhiteSpace(r.Protocolo), "autorizada carrega o protocolo");
            checar(!r.Contingencia, "autorizada online não é contingência");
            checar(r.Caminho == "agente", "o caminho da emissão é registrado (agente)");
        }

        // ── rejeitada: NÃO pode virar nota válida ───────────────────────────
        {
            using var sefaz = new FakeSefaz();
            sefaz.Roteiro.Enqueue(FakeSefaz.Desfecho.Rejeitar);
            var emissor = new EmissorAgente(sefaz.Url);

            var r = emissor.EmitirAsync(itens, pagDinheiro, null, CancellationToken.None)
                           .GetAwaiter().GetResult();

            checar(!r.Autorizado, "rejeitada não é autorizada");
            checar(r.CStat == "539", "o cStat da rejeição chega para o operador entender");
            checar(!string.IsNullOrWhiteSpace(r.XMotivo), "rejeição explica o motivo");
            checar(string.IsNullOrWhiteSpace(r.Chave), "rejeitada não inventa chave de acesso");
        }

        // ── contingência offline: é venda COM nota, não falha ───────────────
        {
            using var sefaz = new FakeSefaz();
            sefaz.Roteiro.Enqueue(FakeSefaz.Desfecho.Contingencia);
            var emissor = new EmissorAgente(sefaz.Url);

            var r = emissor.EmitirAsync(itens, pagDinheiro, null, CancellationToken.None)
                           .GetAwaiter().GetResult();

            checar(r.Autorizado, "contingência conta como emitida — o cliente leva cupom");
            checar(r.Contingencia, "contingência é marcada como tal para subir o XML depois");
            checar((r.Chave?.Length ?? 0) == 44, "contingência também gera chave");
        }

        // ── agente fora do ar: indisponível, NUNCA rejeitada ────────────────
        {
            using var sefaz = new FakeSefaz();
            sefaz.Roteiro.Enqueue(FakeSefaz.Desfecho.ErroDoAgente);
            var emissor = new EmissorAgente(sefaz.Url);

            var r = emissor.EmitirAsync(itens, pagDinheiro, null, CancellationToken.None)
                           .GetAwaiter().GetResult();

            checar(!r.Autorizado, "erro do agente não vira nota autorizada");
            checar(string.IsNullOrWhiteSpace(r.Chave),
                   "sem resposta boa não existe chave — reemitir cego queimaria numeração");
        }

        // ── cada emissão consome UM número (nNF não se repete) ──────────────
        {
            using var sefaz = new FakeSefaz();
            sefaz.Roteiro.Enqueue(FakeSefaz.Desfecho.Autorizar);
            sefaz.Roteiro.Enqueue(FakeSefaz.Desfecho.Autorizar);
            var emissor = new EmissorAgente(sefaz.Url);

            var a = emissor.EmitirAsync(itens, pagDinheiro, null, CancellationToken.None)
                           .GetAwaiter().GetResult();
            var b = emissor.EmitirAsync(itens, pagDinheiro, null, CancellationToken.None)
                           .GetAwaiter().GetResult();

            checar(a.Chave != b.Chave, "duas vendas nunca compartilham a mesma chave");
            checar(a.Numero != b.Numero, "cada nota consome seu próprio número");
            checar(sefaz.Notas.Count == 2, "o autorizador registrou as duas notas");
        }

        // ── CANCELAMENTO (evento 110111) ────────────────────────────────────
        {
            using var sefaz = new FakeSefaz();
            sefaz.Roteiro.Enqueue(FakeSefaz.Desfecho.Autorizar);
            var emissor = new EmissorAgente(sefaz.Url);

            var r = emissor.EmitirAsync(itens, pagDinheiro, null, CancellationToken.None)
                           .GetAwaiter().GetResult();
            checar(r.Autorizado, "nota emitida antes de cancelar");

            // justificativa curta é recusada pela SEFAZ (mínimo 15 caracteres)
            var curto = Cancelar(sefaz.Url, r.Chave!, "errei");
            checar(curto.Contains("\"cStat\":\"573\""),
                   "justificativa com menos de 15 caracteres é recusada (regra da SEFAZ)");
            checar(!sefaz.Notas[r.Chave!].Cancelada,
                   "recusa de justificativa não cancela a nota");

            // cancelamento válido
            var ok = Cancelar(sefaz.Url, r.Chave!, "venda cancelada a pedido do cliente no balcao");
            checar(ok.Contains("\"cStat\":\"135\""),
                   "cancelamento aceito volta cStat 135 (evento registrado)");
            checar(sefaz.Notas[r.Chave!].Cancelada, "a nota consta cancelada no autorizador");

            // cancelar de novo NÃO é sucesso novo
            var dedois = Cancelar(sefaz.Url, r.Chave!, "venda cancelada a pedido do cliente no balcao");
            checar(dedois.Contains("\"cStat\":\"573\""),
                   "cancelar duas vezes acusa duplicidade de evento, não sucesso");

            // chave inexistente
            var fantasma = Cancelar(sefaz.Url, new string('9', 44), "tentando cancelar nota que nao existe");
            checar(fantasma.Contains("\"cStat\":\"217\""),
                   "cancelar nota inexistente devolve 217, não sucesso silencioso");
        }

        // ── nota fiscal emitida NUNCA se apaga ──────────────────────────────
        {
            using var sefaz = new FakeSefaz();
            sefaz.Roteiro.Enqueue(FakeSefaz.Desfecho.Autorizar);
            var emissor = new EmissorAgente(sefaz.Url);

            var r = emissor.EmitirAsync(itens, pagDinheiro, null, CancellationToken.None)
                           .GetAwaiter().GetResult();
            Cancelar(sefaz.Url, r.Chave!, "venda cancelada a pedido do cliente no balcao");

            checar(sefaz.Notas.ContainsKey(r.Chave!),
                   "nota cancelada continua existindo — guarda de 5 anos, cancelar não é apagar");
            checar(sefaz.Notas[r.Chave!].MotivoCancelamento is not null,
                   "o motivo do cancelamento fica guardado com a nota");
        }
    }

    /// <summary>Chama o cancelamento direto no agente (o PDV faz isso pelo mesmo endpoint).</summary>
    private static string Cancelar(string url, string chave, string motivo)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var corpo = "{\"chave\":" + System.Text.Json.JsonSerializer.Serialize(chave)
                  + ",\"motivo\":" + System.Text.Json.JsonSerializer.Serialize(motivo) + "}";
        using var resp = http.PostAsync($"{url.TrimEnd('/')}/nfce/cancelar",
            new StringContent(corpo, System.Text.Encoding.UTF8, "application/json"))
            .GetAwaiter().GetResult();
        return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }
}
