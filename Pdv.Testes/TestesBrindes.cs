using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// BRINDE DA RASPADINHA NO CAIXA (21/09/2026).
///
/// Pedido do dono: "cliente ganha um cookie clássico, promoção é validada, cliente escolhe qual
/// quer, funcionário coloca no PDV e ele não emite NF pois é promoção, mas abate do estoque". E o
/// validador no TOPO da aba Promoções, que também precisava de arrumação: o UniformGrid dava a
/// toda seção a altura da maior (vão de 180 px) e o card do desconto de funcionário sem item
/// ficava enorme.
///
/// O que esta suíte prova:
///  1. o código é normalizado (com ou sem "AD-", minúsculas, espaços);
///  2. a leitura das respostas de conferir e entregar, com todos os erros tipados;
///  3. o prêmio vira o combo do diálogo e as escolhas viram produto real (nunca o nome do prêmio);
///  4. todo texto novo tem uma linha curta, no máximo 4, sem travessão, e nunca diz "entregue"
///     numa resposta de erro;
///  5. contra o Supabase de mentira: a sessão do terminal (não a chave pública), a client_key
///     gravada ANTES da chamada, o "Tentar de novo" com a MESMA chave, o homologação que bloqueia;
///  6. a fila tri-estado: ok/idempotente sai como enviado, sem rede é transitório, 404 espera, e a
///     fila NUNCA cria brinde (a primeira chamada que não chegou não é reenviada);
///  7. a config da loja (raspadinha_no_caixa) desce pelo pdv_loja_config_caixa;
///  8. a tela de verdade: o cartão no topo, FORA da grade; a altura da aba antes e depois; o
///     brinde não mexe na comanda nem no rascunho; homologação trava o cartão;
///  9. pelo fonte: o brinde não conhece venda, nota nem comanda.
/// </summary>
public static class TestesBrindes
{
    private const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;
    private const int PortaNucleo = 4721;
    private const int PortaFila = 4722;
    private const int PortaTela = 4723;

    private const string NewYork = "11111111-0000-4000-8000-000000000001";
    private const string Tradicional = "11111111-0000-4000-8000-000000000002";
    private const string Triplo = "11111111-0000-4000-8000-000000000003";
    private const string Brownie = "11111111-0000-4000-8000-000000000040";
    private const string BrownieNutella = "11111111-0000-4000-8000-000000000215";

    private const string CodCookie = "AD-CK7NY2";
    private const string CodDuo = "AD-DUXBR2";

    private const string ConferirCookie = """
        {"ok":true,"codigo":"AD-CK7NY2","premio":"1 Cookie Clássico","premio_emoji":"🍪","cliente":"MARIA DA SILVA",
         "valido_ate":"2026-10-05",
         "regras":[{"id":"r-cookie","descricao":"Cookie clássico","quantidade":1,"opcoes":[
           {"pdv_product_id":"11111111-0000-4000-8000-000000000001","nome":"COOKIE NEW YORK","preco":12.0},
           {"pdv_product_id":"11111111-0000-4000-8000-000000000002","nome":"COOKIE TRADICIONAL","preco":"11.00"},
           {"pdv_product_id":"11111111-0000-4000-8000-000000000003","nome":"COOKIE TRIPLO CHOCOLATE","preco":12}]}]}
        """;

    private const string ConferirDuo = """
        {"ok":true,"codigo":"AD-DUXBR2","premio":"Duo de Brownie","premio_emoji":"🍫","cliente":"joão",
         "valido_ate":"2026-10-10T23:59:59-03:00",
         "regras":[
           {"id":"r-b1","descricao":"Brownie","quantidade":1,"opcoes":[{"pdv_product_id":"11111111-0000-4000-8000-000000000040","nome":"BROWNIE AMERICAN DAY"}]},
           {"id":"r-b2","descricao":"Brownie Nutella","quantidade":1,"opcoes":[{"pdv_product_id":"11111111-0000-4000-8000-000000000215","nome":"BROWNIE NUTELLA"}]}]}
        """;

    private const string ConferirDonuts = """
        {"ok":true,"codigo":"AD-DNT2XY","premio":"Caixa com 2 Donuts","regras":[
           {"id":"r-d","descricao":"Donuts clássicos","quantidade":2,"opcoes":[
             {"pdv_product_id":"d-1","nome":"DONUT NINHO"},{"pdv_product_id":"d-2","nome":"DONUT OVOMALTINE"},
             {"pdv_product_id":"d-3","nome":"DONUT BRIGADEIRO"}]}]}
        """;

    private static bool UmaLinha(string s, int max = 90)
        => s.Length > 0 && s.Length <= max && !s.Contains('\n') && !s.Contains('—') && !s.Contains('–');

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        Normalizacao(checar);
        Leitura(checar);
        ComboEItens(checar);
        Textos(checar);
        ConfigDaLoja(checar);
        Colunas(checar);
        await Nucleo(checar);
        await Fila(checar);
        await Tela(checar);
        Fonte(checar);
    }

    // ── 1. O CÓDIGO ──────────────────────────────────────────────────────────
    private static void Normalizacao(Action<bool, string> checar)
    {
        var casos = new (string? Entrada, string? Esperado)[]
        {
            ("AD-YJM5AB", "AD-YJM5AB"), ("ad-yjm5ab", "AD-YJM5AB"), ("AD YJM5AB", "AD-YJM5AB"),
            (" ad yjm 5ab ", "AD-YJM5AB"), ("yjm5ab", "AD-YJM5AB"), ("adyjm5ab", "AD-YJM5AB"),
            ("ad_yjm5ab", "AD-YJM5AB"), ("AD--YJM5AB", "AD-YJM5AB"),
            // sufixo de 6 que começa com AD: é o sufixo, não o prefixo (o gerador faz 6)
            ("ADXY23", "AD-ADXY23"), ("AD-ADXY23", "AD-ADXY23"),
            // código antigo de 5, como o servidor aceita
            ("yjm5a", "AD-YJM5A"),
            (null, null), ("", null), ("   ", null), ("abc", null), ("AD-", null), ("AD-123456789", null), ("---", null),
        };
        var erradas = casos.Where(c => Brindes.NormalizarCodigo(c.Entrada) != c.Esperado)
            .Select(c => $"'{c.Entrada}'→'{Brindes.NormalizarCodigo(c.Entrada)}' (esperado '{c.Esperado}')").ToList();
        checar(erradas.Count == 0,
            $"CD-1 código com ou sem AD-, minúsculas, espaços e pontuação vira AD-XXXXXX; curto ou comprido demais vira nulo ({string.Join("; ", erradas)})");
    }

    // ── 2. LEITURA DAS RESPOSTAS ─────────────────────────────────────────────
    private static void Leitura(Action<bool, string> checar)
    {
        var c = Brindes.LerConferencia(200, ConferirCookie);
        checar(c.Ok && c.Erro == ErroBrinde.Nenhum && c.Codigo == CodCookie && c.Premio == "1 Cookie Clássico"
               && c.PremioEmoji == "🍪" && c.Cliente == "Maria" && c.ValidoAte == new DateTime(2026, 10, 5),
            $"LR-1 conferir ok: código, prêmio, emoji, só o primeiro nome do cliente e a validade ({c.Cliente}, {c.ValidoAte:dd/MM})");
        checar(c.Regras.Count == 1 && c.Regras[0] is { Id: "r-cookie", Quantidade: 1 }
               && c.Regras[0].Opcoes.Select(o => o.PdvProductId).SequenceEqual(new[] { NewYork, Tradicional, Triplo })
               && c.Regras[0].Opcoes[1].Preco == 11.00m && c.Regras[0].Opcoes[2].Preco == 12m,
            "LR-2 as regras chegam com as opções (pdv_product_id, nome, preço em número ou texto)");
        var duo = Brindes.LerConferencia(200, ConferirDuo);
        checar(duo.Ok && duo.Cliente == "João" && duo.ValidoAte == DateTimeOffset.Parse("2026-10-10T23:59:59-03:00").LocalDateTime
               && duo.Regras.Count == 2,
            $"LR-3 validade com fuso vira a data local e o nome ganha maiúscula ({duo.Cliente}, {duo.ValidoAte})");

        var erros = new (string Json, ErroBrinde Esperado)[]
        {
            ("""{"ok":false,"erro":"formato_invalido"}""", ErroBrinde.FormatoInvalido),
            ("""{"ok":false,"error":"invalid_format"}""", ErroBrinde.FormatoInvalido),
            ("""{"ok":false,"erro":"not_found"}""", ErroBrinde.NaoAchou),
            ("""{"ok":false,"erro":"not_scratched"}""", ErroBrinde.NaoRaspou),
            ("""{"ok":false,"erro":"expired","valido_ate":"2026-10-05"}""", ErroBrinde.Venceu),
            ("""{"ok":false,"erro":"already_redeemed","quando":"2026-09-20T14:17:00-03:00","por":"brenno"}""", ErroBrinde.JaUsado),
            ("""{"ok":false,"erro":"premio_sem_produtos"}""", ErroBrinde.PremioSemProdutos),
            ("""{"ok":false,"erro":"loja_sem_raspadinha"}""", ErroBrinde.LojaSemRaspadinha),
            ("""{"ok":false,"erro":"sem_permissao"}""", ErroBrinde.SemPermissao),
            ("""{"ok":false,"erro":"homologacao"}""", ErroBrinde.Homologacao),
            ("""{"ok":false,"erro":"coisa_nova"}""", ErroBrinde.Desconhecido),
        };
        var lidos = erros.Select(e => (e, Brindes.LerConferencia(200, e.Json))).ToList();
        checar(lidos.All(x => !x.Item2.Ok && x.Item2.Erro == x.e.Esperado),
            $"LR-4 cada erro de conferir vira o seu tipo ({string.Join(", ", lidos.Where(x => x.Item2.Erro != x.e.Esperado).Select(x => x.e.Json))})");
        var usado = lidos.First(x => x.e.Esperado == ErroBrinde.JaUsado).Item2;
        var quando = DateTimeOffset.Parse("2026-09-20T14:17:00-03:00").LocalDateTime;   // a tela mostra a hora local
        checar(usado.UsadoEm == quando && usado.UsadoPor == "brenno"
               && Brindes.TextoDaConferencia(usado) == $"Este código já foi usado em {quando:dd/MM} às {quando:HH:mm} por Brenno.",
            $"LR-5 já usado traz quando e por quem, e a tela diz os dois ('{Brindes.TextoDaConferencia(usado)}')");
        // 21/09 (revisão): o 'por' do SQL 37 vem de raspadinha_validate_code, que PREFERE o e-mail de
        // auth.users quando redeemed_by está preenchido (resgate pelo ERP, raspadinha_redeem). E-mail
        // de funcionário não aparece no balcão: a linha fica sem o "por".
        var porEmail = Brindes.LerConferencia(200, """{"ok":false,"erro":"already_redeemed","quando":"2026-09-20T14:17:00-03:00","por":"gerente.loja@gmail.com"}""");
        checar(Brindes.TextoDaConferencia(porEmail) == $"Este código já foi usado em {quando:dd/MM} às {quando:HH:mm}.",
            $"LR-5b já usado por quem resgatou no ERP (o servidor manda o e-mail): o balcão não mostra e-mail ('{Brindes.TextoDaConferencia(porEmail)}')");
        var venceu = lidos.First(x => x.e.Esperado == ErroBrinde.Venceu).Item2;
        checar(Brindes.TextoDaConferencia(venceu) == "Este código venceu em 05/10.",
            $"LR-6 vencido diz a data ('{Brindes.TextoDaConferencia(venceu)}')");

        var semRegra = Brindes.LerConferencia(200, """{"ok":true,"codigo":"AD-X2345Y","premio":"Brinde","regras":[]}""");
        var regraVazia = Brindes.LerConferencia(200, """{"ok":true,"codigo":"AD-X2345Y","regras":[{"id":"r","descricao":"x","quantidade":1,"opcoes":[]}]}""");
        checar(!semRegra.Ok && semRegra.Erro == ErroBrinde.PremioSemProdutos && !regraVazia.Ok && regraVazia.Erro == ErroBrinde.PremioSemProdutos,
            "LR-7 ok sem regra, ou regra sem opção: 'prêmio sem produtos' (não há o que escolher)");
        checar(Brindes.LerConferencia(-1, "").Erro == ErroBrinde.SemRede && Brindes.LerConferencia(0, "").Erro == ErroBrinde.SemRede
               && Brindes.LerConferencia(503, "x").Erro == ErroBrinde.SemRede
               && Brindes.LerConferencia(404, """{"code":"PGRST202"}""").Erro == ErroBrinde.NuvemSemRecurso
               && Brindes.LerConferencia(401, "").Erro == ErroBrinde.SemPermissao
               && Brindes.LerConferencia(200, "nao e json").Erro == ErroBrinde.Desconhecido,
            "LR-8 sem sessão, sem resposta e 5xx: sem rede; 404 PGRST202: a nuvem não tem; 401: sem permissão; lixo: desconhecido");

        // as respostas no formato EXATO do SQL 37 (aplicar-hoje/37, 21/09): 'erro' nulo no ok,
        // 'venceu_em' na recusa por validade, 'quando' e 'por' no já usado, 'mensagem' sempre
        var sql37Ok = Brindes.LerConferencia(200, """
            {"ok":true,"erro":null,"codigo":"AD-CK7NY2","premio":"Cookie Clássico","premio_emoji":"🍪","cliente":"Maria",
             "valido_ate":"2026-10-05T23:59:59.999-03:00","loja":"American Day Savassi",
             "regras":[{"id":"6f0c1d7e-0000-4000-8000-00000000a001","descricao":"Cookie clássico","quantidade":1,
               "opcoes":[{"pdv_product_id":"11111111-0000-4000-8000-000000000001","nome":"COOKIE NEW YORK","preco":12.00}]}]}
            """);
        var venceuEm = DateTimeOffset.Parse("2026-10-05T23:59:59-03:00").LocalDateTime;
        var sql37Venceu = Brindes.LerConferencia(200, """
            {"ok":false,"erro":"expired","mensagem":"Este código venceu em 05/10.","codigo":"AD-CK7NY2","venceu_em":"2026-10-05T23:59:59-03:00"}
            """);
        var sql37Usado = Brindes.LerConferencia(200, """
            {"ok":false,"erro":"already_redeemed","mensagem":"Este código já foi usado em 20/09 às 14:17.","codigo":"AD-CK7NY2",
             "quando":"2026-09-20T17:17:00+00:00","por":"Brenno"}
            """);
        var usadoEm = DateTimeOffset.Parse("2026-09-20T17:17:00+00:00").LocalDateTime;
        checar(sql37Ok.Ok && sql37Ok.Erro == ErroBrinde.Nenhum && sql37Ok.Regras.Single().Opcoes.Single().PdvProductId == NewYork
               && sql37Venceu.Erro == ErroBrinde.Venceu && sql37Venceu.ValidoAte == venceuEm
               && Brindes.TextoDaConferencia(sql37Venceu) == $"Este código venceu em {venceuEm:dd/MM}."
               && sql37Usado.Erro == ErroBrinde.JaUsado && sql37Usado.UsadoEm == usadoEm && sql37Usado.UsadoPor == "Brenno",
            "LR-11 o formato do SQL 37: ok com 'erro' nulo, 'venceu_em' na validade, 'quando' e 'por' no já usado");
        var emUso = Brindes.LerEntrega(200, """{"ok":false,"erro":"client_key_em_uso","mensagem":"Não deu para confirmar. Não entregue ainda. Tente de novo."}""", "k");
        var novoErro = Brindes.LerConferencia(200, """{"ok":false,"erro":"coisa_de_amanha","mensagem":"Linha nova do servidor."}""");
        var comTravessao = Brindes.LerConferencia(200, """{"ok":false,"erro":"coisa_de_amanha","mensagem":"Linha — com travessão"}""");
        var dizEntregue = Brindes.LerEntrega(200, """{"ok":false,"erro":"coisa_de_amanha","mensagem":"Brinde entregue."}""", "k");
        checar(emUso is { Desfecho: DesfechoBrinde.Recusado, Erro: ErroBrinde.Desconhecido }
               && Brindes.TextoDaEntrega(emUso) == "Não deu para confirmar. Não entregue ainda. Tente de novo."
               && Brindes.TextoDaConferencia(novoErro) == "Linha nova do servidor."
               && Brindes.TextoDaConferencia(comTravessao) == "Não deu para conferir agora. Tente de novo."
               && Brindes.TextoDaEntrega(dizEntregue) == "O painel recusou o brinde. Não entregue.",
            "LR-12 erro que o caixa não conhece: a linha do servidor, menos com travessão ou dizendo 'entregue' numa recusa");
        var cancelado = Brindes.LerEntrega(200, """{"ok":true,"erro":null,"idempotente":true,"brinde_id":"b-9","status":"cancelado","falhas":[]}""", "k");
        var sql37Entregue = Brindes.LerEntrega(200, """
            {"ok":true,"erro":null,"idempotente":false,"brinde_id":"b-10","movimentos":3,"custo":1.23,"custo_incompleto":false,
             "falhas":[],"mensagem":"Brinde entregue. Não passe na comanda."}
            """, "k");
        checar(cancelado is { Desfecho: DesfechoBrinde.Recusado, Erro: ErroBrinde.Cancelado }
               && Brindes.TextoDaEntrega(cancelado) == "Este brinde foi cancelado no painel. Não entregue."
               && sql37Entregue is { Desfecho: DesfechoBrinde.Entregue, BrindeId: "b-10", Falhas: 0, Idempotente: false },
            "LR-13 a mesma chave devolvendo um brinde CANCELADO no painel: não entregue; a entrega do SQL 37 é lida inteira");

        // entregar
        var nova = Brindes.LerEntrega(200, """{"ok":true,"brinde_id":"b-1","idempotente":false,"falhas":2}""", "k");
        var repetida = Brindes.LerEntrega(200, """{"ok":true,"brinde_id":"b-1","idempotente":true,"falhas":[{"produto":"x"}]}""", "k");
        checar(nova is { Desfecho: DesfechoBrinde.Entregue, BrindeId: "b-1", Idempotente: false, Falhas: 2 }
               && repetida is { Desfecho: DesfechoBrinde.Entregue, Idempotente: true, Falhas: 1 },
            "LR-9 entregar ok: novo ou idempotente, com o brinde_id e as falhas de estoque (número ou lista)");
        var desfechos = new (int St, string Corpo, DesfechoBrinde D, ErroBrinde E)[]
        {
            (200, """{"ok":false,"erro":"already_redeemed"}""", DesfechoBrinde.Recusado, ErroBrinde.JaUsado),
            (200, """{"ok":false,"erro":"itens_fora_da_regra"}""", DesfechoBrinde.Recusado, ErroBrinde.ItensForaDaRegra),
            (200, """{"ok":false,"erro":"quantidade_errada"}""", DesfechoBrinde.Recusado, ErroBrinde.QuantidadeErrada),
            (200, """{"ok":false,"erro":"homologacao"}""", DesfechoBrinde.Recusado, ErroBrinde.Homologacao),
            (-1, "", DesfechoBrinde.NaoEnviado, ErroBrinde.SemRede),
            (404, """{"code":"PGRST202"}""", DesfechoBrinde.NaoEnviado, ErroBrinde.NuvemSemRecurso),
            (401, "", DesfechoBrinde.NaoEnviado, ErroBrinde.SemPermissao),
            (0, "", DesfechoBrinde.Incerto, ErroBrinde.NaoConfirmou),
            (502, "bad gateway", DesfechoBrinde.Incerto, ErroBrinde.NaoConfirmou),
            (429, "", DesfechoBrinde.Incerto, ErroBrinde.NaoConfirmou),
            (200, "nao e json", DesfechoBrinde.Incerto, ErroBrinde.NaoConfirmou),
            (400, """{"code":"22P02"}""", DesfechoBrinde.Recusado, ErroBrinde.Desconhecido),
        };
        var errados = desfechos.Select(d => (d, r: Brindes.LerEntrega(d.St, d.Corpo, "k")))
            .Where(x => x.r.Desfecho != x.d.D || x.r.Erro != x.d.E).Select(x => $"{x.d.St}:{x.d.Corpo}→{x.r.Desfecho}/{x.r.Erro}").ToList();
        checar(errados.Count == 0,
            $"LR-10 entregar: recusa é recusa; sem sessão, 401 e 404 é 'não saiu'; sem resposta, 5xx, 429 e lixo é incerto ({string.Join("; ", errados)})");
    }

    // ── 3. O PRÊMIO COMO COMBO; AS ESCOLHAS COMO PRODUTO REAL ────────────────
    private static void ComboEItens(Action<bool, string> checar)
    {
        var c = Brindes.LerConferencia(200, ConferirCookie);
        var def = Brindes.ParaCombo(c);
        checar(def.Nome == "1 Cookie Clássico" && def.Grupos.Count == 1 && def.Grupos[0] is { Id: "r-cookie", Min: 1, Max: 1 }
               && def.Grupos[0].Fonte.Tipo == "itens" && def.Grupos[0].Fonte.Itens.Count == 3 && !def.PorTotal,
            "CB-1 o prêmio vira combo: uma regra = um grupo, mínimo e máximo iguais à quantidade, lista fixa de opções");
        var estado = new Combos.Estado(def, null, Array.Empty<Combos.ProdutoLocal>());
        var g = def.Grupos[0];
        var ny = g.Fonte.Itens.First(i => i.ProdutoId == NewYork);
        var antes = estado.Completo;
        estado.Mais(g, ny);
        var segundo = estado.Mais(g, g.Fonte.Itens[1]);
        checar(!antes && estado.Completo && !segundo && estado.Faltam is null,
            "CB-2 o diálogo só completa com a quantidade certa e não deixa passar dela");
        var itens = Brindes.ItensDe(estado.Escolhas());
        checar(itens.Count == 1 && itens[0] is { PdvProductId: NewYork, Qtd: 1, RegraId: "r-cookie" }
               && itens.All(i => i.PdvProductId != c.Premio && Guid.TryParse(i.PdvProductId, out _)),
            "CB-3 a escolha vira item com o PRODUTO REAL (pdv_product_id), a quantidade e a regra; nunca o nome do prêmio");
        checar(Brindes.ConferirItens(c, itens) == ErroBrinde.Nenhum
               && Brindes.ConferirItens(c, new[] { new ItemBrinde(Brownie, 1, "r-cookie") }) == ErroBrinde.ItensForaDaRegra
               && Brindes.ConferirItens(c, new[] { new ItemBrinde(NewYork, 2, "r-cookie") }) == ErroBrinde.QuantidadeErrada
               && Brindes.ConferirItens(c, new[] { new ItemBrinde(NewYork, 1, "r-outra") }) == ErroBrinde.ItensForaDaRegra
               && Brindes.ConferirItens(c, Array.Empty<ItemBrinde>()) == ErroBrinde.QuantidadeErrada,
            "CB-4 o caixa confere antes de mandar: produto fora da regra, quantidade acima e lista vazia são recusados");

        var duo = Brindes.LerConferencia(200, ConferirDuo);
        var fixas = Brindes.EscolhasFixas(duo);
        checar(fixas is { Count: 2 } && Brindes.ItensDe(fixas).Select(i => (i.PdvProductId, i.RegraId))
                   .SequenceEqual(new (string, string?)[] { (Brownie, "r-b1"), (BrownieNutella, "r-b2") })
               && Brindes.EscolhasFixas(c) is null,
            "CB-5 'Duo de Brownie' (uma opção por regra) não pede escolha; o cookie clássico pede");
        var donuts = Brindes.LerConferencia(200, ConferirDonuts);
        var dd = Brindes.ParaCombo(donuts);
        var ed = new Combos.Estado(dd, null, Array.Empty<Combos.ProdutoLocal>());
        ed.Mais(dd.Grupos[0], dd.Grupos[0].Fonte.Itens[0]);
        var faltaUm = ed.Completo;
        ed.Mais(dd.Grupos[0], dd.Grupos[0].Fonte.Itens[0]);
        var itensD = Brindes.ItensDe(ed.Escolhas());
        checar(!faltaUm && ed.Completo && itensD.Count == 1 && itensD[0].Qtd == 2
               && Brindes.PerguntaDeEntrega(ed.Escolhas()) == "Entregar 2 DONUT NINHO de brinde? O código deixa de valer.",
            $"CB-6 'Caixa com 2 Donuts' pede 2, e dois iguais viram um item com qtd 2 ('{Brindes.PerguntaDeEntrega(ed.Escolhas())}')");
    }

    // ── 4. TEXTOS ────────────────────────────────────────────────────────────
    private static void Textos(Action<bool, string> checar)
    {
        var c = Brindes.LerConferencia(200, ConferirCookie);
        var escolha = new List<Escolha> { new(NewYork, null, "COOKIE NEW YORK", "r-cookie", 1, "Cookie clássico") };
        checar(Brindes.PerguntaDeEntrega(escolha) == "Entregar 1 COOKIE NEW YORK de brinde? O código deixa de valer.",
            "TX-1 a pergunta de uma linha: 'Entregar 1 COOKIE NEW YORK de brinde? O código deixa de valer.'");
        checar(Brindes.LinhaDoPremio(c) == "🍪 1 Cookie Clássico · Maria · vale até 05/10",
            $"TX-2 a linha do prêmio: '{Brindes.LinhaDoPremio(c)}'");
        var duo = Brindes.EscolhasFixas(Brindes.LerConferencia(200, ConferirDuo))!;
        var tres = duo.Append(new Escolha(Triplo, null, "COOKIE TRIPLO CHOCOLATE", "x", 1)).ToList();
        checar(Brindes.PerguntaDeEntrega(duo) == "Entregar 1 BROWNIE AMERICAN DAY e 1 BROWNIE NUTELLA de brinde? O código deixa de valer."
               && Brindes.Resumo(tres) == "1 BROWNIE AMERICAN DAY, 1 BROWNIE NUTELLA e 1 COOKIE TRIPLO CHOCOLATE",
            "TX-3 dois e três itens: 'A e B', 'A, B e C'");

        var todos = new List<(string Nome, string Texto)>();
        foreach (var e in Enum.GetValues<ErroBrinde>())
        {
            var conf = new ConferenciaBrinde(e == ErroBrinde.Nenhum, e, null, CodCookie, "1 Cookie Clássico", "🍪", "Maria",
                new DateTime(2026, 10, 5), c.Regras, new DateTime(2026, 9, 20, 14, 17, 0), "Brenno Souza");
            todos.Add(("conferir " + e, Brindes.TextoDaConferencia(conf)));
            foreach (var d in Enum.GetValues<DesfechoBrinde>())
                todos.Add(($"entrega {d}/{e}", Brindes.TextoDaEntrega(new EntregaBrinde(d, e, null, "k", null, false, 0))));
        }
        todos.Add(("pendente", Brindes.TextoPendente("1 COOKIE NEW YORK")));
        todos.Add(("pendente sem resumo", Brindes.TextoPendente(null)));
        todos.Add(("pergunta 3", Brindes.PerguntaDeEntrega(tres)));
        todos.Add(("linha", Brindes.LinhaDoPremio(c)));
        todos.Add(("sem código", Brindes.TextoSemCodigo));
        todos.Add(("entregue", Brindes.TextoEntregue));
        var ruins = todos.Where(t => !UmaLinha(t.Texto, t.Nome.StartsWith("pergunta") ? 130 : 90)).ToList();
        checar(ruins.Count == 0,
            $"TX-4 todo texto do brinde é uma linha curta, sem travessão ({todos.Count} textos; ruins: {string.Join(" | ", ruins.Select(r => r.Nome + ": " + r.Texto))})");
        checar(todos.All(t => t.Texto.Split('\n').Length <= 4), "TX-5 nenhum passa de 4 linhas (teto do caixa)");

        var mentiras = Enum.GetValues<DesfechoBrinde>().Where(d => d != DesfechoBrinde.Entregue)
            .SelectMany(d => Enum.GetValues<ErroBrinde>().Select(e => Brindes.TextoDaEntrega(new EntregaBrinde(d, e, null, "k", null, false, 0))))
            .Where(t => t.Contains("entregue.", StringComparison.OrdinalIgnoreCase) && !t.Contains("Não entregue", StringComparison.Ordinal))
            .ToList();
        var dizemNaoEntregue = Enum.GetValues<ErroBrinde>()
            .Select(e => Brindes.TextoDaEntrega(new EntregaBrinde(DesfechoBrinde.Incerto, e, null, "k", null, false, 0)))
            .All(t => t == Brindes.TextoIncerto && t.Contains("Não entregue ainda", StringComparison.Ordinal));
        checar(mentiras.Count == 0 && dizemNaoEntregue && Brindes.TextoIncerto == "Não deu para confirmar. Não entregue ainda. Tente de novo."
               && Brindes.TextoDaEntrega(new EntregaBrinde(DesfechoBrinde.NaoEnviado, ErroBrinde.SemRede, null, "k", null, false, 0)) == "Sem internet. Não entregue."
               && Brindes.TextoDaEntrega(new EntregaBrinde(DesfechoBrinde.Recusado, ErroBrinde.JaUsado, null, "k", null, false, 0)) == "Este código acabou de ser usado. Não entregue.",
            "TX-6 nunca 'entregue' numa resposta de erro: sem resposta diz 'Não entregue ainda', sem rede diz 'Não entregue'");
        checar(Brindes.TextoDaConferencia(new ConferenciaBrinde(false, ErroBrinde.NaoAchou, null, null, null, null, null, null,
                   Array.Empty<RegraBrinde>())).Contains("cortesia", StringComparison.OrdinalIgnoreCase),
            "TX-7 não achou: aponta o botão da cortesia (os dois códigos são AD-XXXXXX)");
    }

    // ── 7. A CONFIG DA LOJA ──────────────────────────────────────────────────
    private static void ConfigDaLoja(Action<bool, string> checar)
    {
        var linhas = ConfigLojaPainel.Ler("""
            [{"store":"American Day Savassi","raspadinha_no_caixa":true},
             {"store":"American Day Castelo","raspadinha_no_caixa":false},
             {"store":"Centro"}]
            """);
        checar(linhas.Count == 3 && linhas[0].RaspadinhaNoCaixa == true && linhas[1].RaspadinhaNoCaixa == false
               && linhas[2].RaspadinhaNoCaixa is null,
            "CF-1 pdv_loja_config_caixa: raspadinha_no_caixa verdadeiro, falso ou ausente (servidor antigo)");
        var db = Path.Combine(Path.GetTempPath(), "brinde-config-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        try
        {
            Banco.Migrar(db);
            using var cx = Banco.Abrir(db);
            var agora = new DateTime(2026, 9, 21, 9, 0, 0);
            var liga = ConfigLojaPainel.Aplicar(cx, linhas[0], agora);
            var ligado = Brindes.LigadoNaLoja(cx);
            var igual = ConfigLojaPainel.Aplicar(cx, linhas[0], agora);
            var desliga = ConfigLojaPainel.Aplicar(cx, linhas[0] with { RaspadinhaNoCaixa = null }, agora);
            checar(liga.Contains("raspadinha no caixa") && ligado && igual == "" && desliga.Contains("raspadinha no caixa")
                   && !Brindes.LigadoNaLoja(cx),
                $"CF-2 ligar grava a config, repetir não muda nada, ausente desliga (o painel é a verdade) ({liga} / {desliga})");
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(db); } catch { } }
    }

    // ── A REGRA DAS COLUNAS (pura) ───────────────────────────────────────────
    private static void Colunas(Action<bool, string> checar)
    {
        checar(ColunasPorAltura.Distribuir(new double[] { 100, 100, 100 }, 3).SequenceEqual(new[] { 0, 1, 2 })
               && ColunasPorAltura.Distribuir(new double[] { 400, 70, 70, 70 }, 2).SequenceEqual(new[] { 0, 1, 1, 1 })
               && ColunasPorAltura.Distribuir(new double[] { 50, 50 }, 1).SequenceEqual(new[] { 0, 0 })
               && ColunasPorAltura.Distribuir(Array.Empty<double>(), 3).Length == 0,
            "CL-1 cada bloco vai para a coluna mais baixa até ali (empate: a da esquerda)");
        var alturas = new double[] { 64, 430, 250, 250, 430, 250, 250, 250, 250, 430, 250, 250, 250, 250 };
        checar(ColunasPorAltura.AlturaEmColunas(alturas, 1) == alturas.Sum()
               && ColunasPorAltura.AlturaEmGradeUniforme(alturas, 1) == alturas.Length * 430
               && ColunasPorAltura.AlturaEmColunas(alturas, 3) < ColunasPorAltura.AlturaEmGradeUniforme(alturas, 3),
            $"CL-2 com seções de alturas diferentes as colunas somam menos que a grade uniforme (3 col: {ColunasPorAltura.AlturaEmColunas(alturas, 3)} contra {ColunasPorAltura.AlturaEmGradeUniforme(alturas, 3)})");
    }

    // ── 5. CONTRA O SUPABASE DE MENTIRA ──────────────────────────────────────
    private static async Task Nucleo(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"brinde-nucleo-{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir(arquivo);
            SemearTerminal(cx);
            using var fake = new FakePostgrest(PortaNucleo);
            fake.ConferirPorCodigo[CodCookie] = ConferirCookie;
            fake.ConferirPorCodigo[CodDuo] = ConferirDuo;
            var nuvem = new Nuvem(fake.Url);
            checar(await nuvem.EntrarAsync("caixa@savassi", "x"), "NU-0 a nuvem de mentira autentica o terminal");
            var brindes = new Brindes(nuvem);
            var op = new Operador("22222222-0000-4000-8000-000000000007", "Bia", "operador");

            // sem sessão: a chamada nem sai
            var semSessao = await new Brindes(new Nuvem(fake.Url)).ConferirAsync("ck7ny2");
            checar(semSessao.Erro == ErroBrinde.SemRede && fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcConferir) == 0,
                "NU-1 sem sessão do terminal a conferência NÃO sai (nada de cair para a chave pública)");

            var c = await brindes.ConferirAsync("ad ck7ny2");
            checar(c.Ok && c.Codigo == CodCookie && c.Regras.Count == 1,
                "NU-2 conferir manda o código normalizado e lê o prêmio com as regras");
            checar(fake.BearerPorRpc.GetValueOrDefault(Brindes.RpcConferir) == "Bearer tok-fake"
                   && !fake.BearerPorRpc[Brindes.RpcConferir].Contains(Pdv.Nucleo.Nuvem.AnonKey, StringComparison.Ordinal),
                "NU-3 com a sessão do TERMINAL no Bearer, não a chave pública");

            // entrega: gravada ANTES da chamada
            string? situacaoNaHora = null; long filaNaHora = -1;
            fake.AoReceberEntregar = k =>
            {
                using var c2 = Banco.Abrir();
                situacaoNaHora = c2.ExecuteScalar<string?>("SELECT situacao FROM brinde WHERE client_key = @k", new { k });
                filaNaHora = c2.ExecuteScalar<long>("SELECT COUNT(*) FROM outbox WHERE tipo = @t AND client_key = @k", new { t = Brindes.TipoNaFila, k });
            };
            var escolhas = new List<Escolha> { new(NewYork, null, "COOKIE NEW YORK", "r-cookie", 1, "Cookie clássico") };
            var r = await brindes.EntregarAsync(c, escolhas, op, "sessao-1");
            fake.AoReceberEntregar = null;
            checar(r is { Desfecho: DesfechoBrinde.Entregue, Idempotente: false } && r.BrindeId is not null && r.ClientKey is not null,
                $"NU-4 entrega confirmada: {r.Desfecho}, brinde {r.BrindeId}");
            checar(situacaoNaHora == "enviando" && filaNaHora == 1,
                $"NU-5 a client_key já estava gravada no caixa (brinde '{situacaoNaHora}' e {filaNaHora} linha na fila) quando a chamada chegou");
            using (var d = JsonDocument.Parse(fake.CorpoEntregarPorChave[r.ClientKey!]))
            {
                var corpo = d.RootElement;
                var item = corpo.GetProperty("_itens")[0];
                checar(corpo.GetProperty("_client_key").GetString() == r.ClientKey && corpo.GetProperty("_code").GetString() == CodCookie
                       && item.GetProperty("pdv_product_id").GetString() == NewYork && item.GetProperty("qtd").GetInt32() == 1
                       && item.GetProperty("regra_id").GetString() == "r-cookie"
                       && corpo.GetProperty("_operador_id").GetString() == op.Id && corpo.GetProperty("_operador_nome").GetString() == "Bia"
                       && corpo.GetProperty("_terminal_uuid").GetString() == "term-brinde"
                       && !corpo.TryGetProperty("_store", out _) && !corpo.TryGetProperty("store", out _),
                    "NU-6 o corpo leva a chave local, o produto real, a regra, operador e terminal, e nenhuma loja (a loja vem da sessão)");
            }
            var linha = cx.QueryFirst("SELECT situacao, brinde_id, resumo FROM brinde WHERE client_key = @k", new { k = r.ClientKey });
            checar((string)linha.situacao == "entregue" && (string)linha.brinde_id == r.BrindeId && (string)linha.resumo == "1 COOKIE NEW YORK"
                   && cx.ExecuteScalar<long>("SELECT COUNT(*) FROM auditoria WHERE evento IN ('brinde_pedido','brinde_entregue')") == 2,
                "NU-7 a linha local fica 'entregue' com o id da nuvem, e a auditoria tem o pedido e a entrega");
            checar(cx.ExecuteScalar<long>("SELECT COUNT(*) FROM venda") == 0
                   && cx.ExecuteScalar<long>("SELECT COUNT(*) FROM outbox WHERE tipo NOT IN ('brinde_entregar')") == 0
                   && cx.ExecuteScalar<long>("SELECT COUNT(*) FROM comanda_rascunho") == 0,
                "NU-8 nenhuma venda, nenhuma linha de venda na fila, nenhum rascunho: o brinde não é venda");

            var denovo = await brindes.TentarDeNovoAsync(r.ClientKey!);
            checar(denovo.Desfecho == DesfechoBrinde.Entregue && denovo.BrindeId == r.BrindeId
                   && fake.BrindesPorChave.Count == 1,
                "NU-9 tentar de novo com a mesma chave não cria outro brinde");

            var usado = await brindes.EntregarAsync(c, escolhas, op, "sessao-1");
            checar(usado is { Desfecho: DesfechoBrinde.Recusado, Erro: ErroBrinde.JaUsado }
                   && Brindes.TextoDaEntrega(usado) == "Este código acabou de ser usado. Não entregue."
                   && cx.ExecuteScalar<string>("SELECT situacao FROM brinde WHERE client_key = @k", new { k = usado.ClientKey }) == "recusado",
                "NU-10 o mesmo código de novo (chave nova): 'acabou de ser usado. Não entregue.' e a linha fica recusada");

            // resposta perdida: o servidor gravou, o caixa não soube
            var duo = await brindes.ConferirAsync(CodDuo);
            fake.EntregarProcessaEPerde = 1;
            var perdida = await brindes.EntregarAsync(duo, Brindes.EscolhasFixas(duo)!, op, "sessao-1");
            checar(perdida.Desfecho == DesfechoBrinde.Incerto && Brindes.TextoDaEntrega(perdida) == Brindes.TextoIncerto
                   && cx.ExecuteScalar<string>("SELECT situacao FROM brinde WHERE client_key = @k", new { k = perdida.ClientKey }) == "incerto"
                   && Brindes.Pendente(cx, CodDuo)?.ClientKey == perdida.ClientKey,
                "NU-11 resposta perdida: 'Não deu para confirmar. Não entregue ainda.', linha incerta e pendente para o código");
            var retentativa = await brindes.TentarDeNovoAsync(perdida.ClientKey!);
            checar(retentativa is { Desfecho: DesfechoBrinde.Entregue, Idempotente: true }
                   && fake.RaspadinhasUsadas[CodDuo] == perdida.ClientKey && fake.BrindesPorChave.Count == 2
                   && Brindes.Pendente(cx, CodDuo) is null,
                "NU-12 'Tentar de novo' reusa a MESMA chave: o servidor devolve o mesmo brinde (idempotente), sem criar outro");

            // item fora da regra: o caixa nem grava nem chama
            var chamadas = fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcEntregar);
            var linhasAntes = cx.ExecuteScalar<long>("SELECT COUNT(*) FROM brinde");
            var fora = await brindes.EntregarAsync(c with { Codigo = "AD-QQQQQQ" },
                new List<Escolha> { new(Brownie, null, "BROWNIE AMERICAN DAY", "r-cookie", 1) }, op, null);
            checar(fora is { Desfecho: DesfechoBrinde.Recusado, Erro: ErroBrinde.ItensForaDaRegra }
                   && fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcEntregar) == chamadas
                   && cx.ExecuteScalar<long>("SELECT COUNT(*) FROM brinde") == linhasAntes,
                "NU-13 brownie num prêmio de cookie: recusado no caixa, sem gravar e sem chamar");

            // operador criado só no caixa (id que não é uuid): _operador_id nulo, senão o corpo inteiro cai
            fake.ConferirPorCodigo["AD-LCL234"] = ConferirCookie.Replace(CodCookie, "AD-LCL234");
            var cLocal = await brindes.ConferirAsync("AD-LCL234");
            var rLocal = await brindes.EntregarAsync(cLocal, escolhas, new Operador("op-local", "Zé", "operador"), null);
            using (var d = JsonDocument.Parse(fake.CorpoEntregarPorChave[rLocal.ClientKey!]))
                checar(rLocal.Desfecho == DesfechoBrinde.Entregue && d.RootElement.GetProperty("_operador_id").ValueKind == JsonValueKind.Null
                       && d.RootElement.GetProperty("_operador_nome").GetString() == "Zé",
                    "NU-14 operador local (id não uuid): vai o nome e o id nulo");

            // nuvem sem o SQL 37: nada sai, e a tela diz que o painel não tem
            fake.BrindeAusente = true;
            var ausente = await brindes.ConferirAsync("AD-NVX234");
            fake.BrindeAusente = false;
            checar(ausente.Erro == ErroBrinde.NuvemSemRecurso, "NU-15 RPC ausente (404 PGRST202): 'o painel ainda não tem o brinde'");

            // HOMOLOGAÇÃO bloqueia, antes de gravar ou chamar
            Vendas.GravarConfig(cx, "homologacao", "1");
            var confAntes = fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcConferir);
            var entAntes = fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcEntregar);
            var brindesAntes = cx.ExecuteScalar<long>("SELECT COUNT(*) FROM brinde");
            var filaAntes = cx.ExecuteScalar<long>("SELECT COUNT(*) FROM outbox");
            var hc = await brindes.ConferirAsync(CodCookie);
            var he = await brindes.EntregarAsync(c, escolhas, op, null);
            Vendas.GravarConfig(cx, "homologacao", "0");
            checar(hc.Erro == ErroBrinde.Homologacao && he.Desfecho == DesfechoBrinde.Bloqueado
                   && Brindes.TextoDaEntrega(he) == Brindes.TextoHomologacao
                   && fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcConferir) == confAntes
                   && fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcEntregar) == entAntes
                   && cx.ExecuteScalar<long>("SELECT COUNT(*) FROM brinde") == brindesAntes
                   && cx.ExecuteScalar<long>("SELECT COUNT(*) FROM outbox") == filaAntes,
                "NU-16 modo de homologação: conferir e entregar bloqueiam sem gravar nada e sem chamar a nuvem");
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }

    // ── 6. A FILA ────────────────────────────────────────────────────────────
    private static async Task Fila(Action<bool, string> checar)
    {
        checar(Drenagem.TiposComHandler.Contains(Brindes.TipoNaFila), "FL-0 'brinde_entregar' está na lista de tipos com handler");
        var arquivo = Path.Combine(Path.GetTempPath(), $"brinde-fila-{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir(arquivo);
            SemearTerminal(cx);
            using var fake = new FakePostgrest(PortaFila);
            var nuvem = new Nuvem(fake.Url);
            await nuvem.EntrarAsync("caixa@savassi", "x");
            var brindes = new Brindes(nuvem);
            using var dren = new Drenagem(nuvem, fake.Url);
            var op = new Operador("22222222-0000-4000-8000-000000000007", "Bia", "operador");
            var escolhas = new List<Escolha> { new(NewYork, null, "COOKIE NEW YORK", "r-cookie", 1, "Cookie clássico") };

            dynamic LinhaFila(string k) => cx.QueryFirst("SELECT enviado_em, desistido_em, tentativas, ultimo_erro, primeiro_erro_em FROM outbox WHERE client_key = @k ORDER BY id DESC LIMIT 1", new { k });
            string Situacao(string k) => cx.ExecuteScalar<string>("SELECT situacao FROM brinde WHERE client_key = @k", new { k })!;
            async Task<(ConferenciaBrinde C, string Cod)> Novo(string cod)
            {
                fake.ConferirPorCodigo[cod] = ConferirCookie.Replace(CodCookie, cod);
                return (await brindes.ConferirAsync(cod), cod);
            }

            // a) o servidor GRAVOU e a resposta se perdeu: a fila confere (usado) e reenvia a MESMA chave
            var (ca, coda) = await Novo("AD-FLA234");
            fake.EntregarProcessaEPerde = 1;
            var ra = await brindes.EntregarAsync(ca, escolhas, op, null);
            var entregasA = fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcEntregar);
            await dren.DrenarAsync();
            var fa = LinhaFila(ra.ClientKey!);
            checar(ra.Desfecho == DesfechoBrinde.Incerto && fa.enviado_em is string && Situacao(ra.ClientKey!) == "entregue"
                   && cx.ExecuteScalar<string>("SELECT brinde_id FROM brinde WHERE client_key = @k", new { k = ra.ClientKey }) == fake.BrindesPorChave[ra.ClientKey!]
                   && fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcEntregar) == entregasA + 1
                   && fake.BrindesPorChave.Values.Count(v => v == fake.BrindesPorChave[ra.ClientKey!]) == 1,
                "FL-1 resposta perdida com o brinde gravado: a fila reenvia a MESMA chave, recebe o idempotente e sai como enviada");

            // b) a chamada NÃO rodou (caiu antes): a fila confere, vê o código valendo e NÃO reenvia
            var (cb, codb) = await Novo("AD-FLB234");
            fake.EntregarCaiAntes = 1;
            var rb = await brindes.EntregarAsync(cb, escolhas, op, null);
            var entregasB = fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcEntregar);
            await dren.DrenarAsync();
            // 21/09 (revisão): logo depois da resposta perdida a primeira chamada pode AINDA estar
            // rodando no servidor (a rede voltou e cutucou a fila na hora). "Não chegou" só depois
            // do prazo da tela; até lá a linha continua pendente e nada é reenviado.
            var fb0 = LinhaFila(rb.ClientKey!);
            checar(rb.Desfecho == DesfechoBrinde.Incerto && fb0.enviado_em is null && (long)fb0.tentativas == 0
                   && Situacao(rb.ClientKey!) == "incerto" && Brindes.Pendente(cx, codb)?.ClientKey == rb.ClientKey
                   && fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcEntregar) == entregasB,
                $"FL-2a resposta perdida há segundos e código valendo: a fila ainda não diz 'não chegou' ({(string?)fb0.ultimo_erro})");
            cx.Execute("UPDATE brinde SET tentado_em = @t WHERE client_key = @k",
                new { t = DateTime.Now.AddMinutes(-3).ToString("o"), k = rb.ClientKey });
            await dren.DrenarAsync();
            var fb = LinhaFila(rb.ClientKey!);
            checar(rb.Desfecho == DesfechoBrinde.Incerto && fb.enviado_em is string && Situacao(rb.ClientKey!) == "nao_entregue"
                   && fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcEntregar) == entregasB
                   && !fake.BrindesPorChave.ContainsKey(rb.ClientKey!) && !fake.RaspadinhasUsadas.ContainsKey(codb),
                "FL-2 a fila NUNCA cria brinde: passado o prazo, a chamada que não chegou não é reenviada, o código continua valendo e a linha vira 'não entregue'");

            // c) sem rede: transitório (Aguarda), sem contar tentativa; a rede volta e resolve
            var (cc, _) = await Novo("AD-FLC234");
            fake.EntregarProcessaEPerde = 1;
            var rc = await brindes.EntregarAsync(cc, escolhas, op, null);
            using (var morta = new Drenagem(nuvem, Isolamento.UrlMorta))
                for (var i = 0; i < Drenagem.MaxTentativas + 2; i++) await morta.DrenarAsync();
            var fc = LinhaFila(rc.ClientKey!);
            checar(fc.enviado_em is null && fc.desistido_em is null && (long)fc.tentativas == 0 && fc.primeiro_erro_em is string
                   && Situacao(rc.ClientKey!) == "incerto",
                $"FL-3 sem rede é transitório: 14 varreduras sem resposta não contam tentativa nem desistem ({(string?)fc.ultimo_erro})");
            fake.PctErro503 = 100;
            await dren.DrenarAsync();
            var fc503 = LinhaFila(rc.ClientKey!);
            fake.PctErro503 = 0;
            await dren.DrenarAsync();
            var fc2 = LinhaFila(rc.ClientKey!);
            checar(fc503.enviado_em is null && (long)fc503.tentativas == 0 && fc2.enviado_em is string && Situacao(rc.ClientKey!) == "entregue",
                "FL-4 503 também espera; a rede volta e a mesma chave confirma o brinde");

            // d) 404 PGRST202: espera a migration, nunca vira dead-letter
            var (cd, _) = await Novo("AD-FLD234");
            fake.EntregarProcessaEPerde = 1;
            var rd = await brindes.EntregarAsync(cd, escolhas, op, null);
            fake.BrindeAusente = true;
            for (var i = 0; i < Drenagem.MaxTentativas + 2; i++) await dren.DrenarAsync();
            var fd = LinhaFila(rd.ClientKey!);
            fake.BrindeAusente = false;
            checar(fd.enviado_em is null && fd.desistido_em is null && (long)fd.tentativas == 0
                   && ((string?)fd.ultimo_erro ?? "").Contains("migration", StringComparison.Ordinal),
                $"FL-5 404 PGRST202 espera a migration: 14 varreduras sem dead-letter ({(string?)fd.ultimo_erro})");
            await dren.DrenarAsync();
            checar(LinhaFila(rd.ClientKey!).enviado_em is string && Situacao(rd.ClientKey!) == "entregue",
                "FL-6 a migration chega e a fila resolve na varredura seguinte");

            // e) linha que a tela ainda está enviando: espera, sem chamar; linha já resolvida: sai sem chamar
            var chave = Guid.NewGuid().ToString();
            var agora = DateTime.Now.ToString("o");
            cx.Execute("""
                INSERT INTO brinde (client_key, codigo, payload, situacao, criado_em, tentado_em)
                VALUES (@k, 'AD-FLE234', '{"_code":"AD-FLE234","_itens":[]}', 'enviando', @a, @a)
                """, new { k = chave, a = agora });
            Caixa.Enfileirar(cx, null, Brindes.TipoNaFila, chave, chave, new { codigo = "AD-FLE234" });
            var conferirAntes = fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcConferir);
            await dren.DrenarAsync();
            var fe = LinhaFila(chave);
            checar(fe.enviado_em is null && (long)fe.tentativas == 0 && fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcConferir) == conferirAntes,
                "FL-7 linha que a tela ainda está enviando: a fila espera e não chama nada");
            cx.Execute("UPDATE brinde SET situacao = 'entregue' WHERE client_key = @k", new { k = chave });
            await dren.DrenarAsync();
            checar(LinhaFila(chave).enviado_em is string && fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcConferir) == conferirAntes,
                "FL-8 linha que a tela já resolveu sai da fila sem chamada");

            // f) caixa caiu no meio (linha 'enviando' velha): a fila assume, sem criar
            var velha = Guid.NewGuid().ToString();
            var ha10 = DateTime.Now.AddMinutes(-10).ToString("o");
            fake.ConferirPorCodigo["AD-FLF234"] = ConferirCookie.Replace(CodCookie, "AD-FLF234");
            cx.Execute("""
                INSERT INTO brinde (client_key, codigo, payload, situacao, criado_em, tentado_em)
                VALUES (@k, 'AD-FLF234', '{"_code":"AD-FLF234","_itens":[]}', 'enviando', @a, @a)
                """, new { k = velha, a = ha10 });
            Caixa.Enfileirar(cx, null, Brindes.TipoNaFila, velha, velha, new { codigo = "AD-FLF234" });
            await dren.DrenarAsync();
            checar(LinhaFila(velha).enviado_em is string && Situacao(velha) == "nao_entregue" && !fake.RaspadinhasUsadas.ContainsKey("AD-FLF234"),
                "FL-9 caixa caiu no meio da chamada (linha parada há 10 min): a fila confere e resolve sem criar brinde");

            // g) 21/09 (revisão): o dono desliga a raspadinha da loja depois de uma resposta perdida.
            // O conferir passa a responder 'loja_sem_raspadinha' ANTES de olhar o código, e a fila não
            // descobria mais nada (desistia com a linha 'incerto' para sempre). O SQL 37 põe a
            // idempotência da entrega antes da chave da loja justamente para isto: a mesma chave
            // devolve o brinde gravado, e com a loja desligada a RPC não tem como criar outro.
            var (cg, _) = await Novo("AD-FLG234");
            fake.EntregarProcessaEPerde = 1;
            var rg = await brindes.EntregarAsync(cg, escolhas, op, null);
            var (ch, codh) = await Novo("AD-FLH234");
            fake.EntregarCaiAntes = 1;
            var rh = await brindes.EntregarAsync(ch, escolhas, op, null);
            cx.Execute("UPDATE brinde SET tentado_em = @t WHERE client_key IN (@a, @b)",
                new { t = DateTime.Now.AddMinutes(-3).ToString("o"), a = rg.ClientKey, b = rh.ClientKey });
            fake.LojaDesligada = true;
            await dren.DrenarAsync();
            fake.LojaDesligada = false;
            checar(LinhaFila(rg.ClientKey!).enviado_em is string && Situacao(rg.ClientKey!) == "entregue",
                $"FL-10 loja desligada depois da resposta perdida, brinde gravado: a mesma chave confirma ('{Situacao(rg.ClientKey!)}')");
            checar(LinhaFila(rh.ClientKey!).enviado_em is string && Situacao(rh.ClientKey!) == "nao_entregue"
                   && !fake.BrindesPorChave.ContainsKey(rh.ClientKey!) && !fake.RaspadinhasUsadas.ContainsKey(codh),
                $"FL-11 loja desligada e a chamada não tinha chegado: nada criado, a linha vira 'não entregue' ('{Situacao(rh.ClientKey!)}')");
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }

    private static void SemearTerminal(SqliteConnection cx)
    {
        cx.Execute("""
            INSERT INTO terminal (id, terminal_uuid, loja_id, loja_nome, cnpj, serie_nfce, ambiente, api_base, criado_em)
            VALUES (1, 'term-brinde', 'loja-1', 'American Day Savassi', '00000000000000', 1, 2, 'http://127.0.0.1:9', @a)
            """, new { a = DateTime.Now.ToString("o") });
        Isolamento.SemNuvem(cx);
    }

    // ── 8. A TELA DE VERDADE ─────────────────────────────────────────────────
    // 13 promoções com 1 a 6 produtos e o desconto de funcionário (card sem produto): o teste SaaS
    // do dono. Mesma promoção com código da suíte TestesPromoComSenhaNaVitrine (payload real).
    private const string IdFunc = "3037fa4b-0518-4879-817e-eb42fdf8331b";
    private const string PayloadFunc = """
        {"id": "3037fa4b-0518-4879-817e-eb42fdf8331b", "fim": null, "alvo": "todos", "leve": null, "nome": "DESCONTO FUNCIONARIO", "tipo": "percentual", "ativa": true, "combo": null, "lojas": ["American Day Savassi"], "pague": null, "store": "American Day Savassi", "config": {"autorizacao": "gerente"}, "inicio": "2026-09-08", "hora_fim": null, "categorias": null, "percentual": 30, "dias_semana": null, "hora_inicio": null, "produto_ids": null, "regras_semana": null, "valor_desconto_cent": null}
        """;
    private static readonly int[] ProdutosPorPromo = { 6, 1, 2, 1, 4, 1, 3, 1, 2, 5, 1, 2, 1 };

    private static async Task Tela(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"brinde-tela-{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        Exception? erro = null;
        try
        {
            Banco.Migrar(arquivo);
            using (var cx = Banco.Abrir(arquivo)) SemearTela(cx);
            using var fake = new FakePostgrest(PortaTela);
            fake.ConferirPorCodigo[CodCookie] = ConferirCookie;
            var nuvem = new Nuvem(fake.Url);
            await nuvem.EntrarAsync("caixa@savassi", "x");
            var brindes = new Brindes(nuvem);
            try { HostWpf.Executar(() => NaTela(checar, fake, brindes)); }
            catch (Exception ex) { erro = ex; }
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
        checar(erro is null, "TL-0 tela: os passos rodaram (" + (erro?.ToString() ?? "ok") + ")");
    }

    private static void SemearTela(SqliteConnection cx)
    {
        var agora = DateTime.Now.ToString("o");
        SemearTerminal(cx);
        var (h, s) = Operadores.GerarHash("4321");
        cx.Execute("INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,ativo,atualizado) VALUES ('op-real','Maria',@H,@S,'operador',1,@a)",
            new { H = h, S = s, a = agora });
        // o turno existe no banco (o rascunho da comanda aponta para ele por chave estrangeira)
        cx.Execute("""
            INSERT INTO caixa_sessao (id, business_date, operador_id, operador_nome, abertura_em, fundo_troco_cent, status)
            VALUES ('sessao-brinde', @d, 'op-real', 'Maria', @a, 30000, 'aberto')
            """, new { d = Caixa.DiaOperacional(), a = agora });
        var n = 0;
        for (var i = 0; i < ProdutosPorPromo.Length; i++)
        {
            var ids = new List<string>();
            for (var j = 0; j < ProdutosPorPromo[i]; j++)
            {
                n++;
                var id = $"p-{n:00}";
                ids.Add(id);
                cx.Execute("""
                    INSERT INTO produto (id, plu, nome, categoria, preco_cent, unidade, ativo, atualizado, csosn)
                    VALUES (@id, @plu, @nome, 'Donuts', 900, 'UN', 1, @a, '102')
                    """, new { id, plu = n.ToString(), nome = $"DONUT SABOR {n:00}", a = agora });
            }
            cx.Execute("INSERT INTO promo (id, payload, atualizado_em) VALUES (@id, @p, @a)", new
            {
                id = $"promo-{i:00}", a = agora,
                p = JsonSerializer.Serialize(new
                {
                    id = $"promo-{i:00}", nome = $"promocao {i + 1:00}", tipo = "percentual", alvo = "produtos",
                    percentual = 10, produto_ids = ids, inicio = "2026-08-01", config = new { },
                }),
            });
        }
        cx.Execute("INSERT INTO promo (id, payload, atualizado_em) VALUES (@Id, @P, @a)", new { Id = IdFunc, P = PayloadFunc.Trim(), a = agora });
        Vendas.GravarConfig(cx, "modo_fiscal", "recibo");
        // a descida de verdade: o painel ligou a raspadinha na loja
        ConfigLojaPainel.Aplicar(cx, new ConfigLojaPainel.Linha("American Day Savassi", null, null, null, null, null, null, null, null, null,
            RaspadinhaNoCaixa: true), DateTime.Now);
    }

    private static void NaTela(Action<bool, string> checar, FakePostgrest fake, Brindes brindes)
    {
        var host = new Window
        {
            Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0,
        };
        host.Show();
        // Rede de segurança: diálogo de verdade que abrir aqui é fechado e contado, em vez de
        // pendurar a suíte num ShowDialog.
        var janelasAbertas = 0;
        var hosts = new List<Window> { host };
        var fecha = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        fecha.Tick += (_, _) =>
        {
            foreach (var d in Application.Current.Windows.OfType<Window>()
                         .Where(w => !hosts.Contains(w) && w.Owner is { } dono && hosts.Contains(dono) && w.IsVisible).ToList())
            { janelasAbertas++; d.Close(); }
        };
        fecha.Start();

        var op = new Operador("op-real", "Maria", "operador");
        var turno = new Sessao("sessao-brinde", Caixa.DiaOperacional(), op.Id, op.Nome, DateTime.Now, Dinheiro.DeReais(300m));
        var venda = new Pdv.Telas.Venda(op, turno);
        typeof(Pdv.Telas.Venda).GetField("_brindesDeTeste", P)!.SetValue(venda, brindes);
        host.Content = venda;
        host.UpdateLayout();

        var catPromo = (string)typeof(Pdv.Telas.Venda).GetField("CategoriaPromo", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        var contagem = Campo<Dictionary<string, int>>(venda, "_quantosPorCategoria");
        var totalPromo = ProdutosPorPromo.Sum() + 1 + 1;   // produtos + card com código + cartão da raspadinha
        checar(contagem.GetValueOrDefault(catPromo) == totalPromo,
            $"TL-1 a aba Promoções conta os produtos, o card com código e o cartão da raspadinha ({contagem.GetValueOrDefault(catPromo)} de {totalPromo})");

        var cartao = Campo<Border>(venda, "CartaoRaspadinha");
        var lista = Campo<ItemsControl>(venda, "ListaProdutos");
        var rolagem = Campo<ScrollViewer>(venda, "RolagemProdutos");
        checar(cartao.Visibility != Visibility.Visible, "TL-2 fora da aba Promoções o cartão da raspadinha não aparece");

        void AbrirPromo()
        {
            typeof(Pdv.Telas.Venda).GetField("_categoriaAtual", P)!.SetValue(venda, catPromo);
            Invocar(venda, "PintarProdutos");
            host.UpdateLayout();
        }
        AbrirPromo();
        var posCartao = cartao.TranslatePoint(new Point(0, 0), host);
        var posLista = rolagem.TranslatePoint(new Point(0, 0), host);
        checar(cartao.Visibility == Visibility.Visible && cartao.ActualHeight > 0 && posCartao.Y < posLista.Y
               && posCartao.X >= 0 && posCartao.X + cartao.ActualWidth <= 1024,
            $"TL-3 na aba Promoções o cartão aparece no TOPO, acima da lista, e cabe em 1024 (y={posCartao.Y:0} lista y={posLista.Y:0}, {cartao.ActualWidth:0}x{cartao.ActualHeight:0})");
        checar(!Descendentes(lista).Contains(cartao) && !Descendentes(rolagem).Contains(cartao) && !lista.Items.Contains(cartao),
            "TL-4 o cartão está FORA do ItemsControl ListaProdutos (não é célula do UniformGrid)");

        // ── A ALTURA DA ABA, ANTES E DEPOIS ────────────────────────────────
        checar(lista.Items.Count == 1 && lista.Items[0] is Grid, "TL-5 a grade da aba recebe um item só: as colunas independentes");
        var colunasGrid = (Grid)lista.Items[0];
        var pilhas = colunasGrid.Children.OfType<StackPanel>().ToList();
        var blocos = pilhas.SelectMany(p => p.Children.OfType<FrameworkElement>()).ToList();
        var card = blocos.OfType<Button>().FirstOrDefault(b => b.Tag as string == IdFunc);
        checar(blocos.Count == ProdutosPorPromo.Length + 1 && card is not null && pilhas[0].Children.IndexOf(card) == 0,
            $"TL-6 os 14 blocos (13 seções e o card do funcionário) estão nas colunas, com o card abrindo a primeira ({blocos.Count})");
        var depois = lista.ActualHeight;
        // o ESPAÇO que o card ocupa na aba (com a margem): na coluna, o dele; na grade velha, a célula
        var cardDepois = card?.DesiredSize.Height ?? 0;
        var maiorDepois = blocos.Max(b => b.DesiredSize.Height);
        var larguraLista = lista.ActualWidth;
        var colunasDaAba = pilhas.Count;
        // O ANTES medido de verdade: os MESMOS blocos num UniformGrid com o número de colunas de
        // antes, na mesma largura. Era assim que a aba desenhava.
        foreach (var p in pilhas) p.Children.Clear();
        var velha = new UniformGrid { Columns = colunasDaAba };
        foreach (var b in blocos) velha.Children.Add(b);
        velha.Measure(new Size(larguraLista, double.PositiveInfinity));
        velha.Arrange(new Rect(0, 0, larguraLista, velha.DesiredSize.Height));
        var antes = velha.DesiredSize.Height;
        var cardAntes = antes / ((blocos.Count + colunasDaAba - 1) / colunasDaAba);   // a célula: toda linha com a altura da maior
        velha.Children.Clear();
        Console.WriteLine($"      [medido] aba Promoções 1024x768, {colunasDaAba} coluna(s), 14 blocos: altura {antes:0} px antes (UniformGrid) e {depois:0} px depois (colunas); card do funcionário {cardAntes:0} px antes e {cardDepois:0} px depois; maior seção {maiorDepois:0} px");
        checar(depois < antes * 0.75, $"TL-7 a aba fica mais curta: {depois:0} px contra {antes:0} px no UniformGrid");
        checar(cardDepois > 0 && cardDepois < 130 && cardAntes >= maiorDepois - 1 && cardAntes > cardDepois * 2,
            $"TL-8 o card do desconto de funcionário ocupa a altura dele ({cardDepois:0} px), não a da maior seção ({cardAntes:0} px antes)");
        AbrirPromo();   // repinta as colunas de verdade
        Foto(host, "promocoes-1024.png");

        // a mesma aba num monitor grande: 2 ou 3 colunas, e cada uma com as suas alturas
        host.Width = 1920; host.Height = 1080;
        host.UpdateLayout();
        Bombear(150);
        host.UpdateLayout();
        var grade1920 = (Grid)lista.Items[0];
        var pilhas1920 = grade1920.Children.OfType<StackPanel>().ToList();
        var alturas1920 = pilhas1920.SelectMany(p => p.Children.OfType<FrameworkElement>()).Select(b => b.DesiredSize.Height).ToList();
        var uniforme1920 = ColunasPorAltura.AlturaEmGradeUniforme(alturas1920, pilhas1920.Count);
        Console.WriteLine($"      [medido] aba Promoções 1920x1080, {pilhas1920.Count} colunas: {lista.ActualHeight:0} px (colunas) contra {uniforme1920:0} px (UniformGrid); colunas com {string.Join(" / ", pilhas1920.Select(p => p.ActualHeight.ToString("0")))} px");
        Foto(host, "promocoes-1920.png");
        checar(pilhas1920.Count >= 2 && alturas1920.Count == 14 && lista.ActualHeight < uniforme1920 * 0.75,
            $"TL-8b em 1920 a aba usa {pilhas1920.Count} colunas e fica mais curta que a grade uniforme ({lista.ActualHeight:0} contra {uniforme1920:0} px)");
        host.Width = 1024; host.Height = 768;
        host.UpdateLayout();
        Bombear(150);
        host.UpdateLayout();

        // ── O FLUXO NA TELA ─────────────────────────────────────────────────
        var txtCodigo = Campo<TextBox>(venda, "TxtCodigoRaspadinha");
        var btnConferir = Campo<Button>(venda, "BtnConferirRaspadinha");
        var linha = Campo<TextBlock>(venda, "TxtRaspadinha");
        var btnBrinde = Campo<Button>(venda, "BtnBrinde");
        bool Ocupado() => Campo<bool>(venda, "_brindeOcupado");
        void Clicar(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        // item de venda na comanda + rascunho gravado: o brinde não pode tocar em nenhum dos dois.
        // O rascunho só grava depois do Loaded (OferecerRascunho): deixa a tela terminar de subir.
        EsperarAte(() => Campo<bool>(venda, "_rascunhoOferecido"), 3000);
        Invocar(venda, "Adicionar", Produto("p-01"));
        host.UpdateLayout();
        string? Rascunho()
        {
            using var cx = Banco.Abrir();
            return cx.ExecuteScalar<string?>("SELECT itens_json || '|' || desconto_cent FROM comanda_rascunho WHERE id = 1");
        }
        var comanda = Campo<System.Collections.IList>(venda, "_comanda");
        var comandaAntes = string.Join(",", comanda.Cast<object>().Select(i => ((Pdv.Telas.ItemComanda)i).Produto.Id + "x" + ((Pdv.Telas.ItemComanda)i).Qtd.Milesimos));
        var rascunhoAntes = Rascunho();

        var conferirVazio = fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcConferir);
        txtCodigo.Text = "  ";
        Clicar(btnConferir);
        Bombear(100);
        checar(linha.Text == Brindes.TextoSemCodigo && fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcConferir) == conferirVazio,
            $"TL-9a campo vazio: 'Digite o código do cliente.' e nada vai à nuvem ('{linha.Text}')");

        txtCodigo.Text = "zzzzzz";
        Clicar(btnConferir);
        EsperarAte(() => !Ocupado() && linha.Text != "Conferindo o código…", 8000);
        checar(linha.Text == "Não achei esse código. Se for cortesia, use o botão da comanda." && btnBrinde.Visibility != Visibility.Visible
               && txtCodigo.Text == "AD-ZZZZZZ",
            $"TL-9 código que não existe: uma linha, sem botão, e o campo mostra o código como o sistema leu ('{linha.Text}')");

        txtCodigo.Text = "ck7 ny2";
        Clicar(btnConferir);
        EsperarAte(() => !Ocupado() && linha.Text != "Conferindo o código…", 8000);
        Foto(host, "brinde-conferido-1024.png");
        checar(linha.Text == "🍪 1 Cookie Clássico · Maria · vale até 05/10" && btnBrinde.Visibility == Visibility.Visible
               && (string)btnBrinde.Content == "Escolher o brinde",
            $"TL-10 código que vale: a linha do prêmio e 'Escolher o brinde' ('{linha.Text}')");

        // tocar em Escolher abre o diálogo do combo; voltar não entrega nada
        var abertasAntes = janelasAbertas;
        Clicar(btnBrinde);
        Bombear(300);
        checar(janelasAbertas == abertasAntes + 1 && fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcEntregar) == 0,
            "TL-11 'Escolher o brinde' abre o diálogo; fechar sem escolher não entrega nada");

        // 21/09 (revisão): o botão vale para o código CONFERIDO. O operador digita o código do
        // próximo cliente e não confere: o botão tem que sair, senão "Escolher o brinde"
        // queimaria o código do cliente anterior para o cliente novo.
        txtCodigo.Text = "AD-OUT234";
        Bombear(60);
        checar(btnBrinde.Visibility != Visibility.Visible && Campo<object?>(venda, "_brindeConferido") is null,
            $"TL-11b digitou outro código sem conferir: o botão sai e o prêmio conferido é esquecido ('{linha.Text}')");
        txtCodigo.Text = CodCookie;
        Clicar(btnConferir);
        EsperarAte(() => !Ocupado() && linha.Text != "Conferindo o código…", 8000);
        checar(btnBrinde.Visibility == Visibility.Visible && Campo<object?>(venda, "_brindeConferido") is not null,
            "TL-11c conferiu de novo: o botão volta");

        // escolhido e confirmado (os diálogos pulados pela porta da suíte)
        var escolhas = new List<Escolha> { new(NewYork, null, "COOKIE NEW YORK", "r-cookie", 1, "Cookie clássico") };
        var tarefa = (Task)typeof(Pdv.Telas.Venda).GetMethod("EntregarBrindeAsync", P)!.Invoke(venda, new object[] { escolhas })!;
        EsperarAte(() => tarefa.IsCompleted, 10000);
        host.UpdateLayout();
        Foto(host, "brinde-entregue-1024.png");
        checar(linha.Text == Brindes.TextoEntregue && btnBrinde.Visibility != Visibility.Visible && txtCodigo.Text == ""
               && fake.RaspadinhasUsadas.ContainsKey(CodCookie),
            $"TL-12 entregue: 'Brinde entregue. Não passe na comanda.', o campo limpa para o próximo ('{linha.Text}')");
        var comandaDepois = string.Join(",", comanda.Cast<object>().Select(i => ((Pdv.Telas.ItemComanda)i).Produto.Id + "x" + ((Pdv.Telas.ItemComanda)i).Qtd.Milesimos));
        checar(comandaDepois == comandaAntes && comandaAntes == "p-01x1000" && Rascunho() == rascunhoAntes && rascunhoAntes is not null,
            $"TL-13 o brinde NÃO mexe na comanda ({comandaDepois}) nem no rascunho ({rascunhoAntes?.Length} caracteres, iguais)");
        using (var cx = Banco.Abrir())
            checar(cx.ExecuteScalar<long>("SELECT COUNT(*) FROM venda") == 0
                   && cx.ExecuteScalar<long>("SELECT COUNT(*) FROM outbox WHERE tipo <> 'brinde_entregar'") == 0
                   && cx.ExecuteScalar<long>("SELECT COUNT(*) FROM brinde WHERE situacao = 'entregue'") == 1,
                "TL-14 nenhuma venda nem linha de venda na fila: só o registro do brinde");

        // o mesmo código de novo: já foi usado
        txtCodigo.Text = CodCookie;
        Clicar(btnConferir);
        EsperarAte(() => !Ocupado() && linha.Text != "Conferindo o código…", 8000);
        var q = DateTimeOffset.Parse("2026-09-21T16:40:00-03:00").LocalDateTime;
        checar(linha.Text == $"Este código já foi usado em {q:dd/MM} às {q:HH:mm} por Maria." && btnBrinde.Visibility != Visibility.Visible,
            $"TL-15 o mesmo código outra vez: 'já foi usado', quando e por quem ('{linha.Text}')");
        checar(janelasAbertas == abertasAntes + 1, "TL-16 nenhuma janela além do diálogo do combo abriu");

        host.Content = null;
        host.Close();

        // ── homologação: o cartão trava ──────────────────────────────────────
        // (a comanda de antes sai do disco: senão a tela nova oferece restaurá-la)
        using (var cx = Banco.Abrir())
        {
            Pdv.Nucleo.Rascunho.Apagar(cx);
            Vendas.GravarConfig(cx, "homologacao", "1");
        }
        var host2 = new Window { Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0 };
        hosts.Add(host2);
        host2.Show();
        var venda2 = new Pdv.Telas.Venda(op, turno);
        typeof(Pdv.Telas.Venda).GetField("_brindesDeTeste", P)!.SetValue(venda2, brindes);
        host2.Content = venda2;
        typeof(Pdv.Telas.Venda).GetField("_categoriaAtual", P)!.SetValue(venda2, catPromo);
        Invocar(venda2, "PintarProdutos");
        host2.UpdateLayout();
        var conferirAntes = fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcConferir);
        Campo<TextBox>(venda2, "TxtCodigoRaspadinha").Text = CodCookie;
        Invocar(venda2, "ConferirRaspadinhaAsync");
        Bombear(200);
        checar(!Campo<Button>(venda2, "BtnConferirRaspadinha").IsEnabled && !Campo<TextBox>(venda2, "TxtCodigoRaspadinha").IsEnabled
               && Campo<TextBlock>(venda2, "TxtRaspadinha").Text == Brindes.TextoHomologacao
               && fake.ChamadasPorRpc.GetValueOrDefault(Brindes.RpcConferir) == conferirAntes,
            "TL-17 modo de homologação: o cartão trava ('Modo de homologação: brinde não sai.') e nada vai à nuvem");
        host2.Content = null;
        host2.Close();
        using (var cx = Banco.Abrir())
        {
            Vendas.GravarConfig(cx, "homologacao", "0");
            // ── loja sem promoção vigente, com a raspadinha ligada: a aba existe ──
            cx.Execute("DELETE FROM promo");
        }
        var host3 = new Window { Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0 };
        hosts.Add(host3);
        host3.Show();
        var venda3 = new Pdv.Telas.Venda(op, turno);
        host3.Content = venda3;
        host3.UpdateLayout();
        var cont3 = Campo<Dictionary<string, int>>(venda3, "_quantosPorCategoria");
        typeof(Pdv.Telas.Venda).GetField("_categoriaAtual", P)!.SetValue(venda3, catPromo);
        Invocar(venda3, "PintarProdutos");
        host3.UpdateLayout();
        checar(cont3.GetValueOrDefault(catPromo) == 1 && Campo<Border>(venda3, "CartaoRaspadinha").Visibility == Visibility.Visible
               && Campo<TextBlock>(venda3, "TxtSemProduto").Visibility != Visibility.Visible
               && Campo<ItemsControl>(venda3, "ListaProdutos").Items.Count == 0,
            "TL-18 sem promoção vigente e com a raspadinha ligada: a aba existe, com o cartão e sem o 'Nada nesta categoria'");
        host3.Content = null;
        host3.Close();
        // e com a raspadinha desligada e nenhuma promoção, a aba some (como sempre foi). Na janela e
        // fechada no fim, como as outras: tela que nunca descarrega deixa os eventos estáticos
        // (tema, sino) ligados, e um deles gravaria depois no banco de verdade do caixa.
        using (var cx = Banco.Abrir()) Vendas.GravarConfig(cx, Brindes.ChaveConfigLoja, "0");
        var host4 = new Window { Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0 };
        hosts.Add(host4);
        host4.Show();
        var venda4 = new Pdv.Telas.Venda(op, turno);
        host4.Content = venda4;
        host4.UpdateLayout();
        checar(!Campo<Dictionary<string, int>>(venda4, "_quantosPorCategoria").ContainsKey(catPromo),
            "TL-19 raspadinha desligada e nenhuma promoção: a aba Promoções não aparece");
        host4.Content = null;
        host4.Close();
        fecha.Stop();
        checar(janelasAbertas == abertasAntes + 1, "TL-20 as telas seguintes (homologação, aba só com o cartão) não abriram janela nenhuma");
    }

    // ── 9. PELO FONTE ────────────────────────────────────────────────────────
    private static void Fonte(Action<bool, string> checar)
    {
        var raiz = Raiz();
        string Ler(params string[] partes) => raiz is null ? "" : File.ReadAllText(Path.Combine(new[] { raiz }.Concat(partes).ToArray()));
        var nucleo = Ler("Pdv.Nucleo", "Brindes.cs");
        var venda = Ler("Telas", "Venda.xaml.cs");
        var xaml = Ler("Telas", "Venda.xaml");

        checar(nucleo.Length > 0 && !nucleo.Contains("LinhaVenda", StringComparison.Ordinal)
               && !Regex.IsMatch(nucleo, @"Vendas\.Finalizar|Fiscal\.|Emissor|AnonKey"),
            "FT-1 Brindes.cs não conhece venda, nota nem a chave pública");
        var ini = venda.IndexOf("// ── BRINDE DA RASPADINHA (21/09/2026)", StringComparison.Ordinal);
        var fim = venda.IndexOf("// ── fim do BRINDE DA RASPADINHA", StringComparison.Ordinal);
        var bloco = ini >= 0 && fim > ini ? venda[ini..fim] : "";
        var proibidos = new[] { "_comanda", "Rascunho", "Finalizar(", "new Pagamento", "LinhaVenda", "EsvaziarComanda", "PintarComanda", "Emissor" }
            .Where(p => bloco.Contains(p, StringComparison.Ordinal)).ToList();
        checar(bloco.Length > 0 && proibidos.Count == 0,
            $"FT-2 o bloco do brinde na tela não toca comanda, rascunho, pagamento nem nota ({string.Join(", ", proibidos)})");
        var foraDaSuite = raiz is null ? new List<string>() : Directory.EnumerateFiles(raiz, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "Pdv.Testes" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && File.ReadAllText(f).Contains("_brindesDeTeste", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(raiz, f)).ToList();
        checar(Regex.Matches(venda, @"\b_brindesDeTeste\b").Count == 2 && venda.Contains("_brindesDeTeste = null;", StringComparison.Ordinal)
               && foraDaSuite.SequenceEqual(new[] { Path.Combine("Telas", "Venda.xaml.cs") }),
            "FT-3 a porta de teste do brinde só existe declarada nula e lida uma vez em Venda.xaml.cs");
        var iCartao = xaml.IndexOf("x:Name=\"CartaoRaspadinha\"", StringComparison.Ordinal);
        var iLista = xaml.IndexOf("x:Name=\"ListaProdutos\"", StringComparison.Ordinal);
        var iFimLista = iLista < 0 ? -1 : xaml.IndexOf("</ItemsControl>", iLista, StringComparison.Ordinal);
        checar(iCartao > 0 && iLista > iCartao && iFimLista > iLista
               && xaml.IndexOf("CartaoRaspadinha", iLista, StringComparison.Ordinal) is var dentro && (dentro < 0 || dentro > iFimLista),
            "FT-4 no XAML o cartão é declarado antes e fora do ItemsControl ListaProdutos");
        var dren = Ler("Pdv.Nucleo", "Drenagem.cs");
        checar(dren.Contains("Brindes.TipoNaFila => await Brindes.ResolverNaFilaAsync(", StringComparison.Ordinal),
            "FT-5 a Drenagem manda 'brinde_entregar' para a resolução que confere antes (nunca direto para entregar)");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Só com PDV_FOTO_BRINDE apontando para uma pasta: grava a tela em PNG naquele passo, para
    /// conferir o cartão com os olhos. Sem a variável não faz nada (a bateria não grava arquivo).
    /// </summary>
    private static void Foto(Window host, string nome)
    {
        var pasta = Environment.GetEnvironmentVariable("PDV_FOTO_BRINDE");
        if (string.IsNullOrWhiteSpace(pasta)) return;
        host.UpdateLayout();
        FotoVenda.Salvar(Path.Combine(pasta, nome), (int)host.Width, (int)host.Height, host, null);
    }
    private static string? Raiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj"))) return dir.FullName;
        return null;
    }

    private static IEnumerable<DependencyObject> Descendentes(DependencyObject o)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
        {
            var c = VisualTreeHelper.GetChild(o, i);
            yield return c;
            foreach (var d in Descendentes(c)) yield return d;
        }
    }

    private static void Bombear(int ms)
    {
        var fim = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < fim)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(20);
        }
    }

    private static bool EsperarAte(Func<bool> cond, int ms)
    {
        var fim = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < fim)
        {
            Bombear(40);
            if (cond()) return true;
        }
        return cond();
    }

    private static Pdv.Telas.Produto Produto(string id)
    {
        using var cx = Banco.Abrir();
        var p = cx.QueryFirst("SELECT id, plu, nome, categoria, preco_cent, unidade, csosn FROM produto WHERE id=@id", new { id });
        return new Pdv.Telas.Produto((string)p.id, (string?)p.plu, (string)p.nome, (string)p.categoria,
            new Dinheiro((long)p.preco_cent), (string)p.unidade, null, null, (string?)p.csosn, 0, null);
    }

    private static T Campo<T>(object alvo, string nome)
        => (T)alvo.GetType().GetField(nome, P)!.GetValue(alvo)!;

    private static void Invocar(object alvo, string metodo, params object?[] args)
        => alvo.GetType().GetMethods(P).First(m => m.Name == metodo && m.GetParameters().Length == args.Length)
            .Invoke(alvo, args);
}
