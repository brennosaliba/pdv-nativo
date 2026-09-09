using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// NENHUM TESTE PODE CRIAR TERMINAL NA FROTA DE VERDADE.
///
/// 09/09/2026: o dono abriu a lista de terminais e viu seis caixas na Savassi, três
/// com turno aberto. A loja não tem seis caixas. Três eram `term-teste`,
/// `term-promo` e `term-valor`, nascidos aqui nesta pasta e mandados para a
/// PRODUÇÃO pela tela de venda que os testes sobem. O caminho inteiro está contado
/// em <see cref="Isolamento"/>.
///
/// Este arquivo tem dois tipos de teste, e o segundo é o que importa a longo prazo:
///
///   · o de comportamento, que prova que Isolamento.SemNuvem realmente desvia a
///     consulta do endereço de produção;
///   · o ESTRUTURAL, que lê os fontes desta pasta e reprova quem semear terminal
///     sem isolar. Sem ele, o próximo teste escrito daqui a três meses repete o
///     defeito e ninguém liga o terminal fantasma à causa.
/// </summary>
public static class TestesIsolamento
{
    public static void Rodar(Action<bool, string> checar)
    {
        // ── 1. O DESVIO FUNCIONA ────────────────────────────────────────────
        var arquivo = Path.Combine(Path.GetTempPath(), $"iso-{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        try
        {
            Banco.CaminhoForcado = arquivo;
            Banco.Migrar(arquivo);
            using (var cx = Banco.Abrir(arquivo))
            {
                // Antes de isolar: a chave não existe, e é ISSO que faz o código cair
                // no padrão de produção.
                checar(Vendas.Config(cx, "supabase_url") is null,
                    "banco novo não tem supabase_url, e por isso cairia na produção");

                Isolamento.SemNuvem(cx);

                var url = Vendas.Config(cx, "supabase_url");
                checar(url == Isolamento.UrlMorta, $"depois de isolar, a nuvem é o endereço morto ({url})");
                checar(url != Nuvem.UrlPadrao, "e não é o projeto de produção");
                checar(!string.IsNullOrEmpty(url) && !url!.Contains("supabase.co"),
                    "nem qualquer outro supabase");

                var atu = Vendas.Config(cx, "atualizacao_url");
                checar(atu is not null && atu.StartsWith(Isolamento.UrlMorta),
                    "a url de atualização também aponta para o endereço morto");
                checar(atu != Atualizacao.UrlPadrao, "e não para o arquivo público de verdade");
            }
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }

        // ── 2. NINGUÉM ESQUECEU ─────────────────────────────────────────────
        // Lê os fontes: todo arquivo que semeia terminal precisa isolar a nuvem.
        var pasta = AcharPastaDosTestes();
        checar(pasta is not null, "achei a pasta dos fontes de teste para conferir");
        if (pasta is null) return;

        var faltando = new List<string>();
        var semeadores = 0;
        foreach (var f in Directory.GetFiles(pasta, "*.cs"))
        {
            var texto = File.ReadAllText(f);
            if (!texto.Contains("INSERT INTO terminal")) continue;
            semeadores++;
            if (!texto.Contains("Isolamento.SemNuvem")) faltando.Add(Path.GetFileName(f));
        }

        // Se este número cair para zero, a busca quebrou e o teste virou decoração.
        checar(semeadores >= 5, $"achei os arquivos que semeiam terminal ({semeadores})");
        checar(faltando.Count == 0,
            faltando.Count == 0
                ? "todo teste que semeia terminal isola a nuvem"
                : $"semeia terminal SEM isolar a nuvem: {string.Join(", ", faltando)}");
    }

    /// <summary>
    /// A pasta dos fontes, a partir do binário. Sobe até achar Pdv.Testes com este
    /// arquivo dentro, para funcionar tanto em bin\Debug quanto em bin\Release.
    /// </summary>
    private static string? AcharPastaDosTestes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var alvo = Path.Combine(dir.FullName, "Pdv.Testes");
            if (File.Exists(Path.Combine(alvo, "Isolamento.cs"))) return alvo;
            if (File.Exists(Path.Combine(dir.FullName, "Isolamento.cs"))
                && dir.Name == "Pdv.Testes") return dir.FullName;
        }
        return null;
    }
}
