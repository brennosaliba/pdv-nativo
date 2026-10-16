using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Pdv.Nucleo;

namespace Pdv.Instalador;

/// <summary>
/// O trabalho de verdade do instalador — separado da janela para a suíte
/// (TestesInstalador) exercitar TUDO em pasta de sandbox antes de qualquer
/// máquina de loja ver este exe.
///
/// Regras de ouro:
///  1. DADOS SÃO SAGRADOS: instalar por cima, trocar a marca ou desinstalar
///     NUNCA toca em C:\ProgramData\PdvNativo (banco de vendas, segredos,
///     perfil do chat). Versão nova troca só o programa.
///  2. A PASTA INTEIRA VIAJA. O Pdv.exe sozinho NÃO ABRE: o publish deixa as
///     bibliotecas nativas do WPF, do SQLite e do WebView2 soltas ao lado dele,
///     e sem elas o processo morre em DllNotFoundException antes da primeira
///     tela. Copiar só o exe foi como este instalador nasceu — e produzia uma
///     instalação que nunca abriu. Ver <see cref="Essenciais"/>.
///  3. Iniciar com o Windows é entrada Run em HKLM (todos os usuários) — NÃO é
///     serviço do Windows: serviço roda na sessão 0, sem tela, e frente de caixa
///     com toque, impressora e pinpad precisa de usuário logado.
///  4. A ACL do ProgramData é ajustada na instalação: sem ela, o segundo usuário
///     do Windows que logar no caixa toma "attempt to write a readonly database".
/// </summary>
public static class Instalacao
{
    // NOME DO PRODUTO: nao e so texto. Ele vira a pasta de instalacao
    // (PastaDestinoPadrao), o DisplayName do Adicionar/Remover Programas e o nome
    // do atalho. Trocar a palavra instalaria em outro diretorio e deixaria a
    // instalacao antiga orfa — por isso existe MigrarInstalacaoAntiga().
    //
    // O nome e do FABRICANTE do sistema, nao da loja que comprou: uma padaria que
    // assina o servico nao pode ver a marca de outro cliente no Adicionar/Remover
    // Programas. O nome DA LOJA aparece onde importa (tela, cupom, comprovante) e
    // vem da configuracao, nao daqui.
    // 11/09/2026: o produto se chama MMFood; MMTech e a empresa. A chave de
    // desinstalacao (PdvMMTech) fica como esta para nao orfanar quem ja instalou.
    public const string NomePrograma = "MMFood";
    public const string Fabricante = "MMTech";

    /// <summary>Como o produto se chamava antes. Existe só para a migração: uma
    /// máquina que já tinha a versão anterior instalada não pode ficar com duas
    /// entradas no Adicionar/Remover Programas nem com dois atalhos.</summary>
    public const string NomeAntigo = "PDV American Day";

    /// <summary>
    /// TODOS os nomes que o produto já teve, do mais recente para o mais antigo.
    ///
    /// Não é enfeite: a loja da Savassi foi instalada como "PDV MMTech" (nome de
    /// 29/08/2026) e o programa mora em `Program Files\PDV MMTech`. Sem esta lista, a
    /// atualização para a 1.0 procurava `Program Files\MMFood`, não achava e morria com
    /// "o caixa não está instalado nesta máquina" — numa loja a 800 km, a 10 minutos de
    /// abrir. Nome novo vai NA FRENTE; nenhum sai daqui.
    /// </summary>
    public static readonly string[] NomesAntigos = { "PDV MMTech", NomeAntigo };

    /// <summary>
    /// Onde o caixa está instalado NESTA máquina, ou null se não está. Procura, nesta
    /// ordem: a pasta do nome de hoje, as pastas dos nomes antigos e o InstallLocation
    /// que o próprio instalador gravou no registro (a rede de segurança para quem
    /// instalou em outro lugar).
    /// </summary>
    public static string? PastaInstalada()
    {
        if (File.Exists(Path.Combine(PastaDestinoPadrao, "Pdv.exe"))) return PastaDestinoPadrao;
        foreach (var nome in NomesAntigos)
        {
            var p = EmProgramFiles(nome);
            if (File.Exists(Path.Combine(p, "Pdv.exe"))) return p;
        }
        foreach (var chave in new[] { ChaveUninstall, ChaveUninstallAntiga })
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(chave);
                if (k?.GetValue("InstallLocation") as string is { Length: > 0 } loc
                    && File.Exists(Path.Combine(loc, "Pdv.exe")))
                    return loc;
            }
            catch { /* sem permissão de leitura no registro: segue para a próxima */ }
        }
        return null;
    }

    /// <summary>
    /// ONDE O ASSISTENTE INSTALA (fechamento da 1.0.36): onde o caixa já está, e só sem caixa nesta
    /// máquina na pasta do nome de hoje. A Savassi mora em "PDV MMTech", com o Shell do quiosque apontando
    /// para lá. As frases de falha mandam "instale de novo", e o assistente instalava em MMFood e a
    /// migração apagava "PDV MMTech": o Shell ficava apontando para um Pdv.exe que não existe (tela preta
    /// no boot seguinte, com a trava do Ctrl+Alt+Del). A atualização silenciosa já fazia assim.
    /// </summary>
    public static string PastaParaInstalar() => EscolherDestino(PastaInstalada(), PastaDestinoPadrao);

    /// <summary>A regra de <see cref="PastaParaInstalar"/>, pura: a pasta instalada, e só sem ela a padrão.</summary>
    public static string EscolherDestino(string? instalada, string padrao)
        => string.IsNullOrWhiteSpace(instalada) ? padrao : instalada;

    private const string ChaveRun = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ChaveUninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PdvMMTech";
    private const string ChaveUninstallAntiga = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PdvAmericanDay";

    public static string PastaDestinoPadrao => EmProgramFiles(NomePrograma);
    public static string PastaDestinoAntiga => EmProgramFiles(NomeAntigo);

    private static string EmProgramFiles(string nome) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), nome);

    public static string PastaDados =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PdvNativo");

    /// <summary>
    /// Bibliotecas nativas que o Pdv.exe carrega por P/Invoke:
    ///   wpfgfx / PresentationNative / D3DCompiler / PenImc → o WPF não desenha;
    ///   vcruntime → dependência das anteriores;
    ///   e_sqlite3 → o banco de vendas não abre;
    ///   WebView2Loader → o chat do Gestor de Pedidos não sobe.
    ///
    /// ⚠️ ESTA LISTA NÃO É REQUISITO — é DIAGNÓSTICO, e a diferença custou uma volta.
    /// Se o publish usa <c>IncludeNativeLibrariesForSelfExtract=true</c>, elas moram
    /// DENTRO do exe e a pasta legítima não tem nenhuma delas (medido: o publish v21 é
    /// só Pdv.exe, e ele abre sozinho). Sem a flag, elas saem soltas e aí o exe sozinho
    /// morre em DllNotFoundException (medido: o publish v20). Como as duas formas são
    /// válidas, exigir a lista reprovaria metade das pastas boas — foi o que a primeira
    /// versão deste arquivo fazia.
    ///
    /// Quem garante de verdade é <see cref="ConferirQueOProgramaAbre"/>: em vez de
    /// adivinhar quais arquivos deveriam existir, ele MANDA O PROGRAMA ABRIR. A lista
    /// sobrou para dizer ao operador o que provavelmente falta quando ele não abre.
    /// </summary>
    public static readonly string[] BibliotecasNativas =
    {
        "wpfgfx_cor3.dll",
        "PresentationNative_cor3.dll",
        "D3DCompiler_47_cor3.dll",
        "PenImc_cor3.dll",
        "vcruntime140_cor3.dll",
        "e_sqlite3.dll",
        "WebView2Loader.dll",
    };

    /// <summary>Sufixo dos arquivos que a atualização não conseguiu apagar por
    /// estarem em uso. Ficam para trás e somem na instalação seguinte.</summary>
    public const string SufixoVelho = ".velho";

    /// <summary>A PASTA do PDV viaja junto com o instalador (ao lado dele, ou
    /// extraída do próprio pacote). Nada de baixar 165 MB no meio da loja.</summary>
    public static string? AcharOrigemAoLado()
    {
        var aqui = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        if (File.Exists(Path.Combine(aqui, "Pdv.exe"))) return aqui;
        // instalador dentro de uma subpasta ao lado do payload (layout do pacote)
        var irmao = Path.Combine(aqui, "pdv");
        return File.Exists(Path.Combine(irmao, "Pdv.exe")) ? irmao : null;
    }

    /// <summary>
    /// Peneira rápida sobre a origem, antes de copiar 165 MB à toa. Só o que dá para
    /// afirmar sem executar nada: existe pasta, e existe programa dentro dela.
    /// A garantia de que o programa ABRE é outra — ver <see cref="ConferirQueOProgramaAbre"/>.
    /// Devolve null quando está tudo bem.
    /// </summary>
    public static string? ConferirOrigem(string? origem)
    {
        if (string.IsNullOrWhiteSpace(origem) || !Directory.Exists(origem))
            return "A pasta do programa não veio junto com o instalador.";

        return File.Exists(Path.Combine(origem, "Pdv.exe"))
            ? null
            : "O programa (Pdv.exe) não veio junto com o instalador.";
    }

    /// <summary>Quanto esperar o programa provar que abre. Generoso de propósito: num
    /// PC de loja fraco, com antivírus lendo um exe de 179 MB pela primeira vez, a
    /// primeira abertura é MUITO mais lenta que as seguintes.</summary>
    public static readonly TimeSpan PrazoDaConferencia = TimeSpan.FromSeconds(90);

    /// <summary>
    /// A pergunta que interessa, feita do único jeito que não erra: MANDA O PROGRAMA
    /// ABRIR. `--cupom-teste` desenha o cupom de exemplo num PNG e sai — não abre a
    /// frente de caixa, não toca no banco de vendas, não emite nada. Mas para chegar
    /// até ali ele precisa subir o WPF, carregar as bibliotecas nativas e desenhar.
    ///
    /// É a diferença entre "copiei os arquivos que eu achava que eram necessários" e
    /// "o caixa abre nesta máquina". Vale contra tudo que uma lista de arquivos nunca
    /// pegaria: download corrompido, antivírus comendo uma DLL, biblioteca do sistema
    /// faltando, publish com a flag trocada.
    /// </summary>
    public static string? ConferirQueOProgramaAbre(string exe) => ConferirQueOProgramaAbre(exe, PrazoDaConferencia);

    /// <summary>A conferência com um prazo escolhido (a instalação usa <see cref="PrazoDaConferencia"/>).</summary>
    public static string? ConferirQueOProgramaAbre(string exe, TimeSpan prazo)
    {
        var png = Path.Combine(Path.GetTempPath(), "pdv-conferencia-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        try
        {
            var r = RodarComPrazo(PsiDaConferencia(exe, png), prazo);
            return AvaliarConferencia(r.Terminou, r.Codigo, r.Saida, exe);
        }
        catch (Exception ex) { return AvaliarConferencia(false, -1, ex.Message, exe); }
        finally
        {
            try { File.Delete(png); } catch { }
            try { if (Directory.Exists(PastaDaConferencia(exe))) Directory.Delete(PastaDaConferencia(exe), recursive: true); } catch { /* fica para a próxima, protegida como o exe */ }
        }
    }

    /// <summary>A variável do .NET que diz onde um exe de arquivo único extrai as bibliotecas nativas.</summary>
    public const string VariavelDaExtracao = "DOTNET_BUNDLE_EXTRACT_BASE_DIR";

    /// <summary>
    /// Onde o Pdv.exe da conferência extrai as bibliotecas nativas: AO LADO DO EXE, numa pasta só
    /// dele, com a mesma proteção do próprio exe (Program Files: só administrador grava).
    /// </summary>
    public static string PastaDaConferencia(string exe) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, ".conferencia");

    /// <summary>
    /// O processo da conferência (revisão de segurança da 1.0.36). O instalador roda como
    /// administrador e a conferência nasce elevada com ele. Sem a variável, o Pdv.exe de arquivo único
    /// extrairia e CARREGARIA as bibliotecas nativas de %TEMP%\.net, que o usuário grava: uma DLL
    /// plantada ali rodaria como administrador. Com ela, a extração vai para
    /// <see cref="PastaDaConferencia"/>, que só administrador grava, e é apagada no fim.
    /// </summary>
    public static ProcessStartInfo PsiDaConferencia(string exe, string png)
    {
        var psi = new ProcessStartInfo { FileName = exe, WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
        psi.ArgumentList.Add("--cupom-teste");
        psi.ArgumentList.Add(png);
        psi.Environment[VariavelDaExtracao] = PastaDaConferencia(exe);
        return psi;
    }

    /// <summary>O que aconteceu com um programa rodado com prazo.</summary>
    /// <param name="Matou">Passou do prazo e o instalador encerrou ESSE processo (e só ele).</param>
    public sealed record Execucao(bool Terminou, int Codigo, string Saida, bool Matou);

    /// <summary>
    /// RODA UM PROGRAMA COM PRAZO DE VERDADE (14/09/2026, loja Castelo).
    ///
    /// O instalador ficou parado em "Conferindo se o caixa abre nesta máquina". A conferência
    /// lia a saída inteira (ReadToEnd) ANTES de esperar com prazo, e ReadToEnd só volta quando
    /// o cano fecha: quando o programa sai E todo filho que herdou a saída dele também sai. Um
    /// Pdv.exe preso, ou um que abriu WebView2 ou o agente, deixava o prazo de 90 s sem valer
    /// nunca. Aqui a saída é lida em paralelo, o prazo manda, e passou dele o instalador
    /// encerra o processo que ELE abriu, pelo handle, nunca por nome: outro Pdv.exe aberto na
    /// máquina (o caixa da loja) não é da conta da conferência.
    /// </summary>
    public static Execucao RodarComPrazo(ProcessStartInfo psi, TimeSpan prazo)
    {
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var p = Process.Start(psi);
        if (p is null) return new Execucao(false, -1, "", false);

        var saida = p.StandardOutput.ReadToEndAsync();
        var erro = p.StandardError.ReadToEndAsync();
        var terminou = p.WaitForExit((int)Math.Clamp(prazo.TotalMilliseconds, 0, int.MaxValue));
        var matou = false;
        if (!terminou)
        {
            try { p.Kill(entireProcessTree: false); matou = true; } catch { /* saiu no mesmo instante */ }
            try { p.WaitForExit(5_000); } catch { }
        }

        // O cano pode continuar aberto por um filho que herdou a saída: espera pouco e segue com
        // o que chegou. O desfecho é o código de saída, não o texto.
        try { Task.WaitAll(new Task[] { saida, erro }, TimeSpan.FromSeconds(2)); } catch { }
        static string Texto(Task<string> t)
        {
            if (t.IsCompletedSuccessfully) return t.Result;
            _ = t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return "";
        }
        return new Execucao(terminou, terminou ? p.ExitCode : -1, Texto(saida) + Texto(erro), matou);
    }

    /// <summary>
    /// "versão 1.0.10" para a tela do instalador, a partir da FileVersion do CAIXA. Desconhecida
    /// = texto vazio. Nunca a versão do próprio instalador: foi assim que a Castelo leu
    /// "versão 2.0.0.0" (14/09/2026).
    /// </summary>
    public static string RotuloVersao(string? versao)
    {
        var v = (versao ?? "").Trim();
        if (v.Length == 0) return "";
        var partes = v.Split('.');
        if (partes.Length == 4 && partes[3] == "0") v = string.Join('.', partes[..3]);
        return "versão " + v;
    }

    /// <summary>Há algum Pdv.exe aberto nesta máquina?</summary>
    public static bool CaixaAberto()
    {
        var abertos = Process.GetProcessesByName("Pdv");
        foreach (var p in abertos) p.Dispose();
        return abertos.Length > 0;
    }

    /// <summary>
    /// A TROCA ESPERA O CAIXA ANTERIOR SAIR DE VERDADE (1.0.35, Savassi, 14/10/2026 21:21). O caixa
    /// 1.0.34 abriu com o navegador do chat da 1.0.32 ainda vivo no perfil e, como a 1.0.33 mudou as
    /// opções do navegador, o WebView2 recusou o chat e o WhatsApp até o PC reiniciar. Antes a troca
    /// esperava só o Pdv.exe, e em silêncio: passados 30 s seguia sem dizer nada. Agora:
    ///  · espera o caixa até <paramref name="prazoDoCaixa"/> e diz se ele fechou ou não;
    ///  · com o caixa FECHADO, espera os navegadores do WebView2 que o caixa abriu
    ///    (<paramref name="exeDoCaixa"/>) com a pasta de dados em <paramref name="pastaDados"/> (webview e
    ///    webview-whatsapp) até <paramref name="prazoDoNavegador"/> e encerra os que ficarem. Nenhum outro
    ///    navegador da máquina é tocado (ver <see cref="NavegadorDoPerfil.EncerrarSobras"/>);
    ///  · com o caixa ainda ABERTO (ou sem saber), não encerra navegador nenhum, só anota quais ficam
    ///    (fechamento da 1.0.36). O caixa aberto segue vendendo: é o caso da espera que reabre o caixa que
    ///    não subiu (Telas/AtualizarCaixa, fechar = false) e do caixa lento para sair. Encerrar ali derrubava
    ///    o chat e o WhatsApp de um caixa vivo no meio do turno. O caixa novo cuida do perfil quando abrir
    ///    (Telas/HospedeWebView2: órfão encerrado, dono vivo ele entra junto).
    /// Devolve as linhas para <see cref="AnotarTroca"/>. Nunca lança.
    /// </summary>
    public static IReadOnlyList<string> EsperarOCaixaAnteriorSair(Func<bool> caixaAberto, TimeSpan prazoDoCaixa,
        string pastaDados, TimeSpan prazoDoNavegador, string exeDoCaixa = "Pdv.exe")
    {
        var linhas = new List<string>();
        // Na dúvida, aberto: encerrar o navegador de um caixa vivo é pior do que deixar um navegador
        // velho para o caixa novo resolver.
        var aberto = true;
        try
        {
            var relogio = Stopwatch.StartNew();
            aberto = caixaAberto();
            while (aberto && relogio.Elapsed < prazoDoCaixa)
            {
                Thread.Sleep(500);
                aberto = caixaAberto();
            }
            linhas.Add(aberto
                ? $"o caixa anterior não fechou em {prazoDoCaixa.TotalSeconds:0} s; a troca segue com ele aberto"
                : $"o caixa anterior fechou (esperei {relogio.Elapsed.TotalSeconds:0.0} s)");
        }
        catch (Exception ex) { aberto = true; linhas.Add("a espera pelo caixa anterior falhou: " + ex.GetType().Name); }
        linhas.Add(aberto
            ? NavegadorDoPerfil.DescreverSobras(pastaDados, exeDoCaixa)
            : NavegadorDoPerfil.EncerrarSobras(pastaDados, prazoDoNavegador, exeDoCaixa));
        return linhas;
    }

    /// <summary>Uma linha datada em ProgramData\PdvNativo\atualizacao.txt (a troca de versão). Nunca atrapalha.</summary>
    public static void AnotarTroca(string texto)
    {
        try
        {
            Directory.CreateDirectory(PastaDados);
            File.AppendAllText(Path.Combine(PastaDados, "atualizacao.txt"),
                $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}  {texto}{Environment.NewLine}");
        }
        catch { /* diagnóstico nunca atrapalha */ }
    }

    /// <summary>Como o instalador abre o caixa no fim.</summary>
    public enum ComoAbrir { Direto, PeloExplorer, NaoAbre }

    /// <summary>
    /// ABRIR O CAIXA SEM HERDAR O ADMINISTRADOR (14/09/2026, Castelo; fechado de vez na 1.0.36). O
    /// instalador roda elevado, e tudo que ele abre direto nasce elevado também. Um caixa elevado cria
    /// a trava com a DACL de administrador, passa o turno inteiro como administrador (com o WebView2
    /// do chat junto) e carrega as bibliotecas que extrai numa pasta que o usuário grava (%TEMP%\.net).
    /// Por isso, elevado, o instalador NUNCA abre o Pdv.exe direto: com a área de trabalho aberta,
    /// quem abre é o Explorer da sessão, como o usuário comum; sem ela (quiosque), não abre, e quem
    /// chamou reabre (o PDV deixa um cmd comum esperando, Nucleo/ReabrirOCaixa) ou a pessoa reinicia.
    /// Sem administrador não há o que tirar: abre direto.
    /// </summary>
    public static ComoAbrir DecidirComoAbrir(bool elevado, bool explorerAberto)
        => !elevado ? ComoAbrir.Direto : explorerAberto ? ComoAbrir.PeloExplorer : ComoAbrir.NaoAbre;

    /// <summary>O Explorer pelo caminho absoluto da pasta do Windows (nunca pela busca do Windows, que começa na pasta do instalador).</summary>
    public static string CaminhoDoExplorer() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>
    /// A pergunta da atualização sem a área de trabalho para reabrir o caixa (quiosque com um PDV antigo
    /// chamando). Fechamento da 1.0.36: era só "Reinicie o computador", e depois do OK a tela ficava preta,
    /// sem menu Iniciar e sem dizer como reiniciar. Agora o Sim reinicia (<see cref="Reiniciar"/>).
    /// </summary>
    public const string ReinicieParaAbrir = "O caixa foi atualizado. Reiniciar o computador agora para abrir o caixa?";

    /// <summary>A mesma pergunta depois de uma atualização que não terminou (o erro já foi dito antes).</summary>
    public const string ReinicieDepoisDaFalha = "Reiniciar o computador agora para abrir o caixa?";

    /// <summary>Quando a pessoa diz Não, ou o reinício não sai: como reiniciar sem a área de trabalho.</summary>
    public const string ComoReiniciar = "Para abrir o caixa, reinicie o computador: Ctrl+Alt+Del, botão de energia, Reiniciar.";

    /// <summary>O reinício do Windows: shutdown.exe de System32 (nunca pelo nome), reiniciar já.</summary>
    public static ProcessStartInfo PsiDoReinicio()
    {
        var psi = new ProcessStartInfo(ProgramaDoSistema("shutdown.exe")) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("/r");
        psi.ArgumentList.Add("/t");
        psi.ArgumentList.Add("0");
        return psi;
    }

    /// <summary>Pede ao Windows para reiniciar agora. True quando o pedido foi aceito. Nunca lança.</summary>
    public static bool Reiniciar()
    {
        try
        {
            using var p = Process.Start(PsiDoReinicio());
            if (p is null || !p.WaitForExit(15_000)) return false;
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// A frase da atualização que não terminou (fechamento da 1.0.36). "O caixa continua na versão
    /// anterior" só é verdade antes da cópia: depois de mexer no programa, a falha já diz o que houve
    /// ("O caixa foi copiado, mas...").
    /// </summary>
    public static string FraseDaFalha(string falha, bool mexeuNoPrograma)
        => "Não consegui atualizar: " + falha + (mexeuNoPrograma ? "" : "\nO caixa continua na versão anterior.");

    // ── A EXTRAÇÃO NUMA PASTA SÓ DE ADMINISTRADOR (fechamento da 1.0.36, revisão de segurança) ─────
    //
    // O instalador roda elevado. Ele extraía os 265 MB do pacote no TEMP do usuário, pasta que qualquer
    // programa do usuário grava, e depois copiava dali para Program Files e rodava a conferência como
    // administrador: entre a extração e a cópia, um Pdv.exe ou uma DLL trocados ali iam parar em
    // Program Files e rodavam elevados. Agora a extração vai para dentro da pasta do caixa (Program
    // Files: só administrador grava, e só ele renomeia o que está lá dentro), com nome único, e sai
    // no fim. A mesma classe do %TEMP%\.net da conferência e do VBScript do atalho.

    /// <summary>O começo do nome das pastas de extração dentro da pasta do caixa.</summary>
    public const string PrefixoDaExtracao = ".extracao-";

    /// <summary>Onde extrair o pacote: dentro da pasta do caixa, com nome único.</summary>
    public static string PastaDeExtracao(string pastaDoCaixa)
        => Path.Combine(Path.GetFullPath(pastaDoCaixa), PrefixoDaExtracao + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Apaga extrações que sobraram de uma troca interrompida. Só as pastas com o prefixo. Nunca lança.</summary>
    public static void LimparExtracoesAntigas(string pastaDoCaixa)
    {
        try
        {
            if (!Directory.Exists(pastaDoCaixa)) return;
            foreach (var d in Directory.GetDirectories(pastaDoCaixa, PrefixoDaExtracao + "*"))
                try { Directory.Delete(d, recursive: true); } catch { /* em uso: na próxima */ }
        }
        catch { }
    }

    /// <summary>A frase do fim da instalação quando o caixa não abriu sem administrador.</summary>
    public const string AbraPeloAtalho = "O caixa foi instalado. Abra pelo atalho MMFood na área de trabalho ou reinicie o computador.";

    /// <summary>
    /// Abre o caixa (fim da instalação, fim da atualização) sem administrador. Devolve true quando o
    /// caixa apareceu (ou foi aberto direto, sem administrador); false quando não deu para abrir sem
    /// elevar: quem chamou diz para abrir pelo atalho ou reiniciar. Nunca lança.
    /// </summary>
    public static bool AbrirCaixa(string exe)
    {
        try
        {
            using var eu = Process.GetCurrentProcess();
            bool elevado;
            using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                elevado = new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            var explorer = false;
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try { explorer |= p.SessionId == eu.SessionId; } catch { }
                finally { p.Dispose(); }
            }

            switch (DecidirComoAbrir(elevado, explorer))
            {
                case ComoAbrir.Direto:
                    using (Process.Start(new ProcessStartInfo { FileName = exe, WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = true })) { }
                    return true;
                case ComoAbrir.PeloExplorer:
                    var antes = IdsDoCaixa();
                    var psi = new ProcessStartInfo(CaminhoDoExplorer()) { UseShellExecute = false };
                    psi.ArgumentList.Add(exe);
                    using (Process.Start(psi)) { }
                    // O Explorer não devolve erro quando não abre: confere se o caixa apareceu. Não
                    // apareceu: NÃO cai para o direto (seria o caixa como administrador).
                    for (var i = 0; i < 60; i++)
                    {
                        Thread.Sleep(250);
                        if (IdsDoCaixa().Except(antes).Any()) return true;
                    }
                    return false;
                default:
                    return false;
            }
        }
        catch { return false; /* o atalho e o menu do Windows continuam lá */ }
    }

    private static HashSet<int> IdsDoCaixa()
    {
        var ids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("Pdv"))
        {
            ids.Add(p.Id);
            p.Dispose();
        }
        return ids;
    }

    /// <summary>
    /// A DECISÃO da conferência, separada da execução para poder ser testada: o
    /// caminho que mais importa (o programa não abre) é justamente o que não dá para
    /// ensaiar com o programa de verdade, porque ele abre.
    /// </summary>
    public static string? AvaliarConferencia(bool terminou, int codigo, string saida, string exe)
    {
        if (terminou && codigo == 0) return null;

        // Se as bibliotecas nativas eram para estar soltas e sumiram, isso é o que o
        // operador precisa ouvir — mais útil do que repetir a exceção do .NET.
        var pasta = Path.GetDirectoryName(exe) ?? "";
        var achadas = BibliotecasNativas.Count(n => File.Exists(Path.Combine(pasta, n)));
        var pista = achadas > 0 && achadas < BibliotecasNativas.Length
            ? $" Faltam {BibliotecasNativas.Length - achadas} bibliotecas ao lado do programa."
            : "";

        return terminou
            ? $"O caixa foi copiado, mas não abriu nesta máquina.{pista} "
            + "Baixe o instalador de novo. Se repetir, o antivírus pode estar bloqueando o programa."
            : "O caixa foi copiado, mas não respondeu ao teste de abertura. "
            + "Reinicie a máquina e instale de novo.";
    }

    /// <summary>
    /// Tudo que vai ser copiado, em caminho RELATIVO à origem (inclui subpastas —
    /// o publish cria runtimes\ e o WebView2 mora lá). Os restos de atualizações
    /// anteriores (*.velho) ficam de fora: eles não são o programa.
    /// </summary>
    public static IReadOnlyList<string> ArquivosParaCopiar(string origem) =>
        Directory.Exists(origem)
            ? Directory.GetFiles(origem, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(origem, f))
                .Where(r => !r.EndsWith(SufixoVelho, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : Array.Empty<string>();

    /// <summary>
    /// Os arquivos do AGENTE FISCAL, relativos à origem. Mesma regra do PDV (ver
    /// <see cref="ArquivosParaCopiar"/>), e por isso é a mesma implementação.
    ///
    /// POR QUE O AGENTE ENTRA NO INSTALADOR (08/09/2026, pedido do dono). Ele é quem
    /// emite nota com a internet caída, e até hoje ninguém o instalava: o caixa só tinha
    /// contingência se alguém tivesse copiado a pasta na mão. Loja sem ele, sem internet,
    /// vende sem nota nenhuma.
    /// </summary>
    public static IReadOnlyList<string> ArquivosDoAgente(string origem) => ArquivosParaCopiar(origem);

    /// <summary>
    /// A origem do agente é o que parece? Gêmeo de <see cref="ConferirOrigem"/>, e pelo
    /// mesmo motivo: apontar a pasta errada no build gera um instalador que só falha na
    /// loja, com a internet já caída, que é o pior lugar do mundo para descobrir isso.
    ///
    /// As três provas são o esqueleto do agente: quem o inicia, o motor da nota e o
    /// runtime, porque o caixa roda "node.exe pdv-agent.cjs" de dentro da própria pasta.
    /// </summary>
    public static string? ConferirOrigemAgente(string? origem)
    {
        if (string.IsNullOrWhiteSpace(origem) || !Directory.Exists(origem))
            return "A pasta do agente fiscal não existe: " + (origem ?? "(vazio)");
        foreach (var (rel, oQueE) in new[]
                 {
                     ("pdv-agent.cjs", "quem inicia o agente"),
                     (Path.Combine("nfce", "xml.cjs"), "o motor da nota"),
                     ("node.exe", "o runtime"),
                 })
            if (!File.Exists(Path.Combine(origem, rel)))
                return $"Falta {rel} ({oQueE}) na pasta do agente fiscal.";
        return null;
    }

    public sealed record Opcoes(
        string OrigemPasta,
        string PastaDestino,
        bool IniciarComWindows,
        bool AtalhoAreaTrabalho,
        bool AjustarAcl = true,
        bool GravarRegistro = true,
        // Ligada na instalação de verdade; DESLIGADA na suíte. Migrar mexe no
        // registro real, na área de trabalho real e APAGA a pasta da versão
        // anterior — coisas que um teste rodando em sandbox não pode fazer na
        // máquina de quem está desenvolvendo.
        bool MigrarAntiga = true,
        // Desligada na suíte porque lá o "Pdv.exe" é um arquivo de texto: mandar
        // ABRIR o programa é o teste da instalação de verdade, não da cópia.
        bool ConferirQueAbre = true,
        // 1.0.41: o relógio do PC acertando sozinho (w32time + tarefa agendada). Na suíte o
        // executor é trocado por um falso: nada do Windows é tocado.
        bool AcertarRelogio = true);

    // ── 1.0.41: O RELÓGIO DO PC ACERTADO SOZINHO (pedido do dono, 16/10/2026) ─────────────────

    /// <summary>
    /// Roda UM programa do Windows (caminho absoluto de System32) e devolve o código de saída e a saída
    /// (até 60 s). A suíte troca por um falso que grava cada linha. Nunca lança.
    /// </summary>
    public static Func<string, string, (int Codigo, string Saida)> ExecutarPrograma = (programa, argumentos) =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(programa, argumentos)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Environment.SystemDirectory,
            });
            if (p is null) return (-1, "não abriu");
            var saida = p.StandardOutput.ReadToEndAsync();
            var erro = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60_000)) { try { p.Kill(); } catch { } return (-2, "não terminou em 60 s"); }
            p.WaitForExit();
            return (p.ExitCode, (saida.Result + " " + erro.Result).Trim());
        }
        catch (Exception ex) { return (-3, ex.GetType().Name + ": " + ex.Message); }
    };

    /// <summary>
    /// As seis etapas de <see cref="RelogioDoWindows.PassosDaInstalacao"/>, uma a uma, cada falha só
    /// anotada (a instalação segue). Devolve as linhas do diário (nome da etapa, código, começo da
    /// saída). Nunca lança.
    /// </summary>
    public static IReadOnlyList<string> AcertarRelogio(Func<string, string, (int Codigo, string Saida)>? executar = null)
    {
        var linhas = new List<string>();
        executar ??= ExecutarPrograma;
        IReadOnlyList<RelogioDoWindows.Passo> passos;
        try { passos = RelogioDoWindows.PassosDaInstalacao(Environment.SystemDirectory); }
        catch (Exception ex) { linhas.Add("relogio: pasta do sistema fora do esperado (" + ex.GetType().Name + ")"); return linhas; }
        foreach (var p in passos)
        {
            try
            {
                var (codigo, saida) = executar(p.Programa, p.Argumentos);
                var resumo = (saida ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (resumo.Length > 120) resumo = resumo[..120];
                linhas.Add($"relogio {p.Nome}: {(RelogioDoWindows.PassoDeu(p, codigo) ? "ok" : "falhou")} codigo={codigo}" + (resumo.Length > 0 ? " " + resumo : ""));
            }
            catch (Exception ex) { linhas.Add($"relogio {p.Nome}: falhou {ex.GetType().Name}"); }
        }
        return linhas;
    }

    /// <summary>Instala (ou ATUALIZA por cima — preservando os dados). Devolve
    /// null no sucesso, mensagem de erro no fracasso.</summary>
    public static string? Instalar(Opcoes o, Action<string>? progresso = null) => Instalar(o, progresso, out _);

    /// <summary>
    /// Instala e diz se chegou a mexer no programa (<paramref name="mexeuNoPrograma"/>: a cópia começou).
    /// Com a falha antes disso o caixa continua na versão anterior; depois, não dá para dizer isso.
    /// </summary>
    public static string? Instalar(Opcoes o, Action<string>? progresso, out bool mexeuNoPrograma)
    {
        mexeuNoPrograma = false;
        if (ConferirOrigem(o.OrigemPasta) is { } incompleta) return incompleta;

        try
        {
            progresso?.Invoke("Preparando as pastas…");
            Directory.CreateDirectory(o.PastaDestino);
            // A pasta de dados nasce aqui só porque o icacls logo abaixo precisa dela
            // existindo. Sem o ajuste de ACL, quem cria é o próprio PDV na 1ª abertura.
            if (o.AjustarAcl) Directory.CreateDirectory(PastaDados);
            LimparVelhos(o.PastaDestino);

            var arquivos = ArquivosParaCopiar(o.OrigemPasta);
            var total = Math.Max(arquivos.Count, 1);
            var feitos = 0;
            mexeuNoPrograma = arquivos.Count > 0;
            foreach (var rel in arquivos)
            {
                // O Pdv.exe sozinho tem 156 MB e é 95% do tempo: o número que anda
                // na tela é o de arquivos, mas a primeira frase fala do tamanho —
                // senão a contagem fica parada num "2 de 14" que parece travamento.
                progresso?.Invoke(feitos == 0
                    ? "Copiando o programa (são 165 MB, leva cerca de um minuto)…"
                    : $"Copiando o programa… {feitos} de {total}");

                var erro = CopiarUm(Path.Combine(o.OrigemPasta, rel), Path.Combine(o.PastaDestino, rel));
                if (erro is not null) return erro;
                feitos++;
            }

            // instalador junto ao programa: é ele que sabe desinstalar
            var euMesmo = Environment.ProcessPath;
            if (euMesmo is not null && !euMesmo.StartsWith(o.PastaDestino, StringComparison.OrdinalIgnoreCase))
                CopiarDesinstalador(euMesmo, Path.Combine(o.PastaDestino, "InstalarPdv.exe"));

            if (o.AjustarAcl)
            {
                progresso?.Invoke("Liberando a pasta de dados para os operadores…");
                // Usuários (S-1-5-32-545) modificam: qualquer conta do caixa grava no
                // banco. Sem isso o 2º usuário do Windows trava em "readonly database".
                // Caminho absoluto de System32: pelo nome, o Windows procura PRIMEIRO na pasta do
                // instalador (na atualização, o TEMP do usuário) e um icacls.exe plantado ali rodaria
                // como administrador (revisão de segurança da 1.0.36).
                var icacls = Process.Start(new ProcessStartInfo
                {
                    FileName = ProgramaDoSistema("icacls.exe"),
                    Arguments = $"\"{PastaDados}\" /grant *S-1-5-32-545:(OI)(CI)M /T",
                    UseShellExecute = false, CreateNoWindow = true,
                });
                icacls?.WaitForExit(30_000);
            }

            var destinoExe = Path.Combine(o.PastaDestino, "Pdv.exe");

            if (o.ConferirQueAbre)
            {
                progresso?.Invoke("Conferindo se o caixa abre nesta máquina…");
                if (ConferirQueOProgramaAbre(destinoExe) is { } naoAbre) return naoAbre;
            }

            if (o.GravarRegistro)
            {
                progresso?.Invoke("Registrando o programa no Windows…");
                var versao = FileVersionInfo.GetVersionInfo(destinoExe).FileVersion ?? "?";
                using (var k = Registry.LocalMachine.CreateSubKey(ChaveUninstall))
                {
                    k.SetValue("DisplayName", NomePrograma);
                    k.SetValue("DisplayVersion", versao);
                    k.SetValue("Publisher", Fabricante);
                    k.SetValue("InstallLocation", o.PastaDestino);
                    k.SetValue("DisplayIcon", destinoExe);
                    k.SetValue("UninstallString", $"\"{Path.Combine(o.PastaDestino, "InstalarPdv.exe")}\" --desinstalar");
                    k.SetValue("NoModify", 1); k.SetValue("NoRepair", 1);
                }

                using var run = Registry.LocalMachine.CreateSubKey(ChaveRun);
                if (o.IniciarComWindows)
                    run.SetValue(NomePrograma, $"\"{destinoExe}\"");
                else
                    run.DeleteValue(NomePrograma, throwOnMissingValue: false);
            }

            if (o.AtalhoAreaTrabalho)
            {
                progresso?.Invoke("Criando o atalho na área de trabalho…");
                CriarAtalho(Path.Combine(AreaDeTrabalho, NomePrograma + ".lnk"), destinoExe);
            }

            // 1.0.41 (pedido do dono): o relógio do PC acertando sozinho, em toda instalação e
            // atualização. Falha não aborta: só vai para o diário (atualizacao.txt).
            if (o.AcertarRelogio)
            {
                progresso?.Invoke("Acertando o relógio do Windows…");
                foreach (var linha in AcertarRelogio()) AnotarTroca(linha);
            }

            if (o.MigrarAntiga)
            {
                progresso?.Invoke("Limpando a versão anterior…");
                MigrarInstalacaoAntiga(o.PastaDestino);
            }

            progresso?.Invoke("Concluído.");
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Copia um arquivo por cima, mesmo com o PDV aberto. O Windows deixa
    /// RENOMEAR um exe/dll em uso, mas não sobrescrever — então o que está no
    /// caminho sai de lado como *.velho e some na instalação seguinte.
    /// </summary>
    /// <summary>Onde o agente fiscal fica instalado: ao lado do caixa, na mesma árvore.</summary>
    public static string PastaDoAgente(string pastaDestino) => Path.Combine(pastaDestino, "agent");

    /// <summary>
    /// Copia o agente fiscal para <c>&lt;destino&gt;\agent</c>.
    ///
    /// ⚠️ PARA O AGENTE ANTIGO ANTES DE COPIAR, e isto não é zelo: se o processo velho
    /// continuar vivo, o <c>node.exe</c> só é RENOMEADO para .velho (o Windows deixa
    /// renomear exe carregado), o velho segue de pé segurando a porta 4610, e na próxima
    /// vez que o caixa perguntar "o agente está aí?" quem responde é ele. O caixa acha
    /// que está tudo certo e a loja roda a versão antiga até alguém reiniciar a máquina,
    /// sem nenhum aviso. É o pior jeito de isto falhar.
    ///
    /// Mira estreita de propósito: só mata node que esteja rodando DE DENTRO desta pasta.
    /// O node de quem desenvolve na mesma máquina não é da nossa conta.
    ///
    /// Nunca encosta nos DADOS do agente (fila de notas assinadas, XMLs guardados): eles
    /// moram em ProgramData, fora desta árvore, pela mesma regra que protege o banco de
    /// vendas.
    /// </summary>
    public static string? InstalarAgente(string origem, string pastaDestino, Action<string>? progresso = null)
    {
        if (ConferirOrigemAgente(origem) is { } ruim) return ruim;
        var alvo = PastaDoAgente(pastaDestino);
        PararAgente(alvo);

        var arquivos = ArquivosDoAgente(origem);
        var n = 0;
        foreach (var rel in arquivos)
        {
            if (CopiarUm(Path.Combine(origem, rel), Path.Combine(alvo, rel)) is { } erro) return erro;
            if (++n % 50 == 0) progresso?.Invoke($"Instalando o agente fiscal… {n} de {arquivos.Count}");
        }
        LimparVelhos(alvo);
        return null;
    }

    /// <summary>Mata o agente que estiver rodando DE DENTRO de <paramref name="pasta"/>. Silencioso.</summary>
    public static void PararAgente(string pasta)
    {
        string raiz;
        try { raiz = Path.GetFullPath(pasta); } catch { return; }
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("node"))
        {
            try
            {
                var exe = p.MainModule?.FileName;
                if (exe is not null && exe.StartsWith(raiz, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
            }
            catch { /* processo de outro usuário, ou já morreu: não é da nossa conta */ }
            finally { p.Dispose(); }
        }
    }

    private static string? CopiarUm(string origem, string destino)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
            if (File.Exists(destino) && !TentarApagar(destino))
            {
                // Em uso. O Windows recusa APAGAR um exe/dll carregado, mas aceita
                // RENOMEAR: a imagem mapeada em memória continua válida com outro
                // nome. É esta brecha — e só ela — que deixa atualizar o caixa sem
                // pedir para o operador fechar no meio do movimento.
                var velho = destino + SufixoVelho;
                TentarApagar(velho);
                File.Move(destino, velho);
            }
            File.Copy(origem, destino, overwrite: false);
            return null;
        }
        catch (IOException)
        {
            return $"O arquivo {Path.GetFileName(destino)} está em uso. " +
                   "Feche o PDV nesta máquina e instale de novo.";
        }
        catch (UnauthorizedAccessException)
        {
            return "O Windows não deixou gravar em " + Path.GetDirectoryName(destino) +
                   ". Abra o instalador com o botão direito → Executar como administrador.";
        }
    }

    /// <summary>
    /// Deixa uma cópia deste exe ao lado do programa — é ela que o Adicionar/Remover
    /// Programas chama para desinstalar.
    ///
    /// ⚠️ SEM A CAUDA. O instalador empacotado carrega ~265 MB de payload no fim do
    /// arquivo; copiá-lo inteiro deixaria 236 MB parados em Program Files só para
    /// servir de desinstalador — mais que o próprio caixa, que tem 165 MB. Desinstalar
    /// não precisa de payload nenhum. E tem um segundo motivo, menos óbvio e mais
    /// importante: uma cópia COM payload é um instalador da versão de hoje esquecido na
    /// máquina — daqui a um ano alguém clica nele e reinstala uma versão velha por cima
    /// da nova, sem nenhum aviso. Cortar a cauda torna isso impossível.
    /// </summary>
    private static void CopiarDesinstalador(string origem, string destino)
    {
        try
        {
            using var entrada = new FileStream(origem, FileMode.Open, FileAccess.Read, FileShare.Read);
            var ateOnde = Pacote.LerTrailer(entrada)?.Offset ?? entrada.Length;

            if (File.Exists(destino) && !TentarApagar(destino))
                File.Move(destino, destino + SufixoVelho);

            entrada.Position = 0;
            using var saida = new FileStream(destino, FileMode.Create, FileAccess.Write);
            var buf = new byte[81920];
            long faltam = ateOnde;
            while (faltam > 0)
            {
                var lidos = entrada.Read(buf, 0, (int)Math.Min(buf.Length, faltam));
                if (lidos <= 0) break;
                saida.Write(buf, 0, lidos);
                faltam -= lidos;
            }
        }
        catch { /* sem desinstalador em Program Files o programa ainda funciona;
                   o Adicionar/Remover é que fica sem o botão */ }
    }

    /// <summary>
    /// Apagar sem drama. Devolve false quando o arquivo continua lá.
    ///
    /// ⚠️ As DUAS exceções importam. Apagar um exe/dll CARREGADO devolve
    /// ERROR_ACCESS_DENIED, que o .NET entrega como UnauthorizedAccessException —
    /// não como IOException. Tratar só IOException fazia a atualização estourar em
    /// vez de cair no renomeio, ou seja: quebrava exatamente no caso que o renomeio
    /// existe para resolver (atualizar com o caixa aberto).
    /// </summary>
    private static bool TentarApagar(string caminho)
    {
        if (!File.Exists(caminho)) return true;
        try { File.Delete(caminho); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Restos de atualizações anteriores. Somem agora que o PDV está fechado.</summary>
    private static void LimparVelhos(string pasta)
    {
        if (!Directory.Exists(pasta)) return;
        foreach (var f in Directory.GetFiles(pasta, "*" + SufixoVelho, SearchOption.AllDirectories))
            try { File.Delete(f); } catch { /* ainda em uso: na próxima */ }
    }

    /// <summary>
    /// A versão anterior se chamava diferente, logo instalava em OUTRA pasta e
    /// registrava OUTRA entrada. Sem isto a máquina fica com dois programas, dois
    /// atalhos e duas linhas no Adicionar/Remover Programas — e o Windows pode
    /// abrir o antigo no boot. Os dados não estão em nenhuma das duas pastas:
    /// moram no ProgramData e ficam onde estão.
    /// </summary>
    public static void MigrarInstalacaoAntiga(string pastaNova)
    {
        // Fechamento da 1.0.36: um Shell de quiosque que aponta para dentro de uma pasta antiga segura a
        // pasta (apagar seria a tela preta no próximo boot). Sem conseguir ler os perfis, nenhuma sai.
        IReadOnlyList<string>? shells;
        try { shells = QuiosqueDoCaixa.ExesNosShells(Registry.Users); }
        catch { shells = null; }

        try
        {
            using (var run = Registry.LocalMachine.CreateSubKey(ChaveRun))
                foreach (var nome in NomesAntigos)
                    run.DeleteValue(nome, throwOnMissingValue: false);
            Registry.LocalMachine.DeleteSubKeyTree(ChaveUninstallAntiga, throwOnMissingSubKey: false);
        }
        catch { /* sem permissão no registro: o resto da migração ainda vale */ }

        foreach (var nome in NomesAntigos)
        {
            var atalhoAntigo = Path.Combine(AreaDeTrabalho, nome + ".lnk");
            try { if (File.Exists(atalhoAntigo)) File.Delete(atalhoAntigo); } catch { }
        }

        // Só apaga a pasta antiga quando a instalação FOI para outra: atualizar no
        // lugar (loja que já tem o caixa em PDV MMTech) passa por aqui, e apagar
        // seria apagar o programa que acabou de ser copiado.
        foreach (var nome in NomesAntigos)
        {
            var antiga = EmProgramFiles(nome);
            if (Directory.Exists(antiga) &&
                !antiga.Equals(pastaNova, StringComparison.OrdinalIgnoreCase) &&
                PastaAntigaPodeSair(antiga, shells))
                try { Directory.Delete(antiga, recursive: true); } catch { /* em uso: fica o esqueleto */ }
        }
    }

    /// <summary>
    /// A pasta antiga pode sair? Não, quando o Shell de quiosque de algum perfil aponta para um exe dentro
    /// dela, ou quando não deu para ler os perfis (<paramref name="exesNosShells"/> null). Pura.
    /// </summary>
    public static bool PastaAntigaPodeSair(string pasta, IReadOnlyList<string>? exesNosShells)
    {
        if (exesNosShells is null) return false;
        string raiz;
        try { raiz = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pasta)) + Path.DirectorySeparatorChar; }
        catch { return false; }
        foreach (var exe in exesNosShells)
        {
            try { if (Path.GetFullPath(exe).StartsWith(raiz, StringComparison.OrdinalIgnoreCase)) return false; }
            catch { return false; }
        }
        return true;
    }

    private static string AreaDeTrabalho =>
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    /// <summary>Remove programa, atalho e registro. Os DADOS ficam — sempre.</summary>
    public static string? Desinstalar() => Desinstalar(out _);

    /// <summary>
    /// Remove programa, atalho e registro, e ANTES desfaz o quiosque do usuário do caixa (1.0.36):
    /// <paramref name="quiosque"/> diz em quantos perfis do Windows o Shell do PDV e a trava do
    /// Ctrl+Alt+Del saíram, e em quantos algo ficou. Os DADOS ficam, sempre.
    /// </summary>
    public static string? Desinstalar(out DesfazerQuiosque.Resumo quiosque)
    {
        // PRIMEIRO o quiosque: depois que o Pdv.exe sai, o Shell do usuário do caixa apontaria para
        // um arquivo que não existe (tela preta na próxima entrada, com a trava do Ctrl+Alt+Del só
        // com Cancelar). E antes de apagar a chave de desinstalação, que é onde PastaInstalada acha
        // o InstallLocation. Um Shell nosso que NÃO saiu segura a desinstalação inteira (nada foi
        // apagado ainda): apagar o Pdv.exe agora seria a tela preta, e este desinstalador se apaga
        // junto, sem como rodar de novo. Erro ao percorrer os perfis não segura (não há o que provar).
        quiosque = DesfazerQuiosqueDoCaixa();
        if (DesfazerQuiosque.ImpedeDesinstalar(quiosque)) return DesfazerQuiosque.MotivoShellFicou;
        try
        {
            using (var run = Registry.LocalMachine.CreateSubKey(ChaveRun))
                foreach (var nome in new[] { NomePrograma }.Concat(NomesAntigos))
                    run.DeleteValue(nome, throwOnMissingValue: false);
            Registry.LocalMachine.DeleteSubKeyTree(ChaveUninstall, throwOnMissingSubKey: false);
            Registry.LocalMachine.DeleteSubKeyTree(ChaveUninstallAntiga, throwOnMissingSubKey: false);

            foreach (var nome in new[] { NomePrograma }.Concat(NomesAntigos))
            {
                var atalho = Path.Combine(AreaDeTrabalho, nome + ".lnk");
                if (File.Exists(atalho)) File.Delete(atalho);
            }

            foreach (var pasta in new[] { PastaDestinoPadrao }.Concat(NomesAntigos.Select(EmProgramFiles)))
                if (Directory.Exists(pasta))
                    // o próprio desinstalador mora aí: apaga o que der agora; o que
                    // estiver em uso fica como esqueleto — os dados seguem intactos
                    try { Directory.Delete(pasta, recursive: true); } catch { }

            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// Os Pdv.exe que este desinstalador tira da máquina: a pasta onde o caixa está (inclusive o
    /// InstallLocation gravado), a pasta do nome de hoje e as dos nomes antigos.
    /// </summary>
    public static IReadOnlyList<string> ExesDoCaixa()
    {
        string? instalada = null;
        try { instalada = PastaInstalada(); } catch { /* sem leitura do registro: ficam as pastas conhecidas */ }
        return new[] { instalada, PastaDestinoPadrao }.Concat(NomesAntigos.Select(EmProgramFiles))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.Combine(p!, "Pdv.exe"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Desfaz o quiosque nos perfis carregados em HKEY_USERS onde há algo nosso: o Shell do PDV que
    /// está saindo, a trava com a marca do PDV (regra em Pdv.Nucleo/DesfazerQuiosque, registro em
    /// QuiosqueDoCaixa). Devolve o resumo. Nunca lança.
    /// </summary>
    public static DesfazerQuiosque.Resumo DesfazerQuiosqueDoCaixa()
    {
        try
        {
            var exes = ExesDoCaixa();
            var feitos = QuiosqueDoCaixa.DesfazerNosPerfis(Registry.Users, exes, File.Exists).ToList();
            // Fechamento da 1.0.36: o usuário do caixa que não está logado (o suporte desinstala com outra
            // conta de administrador) também tem o quiosque desfeito, pelo NTUSER.DAT dele.
            try { feitos.AddRange(QuiosqueDoCaixa.DesfazerNosPerfisFora(PerfisDoWindows(), Registry.Users.GetSubKeyNames(), exes, File.Exists)); }
            catch { /* sem a lista de perfis: ficam os carregados */ }
            return QuiosqueDoCaixa.Resumir(feitos);
        }
        catch { return DesfazerQuiosque.Resumo.Nenhum; }
    }

    private const string ChaveDosPerfis = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    /// <summary>
    /// Os perfis do Windows desta máquina (ProfileList, só leitura): o SID e o NTUSER.DAT de cada um.
    /// Nunca lança.
    /// </summary>
    public static IReadOnlyList<(string Sid, string Hive)> PerfisDoWindows()
    {
        var perfis = new List<(string, string)>();
        try
        {
            using var lista = Registry.LocalMachine.OpenSubKey(ChaveDosPerfis);
            if (lista is null) return perfis;
            foreach (var sid in lista.GetSubKeyNames())
            {
                try
                {
                    using var k = lista.OpenSubKey(sid);
                    if (k?.GetValue("ProfileImagePath") as string is { Length: > 0 } pasta)
                        perfis.Add((sid, Path.Combine(pasta, "NTUSER.DAT")));
                }
                catch { /* um perfil ilegível não segura os outros */ }
            }
        }
        catch { }
        return perfis;
    }

    // ── APAGAR TAMBÉM OS DADOS DESTE CAIXA (14/09/2026, loja Castelo) ─────────────
    //
    // O dono desinstalou e instalou de novo, e o caixa "já começou logado". Não era defeito da
    // instalação: desinstalar nunca toca em C:\ProgramData\PdvNativo (regra de ouro 1), então o
    // banco, o pareamento e o login voltaram junto. Isso continua sendo o PADRÃO. A opção existe,
    // desmarcada, para quem quer começar do zero, e mesmo ela não apaga nada em silêncio: avisa o
    // que ainda não subiu para o painel e tira a pasta do lugar, guardada com a data.

    // ── APAGAR OS DADOS SÓ COM O USUÁRIO MASTER (15/09/2026) ─────────────────────────────
    //
    // Apagar os dados é ação de admin (decisão do dono de 14/09). O caixa guarda o usuário master da
    // rede no config do banco (Pdv.Nucleo/UsuarioMaster); se ele existe, o instalador pede a senha
    // antes de tirar a pasta do lugar. Sem master no banco (instalação que nunca pegou o master, ou
    // banco ilegível) não há o que conferir: quem tem o administrador do Windows já tira a pasta na
    // mão, e o que esta porta fecha é o caminho de quem só tem o caixa na frente.

    /// <summary>O usuário master guardado no banco do caixa: só nome, hash e sal.</summary>
    public sealed record MasterDoCaixa(string Nome, string Hash, string Salt);

    /// <summary>Lê o master do banco do caixa (somente leitura, sem pool). Null = sem master ou sem banco legível.</summary>
    public static MasterDoCaixa? LerMaster(string pastaDados)
    {
        var db = Path.Combine(pastaDados, "pdv.db");
        if (!File.Exists(db)) return null;
        try
        {
            var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = db,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            using var cx = new Microsoft.Data.Sqlite.SqliteConnection(cs);
            cx.Open();
            string? Ler(string chave)
            {
                using var cmd = cx.CreateCommand();
                cmd.CommandText = "SELECT valor FROM config WHERE chave = $c";
                cmd.Parameters.AddWithValue("$c", chave);
                return cmd.ExecuteScalar() as string;
            }
            var hash = Ler("master_hash");
            var salt = Ler("master_salt");
            if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(salt)) return null;
            return new MasterDoCaixa(Ler("master_nome") ?? "", hash, salt);
        }
        catch { return null; }
    }

    /// <summary>Sem master guardado, libera. Com master, só a senha dele.</summary>
    public static bool MasterLibera(MasterDoCaixa? master, string? senha)
        => master is null || Pdv.Nucleo.HashDeSenha.Confere((senha ?? "").Trim(), master.Hash, master.Salt);

    /// <summary>O que a pasta de dados tem que importa antes de tirá-la do lugar.</summary>
    /// <param name="NaFila">Registros (vendas, turnos, movimentos) que ainda não subiram para o painel.</param>
    /// <param name="Erro">Não deu para ler o banco: ninguém sabe se há o que perder.</param>
    public sealed record DadosDoCaixa(bool Existe, int NaFila, string? Erro);

    public const string PerguntaApagarDados =
        "Apagar também os dados deste caixa?\n\n" +
        "Sim: na próxima instalação o caixa começa do zero, sem login, sem configuração e sem o ponto de captura do TEF.\n\n" +
        "Não (recomendado): os dados ficam para a próxima instalação.";

    /// <summary>Lê a fila do banco do caixa sem mexer em nada (somente leitura, sem pool de conexão).</summary>
    public static DadosDoCaixa ConferirDados(string pastaDados)
    {
        if (!Directory.Exists(pastaDados)) return new DadosDoCaixa(false, 0, null);
        var db = Path.Combine(pastaDados, "pdv.db");
        if (!File.Exists(db)) return new DadosDoCaixa(true, 0, null);
        try
        {
            var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = db,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                // Sem pool: conexão guardada no pool segura o arquivo, e aí a pasta não sai do lugar.
                Pooling = false,
            }.ToString();
            using var cx = new Microsoft.Data.Sqlite.SqliteConnection(cs);
            cx.Open();
            using var cmd = cx.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM outbox WHERE enviado_em IS NULL AND desistido_em IS NULL";
            return new DadosDoCaixa(true, Convert.ToInt32(cmd.ExecuteScalar()), null);
        }
        catch (Exception ex) { return new DadosDoCaixa(true, 0, ex.Message); }
    }

    /// <summary>A segunda pergunta, quando há o que perder. Null = nada pendente, pode seguir.</summary>
    public static string? AvisoAntesDeApagar(DadosDoCaixa d)
    {
        if (!d.Existe) return null;
        if (d.Erro is not null)
            return "Não consegui conferir se este caixa tem vendas que ainda não subiram para o painel.\n\n"
                 + "Os dados ficam numa cópia que só o suporte recupera. Apagar mesmo assim?";
        if (d.NaFila <= 0) return null;
        var um = d.NaFila == 1;
        return $"{d.NaFila} {(um ? "registro" : "registros")} deste caixa (vendas, turnos) ainda não {(um ? "subiu" : "subiram")} para o painel.\n\n"
             + "Se apagar agora, eles ficam numa cópia que só o suporte recupera. Apagar mesmo assim?";
    }

    /// <summary>Onde a pasta de dados vai parar: ao lado dela, com a data ("PdvNativo.apagado-20260914-193000").</summary>
    public static string PastaApartada(string pastaDados, DateTime agora)
        => Path.TrimEndingDirectorySeparator(pastaDados) + ".apagado-"
         + agora.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Tira a pasta de dados do lugar (renomeia). NUNCA apaga. Null = deu (com o destino); senão
    /// a frase para a tela. Com o caixa aberto o Windows não deixa, e a frase manda fechar.
    /// </summary>
    public static string? ApartarDados(string pastaDados, DateTime agora, out string? destino)
    {
        destino = null;
        if (!Directory.Exists(pastaDados)) return null;
        var alvo = PastaApartada(pastaDados, agora);
        try
        {
            Directory.Move(pastaDados, alvo);
            destino = alvo;
            return null;
        }
        catch (IOException) { return "Não consegui tirar os dados do lugar: feche o caixa nesta máquina e tente de novo."; }
        catch (UnauthorizedAccessException) { return "O Windows não deixou mexer nos dados do caixa. Abra o instalador como administrador e tente de novo."; }
    }

    /// <summary>
    /// Um programa do Windows pelo caminho absoluto de System32. O instalador roda como administrador
    /// e, pelo nome, o Windows procura primeiro na pasta do instalador e na pasta atual (na
    /// atualização, o TEMP do usuário): um icacls.exe ou wscript.exe plantado ali rodaria elevado.
    /// </summary>
    public static string ProgramaDoSistema(string nome) => Path.Combine(Environment.SystemDirectory, nome);

    /// <summary>
    /// Onde mora o VBScript do atalho: na pasta do programa (Program Files: só administrador grava),
    /// com nome único. No TEMP do usuário, com nome fixo, o usuário trocaria o script entre a escrita
    /// e o wscript, e o script dele rodaria como administrador (revisão de segurança da 1.0.36).
    /// </summary>
    public static string ScriptDoAtalho(string alvo) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(alvo))!, "atalho-" + Guid.NewGuid().ToString("N")[..8] + ".vbs");

    /// <summary>Atalho .lnk sem referenciar COM tipado: um VBScript de uma vez
    /// evita dependência de Interop no publish single-file.</summary>
    private static void CriarAtalho(string lnk, string alvo)
    {
        var vbs = ScriptDoAtalho(alvo);
        File.WriteAllText(vbs, $"""
            Set ws = CreateObject("WScript.Shell")
            Set a = ws.CreateShortcut("{lnk}")
            a.TargetPath = "{alvo}"
            a.WorkingDirectory = "{Path.GetDirectoryName(alvo)}"
            a.Description = "{NomePrograma}"
            a.Save
            """);
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = ProgramaDoSistema("wscript.exe"), Arguments = $"//B \"{vbs}\"",
                UseShellExecute = false, CreateNoWindow = true,
            });
            p?.WaitForExit(15_000);
        }
        finally { try { File.Delete(vbs); } catch { } }
    }
}
