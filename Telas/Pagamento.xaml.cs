using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>Como a venda terminou, do ponto de vista de quem chamou a tela.</summary>
public enum DesfechoVenda { Concluida, Desistiu }

/// <summary>
/// Tela de pagamento.
///
/// A ordem do fluxo é inegociável e vem do que dói no balcão:
///   1. cobra  — se não pagar, nada foi gravado e nenhum número de nota foi queimado;
///   2. grava  — a venda existe mesmo que a SEFAZ recuse a nota depois: o dinheiro entrou;
///   3. emite  — o desfecho fiscal é registrado por tentativa;
///   4. imprime.
///
/// Inverter 1 e 2 criaria venda sem dinheiro. Inverter 2 e 3 criaria nota sem venda,
/// que é pior: some do caixa e aparece na SEFAZ.
/// </summary>
public partial class Pagamento : UserControl
{
    private enum Fase { Forma, Dinheiro, Cobrando, Emitindo, Sucesso, Falha }

    private readonly Operador _operador;
    private readonly Sessao _sessao;
    private readonly IReadOnlyList<LinhaVenda> _itens;
    private readonly Dinheiro _total;
    private readonly IEmissorFiscal _emissor;
    private readonly IProvedorTef? _tef;
    private readonly string _loja;
    private readonly string? _lojaId;

    private string? _documento;
    private string _digitado = "";
    private Fase _fase = Fase.Forma;
    private CancellationTokenSource? _cobranca;
    private DispatcherTimer? _avanco;
    private VendaGravada? _venda;
    private bool _emitindo;

    /// <summary>
    /// As partes já pagas da conta — 2-3 clientes dividindo, cada um passa um valor.
    /// A venda e a nota continuam sendo UMA; o que se divide é o pagamento. Para a
    /// NFC-e cada parte entra pelo valor APLICADO (valor − troco), e a soma fecha com
    /// o total por construção — o que dispensa a tag de troco que o motor não emite.
    /// </summary>
    private readonly List<PagamentoVenda> _partes = new();
    private string _formaEmEdicao = "dinheiro";

    private Dinheiro Falta => new(Math.Max(0,
        _total.Centavos - _partes.Sum(p => p.Valor.Centavos - p.Troco.Centavos)));

    public event Action<DesfechoVenda>? Encerrou;

    public Pagamento(Operador operador, Sessao sessao, IReadOnlyList<LinhaVenda> itens,
        IEmissorFiscal emissor, IProvedorTef? tef, string loja, string? lojaId)
    {
        InitializeComponent();
        _operador = operador;
        _sessao = sessao;
        _itens = itens;
        _total = new Dinheiro(itens.Sum(i => i.Total.Centavos));
        _emissor = emissor;
        _tef = tef;
        _loja = loja;
        _lojaId = lojaId;

        TxtTotal.Text = _total.Formatado();
        var qtd = itens.Sum(i => i.Qtd.Milesimos) / 1000m;
        TxtResumo.Text = $"{itens.Count} {(itens.Count == 1 ? "produto" : "produtos")} · {qtd:0.###} {(qtd == 1 ? "item" : "itens")}";

        MontarFormas();
        MontarAtalhos();
        Teclado.Digitou += d => { if (_digitado.Length < 9) { _digitado += d; PintarDinheiro(); } };
        Teclado.Apagou += () => { if (_digitado.Length > 0) { _digitado = _digitado[..^1]; PintarDinheiro(); } };
        Teclado.Limpou += () => { _digitado = ""; PintarDinheiro(); };

        Unloaded += (_, _) => { _avanco?.Stop(); _cobranca?.Cancel(); };
    }

    // ── 1. FORMA ────────────────────────────────────────────────────────────
    // A cor é CHAVE do tema, não hex: no claro, o verde/ciano/roxo/rosa têm
    // variantes mais escuras para segurar contraste sobre creme.
    private static readonly (string forma, string rotulo, string icone, string cor)[] Formas =
    {
        ("dinheiro", "Dinheiro", "💵", "Ok"),
        ("debito",   "Débito",   "💳", "Ciano"),
        ("credito",  "Crédito",  "💳", "Roxo"),
        ("pix",      "PIX",      "⚡", "Rosa"),
        // 03/09: vale-refeicao (voucher) pelo TEF — so aparece quando a loja
        // liga em Configuracao > TEF ("forma_voucher"). Loja sem convenio nao
        // ganha um botao que so confunde.
        ("voucher",  "Refeição", "🍽️", "Roxo"),
    };

    private static bool VoucherLigado()
    {
        try { using var cx = Banco.Abrir(); return Vendas.Config(cx, "forma_voucher", "0") == "1"; }
        catch { return false; }
    }

    private void MontarFormas()
    {
        var voucher = VoucherLigado();
        foreach (var (forma, rotulo, icone, cor) in Formas)
        {
            if (forma == "voucher" && !voucher) continue;
            var c = ((SolidColorBrush)Application.Current.Resources[cor]).Color;
            var b = new Button
            {
                Style = (Style)Application.Current.Resources["BotaoBase"],
                Margin = new Thickness(8), MinHeight = 140, Padding = new Thickness(10),
                Background = new LinearGradientBrush(
                    Color.FromArgb(RB("AlfaFormaTopo"), c.R, c.G, c.B), Color.FromArgb(RB("AlfaFormaBase"), c.R, c.G, c.B),
                    new Point(0, 0), new Point(0, 1)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(RB("AlfaFormaBorda"), c.R, c.G, c.B)),
                BorderThickness = new Thickness(2),
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = icone, FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center,
            });
            sp.Children.Add(new TextBlock
            {
                Text = rotulo, FontSize = 21, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0),
                Foreground = (Brush)Application.Current.Resources["Texto"],
            });
            b.Content = sp;
            AutomationProperties.SetName(b, rotulo);

            var escolhida = forma;
            b.Click += (_, _) => Escolheu(escolhida);
            GradeFormas.Children.Add(b);
        }
    }

    private void Escolheu(string forma)
    {
        if (!DocumentoPermiteSeguir()) return;
        // Toda forma passa pela tela de valor: é ela que permite dividir a conta.
        // O caso comum (uma forma só) continua a dois toques — o atalho "restante"
        // já vem como botão principal.
        _formaEmEdicao = forma;
        _digitado = "";
        MontarAtalhos();
        Ir(Fase.Dinheiro);
        PintarDinheiro();
    }

    /// <summary>Uma parte foi paga. Fecha a conta ou volta para as formas com o que falta.</summary>
    private void AdicionarParte(PagamentoVenda parte)
    {
        _partes.Add(parte);
        if (Falta.Centavos == 0) { _ = ConcluirTudoAsync(); return; }
        Ir(Fase.Forma);
        PintarPartes();
    }

    /// <summary>
    /// O topo conta a história inteira do pagamento parcial. O NÚMERO GRANDE vira o
    /// que FALTA — deixar o total gigante com parte já paga faz o operador cobrar o
    /// valor errado; a conta completa (total − pago = falta) fica logo abaixo.
    /// </summary>
    private void PintarPartes()
    {
        if (_partes.Count == 0)
        {
            var qtd = _itens.Sum(i => i.Qtd.Milesimos) / 1000m;
            TxtResumo.Text = $"{_itens.Count} {(_itens.Count == 1 ? "produto" : "produtos")} · {qtd:0.###} {(qtd == 1 ? "item" : "itens")}";
            TxtRotuloTotal.Text = "TOTAL";
            TxtTotal.Text = _total.Formatado();
            TxtTotal.Foreground = (Brush)Application.Current.Resources["Texto"];
            TxtContaPagamento.Visibility = Visibility.Collapsed;
            return;
        }

        var pagas = string.Join("  ·  ", _partes.Select(p =>
            $"✓ {Rotulo(p.Forma)} {new Dinheiro(p.Valor.Centavos - p.Troco.Centavos).Formatado()}"));
        TxtResumo.Text = pagas;

        var pago = new Dinheiro(_partes.Sum(p => p.Valor.Centavos - p.Troco.Centavos));
        TxtRotuloTotal.Text = "FALTA";
        TxtTotal.Text = Falta.Formatado();
        TxtTotal.Foreground = (Brush)Application.Current.Resources["Amarelo"];
        TxtContaPagamento.Text = $"{_total.Formatado()} no total  ·  {pago.Formatado()} já pago";
        TxtContaPagamento.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Venda POS avulsa: a maquininha não conversa com o PDV. O operador cobra nela e
    /// confirma aqui. A nota sai com tpIntegra=2 (pagamento não integrado), que é o
    /// enquadramento correto — e a confirmação é explícita porque, sem TEF, esta
    /// resposta é a ÚNICA testemunha de que o cartão passou.
    ///
    /// Serve dois caminhos: caixa SEM TEF (direto) e caixa COM TEF que falhou na hora
    /// (fallback). No fallback pós-timeout, a cobrança do TEF pode ter ficado ARMADA na
    /// maquininha — sem o aviso, o operador passa de novo e o cliente paga duas vezes.
    /// </summary>
    private void RegistrarComoPos(string forma, Dinheiro valor, bool posPodeEstarOcupado)
    {
        var dono = Window.GetWindow(this)!;
        if (posPodeEstarOcupado &&
            !Dialogo.Confirmar(dono, "Risco de cobrança em dobro",
                "A cobrança de agora pode ter ficado presa na maquininha. Cancele lá primeiro — " +
                "se passar por cima, o cliente paga duas vezes.",
                "Já cancelei — continuar", "Voltar"))
            return;

        if (!Dialogo.Confirmar(dono,
                $"Cobrança de {valor.Formatado()}",
                $"Passe {valor.Formatado()} em {Rotulo(forma)} na maquininha e confirme aqui.\n\nO pagamento foi aprovado?",
                "Aprovado — registrar", "Voltar"))
            return;
        AdicionarParte(new PagamentoVenda(forma, valor, Dinheiro.Zero));
    }

    /// <summary>
    /// Acima do limite legal a nota exige identificação do consumidor. Bloquear aqui é
    /// barato; descobrir depois significa nota rejeitada com o cliente no balcão.
    /// </summary>
    private bool DocumentoPermiteSeguir()
    {
        var limite = LimiteDocumento();
        if (_total.Centavos < limite.Centavos || _documento is not null) return true;
        Dialogo.Avisar(Window.GetWindow(this)!, "CPF/CNPJ obrigatório",
            $"Acima de {limite.Formatado()} a nota só sai com CPF ou CNPJ. Peça ao cliente e toque em Informar.", "erro");
        return false;
    }

    private static Dinheiro LimiteDocumento()
    {
        using var cx = Banco.Abrir();
        // Configurável porque o valor pode divergir do padrão nacional por regra estadual —
        // hardcoded, viraria rejeição sem ninguém entender de onde veio.
        var v = Vendas.Config(cx, "limite_doc_cent");
        return new Dinheiro(long.TryParse(v, out var c) && c > 0 ? c : 1_000_000);
    }

    private void InformarDocumento(object sender, RoutedEventArgs e)
    {
        // Diálogo touch com teclado na tela e validação ao vivo — o Confirmar só
        // habilita com documento que fecha, então não existe mais o caminho
        // "digitou tudo → popup de inválido".
        var texto = PedirDocumento.Mostrar(Window.GetWindow(this)!, _documento ?? "");
        if (texto is null) return;

        _documento = texto.Length == 0 ? null : Documentos.ParaNota(texto);
        PintarDocumento();
    }

    private void PintarDocumento()
    {
        TxtDoc.Text = _documento is null ? "não informado" : Documentos.Formatar(_documento);
        TxtDoc.Foreground = (Brush)Application.Current.Resources[_documento is null ? "TextoFraco" : "Ciano"];
        BtnDoc.Content = _documento is null ? "Informar" : "Trocar";
    }

    // ── 2. VALOR DA PARTE (dinheiro OU cartão) ──────────────────────────────
    private Dinheiro Recebido => new(long.TryParse(_digitado, out var c) ? c : 0);

    private void MontarAtalhos()
    {
        GradeAtalhos.Children.Clear();
        var falta = Falta.Centavos;

        // Cartão não recebe "a mais": o único atalho que faz sentido é o restante.
        // Dinheiro ganha as cédulas redondas ACIMA do restante — é o que o cliente entrega.
        var alvos = new List<long> { falta };
        if (_formaEmEdicao == "dinheiro")
            foreach (var passo in new[] { 500L, 1000L, 2000L, 5000L, 10000L })
            {
                var v = (falta + passo - 1) / passo * passo;
                if (v > falta && !alvos.Contains(v)) alvos.Add(v);
            }

        foreach (var v in alvos.Take(4))
        {
            var b = new Button
            {
                Style = (Style)Application.Current.Resources["BotaoBase"],
                // com parte já paga o botão do valor exato é o que FALTA; na venda inteira
                // ele é o TOTAL — as mesmas palavras que estão no alto da tela.
                Content = v == falta
                    ? $"{(_partes.Count == 0 ? "Total" : "Falta")} ({new Dinheiro(v).Formatado()})"
                    : new Dinheiro(v).Formatado(),
                Margin = new Thickness(5), MinHeight = 58, FontSize = 16, Padding = new Thickness(4, 0, 4, 0),
            };
            var valor = v;
            b.Click += (_, _) => { _digitado = valor.ToString(); PintarDinheiro(); };
            GradeAtalhos.Children.Add(b);
        }
    }

    private void PintarDinheiro()
    {
        var rec = Recebido;
        var falta = Falta.Centavos;
        var ehDinheiro = _formaEmEdicao == "dinheiro";
        TxtRotuloEntrada.Text = ehDinheiro ? "RECEBIDO" : $"COBRAR NO {Rotulo(_formaEmEdicao).ToUpperInvariant()}";
        TxtRecebido.Text = rec.Formatado();

        if (!ehDinheiro && rec.Centavos > falta)
        {
            // cartão não gera troco: cobrar a mais no cartão é erro, não troco
            TxtRotuloTroco.Text = $"MÁXIMO NO {Rotulo(_formaEmEdicao).ToUpperInvariant()}";
            TxtTroco.Text = Falta.Formatado();
            Pintar(CaixaTroco, TxtRotuloTroco, TxtTroco, "Erro");
            BtnConfirmarDinheiro.IsEnabled = false;
            return;
        }

        if (rec.Centavos < falta)
        {
            // O bloco vira o aviso de parte PARCIAL: o que segue faltando depois dela.
            TxtRotuloTroco.Text = _partes.Count > 0 || rec.Positivo ? "AINDA FALTA" : "FALTA";
            TxtTroco.Text = new Dinheiro(falta - rec.Centavos).Formatado();
            Pintar(CaixaTroco, TxtRotuloTroco, TxtTroco, rec.Positivo ? "Amarelo" : "Erro");
        }
        else
        {
            TxtRotuloTroco.Text = "TROCO";
            TxtTroco.Text = new Dinheiro(rec.Centavos - falta).Formatado();
            Pintar(CaixaTroco, TxtRotuloTroco, TxtTroco, "Ok");
        }

        // dinheiro aceita parcial (divide a conta) e aceita a mais (troco);
        // cartão só aceita até o restante
        BtnConfirmarDinheiro.IsEnabled = rec.Positivo;
        BtnConfirmarDinheiro.Content = rec.Centavos >= falta
            ? (ehDinheiro ? "Confirmar pagamento" : "Cobrar na maquininha")
            : $"{(ehDinheiro ? "Receber" : "Cobrar")} {rec.Formatado()} agora";
    }

    private static void Pintar(Border caixa, TextBlock rotulo, TextBlock valor, string cor)
    {
        var b = (SolidColorBrush)Application.Current.Resources[cor];
        caixa.Background = new SolidColorBrush(Color.FromArgb(RB("AlfaChipFundo"), b.Color.R, b.Color.G, b.Color.B));
        caixa.BorderBrush = new SolidColorBrush(Color.FromArgb(RB("AlfaChipBorda"), b.Color.R, b.Color.G, b.Color.B));
        rotulo.Foreground = b;
        valor.Foreground = b;
    }

    private void VoltarParaFormas(object sender, RoutedEventArgs e) { Ir(Fase.Forma); PintarPartes(); }

    private void ConfirmarDinheiro(object sender, RoutedEventArgs e)
    {
        var rec = Recebido;
        var falta = Falta.Centavos;
        if (!rec.Positivo) return;

        if (_formaEmEdicao == "dinheiro")
        {
            // a mais = troco (fecha a conta); a menos = parte parcial (conta dividida)
            var troco = new Dinheiro(Math.Max(0, rec.Centavos - falta));
            AdicionarParte(new PagamentoVenda("dinheiro", rec, troco));
            return;
        }

        if (rec.Centavos > falta) return;   // cartão não passa do restante
        if (_tef is null) { RegistrarComoPos(_formaEmEdicao, rec, posPodeEstarOcupado: false); return; }
        var parcelas = _formaEmEdicao == "credito" ? PerguntarParcelas() : 1;
        if (parcelas <= 0) return;   // desistiu na pergunta das parcelas
        _ = CobrarNoTefAsync(_formaEmEdicao, rec, parcelas);
    }

    /// <summary>
    /// Parcelas no crédito, SÓ quando a loja ligou `tef_perguntar_parcelas` (padrão: à vista
    /// direto — donut não se parcela). Ligado, é o que permite o roteiro de homologação do
    /// PayGo (venda parcelada pelo estabelecimento em 99x). 0 = operador desistiu.
    /// </summary>
    private int PerguntarParcelas()
    {
        using var cx = Banco.Abrir();
        if (Vendas.Config(cx, "tef_perguntar_parcelas", "0") != "1") return 1;
        var dono = Window.GetWindow(this)!;
        while (true)
        {
            var txt = PedirTexto.Mostrar(dono, "Parcelas no crédito", "Em quantas vezes? (1 = à vista, até 99)", "1");
            if (txt is null) return 0;   // cancelou / em branco = desistiu (nunca cobrar com parcelas adivinhadas)
            if (int.TryParse(txt.Trim(), out var n) && n >= 1 && n <= 99) return n;
            // "1O", "100", "abc": perguntar de novo — cair em "à vista" sem avisar cobraria
            // errado e o operador só descobriria no comprovante.
            Dialogo.Avisar(dono, "Parcelas", "Digite um número de 1 a 99.", "erro");
        }
    }

    // ── 3. TEF ──────────────────────────────────────────────────────────────
    private async Task CobrarNoTefAsync(string forma, Dinheiro valor, int parcelas = 1)
    {
        var tipo = forma switch
        {
            "credito" => TipoTef.Credito,
            "debito" => TipoTef.Debito,
            "voucher" => TipoTef.Voucher,
            _ => TipoTef.Pix,
        };

        _cobranca?.Cancel();
        _cobranca = new CancellationTokenSource();
        var ct = _cobranca.Token;

        // Parcelas no TÍTULO (os reports de andamento reescrevem o detalhe): o operador precisa
        // conferir o "3x" enquanto o cliente ainda não passou o cartão.
        var vezes = parcelas > 1 ? $" em {parcelas}x" : "";
        var selo = parcelas > 1 ? $" · {parcelas}x" : "";   // colado nos recados curtos do provedor
        Estado("💳", parcelas > 1 ? $"Aguardando o cliente · {parcelas}x" : "Aguardando o cliente",
            forma == "pix"
                ? $"Peça ao cliente para ler o QR na maquininha ({valor.Formatado()})."
                : $"Aproxime, insira ou passe o cartão na maquininha ({valor.Formatado()}{vezes}).",
            ("Cancelar cobrança", CancelarCobrancaNoTef));
        Ir(Fase.Cobrando);

        var andamento = new Progress<AndamentoTef>(a =>
        {
            RegistrarTef(a, forma, valor, parcelas);
            if (a.Mensagem.Length > 0) TxtDetalheEstado.Text = a.Mensagem + selo;
        });

        DesfechoTef d;
        try
        {
            d = await _tef!.CobrarAsync(tipo, valor, _documento, parcelas, andamento, ct);
        }
        catch (Exception ex)
        {
            // TEF caiu antes de armar a maquininha: registrar como POS é seguro.
            Estado("⚠️", "Maquininha fora do ar",
                "Não consegui falar com a maquininha. O cliente NÃO foi cobrado.\n\n" +
                "Confira se ela está ligada e o programa dela aberto, e tente de novo — " +
                "ou passe o valor direto nela." +
                $"\n\nDetalhe: {ex.Message}",
                ("Tentar de novo", () => _ = CobrarNoTefAsync(forma, valor, parcelas)),
                ("Passar na maquininha", () => RegistrarComoPos(forma, valor, posPodeEstarOcupado: false)),
                ("Trocar forma", () => { Ir(Fase.Forma); PintarPartes(); }));
            Ir(Fase.Falha);
            return;
        }

        AtualizarTef(d, forma);

        if (d.Situacao == SituacaoTef.Pago)
        {
            // 03/09 (Savassi): PIX pelo TEF volta APROVADO com NSU mas sem "codigo de
            // autorizacao" (isso e coisa de cartao). O carimbo do fechamento e
            // `tef_aut IS NULL` = manual — entao TODO pix do TEF era pedido na
            // contagem (0 de 7 com carimbo). O NSU e a prova de que passou pela
            // maquininha; vale como carimbo. O grupo <card> da NFC-e continua
            // exigindo CNPJ da credenciadora, entao o pix nao ganha cAut no XML.
            var carimbo = d.Cartao?.CAut
                       ?? (d.Cartao?.Nsu is { Length: > 0 } nsu ? "NSU:" + nsu : null);
            AdicionarParte(new PagamentoVenda(forma, valor, Dinheiro.Zero,
                carimbo, d.Cartao?.Cnpj, d.Cartao?.Bandeira ?? d.Cartao?.TBand, d.Cartao?.Nsu));
            return;
        }

        // Nada foi gravado e nenhum número de nota foi queimado — o operador pode
        // tentar de novo, ou seguir pela maquininha manualmente.
        //
        // Recusa NÃO ganha o desvio pra POS: a operadora disse NÃO a este cartão, e
        // registrar na mão viraria o jeito de "passar" cartão recusado. Timeout e erro
        // ganham — aí quem falhou foi a integração, não o cartão.
        var acoes = new List<(string, Action)> { ("Tentar de novo", () => _ = CobrarNoTefAsync(forma, valor, parcelas)) };
        // Transação DESFEITA (PayGo/NCN) também não ganha o desvio pra POS: o cliente não pagou
        // nada — registrar na mão seria cobrar o que foi desfeito.
        if (d.Situacao != SituacaoTef.Recusado && !d.Desfeita)
            acoes.Add(("Passar na maquininha",
                () => RegistrarComoPos(forma, valor, d.PosPodeTerFicadoOcupado || d.Situacao == SituacaoTef.Timeout)));
        acoes.Add(("Trocar forma", () => { Ir(Fase.Forma); PintarPartes(); }));
        // "Cancelar venda" só enquanto NADA foi aprovado. Com pagamento aprovado na venda,
        // cancelar aqui deixaria dinheiro cobrado sem venda: o caminho é concluir e estornar
        // em TEF → Estornar (com autorização do gerente), que devolve o dinheiro de verdade.
        if (_partes.Count == 0) acoes.Add(("Cancelar venda", ConfirmarAbandono));

        // A pergunta que o operador faz de verdade é "o cliente pagou ou não?". Quando a
        // resposta é certa, ela vem escrita; quando é incerta, o recado manda conferir na
        // maquininha — nunca deixamos o operador adivinhar e cobrar de novo por cima.
        // Fica MUDO onde o provedor já mandou o recado certo: POS possivelmente ocupado
        // (sufixo do MensagemParaTela) e valor divergente ("NÃO emita a nota: confira e
        // estorne"), onde uma frase genérica só enfraqueceria a instrução.
        var recado = d.PosPodeTerFicadoOcupado || d.Codigo == CodigoTef.ValorDivergente ? ""
            : d.Desfeita ? "A cobrança foi desfeita: o cliente não pagou nada."
            : d.Situacao == SituacaoTef.Recusado ? "O cliente NÃO foi cobrado. Tente outro cartão ou outra forma."
            : d.Situacao == SituacaoTef.Cancelado ? "O cliente NÃO foi cobrado."
            : d.Situacao == SituacaoTef.Timeout ? "Pode ter sobrado uma cobrança na maquininha — confira lá antes de cobrar de novo."
            : "Confira na maquininha se a cobrança passou antes de cobrar de novo.";

        Estado(d.Situacao == SituacaoTef.Cancelado ? "↩️" : "⚠️",
            d.Situacao switch
            {
                SituacaoTef.Recusado => "Pagamento não aprovado",
                SituacaoTef.Cancelado => "Cobrança cancelada",
                SituacaoTef.Timeout => "Sem resposta da maquininha",
                _ => "Cobrança não concluída",
            },
            recado.Length == 0 ? d.MensagemParaTela : $"{d.MensagemParaTela}\n\n{recado}",
            acoes.ToArray());
        Ir(Fase.Falha);
    }

    /// <summary>
    /// A linha do TEF nasce ANTES da venda. Se o caixa morrer no meio da cobrança
    /// (queda de energia, app fechado), é ela que prova que existe uma cobrança viva
    /// na maquininha — sem isso, ninguém tem como estornar nem sequer saber.
    /// </summary>
    /// <summary>
    /// Cancela a cobrança EM ANDAMENTO. Aperta Esc na janela do PayGo pelo operador
    /// — com o QR na tela quem desiste é o PayGo, porque o ControlPay não tem rota
    /// para abortar intenção pendente — e avisa o PDV para parar de esperar.
    ///
    /// O desfecho continua vindo do status REAL da intenção: cancelar aqui nunca
    /// declara a cobrança morta por conta própria, senão um Pix pago no último
    /// segundo viraria venda entregue sem pagamento.
    /// </summary>
    private void CancelarCobrancaNoTef()
    {
        int janelas;
        try { janelas = JanelaPayGo.EnviarEsc(); } catch { janelas = 0; }
        TxtDetalheEstado.Text = janelas > 0
            ? "Pedi o cancelamento. Espere a maquininha confirmar…"
            : "Não achei a janela do PayGo — aperte Esc nela para cancelar a cobrança.";
        _cobranca?.Cancel();
    }

    private void RegistrarTef(AndamentoTef a, string forma, Dinheiro valor, int parcelas = 1)
    {
        try
        {
            using var cx = Banco.Abrir();
            cx.Execute("""
                INSERT INTO tef_transacao (id, charge_id, payment_identifier, tipo, valor_cent,
                                           parcelas, situacao, criado_em, atualizado_em)
                VALUES (@Id,@Ch,@Pid,@T,@V,@P,@S,@Em,@Em)
                ON CONFLICT(id) DO UPDATE SET payment_identifier = COALESCE(@Pid, payment_identifier),
                                              -- PayGo: quem manda na situação é o cliente (Guardar), que já
                                              -- pode ter passado de 'aguardando' quando este report chega
                                              -- pela fila da UI. Não regredir.
                                              situacao = CASE WHEN COALESCE(provedor,'') IN ('paygo','controlpay') THEN situacao ELSE @S END,
                                              atualizado_em = @Em
                """,
                new { Id = a.ChargeId, Ch = a.ChargeId, Pid = a.PaymentIdentifier, T = forma,
                      V = valor.Centavos, P = Math.Max(1, parcelas), S = a.Fase, Em = DateTime.Now.ToString("o") });
        }
        catch { /* registrar a tentativa não pode derrubar a cobrança em andamento */ }
    }

    private void AtualizarTef(DesfechoTef d, string forma)
    {
        try
        {
            using var cx = Banco.Abrir();
            var situacao = d.Situacao switch
            {
                SituacaoTef.Pago => "pago",
                SituacaoTef.Recusado => "recusado",
                SituacaoTef.Cancelado => d.PosPodeTerFicadoOcupado ? "orfa" : "cancelado",
                _ => d.PosPodeTerFicadoOcupado ? "orfa" : "erro",
            };
            cx.Execute("""
                UPDATE tef_transacao SET situacao=@S, motivo=@M, payment_identifier=COALESCE(@Pid, payment_identifier),
                                         aut=@Aut, cnpj_cred=@Cnpj, bandeira=@Band, tband=@Tb, nsu=@Nsu,
                                         terminal=@Term, payment_status=COALESCE(@Ps, payment_status), atualizado_em=@Em
                 -- PayGo: a linha já foi fechada pelo cliente (pago/desfeita/ncn_sem_ack…); reescrever a
                 -- situação pelo desfecho da tela apagaria 'ncn_sem_ack' e o boot não reenviaria o NCN.
                 WHERE charge_id=@Ch AND COALESCE(provedor,'') NOT IN ('paygo','controlpay')
                """,
                new { S = situacao, M = d.Motivo, Pid = d.PaymentIdentifier, Ch = d.ChargeId, Ps = d.PaymentStatus,
                      Aut = d.Cartao?.CAut, Cnpj = d.Cartao?.Cnpj, Band = d.Cartao?.Bandeira,
                      Tb = d.Cartao?.TBand, Nsu = d.Cartao?.Nsu, Term = d.Cartao?.Terminal,
                      Em = DateTime.Now.ToString("o") });
        }
        catch { }
    }

    /// <summary>
    /// Abandonar a venda com parte já paga não pode ser um toque distraído: cartão já
    /// passado precisa de estorno na maquininha, dinheiro já na gaveta precisa voltar.
    /// </summary>
    private void ConfirmarAbandono()
    {
        if (_partes.Count == 0) { Encerrou?.Invoke(DesfechoVenda.Desistiu); return; }

        var cartoes = _partes.Where(p => p.Forma != "dinheiro")
            .Sum(p => p.Valor.Centavos - p.Troco.Centavos);
        var dinheiro = _partes.Where(p => p.Forma == "dinheiro")
            .Sum(p => p.Valor.Centavos - p.Troco.Centavos);
        // Cartão/PIX JÁ APROVADO não se resolve cancelando a tela: o dinheiro só volta com
        // estorno na rede. Então a venda é concluída e o estorno sai pelo botão Cartão →
        // Estornar (com autorização do gerente), que cancela a venda no mesmo ato.
        if (cartoes > 0 && _partes.Any(p => p.Forma != "dinheiro" && (p.Nsu is not null || p.Aut is not null)))
        {
            Dialogo.Avisar(Window.GetWindow(this)!, "Pagamento já aprovado",
                $"O cliente já pagou {new Dinheiro(cartoes).Formatado()} no cartão/PIX — o dinheiro saiu da conta dele.\n\n" +
                "Termine a venda. Para devolver, use o botão Cartão → Estornar (precisa de autorização do gerente): " +
                "devolve o dinheiro e cancela a venda no mesmo passo.", "erro");
            return;
        }

        var aviso = "Esta venda já tem pagamento recebido:";
        if (cartoes > 0) aviso += $"\n• {new Dinheiro(cartoes).Formatado()} no cartão/PIX — estorne na maquininha";
        if (dinheiro > 0) aviso += $"\n• {new Dinheiro(dinheiro).Formatado()} em dinheiro — devolva ao cliente";

        if (Dialogo.Confirmar(Window.GetWindow(this)!, "Pagamento já recebido",
                aviso, "Cancelar mesmo assim", "Voltar", perigo: true))
            Encerrou?.Invoke(DesfechoVenda.Desistiu);
    }

    // ── 4. GRAVA, EMITE, IMPRIME ────────────────────────────────────────────
    private async Task ConcluirTudoAsync()
    {
        Estado("🧾", "Emitindo a nota", "Leva alguns segundos — não desligue o caixa.");
        Ir(Fase.Emitindo);

        // Gravar ANTES de emitir. Se o dinheiro entrou, a venda tem que existir mesmo
        // que a SEFAZ recuse a nota — senão o caixa fecha com falta.
        bool modoRecibo;
        try
        {
            using var cx = Banco.Abrir();
            _venda = Vendas.Finalizar(cx, _sessao, _operador, _itens, _partes,
                _documento, _loja, _lojaId);
            modoRecibo = Vendas.Config(cx, "modo_fiscal") == "recibo";
        }
        catch (Exception ex)
        {
            var extra = _partes.All(p => p.Forma == "dinheiro") ? "" :
                "\n\nO cliente JÁ PAGOU no cartão/PIX — não cobre de novo. Anote o valor.";
            Estado("⛔", "Venda não gravada",
                "O dinheiro já entrou, mas a venda não foi salva. Toque em Tentar de novo antes de liberar o cliente." +
                extra + $"\n\nDetalhe: {ex.Message}",
                ("Tentar de novo", () => _ = ConcluirTudoAsync()),
                ("Voltar ao pagamento", () => { Ir(Fase.Forma); PintarPartes(); }));
            Ir(Fase.Falha);
            return;
        }

        // Modo RECIBO (sem emissão fiscal): a venda está gravada e sobe pro painel
        // normalmente — só não existe NFC-e. O papel sai como recibo simples.
        if (modoRecibo) { await ConcluirReciboAsync(); return; }

        await EmitirAsync();
    }

    /// <summary>
    /// Desfecho do modo RECIBO: sem emissor, sem SEFAZ — imprime recibo (se a
    /// impressão automática estiver ligada) e conclui. A venda já foi gravada e o
    /// Dreno a sobe pro painel igual a qualquer outra.
    /// </summary>
    private async Task ConcluirReciboAsync(bool forcarImpressao = false)
    {
        Servicos.Dreno()?.Cutucar();

        bool autoImp;
        string? cnpj;
        string? impressora;
        using (var cx = Banco.Abrir())
        {
            autoImp = Vendas.Config(cx, "imprimir_automatico", "1") != "0";
            impressora = Vendas.Config(cx, "impressora");
            cnpj = cx.ExecuteScalar<string>("SELECT cnpj FROM terminal LIMIT 1");
        }

        string? erro = null;
        if (autoImp || forcarImpressao)
        {
            var dados = new DadosCupom(
                EmitenteNome: _loja, EmitenteCnpj: cnpj, EmitenteIe: null, EmitenteEndereco: null,
                Numero: 0, Serie: 0, Chave: null, Emissao: DateTime.Now, QrCode: null, TpAmb: null,
                Itens: _itens.Select(i => new ItemCupom(i.Codigo ?? "", i.Descricao, i.Qtd, i.Unidade, i.Preco, i.Total)).ToList(),
                Total: _total, VNf: null,
                Pagamentos: _partes.Select(p => new PagamentoCupom(Rotulo(p.Forma),
                    new Dinheiro(p.Valor.Centavos - p.Troco.Centavos))).ToList(),
                Recebido: new Dinheiro(_partes.Sum(p => p.Valor.Centavos)),
                Documento: _documento, Contingencia: false, Operador: _operador.Nome,
                Recibo: true);
            erro = await Impressao.ImprimirAsync(dados, impressora);
        }

        var troco = new Dinheiro(_partes.Sum(p => p.Troco.Centavos));
        var detalhe = "Este caixa não emite nota fiscal — o papel sai como recibo.";
        if (!autoImp && !forcarImpressao) detalhe += "\nImpressão automática desligada — toque em Imprimir recibo se o cliente quiser.";
        if (erro is not null) detalhe += $"\n\nO recibo não saiu: {erro}";

        var acaoImpressao = erro is not null
            ? ("Reimprimir", (Action)(() => _ = ConcluirReciboAsync(true)))
            : !autoImp && !forcarImpressao
                ? ("Imprimir recibo", (Action)(() => _ = ConcluirReciboAsync(true)))
                : default;
        // ícone honesto: com o recibo entalado na impressora, o ✅ dizia "pronto" bem
        // em cima do texto que avisa que o papel não saiu.
        Estado(erro is null ? "✅" : "⚠️", "Venda concluída", detalhe,
            acaoImpressao,
            ("Nova venda", () => Encerrou?.Invoke(DesfechoVenda.Concluida)));
        Ir(Fase.Sucesso);

        if (troco.Positivo)
        {
            TxtTrocoFinal.Text = troco.Formatado();
            CaixaTrocoFinal.Visibility = Visibility.Visible;
        }

        _avanco?.Stop();
        if (erro is null && (autoImp || !forcarImpressao))
        {
            _avanco = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(troco.Positivo ? 4000 : 1600) };
            _avanco.Tick += (_, _) => { _avanco?.Stop(); Encerrou?.Invoke(DesfechoVenda.Concluida); };
            _avanco.Start();
        }
    }

    private async Task EmitirAsync()
    {
        // Trava de reentrada + tela de espera SEMPRE. Antes, o "Tentar emitir de novo"
        // rodava em silêncio com a tela parada nos botões: parecia travado, o operador
        // metralhava o botão e cada toque disparava uma emissão CONCORRENTE — em
        // 06/08 uma venda de teste consumiu 18 números da série 2 assim.
        if (_emitindo) return;
        _emitindo = true;
        Estado("🧾", "Emitindo a nota", "Leva alguns segundos — não desligue o caixa.");
        Ir(Fase.Emitindo);
        try { await EmitirDeVerdadeAsync(); }
        finally { _emitindo = false; }
    }

    private async Task EmitirDeVerdadeAsync()
    {
        // Para a NFC-e o pagamento é sempre o TOTAL DA NOTA, nunca o valor entregue: o
        // motor não emite <vTroco>, e a SEFAZ valida Σ vPag − vTroco = vNF. O troco vive
        // no cupom e na gaveta.
        // O total da linha vai junto: é ele que faz o vUnit enviado reproduzir, na conta
        // do motor, exatamente o valor que o PDV cobrou. Sem isso, item pesável diverge
        // de um centavo entre o papel e o XML autorizado.
        var itensFiscais = _itens.Select(i => ItemFiscal.De(
            i.Codigo ?? i.ProdutoId ?? "", i.Descricao, i.Ncm, i.Cest, i.Csosn, i.Cfop,
            i.Unidade, i.Qtd, i.Preco, i.Total, i.Origem)).ToList();

        // Cada parte entra pelo valor APLICADO (valor − troco): a soma fecha com o vNF
        // por construção, sem depender da tag de troco que o motor não emite. O cartão
        // integrado leva o grupo <card> da própria parte.
        var pagFiscal = _partes.Select(p =>
        {
            var card = p.Aut is { Length: > 0 } && p.CnpjCredenciadora is { Length: > 0 }
                ? new CartaoFiscal(p.Aut, p.CnpjCredenciadora, p.Bandeira)
                : null;
            return PagamentoFiscal.De(p.Forma, new Dinheiro(p.Valor.Centavos - p.Troco.Centavos), card);
        }).ToList();

        ResultadoEmissao r;
        try
        {
            r = await _emissor.EmitirAsync(itensFiscais, pagFiscal, _documento, _venda!.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            r = new ResultadoEmissao { Caminho = "?", Indisponivel = true, Erro = ex.Message };
        }

        try
        {
            using var cx = Banco.Abrir();
            Vendas.RegistrarEmissao(cx, _venda!.Id, r);
        }
        catch { /* o desfecho fiscal não pode derrubar a venda que já está gravada */ }

        // Manda a nota subir para a nuvem, SEM esperar. Nota que fica só no HD do caixa
        // some da 2ª via e do extrato do contador — mas a fila do balcão não pode
        // esperar a internet para o cupom sair.
        if (r.Chave is { Length: 44 }) Servicos.Guarda()?.Cutucar();

        // E manda a VENDA subir junto, também sem esperar. É isto que faz o painel
        // mostrar o faturamento em tempo real: antes a venda só subia quando alguém
        // apertava "Sincronizar", e um dia inteiro de caixa aparecia como R$ 0,00.
        Servicos.Dreno()?.Cutucar();

        if (r.Sucesso) { await ImprimirEConcluirAsync(r); return; }

        // Nota não saiu. A VENDA CONTINUA VALENDO — o dinheiro entrou e o caixa tem que
        // fechar com ele. O que falta é o documento fiscal, e isso se resolve depois.
        var pendente = r.Indisponivel;
        Estado(pendente ? "⏳" : "⛔",
            // Título é substantivo, e é o MESMO nome que a tela de venda dá a este
            // estado quando barra o estorno — o operador não pode achar que são
            // duas coisas diferentes.
            pendente ? "Nota sem resposta" : "Nota não autorizada",
            (pendente
                // Não existe "relatório de vendas" no caixa: mandar o operador olhar lá
                // é mandá-lo procurar um botão que não está na barra. Quem resolve nota
                // é o gerente, e é isso que o texto diz.
                ? "A venda está gravada — o dinheiro entrou. A nota pode ter saído mesmo assim: chame o gerente para conferir antes de emitir de novo, senão sai nota em dobro."
                : "A venda está gravada e conta no caixa. Falta só a nota — avise o gerente para emitir depois.") +
            (r.Erro is { Length: > 0 } ? $"\n\nMotivo: {r.Erro}" : ""),
            ("Tentar emitir de novo", () => _ = EmitirAsync()),
            ("Concluir sem nota", () => _ = ImprimirEConcluirAsync(r)));
        Ir(Fase.Falha);
    }

    private async Task ImprimirEConcluirAsync(ResultadoEmissao r, bool forcarImpressao = false)
    {
        // Impressão automática é escolha da loja (Configuração/botão 🖨). Desligada,
        // a venda conclui sem papel e o botão "Imprimir cupom" fica à mão.
        bool autoImp;
        using (var cxImp = Banco.Abrir())
            autoImp = Vendas.Config(cxImp, "imprimir_automatico", "1") != "0";
        var erro = autoImp || forcarImpressao ? await ImprimirAsync(r) : null;
        var semImpressao = !autoImp && !forcarImpressao;

        var troco = new Dinheiro(_partes.Sum(p => p.Troco.Centavos));
        // "nota pendente" é como a tela de venda chama esta nota quando ela aparece de
        // novo (no estorno). "Sem internet" também nomeava errado: a nota sai assim
        // sempre que a SEFAZ não responde, com ou sem internet na loja.
        var titulo = r.Contingencia ? "Venda concluída — nota pendente"
                   : r.Sucesso ? "Venda concluída"
                   : "Venda concluída sem nota";
        var detalhe = r.Contingencia
            ? "A nota saiu sem aprovação. Ela sobe sozinha quando a conexão voltar."
            : r.Sucesso ? $"Nota {r.Numero} (série {r.Serie}) autorizada."
            : "Avise o gerente: falta emitir a nota desta venda.";
        if (erro is not null) detalhe += $"\n\nO cupom não saiu: {erro}";
        if (semImpressao) detalhe += "\nImpressão automática desligada — toque em Imprimir cupom se o cliente quiser.";

        var acaoImpressao = erro is not null
            ? ("Reimprimir", (Action)(() => _ = ImprimirEConcluirAsync(r, true)))
            : semImpressao
                ? ("Imprimir cupom", (Action)(() => _ = ImprimirEConcluirAsync(r, true)))
                : default;
        // ícone honesto: cupom entalado na impressora não é ✅, mesmo com a nota autorizada.
        Estado(r.Sucesso && erro is null ? "✅" : "⚠️", titulo, detalhe,
            acaoImpressao,
            ("Nova venda", () => Encerrou?.Invoke(DesfechoVenda.Concluida)));
        Ir(Fase.Sucesso);

        if (troco.Positivo)
        {
            TxtTrocoFinal.Text = troco.Formatado();
            CaixaTrocoFinal.Visibility = Visibility.Visible;
        }

        // Auto-avanço SÓ quando o cupom imprimiu. Se falhou, a tela tem que ficar
        // parada no botão "Reimprimir" — senão o timer arranca o botão da frente do
        // operador (e podia fechar a venda no meio da reimpressão) e o cliente vai
        // embora sem cupom. Também paramos um timer anterior antes de reimprimir.
        _avanco?.Stop();
        if (erro is null)
        {
            // Com troco, o operador precisa de tempo para contar. Sem troco, quanto
            // mais rápido a tela liberar, melhor — a fila não espera.
            _avanco = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(troco.Positivo ? 4000 : 1600) };
            _avanco.Tick += (_, _) => { _avanco?.Stop(); Encerrou?.Invoke(DesfechoVenda.Concluida); };
            _avanco.Start();
        }
    }

    private Task<string?> ImprimirAsync(ResultadoEmissao r)
    {
        using var cx = Banco.Abrir();
        var impressora = Vendas.Config(cx, "impressora");
        var dados = new DadosCupom(
            EmitenteNome: r.Emit?.Nome ?? _loja,
            EmitenteCnpj: r.Emit?.Cnpj,
            EmitenteIe: r.Emit?.Ie,
            EmitenteEndereco: r.Emit?.Endereco,
            Numero: r.Numero ?? 0,
            Serie: r.Serie ?? 0,
            Chave: r.Chave,
            Emissao: DateTime.Now,
            QrCode: r.QrCode,
            TpAmb: r.TpAmb,
            Itens: _itens.Select(i => new ItemCupom(i.Codigo ?? "", i.Descricao, i.Qtd, i.Unidade, i.Preco, i.Total)).ToList(),
            Total: _total,
            VNf: r.VNF,
            // vPag da NOTA por parte (valor aplicado) — não o que o cliente entregou.
            // O entregue vai em Recebido, e o cupom deriva o troco dos dois.
            Pagamentos: _partes.Select(p => new PagamentoCupom(Rotulo(p.Forma),
                new Dinheiro(p.Valor.Centavos - p.Troco.Centavos))).ToList(),
            Recebido: new Dinheiro(_partes.Sum(p => p.Valor.Centavos)),
            Documento: _documento,
            Contingencia: r.Contingencia,
            Operador: _operador.Nome,
            Protocolo: r.Protocolo);
        return Impressao.ImprimirAsync(dados, impressora);
    }

    private static string Rotulo(string forma) => forma switch
    {
        "dinheiro" => "Dinheiro", "debito" => "Débito",
        "credito" => "Crédito", "pix" => "PIX", "voucher" => "Refeição", _ => forma,
    };

    // ── ESTADO DA TELA ──────────────────────────────────────────────────────
    private void Estado(string icone, string titulo, string detalhe, params (string, Action)[] acoes)
    {
        TxtIconeEstado.Text = icone;
        TxtTituloEstado.Text = titulo;
        TxtDetalheEstado.Text = detalhe;
        CaixaTrocoFinal.Visibility = Visibility.Collapsed;

        GradeAcoes.Children.Clear();
        var validas = acoes.Where(a => a.Item1 is not null).ToList();
        GradeAcoes.Columns = Math.Max(1, validas.Count);
        for (var i = 0; i < validas.Count; i++)
        {
            var (rotulo, acao) = validas[i];
            var b = new Button
            {
                Content = rotulo,
                // a última ação é a que segue o fluxo; as outras são saídas de emergência
                Style = (Style)Application.Current.Resources[i == validas.Count - 1 ? "BotaoPrincipal" : "BotaoBase"],
                Margin = new Thickness(6), MinHeight = 68, FontSize = 17,
            };
            b.Click += (_, _) => acao();
            GradeAcoes.Children.Add(b);
        }
    }

    private void Ir(Fase f)
    {
        _fase = f;
        PainelForma.Visibility = f == Fase.Forma ? Visibility.Visible : Visibility.Collapsed;
        PainelDinheiro.Visibility = f == Fase.Dinheiro ? Visibility.Visible : Visibility.Collapsed;
        PainelEstado.Visibility = f is Fase.Cobrando or Fase.Emitindo or Fase.Sucesso or Fase.Falha
            ? Visibility.Visible : Visibility.Collapsed;

        TxtEtapa.Text = f switch
        {
            Fase.Forma => "Como o cliente vai pagar?",
            // "entregou" só vale para dinheiro na mão; no cartão o número é o que vai ser cobrado.
            Fase.Dinheiro => _formaEmEdicao == "dinheiro"
                ? "Quanto o cliente entregou?"
                : $"Quanto cobrar no {Rotulo(_formaEmEdicao)}?",
            Fase.Cobrando => "Cobrando na maquininha",
            Fase.Emitindo => "Emitindo a nota",
            Fase.Sucesso => "Pronto",
            _ => "Não deu certo",
        };
        if (f == Fase.Forma) PintarDocumento();
    }

    private void Cancelar(object sender, RoutedEventArgs e) => ConfirmarAbandono();

    /// <summary>Byte do tema atual (alphas de véu e borda).</summary>
    private static byte RB(string chave) => (byte)Application.Current.Resources[chave];
}
