using System.IO;
using System.Windows;

namespace Pdv.Instalador;

public partial class App : Application
{
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
        // 03/09: o caixa chama o instalador com --atualizar. Sem tela: troca o
        // programa na pasta de sempre e reabre o caixa. O dono viu o assistente
        // completo abrir numa atualizacao e, com razao, quis so a troca.
        if (e.Args.Length > 0 && e.Args[0] == "--atualizar")
        {
            var falha = AtualizarSilencioso();
            if (falha is not null)
                MessageBox.Show("Não consegui atualizar: " + falha + "\nO caixa continua na versão anterior.",
                    "Atualizar o caixa", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                AbrirCaixa();
            Shutdown(falha is null ? 0 : 1);
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--desinstalar")
        {
            var erro = Instalacao.Desinstalar();
            MessageBox.Show(
                erro is null
                    ? "Caixa removido desta máquina. As vendas e a configuração da loja "
                    + "foram PRESERVADAS em C:\\ProgramData\\PdvNativo."
                    : "Não consegui remover: " + erro,
                "Remover o caixa", MessageBoxButton.OK,
                erro is null ? MessageBoxImage.Information : MessageBoxImage.Error);
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
    private static string? AtualizarSilencioso()
    {
        if (!File.Exists(Path.Combine(Instalacao.PastaDestinoPadrao, "Pdv.exe")))
            return "o caixa não está instalado nesta máquina";
        string? temporaria = null;
        try
        {
            var origem = Instalacao.AcharOrigemAoLado();
            if (Pacote.TemPayload())
            {
                temporaria = Path.Combine(Path.GetTempPath(), "pdv-atualizar-" + Guid.NewGuid().ToString("N")[..8]);
                var falha = Pacote.Extrair(temporaria, _ => { });
                if (falha is not null) return falha;
                origem = Path.Combine(temporaria, "pdv");
            }
            var erro = Instalacao.Instalar(new Instalacao.Opcoes(
                OrigemPasta: origem ?? "",
                PastaDestino: Instalacao.PastaDestinoPadrao,
                IniciarComWindows: true,
                AtalhoAreaTrabalho: true), null);
            if (erro is not null) return erro;
            try { if (PayGo.Detectar().Instalado) PayGo.PrepararPastaTroca(); } catch { /* TEF fica como está */ }
            return null;
        }
        catch (Exception ex) { return ex.Message; }
        finally
        {
            if (temporaria is not null)
                try { Directory.Delete(temporaria, true); } catch { /* temporário */ }
        }
    }

    private static void AbrirCaixa()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Instalacao.PastaDestinoPadrao, "Pdv.exe"),
                WorkingDirectory = Instalacao.PastaDestinoPadrao,
                UseShellExecute = true,
            });
        }
        catch { /* o atalho e o menu do Windows continuam lá */ }
    }
}
