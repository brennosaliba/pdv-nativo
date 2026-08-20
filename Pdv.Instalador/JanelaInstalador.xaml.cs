using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Pdv.Instalador;

public partial class JanelaInstalador : Window
{
    public JanelaInstalador()
    {
        InitializeComponent();
        var pdv = Instalacao.AcharPdvExeAoLado();
        if (pdv is null)
        {
            TxtVersao.Text = "Pdv.exe NÃO encontrado ao lado do instalador";
            TxtErro.Text = "Coloque este InstalarPdv.exe na MESMA pasta do Pdv.exe e abra de novo. " +
                           "É assim que a versão certa viaja junto — sem download no meio da loja.";
            BtnInstalar.IsEnabled = false;
            return;
        }
        var v = FileVersionInfo.GetVersionInfo(pdv).FileVersion ?? "?";
        var atualizacao = File.Exists(Path.Combine(Instalacao.PastaDestinoPadrao, "Pdv.exe"));
        TxtVersao.Text = $"versão {v}" + (atualizacao ? "  ·  ATUALIZAÇÃO (dados preservados)" : "  ·  instalação nova");
        if (atualizacao) BtnInstalar.Content = "Atualizar";
    }

    private async void Instalar(object sender, RoutedEventArgs e)
    {
        BtnInstalar.IsEnabled = false;
        TxtErro.Text = "";
        var opcoes = new Instalacao.Opcoes(
            Instalacao.AcharPdvExeAoLado()!,
            Instalacao.PastaDestinoPadrao,
            ChkIniciar.IsChecked == true,
            ChkAtalho.IsChecked == true);

        var erro = await Task.Run(() => Instalacao.Instalar(opcoes,
            p => Dispatcher.Invoke(() => TxtProgresso.Text = p)));

        if (erro is null)
        {
            TxtProgresso.Text = "✓ Instalado. O PDV abre no próximo início do Windows" +
                (ChkIniciar.IsChecked == true ? " automaticamente." : ".");
            BtnInstalar.Content = "Abrir o PDV agora";
            BtnInstalar.IsEnabled = true;
            BtnInstalar.Click -= Instalar;
            BtnInstalar.Click += (_, _) =>
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Instalacao.PastaDestinoPadrao, "Pdv.exe"),
                    UseShellExecute = true,
                });
                Close();
            };
        }
        else
        {
            TxtErro.Text = "Não instalou: " + erro;
            BtnInstalar.IsEnabled = true;
        }
    }
}
