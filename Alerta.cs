namespace Pdv;

/// <summary>
/// Os sons do caixa. Balcão de loja é barulhento: cada aviso tem um padrão
/// distintivo, não um "plim" do sistema que some no liquidificador.
///
/// Console.Beep é síncrono: roda fora da UI para o quadro não travar. Máquina
/// sem dispositivo de som não pode derrubar o caixa: engole e segue.
///
/// A LOJA PODE TROCAR QUALQUER UM DELES sem atualizar o programa: um .wav em
/// C:\ProgramData\PdvNativo\sons\ (ver <see cref="Pdv.Nucleo.SomDaLoja"/>) vale por
/// cima do som próprio. O caixa não embarca áudio de terceiros (o toque do
/// WhatsApp é deles, o do ICQ é deles); quem tem o arquivo põe na pasta.
/// </summary>
public static class Alerta
{
    private static DateTime _ultimo = DateTime.MinValue;
    private static DateTime _ultimoChat = DateTime.MinValue;
    private static DateTime _ultimoWhatsApp = DateTime.MinValue;
    private static DateTime _ultimoCaiu = DateTime.MinValue;

    private static string PastaDados =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PdvNativo");

    /// <summary>Toca o .wav da loja se houver. Devolve false para o chamador tocar o som próprio.</summary>
    private static bool TocarDaLoja(string nome)
    {
        var proprio = Pdv.Nucleo.SomDaLoja.Arquivo(PastaDados, nome);
        if (proprio is null) return false;
        try
        {
            using var p = new System.Media.SoundPlayer(proprio);
            p.PlaySync();
            return true;
        }
        catch { return false; }   // .wav que o Windows não toca: os beeps entram no lugar
    }

    /// <summary>Pedido novo do iFood: três toques, o do meio mais grave. Ou sons\pedido.wav.</summary>
    public static void PedidoNovo()
    {
        // Duas puxadas quase juntas (Venda + KDS abertos) não viram metralhadora.
        if ((DateTime.Now - _ultimo).TotalSeconds < 8) return;
        _ultimo = DateTime.Now;

        _ = Task.Run(() =>
        {
            try
            {
                if (TocarDaLoja(Pdv.Nucleo.SomDaLoja.PedidoNovo)) return;
                // 11/09/2026 (dono): "duração de 1 a 2 segundos". Era 0,7 s e se perdia no
                // barulho do balcão. O padrão de três toques repete uma vez: 1,6 s.
                Console.Beep(880, 180);
                Console.Beep(660, 180);
                Console.Beep(880, 360);
                Thread.Sleep(120);
                Console.Beep(880, 180);
                Console.Beep(660, 180);
                Console.Beep(880, 400);
            }
            catch { /* sem som na máquina: o toast e o badge continuam avisando */ }
        });
    }

    /// <summary>
    /// Mensagem nova no chat do iFood: um aviso CURTO e DISCRETO (dois toques leves),
    /// distinto do pedido novo. Ou sons\ifood-chat.wav, que a loja escolhe (11/09/2026:
    /// o dono quer o "uh-oh" do ICQ; o arquivo é dele, a pasta é esta).
    /// </summary>
    public static void MensagemChat()
    {
        if ((DateTime.Now - _ultimoChat).TotalSeconds < 8) return;
        _ultimoChat = DateTime.Now;

        _ = Task.Run(() =>
        {
            try
            {
                if (TocarDaLoja(Pdv.Nucleo.SomDaLoja.ChatIfood)) return;
                Console.Beep(1046, 110);
                Console.Beep(784, 150);
            }
            catch { /* sem som: o toast e o selo continuam avisando */ }
        });
    }

    /// <summary>
    /// Mensagem nova no WhatsApp. Toca SEMPRE (11/09/2026, dono: "aumentar o som, temos som
    /// ambiente"): o toque embarcado é alto e duplo (Recursos\sons\whatsapp.wav, gerado por
    /// nós, 1 s), e sons\whatsapp.wav da loja vale por cima. O da página, quando toca, soma.
    /// </summary>
    public static void MensagemWhatsApp()
    {
        if ((DateTime.Now - _ultimoWhatsApp).TotalSeconds < 6) return;
        _ultimoWhatsApp = DateTime.Now;

        _ = Task.Run(() =>
        {
            try
            {
                if (TocarDaLoja(Pdv.Nucleo.SomDaLoja.WhatsApp)) return;
                using var fluxo = typeof(Alerta).Assembly.GetManifestResourceStream(Pdv.Nucleo.SomWhatsApp.RecursoEmbarcado);
                if (fluxo is null) { Console.Beep(1046, 110); Console.Beep(1568, 200); return; }
                using var som = new System.Media.SoundPlayer(fluxo);
                som.PlaySync();
            }
            catch { /* sem som: o toast e o selo continuam avisando */ }
        });
    }

    /// <summary>
    /// O WhatsApp CAIU (tela do QR): três toques descendentes, diferentes de "chegou
    /// mensagem" para o operador não confundir. A cadência (primeiro aviso, repetição)
    /// é da vigia no Núcleo; aqui só o freio de 6 s contra toque dobrado.
    /// </summary>
    public static void WhatsAppCaiu()
    {
        if ((DateTime.Now - _ultimoCaiu).TotalSeconds < 6) return;
        _ultimoCaiu = DateTime.Now;

        _ = Task.Run(() =>
        {
            try
            {
                Console.Beep(988, 160);
                Console.Beep(784, 160);
                Console.Beep(587, 320);
            }
            catch { /* sem som: o selo e o aviso continuam na tela */ }
        });
    }
}
