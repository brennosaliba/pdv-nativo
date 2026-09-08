using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

public sealed record Operador(string Id, string Nome, string Perfil)
{
    public bool ESupervisor => Perfil is "supervisor" or "gerente";
}

/// <summary>
/// Um turno de caixa.
///
/// <paramref name="Teste"/> (07/09/2026): turno que o MODO DE HOMOLOGAÇÃO abriu
/// sozinho para o roteiro do TEF (ver <see cref="ModoHomologacao"/>). Ele não teve
/// abertura contada, não vai ter fechamento cego e não sobe para a nuvem. Fora do
/// modo é sempre falso, e nenhum caminho da loja o liga.
/// </summary>
public sealed record Sessao(
    string Id, string BusinessDate, string OperadorId, string OperadorNome,
    DateTime AberturaEm, Dinheiro FundoTroco, bool Teste = false);

/// <summary>
/// Uma forma de pagamento no fechamento.
///
/// <paramref name="PeloTef"/> é a parte que a maquininha liquidou. Ela NÃO se declara:
/// entra no declarado por construção, e é isso que impede a conta de nascer torta.
/// O operador só responde pelo resto (<see cref="AContar"/>): o dinheiro da gaveta e o
/// cartão que passou fora do TEF.
///
/// <paramref name="Conferida"/> separa "bateu" de "ninguém olhou". Linha não conferida
/// fecha pelo valor apurado e fica FORA do desvio: inventar falta do tamanho do que não
/// foi perguntado é pior que admitir que não se conferiu.
/// </summary>
public sealed record LinhaFechamento(string Forma, Dinheiro Declarado, Dinheiro Apurado,
    bool Contada = true, Dinheiro PeloTef = default, bool Conferida = true)
{
    public Dinheiro Diferenca => Declarado - Apurado;

    /// <summary>O que o operador respondeu por: o apurado menos a parte do TEF.</summary>
    public Dinheiro AContar => Apurado - PeloTef;

    /// <summary>
    /// A diferença que PODE virar sobra ou falta: a da parte que alguém contou.
    ///
    /// O critério é <see cref="Contada"/>, não <see cref="Conferida"/>. Linha automática
    /// fecha com declarado = apurado, então já vale zero aqui, e TEF fora do ar não
    /// fabrica desvio. Mas quando o operador conta a maquininha AVULSA e o TEF está
    /// mudo, a linha fica sem conferência e mesmo assim a falta da avulsa é real: ela
    /// foi contada. Zerar por `Conferida` engoliria essa falta.
    /// </summary>
    public Dinheiro DiferencaConferida => Contada ? Diferenca : Dinheiro.Zero;

    /// <summary>
    /// Sobra não é "boa notícia". Falta pode ser troco errado; sobra costuma ser venda
    /// que entrou na gaveta sem passar pelo PDV — o que também some do estoque e da
    /// nota fiscal. As duas precisam de explicação, e por motivos diferentes.
    ///
    /// `sem_conferencia` é a MESMA palavra que o painel já entende (o fechamento de
    /// caixa esquecido usa ela desde sempre), então a nuvem lê a linha nova sem mudar.
    /// </summary>
    public string Situacao => Diferenca.Centavos switch
    {
        > 0 => "sobra",
        < 0 => "falta",
        // Bateu. Só é "confere" se alguém comparou com a fonte de fora; senão o valor
        // é o que o sistema registrou e ninguém olhou.
        _ => Conferida ? "confere" : "sem_conferencia",
    };
}

/// <summary>
/// O que a tela de fechamento tem que PERGUNTAR, forma por forma. Sai pronto do Núcleo
/// para a tela do caixa e a tela do caixa esquecido não divergirem: regra em dois
/// lugares vira duas regras no dia seguinte.
/// </summary>
public sealed record ConferenciaForma(string Forma, Dinheiro Apurado, Dinheiro PeloTef, bool Conta)
{
    /// <summary>O valor que o operador confere. Zero quando o TEF liquidou tudo.</summary>
    public Dinheiro AContar => Apurado - PeloTef;
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
            "SELECT id, business_date, operador_id, operador_nome, abertura_em, fundo_troco_cent, homologacao " +
            "FROM caixa_sessao WHERE status = 'aberto' LIMIT 1");
        if (r is null) return null;
        return new Sessao((string)r.id, (string)r.business_date, (string)r.operador_id,
            (string)r.operador_nome, DateTime.Parse((string)r.abertura_em),
            new Dinheiro((long)r.fundo_troco_cent), (long)r.homologacao == 1);
    }

    /// <summary>
    /// Abre o turno. Recusa se já houver caixa aberto — inclusive de OUTRO DIA, que é o
    /// caso que mais estraga fechamento (as vendas de dois dias caem no mesmo turno e
    /// depois não dá pra separar).
    /// </summary>
    public static Sessao Abrir(SqliteConnection cx, Operador operador, Dinheiro fundoTroco)
    {
        if (fundoTroco.Centavos < 0) throw new InvalidOperationException("Fundo de troco não pode ser negativo.");

        // O OPERADOR DE TESTE NÃO ABRE CAIXA DE VERDADE (07/09/2026).
        //
        // Como ele chegaria aqui: o modo de homologação é desligado com o PDV JÁ ABERTO
        // no turno de teste. A casca encerra a sobra do teste, a entrada direta deixa de
        // valer, e quem ficou na mão dela é o operador de teste. Sem esta linha, a tela
        // de abertura viria com "Teste de homologação" no alto e o dia inteiro da loja
        // sairia assinado por ele: turno na nuvem com um id que o painel não conhece
        // (409 até virar dead-letter) e um fechamento com dono que não é gente.
        //
        // A regra mora AQUI, e não só na casca, porque é aqui que a sessão nasce, é
        // enfileirada e ganha assinatura. Defesa que só existe na tela é decoração.
        if (ModoHomologacao.EhOperadorDeTeste(operador.Id))
            throw new InvalidOperationException(ModoHomologacao.NaoAbreCaixa);

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
    /// Pagamento INTEGRADO (aprovado pela maquininha do TEF), em SQL sobre `venda_pagamento p`:
    /// tem carimbo, cAut ou NSU. É a MESMA regra de <see cref="PagamentoVenda.Integrado"/>;
    /// o contrário é o POS avulso (cartão passado fora do TEF, sem linha em `tef_transacao`).
    /// Uma definição só para os dois lados, senão a tela e o fechamento discordam.
    /// </summary>
    public const string SqlIntegrado = "(p.tef_aut IS NOT NULL OR p.tef_nsu IS NOT NULL)";

    /// <summary>
    /// Versão que olha o TURNO, não só a configuração. Mesmo com TEF ligado, uma venda
    /// pode ter saído como POS avulsa (botão POS da grade, ou o fallback de quando o TEF
    /// falha na hora) — e o pagamento manual não tem carimbo do TEF gravado. Nesse caso a
    /// forma volta a ser CONTADA: o operador soma o fechamento das maquininhas (a
    /// integrada e a avulsa) e confere contra o PDV, que tem as duas.
    /// </summary>
    public static string[] FormasContadas(SqliteConnection cx, Sessao sessao)
    {
        var basicas = FormasContadas(cx);
        if (basicas.Length > 1) return basicas;            // sem TEF: já conta tudo

        // Venda de teste não conta aqui pelo mesmo motivo de Apurado: ela não está no
        // fechamento da maquininha, então não pode puxar uma forma de volta para a
        // contagem — o operador digitaria o total da máquina e sobraria a diferença.
        var manuais = cx.Query<string>($"""
            SELECT DISTINCT p.forma
              FROM venda_pagamento p JOIN venda v ON v.id = p.venda_id
             WHERE v.sessao_id = @S AND v.status = 'finalizada' AND v.homologacao = 0
               AND p.forma <> 'dinheiro' AND NOT {SqlIntegrado}
            """, new { S = sessao.Id }).ToList();
        return manuais.Count == 0 ? basicas : manuais.Prepend("dinheiro").Distinct().ToArray();
    }

    /// <summary>
    /// O ROTEIRO do fechamento: para cada forma, quanto o TEF já liquidou, quanto sobra
    /// para o operador conferir, e se a tela deve mesmo perguntar.
    ///
    /// Existe por causa de um buraco real: quando UMA venda saía como POS avulsa, a forma
    /// inteira voltava para a contagem e o operador era obrigado a digitar um total que
    /// incluía o cartão do TEF — que ele não tem como contar. Ele digitava o que a
    /// maquininha avulsa mostrava e o fechamento acusava uma falta do tamanho exato do
    /// TEF (R$ 3.107,46 num dia). Não era falta nenhuma: era pergunta errada.
    ///
    /// A regra agora: cartão não se declara, o valor vem do TEF. Pergunta-se só o
    /// dinheiro (está na gaveta) e a parte que passou FORA do TEF (está no fechamento da
    /// maquininha avulsa, fonte independente que continua valendo a pena conferir).
    /// </summary>
    public static List<ConferenciaForma> PlanoDeConferencia(SqliteConnection cx, Sessao sessao)
    {
        var apurado = Apurado(cx, sessao);
        var integrado = ApuradoIntegrado(cx, sessao);
        var contadas = FormasContadas(cx, sessao);
        // As formas CONTADAS entram no roteiro mesmo sem venda no PDV. É o dia em que a
        // maquininha avulsa vendeu e nada foi registrado: o apurado dela é zero, e se a
        // pergunta sumir some junto o único jeito de enxergar a venda que não entrou.
        return apurado.Keys.Union(integrado.Keys).Union(contadas)
            .OrderBy(f => f == "dinheiro" ? 0 : 1).ThenBy(f => f, StringComparer.Ordinal)
            .Select(f => Montar(f,
                apurado.TryGetValue(f, out var a) ? a : Dinheiro.Zero,
                integrado, contadas))
            .ToList();
    }

    /// <summary>
    /// A decisão de uma linha, usada pelo roteiro da tela E pelo <see cref="Fechar"/>.
    /// Uma cópia só: se a tela decidir uma coisa e o fechamento outra, volta a nascer a
    /// falta inventada.
    /// </summary>
    private static ConferenciaForma Montar(string forma, Dinheiro apurado,
        Dictionary<string, Dinheiro> integrado, string[] contadas)
    {
        // Dinheiro nunca tem parte de TEF (pagamento em dinheiro não carrega carimbo), e
        // o apurado dele ainda leva fundo, sangria e suprimento — que também se contam.
        var peloTef = forma == "dinheiro" || !integrado.TryGetValue(forma, out var t)
            ? Dinheiro.Zero : t;
        var aContar = apurado - peloTef;
        // Só entra na pergunta o que o operador consegue conferir. Sai da pergunta APENAS
        // a forma que o TEF liquidou inteira (peloTef > 0 e nada sobrando fora dele) —
        // essa ele não tem como contar. Forma sem TEF nenhum continua sendo perguntada
        // mesmo com apurado zero: é assim que a maquininha avulsa que vendeu sem registro
        // aparece como sobra, e era esse o motivo de contar cartão em caixa sem TEF.
        var conta = contadas.Contains(forma) && (peloTef.Centavos == 0 || aContar.Centavos != 0);
        return new ConferenciaForma(forma, apurado, peloTef, conta);
    }

    /// <summary>
    /// O que as vendas do turno receberam PELA MAQUININHA INTEGRADA, por forma. É o único
    /// lado do PDV que pode ser comparado com `tef_transacao`: o POS avulso passou em outra
    /// máquina (ou na mesma, à mão) e não deixa linha no TEF — somá-lo aqui inventaria
    /// divergência quando o TEF está certo e, pior, ESCONDERIA um cartão TEF que sumiu
    /// (R$ 100 órfãos no TEF "batem" com R$ 100 de POS no PDV, e ninguém confere).
    /// </summary>
    public static Dictionary<string, Dinheiro> ApuradoIntegrado(SqliteConnection cx, Sessao sessao)
    {
        var r = new Dictionary<string, Dinheiro>();
        var pagos = cx.Query($"""
            SELECT p.forma, SUM(p.valor_cent - p.troco_cent) AS total
              FROM venda_pagamento p JOIN venda v ON v.id = p.venda_id
             WHERE v.sessao_id = @Ses AND v.status = 'finalizada' AND v.homologacao = 0
               AND {SqlIntegrado}
             GROUP BY p.forma
            """, new { Ses = sessao.Id });
        foreach (var p in pagos) r[(string)p.forma] = new Dinheiro((long)p.total);
        return r;
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

        // Do lado do PDV entra SÓ o que foi integrado (04/09). Antes entrava o Apurado
        // inteiro e a forma era filtrada quando "contada": com POS avulso no turno, a
        // forma virava contada e sumia da comparação — inclusive o cartão TEF que ficou
        // sem transação — ou, quando o TEF também tinha aquela forma, o POS aparecia
        // como divergência falsa. Sem filtro por forma contada: o integrado é sempre
        // comparável com o TEF, haja ou não POS na mesma forma.
        var naVenda = ApuradoIntegrado(cx, sessao);
        return noTef.Keys.Union(naVenda.Keys)
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
    /// <paramref name="contagem"/> traz SÓ o que o operador contou de fato: o dinheiro da
    /// gaveta e, quando houver, a parte que passou fora do TEF (ver
    /// <see cref="PlanoDeConferencia"/>). O cartão do TEF entra sozinho, pelo valor
    /// apurado — não se declara cartão.
    ///
    /// <paramref name="tefDisponivel"/> é o TEF respondendo NA HORA de fechar. Fora do ar,
    /// a linha do cartão fecha pelo apurado mas vai marcada como não conferida: o caixa
    /// fecha do mesmo jeito e o desvio não engorda com um número que ninguém checou.
    /// </summary>
    public static List<LinhaFechamento> Fechar(SqliteConnection cx, Sessao sessao,
        Dictionary<string, Dinheiro> contagem, Operador quemFecha,
        Dinheiro tolerancia, string? justificativa = null, bool tefDisponivel = true)
    {
        ExigirAberto(cx, sessao);
        var apurado = Apurado(cx, sessao);
        var integrado = ApuradoIntegrado(cx, sessao);
        var contadas = FormasContadas(cx, sessao);
        var formas = apurado.Keys.Union(contagem.Keys).ToList();
        var linhas = formas.Select(f =>
        {
            var apu = apurado.TryGetValue(f, out var aa) ? aa : Dinheiro.Zero;
            var plano = Montar(f, apu, integrado, contadas);
            // O operador contou: o declarado é a parte do TEF (que não se declara) MAIS o
            // que ele contou. É aqui que a falta inventada morre — a parcela do TEF entra
            // dos dois lados da subtração e some.
            if (contadas.Contains(f) && contagem.TryGetValue(f, out var contadoAMao))
            {
                // O operador contou a parte de FORA do TEF. A parte da maquininha só se
                // dá por conferida se ela respondeu: com o TEF mudo a tela avisa que o
                // cartão fica sem conferência, e o registro tem que dizer a mesma coisa.
                var conf = plano.PeloTef.Centavos == 0 || tefDisponivel;
                return new LinhaFechamento(f, plano.PeloTef + contadoAMao, apu, true, plano.PeloTef, conf);
            }

            // Ninguém contou esta forma. Fecha pelo apurado (diferença zero, nunca uma
            // falta do tamanho da pergunta que não foi feita) e só se chama CONFERIDA
            // quando não havia nada a contar e o TEF respondeu.
            var conferida = plano.AContar.Centavos == 0
                            && (plano.PeloTef.Centavos == 0 || tefDisponivel);
            return new LinhaFechamento(f, apu, apu, false, plano.PeloTef, conferida);
        }).ToList();

        // Soma dos MÓDULOS, não o líquido. Somar com sinal deixa uma falta de R$50 no
        // dinheiro se anular com uma sobra de R$50 no crédito — e essa combinação é a
        // assinatura de venda lançada na forma errada, justamente o que precisa aparecer.
        //
        // E soma SÓ o que foi conferido: linha sem conferência não vira sobra nem falta.
        var desvio = new Dinheiro(linhas.Sum(l => l.DiferencaConferida.Abs.Centavos));
        if (desvio > tolerancia && string.IsNullOrWhiteSpace(justificativa))
        {
            var detalhe = string.Join("; ", linhas.Where(l => l.DiferencaConferida.Centavos != 0)
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
        //
        // A forma que ficou SEM conferência aparece aqui também. Ela não entra no desvio,
        // e é exatamente por isso que precisa de rastro: fechamento com desvio zero e
        // cartão não conferido não é a mesma coisa que fechamento que bateu.
        var quebra = string.Join("; ", linhas.Where(l => l.Diferenca.Centavos != 0 || !l.Conferida)
            .Select(l => $"{l.Forma}:{l.Situacao}:{l.Diferenca.Abs.Formatado()}"));
        Auditar(cx, tx, "caixa_fechado", idFecha, null,
            $"desvio={desvio.Formatado()}{(quebra.Length == 0 ? " (conferiu)" : " · " + quebra)}" +
            $"{(justificativa is null ? "" : " · " + justificativa)}");
        Enfileirar(cx, tx, "fechamento", sessao.Id, sessao.Id,
            new { sessao = sessao.Id, linhas = linhas.Select(l => new { l.Forma, l.Contada, l.Situacao, decl = l.Declarado.Centavos, apur = l.Apurado.Centavos, dif = l.Diferenca.Centavos, tef = l.PeloTef.Centavos, l.Conferida }), justificativa });
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
        ExigirAberto(cx, sessao);
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

    /// <summary>
    /// Marcador da recusa de fechar turno já fechado. A tela procura por ele para dizer
    /// a frase certa (o caixa NÃO continua aberto), do mesmo jeito que procura
    /// "Justifique" para abrir o campo da justificativa.
    /// </summary>
    public const string MarcaJaFechado = "já foi fechado";

    /// <summary>
    /// Fechar é irreversível E acontece uma vez só. Sem esta trava, chamar
    /// <see cref="Fechar"/> de novo no mesmo turno gravava OUTRO jogo de linhas em
    /// `caixa_fechamento` e OUTRO item na fila: dois fechamentos para uma sessão, com
    /// contagens diferentes, e o fundo esperado do dia seguinte saindo de um deles ao
    /// acaso. A tela de venda espera pelo TEF antes de perguntar qualquer coisa, e um
    /// segundo toque no botão nessa espera chegava aqui.
    /// </summary>
    private static void ExigirAberto(SqliteConnection cx, Sessao sessao)
    {
        var status = cx.ExecuteScalar<string?>(
            "SELECT status FROM caixa_sessao WHERE id = @Id", new { Id = sessao.Id });
        if (status is not null && status != "aberto")
            throw new InvalidOperationException(
                $"Este caixa {MarcaJaFechado}. O resultado está no relatório do turno.");
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
