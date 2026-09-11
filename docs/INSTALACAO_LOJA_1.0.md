# Instalar o MMFood 1.0 na loja (cartão pela biblioteca da PayGo)

Vale a partir da 1.0.1 (11/09/2026), quando a integração pela **Biblioteca Windows
(PGWebLib.dll)** foi homologada pela PayGo/SETIS como **MMFood 1.0.0**.

## O que a PayGo homologou, e o que isso exige na loja

A homologação foi feita com a **distribuição protegida** da biblioteca (a que vem dentro do
PayGo Windows, com o módulo de proteção Topaz/Warsaw). A PayGo é explícita: em produção a
biblioteca precisa desse módulo. Consequências práticas:

* **O PayGo Windows continua instalado na loja.** É ele que traz a biblioteca protegida e o
  serviço de proteção. Não desinstale.
* **A biblioteca só roda da pasta em que o PayGo Windows a instalou**
  (`C:\Program Files (x86)\PayGo\PGWebLib\x64`). Cópia em outra pasta carrega, mas a
  inicialização devolve -2414 e o caixa diz "não iniciou". Desde a 1.0.1 o caixa usa a
  pasta do PayGo Windows sozinho quando ele existe na máquina; a cópia embarcada em
  `pgweb\` ao lado do exe só vale em máquina sem PayGo Windows (kit avulso).
* O que sai da loja é o **ControlPay** (a integração antiga por WebService): o mesmo
  PayGo Windows passa a ser usado pela biblioteca, sem o servidor da ControlPay no meio.

## Liberação de produção (recebida em 11/09/2026)

| Dado | Valor |
|---|---|
| CNPJ / Razão Social | **62.177.839/0002-38** MM FOOD SERVICE PRODUTOS ALIMENTICIOS LTDA |
| Tipo de TEF | PayGoWeb (é a PGWebLib) |
| Ponto de captura (a PayGo chama de "Nº CHECK-OUTS") | **6687461** |
| Senha técnica | é do dono; chega pelo canal da PayGo e ninguém a digita por ele |

O ponto de captura de produção substitui o do sandbox (115998). A liberação é para o
CNPJ da filial 0002: o CNPJ da instalação tem que ser esse, mesmo que o cadastro da loja
no caixa esteja em outra filial (o comprovante do cartão sai com o CNPJ da instalação).
A PayGo libera **um número por caixa**: um segundo caixa integrado precisa de outro.

## Antes de ir à loja

| O que | Quem | Detalhe |
|---|---|---|
| Instalador `InstalarPdv.exe` (1.0.1 ou mais novo) | gerado por `scripts\gerar-instalador.ps1` | 176 MB; pendrive ou o link de download. Ele não instala o PayGo Windows: a loja já tem. |
| PayGo Windows instalado e funcionando | loja | O da Savassi é o 5.1.50.924, o mesmo que atende o ControlPay hoje. |
| Pinpad homologado PayGo (Gertec PPC-930 ou outro) com cabo USB | dono | A maquininha de balcão (POS) continua servindo como avulsa; o caixa integrado usa o pinpad. |
| Internet no PC do caixa | loja | A biblioteca fala direto com o host da PayGo; sem internet a venda no cartão não sai. |
| Senha técnica em mãos | dono | Pedida uma vez, na instalação do ponto de captura. |
| Senha de administrador do caixa | dono | É a que foi gravada na primeira instalação daquele PC (ou 1234, se ninguém cadastrou o dono). |

## Passo a passo

1. Rode o `InstalarPdv.exe`. Ele atualiza o PDV antigo no lugar e preserva tudo em
   `C:\ProgramData\PdvNativo` (banco, pareamento com o painel, certificado, logins do chat e
   do WhatsApp).
2. Abra o caixa, entre como administrador e vá em **Configuração, passo Maquininha**:
   - Escolha **TEF PayGo**.
   - **Ponto de captura**: `6687461`.
   - **CNPJ da instalação**: `62.177.839/0002-38` (o da liberação; em branco o caixa usaria o
     CNPJ do cadastro da loja).
   - Porta do pinpad: 0 (automática), a não ser que a PayGo peça outra.
   - O ambiente já é **produção**: não existe chave de teste numa instalação nova.
3. Toque em **Testar a maquininha**. A resposta diz de qual pasta a biblioteca carregou; tem
   que ser a do PayGo Windows. Se disser "não iniciou", a linha traz o motivo exato (o
   código que a biblioteca devolveu ou a pasta que não abriu).
4. Toque em **Instalar ponto de captura**. A biblioteca pergunta o que falta na própria
   tela; a senha técnica é digitada pelo **dono**. Deu certo, o botão some e o caixa imprime
   o comprovante de instalação (guarde).
5. Faça uma venda de **R$ 1,00 no crédito** e cancele em seguida (Cancelar / Imprimir,
   Estornar). Confira o comprovante e, no ERP, a venda e o estorno.
6. Deixe o log da biblioteca em `C:\ProgramData\PdvNativo\pgweb-paygo\Log`; é ele que a
   PayGo pede se algo der errado.

## O que continua igual

- Vender sem TEF (dinheiro, PIX estático, maquininha avulsa) não depende de nada disto.
- O agente fiscal (nota em contingência) segue no mesmo instalador.
- A configuração antiga de ControlPay/PayGo por arquivos fica guardada; só não é usada quando
  o provedor é TEF PayGo.

## Se der errado

- "não iniciou ... PW_iInit devolveu PWRET_TPNPIXERROR (-2414)": a biblioteca carregou de
  uma cópia fora da pasta do PayGo Windows. Confira se o PayGo Windows está instalado e se
  `C:\Program Files (x86)\PayGo\PGWebLib\x64\PGWebLib.dll` existe.
- "PWRET_NOTINST" ou "não instalado" na primeira venda: o ponto de captura não foi instalado
  (passo 4). O botão **Instalar ponto de captura** volta a aparecer.
- "TEF não responde": pinpad sem driver ou porta errada; confira no Gerenciador de
  Dispositivos se a porta COM do pinpad aparece.
- Senha técnica recusada: é a da liberação de produção (a do sandbox não vale).
- O certificado diz "MMFood 1.0.0": qualquer versão 1.0.x do caixa manda essa identificação;
  mudar o nome ou a versão maior exige avisar a PayGo.
- Atualização do caixa recusada com "acesso negado" no `Pdv.exe`: é a proteção do Warsaw
  travando o executável que usou a biblioteca (medido em 07/09 com a 5.1.50.24; não
  aconteceu nas medições de 11/09). Se acontecer, reinicie o PC e rode o instalador de novo.
