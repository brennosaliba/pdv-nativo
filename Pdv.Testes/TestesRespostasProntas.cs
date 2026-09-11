using System.Text.Json;
using System.Text.RegularExpressions;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// RESPOSTAS PRONTAS DO CHAT DO IFOOD (11/09/2026, pedido do dono: "no frame vazio ao
/// lado esquerdo do chat, uns 5 popups com mensagem pré-pronta; o atendente clica e
/// copia automático; por exemplo pedido revirado").
///
/// O parser e as respostas de fábrica são puros (Pdv.Nucleo/RespostasProntas) e
/// provados pelo valor; a injeção na página e o editor da Configuração, pelo fonte.
/// </summary>
public static class TestesRespostasProntas
{
    public static void Rodar(Action<bool, string> checar)
    {
        // ── as de fábrica ──────────────────────────────────────────────────────
        var padrao = RespostasProntas.Padrao;
        checar(padrao.Count == 5 && RespostasProntas.Ler(null).Count == 5 && RespostasProntas.Ler("  \n \n").Count == 5,
            "sem config (null, vazio ou só espaço) valem as cinco de fábrica");
        checar(padrao.Any(r => r.Titulo == "Pedido revirado") && padrao.Any(r => r.Titulo == "Atraso na entrega"),
            "as de fábrica cobrem o exemplo do dono (pedido revirado) e o atraso");
        checar(padrao.All(r => r.Titulo.Length <= RespostasProntas.MaxTitulo && r.Texto.Length <= RespostasProntas.MaxTexto),
            "títulos até 40 e textos até 600 caracteres");
        checar(padrao.All(r => !r.Titulo.Contains('—') && !r.Texto.Contains('—') && !r.Texto.Contains('–')),
            "nenhuma resposta de fábrica tem travessão");
        checar(padrao.All(r => !Regex.IsMatch(r.Texto, "[A-Z]{4,}")), "nenhuma resposta grita em caixa alta");
        checar(padrao.Select(r => r.Titulo).Distinct().Count() == 5, "títulos distintos");

        // ── o formato que uma pessoa escreve ──────────────────────────────────
        var lidas = RespostasProntas.Ler("Pedido frio\nOlá! Sentimos muito.\nVamos resolver.\n\n\nTroco:\nO troco vai com o entregador.\n\nSó título");
        checar(lidas.Count == 3, "três blocos separados por linha em branco (uma ou mais) = três respostas");
        checar(lidas[0].Titulo == "Pedido frio" && lidas[0].Texto == "Olá! Sentimos muito.\nVamos resolver.",
            "a primeira linha é o título; o resto (com as quebras) é o texto");
        checar(lidas[1].Titulo == "Troco" && lidas[1].Texto == "O troco vai com o entregador.", "dois-pontos no fim do título saem");
        checar(lidas[2].Titulo == "Só título" && lidas[2].Texto == "Só título", "bloco de uma linha: título e texto iguais");
        var crlf = RespostasProntas.Ler("A\r\ntexto a\r\n\r\nB\r\ntexto b");
        checar(crlf.Count == 2 && crlf[1].Texto == "texto b", "quebra de linha do Windows (CRLF) é lida igual");
        checar(RespostasProntas.Ler("A\ntexto a\n \nB\ntexto b").Count == 2 && RespostasProntas.Ler("A\nx\n\t\nB\ny").Count == 2
               && RespostasProntas.Ler("A\nx\n\n\n\nB\ny").Count == 2,
            "linha 'em branco' com espaço ou tab (comum ao colar), ou várias, separa do mesmo jeito");
        var semTitulo = RespostasProntas.Ler("Olá! Seu pedido saiu da loja.\nBom apetite!");
        checar(semTitulo.Count == 1 && semTitulo[0].Texto == "Olá! Seu pedido saiu da loja.\nBom apetite!"
               && semTitulo[0].Titulo == "Olá! Seu pedido saiu da loja.",
            "bloco sem linha de título (a primeira termina em ponto): a mensagem inteira vai no texto");
        checar(RespostasProntas.Ler(RespostasProntas.Escrever(semTitulo)).SequenceEqual(semTitulo), "e a ida e volta continua estável");
        var pad = RespostasProntas.Padrao;
        checar(!pad.Any(r => Regex.IsMatch(r.Texto, "reembols|devolvemos|refazemos|já saiu", RegexOptions.IgnoreCase)),
            "as de fábrica não prometem reembolso nem afirmam que o pedido já saiu (quem decide é o gerente)");
        var muitas = RespostasProntas.Ler(string.Join("\n\n", Enumerable.Range(1, 12).Select(i => $"T{i}\ntexto {i}")));
        checar(muitas.Count == RespostasProntas.Maximo, $"passando de {RespostasProntas.Maximo}, corta (a coluna não é infinita)");
        var longa = RespostasProntas.Ler("T\n" + new string('x', 2000));
        checar(longa[0].Texto.Length == RespostasProntas.MaxTexto, "texto gigante é cortado no teto");

        // ── ida e volta: o que a Configuração grava é o que a tela do chat lê ──
        var ida = RespostasProntas.Escrever(lidas);
        var volta = RespostasProntas.Ler(ida);
        checar(volta.SequenceEqual(lidas), "Escrever depois Ler devolve a mesma lista (o editor não deforma nada)");
        checar(RespostasProntas.Ler(RespostasProntas.Escrever(padrao)).SequenceEqual(padrao), "idem para as de fábrica");

        // ── JSON para a página ────────────────────────────────────────────────
        var json = RespostasProntas.Json(new[] { new RespostasProntas.Resposta("Aspas \"ok\"", "Linha 1\nLinha 2 <b>") });
        using (var doc = JsonDocument.Parse(json))
        {
            var e = doc.RootElement[0];
            checar(e.GetProperty("titulo").GetString() == "Aspas \"ok\"" && e.GetProperty("texto").GetString() == "Linha 1\nLinha 2 <b>",
                "o JSON entregue à página preserva aspas, quebras e sinais (sem escapar demais nem de menos)");
        }
        checar(!json.Contains("\\u00", StringComparison.Ordinal) || !json.Contains("\\u003C", StringComparison.Ordinal),
            "acentos e sinais vão legíveis, não como \\u00XX");

        // ── o fonte: a página e a Configuração ────────────────────────────────
        var chat = Fonte(Path.Combine("Telas", "ChatIfood.xaml.cs")) ?? "";
        var cfgXaml = Fonte(Path.Combine("Telas", "Configuracao.xaml")) ?? "";
        var cfgCs = Fonte(Path.Combine("Telas", "Configuracao.xaml.cs")) ?? "";
        checar(chat.Contains("window.pdvDefinirRespostas = function") && chat.Contains("box.id = 'pdv-respostas'")
               && chat.Contains("body.pdv-so-chat #pdv-respostas.pdv-inline{display:block}"),
            "o script monta os cartões na página e só os mostra com o chat isolado (nunca por cima do login do Gestor)");
        checar(chat.Contains("navigator.clipboard") && chat.Contains("execCommand('copy')") && chat.Contains("execCommand('insertText'"),
            "o toque copia (com plano B) e tenta colar direto na caixa de mensagem");
        checar(chat.Contains("!/^pdv-/.test(p.children[i].id || '')"), "o holofote do chat não esconde os cartões");
        checar(chat.Contains("window.pdvDefinirRespostas(window.__pdvRespostas);"), "ao isolar o chat, os cartões (re)aparecem");
        checar(chat.Contains("RespostasProntas.Json(") && chat.Contains("if (e.IsSuccess) _ = DefinirRespostasAsync(core);"),
            "a lista é lida do banco e entregue à página a cada carga (editar + Recarregar basta)");
        checar(cfgXaml.Contains("x:Name=\"TxtRespostasChat\"") && cfgXaml.Contains("AcceptsReturn=\"True\""),
            "a Configuração tem o editor (caixa de texto de várias linhas)");
        checar(cfgCs.Contains("TxtRespostasChat.Text = RespostasProntas.Escrever(RespostasProntas.Ler(")
               && cfgCs.Contains("Vendas.GravarConfig(cx, RespostasProntas.Chave,"),
            "a Configuração carrega o que vale e grava normalizado na chave chat_respostas");
        checar(Regex.IsMatch(cfgCs, @"normalizadas\.Length == 0 \|\| normalizadas == RespostasProntas\.Escrever\(RespostasProntas\.Padrao\)\)\s*cx\.Execute\(""DELETE FROM config WHERE chave=@C"""),
            "apagar tudo, ou deixar as de fábrica como vieram, volta às de fábrica (a chave sai do banco)");
        checar(cfgCs.Contains("BlocoRespostasChat.Visibility = Se(_jaConfigurado);", StringComparison.Ordinal)
               && cfgXaml.Contains("x:Name=\"BlocoRespostasChat\"", StringComparison.Ordinal),
            "na primeira instalação o bloco não aparece (não é assunto de parear)");
        checar(chat.Contains("ev.stopPropagation()", StringComparison.Ordinal) && chat.Contains("function ajustarLarguraRespostas()", StringComparison.Ordinal),
            "o toque no cartão não vira clique fora da gaveta, e a caixa mede a borda da gaveta");
        checar(chat.Contains("function areaLivre(alvo)", StringComparison.Ordinal) && chat.Contains("document.elementFromPoint(", StringComparison.Ordinal)
               && chat.Contains("pill.id = 'pdv-respostas-pill'", StringComparison.Ordinal) && chat.Contains("box.classList.add('pdv-compacto')", StringComparison.Ordinal),
            "com a conversa aberta por cima do espaço, o painel vira a pilula 'Respostas prontas' e só aparece quando chamado (nunca em cima da conversa sem pedir)");
        checar(chat.Contains("if (window.pdvAjustarRespostas) window.pdvAjustarRespostas();", StringComparison.Ordinal)
               && chat.Contains("window.addEventListener('resize', function () { window.pdvAjustarRespostas(); });", StringComparison.Ordinal),
            "o modo é reavaliado a cada mexida no DOM e ao redimensionar (a conversa abre sem navegação)");
        checar(chat.IndexOf("execCommand('copy')", StringComparison.Ordinal) < chat.IndexOf("navigator.clipboard.writeText(texto).catch", StringComparison.Ordinal),
            "copiar: o caminho síncrono primeiro; a Promise só como plano B, com o erro engolido");
        checar(chat.Contains("aperte Ctrl+V", StringComparison.Ordinal), "sem conversa aberta, o cartão diz o caminho que existe (Ctrl+V)");
        var chatXaml = Fonte(Path.Combine("Telas", "ChatIfood.xaml")) ?? "";
        checar(chatXaml.Contains("Click=\"AlternarGestorInteiro\"") && chatXaml.Contains("Click=\"Diagnostico\"")
               && chat.Contains("gestor-diagnostico-") && chat.Contains("replace(/\\d{4,}/g, '####')") && chat.Contains("window.__pdvSemHolofote"),
            "a aba do chat tem 'Gestor inteiro' e 'Diagnóstico' (estrutura da tela mascarada em ProgramData) para mapear o Fale com o iFood");
        var textosCfg = Regex.Matches(cfgXaml, @"Text=""([^""]*)""").Select(x => x.Groups[1].Value).Where(x => x.Contains("resposta", StringComparison.OrdinalIgnoreCase));
        checar(textosCfg.Any() && textosCfg.All(x => !x.Contains('—') && !x.Contains('–')), "os textos novos da Configuração não têm travessão");
    }

    private static string? Fonte(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj")))
            {
                var c = Path.Combine(dir.FullName, relativo);
                return File.Exists(c) ? File.ReadAllText(c) : null;
            }
        return null;
    }
}
