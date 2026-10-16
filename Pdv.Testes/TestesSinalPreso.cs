using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O SINAL DE 60 s QUE MORREU EM SILÊNCIO (1.0.41, 16/10/2026, Savassi). Duas vezes (12:32 e 13:26) o sinal
/// do chat parou com o PDV vivo e a página do Gestor ok: a rodada anterior ficou presa num await sem prazo e o
/// <c>SemaphoreSlim.WaitAsync(0)</c> das seguintes voltava em silêncio. O ERP alarmou "chat caiu", a equipe
/// recriou o chat 6 vezes à toa e 3 pedidos ficaram sem resposta.
///
///  GR  a guarda com dono e hora: rodada presa há 90 s é abandonada, a geração velha não solta a nova
///  PZ  os prazos: um await que nunca termina não trava a rodada seguinte
///  RB  o Reabrir com a página viva não recria o WebView2 (só manda o sinal)
///  FO  o fonte: a guarda e os prazos estão no SinalAsync; nada de travessão
/// </summary>
public static class TestesSinalPreso
{
    private static readonly DateTime T0 = new(2026, 10, 16, 12, 32, 0);

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        AGuarda(checar);
        await OsPrazosAsync(checar);
        OReabrir(checar);
        PeloFonte(checar);
    }

    private static void AGuarda(Action<bool, string> checar)
    {
        var g = new GuardaDaRodada();
        var e1 = g.Entrar(T0);
        checar(e1 is { Geracao: 1, EtapaAbandonada: null } && g.EmCurso is { Etapa: "inicio" }, "GR-1 a primeira rodada entra com a geração 1");
        checar(g.Entrar(T0.AddSeconds(30)) is null && g.Entrar(T0.AddSeconds(89)) is null, "GR-1b outra rodada com a anterior em curso há menos de 90 s não entra (e o relógio segue)");
        checar(g.Sair(1) && g.EmCurso is null, "GR-1c Sair solta a guarda");
        checar(!g.Sair(1), "GR-1d Sair de novo não faz nada");
        var e2 = g.Entrar(T0.AddSeconds(100));
        checar(e2 is { Geracao: 2, EtapaAbandonada: null }, "GR-1e a rodada seguinte entra com a geração 2");

        // a rodada 2 anota as etapas e fica presa na "rede"
        var ant = g.Etapa(2, "banco", T0.AddSeconds(101));
        var ant2 = g.Etapa(2, "rede", T0.AddSeconds(108));
        checar(ant is { Etapa: "inicio" } && ant2 is { Etapa: "banco" } && ant2.Value.Levou == TimeSpan.FromSeconds(7),
            "GR-2 cada etapa devolve a anterior e quanto ela levou (o diag diz 'etapa X levou N s')");
        checar(g.Entrar(T0.AddSeconds(189)) is null, "GR-2b aos 89 s presa ainda não é abandonada");
        var e3 = g.Entrar(T0.AddSeconds(190));
        checar(e3 is { Geracao: 3, EtapaAbandonada: "rede" } && e3.PresaHa == TimeSpan.FromSeconds(90) && g.EmCurso is { Etapa: "inicio" },
            "GR-2c aos 90 s a rodada presa é abandonada: a nova entra com a geração 3 e sabe a etapa e há quanto tempo");
        checar(g.Etapa(2, "gravar_sinal", T0.AddSeconds(191)) is null, "GR-2d a rodada abandonada não anota mais etapa (a geração não é a atual)");
        checar(!g.Sair(2) && g.EmCurso is { Etapa: "inicio" } && g.Geracao == 3, "GR-2e o Sair da abandonada NÃO solta a guarda da nova");
        checar(g.Sair(3) && g.EmCurso is null, "GR-2f a nova sai normalmente");
        checar(g.Entrar(T0.AddSeconds(200)) is { Geracao: 4 }, "GR-2g e a próxima entra");
        g.Sair(4);

        var muitas = new GuardaDaRodada();
        var t = T0;
        for (var i = 0; i < 1000; i++) { var e = muitas.Entrar(t); muitas.Etapa(e!.Geracao, "x", t); muitas.Sair(e.Geracao); t = t.AddSeconds(1); }
        checar(muitas.EmCurso is null && muitas.Geracao == 1000, "GR-3 mil rodadas entram e saem sem vazar nada");
        checar(GuardaDaRodada.Abandono == TimeSpan.FromSeconds(90) && GuardaDaRodada.EtapaLenta == TimeSpan.FromSeconds(5), "GR-4 os prazos: abandono em 90 s, etapa lenta a partir de 5 s");
    }

    /// <summary>
    /// Uma rodada no molde do SinalAsync: a guarda, as etapas e CADA await com prazo. O await que nunca termina
    /// vira TimeoutException, a rodada sai e a seguinte entra na hora, sem esperar os 90 s.
    /// </summary>
    private static async Task<string> RodadaAsync(GuardaDaRodada g, Func<Task> trabalho, TimeSpan prazo, DateTime agora)
    {
        var entrada = g.Entrar(agora);
        if (entrada is null) return "em_curso";
        try
        {
            g.Etapa(entrada.Geracao, "trabalho", agora);
            await trabalho().WaitAsync(prazo).ConfigureAwait(false);
            return "ok";
        }
        catch (TimeoutException) { return "prazo:" + g.EmCurso?.Etapa; }
        finally { g.Sair(entrada.Geracao); }
    }

    private static async Task OsPrazosAsync(Action<bool, string> checar)
    {
        var g = new GuardaDaRodada();
        var nuncaTermina = new TaskCompletionSource();
        var relogio = System.Diagnostics.Stopwatch.StartNew();
        var r1 = await RodadaAsync(g, () => nuncaTermina.Task, TimeSpan.FromMilliseconds(200), T0);
        checar(r1 == "prazo:trabalho" && relogio.ElapsedMilliseconds < 5000 && g.EmCurso is null,
            $"PZ-1 o await que nunca termina estoura o prazo (WaitAsync), diz a etapa e solta a guarda ({r1}, {relogio.ElapsedMilliseconds} ms)");
        var r2 = await RodadaAsync(g, () => Task.CompletedTask, TimeSpan.FromSeconds(1), T0.AddSeconds(1));
        checar(r2 == "ok", "PZ-2 a rodada seguinte entra na hora, sem esperar os 90 s do abandono");

        // sem prazo (o defeito de antes): a rodada presa segura a guarda, e só o abandono de 90 s salva
        var g2 = new GuardaDaRodada();
        var presa = new TaskCompletionSource();
        var rodadaPresa = Task.Run(async () =>
        {
            var e = g2.Entrar(T0)!;
            try { g2.Etapa(e.Geracao, "cartoes", T0); await presa.Task.ConfigureAwait(false); }
            finally { g2.Sair(e.Geracao); }
        });
        await Task.Delay(50);
        checar(g2.Entrar(T0.AddSeconds(60)) is null && g2.EmCurso is { Etapa: "cartoes" }, "PZ-3 a rodada presa num await sem prazo segura a guarda (o defeito de 16/10)");
        var e3 = g2.Entrar(T0.AddSeconds(90));
        checar(e3 is { Geracao: 2, EtapaAbandonada: "cartoes" }, "PZ-3b mas aos 90 s a guarda com hora abandona a presa e a nova segue (antes: silêncio para sempre)");
        presa.SetResult();
        await rodadaPresa;
        checar(g2.EmCurso is { Etapa: "inicio" } && g2.Geracao == 2, "PZ-3c quando a presa enfim termina, o Sair dela não solta a guarda da nova");
        g2.Sair(2);
        checar(g2.EmCurso is null, "PZ-3d e a nova solta a sua");

        // o padrão Task.Run(...).WaitAsync(prazo) usado nos awaits de UI e de banco não bloqueia a rodada
        var travado = new ManualResetEventSlim(false);
        var inicio = System.Diagnostics.Stopwatch.StartNew();
        string resultado;
        try { await Task.Run(() => { travado.Wait(); return 1; }).WaitAsync(TimeSpan.FromMilliseconds(150)); resultado = "terminou"; }
        catch (TimeoutException) { resultado = "prazo"; }
        travado.Set();
        checar(resultado == "prazo" && inicio.ElapsedMilliseconds < 3000, "PZ-4 Task.Run(...).WaitAsync(prazo) devolve o controle no prazo mesmo com a chamada travada");
    }

    private static void OReabrir(Action<bool, string> checar)
    {
        var agora = T0;
        checar(!VigiaChatIfood.ReabrirRecria("ok", agora.AddSeconds(-30), agora), "RB-1 vigia ok e a página falou há 30 s: o Reabrir NÃO recria (só manda o sinal)");
        checar(VigiaChatIfood.ReabrirRecria("ok", agora.AddMinutes(-3), agora), "RB-2 página calada há 3 min: recria");
        checar(VigiaChatIfood.ReabrirRecria("parado", agora.AddSeconds(-10), agora) && VigiaChatIfood.ReabrirRecria("login", agora, agora), "RB-3 vigia fora de ok (parado, login): recria");
        checar(VigiaChatIfood.ReabrirRecria("ok", null, agora), "RB-4 sem nenhuma fala da página: recria");
        checar(!VigiaChatIfood.ReabrirRecria("ok", agora.AddSeconds(-119), agora) && VigiaChatIfood.ReabrirRecria("ok", agora.AddSeconds(-120), agora),
            "RB-5 o limite é o silêncio da vigia (2 min)");
        checar(VigiaChatIfood.TextoChatVivo.Length <= 60 && !VigiaChatIfood.TextoChatVivo.Contains((char)0x2014) && !VigiaChatIfood.TextoChatVivo.Contains((char)0x2013),
            "RB-6 o texto do chat vivo é curto e sem travessão");
    }

    private static void PeloFonte(Action<bool, string> checar)
    {
        var servico = Ler("ServicoConversaChat.cs");
        var i = servico.IndexOf("private static async Task SinalAsync()", StringComparison.Ordinal);
        var corpo = i < 0 ? "" : servico[i..];
        var fim = corpo.IndexOf("\n    }\n", StringComparison.Ordinal);
        if (fim > 0) corpo = corpo[..fim];
        checar(corpo.Contains("GuardaDoSinal.Entrar(DateTime.Now)") && corpo.Contains("entrada.EtapaAbandonada is not null") && corpo.Contains("ficou presa ha")
               && !servico.Contains("UmSinalPorVez"),
            "FO-1 o SinalAsync usa a guarda com dono e hora (e o SemaphoreSlim de antes saiu)");
        foreach (var etapa in new[] { "diag_sdk", "banco", "cartoes", "estado", "rede", "gravar_sinal", "avisos", "gravar_bonus", "repetir_impressos" })
            checar(corpo.Contains("Etapa(\"" + etapa + "\")"), "FO-2 a etapa é anotada antes de cada passo: " + etapa);
        checar(corpo.Contains("ConversaRaspadinha.Pendentes(cx)") && corpo.Contains(".WaitAsync(TimeSpan.FromSeconds(10))")
               && corpo.Contains("Cartoes?.Invoke()).WaitAsync(TimeSpan.FromSeconds(3))")
               && corpo.Contains("RepetirImpressosAsync().WaitAsync(TimeSpan.FromSeconds(20))")
               && corpo.Contains(".WaitAsync(ConversaRaspadinha.Prazo + TimeSpan.FromSeconds(5))")
               && corpo.Contains("GravarSinal(cx, resp, DateTime.Now); })") && corpo.Contains("GravarBonus(cx, c.Bonus, c.Cabecalho, DateTime.Now);"),
            "FO-3 todo await da rodada tem prazo: banco 10 s, cartões 3 s, rede prazo+5 s, gravar 10 s, repetir impressos 20 s");
        checar(corpo.Contains("catch (TimeoutException) { Diag($\"sinal: prazo estourado em") && corpo.Contains("if (GuardaDoSinal.Sair(g))") && corpo.Contains("ant.Levou > GuardaDaRodada.EtapaLenta"),
            "FO-4 o prazo estourado vira uma linha de diag, o finally só solta a guarda da própria geração, e a etapa lenta (5 s) é anotada");
        var tela = Ler("Telas/ChatIfood.xaml.cs");
        var r = tela.IndexOf("public Task ReabrirAsync()", StringComparison.Ordinal);
        var corpoR = r < 0 ? "" : tela[r..Math.Min(tela.Length, r + 900)];
        checar(corpoR.Contains("if (!VigiaChatIfood.ReabrirRecria(_vigia.Fase, PaginaFalouEm, DateTime.Now))") && corpoR.Contains("ServicoConversaChat.SinalAgora();")
               && corpoR.Contains("VigiaChatIfood.TextoChatVivo") && corpoR.IndexOf("RecriarAsync(", StringComparison.Ordinal) > corpoR.IndexOf("SinalAgora()", StringComparison.Ordinal),
            "FO-5 o Reabrir pelo aviso confere a página viva antes de recriar: viva, só o sinal na hora e o texto curto");
        checar(servico.Contains("public static void SinalAgora()") && servico.Contains("_relogioSinal?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan)"),
            "FO-6 SinalAgora adianta o relógio do sinal para já");
        foreach (var arq in new[] { "Pdv.Nucleo/GuardaDaRodada.cs", "Pdv.Testes/TestesSinalPreso.cs" })
        {
            var t = Ler(arq);
            checar(t.Length > 0 && !t.Contains((char)0x2014) && !t.Contains((char)0x2013), "FO-7 sem travessão: " + arq);
        }
    }

    private static string Ler(string relativo)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var caminho = Path.Combine(dir, relativo);
            if (File.Exists(caminho)) return File.ReadAllText(caminho);
            dir = Path.GetDirectoryName(dir);
        }
        return "";
    }
}
