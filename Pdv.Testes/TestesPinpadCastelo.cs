using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A INSTALAÇÃO DO PAYGO NA LOJA CASTELO (14/09/2026).
///
/// O dono tocou em "Instalar ponto de captura" e o caixa ficou mais de cinco minutos preso,
/// duas vezes, até ele matar o programa. A tela mostrou "TEF não aceitou o dado 32514:
/// PWRET_PPNOTFOUND". O log da biblioteca contou o resto:
///  · o pinpad nunca respondeu ao primeiro sinal do protocolo (CAN sem EOT, o dia inteiro);
///  · a biblioteca devolveu -2489 no PW_iAddParam(0x7F02) e o caixa entrou assim mesmo na
///    chamada longa, que ficou minutos sem voltar;
///  · 12 de 22 chamadas nativas rodavam na thread da TELA, porque o "sai da tela" voltava para
///    ela (await sem ConfigureAwait(false));
///  · a segunda tentativa esperava calada, no semáforo, a primeira sair da chamada presa.
///
/// SE ESTES TESTES QUEBRAREM, volta a loja com o caixa congelado e sem saída.
/// </summary>
public static class TestesPinpadCastelo
{
    private static readonly OpcoesPGWebLib Opcoes = new("MMFood", "1.0.10", "American Day", RedeCartao: "REDE", RedePix: "PIX ITAU");

    private static ProvedorPGWebLib Provedor(FakePGWebLib f, OpcoesPGWebLib? opcoes = null,
        Func<string, CancellationToken, Task<ResultadoTestePinpad>>? conferir = null, List<string>? auditoria = null,
        Func<PwGetData, CancellationToken, Task<string?>>? perguntar = null, List<TransacaoPayGo>? guardadas = null,
        int esperaOcupadoMs = 3_000, int tetoExecMs = 2_000)
        => new(f, TestesPGWebLib.PastaTeste, opcoes ?? Opcoes)
        {
            IntervaloPollMs = 5,
            TempoMaxExecMs = tetoExecMs,
            TempoMaxCapturaMs = 2_000,
            TempoPerguntaMs = 1_000,
            EsperaOcupadoMs = esperaOcupadoMs,
            ConferirPinpad = conferir,
            Perguntar = perguntar,
            Guardar = t => { lock (guardadas ?? new List<TransacaoPayGo>()) guardadas?.Add(t); return true; },
            Auditar = auditoria is null ? null : a => { lock (auditoria) auditoria.Add(a); },
        };

    public static void Rodar(Action<bool, string> checar)
    {
        Normalizacao(checar);
        TesteDoPinpad(checar);
        TesteComPrazo(checar);
        ForaDaTela(checar);
        InstalacaoTestaAntes(checar);
        SemPinpadNaoEntraNaChamadaLonga(checar);
        DadoRecusadoNaoVoltaSozinho(checar);
        OcupadoNaoEsperaCalado(checar);
        VigiaECancelar(checar);
        PortaNovaChega(checar);
        Textos(checar);
        Fontes(checar);
    }

    // ── a porta como a biblioteca quer ──────────────────────────────────────
    private static void Normalizacao(Action<bool, string> checar)
    {
        foreach (var (entrada, esperado) in new[] { ("COM2", "2"), ("com03", "3"), (" 3 ", "3"), ("", "0"), ("0", "0"), ("COM", "0"), ("12", "12") })
            checar(PortaDoPinpad.Normalizar(entrada) == esperado, $"porta '{entrada}' vira '{esperado}' ({PortaDoPinpad.Normalizar(entrada)})");
        checar(PortaDoPinpad.Normalizar(null) == "0", "porta nula é automática");
        checar(ConfigPGWebLib.Opcoes(k => k == ConfigPGWebLib.ChavePortaPinpad ? "COM2" : null, "1").PortaPinpad == "2",
            "a config com 'COM2' chega à biblioteca como '2'");

        var portas = new List<PortaSerial> { new("COM1", "Porta de comunicação"), new("COM3", "Gertec PIN Pad PPC") };
        var ops = PortaDoPinpad.Opcoes(portas, "5");
        checar(ops[0].Valor == "" && ops[0].Rotulo.StartsWith("Automática", StringComparison.Ordinal), "a lista de portas começa em Automática");
        checar(ops.Any(o => o.Valor == "3" && o.Rotulo == "COM3 · Gertec PIN Pad PPC"), "e mostra a porta com o nome do aparelho");
        checar(ops.Any(o => o.Valor == "5" && o.Rotulo.Contains("não encontrada")), "porta gravada que sumiu continua na lista, marcada");
        checar(PortaDoPinpad.Indice(ops, "COM3") == ops.FindIndexDe("3") && PortaDoPinpad.Indice(ops, "") == 0 && PortaDoPinpad.Indice(ops, "9") == 0,
            "a porta gravada é a selecionada; sem casar, Automática");
    }

    private static int FindIndexDe(this IReadOnlyList<PortaDoPinpad.Opcao> ops, string valor)
    {
        for (var i = 0; i < ops.Count; i++) if (ops[i].Valor == valor) return i;
        return -1;
    }

    // ── o teste do pinpad, com portas de mentira ────────────────────────────
    private sealed class SerialDeMentira : IAcessoSerial
    {
        public List<PortaSerial> Portas { get; } = new();
        public Dictionary<string, AberturaPorta> Aberturas { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Respondem { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Programas { get; } = new();
        public ConcurrentQueue<string> Abertas { get; } = new();
        public ConcurrentQueue<(string Com, byte Enviado, byte Esperado)> Sinais { get; } = new();
        public int Fechadas;
        public int TravarMs;

        public IReadOnlyList<PortaSerial> Listar() => Portas;

        public AberturaPorta Abrir(string com, out IPortaAberta? porta)
        {
            porta = null;
            var ab = Aberturas.GetValueOrDefault(com, AberturaPorta.Abriu);
            if (ab != AberturaPorta.Abriu) return ab;
            Abertas.Enqueue(com);
            porta = new Aberta(this, com);
            return ab;
        }

        public IReadOnlyList<string> ProgramasDeMaquininhaAbertos() => Programas;

        private sealed class Aberta : IPortaAberta
        {
            private readonly SerialDeMentira _d; private readonly string _com;
            public Aberta(SerialDeMentira d, string com) { _d = d; _com = com; }
            public bool EnviarEsperar(byte enviar, byte esperado, int prazoMs)
            {
                _d.Sinais.Enqueue((_com, enviar, esperado));
                if (_d.TravarMs > 0) Thread.Sleep(_d.TravarMs);
                return _d.Respondem.Contains(_com);
            }
            public void Dispose() => Interlocked.Increment(ref _d.Fechadas);
        }
    }

    private static void TesteDoPinpad(Action<bool, string> checar)
    {
        // A Castelo: uma porta só, a COM2, sem nome de pinpad, e o pinpad mudo.
        var castelo = new SerialDeMentira();
        castelo.Portas.Add(new PortaSerial("COM2", ""));
        var r = TestePinpad.Testar(castelo, "0", bibliotecaNoCaixa: false, prazoRespostaMs: 10);
        checar(r.Situacao == SituacaoPinpad.NaoRespondeu && !r.Ok
               && r.Frase == "O pinpad está na COM2 mas não respondeu. Tire o cabo USB, espere 10 segundos e ligue de novo.",
            "Castelo: COM2 muda vira a frase do cabo (" + r.Frase + ")");
        checar(castelo.Sinais.All(s => s.Enviado == 0x18 && s.Esperado == 0x04) && castelo.Sinais.Count == 1,
            "o teste manda CAN e espera EOT, como o primeiro passo da biblioteca");
        checar(castelo.Fechadas == castelo.Abertas.Count, "toda porta aberta pelo teste é fechada");

        // Pinpad bom na COM3, porta da placa-mãe na COM1: acha sozinho e nem abre a COM1.
        var loja = new SerialDeMentira();
        loja.Portas.Add(new PortaSerial("COM1", "Porta de comunicação"));
        loja.Portas.Add(new PortaSerial("COM3", "Gertec PIN Pad PPC"));
        loja.Respondem.Add("COM3");
        r = TestePinpad.Testar(loja, "", false, 10);
        checar(r.Ok && r.Situacao == SituacaoPinpad.Respondeu && r.Frase == "Gertec PIN Pad PPC respondeu na COM3." && r.Numero == "3",
            "pinpad que responde: uma linha com o aparelho e a porta (" + r.Frase + ")");
        checar(loja.Abertas.SequenceEqual(new[] { "COM3" }), "a porta que parece pinpad é testada primeiro, e a da placa-mãe nem é aberta");

        var ingenico = new SerialDeMentira();
        ingenico.Portas.Add(new PortaSerial("COM7", "Ingenico iPP320"));
        ingenico.Respondem.Add("COM7");
        checar(TestePinpad.Testar(ingenico, "0", false, 10).Frase == "Pinpad Ingenico iPP320 respondeu na COM7.", "nome sem 'pinpad' ganha a palavra na frente");

        // Ocupada pelo PayGo Windows.
        var ocupada = new SerialDeMentira();
        ocupada.Portas.Add(new PortaSerial("COM3", "Gertec PIN Pad PPC"));
        ocupada.Aberturas["COM3"] = AberturaPorta.Ocupada;
        ocupada.Programas.Add("PayGo Windows");
        r = TestePinpad.Testar(ocupada, "0", false, 10);
        checar(r.Situacao == SituacaoPinpad.Ocupada && !r.Ok && r.Programa == "PayGo Windows"
               && r.Frase == "A COM3 está ocupada pelo programa PayGo Windows. Feche o PayGo Windows e toque em Testar de novo.",
            "porta ocupada diz o programa e o que fazer (" + r.Frase + ")");

        ocupada.Programas.Clear();
        r = TestePinpad.Testar(ocupada, "0", bibliotecaNoCaixa: true, 10);
        checar(r.Situacao == SituacaoPinpad.EmUsoPeloCaixa && r.Ok, "ocupada sem programa conhecido e com a biblioteca no caixa: é a própria biblioteca, pode seguir");
        r = TestePinpad.Testar(ocupada, "0", bibliotecaNoCaixa: false, 10);
        checar(r.Situacao == SituacaoPinpad.Ocupada && r.Programa is null && r.Frase.Contains("Gertec") && r.Frase.Contains("PayGo Windows"),
            "ocupada sem saber por quem: manda fechar os dois suspeitos (" + r.Frase + ")");

        var nada = new SerialDeMentira();
        r = TestePinpad.Testar(nada, "0", false, 10);
        checar(r.Situacao == SituacaoPinpad.NenhumPinpad && r.Frase.StartsWith("Nenhum pinpad ligado neste computador.", StringComparison.Ordinal),
            "sem porta nenhuma: 'Nenhum pinpad ligado neste computador.'");

        r = TestePinpad.Testar(loja.ComPortasApenas(), "COM5", false, 10);
        checar(r.Situacao == SituacaoPinpad.PortaNaoExiste && r.Frase.Contains("COM5") && r.Frase.Contains("não existe") && r.Frase.Contains("COM1, COM3"),
            "porta escolhida que não existe: diz quais existem (" + r.Frase + ")");

        var escolhida = new SerialDeMentira();
        escolhida.Portas.Add(new PortaSerial("COM1", ""));
        escolhida.Portas.Add(new PortaSerial("COM3", ""));
        r = TestePinpad.Testar(escolhida, "3", false, 10);
        checar(escolhida.Abertas.All(c => c == "COM3") && escolhida.Sinais.Count == 2 && r.Situacao == SituacaoPinpad.NaoRespondeu,
            "porta escolhida: só ela é aberta, com duas chances");

        var bt = new SerialDeMentira();
        bt.Portas.Add(new PortaSerial("COM4", "Standard Serial over Bluetooth link"));
        r = TestePinpad.Testar(bt, "0", false, 10);
        checar(bt.Abertas.IsEmpty && r.Situacao == SituacaoPinpad.NenhumPinpad, "porta Bluetooth nunca é aberta pelo teste (abrir tenta conectar e demora)");

        var duas = new SerialDeMentira();
        duas.Portas.Add(new PortaSerial("COM1", ""));
        duas.Portas.Add(new PortaSerial("COM2", ""));
        r = TestePinpad.Testar(duas, "0", false, 10);
        checar(r.Situacao == SituacaoPinpad.NaoRespondeu && r.Frase.Contains("COM1, COM2") && r.Porta is null,
            "duas portas mudas sem nome: não chuta qual é o pinpad (" + r.Frase + ")");

        var sumiu = new SerialDeMentira();
        sumiu.Portas.Add(new PortaSerial("COM8", ""));
        sumiu.Aberturas["COM8"] = AberturaPorta.NaoExiste;
        checar(TestePinpad.Testar(sumiu, "0", false, 10).Situacao == SituacaoPinpad.NenhumPinpad, "porta que some na hora de abrir: nenhum pinpad");

        checar(ProgramasDeMaquininha.Reconhecer("PayGoLauncher") == "PayGo Windows" && ProgramasDeMaquininha.Reconhecer("ControlPay") == "ControlPay"
               && ProgramasDeMaquininha.Reconhecer("Pdv") is null && ProgramasDeMaquininha.Reconhecer("explorer") is null,
            "reconhece os programas de maquininha pelo nome do processo, e não o próprio caixa");
        checar(ProgramasDeMaquininha.PodeFechar("PayGo Windows") && ProgramasDeMaquininha.PodeFechar("Gertec") && !ProgramasDeMaquininha.PodeFechar("SiTef"),
            "o caixa só oferece fechar PayGo, ControlPay e Gertec; serviço de TEF de outra empresa não");
        checar(ProgramasDeMaquininha.Nomes(new[] { "PayGo", "PayGoLauncher", "notepad" }).SequenceEqual(new[] { "PayGo Windows" }),
            "o mesmo programa em dois processos aparece uma vez");
        checar(SerialWindows.LimparNome("Gertec PIN Pad PPC (COM3)", "COM3") == "Gertec PIN Pad PPC"
               && SerialWindows.LimparNome("@oem12.inf,%usbser%;USB Serial Device (COM8)", "COM8") == "USB Serial Device",
            "o nome do Windows perde o '(COMn)' e o prefixo do driver");

        foreach (var f in new[] { TestePinpad.FraseNenhum, TestePinpad.FrasePrazo, TestePinpad.FraseMudo(new("COM2", "")),
                     TestePinpad.FraseOcupada(new("COM2", ""), null), TestePinpad.FraseOcupada(new("COM2", ""), "Gertec") })
            checar(!f.Contains('—') && !f.Contains('–') && !f.Contains("PWRET_"), "frase do teste do pinpad sem travessão e sem código: " + f);
    }

    private static SerialDeMentira ComPortasApenas(this SerialDeMentira s)
    {
        var n = new SerialDeMentira();
        n.Portas.AddRange(s.Portas);
        return n;
    }

    private static void TesteComPrazo(Action<bool, string> checar)
    {
        // Driver que não respeita o tempo da porta: a tela recebe a resposta no prazo mesmo assim.
        var preso = new SerialDeMentira { TravarMs = 4_000 };
        preso.Portas.Add(new PortaSerial("COM2", "Gertec PIN Pad PPC"));
        var relogio = Stopwatch.StartNew();
        var r = TestePinpad.TestarAsync(preso, "0", false, CancellationToken.None, prazoRespostaMs: 5_000, prazoTotalMs: 300).GetAwaiter().GetResult();
        relogio.Stop();
        checar(r.Situacao == SituacaoPinpad.PassouDoPrazo && r.Frase == TestePinpad.FrasePrazo && relogio.ElapsedMilliseconds < 2_500,
            $"teste do pinpad preso no driver volta no prazo ({relogio.ElapsedMilliseconds} ms): {r.Frase}");
        checar(TestePinpad.PrazoTotalMs <= 10_000, "o prazo do teste é de no máximo 10 s, como o dono pediu");
    }

    // ── nenhuma chamada nativa na thread da tela ────────────────────────────
    /// <summary>O Dispatcher do WPF de pobre: uma thread com fila e SynchronizationContext.</summary>
    private sealed class ThreadDeTela : IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Cb, object? Estado)> _fila = new();
        private readonly ManualResetEventSlim _pronta = new(false);
        public int Id;

        public ThreadDeTela()
        {
            new Thread(Laco) { IsBackground = true, Name = "tela-castelo" }.Start();
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
                try { _fila.Add((d, estado)); } catch (InvalidOperationException) { }
            }
        }

        public T Executar<T>(Func<Task<T>> trabalho)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Correr()
            {
                try { tcs.SetResult(await trabalho()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }
            _fila.Add((_ => { _ = Correr(); }, null));
            if (!tcs.Task.Wait(15_000)) throw new TimeoutException("a thread de tela não devolveu");
            return tcs.Task.Result;
        }

        public void Dispose() => _fila.CompleteAdding();
    }

    private static void ForaDaTela(Action<bool, string> checar)
    {
        using var tela = new ThreadDeTela();
        string NaTela(FakePGWebLib f) => string.Join(", ", f.ThreadsDasChamadas.Where(c => c.Thread == tela.Id).Select(c => c.Chamada));

        var fi = new FakePGWebLib();
        var pi = Provedor(fi);
        var di = tela.Executar(() => pi.InstalarAsync(CancellationToken.None));
        checar(di.Pago && fi.ThreadsDasChamadas.Count > 0 && NaTela(fi).Length == 0,
            "instalação: nenhuma chamada à biblioteca na thread da tela (" + NaTela(fi) + ")");

        var fa = new FakePGWebLib();
        var pa = Provedor(fa);
        var ativo = tela.Executar(() => pa.AtivoAsync(CancellationToken.None));
        checar(ativo && NaTela(fa).Length == 0, "Testar a maquininha: PW_iInit e a lista de operações fora da tela (" + NaTela(fa) + ")");

        var fv = new FakePGWebLib();
        var pv = Provedor(fv);
        var dv = tela.Executar(() => pv.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(1), null, 1, null, CancellationToken.None));
        checar(dv.Pago && NaTela(fv).Length == 0, "venda no cartão: nenhuma chamada à biblioteca na thread da tela (" + NaTela(fv) + ")");

        var fo = new FakePGWebLib();
        var po = Provedor(fo);
        var (ret, _) = tela.Executar(() => po.OperacoesAsync(PW.OPERACOES_ADMINISTRATIVAS, CancellationToken.None));
        checar(ret == PW.PWRET_OK && NaTela(fo).Length == 0, "menu do TEF: a lista de operações fora da tela (" + NaTela(fo) + ")");
    }

    // ── a instalação testa o pinpad antes de tocar na biblioteca ────────────
    private static void InstalacaoTestaAntes(Action<bool, string> checar)
    {
        var mudo = new ResultadoTestePinpad(SituacaoPinpad.NaoRespondeu, TestePinpad.FraseMudo(new PortaSerial("COM2", "")), new PortaSerial("COM2", ""));
        var f = new FakePGWebLib();
        var p = Provedor(f, conferir: (_, _) => Task.FromResult(mudo));
        var d = p.InstalarAsync(CancellationToken.None).GetAwaiter().GetResult();
        checar(!d.Pago && d.Motivo == mudo.Frase && f.Chamadas.Count == 0 && !p.Ocupado,
            "pinpad mudo: a instalação NÃO chama a biblioteca, nem o PW_iInit, e devolve a frase do teste (" + d.Motivo + ")");
        checar(p.UltimoRecado == mudo.Frase, "e a última mensagem da tela é a do teste");

        string? portaPedida = null;
        var achou = new ResultadoTestePinpad(SituacaoPinpad.Respondeu, "Gertec PIN Pad PPC respondeu na COM8.", new PortaSerial("COM8", "Gertec PIN Pad PPC"));
        var f2 = new FakePGWebLib();
        var p2 = Provedor(f2, conferir: (porta, _) => { portaPedida = porta; return Task.FromResult(achou); });
        var d2 = p2.InstalarAsync(CancellationToken.None).GetAwaiter().GetResult();
        checar(d2.Pago && portaPedida == "0" && f2.Chamadas.Contains($"AddParam({PW.PWINFO_PPCOMMPORT}=8)"),
            "pinpad que respondeu na COM8 com a porta em automática: a instalação manda 8, e não 0");

        var aud = new List<string>();
        var f3 = new FakePGWebLib();
        var p3 = Provedor(f3, auditoria: aud, conferir: (_, _) => throw new InvalidOperationException("defeito do teste"));
        checar(p3.InstalarAsync(CancellationToken.None).GetAwaiter().GetResult().Pago && aud.Any(a => a.Contains("teste do pinpad lançou")),
            "defeito no próprio teste não trava a loja: a instalação segue e fica na auditoria");

        var vezes = 0;
        var f4 = new FakePGWebLib();
        var p4 = Provedor(f4, conferir: (_, _) => { vezes++; return Task.FromResult(mudo); });
        p4.ReimprimirAsync(CancellationToken.None).GetAwaiter().GetResult();
        p4.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(1), null, 1, null, CancellationToken.None).GetAwaiter().GetResult();
        checar(vezes == 0, "o teste do pinpad roda só na instalação: venda e reimpressão não pagam esse tempo");
    }

    // ── -2489 no PW_iAddParam: não entra na chamada longa ───────────────────
    private static void SemPinpadNaoEntraNaChamadaLonga(Action<bool, string> checar)
    {
        var aud = new List<string>();
        var f = new FakePGWebLib();
        f.RecusarParamSempre[PW.PWINFO_PPCOMMPORT] = PW.PWRET_PPNOTFOUND;
        var p = Provedor(f, Opcoes with { PortaPinpad = "COM2" }, auditoria: aud);
        var d = p.InstalarAsync(CancellationToken.None).GetAwaiter().GetResult();
        checar(!d.Pago && d.Motivo == "Não achei a maquininha na porta 2. Confira o cabo, feche o programa da Gertec e o PayGo Windows se estiverem abertos.",
            "instalação com PWRET_PPNOTFOUND na porta: frase em português (" + d.Motivo + ")");
        checar(!f.Chamadas.Contains("ExecTransac") && !p.Ocupado, "e não entra no PW_iExecTransac (era ali que a Castelo ficava 4 min presa)");
        checar(!(d.Motivo ?? "").Contains("PWRET_") && aud.Any(a => a.Contains("PWRET_PPNOTFOUND")), "o código fica só na auditoria");

        var guardadas = new List<TransacaoPayGo>();
        var fv = new FakePGWebLib();
        fv.RecusarParamSempre[PW.PWINFO_PPCOMMPORT] = PW.PWRET_PPNOTFOUND;
        var pv = Provedor(fv, guardadas: guardadas);
        var dv = pv.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(1), null, 1, null, CancellationToken.None).GetAwaiter().GetResult();
        checar(!dv.Pago && dv.Situacao == SituacaoTef.Erro && !fv.Chamadas.Contains("ExecTransac") && guardadas.Count == 0
               && dv.Motivo!.StartsWith("Não achei a maquininha", StringComparison.Ordinal),
            "venda sem pinpad: para antes da biblioteca ir ao host, sem linha 'aguardando' (" + dv.Motivo + ")");

        var fq = new FakePGWebLib();
        fq.RecusarParamSempre[PW.PWINFO_PPCOMMPORT] = PW.PWRET_PPNOTFOUND;
        var pq = Provedor(fq, Opcoes with { PreferenciaQr = PW.DSPQRPREF_TELA });
        pq.CobrarAsync(TipoTef.Pix, Dinheiro.DeReais(1), null, 1, null, CancellationToken.None).GetAwaiter().GetResult();
        checar(fq.Chamadas.Contains("ExecTransac"), "Pix com o QR na tela segue sem pinpad (não precisa dele)");

        var fi = new FakePGWebLib();
        fi.RecusarParamSempre[PW.PWINFO_USINGPINPAD] = PW.PWRET_INVPARAM;
        checar(Provedor(fi).InstalarAsync(CancellationToken.None).GetAwaiter().GetResult().Pago,
            "recusa que não é de pinpad (PWRET_INVPARAM) não bloqueia nada, como na administrativa de sempre");
    }

    // ── dado recusado não volta sozinho ─────────────────────────────────────
    private static void DadoRecusadoNaoVoltaSozinho(Action<bool, string> checar)
    {
        var perguntas = new List<ushort>();
        var aud = new List<string>();
        var f = new FakePGWebLib { PedidoNaInstalacao = new PwGetData(PW.PWDAT_TYPED, PW.PWINFO_PPCOMMPORT, "PORTA DO PINPAD", TamanhoMinimo: 1, TamanhoMaximo: 2) };
        f.RecusarParamUmaVez[PW.PWINFO_PPCOMMPORT] = PW.PWRET_INVPARAM;
        var p = Provedor(f, Opcoes with { PortaPinpad = "2" }, auditoria: aud,
            perguntar: (dado, _) => { perguntas.Add(dado.Identificador); return Task.FromResult<string?>("3"); });
        var d = p.InstalarAsync(CancellationToken.None).GetAwaiter().GetResult();
        checar(perguntas.SequenceEqual(new[] { PW.PWINFO_PPCOMMPORT }),
            "a biblioteca recusou a porta e pediu de novo: a tela pergunta, o caixa não reenvia sozinho o mesmo valor");
        checar(d.Pago && f.Ultima?.Params.GetValueOrDefault(PW.PWINFO_PPCOMMPORT) == "3", "e o que vai é a resposta da tela");
        checar(aud.Any(a => a.Contains("tinha recusado")), "a recusa fica na auditoria");
    }

    /// <summary>Cópia da lista de chamadas feita com a trava do fake: a venda em voo escreve nela em outra thread.</summary>
    private static List<string> Copia(FakePGWebLib f)
    {
        lock (f.Chamadas) return f.Chamadas.ToList();
    }

    // ── a segunda tentativa não espera calada atrás da primeira ─────────────
    private static void OcupadoNaoEsperaCalado(Action<bool, string> checar)
    {
        var f = new FakePGWebLib();
        f.Roteiro.Enqueue(FakePGWebLib.Desfecho.NuncaTermina);
        var p = Provedor(f, esperaOcupadoMs: 200, tetoExecMs: 8_000);
        using var cts = new CancellationTokenSource();
        var venda = Task.Run(() => p.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(1), null, 1, null, cts.Token));
        for (var i = 0; i < 400 && !Copia(f).Contains("ExecTransac"); i++) Thread.Sleep(5);
        var novas = Copia(f).Count(c => c.StartsWith("NewTransac", StringComparison.Ordinal));
        var relogio = Stopwatch.StartNew();
        var inst = p.InstalarAsync(CancellationToken.None).GetAwaiter().GetResult();
        relogio.Stop();
        checar(!inst.Pago && inst.Motivo == ProvedorPGWebLib.MsgAindaOcupado && relogio.ElapsedMilliseconds < 1_500,
            $"instalação com outra operação presa: responde em {relogio.ElapsedMilliseconds} ms com o que fazer, em vez de esperar calada");
        checar(Copia(f).Count(c => c.StartsWith("NewTransac", StringComparison.Ordinal)) == novas, "e não mexe na biblioteca por cima da operação em voo");
        cts.Cancel();
        var dv = venda.GetAwaiter().GetResult();
        checar(!dv.Pago && !p.Ocupado, "a operação presa termina cancelada e o semáforo fica livre");

        using var jaCancelado = new CancellationTokenSource();
        jaCancelado.Cancel();
        var f2 = new FakePGWebLib();
        var d2 = Provedor(f2).InstalarAsync(jaCancelado.Token).GetAwaiter().GetResult();
        checar(d2.Situacao == SituacaoTef.Cancelado && f2.Chamadas.Count == 0, "Cancelar antes de começar não toca na biblioteca");
    }

    // ── a vigia da chamada presa e o Cancelar ───────────────────────────────
    private static void VigiaECancelar(Action<bool, string> checar)
    {
        var entrou = new ManualResetEventSlim();
        var solta = new ManualResetEventSlim();
        var f = new FakePGWebLib();
        f.DentroDoExecTransac = () => { if (!entrou.IsSet) { entrou.Set(); solta.Wait(5_000); } };
        var p = Provedor(f);
        using var cts = new CancellationTokenSource();
        var venda = Task.Run(() => p.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(1), null, 1, null, cts.Token));
        checar(entrou.Wait(3_000), "a chamada ficou presa dentro do PW_iExecTransac");
        var voo = p.EmVoo;
        checar(voo?.Nome == "PW_iExecTransac", "a vigia sabe qual chamada está presa (" + (voo?.Nome ?? "nada") + ")");
        checar(voo is not null && AcompanhamentoTef.Recado(null, voo, voo.DesdeUtc.AddSeconds(40)).Contains("não responde há 40 s"),
            "presa há 40 s: a tela diz há quanto tempo e manda tirar o cabo");
        checar(voo is not null && !AcompanhamentoTef.Recado("SENHA TECNICA", voo, voo.DesdeUtc.AddSeconds(5)).Contains("cabo"),
            "presa há 5 s: ainda não assusta ninguém, mostra a última mensagem");
        cts.Cancel();
        solta.Set();
        var d = venda.GetAwaiter().GetResult();
        checar(d.Situacao == SituacaoTef.Cancelado && p.EmVoo is null && !p.Ocupado,
            "Cancelar com a chamada presa: quando ela volta, encerra cancelada, sem chamada em voo e com o semáforo livre (" + d.Motivo + ")");
    }

    // ── a porta nova chega à biblioteca ─────────────────────────────────────
    private static void PortaNovaChega(Action<bool, string> checar)
    {
        var f = new FakePGWebLib();
        Provedor(f, Opcoes with { PortaPinpad = "COM2" }).InstalarAsync(CancellationToken.None).GetAwaiter().GetResult();
        var primeira = f.Ultima?.Params.GetValueOrDefault(PW.PWINFO_PPCOMMPORT);
        // Salvar na Configuração troca a instância (Servicos.RecarregarTef): a nova leva a porta nova.
        Provedor(f, Opcoes with { PortaPinpad = "8" }).InstalarAsync(CancellationToken.None).GetAwaiter().GetResult();
        var segunda = f.Ultima?.Params.GetValueOrDefault(PW.PWINFO_PPCOMMPORT);
        checar(primeira == "2" && segunda == "8", $"a segunda instalação depois de trocar a porta manda a porta nova ({primeira} e depois {segunda})");
    }

    // ── os textos da tela da instalação ─────────────────────────────────────
    private static void Textos(Action<bool, string> checar)
    {
        checar(AcompanhamentoTef.Duracao(TimeSpan.FromSeconds(8)) == "8 s" && AcompanhamentoTef.Duracao(TimeSpan.FromSeconds(80)) == "1 min 20 s"
               && AcompanhamentoTef.Duracao(TimeSpan.FromSeconds(120)) == "2 min", "cronômetro em minutos e segundos");
        checar(AcompanhamentoTef.Cronometro("Instalando", TimeSpan.FromSeconds(80)) == "Instalando... 1 min 20 s", "'Instalando... 1 min 20 s'");
        checar(AcompanhamentoTef.Prazo(AcompanhamentoTef.PrazoInstalacao) == "Prazo máximo: 3 minutos. Passou disso, toque em Cancelar.",
            "o prazo máximo em texto simples: " + AcompanhamentoTef.Prazo(AcompanhamentoTef.PrazoInstalacao));
        checar(AcompanhamentoTef.Recado(null, null, DateTime.UtcNow) == "Esperando a maquininha responder."
               && AcompanhamentoTef.Recado(" SENHA TECNICA ", null, DateTime.UtcNow) == "Última mensagem da maquininha: SENHA TECNICA",
            "a última mensagem da biblioteca aparece do jeito que ela mandou");
        checar(ProvedorPGWebLib.MsgPinpadNaoAchado("0").Contains("nenhuma porta"), "automática sem pinpad: 'em nenhuma porta'");
        var todos = new[]
        {
            AcompanhamentoTef.Cronometro("Instalando", TimeSpan.FromSeconds(80)), AcompanhamentoTef.Prazo(AcompanhamentoTef.PrazoInstalacao),
            AcompanhamentoTef.TextoPassouDoPrazo, AcompanhamentoTef.Cancelada("Instalação"), ProvedorPGWebLib.MsgAindaOcupado,
            ProvedorPGWebLib.MsgPinpadNaoAchado("2"), AcompanhamentoTef.Recado(null, new ProvedorPGWebLib.ChamadaNativa("x", DateTime.UtcNow.AddMinutes(-2)), DateTime.UtcNow),
        };
        foreach (var t in todos)
            checar(!t.Contains('%') && !t.Contains('—') && !t.Contains('–') && !t.Contains("PWRET_"),
                "sem porcentagem inventada, sem travessão e sem código: " + t);
    }

    // ── a fiação nas telas ──────────────────────────────────────────────────
    private static string? Fonte(params string[] caminho)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var alvo = Path.Combine(new[] { d.FullName }.Concat(caminho).ToArray());
            if (File.Exists(alvo)) return File.ReadAllText(alvo);
        }
        return null;
    }

    private static void Fontes(Action<bool, string> checar)
    {
        var prov = Fonte("Pdv.Nucleo", "ProvedorPGWebLib.cs") ?? "";
        // Só o código: o comentário que conta a história cita o await errado de propósito.
        var provCodigo = Regex.Replace(prov, @"^\s*//.*$", "", RegexOptions.Multiline);
        checar(prov.Length > 0 && !Regex.IsMatch(provCodigo, @"await\s+ForaDaTelaAsync\(\)\s*;")
               && Regex.Matches(provCodigo, @"await\s+ForaDaTelaAsync\(\)\.ConfigureAwait\(false\)\s*;").Count >= 7,
            "nenhum 'await ForaDaTelaAsync();' sem ConfigureAwait(false) no provedor");

        var xaml = Fonte("Telas", "Configuracao.xaml") ?? "";
        var testar = xaml.IndexOf("x:Name=\"BtnTestarPinpad\"", StringComparison.Ordinal);
        var instalar = xaml.IndexOf("x:Name=\"BtnInstalarPgweb\"", StringComparison.Ordinal);
        checar(testar >= 0 && instalar > testar && xaml.Contains("Content=\"Testar pinpad\"", StringComparison.Ordinal),
            "o botão 'Testar pinpad' vem antes de 'Instalar ponto de captura' no passo Maquininha");
        checar(!xaml.Contains("TxtPgwebPorta", StringComparison.Ordinal) && Regex.IsMatch(xaml, "x:Name=\"BlocoPgwebAvancado\"[^>]*Visibility=\"Collapsed\"")
               && xaml.IndexOf("x:Name=\"CboPgwebPorta\"", StringComparison.Ordinal) > xaml.IndexOf("x:Name=\"BlocoPgwebAvancado\"", StringComparison.Ordinal),
            "a porta do pinpad virou lista e mora em 'avançado', escondida por padrão");

        var cfg = Fonte("Telas", "Configuracao.xaml.cs") ?? "";
        var operar = cfg.IndexOf("private async Task OperarPGWebLibAsync(string oQue)", StringComparison.Ordinal);
        var blocoOperar = operar >= 0 ? cfg[operar..Math.Min(cfg.Length, operar + 6_000)] : "";
        checar(blocoOperar.Contains("TelaOperacaoTef.Acompanhar(", StringComparison.Ordinal) && blocoOperar.Contains("InstalarAsync(ct)", StringComparison.Ordinal),
            "Instalar ponto de captura roda dentro da tela com cronômetro e Cancelar, com o token dela");
        checar(cfg.Contains("Servicos.TestarPinpadAsync(", StringComparison.Ordinal) && cfg.Contains("PortaDoPinpad.Opcoes(", StringComparison.Ordinal),
            "o Testar pinpad e a lista de portas usam o núcleo testado (e a tela não toca na DLL nativa)");

        var tela = Fonte("Telas", "TelaOperacaoTef.cs") ?? "";
        checar(tela.Contains("\"Cancelar\"", StringComparison.Ordinal) && tela.Contains("DispatcherTimer", StringComparison.Ordinal)
               && tela.Contains("AcompanhamentoTef.Cronometro(", StringComparison.Ordinal) && tela.Contains("AcompanhamentoTef.Recado(", StringComparison.Ordinal)
               && !tela.Contains("ProgressBar", StringComparison.Ordinal) && !tela.Contains('%'),
            "a tela da instalação tem cronômetro, última mensagem e Cancelar, e nenhuma barra de progresso inventada");

        var serv = Fonte("Servicos.cs") ?? "";
        checar(Regex.IsMatch(serv, @"ConferirPinpad\s*=\s*TestarPinpadAsync\s*,")
               && serv.Contains("TestePinpad.TestarAsync(new SerialWindows(), porta, PGWebLibNativa.Carregada(), ct)", StringComparison.Ordinal),
            "o provedor da loja testa o pinpad antes de instalar, com as portas de verdade");
        var janela = serv.IndexOf("private static System.Windows.Window JanelaAtiva()", StringComparison.Ordinal);
        var blocoJanela = janela >= 0 ? serv[janela..Math.Min(serv.Length, janela + 1_500)] : "";
        checar(blocoJanela.Contains("LastOrDefault(", StringComparison.Ordinal) && blocoJanela.Contains(".Activate()", StringComparison.Ordinal),
            "pergunta da biblioteca nasce em cima da última janela aberta e traz o caixa para a frente");

        // A senha do administrador no assistente (14/09/2026, Castelo): escondida e repetida.
        checar(!xaml.Contains("x:Name=\"TxtOpPin\"", StringComparison.Ordinal)
               && xaml.Contains("<PasswordBox x:Name=\"PwdOpPin\"", StringComparison.Ordinal)
               && xaml.Contains("<PasswordBox x:Name=\"PwdOpPin2\"", StringComparison.Ordinal)
               && xaml.Contains("Repita a senha", StringComparison.Ordinal),
            "a senha do administrador é campo de senha (não aparece na tela) e tem 'Repita a senha'");
        checar(!cfg.Contains("TxtOpPin", StringComparison.Ordinal) && cfg.Contains("AdminPinRepetido = PwdOpPin2.Password", StringComparison.Ordinal)
               && cfg.Contains("var pin = PwdOpPin.Password.Trim();", StringComparison.Ordinal),
            "o Salvar e a validação leem os dois campos de senha");
    }
}
