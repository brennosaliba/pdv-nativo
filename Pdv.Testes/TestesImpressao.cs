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
            DestinoPorFinalidade(checar);
            ComandaNaBobinaCerta(checar);
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
    /// DESTINO POR FINALIDADE (29/08 — pedido do dono): a comanda do delivery pode sair
    /// na térmica da expedição e o cupom fiscal na do balcão, cada uma com a sua bobina.
    ///
    /// O primeiro teste é o que NÃO PODE QUEBRAR: quem nunca abriu a opção continua
    /// imprimindo a comanda onde já imprime — na impressora do CUPOM. Antes disto, sem
    /// impressora de comanda escolhida a comanda ia para a "padrão do Windows", que numa
    /// máquina de caixa costuma ser o Microsoft Print to PDF: papel que nunca sai.
    /// </summary>
    private static void DestinoPorFinalidade(Action<bool, string> checar)
    {
        var p58 = Papel.De("58");
        var p80 = Papel.De("80");

        // ⭐ REGRESSÃO: nada configurado = tudo na impressora do cupom.
        var nada = Pdv.Impressao.DestinoComanda("EPSON TM-T20", "80", null, null, null);
        checar(nada.Impressora == "EPSON TM-T20" && nada.Papel == p80,
            $"comanda SEM opção ligada sai na impressora e na bobina do cupom (veio {nada.Impressora ?? "(padrão)"})");
        checar(Pdv.Impressao.DestinoComanda(null, "58", null, null, null)
                   == new Pdv.Impressao.Destino(null, p58),
            "e num caixa que usa a padrão do Windows, a comanda continua indo para a padrão do Windows");

        // Ligada: impressora E bobina próprias. É o pedido inteiro em uma linha.
        var propria = Pdv.Impressao.DestinoComanda("EPSON TM-T20", "80", "1", "ELGIN I9 COZINHA", "58");
        checar(propria.Impressora == "ELGIN I9 COZINHA" && propria.Papel == p58,
            "com a opção ligada, a comanda vai para a impressora e a bobina dela");
        checar(Pdv.Impressao.DestinoCupom("EPSON TM-T20", "80").Papel == p80,
            "e o cupom continua em 80 mm — as duas larguras são independentes");
        checar(propria.Papel.Colunas == 32 && Pdv.Impressao.DestinoCupom("EPSON TM-T20", "80").Papel.Colunas == 48,
            $"cada finalidade fecha as colunas na SUA bobina ({propria.Papel.Colunas} contra 48)");

        // Bobina da comanda em branco herda a do cupom: é o que valia antes de a largura
        // separada existir, então ligar a impressora própria não muda a largura sozinho.
        checar(Pdv.Impressao.DestinoComanda(null, "58", "1", "ELGIN", null).Papel == p58
               && Pdv.Impressao.DestinoComanda(null, "58", "1", "ELGIN", "  ").Papel == p58,
            "bobina da comanda em branco herda a do cupom");

        // "" é escolha explícita de padrão do Windows (a 1ª opção do combo).
        checar(Pdv.Impressao.DestinoComanda("EPSON TM-T20", "80", "1", "", "58")
                   == new Pdv.Impressao.Destino(null, p58),
            "com a opção ligada e impressora em branco, a comanda vai para a padrão do Windows");

        // ── a caixinha: ligada, desligada e nunca respondida ────────────────
        checar(!Pdv.Impressao.ComandaSeparada(null, null) && !Pdv.Impressao.ComandaSeparada(null, ""),
            "sem opção gravada e sem impressora de comanda, a comanda NÃO é separada");
        checar(Pdv.Impressao.ComandaSeparada(null, "ELGIN I9"),
            "quem já tinha impressora de comanda antes da caixinha existir continua com ela (retrocompatível)");
        checar(!Pdv.Impressao.ComandaSeparada("0", "ELGIN I9"),
            "e DESLIGAR a caixinha vale mesmo com impressora gravada — é decisão do dono");
        checar(Pdv.Impressao.DestinoComanda("EPSON TM-T20", "80", "0", "ELGIN I9", "58")
                   == new Pdv.Impressao.Destino("EPSON TM-T20", p80),
            "desligada, a comanda volta inteira para a impressora e a bobina do cupom");
        checar(Pdv.Impressao.ComandaSeparada("1", null),
            "ligada sem impressora escolhida continua ligada (a bobina dela pode ser a diferença)");

        // Espaço em branco no banco (digitado à mão pelo suporte) é o mesmo que vazio.
        checar(Pdv.Impressao.DestinoComanda("  EPSON TM-T20  ", "80", null, "   ", null).Impressora == "EPSON TM-T20",
            "nome de impressora com espaço sobrando é o mesmo nome; impressora só de espaços é 'nenhuma'");
    }

    /// <summary>
    /// A COMANDA NÃO PODE SAIR CORTADA. As 40 colunas viviam fixas em
    /// <c>Kds.ComandaLinhas</c>; numa bobina de 58 mm (32 colunas) o fim da linha — onde
    /// está a quantidade do item — simplesmente não chega ao papel.
    /// </summary>
    private static void ComandaNaBobinaCerta(Action<bool, string> checar)
    {
        // POR QUE a largura da comanda precisou deixar de ser fixa. Se este teste um dia
        // disser que cabem, é a medição que quebrou.
        checar(Pdv.Impressao.LarguraDoTextoMm(Pdv.Nucleo.Kds.ColunasPadrao) > Papel.De("58").UtilMm,
            $"as {Pdv.Nucleo.Kds.ColunasPadrao} colunas fixas da comanda NÃO cabem em 58 mm — era isto que saía cortado");

        foreach (var mm in Pdv.Impressao.BobinasSuportadas)
        {
            var p = Papel.De(Texto(mm));
            var colunas = Pdv.Nucleo.Kds.ColunasComanda(p.Colunas);

            checar(Pdv.Impressao.LarguraDoTextoMm(colunas) <= p.UtilMm,
                $"{mm:0} mm: a comanda em {colunas} colunas cabe nos {p.UtilMm:0.#} mm úteis");

            // A comanda não estica em bobina larga: o layout foi desenhado para 40 colunas
            // e esticar só afastaria o item do quadradinho de conferência.
            checar(colunas <= Pdv.Nucleo.Kds.ColunasPadrao,
                $"{mm:0} mm: a comanda não passa das {Pdv.Nucleo.Kds.ColunasPadrao} colunas de sempre (veio {colunas})");

            // ⭐ E o texto de verdade tem que respeitar a largura — não adianta a conta
            // fechar e a montagem continuar escrevendo 40 caracteres numa linha de 32.
            var linhas = Pdv.Nucleo.Kds.ComandaLinhas(Pdv.Servicos.ComandaDeExemplo(), colunas);
            var maior = linhas.Max(l => Pdv.Nucleo.LinhaEscala.Limpa(l).Length);
            checar(maior <= colunas,
                $"{mm:0} mm: a linha mais longa da comanda tem {maior} caracteres e a bobina tem {colunas}");
            checar(linhas.Any(l => Pdv.Nucleo.LinhaEscala.Limpa(l).Contains("Donut Ninho")),
                $"{mm:0} mm: as escolhas do combo continuam na comanda (sem elas a cozinha não sabe o que produzir)");
        }

        // 80 mm continua exatamente como sempre foi: quem não mexeu não vê diferença.
        checar(Pdv.Nucleo.Kds.ColunasComanda(Papel.De("80").Colunas) == Pdv.Nucleo.Kds.ColunasPadrao,
            "em 80 mm a comanda continua com as 40 colunas de sempre");
        checar(Pdv.Nucleo.Kds.ComandaLinhas(Pdv.Servicos.ComandaDeExemplo())
                   .SequenceEqual(Pdv.Nucleo.Kds.ComandaLinhas(Pdv.Servicos.ComandaDeExemplo(), 48)),
            "e chamar sem escolher largura dá a MESMA comanda de 80 mm (nenhum chamador antigo mudou)");
        checar(Pdv.Nucleo.Kds.ColunasComanda(Papel.De("58").Colunas) == 32,
            "em 58 mm a comanda passa a usar as 32 colunas que cabem, em vez das 40 que não cabem");
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
