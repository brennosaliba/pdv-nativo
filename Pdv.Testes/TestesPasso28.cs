using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// PASSO 28 do roteiro v20260819: "Solicitação de dado genérico digitado 1, venda de R$ 1.001,00".
///
/// O passo só existe por causa do VALOR. É R$ 1.001,00 no autorizador C6PAY que faz o simulador
/// da PayGo pedir um dado digitado na tag 0x2F; um centavo a mais ou a menos e ele aprova a venda
/// direto, sem pedir nada, e a gravação não tem o que mostrar. A captura em si já estava pronta
/// (Pdv.Nucleo/ProvedorPGWebLib.cs, AtenderAsync) e o que o operador digita é provado em
/// TestesPasso29; o que faltava era conseguir cobrar o valor.
///
/// A linha de valor de teste na comanda (Telas/Venda.xaml.cs, LancarValorDeTeste) resolveu a
/// primeira metade: o operador digita R$ 1.001,00 e a comanda soma R$ 1.001,00. Faltava a
/// segunda, que é o que se prova aqui.
///
/// ⭐ O BURACO. A linha de teste entrava no motor de promoções como qualquer outra linha da
/// comanda (Telas/Venda.xaml.cs, AvaliarComanda). E promoção SEM "alvo" no payload alcança tudo:
/// Promocoes.AlvoBate termina em `_ => true`. Num caixa de homologação pareado com a loja de
/// verdade (que é o caso: é a máquina do dono, com o cardápio e as promoções da Savassi
/// descendo pela sincronização), uma promoção boba de 10% transformava a venda de R$ 1.001,00
/// em R$ 900,90. O operador via "Promoção: -R$ 100,10" numa venda de teste, a maquininha
/// recebia 90090 em PWINFO_TOTAMNT, o C6PAY não pedia dado nenhum, e a gravação parava no
/// passo 28 sem ninguém entender por quê. Pior: promoção com 2FA que passasse a vencer pedia o
/// código do gerente no meio da gravação.
///
/// O conserto é uma linha no motor (Pdv.Nucleo/Promocoes.cs, AvaliarCarrinho): a linha de teste
/// entra com quantidade ZERO. Com zero unidades ela não ganha desconto, não conta como compra
/// nem como brinde no compre-e-ganhe, não entra em combo e não faz promoção com 2FA vencer. O
/// preço fica intacto, porque quem cobra é a comanda, não o motor.
///
/// A marca do id (`teste-`) é UMA constante, Promocoes.PrefixoLinhaDeTeste, escrita pela tela e
/// lida pelo motor. Duas cópias divergiriam no primeiro dia, e o sintoma seria exatamente este:
/// venda do roteiro cobrada com desconto.
/// </summary>
public static class TestesPasso28
{
    /// <summary>O valor do passo 28, em centavos.</summary>
    private const long Passo28 = 100100;

    /// <summary>A tag 0x2F do roteiro, escrita pelo número: é assim que ela aparece no PDF e na auditoria.</summary>
    private const ushort TagGenerica = 0x2F;

    public static void Rodar(Action<bool, string> checar)
    {
        NoMotor(checar);
        NaTela(checar);
        NaMaquininha(checar);
    }

    // ── 1. o motor de promoções não encosta na linha de teste ───────────────

    private static Promocoes.Promo P(string json)
        => Promocoes.Parsear(json) ?? throw new InvalidOperationException("payload de teste nao parseou: " + json);

    private static void NoMotor(Action<bool, string> checar)
    {
        var agora = new DateTime(2026, 9, 7, 14, 0, 0);
        var ctx = new Promocoes.ContextoAutorizacao();

        // Promoção como as do painel: sem "alvo" no payload. AlvoBate devolve true para
        // qualquer produto, então ela alcança a comanda inteira.
        var dezEmTudo = P("""{"id":"p10","nome":"dez por cento","tipo":"percentual","percentual":10,"ativa":true,"inicio":"2026-01-01","fim":null}""");

        var idTeste = Promocoes.PrefixoLinhaDeTeste + Guid.NewGuid().ToString("N");
        var linhaTeste = new Promocoes.ItemCarrinho(idTeste, "Teste", Passo28, 1000);
        var linhaDonut = new Promocoes.ItemCarrinho("d-ninho", "Donuts", Passo28, 1000);

        // Controle: a promoção está mesmo viva. Sem isto o teste passaria com o motor desligado.
        var controle = Promocoes.AvaliarCarrinho(new[] { dezEmTudo }, new[] { linhaDonut }, agora, ctx);
        checar(controle.TotalCent == 10010,
            $"controle: a promoção sem alvo alcança o donut e desconta R$ 100,10 (descontou {new Dinheiro(controle.TotalCent).Formatado()})");

        // ⭐ e não encosta na linha de teste
        var av = Promocoes.AvaliarCarrinho(new[] { dezEmTudo }, new[] { linhaTeste }, agora, ctx);
        checar(av.TotalCent == 0 && av.DescontoCent[0] == 0,
            $"⭐ passo 28: a linha de valor de teste sai do motor SEM desconto (saiu com {new Dinheiro(av.TotalCent).Formatado()})");
        checar(av.PromoId is null && av.PromoNome is null,
            "e nenhuma promoção é declarada vencedora numa comanda que só tem a linha de teste");

        // O que o caixa cobra: preço × quantidade menos o desconto da linha. É a mesma conta de
        // PintarComanda e de Finalizar.
        var cobrado = new Dinheiro(linhaTeste.PrecoCent).VezesQtd(linhaTeste.QtdMilesimos).Centavos - av.DescontoCent[0];
        checar(cobrado == Passo28,
            $"⭐ o total da comanda continua R$ 1.001,00, que é o que faz o C6PAY pedir o dado (ficou {new Dinheiro(cobrado).Formatado()})");

        // Comanda misturada: o donut leva o desconto, a linha de teste não. O índice de cada
        // linha tem que continuar batendo (o motor devolve arrays paralelos à comanda).
        var misto = Promocoes.AvaliarCarrinho(new[] { dezEmTudo }, new[] { linhaTeste, linhaDonut }, agora, ctx);
        checar(misto.DescontoCent.Length == 2 && misto.DescontoCent[0] == 0 && misto.DescontoCent[1] == 10010,
            $"comanda misturada: desconto só no donut, e cada desconto na sua linha ([{string.Join(",", misto.DescontoCent)}])");

        // Compre e ganhe sem alvo: a linha de teste não pode ser comprada nem ganha. Ela valia
        // R$ 1.001,00 e viraria o brinde mais caro da casa.
        var compreGanhe = P("""{"id":"cg","nome":"compre e ganhe","tipo":"compre_ganhe","ativa":true,"inicio":"2026-01-01","fim":null,"config":{"ganha_regra":"qualquer_mais_barato"}}""");
        var cg = Promocoes.AvaliarCarrinho(new[] { compreGanhe },
            new[] { linhaTeste, new Promocoes.ItemCarrinho("d-ninho", "Donuts", 900, 2000) }, agora, ctx);
        checar(cg.DescontoCent[0] == 0 && cg.UnidadesGratis[0] == 0,
            $"compre e ganhe sem alvo não dá a linha de teste de brinde (desconto {cg.DescontoCent[0]}, grátis {cg.UnidadesGratis[0]})");

        // Leve X pague Y sem alvo: mesma história pelo lado do "mais barato de graça".
        var leve = P("""{"id":"lx","nome":"leve 2 pague 1","tipo":"leve_x_pague_y","leve":2,"pague":1,"ativa":true,"inicio":"2026-01-01","fim":null,"config":{"lxpy":{"gratis_mais_barato":true}}}""");
        var lx = Promocoes.AvaliarCarrinho(new[] { leve }, new[] { linhaTeste, linhaDonut }, agora, ctx);
        checar(lx.DescontoCent[0] == 0,
            $"leve X pague Y sem alvo também não encosta na linha de teste (descontou {lx.DescontoCent[0]})");

        // Promoção com 2FA que alcançaria tudo: numa comanda só de teste ela não pode virar
        // pendência, senão a gravação para para pedir o código do gerente.
        var comCodigo = P("""{"id":"f20","nome":"desconto funcionario","tipo":"percentual","percentual":20,"ativa":true,"inicio":"2026-01-01","fim":null,"config":{"autorizacao":"gerente"}}""");
        var pend = Promocoes.AvaliarCarrinho(new[] { comCodigo }, new[] { linhaTeste }, agora, new Promocoes.ContextoAutorizacao());
        checar(pend.Pendentes.Count == 0,
            $"promoção com 2FA não pede código do gerente por causa da linha de teste ({pend.Pendentes.Count} pendência(s))");
        var pendControle = Promocoes.AvaliarCarrinho(new[] { comCodigo }, new[] { linhaDonut }, agora, new Promocoes.ContextoAutorizacao());
        checar(pendControle.Pendentes.Count == 1,
            "controle: com um produto do cardápio ela continua pedindo o código, como sempre pediu");

        // O reconhecimento é por MARCA, não por categoria: quem batiza a linha é a tela.
        checar(Promocoes.ForaDoMotor(idTeste) && !Promocoes.ForaDoMotor("d-ninho") && !Promocoes.ForaDoMotor("testemunha"),
            "a marca do id reconhece a linha de teste e só ela (produto do cardápio nunca começa com 'teste-')");
    }

    // ── 2. a tela escreve o id com a MESMA constante ────────────────────────

    private static void NaTela(Action<bool, string> checar)
    {
        var cs = Arquivo("Telas", "Venda.xaml.cs");
        if (cs is null) { checar(false, "achei Telas/Venda.xaml.cs"); return; }

        var lancar = Trecho(cs, "private void AdicionarValorDeTeste", "PintarComanda();");
        checar(lancar.Length > 0, "achei AdicionarValorDeTeste na tela de venda");
        checar(lancar.Contains("Promocoes.PrefixoLinhaDeTeste", StringComparison.Ordinal),
            "⭐ a tela monta o id da linha com Promocoes.PrefixoLinhaDeTeste (uma constante só, tela e motor)");
        checar(!lancar.Contains("\"teste-", StringComparison.Ordinal) && !lancar.Contains("$\"teste-", StringComparison.Ordinal),
            "e não escreve o prefixo à mão: duas cópias divergem, e o sintoma é venda do roteiro com desconto");

        // A comanda continua mandando a comanda INTEIRA para o motor: o corte é do motor, não
        // da tela. Duas defesas para a mesma regra é o caminho para elas discordarem.
        var avaliar = Trecho(cs, "private Nucleo.Promocoes.Avaliacao AvaliarComanda", "return av;");
        checar(avaliar.Contains("AvaliarCarrinho(", StringComparison.Ordinal)
               && !avaliar.Contains("PrefixoLinhaDeTeste", StringComparison.Ordinal),
            "a tela não filtra nada antes do motor: a regra da linha de teste mora em um lugar só");
    }

    // ── 3. o valor exato chega à maquininha ─────────────────────────────────

    private static void NaMaquininha(Action<bool, string> checar)
    {
        Directory.CreateDirectory(TestesPGWebLib.PastaTeste);
        var opcoes = new OpcoesPGWebLib("Pdv.AmericanDay", "0.5.9", "American Day", RedeCartao: "C6PAY");

        var f = new FakePGWebLib { DadoDigitadoVezes = 1 };
        var pedidos = new List<PwGetData>();
        var p = new ProvedorPGWebLib(f, TestesPGWebLib.PastaTeste, opcoes)
        {
            IntervaloPollMs = 5,
            TempoMaxExecMs = 2000,
            TempoMaxCapturaMs = 2000,
            TempoPerguntaMs = 500,
            Guardar = _ => true,
            Perguntar = (g, _) => { pedidos.Add(g); return Task.FromResult<string?>("ABC123"); },
        };

        var d = p.CobrarAsync(TipoTef.Credito, new Dinheiro(Passo28), null, 1, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        var totamnt = f.Ultima?.Params.GetValueOrDefault(PW.PWINFO_TOTAMNT);
        checar(totamnt == "100100",
            $"⭐ o total da comanda vai inteiro em PW_iAddParam(PWINFO_TOTAMNT): 100100 (foi \"{totamnt ?? "(nada)"}\")");
        checar(f.Ultima?.Params.GetValueOrDefault(PW.PWINFO_AUTHSYST) == "C6PAY",
            "e no autorizador que o passo manda, o C6PAY");
        checar(pedidos.Any(g => g.Identificador == TagGenerica && g.EhDigitado),
            "com esse valor a biblioteca pede o dado digitado da tag 0x2F, que é o que o passo 28 quer ver");
        checar(d.Pago, $"e a venda aprova depois do dado ({d.Situacao})");

        // A tag 0x2F não sai do caixa antes de ser pedida: o roteiro reprova com DemoErroTeste3
        // quem adianta. A lista de parâmetros da venda é fechada e ela não está lá.
        var adiantada = f.Chamadas
            .TakeWhile(c => c != "ExecTransac")
            .Any(c => c.StartsWith($"AddParam({TagGenerica}=", StringComparison.Ordinal));
        checar(!adiantada,
            "antes do primeiro PW_iExecTransac o caixa não manda a tag 0x2F (senão o teste volta DemoErroTeste3)");
    }

    // ── leitura de fonte (mesma receita das outras suítes) ──────────────────
    private static string? Arquivo(params string[] partes)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var alvo = Path.Combine(new[] { d.FullName }.Concat(partes).ToArray());
            if (File.Exists(alvo)) return File.ReadAllText(alvo);
            d = d.Parent;
        }
        return null;
    }

    private static string Trecho(string fonte, string de, string ate)
    {
        var i = fonte.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var j = fonte.IndexOf(ate, i, StringComparison.Ordinal);
        return j < 0 ? fonte[i..] : fonte[i..(j + ate.Length)];
    }
}
