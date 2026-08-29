using System.IO;
using Pdv.Instalador;

namespace Pdv.Testes;

/// <summary>
/// Testes do INSTALADOR.
///
/// O que quebra aqui não aparece em nenhuma venda: aparece uma vez por máquina,
/// longe de quem escreveu o código, com o dono da loja sozinho na frente da tela e
/// sem ninguém para ligar. Um instalador que erra não erra pequeno — ou a loja não
/// abre o caixa, ou perde o que já vendeu.
///
/// Este arquivo nasceu de um defeito REAL e mudo: o instalador copiava só o
/// Pdv.exe. O publish do .NET deixa as bibliotecas nativas (WPF, SQLite, WebView2)
/// SOLTAS ao lado do exe, então a instalação terminava dizendo "concluído", criava
/// o atalho, registrava o programa no Windows — e o PDV morria em
/// DllNotFoundException antes da primeira tela. Nada no build acusava: o instalador
/// compilava, e o Pdv.exe também. Só rodando a instalação inteira dava para ver.
///
/// A primeira tentativa de consertar isso foi exigir uma LISTA de arquivos na origem —
/// e estava errada também, por outro caminho: com
/// <c>IncludeNativeLibrariesForSelfExtract=true</c> as bibliotecas moram dentro do exe
/// e a pasta boa não tem nenhuma delas. As duas formas de publish são válidas e
/// nenhuma é dedutível olhando a pasta. A garantia certa não é conferir arquivo: é
/// MANDAR O PROGRAMA ABRIR depois de copiar.
///
/// Por isso os quatro eixos aqui são:
///  · a peneira da origem afirma só o que dá para afirmar sem executar nada;
///  · O CAIXA ABRE depois de instalado — e, se não abrir, a instalação NÃO termina
///    (nada de atalho e registro apontando para programa morto);
///  · A PASTA INTEIRA CHEGA no destino, subpastas inclusive (runtimes\ é onde mora
///    o WebView2);
///  · ATUALIZAR POR CIMA NÃO APAGA DADO — é a regra que, se quebrar, custa as
///    vendas do dia.
///
/// Tudo roda em pasta descartável no TEMP. Nenhum teste daqui escreve no registro,
/// na área de trabalho ou em Program Files: por isso todo <see cref="Instalacao.Opcoes"/>
/// destes testes vem com AjustarAcl/GravarRegistro/AtalhoAreaTrabalho/MigrarAntiga
/// desligados — e com ConferirQueAbre desligado, porque aqui o "Pdv.exe" é um arquivo
/// de texto. Ligado, o Instalar de verdade APAGARIA a pasta da versão anterior desta
/// máquina.
/// </summary>
public static class TestesInstalador
{
    public static void Rodar(Action<bool, string> checar)
    {
        var raiz = Path.Combine(Path.GetTempPath(), "pdv-testes-instalador-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            OrigemIncompleta(checar, raiz);
            ConferenciaDeAbertura(checar, raiz);
            ListaDoQueCopiar(checar, raiz);
            InstalacaoNova(checar, raiz);
            AtualizarPorCima(checar, raiz);
            ArquivoEmUso(checar, raiz);
            Marca(checar);
            EtapaPayGo(checar);
            CaudaDoPacote(checar);
            PacoteIdaEVolta(checar, raiz);
        }
        finally
        {
            try { if (Directory.Exists(raiz)) Directory.Delete(raiz, recursive: true); } catch { }
        }
    }

    // ---------------------------------------------------------------- origem

    /// <summary>
    /// A peneira rápida da origem, e o limite dela — que foi onde eu errei antes.
    ///
    /// A primeira versão deste teste exigia uma lista fixa de 7 bibliotecas nativas ao
    /// lado do exe. Parecia rigor e era erro: com
    /// <c>IncludeNativeLibrariesForSelfExtract=true</c> elas moram DENTRO do exe, a
    /// pasta legítima não tem nenhuma, e a "peneira" reprovaria um publish perfeito
    /// (medido: o v21 é só Pdv.exe e abre sozinho; o v20 tem as 7 soltas e o exe
    /// sozinho morre). Duas formas válidas, nenhuma dedutível da pasta.
    ///
    /// Então esta peneira só afirma o que dá para afirmar sem executar nada. Quem
    /// responde "o caixa abre?" é <see cref="ConferenciaDeAbertura"/>, mandando abrir.
    /// </summary>
    private static void OrigemIncompleta(Action<bool, string> checar, string raiz)
    {
        checar(Instalacao.ConferirOrigem(null) is not null, "origem nula é recusada");
        checar(Instalacao.ConferirOrigem(Path.Combine(raiz, "que-nao-existe")) is not null,
            "origem inexistente é recusada");

        var semExe = Path.Combine(raiz, "sem-exe");
        Directory.CreateDirectory(semExe);
        File.WriteAllText(Path.Combine(semExe, "leiame.txt"), "x");
        checar(Instalacao.ConferirOrigem(semExe)?.Contains("Pdv.exe") == true,
            "pasta sem o programa é recusada, e a mensagem diz o que falta");

        // As DUAS formas de publish são aceitas na peneira — nenhuma delas dá para
        // reprovar aqui sem reprovar um publish bom junto.
        var comNativas = Path.Combine(raiz, "com-nativas");
        CriarOrigem(comNativas);
        checar(Instalacao.ConferirOrigem(comNativas) is null,
            "publish com as bibliotecas soltas passa (formato do v20)");

        var soExe = Path.Combine(raiz, "so-exe");
        Directory.CreateDirectory(soExe);
        File.WriteAllText(Path.Combine(soExe, "Pdv.exe"), "x");
        checar(Instalacao.ConferirOrigem(soExe) is null,
            "publish de arquivo único passa (formato do v21 — exigir DLL aqui reprovaria um publish bom)");
    }

    /// <summary>
    /// A CONFERÊNCIA QUE VALE: depois de copiar, o instalador manda o programa ABRIR
    /// (`--cupom-teste`, que desenha o cupom num PNG e sai sem tocar em venda nenhuma).
    /// Se ele não abrir, a instalação não termina — nada de atalho e registro apontando
    /// para um programa morto.
    ///
    /// O caminho feliz é provado pela instalação de verdade (o programa abre). O que
    /// se testa aqui é o caminho que a instalação de verdade nunca percorre — e que é
    /// exatamente o que precisa funcionar quando der ruim numa loja.
    /// </summary>
    private static void ConferenciaDeAbertura(Action<bool, string> checar, string raiz)
    {
        checar(Instalacao.AvaliarConferencia(terminou: true, codigo: 0, "ok: c.png", "x\\Pdv.exe") is null,
            "conferência: saiu com código 0 → o caixa abre");

        var falhou = Instalacao.AvaliarConferencia(true, 1, "erro", "x\\Pdv.exe");
        checar(falhou is not null, "conferência: código diferente de 0 REPROVA a instalação");
        checar(falhou?.Contains("não abriu") == true, "conferência: a mensagem diz que não abriu");
        checar(falhou?.Contains("antivírus") == true,
            "conferência: e aponta a causa mais comum, senão não há o que tentar");

        var travou = Instalacao.AvaliarConferencia(terminou: false, -1, "", "x\\Pdv.exe");
        checar(travou is not null, "conferência: programa que trava também reprova");
        checar(travou != falhou, "conferência: travar e falhar dizem coisas diferentes");

        // Pasta com bibliotecas soltas PELA METADE: a mensagem tem que citar isso,
        // porque é a pista que resolve.
        var meia = Path.Combine(raiz, "meia-pasta");
        Directory.CreateDirectory(meia);
        File.WriteAllText(Path.Combine(meia, "Pdv.exe"), "x");
        File.WriteAllText(Path.Combine(meia, Instalacao.BibliotecasNativas[0]), "x");
        var comPista = Instalacao.AvaliarConferencia(true, 1, "", Path.Combine(meia, "Pdv.exe"));
        checar(comPista?.Contains("bibliotecas") == true,
            "conferência: pasta com bibliotecas pela metade ganha a pista certa");

        // E o guard morde de verdade num processo REAL: o ping existe, roda, e sai
        // com código != 0 quando recebe --cupom-teste. É o mais perto de "o programa
        // não abre" que dá para montar sem um Pdv.exe quebrado.
        var cobaia = Path.Combine(Environment.SystemDirectory, "ping.exe");
        if (File.Exists(cobaia))
        {
            var pasta = Path.Combine(raiz, "abre-nao");
            Directory.CreateDirectory(pasta);
            var falso = Path.Combine(pasta, "Pdv.exe");
            File.Copy(cobaia, falso, overwrite: true);
            checar(Instalacao.ConferirQueOProgramaAbre(falso) is not null,
                "conferência: programa que não entende --cupom-teste é reprovado (processo real)");
        }
    }

    /// <summary>
    /// A lista do que copiar tem que descer nas SUBPASTAS (runtimes\ é onde o
    /// WebView2 mora) e tem que ignorar o lixo da atualização anterior (*.velho) —
    /// copiar .velho de volta ressuscita a versão que acabou de sair.
    /// </summary>
    private static void ListaDoQueCopiar(Action<bool, string> checar, string raiz)
    {
        var origem = Path.Combine(raiz, "lista");
        CriarOrigem(origem);
        var sub = Path.Combine(origem, "runtimes", "win-x64", "native");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "WebView2Loader.dll"), "nativo");
        File.WriteAllText(Path.Combine(origem, "Pdv.exe.velho"), "resto da atualizacao anterior");

        var lista = Instalacao.ArquivosParaCopiar(origem);
        checar(lista.Any(r => r.Contains("runtimes")), "a lista desce nas subpastas");
        checar(!lista.Any(r => r.EndsWith(Instalacao.SufixoVelho, StringComparison.OrdinalIgnoreCase)),
            "a lista ignora os restos *.velho");
        checar(lista.Contains("Pdv.exe"), "a lista traz o programa");
        checar(Instalacao.ArquivosParaCopiar(Path.Combine(raiz, "nao-existe")).Count == 0,
            "pasta inexistente devolve lista vazia, não explode");
    }

    // ------------------------------------------------------------- instalação

    private static void InstalacaoNova(Action<bool, string> checar, string raiz)
    {
        var origem = Path.Combine(raiz, "nova-origem");
        var destino = Path.Combine(raiz, "nova-destino");
        CriarOrigem(origem);
        var sub = Path.Combine(origem, "runtimes", "win-x64", "native");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "extra.dll"), "nativo");

        var passos = new List<string>();
        var erro = Instalacao.Instalar(Sandbox(origem, destino), passos.Add);

        checar(erro is null, "instalação nova conclui sem erro");
        // Todo arquivo da origem, não "o exe e o que der" — foi copiar só o Pdv.exe
        // que produzia uma instalação que nunca abriu.
        checar(File.Exists(Path.Combine(destino, "Pdv.exe")), "chegou no destino: o programa");
        foreach (var nativa in Instalacao.BibliotecasNativas)
            checar(File.Exists(Path.Combine(destino, nativa)), $"chegou no destino: {nativa}");
        checar(File.Exists(Path.Combine(destino, "runtimes", "win-x64", "native", "extra.dll")),
            "subpasta inteira chegou no destino");
        checar(passos.Count > 0 && passos[^1] == "Concluído.", "o progresso termina em Concluído");
    }

    /// <summary>
    /// A regra que, se quebrar, custa dinheiro de verdade: atualizar por cima troca
    /// o PROGRAMA e não encosta nos DADOS. O banco de vendas mora fora da pasta de
    /// instalação exatamente por isso — este teste guarda a fronteira.
    /// </summary>
    private static void AtualizarPorCima(Action<bool, string> checar, string raiz)
    {
        var origem = Path.Combine(raiz, "upd-origem");
        var destino = Path.Combine(raiz, "upd-destino");
        CriarOrigem(origem);
        File.WriteAllText(Path.Combine(origem, "Pdv.exe"), "VERSAO 1");
        Instalacao.Instalar(Sandbox(origem, destino));

        // Coisas que a loja acumulou dentro da pasta do programa e que a atualização
        // não pode varrer junto.
        var deixadoPelaLoja = Path.Combine(destino, "logo-da-loja.png");
        File.WriteAllText(deixadoPelaLoja, "logo");

        File.WriteAllText(Path.Combine(origem, "Pdv.exe"), "VERSAO 2");
        var erro = Instalacao.Instalar(Sandbox(origem, destino));

        checar(erro is null, "atualizar por cima conclui sem erro");
        checar(File.ReadAllText(Path.Combine(destino, "Pdv.exe")) == "VERSAO 2",
            "atualizar por cima troca o programa");
        checar(File.Exists(deixadoPelaLoja), "atualizar por cima NÃO apaga o que a loja deixou na pasta");
    }

    /// <summary>
    /// Atualizar com o PDV ABERTO é o caso normal, não a exceção: o caixa fica ligado
    /// o dia inteiro e ninguém vai fechar no meio do movimento para atualizar.
    ///
    /// ⚠️ ESTE TESTE PRECISA DE UM EXECUTÁVEL DE VERDADE RODANDO, e a primeira versão
    /// dele estava errada: eu segurava o arquivo com um FileStream e chamava aquilo de
    /// "programa aberto". Não é a mesma coisa. Um exe em execução é mapeado pelo
    /// Windows como IMAGEM, e imagem tem uma regra própria: NÃO PODE SER APAGADA, mas
    /// PODE SER RENOMEADA. Nenhuma combinação de FileShare reproduz esse par — com
    /// FileShare.Read o renomeio também é barrado (e o teste acusa um defeito que não
    /// existe), com FileShare.Delete o apagar passa (e o teste aprova um caminho que a
    /// loja nunca percorre). Ou seja: o simulacro respondia sobre outro fenômeno.
    ///
    /// Por isso aqui roda um processo real. O cobaia é o ping do Windows, que existe em
    /// qualquer máquina, é pequeno e fica vivo sozinho pelo tempo que mandarmos — o que
    /// interessa não é o que ele faz, é o cadeado que o Windows põe no arquivo dele.
    /// </summary>
    private static void ArquivoEmUso(Action<bool, string> checar, string raiz)
    {
        var cobaia = Path.Combine(Environment.SystemDirectory, "ping.exe");
        if (!File.Exists(cobaia))
        {
            // Sem cobaia não há teste. Isto é BLOQUEADO, não é "passou": calar aqui
            // devolveria uma suíte verde sem ter exercitado a atualização com o caixa
            // aberto, que é o caminho mais comum de todos.
            checar(false, "atualiza com o programa aberto — SEM COBAIA (ping.exe não achado)");
            return;
        }

        var origem = Path.Combine(raiz, "uso-origem");
        var destino = Path.Combine(raiz, "uso-destino");
        CriarOrigem(origem);
        var destinoExe = Path.Combine(destino, "Pdv.exe");

        // Versão 1 = um exe DE VERDADE, para poder ser executado no passo seguinte.
        File.Copy(cobaia, Path.Combine(origem, "Pdv.exe"), overwrite: true);
        Instalacao.Instalar(Sandbox(origem, destino));

        System.Diagnostics.Process? rodando = null;
        try
        {
            rodando = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = destinoExe,
                // fica vivo alguns segundos sozinho, sem janela e sem depender de stdin
                Arguments = "-n 30 127.0.0.1",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            if (rodando is null || rodando.HasExited)
            {
                checar(false, "atualiza com o programa aberto — a cobaia não ficou rodando");
                return;
            }

            // Versão 2 já pode ser conteúdo qualquer: o que se testa daqui para a
            // frente é a TROCA, não o programa.
            File.WriteAllText(Path.Combine(origem, "Pdv.exe"), "VERSAO 2");
            var erro = Instalacao.Instalar(Sandbox(origem, destino));

            checar(erro is null, "atualiza com o programa RODANDO de verdade");
            checar(File.Exists(destinoExe) && File.ReadAllText(destinoExe) == "VERSAO 2",
                "a versão nova entra no lugar");
            checar(File.Exists(destinoExe + Instalacao.SufixoVelho),
                "a versão em uso sai de lado como .velho");
            checar(!rodando.HasExited, "o caixa que estava aberto continua vivo durante a troca");
        }
        finally
        {
            try { rodando?.Kill(entireProcessTree: true); rodando?.WaitForExit(5_000); } catch { }
            rodando?.Dispose();
        }

        // E o .velho some na instalação seguinte, agora com o programa fechado — senão
        // a pasta acumula 156 MB por atualização até encher o disco do caixa.
        Instalacao.Instalar(Sandbox(origem, destino));
        checar(!File.Exists(destinoExe + Instalacao.SufixoVelho),
            "o .velho é varrido na instalação seguinte");
    }

    // ------------------------------------------------------------------ marca

    /// <summary>
    /// O sistema é vendido para outras lojas: o nome do FABRICANTE aparece no
    /// Adicionar/Remover Programas, e não pode ser o nome de um cliente. E como o
    /// nome mudou depois de já existir máquina instalada, as duas pastas TÊM que ser
    /// diferentes — é o que obriga a migração a existir; iguais, ela viraria um
    /// apagar-a-si-mesmo silencioso.
    /// </summary>
    private static void Marca(Action<bool, string> checar)
    {
        checar(!Instalacao.NomePrograma.Contains("American Day", StringComparison.OrdinalIgnoreCase),
            "o nome do produto não carrega a marca de um cliente");
        checar(!Instalacao.Fabricante.Contains("American Day", StringComparison.OrdinalIgnoreCase),
            "o fabricante não é o nome de um cliente");
        checar(!Instalacao.PastaDestinoPadrao.Equals(Instalacao.PastaDestinoAntiga,
                   StringComparison.OrdinalIgnoreCase),
            "a pasta nova é diferente da antiga (é o que a migração pressupõe)");
        checar(Instalacao.PastaDados.Contains("PdvNativo"),
            "os dados continuam no ProgramData, fora da pasta do programa");
        checar(!Instalacao.PastaDados.StartsWith(Instalacao.PastaDestinoPadrao,
                   StringComparison.OrdinalIgnoreCase),
            "os dados NÃO ficam dentro da pasta do programa (desinstalar apagaria as vendas)");
    }

    // ---------------------------------------------------------------- PayGo

    /// <summary>
    /// A ETAPA DO PAYGO. O que dá para testar sem instalar nada é a DECISÃO — e é
    /// justamente ela que não dá para ensaiar na loja: acontece uma vez por máquina,
    /// e as três situações (limpa, já instalada, TEF de pé) levam a caminhos
    /// diferentes que ninguém vai percorrer de novo para conferir.
    ///
    /// A ordem das regras é o ponto: TEF RODANDO VENCE "já instalado". O instalador
    /// do PayGo fecha o PayGo.exe à força; se houver uma venda no pinpad naquele
    /// instante, ela morre. Isso vale inclusive para quem só ia reinstalar por cima —
    /// por isso a checagem de processo vem antes de tudo, e não depois.
    /// </summary>
    private static void EtapaPayGo(Action<bool, string> checar)
    {
        checar(PayGo.Decidir(arquivoPresente: true, jaInstalado: false, tefRodando: false)
               == PayGo.Acao.Instalar, "paygo: máquina limpa e arquivo junto → instala");

        checar(PayGo.Decidir(true, jaInstalado: true, tefRodando: false)
               == PayGo.Acao.JaInstalado, "paygo: já instalado → não reinstala por cima");

        // As duas linhas que guardam a venda no pinpad.
        checar(PayGo.Decidir(true, jaInstalado: false, tefRodando: true)
               == PayGo.Acao.FecharTefPrimeiro, "paygo: TEF de pé bloqueia a instalação");
        checar(PayGo.Decidir(true, jaInstalado: true, tefRodando: true)
               == PayGo.Acao.FecharTefPrimeiro,
            "paygo: TEF de pé VENCE 'já instalado' (reinstalar também mata a venda)");

        // Sem o arquivo o PDV instala assim mesmo: caixa sem cartão ainda vende.
        checar(PayGo.Decidir(arquivoPresente: false, jaInstalado: false, tefRodando: false)
               == PayGo.Acao.SemArquivo, "paygo: sem o arquivo, a etapa é pulada e não falha");

        // Toda decisão tem que ter texto: uma tela que bloqueia em silêncio vira
        // ligação para o suporte.
        foreach (PayGo.Acao a in Enum.GetValues<PayGo.Acao>())
            checar(!string.IsNullOrWhiteSpace(PayGo.Explicar(a)),
                $"paygo: a decisão '{a}' tem o que dizer na tela");

        checar(PayGo.Explicar(PayGo.Acao.FecharTefPrimeiro).Contains("Feche"),
            "paygo: o bloqueio manda FECHAR, não só informa que está aberto");

        // Arquivo que não existe tem que virar MENSAGEM, não exceção: aqui o caixa já
        // está instalado, e uma exceção nesta altura mostraria "a instalação falhou"
        // para quem acabou de instalar com sucesso.
        checar(PayGo.Instalar(Path.Combine(Path.GetTempPath(), "paygo-que-nao-existe.exe")) is not null,
            "paygo: arquivo ausente vira mensagem, não derruba a instalação");
    }

    // ---------------------------------------------------------------- pacote

    /// <summary>
    /// A CAUDA DO EXE — os 32 bytes no fim que dizem onde o payload começa.
    ///
    /// É um formato de arquivo, e formato de arquivo erra de um jeito específico: não
    /// dá erro de compilação, não dá erro no build, e só aparece na máquina da loja,
    /// no primeiro clique, como uma exceção que não explica nada.
    ///
    /// O caso que mais importa aqui não é o feliz, é o TRUNCADO: uma internet de loja
    /// derruba o download de 236 MB pela metade, e o exe que sobra ainda abre — o host
    /// do .NET não liga para o que vem depois do programa. Sem conferir a geometria
    /// (offset + tamanho + trailer == tamanho do arquivo), esse exe pela metade iria
    /// procurar um zip onde não há nada.
    /// </summary>
    private static void CaudaDoPacote(Action<bool, string> checar)
    {
        // Um exe de mentira: cabeçalho, payload e trailer, no mesmo formato do real.
        const int tamExe = 1000;
        const int tamPayload = 500;
        var arquivo = new MemoryStream();
        arquivo.Write(new byte[tamExe]);
        arquivo.Write(Enumerable.Range(0, tamPayload).Select(i => (byte)i).ToArray());
        arquivo.Write(Pacote.MontarTrailer(tamExe, tamPayload, crc: 0x1234));

        var lida = Pacote.LerTrailer(arquivo);
        checar(lida is not null, "a cauda é lida de volta");
        checar(lida?.Offset == tamExe, "o offset volta igual ao gravado");
        checar(lida?.Tamanho == tamPayload, "o tamanho volta igual ao gravado");
        checar(lida?.Crc == 0x1234, "o CRC volta igual ao gravado");
        checar(lida?.Versao == Pacote.VersaoFormato, "a versão do formato volta");

        // Exe recém-publicado, ainda sem empacotar: é situação NORMAL, não erro.
        var semCauda = new MemoryStream(new byte[2000]);
        checar(Pacote.LerTrailer(semCauda) is null, "exe sem payload devolve nada (não explode)");

        // ⚠️ O caso que importa: download que caiu no meio.
        var truncado = new MemoryStream();
        arquivo.Position = 0;
        arquivo.CopyTo(truncado);
        truncado.SetLength(truncado.Length - 100); // sumiu o fim, trailer inclusive
        checar(Pacote.LerTrailer(truncado) is null, "arquivo truncado é recusado");

        // Trailer intacto, mas o payload encolheu: a conta não fecha e tem que ser
        // recusado ANTES de tentar ler um zip que não está inteiro.
        var mentiroso = new MemoryStream();
        mentiroso.Write(new byte[tamExe]);
        mentiroso.Write(new byte[tamPayload - 50]);
        mentiroso.Write(Pacote.MontarTrailer(tamExe, tamPayload, crc: 0));
        checar(Pacote.LerTrailer(mentiroso) is null, "geometria que não fecha é recusada");

        // Arquivo menor que o próprio trailer não pode virar Seek negativo.
        checar(Pacote.LerTrailer(new MemoryStream(new byte[10])) is null,
            "arquivo menor que o trailer é recusado");

        // CRC32 padrão, conferido contra o valor canônico de "123456789".
        var crc = Pacote.Crc32(new MemoryStream(System.Text.Encoding.ASCII.GetBytes("123456789")));
        checar(crc == 0xCBF43926, "o CRC32 é o padrão (bate com o vetor conhecido)");

        // O offset é onde o exe acaba e o payload começa — é ele que diz até onde
        // copiar quando o desinstalador vai para Program Files sem a cauda.
        checar(lida?.Offset == tamExe,
            "o offset marca o fim do programa (é por onde o desinstalador é cortado)");
    }

    /// <summary>
    /// EMPACOTAR → EXTRAIR, o ciclo inteiro, com bytes conferidos na saída.
    ///
    /// Os testes de trailer acima provam a aritmética; este prova o produto. É o mais
    /// próximo que dá de "gerei o instalador e ele abre" sem gastar os 237 MB e os
    /// minutos do pacote de verdade — e cobre o que a aritmética não alcança: o zip
    /// montado com os caminhos certos, a pasta do PDV saindo instalável do outro lado,
    /// o paygo.exe no lugar combinado, e o conteúdo chegando byte a byte igual.
    /// </summary>
    private static void PacoteIdaEVolta(Action<bool, string> checar, string raiz)
    {
        var origem = Path.Combine(raiz, "pack-origem");
        CriarOrigem(origem);
        // Uma subpasta, porque runtimes\ existe no publish de verdade e caminho de zip
        // usa barra normal — trocar de separador é como um pacote sai torto.
        var sub = Path.Combine(origem, "runtimes", "win-x64", "native");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "extra.dll"), "conteudo nativo");
        File.WriteAllText(Path.Combine(origem, "Pdv.exe"), "PROGRAMA VERSAO X");

        var paygoFalso = Path.Combine(raiz, "paygo-falso.exe");
        File.WriteAllText(paygoFalso, "instalador do tef");

        // "exe base" = qualquer arquivo; o que interessa é que a cauda vem depois dele.
        var exeBase = Path.Combine(raiz, "base.exe");
        File.WriteAllBytes(exeBase, Enumerable.Repeat((byte)0x4D, 4096).ToArray());

        var pacote = Path.Combine(raiz, "InstaladorTeste.exe");
        var erro = Pacote.Empacotar(exeBase, origem, paygoFalso, pacote);
        checar(erro is null, "pacote: empacotar conclui sem erro");
        checar(File.Exists(pacote), "pacote: o instalador foi gravado");

        // O exe base tem que continuar intacto no começo do arquivo — é ele que roda.
        using (var fs = File.OpenRead(pacote))
        {
            var cabeca = new byte[4096];
            fs.ReadExactly(cabeca, 0, 4096);
            checar(cabeca.All(b => b == 0x4D), "pacote: o programa continua intacto antes da cauda");
        }

        var aberto = Path.Combine(raiz, "pack-aberto");
        var falha = Pacote.Extrair(aberto, null, pacote);
        checar(falha is null, "pacote: extrair conclui sem erro");

        var pdvDentro = Path.Combine(aberto, "pdv");
        checar(Instalacao.ConferirOrigem(pdvDentro) is null,
            "pacote: o PDV que sai do pacote é INSTALÁVEL (é o teste que fecha o ciclo)");
        checar(File.Exists(Path.Combine(aberto, "paygo.exe")), "pacote: o PayGo saiu no lugar combinado");
        checar(File.ReadAllText(Path.Combine(pdvDentro, "Pdv.exe")) == "PROGRAMA VERSAO X",
            "pacote: o conteúdo volta byte a byte igual");
        checar(File.Exists(Path.Combine(pdvDentro, "runtimes", "win-x64", "native", "extra.dll")),
            "pacote: a subpasta sobrevive à viagem");

        // E o ciclo completo: o que saiu do pacote instala.
        var destino = Path.Combine(raiz, "pack-instalado");
        checar(Instalacao.Instalar(Sandbox(pdvDentro, destino)) is null,
            "pacote: e a instalação a partir do que foi extraído funciona");
    }

    // ----------------------------------------------------------------- apoio

    /// <summary>Opções que NÃO encostam nesta máquina: sem registro, sem ACL, sem
    /// atalho, sem migração. Só copiar arquivo para dentro do TEMP.</summary>
    private static Instalacao.Opcoes Sandbox(string origem, string destino) => new(
        OrigemPasta: origem,
        PastaDestino: destino,
        IniciarComWindows: false,
        AtalhoAreaTrabalho: false,
        AjustarAcl: false,
        GravarRegistro: false,
        MigrarAntiga: false,
        ConferirQueAbre: false);

    /// <summary>Uma origem crível no formato do publish v20 (exe + bibliotecas
    /// soltas), com conteúdo de mentira: o que se testa aqui é a CÓPIA, não o
    /// programa.</summary>
    private static void CriarOrigem(string pasta)
    {
        Directory.CreateDirectory(pasta);
        File.WriteAllText(Path.Combine(pasta, "Pdv.exe"), "conteudo de Pdv.exe");
        foreach (var nome in Instalacao.BibliotecasNativas)
            File.WriteAllText(Path.Combine(pasta, nome), "conteudo de " + nome);
    }
}
