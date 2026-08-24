using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Pdv.Testes;

/// <summary>
/// A edge `pdv-autorizacao` DE MENTIRA: implementa o contrato que o
/// <see cref="Pdv.Nucleo.ClienteAutorizacao"/> consome — `solicitar` e `validar`.
///
/// Existe porque a edge de verdade manda WhatsApp para o dono e para a gerente
/// geral: exercitar o fluxo contra ela seria acender o celular deles a cada
/// `dotnet run`. Aqui os códigos ficam em memória (<see cref="Codigos"/>) e o
/// teste digita como se tivesse recebido a mensagem.
///
/// O que é copiado da edge de propósito, porque é o que o PDV precisa aguentar:
///  · o código NUNCA volta no corpo da resposta;
///  · cada aprovador recebe um código DIFERENTE do mesmo token;
///  · `referencia` amarra o token àquele estorno — token do estorno A não
///    valida o estorno B (motivo `referencia_divergente`, sem gastar tentativa);
///  · `solicitar` é IDEMPOTENTE por (terminal, referencia) enquanto o token está
///    vivo: a segunda tentativa do mesmo estorno leva o MESMO token de volta
///    (`reaproveitado:true`), sem criar linha nova e sem mandar mensagem. É o que
///    impede o TOKEN FANTASMA — a nuvem que demora mais que os 15 s do caixa e
///    manda o WhatsApp depois de ele já ter saído pelo PIN — de custar duas vagas
///    do rate limit e dois avisos por um estorno só. `reenviar:true` (o "não
///    recebi" do operador) pula a idempotência e manda código novo;
///  · 5 chutes errados queimam o token (`bloqueado`), e o certo não passa mais;
///  · uso único (`ja_usado`), expiração (`expirado`) e rate limit (`429`).
///
/// Ver supabase/functions/pdv-autorizacao/index.ts e a migration
/// 20260824170000_pdv_autorizacao_token.sql.
/// </summary>
public sealed class FakeAutorizacao : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, Token> _tokens = new();
    private int _solicitacoes;
    private int _criados;
    private int _mensagens;

    public string Url { get; }

    /// <summary>
    /// Quantas LINHAS de token nasceram — não quantas sobreviveram. É esta a
    /// conta que o rate limit da edge faz (linhas criadas na janela, queimadas ou
    /// não), então é ela que diz se uma vaga do caixa foi gasta.
    /// </summary>
    public int TokensCriados => Volatile.Read(ref _criados);

    /// <summary>
    /// Quantas mensagens de WhatsApp saíram (uma por aprovador, a cada token
    /// novo). É o celular da Ingrid acendendo.
    /// </summary>
    public int Mensagens => Volatile.Read(ref _mensagens);

    /// <summary>Tokens que ainda autorizariam alguma coisa agora.</summary>
    public int TokensVivos => _tokens.Values.Count(t => !t.Usado && !t.Queimado && DateTime.UtcNow <= t.Expira);

    /// <summary>
    /// Id do token vivo daquele estorno. Existe para o teste enxergar o TOKEN
    /// FANTASMA: o que a nuvem criou depois de o caixa já ter desistido de
    /// esperar, e cujo id o PDV nunca chegou a receber.
    /// </summary>
    public string? TokenVivoDe(string terminal, string referencia)
        => _tokens.Values.FirstOrDefault(t => t.Terminal == terminal && t.Referencia == referencia
                                              && !t.Usado && !t.Queimado && DateTime.UtcNow <= t.Expira)?.Id;

    /// <summary>Corpo cru de toda requisição — o teste confere o que o PDV mandou.</summary>
    public ConcurrentBag<string> Chamadas { get; } = new();

    /// <summary>token_id → códigos sorteados, como se tivessem chegado no WhatsApp.</summary>
    public ConcurrentDictionary<string, List<(string Nome, string Scope, string Codigo)>> Codigos { get; } = new();

    /// <summary>Aprovadores cadastrados (whatsapp_authorized_numbers, scope owner|manager).</summary>
    public List<(string Nome, string Scope, string Telefone)> Aprovadores { get; } = new()
    {
        ("Brenno (dono)", "owner", "553195693928"),
        ("Ingrid Borges (gerente geral)", "manager", "553190706284"),
    };

    /// <summary>Segundos de vida do código. Baixe para testar `expirado`.</summary>
    public int ValidadeSegundos { get; set; } = 300;

    public int MaxTentativas { get; set; } = 5;

    /// <summary>Teto de solicitações por terminal antes do 429 (a edge usa 5 em 10 min).</summary>
    public int MaxSolicitacoes { get; set; } = 5;

    /// <summary>Atraso antes de responder — simula a nuvem lenta (o PDV tem 15 s).</summary>
    public int AtrasoMs { get; set; }

    /// <summary>Quando > 0, as próximas N requisições ficam sem resposta (rede caída).</summary>
    public int EngolirProximas;

    /// <summary>Ninguém recebeu o WhatsApp: a edge queima o token e devolve 502.</summary>
    public bool FalhaNoEnvio { get; set; }

    private sealed class Token
    {
        public string Id = "";
        public string Terminal = "";
        public string Referencia = "";
        public long ValorCent;
        public DateTime Expira;
        public int Tentativas;
        public bool Usado;
        public bool Queimado;
        public readonly Dictionary<string, (string Nome, string Scope)> PorCodigo = new();
    }

    public FakeAutorizacao()
    {
        var porta = PortaLivre();
        Url = $"http://127.0.0.1:{porta}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(LacoAsync);
    }

    private static int PortaLivre()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private async Task LacoAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => AtenderAsync(ctx));
        }
    }

    private async Task AtenderAsync(HttpListenerContext ctx)
    {
        try
        {
            string corpo;
            using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                corpo = await sr.ReadToEndAsync();
            Chamadas.Add(corpo);

            if (Interlocked.Decrement(ref EngolirProximas) >= 0)
            {
                await Task.Delay(60_000, _cts.Token);   // não responde: o cliente estoura o tempo
                return;
            }
            if (AtrasoMs > 0) await Task.Delay(AtrasoMs, _cts.Token);

            var b = JsonDocument.Parse(string.IsNullOrWhiteSpace(corpo) ? "{}" : corpo).RootElement;
            var acao = Txt(b, "acao");
            var (status, resposta) = acao switch
            {
                "solicitar" => Solicitar(b),
                "validar"   => Validar(b),
                _           => (400, (object)new { ok = false, motivo = "acao_invalida" }),
            };
            Responder(ctx, status, resposta);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            try { Responder(ctx, 500, new { ok = false, motivo = "erro_interno", detalhe = e.Message }); } catch { }
        }
    }

    private static void Responder(HttpListenerContext ctx, int status, object corpo)
    {
        var dados = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(corpo));
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = dados.Length;
        ctx.Response.OutputStream.Write(dados, 0, dados.Length);
        ctx.Response.Close();
    }

    private static string? Txt(JsonElement e, string nome)
        => e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private (int, object) Solicitar(JsonElement b)
    {
        var terminal = Txt(b, "terminal");
        var referencia = Txt(b, "referencia");
        if (string.IsNullOrWhiteSpace(terminal)) return (400, new { ok = false, motivo = "terminal_obrigatorio" });
        if (string.IsNullOrWhiteSpace(referencia)) return (400, new { ok = false, motivo = "referencia_obrigatoria" });
        if (!b.TryGetProperty("valor_cent", out var vc) || vc.ValueKind != JsonValueKind.Number
            || !vc.TryGetInt64(out var valor) || valor < 0)
            return (400, new { ok = false, motivo = "valor_invalido" });

        // ── O TOKEN FANTASMA ────────────────────────────────────────────────
        // Idempotência por (terminal, referencia), igual à edge: enquanto o token
        // está vivo, pedir de novo o MESMO estorno devolve o MESMO token — sem
        // criar linha (que é o que o rate limit conta), sem mandar mensagem, e
        // com `validade_segundos` = o que SOBRA.
        //
        // Vem ANTES do contador de rate limit de propósito: o caso que interessa
        // é o do caixa que acabou de gastar uma vaga com o fantasma.
        // `reenviar` (o "não recebi" do operador) pula tudo isto e cai no caminho
        // normal — senão o botão vira enfeite.
        var reenviar = b.TryGetProperty("reenviar", out var re) && re.ValueKind == JsonValueKind.True;
        if (!reenviar)
        {
            var vivo = _tokens.Values.FirstOrDefault(t =>
                t.Terminal == terminal && t.Referencia == referencia
                && !t.Usado && !t.Queimado && t.Tentativas == 0 && t.ValorCent == valor
                && t.Expira - DateTime.UtcNow > TimeSpan.FromSeconds(45));
            if (vivo is not null)
            {
                var restam = (int)Math.Max(0, (vivo.Expira - DateTime.UtcNow).TotalSeconds);
                return (200, new
                {
                    ok = true,
                    id = vivo.Id,
                    expira_em = vivo.Expira.ToString("o"),
                    validade_segundos = restam,
                    max_tentativas = MaxTentativas,
                    dry_run = false,
                    terminal_conhecido = true,
                    entregues = Aprovadores.Count,
                    reaproveitado = true,
                    destinatarios = Aprovadores.Select(a => new
                    {
                        nome = a.Nome, scope = a.Scope,
                        telefone = a.Telefone[..4] + new string('*', a.Telefone.Length - 8) + a.Telefone[^4..],
                        enviado = true,
                    }).ToArray(),
                });
            }
        }

        if (Interlocked.Increment(ref _solicitacoes) > MaxSolicitacoes)
            return (429, new { ok = false, motivo = "muitas_solicitacoes" });

        if (FalhaNoEnvio) return (502, new { ok = false, motivo = "falha_no_envio" });

        // Um estorno, um token vivo: pedido novo queima o anterior da mesma referência.
        foreach (var t in _tokens.Values)
            if (t.Terminal == terminal && t.Referencia == referencia && !t.Usado && !t.Queimado)
                t.Queimado = true;

        var tok = new Token
        {
            Id = Guid.NewGuid().ToString(),
            Terminal = terminal!,
            Referencia = referencia!,
            ValorCent = valor,
            Expira = DateTime.UtcNow.AddSeconds(ValidadeSegundos),
        };
        var sorteados = new List<(string, string, string)>();
        var usados = new HashSet<string>();
        var rnd = Random.Shared;
        foreach (var a in Aprovadores)
        {
            string c;
            do { c = rnd.Next(0, 1_000_000).ToString("D6"); } while (!usados.Add(c));
            tok.PorCodigo[c] = (a.Nome, a.Scope);
            sorteados.Add((a.Nome, a.Scope, c));
        }
        _tokens[tok.Id] = tok;
        Codigos[tok.Id] = sorteados;
        Interlocked.Increment(ref _criados);
        Interlocked.Add(ref _mensagens, Aprovadores.Count);

        return (200, new
        {
            ok = true,
            id = tok.Id,
            expira_em = tok.Expira.ToString("o"),
            validade_segundos = ValidadeSegundos,
            max_tentativas = MaxTentativas,
            dry_run = false,
            terminal_conhecido = true,
            entregues = Aprovadores.Count,
            destinatarios = Aprovadores.Select(a => new
            {
                nome = a.Nome, scope = a.Scope,
                telefone = a.Telefone[..4] + new string('*', a.Telefone.Length - 8) + a.Telefone[^4..],
                enviado = true,
            }).ToArray(),
        });
    }

    private (int, object) Validar(JsonElement b)
    {
        var id = Txt(b, "id");
        if (string.IsNullOrWhiteSpace(id) || id!.Length != 36) return (400, new { ok = false, motivo = "id_invalido" });

        var codigo = new string((Txt(b, "codigo") ?? "").Where(char.IsDigit).ToArray());
        // Dedo gordo não gasta tentativa (igual à edge).
        if (codigo.Length != 6) return (200, new { ok = false, motivo = "codigo_invalido" });

        if (!_tokens.TryGetValue(id, out var t)) return (200, new { ok = false, motivo = "nao_encontrado" });

        lock (t)
        {
            if (t.Usado)    return (200, new { ok = false, motivo = "ja_usado" });
            if (t.Queimado) return (200, new { ok = false, motivo = "bloqueado" });
            if (DateTime.UtcNow > t.Expira) return (200, new { ok = false, motivo = "expirado" });

            var referencia = Txt(b, "referencia");
            // Referência errada não é chute de código: não gasta tentativa.
            if (referencia is not null && referencia != t.Referencia)
                return (200, new { ok = false, motivo = "referencia_divergente" });

            if (!t.PorCodigo.TryGetValue(codigo, out var quem))
            {
                t.Tentativas++;
                if (t.Tentativas >= MaxTentativas) t.Queimado = true;
                // Resposta igual queimando ou não: o placar não vaza.
                return (200, new { ok = false, motivo = "codigo_invalido" });
            }

            t.Usado = true;
            var aprovadoPor = $"{quem.Nome} · {quem.Scope}";
            return (200, new
            {
                ok = true,
                aprovado_por = aprovadoPor,
                aprovador_scope = quem.Scope,
                referencia = t.Referencia,
                valor_cent = t.ValorCent,
                tipo = "estorno",
                terminal = t.Terminal,
            });
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        _cts.Dispose();
    }
}
