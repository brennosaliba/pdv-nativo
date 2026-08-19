namespace Pdv;

/// <summary>
/// Som de pedido novo. Balcão de loja é barulhento: precisa de um padrão
/// distintivo (três toques, o do meio mais grave), não de um "plim" do sistema
/// que some no liquidificador.
///
/// Console.Beep é síncrono — roda fora da UI para o quadro não travar. Máquina
/// sem dispositivo de som não pode derrubar o caixa: engole e segue.
/// </summary>
public static class Alerta
{
    private static DateTime _ultimo = DateTime.MinValue;

    public static void PedidoNovo()
    {
        // Duas puxadas quase juntas (Venda + KDS abertos) não viram metralhadora.
        if ((DateTime.Now - _ultimo).TotalSeconds < 8) return;
        _ultimo = DateTime.Now;

        _ = Task.Run(() =>
        {
            try
            {
                Console.Beep(880, 180);
                Console.Beep(660, 180);
                Console.Beep(880, 350);
            }
            catch { /* sem som na máquina: o toast e o badge continuam avisando */ }
        });
    }
}
