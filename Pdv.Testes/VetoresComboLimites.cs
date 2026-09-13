namespace Pdv.Testes;

/// <summary>
/// A tabela de casos do COMBO POR TOTAL (regra do dono 13/09/2026). COPIA gerada de
/// erp-american-day/src/lib/comboLimites.vetores.json: a mesma que o vitest do ERP e a
/// suite do banco (testes/db/243_combo_limite_total.sql) rodam. Nao editar a mao.
/// </summary>
internal static class VetoresComboLimites
{
    public const string Json = """
{
  "versao": 2,
  "unidade": { "singular": "donut", "plural": "donuts" },
  "produtos": {
    "acucar": { "nome": "Donut Açúcar", "categorias": ["classicos"] },
    "homer": { "nome": "Homer", "categorias": ["classicos"] },
    "pistache": { "nome": "Donut Pistache", "categorias": ["premium"] },
    "nutella": { "nome": "Donut Nutella", "categorias": ["premium"] },
    "ferrero": { "nome": "Donut Ferrero", "categorias": ["super"] },
    "brownie": { "nome": "Brownie", "categorias": ["outros"] }
  },
  "combos": {
    "livre4": {
      "total": { "min": 4, "max": 4 },
      "grupos": [
        { "tipo": "categoria", "id": "classicos", "nome": "Clássicos", "maximo": 4 },
        { "tipo": "categoria", "id": "premium", "nome": "Premium", "maximo": 4 },
        { "tipo": "categoria", "id": "super", "nome": "Super Premium", "maximo": 4 }
      ]
    },
    "box22": {
      "total": { "min": 4, "max": 4 },
      "grupos": [
        { "tipo": "categoria", "id": "premium", "nome": "Premium", "maximo": 2 },
        { "tipo": "produto", "id": "homer", "nome": "Homer", "maximo": 2 }
      ]
    },
    "homer1": {
      "total": { "min": 4, "max": 4 },
      "grupos": [
        { "tipo": "categoria", "id": "classicos", "nome": "Clássicos", "maximo": 4 },
        { "tipo": "produto", "id": "homer", "nome": "Homer", "maximo": 1 }
      ]
    },
    "teto5": {
      "total": { "min": 4, "max": 4 },
      "grupos": [
        { "tipo": "categoria", "id": "classicos", "nome": "Clássicos", "maximo": 5 },
        { "tipo": "categoria", "id": "premium", "nome": "Premium", "maximo": 4 }
      ]
    },
    "resto": {
      "total": { "min": 4, "max": 4 },
      "grupos": [
        { "tipo": "categoria", "id": "premium", "nome": "Premium", "maximo": 2 },
        { "tipo": "todos", "id": null, "nome": "Outros", "maximo": 4 }
      ]
    },
    "antigo_fixo": {
      "total": null,
      "grupos": [
        { "tipo": "categoria", "id": "premium", "nome": "Premium", "minimo": 2, "maximo": 2 },
        { "tipo": "categoria", "id": "classicos", "nome": "Clássicos", "minimo": 2, "maximo": 2 }
      ]
    },
    "antigo_fixo_convertido": {
      "total": { "min": 4, "max": 4 },
      "grupos": [
        { "tipo": "categoria", "id": "premium", "nome": "Premium", "minimo": 2, "maximo": 2 },
        { "tipo": "categoria", "id": "classicos", "nome": "Clássicos", "minimo": 2, "maximo": 2 }
      ]
    },
    "antigo_faixa": {
      "total": null,
      "grupos": [
        { "tipo": "categoria", "id": "premium", "nome": "Premium", "minimo": 1, "maximo": 3 },
        { "tipo": "categoria", "id": "classicos", "nome": "Clássicos", "minimo": 1, "maximo": 3 }
      ]
    },
    "antigo_faixa_convertido": {
      "total": { "min": 2, "max": 6 },
      "grupos": [
        { "tipo": "categoria", "id": "premium", "nome": "Premium", "minimo": 1, "maximo": 3 },
        { "tipo": "categoria", "id": "classicos", "nome": "Clássicos", "minimo": 1, "maximo": 3 }
      ]
    },
    "qualquer4": {
      "total": { "min": 4, "max": 4 },
      "grupos": [
        { "tipo": "categoria", "categorias": ["classicos", "premium", "super"], "avulsos": [], "nome": "Donuts", "maximo": 4 }
      ]
    },
    "qualquer4_como_lista": {
      "total": { "min": 4, "max": 4 },
      "grupos": [
        { "tipo": "produto", "ids": ["acucar", "homer", "pistache", "nutella", "ferrero"], "nome": "Donuts", "maximo": 4 }
      ]
    },
    "dois_e_dois": {
      "total": { "min": 4, "max": 4 },
      "grupos": [
        { "tipo": "categoria", "id": "premium", "nome": "Premium", "maximo": 2 },
        { "tipo": "categoria", "id": "classicos", "nome": "Clássicos", "maximo": 2 }
      ]
    },
    "misto": {
      "total": { "min": 4, "max": 4 },
      "grupos": [
        { "tipo": "categoria", "categorias": ["premium", "super"], "avulsos": ["homer"], "nome": "Especiais", "maximo": 2 },
        { "tipo": "categoria", "id": "classicos", "nome": "Clássicos", "maximo": 4 }
      ]
    }
  },
  "casos": [
    { "id": "L1", "combo": "livre4", "nome": "4 em todas: 3 classicos e 1 premium", "escolhas": [{ "produto": "acucar", "qtd": 3 }, { "produto": "pistache", "qtd": 1 }], "ok": true, "codigo": null, "erro": null },
    { "id": "L2", "combo": "livre4", "nome": "4 em todas: 4 super premium", "escolhas": [{ "produto": "ferrero", "qtd": 4 }], "ok": true, "codigo": null, "erro": null },
    { "id": "L3", "combo": "livre4", "nome": "4 em todas: 2 super premium e 2 classicos", "escolhas": [{ "produto": "ferrero", "qtd": 2 }, { "produto": "acucar", "qtd": 2 }], "ok": true, "codigo": null, "erro": null },
    { "id": "L4", "combo": "livre4", "nome": "4 em todas: 3 no total e recusado", "escolhas": [{ "produto": "acucar", "qtd": 2 }, { "produto": "homer", "qtd": 1 }], "ok": false, "codigo": "total_abaixo", "erro": "Falta 1 donut" },
    { "id": "L5", "combo": "livre4", "nome": "4 em todas: 2 no total", "escolhas": [{ "produto": "pistache", "qtd": 1 }, { "produto": "acucar", "qtd": 1 }], "ok": false, "codigo": "total_abaixo", "erro": "Faltam 2 donuts" },
    { "id": "L6", "combo": "livre4", "nome": "4 em todas: 5 no total e recusado", "escolhas": [{ "produto": "acucar", "qtd": 3 }, { "produto": "pistache", "qtd": 2 }], "ok": false, "codigo": "total_acima", "erro": "No máximo 4 donuts" },
    { "id": "L7", "combo": "livre4", "nome": "4 em todas: 5 do mesmo grupo fala do total", "escolhas": [{ "produto": "ferrero", "qtd": 5 }], "ok": false, "codigo": "total_acima", "erro": "No máximo 4 donuts" },
    { "id": "L8", "combo": "livre4", "nome": "4 em todas: vazio", "escolhas": [], "ok": false, "codigo": "total_abaixo", "erro": "Faltam 4 donuts" },
    { "id": "B1", "combo": "box22", "nome": "box 2 premium e 2 homer: 2 e 2 aceito", "escolhas": [{ "produto": "pistache", "qtd": 2 }, { "produto": "homer", "qtd": 2 }], "ok": true, "codigo": null, "erro": null },
    { "id": "B2", "combo": "box22", "nome": "box 2 premium e 2 homer: 1 e 3 recusado", "escolhas": [{ "produto": "nutella", "qtd": 1 }, { "produto": "homer", "qtd": 3 }], "ok": false, "codigo": "grupo_acima", "erro": "No máximo 2 Homer" },
    { "id": "B3", "combo": "box22", "nome": "box 2 premium e 2 homer: 3 e 1 recusado", "escolhas": [{ "produto": "pistache", "qtd": 3 }, { "produto": "homer", "qtd": 1 }], "ok": false, "codigo": "grupo_acima", "erro": "No máximo 2 Premium" },
    { "id": "B4", "combo": "box22", "nome": "box 2 premium e 2 homer: sem homer", "escolhas": [{ "produto": "pistache", "qtd": 1 }, { "produto": "nutella", "qtd": 1 }], "ok": false, "codigo": "grupo_abaixo", "erro": "Escolha 2 Homer" },
    { "id": "B5", "combo": "box22", "nome": "box 2 premium e 2 homer: 1 homer", "escolhas": [{ "produto": "pistache", "qtd": 2 }, { "produto": "homer", "qtd": 1 }], "ok": false, "codigo": "grupo_abaixo", "erro": "Escolha mais 1 Homer" },
    { "id": "B6", "combo": "box22", "nome": "box 2 premium e 2 homer: classico que nao e homer fica fora", "escolhas": [{ "produto": "pistache", "qtd": 2 }, { "produto": "acucar", "qtd": 2 }], "ok": false, "codigo": "fora_do_combo", "erro": "Donut Açúcar não faz parte do combo" },
    { "id": "B7", "combo": "box22", "nome": "box 2 premium e 2 homer: 4 premium", "escolhas": [{ "produto": "pistache", "qtd": 4 }], "ok": false, "codigo": "grupo_acima", "erro": "No máximo 2 Premium" },
    { "id": "P1", "combo": "homer1", "nome": "limite por produto: 1 homer e 3 classicos", "escolhas": [{ "produto": "homer", "qtd": 1 }, { "produto": "acucar", "qtd": 3 }], "ok": true, "codigo": null, "erro": null },
    { "id": "P2", "combo": "homer1", "nome": "limite por produto: 2 homer passa do teto do produto", "escolhas": [{ "produto": "homer", "qtd": 2 }, { "produto": "acucar", "qtd": 2 }], "ok": false, "codigo": "grupo_acima", "erro": "No máximo 1 Homer" },
    { "id": "P3", "combo": "homer1", "nome": "limite por produto: nenhum homer", "escolhas": [{ "produto": "acucar", "qtd": 4 }], "ok": true, "codigo": null, "erro": null },
    { "id": "P4", "combo": "homer1", "nome": "limite por produto: homer cheio, o que falta so cabe nos classicos", "escolhas": [{ "produto": "homer", "qtd": 1 }, { "produto": "acucar", "qtd": 2 }], "ok": false, "codigo": "grupo_abaixo", "erro": "Escolha mais 1 Clássicos" },
    { "id": "P6", "combo": "homer1", "nome": "limite por produto: sem homer, falta 1", "escolhas": [{ "produto": "acucar", "qtd": 3 }], "ok": false, "codigo": "total_abaixo", "erro": "Falta 1 donut" },
    { "id": "P5", "combo": "homer1", "nome": "limite por produto: premium fora do combo", "escolhas": [{ "produto": "acucar", "qtd": 3 }, { "produto": "pistache", "qtd": 1 }], "ok": false, "codigo": "fora_do_combo", "erro": "Donut Pistache não faz parte do combo" },
    { "id": "T1", "combo": "teto5", "nome": "teto maior que o total vale como o total", "escolhas": [{ "produto": "acucar", "qtd": 4 }], "ok": true, "codigo": null, "erro": null },
    { "id": "T2", "combo": "teto5", "nome": "teto maior que o total: 5 fala do total", "escolhas": [{ "produto": "acucar", "qtd": 5 }], "ok": false, "codigo": "total_acima", "erro": "No máximo 4 donuts" },
    { "id": "R1", "combo": "resto", "nome": "cardapio todo: premium conta no grupo premium", "escolhas": [{ "produto": "pistache", "qtd": 3 }, { "produto": "acucar", "qtd": 1 }], "ok": false, "codigo": "grupo_acima", "erro": "No máximo 2 Premium" },
    { "id": "R2", "combo": "resto", "nome": "cardapio todo: 2 premium e 2 super", "escolhas": [{ "produto": "pistache", "qtd": 2 }, { "produto": "ferrero", "qtd": 2 }], "ok": true, "codigo": null, "erro": null },
    { "id": "A1", "combo": "antigo_fixo", "nome": "regra antiga fixa: 2 e 2", "escolhas": [{ "produto": "pistache", "qtd": 2 }, { "produto": "acucar", "qtd": 2 }], "ok": true, "codigo": null, "erro": null },
    { "id": "A1c", "combo": "antigo_fixo_convertido", "nome": "regra antiga fixa convertida: 2 e 2", "escolhas": [{ "produto": "pistache", "qtd": 2 }, { "produto": "acucar", "qtd": 2 }], "ok": true, "codigo": null, "erro": null },
    { "id": "A2", "combo": "antigo_fixo", "nome": "regra antiga fixa: 1 e 3", "escolhas": [{ "produto": "pistache", "qtd": 1 }, { "produto": "acucar", "qtd": 3 }], "ok": false, "codigo": "grupo_acima", "erro": "No máximo 2 Clássicos" },
    { "id": "A2c", "combo": "antigo_fixo_convertido", "nome": "regra antiga fixa convertida: 1 e 3", "escolhas": [{ "produto": "pistache", "qtd": 1 }, { "produto": "acucar", "qtd": 3 }], "ok": false, "codigo": "grupo_acima", "erro": "No máximo 2 Clássicos" },
    { "id": "A3", "combo": "antigo_fixo", "nome": "regra antiga fixa: 2 e 1", "escolhas": [{ "produto": "pistache", "qtd": 2 }, { "produto": "acucar", "qtd": 1 }], "ok": false, "codigo": "grupo_abaixo", "erro": "Escolha mais 1 Clássicos" },
    { "id": "A3c", "combo": "antigo_fixo_convertido", "nome": "regra antiga fixa convertida: 2 e 1", "escolhas": [{ "produto": "pistache", "qtd": 2 }, { "produto": "acucar", "qtd": 1 }], "ok": false, "codigo": "grupo_abaixo", "erro": "Escolha mais 1 Clássicos" },
    { "id": "A4", "combo": "antigo_fixo", "nome": "regra antiga fixa: vazio", "escolhas": [], "ok": false, "codigo": "grupo_abaixo", "erro": "Escolha 2 Premium" },
    { "id": "A4c", "combo": "antigo_fixo_convertido", "nome": "regra antiga fixa convertida: vazio", "escolhas": [], "ok": false, "codigo": "grupo_abaixo", "erro": "Escolha 2 Premium" },
    { "id": "F1", "combo": "antigo_faixa", "nome": "regra antiga de 1 a 3: 3 e 3", "escolhas": [{ "produto": "pistache", "qtd": 3 }, { "produto": "acucar", "qtd": 3 }], "ok": true, "codigo": null, "erro": null },
    { "id": "F1c", "combo": "antigo_faixa_convertido", "nome": "regra antiga de 1 a 3 convertida: 3 e 3", "escolhas": [{ "produto": "pistache", "qtd": 3 }, { "produto": "acucar", "qtd": 3 }], "ok": true, "codigo": null, "erro": null },
    { "id": "F2", "combo": "antigo_faixa", "nome": "regra antiga de 1 a 3: sem premium", "escolhas": [{ "produto": "acucar", "qtd": 2 }], "ok": false, "codigo": "grupo_abaixo", "erro": "Escolha 1 Premium" },
    { "id": "F2c", "combo": "antigo_faixa_convertido", "nome": "regra antiga de 1 a 3 convertida: sem premium", "escolhas": [{ "produto": "acucar", "qtd": 2 }], "ok": false, "codigo": "grupo_abaixo", "erro": "Escolha 1 Premium" },
    { "id": "F3", "combo": "antigo_faixa", "nome": "regra antiga de 1 a 3: 4 premium", "escolhas": [{ "produto": "pistache", "qtd": 4 }, { "produto": "acucar", "qtd": 1 }], "ok": false, "codigo": "grupo_acima", "erro": "No máximo 3 Premium" },
    { "id": "F3c", "combo": "antigo_faixa_convertido", "nome": "regra antiga de 1 a 3 convertida: 4 premium", "escolhas": [{ "produto": "pistache", "qtd": 4 }, { "produto": "acucar", "qtd": 1 }], "ok": false, "codigo": "grupo_acima", "erro": "No máximo 3 Premium" },
    { "id": "F4", "combo": "antigo_faixa", "nome": "regra antiga de 1 a 3: 1 e 1", "escolhas": [{ "produto": "pistache", "qtd": 1 }, { "produto": "acucar", "qtd": 1 }], "ok": true, "codigo": null, "erro": null },
    { "id": "F4c", "combo": "antigo_faixa_convertido", "nome": "regra antiga de 1 a 3 convertida: 1 e 1", "escolhas": [{ "produto": "pistache", "qtd": 1 }, { "produto": "acucar", "qtd": 1 }], "ok": true, "codigo": null, "erro": null },
    { "id": "Q1", "combo": "qualquer4", "nome": "varias categorias num grupo: 3 classicos e 1 premium", "escolhas": [{ "produto": "acucar", "qtd": 3 }, { "produto": "pistache", "qtd": 1 }], "ok": true, "codigo": null, "erro": null },
    { "id": "Q2", "combo": "qualquer4", "nome": "varias categorias num grupo: 4 do mesmo", "escolhas": [{ "produto": "ferrero", "qtd": 4 }], "ok": true, "codigo": null, "erro": null },
    { "id": "Q3", "combo": "qualquer4", "nome": "varias categorias num grupo: 2 classicos, 1 premium e 1 super", "escolhas": [{ "produto": "acucar", "qtd": 2 }, { "produto": "nutella", "qtd": 1 }, { "produto": "ferrero", "qtd": 1 }], "ok": true, "codigo": null, "erro": null },
    { "id": "Q4", "combo": "qualquer4", "nome": "varias categorias num grupo: 5 no total e recusado", "escolhas": [{ "produto": "acucar", "qtd": 3 }, { "produto": "pistache", "qtd": 2 }], "ok": false, "codigo": "total_acima", "erro": "No máximo 4 donuts" },
    { "id": "Q5", "combo": "qualquer4", "nome": "varias categorias num grupo: 3 no total fala do total, nunca do grupo", "escolhas": [{ "produto": "homer", "qtd": 1 }, { "produto": "pistache", "qtd": 1 }, { "produto": "ferrero", "qtd": 1 }], "ok": false, "codigo": "total_abaixo", "erro": "Falta 1 donut" },
    { "id": "Q6", "combo": "qualquer4", "nome": "varias categorias num grupo: vazio", "escolhas": [], "ok": false, "codigo": "total_abaixo", "erro": "Faltam 4 donuts" },
    { "id": "Q7", "combo": "qualquer4", "nome": "varias categorias num grupo: 5 do mesmo fala do total", "escolhas": [{ "produto": "ferrero", "qtd": 5 }], "ok": false, "codigo": "total_acima", "erro": "No máximo 4 donuts" },
    { "id": "Q8", "combo": "qualquer4", "nome": "varias categorias num grupo: produto de outra categoria fica fora", "escolhas": [{ "produto": "acucar", "qtd": 3 }, { "produto": "brownie", "qtd": 1 }], "ok": false, "codigo": "fora_do_combo", "erro": "Brownie não faz parte do combo" },
    { "id": "QL1", "combo": "qualquer4_como_lista", "nome": "o mesmo grupo como lista (o que o caixa recebe): 3 classicos e 1 premium", "escolhas": [{ "produto": "acucar", "qtd": 3 }, { "produto": "pistache", "qtd": 1 }], "ok": true, "codigo": null, "erro": null },
    { "id": "QL2", "combo": "qualquer4_como_lista", "nome": "o mesmo grupo como lista (o que o caixa recebe): 4 do mesmo", "escolhas": [{ "produto": "ferrero", "qtd": 4 }], "ok": true, "codigo": null, "erro": null },
    { "id": "QL3", "combo": "qualquer4_como_lista", "nome": "o mesmo grupo como lista (o que o caixa recebe): 2 classicos, 1 premium e 1 super", "escolhas": [{ "produto": "acucar", "qtd": 2 }, { "produto": "nutella", "qtd": 1 }, { "produto": "ferrero", "qtd": 1 }], "ok": true, "codigo": null, "erro": null },
    { "id": "QL4", "combo": "qualquer4_como_lista", "nome": "o mesmo grupo como lista (o que o caixa recebe): 5 no total e recusado", "escolhas": [{ "produto": "acucar", "qtd": 3 }, { "produto": "pistache", "qtd": 2 }], "ok": false, "codigo": "total_acima", "erro": "No máximo 4 donuts" },
    { "id": "QL5", "combo": "qualquer4_como_lista", "nome": "o mesmo grupo como lista (o que o caixa recebe): 3 no total fala do total, nunca do grupo", "escolhas": [{ "produto": "homer", "qtd": 1 }, { "produto": "pistache", "qtd": 1 }, { "produto": "ferrero", "qtd": 1 }], "ok": false, "codigo": "total_abaixo", "erro": "Falta 1 donut" },
    { "id": "QL6", "combo": "qualquer4_como_lista", "nome": "o mesmo grupo como lista (o que o caixa recebe): vazio", "escolhas": [], "ok": false, "codigo": "total_abaixo", "erro": "Faltam 4 donuts" },
    { "id": "QL7", "combo": "qualquer4_como_lista", "nome": "o mesmo grupo como lista (o que o caixa recebe): 5 do mesmo fala do total", "escolhas": [{ "produto": "ferrero", "qtd": 5 }], "ok": false, "codigo": "total_acima", "erro": "No máximo 4 donuts" },
    { "id": "QL8", "combo": "qualquer4_como_lista", "nome": "o mesmo grupo como lista (o que o caixa recebe): produto de outra categoria fica fora", "escolhas": [{ "produto": "acucar", "qtd": 3 }, { "produto": "brownie", "qtd": 1 }], "ok": false, "codigo": "fora_do_combo", "erro": "Brownie não faz parte do combo" },
    { "id": "D1", "combo": "dois_e_dois", "nome": "2 de um e 2 de outro: 1 e 1 premium e 2 classicos", "escolhas": [{ "produto": "pistache", "qtd": 1 }, { "produto": "nutella", "qtd": 1 }, { "produto": "acucar", "qtd": 2 }], "ok": true, "codigo": null, "erro": null },
    { "id": "D2", "combo": "dois_e_dois", "nome": "2 de um e 2 de outro: 1 premium e 3 classicos", "escolhas": [{ "produto": "pistache", "qtd": 1 }, { "produto": "acucar", "qtd": 3 }], "ok": false, "codigo": "grupo_acima", "erro": "No máximo 2 Clássicos" },
    { "id": "D3", "combo": "dois_e_dois", "nome": "2 de um e 2 de outro: 3 premium e 1 classico", "escolhas": [{ "produto": "pistache", "qtd": 2 }, { "produto": "nutella", "qtd": 1 }, { "produto": "homer", "qtd": 1 }], "ok": false, "codigo": "grupo_acima", "erro": "No máximo 2 Premium" },
    { "id": "D4", "combo": "dois_e_dois", "nome": "2 de um e 2 de outro: 4 premium", "escolhas": [{ "produto": "pistache", "qtd": 4 }], "ok": false, "codigo": "grupo_acima", "erro": "No máximo 2 Premium" },
    { "id": "D5", "combo": "dois_e_dois", "nome": "2 de um e 2 de outro: 2 premium e 1 classico", "escolhas": [{ "produto": "pistache", "qtd": 1 }, { "produto": "nutella", "qtd": 1 }, { "produto": "acucar", "qtd": 1 }], "ok": false, "codigo": "grupo_abaixo", "erro": "Escolha mais 1 Clássicos" },
    { "id": "D6", "combo": "dois_e_dois", "nome": "2 de um e 2 de outro: super premium fica fora", "escolhas": [{ "produto": "pistache", "qtd": 2 }, { "produto": "acucar", "qtd": 1 }, { "produto": "ferrero", "qtd": 1 }], "ok": false, "codigo": "fora_do_combo", "erro": "Donut Ferrero não faz parte do combo" },
    { "id": "M1", "combo": "misto", "nome": "categorias com avulso: 2 homer avulso e 2 classicos (o homer conta em Especiais)", "escolhas": [{ "produto": "homer", "qtd": 2 }, { "produto": "acucar", "qtd": 2 }], "ok": true, "codigo": null, "erro": null },
    { "id": "M2", "combo": "misto", "nome": "categorias com avulso: homer e 2 premium passam do teto de Especiais", "escolhas": [{ "produto": "homer", "qtd": 1 }, { "produto": "pistache", "qtd": 2 }, { "produto": "acucar", "qtd": 1 }], "ok": false, "codigo": "grupo_acima", "erro": "No máximo 2 Especiais" },
    { "id": "M3", "combo": "misto", "nome": "categorias com avulso: 1 super e 3 classicos", "escolhas": [{ "produto": "ferrero", "qtd": 1 }, { "produto": "acucar", "qtd": 3 }], "ok": true, "codigo": null, "erro": null },
    { "id": "M4", "combo": "misto", "nome": "categorias com avulso: 2 premium e 1 classico", "escolhas": [{ "produto": "nutella", "qtd": 2 }, { "produto": "acucar", "qtd": 1 }], "ok": false, "codigo": "grupo_abaixo", "erro": "Escolha mais 1 Clássicos" },
    { "id": "M5", "combo": "misto", "nome": "categorias com avulso: 4 homer nao escapam pela categoria", "escolhas": [{ "produto": "homer", "qtd": 4 }], "ok": false, "codigo": "grupo_acima", "erro": "No máximo 2 Especiais" },
    { "id": "M6", "combo": "misto", "nome": "categorias com avulso: brownie fica fora", "escolhas": [{ "produto": "acucar", "qtd": 3 }, { "produto": "brownie", "qtd": 1 }], "ok": false, "codigo": "fora_do_combo", "erro": "Brownie não faz parte do combo" },
    { "id": "M7", "combo": "misto", "nome": "categorias com avulso: 2 homer e 1 classico: o homer nao conta em Classicos", "escolhas": [{ "produto": "homer", "qtd": 2 }, { "produto": "acucar", "qtd": 1 }], "ok": false, "codigo": "grupo_abaixo", "erro": "Escolha mais 1 Clássicos" }
  ]
}
""";
}
