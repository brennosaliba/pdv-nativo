using System.Text.Json.Nodes;
using System.Threading.Channels;
using Pdv.Nucleo;

namespace Pdv;

/// <summary>Um aviso do chat novo para a tela de venda (seção 2.2). O texto já vem pronto.</summary>
/// <param name="Tipo">resgatou, aguardando, mandar, humano, expirou, validador, saiu_sem_premio, desfeito, falha_envio, pausado.</param>
/// <param name="TextoParaColar">A resposta ao cliente, quando a pessoa tem de mandar à mão (Abrir e colar, Copiar).</param>
public sealed record AvisoDoChat(string Tipo, string Texto, string? TextoParaColar, string? OrderUuid, string? Numero)
{
    /// <summary>
    /// 1.0.35 (revisão): o falha_envio pode ganhar o "Resgatar aqui"? Só a pergunta do sabor que não saiu
    /// (o código ainda não foi usado). Null: o ERP não disse (o ERP antigo, ou o aviso de outro tipo).
    /// </summary>
    public bool? Resgatavel { get; init; }
}

/// <summary>O que o script de envio contou: mandou, ou o erro do contrato (sdk_ausente, canal_errado...).</summary>
/// <param name="Detalhe">
/// 1.0.35 (caso #8039): o motivo da recusa que a página mandou, já pela lista branca de
/// <see cref="ConversaRaspadinha.DetalheDoErro"/> (etapa, código e mensagem do erro do Sendbird, conexão,
/// contagens). Nunca texto, canal, pedido, usuário nem token.
/// </param>
public sealed record ResultadoDoScript(bool Ok, string? Erro, string? MsgId, JsonObject? Detalhe = null);

/// <summary>
/// O que a tela do chat oferece ao serviço. O serviço não encosta no WebView2: tudo que roda na
/// página passa por estas quatro portas, e a de envio só aceita uma <see cref="PermissaoDeEnvio"/>,
/// que só o portão cria.
/// </summary>
public sealed class PonteDoChat
{
    public required Func<PermissaoDeEnvio, Task<ResultadoDoScript>> EnviarPeloSdk { get; init; }
    /// <summary>(uuid do pedido, número curto, texto) → colou na conversa certa?</summary>
    public required Func<string, string?, string, Task<bool>> AbrirEColar { get; init; }
    /// <summary>(canais para conferir, id da loja no WebSocket) → contagens do SDK, ou null.</summary>
    public required Func<IReadOnlyList<string>, string?, Task<JsonObject?>> DiagSdk { get; init; }
    /// <summary>"logado", "login" ou "ausente".</summary>
    public required Func<string> Gestor { get; init; }
    /// <summary>
    /// 1.0.25: (numero do pedido, uuid) → abre a lista de conversas do Gestor escondido e, com o
    /// numero, a conversa do pedido. E o que AQUECE o SDK do Sendbird: com o Gestor recem-carregado
    /// e nenhuma conversa aberta, getChannel nao acha nada e todo envio cai em canal_errado ate
    /// alguem tocar numa conversa (12:27 e 17:15 de 08/10). Opcional: a bateria nao precisa dela.
    /// </summary>
    public Func<string?, string?, Task<bool>>? AquecerConversa { get; init; }
    /// <summary>1.0.33: a saude da pagina (viva, navegacao, fase da vigia) para o sinal. Opcional.</summary>
    public Func<ConversaRaspadinha.SaudeDoChat>? Saude { get; init; }
    /// <summary>
    /// 1.0.35 (caso #8039), o PLANO B: (numero do pedido, uuid) → a tela recarrega o Gestor ESCONDIDO
    /// (conexão nova do SDK), espera o SDK voltar aberto e aquece a conversa do pedido. Devolve
    /// recarregou, na_frente, login, teto, sem_sdk ou erro. Nunca recarrega com o Gestor na frente do
    /// operador. Opcional: sem ela, o envio é o de hoje.
    /// </summary>
    public Func<string?, string?, Task<string>>? RecarregarParaEnvio { get; init; }
}

/// <summary>
/// 1.0.35: as portas do laço de envio (<see cref="ServicoConversaChat.EnviarComTentativasAsync"/>). O
/// serviço liga nas de verdade (o portão, a confirmação pelo quadro, o sinal, a ponte da tela e o
/// relato ao ERP); a bateria liga nas falsas, com relógio e espera de mentira.
/// </summary>
public sealed class LacoDeEnvio
{
    public required PortaoDeEnvio Portao { get; init; }
    public required ConfirmacaoDeEnvio Confirmacoes { get; init; }
    /// <summary>O último sinal e quando chegou.</summary>
    public required Func<(SinalConversa Sinal, DateTime? Em)> Sinal { get; init; }
    public required Func<PonteDoChat?> Ponte { get; init; }
    /// <summary>(saida_id, resultado, erro, saída, detalhe) → conta ao ERP.</summary>
    public required Func<long, string, string?, SaidaConversa?, JsonObject?, Task> Relatar { get; init; }
    public Func<TimeSpan, Task> Esperar { get; init; } = t => Task.Delay(t);
    public Func<DateTime> Agora { get; init; } = () => DateTime.Now;
    public Action<string> Diag { get; init; } = _ => { };
    /// <summary>A versão do caixa que vai no detalhe do erro.</summary>
    public string? Versao { get; init; }
    /// <summary>Quanto o laço espera a recarga do plano B.</summary>
    public TimeSpan PrazoDaRecarga { get; init; } = PlanoBDoEnvio.PrazoDaRecarga;
    /// <summary>A recarga do plano B aconteceu (conta para o sinal).</summary>
    public Action? Recarregou { get; init; }
}

/// <summary>
/// RESGATE PELO CHAT DO iFOOD, O SERVIÇO VIVO DO CAIXA (07/10/2026, desenho "resgate-final",
/// tarefa P7). O caixa lê o chat e EXECUTA o que o ERP decide:
///
///  1. a tela do chat entrega cada quadro do WebSocket (<see cref="Quadro"/>);
///  2. a triagem (ConversaRaspadinha.Triagem) separa a fala que importa: conversa de pedido desta
///     loja, cliente (ou a loja numa conversa viva), de agora, que não é eco;
///  3. a fala é gravada e enfileirada na mesma transação, e as falas do mesmo canal são juntadas
///     por até 6 s numa chamada só (<c>chat_mensagens</c>);
///  4. a resposta diz o que fazer: imprimir a comanda (só se <c>comanda.imprimir_aqui</c>), mandar
///     a resposta pelo SDK (Automático), mostrar a resposta para a pessoa colar (Assistido), avisar;
///  5. o sinal de 60 s (<c>chat_sinal</c>) leva o diagnóstico (só contagens) e traz o modo da
///     loja, os avisos dos outros terminais, as saídas e as comandas reservadas para este.
///
/// TUDO NASCE DESLIGADO. Sem linha da loja no ERP, o sinal volta "desligado" e o caixa só manda o
/// próprio sinal. Nada fala com o cliente sem passar pelo <see cref="PortaoDeEnvio"/>.
///
/// 1.0.20, A APROVAÇÃO PELO WHATSAPP DO DONO (desenho de 07/10, seções 4.5 e 4.8): a resposta nasce
/// como proposta para o dono (<c>como='dono'</c>), e o caixa não faz nada com ela. Aprovada, ela
/// chega pelo sinal (30 s) como "sdk" reservada a este terminal, com <c>pelo_dono</c>, e sai pelo
/// mesmo portão e pela mesma confirmação pelo eco. A do dono nunca vira texto para colar, nem quando
/// falha. Com a aprovação valendo para todos (<c>aprovacao='dono'</c>) o caixa fica mudo para o chat:
/// sem som, sem aviso e sem "Abrir e colar". A comanda de resgate continua saindo.
///
/// Nunca lança: chat não derruba caixa.
/// </summary>
public static class ServicoConversaChat
{
    /// <summary>Um aviso para a tela de venda (toast). Pode vir de qualquer thread.</summary>
    public static event Action<AvisoDoChat>? Avisou;

    private static PonteDoChat? _ponte;
    private static int _iniciado;

    private static readonly ChatCaptura.ContadoresSendbird Contadores = new();
    private static readonly CacheDeCanais Canais = new();
    private static readonly MemoriaDeEco Eco = new();
    private static readonly LimiteDeEnvio Limite = new();
    private static readonly PortaoDeEnvio Portao = new(Eco, Limite);
    private static readonly Agrupador Lotes = new();
    private static readonly ConfirmacaoDeEnvio Confirmacoes = new();
    private static readonly Channel<(SaidaConversa Saida, DateTime Recebida)> FilaDeEnvio =
        Channel.CreateUnbounded<(SaidaConversa, DateTime)>(new UnboundedChannelOptions { SingleReader = true });
    private static readonly SemaphoreSlim UmaChamadaPorVez = new(1, 1);
    private static readonly SemaphoreSlim UmSinalPorVez = new(1, 1);

    private static readonly object TravaVistas = new();
    private static readonly HashSet<string> Vistas = new(StringComparer.Ordinal);
    private static readonly Queue<string> VistasOrdem = new();
    private const int TetoDeVistas = 2000;

    /// <summary>O último sinal e quando chegou, trocados juntos (lidos na thread da tela e na do envio).</summary>
    private sealed record UltimoSinal(SinalConversa Sinal, DateTime? Em);
    private static volatile UltimoSinal _ultimo = new(SinalConversa.Desligado, null);
    private static string? _sdkUserId;
    private static string? _wsUserId;
    private static DateTime _proximaFaxina = DateTime.MinValue;

    private static System.Threading.Timer? _tique;
    private static System.Threading.Timer? _relogioSinal;
    private static readonly System.Diagnostics.Stopwatch Relogio = System.Diagnostics.Stopwatch.StartNew();

    private static string Terminal => ConversaRaspadinha.NomeDoTerminal();

    private static string Versao
    {
        get
        {
            var v = typeof(ServicoConversaChat).Assembly.GetName().Version;
            return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>O último sinal lido (para a tela e para os testes de fumaça).</summary>
    public static SinalConversa Sinal => _ultimo.Sinal;

    /// <summary>
    /// 1.0.35: as contagens dos cartões da solicitação do cliente, para o sinal. Quem registra os
    /// cartões é o leitor do arquivo do chat, que liga esta porta no primeiro quadro.
    /// </summary>
    public static Func<ConversaRaspadinha.ContagemDeCartoes>? Cartoes { get; set; }

    /// <summary>
    /// Liga o serviço com a ponte da tela do chat. Idempotente: a ponte é trocada quando a tela é
    /// recriada (WebView2 que caiu), e os relógios nascem uma vez só.
    /// </summary>
    public static void Ligar(PonteDoChat ponte)
    {
        _ponte = ponte;
        if (Interlocked.Exchange(ref _iniciado, 1) == 1) return;
        try
        {
            using var cx = Banco.Abrir();
            var (s, em) = ConversaRaspadinha.SinalGravado(cx);
            _ultimo = new UltimoSinal(s, em);
        }
        catch { _ultimo = new UltimoSinal(SinalConversa.Desligado, null); }
        ConversaRaspadinha.ResolvidaNaFila += r => Seguro(() => Processar(r, null));
        _tique = new System.Threading.Timer(_ => Tique(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _relogioSinal = new System.Threading.Timer(_ => _ = SinalAsync(), null, TimeSpan.FromSeconds(8), Timeout.InfiniteTimeSpan);
        _ = Task.Run(LoopDeEnvioAsync);
        Diag("ligado");
    }

    // ── 1. O QUADRO QUE CHEGA ────────────────────────────────────────────────

    /// <summary>
    /// Um quadro do WebSocket capturado pelo CDP. Chamado na thread da tela, então é barato: conta
    /// para o diagnóstico, confere se é a volta de uma resposta que este caixa mandou, faz a triagem
    /// e passa a fala adiante para o banco fora da tela. Só o quadro RECEBIDO vira fala; o que sai da
    /// página serve só para confirmar envio.
    /// </summary>
    public static void Quadro(string? payload, bool enviado, string? conexao, string? wsUserId)
    {
        try
        {
            if (string.IsNullOrEmpty(payload) || _iniciado == 0) return;
            var agora = DateTime.Now;
            if (!enviado) Contadores.Registrar(payload, wsUserId, agora);
            var cmd = QuadroSendbird.Comando(payload);
            if (cmd != QuadroSendbird.ComandoMensagem)
            {
                // 1.0.38 (caso #0434): a observação que o cliente escreveu no cartão do iFood (BRDM/AEDI)
                // vira uma fala sintética do cliente; o resto do quadro de controle não interessa ao robô
                if (!enviado && cmd is "BRDM" or "AEDI") Observacao(payload!, wsUserId, agora);
                return;
            }
            var m = QuadroSendbird.Ler(payload);
            if (m is null) return;
            // 1.0.26: o id da loja so vem da conexao do chat de CLIENTES (canal de pedido
            // sendbird_gc_cm_); a do entregador tem outro usuario e trocava o _wsUserId
            if (!string.IsNullOrWhiteSpace(wsUserId) && CanalIfood.Ler(m.Canal) is not null) _wsUserId = wsUserId;

            if (Confirmacoes.Ver(m, enviado) is { } conf)
                _ = RelatarAsync(conf.SaidaId, "enviada", null, conf.MsgId, null);
            if (enviado) return;

            var t = ConversaRaspadinha.Triagem(m, _ultimo.Sinal, Canais, Eco, wsUserId ?? _wsUserId, _sdkUserId, agora);
            if (!t.Entra) return;
            // 1.0.38: guarda o que o cliente escreve, para a observação do cartão não repetir o mesmo texto
            if (t.Fala!.Lado == LadoFala.Cliente) FalasCliente.Registrar(t.Canal!.Canal, t.Fala.Texto, agora);
            if (!Nova(t.Fala!.Chave)) return;
            var canal = t.Canal!; var fala = t.Fala!;
            _ = Task.Run(() => Guardar(canal, fala, agora));
        }
        catch (Exception ex) { Diag("quadro: " + ex.GetType().Name); }
    }

    // ── 1.0.38 (caso #0434): a observação do cartão vira fala sintética do cliente ──
    private static readonly MemoriaDeFalaCliente FalasCliente = new();
    private static int _obsMandadas, _obsPuladas;

    /// <summary>Quantas observações do cartão o robô mandou e quantas pulou (para o sinal).</summary>
    public static (int Mandadas, int Puladas) Observacoes => (Volatile.Read(ref _obsMandadas), Volatile.Read(ref _obsPuladas));

    /// <summary>
    /// A observação que o cliente escreveu num cartão do iFood (o texto fica no campo data, não no MESG).
    /// Vira uma fala sintética do cliente que passa pela MESMA triagem; a chave é a sol:, igual em todos os
    /// terminais. Não manda se o cliente já tinha escrito o mesmo texto numa fala comum (guarda de 10 min);
    /// a mesma observação duas vezes é barrada pela chave. Quem acha o código e decide é o ERP.
    /// </summary>
    private static void Observacao(string payload, string? wsUserId, DateTime agora)
    {
        var obs = QuadroSendbird.ObservacaoNoCartao(payload);
        if (obs is null) return;
        var ms = obs.CriadoEmMs ?? new DateTimeOffset(agora).ToUnixTimeMilliseconds();
        var m = new MensagemSendbird(QuadroSendbird.ComandoMensagem, obs.Canal, null, null, null, "CUSTOMER", obs.Texto, ms, false);
        var t = ConversaRaspadinha.Triagem(m, _ultimo.Sinal, Canais, Eco, wsUserId ?? _wsUserId, _sdkUserId, agora);
        if (!t.Entra) return;
        if (FalasCliente.Contem(obs.Canal, obs.Texto, agora)) { Interlocked.Increment(ref _obsPuladas); return; }
        var fala = t.Fala! with { Chave = obs.Chave };
        if (!Nova(fala.Chave)) return;
        Interlocked.Increment(ref _obsMandadas);
        var canal = t.Canal!;
        Diag($"observacao origem={obs.Origem} chave={Curta(fala.Chave)} pedido={Curta(canal.OrderId)} texto=(len {fala.Texto.Length})");
        _ = Task.Run(() => Guardar(canal, fala, agora));
    }

    private static bool Nova(string chave)
    {
        lock (TravaVistas)
        {
            if (!Vistas.Add(chave)) return false;
            VistasOrdem.Enqueue(chave);
            while (VistasOrdem.Count > TetoDeVistas) Vistas.Remove(VistasOrdem.Dequeue());
            return true;
        }
    }

    private static void Guardar(CanalDaFala canal, FalaConversa fala, DateTime agora)
    {
        try
        {
            using (var cx = Banco.Abrir())
            {
                if (!ConversaRaspadinha.Registrar(cx, canal, fala, agora)) return;
            }
            // o rastro diz O QUE aconteceu, nunca o que a pessoa escreveu
            Diag($"fala {fala.Lado} chave={Curta(fala.Chave)} canal={Curta(canal.OrderId)} texto=(len {fala.Texto.Length})");
            if (Lotes.Adicionar(canal.Canal, fala.Chave, agora)) _ = DescarregarAsync();
        }
        catch (Exception ex) { Diag("guardar: " + ex.GetType().Name); }
    }

    // ── 2. O RELÓGIO DE 1 s: lotes prontos e confirmações vencidas ───────────

    private static int _noTique;

    private static void Tique()
    {
        if (Interlocked.Exchange(ref _noTique, 1) == 1) return;
        try
        {
            var agora = DateTime.Now;
            foreach (var (id, vistoSaindo) in Confirmacoes.Vencidas(agora))
                _ = RelatarAsync(id, vistoSaindo ? "enviada" : "incerta", vistoSaindo ? null : "sem_confirmacao", null, null);
            _ = DescarregarAsync();
        }
        catch { }
        finally { Interlocked.Exchange(ref _noTique, 0); }
    }

    private static async Task DescarregarAsync()
    {
        try
        {
            foreach (var (canal, chaves) in Lotes.Prontos(DateTime.Now))
                await EnviarLoteAsync(canal, chaves).ConfigureAwait(false);
        }
        catch (Exception ex) { Diag("descarregar: " + ex.GetType().Name); }
    }

    // ── 3. A CHAMADA chat_mensagens ──────────────────────────────────────────

    private static async Task EnviarLoteAsync(string canal, IReadOnlyList<string> chaves)
    {
        await UmaChamadaPorVez.WaitAsync().ConfigureAwait(false);
        try
        {
            string corpo; IReadOnlyList<string> incluidas;
            using (var cx = Banco.Abrir())
            {
                var (c, falas) = ConversaRaspadinha.Pendentes(cx, canal, chaves.ToList());
                if (c is null || falas.Count == 0) return;
                (corpo, incluidas) = ConversaRaspadinha.CorpoMensagens(Terminal, c, falas);
            }
            var (st, resp) = await Servicos.Nuvem().FuncaoAsync(ConversaRaspadinha.Edge, corpo, ConversaRaspadinha.Prazo)
                .ConfigureAwait(false);
            var agora = DateTime.Now;
            var r = ConversaRaspadinha.LerResposta(st, resp, agora);
            using (var cx = Banco.Abrir()) ConversaRaspadinha.Aplicar(cx, incluidas.ToList(), r, agora);
            Diag($"lote {incluidas.Count} fala(s) canal={Curta(canal)} http={st} status={r.Status} modo={r.Modo} "
                 + $"acao={r.Acao} motivo={r.Motivo ?? r.MotivoCru ?? "-"} repetida={r.Repetida} saidas={r.Saidas.Count} "
                 + $"bonus={(r.Bonus is null ? "-" : Curta(r.Bonus.Id))}");
            // fala que não coube no corpo continua pendente e volta para o próximo lote
            foreach (var k in chaves.Except(incluidas)) Lotes.Adicionar(canal, k, agora);
            if (r.Status == StatusConversa.Ok) Processar(r, canal);
        }
        catch (Exception ex) { Diag("lote: " + ex.GetType().Name); }
        finally { UmaChamadaPorVez.Release(); }
    }

    /// <summary>
    /// Executa o que o ERP decidiu: papel, envio, aviso. Vale para a resposta da tela e para a que a
    /// fila resolveu sozinha (<see cref="ConversaRaspadinha.ResolvidaNaFila"/>).
    /// </summary>
    private static void Processar(RespostaConversa r, string? canal)
    {
        var agora = DateTime.Now;
        canal ??= r.Saidas.Select(s => s.Canal).FirstOrDefault(c => c is not null);
        if (canal is not null && r.Conversa?.Estado is { } est && est is not ("sombra" or "aberta" or "expirada" or "desfeito"))
            Canais.MarcarEmConversa(canal, agora);

        if (r.Bonus is not null)
        {
            // a comanda continua saindo com o caixa mudo (D10: 'ambos' = cozinha e caixa)
            if (r.ImprimirAqui) _ = ServicoRaspadinhaChat.ImprimirPendentesAsync();
            // 1.0.20: com a aprovação do dono valendo para todos, o caixa não toca o som do resgate
            if (!r.Repetida && r.Acao == "resgatou" && !_ultimo.Sinal.CaixaMudo) Alerta.Resgate();
        }

        var mandarDoErp = r.Aviso is { Tipo: "mandar" } a0 ? a0.Texto : null;
        var mandou = false;
        foreach (var s in r.Saidas) mandou |= TratarSaida(s, agora, mandarDoErp);
        // a resposta que espera o OK do dono não vira "mande esta resposta" na tela, nem sem o texto
        var doDono = r.Saidas.Any(s => s.PeloDono || s.Como == ConversaRaspadinha.ComoDono);
        // revisão 07/10: o aviso da resposta passa pela trava do mudo. Com aprovacao=dono o ERP não
        // devolve aviso na resposta; quando devolve, a aprovação foi desligada depois do último sinal
        // deste caixa, ou o WhatsApp do dono está fora: o sinal seguinte não traz este aviso de novo.
        if (r.Aviso is { } a && !(a.Tipo == "mandar" && (mandou || doDono)))
            Emitir(new AvisoDoChat(a.Tipo, a.Texto, null, a.IfoodOrderId, a.PedidoNumero) { Resgatavel = a.Resgatavel }, doErp: true);
    }

    /// <summary>
    /// Uma saída, pela regra pura do núcleo (<see cref="ConversaRaspadinha.Destino"/>): "sdk"
    /// reservada vai para a fila de envio (é por ali que sai a aprovada pelo dono, pelo portão e com
    /// a confirmação pelo eco); "operador" vira aviso com o texto; a do dono, a sombra e qualquer
    /// coisa com o caixa mudo ficam quietas.
    /// </summary>
    private static bool TratarSaida(SaidaConversa s, DateTime agora, string? textoDoErp)
    {
        switch (ConversaRaspadinha.Destino(s, _ultimo.Sinal))
        {
            case DestinoDaSaida.Enviar:
                if (s.PeloDono) LembrarDoDono(s.Id, agora);
                FilaDeEnvio.Writer.TryWrite((s, agora));
                return false;
            case DestinoDaSaida.Colar:
                Emitir(new AvisoDoChat("mandar", textoDoErp ?? ConversaRaspadinha.TextoMandar(s.PedidoNumero),
                    s.Texto, s.IfoodOrderId, s.PedidoNumero), doErp: true);
                return true;
            default:
                if (s.PeloDono || s.Como == ConversaRaspadinha.ComoDono || s.Como == ConversaRaspadinha.ComoSdk)
                    Diag($"saida={s.Id} como={s.Como} estado={s.Estado} dono={s.PeloDono}: o caixa nao mexe");
                return false;
        }
    }

    /// <summary>
    /// As saídas do dono que passaram por este caixa (2 h). O relato de uma delas que chega sem a
    /// saída na mão (a confirmação pelo quadro) também não pode virar texto para colar.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, DateTime> SaidasDoDono = new();

    private static void LembrarDoDono(long id, DateTime agora)
    {
        if (SaidasDoDono.Count > 2000)
            foreach (var k in SaidasDoDono.Where(x => x.Value < agora.AddHours(-2)).Select(x => x.Key).ToList())
                SaidasDoDono.TryRemove(k, out _);
        SaidasDoDono[id] = agora;
    }

    private static bool EraDoDono(long id) => SaidasDoDono.ContainsKey(id);

    private static void Emitir(AvisoDoChat a, bool doErp = false)
    {
        // 1.0.20 (D10): com a aprovação do dono valendo para todos, o caixa fica fora do chat. Os
        // avisos vão ao WhatsApp do dono pelo ERP; aqui não toca som nem aparece nada.
        // Revisão 07/10: o que o ERP mandou na resposta da fala (doErp) passa: com o dono ele não
        // manda, e quando manda é porque a aprovação foi desligada ou o WhatsApp do dono caiu.
        // 1.0.33: o chat_fora/chat_voltou da vigia do ERP (SQL 171) é sobre o próprio caixa: passa sempre
        if (_ultimo.Sinal.CaixaMudo && !doErp && a.Tipo is not ("chat_fora" or "chat_voltou"))
        { Diag($"aviso {a.Tipo} calado (aprovacao do dono)"); return; }
        // ⚠️ Regra da casa: nada com travessão chega na tela, nem se vier do ERP.
        if (!ConversaRaspadinha.TextoLimpo(a.Texto, 160)) { Diag($"aviso {a.Tipo} recusado (texto fora da regra)"); return; }
        if (a.TextoParaColar is not null && !ConversaRaspadinha.TextoLimpo(a.TextoParaColar, ConversaRaspadinha.TetoTexto))
            a = a with { TextoParaColar = null };
        if (a.Tipo is "mandar" or "falha_envio" or "humano" or "solicitacao") Alerta.MensagemChat();
        Diag($"aviso {a.Tipo} pedido={a.Numero ?? "-"}");
        try { Avisou?.Invoke(a); } catch { }
    }

    /// <summary>
    /// 1.0.24 (08/10/2026): um aviso que nasce NESTE caixa, sem passar pelo ERP. Hoje: a
    /// "solicitação de alteração" do iFood (cancelar, trocar item, observação), que chega como
    /// cartão do sistema, não como texto, e tem 5 minutos para a loja responder no Gestor. O
    /// leitor da raspadinha não a enxergava e o dono achou que o bot "não respondeu". Passa
    /// pelo mesmo filtro dos outros avisos (texto limpo, caixa mudo).
    /// </summary>
    public static void AvisoLocal(string tipo, string texto, string? orderUuid, string? numero)
        => Seguro(() => Emitir(new AvisoDoChat(tipo, texto, null, orderUuid, numero)));

    // ── 4. O ENVIO (um por vez, 3 s entre mensagens) ─────────────────────────

    private static async Task LoopDeEnvioAsync()
    {
        await foreach (var (s, recebida) in FilaDeEnvio.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { await EnviarUmaAsync(s, recebida).ConfigureAwait(false); }
            catch (Exception ex) { Diag("envio: " + ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
    }

    /// <summary>Quantas vezes o envio frio tenta antes de dizer "falhou" (1.0.24), e o respiro entre elas.
    /// 1.0.25: o respiro caiu para 6 s porque antes dele o caixa aquece o SDK abrindo a conversa; com 15 s
    /// as tres tentativas estouravam o prazo da reserva (50 s) e a terceira nem acontecia.</summary>
    public const int TentativasDeEnvio = 3;
    public static readonly TimeSpan RespiroEntreTentativas = TimeSpan.FromSeconds(6);
    /// <summary>1.0.25: o aquecimento do SDK pelo sinal, no maximo a cada 2 min.</summary>
    public static readonly TimeSpan IntervaloDoAquecimento = TimeSpan.FromMinutes(2);
    private static DateTime _ultimoAquecimento = DateTime.MinValue;

    private static Task EnviarUmaAsync(SaidaConversa s, DateTime recebida)
        => EnviarComTentativasAsync(s, recebida, new LacoDeEnvio
        {
            Portao = Portao,
            Confirmacoes = Confirmacoes,
            Sinal = () => { var u = _ultimo; return (u.Sinal, u.Em); },
            Ponte = () => _ponte,
            Relatar = (id, resultado, erro, saida, detalhe) => RelatarAsync(id, resultado, erro, null, saida, detalhe),
            Diag = Diag,
            Versao = Versao,
            Recarregou = () => ContarRecarga(DateTime.Now),
        });

    /// <summary>
    /// O LAÇO DO ENVIO de uma saída "sdk" reservada a este terminal. Público para a bateria, que liga
    /// as portas falsas (<see cref="LacoDeEnvio"/>); o serviço liga as de verdade em EnviarUmaAsync.
    ///
    /// 1.0.24 (08/10/2026): com o Gestor recem-carregado e nenhuma conversa aberta, o script
    /// devolve canal_errado ou erro_envio de primeira (SDK frio). Antes, uma falha ja virava
    /// "falhou": 5 seguidas as 12:27 pausaram as mensagens do pedido e a resposta de um
    /// resgate foi para o "Abrir e colar" que ninguem tocou. Agora o envio tenta de novo, com
    /// o portao decidido de novo (token novo) e 15 s de respiro, ate 3 vezes; so entao falha.
    /// 1.0.25 (08/10/2026, 17:15): a nova tentativa passava de novo pelo portao, e o eco que a
    /// PRIMEIRA decisao registrou barrava a segunda como repetida: toda mensagem do pedido virou
    /// "incerta/eco" sem sair. Agora so a primeira tentativa decide; as seguintes repetem a mesma
    /// permissao com token novo (Portao.Repetir), e antes de cada uma o caixa aquece o SDK
    /// abrindo a conversa do pedido no Gestor escondido.
    /// 1.0.35 (14/10/2026, caso #8039): o rastro guarda o motivo de cada recusa e vai ao ERP no
    /// relato. E o PLANO B: na primeira recusa do SDK (erro_envio de falhou_sdk ou promessa, sem quadro
    /// saindo), a tela recarrega o Gestor escondido (conexão nova) e o laço ganha UMA tentativa a mais,
    /// com a mesma permissão (Portao.Repetir) e dentro do prazo da reserva. Gestor na frente, login, teto
    /// ou SDK que não volta: segue o laço de hoje.
    /// 1.0.35 (revisão adversarial): A RESERVA DENTRO DO LAÇO. Uma saída que já foi tentada e com certeza
    /// não saiu (recusa sem quadro) nunca vira "incerta": quando a reserva vence entre tentativas, ou não
    /// sobra tempo para outra (aquecer, respirar e tentar), o caixa relata "falhou" na hora e ela vai para
    /// a pessoa. Depois da recarga, o SDK chega frio (nenhuma conversa aberta) e canal_errado ou
    /// sdk_ausente não encerram: o laço aquece, respira e tenta de novo; se nada sair, o relato é
    /// erro_envio (a recusa que começou tudo), nunca canal_errado, que pausaria o robô da loja inteira.
    /// </summary>
    public static async Task EnviarComTentativasAsync(SaidaConversa s, DateTime recebida, LacoDeEnvio l)
    {
        string? erro = null;
        PermissaoDeEnvio? primeira = null;
        var rastro = new RastroDoEnvio(l.Versao);
        var planoBFeito = false;
        var limite = TentativasDeEnvio;
        int? depoisDaRecarga = null;    // a tentativa logo depois da recarga do plano B
        var segDaRecarga = 0d;
        for (var tentativa = 1; tentativa <= limite; tentativa++)
        {
            var (sinal, sinalEm) = l.Sinal();
            PermissaoDeEnvio? permissao;
            MotivoPortao motivo;
            if (tentativa == 1)
            {
                (permissao, motivo) = l.Portao.Decidir(s, sinal, sinalEm, recebida, l.Agora());
                primeira = permissao;
            }
            else
            {
                permissao = primeira is null ? null : l.Portao.Repetir(primeira, recebida, l.Agora());
                motivo = permissao is null ? MotivoPortao.ReservaVencida : MotivoPortao.Liberado;
            }
            l.Diag($"saida={s.Id} etapa={s.Etapa} portao={motivo}{(tentativa > 1 ? " tentativa=" + tentativa : "")}");
            if (permissao is null)
            {
                if (tentativa == 1)
                {
                    if (PortaoDeEnvio.Relato(motivo) is { } rel)
                        await l.Relatar(s.Id, rel.Resultado, rel.Erro, s, rastro.Detalhe()).ConfigureAwait(false);
                    return;
                }
                // a reserva venceu DEPOIS de tentativas que com certeza não saíram: vai para a pessoa
                if (tentativa == depoisDaRecarga)
                {
                    rastro.PlanoB = PlanoBDoEnvio.SemTempo;
                    l.Diag($"saida={s.Id} plano b=recarregou em {segDaRecarga:0} s, tentativa {tentativa}: sem tempo (a reserva venceu), falhou");
                }
                else l.Diag($"saida={s.Id} tentativa {tentativa}: sem tempo (a reserva venceu), falhou");
                await l.Relatar(s.Id, "falhou", "erro_envio", s, rastro.Detalhe()).ConfigureAwait(false);
                return;
            }
            var ponte = l.Ponte();
            if (ponte is null)
            {
                await l.Relatar(s.Id, "falhou", "sdk_ausente", s, null).ConfigureAwait(false);
                return;
            }
            var vez = await TentarAsync(s, permissao, ponte, tentativa, rastro, l).ConfigureAwait(false);
            if (tentativa == depoisDaRecarga)
                l.Diag($"saida={s.Id} plano b=recarregou em {segDaRecarga:0} s, tentativa {tentativa}: "
                    + (vez.Desfecho == DesfechoDaTentativa.Saiu ? "ok" : vez.Erro ?? vez.Desfecho.ToString().ToLowerInvariant()));
            if (vez.Desfecho != DesfechoDaTentativa.Falhou) return;
            erro = vez.Erro;
            // depois da recarga, o SDK que recusa de novo (ou congelada, sem_cliente) encerra; só o frio segue
            if (depoisDaRecarga is not null && erro is not ("canal_errado" or "sdk_ausente")) break;
            // 1.0.35: o plano B, uma vez por saída, na primeira recusa que pede conexão nova
            if (!planoBFeito && PlanoBDoEnvio.Cabe(erro, vez.Detalhe) && ponte.RecarregarParaEnvio is { } recarregar && primeira is not null)
            {
                planoBFeito = true;
                var (fim, seg) = await PlanoBAsync(s, primeira, recarregar, rastro, l).ConfigureAwait(false);
                if (fim == FimDoPlanoB.Encerrou) return;
                if (fim == FimDoPlanoB.Recarregou)
                {
                    // conexão nova: a próxima tentativa é já, sem aquecer (a tela aqueceu) e sem respiro
                    depoisDaRecarga = tentativa + 1;
                    segDaRecarga = seg;
                    limite = TentativasDeEnvio + 1;
                    continue;
                }
            }
            // congelada e sem_cliente nao mudam com o tempo; o resto e o SDK frio: aquece, respira e tenta de novo
            if (erro is "congelada" or "sem_cliente" || tentativa >= limite) break;
            if (!HaTempoParaOutraTentativa(recebida, l.Agora()))
            {
                l.Diag($"saida={s.Id} erro={erro}: sem tempo para a tentativa {tentativa + 1} (reserva de {PortaoDeEnvio.PrazoDaReserva.TotalSeconds:0} s), falhou");
                await l.Relatar(s.Id, "falhou", "erro_envio", s, rastro.Detalhe()).ConfigureAwait(false);
                return;
            }
            var aqueceu = await AquecerAsync(ponte, s.PedidoNumero, s.IfoodOrderId).ConfigureAwait(false);
            rastro.Aqueceu |= aqueceu;
            l.Diag($"saida={s.Id} erro={erro}: aqueceu={aqueceu}, tenta de novo em {RespiroEntreTentativas.TotalSeconds:0} s");
            await l.Esperar(RespiroEntreTentativas).ConfigureAwait(false);
        }
        await l.Relatar(s.Id, "falhou", ErroDoRelato(erro, depoisDaRecarga is not null), s, rastro.Detalhe()).ConfigureAwait(false);
    }

    /// <summary>
    /// 1.0.35 (revisão): o mínimo que outra tentativa pede além do respiro (aquecer e o script responder).
    /// Sem isso sobrando na reserva, o laço relata "falhou" na hora em vez de esperar à toa e segurar a fila.
    /// </summary>
    public static readonly TimeSpan FolgaDaTentativa = TimeSpan.FromSeconds(2);

    /// <summary>Ainda cabe aquecer, respirar e tentar dentro da reserva desta saída?</summary>
    public static bool HaTempoParaOutraTentativa(DateTime recebida, DateTime agora)
        => agora - recebida + RespiroEntreTentativas + FolgaDaTentativa <= PortaoDeEnvio.PrazoDaReserva;

    /// <summary>
    /// O erro do relato quando as tentativas acabaram. Depois da recarga do plano B, canal_errado e
    /// sdk_ausente são o SDK frio que a própria recarga causou: o relato fica erro_envio (a recusa que
    /// começou tudo, que o alarme do ERP conta), porque canal_errado pausa o robô da loja.
    /// </summary>
    public static string ErroDoRelato(string? erro, bool recarregou)
        => recarregou && erro is "canal_errado" or "sdk_ausente" ? "erro_envio" : erro ?? "erro_envio";

    /// <summary>Como terminou uma tentativa: só "Falhou" deixa o laço seguir.</summary>
    public enum DesfechoDaTentativa
    {
        /// <summary>O script disse que mandou: quem confirma é o quadro.</summary>
        Saiu,
        /// <summary>O script não respondeu (pode ter saído): o quadro decide, e sem ele vira "incerta".</summary>
        SemResposta,
        /// <summary>O script disse que não, mas o quadro já tinha confirmado: vale o quadro.</summary>
        JaConfirmada,
        /// <summary>O script disse que não e o quadro foi visto saindo: "incerta", nunca de novo.</summary>
        Incerta,
        /// <summary>O script disse que não e nenhum quadro passou: pode tentar de novo.</summary>
        Falhou,
    }

    /// <summary>
    /// UMA tentativa: a espera pelo quadro nasce ANTES do script, o script manda, o rastro guarda o motivo
    /// e a confirmação decide. A confirmação é PELO QUADRO (seção 0, decisão 9): o SDK só responde depois
    /// que o servidor devolve a mensagem, e a essa altura o quadro que saiu e a volta com o msg_id já
    /// passaram pelo CDP. Esperando só depois, nada nunca casava e toda resposta mandada virava "incerta".
    /// </summary>
    private static async Task<(DesfechoDaTentativa Desfecho, string? Erro, JsonObject? Detalhe)> TentarAsync(
        SaidaConversa s, PermissaoDeEnvio permissao, PonteDoChat ponte, int tentativa, RastroDoEnvio rastro, LacoDeEnvio l)
    {
        l.Confirmacoes.Aguardar(s.Id, permissao.Canal, permissao.Texto, l.Agora());
        ResultadoDoScript res;
        try { res = await ponte.EnviarPeloSdk(permissao).ConfigureAwait(false); }
        catch { res = new ResultadoDoScript(false, "sem_resposta", null); }
        rastro.Registrar(tentativa, res.Detalhe, res.Erro);
        l.Diag($"saida={s.Id} script ok={res.Ok} erro={res.Erro ?? "-"}{ConversaRaspadinha.LinhaDoDetalhe(res.Detalhe)}");
        // Mandou, ou não respondeu (pode ter saído): quem decide é o quadro, em até 15 s. Sem ele,
        // "incerta" (ConfirmacaoDeEnvio.Vencidas), e nunca de novo.
        if (res.Ok) return (DesfechoDaTentativa.Saiu, null, null);
        if (res.Erro is "sem_resposta") return (DesfechoDaTentativa.SemResposta, null, null);
        // O script disse que não mandou. Se o quadro já confirmou, vale o quadro; se o quadro foi
        // visto saindo, é dúvida e vira incerta. Só sem quadro nenhum é "falhou" (vai para a pessoa).
        var visto = l.Confirmacoes.Cancelar(s.Id);
        if (visto is null) return (DesfechoDaTentativa.JaConfirmada, null, null);
        if (visto == true)
        {
            await l.Relatar(s.Id, "incerta", "erro_envio", s, rastro.Detalhe()).ConfigureAwait(false);
            return (DesfechoDaTentativa.Incerta, null, null);
        }
        var erro = res.Erro is "sdk_ausente" or "canal_errado" or "congelada" or "sem_cliente" or "erro_envio"
            ? res.Erro : "erro_envio";
        return (DesfechoDaTentativa.Falhou, erro, res.Detalhe);
    }

    /// <summary>Como terminou o plano B: a saída já tem desfecho, a tela recarregou o Gestor, ou não recarregou.</summary>
    private enum FimDoPlanoB { Encerrou, Recarregou, SemRecarga }

    /// <summary>
    /// 1.0.35: o PLANO B (ver <see cref="PlanoBDoEnvio"/>). Só a recarga: a tentativa seguinte é do laço,
    /// com o mesmo portão (Portao.Repetir) e a mesma reserva. Durante a recarga a GUARDA espera o quadro
    /// desta resposta: se o SDK a reenviar sozinho ao reconectar, ela não sai de novo (Encerrou).
    /// </summary>
    private static async Task<(FimDoPlanoB Fim, double Segundos)> PlanoBAsync(SaidaConversa s, PermissaoDeEnvio primeira,
        Func<string?, string?, Task<string>> recarregar, RastroDoEnvio rastro, LacoDeEnvio l)
    {
        l.Confirmacoes.Aguardar(s.Id, primeira.Canal, primeira.Texto, l.Agora(), PlanoBDoEnvio.GuardaDaRecarga);
        var inicio = l.Agora();
        string pb;
        try { pb = PlanoBDoEnvio.Normalizar(await recarregar(s.PedidoNumero, s.IfoodOrderId).WaitAsync(l.PrazoDaRecarga).ConfigureAwait(false)); }
        catch { pb = PlanoBDoEnvio.Erro; }
        rastro.PlanoB = PlanoBDoEnvio.ParaODetalhe(pb);
        var seg = Math.Max(0, (l.Agora() - inicio).TotalSeconds);
        switch (l.Confirmacoes.Estado(s.Id))
        {
            case null:
                l.Diag($"saida={s.Id} plano b={pb} em {seg:0} s: o quadro desta resposta confirmou durante a recarga");
                return (FimDoPlanoB.Encerrou, seg);
            case true:
                l.Confirmacoes.Cancelar(s.Id);
                l.Diag($"saida={s.Id} plano b={pb} em {seg:0} s: um quadro desta resposta saiu durante a recarga, incerta e sem reenvio");
                await l.Relatar(s.Id, "incerta", "erro_envio", s, rastro.Detalhe()).ConfigureAwait(false);
                return (FimDoPlanoB.Encerrou, seg);
        }
        // nenhum quadro desta resposta: a guarda sai (a próxima tentativa, se houver, espera o dela)
        l.Confirmacoes.Cancelar(s.Id);
        if (pb != PlanoBDoEnvio.Recarregou)
        {
            l.Diag($"saida={s.Id} plano b={pb} em {seg:0} s: sem recarga, segue o laço de hoje");
            return (FimDoPlanoB.SemRecarga, seg);
        }
        l.Recarregou?.Invoke();
        return (FimDoPlanoB.Recarregou, seg);
    }

    // 1.0.35: o envio no sinal (falhas seguidas deste caixa e recargas do plano B na última hora)
    private static int _falhasSeguidas;
    private static readonly System.Collections.Concurrent.ConcurrentQueue<DateTime> Recargas = new();

    private static void ContarRecarga(DateTime agora)
    {
        Recargas.Enqueue(agora);
        while (Recargas.TryPeek(out var t) && agora - t > TimeSpan.FromHours(1)) Recargas.TryDequeue(out _);
    }

    private static ConversaRaspadinha.ContagemDoEnvio ContagemDoEnvio(DateTime agora)
        => new(Volatile.Read(ref _falhasSeguidas), Recargas.Count(t => agora - t <= TimeSpan.FromHours(1)),
            Volatile.Read(ref _obsMandadas), Volatile.Read(ref _obsPuladas));

    /// <summary>
    /// 1.0.25: o SDK esta frio? Achou a instancia e nao conferiu canal nenhum, tendo canais para
    /// conferir, ou com o Gestor logado e nenhum canal de cliente visto ainda (17:51 de 08/10: a
    /// Savassi com o Gestor logado ha 40 min, instancia achada, conferidos 0, canais_cm_por_merchant
    /// vazio: a conexao do chat de clientes so nasce quando a gaveta de conversas abre).
    /// </summary>
    public static bool SdkFrio(JsonObject? sdk, int canaisParaConferir, bool gestorLogado)
    {
        if (sdk is null) return false;
        if (canaisParaConferir <= 0 && !gestorLogado) return false;
        var achou = sdk.TryGetPropertyValue("achou", out var a) && a is JsonValue av && av.TryGetValue<bool>(out var ab) && ab;
        var conferidos = sdk.TryGetPropertyValue("conferidos", out var c) && c is JsonValue cv && cv.TryGetValue<int>(out var ci) ? ci : -1;
        return achou && conferidos == 0;
    }

    /// <summary>
    /// 1.0.27 (09/10): os canais que o diagnostico do SDK confere. Antes eram so os vistos em quadros
    /// de conversa; depois de reiniciar, sem nenhum cliente escrever, nao havia canal nenhum, o SDK
    /// nao era provado e o servidor nao gerava as mensagens do pedido (13:27 a 13:35 da Savassi). O
    /// endereco do canal do iFood e sendbird_gc_cm_(pedido)_(loja): os pedidos do iFood do KDS das
    /// ultimas 3 h com os merchants do sinal completam a lista.
    /// </summary>
    public static IReadOnlyList<string> CanaisParaConferir()
    {
        var lista = Contadores.AlgunsCanais(5).ToList();
        if (lista.Count >= 3) return lista;
        try
        {
            var merchants = _ultimo.Sinal.MerchantIds;
            if (merchants.Count == 0) return lista;
            using var cx = Banco.Abrir();
            using var cmd = cx.CreateCommand();
            cmd.CommandText = "SELECT ref_id FROM kds_ticket WHERE origem = 'ifood' AND criado_em >= $desde ORDER BY criado_em DESC LIMIT 3";
            cmd.Parameters.AddWithValue("$desde", DateTime.Now.AddHours(-3).ToString("yyyy-MM-dd'T'HH:mm:ss"));
            using var r = cmd.ExecuteReader();
            while (r.Read() && lista.Count < 5)
            {
                var pedido = r.IsDBNull(0) ? "" : r.GetString(0).Trim().ToLowerInvariant();
                if (pedido.Length != 36) continue;
                foreach (var m in merchants)
                {
                    var canal = $"sendbird_gc_cm_{pedido}_{m.Trim().ToLowerInvariant()}";
                    if (CanalIfood.Ler(canal) is not null && !lista.Contains(canal)) lista.Add(canal);
                    if (lista.Count >= 5) break;
                }
            }
        }
        catch (Exception ex) { Diag("canais para conferir: " + ex.GetType().Name); }
        return lista;
    }

    /// <summary>1.0.25: abre a conversa do pedido (ou a lista) no Gestor escondido, para o SDK carregar o canal. Nunca lanca.</summary>
    private static async Task<bool> AquecerAsync(PonteDoChat ponte, string? numero, string? orderUuid)
    {
        try
        {
            if (ponte.AquecerConversa is null) return false;
            var ok = await ponte.AquecerConversa(numero, orderUuid).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (ok) _ultimoAquecimento = DateTime.Now;
            return ok;
        }
        catch (Exception ex) { Diag("aquecer: " + ex.GetType().Name); return false; }
    }

    /// <summary>
    /// Conta ao ERP o que aconteceu com uma saída (<c>chat_saida</c>). Quando o envio falhou, o ERP
    /// devolve a saída para a pessoa, e a tela mostra o aviso com "Abrir e colar". Sem rede, o próprio
    /// caixa mostra o aviso com o texto que tinha.
    /// 1.0.20: a saída do dono que falhou NUNCA vira texto para colar, nem pela reserva do ERP nem
    /// sem rede (D8: quem recebe o texto é o dono, no WhatsApp). Com o caixa mudo, nenhuma vira.
    /// </summary>
    private static async Task RelatarAsync(long saidaId, string resultado, string? erro, string? msgId, SaidaConversa? s,
        JsonObject? detalhe = null)
    {
        try
        {
            // 1.0.35: as falhas seguidas do envio deste caixa, para o sinal (os mesmos códigos que o alarme do ERP conta)
            if (resultado == "enviada") Interlocked.Exchange(ref _falhasSeguidas, 0);
            else if (resultado == "falhou" && erro is "erro_envio" or "sdk_ausente" or "canal_errado") Interlocked.Increment(ref _falhasSeguidas);
            var doDono = s?.PeloDono == true || EraDoDono(saidaId);
            // 1.0.35: o detalhe do erro só vai com falhou/incerta (CorpoSaida confere); o ERP sem o SQL 176 ignora
            var corpo = ConversaRaspadinha.CorpoSaida(Terminal, saidaId, resultado, erro, msgId, detalhe);
            for (var i = 0; i < 3; i++)
            {
                var (st, resp) = await Servicos.Nuvem().FuncaoAsync(ConversaRaspadinha.Edge, corpo, ConversaRaspadinha.Prazo)
                    .ConfigureAwait(false);
                if (st is >= 200 and < 300)
                {
                    var rr = ConversaRaspadinha.LerSaidaResultado(st, resp);
                    Diag($"saida={saidaId} resultado={resultado} erp={rr.Estado ?? "-"} dono={doDono}");
                    if (rr.Reserva is { } res)
                    {
                        // 1.0.35: com o SQL 176 o texto vem do ERP (diz o prêmio e o que fazer: "não mandei a
                        // pergunta. Heitor ganhou 1 Cookie Clássico. Pergunte o sabor no chat."); sem ele, o de hoje
                        if (ConversaRaspadinha.FalhaVaiParaPessoa(doDono, _ultimo.Sinal))
                            Emitir(new AvisoDoChat("falha_envio",
                                ConversaRaspadinha.TextoDoAvisoDeFalha(rr, res.PedidoNumero ?? s?.PedidoNumero), res.Texto,
                                res.IfoodOrderId ?? s?.IfoodOrderId, res.PedidoNumero ?? s?.PedidoNumero)
                                // 1.0.35 (revisão): o "Resgatar aqui" só na pergunta do sabor (o ERP diz, ou a etapa da saída)
                                { Resgatavel = ConversaRaspadinha.FalhaResgatavel(s?.Etapa, rr.Resgatavel) });
                        else Diag($"saida={saidaId} reserva do ERP sem texto na tela (saida do dono ou caixa mudo)");
                    }
                    return;
                }
                if (st is >= 400 and < 500 and not (408 or 425 or 429)) break;
                await Task.Delay(TimeSpan.FromSeconds(2 * (i + 1))).ConfigureAwait(false);
            }
            if (resultado == "falhou" && s is not null && ConversaRaspadinha.FalhaVaiParaPessoa(doDono, _ultimo.Sinal))
                Emitir(new AvisoDoChat("falha_envio", ConversaRaspadinha.TextoFalhaEnvio(s.PedidoNumero), s.Texto,
                    s.IfoodOrderId, s.PedidoNumero) { Resgatavel = ConversaRaspadinha.FalhaResgatavel(s.Etapa, null) });
            else if (resultado == "falhou") Diag($"saida={saidaId} falhou sem rede: sem texto na tela (dono={doDono})");
        }
        catch (Exception ex) { Diag("relatar: " + ex.GetType().Name); }
    }

    // ── 5. O SINAL (60 s) ────────────────────────────────────────────────────

    private static async Task SinalAsync()
    {
        var proximo = TimeSpan.FromSeconds(60);
        if (!await UmSinalPorVez.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (!Servicos.TemContaDeNuvem()) return;
            var agora = DateTime.Now;

            JsonObject? sdk = null;
            if (_ponte is { } p)
            {
                try
                {
                    sdk = await p.DiagSdk(CanaisParaConferir(), _wsUserId)
                        .WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
                }
                catch { sdk = null; }
                if (sdk is not null && sdk.TryGetPropertyValue("uid", out var uid)
                    && uid is JsonValue v && v.TryGetValue<string>(out var u) && !string.IsNullOrWhiteSpace(u))
                    _sdkUserId = u.Trim();
                // 1.0.25: SDK frio (achou a instancia, havia canais para conferir e nenhum foi achado):
                // abre a lista de conversas no Gestor escondido, no maximo a cada 2 min
                if (SdkFrio(sdk, CanaisParaConferir().Count, p.Gestor() == "logado") && DateTime.Now - _ultimoAquecimento > IntervaloDoAquecimento)
                {
                    _ultimoAquecimento = DateTime.Now;
                    var aqueceu = await AquecerAsync(p, null, null).ConfigureAwait(false);
                    Diag($"sinal: sdk frio, aquecimento pela lista de conversas={aqueceu}");
                }
            }

            int fila;
            using (var cx = Banco.Abrir())
            {
                fila = ConversaRaspadinha.Pendentes(cx);
                if (agora >= _proximaFaxina)
                {
                    _proximaFaxina = agora.AddHours(1);
                    try { ConversaRaspadinha.Faxina(cx, agora); } catch { }
                }
            }
            var ultimo = Contadores.UltimoQuadro;
            ConversaRaspadinha.ContagemDeCartoes? cartoes = null;
            try { cartoes = Cartoes?.Invoke(); } catch { }
            var estado = ConversaRaspadinha.EstadoDoSinal(_ponte?.Gestor() ?? "ausente",
                ultimo is { } uq && uq > agora - TimeSpan.FromMinutes(2), ultimo, fila,
                Contadores.Quadros(), sdk, Contadores.CanaisPorMerchant(), _ponte?.Saude?.Invoke(),
                ContagemDoEnvio(agora), cartoes);
            var corpo = ConversaRaspadinha.CorpoSinal(Terminal, Versao, estado);
            var (st, resp) = await Servicos.Nuvem().FuncaoAsync(ConversaRaspadinha.Edge, corpo, ConversaRaspadinha.Prazo)
                .ConfigureAwait(false);
            if (st is >= 200 and < 300)
            {
                var s = ConversaRaspadinha.LerSinal(resp, DateTime.Now);
                using (var cx = Banco.Abrir()) ConversaRaspadinha.GravarSinal(cx, resp, DateTime.Now);
                var antes = _ultimo.Sinal.Modo;
                var aprovacaoAntes = _ultimo.Sinal.Aprovacao;
                _ultimo = new UltimoSinal(s, DateTime.Now);
                proximo = TimeSpan.FromSeconds(ConversaRaspadinha.IntervaloDoSinal(s));
                if (antes != s.Modo) Diag($"modo {antes} -> {s.Modo} envio={s.Envio} comanda={s.ComandaOnde}");
                if (aprovacaoAntes != s.Aprovacao) Diag($"aprovacao {aprovacaoAntes} -> {s.Aprovacao} mudo={s.CaixaMudo}");

                foreach (var a in s.Avisos) Emitir(new AvisoDoChat(a.Tipo, a.Texto, null, a.IfoodOrderId, a.PedidoNumero) { Resgatavel = a.Resgatavel });
                foreach (var sd in s.Saidas) TratarSaida(sd, DateTime.Now, null);
                if (s.Comandas.Count > 0)
                {
                    using (var cx = Banco.Abrir())
                        foreach (var c in s.Comandas) ConversaRaspadinha.GravarBonus(cx, c.Bonus, c.Cabecalho, DateTime.Now);
                    _ = ServicoRaspadinhaChat.ImprimirPendentesAsync();
                }
            }
            else Diag($"sinal http={st}");
            await ServicoRaspadinhaChat.RepetirImpressosAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { Diag("sinal: " + ex.GetType().Name); }
        finally
        {
            UmSinalPorVez.Release();
            try { _relogioSinal?.Change(proximo, Timeout.InfiniteTimeSpan); } catch { }
        }
    }

    // ── 6. ABRIR E COLAR (Assistido) ─────────────────────────────────────────

    /// <summary>
    /// "Abrir e colar": a tela do chat abre a conversa do pedido pelo uuid, confere o número no
    /// cabeçalho e só então cola. Se não bater, copia o texto e diz para abrir a conversa à mão.
    /// </summary>
    public static async Task<bool> AbrirEColarAsync(string? orderUuid, string? numero, string texto)
    {
        try
        {
            if (_ponte is not { } p || string.IsNullOrWhiteSpace(orderUuid)) return false;
            return await p.AbrirEColar(orderUuid, numero, texto).ConfigureAwait(false);
        }
        catch (Exception ex) { Diag("abrir e colar: " + ex.GetType().Name); return false; }
    }

    // ── 7. O RESGATE MANUAL PELA TELA DO PDV (08/10/2026, SQL 154) ──────────

    /// <summary>
    /// A resposta de <c>resgate_manual</c> entra pelo MESMO caminho da resposta do chat: o bônus
    /// vira papel deste terminal (quando <c>comanda.imprimir_aqui</c>), a saída "sdk" reservada vai
    /// para a fila de envio pelo portão, a "operador" vira aviso com o texto para a pessoa colar, e
    /// o aviso de uma linha sai como sempre. Devolve a resposta lida, se a comanda saiu daqui e se
    /// sobrou texto para colar. Nunca lança.
    /// </summary>
    public static async Task<(RespostaConversa Resposta, bool ComandaSaiu, bool TemTextoParaColar)> ExecutarRespostaManualAsync(int st, string? corpo)
    {
        var agora = DateTime.Now;
        var r = ConversaRaspadinha.LerResposta(st, corpo, agora);
        if (r.Status != StatusConversa.Ok || r.Bonus is null) return (r, false, false);
        var comandaSaiu = false;
        try
        {
            using (var cx = Banco.Abrir())
                ConversaRaspadinha.GravarBonus(cx, r.Bonus, ConversaRaspadinha.CabecalhoDaResposta(r), agora);
            if (r.ImprimirAqui)
            {
                // o papel ANTES de avisar: se a bobina acabou, a linha da tela e a unica coisa que sobra
                await ServicoRaspadinhaChat.ImprimirPendentesAsync().ConfigureAwait(false);
                using var cx = Banco.Abrir();
                comandaSaiu = Dapper.SqlMapper.ExecuteScalar<long>(cx,
                    "SELECT COUNT(*) FROM raspadinha_bonus WHERE id = @I AND impresso_em IS NOT NULL AND erro_impressao IS NULL",
                    new { I = r.Bonus.Id }) == 1;
            }
            var temTexto = r.Saidas.Any(s => ConversaRaspadinha.Destino(s, _ultimo.Sinal) == DestinoDaSaida.Colar);
            Diag($"resgate manual bonus={Curta(r.Bonus.Id)} acao={r.Acao} comanda_aqui={r.ImprimirAqui} saiu={comandaSaiu} saidas={r.Saidas.Count} colar={temTexto}");
            Seguro(() => Processar(r, null));
            return (r, comandaSaiu, temTexto);
        }
        catch (Exception ex)
        {
            Diag("resgate manual: " + ex.GetType().Name);
            return (r, comandaSaiu, false);
        }
    }

    // ── rastro ───────────────────────────────────────────────────────────────

    /// <summary>
    /// ProgramData\PdvNativo\chat-conversa.txt: uma linha por acontecimento. ⚠️ Nunca o texto da
    /// pessoa, nunca a resposta inteira: tamanho, chave curta, ação, motivo.
    /// </summary>
    private static void Diag(string texto)
    {
        try { Telas.HospedeWebView2.Anotar("chat-conversa.txt", texto, Relogio.Elapsed); } catch { }
    }

    private static string Curta(string? s) => string.IsNullOrEmpty(s) ? "-" : s.Length <= 12 ? s : s[..12];

    private static void Seguro(Action a) { try { a(); } catch { } }
}
