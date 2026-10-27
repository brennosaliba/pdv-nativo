using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// O lado de WPF da digital (1.0.52): e isto que a maquina de estados do nucleo
/// (<see cref="PortaoDigital"/>) chama quando precisa de janela. Tres janelas, todas de uma
/// linha: "Encoste o dedo no leitor" (com os botoes Primeira vez, Sem digital e Cancelar),
/// "Primeira vez: encoste o dedo 3 vezes" (com o contador) e o CPF digitado pelo proprio
/// funcionario na reserva e no primeiro cadastro (1.0.54). As duas primeiras sao NAO modais (como <see cref="Espera"/>): o leitor esta
/// bloqueado esperando o dedo numa thread de trabalho, e a tela precisa seguir desenhando
/// e aceitando o toque nos botoes.
/// </summary>
public sealed class TelaDigital : ITelaDigital
{
    private readonly Window _dono;

    public TelaDigital(Window dono) => _dono = dono;

    private static Brush R(string chave) => (Brush)Application.Current.Resources[chave];

    public async Task<LeituraNaTela> LerAsync(string titulo, string? aviso, bool ofertaPrimeiraVez,
        Func<CancellationToken, Task<Captura>> ler)
    {
        var escolha = EscolhaNaLeitura.Nenhuma;
        using var cts = new CancellationTokenSource();
        var janela = Dialogo.Base(_dono, 480);
        var pilha = new StackPanel();
        pilha.Children.Add(new TextBlock
        {
            Text = titulo, FontSize = 24, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            Foreground = R("Texto"), TextAlignment = TextAlignment.Center,
        });
        pilha.Children.Add(new TextBlock
        {
            Text = aviso ?? "", FontSize = 15, TextWrapping = TextWrapping.Wrap, Foreground = R("Erro"),
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0),
            Visibility = string.IsNullOrWhiteSpace(aviso) ? Visibility.Collapsed : Visibility.Visible,
        });
        pilha.Children.Add(new TextBlock
        {
            Text = "A digital fica so neste caixa, dentro do Windows.", FontSize = 13, TextWrapping = TextWrapping.Wrap,
            Foreground = R("TextoFraco"), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 14),
        });

        void Botao(string texto, EscolhaNaLeitura e, bool destaque = false)
        {
            var b = PedirValor.Botao(texto, destaque);
            b.MinHeight = 52;
            b.FontSize = 15;
            b.Margin = new Thickness(0, 6, 0, 0);
            b.Click += (_, _) => { escolha = e; cts.Cancel(); };
            pilha.Children.Add(b);
        }
        // 1.0.54: sem senha. A primeira vez e a reserva passam pelo codigo do gerente.
        if (ofertaPrimeiraVez) Botao(Biometria.BotaoPrimeiraVez, EscolhaNaLeitura.PrimeiraVez);
        Botao(Biometria.BotaoSemDigital, EscolhaNaLeitura.SemDigital);
        Botao("Cancelar", EscolhaNaLeitura.Desistiu);

        janela.Content = Dialogo.Moldura(pilha);
        janela.KeyDown += (_, e) => { if (e.Key == Key.Escape) { escolha = EscolhaNaLeitura.Desistiu; cts.Cancel(); } };
        _dono.IsEnabled = false;
        janela.Show();
        Captura c;
        try { c = await ler(cts.Token); }
        catch (OperationCanceledException) { c = Captura.Cancelada; }
        catch (Exception ex) { c = Captura.Falhou(ex.Message); }
        finally
        {
            _dono.IsEnabled = true;
            try { janela.Close(); } catch { }
        }
        return new LeituraNaTela(c, escolha);
    }

    public async Task<Cadastro> CadastrarAsync(string titulo, Func<IProgress<string>, CancellationToken, Task<Cadastro>> cadastrar)
    {
        using var cts = new CancellationTokenSource();
        var janela = Dialogo.Base(_dono, 480);
        var pilha = new StackPanel();
        pilha.Children.Add(new TextBlock
        {
            Text = titulo, FontSize = 24, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            Foreground = R("Texto"), TextAlignment = TextAlignment.Center,
        });
        var progresso = new TextBlock
        {
            Text = "Encoste o mesmo dedo, tire e encoste de novo.", FontSize = 16, TextWrapping = TextWrapping.Wrap,
            Foreground = R("Marca"), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 14),
        };
        pilha.Children.Add(progresso);
        var cancelar = PedirValor.Botao("Cancelar", false);
        cancelar.MinHeight = 52;
        cancelar.Click += (_, _) => cts.Cancel();
        pilha.Children.Add(cancelar);
        janela.Content = Dialogo.Moldura(pilha);
        janela.KeyDown += (_, e) => { if (e.Key == Key.Escape) cts.Cancel(); };
        _dono.IsEnabled = false;
        janela.Show();
        // o leitor avisa de uma thread de trabalho: o texto muda pelo Dispatcher da janela
        var p = new PortaoDigital.ProgressoDireto<string>(t => janela.Dispatcher.BeginInvoke(() => progresso.Text = t + ". Tire o dedo e encoste de novo."));
        try { return await cadastrar(p, cts.Token); }
        catch (OperationCanceledException) { return Cadastro.Cancelado; }
        catch (Exception ex) { return Cadastro.Falhou(ex.Message); }
        finally
        {
            _dono.IsEnabled = true;
            try { janela.Close(); } catch { }
        }
    }

    /// <summary>
    /// O CPF digitado pelo proprio funcionario (reserva e primeiro cadastro, ajuste do dono de
    /// 27/10): visor com a mascara 000.000.000-00, teclado na tela e o fisico, Continuar so com
    /// 11 digitos. A linha vermelha e o aviso da tentativa anterior.
    /// </summary>
    public Task<string?> PedirCpfAsync(string titulo, string? aviso)
    {
        var digitos = "";
        string? resultado = null;
        var janela = Dialogo.Base(_dono, 440);
        var pilha = new StackPanel();
        pilha.Children.Add(PedirValor.Cabecalho(janela, titulo));
        var visor = new TextBlock
        {
            FontSize = 34, FontWeight = FontWeights.Bold, Foreground = R("Texto"),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4),
        };
        pilha.Children.Add(visor);
        pilha.Children.Add(new TextBlock
        {
            Text = aviso ?? "", FontSize = 15, TextWrapping = TextWrapping.Wrap, Foreground = R("Erro"),
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 10), MinHeight = 20,
        });
        var ok = PedirValor.Botao("Continuar", true);
        void Pinta()
        {
            visor.Text = Biometria.MascaraCpf(digitos);
            ok.IsEnabled = digitos.Length == 11;
        }
        var teclado = new TecladoNumerico();
        teclado.Digitou += d => { if (digitos.Length < 11) { digitos += d[0]; Pinta(); } };
        teclado.Apagou += () => { if (digitos.Length > 0) { digitos = digitos[..^1]; Pinta(); } };
        teclado.Limpou += () => { digitos = ""; Pinta(); };
        pilha.Children.Add(teclado);

        var linha = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        linha.ColumnDefinitions.Add(new ColumnDefinition());
        var cancelar = PedirValor.Botao("Cancelar", false);
        cancelar.Margin = new Thickness(0, 0, 4, 0);
        ok.Margin = new Thickness(4, 0, 0, 0);
        cancelar.Click += (_, _) => janela.Close();
        ok.Click += (_, _) => { resultado = digitos; janela.Close(); };
        Grid.SetColumn(cancelar, 0); Grid.SetColumn(ok, 1);
        linha.Children.Add(cancelar); linha.Children.Add(ok);
        pilha.Children.Add(linha);

        janela.KeyDown += (_, e) =>
        {
            var d = e.Key is >= Key.D0 and <= Key.D9 ? (char)('0' + (e.Key - Key.D0))
                  : e.Key is >= Key.NumPad0 and <= Key.NumPad9 ? (char)('0' + (e.Key - Key.NumPad0)) : '\0';
            if (d != '\0' && digitos.Length < 11) { digitos += d; Pinta(); }
            else if (e.Key == Key.Back && digitos.Length > 0) { digitos = digitos[..^1]; Pinta(); }
            else if (e.Key == Key.Enter && ok.IsEnabled) { resultado = digitos; janela.Close(); }
            else if (e.Key == Key.Escape) janela.Close();
        };
        Pinta();
        janela.Content = Dialogo.Moldura(pilha);
        janela.ShowDialog();
        return Task.FromResult(resultado);
    }

    /// <summary>
    /// A lista de funcionarios, so na CONFIGURACAO (recadastrar ou apagar uma digital, atras da
    /// senha do master e do codigo do gerente). Na entrada e no fechamento do turno nao ha lista
    /// desde a 1.0.54: o funcionario digita o proprio CPF (PedirCpfAsync).
    /// </summary>
    public Task<Operador?> EscolherFuncionarioAsync(string titulo, IReadOnlyList<Operador> lista)
    {
        Operador? escolhido = null;
        var janela = Dialogo.Base(_dono, 480);
        var pilha = new StackPanel();
        pilha.Children.Add(PedirValor.Cabecalho(janela, titulo));
        if (lista.Count == 0)
            pilha.Children.Add(new TextBlock { Text = "Nenhum funcionario ativo neste caixa.", FontSize = 15, Foreground = R("TextoFraco"), Margin = new Thickness(0, 0, 0, 10) });
        var rolagem = new ScrollViewer { MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var nomes = new StackPanel();
        foreach (var op in lista)
        {
            var b = PedirValor.Botao(op.Nome, false);
            b.MinHeight = 54;
            b.FontSize = 16;
            b.Margin = new Thickness(0, 0, 0, 6);
            b.Click += (_, _) => { escolhido = op; janela.Close(); };
            nomes.Children.Add(b);
        }
        rolagem.Content = nomes;
        pilha.Children.Add(rolagem);
        var voltar = PedirValor.Botao("Voltar", false);
        voltar.MinHeight = 48;
        voltar.Margin = new Thickness(0, 8, 0, 0);
        voltar.Click += (_, _) => janela.Close();
        pilha.Children.Add(voltar);
        janela.Content = Dialogo.Moldura(pilha);
        janela.KeyDown += (_, e) => { if (e.Key == Key.Escape) janela.Close(); };
        janela.ShowDialog();
        return Task.FromResult(escolhido);
    }

    public void Avisar(string titulo, string texto) => Dialogo.Avisar(_dono, titulo, texto, "erro");
}

/// <summary>
/// A montagem de sempre do portao nas telas: o leitor do Windows (um por processo), a tela
/// de WPF, a nuvem do TOTP (URL fixa) e as listas lidas do banco ANTES de qualquer await
/// (conexao do SQLite nao atravessa await). A entrada e o fechamento chamam daqui.
/// </summary>
public static class PortaoDigitalDaTela
{
    public static Task<DesfechoDigital> IdentificarAsync(Window dono, string finalidade, bool ofertaPrimeiraVez)
    {
        string terminal;
        using (var cx = Banco.Abrir()) terminal = Autorizacao.NomeDoTerminal(cx);
        IAutorizacaoRemota? remota;
        try { remota = Servicos.Autorizador(); } catch { remota = null; }
        return PortaoDigital.IdentificarAsync(Servicos.Leitor(), new TelaDigital(dono),
            chave => { using var c = Banco.Abrir(); return Digitais.OperadorDaChave(c, chave); },
            op => { using var c = Banco.Abrir(); return Digitais.CpfDoOperador(c, op.Id); },
            cpf => { using var c = Banco.Abrir(); return Digitais.OperadorDoCpf(c, cpf); },
            remota, new TelaAutorizacao(dono), terminal, finalidade, ofertaPrimeiraVez);
    }

    /// <summary>
    /// A ENTRADA DO CAIXA (1.0.54): so digital, sem senha. A regra mora em PortaoDigital.EntrarAsync
    /// (digital; reserva; primeiro cadastro pelo codigo do gerente; biometria desligada = reserva).
    /// </summary>
    public static Task<DesfechoDigital> EntrarAsync(Window dono, bool biometriaLigada)
    {
        string terminal;
        using (var cx = Banco.Abrir()) terminal = Autorizacao.NomeDoTerminal(cx);
        IAutorizacaoRemota? remota;
        try { remota = Servicos.Autorizador(); } catch { remota = null; }
        var guarda = new GuardaDigitalSqlite(() => Banco.Abrir(), Servicos.MotorDoLeitor);
        return PortaoDigital.EntrarAsync(biometriaLigada, Servicos.Leitor(), new TelaDigital(dono), guarda,
            chave => { using var c = Banco.Abrir(); return Digitais.OperadorDaChave(c, chave); },
            remota, new TelaAutorizacao(dono), terminal);
    }

    /// <summary>
    /// O codigo do gerente ou do dono para recadastrar ou apagar uma digital (tipo
    /// digital_recadastro, nivel gerente). Devolve a trilha ou null (recusado; a tela avisa).
    /// </summary>
    public static async Task<DesfechoAutorizacao?> AutorizarRecadastroAsync(Window dono, string cpfHash)
    {
        string terminal;
        using (var cx = Banco.Abrir()) terminal = Autorizacao.NomeDoTerminal(cx);
        IAutorizacaoRemota? remota;
        try { remota = Servicos.Autorizador(); } catch { remota = null; }
        var pedido = new PedidoAutorizacao(terminal, Autorizacao.ReferenciaDigitalRecadastro(terminal, cpfHash, DateTime.Now), 0)
        {
            Tipo = Autorizacao.TipoDigitalRecadastro,
            Nivel = Autorizacao.NivelGerente,
        };
        var aut = await Autorizacao.ResolverAsync(remota, pedido, new TelaAutorizacao(dono));
        if (aut.Autorizado) return aut;
        if (!aut.Avisado) Dialogo.Avisar(dono, "Digital", aut.Motivo, "erro");
        return null;
    }

    /// <summary>
    /// Cadastra a digital de <paramref name="op"/> (primeira vez ou recadastro) e guarda ligada
    /// ao CPF. Devolve a linha guardada, ou null com a tela ja avisada.
    /// </summary>
    public static async Task<DigitalGuardada?> CadastrarAsync(Window dono, Operador op, string titulo, string? por)
    {
        string? cpf;
        int? vaga;
        using (var cx = Banco.Abrir())
        {
            cpf = Digitais.CpfDoOperador(cx, op.Id);
            vaga = Digitais.PorCpf(cx, cpf ?? "")?.Vaga ?? Digitais.ProximaVaga(cx);
        }
        if (cpf is null) { Dialogo.Avisar(dono, "Digital", $"{op.Nome} nao tem CPF no cadastro. Cadastre o CPF no painel e toque em Atualizar.", "erro"); return null; }
        if (vaga is null) { Dialogo.Avisar(dono, "Digital", Biometria.AvisoVagasEsgotadas, "erro"); return null; }

        // recadastro: a digital antiga sai do Windows antes, senao o dedo novo bate na vaga ocupada
        using (var cx = Banco.Abrir())
            if (Digitais.PorCpf(cx, cpf) is { } antiga)
                try { await Servicos.Leitor().ApagarAsync(antiga.Chave, CancellationToken.None); } catch { }

        var cad = await PortaoDigital.CadastrarAsync(Servicos.Leitor(), new TelaDigital(dono), titulo, vaga.Value);
        switch (cad.Resultado)
        {
            case ResultadoCadastro.Ok:
            {
                using var cx = Banco.Abrir();
                // 1.0.53: no leitor da HID o mesmo dedo em duas pessoas confundiria a entrada
                if (Digitais.DonoDaParecida(cx, cad, cpf) is { } outro)
                {
                    Dialogo.Avisar(dono, "Digital", Biometria.AvisoDigitalDeOutro(outro), "erro");
                    return null;
                }
                return Digitais.Guardar(cx, cad.Chave!, cpf, op.Id, op.Nome, vaga.Value, por, null, cad.Motor, cad.Molde);
            }
            case ResultadoCadastro.Cancelado:
                Dialogo.Avisar(dono, "Digital", Biometria.AvisoCadastroCancelado, "erro");
                return null;
            case ResultadoCadastro.SemLeitor:
                Dialogo.Avisar(dono, "Digital", Biometria.AvisoSemLeitor, "erro");
                return null;
            default:
                Dialogo.Avisar(dono, "Digital", "O leitor falhou (" + cad.Motivo + "). Tente de novo.", "erro");
                return null;
        }
    }
}

/// <summary>
/// A assinatura da digital que a entrada deixou para a abertura do turno (1.0.52). A tela de
/// login reconhece a pessoa; quem grava o turno e a tela de abertura, segundos depois, e e
/// ela que le daqui. Tomar() limpa: vale uma vez.
/// </summary>
public static class EntradaDigital
{
    private static AssinaturaDigital? _ultima;

    public static void Guardar(AssinaturaDigital? a) => _ultima = a;

    public static AssinaturaDigital? Tomar()
    {
        var a = _ultima;
        _ultima = null;
        return a;
    }
}
