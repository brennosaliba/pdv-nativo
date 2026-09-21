using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>
/// O QUE O PAINEL DEFINE E O CAIXA COPIA NO ATUALIZAR (12/09/2026, pedidos do dono):
///  · respostas prontas da aba Chat ("no painel vai ter opção de configurar as mensagens
///    pré-definidas?");
///  · música da loja (Spotify): playlist, aparelho e volume escolhidos no painel;
///  · raspadinha no caixa (21/09/2026): liga o cartão do brinde na aba Promoções.
///
/// A SENHA DE ADMINISTRADOR POR LOJA NÃO DESCE MAIS (15/09/2026, revisão do usuário master).
/// As colunas admin_pin_* de pdv_loja_config continuam na RPC, mas o caixa ignora: a política
/// pdv_loja_config_write deixa o GERENTE (manager) gravar nelas pela API, e o painel não grava
/// mais desde que a senha virou o usuário master da rede. Copiar para a `_admin_` deixava um
/// gerente escolher a senha que abre a Configuração de um caixa ainda sem master.
/// Vem tudo da RPC pdv_loja_config_caixa (uma linha por loja que o usuário alcança).
///
/// Regras (puras, provadas na suíte):
///  · a linha da loja é a de nome igual ao do terminal (sem acento, sem caixa); se o
///    usuário alcança uma só, é ela;
///  · respostas: o painel manda texto = o caixa passa a usar esse texto; painel vazio =
///    o caixa mantém o que tem (nunca apaga o que a loja editou no próprio caixa);
///  · música: o painel é a verdade (chave some quando o painel não tem valor).
/// </summary>
public static class ConfigLojaPainel
{
    public const string ChaveAdminAplicadoEm = "admin_pin_painel_em";
    public const string ChavePlaylistUri = "musica_playlist_uri";
    public const string ChavePlaylistNome = "musica_playlist_nome";
    public const string ChaveDeviceId = "musica_device_id";
    public const string ChaveDeviceNome = "musica_device_nome";
    public const string ChaveVolume = "musica_volume";

    public sealed record Linha(
        string Store, string? ChatRespostas,
        string? AdminHash, string? AdminSalt, DateTime? AdminEm,
        string? PlaylistUri, string? PlaylistNome, string? DeviceId, string? DeviceNome, int? Volume,
        bool? RaspadinhaNoCaixa = null);

    /// <summary>O JSON da RPC (array) em linhas; JSON estranho = lista vazia, nunca exceção.</summary>
    public static IReadOnlyList<Linha> Ler(string? json)
    {
        var saida = new List<Linha>();
        if (string.IsNullOrWhiteSpace(json)) return saida;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return saida;
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                if (o.ValueKind != JsonValueKind.Object) continue;
                string? S(string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                int? I(string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
                bool? B(string n) => o.TryGetProperty(n, out var v) ? v.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                } : null;
                var store = S("store");
                if (string.IsNullOrWhiteSpace(store)) continue;
                DateTime? em = DateTime.TryParse(S("admin_pin_atualizado"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : null;
                saida.Add(new Linha(store, S("chat_respostas"), S("admin_pin_hash"), S("admin_pin_salt"), em,
                    S("playlist_uri"), S("playlist_nome"), S("device_id"), S("device_nome"), I("volume"),
                    B("raspadinha_no_caixa")));
            }
        }
        catch { /* JSON ilegível: nada a aplicar */ }
        return saida;
    }

    /// <summary>Nome de loja comparável: sem acento, sem caixa, sem espaço sobrando.</summary>
    public static string Normalizar(string? nome)
    {
        var d = (nome ?? "").Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>A linha desta loja: pelo nome do terminal; se só há uma ao alcance, é ela.</summary>
    public static Linha? EscolherLinha(IReadOnlyList<Linha> linhas, string? lojaNome)
    {
        var alvo = Normalizar(lojaNome);
        if (alvo.Length > 0)
        {
            var exata = linhas.FirstOrDefault(l => Normalizar(l.Store) == alvo);
            if (exata is not null) return exata;
            var contem = linhas.Where(l => Normalizar(l.Store).Contains(alvo) || alvo.Contains(Normalizar(l.Store))).ToList();
            if (contem.Count == 1) return contem[0];
        }
        return linhas.Count == 1 ? linhas[0] : null;
    }

    /// <summary>Respostas: o texto do painel vale (normalizado); vazio = mantém o local.</summary>
    public static string? RespostasAAplicar(string? doPainel, string? local)
    {
        if (string.IsNullOrWhiteSpace(doPainel)) return null;
        var normal = RespostasProntas.Escrever(RespostasProntas.Ler(doPainel));
        var localNormal = string.IsNullOrWhiteSpace(local) ? "" : RespostasProntas.Escrever(RespostasProntas.Ler(local));
        return normal == localNormal ? null : normal;
    }

    /// <summary>Aplica a linha no SQLite (fora de transação: cada passo é idempotente). Devolve o que mudou.</summary>
    public static string Aplicar(SqliteConnection cx, Linha l, DateTime agora)
    {
        var mudou = new List<string>();

        var resp = RespostasAAplicar(l.ChatRespostas, Vendas.Config(cx, RespostasProntas.Chave));
        if (resp is not null)
        {
            Vendas.GravarConfig(cx, RespostasProntas.Chave, resp);
            Caixa.Auditar(cx, null, "respostas_chat_do_painel", null, null, $"{RespostasProntas.Ler(resp).Count} resposta(s) vindas do painel");
            mudou.Add("respostas do chat");
        }

        // l.AdminHash/AdminSalt/AdminEm são lidos e IGNORADOS de propósito (15/09/2026): ver o
        // resumo da classe. A senha das ações de admin desce só como usuário master (UsuarioMaster).

        var musicaAntes = string.Join("|", Vendas.Config(cx, ChavePlaylistUri), Vendas.Config(cx, ChaveDeviceId), Vendas.Config(cx, ChaveVolume));
        Gravar(cx, ChavePlaylistUri, l.PlaylistUri);
        Gravar(cx, ChavePlaylistNome, l.PlaylistNome);
        Gravar(cx, ChaveDeviceId, l.DeviceId);
        Gravar(cx, ChaveDeviceNome, l.DeviceNome);
        Gravar(cx, ChaveVolume, l.Volume?.ToString(CultureInfo.InvariantCulture));
        var musicaDepois = string.Join("|", Vendas.Config(cx, ChavePlaylistUri), Vendas.Config(cx, ChaveDeviceId), Vendas.Config(cx, ChaveVolume));
        if (musicaAntes != musicaDepois) mudou.Add("música");

        // RASPADINHA NO CAIXA (21/09/2026): o painel é a verdade. Campo ausente (servidor sem o
        // SQL 37) ou falso = desligado: sem a coluna o servidor também não tem as RPCs do brinde,
        // e cartão que só dá "o painel ainda não tem" é pior que cartão nenhum.
        var raspAntes = Vendas.Config(cx, Brindes.ChaveConfigLoja);
        Gravar(cx, Brindes.ChaveConfigLoja, l.RaspadinhaNoCaixa == true ? "1" : null);
        if (raspAntes != Vendas.Config(cx, Brindes.ChaveConfigLoja)) mudou.Add("raspadinha no caixa");

        return string.Join(", ", mudou);
    }

    private static void Gravar(SqliteConnection cx, string chave, string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) cx.Execute("DELETE FROM config WHERE chave=@C", new { C = chave });
        else Vendas.GravarConfig(cx, chave, valor);
    }
}
