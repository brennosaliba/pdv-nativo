using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// Lista fechada de credenciadoras (RedesPayGo).
///
/// O caso que motivou: a rede do cartão e a do Pix eram TEXTO LIVRE na configuração, a loja
/// foi para produção com o `PIX C6 BANK` da homologação ainda gravado e o Pix de produção
/// (ITAU) voltava "MODALIDADE DE PAGAMENTO INVALIDA" — com o cliente no balcão.
///
/// Os testes olham o que o DONO VÊ na caixa de seleção e o que o PDV MANDA para o TEF; a
/// forma como a lista está montada por dentro não interessa a nenhum deles.
/// </summary>
public static class TestesRedesPayGo
{
    public static void Rodar(Action<bool, string> checar)
    {
        // Os nomes como o dono os entregou — cópia independente da lista do Pdv.Nucleo, senão
        // o teste passaria a validar o que a implementação diz, e não o combinado.
        var oficiaisCartao = new[]
        {
            "BANESECARD/MULVI", "BANRISUL/VERO", "BIN", "CIELO", "CONDUCTOR/DOCK", "CREDISHOP",
            "CTF", "C6PAY", "DMCARD", "GETNET", "GLOBALPAYMENTS/ENTREPAYMENTS", "MERCADO PAGO",
            "PAGSEGURO", "PAGBANK", "REDE", "RV", "SAFRAPAY", "SIPAG", "STONE", "TICKETLOG",
        };
        var oficiaisPix = new[]
        {
            "PIX C6 BANK", "PIX CIELO", "PIX ITAU", "PIX SICREDI", "PIX SIPAG", "PIX BRADESCO",
        };

        var cartao = RedesPayGo.OpcoesCartao();
        var pix = RedesPayGo.OpcoesPix();

        // ── a lista que aparece na tela ─────────────────────────────────────
        checar(cartao.Count == oficiaisCartao.Length + 1,
            $"cartão: as 20 credenciadoras + a vazia (contei {cartao.Count})");
        checar(pix.Count == oficiaisPix.Length + 1,
            $"pix: as 6 credenciadoras + a vazia (contei {pix.Count})");

        var faltaCartao = oficiaisCartao.Where(n => !cartao.Any(o => o.Valor == n)).ToList();
        checar(faltaCartao.Count == 0,
            $"cartão: os 20 nomes combinados estão lá (faltou: {string.Join(", ", faltaCartao)})");
        var faltaPix = oficiaisPix.Where(n => !pix.Any(o => o.Valor == n)).ToList();
        checar(faltaPix.Count == 0,
            $"pix: os 6 nomes combinados estão lá (faltou: {string.Join(", ", faltaPix)})");

        checar(cartao.Select(o => o.Valor).Distinct().Count() == cartao.Count,
            "cartão: nenhum nome repetido (duas linhas iguais = escolha no escuro)");
        checar(pix.Select(o => o.Valor).Distinct().Count() == pix.Count, "pix: nenhum nome repetido");

        // Pix e cartão não se misturam: é a troca de campo que a rede recusa na hora.
        checar(!cartao.Any(o => o.Valor.StartsWith("PIX ", StringComparison.Ordinal)),
            "nenhuma rede de Pix na lista do cartão");
        checar(pix.Skip(1).All(o => o.Valor.StartsWith("PIX ", StringComparison.Ordinal)),
            "a lista do Pix só tem rede de Pix");

        // ── a opção vazia: "a PayGo escolhe" ────────────────────────────────
        checar(cartao[0].Valor.Length == 0 && cartao[0].Automatica, "cartão: a PRIMEIRA opção é a vazia");
        checar(pix[0].Valor.Length == 0 && pix[0].Automatica, "pix: a PRIMEIRA opção é a vazia");
        checar(cartao[0].Rotulo.Trim().Length > 0,
            "a opção vazia tem texto — linha em branco no topo ninguém entende como padrão");
        checar(!cartao.Skip(1).Any(o => o.Automatica), "só existe UMA opção de roteamento automático");

        foreach (var vazio in new string?[] { null, "", "   " })
            checar(RedesPayGo.ParaEnvioCartao(vazio) is null && RedesPayGo.ParaEnvioPix(vazio) is null,
                $"config vazia ('{vazio ?? "null"}') não manda rede: quem roteia é a PayGo");
        checar(RedesPayGo.Indice(cartao, null) == 0 && RedesPayGo.Indice(cartao, "   ") == 0,
            "sem nada gravado a tela abre no automático");

        // ── o incidente: a rede da homologação que ficou em produção ────────
        var pixHomolog = RedesPayGo.OpcoesPix("PIX C6 BANK");
        checar(pixHomolog.Count == pix.Count, "PIX C6 BANK é da lista — não vira opção extra");
        checar(pixHomolog[RedesPayGo.Indice(pixHomolog, "PIX C6 BANK")].Valor == "PIX C6 BANK",
            "a tela abre mostrando a rede que ESTÁ valendo (PIX C6 BANK), não em branco");
        checar(pix.Any(o => o.Valor == "PIX ITAU"),
            "e o PIX ITAU está na lista para o dono trocar sem digitar nada");

        // ── valor herdado de outra instalação: aparece, não some ────────────
        const string herdado = "VERO";               // metade de BANRISUL/VERO: parecido não é igual
        var comHerdado = RedesPayGo.OpcoesCartao(herdado);
        checar(comHerdado.Count == cartao.Count + 1,
            "valor fora da lista NÃO desaparece: entra como opção extra");
        checar(comHerdado.Any(o => o.Valor == herdado),
            "e entra com o texto EXATO que está no banco");
        var iHerdado = RedesPayGo.Indice(comHerdado, herdado);
        checar(comHerdado[iHerdado].Valor == herdado,
            "já vem selecionado — o dono vê a config errada em vez de um campo vazio");
        checar(!comHerdado[iHerdado].Conhecida && comHerdado[iHerdado].Rotulo.Contains("fora da lista"),
            "o rótulo denuncia que não é da lista (é o dono quem corrige)");
        checar(comHerdado[iHerdado].Valor != "BANRISUL/VERO" && RedesPayGo.ParaEnvioCartao(herdado) == herdado,
            "'VERO' NÃO é adivinhado como BANRISUL/VERO — segue exatamente como está para o TEF");

        // Campos trocados (os dois ficam lado a lado na tela de configuração).
        var trocado = RedesPayGo.OpcoesCartao("PIX C6 BANK");
        var iTrocado = trocado[RedesPayGo.Indice(trocado, "PIX C6 BANK")];
        checar(iTrocado.Valor == "PIX C6 BANK" && !iTrocado.Conhecida,
            "rede de Pix no campo do cartão sobrevive, marcada como fora da lista");
        checar(iTrocado.Rotulo.Contains("Pix"),
            "o rótulo avisa que aquilo é rede de Pix (campos trocados)");
        var trocado2 = RedesPayGo.OpcoesPix("CIELO");
        checar(trocado2.Last().Rotulo.Contains("cartão"),
            "rede de cartão no campo do Pix avisa que é de cartão");

        // ── C6 PAY x C6PAY: o espaço interno é sagrado ──────────────────────
        // A homologação roda com "C6 PAY"; "C6PAY" devolveu SERVICO NAO HABILITADOO e derrubou
        // quatro cobranças em 21/08. Aproximar um do outro trocaria uma config que funciona.
        var homolog = RedesPayGo.OpcoesCartao("C6 PAY");
        checar(homolog.Count == cartao.Count + 1 && homolog.Any(o => o.Valor == "C6 PAY"),
            "C6 PAY (com espaço) sobrevive: é outra string para o PayGo");
        checar(RedesPayGo.ParaEnvioCartao("C6 PAY") == "C6 PAY",
            "e vai para o TEF com o espaço, não vira C6PAY sozinho");

        // ── caixa, acento e espaço sobrando não criam rede nova ─────────────
        foreach (var digitado in new[] { "cielo", " CIELO ", "Cielo" })
        {
            var ops = RedesPayGo.OpcoesCartao(digitado);
            checar(ops.Count == cartao.Count && ops[RedesPayGo.Indice(ops, digitado)].Valor == "CIELO",
                $"'{digitado}' é a CIELO da lista, não uma rede a mais");
            checar(RedesPayGo.ParaEnvioCartao(digitado) == "CIELO",
                $"'{digitado}' sai para o TEF com a grafia oficial");
        }
        checar(RedesPayGo.ParaEnvioPix("pix itaú") == "PIX ITAU",
            "'pix itaú' é PIX ITAU — o arquivo do PayGo é ASCII, acento não chega do outro lado");
        checar(RedesPayGo.ParaEnvioCartao("MERCADO  PAGO") == "MERCADO PAGO",
            "espaço digitado duas vezes não inventa credenciadora");
        checar(RedesPayGo.CanonicoCartao("stone") == "STONE" && RedesPayGo.CanonicoPix("stone") is null,
            "STONE é rede de cartão e não de Pix — as listas são separadas de verdade");

        // ── o que a tela precisa para não mentir ────────────────────────────
        checar(RedesPayGo.Indice(cartao, "REDE QUE NAO EXISTE") == 0,
            "valor ausente das opções cai no automático, nunca em -1 (caixa em branco)");
        var opCielo = cartao.First(o => o.Valor == "CIELO");
        checar(opCielo.Rotulo == "CIELO" && opCielo.ToString() == opCielo.Rotulo,
            "a opção conhecida se mostra pelo próprio nome (ComboBox sem DisplayMemberPath)");
        checar(cartao.Skip(1).All(o => o.Conhecida) && pix.Skip(1).All(o => o.Conhecida),
            "toda opção da lista oficial é 'conhecida' (só o herdado destoa)");

        // ── ASCII: o intpos rejeita o arquivo inteiro por um byte fora ──────
        var sujos = oficiaisCartao.Concat(oficiaisPix)
            .Where(n => ArquivoIntpos.Ascii(n) != n || n.Trim() != n).ToList();
        checar(sujos.Count == 0,
            $"todo nome é ASCII puro e sem espaço nas pontas (problema em: {string.Join(", ", sujos)})");
    }
}
