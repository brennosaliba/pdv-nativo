using System.Diagnostics;
using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Xps;
using Pdv.Nucleo;
using QRCoder;

namespace Pdv;

/// <summary>Uma linha da tabela de itens do cupom.</summary>
public sealed record ItemCupom(
    string Codigo, string Descricao, Quantidade Qtd, string Unidade,
    Dinheiro Unitario, Dinheiro Total);

/// <summary>
/// Uma linha do bloco de pagamento. `Forma` já vem legível ("Dinheiro", "Cartão de débito").
///
/// <c>Valor</c> é o <b>vPag DA NOTA</b>, não o que o cliente entregou. Na fase 1 isso
/// significa Σ Pagamentos == total da venda (seção 4.2 do plano: o motor não emite
/// &lt;vTroco&gt;, então a SEFAZ exige Σ vPag == vNF). A ambiguidade aqui produzia dois
/// cupons errados e nada no código impedia nenhum dos dois: quem ligasse o caminho
/// natural (venda_pagamento.valor_cent, que guarda o RECEBIDO) imprimia
/// "VALOR TOTAL 25,00 / Dinheiro 50,00", contradizendo o XML; quem ligasse o vPag e
/// ainda imprimisse troco na mesma linha dizia "Dinheiro 25,00 / Troco 25,00", que é
/// absurdo pro cliente. O que o cliente entregou vai em <see cref="DadosCupom.Recebido"/>.
/// </summary>
public sealed record PagamentoCupom(string Forma, Dinheiro Valor);

/// <summary>
/// Tudo que o DANFE NFC-e precisa ter impresso. É um retrato fechado do desfecho da
/// venda: quem monta isto é a tela de pagamento, com a resposta do agente fiscal já
/// na mão. Reimprimir é reimprimir ESTE objeto — nada é recalculado na impressão.
/// </summary>
/// <param name="EmitenteNome">Vem de <c>ResultadoEmissao.Emit</c>, que é anulável inteiro — por isso todos os campos do emitente são anuláveis aqui, e <see cref="Impressao.ImprimirAsync"/> recusa o cupom quando faltam.</param>
/// <param name="Emissao">
/// Hora local do ENVIO da emissão — capture imediatamente ANTES do <c>POST /nfce/emitir</c>.
/// Não é o dhEmi de verdade, e não dá pra prometer que seja: o contrato do agente (1.1.2)
/// devolve <c>dhRecbto</c> e não devolve <c>dhEmi</c>. Preenchendo com <c>DateTime.Now</c>
/// da hora da impressão, o round-trip (até 25 s) faz uma venda às 23:59:5x sair no papel
/// com DATA diferente da do XML. Quem quiser o valor exato tem que ler o XML
/// (<c>GET /nfce/xml?chave=</c>) antes de imprimir.
/// </param>
/// <param name="QrCode">Conteúdo EXATO do QR devolvido pelo agente. Não remontar aqui.</param>
/// <param name="TpAmb">
/// 1 = produção, 2 = homologação, null = o emissor não informou. Um campo só governa o
/// carimbo "SEM VALOR FISCAL" E a URL do portal de consulta: eram dois campos independentes
/// derivados desta mesma informação, e dava pra imprimir URL de produção com carimbo de
/// homologação — ou, pior, cupom de homologação SEM carimbo, ou seja, um papel que se
/// apresenta como documento fiscal válido e não é. Ausência erra pro lado inofensivo.
/// </param>
/// <param name="Total">Total que o PDV somou (Σ dos itens). Só é impresso quando <paramref name="VNf"/> não veio.</param>
/// <param name="VNf">
/// vNF que o emissor devolveu (<c>ResultadoEmissao.VNF</c>) — é ESTE o total que o DANFE
/// tem que mostrar, porque é o que está no XML autorizado. Null só onde a resposta ainda
/// não existe; aí cai no <paramref name="Total"/> do PDV, que é a melhor aproximação disponível.
/// </param>
/// <param name="Recebido">
/// O que o cliente entregou (venda_pagamento.valor_cent em dinheiro). Zero fora de dinheiro.
/// O troco é derivado daqui (<c>Recebido − total</c>) e não é campo próprio de propósito:
/// dois campos podem se contradizer, e troco impresso que não bate com o recebido é reclamação
/// no balcão. O XML não leva &lt;vTroco&gt; nenhum (seção 4.2).
/// </param>
/// <param name="Documento">CPF/CNPJ do consumidor, só dígitos, ou null.</param>
/// <param name="Protocolo">Nulo em contingência — lá ainda não existe autorização.</param>
/// <param name="ProtocoloEm">dhRecbto. Quando nulo, sai só o número do protocolo.</param>
public sealed record DadosCupom(
    string? EmitenteNome,
    string? EmitenteCnpj,
    string? EmitenteIe,
    string? EmitenteEndereco,
    int Numero,
    int Serie,
    string? Chave,
    DateTime Emissao,
    string? QrCode,
    int? TpAmb,
    IReadOnlyList<ItemCupom> Itens,
    Dinheiro Total,
    decimal? VNf,
    IReadOnlyList<PagamentoCupom> Pagamentos,
    Dinheiro Recebido,
    string? Documento,
    bool Contingencia,
    string? Operador,
    string? Protocolo = null,
    DateTime? ProtocoloEm = null,
    // RECIBO (sem emissão fiscal): loja optou por operar sem NFC-e — o papel sai
    // como recibo simples, SEM cabeçalho DANFE, chave, QR, protocolo ou carimbos
    // fiscais, e com o aviso "SEM VALOR FISCAL" explícito.
    bool Recibo = false);

/// <summary>
/// Impressão do cupom da NFC-e na bobina configurada em <c>config['papel_mm']</c>
/// (58, 80 ou 100 mm; 80 quando não há escolha), SEM diálogo nenhum.
///
/// Caminho escolhido: montar um visual WPF e entregá-lo ao spooler por
/// <c>XpsDocumentWriter</c>. Quem abre janela é <c>PrintDialog.ShowDialog()</c>;
/// <c>PrintQueue.CreateXpsDocumentWriter(fila).Write(visual, ticket)</c> não abre nada —
/// e num caixa com fila de cliente qualquer diálogo é uma venda travada.
///
/// Duas regras que o resto do sistema depende:
///  - <see cref="ImprimirAsync"/> NUNCA lança. A venda já aconteceu e o dinheiro já
///    entrou quando esta classe é chamada; cupom que não sai é um recado pro operador,
///    não uma exceção que derruba a tela.
///  - nada aqui usa recurso do app (o tema é escuro): cupom é preto no branco, sempre.
/// </summary>
public static class Impressao
{
    /// <summary>1 mm em DIP — o WPF mede em 1/96 de polegada, a bobina em milímetros.</summary>
    private const double MM = 96.0 / 25.4;

    // ── BOBINA (largura configurável) ────────────────────────────────────────────

    /// <summary>
    /// Área imprimível de cada bobina que a loja pode escolher. TABELA, e não fórmula, de
    /// propósito: a área útil é o número de pontos da cabeça térmica (203 dpi = 8 pontos/mm)
    /// e ele NÃO é proporcional à bobina — 58 mm imprime 48 mm (384 pontos, 5 mm de margem
    /// mecânica de cada lado) e 80 mm imprime 72 mm (576 pontos, 4 mm de cada lado). Não
    /// existe fórmula que acerte as duas, e errar para MAIS é texto cortado no papel — que
    /// é exatamente o defeito que a largura configurável existe para não ter.
    /// </summary>
    private static readonly (double Bobina, double Util)[] AreaImprimivel =
    {
        (58.0, 48.0),    // 384 pontos
        (80.0, 72.0),    // 576 pontos — a única largura que este PDV imprimiu até aqui
        (100.0, 90.0),   // 720 pontos
    };

    /// <summary>
    /// Largura de UMA coluna de texto: a Font A da térmica tem 12 pontos de largura e, a
    /// 203 dpi (8 pontos/mm), isso dá 1,5 mm por caractere. É a régua da IMPRESSORA, e é
    /// ela que explica os dois clássicos com a mesma conta — 72 ÷ 1,5 = as 48 colunas de
    /// 80 mm, 48 ÷ 1,5 = as 32 colunas de 58 mm. A fonte da montagem (Consolas em
    /// <see cref="Corpo"/>) mede ~1,45 mm por caractere, ou seja, cabe dentro da coluna:
    /// quem aperta é o papel, e é o papel que manda. <see cref="LarguraDoTextoMm"/> é o
    /// que permite conferir isso na fonte de verdade em vez de acreditar nesta conta.
    /// </summary>
    private const double ColunaMm = 1.5;

    /// <summary>
    /// A geometria de UM papel: a bobina, quanto dela a cabeça térmica alcança, e quantas
    /// colunas de texto cabem lá. Os três andam juntos de propósito — coluna que não deriva
    /// da MESMA largura que desenha a página é como o texto sai cortado.
    /// </summary>
    public readonly record struct Papel(double BobinaMm, double UtilMm, int Colunas)
    {
        /// <summary>
        /// Traduz o valor cru de <c>config['papel_mm']</c>. Ausente, ilegível ou de bobina
        /// fora da tabela cai em <see cref="PapelPadrao"/>: 80 mm é o que este PDV sempre
        /// imprimiu, então valor estranho na configuração não faz ninguém regredir — e
        /// <see cref="PapelAtual"/> manda o valor estranho para a auditoria.
        /// </summary>
        public static Papel De(string? papelMm)
        {
            var pedida = Milimetros(papelMm);
            foreach (var (bobina, util) in AreaImprimivel)
                // Meio milímetro de tolerância: "80", "80.0" e "80,0" são a mesma bobina.
                if (Math.Abs(bobina - pedida) < 0.5)
                    // PISO, nunca arredondamento: meia coluna a mais é meia coluna fora do papel.
                    return new Papel(bobina, util, (int)(util / ColunaMm));
            return PapelPadrao;
        }
    }

    /// <summary>
    /// Papel de 80 mm — 72 mm úteis, 48 colunas. É exatamente o que estava fixo no código
    /// antes de a largura virar configuração, e é para cá que cai quem não escolheu bobina
    /// nenhuma (a esmagadora maioria hoje) ou escolheu uma que não existe.
    /// </summary>
    public static readonly Papel PapelPadrao = new(80.0, 72.0, 48);

    /// <summary>
    /// As bobinas que a tela de Configuração deve oferecer. Sai da MESMA tabela que decide
    /// a área imprimível para o combo nunca oferecer largura que a impressão não sabe montar.
    /// </summary>
    public static IReadOnlyList<double> BobinasSuportadas { get; } =
        AreaImprimivel.Select(a => a.Bobina).ToArray();

    /// <summary>
    /// Largura da bobina como está em <c>config['papel_mm']</c> — texto cru, do jeito que
    /// saiu do banco (pode vir nulo, com vírgula ou com lixo; quem valida é <see cref="Papel.De"/>).
    ///
    /// Injetada, e não recebida por parâmetro, pelo mesmo motivo de <see cref="Auditar"/>:
    /// esta classe não abre o banco — e nem podia, porque o cupom é montado numa thread STA
    /// própria. E porque a largura é atributo da IMPRESSORA, não do documento: passá-la em
    /// cada uma das chamadas de impressão espalhadas pelas telas é convidar a que uma delas
    /// esqueça, e a que esquecer imprime 48 colunas numa bobina de 58 mm, ou seja, cortado.
    ///
    /// O app liga na abertura e quando a Configuração grava:
    ///     Impressao.PapelMm = Vendas.Config(cx, "papel_mm");
    /// </summary>
    public static string? PapelMm { get; set; }

    /// <summary>
    /// A geometria em vigor. Cada trabalho de impressão tira UM retrato disto e usa o mesmo
    /// do começo ao fim: mudar a configuração no meio de um cupom trocaria a largura da
    /// página sem trocar as colunas do texto — e aí sim sairia cortado.
    /// </summary>
    public static Papel PapelAtual
    {
        get
        {
            var bruto = PapelMm;                 // um retrato: a propriedade é mutável
            var papel = Papel.De(bruto);

            // Bobina que não existe na tabela não pode sumir em silêncio: a loja escolheu
            // 58 mm, o papel continua saindo em 48 colunas e ninguém tem por onde descobrir.
            // Só avisa quando o valor MUDA — isto é lido a cada impressão e um aviso por
            // cupom viraria ruído. (Sem trava: corrida aqui custa uma linha repetida na
            // auditoria, nada mais.)
            if (!string.IsNullOrWhiteSpace(bruto)
                && Math.Abs(Milimetros(bruto) - papel.BobinaMm) >= 0.5
                && Interlocked.Exchange(ref _papelRecusado, bruto) != bruto)
                Avisar($"config papel_mm = '{bruto}' não é uma bobina conhecida " +
                       $"({string.Join("/", BobinasSuportadas.Select(b => b.ToString("0", CultureInfo.InvariantCulture)))} mm); " +
                       $"imprimindo em {papel.BobinaMm.ToString("0", CultureInfo.InvariantCulture)} mm.");
            return papel;
        }
    }

    /// <summary>Último valor de <see cref="PapelMm"/> já denunciado — evita repetir o aviso a cada cupom.</summary>
    private static string? _papelRecusado;

    /// <summary>
    /// Lê o número da configuração. Aceita vírgula E ponto porque o valor pode ter sido
    /// digitado à mão no banco (suporte, script de instalação) e "58,0" é o que um teclado
    /// brasileiro produz. Ilegível devolve 0, que não casa com bobina nenhuma e cai no padrão.
    /// </summary>
    private static double Milimetros(string? bruto)
    {
        var s = (bruto ?? "").Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) ? mm : 0;
    }

    /// <summary>
    /// Quanto ocupa, em milímetros, uma linha cheia de <paramref name="colunas"/> caracteres
    /// na fonte do corpo do cupom. MEDIDO na fonte de verdade, não estimado: <see cref="ColunaMm"/>
    /// é a régua da cabeça térmica, e o que garante que o texto cabe é a fonte caber dentro
    /// dela. É por aqui que a suíte confere, bobina a bobina, que as colunas derivadas não
    /// estouram a área imprimível — conta que antes vivia só num comentário.
    /// </summary>
    public static double LarguraDoTextoMm(int colunas)
    {
        if (colunas <= 0) return 0;
        var linha = new FormattedText(new string('0', colunas), Br, FlowDirection.LeftToRight,
            new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            Corpo, Brushes.Black, 1.0);
        return linha.WidthIncludingTrailingWhitespace / MM;
    }

    // ── TIPOGRAFIA ───────────────────────────────────────────────────────────────

    private const double Corpo = 10;
    private const double Miudo = 8.5;
    private const double Destaque = 15;

    /// <summary>Avanço depois do conteúdo, pra o cupom passar da faca/serrilha antes de ser cortado.</summary>
    private const double AvancoMm = 12.0;

    /// <summary>
    /// Lado do QR do consumidor. 40 mm é o tamanho que lê bem em celular sobre impressão
    /// térmica; não acompanha a bobina de propósito — QR maior não lê melhor, só come papel.
    /// </summary>
    private const double QrLadoMm = 40.0;

    private static readonly CultureInfo Br = new("pt-BR");
    private static readonly FontFamily Mono = new("Consolas");
    private static readonly FontFamily Sans = new("Segoe UI");

    /// <summary>
    /// Depois de entregar ao spooler, quanto tempo vigiar o trabalho antes de desistir
    /// de dar um veredito. Não é o tempo máximo de impressão — é o tempo de diagnóstico.
    /// </summary>
    private static readonly TimeSpan JanelaDeVigia = TimeSpan.FromSeconds(15);

    /// <summary>Intervalo entre as consultas à fila durante a vigia.</summary>
    private const int IntervaloVigiaMs = 250;

    /// <summary>
    /// A PRIMEIRA consulta é mais cedo de propósito: quanto menor a janela cega, menor a
    /// chance de o trabalho nascer e sumir entre duas consultas (cupom curto em térmica
    /// local imprime em fração de segundo) e a vigia nunca chegar a vê-lo.
    /// </summary>
    private const int PrimeiraVigiaMs = 60;

    /// <summary>
    /// Quanto esperar o trabalho APARECER na fila antes de parar de procurá-lo.
    /// <c>Write()</c> volta quando o spooler ACEITA o trabalho; ele só entra no snapshot
    /// do EnumJobs um instante depois.
    /// </summary>
    private static readonly TimeSpan EsperaPeloJob = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Falhas SEGUIDAS de leitura da fila antes de desistir do diagnóstico. Uma
    /// PrintQueueException isolada no <c>Refresh()</c> de impressora de rede é rotina e
    /// some na volta seguinte; fila permanentemente ilegível (driver simples, sem SNMP)
    /// não vai melhorar esperando.
    /// </summary>
    private const int FalhasDeLeituraToleradas = 5;

    /// <summary>Portal de consulta por chave — PRODUÇÃO (tpAmb=1).</summary>
    private const string PortalProducao = "https://portalsped.fazenda.mg.gov.br/portalnfce";

    /// <summary>Portal de HOMOLOGAÇÃO (tpAmb=2) — e também o destino quando o tpAmb não veio.</summary>
    private const string PortalHomologacao = "https://hportalsped.fazenda.mg.gov.br/portalnfce";

    /// <summary>
    /// Canal de diagnóstico pro que NÃO é falha de impressão mas não pode sumir: cupom
    /// que saiu sem QR, fila que ficou ilegível e a vigia não conseguiu confirmar a saída.
    /// Nada disso pode virar retorno de erro de <see cref="ImprimirAsync"/> — erro leva a
    /// tela pra fase ErroImpressao e o botão "Reimprimir" só produziria outro cupom com o
    /// mesmo defeito (ou um segundo cupom fiscal da mesma nota).
    ///
    /// O app liga isto em <c>Caixa.Auditar</c>. Fica como callback pra impressão não
    /// depender do banco (e pra não abrir conexão SQLite dentro da thread STA do cupom).
    /// Cuidado: é invocado na thread de impressão, não na thread da UI.
    /// </summary>
    public static Action<string>? Auditar { get; set; }

    private static void Avisar(string mensagem)
    {
        // Diagnóstico nunca derruba impressão: a venda já aconteceu e o dinheiro já entrou.
        try { Auditar?.Invoke(mensagem); } catch { /* assinante quebrado não é problema do cupom */ }
    }

    // ── CONFIGURAÇÃO (tela de Configuração) ──────────────────────────────────────

    /// <summary>
    /// Versão assíncrona de <see cref="Impressoras"/> — é ESTA que a tela de Configuração
    /// deve usar. Enumerar <c>Connections</c> bloqueia no timeout de RPC/SMB de cada
    /// servidor de impressão inalcançável (segundos por servidor); chamada direto no
    /// clique, congela a UI do WPF exatamente como fazia antes de a impressão sair da
    /// thread de UI.
    /// </summary>
    public static Task<IReadOnlyList<string>> ImpressorasAsync() => Task.Run(Impressoras);

    /// <summary>Mesma história de <see cref="ImpressorasAsync"/>: a consulta ao spooler pode travar.</summary>
    public static Task<string?> ImpressoraPadraoAsync() => Task.Run(ImpressoraPadrao);

    /// <summary>
    /// Filas locais e conexões de rede, pro combo de seleção de impressora.
    /// SÍNCRONA e potencialmente lenta — na UI, use <see cref="ImpressorasAsync"/>.
    /// </summary>
    public static IReadOnlyList<string> Impressoras()
    {
        try
        {
            using var servidor = new LocalPrintServer();
            using var filas = servidor.GetPrintQueues(new[]
            {
                EnumeratedPrintQueueTypes.Local,
                EnumeratedPrintQueueTypes.Connections,
            });
            var nomes = new List<string>();
            foreach (var f in filas)
                using (f) nomes.Add(f.FullName);
            nomes.Sort(StringComparer.CurrentCultureIgnoreCase);
            return nomes;
        }
        catch
        {
            // Spooler parado ou sem permissão. A tela de configuração mostra a lista
            // vazia e o operador escolhe "padrão do Windows" — não é caso de erro.
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Impressora padrão do Windows, ou null se não houver nenhuma.
    /// Também síncrona — na UI, use <see cref="ImpressoraPadraoAsync"/>.
    /// </summary>
    public static string? ImpressoraPadrao()
    {
        try
        {
            using var f = LocalPrintServer.GetDefaultPrintQueue();
            return f.FullName;
        }
        catch { return null; }
    }

    // ── DESTINO POR FINALIDADE (cupom fiscal ≠ comanda do delivery) ───────────────
    //
    // 29/08 — pedido do dono: "pode ser q delivery use uma e cupom fiscal use outra,
    // entao sao configuracoes individuais". A loja tem a térmica do balcão (cupom) e a
    // da expedição (comanda). Antes disto a impressora da comanda já existia
    // (`kds_comanda_impressora`), mas a LARGURA era uma só para as duas — a global
    // `papel_mm` —, então a comanda numa bobina de 58 mm saía cortada mesmo com a
    // impressora certa escolhida.

    /// <summary>
    /// Onde UM papel sai: a fila do Windows (null = padrão do Windows) e a bobina dela.
    /// Os dois andam juntos porque errar o par é o defeito clássico: mandar para a
    /// térmica de 58 mm um texto montado para 80 mm imprime cortado, e cortado no fim
    /// da linha — onde está o que importa.
    /// </summary>
    public readonly record struct Destino(string? Impressora, Papel Papel);

    /// <summary>Vazio e nulo são a mesma coisa na configuração: "padrão do Windows".</summary>
    private static string? Fila(string? nome)
        => string.IsNullOrWhiteSpace(nome) ? null : nome.Trim();

    /// <summary>
    /// A loja escolheu impressora PRÓPRIA para a comanda do delivery?
    ///
    /// <paramref name="separada"/> é <c>config['kds_comanda_separada']</c>, a caixinha
    /// da tela de Configuração. Ausente é o caso de quem nunca abriu a opção — e aí a
    /// resposta vem de <paramref name="impressoraComanda"/>: quem JÁ tinha escolhido uma
    /// impressora de comanda antes desta caixinha existir continua com ela ligada, senão
    /// a atualização silenciosamente jogaria a comanda de volta na bobina do cupom.
    /// </summary>
    public static bool ComandaSeparada(string? separada, string? impressoraComanda)
        => (separada ?? "").Trim() switch
        {
            "1" => true,
            "0" => false,
            // Nunca respondida: liga só se já havia uma impressora de comanda escolhida.
            _ => !string.IsNullOrWhiteSpace(impressoraComanda),
        };

    /// <summary>Onde sai o CUPOM da venda — a impressora e a bobina de sempre.</summary>
    public static Destino DestinoCupom(string? impressoraCupom, string? papelCupom)
        => new(Fila(impressoraCupom), Papel.De(papelCupom));

    /// <summary>
    /// Onde sai a COMANDA do delivery, a partir das chaves cruas do <c>config</c>.
    ///
    /// A REGRA DE OURO: quem nunca abriu a opção continua imprimindo onde já imprime
    /// hoje — ou seja, na impressora do CUPOM, não na "padrão do Windows". Essa era a
    /// armadilha da versão anterior: a loja tinha `impressora = EPSON TM-T20` e nenhuma
    /// impressora de comanda, e a comanda ia parar na padrão do Windows (que numa
    /// máquina de caixa costuma ser o "Microsoft Print to PDF" — um papel que nunca sai).
    ///
    /// Com a opção ligada, cada finalidade tem impressora E bobina próprias; a bobina da
    /// comanda em branco cai na do cupom, que é o que valia antes de ela existir.
    /// </summary>
    public static Destino DestinoComanda(string? impressoraCupom, string? papelCupom,
        string? separada, string? impressoraComanda, string? papelComanda)
    {
        if (!ComandaSeparada(separada, impressoraComanda))
            return DestinoCupom(impressoraCupom, papelCupom);
        return new Destino(Fila(impressoraComanda),
            Papel.De(string.IsNullOrWhiteSpace(papelComanda) ? papelCupom : papelComanda));
    }

    // ── O SELETOR DA ABA DELIVERY (29/08 — relato do dono) ───────────────────────
    //
    // "apos instalado, nao mostra a impressora no PDV na aba de delivery, somente no
    // dash principal". A escolha JÁ existia — só que trancada no passo "Impressora" do
    // assistente de Configuração. Quem opera a cozinha vai procurar onde a comanda sai
    // JUNTO dos pedidos, não num assistente de instalação, e não achando conclui que o
    // PDV não deixa escolher.
    //
    // O seletor do cabeçalho do Delivery grava nas MESMAS chaves do assistente
    // (kds_comanda_separada / kds_comanda_impressora / kds_comanda_papel_mm). Duas fontes
    // de verdade sobre onde o papel sai divergem no primeiro dia, e quem descobre é a
    // cozinha, com o pedido na mão. As regras moram AQUI, fora do WPF, porque o que não
    // pode errar é a ESCOLHA — o desenho do combo a suíte não precisa ver.

    /// <summary>
    /// Uma linha do seletor de impressora da comanda.
    ///
    /// <paramref name="Impressora"/> <c>null</c> é "a mesma do cupom" (a opção de cima),
    /// <c>""</c> é a padrão do Windows escolhida de propósito, e qualquer outro valor é o
    /// nome da fila. Os três estados num campo só, e não num campo + bandeira, porque
    /// bandeira e nome discordando é estado impossível que compila.
    /// </summary>
    public readonly record struct OpcaoComanda(string Rotulo, string? Impressora);

    /// <summary>
    /// As opções do seletor e QUAL delas está valendo agora.
    ///
    /// A primeira é sempre "mesma do cupom", e ela diz o NOME da impressora do cupom
    /// entre parênteses: a pergunta do operador é "sai em qual?", e "mesma do cupom"
    /// sozinho não responde — foi exatamente o que o dono não conseguiu ler na tela.
    ///
    /// A lista NÃO oferece "padrão do Windows" como escolha nova, de propósito: numa
    /// máquina de caixa a padrão do Windows costuma ser o "Microsoft Print to PDF", um
    /// papel que nunca sai. Ela só aparece quando o caixa JÁ está nesse estado — porque
    /// aí esconder seria o seletor mentindo que a comanda sai na do cupom.
    ///
    /// Impressora gravada que sumiu da lista (desligada, renomeada, servidor de impressão
    /// fora do ar) entra marcada com "(não encontrada)", igual ao assistente: sumir com
    /// ela apagaria a configuração da loja no primeiro toque no seletor.
    /// </summary>
    public static (IReadOnlyList<OpcaoComanda> Opcoes, int Selecionada) OpcoesComanda(
        IEnumerable<string>? impressoras, string? impressoraCupom,
        string? separada, string? impressoraComanda)
    {
        var lista = new List<OpcaoComanda>
        {
            new($"Mesma do cupom ({Fila(impressoraCupom) ?? "padrão do Windows"})", null),
        };
        foreach (var nome in impressoras ?? Array.Empty<string>())
            if (Fila(nome) is { } fila && !lista.Any(o => o.Impressora == fila))
                lista.Add(new OpcaoComanda(fila, fila));

        var separadaAgora = ComandaSeparada(separada, impressoraComanda);
        var escolhida = Fila(impressoraComanda);
        if (separadaAgora)
        {
            if (escolhida is null) lista.Add(new OpcaoComanda("(padrão do Windows)", ""));
            else if (!lista.Any(o => o.Impressora == escolhida))
                lista.Add(new OpcaoComanda(escolhida + "  (não encontrada)", escolhida));

            var alvo = escolhida ?? "";
            for (var i = 1; i < lista.Count; i++)
                if (lista[i].Impressora == alvo) return (lista, i);
        }
        return (lista, 0);
    }

    /// <summary>
    /// O que a escolha do seletor grava no <c>config</c>: o par
    /// (<c>kds_comanda_separada</c>, <c>kds_comanda_impressora</c>).
    /// <c>Impressora</c> nulo significa "não mexer nessa chave".
    ///
    /// ⚠️ ESCOLHER UMA IMPRESSORA AQUI LIGA A SEPARAÇÃO SOZINHA. Sem isso o operador
    /// escolheria a térmica da expedição, leria o nome dela na tela e a comanda
    /// continuaria saindo na bobina do caixa: escolha sem efeito é PIOR que escolha
    /// ausente, porque ninguém vai procurar de novo — a tela já disse que estava certo.
    /// A caixinha do assistente responde "separar ou não?"; este seletor responde "sai em
    /// qual?", e essa resposta já contém a anterior.
    ///
    /// Voltar para "mesma do cupom" desliga a separação mas NÃO apaga a impressora
    /// gravada: religar vira um toque, e "mesma do cupom" não promete esquecer a escolha
    /// que a loja fez. (Quem quiser apagar de vez tem o assistente.)
    /// </summary>
    public static (string Separada, string? Impressora) GravacaoComanda(OpcaoComanda escolha)
        => escolha.Impressora is null ? ("0", null) : ("1", escolha.Impressora);

    /// <summary>
    /// O rótulo curto do cabeçalho: para onde a comanda está saindo AGORA.
    ///
    /// Na mesma bobina do cupom o texto NOMEIA a impressora — "mesma do cupom (EPSON
    /// TM-T20)" é informação, e foi a falta dela que fez o dono achar que a aba Delivery
    /// não tinha impressora nenhuma. Separada, entra também a bobina: aí a largura é uma
    /// decisão recente e própria, e comanda saindo cortada por bobina errada é defeito
    /// que esta casa já viu no papel.
    /// </summary>
    public static string RotuloDestinoComanda(string? impressoraCupom, string? papelCupom,
        string? separada, string? impressoraComanda, string? papelComanda, int limite = 26)
    {
        if (!ComandaSeparada(separada, impressoraComanda))
            return $"mesma do cupom ({Curto(Fila(impressoraCupom) ?? "padrão do Windows", limite)})";
        var destino = DestinoComanda(impressoraCupom, papelCupom, separada, impressoraComanda, papelComanda);
        return Curto(destino.Impressora ?? "padrão do Windows", limite)
             + " · " + destino.Papel.BobinaMm.ToString("0", CultureInfo.InvariantCulture) + " mm";
    }

    /// <summary>
    /// Nome de fila cabe em "\\SERVIDOR\EPSON TM-T20 Receipt (Cópia 2)"; o cabeçalho do
    /// Delivery não tem essa largura, e o botão empurrando o "Voltar ao caixa" pra fora
    /// da tela seria um estrago maior que o nome abreviado. O nome inteiro fica no
    /// seletor, que é onde se escolhe.
    /// </summary>
    public static string Curto(string nome, int limite)
    {
        nome = nome.Trim();
        if (limite < 2 || nome.Length <= limite) return nome;
        return nome[..(limite - 1)].TrimEnd() + "…";
    }

    // ── IMPRESSÃO ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Imprime o cupom. Devolve <c>null</c> se saiu, ou a mensagem de erro pronta pra
    /// tela se não saiu. Não lança em nenhuma hipótese.
    /// </summary>
    /// <param name="nomeImpressora">Vazio/null = impressora padrão do Windows.</param>
    public static Task<string?> ImprimirAsync(DadosCupom dados, string? nomeImpressora)
    {
        if (dados is null) return Task.FromResult<string?>("Não há cupom para imprimir.");
        try
        {
            // Recusar ANTES de gastar papel é o desfecho barato. Cupom incoerente com o
            // XML autorizado é achado de fiscalização e não se conserta depois de impresso.
            if (Conferir(dados) is { } recusa) return Task.FromResult<string?>(recusa);
        }
        catch (Exception ex)
        {
            // A regra "ImprimirAsync NUNCA lança" vale pro pré-voo também: DadosCupom com
            // lista nula chegaria aqui como NullReferenceException e derrubaria a tela de
            // pagamento com a venda já paga.
            return Task.FromResult<string?>($"Cupom inconsistente, não imprimi: {ex.Message}");
        }
        // UM retrato da bobina por cupom, tirado AQUI e não lá dentro: a montagem do
        // visual e a declaração do tamanho da página são dois lugares diferentes, e ler a
        // configuração duas vezes permitiria que uma troca no meio do caminho desenhasse
        // 48 colunas numa página de 58 mm.
        var papel = PapelAtual;
        return NaFilaAsync(PrioridadeImpressao.Alta,
            () => ComPrazoAsync(EmThreadStaAsync(() => Imprimir(dados, nomeImpressora, papel))));
    }

    /// <summary>Quem tem preferência na impressora quando dois papéis disputam a bobina.</summary>
    public enum PrioridadeImpressao
    {
        /// <summary>Cupom/recibo da venda — é o papel que o cliente está esperando na mão.</summary>
        Alta,
        /// <summary>Via do TEF, comprovante de estorno: importam, mas ninguém está parado por elas.</summary>
        Baixa,
    }

    // A impressora é UMA. Depois de aprovar o cartão saem dois papéis: as vias do TEF
    // (disparadas em segundo plano, sem ninguém esperando) e o cupom da venda. Sem
    // ordem, as vias entram no spooler primeiro e o cupom — o único que o cliente está
    // esperando — sai atrás delas. Aqui o cupom passa na frente: um trabalho de baixa
    // prioridade não começa enquanto houver cupom na fila, e o que já começou termina
    // (não dá para tirar papel do meio do caminho).
    private static readonly SemaphoreSlim Bobina = new(1, 1);
    private static int _altaNaFila;

    private static async Task<string?> NaFilaAsync(PrioridadeImpressao prioridade, Func<Task<string?>> trabalho)
    {
        if (prioridade == PrioridadeImpressao.Alta) Interlocked.Increment(ref _altaNaFila);
        try
        {
            while (prioridade == PrioridadeImpressao.Baixa && Volatile.Read(ref _altaNaFila) > 0)
                await Task.Delay(40).ConfigureAwait(false);
            await Bobina.WaitAsync().ConfigureAwait(false);
            try { return await trabalho().ConfigureAwait(false); }
            finally { Bobina.Release(); }
        }
        finally
        {
            if (prioridade == PrioridadeImpressao.Alta) Interlocked.Decrement(ref _altaNaFila);
        }
    }

    /// <summary>
    /// Teto de tempo da impressão. Ninguém segura o caixa esperando papel.
    /// </summary>
    private const int TempoTotalImpressaoMs = 25_000;

    /// <summary>
    /// Devolve o controle à tela mesmo que a impressão nunca termine.
    ///
    /// Isso NÃO é paranoia: com a impressora padrão sendo "Microsoft Print to PDF" (ou
    /// XPS, ou qualquer porta que peça arquivo), o Write abre uma janela perguntando onde
    /// salvar. Numa thread de segundo plano essa janela é invisível, e a impressão espera
    /// para sempre — com a VENDA JÁ FEITA e a NOTA JÁ AUTORIZADA. O caixa fica preso numa
    /// tela de "aguarde" achando que falhou, e a reação natural do operador é reemitir.
    /// Aconteceu no primeiro teste real.
    ///
    /// A thread travada continua lá (não dá para matar thread em .NET), mas ela é
    /// background e não segura o processo. O que importa é o caixa voltar a vender.
    /// </summary>
    private static async Task<string?> ComPrazoAsync(Task<string?> impressao)
    {
        var vencido = await Task.WhenAny(impressao, Task.Delay(TempoTotalImpressaoMs));
        if (vencido == impressao) return await impressao;
        return $"a impressora não respondeu em {TempoTotalImpressaoMs / 1000}s. " +
               "Se ela estiver configurada como 'Print to PDF' ou similar, há uma janela " +
               "invisível esperando um nome de arquivo — escolha uma impressora térmica.";
    }

    /// <summary>
    /// Portão local: o que não pode virar papel. Devolve a mensagem pro operador, ou null
    /// se está tudo coerente.
    /// </summary>
    private static string? Conferir(DadosCupom d)
    {
        // O emitente vem de ResultadoEmissao.Emit, que é `EmitenteFiscal?` com as QUATRO
        // propriedades `string?` (e vem null inteiro quando o objeto `emit` não veio na
        // resposta). Sem esta recusa, um chamador escrevendo `r.Emit?.Nome ?? ""` compila
        // limpo e imprime cabeçalho em branco, sem CNPJ e sem IE — papel que NÃO é DANFE
        // válido e que ninguém percebe até a fiscalização.
        if (string.IsNullOrWhiteSpace(d.EmitenteNome) || SoDigitos(d.EmitenteCnpj).Length != 14)
            return "Não recebi os dados do emitente — não dá pra imprimir cupom sem emitente.";

        // Σ vPag == vNF é regra absoluta da fase 1 (seção 4.2): o motor não emite <vTroco>,
        // então o bloco de pagamento do papel tem que fechar com o total. Se não fecha, o
        // que está errado é o objeto do cupom — imprimir seria documentar o erro.
        long soma = 0;
        foreach (var p in d.Pagamentos) soma += p.Valor.Centavos;
        if (soma != d.Total.Centavos)
            return $"Os pagamentos do cupom somam R$ {Valor(new Dinheiro(soma))} e o total é " +
                   $"R$ {Valor(d.Total)} — não imprimi um cupom que não fecha.";

        // O total impresso é o vNF DA NOTA. Divergir do total do PDV é possível de verdade
        // (item pesável: Dinheiro.VezesQtd arredonda AwayFromZero, o motor faz a própria
        // conta de vProd sobre os decimais que foram no JSON) e um centavo de diferença
        // entre papel e XML autorizado é achado de fiscalização. Sem tolerância: o certo
        // aqui é travar no balcão e chamar quem conserta, não escolher qual dos dois mente.
        if (d.VNf is { } vnf && Dinheiro.DeReais(vnf).Centavos != d.Total.Centavos)
            return $"O total da nota (R$ {Valor(Dinheiro.DeReais(vnf))}) não bate com o total da " +
                   $"venda (R$ {Valor(d.Total)}). Não imprimi papel que contradiz o XML — chame o suporte.";

        return null;
    }

    /// <summary>
    /// Roda o trabalho numa thread STA dedicada.
    ///
    /// Montar visual WPF exige STA com Dispatcher, mas fazer isso na thread da UI
    /// congelaria a tela pelo tempo do spool. Uma thread própria resolve os dois:
    /// o visual não está preso a nenhuma árvore visual, então não há afinidade a quebrar.
    /// </summary>
    private static Task<string?> EmThreadStaAsync(Func<string?> trabalho)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var thread = new Thread(() =>
            {
                string? r = null;
                try { r = trabalho(); }
                catch (Exception ex) { r = $"Falha inesperada ao imprimir: {ex.Message}"; }
                finally
                {
                    // O Dispatcher nasce sozinho ao criar o primeiro objeto WPF nesta
                    // thread e sobrevive a ela. Sem derrubá-lo, cada cupom deixa um resíduo.
                    //
                    // Este try/catch e a posição do TrySetResult não são preciosismo: o
                    // InvokeShutdown pode lançar, e em .NET 8 exceção não tratada em thread
                    // de BACKGROUND derruba o PROCESSO inteiro. Mesmo que não derrubasse, o
                    // TrySetResult ficando fora do finally deixava o Task nunca completar —
                    // a tela de pagamento presa na fase "Imprimindo" pra sempre, sem timeout
                    // previsto. O TrySetResult tem que ser a ÚLTIMA coisa DENTRO do finally.
                    try { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
                    catch { /* dispatcher já derrubado ou em estado ruim: só vaza recurso, não cancela o cupom */ }
                    tcs.TrySetResult(r);
                }
            })
            {
                Name = "Cupom NFC-e",
                IsBackground = true,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception ex)
        {
            tcs.TrySetResult($"Não consegui iniciar a impressão: {ex.Message}");
        }
        return tcs.Task;
    }

    private static string? Imprimir(DadosCupom dados, string? nomeImpressora, Papel papel)
        => ImprimirVisual(papel, () => MontarCupom(dados, papel),
            // Nomear o trabalho é o que permite reconhecê-lo depois na fila do Windows.
            // O sufixo torna o nome único POR TENTATIVA e não é enfeite: a fase
            // ErroImpressao tem botão "Reimprimir", e o nome antigo era IDÊNTICO entre a
            // impressão e a reimpressão da mesma nota. Como a vigia casa job por nome, ela
            // ficava olhando o trabalho VELHO (travado/pausado na fila), acusava erro que
            // não era dela, o operador mandava imprimir de novo — e saíam DOIS ou TRÊS
            // cupons fiscais da mesma nota. O prefixo continua identificando a nota.
            $"Cupom NFC-e {dados.Numero}/{dados.Serie} #{Environment.TickCount64:x}", nomeImpressora);

    // ── TEXTO LIVRE (comprovante do TEF e afins) ─────────────────────────────────

    /// <summary>
    /// Imprime blocos de texto monoespaçado — um trabalho de impressão por bloco, para cada
    /// via sair destacável. É o comprovante do TEF: as vias vêm prontas da rede (≤ 40 colunas)
    /// e saem como estão, inclusive os espaços à esquerda que centralizam o texto.
    ///
    /// ⚠️ Quem monta estes blocos (a rede do TEF, <c>Kds.ComandaLinhas</c>) escolhe a largura
    /// sozinho e não sabe qual bobina está configurada. Em 58 mm (32 colunas) as 40 colunas
    /// desses blocos passam do papel; aqui elas QUEBRAM em vez de sumir cortadas (ver
    /// <see cref="MontarTexto"/>), o que preserva o conteúdo e estraga o alinhamento.
    ///
    /// Mesmo contrato do cupom: <c>null</c> = saiu tudo; string = mensagem pronta pra tela
    /// (do primeiro bloco que não saiu). Nunca lança. NÃO passa por <see cref="Conferir"/> —
    /// aquilo é regra fiscal do DANFE; aqui o papel é o que a rede mandou.
    /// </summary>
    /// <param name="descricao">Nome do trabalho na fila do Windows (a vigia casa por nome; ganha índice e sufixo únicos).</param>
    public static async Task<string?> ImprimirTextoAsync(string descricao, IReadOnlyList<IReadOnlyList<string>> blocos,
        string? nomeImpressora, PrioridadeImpressao prioridade = PrioridadeImpressao.Alta)
        => (await ImprimirBlocosAsync(descricao, blocos, nomeImpressora, 0, prioridade)).Erro;

    /// <summary>
    /// Mesma coisa, mas com o par impressora+bobina EXPLÍCITO (ver <see cref="Destino"/>).
    /// É por aqui que a comanda do delivery sai: ela pode estar noutra térmica, com outra
    /// largura, e a bobina global <see cref="PapelAtual"/> é a do CUPOM — usá-la para a
    /// comanda é o que fazia o texto sair cortado na bobina estreita da expedição.
    /// </summary>
    public static async Task<string?> ImprimirTextoAsync(string descricao,
        IReadOnlyList<IReadOnlyList<string>> blocos, Destino destino,
        PrioridadeImpressao prioridade = PrioridadeImpressao.Alta)
        => (await ImprimirBlocosAsync(descricao, blocos, destino.Impressora, 0, prioridade, destino.Papel)).Erro;

    /// <summary>
    /// Igual a <see cref="ImprimirTextoAsync"/>, mas devolve também QUANTOS blocos saíram e aceita
    /// pular os primeiros — é o que permite a retentativa continuar da via que faltou em vez de
    /// imprimir a via do cliente duas vezes. `Sairam` conta só os desta chamada.
    /// </summary>
    /// <param name="papel">
    /// Bobina a usar. Null = a do cupom (<see cref="PapelAtual"/>), que é o que vale para as
    /// vias do TEF — elas saem na MESMA impressora do cupom. A comanda do delivery passa a
    /// dela aqui porque pode estar noutra térmica.
    /// </param>
    public static async Task<(string? Erro, int Sairam)> ImprimirBlocosAsync(string descricao,
        IReadOnlyList<IReadOnlyList<string>> blocos, string? nomeImpressora, int pular,
        PrioridadeImpressao prioridade = PrioridadeImpressao.Alta, Papel? papel = null)
    {
        if (blocos is null || blocos.Count == 0 || blocos.All(b => b is null || b.Count == 0))
            return ("Não há texto para imprimir.", 0);
        // Um retrato só para o conjunto de vias: elas são o mesmo comprovante partido em
        // pedaços destacáveis e não podem sair com larguras diferentes uma da outra.
        var papelDoTrabalho = papel ?? PapelAtual;
        var n = 0;
        var sairam = 0;
        foreach (var bloco in blocos)
        {
            if (bloco is null || bloco.Count == 0) continue;
            n++;
            if (n <= pular) continue;
            var linhas = bloco;
            var nome = $"{descricao} {n}/{blocos.Count} #{Environment.TickCount64:x}";
            var erro = await NaFilaAsync(prioridade,
                () => ComPrazoAsync(EmThreadStaAsync(() => ImprimirVisual(papelDoTrabalho,
                    () => MontarTexto(linhas, papelDoTrabalho), nome, nomeImpressora))));
            if (erro is not null) return (erro, sairam);
            sairam++;
        }
        return (null, sairam);
    }

    /// <summary>
    /// Bloco de texto: a MESMA bobina do cupom (ver <see cref="Folha"/>), uma linha Mono por
    /// item. Linha vazia vira " " (StackPanel pula TextBlock vazio e o comprovante perderia
    /// o espaçamento que a rede desenhou). `quebra: true` protege a linha maior que a bobina
    /// — quebra em vez de sumir cortada pela Border, e isso vale ainda mais desde que a
    /// largura é escolhida: em 58 mm cabem 32 colunas e estes blocos vêm com até 40.
    /// </summary>
    private static FrameworkElement MontarTexto(IReadOnlyList<string> linhas, Papel papel)
    {
        var pilha = new StackPanel();
        foreach (var bruta in linhas)
        {
            // Linha pode vir marcada com escala (ver LinhaEscala): a comanda da
            // cozinha usa isso para o número do pedido e os itens saírem maiores.
            var (l, escala) = LinhaEscala.Le(bruta);
            pilha.Children.Add(Texto(string.IsNullOrEmpty(l) ? " " : l, Corpo * escala,
                negrito: escala > 1.0, quebra: true));
        }
        return Folha(papel, pilha);
    }

    /// <summary>
    /// A bobina desenhada: fundo branco e a margem mecânica reservada por dentro, de modo
    /// que o conteúdo caia todo dentro da área que a cabeça térmica alcança. Uma função só
    /// para o cupom e para o texto livre porque as duas margens têm que ser IGUAIS — sai
    /// tudo na mesma impressora, e comprovante deslocado do cupom denuncia a diferença.
    /// </summary>
    private static Border Folha(Papel papel, UIElement conteudo) => new()
    {
        Width = papel.BobinaMm * MM,
        // Branco explícito: o app é escuro, e herdar o tema imprimiria um retângulo preto.
        Background = Brushes.White,
        Padding = new Thickness(
            (papel.BobinaMm - papel.UtilMm) / 2 * MM, 3 * MM,
            (papel.BobinaMm - papel.UtilMm) / 2 * MM, 3 * MM),
        Child = conteudo,
    };

    /// <summary>
    /// Miolo comum de impressão (roda na thread STA): resolve a fila, monta o visual NESTA
    /// thread, declara a página do tamanho do conteúdo, nomeia o trabalho e vigia a saída.
    /// Serve ao cupom fiscal e ao texto livre — o que muda é só o que se desenha.
    /// </summary>
    private static string? ImprimirVisual(Papel papel, Func<FrameworkElement> montar,
        string descricao, string? nomeImpressora)
    {
        LocalPrintServer? servidor = null;
        PrintQueue? fila = null;
        try
        {
            var pedida = nomeImpressora?.Trim();
            try
            {
                if (string.IsNullOrEmpty(pedida))
                {
                    fila = LocalPrintServer.GetDefaultPrintQueue();
                }
                else
                {
                    // O servidor precisa continuar vivo enquanto a fila for usada —
                    // ele é o dono do handle nativo por trás dela.
                    servidor = new LocalPrintServer();
                    fila = servidor.GetPrintQueue(pedida);
                }
            }
            catch
            {
                return string.IsNullOrEmpty(pedida)
                    ? "Nenhuma impressora padrão configurada no Windows."
                    : $"Impressora '{pedida}' não encontrada.";
            }

            var visual = montar();
            var largura = papel.BobinaMm * MM;
            visual.Measure(new Size(largura, double.PositiveInfinity));
            visual.Arrange(new Rect(new Point(0, 0), visual.DesiredSize));
            visual.UpdateLayout();

            var ticket = TicketDe(fila);
            // Não existe "altura automática" em XPS/GDI: a página é do tamanho que se
            // declara aqui. Curta demais corta o cupom no meio; longa demais come bobina.
            ticket.PageMediaSize = new PageMediaSize(largura, visual.DesiredSize.Height + AvancoMm * MM);
            ticket.PageOrientation = PageOrientation.Portrait;

            // O nome do trabalho é único por tentativa (quem chama garante o sufixo) —
            // é por ele que a vigia reconhece ESTE job na fila, e não um velho travado.
            try { fila.CurrentJobSettings.Description = descricao; }
            catch { /* driver que não aceita descrição: perde-se só o diagnóstico fino */ }

            PrintQueue.CreateXpsDocumentWriter(fila).Write(visual, ticket);
            return Vigiar(fila, descricao);
        }
        catch (Exception ex)
        {
            return Legivel(ex, nomeImpressora);
        }
        finally
        {
            fila?.Dispose();
            servidor?.Dispose();
        }
    }

    private static PrintTicket TicketDe(PrintQueue fila)
    {
        // Partir do ticket do usuário preserva ajustes que o dono fez no driver
        // (densidade, velocidade, corte automático) — reinventar tudo aqui é regredir.
        try { if (fila.UserPrintTicket is { } u) return u.Clone(); }
        catch { /* driver com ticket ilegível */ }
        try { if (fila.DefaultPrintTicket is { } d) return d.Clone(); }
        catch { /* idem */ }
        return new PrintTicket();
    }

    /// <summary>
    /// O <c>Write</c> volta assim que o spooler aceita o trabalho — isso não é o papel
    /// saindo. Aqui o trabalho é acompanhado até sumir da fila (= imprimiu) ou acusar
    /// problema. Sem isto, "impresso" mente e o cliente vai embora sem cupom.
    /// </summary>
    private static string? Vigiar(PrintQueue fila, string descricao)
    {
        // Relógio MONOTÔNICO. Com DateTime.UtcNow, um ajuste de NTP pra trás no meio da
        // vigia deixa o "decorrido" negativo e nenhum dos limites de tempo abaixo dispara —
        // a vigia gira presa enquanto o trabalho estiver na fila.
        var relogio = Stopwatch.StartNew();
        var jaVi = false;               // o trabalho DESTA chamada já foi visto na fila?
        var falhasSeguidas = 0;
        var espera = PrimeiraVigiaMs;

        while (true)
        {
            // DORMIR ANTES de consultar. O Write() volta quando o spooler ACEITA o
            // trabalho, e o job só entra no snapshot do EnumJobs um instante depois:
            // consultando na hora 0, a primeira volta não achava nada, `achou` ficava
            // false e a função devolvia null = "impresso" SEM NUNCA TER VISTO o trabalho.
            // O drain-wait inteiro virava no-op — impressora sem papel devolvia sucesso e
            // o cliente ia embora sem cupom. (Mesmo motivo do laço do TEF, seção 3.2.)
            Thread.Sleep(espera);
            espera = IntervaloVigiaMs;

            var achou = false;
            string? doJob = null;
            try
            {
                fila.Refresh();
                using var jobs = fila.GetPrintJobInfoCollection();
                foreach (var j in jobs)
                    using (j)
                    {
                        if (j.JobName != descricao) continue;
                        // Marcar aqui, ANTES de filtrar o concluído: ver o trabalho, mesmo
                        // já terminado, é a prova de que a vigia enxerga a fila. Sem essa
                        // prova, "sumiu da fila porque imprimiu" e "nunca apareceu" seriam
                        // indistinguíveis — e o segundo mente dizendo que imprimiu.
                        jaVi = true;
                        if (j.IsCompleted || j.IsPrinted) continue;   // esse já saiu
                        achou = true;
                        doJob ??= ProblemaDoJob(j);
                    }
                falhasSeguidas = 0;
            }
            catch
            {
                // Fila que não expõe os trabalhos (driver simples, impressora de rede sem
                // SNMP) ou tropeço transitório do spooler. Sem visibilidade não dá pra
                // afirmar falha, e o spooler já aceitou o cupom — acusar erro aqui provoca
                // reimpressão duplicada. Mas antes engolia QUALQUER exceção como sucesso na
                // primeira volta; agora insiste algumas vezes, porque PrintQueueException
                // isolada no Refresh de impressora de rede some na volta seguinte.
                falhasSeguidas++;
                if (falhasSeguidas < FalhasDeLeituraToleradas && relogio.Elapsed <= JanelaDeVigia) continue;

                // Desisto do diagnóstico, não da impressão: continua devolvendo null (não
                // provoca reimpressão), mas por um caminho SEPARADO, que fica rastreável.
                // "Não consigo enxergar" não é "imprimiu".
                // Deliberadamente NÃO espero os 15 s da JanelaDeVigia neste caso: segurar o
                // operador 15 s em toda venda numa fila que nunca vai ser legível custa mais
                // que o diagnóstico, e o registro em auditoria já denuncia o caso permanente.
                Avisar($"não consegui confirmar a saída do cupom ({descricao}): a fila da impressora ficou ilegível.");
                return null;
            }

            var decorrido = relogio.Elapsed;

            if (!achou)
            {
                // Sumiu da fila DEPOIS de ter sido visto = imprimiu. Este é o único
                // "impresso" que a vigia tem direito de afirmar.
                if (jaVi) return null;

                // Ainda não apareceu: dar mais um tempo pro spooler publicar o job.
                if (decorrido <= EsperaPeloJob) continue;

                // Nunca apareceu. Pode ser cupom curto que nasceu e morreu entre duas
                // consultas (comum em térmica local) — por isso não é erro por si só. Mas
                // se a própria FILA acusa defeito, o trabalho não saiu: aí vale como erro.
                return ProblemaDaFilaSeguro(fila);
            }

            // Os primeiros segundos são ruidosos: driver ainda inicializando reporta
            // "offline"/"em erro" antes de acordar. Só vale o que persistir.
            if (decorrido > TimeSpan.FromSeconds(2))
            {
                if (doJob is not null) return doJob;
                if (ProblemaDaFilaSeguro(fila) is { } daFila) return daFila;
            }

            // Estourou a vigia com o trabalho ainda na fila e sem ninguém acusar defeito:
            // é impressora lenta ou fila com outro documento na frente. Tratar como erro
            // faria o operador reimprimir e sairiam DOIS cupons da mesma nota.
            if (decorrido > JanelaDeVigia) return null;
        }
    }

    private static string? ProblemaDoJob(PrintSystemJobInfo j) =>
        j.IsPaperOut ? "acabou o papel na impressora."
        : j.IsOffline ? "a impressora está desligada ou desconectada."
        : j.IsUserInterventionRequired ? "a impressora pede intervenção (tampa aberta ou papel preso)."
        : j.IsInError ? "a impressora acusou erro."
        : j.IsPaused ? "o trabalho está pausado na fila do Windows."
        : j.IsDeleted || j.IsDeleting ? "o trabalho foi cancelado na fila do Windows."
        : null;

    /// <summary>
    /// <see cref="ProblemaDaFila"/> à prova de fila que morre no meio da leitura. Estas
    /// propriedades ficam FORA do try da vigia: uma PrintQueueException aqui subiria até o
    /// catch de <see cref="Imprimir"/> e viraria "O Windows recusou o cupom" — uma falha
    /// ANUNCIADA sem prova nenhuma, que manda o operador reimprimir um cupom que saiu.
    /// Não conseguir ler o estado da fila é silêncio, não é defeito.
    /// </summary>
    private static string? ProblemaDaFilaSeguro(PrintQueue f)
    {
        try { return ProblemaDaFila(f); }
        catch { return null; }
    }

    // Atenção ao nome: a fila expõe IsOutOfPaper e o trabalho expõe IsPaperOut. São
    // dois nomes para a mesma condição, e trocá-los não compila.
    private static string? ProblemaDaFila(PrintQueue f) =>
        f.IsNotAvailable ? "a impressora não está disponível."
        : f.IsOffline ? "a impressora está desligada ou desconectada."
        : f.IsOutOfPaper ? "acabou o papel na impressora."
        : f.IsDoorOpened ? "a tampa da impressora está aberta."
        : f.IsPaperJammed ? "há papel preso na impressora."
        : f.IsPaused ? "a fila da impressora está pausada no Windows."
        : f.IsInError ? "a impressora acusou erro."
        : null;

    /// <summary>Traduz a exceção do subsistema de impressão em recado de operador.</summary>
    private static string Legivel(Exception ex, string? nome)
    {
        var alvo = string.IsNullOrWhiteSpace(nome) ? "padrão do Windows" : $"'{nome.Trim()}'";
        return ex switch
        {
            PrintQueueException => $"Impressora {alvo} não encontrada ou sem acesso.",
            PrintSystemException => $"O Windows recusou o cupom na impressora {alvo}: {ex.Message}",
            UnauthorizedAccessException => $"Sem permissão para usar a impressora {alvo}.",
            _ => $"Falha ao imprimir na impressora {alvo}: {ex.Message}",
        };
    }

    // ── MONTAGEM DO CUPOM ────────────────────────────────────────────────────────

    /// <summary>
    /// Desenha o MESMO cupom que iria para o papel, num PNG.
    ///
    /// Existe porque conferir layout na impressora custa bobina e exige estar na frente
    /// dela — e porque um cupom torto descoberto junto com a primeira nota real confunde
    /// dois problemas num só. Renderiza pela mesma <see cref="MontarCupom"/>, senão a
    /// prévia mentiria justamente sobre o que se quer conferir.
    /// </summary>
    public static Task<string?> PreVisualizarAsync(DadosCupom dados, string caminhoPng)
    {
        if (dados is null) return Task.FromResult<string?>("Não há cupom para desenhar.");
        // A prévia só serve se for a MESMA bobina que iria para o papel — é para conferir
        // largura que ela existe, e prévia de 80 mm com a loja configurada em 58 esconderia
        // justamente o corte que se quer ver.
        var papel = PapelAtual;
        return EmThreadStaAsync(() =>
        {
            var visual = MontarCupom(dados, papel);
            // MESMA largura da impressão (bobina inteira, não a área útil). Medir contra
            // a área útil parece certo e não é: a montagem já tem Width = bobina e reserva
            // a margem por dentro, e o WPF corta o DesiredSize no limite que recebe — o
            // desenho continua saindo mais largo que a régua e a prévia mente dizendo que
            // o cupom está cortado quando quem está errado é a régua.
            var largura = papel.BobinaMm * MM;
            visual.Measure(new Size(largura, double.PositiveInfinity));
            visual.Arrange(new Rect(new Point(0, 0), visual.DesiredSize));
            visual.UpdateLayout();

            // 4x: no tamanho real (203 dpi ≈ 2,1 px/DIP) o texto de 8pt fica ilegível na tela
            const double escala = 4.0;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(visual.DesiredSize.Width * escala),
                (int)Math.Ceiling(visual.DesiredSize.Height * escala),
                96 * escala, 96 * escala, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(visual);

            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = System.IO.File.Create(caminhoPng);
            enc.Save(fs);
            return null;
        });
    }

    private static FrameworkElement MontarCupom(DadosCupom d, Papel papel)
    {
        var pilha = new StackPanel();

        // tpAmb manda no carimbo E na URL de consulta — ver o doc-comment de DadosCupom.
        // Ausente (null) é tratado como NÃO-produção: cupom carimbado "sem valor fiscal"
        // por engano é constrangimento; cupom de homologação sem carimbo é um papel se
        // fazendo passar por documento fiscal.
        var producao = d.TpAmb == 1;

        // ── emitente ──
        // `?? ""` em tudo: os campos vêm de ResultadoEmissao.Emit, que é anulável inteiro.
        // Nome e CNPJ já foram exigidos em Conferir; o resto sai vazio em vez de estourar
        // NullReferenceException no meio da montagem (que viraria "Falha ao imprimir...:
        // Object reference not set to an instance of an object" na cara do operador).
        pilha.Children.Add(Texto((d.EmitenteNome ?? "").ToUpperInvariant(), 13, Sans, negrito: true,
            centro: true, quebra: true));
        pilha.Children.Add(Texto($"CNPJ {Documento(d.EmitenteCnpj)}   IE {d.EmitenteIe ?? ""}", Miudo,
            centro: true, quebra: true));
        pilha.Children.Add(Texto(d.EmitenteEndereco ?? "", Miudo, centro: true, quebra: true,
            margemBaixo: 2));

        pilha.Children.Add(Regua());

        // ── identificação do documento ──
        if (d.Recibo)
        {
            pilha.Children.Add(Texto("RECIBO DE VENDA", Corpo, negrito: true, centro: true));
            pilha.Children.Add(Texto("SEM VALOR FISCAL — não substitui documento fiscal",
                Miudo, centro: true, quebra: true, margemBaixo: 2));
        }
        else
        {
            // texto exigido no DANFE NFC-e
            pilha.Children.Add(Texto(
                "DANFE NFC-e - Documento Auxiliar da Nota Fiscal de Consumidor Eletrônica",
                Miudo, centro: true, quebra: true));
            pilha.Children.Add(Texto("Não permite aproveitamento de crédito de ICMS",
                Miudo, centro: true, quebra: true, margemBaixo: 2));
        }

        pilha.Children.Add(Regua());

        // ── itens ──
        // Duas linhas por item: a de cima identifica, a de baixo faz a conta. É o arranjo
        // que cabe nas colunas da bobina sem picotar nome de produto — e é por isso que a
        // linha da conta pode sair sem quebra: Colunado a fecha na largura exata do papel.
        pilha.Children.Add(Texto("ITEM CÓDIGO DESCRIÇÃO", Corpo));
        pilha.Children.Add(Texto(Colunado("     QTD UN X VL UNIT", "VL TOTAL", papel.Colunas), Corpo,
            margemBaixo: 2));

        var seq = 1;
        foreach (var i in d.Itens)
        {
            var codigo = (i.Codigo ?? "").Trim();
            if (codigo.Length > 12) codigo = codigo[..12];
            pilha.Children.Add(Texto($"{seq:000} {codigo} {i.Descricao}".Trim(), Corpo, quebra: true));
            pilha.Children.Add(Texto(
                Colunado($"     {i.Qtd.Formatada()} {i.Unidade} X {Valor(i.Unitario)}", Valor(i.Total),
                    papel.Colunas),
                Corpo));
            seq++;
        }

        pilha.Children.Add(Regua());

        // ── totais ──
        // O DANFE mostra o total DA NOTA (vNF que o emissor devolveu), não o total que o
        // PDV somou. Os dois podem divergir por um centavo em item pesável — Dinheiro.VezesQtd
        // arredonda AwayFromZero, o motor faz a própria conta de vProd sobre os decimais do
        // JSON — e um centavo de diferença entre papel e XML autorizado é achado de
        // fiscalização. Conferir() já barra a divergência antes de chegar aqui; esta linha
        // continua imprimindo o vNF porque é ele que vale se alguém afrouxar aquele portão.
        // Sem vNF (resposta que não trouxe o campo), cai no total do PDV — é a única fonte
        // que sobra, e nesse caso o papel é a melhor aproximação disponível, não a verdade fiscal.
        var totalDaNota = d.VNf is { } vnf ? Dinheiro.DeReais(vnf) : d.Total;
        pilha.Children.Add(Duas("Qtd. total de itens", d.Itens.Count.ToString(Br), Corpo));
        pilha.Children.Add(Duas("VALOR TOTAL R$", Valor(totalDaNota), Destaque, negrito: true));

        // ── pagamento ──
        // Cada linha aqui é o vPag DA NOTA (Σ == total, seção 4.2). O que o cliente
        // entregou vem depois, separado: juntar os dois no mesmo bloco produz ou
        // "VALOR TOTAL 25,00 / Dinheiro 50,00" (papel contradizendo o XML) ou
        // "Dinheiro 25,00 / Troco 25,00" (absurdo pro cliente).
        pilha.Children.Add(Texto("FORMA DE PAGAMENTO", Corpo, margemCima: 4));
        foreach (var p in d.Pagamentos)
            pilha.Children.Add(Duas(p.Forma, Valor(p.Valor), Corpo));

        // Recebido/troco só existem no papel e em venda_pagamento: o XML leva vPag igual
        // ao total e NENHUM <vTroco> (o motor não emite a tag). Derivado do Recebido de
        // propósito — troco como campo próprio poderia contradizer o que está impresso logo acima.
        var troco = d.Recebido - totalDaNota;
        if (troco.Positivo)
        {
            pilha.Children.Add(Duas("Dinheiro recebido R$", Valor(d.Recebido), Corpo));
            pilha.Children.Add(Duas("Troco R$", Valor(troco), 13, negrito: true));
        }

        pilha.Children.Add(Regua(margemCima: 4));

        // ── carimbos que mudam o valor jurídico do papel (não se aplicam a recibo) ──
        if (!d.Recibo && !producao)
            pilha.Children.Add(Texto("EMITIDA EM AMBIENTE DE HOMOLOGACAO - SEM VALOR FISCAL",
                Corpo, negrito: true, centro: true, quebra: true, margemBaixo: 3));

        if (!d.Recibo && d.Contingencia)
        {
            pilha.Children.Add(Texto("EMITIDA EM CONTINGÊNCIA OFFLINE", Corpo,
                negrito: true, centro: true, quebra: true));
            pilha.Children.Add(Texto("Aguardando autorização da SEFAZ", Corpo,
                centro: true, quebra: true, margemBaixo: 3));
        }

        // ── consulta pela chave (recibo não tem chave — pula o bloco inteiro) ──
        if (!d.Recibo)
        {
            pilha.Children.Add(Texto("Consulte pela Chave de Acesso em", Miudo, centro: true));
            // Mesma fonte do carimbo (tpAmb): assim não existe a combinação "URL de produção
            // com carimbo de homologação", que dois campos independentes permitiam montar.
            pilha.Children.Add(Texto(producao ? PortalProducao : PortalHomologacao,
                Miudo, centro: true, quebra: true, margemBaixo: 2));
            // Grupos de 4 com quebra automática: 44 dígitos + separadores passam de uma
            // linha em algumas larguras, e cortar a chave inutiliza a consulta.
            pilha.Children.Add(Texto(Chave(d.Chave), 9, centro: true, quebra: true, margemBaixo: 3));
        }

        // ── consumidor ──
        var doc = SoDigitos(d.Documento);
        pilha.Children.Add(Texto(
            doc.Length switch
            {
                11 => $"CONSUMIDOR CPF: {Documento(doc)}",
                14 => $"CONSUMIDOR CNPJ: {Documento(doc)}",
                _ => "CONSUMIDOR NÃO IDENTIFICADO",
            },
            Corpo, centro: true, quebra: true, margemBaixo: 2));

        // ── numeração, emissão e protocolo (recibo não tem numeração fiscal) ──
        if (!d.Recibo)
            pilha.Children.Add(Texto($"NFC-e nº {d.Numero}   Série {d.Serie}", Corpo, centro: true));
        // ⚠️ `Emissao` é a hora do ENVIO, não o dhEmi do XML — o agente não devolve dhEmi
        // (ver o doc-comment de DadosCupom). Quem preencher esse campo com DateTime.Now da
        // impressão faz o papel divergir do XML pelo tempo do round-trip. A linha "Impresso
        // em", lá embaixo, é a única que fala mesmo da hora da impressão.
        pilha.Children.Add(Texto($"Emitida em {d.Emissao:dd/MM/yyyy HH:mm:ss}", Miudo, centro: true));

        // Em contingência não há protocolo — imprimir qualquer coisa nessa linha seria
        // afirmar uma autorização que a SEFAZ ainda não deu.
        if (!d.Contingencia && !string.IsNullOrWhiteSpace(d.Protocolo))
        {
            pilha.Children.Add(Texto("Protocolo de Autorização", Miudo, centro: true, margemCima: 2));
            pilha.Children.Add(Texto(
                d.ProtocoloEm is { } quando
                    ? $"{d.Protocolo}  {quando:dd/MM/yyyy HH:mm:ss}"
                    : d.Protocolo!,
                Miudo, centro: true, quebra: true));
        }

        // ── QR (recibo não tem QR nem deve disparar o alerta de "cupom sem QR") ──
        if (d.Recibo) { /* nada */ }
        // O QR nunca pode passar da área útil: fora dela ele sai com um lado faltando, e
        // QR aparado não lê. Hoje o Math.Min não corta nada (a bobina mais estreita da
        // tabela ainda tem 48 mm úteis) — é a garantia de que continua assim se aparecer
        // bobina menor.
        else if (Qr(d.QrCode, Math.Min(QrLadoMm, papel.UtilMm) * MM) is { } qr)
        {
            qr.Margin = new Thickness(0, 5 * MM, 0, 0);
            pilha.Children.Add(qr);
        }
        else
        {
            // O QR é item OBRIGATÓRIO do DANFE NFC-e — é como o consumidor valida a nota.
            // Faltar em silêncio absoluto era o pior desfecho: o gatilho mais provável não
            // é exceção, é `QrCode` vazio (o campo é string? e some se o emissor omitir ou
            // renomear `qrCode`), então um erro de contrato numa ponta faria TODOS os cupons
            // saírem sem QR e ninguém perceberia até a fiscalização. Agora o papel denuncia
            // e o caso vai pra auditoria.
            pilha.Children.Add(Texto("QR indisponível — consulte pela chave de acesso acima",
                Miudo, centro: true, quebra: true, margemCima: 4));
            // Vai pelo canal de aviso e NÃO pelo retorno de ImprimirAsync: retorno não-nulo
            // joga a tela em ErroImpressao, e "Reimprimir" só produziria outro cupom sem QR.
            Avisar(string.IsNullOrWhiteSpace(d.QrCode)
                ? $"cupom {d.Numero}/{d.Serie} impresso SEM QR Code: o emissor não devolveu o campo qrCode."
                : $"cupom {d.Numero}/{d.Serie} impresso SEM QR Code: falha ao desenhar o código.");
        }

        pilha.Children.Add(Regua(margemCima: 5));
        pilha.Children.Add(Texto($"Operador: {d.Operador ?? ""}", Miudo, centro: true, quebra: true));
        pilha.Children.Add(Texto($"Impresso em {DateTime.Now:dd/MM/yyyy HH:mm:ss}", Miudo, centro: true));

        return Folha(papel, pilha);
    }

    // ── PEÇAS DO VISUAL ──────────────────────────────────────────────────────────

    private static TextBlock Texto(string conteudo, double tamanho, FontFamily? fonte = null,
        bool negrito = false, bool centro = false, bool quebra = false,
        double margemCima = 0, double margemBaixo = 0) => new()
        {
            Text = conteudo,
            FontFamily = fonte ?? Mono,
            FontSize = tamanho,
            FontWeight = negrito ? FontWeights.Bold : FontWeights.Normal,
            Foreground = Brushes.Black,
            TextAlignment = centro ? TextAlignment.Center : TextAlignment.Left,
            TextWrapping = quebra ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Margin = new Thickness(0, margemCima, 0, margemBaixo),
        };

    /// <summary>
    /// Rótulo à esquerda, valor colado na direita. Em grade e não em string preenchida
    /// porque estas linhas mudam de tamanho de fonte — padding de espaços só alinha
    /// enquanto a fonte é a mesma.
    /// </summary>
    private static Grid Duas(string esquerda, string direita, double tamanho, bool negrito = false)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var e = Texto(esquerda, tamanho, negrito: negrito, quebra: true);
        var d = Texto(direita, tamanho, negrito: negrito);
        d.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(e, 0);
        Grid.SetColumn(d, 1);
        g.Children.Add(e);
        g.Children.Add(d);
        return g;
    }

    /// <summary>Traço separador: linha cheia sai mais limpa em térmica do que fileira de hífens.</summary>
    private static Border Regua(double margemCima = 3, double margemBaixo = 3) => new()
    {
        Height = 1,
        Background = Brushes.Black,
        Margin = new Thickness(0, margemCima, 0, margemBaixo),
    };

    /// <summary>
    /// QR do consumidor, desenhado como geometria vetorial.
    ///
    /// O QRCoder 1.6.0 não traz mais o renderizador XAML, e o renderizador de bitmap
    /// puxaria System.Drawing.Common junto. A matriz de módulos é pública, então o
    /// caminho honesto é montar os retângulos aqui: fica vetorial (o driver rasteriza
    /// na resolução real da térmica, sem borrão de reamostragem) e sem dependência nova.
    /// </summary>
    private static FrameworkElement? Qr(string? conteudo, double lado)
    {
        // Anulável de propósito: `ResultadoEmissao.QrCode` é string? e some inteiro quando
        // o emissor não manda o campo. Quem trata o null é o chamador (imprime a linha de
        // aviso no lugar do QR) — aqui só devolvemos "não deu".
        if (string.IsNullOrWhiteSpace(conteudo)) return null;
        try
        {
            // ECCLevel.M é o nível usado no cupom do caminho web. O conteúdo vai
            // literal: a URL do QR da NFC-e é assinada, um caractere a mais invalida.
            var dados = QRCodeGenerator.GenerateQrCode(conteudo, QRCodeGenerator.ECCLevel.M);
            var matriz = dados.ModuleMatrix;
            var n = matriz.Count;
            if (n == 0) return null;

            var geometria = new StreamGeometry();
            using (var ctx = geometria.Open())
            {
                for (var y = 0; y < n; y++)
                {
                    var linha = matriz[y];
                    var largura = Math.Min(n, linha.Length);
                    var x = 0;
                    while (x < largura)
                    {
                        if (!linha[x]) { x++; continue; }
                        // Módulos escuros vizinhos viram um retângulo só: menos figuras
                        // no XPS e, principalmente, sem costura branca entre eles.
                        var ini = x;
                        while (x < largura && linha[x]) x++;
                        ctx.BeginFigure(new Point(ini, y), true, true);
                        ctx.LineTo(new Point(x, y), false, false);
                        ctx.LineTo(new Point(x, y + 1), false, false);
                        ctx.LineTo(new Point(ini, y + 1), false, false);
                    }
                }
            }
            geometria.Freeze();

            var caminho = new System.Windows.Shapes.Path
            {
                Data = geometria,
                Fill = Brushes.Black,
                // A geometria está em unidades de módulo (0..n); o Uniform ajusta ao
                // tamanho pedido sem eu ter que calcular fração de módulo em DIP.
                Stretch = Stretch.Uniform,
                Width = lado,
                Height = lado,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            // A zona de silêncio de 4 módulos já vem embutida na matriz do QRCoder.
            // Aparar pra 1 economizaria ~3 mm de bobina e é onde leitor de celular
            // começa a falhar em impressão térmica com contraste baixo.
            RenderOptions.SetEdgeMode(caminho, EdgeMode.Aliased);
            return caminho;
        }
        catch
        {
            // A venda já está fechada e paga. Cupom sem QR ainda serve de comprovante
            // (a chave impressa permite a consulta); abortar a impressão inteira, não.
            // O null aqui não some mais em silêncio: quem chama imprime a linha de
            // "QR indisponível" no lugar e manda o caso pra auditoria.
            return null;
        }
    }

    // ── FORMATAÇÃO ───────────────────────────────────────────────────────────────

    private static string Valor(Dinheiro d) => d.Reais.ToString("N2", Br);

    private static string SoDigitos(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var buf = new char[s.Length];
        var n = 0;
        foreach (var c in s) if (c is >= '0' and <= '9') buf[n++] = c;
        return new string(buf, 0, n);
    }

    private static string Documento(string? bruto)
    {
        var d = SoDigitos(bruto);
        return d.Length switch
        {
            11 => $"{d[..3]}.{d[3..6]}.{d[6..9]}-{d[9..]}",
            14 => $"{d[..2]}.{d[2..5]}.{d[5..8]}/{d[8..12]}-{d[12..]}",
            // Documento fora do padrão sai como veio: inventar máscara esconderia o defeito.
            _ => bruto ?? "",
        };
    }

    /// <summary>Chave de acesso em grupos de 4 — é assim que o consumidor digita no portal.</summary>
    private static string Chave(string? chave)
    {
        var d = SoDigitos(chave);
        if (d.Length == 0) return "";
        var partes = new List<string>();
        for (var i = 0; i < d.Length; i += 4)
            partes.Add(d.Substring(i, Math.Min(4, d.Length - i)));
        return string.Join(" ", partes);
    }

    /// <summary>
    /// Cola o valor na ÚLTIMA coluna da bobina, preenchendo o meio com espaços (fonte
    /// monoespaçada). Devolve sempre uma linha de exatamente <paramref name="colunas"/>
    /// caracteres — é o que permite imprimi-la sem quebra automática: ela não tem como
    /// passar do papel, porque foi montada na medida dele. A esquerda é aparada quando não
    /// cabe; o valor, nunca (cortar dinheiro no papel é pior do que cortar a descrição).
    /// </summary>
    private static string Colunado(string esquerda, string direita, int colunas)
    {
        var d = direita ?? "";
        if (d.Length >= colunas) return d;
        var espaco = colunas - d.Length;
        var e = esquerda ?? "";
        if (e.Length > espaco) e = e[..espaco];
        return e.PadRight(espaco) + d;
    }
}
