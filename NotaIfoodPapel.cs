using System.Globalization;
using System.Text.Json;

namespace Pdv.Nucleo;

/// <summary>
/// A NOTA DO iFOOD SAINDO NO PAPEL DO CAIXA (16/09/2026).
///
/// Pedido do dono: "de manhã o movimento é fraco. coloca pra emitir as 5 primeiras notas
/// e imprimir no caixa. depois das 5 primeiras a gente analisa e corrige o que tiver que
/// corrigir, e volta pra emissão em nuvem."
///
/// A nota do pedido de iFood é montada e autorizada NA NUVEM, sem passar por este PC. O
/// caixa não emite nada aqui: ele só desenha no papel um cupom que JÁ está autorizado,
/// para o dono conferir com o documento na mão. Quem decide quais notas saem no papel é o
/// servidor (contador de conferência em <c>nfce_ifood_config</c>); o caixa só pergunta
/// "tem papel para mim?" a cada puxada do delivery.
///
/// Esta classe é PURA de propósito: transforma a resposta da RPC em
/// <see cref="DadosCupom"/>, que é o mesmo objeto do cupom da venda do balcão. Assim o
/// papel do iFood sai com o MESMO desenho, o mesmo QR e o mesmo bloco fiscal do cupom que
/// a loja já conhece, sem uma segunda cópia do layout para divergir.
///
/// Nada aqui fala com a rede nem com a impressora: <c>Nuvem</c> busca, <c>Servicos</c>
/// imprime. Sem isso não daria para provar o desenho na bateria.
/// </summary>
public sealed record PapelNotaIfood(string OrderId, DadosCupom Cupom);

/// <summary>
/// Resposta da reimpressão pelo número do pedido (1.0.61, SQL 245): o papel, ou a frase
/// do servidor dizendo por que não tem (cancelada, ainda sem nota, de outra loja...).
/// </summary>
public sealed record NotaIfoodDoPedido(bool Ok, string Mensagem, string? Pedido, PapelNotaIfood? Papel);

/// <summary>O link público da nota (americanday.delivery/n/CODIGO), ou a frase do servidor.</summary>
public sealed record LinkDaNotaIfood(bool Ok, string Mensagem, string? Pedido, string? Url);

public static class NotaIfoodPapel
{
    /// <summary>RPC que entrega o que falta imprimir (e reserva por 2 minutos).</summary>
    public const string Rpc = "nfce_ifood_papeis";

    /// <summary>RPC que confirma o papel, ou conta por que ele não saiu.</summary>
    public const string RpcConfirmar = "nfce_ifood_papel_impresso";

    /// <summary>1.0.61 (SQL 245): a nota do pedido pelo número curto, para reimprimir.</summary>
    public const string RpcDoPedido = "nfce_ifood_nota_do_pedido";

    /// <summary>1.0.61 (SQL 245): o link público da nota, para mandar ao cliente.</summary>
    public const string RpcLink = "nfce_ifood_link_gerar";

    /// <summary>O único endereço que o caixa aceita copiar (o servidor monta; aqui só confere).</summary>
    public const string BaseDoLink = "https://americanday.delivery/n/";

    /// <summary>Quando a nuvem não respondeu (sem internet, sessão caída, erro do servidor).</summary>
    public const string SemResposta = "Não consegui falar com a nuvem agora. Confira a internet e tente de novo.";

    /// <summary>
    /// O número que o operador digitou, como o servidor procura: só os dígitos, sem zeros
    /// à esquerda ("#0055" vira "55"). Vazio ou maior que 12 dígitos é null (nem pergunta).
    /// </summary>
    public static string? NumeroDoPedido(string? digitado)
    {
        var d = new string((digitado ?? "").Where(char.IsDigit).ToArray()).TrimStart('0');
        return d.Length is > 0 and <= 12 ? d : null;
    }

    private static (bool Ok, string Mensagem, string? Pedido, JsonElement Raiz, JsonDocument? Doc) Abrir(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (false, SemResposta, null, default, null);
        try
        {
            var doc = JsonDocument.Parse(json);
            var raiz = doc.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object) { doc.Dispose(); return (false, SemResposta, null, default, null); }
            var ok = raiz.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            var msg = Texto(raiz, "mensagem");
            var pedido = Texto(raiz, "display_id");
            return (ok, string.IsNullOrWhiteSpace(msg) ? (ok ? "" : SemResposta) : msg!, pedido, raiz, doc);
        }
        catch { return (false, SemResposta, null, default, null); }
    }

    /// <summary>
    /// Lê a resposta de <see cref="RpcDoPedido"/>: {ok, display_id, papeis:[linha]} com a
    /// MESMA linha de <see cref="Rpc"/>, que <see cref="Ler"/> já transforma em cupom. Ok sem
    /// papel que dê para imprimir vira falha (nunca "imprimi" sem papel).
    /// </summary>
    public static NotaIfoodDoPedido LerDoPedido(string? json)
    {
        var (ok, msg, pedido, raiz, doc) = Abrir(json);
        using (doc)
        {
            if (!ok) return new NotaIfoodDoPedido(false, msg, pedido, null);
            var papeis = raiz.TryGetProperty("papeis", out var p) && p.ValueKind == JsonValueKind.Array
                ? Ler(p.GetRawText()) : Array.Empty<PapelNotaIfood>();
            return papeis.Count == 0
                ? new NotaIfoodDoPedido(false, SemResposta, pedido, null)
                : new NotaIfoodDoPedido(true, "", pedido, papeis[0]);
        }
    }

    /// <summary>
    /// Lê a resposta de <see cref="RpcLink"/>. Só aceita um link que comece em
    /// <see cref="BaseDoLink"/> e termine num código de 10 caracteres: o caixa nunca põe na
    /// área de transferência um endereço que não seja o da nossa página.
    /// </summary>
    public static LinkDaNotaIfood LerLink(string? json)
    {
        var (ok, msg, pedido, raiz, doc) = Abrir(json);
        using (doc)
        {
            if (!ok) return new LinkDaNotaIfood(false, msg, pedido, null);
            var url = Texto(raiz, "url") ?? "";
            var codigo = url.StartsWith(BaseDoLink, StringComparison.Ordinal) ? url[BaseDoLink.Length..] : "";
            var valido = codigo.Length == 10 && codigo.All(ch => "23456789ABCDEFGHJKMNPQRSTUVWXYZ".Contains(ch));
            return valido
                ? new LinkDaNotaIfood(true, "", pedido, url)
                : new LinkDaNotaIfood(false, SemResposta, pedido, null);
        }
    }

    /// <summary>
    /// tPag da NFC-e em português. Os códigos são os da Receita; o que o cliente lê no
    /// papel é a palavra. Código que não está aqui sai como "Outros", nunca em branco.
    /// </summary>
    public static string FormaDePagamento(string? tPag) => (tPag ?? "").Trim() switch
    {
        "01" => "Dinheiro",
        "02" => "Cheque",
        "03" => "Crédito",
        "04" => "Débito",
        "05" => "Crédito da loja",
        "10" => "Vale alimentação",
        "11" => "Vale refeição",
        "12" => "Vale presente",
        "13" => "Vale combustível",
        "15" => "Boleto",
        "16" => "Depósito",
        "17" => "PIX",
        "18" => "Transferência",
        "19" => "Cashback",
        "90" => "Sem pagamento",
        _    => "Outros",
    };

    private static string? Texto(JsonElement e, string nome) =>
        e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal Numero(JsonElement e, string nome)
    {
        if (!e.TryGetProperty(nome, out var v)) return 0m;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : 0m,
            JsonValueKind.String => decimal.TryParse(v.GetString(), NumberStyles.Any,
                                        CultureInfo.InvariantCulture, out var s) ? s : 0m,
            _ => 0m,
        };
    }

    private static int Inteiro(JsonElement e, string nome)
    {
        var n = Numero(e, nome);
        return n > int.MaxValue || n < int.MinValue ? 0 : (int)n;
    }

    /// <summary>
    /// Lê a resposta de <see cref="Rpc"/>. Linha quebrada (JSON estranho, sem order_id)
    /// é PULADA em vez de derrubar as outras: uma nota mal formada não pode segurar o
    /// papel das outras quatro. Resposta que não é lista devolve vazio.
    /// </summary>
    public static IReadOnlyList<PapelNotaIfood> Ler(string? json)
    {
        var papeis = new List<PapelNotaIfood>();
        if (string.IsNullOrWhiteSpace(json)) return papeis;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return papeis;
            foreach (var linha in doc.RootElement.EnumerateArray())
            {
                try
                {
                    var papel = Uma(linha);
                    if (papel is not null) papeis.Add(papel);
                }
                catch { /* linha torta não derruba as outras */ }
            }
        }
        catch { return papeis; }
        return papeis;
    }

    private static PapelNotaIfood? Uma(JsonElement linha)
    {
        var orderId = Texto(linha, "order_id");
        if (string.IsNullOrWhiteSpace(orderId)) return null;

        var emit = linha.TryGetProperty("emitente", out var em) && em.ValueKind == JsonValueKind.Object
            ? em : default;

        var itens = new List<ItemCupom>();
        if (linha.TryGetProperty("itens", out var lista) && lista.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in lista.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;
                var desc = Texto(it, "descricao") ?? Texto(it, "nome_ifood") ?? "item";
                var qtd  = Numero(it, "qtd");
                if (qtd <= 0) qtd = 1m;
                var vUnit = Numero(it, "vUnit");
                var vProd = Numero(it, "vProd");
                if (vProd <= 0) vProd = Math.Round(vUnit * qtd, 2, MidpointRounding.AwayFromZero);
                // ItemCupom.Total e LIQUIDO, ja sem o desconto: e assim que a venda do
                // balcao monta (Pagamento.xaml.cs) e e o que Impressao confere contra o
                // total da venda. Aqui vinha o vProd CHEIO, e o cupom do iFood, que tem
                // cupom da loja por item, era recusado na hora de imprimir com "os itens
                // somam R$ 21,90 e o total e R$ 16,90". Medido em 17/09/2026: 10 notas
                // autorizadas na Receita e nenhuma impressa, todas com a diferenca igual
                // ao desconto. O desconto continua indo separado, para sair na linha.
                var vDesc = Numero(it, "vDesc");
                if (vDesc < 0) vDesc = 0m;
                if (vDesc > vProd) vDesc = vProd;
                itens.Add(new ItemCupom(
                    Codigo: Texto(it, "codigo") ?? "",
                    Descricao: desc,
                    Qtd: Quantidade.DeDecimal(qtd),
                    Unidade: (Texto(it, "unidade") ?? "UN").Trim(),
                    Unitario: Dinheiro.DeReais(vUnit),
                    Total: Dinheiro.DeReais(vProd - vDesc),
                    Desconto: Dinheiro.DeReais(vDesc)));
            }
        }

        var pagamentos = new List<PagamentoCupom>();
        if (linha.TryGetProperty("pagamentos", out var pgs) && pgs.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pgs.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                var rotulo = Texto(p, "xPag");
                if (string.IsNullOrWhiteSpace(rotulo)) rotulo = FormaDePagamento(Texto(p, "tPag"));
                pagamentos.Add(new PagamentoCupom(rotulo!, Dinheiro.DeReais(Numero(p, "valor"))));
            }
        }

        var vNf = Numero(linha, "v_nf");
        var total = vNf > 0 ? Dinheiro.DeReais(vNf) : new Dinheiro(itens.Sum(i => i.Total.Centavos));

        var quando = DateTime.Now;
        var autorizada = Texto(linha, "autorizada_em");
        if (!string.IsNullOrWhiteSpace(autorizada) &&
            DateTimeOffset.TryParse(autorizada, CultureInfo.InvariantCulture,
                                    DateTimeStyles.AssumeUniversal, out var dto))
            quando = dto.ToLocalTime().DateTime;

        var cupom = new DadosCupom(
            EmitenteNome: emit.ValueKind == JsonValueKind.Object ? Texto(emit, "nome") : null,
            EmitenteCnpj: emit.ValueKind == JsonValueKind.Object ? Texto(emit, "cnpj") : null,
            EmitenteIe: emit.ValueKind == JsonValueKind.Object ? Texto(emit, "ie") : null,
            EmitenteEndereco: emit.ValueKind == JsonValueKind.Object ? Texto(emit, "endereco") : null,
            Numero: Inteiro(linha, "numero"),
            Serie: Inteiro(linha, "serie"),
            Chave: Texto(linha, "chave"),
            Emissao: quando,
            QrCode: Texto(linha, "qr_code"),
            TpAmb: linha.TryGetProperty("tp_amb", out var amb) && amb.ValueKind == JsonValueKind.Number
                   ? amb.GetInt32() : null,
            Itens: itens,
            Total: total,
            VNf: vNf > 0 ? vNf : null,
            Pagamentos: pagamentos,
            // iFood é pago no aplicativo: não há dinheiro na gaveta nem troco a devolver.
            Recebido: total,
            Documento: Texto(linha, "documento"),
            Contingencia: false,
            // O bloco do operador vira a etiqueta de ORIGEM: quem lê o papel precisa saber
            // na hora que esta nota não é uma venda do balcão, é do iFood.
            Operador: "iFood",
            Protocolo: Texto(linha, "protocolo"),
            ProtocoloEm: quando);

        return new PapelNotaIfood(orderId!, cupom);
    }
}
