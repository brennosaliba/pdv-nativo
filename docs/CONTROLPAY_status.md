# Homologação ControlPay — status em 24/08/2026 11:40

Integração **Pagamento via Web Service (ControlPay)**. Tudo abaixo foi conferido contra
`C:/ProgramData/PdvNativo/pdv.db` (tabelas `homolog_passo`, `tef_transacao`, `auditoria`),
aberto **somente leitura**. O que não aparece na base **não está afirmado aqui**.

## Placar: 21 de 23 passos obrigatórios fechados

Dos 55 passos da planilha, **23 são obrigatórios e se aplicam** a esta integração,
9 são opcionais e 23 não se aplicam ("Pagamento via Web Service").

| | passos | situação |
|---|---|---|
| Obrigatórios fechados | 01 02 03 04 05 06 07 08 10 11 19 21 30 45 46 47 48 49 50 53 54 | **21** |
| Obrigatórios **em aberto** | **24** (queda de energia) · **52** (Esc na tela do QR do Pix) | **2** |
| Opcionais fechados | 17 18 20 22 | 4 |
| Opcionais em branco | 09 13 14 15 23 | 5 |
| Não se aplicam | 12 16 25–29 31–44 51 55 | 23 |

Planilha preenchida: `docs/evidencias-controlpay/Planilha de testes v20260703 - PREENCHIDA.xlsx`
(a versão anterior ficou salva como `... - ANTERIOR.xlsx` para comparação).

### Evidência de cada passo fechado

| Passo | Intenção | O que a resposta crua do PayGo mostra |
|---|---|---|
| 01 | 200 OK | instalação (PdC 114975) + login/consulta na API |
| 02 | **167517** | R$ 100.000,00 crédito · `030-000 = TRANSACAO AUTORIZADA` · aut 094787 · NSU 741684 |
| 03 | **167518** | R$ 5,00 crédito à vista · aut 094787 · NSU 741686 |
| 04 | **167523** | R$ 1.000,01 crédito · recusada · `NEGADA 01` |
| 05 | **167516** | operação abortada na seleção de rede · `OPERACAO CANCELADA` |
| 06 | **167513** | R$ 2,00 crédito · `040-000 = MASTERCARD CREDITO` · NSU 741678 |
| 07 | **167509** | R$ 12,00 débito · `040-000 = VISA ELECTRON` · NSU 741672 |
| 08 | **167563** | R$ 990,00 crédito, 99 parcelas pela loja · NSU 742421 |
| 10 | **167564** | vias `713-*` (Via Cliente) e `715-*` (Via Estabelecimento) + `029-*` (completa) |
| 11 | **167507** | Pix QR · `010-000 = PIX C6 BANK` · E2E `E1928374620260821213986g6pAvf343` |
| 17 | **167566** | R$ 1,00 crédito (venda para o cancelamento do 22) |
| 18 | **167567** | R$ 2,00 crédito (venda para o cancelamento do 20) |
| 19 | **167572** | R$ 12.345,67 crédito · aut 297061 · NSU 682424 |
| 20 | **167567** | estorno · comprovante `ESTORNO:742429 VALOR:R$ 2,00` · NSU 44 |
| 21 | **167572** | estorno · `***CANCELAMENTO*** / CANCELAMENTO DE VENDA` R$ 12.345,67 · NSU 6 |
| 22 | **167566** | estorno · comprovante `ESTORNO:742427 VALOR:R$ 1,00` · NSU 46 |
| 30 | **167575** | `030-000` = a mensagem de **80 caracteres**, inteira (conferida byte a byte) |
| 45 | **167576** | R$ 1.020,00 crédito · via com `USO DE SENHA PESSOAL` |
| 46 | **167577** | R$ 999,00 crédito · vias **sem** `USO DE SENHA PESSOAL` e **sem** `TRANSACAO AUTORIZADA COM SENHA` |
| 47 | 200 OK | `Terminal/GetByPessoaId` (pessoaId 12247, terminal 6408 → físico 7027) |
| 48 | 167517 | `IntencaoVenda/GetById` da venda do passo 02 → status 10 Creditado |
| 49 | 167513 | idem para a venda do passo 06 |
| 50 | 200 OK | `Callback/Insert` → `https://erp.americandaybrasil.com.br/paygo-callback` |
| 53 | **167560** | Pix R$ 500,00 · E2E `E19283746202608241411Pmggfpq7Cbr` |
| 54 | **167560** | cancelamento **NEGADO** — `TRANSACAO NEGADA PELO HOST` (é o resultado correto deste passo) |

## As três redes têm estes nomes exatos

Valores realmente gravados na coluna `rede` de `tef_transacao`:

| nome exato | uso | ocorrências |
|---|---|---|
| `REDE` | cartão | 19 |
| `C6 PAY` | cartão — **com espaço** | 4 |
| `PIX C6 BANK` | Pix | 4 |

⚠️ **`C6PAY` sem espaço não existe.** Configurado assim, o PayGo devolve
`SERVICO NAO HABILITADOO` — foi o que derrubou 167552, 167553, 167555 e 167557 hoje entre
11:03 e 11:07. Config atual: `tef_cpay_adquirente = C6 PAY`, `tef_cpay_adquirente_pix = PIX C6 BANK`.

## Correção: na REDE os centavos **são** o código de resposta

A versão anterior deste documento dizia que *"a REDE de teste nega qualquer valor com
centavos"*. **Está errado.** Na REDE os centavos do valor viram o código de resposta:

| centavos | resultado observado | evidência |
|---|---|---|
| `.00` | **aprova** | 167517 (R$ 100.000,00), 167558 (R$ 990,00), 167564 (R$ 10,00), 167576 (R$ 1.020,00) |
| `.01` | `NEGADA 01` | 167523, 167524 (R$ 1.000,01) |
| `.57` | `TRANSACAO NAO PERMITIDA` (código 57) | 167497 (R$ 10,57) |
| `.67` | `NEGADA 67` | 167568, 167569 (R$ 12.345,67) |

A prova mais limpa: **R$ 12.345,67 foi negada duas vezes na REDE (`NEGADA 67`) e aprovada na
C6 PAY** (167572, aut 297061). Ou seja, o valor não é "inválido" — o comportamento é da rede.

Consequência prática: os **valores mágicos do roteiro** (R$ 1.003,00 = mensagem de 80 chars,
etc.) são do autorizador de testes **C6 PAY**, não da REDE. Rodar esses valores na REDE não
produz o comportamento que o roteiro pede.

⚠️ **A pré-seleção de rede não é determinística.** Mesmo com `tef_cpay_adquirente = C6 PAY`,
as transações de hoje se dividiram: 167563, 167564, 167566, 167567 e 167576 saíram pela `REDE`;
167572, 167575 e 167577 pela `C6 PAY`. Por isso **a planilha não afirma adquirente
pré-selecionado em passo nenhum** — ver abaixo.

## O que foi corrigido na planilha

- **Passo 3 — afirmação falsa removida.** A observação dizia *"autorizador C6PAY
  pre-selecionado"*, mas a resposta crua da 167518 traz `010-000 = REDE`, e a config
  `tef_cpay_adquirente` só passou a existir às **19:12:15 de 21/08** — depois da transação
  (19:06:23). A observação agora descreve só o que a resposta sustenta.
- Passos 2, 4, 5, 6, 7, 11 — observações reescritas com `030-000`, autorização e NSU.
- Passos 8, 10, 17, 18, 19, 20, 21, 22, 30, 45, 46, 53, 54 — preenchidos (estavam em branco).
- Passos 24 e 52 — **deixados em branco de propósito**, porque não foram executados.

Três passos ficaram com ressalva escrita na própria observação, porque o dado que voltou não
prova o objeto do teste (só a aprovação):

- **03** — o "à vista" só existe no que o PDV *enviou* (`aVista = true`); o comprovante da REDE
  não imprime linha de financiamento. Na C6 PAY o comprovante imprime `VENDA CREDITO A VISTA`.
- **04** — em recusas o PDV **não grava** o dump bruto; a mensagem `NEGADA 01` vem do campo
  `mensagemRespostaAdquirente` da intenção.
- **08** — nada que voltou confirma 99 parcelas: `732-000` e `018-000` não existem em nenhuma
  resposta da base, e o comprovante não tem linha de parcelamento. O 99 está no que o PDV
  enviou (`quantidadeParcelas = 99`, `parcelamentoAdmin = false`) e na coluna `parcelas`.

## O que falta

### Passo 52 — Esc na tela do QR do Pix
Nunca foi executado como passo do roteiro (não há linha em `homolog_passo`). Existe uma
transação Pix cancelada em 21/08 (167500, `OPERACAO CANCELADA`), mas **nada liga esse
cancelamento à tela do QR** — pode ter sido abortada antes. Refazer: venda Pix → **Esc** na
tela do QR → conferir `OPERACAO CANCELADA`.

### Passo 24 — queda de energia (⚠️ pode reprovar como está hoje)
Também nunca foi executado como passo do roteiro. O que existe é um religamento acidental em
21/08 18:58:55, e **o resultado foi o contrário do que o passo exige**:

```
18:58:55  religamento — intenção 167511 estava APROVADA sem venda; marcada para estorno
18:58:56  religamento — intenção 167515 estava APROVADA sem venda; marcada para estorno
```

Na `tef_transacao`, 167511 e 167515 estão com `situacao = 'pago'` e motivo
*"APROVADA SEM VENDA (religamento) — estornar pelo menu TEF"*. O critério do passo 24 é que a
linha vire **órfã/recusada e nunca paga**. A venda de fato não foi concluída (não há
`venda_finalizada` para nenhuma das duas), mas a linha do TEF **ficou paga**.

**Antes de executar o passo 24, decidir o comportamento no religamento**: hoje o PDV marca a
transação como paga e deixa o estorno para o operador. Se a PayGo exigir "nunca paga", isso é
mudança de código, não de roteiro.

### Opcionais em branco
09, 13, 14, 15, 23 — a coluna Obrigatoriedade já diz OPCIONAL; podem ir em branco.

## Como executar o que falta

**TEF → ▶ Roteiro de homologação** → "Executar passo N". O PDV roda o roteiro com **o mesmo
código da venda normal**, marca ✔/✗ e grava o id da intenção em `homolog_passo`.

Precisam de gesto humano, fora do automático:
- **52** — apertar **Esc** na tela do QR do Pix;
- **24** — desligar o PC no meio da venda (ler a ressalva acima antes).

Para reproduzir os valores mágicos do roteiro, garanta que a transação saia pela **C6 PAY**
(confira `010-000` na resposta depois, porque a pré-seleção não é garantida).

## Fiscal — resolvido (situação inalterada desde 21/08)

- **Nenhuma NFC-e foi emitida desde 21/08 18:54** — confirmado: `nfce_emissao` tem 9 linhas e a
  última é de `2026-08-21T18:54:07`. O PDV segue em **modo recibo**, inclusive na venda de
  R$ 100.000,00 e em todas as de hoje.
- As **7 NFC-e** dos primeiros testes (nNF 28→34) foram **canceladas na SEFAZ**.
- ⚠️ **2 notas antigas não puderam ser canceladas** (`cStat 501 — prazo superior ao previsto`;
  o limite da NFC-e é 30 min): **14:45:06** (chave …44196587) e **17:32:33** (…84346650).
  Precisam de tratamento contábil.
- ⚠️ Antes de voltar a operar: **religar o fiscal** (Configuração → FISCAL) e **desligar o modo
  de homologação**.

## Pendências menores

- **167511** e **167515** (R$ 2,00 cada) continuam aprovadas sem venda, marcadas para estorno.
  Estornar pelo menu do PayGo.
- Produtos de teste na categoria **TESTE PAYGO**. Um "Sincronizar" desativa todos eles.
- Reforçar a evidência dos passos 04, 07 e 08 salvando o JSON do `IntencaoVenda/GetById`
  (ou `GetByFiltros`) de 167523, 167509 e 167563 em `docs/evidencias-controlpay/`, no mesmo
  padrão dos arquivos `p48-*` e `p49-*` que já existem.
