# Homologação ControlPay — status em 21/08/2026 19:20

## Onde estamos: 12 de 32 passos fechados

**Prontos** (planilha já preenchida em `docs/evidencias-controlpay/`):

| Passo | Evidência | O que foi |
|---|---|---|
| 01 | 200 OK | instalação do PayGo (PdC 114975) + login/consulta na API |
| 02 | **167517** | venda de R$ 100.000,00 no crédito |
| 03 | **167518** | à vista R$ 5,00 |
| 04 | **167523** | R$ 1.000,01 → **NEGADA 01** (e 167524 no débito) |
| 05 | **167516** | Esc no menu de rede → OPERAÇÃO CANCELADA |
| 06 | **167513** | crédito aprovado |
| 07 | **167509** | débito aprovado |
| 11 | **167507** | Pix QR (PIX C6 BANK), aprovação automática |
| 47 | 200 OK | `Terminal/GetByPessoaId` |
| 48 | 167517 | `IntencaoVenda/GetById` da venda do passo 02 → 10 Creditado |
| 49 | 167513 | idem para a venda do passo 06 |
| 50 | 200 OK | `Callback/Insert` → `https://erp.americandaybrasil.com.br/paygo-callback` |

**Faltam 8 vendas + 4 estornos** (todos com cartão na mão):
08 (99x) · 10 (vias) · 17 · 18 · 19 · 20 · 21 · 22 · 24 (queda de energia) · 30 · 45 · 46 · 52 · 53 · 54.
Opcionais: 09, 13, 14, 15, 23.

## O jeito rápido de terminar: TEF → Roteiro de homologação

O PDV agora executa o roteiro sozinho — **usando o mesmo código da venda** (a evidência é da
automação real, não de script). Abra **TEF → ▶ Roteiro de homologação**, clique em
**"Executar passo N"** e só encoste/insira o cartão quando o pinpad pedir. Ele marca ✔/✗,
guarda o id da intenção (tabela `homolog_passo`) e já sabe qual é o próximo. "Pular este passo"
serve para os que você quiser fazer à mão.

Fora do roteiro automático (precisam de gesto específico):
- **05 / 52** — apertar **Esc** no menu de rede / na tela do QR (o 05 já está feito);
- **24** — desligar o PC no meio da venda;
- **45 / 46** — o roteiro dispara a venda, você **aproxima** o cartão.

## O que mudou no PDV por causa dos testes de hoje

- **Autorizador fixo**: a venda vai com `adquirente = C6PAY` (Pix: `PIX C6 BANK`) — o PayGo
  **não abre mais o menu de rede**. Foi o que causou os `-2573`: a REDE de teste nega qualquer
  valor com centavos. Config: `tef_cpay_adquirente` / `tef_cpay_adquirente_pix` (vazio = a PayGo roteia).
- **Timeouts**: 60 s no cartão e **180 s no Pix** (o QR fica na tela esperando o cliente).
  Estourou? A tela volta para o operador, a linha fica `orfa` com aviso e **nunca** vira paga.
- **Comprovante não segura a venda**: a impressão saiu do caminho crítico. A fila travada da
  ELGIN prendia o caixa em "aguardando o cliente" com a venda já cobrada.
- **Sem parcelamento**: 1 parcela vai como `aVista=true` — o pinpad não pergunta financiamento.
- **Depois de aprovado não existe "Cancelar venda"**: conclui e estorna em TEF → Estornar.
- **Modo de homologação**: entra sem senha, autoriza sem PIN, janela ajustável.

## Fiscal — resolvido, com uma ressalva

- As **7 NFC-e** emitidas nos primeiros testes (nNF 28→34, 18:29→18:54) foram **canceladas na
  SEFAZ** (retorno `573 Duplicidade de Evento` na segunda tentativa = já cancelada).
- O PDV está em **modo recibo** desde 18:54: **nada mais gera nota** — inclusive a venda de
  R$ 100.000,00 (venda #10) e as seguintes. Confirmado no log do servidor fiscal: última
  emissão foi a nNF 34.
- ⚠️ **2 notas antigas não puderam ser canceladas** (`cStat 501 — prazo superior ao previsto`,
  o limite da NFC-e é 30 min): **14:45:06** (chave …44196587) e **17:32:33** (…84346650), de
  testes anteriores ao TEF. Precisam de tratamento contábil.
- ⚠️ Antes de voltar a operar: **religar o fiscal** (Configuração → FISCAL) e **desligar o modo
  de homologação**.

## Pendências menores

- Duas cobranças de R$ 2,00 (intenções **167511** e **167515**) ficaram aprovadas sem venda
  quando o PDV travou na impressão; o cancelamento por API não foi executado pelo PayGo
  (continuam "Creditado"). Não afetam o roteiro; dá para estornar pelo menu do PayGo.
- Produtos de teste na categoria **TESTE PAYGO** (12 itens com os valores do roteiro). Um
  "Sincronizar" desativa todos eles — é só avisar que eu recrio.
