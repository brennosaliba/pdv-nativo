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
    /// <summary>Item com subitens (combo): título do grupo com a quantidade, SEM quadradinho.</summary>
    Grupo,
    /// <summary>Subitem do combo (sabor, complemento): recuado, COM quadradinho.</summary>
    Subitem,
    /// <summary>Observação do item, recuada e sem quadradinho.</summary>
    Observacao,
}

/// <summary>Uma linha do miolo da etiqueta.</summary>
/// <param name="Qtd">"2×", ou vazio (observação, escolha sem número).</param>
public sealed record LinhaEtiqueta(TipoLinhaEtiqueta Tipo, string Qtd, string Texto)
{
    /// <summary>Leva o quadradinho de conferência?</summary>
    public bool Caixa => Tipo is TipoLinhaEtiqueta.Item or TipoLinhaEtiqueta.Subitem;

    /// <summary>0 = encostado na margem; 1 = recuado sob o item.</summary>
    public int Nivel => Tipo is TipoLinhaEtiqueta.Subitem or TipoLinhaEtiqueta.Observacao ? 1 : 0;

    /// <summary>A linha como se lê, para teste e log.</summary>
    public string Lida => (Caixa ? "[ ] " : "") + (Qtd.Length == 0 ? Texto : Qtd + " " + Texto);
}

/// <summary>Tudo o que vai na etiqueta, já decidido. A tela só desenha.</summary>
/// <param name="Cliente">O nome em até <see cref="EtiquetaKds.ClienteLinhas"/> linhas, já cortado.</param>
/// <param name="Qr">Conteúdo EXATO do QR (prefixo + id).</param>
public sealed record Etiqueta(string Numero, string Origem, IReadOnlyList<string> Cliente,
                              string? Agendado, string Chegou, bool Retirada,
                              IReadOnlyList<LinhaEtiqueta> Linhas, string Qr);

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

    public const double LarguraMm = 100;
    public const double AlturaMm = 150;

    /// <summary>Lado do QR no papel. O pedido do dono é no mínimo 35 mm.</summary>
    public const double QrLadoMm = 40;

    /// <summary>Caracteres por linha do nome do cliente na fonte grande (cabe com folga em 92 mm).</summary>
    public const int ClienteColunas = 18;
    /// <summary>Linhas do nome do cliente. A terceira não existe: corta com reticências.</summary>
    public const int ClienteLinhas = 2;

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

    /// <summary>Monta a etiqueta inteira de um ticket.</summary>
    /// <param name="hoje">O "hoje" de quem imprime (decide se a hora marcada sai com data). Só os testes cravam.</param>
    public static Etiqueta Montar(Ticket t, DateTime? hoje = null)
    {
        var linhas = new List<LinhaEtiqueta>();
        foreach (var i in t.Itens)
        {
            var principal = CardKds.ItemPrincipal(i);
            var temSub = i.Escolhas is { Count: > 0 };
            linhas.Add(new LinhaEtiqueta(temSub ? TipoLinhaEtiqueta.Grupo : TipoLinhaEtiqueta.Item,
                                         principal.Qtd, principal.Nome));
            if (temSub)
                foreach (var esc in i.Escolhas!)
                {
                    var s = CardKds.SubItem(esc);
                    if (s.Nome.Length == 0 && s.Qtd.Length == 0) continue;
                    linhas.Add(new LinhaEtiqueta(TipoLinhaEtiqueta.Subitem, s.Qtd, s.Nome));
                }
            if (i.Observacao is { Length: > 0 } obs)
                linhas.Add(new LinhaEtiqueta(TipoLinhaEtiqueta.Observacao, "", obs.Trim()));
        }

        string? agendado = t.Agendado && t.AgendadoPara is { } p
            ? "AGENDADO para " + Kds.TextoHorario(p, t.AgendadoAte, hoje ?? DateTime.Now)
            : null;

        return new Etiqueta(t.Numero, Origem(t), CortarNome(t.Cliente, ClienteColunas, ClienteLinhas),
                            agendado, $"Chegou {t.CriadoEm:HH:mm}", t.Retirada, linhas, ConteudoQr(t));
    }

    /// <summary>
    /// O nome do cliente em até <paramref name="linhas"/> linhas de <paramref name="colunas"/>
    /// caracteres, quebrando entre palavras. O que não cabe some e a última linha ganha "…".
    /// Palavra maior que a linha é partida (nome sem espaço não pode estourar o papel).
    /// Nome vazio = lista vazia (a etiqueta não desenha a faixa do cliente).
    /// </summary>
    public static IReadOnlyList<string> CortarNome(string? nome, int colunas, int linhas)
    {
        var s = string.Join(' ', (nome ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (s.Length == 0 || colunas < 2 || linhas < 1) return Array.Empty<string>();

        var palavras = new Queue<string>();
        foreach (var w in s.Split(' '))
            for (var k = 0; k < w.Length; k += colunas)
                palavras.Enqueue(w.Substring(k, Math.Min(colunas, w.Length - k)));

        var saida = new List<string>();
        while (palavras.Count > 0 && saida.Count < linhas)
        {
            var linha = palavras.Dequeue();
            while (palavras.Count > 0 && linha.Length + 1 + palavras.Peek().Length <= colunas)
                linha += " " + palavras.Dequeue();
            saida.Add(linha);
        }
        if (palavras.Count > 0)
        {
            // Sobrou nome: a última linha cede o último caractere (ou o espaço) às reticências.
            var ult = saida[^1];
            ult = ult.Length + 1 <= colunas ? ult : ult[..(colunas - 1)];
            saida[^1] = ult.TrimEnd() + "…";
        }
        return saida;
    }

    /// <summary>
    /// O texto parece o começo de uma etiqueta bipada? Serve à busca do KDS: o leitor
    /// digitando dentro do campo de busca não pode virar filtro de pedido.
    /// </summary>
    public static bool PareceEtiqueta(string? texto)
        => (texto ?? "").TrimStart().StartsWith("ADKDS", StringComparison.OrdinalIgnoreCase);
}
