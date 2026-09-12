using Microsoft.Data.Sqlite;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// RETIRADA DE FECHAMENTO PARA O COFRE (12/09/2026, regra do dono: "após o fechamento do
/// dinheiro anotar quanto está sendo retirado e quanto está ficando no caixa; a retirada
/// imprime um papel com nome do operador, valor e data, que vai para o cofre com o
/// dinheiro para conferência depois").
///
/// A conta, a pergunta e o papel são puros (RetiradaCofre). O registro (sangria com
/// destino cofre na mesma transação do fechamento, fila para a nuvem, e a abertura do
/// dia seguinte esperando o que FICOU) é provado num banco de verdade, em arquivo
/// temporário. O caminho da tela é travado pelo fonte.
/// </summary>
public static class TestesRetiradaCofre
{
    public static void Rodar(Action<bool, string> checar)
    {
        // ── a conta ───────────────────────────────────────────────────────────
        var (ret, erro) = RetiradaCofre.Calcular(Dinheiro.DeReais(521), Dinheiro.DeReais(200));
        checar(erro is null && ret == Dinheiro.DeReais(321), "contou 521, fica 200: saem 321 para o cofre");
        var (_, erroMaior) = RetiradaCofre.Calcular(Dinheiro.DeReais(100), Dinheiro.DeReais(150));
        checar(erroMaior is not null && erroMaior.Contains("R$ 150,00") && erroMaior.Contains("R$ 100,00"),
            "deixar mais do que contou é recusado, dizendo os dois valores");
        checar(RetiradaCofre.Calcular(Dinheiro.DeReais(100), new Dinheiro(-1)).Erro is not null, "fica negativo é recusado");
        var (zero, erroZero) = RetiradaCofre.Calcular(Dinheiro.DeReais(200), Dinheiro.DeReais(200));
        checar(erroZero is null && zero == Dinheiro.Zero, "deixar tudo na gaveta: retirada zero, sem erro");

        // ── a pergunta ────────────────────────────────────────────────────────
        var pergunta = RetiradaCofre.Pergunta(Dinheiro.DeReais(521));
        checar(pergunta.Contains("R$ 521,00") && pergunta.Contains("fica") && pergunta.Contains("cofre"),
            "a pergunta repete o contado e diz que o resto vai para o cofre");
        checar(pergunta.Length <= 160 && !pergunta.Contains('—') && !pergunta.Contains('–'), "pergunta curta, sem travessão");

        // ── o papel ───────────────────────────────────────────────────────────
        var quando = new DateTime(2026, 9, 12, 22, 47, 0);
        foreach (var colunas in new[] { 32, 48 })
        {
            var papel = RetiradaCofre.Papel("American Day Savassi", quando, "DAVID MATEUS", "2026-09-12",
                Dinheiro.DeReais(521), Dinheiro.DeReais(200), Dinheiro.DeReais(321), colunas);
            checar(papel.All(l => l.Length <= colunas), $"em {colunas} colunas nenhuma linha estoura a bobina");
            checar(papel.Any(l => l.Contains("DAVID MATEUS")), $"{colunas}: o nome do operador está no papel");
            checar(papel.Any(l => l.Contains("12/09/2026 22:47")), $"{colunas}: data e hora em 24 h");
            checar(papel.Any(l => l.Contains("RETIRADO") && l.Contains("R$ 321,00")), $"{colunas}: o valor retirado está na linha RETIRADO");
            checar(papel.Any(l => l.Contains("R$ 200,00")) && papel.Any(l => l.Contains("R$ 521,00")), $"{colunas}: o que ficou e o contado também");
            checar(papel.Any(l => l.StartsWith("Assinatura")) && papel.Any(l => l.StartsWith("Conferido por")), $"{colunas}: tem espaço para assinar e para conferir depois");
            checar(papel.All(l => !l.Contains('—') && !l.Contains('–')), $"{colunas}: sem travessão");
        }

        // ── o registro ────────────────────────────────────────────────────────
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-retirada-cofre-{Guid.NewGuid():N}.db");
        Banco.Migrar(arquivo);
        try
        {
            using var cx = Banco.Abrir(arquivo);
            var op = new Operador("rc-op", "Retirada Op", "operador");
            Operadores.Salvar(cx, op.Id, op.Nome, "1111", "operador");
            var tol = new Dinheiro(200);

            // turno 1: abre com 200, fecha contando 200, deixa 150 → 50 vão para o cofre
            var s1 = Caixa.Abrir(cx, op, Dinheiro.DeReais(200));
            Caixa.Fechar(cx, s1, new() { ["dinheiro"] = Dinheiro.DeReais(200) }, op, tol, null, true, Dinheiro.DeReais(150));
            var mov = cx.QueryFirstOrDefault("SELECT tipo, valor_cent, destino, motivo, autorizado_por FROM caixa_movimento WHERE sessao_id=@S", new { S = s1.Id });
            checar(mov is not null && (string)mov.tipo == "sangria" && (long)mov.valor_cent == 5000 && (string)mov.destino == "cofre",
                "a retirada vira uma sangria de R$ 50,00 com destino cofre, na sessão fechada");
            checar(mov is not null && (string)mov.motivo == RetiradaCofre.Motivo, "o motivo diz que foi a retirada de fechamento");
            var fila = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo='movimento' AND ref_id IN (SELECT id FROM caixa_movimento WHERE sessao_id=@S)", new { S = s1.Id });
            checar(fila == 1, "a retirada entra na fila para subir ao painel (pdv_caixa_movimentos)");
            var ses = cx.QueryFirstOrDefault("SELECT fica_cent, retirada_cent, status FROM caixa_sessao WHERE id=@Id", new { Id = s1.Id });
            checar((long)ses.fica_cent == 15000 && (long)ses.retirada_cent == 5000 && (string)ses.status == "fechado",
                "a sessão guarda o que ficou e o que saiu, e está fechada");
            checar(Caixa.FundoEsperado(cx) == Dinheiro.DeReais(150), "a abertura de amanhã espera o que FICOU (150), não o que foi contado (200)");
            checar(Caixa.UltimaRetirada(cx, s1.Id) is { } ur && ur.Fica == Dinheiro.DeReais(150) && ur.Retirada == Dinheiro.DeReais(50), "UltimaRetirada devolve o par");
            var aud = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM auditoria WHERE evento='caixa_retirada_cofre'");
            checar(aud == 1, "a auditoria registra a retirada para o cofre");

            // turno 2: fecha SEM a pergunta (caminho antigo): nada muda, esperado = declarado
            var s2 = Caixa.Abrir(cx, op, Dinheiro.DeReais(150));
            Caixa.Fechar(cx, s2, new() { ["dinheiro"] = Dinheiro.DeReais(150) }, op, tol);
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM caixa_movimento WHERE sessao_id=@S", new { S = s2.Id }) == 0, "sem a pergunta, nenhuma sangria nasce");
            checar(Caixa.FundoEsperado(cx) == Dinheiro.DeReais(150), "sem a pergunta, o esperado é o declarado, como sempre foi");
            checar(Caixa.UltimaRetirada(cx, s2.Id) is null, "sem a pergunta, UltimaRetirada é null");

            // turno 3: deixar tudo (fica == contado): sessão anota, mas não há sangria
            var s3 = Caixa.Abrir(cx, op, Dinheiro.DeReais(150));
            Caixa.Fechar(cx, s3, new() { ["dinheiro"] = Dinheiro.DeReais(150) }, op, tol, null, true, Dinheiro.DeReais(150));
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM caixa_movimento WHERE sessao_id=@S", new { S = s3.Id }) == 0, "retirada zero não gera sangria");
            checar(Caixa.FundoEsperado(cx) == Dinheiro.DeReais(150), "e o esperado continua 150");

            // turno 4: fica maior que o contado é recusado ANTES de fechar: o turno segue aberto
            var s4 = Caixa.Abrir(cx, op, Dinheiro.DeReais(150));
            var recusou = false;
            try { Caixa.Fechar(cx, s4, new() { ["dinheiro"] = Dinheiro.DeReais(150) }, op, tol, null, true, Dinheiro.DeReais(500)); }
            catch (InvalidOperationException) { recusou = true; }
            checar(recusou && Caixa.SessaoAberta(cx)?.Id == s4.Id, "fica maior que o contado: recusa e o turno continua aberto");
            var recusouSemDinheiro = false;
            try { Caixa.Fechar(cx, s4, new() { ["credito"] = Dinheiro.DeReais(10) }, op, tol, null, true, Dinheiro.DeReais(1)); }
            catch (InvalidOperationException) { recusouSemDinheiro = true; }
            checar(recusouSemDinheiro, "retirada sem contagem de dinheiro é recusada");
            Caixa.Fechar(cx, s4, new() { ["dinheiro"] = Dinheiro.DeReais(150) }, op, tol, null, true, Dinheiro.DeReais(100));
            checar(Caixa.FundoEsperado(cx) == Dinheiro.DeReais(100), "depois da recusa, fecha certo e o esperado acompanha");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }

        // ── a tela ────────────────────────────────────────────────────────────
        var venda = Fonte(Path.Combine("Telas", "Venda.xaml.cs")) ?? "";
        var banco = Fonte(Path.Combine("Pdv.Nucleo", "Banco.cs")) ?? "";
        checar(venda.Contains("RetiradaCofre.Pergunta(contadoDinheiro)") && venda.Contains("PedirValor.MostrarComVoltar(dono, \"Fechamento de caixa\", RetiradaCofre.Pergunta("),
            "o fechamento pergunta quanto fica, com Voltar");
        checar(venda.Contains("tefDisponivel, fica)") && venda.Count(s => s == 'x') >= 0 && venda.Split("tefDisponivel, fica)").Length == 3,
            "os dois caminhos do fechamento (com e sem justificativa) passam o que ficou para o Núcleo");
        checar(venda.Contains("Impressao.ImprimirTextoAsync(\"Retirada para o cofre\"") && venda.Contains("Impressao.DestinoCupom("),
            "o papel da retirada sai na impressora e na bobina do cupom");
        checar(venda.Contains("Anote à mão: operador, valor e data"), "se o papel não sair, o resumo manda anotar à mão o que iria nele");
        checar(banco.Contains("ALTER TABLE caixa_sessao ADD COLUMN fica_cent INTEGER") && banco.Contains("ALTER TABLE caixa_sessao ADD COLUMN retirada_cent INTEGER"),
            "as colunas novas entram por ALTER (caixa da loja que já tem a tabela não fica para trás)");
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
