using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>De que lado da conversa veio a fala. Desconhecido não entra.</summary>
public enum LadoFala { Desconhecido, Cliente, Loja }

/// <summary>Uma fala já triada, pronta para ir ao ERP.</summary>
public sealed record FalaConversa(string Chave, string? MsgId, LadoFala Lado, string? AutorId, string Texto,
    DateTimeOffset Quando);

/// <summary>A conversa de um pedido: o canal do Sendbird e o que ele diz (pedido e loja).</summary>
public sealed record CanalDaFala(string Canal, string OrderId, string MerchantId);

/// <summary>O que aconteceu com a chamada, do ponto de vista do caixa.</summary>
public enum StatusConversa
{
    /// <summary>O ERP respondeu <c>ok:true</c>: a fala tem desfecho, seja ele qual for.</summary>
    Ok,
    /// <summary>O ERP (ou a borda) disse não: a fala não volta a ser mandada.</summary>
    Recusado,
    /// <summary>Nada saiu do caixa: a fala fica na fila.</summary>
    SemRede,
    /// <summary>Saiu e a resposta se perdeu: a fala fica na fila (o ERP deduplica pela chave).</summary>
    NaoConfirmou,
    /// <summary>A borda ainda não está no ar: espera o deploy.</summary>
    NuvemSemRecurso,
    /// <summary>Este caixa foi barrado (401/403).</summary>
    SemPermissao,
}

/// <summary>Uma resposta pronta para o cliente, como o ERP a devolve (seção 4.3).</summary>
/// <param name="PeloDono">
/// 1.0.20 (aprovação pelo WhatsApp do dono): a saída passou pelo OK do dono. Ela só sai pelo SDK,
/// reservada a este terminal; NUNCA vira texto para a pessoa colar, nem quando o envio falha.
/// </param>
public sealed record SaidaConversa(long Id, string Etapa, int Ordem, string Texto, string Como, string Estado,
    string? Canal, string? IfoodOrderId, string? PedidoNumero, bool PeloDono = false);

/// <summary>O que o caixa faz com uma saída que o ERP mandou (1.0.20).</summary>
public enum DestinoDaSaida
{
    /// <summary>Fica quieta: sombra, a proposta esperando o dono, ou o caixa mudo.</summary>
    Nada,
    /// <summary>Vai para a fila de envio pelo SDK, e o portão decide.</summary>
    Enviar,
    /// <summary>Vira aviso com o texto para a pessoa colar (Assistido).</summary>
    Colar,
}

/// <summary>Um aviso de uma linha para o operador (seção 2.2), já escrito pelo ERP.</summary>
public sealed record AvisoConversa(string Tipo, string Texto, string? IfoodOrderId, string? PedidoNumero);

public sealed record ResultadoFala(string Chave, string Acao, string? Motivo, bool Repetida);

public sealed record PedidoConversa(string? IfoodOrderId, string? Numero, string? Cliente, string? EntregaStatus);

public sealed record EstadoDaConversa(string? Id, string? Estado, bool Sombra);

/// <summary>A resposta de <c>raspadinha_chat_mensagem</c> no vocabulário do caixa.</summary>
public sealed record RespostaConversa(
    StatusConversa Status, string? MotivoCru, string Modo, bool Prova, string Acao, string? Motivo, bool Repetida,
    IReadOnlyList<ResultadoFala> Resultados, PedidoConversa? Pedido, EstadoDaConversa? Conversa,
    BonusRaspadinha? Bonus, bool ImprimirAqui, IReadOnlyList<SaidaConversa> Saidas, AvisoConversa? Aviso);

/// <summary>Uma comanda que o sinal entrega a este terminal, já reservada para ele.</summary>
public sealed record ComandaDoSinal(BonusRaspadinha Bonus, string Cabecalho);

/// <summary>
/// O sinal de vida (<c>raspadinha_chat_sinal</c>): o modo da loja, como enviar, quem imprime, de
/// que merchants são as conversas desta loja, e o que o ERP guardou para este terminal.
/// <see cref="Legivel"/> false = sinal ausente ou ilegível, e isso conta como DESLIGADO.
/// </summary>
/// <param name="Aprovacao">
/// 1.0.20: a aprovação pelo WhatsApp do dono (<c>nenhuma</c>, <c>prova</c> ou <c>dono</c>). Valor
/// desconhecido ou ausente é <c>nenhuma</c>. É uma chave à parte do modo: a loja continua
/// <c>assistido</c>, e um caixa velho continua lendo um modo que conhece.
/// </param>
/// <param name="DonoFora">
/// 1.0.20 (revisão 07/10): o WhatsApp do dono parou de receber (o ERP gravou o aviso
/// <c>dono_fora</c> e nada chegou a ele depois). A aprovação continua ligada, mas os pedidos de ajuda
/// voltam ao caixa: sem isso, com o caixa mudo, o cliente que pede uma pessoa não chegava a ninguém.
/// Só <c>true</c> do JSON conta.
/// </param>
public sealed record SinalConversa(
    bool Legivel, string Modo, string PalavraModo, string Envio, string ComandaOnde,
    IReadOnlyList<string> MerchantIds, int IntervaloS, string? Pausado,
    IReadOnlyList<AvisoConversa> Avisos, IReadOnlyList<SaidaConversa> Saidas, IReadOnlyList<ComandaDoSinal> Comandas,
    string Aprovacao = ConversaRaspadinha.AprovacaoNenhuma, bool DonoFora = false)
{
    /// <summary>O chat novo está valendo nesta loja (Sombra, Assistido ou Automático)?</summary>
    public bool Ativo => Legivel && Modo is not ConversaRaspadinha.ModoDesligado;

    /// <summary>
    /// O caixa fica MUDO para o chat (1.0.20, D10): com a aprovação do dono valendo para todos, nada
    /// de som, de aviso na tela nem de texto para colar. A comanda de resgate continua saindo.
    /// Revisão 07/10: com o WhatsApp do dono fora (<see cref="DonoFora"/>) o caixa volta a falar,
    /// porque ele é o único que ainda pode ver um pedido de ajuda.
    /// </summary>
    public bool CaixaMudo => Ativo && Aprovacao == ConversaRaspadinha.AprovacaoDono && !DonoFora;

    public static SinalConversa Desligado { get; } = new(false, ConversaRaspadinha.ModoDesligado,
        ConversaRaspadinha.ModoDesligado, "nenhum", "caixa", Array.Empty<string>(), 60, null,
        Array.Empty<AvisoConversa>(), Array.Empty<SaidaConversa>(), Array.Empty<ComandaDoSinal>());
}

/// <summary>A resposta de <c>raspadinha_chat_saida_resultado</c>.</summary>
public sealed record ResultadoSaida(bool Ok, string? Estado, SaidaConversa? Reserva);

public enum DesfechoReserva { Ok, Recusada, SemResposta }

public enum DesfechoPapel { Saiu, NaoSaiu, NaoEraMeu }

/// <summary>O que aconteceu ao tirar o papel de um bônus.</summary>
public sealed record ResultadoPapel(DesfechoPapel Desfecho, string? Erro, bool ImpressoAvisado);

/// <summary>Uma fala depois da triagem: a fala e o canal, ou o motivo de ter ficado de fora.</summary>
public sealed record FalaTriada(FalaConversa? Fala, CanalDaFala? Canal, string Motivo)
{
    public bool Entra => Fala is not null && Canal is not null;
}

/// <summary>
/// RESGATE PELO CHAT DO iFOOD, LADO DO CAIXA (07/10/2026, desenho "resgate-final", tarefa P2).
///
/// O caixa LÊ e EXECUTA; quem decide é o ERP (<c>raspadinha_chat_mensagem</c>). Aqui mora o que é
/// do contexto da captura e não pode morar em outro lugar (seção 5):
///  · de que lado veio a fala (cliente, loja ou desconhecido);
///  · a chave da mensagem, igual em todos os terminais;
///  · a triagem (canal, loja, recência, eco) e o agrupamento de 6 s;
///  · o corpo das chamadas e a leitura das respostas;
///  · o PORTÃO de envio, o eco local e o limite de 20 por minuto;
///  · a fila sem internet e a política do papel da comanda.
///
/// ⚠️ ESTE ARQUIVO NÃO CONHECE O CÓDIGO DA RASPADINHA NEM AS PALAVRAS-CHAVE. Formato do código,
/// sufixo sem AD, gatilho, negação e tolerância a erro de digitação moram no ERP, numa cópia só.
/// A suíte vigia isso pelo fonte (FT-2 e a FT das palavras).
/// </summary>
public static class ConversaRaspadinha
{
    public const string Edge = ChatRaspadinha.Edge;

    /// <summary>Tipo da linha na fila (Drenagem.TiposComHandler). ref_id = canal.</summary>
    public const string TipoNaFila = "raspadinha_conversa";

    public const string AcaoMensagens = "chat_mensagens";
    public const string AcaoSinal = "chat_sinal";
    public const string AcaoSaida = "chat_saida";
    public const string AcaoReservar = "comanda_reservar";
    public const string AcaoLiberar = "comanda_liberar";
    public const string AcaoImpresso = "impresso";

    public const string ModoDesligado = "desligado";
    public const string ModoSombra = "sombra";
    public const string ModoAssistido = "assistido";
    public const string ModoAutomatico = "automatico";

    public const string ComoSdk = "sdk";
    public const string ComoOperador = "operador";
    public const string ComoSombra = "sombra";
    /// <summary>1.0.20: a proposta esperando o OK do dono no WhatsApp. O caixa não faz nada com ela.</summary>
    public const string ComoDono = "dono";

    /// <summary>1.0.20: a aprovação pelo WhatsApp do dono (coluna <c>raspadinha_chat_loja.aprovacao</c>).</summary>
    public const string AprovacaoNenhuma = "nenhuma";
    public const string AprovacaoProva = "prova";
    public const string AprovacaoDono = "dono";

    public const string CabecalhoNormal = "normal";
    public const string CabecalhoReservaDoCaixa = "reserva_do_caixa";

    /// <summary>Config local com o JSON do último sinal e quando ele chegou (seção 4.9).</summary>
    public const string ChaveSinal = "raspadinha_v2_sinal";
    public const string ChaveSinalEm = "raspadinha_v2_sinal_em";

    /// <summary>Até 10 falas por chamada, texto de até 1000 caracteres, corpo de até 16 KB (seção 4.6).</summary>
    public const int TetoFalas = 10;
    public const int TetoTexto = 1000;
    public const int TetoCorpoBytes = 15_000;
    public const int TetoTerminal = 60;

    /// <summary>Fala mais velha que isto não entra (seção 1.1).</summary>
    public static readonly TimeSpan Recencia = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ToleranciaDeRelogio = TimeSpan.FromMinutes(2);

    /// <summary>Quanto a fila espera antes de mexer numa fala que a tela ainda está juntando.</summary>
    public static readonly TimeSpan EsperaDaTela = TimeSpan.FromMinutes(2);

    /// <summary>Prazo de cada chamada da borda.</summary>
    public static readonly TimeSpan Prazo = TimeSpan.FromSeconds(12);

    /// <summary>A fala local some inteira depois disto (o texto já sai no desfecho).</summary>
    public static readonly TimeSpan GuardaDaFala = TimeSpan.FromDays(2);

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ── QUEM FALOU (puro) ────────────────────────────────────────────────────

    /// <summary>
    /// O lado da fala. A LOJA é quem tem o id da URL do WebSocket ou o do usuário do SDK, e isso vale
    /// por cima de tudo: o caixa nunca pode tratar a própria fala como do cliente. Depois disso vale o
    /// que o quadro declara: CUSTOMER é cliente, MERCHANT é loja. Qualquer outro tipo (suporte do
    /// iFood, entregador, sistema) e quadro sem tipo ficam DESCONHECIDOS, e desconhecido não entra.
    /// </summary>
    public static LadoFala Lado(MensagemSendbird? m, string? wsUserId, string? sdkUserId)
    {
        if (m is null) return LadoFala.Desconhecido;
        var autor = m.AutorId?.Trim();
        if (!string.IsNullOrEmpty(autor)
            && (Igual(autor, wsUserId) || Igual(autor, sdkUserId)))
            return LadoFala.Loja;
        return (m.UserType ?? "").Trim().ToUpperInvariant() switch
        {
            "CUSTOMER" => LadoFala.Cliente,
            "MERCHANT" => LadoFala.Loja,
            _ => LadoFala.Desconhecido,
        };

        static bool Igual(string a, string? b)
            => !string.IsNullOrWhiteSpace(b) && string.Equals(a, b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A chave da fala, IGUAL em todos os terminais que virem o mesmo quadro: <c>sb:&lt;msg_id&gt;</c>
    /// quando o quadro traz o id da mensagem; senão <c>h:&lt;sha256(canal|autor|created_at_ms|texto)&gt;</c>.
    /// É ela que o ERP usa no <c>unique(loja, chave)</c>.
    /// </summary>
    public static string Chave(MensagemSendbird m)
        => m.MsgId is { Length: > 0 } id
            ? "sb:" + id.Trim()
            : "h:" + Sha256(string.Join('|', m.Canal ?? "", m.AutorId ?? "",
                m.CriadoEmMs?.ToString(CultureInfo.InvariantCulture) ?? "", m.Texto));

    /// <summary>O texto como ele viaja: uma linha, sem espaço dobrado, cortado no teto.</summary>
    public static string CortarTexto(string? texto)
    {
        var t = Regex.Replace(texto ?? "", @"\s+", " ").Trim();
        return t.Length <= TetoTexto ? t : t[..TetoTexto];
    }

    /// <summary>Hash do texto normalizado (minúsculas, uma linha). Serve para o eco local.</summary>
    public static string HashTexto(string? texto)
        => Sha256(CortarTexto(texto).ToLowerInvariant());

    private static string Sha256(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    // ── A TRIAGEM (pura) ─────────────────────────────────────────────────────

    /// <summary>
    /// Decide se a fala vai ao ERP. Na ordem: o chat novo está valendo; não é sussurro; o canal é de
    /// pedido do iFood; o merchant é desta loja (senão o canal fica marcado como de fora por 12 h);
    /// tem texto; é de agora (30 min); o lado é conhecido; fala da loja só em conversa viva; fala do
    /// cliente igual ao que este caixa mandou é eco.
    /// </summary>
    public static FalaTriada Triagem(MensagemSendbird? m, SinalConversa sinal, CacheDeCanais canais, MemoriaDeEco eco,
        string? wsUserId, string? sdkUserId, DateTime agora)
    {
        if (m is null || m.Comando != QuadroSendbird.ComandoMensagem) return Fora("nao_e_fala");
        if (!sinal.Ativo) return Fora("desligado");
        if (m.Whisper) return Fora("sussurro");
        if (CanalIfood.Ler(m.Canal) is not { } ids) return Fora("canal_fora_do_padrao");
        var canal = new CanalDaFala(m.Canal!, ids.OrderId, ids.MerchantId);
        if (canais.EhDeFora(canal.Canal, agora)) return Fora("de_fora");
        if (sinal.MerchantIds.Count == 0) return Fora("sem_merchants");
        if (!sinal.MerchantIds.Contains(canal.MerchantId, StringComparer.OrdinalIgnoreCase))
        {
            canais.MarcarDeFora(canal.Canal, agora);
            return Fora("merchant_de_outra_loja");
        }
        var texto = CortarTexto(m.Texto);
        if (texto.Length == 0) return Fora("sem_texto");
        if (m.CriadoEmMs is not { } ms) return Fora("sem_hora");
        DateTimeOffset quando;
        try { quando = DateTimeOffset.FromUnixTimeMilliseconds(ms); }
        catch { return Fora("sem_hora"); }
        var local = quando.LocalDateTime;
        if (local < agora - Recencia || local > agora + ToleranciaDeRelogio) return Fora("antiga");
        var lado = Lado(m, wsUserId, sdkUserId);
        if (lado == LadoFala.Desconhecido) return Fora("autor_desconhecido");
        if (lado == LadoFala.Loja && !canais.EmConversa(canal.Canal, agora)) return Fora("loja_fora_de_conversa");
        if (lado == LadoFala.Cliente && eco.Contem(canal.Canal, texto, agora)) return Fora("eco");
        return new FalaTriada(new FalaConversa(Chave(m), m.MsgId, lado, m.AutorId, texto, quando), canal, "ok");

        static FalaTriada Fora(string motivo) => new(null, null, motivo);
    }

    // ── O QUE VAI (puro) ─────────────────────────────────────────────────────

    /// <summary>O nome da máquina, que é o "terminal" das chamadas (seção 4.6), cortado em 60.</summary>
    public static string NomeDoTerminal()
    {
        string n;
        try { n = Environment.MachineName; } catch { n = "caixa"; }
        n = string.IsNullOrWhiteSpace(n) ? "caixa" : n.Trim();
        return n.Length <= TetoTerminal ? n : n[..TetoTerminal];
    }

    private static string Terminal(string? t)
    {
        var s = string.IsNullOrWhiteSpace(t) ? NomeDoTerminal() : t.Trim();
        return s.Length <= TetoTerminal ? s : s[..TetoTerminal];
    }

    /// <summary>
    /// O corpo do <c>chat_mensagens</c> e as chaves que couberam nele. Até 10 falas, cada texto até
    /// 1000 caracteres, e o corpo inteiro abaixo de 16 KB: fala que não coube fica para a próxima
    /// chamada (continua pendente). A loja NUNCA vai no corpo: ela vem da porta, na borda.
    /// </summary>
    public static (string Corpo, IReadOnlyList<string> Chaves) CorpoMensagens(string? terminal, CanalDaFala c,
        IEnumerable<FalaConversa> falas)
    {
        var lista = falas.Take(TetoFalas).ToList();
        while (true)
        {
            var corpo = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["acao"] = AcaoMensagens,
                ["terminal"] = Terminal(terminal),
                ["canal"] = c.Canal,
                ["ifood_order_id"] = c.OrderId,
                ["merchant_id"] = c.MerchantId,
                ["mensagens"] = lista.Select(f => new Dictionary<string, object?>
                {
                    ["chave"] = f.Chave,
                    ["msg_id"] = f.MsgId,
                    ["lado"] = f.Lado == LadoFala.Loja ? "loja" : "cliente",
                    ["autor_id"] = f.AutorId,
                    ["texto"] = CortarTexto(f.Texto),
                    ["quando"] = f.Quando.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                }).ToList(),
            }, Json);
            if (Encoding.UTF8.GetByteCount(corpo) <= TetoCorpoBytes || lista.Count <= 1)
                return (corpo, lista.Select(f => f.Chave).ToList());
            lista.RemoveAt(lista.Count - 1);
        }
    }

    /// <summary>O corpo do <c>chat_sinal</c>. O estado é só chaves e contagens (ver <see cref="EstadoDoSinal"/>).</summary>
    public static string CorpoSinal(string? terminal, string? versao, string? estadoJson)
    {
        JsonNode? estado = null;
        try { estado = string.IsNullOrWhiteSpace(estadoJson) ? null : JsonNode.Parse(estadoJson); } catch { }
        var o = new JsonObject
        {
            ["acao"] = AcaoSinal,
            ["terminal"] = Terminal(terminal),
            ["versao"] = versao,
            ["estado"] = estado as JsonObject ?? new JsonObject(),
        };
        return o.ToJsonString(Json);
    }

    /// <summary>O corpo do <c>chat_saida</c>: o que aconteceu com uma resposta reservada a este terminal.</summary>
    public static string CorpoSaida(string? terminal, long saidaId, string resultado, string? erro, string? sendbirdMsgId)
    {
        var r = resultado is "enviada" or "falhou" or "incerta" ? resultado : "incerta";
        var o = new Dictionary<string, object?>
        {
            ["acao"] = AcaoSaida,
            ["terminal"] = Terminal(terminal),
            ["saida_id"] = saidaId,
            ["resultado"] = r,
        };
        if (!string.IsNullOrWhiteSpace(erro)) o["erro"] = erro.Trim();
        if (!string.IsNullOrWhiteSpace(sendbirdMsgId)) o["sendbird_msg_id"] = sendbirdMsgId.Trim();
        return JsonSerializer.Serialize(o, Json);
    }

    /// <summary>O corpo de <c>comanda_reservar</c> e <c>comanda_liberar</c>.</summary>
    public static string CorpoComanda(string acao, string bonusId, string? quem)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["acao"] = acao == AcaoLiberar ? AcaoLiberar : AcaoReservar,
            ["bonus_id"] = bonusId,
            ["quem"] = Terminal(quem),
        }, Json);

    /// <summary>O corpo do <c>impresso</c> (ação que já existe na borda): só depois do papel.</summary>
    public static string CorpoImpresso(string bonusId, string? por)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["acao"] = AcaoImpresso,
            ["bonus_id"] = bonusId,
            ["por"] = Terminal(por),
        }, Json);

    // ── O QUE VOLTA (puro) ───────────────────────────────────────────────────

    /// <summary>
    /// A resposta do <c>chat_mensagens</c>. Status: -1 = nada saiu do caixa, 0 = saiu e ficou sem
    /// resposta. 404 é a borda sem publicar, 401/403 é o caixa barrado, 5xx/408/425/429 é "tente
    /// depois". Corpo ilegível num 2xx NÃO é recusa: o ERP pode ter decidido.
    /// </summary>
    public static RespostaConversa LerResposta(int status, string? corpo, DateTime agora)
    {
        if (status == -1) return Falha(StatusConversa.SemRede, "nada saiu do caixa");
        if (status == 404) return Falha(StatusConversa.NuvemSemRecurso, "a borda ainda não está no ar");
        if (status is 401 or 403) return Falha(StatusConversa.SemPermissao, $"HTTP {status}");
        if (status == 0 || status >= 500 || status is 408 or 425 or 429)
            return Falha(StatusConversa.NaoConfirmou, status == 0 ? "sem resposta" : $"HTTP {status}");
        try
        {
            using var doc = JsonDocument.Parse(corpo ?? "");
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0) r = r[0];
            if (r.ValueKind != JsonValueKind.Object) return Falha(StatusConversa.NaoConfirmou, "resposta sem objeto");
            var ok = r.TryGetProperty("ok", out var okv) && okv.ValueKind == JsonValueKind.True;
            if (!ok || status is < 200 or >= 300)
            {
                var cru = Texto(r, "motivo") ?? Texto(r, "erro") ?? Texto(r, "error") ?? $"HTTP {status}";
                return Falha(StatusConversa.Recusado, cru) with { Motivo = cru };
            }

            var resultados = new List<ResultadoFala>();
            if (r.TryGetProperty("resultados", out var rs) && rs.ValueKind == JsonValueKind.Array)
                foreach (var x in rs.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.Object && Texto(x, "chave") is { } ch)
                        resultados.Add(new ResultadoFala(ch, Texto(x, "acao") ?? "ignorar", Texto(x, "motivo"),
                            Bool(x, "repetida")));

            PedidoConversa? pedido = null;
            if (r.TryGetProperty("pedido", out var pv) && pv.ValueKind == JsonValueKind.Object)
                pedido = new PedidoConversa(Texto(pv, "ifood_order_id"), TextoOuNumero(pv, "numero"),
                    Texto(pv, "cliente"), Texto(pv, "entrega_status"));

            EstadoDaConversa? conversa = null;
            if (r.TryGetProperty("conversa", out var cv) && cv.ValueKind == JsonValueKind.Object)
                conversa = new EstadoDaConversa(Texto(cv, "id"), Texto(cv, "estado"), Bool(cv, "sombra"));

            BonusRaspadinha? bonus = null;
            if (r.TryGetProperty("bonus", out var bv) && bv.ValueKind == JsonValueKind.Object)
                bonus = LerBonus(bv, agora);

            var imprimir = r.TryGetProperty("comanda", out var cmv) && cmv.ValueKind == JsonValueKind.Object
                           && Bool(cmv, "imprimir_aqui");

            return new RespostaConversa(StatusConversa.Ok, null,
                Modo(Texto(r, "modo")), Bool(r, "prova"), Texto(r, "acao") ?? "ignorar", Texto(r, "motivo"),
                Bool(r, "repetida"), resultados, pedido, conversa, bonus, imprimir && bonus is not null,
                Saidas(r, "saidas"), Aviso(r, "aviso"));
        }
        catch
        {
            return status is >= 200 and < 300
                ? Falha(StatusConversa.NaoConfirmou, "resposta ilegível")
                : Falha(StatusConversa.Recusado, $"HTTP {status}");
        }

        static RespostaConversa Falha(StatusConversa s, string cru)
            => new(s, cru, ModoDesligado, false, "ignorar", null, false, Array.Empty<ResultadoFala>(), null, null,
                null, false, Array.Empty<SaidaConversa>(), null);
    }

    /// <summary>
    /// O sinal. Qualquer coisa que não seja um <c>ok:true</c> com um modo conhecido conta como
    /// DESLIGADO: é o que acontece com a borda antiga, que responde ao sinal como se fosse uma
    /// mensagem sem código.
    /// </summary>
    public static SinalConversa LerSinal(string? corpo, DateTime? agora = null)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return SinalConversa.Desligado;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return SinalConversa.Desligado;
            if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)) return SinalConversa.Desligado;
            var modoCru = Texto(r, "modo");
            if (modoCru is null || Modo(modoCru) != modoCru.Trim().ToLowerInvariant()) return SinalConversa.Desligado;

            var merchants = new List<string>();
            if (r.TryGetProperty("merchant_ids", out var mv) && mv.ValueKind == JsonValueKind.Array)
                foreach (var x in mv.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s) merchants.Add(s.Trim());

            // teto de 4 min: o sinal vale 5 min para o portão (PortaoDeEnvio.SinalValido), e um
            // intervalo maior que isso deixaria o envio parado entre um sinal e outro
            var intervalo = r.TryGetProperty("intervalo_s", out var iv) && iv.ValueKind == JsonValueKind.Number
                            && iv.TryGetInt32(out var n) ? Math.Clamp(n, 30, 240) : 60;

            string? pausado = null;
            if (r.TryGetProperty("pausado", out var pz))
                pausado = pz.ValueKind switch
                {
                    JsonValueKind.String => pz.GetString() is { Length: > 0 } ps ? ps : null,
                    JsonValueKind.Object => Texto(pz, "motivo") ?? "pausado",
                    JsonValueKind.True => "pausado",
                    _ => null,
                };

            var comandas = new List<ComandaDoSinal>();
            if (r.TryGetProperty("comandas", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var c in cs.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) continue;
                    if (!c.TryGetProperty("bonus", out var b) || b.ValueKind != JsonValueKind.Object) continue;
                    var cab = Texto(c, "cabecalho") == CabecalhoReservaDoCaixa ? CabecalhoReservaDoCaixa : CabecalhoNormal;
                    if (LerBonus(b, agora ?? DateTime.Now) is { } bonus)
                        comandas.Add(new ComandaDoSinal(bonus with { Cabecalho = cab }, cab));
                }

            var envio = Texto(r, "envio")?.ToLowerInvariant();
            var onde = Texto(r, "comanda_onde")?.ToLowerInvariant();
            return new SinalConversa(true, Modo(modoCru), Modo(Texto(r, "palavra_modo")),
                envio is ComoSdk or ComoOperador ? envio : "nenhum",
                onde == "kds" ? "kds" : "caixa", // 'ambos' = a copia do caixa
                merchants, intervalo, pausado, Avisos(r, "avisos"), Saidas(r, "saidas"), comandas,
                Aprovacao(Texto(r, "aprovacao")), Bool(r, "dono_fora"));
        }
        catch { return SinalConversa.Desligado; }
    }

    /// <summary>A resposta do <c>chat_saida</c>: o estado da saída e, se o envio falhou, a reserva para a pessoa.</summary>
    public static ResultadoSaida LerSaidaResultado(int status, string? corpo)
    {
        if (status is < 200 or >= 300) return new ResultadoSaida(false, null, null);
        try
        {
            using var doc = JsonDocument.Parse(corpo ?? "");
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return new ResultadoSaida(false, null, null);
            SaidaConversa? reserva = null;
            if (r.TryGetProperty("reserva", out var rv) && rv.ValueKind == JsonValueKind.Object
                && Texto(rv, "texto") is { } t)
                reserva = new SaidaConversa(0, "reserva", 1, t, Texto(rv, "como") ?? ComoOperador, ComoOperador,
                    Texto(rv, "canal"), Texto(rv, "ifood_order_id"), TextoOuNumero(rv, "pedido_numero"));
            return new ResultadoSaida(Bool(r, "ok"), Texto(r, "estado"), reserva);
        }
        catch { return new ResultadoSaida(false, null, null); }
    }

    /// <summary>
    /// A resposta de <c>comanda_reservar</c>. Só os motivos do contrato recusam o papel; sem resposta,
    /// ou com um motivo que esta versão não conhece, o papel sai assim mesmo: melhor dobrado do que
    /// nenhum (seção 4.7).
    /// </summary>
    public static (DesfechoReserva Desfecho, string? Motivo) LerReserva(int status, string? corpo)
    {
        if (status is < 200 or >= 300) return (DesfechoReserva.SemResposta, status == 0 ? "sem resposta" : $"HTTP {status}");
        try
        {
            using var doc = JsonDocument.Parse(corpo ?? "");
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return (DesfechoReserva.SemResposta, "resposta sem objeto");
            if (Bool(r, "ok")) return (DesfechoReserva.Ok, null);
            var motivo = Texto(r, "motivo");
            return motivo is "ja_impresso" or "reservada" or "cancelado" or "outra_loja" or "nao_achado"
                ? (DesfechoReserva.Recusada, motivo)
                : (DesfechoReserva.SemResposta, motivo);
        }
        catch { return (DesfechoReserva.SemResposta, "resposta ilegível"); }
    }

    /// <summary>O bônus do contrato (seção 4.3). Sem id não há o que gravar.</summary>
    public static BonusRaspadinha? LerBonus(JsonElement b, DateTime agora)
    {
        var id = Texto(b, "id");
        if (id is null) return null;
        var itens = new List<ItemBonus>();
        if (b.TryGetProperty("itens", out var iv) && iv.ValueKind == JsonValueKind.Array)
            foreach (var x in iv.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s)
                { itens.Add(new ItemBonus(s.Trim(), 1)); continue; }
                if (x.ValueKind != JsonValueKind.Object || Texto(x, "nome") is not { } nome) continue;
                var q = x.TryGetProperty("qtd", out var qv) && qv.ValueKind == JsonValueKind.Number
                        && qv.TryGetInt32(out var qi) ? qi : 1;
                itens.Add(new ItemBonus(nome, Math.Clamp(q, 1, 99)));
            }
        var onde = Texto(b, "comanda_onde")?.ToLowerInvariant();
        var origem = Texto(b, "origem")?.ToLowerInvariant();
        return new BonusRaspadinha(
            id, Texto(b, "codigo") ?? Texto(b, "redeem_code"),
            Texto(b, "premio_nome") ?? Texto(b, "premio"), Texto(b, "premio_emoji"),
            Texto(b, "cliente_nome") ?? Texto(b, "cliente"),
            TextoOuNumero(b, "pedido_numero"), Texto(b, "loja"), Texto(b, "ifood_order_id"),
            origem is ChatRaspadinha.OrigemExtensao or ChatRaspadinha.OrigemAutomatico ? origem : ChatRaspadinha.OrigemCaixa,
            Data(b, "criado_em") ?? agora,
            itens.Count > 0 ? itens : null,
            Texto(b, "assinatura"),
            // 'ambos' (07/10, decisao do dono): a comanda sai na cozinha E no caixa.
            // Para o caixa e a copia dele, igual a 'caixa'. Antes caia em null, que
            // aqui quer dizer fluxo antigo, e o bonus perdia a reserva do chat novo.
            onde is "kds" or "caixa" ? onde : onde == "ambos" ? "caixa" : null,
            null);
    }

    private static List<SaidaConversa> Saidas(JsonElement r, string campo)
    {
        var lista = new List<SaidaConversa>();
        if (!r.TryGetProperty(campo, out var sv) || sv.ValueKind != JsonValueKind.Array) return lista;
        foreach (var s in sv.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            if (Numero(s, "id") is not { } id || Texto(s, "texto") is not { } texto) continue;
            lista.Add(new SaidaConversa(id, Texto(s, "etapa") ?? "", (int)(Numero(s, "ordem") ?? 1), texto,
                (Texto(s, "como") ?? "").ToLowerInvariant(), (Texto(s, "estado") ?? "").ToLowerInvariant(),
                Texto(s, "canal"), Texto(s, "ifood_order_id"), TextoOuNumero(s, "pedido_numero"),
                Bool(s, "pelo_dono")));
        }
        return lista;
    }

    private static AvisoConversa? Aviso(JsonElement r, string campo)
        => r.TryGetProperty(campo, out var a) && a.ValueKind == JsonValueKind.Object ? AvisoDe(a) : null;

    private static List<AvisoConversa> Avisos(JsonElement r, string campo)
    {
        var lista = new List<AvisoConversa>();
        if (!r.TryGetProperty(campo, out var av) || av.ValueKind != JsonValueKind.Array) return lista;
        foreach (var a in av.EnumerateArray())
            if (a.ValueKind == JsonValueKind.Object && AvisoDe(a) is { } x) lista.Add(x);
        return lista;
    }

    private static AvisoConversa? AvisoDe(JsonElement a)
    {
        var texto = Texto(a, "texto");
        if (texto is null) return null;
        return new AvisoConversa(Texto(a, "tipo") ?? "aviso", texto, Texto(a, "ifood_order_id"),
            TextoOuNumero(a, "pedido_numero"));
    }

    private static string Modo(string? m) => (m ?? "").Trim().ToLowerInvariant() switch
    {
        ModoSombra => ModoSombra,
        ModoAssistido => ModoAssistido,
        ModoAutomatico => ModoAutomatico,
        _ => ModoDesligado,
    };

    /// <summary>A aprovação do sinal. Qualquer valor que esta versão não conhece é <c>nenhuma</c>.</summary>
    private static string Aprovacao(string? a) => (a ?? "").Trim().ToLowerInvariant() switch
    {
        AprovacaoProva => AprovacaoProva,
        AprovacaoDono => AprovacaoDono,
        _ => AprovacaoNenhuma,
    };

    // ── A APROVAÇÃO DO DONO NO CAIXA (puro, 1.0.20) ──────────────────────────

    /// <summary>
    /// O que o caixa faz com uma saída (desenho da aprovação pelo WhatsApp, seções 4.5 e 4.8):
    ///  · "sdk" reservada a este terminal vai para a fila de envio, e o portão decide o resto. É por
    ///    aqui que sai a aprovada pelo dono (<c>pelo_dono</c>), com a confirmação pelo eco de hoje;
    ///  · com o caixa mudo (aprovação <c>dono</c>, tudo passa pelo dono) só sai pelo SDK a saída que
    ///    tem o <c>pelo_dono</c>: uma "sdk" sem ele seria texto ao cliente sem o OK do dono;
    ///  · a saída do dono (<c>pelo_dono</c>) em qualquer outro estado fica quieta: o texto dela só
    ///    vai ao cliente pelo SDK, depois do OK, e nunca vira texto para colar;
    ///  · a proposta esperando o dono (<c>como='dono'</c>) fica quieta;
    ///  · "operador" vira aviso para a pessoa colar (o Assistido de hoje, e o <c>prova</c> para os
    ///    clientes de fora da lista). Revisão 07/10: mesmo com o caixa mudo, porque com
    ///    <c>aprovacao='dono'</c> o ERP nunca cria "operador"; quando cria, a aprovação foi desligada
    ///    (152d) depois do último sinal deste caixa, e calar perdia a resposta (o sinal seguinte não
    ///    entrega "operador" de novo e ela vence em 10 min);
    ///  · sombra e qualquer valor desconhecido ficam quietos.
    /// </summary>
    public static DestinoDaSaida Destino(SaidaConversa s, SinalConversa sinal)
    {
        if (s.Como == ComoSdk && s.Estado == "reservada")
            return sinal.CaixaMudo && !s.PeloDono ? DestinoDaSaida.Nada : DestinoDaSaida.Enviar;
        if (s.PeloDono || s.Como == ComoDono) return DestinoDaSaida.Nada;
        return s.Como == ComoOperador ? DestinoDaSaida.Colar : DestinoDaSaida.Nada;
    }

    /// <summary>
    /// Uma saída cujo envio falhou pode virar aviso com o texto para a pessoa colar? Nunca a do dono
    /// (D8: o dono recebe o texto no WhatsApp para mandar pelo app do Gestor), e nunca com o caixa
    /// mudo. Vale para a reserva que o ERP devolve e para o aviso que o caixa monta sem rede.
    /// </summary>
    public static bool FalhaVaiParaPessoa(bool saidaDoDono, SinalConversa sinal)
        => !saidaDoDono && !sinal.CaixaMudo;

    /// <summary>
    /// De quantos em quantos segundos vai o próximo sinal. Com a aprovação ligada (prova ou dono) o
    /// teto é 30 s, mesmo que o ERP mande mais: é o sinal que entrega a aprovada a este caixa, e o
    /// cliente espera o tempo do sinal depois do OK do dono (seção 1.2).
    /// </summary>
    public static int IntervaloDoSinal(SinalConversa s)
        => s.Aprovacao is AprovacaoProva or AprovacaoDono ? Math.Min(s.IntervaloS, 30) : s.IntervaloS;

    // ── O AVISO NA TELA (puro) ───────────────────────────────────────────────

    /// <summary>
    /// A linha que a tela mostra quando o ERP manda a pessoa colar a resposta (Assistido). O ERP
    /// devolve pronta quando é ele quem avisa; esta é a do caixa, com o mesmo texto da seção 2.2.
    /// </summary>
    public static string TextoMandar(string? numero)
        => $"{Numero(numero)}: mande esta resposta no chat.";

    /// <summary>A linha de quando o envio automático não deu (seção 2.2, <c>falha_envio</c>).</summary>
    public static string TextoFalhaEnvio(string? numero)
        => $"{Numero(numero)}: não consegui mandar a resposta. Mande pelo chat.";

    /// <summary>A linha de quando a pessoa precisa abrir a conversa à mão.</summary>
    public static string TextoAbraECole(string? numero)
        => $"Abra a conversa do {Numero(numero)} e cole";

    private static string Numero(string? n)
    {
        var d = new string((n ?? "").Where(char.IsDigit).ToArray());
        return d.Length == 0 ? "O pedido" : "#" + d;
    }

    /// <summary>Texto que pode ir para a tela ou para o cliente: uma linha, sem travessão.</summary>
    public static bool TextoLimpo(string? t, int teto)
        => !string.IsNullOrWhiteSpace(t) && t.Length <= teto && !t.Contains('—') && !t.Contains('–');

    // ── O ESTADO DO SINAL (puro) ─────────────────────────────────────────────

    /// <summary>
    /// O <c>estado</c> do sinal (seção 4.9): só chaves e contagens, NUNCA texto nem id. O pedaço do
    /// SDK vem da página (dado não confiável) e passa por uma lista branca de campos e tipos.
    /// </summary>
    public static string EstadoDoSinal(string gestor, bool wsVivo, DateTime? ultimoQuadro, int fila,
        JsonObject? quadros, JsonObject? sdkDaPagina, IReadOnlyDictionary<string, int>? canaisPorMerchant)
    {
        var sdk = new JsonObject();
        if (sdkDaPagina is not null)
        {
            // 1.0.20: por onde achou (o módulo do webpack ou a árvore do React), quantos runtimes do
            // webpack e quantos módulos casaram com a assinatura do SDK. Só contagens, para o teste
            // na loja dizer por que o sdk.achou deu falso sem ninguém abrir o caixa.
            foreach (var k in new[] { "achou", "user_id_igual_ws", "tem_order_uuid", "via_webpack", "via_react" })
                if (sdkDaPagina.TryGetPropertyValue(k, out var v) && v is JsonValue jv && jv.TryGetValue<bool>(out var b))
                    sdk[k] = b;
            foreach (var k in new[] { "instancias", "congeladas", "conferidos", "modulos", "runtimes" })
                if (sdkDaPagina.TryGetPropertyValue(k, out var v) && v is JsonValue jv && jv.TryGetValue<int>(out var n))
                    sdk[k] = Math.Clamp(n, 0, 100_000);
        }
        if (canaisPorMerchant is not null)
        {
            var mapa = new JsonObject();
            foreach (var (k, v) in canaisPorMerchant.Take(20))
                if (Regex.IsMatch(k, "^[0-9a-f]{1,4}$")) mapa[k] = Math.Clamp(v, 0, 100_000);
            sdk["canais_cm_por_merchant"] = mapa;
        }
        var o = new JsonObject
        {
            ["gestor"] = gestor is "logado" or "login" or "ausente" ? gestor : "ausente",
            ["ws_vivo"] = wsVivo,
            ["ultimo_quadro_em"] = ultimoQuadro?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["fila"] = Math.Max(0, fila),
            ["quadros"] = quadros?.DeepClone() ?? new JsonObject(),
            ["sdk"] = sdk,
        };
        return o.ToJsonString(Json);
    }

    // ── O PAPEL (orquestração testável) ──────────────────────────────────────

    /// <summary>
    /// O cabeçalho que a resposta do <c>chat_mensagens</c> dá ao bônus: "normal" quando o ERP
    /// reservou a comanda para ESTE terminal (<c>comanda.imprimir_aqui</c>); null quando o papel é de
    /// outro (o KDS, ou outro caixa). Sem cabeçalho a comanda não sai sozinha daqui.
    /// </summary>
    public static string? CabecalhoDaResposta(RespostaConversa r)
        => r.Bonus is not null && r.ImprimirAqui ? CabecalhoNormal : null;

    /// <summary>
    /// Tira o papel de um bônus, na ordem que não deixa rastro errado (seção 4.3 e 9):
    ///  1. bônus do chat novo numa nova tentativa reserva de novo (a reserva vale 2 min); reserva
    ///     recusada pelo contrato (já impresso, reservada, cancelado) não tira papel;
    ///  2. o papel;
    ///  3. só DEPOIS do papel, o <c>impresso</c>; se o papel falhou, a reserva é liberada para outro.
    /// </summary>
    public static async Task<ResultadoPapel> TirarPapelAsync(BonusRaspadinha b, bool tentativaRepetida, string? quem,
        Func<Task<string?>> papel, Func<string, string, Task<(int Status, string? Corpo)>> funcao)
    {
        var doChatNovo = b.ComandaOnde is not null;
        if (doChatNovo && tentativaRepetida)
        {
            var (st, corpo) = await Seguro(funcao, CorpoComanda(AcaoReservar, b.Id, quem)).ConfigureAwait(false);
            var (d, motivo) = LerReserva(st, corpo);
            if (d == DesfechoReserva.Recusada) return new ResultadoPapel(DesfechoPapel.NaoEraMeu, motivo, false);
        }

        string? erro;
        try { erro = await papel().ConfigureAwait(false); }
        catch (Exception ex) { erro = ex.GetType().Name; }

        if (erro is null)
        {
            var (st, corpo) = await Seguro(funcao, CorpoImpresso(b.Id, quem)).ConfigureAwait(false);
            var avisado = st is >= 200 and < 300 && OkNoCorpo(corpo);
            return new ResultadoPapel(DesfechoPapel.Saiu, null, avisado);
        }
        if (doChatNovo) await Seguro(funcao, CorpoComanda(AcaoLiberar, b.Id, quem)).ConfigureAwait(false);
        return new ResultadoPapel(DesfechoPapel.NaoSaiu, erro, false);
    }

    private static async Task<(int, string?)> Seguro(Func<string, string, Task<(int Status, string? Corpo)>> f, string corpo)
    {
        try { return await f(Edge, corpo).ConfigureAwait(false); }
        catch { return (0, null); }
    }

    private static bool OkNoCorpo(string? corpo)
    {
        try
        {
            using var doc = JsonDocument.Parse(corpo ?? "");
            return doc.RootElement.ValueKind == JsonValueKind.Object && Bool(doc.RootElement, "ok");
        }
        catch { return false; }
    }

    // ── O BANCO LOCAL E A FILA ───────────────────────────────────────────────

    /// <summary>
    /// Grava a fala e a enfileira NA MESMA TRANSAÇÃO. Devolve false quando a fala já era conhecida
    /// (a chave é PK): duas capturas do mesmo quadro não viram duas linhas nem duas chamadas.
    /// </summary>
    public static bool Registrar(SqliteConnection cx, CanalDaFala c, FalaConversa f, DateTime agora)
    {
        using var tx = cx.BeginTransaction();
        var n = cx.Execute("""
            INSERT OR IGNORE INTO raspadinha_conversa_fala
                   (chave, canal, ifood_order_id, merchant_id, lado, autor_id, msg_id, texto, quando,
                    estado, tentativas, criado_em)
            VALUES (@K, @C, @O, @M, @L, @A, @I, @T, @Q, 'pendente', 0, @Em)
            """, new
        {
            K = f.Chave, C = c.Canal, O = c.OrderId, M = c.MerchantId,
            L = f.Lado == LadoFala.Loja ? "loja" : "cliente",
            A = f.AutorId, I = f.MsgId, T = CortarTexto(f.Texto),
            Q = f.Quando.ToString("o", CultureInfo.InvariantCulture), Em = agora.ToString("o", CultureInfo.InvariantCulture),
        }, tx);
        if (n == 0) { tx.Rollback(); return false; }
        Caixa.Enfileirar(cx, tx, TipoNaFila, c.Canal, f.Chave, new { canal = c.Canal });
        tx.Commit();
        return true;
    }

    /// <summary>As falas pendentes de um canal, da mais antiga para a mais nova (até 10).</summary>
    public static (CanalDaFala? Canal, IReadOnlyList<FalaConversa> Falas) Pendentes(SqliteConnection cx, string canal,
        IReadOnlyCollection<string>? soEstas = null)
    {
        var linhas = cx.Query("""
            SELECT chave, canal, ifood_order_id, merchant_id, lado, autor_id, msg_id, texto, quando
              FROM raspadinha_conversa_fala
             WHERE canal = @C AND estado = 'pendente'
             ORDER BY quando, chave
             LIMIT 50
            """, new { C = canal }).ToList();
        CanalDaFala? c = null;
        var falas = new List<FalaConversa>();
        foreach (var l in linhas)
        {
            var chave = (string)l.chave;
            if (soEstas is not null && !soEstas.Contains(chave)) continue;
            c ??= new CanalDaFala((string)l.canal, (string)l.ifood_order_id, (string)l.merchant_id);
            var quando = DateTimeOffset.TryParse(l.quando as string, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTimeOffset q) ? q : DateTimeOffset.Now;
            falas.Add(new FalaConversa(chave, l.msg_id as string, (l.lado as string) == "loja" ? LadoFala.Loja : LadoFala.Cliente,
                l.autor_id as string, (l.texto as string) ?? "", quando));
            if (falas.Count >= TetoFalas) break;
        }
        return (c, falas);
    }

    /// <summary>
    /// Aplica o desfecho às falas mandadas. Ok: entregue. Recusa: descartada. Nos dois o TEXTO SAI
    /// (a fala só ficava gravada para poder ser reenviada). Sem desfecho: continua pendente, com a
    /// tentativa contada. Bônus que veio na resposta é gravado aqui, na mesma conexão.
    /// </summary>
    public static void Aplicar(SqliteConnection cx, IReadOnlyCollection<string> chaves, RespostaConversa r, DateTime agora)
    {
        if (chaves.Count == 0) return;
        var estado = r.Status switch
        {
            StatusConversa.Ok => "enviada",
            StatusConversa.Recusado or StatusConversa.SemPermissao => "descartada",
            _ => null,
        };
        if (estado is null)
            cx.Execute("UPDATE raspadinha_conversa_fala SET tentativas = tentativas + 1 WHERE chave IN @K AND estado = 'pendente'",
                new { K = chaves });
        else
            cx.Execute("UPDATE raspadinha_conversa_fala SET estado = @E, texto = NULL WHERE chave IN @K AND estado = 'pendente'",
                new { E = estado, K = chaves });
        if (r.Status == StatusConversa.Ok && r.Bonus is { } b)
            GravarBonus(cx, b, CabecalhoDaResposta(r), agora);
    }

    /// <summary>
    /// Grava o bônus do chat novo na tabela local de bônus (é ela que vira papel). Com cabeçalho, o
    /// papel é deste terminal; sem ele, o bônus fica só para consulta e para o Reimprimir. Um bônus que
    /// já existia ganha o cabeçalho quando o sinal o entrega a este terminal como reserva do caixa.
    /// </summary>
    public static void GravarBonus(SqliteConnection cx, BonusRaspadinha b, string? cabecalho, DateTime agora)
    {
        var itens = b.Itens is { Count: > 0 } ? JsonSerializer.Serialize(b.Itens.Select(i => new { nome = i.Nome, qtd = i.Qtd }), Json) : null;
        cx.Execute("""
            INSERT OR IGNORE INTO raspadinha_bonus
                   (id, codigo, premio, premio_emoji, cliente, pedido, loja, ifood_order_id, origem, criado_em,
                    de_outro_terminal, itens_json, assinatura, comanda_onde, cabecalho)
            VALUES (@I, @C, @P, @E, @N, @D, @L, @O, @G, @Em, 0, @It, @As, @On, @Cab)
            """, new
        {
            I = b.Id, C = b.Codigo, P = b.Premio, E = b.PremioEmoji, N = b.Cliente, D = b.Pedido, L = b.Loja,
            O = b.IfoodOrderId, G = b.Origem, Em = (b.Quando == default ? agora : b.Quando).ToString("o", CultureInfo.InvariantCulture),
            It = itens, As = b.Assinatura, On = b.ComandaOnde, Cab = cabecalho,
        });
        if (cabecalho is not null)
            cx.Execute("""
                UPDATE raspadinha_bonus
                   SET cabecalho = @Cab,
                       comanda_onde = COALESCE(@On, comanda_onde),
                       itens_json = COALESCE(itens_json, @It),
                       assinatura = COALESCE(assinatura, @As),
                       de_outro_terminal = 0
                 WHERE id = @I AND impresso_em IS NULL
                """, new { I = b.Id, Cab = cabecalho, On = b.ComandaOnde, It = itens, As = b.Assinatura });
    }

    /// <summary>Grava o JSON do último sinal e a hora em que ele chegou.</summary>
    public static void GravarSinal(SqliteConnection cx, string? corpo, DateTime agora)
    {
        Vendas.GravarConfig(cx, ChaveSinal, corpo ?? "");
        Vendas.GravarConfig(cx, ChaveSinalEm, agora.ToString("o", CultureInfo.InvariantCulture));
    }

    /// <summary>O último sinal gravado. Ausente ou ilegível é desligado.</summary>
    public static (SinalConversa Sinal, DateTime? Em) SinalGravado(SqliteConnection cx)
    {
        try
        {
            var s = LerSinal(Vendas.Config(cx, ChaveSinal));
            var em = DateTime.TryParse(Vendas.Config(cx, ChaveSinalEm), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var d) ? d : (DateTime?)null;
            return (s, em);
        }
        catch { return (SinalConversa.Desligado, null); }
    }

    /// <summary>O chat novo está valendo nesta loja, pelo último sinal gravado?</summary>
    public static bool SinalAtivo(SqliteConnection cx) => SinalGravado(cx).Sinal.Ativo;

    /// <summary>
    /// Disparado quando a FILA (e não a tela) resolveu falas e o ERP respondeu: quem está vivo no
    /// caixa (ServicoConversaChat) trata saídas, avisos e papel do mesmo jeito.
    /// </summary>
    public static event Action<RespostaConversa>? ResolvidaNaFila;

    /// <summary>
    /// O que a Drenagem faz com uma linha <see cref="TipoNaFila"/>, no contrato da fila: (true)
    /// resolvido, (false) recusa permanente, (null) transitório. Manda TODAS as falas pendentes do
    /// canal (até 10) numa chamada só; reenviar é seguro porque o ERP deduplica pela chave e devolve
    /// o mesmo resultado (<c>repetida</c>). Fala de mais de 2 h vai assim mesmo: quem descarta é o
    /// servidor (<c>antiga</c>). Loja que desligou: as falas pendentes saem sem chamada.
    /// </summary>
    public static async Task<(bool? Ok, string? Erro)> ResolverNaFilaAsync(string chave,
        Func<string, string, Task<(int Status, string? Corpo)>> funcao, DateTime agora, string? terminal = null)
    {
        string corpo;
        IReadOnlyList<string> chaves;
        using (var cx = Banco.Abrir())
        {
            var linha = cx.QueryFirstOrDefault(
                "SELECT canal, estado, criado_em FROM raspadinha_conversa_fala WHERE chave = @K", new { K = chave });
            if (linha is null) return (true, "a fala local sumiu: nada a mandar");
            var estado = (string)linha.estado;
            if (estado == "enviada") return (true, "a fala já foi entregue");
            if (estado == "descartada") return (true, "a fala foi descartada");
            if (DateTime.TryParse(linha.criado_em as string, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                    out DateTime em) && em > agora - EsperaDaTela)
                return (null, "a tela ainda está juntando esta fala");

            var canal = (string)linha.canal;
            if (!SinalGravado(cx).Sinal.Ativo)
            {
                cx.Execute("UPDATE raspadinha_conversa_fala SET estado = 'descartada', texto = NULL WHERE canal = @C AND estado = 'pendente'",
                    new { C = canal });
                return (true, "o chat automático está desligado nesta loja");
            }
            var (c, falas) = Pendentes(cx, canal);
            if (c is null || falas.Count == 0) return (true, "nada pendente neste canal");
            (corpo, chaves) = CorpoMensagens(terminal, c, falas);
        }

        var (st, resp) = await funcao(Edge, corpo).ConfigureAwait(false);
        var r = LerResposta(st, resp, agora);
        using (var cx = Banco.Abrir()) Aplicar(cx, chaves.ToList(), r, agora);
        if (r.Status == StatusConversa.Ok)
            try { ResolvidaNaFila?.Invoke(r); } catch { /* quem escuta não derruba a fila */ }

        return r.Status switch
        {
            StatusConversa.Ok => (true, null),
            StatusConversa.Recusado => (true, $"o servidor recusou ({r.MotivoCru})"),
            StatusConversa.SemPermissao => (false, $"este caixa foi barrado ({r.MotivoCru})"),
            StatusConversa.NuvemSemRecurso => (null, $"a nuvem ainda não tem a função {Edge} (publicar a borda)"),
            _ => (null, $"sem desfecho ao mandar as falas: {r.MotivoCru}"),
        };
    }

    /// <summary>Quantas falas esperam a rede (para o diagnóstico do sinal).</summary>
    public static int Pendentes(SqliteConnection cx)
        => cx.ExecuteScalar<int>("SELECT COUNT(*) FROM raspadinha_conversa_fala WHERE estado = 'pendente'");

    /// <summary>Apaga a fala velha (2 dias), com desfecho ou sem: depois disso ela não prova nada.</summary>
    public static int Faxina(SqliteConnection cx, DateTime agora)
        => cx.Execute("DELETE FROM raspadinha_conversa_fala WHERE criado_em < @L",
            new { L = (agora - GuardaDaFala).ToString("o", CultureInfo.InvariantCulture) });

    // ── json ─────────────────────────────────────────────────────────────────

    private static string? Texto(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
           && v.GetString() is { } s && s.Trim().Length > 0 ? s.Trim() : null;

    private static string? TextoOuNumero(JsonElement e, string k)
        => e.TryGetProperty(k, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() is { } s && s.Trim().Length > 0 ? s.Trim() : null,
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static long? Numero(JsonElement e, string k)
    {
        if (!e.TryGetProperty(k, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var m)) return m;
        return null;
    }

    private static bool Bool(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTime? Data(JsonElement e, string k)
    {
        var s = Texto(e, k);
        if (s is null) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto)
            ? dto.LocalDateTime : null;
    }
}

// ── AS PEÇAS COM MEMÓRIA (todas com trava própria: a captura e a fila rodam em threads diferentes) ──

/// <summary>
/// Os canais que este caixa já sabe classificar: DE FORA (merchant de outra loja, 12 h) e EM
/// CONVERSA (o ERP abriu conversa nele, 2 h). Fala da loja só segue em canal em conversa.
/// </summary>
public sealed class CacheDeCanais
{
    public static readonly TimeSpan DeFora = TimeSpan.FromHours(12);
    public static readonly TimeSpan EmConversaPor = TimeSpan.FromHours(2);
    private const int Teto = 2000;

    private readonly object _trava = new();
    private readonly Dictionary<string, DateTime> _deFora = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _emConversa = new(StringComparer.Ordinal);

    public void MarcarDeFora(string canal, DateTime agora) { lock (_trava) Por(_deFora, canal, agora + DeFora, agora); }
    public void MarcarEmConversa(string canal, DateTime agora) { lock (_trava) Por(_emConversa, canal, agora + EmConversaPor, agora); }
    public bool EhDeFora(string canal, DateTime agora) { lock (_trava) return Vale(_deFora, canal, agora); }
    public bool EmConversa(string canal, DateTime agora) { lock (_trava) return Vale(_emConversa, canal, agora); }

    private static bool Vale(Dictionary<string, DateTime> d, string canal, DateTime agora)
        => d.TryGetValue(canal, out var ate) && ate > agora;

    private static void Por(Dictionary<string, DateTime> d, string canal, DateTime ate, DateTime agora)
    {
        if (d.Count >= Teto)
            foreach (var k in d.Where(x => x.Value <= agora).Select(x => x.Key).ToList()) d.Remove(k);
        if (d.Count >= Teto) d.Clear();
        d[canal] = ate;
    }
}

/// <summary>
/// O que ESTE caixa mandou, por canal, por 2 h (pelo hash do texto). Serve a duas coisas: o portão
/// não manda o mesmo texto duas vezes na mesma conversa, e a fala que volta igual ao que foi mandado
/// não vira fala do cliente.
/// </summary>
public sealed class MemoriaDeEco
{
    public static readonly TimeSpan Validade = TimeSpan.FromHours(2);
    private const int Teto = 2000;
    private readonly object _trava = new();
    private readonly Dictionary<string, DateTime> _visto = new(StringComparer.Ordinal);

    public void Registrar(string canal, string texto, DateTime agora)
    {
        lock (_trava)
        {
            if (_visto.Count >= Teto)
                foreach (var k in _visto.Where(x => x.Value <= agora - Validade).Select(x => x.Key).ToList()) _visto.Remove(k);
            if (_visto.Count >= Teto) _visto.Clear();
            _visto[Chave(canal, texto)] = agora;
        }
    }

    public bool Contem(string canal, string texto, DateTime agora)
    {
        lock (_trava)
            return _visto.TryGetValue(Chave(canal, texto), out var em) && em > agora - Validade;
    }

    private static string Chave(string canal, string texto) => canal + "|" + ConversaRaspadinha.HashTexto(texto);
}

/// <summary>Até 20 envios por minuto por terminal (seção 5). O script tem a mesma trava.</summary>
public sealed class LimiteDeEnvio
{
    public const int PorMinuto = 20;
    private readonly object _trava = new();
    private readonly Queue<DateTime> _envios = new();

    public bool Pode(DateTime agora)
    {
        lock (_trava) { Limpar(agora); return _envios.Count < PorMinuto; }
    }

    public void Registrar(DateTime agora)
    {
        lock (_trava) { Limpar(agora); _envios.Enqueue(agora); }
    }

    private void Limpar(DateTime agora)
    {
        while (_envios.Count > 0 && _envios.Peek() <= agora - TimeSpan.FromMinutes(1)) _envios.Dequeue();
    }
}

/// <summary>
/// Junta as falas do mesmo canal por até 6 s, no máximo 10 (seção 1.1). Só agrupa a CHAMADA: cada
/// fala continua com a sua chave, e a deduplicação é do ERP.
/// </summary>
public sealed class Agrupador
{
    public static readonly TimeSpan Janela = TimeSpan.FromSeconds(6);
    public const int Teto = ConversaRaspadinha.TetoFalas;

    private readonly object _trava = new();
    private readonly Dictionary<string, (DateTime Primeira, List<string> Chaves)> _lotes = new(StringComparer.Ordinal);

    /// <summary>Acrescenta a fala ao lote do canal. Devolve true quando o lote encheu (manda já).</summary>
    public bool Adicionar(string canal, string chave, DateTime agora)
    {
        lock (_trava)
        {
            if (!_lotes.TryGetValue(canal, out var lote)) _lotes[canal] = lote = (agora, new List<string>());
            if (!lote.Chaves.Contains(chave)) lote.Chaves.Add(chave);
            return lote.Chaves.Count >= Teto;
        }
    }

    /// <summary>Os lotes que já podem sair (cheios, ou com a primeira fala de 6 s atrás). Saem da memória.</summary>
    public IReadOnlyList<(string Canal, IReadOnlyList<string> Chaves)> Prontos(DateTime agora)
    {
        lock (_trava)
        {
            var prontos = _lotes.Where(l => l.Value.Chaves.Count >= Teto || agora - l.Value.Primeira >= Janela)
                .Select(l => (l.Key, (IReadOnlyList<string>)l.Value.Chaves.Take(Teto).ToList())).ToList();
            foreach (var (canal, chaves) in prontos)
            {
                var resto = _lotes[canal].Chaves.Skip(chaves.Count).ToList();
                if (resto.Count == 0) _lotes.Remove(canal);
                else _lotes[canal] = (agora, resto);
            }
            return prontos;
        }
    }

    public int Esperando { get { lock (_trava) return _lotes.Sum(l => l.Value.Chaves.Count); } }
}

/// <summary>
/// A confirmação do envio PELO QUADRO (seção 0, decisão 9): depois que o SDK aceitou, espera até
/// 15 s pelo quadro que sai da página com o mesmo texto no mesmo canal, ou pela volta dele com o
/// <c>msg_id</c>. Sem nenhum dos dois vira "incerta", e incerta NUNCA é reenviada.
/// </summary>
public sealed class ConfirmacaoDeEnvio
{
    public static readonly TimeSpan Prazo = TimeSpan.FromSeconds(15);
    private readonly object _trava = new();
    private readonly Dictionary<long, (string Canal, string Hash, DateTime Ate, bool VistoSaindo)> _esperando = new();

    public void Aguardar(long saidaId, string canal, string texto, DateTime agora)
    {
        lock (_trava) _esperando[saidaId] = (canal, ConversaRaspadinha.HashTexto(texto), agora + Prazo, false);
    }

    public bool Pendente(long saidaId) { lock (_trava) return _esperando.ContainsKey(saidaId); }

    /// <summary>
    /// Tira a saída da espera porque o script disse que NÃO mandou. Devolve null quando ela já não
    /// esperava (o quadro confirmou antes, ou venceu: vale o que já foi relatado); senão, se o
    /// quadro dela foi visto SAINDO da página. Visto saindo e o script dizendo erro é dúvida, e
    /// dúvida vira "incerta", nunca reenvio.
    /// </summary>
    public bool? Cancelar(long saidaId)
    {
        lock (_trava)
        {
            if (!_esperando.Remove(saidaId, out var e)) return null;
            return e.VistoSaindo;
        }
    }

    /// <summary>
    /// Um quadro passou. O que SAI da página marca a saída como vista; o que VOLTA com o id da
    /// mensagem confirma na hora e devolve o id do Sendbird.
    /// </summary>
    public (long SaidaId, string? MsgId)? Ver(MensagemSendbird? m, bool enviado)
    {
        if (m is null || m.Canal is null) return null;
        var hash = ConversaRaspadinha.HashTexto(m.Texto);
        lock (_trava)
        {
            foreach (var (id, e) in _esperando.ToList())
            {
                if (e.Canal != m.Canal || e.Hash != hash) continue;
                if (enviado) { _esperando[id] = e with { VistoSaindo = true }; return null; }
                _esperando.Remove(id);
                return (id, m.MsgId);
            }
        }
        return null;
    }

    /// <summary>As que passaram do prazo: vistas saindo viram "enviada" sem id; as outras, "incerta".</summary>
    public IReadOnlyList<(long SaidaId, bool VistoSaindo)> Vencidas(DateTime agora)
    {
        lock (_trava)
        {
            var v = _esperando.Where(x => x.Value.Ate <= agora).Select(x => (x.Key, x.Value.VistoSaindo)).ToList();
            foreach (var (id, _) in v) _esperando.Remove(id);
            return v;
        }
    }
}

/// <summary>Por que o portão segurou uma resposta.</summary>
public enum MotivoPortao
{
    Liberado, Sombra, Operador, NaoReservada, SemSinal, SinalVelho, EnvioNaoSdk, Pausado,
    ReservaVencida, CanalInvalido, TextoInvalido, Eco, Limite,
}

/// <summary>
/// A licença para UM envio pelo SDK. Só o <see cref="PortaoDeEnvio"/> cria (construtor interno), e a
/// tela só sabe enviar recebendo uma destas: o envio não tem como acontecer fora do portão. O token
/// sai uma vez só.
/// </summary>
public sealed class PermissaoDeEnvio
{
    private string? _token;

    internal PermissaoDeEnvio(SaidaConversa saida, string canal, string orderUuid, string token)
    {
        Saida = saida; Canal = canal; OrderUuid = orderUuid; _token = token;
    }

    public SaidaConversa Saida { get; }
    public string Canal { get; }
    public string OrderUuid { get; }
    public string Texto => Saida.Texto;

    /// <summary>O token de uso único. A segunda chamada devolve null.</summary>
    public string? ConsumirToken() => Interlocked.Exchange(ref _token, null);
}

/// <summary>
/// O PORTÃO DE ENVIO (seção 5 e 7). Nada sai para o cliente sem passar aqui:
///  · sombra e operador nunca chamam o script;
///  · só a saída "sdk" reservada a este terminal, e dentro do prazo da reserva;
///  · o sinal tem de ser legível, de menos de 5 min, sem pausa e com envio "sdk";
///  · o canal tem de ser de pedido do iFood e bater com o pedido da saída;
///  · o texto não pode ter travessão nem passar do teto;
///  · o mesmo texto não vai duas vezes na mesma conversa (eco, 2 h);
///  · no máximo 20 por minuto.
/// </summary>
public sealed class PortaoDeEnvio
{
    public static readonly TimeSpan SinalValido = TimeSpan.FromMinutes(5);
    /// <summary>A reserva do ERP vale 60 s; o caixa desiste antes, para não mandar fora dela.</summary>
    public static readonly TimeSpan PrazoDaReserva = TimeSpan.FromSeconds(50);

    private readonly MemoriaDeEco _eco;
    private readonly LimiteDeEnvio _limite;
    private readonly object _trava = new();

    public PortaoDeEnvio(MemoriaDeEco eco, LimiteDeEnvio limite) { _eco = eco; _limite = limite; }

    public (PermissaoDeEnvio? Permissao, MotivoPortao Motivo) Decidir(SaidaConversa s, SinalConversa sinal,
        DateTime? sinalEm, DateTime recebidaEm, DateTime agora)
    {
        if (s.Como == ConversaRaspadinha.ComoSombra) return (null, MotivoPortao.Sombra);
        if (s.Como == ConversaRaspadinha.ComoOperador) return (null, MotivoPortao.Operador);
        if (s.Como != ConversaRaspadinha.ComoSdk || s.Estado != "reservada") return (null, MotivoPortao.NaoReservada);
        if (!sinal.Legivel || sinalEm is not { } em) return (null, MotivoPortao.SemSinal);
        if (agora - em > SinalValido || em > agora + ConversaRaspadinha.ToleranciaDeRelogio) return (null, MotivoPortao.SinalVelho);
        if (!sinal.Ativo || sinal.Envio != ConversaRaspadinha.ComoSdk) return (null, MotivoPortao.EnvioNaoSdk);
        if (sinal.Pausado is not null) return (null, MotivoPortao.Pausado);
        if (agora - recebidaEm > PrazoDaReserva) return (null, MotivoPortao.ReservaVencida);
        if (CanalIfood.Ler(s.Canal) is not { } ids
            || (s.IfoodOrderId is { Length: > 0 } o && !string.Equals(o, ids.OrderId, StringComparison.OrdinalIgnoreCase)))
            return (null, MotivoPortao.CanalInvalido);
        if (!ConversaRaspadinha.TextoLimpo(s.Texto, ConversaRaspadinha.TetoTexto)) return (null, MotivoPortao.TextoInvalido);
        lock (_trava)
        {
            if (_eco.Contem(s.Canal!, s.Texto, agora)) return (null, MotivoPortao.Eco);
            if (!_limite.Pode(agora)) return (null, MotivoPortao.Limite);
            _eco.Registrar(s.Canal!, s.Texto, agora);
            _limite.Registrar(agora);
        }
        return (new PermissaoDeEnvio(s, s.Canal!, ids.OrderId, NovoToken()), MotivoPortao.Liberado);
    }

    public static string NovoToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// 1.0.25 (08/10/2026): a segunda ou terceira tentativa da MESMA saida, depois de o script dizer
    /// que nao mandou (canal_errado/erro_envio com o SDK frio). Na 1.0.24 a tentativa passava de
    /// novo por <see cref="Decidir"/>, e o eco registrado pela primeira decisao a barrava como
    /// repetida: toda mensagem do pedido virou "incerta/eco" sem sair (17:15 as 17:30 na Savassi).
    /// Aqui: token novo para a mesma saida, sem a trava do eco (o eco e desta propria saida) e sem
    /// contar de novo no limite; o prazo da reserva continua valendo (null = reserva vencida).
    /// </summary>
    public PermissaoDeEnvio? Repetir(PermissaoDeEnvio anterior, DateTime recebidaEm, DateTime agora)
    {
        if (agora - recebidaEm > PrazoDaReserva) return null;
        return new PermissaoDeEnvio(anterior.Saida, anterior.Canal, anterior.OrderUuid, NovoToken());
    }

    /// <summary>
    /// O que dizer ao ERP sobre uma saída "sdk" reservada que o portão segurou. Eco e reserva vencida
    /// viram "incerta" (não manda de novo); o resto vira "falhou", e o ERP passa a saída para a pessoa.
    /// Sombra e operador não têm o que dizer.
    /// </summary>
    public static (string Resultado, string Erro)? Relato(MotivoPortao m) => m switch
    {
        MotivoPortao.Liberado or MotivoPortao.Sombra or MotivoPortao.Operador or MotivoPortao.NaoReservada => null,
        MotivoPortao.Eco => ("incerta", "eco"),
        MotivoPortao.ReservaVencida => ("incerta", "reserva_vencida"),
        _ => ("falhou", "portao_" + m.ToString().ToLowerInvariant()),
    };
}
