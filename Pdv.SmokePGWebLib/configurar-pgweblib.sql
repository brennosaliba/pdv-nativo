-- Configura ESTA instância do PDV para o Pay&Go pela biblioteca (PGWebLib.dll), 64 bits.
-- Banco local do caixa (Banco.Arquivo em Pdv.Nucleo/Banco.cs):
--   C:\ProgramData\PdvNativo\pdv.db
-- Aplicar com o PDV FECHADO:
--   C:\adb\sqlite3.exe "C:\ProgramData\PdvNativo\pdv.db" < Pdv.SmokePGWebLib\configurar-pgweblib.sql
-- Ou pela tela de Configuração (cartão 4 = "PayGo (biblioteca)"), que grava as mesmas chaves.
--
-- Essa configuração é LOCAL: `tef_provedor` sai da tabela `config` do SQLite desta máquina
-- (Servicos.cs:225, via Vendas.Config). Nada disso sobe para o Supabase, então mexer aqui
-- não alcança o caixa da loja.
--
-- ── O QUE MUDOU EM 07/09/2026 ───────────────────────────────────────────────────────
--
-- O dono desinstalou o PayGo Windows e o Warsaw e passou a usar o kit avulso da biblioteca
-- (20260820-Integracao-PGWebLib_v4.1.50.924). Isso resolveu três problemas de uma vez:
--
--   1. A biblioteca NÃO precisa do cliente PayGo instalado. Ela sobe sozinha.
--   2. O kit traz a DLL de 64 bits, então o PDV continua no build normal x64. Acabou a
--      necessidade do publish x86 separado.
--   3. Sem o Warsaw, o executável que carrega a DLL deixa de ficar travado, e o
--      `--atualizar` das lojas volta a funcionar. (Com o Warsaw instalado, o exe não podia
--      mais ser renomeado, sobrescrito nem apagado. Está medido em docs/TEF_PAYGO_homologacao.md.)
--
-- Medido com a DLL nova, processo de 64 bits, pasta de trabalho vazia:
--   PW_iInit -> PWRET_OK em meio segundo, sem a espera de minutos que o Warsaw causava.
--   PWINFO_IDLEPROCTIME = 551231235959 (o sentinela de "nunca") e a lista de operações de
--   VENDA devolve PWRET_NOTINST, porque o terminal ainda não foi instalado nesta pasta.
--   O menu administrativo responde normalmente, com INSTALACAO disponível.
--
-- Ou seja: falta só rodar a INSTALAÇÃO pelo menu do TEF, com o ponto de captura e a senha.
-- Esse passo é do dono, porque exige digitar a senha de instalação.

BEGIN;
INSERT INTO config (chave, valor, atualizado) VALUES
  ('tef_habilitado',         '1',                               strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime')),
  ('tef_provedor',           'pgweblib',                        strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime')),
  ('tef_pgweb_dll',          'C:\PGWebLib\x64',                 strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime')),
  ('tef_pgweb_dir',          'C:\ProgramData\PdvNativo\pgweb64', strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime')),
  ('tef_pgweb_porta_pinpad', '5',                               strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime')),
  -- producao (padrao) ou homologacao. Vira PW_iSetEnvironment, chamada antes do PW_iInit.
  -- Esta maquina e a de homologacao; loja fica em producao, que e o padrao quando a chave falta.
  ('tef_pgweb_ambiente',     'homologacao',                     strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime')),
  -- 1 desenha o QR do Pix na TELA DO CAIXA (o roteiro pede isso no passo 55: Esc na tela do QR
  -- cancela a venda). Sem esta chave o cliente le o QR no pinpad, que e o que as lojas fazem hoje.
  ('tef_pgweb_qr_na_tela',   '1',                               strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime')),
  -- As unicas redes que o roteiro usa. O menu CONTINUA aparecendo (o passo 05 manda apertar Esc
  -- nele), so mostra menos linhas. Em branco volta a mostrar todas, que e o certo na loja.
  ('tef_pgweb_redes',        'C6PAY, REDE, PIX C6 BANK',        strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime'))
ON CONFLICT(chave) DO UPDATE SET valor = excluded.valor, atualizado = excluded.atualizado;
COMMIT;

-- A pasta de trabalho PRECISA EXISTIR antes do PW_iInit: a DLL não a cria e o PDV cria só
-- na inicialização do provedor. Se preferir garantir antes:
--   mkdir "C:\ProgramData\PdvNativo\pgweb64"
--
-- Conferir:
--   SELECT chave, valor FROM config WHERE chave LIKE 'tef_%' ORDER BY chave;
--
-- Voltar ao ControlPay (o que estava antes):
--   UPDATE config SET valor = 'controlpay' WHERE chave = 'tef_provedor';
--   DELETE FROM config WHERE chave IN ('tef_pgweb_dll','tef_pgweb_dir','tef_pgweb_porta_pinpad');
