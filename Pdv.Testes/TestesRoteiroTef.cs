using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O ROTEIRO DE HOMOLOGACAO DENTRO DO CAIXA, E A PLANILHA QUE SAI DELE.
///
/// 09/09/2026: o dono fez a primeira transacao aprovada no sandbox e pediu o menu
/// com "cada passo, cada valor", e a planilha gerada em vez de preenchida a mao.
///
/// O que estes testes seguram:
///  · os 58 passos existem, com a numeracao da planilha oficial;
///  · os valores exatos do roteiro (R$ 1.000,01 na venda negada) nao se perdem na
///    conversao para centavos: um centavo errado faz o passo inteiro voltar;
///  · o placar conta so o que trava a homologacao;
///  · passo aprovado sem REQNUM e denunciado AQUI, e nao na analise da PayGo.
/// </summary>
public static class TestesRoteiroTef
{
    public static void Rodar(Action<bool, string> checar)
    {
        var todos = RoteiroTef.Passos;

        // ── O ROTEIRO INTEIRO ───────────────────────────────────────────────
        checar(todos.Count == 58, $"os 58 passos da planilha estao aqui ({todos.Count})");
        checar(todos.Select(p => p.Numero).SequenceEqual(Enumerable.Range(1, 58)),
            "numerados de 1 a 58, na ordem da planilha oficial");
        checar(todos.All(p => p.Titulo.Length > 0), "todo passo tem titulo");
        checar(todos.All(p => p.OQueFazer.Length > 0), "todo passo diz o que fazer");

        // ── O QUE VALE PARA ESTA INTEGRACAO ─────────────────────────────────
        var nossos = RoteiroTef.ParaBibliotecaWindows();
        checar(nossos.All(p => p.Obrigatoriedade is "SIM" or "OPCIONAL"),
            "so entram os que valem para biblioteca Windows");
        checar(!nossos.Any(p => p.Numero is 49 or 50 or 51 or 52 or 53),
            "os cinco de ControlPay ficam de fora: e outra integracao");
        checar(!nossos.Any(p => p.Numero is 41 or 42), "autoatendimento fica de fora");
        checar(!nossos.Any(p => p.Numero == 58), "C6PAY Android fica de fora");

        var obrig = RoteiroTef.Obrigatorios();
        checar(obrig.Count is > 30 and < 50, $"os obrigatorios sao a maioria ({obrig.Count})");
        checar(obrig.All(p => p.Obrigatoriedade == "SIM"), "e todos marcados SIM");

        // ── OS VALORES, QUE E ONDE SE PERDE PASSO ───────────────────────────
        // R$ 1.000,01 e a venda negada. Virar 100001 centavos, nao 1000,01 nem 100000.
        var negada = todos.First(p => p.Valor == "1.000,01");
        checar(RoteiroTef.ValorCent(negada) == 100001,
            $"1.000,01 vira 100001 centavos ({RoteiroTef.ValorCent(negada)})");

        var comValor = todos.Where(p => p.Valor.Length > 0).ToList();
        checar(comValor.Count > 5, $"varios passos trazem o valor exato do roteiro ({comValor.Count})");
        checar(comValor.All(p => RoteiroTef.ValorCent(p) is > 0),
            "todo passo com valor converte para centavos maior que zero");

        var semValor = todos.First(p => p.Valor.Length == 0);
        checar(RoteiroTef.ValorCent(semValor) is null,
            "passo que nao e de venda nao inventa valor");

        checar(RoteiroTef.RetornoExigido == "PWINFO_REQNUM",
            "a planilha exige o PWINFO_REQNUM nesta integracao");

        // ── O PLACAR ────────────────────────────────────────────────────────
        var quando = new DateTime(2026, 9, 9, 14, 30, 0);
        var feitos = new Dictionary<int, PassoFeito>
        {
            [1] = new(1, PlacarHomologacao.Aprovado, "000123", quando),
            [6] = new(6, PlacarHomologacao.Aprovado, "000456", quando),
            [4] = new(4, PlacarHomologacao.Recusado, "000789", quando),
        };
        var linhas = PlacarHomologacao.Montar(nossos, feitos);

        checar(linhas.Count == nossos.Count, "o placar tem uma linha por passo");
        checar(linhas.First().Passo.Numero == 1, "e comeca no passo 1");
        checar(linhas.First(l => l.Passo.Numero == 1).Ok, "passo 1 aprovado conta como ok");
        checar(!linhas.First(l => l.Passo.Numero == 4).Ok, "passo recusado NAO conta como ok");
        checar(linhas.First(l => l.Passo.Numero == 4).Tentado, "mas conta como tentado");
        checar(!linhas.First(l => l.Passo.Numero == 2).Tentado, "passo nao executado nao e tentado");

        var (f, t) = PlacarHomologacao.Progresso(linhas);
        checar(t == obrig.Count, $"o total do placar sao os obrigatorios ({t})");
        checar(f == 2, $"dois obrigatorios aprovados ({f})");

        // Opcional aprovado nao infla o placar.
        var comOpcional = new Dictionary<int, PassoFeito>(feitos)
        {
            [13] = new(13, PlacarHomologacao.Aprovado, "000999", quando),
        };
        var (f2, t2) = PlacarHomologacao.Progresso(PlacarHomologacao.Montar(nossos, comOpcional));
        checar(f2 == f && t2 == t, "passo opcional aprovado nao mexe no que trava a homologacao");

        // ── O PROXIMO PASSO ─────────────────────────────────────────────────
        var prox = PlacarHomologacao.Proximo(linhas);
        checar(prox is not null && prox.Numero == 2,
            $"o proximo obrigatorio e o 2, porque o 1 passou ({prox?.Numero})");
        var tudoOk = PlacarHomologacao.Montar(nossos,
            obrig.ToDictionary(p => p.Numero, p => new PassoFeito(p.Numero, PlacarHomologacao.Aprovado, "x", quando)));
        checar(PlacarHomologacao.Proximo(tudoOk) is null, "com tudo aprovado, nao ha proximo");

        // ── A PLANILHA ──────────────────────────────────────────────────────
        var csv = PlacarHomologacao.Csv(linhas);
        var linhasCsv = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        checar(linhasCsv.Length == nossos.Count + 1, "uma linha por passo, mais o cabecalho");
        checar(linhasCsv[0].Contains("PWINFO_REQNUM"),
            "o cabecalho lembra o que a planilha exige nessa coluna");
        checar(linhasCsv[0].Contains("Teste;Obrigatoriedade"),
            "as colunas sao as da planilha oficial, na mesma ordem");
        checar(csv.Contains("Passo 1;SIM;000123"), "o REQNUM do passo vai na coluna certa");
        checar(csv.Contains("nao executado"), "passo nao feito aparece dito, e nao em branco");
        // Ponto e virgula no titulo nao pode partir a linha em duas colunas.
        var comPontoVirgula = PlacarHomologacao.Csv(new[]
        {
            new LinhaDoPlacar(new PassoTef(1, "Venda; com ponto e virgula", "SIM", "", "", "x", "y"), null),
        });
        checar(comPontoVirgula.Contains("\"Venda; com ponto e virgula\""),
            "titulo com ponto e virgula sai entre aspas");

        // ── O AVISO QUE EVITA DEVOLUCAO ─────────────────────────────────────
        checar(PlacarHomologacao.AvisoDeReqnumFaltando(linhas) is null,
            "com todos os aprovados trazendo REQNUM, nao ha o que avisar");

        var semReqnum = PlacarHomologacao.Montar(nossos, new Dictionary<int, PassoFeito>
        {
            [1] = new(1, PlacarHomologacao.Aprovado, null, quando),
            [6] = new(6, PlacarHomologacao.Aprovado, "   ", quando),
        });
        var aviso = PlacarHomologacao.AvisoDeReqnumFaltando(semReqnum)!;
        checar(aviso.Contains("2 passos"), $"denuncia os aprovados sem REQNUM ({aviso})");
        checar(aviso.Contains("1, 6"), "e diz quais sao");
        checar(!aviso.Contains('—'), "sem travessao");

        var umSo = PlacarHomologacao.Montar(nossos, new Dictionary<int, PassoFeito>
        {
            [1] = new(1, PlacarHomologacao.Aprovado, "", quando),
        });
        checar(PlacarHomologacao.AvisoDeReqnumFaltando(umSo)!.Contains("O passo 1"),
            "um so fala no singular");

        // ── O MESMO REQNUM EM DOIS PASSOS ──────────────────────────────────
        // 09/09/2026: o dono rodou os passos 2, 3, 4 e 5 em poucos minutos e os
        // QUATRO ficaram com 276864. O passo 5 e "Esc no menu de rede": nao completa
        // transacao, entao o ultimo REQNUM continuava sendo o do passo 4. A tela
        // ofereceu e ele aceitou, porque quem roda o roteiro confia que o sistema so
        // oferece o que faz sentido.
        var repetido = PlacarHomologacao.Montar(nossos, new Dictionary<int, PassoFeito>
        {
            [2] = new(2, PlacarHomologacao.Aprovado, "276864", quando),
            [3] = new(3, PlacarHomologacao.Aprovado, "276864", quando),
            [4] = new(4, PlacarHomologacao.Aprovado, "276865", quando),
        });
        var avisoRep = PlacarHomologacao.AvisoDeReqnumRepetido(repetido)!;
        checar(avisoRep.Contains("276864"), $"denuncia o REQNUM repetido ({avisoRep})");
        checar(avisoRep.Contains("passo 2") && avisoRep.Contains("passo 3"), "e diz em quais passos");
        checar(!avisoRep.Contains("276865"), "o que aparece uma vez so nao entra no aviso");
        checar(PlacarHomologacao.AvisoDeReqnumRepetido(linhas) is null,
            "sem repeticao, nao ha o que avisar");
        checar(!avisoRep.Contains('—'), "sem travessao");

        // ── A ULTIMA TRANSACAO PRECISA SER RECONHECIVEL ────────────────────
        // 09/09/2026, o dono: "ele mostra ultima transacao TEF XXXXXX, mas eu nao sei
        // se a ultima foi essa". Numero de oito digitos ninguem reconhece; hora e valor
        // sim. Quem acabou de cobrar R$ 100.000,00 as 15:42 sabe na hora se e aquela.
        PlacarHomologacao.GuardarUltimo("0000278036", 10000000, "aprovada");
        var desc = PlacarHomologacao.DescricaoDaUltima(DateTime.Now)!;
        checar(desc.Contains("0000278036"), $"a descricao traz o numero ({desc})");
        checar(desc.Contains("100.000,00"), "e o VALOR, que e o que se reconhece");
        checar(desc.Contains("aprovada"), "e como ela terminou");
        checar(System.Text.RegularExpressions.Regex.IsMatch(desc, @"\d{2}:\d{2}:\d{2}"),
            "e a hora com segundos, para casar com o log");

        // ── TRANSACAO QUE NASCEU DE UM PASSO TEM DONO ──────────────────────
        // 09/09/2026: o dono rodou o passo 3 duas vezes, a tela mostrou 278745 e
        // 278747, e na planilha o 278747 foi parar no PASSO 2. Transacao que nasceu de
        // "Cobrar este valor" no passo 3 nao pode ser oferecida ao passo 2, por mais
        // recente que seja.
        PlacarHomologacao.GuardarUltimo("278747", 100000, "aprovada", passo: 3);
        var vazio = Array.Empty<string>();
        checar(PlacarHomologacao.ReqnumParaOferecer(DateTime.Now, vazio, passoAtual: 3) == "278747",
            "ao passo dono, e oferecida");
        checar(PlacarHomologacao.ReqnumParaOferecer(DateTime.Now, vazio, passoAtual: 2) is null,
            "a OUTRO passo, nao e oferecida nem sendo a mais recente");

        // Venda pelo caminho normal nao tem dono: serve a qualquer passo, e quem sabe
        // de quem e, e so quem estava na frente do pinpad.
        PlacarHomologacao.GuardarUltimo("278900", 100000, "aprovada", passo: null);
        checar(PlacarHomologacao.ReqnumParaOferecer(DateTime.Now, vazio, passoAtual: 2) == "278900",
            "transacao sem passo continua sendo oferecida a qualquer um");

        // Velha demais nao e oferecida: numero de meia hora atras nao e deste passo.
        PlacarHomologacao.GuardarUltimo("0000000001", 100, "aprovada");
        checar(PlacarHomologacao.DescricaoDaUltima(DateTime.Now.AddMinutes(30)) is null,
            "transacao de meia hora atras nao e descrita");

        // ── E O QUE JA FOI USADO NAO E OFERECIDO DE NOVO ────────────────────
        var usados = PlacarHomologacao.ReqnumsJaUsados(repetido);
        checar(usados.Contains("276864") && usados.Contains("276865"),
            "os carimbados sao reconhecidos");
        checar(!usados.Contains("999999"), "e so eles");

        // Passo RECUSADO sem reqnum nao e problema: nao vai para a planilha como ok.
        var recusadoSemReq = PlacarHomologacao.Montar(nossos, new Dictionary<int, PassoFeito>
        {
            [4] = new(4, PlacarHomologacao.Recusado, null, quando),
        });
        checar(PlacarHomologacao.AvisoDeReqnumFaltando(recusadoSemReq) is null,
            "passo recusado sem REQNUM nao vira alarme");
    }
}
