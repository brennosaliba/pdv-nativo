namespace Pdv.Nucleo;

/// <summary>
/// O LEITOR USB DA ETIQUETA (05/10/2026). O leitor é um TECLADO: ao bipar o QR ele
/// "digita" <c>ADKDS:&lt;id&gt;</c> e aperta Enter, tudo em poucos milissegundos.
/// Esta classe separa essa rajada da digitação de gente, sem WPF, para a suíte provar:
///
///   - só vale o que começa com o prefixo <see cref="EtiquetaKds.PrefixoQr"/>;
///   - entre uma tecla e a seguinte no máximo <see cref="IntervaloMaxMs"/> (100 ms):
///     gente digitando é mais lenta, e a sequência lenta é descartada;
///   - termina em Enter; sem Enter não acontece nada;
///   - com o foco num CAMPO DE TEXTO nada é capturado: o operador digitando o CPF ou
///     procurando um pedido nunca tem tecla roubada.
///
/// O teclado do Windows da loja é ABNT2 e o leitor costuma vir de fábrica em inglês: o
/// ":" do inglês sai como "Ç" no ABNT2 (é a mesma tecla). Aceitar "Ç", ";" e ":" no
/// separador evita que a etiqueta "não seja reconhecida" por causa do idioma do leitor.
/// Letras são comparadas sem caixa pelo mesmo motivo (Caps Lock ligado no leitor).
///
/// Quem usa (a janela) chama <see cref="Caractere"/> no PreviewTextInput e
/// <see cref="Enter"/> no PreviewKeyDown do Enter. O retorno de <see cref="Caractere"/>
/// diz se a tecla deve ser ENGOLIDA (é parte de uma etiqueta em curso): fora de campo de
/// texto essas teclas não têm destino nenhum, e engolir o Enter da etiqueta é o que impede
/// o leitor de "apertar" o botão que estiver com o foco.
/// </summary>
public sealed class LeitorKds
{
    public const int IntervaloMaxPadraoMs = 100;

    /// <summary>Teto do id depois do prefixo. Um UUID tem 36; folga para outros formatos.</summary>
    public const int TamanhoMaxId = 80;

    public int IntervaloMaxMs { get; }

    private readonly System.Text.StringBuilder _buf = new();
    private long _ultimoMs;

    public LeitorKds(int intervaloMaxMs = IntervaloMaxPadraoMs) => IntervaloMaxMs = intervaloMaxMs;

    /// <summary>O que já foi juntado (para teste e diagnóstico).</summary>
    public string Juntado => _buf.ToString();

    /// <summary>Esquece a rajada em curso.</summary>
    public void Zerar() => _buf.Clear();

    /// <summary>
    /// Uma tecla de texto chegou em <paramref name="ms"/> (relógio monotônico, em ms).
    /// Devolve true quando a tecla é parte de uma etiqueta em curso e deve ser engolida.
    /// </summary>
    public bool Caractere(string? texto, long ms, bool emCampoDeTexto)
    {
        if (emCampoDeTexto || string.IsNullOrEmpty(texto)) { Zerar(); return false; }
        if (texto is "\r" or "\n") return false;   // Enter vem por Enter()

        // Pausa grande desde a última tecla: o que estava junto era gente, recomeça daqui.
        if (_buf.Length > 0 && ms - _ultimoMs > IntervaloMaxMs) Zerar();
        _ultimoMs = ms;

        _buf.Append(texto);
        if (Valido(_buf.ToString())) return true;

        // Não casa mais. Talvez ESTA tecla seja o começo de uma etiqueta ("xA" -> "A").
        Zerar();
        _buf.Append(texto);
        if (Valido(_buf.ToString())) return true;
        Zerar();
        return false;
    }

    /// <summary>
    /// Enter chegou. Devolve o id da etiqueta quando a rajada é uma etiqueta completa e
    /// rápida (quem chama engole o Enter e processa o bipe); senão null e o Enter segue
    /// seu caminho normal.
    /// </summary>
    public string? Enter(long ms, bool emCampoDeTexto)
    {
        var juntado = _buf.ToString();
        var rapido = _buf.Length > 0 && ms - _ultimoMs <= IntervaloMaxMs;
        Zerar();
        if (emCampoDeTexto || !rapido) return null;
        return Id(juntado);
    }

    /// <summary>
    /// O id de um texto completo de etiqueta ("ADKDS:abc" -> "abc"), ou null. Pura e
    /// estática para quem já tem o texto inteiro (teste, colagem).
    /// </summary>
    public static string? Id(string? completo)
    {
        var s = (completo ?? "").Trim();
        var p = EtiquetaKds.PrefixoQr.Length;
        if (s.Length <= p || !PrefixoCasa(s, p)) return null;
        var id = s[p..].Trim();
        if (id.Length == 0 || id.Length > TamanhoMaxId) return null;
        foreach (var c in id)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        return id.ToLowerInvariant();
    }

    /// <summary>O juntado até agora ainda pode ser (o começo de) uma etiqueta?</summary>
    private static bool Valido(string s)
    {
        var p = EtiquetaKds.PrefixoQr.Length;
        if (s.Length <= p) return PrefixoCasa(s, s.Length);
        if (!PrefixoCasa(s, p) || s.Length - p > TamanhoMaxId) return false;
        for (var i = p; i < s.Length; i++)
            if (!(char.IsAsciiLetterOrDigit(s[i]) || s[i] is '-' or '_')) return false;
        return true;
    }

    /// <summary>Os primeiros <paramref name="n"/> caracteres casam com o prefixo (tolerante a idioma do leitor)?</summary>
    private static bool PrefixoCasa(string s, int n)
    {
        var prefixo = EtiquetaKds.PrefixoQr;
        for (var i = 0; i < n && i < prefixo.Length; i++)
        {
            var esperado = prefixo[i];
            var c = s[i];
            if (esperado == ':')
            {
                if (c is not (':' or 'Ç' or 'ç' or ';')) return false;
            }
            else if (char.ToUpperInvariant(c) != esperado) return false;
        }
        return true;
    }
}
