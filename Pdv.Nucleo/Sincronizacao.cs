using Dapper;

namespace Pdv.Nucleo;

/// <summary>
/// UMA venda parada, com nome. Existe porque "3 vendas" não é informação para quem
/// está no balcão: o dono leu isso no dia 29/08 e a primeira pergunta dele foi "QUE
/// vendas são essas?" — não dava para conferir nem para contar ao gerente.
/// </summary>
/// <param name="Numero">
/// <c>numero_local</c>: o número que o operador GRITA no balcão e que sai impresso no
/// cupom. É por ele que a venda é reconhecida na loja — o id (GUID) não serve para nada
/// nessa conversa.
/// </param>
/// <param name="Dia">
/// <c>business_date</c> (dia OPERACIONAL, vira às 05h). Vai junto porque o número se
/// repete todo dia: "nº 3" sozinho é ambíguo assim que o caixa passa da meia-noite.
/// </param>
public sealed record VendaParada(long Numero, string Dia, bool Desistiu);

/// <summary>
/// A ÚNICA pergunta que o aviso precisa responder: o que dá para fazer com o que
/// travou. Existe porque até 08/09/2026 não existia — 409 de operador que falta no
/// painel e 400 de registro que o painel nunca vai aceitar caíam no MESMO balde, e o
/// aviso mandava a mesma coisa para os dois: "chame o gerente e toque em Sincronizar".
///
/// No segundo caso isso é instrução falsa. O dono tocou, e cada toque reabria 8 linhas,
/// gastava 8 chamadas que não podiam dar certo e as devolvia mortas com o contador
/// maior. Aviso que manda fazer o que não adianta é pior que aviso nenhum: ensina a
/// não ler o próximo.
/// </summary>
public enum SaidaParada
{
    /// <summary>Ainda na fila. Sobe sozinha, ninguém precisa fazer nada.</summary>
    Sozinha,
    /// <summary>Alguém arruma a causa (cadastro no painel, versão do caixa) e o próximo envio leva.</summary>
    Resolver,
    /// <summary>Ficou dias sem falar com o painel. Não há o que arrumar: é a internet voltar.</summary>
    Espera,
    /// <summary>
    /// O painel NUNCA vai aceitar este registro do jeito que ele está gravado. Tentar de
    /// novo é gastar chamada. Só dispensando.
    /// </summary>
    SemConserto,
}

/// <summary>
/// Vendas gravadas no caixa que a nuvem NÃO confirmou. Em duas situações bem
/// diferentes, e por isso separadas:
///  · <paramref name="Aguardando"/> — o dreno ainda vai tentar. Some sozinho.
///  · <paramref name="Desistidas"/> — o dreno PAROU de tentar (dead-letter). NÃO some
///    sozinho: alguém precisa reconciliar à mão.
/// </summary>
/// <param name="Valor">
/// Quanto está parado. Existe porque contagem sozinha não dimensiona nada: "3 vendas"
/// tanto pode ser R$ 12,00 de café quanto os R$ 2.493,00 do roteiro de hoje, e é o
/// valor que decide se isso é recado de fim de expediente ou telefonema agora.
/// </param>
/// <param name="Motivo">
/// POR QUE o envio desistiu, em português de balcão. O rastro cru é
/// <c>HTTP 409: {"code":"23503","details":"Key (operator_id)=(…) is not present in
/// table employees"}</c> — verdadeiro, e ilegível para quem está no caixa. Sem esta
/// tradução o aviso é um número sem causa, e número sem causa não vira ação.
/// </param>
/// <param name="Lista">
/// QUAIS vendas são, uma a uma, para o aviso poder nomeá-las. null (ou incompleta)
/// só degrada o texto: o aviso perde a linha "Quais:" e continua correto no resto.
/// </param>
/// <param name="ValorParado">
/// Quanto TRAVOU, sem somar o que ainda sobe sozinho. Existe porque a soma única
/// mentia no caso misto: R$ 100.089,00 na tela quando só R$ 24,00 precisavam de gente.
/// </param>
/// <param name="Saida">O que dá para fazer. Ver <see cref="SaidaParada"/>.</param>
public sealed record VendasParadas(
    int Aguardando, int Desistidas, Dinheiro Valor, Dinheiro ValorParado,
    string? Motivo = null, IReadOnlyList<VendaParada>? Lista = null,
    SaidaParada Saida = SaidaParada.Sozinha)
{
    public int Total => Aguardando + Desistidas;

    /// <summary>
    /// Quantos números cabem antes de a linha virar parede. 3 se lê; 40 não — vira
    /// um borrão que ninguém confere. Passando disto, o aviso diz quantas ficaram
    /// de fora e de que dias: cortar em silêncio é o mesmo defeito de novo.
    /// </summary>
    private const int MaxListadas = 6;

    private const string NL = "\n";

    /// <summary>
    /// O aviso que vai para a tela. null quando não há nada parado.
    ///
    /// A PRIMEIRA LINHA EXISTE PARA MATAR O SUSTO. Uma versão antiga abria com
    /// "3 venda(s) que o servidor não tem", e o dono leu exatamente o que estava
    /// escrito: que 3 vendas não se concretizaram. Não é isso: a venda aconteceu, o
    /// cliente levou o produto e o dinheiro entrou. No susto se cancela venda certa e
    /// se mexe em caixa fechado, e aí o aviso custa mais caro que o problema.
    ///
    /// O QUE MUDOU EM 08/09/2026, E POR QUÊ. O aviso tinha virado um bloco de onze
    /// linhas com rótulos em caixa alta, e o dono reprovou na hora: "pessimo, tanto de
    /// clareza quanto de quantidade de informacao". Duas coisas saíram:
    ///  · as linhas que não mudam o que ele faz nos próximos cinco minutos (o que muda
    ///    e o que não muda no painel, a contagem de tentativas, a repetição da garantia
    ///    em forma de lista);
    ///  · a instrução que era falsa. Ver <see cref="SaidaParada"/>: mandar tocar em
    ///    Sincronizar quando o painel nunca vai aceitar o registro não é só inútil,
    ///    é um moedor, e o texto agora sai do que foi MEDIDO no motivo.
    ///
    /// Sobrou o mínimo, nesta ordem: o dinheiro está certo, o tamanho do que travou, a
    /// causa, e o próximo passo (inclusive quando o passo é não fazer nada). Teto de
    /// quatro linhas, vigiado por teste.
    /// </summary>
    public string? Resumo
    {
        get
        {
            if (Total == 0) return null;

            // A GARANTIA VEM ANTES DE QUALQUER NÚMERO. O dono já leu "3 venda(s) que o
            // servidor não tem" e entendeu que 3 vendas não se concretizaram. Não é isso:
            // a venda aconteceu, o cliente levou, o dinheiro entrou. No susto se cancela
            // venda certa e se mexe em caixa fechado, e aí o aviso sai mais caro que o
            // problema que denuncia.
            var l = new List<string>();

            if (Desistidas == 0)
            {
                l.Add($"O dinheiro está certo. Falta o registro de {Aguardando} "
                      + $"{(Aguardando == 1 ? "venda" : "vendas")} subir para o painel ({Valor.Formatado()}).");
                l.Add(Aguardando == 1
                    ? "Sobe sozinha. Não precisa fazer nada no caixa."
                    : "Sobem sozinhas. Não precisa fazer nada no caixa.");
                return string.Join(NL, l);
            }

            l.Add("O dinheiro está certo. O que não subiu foi só o registro no painel.");

            // Os números só entram quando servem: no caso em que alguém vai procurar
            // venda por venda no painel. Onde não há o que fazer com elas, número é peso.
            var quais = Saida == SaidaParada.Resolver ? Nomear() : null;
            var travaram = $"Travaram {Desistidas} {(Desistidas == 1 ? "venda" : "vendas")}, "
                           + ValorParado.Formatado() + (quais is null ? "" : $" ({quais})") + ".";
            if (Aguardando > 0)
                travaram += $" {(Aguardando == 1 ? "Outra sobe sozinha" : $"Outras {Aguardando} sobem sozinhas")}.";
            l.Add(travaram);

            // O motivo é o MAIS COMUM entre as travadas, não o de todas: por isso ele é
            // dito como causa, sem prometer que explica cada uma.
            // "O painel recusou" só quando ELE recusou. Quando o caixa é que não
            // alcançou o painel, dizer que o painel recusou é mandar procurar culpa no
            // lugar errado.
            if (Motivo is { Length: > 0 } m)
                l.Add(Saida == SaidaParada.Espera ? $"O caixa {m}." : $"O painel recusou: {m}.");

            l.Add(Saida switch
            {
                // Tentar de novo aqui não é inofensivo: cada toque reabre a linha, gasta
                // uma chamada que não pode dar certo e recarimba a desistência.
                SaidaParada.SemConserto =>
                    "Tentar de novo não muda nada. Na Configuração dá para tirar essas vendas da fila.",
                SaidaParada.Espera => "Assim que a internet voltar, toque em Sincronizar.",
                _ => "Depois de resolver isso, toque em Sincronizar.",
            });

            return string.Join(NL, l);
        }
    }

    /// <summary>
    /// "nº 41 e 42, hoje" — os números pelos quais a venda é conhecida na loja,
    /// agrupados por dia (o número reinicia a cada dia operacional). Null quando não há
    /// lista: o aviso perde esta metade da linha e continua verdadeiro. O que ele não
    /// pode fazer é inventar número.
    /// </summary>
    private string? Nomear()
    {
        if (Lista is null) return null;
        var vendas = Lista.Where(v => v.Desistiu).ToList();
        if (vendas.Count == 0) return null;

        var mostradas = vendas.Take(MaxListadas).ToList();
        var texto = string.Join("; ", mostradas
            .GroupBy(v => v.Dia)
            .Select(g => "nº " + Juntar(g.Select(v => v.Numero.ToString())) + $", {Quando(g.Key)}"));

        // Quantas ficaram de fora vem do CONTADOR, não do tamanho da lista: a consulta
        // tem teto, e é melhor dizer "e mais 34" do que fingir que eram só as 6.
        var restam = Desistidas - mostradas.Count;
        if (restam > 0) texto += $", e mais {restam}";
        return texto;
    }

    /// <summary>"41, 42 e 43" — como se fala, não "41,42,43".</summary>
    private static string Juntar(IEnumerable<string> itens)
    {
        var v = itens.ToList();
        return v.Count <= 1 ? string.Concat(v)
            : string.Join(", ", v.Take(v.Count - 1)) + " e " + v[^1];
    }

    /// <summary>
    /// "hoje" para o dia operacional corrente (é o que o operador entende no balcão) e
    /// dd/MM para os outros. O dia bruto ("2026-08-29") só aparece se não for data.
    /// </summary>
    private static string Quando(string dia)
        => dia == Caixa.DiaOperacional() ? "hoje"
         : DateTime.TryParse(dia, System.Globalization.CultureInfo.InvariantCulture,
                             System.Globalization.DateTimeStyles.None, out var d) ? d.ToString("dd/MM")
         : dia;
}

/// <summary>O que a sincronização fez, para mostrar ao operador em uma tela só.</summary>
/// <param name="CatalogoMudou">
/// true quando produtos OU operadores mudaram DE VERDADE nesta passada. A baixada
/// regrava o catálogo inteiro sempre, então "quantos produtos desceram" não diz nada —
/// era por isso que sincronizar sem novidade repetia o relatório da vez anterior.
/// </param>
public sealed record ResultadoSync(
    int ProdutosBaixados, int FotosBaixadas, int NotasSubidas, int NotasPendentes,
    VendasParadas Vendas, bool CatalogoMudou, string? Erro)
{
    public bool Ok => Erro is null;

    /// <summary>Nada desceu, nada subiu, nada pendente: a resposta certa é "tudo em dia".</summary>
    public bool SemNovidade => Ok && !CatalogoMudou && FotosBaixadas == 0 && NotasSubidas == 0
        && NotasPendentes == 0 && Vendas.Total == 0;
}

/// <summary>
/// O botão "Sincronizar".
///
/// O PDV vende sem nuvem — catálogo, operadores e vendas moram no SQLite local, e é
/// assim que a loja não para quando a internet cai. A troca com o servidor é um ato
/// EXPLÍCITO: o gerente mexe no painel, o caixa aperta o botão, as tabelas atualizam.
///
/// Vai nas DUAS direções de propósito. Descer o catálogo é o que o dono pediu; subir o
/// XML da nota não é opcional — nota que fica só no HD do caixa não aparece na 2ª via
/// nem no extrato do contador, e a guarda de 5 anos passa a depender de um disco de loja.
/// </summary>
public static class Sincronizacao
{
    /// <param name="reenviarDesistidas">
    /// Só o TOQUE MANUAL no botão manda true. É o gesto "eu tratei o motivo, tenta de
    /// novo": as linhas em dead-letter voltam para UMA tentativa cada (o contador de
    /// tentativas NÃO é zerado, então uma recusa permanente as devolve ao estado
    /// terminal na mesma varredura, com o motivo novo gravado). O ciclo automático de
    /// 45 s passa false de propósito — fila morta batendo sozinha no servidor a cada
    /// varredura é exatamente o laço silencioso que este estado existe para impedir.
    /// </param>
    public static async Task<ResultadoSync> ExecutarAsync(
        Nuvem nuvem, GuardaNuvem? guarda, Drenagem? drenagem = null,
        IProgress<string>? andamento = null, CancellationToken ct = default,
        bool reenviarDesistidas = false)
    {
        var produtos = 0;
        var fotos = 0;
        var notas = 0;

        // Vendas da fila primeiro: são elas que alimentam os relatórios do painel.
        if (drenagem is not null)
        {
            if (reenviarDesistidas) Drenagem.ReabrirDesistidas();
            andamento?.Report("Enviando as vendas…");
            try { await drenagem.DrenarAsync(ct).ConfigureAwait(false); }
            catch { /* a fila fica para o próximo ciclo */ }
        }

        // Subir ANTES de baixar: se algo der errado no meio, o que já está no papel do
        // cliente é mais urgente que preço novo de produto. A subida exige identidade
        // (guarda nula = caixa ainda não pareado); o catálogo, não — ele desce pela
        // chave pública do app, então caixa recém-instalado sincroniza sem configurar nada.
        if (guarda is not null)
        {
            andamento?.Report("Enviando as notas emitidas…");
            try { notas = await guarda.SubirAsync(ct).ConfigureAwait(false); }
            catch { /* a subida nunca derruba a sincronização inteira */ }
        }

        var mudou = false;
        try
        {
            andamento?.Report("Baixando o catálogo…");
            using var cx = Banco.Abrir();
            var antes = ImpressaoDigital(cx);
            produtos = await nuvem.BaixarProdutosAsync(cx).ConfigureAwait(false);

            // promocoes descem SEMPRE junto do catalogo: foi a falta disto que
            // deixou o "donuts do dia" publicado no painel invisivel pro caixa
            andamento?.Report("Baixando as promoções…");
            var lojaPromo = cx.ExecuteScalar<string>("SELECT loja_nome FROM terminal LIMIT 1") ?? "";
            try { await nuvem.BaixarPromocoesAsync(cx, lojaPromo).ConfigureAwait(false); }
            catch { /* espelho anterior continua valendo */ }

            // combos com sub-escolhas descem logo depois, pelo mesmo motivo
            andamento?.Report("Baixando os combos…");
            try { await nuvem.BaixarCombosAsync(cx, lojaPromo).ConfigureAwait(false); }
            catch { /* espelho anterior continua valendo */ }

            // operadores criados no painel passam a logar no caixa (CPF + senha)
            andamento?.Report("Atualizando os operadores…");
            try { await nuvem.BaixarOperadoresAsync(cx).ConfigureAwait(false); }
            catch { /* sem identidade de escrita ainda: fica pro próximo ciclo */ }

            mudou = ImpressaoDigital(cx) != antes;

            // Fotos por último e sem prazo curto: é o que mais demora e é o que menos
            // importa — produto sem foto vende, produto com preço errado não.
            andamento?.Report("Atualizando as fotos…");
            var urls = cx.Query<string>(
                "SELECT foto_local FROM produto WHERE ativo = 1 AND foto_local IS NOT NULL").ToList();
            if (urls.Count > 0)
                fotos = await Fotos.BaixarFaltantesAsync(urls).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var (n2, v2) = Pendencias();
            return new ResultadoSync(produtos, fotos, notas, n2, v2, mudou, ex.Message);
        }

        var (nf, vd) = Pendencias();
        return new ResultadoSync(produtos, fotos, notas, nf, vd, mudou, null);
    }

    /// <summary>
    /// Impressão digital do que o PAINEL governa (produtos + operadores), SEM os
    /// carimbos de hora: a baixada regrava as linhas a cada sincronização, então
    /// comparar "atualizado" acusaria mudança sempre e o "tudo em dia" nunca sairia.
    /// </summary>
    internal static string ImpressaoDigital(Microsoft.Data.Sqlite.SqliteConnection cx)
    {
        var partes = cx.Query<string>("""
            SELECT id||'§'||nome||'§'||COALESCE(plu,'')||'§'||COALESCE(ean,'')||'§'||COALESCE(categoria,'')
                  ||'§'||preco_cent||'§'||unidade||'§'||COALESCE(foto_local,'')||'§'||COALESCE(ncm,'')
                  ||'§'||COALESCE(cest,'')||'§'||COALESCE(csosn,'')||'§'||COALESCE(cfop,'')
                  ||'§'||origem||'§'||pesavel||'§'||ativo
              FROM produto ORDER BY id
            """)
            .Concat(cx.Query<string>(
                "SELECT id||'§'||nome||'§'||pin_hash||'§'||perfil||'§'||ativo FROM operador ORDER BY id"))
            // promocao entra na digital: sem isto, publicar so promocao dizia
            // "tudo em dia" pro operador - o bug exato que o dono viu na quinta
            .Concat(cx.Query<string>("SELECT id||'§'||payload FROM promo ORDER BY id"))
            // combo tambem: cadastrar a composicao no painel tem que acordar o caixa
            .Concat(cx.Query<string>("SELECT produto_id||'§'||payload FROM combo ORDER BY produto_id"));
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n", partes))));
    }

    /// <summary>O que ainda não subiu. Vai na tela porque pendência invisível vira pendência eterna.</summary>
    public static (int notas, VendasParadas vendas) Pendencias()
    {
        var vendas = VendasNaoEntregues();
        try
        {
            using var cx = Banco.Abrir();
            var notas = cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM nfce_emissao WHERE chave IS NOT NULL AND sincronizada = 0");
            return (notas, vendas);
        }
        catch { return (0, vendas); }
    }

    /// <summary>
    /// Vendas que a nuvem NÃO confirmou — quantas E QUANTO.
    ///
    /// Só venda FINALIZADA entra. Venda cancelada que a nuvem nunca recebeu não é
    /// divergência: sem venda lá não há faturamento para neutralizar, o estado já é
    /// consistente. Contá-la inventaria um alarme — e alarme falso é o caminho mais
    /// curto para o operador parar de olhar o número.
    ///
    /// Pelo MESMO motivo, venda de HOMOLOGAÇÃO fica de fora. Ela não deve subir (o
    /// roteiro da PayGo viraria receita na DRE), e hoje nem é enfileirada — mas o caixa
    /// da loja carrega 3 linhas de quando esse filtro ainda não existia. Somá-las era
    /// R$ 2.493,00 de alarme que NENHUMA ação do operador conseguia zerar, para sempre.
    /// É a mesma regra do <see cref="Caixa.Apurado"/>, que já as tira do fechamento.
    /// </summary>
    public static VendasParadas VendasNaoEntregues()
    {
        try
        {
            using var cx = Banco.Abrir();
            var r = cx.QuerySingle($"""
                SELECT COALESCE(SUM(CASE WHEN {SqlDesistiu} THEN 0 ELSE 1 END), 0) AS aguardando,
                       COALESCE(SUM(CASE WHEN {SqlDesistiu} THEN 1 ELSE 0 END), 0) AS desistidas,
                       COALESCE(SUM(v.total_cent), 0)                              AS valor,
                       COALESCE(SUM(CASE WHEN {SqlDesistiu} THEN v.total_cent ELSE 0 END), 0) AS valor_parado
                  FROM outbox o
                  JOIN venda  v ON v.id = o.ref_id
                 WHERE o.tipo IN ('venda','venda_composta')
                   AND v.status = 'finalizada'
                   AND v.homologacao = 0
                   AND o.descartado_em IS NULL
                   AND (o.enviado_em IS NULL OR {SqlDesistiu})
                """);
            var desistidas = (int)r.desistidas;
            // O motivo mais comum entre as desistidas. Uma causa só, dita uma vez: o
            // operador não precisa de 16 linhas de rastro, precisa saber a quem ligar.
            var cru = desistidas == 0 ? null : cx.ExecuteScalar<string?>($"""
                SELECT o.ultimo_erro
                  FROM outbox o
                  JOIN venda  v ON v.id = o.ref_id
                 WHERE o.tipo IN ('venda','venda_composta') AND v.status = 'finalizada' AND v.homologacao = 0
                   AND o.descartado_em IS NULL
                   AND {SqlDesistiu}
                 GROUP BY o.ultimo_erro
                 ORDER BY COUNT(*) DESC
                 LIMIT 1
                """);
            var motivo = MotivoHumano(cru);
            // QUAIS vendas são. O mesmo WHERE do contador acima, palavra por palavra —
            // se as duas consultas divergirem, o aviso lista uma venda que ele mesmo
            // não contou, e aí ninguém acredita em nenhum dos dois números.
            //
            // O teto de 400 é rede de segurança para o caixa que passou semanas offline:
            // a lista mostra 6 números de qualquer jeito, e o "e mais N" sai do CONTADOR,
            // que não tem teto. Ou seja: o teto encurta a leitura, nunca a verdade.
            var lista = cx.Query($"""
                SELECT v.numero_local                              AS numero,
                       v.business_date                             AS dia,
                       CASE WHEN {SqlDesistiu} THEN 1 ELSE 0 END   AS desistiu
                  FROM outbox o
                  JOIN venda  v ON v.id = o.ref_id
                 WHERE o.tipo IN ('venda','venda_composta')
                   AND v.status = 'finalizada'
                   AND v.homologacao = 0
                   AND o.descartado_em IS NULL
                   AND (o.enviado_em IS NULL OR {SqlDesistiu})
                 ORDER BY desistiu DESC, v.business_date, v.numero_local
                 LIMIT 400
                """)
                .Select(x => new VendaParada((long)x.numero, (string)x.dia, (long)x.desistiu == 1))
                .ToList();

            var saida = desistidas == 0 ? SaidaParada.Sozinha : SaidaDoErro(cru);
            return new VendasParadas((int)r.aguardando, desistidas, new Dinheiro((long)r.valor),
                new Dinheiro((long)r.valor_parado), motivo, lista, saida);
        }
        catch { return new VendasParadas(0, 0, Dinheiro.Zero, Dinheiro.Zero); }
    }

    /// <summary>
    /// Traduz o rastro do dead-letter para quem está no balcão. Os casos vieram do
    /// banco da loja, não da imaginação: o 23503 (operador do caixa que não existe em
    /// employees) respondeu por TODAS as 16 vendas paradas, e o 42501 pelo movimento
    /// de caixa. Motivo desconhecido devolve o rastro cru e curto — pior que traduzir
    /// errado é esconder a única pista que o suporte tem.
    /// </summary>
    internal static string? MotivoHumano(string? erro)
    {
        if (string.IsNullOrWhiteSpace(erro)) return null;

        bool Tem(string t) => erro!.Contains(t, StringComparison.OrdinalIgnoreCase);

        // "lá" e não "no painel": a frase já entra depois de "O painel recusou:", e
        // repetir a palavra na mesma linha deixa o aviso com cara de texto de robô.
        if (Tem("operator_id") && Tem("employees"))
            return "o operador que fez a venda não está cadastrado lá";
        if (Tem("row-level security") || Tem("42501"))
            return "o painel recusou por permissão: este caixa não está autorizado a gravar";
        if (Tem("órfão") || Tem("orfão"))
            return "a venda a que esta nota se liga nunca subiu";
        if (Tem("dias falhando"))
            return "ficou dias sem conseguir falar com o painel";
        if (Tem("sem_caixa_aberto"))
            return "o painel não tinha caixa aberto para receber esta venda";
        if (Tem("tipo sem handler"))
            return "esta versão do PDV não sabe enviar este tipo de registro";
        // Exceção no PRÓPRIO caixa ao processar a linha (não foi a nuvem que recusou).
        // O trecho cru vai junto: é a única pista que o suporte tem.
        var exc = erro!.IndexOf("exceção:", StringComparison.OrdinalIgnoreCase);
        if (exc >= 0)
        {
            var trecho = erro[(exc + "exceção:".Length)..].Trim();
            if (trecho.Length > 60) trecho = trecho[..60];
            return trecho.Length > 0
                ? $"o caixa falhou ao processar este registro ({trecho})"
                : "o caixa falhou ao processar este registro";
        }

        // 22P02: o painel faz cast para uuid e o registro GRAVADO leva outra coisa no
        // lugar (nesta máquina, `pdv_product_id = "saas-teste-…"`, semeado por script de
        // teste). O texto está na fila, não no painel: nenhum cadastro que o gerente
        // faça muda uma letra dele. Antes isto caía no fallback abaixo e o dono lia
        // "o painel recusou o envio (HTTP 400)" no balcão.
        //
        // "ficou gravado" não é enfeite: sem isso o dono vai ao painel arrumar o
        // cadastro do produto e volta para tocar em Sincronizar de novo. O código
        // viajou JUNTO com a venda, e arrumar o catálogo hoje não reescreve o que já
        // está na fila.
        if (Tem("22P02") || Tem("invalid input syntax"))
            return "o código de produto que ficou gravado nessas vendas";
        if (Tem("22007") || Tem("22008") || Tem("invalid input value"))
            return "um dado que ficou gravado nessas vendas em formato que ele não entende";

        // O número do status saiu da tela em 08/09/2026: "HTTP 400" é código de erro no
        // balcão, e a regra da casa proíbe. O rastro cru continua inteiro no banco, que
        // é onde o suporte olha.
        if (System.Text.RegularExpressions.Regex.IsMatch(erro!, @"HTTP \d{3}"))
            return "o painel recusou o envio";

        // Rastro cru, sem o prefixo "desistido após N tentativas — " que já foi dito.
        var corte = erro!.IndexOf("— ", StringComparison.Ordinal);
        var cru = (corte >= 0 ? erro[(corte + 2)..] : erro).Trim();
        return cru.Length <= 90 ? cru : cru[..90];
    }

    /// <summary>
    /// O QUE DÁ PARA FAZER com uma linha que o envio desistiu, lido do rastro que a
    /// própria drenagem gravou. Puro: entra texto, sai a saída.
    ///
    /// POR QUE ISTO EXISTE. Até 08/09/2026 o PDV tinha um balde só para "recusado":
    /// <see cref="Drenagem"/> classifica 4xx como recusa permanente para efeito de
    /// dead-letter, e ali isso está certo (é matemática de fila). O que faltava era a
    /// outra pergunta, a que o operador faz: dá para consertar? O 409 de operador que
    /// falta no painel e o 400 de registro malformado são a mesma coisa para a fila e o
    /// oposto para quem está no balcão.
    ///
    /// Na dúvida devolve <see cref="SaidaParada.Resolver"/>: mandar alguém olhar é
    /// errar para o lado seguro. O caro é o contrário, dizer "não tem conserto" para
    /// uma venda de verdade que só precisava de um cadastro.
    /// </summary>
    public static SaidaParada SaidaDoErro(string? erro)
    {
        if (string.IsNullOrWhiteSpace(erro)) return SaidaParada.Resolver;
        bool Tem(string t) => erro!.Contains(t, StringComparison.OrdinalIgnoreCase);

        // Ficou dias sem alcançar o painel (Drenagem.DiasParaDesistir). Não há cadastro
        // para arrumar: é a internet voltar e alguém mandar de novo.
        if (Tem("dias falhando")) return SaidaParada.Espera;

        // O painel nunca vai aceitar o que está GRAVADO na fila. O payload é imutável:
        // foi montado na hora da venda e ninguém o reescreve.
        if (Tem("22P02") || Tem("invalid input syntax") || Tem("invalid input value")
            || Tem("22007") || Tem("22008"))
            return SaidaParada.SemConserto;

        // Órfão: a venda a que esta linha se pendura nunca subiu e já foi desistida.
        // Enquanto a mãe não subir, a filha não tem como subir; e a mãe, aqui, está morta.
        if (Tem("órfão") || Tem("orfão")) return SaidaParada.SemConserto;

        return SaidaParada.Resolver;
    }

    /// <summary>
    /// SQL de "a nuvem DESISTIU desta linha". <c>desistido_em</c> é o estado explícito
    /// de hoje; o <c>ultimo_erro</c> é o que desmascara a linha ANTIGA, gravada quando
    /// a desistência era carimbada em <c>enviado_em</c> — sem esta metade, as 16 vendas
    /// já perdidas no caixa da loja continuariam invisíveis para sempre.
    /// </summary>
    private const string SqlDesistiu =
        "(o.desistido_em IS NOT NULL OR COALESCE(o.ultimo_erro,'') LIKE 'desistido%')";

    /// <summary>
    /// Vendas que a nuvem NUNCA recebeu e que o dreno desistiu de enviar (dead-letter).
    /// Não voltam sozinhas: precisam de reconciliação manual.
    /// </summary>
    public static int Desistidos() => VendasNaoEntregues().Desistidas;

    /// <summary>
    /// A SAÍDA. Tira da fila o que o painel nunca vai aceitar, e só isso.
    ///
    /// POR QUE PRECISOU EXISTIR. Nesta máquina havia 8 vendas de teste de 21/08 que o
    /// painel recusava por formato. O caixa não tinha nenhum caminho para dispensá-las:
    /// as duas únicas coisas que o PDV sabia fazer com uma linha morta eram tentar de
    /// novo (que não podia dar certo) e zerar o banco inteiro. Ou seja, o aviso ia ficar
    /// na tela para sempre. Aviso eterno é aviso que se aprende a ignorar, e no dia em
    /// que uma venda de verdade parar, ninguém vai olhar.
    ///
    /// NÃO APAGA NADA. Carimba <c>descartado_em</c>: a linha, o payload e o rastro
    /// continuam no banco para quem for conferir depois. É a regra da casa (o que é
    /// dinheiro não se edita nem se apaga, só ganha um evento novo) e é o que deixa a
    /// auditoria poder responder "quem tirou isto daqui, e quando".
    ///
    /// Dispensa TODO tipo de linha, não só venda: as 8 vendas arrastavam 6 vínculos de
    /// nota fiscal que ficaram órfãos por causa delas. Deixar as filhas para trás seria
    /// trocar um aviso eterno por outro.
    /// </summary>
    /// <param name="quem">Quem autorizou, para a auditoria.</param>
    /// <param name="simular">
    /// true só CONTA, sem escrever. É como a tela sabe se tem algo a oferecer e quantas
    /// são, pelo mesmo caminho que faria a conta de verdade: dois códigos separados para
    /// contar e para agir divergem no primeiro dia.
    /// </param>
    /// <returns>Quantas linhas saíram (ou sairiam) da fila.</returns>
    public static int Dispensar(string? quem = null, bool simular = false)
    {
        using var cx = Banco.Abrir();
        var mortas = cx.Query<(long Id, string Tipo, string RefId, string? Erro)>("""
            SELECT id, tipo, ref_id, ultimo_erro
              FROM outbox
             WHERE descartado_em IS NULL
               AND (desistido_em IS NOT NULL OR COALESCE(ultimo_erro,'') LIKE 'desistido%')
            """).ToList();

        var alvos = mortas.Where(m => SaidaDoErro(m.Erro) == SaidaParada.SemConserto).ToList();
        if (alvos.Count == 0 || simular) return alvos.Count;

        var agora = DateTime.Now.ToString("o");
        using var tx = cx.BeginTransaction();
        foreach (var m in alvos)
        {
            cx.Execute("UPDATE outbox SET descartado_em = @Em WHERE id = @Id AND descartado_em IS NULL",
                new { Em = agora, Id = m.Id }, tx);
            Caixa.Auditar(cx, tx, "outbox_dispensado", null, null,
                $"{m.Tipo} {m.RefId} tirado da fila: {MotivoHumano(m.Erro) ?? "sem motivo gravado"}"
                + (quem is { Length: > 0 } q ? $" (por {q})" : ""));
        }
        tx.Commit();
        return alvos.Count;
    }

    /// <summary>
    /// A fila, em uma linha, para o heartbeat do terminal (vira o "último detalhe" do
    /// caixa no painel). Só o que ainda está PENDENTE (nem enviado, nem desistido): é
    /// isso que decide se a nuvem está atrasada em relação ao balcão.
    ///
    /// POR QUE EXISTE: em 04/09/2026 a fila da Savassi parou às 16:15 e o painel só
    /// dizia "turno aberto · nenhum · relógio -15s" por três horas, com o caixa vivo e
    /// vendendo. O contador de vendas por subir já ia no mesmo relato e não bastou:
    /// faltava o tipo do que estava preso, há quanto tempo e o erro que a fila via.
    ///
    /// Devolve null quando não dá para ler (banco bloqueado, esquema velho): o
    /// heartbeat manda o relato de sempre, sem este campo. Nunca lança.
    /// </summary>
    public static string? ResumoDaFila()
    {
        try
        {
            using var cx = Banco.Abrir();
            const string pendente = "enviado_em IS NULL AND desistido_em IS NULL";
            var porTipo = cx.Query($"SELECT tipo, COUNT(*) AS n FROM outbox WHERE {pendente} GROUP BY tipo ORDER BY n DESC, tipo")
                .Select(r => ((string)r.tipo, (int)(long)r.n))
                .ToList();
            var maisAntigoTxt = cx.ExecuteScalar<string?>($"SELECT MIN(criado_em) FROM outbox WHERE {pendente}");
            DateTime? maisAntigo = maisAntigoTxt is not null
                && DateTime.TryParse(maisAntigoTxt, System.Globalization.CultureInfo.InvariantCulture,
                                     System.Globalization.DateTimeStyles.RoundtripKind, out var d)
                ? (d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d) : null;
            // A linha pendente mais nova que já tem rastro: é o erro mais recente que a
            // fila escreveu (as linhas são visitadas em ordem de id a cada varredura).
            var ultimoErro = cx.ExecuteScalar<string?>(
                $"SELECT ultimo_erro FROM outbox WHERE {pendente} AND ultimo_erro IS NOT NULL ORDER BY id DESC LIMIT 1");
            return MontarResumoDaFila(porTipo, maisAntigo, ultimoErro, DateTime.Now);
        }
        catch { return null; }
    }

    /// <summary>
    /// O texto do resumo. PURA, para a suíte provar o formato sem banco:
    /// "fila: venda 17, kds_pronto 3 · mais antigo 146 min · último erro: exceção: database is locked".
    /// "fila vazia" quando não há pendência: some do painel é o que a fila fazia antes.
    /// O erro é cortado em 60 caracteres (o painel guarda 400 no total) e nunca leva
    /// travessão: isto é texto de tela.
    /// </summary>
    public static string MontarResumoDaFila(IReadOnlyList<(string Tipo, int Quantos)> porTipo,
        DateTime? maisAntigo, string? ultimoErro, DateTime agora)
    {
        if (porTipo.Count == 0) return "fila vazia";
        var partes = new List<string>
        {
            "fila: " + string.Join(", ", porTipo.Select(p => $"{p.Tipo} {p.Quantos}")),
        };
        if (maisAntigo is { } m)
            partes.Add($"mais antigo {Math.Max(0, (int)(agora - m).TotalMinutes)} min");
        if (!string.IsNullOrWhiteSpace(ultimoErro))
        {
            var e = ultimoErro.Replace('\r', ' ').Replace('\n', ' ').Replace('—', '-').Replace('–', '-').Trim();
            partes.Add("último erro: " + (e.Length <= 60 ? e : e[..60]));
        }
        return string.Join(" · ", partes);
    }
}
