using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
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
///
/// Segunda resposta do dono no mesmo dia: "sim pix separado TEF POS". O PIX passa a sair
/// sempre como "PIX TEF" e "PIX POS", com a mesma regra. Com duas linhas fixas a mais, a
/// janela do relatório ganhou teto de altura e rolagem (Dialogo.Relatorio), provada aqui
/// na conta e numa janela de verdade a 1024x768.
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
        PixTefEPos(checar);
        OrdemELargura(checar);
        NoBancoDeVerdade(checar);
        Telas(checar);
        PerguntasDoPix(checar);
        AlturaDoRelatorio(checar);
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

        // 13/09/2026: o PIX também se parte ("sim pix separado TEF POS"). Refeição não:
        // segue numa linha só, com a regra de sempre.
        var pix = new List<LinhaFechamento>
        {
            new("pix", R(40), R(45), true, R(30), false),
            new("voucher", R(12), R(12), true, Dinheiro.Zero, true),
        };
        var rp = ResumoFechamento.Linhas(pix, false);
        checar(Parte(rp, "PIX TEF") is { Situacao: "sem_conferencia", SemConferencia: true, Origem: "máquina" } pt && pt.Declarado == R(30)
               && Parte(rp, "PIX POS") is { Situacao: "falta", SemConferencia: false, Origem: "contou" } pp
               && pp.Declarado == R(10) && pp.Esperado == R(15) && pp.Fim == "FALTA " + R(5).Formatado()
               && Parte(rp, "PIX") is null,
            "PIX com maquininha muda e falta fora do caixa: PIX TEF sem conferência e PIX POS FALTA R$ 5,00, cada um no seu lugar");
        checar(Parte(rp, "Refeição") is { Situacao: "confere" }
               && !rp.Any(x => x.Rotulo.StartsWith("Refeição ", StringComparison.Ordinal)),
            "Refeição segue numa linha só (sem \"Refeição TEF\")");
    }

    // ── PIX partido em TEF e POS ("sim pix separado TEF POS") ──────────────
    private static void PixTefEPos(Action<bool, string> checar)
    {
        // R$ 30,00 na maquininha do caixa e R$ 45,00 fora dele (avulsa + QR do banco),
        // e o operador contou os R$ 45,00 de fora.
        var linhas = new List<LinhaFechamento>
        {
            new("dinheiro", R(10), R(10)),
            new("pix", R(75), R(75), true, R(30), true),
        };
        var r = ResumoFechamento.Linhas(linhas);
        var tef = Parte(r, "PIX TEF");
        var pos = Parte(r, "PIX POS");
        checar(tef is { Origem: "máquina", Situacao: "confere", SemConferencia: false }
               && tef.Declarado == R(30) && tef.Esperado == R(30) && tef.Diferenca == Dinheiro.Zero,
            $"PIX TEF: apurado = declarado = a parte da maquininha do caixa, R$ 30,00, sem diferença (veio {tef?.Texto})");
        checar(pos is { Origem: "contou", Situacao: "confere" } && pos.Declarado == R(45) && pos.Esperado == R(45),
            $"PIX POS: apurado e declarado sem a parte do TEF, R$ 45,00 (veio {pos?.Texto})");
        checar(Parte(r, "PIX") is null
               && !ResumoFechamento.Texto(linhas).Split('\n').Any(x => x.StartsWith("PIX ", StringComparison.Ordinal)
                                                                    && !x.StartsWith("PIX TEF ", StringComparison.Ordinal)
                                                                    && !x.StartsWith("PIX POS ", StringComparison.Ordinal)),
            "não sobra linha de \"PIX\" inteiro junto das duas partes");

        // Sobra (QR do banco que o sistema não viu) e falta: só na parte POS.
        var sobra = ResumoFechamento.Linhas(new List<LinhaFechamento> { new("pix", R(80), R(75), true, R(30), true) });
        checar(Parte(sobra, "PIX TEF") is { Situacao: "confere" } st && st.Diferenca == Dinheiro.Zero
               && Parte(sobra, "PIX POS") is { Situacao: "sobra" } sp && sp.Diferenca == R(5) && sp.Fim == "SOBRA " + R(5).Formatado(),
            "sobra no PIX cai no PIX POS, nunca no PIX TEF");

        // Só pela maquininha do caixa: PIX POS aparece zerado.
        var soTef = ResumoFechamento.Linhas(new List<LinhaFechamento> { new("pix", R(30), R(30), false, R(30), true) });
        checar(Parte(soTef, "PIX TEF") is { Situacao: "confere" } a && a.Declarado == R(30)
               && Parte(soTef, "PIX POS") is { Situacao: "confere", SemConferencia: false } b
               && b.Declarado == Dinheiro.Zero && b.Esperado == Dinheiro.Zero,
            "só PIX na maquininha do caixa: PIX POS sai com R$ 0,00 e confere");

        // Turno sem PIX nenhum: as duas linhas aparecem com R$ 0,00.
        var semPix = new List<LinhaFechamento> { new("dinheiro", R(10), R(10)) };
        var texto = ResumoFechamento.Texto(semPix);
        var lt = Linha(texto, "PIX TEF");
        var lp = Linha(texto, "PIX POS");
        checar(lt.Contains(Dinheiro.Zero.Formatado()) && lt.EndsWith("confere")
               && lp.Contains(Dinheiro.Zero.Formatado()) && lp.EndsWith("confere"),
            $"turno sem PIX: \"PIX TEF ... R$ 0,00 ... confere\" e \"PIX POS ... R$ 0,00 ... confere\" (veio \"{lt}\" / \"{lp}\")");

        // Fechamento antigo, linha sem a parte do TEF: nada de separação inventada.
        var antigo = ResumoFechamento.Linhas(new List<LinhaFechamento> { new("pix", R(40), R(45)) });
        checar(Parte(antigo, "PIX TEF") is { Situacao: "confere" } e && e.Declarado == Dinheiro.Zero
               && Parte(antigo, "PIX POS") is { Situacao: "falta" } f && f.Declarado == R(40) && f.Esperado == R(45),
            "linha sem a parte do TEF: PIX TEF R$ 0,00 e a linha inteira, com a falta, no PIX POS (igual ao crédito)");

        // Maquininha muda: a mesma regra do cartão.
        var mudo = new List<LinhaFechamento> { new("pix", R(75), R(75), true, R(30), false) };
        var rm = ResumoFechamento.Linhas(mudo, tefDisponivel: false);
        checar(Parte(rm, "PIX TEF") is { Situacao: "sem_conferencia", SemConferencia: true } g && g.Fim == "sem conferência"
               && Parte(rm, "PIX POS") is { Situacao: "confere", SemConferencia: false },
            "maquininha muda: PIX TEF sem conferência e o PIX POS contado confere");
        checar(ResumoFechamento.SemConferencia(mudo, false).SequenceEqual(new[] { "PIX TEF" })
               && Parte(ResumoFechamento.Linhas(mudo), "PIX TEF") is { SemConferencia: true },
            "a tela nomeia \"PIX TEF\" sem conferência, mesmo quando o chamador não passa o TEF (caixa esquecido)");
        var ninguem = new List<LinhaFechamento> { new("pix", R(75), R(75), false, R(30), false) };
        checar(Parte(ResumoFechamento.Linhas(ninguem, true), "PIX TEF") is { Situacao: "confere" }
               && Parte(ResumoFechamento.Linhas(ninguem, true), "PIX POS") is { Situacao: "sem_conferencia" } n && n.Esperado == R(45),
            "TEF respondeu e ninguém contou o PIX de fora: só o PIX POS fica sem conferência");

        // Totais: a partição não muda a diferença total nem a situação da forma.
        var misto = new List<LinhaFechamento>
        {
            new("dinheiro", R(98), R(100)),
            new("credito", R(3122.46m), R(3127.46m), true, R(3107.46m), true),
            new("pix", R(72), R(75), true, R(30), true),
            new("voucher", R(12), R(12)),
        };
        var totalNucleo = misto.Sum(l => l.DiferencaConferida.Abs.Centavos);
        var totalResumo = ResumoFechamento.Linhas(misto).Sum(x => x.Diferenca.Abs.Centavos);
        checar(totalNucleo == totalResumo && totalResumo == 1000,
            $"com PIX partido, a diferença total do resumo continua a do Núcleo (resumo {totalResumo}, núcleo {totalNucleo})");
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
        var esperada = new[] { "Dinheiro", "Crédito TEF", "Crédito POS", "Débito TEF", "Débito POS", "PIX TEF", "PIX POS", "Refeição", "outros" };
        checar(rotulos.SequenceEqual(esperada),
            $"a ordem é fixa, não a do banco (veio: {string.Join(", ", rotulos)})");

        var vazio = ResumoFechamento.Linhas(new List<LinhaFechamento>());
        checar(vazio.Select(x => x.Rotulo).SequenceEqual(new[] { "Crédito TEF", "Crédito POS", "Débito TEF", "Débito POS", "PIX TEF", "PIX POS" })
               && vazio.All(x => x.Declarado == Dinheiro.Zero && x.Situacao == "confere"),
            "turno sem cartão nem PIX: as seis linhas de crédito, débito e PIX saem com R$ 0,00");

        var texto = ResumoFechamento.Texto(embaralhado);
        var saida = texto.Split('\n');
        checar(!texto.Contains('—') && !texto.Contains('–'), "nenhuma linha do resumo tem travessão");
        var coluna = saida.Select(x => x.IndexOf("esperado", StringComparison.Ordinal)).Distinct().ToList();
        checar(coluna.Count == 1 && coluna[0] > 0,
            $"a palavra \"esperado\" cai na mesma coluna em todas as linhas (colunas: {string.Join(",", coluna)})");

        var grande = new List<LinhaFechamento>
        {
            new("credito", R(102_626.50m + 99_999.99m), R(205_253m + 99_999.99m), true, R(99_999.99m), false),
            new("pix", R(102_626.50m + 99_999.99m), R(205_253m + 99_999.99m), true, R(99_999.99m), false),
            new("outros", R(0), R(102_626.50m), true, Dinheiro.Zero, false),
        };
        var saidaGrande = ResumoFechamento.Texto(grande, false).Split('\n');
        var maior = saidaGrande.Max(x => x.Length);
        checar(maior <= Colunas,
            $"a linha mais larga do resumo, PIX TEF e PIX POS inclusive, cabe na tela do relatório ({maior} de {Colunas} colunas)");
        // Valor de seis dígitos já passa das 11 colunas do valor e empurra o "esperado" em
        // qualquer linha; o que se prova é que o PIX sai no MESMO formato do crédito.
        int Coluna(string rotulo) => Linha(string.Join("\n", saidaGrande), rotulo).IndexOf("esperado", StringComparison.Ordinal);
        checar(Coluna("PIX TEF") > 0 && Coluna("PIX TEF") == Coluna("Crédito TEF") && Coluna("PIX POS") == Coluna("Crédito POS"),
            $"e o PIX TEF e o PIX POS saem no mesmo formato do Crédito TEF e POS com o mesmo valor (colunas {Coluna("PIX TEF")}/{Coluna("Crédito TEF")} e {Coluna("PIX POS")}/{Coluna("Crédito POS")})");
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

            // PIX: R$ 30,00 pela maquininha do caixa e R$ 45,00 fora dele (avulsa + QR do
            // banco), e o operador responde "PIX fora do caixa" com R$ 45,00.
            var s4 = Caixa.Abrir(cx, op, Dinheiro.Zero);
            Pagar(cx, s4, op, "pix", 3000, "AUT-PIX1");
            Pagar(cx, s4, op, "pix", 4500, null);
            var l4 = Caixa.Fechar(cx, s4,
                new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.Zero, ["pix"] = R(45) },
                op, new Dinheiro(200));
            var r4 = ResumoFechamento.Linhas(l4, true);
            checar(Parte(r4, "PIX TEF") is { Situacao: "confere" } t4 && t4.Declarado == R(30)
                   && Parte(r4, "PIX POS") is { Situacao: "confere" } p4 && p4.Declarado == R(45) && p4.Esperado == R(45)
                   && Parte(r4, "PIX") is null,
                $"no banco: PIX TEF R$ 30,00 e PIX POS R$ 45,00 (veio:\n{ResumoFechamento.Texto(l4)})");

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

    // ── a pergunta do PIX e o diálogo da maquininha usam as palavras do resumo ──
    private static void PerguntasDoPix(Action<bool, string> checar)
    {
        ConferenciaForma F(long tef) => new("pix", new Dinheiro(7500), new Dinheiro(tef), true);
        var semTef = Pdv.Telas.Venda.PerguntaDoFechamento(F(0));
        var comTef = Pdv.Telas.Venda.PerguntaDoFechamento(F(3000));
        checar(semTef == Pdv.Telas.Venda.PerguntaPixPos && semTef.Contains("PIX POS") && semTef.Contains("fora do caixa")
               && semTef.Contains("maquininha avulsa") && semTef.Contains("QR do banco") && !semTef.Contains("PIX TEF"),
            $"sem TEF, a pergunta é a linha PIX POS e continua pedindo avulsa + QR do banco (\"{semTef}\")");
        checar(comTef.Contains("PIX POS") && comTef.Contains("fora do caixa") && comTef.Contains("maquininha avulsa")
               && comTef.Contains("QR do banco") && comTef.Contains("PIX TEF já entrou sozinho"),
            $"com TEF, pergunta o PIX POS e diz que o PIX TEF já entrou (\"{comTef}\")");
        foreach (var t in new[] { semTef, comTef })
            checar(t.Length <= 110 && !t.Contains('—') && !t.Contains('–'),
                $"pergunta do PIX curta e sem travessão ({t.Length} letras)");

        var venda = Fonte(Path.Combine("Telas", "Venda.xaml.cs")) ?? "";
        var avulsa = Trecho(venda, "internal static bool PerguntarMaquininhaAvulsa(", "private static string Rotulo(");
        checar(avulsa.Contains("PerguntaPixPos + \" Zero se não teve.\"") && !avulsa.Contains("\"Quanto deu em PIX fora do caixa"),
            "a pergunta da maquininha avulsa usa a MESMA frase do PIX POS (sem cópia)");

        // "Cartão da maquininha": a lista do que o TEF liquidou sai com o rótulo do resumo.
        var fechar = Trecho(venda, "private async void FecharCaixa(object sender, RoutedEventArgs e)", "private static void MostrarResultado");
        checar(fechar.Contains("doTef.Select(p => $\"{ResumoFechamento.RotuloTef(p.Forma)} {p.PeloTef.Formatado()}\")")
               && ResumoFechamento.RotuloTef("pix") == "PIX TEF" && ResumoFechamento.RotuloTef("credito") == "Crédito TEF"
               && ResumoFechamento.RotuloTef("voucher") == "Refeição",
            "o diálogo da maquininha lista \"PIX TEF R$ x\", a mesma palavra do resumo");
    }

    // ── a janela do relatório não passa da tela ─────────────────────────────
    private static void AlturaDoRelatorio(Action<bool, string> checar)
    {
        // A conta pura.
        checar(Pdv.Telas.Dialogo.AlturaMaximaRelatorio(768) == 768 - Pdv.Telas.Dialogo.FolgaTelaRelatorio,
            $"a 768 px (tela da loja) o relatório para em {Pdv.Telas.Dialogo.AlturaMaximaRelatorio(768)} px");
        checar(Pdv.Telas.Dialogo.AlturaMaximaRelatorio(1080) == 1080 - Pdv.Telas.Dialogo.FolgaTelaRelatorio,
            "numa tela maior o teto acompanha a tela");
        checar(Pdv.Telas.Dialogo.AlturaMaximaRelatorio(200) == 200,
            "tela menor que o mínimo: o teto é a própria tela, nunca maior");
        checar(Pdv.Telas.Dialogo.AlturaMaximaRelatorio(0) == Pdv.Telas.Dialogo.AlturaMinimaRelatorio
               && Pdv.Telas.Dialogo.AlturaMaximaRelatorio(double.NaN) == Pdv.Telas.Dialogo.AlturaMinimaRelatorio,
            "altura que não mediu não vira janela de zero pixel");

        // A barra reservada no padding é a barra que o estilo desenha.
        var estilos = Fonte("Estilos.xaml") ?? "";
        var barra = Regex.Match(estilos, @"<Style TargetType=""ScrollBar"">.*?<Setter Property=""Width"" Value=""(\d+)""/>",
            RegexOptions.Singleline);
        checar(barra.Success && double.Parse(barra.Groups[1].Value) == Pdv.Telas.Dialogo.BarraRolagem,
            $"a barra de rolagem do relatório tem a largura do estilo (Estilos.xaml {(barra.Success ? barra.Groups[1].Value : "?")}, Dialogo {Pdv.Telas.Dialogo.BarraRolagem})");

        // A janela de verdade, sobre um caixa de 1024x768.
        var linhas = new List<LinhaFechamento>
        {
            new("dinheiro", R(98), R(100)),
            new("credito", R(3122.46m), R(3127.46m), true, R(3107.46m), false),
            new("debito", R(35), R(30), true, Dinheiro.Zero, true),
            new("pix", R(72), R(75), true, R(30), false),
            new("voucher", R(12), R(12)),
        };
        var corpoGrande = ResumoFechamento.Texto(linhas, false) + "\n\nDiferença total: R$ 15,00"
            + string.Concat(Enumerable.Range(1, 40).Select(i => $"\n\nParágrafo {i} do relatório, para passar da tela."));
        var justificativa = "Justificativa: " + string.Join(" ", Enumerable.Repeat("conferi a avulsa duas vezes", 40));
        var corpoPequeno = ResumoFechamento.Texto(new List<LinhaFechamento> { new("dinheiro", R(10), R(10)) });

        Medida? grande = null, pequeno = null;
        Exception? erro = null;
        try
        {
            HostWpf.Executar(() =>
            {
                var host = new Window
                {
                    Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                    ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0,
                };
                host.Show();
                try
                {
                    QuandoAbrir(host, d => grande = Medir(d));
                    Pdv.Telas.Dialogo.Relatorio(host, "Caixa fechado", corpoGrande, justificativa);
                    QuandoAbrir(host, d => pequeno = Medir(d));
                    Pdv.Telas.Dialogo.Relatorio(host, "Caixa fechado", corpoPequeno);
                }
                finally { host.Close(); }
            });
        }
        catch (Exception ex) { erro = ex; }
        checar(erro is null && grande is not null && pequeno is not null,
            "relatório: as duas janelas abriram e fecharam pelo botão Fechar (" + (erro?.Message ?? "ok") + ")");
        if (grande is not { } g || pequeno is not { } p) return;

        checar(g.Altura <= 768 && g.Altura <= g.Teto + 0.5 && g.Teto <= 768,
            $"relatório comprido a 1024x768: a janela para em {g.Altura:0} px (teto {g.Teto:0}), dentro dos 768 da tela");
        checar(g.Rolavel > 0 && g.BarraVisivel,
            $"o texto que não coube rola por dentro ({g.Rolavel:0} px para rolar)");
        checar(g.FundoDoFechar <= g.Altura + 0.5 && g.FecharForaDaRolagem,
            $"o botão Fechar fica inteiro à vista, fora da rolagem (fundo dele em {g.FundoDoFechar:0} de {g.Altura:0} px)");
        checar(g.RodapeNaRolagem,
            "a justificativa comprida rola junto com o texto, sem empurrar o Fechar");
        checar(Math.Abs(g.LarguraTexto - Pdv.Telas.Dialogo.LarguraUtilRelatorio) < 1,
            $"com a barra à vista o texto tem a largura que o Encaixar usou ({g.LarguraTexto:0} de {Pdv.Telas.Dialogo.LarguraUtilRelatorio:0} px): nenhuma coluna quebra");
        checar(p.Rolavel == 0 && p.Altura < p.Teto - 100 && p.FundoDoFechar <= p.Altura + 0.5,
            $"relatório curto continua do tamanho do texto, sem rolagem ({p.Altura:0} px)");
    }

    private sealed record Medida(double Altura, double Teto, double Rolavel, bool BarraVisivel,
        double FundoDoFechar, bool FecharForaDaRolagem, bool RodapeNaRolagem, double LarguraTexto);

    /// <summary>Mede o relatório aberto e fecha pelo botão Fechar (o mesmo toque do operador).</summary>
    private static Medida Medir(Window d)
    {
        d.UpdateLayout();
        var rolagem = Descendentes<ScrollViewer>(d).First();
        var fechar = Descendentes<Button>(d).First(b => b.Content as string == "Fechar");
        var texto = Descendentes<TextBlock>(rolagem).First(t => t.FontFamily.Source == "Consolas");
        var fundo = fechar.TransformToAncestor(d).Transform(new Point(0, fechar.ActualHeight)).Y;
        var m = new Medida(d.ActualHeight, d.MaxHeight, rolagem.ScrollableHeight,
            rolagem.ComputedVerticalScrollBarVisibility == Visibility.Visible, fundo,
            !Descendentes<Button>(rolagem).Any(),
            Descendentes<TextBlock>(rolagem).Any(t => t.Text.StartsWith("Justificativa:", StringComparison.Ordinal)),
            texto.ActualWidth);
        fechar.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        return m;
    }

    /// <summary>
    /// Espera o próximo diálogo modal abrir sobre o host e age nele (ShowDialog bloqueia quem
    /// chamou; o laço aninhado dele dispara o timer). Se a ação falhar, o diálogo é fechado
    /// mesmo assim, para a suíte nunca ficar presa numa janela aberta.
    /// </summary>
    private static void QuandoAbrir(Window host, Action<Window> acao)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        var tentativas = 0;
        timer.Tick += (_, _) =>
        {
            var d = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w != host && w.Owner == host && w.IsVisible);
            if (d is null)
            {
                if (++tentativas > 50) timer.Stop();
                return;
            }
            timer.Stop();
            try { acao(d); }
            finally { if (d.IsVisible) d.Close(); }
        };
        timer.Start();
    }

    private static IEnumerable<T> Descendentes<T>(DependencyObject raiz) where T : DependencyObject
    {
        foreach (var filho in LogicalTreeHelper.GetChildren(raiz))
        {
            if (filho is not DependencyObject d) continue;
            if (d is T t) yield return t;
            foreach (var neto in Descendentes<T>(d)) yield return neto;
        }
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
