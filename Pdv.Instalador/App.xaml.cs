using System.IO;
using System.Windows;

namespace Pdv.Instalador;

public partial class App : Application
{
    /// <summary>
    /// O PRÓPRIO EXE, PRESO ENQUANTO O INSTALADOR VIVE (fechamento da 1.0.36, revisão de segurança). Na
    /// atualização este exe mora no TEMP do usuário, e o pacote é lido dele DEPOIS de esperar o caixa
    /// (até 30 s). O Windows deixa renomear um exe em execução: sem este handle, um programa do usuário
    /// trocava o arquivo nesse meio e o pacote dele ia para Program Files. Só leitura e FileShare.Read:
    /// ninguém grava, apaga nem renomeia, e as leituras do próprio pacote continuam passando.
    /// </summary>
    private static FileStream? _euMesmo;

    /// <summary>
    /// Um modo de linha de comando: <c>--desinstalar</c>, que remove o programa
    /// PRESERVANDO os dados da loja. Ele não abre a janela de instalação.
    ///
    /// O passo que monta o pacote (pendurar a pasta do PDV e o paygo.exe na cauda de
    /// uma cópia deste exe) NÃO mora aqui, embora o código do formato more — em
    /// <see cref="Pacote"/>. Motivo prático: este exe tem requireAdministrator no
    /// manifesto, e o Windows recusa iniciá-lo de um shell comum. Empacotar é passo de
    /// build e não pode exigir UAC de quem compila; por isso a chamada vive no
    /// Pdv.Testes, que compila o MESMO Pacote.cs e não pede elevação.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        try { if (Environment.ProcessPath is { } eu) _euMesmo = new FileStream(eu, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch { /* sem a trava o instalador ainda funciona */ }

        // 03/09: o caixa chama o instalador com --atualizar. Sem tela: troca o
        // programa na pasta de sempre e reabre o caixa. O dono viu o assistente
        // completo abrir numa atualizacao e, com razao, quis so a troca.
        if (e.Args.Length > 0 && e.Args[0] == Pdv.Nucleo.ReabrirOCaixa.ArgumentoAtualizar)
        {
            var falha = AtualizarSilencioso(out var pastaDoCaixa, out var mexeu);
            if (falha is not null)
                MessageBox.Show(Instalacao.FraseDaFalha(falha, mexeu),
                    "Atualizar o caixa", MessageBoxButton.OK, MessageBoxImage.Error);
            // 1.0.36: o caixa novo deixa um cmd comum esperando este instalador sair e se reabre
            // sozinho, sem administrador (--sem-abrir). Caixa antigo: reabre aqui, ONDE o caixa está
            // (a Savassi mora em "PDV MMTech") e sem herdar o administrador deste instalador; sem a
            // área de trabalho para isso (quiosque), pergunta se reinicia o computador.
            // Fechamento da 1.0.36: reabre também depois de uma falha (antes, no quiosque, ficava a tela
            // preta só com o erro), e o Sim reinicia (antes era só um OK e a tela preta).
            var exe = pastaDoCaixa is null ? null : Path.Combine(pastaDoCaixa, "Pdv.exe");
            if (Pdv.Nucleo.ReabrirOCaixa.InstaladorAbre(e.Args) && exe is not null && File.Exists(exe)
                && !Instalacao.AbrirCaixa(exe))
                PedirParaReiniciar(falha is null ? Instalacao.ReinicieParaAbrir : Instalacao.ReinicieDepoisDaFalha);
            Shutdown(falha is null ? 0 : 1);
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--desinstalar")
        {
            // 14/09/2026, Castelo: desinstalar e instalar de novo trazia o caixa "já logado", porque os
            // dados ficam. Continuam ficando por padrão (Enter e Esc caem no Não); quem quer começar do
            // zero diz Sim, vê o que ainda não subiu, e a pasta sai do lugar guardada com a data.
            var apagarDados = PerguntarApagarDados();
            if (apagarDados && Instalacao.PastaInstalada() is { } instalada)
                Instalacao.PararAgente(Instalacao.PastaDoAgente(instalada));
            // 1.0.36: Desinstalar desfaz antes o quiosque do usuário do caixa (Shell e trava do
            // Ctrl+Alt+Del), senão a próxima entrada no Windows seria uma tela preta sem saída. Um
            // Shell nosso que não sai segura a desinstalação (erro, e nada é apagado).
            var erro = Instalacao.Desinstalar(out var quiosque);
            var avisoQuiosque = Pdv.Nucleo.DesfazerQuiosque.Aviso(quiosque);
            string? destino = null, erroDados = null;
            if (erro is null && apagarDados)
                erroDados = Instalacao.ApartarDados(Instalacao.PastaDados, DateTime.Now, out destino);
            MessageBox.Show(
                erro is not null ? "Não consegui remover: " + erro + avisoQuiosque
                : !apagarDados ? "Caixa removido desta máquina. As vendas e a configuração da loja "
                    + "foram PRESERVADAS em C:\\ProgramData\\PdvNativo." + avisoQuiosque
                : erroDados is not null ? "Caixa removido desta máquina, mas os dados continuam no lugar. " + erroDados + avisoQuiosque
                : $"Caixa removido desta máquina. Os dados foram guardados em {destino}. A próxima instalação começa do zero." + avisoQuiosque,
                "Remover o caixa", MessageBoxButton.OK,
                erro is null && erroDados is null ? MessageBoxImage.Information : MessageBoxImage.Error);
            Shutdown(erro is null ? 0 : 1);
            return;
        }

        base.OnStartup(e);
    }

    /// <summary>
    /// Mesma instalação do assistente, sem o assistente: extrai o pacote, copia por
    /// cima da pasta de sempre (com a conferência de que o programa abre) e, se o
    /// PayGo já está na máquina, prepara a pasta de troca. Não mexe no PayGo em
    /// atualização: o assistente dele é uma janela, e aqui não pode haver janela.
    /// </summary>
    private static string? AtualizarSilencioso(out string? pasta, out bool mexeu)
    {
        mexeu = false;
        // ATUALIZA ONDE O CAIXA ESTÁ, não onde ele nasceria hoje. A loja instalada como
        // "PDV MMTech" tem o programa em Program Files\PDV MMTech; procurar só a pasta do
        // nome novo (MMFood) faria a atualização morrer com "não está instalado".
        var pastaAtual = Instalacao.PastaInstalada();
        pasta = pastaAtual;
        if (pastaAtual is null)
            return "o caixa não está instalado nesta máquina";
        // O caixa se fecha logo depois de nos chamar; sem o assistente no meio, a
        // cópia podia começar antes de ele sair. Espera até 30 s pelo processo.
        // 1.0.35 (Savassi, 14/10/2026): e os navegadores do chat e do WhatsApp dele também (até 10 s;
        // os que ficarem são encerrados). Um navegador do caixa anterior vivo no perfil faz o WebView2
        // recusar o caixa novo quando as opções mudaram. Tudo anotado em atualizacao.txt.
        // Fechamento da 1.0.36: com o caixa AINDA aberto passados os 30 s, nenhum navegador é encerrado
        // (são do caixa que segue vendendo); só fica anotado.
        foreach (var linha in Instalacao.EsperarOCaixaAnteriorSair(Instalacao.CaixaAberto, TimeSpan.FromSeconds(30),
                     Instalacao.PastaDados, TimeSpan.FromSeconds(10)))
            Instalacao.AnotarTroca(linha);
        string? temporaria = null;
        try
        {
            var origem = Instalacao.AcharOrigemAoLado();
            if (Pacote.TemPayload())
            {
                // Fechamento da 1.0.36: extrai dentro da pasta do caixa (só administrador grava), e não no
                // TEMP do usuário (ver Instalacao.PastaDeExtracao).
                Instalacao.LimparExtracoesAntigas(pastaAtual);
                temporaria = Instalacao.PastaDeExtracao(pastaAtual);
                var falha = Pacote.Extrair(temporaria, _ => { });
                if (falha is not null) return falha;
                origem = Path.Combine(temporaria, "pdv");
            }
            var erro = Instalacao.Instalar(new Instalacao.Opcoes(
                OrigemPasta: origem ?? "",
                PastaDestino: pastaAtual,
                IniciarComWindows: true,
                AtalhoAreaTrabalho: true), null, out mexeu);
            if (erro is not null) return erro;
            try { if (PayGo.Detectar().Instalado) PayGo.PrepararPastaTroca(); } catch { /* TEF fica como está */ }
            // O COMPONENTE DA MICROSOFT DO CHAT E DO WHATSAPP (15/09/2026, Castelo). O caixa se
            // atualiza por aqui, sem a janela, e só a janela punha o componente: o PC do Castelo
            // continuava sem ele a cada versão nova. Com ele já na máquina a conferência é
            // instantânea. Faltando, prazos curtos (o caixa está fechado) e nada de esperar depois
            // do prazo; o instalador da Microsoft segue sozinho. Falha não desfaz a atualização.
            var componente = WebView2Runtime.GarantirNaAtualizacao(WebView2Runtime.PassosDaAtualizacao(), Path.GetTempPath());
            WebView2Runtime.Anotar("atualizacao", componente);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
        finally
        {
            if (temporaria is not null)
                try { Directory.Delete(temporaria, true); } catch { /* temporário */ }
        }
    }

    /// <summary>
    /// O caixa não abriu sem administrador (quiosque com um caixa antigo chamando): pergunta se reinicia o
    /// computador agora, com o Sim como resposta padrão. Sim reinicia; Não, ou o reinício que não sai,
    /// diz como reiniciar sem a área de trabalho (fechamento da 1.0.36).
    /// </summary>
    private static void PedirParaReiniciar(string pergunta)
    {
        var sim = MessageBox.Show(pergunta, "Atualizar o caixa", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (sim == MessageBoxResult.Yes && Instalacao.Reiniciar()) return;
        MessageBox.Show(Instalacao.ComoReiniciar, "Atualizar o caixa", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// "Apagar também os dados deste caixa?" com o Não como resposta padrão. Se há algo que ainda
    /// não subiu para o painel (ou não deu para conferir), pergunta de novo dizendo quanto.
    /// </summary>
    internal static bool PerguntarApagarDados(Window? dono = null)
    {
        var dados = Instalacao.ConferirDados(Instalacao.PastaDados);
        if (!dados.Existe) return false;
        var sim = dono is null
            ? MessageBox.Show(Instalacao.PerguntaApagarDados, "Remover o caixa", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(dono, Instalacao.PerguntaApagarDados, "Começar do zero", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (sim != MessageBoxResult.Yes) return false;
        // Apagar os dados é ação de admin (15/09/2026): com usuário master guardado, só a senha dele.
        if (Instalacao.LerMaster(Instalacao.PastaDados) is { } master)
        {
            var senha = PedirSenhaMaster(dono, master.Nome);
            if (senha is null) return false;
            if (!Instalacao.MasterLibera(master, senha))
            {
                const string recusa = "A senha do usuário master não confere. Os dados continuam no lugar.";
                if (dono is null) MessageBox.Show(recusa, "Remover o caixa", MessageBoxButton.OK, MessageBoxImage.Error);
                else MessageBox.Show(dono, recusa, "Começar do zero", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
        if (Instalacao.AvisoAntesDeApagar(dados) is not { } aviso) return true;
        var mesmoAssim = dono is null
            ? MessageBox.Show(aviso, "Remover o caixa", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(dono, aviso, "Começar do zero", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return mesmoAssim == MessageBoxResult.Yes;
    }

    /// <summary>A senha do usuário master, numa janela simples. Null = voltou sem digitar.</summary>
    private static string? PedirSenhaMaster(Window? dono, string nome)
    {
        string? resultado = null;
        var janela = new Window
        {
            Title = "Usuário master",
            Width = 400,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = dono is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        if (dono is not null) janela.Owner = dono;
        var pilha = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };
        pilha.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = string.IsNullOrWhiteSpace(nome)
                ? "Para apagar os dados deste caixa, digite a senha do usuário master da rede."
                : $"Para apagar os dados deste caixa, digite a senha do usuário master da rede ({nome}).",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        var caixa = new System.Windows.Controls.PasswordBox { FontSize = 18, Padding = new Thickness(8) };
        pilha.Children.Add(caixa);
        var botoes = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var voltar = new System.Windows.Controls.Button { Content = "Voltar", IsCancel = true, MinWidth = 90, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
        var confirmar = new System.Windows.Controls.Button { Content = "Confirmar", IsDefault = true, MinWidth = 90, Padding = new Thickness(10, 6, 10, 6) };
        confirmar.Click += (_, _) => { resultado = caixa.Password; janela.Close(); };
        botoes.Children.Add(voltar);
        botoes.Children.Add(confirmar);
        pilha.Children.Add(botoes);
        janela.Content = pilha;
        janela.Loaded += (_, _) => caixa.Focus();
        janela.ShowDialog();
        return resultado;
    }

}
