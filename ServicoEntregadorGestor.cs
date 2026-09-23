using Pdv.Nucleo;

namespace Pdv;

/// <summary>
/// O NOME DO ENTREGADOR, LIDO DO GESTOR DE PEDIDOS (23/09/2026, pedido do dono).
///
/// "Ter o nome do entregador de cada pedido, para conferir o print da avaliação que o cliente
/// manda." O caixa já mantém o Gestor logado num WebView2 o dia inteiro; dentro do navegador
/// dele, cada pedido guardado em <c>localStorage['order/&lt;uuid&gt;']</c> tem o evento
/// ASSIGN_DRIVER com o nome do entregador E o mesmo identificador que a nossa integração já
/// recebe nos eventos de coleta. Esse evento NUNCA chegou pela API (0 de 40.291 eventos medidos
/// em 23/09): é por isso que o nome não existe no banco, e é por isso que a leitura é aqui.
///
/// ISTO RODA EM SILÊNCIO. Não tem tela, não pergunta nada, não imprime e não avisa ninguém.
/// Quem manda é a fila (Drenagem), que é a mesma rede de segurança da venda: sem internet o lote
/// espera, e sobe quando a rede voltar.
///
/// SÓ LÊ. Nada é escrito no Gestor. O que sai daqui é o que o cliente já vê na tela dele (o
/// primeiro nome com a inicial), o número do pedido e os identificadores; nada de telefone,
/// endereço, item de pedido ou qualquer segredo da sessão.
/// </summary>
public static class ServicoEntregadorGestor
{
    /// <summary>De quanto em quanto tempo a tela relê o localStorage do Gestor.</summary>
    public static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(5);

    private static readonly SemaphoreSlim UmaPorVez = new(1, 1);

    private static string? _loja;
    private static DateTime _lojaAte = DateTime.MinValue;

    /// <summary>
    /// A loja deste terminal. Em cache por 60 s: pareamento de terminal não muda no meio do
    /// expediente, e abrir o banco a cada leitura é custo à toa.
    ///
    /// Ela vai no corpo, mas quem decide a loja é o SERVIDOR: para o terminal comum vale a loja do
    /// cadastro do usuário, e cada pedido do lote ainda é conferido contra ela. Um caixa da
    /// Savassi com a conta do Gestor que enxerga as duas lojas não consegue, por este caminho,
    /// gravar nada em nome do Castelo.
    /// </summary>
    private static string? Loja()
    {
        if (DateTime.Now < _lojaAte) return _loja;
        try
        {
            using var cx = Banco.Abrir();
            _loja = Dapper.SqlMapper.ExecuteScalar<string?>(cx, "SELECT loja_nome FROM terminal LIMIT 1");
        }
        catch { _loja = null; }
        _lojaAte = DateTime.Now.AddSeconds(60);
        return _loja;
    }

    /// <summary>
    /// O que a tela do chat entrega a cada leitura: os pedidos crus que ela achou no localStorage
    /// do Gestor, já virados em fatos pelo núcleo. Grava o que é novo, enfileira UM lote e cutuca
    /// a fila. Devolve quantos fatos entraram (0 é o caso normal: nenhum entregador novo).
    ///
    /// NUNCA LANÇA: uma leitura de conforto não pode derrubar o caixa.
    /// </summary>
    public static int Ouvir(IReadOnlyList<EntregadorNoPedido> lidos)
    {
        try
        {
            if (lidos.Count == 0) return 0;
            if (!UmaPorVez.Wait(0)) return 0;
            try
            {
                var quantos = EntregadorGestor.Registrar(lidos, Loja());
                if (quantos > 0) Servicos.Dreno()?.Cutucar();
                return quantos;
            }
            finally { UmaPorVez.Release(); }
        }
        catch { return 0; }
    }
}
