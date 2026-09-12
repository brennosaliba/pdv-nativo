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
///  · senha de administrador do caixa (a que abre a Configuração), definida no painel;
///  · música da loja (Spotify): playlist, aparelho e volume escolhidos no painel.
/// Vem tudo da RPC pdv_loja_config_caixa (uma linha por loja que o usuário alcança).
///
/// Regras (puras, provadas na suíte):
///  · a linha da loja é a de nome igual ao do terminal (sem acento, sem caixa); se o
///    usuário alcança uma só, é ela;
///  · respostas: o painel manda texto = o caixa passa a usar esse texto; painel vazio =
///    o caixa mantém o que tem (nunca apaga o que a loja editou no próprio caixa);
///  · senha: só é reescrita quando o painel a definiu DEPOIS da última que o caixa
///    aplicou (mesma lógica do pin_nuvem_hash dos operadores: o ciclo de sincronização
///    passa a toda hora, a troca de senha é um ato);
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
        string? PlaylistUri, string? PlaylistNome, string? DeviceId, string? DeviceNome, int? Volume);

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
                var store = S("store");
                if (string.IsNullOrWhiteSpace(store)) continue;
                DateTime? em = DateTime.TryParse(S("admin_pin_atualizado"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : null;
                saida.Add(new Linha(store, S("chat_respostas"), S("admin_pin_hash"), S("admin_pin_salt"), em,
                    S("playlist_uri"), S("playlist_nome"), S("device_id"), S("device_nome"), I("volume")));
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

    /// <summary>A senha do painel é mais nova que a última aplicada aqui?</summary>
    public static bool AdminPinNovo(DateTime? painelEm, DateTime? aplicadoEm)
        => painelEm is { } p && (aplicadoEm is null || p > aplicadoEm.Value.AddSeconds(1));

    /// <summary>Respostas: o texto do painel vale (normalizado); vazio = mantém o local.</summary>
    public static string? RespostasAAplicar(string? doPainel, string? local)
    {
        if (string.IsNullOrWhiteSpace(doPainel)) return null;
        var normal = RespostasProntas.Escrever(RespostasProntas.Ler(doPainel));
        var localNormal = string.IsNullOrWhiteSpace(local) ? "" : RespostasProntas.Escrever(RespostasProntas.Ler(local));
        return normal == localNormal ? null : normal;
    }

    public static DateTime? UltimoAdminAplicado(SqliteConnection cx)
        => DateTime.TryParse(Vendas.Config(cx, ChaveAdminAplicadoEm), CultureInfo.InvariantCulture,
               DateTimeStyles.RoundtripKind, out var d) ? d : null;

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

        if (l.AdminHash is { Length: > 0 } && l.AdminSalt is { Length: > 0 } && AdminPinNovo(l.AdminEm, UltimoAdminAplicado(cx)))
        {
            cx.Execute("""
                INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,ativo,atualizado)
                VALUES ('_admin_','Administrador (painel)',@H,@S,'gerente',0,@Em)
                ON CONFLICT(id) DO UPDATE SET pin_hash=@H, pin_salt=@S, atualizado=@Em
                """, new { H = l.AdminHash, S = l.AdminSalt, Em = agora.ToString("o") });
            Vendas.GravarConfig(cx, ChaveAdminAplicadoEm, l.AdminEm!.Value.ToString("o"));
            Caixa.Auditar(cx, null, "senha_admin_do_painel", null, null, $"definida no painel em {l.AdminEm:dd/MM HH:mm}");
            mudou.Add("senha de administrador");
        }

        var musicaAntes = string.Join("|", Vendas.Config(cx, ChavePlaylistUri), Vendas.Config(cx, ChaveDeviceId), Vendas.Config(cx, ChaveVolume));
        Gravar(cx, ChavePlaylistUri, l.PlaylistUri);
        Gravar(cx, ChavePlaylistNome, l.PlaylistNome);
        Gravar(cx, ChaveDeviceId, l.DeviceId);
        Gravar(cx, ChaveDeviceNome, l.DeviceNome);
        Gravar(cx, ChaveVolume, l.Volume?.ToString(CultureInfo.InvariantCulture));
        var musicaDepois = string.Join("|", Vendas.Config(cx, ChavePlaylistUri), Vendas.Config(cx, ChaveDeviceId), Vendas.Config(cx, ChaveVolume));
        if (musicaAntes != musicaDepois) mudou.Add("música");

        return string.Join(", ", mudou);
    }

    private static void Gravar(SqliteConnection cx, string chave, string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) cx.Execute("DELETE FROM config WHERE chave=@C", new { C = chave });
        else Vendas.GravarConfig(cx, chave, valor);
    }
}
