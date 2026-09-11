using System.Text.RegularExpressions;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A VIGIA DA SESSÃO DO WHATSAPP (11/09/2026, pedido do dono: "trava de notificação
/// caso o QR do WhatsApp Web desconecte do PDV; verificar de tempos em tempos se está
/// conectado, porque às vezes desloga sem motivo") e o TOQUE ORIGINAL ("chegou
/// notificação, toca o do WhatsApp; o meu só se a página calar").
///
/// A regra mora em Pdv.Nucleo/SessaoWhatsApp e é provada pelo VALOR, com relógio por
/// parâmetro. O que só existe no WPF/WebView2 (o script que lê o DOM, a camada empurrada
/// para fora da janela, o selo, o aviso fixo, o som) é travado no FONTE.
/// </summary>
public static class TestesSessaoWhatsApp
{
    public static void Rodar(Action<bool, string> checar)
    {
        var t0 = new DateTime(2026, 9, 11, 9, 0, 0);
        TimeSpan s(int n) => TimeSpan.FromSeconds(n);
        TimeSpan m(int n) => TimeSpan.FromMinutes(n);

        // ── Ler: nunca lança, aceita a palavra crua, com aspas, null e lixo ──
        checar(SessaoWhatsApp.Ler("qr") == EstadoWa.PedindoQr && SessaoWhatsApp.Ler("\"qr\"") == EstadoWa.PedindoQr,
            "Ler: 'qr' cru ou JSON-codificado = PedindoQr");
        checar(SessaoWhatsApp.Ler("conectado") == EstadoWa.Conectado && SessaoWhatsApp.Ler("lista") == EstadoWa.Conectado,
            "Ler: 'conectado'/'lista' = Conectado");
        checar(SessaoWhatsApp.Ler("telefone") == EstadoWa.TelefoneSemConexao && SessaoWhatsApp.Ler("pc") == EstadoWa.PcSemInternet
               && SessaoWhatsApp.Ler("carregando") == EstadoWa.Carregando && SessaoWhatsApp.Ler("semcomponente") == EstadoWa.SemComponente,
            "Ler: telefone, pc, carregando, semcomponente");
        checar(SessaoWhatsApp.Ler("semleitura") == EstadoWa.SemLeitura && SessaoWhatsApp.Ler("  QR  ") == EstadoWa.PedindoQr,
            "Ler: 'semleitura' (o ProcessFailed manda) e maiúsculas/espaços");
        {
            // toda palavra que a tela manda tem que ser conhecida (fora 'desconhecido')
            var telaCs = Fonte(Path.Combine("Telas", "ChatWhatsApp.xaml.cs")) ?? "";
            var palavras = Regex.Matches(telaCs, @"ReportarSessao\(""(\w+)""\)").Select(x => x.Groups[1].Value)
                .Concat(Regex.Matches(telaCs, @"estado: '(\w+)'").Select(x => x.Groups[1].Value))
                .Where(p => p != "desconhecido").Distinct().ToList();
            checar(palavras.Count >= 5 && palavras.All(p => SessaoWhatsApp.Ler(p) != EstadoWa.Desconhecido),
                "toda palavra que a tela manda passa por Ler sem virar Desconhecido: " + string.Join(", ", palavras));
        }
        checar(SessaoWhatsApp.Ler(null) == EstadoWa.Desconhecido && SessaoWhatsApp.Ler("null") == EstadoWa.Desconhecido
               && SessaoWhatsApp.Ler("") == EstadoWa.Desconhecido && SessaoWhatsApp.Ler("xyz") == EstadoWa.Desconhecido,
            "Ler: null, 'null', vazio e lixo = Desconhecido (nunca lança)");

        // ── loja que NUNCA conectou: nada de aviso, nunca ─────────────────────
        {
            var v = new SessaoWhatsApp(jaConectou: false);
            var avisou = false;
            // leitura a cada 5 s e o relógio batendo entre elas, como na tela, por 30 min
            for (var i = 0; i < 360; i++)
            {
                avisou |= v.Observar(EstadoWa.PedindoQr, t0 + s(5 * i)).Avisar;
                avisou |= v.Bater(t0 + s(5 * i) + s(2)).Avisar;
            }
            checar(!avisou && !v.Armada && v.Estado == EstadoWa.PedindoQr,
                "loja que nunca leu o QR: 30 min de tela de QR e NENHUM aviso (a função não é usada ali)");
        }

        // ── arma depois de conectar, avisa depois da carência, repete, cala, volta ──
        {
            var v = new SessaoWhatsApp(jaConectou: false);
            var r1 = v.Observar(EstadoWa.Conectado, t0);
            var r2 = v.Observar(EstadoWa.Conectado, t0 + s(5));
            checar(!r1.Armou && r2.Armou && v.Armada, "arma na SEGUNDA leitura seguida de Conectado (uma só pode ser ruído)");
            var r3 = v.Observar(EstadoWa.Conectado, t0 + s(10));
            checar(!r3.Armou && !r3.ZerarContagem && !r3.Voltou, "Armou é dito uma vez só, e Conectado firme não zera a contagem nem diz Voltou");
            var inter = new SessaoWhatsApp(jaConectou: false);
            inter.Observar(EstadoWa.Conectado, t0); inter.Observar(EstadoWa.Carregando, t0 + s(5));
            checar(!inter.Observar(EstadoWa.Conectado, t0 + s(10)).Armou && !inter.Armada && inter.Observar(EstadoWa.Conectado, t0 + s(15)).Armou,
                "Conectado, Carregando, Conectado não arma (são duas SEGUIDAS); a seguinte arma");
            var frio = new SessaoWhatsApp(jaConectou: true);
            var b0 = frio.Bater(t0 + m(10));
            checar(b0.Estado == EstadoWa.Desconhecido && !b0.Avisar, "o relógio bate antes de qualquer leitura (PC liga antes do Wi-Fi): nada acontece");

            var q = t0 + m(1);
            var a0 = v.Observar(EstadoWa.PedindoQr, q);
            var a1 = v.Observar(EstadoWa.PedindoQr, q + s(59));
            checar(!a0.Avisar && !a1.Avisar && v.CaidoDesde == q, "tela do QR: nos primeiros 60 s não avisa (recarga passa por ela)");
            var a2 = v.Observar(EstadoWa.PedindoQr, q + s(61));
            checar(a2.Avisar && a2.Estado == EstadoWa.PedindoQr, "QR há mais de 60 s num caixa armado: AVISA");
            checar(!v.Observar(EstadoWa.PedindoQr, q + m(5)).Avisar && !v.Bater(q + m(5) + s(2)).Avisar
                   && !v.Observar(EstadoWa.PedindoQr, q + m(15)).Avisar,
                "depois do aviso, os 14 min seguintes ficam quietos (não vira metralhadora)");
            var rep = v.Bater(q + m(16) + s(2));
            checar(rep.Avisar, "aos 15 min repete, e repete pelo relógio (Bater), sem precisar de leitura nova");

            v.Adiar(q + m(17));
            checar(!v.Observar(EstadoWa.PedindoQr, q + m(40)).Avisar && !v.Observar(EstadoWa.PedindoQr, q + m(90)).Avisar,
                "'Depois' cala por 2 h");
            checar(v.Observar(EstadoWa.PedindoQr, q + m(17) + TimeSpan.FromHours(2) + s(1)).Avisar, "passadas as 2 h, volta a avisar");
            // e se a página parar de responder no meio da queda, o relógio NÃO repete o aviso no escuro
            var mudo = new SessaoWhatsApp(jaConectou: true);
            mudo.Observar(EstadoWa.PedindoQr, t0); mudo.Observar(EstadoWa.PedindoQr, t0 + m(2));
            checar(mudo.Bater(t0 + m(30)).Estado == EstadoWa.SemLeitura && !mudo.Bater(t0 + m(31)).Avisar,
                "sem leitura há 3 min a vigia vira SemLeitura e não repete o aviso às cegas");

            var volta = v.Observar(EstadoWa.Conectado, q + m(200));
            checar(volta.Voltou && volta.ZerarContagem && v.CaidoDesde is null && v.Estado == EstadoWa.Conectado,
                "voltou: o aviso some e a contagem de não lidas recomeça (sem '7 mensagens novas' velhas)");
            var blip = new SessaoWhatsApp(jaConectou: true);
            blip.Observar(EstadoWa.Conectado, t0); blip.Observar(EstadoWa.PedindoQr, t0 + s(5));
            var rb = blip.Observar(EstadoWa.Conectado, t0 + s(10));
            checar(!rb.Voltou && rb.ZerarContagem, "QR por 5 s sem aviso: não diz Voltou (não havia aviso), mas a contagem recomeça");
            checar(!v.Bater(q + m(300)).Avisar, "conectado: o relógio não avisa nada");

            var q2 = q + m(400);
            v.Observar(EstadoWa.PedindoQr, q2);
            checar(v.Observar(EstadoWa.PedindoQr, q2 + s(61)).Avisar && v.CaidoDesde == q2,
                "caiu de novo mais tarde: nova carência a partir da nova queda, e avisa de novo");
        }

        // ── Carregando/Desconhecido no meio da queda não 'curam' a queda ──────
        {
            var v = new SessaoWhatsApp(jaConectou: true);
            v.Observar(EstadoWa.PedindoQr, t0);
            v.Observar(EstadoWa.Carregando, t0 + s(30));
            v.Observar(EstadoWa.Desconhecido, t0 + s(40));
            var r = v.Observar(EstadoWa.PedindoQr, t0 + s(70));
            checar(r.Avisar && v.CaidoDesde == t0, "QR, carregando, QR de novo: a queda continua contando desde a primeira vez");
            var wifi = new SessaoWhatsApp(jaConectou: true);
            wifi.Observar(EstadoWa.PedindoQr, t0); wifi.Observar(EstadoWa.PcSemInternet, t0 + s(20)); wifi.Observar(EstadoWa.TelefoneSemConexao, t0 + s(30));
            wifi.Bater(t0 + m(4)); wifi.Observar(EstadoWa.PedindoQr, t0 + m(5));
            checar(wifi.CaidoDesde == t0, "QR, Wi-Fi piscando, telefone, sem leitura, QR: só Conectado cura; a queda é a mesma");
        }

        // ── memória gravada: caixa que já conectou ontem avisa sem ver Conectado hoje ──
        {
            var v = new SessaoWhatsApp(jaConectou: true);
            v.Observar(EstadoWa.Carregando, t0);
            v.Observar(EstadoWa.PedindoQr, t0 + s(10));
            checar(v.Observar(EstadoWa.PedindoQr, t0 + s(75)).Avisar,
                "reiniciou de manhã já na tela do QR: a memória 'já conectou' basta para avisar");
        }

        // ── o que NÃO avisa: telefone sem conexão, PC sem internet, carregando ──
        {
            var v = new SessaoWhatsApp(jaConectou: true);
            var avisou = false;
            foreach (var e in new[] { EstadoWa.TelefoneSemConexao, EstadoWa.PcSemInternet, EstadoWa.Carregando, EstadoWa.Desconhecido })
                for (var i = 0; i < 100; i++) avisou |= v.Observar(e, t0 + m(i)).Avisar || v.Bater(t0 + m(i) + s(2)).Avisar;
            checar(!avisou, "telefone sem conexão, PC sem internet, carregando: horas assim e nenhum aviso (não é o QR)");
        }

        // ── silêncio da página vira SemLeitura, sem aviso; leitura nova cura ─
        {
            var v = new SessaoWhatsApp(jaConectou: true);
            v.Observar(EstadoWa.Conectado, t0);
            var b1 = v.Bater(t0 + m(2));
            var b2 = v.Bater(t0 + m(3) + s(1));
            checar(b1.Estado == EstadoWa.Conectado && b2.Estado == EstadoWa.SemLeitura && !b2.Avisar,
                "3 min sem leitura: SemLeitura (diagnóstico), sem gritar");
            var r = v.Observar(EstadoWa.Conectado, t0 + m(4));
            checar(r.Estado == EstadoWa.Conectado && r.ZerarContagem, "a leitura volta: Conectado de novo, contagem recomeça");
        }

        // ── depois de 7 dias caído, para de repetir (alguém decidiu não usar) ─
        {
            var v = new SessaoWhatsApp(jaConectou: true);
            v.Observar(EstadoWa.PedindoQr, t0);
            checar(v.Observar(EstadoWa.PedindoQr, t0 + m(2)).Avisar, "dia 0: avisa");
            checar(v.Observar(EstadoWa.PedindoQr, t0 + TimeSpan.FromDays(6)).Avisar, "dia 6: ainda avisa");
            var d7 = v.Observar(EstadoWa.PedindoQr, t0 + TimeSpan.FromDays(7) + s(1));
            checar(!d7.Avisar && d7.Desarmou && !v.Armada && v.CaidoDesde is null,
                "7 dias e 1 s: desarma de vez (selo e som somem; ninguém vai ler esse QR)");
            checar(v.Observar(EstadoWa.Conectado, t0 + TimeSpan.FromDays(9)).Armou == false
                   && v.Observar(EstadoWa.Conectado, t0 + TimeSpan.FromDays(9) + s(5)).Armou,
                "uma conexão nova rearma (duas leituras seguidas)");
            var reinicio = new SessaoWhatsApp(jaConectou: true, caidoDesde: t0);
            var rr = reinicio.Observar(EstadoWa.PedindoQr, t0 + TimeSpan.FromDays(8));
            checar(!rr.Avisar && rr.Desarmou, "reiniciou o PDV no dia 8 com a queda gravada: não avisa, desarma");
            var reinicioCedo = new SessaoWhatsApp(jaConectou: true, caidoDesde: t0);
            checar(reinicioCedo.Observar(EstadoWa.PedindoQr, t0 + TimeSpan.FromDays(2)).Avisar,
                "reiniciou no dia 2 com a queda gravada: avisa na hora (a queda não é nova)");
        }

        // ── sem o componente: avisa UMA vez, e só se armado ───────────────────
        {
            var v = new SessaoWhatsApp(jaConectou: true);
            var r1 = v.SemComponente(t0); var r2 = v.SemComponente(t0 + m(30));
            checar(r1.Avisar && !r2.Avisar && v.Estado == EstadoWa.SemComponente, "WebView2 ausente: um aviso só, e não repete");
            var rc = v.Observar(EstadoWa.Conectado, t0 + m(40));
            checar(rc.Voltou && rc.ZerarContagem && rc.Estado == EstadoWa.Conectado, "instalou o runtime e conectou: o aviso some (Voltou)");
            var u = new SessaoWhatsApp(jaConectou: false);
            checar(!u.SemComponente(t0).Avisar, "sem componente numa loja que nunca usou: silêncio");
        }

        // ── textos: curtos, sem travessão, ação que existe ────────────────────
        {
            var textos = new List<string> { SessaoWhatsApp.Selo };
            foreach (EstadoWa e in Enum.GetValues(typeof(EstadoWa)))
            {
                var (ti, ac) = SessaoWhatsApp.Aviso(e);
                textos.Add(ti); textos.Add(ac); textos.Add(SessaoWhatsApp.Cabecalho(e));
            }
            checar(textos.All(x => !x.Contains('—') && !x.Contains('–')), "nenhum texto da vigia tem travessão");
            checar(textos.All(x => x.Split('\n').Length <= 2 && x.Length <= 110), "textos de uma linha, curtos");
            checar(textos.All(x => !Regex.IsMatch(x.Replace("QR", "").Replace("PC", "").Replace("PDV", ""), "[A-Z]{4,}")),
                "sem palavras em caixa alta (o dono lê como grito)");
            checar(SessaoWhatsApp.Aviso(EstadoWa.PedindoQr).Acao.Contains("celular") && SessaoWhatsApp.Selo == "QR",
                "o aviso do QR manda ler com o celular da loja, e o selo diz QR");
            checar(SessaoWhatsApp.Cabecalho(EstadoWa.PedindoQr).Contains("Manter conectado"),
                "o cabeçalho da aba lembra de deixar marcado Manter conectado (a causa mais comum do QR diário)");
        }

        // ── o toque: a página cobre ou o caixa toca a reserva ─────────────────
        {
            var msg = t0;
            checar(SomWhatsApp.PaginaCobre(msg - s(2), msg), "a página tocou 2 s ANTES de o título mudar: foi ela, o caixa cala");
            checar(SomWhatsApp.PaginaCobre(msg + s(1), msg), "a página tocou logo depois: idem");
            checar(!SomWhatsApp.PaginaCobre(msg - m(5), msg), "um áudio de 5 min atrás não conta");
            checar(!SomWhatsApp.PaginaCobre(DateTime.MinValue, msg), "página que nunca tocou: a reserva toca");
            checar(SomWhatsApp.EsperaPelaPagina < TimeSpan.FromSeconds(3), "a espera pela página é curta (o toque não pode atrasar)");
            // o mecanismo REAL, com espera curta: página calada toca a reserva; página que tocou, não
            ServicoWhatsApp.RecomecarSessaoParaTeste();
            int calada = 0, tocou = 0;
            ServicoWhatsApp.TocarSeAPaginaCalar(() => Interlocked.Increment(ref calada), TimeSpan.FromMilliseconds(50));
            Thread.Sleep(400);
            ServicoWhatsApp.PaginaTocou();
            ServicoWhatsApp.TocarSeAPaginaCalar(() => Interlocked.Increment(ref tocou), TimeSpan.FromMilliseconds(50));
            Thread.Sleep(400);
            checar(calada == 1 && tocou == 0, "TocarSeAPaginaCalar de verdade: reserva toca só quando a página calou");
            var dados = Path.Combine(Path.GetTempPath(), "pdv-teste-sons-" + Guid.NewGuid().ToString("N"));
            checar(SomDaLoja.Caminho(dados, SomDaLoja.ChatIfood).EndsWith(Path.Combine("sons", "ifood-chat.wav"))
                   && SomDaLoja.Caminho(dados, SomDaLoja.PedidoNovo).EndsWith(Path.Combine("sons", "pedido.wav"))
                   && SomDaLoja.Caminho(dados, SomDaLoja.WhatsApp) == SomWhatsApp.Caminho(dados),
                "a pasta sons\\ da loja serve para o chat do iFood, o pedido novo e o WhatsApp");
        }

        // ── o serviço: a sessão não mora na contagem ──────────────────────────
        {
            var config = new Dictionary<string, string>();
            ServicoWhatsApp.Ligar(k => config.TryGetValue(k, out var v) ? v : null, (k, v) => config[k] = v);
            var mudou = new List<EstadoWa>(); var totais = new List<int>(); var novas = new List<int>();
            void M(EstadoWa e) => mudou.Add(e);
            void T(int n) => totais.Add(n);
            void N(int n) => novas.Add(n);
            ServicoWhatsApp.SessaoMudou += M; ServicoWhatsApp.Mudou += T; ServicoWhatsApp.MensagemNova += N;
            try
            {
                ServicoWhatsApp.Recomecar(); totais.Clear();
                ServicoWhatsApp.ReportarSessao("conectado");
                ServicoWhatsApp.ReportarSessao("conectado");
                checar(config.ContainsKey(SessaoWhatsApp.ChaveConectouEm) && ServicoWhatsApp.Armada,
                    "duas leituras de conectado gravam whatsapp_conectou_em (pela porta injetada, não pelo Banco)");
                ServicoWhatsApp.ReportarTitulo("(7) WhatsApp");
                ServicoWhatsApp.ReportarSessao("conectado");
                checar(!totais.Contains(0) && novas.Count == 0 && ServicoWhatsApp.NaoLidas == 7,
                    "conectado firme não zera a contagem (o heartbeat não pode virar linha de base)");
                ServicoWhatsApp.ReportarSessao("qr");
                checar(ServicoWhatsApp.Sessao == EstadoWa.PedindoQr && mudou.SequenceEqual(new[] { EstadoWa.Conectado, EstadoWa.PedindoQr }),
                    "o serviço espelha o estado e avisa a mudança");
                checar(!string.IsNullOrEmpty(config.GetValueOrDefault(SessaoWhatsApp.ChaveCaidoDesde)), "a queda fica gravada (whatsapp_caido_desde)");
                ServicoWhatsApp.Recomecar();
                checar(ServicoWhatsApp.Sessao == EstadoWa.PedindoQr, "Recomecar (contagem) NÃO mexe na sessão");
                // a volta, na ORDEM do script (sessão antes da contagem): 7 velhas não avisam, a 8ª avisa
                novas.Clear();
                ServicoWhatsApp.ReportarSessao("conectado");
                ServicoWhatsApp.ReportarTitulo("(7) WhatsApp");
                checar(novas.Count == 0 && ServicoWhatsApp.NaoLidas == 7, "voltou do QR com 7 não lidas: selo 7, sem toque (são velhas)");
                ServicoWhatsApp.ReportarTitulo("(8) WhatsApp");
                checar(novas.SequenceEqual(new[] { 8 }), "a 8ª chega: avisa");
                checar(string.IsNullOrEmpty(config.GetValueOrDefault(SessaoWhatsApp.ChaveCaidoDesde)), "reconectou: a queda gravada sai");
                ServicoWhatsApp.Ligar(k => config.TryGetValue(k, out var v) ? v : null, (k, v) => config[k] = v);
                checar(ServicoWhatsApp.Armada, "ao religar, a memória gravada arma a vigia de cara");
            }
            finally
            {
                ServicoWhatsApp.SessaoMudou -= M; ServicoWhatsApp.Mudou -= T; ServicoWhatsApp.MensagemNova -= N;
                ServicoWhatsApp.Recomecar();
                ServicoWhatsApp.RecomecarSessaoParaTeste();
                ServicoWhatsApp.Ligar(_ => null, (_, _) => { });
            }
        }

        // ── o fonte: o que só existe no WPF/WebView2 ──────────────────────────
        {
            var tela = Fonte(Path.Combine("Telas", "ChatWhatsApp.xaml.cs")) ?? "";
            var venda = Fonte(Path.Combine("Telas", "Venda.xaml")) ?? "";
            var vendaCs = Fonte(Path.Combine("Telas", "Venda.xaml.cs")) ?? "";
            var main = Fonte("MainWindow.xaml") ?? "";
            var mainCs = Fonte("MainWindow.xaml.cs") ?? "";
            var servico = Fonte("ServicoWhatsApp.cs") ?? "";
            var alerta = Fonte("Alerta.cs") ?? "";

            checar(tela.Contains("if (window.top !== window) return;") && tela.Contains("tipo: 'sessao'") && tela.Contains("window.pdvEstadoWa"),
                "o script lê o estado só na janela de cima e posta {tipo:'sessao'}");
            checar(tela.Contains("#pane-side") && tela.Contains("data-ref") && tela.Contains("alert-phone") && tela.Contains("marcarManterConectado"),
                "lê pela estrutura (lista, QR, telefone) e marca o Manter conectado na tela do QR");
            checar(tela.Contains("core.ProcessFailed +=") && tela.Contains("Dispatcher.ShutdownStarted") && tela.Contains("ServicoWhatsApp.Bater();"),
                "trata renderer caído, para o relógio no encerramento e bate a vigia a cada tick");
            checar(tela.Contains("IsDocumentPlayingAudioChanged") && tela.Contains("ServicoWhatsApp.PaginaTocou()")
                   && tela.Contains("--autoplay-policy=no-user-gesture-required"),
                "escuta o áudio da página e libera o autoplay (o toque original é dela)");
            checar(tela.Contains("e.Source ?? \"\").StartsWith(UrlWhatsApp") && tela.Contains("ServicoWhatsApp.SemComponente()"),
                "só a página do WhatsApp fala com o caixa; sem runtime, a vigia sabe");
            var recomecar = Trecho(servico, "public static void Recomecar()", "PaginaTocou");
            checar(recomecar.Length > 0 && !recomecar.Contains("_sessao", StringComparison.Ordinal), "Recomecar do serviço não toca em _sessao");
            var contar = Trecho(tela, "window.pdvContarWa = function", "var pend = null;");
            checar(contar.Length > 0 && contar.IndexOf("tipo: 'sessao'", StringComparison.Ordinal) < contar.IndexOf("tipo: 'naolidas', total", StringComparison.Ordinal)
                   && contar.Contains("if (s.estado !== 'conectado') { ultimo = null; return; }", StringComparison.Ordinal)
                   && contar.Contains("agora - ultimoEnvio >= 30000", StringComparison.Ordinal),
                "o script manda a sessão ANTES da contagem, só conta com a lista na tela, e o heartbeat é por tempo");
            checar(tela.IndexOf("if (avisoTelefone())", StringComparison.Ordinal) < tela.IndexOf("visivel(q('#pane-side'))", StringComparison.Ordinal),
                "a faixa 'telefone sem conexão' é lida antes da lista (ela aparece por cima da lista)");
            checar(Regex.IsMatch(tela, @"finally \{ try \{ ServicoWhatsApp\.Bater\(\); \} catch \{ \} \}"), "a vigia bate mesmo quando a página não responde (finally)");
            checar(tela.Contains("public event Action? Iniciou;") && mainCs.Contains("CamadaWhatsApp.Iniciou +=") && mainCs.Contains("DevolverTecladoAoCaixa()"),
                "o WebView2 nascendo fora da tela devolve o teclado ao caixa");
            checar(Regex.Matches(mainCs, @"ServicoWhatsApp\.Ligar\(").Count == 1, "Ligar é chamado uma vez só");
            checar(vendaCs.Contains("{d:HH:mm}") && !Regex.IsMatch(vendaCs, @"AvisoWhatsAppCaido[\s\S]{0,400}hh:mm"), "a hora da queda sai em 24 h");
            checar(vendaCs.Contains("ServicoWhatsApp.CaidoDesde is not null || e == EstadoWa.SemComponente"), "o selo QR segue a queda aberta, não o estado do instante");
            var avisoBloco = Regex.Match(venda, @"<Border x:Name=""AvisoWhatsAppCaido"".*?</Border>", RegexOptions.Singleline).Value;
            checar(avisoBloco.Contains("Text=\"🔴\"") && !avisoBloco.Contains("Text=\"🟢\""), "o aviso de queda é vermelho, não verde");
            foreach (var (metodo, som) in new[] { ("MensagemChat", "ChatIfood"), ("PedidoNovo", "PedidoNovo"), ("MensagemWhatsApp", "WhatsApp") })
            {
                var corpoM = Trecho(alerta, $"public static void {metodo}()", "public static void ");
                checar(corpoM.Contains($"TocarDaLoja(Pdv.Nucleo.SomDaLoja.{som})"), $"Alerta.{metodo} toca sons\\{som} da loja");
            }
            checar(Trecho(alerta, "private static bool TocarDaLoja(", "public static void").Contains("catch { return false; }"), "um .wav que o Windows não toca cai nos beeps, não no silêncio");
            checar(venda.Contains("x:Name=\"SeloWhatsAppCaido\"") && venda.Contains("x:Name=\"AvisoWhatsAppCaido\"")
                   && venda.Contains("Click=\"AdiarAvisoWhatsApp\"") && venda.Contains("MouseLeftButtonUp=\"AbrirWhatsAppPeloAviso\""),
                "a venda tem o selo QR, o aviso fixo com Depois, e o toque abre a aba");
            var avisoXaml = Regex.Match(venda, @"<Border x:Name=""AvisoWhatsAppCaido"".*?</Border>", RegexOptions.Singleline).Value;
            checar(!avisoXaml.Contains("_toastWhatsAppSome") && !Regex.IsMatch(vendaCs, @"AvisoWhatsAppCaido[\s\S]{0,200}FromSeconds\(60\)"),
                "o aviso de caído não herda o sumiço de 60 s do aviso de mensagem");
            foreach (var ev in new[] { "SessaoMudou", "Caiu", "Voltou" })
                checar(vendaCs.Contains($"ServicoWhatsApp.{ev} += ") && vendaCs.Contains($"ServicoWhatsApp.{ev} -= "),
                    $"a venda escuta {ev} ao entrar e solta ao sair");
            checar(vendaCs.Contains("WhatsAppSessaoMudou(ServicoWhatsApp.Sessao);"), "ao entrar na venda, pinta o estado que já existe (voltou do KDS)");
            checar(vendaCs.Contains("if (_comanda.Count == 0) Alerta.WhatsAppCaiu();"), "som de caído só sem comanda aberta");
            checar(alerta.Contains("public static void WhatsAppCaiu()") && alerta.Contains("SomDaLoja.ChatIfood") && alerta.Contains("SomDaLoja.PedidoNovo"),
                "Alerta tem o som de caído, e o chat do iFood e o pedido novo aceitam o .wav da loja");
            checar(main.Contains("<TranslateTransform X=\"30000\"/>") && !Regex.IsMatch(main, @"x:Name=""CamadaWhatsApp""[^>]*Visibility=""Collapsed"""),
                "a camada do WhatsApp nasce EMPURRADA para fora da janela, não recolhida (a página fica visível para o Chromium)");
            checar(mainCs.Contains("ServicoWhatsApp.Ligar(") && mainCs.Contains("private void EsconderWhatsApp()")
                   && mainCs.Contains("CamadaWhatsApp.Voltou += EsconderWhatsApp") && mainCs.Contains("Keyboard.ClearFocus()"),
                "o MainWindow liga a memória, esconde empurrando e devolve o teclado ao caixa");

            var textosXaml = Regex.Matches(venda, @"(?:Text|ToolTip|Content)=""([^""]*)""").Select(x => x.Groups[1].Value).ToList();
            checar(textosXaml.All(x => !x.Contains('—') && !x.Contains('–')), "sem travessão nos textos da venda");
        }
    }

    private static string Trecho(string todo, string de, string ate)
    {
        var i = todo.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var f = todo.IndexOf(ate, i + de.Length, StringComparison.Ordinal);
        return f < 0 ? todo[i..] : todo[i..f];
    }

    private static string? Fonte(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj")))
            {
                var c = Path.Combine(dir.FullName, relativo);
                return File.Exists(c) ? File.ReadAllText(c) : null;
            }
        return null;
    }
}
