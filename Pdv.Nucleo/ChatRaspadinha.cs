using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>O que aconteceu com UMA mensagem do cliente, do ponto de vista do caixa.</summary>
public enum DesfechoChat
{
    /// <summary>O servidor registrou (ou devolveu) o bônus: sai comanda.</summary>
    Bonus,
    /// <summary>Não havia código na mensagem. Silêncio: nem papel, nem aviso.</summary>
    SemCodigo,
    /// <summary>Havia código e ele não vale (outra loja, vencido, já usado, inexistente).</summary>
    Recusado,
    /// <summary>Nada saiu do caixa (sem sessão, sem conexão): fica na fila.</summary>
    SemRede,
    /// <summary>Saiu e a resposta se perdeu: fica na fila. O servidor é idempotente.</summary>
    NaoConfirmou,
    /// <summary>A função de borda ainda não está no ar: fica na fila esperando o deploy.</summary>
    NuvemSemRecurso,
    /// <summary>O servidor barrou este caixa (401/403): não adianta insistir.</summary>
    SemPermissao,
    /// <summary>A loja não ligou a captura: nada é mandado.</summary>
    Desligado,
}

/// <summary>Por que o servidor recusou. A tela traduz em uma linha.</summary>
public enum MotivoChat
{
    Nenhum,
    /// <summary>sem_codigo: o texto do cliente não tinha código nenhum.</summary>
    SemCodigo,
    NaoAchou,
    NaoRaspou,
    Venceu,
    JaUsado,
    OutraLoja,
    LojaDesligada,
    SemPermissao,
    Desconhecido,
}

/// <summary>
/// O bônus que o SERVIDOR registrou (raspadinha_bonus_pedido). É isto, e só isto, que vira
/// comanda: o caixa não inventa prêmio nem decide código.
/// </summary>
public sealed record BonusRaspadinha(
    string Id, string? Codigo, string? Premio, string? PremioEmoji, string? Cliente,
    string? Pedido, string? Loja, string? IfoodOrderId, string Origem, DateTime Quando);

/// <summary>A resposta da borda, já no vocabulário do caixa.</summary>
public sealed record RespostaChat(DesfechoChat Desfecho, MotivoChat Motivo, string? MotivoCru,
    BonusRaspadinha? Bonus, bool JaRegistrado, string? Mensagem = null);

/// <summary>
/// Uma mensagem do cliente pronta para virar chamada: a chave estável (para não repetir),
/// o texto e o contexto que deu para saber.
/// </summary>
public sealed record MensagemDoChat(string Chave, string Texto, string? Loja, string? Pedido,
    string? IfoodOrderId, string? Cliente, string Origem);

/// <summary>
/// CÓDIGO DA RASPADINHA NO CHAT DO iFOOD (22/09/2026, pedido do dono).
///
/// "Capturar quando o cliente enviar o código de resgate da raspadinha, validar e gerar uma
/// comanda avisando do bônus." Dois caminhos capturam: o caixa (esta classe) e a extensão do
/// Chrome. Os dois fazem a MESMA coisa: mandam o texto cru e agem só pela resposta.
///
/// ⚠️ O SERVIDOR DECIDE. Aqui não se procura código no texto, não se valida formato, não se
/// resgata nada. Uma mensagem nova do cliente vira UMA chamada da borda `raspadinha-chat` com o
/// texto e o contexto; o que volta é que diz se há bônus. Isso é de propósito: a regra do código
/// (formato, loja, validade, idempotência) mora num lugar só, e caixa desatualizado não vira
/// uma segunda regra divergente. Foi assim que a promoção de 30% escapou na Savassi.
///
/// O QUE MORA AQUI, e por quê:
///  · a CHAVE da mensagem (para a mesma mensagem não virar duas chamadas nem duas comandas);
///  · quem é o CLIENTE e quem é a LOJA (a loja fala no mesmo chat, e o que ela escreve não vai);
///  · o corpo da chamada e a leitura da resposta;
///  · as linhas da comanda e a linha da tela;
///  · a fila local, para a mensagem capturada sem internet sair quando a rede voltar.
///
/// SEM INTERNET A MENSAGEM NÃO SE PERDE. Ela é gravada e enfileirada ANTES da chamada, na mesma
/// transação (o desenho do brinde, 21/09): se o caixa cair no meio, a linha já existe e a fila
/// descobre o desfecho. Reenviar é seguro porque o servidor é idempotente por resgate, e porque
/// a comanda nasce da tabela local de bônus, com uma linha por bônus.
///
/// DESLIGADO NA LOJA = NADA É MANDADO. A chave vem do painel (pdv_loja_config), como a
/// "raspadinha no caixa". Config ausente é desligado.
/// </summary>
public sealed class ChatRaspadinha
{
    /// <summary>A função de borda que confere e registra. Ver supabase/functions/raspadinha-chat.</summary>
    public const string Edge = "raspadinha-chat";

    /// <summary>Tipo da linha na fila (Drenagem.TiposComHandler).</summary>
    public const string TipoNaFila = "raspadinha_chat";

    /// <summary>Config por loja que liga a captura (vem do painel, como a do brinde no caixa).</summary>
    public const string ChaveConfigLoja = "raspadinha_chat_caixa";

    public const string OrigemCaixa = "caixa";
    public const string OrigemExtensao = "extensao";

    /// <summary>Teto do texto que sai daqui. O servidor corta de novo; este é o do caixa.</summary>
    public const int TetoTexto = 400;

    public static readonly TimeSpan Prazo = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Quanto a fila espera por uma linha que a TELA ainda está enviando. Mesma regra do brinde:
    /// passado isto sem desfecho, o caixa caiu no meio e a fila assume.
    /// </summary>
    public static readonly TimeSpan EsperaDaTela = TimeSpan.FromMinutes(2);

    private readonly Nuvem _nuvem;

    public ChatRaspadinha(Nuvem nuvem) => _nuvem = nuvem;

    // ── A LOJA LIGOU? ────────────────────────────────────────────────────────

    /// <summary>A loja ligou a captura do código no chat (pdv_loja_config, copiada no Atualizar)?</summary>
    public static bool LigadoNaLoja(SqliteConnection cx) => Vendas.Config(cx, ChaveConfigLoja) == "1";

    // ── QUEM FALOU, E É NOVO? (puro) ─────────────────────────────────────────

    private static readonly Regex Espacos = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// O texto como ele viaja: uma linha só, sem espaço dobrado, cortado no teto. Mensagem sem
    /// texto nenhum devolve "" (e aí não vira chamada).
    /// </summary>
    public static string CortarTexto(string? texto)
    {
        var t = Espacos.Replace(texto ?? "", " ").Trim();
        return t.Length <= TetoTexto ? t : t[..TetoTexto];
    }

    /// <summary>
    /// A CHAVE da mensagem: a mesma mensagem, vista duas vezes, dá a mesma chave e só vira UMA
    /// chamada. Nasce da conversa, de quem escreveu, do instante (em segundos) e do texto.
    ///
    /// Por que segundos e não milissegundos: o quadro do WebSocket traz o instante cheio, o DOM
    /// traz "14:32". Os dois caminhos não empatam sempre, e por isso a chave NÃO é a única
    /// defesa: quem impede a comanda dobrada é a tabela de bônus, com uma linha por bônus.
    /// </summary>
    public static string Chave(MensagemChat m)
    {
        var bruto = string.Join('',
            (m.ConversaId ?? "").Trim(),
            (m.Autor ?? "").Trim(),
            m.Quando?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "",
            CortarTexto(m.Texto));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bruto)), 0, 12).ToLowerInvariant();
    }

    /// <summary>
    /// A mensagem é DO CLIENTE (não da loja)? A loja escreve no mesmo chat, e o que ela escreve
    /// não pode virar chamada.
    ///
    /// NA DÚVIDA, NÃO ENTRA (22/09/2026). São só dois jeitos de saber, e é preciso UM deles:
    ///  · o protocolo diz o lado (mine/isMine/fromMe/outgoing): true é a loja, false é o cliente;
    ///  · dá para comparar quem escreveu com o usuário DESTA loja (o claim do token capturado).
    /// Sem nenhum dos dois a mensagem fica de fora. O campo era <c>bool</c> e o desconhecido caía
    /// em false ("é do cliente"): com o PDV reiniciado e a sessão do Gestor já quente o token nunca
    /// é refeito, o usuário da loja fica desconhecido o dia inteiro e TODO quadro recebido passava
    /// a virar chamada, inclusive o eco do que a própria loja manda e as respostas prontas. É a
    /// mesma regra que o DOM já aplicava (balão sem lado decidido fica de fora).
    ///
    /// O id do usuário vem do JWT capturado e fica só em memória: não é gravado nem enviado.
    /// </summary>
    public static bool DoCliente(MensagemChat? m, string? meuUserId)
    {
        if (m is null || CortarTexto(m.Texto).Length == 0) return false;
        if (m.Minha == true) return false;          // a loja falou
        if (m.Minha == false) return true;          // o protocolo decidiu: não foi a loja
        // lado desconhecido: só entra se der para comparar o autor com o usuário desta loja
        return !string.IsNullOrWhiteSpace(meuUserId) && !string.IsNullOrWhiteSpace(m.Autor)
            && !string.Equals(m.Autor.Trim(), meuUserId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // ── E É DE AGORA? (puro) ─────────────────────────────────────────────────

    /// <summary>
    /// Quanto tempo uma fala do cliente continua valendo como "acabou de chegar". Passado isto, ela
    /// não vira resgate: ver <see cref="Recente"/>.
    /// </summary>
    public static readonly TimeSpan JanelaDeRecencia = TimeSpan.FromMinutes(30);

    /// <summary>Folga para relógio do caixa adiantado em relação ao do chat.</summary>
    public static readonly TimeSpan ToleranciaDeRelogio = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A fala é DE AGORA? Nada no caixa comparava a idade da mensagem com o relógio, e o leitor de
    /// DOM manda a rolagem VISÍVEL inteira: abrir hoje uma conversa de três dias atrás mandava para
    /// a borda um código que ninguém tratou na época, o servidor RESGATAVA e saía comanda de um
    /// pedido que foi embora. A raspadinha vive 14 dias, então o código velho continua válido e o
    /// prazo da pessoa era queimado sem ela receber nada.
    ///
    /// Regra conservadora, a mesma da autoria: instante DESCONHECIDO não entra. Instante no futuro
    /// além da folga de relógio também não: é sinal de hora de outro dia ancorada em hoje.
    /// </summary>
    public static bool Recente(MensagemChat? m, DateTime agora)
    {
        if (m?.Quando is not { } q) return false;
        var quando = q.LocalDateTime;
        return quando <= agora + ToleranciaDeRelogio && quando >= agora - JanelaDeRecencia;
    }

    // ── O QUE VAI (puro) ─────────────────────────────────────────────────────

    /// <summary>
    /// O corpo da chamada da borda. Contexto que o caixa não sabe vai NULO de propósito: o
    /// servidor liga ao pedido pelo que der (número mandado, senão o pedido aberto mais recente
    /// daquela loja com o mesmo cliente). Mandar palpite daqui seria decidir no caixa.
    /// </summary>
    public static string CorpoDoPedido(string? texto, string? loja, string? pedido,
        string? ifoodOrderId, string? cliente, string? origem)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["texto"] = CortarTexto(texto),
            ["loja"] = Vazio(loja),
            ["pedido"] = Vazio(pedido),
            ["ifood_order_id"] = Vazio(ifoodOrderId),
            ["cliente"] = Vazio(cliente),
            ["origem"] = origem == OrigemExtensao ? OrigemExtensao : OrigemCaixa,
        });

    private static string? Vazio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ── O QUE VOLTA (puro) ───────────────────────────────────────────────────

    /// <summary>O motivo da recusa no tipo do caixa. Aceita os nomes em inglês do validador web.</summary>
    public static MotivoChat LerMotivo(string? cru) => (cru ?? "").Trim().ToLowerInvariant() switch
    {
        "" => MotivoChat.Desconhecido,
        "sem_codigo" or "no_code" or "code_required" or "codigo_ausente" => MotivoChat.SemCodigo,
        "not_found" or "nao_encontrado" or "inexistente" or "formato_invalido" or "invalid_format"
            => MotivoChat.NaoAchou,
        "not_scratched" or "nao_raspado" => MotivoChat.NaoRaspou,
        "expired" or "vencido" or "venceu" => MotivoChat.Venceu,
        "already_redeemed" or "ja_resgatado" or "ja_usado" => MotivoChat.JaUsado,
        "outra_loja" or "loja_diferente" or "wrong_store" => MotivoChat.OutraLoja,
        "loja_sem_raspadinha" or "loja_desligada" or "chat_desligado" => MotivoChat.LojaDesligada,
        "sem_permissao" or "forbidden" or "unauthorized" => MotivoChat.SemPermissao,
        _ => MotivoChat.Desconhecido,
    };

    /// <summary>
    /// A resposta da borda. Status: -1 = nada saiu do caixa (certeza de que o servidor não fez
    /// nada), 0 = saiu e ficou sem resposta (pode ter feito). A diferença importa para a fila.
    ///
    /// 404 é a borda ainda não publicada: espera o deploy, não é recusa. 401/403 é o caixa
    /// barrado: não adianta insistir. 5xx, 408, 425 e 429 são "tente depois".
    /// </summary>
    public static RespostaChat LerResposta(int status, string? corpo, DateTime agora)
    {
        if (status == -1) return Falha(DesfechoChat.SemRede, "nada saiu do caixa");
        if (status == 404) return Falha(DesfechoChat.NuvemSemRecurso, "a borda ainda não está no ar");
        if (status is 401 or 403) return Falha(DesfechoChat.SemPermissao, $"HTTP {status}", MotivoChat.SemPermissao);
        if (status == 0 || status >= 500 || status is 408 or 425 or 429)
            return Falha(DesfechoChat.NaoConfirmou, status == 0 ? "sem resposta" : $"HTTP {status}");

        try
        {
            using var doc = JsonDocument.Parse(corpo ?? "");
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0) r = r[0];
            if (r.ValueKind != JsonValueKind.Object) return Falha(DesfechoChat.NaoConfirmou, "resposta sem objeto");

            var ok = r.TryGetProperty("ok", out var okv) && okv.ValueKind == JsonValueKind.True;
            var ja = (r.TryGetProperty("ja_registrado", out var jv) && jv.ValueKind == JsonValueKind.True)
                     || (r.TryGetProperty("idempotente", out var iv) && iv.ValueKind == JsonValueKind.True);
            var mensagem = Texto(r, "mensagem") ?? Texto(r, "frase");

            if (!ok)
            {
                var cru = Texto(r, "motivo") ?? Texto(r, "erro") ?? Texto(r, "error");
                var mt = LerMotivo(cru);
                return new RespostaChat(mt == MotivoChat.SemCodigo ? DesfechoChat.SemCodigo : DesfechoChat.Recusado,
                    mt, cru, null, false, mensagem);
            }

            var b = LerBonus(r, agora);
            // ok sem bônus: o servidor não achou o que registrar. Mesmo silêncio do sem_codigo.
            return b is null
                ? new RespostaChat(DesfechoChat.SemCodigo, MotivoChat.SemCodigo, "ok_sem_bonus", null, ja, mensagem)
                : new RespostaChat(DesfechoChat.Bonus, MotivoChat.Nenhum, null, b, ja, mensagem);
        }
        catch
        {
            // Corpo ilegível num 2xx: o servidor PODE ter registrado. Não é recusa.
            return status is >= 200 and < 300
                ? Falha(DesfechoChat.NaoConfirmou, "resposta ilegível")
                : Falha(DesfechoChat.Recusado, $"HTTP {status}", MotivoChat.Desconhecido);
        }

        static RespostaChat Falha(DesfechoChat d, string cru, MotivoChat m = MotivoChat.Desconhecido)
            => new(d, m, cru, null, false);
    }

    /// <summary>
    /// O bônus do corpo da resposta. Aceita tanto <c>{"bonus":{...}}</c> quanto os campos na
    /// raiz. Sem id nem código não há o que gravar: devolve null (e o caixa fica em silêncio).
    /// </summary>
    private static BonusRaspadinha? LerBonus(JsonElement r, DateTime agora)
    {
        var b = r.TryGetProperty("bonus", out var bv) && bv.ValueKind == JsonValueKind.Object ? bv : r;
        var codigo = Texto(b, "redeem_code") ?? Texto(b, "codigo");
        var id = Texto(b, "id") ?? Texto(b, "bonus_id") ?? Texto(b, "scratch_id") ?? codigo;
        if (id is null) return null;
        return new BonusRaspadinha(
            id,
            codigo,
            Texto(b, "premio_nome") ?? Texto(b, "premio") ?? Texto(b, "prize_name"),
            Texto(b, "premio_emoji") ?? Texto(b, "prize_emoji"),
            PrimeiroNome(Texto(b, "cliente_nome") ?? Texto(b, "cliente") ?? Texto(b, "customer_name")),
            Texto(b, "pedido_numero") ?? Texto(b, "pedido") ?? Texto(b, "display_id"),
            Texto(b, "loja") ?? Texto(b, "store"),
            Texto(b, "ifood_order_id") ?? Texto(b, "order_id"),
            Texto(b, "origem") == OrigemExtensao ? OrigemExtensao : OrigemCaixa,
            Data(b, "criado_em") ?? Data(b, "created_at") ?? agora);
    }

    // ── A COMANDA (pura) ─────────────────────────────────────────────────────

    /// <summary>
    /// A comanda do brinde, no contrato de <c>Impressao.ImprimirTextoAsync</c> e no mesmo estilo
    /// da comanda de cozinha: moldura, o que importa em tamanho grande e nada além disso.
    ///
    /// Quem pega este papel precisa de quatro coisas e nenhuma a mais: QUAL é o prêmio, PARA
    /// QUEM, de QUE pedido e QUE código foi queimado (é o que o gerente confere depois).
    ///
    /// Largura: passe <see cref="Kds.ColunasComanda"/> da bobina que vai imprimir, como a comanda
    /// de cozinha faz. Em 58 mm o texto encolhe junto, em vez de sair cortado.
    /// </summary>
    public static IReadOnlyList<string> ComandaLinhas(BonusRaspadinha b, int colunas = Kds.ColunasPadrao,
        DateTime? hoje = null)
    {
        var L = Kds.ColunasComanda(colunas);
        var agora = hoje ?? DateTime.Now;
        var premio = string.IsNullOrWhiteSpace(b.Premio) ? "BRINDE" : b.Premio!.Trim().ToUpperInvariant();
        var linhas = new List<string>
        {
            new string('=', L),
            LinhaEscala.Com(Centro("BRINDE DA RASPADINHA", L), 1.2),
            Centro("iFOOD", L),
            new string('=', L),
            "",
        };
        // O PRÊMIO em 2x, como o número do pedido na comanda de cozinha: é o que a pessoa lê de
        // longe, com a sacola na mão. Nome comprido quebra em vez de sumir cortado.
        foreach (var parte in Quebra(premio, L))
            linhas.Add(LinhaEscala.Com(Centro(parte, L), 2.0));
        linhas.Add("");
        linhas.Add(new string('-', L));
        if (!string.IsNullOrWhiteSpace(b.Cliente))
            linhas.Add(LinhaEscala.Com(Corta("Cliente: " + b.Cliente!.Trim(), L), 1.3));
        // O NÚMERO DO PEDIDO É O QUE FAZ O PAPEL SERVIR NO BALCÃO. Com oito sacolas prontas na
        // bancada, comanda sem número não diz em qual colocar o brinde. Quando ele não veio (o
        // caminho do WebSocket não passa pelo cabeçalho da conversa), a linha DIZ isso, em vez de
        // sumir e deixar quem pegou o papel sem saber que falta uma informação.
        linhas.Add(string.IsNullOrWhiteSpace(b.Pedido)
            ? LinhaEscala.Com(Corta("Pedido: confira no chat", L), 1.3)
            : LinhaEscala.Com(Corta("Pedido: #" + b.Pedido!.Trim().TrimStart('#'), L), 1.5));
        if (!string.IsNullOrWhiteSpace(b.Codigo))
            linhas.Add(Corta("Codigo: " + b.Codigo!.Trim(), L));
        linhas.Add(Corta("Hora: " + agora.ToString("HH:mm"), L));
        linhas.Add(new string('-', L));
        linhas.Add(Corta("Entregar junto com o pedido.", L));
        linhas.Add("");
        return linhas;
    }

    private static string Centro(string s, int larg)
        => s.Length >= larg ? s[..larg] : s.PadLeft((larg + s.Length) / 2).PadRight(larg);

    private static string Corta(string s, int larg) => s.Length <= larg ? s : s[..(larg - 1)] + "…";

    private static IEnumerable<string> Quebra(string s, int larg)
    {
        if (s.Length == 0) { yield return s; yield break; }
        for (var i = 0; i < s.Length; i += larg)
            yield return s.Substring(i, Math.Min(larg, s.Length - i));
    }

    // ── TEXTOS DE TELA (uma linha cada) ──────────────────────────────────────

    /// <summary>"Brinde da raspadinha: Cookie Clássico para Maria, pedido #5592".</summary>
    public static string LinhaDoBonus(BonusRaspadinha b)
    {
        var premio = $"{b.PremioEmoji} {b.Premio}".Trim();
        var sb = new StringBuilder("Brinde da raspadinha: ");
        sb.Append(premio.Length > 0 ? premio : "brinde");
        if (!string.IsNullOrWhiteSpace(b.Cliente)) sb.Append(" para ").Append(b.Cliente!.Trim());
        if (!string.IsNullOrWhiteSpace(b.Pedido)) sb.Append(", pedido #").Append(b.Pedido!.Trim().TrimStart('#'));
        return sb.ToString();
    }

    /// <summary>
    /// O que o operador lê. Uma linha, sem código de erro e sem jargão. Silêncio (null) quando
    /// não havia código na mensagem: é a conversa normal do cliente, e avisar seria ruído.
    /// </summary>
    public static string? TextoDeTela(RespostaChat r) => r.Desfecho switch
    {
        DesfechoChat.Bonus => LinhaDoBonus(r.Bonus!),
        DesfechoChat.SemCodigo => null,
        DesfechoChat.Desligado => null,
        DesfechoChat.SemRede => "Chegou um código no chat e ainda não deu para conferir. Sai quando a internet voltar.",
        DesfechoChat.NaoConfirmou => "Chegou um código no chat e ainda não deu para conferir. Sai quando a internet voltar.",
        DesfechoChat.NuvemSemRecurso => null,
        DesfechoChat.SemPermissao => "Este caixa não pode conferir código do chat. Fale com o gerente.",
        _ => r.Motivo switch
        {
            MotivoChat.NaoAchou => "O cliente mandou um código que eu não achei.",
            MotivoChat.NaoRaspou => "O cliente mandou um código que ele ainda não raspou.",
            MotivoChat.Venceu => "O cliente mandou um código que já venceu.",
            MotivoChat.JaUsado => "O cliente mandou um código que já foi usado.",
            // NÃO ACUSA O CLIENTE. A loja mandada na chamada é a do TERMINAL, e com a mesma conta
            // do Gestor enxergando as duas lojas o código bom do Castelo chega aqui julgado contra
            // a Savassi: quem está na loja errada é o caixa, não a pessoa que mandou o código.
            MotivoChat.OutraLoja => "Este código é de outra loja da rede. Confira em qual loja você está.",
            MotivoChat.LojaDesligada => null,
            _ => DoServidor(r.Mensagem) ?? "Chegou um código no chat e não deu para conferir agora.",
        },
    };

    /// <summary>
    /// A frase do servidor, só para motivo que esta versão não conhece, e só se ela obedece a
    /// regra da tela: uma linha curta, sem travessão. Senão, o texto de sempre do caixa.
    /// </summary>
    private static string? DoServidor(string? m)
        => m is { Length: > 0 and <= 90 } && !m.Contains('\n') && !m.Contains('—') && !m.Contains('–') ? m : null;

    // ── AS MENSAGENS QUE A TELA VÊ (puro) ────────────────────────────────────

    /// <summary>
    /// O que a página manda quando o chat está aberto: a conversa visível, o pedido e o cliente
    /// que dá para ler no cabeçalho, e as falas.
    /// </summary>
    /// <summary>
    /// ⚠️ NÃO HÁ A LOJA DA CONVERSA AQUI. A loja mandada na chamada continua sendo a do TERMINAL,
    /// e a mesma conta do Gestor enxerga Savassi e Castelo. Ler a loja do cabeçalho exigiria um
    /// seletor do Gestor que ninguém mediu, e defesa que não sabe medir é decoração. O que o caixa
    /// faz de verdade contra a troca de loja está em dois lugares: o número do pedido só vai quando
    /// ESTA loja tem esse pedido (ServicoRaspadinhaChat.PedidoDestaLoja), e a frase do motivo
    /// outra_loja não acusa o cliente.
    /// </summary>
    public sealed record LeituraDoDom(string? ConversaId, string? Pedido, string? Cliente,
        IReadOnlyList<MensagemChat> Mensagens);

    /// <summary>
    /// Lê o pacote que o script do painel manda (<c>{tipo:'chatmsgs', ...}</c>). TOLERANTE de
    /// propósito, como todo leitor de DOM do iFood: campo que falta vira null e JSON estranho
    /// vira leitura vazia, nunca exceção.
    ///
    /// ⚠️ <c>minha</c> AUSENTE NÃO É "do cliente". No DOM a autoria se descobre por geometria
    /// (a fala da loja fica de um lado, a do cliente do outro), e quando a página não consegue
    /// decidir ela manda o campo vazio. Aí a mensagem fica de fora: mandar a fala da própria
    /// loja para a borda é exatamente o que o desenho proíbe. O quadro do WebSocket continua
    /// sendo o caminho principal, e ele não depende desta leitura.
    /// </summary>
    public static LeituraDoDom MensagensDoDom(string? json, DateTime? agora = null)
    {
        var vazia = new LeituraDoDom(null, null, null, Array.Empty<MensagemChat>());
        if (string.IsNullOrWhiteSpace(json)) return vazia;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return vazia;
            var conversa = Texto(r, "conversa") ?? Texto(r, "conversaId");
            var pedido = Texto(r, "pedido");
            var cliente = Texto(r, "cliente");
            var saida = new List<MensagemChat>();
            if (r.TryGetProperty("mensagens", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var m in arr.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object) continue;
                    var texto = CortarTexto(Texto(m, "texto") ?? Texto(m, "text"));
                    if (texto.Length == 0) continue;
                    // sem autoria decidida a mensagem não entra (ver o resumo acima)
                    if (!m.TryGetProperty("minha", out var mv) || mv.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        continue;
                    saida.Add(new MensagemChat(conversa, Texto(m, "autor"), texto,
                        Instante(Texto(m, "hora"), agora), mv.ValueKind == JsonValueKind.True));
                }
            return new LeituraDoDom(conversa, pedido, cliente, saida);
        }
        catch { return vazia; }
    }

    /// <summary>
    /// "14:32" (a hora que o balão mostra) vira o instante mais RECENTE que bate com ela; ISO vira
    /// ele mesmo.
    ///
    /// ⚠️ O balão só mostra a hora, nunca o dia, então este instante é um palpite. Ancorar sempre
    /// em HOJE deixava a hora de um balão da noite passada cair no FUTURO, e futuro passava por
    /// recente. Aqui a hora que ainda não chegou hoje é de ONTEM, que é o mais novo que ela pode
    /// ser: assim a <see cref="Recente"/> a descarta em vez de tratá-la como acabada de chegar.
    /// </summary>
    private static DateTimeOffset? Instante(string? hora, DateTime? agora = null)
    {
        if (string.IsNullOrWhiteSpace(hora)) return null;
        // ⚠️ O "HH:mm" PRIMEIRO. O TryParse aceita hora solta e a ancora em HOJE por conta própria,
        // então ele engolia o caso do balão antes de a correção do dia acontecer: "23:50" lido às
        // 00:10 virava hoje às 23:50, ou seja o futuro, e futuro passava por recente.
        var m = Regex.Match(hora.Trim(), @"^(\d{1,2}):(\d{2})$");
        if (!m.Success)
            return DateTimeOffset.TryParse(hora, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto)
                ? dto : null;
        var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (h > 23 || min > 59) return null;
        var agoraReal = agora ?? DateTime.Now;
        var hoje = agoraReal.Date;
        var palpite = new DateTimeOffset(hoje.Year, hoje.Month, hoje.Day, h, min, 0, DateTimeOffset.Now.Offset);
        return palpite.LocalDateTime > agoraReal + ToleranciaDeRelogio ? palpite.AddDays(-1) : palpite;
    }

    // ── A LINHA LOCAL E A FILA ───────────────────────────────────────────────

    /// <summary>
    /// Grava a mensagem e a enfileira, NA MESMA TRANSAÇÃO (outbox transacional). Devolve false
    /// quando a loja não ligou, quando não sobrou texto, ou quando esta mensagem já foi vista:
    /// nesses casos nada é gravado e nada vai à rede.
    ///
    /// A chave é PRIMARY KEY e a inserção é OR IGNORE: duas threads capturando a mesma mensagem
    /// (o quadro do WebSocket e o DOM na mesma varredura) não viram duas linhas nem duas
    /// chamadas. Quem não inseriu desiste sem enfileirar.
    /// </summary>
    public static bool Registrar(SqliteConnection cx, MensagemDoChat m)
    {
        if (!LigadoNaLoja(cx)) return false;
        var texto = CortarTexto(m.Texto);
        if (texto.Length == 0 || string.IsNullOrWhiteSpace(m.Chave)) return false;

        using var tx = cx.BeginTransaction();
        var n = cx.Execute("""
            INSERT OR IGNORE INTO raspadinha_chat
                   (chave, texto, loja, pedido, ifood_order_id, cliente, origem, situacao, criado_em)
            VALUES (@K, @T, @L, @P, @O, @C, @G, 'na_fila', @Em)
            """, new { K = m.Chave, T = texto, L = Vazio(m.Loja), P = Vazio(m.Pedido), O = Vazio(m.IfoodOrderId),
                       C = Vazio(m.Cliente), G = m.Origem == OrigemExtensao ? OrigemExtensao : OrigemCaixa,
                       Em = DateTime.Now.ToString("o") }, tx);
        if (n == 0) { tx.Rollback(); return false; }
        Caixa.Enfileirar(cx, tx, TipoNaFila, m.Chave, m.Chave, new { origem = m.Origem });
        tx.Commit();
        FaxinaDeVezEmQuando(cx, DateTime.Now);
        return true;
    }

    /// <summary>Mesmo que o de cima, abrindo o banco.</summary>
    public static bool Registrar(MensagemDoChat m)
    {
        using var cx = Banco.Abrir();
        return Registrar(cx, m);
    }

    /// <summary>
    /// Manda a mensagem já gravada para a borda e aplica o desfecho. É o caminho da TELA: a fila
    /// é a rede de segurança para quando este caminho não completa.
    /// </summary>
    public async Task<RespostaChat> EnviarAsync(string chave, CancellationToken ct = default)
    {
        string corpo;
        bool primeira;
        using (var cx = Banco.Abrir())
        {
            var linha = cx.QueryFirstOrDefault("""
                SELECT texto, loja, pedido, ifood_order_id, cliente, origem, situacao, motivo, bonus_id, tentado_em
                  FROM raspadinha_chat WHERE chave = @K
                """, new { K = chave });
            if (linha is null)
                return new RespostaChat(DesfechoChat.Recusado, MotivoChat.Desconhecido, "mensagem local sumiu", null, false);
            var pronta = Resolvida(cx, (string)linha.situacao, linha.motivo as string, linha.bonus_id as string);
            if (pronta is not null) return pronta;
            if (!LigadoNaLoja(cx))
                return new RespostaChat(DesfechoChat.Desligado, MotivoChat.LojaDesligada, "desligado na loja", null, false);
            // tentado_em ainda vazio = esta mensagem nunca saiu deste caixa (ver Aplicar)
            primeira = linha.tentado_em is null;
            corpo = CorpoDoPedido((string)linha.texto, linha.loja as string, linha.pedido as string,
                linha.ifood_order_id as string, linha.cliente as string, linha.origem as string);
            // 'enviando' antes de sair: a fila vê que a tela está com a linha e não mexe nela.
            cx.Execute("UPDATE raspadinha_chat SET situacao = 'enviando', tentado_em = @Em WHERE chave = @K",
                new { Em = DateTime.Now.ToString("o"), K = chave });
        }

        var (st, resp) = await _nuvem.FuncaoAsync(Edge, corpo, Prazo, ct).ConfigureAwait(false);
        var r = LerResposta(st, resp, DateTime.Now);
        using (var cx = Banco.Abrir()) Aplicar(cx, chave, r, "tela", primeira);
        return r;
    }

    /// <summary>A linha já tem desfecho? (a tela não chama de novo, e a fila sai sem chamada)</summary>
    private static RespostaChat? Resolvida(SqliteConnection cx, string situacao, string? motivo, string? bonusId)
        => situacao switch
        {
            "bonus" => new RespostaChat(DesfechoChat.Bonus, MotivoChat.Nenhum, null,
                bonusId is null ? null : Bonus(cx, bonusId), true),
            "sem_codigo" => new RespostaChat(DesfechoChat.SemCodigo, MotivoChat.SemCodigo, motivo, null, true),
            "recusado" => new RespostaChat(DesfechoChat.Recusado, LerMotivo(motivo), motivo, null, true),
            "nao_enviado" => new RespostaChat(DesfechoChat.SemPermissao, MotivoChat.SemPermissao, motivo, null, true),
            _ => null,
        };

    /// <summary>
    /// Grava o desfecho na linha da mensagem e, quando há bônus, a linha do BÔNUS, que é o que
    /// vira papel. A linha do bônus é INSERT OR IGNORE pela PK: a mesma mensagem reenviada (ou a
    /// mesma mensagem vista pelos dois caminhos) devolve o mesmo bônus e não vira segunda comanda.
    /// </summary>
    private static void Aplicar(SqliteConnection cx, string chave, RespostaChat r, string quem,
        bool primeiraTentativa = false)
    {
        var agora = DateTime.Now.ToString("o");
        var situacao = r.Desfecho switch
        {
            DesfechoChat.Bonus => "bonus",
            DesfechoChat.SemCodigo => "sem_codigo",
            DesfechoChat.Recusado => "recusado",
            DesfechoChat.SemPermissao => "nao_enviado",
            _ => null,   // sem desfecho: a linha continua na fila
        };
        if (situacao is null)
        {
            cx.Execute("UPDATE raspadinha_chat SET situacao = 'na_fila', motivo = @M WHERE chave = @K",
                new { M = r.MotivoCru, K = chave });
            return;
        }

        if (r.Bonus is { } b)
        {
            // ⚠️ COMANDA DOBRADA É BRINDE DOBRADO, E A PK AQUI É LOCAL. Dois caixas da mesma loja
            // enxergam o MESMO quadro do WebSocket (cada um tem o Gestor vivo desde a abertura),
            // calculam a mesma chave e chamam a borda: um resgata e o outro recebe o bônus com
            // ja_registrado. Nos dois a linha nasce nova, nos dois ela entra na lista de imprimir,
            // e saem dois papéis do mesmo brinde.
            //
            // Enquanto a borda não devolver QUEM reivindicou a impressão, a regra é: bônus que o
            // servidor diz que já existia e que é novo neste disco NÃO tira papel sozinho. Ele
            // continua achável, e o botão Reimprimir do chat traz o papel se este caixa precisar.
            //
            // PRIMEIRA TENTATIVA é o que separa "outro terminal" de "eu, reenviando". O caixa que
            // mandou e não ouviu a resposta reenvia a MESMA chave e recebe ja_registrado do próprio
            // resgate dele: esse papel é dele e tem de sair (é a promessa de que a comanda sai
            // quando a rede volta). Só quem nunca mandou esta mensagem antes e já encontra o bônus
            // pronto está vendo o trabalho de outro.
            var deOutro = r.JaRegistrado && primeiraTentativa ? 1 : 0;
            cx.Execute("""
                INSERT OR IGNORE INTO raspadinha_bonus
                       (id, codigo, premio, premio_emoji, cliente, pedido, loja, ifood_order_id, origem,
                        criado_em, de_outro_terminal)
                VALUES (@I, @C, @P, @E, @N, @D, @L, @O, @G, @Em, @X)
                """, new { I = b.Id, C = b.Codigo, P = b.Premio, E = b.PremioEmoji, N = b.Cliente, D = b.Pedido,
                           L = b.Loja, O = b.IfoodOrderId, G = b.Origem, Em = agora, X = deOutro });
        }

        // ⚠️ O TEXTO DA PESSOA SAI DAQUI ASSIM QUE TEM DESFECHO. A mensagem é gravada só porque
        // precisa ser reenviada enquanto não houver resposta; resolvida, ela não tem mais função e
        // não pode ficar seis meses num SQLite de balcão (reclamação, endereço, telefone digitado
        // no chat). A chave continua sendo o hash que impede a repetição, então nada se perde.
        cx.Execute("""
            UPDATE raspadinha_chat
               SET situacao = @S, motivo = @M, bonus_id = COALESCE(@B, bonus_id), resolvido_em = @Em,
                   texto = ''
             WHERE chave = @K
            """, new { S = situacao, M = r.MotivoCru, B = r.Bonus?.Id, Em = agora, K = chave });

        if (r.Desfecho == DesfechoChat.Bonus)
            Caixa.Auditar(cx, null, "raspadinha_chat_bonus", null, null,
                $"{quem}: chave={chave} bonus={r.Bonus?.Id} codigo={r.Bonus?.Codigo} "
                + $"premio={r.Bonus?.Premio} pedido={r.Bonus?.Pedido} ja_registrado={r.JaRegistrado}");
        else if (r.Desfecho == DesfechoChat.Recusado)
            Caixa.Auditar(cx, null, "raspadinha_chat_recusado", null, null,
                $"{quem}: chave={chave} motivo={r.MotivoCru}");
    }

    /// <summary>
    /// O que a Drenagem faz com uma linha <see cref="TipoNaFila"/>. Desfecho no contrato da fila:
    /// (true) resolvido, (false) recusa permanente, (null) transitório.
    ///
    /// Reenviar aqui é seguro, e por um motivo que o brinde não tinha: NADA é entregue por esta
    /// chamada. Ela só conta ao servidor o que o cliente escreveu; quem resgata é o servidor, uma
    /// vez por raspadinha, e a segunda chamada devolve o bônus que já existe. Se a resposta se
    /// perdeu depois de o servidor registrar, a fila descobre o bônus e a comanda sai agora.
    ///
    /// A loja DESLIGOU a captura depois da mensagem entrar na fila: a linha sai sem chamada. Foi
    /// isso que o dono pediu ("desligado = nada é mandado"), e vale também para o que já esperava.
    /// </summary>
    public static async Task<(bool? Ok, string? Erro)> ResolverNaFilaAsync(string chave,
        Func<string, string, Task<(int Status, string? Corpo)>> funcao, DateTime agora)
    {
        string corpo;
        bool primeira;
        using (var cx = Banco.Abrir())
        {
            var linha = cx.QueryFirstOrDefault("""
                SELECT texto, loja, pedido, ifood_order_id, cliente, origem, situacao, motivo, bonus_id, tentado_em
                  FROM raspadinha_chat WHERE chave = @K
                """, new { K = chave });
            if (linha is null) return (true, "mensagem local sumiu: nada a mandar");

            var situacao = (string)linha.situacao;
            switch (situacao)
            {
                case "bonus": return (true, "o bônus já foi registrado");
                case "sem_codigo": return (true, "não havia código nesta mensagem");
                case "recusado": return (true, $"o servidor recusou ({linha.motivo as string})");
                case "nao_enviado": return (false, $"este caixa foi barrado ({linha.motivo as string})");
                case "enviando":
                    // a tela pode estar com a linha agora; só depois do prazo a fila assume
                    if (DateTime.TryParse(linha.tentado_em as string, CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out DateTime em) && em > agora - EsperaDaTela)
                        return (null, "a tela ainda está mandando esta mensagem");
                    break;
            }

            if (!LigadoNaLoja(cx))
            {
                cx.Execute("""
                    UPDATE raspadinha_chat
                       SET situacao = 'sem_codigo', motivo = 'desligado_na_loja', resolvido_em = @Em, texto = ''
                     WHERE chave = @K AND situacao IN ('na_fila','enviando')
                    """, new { Em = agora.ToString("o"), K = chave });
                return (true, "a captura do código no chat foi desligada nesta loja");
            }

            primeira = linha.tentado_em is null;
            corpo = CorpoDoPedido((string)linha.texto, linha.loja as string, linha.pedido as string,
                linha.ifood_order_id as string, linha.cliente as string, linha.origem as string);
            cx.Execute("UPDATE raspadinha_chat SET situacao = 'enviando', tentado_em = @Em WHERE chave = @K",
                new { Em = agora.ToString("o"), K = chave });
        }

        var (st, resp) = await funcao(Edge, corpo).ConfigureAwait(false);
        var r = LerResposta(st, resp, agora);
        using (var cx = Banco.Abrir()) Aplicar(cx, chave, r, "fila", primeira);

        return r.Desfecho switch
        {
            DesfechoChat.Bonus => (true, r.JaRegistrado ? "o bônus já estava registrado" : null),
            DesfechoChat.SemCodigo => (true, "não havia código nesta mensagem"),
            DesfechoChat.Recusado => (true, $"o servidor recusou ({r.MotivoCru})"),
            DesfechoChat.SemPermissao => (false, $"este caixa foi barrado ({r.MotivoCru})"),
            DesfechoChat.NuvemSemRecurso => (null, $"a nuvem ainda não tem a função {Edge} (publicar a borda)"),
            _ => (null, $"sem desfecho ao mandar a mensagem: {r.MotivoCru}"),
        };
    }

    // ── O PAPEL ──────────────────────────────────────────────────────────────

    /// <summary>Quantas vezes o mesmo bônus tenta o papel sozinho antes de esperar uma pessoa.</summary>
    public const int TetoDeImpressao = 3;

    /// <summary>
    /// Os bônus que ainda não saíram no papel, do mais antigo para o mais novo.
    ///
    /// Fica de fora o que é de OUTRO terminal (o papel é de lá) e o que já gastou o teto de
    /// tentativas (impressora morta não pode virar metralhadora). Os dois continuam achando
    /// caminho para o papel pelo botão Reimprimir da tela do chat.
    /// </summary>
    public static IReadOnlyList<BonusRaspadinha> ParaImprimir()
    {
        try
        {
            using var cx = Banco.Abrir();
            return cx.Query("""
                SELECT id, codigo, premio, premio_emoji, cliente, pedido, loja, ifood_order_id, origem, criado_em
                  FROM raspadinha_bonus
                 WHERE impresso_em IS NULL AND de_outro_terminal = 0 AND tentativas_impressao < @Teto
                 ORDER BY criado_em
                """, new { Teto = TetoDeImpressao }).Select(Montar).ToList();
        }
        catch { return Array.Empty<BonusRaspadinha>(); }
    }

    /// <summary>
    /// Os bônus de HOJE que não estão no papel: os que a impressão falhou, os que são de outro
    /// terminal e os que ainda esperam. É o que o botão Reimprimir do chat oferece, porque o
    /// último bônus é UM só e numa noite sem bobina três brindes ficam sem comanda e sem botão.
    /// </summary>
    public static IReadOnlyList<BonusRaspadinha> SemPapelHoje(DateTime? hoje = null)
    {
        try
        {
            var dia = (hoje ?? DateTime.Now).Date.ToString("o");
            using var cx = Banco.Abrir();
            return cx.Query("""
                SELECT id, codigo, premio, premio_emoji, cliente, pedido, loja, ifood_order_id, origem, criado_em
                  FROM raspadinha_bonus
                 WHERE criado_em >= @Dia
                   AND (impresso_em IS NULL OR erro_impressao IS NOT NULL)
                 ORDER BY criado_em
                """, new { Dia = dia }).Select(Montar).ToList();
        }
        catch { return Array.Empty<BonusRaspadinha>(); }
    }

    /// <summary>Um bônus pelo id (para a reimpressão pela tela do chat). Null se não existe.</summary>
    public static BonusRaspadinha? Bonus(string id)
    {
        using var cx = Banco.Abrir();
        return Bonus(cx, id);
    }

    private static BonusRaspadinha? Bonus(SqliteConnection cx, string id)
    {
        var linha = cx.QueryFirstOrDefault("""
            SELECT id, codigo, premio, premio_emoji, cliente, pedido, loja, ifood_order_id, origem, criado_em
              FROM raspadinha_bonus WHERE id = @I
            """, new { I = id });
        return linha is null ? null : Montar(linha);
    }

    /// <summary>O último bônus desta loja, para o botão de reimprimir. Null quando não há nenhum.</summary>
    public static BonusRaspadinha? UltimoBonus()
    {
        try
        {
            using var cx = Banco.Abrir();
            var linha = cx.QueryFirstOrDefault("""
                SELECT id, codigo, premio, premio_emoji, cliente, pedido, loja, ifood_order_id, origem, criado_em
                  FROM raspadinha_bonus ORDER BY criado_em DESC LIMIT 1
                """);
            return linha is null ? null : Montar(linha);
        }
        catch { return null; }
    }

    private static BonusRaspadinha Montar(dynamic l) => new(
        (string)l.id, l.codigo as string, l.premio as string, l.premio_emoji as string,
        l.cliente as string, l.pedido as string, l.loja as string, l.ifood_order_id as string,
        (l.origem as string) == OrigemExtensao ? OrigemExtensao : OrigemCaixa,
        DateTime.TryParse(l.criado_em as string, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTime em) ? em : DateTime.Now);

    /// <summary>
    /// Reivindica a impressão ANTES de mandar para o papel. Atômico: a puxada do delivery e a
    /// volta da rede se sobrepõem, e sem isto o mesmo bônus sairia duas vezes. Falhou DEPOIS do
    /// claim, o bônus NÃO volta sozinho (impressora morta viraria metralhadora): quem recupera é
    /// o botão de reimprimir da tela do chat.
    /// </summary>
    public static bool ReivindicarImpressao(string id)
    {
        try
        {
            using var cx = Banco.Abrir();
            return cx.Execute("UPDATE raspadinha_bonus SET impresso_em = @Em WHERE id = @I AND impresso_em IS NULL",
                new { I = id, Em = DateTime.Now.ToString("o") }) == 1;
        }
        catch { return false; }
    }

    /// <summary>O papel saiu: apaga o rastro de falha, para o bônus sair da lista do Reimprimir.</summary>
    public static void ConfirmarImpressao(string id)
    {
        try
        {
            using var cx = Banco.Abrir();
            cx.Execute("UPDATE raspadinha_bonus SET erro_impressao = NULL WHERE id = @I", new { I = id });
        }
        catch { /* conforto: o papel já saiu */ }
    }

    // ── FAXINA ───────────────────────────────────────────────────────────────

    /// <summary>Quanto tempo a linha da mensagem resolvida ainda serve para conferência.</summary>
    public static readonly TimeSpan GuardaDaMensagem = TimeSpan.FromDays(2);

    private static DateTime _proximaFaxina = DateTime.MinValue;

    /// <summary>
    /// Apaga as linhas de mensagem já resolvidas e velhas. O texto da pessoa já sai no desfecho
    /// (ver Aplicar); isto tira a linha inteira, que depois de dois dias não prova mais nada. Sem
    /// isto a tabela só crescia: não havia DELETE nenhum em todo o recurso.
    ///
    /// O BÔNUS NÃO É APAGADO: ele é o rastro do brinde entregue.
    /// </summary>
    public static int Faxina(SqliteConnection cx, DateTime agora)
        => cx.Execute("""
            DELETE FROM raspadinha_chat
             WHERE situacao IN ('bonus','sem_codigo','recusado','nao_enviado')
               AND resolvido_em IS NOT NULL AND resolvido_em < @Limite
            """, new { Limite = (agora - GuardaDaMensagem).ToString("o") });

    /// <summary>A faxina, no máximo uma vez por hora e sem nunca atrapalhar a captura.</summary>
    private static void FaxinaDeVezEmQuando(SqliteConnection cx, DateTime agora)
    {
        if (agora < _proximaFaxina) return;
        _proximaFaxina = agora.AddHours(1);
        try { Faxina(cx, agora); } catch { /* faxina é conforto */ }
    }

    /// <summary>
    /// O papel não saiu: o CLAIM VOLTA e a tentativa é contada.
    ///
    /// O claim ficava gravado para sempre. Numa sexta com a bobina acabada os três bônus da noite
    /// eram reivindicados, falhavam e sumiam da lista de pendentes: trocada a bobina, o botão
    /// trazia só o último, e os outros dois existiam no banco com o brinde já queimado no servidor
    /// sem nenhuma tela que os mostrasse. Devolver o claim faz a comanda sair sozinha na varredura
    /// seguinte; o teto de <see cref="TetoDeImpressao"/> é o que impede a metralhadora, e o rastro
    /// em erro_impressao continua dizendo que existe brinde sem papel.
    /// </summary>
    public static void AnotarFalhaDeImpressao(string id, string? erro)
    {
        try
        {
            using var cx = Banco.Abrir();
            cx.Execute("""
                UPDATE raspadinha_bonus
                   SET erro_impressao = @E, impresso_em = NULL,
                       tentativas_impressao = tentativas_impressao + 1
                 WHERE id = @I
                """, new { I = id, E = erro });
            Caixa.Auditar(cx, null, "raspadinha_chat_sem_papel", null, null, $"bonus={id} {erro}");
        }
        catch { /* o aviso na tela já contou; o rastro é conforto */ }
    }

    // ── json ─────────────────────────────────────────────────────────────────

    private static string? Texto(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
           && v.GetString() is { } s && s.Trim().Length > 0 ? s.Trim() : null;

    private static DateTime? Data(JsonElement e, string k)
    {
        var s = Texto(e, k);
        if (s is null) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto)
            ? dto.LocalDateTime : null;
    }

    /// <summary>"MARIA DA SILVA" vira "Maria": o balcão não precisa do nome inteiro do cliente.</summary>
    public static string? PrimeiroNome(string? nome)
    {
        if (string.IsNullOrWhiteSpace(nome)) return null;
        var p = nome.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return p.Length == 0 ? null : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant();
    }
}
