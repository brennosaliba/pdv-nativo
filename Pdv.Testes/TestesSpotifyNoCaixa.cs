using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O SPOTIFY ESCONDIDO ATRÁS DO PDV (12/09/2026). O dono conectou a conta e viu na lista
/// de aparelhos só o PC do escritório: a lista é a do Spotify (quem está com o app aberto
/// e logado), e o caixa em modo quiosque não roda o Spotify. A opção faz o PDV abrir o
/// app minimizado; a janela da música diz se este caixa está na lista.
///
/// Puro: o parser da lista, a comparação com o nome da máquina e os caminhos procurados.
/// O resto (processo, janela, vigia) é Windows e fica travado pelo fonte.
/// </summary>
public static class TestesSpotifyNoCaixa
{
    public static void Rodar(Action<bool, string> checar)
    {
        const string corpo = """
            {"devices":[
              {"id":"a1","is_active":false,"name":"DESKTOP-JFV9JVQ","type":"Computer","volume_percent":50},
              {"id":"b2","is_active":true,"name":"Caixa de som da loja","type":"Speaker","volume_percent":80},
              {"name":"sem id","type":"Smartphone"}
            ]}
            """;
        var aps = Spotify.LerAparelhos(200, corpo);
        checar(aps.Count == 3 && aps[0].Nome == "DESKTOP-JFV9JVQ" && aps[0].Tipo == "Computer" && !aps[0].Ativo,
            "a lista traz nome, tipo e se está ativo");
        checar(aps[1].Ativo && aps[1].Id == "b2", "o aparelho ativo vem marcado");
        checar(aps[2].Id == "" , "aparelho sem id (restrito) entra com id vazio, não derruba a lista");
        checar(Spotify.LerAparelhos(200, "lixo").Count == 0 && Spotify.LerAparelhos(401, corpo).Count == 0 && Spotify.LerAparelhos(200, "{}").Count == 0,
            "corpo ilegível, status errado ou sem devices = lista vazia, sem exceção");

        checar(SpotifyNoCaixa.EsteCaixaNaLista(aps, "desktop-jfv9jvq"), "este PC está na lista (sem diferenciar caixa alta)");
        checar(!SpotifyNoCaixa.EsteCaixaNaLista(aps, "DESKTOP-7AJ1OD7"), "o caixa da loja não está (é o caso do dono)");
        checar(!SpotifyNoCaixa.EsteCaixaNaLista(Array.Empty<Spotify.Aparelho>(), "X"), "lista vazia: não está");

        var cands = SpotifyNoCaixa.Candidatos(@"C:\U\AppData\Local", @"C:\U\AppData\Roaming").ToList();
        checar(cands.Count == 2 && cands[0].EndsWith(@"WindowsApps\Spotify.exe") && cands[1].EndsWith(@"Spotify\Spotify.exe"),
            "procura a versão da Loja (atalho de execução) e a clássica, nessa ordem");
        checar(SpotifyNoCaixa.Chave == "spotify_escondido", "a chave de config é estável (o painel e o caixa leem o mesmo nome)");

        var app = Fonte("App.xaml.cs") ?? "";
        var conf = Fonte(Path.Combine("Telas", "Configuracao.xaml.cs")) ?? "";
        var confXaml = Fonte(Path.Combine("Telas", "Configuracao.xaml")) ?? "";
        var musica = Fonte(Path.Combine("Telas", "Musica.cs")) ?? "";
        var sp = Fonte("SpotifyNoCaixa.cs") ?? "";
        checar(app.Contains("if (Vendas.Config(cx, SpotifyNoCaixa.Chave) == \"1\") SpotifyNoCaixa.Iniciar();"),
            "ao iniciar, com a opção ligada, o PDV abre o Spotify escondido");
        checar(confXaml.Contains("x:Name=\"ChkSpotifyEscondido\"") && conf.Contains("SpotifyNoCaixa.Iniciar()") && conf.Contains("SpotifyNoCaixa.Parar()"),
            "a Configuração tem a opção e já aplica ao salvar");
        checar(musica.Contains("_sp.AparelhosAsync()") && musica.Contains("SpotifyNoCaixa.EsteCaixaNaLista(aps, Environment.MachineName)"),
            "a janela da música diz se este caixa está na lista de aparelhos");
        checar(sp.Contains("SW_MINIMIZE") && sp.Contains("\"--minimized\"") && sp.Contains("TimeSpan.FromMinutes(2)"),
            "o Spotify sobe minimizado, a janela é minimizada se aparecer, e a reabertura tem folga de 2 min (não briga com quem fechou de propósito)");
        checar(!sp.Contains("Kill(") && !sp.Contains("senha", StringComparison.OrdinalIgnoreCase) || sp.Contains("Senha é dele"),
            "o PDV nunca mata o Spotify nem mexe em senha: só abre e esconde");
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
