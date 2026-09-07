// SelecaoTef.cs: a ligação dos provedores de TEF na casa, sem WPF e sem banco:
//   · SelecaoTef: qual provedor a config pede (tef_habilitado + tef_provedor) e os códigos/modos
//     que Servicos.Tef() e a Configuração compartilham (uma regra só, testada uma vez);
//   · ConfigPGWebLib: as chaves da PGWebLib (tef_pgweb_*) e as reaproveitadas do PayGo por
//     arquivos (tef_paygo_rede/rede_pix/empresa) viram OpcoesPGWebLib;
//   · RespostaDaTela: o tradutor entre o PwGetData que a biblioteca pede (menu, dado digitado,
//     senha) e os diálogos da casa (Dialogo.Escolher, PedirTexto, PedirSenha). A tela só
//     chama o diálogo; quem decide título, validação e valor devolvido é este arquivo;
//   · FiltroRedes: a lista de redes que a loja deixa aparecer no menu de seleção da rede
//     (tef_pgweb_redes) e como ela encurta esse menu sem nunca esvaziá-lo.
namespace Pdv.Nucleo;

/// <summary>Os provedores que `tef_provedor` pode nomear. <see cref="Nenhum"/> = tef_habilitado != 1.</summary>
public enum ProvedorTef { Nenhum, Nuvem, PayGo, ControlPay, PGWebLib }

public static class SelecaoTef
{
    /// <summary>Os provedores que gravam linha própria em tef_transacao e passam pelo two-phase (estorno, reimpressão, guarda da situação).</summary>
    public static readonly string[] Integrados = { "paygo", "controlpay", "pgweblib" };

    /// <summary>Fragmento para `provedor IN (...)` nas consultas da casa. Provedor novo entra aqui e nos SQL de uma vez.</summary>
    public const string SqlIntegrados = "('paygo','controlpay','pgweblib')";

    public static bool EhIntegrado(string? provedor)
        => provedor is not null && Array.IndexOf(Integrados, provedor.Trim().ToLowerInvariant()) >= 0;

    /// <summary>A regra de Servicos.Tef(): desligado = Nenhum; senão pelo código, e qualquer outro valor é a nuvem (o caminho antigo).</summary>
    public static ProvedorTef Escolher(string? habilitado, string? provedor)
    {
        if (habilitado?.Trim() != "1") return ProvedorTef.Nenhum;
        return (provedor ?? "").Trim().ToLowerInvariant() switch
        {
            "paygo" => ProvedorTef.PayGo,
            "controlpay" => ProvedorTef.ControlPay,
            "pgweblib" => ProvedorTef.PGWebLib,
            _ => ProvedorTef.Nuvem,
        };
    }

    /// <summary>O que a Configuração grava em `tef_provedor`. Nenhum grava 'nuvem' (e tef_habilitado = 0), como sempre gravou.</summary>
    public static string Codigo(ProvedorTef p) => p switch
    {
        ProvedorTef.PayGo => "paygo",
        ProvedorTef.ControlPay => "controlpay",
        ProvedorTef.PGWebLib => "pgweblib",
        _ => "nuvem",
    };

    /// <summary>O número do cartão da Configuração (0 sem · 1 nuvem · 2 PayGo arquivos · 3 ControlPay · 4 PayGo biblioteca).</summary>
    public static int Modo(ProvedorTef p) => p switch
    {
        ProvedorTef.Nuvem => 1,
        ProvedorTef.PayGo => 2,
        ProvedorTef.ControlPay => 3,
        ProvedorTef.PGWebLib => 4,
        _ => 0,
    };

    public static ProvedorTef DeModo(int modo) => modo switch
    {
        1 => ProvedorTef.Nuvem,
        2 => ProvedorTef.PayGo,
        3 => ProvedorTef.ControlPay,
        4 => ProvedorTef.PGWebLib,
        _ => ProvedorTef.Nenhum,
    };
}

/// <summary>As chaves de `config` da PGWebLib e como viram <see cref="OpcoesPGWebLib"/>.</summary>
public static class ConfigPGWebLib
{
    public const string ChaveDir = "tef_pgweb_dir";
    public const string ChavePortaPinpad = "tef_pgweb_porta_pinpad";
    public const string ChaveCapacidades = "tef_pgweb_capacidades";
    /// <summary>
    /// Pasta onde o PayGo Windows deixou a PGWebLib.dll (64 bits, o Pdv.exe é x64). Em branco
    /// o Windows procura na pasta do exe e no PATH. A DLL não é nossa: vem com o instalador
    /// do PayGo Windows, e o caminho muda por versão; por isso é config e não constante.
    /// </summary>
    public const string ChaveDll = "tef_pgweb_dll";

    /// <summary>
    /// `producao` (padrão) ou `homologacao`. Vira PW_iSetEnvironment. Antes, quem escolhia o
    /// ambiente era o instalador do PayGo Windows; no kit avulso da biblioteca a mesma DLL
    /// atende os dois, e a escolha é nossa. Em branco vale produção, que é o padrão da própria
    /// biblioteca: assim ninguém entra em homologação sem pedir.
    /// </summary>
    public const string ChaveAmbiente = "tef_pgweb_ambiente";

    /// <summary>
    /// `1` liga o QR do Pix na TELA DO CAIXA. Sem isso a biblioteca continua mandando o cliente ler
    /// no pinpad, que e o comportamento de hoje nas lojas.
    ///
    /// Fica desligado por padrao de proposito: ligar muda o fluxo de Pix de todo mundo, e o
    /// roteiro de homologacao (passo 55) e quem exige a tela. A maquina de homologacao liga; as
    /// lojas so depois de o dono ver funcionando.
    /// </summary>
    public const string ChaveQrNaTela = "tef_pgweb_qr_na_tela";

    /// <summary>
    /// As redes que a loja deixa aparecer no menu de seleção da rede, separadas por vírgula.
    /// Em branco (o padrão) o menu mostra tudo que a biblioteca listar, que é o certo numa loja
    /// de verdade: quem sabe o que está credenciado no terminal é o PayGo, não o caixa.
    ///
    /// NÃO é a mesma coisa que `tef_paygo_rede`. Aquela FIXA a rede e o menu deixa de aparecer;
    /// esta só encurta a lista, e o menu continua aparecendo mesmo com uma opção só. A diferença
    /// é o passo 05 do roteiro: ele manda apertar Esc no menu de seleção da rede, e sem menu não
    /// há onde apertar.
    /// </summary>
    public const string ChaveRedes = "tef_pgweb_redes";

    /// <summary>Diretório de trabalho da biblioteca (PW_iInit). Fora de C:\PAYGO de propósito: é nosso, não do PayGo Windows.</summary>
    public const string DirPadrao = @"C:\ProgramData\PdvNativo\pgweb";

    /// <summary>
    /// O que gravar em <see cref="ChaveAmbiente"/> para cada ambiente, e como ler de volta.
    /// Aceita as grafias que uma pessoa digitaria, com e sem acento.
    /// </summary>
    public const string AmbienteProducao = "producao";
    public const string AmbienteHomologacao = "homologacao";

    /// <summary>ENVRMNT_TEST só quando a config pede homologação; qualquer outra coisa é produção.</summary>
    public static short Ambiente(Func<string, string?> config)
    {
        var v = config(ChaveAmbiente)?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(v)) return PW.ENVRMNT_PROD;
        return v is "homologacao" or "homologação" or "homolog" or "teste" or "test" or "sandbox"
            ? PW.ENVRMNT_TEST
            : PW.ENVRMNT_PROD;
    }

    /// <summary>Uma linha para a tela dizer em que ambiente o caixa está.</summary>
    public static string RotuloAmbiente(short ambiente)
        => ambiente == PW.ENVRMNT_TEST ? "Homologação" : "Produção";

    /// <summary>A loja pediu para desenhar o QR do Pix na tela do caixa?</summary>
    public static bool QrNaTela(Func<string, string?> config)
        => (config(ChaveQrNaTela)?.Trim() ?? "") == "1";

    /// <summary>As redes de <see cref="ChaveRedes"/> já quebradas. Vazia = o menu mostra todas.</summary>
    public static IReadOnlyList<string> Redes(Func<string, string?> config) => FiltroRedes.Ler(config(ChaveRedes));

    /// <summary>
    /// As capacidades que a automação declara. CAP_QR e CAP_MSG_CHECKOUT entram JUNTAS e só quando
    /// a loja liga o QR na tela: declarar a capacidade é o que faz a biblioteca mandar
    /// PWDAT_DSPQRCODE e PWDAT_DSPCHECKOUT, e prometer o que a tela não faz trava a venda.
    /// </summary>
    public static int CapacidadesCom(int capacidades, bool qrNaTela)
        => qrNaTela ? capacidades | PW.CAP_QR | PW.CAP_MSG_CHECKOUT : capacidades;

    public const string NomeAutomacao = "Pdv.AmericanDay";

    /// <summary>AUTDEV em branco: a mesma razão social que o 716 do PayGo por arquivos usa.</summary>
    public const string DesenvolvedorPadrao = "American Day";

    public static string Diretorio(Func<string, string?> config)
    {
        var v = config(ChaveDir)?.Trim();
        return string.IsNullOrEmpty(v) ? DirPadrao : v;
    }

    /// <summary>Pasta da PGWebLib.dll, ou null para deixar o Windows procurar.</summary>
    public static string? PastaDll(Func<string, string?> config)
    {
        var v = config(ChaveDll)?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>
    /// Aviso de uma linha para a Configuração quando `tef_pgweb_dll` aponta para uma pasta sem
    /// PGWebLib.dll. Null = em branco (o Windows procura) ou a DLL está lá.
    ///
    /// Antes valia outra regra, e ela caiu: com o PayGo Windows instalado, a DLL protegida pelo
    /// Warsaw só carregava da pasta original e uma cópia devolvia -2414 no PW_iInit. O kit avulso
    /// da biblioteca (4.1.50.924, sem Warsaw) carrega de qualquer pasta. Medido em 07/09/2026
    /// rodando de C:\PGWebLib\x64, com PW_iInit devolvendo PWRET_OK em meio segundo.
    /// </summary>
    public static string? AvisoPastaDll(string? pasta, Func<string, bool>? existeArquivo = null)
    {
        var p = pasta?.Trim();
        if (string.IsNullOrEmpty(p)) return null;
        var existe = existeArquivo ?? File.Exists;
        return existe(Path.Combine(p, "PGWebLib.dll")) ? null
            : $"Não achei PGWebLib.dll em {p}. Aponte para a pasta onde você copiou a biblioteca.";
    }

    /// <summary>
    /// Monta as opções a partir da config. Reaproveita `tef_paygo_empresa` (AUTDEV),
    /// `tef_paygo_rede` (AUTHSYST cartão) e `tef_paygo_rede_pix` (AUTHSYST Pix): a loja só
    /// tem uma rede, seja qual for o caminho até o PayGo.
    /// </summary>
    public static OpcoesPGWebLib Opcoes(Func<string, string?> config, string versao)
    {
        static string? Limpo(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var caps = int.TryParse(config(ChaveCapacidades)?.Trim(), out var c) && c >= 0 ? c : ProvedorPGWebLib.CapacidadesPadrao;
        caps = CapacidadesCom(caps, QrNaTela(config));
        return new OpcoesPGWebLib(
            NomeAutomacao: NomeAutomacao,
            VersaoAutomacao: versao,
            Desenvolvedor: Limpo(config("tef_paygo_empresa")) ?? DesenvolvedorPadrao,
            Capacidades: caps,
            RedeCartao: Limpo(config("tef_paygo_rede")),
            RedePix: Limpo(config("tef_paygo_rede_pix")),
            PortaPinpad: Limpo(config(ChavePortaPinpad)) ?? "0",
            Ambiente: Ambiente(config),
            RedesPermitidas: Redes(config));
    }
}

/// <summary>
/// O que a tela mostra e o que devolve para cada <see cref="PwGetData"/>. Puro: a tela
/// chama o diálogo com <see cref="Titulo"/>/<see cref="Rotulo"/>/<see cref="Textos"/> e
/// passa o toque/texto por <see cref="Menu"/>/<see cref="Digitado"/>.
/// </summary>
public static class RespostaDaTela
{
    public const string ErroParcelas = "Digite um número de 1 a 99.";

    public static bool EhParcelas(PwGetData d) => !d.EhMenu && d.Identificador == PW.PWINFO_INSTALLMENTS;

    /// <summary>Senha do lojista (PWDAT_USERAUTH) ou dado que a biblioteca marcou como oculto: diálogo de senha, sem eco.</summary>
    public static bool Ocultar(PwGetData d) => d.Ocultar || d.Tipo == PW.PWDAT_USERAUTH;

    public static string Titulo(PwGetData d)
    {
        if (d.EhMenu && d.Identificador == PW.PWINFO_AUTHSYST) return "Rede";
        if (EhParcelas(d)) return "Parcelas no crédito";
        if (d.Tipo == PW.PWDAT_USERAUTH) return "Senha do lojista";
        var p = d.Prompt?.Trim();
        return string.IsNullOrEmpty(p) ? "Maquininha" : p;
    }

    /// <summary>A pergunta do diálogo de texto. Parcelas usa a MESMA frase da tela de pagamento.</summary>
    public static string Rotulo(PwGetData d)
    {
        if (EhParcelas(d)) return "Em quantas vezes? (1 = à vista, até 99)";
        if (d.Tipo == PW.PWDAT_USERAUTH) return "Senha do lojista";
        var p = d.Prompt?.Trim();
        return string.IsNullOrEmpty(p) ? "Digite o dado pedido pela maquininha" : p;
    }

    /// <summary>Valor com que a caixa abre: o inicial da biblioteca, "1" nas parcelas, nunca nada numa senha.</summary>
    public static string Sugestao(PwGetData d)
    {
        if (Ocultar(d)) return "";
        if (!string.IsNullOrEmpty(d.ValorInicial)) return d.ValorInicial;
        return EhParcelas(d) ? "1" : "";
    }

    /// <summary>Os botões do menu, na ordem da biblioteca.</summary>
    public static IReadOnlyList<string> Textos(PwGetData d)
        => d.Opcoes is null ? Array.Empty<string>() : d.Opcoes.Select(o => o.Texto).ToList();

    /// <summary>Índice tocado → VALOR da opção (o que vai em PW_iAddParam). -1 ou fora = null (cancela).</summary>
    public static string? Menu(PwGetData d, int indice)
    {
        if (!d.EhMenu || d.Opcoes is null || indice < 0 || indice >= d.Opcoes.Count) return null;
        return d.Opcoes[indice].Valor;
    }

    /// <summary>
    /// Texto digitado → (valor, erro). Cancelou (null) = (null, null). Erro preenchido = a tela
    /// avisa e pergunta de novo (nunca chuta um valor: parcela adivinhada é cobrança errada).
    /// </summary>
    public static (string? Valor, string? Erro) Digitado(PwGetData d, string? texto)
    {
        if (texto is null) return (null, null);
        var v = texto.Trim();
        if (EhParcelas(d))
            return int.TryParse(v, out var n) && n >= 1 && n <= 99 ? (n.ToString(), null) : (null, ErroParcelas);
        if (v.Length == 0 && !d.AceitaNulo) return (null, "Este campo não pode ficar em branco.");
        var min = d.TamanhoMinimo; var max = d.TamanhoMaximo;
        if ((min > 0 && v.Length < min) || (max > 0 && v.Length > max))
            return (null, max > 0 ? $"Digite entre {min} e {max} caracteres." : $"Digite pelo menos {min} caracteres.");
        return (v, null);
    }
}

/// <summary>
/// A lista de redes que a loja deixa o caixa ver (<see cref="ConfigPGWebLib.ChaveRedes"/>) e como
/// ela encurta o menu de seleção da rede (PWDAT_MENU com PWINFO_AUTHSYST) que a biblioteca manda.
///
/// Por que existe: o terminal lista TUDO que está instalado nele, e quem opera precisa escolher
/// entre nomes parecidos com o cliente esperando. Na homologação isso é pior ainda: o roteiro usa
/// três autorizadores e só três (C6PAY na maioria das vendas, REDE no passo 38, PIX C6 BANK nos
/// passos 11, 55 e 56), e tocar no vizinho errado queima a venda e o passo.
///
/// Quatro coisas aqui são inegociáveis:
///
///   1. lista VAZIA mostra tudo, e é o padrão. Numa loja de verdade quem sabe o que está
///      credenciado no terminal é o PayGo, não a nossa config;
///   2. o menu continua APARECENDO, nem que sobre uma opção. Quem mata o menu é a rede fixa
///      (`tef_paygo_rede`), e com ela morre o passo 05, que manda apertar Esc nesse menu;
///   3. filtro que não casa com NADA é filtro ignorado: mostra a lista inteira e deixa a linha na
///      auditoria. Menu vazio no meio de uma venda é pior do que filtro que não pegou;
///   4. o que volta para a biblioteca é sempre o VALOR da opção dela. Aqui só se escondem linhas:
///      as opções que sobram são os mesmos objetos que a biblioteca mandou, nunca o texto que
///      alguém digitou na Configuração.
///
/// Vale SÓ para o menu de rede. Menu de outra coisa (o administrativo, o genérico dos passos 30 e
/// 31) passa inteiro: a lista da loja fala de credenciadora, e nada mais.
/// </summary>
public static class FiltroRedes
{
    /// <summary>Os separadores aceitos. A tela pede vírgula; ponto e vírgula e quebra de linha entram porque é o que sai de um texto colado.</summary>
    private static readonly char[] Separadores = { ',', ';', '\n', '\r' };

    /// <summary>
    /// Quebra o texto da config em nomes de rede. Vazio, só espaços ou só separadores devolvem
    /// lista vazia, que é o "mostra tudo".
    /// </summary>
    public static IReadOnlyList<string> Ler(string? config)
    {
        if (string.IsNullOrWhiteSpace(config)) return Array.Empty<string>();
        var redes = new List<string>();
        foreach (var parte in config.Split(Separadores, StringSplitOptions.RemoveEmptyEntries))
        {
            var nome = parte.Trim();
            if (nome.Length > 0) redes.Add(nome);
        }
        return redes;
    }

    /// <summary>O texto para gravar de volta na config a partir de uma lista.</summary>
    public static string Texto(IEnumerable<string>? redes)
        => redes is null ? "" : string.Join(", ", Ler(string.Join(",", redes)));

    /// <summary>
    /// O pedido que vai para a tela. Devolve o MESMO objeto quando não há o que encurtar (não é
    /// menu de rede, lista vazia, nada casou, ou casou tudo); senão um pedido igual com menos
    /// opções, na ordem em que a biblioteca mandou.
    /// </summary>
    public static PwGetData Aplicar(PwGetData pedido, IReadOnlyList<string>? permitidas, Action<string>? auditar = null)
    {
        // Só o menu de rede: o administrativo e os menus genéricos passam inteiros.
        if (!pedido.EhMenu || pedido.Identificador != PW.PWINFO_AUTHSYST) return pedido;
        if (permitidas is null || permitidas.Count == 0) return pedido;
        if (pedido.Opcoes is null || pedido.Opcoes.Count == 0) return pedido;

        // Casa pelo VALOR ou pelo TEXTO da opção: as duas colunas vêm da biblioteca e nem sempre
        // são iguais. A comparação é a mesma da lista fechada de credenciadoras (RedesPayGo):
        // dobra acento, maiúscula e espaço nas pontas, e não mexe no espaço interno.
        var casadas = pedido.Opcoes
            .Where(o => permitidas.Any(r => RedesPayGo.Mesma(r, o.Valor) || RedesPayGo.Mesma(r, o.Texto)))
            .ToList();

        if (casadas.Count == 0)
        {
            // Ignorar o filtro é a saída menos ruim: com o menu vazio o operador não teria como
            // escolher nem como sair, e a venda morreria sem ninguém entender por quê.
            auditar?.Invoke($"pgweblib: as redes da loja ({Lista(permitidas)}) não casaram com nenhuma opção do menu ({Lista(pedido.Opcoes.Select(o => o.Texto))}); mostrando todas");
            return pedido;
        }
        if (casadas.Count == pedido.Opcoes.Count) return pedido;

        auditar?.Invoke($"pgweblib: menu de redes encurtado pela lista da loja: {casadas.Count} de {pedido.Opcoes.Count} ({Lista(casadas.Select(o => o.Texto))})");
        return pedido with { Opcoes = casadas };
    }

    private static string Lista(IEnumerable<string> nomes) => string.Join("|", nomes);
}
