using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Pdv.Nucleo;
using Pdv.Telas;

namespace Pdv.Testes;

/// <summary>
/// AS CAMADAS WEBVIEW2 (chat do iFood e WhatsApp) NÃO DERRUBAM O CAIXA (15/09/2026, Castelo).
///
/// No PC novo do Castelo (PDV 1.0.10) o dono viu o painel "instale o WebView2 Runtime" e depois
/// o aviso "Alguma coisa falhou nesta tela e eu segurei o caixa de pé" com a frase
/// "CoreWebView2Controller members can only be accessed from the UI thread.". A varredura de
/// 15/09 provou que nenhum caminho do PDV toca o WebView2 fora da thread da tela: a frase é
/// do SDK para QUALQUER E_NOINTERFACE do controller, em qualquer thread. Os caminhos reais que
/// levam uma exceção de WebView2 à caixa de aviso são:
///   A) inicialização que falha no meio: o SDK grava o controller e não desfaz; o controle
///      fica "meio vivo" e qualquer mexida de visível/posição/foco lança no Dispatcher;
///   B) processo do navegador morto no chat do iFood (sem ProcessFailed; Reload sem try);
///   C) nova tentativa com OUTRO ambiente num controle que já passou do Ensure:
///      ArgumentException, e o painel manda instalar um runtime que está instalado.
///
/// O que esta suíte protege:
///  · a regra pura (Núcleo): o que é exceção de WebView2, que tipo de falha é, a frase de uma
///    linha do painel, e quando recriar o controle e descartar o ambiente;
///  · o <see cref="HospedeWebView2"/> DE VERDADE, com o runtime desta máquina, numa janela fora
///    da tela: runtime ausente não toca no controle; falha depois do Ensure troca o controle e a
///    nova tentativa funciona sem ArgumentException; navegador morto é reconhecido como controle
///    quebrado (e só aquele), e recriar com ambiente novo volta a funcionar;
///  · as duas telas usam o hospedeiro, marshalam as entradas públicas para a thread da tela, e
///    o App manda exceção de WebView2 para o log e para a camada, nunca para a caixa de aviso.
/// </summary>
public static class TestesCamadaWebView2
{
    public static Task RodarAsync(Action<bool, string> checar)
    {
        RegraPura(checar);
        FonteDasTelas(checar);
        var raiz = Path.Combine(Path.GetTempPath(), "pdv-testes-webview2-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            HostWpf.ExecutarAsync(() => ComWebView2DeVerdadeAsync(checar, raiz), TimeSpan.FromSeconds(150));
        }
        catch (Exception ex)
        {
            checar(false, "o hospedeiro de verdade rodou até o fim — ESCAPOU " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(raiz)) Directory.Delete(raiz, recursive: true); } catch { /* o navegador pode segurar o perfil por uns segundos */ }
        }
        return Task.CompletedTask;
    }

    // ── 1. A REGRA PURA ────────────────────────────────────────────────────────
    private static void RegraPura(Action<bool, string> checar)
    {
        var doController = new InvalidOperationException(FalhaWebView2.MensagemDoController);
        checar(FalhaWebView2.EhDoWebView2(doController),
            "FW-1 a frase do controller (a do Castelo) é reconhecida como falha de WebView2");
        checar(FalhaWebView2.EhDoWebView2(new System.Reflection.TargetInvocationException(doController))
               && FalhaWebView2.EhDoWebView2(new AggregateException(new Exception("fora"), doController)),
            "FW-2 embrulhada (TargetInvocation, AggregateException) continua reconhecida");

        var semRuntime = new WebView2RuntimeNotFoundException("Couldn't find a compatible Webview2 Runtime installation to host WebViews.");
        checar(FalhaWebView2.EhDoWebView2(semRuntime) && FalhaWebView2.Classificar(semRuntime) == TipoFalhaWeb.RuntimeAusente,
            "FW-3 WebView2RuntimeNotFoundException é de WebView2 e é 'runtime ausente'");

        Exception nossa;
        try { _ = new List<int>().First(); throw new InvalidOperationException("não chega"); }
        catch (Exception ex) { nossa = ex; }
        Exception tecla;
        try { string? s = null; _ = s!.Length; throw new Exception("não chega"); }
        catch (Exception ex) { tecla = ex; }
        checar(!FalhaWebView2.EhDoWebView2(nossa) && !FalhaWebView2.EhDoWebView2(tecla) && !FalhaWebView2.EhDoWebView2(null)
               && !FalhaWebView2.EhDoWebView2(new InvalidOperationException("O thread de chamada não pode acessar este objeto")),
            "FW-4 exceção do próprio caixa (e nula) NÃO é de WebView2: essas continuam indo para a caixa de aviso");

        checar(FalhaWebView2.Classificar(new InvalidOperationException("The WebView control is no longer valid because the browser process crashed. To work around this, please listen for the ProcessFailed event to explicitly manage process failures."))
               == TipoFalhaWeb.NavegadorCaiu
               && FalhaWebView2.Classificar(new COMException("fechado", unchecked((int)0x8007139F))) == TipoFalhaWeb.NavegadorCaiu,
            "FW-5 'browser process crashed' e o 0x8007139F (controller fechado) são 'navegador caiu'");
        checar(FalhaWebView2.Classificar(new ArgumentException("WebView2 was already initialized with a different CoreWebView2Environment. Check to see if the Source property was already set or EnsureCoreWebView2Async was previously called with different values."))
               == TipoFalhaWeb.JaIniciadoComOutroAmbiente
               && FalhaWebView2.Classificar(doController) == TipoFalhaWeb.Outra && FalhaWebView2.Classificar(null) == TipoFalhaWeb.Outra,
            "FW-6 'already initialized with a different environment' tem nome próprio; o resto é 'outra'");

        var paineis = Enum.GetValues<TipoFalhaWeb>()
            .SelectMany(t => new[] { ("O chat", t, FalhaWebView2.Painel(t, "O chat")), ("O WhatsApp", t, FalhaWebView2.Painel(t, "O WhatsApp")) })
            .ToList();
        checar(paineis.All(p => p.Item3.Length is > 10 and <= 80 && !p.Item3.Contains('\n') && !p.Item3.Contains('—') && !p.Item3.Contains('–')
                                && !p.Item3.Contains("WebView2", StringComparison.OrdinalIgnoreCase)
                                && !p.Item3.Contains("técnico", StringComparison.OrdinalIgnoreCase)
                                && !p.Item3.Contains("Exception", StringComparison.Ordinal)),
            "FW-7 o painel diz em UMA linha curta o que fazer, sem travessão, sem 'WebView2' e sem detalhe técnico"
            + " (" + string.Join(" | ", paineis.Select(p => p.Item3).Distinct()) + ")");
        checar(FalhaWebView2.Painel(TipoFalhaWeb.RuntimeAusente, "O chat").Contains("suporte", StringComparison.OrdinalIgnoreCase)
               && new[] { TipoFalhaWeb.Outra, TipoFalhaWeb.NavegadorCaiu, TipoFalhaWeb.JaIniciadoComOutroAmbiente }
                   .All(t => FalhaWebView2.Painel(t, "O WhatsApp").StartsWith("O WhatsApp", StringComparison.Ordinal)
                          && FalhaWebView2.Painel(t, "O WhatsApp").Contains(FalhaWebView2.TentarDeNovo, StringComparison.Ordinal)),
            "FW-8 só o runtime ausente manda chamar o suporte; qualquer outra falha diz o que não abriu e manda tocar em Tentar de novo");
        // Revisão 15/09: a mesma falta aparecia com três instruções (rodar o instalador, chamar o
        // suporte, "componente de navegação"), e depois de atualizar pelo botão não há instalador à mão.
        var faltaDoComponente = new[]
        {
            FalhaWebView2.Painel(TipoFalhaWeb.RuntimeAusente, "O chat"),
            SessaoWhatsApp.Aviso(EstadoWa.SemComponente).Acao,
            SessaoWhatsApp.Cabecalho(EstadoWa.SemComponente),
        };
        checar(faltaDoComponente.All(t => t.Contains("componente da Microsoft", StringComparison.OrdinalIgnoreCase)
                                          && t.Contains("chame o suporte", StringComparison.OrdinalIgnoreCase)
                                          && !t.Contains("instalador", StringComparison.OrdinalIgnoreCase)),
            "FW-14 a falta do componente diz a MESMA coisa no painel, no aviso da venda e no cabeçalho da aba (" + string.Join(" | ", faltaDoComponente) + ")");
        checar(FalhaWebView2.TentarDeNovo == "Tentar de novo", "FW-9 o botão se chama 'Tentar de novo'");

        checar(FalhaWebView2.AposFalha(ensureChamado: false, ensureTerminou: false, TipoFalhaWeb.RuntimeAusente) == (false, true)
               && FalhaWebView2.AposFalha(true, false, TipoFalhaWeb.Outra) == (true, true)
               && FalhaWebView2.AposFalha(true, true, TipoFalhaWeb.Outra) == (true, false)
               && FalhaWebView2.AposFalha(true, true, TipoFalhaWeb.NavegadorCaiu) == (true, true)
               && FalhaWebView2.AposFalha(true, true, TipoFalhaWeb.JaIniciadoComOutroAmbiente) == (true, false),
            "FW-10 controle que recebeu Ensure nunca é reaproveitado; o ambiente só fica se o Ensure terminou com ele e o navegador não caiu");

        var linha = FalhaWebView2.Linha("iniciar", doController, 7, 1, null);
        var linhaLonga = FalhaWebView2.Linha("iniciar", new Exception(new string('x', 5000) + "\nsegunda linha"), 9, 1, "152.0.4191.66");
        checar(linha.Contains("thread=7") && linha.Contains("tela=1") && linha.Contains("runtime=ausente") && linha.Contains("InvalidOperationException")
               && linhaLonga.Contains("runtime=152.0.4191.66") && !linhaLonga.Contains('\n') && linhaLonga.Length <= 400,
            "FW-11 a linha de diagnóstico leva onde, tipo, thread, thread da tela e versão do runtime, numa linha só e curta");

        var teto = new TetoPorHora(6);
        var t0 = new DateTime(2026, 9, 15, 14, 0, 0, DateTimeKind.Utc);
        var seis = Enumerable.Range(0, 6).All(i => teto.Permitir(t0.AddMinutes(i)));
        checar(seis && !teto.Permitir(t0.AddMinutes(10)) && !teto.Permitir(t0.AddMinutes(59)) && teto.Permitir(t0.AddMinutes(61)),
            "FW-12 recuperação automática tem teto: 6 por hora, a sétima espera a hora virar");

        var filtro = new FiltroDeRepeticao(3, TimeSpan.FromMinutes(10));
        var tres = Enumerable.Range(0, 3).All(i => filtro.Registrar("a", t0.AddSeconds(i)));
        checar(tres && !filtro.Registrar("a", t0.AddSeconds(5)) && !filtro.Registrar("a", t0.AddMinutes(9)) && filtro.Silenciadas == 2
               && filtro.Registrar("b", t0.AddMinutes(9)) && filtro.Registrar("a", t0.AddMinutes(11)),
            "FW-13 a mesma falha em rajada (a cada layout) vai 3 vezes para o erros.log a cada 10 min, e as outras só contam");
    }

    // ── 2. O FONTE DAS TELAS, DO APP E DA VENDA ────────────────────────────────
    private static void FonteDasTelas(Action<bool, string> checar)
    {
        var chat = Fonte("Telas", "ChatIfood.xaml.cs");
        var chatXaml = Fonte("Telas", "ChatIfood.xaml");
        var wa = Fonte("Telas", "ChatWhatsApp.xaml.cs");
        var waXaml = Fonte("Telas", "ChatWhatsApp.xaml");
        var app = Fonte("App.xaml.cs");
        var venda = Fonte("Telas", "Venda.xaml.cs");
        checar(chat.Length > 0 && wa.Length > 0 && app.Length > 0 && venda.Length > 0 && chatXaml.Length > 0 && waXaml.Length > 0,
            "TW-0 achei as duas telas, os XAML, o App e a venda");

        foreach (var (nome, cs, xaml) in new[] { ("chat", chat, chatXaml), ("whatsapp", wa, waXaml) })
        {
            var iniciar = Corpo(cs, "private async Task IniciarAsync()");
            checar(cs.Contains("new HospedeWebView2(", StringComparison.Ordinal)
                   && iniciar.Contains("Hospede.IniciarControleAsync()", StringComparison.Ordinal)
                   && iniciar.Contains("Hospede.Falhou(", StringComparison.Ordinal)
                   && !cs.Contains("CoreWebView2Environment.CreateAsync(", StringComparison.Ordinal),
                $"TW-1 {nome}: o ambiente e o Ensure passam pelo hospedeiro (reaproveita o ambiente e troca o controle que falhou)");
            checar(iniciar.Contains("FalhaWebView2.Painel(", StringComparison.Ordinal)
                   && !cs.Contains("Detalhe técnico", StringComparison.Ordinal)
                   && !cs.Contains("Instale o \\\"WebView2 Runtime\\\"", StringComparison.Ordinal),
                $"TW-2 {nome}: o painel de erro usa a frase de uma linha (sem 'Detalhe técnico', sem mandar instalar para qualquer erro)");
            checar(xaml.Contains("Click=\"TentarDeNovo\"", StringComparison.Ordinal) && xaml.Contains("Tentar de novo", StringComparison.Ordinal)
                   && Corpo(cs, "private async void TentarDeNovo(").Contains("Hospede.RecriarSeUsado(", StringComparison.Ordinal)
                   && Corpo(cs, "private async void TentarDeNovo(").Contains("IniciarAsync()", StringComparison.Ordinal),
                $"TW-3 {nome}: o painel tem 'Tentar de novo', que recria o controle usado antes de iniciar");
            checar(Corpo(cs, "public Task PreAquecerAsync()").Contains("HospedeWebView2.NaTela(Dispatcher", StringComparison.Ordinal),
                $"TW-4 {nome}: PreAquecerAsync volta para a thread da tela antes de encostar no controle");
            checar(cs.Contains("Hospede.Quebrou +=", StringComparison.Ordinal),
                $"TW-5 {nome}: falha de WebView2 que chega ao Dispatcher vira painel desta camada");
        }

        foreach (var assinatura in new[] { "public async Task<bool> AbrirConversaPorPedidoAsync(", "public async Task<bool> FaleComIfoodAsync(" })
            checar(Corpo(chat, assinatura).Contains("HospedeWebView2.NaTela(Dispatcher", StringComparison.Ordinal),
                $"TW-6 chat: {assinatura.Split('(')[0].Split(' ').Last()} volta para a thread da tela");
        foreach (var assinatura in new[] { "private async void AbrirAjuda(", "private async void AlternarGestorInteiro(", "private async void Diagnostico(", "public async Task<bool> FaleComIfoodAsync(" })
        {
            var corpo = Corpo(chat, assinatura);
            var iTry = corpo.IndexOf("try", StringComparison.Ordinal);
            var iCore = corpo.IndexOf("CoreWebView2", StringComparison.Ordinal);
            checar(corpo.Length > 0 && iTry >= 0 && iCore > iTry,
                $"TW-7 chat: {assinatura.Split('(')[0].Split(' ').Last()} só encosta no CoreWebView2 dentro do try (com o navegador morto ele lança)");
        }
        var recarregar = Corpo(chat, "private void Recarregar(");
        checar(recarregar.IndexOf("try", StringComparison.Ordinal) is var it && it >= 0 && recarregar.IndexOf("Reload()", StringComparison.Ordinal) > it
               && recarregar.Contains("RecriarAsync(", StringComparison.Ordinal),
            "TW-8 chat: Recarregar com o navegador morto recria em vez de lançar na tela");
        checar(chat.Contains("core.ProcessFailed +=", StringComparison.Ordinal)
               && chat.Contains("CoreWebView2ProcessFailedKind.BrowserProcessExited", StringComparison.Ordinal)
               && chat.Contains("CoreWebView2ProcessFailedKind.RenderProcessExited", StringComparison.Ordinal)
               && Corpo(chat, "private async Task RecriarAsync(").Contains("Hospede.Recriar(", StringComparison.Ordinal),
            "TW-9 chat: trata o processo que caiu como o WhatsApp (renderer: recarrega; navegador: recria o controle)");
        checar(Corpo(wa, "private async Task RecriarAsync(").Contains("Hospede.Recriar(", StringComparison.Ordinal)
               && Corpo(wa, "private async Task RecriarAsync(").Contains("NaTela(", StringComparison.Ordinal)
               && wa.Contains("new TetoPorHora(", StringComparison.Ordinal) && chat.Contains("new TetoPorHora(", StringComparison.Ordinal),
            "TW-10 whatsapp e chat: recriar passa pelo hospedeiro, na thread da tela, com o mesmo teto por hora");

        var guarda = Trecho(app, "DispatcherUnhandledException +=", "var args = e.Args;");
        checar(guarda.Contains("FalhaWebView2.EhDoWebView2(", StringComparison.Ordinal)
               && guarda.Contains("HospedeWebView2.AvisarFalhaForaDaCamada(", StringComparison.Ordinal)
               && guarda.IndexOf("FalhaWebView2.EhDoWebView2(", StringComparison.Ordinal) < guarda.IndexOf("MessageBox.Show(", StringComparison.Ordinal)
               && guarda.Contains("HospedeWebView2.Contexto(", StringComparison.Ordinal),
            "TW-11 App: exceção de WebView2 no Dispatcher vai para o erros.log (com thread e runtime) e para a camada, ANTES e no lugar da caixa de aviso");

        checar(new[] { chat, wa }.All(cs => !Regex.IsMatch(cs, @"(TxtEstado|TxtErro)\.Text\s*=[^;]*ex\.Message")),
            "TW-13 chat e whatsapp: texto cru de exceção nunca vai para a tela (o detalhe vai para o arquivo de diagnóstico)");

        var avisos = Regex.Matches(venda, @"NotificarPedidoNovo\(tt\.Result\)").Count;
        var naTela = Regex.Matches(venda, @"Dispatcher\.Invoke\(\(\) => NotificarPedidoNovo\(tt\.Result\)\)").Count;
        checar(avisos >= 2 && avisos == naTela,
            $"TW-12 venda: as continuações do sino e da puxada do KDS (thread do pool) só tocam a tela por Dispatcher.Invoke ({naTela}/{avisos})");
    }

    // ── 3. O HOSPEDEIRO COM O WEBVIEW2 DE VERDADE ──────────────────────────────
    private static async Task ComWebView2DeVerdadeAsync(Action<bool, string> checar, string raiz)
    {
        var versao = HospedeWebView2.VersaoDoRuntime();
        checar(versao is not null, $"RW-0 esta máquina tem o runtime do WebView2 ({versao ?? "ausente"}): sem ele a prova de verdade não roda");
        if (versao is null) return;

        var janela = new Window
        {
            Width = 480, Height = 360, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, ShowActivated = false, Title = "prova do hospedeiro WebView2",
        };
        var grade = new Grid();
        grade.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grade.RowDefinitions.Add(new RowDefinition());
        grade.Children.Add(new TextBlock { Text = "cabeçalho" });
        janela.Content = grade;
        janela.Show();

        var hospedes = new List<HospedeWebView2>();
        // Como no App: exceção de WebView2 que escapa para o Dispatcher (layout, foco) não derruba o
        // host da bateria. Conta, para o diagnóstico da suíte.
        var webNoDispatcher = 0;
        DispatcherUnhandledExceptionEventHandler engolir = (_, e) =>
        {
            if (!FalhaWebView2.EhDoWebView2(e.Exception)) return;
            webNoDispatcher++;
            e.Handled = true;
        };
        janela.Dispatcher.UnhandledException += engolir;
        WebView2 NovoControle()
        {
            var w = new WebView2 { Visibility = Visibility.Collapsed };
            Grid.SetRow(w, 1);
            grade.Children.Add(w);
            return w;
        }
        HospedeWebView2 Hospede(string nome, Func<WebView2> atual, Action<WebView2> trocar, List<string> diag)
        {
            var h = new HospedeWebView2(nome, janela.Dispatcher, atual, trocar, Path.Combine(raiz, nome), l => diag.Add(l));
            hospedes.Add(h);
            return h;
        }
        try
        {
            // RW-1/2 runtime ausente simulado (pasta de navegador vazia), depois "instalado"
            {
                var web = NovoControle(); var atual = web; var diag = new List<string>();
                var h = Hospede("ausente", () => atual, w => atual = w, diag);
                h.PastaDoNavegador = Directory.CreateDirectory(Path.Combine(raiz, "navegador-vazio")).FullName;
                TipoFalhaWeb? tipo = null;
                try { await h.IniciarControleAsync(); }
                catch (Exception ex) { tipo = h.Falhou("teste", ex); }
                checar(tipo == TipoFalhaWeb.RuntimeAusente && ReferenceEquals(atual, web) && web.CoreWebView2 is null
                       && h.Recriacoes == 0 && h.AmbientesCriados == 0 && !h.ControleUsado,
                    $"RW-1 runtime ausente: nem o ambiente nem o controle são tocados (tipo={tipo}, recriações={h.Recriacoes}, ambientes={h.AmbientesCriados})");
                checar(diag.Any(l => l.Contains("thread=", StringComparison.Ordinal) && l.Contains("runtime=ausente", StringComparison.Ordinal)),
                    "RW-2 o diagnóstico da falha diz a thread e que o runtime está ausente");

                h.PastaDoNavegador = null;   // o instalador pôs o runtime
                var core = await h.IniciarControleAsync();
                checar(core is not null && ReferenceEquals(atual, web) && h.AmbientesCriados == 1 && h.ControleUsado,
                    "RW-3 depois de instalar o runtime, a tentativa seguinte funciona no MESMO controle (ele nunca foi tocado)");
            }

            // RW-4/5/6 falha depois do Ensure: troca o controle, reaproveita o ambiente, nova tentativa sem ArgumentException
            {
                var web = NovoControle(); var atual = web; var diag = new List<string>();
                var h = Hospede("depois-do-ensure", () => atual, w => atual = w, diag);
                await h.IniciarControleAsync();
                var indice = grade.Children.IndexOf(web);
                var tipo = h.Falhou("teste", new InvalidOperationException("a etapa depois do Ensure falhou"));
                checar(tipo == TipoFalhaWeb.Outra && h.Recriacoes == 1 && !ReferenceEquals(atual, web)
                       && !grade.Children.Contains(web) && grade.Children.IndexOf(atual) == indice && Grid.GetRow(atual) == 1
                       && atual.Visibility == Visibility.Collapsed && atual.CoreWebView2 is null && !h.ControleUsado,
                    $"RW-4 falha depois do Ensure: o controle usado sai da grade e um novo, recolhido, entra no MESMO lugar (recriações={h.Recriacoes})");
                Exception? naNova = null;
                try { await h.IniciarControleAsync(); } catch (Exception ex) { naNova = ex; }
                checar(naNova is null && atual.CoreWebView2 is not null && h.AmbientesCriados == 1,
                    $"RW-5 a nova tentativa inicia o controle novo com o MESMO ambiente, sem ArgumentException (ambientes={h.AmbientesCriados}{(naNova is null ? "" : ", lançou " + naNova.GetType().Name)})");

                // A prova do defeito que existia: no controle que já passou do Ensure, outro ambiente lança.
                Exception? outroAmbiente = null;
                try
                {
                    var amb = await CoreWebView2Environment.CreateAsync(null, Path.Combine(raiz, "depois-do-ensure"));
                    await atual.EnsureCoreWebView2Async(amb);
                }
                catch (Exception ex) { outroAmbiente = ex; }
                checar(outroAmbiente is ArgumentException && FalhaWebView2.Classificar(outroAmbiente) == TipoFalhaWeb.JaIniciadoComOutroAmbiente
                       && FalhaWebView2.EhDoWebView2(outroAmbiente),
                    $"RW-6 (a prova do caminho C) o mesmo controle com OUTRO ambiente lança ArgumentException, e a regra reconhece ({outroAmbiente?.GetType().Name ?? "não lançou"})");

                // Fora da thread da tela: o hospedeiro recusa, e a exceção do WPF que sai daí é reconhecida como de WebView2.
                Exception? foraDaTela = null, acessoFora = null;
                var controleAntes = atual;
                await Task.Run(async () =>
                {
                    try { await h.IniciarControleAsync(); } catch (Exception ex) { foraDaTela = ex; }
                    try { _ = controleAntes.CoreWebView2; } catch (Exception ex) { acessoFora = ex; }
                });
                checar(foraDaTela is InvalidOperationException && ReferenceEquals(atual, controleAntes) && h.Recriacoes == 1,
                    $"RW-7 chamado fora da thread da tela, o hospedeiro recusa na porta e não mexe em nada ({foraDaTela?.GetType().Name ?? "não lançou"})");
                checar(acessoFora is not null && FalhaWebView2.EhDoWebView2(acessoFora),
                    $"RW-8 o erro de thread que o WebView2 do WPF lança é reconhecido como de WebView2 ({acessoFora?.GetType().Name ?? "não lançou"})");

                var threadTela = Environment.CurrentManagedThreadId;
                var threadDoCorpo = await Task.Run(() => HospedeWebView2.NaTela(janela.Dispatcher, () => Task.FromResult(Environment.CurrentManagedThreadId)));
                checar(threadDoCorpo == threadTela, $"RW-9 NaTela chamado do pool roda o corpo na thread da tela ({threadDoCorpo} = {threadTela})");
            }

            // RW-10/11/12 navegador morto: controle quebrado reconhecido (e só ele), recriar com ambiente novo volta
            {
                var webA = NovoControle(); var atualA = webA; var diagA = new List<string>();
                var webB = NovoControle(); var atualB = webB; var diagB = new List<string>();
                var hA = Hospede("navegador-morto", () => atualA, w => atualA = w, diagA);
                var hB = Hospede("saudavel", () => atualB, w => atualB = w, diagB);
                var coreA = await hA.IniciarControleAsync();
                await hB.IniciarControleAsync();
                var quebrouA = 0; var quebrouB = 0;
                hA.Quebrou += _ => quebrouA++;
                hB.Quebrou += _ => quebrouB++;

                checar(!hA.ControleQuebrado() && !hB.ControleQuebrado(), "RW-10 com os dois navegadores de pé, nenhum controle é dado como quebrado");

                var caiu = false;
                coreA.ProcessFailed += (_, e) => { if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) caiu = true; };
                using (var p = Process.GetProcessById((int)coreA.BrowserProcessId)) p.Kill();
                var relogio = Stopwatch.StartNew();
                while (relogio.Elapsed < TimeSpan.FromSeconds(15) && !(caiu && hA.ControleQuebrado())) await Task.Delay(100);

                HospedeWebView2.AvisarFalhaForaDaCamada(new InvalidOperationException(FalhaWebView2.MensagemDoController));
                checar(caiu && hA.ControleQuebrado() && !hB.ControleQuebrado() && quebrouA == 1 && quebrouB == 0,
                    $"RW-11 navegador morto: só o controle dele é dado como quebrado, e só a camada dele recebe a falha do Dispatcher (caiu={caiu}, A={quebrouA}, B={quebrouB})");

                hA.Recriar("navegador caiu", descartarAmbiente: true);
                Exception? volta = null;
                try { await hA.IniciarControleAsync(); } catch (Exception ex) { volta = ex; }
                checar(volta is null && atualA.CoreWebView2 is not null && hA.AmbientesCriados == 2 && !hA.ControleQuebrado(),
                    $"RW-12 recriar com ambiente novo depois do navegador morto volta a funcionar (ambientes={hA.AmbientesCriados}{(volta is null ? "" : ", lançou " + volta.GetType().Name + ": " + volta.Message)})");
            }

            // RW-13..16 A FRASE DO CASTELO NUM CONTROLE QUE TERMINOU A INICIALIZAÇÃO (revisão 15/09).
            // O SDK escreve "CoreWebView2Controller members can only be accessed from the UI thread."
            // quando o cast do objeto nativo do CONTROLLER dá E_NOINTERFACE. A simulação faz exatamente
            // isso (troca o objeto nativo por um que não implementa a interface): o controller lança a
            // frase, e o CoreWebView2 (outro objeto) continua respondendo.
            {
                var webC = NovoControle(); var atualC = webC; var diagC = new List<string>();
                var webD = NovoControle(); var atualD = webD; var diagD = new List<string>();
                var hC = Hospede("castelo", () => atualC, w => atualC = w, diagC);
                var hD = Hospede("vizinho", () => atualD, w => atualD = w, diagD);
                var coreC = await hC.IniciarControleAsync();
                await hD.IniciarControleAsync();
                var quebrouC = 0; var quebrouD = 0;
                hC.Quebrou += _ => quebrouC++;
                hD.Quebrou += _ => quebrouD++;

                var controllerC = ControllerPorReflexao(atualC) as CoreWebView2Controller;
                var desfazer = QuebrarComoNoCastelo(atualC);
                Exception? doController = null, doNucleoWpf = null; var navegadorDePe = false;
                try { _ = controllerC!.IsVisible; } catch (Exception ex) { doController = ex; }
                try { _ = atualC.CoreWebView2; } catch (Exception ex) { doNucleoWpf = ex; }
                try { navegadorDePe = coreC.BrowserProcessId > 0; } catch { }
                checar(desfazer is not null && doController is not null && FalhaWebView2.EhDoWebView2(doController) && doNucleoWpf is not null && navegadorDePe,
                    $"RW-13 (a simulação) todo membro do controller lança; o CoreWebView2 do controle WPF lança junto, porque o SDK o lê PELO controller; e o navegador segue de pé ({doController?.GetType().Name ?? "não lançou"}/{doNucleoWpf?.GetType().Name ?? "não lançou"}, de pé={navegadorDePe})");

                // Regressão (o achado P1 da revisão foi refutado por esta checagem e pelo IL): com a
                // inicialização terminada, o controller meio vivo é reconhecido pela leitura do núcleo.
                HospedeWebView2.AvisarFalhaForaDaCamada(new InvalidOperationException(FalhaWebView2.MensagemDoController));
                checar(hC.ControleQuebrado() && !hD.ControleQuebrado() && quebrouC == 1 && quebrouD == 0,
                    $"RW-14 controller meio vivo com a inicialização TERMINADA é atribuído ao controle dele (e só a ele): vira painel e recriação (C={quebrouC}, D={quebrouD})");

                var velhoC = atualC;
                var janelaVelha = IntPtr.Zero;
                try { janelaVelha = velhoC.Handle; } catch { }
                var existiaAntes = janelaVelha != IntPtr.Zero && IsWindow(janelaVelha);
                Exception? recriar = null;
                try { hC.Recriar("a frase do Castelo", descartarAmbiente: true); } catch (Exception ex) { recriar = ex; }
                var existeDepois = janelaVelha != IntPtr.Zero && IsWindow(janelaVelha);
                checar(recriar is null && existiaAntes && !existeDepois && !ReferenceEquals(atualC, velhoC),
                    $"RW-15 trocar o controle meio vivo não deixa a janela dele estacionada: o Dispose do SDK para no meio e a janela é destruída (antes={existiaAntes}, depois={existeDepois}{(recriar is null ? "" : ", lançou " + recriar.GetType().Name)})");

                desfazer?.Invoke();
                try { controllerC?.Close(); } catch { }

                Exception? voltaC = null;
                try { await hC.IniciarControleAsync(); } catch (Exception ex) { voltaC = ex; }
                checar(voltaC is null && atualC.CoreWebView2 is not null && !hC.ControleQuebrado() && !hD.ControleQuebrado(),
                    $"RW-16 depois da troca o controle novo inicia, e o vizinho segue intacto{(voltaC is null ? "" : " (lançou " + voltaC.GetType().Name + ")")}");
            }

            // RW-17 falso positivo: exceção de OUTRA camada enquanto o Ensure desta está em curso
            {
                var webE = NovoControle(); var atualE = webE; var diagE = new List<string>();
                var hE = Hospede("em-curso", () => atualE, w => atualE = w, diagE);
                await hE.IniciarControleAsync();
                hE.Recriar("preparar um Ensure pendente", descartarAmbiente: false);
                var quebrouE = 0;
                hE.Quebrou += _ => quebrouE++;
                var recriacoesAntes = hE.Recriacoes;
                var pendente = hE.IniciarControleAsync();
                var estavaPendente = !pendente.IsCompleted;
                HospedeWebView2.AvisarFalhaForaDaCamada(new InvalidOperationException(FalhaWebView2.MensagemDoController));
                Exception? fimE = null;
                try { await pendente; } catch (Exception ex) { fimE = ex; }
                checar(estavaPendente && quebrouE == 0 && hE.Recriacoes == recriacoesAntes && fimE is null && atualE.CoreWebView2 is not null,
                    $"RW-17 falha de outra camada com o Ensure desta em curso: esta não é dada como quebrada nem recriada, e a inicialização termina (quebrou={quebrouE}, pendente={estavaPendente})");
            }

            // RW-18/19 a recriação automática tem teto (o Tentar de novo, não)
            {
                var webF = NovoControle(); var atualF = webF; var diagF = new List<string>();
                var hF = Hospede("teto", () => atualF, w => atualF = w, diagF);
                for (var i = 0; i < 7; i++)
                {
                    await hF.IniciarControleAsync();
                    hF.Falhou("teto " + i, new InvalidOperationException("a etapa depois do Ensure falhou"));
                }
                var parado = atualF;
                Exception? recusou = null;
                try { await hF.IniciarControleAsync(); } catch (Exception ex) { recusou = ex; }
                checar(hF.Recriacoes == 6 && hF.ControleUsado && recusou is not null && ReferenceEquals(atualF, parado),
                    $"RW-18 recriar sozinho tem teto de 6 por hora: a sétima falha deixa o controle parado e a inicialização recusa sem tocar nele (recriações={hF.Recriacoes}, recusou={recusou?.GetType().Name ?? "não"})");
                var recriou = hF.RecriarSeUsado("tentar de novo");
                Exception? depoisF = null;
                try { await hF.IniciarControleAsync(); } catch (Exception ex) { depoisF = ex; }
                checar(recriou && hF.Recriacoes == 7 && depoisF is null && atualF.CoreWebView2 is not null,
                    $"RW-19 o Tentar de novo (gesto de gente) passa do teto: recria e inicia (recriações={hF.Recriacoes})");
            }
        }
        finally
        {
            janela.Dispatcher.UnhandledException -= engolir;
            foreach (var h in hospedes) h.Desligar();
            foreach (var w in grade.Children.OfType<WebView2>().ToList()) { try { w.Dispose(); } catch { } }
            try { janela.Close(); } catch { }
            await Task.Delay(500);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    /// <summary>O CoreWebView2Controller interno do controle WPF (WebView2Base.CoreWebView2Controller), por reflexão.</summary>
    private static object? ControllerPorReflexao(WebView2 w)
    {
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        try
        {
            var baseDoSdk = typeof(WebView2).GetField("m_webview2Base", F)?.GetValue(w);
            return baseDoSdk?.GetType().GetProperty("CoreWebView2Controller", F)?.GetValue(baseDoSdk);
        }
        catch { return null; }
    }

    /// <summary>
    /// Deixa o controller como no Castelo: o objeto nativo dele vira um que não implementa
    /// ICoreWebView2Controller, então todo membro dá E_NOINTERFACE e o SDK lança a frase. O
    /// CoreWebView2 é outro objeto e continua de pé. Devolve como desfazer (null = o SDK mudou).
    /// </summary>
    private static Action? QuebrarComoNoCastelo(WebView2 w)
    {
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        if (ControllerPorReflexao(w) is not { } controller) return null;
        var tipo = controller.GetType();
        var campoNativo = tipo.GetField("_rawNative", F);
        var campoCache = tipo.GetField("_nativeICoreWebView2ControllerValue", F);
        if (campoNativo is null || campoCache is null) return null;
        var nativo = campoNativo.GetValue(controller);
        var cache = campoCache.GetValue(controller);
        campoCache.SetValue(controller, null);
        campoNativo.SetValue(controller, new object());
        return () => { campoNativo.SetValue(controller, nativo); campoCache.SetValue(controller, cache); };
    }

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

    private static string Trecho(string todo, string de, string ate)
    {
        var i = todo.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var f = todo.IndexOf(ate, i, StringComparison.Ordinal);
        return f < 0 ? "" : todo[i..f];
    }

    private static string Fonte(params string[] partes)
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
