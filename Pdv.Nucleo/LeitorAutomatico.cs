namespace Pdv.Nucleo;

/// <summary>
/// A ESCOLHA AUTOMATICA DO LEITOR (1.0.53). A cada sonda (o portao sonda antes de cada
/// leitura): primeiro o Windows (WinBio, driver WBF da HID); se o Windows nao tiver
/// unidade, o runtime da HID (U.are.U RTE, dpfpdd_query_devices &gt; 0); se nenhum dos
/// dois, a frase do dono: "Leitor nao encontrado. Instale o driver WBF da HID ou o
/// U.are.U RTE." O passo vai no detalhe e no Conferir o leitor da Configuracao.
///
/// Sondar de novo a cada vez e de proposito: o leitor plugado depois, ou o RTE instalado
/// com o caixa aberto, passa a valer na leitura seguinte, sem reiniciar.
/// </summary>
public sealed class LeitorAutomatico : ILeitorDigital
{
    private readonly ILeitorDigital _windows;
    private readonly ILeitorDigital _hid;
    private volatile ILeitorDigital? _emUso;

    public LeitorAutomatico(ILeitorDigital windows, ILeitorDigital hid)
    {
        _windows = windows;
        _hid = hid;
    }

    /// <summary>"WinBio", "HID" ou "nenhum" (o diagnostico mostra).</summary>
    public string Nome => _emUso?.Nome ?? "nenhum";

    /// <summary>"winbio", "hid" ou null: o valor da coluna digital.motor do motor em uso.</summary>
    public string? MotorEmUso => _emUso is null ? null : ReferenceEquals(_emUso, _hid) ? LeitorHidUareU.MotorTexto : "winbio";

    public async Task<SondaLeitor> SondarAsync(CancellationToken ct)
    {
        var w = await _windows.SondarAsync(ct);
        if (w.Pronto) { _emUso = _windows; return w with { Detalhe = "Windows: " + w.Detalhe }; }
        var h = await _hid.SondarAsync(ct);
        if (h.Pronto) { _emUso = _hid; return h with { Detalhe = "HID: " + h.Detalhe }; }
        _emUso = null;
        // o que vai para a tela: plugado sem nenhum dos dois drivers = a frase do dono;
        // WBF instalado com o servico parado = o servico; nada plugado = sem leitor
        var estado = w.Estado == EstadoLeitor.SemDriver ? EstadoLeitor.SemDriver
                   : w.Estado == EstadoLeitor.SemServico ? EstadoLeitor.SemServico
                   : EstadoLeitor.SemLeitor;
        return new SondaLeitor(estado, $"Windows: {w.Detalhe} | HID: {h.Detalhe}", Array.Empty<string>());
    }

    private async Task<ILeitorDigital?> EmUsoAsync(CancellationToken ct)
    {
        if (_emUso is { } l) return l;
        await SondarAsync(ct);
        return _emUso;
    }

    public async Task<Captura> IdentificarAsync(CancellationToken ct)
    {
        var l = await EmUsoAsync(ct);
        return l is null ? new Captura(ResultadoCaptura.SemLeitor) : await l.IdentificarAsync(ct);
    }

    public async Task<Cadastro> CadastrarAsync(int vaga, IProgress<int>? amostra, CancellationToken ct)
    {
        var l = await EmUsoAsync(ct);
        return l is null ? new Cadastro(ResultadoCadastro.SemLeitor) : await l.CadastrarAsync(vaga, amostra, ct);
    }

    /// <summary>Apaga no motor DA CHAVE (nao no motor em uso): hid: na guarda, o resto no Windows.</summary>
    public Task<bool> ApagarAsync(string chave, CancellationToken ct)
        => (chave ?? "").StartsWith(LeitorHidUareU.Prefixo, StringComparison.Ordinal)
            ? _hid.ApagarAsync(chave!, ct)
            : _windows.ApagarAsync(chave ?? "", ct);
}

/// <summary>
/// O TEXTO DO "CONFERIR O LEITOR" (Configuracao, bloco Digital, 1.0.53): o que o dono
/// fotografa e manda. Motor em uso, o que o Windows achou, o que o runtime da HID achou
/// (versao e de onde carregou) e as entradas USB VID_05BA com classe e driver. Sem
/// motor: a frase do dono e o passo.
/// </summary>
public static class DiagnosticoLeitor
{
    /// <summary>O PC da loja (26/10): driver da HID (Authentication Devices) e nenhuma dpfpdd.dll/dpfj.dll.</summary>
    public const string LinhaDriverHidSemRuntime =
        "Leitor plugado com o driver da HID, sem o runtime. Instale o driver do Windows (Biometric) ou o U.are.U RTE.";

    public static IReadOnlyList<string> Linhas(string? motorEmUso,
        IReadOnlyList<string> unidadesWindows, int hrWindows, string? erroWindows,
        RuntimeHid hid, IReadOnlyList<DispositivoHid> dispositivosHid, string? erroHid,
        IReadOnlyList<DriverDigitalPersona.Aparelho> aparelhos, string versaoCaixa, DateTime agora)
    {
        var l = new List<string>
        {
            $"Caixa {versaoCaixa}, {agora:dd/MM/yyyy HH:mm}",
            "Motor em uso: " + (motorEmUso switch
            {
                "winbio" => "Windows (WinBio)",
                LeitorHidUareU.MotorTexto => "HID (U.are.U RTE)",
                _ => "nenhum",
            }),
        };

        if (unidadesWindows.Count > 0)
        {
            l.Add($"Windows: {unidadesWindows.Count} unidade(s)");
            l.AddRange(unidadesWindows.Select(u => "  " + u));
        }
        else
            l.Add("Windows: nenhuma unidade" + (hrWindows == 0 || erroWindows is null ? "" : $" ({erroWindows})"));

        if (!hid.Carregado)
            l.Add("HID: U.are.U RTE não instalado" + (hid.Erro is null ? "" : $" ({hid.Erro})"));
        else
        {
            l.Add($"HID: U.are.U RTE {hid.Versao ?? "?"} em {hid.Caminho}, {dispositivosHid.Count} leitor(es)" + (erroHid is null ? "" : $" ({erroHid})"));
            l.AddRange(dispositivosHid.Select(d => "  " + d));
        }

        if (aparelhos.Count == 0)
            l.Add("USB VID_05BA: nenhum leitor DigitalPersona neste PC");
        else
        {
            l.Add($"USB VID_05BA: {aparelhos.Count} entrada(s)");
            l.AddRange(aparelhos.Select(a => "  " + DriverDigitalPersona.Linha(a)));
        }

        // o caso da loja (26/10): classe "Authentication Devices" = driver proprio da HID, sem WBF
        var daHid = aparelhos.FirstOrDefault(a => a.Presente && DriverDigitalPersona.EhDriverDaHid(a));
        if (daHid is not null && hid.Carregado)
            l.Add($"Driver da HID instalado (classe {daHid.Classe}, não é o WBF): usando o runtime da HID.");

        if (motorEmUso is null)
        {
            l.Add(daHid is not null && !hid.Carregado ? LinhaDriverHidSemRuntime : Biometria.AvisoLeitorNaoEncontrado);
            l.Add(DriverDigitalPersona.PassoDoDriver);
        }
        return l;
    }

    /// <summary>
    /// Colhe tudo (a sonda escolhe o motor; WinBio, runtime da HID e registro sao lidos fora
    /// da thread de tela) e devolve as linhas. Nunca lanca.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ColherAsync(LeitorAutomatico leitor, IApiHid api, string versaoCaixa,
        CancellationToken ct = default)
    {
        try { await leitor.SondarAsync(ct); } catch { }
        var (unidades, hr, erro) = await Task.Run(LeitorWinBio.Enumerar);
        var rt = await Task.Run(api.Carregar);
        string? erroHid = null;
        var devs = rt.Carregado ? await Task.Run(() => api.Dispositivos(out erroHid)) : Array.Empty<DispositivoHid>();
        var aparelhos = await Task.Run(DriverDigitalPersona.LerAparelhos);
        return Linhas(leitor.MotorEmUso, unidades.Select(u => u.ToString()).ToList(), hr, erro, rt, devs, erroHid, aparelhos, versaoCaixa, DateTime.Now);
    }
}
