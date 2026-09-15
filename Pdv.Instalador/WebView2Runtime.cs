using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace Pdv.Instalador;

/// <summary>
/// O COMPONENTE DA MICROSOFT QUE O CHAT E O WHATSAPP USAM (WebView2 Runtime). 15/09/2026, Castelo.
///
/// O PC novo do Castelo não tinha o runtime, e o instalador não olhava: a conferência roda
/// `Pdv.exe --cupom-teste`, que nunca abre WebView2. A instalação terminava "pronta" e o dono
/// descobria no chat, com um painel mandando instalar um componente que ele nem sabe o que é.
///
/// A etapa, depois do caixa e do agente (nunca no lugar deles):
///  1. procura a chave `pv` do cliente {F3017226-...} do EdgeUpdate: HKLM 64 bits
///     (WOW6432Node), HKLM 32 bits e HKCU. Vale versão que não seja vazia nem "0.0.0.0"
///     (é o que a documentação da Microsoft manda conferir);
///  2. faltando, BAIXA o instalador oficial (Evergreen Bootstrapper, link fwlink da Microsoft)
///     aqui, na máquina da loja, só por https e só de domínio da Microsoft;
///  3. confere a assinatura Authenticode e que o assinante é a Microsoft ANTES de rodar: o
///     instalador do caixa roda como administrador, e um arquivo trocado no TEMP rodaria junto;
///  4. roda `/silent /install` com prazo e confere a chave de novo.
/// Falha em qualquer passo vira UMA frase no fim da instalação e nada mais: o caixa vende sem
/// chat, e rodar o instalador de novo com internet resolve.
///
/// Testes: Pdv.Testes/TestesWebView2Runtime.cs (tudo entra por <see cref="Passos"/>; a suíte não
/// baixa nada, não lê o registro real e não roda o instalador da Microsoft).
/// </summary>
public static class WebView2Runtime
{
    /// <summary>O id do runtime Evergreen no EdgeUpdate (documentação "Distribute your app and the WebView2 Runtime").</summary>
    public const string IdDoCliente = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    /// <summary>O link oficial da Microsoft para o Evergreen Bootstrapper (MicrosoftEdgeWebview2Setup.exe).</summary>
    public const string UrlDoBootstrapper = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    public static readonly TimeSpan PrazoDoDownload = TimeSpan.FromMinutes(4);

    /// <summary>O bootstrapper baixa o runtime inteiro (~150 MB) antes de instalar: numa internet de loja, demora.</summary>
    public static readonly TimeSpan PrazoDaInstalacao = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Passou do prazo e o instalador da Microsoft segue rodando: espera a chave mais este tanto
    /// (revisão 15/09). Ele NUNCA é morto: matar o bootstrapper deixava o EdgeUpdate instalando por
    /// trás e o caixa abria no meio da instalação.
    /// </summary>
    public static readonly TimeSpan EsperaDepoisDoPrazo = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PassoDaEspera = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Na atualização pelo botão do caixa (--atualizar) não há janela e o caixa está FECHADO: prazos
    /// curtos e nenhuma espera depois do prazo. O caixa volta em minutos; o instalador da Microsoft
    /// segue sozinho, e o "Tentar de novo" do chat pega o componente quando ele chegar.
    /// </summary>
    public static readonly TimeSpan PrazoDoDownloadNaAtualizacao = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan PrazoDaInstalacaoNaAtualizacao = TimeSpan.FromMinutes(4);

    /// <summary>Sem um byte por este tanto (cabeçalho ou corpo): desiste. Portal cativo aceita a conexão e não responde.</summary>
    public static readonly TimeSpan SemRespostaPadrao = TimeSpan.FromSeconds(30);

    public const string TextoConferindo = "Conferindo o componente da Microsoft que o chat e o WhatsApp usam…";
    public const string TextoJaInstalado = "O componente da Microsoft do chat e do WhatsApp já está nesta máquina.";
    public const string TextoInstalando = "Instalando o componente da Microsoft do chat e do WhatsApp. Pode levar alguns minutos…";

    /// <summary>A frase do fim da instalação quando o componente não entrou. Uma só, para qualquer motivo.</summary>
    public const string AvisoSemComponente =
        "Não consegui instalar o componente da Microsoft que o chat e o WhatsApp usam. "
        + "O caixa vende normalmente. Com internet, rode este instalador de novo.";

    /// <summary>A frase do fim quando o prazo passou e o instalador da Microsoft ainda está trabalhando.</summary>
    public const string AvisoAindaInstalando =
        "O componente da Microsoft do chat e do WhatsApp ainda está instalando. "
        + "O caixa vende normalmente. Daqui a uns minutos, toque em Tentar de novo no chat.";

    public static string TextoBaixando(int? porcento)
        => "Baixando o componente da Microsoft do chat e do WhatsApp…" + (porcento is { } p ? $" {p}%" : "");

    // ── 1. DETECÇÃO ─────────────────────────────────────────────────────────────

    public enum Raiz { MaquinaLocal, UsuarioAtual }

    public sealed record Chave(Raiz Raiz, string Caminho);

    /// <summary>Onde o EdgeUpdate registra o runtime, na ordem em que o instalador procura.</summary>
    public static IReadOnlyList<Chave> Chaves { get; } = new[]
    {
        new Chave(Raiz.MaquinaLocal, $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{IdDoCliente}"),
        new Chave(Raiz.MaquinaLocal, $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{IdDoCliente}"),
        new Chave(Raiz.UsuarioAtual, $@"Software\Microsoft\EdgeUpdate\Clients\{IdDoCliente}"),
    };

    public sealed record Presenca(bool Instalado, string? Versao, Chave? Onde);

    /// <summary>Versão que vale: não vazia, não "0.0.0.0" (runtime desinstalado deixa isso), e com cara de versão.</summary>
    public static bool VersaoValida(string? pv)
    {
        var v = pv?.Trim();
        return !string.IsNullOrEmpty(v) && v != "0.0.0.0" && Version.TryParse(v, out _);
    }

    /// <summary>
    /// A decisão, com o leitor de registro e a conferência dos arquivos injetados. Leitor que lança
    /// conta como chave ausente. Revisão 15/09: a chave sozinha não basta. O caixa decide pelo loader
    /// (a pasta do runtime), e com a pv gravada e a pasta faltando o instalador dizia "já está" para
    /// sempre enquanto o painel mandava instalar.
    /// </summary>
    public static Presenca Detectar(Func<Raiz, string, string?> lerPv, Func<Raiz, string, bool> temArquivos)
    {
        foreach (var c in Chaves)
        {
            string? pv;
            try { pv = lerPv(c.Raiz, c.Caminho); } catch { pv = null; }
            if (!VersaoValida(pv)) continue;
            bool arquivos;
            try { arquivos = temArquivos(c.Raiz, pv!.Trim()); } catch { arquivos = false; }
            if (arquivos) return new Presenca(true, pv!.Trim(), c);
        }
        return new Presenca(false, null, null);
    }

    public static Presenca DetectarNoWindows() => Detectar(LerPvDoRegistro, TemArquivos);

    /// <summary>
    /// O executável do runtime está onde a Microsoft instala? Máquina: Program Files (x86) ou
    /// Program Files. Usuário: LocalAppData. Pasta\Microsoft\EdgeWebView\Application\{versão}\msedgewebview2.exe.
    /// </summary>
    public static bool TemArquivos(Raiz raiz, string versao)
    {
        if (!VersaoValida(versao)) return false;
        var bases = raiz == Raiz.MaquinaLocal
            ? new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }
            : new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
        foreach (var b in bases)
        {
            if (string.IsNullOrWhiteSpace(b)) continue;
            try
            {
                if (File.Exists(Path.Combine(b, "Microsoft", "EdgeWebView", "Application", versao.Trim(), "msedgewebview2.exe")))
                    return true;
            }
            catch { /* caminho torto conta como ausente */ }
        }
        return false;
    }

    private static string? LerPvDoRegistro(Raiz raiz, string caminho)
    {
        using var baseKey = RegistryKey.OpenBaseKey(
            raiz == Raiz.MaquinaLocal ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
        using var k = baseKey.OpenSubKey(caminho);
        return k?.GetValue("pv") as string;
    }

    // ── 2. DOWNLOAD ─────────────────────────────────────────────────────────────

    /// <summary>https em microsoft.com ou subdomínio (o fwlink redireciona para a CDN da Microsoft).</summary>
    public static bool HostDaMicrosoft(Uri? uri)
    {
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        return host == "microsoft.com" || host.EndsWith(".microsoft.com", StringComparison.Ordinal);
    }

    /// <summary>
    /// Baixa <paramref name="url"/> em <paramref name="destino"/>. Null = baixou; senão, o motivo.
    /// Nunca lança. Confere o endereço antes de pedir e o endereço FINAL (depois dos
    /// redirecionamentos) antes de gravar; arquivo pela metade é apagado.
    /// </summary>
    public static async Task<string?> BaixarAsync(Uri url, string destino, Func<Uri, bool> hostAceito,
        Action<int?> porcento, TimeSpan prazo, CancellationToken ct, TimeSpan? semResposta = null)
    {
        if (!hostAceito(url)) return "endereço fora da Microsoft: " + url.Host;
        var parcial = destino + ".baixando";
        // Revisão 15/09: sem internet de verdade (portal cativo, DNS que resolve e rota que não
        // existe) o download ficava até 4 min parado em "Baixando…". A conexão tem prazo curto, e
        // cabeçalho e corpo desistem depois de um tanto sem nenhum byte.
        var quieto = semResposta ?? SemRespostaPadrao;
        var parou = false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(prazo);
            using var mudo = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            using var handler = new SocketsHttpHandler { ConnectTimeout = quieto < TimeSpan.FromSeconds(20) ? quieto : TimeSpan.FromSeconds(20) };
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            HttpResponseMessage respBruta;
            mudo.CancelAfter(quieto);
            try { respBruta = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, mudo.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested) { parou = true; throw; }
            using var resp = respBruta;
            var final = resp.RequestMessage?.RequestUri ?? url;
            if (!hostAceito(final)) return "o download foi redirecionado para fora da Microsoft: " + final.Host;
            if (!resp.IsSuccessStatusCode) return $"HTTP {(int)resp.StatusCode}";

            var total = resp.Content.Headers.ContentLength;
            await using (var entrada = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
            await using (var saida = new FileStream(parcial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[81920];
                long lidos = 0;
                int? ultimo = null;
                porcento(total is > 0 ? 0 : null);
                int n;
                while (true)
                {
                    mudo.CancelAfter(quieto);
                    try { n = await entrada.ReadAsync(buf, mudo.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cts.IsCancellationRequested) { parou = true; throw; }
                    if (n <= 0) break;
                    await saida.WriteAsync(buf.AsMemory(0, n), cts.Token).ConfigureAwait(false);
                    lidos += n;
                    if (total is > 0)
                    {
                        var p = (int)Math.Min(100, lidos * 100 / total.Value);
                        if (p != ultimo) { ultimo = p; porcento(p); }
                    }
                }
                if (total is > 0 && lidos != total) return $"o download parou no meio ({lidos} de {total} bytes)";
            }
            File.Move(parcial, destino, overwrite: true);
            return null;
        }
        catch (OperationCanceledException)
        {
            return ct.IsCancellationRequested ? "cancelado"
                : parou ? $"a Microsoft parou de responder por {quieto.TotalSeconds:0} s"
                : "o download passou do prazo";
        }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
        finally { try { if (File.Exists(parcial)) File.Delete(parcial); } catch { } }
    }

    // ── 3. ASSINATURA ───────────────────────────────────────────────────────────

    /// <summary>
    /// Null quando o arquivo tem assinatura Authenticode válida (WinVerifyTrust) e o assinante é
    /// a Microsoft (O=Microsoft Corporation). Sem rede: a revogação não é consultada, para uma
    /// loja com internet ruim não travar aqui. Qualquer outra coisa devolve o motivo.
    /// </summary>
    public static string? ConferirAssinaturaDaMicrosoft(string arquivo)
    {
        if (!File.Exists(arquivo)) return "o arquivo não existe";
        int resultado;
        try { resultado = Wintrust.Verificar(arquivo); }
        catch (Exception ex) { return "não consegui conferir a assinatura: " + ex.Message; }
        if (resultado != 0) return $"assinatura inválida (0x{resultado:X8})";
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = X509Certificate.CreateFromSignedFile(arquivo);
#pragma warning restore SYSLIB0057
            return cert.Subject.Contains("O=Microsoft Corporation", StringComparison.Ordinal)
                ? null
                : "não é assinado pela Microsoft: " + cert.Subject;
        }
        catch (Exception ex) { return "não consegui ler quem assinou: " + ex.Message; }
    }

    private static class Wintrust
    {
        private static readonly Guid AcaoVerificarV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential)]
        private struct ArquivoInfo
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Dados
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid acao, IntPtr dados);

        private const uint WtdUiNone = 2, WtdRevokeNone = 0, WtdChoiceFile = 1;
        private const uint WtdStateVerify = 1, WtdStateClose = 2, WtdCacheOnlyUrlRetrieval = 0x1000;

        public static int Verificar(string arquivo)
        {
            var caminho = Marshal.StringToHGlobalUni(arquivo);
            var info = Marshal.AllocHGlobal(Marshal.SizeOf<ArquivoInfo>());
            var dados = Marshal.AllocHGlobal(Marshal.SizeOf<Dados>());
            try
            {
                Marshal.StructureToPtr(new ArquivoInfo { cbStruct = (uint)Marshal.SizeOf<ArquivoInfo>(), pcwszFilePath = caminho }, info, false);
                var d = new Dados
                {
                    cbStruct = (uint)Marshal.SizeOf<Dados>(),
                    dwUIChoice = WtdUiNone,
                    fdwRevocationChecks = WtdRevokeNone,
                    dwUnionChoice = WtdChoiceFile,
                    pFile = info,
                    dwStateAction = WtdStateVerify,
                    dwProvFlags = WtdCacheOnlyUrlRetrieval,
                };
                Marshal.StructureToPtr(d, dados, false);
                var acao = AcaoVerificarV2;
                var r = WinVerifyTrust(new IntPtr(-1), ref acao, dados);
                d = Marshal.PtrToStructure<Dados>(dados);
                d.dwStateAction = WtdStateClose;
                Marshal.StructureToPtr(d, dados, false);
                WinVerifyTrust(new IntPtr(-1), ref acao, dados);
                return r;
            }
            finally
            {
                Marshal.FreeHGlobal(dados);
                Marshal.FreeHGlobal(info);
                Marshal.FreeHGlobal(caminho);
            }
        }
    }

    // ── 4. A ETAPA ──────────────────────────────────────────────────────────────

    /// <summary>O comando que instala em silêncio (o instalador do caixa já está elevado).</summary>
    public static ProcessStartInfo ComandoDeInstalacao(string arquivo)
    {
        var psi = new ProcessStartInfo { FileName = arquivo, WorkingDirectory = Path.GetDirectoryName(arquivo) ?? "" };
        psi.ArgumentList.Add("/silent");
        psi.ArgumentList.Add("/install");
        return psi;
    }

    /// <summary>Tudo o que a etapa faz no mundo de fora, para a suíte trocar por mentira.</summary>
    /// <param name="InstaladorAindaRodando">O instalador da Microsoft (bootstrapper ou EdgeUpdate) ainda está trabalhando? null = não sei, conta como sim.</param>
    public sealed record Passos(
        Func<Presenca> Detectar,
        Func<string, Action<int?>, CancellationToken, Task<string?>> Baixar,
        Func<string, string?> ConferirAssinatura,
        Func<string, Instalacao.Execucao> Rodar,
        Func<TimeSpan, Task> Esperar,
        Func<bool>? InstaladorAindaRodando = null);

    public static Passos PassosDeVerdade() => PassosDeVerdade(PrazoDoDownload, PrazoDaInstalacao);

    /// <summary>Os passos da atualização pelo botão do caixa: prazos curtos (ver <see cref="PrazoDaInstalacaoNaAtualizacao"/>).</summary>
    public static Passos PassosDaAtualizacao() => PassosDeVerdade(PrazoDoDownloadNaAtualizacao, PrazoDaInstalacaoNaAtualizacao);

    public static Passos PassosDeVerdade(TimeSpan prazoDoDownload, TimeSpan prazoDaInstalacao) => new(
        Detectar: DetectarNoWindows,
        Baixar: (destino, porcento, ct) => BaixarAsync(new Uri(UrlDoBootstrapper), destino, HostDaMicrosoft, porcento, prazoDoDownload, ct),
        ConferirAssinatura: ConferirAssinaturaDaMicrosoft,
        Rodar: arquivo => RodarSemMatar(ComandoDeInstalacao(arquivo), prazoDaInstalacao),
        Esperar: t => Task.Delay(t),
        InstaladorAindaRodando: InstaladorDaMicrosoftRodando);

    /// <summary>
    /// Roda e espera até o prazo, SEM matar no fim (revisão 15/09). O bootstrapper da Microsoft
    /// dispara o EdgeUpdate, que continua baixando e instalando: matar só o primeiro não para a
    /// instalação, só tira de quem instala a notícia de que ela continua. Sem redirecionar a
    /// saída: o processo pode viver mais que o instalador do caixa.
    /// </summary>
    public static Instalacao.Execucao RodarSemMatar(ProcessStartInfo psi, TimeSpan prazo)
    {
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        using var p = Process.Start(psi);
        if (p is null) return new Instalacao.Execucao(false, -1, "", false);
        var terminou = p.WaitForExit((int)Math.Clamp(prazo.TotalMilliseconds, 0, int.MaxValue));
        return new Instalacao.Execucao(terminou, terminou ? p.ExitCode : -1, "", false);
    }

    /// <summary>O bootstrapper (MicrosoftEdgeWebview2Setup*, o nome baixado leva um sufixo) ou o MicrosoftEdgeUpdate está rodando?</summary>
    public static bool InstaladorDaMicrosoftRodando()
    {
        var todos = Process.GetProcesses();
        try
        {
            return todos.Any(p =>
            {
                try
                {
                    var nome = p.ProcessName;
                    return nome.StartsWith("MicrosoftEdgeWebview2Setup", StringComparison.OrdinalIgnoreCase)
                        || nome.Equals("MicrosoftEdgeUpdate", StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            });
        }
        finally { foreach (var p in todos) p.Dispose(); }
    }

    /// <param name="Instalado">O runtime está na máquina no fim da etapa.</param>
    /// <param name="JaEstava">Já estava antes: nada foi baixado nem rodado.</param>
    /// <param name="Aviso">A frase para o fim da instalação, ou null.</param>
    /// <param name="Detalhe">O que aconteceu, para diagnóstico (nunca vai para a tela).</param>
    public sealed record Resultado(bool Instalado, bool JaEstava, string? Aviso, string Detalhe);

    /// <summary>
    /// A etapa inteira. Nunca lança: qualquer falha vira <see cref="AvisoSemComponente"/>. O
    /// arquivo baixado é apagado no fim, instalando ou não.
    /// </summary>
    public static async Task<Resultado> GarantirAsync(Passos passos, Action<string> progresso, Action<int?> barra,
        string pastaTemporaria, CancellationToken ct = default, TimeSpan? esperaDepoisDoPrazo = null)
    {
        static Resultado Falha(string detalhe) => new(false, false, AvisoSemComponente, detalhe);
        void Texto(string t) { try { progresso(t); } catch { } }
        void Barra(int? p) { try { barra(p); } catch { } }
        Presenca Detectou() { try { return passos.Detectar(); } catch { return new Presenca(false, null, null); } }
        bool AindaRodando() { try { return passos.InstaladorAindaRodando?.Invoke() ?? true; } catch { return true; } }

        Texto(TextoConferindo);
        Barra(null);
        Presenca antes;
        try { antes = passos.Detectar(); } catch { antes = new Presenca(false, null, null); }
        if (antes.Instalado)
        {
            Texto(TextoJaInstalado);
            return new Resultado(true, true, null, "já estava: " + antes.Versao);
        }

        var arquivo = Path.Combine(pastaTemporaria, "MicrosoftEdgeWebview2Setup-" + Guid.NewGuid().ToString("N")[..8] + ".exe");
        FileStream? trava = null;
        try
        {
            Texto(TextoBaixando(null));
            string? erro;
            try { erro = await passos.Baixar(arquivo, p => { Barra(p); Texto(TextoBaixando(p)); }, ct); }
            catch (Exception ex) { erro = ex.GetType().Name + ": " + ex.Message; }
            if (erro is not null) return Falha("download: " + erro);
            if (!File.Exists(arquivo)) return Falha("download: o arquivo não ficou no disco");

            // TRAVADO DA CONFERÊNCIA ATÉ RODAR (revisão 15/09). A pasta temporária é do usuário que
            // elevou: um processo comum dele podia trocar o .exe entre a conferência da assinatura e a
            // execução como administrador. Aberto só para leitura, com FileShare.Read: ninguém escreve
            // por cima nem tira do lugar, e a conferência e a execução continuam funcionando.
            try { trava = new FileStream(arquivo, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (Exception ex) { return Falha("travar o arquivo baixado: " + ex.GetType().Name + ": " + ex.Message); }

            // Conferida IMEDIATAMENTE antes de rodar, no mesmo caminho que vai rodar.
            if (passos.ConferirAssinatura(arquivo) is { } assinatura) return Falha("assinatura: " + assinatura);

            Texto(TextoInstalando);
            Barra(null);
            Instalacao.Execucao exec;
            try { exec = passos.Rodar(arquivo); }
            catch (Exception ex) { return Falha("rodar: " + ex.GetType().Name + ": " + ex.Message); }
            finally { trava.Dispose(); trava = null; }

            // Saiu com erro: uma conferência só. Terminou bem (ou passou do prazo, e o EdgeUpdate
            // pode seguir sozinho): a chave demora uns segundos para aparecer.
            var tentativas = exec.Terminou && exec.Codigo != 0 ? 1 : 10;
            for (var i = 0; i < tentativas; i++)
            {
                var depois = Detectou();
                if (depois.Instalado)
                {
                    Texto(TextoJaInstalado);
                    return new Resultado(true, false, null, $"instalado {depois.Versao} (código {exec.Codigo})");
                }
                if (i < tentativas - 1) await passos.Esperar(TimeSpan.FromSeconds(3));
            }

            // Passou do prazo e o instalador da Microsoft segue trabalhando: espera a chave mais um
            // pouco em vez de desistir com ele no meio (revisão 15/09).
            if (!exec.Terminou)
            {
                var limite = esperaDepoisDoPrazo ?? EsperaDepoisDoPrazo;
                for (var gasto = TimeSpan.Zero; gasto < limite && AindaRodando(); gasto += PassoDaEspera)
                {
                    await passos.Esperar(PassoDaEspera);
                    var depois = Detectou();
                    if (depois.Instalado)
                    {
                        Texto(TextoJaInstalado);
                        return new Resultado(true, false, null, $"instalado {depois.Versao} depois do prazo");
                    }
                }
                if (AindaRodando())
                    return new Resultado(false, false, AvisoAindaInstalando, "passou do prazo e o instalador da Microsoft segue rodando");
            }
            return Falha($"rodou (terminou={exec.Terminou}, código {exec.Codigo}, encerrado={exec.Matou}) e a chave pv não apareceu");
        }
        finally
        {
            try { trava?.Dispose(); } catch { }
            try { if (File.Exists(arquivo)) File.Delete(arquivo); } catch { }
            Barra(null);
        }
    }

    /// <summary>
    /// A etapa SEM janela, para a atualização pelo botão do caixa (--atualizar, 15/09/2026). Até
    /// aqui só o assistente com janela passava por ela, e um PC como o do Castelo que recebe a
    /// próxima versão pelo botão continuava sem o componente. Sem espera depois do prazo: o caixa
    /// está fechado. Nunca lança.
    /// </summary>
    public static Resultado GarantirNaAtualizacao(Passos passos, string pastaTemporaria)
    {
        try
        {
            return Task.Run(() => GarantirAsync(passos, _ => { }, _ => { }, pastaTemporaria, default, TimeSpan.Zero))
                .GetAwaiter().GetResult();
        }
        catch (Exception ex) { return new Resultado(false, false, AvisoSemComponente, "etapa: " + ex.GetType().Name + ": " + ex.Message); }
    }

    /// <summary>
    /// Uma linha em ProgramData\PdvNativo\componente-microsoft.txt com o desfecho da etapa (quando,
    /// modo, instalado, detalhe). É o que responde "o componente entrou nesse PC?" sem ir à loja.
    /// Nunca atrapalha a instalação.
    /// </summary>
    public static void Anotar(string modo, Resultado r)
    {
        try
        {
            Directory.CreateDirectory(Instalacao.PastaDados);
            File.AppendAllText(Path.Combine(Instalacao.PastaDados, "componente-microsoft.txt"),
                $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}  {modo}  instalado={r.Instalado} ja_estava={r.JaEstava}  {r.Detalhe}{Environment.NewLine}");
        }
        catch { /* diagnóstico nunca atrapalha */ }
    }
}
