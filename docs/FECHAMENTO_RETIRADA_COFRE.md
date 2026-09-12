# Retirada de fechamento para o cofre (1.0.10, 12/09/2026)

Pedido do dono: "após o fechamento do dinheiro, anotar quanto está sendo retirado e
quanto está ficando no caixa. Essa retirada imprime um papel com nome do operador,
valor e data, que junto do dinheiro retirado vai para o cofre para conferência depois."

## Como fica o fechamento

1. O operador conta a gaveta (como sempre: dinheiro, e as outras formas quando o
   caixa não tem TEF).
2. Pergunta nova: **"Você contou R$ X em dinheiro. Quanto fica na gaveta para o troco
   de amanhã? O resto vai para o cofre."** Tem **Voltar** (reabre a contagem do
   dinheiro). Deixar mais do que contou é recusado na hora, com os dois valores.
3. O caixa fecha. Na mesma gravação:
   - a **retirada** (contado menos o que ficou) vira uma **sangria com destino
     "cofre"** e motivo "Retirada de fechamento", assinada por quem fechou;
   - a sessão guarda **o que ficou** e **o que saiu** (`fica_cent`, `retirada_cent`);
   - a auditoria registra `caixa_retirada_cofre`;
   - a sangria entra na fila para o painel (`pdv_caixa_movimentos`).
4. Sai **um papel** na impressora do cupom, na bobina configurada (58 ou 80 mm):
   loja, data e hora, turno, operador, contado, o que ficou, **RETIRADO**, linha para
   assinatura e "Conferido por ___ em __/__/____". Vai para o cofre junto com o
   dinheiro. Se a impressora falhar, o resumo do fechamento manda anotar à mão
   (operador, valor e data) e diz o motivo.
5. O resumo do fechamento mostra "Retirado para o cofre: R$ X. Fica no caixa: R$ Y."

## O dia seguinte

A abertura passa a esperar na gaveta **o que ficou**, não o que foi contado.
Fechou contando R$ 521,00 e deixou R$ 200,00: amanhã a conferência do fundo espera
R$ 200,00. Fechamento anterior a esta versão (sem a pergunta) continua esperando o
declarado, como sempre foi.

## Por que a retirada não pede o PIN do supervisor

A sangria do meio do turno exige dupla assinatura porque sai dinheiro sem
conferência. A retirada de fechamento nasce DEPOIS da contagem às cegas, com a
diferença já auditada, o papel impresso vai para o cofre e a abertura de amanhã
confere o que ficou. E ela nunca pode passar do contado (regra no Núcleo, não na
tela). Por isso ela é registrada direto em `Caixa.Fechar`, na mesma transação.

## Por dentro

- `Pdv.Nucleo/RetiradaCofre.cs`: `Calcular`, `Pergunta`, `Papel` (puros).
- `Caixa.Fechar(..., ficaNoCaixa)`, `Caixa.FundoEsperado` (COALESCE em `fica_cent`),
  `Caixa.UltimaRetirada`.
- `Telas/Venda.xaml.cs`: a pergunta no `FecharCaixa`, `ImprimirRetiradaAsync`, o
  resumo em `MostrarResultado`.
- Testes: `Pdv.Testes/TestesRetiradaCofre.cs` (conta, papel em 32 e 48 colunas, e o
  registro num banco de verdade).

## Painel

A retirada chega ao painel como sangria com `destino = 'cofre'` em
`pdv_caixa_movimentos`. Atenção: até 12/09 essa tabela estava vazia porque o caixa
não tinha permissão de leitura nela (migration
`20260912170000_pdv_caixa_movimentos_select_terminal.sql` do ERP corrige). Depois
de aplicada, o caixa reenvia o que ficou preso ao tocar em **Atualizar**.
