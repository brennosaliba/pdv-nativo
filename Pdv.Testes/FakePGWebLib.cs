using System.Collections.Concurrent;
using System.Globalization;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// PGWebLib de mentira, FIEL à especificação pública: estado por transação (PW_iNewTransac
/// zera), PWRET_MOREDATA com menu/digitado e captura no pinpad (PW_iPP* + PW_iPPEventLoop com
/// NOTHING/DISPLAY/OK), PWRET_NOTHING no meio, CNFREQ na aprovação, PWRET_INVALIDTRN na
/// segunda confirmação, PWRET_NOTINST antes de PWOPER_INSTALL, PWRET_DLLNOTINIT antes de
/// PW_iInit, PWRET_INVCALL se a automação chama PW_iExecTransac sem capturar o que foi pedido,
/// e pendência (PWINFO_PND*) que BLOQUEIA vendas até PW_iConfirmation. Guarda tudo que a
/// automação fez (parâmetros, chamadas, leituras, confirmações) para a suíte conferir.
/// </summary>
public sealed class FakePGWebLib : IPGWebLib
{
    public enum Desfecho { Aprovar, Recusar, CancelarNoPinpad, Timeout, HostFora, NuncaTermina }

    /// <summary>Identificadores que a biblioteca de mentira usa nos menus/dados próprios.</summary>
    public const ushort IdSenhaLojista = 32700;
    public const ushort IdMenuAdm = 32701;
    /// <summary>
    /// Tag 0x2F, a do "dado genérico" dos passos 28 a 31 do roteiro v20260819. É a MESMA tag nos
    /// dois formatos que o roteiro exercita: digitado (passos 28 e 29) e menu (passos 30 e 31).
    /// Os dois nomes existem porque cada suíte fala do seu passo; o valor é um só.
    /// </summary>
    public const ushort IdMenuGenerico = 0x2F;
    /// <inheritdoc cref="IdMenuGenerico"/>
    public const ushort IdDadoGenerico = IdMenuGenerico;

    public sealed record Pendencia(string ReqNum, string LocRef, string ExtRef, string VirtMerch, string AuthSyst);
    public sealed record Transacao(byte Oper, Dictionary<ushort, string> Params);

    // ── roteiro ──────────────────────────────────────────────────────────
    public ConcurrentQueue<Desfecho> Roteiro { get; } = new();
    public bool Instalado { get; set; } = true;
    public bool InitLanca { get; set; }
    /// <summary>PW_End lança (a DLL caiu no meio do encerramento).</summary>
    public bool EndLanca { get; set; }
    /// <summary>Como a DLL de verdade (07/09/2026): PW_iInit devolve PWRET_WRITERR se o diretório de trabalho não existe (ela não o cria).</summary>
    public bool ExigePasta { get; set; }
    /// <summary>PWINFO_* que PW_iAddParam recusa com PWRET_INVPARAM em PWOPER_ADMIN (a DLL recusa USINGPINPAD e PPCOMMPORT lá, e aceita em INSTALL).</summary>
    public HashSet<ushort> ParamsRecusadosNoAdmin { get; } = new();
    /// <summary>PWINFO_* que PW_iGetResult recusa com PWRET_INVPARAM em vez de PWRET_NODATA (a DLL faz isso com AUTDATETIME).</summary>
    public HashSet<ushort> InfosRecusados { get; } = new();
    /// <summary>Pede o menu de redes mesmo com AUTHSYST já informado (prova o "responder do que já sabe").</summary>
    public bool SempreMenuRede { get; set; }
    /// <summary>
    /// Quantas vezes a biblioteca pede o MESMO menu genérico (tag 0x2F) na mesma venda. Passos 30 e
    /// 31 do roteiro v20260819: o autorizador C6PAY pede a tag duas vezes, e a segunda tem que
    /// chegar ao operador igual à primeira, com o prompt "SELECIONAR:".
    /// </summary>
    public int MenuGenericoVezes { get; set; }
    /// <summary>
    /// As credenciadoras que o menu de redes lista. UMA só é a loja de homologação com um
    /// autorizador instalado: o passo 05 do roteiro pede o Esc nesse menu, então ele tem que
    /// chegar ao operador mesmo com uma opção.
    /// </summary>
    public string[] RedesDoMenu { get; set; } = { "REDE", "CIELO", "PIX ITAU" };
    /// <summary>
    /// O menu administrativo vem com bNotificarCancelamento ligado: a biblioteca quer ser avisada
    /// se o operador apertar Esc nele. É o menu do passo 16 do roteiro v20260819.
    /// </summary>
    public bool MenuAdmPedeAviso { get; set; } = true;
    /// <summary>
    /// Passos 28 e 29 do roteiro v20260819: quantas vezes a biblioteca pede um dado DIGITADO na tag
    /// 0x2F (PWDAT_TYPED) no meio da venda. O roteiro faz uma venda de R$ 1001,00 no C6PAY, o
    /// operador digita ABC123 e a venda tem que aprovar. Zero = não pede.
    /// </summary>
    public int DadoDigitadoVezes { get; set; }
    public bool ComSenha { get; set; } = true;
    public bool PedirRemocao { get; set; } = true;
    public bool Cnfreq { get; set; } = true;
    /// <summary>
    /// A biblioteca responde SEM escrever PWINFO_RESULTMSG. Existe porque quase metade do roteiro
    /// v20260819 cobra uma frase da rede na tela ("TRANSAÇÃO APROVADA", "OPERAÇÃO CANCELADA") e o
    /// caixa passou a mostrar a frase DELA: é preciso provar também o contrário, que biblioteca
    /// calada não deixa o operador sem recado nem faz o caixa inventar uma frase.
    /// </summary>
    public bool SemResultMsg { get; set; }

    /// <summary>
    /// O que a biblioteca deixa em PWINFO_RESULTMSG quando a transação encerra por PW_iPPAbort (o
    /// Esc do operador). Vazio = ela encerra calada, que é como esta fake sempre se comportou.
    ///
    /// ⚠️ NÃO é fato medido: o cabeçalho não descreve o RESULTMSG do aborto, e a DLL de verdade só
    /// vai dizer na homologação. Serve para provar o que é responsabilidade NOSSA — que a frase da
    /// biblioteca, quando existe, chega à automação em vez de ser descartada porque quem cancelou
    /// foi o operador. É o que o passo 55 do roteiro cobra ("OPERAÇÃO CANCELADA"). Com a fake
    /// calada (o padrão) o caixa continua mostrando a frase da casa, e essa é a outra metade da
    /// prova.
    /// </summary>
    public string MensagemAoAbortar { get; set; } = "";

    /// <summary>PW_iConfirmation devolve PWRET_WRITERR uma vez (e a pendência fica).</summary>
    public bool FalharConfirmacao { get; set; }
    /// <summary>PW_iConfirmation lança (o processo caiu no meio).</summary>
    public bool ConfirmacaoLanca { get; set; }
    /// <summary>Pendência que a biblioteca descreve logo após PW_iInit (PWINFO_PND*).</summary>
    public Pendencia? PendenciaNoInit { get; set; }
    /// <summary>Aprova com CNFREQ=1 mas NÃO guarda a pendência: o PW_iConfirmation seguinte devolve PWRET_INVALIDTRN.</summary>
    public bool EsquecerPendencia { get; set; }

    /// <summary>A biblioteca perde a pendência que segurava (queda, outro módulo resolveu): CNF/REV dela viram PWRET_INVALIDTRN.</summary>
    public void Esquecer() => _pendente = null;
    public string Rede { get; set; } = "REDE";
    public string AutDateTime { get; set; } = "20260905143000";
    /// <summary>AUTRESPCODE que a rede devolve na aprovação. Redes reais mandam "00", "000", "0000" ou nada (vazio = não informa).</summary>
    public string AutRespCode { get; set; } = "00";
    /// <summary>PWINFO_IDLEPROCTIME que a biblioteca informa desde o PW_iInit (e de novo ao fim de cada transação). Vazio = PWRET_NODATA.</summary>
    public string IdleProcTime { get; set; } = "";
    /// <summary>PWINFO_IDLEPROCTIME que a biblioteca passa a informar DEPOIS de cada PW_iIdleProc. Vazio = PWRET_NODATA.</summary>
    public string IdleProcTimeDepois { get; set; } = "";

    // ── o que a automação fez ────────────────────────────────────────────
    public List<string> Chamadas { get; } = new();
    public List<Transacao> Transacoes { get; } = new();
    public Transacao? Ultima => Transacoes.Count > 0 ? Transacoes[^1] : null;
    public List<(uint Resultado, string ReqNum)> Confirmadas { get; } = new();
    public HashSet<ushort> Lidos { get; } = new();
    public int Inits { get; private set; }
    public int Ends { get; private set; }
    public int Abortos { get; private set; }
    public int IdleProcs { get; private set; }
    public Pendencia? Pendente => _pendente;
    public string? UltimoReqNum { get; private set; }
    public string? UltimoNsu { get; private set; }

    /// <summary>Quantas vezes QUALQUER automação leu PWINFO_CARDFULLPAN nesta bateria. Tem que ficar em zero.</summary>
    public static int LeiturasDePan;

    private bool _iniciada;
    private bool _pediuQr;
    private Pendencia? _pendente, _pendenteAoComecar;
    /// <summary>PWINFO_IDLEPROCTIME é da biblioteca, não da transação: sobrevive ao PW_iNewTransac.</summary>
    private string _idleProcTime = "";
    private static int _seq = 1000;

    // estado da transação corrente
    private byte _oper;
    private Dictionary<ushort, string> _params = new();
    private int _etapa;
    private Desfecho _d;
    private bool _pediuRede, _cancelada, _abortada;
    private int _menusGenericos;
    private int _dadosDigitados;
    private readonly Dictionary<ushort, string> _res = new();
    private ushort? _ppEsperado;
    private Queue<(short Ret, string Display)> _eventos = new();

    private void Log(string s) => Chamadas.Add(s);

    // ------------------------------------------------------------------ ambiente

    /// <summary>O que PW_iSetEnvironment recebeu, na ordem. Null enquanto ninguém chamou.</summary>
    public List<short> AmbientesPedidos { get; } = new();
    /// <summary>Como a biblioteca num terminal já instalado: recusa a troca de ambiente.</summary>
    public bool RecusarAmbiente { get; set; }

    /// <summary>
    /// Venda de Pix: a biblioteca pede que o caixa DESENHE o QR na tela (PWDAT_DSPQRCODE), em vez
    /// de mandar o cliente ler no pinpad. É o que o passo 55 do roteiro exercita.
    /// </summary>
    public bool PedirQrNaTela { get; set; }
    /// <summary>O conteúdo do QR que a biblioteca devolve em PWINFO_AUTHPOSQRCODE.</summary>
    public string QrCode { get; set; } = "00020126580014BR.GOV.BCB.PIX0136teste-pix-american-day-0000000000005204000053039865802BR5913AMERICAN DAY6009BELO HORIZ62070503***6304ABCD";
    /// <summary>A biblioteca manda o QR mas esquece o conteúdo: o caixa não pode mostrar QR vazio.</summary>
    public bool QrSemConteudo { get; set; }
    /// <summary>Biblioteca anterior à 4.1.43.10: o símbolo não existe.</summary>
    public bool AmbienteLanca { get; set; }

    public short SetEnvironment(short ambiente)
    {
        Log($"SetEnvironment({ambiente})");
        if (AmbienteLanca) throw new EntryPointNotFoundException("PW_iSetEnvironment");
        AmbientesPedidos.Add(ambiente);
        return RecusarAmbiente ? PW.PWRET_INVCALL : PW.PWRET_OK;
    }

    // ------------------------------------------------------------------ init

    public short Init(string diretorioTrabalho)
    {
        Log("Init");
        if (InitLanca) throw new DllNotFoundException("PGWebLib.dll");
        Inits++;
        if (_iniciada) return PW.PWRET_INVCALL;
        if (ExigePasta && !Directory.Exists(diretorioTrabalho)) return PW.PWRET_WRITERR;
        _iniciada = true;
        _idleProcTime = IdleProcTime;
        if (PendenciaNoInit is not null) _pendente = PendenciaNoInit;
        return PW.PWRET_OK;
    }

    /// <summary>PW_End: zera o "iniciada" (como a DLL, cujo detach vira no-op depois disto). Sem retorno, como na DLL.</summary>
    public void End()
    {
        Log("End");
        if (EndLanca) throw new AccessViolationException("PW_End caiu");
        Ends++;
        _iniciada = false;
    }

    public short NewTransac(byte operacao)
    {
        Log($"NewTransac({operacao})");
        if (!_iniciada) return PW.PWRET_DLLNOTINIT;
        if (!Instalado && operacao != PW.PWOPER_INSTALL) return PW.PWRET_NOTINST;
        _oper = operacao;
        _params = new Dictionary<ushort, string>();
        _etapa = 0;
        _pediuRede = _pediuQr = _cancelada = _abortada = false;
        _menusGenericos = 0;
        _dadosDigitados = 0;
        _res.Clear();
        _ppEsperado = null;
        _eventos.Clear();
        _d = Roteiro.TryDequeue(out var d) ? d : Desfecho.Aprovar;
        _pendenteAoComecar = _pendente;
        Transacoes.Add(new Transacao(operacao, _params));
        return PW.PWRET_OK;
    }

    public short AddParam(ushort info, string valor)
    {
        Log($"AddParam({info}={valor})");
        if (Transacoes.Count == 0 || _params is null) return PW.PWRET_TRNNOTINIT;
        if (valor is null || valor.Any(ch => ch < 0x20 || ch > 0x7E)) return PW.PWRET_INVPARAM;
        if (_oper == PW.PWOPER_ADMIN && ParamsRecusadosNoAdmin.Contains(info)) return PW.PWRET_INVPARAM;
        _params[info] = valor;
        return PW.PWRET_OK;
    }

    // ------------------------------------------------------------------ exec

    public short ExecTransac(out IReadOnlyList<PwGetData> pedidos)
    {
        Log("ExecTransac");
        pedidos = Array.Empty<PwGetData>();
        if (!_iniciada) return PW.PWRET_DLLNOTINIT;
        if (_ppEsperado is not null) return PW.PWRET_INVCALL;          // pediu captura e a automação não capturou
        if (_cancelada || _abortada)
        {
            // Encerrar por PW_iPPAbort é o passo 55 do roteiro (Esc na tela do QR). Se a biblioteca
            // escreve alguma coisa em PWINFO_RESULTMSG ao encerrar assim, ela é da REDE e tem que
            // chegar à automação — ver MensagemAoAbortar.
            if (MensagemAoAbortar.Length > 0 && !SemResultMsg) _res[PW.PWINFO_RESULTMSG] = MensagemAoAbortar;
            return PW.PWRET_CANCEL;
        }
        if (_params.ContainsKey(PW.PWINFO_OPERABORTED))
        {
            // A automação avisou que o operador desistiu do dado pedido (PWINFO_OPERABORTED): a
            // biblioteca encerra a transação cancelada e escreve a mensagem que o roteiro
            // v20260819 cobra no passo 16. O cabeçalho declara o parâmetro mas não descreve o
            // fluxo, então isto é a leitura do roteiro, para conferir contra a DLL na homologação.
            _cancelada = true;
            _res[PW.PWINFO_RESULTMSG] = "OPERACAO CANCELADA";
            return PW.PWRET_CANCEL;
        }
        // Pendência de OUTRA transação (existia quando esta começou) bloqueia o ponto de captura.
        if (_pendenteAoComecar is not null && ReferenceEquals(_pendenteAoComecar, _pendente) && _oper is PW.PWOPER_SALE or PW.PWOPER_SALEVOID)
        {
            _res[PW.PWINFO_RESULTMSG] = "TRANSACAO PENDENTE NAO RESOLVIDA";
            return PW.PWRET_INVCALL;
        }
        var ret = _oper switch
        {
            PW.PWOPER_SALE => Venda(out pedidos),
            PW.PWOPER_SALEVOID => Cancelamento(out pedidos),
            PW.PWOPER_ADMIN => Administrativa(out pedidos),
            PW.PWOPER_REPRINT => Reimpressao(),
            PW.PWOPER_INSTALL => Instalacao(),
            PW.PWOPER_VERSION => Versao(),
            _ => PW.PWRET_INVCALL,
        };
        if (SemResultMsg) _res.Remove(PW.PWINFO_RESULTMSG);
        return ret;
    }

    private short Venda(out IReadOnlyList<PwGetData> pedidos)
    {
        pedidos = Array.Empty<PwGetData>();
        foreach (var obrig in new[] { PW.PWINFO_TOTAMNT, PW.PWINFO_CURRENCY, PW.PWINFO_AUTNAME, PW.PWINFO_AUTVER })
            if (!_params.ContainsKey(obrig)) { _res[PW.PWINFO_RESULTMSG] = "PARAMETRO OBRIGATORIO AUSENTE " + obrig; return PW.PWRET_NOMANDATORY; }

        switch (_etapa)
        {
            case 0:
                if ((SempreMenuRede || !_params.ContainsKey(PW.PWINFO_AUTHSYST)) && !_pediuRede)
                {
                    _pediuRede = true;
                    pedidos = new[] { MenuRede() };
                    return PW.PWRET_MOREDATA;
                }
                if (!_params.ContainsKey(PW.PWINFO_AUTHSYST)) { _res[PW.PWINFO_RESULTMSG] = "REDE NAO INFORMADA"; return PW.PWRET_NOMANDATORY; }
                if (DadoDigitadoVezes > 0)
                {
                    // Passo 28: a automação não pode adiantar a tag 0x2F. O roteiro reprova com
                    // DemoErroTeste3 quem manda o dado antes de a biblioteca pedir.
                    if (_dadosDigitados == 0 && _params.ContainsKey(IdMenuGenerico))
                    {
                        _res[PW.PWINFO_RESULTMSG] = "DEMOERROTESTE3";
                        return PW.PWRET_FROMHOST_FIM;
                    }
                    if (_dadosDigitados < DadoDigitadoVezes)
                    {
                        _dadosDigitados++;
                        pedidos = new[] { DadoDigitado() };
                        return PW.PWRET_MOREDATA;
                    }
                }
                if (MenuGenericoVezes > 0)
                {
                    // Passos 30 e 31: a automação não pode adiantar a tag 0x2F, e o MESMO menu é
                    // pedido de novo depois de respondido. O roteiro reprova com DemoErroTeste4
                    // quem manda a tag antes de a biblioteca pedir.
                    if (_menusGenericos == 0 && _params.ContainsKey(IdMenuGenerico))
                    {
                        _res[PW.PWINFO_RESULTMSG] = "DEMOERROTESTE4";
                        return PW.PWRET_FROMHOST_FIM;
                    }
                    if (_menusGenericos < MenuGenericoVezes)
                    {
                        _menusGenericos++;
                        pedidos = new[] { MenuGenerico() };
                        return PW.PWRET_MOREDATA;
                    }
                }
                _etapa = 1;
                return PW.PWRET_NOTHING;                           // "chamar de novo"
            case 1:
                if (PedirQrNaTela && !_pediuQr)
                {
                    _pediuQr = true;
                    if (!QrSemConteudo) _res[PW.PWINFO_AUTHPOSQRCODE] = QrCode;
                    pedidos = new[] { new PwGetData(PW.PWDAT_DSPQRCODE, PW.PWINFO_AUTHPOSQRCODE, "LEIA O QR NO APLICATIVO DO BANCO") };
                    return PW.PWRET_MOREDATA;
                }
                _etapa = 2;
                pedidos = new[] { new PwGetData(PW.PWDAT_CARDINF, 0, "APROXIME OU INSIRA O CARTAO") };
                _ppEsperado = PW.PWDAT_CARDINF;
                _eventos = new Queue<(short, string)>(new[]
                {
                    (PW.PWRET_NOTHING, ""),
                    (PW.PWRET_DISPLAY, "APROXIME O CARTAO"),
                    (PW.PWRET_NOTHING, ""),
                    (_d == Desfecho.Timeout ? (PW.PWRET_TIMEOUT, "") : (PW.PWRET_OK, "")),
                });
                return PW.PWRET_MOREDATA;
            case 2:
                if (ComSenha)
                {
                    _etapa = 3;
                    pedidos = new[] { new PwGetData(PW.PWDAT_PPENCPIN, 0, "SENHA") };
                    _ppEsperado = PW.PWDAT_PPENCPIN;
                    _eventos = new Queue<(short, string)>(new[]
                    {
                        (PW.PWRET_DISPLAY, "DIGITE A SENHA"),
                        (PW.PWRET_NOTHING, ""),
                        (_d == Desfecho.CancelarNoPinpad ? (PW.PWRET_CANCEL, "") : (PW.PWRET_OK, "")),
                    });
                    return PW.PWRET_MOREDATA;
                }
                goto case 3;
            case 3:
                // Host
                switch (_d)
                {
                    case Desfecho.NuncaTermina:
                        return PW.PWRET_NOTHING;
                    case Desfecho.HostFora:
                        _res[PW.PWINFO_RESULTMSG] = "SEM COMUNICACAO COM O HOST";
                        return PW.PWRET_HOSTCONNERR;
                    case Desfecho.Recusar:
                        _res[PW.PWINFO_RESULTMSG] = "TRANSACAO NAO AUTORIZADA";
                        _res[PW.PWINFO_AUTRESPCODE] = "51";
                        return PW.PWRET_FROMHOST_FIM;
                }
                // Aprovada pelo host: resultado (e a pendência) já existem ANTES de tirar o cartão.
                Aprovar();
                if (PedirRemocao)
                {
                    _etapa = 4;
                    pedidos = new[] { new PwGetData(PW.PWDAT_PPREMCRD, 0, "RETIRE O CARTAO") };
                    _ppEsperado = PW.PWDAT_PPREMCRD;
                    _eventos = new Queue<(short, string)>(new[] { (PW.PWRET_DISPLAY, "RETIRE O CARTAO"), (PW.PWRET_NOTHING, ""), (PW.PWRET_OK, "") });
                    return PW.PWRET_MOREDATA;
                }
                _etapa = 5;
                return PW.PWRET_OK;
            case 4:
                _etapa = 5;
                return PW.PWRET_OK;
            default:
                return PW.PWRET_OK;
        }
    }

    /// <summary>
    /// Passo 28: o dado DIGITADO da tag 0x2F. Tamanho de 1 a 6 caracteres, que é o que cabe no
    /// ABC123 que o passo 29 manda digitar, e serve para provar que o caixa confere o tamanho
    /// antes de mandar em vez de empurrar qualquer coisa para a biblioteca.
    /// </summary>
    private static PwGetData DadoDigitado()
        => new(PW.PWDAT_TYPED, IdDadoGenerico, "DIGITE O DADO:", TamanhoMinimo: 1, TamanhoMaximo: 6);

    /// <summary>O menu genérico dos passos 30 e 31: prompt "SELECIONAR:" com as opções 123456 e ABCDEF.</summary>
    private static PwGetData MenuGenerico() => new(PW.PWDAT_MENU, IdMenuGenerico, "SELECIONAR:", new[]
    {
        new PwOpcaoMenu("123456", "123456"), new PwOpcaoMenu("ABCDEF", "ABCDEF"),
    });

    private PwGetData MenuRede()
        => new(PW.PWDAT_MENU, PW.PWINFO_AUTHSYST, "REDE", RedesDoMenu.Select(r => new PwOpcaoMenu(r, r)).ToList());

    private void Aprovar()
    {
        var n = Interlocked.Increment(ref _seq);
        var reqNum = n.ToString(CultureInfo.InvariantCulture);
        var nsu = (700000 + n).ToString(CultureInfo.InvariantCulture);
        var rede = _params.GetValueOrDefault(PW.PWINFO_AUTHSYST) ?? Rede;
        var tot = _params.GetValueOrDefault(PW.PWINFO_TOTAMNT) ?? _params.GetValueOrDefault(PW.PWINFO_TRNORIGAMNT) ?? "0";
        var valor = long.TryParse(tot, out var vc) ? new Dinheiro(vc).Formatado() : tot;
        var parc = _params.GetValueOrDefault(PW.PWINFO_INSTALLMENTS);
        var venda = _oper == PW.PWOPER_SALEVOID ? "CANCELAMENTO DE VENDA" : parc is null ? "VENDA CREDITO A VISTA" : $"VENDA CREDITO {parc}X";
        UltimoReqNum = reqNum; UltimoNsu = nsu;
        _res[PW.PWINFO_CNFREQ] = Cnfreq ? "1" : "0";
        _res[PW.PWINFO_REQNUM] = reqNum;
        _res[PW.PWINFO_AUTLOCREF] = "LOC" + reqNum;
        _res[PW.PWINFO_AUTEXTREF] = nsu;
        _res[PW.PWINFO_VIRTMERCH] = "VM1";
        _res[PW.PWINFO_AUTHSYST] = rede;
        _res[PW.PWINFO_AUTHCODE] = "A" + reqNum;
        if (AutRespCode.Length > 0) _res[PW.PWINFO_AUTRESPCODE] = AutRespCode;
        _res[PW.PWINFO_AUTDATETIME] = AutDateTime;
        _res[PW.PWINFO_CARDNAME] = "VISA CREDITO";
        _res[PW.PWINFO_CARDNAMESTD] = "VISA";
        _res[PW.PWINFO_CARDPARCPAN] = "489391******0008";
        _res[PW.PWINFO_CARDFULLPAN] = "4893910000000008";        // existe: a automação NÃO pode ler
        _res[PW.PWINFO_CARDTYPE] = _params.GetValueOrDefault(PW.PWINFO_CARDTYPE) ?? "1";
        _res[PW.PWINFO_TOTAMNT] = tot;
        _res[PW.PWINFO_RESULTMSG] = "TRANSACAO AUTORIZADA";
        _res[PW.PWINFO_RCPTPRN] = "3";
        _res[PW.PWINFO_RCPTCHOLDER] = $"PAYGO FAKE\rVIA CLIENTE\r{rede} NSU:{nsu}\r{venda}\rVALOR: {valor}\r";
        _res[PW.PWINFO_RCPTMERCH] = $"PAYGO FAKE\rVIA ESTABELECIMENTO\r{rede} NSU:{nsu}\r{venda}\rVALOR: {valor}\rAUT:A{reqNum}\r";
        _res[PW.PWINFO_RCPTCHSHORT] = $"PAYGO FAKE {rede} NSU:{nsu} {valor}";
        _res[PW.PWINFO_RCPTFULL] = _res[PW.PWINFO_RCPTCHOLDER] + "\r" + _res[PW.PWINFO_RCPTMERCH];
        _idleProcTime = IdleProcTime;
        if (Cnfreq && !EsquecerPendencia) _pendente = new Pendencia(reqNum, "LOC" + reqNum, nsu, "VM1", rede);
    }

    private short Cancelamento(out IReadOnlyList<PwGetData> pedidos)
    {
        pedidos = Array.Empty<PwGetData>();
        foreach (var obrig in new[] { PW.PWINFO_TRNORIGNSU, PW.PWINFO_TRNORIGAMNT, PW.PWINFO_TRNORIGDATE })
            if (!_params.ContainsKey(obrig)) { _res[PW.PWINFO_RESULTMSG] = "PARAMETRO OBRIGATORIO AUSENTE " + obrig; return PW.PWRET_NOMANDATORY; }
        switch (_etapa)
        {
            case 0:
                _etapa = 1;
                pedidos = new[] { new PwGetData(PW.PWDAT_USERAUTH, IdSenhaLojista, "SENHA DO LOJISTA", null, 4, 8, Ocultar: true) };
                return PW.PWRET_MOREDATA;
            case 1:
                if (!_params.ContainsKey(IdSenhaLojista)) return PW.PWRET_NOMANDATORY;
                if (_d == Desfecho.Recusar) { _res[PW.PWINFO_RESULTMSG] = "CANCELAMENTO NAO AUTORIZADO"; _res[PW.PWINFO_AUTRESPCODE] = "57"; return PW.PWRET_FROMHOST_FIM; }
                Aprovar();
                _res[PW.PWINFO_RESULTMSG] = "CANCELAMENTO AUTORIZADO";
                _etapa = 2;
                return PW.PWRET_OK;
            default:
                return PW.PWRET_OK;
        }
    }

    private short Administrativa(out IReadOnlyList<PwGetData> pedidos)
    {
        pedidos = Array.Empty<PwGetData>();
        switch (_etapa)
        {
            case 0:
                _etapa = 1;
                pedidos = new[] { new PwGetData(PW.PWDAT_MENU, IdMenuAdm, "ADMINISTRATIVA", new[]
                {
                    new PwOpcaoMenu("TESTE DE COMUNICACAO", "1"), new PwOpcaoMenu("REIMPRESSAO", "2"), new PwOpcaoMenu("RELATORIO", "3"),
                }, NotificarCancelamento: MenuAdmPedeAviso) };
                return PW.PWRET_MOREDATA;
            case 1:
                if (!_params.TryGetValue(IdMenuAdm, out var op)) return PW.PWRET_NOMANDATORY;
                _res[PW.PWINFO_CNFREQ] = "0";
                _res[PW.PWINFO_RESULTMSG] = op == "1" ? "COMUNICACAO OK" : op == "2" ? "REIMPRESSAO OK" : "RELATORIO EMITIDO";
                _res[PW.PWINFO_RCPTFULL] = "PAYGO FAKE\rADMINISTRATIVA " + op + "\r";
                _res[PW.PWINFO_RCPTPRN] = "2";
                _etapa = 2;
                return PW.PWRET_OK;
            default:
                return PW.PWRET_OK;
        }
    }

    private short Reimpressao()
    {
        _res[PW.PWINFO_CNFREQ] = "0";
        _res[PW.PWINFO_RESULTMSG] = "REIMPRESSAO OK";
        _res[PW.PWINFO_RCPTFULL] = "PAYGO FAKE\rREIMPRESSAO ULTIMA\r";
        return PW.PWRET_OK;
    }

    private short Instalacao()
    {
        Instalado = true;
        _res[PW.PWINFO_CNFREQ] = "0";
        _res[PW.PWINFO_RESULTMSG] = "INSTALACAO CONCLUIDA";
        return PW.PWRET_OK;
    }

    private short Versao()
    {
        _res[PW.PWINFO_RESULTMSG] = "PGWebLib fake 0.1";
        return PW.PWRET_OK;
    }

    // ------------------------------------------------------------------ resultados

    public short GetResult(ushort info, out string valor)
    {
        Lidos.Add(info);
        if (info == PW.PWINFO_CARDFULLPAN) Interlocked.Increment(ref LeiturasDePan);
        valor = "";
        if (InfosRecusados.Contains(info)) return PW.PWRET_INVPARAM;
        if (_pendente is not null)
        {
            var p = _pendente;
            var pnd = info switch
            {
                PW.PWINFO_PNDREQNUM => p.ReqNum,
                PW.PWINFO_PNDAUTLOCREF => p.LocRef,
                PW.PWINFO_PNDAUTEXTREF => p.ExtRef,
                PW.PWINFO_PNDVIRTMERCH => p.VirtMerch,
                PW.PWINFO_PNDAUTHSYST => p.AuthSyst,
                _ => null,
            };
            if (pnd is not null) { valor = pnd; return PW.PWRET_OK; }
        }
        if (info == PW.PWINFO_IDLEPROCTIME) { valor = _idleProcTime; return _idleProcTime.Length > 0 ? PW.PWRET_OK : PW.PWRET_NODATA; }
        if (_res.TryGetValue(info, out var v)) { valor = v; return PW.PWRET_OK; }
        return PW.PWRET_NODATA;
    }

    public short Confirmation(uint resultado, string reqNum, string locRef, string extRef, string virtMerch, string authSyst)
    {
        Log($"Confirmation({resultado},{reqNum})");
        if (ConfirmacaoLanca) throw new InvalidOperationException("processo caiu");
        if (_pendente is null || _pendente.ReqNum != reqNum) return PW.PWRET_INVALIDTRN;
        if (FalharConfirmacao) { FalharConfirmacao = false; return PW.PWRET_WRITERR; }
        Confirmadas.Add((resultado, reqNum));
        _pendente = null;
        return PW.PWRET_OK;
    }

    public short WaitConfirmation() { Log("WaitConfirmation"); return PW.PWRET_OK; }
    /// <summary>Depois de cada PW_iIdleProc a biblioteca informa o próximo horário (ou nada): a automação tem que reler.</summary>
    public short IdleProc()
    {
        Log("IdleProc");
        IdleProcs++;
        if (!_iniciada) return PW.PWRET_DLLNOTINIT;
        _idleProcTime = IdleProcTimeDepois;
        return PW.PWRET_OK;
    }

    public short GetOperations(byte tipoOperacao, out IReadOnlyList<PwOperacao> operacoes)
    {
        Log($"GetOperations({tipoOperacao})");
        // Como a DLL de verdade (medido em 07/09/2026, PGWebLib 4.1.50.924): num terminal
        // SEM instalação o menu administrativo continua respondendo, com INSTALACAO no
        // meio, e a lista de VENDA devolve PWRET_NOTINST.
        if (!Instalado && tipoOperacao == PW.OPERACOES_DE_VENDA)
        {
            operacoes = Array.Empty<PwOperacao>();
            return PW.PWRET_NOTINST;
        }
        if (tipoOperacao == PW.OPERACOES_DE_VENDA)
        {
            operacoes = new[] { new PwOperacao(2, "VENDA", "33") };
            return PW.PWRET_OK;
        }
        operacoes = new[] { new PwOperacao(1, "TESTE DE COMUNICACAO", "1"), new PwOperacao(2, "REIMPRESSAO", "2") };
        return PW.PWRET_OK;
    }

    // ------------------------------------------------------------------ pinpad

    private short PP(string nome, ushort tipo, ushort indice)
    {
        Log($"{nome}({indice})");
        if (_ppEsperado != tipo) return PW.PWRET_INVCALL;
        _ppEsperado = null;
        return PW.PWRET_OK;
    }

    public short PPGetCard(ushort indice) => PP("PPGetCard", PW.PWDAT_CARDINF, indice);
    public short PPGetPIN(ushort indice) => PP("PPGetPIN", PW.PWDAT_PPENCPIN, indice);
    public short PPGetData(ushort indice) => PP("PPGetData", PW.PWDAT_PPENTRY, indice);
    public short PPGoOnChip(ushort indice) => PP("PPGoOnChip", PW.PWDAT_CARDOFF, indice);
    public short PPFinishChip(ushort indice) => PP("PPFinishChip", PW.PWDAT_CARDONL, indice);
    public short PPConfirmData(ushort indice) => PP("PPConfirmData", PW.PWDAT_PPCONF, indice);
    public short PPPositiveConfirmation(ushort indice) => PP("PPPositiveConfirmation", PW.PWDAT_PPDATAPOSCNF, indice);
    public short PPRemoveCard() => PP("PPRemoveCard", PW.PWDAT_PPREMCRD, 0);
    public short PPGenericCMD(ushort indice) => PP("PPGenericCMD", PW.PWDAT_PPGENCMD, indice);

    public short PPEventLoop(out string display)
    {
        Log("PPEventLoop");
        display = "";
        if (_abortada) { _cancelada = true; return PW.PWRET_CANCEL; }
        if (_eventos.Count == 0) return PW.PWRET_NOTHING;
        var (ret, disp) = _eventos.Dequeue();
        display = disp;
        if (ret == PW.PWRET_CANCEL) _cancelada = true;
        return ret;
    }

    /// <summary>
    /// PW_iPPAbort (spec): cancela a captura em andamento no pinpad. Daí em diante, nesta
    /// transação, PW_iPPEventLoop e PW_iExecTransac devolvem PWRET_CANCEL (o fluxo em curso
    /// encerra cancelado); o que já tinha sido aprovado (CNFREQ/REQNUM) continua legível para
    /// a automação desfazer. Só PW_iNewTransac zera.
    /// </summary>
    public short PPAbort()
    {
        Log("PPAbort");
        Abortos++;
        _abortada = true;
        _ppEsperado = null;
        return PW.PWRET_OK;
    }

    public short TransactionInquiry(string xmlRequisicao, out string xmlResposta)
    {
        xmlResposta = "<resposta/>";
        return PW.PWRET_OK;
    }
}
