using System.Globalization;
using System.Text.Json;

namespace Pdv.Nucleo;

/// <summary>
/// O que o PAINEL sabe do fiscal de uma loja (RPC <c>pdv_fiscal_da_loja</c>). Só metadados:
/// datas e ambiente. Certificado, senha e CSC nunca saem da EC2, e este registro não tem nem
/// onde guardá-los.
/// </summary>
/// <param name="CertValidoAte">Validade do certificado, quando o painel guardou (a cópia pode estar vazia).</param>
/// <param name="TpAmb">Ambiente do emissor da nuvem (1 produção, 2 homologação). Null = a loja não tem emissor na nuvem.</param>
public sealed record FiscalDaLoja(string Loja, string Cnpj, DateTime? CertValidoAte, DateTime? CertAtualizadoEm,
    DateTime? CscProdEm, DateTime? CscHomologEm, int? TpAmb, bool EmissorAtivo);

/// <summary>Uma linha para a tela. Nível: 0 ok, 1 aviso, 2 erro.</summary>
public sealed record LinhaNotaNuvem(int Nivel, string Texto);

/// <summary>Como foi a consulta ao painel.</summary>
public enum ConsultaFiscal { Respondeu, SemSessao, SemRede, NaoDisponivel }

/// <summary>
/// CERTIFICADO E CSC NUM LUGAR SÓ (14/09/2026, loja Castelo).
///
/// O passo 2 da Configuração pedia certificado e CSC, mas eles só servem ao emissor LOCAL (o
/// agente), que não estava instalado na Castelo. A nota daquele caixa sai pela nuvem (edge
/// nfce-emitir, EC2), e o certificado e o CSC dela são cadastrados no painel em Empresa &amp; Fiscal.
/// O dono cadastrou no caixa, e a nota falhou com "Sem CSC de prod para o CNPJ". Dado em dois
/// lugares, e o lugar errado à vista.
///
/// Regra: sem o emissor local instalado o caixa não pede certificado nem CSC, e mostra em uma
/// linha o estado do painel. Com o emissor local, o fluxo de sempre continua.
/// </summary>
public static class NotaPelaNuvem
{
    /// <summary>A RPC do painel (migration 20260915010000_pdv_fiscal_da_loja).</summary>
    public const string Rpc = "pdv_fiscal_da_loja";

    private const string Prefixo = "Nota pela nuvem: ";
    private const string NoPainel = "Cadastre no painel em Empresa & Fiscal.";

    /// <summary>O caixa pede certificado e CSC na tela? Só com o emissor local instalado e fora do modo recibo.</summary>
    public static bool PedirCertificadoNoCaixa(bool emissorLocalInstalado, bool modoRecibo) => !modoRecibo && emissorLocalInstalado;

    /// <summary>As linhas da RPC. Resposta vazia ou quebrada vira lista vazia.</summary>
    public static IReadOnlyList<FiscalDaLoja> Ler(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<FiscalDaLoja>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<FiscalDaLoja>();
            var linhas = new List<FiscalDaLoja>();
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                if (o.ValueKind != JsonValueKind.Object) continue;
                linhas.Add(new FiscalDaLoja(
                    Texto(o, "store") ?? "",
                    Documentos.SoDigitos(Texto(o, "cnpj") ?? ""),
                    Data(o, "cert_valido_ate"),
                    Instante(o, "cert_atualizado_em"),
                    Instante(o, "csc_prod_em"),
                    Instante(o, "csc_homolog_em"),
                    o.TryGetProperty("tp_amb", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var tp) ? tp : null,
                    o.TryGetProperty("emissor_ativo", out var a) && a.ValueKind == JsonValueKind.True));
            }
            return linhas;
        }
        catch (JsonException) { return Array.Empty<FiscalDaLoja>(); }
    }

    /// <summary>A linha deste caixa: pelo CNPJ; sem CNPJ, só quando a resposta tem uma loja (a do terminal).</summary>
    public static FiscalDaLoja? Escolher(IReadOnlyList<FiscalDaLoja> linhas, string? cnpjDoCaixa)
    {
        var cnpj = Documentos.SoDigitos(cnpjDoCaixa ?? "");
        if (cnpj.Length == 14) return linhas.FirstOrDefault(l => l.Cnpj == cnpj);
        return linhas.Count == 1 ? linhas[0] : null;
    }

    /// <summary>A linha que a tela mostra, na ordem em que as coisas impedem a nota.</summary>
    public static LinhaNotaNuvem Linha(FiscalDaLoja? f, ConsultaFiscal consulta, DateTime hoje)
    {
        switch (consulta)
        {
            case ConsultaFiscal.SemSessao:
                return new(1, Prefixo + "pareie o caixa no passo 5 para conferir o certificado e o CSC.");
            case ConsultaFiscal.SemRede:
                return new(1, Prefixo + "sem internet agora para conferir o certificado e o CSC.");
            case ConsultaFiscal.NaoDisponivel:
                return new(1, Prefixo + "o painel não respondeu. Confira o certificado e o CSC em Empresa & Fiscal.");
        }

        if (f is null) return new(2, Prefixo + "o CNPJ deste caixa não está no painel. Cadastre a loja em Empresa & Fiscal.");
        if (f.TpAmb is null) return new(2, Prefixo + "a loja ainda não tem emissor na nuvem. Chame o suporte.");
        if (!f.EmissorAtivo) return new(2, Prefixo + "o emissor da loja está desligado. Chame o suporte.");

        var dia = hoje.Date;
        if (f.CertValidoAte is { } venceu && venceu.Date < dia)
            return new(2, Prefixo + $"o certificado venceu em {DiaMesAno(venceu)}. Cadastre o novo no painel em Empresa & Fiscal.");

        var producao = f.TpAmb == 1;
        var nomeCsc = producao ? "CSC de produção" : "CSC de homologação";
        var csc = producao ? f.CscProdEm : f.CscHomologEm;
        if (csc is null) return new(2, Prefixo + $"falta o {nomeCsc}. " + NoPainel);

        if (f.CertValidoAte is { } vence && (vence.Date - dia).TotalDays <= 30)
            return new(1, Prefixo + $"o certificado vence em {DiaMesAno(vence)}. Cadastre o novo no painel em Empresa & Fiscal.");

        // O certificado só entra quando o painel sabe a validade: a cópia dos metadados estava
        // vazia nas duas lojas em 14/09/2026, e dizer "falta" por isso seria mentir.
        var partes = new List<string>();
        if (f.CertValidoAte is { } ok) partes.Add($"certificado ok até {DiaMesAno(ok)}");
        partes.Add($"{nomeCsc} cadastrado em {csc.Value.ToString("dd'/'MM", CultureInfo.InvariantCulture)}");
        return new(0, Prefixo + string.Join(", ", partes) + ".");
    }

    private static string DiaMesAno(DateTime d) => d.ToString("dd'/'MM'/'yyyy", CultureInfo.InvariantCulture);

    private static string? Texto(JsonElement o, string nome)
        => o.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? Data(JsonElement o, string nome)
        => Texto(o, nome) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : null;

    private static DateTime? Instante(JsonElement o, string nome)
        => Texto(o, nome) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.LocalDateTime : null;
}
