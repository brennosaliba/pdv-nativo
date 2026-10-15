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

        checar(SpotifyNoCaixa.Chave == "spotify_escondido", "a chave de config é estável (o painel e o caixa leem o mesmo nome)");

        RegrasDoInicio(checar);

        var app = Fonte("App.xaml.cs") ?? "";
        var conf = Fonte(Path.Combine("Telas", "Configuracao.xaml.cs")) ?? "";
        var confXaml = Fonte(Path.Combine("Telas", "Configuracao.xaml")) ?? "";
        var musica = Fonte(Path.Combine("Telas", "Musica.cs")) ?? "";
        var sp = Fonte("SpotifyNoCaixa.cs") ?? "";
        var nucleo = Fonte(Path.Combine("Pdv.Nucleo", "MusicaNoInicio.cs")) ?? "";
        var iw = Fonte("InicioDoWindowsNoCaixa.cs") ?? "";
        checar(app.Contains("SpotifyNoCaixa.AoAbrirOPdv(Vendas.Config(cx, SpotifyNoCaixa.Chave));")
               && sp.Contains("if (opcao != \"1\") { Log(\"desligado na Configuração\"); return; }")
               && sp.Contains("catch (Exception ex) { Log(\"ao abrir o PDV: \" + ex.GetType().Name); }"),
            "ao iniciar, o PDV entrega a opção ao Spotify do caixa, que anota até o \"desligado\" e nunca lança");
        checar(confXaml.Contains("x:Name=\"ChkSpotifyEscondido\"") && conf.Contains("SpotifyNoCaixa.Iniciar()") && conf.Contains("SpotifyNoCaixa.Parar()"),
            "a Configuração tem a opção e já aplica ao salvar");
        checar(musica.Contains("_sp.AparelhosAsync()") && musica.Contains("SpotifyNoCaixa.EsteCaixaNaLista(aps, Environment.MachineName)"),
            "a janela da música diz se este caixa está na lista de aparelhos");
        checar(sp.Contains("SW_HIDE") && !sp.Contains("SW_MINIMIZE") && sp.Contains("EnumWindows(") && nucleo.Contains("\"--minimized\"")
               && nucleo.Contains("FolgaReabrir = TimeSpan.FromMinutes(2)") && sp.Contains("TimeSpan.FromSeconds(5)"),
            "1.0.33: o Spotify sobe minimizado e toda janela dele é ESCONDIDA (fora do Alt+Tab) a cada 5 s; a reabertura tem folga de 2 min");
        checar(sp.Contains("lock (_escondidas) nova = _escondidas.Add(h);") && sp.Contains("ShowWindow(h, SW_SHOWMINNOACTIVE)")
               && sp.IndexOf("ShowWindow(h, SW_SHOWMINNOACTIVE)", StringComparison.Ordinal) > sp.IndexOf("public static void Parar()", StringComparison.Ordinal),
            "1.0.33 (revisão): desligar a opção devolve as janelas que o PDV escondeu (um Spotify pedindo login não some para sempre)");
        checar(!sp.Contains("Kill(") && !sp.Contains("senha", StringComparison.OrdinalIgnoreCase) || sp.Contains("Senha é dele"),
            "o PDV nunca mata o Spotify nem mexe em senha: só abre e esconde");

        // SP-13..17 (1.0.37) o lado Windows, pelo fonte
        var iEsc = sp.IndexOf("public static bool Esconder()", StringComparison.Ordinal);
        checar(iEsc > 0 && sp.IndexOf("PidsDoSpotify(Sessao)", iEsc, StringComparison.Ordinal) > iEsc
               && sp.Contains("if (p.SessionId == sessao) pids.Add((uint)p.Id);")
               && sp.Contains("MusicaNoInicio.PodeAbrir(MusicaNoInicio.Rodando(procs, sessao), MusicaNoInicio.Abrindo(procs, sessao), DateTime.Now, _ultimaAbertura)"),
            "SP-13 esconde e conta só o Spotify desta sessão; abrir passa pela regra anti-duplicata");
        checar(sp.Contains("await EsperarRedeAsync(TimeSpan.FromSeconds(90))") && sp.Contains("await ConfirmarProcessoAsync(sessao, TimeSpan.FromSeconds(30))")
               && sp.Contains("Dns.GetHostAddressesAsync(\"api.spotify.com\"") && sp.Contains("Process.GetProcessesByName(\"sihost\")"),
            "SP-14 abre depois da rede (até 90 s), confere o Spotify.exe nesta sessão (até 30 s), e mede o logon pelo sihost (inicialização rápida não zera o relógio)");
        checar(sp.Contains("InicioDoWindowsNoCaixa.Anotar(\"[spotify] \" + texto)") && iw.Contains("internal static void Anotar(string texto)")
               && iw.Contains("lock (_travaLog)") && iw.Contains("\"inicio-windows.txt\""),
            "SP-15 o log do Spotify vai para ProgramData/PdvNativo/inicio-windows.txt com [spotify], com trava (escreve de outra thread)");
        checar(sp.Contains("catch (Exception ex) { Log(\"abrir: \" + ex.GetType().Name); }")
               && sp.Contains("catch (Exception ex) { Log(\"tocar sozinho: \" + ex.GetType().Name); }")
               && sp.Contains("catch (Exception ex) { Log(\"mostrar: \" + ex.GetType().Name); return \"Não consegui mostrar o Spotify.\"; }")
               && sp.Contains("cts.CancelAfter(TimeSpan.FromSeconds(10));"),
            "SP-16 abrir, tocar sozinho e mostrar rodam com try/catch e cada pergunta ao Spotify tem prazo de 10 s: falha do Spotify não derruba o PDV");
        checar(!sp.Contains("schtasks", StringComparison.OrdinalIgnoreCase) && !sp.Contains("TaskScheduler") && !sp.Contains("SetWindowsHookEx")
               && !sp.Contains("Winlogon") && !sp.Contains("Policies") && !sp.Contains("CreateProcessAsUser"),
            "SP-17 sem tarefa agendada, sem sessão de serviço, sem gancho de teclado e sem mexer no Winlogon nem nas políticas");
        var esc = SpotifyNoCaixa.Escolha();
        var som = SpotifyNoCaixa.SaidaDeSomPadrao();
        checar(som.Length > 0, "SP-18 ler a saída de som padrão deste PC não lança");
        checar(confXaml.Contains("x:Name=\"BtnMostrarSpotify\"") && confXaml.Contains("Click=\"MostrarSpotify\"")
               && conf.Contains("TxtSpotifyEstado.Text = SpotifyNoCaixa.Mostrar();") && conf.Contains("spotify.Logada ?")
               && sp.Contains("_mostrarAte = DateTime.Now + MusicaNoInicio.TempoMostrando;"),
            "SP-19 a Configuração (admin) diz se o Spotify está logado e mostra a janela por 3 min para entrar na conta pelo AnyDesk");
        var textos = conf[conf.IndexOf("var spotify = SpotifyNoCaixa.Escolha();", StringComparison.Ordinal)..][..900] + sp;
        checar(!textos.Contains('—') && !textos.Contains('–'), "SP-20 os textos e o log do Spotify não têm travessão");
        Console.WriteLine($"  (info) Spotify deste PC: {MusicaNoInicio.Descrever(esc)}; saída de som: {som}");
    }

    /// <summary>
    /// SP-1..12 (15/10/2026, 1.0.37): o Spotify só tocava depois de liberar a área de trabalho. As
    /// regras puras de MusicaNoInicio: qual versão abrir, anti-duplicata, esconder, quando tocar
    /// sozinho, e o laço que toca e CONFIRMA (com o mundo de mentira: sem rede, sem relógio).
    /// </summary>
    private static void RegrasDoInicio(Action<bool, string> checar)
    {
        // SP-1 logado = a chave do login no prefs (só a presença; o valor é o usuário)
        checar(MusicaNoInicio.Logado(new[] { "app.autostart-configured=true", "autologin.canonical_username=\"x\"" })
               && MusicaNoInicio.Logado(new[] { " autologin.canonical_username = \"x\"" })
               && !MusicaNoInicio.Logado(new[] { "autologin.username=\"x\"", "# autologin.canonical_username" })
               && !MusicaNoInicio.Logado(null) && !MusicaNoInicio.Logado(Array.Empty<string>()),
            "SP-1 logado é a chave autologin.canonical_username no prefs; outra chave, comentário ou arquivo vazio não é");
        // SP-2 a escolha: a logada; empate ou nenhuma, a clássica com --minimized; a da Loja sem argumento
        const string cl = @"C:\R\Spotify\Spotify.exe", lj = @"C:\L\Microsoft\WindowsApps\Spotify.exe";
        var e1 = MusicaNoInicio.Escolher(true, false, true, true, cl, lj);
        var e2 = MusicaNoInicio.Escolher(true, true, true, true, cl, lj);
        var e3 = MusicaNoInicio.Escolher(true, false, true, false, cl, lj);
        var e4 = MusicaNoInicio.Escolher(false, false, true, false, cl, lj);
        var e5 = MusicaNoInicio.Escolher(true, true, false, false, cl, lj);
        var e6 = MusicaNoInicio.Escolher(false, false, false, false, cl, lj);
        checar(e1 is { Versao: MusicaNoInicio.Versao.Loja, Exe: lj, Argumentos: "", Logada: true }
               && e2 is { Versao: MusicaNoInicio.Versao.Classica, Exe: cl, Argumentos: "--minimized" }
               && e3 is { Versao: MusicaNoInicio.Versao.Classica, Logada: false }
               && e4 is { Versao: MusicaNoInicio.Versao.Loja, Argumentos: "" }
               && e5 is { Versao: MusicaNoInicio.Versao.Classica, Logada: true }
               && e6 is { Versao: MusicaNoInicio.Versao.Nenhuma, Exe: null },
            "SP-2 abre a versão logada; empate fica com a clássica (--minimized); a da Loja vai sem argumento; nenhuma = null");
        checar(MusicaNoInicio.ExeLoja(@"C:\U\AppData\Local").EndsWith(@"WindowsApps\Spotify.exe")
               && MusicaNoInicio.PrefsLoja(@"C:\U\AppData\Local").EndsWith(@"Packages\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\LocalState\Spotify\prefs")
               && MusicaNoInicio.PrefsClassico(@"C:\U\AppData\Roaming") == @"C:\U\AppData\Roaming\Spotify\prefs"
               && MusicaNoInicio.Descrever(e1) == "versão da Loja, logada" && MusicaNoInicio.Descrever(e3) == "versão clássica, SEM login",
            "SP-3 os caminhos de cada versão e a frase do log");
        // SP-4 anti-duplicata: só esta sessão, qualquer versão (as duas são Spotify.exe); ajudante abrindo também segura
        var procs = new[] { ("Spotify", 0), ("spotifymigrator", 2), ("explorer", 1) };
        checar(!MusicaNoInicio.Rodando(procs, 1) && MusicaNoInicio.Rodando(procs, 0)
               && MusicaNoInicio.Abrindo(procs, 2) && !MusicaNoInicio.Abrindo(procs, 1)
               && MusicaNoInicio.Rodando(new[] { ("SPOTIFY", 1) }, 1),
            "SP-4 o Spotify de outra sessão não conta; nesta sessão conta qualquer versão; o Migrator abrindo segura a próxima abertura");
        var t0 = new DateTime(2026, 10, 15, 9, 0, 0);
        checar(MusicaNoInicio.PodeAbrir(false, false, t0, DateTime.MinValue)
               && !MusicaNoInicio.PodeAbrir(true, false, t0, DateTime.MinValue) && !MusicaNoInicio.PodeAbrir(false, true, t0, DateTime.MinValue)
               && !MusicaNoInicio.PodeAbrir(false, false, t0, t0.AddSeconds(-119)) && MusicaNoInicio.PodeAbrir(false, false, t0, t0.AddMinutes(-2)),
            "SP-5 abre só sem Spotify de pé e sem ajudante abrindo, e no máximo uma vez a cada 2 min");
        // SP-6 esconder: sempre, menos nos 3 min do "Mostrar o Spotify"
        checar(MusicaNoInicio.Escondendo(t0, DateTime.MinValue) && !MusicaNoInicio.Escondendo(t0, t0 + MusicaNoInicio.TempoMostrando)
               && MusicaNoInicio.TempoMostrando == TimeSpan.FromMinutes(3) && MusicaNoInicio.Escondendo(t0.AddMinutes(3), t0.AddMinutes(3)),
            "SP-6 a vigia esconde sempre, menos nos 3 min em que o admin pediu para ver o Spotify");
        // SP-7 tocar sozinho só com o PC recém ligado (PDV reaberto à noite não liga o som)
        checar(MusicaNoInicio.TocarAoAbrir(TimeSpan.FromMinutes(2)) && MusicaNoInicio.TocarAoAbrir(TimeSpan.FromMinutes(15))
               && !MusicaNoInicio.TocarAoAbrir(TimeSpan.FromMinutes(16)) && !MusicaNoInicio.TocarAoAbrir(TimeSpan.FromHours(9)),
            "SP-7 toca sozinho só nos primeiros 15 min do Windows ligado");
        // SP-8 as esperas: 20/40/80/160 s, depois 5 min, e desiste perto de 1 h
        var esperas = Enumerable.Range(0, 20).Select(MusicaNoInicio.Espera).TakeWhile(x => x is not null).Select(x => (int)x!.Value.TotalSeconds).ToList();
        checar(esperas.Take(6).SequenceEqual(new[] { 20, 40, 80, 160, 300, 300 }) && esperas.Count == MusicaNoInicio.MaxTentativas - 1
               && esperas.Sum() is > 3000 and <= 3600 && MusicaNoInicio.Espera(-1) is null,
            $"SP-8 tenta de novo em 20/40/80/160 s, depois de 5 em 5 min, e desiste perto de 1 h ({esperas.Sum()} s)");

        // SP-9 a decisão de cada tentativa
        var aqui = new Spotify.Aparelho("id-novo", "DESKTOP-7AJ1OD7", "Computer", false);
        var outro = new Spotify.Aparelho("o1", "Celular", "Smartphone", true);
        Spotify.Estado Est(bool toc, string? ap) => new(toc, "f", "a", ap, ap is null ? null : "x", 50, "spotify:playlist:p");
        var dSem = MusicaNoInicio.Decidir("", new[] { aqui }, null, "DESKTOP-7AJ1OD7");
        var dFora = MusicaNoInicio.Decidir("spotify:playlist:p", new[] { outro }, null, "DESKTOP-7AJ1OD7");
        var dOutro = MusicaNoInicio.Decidir("spotify:playlist:p", new[] { aqui, outro }, Est(true, "Celular"), "DESKTOP-7AJ1OD7");
        var dJa = MusicaNoInicio.Decidir("spotify:playlist:p", new[] { aqui }, Est(true, "desktop-7aj1od7"), "DESKTOP-7AJ1OD7");
        var dToca = MusicaNoInicio.Decidir("spotify:playlist:p", new[] { outro, aqui }, Est(false, "Celular"), "DESKTOP-7AJ1OD7");
        checar(dSem.Passo == MusicaNoInicio.Passo.SemPlaylist && dFora.Passo == MusicaNoInicio.Passo.CaixaForaDaLista
               && dOutro.Passo == MusicaNoInicio.Passo.OutroTocando && dJa.Passo == MusicaNoInicio.Passo.JaTocandoAqui
               && dToca is { Passo: MusicaNoInicio.Passo.Tocar, DeviceId: "id-novo" } && dFora.Motivo.Contains("Celular"),
            "SP-9 sem playlist não toca; caixa fora da lista espera; outro aparelho TOCANDO não perde o som; pausado noutro lugar toca aqui, com o id de agora");
        checar(MusicaNoInicio.Confirmado(Est(true, "Desktop-7aj1od7"), "DESKTOP-7AJ1OD7") && !MusicaNoInicio.Confirmado(Est(false, "DESKTOP-7AJ1OD7"), "DESKTOP-7AJ1OD7")
               && !MusicaNoInicio.Confirmado(Est(true, "Celular"), "DESKTOP-7AJ1OD7") && !MusicaNoInicio.Confirmado(null, "DESKTOP-7AJ1OD7"),
            "SP-10 confirmado só com is_playing e o aparelho com o nome deste caixa");
        // id de agora pelo nome; o guardado é só reserva
        var lista = new[] { new Spotify.Aparelho("", "Caixa", "Computer", false), new Spotify.Aparelho("velho", "Caixa", "Computer", false), new Spotify.Aparelho("ativo", "caixa", "Computer", true) };
        checar(Spotify.IdAtual(lista, "CAIXA", "guardado") == "ativo" && Spotify.IdAtual(lista, "Outro", "guardado") == "guardado"
               && Spotify.IdAtual(lista, null, "") is null && Spotify.AparelhoPorNome(new[] { lista[0] }, "Caixa") is null,
            "SP-11 o id do aparelho vem da lista de agora pelo nome (o ativo primeiro, nunca sem id); fora da lista, o guardado");

        // SP-12 o laço, com o mundo de mentira
        (bool Ok, List<string> Log, List<string> Plays, List<int> Esperas) Rodar(
            Func<int, (IReadOnlyList<Spotify.Aparelho>, string?)> aps, Func<int, Spotify.Estado?> estado,
            (int, string?) play, string? playlist = "spotify:playlist:p", bool explodir = false)
        {
            var log = new List<string>(); var plays = new List<string>(); var esp = new List<int>();
            int nAps = 0, nEst = 0;
            var amb = new MusicaNoInicio.Ambiente(
                _ => explodir ? throw new InvalidOperationException("boom") : Task.FromResult(aps(nAps++)),
                _ => Task.FromResult<(Spotify.Estado?, string?)>((estado(nEst++), null)),
                (dev, uri, _) => { plays.Add(dev + " " + uri); return Task.FromResult(play); },
                (v, dev, _) => Task.FromResult<string?>(null),
                () => (playlist, 55),
                (ts, _) => { esp.Add((int)ts.TotalSeconds); return Task.CompletedTask; },
                log.Add);
            var ok = MusicaNoInicio.TocarAteConfirmarAsync(amb, "DESKTOP-7AJ1OD7").GetAwaiter().GetResult();
            return (ok, log, plays, esp);
        }
        IReadOnlyList<Spotify.Aparelho> L(params Spotify.Aparelho[] a) => a;
        // o caixa entra na lista na 2ª pergunta; toca com o id de agora; confirma depois de 6 s
        var r1 = Rodar(n => (n == 0 ? L(outro) : L(outro, aqui), null), n => n == 0 ? null : n == 1 ? Est(false, "Celular") : Est(true, "DESKTOP-7AJ1OD7"), (204, null));
        checar(r1.Ok && r1.Plays.SequenceEqual(new[] { "id-novo spotify:playlist:p" }) && r1.Esperas.SequenceEqual(new[] { 20, 6 })
               && r1.Log.Any(l => l.Contains("play: HTTP 204")) && r1.Log[^1] == "confirmado: tocando neste caixa",
            $"SP-12a caixa ainda fora da lista: espera 20 s, toca com o id de agora, confere em 6 s e para ({string.Join(" | ", r1.Log)})");
        var r2 = Rodar(_ => (L(aqui, outro), null), _ => Est(true, "Celular"), (204, null));
        checar(!r2.Ok && r2.Plays.Count == 0 && r2.Esperas.Count == 0 && r2.Log.Any(l => l.Contains("outro aparelho")),
            "SP-12b outro aparelho tocando: não manda play e para (nunca tira o som de outro lugar)");
        var r3 = Rodar(_ => (L(aqui), null), _ => null, (204, null), playlist: null);
        checar(!r3.Ok && r3.Plays.Count == 0 && r3.Log.Any(l => l.Contains("sem playlist")), "SP-12c sem playlist no painel: não toca e diz por quê");
        var r4 = Rodar(_ => (L(aqui), null), _ => null, (404, "Nenhum aparelho com o Spotify aberto agora."));
        checar(!r4.Ok && r4.Plays.Count == MusicaNoInicio.MaxTentativas && r4.Esperas.Take(5).SequenceEqual(new[] { 20, 40, 80, 160, 300 })
               && r4.Log.Any(l => l.Contains("play: HTTP 404")) && r4.Log[^1].StartsWith("desisti"),
            "SP-12d play recusado: anota o HTTP e o motivo, tenta de novo com folga crescente e desiste perto de 1 h");
        var r5 = Rodar(_ => (L(aqui), null), _ => Est(false, "DESKTOP-7AJ1OD7"), (202, null));
        checar(!r5.Ok && r5.Plays.Count == MusicaNoInicio.MaxTentativas && r5.Log.Any(l => l.Contains("não confirmou: tocando=não")),
            "SP-12e o Spotify aceitou (202) mas não tocou: não conta como certo, tenta de novo");
        var r6 = Rodar(_ => (L(aqui), null), _ => null, (204, null), explodir: true);
        checar(!r6.Ok && r6.Log.Any(l => l.Contains("falhou (InvalidOperationException)")) && r6.Log[^1].StartsWith("desisti"),
            "SP-12f erro do próprio ambiente nunca escapa do laço (falha do Spotify não derruba o PDV)");
        var r7 = Rodar(_ => (Array.Empty<Spotify.Aparelho>(), "Sem internet agora."), _ => null, (204, null));
        checar(!r7.Ok && r7.Plays.Count == 0 && r7.Log[0].Contains("não deu para perguntar ao Spotify: Sem internet agora."),
            "SP-12g sem rede ou sem token: anota e tenta de novo");
        var r8 = Rodar(_ => (L(aqui), null), _ => Est(true, "DESKTOP-7AJ1OD7"), (204, null));
        checar(r8.Ok && r8.Plays.Count == 0, "SP-12h já tocando neste caixa: nada a fazer");
        checar(r1.Log.Concat(r2.Log).Concat(r4.Log).All(l => !l.Contains('\u2014') && !l.Contains('\u2013')), "SP-12i o log não tem travessão");
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
