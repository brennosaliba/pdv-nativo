using System.Text.RegularExpressions;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// REINICIAR O COMPUTADOR PELO CAIXA (1.0.48, 22/10/2026, pedido do dono: "quando o PC trava
/// ficar fácil reiniciar sem sair do PDV").
///
/// A REGRA (o que pode, quantas confirmações, as frases de uma linha, os argumentos do
/// shutdown.exe, a recusa do Windows e as linhas de auditoria) mora em Pdv.Nucleo/ReinicioDoPc
/// e é provada pelo VALOR. O que só existe no WPF (a opção nos dois menus, a ordem
/// "o Windows aceitou, depois a auditoria, só então o PDV fecha", nada de registro) é
/// travado no FONTE, como o quiosque (TestesQuiosque) e os menus da barra (TestesMenuBarra).
///
/// Também aqui: o AVISO DA SAÍDA DE SOM (follow-up da 1.0.42: a Savassi tocava no Spotify e
/// ninguém ouvia porque a saída padrão era um fone), regra pura em Pdv.Nucleo/SaidaDeSom.
/// </summary>
public static class TestesReinicioDoPc
{
    public static void Rodar(Action<bool, string> checar)
    {
        // ── 1. os argumentos do shutdown.exe: /r, /t 3, SEM /f ───────────────────
        checar(ReinicioDoPc.Programa == "shutdown.exe" && ReinicioDoPc.CaminhoDoShutdown(@"C:\WINDOWS\system32") == @"C:\WINDOWS\system32\shutdown.exe",
            "RP-1 o programa é o shutdown.exe da pasta do sistema (nunca pelo PATH)");
        var args = ReinicioDoPc.Argumentos;
        checar(args.StartsWith("/r /t 3", StringComparison.Ordinal), "RP-2 reinicia (/r) em 3 s (/t 3): " + args);
        checar(!Regex.IsMatch(args, @"(^|\s)/f(\s|$)"), "RP-3 sem /f: o PDV fecha antes de forma limpa (TEF, impressora e chat soltos como no fechamento normal)");
        checar(args.Contains("/c \"" + ReinicioDoPc.Motivo + "\"", StringComparison.Ordinal) && ReinicioDoPc.Motivo == "reiniciar pelo PDV",
            "RP-4 o motivo 'reiniciar pelo PDV' vai no /c (fica no log de eventos do Windows, 1074)");
        checar(ReinicioDoPc.Segundos == 3, "RP-5 três segundos: tempo de o PDV fechar, curto para o operador que quer o PC de volta");

        // ── 2. a pasta do sistema e o shutdown.exe presente ──────────────────────
        checar(ReinicioDoPc.MotivoParaNaoReiniciar(@"C:\WINDOWS\system32", _ => true) is null, "RP-6 pasta do sistema certa e shutdown.exe presente: pode");
        checar(ReinicioDoPc.MotivoParaNaoReiniciar(@"C:\WINDOWS\system32", _ => false) is { } sem && sem.Contains("shutdown.exe"),
            "RP-7 shutdown.exe ausente: motivo diz o que falta");
        checar(ReinicioDoPc.MotivoParaNaoReiniciar(@"C:\Users\x\Desktop", _ => true) is not null && ReinicioDoPc.MotivoParaNaoReiniciar(null, _ => true) is not null,
            "RP-8 pasta fora do padrão do sistema (ou nula): não roda nada de lá");

        // ── 3. o que decide: barrado, uma confirmação, duas ──────────────────────
        checar(ReinicioDoPc.Avaliar(tefOcupado: false, cobrancasNoPinpad: 0, itensNaComanda: 0) == ReinicioDoPc.Caminho.Confirmar,
            "RP-9 caixa parado: uma confirmação só");
        checar(ReinicioDoPc.Avaliar(false, 0, 2) == ReinicioDoPc.Caminho.ConfirmarDuasVezes, "RP-10 venda em andamento com itens: avisa e pede confirmação de novo");
        checar(ReinicioDoPc.Avaliar(true, 0, 0) == ReinicioDoPc.Caminho.Barrado && ReinicioDoPc.Avaliar(false, 1, 0) == ReinicioDoPc.Caminho.Barrado,
            "RP-11 cancelamento/estorno em andamento ou cobrança no pinpad: barrado (o cliente está com o cartão na mão)");
        checar(ReinicioDoPc.Avaliar(true, 0, 3) == ReinicioDoPc.Caminho.Barrado, "RP-12 barrado ganha da comanda: o pinpad decide antes");

        // ── 4. frases de uma linha, as palavras do dono, sem travessão ───────────
        checar(ReinicioDoPc.Pergunta == "Reiniciar o computador agora? O PDV volta sozinho.", "RP-13 a confirmação é a frase do dono, em uma linha");
        checar(ReinicioDoPc.Titulo == "Reiniciar o computador", "RP-14 o título é o nome da opção");
        var aviso1 = ReinicioDoPc.AvisoVenda(1);
        var aviso3 = ReinicioDoPc.AvisoVenda(3);
        checar(aviso1.Contains("1 item", StringComparison.Ordinal) && aviso3.Contains("3 itens", StringComparison.Ordinal) && aviso3.EndsWith("Reiniciar mesmo?", StringComparison.Ordinal),
            "RP-15 o aviso da venda diz quantos itens se perdem e pergunta de novo: " + aviso3);
        foreach (var (nome, texto) in new[] { ("pergunta", ReinicioDoPc.Pergunta), ("aviso da venda", aviso3), ("barrado", ReinicioDoPc.Barrado) })
        {
            checar(!texto.Contains('\n') && texto.Length <= 90, $"RP-16 {nome}: uma linha só ({texto.Length} caracteres)");
            checar(!texto.Contains('\u2014') && !texto.Contains(" - ", StringComparison.Ordinal), $"RP-17 {nome}: sem travessão");
        }
        checar(ReinicioDoPc.BotaoSim == "Reiniciar" && ReinicioDoPc.BotaoNao == "Voltar", "RP-18 botões com verbo: Reiniciar / Voltar");

        // ── 5. a resposta do Windows: 0 aceita; o resto é recusa com texto para a tela ──
        checar(ReinicioDoPc.Recusa(0) is null, "RP-19 código 0: o Windows aceitou");
        checar(ReinicioDoPc.Recusa(1190) is { } r1190 && r1190.Contains("já tem", StringComparison.Ordinal), "RP-20 1190: já tem um reinício marcado");
        checar(ReinicioDoPc.Recusa(5) is { } r5 && r5.Contains("permiss", StringComparison.Ordinal), "RP-21 5: sem permissão");
        checar(ReinicioDoPc.Recusa(77) is { } r77 && r77.Contains("77", StringComparison.Ordinal) && !r77.Contains('\n'), "RP-22 outro código: diz o número, em uma linha");

        // ── 6. auditoria e log local: quem, de onde, o motivo; nunca senha ────────
        var det = ReinicioDoPc.DetalheAuditoria("Ana", "menu Fechar / Sair", 2);
        checar(ReinicioDoPc.Evento == "reiniciar_pc" && det.Contains("reiniciar pelo PDV") && det.Contains("Ana") && det.Contains("menu Fechar / Sair") && det.Contains("2"),
            "RP-23 a auditoria leva o motivo, quem pediu, de onde e os itens: " + det);
        checar(ReinicioDoPc.DetalheAuditoria(null, "Fechar", 0).Contains("(sem operador)"), "RP-24 sem operador logado a auditoria diz isso, não fica vazia");
        var log = ReinicioDoPc.LinhaDeLog("Ana", "Fechar (quiosque)", "o Windows aceitou");
        checar(log.StartsWith("[reiniciar] ", StringComparison.Ordinal) && log.Contains("Ana") && log.Contains("o Windows aceitou") && !log.Contains('\n'),
            "RP-25 a linha do inicio-windows.txt tem a marca [reiniciar], quem e o resultado");

        // ── 7. o fonte: a opção nos dois lugares, a ordem certa, nada de registro ──
        var main = Fonte("MainWindow.xaml.cs") ?? "";
        checar(main.Contains("\"Reiniciar o PDV\", \"Reiniciar o computador\", \"Sair para o Windows\""),
            "RP-26 no quiosque, Fechar oferece Reiniciar o PDV, Reiniciar o computador e Sair para o Windows (nesta ordem)");
        checar(main.Contains("\"Fechar o PDV\", \"Reiniciar o computador\""), "RP-27 no modo normal, Fechar oferece Fechar o PDV e Reiniciar o computador");
        checar(Regex.Matches(main, @"ReinicioDoPcNoCaixa\.Pedir\(").Count == 2, "RP-28 os dois caminhos do Fechar chamam o MESMO pedido (ReinicioDoPcNoCaixa.Pedir)");
        checar(!main.Contains("AcessoDoMaster(\"Reiniciar o computador\""), "RP-29 reiniciar não pede o código do gerente (decisão: o PC volta no PDV)");

        var menu = MenuBarra.FecharSair();
        checar(menu.Select(i => i.Rotulo).SequenceEqual(new[] { "Fechamento de caixa", "Sair", "Reiniciar o computador" })
               && menu[2].Acao == MenuBarra.ReiniciarPc,
            "RP-30 o menu Fechar / Sair da venda lista Fechamento de caixa, Sair, Reiniciar o computador (nesta ordem)");
        var venda = Fonte(Path.Combine("Telas", "Venda.xaml.cs")) ?? "";
        checar(Regex.IsMatch(venda, @"case MenuBarra\.ReiniciarPc:\s*ReiniciarPc\(sender, e\)"), "RP-31 Reiniciar o computador -> ReiniciarPc na tela de venda");
        var handler = Trecho(venda, "private void ReiniciarPc(object sender, RoutedEventArgs e)", "private TaskCompletionSource<string?>? _menuAberto");
        checar(handler.Contains("ReinicioDoPcNoCaixa.Pedir(") && handler.Contains("_comanda.Count") && handler.Contains("_tefOcupado") && handler.Contains("_operador.Id"),
            "RP-32 a venda passa a comanda, o TEF em andamento e o operador logado");
        checar(handler.Contains("if (_contaAberta is not null) GuardarConta();"), "RP-33 comanda aberta (13/10) é guardada antes, como no Sair");

        var exe = Fonte("ReinicioDoPcNoCaixa.cs") ?? "";
        checar(exe.Contains("ReinicioDoPc.CaminhoDoShutdown(sistema)") && exe.Contains("ReinicioDoPc.Argumentos") && exe.Contains("Environment.SystemDirectory"),
            "RP-34 o exe roda o shutdown.exe da pasta do sistema com os argumentos do núcleo");
        checar(exe.Contains("UseShellExecute = false") && !exe.Contains("runas"), "RP-35 sem elevação: shutdown /r é do usuário comum");
        var ordem = new[] { "ReinicioDoPc.Recusa(", "Caixa.Auditar(", "InicioDoWindowsNoCaixa.Anotar(", "Application.Current.Shutdown()" }
            .Select(m => exe.LastIndexOf(m, StringComparison.Ordinal)).ToArray();
        checar(ordem.All(i => i >= 0) && ordem.SequenceEqual(ordem.OrderBy(i => i)),
            "RP-36 a ordem: o Windows aceitou (código) -> auditoria -> log local -> só então o PDV fecha (nunca fecha antes de provar)");
        checar(!exe.Contains("Registry") && !exe.Contains("Quiosque.Desligar") && !exe.Contains("DesfazerQuiosque") && !exe.Contains("ChaveRun"),
            "RP-37 reiniciar não toca o registro: o Shell do quiosque e o Run do início automático seguem como estão");
        checar(exe.Contains("Dialogo.Confirmar(") && exe.Contains("perigo: true"), "RP-38 a confirmação é o diálogo da casa, com a ação em vermelho");
        checar(exe.Contains("tef_transacao") && exe.Contains("'criando','aguardando'"), "RP-39 a cobrança no pinpad é lida do banco (a mesma leitura do portão da atualização)");

        var dialogo = Fonte(Path.Combine("Telas", "Dialogo.cs")) ?? "";
        var escolher = Trecho(dialogo, "public static int Escolher(", "public static void Avisar(");
        checar(escolher.Contains("b.ContentTemplate = RotuloQuebravel();") && escolher.Contains("TextBlock.TextWrappingProperty, TextWrapping.Wrap")
               && !escolher.Contains("b.Content = new TextBlock"),
            "RP-40 o Escolher quebra o rótulo em duas linhas pelo molde, com o Content ainda string (três opções com 'Reiniciar o computador' cabem a 1024; a suíte acha o botão pelo texto)");

        var csproj = Fonte("Pdv.csproj") ?? "";
        checar(csproj.Contains("<Version>1.0.54</Version>", StringComparison.Ordinal), "RP-41 a versão do caixa é 1.0.54 (so digital)");

        // ── 8. A SAÍDA DE SOM (follow-up da 1.0.42) ──────────────────────────────
        checar(SaidaDeSom.Aviso(new SaidaDeSom.Leitura(false, null, null)) == "Sem saída de som ativa no Windows.", "SS-1 nenhuma saída: o aviso do follow-up");
        checar(SaidaDeSom.Aviso(new SaidaDeSom.Leitura(true, "Fones de ouvido (Oneal)", null)) == "A saída de som padrão é um fone.", "SS-2 'Fones de ouvido (Oneal)' (o caso da Savassi em 16/10) pelo nome");
        checar(SaidaDeSom.Aviso(new SaidaDeSom.Leitura(true, "Speakers (Realtek)", SaidaDeSom.FormatoFone)) == "A saída de som padrão é um fone.", "SS-3 pelo formato do Windows (FormFactor 3 = Headphones), mesmo com nome neutro");
        checar(SaidaDeSom.Aviso(new SaidaDeSom.Leitura(true, "Headset (Logitech)", SaidaDeSom.FormatoHeadset)) is not null, "SS-4 headset (6) também é fone");
        checar(SaidaDeSom.Aviso(new SaidaDeSom.Leitura(true, "Alto-falantes (Realtek)", 1)) is null, "SS-5 alto-falante: nada a avisar");
        checar(SaidaDeSom.Aviso(new SaidaDeSom.Leitura(true, "Alto-falantes (Microfone USB)", 1)) is null, "SS-6 'Microfone' no nome não é fone");
        checar(SaidaDeSom.ParaOSinal(new SaidaDeSom.Leitura(false, null, null)) == "sem_saida"
               && SaidaDeSom.ParaOSinal(new SaidaDeSom.Leitura(true, "Fones (X)", null)) == "fone"
               && SaidaDeSom.ParaOSinal(new SaidaDeSom.Leitura(true, "Alto-falantes", 1)) is null,
            "SS-7 no sinal vai só a chave (sem_saida / fone / nada), nunca o nome do aparelho");
        foreach (var t in new[] { SaidaDeSom.SemSaida, SaidaDeSom.Fone })
            checar(!t.Contains('\n') && !t.Contains('\u2014'), "SS-8 aviso de uma linha, sem travessão: " + t);
        var musica = Fonte(Path.Combine("Telas", "Musica.cs")) ?? "";
        checar(musica.Contains("SaidaDeSom.Aviso(SpotifyNoCaixa.LerSaidaDeSom())"), "SS-9 a janela ♫ música mostra o aviso");
        var servico = Fonte("ServicoConversaChat.cs") ?? "";
        checar(servico.Contains("SaidaDeSom.ParaOSinal(SpotifyNoCaixa.LerSaidaDeSom())"), "SS-10 o sinal do chat leva a chave da saída de som");
        var nucleo = Fonte(Path.Combine("Pdv.Nucleo", "ConversaRaspadinha.cs")) ?? "";
        checar(nucleo.Contains("if (som is not null) o[\"som\"] = som;"), "SS-11 o estado do sinal só grava 'som' quando há o que avisar");
        var spot = Fonte("SpotifyNoCaixa.cs") ?? "";
        checar(spot.Contains("{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},0") && !spot.Contains("NAudio"), "SS-12 o formato vem da propriedade do Windows pelo COM que já existia (sem pacote novo)");
    }

    private static string Trecho(string fonte, string de, string ate)
    {
        var i = fonte.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var j = fonte.IndexOf(ate, i, StringComparison.Ordinal);
        return j < 0 ? fonte[i..] : fonte[i..j];
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
