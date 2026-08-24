using Dapper;

namespace Pdv.Nucleo;

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
public sealed record VendasParadas(int Aguardando, int Desistidas, Dinheiro Valor)
{
    public int Total => Aguardando + Desistidas;

    /// <summary>A linha que vai para a tela. null quando não há nada parado.</summary>
    public string? Resumo => Total == 0 ? null
        : Desistidas == 0
            ? $"{Aguardando} venda(s) na fila para o servidor — {Valor.Formatado()}."
            : $"{Total} venda(s) que o servidor não tem — {Valor.Formatado()}. "
              + $"Em {Desistidas} delas o envio DESISTIU: confira antes de fechar o mês.";
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
    public static async Task<ResultadoSync> ExecutarAsync(
        Nuvem nuvem, GuardaNuvem? guarda, Drenagem? drenagem = null,
        IProgress<string>? andamento = null, CancellationToken ct = default)
    {
        var produtos = 0;
        var fotos = 0;
        var notas = 0;

        // Vendas da fila primeiro: são elas que alimentam os relatórios do painel.
        if (drenagem is not null)
        {
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
                   AND (o.enviado_em IS NULL OR {SqlDesistiu})
                """);
            return new VendasParadas((int)r.aguardando, (int)r.desistidas, new Dinheiro((long)r.valor));
        }
        catch { return new VendasParadas(0, 0, Dinheiro.Zero); }
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
