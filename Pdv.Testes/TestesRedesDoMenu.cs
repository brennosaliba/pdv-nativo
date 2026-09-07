using Pdv.Nucleo;
using Pdv.Telas;

namespace Pdv.Testes;

/// <summary>
/// A lista de redes que a loja deixa aparecer no menu de seleção da rede (`tef_pgweb_redes`).
///
/// De onde veio: o roteiro de homologação v20260819 usa TRÊS autorizadores e nenhum outro. C6PAY
/// na maioria das vendas, REDE no passo 38 e PIX C6 BANK nos passos 11, 55 e 56. O terminal, no
/// entanto, lista tudo o que estiver instalado nele, e escolher o vizinho errado no meio de um
/// passo queima a venda e o passo. Na loja o problema é o mesmo, só que com dinheiro de verdade.
///
/// O que NÃO dá para fazer é fixar a rede (`tef_paygo_rede`): com rede fixa a biblioteca para de
/// perguntar, o menu some, e some com ele o passo 05, que manda "realizar uma venda e no menu de
/// seleção da rede, pressionar a tecla Esc" esperando OPERAÇÃO CANCELADA. Por isso esta lista
/// ENCURTA o menu em vez de responder por ele: o menu continua aparecendo, nem que sobre uma
/// opção só, e o Esc continua sendo do operador.
///
/// As armadilhas que esta suíte segura, uma por uma:
///
///   1. lista vazia mostra TUDO (é o padrão, e é o certo numa loja: quem sabe o que está
///      credenciado no terminal é o PayGo);
///   2. filtro que não casa com NADA mostra tudo e deixa a linha na auditoria. Menu vazio no meio
///      de uma venda é pior do que filtro que não pegou: não dá para escolher nem para entender;
///   3. o valor que vai para a biblioteca é sempre o VALOR da opção DELA, nunca o texto digitado
///      na Configuração. É por isso que o filtro roda no provedor, antes de chamar a tela: os
///      botões e o valor devolvido saem da mesma lista, e não existe índice de uma valendo na
///      outra;
///   4. só o menu de rede (PWINFO_AUTHSYST) é filtrado. Menu administrativo e menu genérico
///      passam inteiros.
/// </summary>
public static class TestesRedesDoMenu
{
    private static string? Fonte(params string[] caminho)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var alvo = Path.Combine(new[] { d.FullName }.Concat(caminho).ToArray());
            if (File.Exists(alvo)) return File.ReadAllText(alvo);
        }
        return null;
    }

    /// <summary>O menu de redes como a biblioteca manda: texto igual ao valor, na ordem dela.</summary>
    private static PwGetData MenuRede(params string[] redes)
        => new(PW.PWDAT_MENU, PW.PWINFO_AUTHSYST, "REDE", redes.Select(r => new PwOpcaoMenu(r, r)).ToList());

    private static string Textos(PwGetData d) => string.Join("|", RespostaDaTela.Textos(d));

    public static void Rodar(Action<bool, string> checar)
    {
        // ── 1. a lista da config, quebrada ────────────────────────────────────
        {
            checar(FiltroRedes.Ler(null).Count == 0 && FiltroRedes.Ler("").Count == 0 && FiltroRedes.Ler("   ").Count == 0
                   && FiltroRedes.Ler(" , , ").Count == 0,
                "config em branco (ou só separadores) é lista vazia, que é o 'mostra tudo'");
            checar(FiltroRedes.Ler("C6PAY, REDE, PIX C6 BANK").SequenceEqual(new[] { "C6PAY", "REDE", "PIX C6 BANK" }),
                "vírgula separa e o espaço das pontas cai: " + string.Join("|", FiltroRedes.Ler("C6PAY, REDE, PIX C6 BANK")));
            checar(FiltroRedes.Ler(" c6pay ;REDE\nPIX C6 BANK ").SequenceEqual(new[] { "c6pay", "REDE", "PIX C6 BANK" }),
                "ponto e vírgula e quebra de linha também separam (é o que sai de um texto colado)");
            checar(FiltroRedes.Texto(new[] { "  c6pay ,, rede  " }) == "c6pay, rede",
                "Texto(): o que a Configuração grava sai arrumado, sem vazio e sem espaço sobrando");
            checar(FiltroRedes.Texto(new[] { "   " }) == "" && FiltroRedes.Texto(null) == "",
                "e em branco continua em branco, para a Configuração APAGAR a chave");
        }

        // ── 2. o filtro no menu de rede ───────────────────────────────────────
        var cinco = MenuRede("C6PAY", "REDE", "CIELO", "PIX C6 BANK", "PIX ITAU");
        {
            checar(ReferenceEquals(FiltroRedes.Aplicar(cinco, null), cinco)
                   && ReferenceEquals(FiltroRedes.Aplicar(cinco, Array.Empty<string>()), cinco),
                "lista vazia: o menu vai inteiro para a tela, do jeito que a biblioteca mandou");

            var so3 = FiltroRedes.Aplicar(cinco, new[] { "C6PAY", "REDE", "PIX C6 BANK" });
            checar(Textos(so3) == "C6PAY|REDE|PIX C6 BANK",
                "com a lista da loja, o caixa vê só as três do roteiro, na ordem da biblioteca: " + Textos(so3));
            checar(Textos(cinco) == "C6PAY|REDE|CIELO|PIX C6 BANK|PIX ITAU", "e o pedido original não foi mexido: " + Textos(cinco));

            var torto = FiltroRedes.Aplicar(cinco, new[] { "  c6pay  ", "pix itaú", "Rede" });
            checar(Textos(torto) == "C6PAY|REDE|PIX ITAU",
                "casa sem ligar para maiúscula, acento nem espaço nas pontas: " + Textos(torto));

            var umaSo = FiltroRedes.Aplicar(cinco, new[] { "C6PAY" });
            checar(umaSo.Opcoes is { Count: 1 } && umaSo.EhMenu && umaSo.Identificador == PW.PWINFO_AUTHSYST,
                "sobrando uma rede, ainda é MENU (é onde o passo 05 aperta Esc), não uma resposta pronta");

            var tudo = FiltroRedes.Aplicar(cinco, new[] { "C6PAY", "REDE", "CIELO", "PIX C6 BANK", "PIX ITAU" });
            checar(ReferenceEquals(tudo, cinco), "filtro que deixa tudo passar devolve o mesmo pedido, sem encurtar nada");
        }

        // ── 3. o valor devolvido é o da BIBLIOTECA, nunca o texto da config ───
        {
            // Menu em que texto e valor são diferentes, que é o caso que quebra quando alguém
            // filtra a lista dos botões e responde pela lista de antes.
            var porCodigo = new PwGetData(PW.PWDAT_MENU, PW.PWINFO_AUTHSYST, "REDE", new[]
            {
                new PwOpcaoMenu("C6PAY", "07"), new PwOpcaoMenu("REDE", "12"), new PwOpcaoMenu("CIELO", "33"),
            });
            var f = FiltroRedes.Aplicar(porCodigo, new[] { "c6pay", "rede" });
            checar(Textos(f) == "C6PAY|REDE", "casa também pelo TEXTO da opção quando o valor é um código: " + Textos(f));
            checar(RespostaDaTela.Menu(f, 0) == "07" && RespostaDaTela.Menu(f, 1) == "12",
                "e o que volta para a biblioteca é o VALOR dela (07/12), não o que foi digitado na config: "
                + RespostaDaTela.Menu(f, 0));
            checar(RespostaDaTela.Menu(f, 2) is null, "o índice que sumiu do menu encurtado não vira escolha: nada além da lista");
            checar(RespostaDaTela.Menu(f, -1) is null, "e o Esc (índice -1) continua cancelando no menu encurtado");

            var porValor = FiltroRedes.Aplicar(porCodigo, new[] { "33" });
            checar(Textos(porValor) == "CIELO" && RespostaDaTela.Menu(porValor, 0) == "33",
                "quem escreveu o código na config também casa (o valor da opção): " + Textos(porValor));

            // A prova de que botão e resposta saem da MESMA lista: para todo índice, o texto do
            // botão é o texto da opção cujo valor volta.
            var alinhado = true;
            for (var i = 0; i < RespostaDaTela.Textos(f).Count; i++)
                alinhado &= porCodigo.Opcoes!.First(o => o.Texto == RespostaDaTela.Textos(f)[i]).Valor == RespostaDaTela.Menu(f, i);
            checar(alinhado, "botão tocado e valor devolvido são da mesma opção, índice por índice");
        }

        // ── 4. filtro que não casa com nada: mostra tudo e AVISA ──────────────
        {
            var linhas = new List<string>();
            var r = FiltroRedes.Aplicar(cinco, new[] { "BANCO XPTO", "MAQUININHA DA ESQUINA" }, linhas.Add);
            checar(ReferenceEquals(r, cinco), "nada casou: o menu vai INTEIRO, nunca vazio");
            checar(linhas.Count == 1 && linhas[0].Contains("não casaram", StringComparison.Ordinal)
                   && linhas[0].Contains("BANCO XPTO", StringComparison.Ordinal) && linhas[0].Contains("mostrando todas", StringComparison.Ordinal),
                "e a auditoria diz que o filtro não pegou: " + (linhas.Count > 0 ? linhas[0] : "nenhuma linha"));
            checar(linhas.All(l => !l.Contains('—') && !l.Contains('–')), "auditoria sem travessão nem meia-risca");

            var encurtou = new List<string>();
            FiltroRedes.Aplicar(cinco, new[] { "C6PAY" }, encurtou.Add);
            checar(encurtou.Count == 1 && encurtou[0].Contains("1 de 5", StringComparison.Ordinal),
                "quando encurta, a auditoria conta quantas ficaram (para a homologação ter prova): "
                + (encurtou.Count > 0 ? encurtou[0] : "nenhuma linha"));

            var quieto = new List<string>();
            FiltroRedes.Aplicar(cinco, Array.Empty<string>(), quieto.Add);
            FiltroRedes.Aplicar(cinco, new[] { "C6PAY", "REDE", "CIELO", "PIX C6 BANK", "PIX ITAU" }, quieto.Add);
            checar(quieto.Count == 0, "sem filtro (ou com filtro que não muda nada) a auditoria fica quieta");
        }

        // ── 5. só o menu de REDE é filtrado ───────────────────────────────────
        {
            var adm = new PwGetData(PW.PWDAT_MENU, 32701, "ADMINISTRATIVA", new[]
            {
                new PwOpcaoMenu("TESTE DE COMUNICACAO", "1"), new PwOpcaoMenu("REIMPRESSAO", "2"), new PwOpcaoMenu("RELATORIO", "3"),
            });
            var linhas = new List<string>();
            checar(ReferenceEquals(FiltroRedes.Aplicar(adm, new[] { "RELATORIO" }, linhas.Add), adm) && linhas.Count == 0,
                "menu que não é de rede passa inteiro, mesmo que um item tenha o nome da lista");

            var digitado = new PwGetData(PW.PWDAT_TYPED, PW.PWINFO_AUTHSYST, "REDE");
            checar(ReferenceEquals(FiltroRedes.Aplicar(digitado, new[] { "C6PAY" }), digitado),
                "dado digitado não é menu: nada a encurtar");

            var vazio = new PwGetData(PW.PWDAT_MENU, PW.PWINFO_AUTHSYST, "REDE");
            checar(ReferenceEquals(FiltroRedes.Aplicar(vazio, new[] { "C6PAY" }), vazio), "menu sem opção nenhuma sai como veio");
        }

        // ── 6. a chave da config e as opções do provedor ──────────────────────
        {
            static Func<string, string?> Cfg(params (string Chave, string? Valor)[] pares)
            {
                var d = pares.ToDictionary(p => p.Chave, p => p.Valor);
                return k => d.GetValueOrDefault(k);
            }
            checar(ConfigPGWebLib.ChaveRedes == "tef_pgweb_redes", "a chave é tef_pgweb_redes: " + ConfigPGWebLib.ChaveRedes);
            checar(ConfigPGWebLib.Redes(Cfg()).Count == 0 && ConfigPGWebLib.Redes(Cfg((ConfigPGWebLib.ChaveRedes, "  "))).Count == 0,
                "chave ausente ou em branco: sem filtro (o caixa novo mostra todas)");
            var op = ConfigPGWebLib.Opcoes(Cfg((ConfigPGWebLib.ChaveRedes, "C6PAY, REDE, PIX C6 BANK")), "0.6.0");
            checar(op.RedesPermitidas is { Count: 3 } && op.RedesPermitidas.SequenceEqual(new[] { "C6PAY", "REDE", "PIX C6 BANK" }),
                "ConfigPGWebLib.Opcoes leva a lista para o provedor");
            checar(ConfigPGWebLib.Opcoes(Cfg(), "0.6.0").RedesPermitidas is { Count: 0 },
                "e sem a chave o provedor recebe lista vazia, que é o comportamento de sempre");
        }

        // ── 7. a venda de verdade, contra a biblioteca de mentira ─────────────
        var pasta = TestesPGWebLib.PastaTeste;
        Directory.CreateDirectory(pasta);

        ProvedorPGWebLib Provedor(IPGWebLib lib, Func<PwGetData, CancellationToken, Task<string?>> perguntar,
            string[]? permitidas = null, Action<string>? auditar = null, Func<TransacaoPayGo, bool>? guardar = null)
            => new(lib, pasta, new OpcoesPGWebLib("Pdv.AmericanDay", "0.5.9", "American Day", RedesPermitidas: permitidas))
            {
                IntervaloPollMs = 5,
                TempoMaxExecMs = 2000,
                TempoMaxCapturaMs = 2000,
                TempoPerguntaMs = 500,
                Perguntar = perguntar,
                Auditar = auditar,
                Guardar = guardar ?? (_ => true),
            };

        // 7a. o caixa vê só as três do roteiro, e a que ele toca é a que a biblioteca recebe
        {
            var f = new FakePGWebLib { RedesDoMenu = new[] { "CIELO", "C6PAY", "STONE", "REDE", "PIX C6 BANK" } };
            var vistos = new List<PwGetData>();
            var p = Provedor(f, (d, _) =>
            {
                vistos.Add(d);
                // A tela responde exatamente como Servicos.PerguntarNaTelaAsync: índice tocado
                // vira valor pelo MESMO PwGetData que desenhou os botões.
                var i = RespostaDaTela.Textos(d).ToList().IndexOf("C6PAY");
                return Task.FromResult(RespostaDaTela.Menu(d, i));
            }, permitidas: new[] { "C6PAY", "REDE", "PIX C6 BANK" });
            var d = p.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(10m), null, 1, null, CancellationToken.None)
                     .GetAwaiter().GetResult();

            checar(vistos.Count == 1 && Textos(vistos[0]) == "C6PAY|REDE|PIX C6 BANK",
                "na venda, o menu que chega à tela tem só as redes da loja: " + (vistos.Count > 0 ? Textos(vistos[0]) : "nenhum menu"));
            checar(d.Pago && f.Ultima?.Params.GetValueOrDefault(PW.PWINFO_AUTHSYST) == "C6PAY",
                "e a rede que foi para a biblioteca é a que o operador tocou: " + f.Ultima?.Params.GetValueOrDefault(PW.PWINFO_AUTHSYST));
        }

        // 7b. PASSO 05 com o filtro ligado: o menu APARECE e o Esc nega a venda
        // Este é o bloco que impede a "melhoria" de responder o menu sozinho quando sobra uma
        // rede. Sobrando uma, ainda tem que haver menu: é nele que o roteiro aperta Esc.
        {
            var f = new FakePGWebLib { RedesDoMenu = new[] { "CIELO", "C6PAY", "STONE" } };
            var vistos = new List<PwGetData>();
            var guardadas = new List<TransacaoPayGo>();
            var p = Provedor(f, (g, _) => { vistos.Add(g); return Task.FromResult<string?>(null); },
                permitidas: new[] { "C6PAY" }, guardar: t => { guardadas.Add(t); return true; });
            var d = p.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(10m), null, 1, null, CancellationToken.None)
                     .GetAwaiter().GetResult();

            checar(vistos.Count == 1 && vistos[0].EhMenu && vistos[0].Opcoes is { Count: 1 } && Textos(vistos[0]) == "C6PAY",
                "com uma rede sobrando o menu AINDA aparece (passo 05 continua possível): "
                + (vistos.Count > 0 ? Textos(vistos[0]) : "nenhum menu"));
            checar(!d.Pago && d.Situacao == SituacaoTef.Cancelado && d.Codigo == CodigoTef.Cancelado,
                "e o Esc nele nega a venda, como o roteiro espera: " + d.Situacao);
            checar(f.Confirmadas.Count == 0 && guardadas.Count > 0 && guardadas[^1].Situacao == "cancelado",
                "nada confirmado na biblioteca e a linha de tef_transacao fica 'cancelado'");
        }

        // 7c. filtro que não casa com nada, no meio de uma venda: mostra tudo e audita
        {
            var f = new FakePGWebLib { RedesDoMenu = new[] { "CIELO", "C6PAY", "STONE" } };
            var vistos = new List<PwGetData>();
            var auditoria = new List<string>();
            var p = Provedor(f, (g, _) => { vistos.Add(g); return Task.FromResult(RespostaDaTela.Menu(g, 0)); },
                permitidas: new[] { "BANCO XPTO" }, auditar: auditoria.Add);
            var d = p.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(10m), null, 1, null, CancellationToken.None)
                     .GetAwaiter().GetResult();

            checar(vistos.Count == 1 && Textos(vistos[0]) == "CIELO|C6PAY|STONE",
                "lista errada não deixa o operador sem menu: aparecem todas: " + (vistos.Count > 0 ? Textos(vistos[0]) : "nenhum menu"));
            checar(d.Pago && f.Ultima?.Params.GetValueOrDefault(PW.PWINFO_AUTHSYST) == "CIELO",
                "a venda segue com a rede escolhida na tela");
            checar(auditoria.Any(l => l.Contains("não casaram", StringComparison.Ordinal) && l.Contains("mostrando todas", StringComparison.Ordinal)),
                "e fica registrado que a lista da loja não pegou");
        }

        // 7d. o menu que NÃO é de rede não é filtrado, na venda
        {
            var f = new FakePGWebLib { RedesDoMenu = new[] { "C6PAY", "CIELO" }, MenuGenericoVezes = 1 };
            var vistos = new List<PwGetData>();
            var p = Provedor(f, (g, _) => { vistos.Add(g); return Task.FromResult(RespostaDaTela.Menu(g, 0)); },
                // "ABCDEF" está na lista de propósito: se o filtro escapasse para o menu genérico,
                // o operador veria só ela, e o passo 30 do roteiro pede as duas opções.
                permitidas: new[] { "C6PAY", "ABCDEF" });
            var d = p.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(10m), null, 1, null, CancellationToken.None)
                     .GetAwaiter().GetResult();

            var generico = vistos.FirstOrDefault(g => g.Identificador == FakePGWebLib.IdMenuGenerico);
            checar(vistos.Count == 2 && Textos(vistos[0]) == "C6PAY", "o menu de rede saiu encurtado");
            checar(generico is not null && Textos(generico) == "123456|ABCDEF",
                "e o menu genérico saiu INTEIRO: " + (generico is null ? "não veio" : Textos(generico)));
            checar(d.Pago, "a venda fecha normalmente");
        }

        // ── 8. como o dono liga isso: a tela de Configuração ──────────────────
        {
            var xaml = Fonte("Telas", "Configuracao.xaml") ?? "";
            var cfg = Fonte("Telas", "Configuracao.xaml.cs") ?? "";
            checar(xaml.Length > 0 && cfg.Length > 0, "achei a Configuração (xaml e xaml.cs)");
            checar(xaml.Contains("x:Name=\"TxtPgwebRedes\"", StringComparison.Ordinal)
                   && xaml.Contains("Redes que aparecem para o caixa escolher", StringComparison.Ordinal),
                "o bloco da biblioteca tem o campo das redes, com rótulo que se lê sem manual");
            checar(xaml.Contains("Em branco aparecem todas as redes do terminal", StringComparison.Ordinal),
                "e a frase curta diz o que acontece deixando em branco");
            checar(cfg.Contains("TxtPgwebRedes.Text = Vendas.Config(cx, ConfigPGWebLib.ChaveRedes", StringComparison.Ordinal)
                   && cfg.Contains("Chave(ConfigPGWebLib.ChaveRedes,", StringComparison.Ordinal)
                   && cfg.Contains("ConfigPGWebLib.ChaveDll, ConfigPGWebLib.ChaveRedes,", StringComparison.Ordinal),
                "a chave é lida, gravada e restaurada no Sair sem salvar");
            checar(cfg.Contains("PgwebRedes = TxtPgwebRedes.Text", StringComparison.Ordinal), "e vai para o resumo do fim");

            var comFiltro = AssistenteConfig.Resumo(new DadosAssistente { Tef = 4, PgwebRedes = " c6pay, rede , PIX C6 BANK " })
                .First(l => l.Titulo == "Maquininha").Valor;
            checar(comFiltro.Contains("o caixa só escolhe entre c6pay, rede, PIX C6 BANK", StringComparison.Ordinal),
                "o resumo do fim mostra a lista encurtada (é escolha que o caixa vê na venda): " + comFiltro);
            var semFiltro = AssistenteConfig.Resumo(new DadosAssistente { Tef = 4 })
                .First(l => l.Titulo == "Maquininha").Valor;
            checar(!semFiltro.Contains("só escolhe", StringComparison.Ordinal),
                "e sem lista o resumo não inventa linha nenhuma: " + semFiltro);
            checar(!comFiltro.Contains('—') && !comFiltro.Contains('–'), "resumo sem travessão nem meia-risca");
        }

        // ── 9. o guia que o dono abre na manhã da homologação ─────────────────
        // Campo novo que ninguém sabe que existe é campo que não existe. O guia mandava o
        // contrário do certo: gravar C6PAY de volta depois do passo 05. Com C6PAY gravada a
        // rede vira fixa, o menu não abre, e ficam impossíveis os passos que pedem REDE
        // (38, 40, 45 e 46) e os que o roteiro descreve sem pré-seleção (06, 07 e 08).
        {
            // O guia quebra linha onde couber, e a frase que interessa cai no meio da quebra:
            // compara com o texto corrido, senão o teste passa a depender da largura da coluna.
            var guia = (Fonte("docs", "HOMOLOGACAO_COMECAR_AQUI.md") ?? "")
                .Replace((char)13, ' ').Replace((char)10, ' ');
            checar(guia.Length > 0, "achei o guia da homologação");
            checar(guia.Contains("Redes que aparecem para o caixa escolher", StringComparison.Ordinal)
                   && guia.Contains("C6PAY, REDE, PIX C6 BANK", StringComparison.Ordinal),
                "o guia mostra o campo novo e o que digitar nele");
            checar(guia.Contains("Deixe a rede do cartão em branco o roteiro inteiro", StringComparison.Ordinal),
                "e manda deixar a rede do cartão em branco o roteiro inteiro, não só no passo 05");
            checar(!guia.Contains("grave C6PAY de volta", StringComparison.Ordinal),
                "o conselho antigo saiu: gravar C6PAY de volta fecha o menu e mata os passos de REDE");
            checar(guia.Contains("dois a menos", StringComparison.Ordinal),
                "o guia avisa que os 'PASSO NN' que o roteiro cita estão dois a menos que o passo real");
            checar(guia.Contains("TRANSAÇÃO NEGADA PELO HOST", StringComparison.Ordinal),
                "e que o cancelamento do passo 57 é NEGADO, para ninguém gravar como falha do caixa");
        }
    }
}
