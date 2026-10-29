using System.Text;
using System.Xml.Linq;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A BARRA DO WINDOWS NO CAIXA (1.0.58, 29/10/2026, pedido do dono): "ver a barra de tarefa sem menu
/// iniciar nem nada, somente configuração de wifi e som". No quiosque não há Explorer, então não há
/// barra, Wi-Fi nem volume do Windows: o PDV desenha a dele.
///
/// Puro: quando aparece, a lista de redes, a rede que dá para conectar daqui, a senha, o perfil que o
/// Windows grava, o volume e os textos. O exe (wlanapi, Core Audio, a tela) fica travado pelo fonte, e
/// a leitura de verdade deste PC roda uma vez só para provar que não lança.
/// </summary>
public static class TestesBarraWindows
{
    public static void Rodar(Action<bool, string> checar)
    {
        // ── quando aparece ────────────────────────────────────────────────────
        checar(BarraDoWindows.Mostrar(null, windowsSemBarra: true) && !BarraDoWindows.Mostrar(null, windowsSemBarra: false),
            "BW-01 sem escolha: aparece só com o Windows sem a barra dele (o quiosque)");
        checar(BarraDoWindows.Mostrar("1", false) && BarraDoWindows.Mostrar("1", true),
            "BW-02 marcada na Configuração: aparece sempre, até com o Explorer aberto");
        checar(!BarraDoWindows.Mostrar("0", true) && !BarraDoWindows.Mostrar("0", false),
            "BW-03 desmarcada: nunca aparece, nem no quiosque");
        checar(BarraDoWindows.Mostrar("lixo", true) && !BarraDoWindows.Mostrar("", false),
            "BW-04 valor estranho vale como sem escolha");
        checar(BarraDoWindows.MarcadaNaConfiguracao(null, true) && !BarraDoWindows.MarcadaNaConfiguracao(null, false)
               && BarraDoWindows.MarcadaNaConfiguracao("1", false) && !BarraDoWindows.MarcadaNaConfiguracao("0", true),
            "BW-05 a caixa da Configuração nasce marcada no caixa em quiosque");
        checar(BarraDoWindows.Chave == "barra_windows", "BW-06 a chave de config é estável");

        // ── a lista de redes ──────────────────────────────────────────────────
        static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
        var bruto = new[]
        {
            new BarraDoWindows.Rede(B("Vizinho"), 90, true, 7, 4, null, false),
            new BarraDoWindows.Rede(B("AmericanDay"), 40, true, 7, 4, null, false),
            new BarraDoWindows.Rede(B("AmericanDay"), 55, true, 7, 4, "AmericanDay", true),
            new BarraDoWindows.Rede(Array.Empty<byte>(), 99, true, 7, 4, null, false),      // rede oculta
            new BarraDoWindows.Rede(B("Cafe Gratis"), 70, false, 1, 0, null, false),
            new BarraDoWindows.Rede(B("Loja 5G"), 70, true, 7, 4, "Loja 5G", false),
        };
        var lista = BarraDoWindows.Organizar(bruto);
        checar(lista.Count == 4 && lista.All(r => r.Nome.Length > 0), "BW-07 rede oculta sai e o mesmo nome vira uma linha só");
        checar(lista[0].Nome == "AmericanDay" && lista[0].Conectada && lista[0].Salva && lista[0].Sinal == 55,
            "BW-08 a conectada vem primeiro, com o perfil salvo e o maior sinal das duas");
        checar(lista[1].Nome == "Vizinho" && lista[2].Nome == "Cafe Gratis" && lista[3].Nome == "Loja 5G",
            "BW-09 o resto pelo sinal (empate pelo nome)");
        checar(BarraDoWindows.Rotulo(lista[0]) == "Conectado" && BarraDoWindows.Rotulo(lista[1]) == "Com senha"
               && BarraDoWindows.Rotulo(lista[2]) == "Aberta" && BarraDoWindows.Rotulo(lista[3]) == "Salva",
            "BW-10 o que a linha diz à direita");
        checar(BarraDoWindows.Organizar(Array.Empty<BarraDoWindows.Rede>()).Count == 0, "BW-11 lista vazia, sem exceção");

        checar(BarraDoWindows.Arcos(0) == 0 && BarraDoWindows.Arcos(1) == 1 && BarraDoWindows.Arcos(33) == 1
               && BarraDoWindows.Arcos(34) == 2 && BarraDoWindows.Arcos(66) == 2 && BarraDoWindows.Arcos(67) == 3 && BarraDoWindows.Arcos(100) == 3,
            "BW-12 o leque acende 0 a 3 arcos pelo sinal");
        checar(BarraDoWindows.NomeDoSsid(B("Café da Loja")) == "Café da Loja" && BarraDoWindows.NomeDoSsid(new byte[] { 0x43, 0xE9 }) == "Cé"
               && BarraDoWindows.NomeDoSsid(Array.Empty<byte>()) == "",
            "BW-13 nome da rede em UTF-8; bytes que não são UTF-8 caem no Latin-1 sem exceção");

        // ── o texto da barra ──────────────────────────────────────────────────
        static BarraDoWindows.EstadoWifi E(BarraDoWindows.SituacaoWifi s, string? r = null, bool cabo = false) => new(s, r, 60, cabo);
        checar(BarraDoWindows.TextoDoWifi(E(BarraDoWindows.SituacaoWifi.Conectado, "AmericanDay")) == "AmericanDay"
               && BarraDoWindows.TextoDoWifi(E(BarraDoWindows.SituacaoWifi.Conectado)) == "Wi-Fi"
               && BarraDoWindows.TextoDoWifi(E(BarraDoWindows.SituacaoWifi.Conectando)) == "Conectando..."
               && BarraDoWindows.TextoDoWifi(E(BarraDoWindows.SituacaoWifi.Desconectado)) == "Sem Wi-Fi"
               && BarraDoWindows.TextoDoWifi(E(BarraDoWindows.SituacaoWifi.Desligado)) == "Wi-Fi desligado"
               && BarraDoWindows.TextoDoWifi(E(BarraDoWindows.SituacaoWifi.SemPlaca, cabo: true)) == "Internet pelo cabo"
               && BarraDoWindows.TextoDoWifi(E(BarraDoWindows.SituacaoWifi.SemPlaca)) == "Sem rede",
            "BW-14 o texto ao lado do leque em cada situação");
        checar(BarraDoWindows.SituacaoDaPlaca(1) == BarraDoWindows.SituacaoWifi.Conectado
               && new[] { 5, 6, 7 }.All(x => BarraDoWindows.SituacaoDaPlaca(x) == BarraDoWindows.SituacaoWifi.Conectando)
               && new[] { 0, 2, 3, 4 }.All(x => BarraDoWindows.SituacaoDaPlaca(x) == BarraDoWindows.SituacaoWifi.Desconectado),
            "BW-15 WLAN_INTERFACE_STATE para a situação da barra");

        // ── a rede que dá para conectar daqui ─────────────────────────────────
        var aberta = BarraDoWindows.Conexao(1, 0);
        var wpa2 = BarraDoWindows.Conexao(7, 4);
        var wpa2Tkip = BarraDoWindows.Conexao(7, 2);
        var wpa = BarraDoWindows.Conexao(4, 2);
        var wpa3 = BarraDoWindows.Conexao(9, 4);
        checar(aberta is { Autenticacao: "open", Cifra: "none", PedeSenha: false, Motivo: null }, "BW-16 rede aberta: sem senha");
        checar(wpa2 is { Autenticacao: "WPA2PSK", Cifra: "AES", PedeSenha: true, Motivo: null }
               && wpa2Tkip is { Autenticacao: "WPA2PSK", Cifra: "TKIP" } && wpa is { Autenticacao: "WPAPSK", Cifra: "TKIP" }
               && wpa3 is { Autenticacao: "WPA3SAE", Cifra: "AES", PedeSenha: true },
            "BW-17 WPA, WPA2 (AES ou TKIP) e WPA3 pedem senha e viram o perfil certo");
        checar(new[] { 3, 6, 8, 11 }.All(a => BarraDoWindows.Conexao(a, 4).Motivo == BarraDoWindows.MotivoEmpresa),
            "BW-18 rede de empresa (usuário e senha): manda chamar o suporte");
        checar(BarraDoWindows.Conexao(1, 1).Motivo == BarraDoWindows.MotivoAntiga && BarraDoWindows.Conexao(2, 5).Motivo == BarraDoWindows.MotivoAntiga
               && BarraDoWindows.Conexao(1, 0x101).Motivo == BarraDoWindows.MotivoAntiga && BarraDoWindows.Conexao(99, 4).Motivo == BarraDoWindows.MotivoAntiga,
            "BW-19 WEP e o que não se conhece: não tenta");

        checar(BarraDoWindows.ProblemaNaSenha("") is not null && BarraDoWindows.ProblemaNaSenha(null) is not null
               && BarraDoWindows.ProblemaNaSenha("1234567") == "A senha do Wi-Fi tem pelo menos 8 letras ou números."
               && BarraDoWindows.ProblemaNaSenha("12345678") is null && BarraDoWindows.ProblemaNaSenha(new string('a', 63)) is null
               && BarraDoWindows.ProblemaNaSenha(new string('z', 64)) is not null && BarraDoWindows.ProblemaNaSenha(new string('f', 64)) is null,
            "BW-20 senha: 8 a 63 letras, ou a chave de 64 hexa");

        // ── o perfil que o Windows grava ──────────────────────────────────────
        var nomeEsquisito = B("Loja & <Cia> \"Café\"");
        var xml = BarraDoWindows.PerfilXml(nomeEsquisito, wpa2, "senha&<forte>");
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://www.microsoft.com/networking/WLAN/profile/v1";
        var raiz = doc.Root!;
        checar(raiz.Name == ns + "WLANProfile" && raiz.Element(ns + "name")!.Value == "Loja & <Cia> \"Café\"",
            "BW-21 o perfil é XML válido e o nome com & < > aspas e acento volta igual");
        checar(raiz.Descendants(ns + "hex").Single().Value == Convert.ToHexString(nomeEsquisito),
            "BW-22 o SSID vai também em hexa, byte a byte");
        checar(raiz.Descendants(ns + "authentication").Single().Value == "WPA2PSK" && raiz.Descendants(ns + "encryption").Single().Value == "AES"
               && raiz.Descendants(ns + "keyType").Single().Value == "passPhrase" && raiz.Descendants(ns + "keyMaterial").Single().Value == "senha&<forte>"
               && raiz.Descendants(ns + "protected").Single().Value == "false" && raiz.Descendants(ns + "connectionMode").Single().Value == "auto",
            "BW-23 WPA2: senha como frase, conecta sozinho da próxima vez");
        var xmlAberta = XDocument.Parse(BarraDoWindows.PerfilXml(B("Cafe Gratis"), aberta, null));
        checar(!xmlAberta.Descendants(ns + "sharedKey").Any() && xmlAberta.Descendants(ns + "authentication").Single().Value == "open",
            "BW-24 rede aberta: perfil sem senha");
        var hex64 = new string('a', 64);
        checar(XDocument.Parse(BarraDoWindows.PerfilXml(B("X"), wpa3, hex64)).Descendants(ns + "keyType").Single().Value == "networkKey",
            "BW-25 a chave de 64 hexa vai como networkKey");
        checar(Lanca(() => BarraDoWindows.PerfilXml(B("X"), BarraDoWindows.Conexao(6, 4), "12345678"))
               && Lanca(() => BarraDoWindows.PerfilXml(B("X"), wpa2, "curta")),
            "BW-26 rede de empresa ou senha curta nunca viram perfil");

        // ── o som ─────────────────────────────────────────────────────────────
        checar(BarraDoWindows.Porcentagem(0.4f) == 40 && BarraDoWindows.Porcentagem(0.556f) == 56 && BarraDoWindows.Porcentagem(1.7f) == 100
               && BarraDoWindows.Porcentagem(-1f) == 0 && Math.Abs(BarraDoWindows.Escalar(35) - 0.35f) < 0.0001f && BarraDoWindows.Escalar(140) == 1f,
            "BW-27 volume do Windows (0 a 1) em porcentagem e de volta, sempre entre 0 e 100");
        checar(BarraDoWindows.Passo(95, 10) == 100 && BarraDoWindows.Passo(5, -10) == 0 && BarraDoWindows.Passo(40, 10) == 50,
            "BW-28 o − e o + andam de 10 e param nas pontas");
        checar(BarraDoWindows.Ondas(30, true) == -1 && BarraDoWindows.Ondas(0, false) == 0 && BarraDoWindows.Ondas(49, false) == 1 && BarraDoWindows.Ondas(50, false) == 2,
            "BW-29 o alto-falante: X no mudo, 0 a 2 ondas pelo volume");
        checar(BarraDoWindows.TextoDoSom(true, 40, false) == "40%" && BarraDoWindows.TextoDoSom(true, 40, true) == "Mudo"
               && BarraDoWindows.TextoDoSom(false, 0, false) == "Sem som",
            "BW-30 o texto ao lado do alto-falante");

        // ── o exe ─────────────────────────────────────────────────────────────
        var barra = Fonte(Path.Combine("Telas", "BarraWindows.cs")) ?? "";
        var wifi = Fonte("WifiDoWindows.cs") ?? "";
        var som = Fonte("SomDoWindows.cs") ?? "";
        var nucleo = Fonte(Path.Combine("Pdv.Nucleo", "BarraDoWindows.cs")) ?? "";
        var janela = Fonte("MainWindow.xaml") ?? "";
        var conf = Fonte(Path.Combine("Telas", "Configuracao.xaml.cs")) ?? "";
        var confXaml = Fonte(Path.Combine("Telas", "Configuracao.xaml")) ?? "";
        checar(janela.Contains("<RowDefinition Height=\"Auto\"/>   <!-- barra do Windows")
               && janela.Contains("<telas:BarraWindows Grid.Row=\"2\" x:Name=\"BarraWindows\"/>"),
            "BW-31 a barra mora numa linha própria da janela, embaixo, fora das camadas do chat");
        checar(barra.Contains("BarraDoWindows.Mostrar(opcao, !WifiDoWindows.WindowsComBarra())")
               && barra.Contains("Interval = TimeSpan.FromSeconds(5)") && wifi.Contains("FindWindow(\"Shell_TrayWnd\", null)"),
            "BW-32 reavalia de 5 em 5 s pela barra do Windows de verdade (Shell_TrayWnd)");
        checar(barra.Split("new Button").Length - 1 == 2 && barra.Split("Focusable = false").Length - 1 >= 4,
            "BW-33 nenhum botão da barra nem das janelinhas pega o foco do teclado (o leitor continua bipando)");
        checar(barra.Contains("await Task.Run(WifiDoWindows.Estado)") && barra.Contains("await Task.Run(WifiDoWindows.Redes)")
               && barra.Contains("await Task.Run(WifiDoWindows.Procurar)") && barra.Contains("await WifiDoWindows.ConectarAsync(r, xml)"),
            "BW-34 o Wi-Fi do Windows é lido fora da thread da tela");
        checar(wifi.Contains("if (gravou) WlanDeleteProfile(s.H, ref id, perfil, IntPtr.Zero);")
               && wifi.Split("WlanDeleteProfile(s.H").Length - 1 == 2,
            "BW-35 rede nova que não entrou tem o perfil apagado (senha errada não fica salva)");
        checar(wifi.Contains("TamanhoRede = 628") && wifi.Contains("TamanhoPlaca = 532") && wifi.Contains("var q = p + 512 + 36 + 4 + 4 + 4 + 4 + 4 + 32 + 4;"),
            "BW-36 o desenho do WLAN_AVAILABLE_NETWORK (628 bytes, sinal no byte 604)");
        checar(som.Contains("Guid(\"5CDF2C82-841E-4546-9722-0CF74078229A\")") && som.Contains("if (ok && porcentagem > 0) v.SetMute(false, ref ctx);"),
            "BW-37 o volume pelo IAudioEndpointVolume; subir o volume tira o mudo, como no Windows");
        checar(barra.Contains("Dialogo.Escolher(dono, r.Nome, \"Não conectou. A senha do Wi-Fi pode ter mudado.\", \"Digitar a senha\")"),
            "BW-38 rede salva que não entra oferece digitar a senha de novo");
        checar(confXaml.Contains("x:Name=\"ChkBarraWindows\"")
               && conf.Contains("ChkBarraWindows.IsChecked = BarraDoWindows.MarcadaNaConfiguracao(_barraWindowsAntes, Quiosque.Ligado);")
               && conf.Contains("if (_barraWindowsAntes is not null || barraQuer != _barraWindowsMarcada)")
               && conf.Contains("(Window.GetWindow(this) as MainWindow)?.BarraWindows.Reavaliar()"),
            "BW-39 a Configuração: nasce pela regra, sem mexer continua automático, salvar aplica na hora");
        var venda = Fonte(Path.Combine("Telas", "Venda.xaml")) ?? "";
        checar(venda.Contains("<TextBlock x:Name=\"TxtRodape\" Grid.Column=\"1\"")
               && venda.Contains("<StackPanel Grid.Column=\"2\" Orientation=\"Horizontal\" HorizontalAlignment=\"Right\""),
            "BW-42 o texto do rodapé da venda (loja, série, versão) tem coluna própria e não passa por baixo do configuração");
        var estiloTexto = venda[venda.IndexOf("<Style x:Key=\"BotaoTexto\"", StringComparison.Ordinal)..];
        checar(estiloTexto[..estiloTexto.IndexOf("</Style>", StringComparison.Ordinal)].Contains("<Setter Property=\"Foreground\" Value=\"{DynamicResource Texto}\"/>"),
            "BW-43 os botões de texto da venda (configuração, modo claro, música) pegam a cor do tema, não o preto do sistema");
        checar(!(barra + wifi + som + nucleo).Contains('—') && !(barra + wifi + som + nucleo).Contains('–')
               && !TrechoNovoDaConfiguracao(confXaml).Contains('—') && !TrechoNovoDaConfiguracao(confXaml).Contains('–'),
            "BW-40 nenhum texto com travessão");

        // ── este PC de verdade: só não pode lançar ────────────────────────────
        var semExcecao = true;
        try
        {
            var e = WifiDoWindows.Estado();
            var redes = WifiDoWindows.Redes();
            var s = SomDoWindows.Ler();
            Console.WriteLine($"   (info) este PC: Wi-Fi {e.Situacao} '{BarraDoWindows.TextoDoWifi(e)}' sinal {e.Sinal}, {redes.Count} redes; "
                              + $"som {BarraDoWindows.TextoDoSom(s.Achou, s.Porcentagem, s.Mudo)}; barra do Windows {(WifiDoWindows.WindowsComBarra() ? "existe" : "não existe")}");
            foreach (var r in redes.Take(4))
                Console.WriteLine($"   (info)   {r.Nome}: sinal {r.Sinal}, {BarraDoWindows.Rotulo(r)}, auth {r.Autenticacao}/cifra {r.Cifra}, {BarraDoWindows.Conexao(r.Autenticacao, r.Cifra).Autenticacao ?? BarraDoWindows.Conexao(r.Autenticacao, r.Cifra).Motivo}");
        }
        catch (Exception ex) { semExcecao = false; Console.WriteLine("   (info) " + ex); }
        checar(semExcecao, "BW-41 a leitura de verdade do Wi-Fi, das redes e do som deste PC não lança");
    }

    private static string TrechoNovoDaConfiguracao(string xaml)
    {
        var i = xaml.IndexOf("ChkBarraWindows", StringComparison.Ordinal);
        return i < 0 ? "" : xaml.Substring(i, Math.Min(600, xaml.Length - i));
    }

    private static bool Lanca(Action a)
    {
        try { a(); return false; }
        catch (ArgumentException) { return true; }
    }

    private static string? Fonte(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj")))
            {
                var c = Path.Combine(dir.FullName, relativo);
                return File.Exists(c) ? File.ReadAllText(c) : null;
            }
        return null;
    }
}
