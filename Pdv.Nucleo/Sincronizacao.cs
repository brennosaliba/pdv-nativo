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
public sealed record VendasParadas(
    int Aguardando, int Desistidas, Dinheiro Valor, string? Motivo = null,
    IReadOnlyList<VendaParada>? Lista = null)
{
    public int Total => Aguardando + Desistidas;

    /// <summary>
    /// Quantos números cabem antes de a linha virar parede. 3 se lê; 40 não — vira
    /// um borrão que ninguém confere. Passando disto, o aviso diz quantas ficaram
    /// de fora e de que dias: cortar em silêncio é o mesmo defeito de novo.
    /// </summary>
    private const int MaxListadas = 6;

    /// <summary>
    /// O aviso que vai para a tela. null quando não há nada parado.
    ///
    /// A PRIMEIRA LINHA EXISTE PARA MATAR O SUSTO. A versão anterior abria com
    /// "3 venda(s) que o servidor não tem", e o dono leu exatamente o que estava
    /// escrito: que 3 vendas não se concretizaram. NÃO É ISSO — a venda aconteceu, o
    /// cliente levou o produto e o dinheiro entrou na gaveta; o que ficou para trás é
    /// só o REGISTRO dela no painel. Um aviso técnico e correto que assusta o dono à
    /// toa custa mais caro que o problema que ele denuncia: no susto se cancela venda,
    /// se refaz cupom, se mexe no caixa que estava certo.
    ///
    /// Por isso a ordem é: (1) nada se perdeu; (2) o que de fato não subiu, e quanto;
    /// (3) QUAIS vendas, pelo número que se grita no balcão; (4) por quê; (5) o que
    /// isso muda de verdade — e o que NÃO muda; (6) o próximo passo.
    ///
    /// O passo (6) não é enfeite: sem ele o dono lia, arrumava o cadastro no painel,
    /// apertava Sincronizar e o número continuava o mesmo, porque nada no PDV sabia
    /// tirar uma linha do dead-letter. Aviso sem saída é aviso que se aprende a ignorar.
    /// </summary>
    public string? Resumo
    {
        get
        {
            if (Total == 0) return null;
            var quanto = Valor.Formatado();
            var l = new List<string>
            {
                // Antes de qualquer número: o caixa está certo. Só depois o problema.
                // Frase curta e sozinha de propósito — é a única linha que TEM que ser
                // lida inteira, e linha curta não quebra em lugar nenhum.
                "NENHUMA VENDA FOI PERDIDA.",
                "O cliente levou o produto e o dinheiro entrou na gaveta.",
                Desistidas == 0
                    ? $"Falta só o REGISTRO de {Aguardando} venda(s) subir para o painel ({quanto})."
                    : $"O que não subiu para o painel foi o REGISTRO de {Total} venda(s) ({quanto}).",
            };

            // Quais. Separadas quando há dos dois tipos: uma metade precisa de gente,
            // a outra se resolve sozinha, e misturá-las é pedir para o dono agir na errada.
            if (Desistidas > 0 && Aguardando > 0)
            {
                if (Nomear("Paradas de vez", v => v.Desistiu, Desistidas) is string d) l.Add(d);
                if (Nomear("Ainda na fila", v => !v.Desistiu, Aguardando, ", e essas sobem sozinhas")
                    is string a) l.Add(a);
            }
            else if (Nomear("Quais", _ => true, Total) is string todas) l.Add(todas);

            if (Desistidas > 0)
            {
                var porque = Motivo is { Length: > 0 } m ? $" ({m})" : "";
                l.Add(Aguardando == 0
                    ? $"O envio DESISTIU delas{porque}."
                    : $"Em {Desistidas} delas o envio DESISTIU{porque}.");
            }

            // O tamanho REAL do estrago, e o tamanho do que NÃO é estrago. Sem a
            // segunda metade o operador inventa a dele — e a dele é sempre pior:
            // no susto se cancela venda que estava certa e se mexe em caixa fechado.
            l.Add($"SÓ MUDA NO PAINEL: faturamento e DRE ficam {quanto} menores até elas subirem.");
            l.Add("NÃO MUDA: a venda, o caixa deste turno e o cupom do cliente. Tudo certo.");

            l.Add(Desistidas == 0
                ? "O QUE FAZER: nada no caixa. Elas sobem sozinhas na próxima sincronização."
                : "O QUE FAZER: chame o gerente para resolver esse motivo no painel e, "
                  + "depois, toque em Sincronizar. Cada toque dá mais UMA tentativa a "
                  + "estas vendas.");

            return string.Join("\n", l);
        }
    }

    /// <summary>
    /// "Paradas de vez: nº 41, 42 e 43 (hoje)." — os números pelos quais a venda é
    /// conhecida na loja, agrupados por dia (o número reinicia a cada dia operacional).
    ///
    /// Devolve null quando não há lista (chamada antiga, ou a consulta falhou): o aviso
    /// perde esta linha e continua verdadeiro. O que ele NÃO pode fazer é inventar
    /// número.
    /// </summary>
    private string? Nomear(string rotulo, Func<VendaParada, bool> filtro, int quantas,
        string sufixo = "")
    {
        if (Lista is null || quantas == 0) return null;
        var vendas = Lista.Where(filtro).ToList();
        if (vendas.Count == 0) return null;

        var mostradas = vendas.Take(MaxListadas).ToList();
        var texto = string.Join("; ", mostradas
            .GroupBy(v => v.Dia)
            .Select(g => "nº " + Juntar(g.Select(v => v.Numero.ToString())) + $" ({Quando(g.Key)})"));

        // Quantas ficaram de fora vem do CONTADOR, não do tamanho da lista: a consulta
        // tem teto, e é melhor dizer "e mais 34" do que fingir que eram só as 6.
        var restam = quantas - mostradas.Count;
        if (restam > 0)
        {
            // Só se promete o período dos dias quando a lista veio inteira; truncada,
            // o intervalo seria um palpite.
            var completa = Lista.Count == Total;
            var dias = completa
                ? vendas.Skip(MaxListadas).Select(v => v.Dia).Distinct().OrderBy(d => d).ToList()
                : new List<string>();
            texto += $", e mais {restam}"
                + dias switch
                {
                    { Count: 0 } => "",
                    { Count: 1 } => $", de {Quando(dias[0])}",
                    _ => $", de {Quando(dias[0])} a {Quando(dias[^1])}",
                };
        }
        // O ponto final entra DEPOIS do sufixo: "…(hoje) — essas sobem sozinhas."
        return $"{rotulo}: {texto}{sufixo}.";
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
    private static string ImpressaoDigital(Microsoft.Data.Sqlite.SqliteConnection cx)
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
            .Concat(cx.Query<string>("SELECT id||'§'||payload FROM promo ORDER BY id"));
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
                       COALESCE(SUM(v.total_cent), 0)                              AS valor
                  FROM outbox o
                  JOIN venda  v ON v.id = o.ref_id
                 WHERE o.tipo = 'venda'
                   AND v.status = 'finalizada'
                   AND v.homologacao = 0
                   AND (o.enviado_em IS NULL OR {SqlDesistiu})
                """);
            var desistidas = (int)r.desistidas;
            // O motivo mais comum entre as desistidas. Uma causa só, dita uma vez: o
            // operador não precisa de 16 linhas de rastro, precisa saber a quem ligar.
            var motivo = desistidas == 0 ? null : MotivoHumano(cx.ExecuteScalar<string?>($"""
                SELECT o.ultimo_erro
                  FROM outbox o
                  JOIN venda  v ON v.id = o.ref_id
                 WHERE o.tipo = 'venda' AND v.status = 'finalizada' AND v.homologacao = 0
                   AND {SqlDesistiu}
                 GROUP BY o.ultimo_erro
                 ORDER BY COUNT(*) DESC
                 LIMIT 1
                """));
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
                 WHERE o.tipo = 'venda'
                   AND v.status = 'finalizada'
                   AND v.homologacao = 0
                   AND (o.enviado_em IS NULL OR {SqlDesistiu})
                 ORDER BY desistiu DESC, v.business_date, v.numero_local
                 LIMIT 400
                """)
                .Select(x => new VendaParada((long)x.numero, (string)x.dia, (long)x.desistiu == 1))
                .ToList();

            return new VendasParadas((int)r.aguardando, desistidas, new Dinheiro((long)r.valor), motivo, lista);
        }
        catch { return new VendasParadas(0, 0, Dinheiro.Zero); }
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

        if (Tem("operator_id") && Tem("employees"))
            return "o operador que fez a venda não está cadastrado no painel";
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

        var http = System.Text.RegularExpressions.Regex.Match(erro!, @"HTTP (\d{3})");
        if (http.Success) return $"o painel recusou o envio (HTTP {http.Groups[1].Value})";

        // Rastro cru, sem o prefixo "desistido após N tentativas — " que já foi dito.
        var corte = erro!.IndexOf("— ", StringComparison.Ordinal);
        var cru = (corte >= 0 ? erro[(corte + 2)..] : erro).Trim();
        return cru.Length <= 90 ? cru : cru[..90];
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
}
