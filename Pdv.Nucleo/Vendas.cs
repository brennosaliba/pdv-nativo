using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>Uma linha da comanda, congelada no momento da venda.</summary>
public sealed record LinhaVenda(
    string? ProdutoId, string? Codigo, string Descricao,
    Quantidade Qtd, Dinheiro Preco, Dinheiro Total,
    string Unidade, string? Ncm, string? Cest, string? Csosn, string? Cfop, int Origem);

/// <summary>
/// Um pagamento da venda.
///
/// <paramref name="Valor"/> é o que o cliente ENTREGOU e <paramref name="Troco"/> o que
/// voltou — é assim que a apuração do caixa fecha (ela soma valor − troco). O que vai
/// para a NFC-e é outra coisa: lá o pagamento tem que ser exatamente o total da nota,
/// porque o motor não emite &lt;vTroco&gt; e a SEFAZ valida Σ vPag − vTroco = vNF.
/// </summary>
public sealed record PagamentoVenda(
    string Forma, Dinheiro Valor, Dinheiro Troco,
    string? Aut = null, string? CnpjCredenciadora = null, string? Bandeira = null, string? Nsu = null);

public sealed record VendaGravada(string Id, string ClientKey, long Numero, Dinheiro Total, string BusinessDate);

/// <summary>
/// Gravação da venda no caixa.
///
/// A ordem do fluxo inteiro é inegociável: cobra primeiro, grava depois, emite por
/// último. Se a cobrança não passar, nada foi gravado e nenhum número de nota foi
/// queimado. E a venda existe mesmo que a SEFAZ recuse a nota depois — o dinheiro
/// entrou, e o caixa tem que fechar com ele.
/// </summary>
public static class Vendas
{
    private static string Agora => DateTime.Now.ToString("o");

    /// <summary>
    /// Grava venda, itens, pagamento, auditoria e a fila de sincronização numa
    /// transação só. Ou entra tudo, ou não entra nada: venda sem pagamento faria o
    /// caixa acusar falta, e pagamento sem venda faria acusar sobra.
    /// </summary>
    public static VendaGravada Finalizar(
        SqliteConnection cx, Sessao sessao, Operador operador,
        IReadOnlyList<LinhaVenda> itens, IReadOnlyList<PagamentoVenda> pagamentos,
        string? documento, string loja, string? lojaId)
    {
        if (itens.Count == 0) throw new InvalidOperationException("Não dá para finalizar uma comanda vazia.");
        if (pagamentos.Count == 0) throw new InvalidOperationException("Informe a forma de pagamento.");

        var total = new Dinheiro(itens.Sum(i => i.Total.Centavos));
        var liquido = new Dinheiro(pagamentos.Sum(p => p.Valor.Centavos - p.Troco.Centavos));
        if (liquido.Centavos != total.Centavos)
            throw new InvalidOperationException(
                $"Os pagamentos somam {liquido.Formatado()} e a venda é {total.Formatado()}.");

        var vendaId = Guid.NewGuid().ToString();
        // Uma chave por venda, gerada UMA vez. É ela que faz o reenvio para a nuvem ser
        // idempotente: se a resposta se perder no caminho, o replay não vira venda dobrada.
        var clientKey = Guid.NewGuid().ToString();
        var agora = Agora;

        using var tx = cx.BeginTransaction();

        // Numeração dentro da transação, senão duas vendas quase simultâneas pegam o
        // mesmo número e a segunda estoura no índice único.
        var numero = cx.ExecuteScalar<long>(
            "SELECT COALESCE(MAX(numero_local),0)+1 FROM venda WHERE business_date = @B",
            new { B = sessao.BusinessDate }, tx);

        cx.Execute("""
            INSERT INTO venda (id, client_key, sessao_id, business_date, numero_local, operador_id,
                               subtotal_cent, desconto_cent, total_cent, documento, status,
                               fiscal_status, criada_em, finalizada_em)
            VALUES (@Id,@Key,@Ses,@Bd,@Num,@Op,@Tot,0,@Tot,@Doc,'finalizada','pendente',@Em,@Em)
            """,
            new { Id = vendaId, Key = clientKey, Ses = sessao.Id, Bd = sessao.BusinessDate,
                  Num = numero, Op = operador.Id, Tot = total.Centavos, Doc = documento, Em = agora }, tx);

        var seq = 1;
        foreach (var i in itens)
            cx.Execute("""
                INSERT INTO venda_item (id, venda_id, seq, produto_id, codigo, descricao,
                                        qtd_milesimo, preco_cent, desconto_cent, total_cent,
                                        unidade, ncm, cest, csosn, cfop, origem, cancelado)
                VALUES (@Id,@V,@Seq,@Prod,@Cod,@Desc,@Qtd,@Preco,0,@Tot,@Un,@Ncm,@Cest,@Csosn,@Cfop,@Orig,0)
                """,
                new { Id = Guid.NewGuid().ToString(), V = vendaId, Seq = seq++,
                      Prod = i.ProdutoId, Cod = i.Codigo, Desc = i.Descricao,
                      Qtd = i.Qtd.Milesimos, Preco = i.Preco.Centavos, Tot = i.Total.Centavos,
                      Un = i.Unidade, Ncm = i.Ncm, Cest = i.Cest, Csosn = i.Csosn,
                      Cfop = i.Cfop, Orig = i.Origem }, tx);

        foreach (var p in pagamentos)
            cx.Execute("""
                INSERT INTO venda_pagamento (id, venda_id, forma, valor_cent, troco_cent,
                                             tef_aut, tef_cnpj_cred, tef_bandeira, tef_nsu)
                VALUES (@Id,@V,@F,@Val,@Tr,@Aut,@Cnpj,@Band,@Nsu)
                """,
                new { Id = Guid.NewGuid().ToString(), V = vendaId, F = p.Forma,
                      Val = p.Valor.Centavos, Tr = p.Troco.Centavos,
                      Aut = p.Aut, Cnpj = p.CnpjCredenciadora, Band = p.Bandeira, Nsu = p.Nsu }, tx);

        Caixa.Auditar(cx, tx, "venda_finalizada", operador.Id, null,
            $"venda={numero} total={total.Formatado()} formas={string.Join('+', pagamentos.Select(p => p.Forma))}");

        // O payload da fila é literalmente o corpo da RPC da nuvem, para o dreno ser um
        // POST direto, sem transformação. Transformar na hora do envio é onde se erra
        // nome de campo — e o erro só aparece dias depois, na venda que não subiu.
        Caixa.Enfileirar(cx, tx, "venda", vendaId, clientKey, new
        {
            p_itens = itens.Select(i => new
            {
                pdv_product_id = i.ProdutoId,
                codigo = i.Codigo,
                descricao = i.Descricao,
                ncm = i.Ncm,
                csosn = i.Csosn,
                unidade = i.Unidade,
                qtd = i.Qtd.Milesimos / 1000m,
                valor_unitario = i.Preco.Reais,
            }).ToArray(),
            p_pagamentos = pagamentos.Select(p => new { metodo = p.Forma, valor = (p.Valor.Centavos - p.Troco.Centavos) / 100m }).ToArray(),
            p_store = loja,
            p_store_id = lojaId,
            // A venda TEM que subir com QUEM a fez, senão o antifraude da nuvem fica
            // cego por operador: mandar null aqui matava queda_no_turno, deixava o
            // mix_dinheiro em "aprendendo" eterno e o score descartava a venda no
            // balde "?". O nome vai junto porque o operador do caixa NÃO é employee do
            // ERP — o id sozinho não resolve o nome do outro lado.
            p_operator_id = operador.Id,
            p_operator_name = operador.Nome,
            p_desconto = 0m,
            p_recebido = pagamentos.Sum(p => p.Valor.Centavos) / 100m,
            p_documento = documento,
            p_session_id = (string?)null,
            p_exigir_caixa = false,
            p_observacao = (string?)null,
            p_client_key = clientKey,
            p_business_date = sessao.BusinessDate,
        });

        tx.Commit();

        // KDS opcional do balcao (config kds_balcao=1): quiosque com forno liga,
        // vitrine que so entrega da estufa deixa desligado - senao cada venda de
        // balcao vira card e a fila real do delivery some no meio. NUNCA pode
        // derrubar a venda: o dinheiro ja esta gravado quando isto roda.
        try { if (Config(cx, "kds_balcao") == "1") Kds.DoBalcao(vendaId); }
        catch { /* fila de preparo nunca e motivo para perder venda */ }

        return new VendaGravada(vendaId, clientKey, numero, total, sessao.BusinessDate);
    }

    /// <summary>
    /// Registra o desfecho de UMA tentativa de emissão e atualiza a venda.
    ///
    /// Fica fora da transação da venda de propósito: a emissão pode levar 25 segundos, e
    /// segurar a transação aberta esse tempo todo travaria o banco para a próxima venda.
    /// </summary>
    public static void RegistrarEmissao(SqliteConnection cx, string vendaId, ResultadoEmissao r)
    {
        var tentativa = cx.ExecuteScalar<long>(
            "SELECT COALESCE(MAX(tentativa),0)+1 FROM nfce_emissao WHERE venda_id = @V", new { V = vendaId });

        cx.Execute("""
            INSERT INTO nfce_emissao (id, venda_id, tentativa, caminho, modo, autorizado, contingencia,
                                      chave, numero, serie, tp_amb, protocolo, c_stat, x_motivo, qr_code,
                                      dh_recbto, v_nf_cent, emit_nome, emit_cnpj, emit_ie, emit_endereco,
                                      erro, criado_em)
            VALUES (@Id,@V,@T,@Cam,@Modo,@Aut,@Cont,@Ch,@Num,@Ser,@Amb,@Prot,@CStat,@XMot,@Qr,
                    @Dh,@VNf,@ENome,@ECnpj,@EIe,@EEnd,@Erro,@Em)
            """,
            new { Id = Guid.NewGuid().ToString(), V = vendaId, T = tentativa,
                  Cam = r.Caminho, Modo = r.Modo, Aut = r.Autorizado ? 1 : 0, Cont = r.Contingencia ? 1 : 0,
                  Ch = r.Chave, Num = r.Numero, Ser = r.Serie, Amb = r.TpAmb, Prot = r.Protocolo,
                  CStat = r.CStat, XMot = r.XMotivo, Qr = r.QrCode, Dh = r.DhRecbto,
                  VNf = r.VNF is null ? (long?)null : (long)Math.Round(r.VNF.Value * 100m, MidpointRounding.AwayFromZero),
                  ENome = r.Emit?.Nome, ECnpj = r.Emit?.Cnpj, EIe = r.Emit?.Ie, EEnd = r.Emit?.Endereco,
                  Erro = r.Erro, Em = Agora });

        cx.Execute("UPDATE venda SET fiscal_status = @St, nfce_chave = @Ch, nfce_protocolo = @Prot WHERE id = @Id",
            new { St = r.StatusFiscal, Ch = r.Chave, Prot = r.Protocolo, Id = vendaId });

        // Nota emitida PELA NUVEM já nasce sincronizada: a edge a gravou em
        // nfce_emitidas no mesmo instante, e o XML mora lá, não aqui. A GuardaNuvem
        // sobe XML do AGENTE local — e uma nota da nuvem não tem XML local, então
        // ela procurava, não achava e deixava sincronizada=0 PARA SEMPRE. Era o
        // "(2)" fantasma no botão Sincronizar, que nenhum clique resolvia.
        if (r.Caminho == EmissorNuvem.Caminho && r.Chave is { Length: 44 })
            cx.Execute("UPDATE nfce_emissao SET sincronizada = 1 WHERE chave = @Ch", new { Ch = r.Chave });

        // Vincular a nota à venda na nuvem é um fato separado da venda: a venda pode ter
        // subido antes de a nota existir (contingência), e a nuvem precisa dos dois.
        if (r.Chave is { Length: > 0 })
        {
            var chave = cx.ExecuteScalar<string>("SELECT client_key FROM venda WHERE id = @Id", new { Id = vendaId }) ?? vendaId;
            Caixa.Enfileirar(cx, null, "nfce_vinculo", vendaId, chave, new
            {
                p_chave = r.Chave,
                p_fiscal_status = r.StatusFiscal,
                p_c_stat = r.CStat,
                p_x_motivo = r.XMotivo,
            });
        }
    }

    /// <summary>
    /// Cancela uma venda já finalizada.
    ///
    /// Se a nota FOI autorizada, cancelar aqui não basta: a NFC-e é um documento que
    /// existe na SEFAZ e só sai de lá pelo evento de cancelamento (110111), dentro do
    /// prazo. Cancelar só do lado de cá deixaria uma nota válida para uma venda que não
    /// existe mais — divergência que aparece na apuração do contador, não no caixa.
    /// Por isso este método RECUSA, e quem quiser cancelar tem que cancelar a nota antes.
    /// </summary>
    public static void Cancelar(SqliteConnection cx, string vendaId, string quemCancelou,
        string motivo, string? autorizadoPor = null)
    {
        if (string.IsNullOrWhiteSpace(motivo))
            throw new InvalidOperationException("Cancelamento exige motivo.");

        var v = cx.QueryFirstOrDefault("SELECT status, fiscal_status, numero_local, total_cent, nfce_chave FROM venda WHERE id = @Id",
            new { Id = vendaId })
            ?? throw new InvalidOperationException("Venda não encontrada.");

        if ((string)v.status == "cancelada") return;                 // idempotente
        if ((string)v.status != "finalizada")
            throw new InvalidOperationException($"A venda está '{v.status}'.");

        var fiscal = (string)v.fiscal_status;
        if (fiscal is "autorizada" or "contingencia")
            throw new InvalidOperationException(
                $"A NFC-e {v.nfce_chave} já foi emitida. Cancele a nota na SEFAZ antes de cancelar a venda.");

        using var tx = cx.BeginTransaction();
        cx.Execute("""
            UPDATE venda SET status='cancelada', cancelada_por=@Por, cancel_motivo=@Mot
             WHERE id=@Id
            """, new { Por = quemCancelou, Mot = motivo, Id = vendaId }, tx);

        Caixa.Auditar(cx, tx, "venda_cancelada", quemCancelou, autorizadoPor,
            $"venda={v.numero_local} total={new Dinheiro((long)v.total_cent).Formatado()} — {motivo}");

        var clientKey = cx.ExecuteScalar<string>("SELECT client_key FROM venda WHERE id=@Id", new { Id = vendaId }, tx) ?? vendaId;
        Caixa.Enfileirar(cx, tx, "venda_cancelada", vendaId, clientKey,
            new { venda = vendaId, motivo, por = quemCancelou });
        tx.Commit();
    }

    /// <summary>Config solta do terminal (impressora, limites). Ausente devolve o padrão.</summary>
    /// <summary>
    /// MODO DE HOMOLOGAÇÃO/TESTE (config `homologacao` = 1): login sem senha, autorizações
    /// sem PIN, janela com borda. Existe para o dono rodar o roteiro da PayGo com agilidade;
    /// em operação real fica desligado (a Configuração avisa em maiúsculas).
    /// </summary>
    public static bool Homologacao(SqliteConnection cx) => Config(cx, "homologacao") == "1";

    public static string? Config(SqliteConnection cx, string chave, string? padrao = null)
        => cx.ExecuteScalar<string?>("SELECT valor FROM config WHERE chave = @C", new { C = chave }) ?? padrao;

    public static void GravarConfig(SqliteConnection cx, string chave, string valor)
        => cx.Execute("""
            INSERT INTO config (chave, valor, atualizado) VALUES (@C,@V,@Em)
            ON CONFLICT(chave) DO UPDATE SET valor = @V, atualizado = @Em
            """, new { C = chave, V = valor, Em = Agora });

    /// <summary>Serializa igual ao resto do banco — a fila é lida por quem drena, não por gente.</summary>
    internal static string Json(object o) => JsonSerializer.Serialize(o);
}
