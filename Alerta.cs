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
    private static DateTime _ultimoChat = DateTime.MinValue;

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

    /// <summary>
    /// Mensagem nova no chat: um aviso CURTO e DISCRETO (dois toques leves),
    /// distinto do pedido novo — o operador reconhece sem olhar. Mesma proteção
    /// contra metralhadora do pedido.
    /// </summary>
    public static void MensagemChat()
    {
        if ((DateTime.Now - _ultimoChat).TotalSeconds < 8) return;
        _ultimoChat = DateTime.Now;

        _ = Task.Run(() =>
        {
            try
            {
                Console.Beep(1046, 110);
                Console.Beep(784, 150);
            }
            catch { /* sem som: o toast e o selo continuam avisando */ }
        });
    }

    private static DateTime _ultimoWhatsApp = DateTime.MinValue;

    /// <summary>
    /// Mensagem nova no WhatsApp: um toque curto de dois tons (Recursos\sons\whatsapp.wav,
    /// gerado por nós), distinto do chat do iFood e do pedido novo. A loja pode trocar
    /// pelo próprio .wav (ver <see cref="Pdv.Nucleo.SomWhatsApp"/>). Mesma proteção
    /// contra metralhadora; máquina sem som não derruba nada.
    /// </summary>
    public static void MensagemWhatsApp()
    {
        if ((DateTime.Now - _ultimoWhatsApp).TotalSeconds < 6) return;
        _ultimoWhatsApp = DateTime.Now;

        _ = Task.Run(() =>
        {
            try
            {
                var dados = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PdvNativo");
                var proprio = Pdv.Nucleo.SomWhatsApp.ArquivoDaLoja(dados);
                if (proprio is not null)
                {
                    using var p = new System.Media.SoundPlayer(proprio);
                    p.PlaySync();
                    return;
                }
                using var fluxo = typeof(Alerta).Assembly.GetManifestResourceStream(Pdv.Nucleo.SomWhatsApp.RecursoEmbarcado);
                if (fluxo is null) { Console.Beep(1046, 110); Console.Beep(1568, 200); return; }
                using var som = new System.Media.SoundPlayer(fluxo);
                som.PlaySync();
            }
            catch { /* sem som: o toast e o selo continuam avisando */ }
        });
    }
}
