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
}
