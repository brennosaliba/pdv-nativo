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

## Roteiro v20260819 (58 passos) e o caminho pela biblioteca (PGWebLib.dll)

Fonte: `docs/paygo-kit-2026-08-21/Roteiro de testes v20260819.pdf` + `Planilha de testes v20260819.xlsx`
(kit `20260821-Integracao-SetupPayGo_v5.1.50.24.zip`, baixado em 05/09/2026). O roteiro de agosto tem
58 passos: entram **17 Manutenção** e **18 Reinstalação/Instalação**, e o bloco ControlPay ganha
**52 Cadastro de URL de Callback**; tudo a partir do 17 antigo desloca (17 antigo = 19 novo, 47 antigo = 49 novo,
51 antigo = 54 novo, 55 antigo = 58 novo). A tabela acima continua valendo para o TXT com a renumeração.

Provedor da biblioteca: `Pdv.Nucleo/ProvedorPGWebLib.cs` sobre `IPGWebLib`; binding real em
`PGWebLibNativa.cs` (assinaturas copiadas do exemplo oficial `PGPagamentos/pdvWindowsPayGoLibC_CSharp`);
seleção `tef_provedor = pgweblib` (índice 4 na Configuração); chaves `tef_pgweb_dir` (pasta de trabalho do
PW_iInit, padrão `C:\ProgramData\PdvNativo\pgweb`), `tef_pgweb_dll` (pasta da PGWebLib.dll do PayGo Windows,
em branco o Windows procura), `tef_pgweb_porta_pinpad`, `tef_pgweb_capacidades` (AUTCAP, padrão 28 =
valor fixo + vias diferenciadas + via reduzida). Rede pré-selecionada reaproveita `tef_paygo_rede` /
`tef_paygo_rede_pix` (AUTHSYST) e `tef_paygo_empresa` (AUTDEV).

Legenda: ✅ no provedor · 🔧 falta no PDV · 🟡 é do PayGo/pinpad · ⛔ não se aplica à DLL.

| # (ago) | Obrig. | Passo | Como a DLL faz | PDV |
|---|---|---|---|---|
| 1, 18 | SIM | Instalação / reinstalação | `PWOPER_INSTALL` (`InstalarAsync`) ou pelo menu administrativo | ✅ |
| 2 | SIM | Venda R$ 100.000,00 | `PWOPER_SALE` + `PWINFO_TOTAMNT = 10000000` | ✅ |
| 3 | SIM | Venda pré-selecionada C6PAY cartão crédito à vista | `AUTHSYST` (rede da config) + `PAYMNTTYPE = 1` + `CARDTYPE = 1` + `FINTYPE = 1` | ✅ |
| 4 | SIM | Negada R$ 1.000,01 | `PW_iExecTransac` diferente de `PWRET_OK`; mostrar `PWINFO_RESULTMSG`; nada gravado | ✅ |
| 5 | SIM | Menu adquirente + Esc | sem `AUTHSYST` a DLL pede `PWDAT_MENU` (tela `Perguntar`); Esc devolve nulo, `PWRET_CANCEL` | ✅ |
| 6, 7 | SIM | Crédito / débito | `CARDTYPE = 1` / `2` | ✅ |
| 8 | SIM | Parcelado pela loja 99x | `FINTYPE = 3` + `INSTALLMENTS = 99`; ou omitir e a DLL pergunta (`PWDAT_TYPED`) | 🔧 tela de Pagamento manda parcelas = 1 |
| 9, 10 | opc/SIM | Recibos diferenciados | AUTCAP bits 8 e 16; `RCPTCHOLDER` + `RCPTMERCH` (fallback `RCPTFULL`) | ✅ leitura · 🔧 impressão das vias |
| 11, 56 | SIM | Pix QR Code ("PIX C6 BANK") | `PAYMNTTYPE = 8` + `AUTHSYST` da config Pix. Se a DLL pedir `PWDAT_DSPQRCODE` (QR na tela do caixa) o provedor ainda não desenha | 🔧 QR na tela · 🟡 se o pinpad mostra |
| 12 a 16, 17 | SIM/opc | Menu administrativo: teste de comunicação, relatórios, Esc, manutenção | `PWOPER_ADMIN` (`AdministrativaAsync`); a DLL mostra o menu por `PWDAT_MENU`; Esc = `PWRET_CANCEL` | ✅ |
| 19 a 21 | opc/SIM | Vendas R$ 1,00 / 2,00 / 12.345,67 | `PWOPER_SALE` | ✅ |
| 22 a 25 | opc/SIM | Cancelamentos | `PWOPER_SALEVOID` (`CancelarAsync`) com `TRNORIGREQNUM` / `TRNORIGNSU` / `TRNORIGDATE` / `TRNORIGAMNT` da venda; o 25 pelo menu ADM | ✅ · 🔧 tela para escolher a venda |
| 26, 27 | SIM | Queda de energia na venda / no ADM | ao religar `PW_iInit` + `PWINFO_PND*`; pendente desconhecida vira `PW_iConfirmation(PWCNF_REV_PWR_AUT)` (`ResolverPendenciasAsync`) | ✅ |
| 28 a 31 | SIM | Dado genérico digitado / menu genérico (R$ 1.001,00 e 1.002,00) | `PWDAT_TYPED` e `PWDAT_MENU` respondidos pela tela (`RespostaDaTela`, `Perguntar`); é o motivo de existir a DLL: no TXT esses passos não são possíveis | ✅ |
| 32 | SIM | Mensagem de 80 caracteres | `RESULTMSG` inteiro, sem truncar | ✅ |
| 33 a 36 | SIM | Transação pendente conhecida / desconhecida | negada trazendo `PWINFO_PNDREQNUM`: conhecida e paga = `PWCNF_CNF_AUTO`, desconhecida = `PWCNF_REV_PWR_AUT`; nunca imprime | ✅ |
| 37 a 40 | SIM/opc | Confirmação manual / desfazimento manual | aprovada com `CNFREQ = 1`: gravou = `PWCNF_CNF_AUTO`; operador desistiu = `PWCNF_REV_MANU_AUT` | ✅ |
| 41, 42 | auto-atend. | Falha na liberação da mercadoria | | ⛔ não é autoatendimento |
| 43 a 46 | SIM | Cancelamento por referência local / externa | `SALEVOID`; o que faltar a DLL pede por `PWDAT_TYPED` (`AUTLOCREF` / `AUTEXTREF` guardados da venda) | ✅ |
| 47, 48 | SIM | Contactless com / sem senha | `PWDAT_PPGETCARD`, `PPENTRY`, `PPENCPIN`, `PPGOONCHIP`, `PPFINISHCHIP`, `PPCONF`, `PPDATAPOSCNF` (`PW_iPPPositiveConfirmation`), `PPREMCRD` no loop `PW_iPPEventLoop` | ✅ código · 🟡 pinpad |
| 49 a 53 | ControlPay | terminais, status, callback | | ⛔ WebService |
| 54 | SIM | Queda de energia após a aprovação, antes da confirmação | boot: pendência não paga = `PWCNF_REV_PWR_AUT`, sem venda | ✅ |
| 55 | SIM | Esc na tela do QR | `PW_iPPAbort` → `PWRET_CANCEL` | ✅ |
| 57 | SIM | Cancelamento do Pix (negado pelo host) | `SALEVOID` negado; mostrar `RESULTMSG` | ✅ |
| 58 | C6Pay Android | comprovante gráfico | | ⛔ |

### Medido com a DLL de verdade (07/09/2026, PGWebLib.dll 4.1.50.24 x86, harness `Pdv.SmokePGWebLib`)

Binding bateu (22 de 22 símbolos, fluxo Init, NewTransac, AddParam, ExecTransac(MOREDATA), PP*, PPEventLoop,
GetResult inteiro). O que a DLL faz diferente da spec, e o que o PDV faz a respeito:

1. **A DLL só carrega da pasta onde o PayGo Windows a instalou** (`C:\Program Files (x86)\PayGo\PGWebLib`, ou a de
   64 bits). Cópia da PGWebLib.dll em outra pasta carrega, mas `PW_iInit` devolve **-2414** (código fora da tabela
   pública) e nada funciona. `tef_pgweb_dll` tem que apontar para a pasta original; a Configuração avisa em uma
   linha se a pasta apontada não tem `PGWebLib.dll` (`ConfigPGWebLib.AvisoPastaDll`).
2. **`PW_iInit` devolve `PWRET_WRITERR` (-2485) se o diretório de trabalho não existe.** A DLL não o cria. O
   provedor cria (`Directory.CreateDirectory`) antes de cada `PW_iInit`; sem permissão, auditoria com a pasta e o
   motivo e a tela diz "TEF não responde: a PGWebLib não iniciou, pasta de trabalho inacessível".
3. **`PWINFO_IDLEPROCTIME` vem `"551231235959"`** (a DLL usa 31/12/2055 como "nunca"). Lido com `yyMMddHHmmss` o
   .NET faz 1955 e o `PW_iIdleProc` rodaria a cada tique. O século é sempre 20 (`ProvedorPGWebLib.HorarioIdle`);
   horário no passado ou inválido cai no intervalo de segurança (`IntervaloIdleMs`).
4. **`PW_iAddParam(PWINFO_USINGPINPAD)` e `(PWINFO_PPCOMMPORT)` devolvem `PWRET_INVPARAM` em `PWOPER_ADMIN`** (são
   aceitos em `PWOPER_INSTALL` e na venda). Parâmetro opcional recusado vira uma linha de auditoria; a operação segue.
5. **`PW_iGetResult(PWINFO_AUTDATETIME)` devolve `PWRET_INVPARAM`** nesta DLL (os outros infos ausentes devolvem
   `PWRET_NODATA`). O campo fica vazio (sem 022/023/952 na resposta), sem erro.
6. **O processo morre com fail-fast `0xC0000409` NA SAÍDA** (depois do `Main` devolver 0) sempre que a DLL foi
   iniciada e não encerrada: o `DLL_PROCESS_DETACH` roda `PGWLib_End` -> `warsaw_sdk::Initialize` sob o loader lock
   e aborta. Só acontece com `PW_iInit` OK (com `WRITERR` sai limpo). `FreeLibrary` não descarrega a DLL (ela se
   prende no processo). A saída é o export **`PW_End`** (fora do exemplo oficial; sem argumentos, `ret` simples,
   a mesma rotina do detach): chamada com o processo de pé ela termina (~2 s, passa pelo warsaw) e zera o estado,
   e o detach vira no-op. `ProvedorPGWebLib.Encerrar()` faz isso (nunca por cima de operação em voo) e
   `App.OnExit` chama `Servicos.EncerrarTef()` por último. Exit code do harness: -1073740791 antes, 0 depois
   (`--sem-end` reproduz o crash).
7. A instalação (`PWOPER_INSTALL`) com o PdC do sandbox foi recusada pelo host com `[NA A110] TIPO PONTO DE
   CAPTURA INCORRETO`: o ponto de captura 114975 é do tipo ControlPay. Precisa de um PdC de biblioteca no Jira da
   PayGo (portal 16). Não repetir a instalação até lá.
8. **Efeito colateral do `PW_End`: o Warsaw protege o exe da automação.** O kit instala o serviço "Warsaw
   Technology" (core.exe, antifraude da Diebold). No caminho do End a DLL monta a lista "caminhos protegidos"
   (`ConfiguraCaminhosProtegidos`: pastas do PayGo, a pasta de trabalho e `gszAutoPath` = o exe que a chamou) e
   chama `warsaw_sdk::Protect`. Depois disso o exe não pode ser sobrescrito nem renomeado, mesmo com o processo já
   encerrado (medido com o harness: `dotnet build` falha com acesso negado ao copiar o apphost; a ACL está normal).
   Só o exe: os outros arquivos da pasta continuam graváveis. Isso NÃO acontece sem o `PW_End` porque o detach
   aborta antes do `Protect`. Aberto: quanto tempo dura (reboot?) e o que faz com a atualização do caixa
   (`--atualizar` troca o Pdv.exe). Medir com o Pdv.exe instalado antes de ligar a biblioteca numa loja.
   `Servicos.EncerrarTef` também cobre a instância trocada pelo `RecarregarTef` (Testar na Configuração e depois
   salvar ou trocar de provedor): se a DLL está no processo e a instância atual não a iniciou, `PW_End` direto.
   A DLL conta a instância desde a carga (`Num da Instancia` no attach), então `PW_End` sem `PW_iInit` roda o
   encerramento inteiro (com o `Protect`) e sai limpo (medido: `--so-end`, exit 0).

### O que falta para rodar o roteiro inteiro pela DLL

1. Impressão das vias na bobina (mesma pendência do TXT).
2. Parcelas na tela de Pagamento (passo 8) e tela de cancelamento TEF para escolher a venda (22 a 25).
3. `PWDAT_DSPCHECKOUT` (mensagem no caixa) e `PWDAT_DSPQRCODE` (QR do Pix no caixa): constantes já existem, o provedor ainda não trata.
4. Ambiente: PayGo Windows 5.1.50.24 instalado na máquina de homologação (kit em `docs/paygo-kit-2026-08-21`),
   PdC de sandbox + CNPJ + senha pedidos no Jira da PayGo (portal 16), `tef_pgweb_dll` apontando para a DLL de 64 bits.
5. Logs: PGLogCollector (manual no kit) anexado ao chamado com a planilha v20260819 preenchida.

## Trava do Warsaw no executavel, medida em 07/09/2026 as 13h30

Depois que o processo chama `PW_End`, a DLL manda o Warsaw proteger a lista de caminhos, e o
executavel que carregou a `PGWebLib.dll` fica **permanentemente travado naquele caminho**, com o
processo ja encerrado e com ACL normal. Medido, com o servico "Warsaw Technology" rodando
(ele se declara NOT_STOPPABLE):

| tentativa | resultado |
|---|---|
| renomear o exe que carregou a DLL | barrado |
| sobrescrever esse exe | barrado |
| apagar esse exe | barrado |
| criar um exe novo, com outro nome, na mesma pasta | funciona |
| copiar o exe para uma pasta nova e mexer nele la | funciona |
| exe que nunca carregou a DLL (`publish\homolog-x86\Pdv.exe`, `bin\Release\...\Pdv.exe`) | livre |

A trava e por caminho e so pega quem carregou a biblioteca. Nao e o servico que trava tudo.

### O que isso quebra

O `--atualizar` troca o `Pdv.exe` no lugar. Numa loja que ja tenha aberto o TEF pela biblioteca uma
vez, essa troca passa a falhar para sempre. O caixa ficaria preso na versao instalada.

### Como resolver, antes de ligar a biblioteca em qualquer loja

Instalar cada versao na sua propria pasta e apontar o atalho para a nova, em vez de sobrescrever o
executavel que esta rodando. A pasta antiga fica no disco com o exe travado, o que nao atrapalha.
E o mesmo desenho que qualquer atualizador usa quando o binario pode estar em uso.

Enquanto isso nao existir, a biblioteca so pode ser ligada nesta maquina de homologacao.

## 07/09/2026, 15h30: o kit avulso da biblioteca mudou o jogo

A PayGo publicou uma versão do kit de integração **só com a PGWebLib.dll, sem a camada Warsaw**. O
dono desinstalou o PayGo Windows e o Warsaw e passou a usar esse kit
(`20260820-Integracao-PGWebLib_v4.1.50.924`). Isso resolveu de uma vez três coisas que estavam
travando a homologação.

### O que ficou provado, medido nesta máquina

| medida | antes (PayGo Windows + Warsaw) | agora (kit avulso) |
|---|---|---|
| precisa do PayGo Windows instalado | sim | **não** |
| arquitetura da DLL | só x86 | **x64 e x86**, então o PDV continua no build normal |
| `PW_iInit` numa pasta nova | `PWRET_WRITERR`, depois OK | `PWRET_OK` em meio segundo |
| carregar a DLL de outra pasta | recusava com `-2414` | **carrega de qualquer pasta** |
| trava do executável depois do `PW_End` | permanente, quebrava o `--atualizar` | **some** |
| escolher produção ou homologação | vinha do instalador | `PW_iSetEnvironment` |

O terminal responde `PWRET_NOTINST` na lista de operações de venda, e o menu administrativo abre
normalmente com INSTALACAO disponível. Ou seja, falta só rodar a instalação com o ponto de captura e
a senha. Esse passo é do dono: eu não digito senha em campo nenhum.

### O que mudou no PDV por causa disso

1. **`PW_iSetEnvironment` entrou no binding.** É ela que escolhe produção (`ENVRMNT_PROD`, o padrão)
   ou homologação (`ENVRMNT_TEST`). É chamada **antes** do `PW_iInit`, como o cabeçalho oficial
   exige. Se a biblioteca recusar (terminal já instalado) ou nem exportar a função (versão anterior
   à 4.1.43.10), o caixa continua funcionando e a recusa vai para a auditoria.
   Config nova: `tef_pgweb_ambiente`, com `producao` ou `homologacao`. Sem a chave, produção.
2. **`AtivoAsync` parou de mentir.** O `PW_iInit` devolve OK mesmo num terminal sem instalação, e o
   provedor respondia "ativo". A tela do caixa oferecia cartão e só falhava com o cliente esperando.
   Agora ele confere a lista de operações de venda e, quando ela devolve `PWRET_NOTINST`, responde
   que não está ativo com a frase que manda instalar.
3. **O aviso da pasta da DLL foi corrigido.** Ele dizia para apontar "para a pasta onde o PayGo
   Windows a instalou", e isso deixou de ser verdade.

### O que a própria PayGo avisa sobre esse kit

Sem o Warsaw não há a proteção contra o vírus Prillex, que ataca justamente terminais de pagamento.
Eles permitem usar em produção, mas por conta de quem usa, e recomendam compensar com antivírus,
política de rede e isolamento. Vale decidir isso antes de levar a biblioteca para a loja. Para a
máquina de homologação não muda nada.

### Configuração desta máquina, já aplicada

```
tef_provedor           = pgweblib
tef_pgweb_dll          = C:\PGWebLib\x64
tef_pgweb_dir          = C:\ProgramData\PdvNativo\pgweb64
tef_pgweb_ambiente     = homologacao
tef_pgweb_porta_pinpad = 5
```

Bateria do PDV depois de tudo: 2887 OK, 0 falhas.

## 07/09/2026, 16h: o QR do Pix na tela do caixa (passo 55)

Lendo o roteiro v20260819 achei um buraco que teria derrubado a homologação de Pix. O passo 55 diz:

> Realizar uma venda e na tela de exibição do QRCode, pressionar a tecla 'Esc' em uma solução Windows

Ou seja: a tela do QR é **do caixa**, não do pinpad, e o Esc dela tem que virar "OPERAÇÃO CANCELADA".

O provedor não sabia disso. Quando a biblioteca pede `PWDAT_DSPQRCODE`, ele caía no ramo final e
matava a venda com "TEF pediu captura que o caixa não suporta (tipo 20)".

### O que passou a existir

1. **O provedor atende os pedidos de exibição** (`PWDAT_DSPQRCODE` e `PWDAT_DSPCHECKOUT`). Ele lê o
   conteúdo do QR, manda a tela abrir e avisa a biblioteca que mostrou. A tela **não bloqueia**:
   quem espera o cliente pagar é o laço que pergunta o desfecho ao host.
2. **A tela** `Telas/TelaQrTef.cs`: QR grande sobre fundo branco, uma linha de instrução e o aviso
   de que Esc cancela. Esc, o botão e o X da janela fazem a mesma coisa: cancelam a **venda**, não
   só a janela. O provedor então chama `PW_iPPAbort` e a biblioteca encerra.
3. **A janela fecha com qualquer desfecho**: aprovada, recusada, cancelada, host fora ou erro. Sem
   isso o QR do cliente anterior ficaria na tela.
4. **A capacidade é configurável e nasce desligada.** `tef_pgweb_qr_na_tela = 1` acrescenta
   `CAP_QR` e `CAP_MSG_CHECKOUT` às capacidades declaradas. Declarar a capacidade é o que faz a
   biblioteca pedir a tela, então prometer sem ter a tela travaria a venda. As lojas seguem com o
   Pix no pinpad até o dono ver funcionando aqui.

### Duas coisas para confirmar no passo 11, com o host de verdade

Não dá para provar com a biblioteca parada, então elas estão escritas no código e aparecem na
auditoria de toda venda de Pix:

1. o conteúdo do QR vem de `PW_iGetResult(PWINFO_AUTHPOSQRCODE)`, e não do `szPrompt`, que tem 84
   caracteres e não comporta um payload de Pix;
2. a resposta de um pedido de exibição é `PW_iAddParam` do mesmo identificador com valor vazio.

Se alguma das duas estiver errada, a linha de auditoria diz exatamente o que foi lido e respondido.

### O que já estava pronto e o doc dizia que faltava

Parcelas na tela de pagamento já existem, com a chave `tef_perguntar_parcelas` (desligada por
padrão, todo crédito sai à vista). Cancelamento pela biblioteca também já existe
(`ProvedorPGWebLib.CancelarAsync`, `PWOPER_SALEVOID`).

Bateria do PDV: 2913 OK, 0 falhas, sendo 22 aferições novas só do QR na tela.
