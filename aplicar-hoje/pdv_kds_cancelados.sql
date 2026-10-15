-- KDS DO CAIXA, LISTA DOS QUE JA SAIRAM (14/10/2026): quais pedidos o iFood CANCELOU.
--
-- DESENHO PARA O AGENTE PRINCIPAL / O DONO. NAO FOI APLICADO. O caixa funciona sem ele.
--
-- O problema: pdv_kds_pedidos (feed) e pdv_kds_status devolvem
--   case when coalesce(o.entrega_status,'') <> '' then o.entrega_status else o.status end
-- entao um pedido despachado e DEPOIS cancelado pelo iFood chega ao caixa como "despachado"
-- ou "concluido", nunca como "cancelado". Medido em 14/10 (so SELECT): em 30 dias, 59 de 63
-- cancelados da Savassi e 16 de 16 do Castelo ficavam escondidos assim.
--
-- O que o caixa da lista dos que sairam (pdv-kds-finalizados) faz HOJE, sem este SQL: pergunta
-- primeiro por esta RPC; no 404 (PGRST202) lembra e cai no ultimo evento de entrega
-- (pdv_kds_entrega, que ja existe): codigo CANCELLED. Isso cobre so as ultimas 12 h e so a loja
-- que recebe os eventos (a Savassi; o Castelo nao tem evento nenhum) e perde o cancelamento
-- cujo ultimo evento e DELIVERY_GROUP_DISSOCIATED (16 de 63 na Savassi em 30 dias).
-- Com este SQL o caixa passa a ver todo cancelamento, de qualquer dia e das duas lojas, SEM
-- versao nova: o mesmo exe ja chama pdv_kds_cancelados.
--
-- Contrato (Pdv.Nucleo/Nuvem.cs CanceladosAsync / LerCancelados):
--   POST /rest/v1/rpc/pdv_kds_cancelados  {"_order_ids": ["...", ...]}  (ate 100 por chamada)
--   -> [{"order_id": "...", "cancelado_em": "2026-10-14T17:20:00+00:00" | null}]
--   so os pedidos CANCELADOS da lista; cancelado_em = o evento CANCELLED, ou null sem evento.
-- Nao mexe no feed nem na pdv_kds_status: os caixas antigos continuam iguais.
--
-- Comando para rodar (Prompt de Comando ou Git Bash, sem cd):
--   npx --yes supabase@2.116.0 db query --linked --workdir C:/Users/Waz/erp-american-day --file C:/Users/Waz/pdv-nativo/aplicar-hoje/pdv_kds_cancelados.sql
-- Deve mostrar, no fim, "pdv_kds_cancelados pronta" com porta_conhece_a_rpc = true.

BEGIN;

DO $guarda$
BEGIN
  IF to_regprocedure('public.has_role(uuid,app_role)') IS NULL
     OR to_regprocedure('public.user_store(uuid)') IS NULL
     OR to_regclass('public.ifood_orders') IS NULL
     OR to_regclass('public.ifood_entrega_eventos') IS NULL THEN
    RAISE EXCEPTION 'pdv_kds_cancelados: falta has_role, user_store, ifood_orders ou ifood_entrega_eventos nesta base. Nada foi alterado.';
  END IF;
END $guarda$;

-- Mesmo escopo de loja da pdv_kds_status e da pdv_kds_entrega (security definer: o terminal
-- e "so caixa" e nao le ifood_orders direto).
CREATE OR REPLACE FUNCTION public.pdv_kds_cancelados(_order_ids text[])
RETURNS TABLE(order_id text, cancelado_em timestamptz)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path TO 'public'
AS $function$
  select o.order_id,
         (select max(e.criado_em)
            from public.ifood_entrega_eventos e
           where e.order_id = o.order_id
             and e.code = 'CANCELLED') as cancelado_em
    from public.ifood_orders o
   where o.order_id = any((coalesce(_order_ids, '{}'::text[]))[1:100])
     and o.status = 'cancelado'
     and ( o.store = public.user_store(auth.uid())
        or public.has_role(auth.uid(), 'owner'::app_role)
        or public.has_role(auth.uid(), 'manager'::app_role) )
$function$;

-- Quem chama e o TERMINAL logado, nunca a chave publica (o grant viria de PUBLIC: licao de 08/09).
REVOKE ALL ON FUNCTION public.pdv_kds_cancelados(text[]) FROM PUBLIC, anon;
GRANT EXECUTE ON FUNCTION public.pdv_kds_cancelados(text[]) TO authenticated, service_role;

-- A porta da API do caixa (caixa_superficie) passa a conhecer a RPC, do mesmo jeito do
-- pdv_conta_evento.sql: so reescreve se o corpo vivo for o medido em 14/10 (md5 sem CR).
-- Corpo diferente: so avisa (a porta esta em observar e o caixa passa igual).
DO $porta$
DECLARE
  v_def  text;
  v_novo text;
  c_producao constant text := 'b1544690d3810ae33574158b7e943ced';   -- 14/10, so leitura
BEGIN
  IF to_regprocedure('public.caixa_superficie()') IS NULL THEN
    RAISE NOTICE 'pdv_kds_cancelados: sem caixa_superficie, passo pulado';
    RETURN;
  END IF;
  v_def := replace(pg_get_functiondef('public.caixa_superficie()'::regprocedure), chr(13), '');
  IF position('pdv_kds_cancelados' in v_def) > 0 THEN
    RETURN;
  END IF;
  IF md5(v_def) <> c_producao THEN
    RAISE NOTICE 'pdv_kds_cancelados: caixa_superficie() nao e a medida em 14/10 (md5 %); a linha da RPC NAO foi acrescentada (porta em observar: o caixa passa igual)', md5(v_def);
    RETURN;
  END IF;
  v_novo := regexp_replace(
    v_def,
    '\)\s*\$function\$\s*$',
    '),' || chr(10)
      || '    (''rpc'', ''POST'', ''pdv_kds_cancelados'', ''pedidos que o iFood cancelou, ate depois de despachados (lista dos que sairam do KDS), Pdv.Nucleo/Nuvem.cs CanceladosAsync'', false)'
      || chr(10) || '$function$' || chr(10));
  IF v_novo = v_def THEN
    RAISE EXCEPTION 'pdv_kds_cancelados: nao consegui acrescentar a linha em caixa_superficie. Nada foi alterado.';
  END IF;
  EXECUTE v_novo;
  IF to_regprocedure('public.caixa_pode_chamar(text, text)') IS NOT NULL
     AND NOT public.caixa_pode_chamar('POST', '/rest/v1/rpc/pdv_kds_cancelados') THEN
    RAISE EXCEPTION 'pdv_kds_cancelados: a porta da API continua sem pdv_kds_cancelados. Nada foi alterado.';
  END IF;
END $porta$;

NOTIFY pgrst, 'reload schema';

COMMIT;

SELECT 'pdv_kds_cancelados pronta' AS resultado,
       position('pdv_kds_cancelados' in COALESCE(pg_get_functiondef(to_regprocedure('public.caixa_superficie()')), '')) > 0
         AS porta_conhece_a_rpc;
