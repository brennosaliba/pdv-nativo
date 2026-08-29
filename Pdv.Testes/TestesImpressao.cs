using System.Globalization;
using Papel = Pdv.Impressao.Papel;

namespace Pdv.Testes;

/// <summary>
/// Testes da LARGURA DO PAPEL (config <c>papel_mm</c>: 58, 80 ou 100 mm).
///
/// O que quebra aqui não custa dinheiro, custa o cupom: colunas que não derivam da bobina
/// escolhida saem CORTADAS no papel. A linha do item ("qtd un x unit ......... total") é
/// impressa sem quebra automática de propósito — ela é montada na medida exata da bobina —
/// então o que passar da área imprimível não quebra: some. E some justamente no fim da
/// linha, onde está o valor.
///
/// <c>Impressao</c> vive no projeto WPF; a suíte passou a referenciá-lo (ver o csproj) para
/// poder medir isto de verdade. Procurar texto no fonte, que era o único recurso antes,
/// passa com o código errado desde que a linha esteja escrita.
/// </summary>
public static class TestesImpressao
{
    public static async Task RodarAsync(Action<bool, string> checar)
    {
        // PapelMm e Auditar são estado global do processo: devolver como estava para não
        // contaminar o que roda depois.
        var papelAntes = Pdv.Impressao.PapelMm;
        var auditarAntes = Pdv.Impressao.Auditar;
        try
        {
            Geometria(checar);
            Configuracao(checar);
            await NoPapelAsync(checar);
        }
        finally
        {
            Pdv.Impressao.PapelMm = papelAntes;
            Pdv.Impressao.Auditar = auditarAntes;
        }
    }

    /// <summary>Cada bobina suportada tem que fechar a conta: colunas que cabem e papel que não sobra.</summary>
    private static void Geometria(Action<bool, string> checar)
    {
        checar(Pdv.Impressao.BobinasSuportadas.SequenceEqual(new[] { 58.0, 80.0, 100.0 }),
            "as bobinas oferecidas são 58, 80 e 100 mm");

        foreach (var mm in Pdv.Impressao.BobinasSuportadas)
        {
            var p = Papel.De(Texto(mm));

            checar(Math.Abs(p.BobinaMm - mm) < 0.001,
                $"{mm:0} mm: a bobina escolhida é a que vale (veio {p.BobinaMm:0.#})");

            // A cabeça térmica não alcança a bobina inteira, mas também não pode sobrar
            // meio papel: os dois extremos são erro de tabela.
            checar(p.UtilMm < p.BobinaMm && p.UtilMm > p.BobinaMm * 0.7,
                $"{mm:0} mm: sobra margem mecânica e não sobra papel demais ({p.UtilMm:0.#} mm úteis)");

            // ⭐ O TESTE QUE IMPORTA: a linha CHEIA, medida na fonte que vai para o papel,
            // cabe na área imprimível. É o que garante que nada sai cortado.
            var linha = Pdv.Impressao.LarguraDoTextoMm(p.Colunas);
            checar(linha <= p.UtilMm,
                $"{mm:0} mm: {p.Colunas} colunas medem {linha:0.0} mm e cabem nos {p.UtilMm:0.#} mm úteis");

            // E o contrário também é defeito: colunas de menos desperdiçam bobina e
            // espremem a descrição do produto sem necessidade.
            checar(linha >= p.UtilMm * 0.90,
                $"{mm:0} mm: as {p.Colunas} colunas usam {linha / p.UtilMm:P0} da área útil (não estão sobrando)");

            // O QR do consumidor tem 40 mm de lado e é item obrigatório do DANFE; QR
            // aparado não lê, e aí o cupom perde a única forma de o cliente validar a nota.
            checar(p.UtilMm >= 40.0,
                $"{mm:0} mm: o QR de 40 mm cabe inteiro na área útil");
        }

        var p58 = Papel.De("58");
        var p80 = Papel.De("80");
        var p100 = Papel.De("100");

        checar(p58.Colunas < p80.Colunas && p80.Colunas < p100.Colunas,
            $"bobina mais larga, mais colunas ({p58.Colunas} < {p80.Colunas} < {p100.Colunas})");
        checar(p58.Colunas == 32 && p80.Colunas == 48,
            $"os clássicos da térmica saem certos: 32 colunas em 58 mm e 48 em 80 mm (vieram {p58.Colunas} e {p80.Colunas})");

        // POR QUE a largura precisou virar configuração: as 48 colunas que estavam fixas no
        // código não cabem em 58 mm. Se este teste um dia passar a dizer que cabem, é a
        // medição que quebrou — e aí os outros também não valem nada.
        checar(Pdv.Impressao.LarguraDoTextoMm(48) > p58.UtilMm,
            $"48 colunas ({Pdv.Impressao.LarguraDoTextoMm(48):0.0} mm) NÃO cabem nos {p58.UtilMm:0.#} mm úteis de 58 mm — era isto que saía cortado");

        // Quem não mexer em nada não pode ver diferença nenhuma no papel.
        checar(p80 == Pdv.Impressao.PapelPadrao,
            "80 mm continua sendo 72 mm úteis e 48 colunas, exatamente como estava fixo no código");
    }

    /// <summary>O que chega de <c>config['papel_mm']</c> é texto solto do banco — e pode ser lixo.</summary>
    private static void Configuracao(Action<bool, string> checar)
    {
        var p58 = Papel.De("58");

        // Ausente é o caso NORMAL (ninguém escolheu ainda); o resto é digitação torta,
        // sobra de configuração antiga ou bobina que a impressão não sabe montar. Todos
        // caem no padrão: 80 mm é o que a loja já imprime hoje, então ninguém regride.
        foreach (var ruim in new string?[] { null, "", "   ", "abc", "58mm", "oitenta", "0", "-58", "75", "9999" })
            checar(Papel.De(ruim) == Pdv.Impressao.PapelPadrao,
                $"papel_mm = {Mostra(ruim)} cai no padrão de 80 mm");

        // O valor pode ter sido gravado à mão (suporte, script de instalação), e num
        // teclado brasileiro sai com vírgula.
        checar(Papel.De(" 58 ") == p58 && Papel.De("58,0") == p58 && Papel.De("58.0") == p58,
            "'58', ' 58 ', '58,0' e '58.0' são a mesma bobina");

        var avisos = new List<string>();
        Pdv.Impressao.Auditar = avisos.Add;

        Pdv.Impressao.PapelMm = "58";
        checar(Pdv.Impressao.PapelAtual == p58, "a impressão passa a usar a bobina configurada");
        checar(avisos.Count == 0, "bobina conhecida não gera aviso");

        Pdv.Impressao.PapelMm = null;
        checar(Pdv.Impressao.PapelAtual == Pdv.Impressao.PapelPadrao && avisos.Count == 0,
            "sem configuração nenhuma: 80 mm e silêncio (é o estado de quem nunca escolheu)");

        // Bobina que não existe é a pior situação possível: a loja acha que configurou e o
        // papel continua saindo no padrão. Não pode sair cortado E não pode sumir do rastro.
        Pdv.Impressao.PapelMm = "76";
        var caiu = Pdv.Impressao.PapelAtual;
        _ = Pdv.Impressao.PapelAtual;   // a impressão lê isto uma vez por cupom
        _ = Pdv.Impressao.PapelAtual;
        checar(caiu == Pdv.Impressao.PapelPadrao,
            "bobina desconhecida imprime no padrão em vez de arriscar sair cortada");
        checar(avisos.Count == 1,
            $"e vai para a auditoria UMA vez, não a cada cupom (vieram {avisos.Count} avisos)");
        checar(avisos.Count == 1 && avisos[0].Contains("76", StringComparison.Ordinal),
            "o aviso diz qual valor foi recusado");

        Pdv.Impressao.PapelMm = "77";
        _ = Pdv.Impressao.PapelAtual;
        checar(avisos.Count == 2, "trocar por outro valor errado avisa de novo (não é 'avisei uma vez e pronto')");

        Pdv.Impressao.Auditar = null;
        Pdv.Impressao.PapelMm = null;
    }

    /// <summary>
    /// A prova no papel: o MESMO cupom desenhado em cada bobina. Sem isto, a geometria
    /// poderia estar perfeita na struct e o desenho continuar saindo em 80 mm — que é o
    /// defeito de verdade, o que chega na mão do cliente.
    /// </summary>
    private static async Task NoPapelAsync(Action<bool, string> checar)
    {
        var alturas = new Dictionary<double, int>();

        foreach (var mm in Pdv.Impressao.BobinasSuportadas)
        {
            Pdv.Impressao.PapelMm = Texto(mm);
            var png = Path.Combine(Path.GetTempPath(), $"pdv-papel-{mm:0}-{Guid.NewGuid():N}.png");
            try
            {
                var erro = await Pdv.Impressao.PreVisualizarAsync(
                    Servicos.CupomDeExemplo("LOJA DE TESTE", "62177839000238", 3), png);
                checar(erro is null, $"{mm:0} mm: o cupom de exemplo desenhou ({erro ?? "ok"})");
                if (erro is not null) continue;

                var (largura, altura) = TamanhoPng(png);
                alturas[mm] = altura;

                // A prévia desenha em 4x sobre 96 dpi: mm → DIP → pixel. Um pixel de folga
                // porque o desenho é arredondado para cima.
                var esperado = (int)Math.Ceiling(mm * (96.0 / 25.4) * 4.0);
                checar(Math.Abs(largura - esperado) <= 1,
                    $"{mm:0} mm: o cupom saiu com {largura} px de largura e a bobina são {esperado} px");
            }
            finally { try { File.Delete(png); } catch { /* temp que não apaga não é falha de teste */ } }
        }

        // Bobina estreita não é o mesmo cupom menor: é o mesmo conteúdo em mais linhas
        // (endereço, URL do portal e descrição de produto quebram). Se a altura não muda,
        // o texto não reflui — está sendo cortado.
        checar(alturas.TryGetValue(58.0, out var a58) && alturas.TryGetValue(80.0, out var a80) && a58 > a80,
            $"em 58 mm o mesmo cupom fica mais COMPRIDO ({(alturas.TryGetValue(58.0, out var x) ? x : -1)} px contra " +
            $"{(alturas.TryGetValue(80.0, out var y) ? y : -1)} px) — o texto reflui em vez de sumir");
    }

    /// <summary>Largura e altura lidas do cabeçalho IHDR do PNG — sem decodificar a imagem.</summary>
    private static (int Largura, int Altura) TamanhoPng(string caminho)
    {
        var b = File.ReadAllBytes(caminho);
        if (b.Length < 24) return (-1, -1);
        static int Be(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
        return (Be(b, 16), Be(b, 20));
    }

    private static string Texto(double mm) => mm.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Mostra(string? v) => v is null ? "(nulo)" : $"'{v}'";
}
