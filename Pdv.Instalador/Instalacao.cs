using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

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
    public const string NomePrograma = "PDV MMTech";
    public const string Fabricante = "MMTech";

    /// <summary>Como o produto se chamava antes. Existe só para a migração: uma
    /// máquina que já tinha a versão anterior instalada não pode ficar com duas
    /// entradas no Adicionar/Remover Programas nem com dois atalhos.</summary>
    public const string NomeAntigo = "PDV American Day";

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
    public static string? ConferirQueOProgramaAbre(string exe)
    {
        var png = Path.Combine(Path.GetTempPath(), "pdv-conferencia-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--cupom-teste \"{png}\"",
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p is null) return AvaliarConferencia(false, -1, "", exe);

            var saida = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            var terminou = p.WaitForExit((int)PrazoDaConferencia.TotalMilliseconds);
            if (!terminou) { try { p.Kill(entireProcessTree: true); } catch { } }
            return AvaliarConferencia(terminou, terminou ? p.ExitCode : -1, saida, exe);
        }
        catch (Exception ex) { return AvaliarConferencia(false, -1, ex.Message, exe); }
        finally { try { File.Delete(png); } catch { } }
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
        bool ConferirQueAbre = true);

    /// <summary>Instala (ou ATUALIZA por cima — preservando os dados). Devolve
    /// null no sucesso, mensagem de erro no fracasso.</summary>
    public static string? Instalar(Opcoes o, Action<string>? progresso = null)
    {
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
                var icacls = Process.Start(new ProcessStartInfo
                {
                    FileName = "icacls",
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
        try
        {
            using (var run = Registry.LocalMachine.CreateSubKey(ChaveRun))
                run.DeleteValue(NomeAntigo, throwOnMissingValue: false);
            Registry.LocalMachine.DeleteSubKeyTree(ChaveUninstallAntiga, throwOnMissingSubKey: false);
        }
        catch { /* sem permissão no registro: o resto da migração ainda vale */ }

        var atalhoAntigo = Path.Combine(AreaDeTrabalho, NomeAntigo + ".lnk");
        try { if (File.Exists(atalhoAntigo)) File.Delete(atalhoAntigo); } catch { }

        var antiga = PastaDestinoAntiga;
        if (Directory.Exists(antiga) &&
            !antiga.Equals(pastaNova, StringComparison.OrdinalIgnoreCase))
            try { Directory.Delete(antiga, recursive: true); } catch { /* em uso: fica o esqueleto */ }
    }

    private static string AreaDeTrabalho =>
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    /// <summary>Remove programa, atalho e registro. Os DADOS ficam — sempre.</summary>
    public static string? Desinstalar()
    {
        try
        {
            using (var run = Registry.LocalMachine.CreateSubKey(ChaveRun))
            {
                run.DeleteValue(NomePrograma, throwOnMissingValue: false);
                run.DeleteValue(NomeAntigo, throwOnMissingValue: false);
            }
            Registry.LocalMachine.DeleteSubKeyTree(ChaveUninstall, throwOnMissingSubKey: false);
            Registry.LocalMachine.DeleteSubKeyTree(ChaveUninstallAntiga, throwOnMissingSubKey: false);

            foreach (var nome in new[] { NomePrograma, NomeAntigo })
            {
                var atalho = Path.Combine(AreaDeTrabalho, nome + ".lnk");
                if (File.Exists(atalho)) File.Delete(atalho);
            }

            foreach (var pasta in new[] { PastaDestinoPadrao, PastaDestinoAntiga })
                if (Directory.Exists(pasta))
                    // o próprio desinstalador mora aí: apaga o que der agora; o que
                    // estiver em uso fica como esqueleto — os dados seguem intactos
                    try { Directory.Delete(pasta, recursive: true); } catch { }

            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Atalho .lnk sem referenciar COM tipado: um VBScript de uma vez
    /// evita dependência de Interop no publish single-file.</summary>
    private static void CriarAtalho(string lnk, string alvo)
    {
        var vbs = Path.Combine(Path.GetTempPath(), "atalho_pdv.vbs");
        File.WriteAllText(vbs, $"""
            Set ws = CreateObject("WScript.Shell")
            Set a = ws.CreateShortcut("{lnk}")
            a.TargetPath = "{alvo}"
            a.WorkingDirectory = "{Path.GetDirectoryName(alvo)}"
            a.Description = "{NomePrograma}"
            a.Save
            """);
        var p = Process.Start(new ProcessStartInfo
        {
            FileName = "wscript", Arguments = $"//B \"{vbs}\"",
            UseShellExecute = false, CreateNoWindow = true,
        });
        p?.WaitForExit(15_000);
        try { File.Delete(vbs); } catch { }
    }
}
