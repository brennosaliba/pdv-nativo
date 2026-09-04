using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// O chat do iFood dentro do PDV como PAINEL nativo. Por baixo continua o Gestor
/// de Pedidos num WebView2 (é a única forma de ENVIAR mensagem — não há API
/// pública), mas a tela injeta CSS/JS para mostrar SÓ o chat, observa o DOM para
/// saber quantas mensagens não lidas há (sem depender do protocolo do iFood) e
/// deixa a base do "nativo depois": liga o Network do CDP e captura, em memória,
/// a URL do WebSocket, o token do /chat/v1.0/auth e os quadros recebidos —
/// gravando um diagnóstico local com TUDO mascarado.
///
/// Perfil persistente em ProgramData: o gerente loga UMA vez e a sessão sobrevive
/// a reinício e a atualização do exe. Máquina sem o runtime WebView2 não derruba
/// o caixa: mostra o aviso e o resto do PDV segue.
///
/// RESILIÊNCIA: se os seletores do Gestor mudarem, o isolamento do painel é
/// PULADO (cai no Gestor inteiro) em vez de deixar tela branca — o chat continua
/// acessível pela barra lateral. O contador de não lidas é lido do texto do DOM
/// por função pura (Núcleo), então uma mudança de marcação vira 0, nunca exceção.
///
/// ⚠️ SEGREDO: o token capturado é a sessão do dono. Fica só nesta máquina, só em
/// memória; o diagnóstico grava tudo MASCARADO (XXXX). Nunca é logado em claro
/// nem enviado para lugar nenhum.
/// </summary>
public partial class ChatIfood : UserControl
{
    public event Action? Voltou;

    private const string UrlGestor = "https://gestordepedidos.ifood.com.br/";
    private bool _pronto;
    private bool _iniciando;
    private DispatcherTimer? _poll;

    // Groundwork do nativo: acumulador em memória do que o CDP capturou.
    private readonly ChatCaptura.Acumulador _captura = new();
    private DateTime _ultimoDiag = DateTime.MinValue;
    private string? _authRequestId;

    // guarda os receivers para não serem coletados
    private CoreWebView2DevToolsProtocolEventReceiver? _wsCreated, _wsRecv, _wsSent, _reqWill, _respRecv, _loadFin;

    public ChatIfood()
    {
        InitializeComponent();
        Loaded += async (_, _) => { if (!_pronto) await IniciarAsync(); };
    }

    /// <summary>
    /// Pré-aquece o WebView2 para o observador já rodar em segundo plano (o selo
    /// na venda acende antes de alguém abrir o chat). Se a plataforma não
    /// inicializar o WebView2 enquanto a camada está oculta, não faz mal: o
    /// observador liga assim que o operador abrir o chat pela primeira vez.
    /// </summary>
    public async Task PreAquecerAsync() { if (!_pronto) await IniciarAsync(); }

    private async Task IniciarAsync()
    {
        if (_iniciando || _pronto) return;   // pré-aquecer + 1ª abertura não podem inicializar duas vezes
        _iniciando = true;
        try
        {
            TxtEstado.Text = "carregando…";
            // perfil em ProgramData, NUNCA na pasta do exe: atualização de versão
            // troca o executável e o login tem que continuar de pé
            var perfil = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PdvNativo", "webview");
            Directory.CreateDirectory(perfil);

            var ambiente = await CoreWebView2Environment.CreateAsync(null, perfil);
            await Web.EnsureCoreWebView2Async(ambiente);
            var core = Web.CoreWebView2;

            // quiosque: sem DevTools nem menu de contexto pro operador se perder.
            // (A captura de rede NÃO depende desta flag — ela é o F12 visual.)
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            core.NavigationCompleted += (_, e) =>
                TxtEstado.Text = e.IsSuccess ? "painel do chat" : "sem conexão: toque em Recarregar";

            // mensagens do DOM (não lidas + modo do painel)
            core.WebMessageReceived += OnWebMessage;

            // injeta o script do painel ANTES de navegar (roda a cada carga)
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ScriptPainel);

            // liga a captura de rede (groundwork do nativo) — best-effort
            await LigarCapturaAsync(core);

            Web.Source = new Uri(UrlGestor);
            Web.Visibility = Visibility.Visible;
            PainelErro.Visibility = Visibility.Collapsed;
            _pronto = true;

            // rede de segurança: reconta a cada 7 s mesmo se o observador falhar
            _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            _poll.Tick += async (_, _) =>
            {
                try { await core.ExecuteScriptAsync("window.pdvContar && window.pdvContar()"); }
                catch { /* navegação em curso: o próximo tick tenta de novo */ }
            };
            _poll.Start();
        }
        catch (Exception ex)
        {
            Web.Visibility = Visibility.Collapsed;
            PainelErro.Visibility = Visibility.Visible;
            TxtEstado.Text = "";
            TxtErro.Text =
                "O componente de navegação do Windows (WebView2) não está disponível " +
                "nesta máquina. Instale o \"WebView2 Runtime\" da Microsoft e abra o " +
                "chat de novo. O restante do PDV segue funcionando normalmente.\n\n" +
                "Detalhe técnico: " + ex.Message;
        }
        finally { _iniciando = false; }
    }

    // ── mensagens vindas do DOM ──────────────────────────────────────────────
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
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
                var texto = doc.RootElement.TryGetProperty("texto", out var v) ? v.GetString() : null;
                ServicoChat.ReportarTexto(texto);
            }
            else if (tipo == "modo")
            {
                var modo = doc.RootElement.TryGetProperty("modo", out var v) ? v.GetString() : null;
                TxtEstado.Text = modo == "gestor" ? "Gestor (chat na barra lateral)" : "painel do chat";
            }
        }
        catch { /* mensagem malformada não derruba nada */ }
    }

    // ── captura de rede (CDP) — groundwork do parser nativo ──────────────────
    private async Task LigarCapturaAsync(CoreWebView2 core)
    {
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");

            _wsCreated = core.GetDevToolsProtocolEventReceiver("Network.webSocketCreated");
            _wsCreated.DevToolsProtocolEventReceived += (_, e) => Seguro(() =>
            {
                using var d = JsonDocument.Parse(e.ParameterObjectAsJson);
                if (d.RootElement.TryGetProperty("url", out var u))
                    _captura.RegistrarWebSocket(u.GetString());
                AgendarDiagnostico();
            });

            _wsRecv = core.GetDevToolsProtocolEventReceiver("Network.webSocketFrameReceived");
            _wsRecv.DevToolsProtocolEventReceived += (_, e) => Seguro(() =>
            {
                var p = PayloadDoFrame(e.ParameterObjectAsJson);
                if (p is not null) { _captura.RegistrarFrame(p, enviado: false); AgendarDiagnostico(); }
            });

            _wsSent = core.GetDevToolsProtocolEventReceiver("Network.webSocketFrameSent");
            _wsSent.DevToolsProtocolEventReceived += (_, e) => Seguro(() =>
            {
                var p = PayloadDoFrame(e.ParameterObjectAsJson);
                if (p is not null) { _captura.RegistrarFrame(p, enviado: true); AgendarDiagnostico(); }
            });

            _reqWill = core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
            _reqWill.DevToolsProtocolEventReceived += (_, e) => Seguro(() =>
            {
                using var d = JsonDocument.Parse(e.ParameterObjectAsJson);
                if (!d.RootElement.TryGetProperty("request", out var req)) return;
                var url = req.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (url is null) return;
                if (url.Contains("/chat/v1.0/auth"))
                    _authRequestId = d.RootElement.TryGetProperty("requestId", out var ri) ? ri.GetString() : null;
                // o JWT também viaja no header Authorization das chamadas de chat
                if (url.Contains("/chat/") && req.TryGetProperty("headers", out var h))
                {
                    var jwt = TokenDeHeaders(h);
                    if (jwt is not null) { _captura.RegistrarToken(jwt); AgendarDiagnostico(); }
                }
            });

            _respRecv = core.GetDevToolsProtocolEventReceiver("Network.responseReceived");
            _respRecv.DevToolsProtocolEventReceived += (_, e) => Seguro(() =>
            {
                using var d = JsonDocument.Parse(e.ParameterObjectAsJson);
                var id = d.RootElement.TryGetProperty("requestId", out var ri) ? ri.GetString() : null;
                if (id is not null && id == _authRequestId) _authRequestId = id; // marca para o loadingFinished
            });

            _loadFin = core.GetDevToolsProtocolEventReceiver("Network.loadingFinished");
            _loadFin.DevToolsProtocolEventReceived += async (_, e) =>
            {
                try
                {
                    using var d = JsonDocument.Parse(e.ParameterObjectAsJson);
                    var id = d.RootElement.TryGetProperty("requestId", out var ri) ? ri.GetString() : null;
                    if (id is null || id != _authRequestId) return;
                    var corpo = await core.CallDevToolsProtocolMethodAsync(
                        "Network.getResponseBody", $$"""{"requestId":"{{id}}"}""");
                    using var rc = JsonDocument.Parse(corpo);
                    if (!rc.RootElement.TryGetProperty("body", out var b)) return;
                    var body = b.GetString();
                    _captura.RegistrarAuthResposta(body);
                    // token da resposta {expiresAt, token}
                    if (body is not null)
                        try
                        {
                            using var bj = JsonDocument.Parse(body);
                            if (bj.RootElement.TryGetProperty("token", out var tk))
                                _captura.RegistrarToken(tk.GetString());
                        }
                        catch { }
                    AgendarDiagnostico();
                }
                catch { /* corpo indisponível: seguimos com o header */ }
            };
        }
        catch { /* sem captura: o painel e o contador continuam funcionando */ }
    }

    private static void Seguro(Action a) { try { a(); } catch { } }

    /// <summary>opcode 1 = texto; extrai response.payloadData do evento do CDP.</summary>
    private static string? PayloadDoFrame(string parametroJson)
    {
        try
        {
            using var d = JsonDocument.Parse(parametroJson);
            if (!d.RootElement.TryGetProperty("response", out var r)) return null;
            if (r.TryGetProperty("opcode", out var op) && op.TryGetInt32(out var o) && o != 1) return null;
            return r.TryGetProperty("payloadData", out var pd) ? pd.GetString() : null;
        }
        catch { return null; }
    }

    private static string? TokenDeHeaders(JsonElement headers)
    {
        if (headers.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in headers.EnumerateObject())
            if (p.Name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                && p.Value.ValueKind == JsonValueKind.String)
            {
                var v = p.Value.GetString() ?? "";
                const string bearer = "Bearer ";
                return v.StartsWith(bearer, StringComparison.OrdinalIgnoreCase) ? v[bearer.Length..].Trim() : v;
            }
        return null;
    }

    /// <summary>
    /// Escreve o diagnóstico MASCARADO em ProgramData, no máximo a cada 10 s.
    /// É o arquivo que o dono me manda para eu fechar o parser nativo.
    /// </summary>
    private void AgendarDiagnostico()
    {
        if ((DateTime.Now - _ultimoDiag).TotalSeconds < 10) return;
        _ultimoDiag = DateTime.Now;
        var texto = _captura.MontarDiagnostico();   // já vem mascarado
        _ = Task.Run(() =>
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PdvNativo");
                Directory.CreateDirectory(dir);
                // cinto E suspensório: quem GRAVA passa a varredura de novo (é
                // idempotente). Assim, nem um texto montado por outro caminho no
                // futuro chega ao disco com segredo em claro.
                File.WriteAllText(Path.Combine(dir, "chat-diagnostico.txt"), ChatCaptura.MascararTexto(texto));
            }
            catch { /* diagnóstico é conveniência, nunca derruba o caixa */ }
        });
    }

    // ── pulo do pedido para a conversa (exposto para venda/KDS no futuro) ─────
    /// <summary>
    /// Abre a conversa do cliente de um pedido no painel. Por deep-link não há
    /// (o Gestor não expõe), então procura na lista de conversas por JS. Se não
    /// achar com segurança, ao menos deixa o painel de conversas aberto e devolve
    /// false — a limitação está relatada, não escondida.
    /// </summary>
    public async Task<bool> AbrirConversaPorPedidoAsync(string numero)
    {
        if (!_pronto || !ChatContagem.NumeroPedidoValido(numero)) return false;
        try
        {
            var core = Web.CoreWebView2;
            await core.ExecuteScriptAsync("window.pdvAbrirConversas && window.pdvAbrirConversas()");
            var arg = JsonSerializer.Serialize(numero);   // dígitos, mas encode para não injetar
            var r = await core.ExecuteScriptAsync($"window.pdvBuscarConversa ? window.pdvBuscarConversa({arg}) : false");
            return r == "true";
        }
        catch { return false; }
    }

    private void Recarregar(object sender, RoutedEventArgs e)
    {
        ServicoChat.Recomecar();   // a próxima leitura vira linha de base
        if (_pronto) Web.Reload();
        else _ = IniciarAsync();
    }

    private void Voltar(object sender, RoutedEventArgs e) => Voltou?.Invoke();

    // ── o script injetado (roda dentro do WebView2, a cada carga) ────────────
    // Observa o DOM para: (1) contar não lidas e mandar o TEXTO cru para o C#;
    // (2) abrir a conversa; (3) isolar o painel do chat com SEGURANÇA (se não
    // achar o painel, não mexe em nada — nunca deixa tela branca).
    private const string ScriptPainel = """
    (function () {
      if (window.__pdvChat) return; window.__pdvChat = true;
      function envia(o){ try{ window.chrome.webview.postMessage(JSON.stringify(o)); }catch(e){} }

      // (1) NÃO LIDAS: acha o melhor candidato de texto e manda cru pro C#.
      window.pdvContar = function () {
        try {
          var sel = '[aria-label*="não lida"],[aria-label*="nao lida"],[aria-label*="não lidas"],[aria-label*="nao lidas"]';
          var el = document.querySelector(sel);
          var txt = '';
          if (el) txt = el.getAttribute('aria-label') || el.textContent || '';
          else {
            // fallback: badge numérico dentro do botão de Conversas/Atendimento
            var b = document.querySelector('[aria-label*="Conversas"],[aria-label*="Atendimento"]');
            if (b) {
              var num = b.querySelector('span,div');
              txt = (num && /\d/.test(num.textContent)) ? num.textContent : '';
            }
          }
          envia({ tipo: 'naolidas', texto: txt });
        } catch (e) { envia({ tipo: 'naolidas', texto: '' }); }
      };

      // (1.5) FECHAR O AVISO QUE BLOCA TUDO. O Gestor abre "Ativar som das
      // notificações" por cima da página; enquanto ele está aberto NADA mais é
      // alcançável, e era por isso que o painel nunca era achado (04/09).
      window.pdvFecharAvisos = function () {
        try {
          var bs = document.querySelectorAll('button');
          for (var i = 0; i < bs.length; i++) {
            var t = (bs[i].textContent || '').trim().toLowerCase();
            if (t === 'ok' || t === 'entendi' || t === 'permitir') {
              var r = bs[i].getBoundingClientRect();
              if (r.width > 0 && r.height > 0) { bs[i].click(); return true; }
            }
          }
        } catch (e) {}
        return false;
      };

      // (2) abrir o painel de conversas.
      // ⚠️ O botão do chat NÃO tem aria-label nem title no HTML do Gestor: ele é
      // um ícone na ponta direita da barra de cima (o leitor de tela mostra um
      // nome porque deduz do tooltip, mas o DOM não tem). Por isso a busca é por
      // POSIÇÃO, com o seletor por rótulo antes, de graça, caso um dia exista.
      window.pdvAbrirConversas = function () {
        try {
          var b = document.querySelector('[aria-label*="Conversas com clientes"],[aria-label*="Conversas"]');
          if (b) { b.click(); return true; }
          var alvo = null, melhorX = -1;
          var cands = document.querySelectorAll('button,[role="button"],div');
          for (var i = 0; i < cands.length; i++) {
            var r = cands[i].getBoundingClientRect();
            if (r.top < 60 && r.width >= 40 && r.height >= 40 &&
                r.left > window.innerWidth - 120 && r.left > melhorX) { melhorX = r.left; alvo = cands[i]; }
          }
          if (alvo) { (alvo.closest('button') || alvo).click(); return true; }
        } catch (e) {}
        return false;
      };

      // (3) procurar a conversa de um pedido pelo número e clicar.
      window.pdvBuscarConversa = function (numero) {
        try {
          var n = String(numero);
          var itens = document.querySelectorAll('[role="listitem"],li,a,[role="button"]');
          for (var i = 0; i < itens.length; i++) {
            var t = itens[i].textContent || '';
            if (t.indexOf(n) >= 0) { itens[i].click(); return true; }
          }
        } catch (e) {}
        return false;
      };

      // ISOLAR o painel: "holofote" no chat, escondendo os irmãos na subida até o
      // body. Só aplica se achar um painel grande de verdade — senão, não mexe
      // (cai no Gestor inteiro, sem tela branca).
      function estilo(){
        if (document.getElementById('pdv-css')) return;
        var s = document.createElement('style'); s.id = 'pdv-css';
        s.textContent = 'body.pdv-so-chat [data-pdv-hide]{display:none!important}';
        (document.head || document.documentElement).appendChild(s);
      }
      // ÂNCORA DO PAINEL: o título "Conversas" (um h1 dentro da gaveta), NÃO o
      // botão da barra. Subir a partir do BOTÃO leva à barra de cima, nunca ao
      // painel, que é uma gaveta em outro ramo do DOM: foi o defeito de 04/09,
      // que fazia o PDV cair no Gestor inteiro.
      function tituloDoPainel(){
        var h = document.querySelectorAll('h1,h2,h3,[role="heading"]');
        for (var i = 0; i < h.length; i++){
          var t = (h[i].textContent || '').trim();
          if (/^convers/i.test(t) && t.length < 30){
            var r = h[i].getBoundingClientRect();
            if (r.width > 0 && r.height > 0) return h[i];
          }
        }
        return null;
      }
      function candidato(){
        var el = tituloDoPainel();
        if (!el) return null;
        // sobe do título até o contêiner da gaveta (largo o bastante para ser o
        // painel, alto o bastante para não ser só o cabeçalho dele)
        var no = el;
        while (no && no !== document.body){
          var r = no.getBoundingClientRect();
          if (r.width >= 250 && r.width <= 900 && r.height > 400) return no;
          no = no.parentElement;
        }
        return null;
      }
      // O X DA GAVETA (04/09, pedido do dono). O holofote esconde os IRMÃOS da
      // gaveta, mas o X fica DENTRO dela, na linha do título, e continuava
      // clicável. Fechar por ele deixava o PDV sem chat e sem saída: o resto da
      // página seguia escondido e só o Recarregar trazia algo de volta, e trazia
      // o painel inicial do Gestor. Aqui o X some. O único jeito de sair do chat
      // passa a ser o "Voltar ao caixa", que é o que o dono queria.
      function esconderFecharDaGaveta(alvo, titulo){
        try {
          var rt = titulo.getBoundingClientRect();
          var bs = alvo.querySelectorAll('button,[role="button"]');
          for (var i = 0; i < bs.length; i++){
            var b = bs[i];
            if (b.contains(titulo)) continue;
            var r = b.getBoundingClientRect();
            if (r.width === 0 || r.height === 0) continue;
            var rotulo = ((b.getAttribute('aria-label') || '') + ' ' + (b.getAttribute('title') || '')).toLowerCase();
            var naLinhaDoTitulo = r.top < rt.bottom && r.bottom > rt.top;
            var aDireita = r.left >= rt.right;
            var pequeno = r.width <= 80 && r.height <= 80;
            if ((naLinhaDoTitulo && aDireita && pequeno) || /fechar|close/.test(rotulo))
              b.setAttribute('data-pdv-hide','');
          }
        } catch (e) {}
      }
      window.pdvIsolar = function () {
        try {
          var alvo = candidato();
          if (!alvo){ envia({tipo:'modo', modo:'gestor'}); return false; }
          estilo();
          document.querySelectorAll('[data-pdv-hide]').forEach(function(x){ x.removeAttribute('data-pdv-hide'); });
          var el = alvo;
          while (el && el !== document.body){
            var p = el.parentElement; if (!p) break;
            for (var i=0;i<p.children.length;i++){ if (p.children[i] !== el) p.children[i].setAttribute('data-pdv-hide',''); }
            el = p;
          }
          var titulo = tituloDoPainel();
          if (titulo) esconderFecharDaGaveta(alvo, titulo);
          document.body.classList.add('pdv-so-chat');
          envia({tipo:'modo', modo:'chat'});
          return true;
        } catch (e) { envia({tipo:'modo', modo:'gestor'}); return false; }
      };

      // observador: qualquer mexida no DOM reconta (com folga) e tenta abrir/isolar.
      var pend = null;
      function agenda(){ if (pend) return; pend = setTimeout(function(){ pend=null; window.pdvContar(); }, 400); }
      function liga(){
        try { new MutationObserver(agenda).observe(document.body, {childList:true, subtree:true, characterData:true}); } catch(e){}
        // ⚠️ O chat é um mini-aplicativo que carrega TARDE: em teste real ele não
        // existia no DOM depois de 33 s. A tentativa antiga parava em 8 s e por
        // isso desistia antes de o painel existir. Agora insiste por ~5 min e
        // para assim que consegue isolar.
        var pronto = false;
        function tentar(){
          if (pronto) return;
          try {
            window.pdvFecharAvisos();      // o aviso de som bloqueia tudo
            window.pdvAbrirConversas();
            if (window.pdvIsolar()) pronto = true;
            window.pdvContar();
          } catch (e) {}
        }
        [1000,2000,4000,7000,11000,16000,25000,40000,60000,90000].forEach(function(ms){ setTimeout(tentar, ms); });
        var tid = setInterval(function(){ if (pronto) { clearInterval(tid); return; } tentar(); }, 20000);
        setTimeout(function(){ clearInterval(tid); }, 300000);
        setInterval(window.pdvContar, 5000);
        // VIGIA DA GAVETA (04/09). `pronto` travava em true para sempre: se a
        // gaveta fechasse depois (Esc, clique fora, o X antes de ser escondido),
        // ninguém a reabria e o dono tinha que recarregar a página, que caía no
        // painel inicial do Gestor. Agora, isolada e sumida = volta a insistir.
        // Só age quando a gaveta NÃO está na tela, para nunca clicar no botão
        // com ela aberta (poderia fechá-la).
        setInterval(function(){
          if (!pronto) return;
          if (candidato()) return;
          pronto = false;
          tentar();
        }, 1500);
      }
      if (document.body) liga(); else document.addEventListener('DOMContentLoaded', liga);
    })();
    """;
}
