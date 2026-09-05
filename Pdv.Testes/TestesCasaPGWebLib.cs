using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A LIGAÇÃO do provedor PGWebLib na casa: seleção pela config (`tef_provedor`), opções
/// montadas das chaves `tef_pgweb_*` (e das `tef_paygo_*` reaproveitadas), o callback de
/// menu/dado digitado que a tela responde com os diálogos da casa, e a prova de que os
/// caminhos fixados por nome ('paygo','controlpay') também deixam o terceiro provedor passar.
/// Nada aqui abre janela: a tela é só o tradutor PwGetData ↔ diálogo, e o tradutor é puro.
/// </summary>
public static class TestesCasaPGWebLib
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

    private static Func<string, string?> Cfg(params (string Chave, string? Valor)[] pares)
    {
        var d = pares.ToDictionary(p => p.Chave, p => p.Valor);
        return k => d.GetValueOrDefault(k);
    }

    public static void Rodar(Action<bool, string> checar)
    {
        // ── seleção do provedor pela config ───────────────────────────────
        {
            checar(SelecaoTef.Escolher("0", "pgweblib") == ProvedorTef.Nenhum && SelecaoTef.Escolher(null, "paygo") == ProvedorTef.Nenhum,
                "tef_habilitado != 1 é Nenhum, seja qual for o provedor");
            checar(SelecaoTef.Escolher("1", "pgweblib") == ProvedorTef.PGWebLib, "tef_provedor = pgweblib seleciona a biblioteca");
            checar(SelecaoTef.Escolher("1", "paygo") == ProvedorTef.PayGo && SelecaoTef.Escolher("1", "controlpay") == ProvedorTef.ControlPay,
                "paygo e controlpay continuam onde estavam");
            checar(SelecaoTef.Escolher("1", null) == ProvedorTef.Nuvem && SelecaoTef.Escolher("1", "nuvem") == ProvedorTef.Nuvem && SelecaoTef.Escolher("1", "xyz") == ProvedorTef.Nuvem,
                "qualquer outro valor (ou nenhum) cai na nuvem, o caminho antigo");
            checar(SelecaoTef.Escolher("1", " PGWebLib ") == ProvedorTef.PGWebLib, "o código é comparado sem caixa e sem espaços");

            checar(SelecaoTef.Codigo(ProvedorTef.PGWebLib) == "pgweblib" && SelecaoTef.Codigo(ProvedorTef.PayGo) == "paygo"
                   && SelecaoTef.Codigo(ProvedorTef.ControlPay) == "controlpay" && SelecaoTef.Codigo(ProvedorTef.Nuvem) == "nuvem"
                   && SelecaoTef.Codigo(ProvedorTef.Nenhum) == "nuvem",
                "Codigo(): o que a Configuração grava em tef_provedor (Nenhum grava 'nuvem' + tef_habilitado=0, como sempre)");
            foreach (var p in new[] { ProvedorTef.Nuvem, ProvedorTef.PayGo, ProvedorTef.ControlPay, ProvedorTef.PGWebLib })
                checar(SelecaoTef.Escolher("1", SelecaoTef.Codigo(p)) == p, "ida e volta Codigo/Escolher: " + p);

            checar(SelecaoTef.Modo(ProvedorTef.Nenhum) == 0 && SelecaoTef.Modo(ProvedorTef.Nuvem) == 1 && SelecaoTef.Modo(ProvedorTef.PayGo) == 2
                   && SelecaoTef.Modo(ProvedorTef.ControlPay) == 3 && SelecaoTef.Modo(ProvedorTef.PGWebLib) == 4,
                "modo da Configuração: 0 sem · 1 nuvem · 2 PayGo arquivos · 3 ControlPay · 4 PayGo biblioteca");
            for (var m = 0; m <= 4; m++) checar(SelecaoTef.Modo(SelecaoTef.DeModo(m)) == m, "ida e volta Modo/DeModo " + m);
            checar(SelecaoTef.DeModo(99) == ProvedorTef.Nenhum && SelecaoTef.DeModo(-1) == ProvedorTef.Nenhum, "modo desconhecido é Nenhum (nunca liga maquininha por engano)");

            checar(SelecaoTef.Integrados.SequenceEqual(new[] { "paygo", "controlpay", "pgweblib" }), "os três provedores integrados, na ordem em que nasceram");
            checar(SelecaoTef.SqlIntegrados == "('paygo','controlpay','pgweblib')", "o fragmento SQL dos integrados inclui pgweblib: " + SelecaoTef.SqlIntegrados);
            checar(SelecaoTef.EhIntegrado("pgweblib") && SelecaoTef.EhIntegrado("paygo") && !SelecaoTef.EhIntegrado("nuvem") && !SelecaoTef.EhIntegrado(null) && !SelecaoTef.EhIntegrado(""),
                "EhIntegrado: só os três; nuvem/vazio/nulo não");
        }

        // ── opções da biblioteca a partir das chaves de config ─────────────
        {
            checar(ConfigPGWebLib.DirPadrao == @"C:\ProgramData\PdvNativo\pgweb" && ProvedorPGWebLib.PastaPadrao == ConfigPGWebLib.DirPadrao,
                "diretório de trabalho padrão C:\\ProgramData\\PdvNativo\\pgweb, o mesmo que o provedor usa em branco");
            checar(ConfigPGWebLib.Diretorio(Cfg()) == ConfigPGWebLib.DirPadrao && ConfigPGWebLib.Diretorio(Cfg(("tef_pgweb_dir", "  "))) == ConfigPGWebLib.DirPadrao,
                "sem tef_pgweb_dir (ou em branco) vale o padrão");
            checar(ConfigPGWebLib.Diretorio(Cfg(("tef_pgweb_dir", @" D:\pg "))) == @"D:\pg", "tef_pgweb_dir preenchido manda (sem espaços nas pontas)");
            checar(ConfigPGWebLib.ChaveDir == "tef_pgweb_dir" && ConfigPGWebLib.ChavePortaPinpad == "tef_pgweb_porta_pinpad" && ConfigPGWebLib.ChaveCapacidades == "tef_pgweb_capacidades"
                   && ConfigPGWebLib.ChaveDll == "tef_pgweb_dll",
                "nomes das chaves novas");
            checar(ConfigPGWebLib.PastaDll(Cfg()) is null && ConfigPGWebLib.PastaDll(Cfg(("tef_pgweb_dll", "  "))) is null && ConfigPGWebLib.PastaDll(Cfg(("tef_pgweb_dll", @" D:\pg\dll "))) == @"D:\pg\dll",
                "tef_pgweb_dll em branco = null (o Windows procura); preenchido manda, sem espaços nas pontas");

            var vazio = ConfigPGWebLib.Opcoes(Cfg(), "0.5.9");
            checar(vazio.NomeAutomacao == "Pdv.AmericanDay" && vazio.VersaoAutomacao == "0.5.9", "AUTNAME/AUTVER: nome fixo da automação e a versão do exe");
            checar(vazio.Desenvolvedor == "American Day", "AUTDEV em branco: American Day (mesmo default do 716 do PayGo por arquivos)");
            checar(vazio.Capacidades == ProvedorPGWebLib.CapacidadesPadrao && vazio.PortaPinpad == "0" && vazio.Moeda == "986", "AUTCAP padrão, porta automática, moeda 986");
            checar(vazio.RedeCartao is null && vazio.RedePix is null, "sem rede gravada: null (a biblioteca abre o menu, a tela responde)");

            var cheio = ConfigPGWebLib.Opcoes(Cfg(
                ("tef_paygo_empresa", " AMERICAN DAY LTDA "), ("tef_pgweb_capacidades", "156"), ("tef_pgweb_porta_pinpad", "3"),
                ("tef_paygo_rede", "REDE"), ("tef_paygo_rede_pix", "PIX ITAU")), "1.2.3");
            checar(cheio.Desenvolvedor == "AMERICAN DAY LTDA" && cheio.Capacidades == 156 && cheio.PortaPinpad == "3"
                   && cheio.RedeCartao == "REDE" && cheio.RedePix == "PIX ITAU" && cheio.VersaoAutomacao == "1.2.3",
                "reaproveita tef_paygo_empresa/rede/rede_pix e lê as chaves tef_pgweb_*");
            checar(ConfigPGWebLib.Opcoes(Cfg(("tef_pgweb_capacidades", "abc")), "1").Capacidades == ProvedorPGWebLib.CapacidadesPadrao
                   && ConfigPGWebLib.Opcoes(Cfg(("tef_pgweb_capacidades", "-5")), "1").Capacidades == ProvedorPGWebLib.CapacidadesPadrao,
                "AUTCAP inválido ou negativo cai no padrão");
            checar(ConfigPGWebLib.Opcoes(Cfg(("tef_paygo_rede", "  "), ("tef_pgweb_porta_pinpad", " ")), "1") is { RedeCartao: null, PortaPinpad: "0" },
                "rede/porta em branco viram null/\"0\", nunca string vazia para a biblioteca");
        }

        // ── o tradutor PwGetData ↔ diálogo da casa ────────────────────────
        {
            var menuRede = new PwGetData(PW.PWDAT_MENU, PW.PWINFO_AUTHSYST, "SELECIONE A REDE", new[]
            {
                new PwOpcaoMenu("REDE", "REDE"), new PwOpcaoMenu("CIELO", "CIELO"), new PwOpcaoMenu("STONE", "STONE"),
            });
            checar(RespostaDaTela.Titulo(menuRede) == "Rede", "menu AUTHSYST tem título 'Rede'");
            checar(RespostaDaTela.Textos(menuRede).SequenceEqual(new[] { "REDE", "CIELO", "STONE" }), "os textos do menu vão para os botões, na ordem");
            checar(RespostaDaTela.Menu(menuRede, 1) == "CIELO", "índice tocado vira o VALOR da opção");
            checar(RespostaDaTela.Menu(menuRede, -1) is null && RespostaDaTela.Menu(menuRede, 3) is null, "Voltar/Esc (-1) ou índice fora: null = cancela");
            var menuValorDiferente = menuRede with { Opcoes = new[] { new PwOpcaoMenu("Teste de comunicação", "1"), new PwOpcaoMenu("Relatório", "3") } };
            checar(RespostaDaTela.Menu(menuValorDiferente, 1) == "3", "quando texto e valor diferem, devolve o valor (o que a biblioteca entende)");
            checar(RespostaDaTela.Titulo(menuValorDiferente with { Identificador = 32701, Prompt = "ADMINISTRATIVA" }) == "ADMINISTRATIVA",
                "menu que não é de rede usa o prompt da biblioteca como título");
            checar(RespostaDaTela.Titulo(menuRede with { Identificador = 32701, Prompt = "" }) == "Maquininha", "sem prompt: 'Maquininha'");

            var parcelas = new PwGetData(PW.PWDAT_TYPED, PW.PWINFO_INSTALLMENTS, "NUMERO DE PARCELAS", TamanhoMinimo: 1, TamanhoMaximo: 2);
            checar(RespostaDaTela.EhParcelas(parcelas) && !RespostaDaTela.EhParcelas(menuRede), "INSTALLMENTS digitado é a pergunta de parcelas");
            checar(RespostaDaTela.Titulo(parcelas) == "Parcelas no crédito" && RespostaDaTela.Rotulo(parcelas) == "Em quantas vezes? (1 = à vista, até 99)",
                "parcelas: mesmo título e mesma pergunta da tela de pagamento");
            checar(RespostaDaTela.Sugestao(parcelas) == "1", "parcelas sugere 1 (à vista)");
            checar(RespostaDaTela.Digitado(parcelas, "3") == ("3", null), "parcelas '3' passa");
            checar(RespostaDaTela.Digitado(parcelas, "0").Erro is not null && RespostaDaTela.Digitado(parcelas, "abc").Erro is not null && RespostaDaTela.Digitado(parcelas, "100").Erro is not null,
                "parcelas 0, 'abc' e 100 não passam (e a tela pergunta de novo, não chuta à vista)");
            checar(RespostaDaTela.Digitado(parcelas, "0").Erro == "Digite um número de 1 a 99.", "a frase do erro é a da tela de pagamento");
            checar(RespostaDaTela.Digitado(parcelas, null) == (null, null), "cancelou = (null, null): o provedor cancela a captura");

            var senha = new PwGetData(PW.PWDAT_USERAUTH, 32700, "SENHA DO LOJISTA", TamanhoMinimo: 4, TamanhoMaximo: 8);
            checar(RespostaDaTela.Ocultar(senha) && !RespostaDaTela.Ocultar(parcelas) && RespostaDaTela.Ocultar(parcelas with { Ocultar = true }),
                "USERAUTH (ou Ocultar) vai para o diálogo de senha, sem eco na tela");
            checar(RespostaDaTela.Titulo(senha) == "Senha do lojista", "título da senha");
            checar(RespostaDaTela.Digitado(senha, " 1234 ") == ("1234", null), "dado digitado sai sem espaços nas pontas");
            checar(RespostaDaTela.Digitado(senha, "12").Erro == "Digite entre 4 e 8 caracteres." && RespostaDaTela.Digitado(senha, "123456789").Erro is not null,
                "tamanho mínimo/máximo da biblioteca valem: " + RespostaDaTela.Digitado(senha, "12").Erro);
            var livre = new PwGetData(PW.PWDAT_TYPED, 40, "REFERENCIA", TamanhoMinimo: 0, TamanhoMaximo: 0, ValorInicial: "abc");
            checar(RespostaDaTela.Digitado(livre, "").Erro is not null && RespostaDaTela.Digitado(livre, "x") == ("x", null),
                "sem tamanhos (0/0) só o vazio é recusado");
            checar(RespostaDaTela.Digitado(livre with { AceitaNulo = true }, "") == ("", null), "AceitaNulo: vazio passa");
            checar(RespostaDaTela.Sugestao(livre) == "abc" && RespostaDaTela.Sugestao(senha) == "", "sugestão = ValorInicial da biblioteca; senha nunca sugere");
            checar(RespostaDaTela.Titulo(livre) == "REFERENCIA" && RespostaDaTela.Rotulo(livre) == "REFERENCIA", "dado livre: prompt como título e rótulo");
        }

        // ── o callback de ponta a ponta, respondido como a tela responde ──
        // A tela é isto: menu → índice tocado → RespostaDaTela.Menu; digitado → texto →
        // RespostaDaTela.Digitado. Contra a biblioteca de mentira, sem rede pré-selecionada,
        // a venda passa pelo menu e o estorno pela senha do lojista.
        {
            var f = new FakePGWebLib();
            var perguntas = new List<PwGetData>();
            var opcoes = ConfigPGWebLib.Opcoes(Cfg(), "0.5.9");
            Task<string?> ComoATela(PwGetData d, CancellationToken ct)
            {
                perguntas.Add(d);
                if (d.EhMenu)
                {
                    var textos = RespostaDaTela.Textos(d);
                    var idx = textos.ToList().IndexOf("CIELO");          // o dedo do operador
                    return Task.FromResult(RespostaDaTela.Menu(d, idx));
                }
                var (valor, erro) = RespostaDaTela.Digitado(d, RespostaDaTela.Ocultar(d) ? "1234" : "1");
                return Task.FromResult(erro is null ? valor : null);
            }
            var guardadas = new List<TransacaoPayGo>();
            var p = new ProvedorPGWebLib(f, ConfigPGWebLib.Diretorio(Cfg()), opcoes)
            {
                IntervaloPollMs = 5, TempoMaxExecMs = 2000, TempoMaxCapturaMs = 2000, TempoPerguntaMs = 500,
                Guardar = t => { guardadas.Add(t); return true; },
                Perguntar = ComoATela,
            };
            checar(p.PastaTrabalho == ConfigPGWebLib.DirPadrao, "o provedor nasce no diretório padrão");
            var d = p.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(25m), null, 1, null, CancellationToken.None).GetAwaiter().GetResult();
            checar(d.Pago && d.Cartao?.Adquirente == "CIELO", "venda sem rede gravada: o menu foi respondido pelo dedo (CIELO) e a venda passou");
            checar(perguntas.Count == 1 && perguntas[0].EhMenu && perguntas[0].Identificador == PW.PWINFO_AUTHSYST, "uma pergunta só: o menu de redes");
            checar(f.Ultima?.Params.GetValueOrDefault(PW.PWINFO_AUTHSYST) == "CIELO", "a biblioteca recebeu o VALOR da opção");

            var original = guardadas.Last(g => g.Situacao == "pago");
            var e = p.CancelarAsync(original, CancellationToken.None).GetAwaiter().GetResult();
            checar(e.Pago && e.PaymentStatus == "estornado", "estorno: a senha do lojista (USERAUTH) veio pelo mesmo callback e passou (" + e.Motivo + ")");
            checar(perguntas.Any(q => q.Tipo == PW.PWDAT_USERAUTH) && f.Ultima?.Params.GetValueOrDefault(FakePGWebLib.IdSenhaLojista) == "1234",
                "a senha foi pedida oculta e entregue à biblioteca");

            // Voltar no menu = venda cancelada, nada confirmado.
            var f2 = new FakePGWebLib();
            var p2 = new ProvedorPGWebLib(f2, "", opcoes)
            {
                IntervaloPollMs = 5, TempoMaxExecMs = 2000, TempoMaxCapturaMs = 2000, TempoPerguntaMs = 500,
                Perguntar = (q, _) => Task.FromResult(RespostaDaTela.Menu(q, -1)),
            };
            var d2 = p2.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(25m), null, 1, null, CancellationToken.None).GetAwaiter().GetResult();
            checar(!d2.Pago && d2.Situacao == SituacaoTef.Cancelado && f2.Confirmadas.Count == 0, "Voltar no menu de redes cancela a venda sem confirmar nada");
        }

        // ── a ligação na casa, pelo fonte (a tela só existe com WPF de pé) ──
        {
            var servicos = Fonte("Servicos.cs");
            checar(servicos is not null, "achei Servicos.cs");
            var s = servicos ?? "";
            checar(s.Contains("ProvedorTef.PGWebLib", StringComparison.Ordinal) && s.Contains("new ProvedorPGWebLib(", StringComparison.Ordinal),
                "Servicos.Tef() constrói o ProvedorPGWebLib quando a seleção diz PGWebLib");
            checar(s.Contains("SelecaoTef.Escolher(", StringComparison.Ordinal), "Servicos.Tef() seleciona pela mesma regra testada acima (SelecaoTef.Escolher)");
            var agulha = "new PGWebLib" + "Nativa(";   // montada em duas partes para este arquivo não se acusar lá embaixo
            checar(s.Contains(agulha, StringComparison.Ordinal), "a instância NATIVA (DllImport) é criada em Servicos, e só lá");
            checar(s.Contains("ConfigPGWebLib.Opcoes(", StringComparison.Ordinal) && s.Contains("ConfigPGWebLib.Diretorio(", StringComparison.Ordinal),
                "as opções e o diretório vêm de ConfigPGWebLib (as chaves testadas acima)");
            checar(s.Contains("Perguntar = PerguntarNaTelaAsync", StringComparison.Ordinal), "o callback Perguntar está ligado à tela");
            checar(s.Contains("RespostaDaTela.Menu(", StringComparison.Ordinal) && s.Contains("RespostaDaTela.Digitado(", StringComparison.Ordinal)
                   && s.Contains("Dialogo.Escolher(", StringComparison.Ordinal) && s.Contains("PedirSenha.Mostrar(", StringComparison.Ordinal) && s.Contains("PedirTexto.Mostrar(", StringComparison.Ordinal),
                "a tela responde com os diálogos da casa através do tradutor testado");
            checar(s.Contains("GuardarTef(t, \"pgweblib\")", StringComparison.Ordinal), "a linha de tef_transacao nasce com provedor='pgweblib'");
            checar(s.Contains("is ProvedorPGWebLib", StringComparison.Ordinal) && s.Contains("\"pgweb-%\"", StringComparison.Ordinal),
                "o religamento passa pelo ProvedorPGWebLib e fecha as órfãs 'pgweb-%'");
            checar(s.Contains("IniciarIdle(", StringComparison.Ordinal), "PW_iIdleProc é agendado (PWINFO_IDLEPROCTIME)");
            var recarregar = s[Math.Max(0, s.IndexOf("public static void RecarregarTef()", StringComparison.Ordinal))..];
            recarregar = recarregar[..Math.Max(0, recarregar.IndexOf("private static bool _tefVencido", StringComparison.Ordinal))];
            checar(recarregar.Contains("as IDisposable", StringComparison.Ordinal) && recarregar.Contains("Descartar(antiga)", StringComparison.Ordinal)
                   && recarregar.IndexOf("Descartar(antiga)", StringComparison.Ordinal) > recarregar.LastIndexOf("_tefVencido = false;", StringComparison.Ordinal),
                "RecarregarTef descarta (Dispose) a instância antiga depois de sair do lock");
            checar(s.Contains("vencida = _tef as IDisposable", StringComparison.Ordinal) && s.Contains("finally { Descartar(vencida); }", StringComparison.Ordinal),
                "a troca adiada (_tefVencido) em Tef() também descarta a instância antiga, fora do lock");
            checar(!s.Contains("('paygo','controlpay')", StringComparison.Ordinal), "nenhum filtro em Servicos ficou só com os dois provedores antigos");

            foreach (var arq in new[] { new[] { "Telas", "Venda.xaml.cs" }, new[] { "Telas", "Pagamento.xaml.cs" } })
            {
                var src = Fonte(arq);
                checar(src is not null, "achei " + arq[^1]);
                checar(src?.Contains("('paygo','controlpay')", StringComparison.Ordinal) == false && src?.Contains("'pgweblib'", StringComparison.Ordinal) == true,
                    arq[^1] + ": os filtros por provedor incluem 'pgweblib' (estorno, reimpressão, guarda da situação)");
                checar(src?.Contains("PGWebLibNativa", StringComparison.Ordinal) == false, arq[^1] + " não toca na DLL nativa");
            }

            var xaml = Fonte("Telas", "Configuracao.xaml") ?? "";
            checar(xaml.Contains("x:Name=\"OpTefPGWebLib\"", StringComparison.Ordinal) && xaml.Contains("PayGo (biblioteca)", StringComparison.Ordinal),
                "a Configuração tem o cartão 'PayGo (biblioteca)'");
            checar(xaml.Contains("BlocoPGWebLib", StringComparison.Ordinal) && xaml.Contains("TxtPgwebDir", StringComparison.Ordinal)
                   && xaml.Contains("TxtPgwebPorta", StringComparison.Ordinal) && xaml.Contains("TxtPgwebCapacidades", StringComparison.Ordinal),
                "com os campos novos: diretório, porta do pinpad, capacidades");
            checar(xaml.Contains("Instalar ponto de captura", StringComparison.Ordinal) && xaml.Contains("Click=\"InstalarPGWebLib\"", StringComparison.Ordinal)
                   && xaml.Contains("Click=\"AdmPGWebLib\"", StringComparison.Ordinal),
                "e os botões 'Instalar ponto de captura' e 'ADM'");
            // Só o trecho novo: os comentários antigos do XAML usam travessão, e comentário não é tela.
            var semComentario = System.Text.RegularExpressions.Regex.Replace(xaml, "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            var iNovo = semComentario.IndexOf("x:Name=\"OpTefPGWebLib\"", StringComparison.Ordinal);
            var fNovo = semComentario.IndexOf("x:Name=\"BlocoTefNuvem\"", StringComparison.Ordinal);
            var blocoNovo = iNovo >= 0 && fNovo > iNovo ? semComentario[iNovo..fNovo] : "";
            checar(blocoNovo.Length > 0 && !blocoNovo.Contains("—", StringComparison.Ordinal) && !blocoNovo.Contains("–", StringComparison.Ordinal),
                "nenhum travessão nem meia-risca nos textos novos da Configuração");

            var cfg = Fonte("Telas", "Configuracao.xaml.cs") ?? "";
            checar(cfg.Contains("\"tef_pgweb_dir\"", StringComparison.Ordinal) && cfg.Contains("\"tef_pgweb_porta_pinpad\"", StringComparison.Ordinal) && cfg.Contains("\"tef_pgweb_capacidades\"", StringComparison.Ordinal),
                "a Configuração grava/restaura as chaves novas");
            checar(xaml.Contains("x:Name=\"TxtPgwebDll\"", StringComparison.Ordinal) && xaml.Contains("Pasta da PGWebLib.dll", StringComparison.Ordinal)
                   && cfg.Contains("TxtPgwebDll.Text = Vendas.Config(cx, ConfigPGWebLib.ChaveDll", StringComparison.Ordinal)
                   && cfg.Contains("Chave(ConfigPGWebLib.ChaveDll, TxtPgwebDll.Text)", StringComparison.Ordinal)
                   && cfg.Contains("\"tef_pgweb_capacidades\", ConfigPGWebLib.ChaveDll,", StringComparison.Ordinal)
                   && cfg.Contains("PgwebDll = TxtPgwebDll.Text", StringComparison.Ordinal),
                "campo 'Pasta da PGWebLib.dll' (tef_pgweb_dll): lido, gravado, restaurado no Sair sem salvar e levado ao resumo");
            checar(cfg.Contains("SelecaoTef.Codigo(", StringComparison.Ordinal) && cfg.Contains("SelecaoTef.Modo(", StringComparison.Ordinal),
                "TefModo e tef_provedor passam pela SelecaoTef (a mesma regra do Servicos.Tef())");
            checar(cfg.Contains("InstalarAsync(", StringComparison.Ordinal) && cfg.Contains("AdministrativaAsync(", StringComparison.Ordinal),
                "Instalar e ADM chamam o provedor pelo mesmo laço (PWOPER_INSTALL / PWOPER_ADMIN)");
            checar(!cfg.Contains("PGWebLibNativa", StringComparison.Ordinal), "a Configuração não constrói a DLL nativa: pede a instância ao Servicos");

            var testes = new[] { "FakePGWebLib.cs", "TestesPGWebLib.cs", "TestesCasaPGWebLib.cs", "Program.cs" }
                .Select(n => Fonte("Pdv.Testes", n) ?? "").ToList();
            checar(testes.All(t => !t.Contains(agulha, StringComparison.Ordinal)), "a bateria nunca instancia a DLL nativa");
            var cfgNovo = cfg[Math.Max(0, cfg.IndexOf("OperarPGWebLibAsync(string oQue)", StringComparison.Ordinal))..];
            cfgNovo = cfgNovo[..Math.Max(0, cfgNovo.IndexOf("private void StatusTef(", StringComparison.Ordinal))];
            var servNovo = s[Math.Max(0, s.IndexOf("PerguntarNaTelaAsync(PwGetData", StringComparison.Ordinal))..];
            servNovo = servNovo[..Math.Max(0, servNovo.IndexOf("JanelaAtiva()", StringComparison.Ordinal))];
            checar(cfgNovo.Length > 0 && servNovo.Length > 0 && !cfgNovo.Contains("—", StringComparison.Ordinal) && !servNovo.Contains("—", StringComparison.Ordinal),
                "nenhum travessão nas mensagens novas de Servicos e Configuração");
        }
    }
}
