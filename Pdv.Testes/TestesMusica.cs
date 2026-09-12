using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// MÚSICA DA LOJA (12/09/2026): a leitura das respostas do Spotify e da função de borda é
/// pura (Spotify.LerEstado/LerToken/MensagemErro) e provada pelo valor; a janela e o
/// botão da barra, pelo fonte.
/// </summary>
public static class TestesMusica
{
    public static void Rodar(Action<bool, string> checar)
    {
        var player = """
            {"device":{"id":"d1","name":"Caixa Savassi","volume_percent":42},"is_playing":true,
             "context":{"uri":"spotify:playlist:abc"},"progress_ms":1000,
             "item":{"name":"Blue Suede Shoes","duration_ms":120000,"artists":[{"name":"Elvis Presley"},{"name":"Banda"}]}}
            """;
        var e = Spotify.LerEstado(200, player);
        checar(e is { Tocando: true, Faixa: "Blue Suede Shoes", Artista: "Elvis Presley, Banda", Aparelho: "Caixa Savassi", AparelhoId: "d1", Volume: 42, Contexto: "spotify:playlist:abc" },
            "GET /me/player vira o estado (faixa, artistas, aparelho, volume, playlist)");
        checar(Spotify.LerEstado(204, "") is null && Spotify.LerEstado(200, "lixo") is null, "204 (nada tocando) e corpo ilegível = null, sem exceção");
        checar(Spotify.LerEstado(200, """{"is_playing":false,"item":null,"device":null}""") is { Tocando: false, Faixa: null, Aparelho: null },
            "campos nulos do Spotify não derrubam a leitura");

        checar(Spotify.MensagemErro(404, """{"error":{"status":404,"reason":"NO_ACTIVE_DEVICE","message":"Player command failed"}}""").Contains("aparelho"),
            "sem aparelho ativo: frase que diz para abrir o Spotify no aparelho");
        checar(Spotify.MensagemErro(403, """{"error":{"status":403,"reason":"PREMIUM_REQUIRED","message":"x"}}""").Contains("Premium"),
            "sem Premium: dito com todas as letras");
        checar(!Spotify.MensagemErro(500, "").Contains('—') && !Spotify.MensagemErro(429, "").Contains('—'), "sem travessão");

        var (tok, exp, erro) = Spotify.LerToken(200, """{"ok":true,"access_token":"abc","expires_in":3600}""");
        checar(tok == "abc" && exp == 3600 && erro is null, "a função devolve o token e a validade");
        var (tok2, _, erro2) = Spotify.LerToken(409, """{"ok":false,"error":"nao_conectado"}""");
        checar(tok2 is null && erro2 is not null && erro2.Contains("painel"), "não conectado: manda para o painel (Música)");
        checar(Spotify.LerToken(401, "").Token is null && Spotify.LerToken(401, "").Erro!.Contains("Atualizar"), "401: sessão do caixa, não do Spotify");
        checar(Spotify.LerToken(0, "").Erro == "Sem internet agora.", "status 0 = sem rede");

        var venda = Fonte(Path.Combine("Telas", "Venda.xaml")) ?? "";
        var vendaCs = Fonte(Path.Combine("Telas", "Venda.xaml.cs")) ?? "";
        var janela = Fonte(Path.Combine("Telas", "Musica.cs")) ?? "";
        var nuvem = Fonte(Path.Combine("Pdv.Nucleo", "Nuvem.cs")) ?? "";
        checar(venda.Contains("Click=\"AbrirMusica\"") && vendaCs.Contains("Musica.Abrir(Window.GetWindow(this)!)"),
            "a barra da venda tem o botão ♫ que abre a janela da música");
        checar(janela.Contains("ConfigLojaPainel.ChavePlaylistUri") && janela.Contains("_sp.TocarPlaylistAsync(_deviceId, _playlistUri") && !janela.Contains("/me/playlists"),
            "a janela toca a playlist do painel e não deixa escolher outra (\"eu escolho daqui o que vai tocar lá\")");
        checar(nuvem.Contains("\"/functions/v1/\" + nome") && nuvem.Contains("Spotify.LerToken(st, corpo)") && !nuvem.Contains("refresh_token\":"),
            "o caixa pede o access_token à função de borda; o refresh_token nunca chega aqui");
    }

    private static string? Fonte(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj")))
            {
                var c = Path.Combine(dir.FullName, relativo);
                return File.Exists(c) ? File.ReadAllText(c) : null;
            }
        return null;
    }
}
