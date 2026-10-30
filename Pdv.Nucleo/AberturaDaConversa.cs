using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pdv.Nucleo;

/// <summary>
/// A PRIMEIRA MENSAGEM PARA QUEM NUNCA ABRIU O CHAT (1.0.62, 30/10/2026).
///
/// O dono: "so esta mandando msg pros clientes de boas-vindas e pedido enviado se o cliente abrir uma msg antes".
/// O canal do Sendbird de um pedido so existe depois que alguem escreve nele; o envio pelo SDK num canal que nao
/// existe volta 900500 (canal_inexistente) e a mensagem do pedido morria ali. O proprio Gestor de Pedidos (o chat
/// partners-consumer-chat 1.1.1, servico "createChannel") cria a conversa mandando a primeira mensagem para
/// <c>POST https://order-manager-api.ifood.com.br/orderManager/v1.0/chat/orders/{pedido}</c> com
/// <c>{ customerId, message }</c>, e a resposta traz o <c>chatId</c>. O caixa faz o mesmo, de dentro da pagina do
/// Gestor ja logada, com o mesmo token que o Gestor usa (visto saindo nas chamadas dele, so em memoria).
///
/// Esta classe e PURA: monta o script e le a resposta. Quem chama a pagina e a tela do chat; quem decide quando e o
/// laco de envio (so no canal_inexistente de uma mensagem do pedido que traz o cliente).
/// </summary>
/// <summary>O que a abertura deu: abriu (com o chatId, quando vem) ou o erro, e o detalhe para o relato.</summary>
public sealed record AberturaResultado(bool Ok, string? Erro, string? ChatId, JsonObject Detalhe);

public static class AberturaDaConversa
{
    public const string Endereco = "https://order-manager-api.ifood.com.br/orderManager/v1.0/chat/orders/";

    /// <summary>O token do Gestor vale por pouco tempo; um mais velho que isso nem e tentado (a pagina renova sozinha).</summary>
    public static readonly TimeSpan IdadeMaximaDoToken = TimeSpan.FromMinutes(50);

    private static readonly Regex Uuid = new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant);

    /// <summary>As APIs do Gestor que levam o token da loja (de onde o caixa aprende o token).</summary>
    public static bool EhApiDoGestor(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return false;
        return u.Host is "order-manager-api.ifood.com.br" or "merchant-api.ifood.com.br";
    }

    /// <summary>Da para abrir? O pedido e o cliente tem que ser ids do iFood (uuid) e o texto nao pode ser vazio.</summary>
    public static bool Cabe(string? orderUuid, string? clienteId, string? texto)
        => orderUuid is not null && Uuid.IsMatch(orderUuid.Trim())
           && clienteId is not null && Uuid.IsMatch(clienteId.Trim())
           && !string.IsNullOrWhiteSpace(texto);

    /// <summary>
    /// A expressao para o Runtime.evaluate (com awaitPromise): o mesmo pedido que o Gestor faz. Tudo entra como JSON
    /// (nada de texto colado no script). Devolve <c>{ status, corpo }</c>; o corpo vai cortado em 400.
    /// </summary>
    public static string Script(string orderUuid, string clienteId, string texto, string token)
    {
        var url = JsonSerializer.Serialize(Endereco + orderUuid.Trim().ToLowerInvariant());
        var corpo = JsonSerializer.Serialize(JsonSerializer.Serialize(new { customerId = clienteId.Trim(), message = texto }));
        var auth = JsonSerializer.Serialize("Bearer " + token);
        return "(async function(){ try { var r = await fetch(" + url + ", { method: 'POST', credentials: 'omit', headers: {"
             + " 'authorization': " + auth + ", 'content-type': 'application/json', 'accept': 'application/json', 'app-platform': 'web' },"
             + " body: " + corpo + " }); var t = ''; try { t = await r.text(); } catch (e) {}"
             + " return { status: r.status, corpo: String(t || '').slice(0, 400) }; }"
             + " catch (e) { return { status: 0, corpo: String(e && e.message || e).slice(0, 200) }; } })()";
    }

    /// <summary>
    /// A resposta do Runtime.evaluate ({ result: { value: { status, corpo } } }). 2xx abriu (o chatId, quando vem, e o
    /// id que volta); o resto e o erro <c>abrir_http_NNN</c> (0 = a chamada nem saiu), que o laco relata como antes.
    /// </summary>
    public static AberturaResultado Ler(string? respostaCdp)
    {
        try
        {
            var valor = JsonNode.Parse(respostaCdp ?? "")?["result"]?["value"];
            var status = valor?["status"]?.GetValue<int>() ?? 0;
            var corpo = valor?["corpo"]?.GetValue<string>() ?? "";
            var detalhe = new JsonObject { ["via"] = "abriu_conversa", ["status"] = status };
            if (status is >= 200 and < 300)
            {
                string? chat = null;
                try { chat = JsonNode.Parse(corpo)?["chatId"]?.GetValue<string>(); } catch { }
                return new AberturaResultado(true, null, chat is { Length: > 0 and <= 200 } ? chat : null, detalhe);
            }
            return new AberturaResultado(false, "abrir_http_" + status, null, detalhe);
        }
        catch { return new AberturaResultado(false, "abrir_ilegivel", null, new JsonObject { ["via"] = "abriu_conversa" }); }
    }
}
