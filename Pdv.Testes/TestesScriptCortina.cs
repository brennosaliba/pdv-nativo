using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pdv.Testes;

/// <summary>
/// A CORTINA DO CHAT E O GESTOR 9.349 (1.0.47, 22/10/2026). Na manha de 22/10 o Gestor de Pedidos do iFood
/// atualizou (9.349.0, "Expedicao 5.0.1") e, na Savassi, a tela CHAT DO iFOOD do caixa 1.0.43 ficou presa para
/// sempre na NOSSA cortina "Abrindo o chat...": Recarregar, Diagnostico e Gestor inteiro nao mudavam nada. Por tras
/// dela o dono viu a casca do proprio Gestor (o spinner vermelho e branco em fundo cinza), que recarrega sozinha
/// a cada 45 s; cada recarga subia a cortina de novo. O chat virou outro programa dentro do Gestor (o microfrontend
/// partners-consumer-chat) e o titulo "Conversas" da gaveta nova nao e mais um h1.
///
/// O que esta suite trava:
///  . o ScriptPainel (a constante de Telas/ChatIfood.xaml.cs) roda no node contra um DOM falso com relogio falso
///    (Pdv.Testes/script_cortina.test.cjs): a cortina cai sozinha em 20 s e avisa o C# (tipo cortina); o JsGestorInteiro
///    do C# a derruba na hora e ela nao volta; a gaveta nova (p) e a velha (h1) sao isoladas; o botao do cabecalho e
///    achado pelo rotulo e pelo data-testid; login sem cortina; chat isolado antes do prazo sem aviso;
///  . o C#: Recarregar e recarga forte (service worker, Cache Storage e cache de disco do perfil; cookies e localStorage
///    ficam), o Gestor inteiro e o Diagnostico funcionam sem o _pronto, a barra diz "Chat sem o recorte por enquanto",
///    o Gestor inteiro sobrevive a recarga, o diag do Gestor diz se a casca ainda esta na tela.
/// Sem node na maquina a suite FALHA (nao pula).
/// </summary>
public static class TestesScriptCortina
{
    public static void Rodar(Action<bool, string> checar)
    {
        var raiz = TestesScriptSendbird.Raiz();
        var fonte = raiz is null ? "" : TestesScriptSendbird.LerSeExiste(Path.Combine(raiz, "Telas", "ChatIfood.xaml.cs"));
        var script = TestesScriptSendbird.ExtrairRaw(fonte, "ScriptPainel");
        checar(script is { Length: > 5000 }, "CT-0 o ScriptPainel sai da constante de Telas/ChatIfood.xaml.cs");
        if (script is null || raiz is null) return;
        checar(!script.Contains((char)0x2014) && !script.Contains((char)0x2013), "CT-0b o script nao tem travessao");

        // ── o script ──
        checar(script.Contains("var CORTINA_MS = 20000"), "CT-1 a cortina cai em 20 s (CORTINA_MS = 20000)");
        checar(!script.Contains("25000);") || !Regex.IsMatch(script, @"cortina\(false\);\s*\},\s*25000"), "CT-2 o relogio de 25 s so da carga saiu (toda subida arma a propria queda)");
        checar(script.Contains("window.pdvSemCortina = function") && script.Contains("window.__pdvSemCortina = true"), "CT-3 a pagina expoe pdvSemCortina (derruba e desliga a cortina neste documento)");
        checar(script.Contains("if (window.__pdvSemCortina) return;"), "CT-4 com a cortina desligada ela nao sobe mais (nem pelo Fale com o iFood)");
        checar(script.Contains("envia({ tipo: 'cortina', motivo: 'tempo'"), "CT-5 a queda pelo prazo sem o chat isolado avisa o C# (tipo cortina)");
        checar(script.Contains("function textoSoDele(el)") && script.Contains("/^conversas?$/i.test(textoSoDele(el))"), "CT-6 o titulo 'Conversas' da gaveta nova (p/span/div, texto proprio) e aceito");
        checar(script.Contains("r.width >= 250 && r.width <= 1000 && r.height > 400"), "CT-7 a gaveta pode ter ate 1000 px de largura");
        checar(script.Contains("[data-testid*=\"consumer-chat\"]"), "CT-8 pdvAbrirConversas tambem acha o botao pelo data-testid do chat novo");

        // ── o C# ──
        var js = JsDoCSharp(fonte, "JsGestorInteiro");
        checar(js is { Length: > 50 } && js.Contains("window.pdvSemCortina && window.pdvSemCortina()") && js.Contains("window.__pdvSemHolofote = true"),
            "CT-10 o JsGestorInteiro do C# derruba a cortina e tira o holofote");
        var alternar = Entre(fonte, "private async void AlternarGestorInteiro(", "AutorizarGestorInteiroAsync()");
        checar(alternar.Length > 0 && !alternar.Contains("!_pronto"), "CT-11 o Gestor inteiro nao depende mais do _pronto (so sem navegador nao da)");
        checar(fonte.Contains("ExecuteScriptAsync(_gestorInteiro ? JsGestorInteiro : JsSoOChat)"), "CT-12 o botao usa as constantes (o mesmo texto vai para a TV)");
        checar(fonte.Contains("if (e.IsSuccess && _gestorInteiro) _ = ReaplicarGestorInteiroAsync(core);") && fonte.Contains("_semRecorte = false;   // 1.0.47"),
            "CT-13 depois de cada navegacao: o Gestor inteiro e reaplicado e a marca 'sem recorte' zera");
        checar(fonte.Contains("else if (tipo == \"cortina\")") && fonte.Contains("TxtEstado.Text = ChatSemRecorte;") && fonte.Contains("ChatSemRecorte = \"Chat sem o recorte por enquanto\""),
            "CT-14 o aviso da cortina vira a linha 'Chat sem o recorte por enquanto' na barra");
        checar(Pdv.Telas.ChatIfood.ChatSemRecorte.Split('\n').Length == 1 && Pdv.Telas.ChatIfood.ChatSemRecorte.Length <= 40 && !Pdv.Telas.ChatIfood.ChatSemRecorte.Contains('—'),
            "CT-15 a linha e curta, uma so, sem travessao");
        var recarregar = Entre(fonte, "private async void Recarregar(object sender", "private void Voltar(");
        checar(recarregar.Contains("await RecarregarForteAsync(\"botao Recarregar\")"), "CT-16 o botao Recarregar faz a recarga forte");
        checar(recarregar.Contains("CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage | CoreWebView2BrowsingDataKinds.ServiceWorkers")
               && !recarregar.Contains("Cookies") && !recarregar.Contains("LocalStorage") && !recarregar.Contains("AllSite"),
            "CT-17 a recarga forte limpa cache de disco, Cache Storage e service workers do perfil, e nunca cookies nem localStorage (o login)");
        checar(recarregar.Contains("Network.clearBrowserCache") && recarregar.Contains("RecarregarNoInicio(core);") && recarregar.Contains("getRegistrations()") && recarregar.Contains("r.unregister()"),
            "CT-18 com prazo, pela pagina (unregister + caches.delete), pelo perfil, e pelo CDP se o perfil faltar; depois recarrega");
        checar(recarregar.Contains("Task.WhenAny(js, Task.Delay(3000))"), "CT-19 o script da pagina tem prazo de 3 s (pagina presa nao segura o Recarregar)");
        var diag = Entre(fonte, "private async void Diagnostico(object sender", "private const string ScriptDiagnostico");
        checar(diag.Length > 0 && !diag.Contains("!_pronto") && diag.Contains("Dialogo.Avisar(dono, \"Diagnóstico\", \"Gravado em \" + caminho)"),
            "CT-20 o Diagnostico grava com a cortina presa e DIZ na tela onde gravou");
        checar(diag.Contains("runtime WebView2:") && diag.Contains("navegacao:"), "CT-21 o arquivo do diag leva a versao do runtime do WebView2 e a navegacao");
        var scriptDiag = TestesScriptSendbird.ExtrairRaw(fonte, "ScriptDiagnostico") ?? "";
        checar(scriptDiag.Contains("casca_do_gestor=") && scriptDiag.Contains("root_filhos=") && scriptDiag.Contains("cortina=") && scriptDiag.Contains("aviso_do_gestor"),
            "CT-22 a primeira linha do diag diz se a casca do Gestor segue na tela, se o app montou, a cortina e o aviso do Gestor");
        var csproj = TestesScriptSendbird.LerSeExiste(Path.Combine(raiz, "Pdv.csproj"));
        checar(csproj.Contains("<Version>1.0.60</Version>", StringComparison.Ordinal), "CT-23 a versao e 1.0.60 (chat sai da ajuda presa)");

        // ── 1.0.60 (30/10/2026): a ajuda da impressora do Gestor 9.351 prendia o chat no spinner ──
        checar(fonte.Contains("internal static void RecarregarNoInicio(CoreWebView2 core) => core.Navigate(UrlGestor);"),
            "CT-24 recarregar e navegar para a raiz do Gestor (o Reload repetia a ajuda presa)");
        var vigia = TestesScriptSendbird.LerSeExiste(Path.Combine(raiz, "Telas", "ChatIfood.Vigia.cs"));
        checar(vigia.Contains("RecarregarNoInicio(c);") && !vigia.Contains("c.Reload();"), "CT-25 a vigia tambem recarrega pela raiz");
        checar(script.Contains("function saiDaAjuda()") && script.Contains("location.hash = '#/home/order-display/expedition';")
               && script.Contains("if (window.__pdvSemHolofote) return false;"),
            "CT-26 no modo so o chat a pagina sai sozinha de #/home/help para a expedicao; com o Gestor inteiro nao");
        checar(script.Contains("if (avisoOuAjuda(cands[i])) continue;"), "CT-27 o clique pela posicao pula aviso e link da ajuda");
        checar(fonte.Contains("else if (tipo == \"saiudaajuda\")"), "CT-28 a saida da ajuda fica no diagnostico do chat");

        // ── o node ──
        var harness = Path.Combine(raiz, "Pdv.Testes", "script_cortina.test.cjs");
        checar(File.Exists(harness), "CT-30 o harness esta em Pdv.Testes/script_cortina.test.cjs");
        if (!File.Exists(harness) || js is null) return;
        var pasta = Path.Combine(Path.GetTempPath(), "pdv-ct-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(pasta);
        try
        {
            var arqScript = Path.Combine(pasta, "script-painel.js");
            var arqJs = Path.Combine(pasta, "gestor-inteiro.js");
            File.WriteAllText(arqScript, script, new UTF8Encoding(false));
            File.WriteAllText(arqJs, js, new UTF8Encoding(false));
            var check = TestesScriptSendbird.Node(pasta, "--check", arqScript);
            checar(check.Exit == 0, $"CT-31 o ScriptPainel passa no node --check ({Corta(check.Erro)})");
            var r = TestesScriptSendbird.Node(pasta, harness, arqScript, arqJs);
            List<Item>? itens = null;
            try
            {
                var ultima = r.Saida.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('['));
                if (ultima is not null) itens = JsonSerializer.Deserialize<List<Item>>(ultima);
            }
            catch { itens = null; }
            checar(r.Exit == 0 && itens is { Count: >= 40 }, $"CT-32 o node rodou o ScriptPainel contra o DOM falso com relogio falso (saida {r.Exit}, {itens?.Count ?? 0} conferencias) {Corta(r.Erro)}");
            foreach (var i in itens ?? new List<Item>()) checar(i.ok, "CT-" + i.nome);
        }
        finally
        {
            try { Directory.Delete(pasta, true); } catch { }
        }
    }

    private sealed record Item(bool ok, string nome);

    private static string Corta(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim();

    private static string Entre(string fonte, string de, string ate)
    {
        var i = fonte.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var j = fonte.IndexOf(ate, i, StringComparison.Ordinal);
        return j < 0 ? "" : fonte[i..j];
    }

    /// <summary>O texto de uma constante C# feita de literais "..." concatenados (JsGestorInteiro), como a pagina o recebe.</summary>
    internal static string? JsDoCSharp(string fonte, string nome)
    {
        var i = fonte.IndexOf("const string " + nome + " =", StringComparison.Ordinal);
        if (i < 0) return null;
        var j = fonte.IndexOf(";\n", i, StringComparison.Ordinal);
        if (j < 0) j = fonte.IndexOf(";\r\n", i, StringComparison.Ordinal);
        if (j < 0) return null;
        var sb = new StringBuilder();
        foreach (Match m in Regex.Matches(fonte[i..j], "\"((?:[^\"\\\\]|\\\\.)*)\""))
            sb.Append(Regex.Unescape(m.Groups[1].Value));
        return sb.ToString();
    }
}
