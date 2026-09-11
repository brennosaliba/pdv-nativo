# Instalar o MMFood 1.0 na loja (cartão pela biblioteca da PayGo)

Vale a partir da 1.0.0 (11/09/2026), quando a integração pela **Biblioteca Windows
(PGWebLib.dll)** foi homologada pela PayGo/SETIS como **MMFood 1.0.0**. A biblioteca viaja
dentro do caixa (pasta `pgweb` ao lado do `Pdv.exe`). **O PayGo Windows não é mais
instalado na loja** e o instalador não o leva junto.

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

## Antes de ir à loja

| O que | Quem | Detalhe |
|---|---|---|
| Instalador `InstalarPdv.exe` (v86 ou mais novo) | gerado por `scripts\gerar-instalador.ps1` | 176 MB; num pendrive ou pelo link de download. Ele já sai sem PayGo Windows. |
| Pinpad homologado PayGo (Gertec PPC-930 ou outro) com cabo USB | dono | A maquininha de balcão (POS) continua servindo como avulsa; o caixa integrado usa o pinpad. |
| Driver USB do pinpad instalado no Windows da loja | quem instala | O Gertec cria uma porta COM; o caixa acha sozinho (porta 0 = automática). |
| Internet no PC do caixa | loja | A biblioteca fala direto com o host da PayGo; sem internet a venda no cartão não sai. |
| Senha técnica em mãos | dono | Pedida uma vez, na instalação do ponto de captura. |

## Passo a passo

1. Rode o `InstalarPdv.exe`. Ele copia o caixa para `Program Files\MMFood`, com a pasta
   `pgweb` dentro, e preserva tudo que já existia em `C:\ProgramData\PdvNativo` (banco,
   pareamento com o painel, certificado, logins do chat e do WhatsApp). Num PC que já tinha
   o PDV antigo ("PDV MMTech" ou "PDV American Day"), ele atualiza no lugar.
2. Abra o caixa, entre como administrador e vá em **Configuração, passo Maquininha**:
   - Escolha **TEF PayGo**.
   - **Ponto de captura**: `6687461`.
   - **CNPJ**: `62.177.839/0002-38` (o da liberação; em branco o caixa usaria o CNPJ do
     cadastro da loja).
   - Porta do pinpad: 0 (automática), a não ser que a PayGo peça outra.
   - O ambiente já é **produção**: não existe chave de teste numa instalação nova, e o
     instalador não grava nenhuma.
3. Toque em **Instalar ponto de captura**. A biblioteca pergunta o que falta na própria
   tela; a senha técnica é digitada pelo **dono**. Deu certo, o botão some e o caixa imprime
   o comprovante de instalação (guarde).
4. Toque em **Testar a maquininha**: tem que dizer que o terminal responde.
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

- "PWRET_NOTINST" ou "não instalado" na primeira venda: o ponto de captura não foi instalado
  (passo 3). O botão **Instalar ponto de captura** volta a aparecer.
- "TEF não responde": pinpad sem driver ou porta errada; confira no Gerenciador de
  Dispositivos se a porta COM do pinpad aparece.
- Senha técnica recusada: é a da liberação de produção (a do sandbox não vale).
- O certificado diz "MMFood 1.0.0": qualquer versão 1.0.x do caixa manda essa identificação;
  mudar o nome ou a versão maior exige avisar a PayGo.
