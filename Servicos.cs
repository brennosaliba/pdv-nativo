using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;
using Pdv.Telas;

namespace Pdv;

/// <summary>
/// Onde as peças são ligadas: sessão da nuvem, emissor fiscal e TEF.
///
/// Fica num lugar só porque as três compartilham a MESMA sessão. Cada tela criando a
/// sua própria levaria a renovações concorrentes do token — e duas trocas simultâneas
/// invalidam o refresh_token uma da outra, derrubando o caixa no meio do expediente.
/// </summary>
public static class Servicos
{
    /// <summary>
    /// Cupom de exemplo para conferir layout, corte e QR sem emitir documento fiscal.
    /// Os valores são escolhidos para exercitar o que costuma quebrar: nome de produto
    /// longo, quantidade fracionária (item pesável), troco e CPF na nota.
    /// </summary>
    public static DadosCupom CupomDeExemplo(string loja, string cnpj, int serie) => new(
        EmitenteNome: loja.Length > 0 ? loja : "LOJA DE TESTE",
        EmitenteCnpj: cnpj.Length == 14 ? cnpj : "62177839000238",
        EmitenteIe: "0012345670098",
        EmitenteEndereco: "R FERNANDES TOURINHO 137 LOJA 1 - SAVASSI - BELO HORIZONTE/MG - CEP 30112-000",
        Numero: 0, Serie: serie,
        Chave: "31260862177839000238650030000000041649223753",
        Emissao: DateTime.Now,
        QrCode: "https://portalsped.fazenda.mg.gov.br/portalnfce/sistema/qrcode.xhtml?p=31260862177839000238650030000000041649223753|2|2|1|EXEMPLO",
        TpAmb: 2,
        Itens: new[]
        {
            new ItemCupom("7891", "COOKIE TRIPLO CHOCOLATE COM NOZES", Quantidade.Um, "UN",
                Dinheiro.DeReais(13.90m), Dinheiro.DeReais(13.90m)),
            new ItemCupom("7892", "AGUA MINERAL 500ML", new Quantidade(2000), "UN",
                Dinheiro.DeReais(6), Dinheiro.DeReais(12)),
            new ItemCupom("7893", "DONUT A GRANEL", new Quantidade(375), "KG",
                Dinheiro.DeReais(89.90m), Dinheiro.DeReais(33.71m)),
        },
        Total: Dinheiro.DeReais(59.61m),
        VNf: 59.61m,
        Pagamentos: new[] { new PagamentoCupom("Dinheiro", Dinheiro.DeReais(59.61m)) },
        Recebido: Dinheiro.DeReais(100),
        Documento: "11144477735",
        Contingencia: false,
        Operador: "TESTE",
        Protocolo: "131261827473060",
        ProtocoloEm: DateTime.Now);

    private static readonly object Trava = new();
    private static Nuvem? _nuvem;
    private static RealtimeKds? _sino;
    private static IEmissorFiscal? _emissor;
    private static IProvedorTef? _tef;

    /// <summary>
    /// Sino do KDS (websocket). Um por processo; nasce no primeiro uso e vive
    /// até o app fechar. Acelerador do polling, nunca substituto.
    /// </summary>
    public static RealtimeKds Sino(string loja)
    {
        if (_sino is null)
        {
            var n = Nuvem();
            _sino = new RealtimeKds(Pdv.Nucleo.Nuvem.UrlPadrao, Pdv.Nucleo.Nuvem.AnonKey,
                                    () => n.TokenAsync(), loja);
            _sino.Iniciar();
        }
        return _sino;
    }

    public static Nuvem Nuvem()
    {
        lock (Trava)
        {
            if (_nuvem is not null) return _nuvem;
            var n = new Nuvem(UrlNuvem());
            // Re-login silencioso quando nem o refresh_token serve mais. A credencial é
            // do TERMINAL e está cifrada com DPAPI — por isso vem por delegate, e não
            // lida direto pelo núcleo.
            n.Credenciais = () =>
            {
                var seg = Configuracao.LerSegredos();
                var email = seg.GetValueOrDefault("nuvemEmail", "");
                var senha = seg.GetValueOrDefault("nuvemSenha", "");
                return email.Length > 0 && senha.Length > 0 ? (email, senha) : null;
            };
            _nuvem = n;
            return n;
        }
    }

    /// <summary>
    /// Emissor da NFC-e: nuvem primeiro, agente local só quando a rede não responde.
    ///
    /// A ordem é essa por decisão do dono (fibra tem 99,99% de disponibilidade) e porque
    /// emitindo pela nuvem o certificado A1 continua no servidor, não no PC do balcão —
    /// e a nota entra em `nfce_emitidas`, aparecendo na 2ª via e no extrato contábil.
    /// Nota emitida pelo agente local fica só na guarda em disco do caixa.
    /// </summary>
    public static IEmissorFiscal Emissor()
    {
        lock (Trava)
        {
            if (_emissor is not null) return _emissor;
            var nuvem = Nuvem();
            using var cx = Banco.Abrir();
            var t = cx.QueryFirstOrDefault("SELECT cnpj, serie_nfce, ambiente FROM terminal LIMIT 1");
            var agenteUrl = Vendas.Config(cx, "agente_url", "http://127.0.0.1:4610")!;

            // Terminal SEM conta de nuvem não é terminal com defeito: é um modo de
            // operação legítimo (loja que emite só pelo agente local). Tratar isso como
            // falha de autenticação bloquearia a venda numa loja que tem emissor são —
            // que foi o que aconteceu no primeiro teste. Falha de sessão num terminal
            // QUE ESTÁ configurado continua bloqueando, porque aí é problema de verdade.
            if (!TemContaDeNuvem())
            {
                _emissor = new EmissorAgente(agenteUrl);
                return _emissor;
            }

            _emissor = new EmissorResolvido(
                new EmissorNuvem(ct => nuvem.TokenAsync(ct), UrlNuvem(), t?.cnpj as string)
                {
                    GarantirSessao = ct => nuvem.SessaoOkAsync(ct),
                },
                new EmissorAgente(agenteUrl))
            {
                // A trava de série existe porque os dois caminhos numeram em contadores
                // diferentes: série igual nos dois = Rejeição 539 em cascata, e ela só
                // aparece na hora da venda, com cliente no balcão.
                //
                // ⚠️ NÃO usar terminal.serie_nfce aqui: aquela é a série DESTE CAIXA, usada
                // pelo agente local. A série da nuvem é definida no servidor (nfce_config) e
                // o caixa não tem como descobri-la sozinho — por isso vem de config, e fica
                // nula quando ninguém informou (aí a trava avisa em vez de bloquear).
                SerieNuvem = int.TryParse(Vendas.Config(cx, "serie_nuvem"), out var sn) ? sn : null,
                TpAmbEsperado = t is null ? null : Convert.ToInt32(t.ambiente),
            };
            // A sonda roda num relógio de fundo pra venda nunca pagar o pré-voo na
            // frente do cliente — com a nuvem doente eram ~8 s parado em "Emitindo".
            ((EmissorResolvido)_emissor).LigarSondaDeFundo();
            return _emissor;
        }
    }

    private static GuardaNuvem? _guarda;
    private static Drenagem? _drenagem;

    /// <summary>Dreno da fila de vendas. Null sem identidade de escrita (caixa não pareado).</summary>
    public static Drenagem? Dreno()
    {
        lock (Trava)
        {
            if (_drenagem is not null) return _drenagem;
            if (!TemContaDeNuvem()) return null;
            _drenagem = new Drenagem(Nuvem(), UrlNuvem());
            // Sobe a fila sozinha (a cada 45 s e quando a rede volta). O painel
            // precisa refletir a venda em SEGUNDOS — o dono não pode abrir o
            // relatório à noite e ver R$ 0,00 porque ninguém apertou um botão.
            _drenagem.Iniciar();
            return _drenagem;
        }
    }

    /// <summary>
    /// Sobe para a nuvem os XMLs das notas que saíram pelo agente local.
    ///
    /// Null quando o terminal não tem conta de nuvem — e nesse modo a guarda de 5 anos
    /// simplesmente não existe: a nota mora só no HD do caixa. O rodapé avisa.
    /// </summary>
    public static GuardaNuvem? Guarda()
    {
        lock (Trava)
        {
            if (_guarda is not null) return _guarda;
            if (!TemContaDeNuvem()) return null;
            using var cx = Banco.Abrir();
            _guarda = new GuardaNuvem(Nuvem(),
                Vendas.Config(cx, "agente_url", "http://127.0.0.1:4610")!, UrlNuvem());
            _guarda.Iniciar();
            return _guarda;
        }
    }

    /// <summary>Se o terminal tem credencial de nuvem gravada (cifrada com DPAPI).</summary>
    public static bool TemContaDeNuvem()
    {
        var seg = Configuracao.LerSegredos();
        return seg.GetValueOrDefault("nuvemEmail", "").Length > 0
            && seg.GetValueOrDefault("nuvemSenha", "").Length > 0;
    }

    /// <summary>
    /// Quantas notas assinadas neste PC ainda esperam a SEFAZ. Ver
    /// <see cref="EmissorResolvido.FilaLocalAsync"/>: pergunta ao agente mesmo com a nuvem
    /// de pé, porque a fila que interessa é a que sobrou de ontem.
    ///
    /// O ramo do <see cref="EmissorAgente"/> puro não é enfeite: terminal sem conta de
    /// nuvem opera SÓ pelo agente, e é justamente a loja que vive de contingência.
    /// </summary>
    public static async Task<int> NotasEsperandoSefazAsync()
    {
        try
        {
            return Emissor() switch
            {
                EmissorResolvido r => await r.FilaLocalAsync(CancellationToken.None),
                EmissorAgente a => (await a.SondarAsync(CancellationToken.None)).Pendentes,
                _ => 0,
            };
        }
        catch { return 0; }
    }

    /// <summary>De onde a nota está saindo — vai no rodapé, porque muda a série da nota.</summary>
    public static string CaminhoDoEmissor() => Emissor() switch
    {
        EmissorResolvido r => r.CaminhoAtual,
        EmissorAgente => "agente",
        _ => "nuvem",
    };

    /// <summary>
    /// TEF; null quando o caixa ainda não tem maquininha ligada.
    /// `config['tef_provedor']` (regra em <see cref="SelecaoTef.Escolher"/>): `paygo` = PayGo Windows
    /// por troca de arquivos; `controlpay` = WebService da PayGo; `pgweblib` = PayGo Windows pela
    /// biblioteca PGWebLib (DLL); qualquer outra coisa = Smart TEF pela nuvem (o caminho antigo).
    /// </summary>
    public static IProvedorTef? Tef()
    {
        // Troca adiada (RecarregarTef com comando em voo): a instância velha sai daqui e é
        // descartada FORA do lock, como no caminho direto.
        IDisposable? vencida = null;
        try { return TefSobTrava(ref vencida); }
        finally { Descartar(vencida); }
    }

    private static IProvedorTef? TefSobTrava(ref IDisposable? vencida)
    {
        lock (Trava)
        {
            if (_tefVencido && _tef is IProvedorTefOperavel { Ocupado: false }) { vencida = _tef as IDisposable; _tef = null; _tefVencido = false; }
            if (_tef is not null) return _tef;
            using var cx = Banco.Abrir();
            var selecao = SelecaoTef.Escolher(Vendas.Config(cx, "tef_habilitado"), Vendas.Config(cx, "tef_provedor"));
            if (selecao == ProvedorTef.Nenhum) return null;

            if (selecao == ProvedorTef.ControlPay)
            {
                // ControlPay (WebService da PayGo): o PayGo Windows desta máquina é o terminal;
                // chave de integração e senha técnica vivem no cofre DPAPI, nunca na tabela config.
                var seg = Configuracao.LerSegredos();
                var chave = seg.GetValueOrDefault("cpayChave", "");
                if (string.IsNullOrWhiteSpace(chave)) return null;   // sem chave não existe provedor (a tela avisa)
                var versao = typeof(Servicos).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
                _tef = new ClienteControlPay(new OpcoesControlPay(
                        BaseUrl: OpcoesControlPay.UrlDoAmbiente(Vendas.Config(cx, "tef_cpay_ambiente")),
                        Chave: chave,
                        SenhaTecnica: seg.GetValueOrDefault("cpaySenhaTecnica", "314159"),
                        TerminalId: Vendas.Config(cx, "tef_cpay_terminal") ?? "",
                        PessoaId: Vendas.Config(cx, "tef_cpay_pessoa") ?? "",
                        UserAgent: "Pdv.AmericanDay/" + versao,
                        // Autorizador fixo (homologação): sem isso o PayGo abre o menu de redes e
                        // a venda pode cair na rede errada. Vazio = roteamento da PayGo decide.
                        Adquirente: Vendas.Config(cx, "tef_cpay_adquirente"),
                        AdquirentePix: Vendas.Config(cx, "tef_cpay_adquirente_pix")))
                {
                    // Teto do POST que cria a cobrança, separado do teto da cobrança em si. Ajustável
                    // pela config para não precisar de exe novo se a loja/PayGo ficar mais lenta.
                    TempoCriacaoMs = int.TryParse(Vendas.Config(cx, "tef_cpay_timeout_criacao_s"), out var tc) ? tc * 1000 : 90_000,
                    // "0" faz a API responder na hora em vez de segurar até o PayGo pegar a
                    // transação. Padrão continua o homologado (segurar).
                    EsperarTefPegar = Vendas.Config(cx, "tef_cpay_esperar_tef") != "0",
                    TempoMaxEmPagamentoMs = int.TryParse(Vendas.Config(cx, "tef_cpay_timeout_s"), out var ts) ? ts * 1000 : 60_000,
                    TempoMaxPixMs = int.TryParse(Vendas.Config(cx, "tef_cpay_timeout_pix_s"), out var tp) ? tp * 1000 : 180_000,
                    Guardar = t => GuardarTef(t, "controlpay"),
                    CnpjDaRede = rede =>
                    {
                        using var c2 = Banco.Abrir();
                        return Vendas.Config(c2, "tef_cnpj_rede_" + rede.Trim().ToLowerInvariant());
                    },
                    ImprimirComprovante = ImprimirComprovantePayGoAsync,
                    Auditar = detalhe =>
                    {
                        try
                        {
                            using var c4 = Banco.Abrir();
                            Caixa.Auditar(c4, null, "tef_controlpay", null, null, detalhe);
                        }
                        catch { /* auditoria não derruba cobrança */ }
                        // Cópia legível dos tempos num arquivo só, para o dono conseguir mandar sem
                        // depender de abrir banco. A auditoria continua sendo a fonte de verdade.
                        if (detalhe.StartsWith("controlpay: tempo", StringComparison.Ordinal))
                            AnotarTempoTef(detalhe);
                    },
                };
                return _tef;
            }

            if (selecao == ProvedorTef.PGWebLib)
            {
                // PayGo Windows pela BIBLIOTECA (PGWebLib.dll, DllImport). A instância nativa só
                // existe aqui: a bateria roda o mesmo provedor contra uma DLL de mentira.
                // Reaproveita tef_paygo_empresa/rede/rede_pix (a loja tem uma rede só) e lê
                // tef_pgweb_dir / tef_pgweb_porta_pinpad / tef_pgweb_capacidades.
                var versao = typeof(Servicos).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
                string? Cfg(string chave) => Vendas.Config(cx, chave);
                PGWebLibNativa.UsarPasta(ConfigPGWebLib.PastaDll(Cfg));   // tef_pgweb_dll: de onde carregar a PGWebLib.dll
                var pg = new ProvedorPGWebLib(new PGWebLibNativa(), ConfigPGWebLib.Diretorio(Cfg), ConfigPGWebLib.Opcoes(Cfg, versao))
                {
                    Guardar = t => GuardarTef(t, "pgweblib"),
                    CnpjDaRede = rede =>
                    {
                        using var c2 = Banco.Abrir();
                        return Vendas.Config(c2, "tef_cnpj_rede_" + rede.Trim().ToLowerInvariant());
                    },
                    // Pendência que a biblioteca descreve no boot (PWINFO_PND*): confirma só o que
                    // ESTE caixa já deu como pago; o resto é desfeito (REV_PWR_AUT). O REQNUM
                    // mora em cod_controle, o mesmo lugar do 027 do PayGo por arquivos.
                    ConhecidaConfirmada = reqNum =>
                    {
                        using var c3 = Banco.Abrir();
                        return c3.ExecuteScalar<int>("""
                            SELECT COUNT(*) FROM tef_transacao
                             WHERE provedor = 'pgweblib' AND cod_controle = @C
                               AND (situacao IN ('pago','cnf_sem_ack') OR payment_status = 'cnf_sem_ack')
                            """, new { C = reqNum }) > 0;
                    },
                    // Comprovante ANTES do PW_iConfirmation: a impressão decide o CNF (spec).
                    ImprimirComprovante = ImprimirComprovantePayGoAsync,
                    // Menu de redes sem rede gravada, parcelas, senha do lojista: a biblioteca
                    // pergunta e a tela responde com os diálogos da casa.
                    Perguntar = PerguntarNaTelaAsync,
                    // Pix: a biblioteca manda o caixa DESENHAR o QR (PWDAT_DSPQRCODE). A tela abre
                    // e volta na hora; quem espera o cliente pagar e o laco do provedor.
                    Exibir = ExibirNaTelaAsync,
                    FecharExibicao = FecharExibicaoNaTela,
                    Auditar = detalhe =>
                    {
                        try
                        {
                            using var c4 = Banco.Abrir();
                            Caixa.Auditar(c4, null, "tef_pgweblib", null, null, detalhe);
                        }
                        catch { /* auditoria não derruba cobrança */ }
                    },
                };
                // PW_iIdleProc no horário que a biblioteca pedir (PWINFO_IDLEPROCTIME); barato,
                // só roda com nada em voo.
                pg.IniciarIdle(60_000);
                _tef = pg;
                return _tef;
            }

            if (selecao == ProvedorTef.PayGo)
            {
                var versao = typeof(Servicos).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
                var caps = int.TryParse(Vendas.Config(cx, "tef_paygo_capacidades"), out var c)
                    ? c : ClientePayGo.CapacidadesPadrao;
                _tef = new ClientePayGo(
                    Vendas.Config(cx, "tef_paygo_pasta") ?? ClientePayGo.PastaPadrao,
                    new OpcoesPayGo(
                        // Campo 716 e a razao social de QUEM FAZ A AUTOMACAO, nao a da
                        // loja — o nome da loja quem manda e o terminal do PayGo. Trocar
                        // este default por loja_nome muda o que vai para a rede e nao e
                        // reescrita de texto.
                        Empresa: Vendas.Config(cx, "tef_paygo_empresa") ?? "American Day",
                        NomeAutomacao: "Pdv.AmericanDay",
                        VersaoAutomacao: versao,
                        // 738-000: a PayGo entrega na certificação. Vazio no sandbox do kit
                        // é aceito; em produção sem ele a transação é negada — erro de config.
                        Registro: Vendas.Config(cx, "tef_paygo_registro") ?? "",
                        Capacidades: caps,
                        // Pré-seleção de rede (010-000). Vazio = o PayGo abre o menu de redes.
                        RedeCartao: Vendas.Config(cx, "tef_paygo_rede"),
                        RedePix: Vendas.Config(cx, "tef_paygo_rede_pix")))
                {
                    Guardar = t => GuardarTef(t, "paygo"),
                    CnpjDaRede = rede =>
                    {
                        using var c2 = Banco.Abrir();
                        return Vendas.Config(c2, "tef_cnpj_rede_" + rede.Trim().ToLowerInvariant());
                    },
                    // "Transação pendente": só confirma o que ESTE caixa já deu como pago.
                    ConhecidaConfirmada = ctrl =>
                    {
                        using var c3 = Banco.Abrir();
                        return c3.ExecuteScalar<int>("""
                            SELECT COUNT(*) FROM tef_transacao
                             WHERE provedor = 'paygo' AND cod_controle = @C
                               AND (situacao IN ('pago','cnf_sem_ack') OR payment_status = 'cnf_sem_ack')
                            """, new { C = ctrl }) > 0;
                    },
                    // Resposta/ack com 001 que NÓS emitimos em outro processo (qualquer linha paygo:
                    // identificacao ou o 001 embutido no charge_id — a de 'criando' não tem
                    // identificacao) é alheia; 001 desconhecido é aceito como da venda em curso.
                    EhNossaIdentificacao = ident =>
                    {
                        using var c5 = Banco.Abrir();
                        return c5.ExecuteScalar<int>("""
                            SELECT COUNT(*) FROM tef_transacao
                             WHERE identificacao = @I
                                OR charge_id IN ('paygo-' || @I, 'paygo-cnc-' || @I, 'paygo-adm-' || @I)
                            """, new { I = ident }) > 0;
                    },
                    // Trilha do two-phase (a PayGo pede log): CNF/NCN enviados/acusados, órfãs desfeitas.
                    // Comprovante ANTES do CNF: a impressão decide o commit (spec). Falhou e o
                    // operador desistiu → o cliente manda NCN e a venda não é cobrada.
                    ImprimirComprovante = ImprimirComprovantePayGoAsync,
                    Auditar = detalhe =>
                    {
                        try
                        {
                            using var c4 = Banco.Abrir();
                            Caixa.Auditar(c4, null, "tef_paygo", null, null, detalhe);
                        }
                        catch { /* auditoria não derruba cobrança */ }
                    },
                };
                return _tef;
            }

            var nuvem = Nuvem();
            _tef = new ClienteTef(ct => nuvem.TokenAsync(ct), UrlNuvem())
            {
                GarantirSessao = ct => nuvem.SessaoOkAsync(ct),
                SerialPos = Vendas.Config(cx, "tef_serial_pos"),
            };
            return _tef;
        }
    }

    /// <summary>
    /// Zera o cache do TEF: a Configuração acabou de gravar chaves `tef_*` e o provedor tem
    /// que ser reconstruído com elas. Só se chama FORA de venda (a tela de Pagamento captura a
    /// instância no Finalizar e a Configuração só é alcançável do Login) — trocar a instância
    /// no meio de uma cobrança deixaria dois clientes disputando a pasta do PayGo.
    /// </summary>
    public static void RecarregarTef()
    {
        IDisposable? antiga;
        lock (Trava)
        {
            // Comando em voo (ADM/ATV disparado pela própria Configuração, reenvio do boot):
            // trocar a instância agora deixaria DUAS disputando a pasta do PayGo — uma
            // apagaria o .001 que a outra espera. Marca como vencida e troca no próximo Tef()
            // assim que a atual desocupar.
            if (_tef is IProvedorTefOperavel { Ocupado: true }) { _tefVencido = true; return; }
            antiga = _tef as IDisposable;
            _tef = null;
            _tefVencido = false;
        }
        // A instância velha tem recursos vivos (timer do PW_iIdleProc na PGWebLib, HttpClient no
        // ControlPay): sem Dispose, o idle da velha continuaria rodando por cima da nova.
        // Fora do lock: descartar não precisa da trava e não pode segurar quem chama Tef().
        Descartar(antiga);
    }

    /// <summary>Dispose que nunca derruba quem trocou o provedor (o velho já saiu do cache).</summary>
    private static void Descartar(IDisposable? provedor)
    {
        if (provedor is null) return;
        try { provedor.Dispose(); }
        catch { /* provedor velho: o pior caso é o timer morrer com o processo */ }
    }

    /// <summary>
    /// Fechamento do processo (App.OnExit): PW_End na PGWebLib se ela foi usada. Medido em
    /// 07/09/2026: a DLL iniciada e não encerrada aborta o processo no DLL_PROCESS_DETACH
    /// (fail-fast 0xC0000409) e o operador vê "o caixa fechou com erro". PW_End com o processo
    /// de pé leva ~2 s (warsaw) e zera o estado; o detach vira no-op. Nunca derruba o fechamento.
    /// </summary>
    public static void EncerrarTef()
    {
        if (!PGWebLibNativa.Carregada()) return;   // a DLL nunca entrou neste processo: nada a fazer
        IProvedorTef? atual;
        lock (Trava) { atual = _tef; }
        try
        {
            if (atual is ProvedorPGWebLib pg && pg.Encerrar() != ProvedorPGWebLib.Encerramento.NaoIniciada) return;
            // A DLL está no processo, mas o provedor atual não foi quem a iniciou: foi uma
            // instância já trocada pelo RecarregarTef (Testar na Configuração e depois salvar,
            // ou trocar de provedor). PW_End direto: a DLL conta a instância desde a carga, e o
            // PW_End sem PW_iInit encerra essa instância e sai limpo (medido: --so-end, exit 0).
            new PGWebLibNativa().End();
        }
        catch { /* já estamos saindo: sem onde reclamar */ }
    }

    /// <summary>Config mudou com o provedor ocupado: o próximo Tef() com a instância livre reconstrói.</summary>
    private static bool _tefVencido;

    /// <summary>O provedor atual, se for o PayGo por arquivos.</summary>
    public static ClientePayGo? PayGo() => Tef() as ClientePayGo;

    /// <summary>O provedor atual, se for o PayGo pela biblioteca (PGWebLib): Instalar/ADM da Configuração.</summary>
    public static ProvedorPGWebLib? PGWebLib() => Tef() as ProvedorPGWebLib;

    /// <summary>
    /// Um dado que a PGWebLib pede no meio da operação (PWRET_MOREDATA) e a automação não
    /// sabe: menu de redes sem rede gravada, parcelas, senha do lojista, dado livre. Vai para
    /// os diálogos da casa na thread de UI; título, validação e valor devolvido são de
    /// <see cref="RespostaDaTela"/> (puro, testado). Null = o operador cancelou (o provedor
    /// cancela a captura). Roda dentro do provedor, fora da UI.
    /// </summary>
    private static Task<string?> PerguntarNaTelaAsync(PwGetData d, CancellationToken ct)
        => NaUiAsync<string?>(() =>
        {
            if (ct.IsCancellationRequested) return null;
            var dono = JanelaAtiva();
            if (d.EhMenu)
            {
                var textos = RespostaDaTela.Textos(d).ToArray();
                // Até 4 opções cabem lado a lado (o diálogo do POS); mais que isso vai para a
                // lista rolável do menu da venda, uma opção por linha.
                var idx = textos.Length <= 4
                    ? Dialogo.Escolher(dono, RespostaDaTela.Titulo(d), RespostaDaTela.Rotulo(d), textos)
                    : Venda.EscolherOpcao(dono, RespostaDaTela.Titulo(d), RespostaDaTela.Rotulo(d), textos);
                return RespostaDaTela.Menu(d, idx);
            }
            while (true)
            {
                var texto = RespostaDaTela.Ocultar(d)
                    ? PedirSenha.Mostrar(dono, RespostaDaTela.Titulo(d), RespostaDaTela.Rotulo(d))
                    : PedirTexto.Mostrar(dono, RespostaDaTela.Titulo(d), RespostaDaTela.Rotulo(d), RespostaDaTela.Sugestao(d));
                var (valor, erro) = RespostaDaTela.Digitado(d, texto);
                if (erro is null) return valor;
                // Errou: avisa e pergunta de novo. Nunca chuta (parcela adivinhada é cobrança errada).
                Dialogo.Avisar(dono, RespostaDaTela.Titulo(d), erro, "erro");
            }
        });

    /// <summary>
    /// Cancela a cobrança de TEF que está em voo. Quem preenche é a tela de pagamento, que é dona
    /// do CancellationTokenSource da venda; quem chama é a tela do QR, quando o operador aperta Esc
    /// (passo 55 do roteiro). Null fora de uma cobrança.
    /// </summary>
    internal static Action? CancelarTefEmVoo { get; set; }

    private static Action? _fecharExibicaoTef;

    /// <summary>
    /// Abre a tela que a biblioteca mandou mostrar (o QR do Pix ou uma mensagem de checkout) e
    /// devolve na hora. NÃO espera o cliente pagar: se esperasse, a biblioteca nunca saberia que o
    /// QR foi mostrado e a venda morreria de tempo.
    /// </summary>
    private static Task<bool> ExibirNaTelaAsync(ExibicaoTef exibicao, CancellationToken ct)
        => NaUiAsync(() =>
        {
            if (ct.IsCancellationRequested) return false;
            FecharExibicaoNaTela();
            try
            {
                _fecharExibicaoTef = TelaQrTef.Mostrar(JanelaAtiva(), exibicao,
                    () => CancelarTefEmVoo?.Invoke());
                return true;
            }
            catch (Exception ex)
            {
                // Sem tela, o provedor para a venda com uma frase clara. Melhor do que cobrar às
                // cegas um QR que ninguém viu.
                try
                {
                    using var c = Banco.Abrir();
                    Caixa.Auditar(c, null, "tef_pgweblib", null, null, "tela do QR não abriu: " + ex.Message);
                }
                catch { }
                return false;
            }
        });

    private static void FecharExibicaoNaTela()
    {
        var fechar = Interlocked.Exchange(ref _fecharExibicaoTef, null);
        if (fechar is null) return;
        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp is null || disp.CheckAccess()) { try { fechar(); } catch { } return; }
        disp.InvokeAsync(() => { try { fechar(); } catch { } });
    }

    /// <summary>A janela que está na frente (Configuração ou venda), para os diálogos do TEF nascerem em cima dela.</summary>
    private static System.Windows.Window JanelaAtiva()
    {
        var app = System.Windows.Application.Current;
        return app.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
    }

    /// <summary>O provedor atual, se souber estornar/ADM/ativo (PayGo ou ControlPay) — é o que o botão TEF da venda e a Configuração usam.</summary>
    public static IProvedorTefOperavel? Operavel() => Tef() as IProvedorTefOperavel;

    private static ClienteAutorizacao? _autorizador;

    /// <summary>
    /// Quem confere na nuvem o código do autenticador do dono (RPC pdv_autorizacao_totp).
    ///
    /// Vai com o bearer da SESSÃO do terminal (Nuvem.TokenAsync renova sozinho): a
    /// RPC é executável por `authenticated`, e é a sessão que diz de qual loja o
    /// pedido vem. Terminal sem conta na nuvem não estorna, e é assim de propósito.
    ///
    /// O terminal_uuid é o balde do rate limit na nuvem (5 falhas em 10 min).
    ///
    /// Um por processo: o HttpClient de baixo é o do <see cref="Pdv.Nucleo.Fiscal"/>,
    /// único do PDV inteiro. O diagnóstico (uma linha por tentativa, código sempre
    /// mascarado) vai para ProgramData\PdvNativo\autorizacao.txt.
    /// </summary>
    public static ClienteAutorizacao Autorizador()
    {
        lock (Trava)
            // URL FIXA de proposito (revisao 04/09): o veredito do estorno nao pode depender de uma
            // linha editavel do SQLite local (config.supabase_url); quem trocasse a URL por um
            // servidor de mentira teria "ok" sem codigo. O TOTP fala SO com o projeto de producao.
            return _autorizador ??= new ClienteAutorizacao(ct => Nuvem().TokenAsync(ct), Pdv.Nucleo.Nuvem.UrlPadrao,
                terminalUuid: TerminalUuid)
            {
                Diagnostico = linha => AnotarDiagnostico("autorizacao.txt", linha),
            };
    }

    private static string? TerminalUuid()
    {
        try
        {
            using var cx = Banco.Abrir();
            return cx.ExecuteScalar<string?>("SELECT terminal_uuid FROM terminal LIMIT 1");
        }
        catch { return null; }
    }

    /// <summary>Uma linha por evento num arquivo de diagnóstico em ProgramData; passando de 1 MB, recomeça.</summary>
    private static void AnotarDiagnostico(string arquivo, string texto)
    {
        try
        {
            var caminho = System.IO.Path.Combine(Banco.Pasta, arquivo);
            if (System.IO.File.Exists(caminho) && new System.IO.FileInfo(caminho).Length > 1_000_000) System.IO.File.Delete(caminho);
            System.IO.File.AppendAllText(caminho, DateTime.Now.ToString("dd/MM HH:mm:ss") + "  " + texto + Environment.NewLine);
        }
        catch { /* diagnóstico nunca atrapalha a operação */ }
    }

    /// <summary>
    /// Garante a linha original como 'estornada' depois de um CNC aprovado. O cliente já grava
    /// isso no fluxo normal; aqui é cinto e suspensório para a contagem de "restantes" do
    /// estorno (e para o cancelamento feito pelo menu do PayGo, que o cliente não vê). Só mexe
    /// em linha paygo 'pago' — nunca regride outro estado.
    /// </summary>
    public static void MarcarEstornada(string tefId, string motivo)
    {
        try
        {
            using var cx = Banco.Abrir();
            cx.Execute("""
                UPDATE tef_transacao SET situacao = 'estornada', payment_status = 'estornada',
                       motivo = COALESCE(motivo, @M), atualizado_em = @Em
                 WHERE id = @Id AND provedor IN ('paygo','controlpay','pgweblib') AND situacao = 'pago'
                """, new { Id = tefId, M = motivo, Em = DateTime.Now.ToString("o") });
        }
        catch { /* a auditoria do estorno já registrou; não derrubar a tela */ }
    }

    /// <summary>
    /// A operação administrativa `chargeId` (linha `paygo-adm-&lt;id&gt;`) foi um CANCELAMENTO de
    /// venda pelo menu do PayGo? Heurística sobre a resposta guardada: aprovada, com valor e
    /// com NSU original (025) ou operação (730) diferente de venda. Devolve (NSU da venda
    /// original, valor) ou null. A tela usa para cancelar a venda correspondente.
    /// </summary>
    public static (string Nsu, long ValorCent)? CancelamentoNoAdm(string? chargeId)
    {
        if (string.IsNullOrEmpty(chargeId)) return null;
        try
        {
            using var cx = Banco.Abrir();
            var txt = cx.ExecuteScalar<string?>("SELECT resposta_txt FROM tef_transacao WHERE id = @Id", new { Id = chargeId });
            if (txt is null) return null;
            var r = RespostaPayGo.Analisar(txt);
            if (!r.Aprovada || (r.ValorCent ?? 0) <= 0) return null;
            r.Campos.TryGetValue("025-000", out var nsuOriginal);
            r.Campos.TryGetValue("730-000", out var operacao);
            var pareceCancelamento = !string.IsNullOrWhiteSpace(nsuOriginal)
                                     || (!string.IsNullOrWhiteSpace(operacao) && operacao.Trim() != "1" && operacao.Trim() != "01");
            if (!pareceCancelamento) return null;
            var nsu = !string.IsNullOrWhiteSpace(nsuOriginal) ? nsuOriginal.Trim() : r.Nsu;
            return nsu is null ? null : (nsu, r.ValorCent!.Value);
        }
        catch { return null; }
    }

    /// <summary>
    /// Vias a imprimir, na ordem em que saem: cliente, estabelecimento. `737-000` (1 só cliente,
    /// 2 só estabelecimento, 3 ambas) manda; sem as diferenciadas, vale a reduzida ou a única.
    /// </summary>
    /// <summary>
    /// Endereco do agente fiscal local (o Node que tem o certificado A1 e fala
    /// com a SEFAZ). Um lugar so: emissao, guarda e cancelamento leem daqui.
    /// </summary>
    public static string AgenteUrl()
    {
        using var cx = Banco.Abrir();
        return Vendas.Config(cx, "agente_url", "http://127.0.0.1:4610")!;
    }

    /// <summary>
    /// Impressora e bobina da COMANDA do delivery, lidas do <c>config</c> deste caixa.
    ///
    /// Um lugar só porque são TRÊS os caminhos que tiram a mesma comanda no papel (a
    /// automática do sino/timer, o 🖨 do card no KDS e o botão de teste da Configuração)
    /// e eles não podem discordar sobre onde ela sai — comanda de teste que sai numa
    /// térmica e comanda de verdade que sai noutra é pior que não ter teste nenhum.
    /// </summary>
    public static Impressao.Destino DestinoDaComanda(SqliteConnection cx)
        => Impressao.DestinoComanda(
            Vendas.Config(cx, "impressora"), Vendas.Config(cx, "papel_mm"),
            Vendas.Config(cx, "kds_comanda_separada"),
            Vendas.Config(cx, "kds_comanda_impressora"),
            Vendas.Config(cx, "kds_comanda_papel_mm"));

    /// <summary>
    /// Comanda de exemplo para o botão de teste da tela de Configuração. Os itens são
    /// escolhidos para exercitar o que costuma estourar a bobina estreita: descrição
    /// longa, combo com escolhas e observação de cozinha — é onde o corte aparece.
    /// </summary>
    public static Pdv.Nucleo.Ticket ComandaDeExemplo() => new(
        Id: "exemplo", Origem: "ifood", RefId: "exemplo", Numero: "TESTE-1",
        Cliente: "CLIENTE DE TESTE",
        // Passa pelo MESMO ItensDeJson que a sincronização usa e serializa o resultado,
        // que é exatamente o que fica em kds_ticket.itens_json. Escrever o JSON final à
        // mão daria uma comanda de teste que não é a comanda de verdade.
        ItensJson: System.Text.Json.JsonSerializer.Serialize(Pdv.Nucleo.Kds.ItensDeJson("""
            [{"descricao":"COMBO BOX 4 DONUTS SORTIDOS","qtd":1,
              "escolhas":["1x Donut Ninho com Nutella","1x Donut Red Velvet",
                          "1x Donut Chocolate Belga","1x Donut Doce de Leite"]},
             {"descricao":"COOKIE TRIPLO CHOCOLATE COM NOZES","qtd":2,
              "observacao":"sem granulado, embalar separado"}]
            """)),
        Status: Pdv.Nucleo.Kds.Recebido, CriadoEm: DateTime.Now, PreparoEm: null, ProntoEm: null);

    private static readonly SemaphoreSlim UmaComandaPorVez = new(1, 1);

    /// <summary>
    /// Imprime a comanda dos pedidos de delivery que ainda nao sairam no papel.
    ///
    /// Mora AQUI, e nao na tela do KDS, porque a cozinha nao pode depender de
    /// alguem ter deixado o quadro aberto: o pedido chega, a comanda sai. Roda
    /// depois de toda puxada da nuvem (sino e timer), com o caixa na tela de
    /// venda ou no KDS.
    ///
    /// Devolve a mensagem do primeiro erro (para quem quiser avisar na tela) ou
    /// null quando tudo saiu. Nunca lanca: imprimir e conforto, o quadro na tela
    /// continua sendo a fonte de verdade.
    /// </summary>
    public static async Task<string?> ImprimirComandasPendentesAsync()
    {
        if (!await UmaComandaPorVez.WaitAsync(0).ConfigureAwait(false)) return null;
        try
        {
            Impressao.Destino destino; PoliticaImpressao politica;
            using (var cx = Banco.Abrir())
            {
                politica = Impressoes.Politica(cx, Impressoes.Comanda);
                destino = DestinoDaComanda(cx);
            }
            // Só "imprimir sozinho" tira papel aqui. Em "perguntar" quem tira é o 🖨 do
            // card na tela Delivery, e em "não imprimir" nada sai e o 🖨 some.
            if (politica != PoliticaImpressao.Automatico) return null;

            string? falha = null;
            foreach (var t in Pdv.Nucleo.Kds.ParaImprimir())
            {
                // claim ANTES do papel: sino + timer se sobrepoem, e comanda dupla
                // e donut duplo. Falhou depois do claim -> o botao imprimir do card
                // reimprime (impressora morta nao pode virar metralhadora).
                if (!Pdv.Nucleo.Kds.ReivindicarImpressao(t.Id)) continue;
                var erro = await Impressao.ImprimirTextoAsync(
                    $"Comanda cozinha #{t.Numero}",
                    new[] { Pdv.Nucleo.Kds.ComandaLinhas(t, Pdv.Nucleo.Kds.ColunasComanda(destino.Papel.Colunas)) },
                    destino).ConfigureAwait(false);
                // Mesma frase que o botao de reimprimir da tela Delivery mostra: e o
                // mesmo papel que nao saiu, entao nao pode ter dois textos diferentes.
                falha ??= erro is null ? null : $"A comanda do #{t.Numero} não saiu";
            }
            return falha;
        }
        catch { return null; }
        finally { UmaComandaPorVez.Release(); }
    }

    /// <summary>
    /// De quem é a via que saiu da rede. <see cref="Unica"/> é o bloco que sobrou sem a rede
    /// dizer de quem ele é (via única 029, ou o cupom reduzido num 737 que só pediu a via da
    /// loja): o papel que sobra é o que vai para a MÃO DO CLIENTE.
    /// </summary>
    public enum ViaTef { Cliente, Estabelecimento, Unica }

    /// <summary>
    /// As vias com dono, na ordem em que saem. É daqui que a política por via consegue
    /// separar o que a rede mandou junto — antes disto existia só a lista sem rótulo, e
    /// "imprimir só a do cliente" não tinha como ser dito.
    /// </summary>
    public static IReadOnlyList<(ViaTef Qual, IReadOnlyList<string> Linhas)> ViasRotuladas(RespostaPayGo r)
    {
        var vias = r.Vias ?? 3;
        var b = new List<(ViaTef, IReadOnlyList<string>)>();
        if (vias == 0) return b;   // 737 = 0: "não há comprovante" — nada sai, e o CNF não depende de papel
        // O cupom REDUZIDO (711) é o papel do portador do cartão. Quando a rede manda a via
        // diferenciada dele (713), o reduzido é a mesma compra em papel menor e não vira uma
        // terceira folha. Quando não manda, o reduzido É a via do cliente: o C6PAY faz assim,
        // reduzido para o portador e diferenciado para o lojista, e sem esta linha a via da
        // loja preenchia a lista sozinha e o cliente saía do balcão de mão vazia.
        var doCliente = r.ViaCliente.Count > 0 ? r.ViaCliente : r.CupomReduzido;
        if (vias != 2 && doCliente.Count > 0) b.Add((ViaTef.Cliente, doCliente));
        if (vias != 1 && r.ViaEstabelecimento.Count > 0) b.Add((ViaTef.Estabelecimento, r.ViaEstabelecimento));
        if (b.Count == 0 && r.CupomReduzido.Count > 0) b.Add((ViaTef.Unica, r.CupomReduzido));
        if (b.Count == 0 && r.ViaUnica.Count > 0) b.Add((ViaTef.Unica, r.ViaUnica));
        return b;
    }

    /// <summary>
    /// A lista sem rótulo, como sempre foi. Continua sendo o que a reimpressão manual usa
    /// (Venda → TEF → Reimprimir), onde quem escolhe a via é o operador, não a política.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ViasParaImprimir(RespostaPayGo r)
        => ViasRotuladas(r).Select(v => v.Linhas).ToList();

    /// <summary>
    /// As vias que saem SOZINHAS, filtradas pela política de cada uma.
    ///
    /// "Perguntar" e "não imprimir" tiram a via daqui: a diferença entre as duas é o que
    /// a tela oferece depois (TEF → Reimprimir o último comprovante continua à mão nas
    /// duas — reimpressão é dedo humano, não automação).
    ///
    /// ⚠️ A via ÚNICA (sem 713 nem 715) obedece à política da via do CLIENTE: é o papel
    /// que o cliente leva, e chutar "estabelecimento" tiraria o comprovante da mão de
    /// quem pagou.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ViasAutomaticas(
        RespostaPayGo r, PoliticaImpressao cliente, PoliticaImpressao estabelecimento)
        => ViasRotuladas(r)
            .Where(v => (v.Qual == ViaTef.Estabelecimento ? estabelecimento : cliente) == PoliticaImpressao.Automatico)
            .Select(v => v.Linhas)
            .ToList();

    /// <summary>
    /// As vias que a loja marcou como "Perguntar na tela" (imp_via_cliente /
    /// imp_via_estabelecimento). Elas NÃO saem sozinhas: a tela de pagamento oferece um
    /// pop-up logo após a aprovação, com o cliente ainda no balcão. Volta ROTULADA para o
    /// pop-up saber o nome de cada via. A via única obedece à política da via do CLIENTE,
    /// a mesma regra de <see cref="ViasAutomaticas"/> — é o papel que o cliente leva.
    /// </summary>
    public static IReadOnlyList<(ViaTef Qual, IReadOnlyList<string> Linhas)> ViasPerguntarRotuladas(
        RespostaPayGo r, PoliticaImpressao cliente, PoliticaImpressao estabelecimento)
        => ViasRotuladas(r)
            .Where(v => (v.Qual == ViaTef.Estabelecimento ? estabelecimento : cliente) == PoliticaImpressao.Perguntar)
            .ToList();

    /// <summary>
    /// Hook do two-phase do PayGo: imprime as vias do comprovante e diz se SAÍRAM. Falhou →
    /// pergunta ao operador se tenta de novo; "desistir" devolve false e o cliente manda NCN
    /// (o cliente não é cobrado; a tela mostra "Transação TEF cancelada: Rede/NSU/Valor").
    /// Cada via tem política própria (`imp_via_cliente` / `imp_via_estabelecimento`): fora de
    /// "imprimir sozinho" a via não entra na lista e, sem lista, devolve true — nada pode
    /// falhar. Roda FORA da thread de UI (dentro do cliente) — diálogo via Dispatcher.
    /// </summary>
    private static async Task<bool> ImprimirComprovantePayGoAsync(TransacaoPayGo t)
    {
        PoliticaImpressao pCliente, pEstabelecimento;
        string? impressora;
        using (var cx = Banco.Abrir())
        {
            pCliente = Impressoes.Politica(cx, Impressoes.ViaCliente);
            pEstabelecimento = Impressoes.Politica(cx, Impressoes.ViaEstabelecimento);
            impressora = Vendas.Config(cx, "impressora");
        }
        if (t.Resposta is null) return true;
        // ⚠️ NO TWO-PHASE O PAPEL DECIDE O CNF. Por isso "perguntar" NÃO segura a
        // transação esperando dedo humano: ele sai daqui como "nada a imprimir" (devolve
        // true, o CNF sai) e a via fica em TEF → Reimprimir o último comprovante. Prender
        // o CNF num botão viraria transação rasgada ou NCN indevido com o cliente na
        // frente. Mesma conta de quando `tef_paygo_imprimir_vias` era 0.
        var blocos = ViasAutomaticas(t.Resposta, pCliente, pEstabelecimento);
        if (blocos.Count == 0) return true;

        var descricao = $"Comprovante TEF{(t.EhCancelamento ? " (cancelamento)" : "")} {t.Resposta.Nsu ?? t.Identificacao}";
        // 729 = 2: a impressão DECIDE (desistir = NCN, cliente não cobrado). 729 = 1: a rede já
        // efetivou — não existe NCN; o diálogo não pode prometer "desfaz": só reimprime ou deixa
        // para o menu TEF → Reimprimir. Mentir aqui é o operador dizendo ao cliente que foi
        // desfeito quando foi cobrado.
        var decide = t.Resposta.RequerConfirmacao;
        var impressos = 0;
        var tentativas = 0;
        while (true)
        {
            // Sem two-phase (ControlPay/729=1) a impressão é acessória e roda FORA da venda:
            // insistir com diálogo aqui só empilharia janelas sobre o caixa. Uma tentativa,
            // um aviso, e o operador reimprime quando quiser (TEF → Reimprimir).
            if (!decide && ++tentativas > 1) return false;
            // `decide` = PayGo two-phase: sem o papel não há CNF, então a via tem a mesma
            // urgência da venda. No ControlPay a transação já está efetivada e ninguém está
            // esperando esta via — ela cede a vez para o cupom, que é o papel que o cliente
            // tem na mão. Mesma impressora, ordem que faz sentido para quem está no balcão.
            var (erro, saiu) = await Impressao.ImprimirBlocosAsync(descricao, blocos, impressora, impressos,
                decide ? Impressao.PrioridadeImpressao.Alta : Impressao.PrioridadeImpressao.Baixa).ConfigureAwait(false);
            impressos += saiu;   // retentativa continua da via que NÃO saiu (não duplica a via do cliente)
            if (erro is null) return true;
            if (!decide)
            {
                // Aviso único, sem prender ninguém: a venda já seguiu.
                await NaUiAsync<object?>(() =>
                {
                    Dialogo.Avisar(System.Windows.Application.Current.MainWindow, "Comprovante não saiu",
                        $"{erro}\n\nA transação está efetivada: a venda seguiu normalmente. " +
                        "Reimprima em TEF → Reimprimir o último comprovante quando a impressora estiver pronta.", "erro");
                    return null;
                }).ConfigureAwait(false);
                return false;
            }
            var tentar = await NaUiAsync(() => Dialogo.Confirmar(
                System.Windows.Application.Current.MainWindow, "Comprovante não saiu",
                decide
                    ? $"{erro}\n\nSem o comprovante impresso a transação TEF NÃO é confirmada. Tentar imprimir de novo?"
                    : $"{erro}\n\nA transação JÁ está efetivada na rede (não dá para desfazer por aqui). Tentar imprimir de novo? " +
                      "Se desistir, reimprima depois em TEF → Reimprimir o último comprovante.",
                "Tentar de novo",
                decide ? "Desistir (desfaz a transação)" : "Deixar para depois",
                perigo: decide)).ConfigureAwait(false);
            if (!tentar) return false;
        }
    }

    /// <summary>Roda na thread de UI (diálogo modal) a partir de qualquer thread; na própria UI, executa direto.</summary>
    private static Task<T> NaUiAsync<T>(Func<T> acao)
    {
        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp is null || disp.CheckAccess()) return Task.FromResult(acao());
        return disp.InvokeAsync(acao).Task;
    }

    /// <summary>
    /// A "memória não volátil" do PayGo: grava/atualiza a linha de `tef_transacao` ANTES do
    /// CNF. False = o cliente desfaz a transação (NCN). Upsert por `id = charge_id`, a mesma
    /// chave que a tela de pagamento usa — as duas escritas convergem na mesma linha.
    /// </summary>
    /// <summary>
    /// Uma linha por cobrança em ProgramData\PdvNativo\tef-tempos.txt, para responder "por que o
    /// PayGo demora a aparecer" com número em vez de impressão. `criar` é a espera até a janela do
    /// PayGo abrir; `pinpad` é o cliente no cartão. Nunca derruba a cobrança e não guarda segredo:
    /// só tipo, valor, tempos e o id da intenção.
    /// </summary>
    private static void AnotarTempoTef(string detalhe)
    {
        try
        {
            var caminho = System.IO.Path.Combine(Banco.Pasta, "tef-tempos.txt");
            // O arquivo é para diagnóstico, não para histórico: passando de 1 MB, recomeça.
            if (System.IO.File.Exists(caminho) && new System.IO.FileInfo(caminho).Length > 1_000_000) System.IO.File.Delete(caminho);
            System.IO.File.AppendAllText(caminho, DateTime.Now.ToString("dd/MM HH:mm:ss") + "  " +
                                        detalhe.Replace("controlpay: tempo ", "") + Environment.NewLine);
        }
        catch { /* diagnóstico nunca atrapalha a venda */ }
    }

    private static bool GuardarPayGo(TransacaoPayGo t) => GuardarTef(t, "paygo");

    /// <summary>Mesma linha para PayGo e ControlPay — só o `provedor` muda (e o charge_id já diz quem é: paygo-/cpay-).</summary>
    private static bool GuardarTef(TransacaoPayGo t, string provedor)
    {
        try
        {
            var r = t.Resposta;
            using var cx = Banco.Abrir();
            cx.Execute("""
                INSERT INTO tef_transacao (id, charge_id, payment_identifier, tipo, valor_cent, parcelas, situacao,
                                           payment_status, motivo, aut, cnpj_cred, bandeira, tband, nsu, terminal, provedor,
                                           identificacao, cod_controle, rede, data_tef, hora_tef, vias_json,
                                           resposta_txt, criado_em, atualizado_em)
                VALUES (@Id,@Id,@Ident,@Tipo,@V,@P,@S,@S,@M,@Aut,@Cnpj,@Band,@Tb,@Nsu,@Term,@Prov,
                        @Ident,@Ctrl,@Rede,@Dt,@Hr,@Vias,@Txt,@Em,@Em)
                ON CONFLICT(id) DO UPDATE SET
                    situacao      = excluded.situacao,
                    -- payment_status acompanha a situação: é por ele que o boot acha 'cnf_sem_ack'
                    -- (a tela grava o dela no fim); sem isto o CNF reenviado casaria para sempre.
                    payment_status = excluded.payment_status,
                    motivo        = COALESCE(excluded.motivo, motivo),
                    aut           = COALESCE(excluded.aut, aut),
                    cnpj_cred     = COALESCE(excluded.cnpj_cred, cnpj_cred),
                    bandeira      = COALESCE(excluded.bandeira, bandeira),
                    tband         = COALESCE(excluded.tband, tband),
                    nsu           = COALESCE(excluded.nsu, nsu),
                    terminal      = COALESCE(excluded.terminal, terminal),
                    provedor      = @Prov,
                    identificacao = excluded.identificacao,
                    cod_controle  = COALESCE(excluded.cod_controle, cod_controle),
                    rede          = COALESCE(excluded.rede, rede),
                    data_tef      = COALESCE(excluded.data_tef, data_tef),
                    hora_tef      = COALESCE(excluded.hora_tef, hora_tef),
                    vias_json     = COALESCE(excluded.vias_json, vias_json),
                    resposta_txt  = COALESCE(excluded.resposta_txt, resposta_txt),
                    atualizado_em = excluded.atualizado_em
                """,
                new
                {
                    Id = t.ChargeId, Ident = t.Identificacao, Tipo = t.Tipo.Codigo(), V = t.ValorCent, Prov = provedor,
                    P = t.Parcelas, S = t.Situacao, M = t.Motivo,
                    Aut = r?.Autorizacao,
                    // mesma regra do <card> da NFC-e: config sobrepõe a tabela conhecida
                    Cnpj = r?.Rede is { } rd
                        ? (Vendas.Config(cx, "tef_cnpj_rede_" + rd.Trim().ToLowerInvariant()) ?? ClientePayGo.CnpjConhecido(rd))
                        : null,
                    Band = r?.NomeCartao ?? r?.Produto, Tb = r is null ? null : ClientePayGo.TBand(r.Produto ?? r.NomeCartao),
                    Nsu = r?.Nsu, Term = r?.Terminal, Ctrl = r?.CodigoControle, Rede = r?.Rede,
                    Dt = r?.Data, Hr = r?.Hora, Vias = r?.ViasJson(), Txt = r?.Texto,
                    Em = DateTime.Now.ToString("o"),
                });
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Religamento do PayGo: transações aprovadas que ficaram sem CNF/NCN, e resposta órfã na
    /// pasta. Venda gravada (`venda_id`) → CNF; sem venda → NCN; CNF sem ack → reenvia.
    /// Devolve quantas resolveu; 0 quando o provedor não é PayGo. O PayGo pela biblioteca
    /// (PGWebLib) segue a MESMA varredura: mesmas situações, mesmo two-phase, só muda o
    /// provedor da linha e o prefixo do charge_id (pgweb-).
    /// </summary>
    public static async Task<int> ResolverPendenciasTefAsync()
    {
        if (Tef() is ClienteControlPay cpay) return await ReconciliarControlPayAsync(cpay);
        if (Tef() is ProvedorPGWebLib pg) return await ResolverPendenciasDuasFasesAsync("pgweblib", "pgweb-%", pg.ResolverPendenciasAsync);
        if (Tef() is not ClientePayGo cli) return 0;
        return await ResolverPendenciasDuasFasesAsync("paygo", "paygo-%", cli.ResolverPendenciasAsync);
    }

    private static async Task<int> ResolverPendenciasDuasFasesAsync(string provedor, string prefixoLike,
        Func<IReadOnlyList<(TransacaoPayGo Tx, bool VendaConcluida)>, Task<int>> resolver)
    {
        var pendentes = new List<(TransacaoPayGo, bool)>();
        using (var cx = Banco.Abrir())
        {
            var linhas = cx.Query("""
                SELECT id, identificacao, tipo, valor_cent, parcelas, situacao, payment_status, venda_id, resposta_txt
                  FROM tef_transacao
                 WHERE provedor = @Prov
                   AND (situacao IN ('aprovada','cnf_sem_ack','ncn_sem_ack') OR payment_status IN ('cnf_sem_ack','ncn_sem_ack'))
                """, new { Prov = provedor }).ToList();
            foreach (var l in linhas)
            {
                var tipo = TipoTefExtensoes.Analisar((string?)l.tipo) ?? TipoTef.Credito;
                // payment_status manda quando diz 'sem ack' — seja qual for a situacao (a tela
                // pode ter escrito 'cancelado'/'erro' por cima antes da guarda de provedor).
                var ps = (string?)l.payment_status;
                var situacao = ps is "cnf_sem_ack" or "ncn_sem_ack" ? ps! : (string)l.situacao;
                var tx = new TransacaoPayGo((string)l.id, (string?)l.identificacao ?? "", tipo,
                    (long)l.valor_cent, (int)(long)l.parcelas, situacao,
                    RespostaPayGo.Analisar((string?)l.resposta_txt));
                pendentes.Add((tx, l.venda_id is string v && v.Length > 0));
            }
        }
        var n = await resolver(pendentes);

        // Cobrança que morreu ANTES da resposta (roteiro P24/25): a linha ficou 'criando'/
        // 'aguardando' e nunca mais muda sozinha. No boot ninguém está cobrando: vira órfã
        // (se a resposta dela estava na pasta, a varredura acima já a encerrou pela identificação).
        using (var cx = Banco.Abrir())
        {
            // criado_em < início do processo: a 1ª venda pós-boot pode já estar em 'criando'
            // enquanto esta varredura roda — essa é VIVA, não órfã.
            var inicio = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToString("o");
            cx.Execute("""
                UPDATE tef_transacao
                   SET situacao = 'orfa', motivo = 'PDV reiniciou durante a cobrança; confira no PayGo', atualizado_em = @Em
                 WHERE charge_id LIKE @Prefixo AND situacao IN ('criando','aguardando') AND criado_em < @Inicio
                """, new { Em = DateTime.Now.ToString("o"), Inicio = inicio, Prefixo = prefixoLike });
        }
        return n;
    }

    /// <summary>
    /// Religamento do ControlPay: linhas 'aguardando'/'orfa' (PDV morreu com a intenção em
    /// pagamento) são consultadas na API e fechadas pelo status real. Aprovada sem venda vira
    /// 'pago' marcado como órfã — o fechamento acusa e o estorno sai pelo menu TEF.
    /// </summary>
    private static async Task<int> ReconciliarControlPayAsync(ClienteControlPay cli)
    {
        var pendentes = new List<TransacaoPayGo>();
        var criadaEm = new Dictionary<string, DateTime>();
        using (var cx = Banco.Abrir())
        {
            var linhas = cx.Query("""
                SELECT id, identificacao, tipo, valor_cent, parcelas, situacao, resposta_txt, criado_em
                  FROM tef_transacao
                 WHERE provedor = 'controlpay' AND situacao IN ('aguardando','orfa','aprovada')
                   AND identificacao IS NOT NULL AND identificacao <> ''
                """).ToList();
            foreach (var l in linhas)
            {
                var tipo = TipoTefExtensoes.Analisar((string?)l.tipo) ?? TipoTef.Credito;
                pendentes.Add(new TransacaoPayGo((string)l.id, (string)l.identificacao, tipo,
                    (long)l.valor_cent, (int)(long)l.parcelas, (string)l.situacao,
                    RespostaPayGo.Analisar((string?)l.resposta_txt)));
                // Nascimento da linha: é o que diz se a intenção sem status final ficou para trás
                // (vira órfã) ou está sendo cobrada agora (não se encosta).
                if (DateTime.TryParse((string?)l.criado_em, System.Globalization.CultureInfo.InvariantCulture,
                                      System.Globalization.DateTimeStyles.RoundtripKind, out DateTime em))
                    criadaEm[(string)l.id] = em.Kind == DateTimeKind.Utc ? em.ToLocalTime() : em;
            }
            // 'criando' (morreu antes de a API responder) não tem intenção para consultar: órfã.
            var inicio = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToString("o");
            cx.Execute("""
                UPDATE tef_transacao
                   SET situacao = 'orfa', motivo = 'PDV reiniciou antes de o ControlPay responder', atualizado_em = @Em
                 WHERE charge_id LIKE 'cpay-%' AND situacao = 'criando' AND criado_em < @Inicio
                """, new { Em = DateTime.Now.ToString("o"), Inicio = inicio });
        }
        return pendentes.Count == 0 ? 0 : await cli.ReconciliarAsync(pendentes, criadaEm);
    }

    /// <summary>
    /// URL do Supabase (auth, edge functions, RPC).
    ///
    /// ⚠️ NÃO é o `terminal.api_base`. Aquele campo guarda o endereço do servidor fiscal
    /// na AWS (hoje `http://54.232.6.39`), que é outra coisa — apontar a autenticação
    /// para lá faz todo login falhar com "sem sessão", que foi exatamente o que
    /// aconteceu na primeira emissão de teste.
    /// </summary>
    private static string UrlNuvem()
    {
        using var cx = Banco.Abrir();
        var url = Vendas.Config(cx, "supabase_url");
        return string.IsNullOrWhiteSpace(url) ? Pdv.Nucleo.Nuvem.UrlPadrao : url!.TrimEnd('/');
    }
}
