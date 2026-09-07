# O que pedir à PayGo para liberar o teste da DLL

Abrir chamado no portal da Setis (a empresa que atende a integração PayGo):
https://dev.proj.setis.com.br/servicedesk/customer/portal/16

Tipo do chamado: homologação de integração. Modo: **Biblioteca Windows (PGWebLib.dll)**.

## Texto pronto para colar no chamado

> Boa tarde. Somos a American Day (CNPJ 62.177.839/0001-57) e desenvolvemos internamente o
> nosso PDV para as nossas lojas. Já operamos com o PayGo Windows pela troca de arquivos e
> agora queremos homologar a integração pela **Biblioteca Windows (PGWebLib.dll)**.
>
> Dados da automação:
> - Nome da automação: Pdv.AmericanDay
> - Desenvolvedor: American Day
> - Versão: 0.5.9
> - Sistema: Windows 64 bits, aplicação própria (não é BI, não é Postman, é o PDV que roda no caixa)
> - Integração pretendida: Biblioteca Windows (DLL), operações de venda, cancelamento,
>   administrativa, reimpressão e instalação, com Pix por QR Code
>
> Para iniciar os testes, preciso da liberação de:
>
> 1. **Ponto de Captura (PdC) de homologação** para esta automação, com o número do PdC.
> 2. **CNPJ de teste** autorizado no ambiente de homologação, se for diferente do nosso.
> 3. **Senha ou ID de instalação** para ativar o PayGo Windows no ambiente de testes.
> 4. Confirmação de que devo usar o **PayGo Windows versão 5.1.50.24** (kit
>    20260821-Integracao-SetupPayGo_v5.1.50.24.zip) e de que a PGWebLib.dll de 64 bits vem
>    com esse instalador.
> 5. Confirmação de que o roteiro válido é o **Roteiro de testes v20260819** com a planilha
>    correspondente, já que ele tem 58 passos e o anterior tinha 55.
> 6. **Cartões de teste** e a lista de valores que disparam cada cenário do roteiro
>    (negada, pendente, contactless, dado genérico, mensagem longa).
> 7. Se o **pinpad físico é obrigatório** para os cenários de cartão, ou se existe simulador
>    aceito na homologação. Se for obrigatório, qual modelo vocês recomendam.
> 8. Prazo estimado de análise depois do envio das evidências.
>
> As evidências serão enviadas com a planilha preenchida e os logs coletados pelo
> PGLogCollector, conforme o manual do kit.

## O que já está pronto do nosso lado

- Provedor da biblioteca escrito e testado: 2.782 verificações passando, zero falhas.
- Binding da DLL copiado do exemplo oficial da PayGo em C#, sem nada deduzido.
- Kit de 21/08 baixado, com o instalador do PayGo Windows 5.1.50.24, o roteiro v20260819 e o
  manual do PGLogCollector.
- Mapeamento dos 58 passos do roteiro contra o que o PDV faz, em
  `docs/TEF_PAYGO_homologacao.md`.

## O que falta do nosso lado, depois que eles liberarem

1. Instalar o PayGo Windows 5.1.50.24 na máquina de homologação e ativar com o PdC e a senha.
2. Apontar a configuração `tef_pgweb_dll` para a pasta da DLL de 64 bits e escolher
   "PayGo (biblioteca)" na tela de configuração do caixa.
3. Três coisas que ainda não existem no PDV e aparecem no roteiro: escolher parcelas na tela
   de pagamento, tela para escolher a venda a cancelar, e desenhar o QR do Pix na tela do caixa.
4. Rodar os 58 passos, preencher a planilha e coletar os logs.

## Atualização de 07/09/2026: o que já foi medido e o que falta pedir

O PayGo Windows 5.1.50.24 já está instalado e ativado no ambiente Demonstração nesta máquina
(ponto de captura 114975, terminal 7027, pinpad Gertec PPC-930 na COM5). A biblioteca de 32 bits
carrega e o PDV fala com ela de ponta a ponta: os 21 pontos de entrada existem, a instalação abre o
pinpad e fecha TLS com o servidor de sandbox da PayGo.

A instalação pela biblioteca foi recusada pelo servidor com o código `[NA A110] TIPO PONTO DE
CAPTURA INCORRETO`. O ponto de captura 114975 é do tipo ControlPay. A integração por biblioteca
exige um ponto de captura do tipo automação, e isso só a PayGo cria.

Então o pedido número 1 do chamado passa a ser este, com o texto pronto:

> Já temos o PayGo Windows 5.1.50.24 ativado em Demonstração com o ponto de captura 114975, que é do
> tipo ControlPay. Ao instalar pela biblioteca PGWebLib.dll o servidor responde `[NA A110] TIPO PONTO
> DE CAPTURA INCORRETO`. Precisamos de um ponto de captura de homologação do tipo automação
> (biblioteca) vinculado ao CNPJ 62.177.839/0001-57, com o identificador e a senha de instalação
> correspondentes. Perguntamos também se a PayGo disponibiliza a PGWebLib.dll em 64 bits, já que o
> instalador 5.1.50.24 só entrega a de 32 bits; enquanto isso vamos homologar com o PDV em 32 bits.
