namespace Pdv.Nucleo;

/// <summary>
/// O SPOTIFY DO CAIXA NO QUIOSQUE (15/10/2026, PDV 1.0.37). O dono: com o PDV no lugar da área
/// de trabalho, o Spotify só tocava depois de "liberar o desktop". O PDV abria o atalho da Loja
/// (que roda o SpotifyMigrator e sai), ninguém conferia se o app subiu logado, e o play ia para um
/// device_id guardado que o Spotify não garante. Aqui moram as regras puras (testadas em
/// TestesSpotifyNoCaixa): qual Spotify abrir, quando abrir de novo, quando esconder, e o laço
/// que toca a playlist da loja ao ligar o PC e só para quando o Spotify confirma que tocou.
/// O Windows (processo, janela, registro) fica em SpotifyNoCaixa, no caixa.
/// </summary>
public static class MusicaNoInicio
{
    public enum Versao { Nenhuma, Classica, Loja }

    /// <summary>O Spotify que o PDV abre: o executável, os argumentos e se ele está logado.</summary>
    public sealed record Escolha(Versao Versao, string? Exe, string Argumentos, bool Logada);

    public const string PacoteLoja = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0";
    /// <summary>A chave do arquivo prefs que só existe com uma conta logada. Só a presença conta: o valor é o usuário.</summary>
    public const string ChaveLogado = "autologin.canonical_username";

    public static string ExeClassico(string appData) => Path.Combine(appData, "Spotify", "Spotify.exe");
    /// <summary>O atalho de execução da Loja (abre o SpotifyMigrator, que abre o Spotify e sai).</summary>
    public static string ExeLoja(string localAppData) => Path.Combine(localAppData, "Microsoft", "WindowsApps", "Spotify.exe");
    public static string PrefsClassico(string appData) => Path.Combine(appData, "Spotify", "prefs");
    public static string PrefsLoja(string localAppData) => Path.Combine(localAppData, "Packages", PacoteLoja, "LocalState", "Spotify", "prefs");

    /// <summary>O prefs tem a chave do login? (linha "autologin.canonical_username=...").</summary>
    public static bool Logado(IEnumerable<string>? linhas)
    {
        if (linhas is null) return false;
        foreach (var l in linhas)
        {
            var i = l.IndexOf('=');
            if (i > 0 && l[..i].Trim() == ChaveLogado) return true;
        }
        return false;
    }

    /// <summary>
    /// Qual abrir: a versão logada; empate (as duas logadas ou nenhuma) fica com a clássica, que
    /// aceita "--minimized". A da Loja abre sem argumentos (o Migrator não conhece "--minimized").
    /// </summary>
    public static Escolha Escolher(bool temClassica, bool classicaLogada, bool temLoja, bool lojaLogada,
        string exeClassico, string exeLoja)
    {
        var classica = new Escolha(Versao.Classica, exeClassico, "--minimized", classicaLogada);
        var loja = new Escolha(Versao.Loja, exeLoja, "", lojaLogada);
        if (temClassica && temLoja) return lojaLogada && !classicaLogada ? loja : classica;
        if (temClassica) return classica;
        if (temLoja) return loja;
        return new Escolha(Versao.Nenhuma, null, "", false);
    }

    public static string Descrever(Escolha e) => e.Versao switch
    {
        Versao.Classica => "versão clássica, " + (e.Logada ? "logada" : "SEM login"),
        Versao.Loja => "versão da Loja, " + (e.Logada ? "logada" : "SEM login"),
        _ => "não instalado",
    };

    /// <summary>Os processos que são o Spotify tocando (as duas versões rodam como Spotify.exe).</summary>
    public static bool Rodando(IEnumerable<(string Nome, int Sessao)> processos, int sessao)
        => processos.Any(p => p.Sessao == sessao && string.Equals(p.Nome, "Spotify", StringComparison.OrdinalIgnoreCase));

    /// <summary>Os ajudantes que estão abrindo o Spotify agora (o da Loja passa pelo Migrator).</summary>
    public static bool Abrindo(IEnumerable<(string Nome, int Sessao)> processos, int sessao)
        => processos.Any(p => p.Sessao == sessao && p.Nome.ToLowerInvariant() is "spotifymigrator" or "spotifylauncher" or "spotifystartuptask");

    /// <summary>Folga entre duas aberturas: não briga com alguém fechando de propósito.</summary>
    public static readonly TimeSpan FolgaReabrir = TimeSpan.FromMinutes(2);

    /// <summary>Um dono só e sem duplicata: abre só se nada do Spotify está de pé nesta sessão e a folga passou.</summary>
    public static bool PodeAbrir(bool rodando, bool abrindo, DateTime agora, DateTime ultimaAbertura)
        => !rodando && !abrindo && agora - ultimaAbertura >= FolgaReabrir;

    /// <summary>Quanto tempo o botão "Mostrar o Spotify" deixa a janela à vista (login, aviso do app).</summary>
    public static readonly TimeSpan TempoMostrando = TimeSpan.FromMinutes(3);
    public static bool Escondendo(DateTime agora, DateTime mostrarAte) => agora >= mostrarAte;

    /// <summary>Tocar sozinho só quando o PC acabou de ligar: o PDV reaberto à noite (atualização) não liga o som.</summary>
    public static readonly TimeSpan JanelaDoInicio = TimeSpan.FromMinutes(15);
    public static bool TocarAoAbrir(TimeSpan windowsLigadoHa) => windowsLigadoHa >= TimeSpan.Zero && windowsLigadoHa <= JanelaDoInicio;

    /// <summary>Esperas entre as tentativas de tocar: 20, 40, 80, 160 s e depois de 5 em 5 min; null = desiste (cerca de 1 h).</summary>
    public const int MaxTentativas = 15;
    public static TimeSpan? Espera(int tentativa)
    {
        if (tentativa < 0 || tentativa >= MaxTentativas - 1) return null;
        return TimeSpan.FromSeconds(Math.Min(300, 20 * (1 << Math.Min(tentativa, 4))));
    }

    public enum Passo { SemPlaylist, CaixaForaDaLista, OutroTocando, JaTocandoAqui, Tocar }
    public sealed record Decisao(Passo Passo, string? DeviceId, string Motivo);

    private static bool Mesmo(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Uma tentativa: sem playlist não faz nada; com este caixa fora da lista espera; tocando aqui
    /// já está feito; tocando NOUTRO aparelho não mexe (nunca tira o som de outro lugar); com a
    /// conta parada, toca neste caixa pelo id de agora.
    /// </summary>
    public static Decisao Decidir(string? playlistUri, IReadOnlyList<Spotify.Aparelho> aparelhos, Spotify.Estado? estado, string maquina)
    {
        if (string.IsNullOrWhiteSpace(playlistUri)) return new Decisao(Passo.SemPlaylist, null, "sem playlist escolhida no painel (Música)");
        if (estado is { Tocando: true })
            return Mesmo(estado.Aparelho, maquina)
                ? new Decisao(Passo.JaTocandoAqui, estado.AparelhoId, "já está tocando neste caixa")
                : new Decisao(Passo.OutroTocando, null, $"a conta está tocando em outro aparelho ({estado.Aparelho ?? "sem nome"}); não mexo");
        var aqui = Spotify.AparelhoPorNome(aparelhos, maquina);
        if (aqui is null)
            return new Decisao(Passo.CaixaForaDaLista, null,
                $"este caixa ({maquina}) não está na lista de aparelhos ({(aparelhos.Count == 0 ? "lista vazia" : string.Join(", ", aparelhos.Select(a => a.Nome)))})");
        return new Decisao(Passo.Tocar, aqui.Id, "conta parada; toco aqui");
    }

    /// <summary>Confirmado = o Spotify diz que está tocando e o aparelho é este caixa.</summary>
    public static bool Confirmado(Spotify.Estado? e, string maquina) => e is { Tocando: true } && Mesmo(e.Aparelho, maquina);

    /// <summary>O que o laço precisa do mundo (a Web API, a config, o relógio e o log), para ser testado sem rede.</summary>
    public sealed record Ambiente(
        Func<CancellationToken, Task<(IReadOnlyList<Spotify.Aparelho> Aparelhos, string? Erro)>> Aparelhos,
        Func<CancellationToken, Task<(Spotify.Estado? Estado, string? Erro)>> Estado,
        Func<string, string, CancellationToken, Task<(int Status, string? Erro)>> Tocar,
        Func<int, string, CancellationToken, Task<string?>> Volume,
        Func<(string? Playlist, int? Volume)> Config,
        Func<TimeSpan, CancellationToken, Task> Esperar,
        Action<string> Anotar);

    /// <summary>Quanto esperar depois do play antes de perguntar se tocou.</summary>
    public static readonly TimeSpan EsperaConfirmar = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Toca a playlist da loja neste caixa e CONFIRMA. Tenta de novo nas <see cref="Espera"/> até
    /// confirmar, até ver outro aparelho tocando, faltar playlist, ou desistir. Nunca lança: erro de
    /// rede, do token ou do próprio ambiente vira linha de log e nova tentativa. true = confirmado.
    /// </summary>
    public static async Task<bool> TocarAteConfirmarAsync(Ambiente amb, string maquina, CancellationToken ct = default)
    {
        void Log(string t) { try { amb.Anotar(t); } catch { } }
        for (var t = 0; !ct.IsCancellationRequested; t++)
        {
            try
            {
                var (playlist, volume) = amb.Config();
                var (aps, erroAps) = await amb.Aparelhos(ct).ConfigureAwait(false);
                var (estado, erroEstado) = erroAps is null ? await amb.Estado(ct).ConfigureAwait(false) : (null, null);
                var erro = erroAps ?? erroEstado;
                if (erro is not null) Log($"tentativa {t + 1}: não deu para perguntar ao Spotify: {erro}");
                else
                {
                    var d = Decidir(playlist, aps, estado, maquina);
                    Log($"tentativa {t + 1}: {d.Motivo}");
                    if (d.Passo is Passo.SemPlaylist or Passo.OutroTocando) return false;
                    if (d.Passo == Passo.JaTocandoAqui) { Log("confirmado: tocando neste caixa"); return true; }
                    if (d.Passo == Passo.Tocar)
                    {
                        var (st, erroPlay) = await amb.Tocar(d.DeviceId!, playlist!, ct).ConfigureAwait(false);
                        Log($"play: HTTP {st}" + (erroPlay is null ? "" : " " + erroPlay));
                        if (erroPlay is null)
                        {
                            if (volume is { } v)
                            {
                                var erroVol = await amb.Volume(v, d.DeviceId!, ct).ConfigureAwait(false);
                                if (erroVol is not null) Log($"volume {v}%: {erroVol}");
                            }
                            await amb.Esperar(EsperaConfirmar, ct).ConfigureAwait(false);
                            var (depois, erroDepois) = await amb.Estado(ct).ConfigureAwait(false);
                            if (Confirmado(depois, maquina)) { Log("confirmado: tocando neste caixa"); return true; }
                            Log(erroDepois is not null ? "não confirmou: " + erroDepois
                                : $"não confirmou: tocando={(depois?.Tocando == true ? "sim" : "não")}, aparelho={depois?.Aparelho ?? "nenhum"}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
            catch (Exception ex) { Log($"tentativa {t + 1}: falhou ({ex.GetType().Name})"); }

            var espera = Espera(t);
            if (espera is null) { Log($"desisti depois de {t + 1} tentativas; o play pelo painel continua valendo"); return false; }
            Log($"nova tentativa em {(int)espera.Value.TotalSeconds} s");
            try { await amb.Esperar(espera.Value, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex) { Log("espera falhou: " + ex.GetType().Name); return false; }
        }
        return false;
    }
}
