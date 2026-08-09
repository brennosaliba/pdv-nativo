using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using Dapper;

namespace Pdv.Nucleo;

/// <summary>
/// Sobe para a nuvem os XMLs das notas emitidas pelo agente local.
///
/// Sem isso a nota existe na SEFAZ mas mora só no HD do caixa: some da 2ª via, do
/// extrato do contador, e a guarda de 5 anos passa a depender de um disco de loja.
/// Do ponto de vista do dono, a nota "não saiu direito" — ele não a encontra em lugar
/// nenhum dos relatórios dele.
///
/// Nada aqui pode atrasar uma venda: a subida é disparada DEPOIS do cupom, sempre em
/// segundo plano, e falha em silêncio para tentar de novo no próximo ciclo.
/// </summary>
public sealed class GuardaNuvem : IDisposable
{
    /// <summary>Uma nota pronta para subir. O XML é a carga; o resto é índice.</summary>
    private sealed record NotaLocal(
        string Chave, string? Cnpj, int? Serie, long? NNF, decimal? VNF,
        string? Status, string? CStat, string? XMotivo, string? Protocolo,
        string? QrCode, string? Documento, string? EmitidaEm, string Xml);

    private readonly Nuvem _nuvem;
    private readonly string _agenteUrl;
    private readonly string _urlNuvem;
    private readonly SemaphoreSlim _porta = new(1, 1);
    private Timer? _timer;
    private int _semPermissao;

    /// <summary>Quantas vezes seguidas a nuvem recusou por permissão. Não é transitório.</summary>
    public int RecusasPorPermissao => _semPermissao;

    public GuardaNuvem(Nuvem nuvem, string agenteUrl, string urlNuvem)
    {
        _nuvem = nuvem;
        _agenteUrl = agenteUrl.TrimEnd('/');
        _urlNuvem = urlNuvem.TrimEnd('/');
    }

    /// <summary>
    /// Dispara sem esperar. É o que a tela de pagamento chama depois de imprimir —
    /// nunca antes, e nunca com await: o cupom e a gaveta não esperam a nuvem.
    /// </summary>
    public void Cutucar() => _ = Task.Run(() => SubirAsync());

    /// <summary>
    /// Liga o ciclo periódico e o gatilho de volta de rede. O periódico existe para
    /// pegar a nota que saiu em contingência e só ganhou protocolo depois, quando o
    /// agente retransmitiu.
    /// </summary>
    public IDisposable Iniciar(TimeSpan? intervalo = null)
    {
        var passo = intervalo ?? TimeSpan.FromSeconds(120);
        _timer = new Timer(_ => Cutucar(), null, TimeSpan.FromSeconds(20), passo);
        NetworkChange.NetworkAddressChanged += AoMudarRede;
        return this;
    }

    private void AoMudarRede(object? s, EventArgs e) => Cutucar();

    /// <summary>Sobe o que falta. Devolve quantas a nuvem confirmou. NUNCA lança.</summary>
    public async Task<int> SubirAsync(CancellationToken ct = default)
    {
        // Uma varredura por vez. Duas simultâneas mandariam a mesma chave duas vezes,
        // e a RPC faz SELECT-depois-INSERT sem trava.
        if (!_porta.Wait(0)) return 0;
        try
        {
            var notas = await ReunirAsync(ct).ConfigureAwait(false);
            if (notas.Count == 0) return 0;

            // Sessão só DEPOIS de saber que há trabalho: sem isso, todo ciclo de 2
            // minutos gastaria um login à toa.
            if (!await _nuvem.SessaoOkAsync(ct).ConfigureAwait(false)) return 0;
            var token = await _nuvem.TokenAsync(ct).ConfigureAwait(false);
            if (token is null) return 0;

            var confirmadas = 0;
            var paraMarcar = new List<string>();

            foreach (var n in notas)
            {
                if (ct.IsCancellationRequested) break;
                var (ok, permanente) = await EnviarAsync(n, token, ct).ConfigureAwait(false);
                if (!ok)
                {
                    if (permanente) continue;   // nota ruim: pula, não trava a fila
                    break;                      // problema de rede/sessão: tenta tudo depois
                }

                confirmadas++;
                _semPermissao = 0;

                // Contingência SOBE (para não ficar só no HD) mas CONTINUA na fila até
                // ganhar protocolo. Marcar antes disso congela na nuvem um XML sem
                // protocolo, e nada mais volta a oferecê-la.
                if (n.Xml.Contains("<protNFe", StringComparison.Ordinal) || n.Status == "cancelada")
                {
                    paraMarcar.Add(n.Chave);
                    using var cx = Banco.Abrir();
                    cx.Execute("UPDATE nfce_emissao SET sincronizada = 1 WHERE chave = @Ch", new { Ch = n.Chave });
                }
            }

            if (paraMarcar.Count > 0) await MarcarNoAgenteAsync(paraMarcar, ct).ConfigureAwait(false);
            return confirmadas;
        }
        catch { return 0; }
        finally { _porta.Release(); }
    }

    // ── de onde vêm as notas ────────────────────────────────────────────────
    private async Task<List<NotaLocal>> ReunirAsync(CancellationToken ct)
    {
        var lista = new List<NotaLocal>();
        var vistas = new HashSet<string>(StringComparer.Ordinal);

        // 1) o que o agente diz que falta
        foreach (var n in await DoAgenteAsync(ct).ConfigureAwait(false))
            if (vistas.Add(n.Chave)) lista.Add(n);

        // 2) rede de segurança: o índice do agente já se provou capaz de perder
        //    registros (leitura ruim devolve objeto vazio). O nosso banco tem a chave
        //    de toda nota que passou por aqui — e ainda tem qrCode e xMotivo, que o
        //    índice do agente nem guarda.
        foreach (var n in await DoBancoLocalAsync(vistas, ct).ConfigureAwait(false))
            if (vistas.Add(n.Chave)) lista.Add(n);

        return lista;
    }

    private async Task<List<NotaLocal>> DoAgenteAsync(CancellationToken ct)
    {
        var vazio = new List<NotaLocal>();
        var corpo = await PegarAsync($"{_agenteUrl}/nfce/nao-sincronizadas?limit=50", 8000, ct).ConfigureAwait(false);
        if (corpo is null) return vazio;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            if (!doc.RootElement.TryGetProperty("notas", out var arr)) return vazio;
            foreach (var e in arr.EnumerateArray())
            {
                var chave = Texto(e, "chave");
                var xml = Texto(e, "xml");
                if (chave is not { Length: 44 } || xml is not { Length: > 0 }) continue;
                vazio.Add(new NotaLocal(chave, Texto(e, "cnpj"), Inteiro(e, "serie"), Longo(e, "nNF"),
                    Decimal(e, "vNF"), StatusDaNuvem(Texto(e, "status"), Texto(e, "cStat"), chave),
                    Texto(e, "cStat"), null, Texto(e, "protocolo"), null,
                    Texto(e, "documento"), Texto(e, "atualizadoEm"), xml));
            }
        }
        catch { return vazio; }
        return vazio;
    }

    private async Task<List<NotaLocal>> DoBancoLocalAsync(HashSet<string> jaTem, CancellationToken ct)
    {
        var lista = new List<NotaLocal>();
        List<dynamic> pendentes;
        try
        {
            using var cx = Banco.Abrir();
            pendentes = cx.Query("""
                SELECT chave, numero, serie, tp_amb, protocolo, c_stat, x_motivo, qr_code,
                       v_nf_cent, dh_recbto, emit_cnpj, autorizado, contingencia
                  FROM nfce_emissao
                 WHERE chave IS NOT NULL AND sincronizada = 0
                 ORDER BY criado_em
                 LIMIT 50
                """).ToList();
        }
        catch { return lista; }

        foreach (var p in pendentes)
        {
            var chave = (string)p.chave;
            if (jaTem.Contains(chave)) continue;
            // o agente é a fonte do XML; aqui só buscamos o que ele guardou
            var xml = await XmlDoAgenteAsync(chave, ct).ConfigureAwait(false);
            if (xml is null) continue;

            var status = (long)p.autorizado == 1 ? "autorizada"
                       : (long)p.contingencia == 1 ? "contingencia" : "rejeitada";
            lista.Add(new NotaLocal(chave, p.emit_cnpj as string,
                p.serie is null ? null : (int)(long)p.serie,
                p.numero is null ? null : (long)p.numero,
                p.v_nf_cent is null ? null : (long)p.v_nf_cent / 100m,
                status, p.c_stat as string, p.x_motivo as string, p.protocolo as string,
                p.qr_code as string, null, p.dh_recbto as string, xml));
        }
        return lista;
    }

    private async Task<string?> XmlDoAgenteAsync(string chave, CancellationToken ct)
    {
        var corpo = await PegarAsync($"{_agenteUrl}/nfce/xml?chave={chave}", 8000, ct).ConfigureAwait(false);
        if (corpo is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var xml = Texto(doc.RootElement, "xml");
            return xml is { Length: > 0 } ? xml : null;
        }
        catch { return null; }
    }

    // ── envio ───────────────────────────────────────────────────────────────
    /// <summary>Devolve (subiu, erroPermanente). Erro permanente = pular a nota.</summary>
    private async Task<(bool ok, bool permanente)> EnviarAsync(NotaLocal n, string token, CancellationToken ct)
    {
        try
        {
            var corpo = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["p_chave"] = n.Chave,
                ["p_xml"] = n.Xml,
                ["p_numero"] = n.NNF,
                ["p_serie"] = n.Serie,
                ["p_status"] = n.Status,
                ["p_protocolo"] = n.Protocolo,
                ["p_valor"] = n.VNF,
                ["p_c_stat"] = n.CStat,
                ["p_x_motivo"] = n.XMotivo,
                ["p_qr_code"] = n.QrCode,
                ["p_documento"] = n.Documento,
                ["p_emitida_em"] = n.EmitidaEm,
            });

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(20_000);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_urlNuvem}/rest/v1/rpc/nfce_registrar_do_caixa");
            req.Headers.TryAddWithoutValidation("apikey", Nuvem.AnonKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(corpo, Encoding.UTF8, "application/json");

            using var resp = await Fiscal.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var texto = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            // Chave duplicada é SUCESSO: significa que a nota já está lá. Duas varreduras
            // concorrentes (ou um retry) chegam nisso, e tratar como erro faria a nota
            // ficar eternamente na fila.
            if (texto.Contains("23505", StringComparison.Ordinal)
                || texto.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)) return (true, false);

            if (!resp.IsSuccessStatusCode) return (false, false);

            using var doc = JsonDocument.Parse(texto);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                var erro = Texto(doc.RootElement, "error") ?? "";
                // Falta de permissão não melhora tentando de novo — é configuração.
                if (erro.Contains("permiss", StringComparison.OrdinalIgnoreCase))
                {
                    _semPermissao++;
                    return (false, false);
                }
                return (false, true);   // chave inválida, xml ausente: pula a nota
            }
            return (true, false);
        }
        catch { return (false, false); }
    }

    private async Task MarcarNoAgenteAsync(List<string> chaves, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(8000);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_agenteUrl}/nfce/marcar-sincronizada");
            req.Content = new StringContent(JsonSerializer.Serialize(new { chaves }), Encoding.UTF8, "application/json");
            using var _ = await Fiscal.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
        }
        catch { /* não conseguiu marcar: a nota volta na próxima varredura e a RPC é idempotente */ }
    }

    private static async Task<string?> PegarAsync(string url, int prazoMs, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(prazoMs);
            using var resp = await Fiscal.Http.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch { return null; }
    }

    /// <summary>
    /// O agente grava no masculino ('autorizado') e a nuvem usa o feminino
    /// ('autorizada'). Não é preciosismo de idioma: o extrato do contador filtra por
    /// <c>status in ('autorizada','cancelada','contingencia')</c>, então gravar
    /// 'autorizado' faz a nota subir e MESMO ASSIM sumir do relatório — o pior dos
    /// dois mundos, porque o sintoma some e o problema fica.
    /// </summary>
    private static string StatusDaNuvem(string? doAgente, string? cStat, string chave) => (doAgente ?? "").Trim() switch
    {
        "autorizado" => "autorizada",
        "pendente" => "contingencia",
        "rejeitado" => "rejeitada",
        "cancelada" or "autorizada" or "contingencia" or "rejeitada" => doAgente!.Trim(),
        // vazio ou o cStat cru: decide pelo cStat e, na dúvida, pelo tpEmis da chave
        _ => cStat is "100" or "150" or "120" ? "autorizada"
           : chave.Length == 44 && chave[34] == '9' ? "contingencia"
           : "rejeitada",
    };

    private static string? Texto(JsonElement e, string nome)
        => e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Inteiro(JsonElement e, string nome)
        => e.TryGetProperty(nome, out var v) && v.TryGetInt32(out var i) ? i : null;

    private static long? Longo(JsonElement e, string nome)
        => e.TryGetProperty(nome, out var v) && v.TryGetInt64(out var l) ? l : null;

    private static decimal? Decimal(JsonElement e, string nome)
        => e.TryGetProperty(nome, out var v) && v.TryGetDecimal(out var d) ? d : null;

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= AoMudarRede;
        _timer?.Dispose();
        _porta.Dispose();
    }
}
