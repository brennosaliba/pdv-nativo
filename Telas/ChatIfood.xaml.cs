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
    /// <summary>Cada inicialização ganha um número; a que foi passada para trás (controle recriado no meio) não mexe mais na tela.</summary>
    private int _tentativa;
    private DispatcherTimer? _poll;
    private readonly HospedeWebView2 Hospede;
    private readonly string _perfil;
    private readonly TetoPorHora _tetoRecriar = new TetoPorHora(6);
    private static readonly System.Diagnostics.Stopwatch _relogio = System.Diagnostics.Stopwatch.StartNew();

    // Groundwork do nativo: acumulador em memória do que o CDP capturou.
    private readonly ChatCaptura.Acumulador _captura = new();
    private DateTime _ultimoDiag = DateTime.MinValue;
    private string? _authRequestId;

    // guarda os receivers para não serem coletados
    private CoreWebView2DevToolsProtocolEventReceiver? _wsCreated, _wsRecv, _wsSent, _reqWill, _respRecv, _loadFin;

    public ChatIfood()
    {
        InitializeComponent();
        // perfil em ProgramData, NUNCA na pasta do exe: atualização de versão
        // troca o executável e o login tem que continuar de pé
        _perfil = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PdvNativo", "webview");
        // 15/09/2026 (Castelo): o ambiente, o Ensure e a troca do controle que falhou moram no
        // hospedeiro, igual para o WhatsApp. Ver Telas/HospedeWebView2.cs.
        Hospede = new HospedeWebView2("chat", Dispatcher, () => Web, w => Web = w, _perfil, Diag);
        Hospede.Quebrou += AoQuebrar;
        Loaded += async (_, _) =>
        {
            try { if (!_pronto) await IniciarAsync(); }
            catch (Exception ex) { Diag("loaded: " + ex.GetType().Name + " " + ex.Message); }
        };
    }

    /// <summary>
    /// Uma linha datada em ProgramData\PdvNativo\chat-webview-diagnostico.txt: quando o WebView2 do
    /// chat nasceu, falhou (com a thread e a versão do runtime), caiu ou foi recriado. O
    /// chat-diagnostico.txt continua sendo só a captura de rede mascarada.
    /// </summary>
    internal static void Diag(string texto) => HospedeWebView2.Anotar("chat-webview-diagnostico.txt", texto, _relogio.Elapsed);

    /// <summary>
    /// Pré-aquece o WebView2 para o observador já rodar em segundo plano (o selo
    /// na venda acende antes de alguém abrir o chat). Se a plataforma não
    /// inicializar o WebView2 enquanto a camada está oculta, não faz mal: o
    /// observador liga assim que o operador abrir o chat pela primeira vez.
    /// Chamado de qualquer thread, volta para a da tela antes de encostar no controle.
    /// </summary>
    public Task PreAquecerAsync() => HospedeWebView2.NaTela(Dispatcher, () => _pronto ? Task.CompletedTask : IniciarAsync());

    private async Task IniciarAsync()
    {
        if (_iniciando || _pronto) return;   // pré-aquecer + 1ª abertura não podem inicializar duas vezes
        _iniciando = true;
        var minha = ++_tentativa;
        try
        {
            TxtEstado.Text = "carregando…";
            Diag("iniciar: " + HospedeWebView2.Contexto("chat"));
            Directory.CreateDirectory(_perfil);

            var core = await Hospede.IniciarControleAsync();
            if (minha != _tentativa) return;

            // quiosque: sem DevTools nem menu de contexto pro operador se perder.
            // (A captura de rede NÃO depende desta flag — ela é o F12 visual.)
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            core.NavigationCompleted += (_, e) =>
            {
                TxtEstado.Text = e.IsSuccess ? "painel do chat" : "sem conexão: toque em Recarregar";
                // Respostas prontas: a lista (lida do banco a cada carga) entra na página.
                if (e.IsSuccess) _ = DefinirRespostasAsync(core);
            };

            // mensagens do DOM (não lidas + modo do painel)
            core.WebMessageReceived += OnWebMessage;

            // PROCESSO QUE CAIU (15/09/2026). O chat não tratava: com o navegador morto o
            // Recarregar lançava na tela e o painel ficava branco até reiniciar o PDV. A regra é a
            // do WhatsApp (doc do WebView2 1.0.4129): renderer morto recarrega; navegador morto
            // deixa o CoreWebView2 inútil e pede um controle novo.
            core.ProcessFailed += (_, e) =>
            {
                Diag($"processo caiu: {e.ProcessFailedKind} {e.Reason}");
                switch (e.ProcessFailedKind)
                {
                    case CoreWebView2ProcessFailedKind.RenderProcessExited:
                        if (!_tetoRecriar.Permitir(DateTime.UtcNow)) { Diag("recarga: teto da hora"); break; }
                        try { Web.CoreWebView2?.Reload(); }
                        catch { _ = RecriarAsync("renderer caiu e o navegador não respondeu"); }
                        break;
                    case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                        _ = RecriarAsync("navegador caiu");
                        break;
                }
            };

            // injeta o script do painel ANTES de navegar (roda a cada carga)
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ScriptPainel);

            // liga a captura de rede (groundwork do nativo) — best-effort
            await LigarCapturaAsync(core);
            if (minha != _tentativa) return;

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
            // Uma tentativa passada para trás (o controle foi recriado no meio) não pinta a tela.
            if (minha != _tentativa) { Diag("tentativa antiga terminou: " + ex.GetType().Name); return; }
            // O hospedeiro anota (thread, runtime) e troca o controle que chegou a receber o Ensure:
            // controle com init falho não se reaproveita (15/09/2026, Castelo).
            var tipo = Hospede.Falhou("iniciar", ex);
            MostrarFalha(FalhaWebView2.Painel(tipo, "O chat"));
        }
        finally { if (minha == _tentativa) _iniciando = false; }
    }

    /// <summary>
    /// O painel de erro desta camada: UMA linha com o que fazer e o botão Tentar de novo. O
    /// detalhe fica no chat-webview-diagnostico.txt, nunca na tela.
    /// </summary>
    private void MostrarFalha(string texto)
    {
        try
        {
            _pronto = false;
            _poll?.Stop(); _poll = null;
            try { Web.Visibility = Visibility.Collapsed; } catch { }
            PainelErro.Visibility = Visibility.Visible;
            TxtEstado.Text = "";
            TxtErro.Text = texto;
        }
        catch (Exception ex) { Diag("painel de erro: " + ex.GetType().Name); }
    }

    /// <summary>
    /// Falha de WebView2 que chegou ao Dispatcher (layout, foco, visibilidade) e é DESTE controle
    /// (App.xaml.cs, HospedeWebView2.AvisarFalhaForaDaCamada). No lugar da caixa de aviso sobre o
    /// caixa: o painel desta camada, e um controle novo assim que o Dispatcher estiver livre.
    /// </summary>
    private void AoQuebrar(Exception ex)
    {
        _tentativa++; _iniciando = false;
        MostrarFalha(FalhaWebView2.Painel(TipoFalhaWeb.Outra, "O chat"));
        Hospede.RecriarDepois("quebrou fora da inicialização");
    }

    /// <summary>"Tentar de novo" do painel: controle que já foi usado dá lugar a um novo, e inicia.</summary>
    private async void TentarDeNovo(object sender, RoutedEventArgs e)
    {
        if (_iniciando) return;
        try
        {
            ServicoChat.Recomecar();
            Hospede.RecriarSeUsado("tentar de novo");
            PainelErro.Visibility = Visibility.Collapsed;
            await IniciarAsync();
        }
        catch (Exception ex) { Diag("tentar de novo: " + ex.GetType().Name + " " + ex.Message); }
    }

    /// <summary>
    /// O processo do navegador morreu (ou o Recarregar lançou): o controle é inútil. Controle novo
    /// no mesmo lugar, com ambiente novo, e inicia de novo. No máximo 6 por hora.
    /// </summary>
    private async Task RecriarAsync(string motivo)
    {
        if (!Dispatcher.CheckAccess()) { await HospedeWebView2.NaTela(Dispatcher, () => RecriarAsync(motivo)); return; }
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (!_tetoRecriar.Permitir(DateTime.UtcNow))
            {
                Diag("recriar: teto da hora");
                MostrarFalha(FalhaWebView2.Painel(TipoFalhaWeb.NavegadorCaiu, "O chat"));
                return;
            }
            Diag($"recriando o WebView2 ({motivo})");
            _poll?.Stop(); _poll = null;
            _pronto = false; _tentativa++; _iniciando = false;
            Hospede.Recriar(motivo, descartarAmbiente: true);
            ServicoChat.Recomecar();
            await IniciarAsync();
        }
        catch (Exception ex) { Diag("recriar: " + ex.GetType().Name + " " + ex.Message); }
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
            else if (tipo == "ajuda")
            {
                // a página conta como foi a busca do pedido; se desistiu, o Gestor inteiro
                // ficou na tela e o botão da barra tem que dizer isso
                var texto = doc.RootElement.TryGetProperty("texto", out var v) ? v.GetString() : null;
                if (!string.IsNullOrWhiteSpace(texto)) TxtEstado.Text = texto;
                var inteiro = doc.RootElement.TryGetProperty("gestorInteiro", out var g) && g.ValueKind == JsonValueKind.True;
                _gestorInteiro = inteiro;
                TxtGestorInteiro.Text = inteiro ? "Só o chat" : "Gestor inteiro";
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
        if (!Dispatcher.CheckAccess()) return await HospedeWebView2.NaTela(Dispatcher, () => AbrirConversaPorPedidoAsync(numero));
        if (!_pronto || !ChatContagem.NumeroPedidoValido(numero)) return false;
        try
        {
            var core = Web.CoreWebView2;
            if (core is null) return false;
            await core.ExecuteScriptAsync("window.pdvAbrirConversas && window.pdvAbrirConversas()");
            var arg = JsonSerializer.Serialize(numero);   // dígitos, mas encode para não injetar
            var r = await core.ExecuteScriptAsync($"window.pdvBuscarConversa ? window.pdvBuscarConversa({arg}) : false");
            return r == "true";
        }
        catch (Exception ex) { Diag("abrir conversa: " + ex.GetType().Name); return false; }
    }

    private void Recarregar(object sender, RoutedEventArgs e)
    {
        ServicoChat.Recomecar();   // a próxima leitura vira linha de base
        if (!_pronto) { _ = IniciarAsync(); return; }
        // com o navegador morto o controle lança: aí é recriar, não recarregar (15/09/2026)
        try { Web.CoreWebView2?.Reload(); }
        catch (Exception ex) { Diag("recarregar: " + ex.GetType().Name); _ = RecriarAsync("recarregar com o navegador morto"); }
    }

    private void Voltar(object sender, RoutedEventArgs e) => Voltou?.Invoke();

    // ── AJUDA DO iFOOD ("Fale com o iFood") ──────────────────────────────────────
    // 12/09/2026, pedido do dono: "quando um pedido tem problema (motoqueiro não
    // chegou), no Gestor tem a opção Fale com o iFood; clica e já abre chamado. Tem
    // como adicionar isso no KDS? E abre um chat na aba chat, no meio: ajuda iFood
    // entre respostas prontas e conversas."
    //
    // Mapeado pelo diagnóstico da loja (11/09 20:42): a gaveta do Atendimento mora no
    // cabeçalho do Gestor (irmã do botão com o ícone de fone, ifdl-icon-customer-service)
    // e por dentro é o help-center (data-testid help-center__page--chat, mensagens
    // help-center__message--text, árvore de decisão help-center__btn--decision-tree). O
    // "Fale com o iFood" é um link sem href dentro do detalhe do pedido; só JS abre.

    /// <summary>Botão da barra: abre o Atendimento do iFood na coluna do meio.</summary>
    private async void AbrirAjuda(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_pronto || Web.CoreWebView2 is null) { TxtEstado.Text = "o chat ainda não abriu"; return; }
            var r = await Web.CoreWebView2.ExecuteScriptAsync("window.pdvAbrirAjuda ? window.pdvAbrirAjuda() : false");
            TxtEstado.Text = r == "true" ? "abrindo o atendimento do iFood…" : "não achei o botão de atendimento do iFood nesta tela";
        }
        catch (Exception ex)
        {
            Diag("ajuda: " + ex.GetType().Name + " " + ex.Message);
            TxtEstado.Text = "a ajuda não abriu agora: toque em Recarregar";
        }
    }

    /// <summary>
    /// Do KDS: "Fale com o iFood" de um pedido. A página procura o pedido no Gestor
    /// (busca, card, link) e, achando, isola a gaveta do Atendimento ao lado do chat;
    /// não achando, deixa o Gestor inteiro na tela com a dica do caminho. Devolve
    /// false só se a página nem recebeu o pedido (chat sem WebView2).
    /// </summary>
    public async Task<bool> FaleComIfoodAsync(string numero)
    {
        if (!Dispatcher.CheckAccess()) return await HospedeWebView2.NaTela(Dispatcher, () => FaleComIfoodAsync(numero));
        if (!AjudaIfood.PodePedirAjuda("ifood", numero)) return false;
        try
        {
            if (!_pronto) await PreAquecerAsync();
            if (!_pronto || Web.CoreWebView2 is null) return false;
            TxtEstado.Text = AjudaIfood.Abrindo(numero);
            var arg = JsonSerializer.Serialize(AjudaIfood.SoDigitos(numero));
            var r = await Web.CoreWebView2.ExecuteScriptAsync($"window.pdvFaleComIfood ? window.pdvFaleComIfood({arg}) : false");
            return r == "true";
        }
        catch (Exception ex) { Diag("fale com o ifood: " + ex.GetType().Name); return false; }
    }

    private bool _gestorInteiro;

    /// <summary>
    /// Tira (ou devolve) o holofote: com o Gestor inteiro na tela dá para navegar até o
    /// pedido e chegar ao "Fale com o iFood". O observador de não lidas continua. Ao
    /// devolver, a vigia da gaveta (a cada 1,5 s) isola de novo sozinha.
    /// </summary>
    private async void AlternarGestorInteiro(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_pronto || Web.CoreWebView2 is null) return;
            _gestorInteiro = !_gestorInteiro;
            TxtGestorInteiro.Text = _gestorInteiro ? "Só o chat" : "Gestor inteiro";
            await Web.CoreWebView2.ExecuteScriptAsync(_gestorInteiro
                ? "window.__pdvSemHolofote = true; document.body.classList.remove('pdv-so-chat'); document.querySelectorAll('[data-pdv-hide]').forEach(function(x){ x.removeAttribute('data-pdv-hide'); });"
                : "window.__pdvSemHolofote = false; window.pdvIsolar && window.pdvIsolar();");
            TxtEstado.Text = _gestorInteiro ? "Gestor inteiro (toque em Só o chat para voltar)" : "painel do chat";
        }
        catch (Exception ex) { Diag("gestor inteiro: " + ex.GetType().Name); }
    }

    /// <summary>
    /// A ESTRUTURA DA TELA num arquivo (11/09/2026, para mapear o "Fale com o iFood" e o
    /// painel de ajuda sem pedir DevTools ao dono). Uma linha por elemento visível: tag,
    /// id, classes, papel, rótulo e um pedaço do texto, com telefone, CPF e e-mail
    /// mascarados. Vai para ProgramData\PdvNativo\gestor-diagnostico-HHmmss.txt.
    /// </summary>
    private async void Diagnostico(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_pronto || Web.CoreWebView2 is null) { TxtEstado.Text = "o chat ainda não abriu"; return; }
            var bruto = await Web.CoreWebView2.ExecuteScriptAsync(ScriptDiagnostico);
            var texto = JsonSerializer.Deserialize<string>(bruto) ?? "";
            var caminho = Path.Combine(Pdv.Nucleo.Banco.Pasta, $"gestor-diagnostico-{DateTime.Now:HHmmss}.txt");
            File.WriteAllText(caminho,
                $"url: {Web.CoreWebView2.Source}\ntitulo: {Web.CoreWebView2.DocumentTitle}\nquando: {DateTime.Now:dd/MM/yyyy HH:mm:ss}\n\n{texto}");
            TxtEstado.Text = "Diagnóstico gravado em " + caminho;
        }
        catch (Exception ex)
        {
            // texto cru de exceção não vai para a tela (revisão 15/09): o detalhe fica no diagnóstico
            Diag("diagnostico do gestor: " + ex.GetType().Name + " " + ex.Message);
            TxtEstado.Text = "O diagnóstico não gravou agora. Toque de novo.";
        }
    }

    private const string ScriptDiagnostico = """
        (function () {
          function mascara(s){ return (s || '').replace(/\s+/g, ' ').replace(/\d{4,}/g, '####').replace(/[\w.+-]+@[\w-]+\.[\w.]+/g, 'email@####').slice(0, 70); }
          function visivel(el){ try { var r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; } catch (e) { return false; } }
          var linhas = [], n = 0;
          function anda(el, prof){
            if (n > 3000 || prof > 18) return;
            if (!(el instanceof Element)) return;
            if (el.id && /^pdv-/.test(el.id)) return;
            var tag = el.tagName.toLowerCase();
            if (tag === 'script' || tag === 'style' || tag === 'svg' || tag === 'path') return;
            var vis = visivel(el);
            var oculto = el.hasAttribute('data-pdv-hide') ? ' [holofote]' : '';
            if (!vis && !oculto) return;
            var proprio = '';
            for (var i = 0; i < el.childNodes.length; i++) { var c = el.childNodes[i]; if (c.nodeType === 3) proprio += c.textContent; }
            proprio = mascara(proprio.trim());
            var cls = (typeof el.className === 'string' ? el.className : '').split(/\s+/).filter(Boolean).slice(0, 3).join('.');
            var attrs = [];
            ['role','aria-label','data-testid','href','type','placeholder','name'].forEach(function (a) { var v = el.getAttribute(a); if (v) attrs.push(a + '=' + mascara(v).slice(0, 50)); });
            var r = el.getBoundingClientRect();
            linhas.push(new Array(prof + 1).join('  ') + tag + (el.id ? '#' + el.id : '') + (cls ? '.' + cls : '')
              + (attrs.length ? ' [' + attrs.join(' ') + ']' : '') + oculto
              + ' @' + Math.round(r.left) + ',' + Math.round(r.top) + ' ' + Math.round(r.width) + 'x' + Math.round(r.height)
              + (proprio ? ' "' + proprio + '"' : ''));
            n++;
            for (var j = 0; j < el.children.length; j++) anda(el.children[j], prof + 1);
          }
          anda(document.body, 0);
          return linhas.join('\n');
        })();
        """;

    /// <summary>
    /// RESPOSTAS PRONTAS (11/09/2026, pedido do dono): entrega à página a lista de
    /// cartões do espaço vazio ao lado do chat. Lida do banco a cada carga, então editar
    /// na Configuração e tocar em Recarregar basta. Sem respostas não é sem chat.
    /// </summary>
    private static async Task DefinirRespostasAsync(CoreWebView2 core)
    {
        try
        {
            string json;
            using (var cx = Pdv.Nucleo.Banco.Abrir())
                json = Pdv.Nucleo.RespostasProntas.Json(
                    Pdv.Nucleo.RespostasProntas.Ler(Pdv.Nucleo.Vendas.Config(cx, Pdv.Nucleo.RespostasProntas.Chave)));
            await core.ExecuteScriptAsync("window.pdvDefinirRespostas && window.pdvDefinirRespostas(" + json + ")");
        }
        catch { /* sem respostas prontas o chat continua inteiro */ }
    }

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
          // 1) pelo ICONE do chat (classe estavel do design system do iFood, achada pelo
          //    dono no inspetor: "ifdl-icon-chat"). Independe de posicao e de resolucao.
          var ic = document.querySelector('.ifdl-icon-chat,[class*="ifdl-icon-chat"],[class*="icon-chat"]');
          if (ic) { (ic.closest('a,button,[role="button"]') || ic).click(); return true; }
          // 2) pelo rotulo, se um dia existir
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
        s.textContent = 'body.pdv-so-chat [data-pdv-hide]{display:none!important;pointer-events:none!important}' +
          // a cadeia do cabecalho ate a gaveta da ajuda fica invisivel (sem faixa branca em cima);
          // a gaveta em si volta a aparecer por cima, na coluna do meio
          'body.pdv-so-chat [data-pdv-veu]{visibility:hidden!important;background:transparent!important;box-shadow:none!important;border-color:transparent!important}' +
          'body.pdv-so-chat [data-pdv-ajuda]{visibility:visible!important;position:fixed!important;top:18px!important;bottom:18px!important;height:auto!important;max-height:none!important;max-width:none!important;margin:0!important;transform:none!important;z-index:2147482000!important;border-radius:16px;overflow:auto;box-shadow:0 12px 40px rgba(0,0,0,.18)}' +
          '#pdv-ajuda{position:fixed;z-index:2147483000;display:none;font:14px system-ui,Segoe UI,sans-serif}' +
          'body.pdv-so-chat #pdv-ajuda.pdv-visivel{display:block}' +
          '#pdv-ajuda.pdv-coluna{top:18px;background:#f7f4ee;border:1px solid #e6e1d8;border-radius:16px;padding:14px}' +
          '#pdv-ajuda h4{margin:0 0 10px 2px;font-size:12px;letter-spacing:.08em;color:#8a8580;font-weight:700}' +
          '#pdv-ajuda p{margin:10px 2px 0;color:#6b655e;font-size:13px;line-height:1.4}' +
          '#pdv-ajuda button{display:block;width:100%;border:0;border-radius:24px;padding:12px 16px;background:#F276A5;color:#fff;font:700 14px system-ui,Segoe UI,sans-serif;cursor:pointer;box-shadow:0 4px 14px rgba(0,0,0,.18)}' +
          '#pdv-ajuda.pdv-pill{background:transparent;padding:0}' +
          '#pdv-ajuda.pdv-pill h4,#pdv-ajuda.pdv-pill p{display:none}' +
          '#pdv-conversas-pill{position:fixed;right:18px;top:18px;z-index:2147483000;display:none;border:0;border-radius:24px;padding:12px 18px;background:#2b2724;color:#fff;font:700 14px system-ui,Segoe UI,sans-serif;cursor:pointer}' +
          'body.pdv-so-chat #pdv-conversas-pill.pdv-visivel{display:block}' +
          '#pdv-cortina{position:fixed;inset:0;z-index:2147483647;background:#f7f4ee;display:flex;align-items:center;' +
          'justify-content:center;font:16px system-ui,Segoe UI,sans-serif;color:#555}';
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
      // AJUDA DO iFOOD (12/09/2026). A gaveta do Atendimento ("Fale com o iFood") e o
      // help-center: mora no CABECALHO do Gestor, irma do botao com o icone de fone.
      // Mapeado pelo diagnostico da loja de 11/09 20:42 (gestor-diagnostico-204245).
      function ajudaMiolo(){ return document.querySelector('[data-testid^="help-center__"]'); }
      function pareceGaveta(el){
        var r = el.getBoundingClientRect();
        return r.width >= 250 && r.height > 300 && r.width * r.height < window.innerWidth * window.innerHeight * 0.9;
      }
      // sobe do miolo ate o no mais alto que ainda tem cara de gaveta (o pai dele e o
      // grupo de botoes do cabecalho, 208x40, ou a pagina inteira)
      function ajudaCandidato(){
        var m = ajudaMiolo(); if (!m) return null;
        var no = m, ultimo = null;
        while (no && no !== document.body){
          if (pareceGaveta(no)) ultimo = no;
          else if (ultimo) break;
          no = no.parentElement;
        }
        return ultimo;
      }
      function botaoAtendimento(){
        var ic = document.querySelector('.ifdl-icon-customer-service,[class*="icon-customer-service"]');
        if (ic) return ic.closest('button,a,[role="button"]') || ic;
        return document.querySelector('[aria-label*="Atendimento"]');
      }
      window.pdvAbrirAjuda = function () {
        try {
          if (ajudaCandidato()) return true;
          var b = botaoAtendimento(); if (!b) return false;
          b.click(); return true;
        } catch (e) { return false; }
      };

      // O X DA GAVETA (04/09, pedido do dono). O holofote esconde os IRMÃOS da
      // gaveta, mas o X fica DENTRO dela, na linha do título, e continuava
      // clicável. Fechar por ele deixava o PDV sem chat e sem saída: o resto da
      // página seguia escondido e só o Recarregar trazia algo de volta, e trazia
      // o painel inicial do Gestor. Aqui o X some. O único jeito de sair do chat
      // passa a ser o "Voltar ao caixa", que é o que o dono queria.
      // E um X que fecha? Tres redes, qualquer uma basta (a 0.5.4 tinha so a
      // geometria "a direita do titulo", e o titulo desta gaveta ocupa a linha
      // inteira, entao o X ficava "dentro" da largura dele e escapava):
      //   a) classe do icone do design system do iFood (ifdl-icon-close e parentes)
      //      ou rotulo "fechar"/"close";
      //   b) o glifo em si: texto que e so um "x" (×, ✕, ✖, X);
      //   c) qualquer clicavel na LINHA do cabecalho que nao contenha o titulo.
      function ehFechar(el, titulo, rt){
        try {
          if (el.contains(titulo)) return false;
          var cls = (typeof el.className === 'string' ? el.className : (el.getAttribute('class') || '')).toLowerCase();
          var rot = ((el.getAttribute('aria-label') || '') + ' ' + (el.getAttribute('title') || '')).toLowerCase();
          if (/icon-close|icon-x\b|close-icon|\bclose\b|fechar/.test(cls) || /fechar|close/.test(rot)) return true;
          var txt = (el.textContent || '').trim();
          if (/^[×✕✖xX]$/.test(txt)) return true;
          if (el.querySelector && el.querySelector('[class*="icon-close"],[class*="close-icon"],[class*="ifdl-icon-close"]')) return true;
          var r = el.getBoundingClientRect();
          if (r.width === 0 || r.height === 0) return false;
          var naLinhaDoTitulo = r.top < rt.bottom && r.bottom > rt.top;
          var pequeno = r.width <= 80 && r.height <= 80;
          return naLinhaDoTitulo && pequeno && !/input|textarea|select/i.test(el.tagName);
        } catch (e) { return false; }
      }
      function esconderFecharDaGaveta(alvo, titulo){
        try {
          var rt = titulo.getBoundingClientRect();
          var cands = alvo.querySelectorAll('button,[role="button"],a,svg,[class*="icon-close"],[class*="close"]');
          for (var i = 0; i < cands.length; i++){
            var el = cands[i];
            if (!ehFechar(el, titulo, rt)) continue;
            var alvoClique = el.closest('button,[role="button"],a') || el;
            if (alvoClique.contains(titulo)) continue;
            alvoClique.setAttribute('data-pdv-hide','');
            alvoClique.setAttribute('data-pdv-fechar','');
          }
        } catch (e) {}
      }
      // Cinto: mesmo que o X reapareca (React re-renderiza), clique nele morre na
      // fase de captura, antes de o Gestor ouvir. So enquanto o chat esta isolado.
      document.addEventListener('click', function (ev) {
        try {
          if (!document.body.classList.contains('pdv-so-chat')) return;
          var t = ev.target && ev.target.closest ? ev.target.closest('[data-pdv-fechar]') : null;
          if (!t) {
            var titulo = tituloDoPainel();
            if (!titulo || !ev.target.closest) return;
            var el = ev.target.closest('button,[role="button"],a,svg');
            if (!el || !ehFechar(el, titulo, titulo.getBoundingClientRect())) return;
          }
          ev.stopImmediatePropagation(); ev.preventDefault();
        } catch (e) {}
      }, true);
      // RESPOSTAS PRONTAS (11/09/2026, pedido do dono): cartoes no espaco vazio a
      // ESQUERDA do chat (e espaco da propria pagina: o Gestor escondido). Toque =
      // copia o texto e tenta colar direto na caixa de mensagem da conversa aberta
      // (o menu de contexto esta desligado no quiosque, entao "colar" tem que ser
      // nosso). A lista vem do C# (pdvDefinirRespostas), lida do banco a cada carga.
      window.__pdvRespostas = window.__pdvRespostas || [];
      function respostasCss(){
        if (document.getElementById('pdv-css-resp')) return;
        var s = document.createElement('style'); s.id = 'pdv-css-resp';
        s.textContent = '#pdv-respostas{position:fixed;left:18px;top:18px;bottom:18px;width:340px;max-width:calc(100vw - 560px);overflow:auto;z-index:2147483000;font:14px system-ui,Segoe UI,sans-serif;display:none}' +
          'body.pdv-so-chat #pdv-respostas.pdv-inline{display:block}' +
          // COMPACTO: a conversa aberta ocupa a esquerda; o painel vira uma pilula e so aparece
          // por cima (com fundo e X) quando o atendente pede. Nunca fica em cima da conversa
          // sem ser chamado (11/09/2026, dono: "ele coloca o frame em cima").
          'body.pdv-so-chat #pdv-respostas.pdv-compacto.pdv-aberto{display:block;top:18px;bottom:auto;max-height:calc(100vh - 36px);width:340px;max-width:calc(100vw - 36px);background:#f7f4ee;border:1px solid #e6e1d8;border-radius:16px;padding:12px;box-shadow:0 12px 40px rgba(0,0,0,.18)}' +
          '#pdv-respostas .pdv-fechar{display:none;position:absolute;right:10px;top:8px;width:34px;height:34px;border:0;border-radius:17px;background:#e6e1d8;color:#2b2724;font-size:18px;cursor:pointer}' +
          '#pdv-respostas.pdv-aberto .pdv-fechar{display:block}' +
          '#pdv-respostas-pill{position:fixed;left:18px;bottom:18px;z-index:2147483000;display:none;background:#F276A5;color:#fff;border:0;border-radius:24px;padding:12px 18px;font:700 14px system-ui,Segoe UI,sans-serif;cursor:pointer;box-shadow:0 4px 14px rgba(0,0,0,.18)}' +
          'body.pdv-so-chat #pdv-respostas-pill.pdv-visivel{display:block}' +
          '#pdv-respostas h4{margin:0 0 10px 2px;font-size:12px;letter-spacing:.08em;color:#8a8580;font-weight:700}' +
          '.pdv-resp{background:#fff;border:1px solid #e6e1d8;border-radius:14px;padding:12px 14px;margin:0 0 10px;cursor:pointer;box-shadow:0 1px 2px rgba(0,0,0,.04)}' +
          '.pdv-resp:active{transform:scale(.99)}' +
          '.pdv-resp b{display:block;color:#2b2724;font-size:15px;margin-bottom:4px}' +
          '.pdv-resp span{display:-webkit-box;-webkit-line-clamp:3;-webkit-box-orient:vertical;overflow:hidden;color:#6b655e;font-size:13px;line-height:1.35}' +
          '.pdv-resp.ok{border-color:#F276A5;background:#fff4f8}' +
          '.pdv-resp .pdv-ok{display:none;color:#c9407a;font-weight:700;font-size:12px;margin-top:6px}' +
          '.pdv-resp.ok .pdv-ok{display:block}';
        (document.head || document.documentElement).appendChild(s);
      }
      function respVisivel(el){ if (!el) return false; var r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; }
      function respComposer(){
        var cs = document.querySelectorAll('textarea, [contenteditable="true"]');
        for (var i = 0; i < cs.length; i++) { if (respVisivel(cs[i]) && !cs[i].closest('#pdv-respostas')) return cs[i]; }
        return null;
      }
      function respCopiar(texto){
        // o caminho SINCRONO primeiro (funciona dentro do gesto, mesmo sem foco no documento);
        // a Promise do clipboard so como plano B, com o erro engolido
        try {
          var ta = document.createElement('textarea'); ta.value = texto; ta.style.position = 'fixed'; ta.style.left = '-9999px';
          document.body.appendChild(ta); ta.select(); var ok = document.execCommand('copy'); ta.remove();
          if (ok) return true;
        } catch (e) {}
        try { if (navigator.clipboard && navigator.clipboard.writeText) { navigator.clipboard.writeText(texto).catch(function(){}); return true; } } catch (e) {}
        return false;
      }
      // O ESPACO A ESQUERDA ESTA LIVRE? Mede o que esta EMBAIXO do painel (elementFromPoint com
      // o painel escondido): so os ancestrais da gaveta sao "fundo"; qualquer outra coisa
      // visivel ali (a conversa aberta, um modal) e ocupacao. Sem isso o painel ficava em
      // cima da conversa assim que ela abria.
      function areaLivre(alvo){
        var box = document.getElementById('pdv-respostas'); var pill = document.getElementById('pdv-respostas-pill');
        if (!box) return true;
        var w = Math.min(340, window.innerWidth - 36), h = window.innerHeight - 36;
        var pts = [[38, 40], [18 + w - 20, 40], [38, 18 + h / 2], [18 + w - 20, 18 + h / 2], [38, h - 30], [18 + w - 20, h - 30]];
        var vb = box.style.visibility, vp = pill ? pill.style.visibility : '';
        box.style.visibility = 'hidden'; if (pill) pill.style.visibility = 'hidden';
        try {
          for (var i = 0; i < pts.length; i++) {
            var el = document.elementFromPoint(pts[i][0], pts[i][1]);
            if (!el || el === document.body || el === document.documentElement) continue;
            if (alvo && (el === alvo || el.contains(alvo))) continue;   // fundo: a cadeia da gaveta
            return false;                                              // algo visivel ali: ocupado
          }
          return true;
        } catch (e) { return true; }
        finally { box.style.visibility = vb; if (pill) pill.style.visibility = vp; }
      }
      window.pdvAjustarRespostas = function () {
        try {
          var box = document.getElementById('pdv-respostas'); var pill = document.getElementById('pdv-respostas-pill');
          var alvo = candidato();
          if (!box || !pill) return;
          if (!document.body.classList.contains('pdv-so-chat') || !window.__pdvRespostas.length) { box.classList.remove('pdv-inline'); pill.classList.remove('pdv-visivel'); return; }
          var l = alvo ? alvo.getBoundingClientRect().left - 36 : window.innerWidth - 36;
          var la = ajudaCandidato() ? layoutAjuda() : null;
          var cabe = la ? la.inline : (l >= 160 && areaLivre(alvo));
          if (cabe) {
            box.classList.add('pdv-inline'); box.classList.remove('pdv-compacto', 'pdv-aberto');
            box.style.width = (la ? la.respW : Math.max(0, Math.min(340, l))) + 'px';
            pill.classList.remove('pdv-visivel');
          } else {
            box.classList.remove('pdv-inline'); box.classList.add('pdv-compacto');
            box.style.width = '';
            pill.classList.add('pdv-visivel');
          }
        } catch (e) {}
      };
      function ajustarLarguraRespostas(){ window.pdvAjustarRespostas(); }
      function respColar(texto){
        try {
          var c = respComposer(); if (!c) return false;
          c.focus();
          if (c.tagName === 'TEXTAREA' || c.tagName === 'INPUT') {
            var proto = c.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
            var set = Object.getOwnPropertyDescriptor(proto, 'value').set;
            set.call(c, texto);
            c.dispatchEvent(new Event('input', { bubbles: true }));
            return true;
          }
          document.execCommand('selectAll', false, null);
          return document.execCommand('insertText', false, texto);
        } catch (e) { return false; }
      }
      window.pdvDefinirRespostas = function (lista) {
        try {
          window.__pdvRespostas = Array.isArray(lista) ? lista : [];
          respostasCss();
          var box = document.getElementById('pdv-respostas');
          var pill = document.getElementById('pdv-respostas-pill');
          if (!box) {
            box = document.createElement('div'); box.id = 'pdv-respostas'; document.body.appendChild(box);
            // o toque no cartao nao pode virar "clique fora" da gaveta (ela fecharia)
            ['pointerdown','mousedown','touchstart','click'].forEach(function (tp) { box.addEventListener(tp, function (ev) { ev.stopPropagation(); }); });
          }
          if (!pill) {
            pill = document.createElement('button'); pill.id = 'pdv-respostas-pill'; pill.type = 'button'; pill.textContent = 'Respostas prontas';
            ['pointerdown','mousedown','touchstart','click'].forEach(function (tp) { pill.addEventListener(tp, function (ev) { ev.stopPropagation(); }); });
            pill.addEventListener('click', function () { var b = document.getElementById('pdv-respostas'); if (b) b.classList.toggle('pdv-aberto'); });
            document.body.appendChild(pill);
            window.addEventListener('resize', function () { window.pdvAjustarRespostas(); });
          }
          box.innerHTML = '';
          if (!window.__pdvRespostas.length) { ajustarLarguraRespostas(); return; }
          var x = document.createElement('button'); x.type = 'button'; x.className = 'pdv-fechar'; x.textContent = '\u00d7';
          x.addEventListener('click', function () { box.classList.remove('pdv-aberto'); });
          box.appendChild(x);
          var h = document.createElement('h4'); h.textContent = 'RESPOSTAS PRONTAS'; box.appendChild(h);
          window.__pdvRespostas.forEach(function (r) {
            var d = document.createElement('div'); d.className = 'pdv-resp';
            var b = document.createElement('b'); b.textContent = r.titulo || ''; d.appendChild(b);
            var s = document.createElement('span'); s.textContent = r.texto || ''; d.appendChild(s);
            var ok = document.createElement('div'); ok.className = 'pdv-ok'; d.appendChild(ok);
            d.addEventListener('click', function () {
              var copiou = respCopiar(r.texto || ''); var colou = respColar(r.texto || '');
              ok.textContent = colou ? 'Colado na conversa. É só enviar.' : (copiou ? 'Copiado. Abra a conversa, toque na caixa de mensagem e aperte Ctrl+V.' : 'Não consegui copiar.');
              d.classList.add('ok'); setTimeout(function(){ d.classList.remove('ok'); }, 3000);
              // na sobreposicao, o toque fecha o painel: a conversa volta a ficar inteira na tela
              if (box.classList.contains('pdv-compacto')) setTimeout(function(){ box.classList.remove('pdv-aberto'); }, 900);
              envia({ tipo: 'resposta', titulo: r.titulo || '', colou: colou, copiou: copiou });
            });
            box.appendChild(d);
          });
          ajustarLarguraRespostas();
        } catch (e) {}
      };

      // A COLUNA DO MEIO (12/09/2026): a gaveta da ajuda, quando aberta, fica entre as
      // respostas prontas e o chat. Em tela estreita (1024) as respostas viram a pilula e
      // a ajuda ocupa a esquerda inteira; em tela larga cabem as tres colunas. Sem gaveta
      // aberta, um cartao "Ajuda iFood" (ou so o botao, se nao ha espaco) segura o lugar.
      function medidaLivre(){
        var chat = candidato();
        return chat ? chat.getBoundingClientRect().left : window.innerWidth;
      }
      function layoutAjuda(){
        var direita = medidaLivre();                       // onde comeca o chat (ou a borda)
        var livre = direita - 36;                          // margens de 18 dos dois lados
        var inline = livre >= 320 + 260 + 18;              // ajuda minima 320 + respostas 260
        var respW = inline ? Math.max(0, Math.min(340, livre - 320 - 18)) : 0;
        var ajudaL = inline ? 18 + respW + 18 : 18;
        var ajudaW = Math.max(0, Math.min(640, direita - ajudaL - 18));
        return { inline: inline, respW: respW, ajudaL: ajudaL, ajudaW: ajudaW, direita: direita };
      }
      function posicionarAjuda(){
        var a = ajudaCandidato(); if (!a) return;
        var l = layoutAjuda();
        a.setAttribute('data-pdv-ajuda', '');
        a.style.setProperty('left', l.ajudaL + 'px', 'important');
        a.style.setProperty('width', l.ajudaW + 'px', 'important');
      }
      function cartaoAjuda(){
        var c = document.getElementById('pdv-ajuda');
        if (c) return c;
        c = document.createElement('div'); c.id = 'pdv-ajuda';
        var h = document.createElement('h4'); h.textContent = 'AJUDA iFOOD'; c.appendChild(h);
        var b = document.createElement('button'); b.type = 'button'; b.textContent = '🛟 Falar com o iFood';
        b.addEventListener('click', function (ev) { ev.stopPropagation(); if (!window.pdvAbrirAjuda()) envia({tipo:'ajuda', texto:'não achei o botão de atendimento do iFood nesta tela'}); });
        c.appendChild(b);
        var p = document.createElement('p'); p.textContent = 'Abre o atendimento do iFood aqui do lado. Para um pedido específico: Delivery, toque no pedido e em Fale com o iFood.'; c.appendChild(p);
        ['pointerdown','mousedown','touchstart','click'].forEach(function (tp) { c.addEventListener(tp, function (ev) { ev.stopPropagation(); }); });
        document.body.appendChild(c);
        var cp = document.createElement('button'); cp.id = 'pdv-conversas-pill'; cp.type = 'button'; cp.textContent = 'Abrir conversas';
        ['pointerdown','mousedown','touchstart'].forEach(function (tp) { cp.addEventListener(tp, function (ev) { ev.stopPropagation(); }); });
        cp.addEventListener('click', function (ev) { ev.stopPropagation(); window.pdvAbrirConversas(); });
        document.body.appendChild(cp);
        return c;
      }
      function ajustarAjuda(){
        try {
          var c = cartaoAjuda(); var cp = document.getElementById('pdv-conversas-pill');
          var isolado = document.body.classList.contains('pdv-so-chat');
          var gaveta = ajudaCandidato();
          if (!isolado || gaveta) { c.classList.remove('pdv-visivel'); }
          else {
            var l = layoutAjuda();
            // com respostas na tela: o cartao entra a direita delas se sobra >= 200; senao vira botao no rodape
            var box = document.getElementById('pdv-respostas');
            var respInline = box && box.classList.contains('pdv-inline');
            var esq = respInline ? 18 + box.getBoundingClientRect().width + 18 : 18;
            var sobra = l.direita - esq - 18;
            c.classList.add('pdv-visivel');
            if (sobra >= 200) { c.classList.add('pdv-coluna'); c.classList.remove('pdv-pill'); c.style.left = esq + 'px'; c.style.top = '18px'; c.style.bottom = ''; c.style.width = Math.min(300, sobra) + 'px'; }
            else { c.classList.add('pdv-pill'); c.classList.remove('pdv-coluna'); c.style.left = '18px'; c.style.top = ''; c.style.bottom = respInline ? '' : '70px'; c.style.width = '190px'; if (respInline) { c.style.top = '18px'; c.style.left = (18 + box.getBoundingClientRect().width + 12) + 'px'; c.style.width = Math.max(120, Math.min(190, sobra - 12)) + 'px'; } }
          }
          if (cp) { if (isolado && gaveta && !candidato()) cp.classList.add('pdv-visivel'); else cp.classList.remove('pdv-visivel'); }
          if (gaveta) posicionarAjuda();
        } catch (e) {}
      }
      window.pdvAjustarAjuda = ajustarAjuda;

      window.pdvIsolar = function () {
        try {
          var chat = candidato(), ajuda = ajudaCandidato();
          var alvos = [chat, ajuda].filter(Boolean);
          if (!alvos.length){ envia({tipo:'modo', modo:'gestor'}); return false; }
          estilo();
          document.querySelectorAll('[data-pdv-hide]').forEach(function(x){ x.removeAttribute('data-pdv-hide'); });
          document.querySelectorAll('[data-pdv-veu]').forEach(function(x){ x.removeAttribute('data-pdv-veu'); });
          // guarda = os alvos e todos os seus ancestrais: nunca escondidos
          var guarda = [];
          alvos.forEach(function (a) { var e = a; while (e && e !== document.body) { guarda.push(e); e = e.parentElement; } });
          function guardado(x){ return guarda.indexOf(x) >= 0; }
          alvos.forEach(function (alvo) {
            var el = alvo;
            while (el && el !== document.body){
              var p = el.parentElement; if (!p) break;
              for (var i=0;i<p.children.length;i++){ var c = p.children[i]; if (c !== el && !guardado(c) && !/^pdv-/.test(c.id || '')) c.setAttribute('data-pdv-hide',''); }
              el = p;
            }
          });
          // a cadeia da AJUDA que nao e cadeia do chat fica invisivel (o cabecalho do Gestor
          // e a pagina por tras dele); a gaveta em si e reposicionada por cima
          if (ajuda) {
            var cadeiaChat = []; if (chat) { var e2 = chat; while (e2 && e2 !== document.body) { cadeiaChat.push(e2); e2 = e2.parentElement; } }
            var e3 = ajuda.parentElement;
            while (e3 && e3 !== document.body) { if (cadeiaChat.indexOf(e3) < 0) e3.setAttribute('data-pdv-veu', ''); e3 = e3.parentElement; }
          }
          var titulo = tituloDoPainel();
          if (chat && titulo) esconderFecharDaGaveta(chat, titulo);
          document.body.classList.add('pdv-so-chat');
          window.pdvDefinirRespostas(window.__pdvRespostas);
          ajustarLarguraRespostas();
          ajustarAjuda();
          envia({tipo:'modo', modo:'chat'});
          return true;
        } catch (e) { envia({tipo:'modo', modo:'gestor'}); return false; }
      };

      // FALE COM O iFOOD DE UM PEDIDO (do KDS). Sem deep-link no Gestor, a pagina faz o
      // caminho de uma pessoa: Gestor inteiro, busca pelo numero, toque no pedido, toque
      // no link "Fale com o iFood", e ai isola a gaveta da ajuda ao lado do chat. Tudo
      // por baixo da cortina. Se nao achar em ~25 s, deixa o Gestor inteiro na tela com
      // o caminho dito na barra: o dono chega la em dois toques.
      function setValor(inp, v){
        try {
          var proto = inp.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
          var set = Object.getOwnPropertyDescriptor(proto, 'value').set;
          inp.focus(); set.call(inp, v); inp.dispatchEvent(new Event('input', { bubbles: true })); inp.dispatchEvent(new Event('change', { bubbles: true }));
        } catch (e) {}
      }
      function visivelEl(el){ try { var r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; } catch (e) { return false; } }
      function acharCardDoPedido(n){
        // o menor elemento visivel cujo texto tem o numero (com ou sem #), fora dos nossos
        var re = new RegExp('(^|[^0-9])#?' + n + '([^0-9]|$)');
        var cands = document.querySelectorAll('[role="listitem"] *, article *, [role="list"] *, [class*="card"] *, [class*="Card"] *');
        var melhor = null, area = Infinity;
        for (var i = 0; i < cands.length; i++) {
          var el = cands[i]; if (el.closest('#pdv-respostas,#pdv-ajuda,#pdv-cortina')) continue;
          if (!visivelEl(el)) continue;
          var t = (el.textContent || '').replace(/\s+/g, ' ');
          if (t.length > 400 || !re.test(t)) continue;
          var r = el.getBoundingClientRect(); var a = r.width * r.height;
          if (a < area) { area = a; melhor = el; }
        }
        if (!melhor) return null;
        return melhor.closest('[role="listitem"],article,a,button,[role="button"],[class*="card"],[class*="Card"]') || melhor;
      }
      function acharLinkAjuda(){
        var els = document.querySelectorAll('a,button,[role="button"],span');
        for (var i = 0; i < els.length; i++) {
          var t = (els[i].textContent || '').trim();
          if (/^fale com o ifood$/i.test(t) && visivelEl(els[i])) return els[i].closest('a,button,[role="button"]') || els[i];
        }
        return null;
      }
      function desisolar(){
        document.body.classList.remove('pdv-so-chat');
        document.querySelectorAll('[data-pdv-hide]').forEach(function(x){ x.removeAttribute('data-pdv-hide'); });
        document.querySelectorAll('[data-pdv-veu]').forEach(function(x){ x.removeAttribute('data-pdv-veu'); });
      }
      var __pdvBuscaAjuda = null;
      window.pdvFaleComIfood = function (numero) {
        var n = String(numero || '').replace(/\D/g, '');
        if (!n) return false;
        try {
          if (__pdvBuscaAjuda) { clearInterval(__pdvBuscaAjuda); __pdvBuscaAjuda = null; }
          window.__pdvSemHolofote = true;
          desisolar();
          cortina(false); cortina(true);
          var c = document.getElementById('pdv-cortina'); if (c) c.textContent = 'Abrindo o Fale com o iFood do pedido #' + n + '...';
          var passo = 0, tent = 0, tinhaAjuda = !!ajudaCandidato();
          function fim(ok){
            clearInterval(__pdvBuscaAjuda); __pdvBuscaAjuda = null;
            if (ok) {
              window.__pdvSemHolofote = false;
              window.pdvIsolar(); cortina(false);
              envia({tipo:'ajuda', texto:'Ajuda do iFood aberta para o pedido #' + n, gestorInteiro:false});
            } else {
              cortina(false);
              envia({tipo:'ajuda', texto:'Não achei o pedido #' + n + ' no Gestor. Abra o pedido e toque em Fale com o iFood; depois toque em Só o chat.', gestorInteiro:true});
            }
          }
          __pdvBuscaAjuda = setInterval(function () {
            tent++;
            try {
              if (passo >= 2 && ajudaCandidato() && !tinhaAjuda) { fim(true); return; }
              if (passo === 0) {
                var inp = document.querySelector('#order-search,input[name="order-search"],input[role="search"],input[placeholder*="Buscar"]');
                if (!inp || !visivelEl(inp)) { if (tent === 1 && location.hash.indexOf('order-display') < 0) location.hash = '#/home/order-display/expedition'; }
                else { setValor(inp, n); passo = 1; }
              } else if (passo === 1) {
                var card = acharCardDoPedido(n); if (card) { card.click(); passo = 2; }
              } else if (passo === 2) {
                var a = acharLinkAjuda(); if (a) { a.click(); passo = 3; tinhaAjuda = false; }
              } else if (passo === 3) {
                if (ajudaCandidato()) { fim(true); return; }
              }
            } catch (e) {}
            if (tent >= 36) fim(false);
          }, 700);
          return true;
        } catch (e) { return false; }
      };

      // observador: qualquer mexida no DOM reconta (com folga) e tenta abrir/isolar.
      var pend = null;
      var ajudaAntes = false;
      function vigiarAjuda(){
        try {
          var agora = !!ajudaCandidato();
          if (agora !== ajudaAntes) {
            ajudaAntes = agora;
            // a gaveta entrou ou saiu: o holofote e refeito com os alvos de agora
            if (!window.__pdvSemHolofote && (document.body.classList.contains('pdv-so-chat') || agora)) window.pdvIsolar();
          }
          if (window.pdvAjustarAjuda) window.pdvAjustarAjuda();
        } catch (e) {}
      }
      function agenda(){ if (pend) return; pend = setTimeout(function(){ pend=null; window.pdvContar(); if (window.pdvAjustarRespostas) window.pdvAjustarRespostas(); vigiarAjuda(); }, 400); }
      // CORTINA: o dono nao quer ver o painel do Gestor nem por 3 segundos depois
      // de recarregar. Cobre a pagina desde a carga e so sai quando o chat esta
      // isolado. Nunca fica para sempre: cai sozinha em 25 s (tela morta e pior
      // que Gestor a mostra) e nunca cobre a tela de LOGIN (tem campo de senha).
      function cortina(on){
        try {
          var c = document.getElementById('pdv-cortina');
          if (!on) { if (c) c.remove(); return; }
          if (c || document.querySelector('input[type="password"]')) return;
          estilo();
          c = document.createElement('div'); c.id = 'pdv-cortina'; c.textContent = 'Abrindo o chat...';
          document.body.appendChild(c);
        } catch (e) {}
      }
      function liga(){
        cortina(true);
        setTimeout(function(){ cortina(false); }, 25000);
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
            if (window.pdvIsolar()) { pronto = true; cortina(false); }
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
          if (window.__pdvSemHolofote) return;   // o dono pediu o Gestor inteiro: nao reisolar
          if (!pronto) return;
          if (candidato()) return;
          if (ajudaCandidato()) return;          // ajuda aberta sem chat: nao clicar no chat (poderia fechar a ajuda)
          pronto = false;
          tentar();
        }, 1500);
      }
      if (document.body) liga(); else document.addEventListener('DOMContentLoaded', liga);
    })();
    """;
}
