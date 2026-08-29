using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>
/// Identidade de quem opera o caixa.
///
/// PIN curto (4-6 dígitos) e não senha: com fila no balcão, senha longa em tela touch
/// mata a velocidade e o operador acaba anotando num papel colado no monitor — o que é
/// pior que PIN curto. A proteção vem de outro lado: o PIN é individual (nunca
/// compartilhado), toda ação sensível fica auditada com o nome de quem fez, e há
/// bloqueio por tentativas.
///
/// Guardado com PBKDF2 (100k iterações, SHA-256) + salt por operador. Mesmo com o
/// banco do caixa na mão, ninguém descobre o PIN — e é banco de loja, que roda em PC
/// destrancado, então isso importa.
/// </summary>
public static class Operadores
{
    private const int Iteracoes = 100_000;
    private const int TamanhoHash = 32;

    public static (string hash, string salt) GerarHash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iteracoes, HashAlgorithmName.SHA256, TamanhoHash);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Confere(string pin, string hashGuardado, string saltGuardado)
    {
        try
        {
            var salt = Convert.FromBase64String(saltGuardado);
            var esperado = Convert.FromBase64String(hashGuardado);
            var calculado = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iteracoes, HashAlgorithmName.SHA256, TamanhoHash);
            // comparação em tempo fixo: não vaza quantos dígitos estão certos
            return CryptographicOperations.FixedTimeEquals(calculado, esperado);
        }
        catch { return false; }
    }

    public static void Salvar(SqliteConnection cx, string id, string nome, string pin, string perfil,
        string? cpf = null)
    {
        if (!PinValido(pin)) throw new InvalidOperationException("O PIN deve ter de 4 a 6 dígitos.");

        // CPF é a identidade preferida do login ("foi ELE que abriu o caixa" precisa
        // apontar para um documento, não para um número decorável). Um CPF por pessoa:
        // repetido, a atribuição desmorona igualzinho ao PIN repetido.
        var cpfLimpo = string.IsNullOrWhiteSpace(cpf) ? null : CpfChave(cpf);
        if (!string.IsNullOrWhiteSpace(cpf) && cpfLimpo is null)
            throw new InvalidOperationException("CPF inválido — confira os dígitos.");
        if (cpfLimpo is not null)
        {
            // A comparação limpa a pontuação DOS DOIS LADOS: build antigo gravava o CPF
            // da nuvem do jeito que o painel escreveu, e "529.982.247-25" nunca casaria
            // com "52998224725" — a guarda passaria batido justo na duplicata que importa.
            // Linha da NUVEM conta mesmo inativa: o que se está prevenindo é o nascimento
            // de um SEGUNDO id para uma pessoa que o painel já governa.
            var dono = cx.QueryFirstOrDefault("""
                SELECT nome, da_nuvem FROM operador
                 WHERE id <> @Id AND cpf IS NOT NULL AND cpf <> ''
                   AND replace(replace(replace(cpf,'.',''),'-',''),' ','') = @C
                   AND (ativo = 1 OR da_nuvem = 1)
                 ORDER BY da_nuvem DESC LIMIT 1
                """, new { C = cpfLimpo, Id = id });
            if (dono is not null)
            {
                // POR QUE A MENSAGEM MUDA QUANDO O DONO VEM DO PAINEL: "esse CPF já é de
                // fulano" não diz o que fazer, e o instalador contorna cadastrando com
                // outro documento — que é como nasce o segundo id. O servidor não pergunta
                // "existe algum operador?", pergunta "existe ESTE id?": um id que só existe
                // no caixa faz TODA venda dele voltar com 409 e virar dead-letter.
                if ((long)dono.da_nuvem == 1)
                    throw new InvalidOperationException(
                        $"{(string)dono.nome} já vem do PAINEL com este CPF. Criar de novo aqui faria " +
                        "nascer um segundo cadastro para a mesma pessoa, e o painel recusa toda venda " +
                        "assinada por alguém que ele não conhece. O QUE FAZER: quem vem do painel entra " +
                        "no caixa com o CPF e a senha dele — não precisa cadastrar. Para trocar nome, " +
                        "perfil ou senha, mexa no cadastro do painel e toque em Sincronizar.");
                throw new InvalidOperationException($"Esse CPF já é de {(string)dono.nome}.");
            }
        }

        // O PIN é a identidade de quem ainda NÃO tem CPF: o login legado não pede nome,
        // então PIN repetido faz a venda e a sangria irem para a pessoa errada. Pior,
        // um operador repetindo o PIN de um supervisor autorizaria a própria sangria.
        if (DonoDoPin(cx, pin, ignorarId: id) is { } jaUsa)
            throw new InvalidOperationException(
                $"Esse PIN já é de {jaUsa.Nome}. Como o login é só pelo PIN, dois iguais fariam " +
                "as vendas de um cair no nome do outro. Escolha outro.");

        var (hash, salt) = GerarHash(pin);
        cx.Execute("""
            INSERT INTO operador (id, nome, pin_hash, pin_salt, perfil, cpf, ativo, atualizado)
            VALUES (@Id,@Nome,@H,@S,@P,@Cpf,1,@Em)
            ON CONFLICT(id) DO UPDATE SET nome=@Nome, pin_hash=@H, pin_salt=@S, perfil=@P,
                                          cpf=COALESCE(@Cpf, cpf), atualizado=@Em
            """,
            new { Id = id, Nome = nome, H = hash, S = salt, P = perfil, Cpf = cpfLimpo, Em = DateTime.Now.ToString("o") });
    }

    /// <summary>
    /// Login por CPF + senha — a identidade forte. O CPF diz QUEM é (documento, não
    /// número decorável) e a senha prova. É o que amarra "foi ele que abriu o caixa"
    /// a uma pessoa de verdade.
    /// </summary>
    public static Operador? EntrarComCpf(SqliteConnection cx, string cpf, string senha)
    {
        var limpo = Documentos.SoDigitos(cpf);
        if (!Documentos.CpfValido(limpo) || !PinValido(senha)) return null;
        var r = cx.QueryFirstOrDefault(
            "SELECT id, nome, pin_hash, pin_salt, perfil FROM operador WHERE cpf = @C AND ativo = 1",
            new { C = limpo });
        if (r is null) return null;
        return Confere(senha, (string)r.pin_hash, (string)r.pin_salt)
            ? new Operador((string)r.id, (string)r.nome, (string)r.perfil)
            : null;
    }

    public static bool PinValido(string pin) =>
        pin.Length is >= 4 and <= 6 && pin.All(char.IsDigit);

    /// <summary>Por que o login não passou — o motivo muda a mensagem na tela.</summary>
    public enum Recusa { Ok, NaoConfere, PinDuplicado }

    /// <summary>
    /// Quem já usa este PIN entre os operadores ativos, se alguém usar.
    /// Varre todos porque o hash tem salt por operador — não dá pra consultar por índice.
    /// </summary>
    public static Operador? DonoDoPin(SqliteConnection cx, string pin, string? ignorarId = null)
        => TodosQueUsam(cx, pin, ignorarId).FirstOrDefault();

    private static List<Operador> TodosQueUsam(SqliteConnection cx, string pin, string? ignorarId = null,
        bool soSemCpf = false)
    {
        var achados = new List<Operador>();
        if (!PinValido(pin)) return achados;
        // varre os ativos: são poucos (uma loja tem 3-10), e assim o PIN sozinho já
        // identifica quem é — um toque a menos na fila.
        var sql = soSemCpf
            ? "SELECT id, nome, pin_hash, pin_salt, perfil FROM operador WHERE ativo = 1 AND (cpf IS NULL OR cpf = '')"
            : "SELECT id, nome, pin_hash, pin_salt, perfil FROM operador WHERE ativo = 1";
        foreach (var o in cx.Query(sql))
        {
            if (ignorarId is not null && (string)o.id == ignorarId) continue;
            if (Confere(pin, (string)o.pin_hash, (string)o.pin_salt))
                achados.Add(new Operador((string)o.id, (string)o.nome, (string)o.perfil));
        }
        return achados;
    }

    /// <summary>
    /// Entrada legada, só pelo PIN — vale apenas para quem AINDA NÃO tem CPF
    /// cadastrado. Assim ninguém fica trancado na transição, e assim que o CPF entra,
    /// o caminho fraco fecha sozinho para aquela pessoa.
    /// </summary>
    public static Operador? Entrar(SqliteConnection cx, string pin) => Entrar(cx, pin, out _);

    public static Operador? Entrar(SqliteConnection cx, string pin, out Recusa motivo)
    {
        var achados = TodosQueUsam(cx, pin, soSemCpf: true);

        // Dois donos para o mesmo PIN não deveria acontecer (Salvar barra), mas pode
        // chegar pela sincronização com a nuvem. Aqui a única resposta honesta é
        // recusar: entrar como "algum dos dois" assina venda e sangria no nome errado,
        // e é justamente essa assinatura que dá valor ao registro.
        if (achados.Count > 1) { motivo = Recusa.PinDuplicado; return null; }
        if (achados.Count == 0) { motivo = Recusa.NaoConfere; return null; }

        motivo = Recusa.Ok;
        return achados[0];
    }

    /// <summary>
    /// Autorização de supervisor para uma ação sensível (desconto acima do teto,
    /// cancelamento, sangria). Devolve QUEM autorizou — o registro sem nome não serve
    /// de nada numa auditoria.
    /// </summary>
    public static Operador? AutorizarSupervisor(SqliteConnection cx, string pin)
    {
        // Autorização NÃO é login: o supervisor digita só o PIN dele por cima do turno
        // de outra pessoa, com fila. Aqui a varredura inclui quem tem CPF — senão
        // cadastrar o CPF do supervisor quebraria a sangria no meio do expediente.
        var achados = TodosQueUsam(cx, pin);
        if (achados.Count != 1) return null;       // duplicata = ninguém autoriza
        return achados[0] is { ESupervisor: true } q ? q : null;
    }

    // ── IDENTIDADE: UMA PESSOA, UM ID ───────────────────────────────────────

    /// <summary>
    /// CPF reduzido à CHAVE DE IDENTIDADE — o que se compara para dizer "é a mesma
    /// pessoa". Tira pontuação, repõe o zero à esquerda que planilha e campo numérico
    /// comem (09595270601 vira 9595270601 e deixa de casar consigo mesmo) e exige um
    /// CPF que EXISTE.
    ///
    /// Devolver `null` para o inválido é a metade que importa: sem documento não há
    /// identidade, e dois operadores sem CPF são DUAS PESSOAS — vazio não casa com
    /// vazio. Exigir os dígitos verificadores fecha a outra ponta: "000.000.000-00"
    /// digitado por dois instaladores diferentes fundiria dois estranhos num só.
    /// </summary>
    public static string? CpfChave(string? cpf)
    {
        var d = Documentos.SoDigitos(cpf);
        if (d.Length is 10 or 11) d = d.PadLeft(11, '0');
        return Documentos.CpfValido(d) ? d : null;
    }

    /// <summary>
    /// O id com que ESTE operador deve assinar o que é novo.
    ///
    /// POR QUE ISTO EXISTE: quem logou às 8h carrega na memória a identidade daquele
    /// momento. Se a sincronização das 10h reconciliar esse operador com o cadastro do
    /// painel, o resto do turno continuaria nascendo com o id antigo — e o id antigo é
    /// justamente o que a nuvem recusa. Deslogar todo mundo na hora da sincronização não
    /// é opção (fila no balcão), então a tradução acontece na hora de gravar.
    ///
    /// O laço é cinto de segurança: uma lápide aponta para uma linha do painel, e linha
    /// do painel não vira lápide — encadeamento não deveria existir. Mas um ciclo aqui
    /// travaria a VENDA, então o número de saltos é limitado.
    /// </summary>
    public static string IdCanonico(SqliteConnection cx, string id)
    {
        var atual = id;
        for (var salto = 0; salto < 4; salto++)
        {
            var proximo = cx.ExecuteScalar<string?>(
                "SELECT mesmo_que FROM operador WHERE id = @Id", new { Id = atual });
            if (string.IsNullOrWhiteSpace(proximo) || proximo == atual) break;
            atual = proximo;
        }
        return atual;
    }

    /// <summary>
    /// RECONCILIAÇÃO NA DESCIDA: o operador que nasceu NO CAIXA e o que veio do PAINEL
    /// com o mesmo CPF são a mesma pessoa, e passam a ter uma identidade só — a do painel.
    ///
    /// O DEFEITO QUE ISTO FECHA. O assistente de configuração cria o primeiro operador
    /// no passo 1, com um id gerado aqui; o pareamento é o passo 5, e o painel só é
    /// consultado na sincronização seguinte. Quando a MESMA pessoa desce de lá, com
    /// outro id, os dois ficavam vivos lado a lado — e o local continuava sendo quem
    /// logava, porque foi inserido primeiro. Toda venda dele subia assinada por um id
    /// que `employees` não tem e voltava com HTTP 409 / 23503, até virar dead-letter.
    /// Medido no caixa da Savassi em 29/08/2026: 16 vendas, R$ 102.626,50.
    ///
    /// O CAMINHO ESCOLHIDO — LÁPIDE, NÃO TROCA DE ID. A linha local não é apagada nem
    /// tem o id trocado: venda, sessão de caixa, movimento, rascunho e auditoria apontam
    /// para ele, e a MAIORIA dessas colunas não tem chave estrangeira — reapontar em
    /// cascata orfanaria em silêncio o que fosse esquecido, e reescreveria quem assinou
    /// vendas JÁ FECHADAS. A linha antiga fica de pé como âncora do histórico, marcada
    /// com <c>mesmo_que</c>; quem assina o que é NOVO passa a ser o id do painel.
    ///
    /// O PIN NÃO PODE QUEBRAR. A senha que a loja digita todo dia é a da linha LOCAL; a
    /// do painel pode ser outra e ninguém no balcão a conhece. Ela viaja para a
    /// identidade nova — senão a loja abre amanhã e ninguém entra no caixa.
    ///
    /// SÓ RECONCILIA CONTRA OPERADOR ATIVO NO PAINEL. Adotar uma identidade que o painel
    /// já desligou trocaria um problema de fila (caro, mas com dinheiro na gaveta e
    /// conserto possível) por um caixa que não abre (a loja para).
    /// </summary>
    /// <returns>Quantas linhas locais foram reconhecidas como esta pessoa.</returns>
    public static int ReconciliarComNuvem(SqliteConnection cx, SqliteTransaction? tx,
        string idNuvem, string? cpfNuvem, bool ativoNaNuvem, string agora)
    {
        var chave = CpfChave(cpfNuvem);
        if (chave is null || !ativoNaNuvem) return 0;

        // Varre os locais e normaliza EM C#, com a mesma função dos dois lados: a loja
        // tem 3-10 operadores, e comparar em SQL exigiria repetir a normalização numa
        // segunda linguagem — que é onde as duas versões da regra se separam.
        var candidatos = cx.Query("""
            SELECT id, cpf, pin_hash, pin_salt FROM operador
             WHERE da_nuvem = 0 AND mesmo_que IS NULL AND id <> @Id AND id <> '_admin_'
               AND cpf IS NOT NULL AND cpf <> ''
            """, new { Id = idNuvem }, tx).ToList();

        var n = 0;
        foreach (var l in candidatos)
        {
            if (CpfChave((string?)l.cpf) != chave) continue;
            var idLocal = (string)l.id;

            // 1. A linha local vira LÁPIDE: continua existindo (o histórico aponta para
            //    ela), diz de quem ela é, e sai do caminho do login — senão o login por
            //    CPF continuaria achando duas linhas e escolhendo "a primeira".
            cx.Execute(
                "UPDATE operador SET mesmo_que = @Nuvem, ativo = 0, atualizado = @Em WHERE id = @Id",
                new { Nuvem = idNuvem, Id = idLocal, Em = agora }, tx);

            // 2. A senha EM USO viaja para a identidade nova. `pin_nuvem_hash` já guarda
            //    o que o painel mandou agora, então a descida seguinte vê "o painel não
            //    mexeu" e preserva esta — e no dia em que alguém trocar a senha no
            //    painel, aí sim ela passa a valer (ver BaixarOperadoresAsync).
            cx.Execute("UPDATE operador SET pin_hash = @H, pin_salt = @S, atualizado = @Em WHERE id = @Id",
                new { H = (string)l.pin_hash, S = (string)l.pin_salt, Id = idNuvem, Em = agora }, tx);

            Caixa.Auditar(cx, tx, "operador_reconciliado", idNuvem, null,
                $"{idLocal} (criado no caixa) e {idNuvem} (painel) têm o mesmo CPF — mesma pessoa. " +
                "A senha usada no caixa foi preservada; o histórico continua apontando para a linha antiga.");
            n++;
        }
        return n;
    }

    /// <summary>
    /// O ADMINISTRADOR DA LOJA (o dono), gravado pelo assistente de configuração.
    ///
    /// O DEFEITO QUE ISTO FECHA, DO OUTRO LADO. A reconciliação conserta depois; aqui o
    /// cadastro errado nem chega a nascer. O assistente pede este operador no PASSO 1 e
    /// pareia no PASSO 5 — quando o Salvar roda, o pareamento já aconteceu e a lista do
    /// painel já desceu. Se essa pessoa já está lá, criar outra linha faria dois ids
    /// para uma pessoa só, e a nuvem recusa com 409 toda venda assinada pelo id que ela
    /// não conhece.
    ///
    /// ADOTA, NÃO RECUSA. Barrar o Salvar no meio da instalação deixaria o instalador
    /// sem saída (ele precisa de alguém que abra o caixa hoje). A identidade passa a ser
    /// a do painel e a SENHA é a que o dono acabou de escolher — mesma regra da
    /// reconciliação: é essa senha que abre o caixa amanhã, e a do painel pode ser outra
    /// que ninguém na loja conhece.
    ///
    /// E QUANDO O PAINEL AINDA NÃO DESCEU (sem rede no pareamento): o cadastro local
    /// nasce mesmo assim — o caixa TEM que abrir antes da primeira sincronização — e a
    /// reconciliação na descida funde os dois no primeiro Sincronizar.
    /// </summary>
    /// <returns>O id com que essa pessoa passa a entrar no caixa, e se ele veio do painel.</returns>
    public static (string Id, bool AdotadoDoPainel) SalvarAdministrador(
        SqliteConnection cx, SqliteTransaction? tx, string nome, string pin, string? cpf)
    {
        nome = (nome ?? "").Trim();
        pin = (pin ?? "").Trim();
        if (nome.Length < 2) throw new InvalidOperationException("Falta o nome do administrador (o dono da loja).");
        // CPF é o login: sem ele, a abertura de caixa não tem dono de verdade
        var chave = CpfChave(cpf);
        if (chave is null)
            throw new InvalidOperationException("CPF do administrador inválido — é com ele que o dono entra no caixa.");
        if (!PinValido(pin))
            throw new InvalidOperationException("A senha do administrador deve ter de 4 a 6 dígitos.");

        var (h, s) = GerarHash(pin);
        var agora = DateTime.Now.ToString("o");

        // A comparação limpa a pontuação do lado do BANCO também: build antigo gravava o
        // CPF da nuvem do jeito que o painel escreveu.
        var doPainel = cx.ExecuteScalar<string?>("""
            SELECT id FROM operador
             WHERE da_nuvem = 1 AND ativo = 1 AND cpf IS NOT NULL AND cpf <> ''
               AND replace(replace(replace(cpf,'.',''),'-',''),' ','') = @Cpf
             LIMIT 1
            """, new { Cpf = chave }, tx);

        if (doPainel is not null)
        {
            // `pin_nuvem_hash` já guarda o que o painel mandou na descida, então a
            // sincronização seguinte PRESERVA esta senha em vez de reescrevê-la por cima
            // (ver o CASE em Nuvem.BaixarOperadoresAsync). Sem isso o conserto duraria
            // até o próximo ciclo e a loja não abriria.
            cx.Execute("UPDATE operador SET pin_hash=@H, pin_salt=@S, atualizado=@Em WHERE id=@Id",
                new { H = h, S = s, Id = doPainel, Em = agora }, tx);
            Caixa.Auditar(cx, tx, "admin_adotado_do_painel", doPainel, null,
                $"{nome} já vinha do painel com este CPF — nenhum cadastro local foi criado " +
                "(dois ids para a mesma pessoa é o que faz a nuvem recusar as vendas)");
            return (doPainel, true);
        }

        var id = Guid.NewGuid().ToString();
        cx.Execute("""
            INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,cpf,ativo,atualizado)
            VALUES (@Id,@N,@H,@S,'gerente',@Cpf,1,@Em)
            """, new { Id = id, N = nome, H = h, S = s, Cpf = chave, Em = agora }, tx);
        return (id, false);
    }

    public static bool ExisteAlgum(SqliteConnection cx) =>
        cx.ExecuteScalar<int>("SELECT COUNT(*) FROM operador WHERE ativo = 1") > 0;

    /// <summary>
    /// MODO DE HOMOLOGAÇÃO (config `homologacao` = 1): o primeiro operador ativo, gerente/
    /// supervisor primeiro — é quem "entra" sem senha e quem autoriza sem PIN. Fora desse modo
    /// ninguém chama isto: em operação real não existe atalho de senha.
    /// </summary>
    public static Operador? PrimeiroAtivo(SqliteConnection cx)
        => cx.QueryFirstOrDefault<Operador>("""
            SELECT id AS Id, nome AS Nome, perfil AS Perfil FROM operador
             WHERE ativo = 1 AND id <> '_admin_'
             ORDER BY CASE perfil WHEN 'gerente' THEN 0 WHEN 'supervisor' THEN 1 ELSE 2 END, nome
             LIMIT 1
            """);

    /// <summary>Idem, só supervisor/gerente (autorização sem PIN no modo de homologação).</summary>
    public static Operador? PrimeiroSupervisor(SqliteConnection cx)
        => cx.QueryFirstOrDefault<Operador>("""
            SELECT id AS Id, nome AS Nome, perfil AS Perfil FROM operador
             WHERE ativo = 1 AND id <> '_admin_' AND perfil IN ('gerente','supervisor')
             ORDER BY CASE perfil WHEN 'gerente' THEN 0 ELSE 1 END, nome
             LIMIT 1
            """);
}
