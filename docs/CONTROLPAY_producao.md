# Virada para PRODUÇÃO — TEF ControlPay (PayGo)

Liberação recebida em 28/08/2026 para **American Day Savassi**
(CNPJ 62.177.839/0002-38 — confere com `stores.name = 'American Day Savassi'`).

> ⚠️ A chave de integração (token) **nunca** é escrita neste arquivo, no banco `config`,
> no git ou em conversa. Ela é digitada direto na tela e guardada no cofre DPAPI da
> máquina (`C:\ProgramData\PdvNativo\seg\seg.dat`, escopo LocalMachine — não abre em
> outro PC). Mesma regra para a senha técnica.

## Onde vai cada dado da liberação

| Dado da PayGo | Onde entra |
|---|---|
| **ID de Instalação** `263397` | **PayGo Windows** desta máquina (ativação do ponto de captura). Não é campo do PDV. |
| **Senha do Terminal** `7B54666D` | **PayGo Windows**, na mesma ativação. |
| **ID Terminal Virtual** `25469` | PDV → Configuração → TEF → **ID do terminal** (hoje `6408`, do sandbox) |
| **Chave de Integração (token)** | PDV → Configuração → TEF → **Chave de integração** (campo mascarado → cofre DPAPI) |
| **ID da pessoa** — *não veio na liberação* | PDV → Configuração → TEF → **ID da pessoa** (hoje `12247`, do sandbox). Está no portal ControlPay / no login do PayGo. **Sem ele o botão de teste não roda** (`Terminal/GetByPessoaId`). |
| **Senha técnica** | Campo próprio. Padrão PayGo = `314159`; a "Senha do Terminal" acima é da *instalação*, não é esta. Confirmar com a PayGo. |

## Ordem da virada

1. **PayGo Windows**: ativar o terminal com `263397` / `7B54666D` até o TEF ficar verde.
   É ele que fala com o pinpad — o PDV só cria a intenção e acompanha.
2. **PDV → Configuração → TEF (CARTÃO / PIX)**, provedor já em *ControlPay*:
   - Ambiente: **Sandbox → Produção** (`api.controlpay.com.br`)
   - Chave de integração: colar o token
   - ID do terminal: `25469` · ID da pessoa: (produção) · Senha técnica: confirmar
   - **Rede p/ PIX**: hoje está `PIX C6 BANK`, que é **autorizador de testes**.
     Em produção deixar **vazio** (roteamento da PayGo decide) ou o autorizador real.
3. **Testar ControlPay (listar terminais)** — sem salvar nada, monta um cliente efêmero.
   Verde esperado: `✓ Chave aceita · terminal 25469 (…) pronto`, e a linha do terminal
   deve mostrar `instalação 263397`. Se aparecer **SEM terminal físico**, a PayGo ainda
   não vinculou o terminal lógico ao físico — parar aqui e cobrar o vínculo.
4. **Salvar**.

## Antes de vender de verdade (senão o caixa segue em modo de teste)

- [ ] **Desligar o MODO DE HOMOLOGAÇÃO** — hoje `homologacao = 1`: entra sem CPF/senha e
      autoriza **sem PIN**. Reiniciar o PDV depois.
- [ ] **Religar o FISCAL** (Configuração → FISCAL). O PDV está em **modo recibo** desde
      21/08 — nenhuma NFC-e é emitida enquanto isso.
- [ ] Desativar os produtos da categoria **TESTE PAYGO** (um "Sincronizar" resolve).
- [ ] Estornar pelo menu do PayGo as intenções **167511** e **167515** (R$ 2,00 cada,
      aprovadas sem venda no religamento de 21/08).
- [ ] Tratamento contábil das **2 NFC-e** que não puderam ser canceladas
      (`cStat 501`, prazo de 30 min estourado): 14:45:06 (…44196587) e 17:32:33 (…84346650).

## Homologação: o que ficou aberto

A liberação veio com **21 de 23 passos obrigatórios** fechados (ver `CONTROLPAY_status.md`).
Seguem sem execução:

- **Passo 52** — Esc na tela do QR do Pix (`OPERACAO CANCELADA`).
- **Passo 24** — queda de energia. ⚠️ Como o código está hoje, o religamento marca a
  transação como **paga** e deixa o estorno pro operador; o passo exige que a linha fique
  **órfã/recusada e nunca paga**. Se a PayGo cobrar isso, é mudança de código, não de roteiro.

## Primeira venda em produção — o que conferir

Uma venda pequena no crédito e conferir, na ordem:

1. comprovante impresso com **Via Cliente** e **Via Estabelecimento**;
2. `tef_transacao`: `provedor = 'controlpay'`, `situacao = 'pago'`, NSU e autorização preenchidos;
3. a NFC-e saiu (fiscal religado) com os dados do cartão (`nsuTid`, autorização, bandeira);
4. um **estorno** pelo TEF → Estornar, para provar o caminho de volta em produção.
