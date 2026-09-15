using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>
/// USUÁRIO MASTER DA REDE (15/09/2026; decisão do dono em 14/09).
///
/// O QUE ACONTECEU NO CASTELO. A senha de administrador do caixa era uma CÓPIA da senha de uma
/// pessoa. O assistente grava a senha do "administrador da loja" em dois lugares: no operador dele
/// (que, pelo CPF, adota o funcionário que o painel já tem, ver Operadores.SalvarAdministrador) e
/// na linha `_admin_`. A Configuração só comparava com essa cópia. Quem foi cadastrado ali abre o
/// admin com a própria senha para sempre, porque nada troca a cópia depois: a senha por loja do
/// painel (pdv_loja_config.admin_pin_hash) nunca foi definida para o Castelo (em produção só a
/// Savassi tem linha, e sem senha). Foi assim que a senha do Lucas, funcionário do caixa, abriu a
/// Configuração.
///
/// A REGRA:
///  · o master é da EMPRESA (uma senha para a rede toda), criado no painel e baixado no Atualizar
///    pela RPC <see cref="Rpc"/>; fica guardado no SQLite e vale sem internet;
///  · existindo master guardado, toda ação de admin confere SÓ ele: nem `_admin_`, nem operador;
///  · sem master (instalação nova, painel sem master), vale a senha criada na instalação
///    (`_admin_`) até o primeiro Atualizar que trouxer o master;
///  · operador, gerente ou não, nunca é consultado aqui;
///  · resposta vazia ou torta do painel NÃO apaga o master guardado: voltar para a senha da
///    instalação reabriria a porta que o master fecha.
/// </summary>
public static class UsuarioMaster
{
    public const string Rpc = "pdv_master_caixa";

    public const string ChaveNome = "master_nome";
    public const string ChaveHash = "master_hash";
    public const string ChaveSalt = "master_salt";
    public const string ChaveEm = "master_em";

    /// <summary>A linha embaixo da senha do administrador, no assistente.</summary>
    public const string AvisoInstalador =
        "Esta senha abre a Configuração só até o primeiro Atualizar trazer o usuário master da rede, criado no painel.";

    public sealed record Master(string Nome, string Hash, string Salt, DateTime Em);

    public enum Via { Master, Instalador }

    /// <summary>O resultado de uma senha digitada numa ação de admin.</summary>
    public sealed record Conferencia(bool Liberado, Via Via, string? Nome);

    /// <summary>
    /// O JSON da RPC (array com zero ou uma linha; aceita objeto solto) no master. Sem linha, nome
    /// vazio, hash ou sal fora do formato, data ilegível: null. Nunca exceção.
    /// </summary>
    public static Master? Ler(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var raiz = doc.RootElement;
            JsonElement o;
            if (raiz.ValueKind == JsonValueKind.Array)
            {
                if (raiz.GetArrayLength() != 1) return null;
                o = raiz[0];
            }
            else o = raiz;
            if (o.ValueKind != JsonValueKind.Object) return null;

            string? S(string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var nome = S("nome")?.Trim();
            var hash = S("senha_hash");
            var salt = S("senha_salt");
            if (string.IsNullOrWhiteSpace(nome) || !HashDeSenha.FormatoValido(hash, salt)) return null;
            if (!DateTime.TryParse(S("atualizado_em"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var em))
                return null;
            return new Master(nome, hash!, salt!, em.ToUniversalTime());
        }
        catch { return null; }
    }

    /// <summary>O master guardado neste caixa (null = ainda não chegou nenhum).</summary>
    public static Master? Guardado(SqliteConnection cx)
    {
        var hash = Vendas.Config(cx, ChaveHash);
        if (string.IsNullOrWhiteSpace(hash)) return null;
        var em = DateTime.TryParse(Vendas.Config(cx, ChaveEm), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : DateTime.MinValue;
        return new Master(Vendas.Config(cx, ChaveNome) ?? "", hash, Vendas.Config(cx, ChaveSalt) ?? "", em);
    }

    /// <summary>
    /// Guarda o master que veio do painel. Devolve o que mudou ("" = nada). Null do painel (sem
    /// master, sem empresa, JSON torto) mantém o que já está guardado. Os quatro campos entram numa
    /// transação: hash de um e sal de outro trancaria a Configuração de todo mundo.
    /// </summary>
    public static string Aplicar(SqliteConnection cx, Master? doPainel)
    {
        if (doPainel is null) return "";
        if (Guardado(cx) == doPainel) return "";

        var agora = DateTime.Now.ToString("o");
        using var tx = cx.BeginTransaction();
        foreach (var (chave, valor) in new[]
                 {
                     (ChaveNome, doPainel.Nome), (ChaveHash, doPainel.Hash), (ChaveSalt, doPainel.Salt),
                     (ChaveEm, doPainel.Em.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)),
                 })
            cx.Execute("""
                INSERT INTO config (chave, valor, atualizado) VALUES (@C,@V,@Em)
                ON CONFLICT(chave) DO UPDATE SET valor = @V, atualizado = @Em
                """, new { C = chave, V = valor, Em = agora }, tx);
        // nunca o hash na auditoria: só quem e quando
        Caixa.Auditar(cx, tx, "master_do_painel", null, null,
            $"usuário master {doPainel.Nome}, trocado no painel em {doPainel.Em.ToLocalTime():dd/MM HH:mm}");
        tx.Commit();
        return "usuário master";
    }

    /// <summary>
    /// A senha digitada numa ação de admin (Configuração, sair para o Windows). Com master guardado,
    /// só ele. Sem master, só a senha da instalação (`_admin_`). Operador nunca.
    /// </summary>
    public static Conferencia Conferir(SqliteConnection cx, string? senha)
    {
        var digitada = (senha ?? "").Trim();
        var hash = Vendas.Config(cx, ChaveHash);
        if (!string.IsNullOrWhiteSpace(hash))
            return new Conferencia(
                HashDeSenha.Confere(digitada, hash, Vendas.Config(cx, ChaveSalt)),
                Via.Master, Vendas.Config(cx, ChaveNome));

        var instalacao = cx.QueryFirstOrDefault("SELECT pin_hash, pin_salt FROM operador WHERE id = '_admin_'");
        var ok = instalacao is not null
                 && HashDeSenha.Confere(digitada, (string?)instalacao.pin_hash, (string?)instalacao.pin_salt);
        return new Conferencia(ok, Via.Instalador, null);
    }

    /// <summary>O rótulo da caixa de senha.</summary>
    public static string Rotulo(SqliteConnection cx)
        => Guardado(cx) is not null ? "Senha do usuário master" : "Senha de administrador";

    /// <summary>A frase quando a senha não confere.</summary>
    public static string NaoConfere(SqliteConnection cx)
        => Guardado(cx) is not null ? "A senha do usuário master não confere." : "A senha de administrador não confere.";

    /// <summary>Quem liberou, para a auditoria (sem senha, sem hash).</summary>
    public static string Detalhe(Conferencia c)
        => c.Via == Via.Master ? $"usuário master {c.Nome}" : "senha da instalação (sem usuário master ainda)";

    /// <summary>Uma linha na Configuração: o caixa diz sozinho qual senha vale nele.</summary>
    public static string Estado(SqliteConnection cx)
        => Guardado(cx) is { } m
            ? $"Usuário master da rede: {m.Nome}, trocado no painel em {m.Em.ToLocalTime():dd/MM/yyyy}. Só a senha dele abre a Configuração."
            : "Ainda sem usuário master da rede: vale a senha criada na instalação até o Atualizar trazer o master do painel.";
}
