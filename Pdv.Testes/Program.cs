using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;
using Pdv.Testes;

// Testes do ciclo do dinheiro. Sem framework: `dotnet run` e código != 0 se falhar.
// O que está aqui é o que, se quebrar, faz a loja perder dinheiro ou o operador
// levar culpa por diferença que não é dele.

// Modo semente: carrega um catálogo de arquivo no banco REAL do caixa. Serve pra
// preparar uma máquina sem depender de credencial da nuvem (instalação, demo, suporte).
//   dotnet run -- semear caminho\produtos.json
// Modo simulador: PayGo Windows DE MENTIRA numa pasta fixa, ao lado do PDV de verdade.
// Ambiente de teste da tela inteira (venda → comprovante → CNF, estorno/CNC, ADM,
// pendência…) sem credencial da PayGo. Aprova tudo por padrão; para roteirizar o próximo
// desfecho, escreva uma palavra por linha em <pasta>\roteiro.txt:
//   recusar | divergente | 729=1 | sumir | semsts | pendencia | inconsistente | cancelar
//   dotnet run --project Pdv.Testes -- --fake-paygo C:\PAYGO\SIM
// Modo SONDA da 2ª instância (usado por TestesInstanciaUnica): este processo se
// comporta como um Pdv.exe que acabou de subir — pega a trava de instância única e,
// se conseguir, roda o religamento do TEF do boot (o mesmo SQL de
// Servicos.ResolverPendenciasTefAsync, com a mesma referência de tempo: o START
// DESTE PROCESSO). Recusado pela trava, sai sem encostar no banco.
//   Pdv.Testes.exe --sonda-2a-instancia <banco.db> <nome-da-trava>
// Modo EMPACOTAR: monta o instalador final pendurando a pasta do PDV e o paygo.exe
// na cauda de uma copia do InstalarPdv.exe ja publicado. Chamado por
// scripts\gerar-instalador.ps1.
//   Pdv.Testes.exe --empacotar <exe-base> <pasta-pdv> <paygo.exe|-> <saida.exe>
//
// ⚠️ POR QUE ISTO MORA AQUI E NAO NO PROPRIO INSTALADOR. O InstalarPdv.exe tem
// requireAdministrator no manifesto (ele grava em Program Files e no HKLM), e o
// Windows recusa inicia-lo de um shell comum: "a operacao solicitada requer
// elevacao". Empacotar e passo de BUILD, nao de instalacao — nao pode exigir UAC na
// maquina de quem compila. O codigo do formato continua unico, em
// Pdv.Instalador\Pacote.cs, compilado aqui por <Compile Include>: quem grava o
// pacote e quem o le sao literalmente o mesmo fonte.
// ⚠️ O casamento e so pelo args[0]. Exigir a aridade AQUI faria o modo cair em
// silencio na suite de testes quando um argumento viesse errado — e foi exatamente o
// que aconteceu: o script de build achou que empacotou e o que rodou foram os 1161
// testes. Passo de build tem que falhar alto.
if (args.Length >= 1 && args[0] == "--empacotar")
{
    if (args.Length < 5)
    {
        Console.WriteLine("uso: Pdv.Testes.exe --empacotar <exe-base> <pasta-pdv> <paygo.exe|-> <saida.exe>");
        return 2;
    }

    var erroPacote = Pdv.Instalador.Pacote.Empacotar(
        exeBase: args[1],
        pastaPdv: args[2],
        paygoExe: args[3] == "-" ? null : args[3],
        saida: args[4],
        progresso: Console.WriteLine);

    if (erroPacote is not null) { Console.WriteLine("FALHOU: " + erroPacote); return 1; }

    // Reabre o que acabou de gravar: empacotar sem conferir e entregar para a loja um
    // exe que so falha la.
    using var confere = File.OpenRead(args[4]);
    var cauda = Pdv.Instalador.Pacote.LerTrailer(confere);
    if (cauda is null) { Console.WriteLine("FALHOU: o trailer nao le de volta."); return 1; }
    Console.WriteLine($"ok: {args[4]} ({new FileInfo(args[4]).Length / 1024 / 1024} MB, "
                    + $"payload {cauda.Tamanho / 1024 / 1024} MB)");
    return 0;
}

// Modo CONFERIR: abre o instalador JA GERADO, extrai a cauda de verdade num temporario
// e verifica que o que saiu de la e uma pasta de PDV instalavel.
//   Pdv.Testes.exe --conferir-pacote <instalador.exe>
// Ler o trailer de volta so prova que a aritmetica fecha. Descompactar prova que o zip
// esta inteiro — que e o que a loja vai descobrir no primeiro clique, se ninguem
// descobrir antes.
if (args.Length >= 2 && args[0] == "--conferir-pacote")
{
    var tmp = Path.Combine(Path.GetTempPath(), "confere-pacote-" + Guid.NewGuid().ToString("N")[..8]);
    try
    {
        var falha = Pdv.Instalador.Pacote.Extrair(tmp, null, args[1]);
        if (falha is not null) { Console.WriteLine("FALHOU: " + falha); return 1; }

        var pdvDentro = Path.Combine(tmp, "pdv");
        if (Pdv.Instalador.Instalacao.ConferirOrigem(pdvDentro) is { } incompleto)
        {
            Console.WriteLine("FALHOU: o pacote abriu mas o PDV la dentro esta incompleto — " + incompleto);
            return 1;
        }

        var arquivos = Pdv.Instalador.Instalacao.ArquivosParaCopiar(pdvDentro).Count;
        var temPayGo = File.Exists(Path.Combine(tmp, "paygo.exe"));
        Console.WriteLine($"ok: o pacote abre — {arquivos} arquivos do PDV"
                        + (temPayGo ? " + paygo.exe" : " (sem PayGo)"));
        return 0;
    }
    finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { } }
}

// Modo FOTO: fotografa a tela de venda numa resolução (ver Pdv.Testes/FotoVenda.cs).
//   Pdv.Testes.exe --foto-venda saida.png 1024 768 [categoria] [claro|escuro] [item;item]
if (args.Length >= 4 && args[0] == "--foto-venda")
    return FotoVenda.Rodar(args);

if (args.Length >= 3 && args[0] == "--sonda-2a-instancia")
{
    using var trava = InstanciaUnica.Tentar(args[2]);
    if (trava is null) return Pdv.Testes.TestesInstanciaUnica.Recusada;

    using var cxSonda = Banco.Abrir(args[1]);
    var agoraSonda = DateTime.Now.ToString("o");
    var bootSonda = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToString("o");
    cxSonda.Execute("""
        UPDATE tef_transacao
           SET situacao = 'orfa', motivo = 'PDV reiniciou durante a cobrança; confira no PayGo', atualizado_em = @Em
         WHERE charge_id LIKE 'paygo-%' AND situacao IN ('criando','aguardando') AND criado_em < @Inicio
        """, new { Em = agoraSonda, Inicio = bootSonda });
    cxSonda.Execute("""
        UPDATE tef_transacao
           SET situacao = 'orfa', motivo = 'PDV reiniciou antes de o ControlPay responder', atualizado_em = @Em
         WHERE charge_id LIKE 'cpay-%' AND situacao = 'criando' AND criado_em < @Inicio
        """, new { Em = agoraSonda, Inicio = bootSonda });
    Console.WriteLine($"sonda: bootei com a trava '{args[2]}' e rodei o religamento");
    return 0;
}

// Modo SONDA do 2º PDV COM COMANDA (usado por TestesRascunho): este processo grava um
// rascunho no banco do caixa e FICA VIVO segurando a comanda, exatamente como faria a
// 2ª tela de venda que a trava de instância única deveria ter impedido. Fica esperando
// no stdin; o pai o mata quando quer simular a queda de energia daquele processo.
//   Pdv.Testes.exe --sonda-rascunho <banco.db> <sessaoId> <operadorId> <nome do item>
if (args.Length >= 5 && args[0] == "--sonda-rascunho")
{
    using var cxRas = Banco.Abrir(args[1]);
    // Gravar só usa Id da sessão e Id do operador; o resto do registro é enfeite aqui.
    var sesRas = new Sessao(args[2], "", args[3], "", DateTime.Now, Dinheiro.Zero);
    var opRas = new Operador(args[3], "sonda", "operador");
    Rascunho.Gravar(cxRas, sesRas, opRas,
        new[] { new ItemRascunho("p-sonda", "999", args[4], "Cat", 1000, 1000,
                                 "UN", "19053100", null, "102", 0, null) },
        Dinheiro.Zero);
    Console.WriteLine("ok");
    Console.Out.Flush();
    Console.ReadLine();   // segura a comanda até o pai fechar o stdin (ou matar o processo)
    return 0;
}

if (args.Length >= 2 && args[0] == "--fake-paygo")
{
    var pasta = args[1];
    using var fake = new FakePayGo(pasta) { AtrasoRespostaMs = 1500 };
    fake.Recebeu = c =>
    {
        var cmd = c.GetValueOrDefault("000-000") ?? "?";
        var extra = cmd switch
        {
            "CRT" => $" valor={c.GetValueOrDefault("003-000")} tipo731={c.GetValueOrDefault("731-000")} 732={c.GetValueOrDefault("732-000")} 018={c.GetValueOrDefault("018-000") ?? "-"} 749={c.GetValueOrDefault("749-000")} rede={c.GetValueOrDefault("010-000") ?? "(menu)"}",
            "CNC" => $" valor={c.GetValueOrDefault("003-000")} nsu={c.GetValueOrDefault("012-000")} 027={c.GetValueOrDefault("027-000")}",
            "CNF" or "NCN" => $" 027={c.GetValueOrDefault("027-000") ?? "-"}",
            _ => "",
        };
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} ◀ {cmd} 001={c.GetValueOrDefault("001-000")}{extra}");
    };
    var roteiro = Path.Combine(pasta, "roteiro.txt");
    Console.WriteLine($"PayGo SIMULADO observando {fake.Req}  (Ctrl+C para parar)");
    Console.WriteLine($"Próximos desfechos: escreva em {roteiro} (recusar|divergente|729=1|sumir|semsts|pendencia|inconsistente|cancelar)");
    while (true)
    {
        try
        {
            if (File.Exists(roteiro))
            {
                var linhasRoteiro = File.ReadAllLines(roteiro);
                File.WriteAllText(roteiro, "");
                foreach (var l in linhasRoteiro)
                {
                    var d = l.Trim().ToLowerInvariant() switch
                    {
                        "recusar" => FakePayGo.Desfecho.Recusar,
                        "divergente" => FakePayGo.Desfecho.AprovarValorDivergente,
                        "729=1" => FakePayGo.Desfecho.AprovarSemConfirmacao,
                        "sumir" => FakePayGo.Desfecho.Sumir,
                        "semsts" => FakePayGo.Desfecho.SemSts,
                        "pendencia" => FakePayGo.Desfecho.NegarComPendencia,
                        "inconsistente" => FakePayGo.Desfecho.Inconsistente,
                        "cancelar" => FakePayGo.Desfecho.CancelarNoPinpad,
                        "" => (FakePayGo.Desfecho?)null,
                        _ => FakePayGo.Desfecho.Aprovar,
                    };
                    if (d is { } dd) { fake.Roteiro.Enqueue(dd); Console.WriteLine($"{DateTime.Now:HH:mm:ss} ▶ próximo desfecho: {dd}"); }
                }
            }
        }
        catch { }
        Thread.Sleep(400);
    }
}

if (args.Length >= 2 && args[0] == "semear")
{
    var caminhoJson = args[1];
    using var cxs = Banco.Abrir();
    Banco.Migrar();
    var itens = System.Text.Json.JsonDocument.Parse(File.ReadAllText(caminhoJson)).RootElement;
    var agoraS = DateTime.Now.ToString("o");
    var qtd = 0;
    using (var txs = cxs.BeginTransaction())
    {
        cxs.Execute("UPDATE produto SET ativo = 0", transaction: txs);
        foreach (var p in itens.EnumerateArray())
        {
            string? S(string n) => p.TryGetProperty(n, out var v) && v.ValueKind != System.Text.Json.JsonValueKind.Null
                ? (v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetRawText() : v.GetString()) : null;
            if (!decimal.TryParse(S("unit_price"), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var pr) || pr <= 0) continue;
            cxs.Execute("""
                INSERT INTO produto (id,plu,nome,categoria,preco_cent,unidade,foto_local,ncm,cest,csosn,origem,pesavel,ativo,atualizado)
                VALUES (@Id,@Plu,@Nome,@Cat,@Preco,@Un,@Foto,@Ncm,@Cest,@Csosn,@Orig,0,1,@Em)
                ON CONFLICT(id) DO UPDATE SET plu=@Plu,nome=@Nome,categoria=@Cat,preco_cent=@Preco,
                    unidade=@Un,foto_local=@Foto,ncm=@Ncm,cest=@Cest,csosn=@Csosn,origem=@Orig,ativo=1,atualizado=@Em
                """,
                new { Id = S("id"), Plu = S("degust_code"), Nome = S("name") ?? "PRODUTO",
                      Cat = S("grupo") ?? "Outros", Preco = Dinheiro.DeReais(pr).Centavos,
                      Un = S("unidade") ?? "UN", Foto = S("image_url"),
                      Ncm = S("ncm"), Cest = S("cest"), Csosn = S("csosn"),
                      Orig = int.TryParse(S("origem"), out var oo) ? oo : 0, Em = agoraS }, txs);
            qtd++;
        }
        txs.Commit();
    }
    var categorias = cxs.ExecuteScalar<int>("SELECT COUNT(DISTINCT categoria) FROM produto WHERE ativo=1");
    Console.WriteLine($"{qtd} produtos em {categorias} categorias carregados em {Banco.Arquivo}");

    // Fotos: baixa uma vez e guarda em disco. A grade não pode depender de internet.
    var urls = cxs.Query<string>("SELECT foto_local FROM produto WHERE ativo=1 AND foto_local IS NOT NULL").ToList();
    if (urls.Count > 0)
    {
        Console.WriteLine($"baixando {urls.Count} fotos…");
        var novas = await Fotos.BaixarFaltantesAsync(urls, (i, t) =>
        {
            if (i % 10 == 0 || i == t) Console.Write($"\r  {i}/{t}   ");
        });
        Console.WriteLine($"\r{novas} fotos novas em {Fotos.Pasta}");
    }
    return 0;
}

// Modo simular: N dias de operação REAL num banco paralelo + drenagem contra uma
// nuvem fake com falhas injetadas. É o teste de aceitação antes de operar de verdade.
//   dotnet run -- simular 180
if (args.Length >= 1 && args[0] == "simular")
{
    var diasSim = args.Length >= 2 && int.TryParse(args[1], out var dd) ? dd : 180;
    return await Pdv.Testes.Simulador.RodarAsync(diasSim);
}

// Modo operador: cadastra ou remove um operador no banco local. Instalação e suporte
// precisam disso — máquina nova não tem ninguém cadastrado, e sem operador o caixa
// não abre. Fica no projeto de testes de propósito: não vai junto com o Pdv.exe.
//   dotnet run -- operador "Maria" 1234 supervisor
//   dotnet run -- operador --remover <id>
if (args.Length >= 2 && args[0] == "operador")
{
    Banco.Migrar();
    using var cxo = Banco.Abrir();
    if (args[1] == "--remover")
    {
        // por id ou por nome: no suporte quase nunca se tem o id em mãos
        var n = cxo.Execute("DELETE FROM operador WHERE id = @A OR nome = @A", new { A = args[2] });
        Console.WriteLine(n > 0 ? $"{n} operador(es) removido(s): {args[2]}" : $"não achei \"{args[2]}\"");
        return 0;
    }
    if (args.Length < 3 || !Operadores.PinValido(args[2]))
    {
        Console.WriteLine("uso: operador \"Nome\" <senha 4-6 dígitos> [operador|supervisor|gerente] [cpf]");
        return 1;
    }
    var idNovo = Guid.NewGuid().ToString();
    Operadores.Salvar(cxo, idNovo, args[1], args[2], args.Length > 3 ? args[3] : "operador",
        args.Length > 4 ? args[4] : null);
    Console.WriteLine($"operador \"{args[1]}\" cadastrado — id {idNovo}" +
        (args.Length > 4 ? $" (login por CPF)" : " (login legado por PIN até ganhar CPF)"));
    return 0;
}

// Modo operador-cpf: dá CPF a um operador que já existe (fecha o login legado dele).
//   dotnet run -- operador-cpf "Brenno" 111.444.777-35
if (args.Length >= 3 && args[0] == "operador-cpf")
{
    Banco.Migrar();
    using var cxc2 = Banco.Abrir();
    var cpfNovo = Documentos.SoDigitos(args[2]);
    if (!Documentos.CpfValido(cpfNovo)) { Console.WriteLine("CPF inválido."); return 1; }
    var jaTem = cxc2.ExecuteScalar<string?>(
        "SELECT nome FROM operador WHERE cpf = @C AND ativo = 1", new { C = cpfNovo });
    if (jaTem is not null) { Console.WriteLine($"esse CPF já é de {jaTem}."); return 1; }
    var nAf = cxc2.Execute("UPDATE operador SET cpf = @C WHERE (id = @A OR nome = @A) AND ativo = 1",
        new { C = cpfNovo, A = args[1] });
    Console.WriteLine(nAf > 0
        ? $"CPF vinculado a \"{args[1]}\" — a partir de agora o login dele é CPF + senha."
        : $"não achei operador ativo \"{args[1]}\".");
    return nAf > 0 ? 0 : 1;
}

// Modo loja: corrige o nome exibido do terminal. É só rótulo de tela e cupom não
// fiscal — o nome que vai na NFC-e vem da razão social do CNPJ, no servidor. Existe
// porque trocar isso pela tela de configuração obriga a refazer certificado e CSC.
//   dotnet run -- loja "American Day Savassi"
if (args.Length >= 2 && args[0] == "loja")
{
    Banco.Migrar();
    using var cxl = Banco.Abrir();
    var antes = cxl.ExecuteScalar<string>("SELECT loja_nome FROM terminal LIMIT 1");
    if (antes is null) { Console.WriteLine("nenhum terminal configurado"); return 1; }
    cxl.Execute("UPDATE terminal SET loja_nome = @N WHERE id = 1", new { N = args[1] });
    Console.WriteLine($"loja: \"{antes}\" -> \"{args[1]}\"");
    return 0;
}

// Modo consulta: SELECT no banco REAL do caixa, para suporte e diagnóstico.
// Só leitura — qualquer coisa que não comece com SELECT é recusada, para este atalho
// não virar o caminho por onde alguém "conserta" dado de venda na unha.
//   dotnet run -- consulta "SELECT * FROM venda ORDER BY criada_em DESC LIMIT 5"
if (args.Length >= 2 && args[0] == "consulta")
{
    var sql = args[1].TrimStart();
    if (!sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("só SELECT.");
        return 1;
    }
    using var cxq = Banco.Abrir();
    var registros = cxq.Query(sql).ToList();
    if (registros.Count == 0) { Console.WriteLine("(nenhuma linha)"); return 0; }
    foreach (var reg in registros)
    {
        var d = (IDictionary<string, object>)reg;
        Console.WriteLine(string.Join("  |  ", d.Select(kv => $"{kv.Key}={kv.Value}")));
    }
    Console.WriteLine($"({registros.Count} linha(s))");
    return 0;
}

// Modo destravar-nuvem: nota emitida pela nuvem já está no servidor (a edge
// gravou), mas ficava sincronizada=0 no caixa porque o XML não é local. Marca
// como sincronizada as notas do caminho 'nuvem' — limpa o "(N)" fantasma.
//   dotnet run -- destravar-nuvem
if (args.Length >= 1 && args[0] == "destravar-nuvem")
{
    using var cxd = Banco.Abrir();
    var n = cxd.Execute(
        "UPDATE nfce_emissao SET sincronizada = 1 WHERE caminho = 'nuvem' AND chave IS NOT NULL AND sincronizada = 0");
    Console.WriteLine($"destravadas: {n} nota(s) do caminho nuvem");
    return 0;
}

// Modo cancelar-venda: desfaz uma venda do banco REAL, pelo número do dia.
// Recusa se a nota já foi autorizada — nota emitida sai pela SEFAZ, não por aqui.
//   dotnet run -- cancelar-venda 1 "venda de teste"
if (args.Length >= 3 && args[0] == "cancelar-venda")
{
    using var cxc = Banco.Abrir();
    var alvo = cxc.QueryFirstOrDefault(
        "SELECT id, numero_local, total_cent, fiscal_status FROM venda WHERE numero_local = @N AND status='finalizada' ORDER BY criada_em DESC LIMIT 1",
        new { N = int.Parse(args[1]) });
    if (alvo is null) { Console.WriteLine($"não achei venda finalizada nº {args[1]}"); return 1; }
    try
    {
        Vendas.Cancelar(cxc, (string)alvo.id, "suporte", args[2]);
        Console.WriteLine($"venda nº {alvo.numero_local} ({new Dinheiro((long)alvo.total_cent).Formatado()}) cancelada");
        return 0;
    }
    catch (Exception e) { Console.WriteLine("NÃO cancelei: " + e.Message); return 1; }
}

// Modo admin: redefine a senha de administrador do terminal.
// Vive no projeto de testes (não vai junto com o exe): quem tem acesso físico à máquina
// e ao código-fonte já poderia editar o SQLite na mão — isto só torna o socorro seguro.
//   dotnet run -- admin NovaSenha123
if (args.Length >= 2 && args[0] == "admin")
{
    if (args[1].Length < 4) { Console.WriteLine("mínimo 4 caracteres."); return 1; }
    Banco.Migrar();
    using var cxa = Banco.Abrir();
    var (h, s) = Operadores.GerarHash(args[1]);
    cxa.Execute("""
        INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,ativo,atualizado)
        VALUES ('_admin_','Administrador',@H,@S,'gerente',0,@Em)
        ON CONFLICT(id) DO UPDATE SET pin_hash=@H, pin_salt=@S, atualizado=@Em
        """, new { H = h, S = s, Em = DateTime.Now.ToString("o") });
    Caixa.Auditar(cxa, null, "senha_admin_redefinida", null, null, "via comando de suporte");
    Console.WriteLine("senha de administrador redefinida.");
    return 0;
}

// Modo nota-cancelada: espelha no banco local um cancelamento JÁ CONFIRMADO pela SEFAZ
// (cStat 135/155). Não cancela nada — só registra um fato que aconteceu lá fora.
//   dotnet run -- nota-cancelada <chave de 44>
if (args.Length >= 2 && args[0] == "nota-cancelada")
{
    var ch = args[1].Trim();
    if (ch.Length != 44) { Console.WriteLine("chave deve ter 44 dígitos."); return 1; }
    using var cxn = Banco.Abrir();
    var n1 = cxn.Execute("UPDATE venda SET fiscal_status='cancelada' WHERE nfce_chave = @C", new { C = ch });
    Caixa.Auditar(cxn, null, "nfce_cancelada_sefaz", null, null, $"chave={ch}");
    Console.WriteLine($"{n1} venda(s) marcada(s) com nota cancelada.");
    return 0;
}

// Modo fiscal-csv: exporta o cadastro fiscal dos produtos para a contabilidade conferir.
// O CFOP sai DERIVADO pela mesma regra do motor de emissão (CSOSN 500 → 5405; resto →
// 5102) — é o que realmente vai na nota, não um campo de cadastro. A coluna "atencao"
// aponta o que precisa de olho humano: sem NCM, ST sem CEST, sem CSOSN.
//   dotnet run -- fiscal-csv C:\caminho\produtos-fiscal.csv
if (args.Length >= 2 && args[0] == "fiscal-csv")
{
    Banco.Migrar();
    using var cxf = Banco.Abrir();
    var produtosFiscais = cxf.Query("""
        SELECT COALESCE(plu, id) AS codigo, nome, categoria, ncm, cest, csosn, origem,
               preco_cent, ativo
          FROM produto ORDER BY categoria, nome
        """).ToList();

    var sb = new System.Text.StringBuilder();
    // ; como separador e BOM: é o que o Excel em português abre certo com dois cliques
    sb.AppendLine("codigo;nome_produto;categoria;ncm;cfop;csosn;cest;origem;preco;ativo;atencao");
    static string Csv(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
    var problemas = 0;
    foreach (var l in produtosFiscais)
    {
        var csosn = (l.csosn as string)?.Trim() ?? "";
        var cest = (l.cest as string)?.Trim() ?? "";
        var ncm = (l.ncm as string)?.Trim() ?? "";
        var cfop = csosn == "500" ? "5405" : "5102";
        var avisos = new List<string>();
        if (ncm.Length != 8) avisos.Add("SEM NCM ou NCM invalido");
        if (csosn.Length == 0) avisos.Add("SEM CSOSN (motor usa 102 padrao)");
        if (csosn == "500" && cest.Length == 0) avisos.Add("ST SEM CEST (rejeita na SEFAZ)");
        if (avisos.Count > 0) problemas++;
        sb.AppendLine(string.Join(';',
            Csv((string)l.codigo), Csv((string)l.nome), Csv((string)l.categoria),
            Csv(ncm), cfop, Csv(csosn), Csv(cest), l.origem ?? 0,
            ((long)l.preco_cent / 100m).ToString("F2", new System.Globalization.CultureInfo("pt-BR")),
            (long)l.ativo == 1 ? "sim" : "nao",
            Csv(string.Join(" | ", avisos))));
    }
    File.WriteAllText(args[1], sb.ToString(), new System.Text.UTF8Encoding(true));
    Console.WriteLine($"{produtosFiscais.Count} produtos exportados para {args[1]} — {problemas} com pendência fiscal.");
    return 0;
}

// Modo sincronizar: roda a sincronização completa fora da UI (suporte/diagnóstico).
// Usa a MESMA credencial pareada (seg.dat, DPAPI) e o MESMO código da tela.
//   dotnet run -- sincronizar
if (args.Length >= 1 && args[0] == "sincronizar")
{
    Banco.Migrar();
    var (nAntes, vAntes) = Sincronizacao.Pendencias();
    Console.WriteLine($"antes : {nAntes} nota(s) e {vAntes.Total} venda(s) não entregues "
        + $"({vAntes.Desistidas} desistida(s)) — {vAntes.Valor.Formatado()}");

    Dictionary<string, string> LerSeg()
    {
        try
        {
            var arq = @"C:\ProgramData\PdvNativo\seg\seg.dat";
            if (!File.Exists(arq)) return new();
            var plano = System.Security.Cryptography.ProtectedData.Unprotect(
                File.ReadAllBytes(arq), null, System.Security.Cryptography.DataProtectionScope.LocalMachine);
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                System.Text.Encoding.UTF8.GetString(plano)) ?? new();
        }
        catch { return new(); }
    }

    var seg = LerSeg();
    if (seg.GetValueOrDefault("nuvemEmail", "").Length == 0)
    {
        Console.WriteLine("caixa não pareado (sem credencial no seg.dat).");
        return 1;
    }

    var nuvemS = new Nuvem();
    nuvemS.Credenciais = () => (seg["nuvemEmail"], seg["nuvemSenha"]);
    var guardaS = new GuardaNuvem(nuvemS, "http://127.0.0.1:4610", Nuvem.UrlPadrao);
    var drenoS = new Drenagem(nuvemS, Nuvem.UrlPadrao);

    var r = await Sincronizacao.ExecutarAsync(nuvemS, guardaS, drenoS,
        new Progress<string>(t => Console.WriteLine("  … " + t)));
    Console.WriteLine(r.Ok
        ? $"sync  : {r.ProdutosBaixados} produtos, {r.FotosBaixadas} fotos, {r.NotasSubidas} nota(s) subidas"
        : $"ERRO  : {r.Erro}");
    var (nDepois, vDepois) = Sincronizacao.Pendencias();
    Console.WriteLine($"depois: {nDepois} nota(s) e {vDepois.Total} venda(s) não entregues "
        + $"({vDepois.Desistidas} desistida(s)) — {vDepois.Valor.Formatado()}");
    return r.Ok ? 0 : 1;
}

// Modo zerar: recomeço operacional do caixa — apaga vendas, turnos, fila e auditoria.
// PRESERVA: produtos, operadores, terminal, config — e NÃO TOCA no fiscal (os XMLs e
// o contador de numeração vivem no agente; nota emitida não se apaga, numeração não
// volta). Backup do banco inteiro antes, sempre.
//   dotnet run -- zerar CONFIRMO
if (args.Length >= 1 && args[0] == "zerar")
{
    if (args.Length < 2 || args[1] != "CONFIRMO")
    {
        Console.WriteLine("isso apaga vendas/turnos/fila/auditoria deste caixa. rode: zerar CONFIRMO");
        return 1;
    }
    Banco.Migrar();
    var backup = Path.Combine(@"C:\ProgramData\PdvNativo",
        $"backup-antes-do-zero-{DateTime.Now:yyyyMMdd-HHmmss}.db");
    File.Copy(Path.Combine(@"C:\ProgramData\PdvNativo", "pdv.db"), backup, true);
    Console.WriteLine($"backup: {backup}");

    using var cxz = Banco.Abrir();
    using var txz = cxz.BeginTransaction();
    foreach (var t in new[] { "venda_pagamento", "venda_item", "nfce_emissao", "venda",
                              "caixa_fechamento", "caixa_movimento", "caixa_sessao",
                              "outbox", "tef_transacao", "auditoria" })
        Console.WriteLine($"  {t}: {cxz.Execute($"DELETE FROM {t}", transaction: txz)} apagadas");
    txz.Commit();
    Console.WriteLine("zerado. produtos/operadores/terminal/config preservados; fiscal intacto no agente e na nuvem.");
    return 0;
}

// Modo diagnostico-emissor: monta o MESMO resolvedor da tela e mostra por que cada
// caminho passa ou falha. Não emite nada.
if (args.Length >= 1 && args[0] == "diagnostico-emissor")
{
    Banco.Migrar();
    Dictionary<string, string> LerSegD()
    {
        try
        {
            var plano = System.Security.Cryptography.ProtectedData.Unprotect(
                File.ReadAllBytes(@"C:\ProgramData\PdvNativo\seg\seg.dat"), null,
                System.Security.Cryptography.DataProtectionScope.LocalMachine);
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                System.Text.Encoding.UTF8.GetString(plano)) ?? new();
        }
        catch { return new(); }
    }
    var segD = LerSegD();
    Console.WriteLine($"credencial de nuvem: {(segD.ContainsKey("nuvemEmail") ? "SIM" : "NAO")}");

    using var cxd = Banco.Abrir();
    var tD = cxd.QueryFirstOrDefault("SELECT cnpj, serie_nfce, ambiente FROM terminal LIMIT 1");
    var serieNuvem = Vendas.Config(cxd, "serie_nuvem");
    Console.WriteLine($"terminal: serie={tD?.serie_nfce} amb={tD?.ambiente} | serie_nuvem(config)={serieNuvem}");

    var nuvemD = new Nuvem();
    nuvemD.Credenciais = () => segD.ContainsKey("nuvemEmail") ? (segD["nuvemEmail"], segD["nuvemSenha"]) : null;
    var eNuvem = new EmissorNuvem(ct => nuvemD.TokenAsync(ct), null, tD?.cnpj as string)
    { GarantirSessao = ct => nuvemD.SessaoOkAsync(ct) };
    var eAgente = new EmissorAgente("http://127.0.0.1:4610");

    // ping cru: o que a edge respondeu de verdade (status + corpo), sem classificação
    var tok = await nuvemD.TokenAsync(CancellationToken.None);
    if (tok is not null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var reqP = new HttpRequestMessage(HttpMethod.Post,
            $"{Nuvem.UrlPadrao}/functions/v1/nfce-emitir");
        reqP.Headers.TryAddWithoutValidation("apikey", Nuvem.AnonKey);
        reqP.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tok);
        reqP.Content = new StringContent("{\"ping\":true}", System.Text.Encoding.UTF8, "application/json");
        using var respP = await http.SendAsync(reqP);
        var corpoP = await respP.Content.ReadAsStringAsync();
        Console.WriteLine($"PING cru: HTTP {(int)respP.StatusCode} corpo={corpoP[..Math.Min(corpoP.Length, 200)]}");
    }

    var sN = await eNuvem.SondarAsync(CancellationToken.None);
    Console.WriteLine($"NUVEM  : ok={sN.Ok} erro={sN.Erro ?? "-"}");
    var sA = await eAgente.SondarAsync(CancellationToken.None);
    Console.WriteLine($"AGENTE : ok={sA.Ok} serie={sA.Serie} tpAmb={sA.TpAmb?.ToString() ?? "null"} nnf={sA.ProximoNNF} erro={sA.Erro ?? sA.ErroCertificado ?? "-"}");

    var res = new EmissorResolvido(eNuvem, eAgente)
    {
        SerieNuvem = int.TryParse(serieNuvem, out var snv) ? snv : null,
        TpAmbEsperado = tD is null ? null : Convert.ToInt32(tD.ambiente),
    };
    var sR = await res.SondarAsync(CancellationToken.None);
    Console.WriteLine($"RESOLVIDO: ok={sR.Ok} caminho={res.CaminhoAtual} erro={sR.Erro ?? "-"}");
    Console.WriteLine($"  quedaDaNuvem={res.QuedaDaNuvem ?? "-"}");
    return 0;
}

// Modo config: lê ou grava uma chave da tabela `config` do terminal.
//   dotnet run -- config                      lista tudo
//   dotnet run -- config serie_nuvem          lê
//   dotnet run -- config serie_nuvem 2        grava
if (args.Length >= 1 && args[0] == "config")
{
    Banco.Migrar();
    using var cxg = Banco.Abrir();
    if (args.Length == 1)
    {
        var todas = cxg.Query("SELECT chave, valor FROM config ORDER BY chave").ToList();
        if (todas.Count == 0) Console.WriteLine("(config vazia)");
        foreach (var c in todas) Console.WriteLine($"{c.chave} = {c.valor}");
        return 0;
    }
    if (args.Length == 2)
    {
        Console.WriteLine(Vendas.Config(cxg, args[1]) ?? "(não definida)");
        return 0;
    }
    Vendas.GravarConfig(cxg, args[1], args[2]);
    Console.WriteLine($"{args[1]} = {args[2]}");
    return 0;
}

int ok = 0, falhas = 0;
void Check(string nome, bool cond)
{
    Console.WriteLine((cond ? "OK   " : "FALHA") + "  " + nome);
    if (cond) ok++; else falhas++;
}
void Recusa(string nome, Action acao, string trechoEsperado)
{
    try { acao(); Check(nome + " (NÃO recusou!)", false); }
    catch (Exception e) { Check(nome, e.Message.Contains(trechoEsperado, StringComparison.OrdinalIgnoreCase)); }
}

var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-teste-{Guid.NewGuid():N}.db");
Banco.Migrar(arquivo);

// ── DINHEIRO: por que tudo é centavo inteiro ────────────────────────────────
Check("0,10 + 0,20 = 0,30 exato (ponto flutuante erra isso)",
    (Dinheiro.DeReais(0.10m) + Dinheiro.DeReais(0.20m)).Centavos == 30);
Check("1000 itens de R$ 0,07 somam R$ 70,00 sem desvio",
    Enumerable.Range(0, 1000).Aggregate(Dinheiro.Zero, (a, _) => a + Dinheiro.DeReais(0.07m)).Centavos == 7000);
Check("0,375 kg a R$ 24,90 = R$ 9,34",
    Dinheiro.DeReais(24.90m).VezesQtd(Quantidade.DeDecimal(0.375m).Milesimos).Centavos == 934);
Check("quantidade inteira aparece como '2', não '2,000'", Quantidade.DeUnidades(2).Formatada() == "2");
Check("quantidade por peso aparece como '0,375'", Quantidade.DeDecimal(0.375m).Formatada() == "0,375");

using var cx = Banco.Abrir(arquivo);
var op = new Operador("op-1", "Maria", "operador");
var sup = new Operador("sup-1", "Ingrid", "supervisor");
Operadores.Salvar(cx, op.Id, op.Nome, "1234", "operador");
Operadores.Salvar(cx, sup.Id, sup.Nome, "9876", "supervisor");

// ── LOGIN POR PIN ───────────────────────────────────────────────────────────
Check("PIN certo identifica o operador", Operadores.Entrar(cx, "1234")?.Id == op.Id);
Check("PIN errado não entra", Operadores.Entrar(cx, "1111") is null);
Check("PIN de 3 dígitos é rejeitado", Operadores.Entrar(cx, "123") is null);
Check("PIN com letra é rejeitado", Operadores.Entrar(cx, "12a4") is null);
Check("o PIN NÃO fica legível no banco",
    cx.ExecuteScalar<string>("SELECT pin_hash FROM operador WHERE id=@i", new { i = op.Id }) != "1234");
Check("cada operador tem salt diferente (hash igual não denuncia PIN igual)",
    cx.ExecuteScalar<string>("SELECT pin_salt FROM operador WHERE id=@i", new { i = op.Id })
    != cx.ExecuteScalar<string>("SELECT pin_salt FROM operador WHERE id=@i", new { i = sup.Id }));
Check("supervisor autoriza com o PIN dele", Operadores.AutorizarSupervisor(cx, "9876")?.Id == sup.Id);
Check("operador comum NÃO consegue autorizar", Operadores.AutorizarSupervisor(cx, "1234") is null);

// PIN repetido é o furo mais caro deste modelo: como o login não pede nome, quem entra
// é "o primeiro que casar". Venda e sangria cairiam no nome errado — e um operador que
// repetisse o PIN de um supervisor passaria a autorizar a própria sangria.
Recusa("PIN repetido é barrado no cadastro",
    () => Operadores.Salvar(cx, "op-2", "João", "1234", "operador"), "já é de Maria");
Check("o cadastro barrado não criou ninguém",
    cx.ExecuteScalar<int>("SELECT COUNT(*) FROM operador WHERE id='op-2'") == 0);
// Regravar o próprio operador não pode esbarrar na checagem de duplicata contra ele mesmo
Operadores.Salvar(cx, op.Id, op.Nome, "1234", "operador");
Check("regravar o próprio operador com o mesmo PIN continua valendo",
    Operadores.Entrar(cx, "1234")?.Id == op.Id);

// Duplicata que escapou (viria da sincronização com a nuvem): recusa em vez de chutar.
cx.Execute("""
    INSERT INTO operador (id, nome, pin_hash, pin_salt, perfil, ativo, atualizado)
    SELECT 'op-clone', 'Clone', pin_hash, pin_salt, 'operador', 1, atualizado
      FROM operador WHERE id = @i
    """, new { i = op.Id });
Operadores.Entrar(cx, "1234", out var motivoDup);
Check("PIN duplicado no banco recusa o login em vez de entrar como o errado",
    motivoDup == Operadores.Recusa.PinDuplicado);
Check("supervisor com PIN duplicado também não autoriza", Operadores.AutorizarSupervisor(cx, "1234") is null);
cx.Execute("DELETE FROM operador WHERE id = 'op-clone'");
Check("removida a duplicata, o login volta a funcionar", Operadores.Entrar(cx, "1234")?.Id == op.Id);

// ── LOGIN POR CPF + SENHA ───────────────────────────────────────────────────
// CPF é a identidade forte: "foi ele que abriu o caixa" apontando pra documento.
Recusa("CPF inválido no cadastro é recusado",
    () => Operadores.Salvar(cx, "op-c", "Carlos", "5555", "operador", "123"), "CPF inválido");
Operadores.Salvar(cx, "op-c", "Carlos", "5555", "operador", "111.444.777-35");
Check("entra com CPF + senha", Operadores.EntrarComCpf(cx, "111.444.777-35", "5555")?.Id == "op-c");
Check("senha errada com CPF certo não entra", Operadores.EntrarComCpf(cx, "11144477735", "9999") is null);
Check("quem TEM CPF não entra mais só pelo PIN (caminho fraco fecha sozinho)",
    Operadores.Entrar(cx, "5555") is null);
Recusa("CPF repetido é barrado no cadastro",
    () => Operadores.Salvar(cx, "op-d", "Davi", "6666", "operador", "11144477735"), "já é de Carlos");
// supervisor ganha CPF e a AUTORIZAÇÃO por PIN continua valendo — autorização não é login
Operadores.Salvar(cx, sup.Id, sup.Nome, "9876", "supervisor", "529.982.247-25");
Check("supervisor com CPF ainda autoriza sangria pelo PIN",
    Operadores.AutorizarSupervisor(cx, "9876")?.Id == sup.Id);
cx.Execute("DELETE FROM operador WHERE id = 'op-c'");

// ── DOCUMENTO NA NOTA ───────────────────────────────────────────────────────
// Documento inválido é rejeitado pelo emissor com erro genérico. Barrar aqui evita
// o operador olhando "erro 400" sem saber que trocou um dígito.
Check("CPF válido passa", Documentos.CpfValido("111.444.777-35"));
Check("CPF com dígito trocado não passa", !Documentos.CpfValido("111.444.777-36"));
Check("CPF de dígitos repetidos não passa (fecha a conta mas não existe)",
    !Documentos.CpfValido("111.111.111-11"));
Check("CPF curto não passa", !Documentos.CpfValido("1114447773"));
Check("CNPJ numérico válido passa", Documentos.CnpjValido("62.177.839/0002-38"));
Check("CNPJ com dígito trocado não passa", !Documentos.CnpjValido("62.177.839/0002-39"));
Check("aceita CPF ou CNPJ no mesmo campo",
    Documentos.Valido("11144477735") && Documentos.Valido("62177839000238"));
Check("documento inválido não vira nada para a nota", Documentos.ParaNota("123") is null);
Check("o que vai para a nota é sem pontuação",
    Documentos.ParaNota("111.444.777-35") == "11144477735");
Check("CPF formatado para o cupom", Documentos.Formatar("11144477735") == "111.444.777-35");
Check("CNPJ formatado para o cupom", Documentos.Formatar("62177839000238") == "62.177.839/0002-38");

// ── ABERTURA ────────────────────────────────────────────────────────────────
Check("sem turno, não há caixa aberto", Caixa.SessaoAberta(cx) is null);
var sessao = Caixa.Abrir(cx, op, Dinheiro.DeReais(200));
Check("abriu com fundo de troco de R$ 200", sessao.FundoTroco.Centavos == 20000);
Check("o caixa aberto é encontrado", Caixa.SessaoAberta(cx)?.Id == sessao.Id);
Recusa("recusa abrir um segundo caixa no mesmo terminal",
    () => Caixa.Abrir(cx, sup, Dinheiro.DeReais(100)), "já existe caixa aberto");

// ── VENDAS (a tela vem depois; aqui só o efeito no caixa) ───────────────────
void Vender(string forma, decimal recebido, decimal troco = 0)
{
    var vid = Guid.NewGuid().ToString();
    var liquido = Dinheiro.DeReais(recebido - troco);
    cx.Execute("""
        INSERT INTO venda (id, client_key, sessao_id, business_date, numero_local, operador_id,
                           subtotal_cent, total_cent, status, criada_em, finalizada_em)
        VALUES (@Id,@K,@S,@Bd,@N,@Op,@T,@T,'finalizada',@Em,@Em)
        """,
        new { Id = vid, K = vid, S = sessao.Id, Bd = sessao.BusinessDate,
              N = cx.ExecuteScalar<int>("SELECT COALESCE(MAX(numero_local),0)+1 FROM venda WHERE business_date=@B", new { B = sessao.BusinessDate }),
              Op = op.Id, T = liquido.Centavos, Em = DateTime.Now.ToString("o") });
    cx.Execute("INSERT INTO venda_pagamento (id,venda_id,forma,valor_cent,troco_cent) VALUES (@i,@v,@f,@val,@tr)",
        new { i = Guid.NewGuid().ToString(), v = vid, f = forma,
              val = Dinheiro.DeReais(recebido).Centavos, tr = Dinheiro.DeReais(troco).Centavos });
}
Vender("dinheiro", recebido: 60, troco: 10);   // venda de 50
Vender("dinheiro", recebido: 30);
Vender("credito", recebido: 80);
Vender("pix", recebido: 25);

// ── SANGRIA / SUPRIMENTO ────────────────────────────────────────────────────
Recusa("sangria sem supervisor é recusada",
    () => Caixa.Movimentar(cx, sessao, "sangria", Dinheiro.DeReais(100), "cofre", op), "supervisor");
Recusa("sangria sem motivo é recusada",
    () => Caixa.Movimentar(cx, sessao, "sangria", Dinheiro.DeReais(100), "   ", op, sup.Id), "motivo");
Recusa("valor zero é recusado",
    () => Caixa.Movimentar(cx, sessao, "suprimento", Dinheiro.Zero, "troco", op), "maior que zero");
// gaveta com R$6 não sangra R$90: acima do apurado é dígito errado ou dinheiro sem registro.
// A mensagem NÃO revela o valor apurado (não pode entregar o gabarito da contagem cega).
Recusa("sangria maior que o dinheiro da gaveta é recusada",
    () => Caixa.Movimentar(cx, sessao, "sangria", Dinheiro.DeReais(999_999), "cofre", op, sup.Id), "mais do que entrou");
Caixa.Movimentar(cx, sessao, "sangria", Dinheiro.DeReais(100), "envio ao cofre", op, sup.Id, "cofre");
Caixa.Movimentar(cx, sessao, "suprimento", Dinheiro.DeReais(50), "reforço de troco", op);

// ── APURAÇÃO (o que o sistema espera achar) ─────────────────────────────────
var apurado = Caixa.Apurado(cx, sessao);
Check("dinheiro = fundo 200 + vendas 80 + suprimento 50 − sangria 100 = R$ 230",
    apurado["dinheiro"].Centavos == 23000);
Check("troco não vira receita (venda de 50, não de 60)", apurado["dinheiro"].Centavos == 23000);
Check("crédito apurado R$ 80", apurado["credito"].Centavos == 8000);
Check("pix apurado R$ 25", apurado["pix"].Centavos == 2500);

// ── FECHAMENTO CEGO ─────────────────────────────────────────────────────────
var declarado = new Dictionary<string, Dinheiro>
{
    ["dinheiro"] = Dinheiro.DeReais(225),   // R$ 5 a menos do que deveria
    ["credito"] = Dinheiro.DeReais(80),
    ["pix"] = Dinheiro.DeReais(25),
};
Recusa("diferença acima da tolerância exige justificativa",
    () => Caixa.Fechar(cx, sessao, declarado, op, Dinheiro.DeReais(2)), "justifique");

var linhas = Caixa.Fechar(cx, sessao, declarado, op, Dinheiro.DeReais(2), "erro de troco às 15h");
Check("falta de R$ 5 no dinheiro é detectada",
    linhas.First(l => l.Forma == "dinheiro").Diferenca.Centavos == -500);
Check("conferência é POR FORMA de pagamento, não só no total", linhas.Count >= 3);
Check("cartão e pix fecham sem diferença",
    linhas.Where(l => l.Forma != "dinheiro").All(l => l.Diferenca.Centavos == 0));
Check("caixa fica fechado depois do fechamento", Caixa.SessaoAberta(cx) is null);

// ── AUDITORIA E FILA ────────────────────────────────────────────────────────
var eventos = cx.Query<string>("SELECT evento FROM auditoria").ToList();
Check("auditou abertura, sangria, suprimento e fechamento",
    new[] { "caixa_aberto", "caixa_sangria", "caixa_suprimento", "caixa_fechado" }.All(eventos.Contains));
Check("a sangria registrou QUEM autorizou",
    cx.ExecuteScalar<string>("SELECT autorizado_por FROM caixa_movimento WHERE tipo='sangria'") == sup.Id);
Check("tudo que a nuvem precisa saber entrou na fila",
    cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE enviado_em IS NULL") >= 4);
Check("movimentos preservados (append-only)",
    cx.ExecuteScalar<int>("SELECT COUNT(*) FROM caixa_movimento") == 2);

// ── IMPORTAÇÃO DO CATÁLOGO (só roda se as credenciais vierem por variável) ──
// ── FECHAMENTO: SOBRA, FALTA E ERRO QUE SE COMPENSA ─────────────────────────
// Um caixa novo só para exercitar as regras de quebra.
var s3 = Caixa.Abrir(cx, op, Dinheiro.DeReais(100));
void Venda3(string forma, decimal valor)
{
    var vid = Guid.NewGuid().ToString();
    cx.Execute("""
        INSERT INTO venda (id, client_key, sessao_id, business_date, numero_local, operador_id,
                           subtotal_cent, total_cent, status, criada_em, finalizada_em)
        VALUES (@Id,@K,@S,@Bd,@N,@Op,@T,@T,'finalizada',@Em,@Em)
        """,
        new { Id = vid, K = vid, S = s3.Id, Bd = s3.BusinessDate,
              N = cx.ExecuteScalar<int>("SELECT COALESCE(MAX(numero_local),0)+1 FROM venda WHERE business_date=@B", new { B = s3.BusinessDate }),
              Op = op.Id, T = Dinheiro.DeReais(valor).Centavos, Em = DateTime.Now.ToString("o") });
    // cartão neste cenário é INTEGRADO (tef_aut preenchido) — é o que o TEF grava.
    // Sem isso o auto-detect do fechamento o trataria, certo, como venda POS manual.
    cx.Execute("INSERT INTO venda_pagamento (id,venda_id,forma,valor_cent,troco_cent,tef_aut) VALUES (@i,@v,@f,@val,0,@Aut)",
        new { i = Guid.NewGuid().ToString(), v = vid, f = forma, val = Dinheiro.DeReais(valor).Centavos,
              Aut = forma == "dinheiro" ? null : "AUT123" });
}
Venda3("dinheiro", 900);
Venda3("credito", 300);

// O exemplo do dono: abriu com 100, vendeu 900 em dinheiro, tem que fechar com 1000.
Check("gaveta esperada = fundo + vendas em dinheiro",
    Caixa.Apurado(cx, s3)["dinheiro"].Centavos == Dinheiro.DeReais(1000).Centavos);

// O que se conta depende do caixa: com TEF, cartão fecha sozinho; com POS avulsa,
// o operador digita o total do fechamento da maquininha (fonte independente).
Vendas.GravarConfig(cx, "tef_habilitado", "1");
Check("com TEF, só o dinheiro é contado pelo operador",
    Caixa.FormasContadas(cx) is ["dinheiro"]);
Vendas.GravarConfig(cx, "tef_habilitado", "0");
Check("sem TEF (POS avulsa), cartão e PIX também são contados",
    Caixa.FormasContadas(cx) is ["dinheiro", "debito", "credito", "pix", "voucher"]);
Vendas.GravarConfig(cx, "tef_habilitado", "1");   // os testes abaixo assumem TEF ligado

var soDinheiro = new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.DeReais(1000) };
var previa = Caixa.Fechar(cx, s3, soDinheiro, op, new Dinheiro(200));
Check("cartão fecha sozinho pelo valor do TEF, sem o operador declarar",
    previa.First(l => l.Forma == "credito") is { Contada: false, Situacao: "confere" });
Check("dinheiro conferido bate", previa.First(l => l.Forma == "dinheiro").Situacao == "confere");

// Falta e sobra que se anulam no líquido: o gate tem que pegar mesmo assim.
var s4 = Caixa.Abrir(cx, op, Dinheiro.DeReais(0));
var v4 = Guid.NewGuid().ToString();
cx.Execute("""
    INSERT INTO venda (id, client_key, sessao_id, business_date, numero_local, operador_id,
                       subtotal_cent, total_cent, status, criada_em, finalizada_em)
    VALUES (@Id,@K,@S,@Bd,@N,@Op,@T,@T,'finalizada',@Em,@Em)
    """,
    new { Id = v4, K = v4, S = s4.Id, Bd = s4.BusinessDate,
          N = cx.ExecuteScalar<int>("SELECT COALESCE(MAX(numero_local),0)+1 FROM venda WHERE business_date=@B", new { B = s4.BusinessDate }),
          Op = op.Id, T = Dinheiro.DeReais(100).Centavos, Em = DateTime.Now.ToString("o") });
cx.Execute("INSERT INTO venda_pagamento (id,venda_id,forma,valor_cent,troco_cent) VALUES (@i,@v,'dinheiro',@val,0)",
    new { i = Guid.NewGuid().ToString(), v = v4, val = Dinheiro.DeReais(100).Centavos });

Recusa("falta no dinheiro acima da tolerância exige justificativa",
    () => Caixa.Fechar(cx, s4, new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.DeReais(50) },
        op, new Dinheiro(200)), "falta");
Recusa("SOBRA também exige justificativa (venda que não passou pelo PDV)",
    () => Caixa.Fechar(cx, s4, new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.DeReais(150) },
        op, new Dinheiro(200)), "sobra");

var fechado4 = Caixa.Fechar(cx, s4, new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.DeReais(150) },
    op, new Dinheiro(200), "cliente pagou e não registrei");
Check("a sobra foi classificada como sobra", fechado4.First(l => l.Forma == "dinheiro").Situacao == "sobra");
Check("a quebra foi discriminada na auditoria, não só o total",
    cx.ExecuteScalar<string>("SELECT detalhe FROM auditoria WHERE evento='caixa_fechado' ORDER BY id DESC LIMIT 1")!
      .Contains("dinheiro:sobra"));

// POS avulsa: o crédito declarado DIVERGENTE da maquininha precisa aparecer.
Vendas.GravarConfig(cx, "tef_habilitado", "0");
var s5 = Caixa.Abrir(cx, op, Dinheiro.DeReais(0));
var v5 = Guid.NewGuid().ToString();
cx.Execute("""
    INSERT INTO venda (id, client_key, sessao_id, business_date, numero_local, operador_id,
                       subtotal_cent, total_cent, status, criada_em, finalizada_em)
    VALUES (@Id,@K,@S,@Bd,@N,@Op,@T,@T,'finalizada',@Em,@Em)
    """,
    new { Id = v5, K = v5, S = s5.Id, Bd = s5.BusinessDate,
          N = cx.ExecuteScalar<int>("SELECT COALESCE(MAX(numero_local),0)+1 FROM venda WHERE business_date=@B", new { B = s5.BusinessDate }),
          Op = op.Id, T = Dinheiro.DeReais(50).Centavos, Em = DateTime.Now.ToString("o") });
cx.Execute("INSERT INTO venda_pagamento (id,venda_id,forma,valor_cent,troco_cent) VALUES (@i,@v,'credito',@val,0)",
    new { i = Guid.NewGuid().ToString(), v = v5, val = Dinheiro.DeReais(50).Centavos });

Recusa("sem TEF, crédito da maquininha diferente do PDV exige justificativa",
    () => Caixa.Fechar(cx, s5, new Dictionary<string, Dinheiro>
        { ["dinheiro"] = Dinheiro.Zero, ["credito"] = Dinheiro.DeReais(30), ["debito"] = Dinheiro.Zero, ["pix"] = Dinheiro.Zero },
        op, new Dinheiro(200)), "credito");
var fech5 = Caixa.Fechar(cx, s5, new Dictionary<string, Dinheiro>
    { ["dinheiro"] = Dinheiro.Zero, ["credito"] = Dinheiro.DeReais(50), ["debito"] = Dinheiro.Zero, ["pix"] = Dinheiro.Zero },
    op, new Dinheiro(200));
Check("sem TEF, crédito conferido com a maquininha fecha limpo",
    fech5.First(l => l.Forma == "credito") is { Contada: true, Situacao: "confere" });
Vendas.GravarConfig(cx, "tef_habilitado", "1");

// TEF ligado mas o turno teve venda POS avulsa (fallback): a forma volta pra contagem.
// É o auto-detect do fechamento — decide pelo que ACONTECEU, não pela configuração.
var s6 = Caixa.Abrir(cx, op, Dinheiro.DeReais(0));
var v6 = Guid.NewGuid().ToString();
cx.Execute("""
    INSERT INTO venda (id, client_key, sessao_id, business_date, numero_local, operador_id,
                       subtotal_cent, total_cent, status, criada_em, finalizada_em)
    VALUES (@Id,@K,@S,@Bd,@N,@Op,@T,@T,'finalizada',@Em,@Em)
    """,
    new { Id = v6, K = v6, S = s6.Id, Bd = s6.BusinessDate,
          N = cx.ExecuteScalar<int>("SELECT COALESCE(MAX(numero_local),0)+1 FROM venda WHERE business_date=@B", new { B = s6.BusinessDate }),
          Op = op.Id, T = Dinheiro.DeReais(20).Centavos, Em = DateTime.Now.ToString("o") });
// pagamento SEM tef_aut = registrado como POS avulsa
cx.Execute("INSERT INTO venda_pagamento (id,venda_id,forma,valor_cent,troco_cent) VALUES (@i,@v,'pix',@val,0)",
    new { i = Guid.NewGuid().ToString(), v = v6, val = Dinheiro.DeReais(20).Centavos });

Check("com TEF, turno com venda POS avulsa traz a forma de volta pra contagem",
    Caixa.FormasContadas(cx, s6).Contains("pix"));
Check("mas só a forma que teve venda manual — as outras seguem automáticas",
    !Caixa.FormasContadas(cx, s6).Contains("credito"));
var fech6 = Caixa.Fechar(cx, s6, new Dictionary<string, Dinheiro>
    { ["dinheiro"] = Dinheiro.Zero, ["pix"] = Dinheiro.DeReais(20) }, op, new Dinheiro(200));
Check("o pix manual conferido fecha limpo",
    fech6.First(l => l.Forma == "pix") is { Contada: true, Situacao: "confere" });

// ── ABERTURA × FECHAMENTO ANTERIOR ──────────────────────────────────────────
// O exemplo do dono: fechou com X em dinheiro → o dia seguinte deve abrir com X.
// O último fechamento (s6) declarou dinheiro = 0; é ELE que manda, não os anteriores.
Check("o fundo esperado da próxima abertura é o dinheiro do ÚLTIMO fechamento",
    Caixa.FundoEsperado(cx)?.Centavos == 0);
var s7 = Caixa.Abrir(cx, op, Dinheiro.DeReais(300));
Caixa.Fechar(cx, s7, new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.DeReais(300) },
    op, new Dinheiro(200));
Check("fechou com 300 → a próxima abertura espera 300",
    Caixa.FundoEsperado(cx)?.Centavos == Dinheiro.DeReais(300).Centavos);

// ── GRAVAÇÃO DA VENDA ───────────────────────────────────────────────────────
// O caixa anterior já foi fechado pelos testes acima, então abre outro.
var sessao2 = Caixa.Abrir(cx, op, Dinheiro.DeReais(100));
var itensV = new List<LinhaVenda>
{
    new("p1", "SKU1", "COOKIE NINHO", Quantidade.Um, Dinheiro.DeReais(13.50m), Dinheiro.DeReais(13.50m),
        "UN", "19053100", null, "102", null, 0),
    new("p2", "SKU2", "AGUA 500ML", new Quantidade(2000), Dinheiro.DeReais(6), Dinheiro.DeReais(12),
        "UN", "22011000", null, "102", null, 0),
};
var totalV = Dinheiro.DeReais(25.50m);

Recusa("comanda vazia não finaliza",
    () => Vendas.Finalizar(cx, sessao2, op, new List<LinhaVenda>(),
        new[] { new PagamentoVenda("dinheiro", totalV, Dinheiro.Zero) }, null, "Loja", null),
    "comanda vazia");
Recusa("pagamento que não fecha com o total é recusado",
    () => Vendas.Finalizar(cx, sessao2, op, itensV,
        new[] { new PagamentoVenda("dinheiro", Dinheiro.DeReais(20), Dinheiro.Zero) }, null, "Loja", null),
    "e a venda é");

// dinheiro: o cliente entrega 30 numa venda de 25,50 e leva 4,50 de troco
var v1 = Vendas.Finalizar(cx, sessao2, op, itensV,
    new[] { new PagamentoVenda("dinheiro", Dinheiro.DeReais(30), Dinheiro.DeReais(4.50m)) },
    "11144477735", "Loja", null);
Check("a venda foi gravada com o total dos itens", v1.Total.Centavos == 2550);
Check("os itens entraram", cx.ExecuteScalar<int>("SELECT COUNT(*) FROM venda_item WHERE venda_id=@v", new { v = v1.Id }) == 2);
Check("o documento foi para a venda",
    cx.ExecuteScalar<string>("SELECT documento FROM venda WHERE id=@v", new { v = v1.Id }) == "11144477735");
Check("o troco ficou guardado no pagamento",
    cx.ExecuteScalar<int>("SELECT troco_cent FROM venda_pagamento WHERE venda_id=@v", new { v = v1.Id }) == 450);
Check("a venda nasce com nota pendente",
    cx.ExecuteScalar<string>("SELECT fiscal_status FROM venda WHERE id=@v", new { v = v1.Id }) == "pendente");
Check("a venda entrou na fila de sincronização com a mesma client_key",
    cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo='venda' AND ref_id=@v AND client_key=@k",
        new { v = v1.Id, k = v1.ClientKey }) == 1);

var v2 = Vendas.Finalizar(cx, sessao2, op, itensV,
    new[] { new PagamentoVenda("credito", totalV, Dinheiro.Zero, Aut: "160342", CnpjCredenciadora: "01425787000104", Bandeira: "02") },
    null, "Loja", null);
Check("a numeração local do dia avança", v2.Numero == v1.Numero + 1);
Check("o retorno do TEF ficou no pagamento (vira o grupo card da nota)",
    cx.ExecuteScalar<string>("SELECT tef_aut FROM venda_pagamento WHERE venda_id=@v", new { v = v2.Id }) == "160342");

// só o líquido conta como receita: 25,50 em dinheiro + 25,50 no crédito
var apurado2 = Caixa.Apurado(cx, sessao2);
Check("o troco não virou receita no apurado do caixa",
    apurado2["dinheiro"].Centavos == 10000 + 2550);   // fundo de 100 + a venda
Check("crédito apurado sem troco", apurado2["credito"].Centavos == 2550);

// ── DESFECHO FISCAL ─────────────────────────────────────────────────────────
// Emissor MUDO não é rejeição: a nota pode ter saído do outro lado. Marcar como
// rejeitada convidaria o operador a reemitir e a nota sairia duplicada.
Vendas.RegistrarEmissao(cx, v1.Id, new ResultadoEmissao { Caminho = "nuvem", Indisponivel = true, Erro = "sem resposta" });
Check("emissor mudo deixa a nota PENDENTE, não rejeitada",
    cx.ExecuteScalar<string>("SELECT fiscal_status FROM venda WHERE id=@v", new { v = v1.Id }) == "pendente");

Vendas.RegistrarEmissao(cx, v1.Id, new ResultadoEmissao
{
    Caminho = "nuvem", Autorizado = true, Modo = "online", Chave = new string('1', 44),
    Numero = 7, Serie = 2, TpAmb = 1, Protocolo = "131260000737192", CStat = "100", VNF = 25.50m,
});
Check("a segunda tentativa foi registrada como tentativa 2",
    cx.ExecuteScalar<int>("SELECT MAX(tentativa) FROM nfce_emissao WHERE venda_id=@v", new { v = v1.Id }) == 2);
Check("as duas tentativas ficaram guardadas (número queimado não se apaga)",
    cx.ExecuteScalar<int>("SELECT COUNT(*) FROM nfce_emissao WHERE venda_id=@v", new { v = v1.Id }) == 2);
Check("autorizada, a venda ganha chave e protocolo",
    cx.ExecuteScalar<string>("SELECT nfce_chave FROM venda WHERE id=@v", new { v = v1.Id })!.Length == 44);
Check("o vínculo da nota entrou na fila da nuvem",
    cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo='nfce_vinculo' AND ref_id=@v", new { v = v1.Id }) == 1);

Vendas.RegistrarEmissao(cx, v2.Id, new ResultadoEmissao
{
    Caminho = "agente", Contingencia = true, Modo = "contingencia", Chave = new string('2', 44), Numero = 41, Serie = 3,
});
Check("contingência conta como venda com nota, não como falha",
    cx.ExecuteScalar<string>("SELECT fiscal_status FROM venda WHERE id=@v", new { v = v2.Id }) == "contingencia");

// ── CANCELAMENTO DE VENDA ───────────────────────────────────────────────────
Recusa("cancelar sem motivo é recusado",
    () => Vendas.Cancelar(cx, v2.Id, "op", ""), "exige motivo");
// v2 ficou em contingência: a nota EXISTE, então cancelar só do lado de cá deixaria
// documento válido para venda inexistente.
Recusa("venda com nota emitida não cancela sem cancelar a nota antes",
    () => Vendas.Cancelar(cx, v2.Id, "op", "erro do operador"), "Cancele a nota na SEFAZ");

var vSemNota = Vendas.Finalizar(cx, sessao2, op, itensV,
    new[] { new PagamentoVenda("dinheiro", totalV, Dinheiro.Zero) }, null, "Loja", null);
Vendas.Cancelar(cx, vSemNota.Id, op.Id, "cliente desistiu");
Check("venda sem nota cancela",
    cx.ExecuteScalar<string>("SELECT status FROM venda WHERE id=@v", new { v = vSemNota.Id }) == "cancelada");
Vendas.Cancelar(cx, vSemNota.Id, op.Id, "de novo");   // não pode estourar
Check("cancelar de novo não quebra (idempotente)",
    cx.ExecuteScalar<int>("SELECT COUNT(*) FROM auditoria WHERE evento='venda_cancelada'") == 1);
Check("venda cancelada sai do apurado do caixa",
    !Caixa.Apurado(cx, sessao2).TryGetValue("dinheiro", out var dep) || dep.Centavos == 10000 + 2550);
Check("o cancelamento foi auditado com motivo",
    cx.ExecuteScalar<string>("SELECT detalhe FROM auditoria WHERE evento='venda_cancelada' ORDER BY id DESC LIMIT 1")!
      .Contains("cliente desistiu"));

// ── MODO DE HOMOLOGAÇÃO: venda de teste NÃO é faturamento ───────────────────
// O roteiro da PayGo é rodado no PDV de verdade, com caixa aberto e operador de
// verdade. Sem marca, a venda de teste é uma venda comum: sobe para a nuvem (vira
// receita na DRE) e infla o apurado do turno — e aí o operador conta a gaveta e
// encontra uma falta que nunca existiu.
var apuradoAntesTeste = Caixa.Apurado(cx, sessao2)["dinheiro"];
var filaAntesTeste = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo='venda'");

Vendas.GravarConfig(cx, "homologacao", "1");
var vTeste = Vendas.Finalizar(cx, sessao2, op, itensV,
    new[] { new PagamentoVenda("dinheiro", totalV, Dinheiro.Zero) }, null, "Loja", null);

Check("homologação: a venda nasce marcada como de teste",
    cx.ExecuteScalar<int>("SELECT homologacao FROM venda WHERE id=@v", new { v = vTeste.Id }) == 1);
Check("homologação: a venda de teste NÃO entra na fila da nuvem",
    cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo='venda' AND ref_id=@v", new { v = vTeste.Id }) == 0);
Check("homologação: a fila não cresceu com a venda de teste",
    cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo='venda'") == filaAntesTeste);
Check("homologação: a auditoria registra que a venda ficou local",
    cx.ExecuteScalar<int>("SELECT COUNT(*) FROM auditoria WHERE evento='venda_homologacao'") == 1);
Check("homologação: a venda de teste não infla o apurado do turno",
    Caixa.Apurado(cx, sessao2)["dinheiro"].Centavos == apuradoAntesTeste.Centavos);
Check("homologação: o total de teste aparece à parte, com rótulo",
    Caixa.ApuradoDeTeste(cx, sessao2).TryGetValue("dinheiro", out var soTeste) && soTeste.Centavos == totalV.Centavos);
Check("homologação: o fechamento avisa que houve venda de teste no turno",
    Caixa.ResumoDeTeste(cx, sessao2) is string resumoTeste
    && resumoTeste.Contains("TESTE") && resumoTeste.Contains(totalV.Formatado()));

// A maquininha do roteiro cobra no ambiente de teste da PayGo. Tirar a venda do
// apurado sem tirar a cobrança do lado do TEF faria o fechamento acusar uma
// divergência inventada: "maquininha R$ 25,50, no PDV R$ 0,00".
//
// O estado montado aqui é O QUE O PDV GRAVA DE VERDADE: `tef_transacao.venda_id`
// nasce NULL e NUNCA é preenchido (nenhum INSERT/UPDATE de produção escreve essa
// coluna — Servicos.GuardarTef e Pagamento.RegistrarTef/AtualizarTef a omitem).
// Quem amarra venda e TEF na loja é o NSU. Montar a linha com venda_id preenchido
// carimbaria verde num banco que não existe.
const string sqlTefPago = """
    INSERT INTO tef_transacao (id, venda_id, charge_id, provedor, tipo, valor_cent, nsu,
                               situacao, criado_em, atualizado_em)
    VALUES (@Id, NULL, @Id, 'paygo', 'debito', @Val, @Nsu, 'pago', @Em, @Em)
    """;

// CONTROLE, na mesma execução: mesma montagem, mesma forma, mesmo NSU-como-vínculo.
// A ÚNICA variável entre este bloco e o de baixo é a config `homologacao`.
Vendas.GravarConfig(cx, "homologacao", "0");
var vRealCartao = Vendas.Finalizar(cx, sessao2, op, itensV,
    new[] { new PagamentoVenda("debito", totalV, Dinheiro.Zero, Aut: "770010", Nsu: "900010") }, null, "Loja", null);
cx.Execute(sqlTefPago,
    new { Id = "tef-real", Val = totalV.Centavos, Nsu = "900010", Em = DateTime.Now.ToString("o") });
Check("CONTROLE: venda REAL de cartão fecha sem divergência TEF x PDV",
    Caixa.DivergenciasTef(cx, sessao2).All(d => d.Forma != "debito"));

Vendas.GravarConfig(cx, "homologacao", "1");
var vTesteCartao = Vendas.Finalizar(cx, sessao2, op, itensV,
    new[] { new PagamentoVenda("debito", totalV, Dinheiro.Zero, Aut: "770011", Nsu: "900011") }, null, "Loja", null);
cx.Execute(sqlTefPago,
    new { Id = "tef-homolog", Val = totalV.Centavos, Nsu = "900011", Em = DateTime.Now.ToString("o") });
Check("homologação: cobrança de teste não vira divergência TEF x PDV no fechamento",
    Caixa.DivergenciasTef(cx, sessao2).All(d => d.Forma != "debito"));

// E o alarme que não pode ser desligado junto: cobrança ÓRFÃ (aprovada na maquininha
// sem venda nenhuma no PDV) continua aparecendo — inclusive durante o roteiro.
cx.Execute(sqlTefPago,
    new { Id = "tef-orfa", Val = 12345L, Nsu = "900099", Em = DateTime.Now.ToString("o") });
Check("cobrança ÓRFÃ continua acusando divergência (dinheiro cobrado sem venda)",
    Caixa.DivergenciasTef(cx, sessao2)
        .Any(d => d.Forma == "debito" && d.Diferenca.Centavos == 12345));

// O vínculo da nota é outra linha para a nuvem — de uma venda que a nuvem não tem.
Vendas.RegistrarEmissao(cx, vTeste.Id, new ResultadoEmissao
{
    Caminho = "agente", Autorizado = true, Modo = "online", Chave = new string('3', 44),
    Numero = 8, Serie = 2, TpAmb = 2, Protocolo = "131260000737193", CStat = "100", VNF = 25.50m,
});
Check("homologação: o vínculo da nota de teste também não vai para a nuvem",
    cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo='nfce_vinculo' AND ref_id=@v", new { v = vTeste.Id }) == 0);

var vTeste2 = Vendas.Finalizar(cx, sessao2, op, itensV,
    new[] { new PagamentoVenda("dinheiro", totalV, Dinheiro.Zero) }, null, "Loja", null);
Vendas.Cancelar(cx, vTeste2.Id, op.Id, "fim do roteiro de homologação");
Check("homologação: o cancelamento da venda de teste também não vai para a nuvem",
    cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo='venda_cancelada' AND ref_id=@v", new { v = vTeste2.Id }) == 0);

Vendas.GravarConfig(cx, "homologacao", "0");
var vReal = Vendas.Finalizar(cx, sessao2, op, itensV,
    new[] { new PagamentoVenda("dinheiro", totalV, Dinheiro.Zero) }, null, "Loja", null);
Check("sem homologação: a venda nasce como venda de verdade (homologacao=0)",
    cx.ExecuteScalar<int>("SELECT homologacao FROM venda WHERE id=@v", new { v = vReal.Id }) == 0);
Check("sem homologação: a venda volta a subir para a nuvem",
    cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo='venda' AND ref_id=@v", new { v = vReal.Id }) == 1);
Check("sem homologação: a venda volta a contar no apurado do turno",
    Caixa.Apurado(cx, sessao2)["dinheiro"].Centavos == apuradoAntesTeste.Centavos + totalV.Centavos);

Vendas.GravarConfig(cx, "impressora", "Elgin i9");
Check("config do terminal grava e lê", Vendas.Config(cx, "impressora") == "Elgin i9");
Check("config ausente devolve o padrão", Vendas.Config(cx, "nao_existe", "padrao") == "padrao");
Vendas.GravarConfig(cx, "impressora", "Bematech MP-4200");
Check("regravar a config substitui em vez de duplicar",
    Vendas.Config(cx, "impressora") == "Bematech MP-4200"
    && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM config WHERE chave='impressora'") == 1);

// ── SINCRONIZAÇÃO: regressões da caça exaustiva ─────────────────────────────
// (1) Badge "(2)" fantasma. Nota da NUVEM não tem XML LOCAL (a edge já a gravou
//     em nfce_emitidas), então a GuardaNuvem procurava o XML no agente, não
//     achava e deixava sincronizada=0 PARA SEMPRE — o "(2)" que nenhum clique
//     resolvia. A varredura ESTÁTICA não pegou (é bug de caminho de dados). Fix:
//     nota da nuvem nasce sincronizada=1; nota do AGENTE não (o XML ainda sobe).
var vNuvem = Vendas.Finalizar(cx, sessao2, op, itensV,
    new[] { new PagamentoVenda("dinheiro", totalV, Dinheiro.Zero) }, null, "Loja", null);
Vendas.RegistrarEmissao(cx, vNuvem.Id, new ResultadoEmissao
{
    Caminho = EmissorNuvem.Caminho, Autorizado = true, Modo = "online",
    Chave = new string('9', 44), Numero = 7, Serie = 1,
});
Check("nota da NUVEM nasce sincronizada (mata o badge fantasma)",
    cx.ExecuteScalar<int>("SELECT sincronizada FROM nfce_emissao WHERE chave=@Ch", new { Ch = new string('9', 44) }) == 1);

var vAgente = Vendas.Finalizar(cx, sessao2, op, itensV,
    new[] { new PagamentoVenda("dinheiro", totalV, Dinheiro.Zero) }, null, "Loja", null);
Vendas.RegistrarEmissao(cx, vAgente.Id, new ResultadoEmissao
{
    Caminho = EmissorAgente.Caminho, Autorizado = true, Modo = "online",
    Chave = new string('8', 44), Numero = 8, Serie = 3,
});
Check("nota do AGENTE NÃO é pré-marcada (o XML local ainda tem que subir)",
    cx.ExecuteScalar<int>("SELECT sincronizada FROM nfce_emissao WHERE chave=@Ch", new { Ch = new string('8', 44) }) == 0);

// (2) O cancelamento de venda entra na fila com o tipo e o payload que o dreno
//     espera. Sem o handler 'venda_cancelada' (ALTO-3), a linha voltava eterna
//     na janela LIMIT 50 e entupia a sincronização.
Check("cancelamento entrou na fila (tipo venda_cancelada)",
    cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo='venda_cancelada' AND ref_id=@v", new { v = vSemNota.Id }) == 1);
Check("o payload do cancelamento carrega o motivo (o dreno o repassa à RPC)",
    cx.ExecuteScalar<string>("SELECT payload FROM outbox WHERE tipo='venda_cancelada' AND ref_id=@v", new { v = vSemNota.Id })!
      .Contains("cliente desistiu"));

// (3) Dead-letter: a matemática que impede a fila de entupir (ALTO-2/3). Pura,
//     sem rede — é onde o "retry eterno e mudo" nascia (4xx tratado como rede).
var refHoje = new DateTime(2026, 8, 8, 12, 0, 0);
Check("envio confirmado sai da fila",
    Drenagem.DecidirFila(true, 0, null, refHoje) == Drenagem.AcaoFila.Enviado);
Check("recusa permanente (4xx) conta tentativa — não desiste na 1ª",
    Drenagem.DecidirFila(false, 0, null, refHoje) == Drenagem.AcaoFila.ContaTentativa);
Check("recusa que bateu o teto vira dead-letter (some do caminho, fica auditável)",
    Drenagem.DecidirFila(false, Drenagem.MaxTentativas - 1, null, refHoje) == Drenagem.AcaoFila.DeadLetter);
Check("rede (5xx/null) NUNCA conta tentativa — espera a rede voltar",
    Drenagem.DecidirFila(null, 999, refHoje, refHoje) == Drenagem.AcaoFila.Aguarda);

// A expiração conta do PRIMEIRO ERRO REAL (sessão de pé), nunca do criado_em:
// terminal religado após semanas desligado não pode perder venda no 1º soluço.
Check("transitório sem primeiro erro registrado SEMPRE aguarda (item recém-religado)",
    Drenagem.DecidirFila(null, 0, null, refHoje) == Drenagem.AcaoFila.Aguarda);
Check("transitório falhando há dias (do 1º erro real) desiste — não starva a fila",
    Drenagem.DecidirFila(null, 0, refHoje.AddDays(-(Drenagem.DiasParaDesistir + 1)), refHoje) == Drenagem.AcaoFila.ExpiraVelho);
Check("transitório com falha recente ainda aguarda",
    Drenagem.DecidirFila(null, 0, refHoje.AddDays(-1), refHoje) == Drenagem.AcaoFila.Aguarda);

// (4) Tradução do status HTTP: o coração do "4xx engolido como rede = retry eterno".
Check("400 (payload malformado) é recusa permanente",
    Drenagem.DesfechoDeStatus(400, "bad request").Ok == false);
Check("404 (RPC sumida num deploy) é recusa permanente",
    Drenagem.DesfechoDeStatus(404, "not found").Ok == false);
Check("429 (rate-limit) é TRANSITÓRIO — não pode derrubar um flush de backlog inteiro",
    Drenagem.DesfechoDeStatus(429, "too many requests").Ok == null);
Check("408 (request timeout) é transitório",
    Drenagem.DesfechoDeStatus(408, "").Ok == null);
Check("503 (servidor doente) é transitório",
    Drenagem.DesfechoDeStatus(503, "unavailable").Ok == null);
Check("0 (nem resposta) é transitório",
    Drenagem.DesfechoDeStatus(0, null).Ok == null);
Check("todo desfecho de erro carrega rastro para o diagnóstico (nunca mudo)",
    !string.IsNullOrEmpty(Drenagem.DesfechoDeStatus(500, "boom").Erro)
    && !string.IsNullOrEmpty(Drenagem.DesfechoDeStatus(0, null).Erro));

var emailNuvem = Environment.GetEnvironmentVariable("PDV_EMAIL");
var senhaNuvem = Environment.GetEnvironmentVariable("PDV_SENHA");
if (!string.IsNullOrWhiteSpace(emailNuvem) && !string.IsNullOrWhiteSpace(senhaNuvem))
{
    var nuvem = new Nuvem();
    var entrou = await nuvem.EntrarAsync(emailNuvem!, senhaNuvem!);
    Check("nuvem: autenticou com a conta do caixa", entrou);
    if (entrou)
    {
        var n = await nuvem.BaixarProdutosAsync(cx);
        Check($"nuvem: importou o catálogo ({n} produtos)", n > 100);
        Check("catálogo: preços em centavos, todos positivos",
            cx.ExecuteScalar<int>("SELECT COUNT(*) FROM produto WHERE ativo=1 AND preco_cent <= 0") == 0);
        Check("catálogo: tem categorias",
            cx.ExecuteScalar<int>("SELECT COUNT(DISTINCT categoria) FROM produto WHERE ativo=1") >= 5);
        Check("catálogo: bloco fiscal veio junto (NCM)",
            cx.ExecuteScalar<int>("SELECT COUNT(*) FROM produto WHERE ativo=1 AND ncm IS NOT NULL") > 100);
        var caro = cx.QueryFirst("SELECT nome, preco_cent FROM produto WHERE ativo=1 ORDER BY preco_cent DESC LIMIT 1");
        Console.WriteLine($"       (mais caro: {(string)caro.nome} = {new Dinheiro((long)caro.preco_cent).Formatado()})");
        // reimportar não pode duplicar
        var antes = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM produto");
        await nuvem.BaixarProdutosAsync(cx);
        Check("catálogo: reimportar NÃO duplica",
            cx.ExecuteScalar<int>("SELECT COUNT(*) FROM produto") == antes);
    }
}
else Console.WriteLine("(pulei o teste de nuvem — defina PDV_EMAIL e PDV_SENHA para rodá-lo)");

// ── TEF: o caminho do cartão, contra a maquininha de mentira ────────────────
// Antes disto o TEF nunca era exercitado (exige hardware da Rede Smart), e é por
// ele que o dinheiro entra sem passar pela gaveta.
Console.WriteLine();
Console.WriteLine("--- TEF (maquininha simulada) ---");
TestesTef.Rodar((cond, nome) => Check("tef: " + nome, cond));

// ── NFC-e: emissão e cancelamento contra o autorizador de mentira ───────────
Console.WriteLine();
Console.WriteLine("--- NFC-e / SEFAZ (autorizador simulado) ---");
TestesSefaz.Rodar((cond, nome) => Check("nfce: " + nome, cond));

// -- KDS: a fila de preparo do monitor touch ---------------------------------
// Card duplicado faz produzir duas vezes; card que some faz o cliente esperar.
Console.WriteLine();
Console.WriteLine("--- KDS (fila de preparo) ---");
TestesKds.Rodar((cond, nome) => Check("kds: " + nome, cond));

// -- TEMA: decisao diurno/noturno + contraste da paleta clara ----------------
// O teste de contraste le Temas/Claro.xaml de verdade: paleta ilegivel derruba
// a bateria aqui, nao espera reclamacao no balcao.
Console.WriteLine();
Console.WriteLine("--- Tema (diurno/noturno) ---");
TestesTema.Rodar((cond, nome) => Check("tema: " + nome, cond));

// -- PROMOCOES: o motor que faltava (a promocao de quinta invisivel no caixa) --
Console.WriteLine();
Console.WriteLine("--- Promocoes (motor de preco) ---");
TestesPromocoes.Rodar((cond, nome) => Check("promo: " + nome, cond));

// -- TEF PayGo: troca de arquivos contra um PayGo de mentira ------------------
// Two-phase commit (CRT -> CNF/NCN), sem timeout apos o .sts, .tmp+rename,
// religamento. Dinheiro que entra sem passar pela gaveta — de novo.
Console.WriteLine();
Console.WriteLine("--- TEF PayGo (troca de arquivos simulada) ---");
TestesPayGo.Rodar((cond, nome) => Check("paygo: " + nome, cond));
Console.WriteLine("--- TEF ControlPay (WebService simulado) ---");
TestesControlPay.Rodar((cond, nome) => Check("controlpay: " + nome, cond));
// Rede digitada errada = cobranca recusada com o cliente no balcao (o PIX C6 BANK da
// homologacao que ficou em producao, onde o Pix e ITAU). A lista e fechada por isso.
Console.WriteLine("--- Credenciadoras do PayGo (lista fechada) ---");
TestesRedesPayGo.Rodar((cond, nome) => Check("redes: " + nome, cond));

// -- FILA: o que a nuvem RECUSA para sempre ----------------------------------
// Dead-letter carimbava enviado_em: R$ 102.626,50 sumiram do contador com tudo verde.
Console.WriteLine();
Console.WriteLine("--- Fila de sincronizacao (dead-letter) ---");
await TestesFila.RodarAsync((cond, nome) => Check("fila: " + nome, cond));

// -- PENDENCIA: o aviso que nao se apaga -------------------------------------
// 16 vendas / R$ 102.626,50 em dead-letter sem NENHUMA saida: o dono arrumava o
// painel, apertava Sincronizar e o numero continuava 16. Aviso sem saida vira ruido.
Console.WriteLine();
Console.WriteLine("--- Aviso de pendencia (saida do dead-letter) ---");
await TestesPendencias.RodarAsync((cond, nome) => Check("pendencia: " + nome, cond));

// -- RASCUNHO: a comanda em andamento contra a queda de energia ---------------
// Os itens viviam so na memoria da tela: religar = rebipar tudo com o cliente
// no balcao. Restaurar os ITENS nao e realizar a venda (passo 24 da homologacao).
Console.WriteLine();
Console.WriteLine("--- Comanda em andamento (rascunho) ---");
TestesRascunho.Rodar((cond, nome) => Check("rascunho: " + nome, cond));

// -- INSTANCIA UNICA: o operador que clica duas vezes no icone -----------------
// A 2a instancia attacha no turno aberto e roda o religamento do TEF usando o
// PROPRIO boot como referencia: a cobranca viva da 1a instancia vira 'orfa'.
Console.WriteLine();
Console.WriteLine("--- Instancia unica (2o Pdv.exe na mesma maquina) ---");
TestesInstanciaUnica.Rodar((cond, nome) => Check("instancia: " + nome, cond));

// -- LARGURA DO PAPEL: 58 / 80 / 100 mm --------------------------------------
// A largura da bobina era constante de compilacao (80 mm / 48 colunas). Numa
// bobina de 58 mm cabem 32 colunas, e a linha do item sai SEM quebra automatica:
// o que passa do papel nao quebra, some -- e some no fim, onde esta o valor.
Console.WriteLine();
Console.WriteLine("--- Largura do papel (bobina configuravel) ---");
await TestesImpressao.RodarAsync((cond, nome) => Check("papel: " + nome, cond));

// -- ASSISTENTE DE CONFIGURACAO: os 5 passos da instalacao --------------------
// A configuracao era um formulario unico de 8 secoes: o que faltava so aparecia
// no popup do Salvar, depois de rolar tudo. Agora cada passo diz o que bloqueia,
// na tela, e o Salvar pula pro passo culpado -- continua sendo a porta unica.
Console.WriteLine();
Console.WriteLine("--- Assistente de configuracao (5 passos) ---");
TestesAssistente.Rodar((cond, nome) => Check("config: " + nome, cond));

// -- CATEGORIAS: a ordem da coluna e da grade na tela de venda ---------------
// A ordem vinha do ORDER BY do SQLite, que compara texto por BYTE: AGUA MINERAL
// COM GAS era o ULTIMO item de Bebidas, depois de SUCO UVA. Acento e a MESMA
// letra em portugues, e a vitrine de PROMOCAO nao entra no alfabeto.
Console.WriteLine();
Console.WriteLine("--- Ordem das categorias e da grade (alfabetica pt-BR) ---");
TestesCategorias.Rodar((cond, nome) => Check("categoria: " + nome, cond));

// -- AUTORIZACAO DE ESTORNO: token de WhatsApp, PIN como saida ----------------
// Estorno e o unico ponto onde dinheiro VOLTA sem venda. Ate hoje bastava o PIN
// guardado no banco do proprio caixa; agora quem autoriza esta fora da loja.
Console.WriteLine();
Console.WriteLine("--- Autorizacao de estorno (token por WhatsApp) ---");
await TestesAutorizacao.RodarAsync((cond, nome) => Check("autorizacao: " + nome, cond));

// -- ATUALIZAR O CAIXA: o botao que baixa e entrega ao instalador -------------
// O campo "url" vem da REDE e vira um exe rodando como ADMINISTRADOR na maquina da
// loja. Aqui moram as peneiras: https, mesmo dominio do manifesto, versao semantica
// (0.10.0 > 0.9.0), integridade antes de executar, e o portao que recusa trocar o
// programa com comanda aberta ou cobranca no pinpad.
Console.WriteLine();
Console.WriteLine("--- Atualizar o caixa (versao, manifesto, portoes, download) ---");
await TestesAtualizacao.RodarAsync((cond, nome) => Check("atualizacao: " + nome, cond));

// -- INSTALADOR: a unica vez que a loja ve o sistema pela primeira vez --------
// Ele copiava SO o Pdv.exe. O publish deixa as bibliotecas nativas soltas ao lado,
// entao a instalacao dizia "concluido", criava o atalho, registrava o programa --
// e o PDV morria antes da primeira tela. Nada no build acusava.
Console.WriteLine();
Console.WriteLine("--- Instalador (origem completa, copia por cima, dados intactos) ---");
TestesInstalador.Rodar((cond, nome) => Check("instalador: " + nome, cond));


// ── 03/09/2026: caixa da Savassi ──────────────────────────────────────────
// Banco PROPRIO: o `cx` da bateria chega aqui com a sessao da Maria aberta (a
// suite deixa de proposito), e Caixa.Abrir recusa uma segunda sessao. Em 03/09 a
// suite MORREU aqui em excecao nao tratada e a esteira nao viu (so procurava
// "FALHA"): 0.3.3 e 0.3.4 sairam com o fim da bateria sem rodar.
var arquivoT = Path.Combine(Path.GetTempPath(), $"pdv-teste-0309-{Guid.NewGuid():N}.db");
Banco.Migrar(arquivoT);
var cxT = Banco.Abrir(arquivoT);
// (a) a tolerancia nao vai na mensagem do operador
{
    var opT = new Operador("t-tol", "Tolerancia", "operador");
    Operadores.Salvar(cxT, opT.Id, opT.Nome, "1234", "operador");
    var sT = Caixa.Abrir(cxT, opT, Dinheiro.DeReais(100));
    var contagem = new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.DeReais(50) };
    string? msg = null;
    try { Caixa.Fechar(cxT, sT, contagem, opT, Dinheiro.DeReais(2)); }
    catch (InvalidOperationException e) { msg = e.Message; }
    Check("fechamento com diferenca ainda pede justificativa", msg is not null && msg.Contains("Justifique"));
    Check("a mensagem NAO revela a tolerancia", msg is not null && !msg.Contains("toler", StringComparison.OrdinalIgnoreCase));
    Caixa.Fechar(cxT, sT, contagem, opT, Dinheiro.DeReais(2), "teste");
}
// (b) pix do TEF com NSU e sem codigo de autorizacao NAO e contado no fechamento
{
    var opP = new Operador("t-pixnsu", "PixNsu", "operador");
    Operadores.Salvar(cxT, opP.Id, opP.Nome, "4321", "operador");
    Vendas.GravarConfig(cxT, "tef_habilitado", "1");
    var sP = Caixa.Abrir(cxT, opP, Dinheiro.DeReais(100));
    var vP = Guid.NewGuid().ToString();
    cxT.Execute("""
        INSERT INTO venda (id, client_key, sessao_id, business_date, numero_local, operador_id,
                           subtotal_cent, total_cent, status, criada_em, finalizada_em)
        VALUES (@Id,@K,@S,@Bd,@N,@Op,@T,@T,'finalizada',@Em,@Em)
        """,
        new { Id = vP, K = vP, S = sP.Id, Bd = sP.BusinessDate,
              N = cxT.ExecuteScalar<int>("SELECT COALESCE(MAX(numero_local),0)+1 FROM venda WHERE business_date=@B", new { B = sP.BusinessDate }),
              Op = opP.Id, T = Dinheiro.DeReais(10).Centavos, Em = DateTime.Now.ToString("o") });
    cxT.Execute("INSERT INTO venda_pagamento (id,venda_id,forma,valor_cent,troco_cent,tef_aut,tef_nsu) VALUES (@i,@v,'pix',1000,0,'NSU:123456','123456')",
        new { i = Guid.NewGuid().ToString(), v = vP });
    Check("pix com carimbo NSU nao volta para a contagem", !Caixa.FormasContadas(cxT, sP).Contains("pix"));
    Vendas.GravarConfig(cxT, "tef_habilitado", "0");
}
// (c) voucher: tipo, codigo, tPag e formas contadas sem TEF
Check("voucher analisa a partir de 'refeicao'/'vr'", TipoTefExtensoes.Analisar("refeicao") == TipoTef.Voucher && TipoTefExtensoes.Analisar("vr") == TipoTef.Voucher);
Check("voucher tem codigo estavel", TipoTef.Voucher.Codigo() == "voucher");
Check("voucher e contado no fechamento SEM TEF", Caixa.FormasContadas(cxT).Contains("voucher"));
Check("voucher vira tPag 11 (vale-refeicao) na NFC-e", Fiscal.TPagDaForma("voucher") == "11");

// ── 03/09/2026 (2): promoção de carrinho na gravação e na nota ────────────
{
    var arquivoF = Path.Combine(Path.GetTempPath(), $"pdv-teste-promo-{Guid.NewGuid():N}.db");
    Banco.Migrar(arquivoF);
    using var cxF = Banco.Abrir(arquivoF);
    var opF = new Operador("t-promo", "Promo", "operador");
    Operadores.Salvar(cxF, opF.Id, opF.Nome, "7777", "operador");
    var sF = Caixa.Abrir(cxF, opF, Dinheiro.DeReais(100));
    LinhaVenda Linha(string id, string nome, long preco, long qtdMil, long desconto = 0, string? promo = null, long gratis = 0)
        => new(id, id, nome, new Quantidade(qtdMil), new Dinheiro(preco),
               new Dinheiro(new Dinheiro(preco).VezesQtd(qtdMil).Centavos - desconto),
               "UN", "19053100", null, "102", null, 0,
               new Dinheiro(desconto), promo, promo is null ? null : "compre e ganhe", gratis);
    var pagos2190 = new[] { new PagamentoVenda("dinheiro", Dinheiro.DeReais(21.90m), Dinheiro.Zero) };
    var linhasBrinde = new[] { Linha("A", "DONUT NUTELLA", 2190, 1000), Linha("B", "DONUT NINHO", 2190, 1000, 2190, "cg", 1000) };
    var vg = Vendas.Finalizar(cxF, sF, opF, linhasBrinde, pagos2190, null, "Loja Teste", null);
    var v = cxF.QueryFirst("SELECT subtotal_cent, desconto_cent, total_cent, promo_id, promo_nome FROM venda WHERE id = @i", new { i = vg.Id });
    Check("venda com brinde: subtotal e o BRUTO (43,80)", (long)v.subtotal_cent == 4380);
    Check("venda com brinde: desconto 21,90 e total 21,90", (long)v.desconto_cent == 2190 && (long)v.total_cent == 2190);
    Check("venda com brinde: promo_id e nome gravados", (string)v.promo_id == "cg" && (string)v.promo_nome == "compre e ganhe");
    var itB = cxF.QueryFirst("SELECT desconto_cent, total_cent, promo_id, gratis_milesimo FROM venda_item WHERE venda_id = @i AND produto_id = 'B'", new { i = vg.Id });
    Check("item brinde: desconto 21,90, total 0, promo, 1 gratis",
        (long)itB.desconto_cent == 2190 && (long)itB.total_cent == 0 && (string)itB.promo_id == "cg" && (long)itB.gratis_milesimo == 1000);
    var itA = cxF.QueryFirst("SELECT desconto_cent, total_cent, promo_id, gratis_milesimo FROM venda_item WHERE venda_id = @i AND produto_id = 'A'", new { i = vg.Id });
    Check("item pago: sem desconto, sem promo, sem gratis", (long)itA.desconto_cent == 0 && (long)itA.total_cent == 2190 && itA.promo_id is null && (long)itA.gratis_milesimo == 0);
    var payloadF = cxF.ExecuteScalar<string>("SELECT payload FROM outbox ORDER BY rowid DESC LIMIT 1") ?? "";
    Check("payload da nuvem: desconto por item e promocao_id", payloadF.Contains("\"desconto\":21.9") && payloadF.Contains("\"promocao_id\":\"cg\""));
    Check("payload da nuvem: p_desconto continua 0 (desconto vai POR item, nao dobra)", payloadF.Contains("\"p_desconto\":0"));
    Check("payload da nuvem: observacao nomeia a promocao", payloadF.Contains("\"p_observacao\":\"Promo"));
    var aud = cxF.ExecuteScalar<long>("SELECT COUNT(*) FROM auditoria WHERE evento = 'promo_aplicada'");
    Check("auditoria promo_aplicada gravada", aud == 1);
    Recusa("desconto maior que o item e recusado",
        () => Vendas.Finalizar(cxF, sF, opF, new[] { Linha("A", "X", 1000, 1000, 1500, "p") },
            new[] { new PagamentoVenda("dinheiro", Dinheiro.DeReais(-5m), Dinheiro.Zero) }, null, "Loja", null), "Desconto inválido");
    Recusa("duas promocoes na mesma venda e recusado",
        () => Vendas.Finalizar(cxF, sF, opF, new[] { Linha("A", "X", 1000, 1000, 100, "p1"), Linha("B", "Y", 1000, 1000, 100, "p2") },
            new[] { new PagamentoVenda("dinheiro", Dinheiro.DeReais(18m), Dinheiro.Zero) }, null, "Loja", null), "Duas promoções");
    Recusa("venda so com brinde (total zero) e recusada",
        () => Vendas.Finalizar(cxF, sF, opF, new[] { Linha("B", "Y", 1000, 1000, 1000, "p1", 1000) },
            new[] { new PagamentoVenda("dinheiro", Dinheiro.Zero, Dinheiro.Zero) }, null, "Loja", null), "sem valor");
    // os 12 posicionais antigos continuam valendo (desconto zero)
    var vg2 = Vendas.Finalizar(cxF, sF, opF,
        new[] { new LinhaVenda("A", "A", "DONUT", Quantidade.Um, new Dinheiro(1000), new Dinheiro(1000), "UN", null, null, null, null, 0) },
        new[] { new PagamentoVenda("dinheiro", Dinheiro.DeReais(10m), Dinheiro.Zero) }, null, "Loja", null);
    var vSem = cxF.QueryFirst("SELECT subtotal_cent, desconto_cent, total_cent, promo_id FROM venda WHERE id = @i", new { i = vg2.Id });
    Check("linha sem desconto: subtotal = total, desconto 0, sem promo", (long)vSem.subtotal_cent == 1000 && (long)vSem.desconto_cent == 0 && (long)vSem.total_cent == 1000 && vSem.promo_id is null);

    // ── nota fiscal: vDesc por item x transitório ──
    var fA = ItemFiscal.De("B", "DONUT NINHO", "19053100", null, "102", null, "UN", Quantidade.Um, new Dinheiro(2190), new Dinheiro(2190), 0, new Dinheiro(2190));
    Check("ItemFiscal com desconto: vUnit de tabela (21,90) e vDesc 21,90", fA.VUnit == 21.90m && fA.VDesc == 21.90m);
    var comV = ItemFiscal.DaVenda(linhasBrinde, true);
    Check("DaVenda com vDesc: vProd cheio nos dois itens e total da nota liquido 21,90",
        comV[1].VUnit == 21.90m && comV[1].VDesc == 21.90m && Fiscal.TotalDaNota(comV) == 21.90m);
    var pagFiscal = new[] { PagamentoFiscal.De("dinheiro", Dinheiro.DeReais(21.90m), null) };
    Check("Conferir aceita pagamento liquido quando ha vDesc", Fiscal.Conferir(comV, pagFiscal) is null);
    Check("Conferir recusa pagamento no bruto quando ha vDesc", Fiscal.Conferir(comV, new[] { PagamentoFiscal.De("dinheiro", Dinheiro.DeReais(43.80m), null) }) is not null);
    var semV = ItemFiscal.DaVenda(linhasBrinde, false);
    Check("DaVenda transitorio: brinde sai com vUnit 0, sem vDesc, e a nota fecha em 21,90",
        semV[1].VUnit == 0m && semV[1].VDesc == 0m && Fiscal.TotalDaNota(semV) == 21.90m && Fiscal.Conferir(semV, pagFiscal) is null);
    Check("Conferir recusa vDesc maior que o item", Fiscal.Conferir(new[] { fA with { VDesc = 30m } }, pagFiscal) is not null);
    Check("produto de preco zero sem desconto continua passando", Fiscal.Conferir(
        new[] { ItemFiscal.De("Z", "BRINDE INSTITUCIONAL", null, null, "102", null, "UN", Quantidade.Um, Dinheiro.Zero), fA with { VDesc = 0m } },
        pagFiscal) is null);
    var metade = ItemFiscal.DaVenda(new[] { Linha("A", "DONUT", 1200, 2000, 1200, "p", 1000) }, false);
    Check("transitorio: 2 x 12,00 com 1 gratis vira vUnit 6,00 e total 12,00", metade[0].VUnit == 6.00m && Fiscal.TotalDaNota(metade) == 12.00m);
    Caixa.Fechar(cxF, sF, new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.DeReais(131.90m) }, opF, Dinheiro.DeReais(2), null);
    cxF.Dispose();
    try { File.Delete(arquivoF); } catch { }
}
cxT.Dispose();
try { File.Delete(arquivoT); } catch { }
cx.Dispose();
SqliteConnection.ClearAllPools();
try { File.Delete(arquivo); } catch { }

Console.WriteLine($"\n=== {ok} OK, {falhas} falhas ===");
return falhas == 0 ? 0 : 1;
