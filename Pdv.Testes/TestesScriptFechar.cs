using System.Text;
using System.Text.Json;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O X DO PAINEL DE CONVERSA (1.0.41, 16/10/2026, pedido do dono com fotos). Quando o funcionário toca no X
/// do painel da conversa do Gestor (canto superior direito de "#5041 Maluma Lopes"), o Gestor fecha o painel
/// do chat INTEIRO e a tela fica só com "AJUDA iFOOD" e "Respostas prontas"; a equipe então toca em Reabrir,
/// que recria o WebView2 (30 s parado). O dono: "um frame ali no X impedindo de fechar, só com a seta de
/// retornar".
///
/// O script <c>ScriptFechar</c> de Telas/ChatIfood.xaml.cs roda aqui no node contra um DOM falso com a gaveta,
/// o cabeçalho da conversa, a seta de voltar e o X:
///  · o X some (atributo próprio + CSS) e o clique nele morre na captura; a seta continua funcionando;
///  · o X sem rótulo nenhum (só o último botão à direita da linha do cabeçalho) também é pego;
///  · 1.0.42 (foto do dono às 17:55: o X continuava lá com a 1.0.41): a regra é GEOMÉTRICA, sem classe nem rótulo:
///    todo clicável nos 72 px da direita da linha do cabeçalho some, nos DOIS modos (só o chat e Gestor inteiro);
///    o clique cujo alvo está nessa faixa (até 4 níveis acima) morre; o cabeçalho que muda de lugar é reavaliado;
///    uma linha de diag por carga descreve os clicáveis do cabeçalho, mascarada;
///  · 1.0.43 (diag REAL da Savassi na 1.0.42, 19:24: "linha=510,8,506,69 [BUTTON ... txt=## Alessandr 554,20,422,24]",
///    o único clicável da linha é o título; o X é IRMÃO do cabeçalho, no canto do painel): a CAIXA PROIBIDA é o canto
///    superior direito do PAINEL (64 x 64 px): tudo cujo centro cai ali (elementsFromPoint no centro e nos 4 cantos da
///    caixa + varredura do painel, svg/path/div inclusive) fica invisível e sem ponteiro; o clique/pointerdown/mousedown/
///    touchstart POR COORDENADA dentro da caixa morre na captura, seja qual for o alvo; a seta nunca entra na caixa;
///    sem painel achado nada quebra; a linha de diag lista também o que foi achado no canto;
///  · a vigia de 2 s (1.0.43, o LAÇO da Savassi: "painel do chat fechou: reaberto (abriu)" a cada 10 s sem parar):
///    a conversa aberta (cabeçalho na tela, título "Conversas" fora) é painel ABERTO; só reabre se o painel estava
///    na tela há menos de 10 s e sumiu sem toque do operador (15 s); nunca com o operador na tela do chat; no máximo
///    1 reabertura a cada 5 min; 2 reaberturas seguidas que não pegam (some de novo em menos de 30 s, ou nunca volta)
///    desligam a vigia até a próxima navegação (aviso vigiadesligada); o aviso leva as reaberturas da última hora.
/// Sem node na máquina a suíte FALHA (não pula).
/// </summary>
public static class TestesScriptFechar
{
    public static void Rodar(Action<bool, string> checar)
    {
        var raiz = TestesScriptSendbird.Raiz();
        var fonte = raiz is null ? "" : TestesScriptSendbird.LerSeExiste(Path.Combine(raiz, "Telas", "ChatIfood.xaml.cs"));
        var script = TestesScriptSendbird.ExtrairRaw(fonte, "ScriptFechar");
        checar(script is { Length: > 500 }, "SF-0 o ScriptFechar sai da constante de Telas/ChatIfood.xaml.cs");
        if (script is null) return;
        checar(!script.Contains((char)0x2014) && !script.Contains((char)0x2013), "SF-0b o script não tem travessão");
        checar(fonte.Contains("await core.AddScriptToExecuteOnDocumentCreatedAsync(ScriptFechar);")
               && fonte.IndexOf("AddScriptToExecuteOnDocumentCreatedAsync(ScriptPainel)", StringComparison.Ordinal) < fonte.IndexOf("AddScriptToExecuteOnDocumentCreatedAsync(ScriptFechar)", StringComparison.Ordinal),
            "SF-0c a tela injeta o ScriptFechar logo depois do ScriptPainel");
        checar(fonte.Contains("window.pdvGaveta = candidato;"), "SF-0d o ScriptPainel expõe a gaveta (window.pdvGaveta) para o ScriptFechar");
        checar(fonte.Contains("tipo == \"painelfechou\"") && fonte.Contains("painel do chat fechou: reaberto"),
            "SF-0e a tela recebe o aviso painelfechou e anota 'painel do chat fechou: reaberto' no diag");
        checar(fonte.Contains("tipo == \"cabecalho\"") && fonte.Contains("Diag(\"cabecalho da conversa: \" + ConversaRaspadinha.LinhaDoCabecalho(txt));"),
            "SF-0f (1.0.42) a tela recebe a descrição do cabeçalho e anota uma linha mascarada no diag");
        var linha = ConversaRaspadinha.LinhaDoCabecalho("{\"tipo\":\"cabecalho\",\"modo\":\"gestor_inteiro\",\"r\":[600,50,420,56],\"clicaveis\":[{\"tag\":\"BUTTON\",\"cls\":\"ifdl-btn x9\",\"aria\":\"Voltar\",\"txt\":\"\",\"r\":[608,58,40,40]},{\"tag\":\"BUTTON\",\"cls\":\"\",\"aria\":\"\",\"txt\":\"Maluma 123456 <b>\",\"r\":[972,58,40,40]}]}");
        checar(linha.StartsWith("modo=gestor_inteiro linha=600,50,420,56 [BUTTON cls=ifdl-btn x9 aria=Voltar txt=- 608,58,40,40] [BUTTON cls=- aria=- txt=Maluma # _b_ 972,58,40,40]", StringComparison.Ordinal),
            "SF-0g a linha do cabeçalho sai só com letra, número e pontuação simples, números longos viram # (" + linha + ")");
        checar(ConversaRaspadinha.LinhaDoCabecalho("lixo{") == "?" && ConversaRaspadinha.LinhaDoCabecalho(null) == "?" && ConversaRaspadinha.LinhaDoCabecalho("[1]") == "?",
            "SF-0h lixo, nulo e array viram ? sem lançar");

        // 1.0.43: a tela, a vigia desligada e o operador na tela do chat
        checar(fonte.Contains("tipo == \"vigiadesligada\"") && fonte.Contains("vigia do painel: desligada (laco)"),
            "SF-0i (1.0.43) a tela recebe o aviso vigiadesligada e anota 'vigia do painel: desligada (laco)' no diag");
        checar(fonte.Contains("IsHitTestVisibleChanged +=") && fonte.Contains("window.__pdvOperadorNoChat = ")
               && fonte.IndexOf("window.__pdvOperadorNoChat = ", StringComparison.Ordinal) < fonte.IndexOf("Diag(e.IsSuccess ? \"navegou: ok\"", StringComparison.Ordinal),
            "SF-0j (1.0.43) a tela avisa a página se o operador está no chat (IsHitTestVisible), na troca e depois de cada navegação");
        checar(fonte.Contains("na ultima hora"), "SF-0k (1.0.43) o diag da reabertura conta as reaberturas da última hora");
        var comCanto = ConversaRaspadinha.LinhaDoCabecalho("{\"tipo\":\"cabecalho\",\"modo\":\"so_chat\",\"r\":[510,8,506,69],\"clicaveis\":[{\"tag\":\"BUTTON\",\"cls\":\"sc-fa\",\"aria\":\"\",\"txt\":\"## Alessandr\",\"r\":[554,20,422,24]}],\"caixa\":[960,0,64,64],\"canto\":[{\"tag\":\"DIV\",\"cls\":\"sc-canto\",\"aria\":\"\",\"txt\":\"\",\"r\":[984,24,24,24]},{\"tag\":\"svg\",\"cls\":\"\",\"aria\":\"Maluma 123456\",\"txt\":\"\",\"r\":[986,26,20,20]}]}");
        checar(comCanto.Contains(" caixa=960,0,64,64 canto: [DIV cls=sc-canto aria=- txt=- 984,24,24,24] [svg cls=- aria=Maluma # txt=- 986,26,20,20]", StringComparison.Ordinal),
            "SF-0l (1.0.43) a linha do cabeçalho lista a caixa proibida e o que foi achado no canto, mascarado (" + comCanto + ")");
        checar(!linha.Contains("caixa=", StringComparison.Ordinal) && ConversaRaspadinha.LinhaDoCabecalho("{\"modo\":\"so_chat\",\"caixa\":[1,2,3,4],\"canto\":[]}").EndsWith(" caixa=1,2,3,4 canto: nenhum", StringComparison.Ordinal),
            "SF-0m sem caixa a linha fica como era; caixa sem nada no canto diz 'nenhum'");

        var pasta = Path.Combine(Path.GetTempPath(), "pdv-sf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(pasta);
        try
        {
            var arqScript = Path.Combine(pasta, "script-fechar.js");
            var arqTeste = Path.Combine(pasta, "teste-fechar.js");
            File.WriteAllText(arqScript, script, new UTF8Encoding(false));
            File.WriteAllText(arqTeste, Harness, new UTF8Encoding(false));

            var check = TestesScriptSendbird.Node(pasta, "--check", arqScript);
            checar(check.Exit == 0, $"SF-1 o script injetado passa no node --check ({check.Erro.Trim()})");

            var r = TestesScriptSendbird.Node(pasta, arqTeste, arqScript);
            List<ItemHarness>? itens = null;
            try
            {
                var ultima = r.Saida.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("[", StringComparison.Ordinal));
                if (ultima is not null) itens = JsonSerializer.Deserialize<List<ItemHarness>>(ultima);
            }
            catch { itens = null; }
            checar(r.Exit == 0 && itens is { Count: > 10 },
                $"SF-2 o node rodou o script contra o DOM falso (saida {r.Exit}, {itens?.Count ?? 0} conferências) {Corta(r.Erro)}");
            foreach (var i in itens ?? new List<ItemHarness>()) checar(i.ok, "SF-" + i.nome);
        }
        finally
        {
            try { Directory.Delete(pasta, true); } catch { }
        }
    }

    private sealed record ItemHarness(bool ok, string nome);

    private static string Corta(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim();

    // ── o harness em node: um DOM de mentira só com o que o script toca ──────
    private const string Harness = """
'use strict';
const fs = require('fs');
const vm = require('vm');
const codigo = fs.readFileSync(process.argv[2], 'utf8');
const res = [];
function checar(ok, nome) { res.push({ ok: !!ok, nome: nome }); }

// ── o DOM falso ──
let seq = 0;
function El(tag, opts) {
  opts = opts || {};
  this.tagName = tag.toUpperCase();
  this.id = opts.id || '';
  this.className = opts.className || '';
  this._attrs = Object.assign({}, opts.attrs || {});
  this._text = opts.text || '';
  this._rect = opts.rect || { left: 0, top: 0, width: 0, height: 0 };
  this.children = [];
  this.parentElement = null;
  this.clicks = 0;
  this._seq = seq++;
}
El.prototype.appendChild = function (c) { c.parentElement = this; this.children.push(c); return c; };
El.prototype.getAttribute = function (n) { return n === 'class' ? this.className : (Object.prototype.hasOwnProperty.call(this._attrs, n) ? this._attrs[n] : null); };
El.prototype.setAttribute = function (n, v) { if (n === 'class') this.className = v; else this._attrs[n] = String(v); };
El.prototype.removeAttribute = function (n) { delete this._attrs[n]; };
El.prototype.hasAttribute = function (n) { return Object.prototype.hasOwnProperty.call(this._attrs, n); };
El.prototype.getBoundingClientRect = function () { const r = this._rect; return { left: r.left, top: r.top, width: r.width, height: r.height, right: r.left + r.width, bottom: r.top + r.height }; };
Object.defineProperty(El.prototype, 'textContent', {
  get: function () { let t = this._text; this.children.forEach(function (c) { t += c.textContent; }); return t; },
  set: function (v) { this._text = String(v); this.children = []; },
});
El.prototype.contains = function (x) { let n = x; while (n) { if (n === this) return true; n = n.parentElement; } return false; };
El.prototype.descendentes = function () { const out = []; (function walk(n) { n.children.forEach(function (c) { out.push(c); walk(c); }); })(this); return out; };
El.prototype.profundidade = function () { let n = 0, e = this; while (e.parentElement) { n++; e = e.parentElement; } return n; };
El.prototype.click = function () { this.clicks++; };
function casaUm(el, sel) {
  sel = sel.trim();
  if (sel === '*') return true;
  const m = /^([a-zA-Z0-9]*)((?:\[[^\]]+\])*)$/.exec(sel);
  if (!m) return false;
  if (m[1] && el.tagName !== m[1].toUpperCase()) return false;
  const attrs = m[2].match(/\[[^\]]+\]/g) || [];
  for (const a of attrs) {
    const am = /^\[([^=*^$~\]]+)(?:([*^$]?)=["']?([^"'\]]*)["']?)?\]$/.exec(a);
    if (!am) return false;
    const v = el.getAttribute(am[1]);
    if (am[3] === undefined) { if (v === null) return false; continue; }
    if (v === null) return false;
    if (am[2] === '*') { if (v.indexOf(am[3]) < 0) return false; }
    else if (am[2] === '^') { if (v.indexOf(am[3]) !== 0) return false; }
    else if (v !== am[3]) return false;
  }
  return true;
}
function casa(el, selector) { return selector.split(',').some(function (s) { return casaUm(el, s); }); }
El.prototype.querySelectorAll = function (selector) { return this.descendentes().filter(function (e) { return casa(e, selector); }); };
El.prototype.querySelector = function (selector) { return this.querySelectorAll(selector)[0] || null; };
El.prototype.closest = function (selector) { let n = this; while (n && n !== body) { if (casa(n, selector)) return n; n = n.parentElement; } return null; };

const body = new El('body', { rect: { left: 0, top: 0, width: 1024, height: 768 } });
const classes = new Set(['pdv-so-chat']);
body.classList = { contains: function (c) { return classes.has(c); }, add: function (c) { classes.add(c); }, remove: function (c) { classes.delete(c); } };
const ouvintes = {};
const head = new El('head');
function dentroDe(el, x, y) { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0 && x >= r.left && x <= r.right && y >= r.top && y <= r.bottom; }
const document = {
  body: body, head: head, documentElement: body,
  addEventListener: function (tipo, fn, captura) { (ouvintes[tipo] = ouvintes[tipo] || []).push({ fn: fn, captura: !!captura }); },
  querySelectorAll: function (s) { return body.querySelectorAll(s); },
  querySelector: function (s) { return body.querySelector(s); },
  getElementById: function (id) { return body.descendentes().concat(head.descendentes()).find(function (e) { return e.id === id; }) || null; },
  createElement: function (tag) { return new El(tag); },
  // 1.0.43: a pilha de elementos debaixo de um ponto, o mais fundo primeiro, como no navegador
  elementsFromPoint: function (x, y) {
    return body.descendentes().concat([body]).filter(function (e) { return !e.hasAttribute('data-pdv-fechar-canto') && dentroDe(e, x, y); })
      .sort(function (a, b) { return b.profundidade() - a.profundidade(); });
  },
};

// ── a gaveta como o Gestor a monta: título "Conversas", o cabeçalho da conversa (seta, nome, X) e os balões ──
let gaveta, seta, x, xSemRotulo, titulo, cabecalho, canto, cantoIcone;
function montarGaveta(opts) {
  opts = opts || {};
  var dx = opts.dx || 0, dy = opts.dy || 0;   // 1.0.42: o cabecalho em outro lugar da tela
  function R(l, t, w, h) { return { left: l + dx, top: t + dy, width: w, height: h }; }
  body.children = [];
  canto = null; cantoIcone = null;
  gaveta = body.appendChild(new El('div', { className: 'drawer', rect: R(600, 0, 420, 768) }));
  titulo = gaveta.appendChild(new El('h1', { text: 'Conversas', rect: R(610, 10, 400, 30) }));
  cabecalho = gaveta.appendChild(new El('div', { className: 'chat-header', rect: R(600, 50, 420, 56) }));
  seta = cabecalho.appendChild(new El('button', { attrs: opts.setaSemRotulo ? {} : { 'aria-label': 'Voltar' }, rect: R(608, 58, 40, 40) }));
  seta.appendChild(new El('svg', { className: opts.setaSemRotulo ? 'ifdl-icon-arrow-left' : 'ifdl-icon', rect: R(616, 66, 24, 24) }));
  cabecalho.appendChild(new El('span', { text: '#5041 Maluma Lopes', rect: R(660, 62, 250, 30) }));
  if (opts.xNoCanto) {
    // 1.0.43: o DOM real da Savassi: o X e um div com svg, IRMAO do cabecalho, no canto do painel
    // (16 px da borda direita, 24 px do topo), fora da faixa vertical da linha do cabecalho
    x = null; xSemRotulo = null;
    canto = gaveta.appendChild(new El('div', { className: 'sc-canto', rect: R(980, 24, 24, 24) }));
    cantoIcone = canto.appendChild(new El('svg', { rect: R(982, 26, 20, 20) }));
    cantoIcone.appendChild(new El('path', { rect: R(984, 28, 16, 16) }));
  } else if (opts.xSemRotulo) {
    xSemRotulo = cabecalho.appendChild(new El('button', { rect: R(972, 58, 40, 40) }));
    xSemRotulo.appendChild(new El('svg', { className: 'ifdl-icon', rect: R(980, 66, 24, 24) }));
    x = xSemRotulo;
  } else {
    x = cabecalho.appendChild(new El('button', { attrs: { 'aria-label': 'Fechar' }, rect: R(972, 58, 40, 40) }));
    x.appendChild(new El('svg', { className: 'ifdl-icon-close', rect: R(980, 66, 24, 24) }));
  }
  const baloes = gaveta.appendChild(new El('div', { className: 'messages', rect: R(600, 110, 420, 600) }));
  baloes.appendChild(new El('div', { text: 'oi, ganhei na raspadinha', rect: R(610, 120, 200, 40) }));
  const enviar = gaveta.appendChild(new El('button', { attrs: { 'aria-label': 'Enviar' }, rect: R(960, 720, 50, 40) }));
  return gaveta;
}
function despacha(tipo, ev) {
  let parou = false;
  ev.stopImmediatePropagation = function () { parou = true; };
  (ouvintes[tipo] || []).forEach(function (o) { if (o.captura && !parou) o.fn(ev); });
  return parou;
}
// o clique como o navegador o entrega: pointerdown e depois click, na captura do document; com ou sem coordenadas
function clique(alvo, cx, cy) {
  let prevenido = false;
  const base = { target: alvo, preventDefault: function () { prevenido = true; } };
  if (typeof cx === 'number') { base.clientX = cx; base.clientY = cy; }
  const parouToque = despacha('pointerdown', Object.assign({}, base));
  const parou = despacha('click', Object.assign({}, base));
  if (!parou) alvo.clicks++;
  return { parou: parou, parouToque: parouToque, prevenido: prevenido };
}

// ── a janela ──
let agora = 1760630000000;
let aquecimentos = 0, aquecerDevolve = 'abriu', gavetaVisivel = true;
const msgs = [];
const intervalos = [];
const sb = {
  console: console,
  document: document,
  Date: { now: function () { return agora; } },
  setTimeout: function (fn, ms) { return setTimeout(fn, ms); },
  setInterval: function (fn, ms) { intervalos.push({ fn: fn, ms: ms }); return 1; },
  clearInterval: function () {},
  MutationObserver: function (cb) { this.observe = function () { sb.__observador = cb; }; },
  chrome: { webview: { postMessage: function (s) { msgs.push(JSON.parse(s)); } } },
  pdvGaveta: function () { return gavetaVisivel ? gaveta : null; },
  pdvAquecerLista: function () { aquecimentos++; return aquecerDevolve; },
};
sb.window = sb; sb.self = sb;
vm.createContext(sb);
montarGaveta();
vm.runInContext(codigo, sb);

(function () {
  // 10: o estilo e as funções existem
  checar(typeof sb.pdvBlindarFechar === 'function' && typeof sb.pdvVigiarPainel === 'function', '10 o script expõe pdvBlindarFechar e pdvVigiarPainel');
  const css = head.children.find(function (e) { return e.id === 'pdv-css-fechar'; });
  checar(css && css.textContent.indexOf('[data-pdv-fechar-painel]{display:none!important;pointer-events:none!important}') === 0
    && css.textContent.indexOf('[data-pdv-fechar-canto]{visibility:hidden!important;pointer-events:none!important}') > 0,
    '11 o CSS esconde o X marcado (display none) e o que mora no canto (visibility hidden), ambos sem ponteiro, nos DOIS modos');
  checar(intervalos.some(function (i) { return i.ms === 2000; }) && intervalos.some(function (i) { return i.ms === 1000; }) && typeof sb.__observador === 'function',
    '12 a vigia roda a cada 2 s, a blindagem a cada 1 s e o DOM é observado');
  checar(['click', 'pointerdown', 'mousedown', 'touchstart'].every(function (t) { return (ouvintes[t] || []).some(function (o) { return o.captura; }); }),
    '13 (1.0.43) click, pointerdown, mousedown e touchstart são ouvidos na captura do document');

  // 20: o X com rótulo é marcado; a seta não
  const n = sb.pdvBlindarFechar();
  checar(n === 1 && x.hasAttribute('data-pdv-fechar-painel') && !seta.hasAttribute('data-pdv-fechar-painel'),
    '20 pdvBlindarFechar marca o X do painel da conversa (aria-label Fechar, icone close) e deixa a seta de voltar livre (' + n + ')');
  checar(!titulo.hasAttribute('data-pdv-fechar-painel') && !gaveta.querySelector('[aria-label="Enviar"]').hasAttribute('data-pdv-fechar-painel'),
    '21 nem o título, nem o botão de enviar são tocados');

  // 30: o clique no X morre na captura; o da seta passa
  const cx = clique(x.children[0]);
  checar(cx.parou && cx.prevenido && x.clicks === 0, '30 o clique no X (no icone dentro dele) morre na fase de captura: o Gestor nunca ouve');
  const cs = clique(seta.children[0]);
  checar(!cs.parou && !cs.prevenido, '31 o clique na seta de voltar passa');

  // 32: 1.0.42: fora do modo "só o chat" (o Gestor inteiro atrás, a foto do dono) o X continua barrado; a seta passa
  classes.delete('pdv-so-chat');
  const cx2 = clique(x);
  checar(cx2.parou && x.clicks === 0, '32 (1.0.42) no modo Gestor inteiro o clique no X também morre');
  checar(!clique(seta.children[0]).parou, '32b e a seta de voltar segue passando no Gestor inteiro');
  classes.add('pdv-so-chat');

  // 33: o X que o React recriou sem a marca ainda morre pelo formato (icone close)
  const xNovo = cabecalho.appendChild(new El('button', { attrs: { 'aria-label': 'Fechar' }, rect: { left: 972, top: 58, width: 40, height: 40 } }));
  xNovo.appendChild(new El('svg', { className: 'ifdl-icon-close', rect: { left: 980, top: 66, width: 24, height: 24 } }));
  const cx3 = clique(xNovo.children[0]);
  checar(cx3.parou && xNovo.clicks === 0, '33 um X recriado pelo React (sem a marca) morre do mesmo jeito, pelo formato');

  // 40: o X SEM rótulo nenhum: o último botão à direita na linha do cabeçalho (a seta à esquerda)
  montarGaveta({ xSemRotulo: true, setaSemRotulo: true });
  const n2 = sb.pdvBlindarFechar();
  checar(n2 === 1 && xSemRotulo.hasAttribute('data-pdv-fechar-painel') && !seta.hasAttribute('data-pdv-fechar-painel'),
    '40 sem rótulo nem classe de fechar, o último botão à direita da linha do cabeçalho é o X; a seta (icone arrow-left) continua livre (' + n2 + ')');
  const cx4 = clique(xSemRotulo.children[0]);
  checar(cx4.parou && xSemRotulo.clicks === 0, '41 e o clique nele morre');
  checar(!clique(seta).parou, '42 a seta segue passando');

  // 43 (1.0.42): a GEOMETRIA. Um botão qualquer, sem rótulo, sem classe e sem glifo, nos 72 px da direita do cabeçalho some;
  // o mesmo botão na esquerda do cabeçalho (ao lado da seta) e o botão de enviar (fora da faixa vertical) ficam
  montarGaveta({ xSemRotulo: true, setaSemRotulo: true });
  const estranhoDireita = cabecalho.appendChild(new El('button', { className: 'sc-1a2b3c', rect: { left: 950, top: 60, width: 20, height: 36 } }));
  const estranhoEsquerda = cabecalho.appendChild(new El('button', { className: 'sc-9z8y7x', rect: { left: 650, top: 60, width: 20, height: 36 } }));
  const n3 = sb.pdvBlindarFechar();
  checar(n3 === 2 && xSemRotulo.hasAttribute('data-pdv-fechar-painel') && estranhoDireita.hasAttribute('data-pdv-fechar-painel'),
    '43 todo clicável nos 72 px da direita do cabeçalho some, sem depender de classe ou rótulo (' + n3 + ')');
  checar(!estranhoEsquerda.hasAttribute('data-pdv-fechar-painel') && !seta.hasAttribute('data-pdv-fechar-painel') && !gaveta.querySelector('[aria-label="Enviar"]').hasAttribute('data-pdv-fechar-painel'),
    '43b o clicável à esquerda do cabeçalho, a seta e o botão de enviar (fora da faixa vertical) ficam');
  checar(clique(estranhoDireita).parou && !clique(estranhoEsquerda).parou, '43c o clique no da direita morre; no da esquerda passa');

  // 44: o clique num alvo fundo (4 níveis dentro do X) e num elemento solto da faixa (sem botão) morre; na faixa vertical errada passa
  montarGaveta({ xSemRotulo: true, setaSemRotulo: true });
  const d1 = x.appendChild(new El('span', { rect: { left: 976, top: 62, width: 32, height: 32 } }));
  const d2 = d1.appendChild(new El('span', { rect: { left: 980, top: 66, width: 24, height: 24 } }));
  const d4 = d2.appendChild(new El('i', { rect: { left: 984, top: 70, width: 16, height: 16 } }));
  const c4 = clique(d4);
  checar(c4.parou && c4.prevenido && x.clicks === 0, '44 o clique no quarto nível dentro do X morre na captura (sem a marca, pela geometria)');
  const solto = cabecalho.appendChild(new El('span', { text: '', rect: { left: 990, top: 70, width: 16, height: 16 } }));
  checar(clique(solto).parou, '44b um elemento solto (sem botão) na faixa da direita do cabeçalho também morre');
  const foraDaFaixa = gaveta.appendChild(new El('span', { rect: { left: 990, top: 300, width: 16, height: 16 } }));
  checar(!clique(foraDaFaixa).parou, '44c o mesmo canto, mas fora da faixa vertical do cabeçalho, passa');

  // 45: o cabeçalho mudou de lugar (o Gestor inteiro atrás, a gaveta em outra coluna): reavaliado no lugar novo
  montarGaveta({ xSemRotulo: true, setaSemRotulo: true, dx: -500, dy: 120 });
  const n5 = sb.pdvBlindarFechar();
  checar(n5 === 1 && xSemRotulo.hasAttribute('data-pdv-fechar-painel') && !seta.hasAttribute('data-pdv-fechar-painel'),
    '45 com o cabeçalho em outra posição o X é achado de novo pela geometria da linha nova (' + n5 + ')');
  checar(clique(xSemRotulo.children[0]).parou && !clique(seta.children[0]).parou, '45b e o clique nele morre; a seta passa');

  // 46: sem a gaveta (pdvGaveta devolve nulo, o modo Gestor inteiro), o cabeçalho é procurado no documento inteiro
  montarGaveta({ xSemRotulo: true, setaSemRotulo: true });
  gavetaVisivel = false;
  const n6 = sb.pdvBlindarFechar();
  checar(n6 === 1 && xSemRotulo.hasAttribute('data-pdv-fechar-painel'), '46 sem a gaveta o X do cabeçalho some do mesmo jeito (o modo Gestor inteiro da foto) (' + n6 + ')');
  checar(clique(xSemRotulo.children[0]).parou, '46b e o clique nele morre');
  gavetaVisivel = true;

  // 47: a descrição do cabeçalho vai para a tela UMA vez por carga, mascarada
  const cab = msgs.filter(function (m) { return m.tipo === 'cabecalho'; });
  checar(cab.length === 1 && Array.isArray(cab[0].clicaveis) && cab[0].clicaveis.length >= 2 && cab[0].clicaveis.every(function (c) { return c.tag && Array.isArray(c.r) && c.r.length === 4; }),
    '47 uma mensagem "cabecalho" por carga, com os clicáveis (tag e retângulo) (' + cab.length + ')');
  checar(cab.length === 1 && JSON.stringify(cab[0]).indexOf('Maluma') < 0 && JSON.stringify(cab[0]).indexOf('5041') < 0, '47b sem nome de cliente nem número do pedido');
  checar(cab.length === 1 && Array.isArray(cab[0].caixa) && cab[0].caixa.length === 4 && cab[0].caixa[0] === 956 && cab[0].caixa[1] === 0 && cab[0].caixa[2] === 64 && cab[0].caixa[3] === 64
    && Array.isArray(cab[0].canto) && cab[0].canto.length === 0,
    '47c (1.0.43) a mensagem traz a caixa proibida (os 64 x 64 px do canto do painel) e o canto vazio, porque esta gaveta nao tem nada ali (' + JSON.stringify(cab[0] && cab[0].caixa) + ')');

  // 48 (1.0.43): O DOM REAL DA SAVASSI. O X nao e filho da linha do cabecalho: e um div com svg, irmao do cabecalho,
  // no canto do painel (fora da faixa vertical da linha, acima dela). A caixa proibida e o canto do PAINEL.
  montarGaveta({ xNoCanto: true, setaSemRotulo: true });
  const n8 = sb.pdvBlindarFechar();
  checar(n8 >= 1 && canto.hasAttribute('data-pdv-fechar-canto') && !canto.hasAttribute('data-pdv-fechar-painel'),
    '48 o div do canto do painel (fora da linha do cabecalho) some pela caixa proibida (visibility hidden) (' + n8 + ')');
  checar(!seta.hasAttribute('data-pdv-fechar-canto') && !cabecalho.hasAttribute('data-pdv-fechar-canto') && !gaveta.hasAttribute('data-pdv-fechar-canto') && !titulo.hasAttribute('data-pdv-fechar-canto'),
    '48b a seta, a linha do cabecalho, o painel e o titulo nunca entram na caixa');
  const c8 = clique(cantoIcone.children[0], 992, 36);
  checar(c8.parou && c8.parouToque && c8.prevenido && cantoIcone.children[0].clicks === 0, '48c o clique por coordenada no X do canto morre na captura (click e pointerdown), seja qual for o alvo');
  const c8b = clique(gaveta, 1010, 8);
  checar(c8b.parou && c8b.parouToque, '48d um toque na caixa cujo alvo e o proprio painel (sem elemento nenhum) tambem morre');
  const c8c = clique(seta.children[0], 628, 78);
  checar(!c8c.parou && !c8c.parouToque, '48e a seta de voltar, por coordenada, passa');
  const c8d = clique(gaveta.querySelector('[aria-label="Enviar"]'), 985, 740);
  checar(!c8d.parou && !c8d.parouToque, '48f o botao de enviar (mesmo x, fora dos 64 px de cima) passa');
  const c8e = clique(cabecalho.children[1], 900, 77);
  checar(!c8e.parou && !c8e.parouToque, '48g o titulo da conversa (centro fora da caixa) nao e escondido e o toque nele passa');
  checar(!cabecalho.children[1].hasAttribute('data-pdv-fechar-canto'), '48h o titulo nao recebe a marca do canto');
  const cab2 = msgs.filter(function (m) { return m.tipo === 'cabecalho'; });
  checar(cab2.length === 2 && Array.isArray(cab2[1].canto) && cab2[1].canto.length >= 2
    && cab2[1].canto.some(function (c) { return c.tag === 'DIV' && c.cls === 'sc-canto' && c.r[0] === 980 && c.r[1] === 24; })
    && cab2[1].canto.some(function (c) { return String(c.tag).toLowerCase() === 'svg'; }),
    '48i a primeira vez que o canto tem algo, uma segunda (e ultima) mensagem "cabecalho" descreve o que foi achado (div e svg, com retangulo) (' + cab2.length + ')');

  // 48j: o canto que o React recriou e repescado pela varredura de 1 s (a mesma funcao), e o X volta a sumir
  const cantoNovo = gaveta.appendChild(new El('div', { className: 'sc-canto-2', rect: { left: 984, top: 20, width: 28, height: 28 } }));
  cantoNovo.appendChild(new El('svg', { rect: { left: 988, top: 24, width: 20, height: 20 } }));
  sb.pdvBlindarFechar();
  checar(cantoNovo.hasAttribute('data-pdv-fechar-canto'), '48j o canto recriado pelo React e marcado de novo na passada seguinte');
  const cab3 = msgs.filter(function (m) { return m.tipo === 'cabecalho'; });
  checar(cab3.length === 2, '48k e nao ha terceira mensagem "cabecalho" (no maximo duas por carga)');

  // 48l: a conversa aberta SEM o titulo "Conversas" (pdvGaveta nulo): o painel e achado subindo do cabecalho, e o canto some
  montarGaveta({ xNoCanto: true, setaSemRotulo: true });
  gavetaVisivel = false;
  const n8l = sb.pdvBlindarFechar();
  checar(n8l >= 1 && canto.hasAttribute('data-pdv-fechar-canto'), '48l com pdvGaveta nulo (titulo "Conversas" fora da tela) o painel e achado subindo do cabecalho e o X do canto some (' + n8l + ')');
  checar(clique(cantoIcone, 992, 36).parou, '48m e o toque por coordenada no canto morre do mesmo jeito');
  gavetaVisivel = true;

  // 48n: nosso proprio botao ("Abrir conversas", id pdv-...) nunca e escondido nem barrado, mesmo no canto
  montarGaveta({ xNoCanto: true, setaSemRotulo: true });
  const pill = body.appendChild(new El('button', { id: 'pdv-conversas-pill', rect: { left: 970, top: 18, width: 40, height: 40 } }));
  sb.pdvBlindarFechar();
  checar(!pill.hasAttribute('data-pdv-fechar-canto') && !clique(pill, 990, 38).parou, '48n o que e nosso (id pdv-...) no canto nao some nem e barrado');

  // 48o: sem painel nenhum (pagina de login, Gestor sem conversa): nada quebra e nada e barrado
  body.children = [];
  gavetaVisivel = false;
  let n8o = -1, estourou = false;
  try { n8o = sb.pdvBlindarFechar(); } catch (e) { estourou = true; }
  const semNada = new El('div', { rect: { left: 990, top: 30, width: 20, height: 20 } });
  body.appendChild(semNada);
  checar(!estourou && n8o === 0 && !clique(semNada, 1000, 40).parou && !semNada.hasAttribute('data-pdv-fechar-canto'), '48o sem painel achado nada quebra: 0 marcados e o toque no canto da tela passa');
  gavetaVisivel = true;

  // 50: a mutação do DOM re-marca (o React re-renderiza)
  montarGaveta();
  sb.__observador([]);
  setTimeout(function () {
    checar(x.hasAttribute('data-pdv-fechar-painel'), '50 depois de uma mutação do DOM o X recriado é marcado de novo (debounce)');

    // 60: a vigia da gaveta (1.0.43, reescrita contra o laco da Savassi)
    function sumir() { body.children = []; gavetaVisivel = false; }
    function voltar(opts) { montarGaveta(opts); gavetaVisivel = true; }
    function pf() { return msgs.filter(function (m) { return m.tipo === 'painelfechou'; }); }
    const v0 = sb.pdvVigiarPainel();
    checar(v0 === 'aberta' && aquecimentos === 0, '60 com a gaveta na tela a vigia só re-blinda');
    // 60b: o estado LEGITIMO da Savassi: a conversa aberta, o titulo "Conversas" fora (pdvGaveta nulo), o cabecalho na tela
    gavetaVisivel = false;
    checar(sb.pdvVigiarPainel() === 'conversa_aberta' && aquecimentos === 0, '60b conversa aberta (cabecalho na tela, pdvGaveta nulo) e painel ABERTO: nada de reabrir (o laco da Savassi)');
    gavetaVisivel = true;
    // 61: o painel sumiu logo depois de um toque do operador (os cliques de cima foram agora): e ele que saiu
    sumir();
    checar(sb.pdvVigiarPainel() === 'operador_clicou' && aquecimentos === 0, '61 sumiu com toque do operador nos ultimos 15 s: nao reabre');
    // 62: o operador na tela do chat: a vigia nao mexe
    agora += 20000; voltar(); sb.pdvVigiarPainel(); sumir();
    sb.__pdvOperadorNoChat = true;
    checar(sb.pdvVigiarPainel() === 'operador_na_tela' && aquecimentos === 0, '62 com o operador na tela do chat (IsHitTestVisible) a vigia nao mexe');
    sb.__pdvOperadorNoChat = false;
    sb.__pdvSemHolofote = true;
    checar(sb.pdvVigiarPainel() === 'gestor_inteiro' && aquecimentos === 0, '62b com o Gestor inteiro pedido pelo operador, não reabre');
    sb.__pdvSemHolofote = false;
    classes.delete('pdv-so-chat');
    checar(sb.pdvVigiarPainel() === 'fora_do_chat' && aquecimentos === 0, '62c fora do modo "só o chat", não reabre');
    classes.add('pdv-so-chat');
    // 63: sumiu sem toque, estava na tela ha menos de 10 s: reabre pelo caminho do aquecimento e avisa a tela
    const v1 = sb.pdvVigiarPainel();
    checar(v1 === 'reabriu:abriu' && aquecimentos === 1 && pf().length === 1 && pf()[0].resultado === 'abriu' && pf()[0].hora === 1,
      '63 a gaveta sumiu sem o operador sair: reabre pelo caminho do aquecimento e avisa a tela (painelfechou, 1 na hora)');
    agora += 2000;
    checar(sb.pdvVigiarPainel() === 'esperando' && aquecimentos === 1, '64 2 s depois ainda sumida: espera (teto de 1 reabertura a cada 5 min)');
    agora += 300000;
    checar(sb.pdvVigiarPainel() === 'sumiu_ha_muito' && aquecimentos === 1, '65 5 min depois, sumida desde entao (estava na tela ha mais de 10 s): nao reabre');
    // 66: voltou e sumiu de novo (sem toque): a segunda reabertura, ja passados 5 min
    voltar(); checar(sb.pdvVigiarPainel() === 'aberta', '66 a gaveta voltou: a vigia descansa');
    agora += 2000; sumir();
    const v2 = sb.pdvVigiarPainel();
    checar(v2 === 'reabriu:abriu' && aquecimentos === 2 && pf().length === 2 && pf()[1].hora === 2, '66b sumiu de novo, 5 min depois da primeira: segunda reabertura (2 na hora)');
    // 67: a segunda reabertura tambem nao pegou (voltou e sumiu em menos de 30 s): a vigia se desliga ate a proxima navegacao
    agora += 2000; voltar(); checar(sb.pdvVigiarPainel() === 'aberta', '67 a lista reaberta aparece');
    agora += 4000; sumir();
    const v3 = sb.pdvVigiarPainel();
    const vd = msgs.filter(function (m) { return m.tipo === 'vigiadesligada'; });
    checar(v3 === 'desligada' && aquecimentos === 2 && vd.length === 1 && vd[0].falhas === 2, '67b duas reaberturas seguidas que nao pegaram: desligada (laco), com o aviso vigiadesligada (' + v3 + ')');
    agora += 400000;
    checar(sb.pdvVigiarPainel() === 'desligada' && aquecimentos === 2, '67c e continua desligada, mesmo passados os 5 min');
    voltar(); agora += 2000; sumir(); agora += 2000;
    checar(sb.pdvVigiarPainel() === 'desligada' && aquecimentos === 2, '67d o painel que volta e some de novo nao a religa (so a proxima navegacao)');
    const texto = JSON.stringify(msgs);
    checar(texto.indexOf('Maluma') < 0 && texto.indexOf('raspadinha') < 0, '68 o aviso para a tela não leva nome de cliente nem texto da conversa');

    // 70: carregado duas vezes não duplica
    const antes = intervalos.length;
    vm.runInContext(codigo, sb);
    checar(intervalos.length === antes, '70 o script injetado duas vezes na mesma página não cria segunda vigia');
    console.log(JSON.stringify(res));
  }, 500);
})();
""";
}
