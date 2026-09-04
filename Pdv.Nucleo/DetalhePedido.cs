namespace Pdv.Nucleo;

/// <summary>Um item do pedido na tela de detalhe, já com o texto pronto.</summary>
/// <param name="Qtd">A quantidade SEM o "×": ela vai num círculo, sozinha ("2", "1,5").</param>
/// <param name="Nome">O nome sem a cauda redundante do combo (a mesma regra do card).</param>
/// <param name="Escolhas">O que o cliente montou dentro do combo, linha a linha.</param>
/// <param name="Observacao">A instrução do cliente para ESTE item ("sem castanha").</param>
public sealed record ItemDetalhe(string Qtd, string Nome, IReadOnlyList<LinhaCard> Escolhas,
                                 string? Observacao);

/// <summary>
/// O CONTEÚDO da tela de detalhe do pedido no KDS: o que aparece quando alguém toca
/// no cabeçalho do card. Lógica pura, sem WPF, pelo mesmo motivo de <see cref="CardKds"/>:
/// quem prova que o agendado diz "Agendado para 18:30" e que o localizador some
/// quando não veio é a suíte, não o olho de quem abriu a tela.
///
/// NASCEU DE UM PEDIDO DO DONO (04/09), olhando o KDS na loja: "criar no KDS do PDV um
/// pop-up que quando clica ele visualiza o pedido, com uma tela melhor, assim como é o
/// iFood". A referência é a tela de detalhe do Gestor: número grande, cliente, "Feito
/// às 16:53", localizador, badge de origem, a faixa de pedidos agrupados e os itens
/// com a quantidade num círculo.
///
/// SEM PREÇO, de propósito. Cozinha não precisa e polui: nem por item, nem total.
///
/// A ORDEM DOS CAMPOS é a ordem de importância para quem produz: os itens (com o que
/// o combo tem dentro e a observação em destaque, porque observação é o que mais
/// erra na cozinha), número, cliente, hora de chegada, hora marcada se agendado,
/// entrega ou retirada, prazo. As seções da NUVEM (localizador, código de coleta,
/// observação do pedido, agrupado com) chegam depois, pela RPC de detalhe, e só
/// aparecem quando o dado existe: nulo = seção invisível, nunca "Localizador: -".
/// </summary>
/// <param name="Numero">"#5077", já com o cerquilha.</param>
/// <param name="Etapa">A coluna em que o card está, com o MESMO nome do quadro
/// ("NA FILA", "FAZENDO", "PRONTO"): o detalhe abre de qualquer coluna.</param>
/// <param name="Canal">"via iFood", "via Cardápio" ou "Balcão".</param>
/// <param name="Cliente">Nome do cliente; null quando o pedido não trouxe.</param>
/// <param name="FeitoAs">"Feito às 16:53" (ou "Feito 03/09 às 23:10" quando não é hoje).</param>
/// <param name="Agendado">"Agendado para 18:30" (faixa e data pelas regras da comanda); null se imediato.</param>
/// <param name="Modalidade">"Entrega" ou "Retirada no balcão"; null no balcão, onde não há escolha.</param>
/// <param name="Prazo">"Preparar até 17:13", o prazo que o iFood prometeu; null sem prazo.</param>
/// <param name="Comecou">"Começou às 16:55"; null enquanto ninguém assumiu.</param>
/// <param name="ProntoAs">"Pronto às 17:08"; null enquanto não saiu do forno.</param>
/// <param name="Localizador">Da nuvem. Null = seção some.</param>
/// <param name="CodigoColeta">Da nuvem, em destaque: é o que o motoboy diz no balcão. Null = some.</param>
/// <param name="Observacoes">Da nuvem: observação do pedido inteiro. Null = some.</param>
/// <param name="AgrupadoCom">Da nuvem: os OUTROS pedidos do grupo, já como "#3788". Vazio = some.</param>
/// <param name="Itens">Os itens, na ordem do pedido. Nunca vazio para um ticket de verdade.</param>
public sealed record DetalhePedido(
    string Numero, string Etapa, string Canal, string? Cliente,
    string FeitoAs, string? Agendado, string? Modalidade, string? Prazo,
    string? Comecou, string? ProntoAs,
    string? Localizador, string? CodigoColeta, string? Observacoes,
    IReadOnlyList<string> AgrupadoCom,
    IReadOnlyList<ItemDetalhe> Itens)
{
    /// <summary>"Agrupado com #3788 #9002", ou null quando o pedido vai sozinho.</summary>
    public string? AgrupadoTexto =>
        AgrupadoCom.Count == 0 ? null : "Agrupado com " + string.Join(" ", AgrupadoCom);

    /// <summary>A nuvem já respondeu com alguma coisa que vale seção?</summary>
    public bool TemComplemento =>
        Localizador is not null || CodigoColeta is not null || Observacoes is not null || AgrupadoCom.Count > 0;

    /// <summary>
    /// Monta o detalhe a partir do ticket LOCAL e, quando já houver, do complemento da
    /// nuvem. <paramref name="agora"/> é o "hoje" de quem olha: decide se a hora sai
    /// com a data. Só os testes cravam; a tela usa o relógio.
    /// </summary>
    public static DetalhePedido De(Ticket t, DateTime agora, DetalheNuvem? nuvem = null)
    {
        var eCardapio = t.Numero.StartsWith("CD-", StringComparison.OrdinalIgnoreCase);
        var canal = t.Origem == "ifood" ? (eCardapio ? "via Cardápio" : "via iFood") : "Balcão";
        var etapa = t.Status switch
        {
            Kds.Preparando => "FAZENDO",
            Kds.Pronto => "PRONTO",
            _ => "NA FILA",
        };
        // A hora de CHEGADA, a mesma que a comanda chama de "Chegou": é o relógio
        // que a cozinha usa para saber quem está esperando há mais tempo.
        var feito = t.CriadoEm.Date == agora.Date
            ? $"Feito às {t.CriadoEm:HH:mm}"
            : $"Feito {t.CriadoEm:dd/MM} às {t.CriadoEm:HH:mm}";
        // Mesmo texto da comanda e do card (Kds.TextoHorario): "18:30", "18:00 a 18:30"
        // ou "05/09 18:30" quando não é hoje.
        var agendado = t.Agendado && t.AgendadoPara is { } marcado
            ? "Agendado para " + Kds.TextoHorario(marcado, t.AgendadoAte, agora)
            : null;
        // Entrega ou retirada só faz sentido no delivery: no balcão o cliente está ali.
        var modalidade = t.Origem == "ifood" ? (t.Retirada ? "Retirada no balcão" : "Entrega") : null;

        var itens = t.Itens.Select(i => new ItemDetalhe(
                CardKds.Quantidade(i.Qtd),
                CardKds.ItemPrincipal(i).Nome,
                i.Escolhas is { Count: > 0 }
                    ? i.Escolhas.Select(CardKds.SubItem).Where(l => l.Nome.Length > 0).ToList()
                    : Array.Empty<LinhaCard>(),
                i.Observacao is { Length: > 0 } ? i.Observacao.Trim() : null))
            .ToList();

        return new DetalhePedido(
            "#" + t.Numero, etapa, canal,
            t.Cliente is { Length: > 0 } ? t.Cliente.Trim() : null,
            feito, agendado, modalidade,
            t.PreparoAte is { } prazo ? $"Preparar até {prazo:HH:mm}" : null,
            t.PreparoEm is { } comecou ? $"Começou às {comecou:HH:mm}" : null,
            t.ProntoEm is { } pronto ? $"Pronto às {pronto:HH:mm}" : null,
            Limpo(nuvem?.Localizador), Limpo(nuvem?.CodigoColeta), Limpo(nuvem?.Observacoes),
            Agrupados(nuvem?.AgrupadoCom, t.Numero),
            itens);
    }

    /// <summary>O mesmo detalhe, agora com o complemento que a nuvem acabou de mandar.</summary>
    public DetalhePedido ComNuvem(DetalheNuvem? nuvem) => this with
    {
        Localizador = Limpo(nuvem?.Localizador),
        CodigoColeta = Limpo(nuvem?.CodigoColeta),
        Observacoes = Limpo(nuvem?.Observacoes),
        AgrupadoCom = Agrupados(nuvem?.AgrupadoCom, Numero.TrimStart('#')),
    };

    private static string? Limpo(string? s) => s is null || s.Trim().Length == 0 ? null : s.Trim();

    /// <summary>
    /// Os números do grupo como a tela mostra: "#3788", sem repetição e SEM o próprio
    /// pedido. O Gestor lista o próprio número junto ("agrupado com #3788 #9002 #3340
    /// #5077" na tela do 5077); aqui ele sai, porque "agrupado consigo mesmo" é ruído
    /// que faz o operador procurar um quinto pedido que não existe.
    /// </summary>
    public static IReadOnlyList<string> Agrupados(IEnumerable<string>? numeros, string proprio)
    {
        if (numeros is null) return Array.Empty<string>();
        var meu = proprio.Trim().TrimStart('#');
        var saida = new List<string>();
        foreach (var n in numeros)
        {
            var limpo = (n ?? "").Trim().TrimStart('#').Trim();
            if (limpo.Length == 0 || limpo.Equals(meu, StringComparison.OrdinalIgnoreCase)) continue;
            var texto = "#" + limpo;
            if (!saida.Contains(texto, StringComparer.OrdinalIgnoreCase)) saida.Add(texto);
        }
        return saida;
    }
}
