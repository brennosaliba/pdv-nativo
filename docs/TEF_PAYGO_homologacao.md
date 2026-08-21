# Homologação PayGo — os 55 passos × o que o PDV faz

Fonte: `C:\paygo\Roteiro de testes v20260703.pdf` + `Planilha de testes v20260703.xlsx`
(entregues pelo dono em 21/08/2026). Integração do PDV: **Troca de arquivos TXT**.
Na planilha, a coluna "Retorno do teste" recebe o **`001-000`** (identificação) de cada passo.
Ao final: planilha preenchida + logs do PayGo (Configurações → "Logar troca de arquivos",
senha 314159) anexados no chamado do Jira; análise em até 5 dias úteis.

Legenda da coluna **PDV**: ✅ coberto no cliente · 🔧 exige opção/tela · 🟡 depende do PayGo/pinpad (nada a codar) · ⛔ não se aplica ao TXT.

| # | Obrig. | Passo | Como o PDV executa | Verificar | PDV |
|---|---|---|---|---|---|
| 1 | SIM | Instalação | feita no PayGo Windows com os dados da PayGo (ID de instalação + senha) | "TRANSAÇÃO APROVADA", recibo | 🟡 |
| 2 | SIM | Venda valor máximo (R$ 100.000,00) | CRT `003 = 10000000` | aprovada, confirmada (CNF), recibo | ✅ |
| 3 | SIM | Venda **pré-selecionada**: C6PAY · cartão · crédito · à vista | CRT `010 = C6PAY` `749 = 1` `731 = 1` `732 = 1` | recibo, CNF | 🔧 config "rede pré-selecionada" |
| 4 | SIM | Venda negada R$ 1.000,01 (C6PAY) | CRT → `009 ≠ 0`, `030 = NEGADA 01` | mostrar `030`; nada gravado como venda | ✅ |
| 5 | SIM | Menu adquirente + Esc | CRT sem `010` → PayGo mostra menu → Esc → `030 = OPERAÇÃO CANCELADA` | transação não realizada | ✅ |
| 6 | SIM | Crédito | CRT `731 = 1` | recibo, CNF | ✅ |
| 7 | SIM | Débito | CRT `731 = 2` `732 = 1` | recibo, CNF | ✅ |
| 8 | SIM | Crédito **parcelado pelo estabelecimento em 99×** | CRT `731 = 1` `732 = 3` `018 = 99` — ou omitir 732/018 e deixar o PayGo perguntar | recibo "parcelado pela loja 99x" | 🔧 tela de Pagamento hoje manda parcelas = 1 |
| 9 | opc | Recibos diferenciados #1 (reduzido + lojista) | cap. 16 em `706` → `711` + `715` | | ✅ (cap 16 declarada) |
| 10 | SIM | Recibos diferenciados #2 (portador + lojista) | cap. 8 em `706` → imprimir **`713`** e **`715`** | duas vias impressas | ✅ cap 8 · 🔧 impressão das vias |
| 11 | SIM | QR Code Pix ("PIX C6 BANK", aprovação automática) | CRT `749 = 8` (+`750 = 4`) `010 = PIX C6 BANK` | recibo, CNF | 🔧 config rede Pix |
| 12 | SIM | Teste de comunicação (menu administrativo) | **ADM** → operador escolhe "teste de comunicação" | sem recibo; transação "confirmada" | 🔧 comando ADM |
| 13–15 | opc | Relatórios sintético/detalhado/resumido | ADM → relatório → imprimir comprovante → CNF | | 🔧 ADM |
| 16 | SIM | Esc no menu administrativo | ADM → Esc → `030 = OPERAÇÃO CANCELADA` | não realizada | 🔧 ADM |
| 17–18 | opc | Vendas R$ 1,00 / R$ 2,00 (C6PAY) | CRT | | ✅ |
| 19 | SIM | Venda R$ 12.345,67 (C6PAY) | CRT `003 = 1234567` | | ✅ |
| 20 | opc | Cancelamento da #18 | **CNC** `003` `012` `013` `022 DDMMAAAA` `027` `010` → two-phase → recibo | | ✅ CNC · 🔧 tela p/ escolher a venda |
| 21 | SIM | Cancelamento da #19 | CNC | recibo, CNF | idem |
| 22 | opc | Cancelamento da #17 | CNC | | idem |
| 23 | opc | Cancelamento da #2 **pelo menu administrativo** | ADM → cancelamento | | 🔧 ADM |
| 24 | SIM | **Queda de energia** durante a venda (antes do cartão) | desligar o PC; religar → `Req\intpos.001` órfão removido, nada reprocessado | transação não realizada | ✅ (limpar Req no boot) |
| 25 | SIM | Queda de energia durante cancelamento (da #6) | idem com CNC | não realizada | ✅ |
| 26–29 | SIM* | Dado genérico digitado / menu genérico (R$ 1.001,00 / 1.002,00) | *só DLL* (tag 0x2F) | | ⛔ TXT |
| 30 | SIM | Mensagem resultado 80 chars (R$ 1.003,00) | mostrar `030` inteiro, sem truncar | "TRANSAÇÃO DE TESTE APROVADA. CÓDIGO AUTORIZAÇÃO 13456789 TRANSACAO NAO PRODUTIVA" | ✅ |
| 31 | SIM | Transação pendente #1 (R$ 1.005,50) | CRT → aprovada → CNF | recibo | ✅ |
| 32 | SIM | Transação pendente #2 (R$ 1.005,51) | CRT vem **negada trazendo a pendente** (`027`/`010`, `729 = 2`) → PDV manda **CNF** com esses dados (é a #31, conhecida), **sem imprimir**; venda atual não realizada | | 🔧 resolver pendência devolvida |
| 33 | SIM | Pendente não encontrada #1 (R$ 1.005,60) | CRT → CNF | | ✅ |
| 34 | SIM | Pendente não encontrada #2 (R$ 1.005,61) | negada com pendente **desconhecida** → PDV manda **NCN**, sem imprimir | | 🔧 idem (desconhecida → NCN) |
| 35 | SIM | Confirmação manual (R$ 1.012,00) | venda aprovada → CNF pela via normal (ou botão "confirmar pendência") | | ✅ |
| 36 | opc | Confirmação manual REDE R$ 10 | idem | | ✅ |
| 37 | SIM | Desfazimento manual (R$ 1.011,00) | aprovada → operador desiste antes do CNF → **NCN** | "considerada desfeita" | ✅ (cancelar após aprovação = NCN) |
| 38 | opc | Desfazimento manual REDE R$ 333 | idem | | ✅ |
| 39–40 | auto-atend. | Desfazimento por falha de liberação de mercadoria | — | | ⛔ não é autoatendimento |
| 41–42 | SIM | Venda R$ 1.017,00 (C6PAY) + cancelamento "Referência Local" | CNC com `027`/`012` da venda | "TRANSAÇÃO APROVADA", recibo | ✅ CNC |
| 43–44 | SIM | Venda R$ 1.018,00 (REDE) + cancelamento "Referência Externa" | CNC (PayGo pede o que faltar na própria tela) | | ✅ CNC |
| 45 | SIM | Contactless R$ 1.020,00 | CRT; pinpad "APROXIME, INSIRA OU PASSE"; operador "AGUARDE OU DIGITE O NÚMERO DO CARTÃO" (tela do PayGo) | | 🟡 |
| 46 | SIM | Contactless sem senha R$ 999,00 | CRT | sem pedir senha | 🟡 |
| 47–50 | ControlPay | consulta terminais/status/callback | — | | ⛔ WebService |
| 51 | SIM | **Queda de energia após a aprovação, antes do CNF** | religar → pendência detectada → **NCN** (sem venda gravada) | transação não realizada | ✅ (boot resolve) |
| 52 | SIM | Esc na tela do QR (Pix) | `030 = OPERAÇÃO CANCELADA` | não realizada | ✅ |
| 53 | SIM | Pix R$ 500,00 ("PIX C6 BANK") | CRT `749 = 8` | recibo, CNF | 🔧 rede Pix |
| 54 | SIM | Cancelamento do Pix da #53 | CNC → negado `030 = TRANSAÇÃO NEGADA PELO HOST` | mostrar mensagem; não realizada | ✅ |
| 55 | C6Pay Android | comprovante gráfico | — | | ⛔ |

## Estado final em 21/08/2026 — após 3 rodadas de revisão adversarial (bateria 483/483)

Cliente `Pdv.Nucleo/PayGo.cs` pronto para o sandbox. Além do listado abaixo, ficou garantido: toda
resposta (`.sts`/`.001`) é conferida pela identificação `001` — id NOSSO que não é o atual ⇒ alheia
(desfeita se aprovada+729=2; órfã se 729=1 ou sem 027; o PDV segue esperando a sua), id desconhecido ⇒
aceito como nosso com ALARME na auditoria (PayGo que não ecoe o 001 não derruba a 1ª venda) · CNF/NCN
sem ack viram `cnf_sem_ack`/`ncn_sem_ack` e são reenviados antes de todo comando e no boot (sobrevivem
ao restart: a tela não sobrescreve linhas do PayGo) · a resposta é persistida (027 incluso) ANTES de o
arquivo ser apagado (P51) · `.001` só é lido inteiro (`999-999 = 0`) ou estável por 2 s · estorno (CNC)
termina `estornado` e a venda original `estornada` (fora da soma do TEF) · pendência P32 → `confirmada`,
ADM → `adm` (só venda vira `pago`) · nunca CNF/NCN sem 027 · trilha em `auditoria` (evento `tef_paygo`).
**A confirmar no PayGo real:** eco do `001`, campos da "transação pendente" (P32/34), `749/750` do Pix, `733 = 210`.

### Histórico (1ª rodada, bateria 418/418 com 111 checks paygo)

**No cliente (feito):** `.tmp`+rename · `.sts` 7 s → "TEF não responde" · sem timeout após o `.sts`
(cancelar do operador só avisa; aprovou depois → NCN) · CNF só DEPOIS de gravar `tef_transacao`
(memória não volátil; falhou gravar → NCN) · valor divergente → NCN · `729 = 1` · CNC two-phase ·
resposta órfã em `Resp\intpos.001` desfeita antes de todo comando · religamento no boot sem perguntar
(aprovada + venda gravada → CNF; aprovada sem venda → NCN; `cnf_sem_ack` → reenvia) · semáforo da
pasta · `706 = 156` · `004 = 0` · `733 = 210` · Pix `749 = 8 / 750 = 4` · tBand + CNPJ das credenciadoras.
**Em implementação no cliente:** pré-seleção de rede por config (`tef_paygo_rede` → `010` + `749 = 1`;
`tef_paygo_rede_pix`) · P31–34 (negada trazendo `027` → CNF se conhecida-confirmada, senão NCN; venda
atual não realizada) · P24/25 (boot apaga `Req\intpos.001` órfão) · mensagem de inconsistência · `030` inteiro.
**Tela (feito em 21/08, tarde):** hook `ClientePayGo.ImprimirComprovante` — as vias `713/715` (ou `711`/`029`,
respeitando `737`) saem ANTES do CNF pela `Impressao.ImprimirTextoAsync` (um job por via); não saiu →
pergunta "tentar de novo / desistir" → desistiu = NCN + mensagem literal "Transação TEF cancelada: Rede: X
NSU: Y Valor: Z" (venda não cobrada; com `729 = 1` é melhor-esforço, sem NCN) · Configuração → seção
**TEF (CARTÃO / PIX)**: provedor (sem TEF / Smart TEF nuvem / PayGo), pasta, registro 738, empresa 716,
rede cartão e rede PIX (010), "imprimir vias", "perguntar parcelas", botões **Testar PayGo (ATV)** e
**Menu administrativo (ADM)** (gravam as chaves `tef_*` e recarregam o provedor — `Servicos.RecarregarTef`)
· parcelas no crédito (por config; `732 = 3` + `018 = N`) · botão **TEF** na barra da venda: **Estornar
cartão/PIX** (lista as vendas do turno pagas pelo PayGo → PIN de supervisor → motivo → CNC → com o CNC
aprovado cancela a venda no PDV no mesmo ato; venda com 2 cartões só é cancelada no último; NFC-e
autorizada bloqueia antes do CNC), **Menu administrativo do PayGo**, **Reimprimir o último comprovante**.
Bateria 499/499 (16 checks novos do hook).

**Regra da tela de estorno (a fazer):** o estorno de cartão é UMA ação = cancelar a venda no PDV **e** mandar
o CNC (`ClientePayGo.CancelarAsync`) — a linha do CNC termina `estornado` e a venda original `estornada`
(sai da soma do TEF no fechamento), então a venda precisa sair de `naVenda` no mesmo ato, senão o
fechamento acusa divergência até alguém cancelar a venda.

## O que ainda precisa existir no PDV para rodar o roteiro inteiro (TXT)

1. **Impressão das vias** (`713`/`715`, fallback `711`/`029`) na bobina — e é ela que, pela spec, decide CNF × NCN
   (falhou a impressão e o operador desistiu → NCN + "Transação TEF cancelada: Rede/NSU/Valor").
2. **Comando ADM** (menu administrativo): teste de comunicação, relatórios, cancelamento pelo menu, Esc.
3. **Pré-seleção** por config: rede de cartão (`010 = C6PAY`) e rede Pix (`010 = PIX C6 BANK`);
   e **parcelas** (99×) — ou omitir `732/018` para o PayGo perguntar.
4. **Pendência devolvida** (passos 32/34): negada + `027` + `729 = 2` → CNF se cód. controle conhecido/confirmado, NCN se não; nunca imprimir.
5. **Tela de cancelamento TEF** (CNC): escolher a venda de cartão (NSU/valor/data) → cancelar.
6. Limpeza de `Req\intpos.001` órfão no boot (passo 24/25) e detecção de `Resp\intpos.001` órfão (51).
7. Mensagens literais da spec: "TEF não responde" · "Inconsistência no campo <n> do arquivo <nome> gerado pelo TEF" · `030` · "Transação TEF cancelada: …" — e esperar OK.

## Valores "mágicos" do autorizador de testes C6PAY (memorizar)

| Valor | Efeito |
|---|---|
| R$ 1.000,01 | negada "NEGADA 01" |
| R$ 1.001,00 / 1.002,00 | captura genérica (DLL) |
| R$ 1.003,00 | mensagem de 80 chars |
| R$ 1.005,50 → 1.005,51 | pendente conhecida → CNF |
| R$ 1.005,60 → 1.005,61 | pendente desconhecida → NCN |
| R$ 1.011,00 | desfazimento manual |
| R$ 1.012,00 | confirmação manual |
| R$ 1.017,00 (C6PAY) / 1.018,00 (REDE) | cancelamento ref. local / externa |
| R$ 1.020,00 / 999,00 | contactless com / sem senha |
| REDE | só valores inteiros (centavos = negada) |
