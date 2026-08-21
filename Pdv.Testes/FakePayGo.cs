using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// PayGo Windows de MENTIRA: observa `Req\intpos.001` numa pasta temporária e responde com
/// `Resp\intpos.sts` + `Resp\intpos.001` exatamente como o PayGo real (arquivos verbatim do
/// kit de integração, Passo 19). Roteiriza aprovação, recusa, valor divergente, 729=1, PayGo
/// mudo depois do ack, sem ack nenhum, resposta atrasada e antivírus segurando o arquivo.
///
/// Também FISCALIZA a automação: se algum dia vir `intpos.001` pela metade (sem o
/// `999-999 = 0` no fim), marca <see cref="ViuArquivoParcial"/> — é a prova de que o
/// `.tmp` + rename foi "simplificado".
/// </summary>
public sealed class FakePayGo : IDisposable
{
    public enum Desfecho
    {
        Aprovar,
        Recusar,
        AprovarValorDivergente,
        /// <summary>Aprova com `729-000 = 1` (rede que não pede confirmação).</summary>
        AprovarSemConfirmacao,
        /// <summary>Manda o .sts e some — nunca grava o .001.</summary>
        Sumir,
        /// <summary>PayGo desligado: nem o .sts.</summary>
        SemSts,
        /// <summary>Roteiro P31–34: NEGA a venda trazendo o 027 de uma transação pendente na rede.</summary>
        NegarComPendencia,
        /// <summary>Resposta sem 009-000 (arquivo inconsistente).</summary>
        Inconsistente,
        /// <summary>Cliente apertou Esc no pinpad/QR: 009≠0 com 030 "OPERACAO CANCELADA" (roteiro P5/P16/P52).</summary>
        CancelarNoPinpad,
    }

    /// <summary>Grava o .001 AOS POUCOS (sem rename): metade, pausa, resto — PayGo que não grava atômico.</summary>
    public int EscreverAosPoucosMs;

    /// <summary>false = PayGo que NÃO ecoa o 001-000 (responde com identificação própria, como o Passo 19 do kit sugere).</summary>
    public bool EcoIdentificacao = true;

    /// <summary>Código de controle que <see cref="Desfecho.NegarComPendencia"/> devolve.</summary>
    public string PendenciaControle = "CTRL-PENDENTE";

    /// <summary>Pausado = PayGo fechado: o arquivo em Req fica lá, intocado.</summary>
    public bool Pausado;

    public string Pasta { get; }
    public string Req => Path.Combine(Pasta, "Req");
    public string Resp => Path.Combine(Pasta, "Resp");

    /// <summary>Desfecho de cada CRT/CNC/ADM, na ordem. Fila vazia = aprova.</summary>
    public ConcurrentQueue<Desfecho> Roteiro { get; } = new();

    /// <summary>Antes de gravar o .001 (simula o cliente demorando no pinpad).</summary>
    public int AtrasoRespostaMs;

    /// <summary>Grava o .001 e SEGURA o arquivo aberto sem compartilhamento por este tempo (antivírus).</summary>
    public int TravarRespostaMs;

    /// <summary>Ignora tudo — PayGo fechado.</summary>
    public bool SemStsTudo;

    public bool ViuArquivoParcial { get; private set; }

    private readonly List<Dictionary<string, string>> _recebidos = new();
    private readonly object _trava = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _laco;
    private static int _nsu = 721554;

    public FakePayGo() : this(null) { }

    /// <summary>
    /// Pasta FIXA (ex.: `C:\PAYGO\SIM`) para rodar como "PayGo de mentira" ao lado do PDV de
    /// verdade — ambiente de teste da tela sem credencial da PayGo. Nesse modo a pasta não é
    /// apagada no Dispose. Null = pasta temporária (bateria).
    /// </summary>
    public FakePayGo(string? pasta)
    {
        _apagarNoDispose = pasta is null;
        Pasta = pasta ?? Path.Combine(Path.GetTempPath(), "paygo-fake-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Req);
        Directory.CreateDirectory(Resp);
        _laco = Task.Run(LacoAsync);
    }

    private readonly bool _apagarNoDispose;

    /// <summary>Chamado a cada comando recebido (modo simulador: log no console).</summary>
    public Action<Dictionary<string, string>>? Recebeu;

    /// <summary>Tudo que a automação mandou, em ordem (campo → valor).</summary>
    public IReadOnlyList<Dictionary<string, string>> Recebidos
    {
        get { lock (_trava) return _recebidos.ToList(); }
    }

    public IReadOnlyList<Dictionary<string, string>> Comandos(string cmd)
        => Recebidos.Where(r => r.GetValueOrDefault("000-000") == cmd).ToList();

    public int Quantos(string cmd) => Comandos(cmd).Count;

    public bool Esperar(Func<bool> cond, int ms = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms) return false;
            Thread.Sleep(10);
        }
        return true;
    }

    /// <summary>
    /// Planta uma resposta APROVADA "esquecida" em `Resp\intpos.001` — o que sobra quando a
    /// resposta chega depois de o PDV desistir, ou o caixa morre entre o .001 e o CNF.
    /// </summary>
    public void PlantarRespostaOrfa(string id, string controle, long valorCent = 100, bool semConfirmacao = false)
    {
        var req = new Dictionary<string, string> { ["003-000"] = valorCent.ToString(CultureInfo.InvariantCulture), ["731-000"] = "1" };
        var txt = Resposta("CRT", id, req, semConfirmacao ? Desfecho.AprovarSemConfirmacao : Desfecho.Aprovar, controle);
        Escrever(Path.Combine(Resp, "intpos.001"), txt);
    }

    // ------------------------------------------------------------------ laço

    private async Task LacoAsync()
    {
        var req = Path.Combine(Req, "intpos.001");
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!Pausado && File.Exists(req))
                {
                    string? texto = null;
                    try { texto = File.ReadAllText(req, Encoding.ASCII); } catch (IOException) { }
                    if (texto is null) { await Task.Delay(5); continue; }
                    if (!texto.EndsWith("999-999 = 0\r\n", StringComparison.Ordinal))
                    {
                        ViuArquivoParcial = true;
                        await Task.Delay(5);
                        continue;
                    }
                    var campos = ArquivoIntpos.Analisar(texto);
                    try { File.Delete(req); } catch { }
                    lock (_trava) _recebidos.Add(campos);
                    try { Recebeu?.Invoke(campos); } catch { }
                    await TratarAsync(campos);
                }
            }
            catch { /* o fake nunca derruba a bateria */ }
            await Task.Delay(10);
        }
    }

    private async Task TratarAsync(Dictionary<string, string> req)
    {
        if (SemStsTudo) return;
        var cmd = req.GetValueOrDefault("000-000") ?? "";
        var id = EcoIdentificacao ? (req.GetValueOrDefault("001-000") ?? "0") : "7777777777";

        if (cmd is "CRT" or "CNC" or "ADM")
        {
            if (!Roteiro.TryDequeue(out var d)) d = Desfecho.Aprovar;
            if (d == Desfecho.SemSts) return;
            Sts(cmd, id);
            if (d == Desfecho.Sumir) return;
            if (AtrasoRespostaMs > 0) await Task.Delay(AtrasoRespostaMs);

            var txt = Resposta(cmd, id, req, d, d == Desfecho.NegarComPendencia ? PendenciaControle : null);
            var caminho = Path.Combine(Resp, "intpos.001");
            if (EscreverAosPoucosMs > 0)
            {
                var meio = txt.Length / 2;
                File.WriteAllText(caminho, txt[..meio], Encoding.ASCII);
                await Task.Delay(EscreverAosPoucosMs);
                File.AppendAllText(caminho, txt[meio..], Encoding.ASCII);
            }
            else if (TravarRespostaMs > 0)
            {
                // Grava JÁ segurando o arquivo: é o que o antivírus faz — o PDV vê o arquivo
                // existir e leva "acesso negado" até ele ser solto.
                using var fs = new FileStream(caminho, FileMode.Create, FileAccess.Write, FileShare.None);
                var b = Encoding.ASCII.GetBytes(txt);
                fs.Write(b, 0, b.Length);
                fs.Flush(true);
                await Task.Delay(TravarRespostaMs);
            }
            else Escrever(caminho, txt);
        }
        else Sts(cmd, id);
    }

    private void Sts(string cmd, string id)
        => Escrever(Path.Combine(Resp, "intpos.sts"), $"000-000 = {cmd}\r\n001-000 = {id}\r\n999-999 = 0\r\n");

    private static void Escrever(string caminho, string texto)
    {
        var tmp = caminho + ".tmp";
        File.WriteAllText(tmp, texto, Encoding.ASCII);
        File.Move(tmp, caminho, overwrite: true);
    }

    // ------------------------------------------------------------------ resposta (Passo 19 verbatim)

    /// <summary>Monta a resposta do PayGo. Os comprovantes são os do kit (sandbox), com aspas como no arquivo real.</summary>
    public static string Resposta(string cmd, string id, IReadOnlyDictionary<string, string> req, Desfecho d, string? controle = null)
    {
        var c = new List<KeyValuePair<string, string>>();
        void Add(string k, string v) => c.Add(new KeyValuePair<string, string>(k, v));

        Add("000-000", cmd);
        Add("001-000", id);
        if (req.TryGetValue("002-000", out var doc)) Add("002-000", doc);
        var valor = long.TryParse(req.GetValueOrDefault("003-000"), out var v0) ? v0 : 0;
        if (d == Desfecho.AprovarValorDivergente) valor += 100;
        Add("003-000", valor.ToString(CultureInfo.InvariantCulture));
        Add("004-000", "0");

        if (d == Desfecho.Inconsistente)
        {
            Add("030-000", "RESPOSTA SEM STATUS");
            return ArquivoIntpos.Serializar(c);
        }

        if (d is Desfecho.Recusar or Desfecho.CancelarNoPinpad)
        {
            Add("009-000", d == Desfecho.Recusar ? "7" : "99");
            Add("010-000", req.GetValueOrDefault("010-000") ?? "DEMO");
            Add("030-000", d == Desfecho.Recusar ? "TRANSACAO NAO AUTORIZADA" : "OPERACAO CANCELADA");
            Add("729-000", "1");
            Add("730-000", cmd == "CNC" ? "51" : "1");
            return ArquivoIntpos.Serializar(c);
        }

        if (d == Desfecho.NegarComPendencia)
        {
            // Como o roteiro descreve: venda negada, trazendo a transação que a rede segura.
            Add("009-000", "7");
            Add("010-000", req.GetValueOrDefault("010-000") ?? "DEMO");
            Add("012-000", "999001");
            Add("027-000", controle ?? "CTRL-PENDENTE");
            Add("030-000", "TRANSACAO PENDENTE - CONFIRME OU DESFACA A ANTERIOR");
            Add("729-000", "1");
            Add("730-000", "1");
            return ArquivoIntpos.Serializar(c);
        }

        var nsu = Interlocked.Increment(ref _nsu).ToString(CultureInfo.InvariantCulture);
        var tipo = req.GetValueOrDefault("731-000") ?? "1";
        if (tipo == "0") tipo = "1";
        var debito = tipo == "2";

        Add("009-000", "0");
        Add("010-000", req.GetValueOrDefault("010-000") ?? "DEMO");
        Add("011-000", debito ? "20" : "10");
        Add("012-000", nsu);
        Add("013-000", "543733");
        Add("015-000", "3103120343");
        Add("016-000", "3103120343");
        Add("022-000", "31032025");
        Add("023-000", "120343");
        Add("027-000", controle ?? ("310320251203" + nsu));
        Add("028-000", "0");
        Add("030-000", "TRANSACAO AUTORIZADA");
        Add("040-000", debito ? "VISA ELECTRO" : "VISA");
        Add("710-000", "5");
        Add("711-001", "\" *** PAYGO - AMBIENTE SANDBOX *** \"");
        Add("711-002", "\"--------------------------------------\"");
        Add("711-003", "\"86132 EC:0000001380 REF:0000003801\"");
        Add("711-004", "\" \"");
        Add("711-005", "\" TRANSACAO TESTE SEM VALOR FINANCEIRO! \"");
        Add("712-000", "14");
        Add("713-001", "\" *** PAYGO - AMBIENTE SANDBOX *** \"");
        Add("713-002", "\"VIA CLIENTE 31/MAR/25 12:03\"");
        Add("713-003", "\"SETIS*SETIS\"");
        Add("713-004", "\"CNPJ:03.361.770/0001-58 PDC:86132\"");
        Add("713-005", "\"REF:3801 EC:1380\"");
        Add("713-006", debito ? "\"C-476173******0010 VISA DEBITO\"" : "\"C-489391******0008 VISA CREDITO\"");
        Add("713-007", "\"AID:A0000000031010\"");
        Add("713-008", debito ? "\" VENDA DEBITO \"" : "\" VENDA CREDITO A VISTA \"");
        Add("713-009", "\"VALOR FINAL: R$ " + (valor / 100m).ToString("N2", new CultureInfo("pt-BR")) + "\"");
        Add("713-010", "\" \"");
        Add("713-011", "\"--------------------------------------\"");
        Add("713-012", "\"86132 EC:0000001380 REF:0000003801\"");
        Add("713-013", "\" \"");
        Add("713-014", "\" TRANSACAO TESTE SEM VALOR FINANCEIRO! \"");
        Add("714-000", "16");
        Add("715-001", "\" *** PAYGO - AMBIENTE SANDBOX *** \"");
        Add("715-002", "\"VIA ESTABELECIMENTO 31/MAR/25 12:03\"");
        Add("715-003", "\"SETIS*SETIS\"");
        Add("715-004", "\"CNPJ:03.361.770/0001-58 PDC:86132\"");
        Add("715-005", "\"REF:3801 EC:1380\"");
        Add("715-006", debito ? "\"C-476173******0010 VISA ELECTRON\"" : "\"C-489391******0008 VISA CREDITO\"");
        Add("715-007", "\"AID:A0000000031010\"");
        Add("715-008", "\"ARQC:2027E71B1A9D9755\"");
        Add("715-009", debito ? "\" VENDA DEBITO \"" : "\" VENDA CREDITO A VISTA \"");
        Add("715-010", "\"VALOR FINAL: R$ " + (valor / 100m).ToString("N2", new CultureInfo("pt-BR")) + "\"");
        Add("715-011", debito ? "\" ASSINATURA \"" : "\" TRANSACAO AUTORIZADA COM SENHA \"");
        Add("715-012", "\" \"");
        Add("715-013", "\"--------------------------------------\"");
        Add("715-014", "\"86132 EC:0000001380 REF:0000003801\"");
        Add("715-015", "\" \"");
        Add("715-016", "\" TRANSACAO TESTE SEM VALOR FINANCEIRO! \"");
        Add("718-000", "86132");
        Add("719-000", "03361770000158");
        Add("729-000", d == Desfecho.AprovarSemConfirmacao ? "1" : "2");
        Add("730-000", cmd == "CNC" ? "51" : "1");
        Add("731-000", tipo);
        Add("732-000", req.GetValueOrDefault("732-000") ?? "1");
        if (req.TryGetValue("018-000", out var parc)) Add("018-000", parc);
        Add("737-000", "3");
        Add("739-000", "100");
        Add("740-000", debito ? "4***********0010" : "4***********0008");
        Add("747-000", "0230");
        Add("748-000", debito ? "VISA ELECTRON" : "VISA CREDITO");
        return ArquivoIntpos.Serializar(c);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _laco.Wait(1000); } catch { }
        if (!_apagarNoDispose) return;
        for (var i = 0; i < 10; i++)
        {
            try { if (Directory.Exists(Pasta)) Directory.Delete(Pasta, true); return; }
            catch { Thread.Sleep(50); }
        }
    }
}
