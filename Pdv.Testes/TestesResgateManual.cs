using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O RESGATE MANUAL DA RASPADINHA DENTRO DO PDV (08/10/2026, SQL 154, caixa 1.0.22).
///
/// O dono: "add dentro do PDV tb a pagina de resgate da raspadinha caso falhe pra resgatar". A tela
/// confere o codigo, escolhe o pedido (ou o balcao) e o sabor, e resgata pelo MESMO caminho do chat
/// automatico. Aqui se prova o que da sem o servidor de verdade: os corpos das tres acoes, a
/// leitura das respostas (fixtures copiadas do contrato do 154), a regra dos sabores, os textos
/// (uma linha, sem travessao), a execucao da resposta pelo servico (papel, envio, aviso), a janela
/// por reflexao com um servidor falso, e o portao da chave da loja (cartao, toast e KDS so com ela).
/// Pelo fonte: a tela nao conhece o validador antigo, nao toca a venda, e ChatIfood.xaml.cs e
/// Impressao.cs nao mudam.
/// </summary>
public static class TestesResgateManual
{
    private const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string OID = "3f2a0000-0000-4000-8000-000000000001";
    private const string OID2 = "3f2a0000-0000-4000-8000-000000000002";
    private const string SAV = "ff493e25-f413-42e5-a46a-96c0b788813f";
    private const string BONUS = "b1540000-0000-4000-8000-000000000001";
    private static readonly string Trav = ((char)0x2014).ToString();
    private static readonly string Traco = ((char)0x2013).ToString();

    private static readonly DateTime Agora = new(2026, 10, 8, 10, 30, 0);

    // ── fixtures copiadas do contrato (secao 5.1 do desenho) ────────────────
    private const string ConferirOk = """
        {"ok":true,"ligado":true,"modo":"automatico","chat_ativo":true,
         "codigo":{"codigo":"AD-RB684S","scratch_id":"s1","slug":"cookie_classico","premio":"1 Cookie Clássico","nome_curto":"Cookie Clássico","emoji":"🍪",
                   "vence_em":"2026-10-14T23:59:59-03:00","motivo":"ok","loja_codigo":null,"usado_em":null,"usado_por":null,"cliente_nome":"FERNANDA LIMA",
                   "escolhas":1,"repete":false,"opcoes":["New York","Tradicional","Triplo"],"padrao":"","tem_catalogo":true,"itens_fixos":[]},
         "frase":null,
         "pedidos":[{"ifood_order_id":"3f2a0000-0000-4000-8000-000000000001","numero":"2607","cliente":"Fernanda Lima","entrega_status":"pronto","recebido_em":"2026-10-08T10:24:00-03:00","sugerido":true,"premio_codigo":null,"conversa_estado":"humano"},
                    {"ifood_order_id":"3f2a0000-0000-4000-8000-000000000002","numero":"2611","cliente":"Carlos","entrega_status":null,"recebido_em":"2026-10-08T10:31:00-03:00","sugerido":false,"premio_codigo":"AD-ABC123","conversa_estado":null}],
         "pedido_informado":{"ifood_order_id":"3f2a0000-0000-4000-8000-000000000001","numero":"2607","cliente":"Fernanda Lima","entrega_status":"pronto","saiu":false,"cancelado":false},
         "candidatos":["AD-RB684S","AD-7KQ2MX"]}
        """;
    /// <summary>A tela abriu pelo pedido e ainda nao tem codigo: so os pedidos, o informado e os candidatos.</summary>
    private const string ConferirSemCodigo = """
        {"ok":true,"ligado":true,"modo":"automatico","chat_ativo":true,"codigo":null,"frase":null,
         "pedidos":[{"ifood_order_id":"3f2a0000-0000-4000-8000-000000000001","numero":"2607","cliente":"Fernanda Lima","entrega_status":"pronto","recebido_em":"2026-10-08T10:24:00-03:00","sugerido":true,"premio_codigo":null,"conversa_estado":"humano"}],
         "pedido_informado":{"ifood_order_id":"3f2a0000-0000-4000-8000-000000000001","numero":"2607","cliente":"Fernanda Lima","entrega_status":"pronto","saiu":false,"cancelado":false},
         "candidatos":["AD-RB684S","AD-7KQ2MX"]}
        """;
    private const string ConferirCombo = """
        {"ok":true,"ligado":true,"modo":"assistido","chat_ativo":true,
         "codigo":{"codigo":"AD-CMB001","scratch_id":"s2","slug":"combo_coxinha_refri","premio":"1 Combo Coxinha e Refrigerante","nome_curto":"Combo Coxinha","emoji":null,
                   "vence_em":null,"motivo":"ok","cliente_nome":"Rafa","escolhas":1,"repete":false,"opcoes":["Coca-Cola","Coca-Cola Zero","Guaraná"],"padrao":"Coca-Cola","tem_catalogo":true,"itens_fixos":["Coxinha"]},
         "pedidos":[],"pedido_informado":null,"candidatos":[]}
        """;
    private const string ConferirCaixa2 = """
        {"ok":true,"ligado":true,"modo":"automatico","chat_ativo":false,
         "codigo":{"codigo":"AD-CX2001","slug":"caixa_2_cookies","premio":"1 Caixa com 2 Cookies","nome_curto":"Caixa com 2 Cookies","motivo":"ok","cliente_nome":"Bia",
                   "escolhas":2,"repete":true,"opcoes":["New York","Tradicional","Triplo"],"padrao":"","tem_catalogo":true,"itens_fixos":[]},
         "pedidos":[],"pedido_informado":null,"candidatos":[]}
        """;
    private const string ConferirDesligado = """{"ok":true,"ligado":false,"modo":"automatico","chat_ativo":true,"codigo":null,"pedidos":[],"pedido_informado":null,"candidatos":[]}""";
    private static string ConferirRecusa(string motivo, string extra = "") =>
        "{\"ok\":true,\"ligado\":true,\"modo\":\"automatico\",\"chat_ativo\":true,\"codigo\":{\"codigo\":\"AD-RB684S\",\"motivo\":\"" + motivo + "\"," +
        "\"escolhas\":0,\"repete\":false,\"opcoes\":[],\"padrao\":\"\",\"tem_catalogo\":false,\"itens_fixos\":[]" + extra + "},\"frase\":null,\"pedidos\":[],\"pedido_informado\":null,\"candidatos\":[]}";

    private const string ResgateOk = """
        {"ok":true,"modo":"automatico","prova":false,"acao":"resgatou","motivo":null,"repetida":false,"destino":"ifood",
         "pedido":{"ifood_order_id":"3f2a0000-0000-4000-8000-000000000001","numero":"2607","cliente":"Fernanda Lima","entrega_status":"pronto"},
         "conversa":{"id":"9b1c","estado":"resgatado","sombra":false},
         "bonus":{"id":"b1540000-0000-4000-8000-000000000001","codigo":"AD-RB684S","loja":"American Day Savassi","premio_nome":"1 Cookie Clássico","premio_emoji":"🍪",
                  "cliente_nome":"Fernanda Lima","pedido_numero":"2607","ifood_order_id":"3f2a0000-0000-4000-8000-000000000001",
                  "origem":"caixa","itens":[{"nome":"Cookie New York","qtd":1}],"sabores":["New York"],"assinatura":"Ingrid","comanda_onde":"ambos","criado_em":"2026-10-08T13:30:00Z"},
         "itens":[{"nome":"Cookie New York","qtd":1}],
         "comanda":{"imprimir_aqui":true},
         "saidas":[{"id":4413,"etapa":"confirmado","ordem":1,"texto":"Oi, Fernanda! Recebemos o código da sua raspadinha. Seu prêmio vai junto com este pedido: 1 Cookie New York. Aproveite!",
                    "como":"sdk","estado":"reservada","canal":"sendbird_gc_cm_3f2a0000-0000-4000-8000-000000000001_ff493e25-f413-42e5-a46a-96c0b788813f",
                    "ifood_order_id":"3f2a0000-0000-4000-8000-000000000001","pedido_numero":"2607"}],
         "aviso":{"tipo":"resgatou","texto":"Resgate no #2607: 1 Cookie New York para Fernanda.","ifood_order_id":"3f2a0000-0000-4000-8000-000000000001","pedido_numero":"2607"},
         "pelo_dono":false,"frase":"Resgatado: 1 Cookie New York para Fernanda no #2607.","desfazer_ate":"2026-10-08T15:30:00Z"}
        """;
    private static string ResgateRecusa(string motivo, string frase) =>
        "{\"ok\":false,\"motivo\":\"" + motivo + "\",\"frase\":\"" + frase + "\",\"codigo\":{\"codigo\":\"AD-RB684S\",\"motivo\":\"ok\",\"escolhas\":0,\"opcoes\":[],\"padrao\":\"\",\"itens_fixos\":[]}}";
    private const string DesfazerOk = """{"ok":true,"codigo":"AD-RB684S","bonus_id":"b1540000-0000-4000-8000-000000000001","pedido_numero":"2607","autorizador":"Brenno","frase":"Resgate desfeito. O código volta a valer."}""";

    private static readonly string SinalAutomatico =
        "{\"ok\":true,\"agora\":\"2026-10-08T13:40:00Z\",\"modo\":\"automatico\",\"palavra_modo\":\"desligado\",\"envio\":\"sdk\",\"comanda_onde\":\"ambos\","
        + "\"merchant_ids\":[\"" + SAV + "\"],\"intervalo_s\":60,\"pausado\":null,\"avisos\":[],\"saidas\":[],\"comandas\":[]}";

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        OsCorpos(checar);
        ALeitura(checar);
        OsSabores(checar);
        OsTextos(checar);
        AConfigDoPainel(checar);
        await OServico(checar);
        await AJanela(checar);
        await OPortaoDaChave(checar);
        Fonte(checar);
    }

    // ── OS CORPOS DAS TRES ACOES ─────────────────────────────────────────────
    private static void OsCorpos(Action<bool, string> checar)
    {
        var c = JsonNode.Parse(ResgateManual.CorpoConferir(null, " ad-rb684s ", OID))!;
        checar((string)c["acao"]! == "resgate_conferir" && (string)c["terminal"]! == ConversaRaspadinha.NomeDoTerminal()
               && (string)c["codigo"]! == "ad-rb684s" && (string)c["ifood_order_id"]! == OID,
            "CP-1 conferir: acao, terminal (o nome da maquina), o codigo como digitado e o pedido");
        var v = JsonNode.Parse(ResgateManual.CorpoConferir("CX1", "", null))!;
        checar((string)v["codigo"]! == "" && v["ifood_order_id"] is null, "CP-2 conferir sem codigo e sem pedido (a tela abriu vazia)");

        var r = JsonNode.Parse(ResgateManual.CorpoResgatar("CX1", "Ingrid", "AD-RB684S", false, OID, new[] { "New York" }, true))!;
        checar((string)r["acao"]! == "resgate_manual" && (string)r["operador"]! == "Ingrid" && (string)r["codigo"]! == "AD-RB684S"
               && (string)r["destino"]!["tipo"]! == "ifood" && (string)r["destino"]!["ifood_order_id"]! == OID
               && r["sabores"]!.AsArray().Count == 1 && (string)r["sabores"]![0]! == "New York" && (bool)r["avisar"]!,
            "CP-3 resgatar no pedido: os campos no nome do contrato");
        var b = JsonNode.Parse(ResgateManual.CorpoResgatar("CX1", "Ingrid", "AD-RB684S", true, OID, Array.Empty<string>(), false))!;
        checar((string)b["destino"]!["tipo"]! == "balcao" && b["destino"]!["ifood_order_id"] is null && b["sabores"]!.AsArray().Count == 0 && !(bool)b["avisar"]!,
            "CP-4 resgatar no balcao: sem pedido no destino, sem sabor, sem avisar");
        var o = JsonNode.Parse(ResgateManual.CorpoResgatar("CX1", "  ", "AD-RB684S", true, null, new[] { "a", "b", "c" }, false))!;
        checar((string)o["operador"]! == "Caixa" && o["sabores"]!.AsArray().Count == 2, "CP-5 operador vazio vira Caixa; no maximo 2 sabores");
        checar(ResgateManual.Operador("Ana" + Trav + "Bia " + Traco) == "Ana-Bia -" && ResgateManual.Operador(new string('x', 60)).Length == 40,
            "CP-6 a assinatura nunca leva travessao e para em 40");

        var d = JsonNode.Parse(ResgateManual.CorpoDesfazer("CX1", "a0420449-0000-4000-8000-000000000001", "Ingrid", BONUS, "cliente desistiu", "123 456"))!;
        checar((string)d["acao"]! == "resgate_desfazer" && (string)d["terminal_uuid"]! == "a0420449-0000-4000-8000-000000000001"
               && (string)d["bonus_id"]! == BONUS && (string)d["motivo"]! == "cliente desistiu" && (string)d["codigo_totp"]! == "123456",
            "CP-7 desfazer: o codigo do dono so com os digitos, o uuid do terminal, o motivo");
        var diag = ResgateManual.Diag("desfazer", "AD-RB684S", "-", 200, "desfeito", BONUS);
        checar(!diag.Contains("123456") && diag.Contains("codigo=AD-RB68..") && !diag.Contains("Fernanda") && diag.Contains("bonus=b1540000-000"),
            "CP-8 o rastro nunca leva o codigo do dono nem o nome do cliente (codigo e bonus curtos)");
        checar(Autorizacao.ReferenciaResgate(BONUS) == "resgate:" + BONUS && Autorizacao.TipoResgateDesfazer == "resgate_desfazer",
            "CP-9 a referencia do TOTP e resgate:<bonus_id>, o tipo e resgate_desfazer (os mesmos do servidor)");
        checar(ResgateManual.Prazo == TimeSpan.FromSeconds(15) && ResgateManual.Edge == ConversaRaspadinha.Edge
               && ResgateManual.ChaveConfigLoja == "raspadinha_resgate_pdv",
            "CP-10 prazo de 15 s, a mesma borda do chat e a chave da loja do SQL 154");
    }

    // ── A LEITURA DAS RESPOSTAS ──────────────────────────────────────────────
    private static void ALeitura(Action<bool, string> checar)
    {
        var c = ResgateManual.LerConferencia(200, ConferirOk);
        checar(c.Ok && c.Ligado && c.Modo == "automatico" && c.ChatAtivo && c.Codigo is { Ok: true, Codigo: "AD-RB684S", NomeCurto: "Cookie Clássico", Escolhas: 1, Repete: false, Padrao: "", TemCatalogo: true }
               && c.Codigo.Opcoes.SequenceEqual(new[] { "New York", "Tradicional", "Triplo" }) && c.Codigo.VenceEm?.Day == 14 && c.Codigo.ClienteNome == "FERNANDA LIMA",
            "LC-1 o codigo ok: premio, opcoes, escolhas, validade, cliente");
        checar(c.Pedidos.Count == 2 && c.Pedidos[0] is { Numero: "2607", Sugerido: true, EntregaStatus: "pronto", ConversaEstado: "humano", PremioCodigo: null }
               && c.Pedidos[1] is { Numero: "2611", PremioCodigo: "AD-ABC123", Sugerido: false } && c.Pedidos[0].RecebidoEm?.Minute == 24,
            "LC-2 os pedidos abertos com sugerido, estado da conversa e o premio que o pedido ja tem");
        checar(c.PedidoInformado is { Numero: "2607", Saiu: false, Cancelado: false } && c.Candidatos.SequenceEqual(new[] { "AD-RB684S", "AD-7KQ2MX" }),
            "LC-3 o pedido informado e os candidatos da conversa");
        var cb = ResgateManual.LerConferencia(200, ConferirCombo);
        checar(cb.Codigo is { Escolhas: 1, Padrao: "Coca-Cola" } && cb.Codigo.ItensFixos.SequenceEqual(new[] { "Coxinha" }) && cb.Pedidos.Count == 0 && cb.Modo == "assistido",
            "LC-4 o combo: o padrao e os itens fixos; sem pedido aberto");
        var c2 = ResgateManual.LerConferencia(200, ConferirCaixa2);
        checar(c2.Codigo is { Escolhas: 2, Repete: true } && !c2.ChatAtivo, "LC-5 a caixa com 2: duas escolhas, pode repetir; chat da loja desligado");
        var d = ResgateManual.LerConferencia(200, ConferirDesligado);
        checar(d.Ok && !d.Ligado && d.Codigo is null, "LC-6 a chave da loja desligada vem no conferir");
        foreach (var (motivo, extra) in new[]
                 {
                     ("nao_achado", ""), ("outra_loja", ",\"loja_codigo\":\"Castelo\""), ("nao_raspou", ""),
                     ("vencido", ",\"vence_em\":\"2026-10-05T23:59:59-03:00\""), ("ja_usado", ",\"usado_em\":\"2026-10-07T13:16:00-03:00\",\"usado_por\":\"ingrid\""),
                 })
        {
            var r = ResgateManual.LerConferencia(200, ConferirRecusa(motivo, extra));
            checar(r.Ok && r.Codigo is { } k && !k.Ok && k.Motivo == motivo
                   && (motivo != "outra_loja" || k.LojaCodigo == "Castelo") && (motivo != "ja_usado" || (k.UsadoPor == "ingrid" && k.UsadoEm?.Hour == 13))
                   && (motivo != "vencido" || k.VenceEm?.Day == 5),
                $"LC-7 a recusa {motivo} e lida com o que a linha precisa");
        }
        checar(!ResgateManual.LerConferencia(200, "{lixo").Ok && ResgateManual.LerConferencia(200, "{lixo").Motivo == "resposta_ilegivel"
               && !ResgateManual.LerConferencia(200, "{\"ok\":false,\"motivo\":\"nao_autorizado\",\"frase\":\"Esse usuário não pode resgatar agora. Chame o gerente.\"}").Ok
               && ResgateManual.LerConferencia(200, "{\"ok\":false,\"motivo\":\"nao_autorizado\"}").Motivo == "nao_autorizado",
            "LC-8 corpo ilegivel nao e ok; ok:false traz o motivo");
        checar(ResgateManual.LerConferencia(-1, null).Motivo == "sem_rede" && ResgateManual.LerConferencia(0, "").Motivo == "sem_rede"
               && ResgateManual.LerConferencia(503, "x").Motivo == "sem_rede" && ResgateManual.LerConferencia(401, "").Motivo == "sem_permissao"
               && ResgateManual.LerConferencia(404, "").Motivo == "nuvem_sem_recurso",
            "LC-9 sem rede, sem permissao e borda sem publicar sao lidos pelo status");

        checar(ResgateManual.LerRecusa(200, ResgateOk) is null, "LR-1 a resposta ok do resgatar nao e recusa");
        var rec = ResgateManual.LerRecusa(200, ResgateRecusa("pedido_ja_tem", "O #2607 já tem um prêmio (AD-ABC123). Vale um por pedido."));
        checar(rec is { Motivo: "pedido_ja_tem" } && rec.Value.Frase!.Contains("AD-ABC123") && rec.Value.Codigo?.Codigo == "AD-RB684S",
            "LR-2 a recusa do resgatar: motivo, frase do servidor e o codigo");
        checar(ResgateManual.LerRecusa(-1, null) is { Motivo: "sem_rede" } && ResgateManual.LerRecusa(200, "nada") is { Motivo: "resposta_ilegivel" },
            "LR-3 sem rede e corpo ilegivel sao recusa (nada foi resgatado)");
        var ex = ResgateManual.LerExtrasDoResgate(ResgateOk);
        checar(ex.Frase == "Resgatado: 1 Cookie New York para Fernanda no #2607." && ex.DesfazerAte is not null && !ex.PeloDono,
            "LR-4 a frase, o prazo do desfazer e o pelo_dono do resgatar");

        var df = ResgateManual.LerDesfazer(200, DesfazerOk);
        checar(df.Ok && df.Codigo == "AD-RB684S" && df.Frase == "Resgate desfeito. O código volta a valer.", "LD-1 o desfazer ok");
        var dn = ResgateManual.LerDesfazer(200, "{\"ok\":false,\"motivo\":\"codigo_invalido\",\"dica\":\"vencido\",\"frase\":\"Código inválido. Tente de novo.\"}");
        checar(!dn.Ok && dn.Motivo == "codigo_invalido" && dn.Dica == "vencido", "LD-2 a recusa do desfazer com a dica");
        checar(ResgateManual.LerDesfazer(0, "").Motivo == "sem_rede" && ResgateManual.LerDesfazer(200, "?").Motivo == "resposta_ilegivel"
               && ResgateManual.LinhaDoDesfazer(ResgateManual.LerDesfazer(0, "")) == ResgateManual.TextoSemRedeDesfazer,
            "LD-3 sem rede no desfazer: nada foi desfeito");
    }

    // ── OS SABORES ───────────────────────────────────────────────────────────
    private static void OsSabores(Action<bool, string> checar)
    {
        var sem = ResgateManual.LerConferencia(200, ConferirRecusa("ok")).Codigo!;
        var um = ResgateManual.LerConferencia(200, ConferirOk).Codigo!;
        var dois = ResgateManual.LerConferencia(200, ConferirCaixa2).Codigo!;
        var combo = ResgateManual.LerConferencia(200, ConferirCombo).Codigo!;
        checar(ResgateManual.SaboresCompletos(sem, Array.Empty<string>()) && ResgateManual.SaboresCompletos(sem, new[] { "x" }),
            "SB-1 sem escolha o premio esta sempre completo");
        checar(!ResgateManual.SaboresCompletos(um, Array.Empty<string>()) && ResgateManual.SaboresCompletos(um, new[] { "New York" })
               && !ResgateManual.SaboresCompletos(um, new[] { "Banana" }) && !ResgateManual.SaboresCompletos(um, new[] { "New York", "Triplo" }),
            "SB-2 uma escolha: exatamente um sabor da lista");
        checar(!ResgateManual.SaboresCompletos(dois, new[] { "Triplo" }) && ResgateManual.SaboresCompletos(dois, new[] { "Triplo", "Triplo" })
               && ResgateManual.SaboresCompletos(dois, new[] { "New York", "Triplo" }),
            "SB-3 duas escolhas: dois sabores, pode repetir");
        checar(ResgateManual.PadraoMarcado(combo).SequenceEqual(new[] { "Coca-Cola" }) && ResgateManual.PadraoMarcado(um).Count == 0
               && ResgateManual.PadraoMarcado(dois).Count == 0,
            "SB-4 o padrao do catalogo ja vem marcado (Combo Coxinha: Coca-Cola); sem padrao, nada");
        var t1 = ResgateManual.Tocar(um, Array.Empty<string>(), "Triplo");
        var t2 = ResgateManual.Tocar(um, t1, "New York");
        var t3 = ResgateManual.Tocar(um, t2, "New York");
        checar(t1.SequenceEqual(new[] { "Triplo" }) && t2.SequenceEqual(new[] { "New York" }) && t3.Count == 0,
            "SB-5 com uma escolha o toque troca o sabor e o segundo toque no mesmo desmarca");
        var d1 = ResgateManual.Tocar(dois, Array.Empty<string>(), "Triplo");
        var d2 = ResgateManual.Tocar(dois, d1, "Triplo");
        var d3 = ResgateManual.Tocar(dois, d2, "Triplo");
        var d4 = ResgateManual.Tocar(dois, new[] { "Triplo", "New York" }, "Tradicional");
        checar(d1.SequenceEqual(new[] { "Triplo" }) && d2.SequenceEqual(new[] { "Triplo", "Triplo" }) && d3.SequenceEqual(new[] { "Triplo" })
               && d4.SequenceEqual(new[] { "New York", "Tradicional" }),
            "SB-6 com duas escolhas o mesmo chip duas vezes conta 2 (como o chat), o terceiro tira, e o novo empurra o mais velho");
        checar(ResgateManual.LinhaDeApoioDosSabores(um) == "Escolha 1 sabor." && ResgateManual.LinhaDeApoioDosSabores(dois) == "Escolha 2 sabores. Pode repetir."
               && ResgateManual.LinhaDeApoioDosSabores(sem) == "",
            "SB-7 a linha de apoio dos sabores");
    }

    // ── OS TEXTOS ────────────────────────────────────────────────────────────
    private static void OsTextos(Action<bool, string> checar)
    {
        var um = ResgateManual.LerConferencia(200, ConferirOk).Codigo!;
        checar(ResgateManual.LinhaDoPremio(um) == "🍪 Cookie Clássico para Fernanda. Vale até 14/10.",
            "TX-1 a linha do premio: emoji, nome curto, primeiro nome do cliente e a validade");
        var sem = ResgateManual.LerConferencia(200, ConferirCombo).Codigo!;
        checar(ResgateManual.LinhaDoPremio(sem) == "Combo Coxinha para Rafa.", "TX-2 sem emoji e sem validade a linha encurta");
        var linhas = new Dictionary<string, string>
        {
            ["nao_achado"] = "Não achei esse código. Confira as letras.",
            ["outra_loja"] = "Esse código é da Castelo. Aqui não vale.",
            ["nao_raspou"] = "O cliente ainda não raspou essa raspadinha.",
            ["vencido"] = "Esse código venceu em 05/10.",
            ["ja_usado"] = "Esse código já foi usado em 07/10 às 13:16 por ingrid.",
            ["pedido_ja_tem"] = "O #2607 já tem um prêmio. Vale um por pedido.",
            ["pedido_saiu"] = "O #5314 já saiu. Resgate no balcão ou guarde para o próximo pedido.",
            ["pedido_cancelado"] = "O #5314 foi cancelado. O código continua valendo.",
            ["sabor_invalido"] = "Escolha o sabor da lista.",
            ["desligado"] = "A tela de resgate está desligada nesta loja.",
            ["nao_autorizado"] = "Esse usuário não pode resgatar agora. Chame o gerente.",
            ["sem_rede"] = "Sem resposta do servidor. Toque em Resgatar de novo: não resgata duas vezes.",
        };
        var ruins = new List<string>();
        foreach (var (motivo, esperado) in linhas)
        {
            var extra = motivo switch
            {
                "outra_loja" => ",\"loja_codigo\":\"Castelo\"",
                "vencido" => ",\"vence_em\":\"2026-10-05T23:59:59-03:00\"",
                "ja_usado" => ",\"usado_em\":\"2026-10-07T13:16:00-03:00\",\"usado_por\":\"ingrid\"",
                _ => "",
            };
            var c = ResgateManual.LerConferencia(200, ConferirRecusa(motivo, extra)).Codigo;
            var numero = motivo == "pedido_ja_tem" ? "2607" : "5314";
            var linha = ResgateManual.LinhaDaRecusa(motivo, c, numero, null);
            if (linha != esperado) ruins.Add(motivo + "=[" + linha + "]");
            if (linha.StartsWith("Resgatado", StringComparison.Ordinal) || linha.Contains(Trav) || linha.Contains(Traco) || linha.Contains('\n') || linha.Length > 120)
                ruins.Add(motivo + "!");
        }
        checar(ruins.Count == 0, "TX-3 as recusas da secao 3.4, palavra por palavra, uma linha, sem travessao, nunca 'Resgatado' (" + string.Join(" ", ruins) + ")");
        checar(ResgateManual.LinhaDaRecusa("pedido_ja_tem", null, "2607", "O #2607 já tem um prêmio (AD-ABC123). Vale um por pedido.") == "O #2607 já tem um prêmio (AD-ABC123). Vale um por pedido."
               && ResgateManual.LinhaDaRecusa("xyz", null, null, "Frase do servidor.") == "Frase do servidor."
               && ResgateManual.LinhaDaRecusa("xyz", null, null, null) == "Não deu para resgatar agora. Tente de novo."
               && ResgateManual.LinhaDaRecusa("ja_usado", null, null, "Frase " + Trav + " com travessao") == "Esse código já foi usado.",
            "TX-4 a frase do servidor completa o pedido_ja_tem (com o codigo) e cobre motivo desconhecido; o travessao nunca chega a tela");
        var c0 = ResgateManual.LerConferencia(200, ConferirOk);
        checar(ResgateManual.LinhaDoPedido(c0.Pedidos[0]) == "#2607 Fernanda 10:24 pronto"
               && ResgateManual.LinhaDoPedido(c0.Pedidos[1]) == "#2611 Carlos 10:31 recebido já tem prêmio",
            "TX-5 a linha do pedido: numero, primeiro nome, hora, etapa, e 'já tem prêmio' quando ja tem");
        checar(ResgateManual.PerguntaDeConfirmacao(um, new[] { "New York" }, c0.Pedidos[0]) == "Resgatar Cookie Clássico (New York) para Fernanda no #2607?"
               && ResgateManual.PerguntaDeConfirmacao(um, Array.Empty<string>(), null) == "Resgatar Cookie Clássico para Fernanda no balcão?",
            "TX-6 a pergunta de confirmacao, no pedido e no balcao");
        checar(ResgateManual.TextoResgatado(true, true, false) == "Resgatado. A comanda saiu."
               && ResgateManual.TextoResgatado(true, false, false) == "Resgatado. A comanda não saiu: toque em Reimprimir no chat."
               && ResgateManual.TextoResgatado(true, true, true) == "Resgatado. Mande esta resposta no chat."
               && ResgateManual.TextoResgatado(false, false, false) == "Resgatado.",
            "TX-7 a linha depois do resgate");
        checar(ResgateManual.MotivoParaNaoDesfazer(Agora.AddMinutes(-119), Agora, null) is null
               && ResgateManual.MotivoParaNaoDesfazer(Agora.AddMinutes(-121), Agora, null) == "Passou de 2 horas. Para desfazer, use o CRM."
               && ResgateManual.MotivoParaNaoDesfazer(Agora, Agora, "pedido_saiu") == "O pedido já saiu com o brinde. Para desfazer, use o CRM.",
            "TX-8 a janela do desfazer: 2 h e nao depois de o KDS dizer que o pedido saiu");
        // revisao 08/10: a resposta pode se perder DEPOIS do commit (o codigo ja queimado, a comanda reservada);
        // a tela nao pode jurar que nada foi feito: pede para tocar de novo, e o servidor devolve o mesmo resgate
        checar(!ResgateManual.TextoSemRede.Contains("Nada foi") && ResgateManual.TextoSemRede.Contains("Toque em Resgatar de novo")
               && !ResgateManual.TextoSemRedeDesfazer.Contains("Nada foi") && ResgateManual.TextoSemRedeDesfazer.Contains("Toque em Desfazer de novo"),
            "TX-9 sem rede a tela NAO afirma que nada foi feito: pede para tocar de novo (o servidor nao resgata duas vezes)");
        var todos = typeof(ResgateManual).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        checar(todos.Count >= 15 && todos.All(t => !t.Contains(Trav) && !t.Contains(Traco) && !t.Contains('\n') && t.Length <= 120),
            $"TX-10 nenhum texto fixo tem travessao nem passa de uma linha ({todos.Count} textos)");
        checar(ResgateManual.PrimeiroNome("ANA PAULA SOUZA") == "Ana" && ResgateManual.PrimeiroNome("  ") is null && ResgateManual.PrimeiroNome("A") is null,
            "TX-11 o primeiro nome como o chat escreve");
        checar(ResgateManual.TiposDeAvisoComResgate.SetEquals(new[] { "humano", "expirou", "nao_raspou", "validador", "premio_sem_catalogo", "palavra_pedido_saiu" })
               && !ResgateManual.TiposDeAvisoComResgate.Contains("resgatou") && !ResgateManual.TiposDeAvisoComResgate.Contains("pausado"),
            "TX-12 os avisos que ganham o 'Resgatar aqui' sao os da secao 3.1 (nunca o resgatou nem o pausado)");
    }

    // ── A CONFIG DO PAINEL (a coluna nova desce) ─────────────────────────────
    private static void AConfigDoPainel(Action<bool, string> checar)
    {
        var com = ConfigLojaPainel.Ler("[{\"store\":\"American Day Savassi\",\"raspadinha_no_caixa\":false,\"raspadinha_no_chat\":true,\"raspadinha_resgate_pdv\":true}]");
        var sem = ConfigLojaPainel.Ler("[{\"store\":\"American Day Savassi\",\"raspadinha_no_caixa\":true}]");
        checar(com.Count == 1 && com[0].RaspadinhaResgatePdv == true && sem.Count == 1 && sem[0].RaspadinhaResgatePdv is null,
            "CF-1 a coluna raspadinha_resgate_pdv e lida; sem ela (servidor sem o 154) fica nula");
        var db = Path.Combine(Path.GetTempPath(), "resgate-cfg-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        try
        {
            Banco.Migrar(db);
            using var cx = Banco.Abrir(db);
            var mudou = ConfigLojaPainel.Aplicar(cx, com[0], Agora);
            checar(ResgateManual.LigadoNaLoja(cx) && mudou.Contains("resgate da raspadinha") && !Brindes.LigadoNaLoja(cx),
                $"CF-2 o painel liga a tela de resgate sem ligar o validador antigo ({mudou})");
            var mudou2 = ConfigLojaPainel.Aplicar(cx, sem[0], Agora.AddMinutes(1));
            checar(!ResgateManual.LigadoNaLoja(cx) && mudou2.Contains("resgate da raspadinha") && Brindes.LigadoNaLoja(cx),
                "CF-3 o painel sem a coluna (ou falso) desliga a tela e mantem o validador antigo como ele mandou");
            checar(ConfigLojaPainel.Aplicar(cx, sem[0], Agora.AddMinutes(2)) == "", "CF-4 idempotente");
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(db); } catch { } }
    }

    // ── O SERVICO: a resposta entra pelo caminho do chat ─────────────────────
    private static async Task OServico(Action<bool, string> checar)
    {
        var db = Path.Combine(Path.GetTempPath(), "resgate-serv-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var antes = Banco.CaminhoForcado;
        Banco.CaminhoForcado = db;
        var avisos = new List<AvisoDoChat>();
        void Ouvir(AvisoDoChat a) => avisos.Add(a);
        ServicoConversaChat.Avisou += Ouvir;
        try
        {
            Banco.Migrar(db);
            using (var cx = Banco.Abrir(db))
            {
                Isolamento.SemNuvem(cx);
                // a comanda em "nao imprimir": o papel nao sai na bateria, e o bonus fica na lista
                Impressoes.Gravar(cx, Impressoes.Comanda, PoliticaImpressao.Nao);
                ConversaRaspadinha.GravarSinal(cx, SinalAutomatico, DateTime.Now);
            }
            var (r, saiu, colar) = await ServicoConversaChat.ExecutarRespostaManualAsync(200, ResgateOk);
            checar(r.Status == StatusConversa.Ok && r.Acao == "resgatou" && r.Bonus?.Id == BONUS && r.ImprimirAqui && !saiu && !colar,
                "SV-1 a resposta e lida; com a comanda em 'nao imprimir' o papel nao sai (e a tela diz que nao saiu)");
            var b = ChatRaspadinha.Bonus(BONUS);
            checar(b is { Cabecalho: "normal", ComandaOnde: "caixa", Assinatura: "Ingrid", Origem: "caixa" } && b.Itens?.Count == 1 && b.Itens[0].Nome == "Cookie New York",
                "SV-2 o bonus local nasce com o cabecalho normal (o papel e deste terminal), origem caixa e a assinatura da pessoa");
            checar(ChatRaspadinha.ParaImprimir().Any(x => x.Id == BONUS), "SV-3 o bonus esta na lista do papel (sai na proxima varredura ou pelo Reimprimir)");
            checar(avisos.Any(a => a.Tipo == "resgatou" && a.Texto == "Resgate no #2607: 1 Cookie New York para Fernanda." && a.OrderUuid == OID),
                "SV-4 o aviso de uma linha do ERP chega a tela de venda como no automatico");
            checar(!avisos.Any(a => a.Tipo == "mandar"), "SV-5 a saida sdk reservada vai para a fila de envio (pelo portao), nao vira texto para colar");

            avisos.Clear();
            var operador = ResgateOk.Replace("\"como\":\"sdk\",\"estado\":\"reservada\"", "\"como\":\"operador\",\"estado\":\"operador\"")
                .Replace(BONUS, "b1540000-0000-4000-8000-000000000002").Replace("\"id\":4413", "\"id\":4414");
            var (r2, _, colar2) = await ServicoConversaChat.ExecutarRespostaManualAsync(200, operador);
            checar(r2.Status == StatusConversa.Ok && colar2 && avisos.Any(a => a.Tipo == "mandar" && a.TextoParaColar is { } t && t.StartsWith("Oi, Fernanda!", StringComparison.Ordinal)),
                "SV-6 a saida operador (loja assistida ou desligada) vira o aviso 'mandar' com o texto para colar");

            var (r3, saiu3, colar3) = await ServicoConversaChat.ExecutarRespostaManualAsync(0, "");
            checar(r3.Status != StatusConversa.Ok && !saiu3 && !colar3 && ChatRaspadinha.Bonus("b1540000-0000-4000-8000-000000000003") is null,
                "SV-7 sem resposta nada e gravado");
            var (r4, _, _) = await ServicoConversaChat.ExecutarRespostaManualAsync(200, "{\"ok\":false,\"motivo\":\"desligado\"}");
            checar(r4.Status == StatusConversa.Recusado && r4.Bonus is null, "SV-8 a recusa do servidor nao grava bonus");
        }
        finally
        {
            ServicoConversaChat.Avisou -= Ouvir;
            Banco.CaminhoForcado = antes;
            SqliteConnection.ClearAllPools();
            try { File.Delete(db); } catch { }
        }
    }

    // ── A JANELA, por reflexao, com um servidor falso ────────────────────────
    private static async Task AJanela(Action<bool, string> checar)
    {
        Exception? erro = null;
        try { await Task.Run(() => HostWpf.ExecutarAsync(() => NaJanela(checar), TimeSpan.FromSeconds(60))); }
        catch (Exception ex) { erro = ex; }
        checar(erro is null, "JN-0 a janela: os passos rodaram (" + (erro?.ToString() ?? "ok") + ")");
    }

    private static async Task NaJanela(Action<bool, string> checar)
    {
        var host = new Window
        {
            Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0,
        };
        host.Show();
        var chamadas = new List<JsonNode>();
        Task<(int, string?)> Servidor(string nome, string corpo)
        {
            var j = JsonNode.Parse(corpo)!;
            chamadas.Add(j);
            var acao = (string)j["acao"]!;
            return Task.FromResult<(int, string?)>(acao switch
            {
                "resgate_conferir" => (200, (string)j["codigo"]! switch { "" => ConferirSemCodigo, "AD-ZZZZZZ" => ConferirRecusa("nao_achado"), _ => ConferirOk }),
                "resgate_manual" => (200, ResgateOk),
                "resgate_desfazer" => (200, (string)j["codigo_totp"]! == "123456" ? DesfazerOk : "{\"ok\":false,\"motivo\":\"codigo_invalido\",\"frase\":\"Código inválido. Tente de novo.\"}"),
                _ => (500, null),
            });
        }
        var executadas = 0;
        Task<(RespostaConversa, bool, bool)> Executar(int st, string? corpo)
        {
            executadas++;
            return Task.FromResult((ConversaRaspadinha.LerResposta(st, corpo, DateTime.Now), true, false));
        }
        var perguntas = new List<string>();
        var codigosPedidos = 0;

        var tela = new Pdv.Telas.ResgateRaspadinha(host, Servidor, "Ingrid", "a0420449-0000-4000-8000-000000000001", null, OID, "2607", chatAtivo: true, Executar);
        typeof(Pdv.Telas.ResgateRaspadinha).GetField("_confirmar", P)!.SetValue(tela, (Func<string, bool>)(p => { perguntas.Add(p); return true; }));
        typeof(Pdv.Telas.ResgateRaspadinha).GetField("_pedirCodigo", P)!.SetValue(tela, (Func<string?, string?>)(_ => { codigosPedidos++; return "123456"; }));
        var janela = tela.Janela;
        janela.Left = -20000; janela.Top = -20000; janela.ShowActivated = false; janela.Opacity = 0;
        janela.Show();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        janela.UpdateLayout();

        var txt = Campo<TextBox>(tela, "_txtCodigo");
        var btnResgatar = Campo<Button>(tela, "_btnResgatar");
        var destinos = Campo<StackPanel>(tela, "_destinos");
        var sabores = Campo<WrapPanel>(tela, "_sabores");
        var chips = Campo<WrapPanel>(tela, "_chips");
        var chk = Campo<CheckBox>(tela, "_chkAvisar");
        var btnDesfazer = Campo<Button>(tela, "_btnDesfazer");

        // abriu pelo pedido: o Loaded ja conferiu sem codigo (so os pedidos e os candidatos)
        await Task.Delay(50);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        checar(chamadas.Count >= 1 && (string)chamadas[0]["acao"]! == "resgate_conferir" && (string)chamadas[0]["codigo"]! == "" && (string)chamadas[0]["ifood_order_id"]! == OID,
            "JN-1 a janela aberta pelo pedido confere sem codigo, com o pedido");
        checar(chips.Children.OfType<Button>().Select(b => (string)b.Content).SequenceEqual(new[] { "AD-RB684S", "AD-7KQ2MX" }) && !btnResgatar.IsEnabled
               && destinos.Children.OfType<RadioButton>().Count() == 2 && sabores.Children.Count == 0 && txt.Text == "",
            "JN-2 os codigos vivos da conversa aparecem como chips e os pedidos ja aparecem; sem codigo nao ha sabor e o Resgatar fica desabilitado");
        checar(janela.ActualWidth <= 1024 && janela.ActualHeight <= 768 * 0.92 + 1,
            $"JN-3 a janela cabe em 1024x768 ({janela.ActualWidth:0}x{janela.ActualHeight:0})");

        // digita um codigo que nao existe: a linha diz e nada mais aparece
        txt.Text = "AD-ZZZZZZ";
        await tela.ConferirAsync();
        checar(tela.Linha == "Não achei esse código. Confira as letras." && !btnResgatar.IsEnabled && sabores.Children.Count == 0,
            $"JN-4 codigo que nao existe: a linha da recusa e nada para escolher (linha=[{tela.Linha}] resgatar={btnResgatar.IsEnabled} sabores={sabores.Children.Count} chamadas={chamadas.Count})");

        // o codigo certo: premio, pedidos (o sugerido ja marcado), sabores
        txt.Text = "AD-RB684S";
        await tela.ConferirAsync();
        var radios = destinos.Children.OfType<RadioButton>().ToList();
        checar(tela.Linha == "🍪 Cookie Clássico para Fernanda. Vale até 14/10." && radios.Count == 3
               && radios[0].IsChecked == true && (string)radios[0].Content == "#2607 Fernanda 10:24 pronto"
               && !radios[1].IsEnabled && ((string)radios[1].Content).EndsWith("já tem prêmio", StringComparison.Ordinal)
               && (string)radios[2].Content == ResgateManual.TextoBalcao && radios[2].Tag is null,
            "JN-5 o premio na linha; o pedido sugerido ja marcado; o que ja tem premio cinza; o balcao por ultimo");
        checar(sabores.Children.Count == 3 && !btnResgatar.IsEnabled && chk.Visibility == Visibility.Visible && chk.IsChecked == true,
            "JN-6 os chips de sabor; sem sabor o Resgatar segue desabilitado; 'Avisar o cliente no chat' marcado");
        var chipNy = sabores.Children.OfType<Button>().First(c => (string)c.Tag == "New York");
        chipNy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        checar(btnResgatar.IsEnabled && Campo<List<string>>(tela, "_escolhidos").SequenceEqual(new[] { "New York" }) && chipNy.FontWeight == FontWeights.Bold,
            "JN-7 com o sabor escolhido o Resgatar habilita e o chip fica marcado");
        radios[2].IsChecked = true;
        checar(chk.Visibility == Visibility.Collapsed && btnResgatar.IsEnabled, "JN-8 no balcao nao se avisa o cliente");
        radios[0].IsChecked = true;

        // resgata: confirma em uma linha, manda o corpo certo, executa a resposta, mostra o resultado
        await tela.ResgatarAsync();
        var resg = chamadas.Last(c => (string)c["acao"]! == "resgate_manual");
        checar(perguntas.Count == 1 && perguntas[0] == "Resgatar Cookie Clássico (New York) para Fernanda no #2607?",
            "JN-9 a pergunta de confirmacao, uma linha");
        checar((string)resg["operador"]! == "Ingrid" && (string)resg["codigo"]! == "AD-RB684S" && (string)resg["destino"]!["tipo"]! == "ifood"
               && (string)resg["destino"]!["ifood_order_id"]! == OID && (string)resg["sabores"]![0]! == "New York" && (bool)resg["avisar"]!,
            "JN-10 o corpo do resgate_manual: operador, codigo, pedido, sabor, avisar");
        checar(executadas == 1 && tela.BonusId == BONUS && tela.Linha == "Resgatado. A comanda saiu." && !btnResgatar.IsVisible
               && btnDesfazer.Visibility == Visibility.Visible && txt.IsReadOnly,
            $"JN-11 a resposta e executada pelo servico e a linha diz que a comanda saiu; sobra o Desfazer ({tela.Linha})");

        // desfaz: pede o codigo do dono e manda resgate_desfazer com ele
        await tela.DesfazerAsync();
        var des = chamadas.Last(c => (string)c["acao"]! == "resgate_desfazer");
        checar(codigosPedidos == 1 && (string)des["codigo_totp"]! == "123456" && (string)des["bonus_id"]! == BONUS
               && (string)des["terminal_uuid"]! == "a0420449-0000-4000-8000-000000000001" && (string)des["operador"]! == "Ingrid"
               && tela.Linha == "Resgate desfeito. O código volta a valer." && btnDesfazer.Visibility == Visibility.Collapsed,
            "JN-12 o Desfazer pede o codigo do dono, manda resgate_desfazer e mostra a linha");

        // Esc fecha
        var fonte = PresentationSource.FromVisual(janela);
        janela.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, fonte!, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        checar(!janela.IsVisible, "JN-13 Esc fecha a janela");

        // uma segunda janela: o resgate recusado pelo servidor nao executa nada e a pessoa pode escolher outro destino
        var chamadas2 = new List<JsonNode>();
        Task<(int, string?)> Servidor2(string nome, string corpo)
        {
            var j = JsonNode.Parse(corpo)!; chamadas2.Add(j);
            return Task.FromResult<(int, string?)>((string)j["acao"]! == "resgate_manual"
                ? (200, ResgateRecusa("pedido_ja_tem", "O #2607 já tem um prêmio (AD-ABC123). Vale um por pedido."))
                : (200, ConferirCombo));
        }
        var exec2 = 0;
        var tela2 = new Pdv.Telas.ResgateRaspadinha(host, Servidor2, "Ingrid", null, "AD-CMB001", null, null, chatAtivo: false,
            (st, c) => { exec2++; return Task.FromResult((ConversaRaspadinha.LerResposta(st, c, DateTime.Now), false, false)); });
        typeof(Pdv.Telas.ResgateRaspadinha).GetField("_confirmar", P)!.SetValue(tela2, (Func<string, bool>)(_ => true));
        var j2 = tela2.Janela;
        j2.Left = -20000; j2.Top = -20000; j2.ShowActivated = false; j2.Opacity = 0;
        j2.Show();
        await Task.Delay(50);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        var sab2 = Campo<WrapPanel>(tela2, "_sabores").Children.OfType<Button>().ToList();
        var dest2 = Campo<StackPanel>(tela2, "_destinos").Children.OfType<RadioButton>().ToList();
        var btn2 = Campo<Button>(tela2, "_btnResgatar");
        checar(sab2.Count == 3 && Campo<List<string>>(tela2, "_escolhidos").SequenceEqual(new[] { "Coca-Cola" }) && dest2.Count == 1 && dest2[0].IsChecked == true
               && btn2.IsEnabled && Campo<CheckBox>(tela2, "_chkAvisar").Visibility == Visibility.Collapsed,
            "JN-14 com o codigo ja digitado: o combo abre com a Coca-Cola marcada; sem pedido aberto so o balcao, ja marcado; sem chat nao avisa");
        await tela2.ResgatarAsync();
        checar(exec2 == 0 && tela2.BonusId is null && tela2.Linha == "O #2607 já tem um prêmio (AD-ABC123). Vale um por pedido." && btn2.IsEnabled,
            "JN-15 a recusa do servidor nao executa nada, mostra a frase do servidor e deixa escolher outro destino");
        j2.Close();

        // revisao 08/10: a resposta se perdeu (sem rede DEPOIS de o servidor resgatar). A tela nao jura que nada foi
        // feito, deixa tocar de novo, e a segunda chamada volta com repetida=true: executada como um resgate normal
        var chamadas3 = new List<JsonNode>();
        var resgatesVistos = 0;
        Task<(int, string?)> Servidor3(string nome, string corpo)
        {
            var j = JsonNode.Parse(corpo)!; chamadas3.Add(j);
            if ((string)j["acao"]! != "resgate_manual") return Task.FromResult<(int, string?)>((200, ConferirOk));
            resgatesVistos++;
            return Task.FromResult<(int, string?)>(resgatesVistos == 1
                ? (0, null)                                                            // a resposta nao chegou
                : (200, ResgateOk.Replace("\"repetida\":false", "\"repetida\":true"))); // o servidor devolve o mesmo resgate
        }
        var exec3 = 0;
        var tela3 = new Pdv.Telas.ResgateRaspadinha(host, Servidor3, "Ingrid", null, "AD-RB684S", null, null, chatAtivo: true,
            (st, c) => { exec3++; return Task.FromResult((ConversaRaspadinha.LerResposta(st, c, DateTime.Now), true, false)); });
        typeof(Pdv.Telas.ResgateRaspadinha).GetField("_confirmar", P)!.SetValue(tela3, (Func<string, bool>)(_ => true));
        var j3 = tela3.Janela;
        j3.Left = -20000; j3.Top = -20000; j3.ShowActivated = false; j3.Opacity = 0;
        j3.Show();
        await Task.Delay(50);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        var sab3 = Campo<WrapPanel>(tela3, "_sabores").Children.OfType<Button>().First(c => (string)c.Tag == "New York");
        sab3.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var btn3 = Campo<Button>(tela3, "_btnResgatar");
        await tela3.ResgatarAsync();
        checar(exec3 == 0 && tela3.BonusId is null && tela3.Linha == ResgateManual.TextoSemRede && btn3.IsEnabled && !btn3.Visibility.Equals(Visibility.Collapsed),
            $"JN-16 sem resposta: nada executado, a linha pede para tocar de novo (sem jurar que nada foi feito) e o Resgatar continua ({tela3.Linha})");
        await tela3.ResgatarAsync();
        var seg = chamadas3.Where(c => (string)c["acao"]! == "resgate_manual").ToList();
        checar(seg.Count == 2 && (string)seg[1]["codigo"]! == "AD-RB684S" && (string)seg[1]["operador"]! == "Ingrid"
               && exec3 == 1 && tela3.BonusId == BONUS && tela3.Linha == "Resgatado. A comanda saiu." && !btn3.IsVisible
               && Campo<Button>(tela3, "_btnDesfazer").Visibility == Visibility.Visible,
            $"JN-17 a segunda chamada e a MESMA (codigo, operador, destino) e a resposta repetida=true e executada como um resgate normal ({tela3.Linha})");
        j3.Close();
        host.Close();
    }

    // ── O PORTAO DA CHAVE DA LOJA: cartao, toast e KDS ───────────────────────
    private static async Task OPortaoDaChave(Action<bool, string> checar)
    {
        var db = Path.Combine(Path.GetTempPath(), "resgate-tela-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var antes = Banco.CaminhoForcado;
        Banco.CaminhoForcado = db;
        Exception? erro = null;
        try
        {
            Banco.Migrar(db);
            using (var cx = Banco.Abrir(db)) Semear(cx);
            await Task.Run(() => HostWpf.ExecutarAsync(() => NaVenda(checar), TimeSpan.FromSeconds(60)));
        }
        catch (Exception ex) { erro = ex; }
        finally
        {
            Banco.CaminhoForcado = antes;
            SqliteConnection.ClearAllPools();
            try { File.Delete(db); } catch { }
        }
        checar(erro is null, "PT-0 o portao da chave: os passos rodaram (" + (erro?.ToString() ?? "ok") + ")");
    }

    private static void Semear(SqliteConnection cx)
    {
        var agora = DateTime.Now.ToString("o");
        cx.Execute("""
            INSERT INTO terminal (id, terminal_uuid, loja_id, loja_nome, cnpj, serie_nfce, ambiente, api_base, criado_em)
            VALUES (1, 'term-resgate', 'loja-1', 'American Day Savassi', '00000000000000', 1, 2, 'http://127.0.0.1:9', @a)
            """, new { a = agora });
        Isolamento.SemNuvem(cx);
        var (h, s) = Operadores.GerarHash("4321");
        cx.Execute("INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,ativo,atualizado) VALUES ('op-ingrid','Ingrid',@H,@S,'operador',1,@a)",
            new { H = h, S = s, a = agora });
        cx.Execute("""
            INSERT INTO caixa_sessao (id, business_date, operador_id, operador_nome, abertura_em, fundo_troco_cent, status)
            VALUES ('sessao-resgate', @d, 'op-ingrid', 'Ingrid', @a, 30000, 'aberto')
            """, new { d = Caixa.DiaOperacional(), a = agora });
        cx.Execute("""
            INSERT INTO produto (id, plu, nome, categoria, preco_cent, unidade, ativo, atualizado, csosn)
            VALUES ('p-01', '1', 'DONUT HOMER', 'Donuts', 900, 'UN', 1, @a, '102')
            """, new { a = agora });
        Vendas.GravarConfig(cx, "modo_fiscal", "recibo");
    }

    private static async Task NaVenda(Action<bool, string> checar)
    {
        var host = new Window
        {
            Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0,
        };
        host.Show();
        var op = new Operador("op-ingrid", "Ingrid", "operador");
        var turno = new Sessao("sessao-resgate", Caixa.DiaOperacional(), op.Id, op.Nome, DateTime.Now, Dinheiro.DeReais(300m));
        var catPromo = (string)typeof(Pdv.Telas.Venda).GetField("CategoriaPromo", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

        // 1. sem chave nenhuma: nem cartao, nem botao do toast
        var venda = new Pdv.Telas.Venda(op, turno);
        host.Content = venda; host.UpdateLayout();
        checar(!Campo<bool>(venda, "_resgatePdv") && !Campo<bool>(venda, "_raspadinhaNoCaixa") && !venda.CartaoRaspadinhaVisivel(),
            "PT-1 sem a chave (nem a antiga) a aba Promocoes nao tem o cartao da raspadinha");
        await Aviso(venda, new AvisoDoChat("humano", "#2607: Fernanda precisa de ajuda com o prêmio. Responda no chat.", null, OID, "2607"));
        checar(Campo<Button>(venda, "BtnConversaResgatar").Visibility == Visibility.Collapsed && Campo<Border>(venda, "ToastConversa").Visibility == Visibility.Visible,
            "PT-2 sem a chave o aviso 'precisa de ajuda' aparece sem o 'Resgatar aqui'");
        host.Content = null;

        // 2. o painel ligou a tela de resgate (e so ela): o cartao aparece e o toast ganha o botao
        using (var cx = Banco.Abrir())
            ConfigLojaPainel.Aplicar(cx, new ConfigLojaPainel.Linha("American Day Savassi", null, null, null, null, null, null, null, null, null,
                RaspadinhaNoCaixa: false, RaspadinhaNoChat: false, RaspadinhaResgatePdv: true), DateTime.Now);
        var venda2 = new Pdv.Telas.Venda(op, turno);
        host.Content = venda2; host.UpdateLayout();
        typeof(Pdv.Telas.Venda).GetField("_categoriaAtual", P)!.SetValue(venda2, catPromo);
        Invocar(venda2, "PintarProdutos");
        host.UpdateLayout();
        checar(Campo<bool>(venda2, "_resgatePdv") && Campo<bool>(venda2, "_raspadinhaNoCaixa") && venda2.CartaoRaspadinhaVisivel(),
            "PT-3 com a chave nova (e so ela) o cartao da raspadinha aparece na aba Promocoes");
        await Aviso(venda2, new AvisoDoChat("humano", "#2607: Fernanda precisa de ajuda com o prêmio. Responda no chat.", null, OID, "2607"));
        var btn = Campo<Button>(venda2, "BtnConversaResgatar");
        checar(btn.Visibility == Visibility.Visible && (string)btn.Content == ResgateManual.RotuloToast
               && Campo<StackPanel>(venda2, "BotoesToastConversa").Visibility == Visibility.Visible,
            "PT-4 com a chave o aviso 'precisa de ajuda' ganha o 'Resgatar aqui'");
        await Aviso(venda2, new AvisoDoChat("resgatou", "Resgate no #2607: 1 Cookie para Fernanda.", null, OID, "2607"));
        checar(btn.Visibility == Visibility.Collapsed, "PT-5 o aviso 'resgatou' nao ganha o botao");
        await Aviso(venda2, new AvisoDoChat("humano", "#?: o cliente precisa de ajuda.", null, null, null));
        checar(btn.Visibility == Visibility.Collapsed, "PT-6 aviso sem o pedido nao ganha o botao (a tela precisa do uuid)");
        host.Content = null;

        // 3. o detalhe do pedido no KDS: o botao so com o callback (o quadro so passa com a chave)
        var t = new Ticket("t1", "ifood", OID, "5077", "Fernanda Lima", "[]", "recebido", DateTime.Now, null, null);
        var d = DetalhePedido.De(t, DateTime.Now, null);
        var semBotao = new Pdv.Telas.DetalhePedidoKds(d, () => { }, () => { }, null);
        var comBotao = new Pdv.Telas.DetalhePedidoKds(d, () => { }, () => { }, () => { });
        host.Content = comBotao; host.UpdateLayout();
        var rodapeCom = Descendentes(comBotao).OfType<Button>().Where(b => b.Content is string).ToList();
        var botoes = rodapeCom.Select(b => (string)b.Content).ToList();
        host.Content = semBotao; host.UpdateLayout();
        var rodapeSem = Descendentes(semBotao).OfType<Button>().Where(b => b.Content is string).ToList();
        var botoesSem = rodapeSem.Select(b => (string)b.Content).ToList();
        checar(botoes.Contains(ResgateManual.RotuloKds) && botoes.Last() == "Fechar" && botoes.Any(b => b.Contains(AjudaIfood.TextoBotao))
               && !botoesSem.Contains(ResgateManual.RotuloKds) && botoesSem.Last() == "Fechar",
            $"PT-7 o detalhe do pedido tem o botao Raspadinha so com o callback, antes do Fechar ({string.Join("|", botoes)})");
        // revisao 08/10: sem a chave o rodape e o do 1.0.21 (16 px entre o "Fale com o iFood" e o Fechar); com ela, 16 px entre os tres
        checar(rodapeSem.Count == 2 && rodapeSem[0].Margin.Left == 0 && rodapeSem[0].Margin.Right == 8 && rodapeSem[1].Margin.Left == 8 && rodapeSem[1].Margin.Right == 0
               && rodapeCom.Count == 3 && rodapeCom[0].Margin.Right == 8 && rodapeCom[1].Margin.Left == 8 && rodapeCom[1].Margin.Right == 8 && rodapeCom[2].Margin.Left == 8 && rodapeCom[2].Margin.Right == 0,
            $"PT-8 16 px entre os botoes do rodape, com e sem a chave (sem: {string.Join("|", rodapeSem.Select(b => b.Margin))}; com: {string.Join("|", rodapeCom.Select(b => b.Margin))})");
        host.Content = null;
        host.Close();
    }

    private static async Task Aviso(Pdv.Telas.Venda venda, AvisoDoChat a)
    {
        Invocar(venda, "AvisoDaConversa", a);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
    }

    private static bool CartaoRaspadinhaVisivel(this Pdv.Telas.Venda venda)
        => Campo<Border>(venda, "CartaoRaspadinha").Visibility == Visibility.Visible;

    // ── PELO FONTE ───────────────────────────────────────────────────────────
    private static void Fonte(Action<bool, string> checar)
    {
        var raiz = Raiz();
        string Ler(params string[] partes)
        {
            if (raiz is null) return "";
            var p = Path.Combine(new[] { raiz }.Concat(partes).ToArray());
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }
        var nucleo = Ler("Pdv.Nucleo", "ResgateManual.cs");
        var janela = Ler("Telas", "ResgateRaspadinha.cs");
        var venda = Ler("Telas", "Venda.xaml.cs");
        var servico = Ler("ServicoConversaChat.cs");
        var chat = Ler("Telas", "ChatIfood.xaml.cs");
        var impressao = Ler("Impressao.cs");
        var kds = Ler("Telas", "Kds.xaml.cs");
        var main = Ler("MainWindow.xaml.cs");
        checar(nucleo.Length > 0 && janela.Length > 0, "FN-0 os arquivos novos existem");
        var proibidos = new[] { "raspadinha_validate_code", "raspadinha_redeem_public", "brinde_raspadinha_", "_comanda", "Rascunho", "Fiscal.", "Emissor", "new Pagamento", "LinhaVenda", "Finalizar(" };
        var achados = proibidos.Where(p => nucleo.Contains(p, StringComparison.Ordinal) || janela.Contains(p, StringComparison.Ordinal)).ToList();
        checar(achados.Count == 0, "FN-1 a tela e o nucleo nao conhecem o validador antigo nem tocam a venda (" + string.Join(", ", achados) + ")");
        checar(!nucleo.Contains("\"AD-\"", StringComparison.Ordinal) && !Regex.IsMatch(nucleo, @"ABCDEFGHJKMNPQRSTUVWXYZ|AD-\[A-Z|AD\[-"),
            "FN-2 o nucleo nao conhece o formato do codigo (quem acha e o ERP)");
        checar(!chat.Contains("ResgateManual", StringComparison.Ordinal) && !chat.Contains("ResgateRaspadinha", StringComparison.Ordinal)
               && !impressao.Contains("ResgateManual", StringComparison.Ordinal) && !impressao.Contains("ResgateRaspadinha", StringComparison.Ordinal),
            "FN-3 ChatIfood.xaml.cs e Impressao.cs nao tem nada desta entrega");
        foreach (var (nome, texto) in new[] { ("ResgateManual.cs", nucleo), ("ResgateRaspadinha.cs", janela), ("TestesResgateManual.cs", Ler("Pdv.Testes", "TestesResgateManual.cs")) })
            checar(texto.Length > 0 && !texto.Contains(Trav) && !texto.Contains(Traco), $"FN-4 nenhum travessao em {nome}");
        var iIni = venda.IndexOf("// ── RESGATE MANUAL DA RASPADINHA (08/10/2026, SQL 154)", StringComparison.Ordinal);
        var bloco = iIni > 0 ? venda[iIni..Math.Min(venda.Length, iIni + 4000)] : "";
        checar(bloco.Contains("public void AbrirResgate(string? codigo, string? orderId, string? numero)", StringComparison.Ordinal)
               && bloco.Contains("ServicoConversaChat.Sinal.Ativo", StringComparison.Ordinal) && bloco.Contains("Servicos.Nuvem()", StringComparison.Ordinal)
               && !bloco.Contains("_comanda", StringComparison.Ordinal) && !bloco.Contains("Rascunho", StringComparison.Ordinal),
            "FN-5 a tela de venda abre a janela com a sessao do terminal e o chat da loja, sem tocar a comanda");
        checar(venda.Contains("if (_resgatePdv) { AbrirResgate(TxtCodigoRaspadinha.Text, null, null); return; }", StringComparison.Ordinal)
               && venda.Contains("_raspadinhaNoCaixa = Nucleo.Brindes.LigadoNaLoja(cx) || _resgatePdv;", StringComparison.Ordinal),
            "FN-6 o Conferir do cartao abre a janela quando a chave nova esta ligada; com as duas chaves vale a nova");
        checar(servico.Contains("public static async Task<(RespostaConversa Resposta, bool ComandaSaiu, bool TemTextoParaColar)> ExecutarRespostaManualAsync(int st, string? corpo)", StringComparison.Ordinal)
               && Regex.Matches(servico, @"ServicoRaspadinhaChat\.ImprimirPendentesAsync\(\)").Count >= 3,
            "FN-7 o servico tem o metodo novo e o papel continua saindo pelo mesmo ImprimirPendentesAsync");
        checar(kds.Contains("public event Action<string, string>? PediuResgate;", StringComparison.Ordinal)
               && kds.Contains("t.Origem == \"ifood\" && PediuResgate is not null && ResgateLigadoNaLoja()", StringComparison.Ordinal)
               && main.Contains("k.PediuResgate += (orderId, numero) => _telaVenda?.AbrirResgate(null, orderId, numero);", StringComparison.Ordinal),
            "FN-8 o KDS so oferece o botao em pedido do iFood com a chave ligada, e a janela principal liga o quadro a tela de venda");
        var desfazer = janela.IndexOf("public async Task DesfazerAsync()", StringComparison.Ordinal);
        var trecho = desfazer > 0 ? janela[desfazer..Math.Min(janela.Length, desfazer + 1500)] : "";
        checar(trecho.Contains("_pedirCodigo(", StringComparison.Ordinal) && trecho.Contains("CorpoDesfazer(", StringComparison.Ordinal)
               && !Regex.IsMatch(trecho, @"Diag\([^;]*codigo\b[^;]*\)") && janela.Contains("PedirCodigo.Mostrar(", StringComparison.Ordinal),
            "FN-9 o Desfazer pede o codigo pela tela do autenticador do dono, manda no corpo e nunca o escreve no rastro");
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static T Campo<T>(object alvo, string nome)
        => (T)alvo.GetType().GetField(nome, P)!.GetValue(alvo)!;

    private static object? Invocar(object alvo, string metodo, params object?[] args)
        => alvo.GetType().GetMethods(P).First(m => m.Name == metodo && m.GetParameters().Length == args.Length).Invoke(alvo, args);

    private static IEnumerable<DependencyObject> Descendentes(DependencyObject o)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
        {
            var c = VisualTreeHelper.GetChild(o, i);
            yield return c;
            foreach (var n in Descendentes(c)) yield return n;
        }
    }

    private static string? Raiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj"))) return dir.FullName;
        return null;
    }
}
