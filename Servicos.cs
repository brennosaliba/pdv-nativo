using Dapper;
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

    /// <summary>De onde a nota está saindo — vai no rodapé, porque muda a série da nota.</summary>
    public static string CaminhoDoEmissor() => Emissor() switch
    {
        EmissorResolvido r => r.CaminhoAtual,
        EmissorAgente => "agente",
        _ => "nuvem",
    };

    /// <summary>
    /// TEF; null quando o caixa ainda não tem maquininha ligada.
    /// `config['tef_provedor']`: `paygo` = PayGo Windows por troca de arquivos (local, offline);
    /// qualquer outra coisa = Smart TEF pela nuvem (o caminho antigo).
    /// </summary>
    public static IProvedorTef? Tef()
    {
        lock (Trava)
        {
            if (_tef is not null) return _tef;
            using var cx = Banco.Abrir();
            if (Vendas.Config(cx, "tef_habilitado") != "1") return null;

            if (Vendas.Config(cx, "tef_provedor") == "paygo")
            {
                var versao = typeof(Servicos).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
                var caps = int.TryParse(Vendas.Config(cx, "tef_paygo_capacidades"), out var c)
                    ? c : ClientePayGo.CapacidadesPadrao;
                _tef = new ClientePayGo(
                    Vendas.Config(cx, "tef_paygo_pasta") ?? ClientePayGo.PastaPadrao,
                    new OpcoesPayGo(
                        Empresa: Vendas.Config(cx, "tef_paygo_empresa") ?? "American Day",
                        NomeAutomacao: "PdvNativo",
                        VersaoAutomacao: versao,
                        // 738-000: a PayGo entrega na certificação. Vazio no sandbox do kit
                        // é aceito; em produção sem ele a transação é negada — erro de config.
                        Registro: Vendas.Config(cx, "tef_paygo_registro") ?? "",
                        Capacidades: caps,
                        // Pré-seleção de rede (010-000). Vazio = o PayGo abre o menu de redes.
                        RedeCartao: Vendas.Config(cx, "tef_paygo_rede"),
                        RedePix: Vendas.Config(cx, "tef_paygo_rede_pix")))
                {
                    Guardar = GuardarPayGo,
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
                    // Trilha do two-phase (a PayGo pede log): CNF/NCN enviados/acusados, órfãs desfeitas.
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
    /// A "memória não volátil" do PayGo: grava/atualiza a linha de `tef_transacao` ANTES do
    /// CNF. False = o cliente desfaz a transação (NCN). Upsert por `id = charge_id`, a mesma
    /// chave que a tela de pagamento usa — as duas escritas convergem na mesma linha.
    /// </summary>
    private static bool GuardarPayGo(TransacaoPayGo t)
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
                VALUES (@Id,@Id,@Ident,@Tipo,@V,@P,@S,@S,@M,@Aut,@Cnpj,@Band,@Tb,@Nsu,@Term,'paygo',
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
                    provedor      = 'paygo',
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
                    Id = t.ChargeId, Ident = t.Identificacao, Tipo = t.Tipo.Codigo(), V = t.ValorCent,
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
    /// Devolve quantas resolveu; 0 quando o provedor não é PayGo.
    /// </summary>
    public static async Task<int> ResolverPendenciasTefAsync()
    {
        if (Tef() is not ClientePayGo cli) return 0;

        var pendentes = new List<(TransacaoPayGo, bool)>();
        using (var cx = Banco.Abrir())
        {
            var linhas = cx.Query("""
                SELECT id, identificacao, tipo, valor_cent, parcelas, situacao, payment_status, venda_id, resposta_txt
                  FROM tef_transacao
                 WHERE provedor = 'paygo'
                   AND (situacao IN ('aprovada','cnf_sem_ack','ncn_sem_ack') OR payment_status IN ('cnf_sem_ack','ncn_sem_ack'))
                """).ToList();
            foreach (var l in linhas)
            {
                var tipo = TipoTefExtensoes.Analisar((string?)l.tipo) ?? TipoTef.Credito;
                var ps = (string?)l.payment_status;
                var situacao = ps is "cnf_sem_ack" or "ncn_sem_ack" && (string)l.situacao is "pago" or "estornado" or "desfeita"
                    ? ps! : (string)l.situacao;
                var tx = new TransacaoPayGo((string)l.id, (string?)l.identificacao ?? "", tipo,
                    (long)l.valor_cent, (int)(long)l.parcelas, situacao,
                    RespostaPayGo.Analisar((string?)l.resposta_txt));
                pendentes.Add((tx, l.venda_id is string v && v.Length > 0));
            }
        }
        var n = await cli.ResolverPendenciasAsync(pendentes);

        // Cobrança que morreu ANTES da resposta (roteiro P24/25): a linha ficou 'criando'/
        // 'aguardando' e nunca mais muda sozinha. No boot ninguém está cobrando: vira órfã
        // (se a resposta dela estava na pasta, a varredura acima já a encerrou pela identificação).
        using (var cx = Banco.Abrir())
        {
            cx.Execute("""
                UPDATE tef_transacao
                   SET situacao = 'orfa', motivo = 'PDV reiniciou durante a cobrança; confira no PayGo', atualizado_em = @Em
                 WHERE charge_id LIKE 'paygo-%' AND situacao IN ('criando','aguardando')
                """, new { Em = DateTime.Now.ToString("o") });
        }
        return n;
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
