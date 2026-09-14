# Instalar o MMFood 1.0 na loja (cartão pela biblioteca da PayGo)

Vale a partir da 1.0.1 (11/09/2026), quando a integração pela **Biblioteca Windows
(PGWebLib.dll)** foi homologada pela PayGo/SETIS como **MMFood 1.0.0**. Revisto em
14/09/2026 depois da instalação na loja Castelo.

## Qual biblioteca o caixa usa

* **A biblioteca vem dentro do caixa**, na pasta `pgweb\` ao lado do `Pdv.exe`. É o kit
  avulso 4.1.50.924 (64 bits, sem Warsaw), que carrega de qualquer pasta.
* **Não instale o PayGo Windows na loja.** O caixa não precisa dele, e ele pode ficar
  segurando a porta do pinpad. Se já estiver instalado, feche (perto do relógio e no
  Gerenciador de Tarefas: PayGo, PayGoLauncher, ControlPay) ou desinstale.
* Ponto em aberto com a PayGo: a homologação de 11/09 rodou com a biblioteca 4.1.50.24 (a
  protegida, que vem com o PayGo Windows). Confirmar com a PayGo que a aprovação vale para a
  4.1.50.924 que vai embarcada.

## Liberações de produção

| Loja | CNPJ / Razão Social | Ponto de captura ("Nº CHECK-OUTS") |
|---|---|---|
| Savassi (11/09/2026) | **62.177.839/0002-38** MM FOOD SERVICE PRODUTOS ALIMENTICIOS LTDA | **6687461** |
| Castelo (14/09/2026) | **62.177.839/0003-19** MM FOOD SERVICE PRODUTOS ALIMENTICIOS LTDA | **6687636** |

Tipo de TEF: PayGoWeb (é a PGWebLib). Senha técnica: é do dono, chega pelo canal da PayGo e
ninguém a digita por ele. A PayGo libera **um número por caixa**: um segundo caixa integrado
precisa de outro, e o número de um caixa não vai para outro computador.

## Antes de ir à loja

| O que | Quem | Detalhe |
|---|---|---|
| Instalador `InstalarPdv.exe` | gerado por `scripts\gerar-instalador.ps1` | Não instala o PayGo Windows (não precisa). |
| Pinpad homologado PayGo (Gertec PPC-930 ou outro) com cabo USB | dono | Maquininha POS sem fio não serve para o caixa integrado. |
| Driver USB do pinpad | loja | Sem driver o Windows não cria a porta COM e nada funciona. |
| Internet no PC do caixa | loja | A biblioteca fala direto com o host da PayGo. |
| Senha técnica em mãos | dono | Pedida uma vez, na instalação do ponto de captura. |

## Passo a passo

1. Rode o `InstalarPdv.exe`. Ele preserva tudo em `C:\ProgramData\PdvNativo` (banco,
   pareamento com o painel, certificado, logins).
2. Ligue o pinpad no USB, de preferência numa porta de trás do computador, e deixe sempre
   na mesma entrada (trocar de entrada troca a porta COM: na Castelo foi de COM2 para COM8).
3. Abra o caixa, entre como administrador e vá em **Configuração, passo Maquininha**:
   - Escolha **TEF PayGo**.
   - Toque em **Testar pinpad**. Em até 10 segundos aparece uma linha só:
     - "Gertec PIN Pad PPC respondeu na COM3.": pode seguir;
     - "A COM2 está ocupada pelo programa X.": feche o X (o botão **Fechar o X** aparece
       quando o caixa pode fechar sozinho) e teste de novo;
     - "O pinpad está na COM2 mas não respondeu.": tire o cabo USB, espere 10 segundos,
       ligue de novo e teste de novo;
     - "Nenhum pinpad ligado neste computador.": cabo, driver ou aparelho errado.
   - A porta fica em **Automática**. Só mude em **Mostrar opções avançadas** se a PayGo pedir.
   - Ponto de captura e CNPJ da instalação: os da tabela acima (servem para o comprovante).
   - O ambiente já é **produção**: não existe chave de teste numa instalação nova.
4. Toque em **Instalar ponto de captura**. O caixa testa o pinpad de novo e **não chama a
   biblioteca** se ele não respondeu. Com o pinpad bom, abre a tela da instalação com o
   cronômetro, a última mensagem da maquininha, o prazo (3 minutos) e o botão **Cancelar**.
   A biblioteca pergunta o que falta na própria tela; a senha técnica é digitada pelo
   **dono**. Deu certo, o botão some e o caixa imprime o comprovante (guarde).
5. Toque em **Testar a maquininha** e depois em **Salvar**.
6. Faça uma venda de **R$ 1,00 no crédito** e cancele em seguida. Confira o comprovante e,
   no ERP, a venda e o estorno.

## Se der errado

- **Tela da instalação parada e "A maquininha não responde há 40 s"**: tire o cabo USB do
  pinpad. Isso encerra a tentativa em menos de um segundo (medido na Castelo). Só faça isso
  na instalação ou no menu administrativo, nunca no meio de uma cobrança.
- **"A maquininha ainda está presa na tentativa anterior"**: a tentativa de antes ainda não
  voltou. Tire o cabo por 10 segundos; se não voltar, feche e abra o caixa.
- **"Não achei a maquininha na porta N"** (era "dado 32514 PWRET_PPNOTFOUND -2489"): a
  biblioteca não achou pinpad. Cabo, driver, porta ou outro programa segurando a porta.
- "PWRET_NOTINST" ou "não instalado" na primeira venda: o ponto de captura não foi instalado
  (passo 4). O botão **Instalar ponto de captura** volta a aparecer.
- Senha técnica recusada: é a da liberação de produção (a do sandbox não vale).
- Para mandar à PayGo ou ao fabricante do pinpad: `C:\ProgramData\PdvNativo\pgweb\Log\comms_AAMMDD.log`
  e `ppsers_AAMMDD.log` do dia (o `ppsers` mostra se o pinpad respondeu ao primeiro sinal).
  O log pode ter a senha técnica em texto: não repasse a terceiros além da PayGo.
- O certificado diz "MMFood 1.0.0": qualquer versão 1.0.x do caixa manda essa identificação;
  mudar o nome ou a versão maior exige avisar a PayGo.
