using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// Testes do tema diurno/noturno.
///
/// Duas famílias: a DECISÃO (que horas vira, o que valor inválido faz) e a
/// LEGIBILIDADE (o teste lê Temas/Claro.xaml de verdade e mede contraste WCAG —
/// paleta ilegível derruba a bateria, não espera reclamação no balcão).
/// </summary>
public static class TestesTema
{
    public static void Rodar(Action<bool, string> checar)
    {
        var de = new TimeOnly(6, 0);
        var ate = new TimeOnly(18, 0);
        static DateTime Em(int h, int m = 0) => new(2026, 8, 19, h, m, 0);

        // ── a decisão, com relógio cravado ──────────────────────────────────
        checar(Tema.Decidir("auto", de, ate, Em(7)) == ModoTema.Claro,
            "auto às 07:00 (janela 06–18) escolhe claro");
        checar(Tema.Decidir("auto", de, ate, Em(19)) == ModoTema.Escuro,
            "auto às 19:00 escolhe escuro");
        checar(Tema.Decidir("auto", de, ate, Em(2)) == ModoTema.Escuro,
            "auto às 02:00 escolhe escuro — o PDV opera de madrugada");
        checar(Tema.Decidir("auto", de, ate, Em(18)) == ModoTema.Escuro,
            "18:00 em ponto já é escuro — a borda é regra cravada, não opinião");
        checar(Tema.Decidir("auto", de, ate, Em(6)) == ModoTema.Claro,
            "06:00 em ponto já é claro — o início pertence à janela");

        // janela invertida = turno noturno (claro DE 18h ATÉ 6h)
        var deN = new TimeOnly(18, 0);
        var ateN = new TimeOnly(6, 0);
        checar(Tema.Decidir("auto", deN, ateN, Em(2)) == ModoTema.Claro,
            "janela invertida 18–06: às 02:00 é claro (cruza a meia-noite)");
        checar(Tema.Decidir("auto", deN, ateN, Em(12)) == ModoTema.Escuro,
            "janela invertida 18–06: ao meio-dia é escuro");

        // explícito ignora o relógio
        checar(Tema.Decidir("claro", de, ate, Em(23)) == ModoTema.Claro,
            "tema=claro vale às 23h — explícito manda no relógio");
        checar(Tema.Decidir("escuro", de, ate, Em(12)) == ModoTema.Escuro,
            "tema=escuro vale ao meio-dia");

        // lixo não derruba o caixa: o CLI grava qualquer string sem validar
        foreach (var lixo in new[] { null, "", "azul", "AUTO ", "Claro\n" })
            checar(Tema.Decidir(lixo == "AUTO " ? "AUTO " : lixo, de, ate, Em(12)) is ModoTema.Claro or ModoTema.Escuro,
                $"valor '{lixo ?? "null"}' não lança exceção");
        checar(Tema.Decidir("azul", de, ate, Em(12)) == ModoTema.Escuro,
            "valor desconhecido cai no escuro (o tema que sempre existiu)");
        checar(Tema.Decidir("AUTO ", de, ate, Em(12)) == ModoTema.Claro,
            "'AUTO ' com espaço é aceito como auto (trim + case)");

        // janela malformada cai no padrão, não em exceção
        var (d1, a1) = Tema.LerJanela("25:99", "meio-dia");
        checar(d1 == new TimeOnly(6, 0) && a1 == new TimeOnly(18, 0),
            "horário malformado devolve a janela padrão 06:00–18:00");
        var (d2, a2) = Tema.LerJanela("07:30", "20:15");
        checar(d2 == new TimeOnly(7, 30) && a2 == new TimeOnly(20, 15),
            "horário válido é respeitado");

        // ── config grava/lê/regrava sem duplicar ────────────────────────────
        var arquivo = Path.Combine(Path.GetTempPath(), $"tema_teste_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir();
            Vendas.GravarConfig(cx, "tema", "claro");
            Vendas.GravarConfig(cx, "tema", "auto");
            checar(Vendas.Config(cx, "tema") == "auto", "regravar tema fica com o último valor");
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM config WHERE chave='tema'") == 1,
                "regravar tema não duplica a linha de config");
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }

        // ── legibilidade: mede o XAML real, não uma cópia ───────────────────
        var pastaTemas = AcharPastaTemas();
        checar(pastaTemas is not null, "pasta Temas/ encontrada a partir do binário do teste");
        if (pastaTemas is null) return;

        var claro = Cores(Path.Combine(pastaTemas, "Claro.xaml"));
        var escuro = Cores(Path.Combine(pastaTemas, "Escuro.xaml"));

        // o mesmo conjunto de chaves nos dois: chave faltando não quebra o build,
        // quebra em produção na primeira tela que pedir — com o caixa aberto
        var soClaro = Chaves(Path.Combine(pastaTemas, "Claro.xaml"));
        var soEscuro = Chaves(Path.Combine(pastaTemas, "Escuro.xaml"));
        checar(soClaro.SetEquals(soEscuro),
            "Claro.xaml e Escuro.xaml têm exatamente o mesmo conjunto de x:Key" +
            (soClaro.SetEquals(soEscuro) ? "" :
                $" — só no claro: [{string.Join(" ", soClaro.Except(soEscuro))}]" +
                $" só no escuro: [{string.Join(" ", soEscuro.Except(soClaro))}]"));

        // contraste no CLARO (o tema novo; o escuro é o que a loja já usa hoje)
        void Minimo(string frente, string fundo, double minimo) =>
            checar(Tema.Contraste(claro[frente], claro[fundo]) >= minimo,
                $"claro: {frente} sobre {fundo} = {Tema.Contraste(claro[frente], claro[fundo]):0.00}:1 (mínimo {minimo}:1)");

        Minimo("CorTexto", "CorFundo", 7);
        Minimo("CorTexto", "CorPainel", 7);
        Minimo("CorTexto", "CorPainelAlto", 7);
        Minimo("CorTextoFraco", "CorFundo", 4.5);
        Minimo("CorTextoFraco", "CorPainel", 4.5);
        Minimo("CorOk", "CorPainel", 4.5);
        Minimo("CorErro", "CorPainel", 4.5);
        Minimo("CorCiano", "CorPainel", 4.5);
        Minimo("CorAmarelo", "CorPainel", 4.5);
        Minimo("CorRosa", "CorPainel", 4.5);

        // AGENDADO (KDS, 04/09): roxo próprio do pedido com hora marcada, medido nos
        // DOIS temas — o quadro fica na parede da cozinha no turno da noite também.
        Minimo("CorAgendado", "CorPainel", 4.5);
        Minimo("CorAgendado", "CorAgendadoFundo", 4.5);
        Minimo("CorAgendado", "CorChipAgendadoFundo", 4.5);
        Minimo("CorTexto", "CorAgendadoFundo", 7);
        void MinimoEscuro(string frente, string fundo, double minimo) =>
            checar(Tema.Contraste(escuro[frente], escuro[fundo]) >= minimo,
                $"escuro: {frente} sobre {fundo} = {Tema.Contraste(escuro[frente], escuro[fundo]):0.00}:1 (mínimo {minimo}:1)");
        MinimoEscuro("CorAgendado", "CorPainel", 4.5);
        MinimoEscuro("CorAgendado", "CorAgendadoFundo", 4.5);
        MinimoEscuro("CorAgendado", "CorChipAgendadoFundo", 4.5);
        MinimoEscuro("CorTexto", "CorAgendadoFundo", 7);

        // ── HIERARQUIA DO CARD DO KDS (04/09) ───────────────────────────────
        // A reclamação do dono na foto foi de COR, não de layout: subitem do combo,
        // rodapé "AGUARDANDO O ENTREGADOR" e nome do cliente eram todos TextoFraco.
        // Três papéis, um cinza. Aqui medem-se as duas coisas que a correção precisa
        // ter: cada cor LEGÍVEL onde ela é usada, e cada par SEPARADO o bastante para
        // o olho pegar a diferença antes de ler.
        Minimo("CorTextoSubItem", "CorPainel", 7);
        Minimo("CorTextoSubItem", "CorFundo", 7);
        Minimo("CorTextoSubItem", "CorPainelAlto", 7);
        // O card agendado tem fundo próprio: o subitem tem que continuar legível lá.
        Minimo("CorTextoSubItem", "CorAgendadoFundo", 7);
        Minimo("CorTextoEspera", "CorEsperaFundo", 4.5);
        Minimo("CorTextoEspera", "CorPainel", 4.5);
        MinimoEscuro("CorTextoSubItem", "CorPainel", 7);
        MinimoEscuro("CorTextoSubItem", "CorFundo", 7);
        MinimoEscuro("CorTextoSubItem", "CorPainelAlto", 7);
        MinimoEscuro("CorTextoSubItem", "CorAgendadoFundo", 7);
        MinimoEscuro("CorTextoEspera", "CorEsperaFundo", 4.5);
        MinimoEscuro("CorTextoEspera", "CorPainel", 4.5);

        // A SEPARAÇÃO entre os três níveis. Sem isto seria possível "corrigir" o
        // problema criando chaves novas com o mesmo hex de sempre: passaria em tudo
        // acima e o card na parede continuaria igual.
        void Separado(Dictionary<string, string> tema, string nome, string a, string b, double minimo) =>
            checar(Tema.Contraste(tema[a], tema[b]) >= minimo,
                $"{nome}: {a} x {b} = {Tema.Contraste(tema[a], tema[b]):0.00}:1 de distância (mínimo {minimo}:1)");

        foreach (var (nome, tema) in new[] { ("claro", claro), ("escuro", escuro) })
        {
            // subitem não é o item principal, e não é a legenda cinza
            Separado(tema, nome, "CorTextoSubItem", "CorTexto", 1.35);
            Separado(tema, nome, "CorTextoSubItem", "CorTextoFraco", 1.4);
            // o rodapé de espera não é o subitem
            Separado(tema, nome, "CorTextoEspera", "CorTextoSubItem", 1.3);
            Separado(tema, nome, "CorTextoEspera", "CorTexto", 1.7);
            // e a faixa de espera precisa APARECER como faixa sobre o card
            Separado(tema, nome, "CorEsperaFundo", "CorPainel", 1.12);
        }

        // o botão principal é branco sobre o rosa — nos DOIS temas
        checar(Tema.Contraste("#FFFFFF", claro["CorRosa"]) >= 4.5,
            $"claro: branco sobre o rosa do botão = {Tema.Contraste("#FFFFFF", claro["CorRosa"]):0.00}:1");
        checar(Tema.Contraste("#FFFFFF", escuro["CorRosa"]) >= 2.2,
            "escuro: rosa da marca segue legível com branco (limite histórico)");
    }

    /// <summary>Sobe do binário até achar a pasta Temas/ do repositório.</summary>
    private static string? AcharPastaTemas()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var cand = Path.Combine(dir.FullName, "Temas");
            if (File.Exists(Path.Combine(cand, "Claro.xaml"))) return cand;
            dir = dir.Parent;
        }
        return null;
    }

    private static Dictionary<string, string> Cores(string xaml) =>
        Regex.Matches(File.ReadAllText(xaml), "<Color x:Key=\"(\\w+)\">(#[0-9A-Fa-f]{6,8})</Color>")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

    private static HashSet<string> Chaves(string xaml) =>
        Regex.Matches(File.ReadAllText(xaml), "x:Key=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value).ToHashSet();
}
