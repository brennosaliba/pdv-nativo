using System.Text;
using System.Text.Json;

namespace Pdv.Nucleo;

/// <summary>
/// RESPOSTAS PRONTAS DO CHAT DO IFOOD (11/09/2026, pedido do dono: "no frame vazio ao
/// lado esquerdo do chat, uns 5 popups com mensagem pré-pronta; o atendente clica,
/// copia automático e só cola no chat; por exemplo pedido revirado").
///
/// O texto mora numa config do caixa (<see cref="Chave"/>), editável na Configuração,
/// num formato que uma pessoa escreve sem ajuda: blocos separados por linha em branco,
/// a PRIMEIRA linha é o título do cartão e o resto é a mensagem. Sem a config, valem
/// as respostas padrão daqui. A tela do chat injeta a lista na página (JSON) e o
/// cartão, ao toque, copia o texto e tenta colar na caixa de mensagem.
/// </summary>
public static class RespostasProntas
{
    public const string Chave = "chat_respostas";
    public const int Maximo = 8;
    public const int MaxTitulo = 40;
    public const int MaxTexto = 600;

    public sealed record Resposta(string Titulo, string Texto);

    /// <summary>As cinco de fábrica. A loja troca na Configuração.</summary>
    public static IReadOnlyList<Resposta> Padrao { get; } = new[]
    {
        // Sem promessa (reembolso, refazer) e sem afirmar fato que o atendente não sabe
        // ("já saiu"): o cartão abre a conversa; quem decide é o gerente.
        new Resposta("Pedido revirado",
            "Olá! Sentimos muito pelo pedido ter chegado revirado. Não é assim que a gente gosta de entregar. " +
            "Pode nos mandar uma foto? Vamos resolver por aqui agora mesmo."),
        new Resposta("Atraso na entrega",
            "Olá! Pedimos desculpas pela demora, o movimento está acima do normal agora. " +
            "Estamos cuidando do seu pedido e ele chega o quanto antes."),
        new Resposta("Item faltando",
            "Olá! Pedimos desculpas, faltou um item no seu pedido. Pode nos dizer qual foi? Já vamos resolver."),
        new Resposta("Pedido saiu",
            "Olá! Seu pedido saiu da loja em perfeito estado e já está a caminho. Bom apetite!"),
        new Resposta("Agradecimento",
            "Muito obrigado pelo seu pedido conosco! Daqui a pouquinho ele chega. Qualquer dúvida, é só chamar."),
    };

    /// <summary>
    /// Lê o texto da config. Blocos separados por uma ou mais linhas em branco; a
    /// primeira linha de cada bloco é o título; bloco de uma linha só vira título e
    /// texto iguais. Vazio ou só espaço = padrão. Nunca lança.
    /// </summary>
    public static IReadOnlyList<Resposta> Ler(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return Padrao;
        var lista = new List<Resposta>();
        var normal = texto.Replace("\r\n", "\n").Replace('\r', '\n');
        // linha "em branco" com espaço ou tab (comum ao colar) separa do mesmo jeito
        foreach (var bloco in System.Text.RegularExpressions.Regex.Split(normal, @"\n(?:[ \t]*\n)+"))
        {
            var linhas = bloco.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (linhas.Count == 0) continue;
            // A primeira linha é título quando PARECE título: curta e sem ponto final.
            // "Olá! Seu pedido saiu.\nBom apetite!" é a mensagem inteira, não título e
            // resto: senão o cliente receberia só a metade.
            var primeira = linhas[0].TrimEnd(':');
            var pareceTitulo = linhas.Count > 1 && primeira.Length <= MaxTitulo
                               && !System.Text.RegularExpressions.Regex.IsMatch(primeira, @"[.!?…]$");
            var titulo = Corta(primeira, MaxTitulo);
            var corpo = string.Join("\n", pareceTitulo ? linhas.Skip(1) : linhas);
            lista.Add(new Resposta(titulo, Corta(corpo, MaxTexto)));
            if (lista.Count == Maximo) break;
        }
        return lista.Count == 0 ? Padrao : lista;
    }

    /// <summary>O caminho de volta: a lista no formato que a Configuração mostra para editar.</summary>
    public static string Escrever(IEnumerable<Resposta> respostas)
    {
        var sb = new StringBuilder();
        foreach (var r in respostas)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            // título que já é o começo do texto (bloco sem linha de título) não se repete
            if (r.Texto == r.Titulo || r.Texto.StartsWith(r.Titulo, StringComparison.Ordinal)) sb.Append(r.Texto);
            else sb.Append(r.Titulo).Append('\n').Append(r.Texto);
        }
        return sb.ToString();
    }

    /// <summary>JSON seguro para entrar dentro de um script na página (só título e texto).</summary>
    public static string Json(IEnumerable<Resposta> respostas)
        => JsonSerializer.Serialize(respostas.Select(r => new { titulo = r.Titulo, texto = r.Texto }),
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static string Corta(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd();
}
