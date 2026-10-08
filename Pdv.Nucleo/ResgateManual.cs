using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>O que o servidor disse de UM codigo (resgate_conferir e resgate_manual, SQL 154).</summary>
/// <param name="Motivo">"ok" ou a recusa (nao_achado, outra_loja, nao_raspou, vencido, ja_usado...).</param>
/// <param name="Escolhas">Quantos sabores o premio pede (0, 1 ou 2).</param>
/// <param name="Repete">Com 2 escolhas o mesmo sabor pode vir duas vezes.</param>
public sealed record CodigoConferido(
    string Codigo, string Motivo, string? Premio, string? NomeCurto, string? Emoji, DateTime? VenceEm,
    string? LojaCodigo, DateTime? UsadoEm, string? UsadoPor, string? ClienteNome,
    int Escolhas, bool Repete, IReadOnlyList<string> Opcoes, string Padrao, bool TemCatalogo,
    IReadOnlyList<string> ItensFixos)
{
    public bool Ok => Motivo == "ok";
}

/// <summary>Um pedido aberto da loja, para o bloco "Para onde vai".</summary>
public sealed record PedidoAberto(string IfoodOrderId, string Numero, string? Cliente, string? EntregaStatus,
    DateTime? RecebidoEm, bool Sugerido, string? PremioCodigo, string? ConversaEstado);

/// <summary>O pedido de onde a tela abriu, como o servidor o ve.</summary>
public sealed record PedidoInformado(string IfoodOrderId, string Numero, string? Cliente, bool Saiu, bool Cancelado);

/// <summary>A resposta do resgate_conferir no vocabulario do caixa. <see cref="Ok"/> falso = nada para mostrar alem da linha.</summary>
public sealed record ConferenciaManual(bool Ok, string? Motivo, bool Ligado, string Modo, bool ChatAtivo,
    CodigoConferido? Codigo, string? Frase, IReadOnlyList<PedidoAberto> Pedidos, PedidoInformado? PedidoInformado,
    IReadOnlyList<string> Candidatos);

/// <summary>A resposta do resgate_desfazer.</summary>
public sealed record DesfechoDesfazer(bool Ok, string? Motivo, string? Frase, string? Dica, string? Codigo);

/// <summary>
/// RESGATE MANUAL DA RASPADINHA DENTRO DO PDV, A PARTE PURA (08/10/2026, SQL 154).
///
/// O dono: "add dentro do PDV tb a pagina de resgate da raspadinha caso falhe pra resgatar e fazer
/// tudo dentro do pdv". Quando o chat nao resolve sozinho (codigo errado, conversa que foi para
/// uma pessoa, cliente na loja com a raspadinha no celular), a pessoa no caixa confere o codigo,
/// escolhe o pedido do iFood (ou o balcao) e o sabor, e resgata pela MESMA queima, o mesmo bonus,
/// a mesma comanda e a mesma resposta ao cliente do automatico, com o nome dela na assinatura.
///
/// Aqui moram o que a janela (Telas/ResgateRaspadinha.cs) precisa e que da para provar sem WPF:
/// a chave da loja, os corpos das tres acoes, a leitura das respostas, a regra dos sabores, os
/// textos de uma linha e a janela do desfazer. QUEM DECIDE E O SERVIDOR: este arquivo nao conhece
/// o formato do codigo, nao valida premio e nao queima nada. Resgatar NUNCA fica na fila: a queima
/// so vale com ok:true. Sem resposta (revisao 08/10) a tela NAO afirma que nada foi feito: a
/// resposta pode ter se perdido DEPOIS do commit; ela pede para tocar em Resgatar de novo, e o
/// servidor devolve o mesmo resgate (repetida = true) para o mesmo terminal, operador e destino
/// em ate 2 min, em vez de queimar duas vezes ou dizer "ja usado".
///
/// ⚠️ Nao e venda: nada aqui toca comanda, rascunho, pagamento nem nota (a suite vigia pelo fonte).
/// </summary>
public static class ResgateManual
{
    /// <summary>Config local que o painel desce por pdv_loja_config_caixa() (coluna raspadinha_resgate_pdv).</summary>
    public const string ChaveConfigLoja = "raspadinha_resgate_pdv";

    public const string Edge = ConversaRaspadinha.Edge;
    public const string AcaoConferir = "resgate_conferir";
    public const string AcaoResgatar = "resgate_manual";
    public const string AcaoDesfazer = "resgate_desfazer";

    /// <summary>O resgate tem a trava por pedido do servidor: um pouco mais que o chat (12 s).</summary>
    public static readonly TimeSpan Prazo = TimeSpan.FromSeconds(15);

    /// <summary>Ate quando a tela oferece o Desfazer (o servidor tem a mesma janela).</summary>
    public static readonly TimeSpan JanelaDesfazer = TimeSpan.FromHours(2);

    public const int MaxCodigo = 20;
    public const int MaxOperador = 40;

    /// <summary>Os avisos do chat que ganham o botao "Resgatar aqui" (so com o pedido no aviso).</summary>
    public static readonly IReadOnlySet<string> TiposDeAvisoComResgate = new HashSet<string>(StringComparer.Ordinal)
    {
        "humano", "expirou", "nao_raspou", "validador", "premio_sem_catalogo", "palavra_pedido_saiu",
    };

    // ── textos (uma linha, sem travessao) ───────────────────────────────────
    public const string TextoSemRede = "Sem resposta do servidor. Toque em Resgatar de novo: não resgata duas vezes.";
    public const string TextoSemCodigo = "Digite o código do cliente.";
    public const string TextoConferindo = "Conferindo o código...";
    public const string TextoResgatando = "Resgatando...";
    public const string TextoResgatadoComandaSaiu = "Resgatado. A comanda saiu.";
    public const string TextoResgatadoComandaNaoSaiu = "Resgatado. A comanda não saiu: toque em Reimprimir no chat.";
    public const string TextoResgatadoColar = "Resgatado. Mande esta resposta no chat.";
    public const string TextoResgatadoSemComanda = "Resgatado.";
    public const string TextoDesfeito = "Resgate desfeito. O código volta a valer.";
    public const string TextoDesfazerForaDaJanela = "Passou de 2 horas. Para desfazer, use o CRM.";
    public const string TextoDesfazerPedidoSaiu = "O pedido já saiu com o brinde. Para desfazer, use o CRM.";
    public const string TextoPedirCodigoDono = "Peça ao dono o código do autenticador.";
    public const string TextoBalcao = "Balcão (cliente na loja)";
    public const string TextoSemPedido = "Nenhum pedido aberto: só o balcão.";
    public const string TextoDesligado = "A tela de resgate está desligada nesta loja.";
    public const string RotuloToast = "Resgatar aqui";
    public const string RotuloKds = "🎟️ Raspadinha";
    public const string Titulo = "Resgate da raspadinha";

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A loja ligou a tela no painel?</summary>
    public static bool LigadoNaLoja(SqliteConnection cx) => Vendas.Config(cx, ChaveConfigLoja) == "1";

    // ── O QUE VAI (puro) ─────────────────────────────────────────────────────

    public static string CorpoConferir(string? terminal, string? codigo, string? ifoodOrderId)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["acao"] = AcaoConferir,
            ["terminal"] = Terminal(terminal),
            ["codigo"] = Limpo(codigo, MaxCodigo),
            ["ifood_order_id"] = string.IsNullOrWhiteSpace(ifoodOrderId) ? null : ifoodOrderId.Trim(),
        }, Json);

    /// <param name="destinoBalcao">true = balcao; false = o pedido do iFood em <paramref name="ifoodOrderId"/>.</param>
    public static string CorpoResgatar(string? terminal, string? operador, string? codigo, bool destinoBalcao,
        string? ifoodOrderId, IReadOnlyList<string> sabores, bool avisar)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["acao"] = AcaoResgatar,
            ["terminal"] = Terminal(terminal),
            ["operador"] = Operador(operador),
            ["codigo"] = Limpo(codigo, MaxCodigo),
            ["destino"] = destinoBalcao
                ? new Dictionary<string, object?> { ["tipo"] = "balcao" }
                : new Dictionary<string, object?> { ["tipo"] = "ifood", ["ifood_order_id"] = (ifoodOrderId ?? "").Trim() },
            ["sabores"] = sabores.Select(s => s.Trim()).Where(s => s.Length > 0).Take(2).ToList(),
            ["avisar"] = avisar,
        }, Json);

    /// <summary>O codigo do autenticador vai no corpo e NUNCA no rastro (ver <see cref="Diag"/>).</summary>
    public static string CorpoDesfazer(string? terminal, string? terminalUuid, string? operador, string bonusId,
        string? motivo, string codigoTotp)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["acao"] = AcaoDesfazer,
            ["terminal"] = Terminal(terminal),
            ["terminal_uuid"] = string.IsNullOrWhiteSpace(terminalUuid) ? null : terminalUuid.Trim(),
            ["operador"] = Operador(operador),
            ["bonus_id"] = bonusId,
            ["motivo"] = string.IsNullOrWhiteSpace(motivo) ? null : motivo.Trim()[..Math.Min(120, motivo.Trim().Length)],
            ["codigo_totp"] = new string((codigoTotp ?? "").Where(char.IsDigit).ToArray()),
        }, Json);

    private static string Terminal(string? t)
    {
        var s = string.IsNullOrWhiteSpace(t) ? ConversaRaspadinha.NomeDoTerminal() : t.Trim();
        return s.Length <= ConversaRaspadinha.TetoTerminal ? s : s[..ConversaRaspadinha.TetoTerminal];
    }

    /// <summary>O nome do operador vira a assinatura da comanda: sem travessao, ate 40, nunca vazio.</summary>
    public static string Operador(string? nome)
    {
        var s = (nome ?? "").Replace((char)0x2014, '-').Replace((char)0x2013, '-').Trim();
        if (s.Length == 0) s = "Caixa";
        return s.Length <= MaxOperador ? s : s[..MaxOperador];
    }

    private static string Limpo(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length <= max ? t : t[..max];
    }

    // ── O QUE VOLTA (puro) ───────────────────────────────────────────────────

    /// <summary>
    /// A resposta do resgate_conferir. Status -1/0/5xx = sem rede (nada e mostrado alem da linha);
    /// 401/403 = sem permissao; 404 = a borda sem publicar. Corpo ilegivel NAO e "ok".
    /// </summary>
    public static ConferenciaManual LerConferencia(int status, string? corpo)
    {
        if (MotivoDoStatus(status) is { } m) return Falha(m);
        try
        {
            using var doc = JsonDocument.Parse(corpo ?? "");
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return Falha("resposta_ilegivel");
            if (!Bool(r, "ok")) return Falha(Texto(r, "motivo") ?? "erro") with { Frase = Texto(r, "frase") };
            var pedidos = new List<PedidoAberto>();
            if (r.TryGetProperty("pedidos", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var p in ps.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    var oid = Texto(p, "ifood_order_id"); var num = TextoOuNumero(p, "numero");
                    if (oid is null || num is null) continue;
                    pedidos.Add(new PedidoAberto(oid, num, Texto(p, "cliente"), Texto(p, "entrega_status"), Data(p, "recebido_em"),
                        Bool(p, "sugerido"), Texto(p, "premio_codigo"), Texto(p, "conversa_estado")));
                }
            PedidoInformado? inf = null;
            if (r.TryGetProperty("pedido_informado", out var pi) && pi.ValueKind == JsonValueKind.Object
                && Texto(pi, "ifood_order_id") is { } ioid)
                inf = new PedidoInformado(ioid, TextoOuNumero(pi, "numero") ?? "?", Texto(pi, "cliente"), Bool(pi, "saiu"), Bool(pi, "cancelado"));
            var cands = new List<string>();
            if (r.TryGetProperty("candidatos", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var c in cs.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } s) cands.Add(s.Trim());
            CodigoConferido? cod = null;
            if (r.TryGetProperty("codigo", out var cv) && cv.ValueKind == JsonValueKind.Object) cod = LerCodigo(cv);
            return new ConferenciaManual(true, null, Bool(r, "ligado"), Texto(r, "modo") ?? "desligado", Bool(r, "chat_ativo"),
                cod, Texto(r, "frase"), pedidos, inf, cands);
        }
        catch { return Falha("resposta_ilegivel"); }

        static ConferenciaManual Falha(string m) => new(false, m, false, "desligado", false, null, null,
            Array.Empty<PedidoAberto>(), null, Array.Empty<string>());
    }

    /// <summary>O codigo como o servidor o descreve (o mesmo objeto no conferir e na recusa do resgatar).</summary>
    public static CodigoConferido? LerCodigo(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object) return null;
        var opcoes = new List<string>();
        if (c.TryGetProperty("opcoes", out var ov) && ov.ValueKind == JsonValueKind.Array)
            foreach (var o in ov.EnumerateArray())
                if (o.ValueKind == JsonValueKind.String && o.GetString() is { Length: > 0 } s) opcoes.Add(s);
        var fixos = new List<string>();
        if (c.TryGetProperty("itens_fixos", out var fv) && fv.ValueKind == JsonValueKind.Array)
            foreach (var o in fv.EnumerateArray())
                if (o.ValueKind == JsonValueKind.String && o.GetString() is { Length: > 0 } s) fixos.Add(s);
        var escolhas = (int)Math.Clamp(Numero(c, "escolhas") ?? 0, 0, 2);
        return new CodigoConferido(Texto(c, "codigo") ?? "", Texto(c, "motivo") ?? "nao_achado",
            Texto(c, "premio"), Texto(c, "nome_curto"), Texto(c, "emoji"), Data(c, "vence_em"), Texto(c, "loja_codigo"),
            Data(c, "usado_em"), Texto(c, "usado_por"), Texto(c, "cliente_nome"),
            escolhas, Bool(c, "repete") || escolhas >= 2, opcoes, Texto(c, "padrao") ?? "", Bool(c, "tem_catalogo"), fixos);
    }

    /// <summary>O motivo e o codigo da RECUSA do resgate_manual (ok:false). Nulo quando a resposta e ok.</summary>
    public static (string Motivo, string? Frase, CodigoConferido? Codigo)? LerRecusa(int status, string? corpo)
    {
        if (MotivoDoStatus(status) is { } m) return (m, null, null);
        try
        {
            using var doc = JsonDocument.Parse(corpo ?? "");
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return ("resposta_ilegivel", null, null);
            if (Bool(r, "ok")) return null;
            CodigoConferido? cod = r.TryGetProperty("codigo", out var cv) ? LerCodigo(cv) : null;
            return (Texto(r, "motivo") ?? "erro", Texto(r, "frase"), cod);
        }
        catch { return ("resposta_ilegivel", null, null); }
    }

    /// <summary>A frase e o prazo do desfazer que vieram na resposta ok do resgate_manual.</summary>
    public static (string? Frase, DateTime? DesfazerAte, bool PeloDono) LerExtrasDoResgate(string? corpo)
    {
        try
        {
            using var doc = JsonDocument.Parse(corpo ?? "");
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return (null, null, false);
            return (Texto(r, "frase"), Data(r, "desfazer_ate"), Bool(r, "pelo_dono"));
        }
        catch { return (null, null, false); }
    }

    public static DesfechoDesfazer LerDesfazer(int status, string? corpo)
    {
        if (MotivoDoStatus(status) is { } m) return new DesfechoDesfazer(false, m, TextoSemRedeDesfazer, null, null);
        try
        {
            using var doc = JsonDocument.Parse(corpo ?? "");
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return new DesfechoDesfazer(false, "resposta_ilegivel", TextoSemRedeDesfazer, null, null);
            var ok = Bool(r, "ok");
            return new DesfechoDesfazer(ok, ok ? null : Texto(r, "motivo") ?? "erro", Texto(r, "frase"), Texto(r, "dica"), Texto(r, "codigo"));
        }
        catch { return new DesfechoDesfazer(false, "resposta_ilegivel", TextoSemRedeDesfazer, null, null); }
    }

    /// <summary>Sem resposta o desfazer tambem pode ter acontecido: tocar de novo devolve "ja desfeito" se foi.</summary>
    public const string TextoSemRedeDesfazer = "Sem resposta do servidor. Toque em Desfazer de novo para conferir.";

    /// <summary>-1/0/5xx/408/425/429 = sem_rede; 401/403 = sem_permissao; 404 = nuvem_sem_recurso; 2xx e 4xx = nulo (o corpo decide).</summary>
    public static string? MotivoDoStatus(int status) => status switch
    {
        -1 or 0 => "sem_rede",
        401 or 403 => "sem_permissao",
        404 => "nuvem_sem_recurso",
        >= 500 or 408 or 425 or 429 => "sem_rede",
        _ => null,
    };

    // ── OS SABORES (puro) ────────────────────────────────────────────────────

    /// <summary>O premio esta completo? 0 escolhas = sempre; senao exatamente <c>escolhas</c> sabores da lista.</summary>
    public static bool SaboresCompletos(CodigoConferido c, IReadOnlyList<string> escolhidos)
    {
        if (c.Escolhas <= 0) return true;
        if (escolhidos.Count != c.Escolhas) return false;
        return escolhidos.All(s => c.Opcoes.Contains(s, StringComparer.Ordinal));
    }

    /// <summary>O que ja vem marcado quando a tela abre: o padrao do catalogo (Combo Coxinha: Coca-Cola), se e uma opcao.</summary>
    public static IReadOnlyList<string> PadraoMarcado(CodigoConferido c)
        => c.Escolhas == 1 && c.Padrao.Length > 0 && c.Opcoes.Contains(c.Padrao, StringComparer.Ordinal)
            ? new[] { c.Padrao } : Array.Empty<string>();

    /// <summary>Um toque num chip: entra (ate o teto, repetindo se pode) ou sai (a ultima ocorrencia).</summary>
    public static IReadOnlyList<string> Tocar(CodigoConferido c, IReadOnlyList<string> escolhidos, string sabor)
    {
        var lista = escolhidos.ToList();
        if (c.Escolhas <= 1)
            return lista.Count == 1 && lista[0] == sabor ? Array.Empty<string>() : new[] { sabor };
        var ja = lista.Count(x => x == sabor);
        if (ja > 0 && (lista.Count >= c.Escolhas || !c.Repete))
        {
            lista.RemoveAt(lista.LastIndexOf(sabor));
            return lista;
        }
        if (lista.Count >= c.Escolhas) lista.RemoveAt(0);
        lista.Add(sabor);
        return lista;
    }

    public static string LinhaDeApoioDosSabores(CodigoConferido c) => c.Escolhas switch
    {
        <= 0 => "",
        1 => "Escolha 1 sabor.",
        _ => c.Repete ? $"Escolha {c.Escolhas} sabores. Pode repetir." : $"Escolha {c.Escolhas} sabores.",
    };

    // ── OS TEXTOS DA TELA (puro, uma linha, sem travessao) ───────────────────

    /// <summary>"🍪 Cookie Clássico para Fernanda. Vale até 14/10."</summary>
    public static string LinhaDoPremio(CodigoConferido c)
    {
        var nome = c.NomeCurto ?? c.Premio ?? "Prêmio";
        var quem = PrimeiroNome(c.ClienteNome);
        var s = (string.IsNullOrWhiteSpace(c.Emoji) ? "" : c.Emoji.Trim() + " ") + nome
                + (quem is null ? "" : " para " + quem) + "."
                + (c.VenceEm is { } v ? $" Vale até {v:dd/MM}." : "");
        return Limpa(s);
    }

    /// <summary>A recusa em uma linha (as mesmas do chat). O servidor manda a dele; esta e a do caixa quando falta.</summary>
    public static string LinhaDaRecusa(string? motivo, CodigoConferido? c, string? numero, string? fraseDoServidor)
    {
        var n = "#" + (string.IsNullOrWhiteSpace(numero) ? "?" : numero.Trim());
        var s = motivo switch
        {
            "nao_achado" => "Não achei esse código. Confira as letras.",
            "outra_loja" => $"Esse código é da {c?.LojaCodigo ?? "outra loja"}. Aqui não vale.",
            "nao_raspou" => "O cliente ainda não raspou essa raspadinha.",
            "vencido" => c?.VenceEm is { } v ? $"Esse código venceu em {v:dd/MM}." : "Esse código já venceu.",
            "ja_usado" => "Esse código já foi usado" + (c?.UsadoEm is { } u ? $" em {u:dd/MM} às {u:HH:mm}" : "")
                          + (c?.UsadoPor is { Length: > 0 } p ? $" por {p}" : "") + ".",
            "pedido_ja_tem" => $"O {n} já tem um prêmio. Vale um por pedido.",
            "pedido_saiu" => $"O {n} já saiu. Resgate no balcão ou guarde para o próximo pedido.",
            "pedido_cancelado" => $"O {n} foi cancelado. O código continua valendo.",
            "sabor_invalido" => "Escolha o sabor da lista.",
            "desligado" => TextoDesligado,
            "nao_autorizado" => "Esse usuário não pode resgatar agora. Chame o gerente.",
            "sem_rede" or "resposta_ilegivel" or "erro_no_servidor" => TextoSemRede,
            "sem_permissao" => "Este caixa foi barrado pelo servidor. Chame o gerente.",
            "nuvem_sem_recurso" => "O servidor ainda não tem esta tela. Avise o suporte.",
            _ => null,
        };
        // a frase do servidor vale quando ela existe e o caixa nao tem uma melhor para o motivo
        if (s is null || (motivo is "pedido_ja_tem" && fraseDoServidor is { Length: > 0 }))
            s = fraseDoServidor is { Length: > 0 } ? fraseDoServidor : (s ?? "Não deu para resgatar agora. Tente de novo.");
        return Limpa(s);
    }

    /// <summary>"#2607  Fernanda  19:24  preparando"</summary>
    public static string LinhaDoPedido(PedidoAberto p)
    {
        var partes = new List<string> { "#" + p.Numero };
        if (PrimeiroNome(p.Cliente) is { } nome) partes.Add(nome);
        if (p.RecebidoEm is { } r) partes.Add(r.ToString("HH:mm", CultureInfo.InvariantCulture));
        partes.Add(p.EntregaStatus switch
        {
            null or "" => "recebido",
            "pronto" => "pronto",
            "despachado" => "saiu",
            "concluido" => "entregue",
            var e => e,
        });
        if (p.PremioCodigo is { Length: > 0 }) partes.Add("já tem prêmio");
        return Limpa(string.Join("  ", partes));
    }

    /// <summary>"Resgatar Cookie Clássico (New York) para Fernanda no #2607?" ou "... no balcão?"</summary>
    public static string PerguntaDeConfirmacao(CodigoConferido c, IReadOnlyList<string> sabores, PedidoAberto? destino)
    {
        var nome = c.NomeCurto ?? c.Premio ?? "o prêmio";
        var sab = sabores.Count > 0 ? " (" + string.Join(", ", sabores) + ")" : "";
        var quem = PrimeiroNome(destino?.Cliente ?? c.ClienteNome);
        var onde = destino is null ? "no balcão" : "no #" + destino.Numero;
        return Limpa($"Resgatar {nome}{sab}" + (quem is null ? "" : $" para {quem}") + $" {onde}?");
    }

    /// <summary>A linha depois do resgate: a comanda saiu, nao saiu, ou a pessoa tem de colar a resposta.</summary>
    public static string TextoResgatado(bool comandaEraDaqui, bool comandaSaiu, bool temTextoParaColar)
    {
        if (temTextoParaColar) return TextoResgatadoColar;
        if (!comandaEraDaqui) return TextoResgatadoSemComanda;
        return comandaSaiu ? TextoResgatadoComandaSaiu : TextoResgatadoComandaNaoSaiu;
    }

    /// <summary>Nulo = pode desfazer agora; senao a linha que diz por que nao (a mesma regra do servidor).</summary>
    public static string? MotivoParaNaoDesfazer(DateTime resgatadoEm, DateTime agora, string? kdsSituacao)
    {
        if (kdsSituacao == "pedido_saiu") return TextoDesfazerPedidoSaiu;
        if (agora - resgatadoEm > JanelaDesfazer) return TextoDesfazerForaDaJanela;
        return null;
    }

    /// <summary>A linha de uma recusa do desfazer (o servidor manda a frase; esta e a do caixa quando falta).</summary>
    public static string LinhaDoDesfazer(DesfechoDesfazer d)
    {
        if (d.Ok) return TextoDesfeito;
        if (d.Frase is { Length: > 0 }) return Limpa(d.Frase);
        return Limpa(d.Motivo switch
        {
            "codigo_invalido" => Autorizacao.AvisoCodigoInvalido,
            "codigo_vencido" => Autorizacao.AvisoCodigoVencido,
            "fora_da_janela" => TextoDesfazerForaDaJanela,
            "pedido_saiu" => TextoDesfazerPedidoSaiu,
            "ja_desfeito" => "Esse resgate já foi desfeito.",
            "muitas_tentativas" => "Muitas tentativas. Aguarde 10 minutos.",
            "sem_rede" or "resposta_ilegivel" => TextoSemRedeDesfazer,
            _ => "Não deu para desfazer agora. Tente de novo.",
        });
    }

    /// <summary>A linha do rastro (chat-conversa.txt): nunca o nome do cliente, nunca o codigo do dono.</summary>
    public static string Diag(string acao, string? codigo, string? destino, int status, string? motivoOuAcao, string? bonusId)
    {
        var cod = string.IsNullOrEmpty(codigo) ? "-" : codigo.Length <= 7 ? codigo : codigo[..7] + "..";
        return $"resgate manual {acao} codigo={cod} destino={destino ?? "-"} http={status} acao={motivoOuAcao ?? "-"} bonus={(string.IsNullOrEmpty(bonusId) ? "-" : bonusId.Length <= 12 ? bonusId : bonusId[..12])}";
    }

    /// <summary>"ANA PAULA SOUZA" vira "Ana"; vazio vira nulo.</summary>
    public static string? PrimeiroNome(string? nome)
    {
        var p = (nome ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (p is null || p.Length < 2) return null;
        p = p.ToLowerInvariant();
        return char.ToUpperInvariant(p[0]) + p[1..];
    }

    /// <summary>Uma linha, sem travessao, ate 160.</summary>
    public static string Limpa(string s)
    {
        var t = s.Replace((char)0x2014, ',').Replace((char)0x2013, ',').Replace("\r", " ").Replace("\n", " ").Trim();
        while (t.Contains("  ")) t = t.Replace("  ", " ");
        return t.Length <= 160 ? t : t[..157] + "...";
    }

    // ── json ─────────────────────────────────────────────────────────────────
    private static string? Texto(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s && s.Trim().Length > 0 ? s.Trim() : null;
    private static string? TextoOuNumero(JsonElement e, string k)
        => e.TryGetProperty(k, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() is { } s && s.Trim().Length > 0 ? s.Trim() : null,
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;
    private static long? Numero(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    private static bool Bool(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    private static DateTime? Data(JsonElement e, string k)
    {
        var s = Texto(e, k);
        if (s is null) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d.LocalDateTime : null;
    }
}
