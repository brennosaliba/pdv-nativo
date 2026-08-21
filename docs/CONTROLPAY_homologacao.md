# Homologação PayGo — integração **ControlPay (WebService)** · PDV American Day

Roteiro v20260703 filtrado: dos 55 passos, **32 valem para "Pagamento via Web Service (ControlPay)"**.
Na planilha, a coluna **"Retorno do teste"** recebe o **`id` da intenção de venda** (o PDV mostra
na tela e grava em `tef_transacao.identificacao`; eu extraio do banco no fim).

**Ambiente:** sandbox `sandbox.controlpay.com.br/webapi` · PdC **114975** · terminal **6408**
(Terminal Virtual 114975 → físico 7027, instalação 127310) · pessoa **12247** · PayGo Windows
5.1.50.24 em Demonstração, pinpad COM5 · impressora ELGIN L42PRO.

**Antes de começar:** ELGIN ligada e com papel (fila vazia — job travado segura o comprovante) e
PayGo Windows aberto com o **TEF verde**. Autorizador: escolha **C6PAY** no menu de rede
(para Pix, **PIX C6 BANK**). Sandbox: a rede **REDE** nega qualquer valor com centavos.

---

## Bloco 1 — vendas básicas

| # | O que fazer no PDV | Esperado |
|---|---|---|
| **01** | Instalação/login — já feito (chave de integração validada, `Terminal/GetByPessoaId` = 200 OK) | ✔ concluído |
| **02** | Venda com o **valor máximo** que a automação aceita (ex.: **R$ 100.000,00**) · crédito · C6PAY | aprovada + comprovante |
| **03** | Venda de qualquer valor **à vista** (C6PAY · cartão · crédito · à vista) | aprovada + comprovante |
| **04** | Venda de **R$ 1.000,01** · crédito · C6PAY | **negada** — "NEGADA 01" na tela |
| **05** | Venda qualquer → no **menu de rede do PayGo aperte Esc** | negada — "OPERAÇÃO CANCELADA" |
| **06** | Venda qualquer · **crédito** (inserir cartão) | aprovada + comprovante · *(guarde: usada em 49 e 50)* |
| **07** | Venda qualquer · **débito** (inserir cartão) | aprovada + comprovante |
| **08** | Venda · **crédito parcelado pela LOJA em 99x** ⚠️ ligue antes: Configuração → TEF → "Perguntar o número de parcelas" | aprovada, "parcelada pela loja", 99 parcelas |
| 09 *(opc)* | Venda qualquer · C6PAY | vias diferenciadas (reduzida p/ cliente) |
| **10** | Venda qualquer · C6PAY | **duas vias diferenciadas** (cliente e lojista) — é o que o PDV imprime |
| **11** | Venda qualquer · **PIX** (rede **PIX C6 BANK**) | QR aparece; **aprova sozinho** em segundos ⚠️ não leia com app real: o QR é de demonstração |

## Bloco 2 — menu administrativo *(opcionais)*

| # | O que fazer | Esperado |
|---|---|---|
| 13/14/15 *(opc)* | TEF → **Menu administrativo** → relatório sintético / detalhado / resumido | relatório impresso |

## Bloco 3 — cancelamentos (estorno)

No PDV o estorno é **TEF → Estornar cartão/PIX** (autorização do gerente): ele cancela na rede **e**
cancela a venda no PDV no mesmo ato.

| # | O que fazer | Esperado |
|---|---|---|
| 17 *(opc)* | Venda de **R$ 1,00** · C6PAY | aprovada (será cancelada no 22) |
| 18 *(opc)* | Venda de **R$ 2,00** · C6PAY | aprovada (cancelada no 20) |
| **19** | Venda de **R$ 12.345,67** · C6PAY | aprovada (cancelada no 21) |
| 20 *(opc)* | Estornar a venda do **18** | cancelamento aprovado + comprovante |
| **21** | Estornar a venda do **19** | cancelamento aprovado + comprovante |
| 22 *(opc)* | Estornar a venda do **17** | cancelamento aprovado + comprovante |
| 23 *(opc)* | Cancelar a venda do **02** pelo **menu administrativo** do PayGo | cancelamento aprovado (o PDV detecta e oferece cancelar a venda) |

## Bloco 4 — falhas e casos limite

| # | O que fazer | Esperado |
|---|---|---|
| **24** | Venda; **desligue o PC no botão** quando o pinpad pedir o cartão | venda não realizada; ao religar, o PDV reconcilia (linha vira órfã/recusada, nunca paga) |
| **30** | Venda de **R$ 1.003,00** · C6PAY | aprovada com a mensagem longa: "TRANSAÇÃO DE TESTE APROVADA. CÓDIGO AUTORIZAÇAO 13456789 TRANSACAO NAO PRODUTIVA" |
| **45** | Venda de **R$ 1.020,00** · C6PAY · **aproximar** cartão | pinpad pede "APROXIME, INSIRA OU PASSE"; aprovada |
| **46** | Venda de **R$ 999,00** · C6PAY · **aproximar** | aprovada **sem pedir senha** |
| **52** | Venda PIX → **Esc na tela do QR** | negada — "OPERAÇÃO CANCELADA" |
| **53** | Venda de **R$ 500,00** · **PIX C6 BANK** | aprovada (aprovação automática) |
| **54** | Estornar a venda do **53** | **cancelamento NEGADO** — "TRANSAÇÃO NEGADA PELO HOST" *(é o resultado correto)* |

## Bloco 5 — exclusivos do ControlPay (API)

| # | O que é | Como fica |
|---|---|---|
| **47** | Consulta de terminais (`Terminal/GetByPessoaId`) | ✔ já executado — 200 OK |
| **48** | Consultar status da transação do **passo 02** (`IntencaoVenda/GetById`) | eu rodo e guardo o JSON |
| **49** | Consultar status da transação do **passo 06** | eu rodo e guardo o JSON |
| **50** | **Callback** da venda do passo 06 (`Callback/Insert` + POST recebido) | eu cadastro a URL e capturo o POST |

---

## Não se aplicam ao ControlPay (marcar assim na planilha)

12, 16, 25, 26–29, 31–44, 51, 55 — são de troca de arquivos TXT / DLL / Android.
Motivo: no ControlPay **não existe confirmação/desfazimento pela automação** (quem confirma é o
PayGo Windows) e não há captura de dado genérico nem transação pendente exposta por API.

## Fechamento

1. Preencho a planilha `Planilha de testes v20260703.xlsx` com o `id` da intenção de cada passo
   (leio de `tef_transacao`) e marco os não aplicáveis.
2. Coleta de logs: `C:\PAYGO\PGLogCollector.exe` (gera o pacote do PayGo Windows).
3. Anexar planilha + logs no chamado do **Jira** (portal 16) e aguardar até 5 dias úteis.

## Dicas ganhas nesta bateria

- **Fila da impressora travada segura o comprovante** — se a ELGIN ficar em "Error", limpe a fila
  (o PDV já não segura mais a venda: grava `pago` antes de imprimir).
- **Timeout de 40 s**: se a intenção não fechar, o PDV devolve a tela e marca a linha como `orfa`
  com aviso — **não cobre de novo**; confira na janela do PayGo e, se aprovou, estorne.
- **Depois de aprovado não existe "Cancelar venda"** na tela: conclua e use TEF → Estornar.
- Modo de homologação ligado (Configuração → TEF): entra sem senha, autoriza sem PIN, janela
  ajustável. **Desligar antes de operar de verdade.**
