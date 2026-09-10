# Os 58 passos, contra o que o caixa faz hoje

Levantado em 07/09/2026, lendo o roteiro v20260819 passo a passo e conferindo cada um no código.
Companheiro de `HOMOLOGACAO_COMECAR_AQUI.md`, que é por onde começar.

## Como está o placar

| situação | passos | o que quer dizer |
|---|---|---|
| pronto | 27 | o caixa já faz, e eu achei o código |
| consertado hoje | 8 | era impossível de executar; o caixa foi mudado, com teste e vermelho provado antes do verde |
| AINDA FALTA | 9 | o caixa ainda não faz. Nenhum destes foi consertado |
| depende de voce | 9 | depende de senha, cartão, pinpad ou cadastro seu |
| depende da PayGo | 3 | depende de liberação ou dado que só a PayGo dá |
| duvida | 2 | o roteiro não deixa claro o que a automação tem que fazer; virou pergunta para o chamado |
| **total** | **58** | |

**Os que ainda faltam, em ordem:** passo 32, passo 34, passo 36, passo 37, passo 38, passo 39, passo 40, passo 52, passo 53.
São os de transação pendente, confirmação e desfazimento manual, mais o callback. Nenhum
deles impede começar a gravação: todos vêm depois do passo 32.

## Passo a passo

### Passo 01. Instalação

**Situação:** depende de voce

**O que você faz:** Abre a Configuração com a senha de administrador, vai no passo Maquininha, confere que está em PayGo (biblioteca) e toca em Instalar ponto de captura. Responde o que a maquininha pedir: CNPJ da loja, número do ponto de captura e a senha que a PayGo mandou.

**O que conferir:** Na tela: "✓ Ponto de captura instalado. Toque em Testar a maquininha e depois em Salvar." Recibo da instalação saindo na EPSON L3150 Series. Na auditoria (evento tef_pgweblib) a linha do CNF com o REQNUM. Depois, o botão Testar a maquininha tem que responder "✓ A biblioteca do PayGo respondeu" no lugar de "PayGo não instalado neste terminal". Confirmado hoje que o terminal está sem instalação:…

### Passo 02. Venda com valor máximo permitido pela automação (R$ 100.000,00)

**Situação:** depende de voce

**O que você faz:** Abre a venda, põe na comanda o produto de teste de R$ 100.000,00, toca em Finalizar, informa o CPF (acima de R$ 10.000,00 o caixa exige), escolhe Crédito, toca em Cobrar na maquininha e o cliente passa o cartão.

**O que conferir:** Título "Aguardando o cliente" com o valor R$ 100.000,00. Depois da aprovação, as duas vias saindo. Na tabela tef_transacao: situacao 'pago', valor_cent 10000000, provedor 'pgweblib'. Na auditoria: PWRET_OK, CNFREQ e o CNF com o REQNUM.

### Passo 03. Venda à vista aprovada com pré-seleção de parâmetros (C6PAY, cartão, crédito, à vista)

**Situação:** depende de voce

**O que você faz:** Com C6PAY já escolhido na Configuração, abre a venda de qualquer valor, toca em Crédito, confirma e o cliente passa o cartão. O caixa não pergunta a rede em nenhum momento.

**O que conferir:** Nenhum diálogo de rede aparece na tela. Recibo com C6PAY, crédito à vista. tef_transacao 'pago' com campo 010 = C6PAY, 731 = 1 e 732 = 1. Na auditoria não pode aparecer a linha "não está no menu ... perguntando à tela".

### Passo 04. Teste para retorno de Venda Negada (R$ 1.000,01 no C6PAY)

**Situação:** depende de voce

**O que você faz:** Com C6PAY escolhido, abre a venda com o produto de teste de R$ 1.000,01, toca em Crédito e o cliente passa o cartão. A rede recusa.

**O que conferir:** Tela: "Pagamento não aprovado", com a frase que a rede mandou (NEGADA 01) logo abaixo, e o recado de que o cliente não foi cobrado. Os botões oferecidos têm que ser só Tentar de novo, Trocar forma e (se não houver outro pagamento na venda) Cancelar venda. tef_transacao com situacao 'recusado', campo 009 = 1, e nenhuma linha de CNF na auditoria.

### Passo 05. Venda negada, rede desconhecida (Esc no menu de seleção da rede)

**Situação:** consertado hoje

**O que você faz:** Sem rede escolhida na Configuração, abre a venda, toca em Crédito, confirma o valor e, quando o caixa perguntar a rede, aperta Esc.

**O que conferir:** Tela: "Cobrança cancelada", com "operação cancelada pelo operador" e o recado "O cliente NÃO foi cobrado." tef_transacao com situacao 'cancelado' e nenhum CNF na auditoria. Se aparecer venda aprovada, o menu foi respondido pelo PDV e não pelo operador.

### Passo 06. Venda aprovada, teste de tipo de cartão crédito

**Situação:** pronto

**O que você faz:** Abre a venda de qualquer valor, toca em Crédito, confirma o valor e insere o cartão no pinpad quando a maquininha pedir.

**O que conferir:** Os recados do pinpad ("INSIRA O CARTAO", "DIGITE A SENHA", "RETIRE O CARTAO") aparecendo na tela do caixa. Recibo com crédito e a bandeira. tef_transacao com situacao 'pago', campo 731 = 1, e o CNF com o REQNUM na auditoria. O PAN completo (PWINFO_CARDFULLPAN) não pode aparecer em lugar nenhum: a bateria prova que ele nunca é lido.

### Passo 07. Venda aprovada, teste de tipo de cartão débito

**Situação:** pronto

**O que você faz:** Abre a venda de qualquer valor, toca em Débito, confirma o valor e insere o cartão no pinpad quando a maquininha pedir.

**O que conferir:** Recibo com débito. tef_transacao 'pago' com 731 = 2, parcelas = 1 e 732 = 1. Nenhuma pergunta de parcelas na tela.

### Passo 08. Venda aprovada, teste de tipo de financiamento (crédito parcelado pela loja em 99x)

**Situação:** depende de voce

**O que você faz:** Com a opção de parcelas ligada, abre a venda, toca em Crédito, confirma o valor, digita 99 na pergunta "Em quantas vezes?" e o cliente passa o cartão.

**O que conferir:** Título da tela "Aguardando o cliente · 99x" e o detalhe com "em 99x". Recibo com "parcelada pela loja" em 99 vezes. tef_transacao com parcelas = 99, campo 018 = 99 e 732 = 3.

### Passo 09. Venda, teste de recibos diferenciados #1 (reduzido para o cliente, diferenciado para o lojista) (opcional)

**Situação:** consertado hoje

**O que você faz:** Abre a venda, toca em Crédito, cobra qualquer valor no C6PAY e espera as duas vias saírem sozinhas na impressora.

**O que conferir:** Tem que sair a via REDUZIDA para o portador do cartão e a via diferenciada para o lojista, duas folhas. Hoje sai só a do lojista.

### Passo 10. Venda, teste de recibos diferenciados #2 (diferenciado para o cliente, diferenciado para o lojista)

**Situação:** pronto

**O que você faz:** Abre a venda, toca em Crédito, cobra qualquer valor no C6PAY e espera as duas vias saírem sozinhas.

**O que conferir:** Duas folhas diferentes saindo, uma do cliente e uma da loja. Se o campo 737 vier 1 ou 2, só a via correspondente sai, e isso é correto. tef_transacao 'pago' com vias_json trazendo cliente e estabelecimento. Antes de rodar, conferir em Configuração, passo Impressora, que as duas vias estão em "Imprimir sozinho".

### Passo 11. Venda por QRCode para PIX e Carteiras Digitais

**Situação:** pronto

**O que você faz:** Abre a venda, lanca R$ 500,00 (o sandbox da PayGo so aprovou sozinho esse valor: em 09/09/2026 oito Pix de R$ 9,99 morreram em 2 minutos com A283 e os dois de R$ 500,00 aprovaram em menos de 1 minuto), toca em PIX e confirma. O QR sai no pinpad (na tela do caixa so com tef_pgweb_qr_onde=tela). O cliente le com o aplicativo e a aprovacao chega sozinha. NAO toque em Cancelar depois de "Transacao autorizada".

**O que conferir:** A janela "Realize a leitura do QR code" parada, sem piscar, com o contador da biblioteca. Aprovacao automatica em ate 1 minuto, recibo impresso e a janela fechando sozinha. tef_transacao 'pago' com rede PIX C6 BANK, nsu = E2E do Pix (comeca com E19283746) e cod_controle = REQNUM. Anota o REQNUM que a tela mostra.

### Passo 12. Teste de comunicação bem-sucedido

**Situação:** pronto

**O que você faz:** Abre a Configuração com a senha de administrador, vai no passo Maquininha e toca em ADM. Na lista que a maquininha mostrar, escolhe Teste de comunicação.

**O que conferir:** Na tela: "✓ Operação administrativa concluída" seguido da frase que a rede mandou. Nada impresso, que é o esperado do passo. tef_transacao com situacao 'adm' (nunca 'pago', ProvedorPGWebLib.cs:551) e o CNF com o REQNUM na auditoria.

### Passo 13. Relatório sintético (opcional)

**Situação:** pronto

**O que você faz:** Mesmo caminho do teste de comunicação: Configuração, Maquininha, botão ADM, e na lista escolhe Relatório sintético.

**O que conferir:** Relatório saindo na impressora e "✓ Operação administrativa concluída" na tela. tef_transacao com situacao 'adm'. Guardar a folha impressa como evidência.

### Passo 14. Relatório detalhado (opcional)

**Situação:** pronto

**O que você faz:** Configuração, Maquininha, botão ADM, e na lista escolhe Relatório detalhado.

**O que conferir:** Relatório detalhado saindo na impressora e "✓ Operação administrativa concluída" na tela. tef_transacao 'adm'. Se o relatório for longo, conferir que saiu inteiro: as vias são quebradas por 0Dh em ProvedorPGWebLib.cs:1389-1395.

### Passo 15. Relatório resumido (opcional)

**Situação:** pronto

**O que você faz:** Configuração, Maquininha, botão ADM, e na lista escolhe Relatório resumido.

**O que conferir:** Relatório resumido saindo na impressora e "✓ Operação administrativa concluída" na tela. tef_transacao 'adm'.

### Passo 16. Operação cancelada no menu administrativo

**Situação:** consertado hoje

**O que você faz:** Abre a Configuração, vai na seção da maquininha (cartão "PayGo (biblioteca)") e toca em ADM. Quando a lista de operações do PayGo aparecer, aperta Esc sem escolher nada.

**O que conferir:** Nenhuma linha nova em tef_transacao (a administrativa só grava depois de aprovada, ProvedorPGWebLib.cs:549), e o texto de erro na tela. Hoje ele é "Operação administrativa não concluída: operação cancelada pelo operador" (Configuracao.xaml.cs:1703), e não a frase OPERAÇÃO CANCELADA que o roteiro cita como mensagem da biblioteca.

### Passo 17. Manutenção

**Situação:** pronto

**O que você faz:** Configuração, botão ADM, escolhe MANUTENÇÃO na lista da maquininha e responde o que ela pedir. Se pedir a senha do lojista, o campo aparece sem eco. Se perguntar "Apagar todos os arquivos?", responde Sim: e o esperado, e o passo 18 reinstala logo em seguida. Depois da manutencao o terminal fica sem tabelas (no log: Versao Param 0, NumReq 0), entao NENHUMA venda funciona ate o passo 18 terminar. Feito em 09/09/2026 as 19:09 (TRANSACAO OK).

**O que conferir:** Tela: "Operação administrativa concluída" (Configuracao.xaml.cs:1702). Sem recibo, e isso é o esperado: sem vias na resposta o hook de impressão devolve true sem imprimir (ProvedorPGWebLib.cs:1423). A prova de "transação confirmada para a automação" é a linha de auditoria do evento tef_pgweblib: "CNF pgweb-adm-... PWRET_OK (0) -> adm" (ProvedorPGWebLib.cs:1221). Vale exportar essa linha da…

### Passo 18. Reinstalação/Instalação

**Situação:** depende da PayGo

**O que você faz:** Configuração, seção da maquininha, toca em "Instalar ponto de captura". A biblioteca pergunta os dados na tela do caixa (menu INSTALACAO, senha tecnica, ponto de captura, CNPJ, endereco do host) e quem digita e o dono. Depois de responder a senha com Enter, espera a proxima pergunta abrir; em 09/09/2026 (0.8.6) o Windows derrubou a tela nesse instante (erro do WPF ao processar a tecla, erros.log) e a 0.8.7 abre a pergunta seguinte so depois de o teclado esvaziar.

**O que conferir:** Tela: "Ponto de captura instalado" (Configuracao.xaml.cs:1694) e recibo impresso pelas vias da resposta (ProvedorPGWebLib.cs:559 chama a impressão ANTES do CNF). Depois, tocar em "Testar a maquininha": tem que sair de "PayGo não instalado neste terminal" (ProvedorPGWebLib.cs:66 e 279) para "a biblioteca respondeu".

### Passo 19. Venda bem-sucedida #1, R$ 1,00 (opcional)

**Situação:** pronto

**O que você faz:** Na venda, toca 4 vezes em PRODUTO TESTE (R$ 0,25) até a comanda somar R$ 1,00, toca em Finalizar, escolhe Crédito, toca em "Cobrar na maquininha", escolhe C6PAY no menu de redes que a biblioteca abrir e o cliente passa o cartão.

**O que conferir:** Recibo: as duas vias saem sozinhas (a política nasce em automático, config tef_paygo_imprimir_vias=1, Pdv.Nucleo/Impressoes.cs:114), na EPSON L3150. Banco: tef_transacao com provedor pgweblib e situacao 'pago'. Auditoria (evento tef_pgweblib): a linha do PWRET_OK com CNFREQ e a linha "CNF ... -> pago". ATENÇÃO: esta máquina está com NFC-e em PRODUÇÃO (terminal.ambiente=1, modo_fiscal=nfce),…

### Passo 20. Venda bem-sucedida #2, R$ 2,00 (opcional)

**Situação:** pronto

**O que você faz:** Mesma coisa do passo 19, com 1 CAIXINHA EXTRA (R$ 2,00) na comanda, ou 8 toques em PRODUTO TESTE. Finaliza, Crédito, C6PAY, cartão.

**O que conferir:** Igual ao passo 19: recibo impresso, linha 'pago' em tef_transacao e o CNF na auditoria. Anote o NSU do comprovante, é por ele que o passo 23 escolhe esta venda na lista do estorno.

### Passo 21. Venda bem-sucedida #3, R$ 12.345,67

**Situação:** consertado hoje

**O que você faz:** Deveria ser: monta uma comanda de R$ 12.345,67, finaliza, Crédito, C6PAY, cartão. Hoje não tem como montar esse valor.

**O que conferir:** Quando der para cobrar: recibo impresso, linha 'pago' com valor_cent = 1234567 e o CNF na auditoria.

### Passo 22. Cancelamento bem-sucedido #1 (da venda de R$ 1,00) (opcional)

**Situação:** depende de voce

**O que você faz:** Na venda, com a comanda vazia, toca em "Cancelar / Imprimir", escolhe "Estornar", escolhe a venda pelo NSU do comprovante, escreve o motivo, confirma e digita o código de 6 dígitos do autenticador do dono. Depois passa o cartão na maquininha.

**O que conferir:** Recibo do cancelamento impresso antes do CNF (ProvedorPGWebLib.cs:491), linha nova 'estornado' e a original marcada 'estornada' (ProvedorPGWebLib.cs:511), e a venda sai do caixa no mesmo ato. A tela termina em "Estorno feito".

### Passo 23. Cancelamento bem-sucedido #2 (da venda de R$ 2,00)

**Situação:** depende de voce

**O que você faz:** Mesmo caminho do passo 22, escolhendo na lista o pagamento de R$ 2,00 pelo NSU, e com o código do autenticador do dono.

**O que conferir:** Recibo do cancelamento, linha 'estornado' em tef_transacao, original em 'estornada' e a venda cancelada no PDV. Auditoria: evento tef_estorno com o NSU e quem autorizou.

### Passo 24. Cancelamento bem-sucedido #3 (da venda de R$ 12.345,67) (opcional)

**Situação:** depende de voce

**O que você faz:** Mesmo caminho do passo 22, escolhendo o pagamento de R$ 12.345,67 pelo NSU, com o código do autenticador do dono.

**O que conferir:** Igual ao passo 23. Só existe se o passo 21 tiver acontecido.

### Passo 25. Cancelamento bem-sucedido #4, pelo menu administrativo (opcional)

**Situação:** pronto

**O que você faz:** Configuração, botão ADM, escolhe CANCELAMENTO na lista da maquininha e digita o que ela pedir (NSU, valor, data), lendo do comprovante da venda de R$ 100.000,00. Depois passa o cartão.

**O que conferir:** Recibo do cancelamento impresso (ProvedorPGWebLib.cs:559) e a tela mostrando "Operação administrativa concluída" mais a mensagem da rede (Configuracao.xaml.cs:1702, esse ramo mostra o RESULTMSG). Cuidado que vale registrar: cancelar por aqui NÃO mexe na venda do PDV; a linha em tef_transacao entra como 'adm' (ProvedorPGWebLib.cs:550) e a venda continua finalizada, então o fechamento do caixa vai…

### Passo 26. Queda de energia durante uma venda

**Situação:** pronto

**O que você faz:** Começa uma venda no cartão e, na hora em que o pinpad pede o cartão, corta a energia do computador de verdade: segura o botão de força por 5 segundos ou tira da tomada. Fechar o PDV pelo X não vale, porque aí o encerramento é limpo. Depois liga de novo e abre o PDV.

**O que conferir:** Nenhuma venda gravada (a venda só nasce no Finalizar) e nenhum pagamento. Em tef_transacao, a linha da cobrança interrompida fica 'orfa' com o motivo "PDV reiniciou durante a cobrança; confira no PayGo". Na auditoria, evento tef_pgweblib, a linha do religamento: se a biblioteca ainda segurava a transação, sai "pendência da biblioteca REQNUM ... desfeita (REV_PWR)" (ProvedorPGWebLib.cs:627). A…

### Passo 27. Queda de energia durante uma operação administrativa

**Situação:** pronto

**O que você faz:** Abre Configuração, ADM, escolhe CANCELAMENTO e, quando o pinpad pedir o cartão, corta a energia do mesmo jeito do passo 26. Liga de novo e abre o PDV.

**O que conferir:** Nenhuma linha nova em tef_transacao, porque a administrativa só grava depois de aprovada. A prova fica na auditoria, evento tef_pgweblib: "pendência da biblioteca REQNUM ... desfeita (REV_PWR)" no religamento, ou a mesma linha com o texto "pendência REQNUM ... antes de pgweb-..." se a próxima operação vier antes. E a venda original continua como estava, não estornada.

### Passo 28. Solicitação de dado genérico digitado 1, venda de R$ 1.001,00

**Situação:** consertado hoje

**O que você faz:** Deveria ser: monta uma comanda de R$ 1.001,00, finaliza, Crédito, escolhe C6PAY e espera a maquininha pedir um dado digitado. Hoje não tem como montar esse valor.

**O que conferir:** Que a caixa de texto aparece de verdade, com o texto que a maquininha mandou. E que o PDV NÃO mandou a tag sozinho antes: confira na auditoria a sequência de PW_iAddParam da venda, que hoje é só identidade, valor, moeda, tipo de cartão, financiamento, rede, pinpad e porta; a tag 0x2F (47) não está em nenhum lugar do código.

### Passo 29. Solicitação de dado genérico digitado 2, digitar ABC123

**Situação:** consertado hoje

**O que você faz:** Digita ABC123 na caixa que a maquininha pediu e confirma. A venda segue e tem que aprovar.

**O que conferir:** Recibo impresso e linha 'pago' em tef_transacao com o CNF na auditoria. A mensagem TRANSAÇÃO APROVADA, que o roteiro pede, NÃO aparece hoje: em venda aprovada o desfecho volta sem motivo e a tela de pagamento só registra o pagamento e segue.

### Passo 30. Solicitação de menu genérico 1, venda de R$ 1.002,00

**Situação:** consertado hoje

**O que você faz:** Deveria ser: comanda de R$ 1.002,00, finaliza, Crédito, C6PAY, e a maquininha abre um menu com duas opções. Hoje não tem como montar esse valor.

**O que conferir:** Que aparecem as duas opções, 123456 e ABCDEF, e que o texto SELECIONAR: está na tela. E que o PDV não mandou a tag antes: a tag 0x2F não existe no código.

### Passo 31. Solicitação de menu genérico 2, escolher ABCDEF

**Situação:** consertado hoje

**O que você faz:** Toca em ABCDEF. O roteiro diz que a maquininha pede o MESMO menu de novo, e é isso que tem que aparecer na tela outra vez.

**O que conferir:** Depois do conserto: o menu com 123456 e ABCDEF tem que reaparecer, com o prompt SELECIONAR:, e a venda tem que aprovar depois da segunda escolha.

### Passo 32. Venda com mensagem resultado no tamanho máximo, R$ 1.003,00

**Situação:** feito no caixa; a frase longa depende do sandbox

**O que você faz:** Abre o passo 32 no roteiro (Cobrar este valor), Crédito, C6PAY, cartão. A frase que a rede devolver aparece em verde no cabeçalho da tela de pagamento, inteira, quebrando linha se precisar (TxtRecadoTef em Pagamento.xaml), e fica gravada no campo 030 de tef_transacao.resposta_txt.

**O que conferir:** O roteiro espera a frase de 80 caracteres TRANSAÇÃO DE TESTE APROVADA. CÓDIGO AUTORIZAÇAO 13456789 TRANSACAO NAO PRODUTIVA. Em 09/09/2026 o simulador C6PAY do sandbox NÃO a mandou: nas duas vendas de R$ 1.003,00 (REQNUM 0000282973 às 20:17 e 0000282975 às 20:18) a biblioteca devolveu PWINFO_RESULTMSG = "Transação autorizada", e a frase longa não aparece em nenhum arquivo da PayGo (comms_260909.log). O caixa mostra o que recebe; o que falta é do lado do sandbox. Anotar o REQNUM 0000282973 na planilha e anexar o log: se a PayGo cobrar a frase, a resposta está no log dela.

### Passo 33. Transação pendente #1 (venda de R$ 1.005,50 no C6PAY)

**Situação:** pronto

**O que você faz:** Abre a venda, poe o produto de teste que soma R$ 1.005,50, escolhe Credito e aproxima o cartao no pinpad. A venda tem que sair aprovada e o comprovante sair na bobina.

**O que conferir:** Na tela: a parte de credito entra na lista de pagamentos da venda. Na bobina: as vias do cliente e do estabelecimento (politica padrao imprime sozinho, Impressoes.Politica devolve Automatico quando a chave nao diz 0). No banco C:\ProgramData\PdvNativo\pdv.db: linha em tef_transacao com provedor 'pgweblib', situacao 'pago' e cod_controle preenchido (esse cod_controle e o REQNUM, e e ele que o…

### Passo 34. Transação pendente #2 (nova venda de R$ 1.005,51, negada trazendo a pendente)

**Situação:** AINDA FALTA

**O que você faz:** Logo depois do passo 33, abre outra venda de R$ 1.005,51, escolhe Credito e passa o cartao. A venda volta negada, e o caixa tem que mandar sozinho a confirmação da transação do passo 33, sem imprimir nada.

**O que conferir:** Hoje o operador ve 'Pagamento não aprovado' com o texto da rede e 'O cliente NÃO foi cobrado' (Telas\Pagamento.xaml.cs:640-649), e NADA sai para a PayGo. O esperado do roteiro e: nenhum recibo impresso (isso ja esta certo, ImprimirSeguroAsync so roda no caminho aprovado), venda atual nao realizada (ja esta certo, situacao 'recusado'), e um PW_iConfirmation(PWCNF_CNF_AUTO) com o REQNUM, LOCREF,…

### Passo 35. Transação pendente não encontrada #1 (venda de R$ 1.005,60 no C6PAY)

**Situação:** pronto

**O que você faz:** Abre a venda de R$ 1.005,60, escolhe Credito e passa o cartao. Venda aprovada, comprovante impresso.

**O que conferir:** Mesma triade do passo 33: mensagem na tela, vias na bobina, linha 'pago' em tef_transacao com o cod_controle. Guardar esse REQNUM anotado: o passo 36 vai comparar com o que o C6PAY devolver.

### Passo 36. Transação pendente não encontrada #2 (venda de R$ 1.005,61, negada com pendente desconhecida)

**Situação:** AINDA FALTA

**O que você faz:** Abre outra venda, de R$ 1.005,61, e passa o cartao. Volta negada trazendo uma transação que este caixa nunca viu, e o PDV tem que mandar sozinho o desfazimento dela, sem imprimir nada.

**O que conferir:** O esperado e um PW_iConfirmation de DESFAZIMENTO com o REQNUM que veio da rede, na hora, sem recibo, e a venda atual como nao realizada. Conferir na auditoria (evento tef_pgweblib) e no log da biblioteca. Hoje nao sai nada ate a proxima operacao.

### Passo 37. Confirmação #1 (venda de R$ 1.012,00 no C6PAY e confirmação manual)

**Situação:** pronto

**O que você faz:** Abre o passo 37 no roteiro (Cobrar este valor), Crédito, C6PAY, cartão. Depois que a rede aprova e o comprovante sai, a tela pergunta "Rede aprovou: confirmar a venda ou desfazer?". Toque em Confirmar venda. Essa é a confirmação manual: o caixa manda PW_iConfirmation com PWCNF_CNF_MANU_AUT (12833) em vez do automático (289). Fora dos passos 37 a 40 a pergunta não existe e o caixa confirma sozinho. A venda tem que NASCER do roteiro: pelo caminho normal de loja não há pergunta. Esc nesse diálogo não decide nada: a pergunta volta até você tocar num dos dois botões.

**O que conferir:** Venda aprovada, vias impressas, REQNUM na tela, linha 'pago' em tef_transacao e a auditoria (tef_pgweblib) com 'CNF ... PWRET_OK -> pago (manual pelo operador, PWCNF_CNF_MANU_AUT 12833)'. A venda de R$ 1.012,00 feita em 09/09 às 20:29 saiu com 289, o automático, porque a pergunta ainda não existia: refazer no caixa 0.8.9 ou mais novo.

### Passo 38. Confirmação #2 (venda de R$ 10,00 na REDE e confirmação manual) (opcional)

**Situação:** pronto

**O que você faz:** O mesmo do passo 37, com R$ 10,00, escolhendo REDE no menu de redes que a biblioteca abre na venda (ou mudando a rede na Configuração). No fim, Confirmar venda.

**O que conferir:** Mesma coisa do 37, com AUTHSYST igual a REDE na resposta gravada (campo 010-000 do resposta_txt) e o 12833 na auditoria.

### Passo 39. Desfazimento manual #1 (venda de R$ 1.011,00 no C6PAY e desfazimento manual)

**Situação:** pronto

**O que você faz:** Abre o passo 39 no roteiro (Cobrar este valor), Crédito, C6PAY, cartão. Depois que a rede aprova e o comprovante sai, a tela pergunta "Rede aprovou: confirmar a venda ou desfazer?". Toque em Desfazer venda. O caixa manda PW_iConfirmation com PWCNF_REV_MANU_AUT (12849): a transação é desfeita na rede, o cliente não é cobrado e a venda volta para as formas de pagamento (aí é Cancelar venda). ATENÇÃO: estornar depois, em TEF e Estornar, NÃO é desfazimento, é cancelamento (CNC), e vale para os passos 43 a 46. Em 09/09 às 20:33 o que saiu para a venda de R$ 1.011,00 foi um estorno; refazer o passo no caixa 0.8.9 ou mais novo.

**O que conferir:** Tela 'Cobrança cancelada' com 'venda desfeita pelo operador' e 'A cobrança foi desfeita: o cliente não pagou nada', mais o REQNUM. Linha 'desfeita' em tef_transacao e a auditoria com 'desfeita pelo operador (PWCNF_REV_MANU_AUT 12849) REQNUM ...'. No roteiro o passo fica com ✓ e a etiqueta 'desfeito · REQNUM' (é o resultado esperado aqui e no 40; nos outros passos 'desfeito' conta como engano).

### Passo 40. Desfazimento manual #2 (venda de R$ 333,00 na REDE e desfazimento manual) (opcional)

**Situação:** pronto

**O que você faz:** O mesmo do passo 39, com R$ 333,00, escolhendo REDE no menu de redes. No fim, Desfazer venda.

**O que conferir:** Mesma coisa do 39: linha 'desfeita' em tef_transacao, 12849 na auditoria, AUTHSYST igual a REDE, e no roteiro ✓ com a etiqueta 'desfeito · REQNUM'.

### Passo 41. Desfazimento por falha na liberação da mercadoria #1 (venda de R$ 1.013,00)

**Situação:** duvida

**O que você faz:** Confere que a mercadoria nao tem no estoque, faz a venda de R$ 1.013,00 e ela e aprovada. O passo continua no 42.

**O que conferir:** Se a PayGo mantiver o passo: venda aprovada e recibo impresso, igual aos passos 33 e 35.

### Passo 42. Desfazimento por falha na liberação da mercadoria #2

**Situação:** duvida

**O que você faz:** Depois da venda do passo 41, o PDV teria que mandar sozinho o desfazimento dizendo que a mercadoria nao foi liberada. Hoje o operador nao tem por onde fazer isso.

**O que conferir:** Se o passo for exigido: um PW_iConfirmation com 143665 sobre o REQNUM da venda do 41, transação considerada desfeita e a venda nao realizada para o caixa.

### Passo 43. Cancelamento aprovado solicitando Referência Local #1 (venda de R$ 1.017,00 no C6PAY)

**Situação:** pronto

**O que você faz:** Abre a venda de R$ 1.017,00, escolhe Credito, passa o cartao e conclui a venda. Precisa CONCLUIR a venda, nao so aprovar o cartao: o cancelamento do passo 44 so enxerga venda finalizada.

**O que conferir:** Venda aprovada, vias impressas, linha 'pago' em tef_transacao com 027 (REQNUM) e 012 (NSU) no resposta_txt, e a venda com status 'finalizada'. Anotar o NSU do comprovante: e por ele que o operador escolhe a venda no passo 44.

### Passo 44. Cancelamento aprovado solicitando Referência Local #2

**Situação:** pronto

**O que você faz:** No menu Cancelar / Imprimir escolhe Estornar, acha a venda do passo 43 pelo NSU do comprovante, escreve o motivo, confirma, e o dono digita o codigo do autenticador. A biblioteca pede a Referência Local e o PDV responde com o codigo de controle guardado da venda.

**O que conferir:** Cancelamento aprovado, vias do cancelamento impressas, a tela dizendo que o valor voltou para o cliente, a linha nova em tef_transacao com situacao 'estornado' e a original virando 'estornada'. Se a biblioteca abrir um dialogo pedindo um dado, anotar QUAL identificador ela pediu: e essa a informacao que decide se o conserto abaixo e necessario.

### Passo 45. Cancelamento aprovado solicitando Referência Externa #1 (venda de R$ 1.018,00 na REDE)

**Situação:** pronto

**O que você faz:** Troca o autorizador para REDE, abre a venda de R$ 1.018,00, passa o cartao e conclui a venda.

**O que conferir:** Venda aprovada, vias impressas, e o campo 010-000 do resposta_txt gravado como REDE. Anotar o NSU.

### Passo 46. Cancelamento aprovado solicitando Referência Externa #2

**Situação:** pronto

**O que você faz:** Cancelar / Imprimir, Estornar, acha a venda de R$ 1.018,00 pelo NSU, escreve o motivo, confirma e o dono digita o codigo do autenticador. A Referência Externa e o proprio NSU, que o PDV ja manda.

**O que conferir:** Cancelamento aprovado, vias impressas, situacao 'estornado' na linha nova e 'estornada' na original. Se a biblioteca abrir dialogo pedindo a referência, o valor esta no comprovante do cliente (NSU) e tambem no rotulo da propria lista de estorno, que ja mostra 'NSU nnnn'.

### Passo 47. Venda contactless aprovada (R$ 1.020,00, autorizador C6PAY)

**Situação:** depende da PayGo

**O que você faz:** Abre a venda, monta uma comanda que soma exatamente R$ 1.020,00, toca Finalizar, escolhe Credito, digita 102000 no teclado, confirma, e quando o pinpad pedir aproxima o cartao sem contato.

**O que conferir:** No pinpad: "APROXIME, INSIRA OU PASSE O CARTAO". Na tela do caixa: primeiro "Aguardando o cliente / Aproxime, insira ou passe o cartao na maquininha (R$ 1.020,00)" e depois as mensagens que a biblioteca manda por PWRET_DISPLAY, que substituem a linha de detalhe. No fim, a tela de pagamento marca a parte como paga. Duas vias saem sozinhas na EPSON L3150 (tef_paygo_imprimir_vias=1 na base local,…

### Passo 48. Venda contactless aprovada sem senha (R$ 999,00, C6PAY)

**Situação:** depende da PayGo

**O que você faz:** Mesma coisa do passo anterior, com a comanda somando R$ 999,00: escolhe Credito, digita 99900, confirma e aproxima o cartao sem contato. Nao pode aparecer pedido de senha em lugar nenhum.

**O que conferir:** Que em nenhum momento o pinpad pede senha e que a tela do caixa nao mostra recado de senha. Depois: "Transacao aprovada" no comprovante, duas vias impressas, e a linha de tef_transacao com situacao pago. Para a evidencia, a auditoria do provedor (Caixa.Auditar com acao tef_pgweblib, Servicos.cs:318-325) grava a linha "pgweblib: pgweb-... PWRET_OK; AUTRESPCODE=...; CNFREQ=..." que serve de log do…

### Passo 49. Consulta de terminais

**Situação:** pronto

**O que você faz:** Nao se aplica a esta homologacao: o operador nao faz nada. Marcar N/A na planilha.

**O que conferir:** Nada a conferir nesta homologacao. Marcar N/A na planilha, na linha do passo 49.

### Passo 50. Consultar status de uma transacao #1 (a transacao do passo 2)

**Situação:** pronto

**O que você faz:** Nao se aplica a esta homologacao: o operador nao faz nada. Marcar N/A na planilha.

**O que conferir:** Nada a conferir nesta homologacao. Marcar N/A na planilha.

### Passo 51. Consultar status de uma transacao #2 (a transacao do passo 6)

**Situação:** pronto

**O que você faz:** Nao se aplica a esta homologacao: o operador nao faz nada. Marcar N/A na planilha.

**O que conferir:** Nada a conferir nesta homologacao. Marcar N/A na planilha.

### Passo 52. Cadastro de URL de Callback (Callback/Insert e Callback/GetRegistered)

**Situação:** AINDA FALTA

**O que você faz:** Nao se aplica a esta homologacao: o operador nao faz nada. Marcar N/A na planilha.

**O que conferir:** Nada a conferir nesta homologacao. Marcar N/A na planilha.

### Passo 53. Callback de venda

**Situação:** AINDA FALTA

**O que você faz:** Nao se aplica a esta homologacao: o operador nao faz nada. Marcar N/A na planilha.

**O que conferir:** Nada a conferir nesta homologacao. Marcar N/A na planilha.

### Passo 54. Queda de energia apos a aprovacao, antes da confirmacao

**Situação:** pronto

**O que você faz:** Faz uma venda normal no cartao. No segundo em que o pinpad mostra aprovado e o comprovante comeca a sair, desliga o caixa na tomada (ou segura o botao de forca). Liga de novo, abre o PDV e nao mexe em mais nada: o proprio PDV desfaz a transacao sozinha.

**O que conferir:** Depois de religar: na tabela tef_transacao a linha do pgweb- tem que estar em situacao desfeita (nunca pago e nunca orfa). Na tabela auditoria, acao tef_pgweblib, tem que aparecer a linha "pgweblib: pendencia da biblioteca REQNUM ... desfeita (REV_PWR)" ou o registro do Desfazer. Na tela: a venda nao existe, nenhum numero de nota foi queimado, e o cliente nao foi cobrado. No relatorio do PayGo a…

### Passo 55. Operacao cancelada durante venda PIX (Esc na tela do QRCode)

**Situação:** pronto (consertado em 09/09/2026 à noite, caixa 0.8.10)

**O que você faz:** Abre o passo 55 no roteiro, escolhe PIX na tela de pagamento e confirma o valor. Com o QR na maquininha (ou na tela do caixa, se tef_pgweb_qr_onde=tela), aperta Esc ou toca em Cancelar cobrança.

**O que conferir:** Em até 5 segundos a tela mostra "Cobrança cancelada", "cobrança cancelada pelo operador", "A cobrança foi desfeita: o cliente não pagou nada" e o REQNUM. A janela do QR fecha sozinha. A linha em tef_transacao fica 'desfeita' e a auditoria mostra o REV; nenhuma parte entra na venda. Antes do conserto (REQNUM 283068, 283108 e 283151, das 20:56 às 20:58) o cancelamento levava de 20 a 40 segundos, porque a biblioteca seguia consultando o host depois do PW_iPPAbort e cada pedido de exibição dela reiniciava o relógio do caixa, e terminava como ERRO ("pinpad devolveu PWRET_TRNNOTINIT"): refazer o passo no caixa 0.8.10 ou mais novo. A frase "OPERAÇÃO CANCELADA" que o roteiro cita é a da biblioteca quando ela mesma encerra; aqui quem encerra é o caixa, e a evidência é a linha desfeita e o REV no log da PayGo.

### Passo 56. Venda por QRCode para PIX e carteiras digitais (R$ 500,00, PIX C6 BANK)

**Situação:** pronto

**O que você faz:** Abre a venda, monta uma comanda de R$ 500,00 (por exemplo 3 itens de R$ 153,00 mais 2 de R$ 20,50), toca Finalizar, escolhe PIX, digita 50000 e confirma. O QR sai no pinpad; espera a aprovacao chegar sozinha (menos de 1 minuto no sandbox). NAO toque em Cancelar depois de "Transacao autorizada": em 09/09/2026 isso desfez um Pix pago (REQNUM 280555).

**O que conferir:** O QR aparece no pinpad e o caixa mostra "Realize a leitura do QR code" sem piscar. Depois da aprovacao a janela fecha sozinha, a tela vira paga e as duas vias saem na impressora. Na base: tef_transacao situacao pago, nsu = E2E do Pix (Pix pelo TEF vem sem codigo de autorizacao, so com NSU, e o PDV ja usa o NSU como carimbo, Telas/Pagamento.xaml.cs:600-605), cod_controle = REQNUM. Na auditoria tem que aparecer a linha "pgweblib: CNF ... -> pago".

### Passo 57. Cancelamento PIX (tem que ser negado pelo host)

**Situação:** feito em 09/09/2026 às 21:05 (REQNUM 0000283283; a rede respondeu "TRANSACAO CANCELADA")

**O que você faz:** Com a venda de Pix do passo anterior ja finalizada e a comanda vazia, toca no botao Cancelar da tela de venda, escolhe Estornar, escolhe a linha do Pix pelo NSU do comprovante, escreve o motivo, confirma, e entao digita o codigo do autenticador do dono. So depois disso o PDV manda o cancelamento para a maquininha, e a rede nega. O REQNUM do estorno negado aparece no aviso a partir do caixa 0.8.10 (antes ele so existia no log).

**O que conferir:** Na tela: titulo "Estorno negado" e o texto "A maquininha nao aprovou o estorno: <mensagem da rede>. O dinheiro nao voltou para o cliente. Tente de novo." A mensagem da rede e o PWINFO_RESULTMSG cru, entao e ali que deve aparecer "TRANSACAO NEGADA PELO HOST". Na base: NENHUMA linha nova em situacao estornado, a venda de Pix continua finalizada e a linha original continua em pago (nao pode virar…

### Passo 58. Venda de R$ 77,00 com impressao do comprovante grafico

**Situação:** pronto

**O que você faz:** Nao se aplica a esta homologacao: o roteiro so libera este passo para C6Pay Android. Marcar N/A na planilha.

**O que conferir:** Nada a conferir nesta homologacao. Marcar N/A na planilha, na linha do passo 58.

