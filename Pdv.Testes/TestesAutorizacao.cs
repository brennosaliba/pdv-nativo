using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// AUTORIZAÇÃO DE ESTORNO POR TOKEN DE WHATSAPP.
///
/// O estorno é o único ponto do PDV onde dinheiro VOLTA para o cliente sem que
/// nada tenha sido vendido — e até hoje ele era liberado por um PIN guardado no
/// banco do próprio caixa. Quem opera e quem autoriza podiam ser a mesma pessoa.
///
/// Aqui o caminho normal passa a ser um código de 6 dígitos que a nuvem manda no
/// WhatsApp da gerente geral e do dono. Mas o PIN CONTINUA VALENDO COMO SAÍDA:
/// se a nuvem não responder em 15 s, o caixa cai para o PIN — e a auditoria
/// registra, num evento DISTINTO, que aquele estorno saiu sem aprovação remota.
///
/// O que esta suíte protege, em ordem de estrago:
///  · o cliente no balcão nunca fica sem estorno (rede caída ⇒ PIN, sempre);
///  · o dono consegue LISTAR depois os estornos que escaparam do token;
///  · o token é de UM estorno só — não serve para estornar outra venda;
///  · o operador nunca fica preso numa tela sem saída;
///  · o código digitado não vai parar em log nem em auditoria.
///
/// A nuvem aqui é <see cref="FakeAutorizacao"/> (contrato da edge
/// `pdv-autorizacao`), porque a de verdade acende o celular do dono.
/// </summary>
public static class TestesAutorizacao
{
    /// <summary>Tela de mentira: o que precisaria de janela vira delegate roteirizável.</summary>
    private sealed class TelaFalsa : ITelaAutorizacao
    {
        public Func<RespostaSolicitacao, string?, RespostaCodigo>? AoPedirCodigo;
        public Func<string, EscolhaAposFalha>? AoFalhar;
        public Operador? PinDevolve;

        public int VezesPediuCodigo, VezesPediuPin, VezesAguardou;
        public readonly List<string> Esperas = new();
        public readonly List<string?> Avisos = new();
        public string? MotivoDoPin;

        private sealed class Nada : IDisposable { public void Dispose() { } }

        public IDisposable Aguardando(string mensagem)
        {
            VezesAguardou++;
            Esperas.Add(mensagem);
            return new Nada();
        }

        public Task<RespostaCodigo> PedirCodigoAsync(RespostaSolicitacao pedido, string? aviso)
        {
            VezesPediuCodigo++;
            Avisos.Add(aviso);
            return Task.FromResult(AoPedirCodigo?.Invoke(pedido, aviso)
                                   ?? new RespostaCodigo(AcaoCodigo.Cancelar, null));
        }

        public Task<EscolhaAposFalha> EscolherAposFalhaAsync(string mensagem)
            => Task.FromResult(AoFalhar?.Invoke(mensagem) ?? EscolhaAposFalha.Desistir);

        public Task<Operador?> PedirPinAsync(string motivo)
        {
            VezesPediuPin++;
            MotivoDoPin = motivo;
            return Task.FromResult(PinDevolve);
        }
    }

    /// <summary>
    /// O DISPATCHER DO WPF DE POBRE: uma thread só, com fila e
    /// <see cref="SynchronizationContext"/>.
    ///
    /// Existe porque o estrago que esta suíte não pegava é de THREAD, não de
    /// lógica: <see cref="TelaFalsa"/> passa em qualquer thread, então um
    /// `ConfigureAwait(false)` no núcleo ficava verde aqui e derrubava o Pdv.exe
    /// no primeiro estorno de verdade (janela do WPF só aceita ser tocada pela
    /// thread que a criou). Trazer WPF para o Pdv.Testes não dá — o projeto é
    /// net8.0 sem SDK de janela. Mas a REGRA que o WPF impõe é só esta: a
    /// continuação tem que voltar para a thread que entrou. Um contexto de
    /// thread única a reproduz inteira, e roda headless.
    /// </summary>
    private sealed class ThreadDeTela : IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Cb, object? Estado)> _fila = new();
        private readonly ManualResetEventSlim _pronta = new(false);
        private readonly Thread _thread;

        /// <summary>A "thread da UI": é a ela que tudo que toca janela precisa voltar.</summary>
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

        /// <summary>Roda `trabalho` NA thread da tela — o equivalente ao clique do operador.</summary>
        public Task<T> ExecutarAsync<T>(Func<Task<T>> trabalho)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Correr()
            {
                // O catch é o que faz o teste ACUSAR em vez de travar: hoje a
                // InvalidOperationException do WPF escapa de ResolverAsync, e sem
                // isto o await do teste ficaria pendurado para sempre.
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

    /// <summary>
    /// Tela que não desenha nada: só anota EM QUE THREAD cada coisa aconteceu —
    /// inclusive o Dispose do aviso de espera, que é exatamente onde o
    /// <c>Espera.Dispose</c> de verdade faz <c>_dono.IsEnabled = true</c>.
    /// </summary>
    private sealed class TelaQueAnotaThread : ITelaAutorizacao
    {
        private readonly int _ui;
        public TelaQueAnotaThread(int ui) => _ui = ui;

        public Func<RespostaSolicitacao, string?, RespostaCodigo>? AoPedirCodigo;
        public Func<string, EscolhaAposFalha>? AoFalhar;
        public Operador? PinDevolve;

        public int VezesPediuCodigo, VezesPediuPin, VezesAguardou, VezesFechouEspera;

        /// <summary>Cada linha aqui é uma janela tocada da thread errada — no PDV, um crash.</summary>
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

        public Task<RespostaCodigo> PedirCodigoAsync(RespostaSolicitacao pedido, string? aviso)
        {
            VezesPediuCodigo++;
            Anotar("PedirCodigoAsync");
            return Task.FromResult(AoPedirCodigo?.Invoke(pedido, aviso)
                                   ?? new RespostaCodigo(AcaoCodigo.Cancelar, null));
        }

        public Task<EscolhaAposFalha> EscolherAposFalhaAsync(string mensagem)
        {
            Anotar("EscolherAposFalhaAsync");
            return Task.FromResult(AoFalhar?.Invoke(mensagem) ?? EscolhaAposFalha.Desistir);
        }

        public Task<Operador?> PedirPinAsync(string motivo)
        {
            VezesPediuPin++;
            Anotar("PedirPinAsync");
            return Task.FromResult(PinDevolve);
        }
    }

    private const string Terminal = "Caixa Savassi 1";
    private const string Loja = "American Day Savassi";

    private static PedidoAutorizacao PedidoDe(string tefId, string nsu, long centavos, long venda)
        => new(Terminal, Autorizacao.Referencia(tefId, nsu, centavos, venda), centavos,
               Loja: Loja, Operador: "Bia", Venda: venda.ToString(), Forma: "credito", Nsu: nsu);

    /// <summary>O código que a Ingrid recebeu no WhatsApp daquele token.</summary>
    private static string CodigoDe(FakeAutorizacao fake, RespostaSolicitacao r, string scope = "manager")
        => r.Id is { Length: > 0 } id && fake.Codigos.TryGetValue(id, out var lista)
            ? lista.First(c => c.Scope == scope).Codigo
            : "000000";   // sem token (a nuvem não respondeu): o teste segue e a asserção falha

    /// <summary>Um código de 6 dígitos que com CERTEZA não é de nenhum aprovador daquele token.</summary>
    private static string ErradoDe(FakeAutorizacao fake, RespostaSolicitacao r)
    {
        var certos = r.Id is { Length: > 0 } id && fake.Codigos.TryGetValue(id, out var lista)
            ? lista.Select(c => c.Codigo).ToHashSet()
            : new HashSet<string>();
        for (var n = 0; n < 1_000_000; n++)
            if (certos.Add(n.ToString("D6"))) return n.ToString("D6");
        return "000000";
    }

    /// <summary>
    /// Espera a nuvem terminar o que o caixa já desistiu de esperar. É o miolo do
    /// token fantasma: quando o PDV cai para o PIN, a edge NÃO para — ela segue
    /// criando a linha e mandando o WhatsApp. Sem esperar esse rabo, o teste
    /// contaria o token antes de ele existir.
    /// </summary>
    private static async Task<bool> EsperarAsync(Func<bool> ate, int limiteMs = 8000)
    {
        var cron = Stopwatch.StartNew();
        while (cron.ElapsedMilliseconds < limiteMs)
        {
            if (ate()) return true;
            await Task.Delay(25);
        }
        return ate();
    }

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
            var sup = new Operador("sup-aut", "Rita", "supervisor");
            Operadores.Salvar(cx, sup.Id, sup.Nome, "9876", "supervisor");

            using var fake = new FakeAutorizacao();

            // ── 1. CONTRATO COM A EDGE ──────────────────────────────────────
            var cli = new ClienteAutorizacao(fake.Url, "chave-publica-de-teste", TimeSpan.FromSeconds(5));
            var pedido = PedidoDe("paygo-1", "000123", 1200, 142);

            var r1 = await cli.SolicitarAsync(pedido, CancellationToken.None);
            checar(r1.Ok && !string.IsNullOrWhiteSpace(r1.Id) && r1.Destinatarios.Count == 2,
                "CT-1 solicitar devolve o id do token e para quem o código foi (dono + gerente)");

            var codigoIngrid = r1.Ok ? CodigoDe(fake, r1) : "000000";
            checar(r1.Ok && !JsonSerializer.Serialize(r1).Contains(codigoIngrid),
                "CT-2 o código NÃO volta para o caixa em lugar nenhum da resposta");

            var enviado = fake.Chamadas.FirstOrDefault(c => c.Contains("solicitar")) ?? "{}";
            var jc = JsonDocument.Parse(enviado).RootElement;
            checar(Txt(jc, "terminal") == Terminal
                   && Txt(jc, "referencia") == pedido.Referencia
                   && Num(jc, "valor_cent") == 1200
                   && Txt(jc, "nsu") == "000123"
                   && Txt(jc, "venda") == "142",
                "CT-3 o pedido leva terminal, referência, valor em centavos INTEIROS, venda e NSU");

            var v1 = await cli.ValidarAsync(r1.Id ?? "", codigoIngrid, pedido.Referencia, CancellationToken.None);
            checar(v1.Ok && v1.AprovadoPor is not null && v1.AprovadoPor.Contains("Ingrid")
                   && v1.Referencia == pedido.Referencia && v1.ValorCent == 1200,
                "CT-4 validar devolve QUEM aprovou e repete referência e valor para o caixa conferir");

            var r2 = await cli.SolicitarAsync(PedidoDe("paygo-2", "000124", 500, 143), CancellationToken.None);
            var v2 = await cli.ValidarAsync(r2.Id ?? "", "999999", null, CancellationToken.None);
            checar(!v2.Ok && v2.Definitiva && v2.Motivo == "codigo_invalido",
                "CT-5 código errado volta como recusa DEFINITIVA (o caixa não fica esperando)");

            var morta = new ClienteAutorizacao("http://127.0.0.1:9", "k", TimeSpan.FromSeconds(2));
            var r3 = await morta.SolicitarAsync(pedido, CancellationToken.None);
            checar(!r3.Ok && !r3.Definitiva,
                "CT-6 nuvem fora do ar não é veredito: volta 'não sei' e o PDV pode cair para o PIN");

            checar(ClienteAutorizacao.TempoPadrao == TimeSpan.FromSeconds(15),
                "CT-7 o tempo padrão do cliente é 15 s (o combinado com o dono)");

            fake.AtrasoMs = 4000;
            var lento = new ClienteAutorizacao(fake.Url, "k", TimeSpan.FromMilliseconds(700));
            var cron = Stopwatch.StartNew();
            var r4 = await lento.SolicitarAsync(pedido, CancellationToken.None);
            cron.Stop();
            fake.AtrasoMs = 0;
            checar(!r4.Ok && !r4.Definitiva && cron.ElapsedMilliseconds < 2500,
                "CT-8 nuvem lenta não segura o caixa: o cliente desiste no tempo configurado");

            // ── 2. HOMOLOGAÇÃO CONTINUA PASSANDO DIRETO ─────────────────────
            // Os passos 20/21/22/54 do roteiro PayGo são estornos: se travarem,
            // o dono não homologa.
            // Daqui para baixo o assunto é o FLUXO, não o rate limit: solta o teto de
            // solicitações do fake (o do rate limit é o AT-18, que baixa de novo).
            fake.MaxSolicitacoes = 99;

            // ── CONFIGURAÇÃO TAMBÉM PASSA PELA APROVAÇÃO REMOTA ───────
            // Quem entra na Configuração muda série fiscal, ambiente da NFC-e e TEF:
            // erra e a nota sai errada por dias sem ninguém perceber. Por isso ela usa
            // o MESMO caminho do estorno, mudando só o `Tipo` — a nuvem escreve outra
            // mensagem, porque quem aprova precisa saber o que está aprovando.
            {
                var pedidoCfg = pedido with { Tipo = "configuracao", ValorCent = 0 };
                checar(pedidoCfg.Tipo == "configuracao" && pedido.Tipo == "estorno",
                    "AT-1b o tipo do pedido separa configuração de estorno (e o estorno segue o padrão)");

                var telaCfg = new TelaFalsa
                {
                    PinDevolve = sup,
                    AoPedirCodigo = (rp, _) => new RespostaCodigo(AcaoCodigo.Confirmar, CodigoDe(fake, rp)),
                };
                var dCfg = await Autorizacao.ResolverAsync(cx, cli, pedidoCfg, op, telaCfg, CancellationToken.None);
                checar(dCfg.Autorizado && dCfg.Via == ViaAutorizacao.Token,
                    "AT-1c a configuração é liberada pelo código do WhatsApp, igual ao estorno");

                // Nuvem fora do ar: a tela cai para a senha — e ela existe de propósito,
                // porque internet caindo é exatamente quando se precisa entrar lá.
                var telaMorta = new TelaFalsa { PinDevolve = sup };
                var dMorta = await Autorizacao.ResolverAsync(cx, morta, pedidoCfg, op, telaMorta, CancellationToken.None);
                checar(dMorta.SemAprovacaoRemota,
                    "AT-1d com a nuvem fora do ar a configuração sai SEM aprovação remota (e a auditoria marca)");
            }

            // AT-1 guarda o que foi REMOVIDO: existia um modo de homologação que
            // autorizava estorno sem PIN, sem token e sem tocar na nuvem. Era porta
            // dos fundos num caixa de verdade — quem ligasse a config estornava
            // sozinho. Saiu quando a operação começou, e este teste existe para que
            // ninguém o traga de volta sem perceber.
            var tela = new TelaFalsa { PinDevolve = sup };
            Vendas.GravarConfig(cx, "homologacao", "1");
            var chamadasAntes = fake.Chamadas.Count;
            var dHom = await Autorizacao.ResolverAsync(cx, cli, pedido, op, tela, CancellationToken.None);
            Vendas.GravarConfig(cx, "homologacao", "0");
            checar(dHom.Via != ViaAutorizacao.Homologacao && fake.Chamadas.Count > chamadasAntes,
                "AT-1 a config 'homologacao' NÃO autoriza mais nada: a autorização é pedida igual");

            // ── 3. NUVEM CAÍDA ⇒ PIN, E A AUDITORIA DENUNCIA ────────────────
            tela = new TelaFalsa { PinDevolve = sup };
            var dPin = await Autorizacao.ResolverAsync(cx, morta, pedido, op, tela, CancellationToken.None);
            checar(dPin.Via == ViaAutorizacao.Pin && dPin.Autorizado && dPin.Supervisor?.Id == sup.Id
                   && tela.VezesPediuPin == 1,
                "AT-2 nuvem fora do ar cai para o PIN do supervisor (o cliente não fica sem estorno)");
            checar(tela.Esperas.Any(e => e.Contains("autoriza", StringComparison.OrdinalIgnoreCase)),
                "AT-3 o caixa vê 'enviando pedido de autorização' enquanto a nuvem é chamada");

            cx.Execute("DELETE FROM outbox");
            Autorizacao.AuditarSemAprovacaoRemota(cx, dPin, op.Id, "venda=142 nsu=000123", pedido, "venda-142");
            Caixa.Auditar(cx, null, "tef_estorno", op.Id, dPin.Autorizador,
                "venda=142 nsu=000123" + Autorizacao.Trilha(dPin));
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM auditoria WHERE evento = @E",
                       new { E = Autorizacao.EventoSemAprovacaoRemota }) == 1,
                "AT-4 estorno sem aprovação remota vira EVENTO PRÓPRIO — o dono consegue listar depois");
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM auditoria WHERE evento = 'tef_estorno'") == 1,
                "AT-5 ...e a linha 'tef_estorno' de sempre continua lá (relatório não perde linha)");
            checar((cx.ExecuteScalar<string>(
                       "SELECT detalhe FROM auditoria WHERE evento = 'tef_estorno' ORDER BY id DESC LIMIT 1") ?? "")
                   .Contains("SEM APROVAÇÃO REMOTA"),
                "AT-6 a própria linha do estorno diz que saiu sem aprovação remota");

            // ── 3b. O FATO TEM QUE SAIR DO DISCO DA LOJA ────────────────────
            //
            // A linha de auditoria acima mora em C:/ProgramData/PdvNativo/pdv.db,
            // no PC DAQUELE caixa. Nenhuma tela do PDV lê a tabela `auditoria` e
            // nada a sincroniza — quem sincroniza é o outbox. Sem a linha na fila,
            // "o dono consegue listar depois" só se cumpre se ele for até a loja,
            // abrir o SQLite do caixa certo e rodar a consulta na mão.
            //
            // A nuvem também não reconstrói a lista sozinha: quando a internet cai
            // ANTES do `solicitar`, não existe nem linha em pdv_autorizacao_token —
            // e um token que ficou aberto é indistinguível entre "o operador
            // cancelou" e "caiu para o PIN".
            var filaSem = cx.Query("SELECT tipo, ref_id, client_key, payload FROM outbox").ToList();
            checar(filaSem.Count == 1 && (string)filaSem[0].tipo == Autorizacao.TipoNaFila,
                "AT-4b o estorno sem aprovação remota TAMBÉM entra na fila da nuvem "
                + $"(outbox: {filaSem.Count} linha(s) — {string.Join(", ", filaSem.Select(f => (string)f.tipo))})");
            var jFila = JsonDocument.Parse(filaSem.Count == 1 ? (string)filaSem[0].payload : "{}").RootElement;
            checar(filaSem.Count == 1
                   && (string)filaSem[0].client_key == pedido.Referencia
                   && (string)filaSem[0].ref_id == "venda-142"
                   && Txt(jFila, "venda") == "142" && Txt(jFila, "nsu") == "000123"
                   && Num(jFila, "valor_cent") == 1200
                   && Txt(jFila, "via") == "pin"
                   && Txt(jFila, "autorizado_por") == sup.Id
                   && Txt(jFila, "motivo").Length > 0
                   && Txt(jFila, "referencia") == pedido.Referencia,
                "AT-4c a linha da fila leva venda, NSU, valor em centavos, quem liberou pelo PIN e por que o token não valeu");
            checar(!jFila.ToString().Contains(codigoIngrid, StringComparison.Ordinal),
                "AT-4d o código do WhatsApp NÃO viaja no payload da fila");
            // Tipo com handler mas FORA do SELECT da varredura já aconteceu uma vez
            // (kds_pronto): a linha existia, o handler existia, e o aviso nunca saía
            // do lugar. Aqui a checagem roda a MESMA consulta que a Drenagem roda.
            var selecionavel = cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM outbox WHERE enviado_em IS NULL AND desistido_em IS NULL "
                + $"AND tipo IN ('{string.Join("','", Drenagem.TiposComHandler)}')");
            checar(Drenagem.TiposComHandler.Contains(Autorizacao.TipoNaFila) && selecionavel == 1,
                "AT-4e a linha é SELECIONÁVEL pela varredura da fila (tipo fora do filtro = fila eterna e invisível)");

            // Auditoria e fila nascem JUNTAS ou não nascem: a mesma transação. Um
            // fato auditado que não foi enfileirado é exatamente o buraco de cima.
            cx.Execute("DELETE FROM auditoria; DELETE FROM outbox");
            var estouroTx = "";
            try
            {
                using var txRollback = cx.BeginTransaction();
                Autorizacao.AuditarSemAprovacaoRemota(cx, dPin, op.Id, "venda=142 nsu=000123",
                    pedido, "venda-142", txRollback);
                txRollback.Rollback();
            }
            catch (Exception ex) { estouroTx = $" — e ainda ESTOUROU {ex.GetType().Name}: {ex.Message}"; }
            checar(estouroTx.Length == 0
                   && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM auditoria") == 0
                   && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox") == 0,
                "AT-4f auditoria e fila entram na MESMA transação do estorno (rollback não deixa fato órfão)"
                + estouroTx);
            cx.Execute("DELETE FROM auditoria; DELETE FROM outbox");

            // ── 4. TOKEN VÁLIDO ⇒ AUTORIZA E DIZ QUEM APROVOU ───────────────
            cx.Execute("DELETE FROM auditoria");
            tela = new TelaFalsa
            {
                PinDevolve = sup,
                AoPedirCodigo = (p, _) => new RespostaCodigo(AcaoCodigo.Confirmar, CodigoDe(fake, p)),
            };
            var pedidoA = PedidoDe("paygo-A", "000200", 1200, 200);
            var dTok = await Autorizacao.ResolverAsync(cx, cli, pedidoA, op, tela, CancellationToken.None);
            checar(dTok.Via == ViaAutorizacao.Token && dTok.Autorizado
                   && (dTok.AprovadoPor ?? "").Contains("Ingrid") && !string.IsNullOrWhiteSpace(dTok.TokenId)
                   && tela.VezesPediuPin == 0,
                "AT-7 código certo autoriza e a auditoria sabe QUEM aprovou (não 'alguém aprovou')");

            Autorizacao.AuditarSemAprovacaoRemota(cx, dTok, op.Id, "venda=200", pedidoA, "venda-200");
            Caixa.Auditar(cx, null, "tef_estorno", op.Id, dTok.Autorizador,
                "venda=200 nsu=000200" + Autorizacao.Trilha(dTok));
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM auditoria WHERE evento = @E",
                       new { E = Autorizacao.EventoSemAprovacaoRemota }) == 0,
                "AT-8 estorno aprovado por token NÃO entra na lista dos que escaparam");
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo = @T",
                       new { T = Autorizacao.TipoNaFila }) == 0,
                "AT-8b ...e também não enfileira nada para a nuvem (a fila é só dos que escaparam)");
            var detalheTok = cx.ExecuteScalar<string>(
                "SELECT detalhe FROM auditoria WHERE evento = 'tef_estorno' ORDER BY id DESC LIMIT 1") ?? "";
            checar(detalheTok.Contains("Ingrid") && dTok.TokenId is { Length: >= 8 } tid && detalheTok.Contains(tid[..8]),
                "AT-9 a linha do estorno guarda o nome de quem aprovou e o id do token");

            var codigoUsado = CodigoDe(fake, new RespostaSolicitacao(true, true, null, dTok.TokenId, null, 0, 0, 0,
                Array.Empty<DestinatarioAutorizacao>()));
            checar(cx.ExecuteScalar<int>(
                       "SELECT COUNT(*) FROM auditoria WHERE detalhe LIKE '%' || @C || '%'",
                       new { C = codigoUsado }) == 0,
                "AT-10 o código digitado NUNCA é gravado na auditoria");

            // ── 5. TOKEN DE OUTRO ESTORNO NÃO SERVE ─────────────────────────
            var pedidoB = PedidoDe("paygo-B", "000201", 120000, 201);
            checar(pedidoA.Referencia != pedidoB.Referencia,
                "AT-11 cada estorno tem referência própria (NSU + valor + venda)");

            var rA = await cli.SolicitarAsync(pedidoA, CancellationToken.None);
            var codA = CodigoDe(fake, rA);
            var cruzado = await cli.ValidarAsync(rA.Id ?? "", codA, pedidoB.Referencia, CancellationToken.None);
            checar(!cruzado.Ok && cruzado.Motivo == "referencia_divergente",
                "AT-12 token do estorno de R$ 12,00 NÃO autoriza o estorno de R$ 1.200,00");
            var certo = await cli.ValidarAsync(rA.Id ?? "", codA, pedidoA.Referencia, CancellationToken.None);
            checar(certo.Ok,
                "AT-13 ...e a recusa cruzada não queimou o token legítimo do estorno certo");

            // ── 6. O OPERADOR NUNCA FICA PRESO ──────────────────────────────
            // 5 chutes errados queimam o token: a tela oferece o PIN e a venda anda.
            tela = new TelaFalsa
            {
                PinDevolve = sup,
                AoPedirCodigo = (_, _) => new RespostaCodigo(AcaoCodigo.Confirmar, "000000"),
                AoFalhar = _ => EscolhaAposFalha.Pin,
            };
            var pedidoC = PedidoDe("paygo-C", "000202", 800, 202);
            var dQueimou = await Autorizacao.ResolverAsync(cx, cli, pedidoC, op, tela, CancellationToken.None);
            checar(dQueimou.Via == ViaAutorizacao.Pin && dQueimou.Autorizado && tela.VezesPediuPin == 1,
                "AT-14 token queimado por erro de digitação não prende o operador: sobra o PIN");
            checar(dQueimou.SemAprovacaoRemota && dQueimou.Motivo.Length > 0,
                "AT-15 ...e esse estorno também entra na lista dos que saíram sem aprovação remota");

            // Desistir é desistir: ninguém autoriza nada.
            tela = new TelaFalsa
            {
                PinDevolve = sup,
                AoPedirCodigo = (_, _) => new RespostaCodigo(AcaoCodigo.Cancelar, null),
            };
            var dDesistiu = await Autorizacao.ResolverAsync(cx, cli,
                PedidoDe("paygo-D", "000203", 900, 203), op, tela, CancellationToken.None);
            checar(!dDesistiu.Autorizado && dDesistiu.Via == ViaAutorizacao.Recusada && tela.VezesPediuPin == 0,
                "AT-16 operador que desiste da tela do código não estorna nada");

            // "Não recebi": pede outro código, e o novo vale.
            var vezes = 0;
            tela = new TelaFalsa
            {
                PinDevolve = sup,
                AoPedirCodigo = (p, _) => ++vezes == 1
                    ? new RespostaCodigo(AcaoCodigo.NovoCodigo, null)
                    : new RespostaCodigo(AcaoCodigo.Confirmar, CodigoDe(fake, p)),
            };
            fake.MaxSolicitacoes = 99;
            var dReenvio = await Autorizacao.ResolverAsync(cx, cli,
                PedidoDe("paygo-E", "000204", 1000, 204), op, tela, CancellationToken.None);
            checar(dReenvio.Via == ViaAutorizacao.Token && vezes == 2,
                "AT-17 'não recebi' pede um código novo e o novo autoriza");

            // Recusa definitiva da edge (429/sem aprovadores) não faz o caixa esperar 15 s.
            fake.MaxSolicitacoes = 0;
            tela = new TelaFalsa { PinDevolve = sup };
            var cronRl = Stopwatch.StartNew();
            var dRl = await Autorizacao.ResolverAsync(cx, cli,
                PedidoDe("paygo-F", "000205", 700, 205), op, tela, CancellationToken.None);
            cronRl.Stop();
            fake.MaxSolicitacoes = 99;
            checar(dRl.Via == ViaAutorizacao.Pin && tela.VezesPediuCodigo == 0 && cronRl.ElapsedMilliseconds < 3000,
                "AT-18 recusa definitiva da nuvem cai para o PIN NA HORA (não espera o tempo todo)");

            // PIN errado na saída de emergência: não autoriza (é a regra de hoje).
            tela = new TelaFalsa { PinDevolve = null };
            var dPinErrado = await Autorizacao.ResolverAsync(cx, morta,
                PedidoDe("paygo-G", "000206", 600, 206), op, tela, CancellationToken.None);
            checar(!dPinErrado.Autorizado && dPinErrado.Via == ViaAutorizacao.Recusada,
                "AT-19 PIN que não confere continua não autorizando estorno nenhum");

            // ── 6b. O TOKEN FANTASMA ────────────────────────────────────────
            //
            // O caixa espera 15 s e cai para o PIN. A edge NÃO SABE DISSO: ela
            // termina o trabalho — cria a linha do token e manda o WhatsApp —
            // depois de o caixa já ter desistido. O estrago não é teórico:
            //
            //  · o celular da Ingrid acende às 22h para um estorno que já saiu
            //    pelo PIN, e um aviso que não corresponde a nada é um aviso que
            //    se aprende a ignorar;
            //  · a linha do fantasma OCUPA UMA DAS 5 VAGAS de 10 minutos daquele
            //    caixa, porque o rate limit conta LINHAS CRIADAS na janela —
            //    queimadas ou não.
            //
            // A edge não tem como adivinhar que o caixa desistiu. O que ela pode
            // é ser IDEMPOTENTE: a segunda tentativa do MESMO estorno reaproveita
            // o fantasma em vez de criar outro, e o código que já está no celular
            // da Ingrid passa a valer. O desperdício vira o token do estorno.
            {
                var pedidoSO = PedidoDe("paygo-SO6", "000207", 4300, 207);

                // 15 s e 5 s aqui viram 500 ms e 1200 ms: a suíte não pode parar
                // meio minuto para provar uma corrida de relógio.
                fake.AtrasoMs = 1200;
                var impaciente = new ClienteAutorizacao(fake.Url, "k", TimeSpan.FromMilliseconds(500));
                var criadosAntes = fake.TokensCriados;
                var msgAntes = fake.Mensagens;
                tela = new TelaFalsa { PinDevolve = sup };
                var dFantasma = await Autorizacao.ResolverAsync(cx, impaciente, pedidoSO, op, tela,
                    CancellationToken.None);
                fake.AtrasoMs = 0;
                await EsperarAsync(() => fake.TokensCriados > criadosAntes);

                checar(dFantasma.Via == ViaAutorizacao.Pin && tela.VezesPediuCodigo == 0
                       && fake.TokensCriados == criadosAntes + 1 && fake.Mensagens == msgAntes + 2,
                    "SO-6a nuvem lenta: o caixa sai pelo PIN, mas o token JÁ nasceu e o WhatsApp JÁ saiu "
                    + $"(tokens criados {criadosAntes}→{fake.TokensCriados}, mensagens {msgAntes}→{fake.Mensagens})");

                // O código que a Ingrid recebeu há 20 segundos continua no celular
                // dela. O id, o caixa nunca chegou a ver.
                var idFantasma = fake.TokenVivoDe(Terminal, pedidoSO.Referencia);
                var noCelularDaIngrid = idFantasma is not null && fake.Codigos.TryGetValue(idFantasma, out var lst)
                    ? lst.First(c => c.Scope == "manager").Codigo
                    : "000000";

                var criadosFantasma = fake.TokensCriados;
                var msgFantasma = fake.Mensagens;
                RespostaSolicitacao? naTela = null;
                tela = new TelaFalsa
                {
                    PinDevolve = sup,
                    AoPedirCodigo = (p, _) => { naTela = p; return new RespostaCodigo(AcaoCodigo.Confirmar, noCelularDaIngrid); },
                };
                var dRetomada = await Autorizacao.ResolverAsync(cx, cli, pedidoSO, op, tela, CancellationToken.None);

                checar(dRetomada.Via == ViaAutorizacao.Token && idFantasma is not null
                       && dRetomada.TokenId == idFantasma,
                    "SO-6b a segunda tentativa do MESMO estorno reaproveita o token fantasma "
                    + $"(fantasma {idFantasma ?? "(nenhum)"} · caixa recebeu {dRetomada.TokenId ?? "(nada)"})");
                checar(fake.TokensCriados == criadosFantasma,
                    "SO-6c ...sem gastar outra das 5 vagas de 10 min daquele caixa "
                    + $"(tokens criados {criadosFantasma}→{fake.TokensCriados})");
                checar(fake.Mensagens == msgFantasma,
                    "SO-6d ...e sem acender o celular da Ingrid de novo "
                    + $"(mensagens {msgFantasma}→{fake.Mensagens})");
                checar(naTela is { Reaproveitado: true },
                    "SO-6e a tela sabe que o código é o que JÁ foi mandado (senão o operador espera "
                    + "uma mensagem que não vem)");

                // O relógio da tela é montado com `validade_segundos`. Num token
                // reaproveitado ele tem que ser o que SOBRA — devolver os 5 minutos
                // inteiros faz a tela prometer tempo que não existe, e o operador
                // descobre no meio da digitação com o cliente esperando.
                fake.ValidadeSegundos = 60;
                var pedidoRel = PedidoDe("paygo-SO6b", "000208", 500, 208);
                var rel1 = await cli.SolicitarAsync(pedidoRel, CancellationToken.None);
                await Task.Delay(1100);
                var rel2 = await cli.SolicitarAsync(pedidoRel, CancellationToken.None);
                fake.ValidadeSegundos = 300;
                checar(rel2.Ok && rel2.Reaproveitado && rel2.Id == rel1.Id
                       && rel2.ValidadeSegundos > 0 && rel2.ValidadeSegundos < 60,
                    "SO-6f o token reaproveitado devolve o tempo que SOBRA, não a validade cheia "
                    + $"(sobrou {rel2.ValidadeSegundos}s de {fake.ValidadeSegundos}s)");

                // E o contra-exemplo que decide o desenho: "não recebi" TEM que
                // mandar mensagem nova, senão o botão vira enfeite e o operador
                // clica até sobrar só o PIN.
                var criadosReenvio = fake.TokensCriados;
                var msgReenvio = fake.Mensagens;
                var rReenvio = await cli.SolicitarAsync(pedidoRel, CancellationToken.None, reenviar: true);
                checar(rReenvio.Ok && !rReenvio.Reaproveitado && rReenvio.Id != rel1.Id
                       && fake.TokensCriados == criadosReenvio + 1 && fake.Mensagens == msgReenvio + 2,
                    "SO-6g 'não recebi' continua queimando o anterior e mandando código NOVO "
                    + $"(tokens criados {criadosReenvio}→{fake.TokensCriados}, mensagens {msgReenvio}→{fake.Mensagens})");
            }

            // ── 7. TUDO QUE TOCA JANELA VOLTA PARA A THREAD DA TELA ─────────
            //
            // O buraco que a TelaFalsa não enxerga. No WPF, uma janela só pode ser
            // tocada pela thread que a criou: `_dono.IsEnabled = true` no
            // Espera.Dispose, ou um `new Window` de outra thread, é
            // InvalidOperationException na hora. E o caminho do estorno não tem
            // rede embaixo — EstornarTefAsync não tem catch, o `async void`
            // MenuTef tem finally mas não catch, e o App não tem
            // DispatcherUnhandledException: exceção ali ENCERRA O PROCESSO com o
            // cliente no balcão, sem nem oferecer o PIN.
            //
            // Um `await ... .ConfigureAwait(false)` no meio da máquina de estados
            // faz exatamente isso: a continuação (o Dispose do `using`, a próxima
            // tela) volta numa thread do pool. Todos os testes acima ficam verdes
            // porque a TelaFalsa aceita qualquer thread e a homologação retorna
            // antes de criar Espera.
            //
            // Aqui a nuvem é HTTP de verdade (FakeAutorizacao), então nenhum await
            // completa síncrono — é o cenário da loja.
            {
                using var ui = new ThreadDeTela();
                fake.MaxSolicitacoes = 99;

                // 7a. Caminho feliz do token: Aguardando ×2 (+ os dois Dispose) e PedirCodigoAsync.
                var telaUi = new TelaQueAnotaThread(ui.Id) { PinDevolve = sup };
                telaUi.AoPedirCodigo = (p, _) => new RespostaCodigo(AcaoCodigo.Confirmar, CodigoDe(fake, p));
                var pedidoUi = PedidoDe("paygo-UI", "000301", 2500, 301);
                DesfechoAutorizacao dUi;
                string estouroUi = "";
                try
                {
                    dUi = await ui.ExecutarAsync(() =>
                        Autorizacao.ResolverAsync(cx, cli, pedidoUi, op, telaUi, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    estouroUi = $" — e ainda ESCAPOU {ex.GetType().Name}: {ex.Message}";
                    dUi = new DesfechoAutorizacao(ViaAutorizacao.Recusada, null, null, null, "estourou");
                }
                checar(dUi.Via == ViaAutorizacao.Token
                       && telaUi.VezesAguardou == 2 && telaUi.VezesFechouEspera == 2
                       && telaUi.ForaDaThreadDaTela.Count == 0,
                    "UI-1 no caminho do token, toda chamada de tela e todo fechamento de espera cai na thread da tela"
                    + (telaUi.ForaDaThreadDaTela.Count > 0
                        ? " — FORA DELA: " + string.Join(" · ", telaUi.ForaDaThreadDaTela) : "")
                    + estouroUi);

                // 7b. Caminho da SAÍDA DO PIN: código sempre errado até o token
                // queimar, EscolherAposFalhaAsync e PedirPinAsync. É o caminho que
                // o dono garantiu que existe — e é o que o crash apagava.
                var telaPin = new TelaQueAnotaThread(ui.Id)
                {
                    PinDevolve = sup,
                    AoFalhar = _ => EscolhaAposFalha.Pin,
                };
                telaPin.AoPedirCodigo = (p, _) => new RespostaCodigo(AcaoCodigo.Confirmar, ErradoDe(fake, p));
                DesfechoAutorizacao dUiPin;
                string estouroPin = "";
                try
                {
                    dUiPin = await ui.ExecutarAsync(() => Autorizacao.ResolverAsync(cx, cli,
                        PedidoDe("paygo-UI2", "000302", 900, 302), op, telaPin, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    estouroPin = $" — e ainda ESCAPOU {ex.GetType().Name}: {ex.Message}";
                    dUiPin = new DesfechoAutorizacao(ViaAutorizacao.Recusada, null, null, null, "estourou");
                }
                checar(dUiPin.Via == ViaAutorizacao.Pin && telaPin.VezesPediuPin == 1
                       && telaPin.ForaDaThreadDaTela.Count == 0,
                    "UI-2 a saída do PIN (código queimado → escolha → PIN) também roda inteira na thread da tela"
                    + (telaPin.ForaDaThreadDaTela.Count > 0
                        ? " — FORA DELA: " + string.Join(" · ", telaPin.ForaDaThreadDaTela) : "")
                    + estouroPin);

                // 7c. A causa, em vez do sintoma: ConfigureAwait(false) num await
                // cuja continuação toca a tela é o que joga a continuação no pool.
                // Dentro do ClienteAutorizacao ele é CERTO (ninguém ali encosta em
                // janela) — por isso a varredura é só da máquina de estados.
                var fonteAut = Fonte("Pdv.Nucleo", "Autorizacao.cs") ?? "";
                var iRes = fonteAut.IndexOf("public static async Task<DesfechoAutorizacao> ResolverAsync", StringComparison.Ordinal);
                var iFim = fonteAut.IndexOf("private static string MotivoDaSolicitacao", StringComparison.Ordinal);
                var corpoRes = iRes >= 0 && iFim > iRes ? fonteAut[iRes..iFim] : "";
                checar(corpoRes.Length > 0 && !corpoRes.Contains("ConfigureAwait(false)", StringComparison.Ordinal),
                    "UI-3 a máquina de estados não usa ConfigureAwait(false) (a continuação tem que voltar ao Dispatcher)");
            }

            // ── 8. A TELA DO ESTORNO REALMENTE USA ISTO ─────────────────────
            // Tudo acima pode estar verde com a tela ainda chamando o PIN direto.
            // EstornarTefAsync é code-behind de WPF (não dá para instanciar num
            // teste), então se confere a fonte — mesma técnica da trava de
            // instância única.
            {
                var fonte = Fonte("Telas", "Venda.xaml.cs") ?? "";
                checar(fonte.Length > 0, "TL-1 achei a fonte da tela de venda para conferir o estorno");

                var i = fonte.IndexOf("private async Task EstornarTefAsync", StringComparison.Ordinal);
                var fim = i < 0 ? -1 : fonte.IndexOf("\n    /// <summary>", i, StringComparison.Ordinal);
                var corpo = i < 0 || fim < 0 ? "" : fonte[i..fim];

                checar(corpo.Contains("Autorizacao.ResolverAsync", StringComparison.Ordinal),
                    "TL-2 o estorno passa pela autorização por token (não mais pelo PIN direto)");
                checar(corpo.Length > 0 && !corpo.Contains("PedirSenha.Mostrar", StringComparison.Ordinal),
                    "TL-3 não sobrou nenhuma porta lateral pedindo PIN dentro do estorno");
                checar(corpo.Contains("Autorizacao.Referencia(", StringComparison.Ordinal)
                       && corpo.Contains("l.tef_id", StringComparison.Ordinal),
                    "TL-4 a referência mandada à nuvem é a daquele estorno (transação + NSU + valor + venda)");

                var confirma = corpo.IndexOf("Dialogo.Confirmar", StringComparison.Ordinal);
                var autoriza = corpo.IndexOf("Autorizacao.ResolverAsync", StringComparison.Ordinal);
                checar(confirma >= 0 && autoriza > confirma,
                    "TL-5 o WhatsApp só sai depois de o operador confirmar (não a cada estorno aberto e abandonado)");

                var cnc = corpo.IndexOf("if (!d.Pago)", StringComparison.Ordinal);
                var linhaPropria = corpo.IndexOf("Autorizacao.AuditarSemAprovacaoRemota", StringComparison.Ordinal);
                checar(linhaPropria > 0 && cnc > 0 && linhaPropria > cnc,
                    "TL-6 a linha 'escapou do token' só é gravada com o estorno CONSUMADO (depois do CNC)");
                // Na LINHA DO ESTORNO CONSUMADO, não em qualquer outra: o detalhe do
                // `tef_estorno` é onde o dono lê quem aprovou.
                var dec = corpo.IndexOf("var detalhe = $\"", StringComparison.Ordinal);
                var linhaDetalhe = dec < 0 ? "" : corpo[dec..corpo.IndexOf('\n', dec)];
                checar(linhaDetalhe.Contains("{trilha}", StringComparison.Ordinal),
                    "TL-7 a linha normal do estorno carrega a trilha (quem aprovou, ou o aviso)");

                // O ESTORNO NÃO PODE DERRUBAR O CAIXA. MenuTef é `async void` com
                // finally mas sem catch, e não existe DispatcherUnhandledException
                // no App: sem catch AQUI, exceção na autorização encerra o Pdv.exe
                // com o cliente no balcão — e sem oferecer o PIN.
                var iAut = corpo.IndexOf("Autorizacao.ResolverAsync", StringComparison.Ordinal);
                var iCatch = corpo.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
                var iPinEmerg = corpo.IndexOf("PedirPinAsync", StringComparison.Ordinal);
                checar(iAut > 0 && iCatch > iAut && iPinEmerg > iCatch,
                    "TL-8 falha na autorização não mata o PDV: tem catch e cai para o PIN do supervisor");

                // O aviso de espera é fechado pelo Dispose de um `using` no meio de
                // awaits: se a continuação voltar fora da thread da UI, ele estoura.
                var fonteEspera = Fonte("Telas", "PedirCodigo.cs") ?? "";
                var iEsp = fonteEspera.IndexOf("public sealed class Espera", StringComparison.Ordinal);
                var iPed = fonteEspera.IndexOf("public static class PedirCodigo", StringComparison.Ordinal);
                var corpoEspera = iEsp >= 0 && iPed > iEsp ? fonteEspera[iEsp..iPed] : "";
                checar(corpoEspera.Contains("Dispatcher.Invoke", StringComparison.Ordinal),
                    "TL-9 o Dispose do aviso de espera marshala para a thread da UI (não estoura se vier do pool)");

                // Token reaproveitado significa que NÃO saiu mensagem nova. Se a
                // tela não disser isso, o operador fica olhando para o celular
                // esperando um WhatsApp que não vem — e aperta "não recebi" sem
                // precisar, gastando uma vaga do balde e acendendo o celular da
                // Ingrid pela segunda vez, que é justamente o que o
                // reaproveitamento economizou.
                checar(fonteEspera.Contains("pedido.Reaproveitado", StringComparison.Ordinal)
                       && fonteEspera.Contains("não saiu mensagem nova", StringComparison.Ordinal),
                    "TL-10 a tela do código avisa quando o token é o que JÁ tinha sido mandado");
            }

            // ── 9. O QUE O .EXE NÃO PODE CARREGAR ───────────────────────────
            // O binário fica numa loja: quem copiar o arquivo tem a chave que
            // estiver dentro dele. Com service_role, isso seria o banco inteiro.
            {
                // Só o que ENTRA no .exe: raiz + Telas + Pdv.Nucleo (o Pdv.csproj exclui
                // Pdv.Testes e Pdv.Instalador). Varrer o repositório inteiro faria este
                // teste achar a si mesmo e chamar de vazamento.
                var raiz = Raiz();
                var doExe = raiz is null ? Array.Empty<string>() : Directory
                    .EnumerateFiles(raiz, "*.cs", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.EnumerateFiles(Path.Combine(raiz, "Telas"), "*.cs", SearchOption.AllDirectories))
                    .Concat(Directory.EnumerateFiles(Path.Combine(raiz, "Pdv.Nucleo"), "*.cs", SearchOption.AllDirectories))
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                             && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    .ToArray();
                var comSegredo = doExe
                    .Where(f => File.ReadAllText(f) is var t
                             && (t.Contains("\"role\":\"service_role\"") || t.Contains("SUPABASE_SERVICE_ROLE")
                                 || t.Split('"').Any(pedaco => PapelDoJwt(pedaco) == "service_role")))
                    .ToList();
                checar(raiz is not null && doExe.Length > 0 && comSegredo.Count == 0,
                    "SEG-1 nenhum fonte que entra no .exe carrega chave service_role" +
                    (comSegredo.Count > 0 ? " — ACHEI EM: " + string.Join(", ", comSegredo) : ""));

                checar(PapelDoJwt(Nuvem.AnonKey) == "anon",
                    "SEG-2 a chave embutida no .exe é a pública (role=anon), a mesma que a edge espera");
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
