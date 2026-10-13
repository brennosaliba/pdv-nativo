-- COMANDA ABERTA NO PDV (13/10/2026): o rastro que sai do terminal.
--
-- O caixa 1.0.32 abre comandas por mesa ou cliente e manda a cozinha produzir ANTES do dinheiro.
-- Tudo que compensa isso sobe para ca pela outbox do caixa (tipo conta_evento):
--   conta_cancelada_com_itens        comanda cancelada com item (pedido ou nao): motivo + PIN de outro gerente
--   conta_item_retirado_apos_cozinha item tirado depois de ir para a cozinha (PIN de outro gerente)
--   conta_atravessou_fechamento      comanda que ficou aberta no fechamento do turno (uma linha por comanda)
--   conta_lote_reenviado             lote que nao chegou a TV e foi mandado de novo
--   conta_cancelada_automatica       comanda vazia cancelada sozinha (fechamento, ou conta_vazia_horas)
--   conta_fechada_sem_venda          comanda inteira coberta por cupom de cortesia (nao ha o que cobrar)
--
-- Esta RPC SO REGISTRA (padrao adotado). Lancar desperdicio por ficha tecnica dos itens com lote fica
-- como opcao futura, aqui mesmo. Idempotente por p_client_key (o caixa reenvia ate confirmar).
-- Ate o SQL rodar o caixa espera na fila com 404 PGRST202, sem travar nada e sem prazo de descarte.
--
-- Comando para rodar (Prompt de Comando ou Git Bash, sem cd):
--   npx --yes supabase@2.116.0 db query --linked --workdir C:/Users/Waz/erp-american-day --file C:/Users/Waz/pdv-nativo/aplicar-hoje/pdv_conta_evento.sql
-- Deve mostrar: CREATE TABLE / CREATE FUNCTION sem erro e, no fim, "pdv_conta_evento pronta".

CREATE TABLE IF NOT EXISTS public.pdv_conta_eventos (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  client_key    text NOT NULL UNIQUE,            -- "<contaId>:<evento>:<seq>", vem do caixa
  store         text,                            -- nome da loja do terminal (loja_nome)
  evento        text NOT NULL,
  conta_id      text,                            -- generated from payload->'conta'->>'id'
  conta_numero  integer,
  valor_tabela_cent bigint,
  payload       jsonb NOT NULL,
  registrado_por uuid DEFAULT auth.uid(),
  criado_em     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_pdv_conta_eventos_store_dia ON public.pdv_conta_eventos (store, criado_em DESC);
CREATE INDEX IF NOT EXISTS ix_pdv_conta_eventos_evento ON public.pdv_conta_eventos (evento, criado_em DESC);

ALTER TABLE public.pdv_conta_eventos ENABLE ROW LEVEL SECURITY;
-- Ninguem le ou escreve a tabela por fora: so a RPC (security definer) escreve, e o painel le por RPC propria depois.
REVOKE ALL ON public.pdv_conta_eventos FROM PUBLIC, anon, authenticated;

CREATE OR REPLACE FUNCTION public.pdv_conta_evento(
  p_client_key text,
  p_store      text,
  p_evento     text,
  p_payload    jsonb
) RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_id bigint;
BEGIN
  IF p_client_key IS NULL OR length(p_client_key) < 8 THEN
    RAISE EXCEPTION 'p_client_key obrigatorio' USING ERRCODE = '22023';
  END IF;
  IF p_evento IS NULL OR p_evento = '' THEN
    RAISE EXCEPTION 'p_evento obrigatorio' USING ERRCODE = '22023';
  END IF;

  INSERT INTO public.pdv_conta_eventos (client_key, store, evento, conta_id, conta_numero, valor_tabela_cent, payload)
  VALUES (
    p_client_key,
    p_store,
    p_evento,
    p_payload -> 'conta' ->> 'id',
    NULLIF(p_payload -> 'conta' ->> 'numero', '')::integer,
    NULLIF(p_payload ->> 'valor_tabela_cent', '')::bigint,
    COALESCE(p_payload, '{}'::jsonb)
  )
  ON CONFLICT (client_key) DO NOTHING
  RETURNING id INTO v_id;

  -- Opcao futura (decisao do dono): lancar desperdicio por ficha tecnica dos itens com lote
  -- quando p_evento = 'conta_cancelada_com_itens'. Por ora so registra.

  RETURN jsonb_build_object('ok', true, 'novo', v_id IS NOT NULL, 'id', v_id);
END;
$$;

-- Quem chama e o TERMINAL logado (sessao do caixa), nunca a chave publica.
REVOKE ALL ON FUNCTION public.pdv_conta_evento(text, text, text, jsonb) FROM PUBLIC, anon;
GRANT EXECUTE ON FUNCTION public.pdv_conta_evento(text, text, text, jsonb) TO authenticated, service_role;

NOTIFY pgrst, 'reload schema';
-- A porta da API do caixa (caixa_superficie) passa a conhecer a RPC nova, do mesmo jeito do SQL 168.
-- Sem a funcao (base sem a porta) ou com o corpo diferente do esperado, so avisa: a porta esta em
-- observar e o caixa passa igual.
DO $porta$
DECLARE
  v_def  text;
  v_novo text;
BEGIN
  IF to_regprocedure('public.caixa_superficie()') IS NULL THEN
    RAISE NOTICE 'pdv_conta_evento: sem caixa_superficie, passo pulado';
    RETURN;
  END IF;
  v_def := replace(pg_get_functiondef('public.caixa_superficie()'::regprocedure), chr(13), '');
  IF position('pdv_conta_evento' in v_def) > 0 THEN
    RETURN;
  END IF;
  v_novo := regexp_replace(
    v_def,
    '\)\s*\$function\$\s*$',
    '),' || chr(10)
      || '    (''rpc'', ''POST'', ''pdv_conta_evento'', ''rastro da comanda aberta, Pdv.Nucleo/Drenagem.cs conta_evento'', false)'
      || chr(10) || '$function$' || chr(10));
  IF v_novo = v_def THEN
    RAISE NOTICE 'pdv_conta_evento: nao consegui acrescentar a linha em caixa_superficie (porta em observar: o caixa passa igual)';
    RETURN;
  END IF;
  EXECUTE v_novo;
END $porta$;

SELECT 'pdv_conta_evento pronta' AS resultado;
