# Instalar o MMFood 1.0 na loja (cartão pela biblioteca da PayGo)

Vale a partir da 1.0.0 (v79, 11/09/2026), quando a integração pela **Biblioteca Windows
(PGWebLib.dll)** foi homologada pela PayGo/SETIS como **MMFood 1.0.0**. A biblioteca viaja
dentro do caixa (pasta `pgweb` ao lado do `Pdv.exe`). **O PayGo Windows não é mais
instalado na loja** e o instalador não o leva junto.

## Antes de ir à loja

| O que | Quem | Detalhe |
|---|---|---|
| Certificado de Conformidade e **ponto de captura de produção** | PayGo, depois do certificado | CNPJ da loja, número do ponto de captura e a senha técnica. A senha é do dono; ninguém a digita por ele. |
| Pinpad **Gertec PPC-930** (ou outro homologado PayGo) com cabo USB | dono | A maquininha de balcão (ControlPay/POS) continua servindo como avulsa, mas o caixa integrado usa o pinpad. |
| Driver USB do pinpad instalado no Windows da loja | quem instala | O Gertec cria uma porta COM; o caixa acha sozinho (porta 0 = automática). |
| Internet no PC do caixa | loja | A biblioteca fala direto com o host da PayGo; sem internet a venda no cartão não sai. |

## Passo a passo

1. Rode o `InstalarPdv.exe` gerado por `scripts\gerar-instalador.ps1` (ele já sai sem PayGo
   Windows). Ele copia o caixa para `Program Files\MMFood`, com a pasta `pgweb` dentro.
2. Abra o caixa, entre como administrador e vá em **Configuração → TEF**:
   - Provedor: **PGWebLib** (biblioteca).
   - Ambiente: **produção**.
   - CNPJ e ponto de captura: os que a PayGo enviou.
   - Pasta da DLL: deixe em branco (o caixa usa a `pgweb` que veio junto).
   - Porta do pinpad: 0 (automática), a não ser que a PayGo peça outra.
3. Em **TEF → Instalar ponto de captura**, o **dono** digita a senha técnica. O caixa manda
   o `PW_iInstall` e a PayGo devolve os parâmetros do terminal.
4. Faça uma venda de R$ 1,00 no crédito e cancele em seguida (TEF → Estornar). Confira o
   comprovante e, no ERP, a venda e o estorno.
5. Deixe o log da biblioteca em `C:\ProgramData\PdvNativo\pgweb-paygo\Log`; é ele que a
   PayGo pede se algo der errado.

## O que continua igual

- Vender sem TEF (dinheiro, PIX estático, maquininha avulsa) não depende de nada disto.
- O agente fiscal (nota em contingência) segue no mesmo instalador.
- A configuração antiga de ControlPay/PayGo por arquivos fica guardada; só não é usada quando
  o provedor é PGWebLib.

## Se der errado

- "PWRET_NOTINST" na primeira venda: o ponto de captura não foi instalado (passo 3).
- "TEF não responde": pinpad sem driver ou porta errada; confira no Gerenciador de
  Dispositivos se a porta COM do Gertec aparece.
- O certificado diz "MMFood 1.0.0": qualquer versão 1.0.x do caixa manda essa identificação;
  mudar o nome ou a versão maior exige avisar a PayGo.
