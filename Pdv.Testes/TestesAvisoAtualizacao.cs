using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A CAIXA QUE PERGUNTA SE PODE ATUALIZAR CABE NA TELA E NA CABEÇA.
///
/// O QUE ACONTECEU (09/09/2026). O dono leu a caixa da 0.6.5 e escreveu:
/// "continuamos com UX horrível de mensagem de atualização. Sem formatação, muito
/// texto, confuso... acho que nem precisa disso tudo, ou algo mais direto e
/// didático".
///
/// O que ele viu, contado:
///
///   Título:  Atualização disponível
///   Linha 1: Versão 0.6.5 disponível (atual: 0.5.6).      ← repete o título
///   Linha 2: [quatro frases de nota de versão, 380 caracteres]
///   Linha 3: O caixa reinicia. Vendas e caixa aberto ficam guardados.
///   Linha 4: O turno continua aberto. Entre com o PIN de novo.   ← repete a 3
///
/// Quem lê está de pé, com fila na frente, e tem UMA pergunta: atualizo agora ou
/// não? O título faz a pergunta, uma linha diz o que muda, outra diz o que ele vai
/// viver.
///
/// É o mesmo teto que TestesAvisoParadas guarda para o aviso de venda parada, pela
/// mesma queixa, de um dia antes.
/// </summary>
public static class TestesAvisoAtualizacao
{
    // A nota real da 0.6.5, do jeito que foi publicada: o caso que gerou a queixa.
    private const string NotaLonga =
        "Preco novo passa a valer na comanda ja aberta: trocar o preco no painel e "
        + "sincronizar agora corrige a linha que ja estava na conta. Preco que sobe avisa "
        + "na tela antes de fechar. Traz junto o que estava parado desde a 0.5.6: estorno "
        + "com 2FA, combos com sabores, promocao com senha do gerente, e o agente de "
        + "contingencia fiscal dentro do instalador.";

    public static void Rodar(Action<bool, string> checar)
    {
        static Atualizacao.EstadoDoCaixa Caixa(bool aberto, int porSubir = 0)
            => new(ItensNaComanda: 0, CaixaAberto: aberto, VendasPorSubir: porSubir);

        static Atualizacao.LeituraManifesto Oferta(string versao, string? notas, bool obrigatoria = false)
            => new(new Atualizacao.Manifesto(versao,
                       "https://pdv.mmtech.software/download/AtualizarPdv-" + versao + ".exe",
                       notas, obrigatoria), null);

        // ── O CASO DO DONO ──────────────────────────────────────────────────
        var v = Atualizacao.Decidir(Caixa(aberto: true), "0.5.6.0", Oferta("0.6.5", NotaLonga));
        var linhas = v.Mensagem.Split('\n');

        checar(v.Situacao == Atualizacao.Situacao.Disponivel, "a atualização é oferecida");
        checar(linhas.Length <= 3, $"a caixa cabe em 3 linhas (viu {linhas.Length}):\n{v.Mensagem}");
        checar(!v.Mensagem.Contains("\n\n"), "sem linha em branco no meio, que dobra a altura à toa");

        // O título FAZ A PERGUNTA. Antes ele anunciava, e a primeira linha repetia.
        checar(v.Titulo.Contains("0.6.5"), $"o título diz para onde vai ({v.Titulo})");
        checar(v.Titulo.EndsWith("?"), $"e é uma pergunta, que é o que o operador precisa responder ({v.Titulo})");
        checar(!v.Mensagem.Contains("disponível"),
            "o corpo não repete o que o título já disse");

        // ── A NOTA DE VERSÃO NÃO INVADE O CAIXA ─────────────────────────────
        checar(v.Mensagem.Length < 200, $"o texto inteiro é curto (viu {v.Mensagem.Length} caracteres)");
        checar(!v.Mensagem.Contains("estorno com 2FA"),
            "a lista de tudo que veio junto fica no painel, não na frente de caixa");
        checar(v.Mensagem.Contains("comanda"), "mas o que mudou de verdade aparece");

        // ── O QUE O OPERADOR VAI VIVER, UMA VEZ SÓ ──────────────────────────
        var ultima = linhas[^1];
        checar(ultima.Contains("reinicia"), $"a última linha diz que o caixa reinicia ({ultima})");
        checar(ultima.Contains("PIN"), "e que vai pedir o PIN, porque o turno está aberto");
        checar(ultima.Contains("Nada se perde"), "e tranquiliza sobre o que fica guardado");
        // Antes isto vinha em três linhas dizendo a mesma coisa.
        checar(CQuantas(v.Mensagem, "reinicia") == 1, "fala do reinício uma vez só");
        checar(!v.Mensagem.Contains("turno continua aberto"), "sem a linha que repetia o turno");

        // Caixa fechado não ouve falar de PIN: não é o que ele vai viver.
        var fechado = Atualizacao.Decidir(Caixa(aberto: false), "0.5.6.0", Oferta("0.6.5", NotaLonga));
        checar(!fechado.Mensagem.Contains("PIN"), "com o turno fechado, nada de PIN no texto");
        checar(fechado.Mensagem.Split('\n').Length <= 2, "e aí cabe em 2 linhas");

        // ── OBRIGATÓRIA MUDA A CONVERSA, NÃO O TAMANHO ──────────────────────
        var obr = Atualizacao.Decidir(Caixa(aberto: true), "0.5.6.0", Oferta("0.6.5", NotaLonga, obrigatoria: true));
        checar(obr.Mensagem.Split('\n').Length <= 3, "obrigatória também cabe em 3 linhas");
        checar(obr.TextoNao == "Não posso agora",
            "e o botão de recusar vira uma frase que se leva ao gerente");

        // ── SEM NOTA NENHUMA ────────────────────────────────────────────────
        var semNota = Atualizacao.Decidir(Caixa(aberto: true), "0.5.6.0", Oferta("0.6.5", null));
        checar(semNota.Mensagem.Split('\n').Length == 1,
            "sem nota, sobra uma linha só, e não uma linha vazia");
        checar(!semNota.Mensagem.StartsWith("\n"), "e o texto não começa com espaço em branco");

        // ── O RESUMO DA NOTA ────────────────────────────────────────────────
        var r = Atualizacao.ResumoDasNotas(NotaLonga)!;
        checar(r.Length <= 93, $"o resumo respeita o teto ({r.Length})");
        checar(!r.Contains('\n'), "o resumo é uma linha");
        checar(Atualizacao.ResumoDasNotas(null) is null, "sem nota, sem resumo");
        checar(Atualizacao.ResumoDasNotas("   ") is null, "nota só de espaço não vira linha vazia");

        // Frase curta passa inteira, sem reticências.
        var curta = Atualizacao.ResumoDasNotas("Corrige o preco na comanda aberta.")!;
        checar(curta == "Corrige o preco na comanda aberta.", $"nota curta passa inteira ({curta})");
        checar(!curta.EndsWith("..."), "e não ganha reticências à toa");

        // ⚠️ Número de versão não é fim de frase. "0.6.5." com ponto colado partiria
        // a frase no meio e a tela mostraria "Vai para a 0." de mensagem.
        var comVersao = Atualizacao.ResumoDasNotas("Vai para a 0.6.5 e corrige o preco.")!;
        checar(comVersao.StartsWith("Vai para a 0.6.5"), $"o ponto do número não corta a frase ({comVersao})");

        // Frase única muito longa é cortada em espaço, não no meio de uma palavra.
        var semPonto = Atualizacao.ResumoDasNotas(new string('a', 40) + " " + new string('b', 80))!;
        checar(semPonto.EndsWith("..."), "frase longa demais avisa que foi cortada");
        checar(!semPonto.Contains("bbbb"), "e o corte cai no espaço, não no meio da palavra");

        // ── NADA DE TRAVESSÃO ───────────────────────────────────────────────
        checar(!v.Mensagem.Contains('—') && !v.Titulo.Contains('—'), "sem travessão na caixa");
    }

    private static int CQuantas(string texto, string agulha)
    {
        var n = 0;
        for (var i = texto.IndexOf(agulha, StringComparison.Ordinal); i >= 0;
             i = texto.IndexOf(agulha, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }
}
