using System.Text.RegularExpressions;

namespace Pdv.Nucleo;

/// <summary>
/// Lógica PURA do painel de chat do iFood: quantas mensagens não lidas há e
/// quando avisar. Fica separada da tela porque o WebView2/DOM não é testável —
/// a decisão, sim.
///
/// O número de não lidas é LIDO do próprio DOM do Gestor (um observador injetado
/// manda o texto para cá). Por isso a leitura é DEFENSIVA: o iFood muda a marcação
/// quando quer, e um texto que não bate não pode virar exceção nem número errado.
/// </summary>
public static class ChatContagem
{
    private static readonly Regex Digitos = new(@"\d+", RegexOptions.Compiled);

    /// <summary>
    /// Lê o número de não lidas do texto/aria-label do Gestor. Aceita as formas
    /// conhecidas ("1 mensagem", "9 mensagens", "3 mensagem(s) do Atendimento
    /// não lida(s)", badge "9+") e devolve 0 para ausência de número ou texto
    /// vazio/nulo. Nunca lança.
    /// </summary>
    public static int Ler(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return 0;
        var m = Digitos.Match(texto);
        if (!m.Success) return 0;
        // teto de segurança: o badge do iFood não passa de dezenas; um "número"
        // gigante quase sempre é id de pedido que vazou para o texto errado.
        return long.TryParse(m.Value, out var n) ? (int)Math.Clamp(n, 0, 9999) : 0;
    }

    /// <summary>
    /// Número de pedido válido para procurar a conversa (só dígitos, curto).
    /// Barra qualquer coisa que pudesse virar injeção de JS na busca.
    /// </summary>
    public static bool NumeroPedidoValido(string? numero)
        => !string.IsNullOrEmpty(numero) && numero.Length <= 12 && numero.All(char.IsDigit);
}

/// <summary>
/// Máquina de estado do "só avisa na SUBIDA". Guarda o último total conhecido e
/// diz, a cada leitura, se uma mensagem NOVA chegou (subiu) — sem repetir o aviso
/// enquanto o número não sobe de novo. A primeira leitura só estabelece a linha
/// de base: abrir o caixa com 3 não lidas não pode tocar sozinho.
/// </summary>
public sealed class ChatAviso
{
    private int _ultimo = -1;   // -1 = ainda sem leitura (linha de base)

    /// <summary>Último total conhecido (0 antes da primeira leitura).</summary>
    public int Atual => _ultimo < 0 ? 0 : _ultimo;

    public readonly record struct Resultado(int Total, bool Avisar, int Delta);

    /// <summary>
    /// Registra uma leitura. Devolve o total normalizado, se deve AVISAR
    /// (subiu, e não é a primeira leitura) e de quanto foi a variação.
    /// </summary>
    public Resultado Observar(int total)
    {
        if (total < 0) total = 0;
        var primeira = _ultimo < 0;
        var anterior = primeira ? 0 : _ultimo;
        _ultimo = total;
        var avisar = !primeira && total > anterior;
        return new Resultado(total, avisar, total - anterior);
    }

    /// <summary>Volta ao estado inicial (ex.: recarregou a página do chat).</summary>
    public void Zerar() => _ultimo = -1;
}
