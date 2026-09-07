using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// O dia do dono COM a maquininha fora do ar: contar a parte avulsa NAO confere a
/// parte do TEF. A tela avisa que o cartao fica sem conferencia; o registro tem que
/// dizer a mesma coisa, senao a auditoria grava "conferiu" sobre R$ 3.107,46 que
/// ninguem olhou.
public static class TestesFechamentoTefMudo
{
    public static void Rodar(Action<bool, string> checar)
    {
        var arq = Path.Combine(Path.GetTempPath(), $"pdv-repro-{Guid.NewGuid():N}.db");
        Banco.Migrar(arq);
        try
        {
            using var cx = Banco.Abrir(arq);
            var op = new Operador("repro-op", "Repro", "operador");
            Operadores.Salvar(cx, op.Id, op.Nome, "1111", "operador");
            Vendas.GravarConfig(cx, "tef_habilitado", "1");

            var s = Caixa.Abrir(cx, op, Dinheiro.Zero);
            Pagar(cx, s, op, "credito", 310746, "AUT-TEF");  // R$ 3.107,46 pelo TEF
            Pagar(cx, s, op, "credito", 2000, null);         // R$ 20,00 na avulsa

            var plano = Caixa.PlanoDeConferencia(cx, s);
            var pc = plano.Single(p => p.Forma == "credito");
            checar(pc.Conta && pc.PeloTef.Centavos == 310746,
                $"cenario montado: a tela pergunta o credito e o TEF responde por R$ 3.107,46 (conta={pc.Conta})");

            // TEF FORA DO AR (tefDisponivel: false) e o operador conta os R$ 20 da avulsa.
            var linhas = Caixa.Fechar(cx, s,
                new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.Zero, ["credito"] = Dinheiro.DeReais(20) },
                op, new Dinheiro(200), null, tefDisponivel: false);

            var lc = linhas.Single(l => l.Forma == "credito");
            checar(!lc.Conferida,
                $"TEF fora do ar: a linha do credito NAO pode sair conferida (veio Conferida={lc.Conferida}, Situacao={lc.Situacao})");
            checar(lc.Situacao == "sem_conferencia",
                $"e a situacao tem que ser sem_conferencia, nao \"{lc.Situacao}\" (a tela acabou de dizer que o cartao ficou sem conferencia)");

            var det = cx.ExecuteScalar<string>(
                "SELECT detalhe FROM auditoria WHERE evento='caixa_fechado' ORDER BY id DESC LIMIT 1") ?? "";
            checar(det.Contains("credito:sem_conferencia"),
                $"a auditoria tem que guardar o credito sem conferencia (veio: {det})");

            var pay = cx.ExecuteScalar<string>(
                "SELECT payload FROM outbox WHERE tipo='fechamento' ORDER BY id DESC LIMIT 1") ?? "";
            checar(pay.Contains("\"Conferida\":false"),
                "a nuvem tem que receber Conferida=false nessa linha");

            // REGRESSAO: marcar a linha como nao conferida NAO pode engolir a falta de
            // verdade na parte avulsa (aquela o operador contou, e ela e conferivel).
            var s2 = Caixa.Abrir(cx, op, Dinheiro.Zero);
            Pagar(cx, s2, op, "credito", 310746, "AUT-TEF2");
            Pagar(cx, s2, op, "credito", 2000, null);
            var erro = "";
            try
            {
                Caixa.Fechar(cx, s2,
                    new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.Zero, ["credito"] = Dinheiro.DeReais(15) },
                    op, new Dinheiro(200), null, tefDisponivel: false);
            }
            catch (InvalidOperationException ex) { erro = ex.Message; }
            checar(erro.Contains("Justifique") && erro.Contains(Dinheiro.DeReais(5).Formatado()),
                $"com o TEF fora do ar, a falta de R$ 5,00 na maquininha avulsa AINDA exige justificativa (erro: {(erro == "" ? "fechou calado" : erro)})");
            var l2 = Caixa.Fechar(cx, s2,
                new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.Zero, ["credito"] = Dinheiro.DeReais(15) },
                op, new Dinheiro(200), "conferi a avulsa", tefDisponivel: false).Single(l => l.Forma == "credito");
            checar(l2.Situacao == "falta",
                $"e essa linha aparece como falta de verdade, nao escondida (veio {l2.Situacao})");
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(arq); } catch { } }
    }

    private static void Pagar(SqliteConnection cx, Sessao s, Operador op, string forma, long cent, string? aut)
    {
        var id = Guid.NewGuid().ToString();
        cx.Execute("""
            INSERT INTO venda (id, client_key, sessao_id, business_date, numero_local, operador_id,
                               subtotal_cent, total_cent, status, criada_em, finalizada_em)
            VALUES (@Id,@K,@S,@Bd,@N,@Op,@T,@T,'finalizada',@Em,@Em)
            """,
            new { Id = id, K = id, S = s.Id, Bd = s.BusinessDate,
                  N = cx.ExecuteScalar<int>("SELECT COALESCE(MAX(numero_local),0)+1 FROM venda WHERE business_date=@B", new { B = s.BusinessDate }),
                  Op = op.Id, T = cent, Em = DateTime.Now.ToString("o") });
        cx.Execute("INSERT INTO venda_pagamento (id,venda_id,forma,valor_cent,troco_cent,tef_aut,tef_nsu) VALUES (@i,@v,@f,@c,0,@a,null)",
            new { i = Guid.NewGuid().ToString(), v = id, f = forma, c = cent, a = aut });
    }
}
