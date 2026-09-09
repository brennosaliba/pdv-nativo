using System.Text.RegularExpressions;

namespace Pdv.Testes;

/// <summary>
/// CHAVE DE COR QUE NAO EXISTE DEIXA A TELA EM BRANCO, SEM ERRO NENHUM.
///
/// O QUE ACONTECEU (09/09/2026). Escrevi a tela do roteiro do TEF usando as chaves
/// "Sucesso", "Aviso" e "Primaria". Nenhuma das tres existe no tema deste caixa.
///
/// `Application.Current.Resources["Sucesso"]` devolve null, o TextBlock fica com
/// Foreground nulo, e o texto some. Sem excecao, sem log, sem nada: a tela desenha
/// o FUNDO da etiqueta e o texto invisivel dentro dela. O dono mandou o print com
/// retangulos cinzas no lugar do titulo, do valor e do REQNUM, e as etiquetas nunca
/// tinham aparecido desde a primeira versao.
///
/// Este teste le os fontes das telas e o tema, e reprova qualquer `R("...")` cuja
/// chave o tema nao tenha. Erro de digitacao em nome de cor volta a ser erro de
/// compilacao, na pratica.
/// </summary>
public static class TestesCoresDoRoteiro
{
    public static void Rodar(Action<bool, string> checar)
    {
        var raiz = AcharRaiz();
        checar(raiz is not null, "achei a raiz do repositorio para ler tema e telas");
        if (raiz is null) return;

        // ── AS CHAVES QUE O TEMA DEFINE ─────────────────────────────────────
        var chaves = new HashSet<string>(StringComparer.Ordinal);
        foreach (var arq in Directory.GetFiles(Path.Combine(raiz, "Temas"), "*.xaml")
                     .Concat(new[] { Path.Combine(raiz, "Estilos.xaml") }))
        {
            if (!File.Exists(arq)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(arq), "x:Key=\"([^\"]+)\""))
                chaves.Add(m.Groups[1].Value);
        }
        checar(chaves.Count > 20, $"o tema tem chaves de verdade ({chaves.Count})");
        checar(chaves.Contains("Texto") && chaves.Contains("Fundo"),
            "e as basicas estao la, senao a leitura quebrou e o teste vira decoracao");

        // ── TODA CHAVE USADA NAS TELAS EXISTE NO TEMA ───────────────────────
        var faltando = new List<string>();
        var usadas = 0;
        foreach (var arq in Directory.GetFiles(Path.Combine(raiz, "Telas"), "*.cs"))
        {
            var texto = File.ReadAllText(arq);
            foreach (Match m in Regex.Matches(texto, "\\bR\\(\"([A-Za-z][A-Za-z0-9]*)\"\\)"))
            {
                usadas++;
                var chave = m.Groups[1].Value;
                if (!chaves.Contains(chave)) faltando.Add(Path.GetFileName(arq) + ": " + chave);
            }
        }

        checar(usadas > 10, $"achei as cores usadas nas telas ({usadas})");
        checar(faltando.Count == 0,
            faltando.Count == 0
                ? "toda cor usada nas telas existe no tema"
                : "cor que o tema NAO tem (texto sai invisivel): " + string.Join(", ", faltando.Distinct()));
    }

    private static string? AcharRaiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "Temas"))
                && Directory.Exists(Path.Combine(dir.FullName, "Telas")))
                return dir.FullName;
        return null;
    }
}
