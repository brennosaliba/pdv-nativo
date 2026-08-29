using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// Testes do botão "Atualizar o caixa".
///
/// O QUE QUEBRA NA LOJA SE ISTO QUEBRAR — e a lista é feia:
///  · comparação de versão como TEXTO: "0.10.0" &lt; "0.9.0" porque '1' &lt; '9'. A partir
///    da décima release o caixa decide sozinho que está em dia, para sempre, SEM
///    MOSTRAR ERRO NENHUM. Ninguém descobre por teste manual — descobre meses depois,
///    com a loja rodando uma versão que já foi corrigida duas vezes;
///  · download pela metade virando instalação: o instalador copia 165 MB truncados por
///    cima do programa que funcionava, e a loja fica sem caixa;
///  · portão furado: o caixa reinicia com a comanda aberta ou com o cartão do cliente
///    no pinpad. Isso não é bug de software, é dinheiro perdido no balcão.
///
/// NADA AQUI TOCA A REDE NEM O INSTALADOR DE VERDADE. O servidor é um
/// <see cref="ServidorDeMentira"/> injetado no HttpClient, e o "instalador" é um
/// arquivo de bytes com cabeçalho de executável — rodar o InstalarPdv.exe de verdade
/// reinstalaria o PDV desta máquina.
/// </summary>
public static class TestesAtualizacao
{
    public static async Task RodarAsync(Action<bool, string> checar)
    {
        var raiz = Path.Combine(Path.GetTempPath(), "pdv-testes-atualizacao-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(raiz);
            VersaoSemantica(checar);
            LeituraDoManifesto(checar);
            Portoes(checar);
            ADecisao(checar);
            MensagemDeProgresso(checar);
            Integridade(checar, raiz);
            DeOndeOExeVem(checar);
            await Download(checar, raiz);
        }
        finally
        {
            try { if (Directory.Exists(raiz)) Directory.Delete(raiz, recursive: true); } catch { }
        }
    }


    // ═══ DE ONDE O EXE VEM ═══════════════════════════════════════════════════

    /// <summary>
    /// A peneira que decide se o instalador anunciado pode ser baixado.
    ///
    /// Ela importa mais do que parece: o campo "url" vem da REDE, e o que sai dele é um
    /// executável rodando como ADMINISTRADOR na máquina da loja. Se um manifesto
    /// adulterado puder apontar para outro servidor, isso deixa de ser atualização e
    /// vira execução remota em toda loja instalada.
    ///
    /// ⚠️ A primeira versão comparava os DOIS ÚLTIMOS RÓTULOS do host. Funcionava para
    /// mmtech.software e falhava exatamente no formato de domínio desta operação: em
    /// .com.br, "pdv.americandaybrasil.com.br" reduz a "com.br" — e aí QUALQUER host
    /// .com.br do mundo passava. Agora é por sufixo de host, sem lista de sufixos
    /// públicos para envelhecer.
    /// </summary>
    private static void DeOndeOExeVem(Action<bool, string> checar)
    {
        const string manifesto = "https://mmtech.software/pdv/versao.json";

        checar(Atualizacao.MesmoDominio(manifesto, "https://pdv.mmtech.software/download/InstalarPdv.exe"),
            "de onde vem: subdomínio do servidor da atualização passa (é o caso real)");
        checar(Atualizacao.MesmoDominio(manifesto, "https://mmtech.software/download/InstalarPdv.exe"),
            "de onde vem: o mesmo host passa");
        checar(!Atualizacao.MesmoDominio(manifesto, "https://cdn-qualquer.com/InstalarPdv.exe"),
            "de onde vem: servidor de terceiro é RECUSADO");

        // O furo do .com.br, que era o caso desta operação.
        const string br = "https://pdv.americandaybrasil.com.br/versao.json";
        checar(Atualizacao.MesmoDominio(br, "https://pdv.americandaybrasil.com.br/x.exe"),
            "de onde vem: .com.br com o mesmo host passa");
        checar(!Atualizacao.MesmoDominio(br, "https://servidor-do-atacante.com.br/x.exe"),
            "de onde vem: OUTRO .com.br é recusado (o furo de comparar só 2 rótulos)");
        checar(!Atualizacao.MesmoDominio(br, "https://com.br/x.exe"),
            "de onde vem: o próprio sufixo público não vira dono do domínio");

        // Sufixo de texto não é sufixo de domínio: "malmmtech.software" termina com
        // "mmtech.software" como TEXTO, mas é outro dono. O ponto é o que separa.
        checar(!Atualizacao.MesmoDominio(manifesto, "https://malmmtech.software/x.exe"),
            "de onde vem: host que só PARECE subdomínio (sem o ponto) é recusado");

        checar(!Atualizacao.MesmoDominio(manifesto, "nao-e-url"),
            "de onde vem: url inválida é recusada, não explode");
    }

    // ═══ 1. VERSÃO ═══════════════════════════════════════════════════════════

    /// <summary>
    /// O bug clássico desta função, e o mais caro: comparar versão como string.
    /// </summary>
    private static void VersaoSemantica(Action<bool, string> checar)
    {
        // ⚠️ ESTE É O TESTE QUE JUSTIFICA O ARQUIVO INTEIRO. Em texto, "0.10.0" vem
        // ANTES de "0.9.0" — o caixa concluiria que já está adiantado e nunca mais
        // atualizaria, sem uma linha de erro em lugar nenhum.
        checar(string.CompareOrdinal("0.10.0", "0.9.0") < 0,
            "versão: comparar como TEXTO diz que 0.10.0 é MENOR que 0.9.0 (é o bug que existe)");
        checar(Atualizacao.Comparar("0.9.0", "0.10.0") < 0,
            "versão: 0.10.0 é MAIOR que 0.9.0 (semântica, não alfabética)");
        checar(Atualizacao.Comparar("0.10.0", "0.9.0") > 0, "versão: e o inverso também");
        checar(Atualizacao.Comparar("1.0.0", "0.99.99") > 0, "versão: o primeiro número manda");
        checar(Atualizacao.Comparar("0.2.10", "0.2.9") > 0, "versão: e o terceiro também é número");

        checar(Atualizacao.Comparar("0.2.0", "0.2.0") == 0, "versão: iguais empatam");
        checar(Atualizacao.Comparar("0.2", "0.2.0.0") == 0,
            "versão: 0.2 == 0.2.0.0 (o FileVersion do Windows tem 4 partes, o csproj tem 3)");
        checar(Atualizacao.Comparar("0.2.0.0", "0.2.0.1") < 0, "versão: a quarta parte desempata");

        checar(Atualizacao.TentarLerVersao("v0.3.0", out var comV) && comV.A == 0 && comV.B == 3,
            "versão: tolera o 'v' na frente");
        checar(Atualizacao.TentarLerVersao("0.3.0-rc1", out var rc) && rc.C == 0,
            "versão: sufixo de pré-lançamento não atrapalha a leitura");

        // "não é versão" NUNCA pode virar 0.0.0: um manifesto quebrado pareceria
        // antiquíssimo e o caixa concluiria que está na frente.
        checar(!Atualizacao.TentarLerVersao("ultima", out _), "versão: texto que não é número é recusado");
        checar(!Atualizacao.TentarLerVersao("", out _), "versão: vazio é recusado");
        checar(!Atualizacao.TentarLerVersao(null, out _), "versão: null é recusado");
        checar(!Atualizacao.TentarLerVersao("1.2.3.4.5", out _), "versão: cinco partes é recusado");
        checar(!Atualizacao.TentarLerVersao("-1.0.0", out _), "versão: negativo é recusado");
        checar(Atualizacao.Comparar("0.2.0", "banana") > 0,
            "versão: contra lixo, a versão legível ganha (o caixa não 'atualiza' para o desconhecido)");
    }

    // ═══ 2. MANIFESTO ════════════════════════════════════════════════════════

    private const string UrlManifesto = "https://mmtech.software/pdv/versao.json";

    /// <summary>O JSON que ESTÁ NO AR hoje, byte a byte. Se este teste quebrar, o
    /// contrato do servidor mudou e o caixa da loja parou de atualizar.</summary>
    private const string JsonDeHoje = """
        { "produto": "pdv", "versao": "0.2.0",
          "url": "https://pdv.mmtech.software/download/InstalarPdv.exe",
          "notas": "correções gerais", "obrigatoria": false }
        """;

    private static void LeituraDoManifesto(Action<bool, string> checar)
    {
        var hoje = Atualizacao.LerManifesto(JsonDeHoje, UrlManifesto);
        checar(hoje.Ok is not null && hoje.Erro is null,
            "manifesto: o contrato que JÁ ESTÁ NO AR é aceito (sem sha256 e sem tamanho)");
        checar(hoje.Ok!.Versao == "0.2.0" && !hoje.Ok.Obrigatoria && hoje.Ok.Sha256 is null,
            "manifesto: versão, obrigatoria e a ausência de hash chegam como estão");

        var completo = Atualizacao.LerManifesto("""
            { "produto": "pdv", "versao": "0.3.0",
              "url": "https://pdv.mmtech.software/download/InstalarPdv.exe",
              "notas": "n", "obrigatoria": true,
              "sha256": "0000000000000000000000000000000000000000000000000000000000000abc",
              "tamanho": 265123456 }
            """, UrlManifesto);
        checar(completo.Ok is { Obrigatoria: true, Tamanho: 265123456 } && completo.Ok.Sha256!.Length == 64,
            "manifesto: o contrato NOVO (com sha256 e tamanho) é lido inteiro");

        // Cada recusa abaixo é um jeito de o caixa da loja acabar executando, como
        // administrador, um arquivo que não é o instalador.
        checar(Atualizacao.LerManifesto("""{"produto":"outra-coisa","versao":"9.0.0","url":"https://mmtech.software/x.exe"}""",
                   UrlManifesto).Erro is not null,
            "manifesto: feed de OUTRO produto é recusado (config apontada para o lugar errado)");

        checar(Atualizacao.LerManifesto("""{"produto":"pdv","versao":"9.0.0","url":"http://pdv.mmtech.software/x.exe"}""",
                   UrlManifesto).Erro is not null,
            "manifesto: http:// puro é recusado — executável só desce cifrado");

        checar(Atualizacao.LerManifesto("""{"produto":"pdv","versao":"9.0.0","url":"https://cdn-qualquer.com/x.exe"}""",
                   UrlManifesto).Erro is not null,
            "manifesto: instalador hospedado FORA do domínio do manifesto é recusado");

        checar(Atualizacao.LerManifesto("""{"produto":"pdv","versao":"9.0.0","url":"https://pdv.mmtech.software/x.exe","sha256":"abc"}""",
                   UrlManifesto).Erro is not null,
            "manifesto: sha256 malformado é recusado (hash torto travaria a loja para sempre)");

        checar(Atualizacao.LerManifesto("""{"produto":"pdv","versao":"amanha","url":"https://pdv.mmtech.software/x.exe"}""",
                   UrlManifesto).Erro is not null,
            "manifesto: versão ilegível é recusada");

        checar(Atualizacao.LerManifesto("""{"produto":"pdv","versao":"9.0.0"}""", UrlManifesto).Erro is not null,
            "manifesto: sem url não há o que baixar");

        var html = Atualizacao.LerManifesto("<html><body>Faça login no wi-fi</body></html>", UrlManifesto);
        checar(html.Erro is not null && html.Erro.Contains("wi-fi"),
            "manifesto: HTML de portal cativo é explicado como wi-fi, não como 'JSON inválido'");

        checar(Atualizacao.LerManifesto("", UrlManifesto).Erro is not null, "manifesto: resposta vazia é erro");
        checar(Atualizacao.LerManifesto("[1,2,3]", UrlManifesto).Erro is not null, "manifesto: lista não é manifesto");

        checar(Atualizacao.LerManifesto("""{"produto":"pdv","versao":"9.0.0","url":"https://pdv.mmtech.software/x.exe","obrigatoria":"true"}""",
                   UrlManifesto).Ok!.Obrigatoria,
            "manifesto: obrigatoria escrita com aspas ainda é obrigatória (quem publica é gente)");

        // O nome do arquivo baixado vem da versão, que vem da REDE.
        checar(!Atualizacao.Seguro("../../Windows/System32/x").Contains('/')
            && !Atualizacao.Seguro(@"..\..\x").Contains('\\'),
            "manifesto: versão não vira caminho — nada escreve fora da pasta temporária");
    }

    // ═══ 3. PORTÕES ══════════════════════════════════════════════════════════

    private static void Portoes(Action<bool, string> checar)
    {
        var livre = new Atualizacao.EstadoDoCaixa();
        checar(Atualizacao.Impede(livre) == Atualizacao.Impedimento.Nenhum,
            "portão: caixa parado pode atualizar");

        checar(Atualizacao.Impede(livre with { ItensNaComanda = 1 }) == Atualizacao.Impedimento.ComandaAberta,
            "portão: UM item na comanda já barra (é um cliente no balcão)");
        checar(Atualizacao.Impede(livre with { MaquininhaOcupada = true }) == Atualizacao.Impedimento.MaquininhaOcupada,
            "portão: maquininha ocupada barra");
        checar(Atualizacao.Impede(livre with { CobrancasNoPinpad = 1 }) == Atualizacao.Impedimento.CobrancaNoPinpad,
            "portão: cobrança sem resposta no pinpad barra (o cliente pode ter pago)");
        checar(Atualizacao.Impede(livre with { PapeisNaFila = 2 }) == Atualizacao.Impedimento.PapelNaFila,
            "portão: papel na fila barra — reiniciar comeria o cupom do cliente");

        // As duas decisões que o dono pediu para justificar:
        checar(Atualizacao.Impede(livre with { CaixaAberto = true }) == Atualizacao.Impedimento.Nenhum,
            "portão: CAIXA ABERTO NÃO barra — a sessão do turno mora no ProgramData e volta inteira");
        checar(Atualizacao.Impede(livre with { PapeisNaFila = -1 }) == Atualizacao.Impedimento.Nenhum,
            "portão: fila ILEGÍVEL não barra — 'não sei' virar 'não pode' mata o botão por causa de driver");
        checar(Atualizacao.Impede(livre with { VendasPorSubir = 40 }) == Atualizacao.Impedimento.Nenhum,
            "portão: venda na fila para o painel não barra (ela fica no banco e sobe depois)");

        // Recusa sem saída é recusa que se aprende a ignorar.
        foreach (var i in new[] { Atualizacao.Impedimento.ComandaAberta, Atualizacao.Impedimento.MaquininhaOcupada,
                                  Atualizacao.Impedimento.CobrancaNoPinpad, Atualizacao.Impedimento.PapelNaFila })
        {
            var (t, m) = Atualizacao.Explicar(i, livre with { ItensNaComanda = 3, CobrancasNoPinpad = 1, PapeisNaFila = 1 });
            checar(t.Length > 0 && m.Contains("O QUE FAZER"), $"portão: a recusa por {i} diz o que fazer para destravar");
        }
    }

    // ═══ 4. A DECISÃO ════════════════════════════════════════════════════════

    private static void ADecisao(Action<bool, string> checar)
    {
        var livre = new Atualizacao.EstadoDoCaixa();
        var nova = Atualizacao.LerManifesto("""
            { "produto":"pdv", "versao":"0.10.0", "url":"https://pdv.mmtech.software/InstalarPdv.exe",
              "notas":"conserta o troco em dinheiro", "obrigatoria":false }
            """, UrlManifesto);

        var d = Atualizacao.Decidir(livre, "0.9.0", nova);
        checar(d.Situacao == Atualizacao.Situacao.Disponivel,
            "decisão: 0.9.0 instalado + 0.10.0 no servidor = TEM atualização (aqui o bug de string mataria)");
        checar(d.Mensagem.Contains("0.9.0") && d.Mensagem.Contains("0.10.0"),
            "decisão: o texto diz de onde para onde");
        checar(d.Mensagem.Contains("conserta o troco"), "decisão: as notas da versão chegam ao operador");
        checar(d.Mensagem.Contains("fecha e abre"), "decisão: o operador fica sabendo que o caixa vai reiniciar");
        checar(d.Mensagem.Contains("NÃO se perde"), "decisão: e que as vendas não se perdem");

        checar(Atualizacao.Decidir(livre, "0.10.0", nova).Situacao == Atualizacao.Situacao.EmDia,
            "decisão: mesma versão = em dia");
        checar(Atualizacao.Decidir(livre, "0.11.0", nova).Situacao == Atualizacao.Situacao.EmDia,
            "decisão: instalado MAIS NOVO que o servidor = em dia (máquina de teste não faz downgrade)");

        // Caixa aberto: avisa, não bloqueia — e o aviso fala do que o operador VIVE.
        var comTurno = Atualizacao.Decidir(livre with { CaixaAberto = true }, "0.9.0", nova);
        checar(comTurno.Situacao == Atualizacao.Situacao.Disponivel && comTurno.Mensagem.Contains("PIN"),
            "decisão: caixa aberto libera a atualização e avisa que vai precisar do PIN de novo");

        var comFila = Atualizacao.Decidir(livre with { VendasPorSubir = 3 }, "0.9.0", nova);
        checar(comFila.Mensagem.Contains("3 vendas ainda não subiram"),
            "decisão: venda pendente é dita e explicada, não escondida");

        // "obrigatoria": true muda a CONVERSA — e não muda quem decide.
        var obr = Atualizacao.LerManifesto("""
            { "produto":"pdv", "versao":"0.10.0", "url":"https://pdv.mmtech.software/InstalarPdv.exe",
              "obrigatoria":true }
            """, UrlManifesto);
        var o = Atualizacao.Decidir(livre, "0.9.0", obr);
        checar(o.Obrigatoria && o.Titulo.Contains("obrigatória"), "obrigatória: o título muda");
        checar(o.Mensagem.Contains("OBRIGATÓRIA") && o.Mensagem.Contains("gerente"),
            "obrigatória: o texto diz o que é e a quem recorrer");
        checar(o.TextoNao == "Não posso agora",
            "obrigatória: recusar deixa de ser 'agora não' e vira uma frase que se leva ao gerente");
        checar(o.TextoNao.Length > 0 && o.Situacao == Atualizacao.Situacao.Disponivel,
            "obrigatória: AINDA DÁ PARA RECUSAR — JSON da internet não reinicia caixa com cliente no balcão");

        // Portão ganha de tudo, inclusive de obrigatória.
        var barrado = Atualizacao.Decidir(livre with { ItensNaComanda = 2 }, "0.9.0", obr);
        checar(barrado.Situacao == Atualizacao.Situacao.Impedido,
            "decisão: comanda aberta barra até a atualização OBRIGATÓRIA");

        var erro = Atualizacao.Decidir(livre, "0.9.0", new Atualizacao.LeituraManifesto(null, "servidor fora"));
        checar(erro.Situacao == Atualizacao.Situacao.Erro && erro.Mensagem.Contains("continua funcionando"),
            "decisão: falha de rede vira recado calmo — o caixa não foi alterado");
    }

    // ═══ 5. PROGRESSO ════════════════════════════════════════════════════════

    private static void MensagemDeProgresso(Action<bool, string> checar)
    {
        var meio = Atualizacao.TextoDoProgresso(new Atualizacao.Andamento(52_428_800, 262_144_000));
        checar(meio.Contains("50,0 MB") && meio.Contains("250,0 MB") && meio.Contains("20%"),
            "progresso: MB baixados, MB totais e porcentagem (em pt-BR, com vírgula)");

        var semTotal = Atualizacao.TextoDoProgresso(new Atualizacao.Andamento(1_048_576, null));
        checar(semTotal.Contains("1,0 MB") && !semTotal.Contains('%'),
            "progresso: sem Content-Length não inventa porcentagem — diz só o que sabe");

        checar(new Atualizacao.Andamento(10, 0).Porcento is null,
            "progresso: total zero não vira divisão por zero");
    }

    // ═══ 6. INTEGRIDADE ══════════════════════════════════════════════════════

    private static void Integridade(Action<bool, string> checar, string raiz)
    {
        var bom = Path.Combine(raiz, "bom.exe");
        File.WriteAllBytes(bom, FabricarExe(2_000_000));
        var sha = Atualizacao.Sha256Do(bom);

        var comHash = Manifesto(sha: sha);
        checar(Atualizacao.Conferir(bom, comHash, null) is null, "integridade: arquivo certo com hash certo passa");

        var hashErrado = Manifesto(sha: new string('a', 64));
        checar(Atualizacao.Conferir(bom, hashErrado, null) is not null,
            "integridade: hash diferente REPROVA (é a única defesa contra troca de arquivo)");

        // Sem hash: o plano B declarado. Cada linha aqui pega uma falha real.
        var sem = Manifesto();
        checar(Atualizacao.Conferir(bom, sem, null) is null, "integridade: sem hash, um exe plausível passa");
        checar(Atualizacao.Conferir(bom, sem, 2_000_001) is not null,
            "integridade: sem hash, Content-Length que não bate reprova (download interrompido)");
        checar(Atualizacao.Conferir(bom, Manifesto(tamanho: 999), null) is not null,
            "integridade: tamanho anunciado no manifesto que não bate reprova");

        var html = Path.Combine(raiz, "erro.html");
        File.WriteAllText(html, "<html><h1>404 Not Found</h1></html>");
        checar(Atualizacao.Conferir(html, sem, null) is not null,
            "integridade: página de erro servida com 200 é reprovada (pequena demais)");

        var grandeMasLixo = Path.Combine(raiz, "lixo.bin");
        File.WriteAllBytes(grandeMasLixo, Encoding.UTF8.GetBytes(new string('x', 2_000_000)));
        checar(Atualizacao.Conferir(grandeMasLixo, sem, null) is not null,
            "integridade: 2 MB que não são executável do Windows são reprovados");
        checar(!Atualizacao.EhExecutavelWindows(grandeMasLixo) && Atualizacao.EhExecutavelWindows(bom),
            "integridade: MZ + cabeçalho PE separam programa de lixo");

        var truncado = Path.Combine(raiz, "truncado.exe");
        var bytes = FabricarExe(2_000_000);
        File.WriteAllBytes(truncado, bytes[..1_500_000]);
        checar(Atualizacao.Conferir(truncado, comHash, null) is not null,
            "integridade: metade do arquivo certo NÃO passa pelo hash");

        checar(Atualizacao.Conferir(Path.Combine(raiz, "nao-existe.exe"), sem, null) is not null,
            "integridade: arquivo que sumiu não vira instalação");
    }

    // ═══ 7. DOWNLOAD ═════════════════════════════════════════════════════════

    private static async Task Download(Action<bool, string> checar, string raiz)
    {
        var conteudo = FabricarExe(3_000_000);
        var sha = Sha256De(conteudo);

        // ── caminho feliz
        var srv = new ServidorDeMentira { Arquivo = conteudo };
        using (var http = new HttpClient(srv))
        {
            var pasta = Pasta(raiz, "feliz");
            var relatos = new List<Atualizacao.Andamento>();
            var b = await Atualizacao.BaixarAsync(http, Manifesto(sha: sha, tamanho: conteudo.Length), pasta,
                new Progress<Atualizacao.Andamento>(a => { lock (relatos) relatos.Add(a); }));
            checar(b.Ok && b.Bytes == conteudo.Length, "download: baixa o arquivo inteiro e aprova");
            checar(b.Caminho!.StartsWith(pasta) && b.Caminho.EndsWith(".exe"),
                "download: o arquivo fica na pasta TEMPORÁRIA, nunca por cima do exe em uso");
            checar(File.ReadAllBytes(b.Caminho).SequenceEqual(conteudo), "download: byte a byte igual ao servidor");
            checar(!Directory.EnumerateFiles(pasta, "*.parcial").Any(), "download: o parcial some quando termina");
            lock (relatos) checar(relatos.Count > 1 && relatos[^1].Baixados == conteudo.Length,
                "download: o progresso anda e termina no total (a barra não fica parada)");
        }

        // ── manifesto pelo HttpClient (a consulta inteira, do jeito que a tela faz)
        srv = new ServidorDeMentira { Arquivo = conteudo, Json = JsonDeHoje };
        using (var http = new HttpClient(srv))
        {
            var l = await Atualizacao.ConsultarAsync(http, UrlManifesto);
            checar(l.Ok is { Versao: "0.2.0" }, "consulta: o versao.json de hoje vai e volta pelo HttpClient");
        }

        srv = new ServidorDeMentira { Status = HttpStatusCode.InternalServerError };
        using (var http = new HttpClient(srv))
        {
            var l = await Atualizacao.ConsultarAsync(http, UrlManifesto);
            checar(l.Erro is not null && l.Erro.Contains("500"),
                "consulta: servidor com erro vira recado, não exceção na frente de caixa");
        }

        // ── 404: é EXATAMENTE o estado do servidor hoje (a url ainda não foi publicada)
        srv = new ServidorDeMentira { Arquivo = conteudo, StatusDoArquivo = HttpStatusCode.NotFound };
        using (var http = new HttpClient(srv))
        {
            var b = await Atualizacao.BaixarAsync(http, Manifesto(sha: sha), Pasta(raiz, "404"));
            checar(!b.Ok && b.Erro!.Contains("404") && b.Erro.Contains("suporte"),
                "download: instalador não publicado (404) vira mensagem tratável, não travamento");
        }

        // ── RETOMADA: metade baixada, rede voltou. Sem isto, wi-fi de loja = nunca atualiza.
        srv = new ServidorDeMentira { Arquivo = conteudo };
        using (var http = new HttpClient(srv))
        {
            var pasta = Pasta(raiz, "retoma");
            var parcial = Path.Combine(pasta, "InstalarPdv-9.9.9.parcial");
            File.WriteAllBytes(parcial, conteudo[..1_000_000]);      // o que sobrou da tentativa anterior

            var b = await Atualizacao.BaixarAsync(http, Manifesto(sha: sha), pasta);
            checar(b.Ok && b.Retomado, "retomada: continua de onde parou em vez de baixar tudo de novo");
            checar(srv.UltimoRangeDe == 1_000_000, "retomada: pede ao servidor exatamente a partir do byte que falta");
            checar(srv.BytesEnviados == conteudo.Length - 1_000_000, "retomada: e o servidor só manda o que falta");
            checar(File.ReadAllBytes(b.Caminho!).SequenceEqual(conteudo),
                "retomada: o arquivo colado é IDÊNTICO ao original (nada de Frankenstein)");
        }

        // ── servidor que ignora Range: recomeça do zero, em silêncio e correto
        srv = new ServidorDeMentira { Arquivo = conteudo, AceitaRange = false };
        using (var http = new HttpClient(srv))
        {
            var pasta = Pasta(raiz, "sem-range");
            File.WriteAllBytes(Path.Combine(pasta, "InstalarPdv-9.9.9.parcial"), conteudo[..1_000_000]);
            var b = await Atualizacao.BaixarAsync(http, Manifesto(sha: sha), pasta);
            checar(b.Ok && !b.Retomado && File.ReadAllBytes(b.Caminho!).SequenceEqual(conteudo),
                "retomada: servidor que ignora Range faz recomeçar do zero — nunca concatenar errado");
        }

        // ── pedaço podre (energia caiu no meio da escrita): o hash é quem pega
        srv = new ServidorDeMentira { Arquivo = conteudo };
        using (var http = new HttpClient(srv))
        {
            var pasta = Pasta(raiz, "podre");
            var podre = conteudo[..1_000_000];
            podre[999_999] = 0;                                       // um byte zerado no fim do pedaço
            File.WriteAllBytes(Path.Combine(pasta, "InstalarPdv-9.9.9.parcial"), podre);
            var b = await Atualizacao.BaixarAsync(http, Manifesto(sha: sha), pasta);
            checar(!b.Ok, "retomada: pedaço corrompido é REPROVADO pelo hash, não instalado");
            checar(!Directory.EnumerateFiles(pasta, "*.parcial").Any(),
                "retomada: e o pedaço reprovado é APAGADO (senão o erro vira eterno)");
        }

        // ── arquivo trocado no servidor: 416 no Range → recomeça limpo
        srv = new ServidorDeMentira { Arquivo = conteudo, RangeForaDeFaixa = true };
        using (var http = new HttpClient(srv))
        {
            var pasta = Pasta(raiz, "416");
            File.WriteAllBytes(Path.Combine(pasta, "InstalarPdv-9.9.9.parcial"), conteudo[..1_000_000]);
            var b = await Atualizacao.BaixarAsync(http, Manifesto(sha: sha), pasta);
            checar(b.Ok && File.ReadAllBytes(b.Caminho!).SequenceEqual(conteudo),
                "retomada: 416 (pedaço não serve mais) recomeça do zero e termina certo");
        }

        // ── o servidor mente sobre o tamanho: recusa ANTES de gastar a internet da loja
        srv = new ServidorDeMentira { Arquivo = conteudo };
        using (var http = new HttpClient(srv))
        {
            var b = await Atualizacao.BaixarAsync(http, Manifesto(sha: sha, tamanho: 999_999_999), Pasta(raiz, "mentiu"));
            checar(!b.Ok && srv.BytesEnviados == 0,
                "download: tamanho anunciado ≠ tamanho oferecido para a recusa antes de baixar um byte");
        }

        // ── página de erro grande servida com 200
        srv = new ServidorDeMentira { Arquivo = Encoding.UTF8.GetBytes(new string('x', 2_000_000)) };
        using (var http = new HttpClient(srv))
        {
            var b = await Atualizacao.BaixarAsync(http, Manifesto(), Pasta(raiz, "html"));
            checar(!b.Ok, "download: 2 MB que não são executável não viram instalação (sem hash, é o PE que salva)");
        }

        // ── REDE MORTA no meio: falha limpa, e o pedaço fica para a próxima
        srv = new ServidorDeMentira { Arquivo = conteudo, TravaDepoisDe = 500_000 };
        using (var http = new HttpClient(srv))
        {
            var pasta = Pasta(raiz, "travou");
            var b = await Atualizacao.BaixarAsync(http, Manifesto(sha: sha), pasta, null, default,
                esperaSemBytes: TimeSpan.FromMilliseconds(400));
            checar(!b.Ok && b.Erro!.Contains("continua de onde parou"),
                "rede: silêncio prolongado desiste e PROMETE a retomada (não é 'tente tudo de novo')");
            checar(File.Exists(Path.Combine(pasta, "InstalarPdv-9.9.9.parcial")),
                "rede: o pedaço baixado SOBREVIVE à queda — é o que torna a promessa verdadeira");
            checar(!File.Exists(Path.Combine(pasta, "InstalarPdv-9.9.9.exe")),
                "rede: e nada pronto é entregue no meio do caminho");
        }

        // ── o operador cancelou (chegou cliente): mesma regra
        srv = new ServidorDeMentira { Arquivo = conteudo, TravaDepoisDe = 500_000 };
        using (var http = new HttpClient(srv))
        {
            var pasta = Pasta(raiz, "cancelou");
            using var cts = new CancellationTokenSource(300);
            var b = await Atualizacao.BaixarAsync(http, Manifesto(sha: sha), pasta, null, cts.Token);
            checar(!b.Ok && b.Erro!.Contains("cancelado"), "cancelar: o operador cancela e ouve que foi cancelado");
            checar(File.Exists(Path.Combine(pasta, "InstalarPdv-9.9.9.parcial")),
                "cancelar: o que baixou fica guardado — cancelar não é jogar 3 MB fora");
        }

        // ── já baixado (o dono recusou o UAC e voltou): não baixa 265 MB de novo
        srv = new ServidorDeMentira { Arquivo = conteudo };
        using (var http = new HttpClient(srv))
        {
            var pasta = Pasta(raiz, "jatem");
            File.WriteAllBytes(Path.Combine(pasta, "InstalarPdv-9.9.9.exe"), conteudo);
            var b = await Atualizacao.BaixarAsync(http, Manifesto(sha: sha), pasta);
            checar(b.Ok && srv.BytesEnviados == 0,
                "download: instalador já conferido no disco é reaproveitado sem tocar na rede");
        }

        // ── faxina: versão velha não fica ocupando o disco de um PC de loja
        srv = new ServidorDeMentira { Arquivo = conteudo };
        using (var http = new HttpClient(srv))
        {
            var pasta = Pasta(raiz, "faxina");
            File.WriteAllBytes(Path.Combine(pasta, "InstalarPdv-9.9.8.exe"), conteudo);
            await Atualizacao.BaixarAsync(http, Manifesto(sha: sha), pasta);
            checar(!File.Exists(Path.Combine(pasta, "InstalarPdv-9.9.8.exe")),
                "download: o instalador da versão anterior é apagado (disco de loja é pequeno)");
        }
    }

    // ═══ APOIO ═══════════════════════════════════════════════════════════════

    private static Atualizacao.Manifesto Manifesto(string? sha = null, long? tamanho = null) =>
        new("9.9.9", "https://pdv.mmtech.software/download/InstalarPdv.exe", null, false, sha, tamanho);

    private static string Pasta(string raiz, string nome)
    {
        var p = Path.Combine(raiz, nome);
        Directory.CreateDirectory(p);
        return p;
    }

    private static string Sha256De(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>
    /// Um "executável de Windows" plausível: MZ no começo, e_lfanew apontando para um
    /// PE\0\0. O miolo é ruído determinístico — o que se testa é a CANALIZAÇÃO dos
    /// bytes, não o programa.
    /// </summary>
    private static byte[] FabricarExe(int tamanho)
    {
        var b = new byte[tamanho];
        new Random(42).NextBytes(b);
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        const int pe = 0x100;
        BitConverter.GetBytes(pe).CopyTo(b, 0x3C);
        b[pe] = (byte)'P'; b[pe + 1] = (byte)'E'; b[pe + 2] = 0; b[pe + 3] = 0;
        return b;
    }

    /// <summary>
    /// O servidor de atualização, de mentira e dentro do processo. NENHUM soquete é
    /// aberto: o HttpClient fala com este handler. É o que permite ensaiar 404, Range,
    /// 416 e rede morta sem depender da internet de quem roda a suíte — e sem nunca
    /// chegar perto do InstalarPdv.exe de verdade.
    /// </summary>
    private sealed class ServidorDeMentira : HttpMessageHandler
    {
        public byte[] Arquivo = Array.Empty<byte>();
        public string? Json;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public HttpStatusCode StatusDoArquivo = HttpStatusCode.OK;
        public bool AceitaRange = true;
        public bool RangeForaDeFaixa;
        /// <summary>Depois de N bytes, o servidor emudece (não fecha, não responde).</summary>
        public int? TravaDepoisDe;

        public long? UltimoRangeDe;
        public long BytesEnviados;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.RequestUri!.AbsolutePath.EndsWith(".json"))
                return Task.FromResult(Status != HttpStatusCode.OK
                    ? new HttpResponseMessage(Status)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(Json ?? "", Encoding.UTF8, "application/json") });

            if (StatusDoArquivo != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(StatusDoArquivo));

            var de = 0L;
            if (req.Headers.Range?.Ranges.FirstOrDefault()?.From is { } f)
            {
                UltimoRangeDe = f;
                if (RangeForaDeFaixa)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
                if (AceitaRange) de = f;
            }

            var corpo = Arquivo[(int)de..];
            var parcial = de > 0;
            // ⚠️ BytesEnviados conta o que foi LIDO do corpo, não o que foi oferecido nos
            // cabeçalhos. A primeira versão contava aqui em cima — e com isso o teste de
            // "recusa antes de baixar um byte" acusava falha num código que estava certo:
            // ele lê o Content-Length, discorda, e devolve sem tocar no corpo. Instrumento
            // errado reprovando código certo é o jeito mais rápido de consertar o que
            // funciona.
            var r = new HttpResponseMessage(parcial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(
                    new StreamQueEmudece(corpo, TravaDepoisDe, n => BytesEnviados += n)),
            };
            r.Content.Headers.ContentLength = corpo.Length;
            if (parcial)
                r.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(de, Arquivo.Length - 1, Arquivo.Length);
            return Task.FromResult(r);
        }
    }

    /// <summary>
    /// O corpo da resposta. Conta o que foi realmente lido e, quando
    /// <paramref name="travaDepoisDe"/> vem preenchido, entrega esse tanto e fica MUDA
    /// para sempre — o wi-fi da loja que não caiu, só parou de responder. É a falha que
    /// um prazo TOTAL não distingue de um download legitimamente lento, e é por isso
    /// que o código de produção conta silêncio, não duração.
    /// </summary>
    private sealed class StreamQueEmudece : Stream
    {
        private readonly byte[] _dados;
        private readonly int _ate;
        private readonly bool _emudece;
        private readonly Action<int> _contou;
        private int _pos;

        public StreamQueEmudece(byte[] dados, int? travaDepoisDe, Action<int> contou)
        {
            _dados = dados;
            _emudece = travaDepoisDe is not null;
            _ate = Math.Min(travaDepoisDe ?? dados.Length, dados.Length);
            _contou = contou;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> destino, CancellationToken ct)
        {
            if (_pos < _ate)
            {
                var n = Math.Min(destino.Length, _ate - _pos);
                _dados.AsMemory(_pos, n).CopyTo(destino);
                _pos += n;
                _contou(n);
                return n;
            }
            // Sem trava, acabou é acabou: devolver 0 é o EOF que fecha o laço de
            // download. (Emudecer aqui faria TODO download normal esperar o prazo de
            // silêncio — foi exatamente o que aconteceu na primeira versão deste fake.)
            if (!_emudece) return 0;
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c), default).AsTask().Result;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _dados.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
