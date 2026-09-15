using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pdv.Nucleo;

/// <summary>Uma recusa do host traduzida: o que aconteceu e o que fazer.</summary>
/// <param name="Codigo">Só para a auditoria ("NA 0201/03"). Nunca vai para a tela.</param>
public sealed record RecusaDoHost(string Codigo, string Frase, string OQueFazer)
{
    /// <summary>As duas frases juntas, como a tela mostra.</summary>
    public string ParaTela => Frase + " " + OQueFazer;
}

/// <summary>
/// RECUSAS DO HOST EM PORTUGUÊS (14/09/2026, loja Castelo).
///
/// Na primeira venda da Castelo o host devolveu "[NA 0201] 03 ESTABELECIMENTO INVALIDO" e, no
/// Pix, "MODALIDADE DE PAGAMENTO INVALIDA". O operador leu isso cru, e nenhuma das duas frases
/// diz o que fazer. As duas são de CADASTRO: repetir a cobrança não resolve, trocar de cartão
/// também não. A tabela abaixo traduz as mais comuns, sempre com o que fazer; o texto original
/// continua em <c>DesfechoTef.Motivo</c> (tef_transacao.motivo) e na auditoria.
///
/// Só entram aqui recusas de cadastro e de configuração. "TRANSACAO NAO AUTORIZADA", "SALDO
/// INSUFICIENTE" e parecidas são do CARTÃO e seguem com a frase da rede, que é o que o cliente
/// precisa ouvir.
/// </summary>
public static class RecusasDoHost
{
    private static readonly Regex CodigoNa = new(@"\bNA\s+([0-9A-Z]{4})\b", RegexOptions.CultureInvariant);
    private static readonly Regex Estabelecimento03 = new(@"\b0201\]?\s+03\b", RegexOptions.CultureInvariant);

    /// <param name="mensagem">O PWINFO_RESULTMSG, do jeito que a biblioteca escreveu.</param>
    /// <param name="tipo">Tipo da cobrança, quando se sabe (Pix muda o conselho).</param>
    /// <param name="redeFixada">A cobrança foi com rede pré-selecionada: aí o automático é o primeiro conselho.</param>
    public static RecusaDoHost? Traduzir(string? mensagem, TipoTef? tipo = null, bool redeFixada = false)
    {
        var m = Normalizar(mensagem);
        if (m.Length == 0) return null;
        var codigo = CodigoNa.Match(m) is { Success: true } achado ? achado.Groups[1].Value : null;
        var pix = tipo == TipoTef.Pix;

        if (m.Contains("ESTABELECIMENTO INVALIDO", StringComparison.Ordinal) || Estabelecimento03.IsMatch(m))
            return new("NA 0201/03", "A adquirente não reconhece este estabelecimento.",
                "Peça à PayGo para ativar o cadastro do ponto de captura.");

        if (codigo == "A110" || m.Contains("TIPO PONTO DE CAPTURA INCORRETO", StringComparison.Ordinal))
            return new("NA A110", "O ponto de captura não é do tipo certo para este caixa.",
                "Peça à PayGo um ponto de captura do tipo automação.");

        if (codigo == "A116" || m.Contains("SERVICO NAO HABILITADO", StringComparison.Ordinal))
            return redeFixada
                ? new("NA A116", "A rede escolhida não está habilitada neste ponto de captura.", VolteAoAutomatico(tipo))
                : pix
                    ? new("NA A116", "O Pix não está habilitado neste ponto de captura.",
                        "Peça à PayGo para habilitar o Pix neste ponto de captura.")
                    : new("NA A116", "Este serviço não está habilitado neste ponto de captura.",
                        "Peça à PayGo para habilitar a rede neste ponto de captura.");

        if (m.Contains("MODALIDADE", StringComparison.Ordinal) && m.Contains("INVALIDA", StringComparison.Ordinal))
        {
            if (redeFixada)
                return new("MODALIDADE INVALIDA", tipo switch
                {
                    TipoTef.Pix => "A rede do Pix escolhida não vale para este terminal.",
                    null => "A rede escolhida não vale para este terminal.",
                    _ => "A rede do cartão escolhida não vale para este terminal.",
                }, VolteAoAutomatico(tipo));
            return tipo switch
            {
                TipoTef.Pix => new("MODALIDADE INVALIDA", "Este terminal não está liberado para Pix.",
                    "Peça à PayGo para liberar o Pix neste ponto de captura."),
                null => new("MODALIDADE INVALIDA", "Esta forma de pagamento não está liberada para este terminal.",
                    "Deixe a rede em automático na Configuração; se continuar, fale com a PayGo."),
                _ => new("MODALIDADE INVALIDA", "Esta forma de pagamento não está liberada para este terminal.",
                    "Peça à PayGo para liberar esta forma de pagamento neste ponto de captura."),
            };
        }

        return null;
    }

    /// <summary>A frase para a tela: a tradução quando existe, senão a mensagem como veio.</summary>
    public static string ParaTela(string? mensagem, TipoTef? tipo = null, bool redeFixada = false)
        => Traduzir(mensagem, tipo, redeFixada)?.ParaTela ?? (mensagem ?? "");

    private static string VolteAoAutomatico(TipoTef? tipo) => tipo switch
    {
        TipoTef.Pix => "Na Configuração, deixe a Rede do Pix em automático e tente de novo.",
        null => "Na Configuração, deixe a rede em automático e tente de novo.",
        _ => "Na Configuração, deixe a rede do cartão em automático e tente de novo.",
    };

    /// <summary>Maiúscula, sem acento e com espaço simples: a biblioteca escreve em ASCII e o log pode vir com acento.</summary>
    private static string Normalizar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return "";
        var sb = new StringBuilder(texto.Length);
        var espaco = false;
        foreach (var ch in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(ch)) { espaco = sb.Length > 0; continue; }
            if (espaco) { sb.Append(' '); espaco = false; }
            sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }
}
