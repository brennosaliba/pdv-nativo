using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Pdv.Instalador;
using Pdv.Nucleo;
using Pdv.Telas;

namespace Pdv.Testes;

/// <summary>
/// O CHAT E O WHATSAPP ABREM COM O PERFIL PRESO PELO CAIXA ANTERIOR (1.0.35, Savassi, 14/10/2026).
///
/// Às 21:21 o caixa da Savassi se atualizou de 1.0.32 para 1.0.34 e os dois mostraram "não abriu
/// agora. Toque em Tentar de novo." até o PC reiniciar. A 1.0.33 mudou as opções do navegador dos
/// dois perfis; com o navegador da versão anterior ainda vivo no perfil, o WebView2 recusa o
/// ambiente com as opções novas (COMException 0x8007139F no EnsureCoreWebView2Async), e o caixa
/// lia isso como "navegador caiu".
///
/// O que esta suíte protege:
///  · a regra pura (PE-*): a recusa tem nome próprio, o painel diz que está esperando e depois manda
///    reiniciar, a espera não gasta o teto da hora; e a leitura da linha de comando do navegador
///    (de que perfil é, quem abriu, com que opções) decide com que opções entrar;
///  · o fonte (PF-*): as duas telas declaram as opções que o perfil já teve e esperam o perfil;
///  · DE VERDADE (RP-*), com o runtime desta máquina e um SEGUNDO PROCESSO segurando o perfil
///    (<see cref="SondaPerfilWebView2"/>, o caixa anterior que não saiu): o caso da Savassi abre;
///    o controle segue vivo depois que o caixa anterior sai; num PC limpo nada muda; o órfão é
///    encerrado; e a troca de versão do instalador espera e encerra os navegadores do caixa.
/// RP-1 é a reprodução: na 1.0.34 (c0fb538) ele falha com o 0x8007139F.
/// </summary>
public static class TestesPerfilEmUso
{
    // As opções que já foram para a loja (cópia FIXA: o teste é a memória do que existe nos PCs).
    private const string Chat133 = "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows";
    private const string Wa132 = "--autoplay-policy=no-user-gesture-required --disable-background-timer-throttling --disable-renderer-backgrounding";
    private const string Wa133 = Wa132 + " --disable-backgrounding-occluded-windows";
    /// <summary>As opções anteriores do chat: até a 1.0.32, nenhuma.</summary>
    private static readonly IReadOnlyList<string?> SemOpcao = new string?[] { null };

    private const string Exe = @"""C:\Program Files (x86)\Microsoft\EdgeWebView\Application\155.0.4283.45\msedgewebview2.exe""";
    private static string Linha(string perfil, string versao, string? argumentos, string extra = "")
        => $@"{Exe} --embedded-browser-webview=1 --webview-exe-name=Pdv.exe --webview-exe-version={versao} "
         + $@"--user-data-dir=""{perfil}\EBWebView"" --noerrdialogs --embedded-browser-webview-dpi-awareness=2 "
         + (argumentos is null ? "" : argumentos + " ")
         + extra + "--mojo-named-platform-channel-pipe=4321.987.123456789 /pfhostedapp:0a1b2c3d";

    public static Task RodarAsync(Action<bool, string> checar)
    {
        RegraPura(checar);
        Fonte(checar);
        var raiz = Path.Combine(Path.GetTempPath(), "pdv-testes-perfil-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            HostWpf.ExecutarAsync(() => DeVerdadeAsync(checar, raiz), TimeSpan.FromSeconds(300));
        }
        catch (Exception ex)
        {
            checar(false, "RP a prova de verdade rodou até o fim, mas ESCAPOU " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            for (var i = 0; i < 10 && Directory.Exists(raiz); i++)
            {
                try { Directory.Delete(raiz, recursive: true); }
                catch { Thread.Sleep(500); /* o navegador solta o perfil em até 1 s */ }
            }
        }
        return Task.CompletedTask;
    }

    // ── 1. A REGRA PURA ────────────────────────────────────────────────────────
    private static void RegraPura(Action<bool, string> checar)
    {
        var recusa = new COMException("O grupo ou recurso não está no estado correto para executar a operação requisitada. (0x8007139F)", unchecked((int)0x8007139F));
        var embrulhada = new PerfilEmUsoException("navegador pid=1", recusa);
        checar(FalhaWebView2.Classificar(embrulhada) == TipoFalhaWeb.PerfilEmUso
               && FalhaWebView2.Classificar(new AggregateException(embrulhada)) == TipoFalhaWeb.PerfilEmUso
               && FalhaWebView2.Classificar(recusa) == TipoFalhaWeb.NavegadorCaiu,
            "PE-1 a recusa do perfil embrulhada pelo hospedeiro é 'perfil em uso'; o 0x8007139F solto (ambiente velho) continua 'navegador caiu'");
        checar(FalhaWebView2.EhRecusaDoPerfil(recusa) && FalhaWebView2.EhRecusaDoPerfil(new AggregateException(recusa))
               && !FalhaWebView2.EhRecusaDoPerfil(new InvalidOperationException(FalhaWebView2.MensagemDoController))
               && !FalhaWebView2.EhRecusaDoPerfil(new COMException("outra", unchecked((int)0x80004005))) && !FalhaWebView2.EhRecusaDoPerfil(null),
            "PE-2 a recusa do perfil é o 0x8007139F (ERROR_INVALID_STATE), e só ele");

        var chat = FalhaWebView2.Painel(TipoFalhaWeb.PerfilEmUso, "O chat");
        var wa = FalhaWebView2.Painel(TipoFalhaWeb.PerfilEmUso, "O WhatsApp", TimeSpan.FromMinutes(4.9));
        var chat5 = FalhaWebView2.Painel(TipoFalhaWeb.PerfilEmUso, "O chat", TimeSpan.FromMinutes(5));
        var wa5 = FalhaWebView2.Painel(TipoFalhaWeb.PerfilEmUso, "O WhatsApp", TimeSpan.FromMinutes(30));
        var textos = new[] { chat, wa, chat5, wa5 };
        checar(chat == "O chat está esperando o caixa anterior fechar." && wa == "O WhatsApp está esperando o caixa anterior fechar."
               && chat5 == "Reinicie o computador para o chat voltar." && wa5 == "Reinicie o computador para o WhatsApp voltar.",
            $"PE-3 o painel do perfil em uso diz que está esperando e, passados 5 min, manda reiniciar o computador ({string.Join(" | ", textos)})");
        checar(textos.All(t => t.Length is > 10 and <= 80 && !t.Contains('\n') && !t.Contains('—') && !t.Contains('–')
                               && !t.Contains("WebView2", StringComparison.OrdinalIgnoreCase) && !t.Contains("perfil", StringComparison.OrdinalIgnoreCase)
                               && !t.Contains("0x", StringComparison.Ordinal))
               && FalhaWebView2.Painel(TipoFalhaWeb.Outra, "O chat", TimeSpan.FromHours(1)) == FalhaWebView2.Painel(TipoFalhaWeb.Outra, "O chat"),
            "PE-4 uma linha curta, sem travessão, sem 'WebView2', 'perfil' nem código; as outras falhas não mudam com o tempo");

        checar(FalhaWebView2.AposFalha(true, false, TipoFalhaWeb.PerfilEmUso) == (true, true)
               && FalhaWebView2.AposFalha(true, true, TipoFalhaWeb.PerfilEmUso) == (true, true)
               && FalhaWebView2.AposFalha(false, false, TipoFalhaWeb.PerfilEmUso) == (false, true),
            "PE-5 perfil em uso: o controle usado sai e o ambiente nunca fica (ele nasceu com as opções recusadas)");
        var esperas = Enumerable.Range(1, 8).Select(i => FalhaWebView2.EsperaDoPerfil(i).TotalSeconds).ToList();
        checar(esperas.SequenceEqual(new double[] { 10, 20, 40, 60, 60, 60, 60, 60 }) && FalhaWebView2.EsperaDoPerfil(0).TotalSeconds == 10,
            $"PE-6 a tela tenta de novo sozinha em 10, 20, 40 e 60 s, e daí a cada minuto ({string.Join(", ", esperas)})");
        var linhaDiag = FalhaWebView2.Linha("chat iniciar", embrulhada, 1, 1, "155.0.4283.45");
        checar(linhaDiag.StartsWith("chat iniciar: PerfilEmUso COMException 0x8007139F", StringComparison.Ordinal),
            $"PE-7 a linha de diagnóstico diz 'PerfilEmUso' e traz o HResult de dentro ({linhaDiag[..Math.Min(80, linhaDiag.Length)]})");

        // A linha de comando do navegador, no formato que o runtime 155 escreve (medido em 14/10).
        const string pdv = @"C:\ProgramData\PdvNativo";
        var chat132 = Linha(pdv + @"\webview", "1.0.32.0", null);
        var chat134 = Linha(pdv + @"\webview", "1.0.34.0", Chat133);
        var wa132 = Linha(pdv + @"\webview-whatsapp", "1.0.32.0", Wa132);
        var wa134 = Linha(pdv + @"\webview-whatsapp", "1.0.34.0", Wa133);
        var renderer = chat132.Replace("--noerrdialogs", "--type=renderer --noerrdialogs");
        var longo = chat132.Replace(@"=""C:\ProgramData", @"=""\\?\C:\ProgramData");
        var outroApp = Linha(@"C:\Users\Waz\AppData\Local\Packages\MSTeams\LocalCache", "26246.0", "--autoplay-policy=no-user-gesture-required");
        var parecido = Linha(@"C:\ProgramData\PdvNativoX\webview", "1.0.32.0", null);
        checar(NavegadorDoPerfil.EhDoPerfil(chat132, pdv + @"\webview") && NavegadorDoPerfil.EhDoPerfil(longo, pdv + @"\webview")
               && NavegadorDoPerfil.EhDoPerfil(chat132, @"c:\programdata\PDVNATIVO\Webview\")
               && !NavegadorDoPerfil.EhDoPerfil(wa132, pdv + @"\webview") && !NavegadorDoPerfil.EhDoPerfil(chat132, pdv + @"\webview-whatsapp")
               && !NavegadorDoPerfil.EhDoPerfil(renderer, pdv + @"\webview") && !NavegadorDoPerfil.EhDoPerfil(outroApp, pdv + @"\webview"),
            "PE-8 o navegador do perfil é achado pela pasta de dados (com ou sem \\\\?\\, maiúscula ou não), e só o principal: o do WhatsApp, os filhos (--type=) e os de outro programa não");
        checar(NavegadorDoPerfil.EhDentroDe(chat132, pdv) && NavegadorDoPerfil.EhDentroDe(wa132, pdv + @"\")
               && !NavegadorDoPerfil.EhDentroDe(outroApp, pdv) && !NavegadorDoPerfil.EhDentroDe(parecido, pdv)
               && !NavegadorDoPerfil.EhDentroDe(renderer, pdv) && !NavegadorDoPerfil.EhDentroDe(chat132, @"C:\")
               && NavegadorDoPerfil.EhDentroDe(chat132, pdv, "PDV.exe") && !NavegadorDoPerfil.EhDentroDe(chat132, pdv, "KdsTreino.exe"),
            "PE-9 o instalador só vê os navegadores que o Pdv.exe abriu com a pasta DENTRO de ProgramData\\PdvNativo (PdvNativoX, outro programa e filhos ficam de fora)");
        var donoVivo = new NavegadorNoPerfil(4242, 1717, true, null, wa132);
        checar(donoVivo.Exe == "Pdv.exe" && donoVivo.Versao == "1.0.32.0"
               && NavegadorDoPerfil.ArgumentosDeQuemAbriu(wa132).SequenceEqual(NavegadorDoPerfil.Argumentos(Wa132))
               && NavegadorDoPerfil.ArgumentosDeQuemAbriu(chat132).Count == 0,
            "PE-10 da linha sai quem abriu (Pdv.exe 1.0.32.0) e só os argumentos que o caixa pôs (os do WebView2 ficam de fora)");

        var ordemChat = NavegadorDoPerfil.OrdemDasOpcoes(new string?[] { Chat133, null }, 0, new[] { chat132 });
        var ordemWa = NavegadorDoPerfil.OrdemDasOpcoes(new string?[] { Wa133, Wa132 }, 0, new[] { wa132 });
        var ordemWaIgual = NavegadorDoPerfil.OrdemDasOpcoes(new string?[] { Wa133, Wa132 }, 0, new[] { wa134 });
        var ordemChat134 = NavegadorDoPerfil.OrdemDasOpcoes(new string?[] { Chat133, null }, 0, new[] { chat134 });
        var ordemSemLinha = NavegadorDoPerfil.OrdemDasOpcoes(new string?[] { "a", null, "b" }, 1, Array.Empty<string>());
        var ordemDeVolta = NavegadorDoPerfil.OrdemDasOpcoes(new string?[] { Chat133, null }, 1, new[] { chat134 });
        checar(ordemChat.SequenceEqual(new[] { 1 }) && ordemWa.SequenceEqual(new[] { 1 }) && ordemWaIgual.Count == 0
               && ordemChat134.Count == 0 && ordemSemLinha.SequenceEqual(new[] { 0, 2 }) && ordemDeVolta.SequenceEqual(new[] { 0 }),
            "PE-11 com a linha do dono, só entra a opção que casa com ele (chat da 1.0.32: sem opção; WhatsApp da 1.0.32: os 3); sem linha, todas as outras");

        var orfao = new NavegadorNoPerfil(10, 20, false, null, chat132);
        checar(NavegadorDoPerfil.Classificar(Array.Empty<NavegadorNoPerfil>()) == NavegadorDoPerfil.Dono.Nenhum
               && NavegadorDoPerfil.Classificar(new[] { orfao }) == NavegadorDoPerfil.Dono.Orfao
               && NavegadorDoPerfil.Classificar(new[] { orfao, donoVivo }) == NavegadorDoPerfil.Dono.Vivo,
            "PE-12 órfão só quando TODOS os navegadores do perfil perderam quem os abriu (um dono vivo basta para não encerrar ninguém)");
        var descr = NavegadorDoPerfil.Descrever(donoVivo);
        checar(descr.Contains("pid=4242") && descr.Contains("pid=1717 (vivo)") && descr.Contains("Pdv.exe 1.0.32.0")
               && descr.Contains("--disable-renderer-backgrounding") && !descr.Contains("mojo") && !descr.Contains("user-data-dir")
               && NavegadorDoPerfil.Descrever(orfao).Contains("(morto)") && descr.Length < 400,
            $"PE-13 o diagnóstico diz o PID, quem abriu e se vive, a versão e os argumentos ({descr})");
    }

    // ── 2. O FONTE ─────────────────────────────────────────────────────────────
    private static void Fonte(Action<bool, string> checar)
    {
        checar(ChatIfood.ArgumentosAnteriores.Count == 1 && ChatIfood.ArgumentosAnteriores[0] is null
               && ChatWhatsApp.ArgumentosAnteriores.SequenceEqual(new string?[] { Wa132 })
               && ChatIfood.ArgumentosDoNavegador == Chat133 && ChatWhatsApp.ArgumentosDoNavegador == Wa133
               && !ChatIfood.ArgumentosAnteriores.Contains(ChatIfood.ArgumentosDoNavegador)
               && !ChatWhatsApp.ArgumentosAnteriores.Contains(ChatWhatsApp.ArgumentosDoNavegador),
            "PF-1 as opções desta versão não mudaram (mudar reabre o risco) e cada perfil declara as que já foram para a loja (chat: nenhuma; WhatsApp: as 3 da 1.0.32)");

        foreach (var (nome, arquivo, campo) in new[] { ("chat", "ChatIfood.xaml.cs", "ChatIfood.ArgumentosAnteriores"), ("whatsapp", "ChatWhatsApp.xaml.cs", "ChatWhatsApp.ArgumentosAnteriores") })
        {
            var cs = Ler("Telas", arquivo);
            var iniciar = Corpo(cs, "private async Task IniciarAsync()");
            var esperar = Corpo(cs, "private async Task EsperarOPerfilAsync(int minha)");
            checar(cs.Contains("ArgumentosAnteriores = " + campo, StringComparison.Ordinal),
                $"PF-2 {nome}: o hospedeiro recebe as opções anteriores do perfil");
            checar(iniciar.Contains("Hospede.EsperandoOPerfil(DateTime.Now)", StringComparison.Ordinal)
                   && iniciar.Contains("if (tipo == TipoFalhaWeb.PerfilEmUso) _ = EsperarOPerfilAsync(minha);", StringComparison.Ordinal)
                   && esperar.Contains("FalhaWebView2.EsperaDoPerfil(Hospede.VezesPerfilEmUso)", StringComparison.Ordinal)
                   && esperar.Contains("if (minha != _tentativa || _pronto || _iniciando) return;", StringComparison.Ordinal)
                   && esperar.Contains("await IniciarAsync();", StringComparison.Ordinal),
                $"PF-3 {nome}: com o perfil em uso o painel diz há quanto tempo espera e a tela tenta de novo sozinha (outra tentativa passa esta para trás)");
        }
        var chat = Ler("Telas", "ChatIfood.xaml.cs");
        var saude = ConversaRaspadinha.EstadoDoSinal("logado", true, null, 0, null, null, null, new ConversaRaspadinha.SaudeDoChat(true, "ok", "ok", 0, true));
        var saudeNormal = ConversaRaspadinha.EstadoDoSinal("logado", true, null, 0, null, null, null, new ConversaRaspadinha.SaudeDoChat(true, "ok", "ok", 0));
        checar(chat.Contains("Hospede.ComOpcoesAnteriores);", StringComparison.Ordinal)
               && JsonNode.Parse(saude)!["vigia"]!["navegador"]!.ToString() == "anterior"
               && JsonNode.Parse(saudeNormal)!["vigia"]!["navegador"]!.ToString() == "proprio",
            "PF-4 o sinal do chat diz ao ERP quando o navegador abriu com as opções anteriores (vigia.navegador = anterior | proprio)");

        var app = Ler("Pdv.Instalador", "App.xaml.cs");
        var atualizar = Corpo(app, "private static string? AtualizarSilencioso(");
        var iEsperar = atualizar.IndexOf("Instalacao.EsperarOCaixaAnteriorSair(Instalacao.CaixaAberto, TimeSpan.FromSeconds(30),", StringComparison.Ordinal);
        var iCopiar = atualizar.IndexOf("Instalacao.Instalar(", StringComparison.Ordinal);
        checar(iEsperar > 0 && iCopiar > iEsperar && atualizar.Contains("Instalacao.PastaDados, TimeSpan.FromSeconds(10)))", StringComparison.Ordinal)
               && atualizar.Contains("Instalacao.AnotarTroca(linha);", StringComparison.Ordinal)
               && !atualizar.Contains("GetProcessesByName(\"Pdv\").Length > 0; i++)", StringComparison.Ordinal),
            "PF-5 a troca de versão espera o caixa (30 s) e os navegadores dele (10 s, encerra os que ficam) ANTES de copiar, e anota tudo");
        checar(Ler("Pdv.Instalador", "Pdv.Instalador.csproj").Contains(@"<Compile Include=""..\Pdv.Nucleo\NavegadorDoPerfil.cs""", StringComparison.Ordinal),
            "PF-6 o instalador compila o MESMO NavegadorDoPerfil.cs do caixa");
    }

    // ── 3. DE VERDADE: o runtime desta máquina e um segundo processo no perfil ──
    private static async Task DeVerdadeAsync(Action<bool, string> checar, string raiz)
    {
        if (HospedeWebView2.VersaoDoRuntime() is null) { checar(false, "RP-0 esta máquina não tem o runtime do WebView2"); return; }

        var janela = new Window
        {
            Width = 480, Height = 360, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, ShowActivated = false, Title = "prova do perfil em uso",
        };
        var grade = new Grid();
        janela.Content = grade;
        janela.Show();
        var hospedes = new List<HospedeWebView2>();
        var sondas = new List<SondaPerfilWebView2.Viva>();

        (HospedeWebView2 H, Func<WebView2> Atual, List<string> Diag) Camada(string nome, string perfil, string? argumentos, IReadOnlyList<string?> anteriores)
        {
            var web = new WebView2 { Visibility = Visibility.Collapsed };
            grade.Children.Add(web);
            var atual = web;
            var diag = new List<string>();
            var h = new HospedeWebView2(nome, janela.Dispatcher, () => atual, w => atual = w, perfil, l => diag.Add(l),
                argumentos is null ? null : () => new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = argumentos })
            {
                ArgumentosAnteriores = anteriores,
            };
            hospedes.Add(h);
            return (h, () => atual, diag);
        }

        async Task<SondaPerfilWebView2.Viva?> Sonda(string perfil, string? argumentos, string rotulo)
        {
            var (s, motivo) = await SondaPerfilWebView2.Viva.AbrirAsync(perfil, argumentos);
            if (s is null) { checar(false, $"{rotulo} a sonda (o caixa anterior) abriu o navegador no perfil: {motivo}"); return null; }
            sondas.Add(s);
            return s;
        }

        try
        {
            // RP-0 PC LIMPO: nada muda em relação à 1.0.34
            {
                var perfil = Path.Combine(raiz, "limpo", "webview");
                var (h, atual, diag) = Camada("limpo", perfil, Chat133, SemOpcao);
                Exception? erro = null;
                CoreWebView2? core = null;
                try { core = await h.IniciarControleAsync(); } catch (Exception ex) { erro = ex; }
                var meus = NavegadorDoPerfil.Listar(perfil);
                checar(erro is null && core is not null && !h.ComOpcoesAnteriores && h.AmbientesCriados == 1 && h.Recriacoes == 0
                       && h.PerfilEmUsoDesde is null && !diag.Any(l => l.Contains("perfil", StringComparison.Ordinal)),
                    $"RP-0 PC limpo: abre de primeira com as opções desta versão, um ambiente, nenhuma troca de controle ({erro?.GetType().Name ?? "ok"}, ambientes={h.AmbientesCriados}, recriações={h.Recriacoes})");
                checar(meus.Count == 1 && meus[0].PaiPid == Environment.ProcessId && meus[0].PaiVivo && core is not null && meus[0].Pid == (int)core.BrowserProcessId
                       && NavegadorDoPerfil.ArgumentosDeQuemAbriu(meus[0].Linha).ToHashSet().SetEquals(NavegadorDoPerfil.Argumentos(Chat133)),
                    $"RP-0b a leitura do Windows acha o navegador do perfil, quem o abriu (este processo, vivo) e as opções dele, em qualquer ordem ({(meus.Count == 1 ? NavegadorDoPerfil.Descrever(meus[0]) : meus.Count + " navegadores")})");
            }

            // RP-1..3 A SAVASSI: o chat da 1.0.32 (sem opção) ainda vivo no perfil, e o caixa novo com as opções da 1.0.33
            {
                var perfil = Path.Combine(raiz, "savassi", "webview");
                var antigo = await Sonda(perfil, null, "RP-1");
                if (antigo is not null)
                {
                    var (h, atual, diag) = Camada("chat", perfil, Chat133, SemOpcao);
                    Exception? erro = null;
                    CoreWebView2? core = null;
                    var relogio = Stopwatch.StartNew();
                    try { core = await h.IniciarControleAsync(); } catch (Exception ex) { erro = ex; }
                    relogio.Stop();
                    string? conta = null;
                    try { if (core is not null) conta = await core.ExecuteScriptAsync("1+1"); } catch { }
                    checar(erro is null && core is not null && conta == "2",
                        $"RP-1 (a Savassi) com o navegador da 1.0.32 vivo no perfil, o chat ABRE e roda script ({(erro is null ? "abriu" : "lançou " + erro.GetType().Name + " 0x" + erro.HResult.ToString("X8") + " " + FalhaWebView2.Classificar(erro))}, {relogio.Elapsed.TotalSeconds:0.0} s)");
                    checar(core is not null && (int)core.BrowserProcessId == antigo.Navegador && h.ComOpcoesAnteriores
                           && diag.Any(l => l.Contains("o perfil recusou as opções", StringComparison.Ordinal) && l.Contains("0x8007139F", StringComparison.Ordinal)
                                            && l.Contains($"aberto por pid={antigo.Processo.Id} (vivo)", StringComparison.Ordinal) && l.Contains("exe=Pdv.Testes.exe", StringComparison.Ordinal))
                           && diag.Any(l => l.Contains("abriu com as opções anteriores (nenhuma)", StringComparison.Ordinal)),
                        $"RP-1b entrou no navegador do caixa anterior com as opções dele, e o diagnóstico diz quem segurava o perfil ({string.Join(" / ", diag.Where(l => l.Contains("perfil")).Take(3))})");

                    var saiu = await antigo.FecharAsync();
                    string? depois = null;
                    try { if (core is not null) depois = await core.ExecuteScriptAsync("2+2"); } catch { }
                    checar(saiu && depois == "4" && core is not null && (int)core.BrowserProcessId == antigo.Navegador && SondaPerfilWebView2.Vivo(antigo.Navegador),
                        $"RP-2 o caixa anterior sai e o chat segue vivo no mesmo navegador (saiu={saiu}, script={depois ?? "falhou"})");

                    var linhasAntes = diag.Count(l => l.Contains("o perfil recusou", StringComparison.Ordinal));
                    h.Recriar("vigia: chat parado", descartarAmbiente: true);
                    Exception? erro3 = null;
                    try { await h.IniciarControleAsync(); } catch (Exception ex) { erro3 = ex; }
                    checar(erro3 is null && atual().CoreWebView2 is not null && h.ComOpcoesAnteriores
                           && diag.Count(l => l.Contains("o perfil recusou", StringComparison.Ordinal)) == linhasAntes,
                        $"RP-3 no mesmo caixa, controle e ambiente novos ficam nas opções que entraram (sem nova recusa); a próxima abertura do caixa tenta as desta versão ({erro3?.GetType().Name ?? "ok"})");
                }
            }

            // RP-4 o WhatsApp da 1.0.32 vivo no perfil
            {
                var perfil = Path.Combine(raiz, "wa", "webview-whatsapp");
                var antigo = await Sonda(perfil, Wa132, "RP-4");
                if (antigo is not null)
                {
                    var (h, _, diag) = Camada("whatsapp", perfil, Wa133, new string?[] { Wa132 });
                    Exception? erro = null;
                    CoreWebView2? core = null;
                    try { core = await h.IniciarControleAsync(); } catch (Exception ex) { erro = ex; }
                    checar(erro is null && core is not null && (int)core.BrowserProcessId == antigo.Navegador && h.ComOpcoesAnteriores,
                        $"RP-4 o WhatsApp abre com o da 1.0.32 vivo no perfil, no navegador dele ({(erro is null ? "abriu" : "lançou " + erro.GetType().Name + " 0x" + erro.HResult.ToString("X8"))})");
                    await antigo.FecharAsync();
                }
            }

            // RP-5/6 dono com opções que o caixa nunca teve: espera, sem teto, e abre quando ele sai
            {
                var perfil = Path.Combine(raiz, "estranho", "webview");
                var estranho = await Sonda(perfil, "--pdv-teste-perfil-preso", "RP-5");
                if (estranho is not null)
                {
                    var (h, _, diag) = Camada("chat", perfil, Chat133, SemOpcao);
                    Exception? erro = null;
                    try { await h.IniciarControleAsync(); } catch (Exception ex) { erro = ex; }
                    var tipo = erro is null ? (TipoFalhaWeb?)null : h.Falhou("iniciar", erro);
                    var painel = tipo is { } t ? FalhaWebView2.Painel(t, "O chat", h.EsperandoOPerfil(DateTime.Now)) : "";
                    var depois5 = tipo is { } t5 ? FalhaWebView2.Painel(t5, "O chat", h.EsperandoOPerfil(DateTime.Now.AddMinutes(6))) : "";
                    checar(erro is PerfilEmUsoException && tipo == TipoFalhaWeb.PerfilEmUso && !h.ControleUsado && h.VezesPerfilEmUso == 1
                           && h.PerfilEmUsoDesde is not null && painel.Contains("esperando o caixa anterior", StringComparison.Ordinal)
                           && depois5.StartsWith("Reinicie o computador", StringComparison.Ordinal),
                        $"RP-5 dono vivo com opções desconhecidas: 'perfil em uso', controle trocado, e o painel espera e depois manda reiniciar ({erro?.GetType().Name ?? "abriu?"}, {painel})");

                    var saiu = await estranho.FecharAsync() && await SondaPerfilWebView2.NavegadorSaiuAsync(estranho.Navegador, TimeSpan.FromSeconds(10));
                    Exception? erro6 = null;
                    try { await h.IniciarControleAsync(); } catch (Exception ex) { erro6 = ex; }
                    checar(saiu && erro6 is null && !h.ComOpcoesAnteriores && h.PerfilEmUsoDesde is null && h.VezesPerfilEmUso == 0
                           && diag.Any(l => l.Contains("abriu depois de", StringComparison.Ordinal)),
                        $"RP-6 quando o dono sai, a próxima tentativa abre com as opções desta versão e a espera zera ({erro6?.GetType().Name ?? "ok"})");
                }
            }

            // RP-7 esperar o perfil não gasta o teto da hora (6 por hora para o resto)
            {
                var (h, atual, _) = Camada("teto", Path.Combine(raiz, "teto", "webview"), Chat133, SemOpcao);
                var falhas = 0;
                for (var i = 0; i < 8; i++)
                {
                    await h.IniciarControleAsync();
                    if (h.Falhou("teto " + i, new PerfilEmUsoException("teste", new COMException("recusa", unchecked((int)0x8007139F)))) == TipoFalhaWeb.PerfilEmUso) falhas++;
                }
                Exception? depois = null;
                try { await h.IniciarControleAsync(); } catch (Exception ex) { depois = ex; }
                checar(falhas == 8 && h.Recriacoes == 8 && depois is null && atual().CoreWebView2 is not null,
                    $"RP-7 oito esperas pelo perfil seguidas trocam o controle oito vezes e ele nunca fica parado pelo teto (recriações={h.Recriacoes}, {depois?.GetType().Name ?? "abriu"})");
            }

            // RP-8 ÓRFÃO: o navegador ficou no perfil sem quem o abriu. Encerra só ele e abre com as opções desta versão.
            {
                var perfil = Path.Combine(raiz, "orfao", "webview");
                var antigo = await Sonda(perfil, null, "RP-8");
                if (antigo is not null)
                {
                    var (h, _, diag) = Camada("chat", perfil, Chat133, SemOpcao);
                    var encerrados = new List<int>();
                    // a sonda segue viva; a lista diz que ela morreu (é o que o caixa veria com o Pdv.exe anterior morto)
                    h.ListarDonos = p => NavegadorDoPerfil.Listar(p).Select(d => d with { PaiVivo = false }).ToList();
                    h.EncerrarDono = (d, prazo) => { encerrados.Add(d.Pid); return NavegadorDoPerfil.Encerrar(d, prazo); };
                    Exception? erro = null;
                    CoreWebView2? core = null;
                    try { core = await h.IniciarControleAsync(); } catch (Exception ex) { erro = ex; }
                    checar(erro is null && core is not null && encerrados.SequenceEqual(new[] { antigo.Navegador })
                           && !SondaPerfilWebView2.Vivo(antigo.Navegador) && (int)core.BrowserProcessId != antigo.Navegador
                           && !h.ComOpcoesAnteriores && diag.Any(l => l.Contains("navegador órfão no perfil encerrado", StringComparison.Ordinal)),
                        $"RP-8 órfão: encerra SÓ o navegador do perfil e abre com as opções desta versão ({(erro is null ? "abriu" : "lançou " + erro.GetType().Name)}, encerrados={string.Join(",", encerrados)})");
                    await antigo.FecharAsync();
                }
            }

            // RP-9/10 A TROCA DE VERSÃO (instalador): espera e encerra os navegadores do caixa anterior, e só eles
            {
                var pdvNativo = Path.Combine(raiz, "PdvNativo");
                var antigo = await Sonda(Path.Combine(pdvNativo, "webview"), null, "RP-9");
                if (antigo is not null)
                {
                    var outros = OutrosNavegadores(pdvNativo);
                    // o filtro do instalador é o exe do caixa: com "Pdv.exe" o navegador da sonda (aberto pelo
                    // Pdv.Testes.exe) NÃO é tocado; com o exe dela, é
                    var doPdv = await Task.Run(() => Instalacao.EsperarOCaixaAnteriorSair(() => false, TimeSpan.FromSeconds(30), pdvNativo, TimeSpan.FromSeconds(1)));
                    var intocado = SondaPerfilWebView2.Vivo(antigo.Navegador);
                    var exeDaSonda = Path.GetFileName(Environment.ProcessPath ?? "Pdv.Testes.exe");
                    var linhas = await Task.Run(() => Instalacao.EsperarOCaixaAnteriorSair(() => false, TimeSpan.FromSeconds(30), pdvNativo, TimeSpan.FromSeconds(1), exeDaSonda));
                    var sumiu = await SondaPerfilWebView2.NavegadorSaiuAsync(antigo.Navegador, TimeSpan.FromSeconds(5));
                    var outrosVivos = outros.Count(SondaPerfilWebView2.Vivo);
                    checar(linhas.Count == 2 && linhas[0].StartsWith("o caixa anterior fechou", StringComparison.Ordinal)
                           && linhas[1].Contains("1 de 1 ficaram", StringComparison.Ordinal) && linhas[1].Contains("encerrado navegador pid=" + antigo.Navegador, StringComparison.Ordinal)
                           && sumiu && NavegadorDoPerfil.ListarDentroDe(pdvNativo).Count == 0,
                        $"RP-9 a troca de versão espera o navegador do caixa anterior e encerra o que ficou no perfil ({string.Join(" / ", linhas)})");
                    checar(outros.Count == outrosVivos && intocado && doPdv.Count == 2 && doPdv[1] == "navegadores do caixa anterior: nenhum",
                        $"RP-9b nenhum outro navegador da máquina é tocado: nem os de fora de PdvNativo ({outrosVivos} de {outros.Count} vivos), nem o de dentro que outro programa abriu ({doPdv[^1]})");
                    await antigo.FecharAsync();
                }
                var vazio = await Task.Run(() => Instalacao.EsperarOCaixaAnteriorSair(() => true, TimeSpan.FromSeconds(1), pdvNativo, TimeSpan.FromSeconds(1)));
                checar(vazio.Count == 2 && vazio[0] == "o caixa anterior não fechou em 1 s; a troca segue com ele aberto"
                       && vazio[1] == "navegadores do caixa anterior: nenhum",
                    $"RP-10 a troca diz quando o caixa anterior não fechou (antes seguia calada) e quando não havia navegador ({string.Join(" / ", vazio)})");
            }
        }
        finally
        {
            foreach (var s in sondas) { try { await s.DisposeAsync(); } catch { } }
            foreach (var h in hospedes) { try { h.Encerrar(); } catch { } }
            foreach (var w in grade.Children.OfType<WebView2>().ToList()) { try { w.Dispose(); } catch { } }
            try { janela.Close(); } catch { }
            await Task.Delay(1000);
        }
    }

    /// <summary>Os navegadores principais desta máquina FORA de <paramref name="pasta"/> (Teams, WhatsApp, os da suíte...).</summary>
    private static List<int> OutrosNavegadores(string pasta)
        => NavegadorDoPerfil.ListarTodos().Where(n => !NavegadorDoPerfil.EhDentroDe(n.Linha, pasta)).Select(n => n.Pid).ToList();

    private static string Corpo(string fonte, string assinatura)
    {
        var i = fonte.IndexOf(assinatura, StringComparison.Ordinal);
        if (i < 0) return "";
        var fim = fonte.Length;
        foreach (var marca in new[] { "\n    private ", "\n    public ", "\n    internal ", "\n    /// <summary>", "\n    // ──" })
        {
            var j = fonte.IndexOf(marca, i + assinatura.Length, StringComparison.Ordinal);
            if (j >= 0 && j < fim) fim = j;
        }
        return fonte[i..fim];
    }

    private static string Ler(params string[] partes)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Pdv.csproj")))
            {
                var alvo = Path.Combine(new[] { d.FullName }.Concat(partes).ToArray());
                return File.Exists(alvo) ? File.ReadAllText(alvo).Replace("\r\n", "\n") : "";
            }
        return "";
    }
}
