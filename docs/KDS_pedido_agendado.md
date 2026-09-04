# KDS: pedido agendado do iFood

Relato do dono (04/09/2026): "pedido 5592 é um pedido agendado, porém não mostra no
KDS do PDV. Tem como mostrar com um box e tag diferente? Cor e aviso de agendado
pra 10h."

## De onde vem

A RPC `pdv_kds_pedidos` do ERP (migration `20260904130000_kds_pedido_agendado.sql`)
devolve três campos a mais por pedido: `agendado` (bool), `agendado_para` e
`agendado_ate` (timestamptz). Quem preenche no ERP é a edge `ifood-order-enrich`,
lendo `orderTiming = SCHEDULED` e `schedule.deliveryDateTimeStart/End` no order
details do iFood. A RPC devolve o agendado desde que a nuvem sabe dele até 45 min
depois do fim da faixa, se a hora cai até as 04:00 do dia seguinte (dia operacional).

RPC antiga (sem os campos) = imediato, como sempre foi. O parser está em
`Nuvem.LerFeedKds`.

## No caixa

- `kds_ticket` ganhou `agendado`, `agendado_para`, `agendado_ate` (hora local).
- Coluna NA FILA: faixa "AGENDADOS" no topo, ordenada pela hora marcada, e faixa
  "AGORA" com a fila de chegada. Sem agendado no quadro, nada muda (`Kds.OrdenarFila`).
- Card: box roxo (fundo `AgendadoFundo`, borda `Agendado`, nos dois temas), tag
  "AGENDADO" ao lado da origem, linha "Agendado para 10:00" (ou "10:00 a 10:30";
  com a data quando não é hoje). O relógio do card mostra a hora marcada; na última
  hora vira "em N min" (amarelo); passou da hora sem ficar pronto vira "+N min"
  (vermelho).
- Expiração: o agendado em A PREPARAR expira 4 h depois do FIM DA FAIXA, não da
  chegada (senão o pedido das 18:00 que entrou de manhã sumia às 13:00).
- Comanda: linha "AGENDADO para 10:00 a 10:30" ampliada, logo abaixo do número.

## Comanda automática do agendado

Não imprime na chegada: a loja não vai montar agora e o papel se perde. Sai sozinha
quando faltar X minutos para a hora marcada (`Kds.ComandaPodeSair`), com X na config
`kds_comanda_agendado_min` (padrão 30; teto 12 h; lixo = 30). O timer de 10 s do
quadro e o de 60 s do caixa reavaliam a cada puxada, então o papel sai no primeiro
ciclo depois do limiar. O botão da comanda no card imprime a qualquer momento (não
passa pela regra). O claim `impresso_em` só é feito quando a comanda sai de fato.
A política de impressão da comanda (imprimir sozinho / perguntar / não imprimir) e
o portão de atualização "comanda aberta" não mudaram.

Testes: `Pdv.Testes/TestesKds.cs` (bloco "PEDIDO AGENDADO") e `TestesTema.cs`
(contraste do roxo nos dois temas).
