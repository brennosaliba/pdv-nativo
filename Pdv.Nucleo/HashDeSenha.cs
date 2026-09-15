using System.Security.Cryptography;
using System.Text;

namespace Pdv.Nucleo;

/// <summary>
/// O PBKDF2 das senhas do caixa, num lugar só (15/09/2026).
///
/// PBKDF2-SHA256, 100.000 iterações, sal de 16 bytes, hash de 32, os dois em base64. É o mesmo
/// cálculo do painel (erp-american-day src/pdv/lib/pinCaixa.ts, WebCrypto): o navegador faz o hash
/// e o caixa só confere. Vetor provado dos dois lados: senha 1234 com sal 01..10 dá
/// zXEe8seaNwtLmNvAYvfpAmiMOk6AXt6Jn4slCkkKXHE= (Pdv.Testes/TestesUsuarioMaster e
/// src/test/pinCaixa.test.ts).
///
/// POR QUE UM ARQUIVO SEM BANCO. O instalador compila este arquivo também: é ele que confere a
/// senha do usuário master antes de apagar os dados do caixa. Duas cópias do mesmo cálculo
/// divergem no primeiro dia; um arquivo sem Dapper nem SQLite pode morar nos dois projetos.
/// </summary>
public static class HashDeSenha
{
    public const int Iteracoes = 100_000;
    public const int TamanhoHash = 32;
    public const int TamanhoSal = 16;

    /// <summary>Hash com sal novo (aleatório).</summary>
    public static (string Hash, string Salt) Gerar(string senha)
    {
        var salt = RandomNumberGenerator.GetBytes(TamanhoSal);
        return (Calcular(senha, salt), Convert.ToBase64String(salt));
    }

    /// <summary>Hash com o sal dado (exposto para o vetor bater com o painel).</summary>
    public static string Calcular(string senha, byte[] salt)
        => Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(senha), salt, Iteracoes, HashAlgorithmName.SHA256, TamanhoHash));

    /// <summary>Confere em tempo fixo (não vaza quantos dígitos estão certos). Qualquer coisa torta = não confere.</summary>
    public static bool Confere(string? senha, string? hashGuardado, string? saltGuardado)
    {
        if (string.IsNullOrEmpty(senha) || string.IsNullOrEmpty(hashGuardado) || string.IsNullOrEmpty(saltGuardado))
            return false;
        try
        {
            var salt = Convert.FromBase64String(saltGuardado);
            var esperado = Convert.FromBase64String(hashGuardado);
            var calculado = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(senha), salt, Iteracoes, HashAlgorithmName.SHA256, TamanhoHash);
            return CryptographicOperations.FixedTimeEquals(calculado, esperado);
        }
        catch { return false; }
    }

    /// <summary>Hash de 32 bytes e sal de 16, em base64. O que não passa aqui não é guardado.</summary>
    public static bool FormatoValido(string? hash, string? salt)
        => Bytes(hash) == TamanhoHash && Bytes(salt) == TamanhoSal;

    private static int Bytes(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return -1;
        try { return Convert.FromBase64String(base64).Length; }
        catch { return -1; }
    }
}
