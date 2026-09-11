namespace Pdv.Nucleo;

/// <summary>
/// De onde sai o toque de mensagem nova do WhatsApp no caixa (11/09/2026).
///
/// O dono pediu o som de um vídeo. Áudio de terceiros não entra no exe (o toque do
/// app é deles, e vídeo não se baixa); o caixa traz um toque próprio, gerado por
/// nós (Recursos\sons\whatsapp.wav, dois tons curtos). A loja que quiser outro som
/// deixa um .wav no caminho de <see cref="Caminho"/> e ele passa a valer na hora,
/// sem atualizar o programa. Puro: quem toca é Alerta, no exe.
/// </summary>
public static class SomWhatsApp
{
    /// <summary>Nome do recurso embarcado (LogicalName no Pdv.csproj).</summary>
    public const string RecursoEmbarcado = "Pdv.sons.whatsapp.wav";

    /// <summary>Onde a loja pode deixar o SEU som: &lt;dados&gt;\sons\whatsapp.wav.</summary>
    public static string Caminho(string pastaDados) => Path.Combine(pastaDados, "sons", "whatsapp.wav");

    /// <summary>O .wav da loja se existir e tiver conteúdo; senão null, e vale o embarcado.</summary>
    public static string? ArquivoDaLoja(string pastaDados)
    {
        try
        {
            var p = Caminho(pastaDados);
            return File.Exists(p) && new FileInfo(p).Length > 44 ? p : null;   // 44 = só o cabeçalho WAV
        }
        catch { return null; }
    }
}
