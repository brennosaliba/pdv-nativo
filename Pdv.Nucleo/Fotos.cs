using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Pdv.Nucleo;

/// <summary>
/// Cache de fotos dos produtos em disco.
///
/// A grade do caixa não pode depender de internet: baixar a imagem toda vez deixaria
/// a tela lenta e, sem rede, os produtos apareceriam sem foto — justo quando o
/// operador mais precisa reconhecer o item de relance. Então baixa uma vez e guarda.
/// </summary>
public static class Fotos
{
    public static string Pasta => Path.Combine(Banco.Pasta, "fotos");

    /// <summary>Pasta de dados do PDV — onde o cliente também larga o logo dele.</summary>
    public static string PastaDados => Banco.Pasta;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Nome do arquivo derivado da URL — mesma URL, mesmo arquivo.</summary>
    public static string CaminhoDe(string url)
    {
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url)))[..16];
        var ext = Path.GetExtension(new Uri(url).AbsolutePath);
        if (ext.Length is 0 or > 5) ext = ".jpg";
        return Path.Combine(Pasta, hash + ext);
    }

    public static string? JaBaixada(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try { var c = CaminhoDe(url); return File.Exists(c) ? c : null; }
        catch { return null; }
    }

    /// <summary>
    /// URLs que já falharam nesta execução do app. Sem esta memória, toda sincronização
    /// mói as MESMAS URLs mortas de novo — com dezenas delas no catálogo, o botão
    /// Sincronizar ficava minutos girando para baixar zero fotos.
    /// </summary>
    private static readonly HashSet<string> Falharam = new();

    /// <summary>
    /// Baixa o que falta, dentro de um orçamento de tempo. Devolve quantas entraram.
    /// Foto é enfeite: o que não coube neste ciclo fica para o próximo.
    /// </summary>
    public static async Task<int> BaixarFaltantesAsync(IEnumerable<string> urls, Action<int, int>? progresso = null,
        TimeSpan? orcamento = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Pasta);
        List<string> lista;
        lock (Falharam)
            lista = urls.Where(u => !string.IsNullOrWhiteSpace(u) && !Falharam.Contains(u)).Distinct().ToList();

        var limite = DateTime.UtcNow + (orcamento ?? TimeSpan.FromSeconds(45));
        var n = 0;
        for (var i = 0; i < lista.Count; i++)
        {
            if (ct.IsCancellationRequested || DateTime.UtcNow > limite) break;
            var url = lista[i];
            progresso?.Invoke(i + 1, lista.Count);
            try
            {
                var destino = CaminhoDe(url);
                if (File.Exists(destino)) continue;
                // timeout curto por foto: URL morta não pode custar 20s cada
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                var bytes = await Http.GetByteArrayAsync(url, cts.Token);
                if (bytes.Length < 200) { lock (Falharam) Falharam.Add(url); continue; }
                await File.WriteAllBytesAsync(destino, bytes, CancellationToken.None);
                n++;
            }
            catch { lock (Falharam) Falharam.Add(url); }
        }
        return n;
    }
}
