using System.Diagnostics;
using Pdv.Instalador;

namespace Pdv.Testes;

/// <summary>
/// O INSTALADOR DA CASTELO (14/09/2026).
///
///   1. Ficou parado em "Conferindo se o caixa abre nesta máquina". A conferência lia a saída
///      inteira do Pdv.exe ANTES de esperar com prazo: ReadToEnd só volta quando o programa (e
///      todo filho que herdou a saída dele) fecha. O prazo de 90 s nunca valia.
///   2. A tela mostrava "versão 2.0.0.0": no instalador empacotado não há Pdv.exe ao lado, e a
///      conta caía na versão do PRÓPRIO instalador.
///   3. O caixa aberto pelo instalador herdava o administrador, e a trava dele não era vista
///      pelo clique normal no ícone.
/// </summary>
public static class TestesInstaladorCastelo
{
    public static void Rodar(Action<bool, string> checar)
    {
        ProgramaPreso(checar);
        FilhoSegurandoASaida(checar);
        VersaoDoCaixa(checar);
        AbrirSemAdministrador(checar);
    }

    private static void ProgramaPreso(Action<bool, string> checar)
    {
        Process? vizinho = null;
        try
        {
            // Outro processo igual, que já estava aberto: é o "outro Pdv.exe" que o instalador
            // não pode derrubar.
            vizinho = Process.Start(Sondas.Psi("--sonda-presa", "60"));
            var relogio = Stopwatch.StartNew();
            var r = Instalacao.RodarComPrazo(Sondas.Psi("--sonda-presa", "20"), TimeSpan.FromSeconds(2));
            relogio.Stop();
            checar(relogio.ElapsedMilliseconds < 8_000,
                $"programa preso: a conferência desiste no prazo ({relogio.ElapsedMilliseconds} ms com prazo de 2 s)");
            checar(!r.Terminou && r.Matou, $"programa preso: sai como não terminou e o processo foi encerrado (terminou={r.Terminou}, matou={r.Matou})");
            checar(vizinho is { HasExited: false },
                "e o outro processo igual que já estava aberto continua vivo: o instalador só encerra o que ele mesmo abriu");

            var fonte = Fonte("Pdv.Instalador", "Instalacao.cs") ?? "";
            var i = fonte.IndexOf("public static string? ConferirQueOProgramaAbre(", StringComparison.Ordinal);
            var fim = i < 0 ? -1 : fonte.IndexOf("public static string? AvaliarConferencia(", i, StringComparison.Ordinal);
            var corpo = i < 0 || fim < 0 ? "" : fonte[i..fim];
            checar(corpo.Contains("RodarComPrazo(", StringComparison.Ordinal),
                "a conferência do instalador passa pela execução com prazo testada aqui");
            checar(!corpo.Contains("ReadToEnd()", StringComparison.Ordinal),
                "e não lê a saída inteira antes de esperar (era isso que tirava o prazo)");
        }
        finally
        {
            try { vizinho?.Kill(); } catch { }
            vizinho?.Dispose();
        }
    }

    private static void FilhoSegurandoASaida(Action<bool, string> checar)
    {
        var arquivoPid = Path.Combine(Path.GetTempPath(), $"neto_{Guid.NewGuid():N}.pid");
        try
        {
            var relogio = Stopwatch.StartNew();
            var r = Instalacao.RodarComPrazo(Sondas.Psi("--sonda-neto", "20", arquivoPid), TimeSpan.FromSeconds(30));
            relogio.Stop();
            checar(r.Terminou && r.Codigo == 0 && relogio.ElapsedMilliseconds < 8_000,
                $"programa que abriu e deixou um filho com a saída: a conferência volta quando ELE termina "
                + $"({relogio.ElapsedMilliseconds} ms, terminou={r.Terminou}, código {r.Codigo})");
        }
        finally
        {
            try
            {
                if (File.Exists(arquivoPid) && int.TryParse(File.ReadAllText(arquivoPid), out var pid))
                    using (var neto = Process.GetProcessById(pid)) neto.Kill();
            }
            catch { /* já saiu */ }
            try { File.Delete(arquivoPid); } catch { }
        }
    }

    private static void VersaoDoCaixa(Action<bool, string> checar)
    {
        checar(Instalacao.RotuloVersao("1.0.10.0") == "versão 1.0.10", "rótulo: 1.0.10.0 aparece como versão 1.0.10");
        checar(Instalacao.RotuloVersao("1.2.0.0") == "versão 1.2.0", "rótulo: só o 4º número some");
        checar(Instalacao.RotuloVersao(null) == "" && Instalacao.RotuloVersao("  ") == "",
            "rótulo: versão desconhecida não mostra número nenhum (antes caía na do instalador, 2.0.0.0)");

        var raiz = Path.Combine(Path.GetTempPath(), "pdv-versao-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var origem = Path.Combine(raiz, "pdv");
            Directory.CreateDirectory(origem);
            var exeDeVerdade = Path.Combine(AppContext.BaseDirectory, "Pdv.exe");
            if (!File.Exists(exeDeVerdade)) exeDeVerdade = Environment.ProcessPath ?? "";
            File.Copy(exeDeVerdade, Path.Combine(origem, "Pdv.exe"));
            var esperado = FileVersionInfo.GetVersionInfo(exeDeVerdade).FileVersion;

            var exeBase = Path.Combine(raiz, "base.exe");
            File.WriteAllBytes(exeBase, Enumerable.Repeat((byte)0x4D, 4096).ToArray());
            var saida = Path.Combine(raiz, "InstalarPdv.exe");
            checar(Pacote.Empacotar(exeBase, origem, null, saida) is null, "versão: empacotei um caixa com versão");
            var lida = Pacote.LerVersaoDoCaixa(saida);
            checar(esperado is { Length: > 0 } && lida == esperado,
                $"versão: o pacote leva a versão do CAIXA ({esperado}) e o instalador lê de lá ('{lida}')");

            var aberto = Path.Combine(raiz, "aberto");
            checar(Pacote.Extrair(aberto, null, saida) is null
                   && Instalacao.ConferirOrigem(Path.Combine(aberto, "pdv")) is null
                   && !Instalacao.ArquivosParaCopiar(Path.Combine(aberto, "pdv")).Any(a => a.Contains("versao", StringComparison.OrdinalIgnoreCase)),
                "versão: o que sai do pacote continua instalável e a anotação da versão não vai para a pasta do caixa");
        }
        finally { try { Directory.Delete(raiz, true); } catch { } }

        var janela = Fonte("Pdv.Instalador", "JanelaInstalador.xaml.cs") ?? "";
        checar(janela.Length > 0 && !janela.Contains("GetVersionInfo(Environment.ProcessPath", StringComparison.Ordinal),
            "a tela do instalador não mostra mais a versão do próprio instalador como se fosse a do caixa");
        checar(janela.Contains("Pacote.LerVersaoDoCaixa(", StringComparison.Ordinal),
            "e no instalador empacotado a versão vem de dentro do pacote");
    }

    private static void AbrirSemAdministrador(Action<bool, string> checar)
    {
        checar(Instalacao.DecidirComoAbrir(elevado: true, explorerAberto: true) == Instalacao.ComoAbrir.PeloExplorer,
            "instalador como administrador abre o caixa como o usuário da área de trabalho, sem herdar o administrador");
        checar(Instalacao.DecidirComoAbrir(elevado: true, explorerAberto: false) == Instalacao.ComoAbrir.Direto,
            "sem área de trabalho aberta (quiosque) abre direto");
        checar(Instalacao.DecidirComoAbrir(elevado: false, explorerAberto: true) == Instalacao.ComoAbrir.Direto,
            "sem administrador não há o que tirar: abre direto");

        var app = Fonte("Pdv.Instalador", "App.xaml.cs") ?? "";
        var janela = Fonte("Pdv.Instalador", "JanelaInstalador.xaml.cs") ?? "";
        checar(app.Contains("Instalacao.AbrirCaixa(", StringComparison.Ordinal) && janela.Contains("Instalacao.AbrirCaixa(", StringComparison.Ordinal),
            "os dois caminhos que reabrem o caixa (fim da instalação e atualização) passam pela mesma regra");
        checar(!app.Contains("Path.Combine(Instalacao.PastaDestinoPadrao, \"Pdv.exe\")", StringComparison.Ordinal),
            "a atualização reabre o caixa onde ele ESTÁ instalado, e não na pasta do nome novo");
    }

    private static string? Fonte(params string[] partes)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var alvo = Path.Combine(new[] { d.FullName }.Concat(partes).ToArray());
            if (File.Exists(alvo)) return File.ReadAllText(alvo);
        }
        return null;
    }
}
