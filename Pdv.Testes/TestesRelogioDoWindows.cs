using System.IO;
using Pdv.Instalador;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O RELÓGIO DO PC ACERTADO SOZINHO (1.0.41, 16/10/2026, pedido do dono: "coloca no PDV para sempre
/// sincronizar o PC com relógio atômico, automaticamente"; o PC da Savassi estava 566 s atrasado).
///
///  RW-1..3   as linhas de comando: só constantes, programas de System32, a tarefa PdvNativo-Relogio,
///            a linha do cmd elevado no molde da trava, pasta fora do formato recusada
///  RW-4..5   o instalador: as seis etapas rodam na ordem, falha não aborta, "net start" já ligado é ok,
///            e o Instalar de verdade chama o acerto (com executor falso: nada do Windows é tocado)
///  RW-6..7   a regra do aviso: duas leituras seguidas acima de 60 s; uma dentro zera; nula não conta
///  RW-8..9   os textos: atrasado/adiantado em minutos, sem travessão; o sinal leva relogio_seg
///  RW-10     o fonte: a tela tem o bloco e o botão; o botão só pede ao Windows o cmd de System32
/// Nenhuma prova roda w32tm, sc, net ou schtasks de verdade.
/// </summary>
public static class TestesRelogioDoWindows
{
    private const string Sys = @"C:\WINDOWS\system32";

    public static void Rodar(Action<bool, string> checar)
    {
        AsLinhas(checar);
        OInstalador(checar);
        OAviso(checar);
        OsTextos(checar);
        PeloFonte(checar);
    }

    private static void AsLinhas(Action<bool, string> checar)
    {
        var passos = RelogioDoWindows.PassosDaInstalacao(Sys);
        var esperado = new[]
        {
            (Sys + @"\w32tm.exe", "/config /manualpeerlist:\"a.st1.ntp.br b.st1.ntp.br pool.ntp.org\" /syncfromflags:manual /update"),
            (Sys + @"\sc.exe", "config w32time start= auto"),
            (Sys + @"\net.exe", "start w32time"),
            (Sys + @"\schtasks.exe", "/create /f /ru SYSTEM /sc hourly /tn PdvNativo-Relogio /tr \"w32tm /resync /nowait\""),
            (Sys + @"\schtasks.exe", "/create /f /ru SYSTEM /sc onstart /tn PdvNativo-Relogio-Inicio /tr \"w32tm /resync /nowait\""),
            (Sys + @"\w32tm.exe", "/resync"),
        };
        checar(passos.Count == 6 && passos.Select(p => (p.Programa, p.Argumentos)).SequenceEqual(esperado),
            "RW-1 as seis etapas, na ordem, por caminho absoluto de System32 e só com constantes (NTP.br + pool, serviço automático, tarefa de hora, tarefa de início, resync)"
            + (passos.Count == 6 ? "" : " [saiu " + passos.Count + "]"));
        checar(passos.All(p => p.Programa.StartsWith(Sys + @"\", StringComparison.Ordinal) && !p.Argumentos.Contains('%') && !p.Argumentos.Contains('|')),
            "RW-1b nenhuma etapa usa programa fora de System32, variável de ambiente ou redirecionamento");

        var cmd = RelogioDoWindows.MontarComandoDoCaixa(Sys);
        var esperadoCmd = "/d /v:off /s /c \"" + string.Join(" & ", esperado.Select(e => "\"" + e.Item1 + "\" " + e.Item2)) + "\"";
        checar(cmd == esperadoCmd, "RW-2 a linha do cmd elevado do botão: /d /v:off /s /c com as seis etapas separadas por & (uma que falhe não para as outras)"
            + (cmd == esperadoCmd ? "" : " [saiu: " + cmd + "]"));
        checar(RelogioDoWindows.ComandoLimpo(cmd) && !RelogioDoWindows.ComandoLimpo(cmd + " %TEMP%") && !RelogioDoWindows.ComandoLimpo(cmd + " | x")
               && !RelogioDoWindows.ComandoLimpo(cmd + "\n"),
            "RW-2b a linha só tem o que as constantes têm: %, |, quebra de linha não passam");
        var recusou = 0;
        foreach (var ruim in new[] { @"C:\WINDOWS\Temp\system32", @"C:\WINDOWS\system32 ", "", @"\\srv\system32", @"C:\WINDOWS\system32\..\x" })
        {
            try { RelogioDoWindows.PassosDaInstalacao(ruim); }
            catch (ArgumentException) { recusou++; continue; }
            try { RelogioDoWindows.MontarComandoDoCaixa(ruim); }
            catch (ArgumentException) { }
        }
        checar(recusou == 5, "RW-3 pasta do sistema fora do formato é recusada antes de virar linha de comando (" + recusou + " de 5)");
        checar(RelogioDoWindows.ArgumentosConsultarTarefa == "/query /tn PdvNativo-Relogio" && RelogioDoWindows.TarefaExiste(0) && !RelogioDoWindows.TarefaExiste(1),
            "RW-3b a consulta da tarefa é schtasks /query /tn PdvNativo-Relogio e só o código 0 diz que existe");
    }

    private static void OInstalador(Action<bool, string> checar)
    {
        var rodou = new List<(string Programa, string Args)>();
        (int, string) Falso(string programa, string args)
        {
            rodou.Add((programa, args));
            if (programa.EndsWith("net.exe", StringComparison.OrdinalIgnoreCase)) return (2, "O serviço solicitado já foi iniciado.");
            if (args.StartsWith("config w32time", StringComparison.Ordinal)) return (1, "Acesso negado");
            return (0, "");
        }
        var linhas = Instalacao.AcertarRelogio(Falso);
        checar(rodou.Count == 6 && rodou[3].Args.Contains("/tn PdvNativo-Relogio ", StringComparison.Ordinal) && rodou[5].Args == "/resync",
            "RW-4 o acerto roda as seis etapas na ordem e registra a tarefa PdvNativo-Relogio mesmo com uma etapa antes falhando");
        checar(linhas.Count == 6 && linhas[1].Contains("servico_automatico: falhou codigo=1") && linhas[2].Contains("servico_ligado: ok codigo=2")
               && linhas[3].Contains("tarefa_hora: ok") && linhas.All(l => l.StartsWith("relogio ", StringComparison.Ordinal)),
            "RW-4b cada etapa vira uma linha do diário: a falha anotada, o \"net start\" já ligado (código 2) é ok");
        var lancou = Instalacao.AcertarRelogio((_, _) => throw new InvalidOperationException("boom"));
        checar(lancou.Count == 6 && lancou.All(l => l.Contains("falhou InvalidOperationException")), "RW-4c o executor que lança não derruba o acerto: seis linhas de falha");

        // o Instalar de verdade chama o acerto (executor falso, pasta descartável; nada do Windows é tocado)
        var raiz = Path.Combine(Path.GetTempPath(), "pdv-testes-relogio-" + Guid.NewGuid().ToString("N")[..8]);
        var executorAntes = Instalacao.ExecutarPrograma;
        try
        {
            var origem = Path.Combine(raiz, "origem");
            var destino = Path.Combine(raiz, "destino");
            Directory.CreateDirectory(origem);
            File.WriteAllText(Path.Combine(origem, "Pdv.exe"), "programa");
            rodou.Clear();
            Instalacao.ExecutarPrograma = (p, a) => { rodou.Add((p, a)); return (1, "falhou de mentira"); };
            var erro = Instalacao.Instalar(new Instalacao.Opcoes(origem, destino, IniciarComWindows: false, AtalhoAreaTrabalho: false,
                AjustarAcl: false, GravarRegistro: false, MigrarAntiga: false, ConferirQueAbre: false, AcertarRelogio: true));
            checar(erro is null && rodou.Count == 6 && rodou.Any(r => r.Args.Contains("/tn PdvNativo-Relogio ", StringComparison.Ordinal)),
                "RW-5 o Instalar chama o acerto do relógio e, com TODAS as etapas falhando, a instalação ainda conclui sem erro");
            rodou.Clear();
            Instalacao.Instalar(new Instalacao.Opcoes(origem, destino, false, false, false, false, false, false, AcertarRelogio: false));
            checar(rodou.Count == 0, "RW-5b com a opção desligada (a suíte) nada roda");
        }
        finally
        {
            Instalacao.ExecutarPrograma = executorAntes;
            try { if (Directory.Exists(raiz)) Directory.Delete(raiz, recursive: true); } catch { }
        }
    }

    private static void OAviso(Action<bool, string> checar)
    {
        var v = new RelogioDoWindows.VigiaDoDesvio();
        var atras = TimeSpan.FromSeconds(-566);
        checar(!v.Registrar(atras) && !v.Avisar && v.Seguidas == 1, "RW-6 uma leitura de 566 s atrasado ainda não avisa (pode ser a rede)");
        checar(v.Registrar(atras) && v.Avisar && v.UltimoDesvio == atras, "RW-6b a segunda seguida avisa, e o último desvio fica guardado");
        checar(v.Registrar(null) && v.Avisar && v.Seguidas == 2, "RW-6c uma leitura nula (o painel não disse a hora) não conta nem zera");
        checar(!v.Registrar(TimeSpan.FromSeconds(30)) && !v.Avisar && v.Seguidas == 0, "RW-7 uma leitura dentro de 60 s zera a contagem");
        checar(!v.Registrar(TimeSpan.FromSeconds(61)) && v.Registrar(TimeSpan.FromSeconds(90)) && v.Avisar, "RW-7b adiantado conta igual (é o módulo): 61 s e 90 s avisam");
        checar(!new RelogioDoWindows.VigiaDoDesvio().Registrar(TimeSpan.FromSeconds(60)), "RW-7c 60 s exatos não é acima do limiar");
        v.Zerar();
        checar(!v.Avisar && v.UltimoDesvio is null, "RW-7d Zerar volta ao começo");
    }

    private static void OsTextos(Action<bool, string> checar)
    {
        var f = RelogioDoWindows.Frase(TimeSpan.FromSeconds(-566));
        checar(f == "Relógio do PC 9 min atrasado. O Windows acerta sozinho em até 1 h.", "RW-8 566 s atrasado vira \"9 min atrasado\" numa linha" + (f.Length > 0 ? " [" + f + "]" : ""));
        checar(RelogioDoWindows.Frase(TimeSpan.FromSeconds(61)) == "Relógio do PC 1 min adiantado. O Windows acerta sozinho em até 1 h.",
            "RW-8b 61 s adiantado é \"1 min adiantado\" (nunca 0 min)");
        foreach (var t in new[] { f, RelogioDoWindows.TextoDoBotao, RelogioDoWindows.FraseSemTarefa, RelogioDoWindows.FraseAcertou, RelogioDoWindows.FraseRecusou, RelogioDoWindows.FraseFalhou })
            checar(!t.Contains('\u2014') && !t.Contains('\u2013') && t.Length <= 120, "RW-8c texto de tela sem travessão e curto: " + t[..Math.Min(30, t.Length)]);
        checar(RelogioDoWindows.Segundos(TimeSpan.FromSeconds(-566.4)) == -566 && RelogioDoWindows.Segundos(null) is null,
            "RW-9 o desvio vira segundos inteiros para o sinal (negativo = atrasado), nulo sem leitura");
        var estado = System.Text.Json.Nodes.JsonNode.Parse(ConversaRaspadinha.EstadoDoSinal("logado", true, null, 0, null, null, null, relogioSeg: -566))!;
        checar((int?)estado["relogio_seg"] == -566, "RW-9b o estado do sinal do chat leva relogio_seg");
        checar(System.Text.Json.Nodes.JsonNode.Parse(ConversaRaspadinha.EstadoDoSinal("logado", true, null, 0, null, null, null))!["relogio_seg"] is null,
            "RW-9c sem leitura o campo não vai");
    }

    private static void PeloFonte(Action<bool, string> checar)
    {
        var xaml = Ler("Telas/Configuracao.xaml");
        var tela = Ler("Telas/Configuracao.xaml.cs");
        var quiosque = Ler("Quiosque.cs");
        var instalador = Ler("Pdv.Instalador/Instalacao.cs");
        var csproj = Ler("Pdv.Instalador/Pdv.Instalador.csproj");
        checar(xaml.Contains("x:Name=\"BlocoRelogio\"") && xaml.Contains("x:Name=\"BtnAcertarRelogio\"") && xaml.Contains("Click=\"AcertarRelogio\""),
            "RW-10 a Configuração tem o bloco do relógio e o botão \"Acertar o relógio agora\"");
        checar(tela.Contains("RelogioDoWindows.Frase(") && tela.Contains("Quiosque.TarefaDoRelogioExiste") && tela.Contains("Quiosque.AcertarRelogio("),
            "RW-10b a tela mostra a frase do núcleo, consulta a tarefa e manda o botão ao Quiosque");
        checar(quiosque.Contains("RelogioDoWindows.MontarComandoDoCaixa(sistema)") && quiosque.Contains("PedirAoWindows(RelogioDoWindows.MontarComandoDoCaixa(sistema), sistema, janela)"),
            "RW-10c o botão pede ao Windows só o cmd.exe de System32 com a linha do núcleo (o mesmo PedirAoWindows da trava, com runas)");
        checar(instalador.Contains("if (o.AcertarRelogio)") && instalador.Contains("foreach (var linha in AcertarRelogio()) AnotarTroca(linha);"),
            "RW-10d o instalador acerta o relógio em toda instalação e atualização e anota cada etapa no diário");
        checar(csproj.Contains("Pdv.Nucleo\\RelogioDoWindows.cs"), "RW-10e o instalador compila o MESMO arquivo das linhas de comando do caixa");
        checar(Ler("Telas/AtualizarCaixa.cs").Contains("VigiaDoRelogio.Registrar(DesvioAgora())"), "RW-10f cada pergunta ao painel é uma leitura do vigia do relógio");
        checar(Ler("ServicoConversaChat.cs").Contains("RelogioDoWindows.Segundos(Telas.AtualizarCaixa.DesvioDoRelogio)"), "RW-10g o sinal do chat leva o desvio (relogio_seg)");
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
