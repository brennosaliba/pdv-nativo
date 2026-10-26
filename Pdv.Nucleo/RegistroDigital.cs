using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>
/// O QUE O ERP FICA SABENDO DA DIGITAL (1.0.52, SQL 224): que o CPF (pelo hash) tem digital
/// neste terminal, quem cadastrou e quando; e quando ela foi apagada. Nunca a digital, nunca
/// o CPF em claro. RPC pdv_biometria_registrar, chamada com a sessao do terminal (o servidor
/// descobre o terminal e a loja pelo auth.uid()).
///
/// Regras puras (provadas na suite): o corpo de cada pendencia, a leitura da resposta e a
/// marca de "o ERP ja sabe".
/// </summary>
public static class RegistroDigital
{
    public const string Rpc = "pdv_biometria_registrar";

    public enum Desfecho { Aceito, Recusado, SemRpc, SemResposta }

    /// <summary>Uma pendencia: o corpo pronto para a RPC e a chave para marcar depois.</summary>
    public sealed record Pendencia(string Chave, string Acao, string Json);

    /// <summary>Os cadastros e as remocoes que a nuvem ainda nao sabe, na ordem em que aconteceram.</summary>
    public static IReadOnlyList<Pendencia> Pendentes(SqliteConnection cx)
    {
        var saida = new List<Pendencia>();
        foreach (var d in Digitais.PendentesDeRegistro(cx))
            saida.Add(new Pendencia(d.Chave, "cadastro", Corpo("cadastro", d.CpfHash, d.OperadorId, d.Nome, d.CadastradaPor, d.CadastradaEm)));
        foreach (var r in Digitais.RemocoesPendentes(cx))
            saida.Add(new Pendencia(r.Chave, "remocao", Corpo("remocao", r.CpfHash, null, r.Nome, r.Por, r.RemovidaEm)));
        return saida;
    }

    /// <summary>Os nomes de campo sao CONTRATO com a RPC (parametros com underscore).</summary>
    public static string Corpo(string acao, string cpfHash, string? operadorId, string nome, string? por, DateTime quando)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["_acao"] = acao,
            ["_cpf_hash"] = cpfHash,
            ["_funcionario_id"] = operadorId is not null && Guid.TryParse(operadorId, out _) ? operadorId : null,
            ["_nome"] = nome,
            ["_por"] = por,
            ["_quando"] = quando.ToUniversalTime().ToString("o"),
        });

    /// <summary>
    /// Status -1/0 = nao chegou (fica pendente); 404 = a RPC nao existe (SQL 224 ainda nao rodou);
    /// 2xx com {ok:true} = aceito; qualquer outro 2xx/4xx = recusado (fica pendente, sem laco:
    /// o proximo Atualizar tenta de novo).
    /// </summary>
    public static Desfecho Ler(int status, string? corpo)
    {
        if (status <= 0) return Desfecho.SemResposta;
        if (status == 404) return Desfecho.SemRpc;
        if (status is < 200 or >= 300) return Desfecho.Recusado;
        try
        {
            using var doc = JsonDocument.Parse(corpo ?? "");
            var r = doc.RootElement;
            return r.ValueKind == JsonValueKind.Object && r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                ? Desfecho.Aceito : Desfecho.Recusado;
        }
        catch { return Desfecho.Recusado; }
    }

    public static void Marcar(SqliteConnection cx, Pendencia p, DateTime? agora = null)
    {
        if (p.Acao == "cadastro") Digitais.MarcarRegistrada(cx, p.Chave, agora);
        else Digitais.MarcarRemocaoRegistrada(cx, p.Chave, agora);
    }
}
