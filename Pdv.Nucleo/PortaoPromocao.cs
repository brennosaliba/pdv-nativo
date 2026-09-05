namespace Pdv.Nucleo;

// ════════════════════════════════════════════════════════════════════════════
//  PORTAO DA PROMOCAO COM 2FA (05/09/2026, pedido do dono)
//
//  "Na aba promocoes do PDV a opcao de exigir senha do gerente para liberacao;
//   ex.: crio desconto funcionario para nao ter abuso e quero que essa promocao
//   tenha 2FA do gerente."
//
//  O motor (Promocoes.AvaliarCarrinho) devolve em Pendentes as promocoes que
//  venceriam mas exigem codigo e ainda nao foram perguntadas NESTA venda. Aqui
//  mora o que a tela faz com isso, sem WPF, para a suite exercitar:
//   . para cada pendente, UMA passagem por Autorizacao.ResolverAsync (a mesma
//     maquina de estados do estorno), com Tipo="promocao", Nivel = o da promocao
//     ('gerente' aceita manager e owner; 'dono' so owner) e a referencia da
//     comanda + promocao;
//   . ok  -> entra em Autorizadas com o id do registro e quem aprovou; a tela
//            reavalia e a promocao passa a valer;
//   . nao (cancelou, 3 erros, sem rede, sem sessao, sem autenticador) -> entra em
//            Excluidas; a tela reavalia SEM ela e avisa numa linha.
//  Depois disto a promocao nunca mais e perguntada nesta venda: esta num dos
//  dois conjuntos. Nova venda zera (ContextoAutorizacao.Zerar) e pergunta de novo.
//
//  Quem grava auditoria e mostra aviso e a tela, a partir de Resultado; aqui nao
//  ha banco nem janela.
// ════════════════════════════════════════════════════════════════════════════
public static class PortaoPromocao
{
    /// <summary>O que aconteceu com uma promocao pendente nesta passagem.</summary>
    public sealed record Resultado(PromoPendenteRef Promo, DesfechoAutorizacao Desfecho)
    {
        public bool Autorizada => Desfecho.Autorizado;
    }

    /// <summary>Identidade da promocao para a auditoria/aviso (sem depender do record do motor).</summary>
    public sealed record PromoPendenteRef(string PromoId, string Nome, string Nivel, long DescontoCent);

    /// <summary>Dados fixos da comanda que vao no pedido e no log da nuvem.</summary>
    public sealed record Comanda(string Id, string Terminal, string? Loja, string? Operador);

    /// <summary>O pedido que vai a nuvem para UMA promocao (testavel separado da tela).</summary>
    public static PedidoAutorizacao Pedido(Comanda comanda, Promocoes.PromoPendente p) => new(
        comanda.Terminal, Autorizacao.ReferenciaPromocao(comanda.Id, p.PromoId), p.DescontoCent,
        Loja: comanda.Loja, Operador: comanda.Operador)
    {
        Tipo = "promocao",
        Nivel = p.Nivel == Promocoes.NivelGerente ? Autorizacao.NivelGerente : Autorizacao.NivelDono,
        PromocaoId = p.PromoId,
        PromocaoNome = p.Nome,
    };

    /// <summary>
    /// Pergunta UMA vez por promocao pendente e escreve a resposta no contexto.
    /// Pendente que ja foi respondida (o carrinho mudou no meio) e pulada.
    /// Sem ConfigureAwait(false): quem chama e a thread de UI (ver Autorizacao.ResolverAsync).
    /// </summary>
    public static async Task<List<Resultado>> ResolverAsync(
        IReadOnlyList<Promocoes.PromoPendente> pendentes, Promocoes.ContextoAutorizacao contexto,
        Comanda comanda, IAutorizacaoRemota? remota, ITelaAutorizacao tela, CancellationToken ct = default)
    {
        var r = new List<Resultado>();
        foreach (var p in pendentes)
        {
            if (!contexto.Pendente(p.PromoId)) continue;
            DesfechoAutorizacao d;
            try
            {
                d = await Autorizacao.ResolverAsync(remota, Pedido(comanda, p), tela, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Falha inesperada nao libera desconto: recusa, como no estorno.
                d = new DesfechoAutorizacao(ViaAutorizacao.Recusada, null, null,
                    "Falha na autorização (" + ex.GetType().Name + "). Promoção não autorizada.");
            }
            if (d.Autorizado && d.TokenId is { Length: > 0 })
                contexto.Autorizar(p.PromoId, d.TokenId, d.AprovadoPor ?? Autorizacao.Papel(d.Nivel));
            else
            {
                if (d.Autorizado)   // aprovado sem id de registro: nao da para auditar, entao nao vale
                    d = new DesfechoAutorizacao(ViaAutorizacao.Recusada, null, null,
                        "A nuvem aprovou sem registro. Promoção não autorizada.");
                contexto.Excluir(p.PromoId);
            }
            r.Add(new Resultado(new PromoPendenteRef(p.PromoId, p.Nome, p.Nivel, p.DescontoCent), d));
        }
        return r;
    }

    /// <summary>
    /// A comanda mudou (item entrou, saiu, mudou de quantidade). Se ficou VAZIA por
    /// qualquer caminho ('-' ate zero, lixeira, limpar, venda), o que ela decidiu
    /// sobre promocoes com codigo morre com ela: comanda vazia e comanda nova, e o
    /// proximo cliente pergunta de novo. Devolve true quando zerou (a tela troca o
    /// id da comanda). A tela chama num ponto so: a pintura, que toda mudanca dispara.
    /// </summary>
    public static bool ComandaMudou(Promocoes.ContextoAutorizacao contexto, int itensNaComanda)
    {
        if (itensNaComanda > 0) return false;
        contexto.Zerar();
        return true;
    }

    /// <summary>Aviso de UMA linha para a tela quando a promocao nao entrou.</summary>
    public static string AvisoNaoAplicada(string nome) => $"Promoção {nome} não aplicada";

    /// <summary>As recusadas de uma passagem numa linha so (nunca um aviso por promocao).</summary>
    public static string AvisoNaoAplicada(IReadOnlyList<string> nomes) => nomes.Count switch
    {
        0 => "",
        1 => AvisoNaoAplicada(nomes[0]),
        _ => $"Promoções {string.Join(", ", nomes.Take(nomes.Count - 1))} e {nomes[^1]} não aplicadas",
    };

    /// <summary>Linha da auditoria local: promocao, nivel, quem aprovou e o registro na nuvem.</summary>
    public static string LinhaAuditoria(Resultado r) => r.Autorizada
        ? $"promo={r.Promo.PromoId} ({r.Promo.Nome}) nivel={r.Promo.Nivel} desconto={new Dinheiro(r.Promo.DescontoCent).Formatado()}{Autorizacao.Trilha(r.Desfecho)}"
        : $"promo={r.Promo.PromoId} ({r.Promo.Nome}) nivel={r.Promo.Nivel} recusada: {r.Desfecho.Motivo}";
}
