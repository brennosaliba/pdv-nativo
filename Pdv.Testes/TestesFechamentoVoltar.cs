using System.Text.RegularExpressions;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// "VOLTAR" NO FECHAMENTO E O PIX DITO COM TODAS AS LETRAS (11/09/2026, noite, pedidos do
/// dono na Savassi: "digitei o valor do PIX errado e tive que cancelar e reiniciar porque
/// não tem botão voltar" e o fechamento que acusou "falta" de R$ 112,99 em PIX porque o
/// cupom da maquininha não traz o PIX do QR do banco).
///
/// O diálogo é WPF (não roda na bateria); o que se prova aqui é o texto das perguntas (puro)
/// e, pelo fonte, que cada pergunta do fechamento e do POS tem Voltar e que Voltar recua uma
/// pergunta em vez de cancelar tudo.
/// </summary>
public static class TestesFechamentoVoltar
{
    public static void Rodar(Action<bool, string> checar)
    {
        ConferenciaForma F(string forma, long tef) => new(forma, new Dinheiro(1000), new Dinheiro(tef), true);

        var pixSemTef = Pdv.Telas.Venda.PerguntaDoFechamento(F("pix", 0));
        var pixComTef = Pdv.Telas.Venda.PerguntaDoFechamento(F("pix", 500));
        checar(pixSemTef.Contains("QR do banco") && pixSemTef.Contains("maquininha avulsa"),
            "a pergunta do PIX manda somar maquininha avulsa e QR do banco (o cupom da POS não traz o QR)");
        checar(pixComTef.Contains("QR do banco") && pixComTef.Contains("já entrou sozinho"),
            "com parte pelo TEF, a pergunta do PIX diz que o PIX do caixa já entrou e pede só o de fora");
        checar(Pdv.Telas.Venda.PerguntaDoFechamento(F("dinheiro", 0)).Contains("fundo de troco"), "dinheiro: conta a gaveta inteira");
        checar(Pdv.Telas.Venda.PerguntaDoFechamento(F("credito", 0)).Contains("Crédito") && Pdv.Telas.Venda.PerguntaDoFechamento(F("credito", 100)).Contains("outra maquininha"),
            "cartão: 'no fechamento da maquininha' sem TEF; 'na outra maquininha' com parte pelo TEF");
        foreach (var t in new[] { pixSemTef, pixComTef })
            checar(!t.Contains('—') && !t.Contains('–') && t.Length <= 140, "texto curto e sem travessão");

        var cs = Fonte(Path.Combine("Telas", "Venda.xaml.cs")) ?? "";
        var pedir = Fonte(Path.Combine("Telas", "PedirValor.cs")) ?? "";
        checar(pedir.Contains("public static Resposta MostrarComVoltar(") && pedir.Contains("Botao(\"Voltar\", false)"),
            "PedirValor tem a versão com o terceiro botão, Voltar");
        var laco = Trecho(cs, "var perguntas = plano.Where(p => p.Conta).ToList();", "// A maquininha avulsa (POS)");
        checar(laco.Contains("if (r.Voltou) { i--; continue; }") && laco.Contains("PedirValor.MostrarComVoltar(dono, \"Fechamento de caixa\"")
               && laco.Contains("i == 0 ? new PedirValor.Resposta(PedirValor.Mostrar("),
            "no fechamento, Voltar recua uma pergunta; a primeira pergunta não tem Voltar (não há para onde)");
        var pos = Trecho(cs, "internal static bool PerguntarMaquininhaAvulsa(", "private static string Rotulo(");
        checar(pos.Contains("PedirValor.MostrarComVoltar(dono, titulo, texto)") && pos.Contains("if (i == 0) { voltouAoInicio = true; break; }")
               && pos.Contains("foreach (var f in FormasDaMaquininha) contagem.Remove(f);"),
            "na pergunta do POS, Voltar recua um campo e, no primeiro, volta ao 'teve venda no POS?' sem deixar valor pela metade");
        checar(pos.Contains("QR do banco"), "o PIX do POS também fala do QR do banco");
    }

    private static string Trecho(string todo, string de, string ate)
    {
        var i = todo.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var f = todo.IndexOf(ate, i + de.Length, StringComparison.Ordinal);
        return f < 0 ? todo[i..] : todo[i..f];
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
