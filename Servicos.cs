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
    private static IEmissorFiscal? _emissor;
    private static ClienteTef? _tef;

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

    /// <summary>TEF; null quando o caixa ainda não tem maquininha ligada.</summary>
    public static ClienteTef? Tef()
    {
        lock (Trava)
        {
            if (_tef is not null) return _tef;
            using var cx = Banco.Abrir();
            if (Vendas.Config(cx, "tef_habilitado") != "1") return null;
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
