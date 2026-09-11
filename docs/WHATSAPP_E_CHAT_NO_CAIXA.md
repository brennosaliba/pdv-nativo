# WhatsApp e chat do iFood dentro do caixa (v86, 11/09/2026)

## O WhatsApp da loja

O botão **WhatsApp** da barra abre o WhatsApp Web dentro do próprio caixa. O QR é lido
uma vez com o celular da loja e o login fica guardado em
`C:\ProgramData\PdvNativo\webview-whatsapp` (sobrevive a reinício e a atualização).

### O toque de mensagem nova

Quem toca é o **próprio WhatsApp Web** (o toque original), mesmo com o operador na tela
de venda. O caixa só toca um som de reserva se a página ficar calada.

Para trocar o som de reserva: deixe um arquivo `whatsapp.wav` em
`C:\ProgramData\PdvNativo\sons\`. Vale na hora, sem atualizar o programa.

### "Desloga sem motivo": a vigia do QR

O caixa lê o estado da página a cada 5 segundos. Se a tela do QR aparecer num caixa que
já esteve conectado e ficar 60 segundos assim:

* o botão WhatsApp ganha um selo vermelho **QR**;
* aparece um aviso fixo na venda: "WhatsApp pediu o QR de novo. Toque para ler com o
  celular da loja" (com a hora da queda);
* toca um som próprio (três notas descendo), sem som se houver comanda aberta;
* repete a cada 15 minutos enquanto continuar caído. **Depois** cala por 2 horas. Depois
  de 7 dias caído o som para e só o selo fica.

Assim que a sessão volta, o aviso some sozinho.

Loja que nunca leu o QR no caixa não recebe aviso nenhum.

### Por que o QR cai (o que a loja pode fazer)

1. **"Manter conectado" desmarcado na hora de ler o QR.** A sessão vale só até o caixa
   fechar, e o QR volta toda manhã. O caixa agora marca essa caixinha sozinho na tela do
   QR, e o cabeçalho da aba lembra de conferir.
2. **Celular da loja mais de 14 dias sem abrir o WhatsApp com internet.** Regra do
   WhatsApp: os aparelhos vinculados caem. Alguém abre o WhatsApp no celular da loja
   pelo menos uma vez por semana.
3. **Limite de 4 aparelhos vinculados.** O quinto derruba o mais antigo. No celular:
   Aparelhos conectados, e sair dos que não são mais usados. A entrada do caixa aparece
   como "Windows".
4. **Alguém tocou em Sair no celular**, ou trocou de celular, ou mudou o número.
5. **Pasta do perfil apagada** por limpeza, antivírus ou reinstalação "do zero". Excluir
   `C:\ProgramData\PdvNativo` da limpeza do antivírus.

Diagnóstico: `C:\ProgramData\PdvNativo\whatsapp-diagnostico.txt` registra a hora em
que a página abriu, o estado da sessão e se a página tocou som. Nunca guarda mensagem.

## O chat do iFood

### Som de mensagem nova

Toca sem precisar abrir a tela do chat. Para usar um som próprio (por exemplo o do ICQ,
se a loja tiver o arquivo): `C:\ProgramData\PdvNativo\sons\ifood-chat.wav`. O caixa não
traz sons de terceiros embutidos; o arquivo é da loja.

O pedido novo do iFood aceita `pedido.wav` na mesma pasta.

Os arquivos precisam ser WAV (PCM). Sem o arquivo, vale o som próprio do caixa.

### Respostas prontas

No espaço vazio à esquerda da lista de conversas aparecem cartões com mensagens prontas
("Pedido revirado", "Atraso na entrega", "Item faltando", "Pedido saiu",
"Agradecimento"). Um toque copia o texto e, quando há uma conversa aberta, cola direto
na caixa de mensagem: é só enviar.

Para editar: Configuração (senha do administrador), passo do pareamento, bloco
**Respostas prontas do chat do iFood**. Um bloco por resposta, separados por uma linha
em branco; a primeira linha é o título do cartão. Até 8 respostas. Apagar tudo volta às
cinco de fábrica. Depois de salvar, toque em **Recarregar** na tela do chat.
