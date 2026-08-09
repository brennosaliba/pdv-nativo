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
    public static void Rodar(Action<bool, string> checar)
    {
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
