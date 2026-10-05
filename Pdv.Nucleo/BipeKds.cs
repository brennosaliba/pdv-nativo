using Dapper;

namespace Pdv.Nucleo;

/// <summary>O que aconteceu com um bipe.</summary>
public enum DesfechoBipe
{
    /// <summary>Estava A PREPARAR ou EM PREPARO e virou PRONTO agora.</summary>
    Pronto,
    /// <summary>Já estava pronto (ou alguém marcou entre a leitura e o toque).</summary>
    JaPronto,
    /// <summary>Já saiu do quadro (entregue/coletado).</summary>
    JaSaiu,
    /// <summary>O pedido foi cancelado: não vai para coleta.</summary>
    Cancelado,
    /// <summary>A mesma etiqueta bipada de novo dentro da janela: ignorado em silêncio.</summary>
    Repetido,
    /// <summary>Nenhum pedido com esse código neste caixa.</summary>
    NaoReconhecida,
    /// <summary>A etiqueta de teste da Configuração: o leitor está funcionando.</summary>
    Teste,
}

/// <summary>O desfecho e o texto de uma linha que a tela mostra grande.</summary>
public sealed record ResultadoBipe(DesfechoBipe Desfecho, string? TicketId, string? Numero, string Mensagem)
{
    /// <summary>Virou pronto agora: é o caso que cutuca a fila e pisca o card.</summary>
    public bool Liberou => Desfecho == DesfechoBipe.Pronto;
}

/// <summary>
/// O PRONTO PELO BIPE (05/10/2026). Bipar o QR da etiqueta faz EXATAMENTE o caminho do
/// rodapé PRONTO·COLETA do card: <see cref="Kds.Liberar"/> (status + outbox kds_pronto no
/// mesmo commit); quem chama cutuca o dreno. Sem o pop-up de confirmação: o bipe É a
/// confirmação, porque só se bipa a etiqueta que está colada na sacola fechada.
///
/// Pedido A PREPARAR pula o FAZENDO: o bipe assume e libera na hora (o rodapé faria isso
/// em dois toques). Pedido do cardápio web é ticket de origem 'ifood' com número CD-: o
/// MESMO Liberar, a mesma outbox, o mesmo destino que o botão já usa para eles.
///
/// BIPE DUPLO: o leitor às vezes lê duas vezes a mesma etiqueta (mão parada em cima do
/// QR). A mesma etiqueta dentro de <see cref="JanelaRepetido"/> é ignorada sem chamar
/// nada; depois disso o segundo bipe só ouve "já estava pronto". Liberar já é idempotente
/// no banco; a janela existe para a tela não gritar "já estava pronto" na cara de quem
/// acabou de marcar.
/// </summary>
public sealed class BipeKds
{
    public static readonly TimeSpan JanelaRepetidoPadrao = TimeSpan.FromSeconds(2);

    private readonly Func<string, Ticket?> _localizar;
    private readonly Func<Ticket, bool> _marcarPronto;
    private string? _ultimoCodigo;
    private DateTime _ultimoEm = DateTime.MinValue;

    public TimeSpan JanelaRepetido { get; }

    /// <param name="localizar">Acha o ticket pelo código da etiqueta (id ou order_id).</param>
    /// <param name="marcarPronto">O caminho do botão PRONTO. Devolve true se mudou.</param>
    public BipeKds(Func<string, Ticket?> localizar, Func<Ticket, bool> marcarPronto, TimeSpan? janelaRepetido = null)
    {
        _localizar = localizar;
        _marcarPronto = marcarPronto;
        JanelaRepetido = janelaRepetido ?? JanelaRepetidoPadrao;
    }

    /// <summary>O bipe da operação: SQLite do caixa e o Liberar de verdade.</summary>
    public static BipeKds Padrao() => new(Localizar, MarcarPronto);

    public ResultadoBipe Processar(string? codigo, DateTime agora)
    {
        var c = (codigo ?? "").Trim().ToLowerInvariant();
        if (c.Length == 0)
            return new ResultadoBipe(DesfechoBipe.NaoReconhecida, null, null, "Etiqueta não reconhecida");

        if (c == _ultimoCodigo && agora - _ultimoEm < JanelaRepetido && agora >= _ultimoEm)
            return new ResultadoBipe(DesfechoBipe.Repetido, null, null, "");
        _ultimoCodigo = c;
        _ultimoEm = agora;

        if (c == EtiquetaKds.IdTeste)
            return new ResultadoBipe(DesfechoBipe.Teste, null, null, "Leitor OK: etiqueta de teste");

        var t = _localizar(c);
        if (t is null)
            return new ResultadoBipe(DesfechoBipe.NaoReconhecida, null, null, "Etiqueta não reconhecida");

        switch (t.Status)
        {
            case Kds.Recebido:
            case Kds.Preparando:
                return _marcarPronto(t)
                    ? new ResultadoBipe(DesfechoBipe.Pronto, t.Id, t.Numero, $"PRONTO {t.Numero}")
                    // Mudou de coluna entre a leitura e o Liberar (outro toque ganhou a corrida).
                    : new ResultadoBipe(DesfechoBipe.JaPronto, t.Id, t.Numero, $"{t.Numero} já estava pronto");
            case Kds.Pronto:
                return new ResultadoBipe(DesfechoBipe.JaPronto, t.Id, t.Numero, $"{t.Numero} já estava pronto");
            case Kds.Cancelado:
                return new ResultadoBipe(DesfechoBipe.Cancelado, t.Id, t.Numero, $"{t.Numero} foi cancelado");
            default:
                return new ResultadoBipe(DesfechoBipe.JaSaiu, t.Id, t.Numero, $"{t.Numero} já saiu do quadro");
        }
    }

    /// <summary>
    /// O ticket do código: primeiro pelo order_id (é o que a etiqueta carrega), depois pelo
    /// id local. Havendo mais de um (não deveria), o ABERTO mais novo ganha.
    /// </summary>
    public static Ticket? Localizar(string codigo)
    {
        using var cx = Banco.Abrir();
        var r = cx.QueryFirstOrDefault(
            @"SELECT * FROM kds_ticket
               WHERE lower(ref_id) = @c OR lower(id) = @c
               ORDER BY CASE WHEN status IN ('recebido','preparando','pronto') THEN 0 ELSE 1 END,
                        criado_em DESC
               LIMIT 1", new { c = codigo.Trim().ToLowerInvariant() });
        if (r is null) return null;
        return (Ticket)Kds.Ler(r);
    }

    /// <summary>O caminho do botão PRONTO: assume se ainda está na fila, e libera.</summary>
    public static bool MarcarPronto(Ticket t)
    {
        if (t.Status == Kds.Recebido) Kds.Assumir(t.Id);
        return Kds.Liberar(t.Id);
    }
}
