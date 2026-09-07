using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// CARTÃO NÃO SE DECLARA (07/09/2026, pedido do dono: "mas não tem que declarar nada
/// não, o cartão tem que vir do TEF").
///
/// O buraco que isto fecha, medido na loja: bastava UMA venda sair como POS avulsa para
/// a forma inteira voltar à contagem. A tela então perguntava o total de crédito do
/// turno, o operador digitava o que a maquininha avulsa mostrava, e o fechamento
/// comparava aquilo com o crédito TODO (avulso + TEF). Saía uma falta do tamanho exato
/// do TEF: R$ 3.107,46 num dia. Ninguém perdeu dinheiro; a pergunta é que estava errada.
///
/// A regra agora:
///  · o que o TEF liquidou entra sozinho, pelo valor do TEF, e não vira campo;
///  · o operador só responde pelo que dá para contar: a gaveta e o cartão que passou
///    fora do TEF (o fechamento da maquininha avulsa, que é fonte independente);
///  · TEF fora do ar não fabrica desvio: a forma fecha marcada SEM CONFERÊNCIA;
///  · o desvio soma só o que foi conferido de verdade.
/// </summary>
public static class TestesFechamentoCartao
{
    public static void Rodar(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-teste-fech-{Guid.NewGuid():N}.db");
        Banco.Migrar(arquivo);
        try
        {
            using var cx = Banco.Abrir(arquivo);
            var op = new Operador("fech-op", "Fechador", "operador");
            Operadores.Salvar(cx, op.Id, op.Nome, "1111", "operador");
            Vendas.GravarConfig(cx, "tef_habilitado", "1");

            ODiaDoDono(cx, op, checar);
            CartaoInteiroDoTef(cx, op, checar);
            TefForaDoAr(cx, op, checar);
            FormaQueNinguemContou(cx, op, checar);
            DesvioSoDoQueFoiConferido(cx, op, checar);
            SemTefNadaMuda(cx, op, checar);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
        Telas(checar);
    }

    // ── o dia que gerou o desvio falso de R$ 3.107,46 ─────────────────────────
    private static void ODiaDoDono(SqliteConnection cx, Operador op, Action<bool, string> checar)
    {
        var s = Caixa.Abrir(cx, op, Dinheiro.DeReais(100));
        Pagar(cx, s, op, "dinheiro", 50000, null);          // R$ 500 na gaveta
        Pagar(cx, s, op, "credito", 310746, "AUT-TEF");     // R$ 3.107,46 pelo TEF
        Pagar(cx, s, op, "credito", 2000, null);            // R$ 20,00 na maquininha avulsa

        var plano = Caixa.PlanoDeConferencia(cx, s);
        var credito = plano.Single(p => p.Forma == "credito");
        var dinheiro = plano.Single(p => p.Forma == "dinheiro");

        checar(credito.PeloTef.Centavos == 310746 && credito.AContar.Centavos == 2000,
            "credito: o TEF responde por R$ 3.107,46 e sobram R$ 20,00 para o operador conferir");
        checar(credito.Conta, "a parte fora do TEF ainda e perguntada (o fechamento da maquininha avulsa confere)");
        checar(dinheiro.Conta && dinheiro.PeloTef.Centavos == 0 && dinheiro.AContar.Centavos == 60000,
            "dinheiro continua sendo contado inteiro: R$ 600,00 na gaveta (fundo + venda)");
        checar(plano.First().Forma == "dinheiro", "a gaveta e sempre a primeira pergunta do fechamento");

        // O operador conta a gaveta e a maquininha avulsa. NADA de cartão do TEF.
        var linhas = FecharCego(cx, s, op,
            new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.DeReais(600), ["credito"] = Dinheiro.DeReais(20) },
            checar, "o dia do dono fecha sem justificativa");

        var lc = linhas.Single(l => l.Forma == "credito");
        checar(lc.Diferenca.Centavos == 0 && lc.Situacao == "confere",
            "o dia do dono fecha SEM desvio: a parte do TEF entra dos dois lados da conta");
        checar(lc.Declarado.Centavos == 312746 && lc.PeloTef.Centavos == 310746,
            "o declarado do credito e R$ 3.127,46 (R$ 3.107,46 do TEF + R$ 20,00 contados)");
        checar(linhas.Sum(l => l.DiferencaConferida.Abs.Centavos) == 0,
            "desvio do turno igual a zero (antes eram R$ 3.107,46 de falta inventada)");
    }

    // ── forma que o TEF liquidou inteira: nem pergunta ────────────────────────
    private static void CartaoInteiroDoTef(SqliteConnection cx, Operador op, Action<bool, string> checar)
    {
        var s = Caixa.Abrir(cx, op, Dinheiro.Zero);
        Pagar(cx, s, op, "credito", 20000, "AUT-A");
        Pagar(cx, s, op, "debito", 15000, null, nsu: "NSU-B");   // carimbo por NSU também é TEF

        var plano = Caixa.PlanoDeConferencia(cx, s);
        checar(plano.Where(p => p.Conta).Select(p => p.Forma).SequenceEqual(new[] { "dinheiro" }),
            "com tudo pelo TEF, a unica pergunta da tela e o dinheiro");
        checar(plano.Single(p => p.Forma == "debito").PeloTef.Centavos == 15000,
            "debito com carimbo de NSU tambem e valor do TEF, nao pergunta");

        var linhas = FecharCego(cx, s, op, new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.Zero },
            checar, "turno so de cartao TEF fecha sem justificativa");
        checar(linhas.Single(l => l.Forma == "credito") is { Contada: false, Conferida: true, Situacao: "confere" },
            "cartao do TEF fecha sozinho, conferido, sem o operador digitar nada");
    }

    // ── TEF fora do ar na hora de fechar ──────────────────────────────────────
    private static void TefForaDoAr(SqliteConnection cx, Operador op, Action<bool, string> checar)
    {
        var s = Caixa.Abrir(cx, op, Dinheiro.Zero);
        Pagar(cx, s, op, "credito", 90000, "AUT-C");

        // Fecha do mesmo jeito: caixa que não fecha porque a maquininha caiu vira turno
        // esquecido, que é sempre pior. O que muda é o RÓTULO da linha.
        var linhas = FecharCego(cx, s, op, new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.Zero },
            checar, "TEF fora do ar nao impede o caixa de fechar", tefDisponivel: false);

        var lc = linhas.Single(l => l.Forma == "credito");
        checar(!lc.Conferida && lc.Situacao == "sem_conferencia",
            "TEF fora do ar: a linha do cartao sai marcada como sem conferencia");
        checar(lc.Diferenca.Centavos == 0 && lc.DiferencaConferida.Centavos == 0,
            "e ela nao vira sobra nem falta: fica fora do desvio");
        checar(linhas.Single(l => l.Forma == "dinheiro").Conferida,
            "o dinheiro contado continua conferido (a gaveta nao depende da maquininha)");

        var detalhe = cx.ExecuteScalar<string>(
            "SELECT detalhe FROM auditoria WHERE evento='caixa_fechado' ORDER BY id DESC LIMIT 1") ?? "";
        checar(detalhe.Contains("credito:sem_conferencia"),
            "a auditoria guarda QUAL forma ficou sem conferencia (desvio zero nao e o mesmo que conferiu)");
        var payload = cx.ExecuteScalar<string>(
            "SELECT payload FROM outbox WHERE tipo='fechamento' ORDER BY id DESC LIMIT 1") ?? "";
        checar(payload.Contains("\"Situacao\":\"sem_conferencia\"") && payload.Contains("\"Conferida\":false"),
            "a nuvem recebe a linha como sem_conferencia (palavra que o painel ja entende)");
    }

    // ── forma que deveria ser contada e a tela não perguntou ──────────────────
    private static void FormaQueNinguemContou(SqliteConnection cx, Operador op, Action<bool, string> checar)
    {
        var s = Caixa.Abrir(cx, op, Dinheiro.Zero);
        Pagar(cx, s, op, "credito", 90000, null);   // R$ 900 na maquininha avulsa

        // Antes: forma contada ausente da contagem declarava ZERO, e nascia uma falta de
        // R$ 900 que ninguém cometeu — o caixa nem fechava sem justificativa.
        var linhas = FecharCego(cx, s, op, new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.Zero },
            checar, "forma nao perguntada nao impede o fechamento");

        var lc = linhas.Single(l => l.Forma == "credito");
        checar(lc.Diferenca.Centavos == 0 && lc.Situacao == "sem_conferencia",
            "forma que ninguem contou fecha pelo apurado e sai sem conferencia, nao como falta de R$ 900");
        checar(lc.Declarado.Centavos == 90000 && !lc.Contada,
            "e o valor gravado e o apurado, nao zero");
    }

    // ── o desvio só soma o que foi conferido ──────────────────────────────────
    private static void DesvioSoDoQueFoiConferido(SqliteConnection cx, Operador op, Action<bool, string> checar)
    {
        var s = Caixa.Abrir(cx, op, Dinheiro.Zero);
        Pagar(cx, s, op, "dinheiro", 10000, null);      // R$ 100 na gaveta
        Pagar(cx, s, op, "credito", 50000, "AUT-D");    // R$ 500 pelo TEF

        // Falta de verdade no dinheiro: R$ 10. O cartão está sem conferência (TEF fora
        // do ar) e não pode somar nada ao desvio.
        var erro = "";
        try
        {
            Caixa.Fechar(cx, s, new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.DeReais(90) },
                op, new Dinheiro(200), null, tefDisponivel: false);
        }
        catch (InvalidOperationException ex) { erro = ex.Message; }

        checar(erro.Contains(Dinheiro.DeReais(10).Formatado()) && erro.Contains("Justifique"),
            "a falta real de R$ 10,00 no dinheiro continua exigindo justificativa");
        checar(!erro.Contains("credito"),
            "e a mensagem nao acusa o credito: linha sem conferencia nao vira sobra nem falta");

        var linhas = Caixa.Fechar(cx, s, new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.DeReais(90) },
            op, new Dinheiro(200), "faltou troco no fim do dia", tefDisponivel: false);
        checar(linhas.Sum(l => l.DiferencaConferida.Abs.Centavos) == 1000,
            "o desvio do turno e R$ 10,00, e nao R$ 510,00");
        var detalhe = cx.ExecuteScalar<string>(
            "SELECT detalhe FROM auditoria WHERE evento='caixa_fechado' ORDER BY id DESC LIMIT 1") ?? "";
        checar(detalhe.Contains("desvio=" + Dinheiro.DeReais(10).Formatado()) && detalhe.Contains("credito:sem_conferencia"),
            "a auditoria grava desvio de R$ 10,00 E o credito sem conferencia, na mesma linha");
    }

    // ── sem TEF, tudo continua como era ───────────────────────────────────────
    private static void SemTefNadaMuda(SqliteConnection cx, Operador op, Action<bool, string> checar)
    {
        Vendas.GravarConfig(cx, "tef_habilitado", "0");
        var s = Caixa.Abrir(cx, op, Dinheiro.Zero);
        Pagar(cx, s, op, "credito", 5000, null);

        var plano = Caixa.PlanoDeConferencia(cx, s);
        var credito = plano.Single(p => p.Forma == "credito");
        checar(credito.Conta && credito.PeloTef.Centavos == 0 && credito.AContar.Centavos == 5000,
            "sem TEF o cartao volta a ser contado inteiro: a maquininha avulsa e a unica fonte");

        var linhas = Caixa.Fechar(cx, s,
            new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.Zero, ["credito"] = Dinheiro.DeReais(30) },
            op, new Dinheiro(200), "conferi duas vezes");
        checar(linhas.Single(l => l.Forma == "credito") is { Contada: true, Situacao: "falta" },
            "sem TEF, credito da maquininha abaixo do PDV continua sendo falta de verdade");
        Vendas.GravarConfig(cx, "tef_habilitado", "1");
    }

    // ── as duas telas do fechamento ───────────────────────────────────────────
    private static void Telas(Action<bool, string> checar)
    {
        var venda = Fonte(Path.Combine("Telas", "Venda.xaml.cs")) ?? "";
        var abertura = Fonte(Path.Combine("Telas", "AberturaCaixa.xaml.cs")) ?? "";
        var fechar = Trecho(venda, "private async void FecharCaixa(object sender, RoutedEventArgs e)",
            "private static void MostrarResultado");

        checar(fechar.Contains("Caixa.PlanoDeConferencia(cxf, _sessao)") && fechar.Contains("plano.Where(p => p.Conta)"),
            "a tela de venda pergunta APENAS o que o roteiro do Nucleo mandou perguntar");
        checar(!fechar.Contains("Caixa.FormasContadas("),
            "e nao decide mais sozinha quais formas contar (regra em dois lugares vira duas regras)");
        checar(fechar.Contains("p.PeloTef.Formatado()") && fechar.Contains("Você conta só o dinheiro"),
            "o valor do TEF aparece como LEITURA antes da contagem, nao como campo");
        checar(fechar.Contains("await TefRespondeAsync()") && fechar.Contains("tefDisponivel"),
            "a tela pergunta ao TEF se ele responde e leva a resposta para o fechamento");
        checar(fechar.Contains("MsgTefSemResposta"), "e mostra a linha de TEF fora do ar quando e o caso");

        // A espera do TEF devolve a tela ao operador: por ate 8 segundos o botao volta
        // a aceitar toque, e a trava de TEF conferida antes dela ficou velha. Sem isto,
        // dois toques fecham o MESMO turno duas vezes (duas linhas por forma, dois itens
        // na fila) e um estorno iniciado no meio nao segura o fechamento.
        checar(fechar.Contains("if (_fechandoCaixa) return;") && fechar.Contains("_fechandoCaixa = true;")
               && fechar.Contains("finally { _fechandoCaixa = false; }"),
            "o botao nao dispara um segundo fechamento enquanto o primeiro espera o TEF");
        checar(Trecho(fechar, "await TefRespondeAsync()", "var contagem").Contains("TefEmAndamento(dono)"),
            "e a trava de TEF e conferida DE NOVO depois da espera (ela ficou velha)");

        checar(abertura.Contains("Caixa.PlanoDeConferencia(cx, antiga)") && !abertura.Contains("Caixa.FormasContadas("),
            "o fechamento do caixa esquecido usa o MESMO roteiro (nao pergunta cartao do TEF)");

        var resultado = Trecho(venda, "private static void MostrarResultado", "private static string Rotulo");
        checar(resultado.Contains("\"sem_conferencia\" => \"sem conferência\""),
            "o relatorio mostra \"sem conferência\", nunca FALTA de R$ 0,00");
        checar(resultado.Contains("l.DiferencaConferida.Abs.Centavos"),
            "e o total da tela usa a MESMA conta do Nucleo: so o que foi conferido");

        // Texto de tela: curto, humano, sem travessão (o dono lê tudo).
        var msg = Pdv.Telas.Venda.MsgTefSemResposta;
        checar(msg.Length <= 90 && !msg.Contains('—') && msg.StartsWith("A maquininha não respondeu"),
            $"o aviso de TEF fora do ar cabe numa linha e nao tem travessao (\"{msg}\")");
    }

    // ── util ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fecha o turno com a contagem cega e devolve as linhas. Recusa do Núcleo vira UMA
    /// falha com o motivo e a bateria segue (o turno fecha com justificativa para não
    /// travar os casos seguintes) — senão o primeiro caso vermelho derruba a suíte
    /// inteira e ninguém enxerga os outros.
    /// </summary>
    private static List<LinhaFechamento> FecharCego(SqliteConnection cx, Sessao s, Operador op,
        Dictionary<string, Dinheiro> contagem, Action<bool, string> checar, string oQue,
        bool tefDisponivel = true)
    {
        var tolerancia = new Dinheiro(200);
        try { return Caixa.Fechar(cx, s, contagem, op, tolerancia, null, tefDisponivel); }
        catch (InvalidOperationException ex)
        {
            checar(false, $"{oQue} · o fechamento recusou: {ex.Message}");
            return Caixa.Fechar(cx, s, contagem, op, tolerancia, "prova do vermelho", tefDisponivel);
        }
    }

    /// <summary>Uma venda finalizada com um pagamento. `aut`/`nsu` nulos = POS avulso.</summary>
    private static void Pagar(SqliteConnection cx, Sessao s, Operador op, string forma, long cent,
        string? aut, string? nsu = null)
    {
        var id = Guid.NewGuid().ToString();
        cx.Execute("""
            INSERT INTO venda (id, client_key, sessao_id, business_date, numero_local, operador_id,
                               subtotal_cent, total_cent, status, criada_em, finalizada_em)
            VALUES (@Id,@K,@S,@Bd,@N,@Op,@T,@T,'finalizada',@Em,@Em)
            """,
            new { Id = id, K = id, S = s.Id, Bd = s.BusinessDate,
                  N = cx.ExecuteScalar<int>("SELECT COALESCE(MAX(numero_local),0)+1 FROM venda WHERE business_date=@B",
                          new { B = s.BusinessDate }),
                  Op = op.Id, T = cent, Em = DateTime.Now.ToString("o") });
        cx.Execute("INSERT INTO venda_pagamento (id,venda_id,forma,valor_cent,troco_cent,tef_aut,tef_nsu) VALUES (@i,@v,@f,@c,0,@a,@n)",
            new { i = Guid.NewGuid().ToString(), v = id, f = forma, c = cent, a = aut, n = nsu });
    }

    private static string Trecho(string todo, string de, string ate)
    {
        var i = todo.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var f = todo.IndexOf(ate, i + de.Length, StringComparison.Ordinal);
        return f < 0 ? "" : todo[i..f];
    }

    /// <summary>Sobe do binário do teste até achar o arquivo pedido no repositório.</summary>
    private static string? Fonte(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidato = Path.Combine(dir.FullName, relativo);
            if (File.Exists(candidato)) return File.ReadAllText(candidato);
        }
        return null;
    }
}
