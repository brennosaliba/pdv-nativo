using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

// ════════════════════════════════════════════════════════════════════════════
//  CODIGOS DE EMERGENCIA (1.0.56, 27/10/2026, decisao do dono; SQL 228 no ERP)
//
//  Para autorizar SEM INTERNET: o leitor de digital falhou e o codigo do
//  autenticador (2FA) nao tem como ser conferido, porque so a nuvem conhece o
//  segredo. O dono ou a gerente geral gera no painel ate 30 codigos de 8 digitos,
//  de uso unico, e guarda a lista. O caixa baixa no Atualizar (e a cada 15 min
//  com sinal) so o HASH de cada um (PBKDF2-HMAC-SHA256 com sal proprio), guarda
//  cifrado pelo DPAPI e confere sozinho.
//
//  REGRAS (a suite TestesEmergencia vigia cada uma):
//   · so quando o 2FA online FALHOU POR FALTA DE REDE (RespostaTotp.Definitiva =
//     false). Codigo errado, autenticador nao configurado ou recusa da nuvem NAO
//     abrem a porta: com rede, o caminho e o 2FA, sempre;
//   · so para entrar/abrir e fechar o turno pela reserva (turno_sem_digital),
//     sangria ate o teto da loja (padrao R$ 300) e fechar sem contar
//     (caixa_sem_contagem). NUNCA estorno, cancelamento, comanda, promocao,
//     Gestor, cadastro da digital ou qualquer outro tipo;
//   · uso unico local: o codigo e marcado usado NA HORA, na mesma transacao da
//     linha da fila e da auditoria; nem este caixa aceita de novo;
//   · a fila (outbox, tipo emergencia_usado) conta a nuvem quando a internet
//     volta; a nuvem tira o codigo do pacote de todos os caixas e alerta o dono
//     se o mesmo codigo foi usado em dois caixas;
//   · 5 codigos errados em 10 minutos travam a emergencia neste caixa por 10 min;
//   · o codigo digitado nunca vai para log, auditoria ou fila: so o id da linha.
//
//  Contrato: supabase/migrations/20261027180000_codigos_emergencia.sql (ERP)
//  Testes:   Pdv.Testes/TestesEmergencia.cs
// ════════════════════════════════════════════════════════════════════════════

/// <summary>Um codigo vivo no pacote: id, hash e sal em hex, iteracoes do PBKDF2.</summary>
public sealed record CodigoNoPacote(string Id, string? Lote, string Hash, string Sal, int Iteracoes, string? TerminalUuid);

/// <summary>O que a RPC pdv_emergencia_pacote devolve, ja conferido.</summary>
public sealed record PacoteEmergencia(string? Loja, string? TerminalUuid, long TetoSangriaCent, DateTime? GeradoEm,
    IReadOnlyList<CodigoNoPacote> Codigos);

/// <summary>Se a emergencia pode ser oferecida para este pedido, e por que nao.</summary>
public enum SituacaoEmergencia
{
    Pronta,
    /// <summary>O ato nao aceita codigo de emergencia (estorno, cancelamento, promocao...).</summary>
    TipoNaoVale,
    /// <summary>Nenhum codigo vivo guardado neste caixa.</summary>
    SemCodigos,
    /// <summary>Sangria acima do teto da loja (ou sem valor).</summary>
    AcimaDoTeto,
    /// <summary>Codigos errados demais nos ultimos 10 minutos.</summary>
    Bloqueada,
}

public sealed record ConferenciaEmergencia(bool Ok, string? CodigoId, SituacaoEmergencia Situacao = SituacaoEmergencia.Pronta);

/// <summary>Os codigos de emergencia deste caixa. Implementacao real: <see cref="GuardaEmergencia"/>.</summary>
public interface ICodigosEmergencia
{
    /// <summary>Pode oferecer a emergencia para este pedido agora? (nao mexe em nada)</summary>
    SituacaoEmergencia Situacao(PedidoAutorizacao pedido, DateTime agora);

    /// <summary>
    /// Confere o codigo digitado. Ok = marcado usado neste caixa, na fila para a nuvem e auditado,
    /// tudo numa transacao. Errado conta para a trava de 10 minutos.
    /// </summary>
    ConferenciaEmergencia Conferir(string codigo, PedidoAutorizacao pedido, DateTime agora);
}

public static class Emergencia
{
    public const int Digitos = 8;
    public const long TetoPadraoCent = 30000;
    public const int MaxFalhas = 5;
    public static readonly TimeSpan JanelaFalhas = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan IntervaloPacote = TimeSpan.FromMinutes(15);
    public const int IteracoesMin = 10000;
    public const int IteracoesMax = 1000000;

    public const string RpcPacote = "pdv_emergencia_pacote";
    public const string RpcUsados = "pdv_emergencia_usados";
    /// <summary>Tipo da linha da fila (outbox) que conta a nuvem o uso.</summary>
    public const string TipoNaFila = "emergencia_usado";
    /// <summary>Config: quando o caixa perguntou pelo pacote pela ultima vez (com ou sem sucesso).</summary>
    public const string ChaveConsultadoEm = "emergencia_consultado_em";

    /// <summary>Os UNICOS atos que aceitam codigo de emergencia (decisao do dono, 27/10/2026).</summary>
    public static readonly IReadOnlySet<string> TiposQueValem = new HashSet<string>(StringComparer.Ordinal)
    {
        Autorizacao.TipoTurnoSemDigital,   // entrar/abrir e fechar o turno pela reserva
        Autorizacao.TipoSangria,           // ate o teto
        Autorizacao.TipoCaixaSemContagem,  // fechar sem contar
    };

    public static bool TipoVale(string? tipo) => tipo is not null && TiposQueValem.Contains(tipo);

    /// <summary>Hora de perguntar pelo pacote de novo? (nunca perguntou, data ilegivel ou 15 min passados)</summary>
    public static bool PacoteVencido(string? consultadoEm, DateTime agora)
        => !DateTime.TryParse(consultadoEm, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var em)
           || agora - em >= IntervaloPacote || em > agora.AddMinutes(5);

    public static bool FormatoValido(string? codigo)
        => codigo is { Length: Digitos } && codigo.All(char.IsAsciiDigit);

    // textos de tela: curtos, uma linha, sem travessao
    public const string AvisoDigite = "Sem internet. Digite um código de emergência.";
    public const string AvisoInvalido = "Código de emergência inválido. Tente de novo.";
    public const string MotivoSemCodigos = "Sem internet e sem código de emergência neste caixa.";
    public const string MotivoBloqueada = "Muitos códigos de emergência errados. Aguarde 10 minutos.";
    public static string MotivoAcimaDoTeto(long tetoCent) => $"Sem internet. Sangria acima de {Reais(tetoCent)} só com internet.";
    public static string MotivoInvalidoVezes(int n) => $"Código de emergência inválido {n} vezes.";
    public const string AprovadoPor = "código de emergência";

    public static string Reais(long cent) => "R$ " + (cent / 100m).ToString("N2", CultureInfo.GetCultureInfo("pt-BR"));

    /// <summary>PBKDF2-HMAC-SHA256 (32 bytes) do codigo ASCII com o sal (hex), em hex minusculo. O mesmo do SQL 228.</summary>
    public static string Hash(string codigo, string salHex, int iteracoes)
        => Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(Encoding.ASCII.GetBytes(codigo), Convert.FromHexString(salHex),
            iteracoes, HashAlgorithmName.SHA256, 32)).ToLowerInvariant();

    private static bool Hex(string? s, int tamanho) => s is not null && s.Length == tamanho && s.All(Uri.IsHexDigit);

    /// <summary>
    /// O JSON da RPC em pacote. Resposta sem ok=true = null (o caixa fica com o que tem). Linha
    /// estranha (hash, sal ou iteracoes fora do contrato) fica de fora, nunca derruba o pacote.
    /// </summary>
    public static PacoteEmergencia? LerPacote(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                return null;
            string? S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var teto = r.TryGetProperty("teto_sangria_cent", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var tv) && tv >= 0
                ? tv : TetoPadraoCent;
            DateTime? gerado = DateTime.TryParse(S(r, "gerado_em"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var g) ? g : null;
            var lista = new List<CodigoNoPacote>();
            if (r.TryGetProperty("codigos", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var c in cs.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) continue;
                    var id = S(c, "id");
                    var hash = S(c, "hash")?.ToLowerInvariant();
                    var sal = S(c, "sal")?.ToLowerInvariant();
                    var it = c.TryGetProperty("iteracoes", out var iv) && iv.ValueKind == JsonValueKind.Number && iv.TryGetInt32(out var ii) ? ii : 0;
                    if (string.IsNullOrWhiteSpace(id) || !Hex(hash, 64) || !Hex(sal, 32) || it < IteracoesMin || it > IteracoesMax) continue;
                    lista.Add(new CodigoNoPacote(id!, S(c, "lote"), hash!, sal!, it, S(c, "terminal_uuid")));
                }
            return new PacoteEmergencia(S(r, "loja"), S(r, "terminal_uuid"), teto, gerado, lista);
        }
        catch { return null; }
    }

    /// <summary>Os codigos que ainda valem NESTE caixa: nao usados aqui e da loja ou deste terminal.</summary>
    public static IReadOnlyList<CodigoNoPacote> Vivos(PacoteEmergencia? pacote, IReadOnlySet<string> usados, string? terminal)
        => pacote is null ? Array.Empty<CodigoNoPacote>()
            : pacote.Codigos.Where(c => !usados.Contains(c.Id)
                                        && (string.IsNullOrWhiteSpace(c.TerminalUuid) || c.TerminalUuid == terminal)).ToList();

    /// <summary>A regra inteira de "pode oferecer?" (pura).</summary>
    public static SituacaoEmergencia Avaliar(PedidoAutorizacao pedido, PacoteEmergencia? pacote, IReadOnlySet<string> usados,
        string? terminal, int falhasRecentes)
    {
        if (!TipoVale(pedido.Tipo)) return SituacaoEmergencia.TipoNaoVale;
        if (falhasRecentes >= MaxFalhas) return SituacaoEmergencia.Bloqueada;
        if (Vivos(pacote, usados, terminal).Count == 0) return SituacaoEmergencia.SemCodigos;
        // sangria: o valor tem de vir (> 0) e caber no teto; sem valor = nao sai (nunca libera no escuro)
        if (pedido.Tipo == Autorizacao.TipoSangria && (pedido.ValorCent <= 0 || pedido.ValorCent > pacote!.TetoSangriaCent))
            return SituacaoEmergencia.AcimaDoTeto;
        return SituacaoEmergencia.Pronta;
    }

    /// <summary>O id do codigo vivo que bate com o digitado (comparacao em tempo constante), ou null.</summary>
    public static string? Casar(string codigo, IEnumerable<CodigoNoPacote> vivos)
    {
        if (!FormatoValido(codigo)) return null;
        string? achado = null;
        foreach (var c in vivos)
        {
            var calc = Convert.FromHexString(Hash(codigo, c.Sal, c.Iteracoes));
            if (CryptographicOperations.FixedTimeEquals(calc, Convert.FromHexString(c.Hash)) && achado is null) achado = c.Id;
        }
        return achado;
    }

    /// <summary>"entrada" / "fechamento" / "sangria" / "fechar sem contar", para o painel dizer para que foi.</summary>
    public static string Finalidade(PedidoAutorizacao p) => p.Finalidade ?? p.Tipo switch
    {
        Autorizacao.TipoSangria => "sangria",
        Autorizacao.TipoCaixaSemContagem => "fechar sem contar",
        _ => p.Tipo,
    };

    /// <summary>A linha que a fila manda a nuvem (um uso). Nunca o codigo.</summary>
    public static string UsoJson(string codigoId, PedidoAutorizacao p, DateTime agora)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = codigoId,
            ["usado_em"] = new DateTimeOffset(agora).ToString("o", CultureInfo.InvariantCulture),
            ["usado_para"] = p.Tipo,
            ["referencia"] = p.Referencia,
            ["cpf_hash"] = p.CpfHash,
            ["valor_cent"] = p.ValorCent > 0 ? p.ValorCent : null,
            ["operador"] = p.Operador,
            ["finalidade"] = Finalidade(p),
        });

    /// <summary>
    /// O handler da fila: manda o uso guardado na linha (payload) para pdv_emergencia_usados. Reenviar
    /// e seguro (a nuvem deduplica por codigo + terminal + referencia). 404 PGRST202 = a nuvem ainda
    /// nao tem o SQL 228: espera. Uso e REGISTRO: nunca sai da fila por idade.
    /// </summary>
    public static async Task<(bool? Ok, string? Erro)> ResolverNaFilaAsync(string payload, string? terminalUuid,
        Func<string, string, Task<(int Status, string? Corpo)>> rpc)
    {
        JsonElement uso;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (false, "uso de emergencia ilegivel na fila");
            uso = doc.RootElement.Clone();
        }
        catch { return (false, "uso de emergencia ilegivel na fila"); }

        var corpo = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["_terminal_uuid"] = terminalUuid,
            ["_usos"] = new[] { uso },
        });
        var (status, resposta) = await rpc(RpcUsados, corpo).ConfigureAwait(false);
        if (Drenagem.RpcAusente(status, resposta))
            return (null, $"a nuvem ainda não tem {RpcUsados} (rodar o SQL 228)");
        if (status is >= 200 and < 300)
        {
            try
            {
                using var doc = JsonDocument.Parse(resposta ?? "");
                var r = doc.RootElement;
                if (r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return (true, null);
                var motivo = r.TryGetProperty("motivo", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : "recusado";
                // sem sessao/terminal: pode voltar (pareamento, login); o resto e recusa do formato
                return motivo is "sem_sessao" or "sem_terminal" ? (null, "a nuvem recusou: " + motivo) : (false, "a nuvem recusou: " + motivo);
            }
            catch { return (false, "resposta ilegivel da nuvem"); }
        }
        return Drenagem.DesfechoDeStatus(status, resposta);
    }
}

/// <summary>
/// A guarda local: o pacote cifrado pelo DPAPI (tabela emergencia_pacote, uma linha), os usos deste
/// caixa (emergencia_uso, a chave e o id do codigo: usar duas vezes e impossivel) e as tentativas
/// (emergencia_tentativa, a trava de 10 minutos).
/// </summary>
public sealed class GuardaEmergencia : ICodigosEmergencia
{
    private static readonly byte[] Entropia = Encoding.UTF8.GetBytes("PdvNativo.emergencia.v1");
    private readonly Func<SqliteConnection> _abrir;
    private readonly Func<string?> _terminal;

    public GuardaEmergencia(Func<SqliteConnection> abrir, Func<string?> terminalUuid)
    {
        _abrir = abrir;
        _terminal = terminalUuid;
    }

    public static string Cifrar(string texto)
    {
        var bytes = Encoding.UTF8.GetBytes(texto);
        if (OperatingSystem.IsWindows())
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(bytes, Entropia, DataProtectionScope.LocalMachine));
        return "claro:" + Convert.ToBase64String(bytes);
    }

    public static string? Decifrar(string? guardado)
    {
        if (string.IsNullOrEmpty(guardado)) return null;
        try
        {
            if (guardado.StartsWith("dpapi:", StringComparison.Ordinal))
                return OperatingSystem.IsWindows()
                    ? Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(guardado[6..]), Entropia, DataProtectionScope.LocalMachine))
                    : null;
            if (guardado.StartsWith("claro:", StringComparison.Ordinal))
                return Encoding.UTF8.GetString(Convert.FromBase64String(guardado[6..]));
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Guarda o pacote que a nuvem mandou (substitui o anterior inteiro: o que a nuvem anulou ou outro
    /// caixa usou some daqui). Resposta que nao e pacote = null e o guardado continua valendo.
    /// Devolve o que mudou ("" = nada).
    /// </summary>
    public static string? AplicarPacote(SqliteConnection cx, string? json, DateTime agora)
    {
        var p = Emergencia.LerPacote(json);
        if (p is null) return null;
        var normal = JsonSerializer.Serialize(p);
        var antes = Decifrar(cx.ExecuteScalar<string?>("SELECT cifrado FROM emergencia_pacote WHERE id = 1"));
        string? antesNormal = null;
        if (antes is not null)
        {
            try { antesNormal = JsonSerializer.Serialize(JsonSerializer.Deserialize<PacoteEmergencia>(antes) with { GeradoEm = p.GeradoEm }); }
            catch { antesNormal = null; }
        }
        cx.Execute("""
            INSERT INTO emergencia_pacote (id, cifrado, quantidade, recebido_em) VALUES (1, @C, @N, @Em)
            ON CONFLICT(id) DO UPDATE SET cifrado = @C, quantidade = @N, recebido_em = @Em
            """, new { C = Cifrar(normal), N = p.Codigos.Count, Em = agora.ToString("o") });
        return antesNormal == normal ? "" : $"{p.Codigos.Count} código(s) de emergência";
    }

    /// <summary>O pacote guardado (null = nunca baixou, ou o DPAPI deste Windows nao abre).</summary>
    public static PacoteEmergencia? PacoteGuardado(SqliteConnection cx)
    {
        var json = Decifrar(cx.ExecuteScalar<string?>("SELECT cifrado FROM emergencia_pacote WHERE id = 1"));
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<PacoteEmergencia>(json); }
        catch { return null; }
    }

    private static HashSet<string> Usados(SqliteConnection cx, SqliteTransaction? tx = null)
        => cx.Query<string>("SELECT codigo_id FROM emergencia_uso", transaction: tx).ToHashSet(StringComparer.Ordinal);

    private static int FalhasRecentes(SqliteConnection cx, DateTime agora, SqliteTransaction? tx = null)
        // a janela olha 10 min para tras; erro gravado "no futuro" (relogio que voltou) conta ate 1 dia
        // a frente, para atrasar o relogio nao zerar a trava
        => cx.ExecuteScalar<int>("SELECT COUNT(*) FROM emergencia_tentativa WHERE ok = 0 AND em >= @De AND em <= @Ate",
            new { De = (agora - Emergencia.JanelaFalhas).ToString("o"), Ate = agora.AddDays(1).ToString("o") }, tx);

    public SituacaoEmergencia Situacao(PedidoAutorizacao pedido, DateTime agora)
    {
        using var cx = _abrir();
        return Emergencia.Avaliar(pedido, PacoteGuardado(cx), Usados(cx), _terminal(), FalhasRecentes(cx, agora));
    }

    /// <summary>Teto que vale neste caixa (o do pacote; sem pacote, o padrao). So para o texto da recusa.</summary>
    public long Teto()
    {
        using var cx = _abrir();
        return PacoteGuardado(cx)?.TetoSangriaCent ?? Emergencia.TetoPadraoCent;
    }

    public ConferenciaEmergencia Conferir(string codigo, PedidoAutorizacao pedido, DateTime agora)
    {
        using var cx = _abrir();
        var terminal = _terminal();
        // a parte cara (PBKDF2) fora da transacao: o banco do caixa nao fica travado durante a conta
        var pacote = PacoteGuardado(cx);
        var sit = Emergencia.Avaliar(pedido, pacote, Usados(cx), terminal, FalhasRecentes(cx, agora));
        if (sit != SituacaoEmergencia.Pronta) return new ConferenciaEmergencia(false, null, sit);
        var id = Emergencia.Casar(codigo, Emergencia.Vivos(pacote, Usados(cx), terminal));

        using var tx = cx.BeginTransaction();
        if (id is null || cx.ExecuteScalar<int>("SELECT COUNT(*) FROM emergencia_uso WHERE codigo_id = @Id", new { Id = id }, tx) > 0)
        {
            cx.Execute("INSERT INTO emergencia_tentativa (em, ok) VALUES (@Em, 0)", new { Em = agora.ToString("o") }, tx);
            Caixa.Auditar(cx, tx, "emergencia_recusada", null, null,
                $"{pedido.Tipo} · {pedido.Referencia} · código {Autorizacao.Mascarar(codigo)} não confere");
            tx.Commit();
            return new ConferenciaEmergencia(false, null);
        }
        var curto = id.Length > 8 ? id[..8] : id;
        cx.Execute("""
            INSERT INTO emergencia_uso (codigo_id, usado_em, usado_para, referencia, cpf_hash, valor_cent, operador, finalidade)
            VALUES (@Id, @Em, @Para, @Ref, @Cpf, @V, @Op, @Fin)
            """, new { Id = id, Em = agora.ToString("o"), Para = pedido.Tipo, Ref = pedido.Referencia, Cpf = pedido.CpfHash,
                       V = pedido.ValorCent > 0 ? pedido.ValorCent : (long?)null, Op = pedido.Operador, Fin = Emergencia.Finalidade(pedido) }, tx);
        cx.Execute("INSERT INTO emergencia_tentativa (em, ok) VALUES (@Em, 1)", new { Em = agora.ToString("o") }, tx);
        cx.Execute("INSERT INTO outbox (tipo, ref_id, client_key, payload, criado_em) VALUES (@T, @R, @K, @P, @Em)",
            new { T = Emergencia.TipoNaFila, R = id, K = "emerg:" + id, P = Emergencia.UsoJson(id, pedido, agora), Em = agora.ToString("o") }, tx);
        Caixa.Auditar(cx, tx, "emergencia_usada", null, "emerg:" + curto,
            $"{pedido.Tipo} · {Emergencia.Finalidade(pedido)} · {pedido.Referencia}"
            + (pedido.ValorCent > 0 ? $" · {Emergencia.Reais(pedido.ValorCent)}" : "")
            + (pedido.Operador is null ? "" : $" · {pedido.Operador}"));
        tx.Commit();
        return new ConferenciaEmergencia(true, id);
    }
}
