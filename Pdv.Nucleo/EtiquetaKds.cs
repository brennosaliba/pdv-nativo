namespace Pdv.Nucleo;

/// <summary>Em que papel a comanda da cozinha sai.</summary>
public enum FormatoComanda
{
    /// <summary>Texto de 40 colunas na bobina térmica (o que a loja sempre usou).</summary>
    Bobina,
    /// <summary>Etiqueta 100x150 mm desenhada, com QR para bipar o PRONTO.</summary>
    Etiqueta,
}

/// <summary>O tipo de cada linha do miolo da etiqueta.</summary>
public enum TipoLinhaEtiqueta
{
    /// <summary>Item simples: quadradinho na frente, porque ele é o que se confere.</summary>
    Item,
    /// <summary>
    /// Subitem do combo (sabor, complemento), COM quadradinho. O nome do combo NÃO sai
    /// (06/10, dono): a separação é por subitem e o nome do pai só gerava dúvida.
    /// </summary>
    Subitem,
    /// <summary>Observação do item, recuada e sem quadradinho.</summary>
    Observacao,
    /// <summary>
    /// EMBALAGEM (06/10, pedido iFood #6066: "Caixinha Extra" dentro do Combo Box 4un).
    /// Sai na lista, porque vai junto na sacola, mas SEM quadradinho e recuada: não é
    /// produção e não pode ser contada como sabor. Quem é embalagem decide a palavra
    /// configurável <see cref="EtiquetaKds.ChaveEmbalagem"/>.
    /// </summary>
    Embalagem,
}

/// <summary>Uma linha do miolo da etiqueta.</summary>
/// <param name="Qtd">"2×", ou vazio (observação, escolha sem número).</param>
public sealed record LinhaEtiqueta(TipoLinhaEtiqueta Tipo, string Qtd, string Texto)
{
    /// <summary>Leva o quadradinho de conferência?</summary>
    public bool Caixa => Tipo is TipoLinhaEtiqueta.Item or TipoLinhaEtiqueta.Subitem;

    /// <summary>0 = encostado na margem; 1 = recuado sob o item.</summary>
    public int Nivel => Tipo is TipoLinhaEtiqueta.Observacao or TipoLinhaEtiqueta.Embalagem ? 1 : 0;

    /// <summary>A linha como se lê, para teste e log.</summary>
    public string Lida => (Caixa ? "[ ] " : "") + (Qtd.Length == 0 ? Texto : Qtd + " " + Texto);
}

/// <summary>Tudo o que vai na etiqueta, já decidido. A tela só desenha.</summary>
/// <param name="Cliente">O nome do cliente (espaços normalizados, teto de <see cref="EtiquetaKds.ClienteMaxCaracteres"/>), ou null.</param>
/// <param name="Qr">Conteúdo EXATO do QR (prefixo + id).</param>
public sealed record Etiqueta(string Numero, string Origem, string? Cliente,
                              string? Agendado, string Chegou, bool Retirada,
                              IReadOnlyList<LinhaEtiqueta> Linhas, string Qr);

/// <summary>Como o nome do cliente cabe na área dele: as linhas, o tamanho da fonte e o aperto horizontal.</summary>
/// <param name="EscalaX">1 = letra natural; menor = apertada na horizontal (nunca abaixo do mínimo pedido).</param>
public readonly record struct AjusteNome(IReadOnlyList<string> Linhas, double Tamanho, double EscalaX);

/// <summary>A folha que o driver recebe: tamanho declarado e giro do desenho dentro dela.</summary>
public readonly record struct FolhaEtiqueta(double LarguraMm, double AlturaMm, int Giro);

/// <summary>
/// A COMANDA EM ETIQUETA 10x15 (05/10/2026, pedido do dono): a cozinha imprime a comanda
/// na Elgin de etiqueta e marca o PRONTO bipando o QR dela com um leitor USB.
///
/// Lógica pura: o que vai em cada linha, onde vai quadradinho, como o nome longo é
/// cortado e o que o QR carrega. O desenho (WPF) mora em Pdv/ImpressaoEtiqueta.cs e só
/// pinta o que sai daqui, para a suíte provar o conteúdo sem impressora.
///
/// A REGRA DO QUADRADINHO: confere-se o que entra na sacola. Item simples tem caixa.
/// Combo NÃO tem caixa no título ("1× Combo Box"): tem em CADA sabor, porque é donut a
/// donut que a caixa sai errada. Um quadradinho no combo convidaria a riscar o combo
/// inteiro sem olhar os sabores.
/// </summary>
public static class EtiquetaKds
{
    /// <summary>
    /// Prefixo fixo do QR. É o que faz o leitor USB não confundir a etiqueta com outro
    /// código (código de barras de produto, QR de Pix): sem ele, nada acontece.
    /// </summary>
    public const string PrefixoQr = "ADKDS:";

    /// <summary>
    /// O id da etiqueta de TESTE da Configuração. Bipar ela não marca nada: responde
    /// "leitor funcionando", que é o teste do leitor USB sem pedido de verdade.
    /// </summary>
    public const string IdTeste = "teste-leitor";

    /// <summary>Config: formato da comanda (bobina|etiqueta). Ausente = bobina.</summary>
    public const string ChaveFormato = "kds_comanda_formato";
    /// <summary>Config: impressora da etiqueta ("" ou ausente = padrão do Windows).</summary>
    public const string ChaveImpressora = "kds_etiqueta_impressora";
    /// <summary>Config: giro do desenho (0, 90, 180, 270). Ausente = 0.</summary>
    public const string ChaveGiro = "kds_etiqueta_giro";

    /// <summary>
    /// Config: palavras que marcam EMBALAGEM, separadas por vírgula ou ponto e vírgula.
    /// Ausente ou vazia = <see cref="EmbalagemPadrao"/>. Vale para etiqueta, card e bobina.
    /// </summary>
    public const string ChaveEmbalagem = "kds_embalagem_palavras";

    /// <summary>O que é embalagem quando a loja não configurou nada.</summary>
    public static readonly IReadOnlyList<string> EmbalagemPadrao = new[] { "Caixinha", "Embalagem", "Sacola" };

    public const double LarguraMm = 100;
    public const double AlturaMm = 150;

    /// <summary>Lado do QR no papel. O pedido do dono é no mínimo 35 mm.</summary>
    public const double QrLadoMm = 40;

    /// <summary>
    /// Piso do QR: só quando o pedido não cabe nem com a letra no mínimo legível, o QR
    /// cede de 40 para 35 mm (o mínimo que o dono pediu) antes de o miolo encolher.
    /// </summary>
    public const double QrLadoMinMm = 35;

    /// <summary>
    /// Teto do nome do cliente, em caracteres, cortado entre palavras. Acima disso o nome
    /// em duas linhas ficaria do tamanho do item; é raro (nome + sobrenome cabe com folga).
    /// </summary>
    public const int ClienteMaxCaracteres = 40;

    /// <summary>Lê a config. Qualquer coisa que não seja "etiqueta" é bobina: nada muda sem o dono escolher.</summary>
    public static FormatoComanda Formato(string? valorConfig)
        => string.Equals((valorConfig ?? "").Trim(), "etiqueta", StringComparison.OrdinalIgnoreCase)
            ? FormatoComanda.Etiqueta : FormatoComanda.Bobina;

    public static string TextoFormato(FormatoComanda f) => f == FormatoComanda.Etiqueta ? "etiqueta" : "bobina";

    /// <summary>Lê o giro. Só 0/90/180/270; lixo é 0.</summary>
    public static int Giro(string? valorConfig)
        => int.TryParse((valorConfig ?? "").Trim(), out var g) && g is 0 or 90 or 180 or 270 ? g : 0;

    /// <summary>
    /// A folha que se declara ao driver. Giro 0/180: folha em pé, 100 x 150. Giro 90/270:
    /// folha DEITADA, 150 x 100, com o desenho girado dentro dela; é para o driver que foi
    /// configurado com o rolo deitado (o caso que as etiquetas da fábrica já viram: "saiu
    /// 15 de altura e 10 de comprimento"). Qual dos quatro acerta só a Elgin responde: o
    /// dono escolhe pelo botão de teste.
    /// </summary>
    public static FolhaEtiqueta Folha(int giro)
    {
        var g = Giro(giro.ToString());
        return g is 90 or 270 ? new FolhaEtiqueta(AlturaMm, LarguraMm, g) : new FolhaEtiqueta(LarguraMm, AlturaMm, g);
    }

    /// <summary>
    /// O que o QR carrega: prefixo + order_id (ref_id do ticket). O order_id, e não o id
    /// local do ticket, porque é o mesmo em qualquer caixa da loja e sobrevive ao ticket
    /// ser recriado no SQLite; o leitor aceita os dois (<see cref="BipeKds.Localizar"/>).
    /// </summary>
    public static string ConteudoQr(Ticket t) => PrefixoQr + (t.RefId is { Length: > 0 } r ? r : t.Id);

    /// <summary>Origem como sai no papel. "CD-" no número é o cardápio próprio (mesma regra da comanda de bobina).</summary>
    public static string Origem(Ticket t)
        => t.Numero.StartsWith("CD-", StringComparison.OrdinalIgnoreCase) ? "CARDÁPIO WEB"
         : t.Origem == "ifood" ? "iFOOD"
         : "BALCÃO";

    /// <summary>Lê a config de embalagem. Ausente ou só separadores = <see cref="EmbalagemPadrao"/>.</summary>
    public static IReadOnlyList<string> PalavrasEmbalagem(string? valorConfig)
    {
        var palavras = (valorConfig ?? "")
            .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0).ToList();
        return palavras.Count > 0 ? palavras : EmbalagemPadrao;
    }

    /// <summary>
    /// O nome é de embalagem? Compara sem acento e sem caixa, pelo COMEÇO de palavra:
    /// "Caixinha Extra" e "Sacola Kraft" são; "Recaixinha" não. Só o começo, para
    /// "Caixinha" pegar "Caixinhas" e "Sacola" pegar "Sacolas".
    /// </summary>
    public static bool EEmbalagem(string? nome, IReadOnlyList<string>? palavras = null)
    {
        var n = SemAcento(nome ?? "");
        if (n.Length == 0) return false;
        foreach (var p in palavras ?? EmbalagemPadrao)
        {
            var w = SemAcento((p ?? "").Trim());
            if (w.Length == 0) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(n,
                    @"(?<![\p{L}\p{N}])" + System.Text.RegularExpressions.Regex.Escape(w)))
                return true;
        }
        return false;
    }

    private static string SemAcento(string s)
    {
        var d = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (var c in d)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Monta a etiqueta inteira de um ticket.</summary>
    /// <param name="hoje">O "hoje" de quem imprime (decide se a hora marcada sai com data). Só os testes cravam.</param>
    /// <param name="embalagem">Palavras de embalagem (<see cref="PalavrasEmbalagem"/>); null = padrão.</param>
    public static Etiqueta Montar(Ticket t, DateTime? hoje = null, IReadOnlyList<string>? embalagem = null)
    {
        var linhas = Linhas(t, embalagem);

        string? agendado = t.Agendado && t.AgendadoPara is { } p
            ? "AGENDADO para " + Kds.TextoHorario(p, t.AgendadoAte, hoje ?? DateTime.Now)
            : null;

        return new Etiqueta(t.Numero, Origem(t), NomeParaEtiqueta(t.Cliente),
                            agendado, $"Chegou {t.CriadoEm:HH:mm}", t.Retirada, linhas, ConteudoQr(t));
    }

    /// <summary>
    /// AS LINHAS DE CONFERÊNCIA de um pedido: a MESMA lista para a etiqueta, o card do
    /// KDS e a comanda de bobina (06/10, pedido iFood #6066: o dono ainda via o "Combo Box
    /// 4un" porque só a etiqueta seguia a regra; o card e a bobina mostravam o pai em
    /// destaque). Uma lista só para os três: a regra não pode valer num papel e no outro não.
    ///
    /// Item com subitens: o pai some e ficam os subitens, cada um com quadradinho. A
    /// embalagem (<see cref="EEmbalagem"/>) fica na lista SEM quadradinho, depois do que
    /// se produz. Se o item só tem embalagem como subitem ("Donut Homer" + "Sacola"), o
    /// pai FICA: sumir com ele seria sumir com o donut.
    /// </summary>
    public static List<LinhaEtiqueta> Linhas(Ticket t, IReadOnlyList<string>? embalagem = null)
    {
        var palavras = embalagem ?? EmbalagemPadrao;
        var linhas = new List<LinhaEtiqueta>();
        foreach (var i in t.Itens)
        {
            // Item COM subitens (06/10, dono): o pai some e ficam só os subitens, cada um com
            // o seu quadradinho. Com 2 combos, cada subitem vale 2 vezes (2 combos com 2 Homer
            // = 4× Homer): é o que entra na sacola. Exceção: o BALCÃO já grava as escolhas
            // multiplicadas pelas unidades da linha (Kds.DoBalcao -> Combos.LinhasKds); só o
            // delivery (iFood e cardápio), que manda a quantidade por unidade, multiplica aqui.
            var subs = new List<LinhaEtiqueta>();
            var embs = new List<LinhaEtiqueta>();
            if (i.Escolhas is { Count: > 0 })
            {
                var mult = t.Origem == "balcao" ? 1m : i.Qtd / 1000m;
                foreach (var esc in i.Escolhas)
                    if (Subitem(esc, mult) is { } sub)
                    {
                        if (EEmbalagem(sub.Texto, palavras)) embs.Add(sub with { Tipo = TipoLinhaEtiqueta.Embalagem });
                        else subs.Add(sub);
                    }
            }
            if (subs.Count > 0) linhas.AddRange(subs);
            else
            {
                // Sem subitem de produção, o pai é o que se produz (ou, ele mesmo, embalagem).
                var principal = CardKds.ItemPrincipal(i.Qtd, i.Descricao, null);
                linhas.Add(new LinhaEtiqueta(
                    EEmbalagem(principal.Nome, palavras) ? TipoLinhaEtiqueta.Embalagem : TipoLinhaEtiqueta.Item,
                    principal.Qtd, principal.Nome));
            }
            linhas.AddRange(embs);
            // A observação do pai continua, logo abaixo dos subitens dele.
            if (i.Observacao is { Length: > 0 } obs)
                linhas.Add(new LinhaEtiqueta(TipoLinhaEtiqueta.Observacao, "", obs.Trim()));
        }
        return linhas;
    }

    /// <summary>
    /// Um subitem com a quantidade multiplicada pelas unidades do pai. Escolha sem número
    /// legível ("Sem cobertura") fica sem quantidade quando o pai é 1 e ganha a do pai
    /// quando é mais (a mesma escolha vale para cada combo). Null para escolha vazia.
    /// </summary>
    public static LinhaEtiqueta? Subitem(string? escolha, decimal multiplicador)
    {
        var s = CardKds.SubItem(escolha);
        if (s.Nome.Length == 0 && s.Qtd.Length == 0) return null;
        if (multiplicador == 1m) return new LinhaEtiqueta(TipoLinhaEtiqueta.Subitem, s.Qtd, s.Nome);
        var q = CardKds.QtdDaEscolha(escolha) ?? 1m;
        return new LinhaEtiqueta(TipoLinhaEtiqueta.Subitem, Numero(q * multiplicador) + CardKds.Vezes, s.Nome);
    }

    private static string Numero(decimal q)
        => q % 1 == 0 ? ((long)q).ToString(System.Globalization.CultureInfo.InvariantCulture)
                      : q.ToString("0.###", new System.Globalization.CultureInfo("pt-BR"));

    /// <summary>
    /// O nome como vai para a etiqueta: espaços normalizados e, acima de
    /// <see cref="ClienteMaxCaracteres"/>, cortado na última palavra inteira com "…". Nunca
    /// corta no meio da palavra (a não ser a primeira, sozinha maior que o teto). Vazio = null.
    /// </summary>
    public static string? NomeParaEtiqueta(string? nome)
    {
        var s = string.Join(' ', (nome ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (s.Length == 0) return null;
        if (s.Length <= ClienteMaxCaracteres) return s;
        var corte = s.LastIndexOf(' ', ClienteMaxCaracteres - 1);
        return (corte > 0 ? s[..corte] : s[..(ClienteMaxCaracteres - 1)]).TrimEnd() + "…";
    }

    /// <summary>
    /// O MAIOR tamanho em que o nome cabe na caixa (06/10, esboço do dono: nome enorme no
    /// topo, letra estreita e alta). Tenta o nome numa linha e em cada quebra entre palavras
    /// em duas linhas, e fica com o arranjo de letra maior: nome curto sai enorme numa linha,
    /// nome longo vai para duas e só então diminui. Nunca corta letra: o tamanho sai da
    /// medida, e a letra encolhe até caber.
    ///
    /// O aperto horizontal é o segundo recurso, em dois degraus: primeiro até
    /// <paramref name="escalaPreferida"/> (quase não se nota numa fonte já condensada); só se
    /// mesmo assim a letra ficar abaixo de <paramref name="tamanhoConfortavel"/>, até
    /// <paramref name="escalaMin"/> (0,6, o limite do dono).
    /// </summary>
    /// <param name="larguraPorEm">Largura do texto com fonte de tamanho 1 (a tela mede com a fonte real).</param>
    /// <param name="alturaLinhaPorEm">Altura de uma linha com fonte de tamanho 1.</param>
    public static AjusteNome AjustarNome(string nome, double largura, double altura,
        Func<string, double> larguraPorEm, double alturaLinhaPorEm,
        double escalaPreferida = 0.85, double escalaMin = 0.6, double tamanhoConfortavel = 34)
    {
        var palavras = nome.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var arranjos = new List<string[]> { new[] { string.Join(' ', palavras) } };
        for (var k = 1; k < palavras.Length; k++)
            arranjos.Add(new[] { string.Join(' ', palavras[..k]), string.Join(' ', palavras[k..]) });

        AjusteNome Melhor(double apertoMax)
        {
            AjusteNome? melhor = null;
            var larguraDoMelhor = double.MaxValue;
            foreach (var a in arranjos)
            {
                var porAltura = altura / (a.Length * alturaLinhaPorEm);
                var maisLarga = a.Max(larguraPorEm);
                if (maisLarga <= 0) continue;
                var tam = Math.Min(porAltura, largura / (maisLarga * apertoMax));
                var escala = Math.Min(1.0, largura / (maisLarga * tam));
                // Empate de tamanho (menos de 2%): fica o de MENOS linhas (vem antes); com as
                // mesmas linhas, o de linha mais curta: menos aperto e quebra equilibrada
                // ("Ana Beatriz / Souza" e não "Ana / Beatriz Souza").
                var b = melhor;
                var ganha = b is null || tam > b.Value.Tamanho * 1.02
                    || (tam >= b.Value.Tamanho * 0.98 && a.Length == b.Value.Linhas.Count && maisLarga < larguraDoMelhor);
                if (ganha) { melhor = new AjusteNome(a, tam, escala); larguraDoMelhor = maisLarga; }
            }
            return melhor ?? new AjusteNome(new[] { nome }, 0, 1);
        }

        var primeiro = Melhor(escalaPreferida);
        return primeiro.Tamanho >= tamanhoConfortavel ? primeiro : Melhor(escalaMin);
    }

    /// <summary>
    /// O texto parece o começo de uma etiqueta bipada? Serve à busca do KDS: o leitor
    /// digitando dentro do campo de busca não pode virar filtro de pedido.
    /// </summary>
    public static bool PareceEtiqueta(string? texto)
        => (texto ?? "").TrimStart().StartsWith("ADKDS", StringComparison.OrdinalIgnoreCase);
}
