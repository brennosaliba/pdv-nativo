using System.Diagnostics;
using System.Globalization;

namespace Pdv.Nucleo;

// TEF PayGo Windows pela PGWebLib (biblioteca). Terceiro provedor; segue o MESMO contrato dos
// outros dois (troca de arquivos em PayGo.cs, WebService em ControlPay.cs): a tela cobra, recebe
// um DesfechoTef, grava `tef_transacao` pelos reports e pelo hook Guardar. O que é inegociável
// aqui vem da spec da PGWebLib e é o mesmo two-phase do PayGo por arquivos:
//
//   1. PW_iExecTransac é um LAÇO: PWRET_MOREDATA pede dados (menu/digitado, ou captura no
//      pinpad via PW_iPP* + PW_iPPEventLoop) e PWRET_NOTHING pede paciência. Nada aqui trava
//      sem teto: cada laço tem relógio e o operador pode abortar (PW_iPPAbort);
//   2. ao terminar, ler IMEDIATAMENTE PWINFO_CNFREQ; se "1", GRAVAR (REQNUM, AUTLOCREF,
//      AUTEXTREF, VIRTMERCH, AUTHSYST) em tef_transacao e SÓ ENTÃO PW_iConfirmation
//      (PWCNF_CNF_AUTO) ou desfazer (PWCNF_REV_*). Sem gravar não há confirmação;
//   3. transação não confirmada BLOQUEIA o ponto de captura: no religamento os PWINFO_PND*
//      descrevem a pendência e o PDV resolve sozinho (CNF se conhece como paga, REV se não),
//      nunca perguntando ao operador;
//   4. confirmação sem ack fica 'cnf_sem_ack'/'ncn_sem_ack' e é reenviada antes do próximo
//      comando e no religamento; PWRET_INVALIDTRN NÃO é ack (a biblioteca só diz que não tem
//      a transação pendente): a linha vira 'orfa', nunca 'pago' nem 'desfeita';
//   5. CNFREQ=0 é aprovação DEFINITIVA: não existe REV. Falha ao gravar ou saída do operador
//      depois dela nunca dizem "desfeita" nem "cancelado": é paga ou órfã com aviso;
//   6. PWINFO_CARDFULLPAN (193) NUNCA é lido. Nem para log.

/// <summary>Identidade da automação (AUTNAME/AUTVER/AUTDEV), capacidades (AUTCAP) e redes pré-selecionadas.</summary>
/// <param name="RedeCartao">Valor para o menu PWINFO_AUTHSYST em cartão (ex.: `REDE`). Null = a biblioteca mostra o menu.</param>
/// <param name="RedePix">Idem para Pix. Null = menu.</param>
/// <param name="PortaPinpad">PWINFO_PPCOMMPORT; "0" = automática.</param>
/// <param name="RedesPermitidas">
/// As redes que o menu de seleção da rede pode mostrar ao operador (`tef_pgweb_redes`). Vazia ou
/// null = mostra o que a biblioteca listar, que é o padrão. Encurtar NÃO é o mesmo que fixar:
/// o menu continua aparecendo, e é nele que o passo 05 do roteiro manda apertar Esc. Ver
/// <see cref="FiltroRedes"/>.
/// </param>
/// <param name="PreferenciaQr">
/// PWINFO_DSPQRPREF na venda de Pix: <see cref="PW.DSPQRPREF_TELA"/> pede o QR na tela do caixa,
/// <see cref="PW.DSPQRPREF_PINPAD"/> no pinpad, null nao manda nada e a biblioteca decide
/// (medido em 09/09/2026: decide pelo pinpad, mesmo com CAP_QR declarada).
/// </param>
public sealed record OpcoesPGWebLib(string NomeAutomacao, string VersaoAutomacao, string Desenvolvedor,
    int Capacidades = ProvedorPGWebLib.CapacidadesPadrao, string? RedeCartao = null, string? RedePix = null,
    string PortaPinpad = "0", string Moeda = "986", short Ambiente = PW.ENVRMNT_PROD,
    IReadOnlyList<string>? RedesPermitidas = null, string? PreferenciaQr = null);

/// <summary>
/// O que a biblioteca mandou o caixa mostrar na tela enquanto a transação corre.
/// </summary>
/// <param name="Titulo">Uma linha, para o cabeçalho do diálogo.</param>
/// <param name="Mensagem">O texto que a biblioteca mandou (szPrompt).</param>
/// <param name="QrCode">
/// O conteúdo do QR, quando é um QR. Vem de PW_iGetResult(PWINFO_AUTHPOSQRCODE), e não do prompt:
/// o prompt tem 84 caracteres e um payload de Pix não cabe lá.
/// </param>
public sealed record ExibicaoTef(string Titulo, string Mensagem, string? QrCode)
{
    public bool EhQrCode => !string.IsNullOrWhiteSpace(QrCode);
}

/// <summary>
/// Provedor de TEF sobre <see cref="IPGWebLib"/>. Uma instância por processo; as chamadas são
/// serializadas por um semáforo porque a biblioteca guarda UMA transação corrente.
/// </summary>
public sealed class ProvedorPGWebLib : IProvedorTefOperavel, IDisposable
{
    public string Nome => "pgweblib";

    /// <summary>4 (valor fixo) + 8 (vias diferenciadas) + 16 (via reduzida).</summary>
    public const int CapacidadesPadrao = PW.CAP_VALOR_FIXO + PW.CAP_VIAS_DIFERENCIADAS + PW.CAP_VIA_REDUZIDA;

    /// <summary>Diretório de trabalho em branco: o da casa (ConfigPGWebLib.DirPadrao), fora de C:\PAYGO de propósito.</summary>
    public const string PastaPadrao = ConfigPGWebLib.DirPadrao;

    // 07/09/2026: a biblioteca passou a ser autonoma (kit avulso, sem o PayGo Windows e sem
    // Warsaw), entao mandar "confira o PayGo Windows" viraria conselho para um programa que nao
    // esta mais instalado. O que resolve hoje e conferir a pasta da biblioteca.
    public const string MsgTefNaoResponde = "TEF não responde: a biblioteca do PayGo não iniciou. Confira a pasta configurada";
    /// <summary>O diretório de trabalho (tef_pgweb_dir) não pôde ser criado: sem ele a DLL devolve PWRET_WRITERR (medido em 07/09/2026).</summary>
    public const string MsgPastaInacessivel = "TEF não responde: a PGWebLib não iniciou, pasta de trabalho inacessível";
    public const string MsgNaoInstalado = "PayGo não instalado neste terminal: faça a instalação pelo menu do TEF";

    /// <summary>
    /// Outra operação da biblioteca ainda está em voo (14/09/2026, Castelo: a primeira instalação
    /// ficou presa minutos dentro de uma chamada e a segunda esperava calada atrás dela). Tirar o
    /// cabo do pinpad encerrou a chamada presa em 0,3 s no log daquele dia. Só aparece fora de
    /// venda: instalação, ADM e menu do TEF.
    /// </summary>
    public const string MsgAindaOcupado = "A maquininha ainda está presa na tentativa anterior. Tire o cabo USB do pinpad, espere 10 segundos e tente de novo. Se não voltar, feche e abra o caixa.";

    /// <summary>
    /// A biblioteca disse que não há pinpad (PWRET_PPNOTFOUND, PPCOMERR ou PINPADERR). A tela
    /// recebia "TEF não aceitou o dado 32514: PWRET_PPNOTFOUND" e ninguém na loja sabia o que
    /// fazer com isso. O código fica só na auditoria.
    /// </summary>
    public static string MsgPinpadNaoAchado(string? porta)
        => PortaDoPinpad.Normalizar(porta) is var n && n != PortaDoPinpad.Automatica
            ? $"Não achei a maquininha na porta {n}. Confira o cabo, feche o programa da Gertec e o PayGo Windows se estiverem abertos."
            : "Não achei a maquininha em nenhuma porta. Confira o cabo, feche o programa da Gertec e o PayGo Windows se estiverem abertos.";

    /// <summary>Retornos com que a biblioteca diz "não achei o pinpad".</summary>
    public static bool EhPinpadAusente(short ret) => ret is PW.PWRET_PPNOTFOUND or PW.PWRET_PPCOMERR or PW.PWRET_PINPADERR;

    /// <summary>CNFREQ=0 (já definitiva na rede) e o caixa não gravou: não existe REV; o cliente JÁ pagou.</summary>
    public static string MsgNaoGravada(string? nsu) => $"Cobrança aprovada (NSU {nsu ?? "-"}) mas não gravada no caixa. Não cobre de novo: confira no PayGo";
    public static string MsgCancelamentoNaoGravado(string? nsu) => $"Cancelamento aprovado (NSU {nsu ?? "-"}) mas não gravado no caixa. Não repita: confira no PayGo";
    /// <summary>PWRET_INVALIDTRN: a biblioteca não tem esta transação pendente; não diz se confirmou ou desfez.</summary>
    public static string MsgNaoReconhece(string? nsu) => $"PayGo não reconhece esta transação (NSU {nsu ?? "-"}). Confira no relatório antes de cobrar de novo";

    /// <summary>Cadência dos laços (PWRET_NOTHING e PW_iPPEventLoop).</summary>
    public int IntervaloPollMs { get; init; } = 100;

    /// <summary>Teto de PWRET_NOTHING seguidos antes de abortar (a biblioteca está com o host ou o pinpad).</summary>
    public int TempoMaxExecMs { get; init; } = 600_000;

    /// <summary>Teto de uma captura no pinpad (PW_iPPEventLoop) antes de PW_iPPAbort.</summary>
    public int TempoMaxCapturaMs { get; init; } = 300_000;

    /// <summary>Teto para a tela responder um menu/dado digitado (PWDAT_MENU/TYPED).</summary>
    public int TempoPerguntaMs { get; init; } = 120_000;

    /// <summary>
    /// Quanto o caixa espera a biblioteca encerrar depois de um PW_iPPAbort pedido pelo operador.
    /// Passado isso, a cobranca encerra cancelada e desfeita por conta do caixa. Medido em
    /// 09/09/2026 (passo 55): a PGWebLib levava de 20 a 40 s para sair da espera do host do Pix.
    /// </summary>
    public int TempoMaxCancelamentoMs { get; init; } = 5_000;

    /// <summary>
    /// Quanto uma operação administrativa (instalação, ADM, menu do TEF) espera outra terminar
    /// antes de desistir com <see cref="MsgAindaOcupado"/>. Antes esperava sem teto e sem aviso.
    /// </summary>
    public int EsperaOcupadoMs { get; init; } = 3_000;

    /// <summary>
    /// O TESTE DO PINPAD antes da instalação (14/09/2026, pedido do dono depois da Castelo).
    /// Recebe a porta configurada e devolve o resultado. Não deu = a instalação NÃO chama a
    /// biblioteca e devolve a frase do teste. Deu = a instalação manda a porta que respondeu.
    /// Null = sem teste (a bateria e o comportamento antigo). Se o teste lançar, a instalação
    /// segue sem ele: defeito do teste não pode travar a loja.
    /// </summary>
    public Func<string, CancellationToken, Task<ResultadoTestePinpad>>? ConferirPinpad { get; init; }

    /// <summary>
    /// Cadência de segurança do PW_iIdleProc: vale quando a biblioteca não informa
    /// PWINFO_IDLEPROCTIME (vazio ou inválido). <see cref="IniciarIdle"/> grava o intervalo do timer aqui.
    /// </summary>
    public int IntervaloIdleMs { get; set; } = 60_000;

    /// <summary>Grava a transação em disco. Chamado ANTES da confirmação; false ou exceção = desfaz (REV).</summary>
    public Func<TransacaoPayGo, bool> Guardar { get; init; } = _ => true;

    /// <summary>CNPJ da credenciadora pelo nome da rede (AUTHSYST). Null = tpIntegra=2 na NFC-e.</summary>
    public Func<string, string?>? CnpjDaRede { get; init; }

    /// <summary>Pendência que a biblioteca descreve (PND*): este caixa conhece o REQNUM como pago? true = CNF; false = REV.</summary>
    public Func<string, bool>? ConhecidaConfirmada { get; init; }

    public Action<string>? Auditar { get; init; }

    /// <summary>
    /// AS REDES QUE ESTE TERMINAL OFERECE, do jeito que a biblioteca as escreve.
    ///
    /// ⚠️ EXISTE POR UMA PERGUNTA DO DONO (09/09/2026): "pra que colocar C6PAY e
    /// C6 PAY? nao eh melhor colocar um q aceite ambos?".
    ///
    /// Nao da para aceitar os dois: o nome da rede nao e rotulo nosso, e sim
    /// identificador que o terminal compara letra por letra. `C6 PAY` aprovou quatro
    /// vezes hoje e `C6PAY` devolveu A116 nas duas tentativas. "Um que aceite ambos"
    /// seria o PDV escolhendo um, e escolher errado e a recusa garantida.
    ///
    /// Mas ele esta certo no fundo: ninguem deveria precisar saber a grafia. Quem
    /// sabe e o TERMINAL, e ele diz, no menu de rede que a biblioteca abre. Aqui esse
    /// menu deixa de passar em branco: as opcoes sao anunciadas e viram a lista da
    /// Configuracao, no lugar da lista escrita a mao.
    /// </summary>
    public Action<IReadOnlyList<string>>? RedesDoTerminal { get; init; }

    /// <summary>
    /// As redes que o menu de rede ofereceu numa cobrança PIX (14/09/2026, loja Castelo). Lista à
    /// parte da do cartão: é ela que a Configuração mostra primeiro na Rede do Pix.
    /// </summary>
    public Action<IReadOnlyList<string>>? RedesPixDoTerminal { get; init; }

    /// <summary>Imprime as vias ANTES da confirmação; false = desfaz (PWCNF_REV_PRN_AUT). Null = terminal sem impressão de TEF.</summary>
    public Func<TransacaoPayGo, Task<bool>>? ImprimirComprovante { get; init; }

    /// <summary>
    /// CONFIRMACAO MANUAL (passos 37 a 40 do roteiro v20260819). Chamado numa venda aprovada
    /// com CNFREQ=1, DEPOIS de gravar e imprimir e ANTES do CNF automatico. A resposta decide o
    /// codigo que vai no PW_iConfirmation:
    ///   true  = o operador confirmou na mao: PWCNF_CNF_MANU_AUT (12833);
    ///   false = o operador desfez na mao:   PWCNF_REV_MANU_AUT (12849), a venda nao existe;
    ///   null  = ninguem quis decidir: PWCNF_CNF_AUTO (289), o de sempre.
    /// Quem decide QUANDO perguntar e a tela (so nos passos do roteiro que pedem isso); o
    /// provedor so obedece. Sem o gancho, ou com ele lancando, e o automatico.
    /// </summary>
    public Func<TransacaoPayGo, CancellationToken, Task<bool?>>? DecidirConfirmacao { get; init; }

    /// <summary>
    /// Pergunta à tela um dado que a automação não sabe (menu de redes sem rede pré-selecionada,
    /// dado digitado, senha do lojista). Devolve o valor (para menu: o `Valor` da opção) ou null
    /// para cancelar. Null aqui = nunca pergunta (cancela a captura).
    /// </summary>
    public Func<PwGetData, CancellationToken, Task<string?>>? Perguntar { get; init; }

    /// <summary>
    /// Mostra na tela do caixa o que a biblioteca mandou mostrar: uma mensagem de checkout ou o QR
    /// do Pix. Devolve `true` se a tela mostrou e o cliente pode pagar, `false` se o operador
    /// desistiu (o Esc do passo 55 do roteiro). Null aqui = o caixa não sabe mostrar, e aí a venda
    /// para com um texto claro em vez de morrer com "captura que o caixa não suporta".
    /// </summary>
    public Func<ExibicaoTef, CancellationToken, Task<bool>>? Exibir { get; init; }

    /// <summary>
    /// Fecha a tela que <see cref="Exibir"/> abriu. Chamada UMA vez, no fim da cobrança, com
    /// qualquer desfecho: paga, recusada, cancelada ou erro. Sem isto o QR fica na tela depois do
    /// cliente pagar, e o proximo cliente ve o QR do anterior.
    /// </summary>
    public Action? FecharExibicao { get; init; }

    /// <summary>Troca so o texto da exibicao ja aberta (o contador da biblioteca), sem recriar a janela.</summary>
    public Action<string>? AtualizarExibicao { get; init; }

    private readonly IPGWebLib _lib;
    private readonly string _pasta;
    private readonly OpcoesPGWebLib _op;
    private readonly SemaphoreSlim _um = new(1, 1);
    private bool _iniciada;
    private Timer? _idle;

    /// <summary>Horário (local) da próxima PW_iIdleProc, lido de PWINFO_IDLEPROCTIME. Null = não informado.</summary>
    public DateTime? ProximoIdle { get; private set; }

    /// <summary>Por que o último PW_iInit não deu (a frase que a tela mostra). Null = iniciada.</summary>
    public string? MotivoIndisponivel { get; private set; }

    /// <summary>
    /// O DETALHE por trás do motivo (o código que PW_iInit devolveu, a exceção ao carregar a
    /// DLL, a pasta que não abriu). "Não iniciou" sozinho deixou a Savassi no escuro em
    /// 11/09/2026; com o número na tela, o suporte sabe o que fazer sem pedir o log.
    /// </summary>
    public string? DetalheIndisponivel { get; private set; }

    /// <summary>De onde a DLL está sendo carregada (null = o Windows procura sozinho).</summary>
    public static string? PastaDaBiblioteca => PGWebLibNativa.PastaAtual;

    /// <summary>CNF/REV que a biblioteca não acusou: reenviados antes do próximo comando e no religamento.</summary>
    private readonly List<(TransacaoPayGo Tx, uint Resultado, string Depois)> _reenvios = new();

    public ProvedorPGWebLib(IPGWebLib lib, string pastaTrabalho, OpcoesPGWebLib opcoes)
    {
        _lib = lib ?? throw new ArgumentNullException(nameof(lib));
        _pasta = string.IsNullOrWhiteSpace(pastaTrabalho) ? PastaPadrao : pastaTrabalho;
        _op = opcoes ?? throw new ArgumentNullException(nameof(opcoes));
    }

    public string PastaTrabalho => _pasta;
    public bool Ocupado => _um.CurrentCount == 0;
    public string Descricao => "PayGo Windows (PGWebLib) em " + _pasta;

    /// <summary>As opções COM QUE ESTA INSTÂNCIA ESTÁ RODANDO (ambiente, redes, porta do pinpad).</summary>
    /// <remarks>
    /// O menu do TEF mostra isto em vez de reler a config: o que interessa a quem grava a
    /// homologação é o que está no ar, não o que está gravado esperando um Salvar.
    /// </remarks>
    public OpcoesPGWebLib Opcoes => _op;

    /// <summary>
    /// A última frase que a biblioteca escreveu em PWINFO_RESULTMSG ("TRANSACAO APROVADA",
    /// "OPERACAO CANCELADA"), de qualquer operação. Null enquanto ela não disser nada.
    ///
    /// Existe porque quase todo passo do roteiro de homologação cobra a frase exata que o
    /// operador viu, e ela vive um instante numa tela que já fechou.
    /// </summary>
    public string? UltimaMensagem { get; private set; }

    /// <summary>Quando <see cref="UltimaMensagem"/> chegou (hora do caixa, para casar com o roteiro).</summary>
    public DateTime? UltimaMensagemEm { get; private set; }

    /// <summary>Uma chamada à biblioteca que ainda não voltou.</summary>
    public sealed record ChamadaNativa(string Nome, DateTime DesdeUtc);

    private volatile ChamadaNativa? _emVoo;

    /// <summary>
    /// A VIGIA DA CHAMADA NATIVA. Null = nenhuma chamada em voo. Existe porque a biblioteca pode
    /// ficar minutos dentro de UMA chamada (14/09/2026: PW_iExecTransac das 19:00:54 que não voltou
    /// até o caixa ser morto) e nada no caixa sabia disso. A tela da instalação lê daqui para
    /// dizer há quanto tempo a maquininha não responde.
    /// </summary>
    public ChamadaNativa? EmVoo => _emVoo;

    /// <summary>A última coisa que a biblioteca pediu ou mostrou durante a operação (prompt, display do pinpad).</summary>
    public string? UltimoRecado { get; private set; }
    public DateTime? UltimoRecadoEm { get; private set; }

    private void Recado(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return;
        UltimoRecado = texto.Replace('\r', ' ').Replace('\n', ' ').Trim();
        UltimoRecadoEm = DateTime.Now;
    }

    private void Entrar(string nome) => _emVoo = new ChamadaNativa(nome, DateTime.UtcNow);
    private void Sair() => _emVoo = null;

    // ------------------------------------------------------------------ init / ativo


    /// <summary>
    /// SAI DA THREAD DA TELA ANTES DE TOCAR A BIBLIOTECA NATIVA.
    ///
    /// O QUE ACONTECIA (relatado em 08/09/2026): "quando vou no menu tef e instalar o
    /// ponto de captura o sistema trava". Travava mesmo, e nao era a maquininha.
    ///
    /// `await _um.WaitAsync(ct).ConfigureAwait(false)` NAO troca de thread quando o
    /// semaforo esta livre: um await que completa na hora continua na mesma thread.
    /// Como quase sempre esta livre, a primeira chamada nativa (PW_iInit,
    /// PW_iExecTransac) rodava na thread da TELA. E a instalacao e justamente a que
    /// faz o handshake com pos-transac-sb.tpgweb.io, entao a janela congelava pelo
    /// tempo da rede.
    ///
    /// O laco de ExecutarAsync ja saia da tela depois do primeiro `Task.Delay`. O que
    /// faltava era sair ANTES da primeira chamada, que e a demorada.
    /// </summary>
    /// <remarks>
    /// ⚠️ 14/09/2026, CASTELO: ISTO SOZINHO NÃO SAÍA DA TELA. Quem chamava fazia
    /// <c>await ForaDaTelaAsync();</c> sem <c>ConfigureAwait(false)</c>, e esse await volta para o
    /// contexto de quem chamou, que é a thread da tela. Provado num Dispatcher de WPF de verdade:
    /// 12 de 22 chamadas nativas caíam na tela, entre elas PW_iInit, PW_iNewTransac e o
    /// PW_iAddParam(USINGPINPAD) que varre as portas COM. Na loja isso foram 62 s de caixa
    /// "não respondendo" num clique. Todo chamador usa <c>.ConfigureAwait(false)</c> agora, e a
    /// bateria roda o provedor dentro de uma thread de tela para cobrar isso.
    /// </remarks>
    private static async Task ForaDaTelaAsync()
    {
        if (SynchronizationContext.Current is null) return;   // ja esta fora
        await Task.Run(static () => { }).ConfigureAwait(false);
    }

    /// <summary>PW_iInit uma vez por processo. PWRET_INVCALL = já iniciada (por nós ou por outro módulo) e serve.</summary>
    private bool Iniciar()
    {
        if (_iniciada) return true;
        // A DLL NÃO cria o diretório de trabalho: sem ele PW_iInit devolve PWRET_WRITERR (medido
        // em 07/09/2026 com a 4.1.50.24). Quem cria é o PDV, aqui, antes de cada tentativa.
        try { Directory.CreateDirectory(_pasta); }
        catch (Exception ex)
        {
            MotivoIndisponivel = MsgPastaInacessivel;
            DetalheIndisponivel = $"{_pasta}: {ex.Message}";
            Auditar?.Invoke($"pgweblib: pasta de trabalho inacessível ({_pasta}): {ex.Message}");
            return false;
        }
        short ret;
        Entrar("PW_iInit");
        try { ret = _lib.Init(_pasta); }
        catch (Exception ex)
        {
            // DllNotFoundException, BadImageFormatException (bitness), AccessViolation da convenção errada…
            MotivoIndisponivel = MsgTefNaoResponde;
            DetalheIndisponivel = ex is DllNotFoundException
                ? "PGWebLib.dll não foi encontrada" + (PGWebLibNativa.PastaAtual is { } pd ? " em " + pd : "")
                : ex.GetType().Name + ": " + ex.Message;
            Auditar?.Invoke("pgweblib: PW_iInit lançou: " + ex.GetType().Name + " " + ex.Message);
            return false;
        }
        finally { Sair(); }
        _iniciada = ret is PW.PWRET_OK or PW.PWRET_INVCALL;
        if (!_iniciada)
        {
            MotivoIndisponivel = MsgTefNaoResponde;
            DetalheIndisponivel = $"PW_iInit devolveu {PW.Nome(ret)} ({ret})"
                + (ret == PW.PWRET_TPNPIXERROR ? ". Esse código é o da biblioteca protegida rodando fora da pasta do PayGo Windows" : "")
                + (PGWebLibNativa.PastaAtual is { } pr ? $". Biblioteca em {pr}" : "");
            Auditar?.Invoke("pgweblib: PW_iInit devolveu " + PW.Nome(ret));
            return false;
        }
        MotivoIndisponivel = null; DetalheIndisponivel = null;

        // A PROTECAO, logo depois do PW_iInit e antes de qualquer transacao.
        //
        // Com o PayGo Windows instalado quem ligava a protecao era ele. O kit avulso 4.1.50.924 nao
        // tem Warsaw nenhum no binario, mas EXPORTA PW_iInitProcess, que o cabecalho oficial
        // descreve como "forca iniciar o processo de protecao", e declara PWRET_PROTECTOFF para
        // "protecao nao ativa". Ou seja: a protecao nao sumiu, mudou de dono. Quem liga agora somos
        // nos.
        //
        // Nao derruba o caixa: biblioteca antiga nao exporta o simbolo e o P/Invoke lanca. O que
        // ela NAO pode e ficar calada, porque foi calada assim que a atualizacao de certificado
        // ficou em tempo esgotado no teste de 07/09 as 17h13.
        try
        {
            var prot = _lib.InitProcess();
            if (prot != PW.PWRET_OK)
                Auditar?.Invoke($"pgweblib: PW_iInitProcess devolveu {PW.Nome(prot)}"
                    + (prot == PW.PWRET_PROTECTOFF ? " (protecao nao ativa)" : ""));
        }
        catch (EntryPointNotFoundException)
        {
            Auditar?.Invoke("pgweblib: PW_iInitProcess nao existe nesta biblioteca; a protecao, se houver, vem de fora");
        }
        catch (Exception ex)
        {
            Auditar?.Invoke("pgweblib: PW_iInitProcess falhou (" + ex.GetType().Name + "), seguindo");
        }

        // O AMBIENTE vem DEPOIS do PW_iInit, e isso foi medido, não deduzido.
        //
        // 07/09/2026 17h13, com a biblioteca avulsa e o terminal ainda sem instalação: chamada
        // ANTES do PW_iInit, PW_iSetEnvironment devolveu PWRET_NOTINST e o ambiente NÃO foi
        // aplicado, ou seja, o caixa continuaria falando com o host de produção sem ninguém
        // perceber. Faz sentido: antes do PW_iInit a biblioteca ainda não sabe nem qual é a pasta
        // de trabalho. O que o cabeçalho oficial exige é que ela venha antes de o PONTO DE CAPTURA
        // estar instalado, e aqui ela vem.
        //
        // Recusa continua não derrubando o caixa: vira auditoria. Mas agora a auditoria diz que o
        // ambiente pedido não valeu, em vez de dizer que o terminal já estava instalado.
        try
        {
            var amb = _lib.SetEnvironment(_op.Ambiente);
            if (amb != PW.PWRET_OK)
                Auditar?.Invoke($"pgweblib: PW_iSetEnvironment({ConfigPGWebLib.RotuloAmbiente(_op.Ambiente)}) devolveu {PW.Nome(amb)}: o ambiente pedido NAO foi aplicado");
        }
        catch (Exception ex)
        {
            // Biblioteca anterior à 4.1.43.10 não exporta a função. Seguir no ambiente padrão dela
            // é o comportamento que já existia, então não derruba o caixa.
            Auditar?.Invoke("pgweblib: PW_iSetEnvironment indisponível (" + ex.GetType().Name + "), seguindo no ambiente padrão da biblioteca");
        }

        // Spec: depois do PW_iInit (e de cada PW_iIdleProc) ler PWINFO_IDLEPROCTIME para saber
        // quando chamar o próximo PW_iIdleProc.
        AgendarIdle(Ler(PW.PWINFO_IDLEPROCTIME), "PW_iInit");
        return true;
    }

    /// <summary>O que <see cref="Encerrar"/> fez.</summary>
    public enum Encerramento
    {
        /// <summary>Esta instância nunca iniciou a DLL: PW_End não chamado (outra instância, já trocada, pode ter iniciado; Servicos.EncerrarTef cobre).</summary>
        NaoIniciada,
        /// <summary>PW_End chamado.</summary>
        Encerrada,
        /// <summary>Operação em voo até o teto: PW_End não chamado.</summary>
        EmVoo,
        /// <summary>PW_End lançou.</summary>
        Falhou,
    }

    /// <summary>
    /// PW_End no fechamento do processo. Medido em 07/09/2026 (PGWebLib.dll 4.1.50.24, x86): a
    /// biblioteca iniciada e NÃO encerrada aborta o processo no DLL_PROCESS_DETACH (fail-fast
    /// 0xC0000409, depois do Main devolver 0; o log dela mostra PGWLib_End -> warsaw_sdk::Initialize).
    /// PW_End é a mesma rotina, chamada com o processo inteiro de pé: ela termina (~2 s) e zera o
    /// estado, e o detach vira no-op. Nunca por cima de uma operação em voo: espera até
    /// <paramref name="esperaMs"/> pelo semáforo e desiste com auditoria. Mata o timer do idle antes.
    /// </summary>
    public Encerramento Encerrar(int esperaMs = 5_000)
    {
        Dispose();
        if (!_um.Wait(Math.Max(0, esperaMs)))
        {
            Auditar?.Invoke("pgweblib: PW_End não chamado: operação em voo no fechamento");
            return Encerramento.EmVoo;
        }
        try
        {
            if (!_iniciada) return Encerramento.NaoIniciada;
            _iniciada = false;
            ProximoIdle = null;
            try { _lib.End(); }
            catch (Exception ex)
            {
                Auditar?.Invoke("pgweblib: PW_End lançou: " + ex.GetType().Name + " " + ex.Message);
                return Encerramento.Falhou;
            }
            return Encerramento.Encerrada;
        }
        finally { _um.Release(); }
    }

    /// <summary>
    /// A lista de operações de VENDA prova que o ponto de captura está instalado? PWRET_OK com
    /// pelo menos uma operação. É a regra que decide se o caixa oferece cartão (<see
    /// cref="AtivoAsync"/>) e é a MESMA que o menu do TEF mostra no quadro de estado: um quadro
    /// dizendo "instalado" para um terminal que a venda vai recusar seria pior que quadro nenhum.
    /// </summary>
    public static bool InstaladoPelaLista(short retorno, int quantasOperacoes)
        => retorno == PW.PWRET_OK && quantasOperacoes > 0;

    /// <summary>
    /// PW_iGetOperations cru, para o menu do TEF montar os itens com o que ESTE terminal oferece
    /// (<see cref="PW.OPERACOES_ADMINISTRATIVAS"/>, <see cref="PW.OPERACOES_DE_VENDA"/> ou
    /// <see cref="PW.OPERACOES_TODAS"/>). Devolve o PWRET_* e a lista, sem interpretar: quem
    /// decide o que fazer com PWRET_NOTINST é quem chamou.
    /// </summary>
    public async Task<(short Retorno, IReadOnlyList<PwOperacao> Operacoes)> OperacoesAsync(byte tipo, CancellationToken ct)
    {
        await _um.WaitAsync(ct).ConfigureAwait(false);
        await ForaDaTelaAsync().ConfigureAwait(false);
        try
        {
            if (!Iniciar()) return (PW.PWRET_DLLNOTINIT, Array.Empty<PwOperacao>());
            try
            {
                var ret = _lib.GetOperations(tipo, out var ops);
                return (ret, ops ?? (IReadOnlyList<PwOperacao>)Array.Empty<PwOperacao>());
            }
            catch (Exception ex)
            {
                Auditar?.Invoke("pgweblib: PW_iGetOperations lançou: " + ex.GetType().Name + " " + ex.Message);
                return (PW.PWRET_DLLNOTINIT, Array.Empty<PwOperacao>());
            }
        }
        finally { _um.Release(); }
    }

    public async Task<bool> AtivoAsync(CancellationToken ct)
    {
        await _um.WaitAsync(ct).ConfigureAwait(false);
        await ForaDaTelaAsync().ConfigureAwait(false);
        try
        {
            if (!Iniciar()) return false;
            // Iniciar() só prova que a biblioteca subiu. Terminal SEM INSTALAÇÃO sobe
            // exatamente igual e só recusa na hora de cobrar. Medido em 07/09/2026 com a
            // PGWebLib 4.1.50.924 numa pasta de trabalho nova: PW_iInit devolve PWRET_OK,
            // o horário de idle vem no sentinela 551231235959 e a lista de operações de
            // venda devolve PWRET_NOTINST. Responder "ativo" aqui faria a tela do caixa
            // oferecer cartão e falhar só depois de o cliente já estar esperando.
            //
            // A checagem fica AQUI e não dentro de Iniciar() de propósito: o menu
            // administrativo precisa continuar abrindo justamente para o operador poder
            // rodar a INSTALAÇÃO. Fechar tudo travaria a única saída.
            short ret;
            IReadOnlyList<PwOperacao> ops;
            try { ret = _lib.GetOperations(PW.OPERACOES_DE_VENDA, out ops); }
            catch (Exception ex)
            {
                MotivoIndisponivel = MsgTefNaoResponde;
                Auditar?.Invoke("pgweblib: PW_iGetOperations lançou: " + ex.GetType().Name + " " + ex.Message);
                return false;
            }
            var quantas = ops?.Count ?? 0;
            if (!InstaladoPelaLista(ret, quantas))
            {
                MotivoIndisponivel = ret is PW.PWRET_NOTINST ? MsgNaoInstalado : MsgTefNaoResponde;
                Auditar?.Invoke($"pgweblib: sem operação de venda (PW_iGetOperations={PW.Nome(ret)}, {quantas} operações)");
                return false;
            }
            MotivoIndisponivel = null;
            return true;
        }
        finally { _um.Release(); }
    }

    // ------------------------------------------------------------------ venda

    public async Task<DesfechoTef> CobrarAsync(TipoTef tipo, Dinheiro valor, string? documento, int parcelas,
        IProgress<AndamentoTef>? andamento, CancellationToken ct)
    {
        var id = ClientePayGo.NovaIdentificacao();
        var chargeId = "pgweb-" + id;
        if (!valor.Positivo)
            return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "valor da cobrança tem que ser maior que zero");
        var parc = tipo == TipoTef.Credito ? Math.Max(1, parcelas) : 1;
        andamento?.Report(new AndamentoTef(FaseTef.Criando, chargeId, null, "Enviando a cobrança para o TEF…"));

        try { await _um.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Falha(SituacaoTef.Cancelado, chargeId, CodigoTef.Cancelado, "cobrança cancelada pelo operador"); }
        await ForaDaTelaAsync().ConfigureAwait(false);   // a cobranca e a mais longa de todas: nunca na thread da tela
        Contexto? ctxExibicao = null;
        try
        {
            var prep = await PrepararAsync(PW.PWOPER_SALE, chargeId).ConfigureAwait(false);
            if (prep is not null) return prep;

            var ctx = new Contexto(chargeId, id, "CRT", tipo, valor.Centavos, parc, andamento, ct);
            ctxExibicao = ctx;
            ParametrosDeIdentidade(ctx);
            Param(ctx, PW.PWINFO_TOTAMNT, valor.Centavos.ToString(CultureInfo.InvariantCulture));
            Param(ctx, PW.PWINFO_CURRENCY, _op.Moeda);
            Param(ctx, PW.PWINFO_CURREXP, "2");
            if (!string.IsNullOrWhiteSpace(documento)) Param(ctx, PW.PWINFO_FISCALREF, documento!);
            if (tipo == TipoTef.Pix)
            {
                Param(ctx, PW.PWINFO_PAYMNTTYPE, PW.PAYMNTTYPE_CARTEIRA_DIGITAL);
                // OS DOIS CAMPOS, como a biblioteca faz quando o operador escolhe no
                // menu. So o 0x35 devolve [NA A266] MENSAGEM INVALIDA: ver a nota em
                // PW.PWINFO_AUTHSYSTNOME, com os dois logs lado a lado.
                if (!string.IsNullOrWhiteSpace(_op.RedePix))
                {
                    Param(ctx, PW.PWINFO_AUTHSYST, _op.RedePix!);
                    Param(ctx, PW.PWINFO_AUTHSYSTNOME, _op.RedePix!);
                }
                // Onde o QR sai. Sem isto a biblioteca manda para o pinpad mesmo com CAP_QR.
                if (!string.IsNullOrWhiteSpace(_op.PreferenciaQr))
                    Param(ctx, PW.PWINFO_DSPQRPREF, _op.PreferenciaQr!);
            }
            else
            {
                Param(ctx, PW.PWINFO_PAYMNTTYPE, PW.PAYMNTTYPE_CARTAO);
                Param(ctx, PW.PWINFO_CARDTYPE, tipo switch
                {
                    TipoTef.Credito => PW.CARDTYPE_CREDITO,
                    TipoTef.Debito => PW.CARDTYPE_DEBITO,
                    _ => PW.CARDTYPE_VOUCHER,
                });
                Param(ctx, PW.PWINFO_FINTYPE, parc > 1 ? PW.FINTYPE_PARCELADO_LOJA : PW.FINTYPE_AVISTA);
                if (parc > 1) Param(ctx, PW.PWINFO_INSTALLMENTS, parc.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrWhiteSpace(_op.RedeCartao))
                {
                    Param(ctx, PW.PWINFO_AUTHSYST, _op.RedeCartao!);
                    Param(ctx, PW.PWINFO_AUTHSYSTNOME, _op.RedeCartao!);
                }
            }
            ctx.Porta = PortaDoPinpad.Normalizar(_op.PortaPinpad);
            Param(ctx, PW.PWINFO_USINGPINPAD, "1");
            Param(ctx, PW.PWINFO_PPCOMMPORT, ctx.Porta);
            // Sem pinpad a venda não entra na chamada longa. Exceção: Pix com o QR na TELA, que
            // pode seguir sem pinpad nenhum.
            if (!(tipo == TipoTef.Pix && _op.PreferenciaQr == PW.DSPQRPREF_TELA) && PinpadAusente(ctx) is { } semPinpad)
                return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, semPinpad);

            // A partir daqui a biblioteca pode ir ao host: linha 'aguardando' antes de executar.
            Guardar(new TransacaoPayGo(chargeId, id, tipo, valor.Centavos, parc, "aguardando", null));
            andamento?.Report(new AndamentoTef(FaseTef.Aguardando, chargeId, id,
                tipo == TipoTef.Pix ? "Peça ao cliente para ler o QR no pinpad…" : "Aproxime, insira ou passe o cartão no pinpad…"));

            var fim = await ExecutarAsync(ctx).ConfigureAwait(false);
            var r = RespostaDaLib("CRT", id, valor.Centavos, tipo, parc, fim.Aprovada, fim.Resultados);

            if (!fim.Aprovada)
            {
                // Terminou sem aprovação: se a biblioteca marcou CNFREQ=1 (aprovou e o operador
                // interrompeu no meio, ou o host aprovou e a captura caiu) desfaz antes de sair.
                if (fim.RequerConfirmacao)
                    await DesfazerSeRequeridoAsync(ctx, fim, r).ConfigureAwait(false);   // grava 'aprovada' -> 'desfeita'
                else
                {
                    // Sem prova de que o pinpad parou (PosOcupado) é ÓRFÃ: alguém confere no PayGo.
                    var sit = fim.Situacao switch
                    {
                        _ when fim.PosOcupado => "orfa",
                        SituacaoTef.Cancelado or SituacaoTef.Timeout => "cancelado",
                        SituacaoTef.Recusado => "recusado",
                        _ => "erro",
                    };
                    Guardar(new TransacaoPayGo(chargeId, id, tipo, valor.Centavos, parc, sit, r, fim.Motivo));
                }
                // O REQNUM SOBE TAMBEM NA RECUSA (09/09/2026). Varios passos do roteiro
                // sao negada de proposito (o 4 e R$ 1.000,01, que a rede recusa), e o
                // cancelamento tem quatro passos so para ele. A planilha exige o
                // PWINFO_REQNUM neles do mesmo jeito: o que se prova ali e a recusa ter
                // funcionado, e ela tem numero como qualquer outra transacao.
                // Tipo e rede fixada sobem junto (14/09/2026): e com eles que a tela traduz uma
                // recusa do host e sugere a rede em automatico quando a fixada nao vale.
                return new DesfechoTef(fim.Situacao, id, chargeId, null, fim.Motivo, fim.PosOcupado)
                {
                    Codigo = fim.Codigo, Desfeita = fim.Desfeita, Reqnum = r.CodigoControle,
                    Tipo = tipo,
                    RedeFixada = !string.IsNullOrWhiteSpace(tipo == TipoTef.Pix ? _op.RedePix : _op.RedeCartao),
                };
            }

            var tx = new TransacaoPayGo(chargeId, id, tipo, valor.Centavos, parc, "aprovada", r);
            var cartao = Cartao(r);
            if (fim.Nota is not null)
                Auditar?.Invoke($"pgweblib: {chargeId} aprovada e definitiva (CNFREQ=0); saída depois da aprovação ignorada: {fim.Nota}");

            // MEMÓRIA NÃO VOLÁTIL ANTES DA CONFIRMAÇÃO. Não gravou = desfaz (REV): cliente cobrado
            // sem o PDV saber é o pior desfecho possível. Com CNFREQ=0 NÃO existe REV: a rede já
            // efetivou; a linha vira 'orfa' e a tela manda conferir, nunca "cobre de novo".
            if (!GuardarSeguro(tx))
            {
                if (!fim.RequerConfirmacao) return Orfa(tx, cartao, MsgNaoGravada(r.Nsu));
                if (Desfazer(tx with { Motivo = "não gravou no caixa (REV)" }, PW.PWCNF_REV_OTHER_AUT, "desfeita") == Ack.Desconhecida)
                    return Orfa(tx, cartao, MsgNaoReconhece(r.Nsu), gravar: false);
                return new DesfechoTef(SituacaoTef.Erro, id, chargeId, cartao,
                    "não consegui gravar a transação no caixa: transação desfeita, cobre de novo", false)
                { Codigo = CodigoTef.Plataforma, Desfeita = true };
            }

            // Operador desistiu depois da aprovação: só dá para atender com REV (CNFREQ=1). Com
            // CNFREQ=0 o dinheiro já andou; a venda segue como paga.
            if (fim.RequerConfirmacao && ctx.Ct.IsCancellationRequested)
            {
                if (Desfazer(tx with { Motivo = "cancelada pelo operador (REV)" }, PW.PWCNF_REV_ABORT, "desfeita") == Ack.Desconhecida)
                    return Orfa(tx, cartao, MsgNaoReconhece(r.Nsu), gravar: false);
                return new DesfechoTef(SituacaoTef.Cancelado, id, chargeId, null, "cobrança cancelada pelo operador: transação desfeita", false)
                { Codigo = CodigoTef.Cancelado, Desfeita = true };
            }

            var situacao = "pago";
            if (fim.RequerConfirmacao)
            {
                // Comprovante ANTES da confirmação (spec: a impressão decide o commit).
                if (!await ImprimirSeguroAsync(tx).ConfigureAwait(false))
                {
                    if (Desfazer(tx with { Motivo = "comprovante não impresso (REV)" }, PW.PWCNF_REV_PRN_AUT, "desfeita") == Ack.Desconhecida)
                        return Orfa(tx, cartao, MsgNaoReconhece(r.Nsu), gravar: false);
                    Auditar?.Invoke($"pgweblib: {chargeId} desfeita (comprovante não saiu)");
                    return new DesfechoTef(SituacaoTef.Cancelado, id, chargeId, cartao,
                        ClientePayGo.MsgCancelada(r.Rede, r.Nsu, r.ValorCent ?? valor.Centavos), false)
                    { Codigo = CodigoTef.Cancelado, Desfeita = true };
                }
                // CONFIRMACAO MANUAL (passos 37 a 40 do roteiro v20260819). A rede aprovou, o
                // caixa gravou e o comprovante saiu. Antes do CNF automatico, quem esta na tela
                // pode mandar confirmar (PWCNF_CNF_MANU_AUT) ou desfazer (PWCNF_REV_MANU_AUT).
                // Ate 09/09/2026 o caixa so sabia confirmar sozinho (289), e o roteiro cobra
                // o codigo manual no log: 12833 na confirmacao, 12849 no desfazimento.
                var codigoCnf = PW.PWCNF_CNF_AUTO;
                if (DecidirConfirmacao is { } decidir)
                {
                    bool? manual = null;
                    try { manual = await decidir(tx, ctx.Ct).ConfigureAwait(false); }
                    catch (Exception ex) { Auditar?.Invoke($"pgweblib: {chargeId} a decisao manual da confirmacao lancou {ex.GetType().Name}: segue automatica"); }
                    if (manual == false)
                    {
                        if (Desfazer(tx with { Motivo = "desfeita pelo operador (REV_MANU_AUT)" }, PW.PWCNF_REV_MANU_AUT, "desfeita") == Ack.Desconhecida)
                            return Orfa(tx, cartao, MsgNaoReconhece(r.Nsu), gravar: false);
                        Auditar?.Invoke($"pgweblib: {chargeId} desfeita pelo operador (PWCNF_REV_MANU_AUT {PW.PWCNF_REV_MANU_AUT}) REQNUM {r.CodigoControle}");
                        // A tela completa com "A cobrança foi desfeita: o cliente não pagou nada".
                        return new DesfechoTef(SituacaoTef.Cancelado, id, chargeId, cartao,
                            "venda desfeita pelo operador", false)
                        { Codigo = CodigoTef.Cancelado, Desfeita = true, Reqnum = r.CodigoControle };
                    }
                    if (manual == true) codigoCnf = PW.PWCNF_CNF_MANU_AUT;
                }
                switch (Confirmar(tx, "pago", codigoCnf))
                {
                    case Ack.Ok: break;
                    case Ack.SemAck: situacao = "cnf_sem_ack"; break;
                    default: return Orfa(tx, cartao, MsgNaoReconhece(r.Nsu), gravar: false);
                }
            }
            else
            {
                Guardar(tx with { Situacao = "pago" });
                if (!await ImprimirSeguroAsync(tx).ConfigureAwait(false))
                    Auditar?.Invoke($"pgweblib: {chargeId} paga sem confirmação (CNFREQ=0) com comprovante não impresso");
            }

            // A mensagem da REDE (PWINFO_RESULTMSG, "TRANSACAO APROVADA") sobe junto com a
            // aprovação: é ela que o passo 29 do roteiro v20260819 manda o operador ler no caixa.
            // A administrativa já fazia assim; a venda voltava muda e a tela não tinha o que mostrar.
            return new DesfechoTef(SituacaoTef.Pago, id, chargeId, cartao, r.Mensagem, false)
            // O REQNUM sobe TAMBEM na venda. Ele ja subia nas administrativas, e eu
            // deixei o caminho da venda de fora: a planilha de homologacao saiu com a
            // coluna vazia justamente nos passos que sao venda, que sao a maioria.
            { Codigo = CodigoTef.Pago, PaymentStatus = situacao, Reqnum = r.CodigoControle };
        }
        finally
        {
            // A tela do QR fecha com QUALQUER desfecho: paga, recusada, cancelada ou erro.
            // Fica aqui, no finally da cobranca, porque e o unico ponto por onde todos passam.
            if (ctxExibicao is { Exibiu: true })
            {
                try { FecharExibicao?.Invoke(); }
                catch (Exception ex) { Auditar?.Invoke("pgweblib: fechar a tela de exibicao lancou: " + ex.GetType().Name); }
            }
            _um.Release();
        }
    }

    // ------------------------------------------------------------------ cancelamento (SALEVOID)

    public async Task<DesfechoTef> CancelarAsync(TransacaoPayGo original, CancellationToken ct)
    {
        var r0 = original.Resposta;
        var id = ClientePayGo.NovaIdentificacao();
        var chargeId = "pgweb-cnc-" + id;
        if (r0 is null || string.IsNullOrWhiteSpace(r0.Nsu))
            return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "transação original sem NSU: cancele pelo menu do PayGo");

        await _um.WaitAsync(ct).ConfigureAwait(false);
        await ForaDaTelaAsync().ConfigureAwait(false);
        try
        {
            var prep = await PrepararAsync(PW.PWOPER_SALEVOID, chargeId).ConfigureAwait(false);
            if (prep is not null) return prep;

            var ctx = new Contexto(chargeId, id, "CNC", original.Tipo, original.ValorCent, original.Parcelas, null, ct);
            ParametrosDeIdentidade(ctx);
            Param(ctx, PW.PWINFO_CURRENCY, _op.Moeda);
            Param(ctx, PW.PWINFO_CURREXP, "2");
            Param(ctx, PW.PWINFO_TRNORIGNSU, r0.Nsu!);
            Param(ctx, PW.PWINFO_TRNORIGAMNT, original.ValorCent.ToString(CultureInfo.InvariantCulture));
            // O QUE O CAIXA JA SABE, O OPERADOR NAO DIGITA (09/09/2026). Medido no log: num
            // estorno de R$ 2,00 a biblioteca parou quatro vezes pedindo valor (0x25), data
            // (0x57), forma de pagamento (0x29) e a Referencia Local (0x78). Tudo isso esta na
            // venda guardada. Pior: o valor vai em CENTAVOS, entao quem digitava "2" estornava
            // dois centavos e quem digitava "2,00" mandava uma vírgula para o host.
            // Mandando aqui, a biblioteca nao pergunta; e se perguntar assim mesmo, o caixa
            // responde sozinho pelo caminho de Predefinido.
            Param(ctx, PW.PWINFO_TOTAMNT, original.ValorCent.ToString(CultureInfo.InvariantCulture));
            Param(ctx, PW.PWINFO_CARDTYPE, original.Tipo switch
            {
                TipoTef.Debito => PW.CARDTYPE_DEBITO,
                TipoTef.Voucher => PW.CARDTYPE_VOUCHER,
                _ => PW.CARDTYPE_CREDITO,
            });
            var locref = r0.Campos.GetValueOrDefault("950-000")?.Trim();
            if (!string.IsNullOrEmpty(locref)) Param(ctx, PW.PWINFO_TRNORIGLOCREF, locref!);
            if (r0.Autorizacao is not null) Param(ctx, PW.PWINFO_TRNORIGAUTH, r0.Autorizacao);
            if (r0.CodigoControle is not null) Param(ctx, PW.PWINFO_TRNORIGREQNUM, r0.CodigoControle);
            var (data, hora) = DataHoraOriginal(r0);
            if (data is not null) Param(ctx, PW.PWINFO_TRNORIGDATE, data);
            if (hora is not null) Param(ctx, PW.PWINFO_TRNORIGTIME, hora);
            if (r0.Rede is not null) Param(ctx, PW.PWINFO_AUTHSYST, r0.Rede);
            ctx.Porta = PortaDoPinpad.Normalizar(_op.PortaPinpad);
            Param(ctx, PW.PWINFO_USINGPINPAD, "1");
            Param(ctx, PW.PWINFO_PPCOMMPORT, ctx.Porta);
            if (PinpadAusente(ctx) is { } semPinpadCnc)
                return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, semPinpadCnc);

            var fim = await ExecutarAsync(ctx).ConfigureAwait(false);
            var r = RespostaDaLib("CNC", id, original.ValorCent, original.Tipo, original.Parcelas, fim.Aprovada, fim.Resultados);
            if (!fim.Aprovada)
            {
                await DesfazerSeRequeridoAsync(ctx, fim, r).ConfigureAwait(false);
                // O REQNUM sobe tambem na recusa (09/09/2026): o passo 57 e um estorno NEGADO
                // pelo host, e a planilha cobra o numero dele como o de qualquer transacao.
                return new DesfechoTef(fim.Situacao, id, chargeId, null, fim.Motivo, fim.PosOcupado) { Codigo = fim.Codigo, Desfeita = fim.Desfeita, Reqnum = r.CodigoControle };
            }

            var tx = new TransacaoPayGo(chargeId, id, original.Tipo, original.ValorCent, original.Parcelas, "aprovada", r, "cancelamento de " + original.ChargeId);
            if (fim.Nota is not null)
                Auditar?.Invoke($"pgweblib: {chargeId} cancelamento aprovado e definitivo (CNFREQ=0); saída depois da aprovação ignorada: {fim.Nota}");
            if (!GuardarSeguro(tx))
            {
                // Sem confirmação pendente não há REV: o cancelamento já vale na rede. Órfã, nunca "desfeito".
                if (!fim.RequerConfirmacao) return Orfa(tx, Cartao(r), MsgCancelamentoNaoGravado(r.Nsu));
                if (Desfazer(tx with { Motivo = "não gravou o cancelamento (REV)" }, PW.PWCNF_REV_OTHER_AUT, "desfeita") == Ack.Desconhecida)
                    return Orfa(tx, Cartao(r), MsgNaoReconhece(r.Nsu), gravar: false);
                return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "não consegui gravar o cancelamento (desfeito)");
            }
            var sit = "estornado";
            if (fim.RequerConfirmacao)
            {
                if (!await ImprimirSeguroAsync(tx).ConfigureAwait(false))
                {
                    if (Desfazer(tx with { Motivo = "comprovante do cancelamento não impresso (REV)" }, PW.PWCNF_REV_PRN_AUT, "desfeita") == Ack.Desconhecida)
                        return Orfa(tx, Cartao(r), MsgNaoReconhece(r.Nsu), gravar: false);
                    return new DesfechoTef(SituacaoTef.Cancelado, id, chargeId, Cartao(r),
                        ClientePayGo.MsgCancelada(r.Rede, r.Nsu, r.ValorCent ?? original.ValorCent), false)
                    { Codigo = CodigoTef.Cancelado, Desfeita = true };
                }
                switch (Confirmar(tx, "estornado"))
                {
                    case Ack.Ok: break;
                    case Ack.SemAck: sit = "cnf_sem_ack"; break;
                    default: return Orfa(tx, Cartao(r), MsgNaoReconhece(r.Nsu), gravar: false);
                }
            }
            else
            {
                Guardar(tx with { Situacao = "estornado" });
                await ImprimirSeguroAsync(tx).ConfigureAwait(false);
            }
            Guardar(original with { Situacao = "estornada", Motivo = "estornada por " + chargeId });
            // A frase da REDE sobe com o cancelamento aprovado, como já sobe com a venda: os passos
            // 44 e 46 do roteiro v20260819 pedem "TRANSAÇÃO APROVADA" para o operador DEPOIS do
            // cancelamento, não só depois da venda. Antes o desfecho vinha mudo e a tela do estorno
            // não tinha o que mostrar. Biblioteca calada continua devolvendo vazio, nunca uma frase
            // nossa disfarçada de resposta da rede.
            return new DesfechoTef(SituacaoTef.Pago, id, chargeId, Cartao(r), r.Mensagem, false) { Codigo = CodigoTef.Pago, PaymentStatus = sit, Reqnum = r.CodigoControle };
        }
        finally { _um.Release(); }
    }

    // ------------------------------------------------------------------ administrativa / reimpressão / instalação

    public Task<DesfechoTef> AdministrativaAsync(CancellationToken ct) => OperacaoAsync(PW.PWOPER_ADMIN, "pgweb-adm-", "administrativa", ct);

    /// <summary>PWOPER_REPRINT: a biblioteca reimprime a última (ou a escolhida no menu). As vias guardadas continuam no menu Reimpressão.</summary>
    public Task<DesfechoTef> ReimprimirAsync(CancellationToken ct) => OperacaoAsync(PW.PWOPER_REPRINT, "pgweb-rep-", "reimpressão", ct);

    /// <summary>PWOPER_INSTALL: ativação do ponto de captura (CNPJ + PdC). Só uma vez por terminal.</summary>
    public Task<DesfechoTef> InstalarAsync(CancellationToken ct) => OperacaoAsync(PW.PWOPER_INSTALL, "pgweb-inst-", "instalação", ct);

    /// <summary>
    /// Uma operação escolhida no MENU DO TEF (só existe no caixa de homologação). Não é caminho
    /// novo: instalação, reimpressão e administrativa caem nos MESMOS três métodos acima, e o
    /// resto passa pelo mesmo <c>OperacaoAsync</c> que eles usam.
    ///
    /// Duas coisas seguram aqui, e as duas são de dinheiro:
    ///
    ///   · operação de VALOR (venda, cancelamento, recarga) é recusada, mesmo que a tela peça.
    ///     Elas têm valor e dono no caixa: a venda sai da comanda e o cancelamento sai do estorno,
    ///     que são quem grava a linha em `tef_transacao`. Recusar aqui é cinto: a tela nem desenha
    ///     botão para elas, mas defesa que mora só na tela é decoração;
    ///   · o chargeId sai com o prefixo "pgweb-adm-" (ou o próprio da instalação/reimpressão).
    ///     Não é enfeite: é por "-adm-", "-rep-" e "-inst-" que o religamento reconhece uma
    ///     operação administrativa e a fecha como 'adm'. Prefixo novo faria uma pendência de
    ///     relatório voltar do boot como 'pago', dinheiro que ninguém pagou.
    /// </summary>
    public Task<DesfechoTef> OperacaoDoMenuAsync(byte oper, string rotulo, CancellationToken ct)
    {
        if (PW.EhOperacaoDeValor(oper))
        {
            Auditar?.Invoke($"pgweblib: menu do TEF recusou a operação de valor {oper} ({rotulo})");
            return Task.FromResult(Falha(SituacaoTef.Erro, "pgweb-menu-recusada", CodigoTef.Plataforma,
                "Esta operação tem valor e sai pela comanda, não por este menu."));
        }
        return oper switch
        {
            PW.PWOPER_INSTALL => InstalarAsync(ct),
            PW.PWOPER_REPRINT => ReimprimirAsync(ct),
            PW.PWOPER_ADMIN => AdministrativaAsync(ct),
            _ => OperacaoAsync(oper, "pgweb-adm-", rotulo, ct),
        };
    }

    private async Task<DesfechoTef> OperacaoAsync(byte oper, string prefixo, string rotulo, CancellationToken ct)
    {
        var id = ClientePayGo.NovaIdentificacao();
        var chargeId = prefixo + id;
        // NUNCA ESPERAR CALADO ATRÁS DE OUTRA OPERAÇÃO (14/09/2026, Castelo). A segunda instalação
        // esperava no semáforo, sem teto e sem aviso, a primeira sair de uma chamada presa há minutos.
        try
        {
            if (!await _um.WaitAsync(EsperaOcupadoMs, ct).ConfigureAwait(false))
            {
                Auditar?.Invoke($"pgweblib: {rotulo} recusada: outra operação ainda em voo ({EmVoo?.Nome ?? "sem chamada nativa"})");
                return Falha(SituacaoTef.Erro, chargeId, CodigoTef.TefNaoResponde, MsgAindaOcupado);
            }
        }
        catch (OperationCanceledException)
        {
            return Falha(SituacaoTef.Cancelado, chargeId, CodigoTef.Cancelado, "operação cancelada pelo operador");
        }
        await ForaDaTelaAsync().ConfigureAwait(false);
        try
        {
            UltimoRecado = null; UltimoRecadoEm = null;
            string? portaDaVez = null;
            if (oper == PW.PWOPER_INSTALL && ConferirPinpad is { } conferir)
            {
                // O TESTE DO PINPAD ANTES DE TOCAR NA BIBLIOTECA. Nem o PW_iInit: com uma porta
                // guardada, ele mesmo abre o pinpad e fica 20 s parado (medido na Castelo).
                Recado("Procurando o pinpad");
                ResultadoTestePinpad? teste = null;
                try { teste = await conferir(_op.PortaPinpad, ct).ConfigureAwait(false); }
                catch (Exception ex) { Auditar?.Invoke("pgweblib: o teste do pinpad lançou " + ex.GetType().Name + "; a instalação segue sem ele"); }
                if (teste is { Ok: false })
                {
                    Auditar?.Invoke($"pgweblib: instalação não chamou a biblioteca: teste do pinpad {teste.Situacao} ({teste.Frase})");
                    Recado(teste.Frase);
                    return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, teste.Frase);
                }
                if (teste is { Ok: true })
                {
                    Recado(teste.Frase);
                    portaDaVez = teste.Numero;
                    Auditar?.Invoke($"pgweblib: teste do pinpad antes da instalação: {teste.Frase}");
                }
            }
            if (ct.IsCancellationRequested)
                return Falha(SituacaoTef.Cancelado, chargeId, CodigoTef.Cancelado, "operação cancelada pelo operador");
            var prep = await PrepararAsync(oper, chargeId).ConfigureAwait(false);
            if (prep is not null) return prep;
            var ctx = new Contexto(chargeId, id, "ADM", TipoTef.Credito, 0, 1, null, ct);
            ctx.Porta = portaDaVez ?? PortaDoPinpad.Normalizar(_op.PortaPinpad);
            ParametrosDeIdentidade(ctx);
            Param(ctx, PW.PWINFO_USINGPINPAD, "1");
            Param(ctx, PW.PWINFO_PPCOMMPORT, ctx.Porta);
            // A biblioteca já disse que não há pinpad: não entra na chamada longa (log da Castelo,
            // 19:08:15 e 19:25:08: PW_iAddParam(0x7F02) <-2489> e mesmo assim 4 min presos depois).
            if (PinpadAusente(ctx) is { } semPinpadAdm)
                return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, semPinpadAdm);

            var fim = await ExecutarAsync(ctx).ConfigureAwait(false);
            var r = RespostaDaLib("ADM", id, 0, TipoTef.Credito, 1, fim.Aprovada, fim.Resultados);
            if (!fim.Aprovada)
            {
                await DesfazerSeRequeridoAsync(ctx, fim, r).ConfigureAwait(false);
                // O REQNUM sobe tambem na recusa (09/09/2026): o passo 57 e um estorno NEGADO
                // pelo host, e a planilha cobra o numero dele como o de qualquer transacao.
                return new DesfechoTef(fim.Situacao, id, chargeId, null, fim.Motivo, fim.PosOcupado) { Codigo = fim.Codigo, Desfeita = fim.Desfeita, Reqnum = r.CodigoControle };
            }
            // 'adm', nunca 'pago': o valor de uma administrativa (ex.: cancelamento pelo menu) não é venda.
            var tx = new TransacaoPayGo(chargeId, id, TipoTef.Credito, r.ValorCent ?? 0, 1, "aprovada", r, rotulo);
            var sit = "adm";
            if (fim.RequerConfirmacao)
            {
                if (!GuardarSeguro(tx))
                {
                    if (Desfazer(tx with { Motivo = "não gravou (REV)" }, PW.PWCNF_REV_OTHER_AUT, "desfeita") == Ack.Desconhecida)
                        return Orfa(tx, null, MsgNaoReconhece(r.Nsu), gravar: false);
                    return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "não consegui gravar a operação (desfeita)");
                }
                if (!await ImprimirSeguroAsync(tx).ConfigureAwait(false))
                {
                    if (Desfazer(tx with { Motivo = "comprovante não impresso (REV)" }, PW.PWCNF_REV_PRN_AUT, "desfeita") == Ack.Desconhecida)
                        return Orfa(tx, null, MsgNaoReconhece(r.Nsu), gravar: false);
                    return new DesfechoTef(SituacaoTef.Cancelado, id, chargeId, null, ClientePayGo.MsgCancelada(r.Rede, r.Nsu, r.ValorCent), false)
                    { Codigo = CodigoTef.Cancelado, Desfeita = true };
                }
                switch (Confirmar(tx, "adm"))
                {
                    case Ack.Ok: break;
                    case Ack.SemAck: sit = "cnf_sem_ack"; break;
                    default: return Orfa(tx, null, MsgNaoReconhece(r.Nsu), gravar: false);
                }
            }
            else
                await ImprimirSeguroAsync(tx).ConfigureAwait(false);
            return new DesfechoTef(SituacaoTef.Pago, id, chargeId, null, r.Mensagem, false) { Codigo = CodigoTef.Pago, PaymentStatus = sit, Reqnum = r.CodigoControle };
        }
        finally { _um.Release(); }
    }

    // ------------------------------------------------------------------ pendências (religamento)

    /// <summary>
    /// Varredura do boot: (a) reenvia CNF/REV sem ack e decide as 'aprovada' pelo que o caixa
    /// sabe (venda concluída → CNF; sem venda → REV); (b) lê PWINFO_PND* da biblioteca: se ela
    /// ainda descreve uma pendência, confirma se este caixa a conhece como paga, senão desfaz
    /// (PWCNF_REV_PWR_AUT). Quem decide é o PDV, nunca o operador. Devolve quantas resolveu.
    /// </summary>
    public async Task<int> ResolverPendenciasAsync(IReadOnlyList<(TransacaoPayGo Tx, bool VendaConcluida)> pendentes)
    {
        var n = 0;
        await _um.WaitAsync().ConfigureAwait(false);
        await ForaDaTelaAsync().ConfigureAwait(false);
        try
        {
            if (!Iniciar()) { Auditar?.Invoke("pgweblib: religamento sem PW_iInit; pendências ficam para o próximo boot"); return 0; }
            Reenviar();
            var vistas = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (tx, concluida) in pendentes)
            {
                if (tx.Resposta is null || !vistas.Add(tx.ChargeId)) continue;
                if (tx.Situacao is not ("aprovada" or "cnf_sem_ack" or "ncn_sem_ack")) continue;
                if (string.IsNullOrWhiteSpace(tx.CodigoControle))
                {
                    Auditar?.Invoke($"pgweblib: pendência {tx.ChargeId} sem REQNUM, não dá para CNF/REV; marcada órfã");
                    GuardarSeguro(tx with { Situacao = "orfa", Motivo = "aprovada sem REQNUM; confira no PayGo" });
                    n++;
                    continue;
                }
                switch (tx.Situacao)
                {
                    case "aprovada" when concluida:
                    case "cnf_sem_ack":
                        Confirmar(tx, Final(tx)); n++; break;
                    case "aprovada":
                        Desfazer(tx with { Motivo = "sem venda concluída no religamento (REV)" }, PW.PWCNF_REV_PWR_AUT, "desfeita"); n++; break;
                    case "ncn_sem_ack":
                        Desfazer(tx, PW.PWCNF_REV_PWR_AUT, "desfeita"); n++; break;
                }
            }

            // O que a BIBLIOTECA ainda segura (bloqueia o ponto de captura até resolver).
            var pnd = LerPendenciaDaLib();
            if (pnd is not null)
            {
                var conhecida = pendentes.Any(p => p.Tx.CodigoControle == pnd.ReqNum && (p.VendaConcluida || p.Tx.Situacao is "cnf_sem_ack" or "pago"))
                                || ConhecidaSegura(pnd.ReqNum);
                var ret = ConfirmacaoCrua(conhecida ? PW.PWCNF_CNF_AUTO : PW.PWCNF_REV_PWR_AUT, pnd.ReqNum, pnd.LocRef, pnd.ExtRef, pnd.VirtMerch, pnd.AuthSyst);
                Auditar?.Invoke($"pgweblib: pendência da biblioteca REQNUM {pnd.ReqNum} {(conhecida ? "confirmada" : "desfeita (REV_PWR)")}: {PW.Nome(ret)}");
                n++;
            }
        }
        finally { _um.Release(); }
        return n;
    }

    // ------------------------------------------------------------------ idle

    /// <summary>
    /// Roda PW_iIdleProc se PWINFO_IDLEPROCTIME já passou e nada está em voo (o semáforo é o
    /// mesmo das transações: nunca por cima de uma). Depois relê PWINFO_IDLEPROCTIME para
    /// agendar o próximo; sem horário válido, daqui a <see cref="IntervaloIdleMs"/>.
    /// </summary>
    public async Task<bool> IdleSeDevidoAsync()
    {
        if (_descartado || ProximoIdle is not { } quando || quando > DateTime.Now) return false;
        if (!await _um.WaitAsync(0).ConfigureAwait(false)) return false;
        await ForaDaTelaAsync().ConfigureAwait(false);
        try
        {
            // Relê o descarte já com o semáforo: um tique que entrou junto com o Encerrar() não pode reiniciar a DLL depois do PW_End.
            if (_descartado || !Iniciar()) return false;
            short ret;
            try { ret = _lib.IdleProc(); }
            catch (Exception ex)
            {
                Auditar?.Invoke("pgweblib: PW_iIdleProc lançou: " + ex.Message);
                AgendarIdle(null, "PW_iIdleProc");
                return false;
            }
            // Só o desvio vai para a auditoria: com o intervalo de segurança isto roda o dia inteiro.
            if (ret != PW.PWRET_OK) Auditar?.Invoke("pgweblib: PW_iIdleProc " + PW.Nome(ret));
            AgendarIdle(Ler(PW.PWINFO_IDLEPROCTIME), "PW_iIdleProc");
            return ret == PW.PWRET_OK;
        }
        finally { _um.Release(); }
    }

    /// <summary>
    /// PWINFO_IDLEPROCTIME (YYMMDDhhmmss, hora local) vira <see cref="ProximoIdle"/>. Vazio,
    /// inválido ou NO PASSADO cai em agora + <see cref="IntervaloIdleMs"/>: a rotina nunca fica
    /// sem próxima vez e nunca roda a cada tique.
    /// </summary>
    private void AgendarIdle(string? cru, string origem)
    {
        var v = cru?.Trim();
        var quando = HorarioIdle(v);
        if (quando is { } q && q > DateTime.Now)
        {
            ProximoIdle = q;
            return;
        }
        ProximoIdle = DateTime.Now.AddMilliseconds(IntervaloIdleMs);
        if (!string.IsNullOrEmpty(v) && v != _idleInvalidoVisto)
        {
            // Lixo ou passado no campo: uma linha por valor diferente, não uma por rodada.
            _idleInvalidoVisto = v;
            Auditar?.Invoke($"pgweblib: PWINFO_IDLEPROCTIME {(quando is null ? "inválido" : "no passado")} após {origem} ({v}); próximo PW_iIdleProc em {IntervaloIdleMs} ms");
        }
    }
    private string? _idleInvalidoVisto;

    /// <summary>
    /// YYMMDDhhmmss da biblioteca em hora local. O século é SEMPRE 20: a DLL devolve
    /// "551231235959" (31/12/2055) como "nunca", e o pivô do .NET (2029) leria 1955, um horário
    /// no passado que faria PW_iIdleProc rodar a cada tique (medido em 07/09/2026). Null = inválido.
    /// </summary>
    public static DateTime? HorarioIdle(string? v)
        => v is { Length: 12 } && v.All(char.IsAsciiDigit)
           && DateTime.TryParseExact("20" + v, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var q)
            ? q : null;

    /// <summary>Timer barato que chama <see cref="IdleSeDevidoAsync"/>; o intervalo também é a cadência de segurança (<see cref="IntervaloIdleMs"/>).</summary>
    public void IniciarIdle(int intervaloMs)
    {
        if (_descartado) return;
        IntervaloIdleMs = Math.Max(1, intervaloMs);
        _idle?.Dispose();
        _idle = new Timer(_ => { _ = IdleSeDevidoAsync(); }, null, IntervaloIdleMs, IntervaloIdleMs);
    }

    private bool _descartado;

    /// <summary>Para o timer do idle. Idempotente; a instância trocada em Servicos.RecarregarTef() passa por aqui.</summary>
    public void Dispose()
    {
        _descartado = true;
        var t = Interlocked.Exchange(ref _idle, null);
        t?.Dispose();
    }

    // ------------------------------------------------------------------ o laço

    private sealed class Contexto
    {
        public Contexto(string chargeId, string id, string cmd, TipoTef tipo, long valorCent, int parcelas, IProgress<AndamentoTef>? andamento, CancellationToken ct)
        { ChargeId = chargeId; Id = id; Cmd = cmd; Tipo = tipo; ValorCent = valorCent; Parcelas = parcelas; Andamento = andamento; Ct = ct; }
        public string ChargeId { get; }
        public string Id { get; }
        public string Cmd { get; }
        public TipoTef Tipo { get; }
        public long ValorCent { get; }
        public int Parcelas { get; }
        public IProgress<AndamentoTef>? Andamento { get; }
        public CancellationToken Ct { get; }
        /// <summary>Alguma tela de exibicao (QR, mensagem) foi aberta e precisa ser fechada no fim.</summary>
        public bool Exibiu { get; set; }
        /// <summary>A biblioteca ja pediu RETIRE O CARTAO: esta terminando, e abortar agora so atrapalha (09/09/2026).</summary>
        public bool Encerrando { get; set; }

        /// <summary>
        /// O que ja esta na tela, para nao redesenhar o mesmo QR a cada segundo.
        ///
        /// ⚠️ MEDIDO (09/09/2026): numa venda PIX a biblioteca pediu a exibicao 497
        /// vezes, uma por segundo, sempre com o MESMO conteudo. Cada pedido recriava a
        /// janela, e o dono viu o QR piscando sem parar. Ler um QR piscando com o
        /// aplicativo do banco e quase impossivel.
        /// </summary>
        public string? NaTela { get; set; }
        /// <summary>
        /// O que a AUTOMAÇÃO mandou (valor, moeda, parcelas, rede pré-selecionada…). Se a biblioteca
        /// pedir de novo por MOREDATA, é a resposta pronta: o operador nem fica sabendo.
        /// </summary>
        public Dictionary<ushort, string> Conhecidos { get; } = new();
        /// <summary>
        /// O que o OPERADOR respondeu. Fica FORA de <see cref="Conhecidos"/> de propósito: quando a
        /// biblioteca pede o mesmo dado outra vez, ela quer uma escolha nova, não a anterior. O passo
        /// 31 do roteiro v20260819 é isso: depois de escolher ABCDEF o mesmo menu (tag 0x2F) volta, e
        /// tem que aparecer na tela de novo em vez de ser respondido sozinho.
        /// </summary>
        public Dictionary<ushort, string> Respondidos { get; } = new();
        public string? UltimoDisplay;

        /// <summary>
        /// Os dados que a biblioteca RECUSOU no PW_iAddParam, com o retorno. Dado recusado não é
        /// "resposta pronta": se ela pedir de novo, o caixa não reenvia sozinho o mesmo valor
        /// (14/09/2026, Castelo, dado 32514).
        /// </summary>
        public Dictionary<ushort, short> Recusados { get; } = new();

        /// <summary>A porta do pinpad que esta operação manda (PWINFO_PPCOMMPORT), já normalizada.</summary>
        public string Porta { get; set; } = PortaDoPinpad.Automatica;
    }

    private sealed class Fim
    {
        public bool Aprovada;
        public bool RequerConfirmacao;
        public SituacaoTef Situacao = SituacaoTef.Erro;
        public string Codigo = CodigoTef.Plataforma;
        public string? Motivo;
        public bool PosOcupado;
        public bool Desfeita;
        /// <summary>Saída (cancel/timeout/erro) que aconteceu DEPOIS de uma aprovação definitiva (CNFREQ=0) e foi ignorada; só para auditoria.</summary>
        public string? Nota;
        public Dictionary<ushort, string> Resultados = new();
    }

    /// <summary>O que a biblioteca respondeu a um CNF/REV.</summary>
    private enum Ack
    {
        /// <summary>PWRET_OK: acusado.</summary>
        Ok,
        /// <summary>Sem ack (WRITERR, exceção…): ficou 'cnf_sem_ack'/'ncn_sem_ack' e vai ser reenviado.</summary>
        SemAck,
        /// <summary>PWRET_INVALIDTRN: a biblioteca não reconhece a transação; a linha ficou 'orfa'.</summary>
        Desconhecida,
    }

    private sealed record Pendencia(string ReqNum, string LocRef, string ExtRef, string VirtMerch, string AuthSyst);

    /// <summary>
    /// Resolve a pendência que a biblioteca descreve (PWINFO_PND*) pelo que ESTE caixa sabe,
    /// com os códigos MANUAIS: quem está decidindo é a automação, a partir do próprio
    /// registro, e não o fluxo automático de logo depois da venda (289) nem uma queda de
    /// energia (536881, que fica só para o religamento). Retorno da PayGo em 10/09/2026:
    /// passo 34 (pendente conhecida como paga) pede PWCNF_CNF_MANU_AUT; passo 36 (pendente
    /// que este caixa nunca viu) pede PWCNF_REV_MANU_AUT. E o roteiro manda resolver "com
    /// os dados recebidos", na hora da recusa, sem imprimir nada.
    /// </summary>
    /// <summary>O REQNUM da última pendência resolvida neste processo (ver PrepararAsync).</summary>
    private string? _pendenciaResolvida;

    private void ResolverPendenciaManual(Pendencia pnd, string quando)
    {
        var conhecida = ConhecidaSegura(pnd.ReqNum);
        var ret = ConfirmacaoCrua(conhecida ? PW.PWCNF_CNF_MANU_AUT : PW.PWCNF_REV_MANU_AUT,
            pnd.ReqNum, pnd.LocRef, pnd.ExtRef, pnd.VirtMerch, pnd.AuthSyst);
        if (ret == PW.PWRET_OK) _pendenciaResolvida = pnd.ReqNum;
        Auditar?.Invoke($"pgweblib: pendência REQNUM {pnd.ReqNum} {quando}: " +
            $"{(conhecida ? $"CNF manual (PWCNF_CNF_MANU_AUT {PW.PWCNF_CNF_MANU_AUT})" : $"REV manual (PWCNF_REV_MANU_AUT {PW.PWCNF_REV_MANU_AUT})")} {PW.Nome(ret)}");
    }

    /// <summary>Init + reenvios + pendência da biblioteca + PW_iNewTransac. Null = pode seguir; senão o desfecho que impede.</summary>
    private async Task<DesfechoTef?> PrepararAsync(byte oper, string chargeId)
    {
        if (!Iniciar()) return Falha(SituacaoTef.Erro, chargeId, CodigoTef.TefNaoResponde, MotivoIndisponivel ?? MsgTefNaoResponde);
        Reenviar();
        if (oper != PW.PWOPER_INSTALL && LerPendenciaDaLib() is { } pnd)
        {
            // Pendência bloqueia o ponto de captura: resolver antes, pelo que o caixa sabe.
            // (O caso normal é a recusa "transação pendente" já ter resolvido na hora; isto
            // pega o que sobrou de um caixa que fechou no meio.)
            //
            // Medido em 10/09/2026 17:53: depois de resolver na recusa, a biblioteca AINDA
            // devolve os mesmos PWINFO_PND* até o PW_iNewTransac seguinte, e o caixa mandou
            // o mesmo CNF duas vezes (283345 às 17:53:03 e 17:53:20). O que já foi
            // resolvido neste processo não se resolve de novo.
            if (pnd.ReqNum == _pendenciaResolvida)
                Auditar?.Invoke($"pgweblib: pendência REQNUM {pnd.ReqNum} já resolvida na recusa; a biblioteca ainda a mostra, ignorada antes de {chargeId}");
            else
                ResolverPendenciaManual(pnd, "antes de " + chargeId);
        }
        short nt;
        Entrar("PW_iNewTransac");
        try { nt = _lib.NewTransac(oper); }
        catch (Exception ex) { Auditar?.Invoke("pgweblib: PW_iNewTransac lançou: " + ex.Message); return Falha(SituacaoTef.Erro, chargeId, CodigoTef.TefNaoResponde, MsgTefNaoResponde); }
        finally { Sair(); }
        if (nt == PW.PWRET_DLLNOTINIT)
        {
            _iniciada = false;
            if (Iniciar())
            {
                Entrar("PW_iNewTransac");
                try { nt = _lib.NewTransac(oper); }
                finally { Sair(); }
            }
        }
        await Task.CompletedTask.ConfigureAwait(false);
        return nt switch
        {
            PW.PWRET_OK => null,
            PW.PWRET_NOTINST => Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, MsgNaoInstalado),
            PW.PWRET_DLLNOTINIT => Falha(SituacaoTef.Erro, chargeId, CodigoTef.TefNaoResponde, MsgTefNaoResponde),
            _ => Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "TEF recusou a operação: " + PW.Nome(nt)),
        };
    }

    private void ParametrosDeIdentidade(Contexto ctx)
    {
        Param(ctx, PW.PWINFO_AUTNAME, _op.NomeAutomacao);
        Param(ctx, PW.PWINFO_AUTVER, _op.VersaoAutomacao);
        Param(ctx, PW.PWINFO_AUTDEV, _op.Desenvolvedor);
        Param(ctx, PW.PWINFO_AUTCAP, _op.Capacidades.ToString(CultureInfo.InvariantCulture));
    }

    private void Param(Contexto ctx, ushort info, string valor)
    {
        var v = ArquivoIntpos.Ascii(valor);
        ctx.Conhecidos[info] = v;
        short ret;
        // USINGPINPAD e PPCOMMPORT são as que podem demorar: é nelas que a biblioteca varre as portas.
        Entrar($"PW_iAddParam({info})");
        try { ret = _lib.AddParam(info, v); }
        finally { Sair(); }
        if (ret == PW.PWRET_OK) { ctx.Recusados.Remove(info); return; }
        ctx.Recusados[info] = ret;
        Auditar?.Invoke($"pgweblib: PW_iAddParam({info}) {PW.Nome(ret)} ({ret})");
    }

    /// <summary>
    /// A frase de "não achei a maquininha" quando a biblioteca recusou USINGPINPAD ou PPCOMMPORT
    /// com um retorno de pinpad ausente. Null = pinpad aceito (ou recusado por outro motivo, como
    /// o PWRET_INVPARAM que a DLL devolve na administrativa e que não impede nada).
    /// </summary>
    private string? PinpadAusente(Contexto ctx)
    {
        foreach (var info in new[] { PW.PWINFO_PPCOMMPORT, PW.PWINFO_USINGPINPAD })
            if (ctx.Recusados.TryGetValue(info, out var ret) && EhPinpadAusente(ret))
            {
                Auditar?.Invoke($"pgweblib: {ctx.ChargeId} sem pinpad (PW_iAddParam({info}) {PW.Nome(ret)} {ret}, porta {ctx.Porta}): não entra no PW_iExecTransac");
                Recado(MsgPinpadNaoAchado(ctx.Porta));
                return MsgPinpadNaoAchado(ctx.Porta);
            }
        return null;
    }

    private async Task<Fim> ExecutarAsync(Contexto ctx)
    {
        var fim = new Fim();
        var relogio = Stopwatch.StartNew();
        var abortado = false;
        Stopwatch? cancelamento = null;
        while (true)
        {
            if (ctx.Ct.IsCancellationRequested && !abortado)
            {
                abortado = true;
                cancelamento = Stopwatch.StartNew();
                // Com RETIRE O CARTAO ja pedido a biblioteca esta terminando por conta propria;
                // o abort aqui virava PWRET_TRNNOTINIT no passo seguinte (09/09/2026).
                if (!ctx.Encerrando) { try { _lib.PPAbort(); } catch { } }
                ctx.Andamento?.Report(new AndamentoTef(FaseTef.Recado, ctx.ChargeId, ctx.Id, "Cancelamento pedido: aguardando o pinpad…"));
                relogio.Restart();
            }
            // O CANCELAMENTO TEM PRAZO PROPRIO (09/09/2026, passo 55). Depois do PW_iPPAbort a
            // PGWebLib de verdade nao encerra a espera do host do Pix: segue pedindo exibicao a
            // cada 0,7 s por 20 a 40 s (REQNUM 283108 e 283151), e cada pedido reiniciava o
            // relogio de execucao la embaixo. Este relogio nao reinicia: passado o prazo, a
            // cobranca encerra cancelada e desfeita (REV), como nos outros caminhos de desistencia.
            if (abortado && cancelamento!.ElapsedMilliseconds >= TempoMaxCancelamentoMs)
                return Encerrar(fim, SituacaoTef.Cancelado, CodigoTef.Cancelado, "cobrança cancelada pelo operador", desfeita: true, ler: true);
            short ret;
            IReadOnlyList<PwGetData> pedidos;
            Entrar("PW_iExecTransac");
            try { ret = _lib.ExecTransac(out pedidos); }
            catch (Exception ex)
            {
                Auditar?.Invoke($"pgweblib: PW_iExecTransac lançou em {ctx.ChargeId}: {ex.Message}");
                return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, "TEF falhou no meio da operação: confira no PayGo", posOcupado: true);
            }
            finally { Sair(); }
            switch (ret)
            {
                case PW.PWRET_OK:
                    // PWRET_OK É a aprovação (spec): a transação terminou e foi autorizada. AUTRESPCODE
                    // e RESULTMSG são informativos e vão só para a auditoria: cada rede escreve o
                    // código aprovado do seu jeito ("00", "000", "0000" ou nada), e recusa chega
                    // pelo RETORNO (PWRET_FROMHOST), nunca por este campo.
                    LerResultados(fim);
                    fim.RequerConfirmacao = fim.Resultados.TryGetValue(PW.PWINFO_CNFREQ, out var c) && c.Trim() == "1";
                    Auditar?.Invoke($"pgweblib: {ctx.ChargeId} PWRET_OK; AUTRESPCODE={(fim.Resultados.TryGetValue(PW.PWINFO_AUTRESPCODE, out var rc) ? rc.Trim() : "-")}; CNFREQ={(c ?? "-").Trim()}; {Mensagem(fim, "sem RESULTMSG")}");
                    fim.Aprovada = true;
                    fim.Situacao = SituacaoTef.Pago;
                    fim.Codigo = CodigoTef.Pago;
                    return fim;
                case PW.PWRET_MOREDATA:
                    var a = await AtenderAsync(ctx, pedidos, fim).ConfigureAwait(false);
                    if (a is not null) return a;
                    relogio.Restart();
                    continue;
                case PW.PWRET_NOTHING:
                    if (abortado && relogio.ElapsedMilliseconds >= Math.Min(TempoMaxExecMs, 5_000))
                        return Encerrar(fim, SituacaoTef.Cancelado, CodigoTef.Cancelado, "cobrança cancelada pelo operador", desfeita: true, ler: true);
                    if (relogio.ElapsedMilliseconds >= TempoMaxExecMs)
                    {
                        try { _lib.PPAbort(); } catch { }
                        return Encerrar(fim, SituacaoTef.Timeout, CodigoTef.Timeout, "o TEF não respondeu a tempo: confira no PayGo", posOcupado: true, ler: true);
                    }
                    await Task.Delay(IntervaloPollMs).ConfigureAwait(false);
                    continue;
                case PW.PWRET_CANCEL:
                    // Depois de PW_iPPAbort a biblioteca encerra o fluxo com PWRET_CANCEL: foi o
                    // operador. Sem abort, foi cancelado no pinpad ou pela própria biblioteca.
                    //
                    // Nos DOIS casos vale a frase que a biblioteca deixou em PWINFO_RESULTMSG, e a
                    // da casa é só o padrão de quem ficou calado. Antes, quando quem cancelou era o
                    // operador, a frase da rede era descartada de propósito — e é justamente esse o
                    // passo 55 do roteiro v20260819 (Esc na tela do QR do Pix), que cobra "OPERAÇÃO
                    // CANCELADA" para a automação. O passo 05, o mesmo desfecho pelo menu de redes,
                    // já respeitava a frase da biblioteca; este caminho não.
                    LerResultados(fim);
                    return Encerrar(fim, SituacaoTef.Cancelado, CodigoTef.Cancelado,
                        Mensagem(fim, abortado ? "cobrança cancelada pelo operador" : "operação cancelada"));
                case PW.PWRET_TIMEOUT:
                    LerResultados(fim);
                    return Encerrar(fim, SituacaoTef.Timeout, CodigoTef.Timeout, Mensagem(fim, "tempo esgotado no pinpad"));
                case PW.PWRET_HOSTCONNERR:
                case PW.PWRET_HOSTTIMEOUT:
                    LerResultados(fim);
                    return Encerrar(fim, SituacaoTef.Erro, CodigoTef.SemRede, Mensagem(fim, "sem comunicação com o host do TEF"));
                case PW.PWRET_NOMANDATORY:
                    LerResultados(fim);
                    return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, Mensagem(fim, "faltou parâmetro obrigatório para o TEF"));
                case PW.PWRET_NOTINST:
                    return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, MsgNaoInstalado);
                default:
                    LerResultados(fim);
                    if (PW.EhRecusaDoHost(ret))
                    {
                        // "TRANSACAO PENDENTE": o host negou ESTA venda e devolveu os dados de
                        // outra, que ficou sem confirmação. O roteiro (passos 34 e 36) manda
                        // resolver com os dados recebidos, na hora, sem imprimir nada. Até
                        // 10/09/2026 isso só acontecia na venda seguinte (52 min depois, no
                        // teste do dono); a venda recusada segue recusada do mesmo jeito.
                        if (LerPendenciaDaLib() is { } pnd)
                            ResolverPendenciaManual(pnd, "na recusa do host");
                        return Encerrar(fim, SituacaoTef.Recusado, CodigoTef.Recusado, Mensagem(fim, "transação não autorizada"));
                    }
                    return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, Mensagem(fim, "TEF devolveu " + PW.Nome(ret)));
            }
        }
    }

    /// <summary>Atende os pedidos de PWRET_MOREDATA. Null = tudo capturado, voltar ao ExecTransac; senão o Fim que interrompe.</summary>
    private async Task<Fim?> AtenderAsync(Contexto ctx, IReadOnlyList<PwGetData> pedidos, Fim fim)
    {
        for (var i = 0; i < pedidos.Count; i++)
        {
            var p = pedidos[i];
            if (p.EhMenu || p.EhDigitado)
            {
                // A PORTA QUE A BIBLIOTECA JÁ RECUSOU POR FALTA DE PINPAD (14/09/2026, Castelo). Antes o
                // caixa respondia sozinho o mesmo valor e a tela recebia "TEF não aceitou o dado 32514:
                // PWRET_PPNOTFOUND". Reenviar não acha pinpad nenhum; a frase diz o que fazer.
                if (p.Identificador is PW.PWINFO_PPCOMMPORT or PW.PWINFO_USINGPINPAD
                    && ctx.Recusados.TryGetValue(p.Identificador, out var recusaPinpad) && EhPinpadAusente(recusaPinpad))
                {
                    Auditar?.Invoke($"pgweblib: o TEF pediu de novo o dado {p.Identificador}, recusado antes com {PW.Nome(recusaPinpad)}; não reenviado");
                    return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, MsgPinpadNaoAchado(ctx.Porta), ler: true);
                }
                Recado(p.Prompt);
                // O menu de rede e a UNICA hora em que o terminal diz, com todas as letras, quais
                // redes ele tem. Anuncia ANTES do filtro da loja (o que interessa guardar e o que o
                // TERMINAL oferece) e ANTES de responder sozinho: em producao a rede unica e
                // respondida pelo Predefinido, e sem isto loja de uma rede so nunca tinha a lista.
                if (p.Identificador == PW.PWINFO_AUTHSYST && p.Opcoes is { Count: > 0 })
                    AnunciarRedes(ctx, p.Opcoes.Select(o => o.Valor).ToList());
                var valor = Predefinido(ctx, p);
                if (valor is null)
                {
                    if (ctx.Respondidos.ContainsKey(p.Identificador))
                        Auditar?.Invoke($"pgweblib: o TEF pediu de novo o dado {p.Identificador}; a tela pergunta outra vez");
                    // A loja pode encurtar o menu de redes (tef_pgweb_redes): o que vai para a
                    // tela é a lista já encurtada, e é por isso que o filtro mora aqui e não na
                    // tela — assim os botões e o valor devolvido saem da MESMA lista, e não tem
                    // como o operador tocar em C6PAY e a biblioteca receber CIELO. O menu nunca
                    // fica vazio: ver FiltroRedes.
                    var resposta = await PerguntarSeguroAsync(ctx, FiltroRedes.Aplicar(p, _op.RedesPermitidas, Auditar)).ConfigureAwait(false);
                    // A tela pode devolver o texto da opção ("RELATORIO") ou o valor em outra caixa
                    // ("cielo"): o que vai para a biblioteca é sempre o VALOR da opção.
                    valor = resposta is null ? null : (Casar(p, resposta) ?? resposta);
                }
                if (valor is null)
                    return await DesistirAsync(ctx, p, fim).ConfigureAwait(false);
                var ret = _lib.AddParam(p.Identificador, ArquivoIntpos.Ascii(valor));
                if (ret != PW.PWRET_OK)
                {
                    ctx.Recusados[p.Identificador] = ret;
                    Auditar?.Invoke($"pgweblib: PW_iAddParam({p.Identificador}) da resposta recusado: {PW.Nome(ret)} ({ret})");
                    if (EhPinpadAusente(ret))
                        return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, MsgPinpadNaoAchado(ctx.Porta), ler: true);
                    return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, $"TEF não aceitou o dado {p.Identificador}: {PW.Nome(ret)}", ler: true);
                }
                ctx.Respondidos[p.Identificador] = valor;
                continue;
            }
            if (p.EhPinpad)
            {
                // A biblioteca passou da exibicao para o pinpad: o QR (ou a mensagem) ja
                // cumpriu o papel. MEDIDO em 09/09/2026 as 18:59: o host aprovou o Pix, a
                // biblioteca pediu RETIRE O CARTAO, e a janela do QR continuou aberta com o
                // botao "Cancelar cobranca". O dono clicou nele e uma venda PAGA virou
                // desfeita (REQNUM 280555). A janela fecha aqui, antes de falar com o pinpad.
                FecharExibicaoSeAberta(ctx);
                // E a tela de pagamento fica sabendo que a rede ja decidiu: e ela que tira o
                // botao de cancelar do Pix (e o mantem no cartao, por causa dos passos 39 e 40).
                if (p.Tipo == PW.PWDAT_PPREMCRD)
                {
                    ctx.Encerrando = true;
                    ctx.Andamento?.Report(new AndamentoTef(FaseTef.Encerrando, ctx.ChargeId, ctx.Id, ""));
                }
                var r = await CapturarNoPinpadAsync(ctx, (ushort)i, p, fim).ConfigureAwait(false);
                if (r is not null) return r;
                continue;
            }
            if (p.EhExibicao)
            {
                var r = await ExibirNaTelaAsync(ctx, p, fim).ConfigureAwait(false);
                if (r is not null) return r;
                continue;
            }
            // Depois de um PW_iPPAbort a PGWebLib devolveu nove pedidos ZERADOS (tipo 0) em
            // 09/09/2026 (REQNUM 283068): nao e captura que o caixa nao suporta, e a biblioteca
            // encerrando torto o que o operador acabou de cancelar.
            if (ctx.Ct.IsCancellationRequested)
                return Encerrar(fim, SituacaoTef.Cancelado, CodigoTef.Cancelado, "cobrança cancelada pelo operador", desfeita: true, ler: true);
            return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, $"TEF pediu captura que o caixa não suporta (tipo {p.Tipo})", ler: true);
        }
        return null;
    }

    /// <summary>
    /// Quantas vezes o caixa chama PW_iExecTransac depois de avisar que o operador desistiu, só para
    /// deixar a biblioteca encerrar a transação e escrever o PWINFO_RESULTMSG dela. Teto baixo de
    /// propósito: aqui não se espera host nenhum, e travar o caixa é pior do que ficar sem a frase.
    /// </summary>
    private const int ChamadasParaFecharDesistencia = 20;

    /// <summary>
    /// O operador apertou Esc num menu (ou numa caixa de texto) que a biblioteca pediu. É o passo 16
    /// do roteiro v20260819, "Operação cancelada no menu administrativo", que cobra duas coisas:
    /// nada realizado para a automação e a mensagem "OPERAÇÃO CANCELADA".
    ///
    /// Quem escreve essa frase é a biblioteca, em PWINFO_RESULTMSG, e para escrevê-la ela precisa
    /// saber que o dado não vem: sem aviso a transação fica aberta esperando uma resposta que não
    /// chega. O aviso é PW_iAddParam(PWINFO_OPERABORTED), e o caixa só o manda quando o próprio
    /// PW_GetData veio com bNotificarCancelamento, porque o cabeçalho oficial declara o campo e o
    /// parâmetro mas não descreve o fluxo. Sem a marca da biblioteca nada muda: o caixa encerra por
    /// conta, como sempre fez.
    ///
    /// O desfecho é Cancelado nos dois caminhos, porque quem desistiu foi o operador. O que muda é
    /// a mensagem: a da biblioteca quando ela falou, a frase da casa quando ela ficou calada.
    /// </summary>
    private async Task<Fim> DesistirAsync(Contexto ctx, PwGetData p, Fim fim)
    {
        if (!p.NotificarCancelamento)
            Auditar?.Invoke($"pgweblib: {ctx.ChargeId} operador desistiu do dado {p.Identificador}; a biblioteca não pediu aviso (bNotificarCancelamento=0)");
        else
        {
            var aviso = _lib.AddParam(PW.PWINFO_OPERABORTED, "1");
            Auditar?.Invoke($"pgweblib: {ctx.ChargeId} operador desistiu do dado {p.Identificador}; PW_iAddParam(PWINFO_OPERABORTED) {PW.Nome(aviso)}");
            if (aviso == PW.PWRET_OK)
                for (var i = 0; i < ChamadasParaFecharDesistencia; i++)
                {
                    short ret;
                    try { ret = _lib.ExecTransac(out _); }
                    catch (Exception ex) { Auditar?.Invoke("pgweblib: PW_iExecTransac lançou ao fechar a desistência: " + ex.Message); break; }
                    if (ret == PW.PWRET_NOTHING) { await Task.Delay(IntervaloPollMs).ConfigureAwait(false); continue; }
                    // Encerrou (PWRET_CANCEL e afins) ou tornou a pedir o dado: em nenhum dos dois
                    // casos o caixa pergunta de novo. O que interessa é o RESULTMSG que ficou.
                    Auditar?.Invoke($"pgweblib: {ctx.ChargeId} desistência fechada com {PW.Nome(ret)}");
                    break;
                }
        }
        LerResultados(fim);
        return Encerrar(fim, SituacaoTef.Cancelado, CodigoTef.Cancelado, Mensagem(fim, "operação cancelada pelo operador"), desfeita: true);
    }

    /// <summary>
    /// PWDAT_DSPCHECKOUT e PWDAT_DSPQRCODE: a biblioteca não quer um dado, quer que o caixa MOSTRE
    /// alguma coisa. É por aqui que o Pix funciona numa solução Windows: o cliente lê o QR na tela
    /// do caixa, e o Esc do operador cancela a venda (passo 55 do roteiro v20260819).
    ///
    /// Duas coisas aqui são leitura do contrato oficial e precisam ser confirmadas contra o host na
    /// primeira venda de Pix da homologação, porque não dá para provar com a biblioteca parada:
    ///   1. o conteúdo do QR vem de PW_iGetResult(PWINFO_AUTHPOSQRCODE), e não do szPrompt, que só
    ///      tem 84 caracteres;
    ///   2. a resposta de um pedido de exibição é um PW_iAddParam do mesmo identificador com valor
    ///      vazio, que é como a biblioteca sabe que a tela já mostrou.
    /// Se algum dos dois estiver errado, o sintoma aparece no passo 11 e a auditoria abaixo diz
    /// exatamente o que foi lido e o que foi respondido.
    /// </summary>
    private async Task<Fim?> ExibirNaTelaAsync(Contexto ctx, PwGetData p, Fim fim)
    {
        var qr = p.EhQrCode ? Ler(PW.PWINFO_AUTHPOSQRCODE) : null;
        Auditar?.Invoke($"pgweblib: exibir tipo={p.Tipo} id={p.Identificador} prompt=\"{p.Prompt}\" qr={(string.IsNullOrWhiteSpace(qr) ? "vazio" : qr!.Length + " caracteres")}");

        // O OPERADOR JA DESISTIU (09/09/2026, passo 55). A biblioteca segue pedindo exibicao
        // enquanto consulta o host, e a "mesma tela" logo abaixo respondia sem olhar o
        // cancelamento: o Esc so surtia efeito 20 a 40 s depois, quando a biblioteca pedia
        // RETIRE O CARTAO. Aqui encerra no primeiro pedido depois do Esc: cancelada e desfeita.
        if (ctx.Ct.IsCancellationRequested)
            return Encerrar(fim, SituacaoTef.Cancelado, CodigoTef.Cancelado, "cobrança cancelada pelo operador", desfeita: true, ler: true);

        if (p.EhQrCode && string.IsNullOrWhiteSpace(qr))
            return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma,
                "O TEF pediu para mostrar o QR mas não mandou o código. Tente de novo ou cobre de outro jeito.", ler: true);

        if (Exibir is null)
            return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma,
                p.EhQrCode
                    ? "Este caixa não sabe mostrar o QR do Pix na tela. Cobre pelo pinpad ou de outro jeito."
                    : "Este caixa não sabe mostrar a mensagem que o TEF pediu.", ler: true);

        var titulo = p.EhQrCode ? "Pix: mostre o QR ao cliente" : "TEF";
        var texto = string.IsNullOrWhiteSpace(p.Prompt)
            ? (p.EhQrCode ? "O cliente lê o código com o aplicativo do banco." : "")
            : p.Prompt;

        // Exibir ABRE a tela e volta: nao espera o cliente pagar. Quem espera e o laco de
        // PW_iExecTransac, que fica pedindo o desfecho ao host. Se a tela bloqueasse aqui, a
        // biblioteca nunca saberia que o QR foi mostrado e a venda morreria de timeout.
        // O Esc do operador cancela o CancellationToken da venda, e o laco de execucao ja trata
        // isso: chama PW_iPPAbort e a biblioteca encerra com PWRET_CANCEL.
        // MESMA TELA NAO REDESENHA. A biblioteca repete o pedido de exibicao a cada
        // segundo, e o TEXTO carrega um contador ("REALIZE A LEITURA DO QR CODE 06",
        // "07", "08"...). Medido na auditoria em 09/09/2026: um pedido por segundo, texto
        // diferente em cada um. A identidade da tela e o titulo mais o QR; o texto muda
        // dentro da janela aberta, e o QR fica parado para o cliente ler.
        var assinatura = titulo + "|" + (qr ?? "");
        if (ctx.Exibiu && string.Equals(ctx.NaTela, assinatura, StringComparison.Ordinal))
        {
            try { AtualizarExibicao?.Invoke(texto); } catch { /* texto e conforto */ }
            var jaRet = _lib.AddParam(p.Identificador, "");
            if (jaRet != PW.PWRET_OK)
                return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma,
                    $"TEF não aceitou o aviso de que a tela mostrou ({p.Identificador}): {PW.Nome(jaRet)}", ler: true);
            return null;
        }

        bool seguiu;
        try
        {
            ctx.Exibiu = true;
            ctx.NaTela = assinatura;
            seguiu = await Exibir(new ExibicaoTef(titulo, texto, qr), ctx.Ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            seguiu = false;
        }
        catch (Exception ex)
        {
            Auditar?.Invoke("pgweblib: a tela de exibição lançou: " + ex.GetType().Name + " " + ex.Message);
            return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma,
                "Não consegui mostrar a tela do TEF. A cobrança não foi feita.", ler: true);
        }

        if (!seguiu)
            return Encerrar(fim, SituacaoTef.Cancelado, CodigoTef.Cancelado,
                "operação cancelada pelo operador", desfeita: true, ler: true);

        // Resposta de exibição: mesmo identificador, valor vazio. Ver o comentário acima.
        var ret = _lib.AddParam(p.Identificador, "");
        if (ret != PW.PWRET_OK)
            return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma,
                $"TEF não aceitou o aviso de que a tela mostrou ({p.Identificador}): {PW.Nome(ret)}", ler: true);
        return null;
    }

    /// <summary>Fecha a tela de exibicao (QR ou mensagem) se estiver aberta; o finally da cobranca nao fecha duas vezes.</summary>
    private void FecharExibicaoSeAberta(Contexto ctx)
    {
        if (!ctx.Exibiu) return;
        ctx.Exibiu = false;
        ctx.NaTela = null;
        try { FecharExibicao?.Invoke(); }
        catch (Exception ex) { Auditar?.Invoke("pgweblib: fechar a tela de exibicao lancou: " + ex.GetType().Name); }
    }

    private async Task<Fim?> CapturarNoPinpadAsync(Contexto ctx, ushort indice, PwGetData p, Fim fim)
    {
        short ret;
        try
        {
            ret = p.Tipo switch
            {
                PW.PWDAT_CARDINF => _lib.PPGetCard(indice),
                PW.PWDAT_PPENTRY => _lib.PPGetData(indice),
                PW.PWDAT_PPENCPIN => _lib.PPGetPIN(indice),
                PW.PWDAT_CARDOFF => _lib.PPGoOnChip(indice),
                PW.PWDAT_CARDONL => _lib.PPFinishChip(indice),
                PW.PWDAT_PPCONF => _lib.PPConfirmData(indice),
                PW.PWDAT_PPDATAPOSCNF => _lib.PPPositiveConfirmation(indice),  // PW_iPPPositiveConfirmation (exemplo oficial)
                PW.PWDAT_PPREMCRD => _lib.PPRemoveCard(),
                PW.PWDAT_PPGENCMD => _lib.PPGenericCMD(indice),
                _ => PW.PWRET_INVCALL,
            };
        }
        catch (Exception ex)
        {
            Auditar?.Invoke($"pgweblib: PW_iPP* ({p.Tipo}) lançou: {ex.Message}");
            return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, "falha ao falar com o pinpad", ler: true);
        }
        if (EhPinpadAusente(ret))
        {
            Auditar?.Invoke($"pgweblib: PW_iPP* ({p.Tipo}) {PW.Nome(ret)} ({ret})");
            return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, MsgPinpadNaoAchado(ctx.Porta), ler: true);
        }
        if (ret != PW.PWRET_OK)
            return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, "pinpad recusou a captura: " + PW.Nome(ret), ler: true);

        var relogio = Stopwatch.StartNew();
        var abortado = false;
        while (true)
        {
            if (ctx.Ct.IsCancellationRequested && !abortado)
            {
                abortado = true;
                // RETIRE O CARTAO nao se aborta (09/09/2026, passo 55): e a biblioteca terminando.
                // Abortar de novo aqui fazia PW_iPPEventLoop devolver PWRET_TRNNOTINIT (REQNUM
                // 283108 e 283151). Deixa tirar o cartao; o prazo curto de abortado continua valendo.
                if (p.Tipo != PW.PWDAT_PPREMCRD) { try { _lib.PPAbort(); } catch { } }
                relogio.Restart();
            }
            short ev;
            string display;
            try { ev = _lib.PPEventLoop(out display); }
            catch (Exception ex)
            {
                Auditar?.Invoke("pgweblib: PW_iPPEventLoop lançou: " + ex.Message);
                return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, "falha ao falar com o pinpad", ler: true);
            }
            switch (ev)
            {
                case PW.PWRET_OK:
                case PW.PWRET_FALLBACK:
                    return null;
                case PW.PWRET_DISPLAY:
                    if (!string.IsNullOrWhiteSpace(display) && display != ctx.UltimoDisplay)
                    {
                        ctx.UltimoDisplay = display;
                        Recado(display);
                        ctx.Andamento?.Report(new AndamentoTef(FaseTef.Recado, ctx.ChargeId, ctx.Id, display.Replace('\r', ' ').Trim()));
                    }
                    break;
                case PW.PWRET_NOTHING:
                    break;
                case PW.PWRET_CANCEL:
                    return Encerrar(fim, SituacaoTef.Cancelado, CodigoTef.Cancelado, abortado ? "cobrança cancelada pelo operador" : "operação cancelada no pinpad", ler: true);
                case PW.PWRET_TIMEOUT:
                    return Encerrar(fim, SituacaoTef.Timeout, CodigoTef.Timeout, "tempo esgotado no pinpad", ler: true);
                default:
                    // Depois do abort a biblioteca ja encerrou: o erro que o pinpad devolve entao
                    // (PWRET_TRNNOTINIT) e consequencia do cancelamento, nao um defeito.
                    if (abortado || ctx.Ct.IsCancellationRequested)
                        return Encerrar(fim, SituacaoTef.Cancelado, CodigoTef.Cancelado, "cobrança cancelada pelo operador", desfeita: true, ler: true);
                    return Encerrar(fim, SituacaoTef.Erro, CodigoTef.Plataforma, "pinpad devolveu " + PW.Nome(ev), ler: true);
            }
            var teto = abortado ? Math.Min(TempoMaxCapturaMs, 5_000) : TempoMaxCapturaMs;
            if (relogio.ElapsedMilliseconds >= teto)
            {
                if (!abortado) { try { _lib.PPAbort(); } catch { } }
                return Encerrar(fim, abortado ? SituacaoTef.Cancelado : SituacaoTef.Timeout, abortado ? CodigoTef.Cancelado : CodigoTef.Timeout,
                    abortado ? "cobrança cancelada pelo operador" : "o pinpad não respondeu a tempo", ler: true, posOcupado: !abortado);
            }
            await Task.Delay(IntervaloPollMs).ConfigureAwait(false);
        }
    }

    /// <summary>As redes do menu vão para a lista do tipo da cobrança: Pix numa, cartão (e ADM) na outra.</summary>
    private void AnunciarRedes(Contexto ctx, IReadOnlyList<string> redes)
    {
        try
        {
            if (ctx.Tipo == TipoTef.Pix) RedesPixDoTerminal?.Invoke(redes);
            else RedesDoTerminal?.Invoke(redes);
        }
        catch { /* saber as redes e conforto: nunca derruba a cobranca */ }
    }

    /// <summary>Valor que a automação já sabe para o dado pedido (o que mandou em AddParam, ou a rede pré-selecionada).</summary>
    private string? Predefinido(Contexto ctx, PwGetData p)
    {
        // Dado que a biblioteca RECUSOU não volta sozinho: ela quer outro valor, e quem decide é a tela.
        if (ctx.Recusados.ContainsKey(p.Identificador))
        {
            Auditar?.Invoke($"pgweblib: o TEF pediu o dado {p.Identificador}, que tinha recusado ({PW.Nome(ctx.Recusados[p.Identificador])}); a tela pergunta");
            return null;
        }
        if (!ctx.Conhecidos.TryGetValue(p.Identificador, out var v) || string.IsNullOrWhiteSpace(v))
        {
            // Menu com uma opção só não precisa de operador. O menu de redes é a exceção EM
            // HOMOLOGAÇÃO: o passo 05 do roteiro manda o operador apertar Esc nele, e numa loja com
            // uma credenciadora só o caixa respondia sozinho, o menu nunca aparecia e não havia onde
            // apertar Esc. Em produção (11/09/2026, homologação aprovada) uma rede só é respondida
            // sem perguntar: lista de um item não é escolha. Rede gravada na Configuração continua
            // sendo respondida pela linha de cima (Conhecidos), que é a decisão do lojista.
            if (p.EhMenu && p.Opcoes is { Count: 1 }
                && (p.Identificador != PW.PWINFO_AUTHSYST || _op.Ambiente == PW.ENVRMNT_PROD))
                return p.Opcoes[0].Valor;
            return null;
        }
        if (!p.EhMenu || p.Opcoes is null || p.Opcoes.Count == 0) return v;
        var casado = Casar(p, v);
        if (casado is null)
            Auditar?.Invoke($"pgweblib: '{v}' não está no menu {p.Identificador} ({string.Join("|", p.Opcoes.Select(o => o.Valor))}); perguntando à tela");
        return casado;
    }

    /// <summary>Opção do menu que corresponde ao texto dado (pelo valor ou pelo texto, sem caixa). Null = não está no menu.</summary>
    private static string? Casar(PwGetData p, string v)
    {
        if (!p.EhMenu || p.Opcoes is null || p.Opcoes.Count == 0) return null;
        var op = p.Opcoes.FirstOrDefault(o => string.Equals(o.Valor, v, StringComparison.OrdinalIgnoreCase))
              ?? p.Opcoes.FirstOrDefault(o => string.Equals(o.Texto.Trim(), v.Trim(), StringComparison.OrdinalIgnoreCase));
        return op?.Valor;
    }

    private async Task<string?> PerguntarSeguroAsync(Contexto ctx, PwGetData p)
    {
        if (Perguntar is null) { Auditar?.Invoke($"pgweblib: sem quem responder o dado {p.Identificador} ({p.Prompt})"); return null; }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
        cts.CancelAfter(TempoPerguntaMs);
        try { return await Perguntar(p, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Auditar?.Invoke("pgweblib: Perguntar lançou: " + ex.Message); return null; }
    }

    private Fim Encerrar(Fim fim, SituacaoTef s, string codigo, string motivo, bool desfeita = false, bool posOcupado = false, bool ler = false)
    {
        if (ler) LerResultados(fim);
        fim.Aprovada = false;
        fim.Situacao = s;
        fim.Codigo = codigo;
        fim.Motivo = motivo;
        fim.Desfeita = desfeita;
        fim.PosOcupado = posOcupado;
        fim.RequerConfirmacao = fim.Resultados.TryGetValue(PW.PWINFO_CNFREQ, out var c) && c.Trim() == "1";
        if (!fim.RequerConfirmacao && s != SituacaoTef.Recusado && AprovadaDefinitiva(fim))
        {
            // O host aprovou e CNFREQ=0: a transação é DEFINITIVA, não existe REV que a desfaça.
            // A saída depois disso (operador cancelou em RETIRE O CARTAO, pinpad deu timeout)
            // não muda o dinheiro. Gravar 'cancelado' aqui diria à tela "o cliente não foi
            // cobrado" com o cliente cobrado. Segue como paga; a saída fica só na auditoria.
            fim.Nota = motivo;
            fim.Aprovada = true;
            fim.Situacao = SituacaoTef.Pago;
            fim.Codigo = CodigoTef.Pago;
            fim.Motivo = null;
            fim.Desfeita = false;
            fim.PosOcupado = false;
        }
        return fim;
    }

    /// <summary>Prova de aprovação lida da biblioteca: AUTRESPCODE aprovado e REQNUM (ou NSU) presentes.</summary>
    private static bool AprovadaDefinitiva(Fim fim)
        => fim.Resultados.TryGetValue(PW.PWINFO_AUTRESPCODE, out var rc) && CodigoAprovado(rc)
           && (fim.Resultados.TryGetValue(PW.PWINFO_REQNUM, out var req) && !string.IsNullOrWhiteSpace(req)
               || fim.Resultados.TryGetValue(PW.PWINFO_AUTEXTREF, out var nsu) && !string.IsNullOrWhiteSpace(nsu));

    /// <summary>AUTRESPCODE de aprovação: só zeros, em qualquer largura ("0", "00", "000", "0000"); cada rede escreve de um jeito.</summary>
    private static bool CodigoAprovado(string? rc)
    {
        var v = rc?.Trim();
        return !string.IsNullOrEmpty(v) && v.All(ch => ch == '0');
    }

    private static string Mensagem(Fim fim, string padrao)
        => fim.Resultados.TryGetValue(PW.PWINFO_RESULTMSG, out var m) && !string.IsNullOrWhiteSpace(m) ? m.Replace('\r', ' ').Trim() : padrao;

    /// <summary>Tudo que a automação lê ao fim. CARDFULLPAN (193) NÃO está aqui, de propósito.</summary>
    private static readonly ushort[] InfosLidas =
    {
        PW.PWINFO_CNFREQ, PW.PWINFO_REQNUM, PW.PWINFO_AUTLOCREF, PW.PWINFO_AUTEXTREF, PW.PWINFO_VIRTMERCH, PW.PWINFO_AUTHSYST,
        PW.PWINFO_AUTHCODE, PW.PWINFO_AUTRESPCODE, PW.PWINFO_AUTDATETIME, PW.PWINFO_CARDNAME, PW.PWINFO_CARDNAMESTD,
        PW.PWINFO_CARDPARCPAN, PW.PWINFO_CARDTYPE, PW.PWINFO_CARDENTMODE, PW.PWINFO_FINTYPE, PW.PWINFO_INSTALLMENTS,
        PW.PWINFO_TOTAMNT, PW.PWINFO_DUEAMNT, PW.PWINFO_RCPTFULL, PW.PWINFO_RCPTMERCH, PW.PWINFO_RCPTCHOLDER,
        PW.PWINFO_RCPTCHSHORT, PW.PWINFO_RCPTPRN, PW.PWINFO_RESULTMSG, PW.PWINFO_IDLEPROCTIME,
    };

    private void LerResultados(Fim fim)
    {
        if (fim.Resultados.Count > 0) return;
        foreach (var info in InfosLidas)
        {
            var v = Ler(info);
            if (v is not null) fim.Resultados[info] = v;
        }
        // A biblioteca também informa o horário ao fim de uma transação: aproveita.
        if (fim.Resultados.TryGetValue(PW.PWINFO_IDLEPROCTIME, out var idle)) AgendarIdle(idle, "transação");
        // A frase da rede fica guardada para o menu do TEF mostrar. É o único ponto do provedor
        // que lê PWINFO_RESULTMSG de todos os desfechos (aprovado, recusado, cancelado, timeout),
        // então guardar aqui é guardar uma vez só.
        if (fim.Resultados.TryGetValue(PW.PWINFO_RESULTMSG, out var frase) && !string.IsNullOrWhiteSpace(frase))
        {
            UltimaMensagem = frase.Replace('\r', ' ').Trim();
            UltimaMensagemEm = DateTime.Now;
        }
    }

    private string? Ler(ushort info)
    {
        try
        {
            var ret = _lib.GetResult(info, out var v);
            return ret == PW.PWRET_OK && !string.IsNullOrEmpty(v) ? v : null;
        }
        catch { return null; }
    }

    private Pendencia? LerPendenciaDaLib()
    {
        var req = Ler(PW.PWINFO_PNDREQNUM);
        if (string.IsNullOrWhiteSpace(req)) return null;
        return new Pendencia(req!, Ler(PW.PWINFO_PNDAUTLOCREF) ?? "", Ler(PW.PWINFO_PNDAUTEXTREF) ?? "",
            Ler(PW.PWINFO_PNDVIRTMERCH) ?? "", Ler(PW.PWINFO_PNDAUTHSYST) ?? "");
    }

    // ------------------------------------------------------------------ confirmação

    private static (string Req, string Loc, string Ext, string Vm, string As) Tupla(RespostaPayGo? r)
        => (r?.CodigoControle ?? "", r?.Campos.GetValueOrDefault("950-000") ?? "", r?.Nsu ?? "",
            r?.Campos.GetValueOrDefault("951-000") ?? "", r?.Rede ?? "");

    private short ConfirmacaoCrua(uint resultado, string req, string loc, string ext, string vm, string authSyst)
    {
        try
        {
            var ret = _lib.Confirmation(resultado, req, loc, ext, vm, authSyst);
            if (ret == PW.PWRET_OK) { try { _lib.WaitConfirmation(); } catch (Exception ex) { Auditar?.Invoke("pgweblib: PW_iWaitConfirmation lançou: " + ex.Message); } }
            return ret;
        }
        catch (Exception ex)
        {
            Auditar?.Invoke("pgweblib: PW_iConfirmation lançou: " + ex.Message);
            return PW.PWRET_WRITERR;
        }
    }

    /// <summary>
    /// CNF (automatico por padrao; PWCNF_CNF_MANU_AUT quando o operador confirmou na mao).
    /// Ok = acusado e linha `depois`; SemAck = 'cnf_sem_ack' para reenvio com o MESMO codigo;
    /// Desconhecida = INVALIDTRN, linha 'orfa'.
    /// </summary>
    private Ack Confirmar(TransacaoPayGo tx, string depois, uint resultado = PW.PWCNF_CNF_AUTO)
    {
        var (req, loc, ext, vm, aut) = Tupla(tx.Resposta);
        var ret = ConfirmacaoCrua(resultado, req, loc, ext, vm, aut);
        if (ret == PW.PWRET_OK)
        {
            GuardarSeguro(tx with { Situacao = depois });
            var manual = resultado == PW.PWCNF_CNF_MANU_AUT ? $" (manual pelo operador, PWCNF_CNF_MANU_AUT {resultado})" : "";
            Auditar?.Invoke($"pgweblib: CNF {tx.ChargeId} REQNUM {req} {PW.Nome(ret)} -> {depois}{manual}");
            return Ack.Ok;
        }
        if (ret == PW.PWRET_INVALIDTRN && NaoReconhecida(tx, "CNF", req)) return Ack.Desconhecida;
        GuardarSeguro(tx with { Situacao = "cnf_sem_ack", Motivo = "confirmação sem ack: " + PW.Nome(ret) });
        _reenvios.RemoveAll(x => x.Tx.ChargeId == tx.ChargeId);
        _reenvios.Add((tx, resultado, depois));
        Auditar?.Invoke($"pgweblib: CNF {tx.ChargeId} sem ack ({PW.Nome(ret)}); reenvio agendado");
        return Ack.SemAck;
    }

    /// <summary>REV_*. Sempre fala com a biblioteca: quem chama só desfaz transação com CNFREQ=1 (sem confirmação pendente não existe REV).</summary>
    private Ack Desfazer(TransacaoPayGo tx, uint resultado, string depois)
    {
        var (req, loc, ext, vm, aut) = Tupla(tx.Resposta);
        var ret = ConfirmacaoCrua(resultado, req, loc, ext, vm, aut);
        if (ret == PW.PWRET_OK)
        {
            GuardarSeguro(tx with { Situacao = depois });
            Auditar?.Invoke($"pgweblib: REV {tx.ChargeId} REQNUM {req} ({resultado}) {PW.Nome(ret)} -> {depois}");
            return Ack.Ok;
        }
        if (ret == PW.PWRET_INVALIDTRN && NaoReconhecida(tx, "REV", req)) return Ack.Desconhecida;
        GuardarSeguro(tx with { Situacao = "ncn_sem_ack", Motivo = "desfazimento sem ack: " + PW.Nome(ret) });
        _reenvios.RemoveAll(x => x.Tx.ChargeId == tx.ChargeId);
        _reenvios.Add((tx, resultado, depois));
        Auditar?.Invoke($"pgweblib: REV {tx.ChargeId} sem ack ({PW.Nome(ret)}); reenvio agendado");
        return Ack.SemAck;
    }

    /// <summary>
    /// PWRET_INVALIDTRN só diz que a biblioteca não tem ESTA transação pendente; não diz se
    /// ela foi confirmada ou desfeita. Cruza com PWINFO_PNDREQNUM: se a biblioteca ainda
    /// descreve este REQNUM é contradição (false: trata como sem ack e reenvia); senão a linha
    /// vira 'orfa' e alguém confere no relatório. Nunca 'pago' nem 'desfeita' sem ack.
    /// </summary>
    private bool NaoReconhecida(TransacaoPayGo tx, string oque, string req)
    {
        var pnd = Ler(PW.PWINFO_PNDREQNUM)?.Trim();
        if (!string.IsNullOrEmpty(pnd) && pnd == req) return false;
        GuardarSeguro(tx with { Situacao = "orfa", Motivo = MsgNaoReconhece(tx.Resposta?.Nsu) });
        _reenvios.RemoveAll(x => x.Tx.ChargeId == tx.ChargeId);
        Auditar?.Invoke($"pgweblib: {oque} {tx.ChargeId} REQNUM {req} {PW.Nome(PW.PWRET_INVALIDTRN)}; pendência da biblioteca: {(string.IsNullOrEmpty(pnd) ? "nenhuma" : pnd)} -> orfa");
        return true;
    }

    /// <summary>Terminou sem aprovação mas CNFREQ=1 (operador interrompeu no meio): desfaz. Grava a linha antes.</summary>
    private Task DesfazerSeRequeridoAsync(Contexto ctx, Fim fim, RespostaPayGo r)
    {
        if (!fim.RequerConfirmacao) return Task.CompletedTask;
        var tx = new TransacaoPayGo(ctx.ChargeId, ctx.Id, ctx.Tipo, ctx.ValorCent, ctx.Parcelas, "aprovada", r, fim.Motivo);
        GuardarSeguro(tx);
        var motivo = fim.Situacao switch
        {
            SituacaoTef.Cancelado => PW.PWCNF_REV_ABORT,
            SituacaoTef.Timeout => PW.PWCNF_REV_AUTO_ABORT,
            _ => PW.PWCNF_REV_OTHER_AUT,
        };
        if (Desfazer(tx with { Motivo = (fim.Motivo ?? "") + " (REV)" }, motivo, "desfeita") == Ack.Desconhecida)
        {
            // A biblioteca não reconhece o REV: pode estar confirmada e cobrada. Órfã com aviso.
            fim.Situacao = SituacaoTef.Erro;
            fim.Codigo = CodigoTef.Plataforma;
            fim.Motivo = MsgNaoReconhece(r.Nsu);
            fim.PosOcupado = true;
            fim.Desfeita = false;
            return Task.CompletedTask;
        }
        fim.Desfeita = true;
        return Task.CompletedTask;
    }

    private void Reenviar()
    {
        if (_reenvios.Count == 0) return;
        foreach (var (tx, resultado, depois) in _reenvios.ToList())
        {
            var (req, loc, ext, vm, aut) = Tupla(tx.Resposta);
            var ret = ConfirmacaoCrua(resultado, req, loc, ext, vm, aut);
            if (ret == PW.PWRET_OK)
            {
                GuardarSeguro(tx with { Situacao = depois });
                _reenvios.RemoveAll(x => x.Tx.ChargeId == tx.ChargeId);
                Auditar?.Invoke($"pgweblib: reenvio de {tx.ChargeId} ({resultado}) acusado -> {depois}");
            }
            else if (ret == PW.PWRET_INVALIDTRN)
                NaoReconhecida(tx, "reenvio de " + (resultado is PW.PWCNF_CNF_AUTO or PW.PWCNF_CNF_MANU_AUT ? "CNF" : "REV"), req);   // tira da fila e grava 'orfa'
        }
    }

    /// <summary>
    /// Desfecho de transação ÓRFÃ (dinheiro pode ter entrado sem venda): Erro com o aviso de
    /// conferir na maquininha, nunca Desfeita. `gravar` = false quando a linha 'orfa' já foi gravada.
    /// </summary>
    private DesfechoTef Orfa(TransacaoPayGo tx, CartaoTef? cartao, string motivo, bool gravar = true)
    {
        if (gravar) GuardarSeguro(tx with { Situacao = "orfa", Motivo = motivo });
        Auditar?.Invoke($"pgweblib: {tx.ChargeId} órfã: {motivo}");
        return new DesfechoTef(SituacaoTef.Erro, tx.Identificacao, tx.ChargeId, cartao, motivo, true) { Codigo = CodigoTef.Plataforma };
    }

    /// <summary>Estado final ao confirmar: só venda vira `pago`.</summary>
    private static string Final(TransacaoPayGo tx)
        => tx.EhCancelamento ? "estornado"
         : tx.ChargeId.Contains("-adm-", StringComparison.Ordinal) || tx.ChargeId.Contains("-rep-", StringComparison.Ordinal) || tx.ChargeId.Contains("-inst-", StringComparison.Ordinal) ? "adm"
         : "pago";

    // ------------------------------------------------------------------ resposta no formato intpos

    /// <summary>
    /// Monta uma <see cref="RespostaPayGo"/> (formato intpos, o que `tef_transacao.resposta_txt`,
    /// o religamento e a reimpressão já entendem) a partir dos PWINFO_* lidos. Mapa:
    /// AUTHSYST→010 (rede), AUTEXTREF→012 (NSU), AUTHCODE→013, REQNUM→027 (código de controle),
    /// AUTDATETIME→022/023, CARDNAMESTD→040, CARDNAME→748, CARDPARCPAN→740, CNFREQ→729 (1→2, 0→1),
    /// RCPTPRN→737, RCPTCHOLDER→713, RCPTMERCH→715, RCPTCHSHORT→711, RCPTFULL→029 (só sem as
    /// diferenciadas). Extras da PGWebLib em 950 (AUTLOCREF), 951 (VIRTMERCH), 952 (AUTDATETIME cru).
    /// </summary>
    public static RespostaPayGo RespostaDaLib(string cmd, string id, long valorCent, TipoTef tipo, int parcelas, bool aprovada,
        IReadOnlyDictionary<ushort, string> r)
    {
        var c = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["000-000"] = cmd,
            ["001-000"] = id,
            ["009-000"] = aprovada ? "0" : "1",
        };
        void Def(string k, ushort info) { if (r.TryGetValue(info, out var v) && !string.IsNullOrWhiteSpace(v)) c[k] = v.Replace('\r', ' ').Trim(); }
        var tot = r.TryGetValue(PW.PWINFO_TOTAMNT, out var t) && long.TryParse(t.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var tc) ? tc : valorCent;
        if (tot > 0) c["003-000"] = tot.ToString(CultureInfo.InvariantCulture);
        c["004-000"] = "0";
        Def("010-000", PW.PWINFO_AUTHSYST);
        Def("012-000", PW.PWINFO_AUTEXTREF);
        Def("013-000", PW.PWINFO_AUTHCODE);
        Def("027-000", PW.PWINFO_REQNUM);
        Def("030-000", PW.PWINFO_RESULTMSG);
        if (!c.ContainsKey("030-000")) c["030-000"] = aprovada ? "TRANSACAO AUTORIZADA" : "TRANSACAO NAO AUTORIZADA";
        Def("040-000", PW.PWINFO_CARDNAMESTD);
        Def("748-000", PW.PWINFO_CARDNAME);
        if (!c.ContainsKey("040-000") && c.TryGetValue("748-000", out var nome)) c["040-000"] = nome;
        Def("740-000", PW.PWINFO_CARDPARCPAN);
        Def("950-000", PW.PWINFO_AUTLOCREF);
        Def("951-000", PW.PWINFO_VIRTMERCH);
        Def("952-000", PW.PWINFO_AUTDATETIME);
        // A DATA DA VENDA, DE QUALQUER UMA DAS DUAS TAGS (09/09/2026). O carimbo da rede
        // (PWINFO_AUTDATETIME) nao veio em nenhuma venda desta homologacao; o que a biblioteca
        // devolve sempre e PWINFO_DATETIME. Sem data guardada, o estorno saia sem TRNORIGDATE e
        // a biblioteca parava para o operador digitar "090926" na mao.
        var carimbo = r.TryGetValue(PW.PWINFO_AUTDATETIME, out var dtRede) && dtRede.Trim().Length >= 14
            ? dtRede.Trim()
            : r.TryGetValue(PW.PWINFO_DATETIME, out var dtLib) && dtLib.Trim().Length >= 14 ? dtLib.Trim() : null;
        if (!c.ContainsKey("952-000") && carimbo is not null) c["952-000"] = carimbo;
        if (carimbo is not null)
        {
            var s = carimbo;
            c["022-000"] = s.Substring(6, 2) + s.Substring(4, 2) + s.Substring(0, 4);   // DDMMYYYY
            c["023-000"] = s.Substring(8, 6);                                              // hhmmss
        }
        c["731-000"] = tipo switch { TipoTef.Credito => "1", TipoTef.Debito => "2", TipoTef.Voucher => "3", _ => "0" };
        c["732-000"] = parcelas > 1 ? "3" : "1";
        if (parcelas > 1) c["018-000"] = parcelas.ToString(CultureInfo.InvariantCulture);
        c["729-000"] = r.TryGetValue(PW.PWINFO_CNFREQ, out var cnf) && cnf.Trim() == "1" ? "2" : "1";
        if (r.TryGetValue(PW.PWINFO_RCPTPRN, out var prn) && int.TryParse(prn.Trim(), out var p) && p is >= 0 and <= 3)
            c["737-000"] = p.ToString(CultureInfo.InvariantCulture);
        Vias(c, "712", "713", r.GetValueOrDefault(PW.PWINFO_RCPTCHOLDER));
        Vias(c, "714", "715", r.GetValueOrDefault(PW.PWINFO_RCPTMERCH));
        Vias(c, "710", "711", r.GetValueOrDefault(PW.PWINFO_RCPTCHSHORT));
        if (!c.ContainsKey("712-000") && !c.ContainsKey("714-000")) Vias(c, "028", "029", r.GetValueOrDefault(PW.PWINFO_RCPTFULL));
        if (!c.ContainsKey("737-000"))
        {
            // O cupom reduzido (710/711) conta como via do CLIENTE. No C6PAY vem o reduzido do
            // portador com o diferenciado do lojista: olhando só o 713/715 o 737 saía 2, "só a
            // via da loja", e o papel do cliente ficava preso na resposta.
            var temCliente = c.ContainsKey("712-000") || c.ContainsKey("710-000");
            var temLojista = c.ContainsKey("714-000");
            c["737-000"] = temCliente && temLojista ? "3" : temCliente ? "1" : temLojista ? "2" : "0";
        }
        return RespostaPayGo.Analisar(ArquivoIntpos.Serializar(c));
    }

    /// <summary>Comprovante da PGWebLib: 40 colunas, linhas separadas por 0Dh (tolera CRLF/LF).</summary>
    private static void Vias(Dictionary<string, string> c, string contador, string prefixo, string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return;
        var linhas = texto.Replace("\r\n", "\r").Replace('\n', '\r').Split('\r').Select(l => l.TrimEnd()).ToList();
        while (linhas.Count > 0 && linhas[0].Trim().Length == 0) linhas.RemoveAt(0);
        while (linhas.Count > 0 && linhas[^1].Trim().Length == 0) linhas.RemoveAt(linhas.Count - 1);
        if (linhas.Count == 0) return;
        c[contador + "-000"] = linhas.Count.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < linhas.Count; i++) c[$"{prefixo}-{i + 1:000}"] = "\"" + linhas[i] + "\"";
    }

    /// <summary>TRNORIGDATE (DDMMAA) e TRNORIGTIME (hhmmss) a partir da resposta original (952 cru ou 022/023).</summary>
    private static (string? Data, string? Hora) DataHoraOriginal(RespostaPayGo r0)
    {
        var cru = r0.Campos.GetValueOrDefault("952-000")?.Trim();
        if (cru is { Length: >= 14 }) return (cru.Substring(6, 2) + cru.Substring(4, 2) + cru.Substring(2, 2), cru.Substring(8, 6));
        var d = r0.Data;
        return (d is { Length: 8 } ? d.Substring(0, 4) + d.Substring(6, 2) : null, r0.Hora);
    }

    private CartaoTef Cartao(RespostaPayGo r)
    {
        var rede = r.Rede;
        string? cnpj = null;
        if (!string.IsNullOrWhiteSpace(rede))
        {
            try { cnpj = CnpjDaRede?.Invoke(rede!); } catch { cnpj = null; }
            cnpj ??= ClientePayGo.CnpjConhecido(rede!);
        }
        var nome = r.NomeCartao ?? r.Produto;
        return new CartaoTef(CAut: r.Autorizacao, Cnpj: cnpj, TBand: ClientePayGo.TBand(nome), Bandeira: nome,
            Adquirente: rede, Nsu: r.Nsu, Parcelas: r.Parcelas, Terminal: r.Terminal,
            Valor: r.ValorCent is { } cc ? cc / 100m : null);
    }

    private async Task<bool> ImprimirSeguroAsync(TransacaoPayGo tx)
    {
        if (ImprimirComprovante is null || tx.Resposta is null || !tx.Resposta.TemVias) return true;
        try { return await ImprimirComprovante(tx).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Auditar?.Invoke($"pgweblib: impressão do comprovante {tx.ChargeId} lançou: {ex.Message}");
            return false;
        }
    }

    private bool GuardarSeguro(TransacaoPayGo t)
    {
        try { return Guardar(t); }
        catch { return false; }
    }

    private bool ConhecidaSegura(string reqNum)
    {
        try { return ConhecidaConfirmada?.Invoke(reqNum) == true; }
        catch { return false; }
    }

    private static DesfechoTef Falha(SituacaoTef s, string chargeId, string codigo, string motivo, bool posOcupado = false)
        => new(s, null, chargeId, null, motivo, posOcupado) { Codigo = codigo };
}
