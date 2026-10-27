using System.Text.RegularExpressions;

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
///
/// 1.0.57 (27/10/2026): o dono mandou a foto do leitor FUNCIONANDO e perguntou "o que isso
/// quer dizer?". A linha "U.are.U RTE nao instalado" assustava. Agora a janela abre com um
/// RESUMO em linguagem de loja (Resumo, puro), e as linhas tecnicas vem depois, sob
/// "Detalhes tecnicos (para o suporte):". Com o Windows lendo, a linha do runtime da HID
/// diz que ele nao faz falta.
/// </summary>
public static class DiagnosticoLeitor
{
    /// <summary>O PC da loja (26/10): driver da HID (Authentication Devices) e nenhuma dpfpdd.dll/dpfj.dll.</summary>
    public const string LinhaDriverHidSemRuntime =
        "Leitor plugado com o driver da HID, sem o runtime. Instale o driver do Windows (Biometric) ou o U.are.U RTE.";

    // ── o resumo (1.0.57): frases curtas, uma ideia por linha, sem jargao ─────────────
    public const string Funcionando = "Leitor funcionando.";
    public const string NaoFunciona = "O leitor não está funcionando.";
    public const string PeloWindows = "O caixa lê a digital pelo Windows.";
    public const string PeloProgramaHid = "O caixa lê a digital pelo programa da HID.";
    public const string NadaAFazer = "Não precisa fazer nada.";
    public const string DriverErrado = "Ele está plugado, mas com o driver errado.";
    public const string FaltaDriver = "Ele está plugado, mas falta o driver.";
    public const string ServicoParado = "O serviço de biometria do Windows não respondeu.";
    public const string Reinicie = "Reinicie o computador.";
    public const string ProgramaHidNaoAchou = "O programa da HID não achou o leitor.";
    public const string Replugue = "Tire o cabo USB, plugue de novo e reinicie o computador.";
    public const string NenhumPlugado = "Nenhum leitor plugado neste caixa.";
    public const string ConfiraCabo = "Confira o cabo USB ou troque de porta.";
    public const string TituloDetalhes = "Detalhes técnicos (para o suporte):";
    public const string RteNaoFazFalta = "HID: U.are.U RTE não instalado (não faz falta, o Windows já lê)";

    /// <summary>O passo do driver WBF da HID, em linguagem de loja (o PassoDoDriver segue nos detalhes).</summary>
    public static readonly IReadOnlyList<string> PassoDriverWbf = new[]
    {
        "Como resolver:",
        "1. Baixe o driver WBF do leitor no site da HID (hidglobal.com, Drivers).",
        "2. No Gerenciador de Dispositivos, clique com o botão direito no leitor e escolha Atualizar driver.",
        "3. Toque em Procurar drivers no computador, depois em Permitir que eu escolha, depois em Com Disco, e abra a pasta do driver.",
        "4. Escolha \"U.are.U 4500 Fingerprint Reader (WBF)\".",
        "5. Reinicie o computador.",
    };

    /// <summary>
    /// O RESUMO que abre a janela (1.0.57), dos mesmos dados das linhas tecnicas. A primeira
    /// linha e o estado (vai tambem na linha da Configuracao): "Leitor funcionando.", "O leitor
    /// nao esta funcionando." ou "Nenhum leitor plugado neste caixa.". A ordem dos casos sem
    /// motor e a do DriverDigitalPersona.Avaliar: WBF plugado = servico; driver da HID = driver
    /// errado; outro driver = falta driver; nada plugado = cabo.
    /// </summary>
    public static IReadOnlyList<string> Resumo(string? motorEmUso, IReadOnlyList<string> unidadesWindows, RuntimeHid hid,
        IReadOnlyList<DispositivoHid> dispositivosHid, IReadOnlyList<DriverDigitalPersona.Aparelho> aparelhos)
    {
        var presentes = aparelhos.Where(a => a.Presente).ToList();
        var r = new List<string>();
        if (motorEmUso == "winbio")
        {
            r.Add(Funcionando);
            r.Add(PeloWindows);
            r.Add(NadaAFazer);
            var nome = unidadesWindows.Select(NomeCurto).FirstOrDefault(n => n is not null)
                       ?? presentes.Select(a => NomeCurto(a.Descricao)).FirstOrDefault(n => n is not null);
            if (nome is not null) r.Add($"Leitor: {nome}, plugado");
            return r;
        }
        if (motorEmUso == LeitorHidUareU.MotorTexto)
        {
            r.Add(Funcionando);
            r.Add(PeloProgramaHid);
            r.Add(NadaAFazer);
            var nome = dispositivosHid.Select(d => NomeCurto(d.Produto)).FirstOrDefault(n => n is not null)
                       ?? presentes.Select(a => NomeCurto(a.Descricao)).FirstOrDefault(n => n is not null);
            if (nome is not null) r.Add($"Leitor: {nome}, plugado");
            return r;
        }

        // sem motor
        if (presentes.Count == 0)
        {
            r.Add(NenhumPlugado);
            r.Add(ConfiraCabo);
            return r;
        }
        r.Add(NaoFunciona);
        if (presentes.Any(DriverDigitalPersona.EhWbf))
        {
            r.Add(ServicoParado);
            r.Add(Reinicie);
        }
        else if (presentes.Any(DriverDigitalPersona.EhDriverDaHid))
        {
            if (hid.Carregado)
            {
                // o runtime da HID esta la, mas nao achou o aparelho
                r.Add(ProgramaHidNaoAchou);
                r.Add(Replugue);
            }
            else
            {
                r.Add(DriverErrado);
                r.AddRange(PassoDriverWbf);
            }
        }
        else
        {
            r.Add(FaltaDriver);
            r.AddRange(PassoDriverWbf);
        }
        return r;
    }

    /// <summary>
    /// "#3 U.are.U® 4500 Fingerprint Reader (WBF) (CROSSMATCH ...)" vira "U.are.U 4500";
    /// "Leitora de Impressão Digital, U.are.U® 4500" tambem. Vazio = null.
    /// </summary>
    public static string? NomeCurto(string? descricao)
    {
        var s = descricao ?? "";
        s = s.Replace("®", "").Replace("™", "").Replace("©", "");
        s = Regex.Replace(s, @"^\s*#\d+\s*", "");
        s = Regex.Replace(s, @"\([^)]*\)", " ");
        s = Regex.Replace(s, @"fingerprint\s+reader|leitora?\s+de\s+impress[aã]o\s+digital", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', ',', '.', ';', ':', '-');
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// O texto inteiro da janela (1.0.57): a linha da versao e hora, o resumo, uma linha em
    /// branco, "Detalhes tecnicos (para o suporte):" e as linhas tecnicas (sem repetir a primeira).
    /// </summary>
    public static IReadOnlyList<string> Janela(IReadOnlyList<string> resumo, IReadOnlyList<string> tecnicas)
    {
        var j = new List<string>();
        if (tecnicas.Count > 0) j.Add(tecnicas[0]);
        j.AddRange(resumo);
        j.Add("");
        j.Add(TituloDetalhes);
        j.AddRange(tecnicas.Skip(1));
        return j;
    }

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
            // 1.0.57: com o Windows lendo, o runtime da HID nao faz falta (a linha assustava o dono)
            l.Add(motorEmUso == "winbio" ? RteNaoFazFalta
                  : "HID: U.are.U RTE não instalado" + (hid.Erro is null ? "" : $" ({hid.Erro})"));
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
    /// O que o Conferir o leitor mostra (1.0.57): o resumo, as linhas tecnicas e a janela
    /// inteira. <see cref="Estado"/> e a primeira frase do resumo (a linha da Configuracao).
    /// </summary>
    public sealed record Conferencia(IReadOnlyList<string> Resumo, IReadOnlyList<string> Tecnicas)
    {
        public string Estado => Resumo.Count > 0 ? Resumo[0] : "";
        public IReadOnlyList<string> Janela => DiagnosticoLeitor.Janela(Resumo, Tecnicas);
    }

    /// <summary>Junta resumo e linhas tecnicas dos mesmos dados (puro; o ColherAsync so colhe).</summary>
    public static Conferencia Montar(string? motorEmUso,
        IReadOnlyList<string> unidadesWindows, int hrWindows, string? erroWindows,
        RuntimeHid hid, IReadOnlyList<DispositivoHid> dispositivosHid, string? erroHid,
        IReadOnlyList<DriverDigitalPersona.Aparelho> aparelhos, string versaoCaixa, DateTime agora)
        => new(Resumo(motorEmUso, unidadesWindows, hid, dispositivosHid, aparelhos),
               Linhas(motorEmUso, unidadesWindows, hrWindows, erroWindows, hid, dispositivosHid, erroHid, aparelhos, versaoCaixa, agora));

    /// <summary>
    /// Colhe tudo (a sonda escolhe o motor; WinBio, runtime da HID e registro sao lidos fora
    /// da thread de tela) e devolve o resumo e as linhas. Nunca lanca.
    /// </summary>
    public static async Task<Conferencia> ColherAsync(LeitorAutomatico leitor, IApiHid api, string versaoCaixa,
        CancellationToken ct = default)
    {
        try { await leitor.SondarAsync(ct); } catch { }
        var (unidades, hr, erro) = await Task.Run(LeitorWinBio.Enumerar);
        var rt = await Task.Run(api.Carregar);
        string? erroHid = null;
        var devs = rt.Carregado ? await Task.Run(() => api.Dispositivos(out erroHid)) : Array.Empty<DispositivoHid>();
        var aparelhos = await Task.Run(DriverDigitalPersona.LerAparelhos);
        return Montar(leitor.MotorEmUso, unidades.Select(u => u.ToString()).ToList(), hr, erro, rt, devs, erroHid, aparelhos, versaoCaixa, DateTime.Now);
    }
}
