using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// Configuração do terminal, em ASSISTENTE DE 5 PASSOS (Loja → Nota fiscal →
/// Impressora → Maquininha → Pareamento) mais uma tela de resumo. Aparece na 1ª
/// execução e depois só por dentro, com senha de administrador. Reabrir vem
/// PREENCHIDO — reconfigurar não pode significar redigitar tudo.
///
/// DOIS MODOS DE NAVEGAÇÃO, e a diferença é proposital:
///  · 1ª instalação (_jaConfigurado = false): linear. As abas da trilha ficam
///    DESLIGADAS e o Avançar só habilita com o passo válido — quem nunca instalou
///    um caixa não tem como saber que pular a maquininha é diferente de pular o
///    pareamento. O Salvar só existe na tela de resumo, depois de ver tudo.
///  · Reconfiguração (_jaConfigurado = true): livre. As abas viram atalho e o
///    Salvar fica no rodapé em TODOS os passos — quem já instalou abre a tela
///    para mexer num campo só (trocar a impressora, corrigir a rede do PIX) e
///    não pode ser obrigado a percorrer cinco telas para gravar isso.
///
/// Em qualquer um dos dois, <see cref="Salvar"/> continua sendo a PORTA ÚNICA de
/// escrita: os passos só coletam. Antes de tocar no banco ele pergunta ao
/// <see cref="AssistenteConfig"/> se algum passo — não só o da tela — ainda
/// bloqueia, e PULA para ele com o motivo à vista.
/// </summary>
public partial class Configuracao : UserControl
{
    public event Action? Concluiu;
    private readonly bool _jaConfigurado;
    private string? _pfxEscolhido;

    /// <summary>Passo na tela. A trilha e os botões do rodapé derivam daqui.</summary>
    private PassoConfig _passo = PassoConfig.Loja;
    private bool _montando = true;     // WPF dispara TextChanged/Checked durante o InitializeComponent
    private bool _navegando;           // marcar a aba não pode ser lido como clique na aba

    /// <summary>
    /// Ambiente da SEFAZ (1 produção · 2 homologação). Saiu da tela: quem decide é o
    /// PAINEL, que manda o ambiente junto com CNPJ e série no pareamento — digitar isso
    /// à mão era como uma loja emitia nota de verdade achando que estava testando (ou o
    /// contrário, que é pior: mês inteiro em homologação, nenhuma nota valendo). Sem
    /// pareamento e sem instalação anterior, começa em 2: homologação não gera dano.
    /// </summary>
    private int _ambiente = 2;

    /// <summary>
    /// Endereço do backend fiscal. Também saiu da tela: NADA no PDV lê `api_base` hoje
    /// (o emissor é local, em 127.0.0.1:4610). O valor que já está gravado é preservado
    /// no Salvar em vez de apagado — coluna morta a gente para de mostrar, não zera.
    /// </summary>
    private string? _apiBase;


    /// <summary>Pareado com o painel. Em campo, e não relido do cofre DPAPI, porque a validação roda a cada tecla.</summary>
    private bool _pareado;

    /// <summary>
    /// A lista de impressoras do Windows já chegou. Enquanto não chegou (enumerar filas de
    /// rede trava no timeout de cada servidor fora do ar — segundos por servidor), o combo
    /// tem UMA opção: "(padrão do Windows)" — que é justamente a que APAGA a impressora
    /// gravada. Reconfigurando, o Salvar está no rodapé desde o passo 1: dá para abrir a
    /// tela e salvar em um segundo, e a loja perder a impressora do cupom sem ninguém ter
    /// tocado no campo. Ver <see cref="AssistenteConfig.PodeGravarImpressora"/>.
    /// </summary>
    private bool _impressorasProntas;
    private bool _comandasProntas;

    /// <summary>
    /// Bobina que a impressão estava usando quando esta tela abriu, e se o Salvar já trocou
    /// a valendo. O botão "Imprimir cupom de teste" mexe em <see cref="Impressao.PapelMm"/>
    /// para o teste sair na bobina QUE ESTÁ NA TELA; sair sem salvar tem que devolver a que
    /// estava valendo — senão "testei 58 mm, desisti, e o caixa imprimiu tudo em 58 mm até
    /// alguém reiniciar o PDV". É a mesma regra do TEF em <see cref="RestaurarTefSeNaoSalvou"/>.
    /// </summary>
    private readonly string? _papelAoAbrir = Impressao.PapelMm;
    private bool _papelGravado;

    public Configuracao()
    {
        InitializeComponent();
        using var cx = Banco.Abrir();
        _jaConfigurado = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM terminal") > 0;
        _pareado = LerSegredos().ContainsKey("nuvemEmail");
        _ = CarregarImpressorasAsync(Vendas.Config(cx, "impressora"));
        _ = CarregarImpressorasComandaAsync(Vendas.Config(cx, "kds_comanda_impressora"));
        ChkComandaAuto.IsChecked = Vendas.Config(cx, "kds_comanda_auto") == "1";

        // Largura da bobina: as opções saem da MESMA tabela que a impressão usa para
        // montar o cupom, então o combo nunca oferece papel que o desenho não sabe fazer.
        foreach (var op in AssistenteConfig.OpcoesPapel()) CboPapel.Items.Add(op);
        CboPapel.SelectedIndex = AssistenteConfig.IndicePapel(Vendas.Config(cx, "papel_mm"));

        // Tema: preferência da MÁQUINA, carrega mesmo antes da primeira configuração
        // e grava na hora (não espera o Salvar — o Salvar valida identidade fiscal,
        // e uma preferência de luz não pode ficar refém do pareamento).
        _carregandoTema = true;
        CboTema.SelectedIndex = Vendas.Config(cx, "tema") switch { "claro" => 1, "auto" => 2, _ => 0 };
        TxtTemaDe.Text = Vendas.Config(cx, "tema_claro_de", "06:00");
        TxtTemaAte.Text = Vendas.Config(cx, "tema_claro_ate", "18:00");
        BlocoJanelaTema.Visibility = CboTema.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        _carregandoTema = false;

        // TEF: também é preferência da MÁQUINA (qual maquininha/PayGo este caixa usa) —
        // carrega sempre e reabre preenchido.
        TefModo = Vendas.Config(cx, "tef_habilitado") != "1" ? 0
            : Vendas.Config(cx, "tef_provedor") switch { "paygo" => 2, "controlpay" => 3, _ => 1 };
        // Sandbox é escolha EXPLÍCITA: caixa novo (chave ausente) nasce em produção, que é
        // como a loja opera. Antes o combo vinha em sandbox por padrão, e um caixa salvo
        // sem reparar nisso cobraria em ambiente de teste — cobrança que não existe.
        ChkCpaySandbox.IsChecked = Vendas.Config(cx, "tef_cpay_ambiente") == "sandbox";
        TxtCpayTerminal.Text = Vendas.Config(cx, "tef_cpay_terminal", "");
        TxtCpayPessoa.Text = Vendas.Config(cx, "tef_cpay_pessoa", "");
        {
            // segredos do ControlPay no cofre DPAPI (reabrir vem preenchido)
            var segTef = LerSegredos();
            PwdCpayChave.Password = segTef.GetValueOrDefault("cpayChave", "");
            PwdCpaySenha.Password = segTef.GetValueOrDefault("cpaySenhaTecnica", "");
        }
        TxtPayGoPasta.Text = Vendas.Config(cx, "tef_paygo_pasta", "");
        TxtPayGoRegistro.Text = Vendas.Config(cx, "tef_paygo_registro", "");
        TxtPayGoEmpresa.Text = Vendas.Config(cx, "tef_paygo_empresa", "");
        ChkPayGoVias.IsChecked = Vendas.Config(cx, "tef_paygo_imprimir_vias", "1") != "0";
        ChkTefParcelas.IsChecked = Vendas.Config(cx, "tef_perguntar_parcelas", "0") == "1";
        TxtTefSerial.Text = Vendas.Config(cx, "tef_serial_pos", "");
        // Rede é lista FECHADA (RedesPayGo), não mais texto livre: a loja entrou em
        // produção com o "PIX C6 BANK" da homologação gravado e toda cobrança PIX voltava
        // recusada. O que está no banco continua aparecendo mesmo fora da lista.
        EncherRedes(CboPayGoRede, RedesPayGo.OpcoesCartao(Vendas.Config(cx, "tef_paygo_rede")), Vendas.Config(cx, "tef_paygo_rede"));
        EncherRedes(CboPayGoRedePix, RedesPayGo.OpcoesPix(Vendas.Config(cx, "tef_paygo_rede_pix")), Vendas.Config(cx, "tef_paygo_rede_pix"));
        EncherRedes(CboCpayRede, RedesPayGo.OpcoesCartao(Vendas.Config(cx, "tef_cpay_adquirente")), Vendas.Config(cx, "tef_cpay_adquirente"));
        EncherRedes(CboCpayRedePix, RedesPayGo.OpcoesPix(Vendas.Config(cx, "tef_cpay_adquirente_pix")), Vendas.Config(cx, "tef_cpay_adquirente_pix"));
        PintarBlocosTef();
        // Testar/ADM gravam as chaves para rodar com o que está na tela; se o operador sair
        // por "Sair" sem salvar, volta TUDO ao que era (senão "testei e o caixa ligou o PayGo").
        foreach (var k in ChavesTef) _tefOriginal[k] = Vendas.Config(cx, k);

        if (_jaConfigurado)
        {
            var t = cx.QueryFirst("SELECT loja_nome, cnpj, serie_nfce, ambiente, api_base FROM terminal LIMIT 1");
            TxtLoja.Text = (string)t.loja_nome;
            TxtCnpj.Text = (string)t.cnpj;
            TxtSerie.Text = ((long)t.serie_nfce).ToString();
            TxtIe.Text = Vendas.Config(cx, "loja_ie", "");
            _ambiente = (long)t.ambiente == 1 ? 1 : 2;
            _apiBase = t.api_base as string;
            // modo recibo (sem emissão) vive na config, por cima do ambiente da SEFAZ
            ModoRecibo = Vendas.Config(cx, "modo_fiscal") == "recibo";
            ChkImprimirAuto.IsChecked = Vendas.Config(cx, "imprimir_automatico", "1") != "0";
            BtnSair.Visibility = Visibility.Visible;
            // reabrir vem preenchido: reconfigurar não pode ser redigitar tudo
            var seg = LerSegredos();
            TxtSenhaPfx.Password = seg.GetValueOrDefault("senhaPfx", "");
            TxtCsc.Password = seg.GetValueOrDefault("csc", "");
            TxtIdCsc.Text = seg.GetValueOrDefault("idCsc", "000001");
            if (File.Exists(ArqCert)) { TxtPfx.Text = "cert.pfx (já configurado)"; ConferirCert(this, new RoutedEventArgs()); }
            // operador já existe: não pede de novo
            if (Operadores.ExisteAlgum(cx))
            {
                TxtPrimeiroOperador.Visibility = Visibility.Collapsed;
                BlocoOperador.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            // Série 1 e não 3: 1 é a primeira série de um caixa novo. O 3 era herança da
            // loja que instalou primeiro, e nasceu virando pergunta ("por que 3?").
            TxtSerie.Text = "1";
            TxtIdCsc.Text = "000001";
            ModoRecibo = false;
        }

        // status do pareamento na PRÓPRIA seção (a bateria de teste não fala dele)
        if (_pareado)
        {
            TxtStatusPareamento.Text = "✓ Este caixa já está pareado com o painel — vendas e notas sobem no Sincronizar.";
            TxtStatusPareamento.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Ok"];
        }

        // Trilha clicável só para quem já instalou; ver o resumo da classe.
        foreach (var aba in Abas) aba.IsEnabled = _jaConfigurado;
        _montando = false;
        AplicarModoFiscal();
        IrPara(PassoConfig.Loja);
    }

    // ── ASSISTENTE: navegação ───────────────────────────────────────────────

    private RadioButton[] Abas => new[] { AbaLoja, AbaFiscal, AbaImpressora, AbaMaquininha, AbaPareamento };

    /// <summary>Mostra um passo e só ele. Recalcula trilha, rodapé e o que bloqueia.</summary>
    private void IrPara(PassoConfig p)
    {
        _passo = p;
        PassoLoja.Visibility = Se(p == PassoConfig.Loja);
        PassoFiscal.Visibility = Se(p == PassoConfig.Fiscal);
        PassoImpressora.Visibility = Se(p == PassoConfig.Impressora);
        PassoMaquininha.Visibility = Se(p == PassoConfig.Maquininha);
        PassoPareamento.Visibility = Se(p == PassoConfig.Pareamento);
        PassoResumo.Visibility = Se(p == PassoConfig.Resumo);

        _navegando = true;
        var indice = (int)p;
        for (var i = 0; i < Abas.Length; i++) Abas[i].IsChecked = i == indice;
        _navegando = false;

        TxtPassoNumero.Text = AssistenteConfig.Indicador(p);
        TxtTitulo.Text = AssistenteConfig.Nome(p);
        TxtSubtitulo.Text = AssistenteConfig.Explicacao(p);
        TxtErro.Visibility = Visibility.Collapsed;   // o erro era do passo anterior

        // O resumo é uma FOTO do que está na tela agora: montar na entrada é o que
        // garante que ele mostra a última tecla digitada, não o que veio do banco.
        if (p == PassoConfig.Resumo) ListaResumo.ItemsSource = AssistenteConfig.Resumo(Coletar());

        BtnAvancar.Content = p == PassoConfig.Resumo
            ? (_jaConfigurado ? "Salvar alterações" : "Salvar e concluir") : "Avançar";
        BtnVoltar.IsEnabled = p != PassoConfig.Loja;
        // Salvar no rodapé é o atalho de quem já instalou; na 1ª vez ele só aparece no fim.
        BtnSalvar.Visibility = Se(_jaConfigurado && p != PassoConfig.Resumo);
        Revalidar();
    }

    private static Visibility Se(bool visivel) => visivel ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Recalcula o que impede o Avançar. O motivo vai para o rodapé, VISÍVEL: o
    /// botão desligado sem explicação é o jeito mais rápido de travar uma instalação.
    /// </summary>
    private void Revalidar()
    {
        if (_montando) return;
        AtualizarStatusIe();
        var motivo = AssistenteConfig.Bloqueio(_passo, Coletar());
        TxtBloqueio.Text = motivo ?? "";
        TxtBloqueio.Visibility = Se(motivo is not null);
        BtnAvancar.IsEnabled = motivo is null;
    }

    private void CampoMudou(object sender, TextChangedEventArgs e) => Revalidar();
    private void CampoMudouSenha(object sender, RoutedEventArgs e) => Revalidar();

    private void AbaEscolhida(object sender, RoutedEventArgs e)
    {
        if (_navegando || _montando) return;
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var i)) IrPara((PassoConfig)i);
    }

    private void Avancar(object sender, RoutedEventArgs e)
    {
        if (_passo == PassoConfig.Resumo) { Salvar(sender, e); return; }
        IrPara(_passo + 1);
    }

    private void Voltar(object sender, RoutedEventArgs e)
    {
        if (_passo == PassoConfig.Loja) return;
        IrPara(_passo - 1);
    }

    /// <summary>Fecha a tela sem salvar (só existe reconfigurando — na 1ª vez não há para onde ir).</summary>
    private void Sair(object sender, RoutedEventArgs e)
    {
        RestaurarTefSeNaoSalvou();
        // O cupom de teste imprime na bobina que está na TELA; desistir tem que devolver
        // a que está gravada, senão a loja inteira passa a imprimir na largura testada.
        if (!_papelGravado) Impressao.PapelMm = _papelAoAbrir;
        Concluiu?.Invoke();
    }

    /// <summary>Retrato do que está na tela agora — é isto que valida, resume e grava.</summary>
    private DadosAssistente Coletar() => new()
    {
        Loja = TxtLoja.Text,
        Cnpj = TxtCnpj.Text,
        Ie = TxtIe.Text,
        Recibo = ModoRecibo,
        Serie = TxtSerie.Text,
        Ambiente = _ambiente,
        TemCertificado = _pfxEscolhido is not null || File.Exists(ArqCert),
        Impressora = ImpressoraEscolhida(),
        ImprimirAuto = ChkImprimirAuto.IsChecked != false,
        PapelMm = PapelEscolhido(),
        ImpressoraComanda = ImpressoraComandaEscolhida(),
        ComandaAuto = ChkComandaAuto.IsChecked == true,
        Tef = TefModo,
        PayGoPasta = TxtPayGoPasta.Text,
        PayGoRedeCartao = RedeEscolhida(CboPayGoRede),
        PayGoRedePix = RedeEscolhida(CboPayGoRedePix),
        CpayChave = PwdCpayChave.Password,
        CpayPessoa = TxtCpayPessoa.Text,
        CpayTerminal = TxtCpayTerminal.Text,
        CpayRedeCartao = RedeEscolhida(CboCpayRede),
        CpayRedePix = RedeEscolhida(CboCpayRedePix),
        CpaySandbox = ChkCpaySandbox.IsChecked == true,
        PosSerial = TxtTefSerial.Text,
        Pareado = _pareado,
        PedeAdmin = BlocoOperador.Visibility == Visibility.Visible,
        AdminNome = TxtOpNome.Text,
        AdminCpf = TxtOpCpf.Text,
        AdminPin = TxtOpPin.Text,
    };

    // ── PASSO 1: LOJA ───────────────────────────────────────────────────────

    /// <summary>Cartão escolhido no passo 1. É ele que decide `modo_fiscal` no Salvar.</summary>
    private bool ModoRecibo
    {
        get => OpFiscalRecibo?.IsChecked == true;
        set { OpFiscalRecibo.IsChecked = value; OpFiscalNfce.IsChecked = !value; }
    }

    private void ModoFiscalMudou(object sender, RoutedEventArgs e) { AplicarModoFiscal(); Revalidar(); }

    /// <summary>
    /// Revelação progressiva: em "Só recibo" o passo da nota fiscal não pede certificado
    /// nem CSC (a loja ESCOLHEU não emitir) — mas continua existindo, com o aviso, porque
    /// sumir com um passo do meio de um assistente de 5 deixa o instalador sem saber se
    /// ele existia e ele errou, ou se nunca existiu.
    /// </summary>
    private void AplicarModoFiscal()
    {
        if (BlocoCertificado is null || AvisoRecibo is null) return;
        BlocoCertificado.Visibility = Se(!ModoRecibo);
        AvisoRecibo.Visibility = Se(ModoRecibo);
    }

    /// <summary>ISENTO é o VALOR do campo (é o que sai impresso), por isso preenche o campo em vez de marcar uma opção ao lado.</summary>
    private void MarcarIsento(object sender, RoutedEventArgs e)
    {
        TxtIe.Text = AssistenteConfig.IeIsento;
        TxtIe.CaretIndex = TxtIe.Text.Length;
    }

    private void AtualizarStatusIe()
    {
        if (TxtStatusIe is null) return;
        var ie = AssistenteConfig.NormalizarIe(TxtIe.Text);
        string texto, cor;
        if (ie.Length == 0) { texto = ""; cor = "TextoFraco"; }
        else if (ie == AssistenteConfig.IeIsento) { texto = "✓ Sem inscrição estadual — sai \"ISENTO\" no cupom."; cor = "Ok"; }
        else if (AssistenteConfig.IeValida(ie)) { texto = "✓ Inscrição estadual registrada."; cor = "Ok"; }
        else { texto = "✗ Inscrição estadual curta demais — são de 8 a 14 dígitos. Sem IE, use o botão ISENTO."; cor = "Erro"; }
        TxtStatusIe.Text = texto;
        TxtStatusIe.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[cor];
    }

    /// <summary>Senha de admin guardada como hash (mesmo PBKDF2 do PIN), nunca em claro.</summary>
    public static bool SenhaAdminConfere(SqliteConnection cx, string senha)
    {
        var r = cx.QueryFirstOrDefault("SELECT pin_hash, pin_salt FROM operador WHERE id = '_admin_'");
        if (r is null) return false;
        return Operadores.Confere(senha, (string)r.pin_hash, (string)r.pin_salt);
    }

    // ── SEGREDOS DA MÁQUINA ─────────────────────────────────────────────────
    // Certificado, senha dele, CSC e a credencial da nuvem ficam cifrados por DPAPI,
    // amarrados a ESTA máquina. Mesmo copiando o arquivo, em outro PC não abre.
    private static string PastaSegredos => Path.Combine(Banco.Pasta, "seg");
    private static string ArqSegredos => Path.Combine(PastaSegredos, "seg.dat");
    public static string ArqCert => Path.Combine(PastaSegredos, "cert.pfx");

    // ── PASSO 3: IMPRESSORA ─────────────────────────────────────────────────

    /// <summary>
    /// Lista as impressoras fora da thread de UI. Enumerar filas de rede bloqueia no
    /// timeout de cada servidor de impressão inalcançável — segundos por servidor, com
    /// a tela congelada.
    /// </summary>
    private async Task CarregarImpressorasAsync(string? escolhida)
    {
        CboImpressora.Items.Add("(padrão do Windows)");
        CboImpressora.SelectedIndex = 0;
        try
        {
            var lista = await Impressao.ImpressorasAsync();
            foreach (var nome in lista) CboImpressora.Items.Add(nome);
            if (escolhida is { Length: > 0 } && CboImpressora.Items.Contains(escolhida))
                CboImpressora.SelectedItem = escolhida;
            else if (escolhida is { Length: > 0 })
            {
                // impressora salva que não está mais instalada: mantém à vista em vez de
                // silenciosamente cair na padrão, senão o cupom sai noutro lugar sem aviso
                CboImpressora.Items.Add(escolhida + "  (não encontrada)");
                CboImpressora.SelectedIndex = CboImpressora.Items.Count - 1;
            }
            // Só agora o combo representa uma ESCOLHA. Antes disto ele representa
            // "ainda não sei", e o Salvar não pode ler isso como "apague a impressora".
            _impressorasProntas = true;
        }
        catch (Exception ex)
        {
            TxtStatusImpressao.Text = "Não consegui listar as impressoras: " + ex.Message;
        }
    }

    private string? ImpressoraEscolhida()
    {
        var s = CboImpressora.SelectedItem as string;
        if (s is null || s.StartsWith("(padrão")) return null;
        var i = s.IndexOf("  (não encontrada)", StringComparison.Ordinal);
        return i > 0 ? s[..i] : s;
    }

    /// <summary>Espelho de <see cref="CarregarImpressorasAsync"/> pro combo da
    /// COMANDA da cozinha — impressora própria, separada da bobina do caixa.</summary>
    private async Task CarregarImpressorasComandaAsync(string? escolhida)
    {
        CboImpressoraComanda.Items.Add("(padrão do Windows)");
        CboImpressoraComanda.SelectedIndex = 0;
        try
        {
            var lista = await Impressao.ImpressorasAsync();
            foreach (var nome in lista) CboImpressoraComanda.Items.Add(nome);
            if (escolhida is { Length: > 0 } && CboImpressoraComanda.Items.Contains(escolhida))
                CboImpressoraComanda.SelectedItem = escolhida;
            else if (escolhida is { Length: > 0 })
            {
                CboImpressoraComanda.Items.Add(escolhida + "  (não encontrada)");
                CboImpressoraComanda.SelectedIndex = CboImpressoraComanda.Items.Count - 1;
            }
            _comandasProntas = true;
        }
        catch { /* a lista do combo de cima já mostrou o erro do spooler */ }
    }

    private string? ImpressoraComandaEscolhida()
    {
        var s = CboImpressoraComanda.SelectedItem as string;
        if (s is null || s.StartsWith("(padrão")) return null;
        var i = s.IndexOf("  (não encontrada)", StringComparison.Ordinal);
        return i > 0 ? s[..i] : s;
    }

    private double PapelEscolhido() =>
        (CboPapel.SelectedItem as OpcaoPapel)?.Mm ?? Impressao.PapelPadrao.BobinaMm;

    /// <summary>
    /// Diz quantos caracteres cabem na linha da bobina escolhida. É a única tradução que
    /// interessa ao dono: "58 mm" não significa nada, "32 colunas — a descrição do produto
    /// vai abreviar mais" significa.
    /// </summary>
    private void PapelMudou(object sender, SelectionChangedEventArgs e)
    {
        if (TxtStatusPapel is null || CboPapel.SelectedItem is not OpcaoPapel op) return;
        TxtStatusPapel.Text = $"Cada linha do cupom cabe {op.Colunas} caracteres."
            + (op.Mm < Impressao.PapelPadrao.BobinaMm
                ? " Bobina estreita: o cupom sai mais comprido e a descrição do produto abrevia mais."
                : "");
    }

    /// <summary>
    /// Cupom de teste com dados falsos. Existe para validar layout, corte e QR SEM
    /// emitir documento fiscal — descobrir que a impressão está torta junto com a
    /// primeira nota real confunde dois problemas num só.
    /// </summary>
    private async void TestarImpressao(object sender, RoutedEventArgs e)
    {
        BtnTesteImpressao.IsEnabled = false;
        TxtStatusImpressao.Text = "Imprimindo…";
        try
        {
            // O teste tem que sair na bobina que está NA TELA, não na que está salva —
            // senão o operador escolhe 58 mm, imprime o teste em 80 e conclui que a
            // largura configurável não funciona.
            Impressao.PapelMm = AssistenteConfig.TextoPapel(PapelEscolhido());

            var dados = new DadosCupom(
                EmitenteNome: TxtLoja.Text.Length > 0 ? TxtLoja.Text : "LOJA DE TESTE",
                EmitenteCnpj: TxtCnpj.Text.Length > 0 ? TxtCnpj.Text : "00000000000000",
                EmitenteIe: AssistenteConfig.NormalizarIe(TxtIe.Text) is { Length: > 0 } ie ? ie : "ISENTO",
                EmitenteEndereco: "RUA DE TESTE, 100 - CENTRO - BELO HORIZONTE/MG",
                Numero: 0, Serie: int.TryParse(TxtSerie.Text, out var sr) ? sr : 0,
                Chave: new string('0', 44),
                Emissao: DateTime.Now,
                // conteúdo qualquer só pra exercitar o desenho do QR
                QrCode: "https://portalsped.fazenda.mg.gov.br/portalnfce/sistema/qrcode.xhtml?p=TESTE",
                TpAmb: 2,
                Itens: new[]
                {
                    new ItemCupom("SKU1", "COOKIE TRADICIONAL", Quantidade.Um, "UN",
                        Dinheiro.DeReais(12), Dinheiro.DeReais(12)),
                    new ItemCupom("SKU2", "AGUA MINERAL 500ML", new Quantidade(2000), "UN",
                        Dinheiro.DeReais(6), Dinheiro.DeReais(12)),
                },
                Total: Dinheiro.DeReais(24),
                VNf: 24m,
                Pagamentos: new[] { new PagamentoCupom("Dinheiro", Dinheiro.DeReais(24)) },
                Recebido: Dinheiro.DeReais(50),
                Documento: null,
                Contingencia: false,
                Operador: "TESTE DE IMPRESSÃO");

            var erro = await Impressao.ImprimirAsync(dados, ImpressoraEscolhida());
            TxtStatusImpressao.Text = erro is null
                ? $"Cupom de teste enviado em {PapelEscolhido():0} mm. Confira o papel: margens, corte e o QR."
                : "Não imprimiu: " + erro;
            TxtStatusImpressao.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[
                erro is null ? "Ok" : "Erro"];
        }
        finally { BtnTesteImpressao.IsEnabled = true; }
    }

    public static Dictionary<string, string> LerSegredos()
    {
        try
        {
            var b = ProtectedData.Unprotect(File.ReadAllBytes(ArqSegredos), null, DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(b))!;
        }
        catch { return new Dictionary<string, string>(); }
    }

    private static void GravarSegredos(Dictionary<string, string> d)
    {
        Directory.CreateDirectory(PastaSegredos);
        var b = ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)), null, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(ArqSegredos, b);
    }

    // ── PASSO 5: PAREAMENTO ─────────────────────────────────────────────────

    /// <summary>
    /// Pareia o caixa com o painel: o gerente gera um código de 6 dígitos no painel, o
    /// caixa digita aqui UMA vez e recebe a identidade própria (criada no servidor),
    /// que fica cifrada nesta máquina. Nenhuma senha é digitada no balcão, e revogar
    /// um caixa é um clique no painel — é o que destrava a subida de vendas e notas.
    /// </summary>
    private async void Parear(object sender, RoutedEventArgs e)
    {
        var dono = Window.GetWindow(this)!;
        var codigo = PedirTexto.Mostrar(dono, "Parear com o painel",
            "Digite o código de 6 dígitos mostrado no painel", "");
        if (string.IsNullOrWhiteSpace(codigo)) return;

        BtnParear.IsEnabled = false;
        TxtStatusPareamento.Text = "Falando com o servidor…";
        try
        {
            using var cx = Banco.Abrir();
            var t = cx.QueryFirstOrDefault("SELECT terminal_uuid, loja_nome FROM terminal LIMIT 1");
            // nomes de campo são CONTRATO com a edge pdv-pareamento: `code` e `nome`
            var corpo = JsonSerializer.Serialize(new
            {
                acao = "resgatar",
                code = codigo.Trim(),
                nome = (t?.loja_nome as string) ?? Environment.MachineName,
            });

            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post,
                Nuvem.UrlPadrao + "/functions/v1/pdv-pareamento");
            req.Headers.TryAddWithoutValidation("apikey", Nuvem.AnonKey);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Nuvem.AnonKey);
            req.Content = new System.Net.Http.StringContent(corpo, Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req);
            var texto = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(texto);
            var r = doc.RootElement;
            if (!resp.IsSuccessStatusCode
                || !r.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
                || !r.TryGetProperty("email", out var em) || !r.TryGetProperty("senha", out var sn))
            {
                var erro = r.TryGetProperty("error", out var er) ? er.GetString() : $"HTTP {(int)resp.StatusCode}";
                TxtStatusPareamento.Text = "✗ " + (erro ?? "não deu certo — gere um código novo no painel");
                return;
            }

            // a identidade do TERMINAL fica cifrada nesta máquina; ninguém a digita nem vê
            var seg = LerSegredos();
            seg["nuvemEmail"] = em.GetString()!;
            seg["nuvemSenha"] = sn.GetString()!;
            GravarSegredos(seg);
            _pareado = true;

            // A IDENTIDADE DA LOJA vem junto do código: CNPJ, razão social, ambiente e
            // — o que mais importa — a SÉRIE alocada pelo servidor. Digitar isso à mão
            // era a origem de dois erros caros: CNPJ errado = nota fiscal emitida em
            // nome de outra loja; série repetida entre dois caixas = Rejeição 539 em
            // cascata, descoberta só com cliente no balcão.
            var resumo = AplicarIdentidade(cx, r);
            TxtStatusPareamento.Text = "✓ Caixa pareado. " + resumo;
            TxtStatusPareamento.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Ok"];
        }
        catch (Exception ex)
        {
            TxtStatusPareamento.Text = "✗ " + ex.Message;
        }
        finally { BtnParear.IsEnabled = true; Revalidar(); }
    }

    /// <summary>
    /// Grava a identidade que veio do pareamento nos campos da tela E na tabela
    /// `terminal`. É o que transforma a primeira instalação em "digitou 6 dígitos,
    /// acabou" — antes, loja/CNPJ/série eram digitados aqui, e os dois erros que
    /// isso causava são caros: CNPJ errado emite nota em nome de outra loja, e
    /// série repetida entre caixas gera Rejeição 539 em cascata.
    ///
    /// Campos ausentes na resposta são IGNORADOS (edge antiga continua parenado
    /// normal, só sem preencher). Devolve o resumo para a tela.
    /// </summary>
    private string AplicarIdentidade(SqliteConnection cx, JsonElement r)
    {
        string? Txt(string nome) =>
            r.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Num(string nome) =>
            r.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

        var loja = Txt("loja_nome");
        var lojaId = Txt("loja_id");
        var cnpj = new string((Txt("cnpj") ?? "").Where(char.IsLetterOrDigit).ToArray());
        var serieLocal = Num("serie_local");
        var serieNuvem = Num("serie_nuvem");
        var ambiente = Num("ambiente");

        if (loja is { Length: > 0 }) TxtLoja.Text = loja;
        if (cnpj.Length == 14) TxtCnpj.Text = cnpj;
        if (serieLocal is int sl) TxtSerie.Text = sl.ToString();
        // O ambiente da SEFAZ não é mais campo de tela: é ISTO que o define.
        if (ambiente is int amb) _ambiente = amb == 1 ? 1 : 2;

        // A série da NUVEM é outro contador (nfce_config). Guardar aqui é o que
        // permite ao resolvedor provar que ela é diferente da série local antes
        // de deixar a contingência assumir.
        if (serieNuvem is int sn) Vendas.GravarConfig(cx, "serie_nuvem", sn.ToString());

        var jaTem = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM terminal") > 0;
        if (jaTem && (loja is { Length: > 0 } || cnpj.Length == 14 || serieLocal is not null))
        {
            cx.Execute("""
                UPDATE terminal SET
                  loja_nome  = COALESCE(@Loja, loja_nome),
                  loja_id    = COALESCE(@LojaId, loja_id),
                  cnpj       = COALESCE(@Cnpj, cnpj),
                  serie_nfce = COALESCE(@Serie, serie_nfce),
                  ambiente   = COALESCE(@Amb, ambiente)
                WHERE id = 1
                """,
                new
                {
                    Loja = loja is { Length: > 0 } ? loja : null,
                    LojaId = lojaId is { Length: > 0 } ? lojaId : null,
                    Cnpj = cnpj.Length == 14 ? cnpj : null,
                    Serie = serieLocal,
                    Amb = ambiente,
                });
        }

        var partes = new List<string>();
        if (loja is { Length: > 0 }) partes.Add(loja);
        if (serieLocal is int s2) partes.Add($"série {s2}");
        if (ambiente == 2) partes.Add("HOMOLOGAÇÃO");
        return partes.Count == 0
            ? "Vendas e notas passam a subir no Sincronizar."
            : string.Join(" · ", partes) + " — confira e salve.";
    }

    // ── PASSO 2: NOTA FISCAL ────────────────────────────────────────────────

    private void EscolherPfx(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Certificado (*.pfx;*.p12)|*.pfx;*.p12" };
        if (d.ShowDialog() != true) return;
        _pfxEscolhido = d.FileName;
        TxtPfx.Text = Path.GetFileName(d.FileName);
        ConferirCert(this, new RoutedEventArgs());
        if (TxtSenhaPfx.Password.Length == 0) TxtSenhaPfx.Focus();
    }

    // ── CNPJ: máscara + algoritmo em tempo real ─────────────────────────────
    // O dígito verificador pega CNPJ digitado errado AQUI, não na Rejeição 207
    // da SEFAZ com cliente no balcão. A máscara (00.000.000/0000-00) elimina a
    // dúvida "com ou sem pontos?" — aceita dos dois jeitos e exibe formatado.
    private bool _formatandoCnpj;

    private void FormatarCnpj(object sender, TextChangedEventArgs e)
    {
        if (_formatandoCnpj) return;
        _formatandoCnpj = true;
        try
        {
            var dig = new string(TxtCnpj.Text.Where(char.IsDigit).Take(14).ToArray());
            var sb = new StringBuilder(18);
            for (var i = 0; i < dig.Length; i++)
            {
                if (i == 2 || i == 5) sb.Append('.');
                else if (i == 8) sb.Append('/');
                else if (i == 12) sb.Append('-');
                sb.Append(dig[i]);
            }
            TxtCnpj.Text = sb.ToString();
            TxtCnpj.CaretIndex = TxtCnpj.Text.Length; // digitação de CNPJ é sempre "no fim"

            if (dig.Length == 0) { TxtStatusCnpj.Text = ""; }
            else if (dig.Length < 14)
            {
                TxtStatusCnpj.Text = $"… {dig.Length}/14 dígitos";
                TxtStatusCnpj.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextoFraco"];
            }
            else if (Documentos.CnpjValido(dig))
            {
                TxtStatusCnpj.Text = "✓ CNPJ válido";
                TxtStatusCnpj.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Ok"];
            }
            else
            {
                TxtStatusCnpj.Text = "✗ Dígitos verificadores não conferem — confira número por número.";
                TxtStatusCnpj.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Erro"];
            }
            // CNPJ mudou: se já há certificado carregado, refaz a comparação
            if (TxtSenhaPfx.Password.Length > 0) ConferirCert(this, new RoutedEventArgs());
        }
        finally { _formatandoCnpj = false; Revalidar(); }
    }

    /// <summary>CNPJ do titular do certificado ICP-Brasil (CN = "RAZÃO SOCIAL:CNPJ").</summary>
    private static string? CnpjDoCertificado(X509Certificate2 c)
    {
        var cn = c.GetNameInfo(X509NameType.SimpleName, false) ?? "";
        var i = cn.LastIndexOf(':');
        if (i >= 0)
        {
            var dig = new string(cn[(i + 1)..].Where(char.IsDigit).ToArray());
            if (dig.Length == 14) return dig;
        }
        // fallback: qualquer sequência de 14 dígitos no Subject
        var m = System.Text.RegularExpressions.Regex.Match(c.Subject, @"\d{14}");
        return m.Success ? m.Value : null;
    }

    /// <summary>
    /// Abre o .pfx com a senha digitada — pega senha errada AQUI, não na 1ª venda.
    /// Roda a cada tecla da senha (PasswordChanged) e também confere se o CNPJ do
    /// certificado é o MESMO da loja: certificado de outro CNPJ emite nota que a
    /// SEFAZ rejeita (ou pior, autoriza em nome de outra empresa).
    /// </summary>
    private void ConferirCert(object sender, RoutedEventArgs e)
    {
        var caminho = _pfxEscolhido ?? (File.Exists(ArqCert) ? ArqCert : null);
        if (caminho is null || TxtSenhaPfx.Password.Length == 0) { TxtStatusCert.Text = ""; return; }
        try
        {
            using var c = new X509Certificate2(caminho, TxtSenhaPfx.Password);
            var dias = (int)(c.NotAfter - DateTime.Now).TotalDays;
            if (dias < 0)
            {
                TxtStatusCert.Text = $"✗ Certificado VENCIDO em {c.NotAfter:dd/MM/yyyy} — a SEFAZ recusa as notas.";
                TxtStatusCert.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Erro"];
                return;
            }
            var texto = $"✓ {c.GetNameInfo(X509NameType.SimpleName, false)} · válido até {c.NotAfter:dd/MM/yyyy}"
                        + (dias <= 30 ? $" (faltam {dias} dias)" : "");
            var chave = dias <= 30 ? "Erro" : "Ok";

            // Comparação pela RAIZ (8 primeiros dígitos = a empresa): certificado da
            // MATRIZ 0001 assina nota das FILIAIS 0002/0003... — a SEFAZ aceita.
            // Erro de verdade é raiz DIFERENTE (outra empresa).
            var cnpjCert = CnpjDoCertificado(c);
            var cnpjLoja = new string(TxtCnpj.Text.Where(char.IsDigit).ToArray());
            if (cnpjCert is not null && cnpjLoja.Length == 14)
            {
                if (cnpjCert == cnpjLoja) texto += " · CNPJ confere com a loja";
                else if (cnpjCert[..8] == cnpjLoja[..8])
                    texto += $" · certificado da matriz/outra filial ({Documentos.Formatar(cnpjCert)}) — mesma empresa, a SEFAZ aceita";
                else
                {
                    texto += $"\n✗ Certificado de OUTRA EMPRESA ({Documentos.Formatar(cnpjCert)}) — a raiz do CNPJ não confere; nota sairia em nome de outra empresa.";
                    chave = "Erro";
                }
            }
            TxtStatusCert.Text = texto;
            TxtStatusCert.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[chave];
        }
        catch
        {
            TxtStatusCert.Text = "✗ Senha incorreta para este certificado.";
            TxtStatusCert.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Erro"];
        }
    }

    // ── TESTE GERAL ─────────────────────────────────────────────────────────
    /// <summary>
    /// Confere a configuração fiscal de uma vez, na ordem em que as coisas quebram
    /// na vida real: CNPJ → certificado → CSC → emissor local. Cada linha é ✓/⚠/✗
    /// com o motivo — pra descobrir o problema AGORA, não na primeira venda com
    /// cliente esperando.
    /// </summary>
    private async void TestarConfiguracao(object sender, RoutedEventArgs e)
    {
        BtnTestarTudo.IsEnabled = false;
        TxtStatusTeste.Text = "Testando…";
        var linhas = new List<string>();
        var pior = 0; // 0 ok, 1 aviso, 2 erro
        void Add(int nivel, string msg) { linhas.Add(msg); pior = Math.Max(pior, nivel); }

        try
        {
            // 1. CNPJ (algoritmo)
            var cnpj = new string(TxtCnpj.Text.Where(char.IsDigit).ToArray());
            if (cnpj.Length == 14 && Documentos.CnpjValido(cnpj)) Add(0, "✓ CNPJ válido");
            else Add(2, cnpj.Length == 14 ? "✗ CNPJ com dígito verificador errado" : "✗ CNPJ incompleto");

            // Modo RECIBO (sem emissão): certificado/CSC/emissor não são exigidos —
            // testar e reprovar por eles confundiria (a loja ESCOLHEU não emitir).
            var modoRecibo = ModoRecibo;
            if (modoRecibo)
                Add(1, "ℹ Modo RECIBO (sem emissão fiscal): certificado, CSC e emissor não são exigidos");

            // 2. Certificado (abre? validade? CNPJ bate?)
            var caminhoCert = _pfxEscolhido ?? (File.Exists(ArqCert) ? ArqCert : null);
            var producao = _ambiente == 1;
            if (modoRecibo) { /* pula certificado/CSC/emissor — segue pros testes de rede */ }
            else
            if (caminhoCert is null || TxtSenhaPfx.Password.Length == 0)
                Add(producao ? 2 : 1, producao
                    ? "✗ Sem certificado/senha — produção NÃO emite nota"
                    : "⚠ Sem certificado (ok em homologação, obrigatório pra produção)");
            else
            {
                try
                {
                    using var c = new X509Certificate2(caminhoCert, TxtSenhaPfx.Password);
                    var dias = (int)(c.NotAfter - DateTime.Now).TotalDays;
                    if (dias < 0) Add(2, $"✗ Certificado VENCIDO em {c.NotAfter:dd/MM/yyyy}");
                    else if (dias <= 30) Add(1, $"⚠ Certificado vence em {dias} dias ({c.NotAfter:dd/MM/yyyy}) — renove");
                    else Add(0, $"✓ Certificado ok (válido até {c.NotAfter:dd/MM/yyyy})");
                    var cnpjCert = CnpjDoCertificado(c);
                    if (cnpjCert is not null && cnpj.Length == 14)
                    {
                        if (cnpjCert == cnpj) Add(0, "✓ CNPJ do certificado confere com a loja");
                        else if (cnpjCert[..8] == cnpj[..8])
                            Add(0, $"✓ Certificado da matriz/outra filial ({Documentos.Formatar(cnpjCert)}) — mesma empresa, aceito");
                        else Add(2, $"✗ Certificado de OUTRA EMPRESA ({Documentos.Formatar(cnpjCert)}) — raiz do CNPJ não confere");
                    }
                }
                catch { Add(2, "✗ Senha do certificado incorreta"); }
            }

            // 3. CSC + ID (a SEFAZ só valida o CSC de verdade na 1ª emissão)
            if (!modoRecibo)
            {
                if (TxtCsc.Password.Length >= 16) Add(0, "✓ CSC preenchido (validação final acontece na 1ª nota)");
                else Add(producao ? 2 : 1, TxtCsc.Password.Length == 0
                    ? (producao ? "✗ CSC vazio — produção NÃO gera o QR Code" : "⚠ CSC vazio (necessário pra emitir)")
                    : "⚠ CSC muito curto — confira no portal da SEFAZ");
                if (!TxtIdCsc.Text.Trim().All(char.IsDigit) || TxtIdCsc.Text.Trim().Length == 0)
                    Add(1, "⚠ ID do CSC deve ser numérico (ex.: 000001)");
            }

            // 4. Emissor fiscal local (o vigia sobe junto com o PDV — MAS só nas
            // máquinas de caixa, onde C:\kiosk\agent está instalado; ver Agente.cs)
            if (modoRecibo) { /* recibo não usa emissor */ }
            else if (!File.Exists(@"C:\kiosk\agent\pdv-agent.cjs"))
            {
                Add(1, "⚠ O programa que emite a nota não está instalado nesta máquina. É normal num PC "
                     + "que não é o caixa da loja. Se as vendas forem sair DAQUI, ele precisa ser instalado "
                     + "antes de ligar a emissão — senão a venda grava e a nota não sai.");
            }
            else
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                try
                {
                    var r = await http.GetAsync("http://127.0.0.1:4610/health");
                    Add(r.IsSuccessStatusCode ? 0 : 1, r.IsSuccessStatusCode
                        ? "✓ Emissor fiscal local no ar"
                        : $"⚠ Emissor local respondeu HTTP {(int)r.StatusCode}");
                }
                catch { Add(1, "⚠ Emissor fiscal local fora do ar — feche e abra o PDV (o vigia religa em 30s)"); }
            }

            // O "endereço do servidor" saiu daqui junto com o campo: `api_base` não é
            // lido por nada no PDV, e testar um endereço que ninguém usa era dar um ✗
            // (ou um ✓) sobre coisa nenhuma.
            //
            // Pareamento também não entra nesta bateria: ele é o passo 5 e tem status
            // próprio lá — acusar "não pareado" aqui era ruído óbvio.
        }
        catch (Exception ex)
        {
            Add(2, "✗ Teste interrompido: " + ex.Message);
        }
        finally
        {
            TxtStatusTeste.Text = string.Join("\n", linhas);
            TxtStatusTeste.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[
                pior == 2 ? "Erro" : pior == 1 ? "TextoFraco" : "Ok"];
            BtnTestarTudo.IsEnabled = true;
        }
    }

    // ── GRAVAÇÃO (porta única) ──────────────────────────────────────────────

    private void Salvar(object sender, RoutedEventArgs e)
    {
        try
        {
            // O assistente é quem sabe o que falta em CADA passo — inclusive nos que não
            // estão na tela (reconfigurando dá pra pular direto pro passo 4). Falta algo:
            // a tela PULA pro passo culpado com o motivo, em vez de um erro no rodapé
            // falando de um campo que o operador não está vendo.
            if (AssistenteConfig.PrimeiroBloqueio(Coletar()) is { } b)
            {
                IrPara(b.Passo);
                TxtErro.Text = b.Motivo;
                TxtErro.Visibility = Visibility.Visible;
                return;
            }

            var loja = TxtLoja.Text.Trim();
            var cnpj = new string(TxtCnpj.Text.Where(char.IsDigit).ToArray());
            var ie = AssistenteConfig.NormalizarIe(TxtIe.Text);
            // As validações abaixo repetem o assistente de propósito: elas são a
            // invariante do BANCO. Quem chega aqui já passou pelo bloqueio da tela;
            // se um dia um caminho novo não passar, ainda assim não grava torto.
            if (loja.Length < 2) throw new InvalidOperationException("Informe o nome da loja.");
            if (cnpj.Length != 14) throw new InvalidOperationException("O CNPJ precisa ter 14 dígitos.");
            // Dígito verificador AQUI, não na Rejeição 207 da SEFAZ com cliente no balcão.
            if (!Documentos.CnpjValido(cnpj))
                throw new InvalidOperationException("CNPJ inválido — os dígitos verificadores não conferem.");
            if (!int.TryParse(TxtSerie.Text.Trim(), out var serie) || serie < 1 || serie > 999)
                throw new InvalidOperationException("Série deve ser um número de 1 a 999.");
            // "Só recibo": a venda NÃO chama o emissor e o papel sai como recibo (SEM VALOR
            // FISCAL). O ambiente da SEFAZ fica em homologação por segurança — se religarem
            // a emissão sem revisar, nada sobe pra produção.
            var modoRecibo = ModoRecibo;
            var ambiente = modoRecibo ? 2 : _ambiente;

            // INTEGRAÇÃO É OBRIGATÓRIA (decisão do dono): sem parear com o painel,
            // vendas e notas ficariam presas neste PC — o Salvar não conclui.
            if (!LerSegredos().ContainsKey("nuvemEmail"))
                throw new InvalidOperationException(
                    "Pareamento obrigatório: gere o código de 6 dígitos no painel e use o botão " +
                    "\"Parear com o painel\" (passo 5) antes de salvar.");

            using var cx = Banco.Abrir();
            using var tx = cx.BeginTransaction();

            var temAdmin = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM operador WHERE id='_admin_'", transaction: tx) > 0;
            var adminNasceuPadrao = false;

            // ADMINISTRADOR DA LOJA (dono) — só quando ainda não há nenhum operador.
            // Ele entra com TODOS os privilégios e a senha dele passa a ser a senha
            // desta tela de configuração (o "_admin_" espelha o hash do dono —
            // morre o 1234 padrão).
            if (BlocoOperador.Visibility == Visibility.Visible)
            {
                var nome = TxtOpNome.Text.Trim();
                var pin = TxtOpPin.Text.Trim();
                var cpfOp = Documentos.SoDigitos(TxtOpCpf.Text);
                if (nome.Length < 2) throw new InvalidOperationException("Informe o nome do administrador (dono).");
                // CPF é o login: sem ele, a abertura de caixa não tem dono de verdade
                if (!Documentos.CpfValido(cpfOp))
                    throw new InvalidOperationException("CPF do administrador inválido — ele é o login dele no caixa.");
                if (!Operadores.PinValido(pin)) throw new InvalidOperationException("A senha deve ter de 4 a 6 dígitos.");
                var (h, s) = Operadores.GerarHash(pin);
                cx.Execute("""
                    INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,cpf,ativo,atualizado)
                    VALUES (@Id,@N,@H,@S,'gerente',@Cpf,1,@Em)
                    """, new { Id = Guid.NewGuid().ToString(), N = nome, H = h, S = s, Cpf = cpfOp, Em = DateTime.Now.ToString("o") }, tx);
                // a senha do DONO é a senha da configuração (upsert do _admin_ espelhando o hash)
                cx.Execute("""
                    INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,ativo,atualizado)
                    VALUES ('_admin_',@N,@H,@S,'gerente',0,@Em)
                    ON CONFLICT(id) DO UPDATE SET nome=@N, pin_hash=@H, pin_salt=@S, atualizado=@Em
                    """, new { N = "Administrador (" + nome + ")", H = h, S = s, Em = DateTime.Now.ToString("o") }, tx);
                Caixa.Auditar(cx, tx, "admin_definido", null, null, $"dono {nome} — senha da configuração é a dele");
            }
            else if (!temAdmin)
            {
                // instalação sem bloco de operador (já havia operadores) e sem admin:
                // fallback raro — nasce 1234 pra tela não ficar aberta a qualquer um.
                var (h, s) = Operadores.GerarHash("1234");
                cx.Execute("""
                    INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,ativo,atualizado)
                    VALUES ('_admin_','Administrador',@H,@S,'gerente',0,@Em)
                    """, new { H = h, S = s, Em = DateTime.Now.ToString("o") }, tx);
                Caixa.Auditar(cx, tx, "senha_admin_padrao", null, null, "sem dono cadastrado — senha 1234");
                adminNasceuPadrao = true;
            }

            var agora = DateTime.Now.ToString("o");
            cx.Execute("""
                INSERT INTO terminal (id, terminal_uuid, loja_id, loja_nome, cnpj, serie_nfce, ambiente, api_base, criado_em)
                VALUES (1, @Uuid, @Loja, @Loja, @Cnpj, @Serie, @Amb, @Api, @Em)
                ON CONFLICT(id) DO UPDATE SET loja_nome=@Loja, cnpj=@Cnpj, serie_nfce=@Serie,
                                              ambiente=@Amb, api_base=@Api
                """,
                new { Uuid = Guid.NewGuid().ToString(), Loja = loja, Cnpj = cnpj, Serie = serie,
                      Amb = ambiente, Api = _apiBase, Em = agora }, tx);

            Caixa.Auditar(cx, tx, _jaConfigurado ? "config_alterada" : "config_inicial", null, null,
                $"serie={serie} ambiente={(ambiente == 1 ? "producao" : "homologacao")}");
            tx.Commit();

            // Fora da tabela `terminal` porque `Migrar()` não faz ALTER: coluna nova só
            // existiria em máquina nova, e a que já roda ficaria sem. Vale pra impressora,
            // pra largura do papel e pra inscrição estadual.
            var impressora = ImpressoraEscolhida();
            if (AssistenteConfig.PodeGravarImpressora(_impressorasProntas, impressora))
            {
                if (impressora is null) cx.Execute("DELETE FROM config WHERE chave='impressora'");
                else Vendas.GravarConfig(cx, "impressora", impressora);
            }
            if (ie.Length == 0) cx.Execute("DELETE FROM config WHERE chave='loja_ie'");
            else Vendas.GravarConfig(cx, "loja_ie", ie);
            Vendas.GravarConfig(cx, "papel_mm", AssistenteConfig.TextoPapel(PapelEscolhido()));
            // A impressão lê a largura desta propriedade; sem isto o cupom só sairia na
            // bobina nova depois de reiniciar o PDV ("salvou mas não mudou").
            Impressao.PapelMm = AssistenteConfig.TextoPapel(PapelEscolhido());
            _papelGravado = true;   // gravada no banco: o Sair não devolve mais a antiga
            Vendas.GravarConfig(cx, "modo_fiscal", modoRecibo ? "recibo" : "nfce");
            Vendas.GravarConfig(cx, "imprimir_automatico", ChkImprimirAuto.IsChecked == false ? "0" : "1");
            var impComanda = ImpressoraComandaEscolhida();
            if (AssistenteConfig.PodeGravarImpressora(_comandasProntas, impComanda))
            {
                if (impComanda is null) cx.Execute("DELETE FROM config WHERE chave='kds_comanda_impressora'");
                else Vendas.GravarConfig(cx, "kds_comanda_impressora", impComanda);
            }
            Vendas.GravarConfig(cx, "kds_comanda_auto", ChkComandaAuto.IsChecked == true ? "1" : "0");
            GravarTef(cx);
            _tefSalvo = true;

            // Segredos fora do banco e cifrados pela máquina (DPAPI). Em produção o
            // certificado é obrigatório — sem ele não sai nota.
            var seg = LerSegredos();
            if (_pfxEscolhido is not null)
            {
                Directory.CreateDirectory(PastaSegredos);
                File.Copy(_pfxEscolhido, ArqCert, true);
            }
            if (TxtSenhaPfx.Password.Length > 0) seg["senhaPfx"] = TxtSenhaPfx.Password;
            if (TxtCsc.Password.Length > 0) seg["csc"] = TxtCsc.Password;
            seg["idCsc"] = TxtIdCsc.Text.Trim() is { Length: > 0 } i ? i : "000001";
            GravarSegredos(seg);

            if (!modoRecibo && ambiente == 1 && (!File.Exists(ArqCert) || !seg.ContainsKey("csc")))
            {
                Dialogo.Avisar(Window.GetWindow(this)!, "Falta o certificado",
                    "Salvo, mas sem o certificado e/ou o CSC o caixa não consegue emitir nota em produção.", "erro");
            }
            if (adminNasceuPadrao)
                Dialogo.Avisar(Window.GetWindow(this)!, "Senha desta tela: 1234",
                    "A senha de administrador nasceu com o padrão 1234. " +
                    "Troque assim que possível (pelo painel, quando existir, ou pelo suporte).", "ok");
            Concluiu?.Invoke();
        }
        catch (Exception ex)
        {
            TxtErro.Text = ex.Message;
            TxtErro.Visibility = Visibility.Visible;
        }
    }

    // ── PASSO 4: MAQUININHA ─────────────────────────────────────────────────

    private static readonly string[] ChavesTef =
    {
        "tef_habilitado", "tef_provedor", "tef_paygo_pasta", "tef_paygo_registro", "tef_paygo_empresa",
        "tef_paygo_rede", "tef_paygo_rede_pix", "tef_paygo_imprimir_vias", "tef_perguntar_parcelas", "tef_serial_pos",
        "tef_cpay_ambiente", "tef_cpay_terminal", "tef_cpay_pessoa",
        // As redes também: o Testar grava o que está na tela, e sair sem salvar tem que
        // devolver a rede que estava valendo — rede trocada é cobrança recusada.
        "tef_cpay_adquirente", "tef_cpay_adquirente_pix",
    };
    private readonly Dictionary<string, string?> _tefOriginal = new();
    private bool _tefGravadoPeloTeste;   // Testar/ADM gravaram sem Salvar
    private bool _tefSalvo;              // Salvar passou por GravarTef

    /// <summary>Sair sem salvar depois de Testar/ADM: restaura as chaves TEF como estavam.</summary>
    private void RestaurarTefSeNaoSalvou()
    {
        if (!_tefGravadoPeloTeste || _tefSalvo) return;
        try
        {
            using var cx = Banco.Abrir();
            foreach (var (k, v) in _tefOriginal)
            {
                if (v is null) cx.Execute("DELETE FROM config WHERE chave=@C", new { C = k });
                else Vendas.GravarConfig(cx, k, v);
            }
            Servicos.RecarregarTef();
        }
        catch { /* melhor esforço: o pior caso é a config do teste ficar — e ela está na tela */ }
    }

    /// <summary>Bloqueia navegação e provedor enquanto um comando TEF (ATV) está em voo — trocar o provedor com o PayGo ocupado deixaria dois clientes na mesma pasta.</summary>
    private void TravarTef(bool ocupado)
    {
        BtnTestarPayGo.IsEnabled = !ocupado;
        BtnTestarCpay.IsEnabled = !ocupado;
        BtnSalvar.IsEnabled = !ocupado;
        BtnVoltar.IsEnabled = !ocupado && _passo != PassoConfig.Loja;
        BtnSair.IsEnabled = !ocupado;
        BtnAvancar.IsEnabled = false;                 // Revalidar devolve se o passo estiver válido
        foreach (var op in OpcoesTef) op.IsEnabled = !ocupado;
        foreach (var aba in Abas) aba.IsEnabled = !ocupado && _jaConfigurado;
        if (!ocupado) Revalidar();
    }

    /// <summary>Enche uma caixa de rede com a lista fechada e seleciona o que está gravado (nunca -1: ver RedesPayGo.Indice).</summary>
    private static void EncherRedes(ComboBox combo, IReadOnlyList<OpcaoRede> opcoes, string? gravado)
    {
        combo.Items.Clear();
        foreach (var op in opcoes) combo.Items.Add(op);
        combo.SelectedIndex = RedesPayGo.Indice(opcoes, gravado);
    }

    private static string RedeEscolhida(ComboBox combo) => (combo.SelectedItem as OpcaoRede)?.Valor ?? "";

    /// <summary>
    /// Chaves `tef_*` em `config` — é exatamente o que Servicos.Tef() e Caixa.FormasContadas
    /// leem. Campo em branco APAGA a chave (o cliente usa o padrão dele). No fim, zera o cache
    /// do provedor: salvar sem isso era "salvou mas não mudou" até reiniciar o PDV.
    /// </summary>
    private void GravarTef(SqliteConnection cx)
    {
        var modo = TefModo;
        Vendas.GravarConfig(cx, "tef_habilitado", modo <= 0 ? "0" : "1");
        Vendas.GravarConfig(cx, "tef_provedor", modo switch { 2 => "paygo", 3 => "controlpay", _ => "nuvem" });
        void Chave(string chave, string valor)
        {
            valor = valor.Trim();
            if (valor.Length == 0) cx.Execute("DELETE FROM config WHERE chave=@C", new { C = chave });
            else Vendas.GravarConfig(cx, chave, valor);
        }
        // ControlPay: ambiente/terminal/pessoa em config; chave e senha técnica no cofre DPAPI.
        // O ambiente é a marca do sandbox, desligada por padrão: quem é novo nasce em
        // produção, quem está em homologação continua lá até desmarcar — e o resumo do
        // fim denuncia sandbox, para não virar configuração invisível.
        Vendas.GravarConfig(cx, "tef_cpay_ambiente", ChkCpaySandbox.IsChecked == true ? "sandbox" : "producao");
        Chave("tef_cpay_terminal", TxtCpayTerminal.Text);
        Chave("tef_cpay_pessoa", TxtCpayPessoa.Text);
        Chave("tef_cpay_adquirente", RedeEscolhida(CboCpayRede));
        Chave("tef_cpay_adquirente_pix", RedeEscolhida(CboCpayRedePix));
        {
            var segTef = LerSegredos();
            var chaveCpay = PwdCpayChave.Password.Trim();
            // aceita colada URL-encoded (%2f, %2b, %3d) — guardamos decodificada e codificamos ao enviar
            if (chaveCpay.Contains('%')) { try { chaveCpay = Uri.UnescapeDataString(chaveCpay); } catch { } }
            if (chaveCpay.Length > 0) segTef["cpayChave"] = chaveCpay; else segTef.Remove("cpayChave");
            var senhaTec = PwdCpaySenha.Password.Trim();
            if (senhaTec.Length > 0) segTef["cpaySenhaTecnica"] = senhaTec; else segTef.Remove("cpaySenhaTecnica");
            GravarSegredos(segTef);
        }
        Chave("tef_paygo_pasta", TxtPayGoPasta.Text);
        Chave("tef_paygo_registro", TxtPayGoRegistro.Text);
        Chave("tef_paygo_empresa", TxtPayGoEmpresa.Text);
        Chave("tef_paygo_rede", RedeEscolhida(CboPayGoRede));
        Chave("tef_paygo_rede_pix", RedeEscolhida(CboPayGoRedePix));
        Vendas.GravarConfig(cx, "tef_paygo_imprimir_vias", ChkPayGoVias.IsChecked == false ? "0" : "1");
        Vendas.GravarConfig(cx, "tef_perguntar_parcelas", ChkTefParcelas.IsChecked == true ? "1" : "0");
        Chave("tef_serial_pos", TxtTefSerial.Text);
        Servicos.RecarregarTef();
    }

    private void TefMudou(object sender, RoutedEventArgs e) { PintarBlocosTef(); Revalidar(); }

    private RadioButton[] OpcoesTef => new[] { OpTefNenhum, OpTefNuvem, OpTefPayGo, OpTefControlPay };

    /// <summary>
    /// Qual TEF este caixa usa, no mesmo código que a config já gravava
    /// (0 sem maquininha · 1 POS · 2 PayGo · 3 ControlPay). Virou cartão em vez de
    /// dropdown a pedido do dono: no balcão, ver as opções vale mais que escondê-las.
    /// </summary>
    private int TefModo
    {
        get => OpTefControlPay?.IsChecked == true ? 3
             : OpTefPayGo?.IsChecked == true ? 2
             : OpTefNuvem?.IsChecked == true ? 1 : 0;
        set
        {
            OpTefNenhum.IsChecked = value == 0;
            OpTefNuvem.IsChecked = value == 1;
            OpTefPayGo.IsChecked = value == 2;
            OpTefControlPay.IsChecked = value == 3;
        }
    }

    /// <summary>Revelação progressiva do bloco do provedor. Guard de null: os cartões disparam no InitializeComponent.</summary>
    private void PintarBlocosTef()
    {
        if (BlocoPayGo is null || BlocoTefNuvem is null || BlocoControlPay is null
            || BlocoTefOpcoes is null || OpTefControlPay is null) return;
        var modo = TefModo;
        BlocoPayGo.Visibility = Se(modo == 2);
        BlocoControlPay.Visibility = Se(modo == 3);
        BlocoTefNuvem.Visibility = Se(modo == 1);
        BlocoTefOpcoes.Visibility = Se(modo is 2 or 3);
    }

    /// <summary>
    /// Testa a conexão com a PayGo usando os campos DA TELA (instância efêmera, não grava
    /// nada) e explica o resultado em português de gente: quem vai cobrar, em qual
    /// maquininha, e o que pedir a eles se faltar algo. Nenhuma mensagem carrega a chave.
    /// </summary>
    private async void TestarControlPay(object sender, RoutedEventArgs e)
    {
        TravarTef(true);
        StatusTef("Falando com a PayGo…", null);
        try
        {
            var chave = PwdCpayChave.Password.Trim();
            if (chave.Contains('%')) { try { chave = Uri.UnescapeDataString(chave); } catch { } }
            if (chave.Length == 0) { StatusTef("Falta a chave de integração — pegue no portal do ControlPay, em Integrações.", "Erro"); return; }
            if (TxtCpayPessoa.Text.Trim().Length == 0) { StatusTef("Falta o ID da pessoa. Ele fica no portal do ControlPay, junto do seu login.", "Erro"); return; }

            var producao = ChkCpaySandbox.IsChecked != true;
            var ondeEstou = producao ? "produção" : "teste (sandbox)";
            using var cli = new ClienteControlPay(new OpcoesControlPay(
                OpcoesControlPay.UrlDoAmbiente(producao ? "producao" : "sandbox"),
                chave, PwdCpaySenha.Password.Trim(), TxtCpayTerminal.Text.Trim(), TxtCpayPessoa.Text.Trim()));
            var terminais = await cli.ListarTerminaisAsync(CancellationToken.None);

            if (terminais.Count == 0)
            {
                StatusTef($"A PayGo aceitou a chave, mas esta conta não tem nenhuma maquininha em {ondeEstou}." +
                    "\n\nO que fazer: peça à PayGo para vincular a maquininha da loja a esta conta.", "Erro");
                return;
            }

            // O campo pode ter sobrado de outra conta (o número do sandbox, por exemplo).
            // Nesse caso o certo é corrigir sozinho, e DIZER que corrigiu — deixar o número
            // velho ali só produz um "escolha um terminal" que ninguém entende.
            var digitado = TxtCpayTerminal.Text.Trim();
            var comMaquininha = terminais.Where(t => t.TerminalFisico is not null).ToList();
            var trocou = false;
            if (terminais.All(t => t.Id != digitado) && comMaquininha.Count == 1)
            {
                TxtCpayTerminal.Text = comMaquininha[0].Id;
                trocou = digitado.Length > 0;
            }
            var escolhido = terminais.FirstOrDefault(t => t.Id == TxtCpayTerminal.Text.Trim());

            var lista = string.Join("\n", terminais.Select(t =>
                $"   • {t.Id} — {t.Nome}" + (t.TerminalFisico is null
                    ? "  (sem maquininha vinculada)"
                    : $"  ·  maquininha {t.TerminalFisico}  ·  instalação {t.InstalacaoId}")));

            if (escolhido is null)
            {
                StatusTef($"Conectado à PayGo em {ondeEstou}. Encontrei {(terminais.Count == 1 ? "1 caixa" : terminais.Count + " caixas")} nesta conta:\n\n" +
                    lista + "\n\nO que fazer: escreva um desses números no campo \"ID do terminal\" aqui em cima e salve.", "Erro");
                return;
            }

            // Pendências que só a PayGo resolve no cadastro dela — o PDV não contorna
            // nenhuma, então o texto tem que dizer exatamente o que pedir a eles.
            var pendencias = new List<string>();
            if (escolhido.TerminalFisico is null)
                pendencias.Add("• Este caixa não tem maquininha vinculada, então a cobrança não tem para onde ir.\n" +
                               "  Peça à PayGo para vincular a maquininha da loja a este terminal.");
            if (!escolhido.AguardaTef)
                pendencias.Add("• A PayGo não ligou o \"aguarda TEF\" neste caixa. Sem isso a cobrança sai daqui\n" +
                               "  mas não acende na maquininha: a venda fica esperando e acaba expirando.\n" +
                               "  Peça à PayGo para ligar o \"aguarda TEF\".");
            if (!escolhido.VendaPorValor)
                pendencias.Add("• A \"venda por valor\" está desligada neste caixa. É ela que deixa o PDV mandar\n" +
                               "  o valor da compra. Peça à PayGo para ligar.");

            var cabeca = $"Conectado à PayGo em {ondeEstou}.\n\n" +
                $"Este caixa vai cobrar pelo terminal {escolhido.Id} ({escolhido.Nome})" +
                (escolhido.TerminalFisico is null
                    ? ".\n"
                    : $",\nna maquininha {escolhido.TerminalFisico}, da instalação {escolhido.InstalacaoId}.\n") +
                (trocou ? $"\nObs.: o campo estava com o número {digitado}, que não existe nesta conta — troquei pelo certo.\n" : "");

            if (pendencias.Count == 0)
            {
                StatusTef(cabeca + "\nEstá tudo pronto para cobrar no cartão. Clique em Salvar para manter." +
                    (producao ? "" : "\n\nAtenção: você ainda está no ambiente de TESTE. Nenhuma cobrança aqui é de verdade."), "Ok");
                return;
            }

            StatusTef(cabeca + "\nFalta a PayGo acertar isto no cadastro deles:\n\n" +
                string.Join("\n\n", pendencias) +
                "\n\nPode salvar assim mesmo: o número do caixa fica guardado e volta a funcionar\nassim que eles acertarem.", "Erro");
        }
        catch (Exception ex) { StatusTef("Não consegui falar com a PayGo: " + ex.Message, "Erro"); }
        finally { TravarTef(false); }
    }

    /// <summary>
    /// ATV contra a pasta digitada. Grava as chaves TEF antes (o teste é do que está NA TELA,
    /// como o de impressão) — o Salvar exige pareamento/CNPJ e não pode ser pré-requisito de
    /// um teste de comunicação.
    /// </summary>
    private async void TestarPayGo(object sender, RoutedEventArgs e)
    {
        TravarTef(true);
        StatusTef("Chamando o PayGo (ATV)… até 7 s.", null);
        try
        {
            if (TefModo != 2) { StatusTef("Selecione \"PayGo (pinpad no caixa)\" acima para testar.", "Erro"); return; }
            using (var cx = Banco.Abrir()) GravarTef(cx);
            _tefGravadoPeloTeste = true;
            if (Servicos.PayGo() is not { } cli) { StatusTef("TEF desligado — selecione o PayGo e tente de novo.", "Erro"); return; }
            var ok = await cli.AtivoAsync(CancellationToken.None);
            StatusTef(ok
                ? $"✓ PayGo respondeu em {cli.PastaReq} — pronto para cobrar. (Salve para manter esta configuração.)"
                : $"✗ {ClientePayGo.MsgTefNaoResponde} Pasta: {cli.PastaReq}. Confira se o PayGo Windows está aberto e se a pasta é a mesma configurada nele.",
                ok ? "Ok" : "Erro");
        }
        catch (Exception ex) { StatusTef("✗ " + ex.Message, "Erro"); }
        finally { TravarTef(false); }
    }

    private void StatusTef(string texto, string? tom)
    {
        TxtStatusTef.Text = texto;
        TxtStatusTef.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[tom ?? "TextoFraco"];
    }

    // ── tema ────────────────────────────────────────────────────────────────
    private bool _carregandoTema;

    private void TemaSelecionado(object sender, SelectionChangedEventArgs e)
    {
        if (_carregandoTema || BlocoJanelaTema is null) return;
        BlocoJanelaTema.Visibility = Se(CboTema.SelectedIndex == 2);
        using var cx = Banco.Abrir();
        Vendas.GravarConfig(cx, "tema", CboTema.SelectedIndex switch { 1 => "claro", 2 => "auto", _ => "escuro" });
        Aparencia.Aplicar(Aparencia.Resolver(cx));   // preview imediato: ver é decidir
    }

    private void JanelaTemaMudou(object sender, RoutedEventArgs e)
    {
        if (_carregandoTema) return;
        using var cx = Banco.Abrir();
        Vendas.GravarConfig(cx, "tema_claro_de", TxtTemaDe.Text.Trim());
        Vendas.GravarConfig(cx, "tema_claro_ate", TxtTemaAte.Text.Trim());
        if (CboTema.SelectedIndex == 2) Aparencia.Aplicar(Aparencia.Resolver(cx));
    }
}

// ══ O MIOLO DO ASSISTENTE, SEM WPF ══════════════════════════════════════════

/// <summary>Os passos, na ordem. O valor numérico É a posição na trilha (Tag do XAML).</summary>
public enum PassoConfig
{
    Loja = 0,
    Fiscal = 1,
    Impressora = 2,
    Maquininha = 3,
    Pareamento = 4,
    /// <summary>Tela final: o que ficou configurado, antes de gravar.</summary>
    Resumo = 5,
}

/// <summary>
/// Retrato do que está na tela do assistente. É texto cru, do jeito que o operador
/// digitou — quem valida é o <see cref="AssistenteConfig"/>, e é ele que os testes
/// exercitam (a tela de verdade precisa de WPF, banco e DPAPI para existir).
/// </summary>
public sealed record DadosAssistente
{
    // 1 · Loja
    public string Loja { get; init; } = "";
    public string Cnpj { get; init; } = "";
    public string Ie { get; init; } = "";
    public bool Recibo { get; init; }

    // 2 · Nota fiscal
    public string Serie { get; init; } = "";
    public int Ambiente { get; init; } = 2;      // 1 produção · 2 homologação (vem do pareamento)
    public bool TemCertificado { get; init; }

    // 3 · Impressora
    public string? Impressora { get; init; }
    public bool ImprimirAuto { get; init; } = true;
    public double PapelMm { get; init; } = 80;
    public string? ImpressoraComanda { get; init; }
    public bool ComandaAuto { get; init; }

    // 4 · Maquininha (0 sem · 1 POS · 2 PayGo · 3 ControlPay)
    public int Tef { get; init; }
    public string PayGoPasta { get; init; } = "";
    public string PayGoRedeCartao { get; init; } = "";
    public string PayGoRedePix { get; init; } = "";
    public string CpayChave { get; init; } = "";
    public string CpayPessoa { get; init; } = "";
    public string CpayTerminal { get; init; } = "";
    public string CpayRedeCartao { get; init; } = "";
    public string CpayRedePix { get; init; } = "";
    public bool CpaySandbox { get; init; }
    public string PosSerial { get; init; } = "";

    // 5 · Pareamento
    public bool Pareado { get; init; }
    public bool PedeAdmin { get; init; }
    public string AdminNome { get; init; } = "";
    public string AdminCpf { get; init; } = "";
    public string AdminPin { get; init; } = "";
}

/// <summary>Uma linha da tela de resumo. <see cref="Atencao"/> não é erro: é escolha que precisa ser vista.</summary>
public sealed record LinhaResumo(string Titulo, string Valor, bool Atencao = false);

/// <summary>Uma bobina oferecida no passo da impressora. O rótulo traduz milímetros em colunas.</summary>
public sealed record OpcaoPapel(double Mm, int Colunas)
{
    public override string ToString() =>
        $"{Mm.ToString("0", CultureInfo.InvariantCulture)} mm  ·  {Colunas} colunas por linha";
}

/// <summary>
/// As regras do assistente de configuração, longe do WPF: o que cada passo exige, o
/// que o resumo mostra e como a inscrição estadual é normalizada.
///
/// Está separado da tela por um motivo prático: <c>Configuracao</c> só existe com
/// janela, banco SQLite e cofre DPAPI de pé, e por isso nunca foi testável. O que
/// decide se uma instalação pode continuar não pode depender disso.
/// </summary>
public static class AssistenteConfig
{
    /// <summary>Quantos passos o operador percorre (o resumo não conta — ele é a revisão).</summary>
    public const int TotalPassos = 5;

    /// <summary>Valor da inscrição estadual de quem não tem uma. É o que sai impresso no cupom.</summary>
    public const string IeIsento = "ISENTO";

    public static string Nome(PassoConfig p) => p switch
    {
        PassoConfig.Loja => "Loja",
        PassoConfig.Fiscal => "Nota fiscal",
        PassoConfig.Impressora => "Impressora",
        PassoConfig.Maquininha => "Maquininha",
        PassoConfig.Pareamento => "Pareamento",
        _ => "Tudo pronto",
    };

    /// <summary>"Passo 3 de 5" — o operador precisa saber quanto falta, não só onde está.</summary>
    public static string Indicador(PassoConfig p) =>
        p == PassoConfig.Resumo ? "REVISÃO" : $"PASSO {(int)p + 1} DE {TotalPassos}";

    public static string Explicacao(PassoConfig p) => p switch
    {
        PassoConfig.Loja => "Quem é a loja e o que sai no papel de cada venda.",
        PassoConfig.Fiscal => "O que a SEFAZ precisa para autorizar as notas deste caixa.",
        PassoConfig.Impressora => "Onde o cupom sai e em que largura de bobina.",
        PassoConfig.Maquininha => "Como este caixa cobra cartão e PIX.",
        PassoConfig.Pareamento => "Ligar este caixa ao painel — é o que faz vendas e notas subirem.",
        _ => "Confira antes de gravar. Dá pra voltar em qualquer passo pela trilha aqui em cima.",
    };

    /// <summary>
    /// O que impede o Avançar neste passo — <c>null</c> quando pode seguir. A mensagem
    /// vai inteira para a tela: "campo obrigatório" não diz a ninguém o que fazer, então
    /// cada uma diz onde achar o valor ou qual é a saída.
    /// </summary>
    public static string? Bloqueio(PassoConfig passo, DadosAssistente d) => passo switch
    {
        PassoConfig.Loja => BloqueioLoja(d),
        PassoConfig.Fiscal => BloqueioFiscal(d),
        // A impressora nunca bloqueia: tudo aqui tem padrão que funciona (impressora do
        // Windows, bobina de 80 mm) e nenhuma escolha errada impede o caixa de vender.
        PassoConfig.Impressora => null,
        PassoConfig.Maquininha => BloqueioMaquininha(d),
        PassoConfig.Pareamento => BloqueioPareamento(d),
        _ => PrimeiroBloqueio(d) is { } b ? $"{Indicador(b.Passo)} · {Nome(b.Passo)}: {b.Motivo}" : null,
    };

    /// <summary>
    /// O primeiro passo que ainda bloqueia, na ordem do assistente. É o que o Salvar
    /// consulta: reconfigurando dá pra pular direto pro passo 4, e o que falta pode
    /// estar num passo que nem foi aberto.
    /// </summary>
    public static (PassoConfig Passo, string Motivo)? PrimeiroBloqueio(DadosAssistente d)
    {
        for (var i = 0; i < TotalPassos; i++)
            if (Bloqueio((PassoConfig)i, d) is { } motivo) return ((PassoConfig)i, motivo);
        return null;
    }

    private static string? BloqueioLoja(DadosAssistente d)
    {
        if (d.Loja.Trim().Length < 2) return "Informe o nome da loja.";
        var cnpj = Documentos.SoDigitos(d.Cnpj);
        if (cnpj.Length != 14)
            return $"O CNPJ precisa ter 14 dígitos — faltam {14 - cnpj.Length}.";
        if (!Documentos.CnpjValido(cnpj))
            return "CNPJ inválido: os dígitos verificadores não conferem. Confira número por número.";

        // IE: exigida para emitir NFC-e (a SEFAZ quer a inscrição ou a palavra ISENTO no
        // emitente). Em "Só recibo" ela é opcional — mas se foi digitada, tem que estar
        // certa, senão o cupom sai com um número que não é de ninguém.
        var ie = NormalizarIe(d.Ie);
        if (ie.Length == 0)
            return d.Recibo ? null : "Informe a inscrição estadual — ou toque em ISENTO se a loja não tem uma.";
        return IeValida(ie) ? null
            : "Inscrição estadual inválida: são de 8 a 14 dígitos. Se a loja não tem IE, toque em ISENTO.";
    }

    private static string? BloqueioFiscal(DadosAssistente d)
    {
        // Série vale nos dois modos: ela identifica o CAIXA, e é o que evita numeração
        // duplicada no dia em que a loja ligar a NFC-e.
        if (!int.TryParse(d.Serie.Trim(), out var serie) || serie < 1 || serie > 999)
            return "A série deste caixa deve ser um número de 1 a 999 (o pareamento traz a certa).";
        // Certificado e CSC NÃO bloqueiam de propósito: em homologação dá pra configurar o
        // caixa inteiro antes de o contador entregar o certificado, e o teste desta tela
        // (mais o aviso do Salvar) já dizem que produção sem eles não emite nota.
        return null;
    }

    private static string? BloqueioMaquininha(DadosAssistente d) => d.Tef switch
    {
        2 when d.PayGoPasta.Trim().Length == 0 =>
            "Informe a pasta do PayGo — tem que ser a MESMA configurada no PayGo Windows (ex.: C:\\PAYGO).",
        3 when d.CpayChave.Trim().Length == 0 =>
            "Falta a chave de integração do ControlPay (portal → Integrações → Chaves de integração).",
        3 when d.CpayPessoa.Trim().Length == 0 =>
            "Falta o ID da pessoa do ControlPay — ele fica no portal, junto do seu login.",
        3 when d.CpayTerminal.Trim().Length == 0 =>
            "Falta o ID do terminal. O botão \"Testar conexão com a PayGo\" lista os desta conta.",
        _ => null,
    };

    private static string? BloqueioPareamento(DadosAssistente d)
    {
        // Sem pareamento as vendas e as notas ficam presas neste PC. É decisão do dono
        // que isto seja obrigatório, e é o Salvar que a faz valer.
        if (!d.Pareado)
            return "Pareamento obrigatório: gere o código de 6 dígitos no painel e toque em \"Parear com o painel\".";
        if (!d.PedeAdmin) return null;
        if (d.AdminNome.Trim().Length < 2) return "Informe o nome do administrador (o dono da loja).";
        if (!Documentos.CpfValido(Documentos.SoDigitos(d.AdminCpf)))
            return "CPF do administrador inválido — é com ele que o dono entra no caixa.";
        if (!Operadores.PinValido(d.AdminPin.Trim()))
            return "A senha do administrador deve ter de 4 a 6 dígitos.";
        return null;
    }

    /// <summary>
    /// O que ficou configurado, em uma linha por assunto. Existe porque o assistente
    /// mostra uma tela por vez: sem a revisão no fim, ninguém consegue conferir o
    /// conjunto — e as escolhas que mais custam caro (só recibo, homologação, sandbox
    /// da maquininha) são justamente as que ficam invisíveis depois de gravadas.
    /// </summary>
    public static IReadOnlyList<LinhaResumo> Resumo(DadosAssistente d)
    {
        var papel = Impressao.Papel.De(TextoPapel(d.PapelMm));
        var ie = NormalizarIe(d.Ie);
        var linhas = new List<LinhaResumo>
        {
            new("Loja", $"{d.Loja.Trim()} · {Documentos.Formatar(Documentos.SoDigitos(d.Cnpj))}"
                        + (ie.Length > 0 ? $" · IE {ie}" : "")),
            d.Recibo
                ? new LinhaResumo("Nota fiscal",
                    "SÓ RECIBO — nenhuma nota é emitida e o papel sai SEM VALOR FISCAL.", true)
                : new LinhaResumo("Nota fiscal",
                    $"Cupom fiscal (NFC-e) · série {d.Serie.Trim()} · "
                    + (d.Ambiente == 1 ? "produção" : "HOMOLOGAÇÃO — as notas não valem")
                    + (d.TemCertificado ? "" : " · sem certificado"),
                    d.Ambiente != 1 || !d.TemCertificado),
            new("Impressora do cupom",
                $"{d.Impressora ?? "padrão do Windows"} · bobina de {papel.BobinaMm.ToString("0", CultureInfo.InvariantCulture)} mm "
                + $"({papel.Colunas} colunas)"
                + (d.ImprimirAuto ? "" : " · impressão automática DESLIGADA"),
                !d.ImprimirAuto),
            new("Comanda da cozinha", d.ComandaAuto
                ? $"Imprime sozinha em {d.ImpressoraComanda ?? "padrão do Windows"} quando o pedido chega."
                : "Só pelo botão 🖨 do KDS."),
            new("Maquininha", ResumoTef(d), d.Tef == 3 && d.CpaySandbox),
            new("Pareamento", d.Pareado
                ? "✓ Pareado com o painel — vendas e notas sobem no Sincronizar."
                : "Ainda NÃO pareado.", !d.Pareado),
        };
        return linhas;
    }

    private static string ResumoTef(DadosAssistente d)
    {
        static string Rede(string valor, string oQue) =>
            valor.Trim().Length == 0 ? $"{oQue}: a PayGo escolhe" : $"{oQue}: {valor.Trim()}";

        return d.Tef switch
        {
            1 => "Venda no POS" + (d.PosSerial.Trim().Length > 0
                    ? $" · maquininha {d.PosSerial.Trim()}" : " · terminal padrão da conta"),
            2 => $"PayGo (pinpad no caixa) · pasta {d.PayGoPasta.Trim()} · "
                 + Rede(d.PayGoRedeCartao, "cartão") + " · " + Rede(d.PayGoRedePix, "PIX"),
            3 => $"ControlPay · terminal {d.CpayTerminal.Trim()} · "
                 + Rede(d.CpayRedeCartao, "cartão") + " · " + Rede(d.CpayRedePix, "PIX")
                 + (d.CpaySandbox ? " · AMBIENTE DE TESTE (sandbox): nenhuma cobrança é de verdade" : ""),
            _ => "Sem maquininha — o caixa registra a forma de pagamento, mas não cobra o cartão.",
        };
    }

    // ── INSCRIÇÃO ESTADUAL ──────────────────────────────────────────────────

    /// <summary>
    /// Tira pontuação e caixa: "012.345.678/0098" e "0123456780098" são a mesma IE, e
    /// qualquer variação de "isento" vira a palavra <see cref="IeIsento"/>, que é o que
    /// a SEFAZ e o cupom esperam ver escrito.
    /// </summary>
    public static string NormalizarIe(string? bruto)
    {
        var s = (bruto ?? "").Trim().ToUpperInvariant();
        if (s.Length == 0) return "";
        if (s.StartsWith("ISENT", StringComparison.Ordinal)) return IeIsento;
        return new string(s.Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>
    /// Aceita ISENTO ou de 8 a 14 dígitos. Não há validação de dígito verificador aqui de
    /// propósito: cada estado tem a sua regra (MG tem 13 dígitos, SP 12 com letra na
    /// produção rural, RJ 8) e recusar uma IE VÁLIDA na tela de instalação é pior que
    /// aceitar uma torta — a nota rejeitada diz qual é o problema, a tela travada não.
    /// O que a conferência pega é o erro de digitação grosso (três dígitos, letra solta).
    /// </summary>
    public static bool IeValida(string? bruto)
    {
        var ie = NormalizarIe(bruto);
        if (ie == IeIsento) return true;
        if (ie.Length is < 8 or > 14) return false;
        return ie.Count(char.IsDigit) >= 8;
    }

    // ── IMPRESSORA ──────────────────────────────────────────────────────────

    /// <summary>
    /// Se o Salvar pode escrever a chave da impressora — ou se tem que deixar quieto o
    /// que já está gravado.
    ///
    /// A lista de impressoras chega DEPOIS da tela (enumerar filas de rede trava no
    /// timeout de cada servidor de impressão fora do ar, segundos por servidor). Até ela
    /// chegar, o combo tem uma opção só: "(padrão do Windows)" — que é exatamente a
    /// escolha que APAGA a impressora da loja. E reconfigurando o Salvar fica no rodapé
    /// desde o passo 1: abrir a tela e salvar em um segundo é gesto normal de quem só
    /// queria mexer noutra coisa, e a loja acordaria imprimindo o cupom na impressora
    /// errada sem ninguém ter tocado no campo.
    ///
    /// Regra: sem lista e sem escolha, "não sei" — e "não sei" nunca apaga configuração.
    /// </summary>
    public static bool PodeGravarImpressora(bool listaPronta, string? escolhida)
        => listaPronta || escolhida is not null;

    // ── LARGURA DO PAPEL ────────────────────────────────────────────────────

    /// <summary>Milímetros no formato que <c>config['papel_mm']</c> guarda (ponto, nunca vírgula).</summary>
    public static string TextoPapel(double mm) => mm.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>As bobinas oferecidas, já com o número de colunas de cada uma.</summary>
    public static IReadOnlyList<OpcaoPapel> OpcoesPapel() =>
        Impressao.BobinasSuportadas
            .Select(mm => new OpcaoPapel(mm, Impressao.Papel.De(TextoPapel(mm)).Colunas))
            .ToList();

    /// <summary>
    /// Índice da bobina gravada, para o <c>SelectedIndex</c>. NUNCA devolve -1: caixa vazia
    /// na tela de configuração se lê como "nada escolhido", e o que a impressão faz sem
    /// escolha é imprimir em 80 mm — então é o 80 mm que tem que aparecer selecionado.
    /// </summary>
    public static int IndicePapel(string? gravado)
    {
        var papel = Impressao.Papel.De(gravado);
        var ops = OpcoesPapel();
        for (var i = 0; i < ops.Count; i++)
            if (Math.Abs(ops[i].Mm - papel.BobinaMm) < 0.5) return i;
        return 0;
    }
}
