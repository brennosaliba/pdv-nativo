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

    public const string TextoConferindo = "Conferindo o componente da Microsoft que o chat e o WhatsApp usam…";
    public const string TextoJaInstalado = "O componente da Microsoft do chat e do WhatsApp já está nesta máquina.";
    public const string TextoInstalando = "Instalando o componente da Microsoft do chat e do WhatsApp. Pode levar alguns minutos…";

    /// <summary>A frase do fim da instalação quando o componente não entrou. Uma só, para qualquer motivo.</summary>
    public const string AvisoSemComponente =
        "Não consegui instalar o componente da Microsoft que o chat e o WhatsApp usam. "
        + "O caixa vende normalmente. Com internet, rode este instalador de novo.";

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

    /// <summary>A decisão, com o leitor de registro injetado. Leitor que lança conta como chave ausente.</summary>
    public static Presenca Detectar(Func<Raiz, string, string?> lerPv)
    {
        foreach (var c in Chaves)
        {
            string? pv;
            try { pv = lerPv(c.Raiz, c.Caminho); } catch { pv = null; }
            if (VersaoValida(pv)) return new Presenca(true, pv!.Trim(), c);
        }
        return new Presenca(false, null, null);
    }

    public static Presenca DetectarNoWindows() => Detectar(LerPvDoRegistro);

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
        Action<int?> porcento, TimeSpan prazo, CancellationToken ct)
    {
        if (!hostAceito(url)) return "endereço fora da Microsoft: " + url.Host;
        var parcial = destino + ".baixando";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(prazo);
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
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
                while ((n = await entrada.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0)
                {
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
        catch (OperationCanceledException) { return ct.IsCancellationRequested ? "cancelado" : "o download passou do prazo"; }
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
    public sealed record Passos(
        Func<Presenca> Detectar,
        Func<string, Action<int?>, CancellationToken, Task<string?>> Baixar,
        Func<string, string?> ConferirAssinatura,
        Func<string, Instalacao.Execucao> Rodar,
        Func<TimeSpan, Task> Esperar);

    public static Passos PassosDeVerdade() => new(
        Detectar: DetectarNoWindows,
        Baixar: (destino, porcento, ct) => BaixarAsync(new Uri(UrlDoBootstrapper), destino, HostDaMicrosoft, porcento, PrazoDoDownload, ct),
        ConferirAssinatura: ConferirAssinaturaDaMicrosoft,
        Rodar: arquivo => Instalacao.RodarComPrazo(ComandoDeInstalacao(arquivo), PrazoDaInstalacao),
        Esperar: t => Task.Delay(t));

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
        string pastaTemporaria, CancellationToken ct = default)
    {
        static Resultado Falha(string detalhe) => new(false, false, AvisoSemComponente, detalhe);
        void Texto(string t) { try { progresso(t); } catch { } }
        void Barra(int? p) { try { barra(p); } catch { } }

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
        try
        {
            Texto(TextoBaixando(null));
            string? erro;
            try { erro = await passos.Baixar(arquivo, p => { Barra(p); Texto(TextoBaixando(p)); }, ct); }
            catch (Exception ex) { erro = ex.GetType().Name + ": " + ex.Message; }
            if (erro is not null) return Falha("download: " + erro);
            if (!File.Exists(arquivo)) return Falha("download: o arquivo não ficou no disco");

            // Conferida IMEDIATAMENTE antes de rodar, no mesmo caminho que vai rodar.
            if (passos.ConferirAssinatura(arquivo) is { } assinatura) return Falha("assinatura: " + assinatura);

            Texto(TextoInstalando);
            Barra(null);
            Instalacao.Execucao exec;
            try { exec = passos.Rodar(arquivo); }
            catch (Exception ex) { return Falha("rodar: " + ex.GetType().Name + ": " + ex.Message); }

            // Saiu com erro: uma conferência só. Terminou bem (ou passou do prazo, e o EdgeUpdate
            // pode seguir sozinho): a chave demora uns segundos para aparecer.
            var tentativas = exec.Terminou && exec.Codigo != 0 ? 1 : 10;
            for (var i = 0; i < tentativas; i++)
            {
                Presenca depois;
                try { depois = passos.Detectar(); } catch { depois = new Presenca(false, null, null); }
                if (depois.Instalado)
                {
                    Texto(TextoJaInstalado);
                    return new Resultado(true, false, null, $"instalado {depois.Versao} (código {exec.Codigo})");
                }
                if (i < tentativas - 1) await passos.Esperar(TimeSpan.FromSeconds(3));
            }
            return Falha($"rodou (terminou={exec.Terminou}, código {exec.Codigo}, encerrado={exec.Matou}) e a chave pv não apareceu");
        }
        finally
        {
            try { if (File.Exists(arquivo)) File.Delete(arquivo); } catch { }
            Barra(null);
        }
    }
}
