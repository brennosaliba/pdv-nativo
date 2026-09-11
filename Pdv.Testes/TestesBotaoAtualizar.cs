using System.Text.RegularExpressions;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// UM BOTÃO SÓ E A LOJA EM PRODUÇÃO (11/09/2026, pedido do dono, na véspera de publicar
/// a 1.0: "juntar o botão sincronizar e atualizar em uma única função; ao mesmo tempo
/// que puxa atualização já puxa sincronização de preço; menos botão, e no lugar entra o
/// WhatsApp" e "ao publicar o installer e updates, remover as mensagens de teste de
/// homologação, modo de homologação, e já deixar em modo produção").
///
/// O que se prova aqui:
///  1. a barra tem UM botão Atualizar (o Sincronizar saiu), com os dois selos dentro, e
///     o WhatsApp está na barra;
///  2. o toque faz as duas coisas, nesta ordem: painel (preços) primeiro, versão depois,
///     e só então a troca do programa; sem versão nova é UMA caixa só;
///  3. nenhum texto do caixa manda mais "tocar em Sincronizar" (o botão não existe);
///  4. a loja nasce em PRODUÇÃO: sem a chave `homologacao` o modo está desligado, sem a
///     chave de ambiente a biblioteca fala com a produção, nada no caixa nem no instalador
///     grava essas chaves, e todo botão/faixa de homologação está atrás do interruptor.
/// </summary>
public static class TestesBotaoAtualizar
{
    public static void Rodar(Action<bool, string> checar)
    {
        var xaml = Fonte(Path.Combine("Telas", "Venda.xaml")) ?? "";
        var cs = Fonte(Path.Combine("Telas", "Venda.xaml.cs")) ?? "";
        checar(xaml.Length > 0 && cs.Length > 0, "achei Telas/Venda.xaml e Telas/Venda.xaml.cs");
        if (xaml.Length == 0 || cs.Length == 0) return;

        // ── 1. a barra ────────────────────────────────────────────────────────
        checar(!xaml.Contains("Click=\"Sincronizar\"", StringComparison.Ordinal)
               && !xaml.Contains("x:Name=\"BtnSync\"", StringComparison.Ordinal),
            "o botão Sincronizar saiu da barra");
        checar(Regex.Matches(xaml, "Click=\"AtualizarOCaixa\"").Count == 1, "sobrou UM botão Atualizar");
        var botao = Regex.Match(xaml, @"<Button x:Name=""BtnAtualizar"".*?</Button>", RegexOptions.Singleline).Value;
        checar(botao.Contains("Text=\"Atualizar\"", StringComparison.Ordinal)
               && botao.Contains("x:Name=\"ChipPendencia\"", StringComparison.Ordinal)
               && botao.Contains("x:Name=\"ChipVersaoNova\"", StringComparison.Ordinal),
            "o botão diz Atualizar e carrega os dois selos (fila presa e versão nova)");
        var ordem = Regex.Matches(xaml, @"Click=""(AtualizarOCaixa|AbrirKds|AbrirChat|AbrirWhatsApp|Sangria)""")
            .Select(m => m.Groups[1].Value).ToList();
        checar(ordem.SequenceEqual(new[] { "AtualizarOCaixa", "AbrirKds", "AbrirChat", "AbrirWhatsApp", "Sangria" }),
            "na barra: Atualizar, Delivery, Chat, WhatsApp, Sangria (o WhatsApp ocupa o lugar que era de dois botões)");
        var textos = Regex.Matches(xaml, @"(?:Text|ToolTip|Content)=""([^""]*)""").Select(m => m.Groups[1].Value).ToList();
        checar(textos.All(t => !t.Contains("Sincronizar", StringComparison.Ordinal)),
            "nenhum texto da tela de venda manda tocar em Sincronizar");
        checar(textos.All(t => !t.Contains('—') && !t.Contains('–')), "sem travessão nos textos da tela de venda");

        // ── 2. o toque faz as duas coisas, nesta ordem ───────────────────────
        checar(!cs.Contains("private async void Sincronizar(", StringComparison.Ordinal)
               && !cs.Contains("BtnSync", StringComparison.Ordinal),
            "o handler Sincronizar não existe mais (um caminho só, um botão só)");
        var corpo = Trecho(cs, "private async void AtualizarOCaixa(", "private static void MostrarOQueDesceu(");
        checar(corpo.Length > 0, "achei AtualizarOCaixa na tela de venda");
        var iSync = corpo.IndexOf("Sincronizacao.ExecutarAsync(", StringComparison.Ordinal);
        var iVersao = corpo.IndexOf("AtualizarCaixa.ProcurarNoSilencioAsync()", StringComparison.Ordinal);
        var iTroca = corpo.IndexOf("AtualizarCaixa.ExecutarAsync(dono, _comanda.Count, _tefOcupado)", StringComparison.Ordinal);
        checar(iSync >= 0 && iVersao > iSync && iTroca > iVersao,
            "primeiro o painel (preços), depois a pergunta da versão, e só então a troca do programa");
        checar(corpo.Contains("reenviarDesistidas: true", StringComparison.Ordinal),
            "o toque continua sendo o gesto 'tratei o motivo, tenta de novo' para as vendas desistidas");
        checar(corpo.Contains("if (r.Ok) RecarregarCatalogo();", StringComparison.Ordinal),
            "com o painel lido, a grade recarrega (a comanda acompanha a tabela de preços)");
        checar(Regex.IsMatch(corpo, @"else if \(nova is null\)\s*\{\s*MostrarOQueDesceu\(dono, r\);"),
            "sem versão nova: UMA caixa só, com o que desceu e a linha do programa");
        checar(Regex.IsMatch(corpo, @"if \(nova is not null\)[\s\S]*?AtualizarCaixa\.ExecutarAsync\("),
            "com versão nova: a caixa que abre é a da troca (portão, sim do operador, download)");
        checar(corpo.Contains("if (!estaFechando) BtnAtualizar.IsEnabled = true;", StringComparison.Ordinal),
            "fechando para trocar o programa, ninguém mexe mais na tela");
        var caixa = Trecho(cs, "private static void MostrarOQueDesceu(", "private void PintarPendencias()");
        checar(caixa.Contains("r.SemNovidade", StringComparison.Ordinal) && caixa.Contains("\"Tudo em dia\"", StringComparison.Ordinal)
               && caixa.Contains("Programa:  versão {versao}", StringComparison.Ordinal),
            "a caixa diz 'Tudo em dia' numa frase, ou lista cardápio, fotos, notas e a linha do programa");
        checar(cs.Contains("private void PintarDica()", StringComparison.Ordinal)
               && Trecho(cs, "private void PintarPendencias()", "private void PintarDica()").Contains("_dicaPainel =", StringComparison.Ordinal)
               && Trecho(cs, "private void PintarVersaoNova(", "/// A COMANDA ABERTA").Contains("_dicaPrograma =", StringComparison.Ordinal),
            "a dica do botão junta as duas metades (programa e painel) em vez de uma apagar a outra");
        // A checagem automática (relógio, 6 h) continua existindo: o selo acende sem toque.
        checar(cs.Contains("private void ProcurarAtualizacao(bool forcar = false)", StringComparison.Ordinal)
               && Regex.IsMatch(cs, @"IniciarRelogio\(\); PintarPendencias\(\); ProcurarAtualizacao\(\);"),
            "o selo de versão continua acendendo sozinho ao entrar e pelo relógio");

        // ── 3. nenhum texto manda "tocar em Sincronizar" ─────────────────────
        foreach (var rel in new[] { Path.Combine("Telas", "Configuracao.xaml.cs"), Path.Combine("Pdv.Nucleo", "Operadores.cs"),
                                    Path.Combine("Pdv.Nucleo", "Sincronizacao.cs"), Path.Combine("Pdv.Nucleo", "Drenagem.cs") })
        {
            var f = Fonte(rel) ?? "";
            var linhasComTexto = f.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Where(l => Regex.IsMatch(l, "\"[^\"]*\\bSincronizar\\b[^\"]*\""))
                .ToList();
            checar(f.Length > 0 && linhasComTexto.Count == 0,
                $"{rel}: nenhuma frase de tela cita o botão Sincronizar" + (linhasComTexto.Count == 0 ? "" : $" (achei {linhasComTexto.Count})"));
        }

        // ── 4. a loja nasce em PRODUÇÃO ──────────────────────────────────────
        using (var cx = Banco.Abrir())
        {
            var homologAntes = Vendas.Config(cx, "homologacao");
            var ambienteAntes = Vendas.Config(cx, ConfigPGWebLib.ChaveAmbiente);
            try
            {
                cx.Execute("DELETE FROM config WHERE chave IN ('homologacao', @A)", new { A = ConfigPGWebLib.ChaveAmbiente });
                checar(!Vendas.Homologacao(cx) && !ModoHomologacao.Ligado(cx),
                    "instalação nova (sem a chave homologacao): o modo de homologação está DESLIGADO");
                checar(ModoHomologacao.EntradaDireta(cx) is null,
                    "instalação nova: o caixa pede login e fechamento como sempre (sem operador de teste)");
                checar(!MenuTef.Aparece(false), "instalação nova: o Menu do TEF não aparece");
                checar(ConfigPGWebLib.Ambiente(c => Vendas.Config(cx, c)) == PW.ENVRMNT_PROD,
                    "instalação nova (sem a chave de ambiente): a biblioteca PayGo fala com a PRODUÇÃO");
            }
            finally
            {
                if (homologAntes is not null) Vendas.GravarConfig(cx, "homologacao", homologAntes);
                if (ambienteAntes is not null) Vendas.GravarConfig(cx, ConfigPGWebLib.ChaveAmbiente, ambienteAntes);
            }
        }

        // Ninguém no caixa nem no instalador LIGA o modo: a chave só existe se alguém
        // gravar à mão no banco (foi assim na homologação). O exe publicado nasce limpo.
        var raiz = Raiz();
        if (raiz is not null)
        {
            var fontes = new[] { "Telas", "Pdv.Nucleo", "Pdv.Instalador", "." }
                .SelectMany(d => Directory.EnumerateFiles(Path.Combine(raiz, d), "*.cs", d == "." ? SearchOption.TopDirectoryOnly : SearchOption.TopDirectoryOnly))
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .ToList();
            var ligam = fontes.Where(p =>
            {
                var t = File.ReadAllText(p);
                return Regex.IsMatch(t, @"GravarConfig\([^;]*""homologacao""")
                    || Regex.IsMatch(t, @"GravarConfig\([^;]*(ChaveAmbiente|""tef_pgweb_ambiente"")");
            }).Select(Path.GetFileName).ToList();
            checar(fontes.Count > 20 && ligam.Count == 0,
                "nenhum código do caixa ou do instalador grava `homologacao` ou o ambiente de teste" + (ligam.Count == 0 ? "" : ": " + string.Join(", ", ligam)));

            var instalador = Directory.EnumerateFiles(Path.Combine(raiz, "Pdv.Instalador"), "*.*", SearchOption.TopDirectoryOnly)
                .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText).ToList();
            // (só o que vira texto de tela: linha de comentário não conta)
            var falaEmTeste = instalador.SelectMany(t => t.Split('\n'))
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Count(l => Regex.IsMatch(l, "\"[^\"]*(homologa|sandbox)[^\"]*\"", RegexOptions.IgnoreCase));
            checar(instalador.Count > 0 && falaEmTeste == 0,
                "o instalador não fala em homologação nem em sandbox" + (falaEmTeste == 0 ? "" : $" (achei {falaEmTeste})"));

            var csproj = Fonte("Pdv.csproj") ?? "";
            checar(Regex.IsMatch(csproj, @"<Version>1\.0\.\d+</Version>") && csproj.Contains("<Product>MMFood</Product>", StringComparison.Ordinal),
                "o exe publicado é o MMFood 1.0.x");
        }

        // Tudo que é de homologação está atrás do interruptor, e nasce escondido no XAML
        // (antes mesmo de a tela ler o banco).
        // 11/09 (dono, ao ver a foto): "pode remover o botão roteiro do TEF". Saiu de vez.
        checar(!xaml.Contains("BtnRoteiroTef", StringComparison.Ordinal) && !xaml.Contains("Roteiro do TEF", StringComparison.Ordinal)
               && !cs.Contains("AbrirRoteiroTef", StringComparison.Ordinal)
               && !File.Exists(Path.Combine(Raiz() ?? "", "Telas", "TelaRoteiroTef.cs")),
            "o botão e a tela Roteiro do TEF não existem mais (homologação aprovada)");
        foreach (var nome in new[] { "BtnValorLivre", "BtnMenuTef" })
        {
            var bloco = Regex.Match(xaml, $@"<Button x:Name=""{nome}""[^>]*>").Value;
            checar(bloco.Contains("Visibility=\"Collapsed\"", StringComparison.Ordinal), $"{nome} nasce escondido no XAML");
        }
        checar(cs.Contains("BtnValorLivre.Visibility = _homologacao ?", StringComparison.Ordinal)
               && cs.Contains("MenuTef.Aparece(_homologacao)", StringComparison.Ordinal),
            "valor de teste e Menu do TEF só aparecem com `homologacao` = 1");
        var main = Fonte("MainWindow.xaml.cs") ?? "";
        checar(main.Contains("FaixaHomologacao.Visibility = ModoHomologacao.Ligado(cx) ? Visibility.Visible : Visibility.Collapsed", StringComparison.Ordinal),
            "a faixa MODO DE HOMOLOGAÇÃO só existe com o interruptor ligado");
        var pag = Fonte(Path.Combine("Telas", "Pagamento.xaml.cs")) ?? "";
        checar(Regex.IsMatch(pag, @"RecadoDoTef\(EhHomologacao\(\) &&") && pag.Contains("!EhHomologacao()) return detalhe;", StringComparison.Ordinal),
            "o REQNUM do roteiro só entra na tela de pagamento em homologação");
    }

    private static string Trecho(string todo, string de, string ate)
    {
        var i = todo.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var f = todo.IndexOf(ate, i + de.Length, StringComparison.Ordinal);
        return f < 0 ? "" : todo[i..f];
    }

    private static string? Raiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj")))
                return dir.FullName;
        return null;
    }

    private static string? Fonte(string relativo)
    {
        var r = Raiz();
        if (r is null) return null;
        var c = Path.Combine(r, relativo);
        return File.Exists(c) ? File.ReadAllText(c) : null;
    }
}
