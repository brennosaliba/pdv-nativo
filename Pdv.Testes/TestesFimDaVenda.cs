using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A TELA DE VENDA CONCLUÍDA SAI DA FRENTE, MENOS QUANDO NÃO PODE.
///
/// O dono, 09/09/2026: "apos venda concluida acho q pode voltar pro dash principal
/// ao inves de colocar botao de nova venda". No caso comum ele está certo: é um
/// toque por venda que não decide nada.
///
/// SE ESTES TESTES QUEBRAREM, é isto que volta a acontecer na loja: a tela some
/// enquanto o operador conta o troco, ou o aviso de recibo entalado desaparece
/// sozinho e o cliente vai embora sem comprovante.
/// </summary>
public static class TestesFimDaVenda
{
    public static void Rodar(Action<bool, string> checar)
    {
        // ── O CASO COMUM: cartão, imprimiu, sem troco ───────────────────────
        checar(FimDaVenda.VoltaSozinho(0, problemaNaImpressao: false),
            "venda de cartão que imprimiu volta sozinha");
        checar(FimDaVenda.PorQueEsperando(0, false) is null,
            "e não tem nada a dizer sobre esperar");
        checar(FimDaVenda.RotuloDoBotao(true) == "Continuar",
            "o botão continua existindo para quem tem pressa");

        // ── TROCO SEGURA ────────────────────────────────────────────────────
        // O valor a devolver esta NESSA tela. Ela sumir enquanto o operador conta a
        // nota e como o troco sumir da vista.
        checar(!FimDaVenda.VoltaSozinho(1550, problemaNaImpressao: false),
            "com troco a devolver, a tela espera");
        checar(FimDaVenda.PorQueEsperando(1550, false)!.Contains("troco"),
            "e diz que e o troco que segura");
        checar(FimDaVenda.RotuloDoBotao(false) == "Nova venda",
            "e o botão volta a ser o de sempre");

        // Um centavo de troco ainda é troco.
        checar(!FimDaVenda.VoltaSozinho(1, false), "um centavo de troco segura igual");

        // ── PAPEL QUE NÃO SAIU SEGURA ───────────────────────────────────────
        // Voltar sozinho aqui e engolir o problema: a proxima venda comeca e ninguem
        // soube que o cliente ficou sem comprovante.
        checar(!FimDaVenda.VoltaSozinho(0, problemaNaImpressao: true),
            "recibo que não saiu segura a tela");
        checar(FimDaVenda.PorQueEsperando(0, true)!.Contains("impressão"),
            "e diz que é a impressão");

        // ── OS DOIS JUNTOS ──────────────────────────────────────────────────
        checar(!FimDaVenda.VoltaSozinho(500, true), "troco e impressão juntos seguram");
        var ambos = FimDaVenda.PorQueEsperando(500, true)!;
        checar(ambos.Contains("troco") && ambos.Contains("impressão"),
            $"e a frase cita os dois, nao so o primeiro ({ambos})");

        // ── TROCO NEGATIVO NÃO EXISTE, E NÃO PODE SEGURAR ───────────────────
        // Arredondamento ou bug de conta nao pode travar a tela de todo mundo.
        checar(FimDaVenda.VoltaSozinho(-1, false), "troco negativo não segura a tela");

        // ── O TEMPO ─────────────────────────────────────────────────────────
        checar(FimDaVenda.SegundosAteVoltar is >= 2 and <= 5,
            $"a espera dá para ler sem segurar a fila ({FimDaVenda.SegundosAteVoltar}s)");

        // ── SEM TRAVESSÃO ───────────────────────────────────────────────────
        foreach (var t in new[] { FimDaVenda.PorQueEsperando(100, false)!,
                                  FimDaVenda.PorQueEsperando(0, true)!, ambos })
            checar(!t.Contains('—'), "nenhuma frase usa travessão");
    }
}
