# Homologação do TEF: comece por aqui

Escrito em 07/09/2026, com a máquina já preparada. Este arquivo é o que abrir na hora de sentar para
homologar. O mapa completo dos 58 passos vem em `TEF_PAYGO_homologacao.md`.

## Qual executável abrir

```
C:\Users\Waz\pdv-nativo\publish\v40\Pdv.exe
```

64 bits, 180,9 MB, versão 0.5.9. É este que tem a tela do QR do Pix, a escolha de ambiente e o
conserto do "TEF ativo" mentiroso.

**Não use o v38 nem o v39.** O v38 chamava a escolha de ambiente antes de inicializar a biblioteca, e
ela recusava com "não instalado": o caixa seguia falando com o servidor de produção sem ninguém
perceber, medido no seu teste das 17h13. O v39 corrigiu isso, mas é anterior aos oito consertos do
roteiro e ao filtro de redes.

**Não use o `publish\homolog-x86\Pdv.exe`.** Ele era a saída para a biblioteca que só existia em 32
bits. O kit avulso traz a de 64, então o caixa voltou ao build normal. Aquele exe também não tem
nada do que foi feito hoje.

Conferido: o exe abre (`Pdv.exe --cupom-teste` sai com código 0 e gera o cupom de teste), e a
máquina do PE diz x64.

Como foi gerado, se precisar refazer:

```bash
dotnet publish Pdv.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:PublishReadyToRun=true -p:DebugType=none -o publish/v40
```

## O que já está pronto nesta máquina

| item | estado |
|---|---|
| biblioteca `PGWebLib.dll` 4.1.50.924, 64 bits | em `C:\PGWebLib\x64` |
| PayGo Windows e Warsaw | desinstalados, e não são mais necessários |
| provedor do caixa | `pgweblib` |
| pasta de trabalho | `C:\ProgramData\PdvNativo\pgweb64` |
| ambiente | homologação (`PW_iSetEnvironment(ENVRMNT_TEST)`) |
| QR do Pix na tela | ligado |
| pinpad | Gertec PPC-930 na COM5 |
| bateria do PDV | 2.913 aferições, zero falha |

Falta uma coisa só: **o terminal ainda não foi instalado**. A biblioteca sobe, o menu administrativo
abre, e a lista de operações de venda responde "não instalado". Isso é exatamente o passo 01.

## As vendas do roteiro não emitem nota, e isso é de propósito

Este caixa está com o **modo de homologação** ligado (`homologacao` = 1). Ele já mantinha a venda de
teste fora da nuvem, fora do fechamento do caixa e fora do contador de pendências. A NFC-e era a
última parte dela que ainda valia de verdade, e valia mesmo: o `terminal` daqui está em **produção**,
com o CNPJ 62.177.839/0002-38 e a série 3 da Savassi, e o modo fiscal é `nfce`.

Sem isso, cada passo do roteiro (são mais de vinte vendas, com a linha "Venda de teste") bateria na
SEFAZ de verdade. Rejeitada, você teria que dispensar "Nota não autorizada" a cada passo, com número
de série queimado; autorizada, seria uma nota válida da loja para um produto que não existe.

Agora a venda do roteiro sai como **recibo**: o papel continua saindo, e a nota não. Quando o roteiro
acabar e o caixa voltar a vender, desligue o modo de homologação e a NFC-e volta sozinha.

## Passo 01, a instalação

É o único passo que eu não faço sozinho, porque exige digitar a senha de instalação. Eu não digito
senha em campo nenhum, em lugar nenhum. Você digita, eu fico do lado lendo o retorno.

1. Abra o PDV e vá em **Configuração**. Ela é um assistente com passos: vá avançando até o passo
   **Maquininha**. Os botões do TEF só existem ali, e só depois de escolher **PayGo (biblioteca)**
   no seletor daquele passo. Aí aparecem os três: Testar a maquininha, Instalar ponto de captura e
   ADM.
2. Confirme que a maquininha está em **PayGo (biblioteca)**.
3. Toque em **Testar a maquininha**. O esperado agora é a mensagem dizendo que o terminal não está
   instalado. Isso é o certo, não é defeito.
4. Toque em **Instalar**.
5. A biblioteca vai pedir os dados na tela do próprio caixa. Digite o ponto de captura e a senha que
   a PayGo mandou.
6. Esperado: "TRANSAÇÃO APROVADA" e o recibo impresso.

### Se aparecer `[NA A110] TIPO PONTO DE CAPTURA INCORRETO`

Não é defeito do caixa e não adianta repetir. Quer dizer que o ponto de captura é do tipo errado: a
integração por biblioteca precisa de um ponto de captura do tipo **automação**, e o 114975 é do tipo
ControlPay. Só a PayGo cria o outro.

O texto do chamado já está pronto em `docs/PEDIDO_PAYGO_SANDBOX_DLL.md`. Abra em
https://dev.proj.setis.com.br/servicedesk/customer/portal/16 e cole.

Vale registrar: hoje de manhã, com o PayGo Windows instalado, o terminal apareceu **instalado e
ativo** pela biblioteca, com as 19 operações inclusive VENDA. Aquela ativação morreu junto com a
desinstalação, e a pasta que a guardava foi apagada, então não dá para saber qual ponto de captura
tinha sido usado. Descobrimos na primeira tentativa.

## Depois que instalar

1. Toque em **Testar a maquininha** de novo. Agora tem que dizer que respondeu.
2. Rode o **Teste de comunicação** no menu administrativo (é o passo 12 do roteiro, e é barato).
3. Só então comece a gravar, do passo 02 em diante.

## Deixe a rede do cartão em branco o roteiro inteiro

Esta é a única regra de configuração que vale para os 58 passos, e ela é ao contrário do que parece.

Na Configuração, passo Maquininha, a **rede do cartão** tem que ficar na primeira opção da lista,
**(automático: a PayGo escolhe a rede)**. Rede gravada ali é rede fixa: o caixa manda ela direto para
a biblioteca e o menu de seleção nunca abre. Com o menu fechado, três coisas ficam impossíveis de
gravar:

- o **passo 05**, que manda apertar Esc justamente nesse menu;
- os passos **38, 40, 45 e 46**, que pedem o autorizador **REDE**, e não o C6PAY;
- os passos **06, 07 e 08**, que o roteiro descreve sem pré-selecionar autorizador nenhum.

Gravar C6PAY para adiantar o passo 03 custa mais caro do que economiza: em cada passo de REDE você
teria que voltar na Configuração, trocar, salvar e voltar. Com a rede em branco, o menu abre em toda
venda e você escolhe o autorizador do passo na hora.

## E encurte o menu para as três redes do roteiro

O terminal lista tudo o que estiver instalado nele, e tocar no vizinho errado queima a venda e o
passo. O roteiro inteiro usa três autorizadores e nenhum outro: **C6PAY** na maioria das vendas,
**REDE** nos passos 38, 40, 45 e 46, e **PIX C6 BANK** nos passos 11, 55, 56 e 57.

Na mesma tela, logo abaixo das duas redes, tem o campo **Redes que aparecem para o caixa escolher**.
Digite:

```
C6PAY, REDE, PIX C6 BANK
```

Isso encurta a lista, não fixa a rede: o menu continua abrindo em toda venda, com as três, e o Esc do
passo 05 continua valendo. Em branco o campo volta ao normal e o caixa vê todas as redes do terminal,
que é o certo na loja. Se você digitar um nome que o terminal não tem, o caixa mostra a lista inteira
em vez de deixar você sem opção, e registra isso na auditoria.

## O roteiro erra os números que ele mesmo cita

Quando um passo diz "o teste será continuado no passo seguinte, PASSO NN", o NN está sempre **dois a
menos** do que o passo real. Dá para conferir sem adivinhar, porque o próprio texto diz "no passo
seguinte": o passo 33 cita o 32, o 35 cita o 34, o 41 cita o 40, o 43 cita o 42 e o 45 cita o 44.

Isso contamina os cancelamentos, que citam a venda pelo número. Some 2 e cancele esta venda:

| Passo | O roteiro diz | Cancele mesmo a venda do | Que é de |
|---|---|---|---|
| 22 | PASSO 18 | passo 20 | R$ 2,00 |
| 23 | PASSO 19 | passo 21 | R$ 12.345,67 |
| 24 | PASSO 17 | passo 19 | R$ 1,00 |

Dois ficam sem conta que feche, e nesses vale perguntar à PayGo antes de gravar: o **passo 25** cita
o PASSO 02, e a venda de valor máximo é mesmo a única que faz sentido cancelar pelo menu
administrativo; o **passo 57** cita o PASSO 53, que não é venda de Pix nenhuma, e o cancelamento que
ele espera é **negado**, com "TRANSAÇÃO NEGADA PELO HOST".

## Enquanto grava

- **Não narre em voz alta.** A evidência é a tela e o recibo. A planilha é quem explica.
- Todo horário na tela é 24 horas.
- O que a PayGo confere em quase todo passo é sempre a mesma tríade: a mensagem que o operador viu,
  o recibo impresso, e a transação confirmada para a automação.

## Duas coisas para confirmar na primeira venda de Pix

O caixa desenha o QR na própria tela (o passo 55 exige isso, com Esc cancelando). Duas partes do
contrato eu li no cabeçalho oficial e ainda não confrontei com o host, porque dependem do terminal
instalado. As duas aparecem na auditoria de toda venda de Pix:

1. o conteúdo do QR vem de `PW_iGetResult(PWINFO_AUTHPOSQRCODE)`;
2. a resposta de um pedido de exibição é `PW_iAddParam` do mesmo identificador com valor vazio.

Se alguma estiver errada, o passo 11 mostra, e a linha de auditoria diz o que foi lido e respondido.


## As redes que aparecem para o caixa

O roteiro usa três e nada mais: **C6PAY** na maioria das vendas, **REDE** nos passos 38, 40 e 45, e
**PIX C6 BANK** nos passos 11, 55 e 56.

Não dá para fixar a rede na configuração, e isso é armadilha: o passo 05 manda apertar Esc **no menu
de seleção da rede**. Rede fixa faz a biblioteca parar de perguntar, e o passo fica impossível.

A saída foi encurtar o menu em vez de matá-lo. Em Configuração, passo Maquininha, bloco da
biblioteca, campo **"Redes que aparecem para o caixa escolher"**, digite:

```
C6PAY, REDE, PIX C6 BANK
```

O menu continua aparecendo, com essas três. Em branco, volta a mostrar todas, que é o certo na loja.
Se o que você digitar não casar com nenhuma rede do terminal, ele mostra todas e registra o aviso:
menu vazio no meio de uma venda seria pior do que filtro ignorado.

O resumo do fim da configuração passa a dizer que a lista está encurtada, para isso não virar uma
configuração invisível que ninguém lembra.

## A tabela de valores, que é o que mais poupa tempo

Cada valor dispara um cenário fixo no simulador da PayGo. Conferido linha a linha no texto oficial.

| Valor | Passo | Autorizador | O que acontece |
|---|---|---|---|
| R$ 1,00 | 19 | C6PAY | venda aprovada, depois cancelada no passo 24 |
| R$ 2,00 | 20 | C6PAY | venda aprovada, depois cancelada no passo 22 |
| R$ 10,00 | 38 | **REDE** | venda aprovada, confirmação manual |
| R$ 77,00 | 58 | C6PAY | comprovante gráfico. **Só C6Pay Android, não vale para o PDV** |
| R$ 333,00 | 40 | **REDE** | venda aprovada, desfazimento manual |
| R$ 500,00 | 56 | **PIX C6 BANK** | venda Pix aprovada por aprovação automática |
| R$ 999,00 | 48 | C6PAY | contactless aprovada **sem pedir senha** |
| R$ 1.000,01 | 04 | C6PAY | **venda negada**, erro NEGADA 01 |
| R$ 1.001,00 | 28 e 29 | C6PAY | pede um dado digitado na tag 0x2F. Digitar `ABC123` |
| R$ 1.002,00 | 30 e 31 | C6PAY | menu genérico com `123456` e `ABCDEF`, prompt `SELECIONAR:`. Escolher `ABCDEF` |
| R$ 1.003,00 | 32 | C6PAY | aprovada com mensagem de 80 caracteres |
| R$ 1.005,50 | 33 | C6PAY | aprovada. Deixa a transação pendente do lado deles |
| R$ 1.005,51 | 34 | C6PAY | **negada**, devolve a pendente. O PDV responde com **confirmação** e não imprime nada |
| R$ 1.005,60 | 35 | C6PAY | aprovada. Prepara a pendente desconhecida |
| R$ 1.005,61 | 36 | C6PAY | **negada**, devolve uma pendente que o PDV não conhece. O PDV responde com **desfazimento** e não imprime nada |
| R$ 1.011,00 | 39 | C6PAY | aprovada, desfazimento manual |
| R$ 1.012,00 | 37 | C6PAY | aprovada, confirmação manual |
| R$ 1.013,00 | 41 e 42 | não cita | aprovada, desfazimento por falha na liberação. Só autoatendimento |
| R$ 1.017,00 | 43 e 44 | C6PAY | aprovada, cancelamento pedindo Referência Local |
| R$ 1.018,00 | 45 e 46 | **REDE** | aprovada, cancelamento pedindo Referência Externa |
| R$ 1.020,00 | 47 | C6PAY | contactless aprovada, **com senha** |
| R$ 12.345,67 | 21 | C6PAY | venda grande, cancelada no passo 23 |
| R$ 100.000,00 | 02 | não cita | valor máximo que a automação aceita. R$ 100.000,00 é só exemplo. Se o PDV tiver teto menor, use o teto do PDV |
| qualquer valor | 03, 06, 07, 08, 09, 10, 11, 26, 54, 55 | conforme a tabela do passo | cenário comum, sem gatilho por valor |

Correções em relação ao que estava de cabeça:

- **1005,50 e 1005,60 sozinhos não fecham o teste.** A pendência só aparece na **segunda** venda, de R$ 1.005,51 e R$ 1.005,61. São dois pares, e a resposta muda: 1.005,51 pede **confirmação**, 1.005,61 pede **desfazimento**.
- **12.345,67 não é a "venda grande".** É a venda do passo 21, que existe para ser cancelada no passo 23. A venda de valor máximo é o passo 02, com R$ 100.000,00 de exemplo.
- **77,00 está certo, mas o passo 58 vale só para C6Pay Android.** Não entra na homologação da biblioteca Windows.
- Faltavam na lista: 1,00, 2,00, 10,00, 333,00, 999,00, 1.005,51, 1.005,61, 1.011,00, 1.013,00, 1.017,00, 1.018,00 e 1.020,00.

Arquivos consultados: `C:\Users\Waz\AppData\Local\Temp\claude\C--Users-Waz\d4a041d3-321c-45bd-899d-e4864a0362c2\scratchpad\roteiro.txt`, `C:\PGWebLib\PGWebLib.h` e `C:\Users\Waz\pdv-nativo\Pdv.Nucleo\SelecaoTef.cs`. Nenhum código foi alterado.
### Uma coisa esquisita do roteiro, que já está resolvida aqui

As referências internas do roteiro estão todas erradas em dois. O passo 22 diz que cancela "o PASSO
18" quando na verdade cancela o 20, e isso se repete em cinco lugares onde ele diz "continuado no
passo seguinte, PASSO NN" e o NN não é o seguinte. A tabela acima já está com os passos certos.

## O risco que a PayGo declarou, e que é seu para decidir

O kit sem Warsaw não traz a proteção contra o vírus Prillex, que ataca terminal de pagamento. A
PayGo libera o uso em produção por conta de quem usa, e recomenda compensar com antivírus, política
de rede e isolamento. Para homologar aqui não muda nada. Para levar às lojas, decida antes.
