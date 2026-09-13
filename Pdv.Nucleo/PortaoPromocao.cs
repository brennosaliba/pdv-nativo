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
                // DESISTIR NAO E RECUSAR (08/09/2026). `Avisado` = foi o operador que
                // fechou a janela do codigo. Enquanto a pergunta saia sozinha na
                // pintura, excluir ali era o que impedia o laco: cada repintura
                // perguntaria de novo. Agora a pergunta e um TOQUE no botao, e excluir
                // vira armadilha: um toque errado, ou o gerente que ainda vai chegar,
                // matava a promocao para o resto da venda sem jeito de voltar atras.
                // Toda OUTRA recusa (sem internet, codigo errado tres vezes, nuvem que
                // negou) continua excluindo, que e onde insistir sozinho nao ajuda.
                if (!d.Avisado) contexto.Excluir(p.PromoId);
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
    /// <summary>
    /// O que o botão ao lado do total diz. Uma promoção: o nome dela, porque é isso que
    /// o operador vai conferir com o cliente. Mais de uma: só o número, senão a linha
    /// vira parede.
    ///
    /// POR QUE EXISTE (08/09/2026). Antes não havia botão: a janela do código abria
    /// sozinha a cada item bipado, porque uma promoção de 30% em "todos os produtos"
    /// vence sempre. O dono: "ela deveria pedir um poup com token somente qdo eh
    /// selacionado". O verbo é APLICAR: a promoção existe, ela vale, e alguém precisa
    /// liberar.
    /// </summary>
    public static string RotuloDoBotao(IReadOnlyList<string> nomes) => nomes.Count switch
    {
        0 => "",
        1 => $"Aplicar {nomes[0]}",
        _ => $"Aplicar promoção ({nomes.Count})",
    };

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

    // ════════════════════════════════════════════════════════════════════════
    //  O CARD NA CATEGORIA PROMOCAO (13/09/2026, Savassi)
    //
    //  "PROMOCAO FUNCIONARIO ATIVA E NAO APARECE NO PDV". Ela so aparecia no botao
    //  ao lado do total, e so depois do primeiro item. Agora tem card na categoria
    //  PROMOCAO, e o toque no card decide AQUI o que fazer. Pedir o codigo continua
    //  sendo ResolverAsync, o mesmo do botao: mesma nuvem, mesma auditoria. Nao
    //  existe caminho que aplique sem o codigo.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>O que o toque no card faz.</summary>
    public enum Toque
    {
        /// <summary>Promocao sumiu do caixa ou saiu da vigencia/janela desde a pintura.</summary>
        Indisponivel,
        /// <summary>Comanda sem item: nao pede codigo (nao ha desconto a liberar).</summary>
        ComandaVazia,
        /// <summary>Ja liberada nesta venda: nao pede de novo.</summary>
        JaAplicada,
        /// <summary>Pede o codigo (ResolverAsync com esta pendente).</summary>
        Perguntar,
        /// <summary>Alcanca a comanda, mas outra promocao da mais desconto (uma por pedido).</summary>
        OutraValeMais,
        /// <summary>Nenhum item da comanda entra nela.</summary>
        NaoAlcanca,
    }

    public sealed record DecisaoToque(Toque Acao, string Nome, Promocoes.PromoPendente? Pendente);

    /// <summary>
    /// Decide o toque no card de uma promocao com codigo.
    ///
    /// Unico efeito no contexto: se ela foi RECUSADA antes nesta venda (sem internet,
    /// codigo errado 3 vezes) e o toque vai perguntar, ela volta a ser pendente
    /// (<see cref="Promocoes.ContextoAutorizacao.Reabrir"/>), porque o operador pediu de
    /// novo. Nunca autoriza: isso so acontece em ResolverAsync, com o codigo conferido.
    /// </summary>
    public static DecisaoToque Tocar(string promoId, IReadOnlyList<Promocoes.Promo> promos,
        IReadOnlyList<Promocoes.ItemCarrinho> itens, DateTime agora, Promocoes.ContextoAutorizacao contexto,
        ISet<string>? combos = null)
    {
        var promo = promos.FirstOrDefault(p => p.Id == promoId && p.ExigeAutorizacao);
        if (promo is null || !Promocoes.Vigente(promo, agora)) return new(Toque.Indisponivel, promo?.Nome ?? "", null);
        if (itens.Count == 0) return new(Toque.ComandaVazia, promo.Nome, null);
        if (contexto.Autorizada(promoId)) return new(Toque.JaAplicada, promo.Nome, null);

        // simula com ela pendente, sem mexer no que a venda ja decidiu
        var simulado = contexto.Copia();
        simulado.Reabrir(promoId);
        var av = Promocoes.AvaliarCarrinho(promos, itens, agora, simulado, combos);
        var pendente = av.Pendentes.FirstOrDefault(p => p.PromoId == promoId);
        if (pendente is not null)
        {
            contexto.Reabrir(promoId);
            return new(Toque.Perguntar, promo.Nome, pendente);
        }
        // nao venceria: e porque outra vale mais, ou porque ela nao alcanca nada?
        var sozinha = Promocoes.AvaliarCarrinho(new[] { promo }, itens, agora, new Promocoes.ContextoAutorizacao(), combos);
        return new(sozinha.Pendentes.Count > 0 ? Toque.OutraValeMais : Toque.NaoAlcanca, promo.Nome, null);
    }

    /// <summary>Aviso de uma linha quando o toque no card nao pede codigo.</summary>
    public static string AvisoDoToque(Toque t) => t switch
    {
        Toque.Indisponivel => "Promoção indisponível agora.",
        Toque.ComandaVazia => "Adicione um item antes.",
        Toque.JaAplicada => "Promoção já aplicada nesta venda.",
        Toque.OutraValeMais => "Outra promoção vale mais nesta venda.",
        Toque.NaoAlcanca => "Nenhum item da venda entra nesta promoção.",
        _ => "",
    };

    /// <summary>A linha embaixo do nome no card: a regra e de quem e o codigo.</summary>
    public static string LinhaDoCard(string regra, string nivel) => $"{regra} · código do {Autorizacao.Papel(nivel)}";
}
