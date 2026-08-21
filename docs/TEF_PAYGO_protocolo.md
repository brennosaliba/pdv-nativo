# TEF PayGo Windows — protocolo de troca de arquivos (spec v2.25)

Extraído de `paygodev.readme.io` em 21/08/2026 (PayGo Windows v5.1.50.24),
**documentação lida por completo, página a página** (troca de arquivos, fluxos,
dicionário de campos, exemplos do kit, roteiro de certificação, e a seção
ControlPay/REST como alternativa). É o contrato para implementar cartão/Pix no
PDV nativo.

Suporte ao desenvolvedor: `devpaygo@setis.com.br` · comercial: `paygo.com.br/contato`.

> Correções em relação à 1ª leitura: **moeda é `0` = Real** (não 986);
> **`729-000` = 2 requer confirmação, 1 não requer**; comprovante e o gatilho
> do commit são a MESMA coisa (ver §5); **`733-000` = `210`** em todos os
> exemplos oficiais (a spec é "2.25", mas o campo vai `210`).

## 1. Arquitetura

```
PDV (automação) ──arquivos──▶ PayGo Windows ──COM/USB──▶ pinpad
                                   └──TLS──▶ PayGoWeb (Setis) ──▶ adquirentes (Rede, Cielo, Getnet, Stone, Pix…)
```

PayGo Windows roda em **segundo plano** (sobe com o Windows, p/ qualquer usuário)
e conversa com o PDV **por arquivos-texto numa pasta**. Enquanto o TEF processa,
ele assume a tela e o pinpad; quando grava a resposta, devolve o controle ao PDV.
Em cada momento só um dos dois fala com o usuário. A confirmação (CNF) percorre
o caminho inteiro de volta até o autorizador.

- Requisito: Windows 7+. Instalador `SetupPayGo_full_vX.X.X.X.exe`
  (`C:\Program Files (x86)\PayGo` + `C:\ProgramData\PayGo`).
- Há build **sem Warsaw** (sem proteção anti-Prilex) — só com AV/isolamento próprios.
- Pinpad é **serial/COM-USB**: não bloquear portas; AV precisa de exceção nas duas
  pastas acima (Kaspersky suspende o processo; Symantec bloqueia COM). AV que
  monitora arquivos pode **negar acesso momentâneo** a `intpos.001`/`intpos.sts`
  → o PDV **re-tenta várias vezes com intervalos de fração de segundo** antes de
  reportar erro.
- Firewall: liberar hosts/portas do PayGoWeb (prod e sandbox) e do Warsaw — lista
  na página "Rede e conectividade" (IPs mudam; não fixar no código).
- **Atualização automática**: a cada 24 h (ou ao abrir) consulta o ControlPay e
  baixa pra `C:\ProgramData\PayGo\Atualizacoes`; se houver transação em curso
  espera 5 min e re-checa; tipos obrigatória / agendada / obrigatória+agendada
  (obrigatória não feita = app encerra). "Atualizar agora" em Configurações.
- **Senhas**: lojista (padrão `1111`, Configurações → Cadastrar senha) p/
  cancelamentos, relatórios e administrativas; técnica p/ reset do terminal.
  Perfis: operador / gerencial / administrador.

## 2. Diretórios (recomendado `C:\PAYGO`, configurável)

| Pasta | Quem escreve | Quem apaga após ler |
|---|---|---|
| `C:\PAYGO\Req\intpos.001` | PDV | **PayGo** |
| `C:\PAYGO\Resp\intpos.sts` | PayGo (ack: "recebi") | PDV |
| `C:\PAYGO\Resp\intpos.001` | PayGo (resposta final) | PDV |

Instalador já concede leitura/escrita a todos os usuários autenticados. Outras
redes usam `C:\TEF_Dial` — por isso o caminho é configuração, não constante.

### 2.1 Regras de gravação/leitura (obrigatórias — "Outras considerações")
1. **Gravar com nome temporário** (`Req\intpos.tmp`), **flush**, fechar, e só
   então **renomear** para `intpos.001` — evita o PayGo ler arquivo pela metade.
2. Ao abrir `Resp\*`, tolerar falha de acesso (AV) → re-tentar em frações de segundo.
3. Enquanto espera resposta: **no máximo 4 verificações/segundo** (pausa 250 ms)
   pra não disputar CPU com o TEF.
4. Antes de qualquer comando, **apagar o conteúdo de `Resp\`**.

## 3. Formato do arquivo

- Texto; linhas terminadas em **CRLF**; só ASCII **20h–7Eh** (**sem acento**).
- Linha = `AAA-BBB = valor` — `AAA` número do campo, `BBB` índice (000 ou
  repetição), **um espaço de cada lado do `=`**.
- Campo desconhecido na resposta → **ignorar sem erro**.
- Linhas de comprovante vêm **entre aspas duplas** (`029-001 = " *** DEMO ***"`)
  — preservar espaços internos.
- Último campo sempre `999-999 = 0`.

## 4. Comandos (`000-000`)

| Cmd | .sts | .001 | Função |
|---|:-:|:-:|---|
| ATV | ✓ |   | PayGo está ativo? |
| CRT | ✓ | ✓ | **Venda** (crédito/débito/voucher/Pix/carteira) |
| ADM | ✓ | ✓ | Administrativa (menu do PayGo; fluxo igual à venda) |
| CNC | ✓ | ✓ | Cancelamento (opcional na automação — o operador consegue pelo ADM) |
| CNF | ✓ |   | **Confirma** a última transação |
| NCN | ✓ |   | **Desfaz** a última transação |
| CDP | ✓ | ✓ | Captura dado no pinpad |

### 4.1 Verificação de atividade (ATV)
1. Apagar `Resp\`; 2. gravar `Req\intpos.001` com `000-000 = ATV`; 3. esperar
**até 7 s** pelo `Resp\intpos.sts`; 4. gerado = ativo. **Nunca** checar
processo/janela/arquivo em disco pra isso. Se inativo: **avisar o operador, não
acionar o PayGo automaticamente** (exceção: quem usa `C:\TEF_Dial` pode rodar
`tef_dial.exe`).

## 5. Two-phase commit — o coração (NUNCA errar aqui)

1. PDV grava `CRT` → espera `Resp\intpos.sts` (ack). **Se o .sts não vier: "TEF não responde"** (único timeout legítimo).
2. Depois do `.sts`: **sem timeout** — a rede/usuário não têm tempo máximo. Não permitir interromper; abortar só por senha restrita.
3. Vem `Resp\intpos.001`. `009-000 = 0` → aprovada; ≠0 → mostrar `030-000` ao operador (com "OK" de leitura). Apagar os dois arquivos de `Resp\`.
4. Se aprovada e `729-000 = 2` (ou ausente com comprovante): a transação está **pendente no concentrador** até o PDV mandar:
   - **`CNF`** (commit) — o comprovante **imprimiu/persistiu com sucesso** (determinado pelo PDV conversando com a impressora, **nunca pelo operador**);
   - **`NCN`** (rollback) — falha de impressão e o operador **desistiu** de tentar de novo → avisar "Transação TEF cancelada: Rede/NSU/Valor".
   - CNF/NCN levam `001-000` (a mesma identificação) + **`027-000` (código de controle da resposta)** + `733/735/736/738` (+ `010-000` rede, opcional).
   - CNF/NCN também geram `Resp\intpos.sts` → esperar e apagar.
5. `729-000 = 1` → não confirma (negada, consulta, ou a rede já efetivou).

**Queda de energia** no meio: ao religar, se existe `Resp\intpos.001` órfão e/ou
uma `tef_transacao` aprovada sem CNF → a transação aconteceu mas não foi confirmada
→ resolver sozinho: venda concluída? CNF : NCN (ou oferecer reimprimir → CNF /
cancelar → NCN). **Nunca** deixar o operador decidir o status por conta própria.
Se o PDV não mandar nada, o próprio PayGo abre "transação pendente" no próximo CRT
pedindo Confirmar/Desfazer (Passos 32/34 do roteiro); em autoatendimento confirma
sozinho na próxima transação. O **Passo 51** do roteiro testa exatamente isso:
vender, não mandar CNF/NCN, matar o PayGo pela bandeja, reabrir e mandar **NCN**
com o `027-000` salvo.

→ No PDV isto vira **outbox**: CRT-ok grava `tef_transacao(pendente_confirmacao)`;
o CNF/NCN é o passo 2 da mesma máquina; no boot, varrer pendências.

### 5.1 Venda com múltiplos cartões (se um dia for oferecido)
Não é suportado "de fábrica" (cada TEF precisa ser confirmado antes do próximo):
- Cada TEF que **não** é o último: CNF **imediatamente**, comprovantes guardados
  em memória não volátil pra imprimir depois.
- Último aprovado → fechar cupom, imprimir todos na ordem, **então** CNF do último.
- Se não completar: o último não confirmado → NCN; os já confirmados → **CNC** de
  cada um (não é imediato: lê cartão, digita dados, depende da rede, pode falhar;
  só conta como feito após aprovação + impressão + CNF do CNC).
- Uma vez iniciado, não pode ser interrompido até sucesso total ou cancelamento
  total; queda de energia no meio do cancelamento → ao reiniciar prossegue
  sozinho, sem opção de parar.

### 5.2 Venda com outras formas (dinheiro + cartão etc.)
Registrar as outras formas **antes** do TEF; o `003-000` enviado é sempre **o valor
ainda não pago**. Aprovado + CNF → venda fecha.

## 6. Requisição de VENDA (CRT) — campos

Obrigatórios: `000` CRT · `001` identificação (n..10, **único por operação**, ecoado) ·
`003` valor total **em centavos** · `004` moeda `0` · `706` capacidades ·
`716` empresa da automação (razão social) · `733` versão da interface (**`210`**) ·
`735` nome da automação · `736` versão · `738` **registro de certificação** (PayGo dá
no início da certificação; o kit usa `G45J35G3JH45B435`) · `999-999 = 0`.

Úteis: `002` documento fiscal (n..12; obrigatório com impressora fiscal) ·
`717` data/hora fiscal `AAMMDDhhmmss` · `731` tipo de cartão (0 qualquer / 1 crédito /
2 débito / 3 voucher — **sempre oferecer "outro"=0**) · `732` financiamento (0 qualquer /
1 à vista / 2 parc. emissor / 3 parc. estabelecimento / 4 pré-datado) · `018` parcelas ·
`749` forma (1 cartão / 8 carteira digital) · `750` id da carteira · `751/752` split ·
`727` taxa de serviço (gorjeta, **C5 = obrigatória no setor de alimentação mesmo
zerada**) · `722..725` dados adicionais (a..128, viram "dado adicional" no extrato) ·
`726` idioma `pt` · `702` índice do estabelecimento.

**`706-000` capacidades** = soma: 1 troco · 2 desconto (**exigido na certificação CIELO**) ·
**4 fixo (sempre incluir)** · 8 vias diferenciadas · 16 cupom reduzido · 32 valor devido ·
64 valor reajustado · **128 NSU até 40 chars (incluir — Pix devolve EndToEndId longo e
sem ele o cancelamento de Pix é impossível)** · 256 índice de rede 4 chars.
→ Sugerido para o PDV: **4+8+16+128 = 156** (+2 se CIELO). Os exemplos do kit usam
`511` (tudo) e `4` (mínimo). **Não declarar 1/32/64** → o PayGo nunca altera o valor
(sem troco, sem aprovação parcial, sem reajuste).

**Não preencher `010`/`739` (rede) na venda**: o PayGo mostra o menu de redes
(o kit força `010-000 = DEMO` só por ser sandbox).

## 7. Resposta de VENDA (`Resp\intpos.001`) — o que guardar

| Campo | Guardar em |
|---|---|
| `009` status (0=ok) | decide o fluxo |
| `030` mensagem operador (a..40) | mostrar |
| `729` status confirmação (1/2) | dispara CNF/NCN |
| `027` **código de controle** (a..30, id único PayGo) | `tef_transacao` — chave do CNF/NCN/cancelamento |
| `012` NSU (a..40; Pix = EndToEndId) | `CartaoTef.Nsu` |
| `013` autorização (a..6; **não vale p/ Pix**) | `CartaoTef.CAut` |
| `010` rede · `739` índice rede | `CartaoTef.Adquirente` (só armazenar, não validar) |
| `011` tipo de transação (10 crédito à vista, 20 débito…) · `730` operação (1 venda, 51 cancel.) | relatório |
| `040` nome do cartão · `748` nome padronizado (relatórios) | `CartaoTef.Bandeira` |
| `740` cartão mascarado · `741` nome do cliente · `747` validade/emissor | cupom |
| `718` terminal lógico (PDC) · `719` cód./CNPJ do estabelecimento | `CartaoTef.Terminal` |
| `731` tipo · `732` financ. · `018` parcelas | `CartaoTef.Parcelas` |
| `022` data `DDMMAAAA` · `023` hora `hhmmss` · `015/016` ref. host | cancelamento futuro |
| `003` valor **efetivamente** cobrado (pode diferir!) | reconciliar |
| `707` valor original · `708` troco(saque) · `709` desconto da rede · `743` **valor devido** (autorizou parte — pedir outra forma!) · `744` reajuste | lógica de caixa (só se 706 habilitou) |
| `737` vias (0/1/2/3) · `028/029` via única · `710/711` reduzido · `712/713` cliente · `714/715` estabelecimento | impressão |

Regra do valor: `003 = 707 + 708 − 709 − 743` (ou `744` no lugar de 707).
O PDV **decide o que imprimir** conforme a impressora; débito traz linha de
`ASSINATURA` na via do estabelecimento.

## 8. Cancelamento (CNC) — requisição

`003` valor · `004` moeda · **`012` NSU (M)** · `013` autorização (se veio) ·
`022` data do comprovante `DDMMAAAA` · `023` hora `hhmmss` · `027` código de controle ·
`010`/`739` rede (C7) · `706` · `716` · `733..738` · `999`.
Resposta traz `025` NSU original e `026` data/hora original. PayGo pede **senha
lojista**. É two-phase também: aprovou → imprimir → CNF. Cartão presente: **só no
mesmo dia** da venda.

## 9. Pix / carteira digital

`749 = 8` + `750` (4 = QR dinâmico). O QR aparece **no pinpad** se ele suportar; senão
na tela do PayGo Windows; autoatendimento inverte a preferência. O PDV não desenha QR.
NSU vem como EndToEndId (por isso capacidade 128 — e sem ela não cancela).
Pix TEF exige ao menos uma chave Pix cadastrada no PayGoWeb. Sandbox PIX C6 aprova
sozinho após alguns segundos.

## 10. Mensagens obrigatórias ao operador

| Situação | Mensagem |
|---|---|
| `.sts` não veio | **TEF não responde** |
| campo inconsistente | Inconsistência no campo `<n>` do arquivo `<nome>` gerado pelo TEF |
| `009 ≠ 0` | o conteúdo de `030-000` |
| falha de impressão (→NCN) | Transação TEF cancelada: Rede `<010>` / NSU `<012>` / Valor `<003>` |

Sempre esperar o "OK" do usuário depois da mensagem.

## 11. Sandbox e roteiro de certificação

3 cliques com botão direito no logo → `demo` → app **roxo**. Precisa CPF/CNPJ +
**ID de instalação + senha** (pedir via Jira). Redes: **DEMO** (cartões virtuais
VISA/ELECTRON) · **REDE** (só valores inteiros — centavos = negada) · **PIX C6 BANK**
(aprova sozinho). Valores de teste: **cheios R$ 1–10 e dezenas** (10, 20, 30…);
evitar > R$ 1.000 e outros valores (têm "desvios" propositais). Logs da troca de
arquivos: Configurações → "Logar troca de arquivos" → senha `314159`.

Roteiro do kit (páginas "Passos") que a automação precisa passar:
- **19**: CRT aprovado + CNF · **21**: CNC com senha lojista `1111`.
- **31**: CRT + CNF · **32**: novo CRT → "transação pendente" → *confirmar*.
- **33**: CRT débito + CNF · **34**: novo CRT → pendente → *desfazer*.
- **51**: CRT aprovado, **não** mandar CNF/NCN, sair do PayGo pela bandeja (queda
  de energia simulada), reabrir, mandar **NCN** com o `027-000` salvo → "Não".

## 12. Exemplos reais (da doc) — fixtures dos testes

### 12.1 Página "Exemplos de arquivos" (rede NOVAREDE)
```
# Req\intpos.001 (venda)
000-000 = CRT
001-000 = 34430576
002-000 = 223546
003-000 = 10000
004-000 = 0
706-000 = 3
716-000 = SETIS AUTOMACAO E SISTEMAS LTDA.
731-000 = 01
732-000 = 00
733-000 = 210
735-000 = KiWi
736-000 = v1, 14, 0, 0
738-000 = G45J35G3JH45B435
749-000 = 1
999-999 = 0

# Resp\intpos.sts
000-000 = CRT
001-000 = 34430576
999-999 = 0

# Resp\intpos.001 (trechos)
009-000 = 0
010-000 = NOVAREDE
012-000 = 19100205783
013-000 = 022167
027-000 = 11011719100219100205783
028-000 = 18
029-001 = " *** DEMONSTRACAO PAYGO ***"
030-000 = AUTORIZADA 022167
040-000 = DEMOCARD
729-000 = 2
730-000 = 1
731-000 = 2
737-000 = 3
740-000 = ************1111
999-999 = 0

# Req\intpos.001 (confirmação)
000-000 = CNF
001-000 = 34430576
002-000 = 223546
010-000 = NOVAREDE
027-000 = 11011719100219100205783
733-000 = 210
735-000 = KiWi
736-000 = v1, 14, 0, 0
738-000 = G45J35G3JH45B435
999-999 = 0
```

### 12.2 Passo 19 (sandbox DEMO, crédito à vista)
```
# Req
000-000 = CRT
001-000 = 17827
002-000 = 223546
003-000 = 1234567
004-000 = 0
010-000 = DEMO
706-000 = 511
716-000 = Setis
733-000 = 210
735-000 = Teste
736-000 = v1
738-000 = G45J35G3JH45B435
999-999 = 0

# Resp (campos-chave; vias omitidas)
000-000 = CRT
001-000 = 69746
003-000 = 1234567
004-000 = 0
009-000 = 0
010-000 = DEMO
011-000 = 10
012-000 = 721554
013-000 = 543733
015-000 = 3103120343
016-000 = 3103120343
022-000 = 31032025
023-000 = 120343
027-000 = 310320251203721554
028-000 = 0
030-000 = TRANSACAO AUTORIZADA
040-000 = VISA
710-000 = 5
711-001 = " *** PAYGO - AMBIENTE SANDBOX *** "
711-002 = "--------------------------------------"
711-003 = "86132 EC:0000001380 REF:0000003801"
711-004 = " "
711-005 = " TRANSACAO TESTE SEM VALOR FINANCEIRO! "
712-000 = 14
713-001 = " *** PAYGO - AMBIENTE SANDBOX *** "
713-002 = "VIA CLIENTE 31/MAR/25 12:03"
713-003 = "SETIS*SETIS"
713-004 = "CNPJ:03.361.770/0001-58 PDC:86132"
713-005 = "REF:3801 EC:1380"
713-006 = "C-489391******0008 VISA CREDITO"
713-007 = "AID:A0000000031010"
713-008 = " VENDA CREDITO A VISTA "
713-009 = "VALOR FINAL: R$ 12.345,67"
713-010 = " "
713-011 = "--------------------------------------"
713-012 = "86132 EC:0000001380 REF:0000003801"
713-013 = " "
713-014 = " TRANSACAO TESTE SEM VALOR FINANCEIRO! "
714-000 = 16
715-001 = " *** PAYGO - AMBIENTE SANDBOX *** "
715-002 = "VIA ESTABELECIMENTO 31/MAR/25 12:03"
715-003 = "SETIS*SETIS"
715-004 = "CNPJ:03.361.770/0001-58 PDC:86132"
715-005 = "REF:3801 EC:1380"
715-006 = "C-489391******0008 VISA CREDITO"
715-007 = "AID:A0000000031010"
715-008 = "ARQC:2027E71B1A9D9755"
715-009 = " VENDA CREDITO A VISTA "
715-010 = "VALOR FINAL: R$ 12.345,67"
715-011 = " TRANSACAO AUTORIZADA COM SENHA "
715-012 = " "
715-013 = "--------------------------------------"
715-014 = "86132 EC:0000001380 REF:0000003801"
715-015 = " "
715-016 = " TRANSACAO TESTE SEM VALOR FINANCEIRO! "
718-000 = 86132
719-000 = 03361770000158
729-000 = 2
730-000 = 1
731-000 = 1
732-000 = 1
737-000 = 3
739-000 = 100
740-000 = 4***********0008
747-000 = 0230
748-000 = VISA CREDITO
999-999 = 0

# CNF
000-000 = CNF
001-000 = 34430590
027-000 = 310320251203721554
733-000 = 210
735-000 = Teste
736-000 = v1
738-000 = G45J35G3JH45B435
999-999 = 0

# NCN (Passo 51) = igual ao CNF com 000-000 = NCN

# CNC (Passo 21)
000-000 = CNC
001-000 = 87659
003-000 = 1234567
004-000 = 0
010-000 = DEMO
012-000 = 721554
022-000 = 31032025
023-000 = 120343
706-000 = 511
716-000 = Setis
733-000 = 210
735-000 = Teste
736-000 = v1
738-000 = G45J35G3JH45B435
999-999 = 0
```
Débito (Passos 33/51): `011-000 = 20`, `731-000 = 2`, `040-000 = VISA ELECTRO`,
`748-000 = VISA ELECTRON`, e a via do estabelecimento traz
`" ______________________________ "` + `" ASSINATURA "`.

## 13. Alternativa: ControlPay (REST) — documentado, NÃO é o caminho escolhido

Middleware REST da PayGo (`https://api.controlpay.com.br/` prod ·
`https://sandbox.controlpay.com.br/webapi/` sandbox — só o sandbox tem `/webapi`).
Chave de integração fixa vai na **query `?key=`** (nunca header/body);
`User-Agent: NomeDaAutomacao/1.0`. O ControlPay cria uma `intencaoVenda` e
**empurra** pro PayGo Windows do terminal (modo ativo,
`iniciarTransacaoAutomaticamente=true`, expira em 15–20 s se o TEF não responder)
ou guarda pra o PayGo puxar (modo receptivo).

- `POST Venda/Vender` — `{formaPagamentoId (20 genérico/21 crédito/22 débito/23 voucher/24 outros/25 Pix), terminalId, referencia, iniciarTransacaoAutomaticamente, parcelamentoAdmin, quantidadeParcelas, valorTotalVendido "1,00" (vírgula!) | produtosVendidos[{Id,Valor "1.00" (ponto!),Quantidade}], preDatado, aVista}`.
- `POST Venda/CancelarVenda` — `{intencaoVendaId, terminalId (mesmo da venda), senhaTecnica}`; só no mesmo dia.
- `POST IntencaoVenda/GetByFiltros` — consultar por `referencia`/status (recomendado); `pagamentosExternos[].respostaAdquirente` traz **o arquivo intpos inteiro** como string.
- `IntencaoVenda/GetById`, `PagamentoExterno/GetById`, `Venda/ConsultarVendas`, `PagamentoExterno/InsertPagamentoExternoTipoAdmin` (ADM), `Pedido/*` (carrinho pra pagar depois, parcial ok — ideal p/ delivery), `Terminal/*`, `Produto/*`, `FormaPagamento/GetByPessoaId`, `Login/Login` (obsoleta).
- Status intenção: 5 Pendente · 6 EmPagamento · 10 **Creditado** · 15 Expirado · 18 CancelamentoIniciado · 19 EmCancelamento · 20 Cancelado · 25 PagamentoRecusado. Pagamento externo: 5/10/15 (Pendente/Em operação/Finalizado); tipo 5 pagamento · 10 cancelamento · 15 admin. Pedido: 5 Aberto · 6 AguardandoPagamento · 10 Pago · 15 Cancelado.
- Terminal precisa estar vinculado a um "Terminal Físico" pela PayGo pra transacionar.

**Por que não**: exige internet no PDV pra cada venda (o PDV nativo é offline-first),
adiciona intermediário e cadastro extra; a troca de arquivos fala direto com o
PayGo local e é o que o kit de homologação cobra. Fica como plano B.

## 14. Glossário (resumo)
Rede adquirente (Cielo/Rede/Vero…) · Estabelecimento · Cliente (portador) · Emissor
(banco/administradora) · Bandeira (Visa/Master) · TEF · Checkout/PDV (automação) ·
Pinpad · PayGo Windows · ControlPay (portal de configuração/atualização do PayGo).

## 15. Arquitetura proposta no PDV

- `Pdv.Nucleo/PayGo/` — `ArquivoIntpos` (parse/serialize puro, testável; aspas nas
  vias; ignora campo desconhecido) · `ClientePayGo` (ATV; escreve Req via `.tmp` +
  rename, espera .sts com timeout 7 s, espera .001 **sem** timeout a 4×/s, re-tenta
  acesso negado por AV, apaga Resp após ler) · `MaquinaConfirmacao` (CRT-ok →
  pendente → CNF/NCN, com varredura no boot) · `Comprovante` (monta vias a partir
  de 711/713/715 ou 029).
- `tef_transacao` ganha `cod_controle` (027), `identificacao` (001) e
  `status_confirmacao`.
- `Pdv.Testes/FakePayGo` — simula a pasta: gera .sts/.001 a partir de roteiro
  (aprovar / negar / sem .sts / valor divergente / 729=1 / 729=2 / queda antes do CNF /
  arquivo travado por AV / centavos na REDE), usando os arquivos verbatim de §12.
- **ClienteTef atual (nuvem) NÃO é isso** — PayGo é caminho local novo; o
  `Servicos.Tef()` escolhe por config `tef_provedor = paygo | nuvem`.
- Certificação PayGo exige: `738` registro, `735/736/716` preenchidos, desconto se
  CIELO, e passar os Passos 19/21/31–34/51.
