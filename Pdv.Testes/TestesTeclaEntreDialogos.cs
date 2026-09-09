using System.Text.RegularExpressions;

namespace Pdv.Testes;

/// <summary>
/// A TECLA ENTRE DOIS DIALOGOS.
///
/// O QUE ACONTECEU (09/09/2026, passo 18 da homologacao, erros.log): a caixa de senha
/// fechou no Enter, o caixa abriu a pergunta seguinte do TEF (ponto de captura) e o WPF
/// caiu com NullReferenceException em System.Windows.Input.TextServicesContext.Keystroke.
/// O Enter ainda estava em voo (KeyUp e WM_CHAR por entregar) quando a janela nova nasceu,
/// e dentro dela o TabTip (teclado virtual) era aberto com ShellExecuteEx NA thread da
/// tela. O mesmo erro tinha aparecido no menu ADM do PayGo mais cedo.
///
/// Quatro regras, cada uma com o seu porque no fonte:
///   1. a pergunta seguinte so abre com prioridade Background (abaixo de Input);
///   2. o TabTip abre num worker e so com a janela ociosa, nunca no GotFocus;
///   3. a caixa de senha fecha fora do KeyDown, com a tecla marcada como tratada;
///   4. o guarda global nao abre MessageBox para essa excecao: so registra.
/// Este teste le os fontes e reprova quem desfizer qualquer uma delas.
/// </summary>
public static class TestesTeclaEntreDialogos
{
    public static void Rodar(Action<bool, string> checar)
    {
        var raiz = AcharRaiz();
        checar(raiz is not null, "achei a raiz do repositorio");
        if (raiz is null) return;

        // ── 1. a pergunta do TEF abre depois de o teclado esvaziar ──────────
        var servicos = File.ReadAllText(Path.Combine(raiz, "Servicos.cs"));
        var naUi = Trecho(servicos, "private static Task<T> NaUiAsync<T>(Func<T> acao)", "\n    }");
        checar(naUi.Contains("DispatcherPriority.Background", StringComparison.Ordinal),
            "NaUiAsync agenda com DispatcherPriority.Background: o KeyUp do Enter anterior e entregue antes de a janela nova existir");
        checar(naUi.Contains("CheckAccess()", StringComparison.Ordinal),
            "e na propria thread da tela continua executando direto");

        // ── 2. o teclado virtual: worker, janela ociosa, nunca no GotFocus ───
        var pedirValor = File.ReadAllText(Path.Combine(raiz, "Telas", "PedirValor.cs"));
        var pedirTexto = Trecho(pedirValor, "public static class PedirTexto", "\n}");
        checar(!Regex.IsMatch(pedirTexto, @"GotFocus\s*\+=\s*\(_,\s*_\)\s*=>\s*AbrirTecladoVirtual"),
            "PedirTexto nao abre o TabTip no GotFocus (era o ShellExecuteEx na thread da tela, dentro do Loaded)");
        checar(pedirTexto.Contains("DispatcherPriority.ApplicationIdle", StringComparison.Ordinal),
            "o TabTip so e pedido com a janela ociosa");
        var abrir = Trecho(pedirTexto, "private static void AbrirTecladoVirtual()", "\n    }");
        checar(abrir.Contains("Task.Run", StringComparison.Ordinal),
            "e o Process.Start roda num worker: o .NET abre a propria thread STA para o ShellExecuteEx");
        // O TabTip fica residente com o teclado escondido; e o Start de novo que o traz de
        // volta. Um "ja esta aberto" por processo deixaria o caixa touch sem teclado a partir
        // da segunda vez (revisao de 09/09/2026, reproduzido nesta maquina com tres TabTip vivos).
        checar(!abrir.Contains("GetProcessesByName", StringComparison.Ordinal),
            "e NAO deixa de chamar o Start por ja existir um TabTip.exe: e o Start que mostra o teclado");

        // ── 3. a caixa de senha fecha fora da tecla ─────────────────────────
        var pedirSenha = File.ReadAllText(Path.Combine(raiz, "Telas", "PedirSenha.cs"));
        var keyDown = Trecho(pedirSenha, "caixa.KeyDown += (_, e) =>", "\n        };");
        checar(keyDown.Contains("e.Handled = true", StringComparison.Ordinal),
            "a tecla e marcada como tratada: o '\\r' nao sobra para a proxima janela");
        checar(keyDown.Contains("BeginInvoke", StringComparison.Ordinal) && !keyDown.Contains("janela.Close();", StringComparison.Ordinal),
            "e o Close vai para a fila do Dispatcher, nunca dentro do KeyDown");

        // ── 4. o guarda global so registra essa excecao ─────────────────────
        var app = File.ReadAllText(Path.Combine(raiz, "App.xaml.cs"));
        var guarda = Trecho(app, "DispatcherUnhandledException += (_, args) =>", "\n        };");
        var registra = guarda.IndexOf("Registrar(\"tela\"", StringComparison.Ordinal);
        var desvio = guarda.IndexOf("TeclaPerdidaDoWindows(args.Exception)", StringComparison.Ordinal);
        var caixa = guarda.IndexOf("MessageBox.Show(", StringComparison.Ordinal);
        checar(registra >= 0 && desvio > registra && caixa > desvio,
            "o guarda registra, desvia a tecla perdida do Windows e so entao abre a MessageBox para o resto");
        checar(app.Contains("TextServicesContext.Keystroke", StringComparison.Ordinal),
            "e o desvio e pela pilha exata (TextServicesContext.Keystroke), nao por toda NullReferenceException");
        checar(guarda.Contains("args.Handled = true", StringComparison.Ordinal),
            "e continua segurando o caixa de pe em todos os casos");
    }

    private static string Trecho(string texto, string inicio, string fim)
    {
        var i = texto.IndexOf(inicio, StringComparison.Ordinal);
        if (i < 0) return "";
        var j = texto.IndexOf(fim, i + inicio.Length, StringComparison.Ordinal);
        return j < 0 ? texto[i..] : texto[i..(j + fim.Length)];
    }

    private static string? AcharRaiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "Temas"))
                && Directory.Exists(Path.Combine(dir.FullName, "Telas"))
                && File.Exists(Path.Combine(dir.FullName, "Servicos.cs")))
                return dir.FullName;
        return null;
    }
}
