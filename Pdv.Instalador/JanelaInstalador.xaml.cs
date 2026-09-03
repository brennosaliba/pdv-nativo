using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Pdv.Instalador;

/// <summary>
/// As três telas da instalação: o que vai acontecer → acontecendo → pronto.
///
/// Quem está do outro lado é o dono de uma loja, sozinho, uma vez na vida daquela
/// máquina. Duas regras guiam tudo aqui:
///  · nunca deixar a tela parada sem dizer o que está acontecendo (165 MB copiando em
///    silêncio é indistinguível de travamento, e a reação natural é fechar a janela);
///  · nenhum passo termina em beco: se o PayGo não instalar sozinho, a mesma tela
///    oferece o caminho manual em vez de mandar procurar o arquivo.
/// </summary>
public partial class JanelaInstalador : Window
{
    private enum Etapa { Inicio, Trabalhando, Fim }

    private Etapa _etapa = Etapa.Inicio;
    private string? _pastaTemporaria;
    private string? _paygoExe;
    private bool _atualizacao;

    public JanelaInstalador()
    {
        InitializeComponent();

        _atualizacao = File.Exists(Path.Combine(Instalacao.PastaDestinoPadrao, "Pdv.exe"));
        var noPacote = Pacote.TemPayload();
        var aoLado = Instalacao.AcharOrigemAoLado();

        TxtPasso.Text = _atualizacao ? "ATUALIZAÇÃO" : "INSTALAÇÃO";
        TxtTitulo.Text = _atualizacao ? "Atualizar o caixa" : "Instalar o caixa";
        TxtIntro.Text = _atualizacao
            ? "Esta máquina já tem o caixa instalado. Atualizar troca só o programa: "
            + "as vendas, o caixa aberto e a configuração da loja continuam exatamente como estão."
            : "Em poucos minutos esta máquina vira uma frente de caixa. Três coisas vão acontecer:";

        if (!noPacote && Instalacao.ConferirOrigem(aoLado) is { } problema)
        {
            // Sem programa não há o que instalar. Falar isso AGORA, com o botão
            // desligado, é melhor do que deixar clicar e falhar no meio.
            TxtTitulo.Text = "Este instalador está incompleto";
            TxtIntro.Text = problema;
            BtnPrincipal.IsEnabled = false;
            return;
        }

        _paygoExe = AcharPayGoAoLado();
        if (!noPacote && _paygoExe is null)
            TxtItemPayGo.Text = "2.  (O PayGo, que fala com a maquininha, não veio junto. Dá para instalar depois.)";

        var versao = aoLado is not null && File.Exists(Path.Combine(aoLado, "Pdv.exe"))
            ? FileVersionInfo.GetVersionInfo(Path.Combine(aoLado, "Pdv.exe")).FileVersion
            : FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? "").FileVersion;
        TxtVersao.Text = $"versão {versao ?? "?"}";

        BtnPrincipal.Content = _atualizacao ? "Atualizar" : "Instalar";
    }

    /// <summary>O paygo.exe viaja ao lado quando o instalador não é empacotado.</summary>
    private static string? AcharPayGoAoLado()
    {
        var aqui = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        var c = Path.Combine(aqui, "paygo.exe");
        return File.Exists(c) ? c : null;
    }

    private async void Avancar(object sender, RoutedEventArgs e)
    {
        switch (_etapa)
        {
            case Etapa.Inicio:
                await InstalarTudo();
                break;
            case Etapa.Fim:
                AbrirPdvEFechar();
                break;
        }
    }

    private async Task InstalarTudo()
    {
        _etapa = Etapa.Trabalhando;
        PainelInicio.Visibility = Visibility.Collapsed;
        PainelProgresso.Visibility = Visibility.Visible;
        BtnPrincipal.IsEnabled = false;
        BtnPrincipal.Content = _atualizacao ? "Atualizando…" : "Instalando…";
        TxtErro.Visibility = Visibility.Collapsed;
        TxtPasso.Text = "PASSO 1 DE 2";
        TxtTitulo.Text = "Instalando o caixa";

        var iniciar = ChkIniciar.IsChecked == true;
        var atalho = ChkAtalho.IsChecked == true;
        void Progresso(string p) => Dispatcher.Invoke(() => TxtProgresso.Text = p);

        // ---- o programa
        var erro = await Task.Run(() =>
        {
            var origem = Instalacao.AcharOrigemAoLado();
            if (Pacote.TemPayload())
            {
                _pastaTemporaria = Path.Combine(Path.GetTempPath(),
                    "pdv-instalador-" + Guid.NewGuid().ToString("N")[..8]);
                var falha = Pacote.Extrair(_pastaTemporaria, Progresso);
                if (falha is not null) return falha;
                origem = Path.Combine(_pastaTemporaria, "pdv");
                var pg = Path.Combine(_pastaTemporaria, "paygo.exe");
                if (File.Exists(pg)) _paygoExe = pg;
            }

            return Instalacao.Instalar(new Instalacao.Opcoes(
                OrigemPasta: origem ?? "",
                PastaDestino: Instalacao.PastaDestinoPadrao,
                IniciarComWindows: iniciar,
                AtalhoAreaTrabalho: atalho), Progresso);
        });

        if (erro is not null) { Falhou(erro); return; }

        // ---- o PayGo
        //
        // O caixa JÁ ESTÁ INSTALADO neste ponto, e essa ordem é a regra: nada que
        // aconteça com o PayGo daqui para frente pode desfazer o que já deu certo.
        // Se o assistente da Setis falhar, for fechado no meio ou nem abrir, o dono
        // termina com um caixa funcionando — sem cartão, que se resolve depois.
        TxtPasso.Text = "PASSO 2 DE 2";
        TxtTitulo.Text = "Agora o PayGo";
        var acao = PayGo.Decidir(
            arquivoPresente: _paygoExe is not null,
            jaInstalado: PayGo.Detectar().Instalado,
            tefRodando: PayGo.TefRodando());

        Progresso(PayGo.Explicar(acao));
        string? avisoPayGo = null;

        if (acao == PayGo.Acao.Instalar)
        {
            avisoPayGo = PayGo.Instalar(_paygoExe!)
                ?? "O assistente do PayGo abriu numa janela separada. Siga até o fim dele. "
                 + "Se você fechar sem terminar, o caixa continua funcionando: só o cartão fica para depois.";
        }
        else if (acao == PayGo.Acao.JaInstalado)
        {
            // As pastas de troca com o PDV ainda valem a garantia de escrita, e
            // criá-las aqui, com o instalador elevado, é de graça.
            await Task.Run(PayGo.PrepararPastaTroca);
        }
        else
        {
            avisoPayGo = PayGo.Explicar(acao);
        }

        Concluiu(avisoPayGo);
    }

    /// <summary>
    /// Fim de linha. O botão leva ao PDV, que abre direto na configuração da loja
    /// quando ainda não há terminal configurado — é o "tela por tela" que o dono pediu,
    /// e é por isso que o instalador não repete esses campos aqui.
    /// </summary>
    private void Concluiu(string? aviso)
    {
        _etapa = Etapa.Fim;
        LimparTemporaria();

        PainelProgresso.Visibility = Visibility.Collapsed;
        PainelFim.Visibility = Visibility.Visible;
        TxtPasso.Text = "PRONTO";
        TxtTitulo.Text = _atualizacao ? "Caixa atualizado" : "Caixa instalado";
        TxtResumo.Text = _atualizacao
            ? "O programa foi trocado. As vendas e a configuração da loja continuam onde estavam."
            : "Falta só configurar a loja: dados, nota fiscal, impressora e maquininha. "
            + "O caixa abre já nessa tela e vai passo a passo.";

        if (aviso is not null)
        {
            CaixaAviso.Visibility = Visibility.Visible;
            TxtAviso.Text = aviso;
            // O caminho manual só faz sentido se o arquivo existir para ser aberto.
            if (_paygoExe is not null && !PayGo.Detectar().Instalado)
                BtnPayGoVisivel.Visibility = Visibility.Visible;
        }

        BtnPrincipal.Content = "Abrir o caixa e configurar a loja";
        BtnPrincipal.IsEnabled = true;
    }

    private void Falhou(string erro)
    {
        _etapa = Etapa.Inicio;
        LimparTemporaria();
        PainelProgresso.Visibility = Visibility.Collapsed;
        PainelInicio.Visibility = Visibility.Visible;
        TxtPasso.Text = "NÃO DEU";
        TxtTitulo.Text = "A instalação não terminou";
        TxtErro.Text = erro;
        TxtErro.Visibility = Visibility.Visible;
        BtnPrincipal.Content = "Tentar de novo";
        BtnPrincipal.IsEnabled = true;
    }

    /// <summary>Segunda chance: o dono fechou o assistente do PayGo sem querer, ou
    /// fechou o TEF que estava bloqueando e agora quer seguir.</summary>
    private void InstalarPayGoVisivel(object sender, RoutedEventArgs e)
    {
        if (_paygoExe is null) return;
        if (PayGo.TefRodando())
        {
            TxtErro.Text = PayGo.Explicar(PayGo.Acao.FecharTefPrimeiro);
            TxtErro.Visibility = Visibility.Visible;
            return;
        }
        var erro = PayGo.Instalar(_paygoExe);
        if (erro is not null) { TxtErro.Text = erro; TxtErro.Visibility = Visibility.Visible; return; }
        TxtErro.Visibility = Visibility.Collapsed;
        TxtAviso.Text = "O assistente do PayGo abriu numa janela separada. Siga até o fim dele.";
        BtnPayGoVisivel.IsEnabled = false;
    }

    private void AbrirPdvEFechar()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Instalacao.PastaDestinoPadrao, "Pdv.exe"),
                WorkingDirectory = Instalacao.PastaDestinoPadrao,
                UseShellExecute = true,
            });
        }
        catch { /* o atalho e o menu do Windows continuam lá */ }
        Close();
    }

    /// <summary>O payload extraído são ~265 MB no TEMP. Deixar para trás enche o disco
    /// de um PC de loja, que costuma ser pequeno.</summary>
    private void LimparTemporaria()
    {
        if (_pastaTemporaria is null) return;
        try { if (Directory.Exists(_pastaTemporaria)) Directory.Delete(_pastaTemporaria, recursive: true); }
        catch { }
        _pastaTemporaria = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        LimparTemporaria();
        base.OnClosed(e);
    }
}
