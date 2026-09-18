using System;
using System.Linq;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A NOTA DO iFOOD SAINDO NO PAPEL DO CAIXA (16/09/2026).
///
/// Pedido do dono: "de manhã o movimento é fraco. coloca pra emitir as 5 primeiras notas e
/// imprimir no caixa. depois das 5 primeiras a gente analisa e corrige o que tiver que
/// corrigir, e volta pra emissão em nuvem."
///
/// O que estes testes travam: a resposta do servidor vira o MESMO cupom da venda do balcão
/// (com chave, protocolo e QR, não um recibo sem valor fiscal), o código de pagamento vira
/// palavra, uma linha torta não leva as outras junto, e o papel realmente sai na puxada do
/// delivery, e não numa tela que alguém precise deixar aberta.
/// </summary>
public static class TestesNotaIfoodPapel
{
    private const string UmaLinha = """
    [{"order_id":"abc-123","numero":4412,"serie":1,
      "chave":"31260912345678000199650010000044121000044120",
      "protocolo":"131260000123456","autorizada_em":"2026-09-16T12:05:00+00:00","tp_amb":1,
      "v_nf":30.00,"documento":null,"qr_code":"http://nfce.fazenda.mg.gov.br/portalnfce?p=3126...",
      "emitente":{"nome":"MM FOOD SERVICE LTDA","cnpj":"62177839000238","ie":"0012345670012",
                  "endereco":"Rua Antonio de Albuquerque, 100, Savassi, Belo Horizonte, MG"},
      "itens":[{"codigo":"12","descricao":"DONUT HOMER","qtd":2,"vUnit":15.50,"vProd":31.00,"unidade":"UN"},
               {"codigo":"","descricao":"Seringa de Nutella","qtd":1,"vUnit":4.00}],
      "pagamentos":[{"tPag":"17","valor":30.00}]}]
    """;

    /// <summary>
    /// O caso REAL que o caixa recusou em 17/09/2026. As dez primeiras notas do
    /// iFood foram autorizadas na Receita e NENHUMA saiu no papel: o caixa dizia
    /// "os itens do cupom somam R$ 21,90 e o total é R$ 16,90. Não imprimi um
    /// cupom que não fecha". A diferença era exatamente o cupom da loja, que no
    /// pedido do iFood vem por item (vDesc). ItemCupom.Total é LÍQUIDO, como a
    /// venda do balcão monta; aqui ia o vProd cheio.
    /// </summary>
    private const string ComCupomDaLoja = """
    [{"order_id":"desc-1","numero":91,"serie":2,
      "chave":"31260912345678000199650020000000911000000911",
      "protocolo":"131260000999999","autorizada_em":"2026-09-17T22:43:00+00:00","tp_amb":1,
      "v_nf":16.90,"documento":null,
      "emitente":{"nome":"MM FOOD SERVICE LTDA","cnpj":"62177839000238","ie":"0012345670012",
                  "endereco":"Rua Antonio de Albuquerque, 100, Savassi, Belo Horizonte, MG"},
      "itens":[{"codigo":"1","descricao":"DONUT HOMER","qtd":1,"vUnit":21.90,"vProd":21.90,"vDesc":5.00,"unidade":"UN"}],
      "pagamentos":[{"tPag":"17","valor":16.90}]}]
    """;

    public static void Rodar(Action<bool, string> checar)
    {
        var papeis = NotaIfoodPapel.Ler(UmaLinha);
        checar(papeis.Count == 1 && papeis[0].OrderId == "abc-123", "a resposta do servidor vira um papel com o número do pedido");

        var c = papeis.Count == 1 ? papeis[0].Cupom : null;
        checar(c is not null && c.EmitenteNome == "MM FOOD SERVICE LTDA" && c.EmitenteCnpj == "62177839000238"
               && c.EmitenteIe == "0012345670012" && (c.EmitenteEndereco ?? "").Contains("Savassi"),
               "o emitente é a loja que emitiu, inteiro (sem emitente o cupom é recusado na impressão)");

        checar(c is not null && c.Numero == 4412 && c.Serie == 1 && c.TpAmb == 1
               && c.Chave == "31260912345678000199650010000044121000044120"
               && c.Protocolo == "131260000123456" && !string.IsNullOrEmpty(c.QrCode),
               "sai com o bloco fiscal completo: número, série, chave, protocolo e QR");

        checar(c is not null && !c.Recibo && !c.Contingencia,
               "é NOTA, não recibo: o papel não pode dizer SEM VALOR FISCAL numa nota autorizada");

        checar(c is not null && c.Itens.Count == 2
               && c.Itens[0].Descricao == "DONUT HOMER" && c.Itens[0].Total.Centavos == 3100
               && c.Itens[0].Qtd.Formatada() == "2" && c.Itens[0].Unidade == "UN",
               "os itens vêm com descrição, quantidade e total");

        // vProd ausente (o segundo item): 1 x 4,00 = 4,00. Sem esta conta o item sairia zerado.
        checar(c is not null && c.Itens[1].Total.Centavos == 400,
               "item sem total calculado sai por quantidade vezes unitário, nunca zerado");

        checar(c is not null && c.Pagamentos.Count == 1 && c.Pagamentos[0].Forma == "PIX"
               && c.Pagamentos[0].Valor.Centavos == 3000,
               "o código 17 da Receita vira a palavra PIX no papel");

        checar(c is not null && c.Total.Centavos == 3000 && c.VNf == 30.00m
               && c.Recebido.Centavos == c.Total.Centavos,
               "o total é o da nota, e sem troco: o pedido do iFood foi pago no aplicativo");

        checar(c is not null && c.Operador == "iFood",
               "o papel se identifica como iFood, para ninguém confundir com venda do balcão");

        checar(NotaIfoodPapel.FormaDePagamento("01") == "Dinheiro"
               && NotaIfoodPapel.FormaDePagamento("03") == "Crédito"
               && NotaIfoodPapel.FormaDePagamento("04") == "Débito"
               && NotaIfoodPapel.FormaDePagamento("99") == "Outros"
               && NotaIfoodPapel.FormaDePagamento(null) == "Outros"
               && NotaIfoodPapel.FormaDePagamento(" 17 ") == "PIX",
               "todo código de pagamento vira palavra, e o desconhecido sai como Outros");

        // xPag do próprio servidor manda quando existe (bandeira, por exemplo)
        var comXPag = NotaIfoodPapel.Ler("""
            [{"order_id":"x1","pagamentos":[{"tPag":"03","xPag":"Crédito Visa","valor":10}],"itens":[]}]
            """);
        checar(comXPag.Count == 1 && comXPag[0].Cupom.Pagamentos[0].Forma == "Crédito Visa",
               "quando o servidor manda o nome do pagamento, é ele que sai no papel");

        // uma linha torta não pode segurar o papel das outras
        var mistura = NotaIfoodPapel.Ler("""
            [{"numero":1,"itens":[]},
             {"order_id":"boa","numero":2,"itens":[{"descricao":"DONUT","qtd":1,"vUnit":10,"vProd":10}],"pagamentos":[]}]
            """);
        checar(mistura.Count == 1 && mistura[0].OrderId == "boa",
               "linha sem número de pedido é pulada e não derruba as outras");

        checar(NotaIfoodPapel.Ler(null).Count == 0 && NotaIfoodPapel.Ler("").Count == 0
               && NotaIfoodPapel.Ler("nao e json").Count == 0 && NotaIfoodPapel.Ler("{\"erro\":\"x\"}").Count == 0,
               "resposta vazia, quebrada ou que não é lista devolve nada, sem estourar");

        // ── o encanamento, lido do fonte ────────────────────────────────────
        var venda = Fonte(Path.Combine("Telas", "Venda.xaml.cs")) ?? "";
        var kds = Fonte(Path.Combine("Telas", "Kds.xaml.cs")) ?? "";
        var servicos = Fonte("Servicos.cs") ?? "";
        var nuvem = Fonte(Path.Combine("Pdv.Nucleo", "Nuvem.cs")) ?? "";

        checar(ContaVezes(venda, "Servicos.ImprimirNotasDoIfoodAsync()") == 2,
               "o papel sai nas DUAS puxadas do delivery na tela de venda (sino e timer), não só numa");
        checar(kds.Contains("Servicos.ImprimirNotasDoIfoodAsync()"),
               "e também quando o quadro do KDS puxa");
        checar(servicos.Contains("UmaNotaIfoodPorVez") && servicos.Contains("ConfirmarPapelIfoodAsync"),
               "uma impressão por vez, e o caixa sempre responde ao servidor o que aconteceu");
        checar(nuvem.Contains("/rest/v1/rpc/nfce_ifood_papeis")
               && nuvem.Contains("/rest/v1/rpc/nfce_ifood_papel_impresso"),
               "as duas RPCs do servidor são as do desenho (buscar e confirmar)");
        checar(nuvem.Contains("SessaoOkAsync") &&
               nuvem.IndexOf("SessaoOkAsync", nuvem.IndexOf("PapeisDoIfoodAsync", StringComparison.Ordinal), StringComparison.Ordinal) > 0,
               "sem sessão o caixa nem pergunta: a nota do iFood não é dado de chave pública");

        // ── o cupom da loja: 17/09/2026 ─────────────────────────────────────
        var comDesc = NotaIfoodPapel.Ler(ComCupomDaLoja);
        var d = comDesc.Count == 1 ? comDesc[0].Cupom : null;
        checar(d is not null && d.Itens.Count == 1 && d.Itens[0].Total.Centavos == 1690
               && d.Itens[0].Desconto.Centavos == 500 && d.Itens[0].Unitario.Centavos == 2190,
               "item com cupom da loja: o total do item ja vem sem o desconto, e o desconto sai separado");

        checar(d is not null && d.Itens.Sum(i => i.Total.Centavos) == d.Total.Centavos,
               "a soma dos itens fecha com o total da nota (era o que travava a impressao das 10 primeiras)");

        checar(d is not null && d.Total.Centavos == 1690 && d.Pagamentos.Sum(p => p.Valor.Centavos) == 1690,
               "o total e o pagamento continuam sendo o valor liquido da nota");

        // desconto maior que o item, ou negativo, nao pode virar total negativo
        var doido = NotaIfoodPapel.Ler(ComCupomDaLoja.Replace("\"vDesc\":5.00", "\"vDesc\":99.00"));
        checar(doido.Count == 1 && doido[0].Cupom.Itens[0].Total.Centavos == 0,
               "desconto maior que o item zera o item em vez de virar valor negativo");
        var negativo = NotaIfoodPapel.Ler(ComCupomDaLoja.Replace("\"vDesc\":5.00", "\"vDesc\":-3.00"));
        checar(negativo.Count == 1 && negativo[0].Cupom.Itens[0].Total.Centavos == 2190
               && negativo[0].Cupom.Itens[0].Desconto.Centavos == 0,
               "desconto negativo e ignorado, nao aumenta o item");
    }

    private static int ContaVezes(string texto, string trecho)
    {
        var n = 0;
        for (var i = texto.IndexOf(trecho, StringComparison.Ordinal); i >= 0;
             i = texto.IndexOf(trecho, i + trecho.Length, StringComparison.Ordinal)) n++;
        return n;
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
