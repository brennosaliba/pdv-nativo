using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>O que a outbox diz de um lote (nunca gravado: derivado da linha kds_cozinha do lote).</summary>
public enum EstadoLote
{
    /// <summary>Sem linha na outbox: lote de homologacao ou turno de teste (nada foi para a TV de verdade).</summary>
    Teste,
    /// <summary>Esperando a rede ou a chave: ainda nao chegou a TV.</summary>
    NaFila,
    /// <summary>O servidor do KDS confirmou: esta na TV.</summary>
    NaTv,
    /// <summary>Descartado, desistido ou chave recusada: para o operador e sempre "nao foi".</summary>
    NaoFoi,
}

/// <summary>Um item de um lote: exatamente o que foi pedido NESTE toque de "Pedir a cozinha".</summary>
public sealed record ItemDoLote(string LinhaId, string Nome, long QtdMilesimos, string Unidade, string? EscolhasJson, bool Brinde);

/// <summary>O que foi tirado de um lote com PIN enquanto ele ainda estava na fila.</summary>
public sealed record RetiradaDoLote(string LinhaId, long QtdMilesimos);

/// <summary>Um "Pedir a cozinha" de uma conta: imutavel, com id "&lt;contaId&gt;-&lt;n&gt;" (o vendaId do POST).</summary>
public sealed record ContaLote(string Id, string ContaId, int N, string Consumo, IReadOnlyList<ItemDoLote> Itens,
    IReadOnlyList<RetiradaDoLote> Retirados, string? SubstituidoPor, bool Homologacao, string OperadorId, DateTime CriadoEm)
{
    public DestinoCozinha Destino => Consumo == "viagem" ? DestinoCozinha.Viagem : DestinoCozinha.Loja;
}

/// <summary>A conta por mesa ou cliente ("comanda" na tela), como ela vive no disco.</summary>
public sealed record ContaAberta(
    string Id, long Numero, string? Nome, string Consumo, string Status,
    IReadOnlyList<ItemRascunho> Itens, string? CortesiaCodigo, IReadOnlyDictionary<string, int> CortesiaCobertura,
    string OperadorId, string SessaoAbertura, string BusinessDate, bool Teste,
    DateTime AbertaEm, DateTime AtualizadoEm, DateTime UltimoToqueEm, DateTime? FechandoEm,
    DateTime? FechadaEm, string? VendaId,
    DateTime? CanceladaEm, string? CancelMotivo, string? CancelOperador, string? CancelAutorizador, bool CancelAutomatico,
    string? Dono)
{
    public bool Aberta => Status == "aberta";
    public DestinoCozinha Destino => Consumo == "viagem" ? DestinoCozinha.Viagem : DestinoCozinha.Loja;
    /// <summary>Preco de tabela x quantidade de cada linha (o que a auditoria e o papel mostram; nao e o total cobrado).</summary>
    public Dinheiro ValorTabela => ContasAbertas.ValorTabela(Itens);
    /// <summary>"12 · Mesa 5", ou so "12" sem nome: o titulo da coluna e da linha da lista.</summary>
    public string Titulo => string.IsNullOrWhiteSpace(Nome) ? Numero.ToString(CultureInfo.InvariantCulture) : $"{Numero} · {Nome}";
}

/// <summary>O que o Pagamento passa a Vendas.Finalizar para fechar a conta como venda.</summary>
/// <param name="DestinoDosPendentes">
/// O que o operador escolheu no bloco "Para a cozinha" para os itens que AINDA NAO foram pedidos:
/// Loja ou Viagem = lote n+1 na mesma transacao; null ("Nao") = sao so vendidos (vitrine, geladeira).
/// Os itens ja pedidos nunca vao de novo, qualquer que seja a escolha.
/// </param>
public sealed record ContaParaFechar(string Id, DestinoCozinha? DestinoDosPendentes);

/// <summary>Uma linha da conta e quanto dela ainda nao foi para a cozinha.</summary>
public sealed record PendenteDaConta(ItemRascunho Linha, long Milesimos);

/// <summary>O desfecho de FecharNaVenda: fechou agora, ou ja estava fechada com esta venda (devolve a existente).</summary>
public sealed record FechamentoDaConta(bool Fechou, VendaGravada? VendaExistente);

/// <summary>As duas linhas do fechamento do caixa: o que foi cancelado no turno e o que ficou aberto.</summary>
public sealed record ResumoContas(int CanceladasN, Dinheiro Canceladas, int AbertasN, Dinheiro Abertas);

/// <summary>
/// Uma linha do dialogo Comandas, ja com os textos que a tela mostra: "12 · Mesa 5" | "3 itens · R$ 45,00" |
/// "cozinha 2 de 3" | selo. O selo e UM so, nesta ordem: na tela, em uso no outro caixa, confira a maquininha,
/// o estado do ultimo lote (na TV, fila, nao foi). <see cref="LoteParaMandarDeNovo"/> e o lote que o botao
/// [Mandar de novo] reenvia (so com "nao foi" e sem substituto).
/// </summary>
public sealed record LinhaDaLista(ContaAberta Conta, string Titulo, string Itens, string Cozinha, string Selo,
    bool NaTela, bool EmUso, bool ConferirMaquininha, string? LoteParaMandarDeNovo);

/// <summary>
/// COMANDA ABERTA (13/10/2026). Pedido do dono: "agora vamos adicionar a funcao comanda no PDV, abre a
/// comanda e fecha pra pagamento.. e a comanda tambem permite solicitar itens da cozinha!"
///
/// NOMES. Aqui, no banco (conta_aberta, conta_lote) e na auditoria a entidade e a CONTA ABERTA: a
/// palavra "comanda" ja e o carrinho da tela (ItemComanda, comanda_rascunho, "Voltar para a comanda")
/// e o papel da cozinha do delivery (SeletorComanda). Na TELA ela se chama comanda, como o dono fala:
/// os textos estao em <see cref="Textos"/>.
///
/// O QUE ELA E: uma conta por mesa ou cliente, com numero curto do dia, que recebe itens ao longo do
/// atendimento, pede a cozinha por LOTE e no fim vira UMA venda pelo caminho de sempre
/// (<see cref="Vendas.Finalizar"/> com <see cref="ContaParaFechar"/>). Varias abertas ao mesmo tempo.
///
/// O QUE ELA NAO E:
///  · NAO E DINHEIRO. Nada aqui entra no apurado, na nota ou na nuvem como venda. A venda nasce no
///    fechamento, uma vez, no turno de quem receber.
///  · NAO E DO TURNO. Ao contrario do rascunho (uma linha, presa a sessao, apagada por outra sessao),
///    a conta sobrevive ao reinicio e a troca de turno. O fechamento do caixa AVISA e deixa passar.
///  · NAO E O RASCUNHO. Com uma conta carregada a tela grava NELA e apaga comanda_rascunho: senao a
///    queda de energia devolveria os mesmos itens duas vezes (rascunho solto E conta), e o operador
///    cobraria o cliente duas vezes.
///
/// A COZINHA PRODUZ ANTES DO DINHEIRO, de proposito. O que compensa: o lote e imutavel e so leva o que
/// ainda nao foi pedido (<see cref="Pendentes"/>); tirar item ja pedido exige PIN de OUTRO gerente
/// (<see cref="RetirarProduzido"/>); cancelar conta com item exige motivo e PIN de outro gerente
/// (<see cref="Cancelar"/>); e tudo isso sobe para o ERP pela outbox <see cref="TipoEvento"/>.
///
/// FECHAR E UMA VENDA, UMA VEZ. <see cref="FecharNaVenda"/> roda PRIMEIRO na transacao do Finalizar e e
/// idempotente: a conta ja fechada com venda finalizada devolve a venda existente (o "Tentar de novo"
/// depois de uma excecao pos-commit nao cria V2, nao baixa estoque de novo, nao emite segunda nota).
///
/// DONO: a mesma marca do rascunho (PID:inicio do processo, <see cref="Rascunho.Eu"/>). Conta carregada
/// noutro Pdv.exe vivo nao se carrega nem se grava; processo morto (reinicio) libera.
/// </summary>
public static class ContasAbertas
{
    /// <summary>Tipo da outbox do rastro que sai do terminal (Drenagem: janela 5, sem prazo de descarte).</summary>
    public const string TipoEvento = "conta_evento";
    /// <summary>A RPC do ERP que registra o rastro (aplicar-hoje/pdv_conta_evento.sql). 404 PGRST202 = espera.</summary>
    public const string RpcEvento = "pdv_conta_evento";
    /// <summary>conta_evento e registro, nao transitorio: nunca sai da fila por idade.</summary>
    public static readonly TimeSpan SemPrazo = TimeSpan.FromDays(3650);

    /// <summary>Config: quantos chips "Mesa N" a face Nova comanda mostra (padrao 8).</summary>
    public const string ChaveMesas = "mesas_quantidade";
    public const int MesasPadrao = 8;
    /// <summary>Config: horas sem toque para cancelar sozinha uma conta vazia (0 = so no fechamento do caixa).</summary>
    public const string ChaveVaziaHoras = "conta_vazia_horas";

    /// <summary>Tirar linha nunca pedida de uma conta com mais vida que isto deixa rastro na auditoria (sem PIN).</summary>
    public static readonly TimeSpan JanelaRetiradaLivre = TimeSpan.FromMinutes(15);
    /// <summary>Sem nome, sem item e guardada antes disto: foi aberta por engano e some sozinha.</summary>
    public static readonly TimeSpan JanelaAbertaPorEngano = TimeSpan.FromMinutes(1);

    private const int NomeMaximo = 60;
    private static string Agora => DateTime.Now.ToString("o");

    // ── TEXTOS DA TELA (uma linha, sem travessao, vigiados por teste) ───────
    /// <summary>Tudo que a tela diz sobre comanda. A tela so escolhe; o texto mora aqui.</summary>
    public static class Textos
    {
        public const string Comandas = "Comandas";
        public const string NovaComanda = "Nova comanda";
        public const string NomeOuMesa = "Nome ou mesa";
        public const string NaLoja = "Na loja";
        public const string ParaViagem = "Para viagem";
        public const string Abrir = "Abrir";
        public const string AbrirVazia = "Abrir vazia";
        public const string Guardar = "Guardar";
        public const string FecharConta = "Fechar conta";
        /// <summary>Alternativa escrita para o botao a 1024, se a foto reprovar "Fechar conta".</summary>
        public const string Cobrar = "Cobrar";
        public const string MandarDeNovo = "Mandar de novo";
        public const string ItemJaPedido = "Item já pedido";
        public const string PinDeOutroGerente = "Esse item já foi para a cozinha. PIN de outro gerente para tirar.";
        public const string PinNaoConfere = "PIN não confere";
        public const string ChameOutroGerente = "Quem opera não autoriza. Chame outro gerente.";
        public const string Motivo = "Motivo";
        public const string MotivoObrigatorio = "Diga o motivo para cancelar.";
        public const string Balcao = "Balcão";
        public const string PedidoNaFila = "Pedido na fila da cozinha.";
        public const string CozinhaRecebeu = "Cozinha recebeu o pedido.";
        public const string NadaNovo = "Nada novo para a cozinha.";
        public const string PedidoJaNaTv = "Esse pedido já está na TV.";
        public const string PedidoAindaNaFila = "Esse pedido ainda está na fila.";
        public const string NadaParaMandarDeNovo = "Nada para mandar de novo.";
        public const string ContinuamProximoTurno = "Elas continuam para o próximo turno.";
        public const string FecharMesmoAssim = "Fechar mesmo assim";
        public const string SaiuDoCardapio = "saiu do cardápio";
        public const string CupomNaoValeMais = "Cupom não vale mais. Cobrando o item.";
        public const string TesteEmTurnoReal = "Comanda de teste não fecha em turno real.";
        public const string RealEmTurnoDeTeste = "Comanda real não fecha em turno de teste.";
        public const string NaoEncontrada = "Comanda não encontrada.";
        public const string ItemNaoEncontrado = "Item não está na comanda.";
        public const string NaTela = "na tela";
        public const string NaTv = "na TV";
        public const string SeloFila = "⚠ fila";
        public const string SeloNaoFoi = "⚠ não foi";
        public const string ConfiraMaquininha = "⚠ confira a maquininha";
        public const string EmUsoNoOutroCaixa = "em uso no outro caixa";
        public const string ZeroItens = "0 itens";
        /// <summary>O Limpar em modo comanda tira so o que ainda nao foi pedido.</summary>
        public const string JaPedidosFicam = "O que já foi pedido fica. Tire um a um com PIN.";
        public const string SemComandaNaTela = "Nenhuma comanda na tela.";
        public const string TituloNaLoja = "Na loja";
        public const string ConsumoDaComanda = "Consumo";
        public const string DicaBotaoComandas = "Comandas abertas: mesas e clientes";
        public const string DicaGuardar = "Guardar a comanda e voltar ao balcão";
        public const string DicaCozinha = "Mandar para a cozinha o que ainda não foi pedido";
        public const string AvisoAutorizador = "PIN de outro gerente";

        public static string ComandaAberta(long n) => $"Comanda {n} aberta.";
        public static string CozinhaN(long pendentes) => $"Cozinha {pendentes}";
        public static string CancelarComanda(long n) => $"Cancelar comanda {n}";
        public static string PedidoNaCozinhaGuardada(long n) => $"Pedido na cozinha. Comanda {n} guardada.";
        public static string JaFoiFechada(long n) => $"Comanda {n} já foi fechada.";
        public static string JaFoiCancelada(long n) => $"Comanda {n} já foi cancelada.";
        public static string ComandasAbertas(int n, Dinheiro valor) => $"Comandas abertas: {n} ({valor.Formatado()})";
        public static string ComandasCanceladasNoTurno(int n, Dinheiro valor) => $"Comandas canceladas no turno: {n} ({valor.Formatado()})";
        public static string CupomJaNaComanda(long n) => $"Esse cupom já está na comanda {n}.";
        public static string CarrinhoTem(int n) => n == 1 ? "O carrinho tem 1 item." : $"O carrinho tem {n} itens.";
        public static string AbrirComItens(int n) => n == 1 ? "Abrir com o item" : $"Abrir com os {n} itens";
        public static string GuardarEAbrir(long n) => $"Guardar e abrir a {n}";
        public static string LevarPara(long n) => $"Levar para a {n}";
        public static string JaEstaAberta(string nome) => $"{nome} já está aberta";
        public static string CriarOutra(string nome) => $"Criar outra {nome}";
        public static string Mesa(int i) => $"Mesa {i}";
        public static string CozinhaDeN(long pedidos, long total) => $"cozinha {pedidos} de {total}";
        public static string ContaGuardada(long n) => $"Comanda {n} guardada.";
        /// <summary>Revisao 13/10: o carrinho solto que virou comanda automatica diz onde foi parar.</summary>
        public static string CarrinhoFicouNa(long n) => $"O carrinho ficou na comanda {n}.";
        /// <summary>Revisao 13/10: o segundo toast do pedido repete o numero (o primeiro some em 2 s).</summary>
        public static string CozinhaRecebeuEGuardou(long n) => $"Cozinha recebeu. Comanda {n} guardada.";
        public const string MotivoDoCancelamento = "Por que está cancelando? Ex.: cliente desistiu";
        public const string GuardarCarrinhoEAbrirVazia = "Guardar e abrir vazia";
        public static string ContaCancelada(long n) => $"Comanda {n} cancelada.";
        public static string CancelarPergunta(long n) => $"Cancelar a comanda {n}?";
        public static string NaCozinha(long unidades) => unidades == 1 ? "1 na cozinha" : $"{unidades} na cozinha";
        public static string ItensEValor(decimal unidades, Dinheiro valor) => $"{Rascunho.TextoItens(unidades)} · {valor.Formatado()}";
        /// <summary>O TxtResumo do Pagamento: "Comanda 12 · Mesa 5 · 3 produtos · 5 itens".</summary>
        public static string ResumoDoPagamento(long n, string? nome, int produtos, decimal itens)
            => $"Comanda {n}" + (string.IsNullOrWhiteSpace(nome) ? "" : $" · {nome}")
               + $" · {produtos} {(produtos == 1 ? "produto" : "produtos")} · {itens:0.###} {(itens == 1 ? "item" : "itens")}";

        /// <summary>O selo da linha da lista pelo estado do ultimo lote. Lote de teste nao tem selo.</summary>
        public static string Selo(EstadoLote e) => e switch
        {
            EstadoLote.NaTv => NaTv,
            EstadoLote.NaFila => SeloFila,
            EstadoLote.NaoFoi => SeloNaoFoi,
            _ => "",
        };

        /// <summary>Todos os textos (os com numero com um exemplo), para o teste de travessao e de uma linha.</summary>
        public static IReadOnlyList<string> Todos() => new[]
        {
            Comandas, NovaComanda, NomeOuMesa, NaLoja, ParaViagem, Abrir, AbrirVazia, Guardar, FecharConta, Cobrar,
            MandarDeNovo, ItemJaPedido, PinDeOutroGerente, PinNaoConfere, ChameOutroGerente, Motivo, MotivoObrigatorio,
            Balcao, PedidoNaFila, CozinhaRecebeu, NadaNovo, PedidoJaNaTv, PedidoAindaNaFila, NadaParaMandarDeNovo,
            ContinuamProximoTurno, FecharMesmoAssim, SaiuDoCardapio, CupomNaoValeMais, TesteEmTurnoReal,
            RealEmTurnoDeTeste, NaoEncontrada, ItemNaoEncontrado, NaTela, NaTv, SeloFila, SeloNaoFoi, ConfiraMaquininha,
            EmUsoNoOutroCaixa, ZeroItens, JaPedidosFicam, SemComandaNaTela, TituloNaLoja, ConsumoDaComanda,
            DicaBotaoComandas, DicaGuardar, DicaCozinha, AvisoAutorizador,
            ComandaAberta(12), CozinhaN(2), CancelarComanda(12), PedidoNaCozinhaGuardada(12), JaFoiFechada(12),
            JaFoiCancelada(12), ComandasAbertas(2, Dinheiro.DeReais(85)), ComandasCanceladasNoTurno(1, Dinheiro.DeReais(24)),
            CupomJaNaComanda(13), CarrinhoTem(3), CarrinhoTem(1), AbrirComItens(3), GuardarEAbrir(12), LevarPara(12),
            JaEstaAberta("Mesa 5"), CriarOutra("Mesa 5"), Mesa(8), CozinhaDeN(2, 3), ContaGuardada(12), ContaCancelada(12),
            CancelarPergunta(12), NaCozinha(2), NaCozinha(1), ItensEValor(3, Dinheiro.DeReais(45)),
            ResumoDoPagamento(12, "Mesa 5", 3, 5), ResumoDoPagamento(12, null, 1, 1),
        };
    }

    // ── NUMERO, NOME ────────────────────────────────────────────────────────

    /// <summary>
    /// MAX(numero)+1 entre as contas de HOJE e as ainda ABERTAS de qualquer dia: a mesa que atravessou
    /// a noite nao tem o numero repetido. Nunca reaproveita numero_local da venda. Com tx, dentro do Abrir.
    /// </summary>
    public static long ProximoNumero(SqliteConnection cx, SqliteTransaction? tx = null)
        => cx.ExecuteScalar<long>(
            "SELECT COALESCE(MAX(numero),0)+1 FROM conta_aberta WHERE business_date = @Hoje OR status = 'aberta'",
            new { Hoje = Caixa.DiaOperacional() }, tx);

    /// <summary>"Mesa 5", " mesa  5 " e "MESA 5" sao a mesma mesa: sem acento, sem caixa, sem espaco duplo.</summary>
    public static string NormalizarNome(string? nome)
    {
        if (string.IsNullOrWhiteSpace(nome)) return "";
        var semAcento = new StringBuilder(nome.Length);
        foreach (var ch in nome.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) semAcento.Append(ch);
        var partes = semAcento.ToString().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', partes);
    }

    private static string? LimparNome(string? nome)
    {
        if (string.IsNullOrWhiteSpace(nome)) return null;
        var limpo = string.Join(' ', nome.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return limpo.Length > NomeMaximo ? limpo[..NomeMaximo] : limpo;
    }

    /// <summary>A conta aberta do modo com este nome (comparacao normalizada), ou null.</summary>
    public static ContaAberta? PorNome(SqliteConnection cx, string? nome, bool teste)
    {
        var alvo = NormalizarNome(nome);
        if (alvo.Length == 0) return null;
        return Listar(cx, teste).FirstOrDefault(c => NormalizarNome(c.Nome) == alvo);
    }

    // ── ABRIR, LER, LISTAR ──────────────────────────────────────────────────

    /// <summary>
    /// Abre a conta: status aberta, numero do dia, teste pelo modo atual (homologacao ou turno de teste),
    /// dono = este processo. Os itens (se vierem do carrinho solto) ganham LinhaId. Nada vai para a fila.
    /// </summary>
    public static ContaAberta Abrir(SqliteConnection cx, Sessao sessao, Operador operador, string? nome,
        DestinoCozinha consumo = DestinoCozinha.Loja, IReadOnlyList<ItemRascunho>? itens = null,
        string? cortesiaCodigo = null, IReadOnlyDictionary<string, int>? cortesiaCobertura = null)
    {
        // lidos ANTES da transacao (o SQLite recusa comando sem transacao com uma pendente)
        var teste = Vendas.Homologacao(cx) || sessao.Teste;
        var idAssina = Operadores.IdCanonico(cx, operador.Id);
        var limpo = LimparNome(nome);
        var lista = ComLinhaId(itens ?? Array.Empty<ItemRascunho>());
        var id = Guid.NewGuid().ToString("N");
        var agora = Agora;
        var cons = consumo == DestinoCozinha.Viagem ? "viagem" : "loja";

        using var tx = cx.BeginTransaction();
        var numero = ProximoNumero(cx, tx);
        cx.Execute("""
            INSERT INTO conta_aberta (id, numero, nome, consumo, status, itens_json, cortesia_codigo, cortesia_json,
                                      operador_id, sessao_abertura, business_date, teste,
                                      aberta_em, atualizado_em, ultimo_toque_em, dono)
            VALUES (@Id, @Num, @Nome, @Cons, 'aberta', @Itens, @Cod, @Cob, @Op, @Ses, @Bd, @Teste, @Em, @Em, @Em, @Dono)
            """,
            new
            {
                Id = id, Num = numero, Nome = limpo, Cons = cons, Itens = JsonSerializer.Serialize(lista),
                Cod = LimparCodigo(cortesiaCodigo),
                Cob = cortesiaCobertura is { Count: > 0 } ? JsonSerializer.Serialize(cortesiaCobertura) : null,
                Op = idAssina, Ses = sessao.Id, Bd = sessao.BusinessDate, Teste = teste ? 1 : 0, Em = agora,
                Dono = Rascunho.Eu.Length == 0 ? null : Rascunho.Eu,
            }, tx);
        Caixa.Auditar(cx, tx, "conta_aberta", idAssina, null,
            $"comanda={numero} nome={limpo ?? "(sem nome)"} consumo={cons} itens={lista.Count}{(teste ? " teste" : "")}");
        if (LimparCodigo(cortesiaCodigo) is { } cod)
            Caixa.Auditar(cx, tx, "cortesia_aplicada_em_comanda", idAssina, null, $"comanda={numero} cupom={cod}");
        tx.Commit();
        return Ler(cx, id)!;
    }

    public static ContaAberta? Ler(SqliteConnection cx, string id, SqliteTransaction? tx = null)
    {
        var l = cx.QueryFirstOrDefault("SELECT * FROM conta_aberta WHERE id = @Id", new { Id = id }, tx);
        return l is null ? null : Mapear(l);
    }

    /// <summary>As contas ABERTAS do modo atual (teste ou real), por numero. A lista do dialogo Comandas.</summary>
    public static IReadOnlyList<ContaAberta> Listar(SqliteConnection cx, bool teste)
        => cx.Query("SELECT * FROM conta_aberta WHERE status = 'aberta' AND teste = @T ORDER BY numero", new { T = teste ? 1 : 0 })
             .Select(l => (ContaAberta)Mapear(l)).ToList();

    /// <summary>O numero do botao Comandas: quantas abertas no modo atual.</summary>
    public static int ContarAbertas(SqliteConnection cx, bool teste)
        => cx.ExecuteScalar<int>("SELECT COUNT(*) FROM conta_aberta WHERE status = 'aberta' AND teste = @T", new { T = teste ? 1 : 0 });

    /// <summary>A conta esta carregada noutro Pdv.exe que continua vivo? (processo morto = livre)</summary>
    public static bool EmUsoPorOutroPdv(ContaAberta conta) => Rascunho.DeOutroPdvVivo(conta.Dono);

    /// <summary>
    /// As linhas do dialogo Comandas do modo atual, por numero, com os textos prontos. A conferencia da
    /// maquininha (Caixa.CobrancaSemVenda) roda UMA vez por lista: positiva, so marca a conta que nao esta
    /// na tela e cujo dono nao esta vivo (o caixa caiu com ela carregada); fechando_em de pe sem venda
    /// marca sempre. Nunca lanca: a lista aparece mesmo com a conferencia falhando.
    /// </summary>
    public static IReadOnlyList<LinhaDaLista> LinhasDaLista(SqliteConnection cx, Sessao sessao, string? carregadaId, bool teste)
    {
        var cobrado = Cobrado(cx, sessao);
        var r = new List<LinhaDaLista>();
        foreach (var c in Listar(cx, teste))
        {
            var naTela = carregadaId is not null && c.Id == carregadaId;
            var emUso = !naTela && EmUsoPorOutroPdv(c);
            var donoMorto = !naTela && !emUso;
            var conferir = (c.FechandoEm is not null && c.VendaId is null) || (donoMorto && cobrado is { Positivo: true });
            var unidades = c.Itens.Sum(i => i.QtdMilesimos) / 1000m;
            // revisao 13/10: lote que NAO FOI nao conta como "na cozinha" (a mesma regua do botao Cozinha N)
            var pedidas = Math.Max(0m, unidades - Pendentes(cx, c).Sum(p => p.Milesimos) / 1000m);
            var lotes = Lotes(cx, c.Id).Where(l => !l.Homologacao).ToList();
            var ultimo = lotes.OrderByDescending(l => l.N).FirstOrDefault();
            var estado = ultimo is null ? EstadoLote.Teste : EstadoDoLote(cx, ultimo.Id);
            var itens = c.Itens.Count == 0 ? Textos.ZeroItens : Textos.ItensEValor(unidades, c.ValorTabela);
            var cozinha = lotes.Count == 0 && pedidas == 0 ? "" : Textos.CozinhaDeN((long)pedidas, (long)unidades);
            var selo = naTela ? Textos.NaTela
                : emUso ? Textos.EmUsoNoOutroCaixa
                : conferir ? Textos.ConfiraMaquininha
                : Textos.Selo(estado);
            var reenviar = estado == EstadoLote.NaoFoi && ultimo is { SubstituidoPor: null } ? ultimo.Id : null;
            r.Add(new LinhaDaLista(c, c.Titulo, itens, cozinha, selo, naTela, emUso, conferir, reenviar));
        }
        return r;
    }

    /// <summary>
    /// Assume a conta para esta tela (dono = este processo). Null quando ela nao esta mais aberta ou esta
    /// carregada noutro Pdv.exe vivo ("em uso no outro caixa"). Quem reinicia o caixa carrega: o dono morreu.
    /// </summary>
    public static ContaAberta? Carregar(SqliteConnection cx, string id)
    {
        var c = Ler(cx, id);
        if (c is null || !c.Aberta || EmUsoPorOutroPdv(c)) return null;
        cx.Execute("UPDATE conta_aberta SET dono = @D, ultimo_toque_em = @Em WHERE id = @Id AND status = 'aberta'",
            new { D = Rascunho.Eu.Length == 0 ? null : Rascunho.Eu, Em = Agora, Id = id });
        return Ler(cx, id);
    }

    /// <summary>Guardar: solta o dono. A conta continua aberta, com os itens, para qualquer tela carregar.</summary>
    public static void Soltar(SqliteConnection cx, string id)
        => cx.Execute("UPDATE conta_aberta SET dono = NULL WHERE id = @Id", new { Id = id });

    /// <summary>
    /// Revisao 13/10 (achado alto): comanda INTEIRA coberta por cupom de cortesia nao vira venda (nao ha o
    /// que cobrar) e por isso nao passa pelo Vendas.Finalizar. Sem isto ela ficava 'aberta' para sempre e,
    /// com lote na cozinha, nem dava para esvaziar. Fecha sem venda (venda_id nulo), com auditoria e o
    /// evento para o ERP (producao sem venda). Idempotente: devolve false se ja nao estava aberta.
    /// </summary>
    public static bool FecharSemVenda(SqliteConnection cx, string id, Operador operador, string motivo)
    {
        var c = Ler(cx, id);
        if (c is null || c.Status != "aberta") return false;
        var lotes = Lotes(cx, id);
        var idOp = Operadores.IdCanonico(cx, operador.Id);
        using var tx = cx.BeginTransaction();
        var n = cx.Execute("""
            UPDATE conta_aberta SET status = 'fechada', fechada_em = @Em, venda_id = NULL, fechando_em = NULL, dono = NULL
             WHERE id = @Id AND status = 'aberta'
            """, new { Em = Agora, Id = id }, tx);
        if (n == 0) { tx.Rollback(); return false; }
        Caixa.Auditar(cx, tx, "conta_fechada_sem_venda", idOp, null,
            $"comanda={c.Numero} nome={c.Nome ?? "(sem nome)"} itens={ResumoItens(c.Itens)} valor={c.ValorTabela.Formatado()} lotes={lotes.Count} motivo={motivo}");
        if (!c.Teste) EnfileirarEvento(cx, tx, "conta_fechada_sem_venda", c, lotes, operador, null, motivo);
        tx.Commit();
        return true;
    }

    /// <summary>
    /// Revisao 13/10: por linha, quanto do que foi pedido a cozinha esta num lote que NAO FOI (sem
    /// substituto). A tela tira isso do "N na cozinha" e a lista conta como pendente, a mesma regua do botao.
    /// </summary>
    public static IReadOnlyDictionary<string, long> PerdidosPorLinha(SqliteConnection cx, string contaId)
    {
        var perdidos = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var lote in LotesPerdidos(cx, contaId, null))
            foreach (var i in ItensQueFicam(lote))
                perdidos[i.LinhaId] = perdidos.GetValueOrDefault(i.LinhaId) + i.QtdMilesimos;
        return perdidos;
    }

    /// <summary>
    /// Grava os itens e a cortesia da conta (a cada PintarComanda com a conta carregada). Lista vazia NAO
    /// cancela: conta vazia guardada fica na lista com "0 itens".
    ///
    /// A REGRA DO ITEM JA PEDIDO MORA AQUI, nao so na tela: linha com CozinhaMilesimos maior que a
    /// quantidade nova (ou que sumiu da lista) e recusada com <see cref="Textos.ItemJaPedido"/>. Tirar
    /// producao e so por <see cref="RetirarProduzido"/>, com PIN de outro gerente. CozinhaMilesimos e o
    /// que o DISCO diz (a tela pode esquecer de copiar o campo; o nucleo nao perde a marca).
    ///
    /// Tirar linha nunca pedida e livre; com a conta viva ha mais de 15 min fica o rastro (item e valor).
    /// </summary>
    public static void Gravar(SqliteConnection cx, string id, IReadOnlyList<ItemRascunho> itens,
        string? cortesiaCodigo, IReadOnlyDictionary<string, int>? cortesiaCobertura, Operador operador)
    {
        var atual = Ler(cx, id) ?? throw new InvalidOperationException(Textos.NaoEncontrada);
        if (!atual.Aberta) throw new InvalidOperationException(atual.Status == "cancelada" ? Textos.JaFoiCancelada(atual.Numero) : Textos.JaFoiFechada(atual.Numero));
        if (EmUsoPorOutroPdv(atual)) throw new InvalidOperationException(Textos.EmUsoNoOutroCaixa);

        var novos = ComLinhaId(itens);
        var novosPorId = novos.ToDictionary(i => i.LinhaId!, StringComparer.Ordinal);
        var velhasPorId = new Dictionary<string, ItemRascunho>(StringComparer.Ordinal);
        var retiradasLivres = new List<(ItemRascunho Linha, long Milesimos)>();
        foreach (var velha in atual.Itens)
        {
            if (velha.LinhaId is null) continue;
            velhasPorId[velha.LinhaId] = velha;
            var qtdNova = novosPorId.TryGetValue(velha.LinhaId, out var nova) ? nova.QtdMilesimos : 0;
            if (qtdNova < velha.CozinhaMilesimos) throw new InvalidOperationException(Textos.ItemJaPedido);
            if (qtdNova < velha.QtdMilesimos) retiradasLivres.Add((velha, velha.QtdMilesimos - qtdNova));
        }
        var finais = novos.Select(n =>
        {
            var cozinha = velhasPorId.TryGetValue(n.LinhaId!, out var v) ? v.CozinhaMilesimos : 0;
            return n with { CozinhaMilesimos = Math.Clamp(cozinha, 0, n.QtdMilesimos) };
        }).ToList();

        var codigo = LimparCodigo(cortesiaCodigo);
        var idAssina = Operadores.IdCanonico(cx, operador.Id);
        var agora = Agora;
        using var tx = cx.BeginTransaction();
        cx.Execute("""
            UPDATE conta_aberta
               SET itens_json = @Itens, cortesia_codigo = @Cod, cortesia_json = @Cob,
                   atualizado_em = @Em, ultimo_toque_em = @Em, dono = @Dono
             WHERE id = @Id AND status = 'aberta'
            """,
            new
            {
                Itens = JsonSerializer.Serialize(finais), Cod = codigo,
                Cob = cortesiaCobertura is { Count: > 0 } ? JsonSerializer.Serialize(cortesiaCobertura) : null,
                Em = agora, Dono = Rascunho.Eu.Length == 0 ? null : Rascunho.Eu, Id = id,
            }, tx);
        if (codigo is not null && codigo != atual.CortesiaCodigo)
            Caixa.Auditar(cx, tx, "cortesia_aplicada_em_comanda", idAssina, null, $"comanda={atual.Numero} cupom={codigo}");
        if (retiradasLivres.Count > 0 && DateTime.Now - atual.AbertaEm > JanelaRetiradaLivre)
            foreach (var (linha, mil) in retiradasLivres)
                Caixa.Auditar(cx, tx, "conta_item_retirado", idAssina, null,
                    $"comanda={atual.Numero} item={linha.Nome} qtd={Qtd(mil)} valor={new Dinheiro(linha.PrecoCent).VezesQtd(mil).Formatado()} (nunca pedido)");
        tx.Commit();
    }

    /// <summary>Sem nome, sem item e com menos de 1 min de vida: foi aberta por engano e pode sumir sozinha ao guardar.</summary>
    public static bool AbertaPorEngano(ContaAberta c, DateTime agora)
        => string.IsNullOrWhiteSpace(c.Nome) && c.Itens.Count == 0 && agora - c.AbertaEm < JanelaAbertaPorEngano;

    /// <summary>Preco de tabela x quantidade, linha a linha (promocao e cortesia nao entram: e o valor de referencia do rastro).</summary>
    public static Dinheiro ValorTabela(IEnumerable<ItemRascunho> itens)
        => new(itens.Sum(i => new Dinheiro(i.PrecoCent).VezesQtd(i.QtdMilesimos).Centavos));

    /// <summary>O numero da OUTRA conta aberta que ja tem este cupom (reserva por conta), ou null se esta livre.</summary>
    public static long? CodigoEmOutraConta(SqliteConnection cx, string? codigo, string? excetoId)
    {
        var alvo = LimparCodigo(codigo);
        if (alvo is null) return null;
        var l = cx.QueryFirstOrDefault(
            "SELECT numero FROM conta_aberta WHERE status = 'aberta' AND cortesia_codigo IS NOT NULL AND upper(cortesia_codigo) = @C AND id <> @Id ORDER BY numero LIMIT 1",
            new { C = alvo, Id = excetoId ?? "" });
        return l is null ? null : (long)l.numero;
    }

    private static string? LimparCodigo(string? codigo)
        => string.IsNullOrWhiteSpace(codigo) ? null : codigo.Trim().ToUpperInvariant();

    // ── CARDAPIO E COMBO: as barreiras ──────────────────────────────────────

    /// <summary>
    /// Linhas cujo produto nao esta mais no catalogo local (ativo). Preco e fiscal vem SEMPRE do catalogo
    /// atual; o snapshot da linha so mostra o nome e barra. A linha de valor de teste nao conta.
    /// </summary>
    public static IReadOnlyList<ItemRascunho> ForaDoCardapio(SqliteConnection cx, ContaAberta conta)
    {
        if (conta.Itens.Count == 0) return Array.Empty<ItemRascunho>();
        var ativos = cx.Query<string>("SELECT id FROM produto WHERE ativo = 1").ToHashSet(StringComparer.Ordinal);
        return conta.Itens.Where(i => !ativos.Contains(i.ProdutoId) && !Promocoes.ForaDoMotor(i.ProdutoId)).ToList();
    }

    /// <summary>
    /// O que barra "Pedir a cozinha" e "Fechar conta" (a tela mostra a frase e nao segue): linha fora do
    /// cardapio, ou combo incompleto (Combos.Pendencia, a mesma regra do Finalizar). Null = pode seguir.
    /// </summary>
    public static string? Barreira(SqliteConnection cx, ContaAberta conta)
    {
        var fora = ForaDoCardapio(cx, conta);
        if (fora.Count > 0) return $"{fora[0].Nome}: {Textos.SaiuDoCardapio}";
        var combos = Combos.Carregar(cx);
        if (combos.Count == 0) return null;
        var catalogo = cx.Query("SELECT id, plu, nome, categoria FROM produto WHERE ativo = 1")
            .Select(p => new Combos.ProdutoLocal((string)p.id, (string?)p.plu, (string)p.nome, (string?)p.categoria ?? "")).ToList();
        foreach (var i in conta.Itens)
            if (combos.TryGetValue(i.ProdutoId, out var def) && Combos.Pendencia(def, Combos.DeJson(i.EscolhasJson), catalogo) is { } pendencia)
                return pendencia;
        return null;
    }

    // ── LOTES PARA A COZINHA ────────────────────────────────────────────────

    public static IReadOnlyList<ContaLote> Lotes(SqliteConnection cx, string contaId, SqliteTransaction? tx = null)
        => cx.Query("SELECT * FROM conta_lote WHERE conta_id = @C ORDER BY n", new { C = contaId }, tx)
             .Select(l => (ContaLote)MapearLote(l)).ToList();

    public static ContaLote? LerLote(SqliteConnection cx, string loteId, SqliteTransaction? tx = null)
    {
        var l = cx.QueryFirstOrDefault("SELECT * FROM conta_lote WHERE id = @Id", new { Id = loteId }, tx);
        return l is null ? null : MapearLote(l);
    }

    /// <summary>
    /// O estado do lote, lido da linha kds_cozinha da outbox (nunca gravado):
    /// sem linha = Teste; enviado_em sem 'descartado' = NaTv; descartado, desistido ou chave recusada
    /// (401/403) = NaoFoi; senao NaFila.
    /// </summary>
    public static EstadoLote EstadoDoLote(SqliteConnection cx, string loteId, SqliteTransaction? tx = null)
    {
        var l = cx.QueryFirstOrDefault(
            "SELECT enviado_em, desistido_em, descartado_em, ultimo_erro FROM outbox WHERE tipo = @T AND ref_id = @R ORDER BY id DESC LIMIT 1",
            new { T = PedidoCozinha.TipoNaFila, R = loteId }, tx);
        if (l is null) return EstadoLote.Teste;
        var erro = (string?)l.ultimo_erro ?? "";
        if (l.desistido_em is string || l.descartado_em is string) return EstadoLote.NaoFoi;
        if (erro.StartsWith("descartado", StringComparison.OrdinalIgnoreCase)) return EstadoLote.NaoFoi;
        if (l.enviado_em is string) return EstadoLote.NaTv;
        if (erro.Contains("401", StringComparison.Ordinal) || erro.Contains("403", StringComparison.Ordinal)) return EstadoLote.NaoFoi;
        return EstadoLote.NaFila;
    }

    /// <summary>O ultimo lote da conta ainda nao esta na TV (fila ou nao foi): o botao Cozinha ganha o aviso.</summary>
    public static bool UltimoLoteNaoChegou(SqliteConnection cx, string contaId)
    {
        var ultimo = Lotes(cx, contaId).Where(l => !l.Homologacao).OrderByDescending(l => l.N).FirstOrDefault();
        if (ultimo is null) return false;
        return EstadoDoLote(cx, ultimo.Id) is EstadoLote.NaFila or EstadoLote.NaoFoi;
    }

    /// <summary>
    /// O que ainda nao foi para a cozinha, linha a linha: Qtd menos CozinhaMilesimos, MAIS o que pertence
    /// a lote que nao foi (NaoFoi) e nao tem substituto. E o numero do botao "Cozinha N" e o conteudo
    /// do proximo lote.
    /// </summary>
    public static IReadOnlyList<PendenteDaConta> Pendentes(SqliteConnection cx, ContaAberta conta, SqliteTransaction? tx = null)
    {
        var perdidos = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var lote in LotesPerdidos(cx, conta.Id, tx))
            foreach (var i in ItensQueFicam(lote))
                perdidos[i.LinhaId] = perdidos.GetValueOrDefault(i.LinhaId) + i.QtdMilesimos;

        var r = new List<PendenteDaConta>();
        foreach (var linha in conta.Itens)
        {
            var aindaNao = Math.Max(0, linha.QtdMilesimos - linha.CozinhaMilesimos);
            var perdido = linha.LinhaId is null ? 0 : perdidos.GetValueOrDefault(linha.LinhaId);
            var total = Math.Min(linha.QtdMilesimos, aindaNao + perdido);
            if (total > 0) r.Add(new PendenteDaConta(linha, total));
        }
        return r;
    }

    /// <summary>Lotes que nao chegaram (NaoFoi) e ainda nao tem substituto: o que "Mandar de novo" e o proximo pedido levam.</summary>
    private static List<ContaLote> LotesPerdidos(SqliteConnection cx, string contaId, SqliteTransaction? tx)
        => Lotes(cx, contaId, tx)
            .Where(l => l.SubstituidoPor is null && !l.Homologacao && EstadoDoLote(cx, l.Id, tx) == EstadoLote.NaoFoi)
            .ToList();

    /// <summary>Os itens do lote menos o que foi tirado com PIN (so o que sobrou maior que zero).</summary>
    private static List<ItemDoLote> ItensQueFicam(ContaLote lote)
    {
        var r = new List<ItemDoLote>();
        foreach (var i in lote.Itens)
        {
            var tirado = lote.Retirados.Where(x => x.LinhaId == i.LinhaId).Sum(x => x.QtdMilesimos);
            var fica = i.QtdMilesimos - tirado;
            if (fica > 0) r.Add(i with { QtdMilesimos = fica });
        }
        return r;
    }

    /// <summary>
    /// "Pedir a cozinha": numa transacao so, o lote n+1 com os pendentes, CozinhaMilesimos = Qtd nas linhas
    /// do lote, a linha kds_cozinha na outbox (salvo homologacao) e a auditoria. Nada de rede no toque: a
    /// tela chama Servicos.Dreno()?.Cutucar() depois. Null = nada pendente ("Nada novo para a cozinha.").
    /// Barra combo incompleto e linha fora do cardapio (<see cref="Barreira"/>).
    /// </summary>
    public static ContaLote? PedirCozinha(SqliteConnection cx, string id, DestinoCozinha? destino, Operador operador, bool homologacao)
    {
        var conta = Ler(cx, id) ?? throw new InvalidOperationException(Textos.NaoEncontrada);
        if (!conta.Aberta) throw new InvalidOperationException(conta.Status == "cancelada" ? Textos.JaFoiCancelada(conta.Numero) : Textos.JaFoiFechada(conta.Numero));
        if (EmUsoPorOutroPdv(conta)) throw new InvalidOperationException(Textos.EmUsoNoOutroCaixa);
        if (Barreira(cx, conta) is { } barreira) throw new InvalidOperationException(barreira);
        var idAssina = Operadores.IdCanonico(cx, operador.Id);

        using var tx = cx.BeginTransaction();
        var lote = CriarLoteNoTx(cx, tx, conta, destino ?? conta.Destino, operador with { Id = idAssina }, homologacao);
        tx.Commit();
        return lote;
    }

    /// <summary>
    /// O lote dentro de uma transacao de quem chama (PedirCozinha, e o Finalizar para os pendentes do
    /// fechamento). Le os pendentes da PROPRIA conta (itens_json), nunca das LinhaVenda. Em homologacao o
    /// lote existe no banco (homologacao=1) e nada entra na outbox. Null quando nao ha pendente.
    /// </summary>
    public static ContaLote? CriarLoteNoTx(SqliteConnection cx, SqliteTransaction tx, ContaAberta conta,
        DestinoCozinha destino, Operador operador, bool homologacao)
    {
        var pendentes = Pendentes(cx, conta, tx);
        if (pendentes.Count == 0) return null;
        var perdidos = LotesPerdidos(cx, conta.Id, tx);

        var n = cx.ExecuteScalar<int>("SELECT COALESCE(MAX(n),0)+1 FROM conta_lote WHERE conta_id = @C", new { C = conta.Id }, tx);
        var loteId = $"{conta.Id}-{n}";
        var itens = pendentes.Select(p => new ItemDoLote(p.Linha.LinhaId!, p.Linha.Nome, p.Milesimos, p.Linha.Unidade, p.Linha.EscolhasJson, false)).ToList();
        var consumo = destino == DestinoCozinha.Viagem ? "viagem" : "loja";
        var agora = DateTime.Now;

        cx.Execute("""
            INSERT INTO conta_lote (id, conta_id, n, consumo, itens_json, homologacao, operador_id, criado_em)
            VALUES (@Id, @C, @N, @Cons, @Itens, @H, @Op, @Em)
            """,
            new { Id = loteId, C = conta.Id, N = n, Cons = consumo, Itens = JsonSerializer.Serialize(itens),
                  H = homologacao ? 1 : 0, Op = operador.Id, Em = agora.ToString("o") }, tx);
        foreach (var velho in perdidos)
            cx.Execute("UPDATE conta_lote SET substituido_por = @S WHERE id = @Id AND substituido_por IS NULL", new { S = loteId, Id = velho.Id }, tx);

        var noLote = itens.Select(i => i.LinhaId).ToHashSet(StringComparer.Ordinal);
        var linhas = conta.Itens.Select(i => i.LinhaId is not null && noLote.Contains(i.LinhaId) ? i with { CozinhaMilesimos = i.QtdMilesimos } : i).ToList();
        cx.Execute("UPDATE conta_aberta SET itens_json = @Itens, atualizado_em = @Em, ultimo_toque_em = @Em WHERE id = @Id",
            new { Itens = JsonSerializer.Serialize(linhas), Em = agora.ToString("o"), Id = conta.Id }, tx);

        if (!homologacao)
            PedidoCozinha.Enfileirar(cx, tx, loteId,
                PedidoCozinha.CorpoDoLote(loteId, conta.Id, conta.Numero, conta.Nome, n, itens, destino, operador.Nome, agora));
        Caixa.Auditar(cx, tx, "conta_lote_pedido", operador.Id, null,
            $"comanda={conta.Numero} lote={n} consumo={consumo} itens={ResumoItens(itens)}{(homologacao ? " teste" : "")}"
            + (perdidos.Count > 0 ? $" substitui={string.Join(",", perdidos.Select(p => p.N))}" : ""));
        return new ContaLote(loteId, conta.Id, n, consumo, itens, Array.Empty<RetiradaDoLote>(), null, homologacao, operador.Id, agora);
    }

    /// <summary>
    /// "Mandar de novo" um lote que NAO FOI (descartado, desistido, chave recusada): cria o lote n+1 com os
    /// itens dele (menos os retirados), marca o velho como substituido e enfileira. Lote na fila ou na TV
    /// nao se manda de novo.
    /// </summary>
    public static ContaLote ReenviarLote(SqliteConnection cx, string loteId, Operador operador)
    {
        var lote = LerLote(cx, loteId) ?? throw new InvalidOperationException(Textos.NaoEncontrada);
        var conta = Ler(cx, lote.ContaId) ?? throw new InvalidOperationException(Textos.NaoEncontrada);
        if (!conta.Aberta) throw new InvalidOperationException(conta.Status == "cancelada" ? Textos.JaFoiCancelada(conta.Numero) : Textos.JaFoiFechada(conta.Numero));
        if (lote.SubstituidoPor is not null) throw new InvalidOperationException(Textos.NadaParaMandarDeNovo);
        var estado = EstadoDoLote(cx, loteId);
        if (estado == EstadoLote.NaTv) throw new InvalidOperationException(Textos.PedidoJaNaTv);
        if (estado == EstadoLote.NaFila) throw new InvalidOperationException(Textos.PedidoAindaNaFila);
        var itens = ItensQueFicam(lote);
        if (itens.Count == 0) throw new InvalidOperationException(Textos.NadaParaMandarDeNovo);
        var idAssina = Operadores.IdCanonico(cx, operador.Id);

        using var tx = cx.BeginTransaction();
        var n = cx.ExecuteScalar<int>("SELECT COALESCE(MAX(n),0)+1 FROM conta_lote WHERE conta_id = @C", new { C = conta.Id }, tx);
        var novoId = $"{conta.Id}-{n}";
        var agora = DateTime.Now;
        cx.Execute("""
            INSERT INTO conta_lote (id, conta_id, n, consumo, itens_json, homologacao, operador_id, criado_em)
            VALUES (@Id, @C, @N, @Cons, @Itens, @H, @Op, @Em)
            """,
            new { Id = novoId, C = conta.Id, N = n, Cons = lote.Consumo, Itens = JsonSerializer.Serialize(itens),
                  H = lote.Homologacao ? 1 : 0, Op = idAssina, Em = agora.ToString("o") }, tx);
        cx.Execute("UPDATE conta_lote SET substituido_por = @S WHERE id = @Id", new { S = novoId, Id = loteId }, tx);
        if (!lote.Homologacao)
            PedidoCozinha.Enfileirar(cx, tx, novoId,
                PedidoCozinha.CorpoDoLote(novoId, conta.Id, conta.Numero, conta.Nome, n, itens, lote.Destino, operador.Nome, agora));
        Caixa.Auditar(cx, tx, "conta_lote_reenviado", idAssina, null,
            $"comanda={conta.Numero} lote={n} substitui={lote.N} itens={ResumoItens(itens)}");
        EnfileirarEvento(cx, tx, "conta_lote_reenviado", conta, Lotes(cx, conta.Id, tx), operador, null, $"lote {lote.N} nao foi; mandado de novo como {n}");
        tx.Commit();
        return new ContaLote(novoId, conta.Id, n, lote.Consumo, itens, Array.Empty<RetiradaDoLote>(), null, lote.Homologacao, idAssina, agora);
    }

    /// <summary>
    /// O que o dreno faz ANTES do POST de um lote (Drenagem.EnviarPedidoCozinhaAsync): conta cancelada ou
    /// lote substituido = descarta sem enviar; item tirado com o lote ainda na fila = refaz o corpo sem
    /// ele (lote que ficou vazio = descartado). Pedido de venda de balcao passa intacto. Nao escreve nada.
    /// </summary>
    public static (bool Descartar, string? Motivo, string? Corpo) PrepararEnvio(SqliteConnection cx, string payload)
    {
        var loteId = PedidoCozinha.LoteDeComanda(payload);
        if (loteId is null) return (false, null, null);
        var lote = LerLote(cx, loteId);
        if (lote is null) return (false, null, null);
        var conta = Ler(cx, lote.ContaId);
        if (conta is null) return (false, null, null);
        if (conta.Status == "cancelada") return (true, "descartado: comanda cancelada antes de ir para a cozinha", null);
        if (lote.SubstituidoPor is not null) return (true, "descartado: substituído por outro pedido (mandado de novo)", null);
        if (lote.Retirados.Count == 0) return (false, null, null);
        var itens = ItensQueFicam(lote);
        if (itens.Count == 0) return (true, "descartado: todos os itens foram tirados antes de ir para a cozinha", null);
        string? operadorNome = null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("operador", out var o) && o.ValueKind == JsonValueKind.String) operadorNome = o.GetString();
        }
        catch { }
        var corpo = PedidoCozinha.CorpoDoLote(lote.Id, conta.Id, conta.Numero, conta.Nome, lote.N, itens, lote.Destino, operadorNome, lote.CriadoEm);
        return (false, null, JsonSerializer.Serialize(corpo));
    }

    /// <summary>
    /// Tira (ou diminui) uma linha JA PEDIDA: PIN de OUTRO gerente (sup.Id != operador, a regra da sangria).
    /// Baixa Qtd e CozinhaMilesimos (zero tira a linha), grava itens_retirados_json nos lotes que ainda
    /// estao na fila (o envio refaz o corpo), audita com item, valor, operador e autorizador, e enfileira
    /// conta_evento. Lote ja na TV e aviso de boca: fica so o rastro.
    /// </summary>
    public static void RetirarProduzido(SqliteConnection cx, string id, string linhaId, long novaQtdMilesimos,
        Operador operador, Operador? autorizador)
    {
        ExigirOutroGerente(cx, operador, autorizador);
        if (novaQtdMilesimos < 0) throw new InvalidOperationException("Quantidade inválida.");
        var conta = Ler(cx, id) ?? throw new InvalidOperationException(Textos.NaoEncontrada);
        if (!conta.Aberta) throw new InvalidOperationException(conta.Status == "cancelada" ? Textos.JaFoiCancelada(conta.Numero) : Textos.JaFoiFechada(conta.Numero));
        var linha = conta.Itens.FirstOrDefault(i => i.LinhaId == linhaId) ?? throw new InvalidOperationException(Textos.ItemNaoEncontrado);
        var retirada = Math.Max(0, linha.CozinhaMilesimos - novaQtdMilesimos);
        var idOp = Operadores.IdCanonico(cx, operador.Id);
        var idAut = Operadores.IdCanonico(cx, autorizador!.Id);

        var novos = conta.Itens
            .Where(i => i.LinhaId != linhaId || novaQtdMilesimos > 0)
            .Select(i => i.LinhaId == linhaId ? i with { QtdMilesimos = novaQtdMilesimos, CozinhaMilesimos = Math.Min(i.CozinhaMilesimos, novaQtdMilesimos) } : i)
            .ToList();
        var agora = Agora;
        using var tx = cx.BeginTransaction();
        cx.Execute("UPDATE conta_aberta SET itens_json = @Itens, atualizado_em = @Em, ultimo_toque_em = @Em WHERE id = @Id AND status = 'aberta'",
            new { Itens = JsonSerializer.Serialize(novos), Em = agora, Id = id }, tx);

        // o que ainda esta na fila nao precisa ser produzido: o corpo e refeito antes do POST
        var restante = retirada;
        foreach (var lote in Lotes(cx, id, tx).OrderByDescending(l => l.N))
        {
            if (restante <= 0) break;
            if (lote.SubstituidoPor is not null || EstadoDoLote(cx, lote.Id, tx) != EstadoLote.NaFila) continue;
            var item = lote.Itens.FirstOrDefault(i => i.LinhaId == linhaId);
            if (item is null) continue;
            var disponivel = item.QtdMilesimos - lote.Retirados.Where(r => r.LinhaId == linhaId).Sum(r => r.QtdMilesimos);
            var tira = Math.Min(disponivel, restante);
            if (tira <= 0) continue;
            var retirados = lote.Retirados.Append(new RetiradaDoLote(linhaId, tira)).ToList();
            cx.Execute("UPDATE conta_lote SET itens_retirados_json = @J WHERE id = @Id", new { J = JsonSerializer.Serialize(retirados), Id = lote.Id }, tx);
            restante -= tira;
        }

        if (retirada > 0)
        {
            var valor = new Dinheiro(linha.PrecoCent).VezesQtd(retirada);
            Caixa.Auditar(cx, tx, "conta_item_retirado_apos_cozinha", idOp, idAut,
                $"comanda={conta.Numero} item={linha.Nome} qtd={Qtd(retirada)} valor={valor.Formatado()} ficou={Qtd(novaQtdMilesimos)}");
            EnfileirarEvento(cx, tx, "conta_item_retirado_apos_cozinha", conta, Lotes(cx, id, tx), operador, autorizador,
                $"{linha.Nome} x{Qtd(retirada)} ({valor.Formatado()}) tirado depois de pedido");
        }
        else if (novaQtdMilesimos < linha.QtdMilesimos)
            Caixa.Auditar(cx, tx, "conta_item_retirado", idOp, null,
                $"comanda={conta.Numero} item={linha.Nome} qtd={Qtd(linha.QtdMilesimos - novaQtdMilesimos)} (nunca pedido)");
        tx.Commit();
    }

    /// <summary>A dupla assinatura: gerente ou supervisor, e NAO a mesma pessoa que opera (ids canonicos).</summary>
    private static void ExigirOutroGerente(SqliteConnection cx, Operador operador, Operador? autorizador)
    {
        if (autorizador is null || !autorizador.ESupervisor) throw new InvalidOperationException(Textos.PinDeOutroGerente);
        if (Operadores.IdCanonico(cx, autorizador.Id) == Operadores.IdCanonico(cx, operador.Id))
            throw new InvalidOperationException(Textos.ChameOutroGerente);
    }

    // ── FECHAMENTO COMO VENDA ───────────────────────────────────────────────

    /// <summary>O Pagamento abriu: a cobranca pode sair antes da venda. Limpo no Desistiu e no commit do Finalizar.</summary>
    public static void MarcarFechando(SqliteConnection cx, string id)
        => cx.Execute("UPDATE conta_aberta SET fechando_em = @Em WHERE id = @Id AND status = 'aberta'", new { Em = Agora, Id = id });

    /// <summary>"Voltar para a comanda": a conta continua carregada e o fechando_em sai.</summary>
    public static void LimparFechando(SqliteConnection cx, string id)
        => cx.Execute("UPDATE conta_aberta SET fechando_em = NULL WHERE id = @Id AND status = 'aberta'", new { Id = id });

    /// <summary>
    /// A linha da lista ganha "confira a maquininha"? Conta com fechando_em de pe e sem venda (o caixa caiu
    /// com o Pagamento aberto), ou cobranca sem venda no turno (Caixa.CobrancaSemVenda).
    /// </summary>
    public static bool PrecisaConferirMaquininha(SqliteConnection cx, Sessao sessao, ContaAberta conta)
        => (conta.FechandoEm is not null && conta.VendaId is null) || Cobrado(cx, sessao) is { Positivo: true };

    /// <summary>
    /// O aviso obrigatorio antes de "Fechar conta" quando pode haver dinheiro vivo na maquininha: o mesmo
    /// texto do rascunho (<see cref="Rascunho.AvisoDeCobranca"/>). Null = nada a avisar.
    /// </summary>
    public static string? AvisoDeMaquininha(SqliteConnection cx, Sessao sessao, ContaAberta conta)
    {
        var cobrado = Cobrado(cx, sessao);
        if ((conta.FechandoEm is not null && conta.VendaId is null) || cobrado is { Positivo: true })
            return Rascunho.AvisoDeCobranca(cobrado);
        return null;
    }

    private static Dinheiro? Cobrado(SqliteConnection cx, Sessao sessao)
    {
        try { return Caixa.CobrancaSemVenda(cx, sessao); }
        catch { return null; }   // nao deu para conferir: o aviso diz isso, nunca "nada foi cobrado"
    }

    /// <summary>
    /// PRIMEIRO passo da transacao do Finalizar, idempotente: UPDATE ... WHERE status='aberta'.
    ///  · 1 linha: fechou agora (venda_id aponta para a venda que o Finalizar vai inserir em seguida);
    ///  · 0 linhas e a conta esta fechada com venda finalizada: devolve a venda EXISTENTE (o Finalizar faz
    ///    rollback e a entrega: nada de V2, nada de segunda baixa, nada de segunda nota);
    ///  · cancelada ou inexistente: excecao antes de qualquer INSERT.
    /// </summary>
    public static FechamentoDaConta FecharNaVenda(SqliteConnection cx, SqliteTransaction tx, string contaId, string vendaId)
    {
        var n = cx.Execute("""
            UPDATE conta_aberta SET status = 'fechada', venda_id = @V, fechada_em = @Em, fechando_em = NULL, dono = NULL
             WHERE id = @Id AND status = 'aberta'
            """, new { V = vendaId, Em = Agora, Id = contaId }, tx);
        if (n == 1) return new FechamentoDaConta(true, null);

        var c = Ler(cx, contaId, tx) ?? throw new InvalidOperationException(Textos.NaoEncontrada);
        if (c.Status == "fechada" && c.VendaId is { } existente)
        {
            var v = cx.QueryFirstOrDefault(
                "SELECT id, client_key, numero_local, total_cent, business_date FROM venda WHERE id = @Id AND status = 'finalizada'",
                new { Id = existente }, tx);
            if (v is not null)
                return new FechamentoDaConta(false, new VendaGravada((string)v.id, (string)v.client_key, (long)v.numero_local,
                    new Dinheiro((long)v.total_cent), (string)v.business_date));
        }
        throw new InvalidOperationException(c.Status == "cancelada" ? Textos.JaFoiCancelada(c.Numero) : Textos.JaFoiFechada(c.Numero));
    }

    // ── CANCELAR ────────────────────────────────────────────────────────────

    /// <summary>
    /// Cancela a conta. Vazia: confirmacao simples na tela, sem PIN. Com QUALQUER item (pedido ou nao):
    /// motivo obrigatorio e PIN de OUTRO gerente. Grava cancel_*, audita (itens, valor de tabela, lotes,
    /// operador, autorizador) e enfileira conta_evento quando ha item ou e automatico. Lote ainda na fila
    /// e descartado pelo dreno antes do POST; o que ja esta na TV e aviso de boca.
    /// Devolve false quando ja estava cancelada (idempotente).
    /// </summary>
    public static bool Cancelar(SqliteConnection cx, string id, Operador operador, string? motivo, Operador? autorizador, bool automatico = false)
    {
        var c = Ler(cx, id) ?? throw new InvalidOperationException(Textos.NaoEncontrada);
        if (c.Status == "cancelada") return false;
        if (c.Status != "aberta") throw new InvalidOperationException(Textos.JaFoiFechada(c.Numero));
        var comItem = c.Itens.Count > 0;
        if (comItem && !automatico)
        {
            if (string.IsNullOrWhiteSpace(motivo)) throw new InvalidOperationException(Textos.MotivoObrigatorio);
            ExigirOutroGerente(cx, operador, autorizador);
        }
        var lotes = Lotes(cx, id);
        var idOp = Operadores.IdCanonico(cx, operador.Id);
        var idAut = autorizador is null ? null : Operadores.IdCanonico(cx, autorizador.Id);
        var evento = automatico ? "conta_cancelada_automatica" : comItem ? "conta_cancelada_com_itens" : "conta_cancelada_vazia";
        var mot = string.IsNullOrWhiteSpace(motivo) ? null : motivo.Trim();

        using var tx = cx.BeginTransaction();
        var n = cx.Execute("""
            UPDATE conta_aberta
               SET status = 'cancelada', cancelada_em = @Em, cancel_motivo = @M, cancel_operador = @O,
                   cancel_autorizador = @A, cancel_automatico = @Auto, dono = NULL, fechando_em = NULL
             WHERE id = @Id AND status = 'aberta'
            """, new { Em = Agora, M = mot, O = idOp, A = idAut, Auto = automatico ? 1 : 0, Id = id }, tx);
        if (n == 0) { tx.Rollback(); return false; }
        var estados = lotes.Select(l => $"{l.N}:{EstadoDoLote(cx, l.Id, tx)}");
        Caixa.Auditar(cx, tx, evento, idOp, idAut,
            $"comanda={c.Numero} nome={c.Nome ?? "(sem nome)"} itens={ResumoItens(c.Itens)} valor={c.ValorTabela.Formatado()}"
            + $" lotes={lotes.Count}{(lotes.Count > 0 ? " (" + string.Join(", ", estados) + ")" : "")}"
            + (mot is null ? "" : $" motivo={mot}"));
        if (!c.Teste && (comItem || automatico))
            EnfileirarEvento(cx, tx, evento, c, lotes, operador, autorizador, mot);
        tx.Commit();
        return true;
    }

    /// <summary>
    /// No fechamento do caixa: cancela sozinha as contas vazias SEM lote (a cozinha nao produziu nada). Com
    /// <paramref name="soMaisVelhasQue"/> (a regra opcional conta_vazia_horas), so as sem toque ha mais que
    /// isso. Conta carregada noutro Pdv.exe vivo fica. Devolve quantas cancelou.
    /// </summary>
    public static int CancelarVaziasAutomatico(SqliteConnection cx, Operador operador, TimeSpan? soMaisVelhasQue = null)
    {
        var n = 0;
        var agora = DateTime.Now;
        foreach (var c in Listar(cx, teste: false).Concat(Listar(cx, teste: true)))
        {
            if (c.Itens.Count > 0 || EmUsoPorOutroPdv(c)) continue;
            if (soMaisVelhasQue is { } prazo && agora - c.UltimoToqueEm < prazo) continue;
            if (cx.ExecuteScalar<int>("SELECT COUNT(*) FROM conta_lote WHERE conta_id = @C", new { C = c.Id }) > 0) continue;
            if (Cancelar(cx, c.Id, operador, soMaisVelhasQue is null ? "comanda vazia no fechamento do caixa" : "comanda vazia sem toque", null, automatico: true)) n++;
        }
        return n;
    }

    /// <summary>Config conta_vazia_horas como TimeSpan (null = 0 = so no fechamento do caixa).</summary>
    public static TimeSpan? PrazoContaVazia(SqliteConnection cx)
        => int.TryParse(Vendas.Config(cx, ChaveVaziaHoras), out var h) && h > 0 ? TimeSpan.FromHours(h) : null;

    /// <summary>Config mesas_quantidade (padrao 8, de 0 a 40).</summary>
    public static int Mesas(SqliteConnection cx)
        => int.TryParse(Vendas.Config(cx, ChaveMesas), out var m) ? Math.Clamp(m, 0, 40) : MesasPadrao;

    /// <summary>
    /// O caixa fechou com contas abertas: elas continuam para o proximo turno (nao bloqueia, nao
    /// transfere) e cada uma deixa um conta_evento conta_atravessou_fechamento. Devolve as que atravessam.
    /// </summary>
    public static IReadOnlyList<ContaAberta> RegistrarAtravessamento(SqliteConnection cx, Sessao sessao, Operador operador)
    {
        var abertas = Listar(cx, sessao.Teste);
        if (abertas.Count == 0) return abertas;
        var idOp = Operadores.IdCanonico(cx, operador.Id);
        using var tx = cx.BeginTransaction();
        foreach (var c in abertas)
        {
            Caixa.Auditar(cx, tx, "conta_atravessou_fechamento", idOp, null,
                $"comanda={c.Numero} nome={c.Nome ?? "(sem nome)"} itens={ResumoItens(c.Itens)} valor={c.ValorTabela.Formatado()} turno={sessao.BusinessDate}");
            if (!c.Teste) EnfileirarEvento(cx, tx, "conta_atravessou_fechamento", c, Lotes(cx, c.Id, tx), operador, null, $"ficou aberta no fechamento do turno {sessao.BusinessDate}");
        }
        tx.Commit();
        return abertas;
    }

    /// <summary>As contas canceladas no turno (desde a abertura, do mesmo modo) e as abertas agora, com o valor de tabela.</summary>
    public static ResumoContas ResumoDoTurno(SqliteConnection cx, Sessao sessao)
    {
        var canceladas = cx.Query<string>(
            "SELECT itens_json FROM conta_aberta WHERE status = 'cancelada' AND teste = @T AND cancelada_em >= @Desde",
            new { T = sessao.Teste ? 1 : 0, Desde = sessao.AberturaEm.ToString("o") }).ToList();
        var valorCanceladas = new Dinheiro(canceladas.Sum(j => ValorTabela(LerItens(j)).Centavos));
        var abertas = Listar(cx, sessao.Teste);
        var valorAbertas = new Dinheiro(abertas.Sum(c => c.ValorTabela.Centavos));
        return new ResumoContas(canceladas.Count, valorCanceladas, abertas.Count, valorAbertas);
    }

    /// <summary>As duas linhas para a tela do resultado e para o papel do fechamento (PapelDeCaixa).</summary>
    public static IReadOnlyList<string> LinhasDoFechamento(SqliteConnection cx, Sessao sessao)
    {
        var r = ResumoDoTurno(cx, sessao);
        return new[]
        {
            Textos.ComandasCanceladasNoTurno(r.CanceladasN, r.Canceladas),
            Textos.ComandasAbertas(r.AbertasN, r.Abertas),
        };
    }

    /// <summary>
    /// O turno de teste acabou (ModoHomologacao.EncerrarTurnoDeTeste): as contas de TESTE abertas morrem
    /// com ele, dentro da mesma transacao, sem fila (nada do teste sobe). So teste=1, nunca a conta real.
    /// </summary>
    public static int CancelarDeTeste(SqliteConnection cx, SqliteTransaction tx, string operadorId, string porque)
    {
        var contas = cx.Query("SELECT id, numero FROM conta_aberta WHERE status = 'aberta' AND teste = 1", transaction: tx).ToList();
        foreach (var c in contas)
        {
            cx.Execute("""
                UPDATE conta_aberta
                   SET status = 'cancelada', cancelada_em = @Em, cancel_motivo = @M, cancel_operador = @O,
                       cancel_automatico = 1, dono = NULL, fechando_em = NULL
                 WHERE id = @Id AND status = 'aberta' AND teste = 1
                """, new { Em = Agora, M = "teste encerrado: " + porque, O = operadorId, Id = (string)c.id }, tx);
            Caixa.Auditar(cx, tx, "conta_cancelada_automatica", operadorId, null, $"comanda={(long)c.numero} teste encerrado: {porque}");
        }
        return contas.Count;
    }

    // ── O RASTRO QUE SAI DO TERMINAL (conta_evento) ─────────────────────────

    /// <summary>
    /// Uma linha conta_evento na outbox, na transacao de quem chama. client_key = contaId:evento:seq (uma
    /// por fato). Payload: evento, loja, conta, itens (nome, produto, qtd, preco de tabela), valor de tabela,
    /// lotes com estado, operador, autorizador, motivo e hora. A RPC pdv_conta_evento so registra.
    /// </summary>
    public static void EnfileirarEvento(SqliteConnection cx, SqliteTransaction? tx, string evento, ContaAberta conta,
        IReadOnlyList<ContaLote> lotes, Operador? operador, Operador? autorizador, string? motivo)
    {
        var seq = cx.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM outbox WHERE tipo = @T AND ref_id = @R AND client_key LIKE @P",
            new { T = TipoEvento, R = conta.Id, P = $"{conta.Id}:{evento}:%" }, tx) + 1;
        var payload = new Dictionary<string, object?>
        {
            ["evento"] = evento,
            ["loja"] = NomeDaLoja(cx, tx),
            ["conta"] = new Dictionary<string, object?>
            {
                ["id"] = conta.Id, ["numero"] = conta.Numero, ["nome"] = conta.Nome, ["consumo"] = conta.Consumo,
                ["aberta_em"] = conta.AbertaEm.ToString("o"), ["business_date"] = conta.BusinessDate,
            },
            ["itens"] = conta.Itens.Select(i => new Dictionary<string, object?>
            {
                ["nome"] = i.Nome, ["produto_id"] = i.ProdutoId, ["qtd_milesimos"] = i.QtdMilesimos,
                ["preco_tabela_cent"] = i.PrecoCent, ["cozinha_milesimos"] = i.CozinhaMilesimos,
            }).ToList(),
            ["valor_tabela_cent"] = conta.ValorTabela.Centavos,
            ["lotes"] = lotes.Select(l => new Dictionary<string, object?>
            {
                ["n"] = l.N, ["criado_em"] = l.CriadoEm.ToString("o"), ["estado"] = EstadoDoLote(cx, l.Id, tx).ToString(),
            }).ToList(),
            ["operador"] = operador?.Nome,
            ["autorizador"] = autorizador?.Nome,
            ["motivo"] = motivo,
            ["hora"] = Agora,
        };
        Caixa.Enfileirar(cx, tx, TipoEvento, conta.Id, $"{conta.Id}:{evento}:{seq}", payload);
    }

    /// <summary>
    /// O que a Drenagem faz com uma linha conta_evento: chama a RPC pdv_conta_evento com p_client_key,
    /// p_store, p_evento e p_payload. 2xx = enviado; 404 PGRST202 (o dono ainda nao rodou o SQL) = espera,
    /// sem contar tentativa; o resto segue a regra geral da fila.
    /// </summary>
    public static async Task<(bool? Ok, string? Erro)> ResolverEventoNaFilaAsync(string clientKey, string payload,
        Func<string, string, Task<(int Status, string? Corpo)>> rpc)
    {
        JsonElement raiz;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (false, "evento da comanda ilegível na fila");
            raiz = doc.RootElement.Clone();
        }
        catch { return (false, "evento da comanda ilegível na fila"); }
        string? S(string n) => raiz.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var corpo = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["p_client_key"] = clientKey,
            ["p_store"] = S("loja"),
            ["p_evento"] = S("evento"),
            ["p_payload"] = raiz,
        });
        var (status, resposta) = await rpc(RpcEvento, corpo).ConfigureAwait(false);
        if (Drenagem.RpcAusente(status, resposta))
            return (null, $"a nuvem ainda não tem {RpcEvento} (rodar aplicar-hoje/pdv_conta_evento.sql)");
        if (status is >= 200 and < 300) return (true, null);
        return Drenagem.DesfechoDeStatus(status, resposta);
    }

    // ── apoio ───────────────────────────────────────────────────────────────

    private static string? NomeDaLoja(SqliteConnection cx, SqliteTransaction? tx)
    {
        try { return cx.ExecuteScalar<string?>("SELECT loja_nome FROM terminal WHERE id = 1", transaction: tx); }
        catch { return null; }
    }

    /// <summary>Toda linha ganha LinhaId (Guid N) ao entrar; CozinhaMilesimos nunca passa da quantidade.</summary>
    private static List<ItemRascunho> ComLinhaId(IEnumerable<ItemRascunho> itens)
        => itens.Select(i => i with
        {
            LinhaId = string.IsNullOrWhiteSpace(i.LinhaId) ? Guid.NewGuid().ToString("N") : i.LinhaId,
            CozinhaMilesimos = Math.Clamp(i.CozinhaMilesimos, 0, Math.Max(0, i.QtdMilesimos)),
        }).ToList();

    private static string Qtd(long milesimos) => (milesimos / 1000m).ToString("0.###", CultureInfo.GetCultureInfo("pt-BR"));

    private static string ResumoItens(IEnumerable<ItemRascunho> itens)
        => string.Join(", ", itens.Select(i => $"{Qtd(i.QtdMilesimos)}x {i.Nome}"));

    private static string ResumoItens(IEnumerable<ItemDoLote> itens)
        => string.Join(", ", itens.Select(i => $"{Qtd(i.QtdMilesimos)}x {i.Nome}"));

    private static List<ItemRascunho> LerItens(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<ItemRascunho>>(json) ?? new(); }
        catch { return new(); }   // JSON ilegivel: a conta aparece sem itens, nunca derruba a tela
    }

    private static DateTime? Data(string? s)
        => s is { Length: > 0 } && DateTime.TryParse(s, null, DateTimeStyles.RoundtripKind, out var d) ? d : null;

    private static ContaAberta Mapear(dynamic l)
    {
        var cobertura = (string?)l.cortesia_json is { Length: > 0 } j
            ? (JsonSerializer.Deserialize<Dictionary<string, int>>(j) ?? new()) : new Dictionary<string, int>();
        return new ContaAberta(
            (string)l.id, (long)l.numero, (string?)l.nome, (string)l.consumo, (string)l.status,
            LerItens((string?)l.itens_json), (string?)l.cortesia_codigo, cobertura,
            (string)l.operador_id, (string)l.sessao_abertura, (string)l.business_date, (long)l.teste == 1,
            Data((string?)l.aberta_em) ?? DateTime.Now, Data((string?)l.atualizado_em) ?? DateTime.Now,
            Data((string?)l.ultimo_toque_em) ?? DateTime.Now, Data((string?)l.fechando_em),
            Data((string?)l.fechada_em), (string?)l.venda_id,
            Data((string?)l.cancelada_em), (string?)l.cancel_motivo, (string?)l.cancel_operador, (string?)l.cancel_autorizador,
            (long)l.cancel_automatico == 1, (string?)l.dono);
    }

    private static ContaLote MapearLote(dynamic l)
    {
        List<ItemDoLote> itens;
        try { itens = JsonSerializer.Deserialize<List<ItemDoLote>>((string)l.itens_json) ?? new(); }
        catch { itens = new(); }
        List<RetiradaDoLote> retirados = new();
        if ((string?)l.itens_retirados_json is { Length: > 0 } rj)
            try { retirados = JsonSerializer.Deserialize<List<RetiradaDoLote>>(rj) ?? new(); } catch { }
        return new ContaLote((string)l.id, (string)l.conta_id, (int)(long)l.n, (string)l.consumo, itens, retirados,
            (string?)l.substituido_por, (long)l.homologacao == 1, (string)l.operador_id,
            Data((string?)l.criado_em) ?? DateTime.Now);
    }
}
