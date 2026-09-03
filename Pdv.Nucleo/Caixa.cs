using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

public sealed record Operador(string Id, string Nome, string Perfil)
{
    public bool ESupervisor => Perfil is "supervisor" or "gerente";
}

public sealed record Sessao(
    string Id, string BusinessDate, string OperadorId, string OperadorNome,
    DateTime AberturaEm, Dinheiro FundoTroco);

public sealed record LinhaFechamento(string Forma, Dinheiro Declarado, Dinheiro Apurado, bool Contada = true)
{
    public Dinheiro Diferenca => Declarado - Apurado;

    /// <summary>
    /// Sobra não é "boa notícia". Falta pode ser troco errado; sobra costuma ser venda
    /// que entrou na gaveta sem passar pelo PDV — o que também some do estoque e da
    /// nota fiscal. As duas precisam de explicação, e por motivos diferentes.
    /// </summary>
    public string Situacao => Diferenca.Centavos switch
    {
        0 => "confere",
        > 0 => "sobra",
        _ => "falta",
    };
}

/// <summary>Divergência entre o que o TEF cobrou e o que virou venda no PDV.</summary>
public sealed record DivergenciaTef(string Forma, Dinheiro NoTef, Dinheiro NaVenda)
{
    public Dinheiro Diferenca => NoTef - NaVenda;
}

/// <summary>
/// Ciclo do dinheiro: abertura, sangria/suprimento e fechamento CEGO.
///
/// Regras que este tipo faz valer (vieram do levantamento de como PDV de verdade opera):
///  - nenhuma venda sem caixa aberto;
///  - um turno por terminal, amarrado ao dia operacional;
///  - o fundo de troco é DECLARADO pelo operador (não sugerido pelo sistema) — é a
///    base aritmética do fechamento e a assinatura dele assumindo a custódia;
///  - movimento de caixa é append-only: corrigir é estornar, nunca editar;
///  - no fechamento o operador NÃO vê o esperado antes de contar.
/// </summary>
public static class Caixa
{
    private static string Agora => DateTime.Now.ToString("o");

    /// <summary>Dia operacional: 05:00 é a virada. Venda das 23h50 pertence ao dia que começou.</summary>
    public static string DiaOperacional(DateTime? momento = null)
    {
        var m = momento ?? DateTime.Now;
        return (m.Hour < 5 ? m.Date.AddDays(-1) : m.Date).ToString("yyyy-MM-dd");
    }

    public static Sessao? SessaoAberta(SqliteConnection cx)
    {
        var r = cx.QueryFirstOrDefault(
            "SELECT id, business_date, operador_id, operador_nome, abertura_em, fundo_troco_cent " +
            "FROM caixa_sessao WHERE status = 'aberto' LIMIT 1");
        if (r is null) return null;
        return new Sessao((string)r.id, (string)r.business_date, (string)r.operador_id,
            (string)r.operador_nome, DateTime.Parse((string)r.abertura_em),
            new Dinheiro((long)r.fundo_troco_cent));
    }

    /// <summary>
    /// Abre o turno. Recusa se já houver caixa aberto — inclusive de OUTRO DIA, que é o
    /// caso que mais estraga fechamento (as vendas de dois dias caem no mesmo turno e
    /// depois não dá pra separar).
    /// </summary>
    public static Sessao Abrir(SqliteConnection cx, Operador operador, Dinheiro fundoTroco)
    {
        if (fundoTroco.Centavos < 0) throw new InvalidOperationException("Fundo de troco não pode ser negativo.");
        var aberta = SessaoAberta(cx);
        if (aberta is not null)
        {
            var msg = aberta.BusinessDate == DiaOperacional()
                ? $"Já existe caixa aberto por {aberta.OperadorNome}. Feche antes de abrir outro."
                : $"O caixa de {aberta.BusinessDate} ficou aberto ({aberta.OperadorNome}). " +
                  "Feche aquele turno antes de começar o dia, senão as vendas dos dois dias se misturam.";
            throw new InvalidOperationException(msg);
        }

        // Quem ABRE o turno assina com o id canônico pelo mesmo motivo da venda: a
        // sessão sobe para o painel e `caixa_sessao.operador_id` tem chave estrangeira
        // para `operador`. Lido antes da transação (o SQLite recusa comando sem
        // transação numa conexão que já tem uma pendente).
        var idAssina = Operadores.IdCanonico(cx, operador.Id);
        var s = new Sessao(Guid.NewGuid().ToString(), DiaOperacional(), idAssina, operador.Nome,
            DateTime.Now, fundoTroco);

        using var tx = cx.BeginTransaction();
        cx.Execute("""
            INSERT INTO caixa_sessao (id, business_date, operador_id, operador_nome,
                                      abertura_em, fundo_troco_cent, status)
            VALUES (@Id, @Bd, @Op, @Nome, @Ab, @Fundo, 'aberto')
            """,
            new { Id = s.Id, Bd = s.BusinessDate, Op = idAssina, Nome = operador.Nome, Ab = Agora, Fundo = fundoTroco.Centavos }, tx);
        Auditar(cx, tx, "caixa_aberto", idAssina, null, $"fundo={fundoTroco.Reais:F2}");
        Enfileirar(cx, tx, "caixa_sessao", s.Id, s.Id, new { s.Id, s.BusinessDate, operador = idAssina, fundo_cent = fundoTroco.Centavos, abertura = Agora });
        tx.Commit();
        return s;
    }

    /// <summary>Sangria/suprimento. Supervisor obrigatório na sangria: é a operação que mais some com dinheiro.</summary>
    public static void Movimentar(SqliteConnection cx, Sessao sessao, string tipo, Dinheiro valor,
        string motivo, Operador operador, string? autorizadoPor = null, string? destino = null)
    {
        if (!valor.Positivo) throw new InvalidOperationException("Informe um valor maior que zero.");
        if (string.IsNullOrWhiteSpace(motivo)) throw new InvalidOperationException("Motivo é obrigatório.");
        if (tipo == "sangria" && string.IsNullOrWhiteSpace(autorizadoPor))
            throw new InvalidOperationException("Sangria exige autorização de supervisor.");
        // A TELA já barra isto, mas a regra mora AQUI: quem opera o caixa não pode
        // autorizar a própria sangria — a dupla assinatura é o único controle da
        // operação que mais some com dinheiro, e defesa que só existe na UI não é defesa.
        //
        // COMPARADA NO ID CANÔNICO, e não no id cru: quem opera pode estar logado com a
        // identidade que nasceu só nesta máquina (login feito antes de a sincronização
        // reconciliá-la com o painel), enquanto a autorização — que varre apenas
        // operadores ATIVOS — devolve a do painel. Ids crus diriam "são duas pessoas"
        // para a MESMA pessoa, e ela liberaria a própria sangria.
        var idAssina = Operadores.IdCanonico(cx, operador.Id);
        var idAutoriza = autorizadoPor is null ? null : Operadores.IdCanonico(cx, autorizadoPor);
        if (tipo == "sangria" && idAutoriza == idAssina)
            throw new InvalidOperationException(
                "Quem opera o caixa não pode autorizar a própria sangria. Chame outro supervisor.");

        // Não existe tirar da gaveta mais dinheiro do que ela tem. Sangria acima do
        // apurado é quase sempre dígito errado (9000 em vez de 90,00); quando não é,
        // significa dinheiro físico que o sistema desconhece — e aí o caminho certo é
        // registrar a venda/suprimento que faltou, nunca sangrar por cima.
        if (tipo == "sangria")
        {
            var gaveta = Apurado(cx, sessao).TryGetValue("dinheiro", out var g) ? g : Dinheiro.Zero;
            // NÃO revelar o valor apurado na mensagem: ele é justamente o número que a
            // contagem cega esconde do operador. Dizer "a gaveta tem R$ 1.842,30" seria
            // entregar o gabarito do fechamento. Barra sem dar o número.
            if (valor > gaveta)
                throw new InvalidOperationException(
                    $"Não dá pra sangrar {valor.Formatado()}: é mais do que entrou de dinheiro no caixa até agora. " +
                    "Confira o valor digitado; se o dinheiro existe mesmo, falta registrar a venda ou o suprimento que o trouxe.");
        }

        var id = Guid.NewGuid().ToString();
        using var tx = cx.BeginTransaction();
        cx.Execute("""
            INSERT INTO caixa_movimento (id, sessao_id, tipo, valor_cent, motivo, destino,
                                         operador_id, autorizado_por, criado_em)
            VALUES (@Id, @Ses, @Tipo, @Val, @Mot, @Dest, @Op, @Aut, @Em)
            """,
            new { Id = id, Ses = sessao.Id, Tipo = tipo, Val = valor.Centavos, Mot = motivo.Trim(),
                  Dest = destino, Op = idAssina, Aut = idAutoriza, Em = Agora }, tx);
        Auditar(cx, tx, $"caixa_{tipo}", idAssina, idAutoriza, $"{valor.Reais:F2} · {motivo}");
        Enfileirar(cx, tx, "movimento", id, id, new { id, sessao = sessao.Id, tipo, valor_cent = valor.Centavos, motivo, destino, autorizadoPor = idAutoriza });
        tx.Commit();
    }

    /// <summary>
    /// O que o sistema espera encontrar, por forma de pagamento.
    /// ⚠️ NÃO mostrar isso ao operador antes de ele declarar a contagem.
    ///
    /// Venda de TESTE (modo homologação) fica de fora — e é isso que mantém o
    /// fechamento consistente, não o contrário. O apurado é o que tem que ESTAR na
    /// gaveta/na maquininha: a venda do roteiro da PayGo não pôs dinheiro na gaveta
    /// (ninguém pagou) nem cobrou no extrato da adquirente (a cobrança é do ambiente
    /// de teste). Mantê-la no total faria a contagem cega acusar uma falta do tamanho
    /// exato do roteiro — R$ 2.493,00 no caso de hoje. Ela não some: sai do total e
    /// volta rotulada em <see cref="ApuradoDeTeste"/> / <see cref="ResumoDeTeste"/>.
    /// </summary>
    public static Dictionary<string, Dinheiro> Apurado(SqliteConnection cx, Sessao sessao)
    {
        var r = new Dictionary<string, Dinheiro>();
        var pagos = cx.Query("""
            SELECT p.forma, SUM(p.valor_cent - p.troco_cent) AS total
              FROM venda_pagamento p JOIN venda v ON v.id = p.venda_id
             WHERE v.sessao_id = @Ses AND v.status = 'finalizada' AND v.homologacao = 0
             GROUP BY p.forma
            """, new { Ses = sessao.Id });
        foreach (var p in pagos) r[(string)p.forma] = new Dinheiro((long)p.total);

        // dinheiro em gaveta = fundo + vendas em dinheiro + suprimentos − sangrias
        var supr = cx.ExecuteScalar<long?>("SELECT COALESCE(SUM(valor_cent),0) FROM caixa_movimento WHERE sessao_id=@S AND tipo='suprimento'", new { S = sessao.Id }) ?? 0;
        var sang = cx.ExecuteScalar<long?>("SELECT COALESCE(SUM(valor_cent),0) FROM caixa_movimento WHERE sessao_id=@S AND tipo='sangria'", new { S = sessao.Id }) ?? 0;
        var emDinheiro = r.TryGetValue("dinheiro", out var d) ? d : Dinheiro.Zero;
        r["dinheiro"] = emDinheiro + sessao.FundoTroco + new Dinheiro(supr) - new Dinheiro(sang);
        return r;
    }

    /// <summary>
    /// O que as vendas de TESTE (modo homologação) movimentaram no turno, por forma.
    ///
    /// Vive fora de <see cref="Apurado"/> de propósito, mas EXISTE: some do total e
    /// aparece rotulado. Aqui não entram fundo, sangria nem suprimento — esses são
    /// dinheiro de verdade na gaveta, e venda de teste não mexe em gaveta.
    /// </summary>
    public static Dictionary<string, Dinheiro> ApuradoDeTeste(SqliteConnection cx, Sessao sessao)
    {
        var r = new Dictionary<string, Dinheiro>();
        var pagos = cx.Query("""
            SELECT p.forma, SUM(p.valor_cent - p.troco_cent) AS total
              FROM venda_pagamento p JOIN venda v ON v.id = p.venda_id
             WHERE v.sessao_id = @Ses AND v.status = 'finalizada' AND v.homologacao = 1
             GROUP BY p.forma
            """, new { Ses = sessao.Id });
        foreach (var p in pagos) r[(string)p.forma] = new Dinheiro((long)p.total);
        return r;
    }

    /// <summary>
    /// Linha do relatório de fechamento para o turno que teve venda de TESTE — null
    /// quando não teve. O número aparece rotulado e fora do total: omiti-lo faria o
    /// operador procurar na gaveta uma diferença que nunca esteve lá.
    /// </summary>
    public static string? ResumoDeTeste(SqliteConnection cx, Sessao sessao)
    {
        var teste = ApuradoDeTeste(cx, sessao);
        if (teste.Count == 0) return null;
        var total = new Dinheiro(teste.Sum(kv => kv.Value.Centavos));
        return $"TESTE (modo homologação): {total.Formatado()} em " +
               string.Join(", ", teste.Select(kv => $"{kv.Key} {kv.Value.Formatado()}")) + ".\n" +
               "Não é faturamento: não subiu para a nuvem e não está nos totais acima.";
    }

    /// <summary>
    /// O dinheiro que DEVERIA estar na gaveta na próxima abertura: o que o operador
    /// declarou no último fechamento. Fechou com R$ 300 e ninguém fez sangria depois,
    /// o dia seguinte tem que abrir com R$ 300 — abertura que não bate com o
    /// fechamento anterior é dinheiro que sumiu (ou sobrou) FORA do expediente, o
    /// buraco que a conferência de turno sozinha nunca enxerga.
    /// Null quando nunca houve fechamento (primeiro dia do caixa).
    /// </summary>
    public static Dinheiro? FundoEsperado(SqliteConnection cx)
    {
        var v = cx.ExecuteScalar<long?>("""
            SELECT f.declarado_cent
              FROM caixa_fechamento f
              JOIN caixa_sessao s ON s.id = f.sessao_id
             WHERE f.forma = 'dinheiro' AND s.status = 'fechado'
             ORDER BY s.fechamento_em DESC LIMIT 1
            """);
        return v is null ? null : new Dinheiro(v.Value);
    }

    /// <summary>
    /// Formas que o operador CONTA de verdade no fechamento — e isso depende de como o
    /// cartão é cobrado neste caixa.
    ///
    /// COM TEF: só dinheiro. Cartão e PIX têm o valor conhecido pelo sistema, e pedir a
    /// contagem seria pedir para o operador copiar um número que ele não tem como
    /// conferir. Conferência de mentira não é neutra: ensina o operador a digitar o que
    /// o sistema espera, e a contagem do dinheiro vira o mesmo teatro.
    ///
    /// SEM TEF (POS avulsa): cartão e PIX são contados TAMBÉM — o total impresso no
    /// fechamento da maquininha é uma fonte independente de verdade, e comparar ele com
    /// o PDV é justamente o que pega venda passada na máquina e não registrada (ou o
    /// contrário).
    /// </summary>
    public static string[] FormasContadas(SqliteConnection cx)
        => Vendas.Config(cx, "tef_habilitado") == "1"
            ? new[] { "dinheiro" }
            : new[] { "dinheiro", "debito", "credito", "pix", "voucher" };

    /// <summary>
    /// Versão que olha o TURNO, não só a configuração. Mesmo com TEF ligado, uma venda
    /// pode ter saído como POS avulsa (o fallback de quando o TEF falha na hora) — e o
    /// pagamento manual não tem autorização do TEF gravada. Nesse caso a forma volta a
    /// ser CONTADA: o total do fechamento da maquininha cobre as duas (a integrada e a
    /// manual passam na mesma máquina), então a conferência fecha naturalmente.
    /// </summary>
    public static string[] FormasContadas(SqliteConnection cx, Sessao sessao)
    {
        var basicas = FormasContadas(cx);
        if (basicas.Length > 1) return basicas;            // sem TEF: já conta tudo

        // Venda de teste não conta aqui pelo mesmo motivo de Apurado: ela não está no
        // fechamento da maquininha, então não pode puxar uma forma de volta para a
        // contagem — o operador digitaria o total da máquina e sobraria a diferença.
        var manuais = cx.Query<string>("""
            SELECT DISTINCT p.forma
              FROM venda_pagamento p JOIN venda v ON v.id = p.venda_id
             WHERE v.sessao_id = @S AND v.status = 'finalizada' AND v.homologacao = 0
               AND p.forma <> 'dinheiro' AND p.tef_aut IS NULL
            """, new { S = sessao.Id }).ToList();
        return manuais.Count == 0 ? basicas : manuais.Prepend("dinheiro").Distinct().ToArray();
    }

    /// <summary>
    /// QUANTO A MAQUININHA COBROU NESTE TURNO SEM QUE EXISTA VENDA GRAVADA.
    ///
    /// A linha do TEF nasce ANTES da venda (a tela cobra e só depois grava), então
    /// existe uma janela em que o cartão já passou e a venda ainda não existe. Quem
    /// for afirmar ao operador que "nada foi cobrado" — o diálogo do rascunho, depois
    /// de uma queda de energia — tem que fazer ESTA conta antes: no meio dessa janela
    /// a frase é mentira, e mentira que faz o operador cobrar o cliente duas vezes.
    ///
    /// O vínculo com a venda é o NSU, NUNCA `venda_id`: essa coluna nasce NULL e
    /// nenhum caminho de produção a preenche (mesma razão explicada em
    /// <see cref="DivergenciasTef"/>). Contar por `venda_id IS NULL` transformaria
    /// toda venda de cartão do turno em alarme, e o aviso morreria de gritar à toa.
    ///
    /// Entram só as situações em que pode haver dinheiro VIVO na maquininha; recusado,
    /// cancelado, desfeito e estornado ficam de fora — ali o cliente não pagou (ou já
    /// foi devolvido). Na dúvida ('criando', 'aguardando', 'orfa', 'cnf_sem_ack',
    /// 'ncn_sem_ack') o valor ENTRA: mandar conferir à toa custa um olhar no PayGo,
    /// e calar custa uma cobrança em dobro.
    /// </summary>
    public static Dinheiro CobrancaSemVenda(SqliteConnection cx, Sessao sessao) =>
        new(cx.ExecuteScalar<long>("""
            SELECT COALESCE(SUM(t.valor_cent), 0)
              FROM tef_transacao t
             WHERE t.situacao IN ('criando','aguardando','aprovada','pago','cnf_sem_ack','ncn_sem_ack','orfa')
               AND t.criado_em >= @Desde
               AND NOT EXISTS (SELECT 1 FROM venda v WHERE v.id = t.venda_id)
               AND NOT EXISTS (
                     SELECT 1
                       FROM venda_pagamento p
                       JOIN venda v2 ON v2.id = p.venda_id
                      WHERE v2.sessao_id = @Ses
                        AND p.tef_nsu IS NOT NULL AND t.nsu IS NOT NULL
                        AND p.tef_nsu = t.nsu)
            """, new { Ses = sessao.Id, Desde = sessao.AberturaEm.ToString("o") }));

    /// <summary>
    /// O que o TEF diz que foi cobrado no turno, por forma — inclusive cobranças que
    /// NÃO viraram venda no PDV.
    ///
    /// É a conferência do cartão, e ela é automática: não se conta cartão, se compara.
    ///
    /// Toda cobrança sai DAQUI (o EXE arma o pinpad; ninguém digita valor na maquininha),
    /// então divergência não significa uso da máquina por fora. Significa que o PDV
    /// PERDEU O DESFECHO de uma cobrança que ele mesmo criou: o cliente aprovou e, antes
    /// de a venda ser gravada, faltou energia, o app morreu, ou o polling estourou os
    /// 3 minutos e desistiu enquanto a máquina já tinha aprovado. Dinheiro entrou, venda
    /// não existe — e é por isso que a linha em `tef_transacao` nasce antes da venda.
    ///
    /// Como a origem é única, o número aqui deve ser zero quase sempre. Quando não for,
    /// aponta para uma transação específica, com `payment_identifier` para estornar.
    /// </summary>
    public static List<DivergenciaTef> DivergenciasTef(SqliteConnection cx, Sessao sessao)
    {
        // Os dois lados da comparação têm que enxergar o MESMO conjunto de vendas. Como
        // Apurado deixa a venda de teste de fora, a cobrança dela sai daqui também —
        // senão o roteiro da PayGo inventaria uma divergência ("maquininha R$ 500, no
        // PDV R$ 0") a cada fechamento. Cobrança ÓRFÃ (sem venda) continua aparecendo:
        // é o alarme de dinheiro cobrado sem venda gravada, e vale no teste também.
        //
        // A exclusão da venda de teste NÃO pode passar por `t.venda_id`: essa coluna
        // nasce NULL e nenhum caminho de produção a preenche (a linha do TEF é gravada
        // ANTES de a venda existir e nunca mais é amarrada a ela). Quem amarra venda e
        // TEF na loja é o NSU — é por ele que a cobrança de teste sai daqui.
        var noTef = cx.Query("""
            SELECT t.tipo AS forma, SUM(t.valor_cent) AS total
              FROM tef_transacao t
              LEFT JOIN venda v ON v.id = t.venda_id
             WHERE t.situacao = 'pago'
               AND ((v.sessao_id = @Ses AND v.homologacao = 0)
                    OR (t.venda_id IS NULL AND t.criado_em >= @Desde
                        AND NOT EXISTS (
                            SELECT 1
                              FROM venda_pagamento p
                              JOIN venda v2 ON v2.id = p.venda_id
                             WHERE v2.sessao_id = @Ses AND v2.homologacao = 1
                               AND p.tef_nsu IS NOT NULL AND p.tef_nsu = t.nsu)))
             GROUP BY t.tipo
            """, new { Ses = sessao.Id, Desde = sessao.AberturaEm.ToString("o") })
            .ToDictionary(x => (string)x.forma, x => new Dinheiro((long)x.total));

        var naVenda = Apurado(cx, sessao);
        return noTef.Keys.Union(naVenda.Keys.Where(f => !FormasContadas(cx, sessao).Contains(f)))
            .Select(f => new DivergenciaTef(f,
                noTef.TryGetValue(f, out var t) ? t : Dinheiro.Zero,
                naVenda.TryGetValue(f, out var v) ? v : Dinheiro.Zero))
            .Where(d => d.Diferenca.Centavos != 0)
            .ToList();
    }

    /// <summary>
    /// Fecha o turno com a contagem DECLARADA pelo operador. A diferença só é
    /// calculada aqui — depois de ele declarar. Fechar é irreversível.
    ///
    /// <paramref name="contagem"/> só precisa trazer as <see cref="FormasContadas"/>.
    /// O resto fecha sozinho pelo valor do TEF.
    /// </summary>
    public static List<LinhaFechamento> Fechar(SqliteConnection cx, Sessao sessao,
        Dictionary<string, Dinheiro> contagem, Operador quemFecha,
        Dinheiro tolerancia, string? justificativa = null)
    {
        var apurado = Apurado(cx, sessao);
        var contadas = FormasContadas(cx, sessao);
        var formas = apurado.Keys.Union(contagem.Keys).ToList();
        var linhas = formas.Select(f =>
        {
            var conta = contadas.Contains(f);
            var apu = apurado.TryGetValue(f, out var aa) ? aa : Dinheiro.Zero;
            // forma automática fecha por construção: declarado = apurado, diferença zero
            var dec = conta ? (contagem.TryGetValue(f, out var dd) ? dd : Dinheiro.Zero) : apu;
            return new LinhaFechamento(f, dec, apu, conta);
        }).ToList();

        // Soma dos MÓDULOS, não o líquido. Somar com sinal deixa uma falta de R$50 no
        // dinheiro se anular com uma sobra de R$50 no crédito — e essa combinação é a
        // assinatura de venda lançada na forma errada, justamente o que precisa aparecer.
        var desvio = new Dinheiro(linhas.Sum(l => l.Diferenca.Abs.Centavos));
        if (desvio > tolerancia && string.IsNullOrWhiteSpace(justificativa))
        {
            var detalhe = string.Join("; ", linhas.Where(l => l.Diferenca.Centavos != 0)
                .Select(l => $"{l.Forma}: {l.Situacao} de {l.Diferenca.Abs.Formatado()}"));
            // "Justifique" e o marcador que a tela usa pra abrir o campo de
            // justificativa (catch por Contains) - manter a palavra ao reescrever.
            // 03/09: a tolerancia NAO vai na mensagem. Ela e regra do gestor, nao
            // informacao do operador — "tolerancia R$ 2,00" na tela do caixa ensina
            // que R$ 2,00 por dia passam sem pergunta.
            throw new InvalidOperationException(
                $"A conferência encontrou {desvio.Formatado()} de diferença ({detalhe}). " +
                "Diferença acontece em qualquer operação; descreva o que houve. Justifique para fechar.");
        }

        using var tx = cx.BeginTransaction();
        foreach (var l in linhas)
        {
            cx.Execute("""
                INSERT INTO caixa_fechamento (id, sessao_id, forma, declarado_cent, apurado_cent,
                                              diferenca_cent, justificativa, criado_em)
                VALUES (@Id, @Ses, @F, @D, @A, @Dif, @J, @Em)
                """,
                new { Id = Guid.NewGuid().ToString(), Ses = sessao.Id, F = l.Forma,
                      D = l.Declarado.Centavos, A = l.Apurado.Centavos, Dif = l.Diferenca.Centavos,
                      J = justificativa, Em = Agora }, tx);
        }
        // Canoniza como o Abrir já faz: quem fechou tem que ser a MESMA identidade que
        // abriu. Sem isto, um turno aberto depois da reconciliação (id do painel) fechava
        // com o id velho na memória da tela, e o histórico dizia que duas pessoas
        // diferentes cuidaram do mesmo caixa.
        var idFecha = Operadores.IdCanonico(cx, quemFecha.Id);
        cx.Execute("UPDATE caixa_sessao SET status='fechado', fechamento_em=@Em, fechado_por=@Por WHERE id=@Id",
            new { Em = Agora, Por = idFecha, Id = sessao.Id }, tx);
        // A auditoria registra a QUEBRA discriminada, não só o número total: "faltou 50 no
        // dinheiro e sobrou 50 no crédito" e "bateu tudo" são fatos opostos que dariam o
        // mesmo total líquido.
        var quebra = string.Join("; ", linhas.Where(l => l.Diferenca.Centavos != 0)
            .Select(l => $"{l.Forma}:{l.Situacao}:{l.Diferenca.Abs.Formatado()}"));
        Auditar(cx, tx, "caixa_fechado", idFecha, null,
            $"desvio={desvio.Formatado()}{(quebra.Length == 0 ? " (conferiu)" : " · " + quebra)}" +
            $"{(justificativa is null ? "" : " · " + justificativa)}");
        Enfileirar(cx, tx, "fechamento", sessao.Id, sessao.Id,
            new { sessao = sessao.Id, linhas = linhas.Select(l => new { l.Forma, l.Contada, l.Situacao, decl = l.Declarado.Centavos, apur = l.Apurado.Centavos, dif = l.Diferenca.Centavos }), justificativa });
        tx.Commit();
        return linhas;
    }

    /// <summary>
    /// Fecha um turno esquecido SEM contagem — só com autorização de supervisor, e só
    /// deve ser oferecido quando o operador escolhe PULAR o fechamento cego na abertura
    /// do dia seguinte. As linhas saem com declarado = apurado, mas isso NÃO significa
    /// "conferiu": significa "ninguém contou" — a justificativa marca isso em todas as
    /// linhas e a auditoria guarda quem pulou e quem autorizou. Relatório que tratar
    /// este fechamento como caixa conferido está lendo errado de propósito.
    /// </summary>
    public static void FecharSemConferencia(SqliteConnection cx, Sessao sessao,
        Operador quemPula, Operador supervisor)
    {
        var apurado = Apurado(cx, sessao);
        const string marca = "FECHADO SEM CONFERÊNCIA: caixa esquecido, contagem pulada com autorização do gerente";

        using var tx = cx.BeginTransaction();
        foreach (var (forma, valor) in apurado)
            cx.Execute("""
                INSERT INTO caixa_fechamento (id, sessao_id, forma, declarado_cent, apurado_cent,
                                              diferenca_cent, justificativa, criado_em)
                VALUES (@Id, @Ses, @F, @V, @V, 0, @J, @Em)
                """,
                new { Id = Guid.NewGuid().ToString(), Ses = sessao.Id, F = forma,
                      V = valor.Centavos, J = marca, Em = Agora }, tx);
        // Mesma canonização do Fechar: identidade única no histórico do turno.
        var idPula = Operadores.IdCanonico(cx, quemPula.Id);
        var idSuper = Operadores.IdCanonico(cx, supervisor.Id);
        cx.Execute("UPDATE caixa_sessao SET status='fechado', fechamento_em=@Em, fechado_por=@Por WHERE id=@Id",
            new { Em = Agora, Por = idPula, Id = sessao.Id }, tx);
        Auditar(cx, tx, "caixa_fechado_sem_conferencia", idPula, idSuper,
            $"dia={sessao.BusinessDate} aberto_por={sessao.OperadorNome}");
        Enfileirar(cx, tx, "fechamento", sessao.Id, sessao.Id, new
        {
            sessao = sessao.Id,
            linhas = apurado.Select(kv => new
            {
                Forma = kv.Key, Contada = false, Situacao = "sem_conferencia",
                decl = kv.Value.Centavos, apur = kv.Value.Centavos, dif = 0L,
            }),
            justificativa = marca,
        });
        tx.Commit();
    }

    public static void Auditar(SqliteConnection cx, SqliteTransaction? tx, string evento,
        string? operador, string? autorizador, string? detalhe)
        => cx.Execute("INSERT INTO auditoria (evento, operador_id, autorizador, detalhe, criado_em) VALUES (@E,@O,@A,@D,@Em)",
            new { E = evento, O = operador, A = autorizador, D = detalhe, Em = Agora }, tx);

    /// <summary>Fila de sincronização — gravada na MESMA transação do fato.</summary>
    public static void Enfileirar(SqliteConnection cx, SqliteTransaction? tx, string tipo,
        string refId, string clientKey, object payload)
        => cx.Execute("INSERT INTO outbox (tipo, ref_id, client_key, payload, criado_em) VALUES (@T,@R,@K,@P,@Em)",
            new { T = tipo, R = refId, K = clientKey, P = JsonSerializer.Serialize(payload), Em = Agora }, tx);
}
