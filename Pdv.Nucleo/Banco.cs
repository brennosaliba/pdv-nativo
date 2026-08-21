using System.IO;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>
/// Banco LOCAL do caixa (SQLite). É a fonte da verdade da operação: o caixa vende
/// mesmo sem internet, e a nuvem recebe depois.
///
/// Três regras que estão na ESTRUTURA (não dependem de o código lembrar):
///  1. DINHEIRO EM CENTAVOS (INTEGER). double/float em dinheiro acumula erro de
///     arredondamento e o fechamento nunca bate. Nada de REAL neste banco.
///  2. APPEND-ONLY no que é dinheiro. Venda cancelada não vira zero: entra um evento
///     de reversão. Sangria não se edita nem se apaga — só se estorna. Se dá pra
///     apagar, o registro não prova nada e o desvio fica invisível.
///  3. OUTBOX TRANSACIONAL: a venda e a linha da fila de sincronização são gravadas
///     na MESMA transação. Ou as duas existem, ou nenhuma — nunca uma venda que a
///     nuvem nunca vai receber.
/// </summary>
public static class Banco
{
    public static string Pasta => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PdvNativo");

    public static string Arquivo => Path.Combine(Pasta, "pdv.db");

    /// <summary>
    /// Redireciona TODO Abrir() sem argumento para outro arquivo. SÓ para teste e
    /// simulação — a Drenagem (e qualquer serviço de fundo) chama Abrir() por dentro,
    /// e sem isto um teste dela leria e ESCREVERIA no banco REAL do caixa.
    /// Produção nunca seta.
    /// </summary>
    public static string? CaminhoForcado { get; set; }

    /// <param name="caminho">Só os testes passam algo aqui; em produção é sempre o padrão.</param>
    public static SqliteConnection Abrir(string? caminho = null)
    {
        var alvo = caminho ?? CaminhoForcado;
        if (alvo is null) Directory.CreateDirectory(Pasta);
        var cx = new SqliteConnection($"Data Source={alvo ?? Arquivo}");
        cx.Open();
        using (var cmd = cx.CreateCommand())
        {
            // WAL: leitura não trava escrita (a tela consulta enquanto a venda grava).
            // synchronous=FULL: em banco de DINHEIRO, queda de energia não pode perder
            // a última transação confirmada. NORMAL é mais rápido e aceita perder — aqui não serve.
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
            cmd.ExecuteNonQuery();
        }
        return cx;
    }

    public static void Migrar(string? caminho = null)
    {
        using var cx = Abrir(caminho);
        using var tx = cx.BeginTransaction();
        using var cmd = cx.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = Esquema;
        cmd.ExecuteNonQuery();
        tx.Commit();

        // Colunas acrescentadas DEPOIS do esquema nascer. O CREATE IF NOT EXISTS não
        // alcança máquina que já tem a tabela — sem isto, campo novo só existiria em
        // instalação nova, e a que roda na loja ficaria para trás.
        foreach (var alter in new[]
        {
            "ALTER TABLE operador ADD COLUMN cpf TEXT",
            // marca quem é regido pelo painel: só esses são atualizados/desativados
            // pela sincronização — operador criado localmente não é tocado
            "ALTER TABLE operador ADD COLUMN da_nuvem INTEGER NOT NULL DEFAULT 0",
            // quando a PRIMEIRA falha real (com sessão de pé) aconteceu. A expiração da
            // fila conta a partir daqui, NÃO do criado_em: um terminal que ficou semanas
            // desligado religaria com itens "velhos" e os perderia no primeiro soluço.
            "ALTER TABLE outbox ADD COLUMN primeiro_erro_em TEXT",
            // 4a etapa do quadro do KDS (pronto -> entregue/coletado)
            "ALTER TABLE kds_ticket ADD COLUMN entregue_em TEXT",
            // prazo estimado do iFood (dueAt): o relogio que o Gestor mostra
            "ALTER TABLE kds_ticket ADD COLUMN preparo_ate TEXT",
            // TEF PayGo (troca de arquivos): two-phase commit precisa do código de
            // controle (027) e da identificação (001) para CNF/NCN no religamento; as
            // vias e a resposta crua ficam para reimpressão e auditoria.
            "ALTER TABLE tef_transacao ADD COLUMN provedor TEXT",
            "ALTER TABLE tef_transacao ADD COLUMN identificacao TEXT",
            "ALTER TABLE tef_transacao ADD COLUMN cod_controle TEXT",
            "ALTER TABLE tef_transacao ADD COLUMN rede TEXT",
            "ALTER TABLE tef_transacao ADD COLUMN data_tef TEXT",
            "ALTER TABLE tef_transacao ADD COLUMN hora_tef TEXT",
            "ALTER TABLE tef_transacao ADD COLUMN vias_json TEXT",
            "ALTER TABLE tef_transacao ADD COLUMN resposta_txt TEXT",
        })
        {
            try { using var c = cx.CreateCommand(); c.CommandText = alter; c.ExecuteNonQuery(); }
            catch { /* coluna já existe */ }
        }
    }

    private const string Esquema = """
        -- ── IDENTIDADE DO TERMINAL ────────────────────────────────────────────
        -- Uma linha só. Define de quem é este caixa (multi-tenant) e qual série
        -- fiscal ele usa. Série é POR TERMINAL: dois caixas na mesma série geram
        -- numeração duplicada e a SEFAZ rejeita em cascata.
        CREATE TABLE IF NOT EXISTS terminal (
          id                INTEGER PRIMARY KEY CHECK (id = 1),
          terminal_uuid     TEXT NOT NULL,
          loja_id           TEXT NOT NULL,
          loja_nome         TEXT NOT NULL,
          cnpj              TEXT NOT NULL,
          serie_nfce        INTEGER NOT NULL,
          ambiente          INTEGER NOT NULL DEFAULT 2,   -- 1=producao 2=homologacao
          api_base          TEXT,                          -- endereco do backend (IP ou dominio)
          criado_em         TEXT NOT NULL
        );

        -- ── OPERADORES (cache local: o caixa loga sem internet) ───────────────
        CREATE TABLE IF NOT EXISTS operador (
          id          TEXT PRIMARY KEY,
          nome        TEXT NOT NULL,
          pin_hash    TEXT NOT NULL,        -- PBKDF2; nunca o PIN em claro
          pin_salt    TEXT NOT NULL,
          perfil      TEXT NOT NULL,        -- operador | supervisor | gerente
          ativo       INTEGER NOT NULL DEFAULT 1,
          atualizado  TEXT NOT NULL
        );

        -- ── SESSÃO DE CAIXA (turno) ───────────────────────────────────────────
        -- business_date é o DIA OPERACIONAL, separado de created_at: venda das 23h50
        -- que fecha 00h10 pertence ao dia anterior. Misturar os dois é o bug clássico
        -- que faz o fechamento de dois dias virar um só.
        CREATE TABLE IF NOT EXISTS caixa_sessao (
          id                TEXT PRIMARY KEY,
          business_date     TEXT NOT NULL,
          operador_id       TEXT NOT NULL REFERENCES operador(id),
          operador_nome     TEXT NOT NULL,
          abertura_em       TEXT NOT NULL,
          fundo_troco_cent  INTEGER NOT NULL,      -- declarado pelo operador na abertura
          fechamento_em     TEXT,
          fechado_por       TEXT,
          status            TEXT NOT NULL DEFAULT 'aberto' CHECK (status IN ('aberto','fechado')),
          sincronizado      INTEGER NOT NULL DEFAULT 0
        );
        -- No máximo UM caixa aberto por vez neste terminal.
        CREATE UNIQUE INDEX IF NOT EXISTS uq_caixa_aberto
          ON caixa_sessao (status) WHERE status = 'aberto';

        -- ── MOVIMENTO DE CAIXA (sangria/suprimento) — APPEND-ONLY ─────────────
        -- Sem UPDATE e sem DELETE: corrigir = lançar um estorno que referencia o
        -- original. Os dois aparecem no relatório.
        CREATE TABLE IF NOT EXISTS caixa_movimento (
          id            TEXT PRIMARY KEY,
          sessao_id     TEXT NOT NULL REFERENCES caixa_sessao(id),
          tipo          TEXT NOT NULL CHECK (tipo IN ('sangria','suprimento','estorno')),
          valor_cent    INTEGER NOT NULL CHECK (valor_cent > 0),
          motivo        TEXT NOT NULL,
          destino       TEXT,
          operador_id   TEXT NOT NULL,
          autorizado_por TEXT,                     -- supervisor que liberou
          estorna_id    TEXT REFERENCES caixa_movimento(id),
          criado_em     TEXT NOT NULL
        );

        -- ── CATÁLOGO (espelho da nuvem; o caixa nunca espera rede pra vender) ──
        CREATE TABLE IF NOT EXISTS produto (
          id            TEXT PRIMARY KEY,
          plu           TEXT,                      -- codigo curto que o operador digita
          ean           TEXT,                      -- codigo de barras
          nome          TEXT NOT NULL,
          categoria     TEXT,
          preco_cent    INTEGER NOT NULL,
          unidade       TEXT NOT NULL DEFAULT 'UN',
          foto_local    TEXT,
          -- bloco fiscal: validado no CADASTRO, não na hora da venda
          ncm           TEXT,
          cest          TEXT,
          csosn         TEXT,
          cfop          TEXT,
          origem        INTEGER NOT NULL DEFAULT 0,
          pesavel       INTEGER NOT NULL DEFAULT 0,
          ativo         INTEGER NOT NULL DEFAULT 1,
          atualizado    TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_produto_plu ON produto(plu) WHERE plu IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_produto_ean ON produto(ean) WHERE ean IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_produto_cat ON produto(categoria) WHERE ativo = 1;

        -- ── VENDA ─────────────────────────────────────────────────────────────
        -- client_key: gerado UMA vez e reusado em todo reenvio. É o que impede a
        -- mesma venda de entrar duas vezes na nuvem quando a resposta se perde.
        CREATE TABLE IF NOT EXISTS venda (
          id              TEXT PRIMARY KEY,
          client_key      TEXT NOT NULL UNIQUE,
          sessao_id       TEXT NOT NULL REFERENCES caixa_sessao(id),
          business_date   TEXT NOT NULL,
          numero_local    INTEGER NOT NULL,
          operador_id     TEXT NOT NULL,
          subtotal_cent   INTEGER NOT NULL,
          desconto_cent   INTEGER NOT NULL DEFAULT 0,
          total_cent      INTEGER NOT NULL,
          documento       TEXT,                    -- CPF/CNPJ na nota
          status          TEXT NOT NULL DEFAULT 'aberta'
                            CHECK (status IN ('aberta','finalizada','cancelada')),
          cancelada_por   TEXT,
          cancel_motivo   TEXT,
          -- desfecho fiscal
          fiscal_status   TEXT NOT NULL DEFAULT 'pendente',
          nfce_chave      TEXT,
          nfce_protocolo  TEXT,
          criada_em       TEXT NOT NULL,
          finalizada_em   TEXT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS uq_venda_numero
          ON venda (business_date, numero_local);

        -- Snapshot do produto NO MOMENTO da venda: preço e tributo mudam depois, e a
        -- venda antiga precisa continuar contando o que realmente foi cobrado.
        CREATE TABLE IF NOT EXISTS venda_item (
          id              TEXT PRIMARY KEY,
          venda_id        TEXT NOT NULL REFERENCES venda(id) ON DELETE CASCADE,
          seq             INTEGER NOT NULL,
          produto_id      TEXT,
          codigo          TEXT,
          descricao       TEXT NOT NULL,
          qtd_milesimo    INTEGER NOT NULL,        -- 1.000 = 1 unidade (permite peso)
          preco_cent      INTEGER NOT NULL,
          desconto_cent   INTEGER NOT NULL DEFAULT 0,
          total_cent      INTEGER NOT NULL,
          unidade         TEXT,
          ncm             TEXT, cest TEXT, csosn TEXT, cfop TEXT, origem INTEGER,
          cancelado       INTEGER NOT NULL DEFAULT 0,
          cancelado_por   TEXT,
          cancel_motivo   TEXT
        );

        CREATE TABLE IF NOT EXISTS venda_pagamento (
          id            TEXT PRIMARY KEY,
          venda_id      TEXT NOT NULL REFERENCES venda(id) ON DELETE CASCADE,
          forma         TEXT NOT NULL,             -- dinheiro|debito|credito|pix|voucher
          valor_cent    INTEGER NOT NULL,
          troco_cent    INTEGER NOT NULL DEFAULT 0,
          -- retorno do TEF (vira o grupo <card> da NFC-e com tpIntegra=1)
          tef_aut       TEXT,
          tef_cnpj_cred TEXT,
          tef_bandeira  TEXT,
          tef_nsu       TEXT
        );

        -- ── FECHAMENTO CEGO ───────────────────────────────────────────────────
        -- O operador declara o que CONTOU sem ver o esperado. O sistema só então
        -- calcula a diferença. Se ele vê o esperado antes, digita o esperado e a
        -- conferência não significa nada.
        CREATE TABLE IF NOT EXISTS caixa_fechamento (
          id              TEXT PRIMARY KEY,
          sessao_id       TEXT NOT NULL REFERENCES caixa_sessao(id),
          forma           TEXT NOT NULL,
          declarado_cent  INTEGER NOT NULL,        -- o que o operador contou
          apurado_cent    INTEGER NOT NULL,        -- o que o sistema calculou
          diferenca_cent  INTEGER NOT NULL,
          justificativa   TEXT,
          criado_em       TEXT NOT NULL
        );

        -- ── AUDITORIA (append-only, tudo que é sensível) ──────────────────────
        CREATE TABLE IF NOT EXISTS auditoria (
          id           INTEGER PRIMARY KEY AUTOINCREMENT,
          evento       TEXT NOT NULL,
          operador_id  TEXT,
          autorizador  TEXT,
          detalhe      TEXT,
          criado_em    TEXT NOT NULL
        );

        -- ── OUTBOX: fila de sincronização com a nuvem ─────────────────────────
        -- Gravada na MESMA transação do fato que ela representa.
        CREATE TABLE IF NOT EXISTS outbox (
          id           INTEGER PRIMARY KEY AUTOINCREMENT,
          tipo         TEXT NOT NULL,              -- venda|caixa_sessao|movimento|fechamento
          ref_id       TEXT NOT NULL,
          client_key   TEXT NOT NULL,
          payload      TEXT NOT NULL,
          tentativas   INTEGER NOT NULL DEFAULT 0,
          ultimo_erro  TEXT,
          criado_em    TEXT NOT NULL,
          -- primeira falha REAL (sessão de pé): a expiração conta daqui, não do criado_em
          primeiro_erro_em TEXT,
          enviado_em   TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_outbox_pendente ON outbox(id) WHERE enviado_em IS NULL;

        -- ── PROMOÇÕES (espelho de pdv_promocoes, payload jsonb cru) ───────────
        -- O motor (Promocoes.cs) parseia na carga; JSON estranho é ignorado.
        CREATE TABLE IF NOT EXISTS promo (
          id            TEXT PRIMARY KEY,
          payload       TEXT NOT NULL,
          atualizado_em TEXT NOT NULL
        );

        -- ── CONFIGURAÇÃO SOLTA DO TERMINAL ────────────────────────────────────
        -- Chave/valor em vez de coluna nova em `terminal`: Migrar() só faz CREATE TABLE
        -- IF NOT EXISTS, não tem ALTER nem versionamento. Coluna nova só chegaria em
        -- máquina nova, e a que já roda ficaria sem — é assim que se perde config no campo.
        CREATE TABLE IF NOT EXISTS config (
          chave       TEXT PRIMARY KEY,
          valor       TEXT NOT NULL,
          atualizado  TEXT NOT NULL
        );

        -- ── DESFECHO FISCAL, POR TENTATIVA ────────────────────────────────────
        -- Uma venda pode ter VÁRIAS: rejeitou, o operador corrigiu e mandou de novo.
        -- Cada tentativa que chegou à SEFAZ queimou um número, e número pulado gera
        -- obrigação de inutilizar a faixa. Guardar só a última apagaria essa dívida.
        CREATE TABLE IF NOT EXISTS nfce_emissao (
          id            TEXT PRIMARY KEY,
          venda_id      TEXT NOT NULL REFERENCES venda(id) ON DELETE CASCADE,
          tentativa     INTEGER NOT NULL DEFAULT 1,
          caminho       TEXT NOT NULL,              -- nuvem | agente
          modo          TEXT,                       -- online | contingencia
          autorizado    INTEGER NOT NULL DEFAULT 0,
          contingencia  INTEGER NOT NULL DEFAULT 0,
          chave         TEXT,
          numero        INTEGER,
          serie         INTEGER,
          tp_amb        INTEGER,
          protocolo     TEXT,
          c_stat        TEXT,
          x_motivo      TEXT,
          qr_code       TEXT,
          dh_recbto     TEXT,
          v_nf_cent     INTEGER,
          emit_nome     TEXT, emit_cnpj TEXT, emit_ie TEXT, emit_endereco TEXT,
          erro          TEXT,
          sincronizada  INTEGER NOT NULL DEFAULT 0, -- guarda na nuvem confirmada
          criado_em     TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_nfce_emissao_venda ON nfce_emissao(venda_id);
        CREATE UNIQUE INDEX IF NOT EXISTS uq_nfce_emissao_chave
          ON nfce_emissao(chave) WHERE chave IS NOT NULL;

        -- ── TODA TENTATIVA DE TEF, INCLUSIVE AS QUE NÃO VIRARAM VENDA ─────────
        -- É o que permite responder "o cliente pagou mas o PDV não viu". Se a linha só
        -- nascesse junto com a venda, uma cobrança armada na maquininha que o caixa
        -- perdeu (queda de energia, app fechado) não deixaria rastro nenhum — e ninguém
        -- teria como cancelar, estornar ou sequer saber que existiu.
        CREATE TABLE IF NOT EXISTS tef_transacao (
          id                 TEXT PRIMARY KEY,
          venda_id           TEXT,                  -- NULL até a venda existir
          pagamento_id       TEXT,
          charge_id          TEXT NOT NULL,
          payment_identifier TEXT,
          tipo               TEXT NOT NULL,         -- credito | debito | pix
          valor_cent         INTEGER NOT NULL,
          parcelas           INTEGER NOT NULL DEFAULT 1,
          situacao           TEXT NOT NULL,         -- criando|aguardando|pago|recusado|cancelado|erro|orfa
          payment_status     TEXT,
          motivo             TEXT,
          aut TEXT, cnpj_cred TEXT, bandeira TEXT, tband TEXT, nsu TEXT, terminal TEXT,
          criado_em          TEXT NOT NULL,
          atualizado_em      TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_tef_pendente
          ON tef_transacao(situacao) WHERE situacao IN ('criando','aguardando','orfa');

        -- ── KDS: fila de preparo ──────────────────────────────────────────────
        -- O ticket NAO e dinheiro, entao ele muda de status no lugar (o append-only
        -- vale para venda e caixa). Mas os CARIMBOS de tempo sao gravados uma vez e
        -- nunca reescritos: e deles que sai o tempo de preparo no painel, e carimbo
        -- reescrito faz a loja parecer mais rapida do que ela e.
        CREATE TABLE IF NOT EXISTS kds_ticket (
          id            TEXT PRIMARY KEY,
          origem        TEXT NOT NULL,            -- balcao | ifood | encomenda
          ref_id        TEXT NOT NULL,            -- venda.id  |  order_id do iFood
          numero        TEXT NOT NULL,            -- o que o funcionario le no monitor
          cliente       TEXT,
          itens_json    TEXT NOT NULL,
          status        TEXT NOT NULL DEFAULT 'recebido',  -- recebido|preparando|pronto|cancelado
          criado_em     TEXT NOT NULL,
          preparo_em    TEXT,
          pronto_em     TEXT,
          -- O mesmo pedido do iFood volta no proximo polling e a mesma venda pode ser
          -- reprocessada. A unicidade e a defesa: repeticao nao vira ticket duplicado
          -- na tela de quem esta produzindo.
          UNIQUE(origem, ref_id)
        );
        CREATE INDEX IF NOT EXISTS ix_kds_aberto
          ON kds_ticket(criado_em) WHERE status IN ('recebido','preparando');
        """;
}
