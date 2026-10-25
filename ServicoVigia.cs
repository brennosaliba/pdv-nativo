using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Dapper;
using Pdv.Nucleo;

namespace Pdv;

/// <summary>
/// O LADO DO APARELHO DO VIGIA, NO CAIXA (1.0.50, 25/10/2026, SQL 220; docs/VIGIA_CONTRATO.md do ERP).
///
/// Um sinal por minuto para a edge <c>vigia</c> (pela sessao do terminal, com o terminal_uuid no corpo): o estado do
/// chat do iFood (as chaves do sinal da raspadinha-chat mais tela_chat, casca_desde, recargas_hora, cpu_pct, mem_mb,
/// uptime_s, erros_console, suporta, venda_em_andamento) e o print JPEG do chat (a cada sinal enquanto a tela nao esta
/// ok, a cada 10 min quando esta; nunca com a tela de pagamento aberta). A resposta traz os comandos pendentes: o caixa
/// executa um de cada vez, na ordem, e conta o resultado em <c>concluir</c>; recusa (venda em andamento, pinpad ocupado,
/// impressao na fila) tambem conta. Quem mata o programa conclui ANTES.
///
/// As regras puras moram em <see cref="VigiaDoAparelho"/> (Nucleo); a pagina e o <see cref="Telas.ChatIfood"/>.
/// O 404 sem_sql_220 (banco sem o 220) nao faz barulho: continua mandando. Nunca shell livre: so os comandos da lista.
/// </summary>
public static class ServicoVigia
{
    private static readonly object _trava = new();
    private static readonly SemaphoreSlim _umSinal = new(1, 1);
    private static System.Threading.Timer? _relogio;
    private static IReadOnlyList<string> _erros = Array.Empty<string>();
    private static readonly List<DateTime> _recargas = new();
    private static DateTime? _ultimoPrintEm, _cascaDesde;
    private static string? _ultimaTela;
    private static int _falhasSeguidas;
    private static long _cpuOciosa, _cpuTotal;

    /// <summary>O estado do ultimo sinal da raspadinha-chat (ServicoConversaChat o guarda aqui): as chaves de sempre.</summary>
    public static volatile string? EstadoDoChat;

    /// <summary>A linha para a Configuracao e o diag: "vigia: ultimo sinal 10:12 ok (tela ok), 0 comandos".</summary>
    public static string Linha { get; private set; } = "Vigia: ainda sem sinal";

    /// <summary>Liga o relogio (uma vez): o primeiro sinal em 20 s, depois a cada 60 s (ou o que a edge pedir).</summary>
    public static void Ligar()
    {
        lock (_trava)
        {
            if (_relogio is not null) return;
            _relogio = new System.Threading.Timer(_ => _ = SinalAsync(), null, TimeSpan.FromSeconds(20), VigiaDoAparelho.Cada);
        }
    }

    /// <summary>Uma linha do console do WebView2 (CDP Log.entryAdded): so os erros, mascarados, os 20 ultimos.</summary>
    public static void AnotarErroDoConsole(string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            using var d = JsonDocument.Parse(json);
            if (!d.RootElement.TryGetProperty("entry", out var e) || e.ValueKind != JsonValueKind.Object) return;
            var nivel = e.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            if (nivel != "error") return;
            var texto = e.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            lock (_trava) _erros = VigiaDoAparelho.AnotarErro(_erros, texto);
        }
        catch { /* linha torta do CDP: ignora */ }
    }

    /// <summary>Conta uma recarga do chat (as do Vigia e as da propria tela, quando ela avisa).</summary>
    public static void ContarRecarga()
    {
        lock (_trava) _recargas.Add(DateTime.Now);
    }

    private static void Diag(string texto) => Telas.ChatIfood.Diag("vigia: " + texto);

    // ── o sinal ───────────────────────────────────────────────────────────────────────────────────
    private static async Task SinalAsync()
    {
        if (!await _umSinal.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var chat = Telas.ChatIfood.Instancia;
            var agora = DateTime.Now;
            var pronto = chat?.VigiaPronto == true;
            var leitura = VigiaDoAparelho.LerTela(pronto ? await chat!.VigiaLerTelaAsync().ConfigureAwait(false) : null);
            var (wsVivo, ultimoQuadro) = WsDoEstado(EstadoDoChat, agora);
            if (pronto && chat!.VigiaPedeLogin && !leitura.Login) leitura = leitura with { Login = true, Leu = true };
            var tela = VigiaDoAparelho.TelaChat(pronto, leitura, wsVivo);
            lock (_trava)
            {
                if (tela == "ok") _cascaDesde = null;
                else _cascaDesde ??= agora;
            }
            var (itens, maquininha) = OQueATelaVive();
            var papeis = PapeisNaFila();
            var emPagamento = Telas.Pagamento.NaTela;
            string? print = null;
            if (pronto && VigiaDoAparelho.QuerPrint(tela, _ultimoPrintEm, agora, emPagamento))
            {
                var bytes = await chat!.VigiaPrintAsync().ConfigureAwait(false);
                if (bytes is { Length: > 0 and <= VigiaDoAparelho.PrintMaximoBytes }) { print = Convert.ToBase64String(bytes); _ultimoPrintEm = agora; }
                else if (bytes is { Length: > VigiaDoAparelho.PrintMaximoBytes }) Diag($"print de {bytes.Length / 1024} KB nao cabe (1 MB); vai sem print");
            }
            int recargas; IReadOnlyList<string> erros;
            lock (_trava) { recargas = VigiaDoAparelho.RecargasNaHora(_recargas, agora); erros = _erros; }
            var l = new VigiaDoAparelho.Leitura(
                Versao: Atualizacao.VersaoInstalada() ?? "?",
                TerminalUuid: TerminalUuid(),
                TelaChat: tela,
                CascaDesde: _cascaDesde,
                WsVivo: pronto ? wsVivo : null,
                UltimoQuadroEm: ultimoQuadro,
                RecargasHora: recargas,
                CpuPct: CpuDoPc(),
                MemMb: Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0,
                UptimeS: (long)(agora - Process.GetCurrentProcess().StartTime).TotalSeconds,
                ErrosConsole: erros,
                VendaEmAndamento: itens > 0,
                Suporta: VigiaDoAparelho.ComandosDoCaixa,
                EstadoBaseJson: EstadoDoChat,
                PrintB64: print);
            var corpo = VigiaDoAparelho.CorpoSinal(l);
            var (st, resp) = await Servicos.Nuvem().FuncaoAsync(VigiaDoAparelho.Edge, corpo, VigiaDoAparelho.Prazo)
                .WaitAsync(VigiaDoAparelho.Prazo + TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var r = VigiaDoAparelho.LerSinal(st, resp);
            if (!r.Ok)
            {
                _falhasSeguidas++;
                Linha = $"Vigia: sinal {agora:HH\\:mm} nao entrou ({r.Motivo})";
                // sem o SQL 220 ainda, ou sem rede: uma linha so, nao a cada minuto
                if (_falhasSeguidas == 1 || _falhasSeguidas % 30 == 0) Diag($"sinal nao entrou ({r.Motivo}, http {st}); tela {tela}");
                return;
            }
            _falhasSeguidas = 0;
            if (tela != _ultimaTela) { Diag($"tela do chat: {tela}" + (print is null ? "" : ", com print")); _ultimaTela = tela; }
            Linha = $"Vigia: sinal {agora:HH\\:mm} ok (tela {tela}), {r.Comandos.Count} comando(s)" + (r.Pausado ? ", vigia pausada" : "");
            try { _relogio?.Change(VigiaDoAparelho.ProximoIntervalo(r), VigiaDoAparelho.ProximoIntervalo(r)); } catch { }
            foreach (var c in r.Comandos)
                await ExecutarAsync(c, itens, maquininha, papeis).ConfigureAwait(false);
        }
        catch (Exception ex) { Diag("sinal falhou (" + ex.GetType().Name + ")"); }
        finally { _umSinal.Release(); }
    }

    /// <summary>ws_vivo e ultimo_quadro_em do estado do sinal da raspadinha-chat (as chaves que o caixa ja manda).</summary>
    private static (bool WsVivo, DateTime? UltimoQuadro) WsDoEstado(string? estado, DateTime agora)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(estado)) return (false, null);
            using var d = JsonDocument.Parse(estado);
            var r = d.RootElement;
            DateTime? uq = null;
            if (r.TryGetProperty("ultimo_quadro_em", out var u) && u.ValueKind == JsonValueKind.String
                && DateTime.TryParse(u.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
                uq = dt.ToLocalTime();
            var vivo = uq is { } q && agora - q < TimeSpan.FromMinutes(2);
            return (vivo, uq);
        }
        catch { return (false, null); }
    }

    // ── os comandos ───────────────────────────────────────────────────────────────────────────────
    private static async Task ExecutarAsync(VigiaDoAparelho.Comando c, int itens, bool maquininha, int papeis)
    {
        var ctx = new VigiaDoAparelho.Contexto(itens > 0, maquininha, papeis > 0, VigiaDoAparelho.ComandosDoCaixa,
            Expirado: c.ExpiraEm is { } e && DateTime.UtcNow > e);
        var (decisao, motivo) = VigiaDoAparelho.Decidir(c.Nome, ctx);
        if (decisao != VigiaDoAparelho.Decisao.Executar)
        {
            Diag($"comando {c.Nome} recusado: {motivo}");
            await ConcluirAsync(c, null, VigiaDoAparelho.ErroDeRecusa(motivo ?? "nao suportado")).ConfigureAwait(false);
            return;
        }
        Diag($"comando {c.Nome}" + (c.Motivo is null ? "" : $" ({c.Motivo})"));
        var chat = Telas.ChatIfood.Instancia;
        try
        {
            switch (c.Nome)
            {
                case VigiaDoAparelho.Recarregar:
                case VigiaDoAparelho.RecargaForte:
                    if (chat is null) { await ConcluirAsync(c, null, "o chat ainda nao abriu").ConfigureAwait(false); return; }
                    ContarRecarga();
                    await chat.VigiaRecarregarAsync(c.Nome == VigiaDoAparelho.RecargaForte, c.Motivo ?? "pedido do vigia").ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                    await ConcluirAsync(c, new JsonObject { ["recarregou"] = true, ["tela_chat_depois"] = await TelaDepoisAsync().ConfigureAwait(false) }, null).ConfigureAwait(false);
                    return;
                case VigiaDoAparelho.GestorInteiro:
                case VigiaDoAparelho.SoAConversa:
                    if (chat is null) { await ConcluirAsync(c, null, "o chat ainda nao abriu").ConfigureAwait(false); return; }
                    var ok = await chat.VigiaModoAsync(c.Nome == VigiaDoAparelho.GestorInteiro).ConfigureAwait(false);
                    await ConcluirAsync(c, new JsonObject { ["modo"] = c.Nome == VigiaDoAparelho.GestorInteiro ? "gestor_inteiro" : "so_a_conversa" }, ok ? null : "o chat ainda nao abriu").ConfigureAwait(false);
                    return;
                case VigiaDoAparelho.RelogarGestor:
                    if (chat is null) { await ConcluirAsync(c, null, "o chat ainda nao abriu").ConfigureAwait(false); return; }
                    if (!chat.VigiaPedeLogin)
                    {
                        // logado: sai da conta e espera a tela de login aparecer (ate 60 s)
                        await chat.VigiaSairDoGestorAsync().ConfigureAwait(false);
                        for (var i = 0; i < 12 && !chat.VigiaPedeLogin; i++) await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                    var linha = await LoginDoGestor.EntrarAgora().ConfigureAwait(false);
                    var entrou = linha.Contains("entrou", StringComparison.Ordinal) && !linha.Contains("falhou", StringComparison.Ordinal);
                    await ConcluirAsync(c, new JsonObject { ["login"] = LoginDoGestor.Mascarar(linha), ["entrou"] = entrou }, entrou ? null : LoginDoGestor.Mascarar(linha)).ConfigureAwait(false);
                    return;
                case VigiaDoAparelho.ColetarDiagnostico:
                    if (chat is null) { await ConcluirAsync(c, null, "o chat ainda nao abriu").ConfigureAwait(false); return; }
                    var (caminho, texto) = await chat.VigiaDiagnosticoAsync().ConfigureAwait(false);
                    _ultimoPrintEm = null; // o proximo sinal leva o print
                    await ConcluirAsync(c, VigiaDoAparelho.ResultadoDoDiagnostico(caminho, texto), caminho is null ? texto : null).ConfigureAwait(false);
                    return;
                case VigiaDoAparelho.Atualizar:
                    var m = await Telas.AtualizarCaixa.ProcurarNoSilencioAsync().ConfigureAwait(false);
                    if (m is null) { await ConcluirAsync(c, new JsonObject { ["atualizou"] = false, ["motivo"] = "sem versao nova" }, null).ConfigureAwait(false); return; }
                    await ConcluirAsync(c, new JsonObject { ["vai_executar"] = true, ["versao"] = m.Versao }, null).ConfigureAwait(false);
                    await Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        try { await Telas.AtualizarCaixa.ExecutarAsync(Application.Current.MainWindow, itens, maquininha); }
                        catch (Exception ex) { Diag("atualizar falhou (" + ex.GetType().Name + ")"); }
                    });
                    return;
                case VigiaDoAparelho.ReiniciarApp:
                    await ConcluirAsync(c, new JsonObject { ["vai_executar"] = true }, null).ConfigureAwait(false);
                    ReabrirOCaixa();
                    return;
                case VigiaDoAparelho.ReiniciarPc:
                    var sistema = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    if (ReinicioDoPc.MotivoParaNaoReiniciar(sistema, File.Exists) is { } nao)
                    {
                        await ConcluirAsync(c, null, nao).ConfigureAwait(false);
                        return;
                    }
                    await ConcluirAsync(c, new JsonObject { ["vai_executar"] = true }, null).ConfigureAwait(false);
                    Diag(ReinicioDoPc.LinhaDeLog(null, "vigia", "shutdown pedido"));
                    Process.Start(new ProcessStartInfo(ReinicioDoPc.CaminhoDoShutdown(sistema), ReinicioDoPc.Argumentos) { UseShellExecute = false, CreateNoWindow = true });
                    return;
                default:
                    await ConcluirAsync(c, null, VigiaDoAparelho.ErroDeRecusa("comando nao suportado")).ConfigureAwait(false);
                    return;
            }
        }
        catch (Exception ex)
        {
            Diag($"comando {c.Nome} falhou ({ex.GetType().Name})");
            await ConcluirAsync(c, null, "falhou: " + ex.GetType().Name).ConfigureAwait(false);
        }
    }

    private static async Task<string> TelaDepoisAsync()
    {
        var chat = Telas.ChatIfood.Instancia;
        if (chat?.VigiaPronto != true) return "outro";
        var (ws, _) = WsDoEstado(EstadoDoChat, DateTime.Now);
        return VigiaDoAparelho.TelaChat(true, VigiaDoAparelho.LerTela(await chat.VigiaLerTelaAsync().ConfigureAwait(false)), ws);
    }

    private static async Task ConcluirAsync(VigiaDoAparelho.Comando c, JsonObject? resultado, string? erro)
    {
        try
        {
            var corpo = VigiaDoAparelho.CorpoConcluir(TerminalUuid(), c.Id, resultado, erro);
            var (st, resp) = await Servicos.Nuvem().FuncaoAsync(VigiaDoAparelho.Edge, corpo, VigiaDoAparelho.Prazo)
                .WaitAsync(VigiaDoAparelho.Prazo + TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var (ok, motivo) = VigiaDoAparelho.LerConcluir(st, resp);
            if (!ok) Diag($"concluir {c.Nome} nao entrou ({motivo})");
        }
        catch (Exception ex) { Diag("concluir falhou (" + ex.GetType().Name + ")"); }
    }

    /// <summary>reiniciar_app: um cmd comum reabre o caixa em 3 s e o programa fecha (como a troca de versao faz).</summary>
    private static void ReabrirOCaixa()
    {
        try
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & start \"\" \"{exe}\"") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex) { Diag("reabrir o caixa: " + ex.GetType().Name); }
        try { Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown()); }
        catch { Environment.Exit(0); }
    }

    // ── o que o caixa vive (para o portao dos comandos) ───────────────────────────────────────────
    private static (int Itens, bool Maquininha) OQueATelaVive()
    {
        try { return Telas.AtualizarCaixa.OQueATelaViveAgora?.Invoke() ?? (0, false); }
        catch { return (0, false); }
    }

    private static int PapeisNaFila()
    {
        try { return Math.Max(0, Telas.AtualizarCaixa.PapeisNaFila()); }
        catch { return 0; }
    }

    private static string? _terminalUuid;

    private static string? TerminalUuid()
    {
        if (_terminalUuid is not null) return _terminalUuid;
        try
        {
            using var cx = Banco.Abrir();
            _terminalUuid = cx.ExecuteScalar<string?>("SELECT terminal_uuid FROM terminal LIMIT 1");
        }
        catch { _terminalUuid = null; }
        return _terminalUuid;
    }

    // ── a CPU do PC (GetSystemTimes, sem contador de desempenho) ──────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private static double? CpuDoPc()
    {
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
            var total = kernel + user;
            double? pct = null;
            if (_cpuTotal > 0 && total > _cpuTotal)
                pct = 100.0 * (1.0 - (double)(idle - _cpuOciosa) / (total - _cpuTotal));
            _cpuOciosa = idle; _cpuTotal = total;
            return pct is { } p ? Math.Clamp(p, 0, 100) : null;
        }
        catch { return null; }
    }
}
