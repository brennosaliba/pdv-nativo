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
        var cpfLimpo = string.IsNullOrWhiteSpace(cpf) ? null : Documentos.SoDigitos(cpf);
        if (cpfLimpo is not null)
        {
            if (!Documentos.CpfValido(cpfLimpo))
                throw new InvalidOperationException("CPF inválido — confira os dígitos.");
            var dono = cx.ExecuteScalar<string?>(
                "SELECT nome FROM operador WHERE cpf = @C AND ativo = 1 AND id <> @Id",
                new { C = cpfLimpo, Id = id });
            if (dono is not null)
                throw new InvalidOperationException($"Esse CPF já é de {dono}.");
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
