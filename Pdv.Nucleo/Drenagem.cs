using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;

namespace Pdv.Nucleo;

/// <summary>
/// Drena a fila de sincronização (outbox) para o servidor: vendas primeiro, depois o
/// vínculo da nota de cada venda.
///
/// A fila existe porque a venda NUNCA espera a nuvem — ela é gravada localmente com
/// uma client_key única, e sobe quando der. A client_key é o que torna o reenvio
/// seguro: se a resposta se perder no caminho, o replay não vira venda dobrada (o
/// servidor deduplica por ela).
///
/// REGRAS DE ESCRITA NA FILA (aprendidas a ferro):
///  · Só o LOOP escreve no outbox, e sempre pela PK (id). Handler não escreve — a
///    client_key NÃO é única (venda e venda_cancelada compartilham a mesma) e um
///    UPDATE por client_key já escreveu diagnóstico na linha errada.
///  · O desfecho de um envio é (ok, erro): ok true/false/null decide o destino,
///    erro é o rastro humano que vai pro ultimo_erro. Falha SEM rastro foi como a
///    abertura de caixa ficou presa semanas "sem ninguém saber por quê".
/// </summary>
public sealed class Drenagem : IDisposable
{
    private readonly Nuvem _nuvem;
    private readonly string _urlNuvem;
    private readonly SemaphoreSlim _porta = new(1, 1);
    private System.Threading.Timer? _timer;

    /// <summary>Teto de recusas (4xx/negócio) antes de mandar o item pro dead-letter.</summary>
    internal const int MaxTentativas = 12;
    /// <summary>Dias FALHANDO DE VERDADE (sessão de pé) antes de desistir de um transitório.</summary>
    internal const int DiasParaDesistir = 7;

    /// <summary>O que fazer com uma linha da fila depois de tentar enviá-la.</summary>
    internal enum AcaoFila
    {
        /// <summary>Servidor confirmou: sai da fila como enviada.</summary>
        Enviado,
        /// <summary>Recusa permanente que já bateu no teto: sai da fila (auditável).</summary>
        DeadLetter,
        /// <summary>Recusa permanente ainda dentro do teto: conta +1 tentativa e reinsiste.</summary>
        ContaTentativa,
        /// <summary>Transitório que já falha há dias: desiste para não starvar a fila.</summary>
        ExpiraVelho,
        /// <summary>Transitório recente: registra o rastro e tenta na próxima varredura.</summary>
        Aguarda,
    }

    /// <summary>
    /// Decide o destino de uma linha da fila a partir do desfecho do envio. É pura de
    /// propósito — a regra do dead-letter é matemática sutil (contar tentativa só na
    /// recusa PERMANENTE, nunca na rede; expirar por TEMPO DE FALHA, não por contador)
    /// e é justamente onde a fila entupia. Testada em Pdv.Testes sem depender de rede.
    ///
    ///  · ok == true  → Enviado.
    ///  · ok == false → recusa permanente (4xx/negócio): ContaTentativa até o teto,
    ///                  depois DeadLetter. Rede NUNCA cai aqui (senão um 5xx viraria
    ///                  desistência).
    ///  · ok == null  → transitório (rede/5xx/dependência ainda não satisfeita):
    ///                  Aguarda, exceto se a PRIMEIRA falha real foi há mais de
    ///                  DiasParaDesistir (aí ExpiraVelho, senão ficaria eterna).
    ///
    /// primeiroErroEm é a primeira falha COM SESSÃO DE PÉ, não o criado_em: um
    /// terminal religado depois de semanas desligado chegaria aqui com itens
    /// "velhos" e os perderia no primeiro soluço do servidor — venda real sumindo
    /// do faturamento por causa de um 503 de cold start. Cada item ganha o
    /// orçamento INTEIRO de dias contado a partir de quando começou a falhar de
    /// verdade (null = ainda nem falhou: Aguarda e o loop carimba agora).
    /// </summary>
    internal static AcaoFila DecidirFila(bool? ok, long tentativas, DateTime? primeiroErroEm, DateTime agora)
    {
        if (ok == true) return AcaoFila.Enviado;
        if (ok == false) return tentativas + 1 >= MaxTentativas ? AcaoFila.DeadLetter : AcaoFila.ContaTentativa;
        return primeiroErroEm is { } p && p < agora.AddDays(-DiasParaDesistir)
            ? AcaoFila.ExpiraVelho
            : AcaoFila.Aguarda;
    }

    public Drenagem(Nuvem nuvem, string urlNuvem)
    {
        _nuvem = nuvem;
        _urlNuvem = urlNuvem.TrimEnd('/');
    }

    /// <summary>
    /// Dispara a subida sem esperar. É o que a tela de pagamento chama assim que a
    /// venda é gravada.
    ///
    /// POR QUE ISSO EXISTE: a venda subia SÓ quando alguém apertava "Sincronizar".
    /// Numa noite em que ninguém apertou, o dono abriu o painel e viu R$ 0,00 de
    /// faturamento — com o caixa tendo vendido o dia inteiro. Relatório que só
    /// existe se alguém lembrar de um botão não é relatório, é armadilha.
    ///
    /// O "Sincronizar" continua existindo, mas com o outro sentido: puxar do painel
    /// para o caixa (catálogo, preço, operadores).
    /// </summary>
    public void Cutucar() => _ = Task.Run(() => DrenarAsync());

    /// <summary>
    /// Liga o ciclo periódico e o gatilho de volta de rede — a rede do balcão cai, e
    /// quando volta a fila tem que ir sozinha. Sem isso, uma queda de 3 minutos
    /// deixaria as vendas presas até o próximo toque manual.
    /// </summary>
    public IDisposable Iniciar(TimeSpan? intervalo = null)
    {
        var passo = intervalo ?? TimeSpan.FromSeconds(45);
        _timer = new System.Threading.Timer(_ => Cutucar(), null, TimeSpan.FromSeconds(10), passo);
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += AoMudarRede;
        return this;
    }

    private void AoMudarRede(object? s, EventArgs e) => Cutucar();

    public void Dispose()
    {
        try { System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= AoMudarRede; } catch { }
        _timer?.Dispose();
        _porta.Dispose();
    }

    /// <summary>Envia o que der. Devolve quantos itens o servidor confirmou. NUNCA lança.</summary>
    public async Task<int> DrenarAsync(CancellationToken ct = default)
    {
        // Uma varredura por vez: duas simultâneas (o timer e o Cutucar da venda que
        // acabou de fechar) mandariam a mesma linha duas vezes. A client_key protege
        // do lado do servidor, mas gastar a chamada à toa atrasa a fila.
        if (!await _porta.WaitAsync(0, ct).ConfigureAwait(false)) return 0;
        try
        {
            if (!await _nuvem.SessaoOkAsync(ct).ConfigureAwait(false)) return 0;
            var token = await _nuvem.TokenAsync(ct).ConfigureAwait(false);
            if (token is null) return 0;

            var enviados = 0;
            List<dynamic> fila;
            using (var cx = Banco.Abrir())
                fila = cx.Query($"""
                    SELECT id, tipo, ref_id, client_key, payload, tentativas, primeiro_erro_em
                      FROM outbox
                     WHERE enviado_em IS NULL
                       AND desistido_em IS NULL
                       AND tipo IN ('{string.Join("','", TiposComHandler)}')
                     ORDER BY id
                     LIMIT 50
                    """).ToList();

            // as vendas vão primeiro: o vínculo da nota precisa do id que a venda ganha
            // no servidor, então a ordem por id da fila já resolve (a venda entra antes)
            var vendaNaNuvem = new Dictionary<string, string>();   // ref_id local -> sale_id da nuvem

            foreach (var item in fila)
            {
                if (ct.IsCancellationRequested) break;
                var tipo = (string)item.tipo;
                var refId = (string)item.ref_id;
                var (ok, erro) = tipo switch
                {
                    "venda" => await EnviarVendaAsync((string)item.payload, refId, vendaNaNuvem, token, ct).ConfigureAwait(false),
                    "nfce_vinculo" => await VincularNotaAsync((string)item.payload, refId, vendaNaNuvem, token, ct).ConfigureAwait(false),
                    "fechamento" => await EnviarFechamentoAsync((string)item.payload, refId, token, ct).ConfigureAwait(false),
                    // Sangria/suprimento: alimenta o painel antifraude (sinal de sangria).
                    // Sem isto a tabela pdv_caixa_movimentos fica vazia e o controle da
                    // operação de maior risco nunca dispara.
                    "movimento" => await EnviarMovimentoAsync(refId, (string)item.client_key, token, ct).ConfigureAwait(false),
                    // Abertura do turno. Sem este ramo o item ficava preso na fila para
                    // sempre (a fila só crescia) e a nuvem nunca sabia a que horas o
                    // caixa abriu — o que deixava o "abriu atrasado" do relatório sem
                    // a metade da informação.
                    "caixa_sessao" => await EnviarAberturaAsync(refId, (string)item.client_key, token, ct).ConfigureAwait(false),
                    // cancelamento de venda NÃO-fiscal: marca a venda como cancelada na
                    // nuvem. (Venda com nota autorizada nem chega aqui — Vendas.Cancelar
                    // recusa antes; a nota se cancela na SEFAZ.)
                    "venda_cancelada" => await CancelarVendaAsync((string)item.client_key, (string)item.payload, token, ct).ConfigureAwait(false),
                    // Resgate de cortesia que falhou na hora (rede caiu). Sem esta fila,
                    // o cupom parcial continuava ATIVO no servidor apos a venda: cliente
                    // levava os itens de graca E o cupom ficava resgatavel de novo, sem
                    // rastro. Agora o resgate e' duravel como o resto.
                    "cortesia_resgate" => await ResgatarCortesiaAsync((string)item.payload, token, ct).ConfigureAwait(false),
                    // PRONTO do KDS: carimba kds_pronto_em na nuvem; a ponte no
                    // servidor ve o carimbo e dispara o readyToPickup no iFood.
                    "kds_pronto" => await EnviarKdsProntoAsync(refId, token, ct).ConfigureAwait(false),
                    // Estorno de cartao/PIX que saiu SEM aprovacao remota (caiu para o
                    // PIN do supervisor). E' o unico caminho pelo qual esse fato sai do
                    // disco daquele caixa: a tabela `auditoria` nao sobe e nenhuma tela
                    // do PDV a le. Sem esta linha, "o dono lista depois" so acontece indo
                    // ate a loja com o SQLite na mao.
                    Autorizacao.TipoNaFila => await EnviarEstornoSemAprovacaoAsync(
                        (string)item.payload, (string)item.client_key, token, ct).ConfigureAwait(false),
                    // Tipo sem handler NÃO pode virar retry eterno em silêncio (foi assim
                    // que caixa_sessao e venda_cancelada entupiram a fila): false o manda
                    // para o dead-letter abaixo depois de poucas tentativas.
                    _ => (false, $"tipo sem handler: {tipo}"),
                };

                var tentativas = item.tentativas is null ? 0L : (long)item.tentativas;
                DateTime? primeiroErro = item.primeiro_erro_em is string pes
                    && DateTime.TryParse(pes, out var pe) ? pe : null;
                var agora = DateTime.Now;

                // ÚNICO ponto que escreve no outbox, sempre por id (PK). Ver o topo
                // da classe: client_key não é única, e escrever por ela já errou o alvo.
                using var cx = Banco.Abrir();
                switch (DecidirFila(ok, tentativas, primeiroErro, agora))
                {
                    case AcaoFila.Enviado:
                        // erro aqui é uma NOTA de desfecho (ex.: "venda nunca subiu;
                        // nada a cancelar na nuvem") — vale guardar; senão preserva.
                        //
                        // MAS o rastro de DESISTÊNCIA morre aqui, sempre. O contador de
                        // pendências lê `ultimo_erro LIKE 'desistido%'` para enxergar as
                        // linhas antigas; preservá-lo depois de o servidor CONFIRMAR fazia
                        // a venda reenviada com sucesso continuar somando no aviso — o
                        // "apertei Sincronizar e continua 16" seguiria igual, agora com a
                        // fila certa e o número mentindo. Desfecho novo apaga rastro velho.
                        cx.Execute("""
                            UPDATE outbox
                               SET enviado_em   = @Em,
                                   desistido_em = NULL,
                                   ultimo_erro  = CASE
                                       WHEN @E IS NOT NULL THEN @E
                                       WHEN COALESCE(ultimo_erro,'') LIKE 'desistido%'
                                         OR COALESCE(ultimo_erro,'') LIKE 'reaberto%' THEN NULL
                                       ELSE ultimo_erro END
                             WHERE id = @Id
                            """, new { Em = agora.ToString("o"), E = erro, Id = (long)item.id });
                        enviados++;
                        break;
                    // DEAD-LETTER: recusa que se repete não pode ficar eterna nem entupir
                    // a janela de 50 (starvation: a fila só devolve linhas mortas e nada
                    // novo sobe). Depois de MaxTentativas, sai da JANELA DE DRENAGEM com
                    // o motivo gravado — mas em desistido_em, NUNCA em enviado_em: a
                    // nuvem não recebeu nada. Marcar as duas coisas na mesma coluna foi o
                    // que fez R$ 102.626,50 sumirem do contador de pendentes.
                    case AcaoFila.DeadLetter:
                        cx.Execute("UPDATE outbox SET desistido_em = @Em, tentativas = tentativas + 1, ultimo_erro = @E WHERE id = @Id",
                            new { Em = agora.ToString("o"),
                                  E = $"desistido após {tentativas + 1} tentativas — {erro ?? "recusado pelo servidor"}",
                                  Id = (long)item.id });
                        break;
                    case AcaoFila.ContaTentativa:
                        cx.Execute("UPDATE outbox SET tentativas = tentativas + 1, ultimo_erro = @E WHERE id = @Id",
                            new { E = erro ?? "recusado pelo servidor", Id = (long)item.id });
                        break;
                    // Transitório que já falha há DiasParaDesistir (contados da primeira
                    // falha real): desiste para não starvar a janela para sempre.
                    case AcaoFila.ExpiraVelho:
                        cx.Execute("UPDATE outbox SET desistido_em = @Em, ultimo_erro = @E WHERE id = @Id",
                            new { Em = agora.ToString("o"),
                                  E = $"desistido: dias falhando sem conseguir enviar — {erro ?? "sem resposta"}",
                                  Id = (long)item.id });
                        break;
                    // Transitório recente: NÃO conta tentativa (rede volta), mas deixa o
                    // rastro (um 5xx repetido era mudo — "presa sem ninguém saber por quê")
                    // e carimba a primeira falha real, que é o relógio da expiração.
                    case AcaoFila.Aguarda:
                        cx.Execute("""
                            UPDATE outbox SET ultimo_erro = COALESCE(@E, ultimo_erro),
                                              primeiro_erro_em = COALESCE(primeiro_erro_em, @Agora)
                             WHERE id = @Id
                            """, new { E = erro, Agora = agora.ToString("o"), Id = (long)item.id });
                        break;
                }
            }
            return enviados;
        }
        catch { return 0; }
        finally { _porta.Release(); }
    }

    /// <summary>(true,_) confirmada · (false,motivo) recusa permanente · (null,motivo) transitório.</summary>
    private async Task<(bool? Ok, string? Erro)> EnviarVendaAsync(string payload, string refId,
        Dictionary<string, string> vendaNaNuvem, string token, CancellationToken ct)
    {
        var (status, corpo) = await RpcAsync("pdv_registrar_venda", payload, token, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300) return DesfechoDeStatus(status, corpo);
        try
        {
            using var doc = JsonDocument.Parse(corpo!);
            var r = doc.RootElement;
            if (r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                if (r.TryGetProperty("sale_id", out var sid) && sid.GetString() is { } s)
                    vendaNaNuvem[refId] = s;
                return (true, null);
            }
            // erro de negócio (ex.: sem_caixa_aberto): registrar e não travar a fila
            return (false, $"recusa de negócio: {Corta(corpo)}");
        }
        catch { return (null, "resposta ilegível do servidor"); }
    }

    private async Task<(bool? Ok, string? Erro)> VincularNotaAsync(string payload, string refId,
        Dictionary<string, string> vendaNaNuvem, string token, CancellationToken ct)
    {
        // o vínculo precisa do id da venda NO SERVIDOR. Se a venda subiu agora, temos;
        // se subiu numa varredura anterior, pedimos ao servidor pela client_key.
        if (!vendaNaNuvem.TryGetValue(refId, out var saleId))
        {
            string? clientKey;
            using (var cx = Banco.Abrir())
                clientKey = cx.ExecuteScalar<string?>(
                    "SELECT client_key FROM venda WHERE id = @Id", new { Id = refId });
            if (clientKey is null) return (false, "venda local sumiu: nada a vincular");

            var busca = await RestAsync($"/rest/v1/pdv_sales?select=id&client_key=eq.{clientKey}&limit=1",
                token, ct).ConfigureAwait(false);
            if (busca is null) return (null, "sem resposta na busca da venda na nuvem");
            try
            {
                using var doc = JsonDocument.Parse(busca);
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                    // A venda não está na nuvem. Se a linha 'venda' dela ainda está NA
                    // FILA, é transitório de verdade (ela sobe e aí vinculamos). Mas se
                    // já SAIU da fila sem chegar lá (dead-letter/expirada), esperar é
                    // eterno — e um punhado desses órfãos, com ids baixos, ocupava a
                    // janela LIMIT 50 inteira e TRAVAVA a subida de venda nova com a
                    // rede perfeita. Órfão real vira recusa e dead-letter auditável.
                    return VendaAindaNaFila(clientKey)
                        ? (null, "aguardando a venda subir para vincular a nota")
                        : (false, "a venda deste vínculo nunca subiu (desistida): órfão");
                saleId = doc.RootElement[0].GetProperty("id").GetString()!;
            }
            catch { return (null, "resposta ilegível na busca da venda"); }
        }

        // injeta o p_sale_id no payload que ficou guardado
        string corpoFinal;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var dict = new Dictionary<string, object?> { ["p_sale_id"] = saleId };
            foreach (var p in doc.RootElement.EnumerateObject())
                dict[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()
                    : p.Value.ValueKind == JsonValueKind.Null ? null : (object)p.Value.GetRawText();
            corpoFinal = JsonSerializer.Serialize(dict);
        }
        catch { return (false, "payload do vínculo corrompido"); }

        var (status, resp) = await RpcAsync("pdv_vincular_nfce", corpoFinal, token, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300) return DesfechoDeStatus(status, resp);
        try
        {
            using var doc = JsonDocument.Parse(resp!);
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                ? (true, null)
                : (false, $"vínculo recusado: {Corta(resp)}");
        }
        catch { return (null, "resposta ilegível do vínculo"); }
    }

    /// <summary>
    /// Sobe o fechamento de caixa — é ele que alimenta o relatório de quebras do
    /// painel. O payload da fila só tem as linhas; o contexto (dia, fundo, quem fechou)
    /// vem da sessão local na hora do envio. O client_key = id da sessão segura o
    /// reenvio: fechamento é registro único, não pode duplicar no replay.
    /// </summary>
    private async Task<(bool? Ok, string? Erro)> EnviarFechamentoAsync(string payload, string sessaoId, string token, CancellationToken ct)
    {
        try
        {
            dynamic? s;
            string? loja;
            string? terminalUuid;
            using (var cx = Banco.Abrir())
            {
                s = cx.QueryFirstOrDefault(
                    "SELECT business_date, fundo_troco_cent, fechado_por, fechamento_em FROM caixa_sessao WHERE id = @Id",
                    new { Id = sessaoId });
                var t = cx.QueryFirstOrDefault("SELECT terminal_uuid, loja_nome FROM terminal LIMIT 1");
                loja = t?.loja_nome as string;
                terminalUuid = t?.terminal_uuid as string;
            }
            if (s is null || s.fechamento_em is null)
                return (false, "sessão sem fechamento local: não sobe");

            long desvio = 0;
            using (var doc = JsonDocument.Parse(payload))
                if (doc.RootElement.TryGetProperty("linhas", out var arr))
                    foreach (var l in arr.EnumerateArray())
                        if (l.TryGetProperty("dif", out var d) && d.TryGetInt64(out var v))
                            desvio += Math.Abs(v);

            var corpo = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["store"] = loja,
                ["terminal_uuid"] = terminalUuid,
                ["business_date"] = (string)s.business_date,
                ["abertura_cent"] = (long)s.fundo_troco_cent,
                ["linhas"] = JsonSerializer.Deserialize<JsonElement>(payload).GetProperty("linhas"),
                ["desvio_cent"] = desvio,
                ["justificativa"] = JsonDocument.Parse(payload).RootElement.TryGetProperty("justificativa", out var j)
                    && j.ValueKind == JsonValueKind.String ? j.GetString() : null,
                ["fechado_por"] = s.fechado_por as string,
                ["fechado_em"] = s.fechamento_em as string,
                ["client_key"] = sessaoId,
            });

            return await PostRestAsync("/rest/v1/pdv_caixa_fechamentos?on_conflict=client_key",
                corpo, token, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { return (null, Corta(ex.Message)); }
    }

    /// <summary>
    /// Sobe uma sangria/suprimento para pdv_caixa_movimentos — a matéria-prima do
    /// sinal antifraude de sangria ("sempre o mesmo operador+autorizador no fim do
    /// turno"). O payload da fila é magro; o contexto (loja, dia, nomes) vem das
    /// tabelas locais na hora do envio, igual ao fechamento. client_key = id do
    /// movimento segura o replay: movimento é registro único, não pode duplicar.
    /// Estorno NÃO sobe: a tabela da nuvem só aceita sangria/suprimento.
    /// </summary>
    private async Task<(bool? Ok, string? Erro)> EnviarMovimentoAsync(string movId, string clientKey, string token, CancellationToken ct)
    {
        try
        {
            dynamic? m;
            string? loja, terminalUuid, businessDate, operadorNome, autorizadorNome;
            using (var cx = Banco.Abrir())
            {
                m = cx.QueryFirstOrDefault("""
                    SELECT tipo, valor_cent, motivo, destino, operador_id, autorizado_por, sessao_id, criado_em
                      FROM caixa_movimento WHERE id = @Id
                    """, new { Id = movId });
                if (m is null) return (false, "movimento sumiu do local: não insiste");
                if (m.tipo != "sangria" && m.tipo != "suprimento")
                    return (true, "estorno não sobe (só sangria/suprimento)");

                businessDate = cx.ExecuteScalar<string?>(
                    "SELECT business_date FROM caixa_sessao WHERE id = @Id", new { Id = (string)m.sessao_id });
                operadorNome = cx.ExecuteScalar<string?>(
                    "SELECT nome FROM operador WHERE id = @Id", new { Id = (string)m.operador_id });
                autorizadorNome = m.autorizado_por is string ap
                    ? cx.ExecuteScalar<string?>("SELECT nome FROM operador WHERE id = @Id", new { Id = ap })
                    : null;
                var t = cx.QueryFirstOrDefault("SELECT terminal_uuid, loja_nome FROM terminal LIMIT 1");
                loja = t?.loja_nome as string;
                terminalUuid = t?.terminal_uuid as string;
            }

            var corpo = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["client_key"] = clientKey,
                ["store"] = loja,
                ["terminal_uuid"] = terminalUuid,
                ["sessao_id"] = (string)m.sessao_id,
                ["business_date"] = businessDate,
                ["tipo"] = (string)m.tipo,
                ["valor_cent"] = (long)m.valor_cent,
                ["motivo"] = m.motivo as string,
                ["destino"] = m.destino as string,
                ["operador_id"] = m.operador_id as string,
                ["operador_nome"] = operadorNome,
                ["autorizado_por"] = m.autorizado_por as string,
                ["autorizador_nome"] = autorizadorNome,
                ["criado_em"] = m.criado_em as string,
            });

            return await PostRestAsync("/rest/v1/pdv_caixa_movimentos?on_conflict=client_key",
                corpo, token, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { return (null, Corta(ex.Message)); }
    }

    /// <summary>
    /// Sobe a ABERTURA do turno. Junto com o fechamento, fecha a linha do tempo do
    /// caixa na nuvem ("abriu 08:12, fechou 22:47") — que é o que permite ao painel
    /// dizer se a loja abriu no horário.
    /// </summary>
    private async Task<(bool? Ok, string? Erro)> EnviarAberturaAsync(string sessaoId, string clientKey, string token, CancellationToken ct)
    {
        try
        {
            dynamic? s;
            string? loja, terminalUuid, operadorNome;
            using (var cx = Banco.Abrir())
            {
                s = cx.QueryFirstOrDefault("""
                    SELECT business_date, operador_id, operador_nome, abertura_em,
                           fundo_troco_cent, fechamento_em, fechado_por
                      FROM caixa_sessao WHERE id = @Id
                    """, new { Id = sessaoId });
                if (s is null) return (false, "sessão sumiu do local: não adianta insistir");

                operadorNome = s.operador_nome as string
                    ?? cx.ExecuteScalar<string?>("SELECT nome FROM operador WHERE id = @Id",
                        new { Id = (string)s.operador_id });
                var t = cx.QueryFirstOrDefault("SELECT terminal_uuid, loja_nome FROM terminal LIMIT 1");
                loja = t?.loja_nome as string;
                terminalUuid = t?.terminal_uuid as string;
            }

            if (string.IsNullOrWhiteSpace(loja))
                return (null, "terminal ainda sem loja definida: tenta depois");

            var corpo = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["client_key"] = clientKey,
                ["store"] = loja,
                ["terminal_uuid"] = terminalUuid,
                ["business_date"] = s.business_date as string,
                ["operador_id"] = s.operador_id as string,
                ["operador_nome"] = operadorNome,
                ["abertura_em"] = s.abertura_em as string,
                ["fundo_cent"] = (long)s.fundo_troco_cent,
                ["fechamento_em"] = s.fechamento_em as string,
                ["fechado_por"] = s.fechado_por as string,
            });

            return await PostRestAsync("/rest/v1/pdv_caixa_sessoes?on_conflict=client_key",
                corpo, token, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { return (null, Corta(ex.Message)); }
    }

    /// <summary>
    /// Espelha na nuvem um cancelamento JÁ autorizado no terminal. Vai pela RPC
    /// pdv_sync_cancelamento (device-callable) — NÃO pela pdv_cancelar_venda do
    /// painel, que exige owner/manager e recusaria o token do dispositivo.
    ///
    /// O payload da fila é <c>{ venda, motivo, por }</c>; a venda na nuvem é achada
    /// pela client_key (a MESMA da linha 'venda' — por isso handler nenhum escreve
    /// no outbox por client_key).
    /// </summary>
    private async Task<(bool? Ok, string? Erro)> CancelarVendaAsync(string clientKey, string payload, string token, CancellationToken ct)
    {
        string? motivo = null, por = null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var r = doc.RootElement;
            if (r.TryGetProperty("motivo", out var m) && m.ValueKind == JsonValueKind.String) motivo = m.GetString();
            if (r.TryGetProperty("por", out var p) && p.ValueKind == JsonValueKind.String) por = p.GetString();
        }
        catch { /* payload corrompido: manda mesmo assim; a RPC valida */ }

        var corpo = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["p_client_key"] = clientKey,
            ["p_motivo"] = motivo ?? "cancelado no PDV",
            ["p_por"] = por,
        });

        var (status, resp) = await RpcAsync("pdv_sync_cancelamento", corpo, token, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300) return DesfechoDeStatus(status, resp);
        try
        {
            using var doc = JsonDocument.Parse(resp!);
            var r = doc.RootElement;
            if (r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                return (true, null);

            var err = r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() : null;

            // 'venda_nao_encontrada' NÃO é recusa: a venda deste cancelamento ainda não
            // subiu. Desistir aqui era o pior bug desta fila — o cancel morria em 12
            // tentativas (~9 min), a venda subia DEPOIS (um 5xx transitório dela), e a
            // venda CANCELADA no caixa virava faturamento VIVO na nuvem para sempre,
            // sem trilha de cancelamento. O cancel tem que viver exatamente enquanto
            // a venda que ele neutraliza tiver futuro:
            //  · venda ainda na fila → espera (transitório de verdade);
            //  · venda já desistida  → a nuvem nunca vai tê-la: não há o que cancelar
            //    lá, e o estado JÁ é consistente (sem venda = sem faturamento). Marca
            //    resolvido com a nota no rastro.
            if (err == "venda_nao_encontrada")
                return VendaAindaNaFila(clientKey)
                    ? (null, "aguardando a venda subir para cancelar na nuvem")
                    : (true, "venda nunca subiu à nuvem: nada a cancelar lá (estado consistente)");

            // Recusas permanentes de verdade: tem_nota_fiscal (fronteira SEFAZ),
            // sem_client_key, sem_sessao.
            return (false, $"cancel recusado: {Corta(resp)}");
        }
        catch { return (null, "resposta ilegível do cancelamento"); }
    }

    /// <summary>
    /// Queima o cupom de cortesia na nuvem (courtesy_redeem). Roda pela fila quando
    /// o resgate na hora da venda falhou por rede — o cupom nao pode ficar ativo
    /// depois que o cliente ja levou os itens. Tri-state: 'ja_resgatado' conta como
    /// sucesso (o cupom esta morto, que e' o objetivo).
    /// </summary>
    private async Task<(bool? Ok, string? Erro)> ResgatarCortesiaAsync(string payload, string token, CancellationToken ct)
    {
        string? codigo = null, operador = null, loja = null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var r = doc.RootElement;
            if (r.TryGetProperty("codigo", out var c) && c.ValueKind == JsonValueKind.String) codigo = c.GetString();
            if (r.TryGetProperty("operador", out var o) && o.ValueKind == JsonValueKind.String) operador = o.GetString();
            if (r.TryGetProperty("loja", out var l) && l.ValueKind == JsonValueKind.String) loja = l.GetString();
        }
        catch { return (false, "payload de cortesia corrompido"); }

        if (string.IsNullOrWhiteSpace(codigo)) return (false, "cortesia sem código");

        var corpo = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["_code"] = codigo,
            ["_staff_name"] = operador,
            ["_store"] = loja,
        });

        var (status, resp) = await RpcAsync("courtesy_redeem", corpo, token, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300) return DesfechoDeStatus(status, resp);
        try
        {
            using var doc = JsonDocument.Parse(resp!);
            var r = doc.RootElement;
            if (r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                return (true, null);

            var err = r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() : null;

            // Cupom já resgatado: o objetivo (cupom morto) já foi atingido — sucesso.
            if (err is "ja_resgatado" or "already_redeemed" or "cortesia_ja_resgatada")
                return (true, null);

            return (false, $"resgate recusado: {err ?? Corta(resp)}");
        }
        catch { return (null, "resposta ilegível do resgate"); }
    }

    /// <summary>
    /// Devolve à fila o que está em DEAD-LETTER, para UMA tentativa a mais. É a única
    /// saída do estado terminal, e ela é do OPERADOR — o botão Sincronizar, depois de
    /// alguém ter tratado o motivo no painel.
    ///
    /// POR QUE ISSO EXISTE: o caixa da loja carregava 16 vendas — R$ 102.626,50 —
    /// recusadas com 409 porque o operador do PDV não existia em `employees`. O aviso
    /// dizia "confira antes de fechar o mês" e ficava lá para sempre: a drenagem não
    /// olha linha desistida (é o que impede o laço), e NENHUM outro trecho do PDV
    /// sabia tirá-las dali. Cadastrar o operador no painel não mudava nada na tela.
    ///
    /// TRÊS CUIDADOS, cada um por um jeito conhecido de errar isto:
    ///  · `tentativas` NÃO é zerado (só sobe até o teto se estiver abaixo). Assim uma
    ///    recusa permanente devolve a linha ao estado terminal na MESMA varredura, com
    ///    o motivo novo — um toque, uma tentativa. Zerar o contador transformaria cada
    ///    clique num novo ciclo de 12 chamadas contra um servidor que já disse não.
    ///  · `enviado_em` volta a NULL. Nas linhas antigas ele foi carimbado pelo build que
    ///    marcava desistência e entrega na mesma coluna; sem limpá-lo, o WHERE da
    ///    drenagem continuaria pulando a linha e o reenvio não reenviaria nada.
    ///  · venda de HOMOLOGAÇÃO fica onde está. Ela não deve subir nunca: o roteiro da
    ///    PayGo (R$ 990 + R$ 1.003 + R$ 500, e a de "valor máximo") viraria faturamento
    ///    de verdade no painel. Vale para os dependentes dela também.
    ///
    /// `primeiro_erro_em` é preservado de propósito: é o relógio dos 7 dias, e reiniciá-lo
    /// daria sobrevida infinita a um transitório que nunca vai passar.
    /// </summary>
    /// <returns>Quantas linhas voltaram para a fila.</returns>
    public static int ReabrirDesistidas()
    {
        try
        {
            using var cx = Banco.Abrir();
            return cx.Execute($"""
                UPDATE outbox
                   SET desistido_em = NULL,
                       enviado_em   = NULL,
                       tentativas   = MAX(tentativas, {MaxTentativas}),
                       ultimo_erro  = 'reaberto pelo operador — ' || COALESCE(ultimo_erro, 'sem rastro')
                 WHERE (desistido_em IS NOT NULL OR COALESCE(ultimo_erro,'') LIKE 'desistido%')
                   AND tipo IN ('{string.Join("','", TiposComHandler)}')
                   AND ref_id NOT IN (SELECT id FROM venda WHERE homologacao = 1)
                """);
        }
        catch { return 0; }
    }

    /// <summary>
    /// A linha 'venda' desta client_key ainda espera envio? (dependência viva)
    ///
    /// "Ainda na fila" é ter FUTURO, não apenas não ter sido entregue: a venda que
    /// DESISTIU nunca vai subir, e um dependente esperando por ela esperaria para
    /// sempre. Por isso desistido_em sai daqui junto com enviado_em — foi exatamente
    /// esse "espera eterna" que travava a janela de 50 com órfãos de id baixo.
    /// </summary>
    private static bool VendaAindaNaFila(string clientKey)
    {
        try
        {
            using var cx = Banco.Abrir();
            return cx.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM outbox WHERE tipo = 'venda' AND client_key = @K "
                + "AND enviado_em IS NULL AND desistido_em IS NULL",
                new { K = clientKey }) > 0;
        }
        catch { return true; }   // na dúvida, espera — desistir é o irreversível
    }

    /// <summary>
    /// POST PostgREST com o contrato do replay: 2xx ou "duplicate" = sucesso
    /// (ignore-duplicates), 4xx = recusa permanente, 5xx/timeout = transitório.
    /// É o tail comum de fechamento/movimento/abertura.
    /// </summary>
    private async Task<(bool? Ok, string? Erro)> PostRestAsync(string caminho, string corpo, string token, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(20_000);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_urlNuvem}{caminho}");
            req.Headers.TryAddWithoutValidation("apikey", Nuvem.AnonKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            // duplicata (replay da fila) é SUCESSO: o registro já está lá
            req.Headers.TryAddWithoutValidation("Prefer", "resolution=ignore-duplicates");
            req.Content = new StringContent(corpo, Encoding.UTF8, "application/json");
            using var resp = await Fiscal.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return (true, null);
            var texto = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (texto.Contains("duplicate", StringComparison.OrdinalIgnoreCase)) return (true, null);
            return DesfechoDeStatus((int)resp.StatusCode, texto);
        }
        catch (Exception ex) { return (null, Corta(ex.Message)); }
    }

    /// <summary>
    /// Chama uma RPC e devolve (status HTTP, corpo). status 0 = nem resposta
    /// (rede/timeout). É a distinção que separa "tenta de novo" (5xx/0) de
    /// "recusa permanente" (4xx) — colapsar tudo em null fazia um payload
    /// malformado (400) ou uma RPC renomeada (404) virar retry ETERNO e mudo.
    /// </summary>
    /// <summary>
    /// FONTE UNICA do filtro da fila. Tipo novo entra AQUI e ganha um ramo no
    /// switch — na primeira versao o kds_pronto ganhou handler mas ficou fora
    /// do SELECT hardcoded: a linha existia, o handler existia, e o aviso
    /// nunca saiu do lugar. Revisao adversarial pegou; o teste agora vigia.
    /// </summary>
    public static readonly string[] TiposComHandler =
        { "venda", "nfce_vinculo", "venda_cancelada", "fechamento",
          "movimento", "caixa_sessao", "cortesia_resgate", "kds_pronto",
          Autorizacao.TipoNaFila };

    /// <summary>
    /// Sobe o estorno que ESCAPOU do token de WhatsApp (saiu pelo PIN do supervisor,
    /// ou pelo modo de homologacao) para pdv_estornos_sem_aprovacao. E' a lista que o
    /// dono pediu — e ate aqui ela existia so no SQLite do caixa, que ninguem le.
    ///
    /// O payload ja vem completo do nucleo (Autorizacao.AuditarSemAprovacaoRemota);
    /// aqui so entram os dois campos que sao do TERMINAL e nao do estorno, e que
    /// podem ter mudado entre o estorno e o envio (a fila espera a rede voltar).
    ///
    /// client_key = referencia do estorno: replay da fila nao vira linha dobrada.
    /// </summary>
    private async Task<(bool? Ok, string? Erro)> EnviarEstornoSemAprovacaoAsync(
        string payload, string clientKey, string token, CancellationToken ct)
    {
        try
        {
            string? loja, terminalUuid;
            using (var cx = Banco.Abrir())
            {
                var t = cx.QueryFirstOrDefault("SELECT terminal_uuid, loja_nome FROM terminal LIMIT 1");
                loja = t?.loja_nome as string;
                terminalUuid = t?.terminal_uuid as string;
            }

            var corpo = System.Text.Json.Nodes.JsonNode.Parse(payload)?.AsObject();
            if (corpo is null) return (false, "payload do estorno sem aprovacao nao e' JSON: nao insiste");

            static string? Texto(System.Text.Json.Nodes.JsonObject o, string chave)
                => o.TryGetPropertyValue(chave, out var v)
                   && v is System.Text.Json.Nodes.JsonValue jv
                   && jv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

            // A client_key da FILA e' a verdade (e' por ela que o replay deduplica).
            corpo["client_key"] = clientKey;
            corpo["terminal_uuid"] = terminalUuid;
            corpo["store"] = Texto(corpo, "store") ?? loja;

            return await PostRestAsync("/rest/v1/pdv_estornos_sem_aprovacao?on_conflict=client_key",
                corpo.ToJsonString(), token, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { return (null, Corta(ex.Message)); }
    }

    private async Task<(bool? Ok, string? Erro)> EnviarKdsProntoAsync(string orderId, string token, CancellationToken ct)
    {
        try
        {
            var corpo = JsonSerializer.Serialize(new { _order_id = orderId });
            var (status, resp) = await RpcAsync("pdv_kds_pronto", corpo, token, ct).ConfigureAwait(false);
            if (status is >= 200 and < 300)
            {
                // "null" = o pedido nao existe na nuvem. Nao insiste: o ticket
                // NASCEU da nuvem, entao isso so acontece se alguem apagou a
                // linha - e re-tentar pra sempre nao a traz de volta.
                return (resp?.Trim() == "null" || string.IsNullOrWhiteSpace(resp?.Trim()))
                    ? (true, "pedido nao existe na nuvem; nada a marcar")
                    : (true, null);
            }
            // DesfechoDeStatus, como TODO handler daqui: rede caida/5xx/429 e
            // transitorio (aguarda), nao recusa. A primeira versao devolvia
            // false incondicional - 9 min de internet fora mandavam o aviso pro
            // dead-letter, e o dedup do Liberar impedia re-enfileirar. PRONTO
            // perdido em definitivo, com a fila existindo exatamente pra isso.
            return DesfechoDeStatus(status, resp);
        }
        catch (Exception ex) { return (null, "kds_pronto: " + ex.Message); }
    }

    private async Task<(int Status, string? Corpo)> RpcAsync(string nome, string corpoJson, string token, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(20_000);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_urlNuvem}/rest/v1/rpc/{nome}");
            req.Headers.TryAddWithoutValidation("apikey", Nuvem.AnonKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(corpoJson, Encoding.UTF8, "application/json");
            using var resp = await Fiscal.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var texto = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return ((int)resp.StatusCode, texto);
        }
        catch { return (0, null); }
    }

    /// <summary>
    /// Traduz um status HTTP de erro num desfecho da fila. PURA — não escreve nada
    /// (só o loop escreve no outbox, por id).
    ///  · 408/425/429 → transitório: são 4xx no número mas "tente depois" na semântica
    ///    (timeout de request, rate-limit). Tratá-los como recusa derrubava um flush
    ///    de backlog INTEIRO no dead-letter durante um rate-limit passageiro do
    ///    gateway — até 50 vendas desistidas de uma vez por um 429 de 10 minutos.
    ///  · demais 4xx → recusa PERMANENTE (payload/permissão/RPC sumida — não insiste);
    ///  · 5xx / 0 → transitório (rede/servidor doente: tenta na próxima varredura).
    /// Só chamar com status FORA de 2xx (sucesso é decidido pelo corpo, no chamador).
    /// </summary>
    internal static (bool? Ok, string? Erro) DesfechoDeStatus(int status, string? corpo)
    {
        var erro = status == 0 ? "sem resposta (rede/timeout)" : $"HTTP {status}: {Corta(corpo)}";
        if (status is 408 or 425 or 429) return (null, erro);              // 4xx transitórios
        return status is >= 400 and < 500 ? (false, erro) : (null, erro);  // 4xx permanente; 5xx/0 rede
    }

    /// <summary>Trecho curto e seguro para o ultimo_erro (corpo pode ser enorme ou nulo).</summary>
    private static string Corta(string? texto)
        => string.IsNullOrEmpty(texto) ? "" : texto.Length <= 160 ? texto : texto[..160];

    private async Task<string?> RestAsync(string caminho, string token, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(15_000);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_urlNuvem}{caminho}");
            req.Headers.TryAddWithoutValidation("apikey", Nuvem.AnonKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Fiscal.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode
                ? await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false)
                : null;
        }
        catch { return null; }
    }
}
