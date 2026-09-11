using System.Text.RegularExpressions;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O WHATSAPP DA LOJA DENTRO DO CAIXA (11/09/2026, pedido do dono: "embarcar o
/// web.whatsapp.com no PDV, com o QR, e avisar no caixa sempre que chegar mensagem").
///
/// O WebView2 não é testável aqui (precisa do Chromium e da conta logada). O que
/// quebra o aviso foi extraído para o Núcleo e é exercitado:
///  - o número de não lidas a partir do TÍTULO da aba ("(3) WhatsApp");
///  - "só avisa na subida" no serviço próprio do WhatsApp (separado do chat do iFood);
///  - de onde sai o som (o .wav da loja manda; sem ele, o embarcado);
///  - e, pela fonte, que a venda tem selo, aviso e botão, que o MainWindow hospeda a
///    camada, e que a tela nega notificação do navegador (o aviso é o do caixa).
/// </summary>
public static class TestesWhatsApp
{
    public static void Rodar(Action<bool, string> checar)
    {
        // ── título da aba ─────────────────────────────────────────────────────
        checar(WhatsAppContagem.LerTitulo("(3) WhatsApp") == 3, "título '(3) WhatsApp' = 3");
        checar(WhatsAppContagem.LerTitulo("(12) WhatsApp Web") == 12, "título '(12) WhatsApp Web' = 12");
        checar(WhatsAppContagem.LerTitulo("  (1) WhatsApp") == 1, "espaço antes do número não atrapalha");
        checar(WhatsAppContagem.LerTitulo("WhatsApp") == 0, "sem parêntese = 0 (nada a ler)");
        checar(WhatsAppContagem.LerTitulo("WhatsApp (3)") == 0, "número no FIM não conta (não é o formato do WhatsApp)");
        checar(WhatsAppContagem.LerTitulo(null) == 0 && WhatsAppContagem.LerTitulo("") == 0, "nulo e vazio = 0");
        checar(WhatsAppContagem.LerTitulo("(99999) WhatsApp") == 9999, "teto de segurança em 9999");

        // ── só avisa na subida, no serviço PRÓPRIO do WhatsApp ────────────────
        {
            var mudou = new List<int>(); var novas = new List<int>();
            void M(int n) => mudou.Add(n); void N(int n) => novas.Add(n);
            ServicoWhatsApp.Mudou += M; ServicoWhatsApp.MensagemNova += N;
            try
            {
                ServicoWhatsApp.Recomecar();
                mudou.Clear(); novas.Clear();
                ServicoWhatsApp.ReportarTitulo("(2) WhatsApp");
                checar(mudou.SequenceEqual(new[] { 2 }) && novas.Count == 0 && ServicoWhatsApp.NaoLidas == 2,
                    "primeira leitura com 2 não lidas: selo acende, mas não avisa (linha de base)");
                ServicoWhatsApp.ReportarTitulo("(5) WhatsApp");
                checar(novas.SequenceEqual(new[] { 5 }), "subiu para 5: avisa uma vez, com o total");
                ServicoWhatsApp.ReportarTitulo("(1) WhatsApp");
                checar(novas.Count == 1 && ServicoWhatsApp.NaoLidas == 1, "caiu para 1 (leu mensagens): selo muda, não avisa");
                ServicoWhatsApp.ReportarTitulo("WhatsApp");
                checar(ServicoWhatsApp.NaoLidas == 0 && mudou[^1] == 0, "título sem número zera o selo");
                ServicoWhatsApp.ReportarTitulo("(1) WhatsApp");
                checar(novas.Count == 2 && novas[^1] == 1, "de 0 para 1 avisa de novo");
                ServicoWhatsApp.Recomecar();
                ServicoWhatsApp.Reportar(4);
                checar(novas.Count == 2, "depois de Recarregar, a primeira leitura é linha de base (não avisa)");
            }
            finally { ServicoWhatsApp.Mudou -= M; ServicoWhatsApp.MensagemNova -= N; ServicoWhatsApp.Recomecar(); }
        }

        // ── o som: o .wav da loja manda; sem ele, o embarcado ────────────────
        {
            var dados = Path.Combine(Path.GetTempPath(), "pdv-teste-wa-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dados);
            try
            {
                checar(SomWhatsApp.Caminho(dados).EndsWith(Path.Combine("sons", "whatsapp.wav"), StringComparison.OrdinalIgnoreCase),
                    "o som da loja mora em <dados>\\sons\\whatsapp.wav");
                checar(SomWhatsApp.ArquivoDaLoja(dados) is null, "sem arquivo: null (vale o embarcado)");
                Directory.CreateDirectory(Path.Combine(dados, "sons"));
                File.WriteAllBytes(SomWhatsApp.Caminho(dados), new byte[10]);
                checar(SomWhatsApp.ArquivoDaLoja(dados) is null, "arquivo vazio/quebrado não vale (só cabeçalho)");
                File.WriteAllBytes(SomWhatsApp.Caminho(dados), new byte[1000]);
                checar(SomWhatsApp.ArquivoDaLoja(dados) is null, "mil bytes de nada não são um .wav (MP3 renomeado não vale)");
                var wav = new byte[1000];
                System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
                System.Text.Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
                File.WriteAllBytes(SomWhatsApp.Caminho(dados), wav);
                checar(SomWhatsApp.ArquivoDaLoja(dados) == SomWhatsApp.Caminho(dados), "com um .wav de verdade (RIFF/WAVE), é ele que toca");
            }
            finally { try { Directory.Delete(dados, true); } catch { } }

            var csproj = Fonte("Pdv.csproj") ?? "";
            checar(csproj.Contains("Recursos\\sons\\whatsapp.wav", StringComparison.Ordinal)
                   && csproj.Contains("LogicalName=\"" + SomWhatsApp.RecursoEmbarcado + "\"", StringComparison.Ordinal),
                "o toque embarcado está no csproj com o nome que o Alerta procura");
            checar(File.Exists(CaminhoNoRepo(Path.Combine("Recursos", "sons", "whatsapp.wav"))),
                "e o arquivo do toque existe no repositório");
        }

        // ── as telas ──────────────────────────────────────────────────────────
        {
            var venda = Fonte(Path.Combine("Telas", "Venda.xaml")) ?? "";
            var vendaCs = Fonte(Path.Combine("Telas", "Venda.xaml.cs")) ?? "";
            var main = Fonte("MainWindow.xaml") ?? "";
            var mainCs = Fonte("MainWindow.xaml.cs") ?? "";
            var tela = Fonte(Path.Combine("Telas", "ChatWhatsApp.xaml.cs")) ?? "";
            var telaXaml = Fonte(Path.Combine("Telas", "ChatWhatsApp.xaml")) ?? "";

            checar(venda.Contains("Click=\"AbrirWhatsApp\"") && venda.Contains("x:Name=\"BadgeWhatsApp\"")
                   && venda.Contains("x:Name=\"ToastWhatsApp\"") && venda.Contains("AbrirWhatsAppPeloToast"),
                "a venda tem o botão WhatsApp com selo, e o aviso que abre a aba");
            checar(vendaCs.Contains("ServicoWhatsApp.Mudou += AtualizarSeloWhatsApp") && vendaCs.Contains("ServicoWhatsApp.Mudou -= AtualizarSeloWhatsApp")
                   && vendaCs.Contains("ServicoWhatsApp.TocarSeAPaginaCalar(Alerta.MensagemWhatsApp)"),
                "a venda escuta o serviço ao entrar, solta ao sair, e na subida toca a reserva só se a página calar");
            checar(main.Contains("x:Name=\"CamadaWhatsApp\"") && mainCs.Contains("t.PediuWhatsApp += MostrarWhatsApp")
                   && mainCs.Contains("CamadaWhatsApp.PreAquecerAsync()"),
                "o MainWindow hospeda a camada viva e a pré-aquece (selo antes de abrir a aba)");
            checar(tela.Contains("https://web.whatsapp.com/") && tela.Contains("\"webview-whatsapp\"")
                   && tela.Contains("CoreWebView2PermissionKind.Notifications") && tela.Contains("CoreWebView2PermissionState.Deny"),
                "a tela abre o WhatsApp Web num perfil próprio e nega a notificação do navegador");
            checar(tela.Contains("tipo: 'naolidas'") && tela.Contains("document.title") && tela.Contains("ServicoWhatsApp.Reportar("),
                "o contador lê o título e entrega ao serviço");

            var semComentario = Regex.Replace(venda + telaXaml, "<!--.*?-->", "", RegexOptions.Singleline);
            var novo = semComentario.Contains("WhatsApp") ? semComentario : "";
            checar(!Regex.IsMatch(Regex.Replace(novo, "<!--.*?-->", ""), "Text=\"[^\"]*[—–][^\"]*\""),
                "nenhum travessão nos textos novos das telas");
        }
    }

    private static string? Fonte(string relativo)
    {
        var c = CaminhoNoRepo(relativo);
        return c is not null && File.Exists(c) ? File.ReadAllText(c) : null;
    }

    private static string? CaminhoNoRepo(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj")))
                return Path.Combine(dir.FullName, relativo);
        return null;
    }
}
