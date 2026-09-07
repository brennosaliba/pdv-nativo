# Homologação do TEF: comece por aqui

Escrito em 07/09/2026, com a máquina já preparada. Este arquivo é o que abrir na hora de sentar para
homologar. O mapa completo dos 58 passos vem em `TEF_PAYGO_homologacao.md`.

## Qual executável abrir

```
C:\Users\Waz\pdv-nativo\publish\v38\Pdv.exe
```

64 bits, 180,9 MB, versão 0.5.9, gerado do commit `100065d`. É este que tem a tela do QR do Pix, a
escolha de ambiente e o conserto do "TEF ativo" mentiroso.

**Não use o `publish\homolog-x86\Pdv.exe`.** Ele era a saída para a biblioteca que só existia em 32
bits. O kit avulso traz a de 64, então o caixa voltou ao build normal. Aquele exe também não tem
nada do que foi feito hoje.

Conferido: o exe abre (`Pdv.exe --cupom-teste` sai com código 0 e gera o cupom de teste), e a
máquina do PE diz x64.

Como foi gerado, se precisar refazer:

```bash
dotnet publish Pdv.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:PublishReadyToRun=true -p:DebugType=none -o publish/v38
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

## Passo 01, a instalação

É o único passo que eu não faço sozinho, porque exige digitar a senha de instalação. Eu não digito
senha em campo nenhum, em lugar nenhum. Você digita, eu fico do lado lendo o retorno.

1. Abra o PDV e vá em **Configuração**.
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

## O risco que a PayGo declarou, e que é seu para decidir

O kit sem Warsaw não traz a proteção contra o vírus Prillex, que ataca terminal de pagamento. A
PayGo libera o uso em produção por conta de quem usa, e recomenda compensar com antivírus, política
de rede e isolamento. Para homologar aqui não muda nada. Para levar às lojas, decida antes.
