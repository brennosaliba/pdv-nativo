using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>
/// O CAIXA DE HOMOLOGAÇÃO (config `homologacao` = 1): login e turno fora do caminho.
///
/// O QUE ISTO RESOLVE. O roteiro do TEF são 58 passos e o dono repete a mesma venda
/// mais de vinte vezes seguidas. A cada volta o caixa pedia login de operador,
/// abertura de caixa com contagem da gaveta e, no fim do dia, fechamento cego. Nenhum
/// dos 58 passos fala de caixa ou de operador: essas três telas não provam nada no
/// roteiro e só cobram tempo de quem está gravando a evidência.
///
/// Com o modo ligado o caixa entra sozinho, com um operador que se chama
/// <see cref="NomeOperador"/> em toda tela e em toda linha de auditoria, e vende num
/// TURNO DE TESTE que ele mesmo abre. Sem contagem na abertura, sem fechamento, sem
/// nada disso subindo para a nuvem.
///
/// ⚠️ NADA DISSO VALE NA LOJA, e a regra mora aqui, não na tela. Com a config
/// desligada toda função deste arquivo devolve null ou zero: o caixa continua pedindo
/// login, abertura e fechamento exatamente como pede hoje. Caixa de loja sem login e
/// sem fechamento não é comodidade, é auditoria impossível e dinheiro sem dono.
///
/// AS TRÊS GUARDAS QUE IMPEDEM O VAZAMENTO PARA A LOJA:
///  1. o operador de teste nasce INATIVO e sem senha que exista (pin_hash vazio):
///     ninguém entra com ele pelo login, ele não conta em <see cref="Operadores.ExisteAlgum"/>,
///     não aparece em <see cref="Operadores.PrimeiroAtivo"/> e o piso da sincronização
///     (Nuvem.BaixarOperadoresAsync, que só reergue quem TEM senha) nunca o escolhe;
///  2. o turno de teste é MARCADO no banco (`caixa_sessao.homologacao`), e é essa
///     marca, não a config do momento, que autoriza encerrá-lo sem contagem;
///  3. havendo turno de VERDADE aberto, este arquivo sai de cena e devolve null: o
///     caixa volta ao caminho normal, com abertura e fechamento. Dinheiro de gente
///     manda em roteiro de teste.
///
/// O que já existia e continua valendo: a venda nasce com `venda.homologacao` = 1, não
/// é enfileirada para a nuvem, fica fora do apurado e do fechamento do turno
/// (<see cref="Caixa.Apurado"/>) e sai como recibo em vez de NFC-e.
/// </summary>
public static class ModoHomologacao
{
    /// <summary>
    /// Id do operador de teste. Sublinhados nas pontas, igual ao `_admin_`: é linha de
    /// sistema, não gente, e nenhum cadastro do painel nasce com este formato.
    /// </summary>
    public const string IdOperador = "_homologacao_";

    /// <summary>
    /// O nome que aparece no topo do caixa e em cada linha de auditoria. Diz o que é
    /// na própria palavra: quem abrir o histórico depois não confunde a venda do
    /// roteiro com venda de gente.
    /// </summary>
    public const string NomeOperador = "Teste de homologação";

    /// <summary>Faixa fixa no alto da janela, em toda tela, o tempo todo.</summary>
    public const string TituloFaixa = "MODO DE HOMOLOGAÇÃO";

    /// <summary>
    /// A segunda linha da faixa. Sem ela, um caixa que não pede login e não fecha é um
    /// caixa que alguém usa de verdade sem perceber.
    /// </summary>
    public const string DetalheFaixa = "Venda de teste. Não entra no caixa, na nota nem na nuvem.";

    /// <summary>
    /// O modo está ligado mas um turno de VERDADE está aberto, então o caixa continua
    /// pedindo login e fechamento (ver <see cref="EntradaDireta"/>). Sem esta frase, a
    /// faixa diria "modo de homologação" e o caixa pediria login mesmo assim: quem
    /// está com o roteiro na mão acharia que a mudança não funcionou.
    /// </summary>
    public const string DetalheBloqueado = "Um caixa de verdade está aberto. Feche esse caixa uma vez e o login sai do caminho.";

    /// <summary>Onde a tela de venda mostraria "Caixa aberto às 14:30".</summary>
    public const string LinhaDoTurno = "Turno de teste, sem abertura e sem fechamento de caixa.";

    /// <summary>O interruptor. Uma chave só, a mesma que o resto do caixa já lê.</summary>
    public static bool Ligado(SqliteConnection cx) => Vendas.Homologacao(cx);

    /// <summary>
    /// Este é o operador de teste? Pergunta pelo id, e não pelo nome, porque o nome é
    /// texto de tela e o id é a identidade.
    ///
    /// NÃO OLHA A CONFIG DE PROPÓSITO. Quem responde isto precisa acertar justamente no
    /// instante em que a config já mudou e o operador de teste ainda está na mão de
    /// quem está operando: é aí que ele vazaria para o caixa da loja.
    /// </summary>
    public static bool EhOperadorDeTeste(string? operadorId)
        => string.Equals(operadorId, IdOperador, StringComparison.Ordinal);

    /// <summary>
    /// O aviso que o operador de teste recebe quando tenta abrir caixa de verdade. Diz o
    /// que fazer, não o que aconteceu.
    /// </summary>
    public const string NaoAbreCaixa =
        "Este é o operador de teste da homologação e ele não abre caixa. " +
        "Entre com o seu login para abrir o caixa da loja.";

    /// <summary>
    /// Modo ligado, mas com turno de gente aberto: a entrada direta não vale e o caixa
    /// segue pedindo login e fechamento. É o que a faixa da janela precisa dizer.
    /// </summary>
    public static bool BloqueadoPorTurnoDeVerdade(SqliteConnection cx)
        => Ligado(cx) && Caixa.SessaoAberta(cx) is { Teste: false };

    /// <summary>
    /// Quem opera o caixa de teste. Criado na primeira vez e reaproveitado depois: o id
    /// é fixo porque `venda.operador_id`, `caixa_sessao.operador_id` e a auditoria
    /// apontam para ele, e um id novo a cada boot espalharia o mesmo roteiro por vários
    /// "operadores" no histórico.
    ///
    /// NASCE INATIVO E SEM SENHA, de propósito (as guardas 1 do cabeçalho). A linha
    /// existe para a chave estrangeira e para o nome aparecer; ela não é um acesso.
    /// </summary>
    public static Operador OperadorDeTeste(SqliteConnection cx)
    {
        cx.Execute("""
            INSERT INTO operador (id, nome, pin_hash, pin_salt, perfil, ativo, atualizado)
            VALUES (@Id, @Nome, '', '', 'operador', 0, @Em)
            ON CONFLICT(id) DO UPDATE SET nome = @Nome, pin_hash = '', pin_salt = '', ativo = 0
            """,
            new { Id = IdOperador, Nome = NomeOperador, Em = DateTime.Now.ToString("o") });
        return new Operador(IdOperador, NomeOperador, "operador");
    }

    /// <summary>
    /// A ENTRADA DIRETA: quem entra e em que turno, sem passar pelo login nem pela
    /// abertura de caixa. É o único ponto que a MainWindow chama.
    ///
    /// Devolve null quando o caixa tem que seguir o caminho normal, e são dois casos:
    /// o modo está DESLIGADO (a loja inteira), ou existe um turno de VERDADE aberto
    /// nesta máquina. No segundo caso o certo é a tela de sempre, que mostra de quem é
    /// o turno e manda fechar: encerrar por conta própria um turno com dinheiro de
    /// gente dentro, para o teste rodar mais rápido, seria trocar auditoria por pressa.
    /// </summary>
    public static (Operador Operador, Sessao Sessao)? EntradaDireta(SqliteConnection cx)
    {
        if (!Ligado(cx)) return null;

        var aberta = Caixa.SessaoAberta(cx);
        if (aberta is not null && !aberta.Teste) return null;   // turno de verdade: sai de cena

        var operador = OperadorDeTeste(cx);
        if (aberta is not null)
        {
            // Turno de teste do dia: continua nele. Assim o roteiro inteiro cabe num
            // turno só e a auditoria do dia não vira uma sessão por venda.
            if (aberta.BusinessDate == Caixa.DiaOperacional()) return (operador, aberta);
            // Virou o dia com o teste aberto. O turno de ontem se encerra aqui mesmo,
            // sem perguntar nada: não há gaveta para contar. Só o de TESTE, nunca outro.
            EncerrarTurnoDeTeste(cx, aberta, "virou o dia operacional");
        }
        return (operador, AbrirTurnoDeTeste(cx, operador));
    }

    /// <summary>
    /// O modo saiu do ar e ficou um turno de teste aberto. Ele não pode continuar de pé:
    /// o índice único deixa UM caixa aberto por vez neste terminal, e a loja abriria
    /// amanhã esbarrando num turno que ninguém abriu e que não tem gaveta para contar.
    ///
    /// Só encerra turno de TESTE. Com o modo ligado, ou com turno de verdade aberto,
    /// não faz nada.
    /// </summary>
    /// <returns>1 se encerrou o turno de teste, 0 se não havia o que encerrar.</returns>
    public static int EncerrarSobras(SqliteConnection cx)
    {
        if (Ligado(cx)) return 0;
        var aberta = Caixa.SessaoAberta(cx);
        if (aberta is null || !aberta.Teste) return 0;
        EncerrarTurnoDeTeste(cx, aberta, "modo de homologação desligado");
        return 1;
    }

    /// <summary>
    /// Abre o turno de teste. Não é <see cref="Caixa.Abrir"/> por dois motivos que
    /// importam: ali o fundo de troco é uma DECLARAÇÃO de quem assume a custódia da
    /// gaveta (aqui não há gaveta nem custódia), e ali a sessão vai para a fila da
    /// nuvem (aqui o painel receberia um turno que nunca existiu, com o nome de um
    /// operador que o ERP não conhece, e devolveria 409 até virar dead-letter).
    ///
    /// Fundo zero e nada de <see cref="Caixa.Enfileirar"/>. O rastro fica na auditoria
    /// local, que é onde ele serve.
    /// </summary>
    private static Sessao AbrirTurnoDeTeste(SqliteConnection cx, Operador operador)
    {
        var agora = DateTime.Now;
        var sessao = new Sessao(Guid.NewGuid().ToString(), Caixa.DiaOperacional(), operador.Id,
            operador.Nome, agora, Dinheiro.Zero, Teste: true);

        using var tx = cx.BeginTransaction();
        cx.Execute("""
            INSERT INTO caixa_sessao (id, business_date, operador_id, operador_nome,
                                      abertura_em, fundo_troco_cent, status, homologacao)
            VALUES (@Id, @Bd, @Op, @Nome, @Ab, 0, 'aberto', 1)
            """,
            new { Id = sessao.Id, Bd = sessao.BusinessDate, Op = operador.Id, Nome = operador.Nome,
                  Ab = agora.ToString("o") }, tx);
        Auditar(cx, tx, "caixa_teste_aberto", sessao.BusinessDate,
            "aberto sozinho pelo modo de homologação, fundo zero, sem contagem e fora da nuvem");
        tx.Commit();
        return sessao;
    }

    /// <summary>
    /// Encerra o turno de teste sem contagem e sem fechamento.
    ///
    /// NÃO grava linha em `caixa_fechamento`, e isso é de propósito: é de lá que sai o
    /// fundo esperado da próxima abertura (<see cref="Caixa.FundoEsperado"/>). Um
    /// fechamento de teste com dinheiro zero faria o caixa da loja acusar diferença no
    /// dia seguinte. Também não enfileira nada para a nuvem, pelo mesmo motivo da
    /// abertura.
    ///
    /// O `AND homologacao = 1` no UPDATE é o cinto: mesmo chamado errado, esta função
    /// não tem como encostar num turno de verdade.
    /// </summary>
    private static void EncerrarTurnoDeTeste(SqliteConnection cx, Sessao sessao, string porque)
    {
        using var tx = cx.BeginTransaction();
        var n = cx.Execute("""
            UPDATE caixa_sessao SET status = 'fechado', fechamento_em = @Em, fechado_por = @Por
             WHERE id = @Id AND homologacao = 1
            """,
            new { Em = DateTime.Now.ToString("o"), Por = IdOperador, Id = sessao.Id }, tx);
        if (n > 0)
            Auditar(cx, tx, "caixa_teste_encerrado", sessao.BusinessDate,
                $"{porque}: turno de teste encerrado sem contagem (só teve venda de teste)");
        tx.Commit();
    }

    private static void Auditar(SqliteConnection cx, SqliteTransaction tx, string evento,
        string dia, string detalhe)
        => Caixa.Auditar(cx, tx, evento, IdOperador, null, $"{NomeOperador} · dia {dia} · {detalhe}");
}
