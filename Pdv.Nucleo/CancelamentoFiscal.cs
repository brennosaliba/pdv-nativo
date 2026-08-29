using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Pdv.Nucleo;

/// <summary>
/// Resultado do evento 110111 (cancelamento de NFC-e).
///
/// `Indisponivel` é a distinção que evita o pior desfecho: agente mudo, timeout
/// ou socket cortado NÃO são recusa. A SEFAZ pode ter registrado o evento e a
/// resposta ter se perdido na volta — tratar isso como "recusado" e seguir para
/// o estorno produziria dinheiro devolvido com nota possivelmente cancelada, ou
/// (pior) nota cancelada e cartão ainda cobrado sem ninguém saber. Mesma
/// doutrina do ResultadoEmissao.ForaDoAr.
/// </summary>
public sealed record ResultadoCancelamento(
    bool Ok, int CStat, string? XMotivo, string? ProtocoloEvento, bool Indisponivel)
{
    /// <summary>Mensagem pronta para a tela — nunca vazia.</summary>
    public string Mensagem => Ok
        ? $"nota cancelada na SEFAZ ({CStat} {XMotivo})"
        : Indisponivel
            ? $"não consegui falar com o emissor fiscal: {XMotivo}"
            : $"a SEFAZ recusou o cancelamento: {CStat} {XMotivo}";
}

/// <summary>
/// Cancela a NFC-e pelo agente fiscal local (o mesmo processo que emite).
///
/// Por que HTTP direto e não um membro de <see cref="IEmissorFiscal"/>: quem sabe
/// cancelar é SÓ o agente — ele tem o certificado A1 da loja. O emissor da nuvem
/// não tem rota de cancelamento (só `nfce-emitir`), então pôr `Cancelar` na
/// interface seria uma promessa que metade das implementações não cumpre.
///
/// O PDV tem localmente tudo que o evento exige (chave e protocolo ficam em
/// `venda`), então não precisa buscar XML em lugar nenhum.
/// </summary>
public static class CancelamentoFiscal
{
    /// <summary>Mínimo que a SEFAZ exige na justificativa (xJust): 15 caracteres.</summary>
    public const int JustificativaMinima = 15;
    public const int JustificativaMaxima = 255;

    /// <summary>Teto da chamada. O agente fala com a SEFAZ, que é lenta em pico.</summary>
    public const int TempoMs = 40_000;

    /// <summary>
    /// PRAZO DO EVENTO 110111: 30 minutos contados da AUTORIZAÇÃO da nota (não da
    /// venda, não do pagamento). Passou, a NFC-e não morre mais nunca — o desfecho
    /// vira nota de devolução com o contador, que é outra conversa e outro papel.
    ///
    /// Por que isto AVISA em vez de PROIBIR: o relógio que vale é o da SEFAZ, e o
    /// relógio deste caixa não é o dela (já houve terminal de loja com meia hora de
    /// diferença). Uma trava local recusaria cancelamento que a SEFAZ ainda aceita,
    /// e o cStat 155 ("cancelamento fora de prazo homologado") existe justamente
    /// porque o corte não é tão seco quanto parece. Quem recusa é a SEFAZ; o PDV
    /// só tem que parar de PROMETER depois do prazo.
    /// </summary>
    public static readonly TimeSpan Prazo = TimeSpan.FromMinutes(30);

    /// <summary>
    /// O que sobra do prazo. NÃO satura em zero de propósito: depois de vencido a
    /// tela precisa dizer de quanto passou ("autorizada há 47 minutos"), senão o
    /// operador acha que faltou pouco e tenta de novo com o cliente esperando.
    /// </summary>
    public static TimeSpan RestanteDoPrazo(DateTime autorizadaEm, DateTime agora)
        => Prazo - (agora - autorizadaEm);

    /// <summary>
    /// cStat de SUCESSO do 110111: 135 (evento vinculado), 136 (registrado, não
    /// vinculado) e 155 (cancelamento fora do prazo, aceito). 573 é DUPLICIDADE:
    /// o evento já existia — a nota já está cancelada, então para o caixa isso é
    /// sucesso, não erro (o operador tentou de novo depois de uma queda).
    /// </summary>
    public static bool Sucesso(int cStat) => cStat is 135 or 136 or 155 or 573;

    /// <summary>Justificativa que a SEFAZ aceita (15..255, sem só espaço).</summary>
    public static bool JustificativaValida(string? j)
    {
        var t = (j ?? "").Trim();
        return t.Length >= JustificativaMinima && t.Length <= JustificativaMaxima;
    }

    public static async Task<ResultadoCancelamento> CancelarAsync(
        string agenteUrl, string chave, string? protocolo, string justificativa,
        CancellationToken ct = default)
    {
        // Barrar aqui é mais barato que descobrir pelo 400 do agente — e a
        // mensagem fica em português, não em erro de servidor.
        if ((chave ?? "").Trim().Length != 44)
            return new ResultadoCancelamento(false, 0, "chave da NFC-e inválida", null, false);
        if (string.IsNullOrWhiteSpace(protocolo))
            return new ResultadoCancelamento(false, 0,
                "esta nota não tem protocolo de autorização (emitida em contingência?) — cancele pelo ERP", null, false);
        if (!JustificativaValida(justificativa))
            return new ResultadoCancelamento(false, 0,
                $"a justificativa precisa de {JustificativaMinima} a {JustificativaMaxima} caracteres", null, false);

        var corpo = JsonSerializer.Serialize(new
        {
            chave = chave!.Trim(),
            xJust = justificativa.Trim(),
            nProt = protocolo!.Trim(),
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TempoMs);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                agenteUrl.TrimEnd('/') + "/nfce/cancelar")
            { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };
            using var resp = await Fiscal.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var texto = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            int cStat = 0;
            string? xMotivo = null, prot = null;
            try
            {
                using var doc = JsonDocument.Parse(texto);
                var r = doc.RootElement;
                cStat = Inteiro(r, "cStat") ?? 0;
                xMotivo = Texto(r, "xMotivo") ?? Texto(r, "error") ?? Texto(r, "erro");
                prot = Texto(r, "nProt") ?? Texto(r, "protocolo");
            }
            catch { xMotivo = texto.Length > 200 ? texto[..200] : texto; }

            if (Sucesso(cStat)) return new ResultadoCancelamento(true, cStat, xMotivo, prot, false);

            // HTTP não-2xx SEM cStat é o agente reclamando do pedido (400 sem
            // protocolo, 500 de certificado): é recusa DELE, não da SEFAZ, e
            // também não é indisponibilidade — a resposta chegou.
            return new ResultadoCancelamento(false, cStat,
                xMotivo ?? $"o emissor respondeu HTTP {(int)resp.StatusCode}", null, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // NÃO SEI se a SEFAZ registrou. Quem chama tem que parar aqui.
            return new ResultadoCancelamento(false, 0,
                $"sem resposta em {TempoMs / 1000}s", null, true);
        }
        catch (Exception ex)
        {
            return new ResultadoCancelamento(false, 0, ex.Message, null, true);
        }

        static string? Texto(JsonElement e, string k) =>
            e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            && v.GetString() is { Length: > 0 } s ? s : null;

        static int? Inteiro(JsonElement e, string k) =>
            e.TryGetProperty(k, out var v) ? v.ValueKind switch
            {
                JsonValueKind.Number => v.GetInt32(),
                JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
                _ => null,
            } : null;
    }
}
