using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// O WHATSAPP DA LOJA dentro do caixa (11/09/2026, pedido do dono). O WhatsApp Web
/// mora num WebView2 com perfil próprio em ProgramData: o QR é lido uma vez e o
/// login sobrevive a reinício e a atualização do exe. A camada vive no MainWindow e
/// nunca é destacada: trocar de aba não derruba a conversa nem o observador.
///
/// O que o caixa lê da página, a cada 5 s pelo relógio desta tela (o relógio de
/// dentro da página é freado pelo Chromium quando ela fica em segundo plano):
///  · o TÍTULO ("(3) WhatsApp"), com plano B pelos selos das conversas → não lidas;
///  · o ESTADO da sessão (lista de conversas, tela do QR, carregando, sem conexão),
///    lido pela ESTRUTURA do DOM primeiro e por texto pt/en só como reforço → a vigia
///    (<see cref="SessaoWhatsApp"/>) decide quando avisar que "o QR caiu".
/// O número e a palavra vão para o <see cref="ServicoWhatsApp"/>, e é ele que acende
/// o selo, toca e avisa na venda.
///
/// A página fica VISÍVEL para o Chromium mesmo fora da tela (o MainWindow a empurra
/// para fora da janela em vez de recolher): assim o WhatsApp Web toca o toque
/// ORIGINAL dele quando chega mensagem, como toca com a aba aberta. Se ele ficar
/// calado, o caixa toca a reserva (ver ServicoWhatsApp.TocarSeAPaginaCalar).
///
/// Quiosque: sem DevTools, sem menu de contexto, sem janela nova, e as
/// notificações do navegador ficam negadas (o aviso é o do caixa, não o do Windows).
/// Máquina sem o runtime WebView2 não derruba o caixa: a tela explica e o resto segue.
/// </summary>
public partial class ChatWhatsApp : UserControl
{
    public event Action? Voltou;
    /// <summary>O WebView2 nasceu (ou a página carregou): o MainWindow devolve o teclado ao caixa.</summary>
    public event Action? Iniciou;

    private const string UrlWhatsApp = "https://web.whatsapp.com/";
    /// <summary>Subpasta do perfil em ProgramData\PdvNativo. Separada da do chat do iFood.</summary>
    public const string PastaPerfil = "webview-whatsapp";

    /// <summary>
    /// Flags do Chromium só para ESTE perfil: o toque da página sem exigir gesto
    /// (autoplay) e sem frear os timers em segundo plano. O caixa é um quiosque; a
    /// política de "site chato" do navegador não se aplica.
    /// </summary>
    public const string ArgumentosDoNavegador =
        "--autoplay-policy=no-user-gesture-required --disable-background-timer-throttling --disable-renderer-backgrounding";

    private bool _pronto, _iniciando;
    private DispatcherTimer? _poll;
    private int _recargasNaHora; private DateTime _janelaRecargas = DateTime.MinValue;
    private int _falhasSeguidas;
    private string? _ultimaSessaoDiag;
    private static readonly System.Diagnostics.Stopwatch _relogio = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// Uma linha datada em ProgramData\PdvNativo\whatsapp-diagnostico.txt (recomeça em 1 MB).
    /// Só marcos: quando o WebView2 nasceu, se a página carregou, o número de não lidas,
    /// o estado da sessão e se a página tocou som. Nunca o conteúdo de mensagem. É o
    /// que responde "o WhatsApp caiu a que horas?" sem ninguém precisar reproduzir.
    /// </summary>
    internal static void Diag(string texto)
    {
        try
        {
            var caminho = Path.Combine(Banco.Pasta, "whatsapp-diagnostico.txt");
            // passou de 1 MB: vira .1 (a hora da queda de ontem não pode sumir junto)
            if (File.Exists(caminho) && new FileInfo(caminho).Length > 1_000_000)
                File.Move(caminho, Path.Combine(Banco.Pasta, "whatsapp-diagnostico.1.txt"), true);
            File.AppendAllText(caminho, $"{DateTime.Now:dd/MM HH:mm:ss}  +{_relogio.Elapsed.TotalSeconds,7:0.0}s  {texto}{Environment.NewLine}");
        }
        catch { /* diagnóstico nunca atrapalha o caixa */ }
    }

    public ChatWhatsApp()
    {
        InitializeComponent();
        ServicoWhatsApp.SessaoMudou += PintarEstado;
        Dispatcher.ShutdownStarted += (_, _) => { try { _poll?.Stop(); } catch { } };
    }

    /// <summary>Pré-aquece o WebView2 para o selo acender na venda antes de alguém abrir a aba.</summary>
    public async Task PreAquecerAsync() { if (!_pronto) await IniciarAsync(); }

    private async Task IniciarAsync()
    {
        if (_iniciando || _pronto) return;
        _iniciando = true;
        try
        {
            TxtEstado.Text = "carregando…";
            Diag($"iniciar: visivel={IsVisible} carregado={IsLoaded}");
            var perfil = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PdvNativo", PastaPerfil);
            Directory.CreateDirectory(perfil);

            var opcoes = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = ArgumentosDoNavegador };
            var ambiente = await CoreWebView2Environment.CreateAsync(null, perfil, opcoes);
            Diag("ambiente ok: runtime " + ambiente.BrowserVersionString);
            await Web.EnsureCoreWebView2Async(ambiente);
            var core = Web.CoreWebView2;
            Diag($"core ok: visivel={IsVisible}");
            Iniciou?.Invoke();

            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            // Notificação, câmera, microfone e localização: negadas. O aviso de mensagem é
            // o do caixa; o resto não tem lugar num PC de balcão.
            core.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind is CoreWebView2PermissionKind.Notifications
                    or CoreWebView2PermissionKind.Camera
                    or CoreWebView2PermissionKind.Microphone
                    or CoreWebView2PermissionKind.Geolocation)
                    e.State = CoreWebView2PermissionState.Deny;
            };
            // Link que abriria outra janela fica nesta mesma tela.
            core.NewWindowRequested += (_, e) => e.Handled = true;

            // O toque ORIGINAL do WhatsApp é a página quem toca. Anotar quando ela toca é
            // o que deixa o caixa ficar quieto em vez de tocar em cima (dois toques) ou
            // tocar a reserva quando ela calou (nenhum).
            core.IsDocumentPlayingAudioChanged += (_, _) =>
            {
                if (core.IsDocumentPlayingAudio) ServicoWhatsApp.PaginaTocou();
                Diag($"audio da pagina: {(core.IsDocumentPlayingAudio ? "tocando" : "parou")}");
            };

            core.NavigationCompleted += (_, e) =>
            {
                Diag($"navegou: {(e.IsSuccess ? "ok" : "falhou " + e.WebErrorStatus)} visivel={IsVisible}");
                if (e.IsSuccess) { _falhasSeguidas = 0; ServicoWhatsApp.ReportarSessao("carregando"); Iniciou?.Invoke(); return; }
                // Recarregar tocado duas vezes: a 1ª navegação é cancelada pela 2ª. Não é
                // falha, e agendar recarga aqui derrubaria a página boa 30 s depois.
                if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
                // PC da loja liga antes do Wi-Fi: não é a sessão que caiu, é a página que
                // não abriu. Reporta "pc" e tenta de novo com recuo (30, 60, 120 s).
                ServicoWhatsApp.ReportarSessao(System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable() ? "carregando" : "pc");
                var espera = TimeSpan.FromSeconds(Math.Min(120, 30 * (1 << Math.Min(_falhasSeguidas, 2))));
                _falhasSeguidas++;
                _ = RecarregarDepoisAsync(espera, "navegacao falhou");
            };
            // Processo caiu. Cada tipo pede uma coisa (doc do WebView2 1.0.4129):
            //  · renderer morreu: a página fica branca; Reload resolve;
            //  · o PROCESSO DO NAVEGADOR morreu: o CoreWebView2 inteiro é inútil, "the app
            //    has to recreate a new WebView"; Reload lançaria para sempre;
            //  · renderer só sem responder (sincronizando milhares de mensagens num PC
            //    fraco), GPU/utilitário: o próprio WebView2 se recupera; recarregar aqui
            //    viraria tempestade de Reload no meio da sincronização.
            core.ProcessFailed += (_, e) =>
            {
                Diag($"processo caiu: {e.ProcessFailedKind} {e.Reason}");
                switch (e.ProcessFailedKind)
                {
                    case CoreWebView2ProcessFailedKind.RenderProcessExited:
                        ServicoWhatsApp.ReportarSessao("semleitura");
                        _ = RecarregarDepoisAsync(TimeSpan.FromSeconds(5), "renderer caiu");
                        break;
                    case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                        ServicoWhatsApp.ReportarSessao("semleitura");
                        _ = RecriarAsync("navegador caiu");
                        break;
                    default:
                        break;   // só o diagnóstico
                }
            };
            core.WebMessageReceived += OnWebMessage;

            await core.AddScriptToExecuteOnDocumentCreatedAsync(ScriptContador);

            Web.Source = new Uri(UrlWhatsApp);
            Web.Visibility = Visibility.Visible;
            PainelErro.Visibility = Visibility.Collapsed;
            _pronto = true;

            // O RELÓGIO OFICIAL: 5 s, na thread da tela. Reconta e relê o estado mesmo se
            // o observador da página falhar, e é aqui que a vigia bate (silêncio e
            // repetição do aviso), sem timer novo e sem thread nova.
            _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _poll.Tick += async (_, _) =>
            {
                try
                {
                    if (!_pronto || Web.CoreWebView2 is null) return;
                    await Web.CoreWebView2.ExecuteScriptAsync("window.pdvContarWa && window.pdvContarWa()");
                }
                catch (Exception ex) { Diag("tick: " + ex.GetType().Name); }
                // a vigia bate MESMO quando a página não responde: é aí que SemLeitura e a
                // repetição do aviso importam
                finally { try { ServicoWhatsApp.Bater(); } catch { } }
            };
            _poll.Start();
        }
        catch (Exception ex)
        {
            Diag("falhou: " + ex.GetType().Name + " " + ex.Message);
            Web.Visibility = Visibility.Collapsed;
            PainelErro.Visibility = Visibility.Visible;
            TxtEstado.Text = "";
            TxtErro.Text =
                "O componente de navegação do Windows (WebView2) não está disponível " +
                "nesta máquina. Instale o \"WebView2 Runtime\" da Microsoft e abra o " +
                "WhatsApp de novo. O restante do caixa segue funcionando normalmente.\n\n" +
                "Detalhe técnico: " + ex.Message;
            ServicoWhatsApp.SemComponente();
        }
        finally { _iniciando = false; }
    }

    /// <summary>
    /// O processo do navegador morreu: o controle WebView2 atual é inútil (toda chamada
    /// lança). Troca o controle por um novo no mesmo lugar da grade e inicia de novo,
    /// com o mesmo teto de recargas. O botão Recarregar cai aqui também.
    /// </summary>
    private async Task RecriarAsync(string motivo)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (DateTime.UtcNow - _janelaRecargas > TimeSpan.FromHours(1)) { _janelaRecargas = DateTime.UtcNow; _recargasNaHora = 0; }
            if (++_recargasNaHora > 6) { Diag("recriar: teto da hora"); return; }
            Diag($"recriando o WebView2 ({motivo})");
            _poll?.Stop(); _poll = null;
            _pronto = false; _iniciando = false;
            var grade = Web.Parent as Grid;
            var velho = Web;
            var novo = new Microsoft.Web.WebView2.Wpf.WebView2 { Visibility = Visibility.Collapsed };
            Grid.SetRow(novo, Grid.GetRow(velho));
            if (grade is not null)
            {
                var i = grade.Children.IndexOf(velho);
                grade.Children.Remove(velho);
                grade.Children.Insert(Math.Max(0, i), novo);
            }
            Web = novo;
            try { velho.Dispose(); } catch { }
            ServicoWhatsApp.Recarregando();
            await IniciarAsync();
        }
        catch (Exception ex) { Diag("recriar: " + ex.GetType().Name + " " + ex.Message); }
    }

    /// <summary>Recarga automática com teto: no máximo 6 por hora, para não virar moedor.</summary>
    private async Task RecarregarDepoisAsync(TimeSpan espera, string motivo)
    {
        try
        {
            await Task.Delay(espera);
            if (!_pronto || Web.CoreWebView2 is null) return;
            if (DateTime.UtcNow - _janelaRecargas > TimeSpan.FromHours(1)) { _janelaRecargas = DateTime.UtcNow; _recargasNaHora = 0; }
            if (++_recargasNaHora > 6) { Diag("recarga automatica: teto da hora"); return; }
            Diag($"recarga automatica ({motivo})");
            ServicoWhatsApp.Recarregando();
            Web.CoreWebView2.Reload();
        }
        catch (Exception ex) { Diag("recarga: " + ex.GetType().Name); }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Só a página do WhatsApp fala com o caixa (iframes de terceiros não).
        if (!(e.Source ?? "").StartsWith(UrlWhatsApp, StringComparison.OrdinalIgnoreCase)) return;
        string? txt;
        try { txt = e.TryGetWebMessageAsString(); }
        catch { return; }
        if (string.IsNullOrEmpty(txt)) return;
        try
        {
            using var doc = JsonDocument.Parse(txt);
            var tipo = doc.RootElement.TryGetProperty("tipo", out var t) ? t.GetString() : null;
            if (tipo == "naolidas")
            {
                if (doc.RootElement.TryGetProperty("total", out var n) && n.TryGetInt32(out var total))
                {
                    Diag($"nao lidas: {total}");
                    ServicoWhatsApp.Reportar(total);
                }
                else if (doc.RootElement.TryGetProperty("titulo", out var tit))
                    ServicoWhatsApp.ReportarTitulo(tit.GetString());
            }
            else if (tipo == "sessao")
            {
                var estado = doc.RootElement.TryGetProperty("estado", out var es) ? es.GetString() : null;
                var evidencia = doc.RootElement.TryGetProperty("evidencia", out var ev) ? ev.GetString() : null;
                if (estado != _ultimaSessaoDiag) { _ultimaSessaoDiag = estado; Diag($"sessao: {estado} ({evidencia})"); }
                ServicoWhatsApp.ReportarSessao(estado);
            }
        }
        catch { /* mensagem malformada não derruba nada */ }
    }

    /// <summary>Um pintor só para o rótulo do cabeçalho: o estado da sessão manda.</summary>
    private void PintarEstado(EstadoWa e)
    {
        try { Dispatcher.BeginInvoke(() => { if (_pronto) TxtEstado.Text = SessaoWhatsApp.Cabecalho(e); }); }
        catch { }
    }

    private void Recarregar(object sender, RoutedEventArgs e)
    {
        ServicoWhatsApp.Recomecar();      // a contagem recomeça; a vigia da sessão NÃO
        ServicoWhatsApp.Recarregando();
        if (!_pronto) { _ = IniciarAsync(); return; }
        // CoreWebView2 lança quando o processo do navegador morreu: aí é recriar, não recarregar
        try { Web.CoreWebView2?.Reload(); } catch { _ = RecriarAsync("recarregar com o navegador morto"); }
    }

    private void Voltar(object sender, RoutedEventArgs e) => Voltou?.Invoke();

    /// <summary>
    /// Roda a cada carga da página (só na janela de cima, nunca em iframes). Lê o
    /// título ("(3) WhatsApp") e, sem número nele, soma os selos de não lidas; e lê o
    /// ESTADO da sessão pela estrutura da página. Manda só quando muda, mais um
    /// heartbeat do estado a cada ~30 s.
    ///
    /// Os marcadores foram conferidos contra o HTML e os bundles de web.whatsapp.com
    /// (rev 1047311227, 11/09/2026): a lista de conversas é <c>#pane-side</c> (o próprio
    /// WhatsApp usa esse id); a tela do QR tem <c>canvas[aria-label^="Scan this QR code"]</c>
    /// (string fixa em inglês mesmo no bundle pt-BR), <c>div[data-ref]</c> e o checkbox
    /// <c>#auto-logout-toggle</c> ("Continuar conectado neste navegador"); a sessão
    /// gravada é <c>localStorage['last-wid-md']</c> (é o teste de "deslogado" do próprio
    /// WhatsApp). Textos pt/en entram só como reforço, com os antigos de 2023 como
    /// segundo reforço (cache velho). Na tela do QR, deixa marcado o "Continuar
    /// conectado": desmarcado, a sessão morre quando o caixa fecha e o QR volta toda
    /// manhã (a causa mais comum de "desloga sem motivo").
    /// </summary>
    private const string ScriptContador = """
        (function () {
          if (window.top !== window) return;
          if (window.__pdvWa) return; window.__pdvWa = true;
          function envia(o){ try { window.chrome.webview.postMessage(JSON.stringify(o)); } catch (e) {} }
          function q(s){ try { return document.querySelector(s); } catch (e) { return null; } }
          function qa(s){ try { return Array.prototype.slice.call(document.querySelectorAll(s)); } catch (e) { return []; } }
          function visivel(el){ if (!el) return false; var r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; }
          function texto(el){ return ((el && (el.innerText || el.textContent)) || '').replace(/\s+/g, ' '); }
          function marcarManterConectado(){
            try {
              // o checkbox "Continuar conectado neste navegador" (id #auto-logout-toggle em 2026;
              // pelo rotulo como plano B). Desmarcado = QR toda manha.
              var cbs = [q('#auto-logout-toggle')].concat(qa('input[type="checkbox"]')).filter(function (x) { return !!x; });
              for (var i = 0; i < cbs.length; i++) {
                var cb = cbs[i]; if (cb.type !== 'checkbox' || cb.checked) continue;
                var rot = texto(cb.closest('label')) + ' ' + texto(q('label[for="' + cb.id + '"]')) + ' ' + (cb.getAttribute('aria-label') || '');
                if (cb.id === 'auto-logout-toggle' || /continuar conectado|stay logged in|manter(-me)? conectado|keep me signed in/i.test(rot)) { cb.click(); return 'marcou manter conectado'; }
              }
            } catch (e) {}
            return '';
          }
          function usarAqui(dialogos){
            // "aberto em outra janela/computador": o caixa e o dono da sessao; toca em Usar aqui.
            try {
              var bs = qa('[role="dialog"] button,[data-animate-modal-popup] button');
              for (var i = 0; i < bs.length; i++) if (/^(usar aqui|usar nesta janela|use here)$/i.test(texto(bs[i]).trim())) { bs[i].click(); return 'usar aqui'; }
            } catch (e) {}
            return '';
          }
          function avisoTelefone(){
            // "Telefone sem conexao" e uma faixa ACIMA da lista (a lista continua na tela):
            // por isso e lida antes da lista, e so fora dela e da conversa aberta.
            try {
              if (visivel(q('[data-icon="alert-phone"]'))) return true;
              var els = qa('div,span,h1,h2,h3,button');
              for (var i = 0; i < els.length; i++) {
                var el = els[i]; if (el.children.length > 3) continue;
                if (el.closest('#pane-side') || el.closest('#main')) continue;
                var tx = (el.textContent || '').trim(); if (tx.length === 0 || tx.length > 80) continue;
                if (/Phone not connected|Telefone desconectado|Celular (sem conex|desconectado|n[ãa]o conectado)|Trying to reach phone|Tentando conectar/i.test(tx) && visivel(el)) return true;
              }
            } catch (e) {}
            return false;
          }
          window.pdvEstadoWa = function () {
            try {
              var corpo = texto(document.body).slice(0, 8000);
              var dialogos = qa('[role="dialog"],[data-animate-modal-popup]').map(texto).join(' | ');
              var temSessao = false;
              try { temSessao = !!(localStorage.getItem('last-wid-md') || localStorage.getItem('last-wid') || localStorage.getItem('WANoiseInfo')); } catch (e) {}
              // 0) celular da loja sem internet: a sessao esta de pe, a faixa e que avisa
              if (avisoTelefone()) return { estado: 'telefone', evidencia: 'aviso do telefone' };
              // 1) lista de conversas na tela = conectado
              if (visivel(q('#pane-side')) ||
                  visivel(q('[aria-label="Chat list"],[aria-label="Lista de conversas"],[aria-label="Search results."],[aria-label="Resultados da pesquisa."]')))
                return { estado: 'conectado', evidencia: 'lista' };
              // 2) aberto em outra janela/computador: a sessao existe; e conflito, nao QR
              if (/open (in another window|on another computer or browser)|aberto em outr[ao] (janela|computador)/i.test(dialogos))
                return { estado: 'carregando', evidencia: 'outra janela ' + usarAqui(dialogos) };
              // 3) a tela do QR (ou do codigo por telefone)
              var qr = q('canvas[aria-label^="Scan this QR code"]') || q('[data-testid="link-device-qr-code"]') || q('#auto-logout-toggle') ||
                       q('div[data-ref]') || q('#link-device-instructions-list');
              if (qr || /Scan to log in|Escaneie para entrar|Link with phone number|Conectar com n[úu]mero de telefone|Enter code on phone|Insira o c[óo]digo no seu celular|Stay logged in on this browser|Continuar conectado neste navegador|Log into WhatsApp Web|Entrar no WhatsApp Web|Etapas para (entrar|acessar)/i.test(corpo))
                return { estado: 'qr', evidencia: 'tela do qr ' + marcarManterConectado() };
              // 4) sem conexao: o PC (navegador ou faixa) ou o celular da loja
              if (!navigator.onLine || /Computer not connected|Computador desconectado|No internet connection|Sem conex[ãa]o [àa] internet/i.test(corpo + ' ' + dialogos) || visivel(q('[data-icon="alert-computer"]')))
                return { estado: 'pc', evidencia: navigator.onLine ? 'aviso do computador' : 'navigator.offline' };
              // 5) carregando/sincronizando (splash, tela React com <progress>, textos)
              if (q('#wa_web_initial_startup') || q('[data-testid="wa-web-loading-screen"]') || q('#app progress') || visivel(q('progress')) ||
                  /Loading your chats|Carregando (suas )?conversas|Organizing messages|Organizando suas mensagens|Downloading messages|Baixando mensagens|Connecting|Conectando/i.test(corpo))
                return { estado: 'carregando', evidencia: 'progresso' };
              // 6) sem nada na tela: a sessao gravada diz se e "ainda montando" ou "deslogado"
              if (temSessao) return { estado: 'carregando', evidencia: 'sessao no storage, tela ainda vazia' };
              if (/^(\(\d+\) )?WhatsApp$/.test(document.title || '')) return { estado: 'qr', evidencia: 'app subiu sem sessao no storage' };
              return { estado: 'desconhecido', evidencia: (document.title || '(sem titulo)') };
            } catch (e) { return { estado: 'desconhecido', evidencia: 'erro ' + e }; }
          };
          var ultimo = null, ultimoEstado = null, ultimoEnvio = 0;
          window.pdvContarWa = function () {
            try {
              // ORDEM: primeiro o estado, depois a contagem. Na volta do QR o "conectado"
              // tem que chegar antes do "(7) WhatsApp", senao o 7 vira "7 mensagens novas"
              // (velhas) e o zero que vem depois apaga o selo.
              var s = window.pdvEstadoWa();
              var agora = Date.now();
              var mudouEstado = s.estado !== ultimoEstado;
              var batida = agora - ultimoEnvio >= 30000;   // heartbeat por TEMPO (tres relogios chamam esta funcao)
              if (mudouEstado || batida) { ultimoEstado = s.estado; ultimoEnvio = agora; envia({ tipo: 'sessao', estado: s.estado, evidencia: s.evidencia }); }
              // a contagem so vale com a lista na tela: fora dela o titulo e "WhatsApp" e o
              // zero viraria linha de base falsa
              if (s.estado !== 'conectado') { ultimo = null; return; }
              var t = document.title || '';
              var m = t.match(/^\s*\((\d+)\)/);
              var n = m ? parseInt(m[1], 10) : 0;
              if (!m) {
                var soma = 0;
                document.querySelectorAll('span[aria-label*="não lida"], span[aria-label*="nao lida"], span[aria-label*="unread"]')
                  .forEach(function (b) {
                    var x = parseInt((b.getAttribute('aria-label') || '').replace(/\D+/g, ''), 10);
                    if (!isNaN(x)) soma += x;
                  });
                if (soma > 0) n = soma;
              }
              if (mudouEstado) ultimo = null;   // primeira leitura conectada: linha de base nova
              if (n !== ultimo || batida) { ultimo = n; envia({ tipo: 'naolidas', total: n }); }
            } catch (e) {}
          };
          var pend = null;
          function agenda(){ if (pend) return; pend = setTimeout(function(){ pend = null; window.pdvContarWa(); }, 300); }
          function liga(){
            try { new MutationObserver(agenda).observe(document.querySelector('title') || document.head, { childList: true, subtree: true, characterData: true }); } catch (e) {}
            try { new MutationObserver(agenda).observe(document.body, { childList: true, subtree: true }); } catch (e) {}
            setInterval(window.pdvContarWa, 3000);
            window.pdvContarWa();
          }
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', liga); else liga();
        })();
        """;
}
