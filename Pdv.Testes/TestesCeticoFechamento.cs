using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// Revisão cética do fechamento novo (07/09/2026, "o cartão tem que vir do TEF").
/// Aqui só entram os casos que mexem em LANÇAMENTO e que a bateria da entrega não
/// cobria: forma que sumiu da pergunta, e fechamento gravado duas vezes.
/// </summary>
public static class TestesCeticoFechamento
{
    public static void Rodar(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-cetico-fech-{Guid.NewGuid():N}.db");
        Banco.Migrar(arquivo);
        try
        {
            using var cx = Banco.Abrir(arquivo);
            var op = new Operador("cet-op", "Cetico", "operador");
            Operadores.Salvar(cx, op.Id, op.Nome, "1111", "operador");

            SemTefFormaSemVenda(cx, op, checar);
            MesmaMaquininha(cx, op, checar);
            FecharDuasVezes(cx, op, checar);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }

    /// <summary>
    /// SEM TEF, forma sem venda no PDV. O roteiro tem que perguntar assim mesmo: é
    /// exatamente o dia em que a maquininha avulsa vendeu e NADA foi registrado, o caso
    /// que a contagem de cartão em caixa sem TEF existe para pegar. Perguntar só as
    /// formas com apurado maior que zero apagava esse alarme.
    /// </summary>
    private static void SemTefFormaSemVenda(SqliteConnection cx, Operador op, Action<bool, string> checar)
    {
        Vendas.GravarConfig(cx, "tef_habilitado", "0");
        var s = Caixa.Abrir(cx, op, Dinheiro.DeReais(100));
        Pagar(cx, s, op, "dinheiro", 20000, null);   // só dinheiro entrou no PDV

        checar(Caixa.FormasContadas(cx).Contains("credito"),
            "sem TEF, a regra do Nucleo diz que credito e forma CONTADA");

        var perguntadas = Caixa.PlanoDeConferencia(cx, s).Where(p => p.Conta).Select(p => p.Forma).ToArray();
        checar(perguntadas.Contains("credito"),
            $"sem TEF, a tela pergunta o credito mesmo sem venda no PDV (perguntou: {string.Join(",", perguntadas)})");

        // O operador digita os R$ 200 da maquininha: nasce a SOBRA, que é o alarme de
        // venda passada na maquina e nao registrada no PDV.
        var linhas = Caixa.Fechar(cx, s,
            new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.DeReais(300), ["credito"] = Dinheiro.DeReais(200) },
            op, new Dinheiro(200), "conferi a maquininha");
        var lc = linhas.FirstOrDefault(l => l.Forma == "credito");
        checar(lc is not null && lc.Situacao == "sobra" && lc.Diferenca.Centavos == 20000,
            "e a venda nao registrada aparece como SOBRA de R$ 200,00 no fechamento");
        Vendas.GravarConfig(cx, "tef_habilitado", "1");
    }

    /// <summary>
    /// RISCO CONHECIDO, medido e NÃO corrigido (precisa de decisão do dono).
    ///
    /// Com TEF, a pergunta da parte avulsa diz "na outra maquininha". Se o cartão avulso
    /// tiver passado na MESMA máquina (o fallback de quando o TEF falha na hora), o
    /// fechamento impresso traz TEF e avulso juntos: o operador digita o total e nasce
    /// uma SOBRA do tamanho do TEF, que é o espelho da falta que a entrega foi consertar.
    /// O PDV não sabe em qual máquina o avulso passou; este teste grava o número.
    /// </summary>
    private static void MesmaMaquininha(SqliteConnection cx, Operador op, Action<bool, string> checar)
    {
        var s = Caixa.Abrir(cx, op, Dinheiro.Zero);
        Pagar(cx, s, op, "credito", 310746, "AUT-TEF");   // passou pelo TEF
        Pagar(cx, s, op, "credito", 2000, null);          // fallback à mão

        var contagem = new Dictionary<string, Dinheiro>
        {
            ["dinheiro"] = Dinheiro.Zero,
            // o operador lê o fechamento da máquina: R$ 3.127,46 (as duas coisas juntas)
            ["credito"] = Dinheiro.DeReais(3127.46m),
        };
        var erro = "";
        try { Caixa.Fechar(cx, s, contagem, op, new Dinheiro(200)); }
        catch (InvalidOperationException ex) { erro = ex.Message; }

        checar(erro.Contains("sobra de " + Dinheiro.DeReais(3107.46m).Formatado()),
            "RISCO ABERTO: se o avulso passou na MESMA maquininha, o total dela vira sobra de R$ 3.107,46");

        Caixa.Fechar(cx, s, contagem, op, new Dinheiro(200), "risco conhecido, medido pela revisao");
    }

    /// <summary>
    /// Fechar o MESMO turno duas vezes. A tela ficou `async` e espera pelo TEF antes de
    /// qualquer diálogo; o Núcleo é a última trava. Sem ela sai um segundo jogo de linhas
    /// em `caixa_fechamento` e um segundo item na fila, com outra contagem.
    /// </summary>
    private static void FecharDuasVezes(SqliteConnection cx, Operador op, Action<bool, string> checar)
    {
        var s = Caixa.Abrir(cx, op, Dinheiro.DeReais(50));
        Pagar(cx, s, op, "dinheiro", 10000, null);

        Caixa.Fechar(cx, s, new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.DeReais(150) },
            op, new Dinheiro(200));
        var linhas1 = Linhas(cx, s);
        var fila1 = Fila(cx, s);

        var recusou = "";
        try
        {
            // segunda contagem, valor diferente: e o que um segundo toque produziria
            Caixa.Fechar(cx, s, new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.DeReais(90) },
                op, new Dinheiro(200), "segunda tentativa");
        }
        catch (InvalidOperationException ex) { recusou = ex.Message; }

        checar(recusou.Contains(Caixa.MarcaJaFechado),
            $"turno ja fechado nao fecha de novo (recusa: {(recusou.Length == 0 ? "NENHUMA" : recusou)})");
        checar(Linhas(cx, s) == linhas1 && Fila(cx, s) == fila1,
            $"e nada foi gravado duas vezes (linhas {linhas1}->{Linhas(cx, s)}, fila {fila1}->{Fila(cx, s)})");
        checar(cx.ExecuteScalar<long>(
            "SELECT declarado_cent FROM caixa_fechamento WHERE sessao_id=@S AND forma='dinheiro'", new { S = s.Id }) == 15000,
            "o fundo esperado do dia seguinte continua sendo a contagem que o operador viu (R$ 150,00)");

        // O caixa esquecido passa pela mesma trava.
        var super = new Operador("cet-sup", "Gerente", "gerente");
        Operadores.Salvar(cx, super.Id, super.Nome, "2222", "gerente");
        var recusou2 = "";
        try { Caixa.FecharSemConferencia(cx, s, op, super); }
        catch (InvalidOperationException ex) { recusou2 = ex.Message; }
        checar(recusou2.Contains(Caixa.MarcaJaFechado) && Linhas(cx, s) == linhas1,
            "e o fechamento sem conferencia do caixa esquecido tambem recusa turno ja fechado");
    }

    private static long Linhas(SqliteConnection cx, Sessao s)
        => cx.ExecuteScalar<long>("SELECT COUNT(*) FROM caixa_fechamento WHERE sessao_id=@S", new { S = s.Id });

    private static long Fila(SqliteConnection cx, Sessao s)
        => cx.ExecuteScalar<long>("SELECT COUNT(*) FROM outbox WHERE tipo='fechamento' AND ref_id=@S", new { S = s.Id });

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
}
