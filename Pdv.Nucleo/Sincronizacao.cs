using Dapper;

namespace Pdv.Nucleo;

/// <summary>O que a sincronização fez, para mostrar ao operador em uma tela só.</summary>
/// <param name="CatalogoMudou">
/// true quando produtos OU operadores mudaram DE VERDADE nesta passada. A baixada
/// regrava o catálogo inteiro sempre, então "quantos produtos desceram" não diz nada —
/// era por isso que sincronizar sem novidade repetia o relatório da vez anterior.
/// </param>
public sealed record ResultadoSync(
    int ProdutosBaixados, int FotosBaixadas, int NotasSubidas, int NotasPendentes,
    int VendasPendentes, bool CatalogoMudou, string? Erro)
{
    public bool Ok => Erro is null;

    /// <summary>Nada desceu, nada subiu, nada pendente: a resposta certa é "tudo em dia".</summary>
    public bool SemNovidade => Ok && !CatalogoMudou && FotosBaixadas == 0 && NotasSubidas == 0
        && NotasPendentes == 0 && VendasPendentes == 0;
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
                "SELECT id||'§'||nome||'§'||pin_hash||'§'||perfil||'§'||ativo FROM operador ORDER BY id"));
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n", partes))));
    }

    /// <summary>O que ainda não subiu. Vai na tela porque pendência invisível vira pendência eterna.</summary>
    public static (int notas, int vendas) Pendencias()
    {
        try
        {
            using var cx = Banco.Abrir();
            var notas = cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM nfce_emissao WHERE chave IS NOT NULL AND sincronizada = 0");
            var vendas = cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM outbox WHERE enviado_em IS NULL AND tipo = 'venda'");
            return (notas, vendas);
        }
        catch { return (0, 0); }
    }

    /// <summary>
    /// Vendas que a nuvem NUNCA recebeu e que o dreno desistiu de enviar
    /// (dead-letter). Elas somem de Pendencias() porque o dead-letter carimba
    /// enviado_em — então sem este contador o caixa mostrava "tudo em dia"
    /// enquanto o SQLite local guardava venda que a nuvem não tem. Precisa de
    /// reconciliação manual: aparece aqui em vez de sumir em silêncio.
    /// </summary>
    public static int Desistidos()
    {
        try
        {
            using var cx = Banco.Abrir();
            return cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM outbox WHERE tipo = 'venda' AND ultimo_erro LIKE 'desistido%'");
        }
        catch { return 0; }
    }
}
