using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// AUTORIZAÇÃO DE ESTORNO PELO AUTENTICADOR DO DONO (TOTP).
///
/// O estorno é o único ponto do PDV onde dinheiro VOLTA para o cliente sem que
/// nada tenha sido vendido. Já foi PIN do supervisor (segredo no banco do próprio
/// caixa), já foi código por mensagem com o PIN como saída (segredo copiável e
/// saída que um funcionário mal-intencionado usava). Agora é UM caminho só: o
/// código de 6 dígitos do Google Authenticator do dono, conferido na nuvem pela
/// RPC `pdv_autorizacao_totp`. O segredo vive só no servidor; o caixa nunca vê,
/// nunca guarda, nunca loga.
///
/// O que esta suíte protege, em ordem de estrago:
///  · NENHUM caminho de estorno ou cancelamento passa sem Via=Totp (garantido
///    pela FONTE dos arquivos que entram no .exe, não só pela máquina de estados);
///  · não existe saída pelo PIN, nem "sem aprovação remota": sem internet, sem
///    sessão ou sem autenticador configurado, o estorno simplesmente não sai;
///  · o código digitado nunca aparece em log nem em auditoria (mascarado);
///  · a chamada vai com o bearer da SESSÃO do terminal, não com a chave pública;
///  · o operador nunca fica preso: 3 códigos inválidos e a tela desiste.
///
/// A nuvem aqui é <see cref="FakeTotp"/>, que fala TOTP de verdade (vetores da
/// RFC 6238 conferidos em FK-*), porque a RPC real ainda não existe neste
/// ambiente e o segredo do dono não pode viver em máquina de teste.
/// </summary>
public static class TestesAutorizacao
{
    /// <summary>Tela de mentira: o que precisaria de janela vira delegate roteirizável.</summary>
    private sealed class TelaFalsa : ITelaAutorizacao
    {
        /// <summary>aviso → código digitado (null = o operador cancelou).</summary>
        public Func<string?, string?>? AoPedirCodigo;

        public int VezesPediuCodigo, VezesAguardou;
        public readonly List<string> Esperas = new();
        public readonly List<string?> Avisos = new();

        private sealed class Nada : IDisposable { public void Dispose() { } }

        public IDisposable Aguardando(string mensagem)
        {
            VezesAguardou++;
            Esperas.Add(mensagem);
            return new Nada();
        }

        public readonly List<string> Niveis = new();

        public Task<string?> PedirCodigoAsync(string? aviso, string nivel)
        {
            VezesPediuCodigo++;
            Avisos.Add(aviso);
            Niveis.Add(nivel);
            return Task.FromResult(AoPedirCodigo?.Invoke(aviso));
        }
    }

    /// <summary>
    /// O DISPATCHER DO WPF DE POBRE: uma thread só, com fila e
    /// <see cref="SynchronizationContext"/>. A regra que o WPF impõe é só esta: a
    /// continuação tem que voltar para a thread que entrou. Um contexto de thread
    /// única a reproduz inteira, e roda headless.
    /// </summary>
    private sealed class ThreadDeTela : IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Cb, object? Estado)> _fila = new();
        private readonly ManualResetEventSlim _pronta = new(false);
        private readonly Thread _thread;

        public int Id;

        public ThreadDeTela()
        {
            _thread = new Thread(Laco) { IsBackground = true, Name = "tela-falsa" };
            _thread.Start();
            _pronta.Wait();
        }

        private void Laco()
        {
            Id = Environment.CurrentManagedThreadId;
            SynchronizationContext.SetSynchronizationContext(new Contexto(_fila));
            _pronta.Set();
            foreach (var (cb, estado) in _fila.GetConsumingEnumerable()) cb(estado);
        }

        private sealed class Contexto : SynchronizationContext
        {
            private readonly BlockingCollection<(SendOrPostCallback, object?)> _fila;
            public Contexto(BlockingCollection<(SendOrPostCallback, object?)> fila) => _fila = fila;
            public override void Post(SendOrPostCallback d, object? estado)
            {
                try { _fila.Add((d, estado)); } catch (InvalidOperationException) { /* já encerrada */ }
            }
        }

        public Task<T> ExecutarAsync<T>(Func<Task<T>> trabalho)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Correr()
            {
                try { tcs.SetResult(await trabalho()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }
            _fila.Add((_ => { _ = Correr(); }, null));
            return tcs.Task;
        }

        public void Dispose()
        {
            _fila.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            _pronta.Dispose();
        }
    }

    /// <summary>Tela que só anota EM QUE THREAD cada coisa aconteceu, inclusive o Dispose da espera.</summary>
    private sealed class TelaQueAnotaThread : ITelaAutorizacao
    {
        private readonly int _ui;
        public TelaQueAnotaThread(int ui) => _ui = ui;

        public Func<string?, string?>? AoPedirCodigo;
        public int VezesPediuCodigo, VezesAguardou, VezesFechouEspera;
        public readonly List<string> ForaDaThreadDaTela = new();

        private void Anotar(string onde)
        {
            var atual = Environment.CurrentManagedThreadId;
            if (atual != _ui)
                lock (ForaDaThreadDaTela)
                    ForaDaThreadDaTela.Add($"{onde} caiu na thread {atual} (tela = {_ui})");
        }

        private sealed class Aviso : IDisposable
        {
            private readonly Action _aoFechar;
            public Aviso(Action aoFechar) => _aoFechar = aoFechar;
            public void Dispose() => _aoFechar();
        }

        public IDisposable Aguardando(string mensagem)
        {
            VezesAguardou++;
            Anotar("Aguardando");
            return new Aviso(() => { VezesFechouEspera++; Anotar("Dispose da espera"); });
        }

        public Task<string?> PedirCodigoAsync(string? aviso, string nivel)
        {
            VezesPediuCodigo++;
            Anotar("PedirCodigoAsync");
            return Task.FromResult(AoPedirCodigo?.Invoke(aviso));
        }
    }

    private const string Terminal = "Caixa Savassi 1";
    private const string Loja = "American Day Savassi";
    private const string TerminalUuid = "9a1c0c2e-0000-4000-8000-terminal0001";

    private static PedidoAutorizacao PedidoDe(string tefId, string nsu, long centavos, long venda)
        => new(Terminal, Autorizacao.Referencia(tefId, nsu, centavos, venda), centavos,
               Loja: Loja, Operador: "Bia", Venda: venda.ToString(), Forma: "credito", Nsu: nsu);

    private static ClienteAutorizacao ClienteDe(FakeTotp fake, string? token, TimeSpan? tempo = null)
        => new(_ => Task.FromResult(token), fake.Url, fake.AnonKey, tempo ?? TimeSpan.FromSeconds(5),
               () => TerminalUuid);

    private static string Txt(JsonElement e, string nome)
        => e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Num(JsonElement e, string nome)
        => e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : -1;

    /// <summary>Raiz do repositório a partir do binário de teste (procura o Pdv.csproj).</summary>
    private static string? Raiz()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Pdv.csproj"))) return d.FullName;
        return null;
    }

    private static string? Fonte(params string[] partes)
    {
        var raiz = Raiz();
        if (raiz is null) return null;
        var alvo = Path.Combine(new[] { raiz }.Concat(partes).ToArray());
        return File.Exists(alvo) ? File.ReadAllText(alvo) : null;
    }

    /// <summary>Só o que ENTRA no .exe: raiz + Telas + Pdv.Nucleo (sem Pdv.Testes nem Pdv.Instalador).</summary>
    private static string[] FontesDoExe()
    {
        var raiz = Raiz();
        if (raiz is null) return Array.Empty<string>();
        return Directory.EnumerateFiles(raiz, "*.cs", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(Path.Combine(raiz, "Telas"), "*.cs", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(raiz, "Pdv.Nucleo"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToArray();
    }

    private static string Trecho(string todo, string de, string ate)
    {
        var i = todo.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var f = todo.IndexOf(ate, i, StringComparison.Ordinal);
        return f < 0 ? "" : todo[i..f];
    }

    private static bool Ordem(string corpo, string primeiro, string depois)
    {
        var a = corpo.IndexOf(primeiro, StringComparison.Ordinal);
        var b = corpo.IndexOf(depois, StringComparison.Ordinal);
        return a >= 0 && b > a;
    }

    /// <summary>Papel declarado dentro do JWT (o "role" do payload), sem validar assinatura.</summary>
    private static string? PapelDoJwt(string jwt)
    {
        try
        {
            var partes = jwt.Split('.');
            if (partes.Length < 2) return null;
            var p = partes[1].Replace('-', '+').Replace('_', '/');
            p += new string('=', (4 - p.Length % 4) % 4);
            var payload = JsonDocument.Parse(Convert.FromBase64String(p)).RootElement;
            return payload.TryGetProperty("role", out var r) ? r.GetString() : null;
        }
        catch { return null; }
    }

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"autorizacao_teste_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir(arquivo);
            var op = new Operador("op-aut", "Bia", "operador");
            Operadores.Salvar(cx, op.Id, op.Nome, "1234", "operador");

            using var fake = new FakeTotp();
            var segredoRfc = System.Text.Encoding.ASCII.GetBytes("12345678901234567890");

            // ── 0. O FAKE FALA TOTP DE VERDADE (vetores da RFC 6238, SHA1, 6 dígitos) ──
            // Os mesmos quatro vetores são obrigatórios na suíte do banco (ERP): se
            // os dois lados batem com a RFC, batem entre si.
            checar(FakeTotp.Codigo(segredoRfc, 59) == "287082", "FK-1 RFC 6238: T=59 → 287082");
            checar(FakeTotp.Codigo(segredoRfc, 1111111109) == "081804", "FK-2 RFC 6238: T=1111111109 → 081804");
            checar(FakeTotp.Codigo(segredoRfc, 1234567890) == "005924", "FK-3 RFC 6238: T=1234567890 → 005924");
            checar(FakeTotp.Codigo(segredoRfc, 2000000000) == "279037", "FK-4 RFC 6238: T=2000000000 → 279037");

            // Relógio FIXO no fake: a suíte não pode depender de virar o passo de 30 s
            // no meio de um cenário. 1234567890 é o vetor 3 da RFC (T=41152263).
            var relogio = DateTimeOffset.FromUnixTimeSeconds(1234567890);
            fake.Relogio = () => relogio;
            long Passo() => relogio.ToUnixTimeSeconds() / 30;
            // Um contador só vale UMA vez (replay): entre cenários que precisam de
            // código aceito, o relógio anda um passo, como o celular do dono andaria.
            void ProximoPasso() => relogio = relogio.AddSeconds(30);

            // ── 1. CONTRATO DO CLIENTE COM A RPC ────────────────────────────
            var cli = ClienteDe(fake, fake.Token);
            var diagnostico = new List<string>();
            cli.Diagnostico = l => { lock (diagnostico) diagnostico.Add(l); };
            var codigosDigitados = new List<string>();

            var pedido = PedidoDe("paygo-1", "000123", 1200, 142);
            var codigo1 = fake.CodigoAgora();
            codigosDigitados.Add(codigo1);
            var v1 = await cli.ValidarTotpAsync(codigo1, pedido.Referencia, pedido.Tipo,
                Autorizacao.Detalhe(pedido), "dono", CancellationToken.None);
            checar(v1.Ok && v1.Definitiva && v1.Id is { Length: > 0 } && (v1.Autorizador ?? "").Contains("Brenno"),
                "CT-1 código do autenticador do dono autoriza: a RPC devolve id do registro e QUEM é o dono"
                + $" (ok={v1.Ok} motivo={v1.Motivo})");

            fake.Chamadas.TryPeek(out var primeira);
            checar(primeira is not null && primeira.Caminho == FakeTotp.Caminho,
                "CT-2 a chamada é POST /rest/v1/rpc/pdv_autorizacao_totp (a RPC do contrato)");
            checar(primeira is not null && primeira.Authorization == "Bearer " + fake.Token
                   && primeira.ApiKey == fake.AnonKey,
                "CT-3 vai com o bearer da SESSÃO do terminal (usuário authenticated) e a chave pública no apikey");
            var jc = JsonDocument.Parse(primeira?.Corpo ?? "{}").RootElement;
            var jd = jc.TryGetProperty("_detalhe", out var det) ? det : default;
            checar(Txt(jc, "_codigo") == codigo1
                   && Txt(jc, "_referencia") == pedido.Referencia
                   && Txt(jc, "_tipo") == "estorno"
                   && Txt(jc, "_terminal_uuid") == TerminalUuid
                   && jd.ValueKind == JsonValueKind.Object
                   && Txt(jd, "venda") == "142" && Txt(jd, "nsu") == "000123" && Num(jd, "valor_cent") == 1200
                   && Txt(jd, "operador") == "Bia" && Txt(jd, "loja") == Loja,
                "CT-4 o corpo leva _codigo, _referencia, _tipo, _terminal_uuid e _detalhe (venda, NSU, valor, operador, loja)");
            checar(!jc.ToString().Contains("_agora", StringComparison.Ordinal),
                "CT-5 o caixa NÃO manda _agora (o relógio que vale é o do servidor)");

            var errado = (int.Parse(codigo1) + 1) % 1_000_000;
            var codigoErrado = errado.ToString("D6");
            codigosDigitados.Add(codigoErrado);
            var v2 = await cli.ValidarTotpAsync(codigoErrado, pedido.Referencia, pedido.Tipo, null, "dono", CancellationToken.None);
            checar(!v2.Ok && v2.Definitiva && v2.Motivo == "codigo invalido" && v2.Id is null && v2.Autorizador is null,
                "CT-6 código errado volta como recusa DEFINITIVA, sem id e sem nome de dono nenhum");

            var morta = new ClienteAutorizacao(_ => Task.FromResult<string?>("t"), "http://127.0.0.1:9", "k",
                TimeSpan.FromSeconds(2));
            var v3 = await morta.ValidarTotpAsync("000000", pedido.Referencia, pedido.Tipo, null, "dono", CancellationToken.None);
            checar(!v3.Ok && !v3.Definitiva,
                "CT-7 nuvem fora do ar não é veredito: volta 'não sei' (e o estorno não sai)");

            checar(ClienteAutorizacao.TempoPadrao == TimeSpan.FromSeconds(10),
                "CT-8 o tempo padrão do cliente é curto (10 s): o cliente está no balcão");

            fake.AtrasoMs = 4000;
            var lento = ClienteDe(fake, fake.Token, TimeSpan.FromMilliseconds(700));
            var cron = Stopwatch.StartNew();
            var v4 = await lento.ValidarTotpAsync("000000", pedido.Referencia, pedido.Tipo, null, "dono", CancellationToken.None);
            cron.Stop();
            fake.AtrasoMs = 0;
            checar(!v4.Ok && !v4.Definitiva && cron.ElapsedMilliseconds < 2500,
                "CT-9 nuvem lenta não segura o caixa: o cliente desiste no tempo configurado");

            var antesSemSessao = fake.Chamadas.Count;
            var semSessao = ClienteDe(fake, null);
            var v5 = await semSessao.ValidarTotpAsync("000000", pedido.Referencia, pedido.Tipo, null, "dono", CancellationToken.None);
            checar(!v5.Ok && v5.Definitiva && v5.Motivo == ClienteAutorizacao.MotivoSemSessao
                   && fake.Chamadas.Count == antesSemSessao,
                "CT-10 terminal sem sessão na nuvem: recusa definitiva SEM ir à rede (não manda código com a chave pública)");

            var bearerErrado = ClienteDe(fake, "outro-token");
            var v6 = await bearerErrado.ValidarTotpAsync("000000", pedido.Referencia, pedido.Tipo, null, "dono", CancellationToken.None);
            checar(!v6.Ok && v6.Definitiva && (v6.Motivo ?? "").Contains("401"),
                "CT-11 sessão recusada pela nuvem (401 do PostgREST) é veredito definitivo, com o HTTP no motivo");

            fake.EngolirProximas = 1;
            var v7 = await ClienteDe(fake, fake.Token, TimeSpan.FromSeconds(3))
                .ValidarTotpAsync("000000", pedido.Referencia, pedido.Tipo, null, "dono", CancellationToken.None);
            checar(!v7.Ok && !v7.Definitiva,
                "CT-12 conexão que cai no meio (sem resposta) também é 'não sei', nunca exceção");

            // O CÓDIGO NUNCA VAI PARA LOG EM CLARO. O cliente escreve uma linha de
            // diagnóstico por tentativa (status, motivo): o código aparece só mascarado.
            var linhas = diagnostico.ToArray();
            checar(linhas.Length >= 2
                   && linhas.All(l => codigosDigitados.All(c => !l.Contains(c, StringComparison.Ordinal)))
                   && linhas.Any(l => l.Contains(Autorizacao.Mascarar(codigo1), StringComparison.Ordinal)),
                "CT-13 o diagnóstico do cliente registra a tentativa com o código MASCARADO, nunca em claro"
                + $" ({linhas.Length} linha(s))");
            checar(Autorizacao.Mascarar("287082") == "******" && Autorizacao.Mascarar("") == ""
                   && Autorizacao.Mascarar(null) == "",
                "CT-14 a máscara não deixa dígito nenhum (só o tamanho)");

            // ── 2. A MÁQUINA DE ESTADOS ─────────────────────────────────────
            fake.ZerarBaldes();
            fake.UltimoContador = 0;

            // AT-1 caminho feliz
            var tela = new TelaFalsa { AoPedirCodigo = _ => fake.CodigoAgora() };
            var pedidoA = PedidoDe("paygo-A", "000200", 1200, 200);
            var dOk = await Autorizacao.ResolverAsync(cli, pedidoA, tela, CancellationToken.None);
            checar(dOk.Via == ViaAutorizacao.Totp && dOk.Autorizado && !dOk.SemAprovacaoRemota
                   && (dOk.AprovadoPor ?? "").Contains("Brenno") && dOk.TokenId is { Length: > 8 }
                   && tela.VezesPediuCodigo == 1 && tela.Avisos[0] is null,
                "AT-1 código certo autoriza com Via=Totp e a auditoria sabe QUEM é o dono"
                + $" (via={dOk.Via} motivo={dOk.Motivo})");
            checar(tela.Esperas.Count == 1 && tela.Esperas[0].Contains("Conferindo", StringComparison.Ordinal),
                "AT-2 o caixa vê 'Conferindo o código' enquanto a nuvem confere (e nada de 'enviando pedido')");
            checar(dOk.Autorizador == "totp:" + dOk.TokenId![..8],
                "AT-3 a coluna `autorizador` da auditoria leva a marca totp:<8 chars do registro>");

            // AT-4 replay: o MESMO código, de novo, não vale (o contador só vale uma vez)
            var codigoUsado = fake.CodigoAgora();
            var vezesReplay = 0;
            tela = new TelaFalsa { AoPedirCodigo = _ => ++vezesReplay == 1 ? codigoUsado : null };
            var dReplay = await Autorizacao.ResolverAsync(cli, PedidoDe("paygo-R", "000201", 500, 201), tela,
                CancellationToken.None);
            fake.Log.TryPeek(out _);
            var ultimaTentativa = fake.Log.LastOrDefault();
            checar(!dReplay.Autorizado && dReplay.Via == ViaAutorizacao.Recusada && tela.VezesPediuCodigo == 2
                   && (tela.Avisos[1] ?? "").Contains("inválido", StringComparison.Ordinal)
                   && ultimaTentativa is { Ok: false, Motivo: "codigo invalido" },
                "AT-4 o código que acabou de autorizar NÃO autoriza outro estorno (replay recusado como inválido)");

            // AT-5 três inválidos: a tela avisa, deixa tentar, e na terceira desiste
            fake.ZerarBaldes();
            var chamadasAntes = fake.Chamadas.Count;
            tela = new TelaFalsa { AoPedirCodigo = _ => "000000" };
            var dTres = await Autorizacao.ResolverAsync(cli, PedidoDe("paygo-3", "000202", 800, 202), tela,
                CancellationToken.None);
            checar(!dTres.Autorizado && dTres.Via == ViaAutorizacao.Recusada && tela.VezesPediuCodigo == 3
                   && fake.Chamadas.Count == chamadasAntes + 3 && !dTres.Avisado
                   && dTres.Motivo.Contains("3 vezes", StringComparison.Ordinal)
                   && dTres.Motivo.EndsWith("Estorno não autorizado.", StringComparison.Ordinal),
                "AT-5 três códigos inválidos: 3 pedidos de código, 3 idas à nuvem, e o estorno não sai"
                + $" (pediu {tela.VezesPediuCodigo}x · motivo={dTres.Motivo})");
            checar(tela.Avisos.Count == 3 && tela.Avisos[0] is null
                   && tela.Avisos.Skip(1).All(a => a is not null && a.Contains("inválido", StringComparison.Ordinal)
                                                   && !a.Contains("\n") && a.Length < 60),
                "AT-6 entre uma tentativa e outra o aviso é curto, de uma linha: 'Código inválido. Tente de novo.'");

            // AT-7 dois inválidos e o certo: autoriza na terceira
            fake.ZerarBaldes();
            ProximoPasso();
            var vezes7 = 0;
            tela = new TelaFalsa { AoPedirCodigo = _ => ++vezes7 < 3 ? "000000" : fake.CodigoAgora() };
            var dTerceira = await Autorizacao.ResolverAsync(cli, PedidoDe("paygo-7", "000203", 900, 203), tela,
                CancellationToken.None);
            checar(dTerceira.Via == ViaAutorizacao.Totp && tela.VezesPediuCodigo == 3,
                "AT-7 dois erros de digitação e o código certo na terceira: autoriza");

            // AT-8 rate limit: 5 falhas em 10 min e a nuvem nem testa o código
            fake.ZerarBaldes();
            for (var n = 0; n < 5; n++)
                await cli.ValidarTotpAsync("000000", "estorno:x", "estorno", null, "dono", CancellationToken.None);
            var certoMasTarde = fake.CodigoAgora();
            tela = new TelaFalsa { AoPedirCodigo = _ => certoMasTarde };
            var dRl = await Autorizacao.ResolverAsync(cli, PedidoDe("paygo-RL", "000204", 700, 204), tela,
                CancellationToken.None);
            var tentativaRl = fake.Log.LastOrDefault();
            checar(!dRl.Autorizado && tela.VezesPediuCodigo == 1
                   && dRl.Motivo.Contains("aguarde", StringComparison.OrdinalIgnoreCase)
                   && tentativaRl is { Ok: false, TestouOCodigo: false },
                "AT-8 depois de 5 falhas em 10 min o código certo é recusado SEM ser testado, e a tela não insiste"
                + $" (motivo={dRl.Motivo})");
            fake.ZerarBaldes();

            // AT-9 autenticador não configurado
            fake.Configurado = false;
            tela = new TelaFalsa { AoPedirCodigo = _ => fake.CodigoAgora() };
            var dSemCfg = await Autorizacao.ResolverAsync(cli, PedidoDe("paygo-C", "000205", 600, 205), tela,
                CancellationToken.None);
            fake.Configurado = true;
            checar(!dSemCfg.Autorizado && tela.VezesPediuCodigo == 1
                   && dSemCfg.Motivo.Contains("utenticador", StringComparison.Ordinal),
                "AT-9 dono sem autenticador configurado: o estorno não sai e o motivo diz isso"
                + $" (motivo={dSemCfg.Motivo})");

            // AT-10 sem rede: desiste na hora, com o texto combinado
            tela = new TelaFalsa { AoPedirCodigo = _ => "123456" };
            var dRede = await Autorizacao.ResolverAsync(morta, PedidoDe("paygo-N", "000206", 400, 206), tela,
                CancellationToken.None);
            checar(!dRede.Autorizado && dRede.Via == ViaAutorizacao.Recusada && tela.VezesPediuCodigo == 1
                   && !dRede.Avisado && dRede.Motivo == "Sem internet. Estorno não autorizado.",
                "AT-10 sem internet: 'Sem internet. Estorno não autorizado.' e desiste (não existe saída pelo PIN)"
                + $" (motivo={dRede.Motivo})");

            var pedidoCanc = new PedidoAutorizacao(Terminal, Autorizacao.ReferenciaCancelamento("v-1", 207, 300), 300,
                Loja: Loja, Operador: "Bia", Venda: "207", Forma: "dinheiro") { Tipo = "cancelamento" };
            tela = new TelaFalsa { AoPedirCodigo = _ => "123456" };
            var dRedeCanc = await Autorizacao.ResolverAsync(morta, pedidoCanc, tela, CancellationToken.None);
            checar(dRedeCanc.Motivo == "Sem internet. Cancelamento não autorizado.",
                "AT-11 no cancelamento de venda o texto fala em cancelamento, não em estorno");

            // AT-12 tipo viaja para a RPC (o log do servidor separa estorno de cancelamento)
            ProximoPasso();
            tela = new TelaFalsa { AoPedirCodigo = _ => fake.CodigoAgora() };
            var dCanc = await Autorizacao.ResolverAsync(cli, pedidoCanc, tela, CancellationToken.None);
            var ultimaChamada = fake.Chamadas.LastOrDefault();
            var jCanc = JsonDocument.Parse(ultimaChamada?.Corpo ?? "{}").RootElement;
            checar(dCanc.Via == ViaAutorizacao.Totp && Txt(jCanc, "_tipo") == "cancelamento"
                   && Txt(jCanc, "_referencia").StartsWith("cancelamento:", StringComparison.Ordinal),
                "AT-12 o cancelamento de venda vai à RPC com _tipo=cancelamento e a referência própria");

            // AT-13 o operador cancela na tela: nada é chamado
            chamadasAntes = fake.Chamadas.Count;
            tela = new TelaFalsa { AoPedirCodigo = _ => null };
            var dCancelou = await Autorizacao.ResolverAsync(cli, PedidoDe("paygo-D", "000208", 900, 208), tela,
                CancellationToken.None);
            checar(!dCancelou.Autorizado && dCancelou.Avisado && fake.Chamadas.Count == chamadasAntes,
                "AT-13 operador que cancela a tela do código não estorna nada e a nuvem nem é chamada");

            // AT-14 sem nuvem configurada / sem sessão: recusa, sem tela de PIN
            var dSemNuvem = await Autorizacao.ResolverAsync(null, PedidoDe("paygo-E", "000209", 900, 209),
                new TelaFalsa(), CancellationToken.None);
            checar(!dSemNuvem.Autorizado && dSemNuvem.Via == ViaAutorizacao.Recusada && !dSemNuvem.Avisado
                   && dSemNuvem.Motivo.EndsWith("Estorno não autorizado.", StringComparison.Ordinal),
                "AT-14 caixa sem nuvem configurada: estorno não sai (e não há PIN para cair)");
            tela = new TelaFalsa { AoPedirCodigo = _ => "123456" };
            var dSemSessao = await Autorizacao.ResolverAsync(semSessao, PedidoDe("paygo-F", "000210", 900, 210), tela,
                CancellationToken.None);
            checar(!dSemSessao.Autorizado && tela.VezesPediuCodigo == 1
                   && dSemSessao.Motivo.Contains("sessão", StringComparison.Ordinal),
                "AT-15 caixa sem sessão na nuvem: estorno não sai e o motivo diz 'sessão'"
                + $" (motivo={dSemSessao.Motivo})");

            // AT-16 a janela de tolerância é ±1 passo; ±2 não vale
            fake.ZerarBaldes();
            fake.UltimoContador = 0;
            var vMenos1 = await cli.ValidarTotpAsync(fake.CodigoAgora(-1), "estorno:j1", "estorno", null, "dono", CancellationToken.None);
            fake.UltimoContador = 0;
            var vMais1 = await cli.ValidarTotpAsync(fake.CodigoAgora(+1), "estorno:j2", "estorno", null, "dono", CancellationToken.None);
            fake.UltimoContador = 0;
            var vMenos2 = await cli.ValidarTotpAsync(fake.CodigoAgora(-2), "estorno:j3", "estorno", null, "dono", CancellationToken.None);
            var vMais2 = await cli.ValidarTotpAsync(fake.CodigoAgora(+2), "estorno:j4", "estorno", null, "dono", CancellationToken.None);
            checar(vMenos1.Ok && vMais1.Ok && !vMenos2.Ok && !vMais2.Ok,
                "AT-16 código do passo anterior ou do seguinte vale (relógio do celular atrasado 30 s); ±2 não");
            // E o replay por contador: aceitar T+1 e depois recusar T (que é menor)
            fake.UltimoContador = 0;
            var vFrente = await cli.ValidarTotpAsync(fake.CodigoAgora(+1), "estorno:j5", "estorno", null, "dono", CancellationToken.None);
            var vAtras = await cli.ValidarTotpAsync(fake.CodigoAgora(0), "estorno:j6", "estorno", null, "dono", CancellationToken.None);
            checar(vFrente.Ok && !vAtras.Ok && fake.UltimoContador == Passo() + 1,
                "AT-17 depois de aceitar o código de T+1, o de T não vale mais (contador só anda para a frente)");
            fake.ZerarBaldes();

            // ── 3. AUDITORIA: quem aprovou entra, o código nunca ────────────
            cx.Execute("DELETE FROM auditoria");
            var trilha = Autorizacao.Trilha(dOk);
            Caixa.Auditar(cx, null, "tef_estorno", op.Id, dOk.Autorizador, "venda=200 nsu=000200" + trilha);
            var detalhe = cx.ExecuteScalar<string>("SELECT detalhe FROM auditoria ORDER BY id DESC LIMIT 1") ?? "";
            var autorizador = cx.ExecuteScalar<string>("SELECT autorizador FROM auditoria ORDER BY id DESC LIMIT 1") ?? "";
            checar(detalhe.Contains("Brenno", StringComparison.Ordinal)
                   && detalhe.Contains("autenticador do dono", StringComparison.Ordinal)
                   && detalhe.Contains(dOk.TokenId![..8], StringComparison.Ordinal)
                   && autorizador == dOk.Autorizador,
                "AU-1 a linha do estorno diz que foi o autenticador do dono, o nome dele e o id do registro na nuvem");
            var todosOsCodigos = fake.Chamadas.Select(c => Txt(JsonDocument.Parse(c.Corpo).RootElement, "_codigo"))
                .Where(c => c.Length == 6).Distinct().ToList();
            checar(todosOsCodigos.Count > 0 && cx.ExecuteScalar<int>(
                       "SELECT COUNT(*) FROM auditoria WHERE " +
                       string.Join(" OR ", todosOsCodigos.Select((_, i) => $"detalhe LIKE '%' || @C{i} || '%'")),
                       todosOsCodigos.Select((c, i) => new KeyValuePair<string, object>($"C{i}", c))
                           .ToDictionary(k => k.Key, k => k.Value)) == 0,
                "AU-2 nenhum código digitado nesta suíte aparece na auditoria");
            checar(Autorizacao.Trilha(dTres) == "" && Autorizacao.Trilha(dRede) == "",
                "AU-3 recusa não tem trilha (não existe 'liberado pelo PIN' nem 'sem aprovação remota')");

            // ── 4. TUDO QUE TOCA JANELA VOLTA PARA A THREAD DA TELA ─────────
            {
                using var ui = new ThreadDeTela();
                fake.ZerarBaldes();
                fake.UltimoContador = 0;   // o AT-17 deixou o contador em T+1
                ProximoPasso();

                var telaUi = new TelaQueAnotaThread(ui.Id) { AoPedirCodigo = _ => fake.CodigoAgora() };
                DesfechoAutorizacao dUi;
                var estouroUi = "";
                try
                {
                    dUi = await ui.ExecutarAsync(() => Autorizacao.ResolverAsync(cli,
                        PedidoDe("paygo-UI", "000301", 2500, 301), telaUi, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    estouroUi = $" — e ainda ESCAPOU {ex.GetType().Name}: {ex.Message}";
                    dUi = new DesfechoAutorizacao(ViaAutorizacao.Recusada, null, null, "estourou");
                }
                checar(dUi.Via == ViaAutorizacao.Totp && telaUi.VezesAguardou == 1 && telaUi.VezesFechouEspera == 1
                       && telaUi.ForaDaThreadDaTela.Count == 0,
                    "UI-1 no caminho feliz, a tela e o fechamento da espera caem na thread da tela"
                    + $" (via={dUi.Via} esperas={telaUi.VezesAguardou}/{telaUi.VezesFechouEspera})"
                    + (telaUi.ForaDaThreadDaTela.Count > 0
                        ? " — FORA DELA: " + string.Join(" · ", telaUi.ForaDaThreadDaTela) : "")
                    + estouroUi);

                var telaTres = new TelaQueAnotaThread(ui.Id) { AoPedirCodigo = _ => "000000" };
                DesfechoAutorizacao dUiTres;
                var estouroTres = "";
                try
                {
                    dUiTres = await ui.ExecutarAsync(() => Autorizacao.ResolverAsync(cli,
                        PedidoDe("paygo-UI2", "000302", 900, 302), telaTres, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    estouroTres = $" — e ainda ESCAPOU {ex.GetType().Name}: {ex.Message}";
                    dUiTres = new DesfechoAutorizacao(ViaAutorizacao.Totp, null, null, "estourou");
                }
                checar(dUiTres.Via == ViaAutorizacao.Recusada && telaTres.VezesPediuCodigo == 3
                       && telaTres.VezesFechouEspera == 3 && telaTres.ForaDaThreadDaTela.Count == 0,
                    "UI-2 o caminho das três tentativas também roda inteiro na thread da tela"
                    + (telaTres.ForaDaThreadDaTela.Count > 0
                        ? " — FORA DELA: " + string.Join(" · ", telaTres.ForaDaThreadDaTela) : "")
                    + estouroTres);
                fake.ZerarBaldes();

                var fonteAut = Fonte("Pdv.Nucleo", "Autorizacao.cs") ?? "";
                var corpoRes = Trecho(fonteAut, "public static async Task<DesfechoAutorizacao> ResolverAsync",
                    "private static string TextoDoMotivo");
                checar(corpoRes.Length > 0 && !corpoRes.Contains("ConfigureAwait(false)", StringComparison.Ordinal),
                    "UI-3 a máquina de estados não usa ConfigureAwait(false) (a continuação tem que voltar ao Dispatcher)");
            }

            // ── 5. GARANTIA POR FONTE: NENHUM CAMINHO SEM Via=Totp ──────────
            // A máquina de estados pode estar certa e a tela ainda fabricar um
            // DesfechoAutorizacao "aprovado" por conta própria (foi assim que o PIN
            // de emergência morava no catch do estorno). Aqui varre-se TUDO que
            // entra no .exe: só Recusada e Totp podem ser construídos, e só o
            // núcleo constrói Totp.
            {
                var fontes = FontesDoExe();
                var construcoes = new List<string>();
                var forasDoNucleo = new List<string>();
                var rx = new Regex(@"new\s+DesfechoAutorizacao\s*\(\s*ViaAutorizacao\.(\w+)", RegexOptions.Compiled);
                foreach (var f in fontes)
                {
                    var t = File.ReadAllText(f);
                    foreach (Match m in rx.Matches(t))
                    {
                        var via = m.Groups[1].Value;
                        if (via is not ("Recusada" or "Totp")) construcoes.Add($"{Path.GetFileName(f)}: {via}");
                        if (via == "Totp" && !f.EndsWith(Path.Combine("Pdv.Nucleo", "Autorizacao.cs"), StringComparison.Ordinal))
                            forasDoNucleo.Add(Path.GetFileName(f));
                    }
                    if (Regex.IsMatch(t, @"with\s*\{[^}]*\bVia\s*=")) construcoes.Add($"{Path.GetFileName(f)}: with {{ Via = }}");
                }
                checar(fontes.Length > 0 && construcoes.Count == 0,
                    "FT-1 em todo fonte do .exe só existe DesfechoAutorizacao com Via=Recusada ou Via=Totp"
                    + (construcoes.Count > 0 ? " — ACHEI: " + string.Join(", ", construcoes) : ""));
                checar(forasDoNucleo.Count == 0,
                    "FT-2 só o núcleo (Autorizacao.cs) fabrica Via=Totp; tela nenhuma aprova por conta própria"
                    + (forasDoNucleo.Count > 0 ? " — ACHEI EM: " + string.Join(", ", forasDoNucleo) : ""));

                var nomes = Enum.GetNames(typeof(ViaAutorizacao));
                checar(nomes.Contains("Totp") && !nomes.Contains("Pin") && !nomes.Contains("Token"),
                    "FT-3 ViaAutorizacao tem Totp e não tem mais Pin nem Token (não há o que produzi-los)");
                checar(typeof(ITelaAutorizacao).GetMethod("PedirPinAsync") is null
                       && typeof(ITelaAutorizacao).GetMethod("EscolherAposFalhaAsync") is null
                       && typeof(ITelaAutorizacao).GetMethod("PedirCodigoAsync") is not null,
                    "FT-4 a tela da autorização não tem mais PedirPinAsync nem escolha depois da falha");
                var metodosRemota = typeof(IAutorizacaoRemota).GetMethods().Select(m => m.Name).OrderBy(n => n).ToArray();
                checar(metodosRemota.SequenceEqual(new[] { "ValidarTotpAsync" }),
                    "FT-5 a nuvem da autorização tem UM método: ValidarTotpAsync (sem solicitar, sem validar token)"
                    + $" ({string.Join(", ", metodosRemota)})");

                foreach (var (pasta, nome) in new[] { ("Pdv.Nucleo", "Autorizacao.cs"), ("Telas", "TelaAutorizacao.cs"), ("Telas", "PedirCodigo.cs") })
                {
                    var t = Fonte(pasta, nome) ?? "";
                    var sobras = new[] { "PedirPinAsync", "PedirSenha", "AutorizarSupervisor", "SolicitarAsync", "WhatsApp", "AcaoCodigo.Pin", "NovoCodigo" }
                        .Where(s => t.Contains(s, StringComparison.Ordinal)).ToList();
                    checar(t.Length > 0 && sobras.Count == 0,
                        $"FT-6 {nome} não tem sobra de PIN nem de mensagem no celular"
                        + (sobras.Count > 0 ? " — ACHEI: " + string.Join(", ", sobras) : ""));
                }

                var pedirCodigo = Fonte("Telas", "PedirCodigo.cs") ?? "";
                checar(pedirCodigo.Contains("Código do autenticador do dono", StringComparison.Ordinal)
                       && pedirCodigo.Contains("MaxLength = 6", StringComparison.Ordinal)
                       && !pedirCodigo.Contains("PIN", StringComparison.Ordinal)
                       && !pedirCodigo.Contains("Não recebi", StringComparison.Ordinal),
                    "FT-7 a tela do código pede 'Código do autenticador do dono', 6 dígitos, sem 'novo código' e sem PIN");
                var corpoEspera = Trecho(pedirCodigo, "public sealed class Espera", "public static class PedirCodigo");
                checar(corpoEspera.Contains("Dispatcher.Invoke", StringComparison.Ordinal),
                    "FT-8 o Dispose do aviso de espera marshala para a thread da UI (não estoura se vier do pool)");

                var servicos = Fonte("Servicos.cs") ?? "";
                var corpoAutorizador = Trecho(servicos, "public static ClienteAutorizacao Autorizador()", "\n    }");
                checar(corpoAutorizador.Contains("TokenAsync", StringComparison.Ordinal)
                       && !corpoAutorizador.Contains("AnonKey", StringComparison.Ordinal),
                    "FT-9 Servicos.Autorizador entrega o bearer da SESSÃO do terminal (Nuvem.TokenAsync), não a chave pública");
            }

            // ── 6. A TELA DO ESTORNO REALMENTE USA ISTO ─────────────────────
            {
                var fonte = Fonte("Telas", "Venda.xaml.cs") ?? "";
                checar(fonte.Length > 0, "TL-1 achei a fonte da tela de venda para conferir o estorno");
                var corpo = Trecho(fonte, "private async Task EstornarTefAsync", "\n    /// <summary>");

                checar(corpo.Contains("Autorizacao.ResolverAsync", StringComparison.Ordinal),
                    "TL-2 o estorno passa pela autorização do núcleo");
                var portas = new[] { "PedirSenha.Mostrar", "PedirPinAsync", "AutorizarSupervisor", "ViaAutorizacao.Totp", "WhatsApp", "Supervisor" }
                    .Where(s => corpo.Contains(s, StringComparison.Ordinal)).ToList();
                checar(corpo.Length > 0 && portas.Count == 0,
                    "TL-3 não sobrou porta lateral no estorno: sem PIN, sem senha, sem fabricar aprovação"
                    + (portas.Count > 0 ? " — ACHEI: " + string.Join(", ", portas) : ""));
                checar(corpo.Contains("Autorizacao.Referencia(", StringComparison.Ordinal)
                       && corpo.Contains("l.tef_id", StringComparison.Ordinal),
                    "TL-4 a referência mandada à nuvem é a daquele estorno (transação + NSU + valor + venda)");
                checar(Ordem(corpo, "Dialogo.Confirmar", "Autorizacao.ResolverAsync"),
                    "TL-5 o código só é pedido depois de o operador confirmar o estorno");
                checar(Ordem(corpo, "Autorizacao.ResolverAsync", "if (!aut.Autorizado)")
                       && Ordem(corpo, "if (!aut.Autorizado)", "cli.CancelarAsync"),
                    "TL-6 sem autorização o método retorna ANTES do CNC (nada é estornado)");
                var dec = corpo.IndexOf("var detalhe = $\"", StringComparison.Ordinal);
                var linhaDetalhe = dec < 0 ? "" : corpo[dec..corpo.IndexOf('\n', dec)];
                checar(linhaDetalhe.Contains("{trilha}", StringComparison.Ordinal),
                    "TL-7 a linha normal do estorno carrega a trilha (quem aprovou pelo autenticador)");
                var iCatch = corpo.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
                var corpoCatch = iCatch < 0 ? "" : corpo[iCatch..Math.Min(corpo.Length, iCatch + 1200)];
                checar(Ordem(corpo, "Autorizacao.ResolverAsync", "catch (Exception ex)")
                       && corpoCatch.Contains("ViaAutorizacao.Recusada", StringComparison.Ordinal),
                    "TL-8 falha na autorização não mata o PDV: tem catch, e o catch RECUSA (não libera por outro caminho)");
                checar(!corpo.Contains("AuditarSemAprovacaoRemota", StringComparison.Ordinal),
                    "TL-9 não existe mais 'estorno sem aprovação remota' para registrar");
            }

            // ── 6b. CANCELAR VENDA E NOTA SEM MAQUININHA (CV-*) ──────────────
            {
                var fonte = Fonte("Telas", "Venda.xaml.cs") ?? "";
                var xaml = Fonte("Telas", "Venda.xaml") ?? "";

                checar(xaml.Contains("Click=\"MenuCancelamento\"", StringComparison.Ordinal)
                       && xaml.Contains("Cancelar venda", StringComparison.Ordinal),
                    "CV-1 a barra tem um botão que DIZ que cancela venda (não só 'Cartão')");

                var menu = Trecho(fonte, "private async void MenuCancelamento", "private static void GuardarPasso");
                checar(menu.Length > 0, "CV-2 achei o menu de cancelamento na tela de venda");
                checar(menu.Length > 0 && !menu.Contains("não tem maquininha ligada a ele", StringComparison.Ordinal),
                    "CV-3 o menu não manda mais 'chame o gerente para configurar' a quem quer cancelar uma nota");
                checar(Ordem(menu, "CancelarVendaAsync", "Servicos.Operavel()"),
                    "CV-4 o cancelamento vem ANTES de qualquer checagem de maquininha (o TEF só barra o estorno)");

                var corpo = Trecho(fonte, "private async Task CancelarVendaAsync", "\n    private void Suprimento");
                checar(corpo.Length > 0, "CV-5 existe um cancelamento de venda que vive por conta própria");
                checar(corpo.Length > 0
                       && !corpo.Contains("Servicos.Operavel()", StringComparison.Ordinal)
                       && !corpo.Contains("IProvedorTefOperavel", StringComparison.Ordinal),
                    "CV-6 o cancelamento funciona COM ou SEM TEF (não toca no provedor da maquininha)");
                checar(Ordem(corpo, "CancelamentoFiscal.CancelarAsync", "Vendas.Cancelar"),
                    "CV-7 cancela a NOTA antes da VENDA (nota viva para venda morta não pode existir)");
                checar(corpo.Contains("rc.Indisponivel", StringComparison.Ordinal)
                       && Ordem(corpo, "rc.Indisponivel", "fiscal_status = 'cancelada'"),
                    "CV-8 agente indisponível não vira nota cancelada no banco");
                var portas = new[] { "PedirSenha.Mostrar", "PedirPinAsync", "AutorizarSupervisor", "ViaAutorizacao.Totp", "WhatsApp", "Supervisor", "AuditarSemAprovacaoRemota" }
                    .Where(s => corpo.Contains(s, StringComparison.Ordinal)).ToList();
                checar(corpo.Contains("Autorizacao.ResolverAsync", StringComparison.Ordinal) && portas.Count == 0,
                    "CV-9 passa pela autorização do núcleo, sem porta lateral"
                    + (portas.Count > 0 ? " — ACHEI: " + string.Join(", ", portas) : ""));
                checar(Ordem(corpo, "Dialogo.Confirmar", "Autorizacao.ResolverAsync"),
                    "CV-10 o código só é pedido depois de o operador confirmar");
                var iCatch = corpo.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
                var corpoCatch = iCatch < 0 ? "" : corpo[iCatch..Math.Min(corpo.Length, iCatch + 1200)];
                checar(Ordem(corpo, "Autorizacao.ResolverAsync", "catch (Exception ex)")
                       && corpoCatch.Contains("ViaAutorizacao.Recusada", StringComparison.Ordinal),
                    "CV-11 falha na autorização não derruba o PDV, e o catch RECUSA");
                checar(corpo.Contains("Tipo = \"cancelamento\"", StringComparison.Ordinal)
                       && corpo.Contains("Autorizacao.ReferenciaCancelamento(", StringComparison.Ordinal),
                    "CV-12 o pedido vai como tipo 'cancelamento' com referência própria (o log da nuvem separa os dois atos)");
                checar(Ordem(corpo, "Autorizacao.ResolverAsync", "if (!aut.Autorizado)")
                       && Ordem(corpo, "if (!aut.Autorizado)", "CancelamentoFiscal.CancelarAsync")
                       && Ordem(corpo, "if (!aut.Autorizado)", "Vendas.Cancelar"),
                    "CV-13 sem autorização nada é cancelado (nem nota, nem venda)");
                checar(corpo.Contains("CancelamentoVenda", StringComparison.Ordinal)
                       && corpo.Contains("AvisoDoDinheiro", StringComparison.Ordinal)
                       && corpo.Contains("TextoDaNota", StringComparison.Ordinal),
                    "CV-14 a tela diz que NENHUM dinheiro volta sozinho e mostra o prazo da nota");
            }

            // ── 7. A CONFIGURAÇÃO CONTINUA PELA SENHA DE ADMINISTRADOR ──────
            {
                var fonte = Fonte("MainWindow.xaml.cs") ?? "";
                var i = fonte.IndexOf("private void AbrirConfigProtegida()", StringComparison.Ordinal);
                var corpo = i < 0 ? "" : fonte[i..Math.Min(fonte.Length, i + 1200)];
                checar(corpo.Contains("SenhaAdminConfere", StringComparison.Ordinal)
                       && !corpo.Contains("ResolverAsync", StringComparison.Ordinal),
                    "CF-1 a configuração é liberada pela senha de administrador, não pelo autenticador");
            }

            // ── 8. O QUE O .EXE NÃO PODE CARREGAR ───────────────────────────
            {
                var doExe = FontesDoExe();
                var comSegredo = doExe
                    .Where(f => File.ReadAllText(f) is var t
                             && (t.Contains("\"role\":\"service_role\"") || t.Contains("SUPABASE_SERVICE_ROLE")
                                 || t.Split('"').Any(pedaco => PapelDoJwt(pedaco) == "service_role")))
                    .ToList();
                checar(doExe.Length > 0 && comSegredo.Count == 0,
                    "SEG-1 nenhum fonte que entra no .exe carrega chave service_role" +
                    (comSegredo.Count > 0 ? " — ACHEI EM: " + string.Join(", ", comSegredo) : ""));
                checar(PapelDoJwt(Nuvem.AnonKey) == "anon",
                    "SEG-2 a chave embutida no .exe é a pública (role=anon)");
                // O segredo do autenticador vive SÓ no servidor: nem base32, nem
                // otpauth, nem HMAC no que entra no .exe.
                var comTotp = doExe
                    .Where(f => File.ReadAllText(f) is var t
                             && (t.Contains("otpauth://", StringComparison.Ordinal)
                                 || t.Contains("HMACSHA1", StringComparison.Ordinal)
                                 || t.Contains("GEZDGNBV", StringComparison.Ordinal)))
                    .ToList();
                checar(comTotp.Count == 0,
                    "SEG-3 o .exe não calcula TOTP nem conhece segredo: quem confere é a nuvem" +
                    (comTotp.Count > 0 ? " — ACHEI EM: " + string.Join(", ", comTotp) : ""));
            }
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }
}
