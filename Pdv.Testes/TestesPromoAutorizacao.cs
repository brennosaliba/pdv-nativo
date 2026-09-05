using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// PROMOÇÃO COM 2FA DO GERENTE OU DO DONO (05/09/2026).
///
/// Pedido do dono: "na aba promoções do PDV a opção de exigir senha do gerente para
/// liberação; ex.: crio desconto funcionário para não ter abuso e quero que essa
/// promoção tenha 2FA do gerente."
///
/// O que esta suíte protege, em ordem de estrago:
///  · o MOTOR nunca aplica promoção com config.autorizacao sem ela estar em
///    Autorizadas; excluída nunca aplica; pendente volta em Pendentes (regra pura);
///  · a MÁQUINA DE ESTADOS (PortaoPromocao + Autorizacao.ResolverAsync) contra o
///    FakeTotp com nível: 'gerente' aceita o código do manager e do owner; 'dono'
///    recusa o do manager; cancelou/3 erros/sem rede excluem, e nunca se pergunta
///    duas vezes na mesma venda; nova venda (Zerar) pergunta de novo;
///  · o PAYLOAD da venda leva autorizacao {log_id, autorizador} no item com promoção
///    liberada e nada nos outros;
///  · por FONTE: a tela não chama o motor sem o contexto, só esvazia a comanda pelo
///    EsvaziarComanda (que zera o contexto), nunca fabrica Autorizar() por conta
///    própria, e estorno/cancelamento continuam no nível dono.
/// </summary>
public static class TestesPromoAutorizacao
{
    private sealed class TelaFalsa : ITelaAutorizacao
    {
        public Func<string?, string?>? AoPedirCodigo;
        public int VezesPediuCodigo;
        public readonly List<string?> Avisos = new();
        public readonly List<string> Niveis = new();
        private sealed class Nada : IDisposable { public void Dispose() { } }
        public IDisposable Aguardando(string mensagem) => new Nada();
        public Task<string?> PedirCodigoAsync(string? aviso, string nivel)
        {
            VezesPediuCodigo++; Avisos.Add(aviso); Niveis.Add(nivel);
            return Task.FromResult(AoPedirCodigo?.Invoke(aviso));
        }
    }

    private const string TerminalUuid = "9a1c0c2e-0000-4000-8000-terminal0002";

    private static string? Raiz()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Pdv.csproj"))) return d.FullName;
        return null;
    }

    private static string Fonte(params string[] partes)
    {
        var raiz = Raiz();
        if (raiz is null) return "";
        var alvo = Path.Combine(new[] { raiz }.Concat(partes).ToArray());
        // fim de linha normalizado: a árvore pode estar em CRLF (autocrlf) ou LF
        return File.Exists(alvo) ? File.ReadAllText(alvo).Replace("\r\n", "\n") : "";
    }

    private static string Trecho(string todo, string de, string ate)
    {
        var i = todo.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var f = todo.IndexOf(ate, i + de.Length, StringComparison.Ordinal);
        return f < 0 ? "" : todo[i..f];
    }

    private static bool Ordem(string corpo, string primeiro, string depois)
    {
        var a = corpo.IndexOf(primeiro, StringComparison.Ordinal);
        var b = corpo.IndexOf(depois, StringComparison.Ordinal);
        return a >= 0 && b > a;
    }

    /// <summary>Corpo de um método: do cabeçalho até o próximo "\n    }" no nível da classe.</summary>
    private static string Metodo(string fonte, string cabecalho) => Trecho(fonte, cabecalho, "\n    }\n");

    /// <summary>Todas as chamadas `nome(...)` do texto, com o miolo entre os parênteses balanceados.</summary>
    private static List<string> Chamadas(string texto, string nome)
    {
        var r = new List<string>();
        var i = 0;
        while ((i = texto.IndexOf(nome + "(", i, StringComparison.Ordinal)) >= 0)
        {
            var k = i + nome.Length + 1; var prof = 1; var j = k;
            for (; j < texto.Length && prof > 0; j++)
            {
                if (texto[j] == '(') prof++;
                else if (texto[j] == ')') prof--;
            }
            r.Add(texto[k..(j - 1)]);
            i = j;
        }
        return r;
    }

    private static string Txt(JsonElement e, string nome)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static Promocoes.Promo P(string json) => Promocoes.Parsear(json)
        ?? throw new InvalidOperationException("payload de teste nao parseou: " + json);

    private static Promocoes.ItemCarrinho It(string id, long preco, int qtd = 1, string cat = "Donuts")
        => new(id, cat, preco, qtd * 1000L);

    /// <summary>Promoção percentual em A com o portão pedido (null = livre).</summary>
    private static string Pct(string id, string nome, int pct, string? autorizacao, string alvo = "\"alvo\":\"produtos\",\"produto_ids\":[\"A\"]")
        => "{\"id\":\"" + id + "\",\"nome\":\"" + nome + "\",\"tipo\":\"percentual\",\"percentual\":" + pct + ",\"ativa\":true," + alvo
           + ",\"inicio\":\"2026-01-01\",\"fim\":null,\"config\":"
           + (autorizacao is null ? "{\"janelas\":[]}" : "{\"janelas\":[],\"autorizacao\":\"" + autorizacao + "\"}") + "}";

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        var sexta = new DateTime(2026, 9, 4, 15, 0, 0);
        var ctx = new Promocoes.ContextoAutorizacao();

        // ── 1. PARSER: config.autorizacao ────────────────────────────────────
        {
            var g = P(Pct("func", "desconto funcionario", 20, "gerente"));
            var d = P(Pct("dono20", "desconto do dono", 20, "dono"));
            var livre = P(Pct("livre", "quinta 10", 10, null));
            checar(g.Autorizacao == "gerente" && g.ExigeAutorizacao, "PA-1 config.autorizacao='gerente' vira Autorizacao=gerente");
            checar(d.Autorizacao == "dono" && d.ExigeAutorizacao, "PA-2 config.autorizacao='dono' vira Autorizacao=dono");
            checar(livre.Autorizacao is null && !livre.ExigeAutorizacao, "PA-3 sem a chave: promoção livre (não pede código)");
            var semCfg = P("{\"id\":\"x\",\"nome\":\"x\",\"tipo\":\"percentual\",\"percentual\":5,\"alvo\":\"todos\",\"config\":null}");
            checar(semCfg.Autorizacao is null, "PA-4 config null: livre");
            var caixaAlta = P("{\"id\":\"x\",\"nome\":\"x\",\"tipo\":\"percentual\",\"percentual\":5,\"alvo\":\"todos\",\"config\":{\"autorizacao\":\" GERENTE \"}}");
            checar(caixaAlta.Autorizacao == "gerente", "PA-5 ' GERENTE ' (caixa e espaço) normaliza para gerente");
            var torto = P("{\"id\":\"x\",\"nome\":\"x\",\"tipo\":\"percentual\",\"percentual\":5,\"alvo\":\"todos\",\"config\":{\"autorizacao\":\"supervisor\"}}");
            checar(torto.Autorizacao == "dono", "PA-6 valor desconhecido FECHA no dono (nunca abre a promoção)");
            var vazio = P("{\"id\":\"x\",\"nome\":\"x\",\"tipo\":\"percentual\",\"percentual\":5,\"alvo\":\"todos\",\"config\":{\"autorizacao\":\"\"}}");
            checar(vazio.Autorizacao is null, "PA-7 autorizacao vazia = ausente (livre)");
            var junto = P("{\"id\":\"cg\",\"nome\":\"cg\",\"tipo\":\"compre_ganhe\",\"alvo\":\"produtos\",\"produto_ids\":[\"A\"],\"config\":{\"ganha_regra\":\"mesmo_produto\",\"limite_por_venda\":1,\"autorizacao\":\"gerente\"}}");
            checar(junto.Autorizacao == "gerente" && junto.GanhaRegra == Promocoes.GanhaRegra.MesmoProduto && junto.LimitePorVenda == 1,
                "PA-8 a chave convive com as outras de config (ganha_regra, limite_por_venda continuam lidas)");
            checar(Promocoes.NivelAutorizacao(null) is null && Promocoes.NivelAutorizacao("dono") == "dono"
                   && Promocoes.NivelAutorizacao("Gerente") == "gerente" && Promocoes.NivelAutorizacao("x") == "dono",
                "PA-9 NivelAutorizacao: null→livre, dono, Gerente→gerente, desconhecido→dono");
        }

        // ── 2. MOTOR: os dois conjuntos ──────────────────────────────────────
        {
            var func = P(Pct("func", "desconto funcionario", 20, "gerente"));
            var livre = P(Pct("livre", "quinta 10", 10, null));
            var promos = new[] { livre, func };
            var carrinho = new[] { It("A", 1000) };

            ctx.Zerar();
            var av = Promocoes.AvaliarCarrinho(promos, carrinho, sexta, ctx);
            checar(av.PromoId == "livre" && av.TotalCent == 100,
                $"MT-1 pendente NÃO aplica: vale a livre (10%) enquanto o gerente não libera (promo={av.PromoId} total={av.TotalCent})");
            checar(av.Pendentes.Count == 1 && av.Pendentes[0].PromoId == "func" && av.Pendentes[0].Nivel == "gerente"
                   && av.Pendentes[0].DescontoCent == 200 && av.Pendentes[0].Nome == "desconto funcionario",
                "MT-2 a pendente volta em Pendentes com id, nome, nível e o desconto que daria (R$ 2,00)");
            checar(!av.Perdedoras.Any(p => p.PromoId == "func"),
                "MT-3 pendente não é 'perdedora' (não vale explicar ao operador uma promoção que ainda não foi liberada)");

            ctx.Autorizar("func", "log-1", "Marcos");
            av = Promocoes.AvaliarCarrinho(promos, carrinho, sexta, ctx);
            checar(av.PromoId == "func" && av.TotalCent == 200 && av.Pendentes.Count == 0,
                $"MT-4 em Autorizadas: concorre e vence (20%), nada pendente (promo={av.PromoId} total={av.TotalCent})");
            checar(av.Perdedoras.Count == 1 && av.Perdedoras[0].PromoId == "livre",
                "MT-5 liberada, a livre vira perdedora como sempre (uma promoção por pedido)");
            checar(ctx.Autorizadas["func"].LogId == "log-1" && ctx.Autorizadas["func"].Autorizador == "Marcos",
                "MT-6 o contexto guarda quem liberou e o registro da nuvem");

            ctx.Excluir("func");
            av = Promocoes.AvaliarCarrinho(promos, carrinho, sexta, ctx);
            checar(av.PromoId == "livre" && av.TotalCent == 100 && av.Pendentes.Count == 0,
                "MT-7 em Excluidas: nunca aplica e não volta a pedir (livre vale, nada pendente)");
            checar(!ctx.Autorizada("func") && ctx.Excluida("func") && !ctx.Pendente("func"),
                "MT-8 Excluir tira de Autorizadas: uma promoção está em no máximo um conjunto");
            ctx.Autorizar("func", "log-2", "Brenno");
            checar(ctx.Autorizada("func") && !ctx.Excluida("func"), "MT-9 Autorizar tira de Excluidas");

            ctx.Zerar();
            av = Promocoes.AvaliarCarrinho(promos, carrinho, sexta, ctx);
            checar(av.Pendentes.Count == 1 && av.PromoId == "livre",
                "MT-10 Zerar (nova venda / rascunho restaurado): volta a pendente e pede de novo");

            // só pede quando VENCERIA
            var fraca = P(Pct("fraca", "func 5", 5, "gerente"));
            av = Promocoes.AvaliarCarrinho(new[] { livre, fraca }, carrinho, sexta, ctx);
            checar(av.PromoId == "livre" && av.Pendentes.Count == 0,
                "MT-11 promoção com 2FA que PERDERIA para a livre não pede código (não atrapalha o balcão)");
            av = Promocoes.AvaliarCarrinho(new[] { func }, new[] { It("B", 1000) }, sexta, ctx);
            checar(av.Pendentes.Count == 0 && av.TotalCent == 0,
                "MT-12 promoção com 2FA que não alcança o carrinho não pede código");
            av = Promocoes.AvaliarCarrinho(new[] { func }, carrinho, sexta, ctx);
            checar(av.PromoId is null && av.TotalCent == 0 && av.Pendentes.Count == 1,
                "MT-13 só a pendente e mais nada: total zero (preço de tabela) e a pendente na lista");
            var fora = P(Pct("fora", "func fora da vigencia", 30, "gerente").Replace("\"fim\":null", "\"fim\":\"2026-08-31\""));
            av = Promocoes.AvaliarCarrinho(new[] { fora }, carrinho, sexta, ctx);
            checar(av.Pendentes.Count == 0, "MT-14 fora da vigência não pede código (vigência vem antes do portão)");

            // dois níveis pendentes ao mesmo tempo
            var dono25 = P(Pct("dono25", "desconto do dono", 25, "dono"));
            av = Promocoes.AvaliarCarrinho(new[] { livre, func, dono25 }, carrinho, sexta, ctx);
            checar(av.Pendentes.Select(p => p.PromoId).OrderBy(x => x).SequenceEqual(new[] { "dono25", "func" })
                   && av.Pendentes.First(p => p.PromoId == "dono25").Nivel == "dono",
                "MT-15 duas com 2FA que venceriam: as duas pendentes, cada uma com o seu nível");
            ctx.Autorizar("func", "l", "Marcos");
            av = Promocoes.AvaliarCarrinho(new[] { livre, func, dono25 }, carrinho, sexta, ctx);
            checar(av.PromoId == "func" && av.Pendentes.Count == 1 && av.Pendentes[0].PromoId == "dono25",
                "MT-16 liberada a do gerente (20%), a do dono (25%) ainda venceria: continua pendente");
            ctx.Autorizar("dono25", "l2", "Brenno");
            av = Promocoes.AvaliarCarrinho(new[] { livre, func, dono25 }, carrinho, sexta, ctx);
            checar(av.PromoId == "dono25" && av.TotalCent == 250 && av.Pendentes.Count == 0,
                "MT-17 as duas liberadas: vence a maior (25%), como manda 'uma promoção por pedido'");
            ctx.Zerar();

            // promoção de CARRINHO com portão
            var lx = P("{\"id\":\"lx\",\"nome\":\"leve 3 pague 2\",\"tipo\":\"leve_x_pague_y\",\"leve\":3,\"pague\":2,\"alvo\":\"todos\",\"ativa\":true,\"inicio\":\"2026-01-01\",\"fim\":null,\"config\":{\"autorizacao\":\"dono\"}}");
            av = Promocoes.AvaliarCarrinho(new[] { lx }, new[] { It("A", 1000, 3) }, sexta, ctx);
            checar(av.TotalCent == 0 && av.Pendentes.Count == 1 && av.Pendentes[0].DescontoCent == 1000 && av.Pendentes[0].Nivel == "dono",
                "MT-18 leve 3 pague 2 com 2FA do dono: pendente com o desconto da unidade grátis, nada aplicado");
            ctx.Autorizar("lx", "l3", "Brenno");
            av = Promocoes.AvaliarCarrinho(new[] { lx }, new[] { It("A", 1000, 3) }, sexta, ctx);
            checar(av.TotalCent == 1000 && av.UnidadesGratis[0] == 1, "MT-19 liberada, a unidade grátis entra");
            ctx.Zerar();

            // preço do CARD
            var pe = Promocoes.PrecoEfetivoCent(promos, "A", "Donuts", 1000, sexta, ctx);
            var (cent, nome) = pe;
            checar(cent == 900 && nome == "quinta 10" && pe.Pendentes.Count == 1 && pe.Pendentes[0].PromoId == "func" && pe.Pendentes[0].DescontoCent == 200,
                $"MT-20 card: pendente não muda o preço (vale a livre, 9,00) e volta em Pendentes (mediu {cent})");
            ctx.Autorizar("func", "l", "Marcos");
            (cent, nome) = Promocoes.PrecoEfetivoCent(promos, "A", "Donuts", 1000, sexta, ctx);
            checar(cent == 800 && nome == "desconto funcionario", "MT-21 card: liberada, o preço do card cai para 8,00");
            ctx.Excluir("func");
            pe = Promocoes.PrecoEfetivoCent(promos, "A", "Donuts", 1000, sexta, ctx);
            checar(pe.Cent == 900 && pe.Pendentes.Count == 0, "MT-22 card: excluída, 9,00 e nada pendente");
            ctx.Zerar();
            pe = Promocoes.PrecoEfetivoCent(new[] { livre, fraca }, "A", "Donuts", 1000, sexta, ctx);
            checar(pe.Cent == 900 && pe.Pendentes.Count == 0, "MT-23 card: com 2FA que perderia não é pendente");

            // ── VITRINE (categoria Promoção do caixa): promoção com 2FA NUNCA anuncia ──
            // A vitrine é para o cliente; "desconto funcionário" com código do gerente
            // não é oferta. Opção escolhida: a vitrine não recebe contexto, logo a
            // promoção com portão fica de fora SEMPRE (liberada ou não). O preço do
            // card na categoria normal (PrecoEfetivoCent) continua caindo quando liberada.
            {
                ctx.Zerar();
                var livreB = P(Pct("livreB", "quinta 10", 10, null, "\"alvo\":\"produtos\",\"produto_ids\":[\"B\"]"));
                var vitrine = Promocoes.ProdutosEmPromocao(new[] { func, livreB }, sexta);
                checar(vitrine.ContainsKey("B") && !vitrine.ContainsKey("A"),
                    "MT-24 vitrine: a livre entra; a com 2FA (desconto funcionário) NÃO anuncia para o cliente");
                var porDia2fa = P("{\"id\":\"pd2\",\"nome\":\"donuts do dia (func)\",\"tipo\":\"percentual\",\"alvo\":\"produtos\",\"produto_ids\":[\"C\"],\"ativa\":true,\"inicio\":\"2026-01-01\",\"fim\":null,"
                    + "\"regras_semana\":[{\"dias\":[5],\"precos_cent\":{\"C\":500},\"produto_ids\":[\"C\"]}],\"config\":{\"autorizacao\":\"dono\"}}");
                var so2fa = Promocoes.ProdutosEmPromocao(new[] { func, porDia2fa }, sexta);
                checar(so2fa.Count == 0, "MT-25 só promoções com 2FA (percentual e por regra do dia): categoria Promoção vazia, nem card cinza");
                ctx.Autorizar("func", "l", "Marcos");
                checar(!Promocoes.ProdutosEmPromocao(new[] { func, livreB }, sexta).ContainsKey("A")
                       && typeof(Promocoes).GetMethod("ProdutosEmPromocao")!.GetParameters().All(pa => pa.ParameterType != typeof(Promocoes.ContextoAutorizacao)),
                    "MT-26 liberada na comanda de UM cliente continua fora da vitrine (a vitrine não conhece o contexto, por desenho)");
                ctx.Zerar();
            }

            // ── COMANDA ESVAZIADA ITEM A ITEM ('−' até zero, lixeira) ────────────
            // Só EsvaziarComanda zerava o contexto; tirando os itens um a um a comanda
            // ficava vazia com a liberação do gerente viva para o próximo cliente.
            // A regra mora no núcleo (ComandaMudou) e a tela a chama num ponto só.
            {
                ctx.Zerar();
                ctx.Autorizar("func", "log-9", "Marcos");
                var cx2 = new List<Promocoes.ItemCarrinho> { It("A", 1000, 2) };
                var av2 = Promocoes.AvaliarCarrinho(promos, cx2, sexta, ctx);
                checar(av2.PromoId == "func" && av2.TotalCent == 400, "MT-27 liberada: 2 unidades de A com 20% (R$ 4,00)");
                cx2[0] = It("A", 1000, 1);                                   // '−': 2 → 1, a linha continua
                checar(!PortaoPromocao.ComandaMudou(ctx, cx2.Count) && ctx.Autorizada("func"),
                    "MT-28 tirar uma unidade não zera: a liberação vale enquanto a comanda tem item");
                av2 = Promocoes.AvaliarCarrinho(promos, cx2, sexta, ctx);
                checar(av2.PromoId == "func" && av2.TotalCent == 200 && av2.Pendentes.Count == 0, "MT-29 com 1 unidade a promoção segue aplicada sem perguntar de novo");
                cx2.Clear();                                                 // '−': 1 → 0, a linha sai, comanda VAZIA
                checar(PortaoPromocao.ComandaMudou(ctx, cx2.Count) && !ctx.Autorizada("func") && ctx.Autorizadas.Count == 0 && ctx.Excluidas.Count == 0,
                    "MT-30 comanda vazia item a item: o contexto morre com ela (Autorizadas e Excluidas)");
                cx2.Add(It("A", 1000));                                      // próximo cliente
                av2 = Promocoes.AvaliarCarrinho(promos, cx2, sexta, ctx);
                checar(av2.Pendentes.Count == 1 && av2.Pendentes[0].PromoId == "func" && av2.PromoId == "livre" && av2.TotalCent == 100,
                    "MT-31 o próximo cliente pede o código de novo (a liberação do gerente não passa de uma venda para a outra)");
                ctx.Excluir("func");
                PortaoPromocao.ComandaMudou(ctx, 0);
                checar(ctx.Pendente("func"), "MT-32 recusada na venda anterior: a comanda vazia também esquece a recusa (pergunta de novo)");
                ctx.Zerar();
            }
        }

        // ── 3. MÁQUINA DE ESTADOS contra o FakeTotp com nível ────────────────
        using var fake = new FakeTotp();
        var relogio = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        fake.Relogio = () => relogio;
        void ProximoPasso() => relogio = relogio.AddSeconds(30);
        var cli = new ClienteAutorizacao(_ => Task.FromResult<string?>(fake.Token), fake.Url, fake.AnonKey,
            TimeSpan.FromSeconds(5), () => TerminalUuid);
        var comanda = new PortaoPromocao.Comanda("c0ffee1234", "Caixa Savassi 1", "American Day Savassi", "Bia");
        var pendFunc = new Promocoes.PromoPendente("func", "desconto funcionario", "gerente", 200);
        var pendDono = new Promocoes.PromoPendente("dono25", "desconto do dono", "dono", 250);
        {
            var pedido = PortaoPromocao.Pedido(comanda, pendFunc);
            checar(pedido.Tipo == "promocao" && pedido.Nivel == "gerente" && pedido.ValorCent == 200
                   && pedido.Referencia == "promocao:c0ffee1234:func" && pedido.PromocaoId == "func"
                   && pedido.PromocaoNome == "desconto funcionario" && pedido.Loja == "American Day Savassi" && pedido.Operador == "Bia",
                "PP-1 o pedido é tipo 'promocao', nível da promoção, referência comanda+promoção, valor = desconto");
            var det = Autorizacao.Detalhe(pedido);
            checar((string?)det["promocao_id"] == "func" && (string?)det["nivel"] == "gerente" && (string?)det["promocao"] == "desconto funcionario",
                "PP-2 o detalhe do log da nuvem leva promocao_id, nome e nível");
            checar(PortaoPromocao.Pedido(comanda, pendDono).Nivel == "dono", "PP-3 promoção 'dono' pede no nível dono");
            checar(new PedidoAutorizacao("t", "estorno:x", 100).Nivel == "dono",
                "PP-4 PedidoAutorizacao sem nível é 'dono' (estorno e cancelamento não mudam)");
        }

        // PP-5 gerente aceita o código do MANAGER
        {
            ctx.Zerar(); fake.ZerarBaldes();
            var tela = new TelaFalsa { AoPedirCodigo = _ => fake.CodigoAgoraGerente() };
            var r = await PortaoPromocao.ResolverAsync(new[] { pendFunc }, ctx, comanda, cli, tela);
            var ultimo = fake.Log.LastOrDefault();
            checar(r.Count == 1 && r[0].Autorizada && r[0].Desfecho.Via == ViaAutorizacao.Totp && r[0].Desfecho.AprovadoPor == "Marcos",
                $"PP-5 nível gerente: o código do autenticador do GERENTE libera (por={r.FirstOrDefault()?.Desfecho.AprovadoPor} motivo={r.FirstOrDefault()?.Desfecho.Motivo})");
            checar(ctx.Autorizada("func") && ctx.Autorizadas["func"].Autorizador == "Marcos" && ctx.Autorizadas["func"].LogId == r[0].Desfecho.TokenId,
                "PP-6 entra em Autorizadas com o nome do gerente e o id do registro");
            checar(tela.Niveis.Count == 1 && tela.Niveis[0] == "gerente",
                "PP-7 a tela é chamada com nível 'gerente' (rótulo 'Código do autenticador do gerente')");
            var corpo = JsonDocument.Parse(fake.Chamadas.Last().Corpo).RootElement;
            var jd = corpo.GetProperty("_detalhe");
            checar(Txt(corpo, "_nivel") == "gerente" && Txt(corpo, "_tipo") == "promocao"
                   && Txt(corpo, "_referencia") == "promocao:c0ffee1234:func" && Txt(corpo, "_terminal_uuid") == TerminalUuid
                   && Txt(jd, "promocao_id") == "func" && Txt(jd, "nivel") == "gerente",
                "PP-8 a RPC recebe _nivel=gerente, _tipo=promocao, a referência da comanda e o detalhe com a promoção");
            checar(ultimo is { Ok: true, Nivel: "gerente", Autorizador: "Marcos" },
                "PP-9 o log da nuvem grava o nível e quem foi");
            checar(r[0].Desfecho.Motivo.Contains("autenticador do gerente", StringComparison.Ordinal)
                   && Autorizacao.Trilha(r[0].Desfecho).Contains("autenticador do gerente (Marcos", StringComparison.Ordinal),
                "PP-10 motivo e trilha dizem 'autenticador do gerente'");
            var linha = PortaoPromocao.LinhaAuditoria(r[0]);
            checar(linha.Contains("promo=func", StringComparison.Ordinal) && linha.Contains("nivel=gerente", StringComparison.Ordinal)
                   && linha.Contains("Marcos", StringComparison.Ordinal) && linha.Contains("registro " + r[0].Desfecho.TokenId![..8], StringComparison.Ordinal),
                "PP-11 a linha da auditoria local leva promoção, nível, autorizador e registro");
            // e o motor agora aplica
            var func = P(Pct("func", "desconto funcionario", 20, "gerente"));
            var av = Promocoes.AvaliarCarrinho(new[] { func }, new[] { It("A", 1000) }, sexta, ctx);
            checar(av.PromoId == "func" && av.TotalCent == 200 && av.Pendentes.Count == 0,
                "PP-12 depois de liberada, o motor aplica na reavaliação");
        }

        // PP-13 gerente aceita também o código do OWNER
        {
            ctx.Zerar(); fake.ZerarBaldes(); ProximoPasso();
            var tela = new TelaFalsa { AoPedirCodigo = _ => fake.CodigoAgora() };
            var r = await PortaoPromocao.ResolverAsync(new[] { pendFunc }, ctx, comanda, cli, tela);
            checar(r[0].Autorizada && r[0].Desfecho.AprovadoPor == "Brenno" && ctx.Autorizadas["func"].Autorizador == "Brenno",
                "PP-13 nível gerente: o código do DONO também libera");
        }

        // PP-14 dono RECUSA o código do manager (3 vezes) e exclui
        {
            ctx.Zerar(); fake.ZerarBaldes(); ProximoPasso();
            var tela = new TelaFalsa { AoPedirCodigo = _ => fake.CodigoAgoraGerente() };
            var antes = fake.Log.Count;
            var r = await PortaoPromocao.ResolverAsync(new[] { pendDono }, ctx, comanda, cli, tela);
            var falhas = fake.Log.Skip(antes).ToList();
            checar(r.Count == 1 && !r[0].Autorizada && tela.VezesPediuCodigo == 3
                   && r[0].Desfecho.Motivo == "Código inválido 3 vezes. Promoção não autorizada.",
                $"PP-14 nível dono: o código do GERENTE é inválido 3 vezes e a promoção não sai (motivo={r[0].Desfecho.Motivo})");
            checar(ctx.Excluida("dono25") && !ctx.Autorizada("dono25"), "PP-15 entra em Excluidas");
            checar(falhas.Count == 3 && falhas.All(f => f is { Ok: false, Motivo: "codigo invalido", Nivel: "dono" }),
                "PP-16 o log da nuvem tem as 3 falhas no nível dono");
            checar(tela.Niveis.All(n => n == "dono") && (tela.Avisos[1] ?? "").Contains("inválido", StringComparison.Ordinal),
                "PP-17 a tela é chamada no nível dono e avisa 'inválido' entre as tentativas");
            // e nada pergunta de novo nesta venda
            var chamadas = fake.Chamadas.Count;
            var tela2 = new TelaFalsa { AoPedirCodigo = _ => fake.CodigoAgora() };
            var r2 = await PortaoPromocao.ResolverAsync(new[] { pendDono }, ctx, comanda, cli, tela2);
            checar(r2.Count == 0 && tela2.VezesPediuCodigo == 0 && fake.Chamadas.Count == chamadas,
                "PP-18 excluída nesta venda: não pergunta de novo nem vai à nuvem");
            var dono25 = P(Pct("dono25", "desconto do dono", 25, "dono"));
            var av = Promocoes.AvaliarCarrinho(new[] { dono25 }, new[] { It("A", 1000) }, sexta, ctx);
            checar(av.TotalCent == 0 && av.Pendentes.Count == 0, "PP-19 o motor segue SEM ela e sem pendência");
            checar(PortaoPromocao.AvisoNaoAplicada("Desconto do dono") == "Promoção Desconto do dono não aplicada",
                "PP-20 o aviso é uma linha: 'Promoção X não aplicada'");
            checar(PortaoPromocao.AvisoNaoAplicada(new[] { "Desconto do dono" }) == "Promoção Desconto do dono não aplicada"
                   && PortaoPromocao.AvisoNaoAplicada(new[] { "Desconto do dono", "Desconto funcionário" }) == "Promoções Desconto do dono e Desconto funcionário não aplicadas"
                   && PortaoPromocao.AvisoNaoAplicada(Array.Empty<string>()) == "",
                "PP-20b duas recusadas na mesma venda viram UMA linha (nunca dois avisos, nunca dois modais)");
            checar(PortaoPromocao.LinhaAuditoria(r[0]).Contains("recusada:", StringComparison.Ordinal),
                "PP-21 a auditoria local da recusa leva o motivo");
        }

        // PP-22 dono aceita o código do owner
        {
            ctx.Zerar(); fake.ZerarBaldes(); ProximoPasso();
            var tela = new TelaFalsa { AoPedirCodigo = _ => fake.CodigoAgora() };
            var r = await PortaoPromocao.ResolverAsync(new[] { pendDono }, ctx, comanda, cli, tela);
            var corpo = JsonDocument.Parse(fake.Chamadas.Last().Corpo).RootElement;
            checar(r[0].Autorizada && r[0].Desfecho.AprovadoPor == "Brenno" && !corpo.TryGetProperty("_nivel", out _),
                "PP-22 nível dono: o código do dono libera, e o corpo vai SEM _nivel (default 'dono' nas duas versões da RPC)");
        }

        // PP-23 operador cancela: exclui, sem ir à nuvem, e não pergunta de novo
        {
            ctx.Zerar(); fake.ZerarBaldes();
            var chamadas = fake.Chamadas.Count;
            var tela = new TelaFalsa { AoPedirCodigo = _ => null };
            var r = await PortaoPromocao.ResolverAsync(new[] { pendFunc }, ctx, comanda, cli, tela);
            checar(!r[0].Autorizada && r[0].Desfecho.Avisado && ctx.Excluida("func") && fake.Chamadas.Count == chamadas,
                "PP-23 cancelou a tela do código: promoção excluída, nuvem nem chamada");
            var r2 = await PortaoPromocao.ResolverAsync(new[] { pendFunc }, ctx, comanda, cli, tela);
            checar(r2.Count == 0 && tela.VezesPediuCodigo == 1, "PP-24 UMA pergunta por promoção por venda");
            ctx.Zerar();
            var r3 = await PortaoPromocao.ResolverAsync(new[] { pendFunc }, ctx, comanda, cli, tela);
            checar(r3.Count == 1 && tela.VezesPediuCodigo == 2, "PP-25 nova venda (Zerar): pergunta de novo");
        }

        // PP-26 sem rede / sem nuvem / sem sessão: exclui com o texto certo
        {
            ctx.Zerar();
            var morta = new ClienteAutorizacao(_ => Task.FromResult<string?>("t"), "http://127.0.0.1:9", "k", TimeSpan.FromSeconds(2));
            var tela = new TelaFalsa { AoPedirCodigo = _ => "123456" };
            var r = await PortaoPromocao.ResolverAsync(new[] { pendFunc }, ctx, comanda, morta, tela);
            checar(!r[0].Autorizada && r[0].Desfecho.Motivo == "Sem internet. Promoção não autorizada." && ctx.Excluida("func"),
                $"PP-26 sem internet: 'Sem internet. Promoção não autorizada.' e exclui (motivo={r[0].Desfecho.Motivo})");
            ctx.Zerar();
            var rNull = await PortaoPromocao.ResolverAsync(new[] { pendFunc }, ctx, comanda, null, new TelaFalsa());
            checar(!rNull[0].Autorizada && ctx.Excluida("func") && rNull[0].Desfecho.Motivo.EndsWith("Promoção não autorizada.", StringComparison.Ordinal),
                "PP-27 caixa sem nuvem configurada: exclui (não há PIN para cair)");
            ctx.Zerar(); fake.ConfiguradoGerente = false; fake.Configurado = false;
            var rCfg = await PortaoPromocao.ResolverAsync(new[] { pendFunc }, ctx, comanda, cli, new TelaFalsa { AoPedirCodigo = _ => "123456" });
            fake.ConfiguradoGerente = true; fake.Configurado = true;
            checar(!rCfg[0].Autorizada && rCfg[0].Desfecho.Motivo.StartsWith("Autenticador do gerente não configurado.", StringComparison.Ordinal),
                $"PP-28 ninguém com autenticador no nível gerente: o motivo fala do gerente (motivo={rCfg[0].Desfecho.Motivo})");
        }

        // PP-29 duas pendentes numa passagem: uma liberada, outra cancelada
        {
            ctx.Zerar(); fake.ZerarBaldes(); ProximoPasso();
            var vez = 0;
            var tela = new TelaFalsa { AoPedirCodigo = _ => ++vez == 1 ? fake.CodigoAgoraGerente() : null };
            var r = await PortaoPromocao.ResolverAsync(new[] { pendFunc, pendDono }, ctx, comanda, cli, tela);
            checar(r.Count == 2 && r[0].Autorizada && !r[1].Autorizada && ctx.Autorizada("func") && ctx.Excluida("dono25")
                   && tela.Niveis.SequenceEqual(new[] { "gerente", "dono" }),
                "PP-29 duas pendentes: cada uma perguntada no seu nível, uma liberada e a outra excluída");
        }

        // PP-30 o estorno e a RPC: corpo SEM _nivel (= dono), e o código do gerente não estorna
        {
            fake.ZerarBaldes(); ProximoPasso();
            var pedidoEstorno = new PedidoAutorizacao("Caixa", Autorizacao.Referencia("tef-1", "000777", 500, 9), 500);
            var tela = new TelaFalsa { AoPedirCodigo = _ => fake.CodigoAgoraGerente() };
            var d = await Autorizacao.ResolverAsync(cli, pedidoEstorno, tela);
            var corpo = JsonDocument.Parse(fake.Chamadas.Last().Corpo).RootElement;
            checar(!d.Autorizado && !corpo.TryGetProperty("_nivel", out _) && tela.Niveis.All(n => n == "dono"),
                "PP-30 estorno vai SEM a chave _nivel (default 'dono') e o código do gerente NÃO estorna");
            ProximoPasso();
            var v = await cli.ValidarTotpAsync(fake.CodigoAgora(), "estorno:z", "estorno", null, "dono", CancellationToken.None);
            checar(v.Ok && (v.Autorizador ?? "").Contains("Brenno"), "PP-31 estorno com o código do dono continua liberando (assinatura = dono)");
            var vTorto = await cli.ValidarTotpAsync(fake.CodigoAgora(), "estorno:z2", "estorno", null, "supervisor", CancellationToken.None);
            var corpoTorto = JsonDocument.Parse(fake.Chamadas.Last().Corpo).RootElement;
            checar(!corpoTorto.TryGetProperty("_nivel", out _), "PP-32 nível desconhecido no cliente vira o corpo de sempre (sem _nivel; nunca manda outro nível à RPC)");
        }

        // PP-33..36 a RPC de PRODUÇÃO antes da migration 20260905120000 (sem _nivel):
        // o exe novo NÃO pode quebrar o estorno se for publicado antes da migration.
        // O PostgREST casa a RPC pelos NOMES do corpo: com "_nivel" presente é 404.
        {
            fake.RpcAntiga = true;
            try
            {
                fake.ZerarBaldes(); ProximoPasso();
                var pedidoEstorno = new PedidoAutorizacao("Caixa", Autorizacao.Referencia("tef-2", "000778", 700, 3), 700);
                var tela = new TelaFalsa { AoPedirCodigo = _ => fake.CodigoAgora() };
                var d = await Autorizacao.ResolverAsync(cli, pedidoEstorno, tela);
                checar(d.Autorizado && (d.AprovadoPor ?? "").Contains("Brenno") && tela.VezesPediuCodigo == 1,
                    $"PP-33 RPC antiga: o estorno com o código do dono LIBERA (motivo={d.Motivo})");
                ProximoPasso();
                var cancel = await cli.ValidarTotpAsync(fake.CodigoAgora(), "cancelamento:c1", "cancelamento", null, "dono", CancellationToken.None);
                checar(cancel.Ok, $"PP-34 RPC antiga: o cancelamento continua liberando (motivo={cancel.Motivo})");
                ctx.Zerar(); fake.ZerarBaldes(); ProximoPasso();
                var telaDono = new TelaFalsa { AoPedirCodigo = _ => fake.CodigoAgora() };
                var rDono = await PortaoPromocao.ResolverAsync(new[] { pendDono }, ctx, comanda, cli, telaDono);
                checar(rDono[0].Autorizada && ctx.Autorizada("dono25"),
                    "PP-35 RPC antiga: promoção com 2FA do DONO libera (corpo sem _nivel)");
                ctx.Zerar(); fake.ZerarBaldes(); ProximoPasso();
                var telaGer = new TelaFalsa { AoPedirCodigo = _ => fake.CodigoAgoraGerente() };
                var rGer = await PortaoPromocao.ResolverAsync(new[] { pendFunc }, ctx, comanda, cli, telaGer);
                checar(!rGer[0].Autorizada && ctx.Excluida("func") && telaGer.VezesPediuCodigo == 1
                       && (rGer[0].Desfecho.Motivo ?? "").Contains("404", StringComparison.Ordinal),
                    "PP-36 RPC antiga: promoção com 2FA do GERENTE fica excluída (404, sem insistir); só ela depende da migration");
            }
            finally { fake.RpcAntiga = false; }
        }

        // ── 4. PAYLOAD DA VENDA ──────────────────────────────────────────────
        {
            var arquivo = Path.Combine(Path.GetTempPath(), $"promo_aut_{Guid.NewGuid():N}.db");
            var anterior = Banco.CaminhoForcado;
            Banco.CaminhoForcado = arquivo;
            try
            {
                Banco.Migrar(arquivo);
                using var cx = Banco.Abrir(arquivo);
                var op = new Operador("op-pa", "Bia", "operador");
                Operadores.Salvar(cx, op.Id, op.Nome, "4321", "operador");
                var sessao = Caixa.Abrir(cx, op, Dinheiro.DeReais(100));
                var aut = new Promocoes.AutorizacaoPromo("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", "Marcos");
                LinhaVenda Linha(string id, long preco, long desconto, string? promo, Promocoes.AutorizacaoPromo? a)
                    => new(id, id, "DONUT " + id, new Quantidade(1000), new Dinheiro(preco), new Dinheiro(preco - desconto),
                           "UN", "19053100", null, "102", null, 0, new Dinheiro(desconto), promo, promo is null ? null : "desconto funcionario", 0, null, a);
                var vg = Vendas.Finalizar(cx, sessao, op,
                    new[] { Linha("A", 1000, 200, "func", aut), Linha("B", 500, 0, null, null) },
                    new[] { new PagamentoVenda("dinheiro", Dinheiro.DeReais(13m), Dinheiro.Zero) }, null, "Loja", null);
                var payload = cx.ExecuteScalar<string>("SELECT payload FROM outbox WHERE ref_id = @v", new { v = vg.Id }) ?? "";
                var itens = JsonDocument.Parse(payload).RootElement.GetProperty("p_itens");
                var a0 = itens[0]; var b1 = itens[1];
                checar(a0.TryGetProperty("autorizacao", out var ja) && Txt(ja, "log_id") == aut.LogId && Txt(ja, "autorizador") == "Marcos"
                       && Txt(a0, "promocao_id") == "func",
                    "PL-1 o item com promoção liberada leva autorizacao {log_id, autorizador} junto de promocao_id");
                checar(!b1.TryGetProperty("autorizacao", out _), "PL-2 o item sem promoção NÃO ganha a chave autorizacao");
                var chaves = a0.EnumerateObject().Select(p => p.Name).ToList();
                checar(chaves.Take(11).SequenceEqual(new[] { "pdv_product_id", "codigo", "descricao", "ncm", "csosn", "unidade", "qtd", "valor_unitario", "desconto", "promocao_id", "promocao_nome" }),
                    "PL-3 as 11 chaves de sempre continuam na mesma ordem antes de `autorizacao`");
                var aud = cx.QueryFirst("SELECT autorizador, detalhe FROM auditoria WHERE evento = 'promo_aplicada'");
                checar((string?)aud.autorizador == "totp:0f1e2d3c" && ((string)aud.detalhe).Contains("liberada por Marcos (registro 0f1e2d3c)", StringComparison.Ordinal),
                    "PL-4 promo_aplicada leva autorizador totp:<8> e 'liberada por Marcos (registro ...)'");
                // venda comum: byte a byte como era
                var vg2 = Vendas.Finalizar(cx, sessao, op,
                    new[] { Linha("A", 1000, 100, "livre", null) },
                    new[] { new PagamentoVenda("dinheiro", Dinheiro.DeReais(9m), Dinheiro.Zero) }, null, "Loja", null);
                var payload2 = cx.ExecuteScalar<string>("SELECT payload FROM outbox WHERE ref_id = @v", new { v = vg2.Id }) ?? "";
                checar(!payload2.Contains("autorizacao", StringComparison.Ordinal) && payload2.Contains("\"promocao_id\":\"livre\"", StringComparison.Ordinal),
                    "PL-5 venda com promoção livre não leva a chave (cupom e promoção comum não mudam)");
            }
            finally
            {
                Banco.CaminhoForcado = anterior;
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { File.Delete(arquivo); } catch { }
            }
        }

        // ── 5. GARANTIAS POR FONTE ───────────────────────────────────────────
        {
            var venda = Fonte("Telas", "Venda.xaml.cs");
            checar(venda.Length > 0, "FP-1 achei a fonte da tela de venda");
            var chamadasMotor = Chamadas(venda, "PrecoEfetivoCent").Concat(Chamadas(venda, "AvaliarCarrinho")).ToList();
            checar(chamadasMotor.Count >= 2 && chamadasMotor.All(c => c.Contains("_autorizacao", StringComparison.Ordinal)),
                $"FP-2 toda chamada de PrecoEfetivoCent/AvaliarCarrinho na tela passa o contexto _autorizacao ({chamadasMotor.Count} chamadas)");
            var vezesClear = Regex.Matches(venda, @"_comanda\.Clear\(\)").Count;
            var esvaziar = Metodo(venda, "private void EsvaziarComanda()");
            checar(vezesClear == 1 && esvaziar.Contains("_comanda.Clear()", StringComparison.Ordinal)
                   && esvaziar.Contains("_autorizacao.Zerar()", StringComparison.Ordinal) && esvaziar.Contains("_comandaId = ", StringComparison.Ordinal),
                $"FP-3 só EsvaziarComanda esvazia a comanda, e ele zera o contexto e troca o id da comanda ({vezesClear} Clear)");
            var rascunho = Metodo(venda, "private void OferecerRascunho()");
            checar(rascunho.Contains("EsvaziarComanda()", StringComparison.Ordinal) && rascunho.Contains("PintarComanda()", StringComparison.Ordinal),
                "FP-4 restaurar o rascunho passa por EsvaziarComanda (zera) e repinta (pergunta de novo)");
            checar(!venda.Contains("_autorizacao.Autorizar(", StringComparison.Ordinal) && !venda.Contains("_autorizacao.Excluir(", StringComparison.Ordinal),
                "FP-5 a tela nunca fabrica Autorizar/Excluir por conta própria: só o PortaoPromocao escreve no contexto");
            var quemAutoriza = Directory.EnumerateFiles(Path.Combine(Raiz() ?? ".", "Pdv.Nucleo"), "*.cs")
                .Concat(Directory.EnumerateFiles(Path.Combine(Raiz() ?? ".", "Telas"), "*.cs"))
                .Concat(Directory.EnumerateFiles(Raiz() ?? ".", "*.cs", SearchOption.TopDirectoryOnly))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\.Autorizar\("))
                .Select(Path.GetFileName).ToList();
            checar(quemAutoriza.SequenceEqual(new[] { "PortaoPromocao.cs" }),
                "FP-6 no .exe só PortaoPromocao.cs chama .Autorizar( (e só depois de um DesfechoAutorizacao com Via=Totp)" + $" [{string.Join(", ", quemAutoriza)}]");
            var portao = Trecho(Fonte("Pdv.Nucleo", "PortaoPromocao.cs"), "public static async Task<List<Resultado>> ResolverAsync", "public static string AvisoNaoAplicada");
            checar(Ordem(portao, "Autorizacao.ResolverAsync", "contexto.Autorizar(") && portao.Contains("d.Autorizado && d.TokenId is { Length: > 0 }", StringComparison.Ordinal)
                   && !portao.Contains("ConfigureAwait(false)", StringComparison.Ordinal),
                "FP-7 o portão só autoriza com desfecho aprovado E registro na nuvem, sem ConfigureAwait(false)");
            var pintar = Metodo(venda, "private void PintarComanda()");
            checar(pintar.Contains("Pendentes.Count > 0", StringComparison.Ordinal) && pintar.Contains("PerguntarPromocoesAsync", StringComparison.Ordinal),
                "FP-8 PintarComanda dispara a pergunta quando o motor devolve pendentes");
            var perguntar = Metodo(venda, "private async Task PerguntarPromocoesAsync()");
            checar(perguntar.Contains("PortaoPromocao.ResolverAsync", StringComparison.Ordinal) && perguntar.Contains("Caixa.Auditar", StringComparison.Ordinal)
                   && perguntar.Contains("AvisoNaoAplicada", StringComparison.Ordinal) && perguntar.Contains("new TelaAutorizacao(dono)", StringComparison.Ordinal)
                   && Ordem(perguntar, "PortaoPromocao.ResolverAsync", "PintarComanda()")
                   && perguntar.Contains("if (respondidas > 0)", StringComparison.Ordinal),
                "FP-9 a pergunta passa pelo portão do núcleo, audita, avisa numa linha e repinta depois (só se respondeu algo: sem laço pelo BeginInvoke)");
            var finalizar = Metodo(venda, "private void Finalizar(object sender, RoutedEventArgs e)");
            var guardaPendente = Trecho(finalizar, "if (agora.Pendentes.Count > 0)", "}");
            checar(Ordem(finalizar, "agora.Pendentes.Count > 0", "new LinhaVenda(") && guardaPendente.Contains("return;", StringComparison.Ordinal)
                   && guardaPendente.Contains("PintarComanda();", StringComparison.Ordinal)
                   && finalizar.Contains("desconto.Centavos > 0 ? autorizacaoPromo : null", StringComparison.Ordinal),
                "FP-10 Finalizar segura a venda com pendente e passa a autorização da promoção para a LinhaVenda");
            var estorno = Metodo(venda, "private async Task EstornarTefAsync");
            var cancel = Metodo(venda, "private async Task CancelarVendaAsync");
            checar(estorno.Length > 0 && cancel.Length > 0 && !estorno.Contains("Nivel =", StringComparison.Ordinal) && !cancel.Contains("Nivel =", StringComparison.Ordinal),
                "FP-11 estorno e cancelamento não mexem no nível: continuam exigindo o dono");
            var pedirCodigo = Fonte("Telas", "PedirCodigo.cs");
            checar(pedirCodigo.Contains("Código do autenticador do gerente", StringComparison.Ordinal) && pedirCodigo.Contains("Autorização do gerente", StringComparison.Ordinal)
                   && pedirCodigo.Contains("Código do autenticador do dono", StringComparison.Ordinal),
                "FP-12 a tela do código tem os dois rótulos: 'do gerente' e 'do dono'");
            var telaAut = Fonte("Telas", "TelaAutorizacao.cs");
            checar(telaAut.Contains("PedirCodigo.Mostrar(_dono, aviso, nivel)", StringComparison.Ordinal),
                "FP-13 TelaAutorizacao repassa o nível para a tela do código");
            var cliente = Trecho(Fonte("Pdv.Nucleo", "Autorizacao.cs"), "public sealed class ClienteAutorizacao", "private async Task<(int Status, string? Corpo)> EnviarAsync");
            checar(cliente.Contains("if (nivel is Autorizacao.NivelGerente) corpo[\"_nivel\"] = Autorizacao.NivelGerente;", StringComparison.Ordinal)
                   && Regex.Matches(cliente, "\"_nivel\"").Count == 1,
                "FP-14 o cliente só manda _nivel quando é 'gerente' (estorno, cancelamento e promoção do dono vão sem a chave: compatível com a RPC de produção)");
            var promoCs = Fonte("Pdv.Nucleo", "Promocoes.cs");
            checar(Regex.IsMatch(promoCs, @"PrecoEfetivoCent\([^)]*ContextoAutorizacao contexto\)") && Regex.IsMatch(promoCs, @"AvaliarCarrinho\([^)]*ContextoAutorizacao contexto,"),
                "FP-15 o contexto é parâmetro OBRIGATÓRIO do motor (sem overload sem ele)");

            // ── revisão 05/09: três achados ──────────────────────────────────
            // (1) comanda esvaziada ITEM A ITEM ('−' até zero, lixeira) não passava por
            //     EsvaziarComanda: a liberação do gerente valia para o próximo cliente.
            var pintarAntesDeAvaliar = Trecho(pintar, "private void PintarComanda()", "_avaliacao = AvaliarComanda(");
            checar(pintarAntesDeAvaliar.Contains("PortaoPromocao.ComandaMudou(_autorizacao, _comanda.Count)", StringComparison.Ordinal)
                   && pintarAntesDeAvaliar.Contains("_comandaId = Guid.NewGuid()", StringComparison.Ordinal),
                "FP-16 PintarComanda passa a comanda por ComandaMudou ANTES de avaliar: vazia por qualquer caminho zera o contexto e troca o id");
            var remocoes = Regex.Matches(venda, @"_comanda\.Remove\(item\);(.{0,160})", RegexOptions.Singleline);
            checar(remocoes.Count >= 2 && remocoes.All(m => m.Groups[1].Value.Contains("PintarComanda()", StringComparison.Ordinal)),
                $"FP-17 toda remoção de linha ('−' até zero e lixeira) repinta em seguida, e é a pintura que vê a comanda vazia ({remocoes.Count} remoções)");
            // (2) depois de cancelar o código, 'não aplicada' era um SEGUNDO modal.
            checar(!perguntar.Contains("Dialogo.", StringComparison.Ordinal) && perguntar.Contains("AvisoLeve(", StringComparison.Ordinal)
                   && perguntar.Contains("AvisoNaoAplicada(recusadas)", StringComparison.Ordinal),
                "FP-18 depois de cancelar o código NÃO abre outro modal: o 'não aplicada' é aviso leve de uma linha (as recusadas juntas)");
            var avisoLeve = Metodo(venda, "private void AvisoLeve(string texto)");
            checar(avisoLeve.Length > 0 && !avisoLeve.Contains("Dialogo", StringComparison.Ordinal)
                   && avisoLeve.Contains("ToastAviso.Visibility = Visibility.Visible", StringComparison.Ordinal)
                   && avisoLeve.Contains("DispatcherTimer", StringComparison.Ordinal) && avisoLeve.Contains("Visibility.Collapsed", StringComparison.Ordinal),
                "FP-19 AvisoLeve é o toast da casa (o mesmo do delivery e do chat): aparece sem bloquear e some sozinho");
            var xamlVenda = Fonte("Telas", "Venda.xaml");
            var toastAviso = Trecho(xamlVenda, "x:Name=\"ToastAviso\"", "</Border>");
            checar(toastAviso.Length > 0 && toastAviso.Contains("x:Name=\"TxtToastAviso\"", StringComparison.Ordinal)
                   && !toastAviso.Contains("MouseLeftButtonUp", StringComparison.Ordinal) && !toastAviso.Contains("toque para", StringComparison.Ordinal),
                "FP-20 o toast do aviso existe no XAML e só informa (não é botão, não diz 'toque para abrir')");
        }
    }
}
