using System.Windows;

namespace Pdv.Instalador;

public partial class App : Application
{
    /// <summary>--desinstalar remove o programa (preservando os DADOS) sem abrir janela.</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && e.Args[0] == "--desinstalar")
        {
            var erro = Instalacao.Desinstalar();
            MessageBox.Show(
                erro is null
                    ? "PDV removido. Os dados da loja (banco e configuração) foram PRESERVADOS em C:\\ProgramData\\PdvNativo."
                    : "Falha ao remover: " + erro,
                "Desinstalar PDV", MessageBoxButton.OK,
                erro is null ? MessageBoxImage.Information : MessageBoxImage.Error);
            Shutdown(erro is null ? 0 : 1);
            return;
        }
        base.OnStartup(e);
    }
}
