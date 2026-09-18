using Microsoft.Data.Sqlite;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// OS PAPÉIS DA ABERTURA E DO FECHAMENTO (18/09/2026, pedido do dono: "na abertura e
/// fechamento de caixa, imprimir o relatório automático do fechamento e da abertura, com
/// campo para assinatura e os valores e data").
///
/// Cada bloco aqui existe por um risco concreto, não por cobertura:
///  · o papel tem que CABER na bobina, nas duas larguras da loja;
///  · ele não pode inventar número nenhum: o que sai impresso é o que a tela disse;
///  · rótulo cortado pela metade não serve como comprovante assinado;
///  · o fechamento sem contagem tem que sair rotulado como tal, com quem autorizou;
///  · e o caminho das telas fica travado pelo fonte, porque janela WPF não abre em teste.
/// </summary>
public static class TestesPapelDeCaixa
{
    public static void Rodar(Action<bool, string> checar)
    {
        var quando = new DateTime(2026, 9, 18, 7, 12, 0);
        var sessao = new Sessao("s1", "2026-09-18", "op1", "DAVID MATEUS",
            new DateTime(2026, 9, 18, 7, 12, 0), Dinheiro.DeReais(300));

        // ── a política decide, e turno de teste nunca imprime ──────────────────
        checar(PapelDeCaixa.SaiSozinho(PoliticaImpressao.Automatico, false), "em Automático o papel sai sozinho");
        checar(!PapelDeCaixa.SaiSozinho(PoliticaImpressao.Perguntar, false), "em Perguntar não sai sozinho");
        checar(PapelDeCaixa.PrecisaPerguntar(PoliticaImpressao.Perguntar, false), "em Perguntar o caixa pergunta");
        checar(!PapelDeCaixa.SaiSozinho(PoliticaImpressao.Nao, false)
               && !PapelDeCaixa.PrecisaPerguntar(PoliticaImpressao.Nao, false), "em Não imprimir não sai e não pergunta");
        checar(!PapelDeCaixa.SaiSozinho(PoliticaImpressao.Automatico, true)
               && !PapelDeCaixa.PrecisaPerguntar(PoliticaImpressao.Perguntar, true),
            "⭐ turno de TESTE não imprime: papel de homologação assinado viraria comprovante de um dia que não existiu");

        // ── as duas folhas cabem na bobina, nas duas larguras ─────────────────
        foreach (var colunas in new[] { 32, 48 })
        {
            var ab = PapelDeCaixa.Abertura("American Day Savassi", quando, sessao, null, colunas);
            checar(ab.All(l => l.Length <= colunas), $"abertura em {colunas} colunas: nenhuma linha estoura a bobina");
            checar(ab.Any(l => l.Contains("ABERTURA DE CAIXA")), $"abertura {colunas}: o papel se apresenta");
            checar(ab.Any(l => l.Contains("DAVID MATEUS")), $"abertura {colunas}: quem abriu está no papel");
            checar(ab.Any(l => l.Contains("18/09/2026 07:12")), $"abertura {colunas}: data e hora em 24 h");
            checar(ab.Any(l => l.Contains("18/09/2026")), $"abertura {colunas}: o dia do turno");
            checar(ab.Any(l => l.Contains("Fundo declarado") && l.Contains("R$ 300,00")), $"abertura {colunas}: o valor na sua linha");
            checar(ab.Any(l => l.StartsWith("Assinatura")) && ab.Any(l => l.StartsWith("Conferido por")),
                $"abertura {colunas}: tem onde assinar e onde conferir depois");
            checar(ab.All(l => !l.Contains('—') && !l.Contains('–')), $"abertura {colunas}: sem travessão");

            var fe = Fechamento(sessao, colunas);
            checar(fe.All(l => l.Length <= colunas), $"fechamento em {colunas} colunas: nenhuma linha estoura a bobina");
            checar(fe.Any(l => l.Contains("FECHAMENTO DE CAIXA")), $"fechamento {colunas}: o papel se apresenta");
            checar(fe.Any(l => l.Contains("DAVID MATEUS")), $"fechamento {colunas}: quem fechou está no papel");
            checar(fe.Any(l => l.Contains("18/09/2026 23:41")), $"fechamento {colunas}: data e hora em 24 h");
            checar(fe.Any(l => l.Contains("Dinheiro") && l.Contains("R$ 521,00")), $"fechamento {colunas}: o dinheiro declarado");
            checar(fe.Any(l => l.Contains("esperado") && l.Contains("R$ 519,00")), $"fechamento {colunas}: e o que era esperado");
            checar(fe.Any(l => l.Contains("SOBRA") && l.Contains("R$ 2,00")), $"fechamento {colunas}: o desfecho da forma que não bateu");
            checar(fe.Any(l => l.Contains("Diferença total") && l.Contains("R$ 2,00")), $"fechamento {colunas}: a diferença total");
            checar(fe.Any(l => l.StartsWith("Assinatura")) && fe.Any(l => l.StartsWith("Conferido por")),
                $"fechamento {colunas}: tem onde assinar e onde conferir depois");
            checar(fe.All(l => !l.Contains('—') && !l.Contains('–')), $"fechamento {colunas}: sem travessão");
        }

        // ── a linha de conferir tem onde escrever o ano ────────────────────────
        // O literal que existia tinha 35 caracteres: em 32 colunas ele era cortado e o ano
        // virava "___/___/___". Quem conferiu não tinha onde escrever 2026.
        foreach (var colunas in new[] { 32, 48 })
        {
            var linha = PapelTexto.Assinatura(colunas);
            checar(linha.Length == colunas, $"{colunas}: a linha de conferir ocupa a bobina exata");
            checar(linha.EndsWith("em __/__/____"), $"⭐ {colunas}: a data de conferência cabe inteira, com o ano");
        }

        // ── valor grande não come o rótulo ─────────────────────────────────────
        // Par corta o rótulo em silêncio quando o valor cresce. Papel assinado com
        // "Fica no caixa (troco" no lugar do rótulo inteiro não serve de comprovante.
        {
            var linhas = PapelTexto.Campo("Retirado para o cofre", "R$ 99.999,99", 32);
            checar(linhas.Count == 2 && linhas[0] == "Retirado para o cofre:" && linhas[1].Trim() == "R$ 99.999,99",
                "⭐ rótulo longo com valor grande sai em duas linhas, inteiro, em vez de ser cortado");
            checar(PapelTexto.Campo("Data", "18/09/2026 07:12", 32).Count == 1,
                "cabendo, continua uma linha só");
            var grande = PapelDeCaixa.Fechamento("American Day Savassi", quando, sessao, "DAVID MATEUS",
                ResumoFechamento.Linhas(new[] { new LinhaFechamento("dinheiro", Dinheiro.DeReais(99999.99m), Dinheiro.DeReais(99999.99m)) }),
                Dinheiro.Zero, new List<string>(), Dinheiro.DeReais(99999.99m), Dinheiro.DeReais(88888.88m),
                null, semContagem: false, autorizador: null, 32);
            checar(grande.All(l => l.Length <= 32), "fechamento de R$ 99.999,99 continua cabendo em 32 colunas");
            checar(grande.Any(l => l.StartsWith("Retirado para o cofre")), "e o rótulo da retirada aparece inteiro");
        }

        // ── o papel não inventa número ─────────────────────────────────────────
        {
            var linhas = new List<LinhaFechamento>
            {
                new("dinheiro", Dinheiro.DeReais(521), Dinheiro.DeReais(519)),
                new("credito", Dinheiro.DeReais(120), Dinheiro.DeReais(120), Contada: false, PeloTef: Dinheiro.DeReais(120), Conferida: false),
            };
            var resumo = ResumoFechamento.Linhas(linhas);
            var desvio = new Dinheiro(linhas.Sum(l => l.DiferencaConferida.Abs.Centavos));
            var papel = PapelDeCaixa.Fechamento("American Day Savassi", quando, sessao, "DAVID MATEUS",
                resumo, desvio, ResumoFechamento.SemConferencia(linhas), null, null, null,
                semContagem: false, autorizador: null, 32);

            foreach (var r in resumo.Where(r => r.Situacao != "confere"))
                checar(papel.Any(l => l.Trim() == r.Fim),
                    $"⭐ o desfecho impresso é o mesmo da tela: {r.Rotulo} sai como \"{r.Fim}\"");
            checar(!papel.Any(l => l.Contains("FALTA R$ 0,00")),
                "⭐ linha que ninguém conferiu nunca sai impressa como FALTA de R$ 0,00");
            checar(papel.Any(l => l.Contains("Diferença total") && l.Contains(desvio.Formatado())),
                "a diferença total impressa é a que quem fechou calculou");
            checar(!papel.Any(l => l.Contains("R$ 2,00") && l.Contains("tolerância", StringComparison.OrdinalIgnoreCase)),
                "a tolerância do caixa não aparece no papel");
        }

        // ── a abertura não entrega o esperado de amanhã ────────────────────────
        {
            var batendo = PapelDeCaixa.Abertura("American Day Savassi", quando, sessao, Dinheiro.DeReais(300), 32);
            checar(!batendo.Any(l => l.Contains("Esperado")),
                "⭐ contagem batendo: o papel NÃO imprime o esperado, que é o número que o operador conta às cegas");

            var faltando = PapelDeCaixa.Abertura("American Day Savassi", quando, sessao, Dinheiro.DeReais(320), 32);
            checar(faltando.Any(l => l.Contains("Esperado na gaveta") && l.Contains("R$ 320,00")),
                "divergiu: o esperado entra no papel, porque já foi visto na tela e já foi auditado");
            checar(faltando.Any(l => l.Contains("a menos") && l.Contains("R$ 20,00")),
                "e a diferença sai com as mesmas palavras da tela");

            var sobrando = PapelDeCaixa.Abertura("American Day Savassi", quando, sessao, Dinheiro.DeReais(280), 32);
            checar(sobrando.Any(l => l.Contains("a mais") && l.Contains("R$ 20,00")), "sobrando, sai 'a mais'");
        }

        // ── justificativa longa não estoura a bobina ───────────────────────────
        {
            var texto = string.Join(" ", Enumerable.Repeat("faltou troco no comeco do dia e peguei do cofre sem anotar", 6));
            foreach (var colunas in new[] { 32, 48 })
            {
                var papel = PapelDeCaixa.Fechamento("American Day Savassi", quando, sessao, "DAVID MATEUS",
                    ResumoFechamento.Linhas(new[] { new LinhaFechamento("dinheiro", Dinheiro.DeReais(100), Dinheiro.DeReais(100)) }),
                    Dinheiro.Zero, new List<string>(), null, null, texto, semContagem: false, autorizador: null, colunas);
                checar(papel.All(l => l.Length <= colunas), $"{colunas}: justificativa de {texto.Length} letras não estoura nenhuma linha");
                checar(papel.Any(l => l.StartsWith("Justificativa")), $"{colunas}: e ela aparece rotulada");
            }
            var palavra = PapelTexto.Quebrar(new string('x', 70), 32);
            checar(palavra.All(l => l.Length <= 32) && string.Concat(palavra) == new string('x', 70),
                "palavra maior que a linha é partida sem perder letra");
        }

        // ── o fechamento sem contagem sai rotulado, num banco de verdade ───────
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-papel-caixa-{Guid.NewGuid():N}.db");
        Banco.Migrar(arquivo);
        try
        {
            using var cx = Banco.Abrir(arquivo);
            var op = new Operador("pc-op", "Operador Papel", "operador");
            var ger = new Operador("pc-ger", "EDUARDO GERENTE", "gerente");
            Operadores.Salvar(cx, op.Id, op.Nome, "2211", "operador");
            Operadores.Salvar(cx, ger.Id, ger.Nome, "2212", "gerente");

            var s = Caixa.Abrir(cx, op, Dinheiro.DeReais(200));
            var linhasSem = Caixa.FecharSemConferencia(cx, s, op, ger);
            checar(linhasSem.Count >= 1 && linhasSem.All(l => !l.Contada && !l.Conferida),
                "⭐ fechar sem contagem devolve as linhas, todas marcadas como não contadas");

            var papel = PapelDeCaixa.Fechamento("American Day Savassi", quando, s, op.Nome,
                ResumoFechamento.Linhas(linhasSem), Dinheiro.Zero, ResumoFechamento.SemConferencia(linhasSem),
                null, null, null, semContagem: true, autorizador: ger.Nome, 32);
            checar(papel.Any(l => l.Contains("FECHADO SEM CONFERÊNCIA")),
                "⭐ o papel diz com todas as letras que ninguém contou a gaveta");
            checar(papel.Any(l => l.Contains("EDUARDO GERENTE")), "e diz quem autorizou");
            checar(!papel.Any(l => l.Contains("FALTA")), "sem contagem não existe falta: ninguém comparou nada");
            checar(papel.All(l => l.Length <= 32), "e continua cabendo na bobina");

            // a política nasce imprimindo sozinha, sem ninguém abrir a Configuração
            checar(Impressoes.Politica(cx, Impressoes.Abertura) == PoliticaImpressao.Automatico
                   && Impressoes.Politica(cx, Impressoes.Fechamento) == PoliticaImpressao.Automatico,
                "⭐ num caixa recém instalado os dois papéis já saem sozinhos");
            Impressoes.Gravar(cx, Impressoes.Abertura, PoliticaImpressao.Nao);
            checar(Impressoes.Politica(cx, Impressoes.Abertura) == PoliticaImpressao.Nao, "e a loja consegue desligar");
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM config WHERE chave LIKE 'imp_abertura%' AND chave <> 'imp_abertura'") == 0,
                "documento que nasceu hoje não inventa chave antiga nenhuma em config");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }

        // ── o caminho das telas, travado pelo fonte ────────────────────────────
        var venda = Fonte(Path.Combine("Telas", "Venda.xaml.cs")) ?? "";
        var abertura = Fonte(Path.Combine("Telas", "AberturaCaixa.xaml.cs")) ?? "";
        var config = Fonte(Path.Combine("Telas", "Configuracao.xaml.cs")) ?? "";

        checar(venda.Contains("Impressao.ImprimirTextoAsync(\"Fechamento de caixa\""),
            "o fechamento da venda manda o papel para a impressora");
        checar(venda.Split("ImprimirFechamentoAsync(dono, cx, linhasFech").Length == 3,
            "⭐ os DOIS caminhos do fechamento imprimem: o normal e o que passa pela justificativa");
        checar(venda.Contains("Anote à mão: operador, valores e data"),
            "se o papel não sair, o resumo diz o que anotar à mão");
        checar(abertura.Contains("Impressao.ImprimirTextoAsync(descricao"),
            "a abertura também manda papel para a impressora");
        checar(abertura.Split("PapelDeCaixa.Fechamento(").Length == 3,
            "⭐ os dois fechamentos da tela de abertura imprimem: o contado e o sem contagem");
        checar(abertura.Contains("private bool _abrindo") && abertura.Contains("if (_abrindo) return;"),
            "⭐ a abertura tem trava: o botão e o Enter chamam o mesmo método");
        checar(abertura.Contains("if (aberta is null) return;"),
            "caixa que não abriu não imprime papel de abertura");
        checar(abertura.Contains("ResumoFechamento.Texto(linhas)"),
            "o relatório da tela continua vindo do Núcleo");
        checar(config.Contains("Impressoes.Gravar(cx, Impressoes.Abertura") && config.Contains("Impressoes.Gravar(cx, Impressoes.Fechamento"),
            "a Configuração grava a escolha dos dois papéis");
        checar(config.Contains("EncherPolitica(CboPolAbertura") && config.Contains("EncherPolitica(CboPolFechamento"),
            "e mostra o que está valendo hoje");
    }

    /// <summary>Um fechamento de exemplo, com uma forma que sobrou, para as duas larguras.</summary>
    private static IReadOnlyList<string> Fechamento(Sessao sessao, int colunas)
    {
        var linhas = new List<LinhaFechamento>
        {
            new("dinheiro", Dinheiro.DeReais(521), Dinheiro.DeReais(519)),
            new("credito", Dinheiro.DeReais(120), Dinheiro.DeReais(120), Contada: false, PeloTef: Dinheiro.DeReais(120)),
        };
        return PapelDeCaixa.Fechamento("American Day Savassi", new DateTime(2026, 9, 18, 23, 41, 0),
            sessao, "DAVID MATEUS", ResumoFechamento.Linhas(linhas),
            new Dinheiro(linhas.Sum(l => l.DiferencaConferida.Abs.Centavos)),
            ResumoFechamento.SemConferencia(linhas),
            Dinheiro.DeReais(150), Dinheiro.DeReais(371), null, semContagem: false, autorizador: null, colunas);
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
