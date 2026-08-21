using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// ControlPay de MENTIRA: um HttpMessageHandler que responde as APIs que o ClienteControlPay usa
/// (Terminal/GetByPessoaId, Venda/Vender, IntencaoVenda/GetById, IntencaoVenda/GetByFiltros,
/// PagamentoExterno/GetById, Venda/CancelarVenda, PagamentoExterno/InsertPagamentoExternoTipoAdmin)
/// com os JSONs da documentação. Cada intenção avança de "Em Pagamento" para o desfecho do
/// roteiro depois de <see cref="PollsAteFinal"/> consultas — simulando o cliente no pinpad.
///
/// Também FISCALIZA: exige a chave certa na query (`key=`) e o header User-Agent em toda
/// chamada, e guarda cada requisição (rota + body) para os testes inspecionarem.
/// </summary>
public sealed class FakeControlPay : HttpMessageHandler
{
    public enum Desfecho
    {
        Aprovar,
        Recusar,                 // 25 com "NEGADA 01"
        CancelarNoPinpad,        // 25 com "OPERAÇÃO CANCELADA"
        ExpirarNoVender,         // Vender já devolve 15
        ExpirarDepois,           // 6 e depois 15
        NuncaTermina,            // fica 6 para sempre (operador cancela)
        Erro500,                 // HTTP 500 no próximo Vender (com a URL no corpo, para o teste de sanitização)
    }

    public enum DesfechoCancelamento { Cancelar, NegarPeloHost, NuncaTermina }

    public const string ChaveCerta = "chave/teste+abc=";
    public string TerminalId { get; init; } = "6408";
    public string PessoaId { get; init; } = "12247";

    /// <summary>Consultas GetById até a intenção virar final (simula o tempo no pinpad).</summary>
    public int PollsAteFinal { get; set; } = 2;

    /// <summary>Responde com a chave antiga "pagamentosExterno" (compat) em vez de "pagamentosExternos".</summary>
    public bool NomeAntigoDaLista { get; set; }

    /// <summary>Omite o dump respostaAdquirente (só comprovantes em texto + campos estruturados).</summary>
    public bool SemDump { get; set; }

    public ConcurrentQueue<Desfecho> Roteiro { get; } = new();
    public ConcurrentQueue<DesfechoCancelamento> RoteiroCancelamento { get; } = new();

    public sealed record Chamada(string Metodo, string Rota, string Query, string? Body, string? UserAgent);
    private readonly List<Chamada> _chamadas = new();
    private readonly object _trava = new();
    public IReadOnlyList<Chamada> Chamadas { get { lock (_trava) return _chamadas.ToList(); } }
    public IReadOnlyList<Chamada> Rota(string prefixo) => Chamadas.Where(c => c.Rota.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase)).ToList();

    private sealed class Intencao
    {
        public long Id; public int Status = 6; public string Nome = "Em Pagamento"; public long ValorCent; public int Forma; public string? Referencia;
        public int Parcelas = 1; public int Polls; public Desfecho Desfecho; public long PeVendaId; public long? PeCancId;
        public DesfechoCancelamento? Canc; public int PollsCanc; public bool Aprovada;
    }
    private readonly ConcurrentDictionary<long, Intencao> _intencoes = new();
    private long _proxId = 2015371, _proxPe = 2025992;
    private readonly ConcurrentDictionary<long, (bool Pronto, int Polls)> _adms = new();

    public long UltimaIntencao { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var uri = req.RequestUri!;
        var rota = uri.AbsolutePath.Replace("/webapi/", "/").Trim('/');
        var query = uri.Query;
        var body = req.Content is null ? null : req.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
        var ua = req.Headers.UserAgent.ToString();
        lock (_trava) _chamadas.Add(new Chamada(req.Method.Method, rota, query, body, string.IsNullOrEmpty(ua) ? null : ua));

        var q = System.Web.HttpUtility.ParseQueryString(query);
        if (q["key"] != ChaveCerta)
            return Task.FromResult(Json(HttpStatusCode.Unauthorized, """{"message": "Acesso não autorizado [Token não encontrado]."}"""));
        if (string.IsNullOrEmpty(ua))
            return Task.FromResult(Json(HttpStatusCode.BadRequest, """{"message": "User-Agent obrigatório"}"""));

        switch (rota.ToLowerInvariant().TrimEnd('/'))
        {
            case "terminal/getbypessoaid":
                return Task.FromResult(Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    data = "21/08/2026 18:00:00.0000",
                    terminais = new object[]
                    {
                        new { id = int.Parse(TerminalId), nome = "Terminal Virtual 114975", aguardaTef = true, vendaPorValor = true, permiteVendaParcelada = true,
                              terminalFisico = new { id = 7027, nome = "Terminal Fisico 114975", instalacaoId = "127310" } },
                        new { id = 9999, nome = "Terminal sem fisico", aguardaTef = false, vendaPorValor = true, permiteVendaParcelada = false, terminalFisico = (object?)null },
                    },
                })));
            case "venda/vender":
                return Task.FromResult(Vender(body ?? "{}", uri));
            case "intencaovenda/getbyid":
                return Task.FromResult(GetById(long.Parse(q["intencaoVendaId"] ?? "0")));
            case "intencaovenda/getbyfiltros":
                return Task.FromResult(GetByFiltros(body ?? "{}"));
            case "pagamentoexterno/getbyid":
                return Task.FromResult(PagamentoExternoGetById(long.Parse(q["pagamentoExternoId"] ?? "0")));
            case "venda/cancelarvenda":
                return Task.FromResult(CancelarVenda(body ?? "{}"));
            case "pagamentoexterno/insertpagamentoexternotipoadmin":
                return Task.FromResult(InserirAdm(body ?? "{}"));
        }
        return Task.FromResult(Json(HttpStatusCode.NotFound, """{"message":"rota desconhecida"}"""));
    }

    private HttpResponseMessage Vender(string body, Uri uri)
    {
        if (!Roteiro.TryDequeue(out var d)) d = Desfecho.Aprovar;
        if (d == Desfecho.Erro500)
            return Json(HttpStatusCode.InternalServerError, "{\"message\":\"erro interno em " + uri.AbsoluteUri.Replace("\"", "") + "\"}");
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        if (!r.TryGetProperty("formaPagamentoId", out _))
            return Json(HttpStatusCode.BadRequest, """{"message": "Parâmetro [formaPagamentoId] não pode ser nulo."}""");
        var valorTxt = r.TryGetProperty("valorTotalVendido", out var v) ? v.GetString() ?? "0,00" : "0,00";
        var it = new Intencao
        {
            Id = Interlocked.Increment(ref _proxId),
            ValorCent = (long)Math.Round(decimal.Parse(valorTxt.Replace(".", "").Replace(',', '.'), CultureInfo.InvariantCulture) * 100),
            Forma = r.GetProperty("formaPagamentoId").GetInt32(),
            Referencia = r.TryGetProperty("referencia", out var rf) ? rf.GetString() : null,
            Parcelas = r.TryGetProperty("quantidadeParcelas", out var qp) && qp.ValueKind == JsonValueKind.Number ? qp.GetInt32() : 1,
            Desfecho = d,
            PeVendaId = Interlocked.Increment(ref _proxPe),
        };
        if (d == Desfecho.ExpirarNoVender) { it.Status = 15; it.Nome = "Expirado"; }
        _intencoes[it.Id] = it;
        UltimaIntencao = it.Id;
        return Json(HttpStatusCode.OK, "{\"data\":\"21/08/2026 18:00:00.0000\",\"intencaoVenda\":" + IntencaoJson(it, resumido: true) + "}");
    }

    private HttpResponseMessage GetById(long id)
    {
        if (!_intencoes.TryGetValue(id, out var it)) return Json(HttpStatusCode.BadRequest, """{"message":"intenção não encontrada"}""");
        Avancar(it);
        return Json(HttpStatusCode.OK, "{\"data\":\"21/08/2026 18:00:01.0000\",\"intencaoVenda\":" + IntencaoJson(it, resumido: true) + "}");
    }

    private HttpResponseMessage GetByFiltros(string body)
    {
        using var doc = JsonDocument.Parse(body);
        long id = 0;
        if (doc.RootElement.TryGetProperty("intencaoVendaId", out var v))
            id = v.ValueKind == JsonValueKind.Number ? v.GetInt64() : long.TryParse(v.GetString(), out var l) ? l : 0;
        if (!_intencoes.TryGetValue(id, out var it)) return Json(HttpStatusCode.OK, """{"data":"x","intencoesVendas":[]}""");
        Avancar(it);
        return Json(HttpStatusCode.OK, "{\"data\":\"21/08/2026 18:00:02.0000\",\"intencoesVendas\":[" + IntencaoJson(it, resumido: false) + "]}");
    }

    private HttpResponseMessage PagamentoExternoGetById(long peId)
    {
        if (_adms.TryGetValue(peId, out var adm))
        {
            // operação administrativa: "em operação" até PollsAteFinal consultas, depois finalizada (sem comprovante)
            adm = (adm.Polls + 1 >= PollsAteFinal, adm.Polls + 1);
            _adms[peId] = adm;
            var st = adm.Pronto ? new { id = 15, nome = "Finalizado" } : new { id = 10, nome = "Em Operação" };
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = "x", pagamentoExterno = new { id = peId, tipo = 15, origem = 1, mensagemRespostaAdquirente = "OPERACAO CONCLUIDA", pagamentoExternoStatus = st, terminal = new { id = int.Parse(TerminalId), nome = "CAIXA 01" } } }));
        }
        var it = _intencoes.Values.FirstOrDefault(i => i.PeVendaId == peId || i.PeCancId == peId);
        if (it is null) return Json(HttpStatusCode.BadRequest, """{"message":"pagamento externo não encontrado"}""");
        return Json(HttpStatusCode.OK, "{\"data\":\"x\",\"pagamentoExterno\":" + PeJson(it, peId == it.PeCancId ? 10 : 5, completo: true, comBandeira: true) + "}");
    }

    private HttpResponseMessage CancelarVenda(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        if (!r.TryGetProperty("intencaoVendaId", out var iv) || string.IsNullOrEmpty(iv.ToString()))
            return Json(HttpStatusCode.BadRequest, """{"message": "Parâmetro [intencaoVendaId] não pode ser nulo."}""");
        if (!r.TryGetProperty("senhaTecnica", out var st) || st.GetString() != "314159")
            return Json(HttpStatusCode.BadRequest, """{"message": "Senha técnica inválida."}""");
        var id = long.Parse(iv.ToString());
        if (!_intencoes.TryGetValue(id, out var it) || !it.Aprovada)
            return Json(HttpStatusCode.BadRequest, """{"message": "Intenção de venda não está autorizada."}""");
        if (!RoteiroCancelamento.TryDequeue(out var dc)) dc = DesfechoCancelamento.Cancelar;
        it.Canc = dc; it.PollsCanc = 0; it.PeCancId = Interlocked.Increment(ref _proxPe);
        it.Status = 18; it.Nome = "Cancelamento iniciado";
        return Json(HttpStatusCode.OK, "{\"data\":\"x\",\"intencaoVenda\":" + IntencaoJson(it, resumido: true) + "}");
    }

    private HttpResponseMessage InserirAdm(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("senhaTecnica", out var st) || st.GetString() != "314159")
            return Json(HttpStatusCode.BadRequest, """{"message": "Senha técnica inválida."}""");
        var id = Interlocked.Increment(ref _proxPe);
        _adms[id] = (false, 0);
        return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = "x", pagamentoExterno = new { id, tipo = 15, origem = 1, pagamentoExternoStatus = new { id = 10, nome = "Em Operacao" }, terminal = new { id = int.Parse(TerminalId), nome = "CAIXA 01" } } }));
    }

    /// <summary>Cada consulta é um "tique" do pinpad: depois de PollsAteFinal a intenção fecha.</summary>
    private void Avancar(Intencao it)
    {
        if (it.Canc is { } dc)
        {
            it.PollsCanc++;
            if (it.PollsCanc < PollsAteFinal) { it.Status = 19; it.Nome = "Em cancelamento"; return; }
            switch (dc)
            {
                case DesfechoCancelamento.Cancelar: it.Status = 20; it.Nome = "Cancelado"; break;
                case DesfechoCancelamento.NegarPeloHost: it.Status = 10; it.Nome = "Creditado"; break;   // volta a creditado + pe tipo 10 negado
                case DesfechoCancelamento.NuncaTermina: it.Status = 19; it.Nome = "Em cancelamento"; break;
            }
            return;
        }
        if (it.Status != 6) return;
        it.Polls++;
        if (it.Polls < PollsAteFinal) return;
        switch (it.Desfecho)
        {
            case Desfecho.Aprovar: it.Status = 10; it.Nome = "Creditado"; it.Aprovada = true; break;
            case Desfecho.Recusar: case Desfecho.CancelarNoPinpad: it.Status = 25; it.Nome = "Pagamento recusado"; break;
            case Desfecho.ExpirarDepois: it.Status = 15; it.Nome = "Expirado"; break;
            case Desfecho.NuncaTermina: break;
        }
    }

    private string IntencaoJson(Intencao it, bool resumido)
    {
        var lista = NomeAntigoDaLista ? "pagamentosExterno" : "pagamentosExternos";
        var pes = new List<Dictionary<string, object?>> { Pe(it, 5, completo: !resumido, comBandeira: false) };
        if (it.PeCancId is not null && (it.Status == 20 || (it.Status == 10 && it.Canc == DesfechoCancelamento.NegarPeloHost)))
            pes.Add(Pe(it, 10, completo: !resumido, comBandeira: false));
        var modalidade = it.Forma switch { 21 => "Crédito", 22 => "Débito", 25 => "Carteira Digital", _ => "Outros" };
        var d = new Dictionary<string, object?>
        {
            ["id"] = it.Id, ["referencia"] = it.Referencia, ["token"] = "822446", ["data"] = "21/08/2026 18:00:00.0000", ["hora"] = "18:00:00",
            ["quantidade"] = 0, ["valorOriginal"] = it.ValorCent / 100m, ["valorOriginalFormat"] = ClienteControlPay.ValorComVirgula(it.ValorCent),
            ["valorFinal"] = it.ValorCent / 100m, ["valorFinalFormat"] = ClienteControlPay.ValorComVirgula(it.ValorCent),
            ["quantidadeParcelas"] = it.Parcelas, ["urlPagamento"] = null,
            ["formaPagamento"] = new { id = it.Forma, nome = "TEF", modalidade, fluxoPagamento = new { id = 21, nome = "TEF" } },
            ["terminal"] = new { id = int.Parse(TerminalId), nome = "Terminal Virtual 114975" },
            [lista] = pes,
            ["intencaoVendaStatus"] = new { id = it.Status, nome = it.Nome },
            ["cliente"] = null, ["produtos"] = Array.Empty<object>(), ["pedido"] = null,
        };
        return JsonSerializer.Serialize(d);
    }

    private string PeJson(Intencao it, int tipo, bool completo, bool comBandeira) => JsonSerializer.Serialize(Pe(it, tipo, completo, comBandeira));

    private Dictionary<string, object?> Pe(Intencao it, int tipo, bool completo, bool comBandeira)
    {
        var id = tipo == 10 ? it.PeCancId ?? 0 : it.PeVendaId;
        var finalizado = tipo == 10 ? it.Status is 20 or 10 : it.Status != 6;
        object st = finalizado ? new { id = 15, nome = "Finalizado" } : new { id = 10, nome = "Em Operação" };
        if (!completo || !finalizado)
            return new Dictionary<string, object?> { ["id"] = id, ["tipo"] = tipo, ["idPagamento"] = "", ["origem"] = 5, ["tipoParcelamento"] = 1, ["pagamentoExternoStatus"] = st, ["nomeTitularCartao"] = null };

        var msg = tipo == 10
            ? (it.Canc == DesfechoCancelamento.NegarPeloHost ? "TRANSAÇÃO NEGADA PELO HOST" : "CANCELAMENTO APROVADO")
            : it.Desfecho switch
            {
                Desfecho.Recusar => "NEGADA 01",
                Desfecho.CancelarNoPinpad => "OPERAÇÃO CANCELADA",
                Desfecho.ExpirarDepois => "EXPIRADA",
                _ => "AUTORIZADA 019501",
            };
        var aprovada = tipo == 10 ? it.Status == 20 : it.Aprovada;
        var nsu = tipo == 10 ? "16175123899" : "16175123815";
        var valor = ClienteControlPay.ValorComVirgula(it.ValorCent);
        var dump = aprovada && !SemDump ? Dump(it, tipo, nsu) : null;
        var viaCliente = aprovada ? "\r\n COMPROVANTE DE TEF\r\n VIA: CLIENTE\r\n DEMOCARD ************4646\r\n VALOR FINAL: R$ " + valor + "\r\n" : null;
        var viaEstab = aprovada ? "\r\n COMPROVANTE DE TEF\r\n VIA: ESTABELECIMENTO\r\n DEMOCARD ************4646\r\n VALOR FINAL: R$ " + valor + "\r\n ________________\r\n" : null;
        var d = new Dictionary<string, object?>
        {
            ["id"] = id, ["tipo"] = tipo, ["origem"] = 15, ["nsuTid"] = aprovada ? nsu : null, ["trnNsu"] = "", ["autorizacao"] = aprovada ? "019501" : null,
            ["adquirente"] = "VISANET", ["codigoRespostaAdquirente"] = aprovada ? "0" : "51",
            ["mensagemRespostaAdquirente"] = msg, ["dataAdquirente"] = "2026-08-21T18:00:03",
            ["respostaAdquirente"] = dump, ["comprovanteAdquirente"] = null, ["comprovanteEstabelecimento"] = viaEstab, ["comprovanteCliente"] = viaCliente,
            ["tipoParcelamento"] = 1, ["pagamentoExternoStatus"] = st, ["nomeTitularCartao"] = "CLIENTE TESTE",
        };
        if (comBandeira) d["bandeira"] = "VISA";
        return d;
    }

    /// <summary>Dump intpos como no exemplo da doc (010/012/013/040/713/715/740).</summary>
    private static string Dump(Intencao it, int tipo, string nsu)
    {
        var linhas = new List<string>
        {
            "000-000 = " + (tipo == 10 ? "CNC" : "CRT"), "001-000 = " + it.Id, "003-000 = " + it.ValorCent, "009-000 = 0",
            "010-000 = VISANET", "012-000 = " + nsu, "013-000 = 019501", "022-000 = 21082026", "023-000 = 180003",
            "027-000 = 2608211800" + nsu, "030-000 = AUTORIZADA 019501", "040-000 = DEMOCARD",
            "712-000 = 4", "713-001 =  *** DEMONSTRACAO PAYGO ***", "713-002 =        VIA: CLIENTE", "713-003 =  DEMOCARD        ************4646", "713-004 =  VALOR FINAL: R$ " + ClienteControlPay.ValorComVirgula(it.ValorCent),
            "714-000 = 5", "715-001 =  *** DEMONSTRACAO PAYGO ***", "715-002 =     VIA: ESTABELECIMENTO", "715-003 =  DEMOCARD        ************4646", "715-004 =  VALOR FINAL: R$ " + ClienteControlPay.ValorComVirgula(it.ValorCent), "715-005 =  ________________",
            "718-000 = DEMO", "729-000 = 2", "730-000 = 1", "731-000 = " + (it.Forma == 22 ? "2" : "1"), "737-000 = 3", "740-000 = 456654*****54646", "999-999 = 0",
        };
        return string.Join("\r\n", linhas);
    }

    private static HttpResponseMessage Json(HttpStatusCode st, string json)
        => new(st) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
