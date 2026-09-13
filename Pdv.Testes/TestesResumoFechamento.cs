using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// RESUMO DO FECHAMENTO PARTIDO EM TEF E POS (13/09/2026, pedido do dono: "relatório de
/// fechamento de caixa tem como segmentar crédito em Crédito TEF e Crédito POS. Caso POS
/// seja 0, mostra 0,00. O mesmo com débito". Exemplo dele: "Crédito TEF X,XX, Crédito POS
/// Y,YY").
///
/// A montagem mora em Pdv.Nucleo/ResumoFechamento e as duas telas (fechamento normal e
/// caixa esquecido) usam ela. Aqui se prova a partição: a parte TEF nunca tem sobra nem
/// falta, a diferença inteira é da parte POS, e crédito e débito saem sempre em duas
/// linhas, com R$ 0,00 quando uma parte não teve venda.
/// </summary>
public static class TestesResumoFechamento
{
    private const int Colunas = 82;   // Consolas 14 na janela de 720 px (TestesPendencias)

    public static void Rodar(Action<bool, string> checar)
    {
        TefEPos(checar);
        SoTef(checar);
        SoPos(checar);
        PosComFalta(checar);
        TefSemConferencia(checar);
        NinguemContou(checar);
        OrdemELargura(checar);
        NoBancoDeVerdade(checar);
        Telas(checar);
    }

    private static Dinheiro R(decimal v) => Dinheiro.DeReais(v);

    private static LinhaDoResumo? Parte(List<LinhaDoResumo> r, string rotulo) => r.SingleOrDefault(x => x.Rotulo == rotulo);

    private static string Linha(string texto, string rotulo)
        => texto.Split('\n').FirstOrDefault(x => x.StartsWith(rotulo + " ", StringComparison.Ordinal)) ?? "";

    // ── o dia do dono: TEF e POS no mesmo turno ──────────────────────────────
    private static void TefEPos(Action<bool, string> checar)
    {
        var linhas = new List<LinhaFechamento>
        {
            new("dinheiro", R(150), R(150)),
            new("credito", R(3127.46m), R(3127.46m), true, R(3107.46m), true),
        };
        var r = ResumoFechamento.Linhas(linhas);
        var tef = Parte(r, "Crédito TEF");
        var pos = Parte(r, "Crédito POS");
        checar(tef is { Origem: "máquina", Situacao: "confere" } && tef.Declarado == R(3107.46m) && tef.Esperado == R(3107.46m),
            $"TEF e POS: Crédito TEF sai com R$ 3.107,46 e confere (veio {tef?.Texto})");
        checar(pos is { Origem: "contou", Situacao: "confere" } && pos.Declarado == R(20) && pos.Esperado == R(20),
            $"TEF e POS: Crédito POS sai com o contado, R$ 20,00, e confere (veio {pos?.Texto})");

        var texto = ResumoFechamento.Texto(linhas);
        var lt = Linha(texto, "Crédito TEF");
        var lp = Linha(texto, "Crédito POS");
        checar(lt.Contains(R(3107.46m).Formatado()) && lt.EndsWith("confere")
               && lp.Contains(R(20).Formatado()) && lp.EndsWith("confere"),
            $"o texto tem as duas linhas do exemplo do dono (\"{lt}\" / \"{lp}\")");
        checar(!texto.Split('\n').Any(x => x.StartsWith("Crédito ", StringComparison.Ordinal)
                                          && !x.StartsWith("Crédito TEF", StringComparison.Ordinal)
                                          && !x.StartsWith("Crédito POS", StringComparison.Ordinal)),
            "nao sobra linha de \"Crédito\" inteiro junto das duas partes");
    }

    // ── só TEF: POS sai R$ 0,00 ─────────────────────────────────────────────
    private static void SoTef(Action<bool, string> checar)
    {
        // O TEF liquidou tudo: a forma nem entra na pergunta e fecha pelo apurado.
        var linhas = new List<LinhaFechamento>
        {
            new("dinheiro", R(80), R(80)),
            new("credito", R(50), R(50), false, R(50), true),
        };
        var r = ResumoFechamento.Linhas(linhas);
        var tef = Parte(r, "Crédito TEF");
        var pos = Parte(r, "Crédito POS");
        checar(tef is { Situacao: "confere" } && tef.Declarado == R(50),
            $"so TEF: Crédito TEF R$ 50,00 confere (veio {tef?.Texto})");
        checar(pos is { Situacao: "confere", SemConferencia: false } && pos.Declarado == Dinheiro.Zero && pos.Esperado == Dinheiro.Zero,
            $"so TEF: Crédito POS aparece mesmo zerado, com R$ 0,00 (veio {pos?.Texto})");
        var lp = Linha(ResumoFechamento.Texto(linhas), "Crédito POS");
        checar(lp.Contains(Dinheiro.Zero.Formatado()) && lp.EndsWith("confere"),
            $"a linha escrita e \"Crédito POS ... R$ 0,00 ... confere\" (veio \"{lp}\")");

        // Débito sem venda nenhuma: as duas partes aparecem do mesmo jeito.
        var dt = Parte(r, "Débito TEF");
        var dp = Parte(r, "Débito POS");
        checar(dt is { Situacao: "confere" } && dt.Declarado == Dinheiro.Zero
               && dp is { Situacao: "confere" } && dp.Declarado == Dinheiro.Zero,
            "débito sem venda: Débito TEF e Débito POS saem os dois com R$ 0,00");
        checar(ResumoFechamento.SemConferencia(linhas).Count == 0,
            "e nada disso vira \"sem conferência\"");
    }

    // ── só POS: TEF sai R$ 0,00 ─────────────────────────────────────────────
    private static void SoPos(Action<bool, string> checar)
    {
        // Caixa sem TEF (ou venda que passou na maquininha avulsa): o operador contou tudo.
        var linhas = new List<LinhaFechamento>
        {
            new("dinheiro", R(0), R(0)),
            new("credito", R(50), R(50), true, Dinheiro.Zero, true),
            new("debito", R(35), R(30), true, Dinheiro.Zero, true),
        };
        var r = ResumoFechamento.Linhas(linhas);
        var tef = Parte(r, "Crédito TEF");
        var pos = Parte(r, "Crédito POS");
        checar(tef is { Situacao: "confere", SemConferencia: false } && tef.Declarado == Dinheiro.Zero,
            $"so POS: Crédito TEF sai com R$ 0,00 e confere (veio {tef?.Texto})");
        checar(pos is { Origem: "contou", Situacao: "confere" } && pos.Declarado == R(50) && pos.Esperado == R(50),
            $"so POS: Crédito POS leva o valor inteiro, R$ 50,00 (veio {pos?.Texto})");

        var dt = Parte(r, "Débito TEF");
        var dp = Parte(r, "Débito POS");
        checar(dt is { Situacao: "confere" } && dt.Diferenca == Dinheiro.Zero
               && dp is { Situacao: "sobra" } && dp.Diferenca == R(5) && dp.Fim == "SOBRA " + R(5).Formatado(),
            $"sobra no débito da maquininha avulsa cai no Débito POS, nunca no TEF (veio {dp?.Texto})");
    }

    // ── POS com falta ───────────────────────────────────────────────────────
    private static void PosComFalta(Action<bool, string> checar)
    {
        // TEF R$ 3.107,46, avulsa registrou R$ 20,00 e o operador contou R$ 15,00.
        var linhas = new List<LinhaFechamento>
        {
            new("dinheiro", R(100), R(100)),
            new("credito", R(3122.46m), R(3127.46m), true, R(3107.46m), true),
        };
        var r = ResumoFechamento.Linhas(linhas);
        var tef = Parte(r, "Crédito TEF");
        var pos = Parte(r, "Crédito POS");
        checar(tef is { Situacao: "confere" } && tef.Diferenca == Dinheiro.Zero && tef.Declarado == R(3107.46m),
            $"POS com falta: a parte TEF continua conferindo, sem diferença (veio {tef?.Texto})");
        checar(pos is { Situacao: "falta" } && pos.Declarado == R(15) && pos.Esperado == R(20)
               && pos.Fim == "FALTA " + R(5).Formatado(),
            $"POS com falta: Crédito POS contou R$ 15,00, esperado R$ 20,00, FALTA R$ 5,00 (veio {pos?.Texto})");

        // A "Diferença total" da tela é a soma do Núcleo; a partição não pode mudar o número.
        var totalNucleo = linhas.Sum(l => l.DiferencaConferida.Abs.Centavos);
        var totalResumo = r.Sum(x => x.Diferenca.Abs.Centavos);
        checar(totalNucleo == totalResumo && totalResumo == 500,
            $"a diferença total do resumo e a do Núcleo sao a mesma (resumo {totalResumo}, núcleo {totalNucleo})");
    }

    // ── TEF sem conferência ─────────────────────────────────────────────────
    private static void TefSemConferencia(Action<bool, string> checar)
    {
        // Maquininha muda na hora de fechar; a avulsa foi contada certinha.
        var linhas = new List<LinhaFechamento>
        {
            new("dinheiro", R(0), R(0)),
            new("credito", R(3127.46m), R(3127.46m), true, R(3107.46m), false),
        };
        var r = ResumoFechamento.Linhas(linhas, tefDisponivel: false);
        var tef = Parte(r, "Crédito TEF");
        var pos = Parte(r, "Crédito POS");
        checar(tef is { Situacao: "sem_conferencia", SemConferencia: true } && tef.Fim == "sem conferência"
               && tef.Declarado == R(3107.46m),
            $"TEF mudo: Crédito TEF sai \"sem conferência\", com o valor que o sistema registrou (veio {tef?.Texto})");
        checar(pos is { Situacao: "confere", SemConferencia: false } && pos.Declarado == R(20),
            $"TEF mudo: a parte POS foi contada e bateu, então confere (veio {pos?.Texto})");
        var nomes = ResumoFechamento.SemConferencia(linhas, false);
        checar(nomes.SequenceEqual(new[] { "Crédito TEF" }),
            $"a tela nomeia a PARTE sem conferência, \"Crédito TEF\" (veio: {string.Join(", ", nomes)})");
        checar(!ResumoFechamento.Texto(linhas, false).Contains("FALTA"),
            "sem conferência nunca vira FALTA de R$ 0,00");

        // A linha já diz que o TEF ficou mudo: mesmo quem chama sem passar o TEF (caixa
        // esquecido) nao pode escrever "confere" sobre o que ninguém olhou.
        checar(Parte(ResumoFechamento.Linhas(linhas), "Crédito TEF") is { SemConferencia: true },
            "com a linha contada, a própria linha diz que o TEF ficou mudo (sem depender do chamador)");

        // O cenário de TestesFechamentoTefMudo: TEF mudo E falta de R$ 5,00 na avulsa.
        var comFalta = new List<LinhaFechamento>
            { new("credito", R(3122.46m), R(3127.46m), true, R(3107.46m), false) };
        var r2 = ResumoFechamento.Linhas(comFalta, false);
        checar(Parte(r2, "Crédito TEF") is { Situacao: "sem_conferencia" }
               && Parte(r2, "Crédito POS") is { Situacao: "falta" } p2 && p2.Fim == "FALTA " + R(5).Formatado(),
            "TEF mudo com falta na avulsa: TEF sem conferência e POS FALTA R$ 5,00, cada um no seu lugar");
    }

    // ── forma que ninguém contou ────────────────────────────────────────────
    private static void NinguemContou(Action<bool, string> checar)
    {
        // Só nasce chamando o Fechar direto: R$ 20,00 fora do TEF que ninguém contou.
        var linhas = new List<LinhaFechamento>
            { new("credito", R(3127.46m), R(3127.46m), false, R(3107.46m), false) };
        var comTef = ResumoFechamento.Linhas(linhas, tefDisponivel: true);
        checar(Parte(comTef, "Crédito TEF") is { Situacao: "confere" }
               && Parte(comTef, "Crédito POS") is { Situacao: "sem_conferencia", Origem: "" } p && p.Esperado == R(20),
            "TEF respondeu e ninguém contou a avulsa: TEF confere e só o POS fica sem conferência");
        var semTef = ResumoFechamento.Linhas(linhas, tefDisponivel: false);
        checar(Parte(semTef, "Crédito TEF") is { SemConferencia: true } && Parte(semTef, "Crédito POS") is { SemConferencia: true },
            "TEF mudo e ninguém contou: as duas partes ficam sem conferência");

        // PIX e Refeição não se partem: uma linha só, com a regra de sempre.
        var pix = new List<LinhaFechamento>
        {
            new("pix", R(40), R(45), true, R(30), false),
            new("voucher", R(12), R(12), true, Dinheiro.Zero, true),
        };
        var rp = ResumoFechamento.Linhas(pix, false);
        checar(Parte(rp, "PIX") is { Situacao: "falta", SemConferencia: true, Origem: "contou" }
               && Parte(rp, "Refeição") is { Situacao: "confere" }
               && !rp.Any(x => x.Rotulo.StartsWith("PIX ", StringComparison.Ordinal)),
            "PIX e Refeição seguem numa linha só (sem \"PIX TEF\")");
    }

    // ── ordem fixa, largura e texto ─────────────────────────────────────────
    private static void OrdemELargura(Action<bool, string> checar)
    {
        var embaralhado = new List<LinhaFechamento>
        {
            new("voucher", R(12), R(12)),
            new("outros", R(1), R(1)),
            new("pix", R(40), R(40)),
            new("credito", R(70), R(70), true, R(50), true),
            new("dinheiro", R(150), R(150)),
        };
        var rotulos = ResumoFechamento.Linhas(embaralhado).Select(x => x.Rotulo).ToList();
        var esperada = new[] { "Dinheiro", "Crédito TEF", "Crédito POS", "Débito TEF", "Débito POS", "PIX", "Refeição", "outros" };
        checar(rotulos.SequenceEqual(esperada),
            $"a ordem é fixa, não a do banco (veio: {string.Join(", ", rotulos)})");

        var vazio = ResumoFechamento.Linhas(new List<LinhaFechamento>());
        checar(vazio.Select(x => x.Rotulo).SequenceEqual(new[] { "Crédito TEF", "Crédito POS", "Débito TEF", "Débito POS" })
               && vazio.All(x => x.Declarado == Dinheiro.Zero && x.Situacao == "confere"),
            "turno sem venda de cartão: as quatro linhas de crédito e débito saem com R$ 0,00");

        var texto = ResumoFechamento.Texto(embaralhado);
        var saida = texto.Split('\n');
        checar(!texto.Contains('—') && !texto.Contains('–'), "nenhuma linha do resumo tem travessão");
        var coluna = saida.Select(x => x.IndexOf("esperado", StringComparison.Ordinal)).Distinct().ToList();
        checar(coluna.Count == 1 && coluna[0] > 0,
            $"a palavra \"esperado\" cai na mesma coluna em todas as linhas (colunas: {string.Join(",", coluna)})");

        var grande = new List<LinhaFechamento>
        {
            new("credito", R(102_626.50m + 99_999.99m), R(205_253m + 99_999.99m), true, R(99_999.99m), false),
        };
        var maior = ResumoFechamento.Texto(grande, false).Split('\n').Max(x => x.Length);
        checar(maior <= Colunas,
            $"a linha mais larga do resumo cabe na tela do relatório ({maior} de {Colunas} colunas)");
    }

    // ── pelo Caixa.Fechar de verdade ────────────────────────────────────────
    private static void NoBancoDeVerdade(Action<bool, string> checar)
    {
        var arq = Path.Combine(Path.GetTempPath(), $"pdv-resumo-{Guid.NewGuid():N}.db");
        Banco.Migrar(arq);
        try
        {
            using var cx = Banco.Abrir(arq);
            var op = new Operador("resumo-op", "Resumo", "operador");
            Operadores.Salvar(cx, op.Id, op.Nome, "1111", "operador");
            Vendas.GravarConfig(cx, "tef_habilitado", "1");

            // O dia do dono: R$ 3.107,46 pelo TEF e R$ 20,00 na avulsa, contados certo.
            var s = Caixa.Abrir(cx, op, Dinheiro.Zero);
            Pagar(cx, s, op, "credito", 310746, "AUT-RES1");
            Pagar(cx, s, op, "credito", 2000, null);
            var l1 = Caixa.Fechar(cx, s,
                new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.Zero, ["credito"] = R(20) },
                op, new Dinheiro(200));
            var r1 = ResumoFechamento.Linhas(l1, true);
            checar(Parte(r1, "Crédito TEF") is { Situacao: "confere" } t1 && t1.Declarado == R(3107.46m)
                   && Parte(r1, "Crédito POS") is { Situacao: "confere" } p1 && p1.Declarado == R(20)
                   && Parte(r1, "Débito TEF") is { } dt1 && dt1.Declarado == Dinheiro.Zero
                   && Parte(r1, "Débito POS") is { } dp1 && dp1.Declarado == Dinheiro.Zero,
                $"no banco: Crédito TEF R$ 3.107,46, Crédito POS R$ 20,00, Débito TEF e POS R$ 0,00 (veio:\n{ResumoFechamento.Texto(l1)})");

            // Maquininha muda e falta de R$ 5,00 na avulsa, com justificativa.
            var s2 = Caixa.Abrir(cx, op, Dinheiro.Zero);
            Pagar(cx, s2, op, "credito", 310746, "AUT-RES2");
            Pagar(cx, s2, op, "credito", 2000, null);
            var l2 = Caixa.Fechar(cx, s2,
                new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.Zero, ["credito"] = R(15) },
                op, new Dinheiro(200), "conferi a avulsa duas vezes", tefDisponivel: false);
            var r2 = ResumoFechamento.Linhas(l2, false);
            checar(Parte(r2, "Crédito TEF") is { Situacao: "sem_conferencia" }
                   && Parte(r2, "Crédito POS") is { Situacao: "falta" } p2 && p2.Diferenca.Abs == R(5),
                $"no banco, TEF mudo: Crédito TEF sem conferência e Crédito POS FALTA R$ 5,00 (veio:\n{ResumoFechamento.Texto(l2, false)})");

            // Caixa sem TEF: o crédito inteiro é POS e o TEF sai zerado.
            Vendas.GravarConfig(cx, "tef_habilitado", "0");
            var s3 = Caixa.Abrir(cx, op, Dinheiro.Zero);
            Pagar(cx, s3, op, "credito", 5000, null);
            var l3 = Caixa.Fechar(cx, s3,
                new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.Zero, ["credito"] = R(50) },
                op, new Dinheiro(200));
            var r3 = ResumoFechamento.Linhas(l3, true);
            checar(Parte(r3, "Crédito TEF") is { Situacao: "confere" } t3 && t3.Declarado == Dinheiro.Zero
                   && Parte(r3, "Crédito POS") is { Situacao: "confere" } p3 && p3.Declarado == R(50),
                $"no banco, sem TEF: Crédito TEF R$ 0,00 e Crédito POS R$ 50,00 (veio:\n{ResumoFechamento.Texto(l3)})");
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(arq); } catch { } }
    }

    // ── as duas telas usam a mesma montagem ─────────────────────────────────
    private static void Telas(Action<bool, string> checar)
    {
        var venda = Fonte(Path.Combine("Telas", "Venda.xaml.cs")) ?? "";
        var abertura = Fonte(Path.Combine("Telas", "AberturaCaixa.xaml.cs")) ?? "";
        var resultado = Trecho(venda, "private static void MostrarResultado", "public static string PerguntaDoFechamento");
        checar(resultado.Contains("ResumoFechamento.Texto(linhas, tefDisponivel)")
               && resultado.Contains("ResumoFechamento.SemConferencia(linhas, tefDisponivel)")
               && !resultado.Contains("esperado {"),
            "o \"Caixa fechado\" monta as linhas pelo Núcleo, sem cópia do formato na tela");
        var fechar = Trecho(venda, "private async void FecharCaixa(object sender, RoutedEventArgs e)", "private static void MostrarResultado");
        checar(fechar.Split("papel, tefDisponivel);").Length == 3,
            "os dois caminhos do fechamento (com e sem justificativa) levam a resposta do TEF ao resumo");
        checar(abertura.Contains("ResumoFechamento.Texto(linhas)") && !abertura.Contains("esperado {"),
            "o fechamento do caixa esquecido usa a MESMA montagem (a cópia do formato saiu)");
    }

    // ── util ────────────────────────────────────────────────────────────────
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

    private static string Trecho(string todo, string de, string ate)
    {
        var i = todo.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var f = todo.IndexOf(ate, i + de.Length, StringComparison.Ordinal);
        return f < 0 ? "" : todo[i..f];
    }

    private static string? Fonte(string relativo)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidato = Path.Combine(dir, relativo);
            if (File.Exists(Path.Combine(dir, "Pdv.csproj")) && File.Exists(candidato)) return File.ReadAllText(candidato);
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
