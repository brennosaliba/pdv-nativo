using System.Text.RegularExpressions;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O MENU DO TEF do caixa de homologação (07/09/2026, pedido do dono: "menu tef igual tinhamos
/// no outro antigo de homologacao pra seguirmos todos passos e ter configuracao").
///
/// O que estava errado antes: as operações da maquininha viviam espalhadas. Testar, Instalar e
/// ADM dentro do assistente de Configuração, cancelamento e reimpressão no menu Cancelar /
/// Imprimir, e o dono precisou perguntar onde ficava cada uma. Percorrer 58 passos assim é
/// caçar botão pela tela.
///
/// As cinco coisas que esta suíte segura, e por que cada uma:
///
///   1. o menu SÓ existe com `homologacao` = 1. Numa loja, um botão que roda operação de
///      terminal ao lado do botão de vender é estrago esperando a hora. E ele NÃO depende de
///      mais nada (nem do TEF ligado, nem do terminal instalado): o passo 01 do roteiro é
///      justamente instalar o ponto de captura, e menu que só aparece com terminal pronto é
///      menu que nunca aparece quando é preciso;
///   2. a lista de operações vem da BIBLIOTECA (PW_iGetOperations), não de uma lista escrita à
///      mão. Terminal diferente oferece coisa diferente, e código que a gente não conhece sai
///      com o nome que ela deu, nunca sem nome;
///   3. venda, cancelamento e recarga NÃO saem daqui. Elas têm valor e dono no caixa: a venda
///      sai da comanda, o cancelamento sai do estorno, e são esses dois caminhos que gravam a
///      linha em `tef_transacao`. O menu mostra o item e diz onde ele mora;
///   4. o que o menu roda fecha como 'adm', nunca como 'pago'. A classificação sai do PREFIXO
///      do chargeId ("-adm-", "-rep-", "-inst-"): prefixo novo faria uma pendência de relatório
///      voltar do religamento como venda paga, dinheiro que ninguém pagou;
///   5. o quadro de estado e a última frase da biblioteca (PWINFO_RESULTMSG) ficam à vista.
///      Quase todo passo do roteiro cobra a frase exata que o operador viu, e ela vive um
///      instante numa tela que já fechou.
/// </summary>
public static class TestesMenuTef
{
    private static PwOperacao Op(byte codigo, string texto) => new(codigo, texto, codigo.ToString());

    private static string Rotulos(IReadOnlyList<MenuTef.Item> itens) => string.Join("|", itens.Select(i => i.Rotulo));

    private static ProvedorPGWebLib Provedor(FakePGWebLib f, OpcoesPGWebLib? opcoes = null,
        Func<PwGetData, CancellationToken, Task<string?>>? perguntar = null, List<TransacaoPayGo>? guardadas = null)
        => new(f, TestesPGWebLib.PastaTeste, opcoes ?? new OpcoesPGWebLib("Pdv.AmericanDay", "0.5.9", "American Day"))
        {
            IntervaloPollMs = 5,
            TempoMaxExecMs = 2000,
            TempoMaxCapturaMs = 2000,
            TempoPerguntaMs = 500,
            Perguntar = perguntar,
            Guardar = t => { guardadas?.Add(t); return true; },
        };

    public static void Rodar(Action<bool, string> checar)
    {
        Directory.CreateDirectory(TestesPGWebLib.PastaTeste);

        // ── 1. quando o menu existe ──────────────────────────────────────────
        {
            checar(!MenuTef.Aparece(homologacao: false), "com `homologacao` desligada o menu do TEF não existe (é a loja)");
            checar(MenuTef.Aparece(homologacao: true), "com `homologacao` = 1 o menu aparece");
        }

        // ── 2. a lista sai da BIBLIOTECA, na ordem dela ──────────────────────
        {
            var daLib = new[]
            {
                Op(PW.PWOPER_INSTALL, "INSTALACAO"),
                Op(PW.PWOPER_REPRINT, "REIMPRESSAO"),
                Op(PW.PWOPER_RPTTRUNC, "RELATORIO RESUMIDO"),
                Op(PW.PWOPER_RPTDETAIL, "RELATORIO DETALHADO"),
                Op(PW.PWOPER_ADMIN, "ADMINISTRATIVA"),
                Op(PW.PWOPER_VERSION, "VERSAO"),
                Op(PW.PWOPER_CONFIG, "CONFIGURACAO"),
                Op(PW.PWOPER_MAINTENANCE, "MANUTENCAO"),
                Op(PW.PWOPER_SALE, "VENDA"),
            };
            var itens = MenuTef.Itens(daLib);
            checar(itens.Count == daLib.Length + 1, $"um item por operação da biblioteca, mais a Configuração: {itens.Count}");
            checar(Rotulos(itens) == "Instalar o ponto de captura|Reimprimir comprovante|Relatório resumido|"
                 + "Relatório detalhado|Menu administrativo|Versão da biblioteca|Configuração do PayGo|Manutenção|"
                 + "Venda|Configuração do caixa",
                "os nomes são curtos e dizem o que fazem, na ORDEM da biblioteca: " + Rotulos(itens));
            checar(itens[^1].Acao == MenuTef.AcaoConfiguracao,
                "o atalho da Configuração fecha a lista (\"e ter configuracao\", nas palavras do dono)");
            checar(itens.Take(daLib.Length).Select(i => MenuTef.OperacaoDaAcao(i.Acao))
                       .SequenceEqual(daLib.Select(o => (byte?)o.Codigo)),
                "cada item leva o PWOPER_* da biblioteca, e é ele que vai em PW_iNewTransac");
            checar(MenuTef.OperacaoDaAcao(MenuTef.AcaoConfiguracao) is null && MenuTef.OperacaoDaAcao(null) is null
                   && MenuTef.OperacaoDaAcao("qualquer") is null,
                "ação que não é operação não vira número de operação nenhum");

            checar(itens[0].Detalhe == "INSTALACAO" && itens[4].Detalhe == "ADMINISTRATIVA",
                "cada item mostra também como a BIBLIOTECA chama a operação (o roteiro fala com as palavras dela)");
            checar(itens[^1].Detalhe is null, "o atalho da casa não inventa nome de biblioteca nenhum");

            // Terminal com operação que este arquivo não conhece: sai com o nome DELA.
            var desconhecida = MenuTef.Itens(new[] { Op(200, "TESTE DE COMUNICACAO") });
            checar(desconhecida[0].Rotulo == "TESTE DE COMUNICACAO" && desconhecida[0].Roda
                   && MenuTef.OperacaoDaAcao(desconhecida[0].Acao) == 200,
                "operação que o caixa não conhece entra com o nome da biblioteca, e roda: " + desconhecida[0].Rotulo);
            var semNome = MenuTef.Itens(new[] { Op(201, "  ") });
            checar(semNome[0].Rotulo == "Operação 201" && semNome[0].Detalhe is null,
                "e biblioteca calada não deixa item sem nome na tela: " + semNome[0].Rotulo);

            // PW_iGetOperations(3) traz as duas listas juntas: repetido entra uma vez.
            var repetida = MenuTef.Itens(new[] { Op(PW.PWOPER_ADMIN, "ADMINISTRATIVA"), Op(PW.PWOPER_ADMIN, "ADMINISTRATIVA") });
            checar(repetida.Count == 2, "operação repetida nas duas listas entra uma vez só: " + Rotulos(repetida));
        }

        // ── 3. biblioteca que não lista nada: a reserva ──────────────────────
        {
            foreach (var (lista, nome) in new (IReadOnlyList<PwOperacao>?, string)[]
                     { (null, "null"), (Array.Empty<PwOperacao>(), "vazia") })
            {
                var itens = MenuTef.Itens(lista);
                checar(Rotulos(itens) == "Instalar o ponto de captura|Menu administrativo|Configuração do caixa",
                    $"lista {nome}: ficam a instalação e o menu administrativo, que são as duas saídas de um "
                    + "terminal que ainda não é terminal: " + Rotulos(itens));
            }
            checar(!MenuTef.ListouOperacoes(null) && !MenuTef.ListouOperacoes(Array.Empty<PwOperacao>())
                   && MenuTef.ListouOperacoes(new[] { Op(PW.PWOPER_ADMIN, "ADMINISTRATIVA") }),
                "e a tela sabe dizer se a lista veio da biblioteca ou é a reserva");
        }

        // ── 4. operação de VALOR não sai deste menu ──────────────────────────
        {
            checar(PW.EhOperacaoDeValor(PW.PWOPER_SALE) && PW.EhOperacaoDeValor(PW.PWOPER_SALEVOID)
                   && PW.EhOperacaoDeValor(PW.PWOPER_PREPAID) && PW.EhOperacaoDeValor(PW.PWOPER_VOID),
                "venda, cancelamento de venda, recarga e cancelamento são operações de valor");
            checar(!PW.EhOperacaoDeValor(PW.PWOPER_ADMIN) && !PW.EhOperacaoDeValor(PW.PWOPER_INSTALL)
                   && !PW.EhOperacaoDeValor(PW.PWOPER_REPRINT) && !PW.EhOperacaoDeValor(PW.PWOPER_RPTDETAIL),
                "administrativa, instalação, reimpressão e relatório não movem dinheiro");

            var itens = MenuTef.Itens(new[]
            {
                Op(PW.PWOPER_SALE, "VENDA"), Op(PW.PWOPER_SALEVOID, "CANCELAMENTO"),
                Op(PW.PWOPER_PREPAID, "RECARGA"), Op(PW.PWOPER_VOID, "CANCELAMENTO"),
                Op(PW.PWOPER_ADMIN, "ADMINISTRATIVA"),
            });
            checar(itens.Take(4).All(i => !i.Roda && !string.IsNullOrWhiteSpace(i.Onde)),
                "as quatro aparecem no menu (o roteiro cita o nome delas) mas NÃO rodam por aqui");
            checar(itens[0].Onde == MenuTef.OndeVenda && itens[1].Onde == MenuTef.OndeCancelamento
                   && itens[2].Onde == MenuTef.OndeRecarga && itens[3].Onde == MenuTef.OndeCancelamento,
                "e cada uma diz ONDE mora, em uma frase: " + itens[0].Onde);
            checar(itens[4].Roda && itens[^1].Roda, "a administrativa e o atalho da Configuração continuam rodando");

            // O cinto: mesmo chamado direto, o provedor recusa. Defesa que mora só na tela é decoração.
            foreach (var oper in new[] { PW.PWOPER_SALE, PW.PWOPER_SALEVOID, PW.PWOPER_PREPAID, PW.PWOPER_VOID })
            {
                var f = new FakePGWebLib();
                var p = Provedor(f);
                var d = p.OperacaoDoMenuAsync(oper, "venda", CancellationToken.None).GetAwaiter().GetResult();
                checar(!d.Pago && f.Transacoes.Count == 0,
                    $"o provedor recusa a operação de valor {oper} vinda do menu, sem abrir transação na biblioteca");
            }
        }

        // ── 5. o que o menu roda fecha como 'adm', nunca como 'pago' ─────────
        // Quem classifica é o PREFIXO do chargeId, e quem lê o prefixo é o religamento do boot.
        // Prefixo novo faria um relatório pendente voltar como venda paga.
        {
            var operacoes = new (byte Oper, string Rotulo)[]
            {
                (PW.PWOPER_INSTALL, "instalação"), (PW.PWOPER_REPRINT, "reimpressão"),
                (PW.PWOPER_ADMIN, "menu administrativo"), (PW.PWOPER_RPTTRUNC, "relatório resumido"),
                (PW.PWOPER_RPTDETAIL, "relatório detalhado"), (PW.PWOPER_PARAMUPD, "atualizar parâmetros"),
                (PW.PWOPER_VERSION, "versão da biblioteca"), (PW.PWOPER_CONFIG, "configuração do PayGo"),
                (PW.PWOPER_MAINTENANCE, "manutenção"),
            };
            foreach (var (oper, rotulo) in operacoes)
            {
                var f = new FakePGWebLib();
                var guardadas = new List<TransacaoPayGo>();
                var p = Provedor(f, perguntar: (d, _) => Task.FromResult(RespostaDaTela.Menu(d, 0)), guardadas: guardadas);
                var d2 = p.OperacaoDoMenuAsync(oper, rotulo, CancellationToken.None).GetAwaiter().GetResult();
                checar(d2.Pago, $"{rotulo} conclui pelo menu do TEF: {d2.Motivo}");
                checar(d2.PaymentStatus == "adm", $"{rotulo} fecha como 'adm', nunca como venda: {d2.PaymentStatus}");
                var id = d2.ChargeId ?? "";
                checar(id.Contains("-adm-", StringComparison.Ordinal) || id.Contains("-rep-", StringComparison.Ordinal)
                       || id.Contains("-inst-", StringComparison.Ordinal),
                    $"{rotulo} usa um prefixo que o religamento reconhece como administrativa: {id}");
                checar(guardadas.All(t => t.Situacao != "pago"), $"{rotulo} não grava linha 'pago' em tef_transacao");
                checar(f.Transacoes.Count > 0 && f.Transacoes[0].Oper == oper,
                    $"{rotulo} abre na biblioteca a operação {oper} que a lista dela mandou");
            }
        }

        // ── 6. o quadro de estado ────────────────────────────────────────────
        {
            checar(MenuTef.Terminal(PW.PWRET_OK, 1) == "Instalado", "PW_iGetOperations com operação de venda: instalado");
            checar(MenuTef.Terminal(PW.PWRET_NOTINST, 0) == "Não instalado",
                "PWRET_NOTINST é o terminal sem ponto de captura, e o quadro diz isso com todas as letras");
            checar(MenuTef.Terminal(PW.PWRET_OK, 0) == "Sem operação de venda no terminal",
                "lista vazia com PWRET_OK não é instalação: " + MenuTef.Terminal(PW.PWRET_OK, 0));
            checar(MenuTef.Terminal(PW.PWRET_DLLNOTINIT, 0).Contains("PWRET_DLLNOTINIT", StringComparison.Ordinal),
                "biblioteca que não respondeu deixa o retorno à vista (é o que o roteiro manda anotar): "
                + MenuTef.Terminal(PW.PWRET_DLLNOTINIT, 0));

            // A MESMA regra que decide se o caixa oferece cartão. Quadro dizendo "instalado" para
            // um terminal que a venda vai recusar seria pior do que quadro nenhum.
            var semInstalar = new FakePGWebLib { Instalado = false };
            var pSem = Provedor(semInstalar);
            var ativo = pSem.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult();
            var (ret, ops) = pSem.OperacoesAsync(PW.OPERACOES_DE_VENDA, CancellationToken.None).GetAwaiter().GetResult();
            checar(!ativo && MenuTef.Terminal(ret, ops.Count) == "Não instalado",
                "terminal sem instalação: o caixa não vende E o quadro diz Não instalado (uma regra só)");
            var comInstalar = new FakePGWebLib();
            var pCom = Provedor(comInstalar);
            var ativo2 = pCom.AtivoAsync(CancellationToken.None).GetAwaiter().GetResult();
            var (ret2, ops2) = pCom.OperacoesAsync(PW.OPERACOES_DE_VENDA, CancellationToken.None).GetAwaiter().GetResult();
            checar(ativo2 && MenuTef.Terminal(ret2, ops2.Count) == "Instalado",
                "terminal instalado: vende E o quadro diz Instalado");
            checar(ProvedorPGWebLib.InstaladoPelaLista(PW.PWRET_OK, 1)
                   && !ProvedorPGWebLib.InstaladoPelaLista(PW.PWRET_OK, 0)
                   && !ProvedorPGWebLib.InstaladoPelaLista(PW.PWRET_NOTINST, 3),
                "a regra do instalado é uma função só, lida pelos dois lados");

            // O menu não lista nada por conta própria: pergunta as DUAS listas à biblioteca.
            var (_, todas) = pCom.OperacoesAsync(PW.OPERACOES_TODAS, CancellationToken.None).GetAwaiter().GetResult();
            var itensDaLib = MenuTef.Itens(todas);
            checar(todas.Any(o => o.Codigo == PW.PWOPER_INSTALL) && todas.Any(o => o.Codigo == PW.PWOPER_SALE),
                "PW_iGetOperations(3) traz o menu administrativo e a venda juntos: " + todas.Count + " operações");
            checar(itensDaLib.Any(i => i.Rotulo == "Menu administrativo" && i.Roda)
                   && itensDaLib.Any(i => i.Rotulo == "Venda" && !i.Roda),
                "e o menu monta em cima do que ELA listou: a administrativa roda, a venda só mostra onde mora");
        }

        // ── 7. o quadro poupa o dono de abrir o assistente ───────────────────
        {
            var op = new OpcoesPGWebLib("Pdv.AmericanDay", "0.5.9", "American Day",
                RedeCartao: null, RedePix: null, PortaPinpad: "5", Ambiente: PW.ENVRMNT_TEST,
                RedesPermitidas: new[] { "C6PAY", "REDE", "PIX C6 BANK" });
            var linhas = MenuTef.Estado(op, @"C:\ProgramData\PdvNativo\pgweb64", @"C:\PGWebLib\x64", "Instalado");
            string V(string rotulo) => linhas.First(l => l.Rotulo == rotulo).Valor;
            checar(linhas.Select(l => l.Rotulo).SequenceEqual(new[]
                {
                    "Terminal", "Ambiente", "Rede do cartão", "Rede do Pix", "Redes no menu",
                    "Porta do pinpad", "Pasta da biblioteca", "Pasta de trabalho",
                }),
                "o quadro mostra o que o dono ia abrir o assistente para conferir, nesta ordem");
            checar(V("Terminal") == "Instalado" && V("Ambiente") == "Homologação",
                "terminal e ambiente à vista (o roteiro inteiro depende de estar em homologação): " + V("Ambiente"));
            checar(V("Rede do cartão") == "o caixa escolhe no menu" && V("Rede do Pix") == "o caixa escolhe no menu",
                "rede em branco não é falta de dado: é o menu aparecendo, que é o que o passo 05 precisa");
            checar(V("Redes no menu") == "C6PAY, REDE, PIX C6 BANK", "as três redes do roteiro: " + V("Redes no menu"));
            checar(V("Porta do pinpad") == "5" && V("Pasta da biblioteca") == @"C:\PGWebLib\x64"
                   && V("Pasta de trabalho") == @"C:\ProgramData\PdvNativo\pgweb64",
                "porta do pinpad e as duas pastas, do jeito que estão configuradas");

            var padrao = MenuTef.Estado(new OpcoesPGWebLib("Pdv.AmericanDay", "0.5.9", "American Day",
                    RedeCartao: "C6PAY", RedePix: "PIX C6 BANK"),
                ConfigPGWebLib.DirPadrao, null, "Não instalado");
            string P(string rotulo) => padrao.First(l => l.Rotulo == rotulo).Valor;
            checar(P("Ambiente") == "Produção" && P("Rede do cartão") == "C6PAY" && P("Rede do Pix") == "PIX C6 BANK",
                "rede fixa aparece pelo nome, e sem a chave de ambiente o quadro diz Produção (que é o padrão)");
            checar(P("Redes no menu") == "todas as do terminal" && P("Porta do pinpad") == "automática"
                   && P("Pasta da biblioteca") == "o Windows procura sozinho",
                "e o que está em branco é explicado, não mostrado vazio: " + P("Pasta da biblioteca"));

            // Sai das opções COM QUE O PROVEDOR ESTÁ RODANDO, não de uma releitura da config.
            var p = Provedor(new FakePGWebLib(), opcoes: op);
            checar(ReferenceEquals(p.Opcoes, op) && p.PastaTrabalho == TestesPGWebLib.PastaTeste,
                "o quadro lê o que está NO AR, não o que está gravado esperando um Salvar");
        }

        // ── 8. a última frase da biblioteca (PWINFO_RESULTMSG) ───────────────
        {
            checar(MenuTef.UltimaResposta(null, null) == MenuTef.SemResposta
                   && MenuTef.UltimaResposta("   ", DateTime.Now) == MenuTef.SemResposta,
                "sem frase nenhuma, o quadro diz isso em vez de mostrar espaço em branco");
            var quando = new DateTime(2026, 9, 7, 14, 32, 0);
            checar(MenuTef.UltimaResposta("TRANSACAO APROVADA", quando) == "TRANSACAO APROVADA (às 14:32)",
                "a frase sai inteira, com a hora em 24 horas: " + MenuTef.UltimaResposta("TRANSACAO APROVADA", quando));
            checar(MenuTef.UltimaResposta("OPERACAO\rCANCELADA", null) == "OPERACAO CANCELADA",
                "o 0Dh da biblioteca vira espaço, e nada mais é reescrito");

            // Da biblioteca até o quadro, sem passar por tradução da casa.
            var f = new FakePGWebLib();
            var p = Provedor(f);
            checar(p.UltimaMensagem is null && MenuTef.UltimaResposta(p.UltimaMensagem, p.UltimaMensagemEm) == MenuTef.SemResposta,
                "provedor recém-criado ainda não tem frase nenhuma");
            var d = p.OperacaoDoMenuAsync(PW.PWOPER_INSTALL, "instalação", CancellationToken.None).GetAwaiter().GetResult();
            checar(d.Pago && p.UltimaMensagem == "INSTALACAO CONCLUIDA",
                "depois da instalação, a frase que ficou é a DA BIBLIOTECA: " + (p.UltimaMensagem ?? "nenhuma"));
            checar(p.UltimaMensagemEm is not null
                   && MenuTef.UltimaResposta(p.UltimaMensagem, p.UltimaMensagemEm).StartsWith("INSTALACAO CONCLUIDA (às ", StringComparison.Ordinal),
                "com a hora em que ela chegou");

            // E também quando NÃO aprova: o passo 55 cobra "OPERACAO CANCELADA" na tela.
            var f2 = new FakePGWebLib();
            f2.Roteiro.Enqueue(FakePGWebLib.Desfecho.Recusar);
            var p2 = Provedor(f2, perguntar: (g, _) => Task.FromResult(RespostaDaTela.Menu(g, 0)));
            var d2 = p2.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(10m), null, 1, null, CancellationToken.None)
                       .GetAwaiter().GetResult();
            checar(!d2.Pago && p2.UltimaMensagem is { Length: > 0 },
                "recusa também deixa a frase da rede guardada: " + (p2.UltimaMensagem ?? "nenhuma"));
        }

        // ── 9. texto de tela ─────────────────────────────────────────────────
        {
            var itens = MenuTef.Itens(new[]
            {
                Op(PW.PWOPER_INSTALL, "INSTALACAO"), Op(PW.PWOPER_REPRINT, "REIMPRESSAO"),
                Op(PW.PWOPER_RPTTRUNC, "RELATORIO RESUMIDO"), Op(PW.PWOPER_RPTDETAIL, "RELATORIO DETALHADO"),
                Op(PW.PWOPER_ADMIN, "ADMINISTRATIVA"), Op(PW.PWOPER_PARAMUPD, "ATUALIZACAO DE PARAMETROS"),
                Op(PW.PWOPER_VERSION, "VERSAO"), Op(PW.PWOPER_CONFIG, "CONFIGURACAO"),
                Op(PW.PWOPER_MAINTENANCE, "MANUTENCAO"), Op(PW.PWOPER_SALE, "VENDA"),
                Op(PW.PWOPER_SALEVOID, "CANCELAMENTO"), Op(PW.PWOPER_PREPAID, "RECARGA"),
            });
            var textos = itens.Select(i => i.Rotulo)
                .Concat(itens.Where(i => i.Onde is not null).Select(i => i.Onde!))
                .Append(MenuTef.Rotulo).Append(MenuTef.Titulo).Append(MenuTef.SemResposta)
                .Concat(MenuTef.Estado(new OpcoesPGWebLib("a", "b", "c"), "d", null, "Instalado")
                    .SelectMany(l => new[] { l.Rotulo, l.Valor }))
                .ToList();
            checar(textos.All(t => !t.Contains('—') && !t.Contains('–')),
                "sem travessão nem meia-risca em texto de tela (o dono lê como texto de IA)");
            checar(itens.All(i => i.Rotulo.Length is > 0 and <= 28),
                "rótulo curto (até 28 caracteres): cabe numa linha do menu");
            checar(itens.Where(i => i.Onde is not null).All(i => i.Onde!.Length <= 80 && i.Onde.EndsWith('.')),
                "a frase de onde a operação mora é uma linha, não um manual");
            checar(itens.All(i => i.Icone.Length > 0), "todo item tem ícone, como nos outros menus da barra");
            checar(itens.Select(i => i.Acao).Distinct().Count() == itens.Count, "nenhuma ação se repete");
            checar(textos.All(t => !Regex.IsMatch(t, @"PWRET_|PWOPER_|PWINFO_")),
                "nenhum nome de constante da biblioteca vira nome de botão");
            checar(MenuTef.Rotulo == "Menu do TEF", "o botão da barra diz o que faz: " + MenuTef.Rotulo);
            checar(MenuTef.AlturaItem >= 44, $"item do menu tem no mínimo 44 px de altura (tem {MenuTef.AlturaItem})");
        }

        // ── 10. o FONTE das telas: o que só existe no WPF ────────────────────
        var xaml = Fonte("Telas", "Venda.xaml");
        var venda = Fonte("Telas", "Venda.xaml.cs");
        var tela = Fonte("Telas", "TelaMenuTef.cs");
        checar(xaml is not null && venda is not null && tela is not null,
            "achei Venda.xaml, Venda.xaml.cs e a tela do menu do TEF");
        if (xaml is null || venda is null || tela is null) return;

        // (a) o botão da barra nasce ESCONDIDO e só a homologação o acende
        {
            var botao = Regex.Match(xaml, @"<Button x:Name=""BtnMenuTef""[\s\S]*?</Button>").Value;
            checar(botao.Length > 0, "o botão do menu do TEF está na barra da tela de venda");
            checar(botao.Contains("Visibility=\"Collapsed\"", StringComparison.Ordinal),
                "e nasce ESCONDIDO no XAML: caixa que nunca rodar o código de homologação não mostra o botão");
            checar(botao.Contains("Click=\"AbrirMenuTef\"", StringComparison.Ordinal), "o clique vai para AbrirMenuTef");
            checar(botao.Contains($"Text=\"{MenuTef.Rotulo}\"", StringComparison.Ordinal),
                "o rótulo do botão é o de MenuTef, não um texto solto no XAML");
            checar(venda.Contains("BtnMenuTef.Visibility = MenuTef.Aparece(_homologacao) ? Visibility.Visible : Visibility.Collapsed;",
                    StringComparison.Ordinal),
                "quem acende o botão é MenuTef.Aparece(_homologacao), e nada mais");
            checar(Regex.Matches(venda, @"BtnMenuTef\.Visibility").Count == 1,
                "um lugar só decide a visibilidade: duas cópias divergem no dia 1");

            var handler = Trecho(venda, "private void AbrirMenuTef(", "private void LancarValorDeTeste(");
            checar(handler.Contains("if (!_homologacao) return;", StringComparison.Ordinal),
                "o handler tem o cinto: sem homologação ele não faz nada, mesmo se alguém acender o botão");
            checar(handler.Contains("TefEmAndamento(dono)", StringComparison.Ordinal),
                "e não abre com cobrança em voo (a mesma trava do fechamento e do sair)");
            checar(handler.Contains("TelaMenuTef.Mostrar(dono)", StringComparison.Ordinal)
                   && handler.Contains("AbrirConfiguracao(sender, e)", StringComparison.Ordinal),
                "o item Configuração volta para o AbrirConfiguracao que JÁ existia, com as travas dele");
        }

        // (b) a tela não duplica regra: chama o MESMO provedor da Configuração
        {
            checar(tela.Contains("Servicos.PGWebLib()", StringComparison.Ordinal),
                "a tela usa o provedor da casa (Servicos.PGWebLib()), o mesmo que a Configuração usa");
            checar(!tela.Contains("new ProvedorPGWebLib", StringComparison.Ordinal)
                   && !tela.Contains("new PGWebLibNativa", StringComparison.Ordinal),
                "e não constrói provedor nenhum: uma cópia da regra de instalação seria a segunda verdade sobre o terminal");
            checar(tela.Contains("OperacaoDoMenuAsync", StringComparison.Ordinal)
                   && !tela.Contains("NewTransac", StringComparison.Ordinal)
                   && !tela.Contains("ExecTransac", StringComparison.Ordinal),
                "quem fala com a biblioteca é o provedor: a tela não chama a DLL por fora");
            checar(tela.Contains("MenuTef.Itens(", StringComparison.Ordinal)
                   && tela.Contains("MenuTef.Estado(", StringComparison.Ordinal)
                   && tela.Contains("MenuTef.Terminal(", StringComparison.Ordinal)
                   && tela.Contains("MenuTef.UltimaResposta(", StringComparison.Ordinal),
                "lista, quadro, estado do terminal e frase da biblioteca vêm de MenuTef (que esta suíte prova)");
            checar(tela.Contains("PW.OPERACOES_TODAS", StringComparison.Ordinal)
                   && tela.Contains("PW.OPERACOES_DE_VENDA", StringComparison.Ordinal),
                "a lista sai de PW_iGetOperations, não de uma lista escrita na tela");

            // A senha da instalação é digitada pelo dono no diálogo da biblioteca. A tela do menu
            // não guarda senha, não preenche campo de senha e não abre diálogo de senha por conta.
            checar(!tela.Contains("PedirSenha", StringComparison.Ordinal)
                   && !Regex.IsMatch(tela, @"(?i)senha\s*=")
                   && !tela.Contains("PasswordBox", StringComparison.Ordinal),
                "a tela não toca em senha: quem pede é a biblioteca, pelo diálogo de sempre");

            var atributos = Regex.Matches(tela, @"Text = ""([^""]*)""").Select(m => m.Groups[1].Value).ToList();
            checar(atributos.All(t => !t.Contains('—') && !t.Contains('–')),
                "nenhum texto da tela do menu tem travessão ou meia-risca");
        }
    }

    private static string Trecho(string todo, string de, string ate)
    {
        var i = todo.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var f = todo.IndexOf(ate, i + de.Length, StringComparison.Ordinal);
        return f < 0 ? "" : todo[i..f];
    }

    /// <summary>Sobe do binário do teste até achar o arquivo pedido no repositório.</summary>
    private static string? Fonte(params string[] caminho)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var alvo = Path.Combine(new[] { d.FullName }.Concat(caminho).ToArray());
            if (File.Exists(alvo)) return File.ReadAllText(alvo);
        }
        return null;
    }
}
