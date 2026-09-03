using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Pdv.Nucleo;

/// <summary>
/// O botão "Atualizar o caixa" — a parte que pensa.
///
/// O QUE ESTE ARQUIVO NÃO FAZ, e é o mais importante: ele não instala nada. Quem
/// sabe trocar o programa por cima preservando as vendas, renomear arquivo em uso e
/// conferir que o caixa ABRE antes de gravar registro e atalho é o
/// <c>Pdv.Instalador</c>, que já existe, já foi testado e já roda elevado pelo
/// manifesto. Reescrever isso aqui seria manter duas verdades sobre a coisa mais
/// perigosa do sistema. O PDV só faz quatro coisas: CONFERE a versão, BAIXA, PROVA
/// que o que baixou é o que devia, e ENTREGA ao instalador antes de sair de cena.
///
/// A lógica mora no Núcleo (e não na tela) porque é ela que erra em silêncio:
/// comparar versão como texto diz que "0.9.0" é maior que "0.10.0", e o caixa fica
/// parado numa versão velha para sempre sem ninguém ver mensagem de erro nenhuma.
/// Um bug desses não aparece em teste manual — aparece daqui a oito releases.
///
/// TRÊS REGRAS QUE VALEM MAIS QUE A ATUALIZAÇÃO:
///  1. Caixa desatualizado é chato. Caixa que reinicia com o cliente no balcão é
///     prejuízo. Por isso <see cref="Impede"/> vem antes de tudo — e é conferido DE
///     NOVO na hora de entregar ao instalador, porque um download de 165 MB numa rede
///     de loja demora minutos e o balcão não fica parado esperando.
///  2. Download pela metade não pode virar instalação. Ver <see cref="Conferir"/>.
///  3. Nada é escrito por cima do programa em uso: o arquivo baixa no TEMP e só o
///     instalador (elevado, em outro processo) encosta em Program Files.
/// </summary>
public static class Atualizacao
{
    /// <summary>Onde o caixa pergunta "tem versão nova?". Sobrescrevível pela config
    /// <c>atualizacao_url</c>: o mesmo executável atende lojas de clientes diferentes,
    /// e uma delas pode um dia servir o próprio arquivo.</summary>
    public const string UrlPadrao = "https://mmtech.software/pdv/versao.json";

    /// <summary>O manifesto tem que dizer de QUE produto ele fala. Sem esta conferência,
    /// apontar a config para o feed errado (ou o servidor servir outro JSON no mesmo
    /// caminho) faria o caixa baixar e executar um instalador de outra coisa.</summary>
    public const string NomeDoProduto = "pdv";

    /// <summary>Pasta de trabalho da atualização. TEMP, nunca ao lado do exe em uso.</summary>
    public static string PastaTemp => Path.Combine(Path.GetTempPath(), "pdv-atualizacao");

    // ══ VERSÃO ═══════════════════════════════════════════════════════════════
    // Este é O bug clássico desta função, e ele é mudo: comparando TEXTO,
    // "0.10.0" < "0.9.0" (porque '1' < '9'), e a partir da versão .10 o caixa
    // decide sozinho que já está em dia. Ninguém vê erro; a loja só fica velha.

    /// <summary>
    /// Versão em números, não em letras. Aceita 1 a 4 partes ("0.2" = "0.2.0.0"),
    /// tolera o "v" na frente e ignora sufixo de pré-lançamento/metadata ("0.3.0-rc1").
    /// </summary>
    public readonly record struct Versao(int A, int B, int C, int D) : IComparable<Versao>
    {
        public int CompareTo(Versao o)
        {
            if (A != o.A) return A.CompareTo(o.A);
            if (B != o.B) return B.CompareTo(o.B);
            if (C != o.C) return C.CompareTo(o.C);
            return D.CompareTo(o.D);
        }

        public override string ToString() => D == 0 ? $"{A}.{B}.{C}" : $"{A}.{B}.{C}.{D}";
    }

    /// <summary>Devolve false quando o texto não é uma versão — e "não é versão" NUNCA
    /// pode virar 0.0.0, senão um manifesto quebrado parece uma versão antiquíssima e
    /// o caixa conclui que está adiantado.</summary>
    public static bool TentarLerVersao(string? texto, out Versao versao)
    {
        versao = default;
        if (string.IsNullOrWhiteSpace(texto)) return false;

        var t = texto.Trim();
        if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase)) t = t[1..];
        // "0.3.0-rc1" / "0.3.0+build7": o que vem depois não entra na comparação.
        var corte = t.IndexOfAny(new[] { '-', '+', ' ' });
        if (corte >= 0) t = t[..corte];
        if (t.Length == 0) return false;

        var partes = t.Split('.');
        if (partes.Length is 0 or > 4) return false;

        var n = new int[4];
        for (var i = 0; i < partes.Length; i++)
        {
            if (!int.TryParse(partes[i], NumberStyles.None, CultureInfo.InvariantCulture, out var v) || v < 0)
                return false;
            n[i] = v;
        }
        versao = new Versao(n[0], n[1], n[2], n[3]);
        return true;
    }

    /// <summary>&lt;0 se a primeira é mais velha, 0 se iguais, &gt;0 se mais nova.
    /// Ilegível de um lado só perde; ilegível dos dois lados empata.</summary>
    public static int Comparar(string? a, string? b)
    {
        var okA = TentarLerVersao(a, out var va);
        var okB = TentarLerVersao(b, out var vb);
        if (okA && okB) return va.CompareTo(vb);
        if (okA) return 1;
        if (okB) return -1;
        return 0;
    }

    /// <summary>Versão deste executável, como o Windows a vê. É a mesma que o
    /// instalador grava no Adicionar/Remover Programas.</summary>
    public static string VersaoInstalada()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return "0.0.0";
            var fv = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            return fv.FileVersion ?? fv.ProductVersion ?? "0.0.0";
        }
        catch { return "0.0.0"; }
    }

    // ══ MANIFESTO ════════════════════════════════════════════════════════════

    /// <summary>
    /// O <c>versao.json</c> do servidor, já validado.
    /// </summary>
    /// <param name="Sha256">
    /// SHA-256 do arquivo, em hexadecimal. O contrato de hoje NÃO tem este campo —
    /// ver <see cref="Conferir"/> para o que sobra quando ele falta, e por que a
    /// ausência é aceita em vez de recusada.
    /// </param>
    /// <param name="Tamanho">Bytes exatos do arquivo. Opcional; quando vem, permite
    /// recusar ANTES de gastar a franquia da loja baixando 165 MB errados.</param>
    public sealed record Manifesto(
        string Versao, string Url, string? Notas, bool Obrigatoria,
        string? Sha256 = null, long? Tamanho = null);

    /// <summary>Ou o manifesto, ou o motivo em português de por que ele não serve.</summary>
    public sealed record LeituraManifesto(Manifesto? Ok, string? Erro);

    /// <summary>
    /// Lê e VALIDA o manifesto. Tudo aqui é recusa antes de baixar, porque o que se
    /// baixa é um executável que vai rodar como administrador nesta máquina.
    ///
    /// <paramref name="urlDoManifesto"/> entra na conta de propósito: o arquivo tem
    /// que morar no MESMO domínio de quem o anunciou. Não é paranoia teórica — o campo
    /// <c>url</c> é texto livre vindo da rede, e sem esta amarra um manifesto trocado
    /// (ou um erro de digitação no deploy) manda o caixa da loja baixar e executar um
    /// exe de qualquer lugar da internet, elevado. Não substitui assinatura digital,
    /// que é a correção de verdade — reduz a superfície enquanto ela não existe.
    /// </summary>
    public static LeituraManifesto LerManifesto(string? json, string urlDoManifesto)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new(null, "O servidor de atualização não respondeu nada.");

        JsonElement raiz;
        try
        {
            using var doc = JsonDocument.Parse(json);
            raiz = doc.RootElement.Clone();
        }
        catch
        {
            // Caso REAL e comum: proxy de loja, portal cativo de wi-fi ou página de erro
            // do servidor devolvem HTML com status 200. Dizer "JSON inválido" manda o
            // dono procurar no lugar errado.
            return new(null, "O servidor respondeu algo que não é a lista de versões. "
                           + "Pode ser o wi-fi da loja pedindo login numa página.");
        }

        if (raiz.ValueKind != JsonValueKind.Object)
            return new(null, "A lista de versões veio num formato que este caixa não entende.");

        var (m, erro) = LerCampos(raiz, urlDoManifesto, exigirVersao: true);
        return erro is { Length: > 0 } ? new(null, erro) : new(m, null);
    }

    /// <summary>
    /// As peneiras de um anúncio de versão, sem se importar de ONDE o JSON veio — o
    /// <c>versao.json</c> do nginx e a resposta da RPC do painel passam pelas MESMAS.
    ///
    /// <paramref name="ancoraDeDominio"/> é o endereço com que o host do instalador é
    /// comparado, e ele é o coração da segurança deste arquivo: o campo <c>url</c> vem
    /// da rede e vira um executável rodando como ADMINISTRADOR nesta máquina. No
    /// caminho do arquivo a âncora é a própria URL do manifesto; no caminho do painel
    /// ela NÃO é o endereço do Supabase — é a `atualizacao_url` gravada NESTE caixa.
    /// A diferença é o que impede o painel (ou quem tomar o painel) de mandar a loja
    /// baixar um exe de qualquer lugar da internet: o painel escolhe QUAL versão e
    /// QUANDO, e nunca DE ONDE.
    ///
    /// <paramref name="exigirVersao"/> falso é o caso do painel: "não tenho nada para
    /// este terminal" é resposta NORMAL (é assim que se libera loja por loja), e tratar
    /// isso como erro faria o caixa acender aviso de falha em todo terminal que ainda
    /// não entrou na onda.
    /// </summary>
    private static (Manifesto? Ok, string? Erro) LerCampos(
        JsonElement raiz, string ancoraDeDominio, bool exigirVersao)
    {
        var versaoCrua = Texto(raiz, "versao");
        if (!exigirVersao && string.IsNullOrWhiteSpace(versaoCrua))
            return (null, null);        // nada para este terminal — e isso não é falha

        var produto = Texto(raiz, "produto");
        if (!string.Equals(produto, NomeDoProduto, StringComparison.OrdinalIgnoreCase))
            return (null, $"Esta lista de versões não é a do caixa (ela fala de \"{produto ?? "?"}\").");

        var versao = versaoCrua;
        if (!TentarLerVersao(versao, out _))
            return (null, $"O servidor anunciou uma versão que não dá para entender (\"{versao ?? ""}\").");

        var url = Texto(raiz, "url");
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return (null, "O servidor não disse onde baixar o instalador.");
        // http:// puro numa rede de loja é o cenário de troca de arquivo mais barato
        // que existe. Executável só desce por canal cifrado.
        if (uri.Scheme != Uri.UriSchemeHttps)
            return (null, "O endereço do instalador não é seguro (precisa ser https).");
        if (!MesmoDominio(ancoraDeDominio, url))
            return (null, "O instalador anunciado está fora do servidor da atualização. "
                        + "Por segurança este caixa não baixa.");

        // Hash torto é pior que hash ausente: sem ele existe um plano B declarado
        // (tamanho + executável válido); com ele torto, a conferência nunca fecharia e
        // a loja ficaria travada num erro que ninguém entende.
        var sha = Texto(raiz, "sha256")?.Trim();
        if (sha is { Length: > 0 } && !EhHexDe32Bytes(sha))
            return (null, "A impressão digital (sha256) anunciada está malformada.");

        long? tamanho = null;
        if (raiz.TryGetProperty("tamanho", out var t))
        {
            if (t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var n) && n > 0) tamanho = n;
            else if (t.ValueKind == JsonValueKind.String && long.TryParse(t.GetString(), out var n2) && n2 > 0) tamanho = n2;
        }

        return (new Manifesto(
            Versao: versao!.Trim(),
            Url: url,
            Notas: Texto(raiz, "notas"),
            Obrigatoria: Bandeira(raiz, "obrigatoria"),
            Sha256: sha is { Length: > 0 } ? sha.ToLowerInvariant() : null,
            Tamanho: tamanho), null);
    }

    private static string? Texto(JsonElement o, string campo)
        => o.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>Aceita true/false e também "true"/"1" — quem publica o JSON é gente,
    /// e gente escreve aspas. Ausente = false (o caso normal).</summary>
    private static bool Bandeira(JsonElement o, string campo)
    {
        if (!o.TryGetProperty(campo, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => v.GetString() is "true" or "True" or "1" or "sim",
            JsonValueKind.Number => v.TryGetInt64(out var n) && n != 0,
            _ => false,
        };
    }

    internal static bool EhHexDe32Bytes(string s)
        => s.Length == 64 && s.All(Uri.IsHexDigit);

    /// <summary>Mesmo domínio registrável (dois últimos rótulos): <c>mmtech.software</c>
    /// aceita <c>pdv.mmtech.software</c> e recusa <c>mmtech.software.exemplo.com</c>.</summary>
    internal static bool MesmoDominio(string urlA, string urlB)
    {
        if (!Uri.TryCreate(urlA, UriKind.Absolute, out var a)) return false;
        if (!Uri.TryCreate(urlB, UriKind.Absolute, out var b)) return false;
        // ⚠️ POR SUFIXO DE HOST, e não pelos dois últimos rótulos.
        //
        // A versão anterior cortava o host em p[^2..]: "pdv.mmtech.software" virava
        // "mmtech.software" e funcionava. Mas num domínio .com.br — que é o padrão dos
        // domínios desta operação — "pdv.americandaybrasil.com.br" vira "com.br", e aí
        // QUALQUER host .com.br do mundo passaria na conferência. O campo "url" vem da
        // rede e vira um exe rodando como administrador: essa peneira é a que impede o
        // manifesto de apontar para um servidor de terceiro.
        //
        // Sem lista de sufixos públicos (que envelhece e precisa ser mantida): exige o
        // MESMO host, ou que um seja subdomínio do outro. Cobre o caso real (manifesto
        // em mmtech.software, instalador em pdv.mmtech.software) e não tem como afrouxar
        // sozinha com o tempo.
        // UMA DIREÇÃO SÓ: o instalador tem que morar NO host do manifesto ou ABAIXO
        // dele. Aceitar o contrário (instalador num host PAI) parece simétrico e não é —
        // com o manifesto em pdv.loja.com.br, um "com.br" seria pai de tudo e a peneira
        // deixaria passar o mundo inteiro. Foi o que o teste pegou. Quem manda é o
        // manifesto: ele é o endereço que ESTE caixa foi configurado para consultar.
        var hManifesto = a.Host.TrimEnd('.');
        var hInstalador = b.Host.TrimEnd('.');
        return hInstalador.Equals(hManifesto, StringComparison.OrdinalIgnoreCase)
            || hInstalador.EndsWith("." + hManifesto, StringComparison.OrdinalIgnoreCase);
    }

    // ══ QUANDO NÃO DÁ ════════════════════════════════════════════════════════

    /// <summary>
    /// O que o caixa está vivendo agora. Vem da tela (que é quem sabe da comanda) e do
    /// banco (que é quem sabe da maquininha e da fila).
    /// </summary>
    /// <param name="PapeisNaFila">Trabalhos na fila da impressora. -1 = não deu para
    /// ler a fila (driver simples, impressora de rede fora do ar). Ver <see cref="Impede"/>.</param>
    /// <param name="EstadoIncerto">Alguma leitura que este estado precisava falhou (o
    /// banco não abriu, a comanda gravada não deu para contar). Não muda nada no
    /// caminho manual — lá tem gente decidindo — e barra o automático, onde não tem.
    /// Ver <see cref="ImpedeSozinho"/>.</param>
    public sealed record EstadoDoCaixa(
        int ItensNaComanda = 0,
        bool MaquininhaOcupada = false,
        int CobrancasNoPinpad = 0,
        int PapeisNaFila = 0,
        bool CaixaAberto = false,
        int VendasPorSubir = 0,
        bool EstadoIncerto = false);

    /// <param name="EstadoDesconhecido">Só existe no caminho AUTOMÁTICO: alguma coisa
    /// que o portão precisa saber não pôde ser lida. Ver <see cref="ImpedeSozinho"/>.</param>
    public enum Impedimento
    {
        Nenhum, ComandaAberta, MaquininhaOcupada, CobrancaNoPinpad, PapelNaFila,
        EstadoDesconhecido,
    }

    /// <summary>
    /// A recusa. Roda ANTES de perguntar ao servidor e DE NOVO antes de entregar ao
    /// instalador — entre as duas passam minutos de download, e nesses minutos entra
    /// cliente.
    ///
    /// Repare no que NÃO impede: caixa aberto. A sessão do turno mora no SQLite em
    /// C:\ProgramData, que o instalador nunca toca — fundo de troco, sangria,
    /// suprimento e apurado voltam intactos. Bloquear por caixa aberto significaria
    /// "só dá para atualizar entre turnos", e numa loja que abre às 8 e fecha às 22
    /// isso quer dizer nunca. O operador VIVE alguma coisa (a tela fecha e ele digita
    /// o PIN de novo), então isso é AVISO — em <see cref="Decidir"/> — e não bloqueio.
    ///
    /// Fila de impressão ilegível (-1) também não impede: a leitura da fila falha por
    /// motivo banal em impressora de rede, e transformar "não sei" em "não pode" faria
    /// o botão parar de funcionar em metade das lojas por causa de um driver.
    /// </summary>
    public static Impedimento Impede(EstadoDoCaixa e)
    {
        if (e.ItensNaComanda > 0) return Impedimento.ComandaAberta;
        if (e.MaquininhaOcupada) return Impedimento.MaquininhaOcupada;
        if (e.CobrancasNoPinpad > 0) return Impedimento.CobrancaNoPinpad;
        if (e.PapeisNaFila > 0) return Impedimento.PapelNaFila;
        return Impedimento.Nenhum;
    }

    /// <summary>
    /// O MESMO portão, para quando NÃO TEM NINGUÉM OLHANDO.
    ///
    /// A única diferença é a regra do desconhecido, e ela é invertida de propósito:
    ///
    ///  · no caminho MANUAL, "não sei" (fila da impressora ilegível) deixa passar,
    ///    porque tem uma pessoa de frente para o balcão que viu a loja e apertou o
    ///    botão. O julgamento dela vale mais do que a leitura de um driver;
    ///  · no caminho AUTOMÁTICO não existe esse julgamento. Aqui "não sei" vira "não
    ///    pode" — e o preço do erro é assimétrico: barrar por engano custa um dia a
    ///    mais na versão velha (e o painel enxerga isso, porque o caixa reporta a
    ///    versão que está rodando); deixar passar por engano fecha a frente de caixa
    ///    sozinha com um cupom saindo pela metade.
    ///
    /// Nada aqui é EXCEÇÃO ao portão: é o portão mais uma regra a mais. A janela
    /// responde "posso agora?"; isto responde "é seguro agora?"; as duas precisam
    /// dizer sim.
    /// </summary>
    public static Impedimento ImpedeSozinho(EstadoDoCaixa e)
    {
        // O impedimento CONCRETO ganha do genérico: "tem 2 itens na comanda" é uma
        // frase que o dono entende no painel; "não sei o que está acontecendo" não.
        var i = Impede(e);
        if (i != Impedimento.Nenhum) return i;
        return e.EstadoIncerto || e.PapeisNaFila < 0
            ? Impedimento.EstadoDesconhecido : Impedimento.Nenhum;
    }

    /// <summary>Por que recusou E o que fazer para destravar. Recusa sem saída é a
    /// forma mais rápida de ensinar o operador a não ler a tela.</summary>
    public static (string Titulo, string Mensagem) Explicar(Impedimento i, EstadoDoCaixa e) => i switch
    {
        Impedimento.ComandaAberta => ("Tem venda em andamento",
            $"A comanda está aberta com {Plural(e.ItensNaComanda, "item", "itens")}.\n\n"
            + "Para trocar o programa o caixa precisa fechar e abrir de novo, e reiniciar "
            + "com o cliente no balcão é pior do que ficar mais um dia na versão atual.\n\n"
            + "O QUE FAZER: termine ou limpe a comanda e toque em Atualizar de novo."),

        Impedimento.MaquininhaOcupada => ("A maquininha está ocupada",
            "Tem uma operação acontecendo na maquininha agora.\n\n"
            + "O QUE FAZER: espere ela terminar na tela do pinpad e toque em Atualizar de novo."),

        Impedimento.CobrancaNoPinpad => ("Tem cobrança na maquininha",
            $"{Plural(e.CobrancasNoPinpad, "cobrança", "cobranças")} ainda sem resposta da maquininha.\n\n"
            + "Fechar o caixa agora deixaria essa cobrança sem dono: o cliente pode ter pago "
            + "e a venda não existir aqui.\n\n"
            + "O QUE FAZER: termine ou cancele a cobrança no pinpad e toque em Atualizar de novo."),

        Impedimento.PapelNaFila => ("Tem papel saindo",
            $"{Plural(e.PapeisNaFila, "papel", "papéis")} na fila da impressora.\n\n"
            + "O QUE FAZER: espere o cupom sair e toque em Atualizar de novo. "
            + "Se a impressora estiver travada, resolva ela primeiro."),

        Impedimento.EstadoDesconhecido => ("Não deu para conferir se é seguro",
            "A fila da impressora não respondeu, então este caixa não consegue provar "
            + "que não tem cupom saindo agora.\n\n"
            + "A troca sozinha (na janela de atualização) fica para quando der. "
            + "O QUE FAZER: toque em Atualizar. Pelo botão, quem julga se a loja está "
            + "parada é você."),

        _ => ("", ""),
    };

    /// <summary>O impedimento em UMA PALAVRA, para o painel. É o que o dono lê na
    /// coluna "por que este caixa não atualizou" — por isso é estável e sem acento:
    /// vira chave de filtro do outro lado, não frase de tela.</summary>
    public static string NomeDoImpedimento(Impedimento i) => i switch
    {
        Impedimento.ComandaAberta => "comanda",
        Impedimento.MaquininhaOcupada => "maquininha",
        Impedimento.CobrancaNoPinpad => "pinpad",
        Impedimento.PapelNaFila => "papel",
        Impedimento.EstadoDesconhecido => "desconhecido",
        _ => "nenhum",
    };

    internal static string Plural(int n, string um, string varios)
        => n == 1 ? $"1 {um}" : $"{n} {varios}";

    // ══ A DECISÃO ════════════════════════════════════════════════════════════

    public enum Situacao
    {
        /// <summary>Já está na última. É o caso normal, e a resposta é curta.</summary>
        EmDia,
        /// <summary>Tem versão nova e o caixa pode parar. Segue para a confirmação.</summary>
        Disponivel,
        /// <summary>Não dá agora (venda, pinpad, papel).</summary>
        Impedido,
        /// <summary>Rede, servidor ou manifesto ruim. Nada acontece com o caixa.</summary>
        Erro,
    }

    public sealed record Veredito(
        Situacao Situacao, string Titulo, string Mensagem,
        Manifesto? Manifesto = null, bool Obrigatoria = false,
        string TextoSim = "Atualizar agora", string TextoNao = "Agora não");

    /// <summary>
    /// Junta tudo: impedimento, erro de leitura, comparação de versão e as ressalvas
    /// que o operador precisa ouvir ANTES de o caixa fechar.
    ///
    /// SOBRE "obrigatoria": true — ela muda a conversa, e NÃO muda quem decide.
    /// O que ela faz: o texto diz que a versão atual não deveria mais estar rodando,
    /// o botão de recusar passa a se chamar "Não posso agora" (que é uma frase que se
    /// leva ao gerente, diferente de "Agora não"), e a tela mantém o aviso à vista até
    /// alguém atualizar. O que ela NÃO faz: reiniciar o caixa sozinha. Um campo
    /// booleano num arquivo JSON servido pela internet não tem mais autoridade do que
    /// a pessoa que está de frente para o cliente — e a regra 1 deste arquivo não tem
    /// exceção. Atualização obrigatória que derruba a frente de caixa no movimento é
    /// como se perde uma loja, não como se corrige um bug.
    /// </summary>
    public static Veredito Decidir(EstadoDoCaixa estado, string? versaoInstalada, LeituraManifesto leitura)
    {
        var impede = Impede(estado);
        if (impede != Impedimento.Nenhum)
        {
            var (t, m) = Explicar(impede, estado);
            return new Veredito(Situacao.Impedido, t, m, TextoSim: "Entendi", TextoNao: "");
        }

        if (leitura.Erro is { Length: > 0 })
            return new Veredito(Situacao.Erro, "Não consegui verificar",
                leitura.Erro
                + "\n\nO caixa continua funcionando normalmente. Tente de novo mais tarde.",
                TextoSim: "Entendi", TextoNao: "");

        // Sem manifesto E sem erro = o PAINEL respondeu, e a resposta foi "não tenho
        // versão para este terminal". É a resposta normal de quem publica loja por
        // loja: as 39 que ainda não entraram na onda recebem exatamente isto. Chamar
        // de falha faria o operador ligar para o suporte por causa do funcionamento
        // correto do sistema.
        if (leitura.Ok is null)
            return new Veredito(Situacao.EmDia, "Tudo em dia",
                $"Este caixa está na versão {Mostrar(versaoInstalada)} e não tem nenhuma "
                + "atualização liberada para ele.",
                TextoSim: "Entendi", TextoNao: "");

        var m2 = leitura.Ok;
        if (Comparar(versaoInstalada, m2.Versao) >= 0)
            return new Veredito(Situacao.EmDia, "Tudo em dia",
                $"Este caixa já está na versão mais nova ({Mostrar(versaoInstalada)}). "
                + "Não tem nada para atualizar.",
                m2, TextoSim: "Entendi", TextoNao: "");

        var linhas = new List<string>
        {
            $"Este caixa tem a versão {Mostrar(versaoInstalada)}. A nova é a {m2.Versao}.",
        };
        if (m2.Notas is { Length: > 0 } notas) linhas.Add("\n" + notas.Trim());

        if (m2.Obrigatoria)
            linhas.Add("\n⚠ ESTA ATUALIZAÇÃO É OBRIGATÓRIA. A versão que está aqui não deveria "
                     + "mais estar rodando. Se você não puder parar agora, avise o gerente hoje.");

        linhas.Add("\nO que vai acontecer: o caixa baixa a versão nova, fecha e abre de novo "
                 + "sozinho. Leva alguns minutos, dependendo da internet da loja.");
        linhas.Add("\nO que NÃO se perde: as vendas, as notas emitidas, a configuração da loja "
                 + "e o caixa aberto. Nada disso mora junto do programa.");

        // Caixa aberto é AVISO, não bloqueio — o motivo está em Impede(). O que o
        // operador precisa saber é a única coisa que ele vai viver: o login de novo.
        if (estado.CaixaAberto)
            linhas.Add("\nO turno continua aberto e o fechamento não muda. Quando o caixa "
                     + "voltar, você entra de novo com o seu PIN.");

        if (estado.VendasPorSubir > 0)
            linhas.Add($"\n{Plural(estado.VendasPorSubir, "venda ainda não subiu", "vendas ainda não subiram")} "
                     + "para o painel. Elas ficam guardadas neste caixa e sobem depois: "
                     + "a atualização não mexe nelas.");

        return new Veredito(Situacao.Disponivel,
            m2.Obrigatoria ? "Atualização obrigatória" : "Tem versão nova",
            string.Join("\n", linhas), m2, m2.Obrigatoria,
            TextoSim: "Atualizar agora",
            TextoNao: m2.Obrigatoria ? "Não posso agora" : "Agora não");
    }

    /// <summary>A versão como o operador deve LER. O Windows guarda FileVersion com
    /// quatro partes ("0.2.0.0") e o csproj/manifesto falam em três ("0.2.0"): mostrar
    /// as duas caras na mesma frase faz o operador achar que são versões diferentes.</summary>
    private static string Mostrar(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "?";
        return TentarLerVersao(v, out var lida) ? lida.ToString() : v!.Trim();
    }

    // ══ A ORDEM DO PAINEL ════════════════════════════════════════════════════
    //
    // POR QUE A INSTRUÇÃO DE VERSÃO SAIU DO ARQUIVO E FOI PARA O PAINEL.
    //
    // O versao.json é UM SÓ para todo mundo. Com 40 lojas isso significa que publicar
    // é publicar para as 40 ao mesmo tempo, e a primeira notícia de que a versão nova
    // tem um defeito chega por telefone, de 40 lugares. O que o dono precisa é mandar
    // para UMA loja, olhar, e só então liberar o resto — e isso é uma decisão POR
    // TERMINAL, que um arquivo estático servido pelo nginx não sabe tomar (e que o
    // painel, aliás, nem consegue escrever).
    //
    // O caixa já fala com o Supabase: sincroniza catálogo, sobe venda, puxa pedido do
    // KDS, e já sabe de que loja ele é (tabela `terminal`, do pareamento). A instrução
    // de versão anda por ESSE canal. O versao.json fica como o que ele sempre foi bom
    // em ser: o caminho do caixa recém-instalado, que ainda não tem identidade.
    //
    // ⚠️ E O QUE O PAINEL NÃO PODE FAZER, que é a parte que importa:
    //  · não escolhe DE ONDE o exe vem — a âncora de domínio é a `atualizacao_url`
    //    gravada neste caixa, não o endereço de quem respondeu (ver LerCampos);
    //  · não fura o portão — nem pela janela, nem por "obrigatória", nem por
    //    "atualizar agora". Remoto aqui quer dizer AGENDAR, não FORÇAR;
    //  · não vale nada se veio do arquivo: instrução com Origem.Arquivo nunca dá
    //    autonomia. Um JSON estático num nginx não reinicia 40 frentes de caixa.

    /// <summary>De onde veio o anúncio de versão. Muda o que ele TEM DIREITO de fazer.</summary>
    public enum Origem
    {
        /// <summary>RPC do painel: sabe de que terminal se trata. Pode agendar.</summary>
        Painel,
        /// <summary>versao.json estático: igual para todo mundo. Só informa.</summary>
        Arquivo,
    }

    /// <summary>
    /// O que o painel respondeu sobre ESTE terminal.
    /// </summary>
    /// <param name="Manifesto">null = "não tenho versão para este terminal". É a
    /// resposta normal de quem libera loja por loja, não uma falha.</param>
    /// <param name="Janela">Faixa de horas em que este caixa pode se trocar sozinho.
    /// null = sem janela, e sem janela ele NUNCA se troca sozinho.</param>
    /// <param name="AgoraNaLoja">O relógio da loja, dito pelo SERVIDOR, com o fuso
    /// dela junto. Ver <see cref="RelogioDaLoja"/> para por que ele não é opcional
    /// quando existe janela.</param>
    /// <param name="AtualizarAgora">O dono marcou ESTE terminal no painel. Dispensa a
    /// janela — e não dispensa o portão.</param>
    public sealed record Instrucao(
        Manifesto? Manifesto,
        Janela? Janela = null,
        DateTimeOffset? AgoraNaLoja = null,
        bool AtualizarAgora = false,
        Origem Origem = Origem.Painel);

    /// <summary>Ou a instrução, ou o motivo em português de por que ela não serve.</summary>
    public sealed record LeituraInstrucao(Instrucao? Ok, string? Erro);

    /// <summary>O manifesto do arquivo, embrulhado como instrução SEM autonomia.</summary>
    public static Instrucao DoArquivo(Manifesto m) => new(m, Origem: Origem.Arquivo);

    /// <summary>
    /// Lê a resposta da RPC do painel.
    ///
    /// Aceita objeto (<c>{...}</c>), lista de um elemento (<c>[{...}]</c>, que é o que
    /// o PostgREST devolve quando a função é <c>SETOF</c>/<c>RETURNS TABLE</c>) e o
    /// literal <c>null</c> — os três são formas legítimas do mesmo "nada para este
    /// terminal", e recusar duas delas amarraria o caixa à assinatura exata que a
    /// função tiver no dia em que ela nascer.
    /// </summary>
    public static LeituraInstrucao LerInstrucao(string? json, string ancoraDeDominio)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new(null, "O painel não respondeu nada sobre a versão deste caixa.");

        JsonElement raiz;
        try
        {
            using var doc = JsonDocument.Parse(json);
            raiz = doc.RootElement.Clone();
        }
        catch
        {
            return new(null, "O painel respondeu algo que não é a versão deste caixa. "
                           + "Pode ser o wi-fi da loja pedindo login numa página.");
        }

        // SETOF: lista vazia é "nada para este terminal"; com mais de um, a função está
        // errada e adivinhar qual linha vale seria escolher qual versão a loja instala.
        if (raiz.ValueKind == JsonValueKind.Array)
        {
            var n = raiz.GetArrayLength();
            if (n == 0) return new(new Instrucao(null), null);
            if (n > 1) return new(null, "O painel respondeu mais de uma versão para este caixa.");
            raiz = raiz[0].Clone();
        }

        if (raiz.ValueKind == JsonValueKind.Null) return new(new Instrucao(null), null);
        if (raiz.ValueKind != JsonValueKind.Object)
            return new(null, "O painel respondeu num formato que este caixa não entende.");

        var (m, erro) = LerCampos(raiz, ancoraDeDominio, exigirVersao: false);
        if (erro is { Length: > 0 }) return new(null, erro);

        // A janela é do painel, e ela pode vir torta (o dono digita "5" e "7", ou
        // alguém salva "25:00"). Janela ilegível não vira erro da consulta: vira
        // AUSÊNCIA de janela — a versão continua aparecendo para o botão, e o que se
        // perde é só a autonomia, que é exatamente o que não se deve conceder por
        // cima de um campo que não deu para ler.
        Janela? janela = TentarLerJanela(Texto(raiz, "janela_inicio"), Texto(raiz, "janela_fim"), out var j)
            ? j : null;

        DateTimeOffset? agora = null;
        if (Texto(raiz, "agora") is { Length: > 0 } quando
            && DateTimeOffset.TryParse(quando, CultureInfo.InvariantCulture,
                   DateTimeStyles.AllowWhiteSpaces, out var dto))
            agora = dto;

        return new(new Instrucao(m, janela, agora, Bandeira(raiz, "atualizar_agora")), null);
    }

    // ══ A JANELA ═════════════════════════════════════════════════════════════

    /// <summary>
    /// A faixa de horas em que este caixa tem PERMISSÃO de se trocar sozinho, em
    /// minutos desde a meia-noite, no relógio DA LOJA.
    ///
    /// Intervalo meio-aberto [início, fim): "05:00 às 07:00" acaba às 07:00 em ponto,
    /// e não às 07:01. Uma loja que abre às 7 não quer o caixa reiniciando às 7h00.
    /// </summary>
    public readonly record struct Janela(int InicioMin, int FimMin)
    {
        /// <summary>22:00–02:00 é janela de loja que fecha tarde, e é o caso que quebra
        /// a comparação ingênua (início &lt; fim).</summary>
        public bool CruzaMeiaNoite => FimMin <= InicioMin;

        public int DuracaoMin => CruzaMeiaNoite ? 1440 - InicioMin + FimMin : FimMin - InicioMin;

        public override string ToString()
            => $"{InicioMin / 60:00}:{InicioMin % 60:00} às {FimMin / 60:00}:{FimMin % 60:00}";
    }

    /// <summary>
    /// "05:00" / "5" / "05h30" / "05:00:00" viram minutos desde a meia-noite. Quem
    /// digita a janela é gente, no painel, e gente escreve de todo jeito.
    /// </summary>
    public static bool TentarLerHora(string? texto, out int minutos)
    {
        minutos = 0;
        if (string.IsNullOrWhiteSpace(texto)) return false;

        var t = texto.Trim().ToLowerInvariant().Replace('h', ':');
        if (t.EndsWith(':')) t = t[..^1];                 // "05h" → "05"
        var p = t.Split(':');
        if (p.Length is 0 or > 3) return false;

        if (!int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hh)) return false;
        var mm = 0;
        if (p.Length > 1 && !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out mm)) return false;
        // Os segundos são aceitos e ignorados: "05:00:00" é a cara de um `time` do
        // Postgres, e recusá-lo faria a janela morrer por causa do tipo da coluna.
        if (p.Length > 2 && !int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)) return false;

        if (hh is < 0 or > 23 || mm is < 0 or > 59) return false;
        minutos = hh * 60 + mm;
        return true;
    }

    /// <summary>
    /// A janela do painel, se ela fizer sentido.
    ///
    /// ⚠️ INÍCIO IGUAL AO FIM É RECUSADO, e essa é a recusa que mais importa aqui.
    /// "05:00 às 05:00" tem duas leituras — "nunca" e "o dia inteiro" — e uma delas
    /// dá ao painel autonomia permanente sobre a frente de caixa. Campo ambíguo cuja
    /// interpretação errada libera o dia inteiro não se interpreta: se recusa.
    /// </summary>
    public static bool TentarLerJanela(string? inicio, string? fim, out Janela janela)
    {
        janela = default;
        if (!TentarLerHora(inicio, out var i) || !TentarLerHora(fim, out var f)) return false;
        if (i == f) return false;
        janela = new Janela(i, f);
        return true;
    }

    /// <summary>Estamos dentro dela agora? <paramref name="horaDaLoja"/> é a hora do
    /// dia no relógio DA LOJA (ver <see cref="RelogioDaLoja"/>).</summary>
    public static bool DentroDaJanela(Janela j, TimeSpan horaDaLoja)
        => MinutosAteFechar(j, horaDaLoja) > 0;

    /// <summary>Quanto ainda resta de janela, em minutos. 0 = está fora dela.</summary>
    public static int MinutosAteFechar(Janela j, TimeSpan horaDaLoja)
    {
        var min = (int)Math.Floor(horaDaLoja.TotalMinutes);
        if (min is < 0 or >= 1440) return 0;              // hora do dia impossível: não é hora
        if (j.CruzaMeiaNoite)
        {
            if (min >= j.InicioMin) return 1440 - min + j.FimMin;
            return min < j.FimMin ? j.FimMin - min : 0;
        }
        return min >= j.InicioMin && min < j.FimMin ? j.FimMin - min : 0;
    }

    /// <summary>Janela mínima para COMEÇAR um download. 265 MB na internet de loja não
    /// cabem em 5 minutos, e começar sabendo que não cabe é saturar o link da loja bem
    /// na hora em que ela abre.</summary>
    public const int MinimoParaBaixar = 15;

    /// <summary>Janela mínima para TROCAR quando o arquivo já está baixado e conferido.
    /// A troca em si é entregar o exe e sair: segundos.</summary>
    public const int MinimoParaTrocar = 2;

    /// <summary>Cabe fazer isto no que resta de janela?</summary>
    public static bool CabeNaJanela(int minutosDeJanela, bool jaBaixado)
        => minutosDeJanela >= (jaBaixado ? MinimoParaTrocar : MinimoParaBaixar);

    // ══ O RELÓGIO ════════════════════════════════════════════════════════════

    /// <summary>
    /// A HORA DA LOJA — e por que ela NÃO vem de <c>DateTime.Now</c>.
    ///
    /// PC de balcão tem relógio errado. Pilha de placa-mãe velha, fuso trocado na
    /// instalação, horário de verão que ninguém desligou: nada disso é raro, e o
    /// sintoma normal (a NFC-e recusada pela SEFAZ por diferença de horário) só
    /// aparece na venda. Pendurar "atualiza entre 05h e 07h" nesse relógio é aceitar
    /// que uma máquina com 8 horas de erro feche a frente de caixa às 13h de sábado.
    ///
    /// A ÂNCORA É O SERVIDOR. O painel responde `agora` já no fuso da loja (ele sabe
    /// de que loja o terminal é; o caixa não precisa de banco de fusos horários para
    /// nada). A partir daí o tempo passa por um contador MONOTÔNICO — o mesmo que
    /// conta desde o boot da máquina, que não anda para trás quando alguém acerta o
    /// relógio no meio do caminho.
    ///
    /// A ÂNCORA VENCE EM 30 MINUTOS, e isso é uma decisão, não um detalhe: o vigia
    /// pergunta ao painel a cada 15 min, então em operação normal ela está sempre
    /// fresca. Quando a internet cai, ela vence — e o caixa PERDE O DIREITO de se
    /// atualizar sozinho. É a propriedade que se quer: autonomia exige painel vivo.
    /// Ninguém se troca sozinho com base numa ordem de ontem.
    /// </summary>
    public sealed class RelogioDaLoja
    {
        public static readonly TimeSpan ValidadeDaAncora = TimeSpan.FromMinutes(30);

        private readonly DateTimeOffset _ancora;
        private readonly long _msDaAncora;
        private readonly Func<long> _ms;

        private RelogioDaLoja(DateTimeOffset ancora, Func<long> ms)
        {
            _ancora = ancora;
            _ms = ms;
            _msDaAncora = ms();
        }

        /// <summary>null quando o painel não disse que horas são na loja — e sem isso
        /// não existe janela. <paramref name="milissegundos"/> é o contador monotônico;
        /// só o teste passa outro.</summary>
        public static RelogioDaLoja? Ancorar(DateTimeOffset? agoraNaLoja, Func<long>? milissegundos = null)
            => agoraNaLoja is { } a ? new RelogioDaLoja(a, milissegundos ?? (() => Environment.TickCount64)) : null;

        public TimeSpan Decorrido => TimeSpan.FromMilliseconds(_ms() - _msDaAncora);

        /// <summary>O INSTANTE na loja (com fuso), ou null com a âncora vencida. É por
        /// aqui que se mede o erro do relógio da máquina — ver <see cref="DesvioDoRelogio"/>.</summary>
        public DateTimeOffset? AgoraNaLoja => Vencido ? null : _ancora + Decorrido;

        /// <summary>Ancoragem velha demais para mandar em alguma coisa. Decorrido
        /// negativo entra aqui também: contador que anda para trás é contador que não
        /// dá para usar (suspensão, troca de núcleo, máquina virtual mal comportada).</summary>
        public bool Vencido => Decorrido < TimeSpan.Zero || Decorrido >= ValidadeDaAncora;

        /// <summary>A hora do dia na loja, ou null quando a âncora venceu.</summary>
        public TimeSpan? HoraDaLoja => Vencido ? null : (_ancora + Decorrido).TimeOfDay;
    }

    /// <summary>Acima disto o relógio da máquina não é ruído de rede, é relógio errado —
    /// e vale contar ao painel, porque a mesma diferença rejeita NFC-e na SEFAZ.</summary>
    public static readonly TimeSpan DesvioQueImporta = TimeSpan.FromMinutes(5);

    /// <summary>Quanto o relógio DESTA MÁQUINA está adiantado em relação ao da loja.
    /// Positivo = a máquina está na frente. null = o painel não disse a hora.</summary>
    public static TimeSpan? DesvioDoRelogio(DateTimeOffset? agoraNaLoja, DateTimeOffset agoraNaMaquina)
        => agoraNaLoja is { } a ? agoraNaMaquina - a : null;

    // ══ A DECISÃO SEM NINGUÉM ════════════════════════════════════════════════

    /// <summary>Por que este caixa pode (ou não pode) se trocar sozinho agora.</summary>
    public enum Autonomia
    {
        /// <summary>O painel não respondeu, ou quem respondeu foi o arquivo estático.</summary>
        SemInstrucao,
        /// <summary>Não há versão nova liberada para este terminal.</summary>
        EmDia,
        /// <summary>Este terminal não tem janela configurada — e sem janela não há
        /// troca sozinha. É o padrão, e é o padrão CERTO.</summary>
        SemJanela,
        /// <summary>Tem janela; agora não é dentro dela.</summary>
        ForaDaJanela,
        /// <summary>Não dá para saber que horas são na loja. Ver <see cref="RelogioDaLoja"/>.</summary>
        SemRelogio,
        /// <summary>A janela deixou; o PORTÃO não deixou.</summary>
        Impedido,
        /// <summary>Pode. As duas perguntas responderam sim.</summary>
        Sim,
    }

    /// <param name="MinutosDeJanela">Quanto tempo ainda cabe fazer coisa. Vira o prazo
    /// do download: quando ele acaba, o download é CANCELADO (e o pedaço fica no disco
    /// para a noite seguinte continuar), nunca a troca é feita fora da hora.</param>
    public sealed record VeredictoSozinho(
        Autonomia Autonomia, string Motivo,
        Manifesto? Manifesto = null, int MinutosDeJanela = 0,
        Impedimento Impedimento = Impedimento.Nenhum)
    {
        public bool Pode => Autonomia == Autonomia.Sim;
    }

    /// <summary>Prazo de um terminal MARCADO no painel ("atualizar agora"). Ele não tem
    /// janela para respeitar, mas tem que ter um fim: download que corre para sempre é
    /// download que ninguém percebe que não termina.</summary>
    public const int MinutosDoMarcado = 240;

    /// <summary>
    /// "Posso me trocar sozinho, agora, sem ninguém clicar?"
    ///
    /// A ORDEM DAS PERGUNTAS NÃO É ARBITRÁRIA — ela é a ordem em que o dono vai LER a
    /// resposta no painel. "Está impedido" só é informação útil depois de "tem versão
    /// nova e é a hora dela"; o contrário encheria o painel de 40 caixas "impedidos"
    /// que na verdade só estão em dia.
    ///
    /// ⚠️ O PORTÃO É O ÚLTIMO E É INTEIRO. Nem a janela, nem "obrigatória", nem
    /// "atualizar agora" marcado pelo dono passam por cima dele. A janela responde
    /// "POSSO agora?"; <see cref="ImpedeSozinho"/> responde "é SEGURO agora?". As duas
    /// precisam dizer sim, e é por isso que elas são duas funções e não uma.
    /// </summary>
    public static VeredictoSozinho DecidirSozinho(
        EstadoDoCaixa estado, string? versaoInstalada,
        Instrucao? instrucao, TimeSpan? horaDaLoja)
    {
        if (instrucao is null)
            return new(Autonomia.SemInstrucao, "o painel não respondeu");

        // ARQUIVO NÃO MANDA REINICIAR CAIXA. O versao.json é igual para as 40 lojas e
        // qualquer um que escreva naquele nginx passaria a poder derrubar as 40 frentes
        // de caixa ao mesmo tempo. Ele informa (e o botão continua funcionando com ele);
        // agendar é privilégio de quem sabe de que terminal está falando.
        if (instrucao.Origem != Origem.Painel)
            return new(Autonomia.SemInstrucao,
                "a versão veio do arquivo público, que não sabe de que caixa está falando");

        if (instrucao.Manifesto is not { } m)
            return new(Autonomia.EmDia, "o painel não tem versão liberada para este caixa");

        if (Comparar(versaoInstalada, m.Versao) >= 0)
            return new(Autonomia.EmDia, $"já está na {Mostrar(versaoInstalada)}", m);

        int minutos;
        if (instrucao.AtualizarAgora)
        {
            // O dono marcou ESTE terminal, olhando para ele no painel. Isso dispensa a
            // janela — é literalmente o pedido "esse aí, agora" — e não dispensa mais
            // nada. O relógio também não é exigido aqui: a marcação vale porque acabou
            // de chegar na resposta, e ela é reconferida antes da troca.
            minutos = MinutosDoMarcado;
        }
        else
        {
            if (instrucao.Janela is not { } j)
                return new(Autonomia.SemJanela,
                    "este caixa não tem janela de atualização: só troca pelo botão", m);

            // Sem relógio confiável a janela não abre. É a escolha entre dois defeitos:
            // uma janela que NUNCA abre deixa a loja um dia a mais na versão velha, e o
            // dono ENXERGA isso (o caixa reporta a versão que está rodando) e resolve
            // com um toque no botão. Uma janela que abre NA HORA ERRADA fecha a frente
            // de caixa no meio do almoço de sábado. Entre o defeito visível e reversível
            // e o defeito invisível e caro, escolhe-se o visível.
            if (horaDaLoja is not { } hora)
                return new(Autonomia.SemRelogio,
                    "não dá para saber que horas são na loja: a janela não abre no escuro", m);

            minutos = MinutosAteFechar(j, hora);
            if (minutos <= 0)
                return new(Autonomia.ForaDaJanela, $"agora não é a janela ({j})", m);
        }

        var impede = ImpedeSozinho(estado);
        if (impede != Impedimento.Nenhum)
            return new(Autonomia.Impedido, NomeDoImpedimento(impede), m, minutos, impede);

        return new(Autonomia.Sim, instrucao.AtualizarAgora ? "marcado no painel" : "dentro da janela",
                   m, minutos);
    }

    // ══ O QUE O CAIXA CONTA DE VOLTA ═════════════════════════════════════════

    /// <summary>
    /// O corpo da pergunta ao painel — que é, na mesma viagem, o RELATÓRIO deste caixa.
    ///
    /// POR QUE JUNTO E NÃO NUM CANAL NOVO: o painel só consegue responder "qual é a sua
    /// versão" se souber em qual o terminal está (é assim que se libera loja por loja),
    /// então a versão instalada JÁ PRECISA ir na pergunta. Reportar é de graça: mesma
    /// requisição, mesmo token, nenhum canal a mais para alguém manter.
    ///
    /// E o que se ganha por ser junto, e não "no boot": a pergunta se repete a cada 15
    /// minutos, então o painel nunca está mais do que um ciclo atrasado. Depois de uma
    /// troca bem-sucedida, o PRIMEIRO ciclo do caixa novo já reporta a versão nova — o
    /// dono vê a onda fechar sozinha. Reportar só no boot deixaria o painel achando que
    /// a loja continua na versão velha até alguém desligar a máquina.
    ///
    /// O estado vai junto porque é a resposta para a única pergunta que sobra quando o
    /// dono olha o painel e vê um caixa parado na versão antiga: POR QUÊ. Sem isso ele
    /// republica às cegas em cima de um caixa que estava com comanda aberta.
    /// </summary>
    public static string CorpoDaPergunta(
        string? terminalUuid, string? lojaId, string? versaoInstalada,
        EstadoDoCaixa? estado, TimeSpan? desvioDoRelogio)
    {
        var impede = estado is null ? Impedimento.EstadoDesconhecido : ImpedeSozinho(estado);
        return JsonSerializer.Serialize(new
        {
            _produto = NomeDoProduto,
            _terminal_uuid = terminalUuid,
            _loja_id = lojaId,
            _versao = string.IsNullOrWhiteSpace(versaoInstalada) ? null : versaoInstalada.Trim(),
            _estado = new
            {
                pode_trocar_agora = impede == Impedimento.Nenhum,
                impedimento = NomeDoImpedimento(impede),
                turno_aberto = estado?.CaixaAberto ?? false,
                vendas_por_subir = estado?.VendasPorSubir ?? 0,
                // Segundos, e não texto: o painel precisa ORDENAR por isto para achar
                // as máquinas de relógio torto antes de a SEFAZ recusar a nota delas.
                desvio_relogio_seg = desvioDoRelogio is { } d ? (int)Math.Round(d.TotalSeconds) : (int?)null,
            },
        });
    }

    // ══ PROGRESSO ════════════════════════════════════════════════════════════

    /// <summary>Quanto já veio. <paramref name="Total"/> null = o servidor não disse.</summary>
    public sealed record Andamento(long Baixados, long? Total)
    {
        public int? Porcento => Total is > 0 ? (int)(Baixados * 100 / Total.Value) : null;
    }

    /// <summary>
    /// A frase que o operador lê enquanto espera. Em MB e honesta: rede de loja cai,
    /// e uma barra que anda é a diferença entre "está baixando" e "travou, vou fechar
    /// essa janela" — que é justamente o clique que não pode acontecer aqui.
    /// Sem Content-Length não se inventa porcentagem: diz só o que se sabe.
    /// </summary>
    public static string TextoDoProgresso(Andamento a)
    {
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        string Mb(long b) => (b / 1048576.0).ToString("N1", pt) + " MB";
        return a.Total is > 0
            ? $"Baixando… {Mb(a.Baixados)} de {Mb(a.Total.Value)}  ·  {a.Porcento}%"
            : $"Baixando… {Mb(a.Baixados)}";
    }

    // ══ INTEGRIDADE ══════════════════════════════════════════════════════════

    /// <summary>
    /// Menor tamanho que um instalador de verdade pode ter. O instalador empacotado
    /// tem ~265 MB; este piso existe contra o caso que mais acontece na prática: o
    /// servidor devolve 200 com uma página de erro, ou o proxy da loja entrega o HTML
    /// do portal cativo. 2 KB de HTML passariam em qualquer conferência de "baixou
    /// inteiro" — o Content-Length bate, o arquivo está completo, e é lixo.
    /// </summary>
    public const long TamanhoMinimoPlausivel = 1_000_000;

    /// <summary>
    /// O arquivo baixado é MESMO o instalador? Devolve null quando sim.
    ///
    /// COM sha256 no manifesto: a resposta é definitiva — byte por byte.
    ///
    /// SEM sha256 (o contrato de hoje): sobra o plano B, e ele é honestamente mais
    /// fraco. Três perguntas: (a) o arquivo tem o tamanho que o servidor prometeu no
    /// Content-Length — pega o download interrompido, que é a falha comum na rede de
    /// loja; (b) ele é grande o bastante para ser um instalador; (c) ele é um
    /// executável de Windows de verdade (assinatura MZ + cabeçalho PE) — pega página
    /// de erro, HTML de portal cativo e arquivo truncado no meio.
    ///
    /// O QUE FICA DESCOBERTO SEM O HASH: troca deliberada do arquivo. Nada aqui
    /// distingue o instalador certo de outro executável válido do mesmo tamanho. Só
    /// o hash (e, melhor ainda, assinatura Authenticode conferida antes de executar)
    /// fecha isso. Por isso o campo é pedido no manifesto — e por isso a ausência
    /// dele não é tratada como "está tudo bem", e sim como "conferi o que dava".
    /// </summary>
    public static string? Conferir(string caminho, Manifesto m, long? contentLength)
    {
        FileInfo fi;
        try { fi = new FileInfo(caminho); } catch { return "Não consegui ler o arquivo baixado."; }
        if (!fi.Exists) return "O arquivo baixado sumiu antes de instalar.";

        if (m.Tamanho is { } prometido && fi.Length != prometido)
            return $"O arquivo baixado tem {fi.Length} bytes e deveria ter {prometido}. "
                 + "O download não veio inteiro.";

        if (contentLength is { } esperado && fi.Length != esperado)
            return $"O download parou no meio ({fi.Length} de {esperado} bytes). "
                 + "Verifique a internet da loja e tente de novo.";

        if (fi.Length < TamanhoMinimoPlausivel)
            return "O que baixou é pequeno demais para ser o instalador "
                 + $"({fi.Length} bytes). O servidor pode ter devolvido uma página de erro.";

        if (!EhExecutavelWindows(caminho))
            return "O arquivo baixado não é um programa do Windows. "
                 + "O servidor pode ter devolvido outra coisa no lugar do instalador.";

        if (m.Sha256 is { Length: 64 } esperadoHash)
        {
            string obtido;
            try { obtido = Sha256Do(caminho); }
            catch { return "Não consegui conferir a impressão digital do arquivo baixado."; }
            if (!obtido.Equals(esperadoHash, StringComparison.OrdinalIgnoreCase))
                return "O arquivo baixado NÃO confere com o que o servidor anunciou. "
                     + "Por segurança ele foi descartado e nada foi instalado.";
        }

        return null;
    }

    public static string Sha256Do(string caminho)
    {
        using var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// "MZ" no começo e "PE\0\0" onde o cabeçalho DOS aponta. É o mínimo que separa um
    /// executável de uma página de erro — e não custa nada: lê 4 bytes em duas posições.
    /// </summary>
    public static bool EhExecutavelWindows(string caminho)
    {
        try
        {
            using var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 0x40) return false;
            var cab = new byte[4];
            if (fs.Read(cab, 0, 2) != 2 || cab[0] != (byte)'M' || cab[1] != (byte)'Z') return false;

            fs.Position = 0x3C;
            if (fs.Read(cab, 0, 4) != 4) return false;
            var pe = BitConverter.ToUInt32(cab, 0);
            if (pe + 4 > fs.Length) return false;

            fs.Position = pe;
            if (fs.Read(cab, 0, 4) != 4) return false;
            return cab[0] == (byte)'P' && cab[1] == (byte)'E' && cab[2] == 0 && cab[3] == 0;
        }
        catch { return false; }
    }

    // ══ DOWNLOAD ═════════════════════════════════════════════════════════════

    /// <summary>
    /// Quanto tempo sem CHEGAR UM BYTE antes de desistir. Não é o prazo total: um
    /// instalador de 265 MB no wi-fi ruim da loja pode levar meia hora legitimamente,
    /// e um prazo total mataria justo o download que estava dando certo. O que denuncia
    /// rede morta é o silêncio, não a lentidão.
    /// </summary>
    public static readonly TimeSpan EsperaSemBytes = TimeSpan.FromSeconds(60);

    /// <summary>O manifesto é pequeno: se demorar 15 s, a rede não está lá.</summary>
    public static readonly TimeSpan PrazoDoManifesto = TimeSpan.FromSeconds(15);

    /// <param name="Caminho">Onde o instalador ficou pronto. null quando falhou.</param>
    /// <param name="Retomado">true quando aproveitou o pedaço de uma tentativa anterior.</param>
    public sealed record Baixa(string? Caminho, string? Erro, long Bytes = 0, bool Retomado = false)
    {
        public bool Ok => Caminho is not null && Erro is null;
    }

    /// <summary>
    /// Busca o manifesto. Erro de rede vira frase de balcão, nunca exceção na tela.
    /// </summary>
    public static async Task<LeituraManifesto> ConsultarAsync(
        HttpClient http, string urlDoManifesto, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PrazoDoManifesto);
            // sem cache: proxy de loja adora guardar json e servir a versão de ontem
            using var req = new HttpRequestMessage(HttpMethod.Get, urlDoManifesto);
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var r = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode)
                return new(null, $"O servidor de atualização respondeu {(int)r.StatusCode}. "
                               + "Tente de novo mais tarde.");
            var json = await r.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return LerManifesto(json, urlDoManifesto);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(null, "Consulta cancelada.");
        }
        catch (OperationCanceledException)
        {
            return new(null, "O servidor de atualização não respondeu a tempo. "
                           + "Confira a internet da loja.");
        }
        catch (Exception ex)
        {
            return new(null, "Não consegui falar com o servidor de atualização: " + ex.Message);
        }
    }

    /// <summary>
    /// Baixa o instalador para o TEMP, com retomada, e só devolve caminho depois de
    /// <see cref="Conferir"/> aprovar.
    ///
    /// RETOMADA (regra da rede de loja): o pedaço já baixado fica em <c>.parcial</c> e a
    /// tentativa seguinte pede <c>Range: bytes=N-</c>. Se o servidor aceitar (206),
    /// continua de onde parou; se ignorar (200) ou reclamar (416), recomeça do zero em
    /// silêncio — o que não pode acontecer é colar bytes novos num pedaço velho e
    /// entregar um Frankenstein para instalar. Por isso o nome do parcial carrega a
    /// VERSÃO: pedaço de outra versão nunca é aproveitado.
    ///
    /// ⚠️ A retomada é O motivo pelo qual o <c>sha256</c> importa mais do que parece.
    /// O pedaço local nunca tem bytes ERRADOS (escrita sequencial só cresce com o que
    /// foi gravado), mas a máquina de loja perde energia, e queda de energia no meio da
    /// escrita pode deixar o fim do arquivo com zeros que o NTFS considera gravados.
    /// Aí o tamanho fecha, o Content-Length fecha, e só o hash acusa.
    ///
    /// NADA AQUI ENCOSTA NO PROGRAMA EM USO. Falhou? O caixa continua exatamente como
    /// estava, com um arquivo pela metade no TEMP que a próxima tentativa aproveita ou
    /// descarta.
    /// </summary>
    public static async Task<Baixa> BaixarAsync(
        HttpClient http, Manifesto m, string? pasta = null,
        IProgress<Andamento>? andamento = null, CancellationToken ct = default,
        TimeSpan? esperaSemBytes = null,
        // Trava da retomada: só o próprio método liga isto, ao recomeçar do zero depois
        // de um 416. Existe para a repetição ser UMA, e não uma pilha (ver o bloco do
        // 416 abaixo). Quem chama de fora nunca precisa passar.
        bool jaRecomecou = false)
    {
        var destinoPasta = pasta ?? PastaTemp;
        string parcial, pronto;
        try
        {
            Directory.CreateDirectory(destinoPasta);
            LimparDeOutrasVersoes(destinoPasta, m.Versao);
            parcial = Path.Combine(destinoPasta, $"InstalarPdv-{Seguro(m.Versao)}.parcial");
            pronto = CaminhoPronto(destinoPasta, m.Versao);
        }
        catch (Exception ex)
        {
            return new Baixa(null, "Não consegui preparar a pasta de download: " + ex.Message);
        }

        // Já pronto de uma tentativa anterior (o dono recusou o UAC, por exemplo):
        // não baixa 265 MB de novo — mas re-confere, porque o arquivo passou por disco.
        if (File.Exists(pronto) && Conferir(pronto, m, null) is null)
            return new Baixa(pronto, null, new FileInfo(pronto).Length, Retomado: true);

        long jaTem = 0;
        try { if (File.Exists(parcial)) jaTem = new FileInfo(parcial).Length; } catch { jaTem = 0; }

        var espera = esperaSemBytes ?? EsperaSemBytes;
        long? total = null;
        var retomou = false;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, m.Url);
            if (jaTem > 0) req.Headers.Range = new RangeHeaderValue(jaTem, null);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(espera);
            using var r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                                    .ConfigureAwait(false);

            if (r.StatusCode == HttpStatusCode.NotFound)
                return new Baixa(null,
                    "O instalador não está publicado no servidor (erro 404). "
                    + "A versão foi anunciada mas o arquivo não subiu. Avise o suporte.");

            if (r.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // O pedaço local não vale mais (arquivo trocado no servidor). Recomeça —
                // UMA vez só.
                //
                // ⚠️ ESTA CHAMADA JÁ FOI RECURSÃO SEM GUARDA, e era a única falha deste
                // arquivo que nenhum try/catch aqui seguraria: servidor que responde 416
                // de novo (por configuração, por proxy, por bug dele) empilharia
                // BaixarAsync até estourar a pilha, e estouro de pilha NÃO é exceção —
                // derruba o processo. A frente de caixa sumindo da tela no meio do
                // movimento é exatamente o desfecho que este botão inteiro existe para
                // evitar. Depois de apagar o parcial não há mais Range para mandar, então
                // uma repetição basta; a segunda vira erro com texto, não queda.
                Apagar(parcial);
                if (jaRecomecou)
                    return new Baixa(null,
                        "O servidor recusou continuar o download de onde parou (erro 416) "
                        + "mesmo começando do zero. Tente de novo mais tarde; se insistir, avise o suporte.");
                return await BaixarAsync(http, m, destinoPasta, andamento, ct, esperaSemBytes,
                                         jaRecomecou: true).ConfigureAwait(false);
            }

            if (!r.IsSuccessStatusCode)
                return new Baixa(null, $"O servidor respondeu {(int)r.StatusCode} ao baixar o instalador. "
                                     + "Tente de novo mais tarde.");

            var acrescentando = r.StatusCode == HttpStatusCode.PartialContent && jaTem > 0;
            if (!acrescentando) jaTem = 0;      // 200 = veio do começo; o parcial é lixo
            retomou = acrescentando;

            var corpo = r.Content.Headers.ContentLength;
            total = r.Content.Headers.ContentRange?.Length
                 ?? (corpo is { } c ? jaTem + c : null);

            // Tamanho anunciado no manifesto x tamanho que o servidor vai mandar:
            // discordância aqui é 265 MB de rede de loja gastos à toa.
            if (m.Tamanho is { } prometido && total is { } t2 && t2 != prometido)
                return new Baixa(null,
                    $"O servidor está oferecendo um arquivo de {t2} bytes, mas anunciou {prometido}. "
                    + "Nada foi instalado. Avise o suporte.");

            if (total is { } t3 && t3 < TamanhoMinimoPlausivel)
                return new Baixa(null,
                    "O que o servidor está oferecendo é pequeno demais para ser o instalador. "
                    + "Pode ser uma página de erro. Avise o suporte.");

            using (var origem = await r.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
            using (var arquivo = new FileStream(parcial,
                       acrescentando ? FileMode.Append : FileMode.Create,
                       FileAccess.Write, FileShare.None, 81920))
            {
                var buf = new byte[81920];
                var baixados = jaTem;
                andamento?.Report(new Andamento(baixados, total));
                while (true)
                {
                    // O relógio de "silêncio" reinicia a CADA pedaço que chega: quem
                    // está baixando devagar continua; quem parou de receber morre.
                    cts.CancelAfter(espera);
                    var lidos = await origem.ReadAsync(buf, cts.Token).ConfigureAwait(false);
                    if (lidos <= 0) break;
                    await arquivo.WriteAsync(buf.AsMemory(0, lidos), ct).ConfigureAwait(false);
                    baixados += lidos;
                    andamento?.Report(new Andamento(baixados, total));
                }
                await arquivo.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelamento do operador: o parcial FICA, para a próxima retomar.
            return new Baixa(null, "Download cancelado. O que já baixou fica guardado para a próxima vez.");
        }
        catch (OperationCanceledException)
        {
            return new Baixa(null,
                $"A internet parou de responder no meio do download (mais de {espera.TotalSeconds:N0}s sem receber nada). "
                + "O que já baixou fica guardado: toque em Atualizar de novo e ele continua de onde parou.");
        }
        catch (Exception ex)
        {
            return new Baixa(null, "O download falhou: " + ex.Message
                                 + "\n\nO caixa não foi alterado. Tente de novo mais tarde.");
        }

        // ── A conferência. Daqui não passa download pela metade.
        if (Conferir(parcial, m, total) is { } ruim)
        {
            // Arquivo reprovado NÃO fica no disco: senão a próxima tentativa "retoma"
            // um arquivo que já se sabe errado, e o erro vira permanente e inexplicável.
            Apagar(parcial);
            return new Baixa(null, ruim);
        }

        try
        {
            Apagar(pronto);
            File.Move(parcial, pronto);
        }
        catch (Exception ex)
        {
            return new Baixa(null, "Não consegui preparar o instalador baixado: " + ex.Message);
        }

        long tamanho;
        try { tamanho = new FileInfo(pronto).Length; } catch { tamanho = 0; }
        return new Baixa(pronto, null, tamanho, retomou);
    }

    private static string CaminhoPronto(string pasta, string versao)
        => Path.Combine(pasta, $"InstalarPdv-{Seguro(versao)}.exe");

    /// <summary>
    /// O instalador DESTA versão já está baixado e aprovado no TEMP? Devolve o caminho,
    /// ou null.
    ///
    /// É o que separa "preciso de 15 minutos de janela" (baixar 265 MB) de "preciso de
    /// 2" (entregar um arquivo que já está no disco) — ver <see cref="CabeNaJanela"/>.
    /// Confere de novo em vez de confiar na existência do arquivo: entre o download de
    /// ontem e a janela de hoje o disco passou por uma noite, e o instalador é o que
    /// vai rodar como administrador.
    /// </summary>
    public static string? JaBaixado(Manifesto m, string? pasta = null)
    {
        try
        {
            var pronto = CaminhoPronto(pasta ?? PastaTemp, m.Versao);
            return File.Exists(pronto) && Conferir(pronto, m, null) is null ? pronto : null;
        }
        catch { return null; }
    }

    /// <summary>Downloads de versões que não interessam mais. PC de loja tem disco
    /// pequeno, e 265 MB por versão abandonada enchem ele em três releases.</summary>
    private static void LimparDeOutrasVersoes(string pasta, string versao)
    {
        var manter = Seguro(versao);
        foreach (var f in Directory.GetFiles(pasta, "InstalarPdv-*"))
        {
            var nome = Path.GetFileNameWithoutExtension(f);
            if (!nome.Equals("InstalarPdv-" + manter, StringComparison.OrdinalIgnoreCase))
                Apagar(f);
        }
    }

    /// <summary>A versão vem da rede e vira NOME DE ARQUIVO. Sem esta peneira, um
    /// manifesto com "..\..\Windows\System32\algo" escreveria fora da pasta temporária.</summary>
    internal static string Seguro(string versao)
    {
        var limpo = new string(versao.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        return limpo.Length == 0 ? "nova" : limpo;
    }

    private static void Apagar(string caminho)
    {
        try { if (File.Exists(caminho)) File.Delete(caminho); } catch { }
    }
}
