namespace Pdv.Nucleo;

/// <summary>
/// De onde sai o toque de mensagem nova do WhatsApp no caixa (11/09/2026).
///
/// O dono quer o toque ORIGINAL do WhatsApp. Áudio de terceiros não entra no exe (o
/// toque é deles, e vídeo não se baixa), mas ele existe onde tem que existir: é a
/// própria página do WhatsApp Web que o toca quando chega mensagem. Por isso a
/// ordem é: (1) a página toca o dela; (2) se ela ficar calada, o caixa toca o .wav
/// que a loja deixou em <see cref="Caminho"/>; (3) sem esse arquivo, o toque próprio
/// embarcado (dois tons curtos, gerado por nós). Puro: quem toca é Alerta, no exe.
/// A mesma pasta serve para os outros sons do caixa: ver <see cref="SomDaLoja"/>.
/// </summary>
public static class SomWhatsApp
{
    /// <summary>Nome do recurso embarcado (LogicalName no Pdv.csproj).</summary>
    public const string RecursoEmbarcado = "Pdv.sons.whatsapp.wav";

    /// <summary>Onde a loja pode deixar o SEU som: &lt;dados&gt;\sons\whatsapp.wav.</summary>
    public static string Caminho(string pastaDados) => SomDaLoja.Caminho(pastaDados, SomDaLoja.WhatsApp);

    /// <summary>O .wav da loja se existir e tiver conteúdo; senão null, e vale o embarcado.</summary>
    public static string? ArquivoDaLoja(string pastaDados) => SomDaLoja.Arquivo(pastaDados, SomDaLoja.WhatsApp);

    /// <summary>Quanto o caixa espera a página tocar antes de tocar a reserva.</summary>
    public static readonly TimeSpan EsperaPelaPagina = TimeSpan.FromMilliseconds(1800);

    /// <summary>Quanto ANTES da leitura da mensagem um áudio da página ainda conta como "foi ela".</summary>
    public static readonly TimeSpan FolgaAntes = TimeSpan.FromSeconds(4);

    /// <summary>
    /// A página já cobriu o aviso sonoro? Sim quando ela emitiu áudio pouco antes da
    /// mensagem ser lida (o título muda depois do som) ou em qualquer instante depois.
    /// </summary>
    public static bool PaginaCobre(DateTime paginaTocouEm, DateTime mensagemEm)
        => paginaTocouEm != DateTime.MinValue && paginaTocouEm >= mensagemEm - FolgaAntes;
}

/// <summary>
/// OS SONS DA LOJA (11/09/2026, pedido do dono: o toque do WhatsApp num som, o do chat
/// do iFood em outro, "sem ter que abrir a tela do chat"). O caixa não embarca áudio de
/// terceiros; a loja deixa o .wav dela em <c>&lt;dados&gt;\sons\&lt;nome&gt;.wav</c> e
/// ele vale na hora, sem atualizar o programa:
///  · whatsapp.wav    mensagem nova no WhatsApp (reserva: a página toca o original)
///  · ifood-chat.wav  mensagem nova no chat do iFood
///  · pedido.wav      pedido novo do iFood
/// Sem o arquivo, vale o som próprio do caixa (beeps ou o .wav embarcado).
/// </summary>
public static class SomDaLoja
{
    public const string WhatsApp = "whatsapp";
    public const string ChatIfood = "ifood-chat";
    public const string PedidoNovo = "pedido";

    public static string Pasta(string pastaDados) => Path.Combine(pastaDados, "sons");
    public static string Caminho(string pastaDados, string nome) => Path.Combine(Pasta(pastaDados), nome + ".wav");

    /// <summary>O .wav da loja se existir e tiver conteúdo (mais do que o cabeçalho); senão null.</summary>
    public static string? Arquivo(string pastaDados, string nome)
    {
        try
        {
            var p = Caminho(pastaDados, nome);
            if (!File.Exists(p) || new FileInfo(p).Length <= 44) return null;   // 44 = só o cabeçalho WAV
            // Só WAV de verdade ("RIFF....WAVE"): um MP3 renomeado faria o SoundPlayer
            // lançar e o caixa ficar mudo sem ninguém saber por quê.
            using var f = File.OpenRead(p);
            var cab = new byte[12];
            if (f.Read(cab, 0, 12) < 12) return null;
            return System.Text.Encoding.ASCII.GetString(cab, 0, 4) == "RIFF"
                && System.Text.Encoding.ASCII.GetString(cab, 8, 4) == "WAVE" ? p : null;
        }
        catch { return null; }
    }
}
