using Dapper;

namespace Pdv.Nucleo;

/// <summary>O que aconteceu ao tentar voltar um card UMA etapa.</summary>
public enum VoltaKds
{
    /// <summary>Voltou. O card já está na coluna anterior.</summary>
    Feito,

    /// <summary>
    /// O card não estava mais na etapa de onde se queria voltar: outro monitor mexeu,
    /// a nuvem reconciliou, ou foram dois toques no mesmo card. Não é erro de ninguém,
    /// e principalmente NÃO é uma transição inventada.
    /// </summary>
    ForaDaEtapa,

    /// <summary>
    /// PRONTO de iFood cujo aviso JÁ saiu daqui (ou que ficou pronto do lado de lá).
    /// Não volta, e o operador precisa saber por quê: readyToPickup não se desfaz.
    /// </summary>
    IFoodJaAvisado,
}

/// <summary>
/// VOLTAR UMA ETAPA no quadro de preparo.
///
/// Nasceu de um relato do dono (04/09, 0.5.3 na Savassi): "pedido 9507 e 5077 foi
/// clicado marcar fazendo porem nao tem como desfazer caso tenha clicado errado".
/// O toque errado num quadro touch de balcão não é hipótese: é o dia normal, com
/// farinha na mão e fila no caixa.
///
/// ⚠️ AS DUAS VOLTAS NÃO SÃO A MESMA COISA, e é por isso que existe um enum em vez de
/// um bool:
///
///   FAZENDO → NA FILA  é local e sem consequência nenhuma fora desta máquina. O
///   carimbo de início é apagado (<see cref="Kds.Desassumir"/>) e ninguém lá fora
///   soube de nada. Volta sempre, sem janela de tempo: não há o que proteger.
///
///   PRONTO → FAZENDO   pode ser uma DECLARAÇÃO JÁ FEITA. Em pedido de iFood, o
///   <see cref="Kds.Liberar"/> enfileira um aviso na outbox; o dreno o envia (a cada
///   45 s), a nuvem carimba kds_pronto_em e a ponte dispara o readyToPickup no iFood.
///   Depois disso o entregador JÁ FOI ACIONADO e nenhum toque nesta tela desaciona.
///   Então a regra é o FATO, não o relógio: enquanto o aviso está na fila e não saiu,
///   a volta é honesta (e a linha da fila morre junto, senão o aviso sairia depois de
///   um "desfeito"); depois que saiu, a volta é recusada com uma frase de uma linha.
///
///   Um "pronto" de iFood SEM linha nenhuma na outbox também é recusado: ele não foi
///   declarado aqui, veio de fora (o Gestor marcou pronto e
///   <see cref="Kds.PromoverProntoDelivery"/> trouxe o card para a coluna de coleta).
///   Voltar seria mentira dupla: o iFood continua achando que está pronto, e a próxima
///   reconciliação empurraria o card de volta sozinho.
///
/// LIMITE CONHECIDO: a checagem é "o aviso ainda não estava marcado como enviado". Se
/// o dreno estiver EXATAMENTE no meio do envio (a chamada HTTP saiu e a resposta não
/// voltou) no instante do toque, o desfazer local acontece e o iFood recebe o pronto
/// assim mesmo. É uma janela de alguns décimos de segundo a cada 45 s, e fechá-la
/// exigiria mudar o dreno; preferi deixá-la escrita a fingir que não existe.
/// </summary>
public static class DesfazerKds
{
    /// <summary>
    /// Volta o card uma etapa, seja qual for a em que ele está. Um método só (e não um
    /// por transição) porque quem chama é UM botão: o card sabe onde está, o operador não
    /// precisa saber.
    /// </summary>
    public static VoltaKds Voltar(string ticketId)
    {
        string status, origem, refId;
        using (var cx = Banco.Abrir())
        {
            var t = cx.QueryFirstOrDefault(
                "SELECT origem, ref_id, status FROM kds_ticket WHERE id = @id", new { id = ticketId });
            if (t is null) return VoltaKds.ForaDaEtapa;
            status = (string)t.status;
            origem = (string)t.origem;
            refId = (string)t.ref_id;
        }

        if (status == Kds.Preparando)
            return Kds.Desassumir(ticketId) ? VoltaKds.Feito : VoltaKds.ForaDaEtapa;

        if (status != Kds.Pronto) return VoltaKds.ForaDaEtapa;
        return DoProntoParaFazendo(ticketId, origem, refId);
    }

    /// <summary>
    /// Dá para voltar este card? Só para a tela decidir o que MOSTRAR — a decisão de
    /// verdade é refeita dentro de <see cref="Voltar"/>, na mesma transação da volta.
    /// Perguntar aqui e agir lá é a corrida clássica; por isso este método não é
    /// autorização de nada.
    /// </summary>
    public static bool PodeVoltar(string ticketId)
    {
        using var cx = Banco.Abrir();
        var t = cx.QueryFirstOrDefault(
            "SELECT origem, ref_id, status FROM kds_ticket WHERE id = @id", new { id = ticketId });
        if (t is null) return false;
        if ((string)t.status == Kds.Preparando) return true;
        if ((string)t.status != Kds.Pronto) return false;
        if ((string)t.origem != "ifood") return true;
        return AvisoAindaNaFila(cx, (string)t.ref_id, null);
    }

    private static VoltaKds DoProntoParaFazendo(string ticketId, string origem, string refId)
    {
        // UMA transação para as duas coisas (matar o aviso e voltar o status) pelo mesmo
        // motivo que o Liberar junta as duas: separado, uma queda no meio deixaria o card
        // em FAZENDO com o aviso de PRONTO ainda na fila para sair.
        using var cx = Banco.Abrir();
        using var tx = cx.BeginTransaction();

        if (origem == "ifood")
        {
            if (!AvisoAindaNaFila(cx, refId, tx)) { tx.Rollback(); return VoltaKds.IFoodJaAvisado; }
            cx.Execute(
                "DELETE FROM outbox WHERE tipo = 'kds_pronto' AND ref_id = @r AND enviado_em IS NULL",
                new { r = refId }, tx);
        }

        // pronto_em volta a NULL pelo mesmo motivo que o Desassumir apaga o preparo_em:
        // o card nunca ficou pronto, e a hora velha faria o relógio da espera congelar
        // (Ticket.Espera conta até o pronto_em) num pedido que continua no forno.
        var mudou = cx.Execute(
            "UPDATE kds_ticket SET status = @para, pronto_em = NULL WHERE id = @id AND status = @de",
            new { id = ticketId, de = Kds.Pronto, para = Kds.Preparando }, tx) == 1;
        if (!mudou) { tx.Rollback(); return VoltaKds.ForaDaEtapa; }

        tx.Commit();
        return VoltaKds.Feito;
    }

    /// <summary>
    /// O aviso de pronto deste pedido ainda está na fila sem ter saído? Linha nenhuma
    /// também é "não": o pronto veio de fora, não foi declarado aqui.
    /// </summary>
    private static bool AvisoAindaNaFila(Microsoft.Data.Sqlite.SqliteConnection cx, string refId,
                                         Microsoft.Data.Sqlite.SqliteTransaction? tx)
        => cx.ExecuteScalar<int>(
               @"SELECT COUNT(*) FROM outbox
                  WHERE tipo = 'kds_pronto' AND ref_id = @r AND enviado_em IS NULL",
               new { r = refId }, tx) > 0;
}
