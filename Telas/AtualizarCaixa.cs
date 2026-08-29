using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// O botão "Atualizar" da barra — a parte que mexe na máquina.
///
/// A DECISÃO toda (comparar versão, validar o manifesto, recusar, montar o texto)
/// mora em <see cref="Nucleo.Atualizacao"/>, onde a suíte alcança. Aqui fica só o que
/// só existe com WPF na frente: ler o estado do caixa, mostrar o progresso e ENTREGAR
/// o instalador.
///
/// A entrega é a parte que precisa ser dita em voz alta: este arquivo NÃO instala
/// nada. Ele baixa o InstalarPdv.exe para o TEMP, prova que o arquivo é o que devia
/// ser, chama o instalador (que sobe o UAC sozinho pelo manifesto dele) e fecha o
/// PDV. Quem troca arquivo em uso, preserva C:\ProgramData e confere que o caixa ABRE
/// antes de gravar registro e atalho é o instalador — código que já existe, já foi
/// testado e não pode ter uma segunda versão morando aqui.
/// </summary>
public static class AtualizarCaixa
{
    /// <summary>
    /// HttpClient próprio e de vida longa. Não é o do Fiscal de propósito: aquele tem
    /// timeout infinito (a SEFAZ demora) e handler afinado para nota fiscal. Baixar
    /// 265 MB e emitir nota não têm nada em comum além do protocolo.
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Onde perguntar. Config por loja (`atualizacao_url`) com o servidor da
    /// MMTech como padrão — o mesmo executável atende clientes diferentes.</summary>
    public static string UrlDoManifesto()
    {
        try
        {
            using var cx = Banco.Abrir();
            var u = Vendas.Config(cx, "atualizacao_url");
            return string.IsNullOrWhiteSpace(u) ? Atualizacao.UrlPadrao : u!.Trim();
        }
        catch { return Atualizacao.UrlPadrao; }
    }

    // ── O QUE O CAIXA ESTÁ VIVENDO ────────────────────────────────────────────

    /// <summary>
    /// Junta o estado que decide se dá para atualizar agora. A comanda e a maquininha
    /// vêm da TELA (só ela sabe); o resto vem do banco e do spooler.
    /// </summary>
    public static Atualizacao.EstadoDoCaixa EstadoAgora(int itensNaComanda, bool maquininhaOcupada)
    {
        var cobrancas = 0;
        var caixaAberto = false;
        var vendas = 0;
        try
        {
            using var cx = Banco.Abrir();
            // 'criando'/'aguardando' = o pinpad está com o cliente AGORA (ou o PDV
            // morreu no meio de uma cobrança e ninguém reconciliou). Fechar o caixa
            // por cima disso é como uma cobrança fica órfã: o cliente pagou e a venda
            // não existe aqui. É a mesma leitura que o religamento do TEF usa no boot.
            cobrancas = cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM tef_transacao WHERE situacao IN ('criando','aguardando')");
            caixaAberto = Caixa.SessaoAberta(cx) is not null;
        }
        catch { /* banco indisponível: os outros portões continuam valendo */ }

        try { vendas = Sincronizacao.VendasNaoEntregues().Total; } catch { vendas = 0; }

        return new Atualizacao.EstadoDoCaixa(
            ItensNaComanda: itensNaComanda,
            MaquininhaOcupada: maquininhaOcupada,
            CobrancasNoPinpad: cobrancas,
            PapeisNaFila: PapeisNaFila(),
            CaixaAberto: caixaAberto,
            VendasPorSubir: vendas);
    }

    /// <summary>
    /// Papéis esperando nas filas que este caixa usa (cupom e comanda). -1 = não deu
    /// para ler.
    ///
    /// ⚠️ Isto lê o SPOOLER DO WINDOWS, que é a fila DEPOIS que o trabalho saiu do PDV.
    /// A fila de DENTRO do processo (o semáforo da bobina em Impressao) não é visível
    /// daqui — está privada, e o arquivo está sendo mexido por outra frente. Na prática
    /// a janela cega é de milissegundos (o trabalho entra no spooler quase junto), e os
    /// outros portões (comanda, pinpad) cobrem o caso que importa. Ver o relatório: o
    /// que fecharia isso é um `Impressao.ImprimindoAgora` público.
    ///
    /// Prazo curto e falha silenciosa de propósito: consultar impressora de rede fora
    /// do ar trava por segundos, e "não consegui ler a fila" não pode virar "não pode
    /// atualizar" — seria o botão morrendo por causa de um driver.
    /// </summary>
    public static int PapeisNaFila()
    {
        var tarefa = Task.Run(() =>
        {
            var nomes = new List<string?>();
            try
            {
                using var cx = Banco.Abrir();
                nomes.Add(Vendas.Config(cx, "impressora"));
                nomes.Add(Vendas.Config(cx, "kds_comanda_impressora"));
            }
            catch { }
            // null/vazio = "padrão do Windows": resolve para o nome real e desduplica,
            // senão a mesma fila é contada duas vezes e vira bloqueio fantasma.
            var padrao = Impressao.ImpressoraPadrao();
            var alvos = nomes
                .Select(n => string.IsNullOrWhiteSpace(n) ? padrao : n!.Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (alvos.Count == 0) return 0;

            var total = 0;
            using var servidor = new LocalPrintServer();
            foreach (var nome in alvos)
            {
                using var fila = servidor.GetPrintQueue(nome);
                fila.Refresh();
                total += fila.NumberOfJobs;
            }
            return total;
        });

        try { return tarefa.Wait(TimeSpan.FromSeconds(2)) ? tarefa.Result : -1; }
        catch { return -1; }
    }

    // ── A CHECAGEM SILENCIOSA (o "tem atualização" do TeamViewer) ─────────────

    /// <summary>
    /// Pergunta ao servidor sem abrir nada na tela. É o que acende o selo no botão:
    /// o dono pediu para o caixa AVISAR, não para alguém ter que ir procurar.
    /// Devolve a versão nova, ou null (em dia, sem rede, servidor fora — tanto faz:
    /// checagem silenciosa que falha tem que falhar em silêncio mesmo).
    /// </summary>
    public static async Task<Atualizacao.Manifesto?> ProcurarNoSilencioAsync()
    {
        try
        {
            var leitura = await Atualizacao.ConsultarAsync(Http, UrlDoManifesto());
            if (leitura.Ok is not { } m) return null;
            return Atualizacao.Comparar(Atualizacao.VersaoInstalada(), m.Versao) < 0 ? m : null;
        }
        catch { return null; }
    }

    // ── O BOTÃO ───────────────────────────────────────────────────────────────

    /// <summary>
    /// O clique inteiro: portão → consulta → confirmação → download → conferência →
    /// portão DE NOVO → entrega ao instalador → o PDV sai de cena.
    ///
    /// Devolve true quando o instalador foi entregue e o PDV está fechando — a tela
    /// não deve fazer mais nada depois disso.
    /// </summary>
    public static async Task<bool> ExecutarAsync(Window dono, int itensNaComanda, bool maquininhaOcupada)
    {
        // 1. PORTÃO PRIMEIRO. Antes da rede, antes de qualquer coisa: se tem cliente no
        //    balcão, a resposta é não, e não interessa se existe versão nova. Perguntar
        //    ao servidor primeiro só serviria para transformar uma recusa clara numa
        //    conversa mais longa com o mesmo fim.
        // Fora da thread da tela: ler a fila do spooler pode custar até 2 s (impressora
        // de rede fora do ar), e travar a UI é como um botão vira "não fez nada".
        var estado = await Task.Run(() => EstadoAgora(itensNaComanda, maquininhaOcupada));
        var leitura = Atualizacao.Impede(estado) != Atualizacao.Impedimento.Nenhum
            ? new Atualizacao.LeituraManifesto(null, null)      // nem consulta: já recusou
            : await Atualizacao.ConsultarAsync(Http, UrlDoManifesto());

        var v = Atualizacao.Decidir(estado, Atualizacao.VersaoInstalada(), leitura);
        if (v.Situacao != Atualizacao.Situacao.Disponivel)
        {
            Dialogo.Avisar(dono, v.Titulo, v.Mensagem,
                v.Situacao == Atualizacao.Situacao.EmDia ? "ok" : "erro");
            return false;
        }

        // 2. O SIM do operador. É aqui que ele fica sabendo que o caixa vai fechar.
        if (!Dialogo.Confirmar(dono, v.Titulo, v.Mensagem, v.TextoSim, v.TextoNao))
        {
            Auditar("atualizacao_recusada", $"{Atualizacao.VersaoInstalada()} → {v.Manifesto!.Versao}"
                + (v.Obrigatoria ? " (obrigatória)" : ""));
            return false;
        }

        // 3. DOWNLOAD. Janela modal com Cancelar que funciona de verdade.
        //
        //    Modal de propósito: enquanto baixa, o caixa não vende. Parece duro e é o
        //    contrário — este é o único momento em que a loja tem a rede inteira para
        //    o download. Deixar vender por baixo colocaria 265 MB disputando a mesma
        //    internet ruim com a autorização da NFC-e e com a cobrança do cartão, que
        //    é como uma venda trava por um motivo que ninguém liga ao botão que
        //    apertaram. Chegou cliente? Cancelar — o pedaço baixado fica guardado e a
        //    próxima tentativa continua de onde parou.
        var (baixa, cancelou) = await BaixarComJanelaAsync(dono, v.Manifesto!);
        if (cancelou) return false;
        if (!baixa.Ok)
        {
            Auditar("atualizacao_falhou", baixa.Erro);
            Dialogo.Avisar(dono, "A atualização não terminou",
                baixa.Erro + "\n\nO caixa NÃO foi alterado: ele continua na versão "
                + Atualizacao.VersaoInstalada() + " e vendendo normalmente.", "erro");
            return false;
        }

        // 4. PORTÃO DE NOVO. Entre o primeiro portão e aqui passaram minutos de
        //    download — tempo de sobra para um cliente chegar no balcão, o operador
        //    bipar um produto e o pinpad estar com o cartão de alguém. O arquivo já
        //    está pronto no TEMP; trocar agora é escolha, não obrigação.
        var agora = await Task.Run(() => EstadoAgora(itensNaComanda, maquininhaOcupada));
        var impede = Atualizacao.Impede(agora);
        if (impede != Atualizacao.Impedimento.Nenhum)
        {
            var (t, msg) = Atualizacao.Explicar(impede, agora);
            Dialogo.Avisar(dono, t,
                "A versão nova já está baixada e guardada neste caixa.\n\n" + msg
                + "\n\nQuando você tocar em Atualizar de novo, a troca é imediata — "
                + "não precisa baixar outra vez.", "erro");
            return false;
        }

        // 5. O PONTO SEM VOLTA, dito com todas as letras. Vale a segunda pergunta:
        //    entre o "sim" lá de cima e este instante passaram minutos, e sumir da
        //    tela sem avisar é diferente de fechar porque alguém mandou.
        if (!Dialogo.Confirmar(dono, "Trocar agora",
                $"A versão {v.Manifesto!.Versao} está baixada e conferida.\n\n"
                + "Ao continuar, o caixa FECHA AGORA e o instalador abre para trocar o "
                + "programa. O Windows vai pedir permissão de administrador.\n\n"
                + "Ao terminar, clique em \"Abrir o caixa\" na tela do instalador e entre "
                + "com o seu PIN.",
                "Trocar agora e fechar", "Ainda não"))
            return false;

        // 6. ENTREGA.
        var erro = EntregarAoInstalador(baixa.Caminho!);
        if (erro is not null)
        {
            Auditar("atualizacao_entrega_falhou", erro);
            Dialogo.Avisar(dono, "Não consegui abrir o instalador",
                erro + "\n\nO caixa continua funcionando normalmente. "
                + "O instalador baixado está em:\n" + baixa.Caminho, "erro");
            return false;
        }

        Auditar("atualizacao_entregue", $"{Atualizacao.VersaoInstalada()} → {v.Manifesto.Versao}");
        // O instalador já está de pé e elevado. Sair AGORA é o que deixa a troca
        // limpa: menos arquivo em uso para renomear, e o teste de abertura que o
        // instalador faz no fim roda contra a máquina sem o PDV velho por cima.
        Application.Current.Shutdown();
        return true;
    }

    /// <summary>
    /// Chama o instalador e devolve null quando ele subiu.
    ///
    /// <c>UseShellExecute = true</c> NÃO é detalhe: o InstalarPdv.exe tem
    /// requireAdministrator no manifesto, e sem o shell o Windows recusa iniciar o
    /// processo em vez de mostrar o UAC. E é justamente por isso que a recusa do UAC
    /// chega aqui como Win32Exception 1223 — que não é falha nenhuma, é o dono dizendo
    /// não. Confundir os dois faria o caixa fechar depois de uma recusa.
    /// </summary>
    public static string? EntregarAoInstalador(string exe)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe)!,
                UseShellExecute = true,
            });
            return p is null ? "O Windows não abriu o instalador." : null;
        }
        catch (Win32Exception w) when (w.NativeErrorCode == 1223)
        {
            return "Você recusou a permissão do Windows. Nada foi alterado — "
                 + "toque em Atualizar de novo e responda Sim à pergunta do Windows.";
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static void Auditar(string evento, string? detalhe)
    {
        try
        {
            using var cx = Banco.Abrir();
            Caixa.Auditar(cx, null, evento, null, null, detalhe);
        }
        catch { /* o rastro é bom ter, não é motivo para travar a atualização */ }
    }

    // ── A JANELA DE PROGRESSO ─────────────────────────────────────────────────

    /// <summary>
    /// Baixa mostrando MB reais. Devolve (resultado, cancelouOOperador).
    /// </summary>
    private static async Task<(Atualizacao.Baixa Baixa, bool Cancelou)> BaixarComJanelaAsync(
        Window dono, Atualizacao.Manifesto m)
    {
        using var cts = new CancellationTokenSource();
        var janela = Dialogo.Base(dono, 480);
        var pilha = new StackPanel();

        pilha.Children.Add(new TextBlock
        {
            Text = $"Baixando a versão {m.Versao}",
            FontSize = 22, FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["Texto"],
        });

        var linha = new TextBlock
        {
            Text = "Conectando…",
            FontSize = 15, Foreground = (Brush)Application.Current.Resources["TextoFraco"],
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 10),
        };
        pilha.Children.Add(linha);

        var barra = new ProgressBar
        {
            Height = 8, Minimum = 0, Maximum = 100, IsIndeterminate = true,
            BorderThickness = new Thickness(0),
            Background = (Brush)Application.Current.Resources["PainelAlto"],
            Foreground = (Brush)Application.Current.Resources["Rosa"],
        };
        pilha.Children.Add(barra);

        pilha.Children.Add(new TextBlock
        {
            Text = "Pode cancelar a qualquer momento — o que já baixou fica guardado "
                 + "e a próxima tentativa continua de onde parou.",
            FontSize = 13, Foreground = (Brush)Application.Current.Resources["TextoFraco"],
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 18),
        });

        var cancelar = new Button
        {
            Content = "Cancelar",
            Style = (Style)Application.Current.Resources["BotaoBase"],
            MinHeight = 52, FontSize = 16,
            Background = (Brush)Application.Current.Resources["PainelAlto"],
        };
        var pediuCancelar = false;
        cancelar.Click += (_, _) => { pediuCancelar = true; cts.Cancel(); };
        pilha.Children.Add(cancelar);

        janela.Content = Dialogo.Moldura(pilha);
        // Escape = cancelar. Fechar no X não existe (a janela não tem barra), e é bom
        // assim: sumir com a janela sem cancelar deixaria o download órfão rodando.
        janela.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { pediuCancelar = true; cts.Cancel(); }
        };

        var andamento = new Progress<Atualizacao.Andamento>(a =>
        {
            linha.Text = Atualizacao.TextoDoProgresso(a);
            if (a.Porcento is { } p) { barra.IsIndeterminate = false; barra.Value = p; }
        });

        Atualizacao.Baixa? resultado = null;
        // A janela só abre DEPOIS de o download estar de pé (ShowDialog bloqueia).
        janela.Loaded += async (_, _) =>
        {
            // async void por baixo: exceção que escape daqui não tem quem pegue e derruba
            // o processo — ou seja, derruba a FRENTE DE CAIXA por causa de um download.
            try { resultado = await Atualizacao.BaixarAsync(Http, m, null, andamento, cts.Token); }
            catch (Exception ex) { resultado = new Atualizacao.Baixa(null, ex.Message); }
            janela.Close();
        };
        janela.ShowDialog();

        return (resultado ?? new Atualizacao.Baixa(null, "O download não chegou a começar."),
                pediuCancelar);
    }
}
