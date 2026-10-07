using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pdv.Nucleo;

/// <summary>
/// Uma fala do chat do iFood como ela vem no quadro <c>MESG</c> do Sendbird, sem interpretação:
/// o comando, o canal (a conversa), os ids da mensagem, quem escreveu, o tipo de usuário que o
/// próprio quadro declara, o texto e o instante em milissegundos.
///
/// <c>Whisper</c> é a fala "sussurrada" (só um lado vê). Ela é lida para o diagnóstico contar, e
/// quem descarta é a triagem (<see cref="ConversaRaspadinha.Triagem"/>).
/// </summary>
public sealed record MensagemSendbird(
    string Comando, string? Canal, string? MsgId, string? ReqId, string? AutorId,
    string? UserType, string Texto, long? CriadoEmMs, bool Whisper);

/// <summary>
/// RESGATE PELO CHAT DO iFOOD (07/10/2026, desenho "resgate-final", seção 4.9 e tarefa P1).
///
/// Lê o quadro do WebSocket do Sendbird que o caixa já capturava pelo CDP. É estrito de propósito,
/// ao contrário do <see cref="ChatCaptura.NormalizarFrame"/>: só o comando <c>MESG</c> vira fala.
/// <c>ADMM</c> (mensagem de administrador), <c>FILE</c> (foto), <c>READ</c>, <c>PING</c> e o resto
/// ficam de fora, porque nenhum deles é o cliente escrevendo.
///
/// ⚠️ ESTE ARQUIVO NÃO CONHECE O CÓDIGO DA RASPADINHA NEM PALAVRA NENHUMA. Quem decide se há código,
/// palavra ou resposta é o ERP (raspadinha_chat_mensagem). Aqui só se separa o que é fala do que é
/// controle, e se diz quem falou e onde.
///
/// Nunca lança: JSON quebrado vira null.
/// </summary>
public static class QuadroSendbird
{
    public const string ComandoMensagem = "MESG";

    /// <summary>
    /// O comando de 4 letras maiúsculas no começo do quadro ("MESG", "ADMM", "FILE"), ou null quando
    /// o quadro não começa assim (JSON puro, texto solto).
    /// </summary>
    public static string? Comando(string? payload)
    {
        if (payload is null || payload.Length < 4) return null;
        for (var i = 0; i < 4; i++)
            if (payload[i] is < 'A' or > 'Z') return null;
        return payload[..4];
    }

    /// <summary>
    /// A fala do quadro, ou null quando ele não é um <c>MESG</c> com texto. Lê o autor primeiro em
    /// <c>user.guest_id</c> (é onde o Sendbird do iFood põe o id) e depois em <c>user.user_id</c>; o
    /// tipo de usuário em <c>metadata.userType</c>, na raiz ou dentro de <c>user</c>, com o metadata
    /// em objeto ou em texto JSON.
    /// </summary>
    public static MensagemSendbird? Ler(string? payload)
    {
        try
        {
            if (Comando(payload) != ComandoMensagem) return null;
            using var doc = JsonDocument.Parse(payload!.AsMemory(4));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;

            var texto = Texto(r, "message");
            if (texto is null) return null;

            string? autor = null, userType = null;
            if (r.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                autor = TextoOuNumero(u, "guest_id") ?? TextoOuNumero(u, "user_id") ?? TextoOuNumero(u, "userId");
                userType = UserType(u);
            }
            autor ??= TextoOuNumero(r, "user_id") ?? TextoOuNumero(r, "guest_id");
            userType ??= UserType(r);

            var msgId = TextoOuNumero(r, "msg_id");
            if (msgId is "0") msgId = null;

            return new MensagemSendbird(
                ComandoMensagem,
                Texto(r, "channel_url"),
                msgId,
                TextoOuNumero(r, "req_id"),
                autor,
                userType,
                texto,
                Numero(r, "created_at") ?? Numero(r, "ts"),
                Sussurro(r));
        }
        catch { return null; }
    }

    /// <summary>
    /// O <c>user_id</c> da URL do WebSocket do Sendbird (<c>wss://ws-...sendbird.com/?...&amp;user_id=...</c>).
    /// É o id da LOJA nesta conexão: a fala cujo autor é ele é da loja.
    ///
    /// ⚠️ Fica só em memória, para comparar autor. Nunca é gravado, logado nem enviado.
    /// </summary>
    public static string? UserIdDaUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var q = url.IndexOf('?');
        if (q < 0) return null;
        var fim = url.IndexOf('#', q);
        var query = fim < 0 ? url[(q + 1)..] : url[(q + 1)..fim];
        foreach (var par in query.Split('&'))
        {
            var eq = par.IndexOf('=');
            if (eq <= 0 || !par[..eq].Equals("user_id", StringComparison.Ordinal)) continue;
            try
            {
                var v = Uri.UnescapeDataString(par[(eq + 1)..].Replace('+', ' ')).Trim();
                return v.Length == 0 ? null : v;
            }
            catch { return null; }
        }
        return null;
    }

    // ── json ─────────────────────────────────────────────────────────────────

    /// <summary>"CUSTOMER", "MERCHANT"... em maiúsculas. Aceita metadata em objeto ou em texto JSON.</summary>
    private static string? UserType(JsonElement o)
    {
        if (!o.TryGetProperty("metadata", out var md)) return null;
        if (md.ValueKind == JsonValueKind.String)
        {
            var s = md.GetString();
            if (string.IsNullOrWhiteSpace(s) || !s.TrimStart().StartsWith('{')) return null;
            try
            {
                using var d = JsonDocument.Parse(s);
                return DoObjeto(d.RootElement);
            }
            catch { return null; }
        }
        return DoObjeto(md);

        static string? DoObjeto(JsonElement m)
        {
            if (m.ValueKind != JsonValueKind.Object) return null;
            var v = Texto(m, "userType") ?? Texto(m, "user_type") ?? Texto(m, "usertype");
            return v?.Trim().ToUpperInvariant();
        }
    }

    /// <summary>
    /// Fala sussurrada: <c>data.whisperMode</c> ligado (o <c>data</c> do Sendbird é texto JSON) ou
    /// <c>whisperMode</c> na raiz. Ligado é true, número diferente de zero ou texto que não diz não.
    /// </summary>
    private static bool Sussurro(JsonElement r)
    {
        if (Ligado(r, "whisperMode")) return true;
        if (!r.TryGetProperty("data", out var d)) return false;
        if (d.ValueKind == JsonValueKind.Object) return Ligado(d, "whisperMode");
        if (d.ValueKind != JsonValueKind.String) return false;
        var s = d.GetString();
        if (string.IsNullOrWhiteSpace(s) || !s.TrimStart().StartsWith('{')) return false;
        try
        {
            using var dd = JsonDocument.Parse(s);
            return dd.RootElement.ValueKind == JsonValueKind.Object && Ligado(dd.RootElement, "whisperMode");
        }
        catch { return false; }

        static bool Ligado(JsonElement o, string k)
        {
            if (!o.TryGetProperty(k, out var v)) return false;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.Number => v.TryGetDouble(out var n) && n != 0,
                JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() is { Length: > 0 } t
                                        && t is not ("false" or "0" or "none" or "off" or "no"),
                _ => false,
            };
        }
    }

    private static string? Texto(JsonElement o, string k)
        => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s ? s : null;

    private static string? TextoOuNumero(JsonElement o, string k)
    {
        if (!o.TryGetProperty(k, out var v)) return null;
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static long? Numero(JsonElement o, string k)
    {
        if (!o.TryGetProperty(k, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var m)) return m;
        return null;
    }
}

/// <summary>
/// O nome da conversa do iFood no Sendbird: <c>sendbird_gc_cm_&lt;orderId&gt;_&lt;merchantId&gt;</c>.
/// Provado em 46 de 47 conversas (seção 0, decisão 5). É dele que sai o pedido exato e a loja: o
/// caixa da Savassi ignora a conversa do Castelo sem precisar ler a tela.
///
/// A expressão é a MESMA que o ERP confere (seção 4.3, item 2), letra por letra: canal que o ERP
/// recusaria como <c>canal_invalido</c> não sai daqui. Por isso maiúscula não passa. O fim é
/// <c>\z</c> e não <c>$</c>: no .NET o <c>$</c> aceita uma quebra de linha no fim, e o Postgres não.
/// </summary>
public static class CanalIfood
{
    private static readonly Regex Padrao = new(
        @"^sendbird_gc_cm_([0-9a-f-]{36})_([0-9a-f-]{36})\z", RegexOptions.CultureInvariant);

    public static (string OrderId, string MerchantId)? Ler(string? canal)
    {
        if (string.IsNullOrEmpty(canal)) return null;
        var m = Padrao.Match(canal);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : null;
    }
}
