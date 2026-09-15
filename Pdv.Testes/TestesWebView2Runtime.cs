using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using Pdv.Instalador;

namespace Pdv.Testes;

/// <summary>
/// O INSTALADOR PÕE O COMPONENTE DO CHAT E DO WHATSAPP (15/09/2026, Castelo).
///
/// O PC novo do Castelo não tinha o WebView2 Runtime da Microsoft. O instalador não conferia
/// nada: a conferência roda `--cupom-teste`, que nunca abre WebView2, então a instalação
/// terminava "pronta" e o dono só descobria no chat, com o painel mandando "instalar o
/// WebView2 Runtime". Agora, depois do caixa (e nunca no lugar dele), o instalador:
///   1. procura a chave `pv` do runtime (HKLM 64 e 32 bits, HKCU);
///   2. faltando, BAIXA o instalador oficial da Microsoft na própria máquina da loja;
///   3. confere que o arquivo é assinado pela Microsoft antes de rodar (o instalador está elevado);
///   4. roda em silêncio (/silent /install) com prazo, e confere a chave de novo.
/// Qualquer falha vira UMA frase no fim da instalação: o caixa vende sem chat.
///
/// Nenhum teste daqui baixa nada da internet, lê o registro real ou roda o instalador da
/// Microsoft: tudo entra por <see cref="WebView2Runtime.Passos"/>.
/// </summary>
public static class TestesWebView2Runtime
{
    private const string Wow = @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    private const string X86 = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    private const string Hkcu = @"Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    public static void Rodar(Action<bool, string> checar)
    {
        var raiz = Path.Combine(Path.GetTempPath(), "pdv-testes-wv2rt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(raiz);
        try
        {
            Deteccao(checar);
            Garantir(checar, raiz);
            Download(checar, raiz);
            Assinatura(checar, raiz);
            Janela(checar);
        }
        finally
        {
            try { Directory.Delete(raiz, recursive: true); } catch { }
        }
    }

    private static Func<WebView2Runtime.Raiz, string, string?> Registro(params (WebView2Runtime.Raiz Raiz, string Caminho, string? Pv)[] chaves)
        => (r, c) => chaves.FirstOrDefault(k => k.Raiz == r && string.Equals(k.Caminho, c, StringComparison.OrdinalIgnoreCase)).Pv;

    // ── 1. DETECÇÃO ─────────────────────────────────────────────────────────────
    private static void Deteccao(Action<bool, string> checar)
    {
        var M = WebView2Runtime.Raiz.MaquinaLocal;
        var U = WebView2Runtime.Raiz.UsuarioAtual;

        checar(WebView2Runtime.IdDoCliente == "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
               && WebView2Runtime.Chaves.Count == 3
               && WebView2Runtime.Chaves[0] == new WebView2Runtime.Chave(M, Wow)
               && WebView2Runtime.Chaves[1] == new WebView2Runtime.Chave(M, X86)
               && WebView2Runtime.Chaves[2] == new WebView2Runtime.Chave(U, Hkcu),
            "WR-1 procura a chave pv do runtime nos três lugares da Microsoft: HKLM 64 bits (WOW6432Node), HKLM 32 bits e HKCU");

        Func<WebView2Runtime.Raiz, string, bool> pastaPresente = (_, _) => true;
        var p64 = WebView2Runtime.Detectar(Registro((M, Wow, "152.0.4191.66")), pastaPresente);
        var p32 = WebView2Runtime.Detectar(Registro((M, X86, "120.0.2210.91")), pastaPresente);
        var pU = WebView2Runtime.Detectar(Registro((U, Hkcu, "140.0.3485.54")), pastaPresente);
        checar(p64 is { Instalado: true, Versao: "152.0.4191.66" } && p64.Onde == WebView2Runtime.Chaves[0]
               && p32 is { Instalado: true, Versao: "120.0.2210.91" } && pU is { Instalado: true } && pU.Onde!.Raiz == U,
            "WR-2 qualquer uma das três com versão vale como instalado, e diz qual versão e onde achou");

        var vazios = new[] { null, "", "   ", "0.0.0.0", "lixo" }
            .Select(pv => WebView2Runtime.Detectar(Registro((M, Wow, pv), (M, X86, pv), (U, Hkcu, pv)), pastaPresente))
            .ToList();
        checar(vazios.All(p => !p.Instalado && p.Versao is null),
            "WR-3 chave sem pv, pv vazio, '0.0.0.0' (desinstalado) ou lixo contam como NÃO instalado");
        checar(!WebView2Runtime.Detectar((_, _) => throw new UnauthorizedAccessException("registro trancado"), pastaPresente).Instalado
               && !WebView2Runtime.Detectar(Registro((M, Wow, "152.0.4191.66")), (_, _) => throw new IOException("disco")).Instalado,
            "WR-4 registro ou pasta que não deixa ler vira 'não instalado', nunca exceção no meio da instalação");
        checar(WebView2Runtime.Detectar(Registro((M, Wow, "0.0.0.0"), (U, Hkcu, "152.0.4191.66")), pastaPresente) is { Instalado: true, Versao: "152.0.4191.66" },
            "WR-5 um '0.0.0.0' numa chave não esconde a versão boa de outra");

        // revisão 15/09: a chave sozinha não basta (o caixa decide pelo loader, que precisa da pasta)
        var semPasta = WebView2Runtime.Detectar(Registro((M, Wow, "152.0.4191.66")), (_, _) => false);
        var soUsuario = WebView2Runtime.Detectar(Registro((M, Wow, "152.0.4191.66"), (U, Hkcu, "140.0.3485.54")), (r, _) => r == U);
        checar(!semPasta.Instalado && soUsuario is { Instalado: true, Versao: "140.0.3485.54" },
            "WR-36 chave pv com a pasta do runtime faltando NÃO conta como instalado (o instalador dizia 'já está' para sempre e o painel mandava instalar)");
        var versaoAqui = Pdv.Telas.HospedeWebView2.VersaoDoRuntime();
        checar(versaoAqui is not null && WebView2Runtime.TemArquivos(M, versaoAqui) && !WebView2Runtime.TemArquivos(M, "1.2.3.4") && !WebView2Runtime.TemArquivos(M, "lixo"),
            $"WR-37 nesta máquina o runtime {versaoAqui ?? "ausente"} é achado na pasta onde a Microsoft instala, e uma versão inventada não");
    }

    // ── 2. A ETAPA INTEIRA ────────────────────────────────────────────────────
    private sealed class Roteiro
    {
        public bool Instalado;
        public bool InstalaAoRodar = true;
        public string? ErroDownload;
        public bool DownloadLanca;
        public string? ErroAssinatura;
        public bool RodarLanca;
        public Instalacao.Execucao Execucao = new(true, 0, "", false);
        public int Deteccoes, Downloads, Conferencias, Execucoes, Esperas;
        public string? ArquivoBaixado, ArquivoRodado, ArquivoConferido;
        /// <summary>A chave pv aparece nesta detecção (o instalador da Microsoft terminou tarde). 0 = nunca por aqui.</summary>
        public int InstalaNaDeteccao;
        /// <summary>Na conferência, alguém tenta trocar o arquivo baixado (escrever por cima, ou tirar do lugar e pôr outro).</summary>
        public bool TentaTrocarNaConferencia;
        public bool Trocou;

        public WebView2Runtime.Passos Passos() => new(
            Detectar: () =>
            {
                Deteccoes++;
                if (InstalaNaDeteccao > 0 && Deteccoes >= InstalaNaDeteccao) Instalado = true;
                return Instalado ? new WebView2Runtime.Presenca(true, "152.0.4191.66", WebView2Runtime.Chaves[0]) : new WebView2Runtime.Presenca(false, null, null);
            },
            Baixar: async (destino, pct, ct) =>
            {
                Downloads++; ArquivoBaixado = destino;
                if (DownloadLanca) throw new HttpRequestException("Nenhuma conexão pôde ser feita");
                if (ErroDownload is not null) return ErroDownload;
                pct(0); pct(45); pct(100);
                await File.WriteAllTextAsync(destino, "bootstrapper de mentira", ct);
                return null;
            },
            ConferirAssinatura: arquivo =>
            {
                Conferencias++; ArquivoConferido = arquivo;
                if (TentaTrocarNaConferencia)
                {
                    try { File.WriteAllText(arquivo, "trocado por cima"); Trocou = true; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    try { File.Move(arquivo, arquivo + ".velho"); File.WriteAllText(arquivo, "trocado de lugar"); Trocou = true; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                return ErroAssinatura;
            },
            Rodar: arquivo =>
            {
                Execucoes++; ArquivoRodado = arquivo;
                if (RodarLanca) throw new System.ComponentModel.Win32Exception("o Windows não deixou");
                if (InstalaAoRodar && Execucao.Terminou && Execucao.Codigo == 0) Instalado = true;
                return Execucao;
            },
            Esperar: _ => { Esperas++; return Task.CompletedTask; });
    }

    private static (WebView2Runtime.Resultado R, List<string> Textos, List<int?> Barra) Rodar(Roteiro r, string pasta)
    {
        var textos = new List<string>(); var barra = new List<int?>();
        var res = WebView2Runtime.GarantirAsync(r.Passos(), textos.Add, barra.Add, pasta).GetAwaiter().GetResult();
        return (res, textos, barra);
    }

    private static void Garantir(Action<bool, string> checar, string raiz)
    {
        // WR-6 já instalado: não baixa, não roda, sem aviso
        {
            var r = new Roteiro { Instalado = true };
            var (res, textos, _) = Rodar(r, raiz);
            checar(res is { Instalado: true, JaEstava: true, Aviso: null } && r.Downloads == 0 && r.Execucoes == 0
                   && textos.Any(t => t.Contains("já está", StringComparison.Ordinal)),
                "WR-6 runtime já instalado: não baixa, não roda nada e não avisa");
        }

        // WR-7 caminho feliz
        {
            var r = new Roteiro();
            var (res, textos, barra) = Rodar(r, raiz);
            checar(res is { Instalado: true, JaEstava: false, Aviso: null } && r.Downloads == 1 && r.Conferencias == 1 && r.Execucoes == 1
                   && r.ArquivoBaixado == r.ArquivoConferido && r.ArquivoConferido == r.ArquivoRodado
                   && r.ArquivoBaixado!.StartsWith(raiz, StringComparison.OrdinalIgnoreCase),
                $"WR-7 faltando: baixa, confere a assinatura DO MESMO arquivo, roda e confere a chave de novo (detalhe={res.Detalhe})");
            checar(textos.Any(t => t.Contains("45%", StringComparison.Ordinal)) && barra.Contains(45) && barra.Contains(100) && barra.Last() is null
                   && textos.All(t => !t.Contains('—') && !t.Contains("WebView2", StringComparison.OrdinalIgnoreCase)),
                "WR-8 a tela acompanha: texto com a porcentagem do download, barra que anda e volta a girar na instalação; sem travessão e sem jargão");
            checar(!File.Exists(r.ArquivoBaixado), "WR-9 o instalador baixado é apagado no fim");
        }

        // WR-10 sem internet (download devolve erro) e download que lança
        foreach (var (nome, lanca) in new[] { ("devolve erro", false), ("lança", true) })
        {
            var r = new Roteiro { ErroDownload = lanca ? null : "sem internet", DownloadLanca = lanca };
            WebView2Runtime.Resultado? res = null; Exception? escapou = null;
            try { res = Rodar(r, raiz).R; } catch (Exception ex) { escapou = ex; }
            checar(escapou is null && res is { Instalado: false } && res.Aviso == WebView2Runtime.AvisoSemComponente
                   && r.Conferencias == 0 && r.Execucoes == 0 && !File.Exists(r.ArquivoBaixado ?? "x"),
                $"WR-10 download que {nome}: não roda nada, não derruba a instalação e deixa o aviso de uma frase");
        }

        // WR-11 assinatura que não é da Microsoft: nunca roda
        {
            var r = new Roteiro { ErroAssinatura = "não é assinado pela Microsoft" };
            var res = Rodar(r, raiz).R;
            checar(res is { Instalado: false } && res.Aviso == WebView2Runtime.AvisoSemComponente && r.Execucoes == 0
                   && res.Detalhe.Contains("assinado", StringComparison.Ordinal) && !File.Exists(r.ArquivoBaixado!),
                "WR-11 arquivo sem a assinatura da Microsoft NUNCA é executado (o instalador roda como administrador)");
        }

        // WR-12 rodou e a chave não apareceu; saiu com código de erro; lançou
        {
            var r = new Roteiro { InstalaAoRodar = false };
            var res = Rodar(r, raiz).R;
            checar(res is { Instalado: false } && res.Aviso == WebView2Runtime.AvisoSemComponente && r.Esperas >= 3 && r.Deteccoes >= 4,
                $"WR-12 rodou com sucesso mas a chave não apareceu: espera um pouco, confere de novo e avisa (esperas={r.Esperas})");

            var rCodigo = new Roteiro { Execucao = new Instalacao.Execucao(true, 1603, "", false) };
            var resCodigo = Rodar(rCodigo, raiz).R;
            checar(resCodigo is { Instalado: false } && resCodigo.Aviso is not null && rCodigo.Esperas == 0 && resCodigo.Detalhe.Contains("1603", StringComparison.Ordinal),
                "WR-13 instalador da Microsoft saiu com erro: avisa sem ficar esperando à toa, e o código vai no detalhe");

            var rLanca = new Roteiro { RodarLanca = true };
            WebView2Runtime.Resultado? resLanca = null; Exception? escapou = null;
            try { resLanca = Rodar(rLanca, raiz).R; } catch (Exception ex) { escapou = ex; }
            checar(escapou is null && resLanca is { Instalado: false, Aviso: not null },
                "WR-14 rodar que lança vira aviso, não exceção");
        }

        var cmd = WebView2Runtime.ComandoDeInstalacao(@"C:\tmp\MicrosoftEdgeWebview2Setup.exe");
        checar(cmd.FileName == @"C:\tmp\MicrosoftEdgeWebview2Setup.exe" && cmd.ArgumentList.SequenceEqual(new[] { "/silent", "/install" }),
            "WR-15 roda o instalador oficial em silêncio: /silent /install");
        checar(WebView2Runtime.UrlDoBootstrapper == "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
            "WR-16 o endereço é o link oficial da Microsoft para o instalador do runtime (Evergreen Bootstrapper)");
        checar(WebView2Runtime.AvisoSemComponente.Contains("vende", StringComparison.Ordinal)
               && WebView2Runtime.AvisoSemComponente.Contains("de novo", StringComparison.Ordinal)
               && !WebView2Runtime.AvisoSemComponente.Contains('—') && !WebView2Runtime.AvisoSemComponente.Contains("WebView2", StringComparison.OrdinalIgnoreCase),
            "WR-17 o aviso diz que o caixa vende e o que fazer (rodar de novo), sem travessão e sem jargão");
        checar(WebView2Runtime.PrazoDaInstalacao >= TimeSpan.FromMinutes(3) && WebView2Runtime.PrazoDoDownload >= TimeSpan.FromMinutes(2),
            "WR-18 prazos folgados para internet e PC de loja (download e instalação)");

        // ── revisão 15/09 ──────────────────────────────────────────────────────
        // WR-32 o instalador da Microsoft passou do prazo e segue trabalhando (o EdgeUpdate baixa o
        // runtime inteiro por trás): a etapa espera a chave em vez de desistir 30 s depois
        {
            var r = new Roteiro { InstalaAoRodar = false, Execucao = new Instalacao.Execucao(false, -1, "", false), InstalaNaDeteccao = 16 };
            var res = Rodar(r, raiz).R;
            checar(res is { Instalado: true, Aviso: null } && r.Deteccoes == 16,
                $"WR-32 passou do prazo e o instalador da Microsoft segue trabalhando: a etapa espera a chave aparecer em vez de desistir (detecções={r.Deteccoes}, detalhe={res.Detalhe})");
        }

        // WR-34 troca do arquivo entre a conferência da assinatura e a execução (o instalador está elevado)
        {
            var r = new Roteiro { TentaTrocarNaConferencia = true };
            var res = Rodar(r, raiz).R;
            checar(!r.Trocou && res.Instalado && r.Execucoes == 1,
                $"WR-34 depois de baixado, ninguém troca o arquivo até ele rodar: fica travado para escrita e para sair do lugar (trocou={r.Trocou})");
            try { File.Delete(r.ArquivoBaixado + ".velho"); } catch { }
        }

        // WR-40 passou do prazo e o instalador da Microsoft já saiu: não espera à toa
        {
            var r = new Roteiro { InstalaAoRodar = false, Execucao = new Instalacao.Execucao(false, -1, "", false) };
            var res = WebView2Runtime.GarantirAsync(r.Passos() with { InstaladorAindaRodando = () => false }, _ => { }, _ => { }, raiz).GetAwaiter().GetResult();
            checar(res is { Instalado: false } && res.Aviso == WebView2Runtime.AvisoSemComponente && r.Deteccoes == 11,
                $"WR-40 passou do prazo e o instalador da Microsoft já saiu: não fica esperando e avisa que não instalou (detecções={r.Deteccoes})");
        }

        // WR-41 ainda instalando no fim da espera: a frase diz isso, e não que falhou
        {
            var r = new Roteiro { InstalaAoRodar = false, Execucao = new Instalacao.Execucao(false, -1, "", false) };
            var res = WebView2Runtime.GarantirAsync(r.Passos() with { InstaladorAindaRodando = () => true }, _ => { }, _ => { }, raiz).GetAwaiter().GetResult();
            var esperado = TimeSpan.FromTicks(WebView2Runtime.EsperaDepoisDoPrazo.Ticks / WebView2Runtime.PassoDaEspera.Ticks * WebView2Runtime.PassoDaEspera.Ticks);
            checar(res is { Instalado: false } && res.Aviso == WebView2Runtime.AvisoAindaInstalando && r.Esperas == 9 + (int)(esperado / WebView2Runtime.PassoDaEspera)
                   && !WebView2Runtime.AvisoAindaInstalando.Contains('—') && WebView2Runtime.AvisoAindaInstalando.Contains("vende", StringComparison.Ordinal),
                $"WR-41 ainda instalando no fim da espera: avisa que está instalando, que o caixa vende e o que fazer depois (esperas={r.Esperas})");
        }

        // WR-42 na atualização pelo botão (sem janela, caixa fechado): prazo curto e nenhuma espera depois dele
        {
            var r = new Roteiro { InstalaAoRodar = false, Execucao = new Instalacao.Execucao(false, -1, "", false) };
            var res = WebView2Runtime.GarantirNaAtualizacao(r.Passos() with { InstaladorAindaRodando = () => true }, raiz);
            var rOk = new Roteiro();
            var resOk = WebView2Runtime.GarantirNaAtualizacao(rOk.Passos(), raiz);
            var rJa = new Roteiro { Instalado = true };
            var resJa = WebView2Runtime.GarantirNaAtualizacao(rJa.Passos(), raiz);
            checar(res.Aviso == WebView2Runtime.AvisoAindaInstalando && r.Esperas == 9
                   && resOk.Instalado && resJa is { Instalado: true, JaEstava: true } && rJa.Downloads == 0
                   && WebView2Runtime.PrazoDoDownloadNaAtualizacao + WebView2Runtime.PrazoDaInstalacaoNaAtualizacao <= TimeSpan.FromMinutes(6),
                $"WR-42 na atualização pelo botão: já instalado não baixa nada; faltando instala; passou do prazo não espera mais (o caixa volta em minutos) (esperas={r.Esperas})");
        }
    }

    // ── 3. O DOWNLOAD DE VERDADE, CONTRA UM SERVIDOR LOCAL ────────────────────
    private static void Download(Action<bool, string> checar, string raiz)
    {
        checar(WebView2Runtime.HostDaMicrosoft(new Uri("https://go.microsoft.com/fwlink/p/?LinkId=2124703"))
               && WebView2Runtime.HostDaMicrosoft(new Uri("https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/x/MicrosoftEdgeWebview2Setup.exe"))
               && !WebView2Runtime.HostDaMicrosoft(new Uri("http://go.microsoft.com/fwlink"))
               && !WebView2Runtime.HostDaMicrosoft(new Uri("https://microsoft.com.golpe.net/setup.exe"))
               && !WebView2Runtime.HostDaMicrosoft(new Uri("https://naomicrosoft.com/setup.exe"))
               && !WebView2Runtime.HostDaMicrosoft(null),
            "WR-19 só aceita baixar de https em domínio da Microsoft (depois de todos os redirecionamentos)");

        var conteudo = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
        using var servidor = new ServidorLocal(conteudo);

        var destino = Path.Combine(raiz, "baixado.exe");
        var pcts = new List<int?>();
        var erro = WebView2Runtime.BaixarAsync(new Uri(servidor.Url + "setup.exe"), destino, _ => true, pcts.Add, TimeSpan.FromSeconds(20), CancellationToken.None)
            .GetAwaiter().GetResult();
        checar(erro is null && File.Exists(destino) && File.ReadAllBytes(destino).SequenceEqual(conteudo) && pcts.Contains(100) && pcts.Where(p => p is not null).SequenceEqual(pcts.Where(p => p is not null).OrderBy(p => p)),
            $"WR-20 baixa o arquivo inteiro e a porcentagem só anda para a frente até 100 (erro={erro ?? "nenhum"})");

        var recusado = Path.Combine(raiz, "recusado.exe");
        var erroHost = WebView2Runtime.BaixarAsync(new Uri(servidor.Url + "setup.exe"), recusado, WebView2Runtime.HostDaMicrosoft, _ => { }, TimeSpan.FromSeconds(20), CancellationToken.None)
            .GetAwaiter().GetResult();
        checar(erroHost is not null && !File.Exists(recusado), "WR-21 endereço fora da Microsoft: não grava nada e devolve o motivo");

        var nao = Path.Combine(raiz, "404.exe");
        var erro404 = WebView2Runtime.BaixarAsync(new Uri(servidor.Url + "nao-existe"), nao, _ => true, _ => { }, TimeSpan.FromSeconds(20), CancellationToken.None)
            .GetAwaiter().GetResult();
        checar(erro404 is not null && erro404.Contains("404", StringComparison.Ordinal) && !File.Exists(nao),
            "WR-22 resposta 404 vira motivo (com o código), sem arquivo pela metade");

        var morto = Path.Combine(raiz, "morto.exe");
        var erroMorto = WebView2Runtime.BaixarAsync(new Uri("http://127.0.0.1:9/setup.exe"), morto, _ => true, _ => { }, TimeSpan.FromSeconds(5), CancellationToken.None)
            .GetAwaiter().GetResult();
        checar(erroMorto is not null && !File.Exists(morto), "WR-23 sem conexão: devolve o motivo, não lança");

        // WR-39 (revisão 15/09) portal cativo: aceita a conexão e não responde nada
        var mudo = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        mudo.Start();
        var portaMuda = ((IPEndPoint)mudo.LocalEndpoint).Port;
        var aceitos = new System.Collections.Concurrent.ConcurrentBag<System.Net.Sockets.TcpClient>();
        _ = Task.Run(async () => { try { while (true) aceitos.Add(await mudo.AcceptTcpClientAsync()); } catch { } });
        var destinoMudo = Path.Combine(raiz, "mudo.exe");
        var relogio = Stopwatch.StartNew();
        var erroMudo = WebView2Runtime.BaixarAsync(new Uri($"http://127.0.0.1:{portaMuda}/setup.exe"), destinoMudo, _ => true, _ => { },
            TimeSpan.FromSeconds(60), CancellationToken.None, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        var tempoMudo = relogio.Elapsed;
        try { mudo.Stop(); } catch { }
        foreach (var c in aceitos) { try { c.Dispose(); } catch { } }
        checar(erroMudo is not null && tempoMudo < TimeSpan.FromSeconds(15) && !File.Exists(destinoMudo),
            $"WR-39 servidor que aceita e não responde: desiste em segundos, sem esperar o prazo inteiro ({tempoMudo.TotalSeconds:0.0} s: {erroMudo ?? "baixou?"})");

        // WR-38 (revisão 15/09) o instalador da Microsoft não é morto no prazo: segue sozinho
        var marca = Path.Combine(raiz, "marca-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = $"/c ping -n 3 127.0.0.1 >nul & echo ok> \"{marca}\"",
        };
        var relogioSem = Stopwatch.StartNew();
        var exec = WebView2Runtime.RodarSemMatar(psi, TimeSpan.FromMilliseconds(300));
        var voltou = relogioSem.Elapsed;
        var apareceu = false;
        for (var i = 0; i < 150 && !apareceu; i++) { Thread.Sleep(100); apareceu = File.Exists(marca); }
        checar(exec is { Terminou: false, Matou: false } && voltou < TimeSpan.FromSeconds(2) && apareceu,
            $"WR-38 passou do prazo: volta sem matar o programa, que termina o trabalho sozinho (voltou em {voltou.TotalSeconds:0.0} s, terminou o trabalho={apareceu})");
    }

    private sealed class ServidorLocal : IDisposable
    {
        private readonly HttpListener _l = new();
        public string Url { get; }
        public ServidorLocal(byte[] conteudo)
        {
            var sock = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            sock.Start(); var porta = ((IPEndPoint)sock.LocalEndpoint).Port; sock.Stop();
            Url = $"http://127.0.0.1:{porta}/";
            _l.Prefixes.Add(Url);
            _l.Start();
            _ = Task.Run(async () =>
            {
                while (_l.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _l.GetContextAsync(); } catch { return; }
                    try
                    {
                        if (ctx.Request.Url!.AbsolutePath != "/setup.exe") { ctx.Response.StatusCode = 404; ctx.Response.Close(); continue; }
                        ctx.Response.ContentLength64 = conteudo.Length;
                        for (var i = 0; i < conteudo.Length; i += 32_768)
                            await ctx.Response.OutputStream.WriteAsync(conteudo.AsMemory(i, Math.Min(32_768, conteudo.Length - i)));
                        ctx.Response.Close();
                    }
                    catch { try { ctx.Response.Abort(); } catch { } }
                }
            });
        }
        public void Dispose() { try { _l.Stop(); _l.Close(); } catch { } }
    }

    // ── 4. A ASSINATURA ───────────────────────────────────────────────────────
    private static void Assinatura(Action<bool, string> checar, string raiz)
    {
        // coreclr.dll do runtime .NET que roda esta suíte: assinado pela Microsoft, com a assinatura dentro do arquivo
        var coreclr = Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");
        checar(File.Exists(coreclr) && WebView2Runtime.ConferirAssinaturaDaMicrosoft(coreclr) is null,
            $"WR-24 um binário assinado pela Microsoft passa na conferência ({WebView2Runtime.ConferirAssinaturaDaMicrosoft(coreclr) ?? "ok"})");

        var texto = Path.Combine(raiz, "MicrosoftEdgeWebview2Setup.exe");
        File.WriteAllText(texto, "não sou um programa");
        checar(WebView2Runtime.ConferirAssinaturaDaMicrosoft(texto) is not null, "WR-25 arquivo sem assinatura não passa");

        var adulterado = Path.Combine(raiz, "adulterado.dll");
        var bytes = File.ReadAllBytes(coreclr);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(adulterado, bytes);
        checar(WebView2Runtime.ConferirAssinaturaDaMicrosoft(adulterado) is not null,
            "WR-26 binário da Microsoft com UM byte trocado não passa (a assinatura não bate mais)");
        checar(WebView2Runtime.ConferirAssinaturaDaMicrosoft(Path.Combine(raiz, "nao-existe.exe")) is not null,
            "WR-27 arquivo que sumiu não passa");

        // WR-35 a trava do WR-34 não atrapalha o que vem depois: com o arquivo aberto só para leitura
        // (FileShare.Read), a conferência da assinatura e a execução funcionam de verdade
        var dllTravada = Path.Combine(raiz, "coreclr-travado.dll");
        var exeTravado = Path.Combine(raiz, "cmd-travado.exe");
        File.Copy(coreclr, dllTravada);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exeTravado);
        string? assinaturaTravada;
        Instalacao.Execucao execTravado;
        var escreveuTravado = false;
        using (new FileStream(dllTravada, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (new FileStream(exeTravado, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            assinaturaTravada = WebView2Runtime.ConferirAssinaturaDaMicrosoft(dllTravada);
            var psi = new ProcessStartInfo(exeTravado);
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("exit 7");
            execTravado = Instalacao.RodarComPrazo(psi, TimeSpan.FromSeconds(30));
            try { File.WriteAllText(exeTravado, "x"); escreveuTravado = true; } catch (IOException) { }
        }
        checar(assinaturaTravada is null && execTravado is { Terminou: true, Codigo: 7 } && !escreveuTravado,
            $"WR-35 com o arquivo travado para escrita, a assinatura confere e o programa roda (assinatura={assinaturaTravada ?? "ok"}, código={execTravado.Codigo}, escreveu={escreveuTravado})");
    }

    // ── 5. A JANELA DO INSTALADOR ─────────────────────────────────────────────
    private static void Janela(Action<bool, string> checar)
    {
        string? fonte = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null && fonte is null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "Pdv.Instalador", "JanelaInstalador.xaml.cs");
            if (File.Exists(c)) fonte = File.ReadAllText(c);
        }
        fonte ??= "";
        var iCaixa = fonte.IndexOf("Instalacao.Instalar(", StringComparison.Ordinal);
        var iWeb = fonte.IndexOf("WebView2Runtime.GarantirAsync(", StringComparison.Ordinal);
        var iPayGo = fonte.IndexOf("PayGo.Decidir(", StringComparison.Ordinal);
        checar(iCaixa > 0 && iWeb > iCaixa && iPayGo > iWeb,
            "WR-28 a janela instala o componente DEPOIS do caixa (que já está de pé) e antes do PayGo");
        checar(Regex.IsMatch(fonte, @"WebView2Runtime\.GarantirAsync\([^;]*WebView2Runtime\.PassosDeVerdade\(\)")
               && fonte.Contains("Barra.IsIndeterminate", StringComparison.Ordinal)
               && fonte.Contains("_avisoComponente", StringComparison.Ordinal),
            "WR-29 com os passos de verdade, a barra mostra a porcentagem e o aviso chega ao fim da instalação");
        var trecho = iWeb < 0 ? "" : fonte[iWeb..Math.Min(fonte.Length, iWeb + 600)];
        checar(trecho.Length > 0 && !trecho.Contains("return falha", StringComparison.Ordinal) && !trecho.Contains("Falhou(", StringComparison.Ordinal),
            "WR-30 falha do componente não interrompe a instalação (o caixa vende sem chat)");

        // WR-31 (revisão 15/09) o caixa se atualiza chamando o instalador com --atualizar, que NÃO passa
        // pela janela: sem a etapa lá, um PC como o do Castelo continuava sem o componente para sempre
        var app = FonteDoInstalador("App.xaml.cs");
        var silencioso = CorpoDe(app, "private static string? AtualizarSilencioso(");
        var iInstalar = silencioso.IndexOf("Instalacao.Instalar(", StringComparison.Ordinal);
        var iComponente = silencioso.IndexOf("WebView2Runtime.", StringComparison.Ordinal);
        var iFim = silencioso.LastIndexOf("return null;", StringComparison.Ordinal);
        var iAtualizar = app.IndexOf("AtualizarSilencioso(out", StringComparison.Ordinal);
        var iAbrir = app.IndexOf("Instalacao.AbrirCaixa(", StringComparison.Ordinal);
        checar(iInstalar > 0 && iComponente > iInstalar && iFim > iComponente && iAtualizar > 0 && iAbrir > iAtualizar,
            "WR-31 atualizar pelo botão do caixa (--atualizar) também põe o componente: depois do caixa instalado e antes de reabrir o caixa");

        // WR-33 (revisão 15/09) o instalador da Microsoft nunca é morto no meio: o Kill do prazo matava
        // só o bootstrapper e deixava o EdgeUpdate instalando por trás, com o caixa abrindo no meio
        var runtime = FonteDoInstalador("WebView2Runtime.cs");
        var passosDeVerdade = CorpoDe(runtime, "public static Passos PassosDeVerdade(");
        checar(passosDeVerdade.Length > 0 && runtime.Length > 0 && !runtime.Contains("RodarComPrazo(", StringComparison.Ordinal),
            "WR-33 os passos de verdade não matam o instalador da Microsoft no prazo (nada no componente passa pelo RodarComPrazo, que dá Kill)");
    }

    private static string FonteDoInstalador(string arquivo)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "Pdv.Instalador", arquivo);
            if (File.Exists(c)) return File.ReadAllText(c).Replace("\r\n", "\n");
        }
        return "";
    }

    private static string CorpoDe(string fonte, string assinatura)
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
}
