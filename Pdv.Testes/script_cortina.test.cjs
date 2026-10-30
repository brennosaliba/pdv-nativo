'use strict';
// A CORTINA DO CHAT NO NODE (caixa 1.0.47 e TV 2.0.24, 22/10/2026). O Gestor de Pedidos 9.349 mudou e a Savassi
// ficou presa no "Abrindo o chat..." do caixa; "Gestor inteiro" nao a tirava. Aqui o ScriptPainel (a constante
// de Telas/ChatIfood.xaml.cs no caixa, ScriptsDoChatPainel.cs na TV) roda contra um DOM falso com RELOGIO FALSO:
//  . a cortina sobe na carga e cai sozinha em 20 s, avisando o C# (tipo cortina) quando o chat nao isolou;
//  . o "Gestor inteiro" do C# (JsGestorInteiro, o segundo arquivo) a derruba na hora e ela nao volta;
//  . a gaveta NOVA (titulo "Conversas" num p, nao num h1) e a VELHA (h1) sao achadas e isoladas;
//  . o botao do cabecalho e achado pelo rotulo "Conversas com clientes" e pelo data-testid do chat novo;
//  . a tela de login nunca ganha cortina; o chat isolado antes dos 20 s nao gera o aviso.
// Uso: node script_cortina.test.cjs <script-painel.js> [js-gestor-inteiro.js]
// Saida: a ultima linha e um JSON [{ok, nome}, ...]; sai com 0 se tudo ok.
const fs = require('fs');
const vm = require('vm');
const codigo = fs.readFileSync(process.argv[2], 'utf8');
const jsGestorInteiro = process.argv[3] ? fs.readFileSync(process.argv[3], 'utf8') : '';
const res = [];
function checar(ok, nome) { res.push({ ok: !!ok, nome: nome }); }

// ── o DOM falso ──────────────────────────────────────────────────────────────
function Texto(t) { this.nodeType = 3; this.textContent = String(t); this.parentElement = null; }
function Estilo() {
  const s = {};
  s.setProperty = function (n, v) { s[n] = v; };
  s.removeProperty = function (n) { delete s[n]; };
  return s;
}
let seq = 0;
function El(tag, o) {
  o = o || {};
  this.nodeType = 1;
  this.tagName = tag.toUpperCase();
  this.id = o.id || '';
  this.className = o.className || '';
  this._attrs = Object.assign({}, o.attrs || {});
  this._rect = o.rect || { left: 0, top: 0, width: 0, height: 0 };
  this.childNodes = [];
  this.parentElement = null;
  this.clicks = 0;
  this.style = Estilo();
  this.scrollHeight = o.scrollHeight || 0;
  this.clientHeight = o.clientHeight || 0;
  this.value = '';
  this._seq = seq++;
  if (o.text) this.childNodes.push(new Texto(o.text));
}
Object.defineProperty(El.prototype, 'children', { get: function () { return this.childNodes.filter(function (n) { return n.nodeType === 1; }); } });
Object.defineProperty(El.prototype, 'classList', {
  get: function () {
    const self = this;
    function lista() { return self.className.split(/\s+/).filter(Boolean); }
    return {
      contains: function (c) { return lista().indexOf(c) >= 0; },
      add: function (c) { if (lista().indexOf(c) < 0) self.className = (self.className + ' ' + c).trim(); },
      remove: function (c) { self.className = lista().filter(function (x) { return x !== c; }).join(' '); },
    };
  },
});
Object.defineProperty(El.prototype, 'textContent', {
  get: function () { return this.childNodes.map(function (n) { return n.textContent; }).join(''); },
  set: function (v) { this.childNodes = [new Texto(v)]; },
});
Object.defineProperty(El.prototype, 'innerText', { get: function () { return this.textContent; } });
El.prototype.appendChild = function (c) { if (c.parentElement) c.parentElement.removeChild(c); c.parentElement = this; this.childNodes.push(c); return c; };
El.prototype.removeChild = function (c) { const i = this.childNodes.indexOf(c); if (i >= 0) this.childNodes.splice(i, 1); c.parentElement = null; return c; };
El.prototype.remove = function () { if (this.parentElement) this.parentElement.removeChild(this); };
El.prototype.getAttribute = function (n) {
  if (n === 'class') return this.className;
  if (n === 'id') return this.id;
  return Object.prototype.hasOwnProperty.call(this._attrs, n) ? this._attrs[n] : null;
};
El.prototype.setAttribute = function (n, v) { if (n === 'class') this.className = String(v); else if (n === 'id') this.id = String(v); else this._attrs[n] = String(v); };
El.prototype.removeAttribute = function (n) { delete this._attrs[n]; };
El.prototype.hasAttribute = function (n) { return Object.prototype.hasOwnProperty.call(this._attrs, n); };
El.prototype.getBoundingClientRect = function () { const r = this._rect; return { left: r.left, top: r.top, width: r.width, height: r.height, right: r.left + r.width, bottom: r.top + r.height }; };
El.prototype.contains = function (x) { let n = x; while (n) { if (n === this) return true; n = n.parentElement; } return false; };
El.prototype.descendentes = function () { const out = []; (function walk(n) { n.children.forEach(function (c) { out.push(c); walk(c); }); })(this); return out; };
El.prototype.click = function () { this.clicks++; };
El.prototype.focus = function () {};
El.prototype.blur = function () {};
El.prototype.dispatchEvent = function () { return true; };
El.prototype.addEventListener = function () {};
El.prototype.removeEventListener = function () {};
El.prototype.querySelectorAll = function (sel) { const raiz = this; return this.descendentes().filter(function (e) { return casa(e, sel, raiz); }); };
El.prototype.querySelector = function (sel) { return this.querySelectorAll(sel)[0] || null; };
El.prototype.closest = function (sel) { let n = this; while (n && n.nodeType === 1) { if (casa(n, sel, null)) return n; n = n.parentElement; } return null; };
El.prototype.matches = function (sel) { return casa(this, sel, null); };

// seletores: tag, #id, .classe, [attr], [attr=v], [attr*=v], [attr^=v], [attr$=v], compostos, descendentes e virgulas
function parseCompound(s) {
  const m = /^([a-zA-Z0-9*-]*)((?:#[\w-]+|\.[\w-]+|\[[^\]]+\])*)$/.exec(s);
  if (!m) return null;
  return { tag: m[1], partes: m[2].match(/#[\w-]+|\.[\w-]+|\[[^\]]+\]/g) || [] };
}
function casaCompound(el, c) {
  if (c.tag && c.tag !== '*' && el.tagName !== c.tag.toUpperCase()) return false;
  for (const p of c.partes) {
    if (p[0] === '#') { if (el.id !== p.slice(1)) return false; }
    else if (p[0] === '.') { if (el.className.split(/\s+/).indexOf(p.slice(1)) < 0) return false; }
    else {
      const am = /^\[([^=*^$~\]]+)(?:([*^$]?)=["']?([^"'\]]*)["']?)?\]$/.exec(p);
      if (!am) return false;
      const v = el.getAttribute(am[1].trim());
      if (am[3] === undefined) { if (v === null) return false; continue; }
      if (v === null) return false;
      if (am[2] === '*') { if (v.indexOf(am[3]) < 0) return false; }
      else if (am[2] === '^') { if (v.indexOf(am[3]) !== 0) return false; }
      else if (am[2] === '$') { if (v.slice(-am[3].length) !== am[3]) return false; }
      else if (v !== am[3]) return false;
    }
  }
  return true;
}
function casaUm(el, sel, raiz) {
  const comps = sel.trim().split(/\s+/).map(parseCompound);
  if (comps.some(function (c) { return !c; })) return false;
  if (!casaCompound(el, comps[comps.length - 1])) return false;
  let n = el.parentElement;
  for (let i = comps.length - 2; i >= 0; i--) {
    while (n && n !== raiz && !casaCompound(n, comps[i])) n = n.parentElement;
    if (!n || n === raiz) return false;
    n = n.parentElement;
  }
  return true;
}
function casa(el, sel, raiz) { return String(sel).split(',').some(function (s) { return s.trim() && casaUm(el, s, raiz); }); }

// ── um mundo: contexto novo, relogio falso, DOM montado por quem chama, script carregado ──
function mundo(montar) {
  let agora = 0, seqT = 1;
  const timers = new Map();
  function setTimeout_(fn, ms) { const id = seqT++; timers.set(id, { fn: fn, due: agora + (ms || 0), rep: 0 }); return id; }
  function setInterval_(fn, ms) { const id = seqT++; timers.set(id, { fn: fn, due: agora + (ms || 1), rep: ms || 1 }); return id; }
  function clear_(id) { timers.delete(id); }
  function avancar(ms) {
    const fim = agora + ms;
    for (;;) {
      let prox = null, pid = null;
      for (const [id, t] of timers) if (t.due <= fim && (!prox || t.due < prox.due || (t.due === prox.due && id < pid))) { prox = t; pid = id; }
      if (!prox) break;
      agora = prox.due;
      if (prox.rep) prox.due = agora + prox.rep; else timers.delete(pid);
      try { prox.fn(); } catch (e) { /* o script engole os proprios erros; aqui tambem */ }
    }
    agora = fim;
  }
  const html = new El('html');
  const head = html.appendChild(new El('head'));
  const body = html.appendChild(new El('body', { rect: { left: 0, top: 0, width: 1366, height: 768 } }));
  const ouvintes = {};
  const mensagens = [];
  const document = {
    nodeType: 9, body: body, head: head, documentElement: html, readyState: 'complete', activeElement: null,
    addEventListener: function (tipo, fn) { (ouvintes[tipo] = ouvintes[tipo] || []).push(fn); },
    querySelectorAll: function (s) { return html.querySelectorAll(s); },
    querySelector: function (s) { return html.querySelector(s); },
    getElementById: function (id) { return html.descendentes().find(function (e) { return e.id === id; }) || null; },
    createElement: function (tag) { return new El(tag); },
    elementFromPoint: function () { return null; },
    elementsFromPoint: function () { return []; },
    execCommand: function () { return false; },
  };
  const ctx = {
    document: document,
    navigator: { clipboard: null, userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/151.0.0.0 Safari/537.36', serviceWorker: null },
    location: { href: 'https://gestordepedidos.ifood.com.br/', hash: '' },
    setTimeout: setTimeout_, clearTimeout: clear_, setInterval: setInterval_, clearInterval: clear_,
    getComputedStyle: function (el) { return { overflowY: 'visible', display: (el && el.style && el.style.display) || 'block' }; },
    MutationObserver: function () { this.observe = function () {}; this.disconnect = function () {}; },
    Event: function (t) { this.type = t; },
    HTMLInputElement: { prototype: {} }, HTMLTextAreaElement: { prototype: {} },
    innerWidth: 1366, innerHeight: 768,
    console: console, JSON: JSON, Date: Date,
    chrome: { webview: { postMessage: function (s) { try { mensagens.push(JSON.parse(s)); } catch (e) { mensagens.push({ tipo: '?', bruto: String(s) }); } } } },
    addEventListener: function (tipo, fn) { (ouvintes['window:' + tipo] = ouvintes['window:' + tipo] || []).push(fn); },
  };
  ctx.window = ctx;
  ctx.self = ctx;
  vm.createContext(ctx);
  const partes = montar(body, El) || {};
  vm.runInContext(codigo, ctx, { filename: 'script-painel.js' });
  return {
    ctx: ctx, body: body, avancar: avancar, mensagens: mensagens, partes: partes, El: El,
    cortina: function () { return document.getElementById('pdv-cortina'); },
    roda: function (js) { try { return vm.runInContext(js, ctx); } catch (e) { return 'erro: ' + (e && e.message); } },
    das: function (tipo) { return mensagens.filter(function (m) { return m && m.tipo === tipo; }); },
  };
}

// a CASCA do Gestor (o spinner vermelho e branco em fundo cinza) e o root vazio: o app nao montou
function casca(body, El) {
  const ini = body.appendChild(new El('div', { className: 'Initial', rect: { left: 0, top: 0, width: 1366, height: 768 } }));
  const root = body.appendChild(new El('div', { id: 'root' }));
  return { ini: ini, root: root };
}
// o Gestor montado com o cabecalho (botao do chat) e o miolo da pagina
function gestor(body, El, opts) {
  opts = opts || {};
  const root = body.appendChild(new El('div', { id: 'root', rect: { left: 0, top: 0, width: 1366, height: 768 } }));
  const topo = root.appendChild(new El('header', { rect: { left: 0, top: 0, width: 1366, height: 56 } }));
  const botao = topo.appendChild(new El('button', { attrs: opts.botaoAttrs || { 'aria-label': 'Conversas com clientes' }, rect: { left: 1300, top: 8, width: 40, height: 40 } }));
  if (opts.icone) botao.appendChild(new El('svg', { className: opts.icone, rect: { left: 1308, top: 16, width: 24, height: 24 } }));
  const miolo = root.appendChild(new El('main', { rect: { left: 0, top: 56, width: 1366, height: 712 } }));
  miolo.appendChild(new El('div', { text: 'Painel de Expedição', rect: { left: 20, top: 80, width: 600, height: 400 } }));
  return { root: root, topo: topo, botao: botao, miolo: miolo };
}
// a gaveta de conversas: velha (titulo h1) ou nova (titulo num p do design system, com o separador dentro)
function gaveta(root, El, nova) {
  const g = root.appendChild(new El('div', { className: nova ? 'drawer-3f9' : 'ifdl-drawer', rect: { left: 886, top: 0, width: 480, height: 768 } }));
  const cab = g.appendChild(new El('div', { className: 'drawer-header', rect: { left: 886, top: 0, width: 480, height: 56 } }));
  let titulo;
  if (nova) {
    titulo = cab.appendChild(new El('p', { className: 'title-x1', rect: { left: 900, top: 16, width: 120, height: 24 } }));
    titulo.appendChild(new El('span', { className: 'separator', text: '/', rect: { left: 900, top: 16, width: 8, height: 24 } }));
    titulo.childNodes.push(new Texto('Conversas'));   // o texto proprio do p, depois do separador
  } else {
    titulo = cab.appendChild(new El('h1', { text: 'Conversas', rect: { left: 900, top: 16, width: 120, height: 24 } }));
  }
  cab.appendChild(new El('button', { attrs: { 'aria-label': 'Fechar' }, rect: { left: 1320, top: 12, width: 32, height: 32 } }));
  const lista = g.appendChild(new El('div', { className: 'list-container', rect: { left: 886, top: 56, width: 480, height: 712 }, scrollHeight: 2000, clientHeight: 712 }));
  lista.appendChild(new El('div', { attrs: { role: 'listitem' }, text: 'Pedido 1234 Maria', rect: { left: 886, top: 60, width: 480, height: 72 } }));
  return { gaveta: g, titulo: titulo, cab: cab };
}

// ── 1. a casca do Gestor: a cortina sobe, cai em 20 s e avisa ──
{
  const w = mundo(casca);
  checar(!!w.cortina(), 'C-01 na carga a cortina esta na tela (id pdv-cortina)');
  checar(w.cortina() && w.cortina().textContent === 'Abrindo o chat...', 'C-02 o texto da cortina e "Abrindo o chat..."');
  w.avancar(19999);
  checar(!!w.cortina(), 'C-03 aos 19,999 s a cortina ainda esta la (o chat pode carregar tarde)');
  checar(w.das('cortina').length === 0, 'C-04 antes do prazo nenhum aviso de cortina');
  w.avancar(1);
  checar(!w.cortina(), 'C-05 aos 20 s a cortina caiu sozinha (nunca fica para sempre)');
  const av = w.das('cortina');
  checar(av.length === 1 && av[0].motivo === 'tempo' && av[0].ms === 20000, 'C-06 e o C# recebeu UM aviso {tipo:cortina, motivo:tempo, ms:20000}');
  checar(!w.body.classList.contains('pdv-so-chat'), 'C-07 sem chat achado a pagina fica inteira (sem pdv-so-chat)');
  checar(w.roda('window.pdvCortina()') === 'nao', 'C-08 pdvCortina() diz "nao" (caiu, mas nao esta desligada: pode isolar depois)');
  w.avancar(300000);
  checar(!w.cortina() && w.das('cortina').length === 1, 'C-09 em 5 min a cortina nao volta e o aviso nao se repete');
  checar(w.das('modo').every(function (m) { return m.modo === 'gestor'; }), 'C-10 as tentativas de isolar seguem (modo gestor avisado) sem achar o chat');
}

// ── 2. "Gestor inteiro" do C# derruba a cortina na hora ──
{
  checar(jsGestorInteiro.length > 50, 'G-00 o JsGestorInteiro do C# chegou ao harness');
  const w = mundo(casca);
  w.avancar(3000);
  checar(!!w.cortina(), 'G-01 aos 3 s a cortina esta la');
  w.roda(jsGestorInteiro);
  checar(!w.cortina(), 'G-02 o Gestor inteiro tirou a cortina na hora');
  checar(w.roda('window.pdvCortina()') === 'desligada', 'G-03 e a cortina ficou desligada neste documento');
  checar(w.roda('window.__pdvSemHolofote === true') === true, 'G-04 o holofote saiu (window.__pdvSemHolofote)');
  w.avancar(60000);
  checar(!w.cortina(), 'G-05 ela nao volta depois');
  checar(w.das('cortina').length === 0, 'G-06 sem aviso de prazo: a cortina nao caiu pelo relogio, caiu pelo dono');
  // o Fale com o iFood (que sobe a cortina de novo) respeita o desligamento
  w.roda('window.pdvFaleComIfood("1234")');
  checar(!w.cortina(), 'G-07 nem o Fale com o iFood a levanta com ela desligada');
}

// ── 3. a gaveta NOVA (titulo "Conversas" num p) e isolada e a cortina cai antes do prazo ──
{
  let partes;
  const w = mundo(function (body, El) { partes = gestor(body, El); const g = gaveta(partes.root, El, true); return Object.assign(partes, g); });
  w.avancar(1000);
  checar(w.roda('window.pdvIsolar()') === true, 'N-01 pdvIsolar acha a gaveta nova (titulo "Conversas" num p com separador dentro)');
  checar(!w.cortina(), 'N-02 e a cortina caiu com o chat isolado');
  checar(w.body.classList.contains('pdv-so-chat'), 'N-03 a pagina esta em pdv-so-chat');
  checar(w.partes.miolo.hasAttribute('data-pdv-hide') && w.partes.topo.hasAttribute('data-pdv-hide'), 'N-04 o miolo e o cabecalho do Gestor estao escondidos (data-pdv-hide)');
  checar(!w.partes.gaveta.hasAttribute('data-pdv-hide') && !w.partes.titulo.hasAttribute('data-pdv-hide'), 'N-05 a gaveta e o titulo ficam');
  checar(w.das('modo').some(function (m) { return m.modo === 'chat'; }), 'N-06 o C# soube: modo chat');
  w.avancar(60000);
  checar(w.das('cortina').length === 0, 'N-07 chat isolado antes dos 20 s: nenhum aviso de cortina');
  checar(w.roda('window.pdvGaveta() === window.pdvIsolar.__x || !!window.pdvGaveta()') === true, 'N-08 pdvGaveta (para o ScriptFechar) devolve a gaveta nova');
}

// ── 4. a gaveta VELHA (h1) continua valendo ──
{
  let partes;
  const w = mundo(function (body, El) { partes = gestor(body, El, { icone: 'ifdl-icon-chat' }); const g = gaveta(partes.root, El, false); return Object.assign(partes, g); });
  w.avancar(1000);
  checar(w.body.classList.contains('pdv-so-chat') && !w.cortina(), 'V-01 a gaveta velha (h1 Conversas) e isolada na primeira tentativa (1 s)');
  checar(w.partes.miolo.hasAttribute('data-pdv-hide'), 'V-02 o miolo escondido');
}

// ── 5. o botao do cabecalho: pelo rotulo novo e pelo data-testid do chat novo ──
{
  const w = mundo(function (body, El) { return gestor(body, El, { botaoAttrs: { 'aria-label': 'Conversas com clientes' } }); });
  checar(w.roda('window.pdvAbrirConversas()') === true && w.partes.botao.clicks >= 1, 'B-01 pdvAbrirConversas clica no botao com aria-label "Conversas com clientes"');
  const w2 = mundo(function (body, El) { return gestor(body, El, { botaoAttrs: { 'data-testid': 'consumer-chat-entry-point' } }); });
  checar(w2.roda('window.pdvAbrirConversas()') === true && w2.partes.botao.clicks >= 1, 'B-02 e no botao marcado por data-testid do chat novo (consumer-chat)');
  const w3 = mundo(function (body, El) { return gestor(body, El, { botaoAttrs: {}, icone: 'sendbird-icon-chat' }); });
  checar(w3.roda('window.pdvAbrirConversas()') === true && w3.partes.botao.clicks >= 1, 'B-03 e pelo icone sendbird-icon-chat (classe do UIKit) sem rotulo nenhum');
}

// ── 6. a tela de login nunca ganha cortina ──
{
  const w = mundo(function (body, El) {
    const f = body.appendChild(new El('form', { rect: { left: 400, top: 200, width: 500, height: 300 } }));
    f.appendChild(new El('input', { attrs: { type: 'password' }, rect: { left: 420, top: 260, width: 400, height: 40 } }));
  });
  checar(!w.cortina(), 'L-01 com campo de senha na tela nao sobe cortina');
  w.avancar(30000);
  checar(w.das('cortina').length === 0, 'L-02 e nao ha aviso de prazo');
  checar(w.das('naolidas').some(function (m) { return m.login === true; }), 'L-03 o C# recebe login=true');
}

// ── 7. o chat chega tarde (aos 3 s): a cortina cai quando isola, nao aos 20 s ──
{
  let partes;
  const w = mundo(function (body, El) { partes = gestor(body, El); return partes; });
  w.avancar(3000);
  checar(!!w.cortina(), 'T-01 aos 3 s, sem gaveta, a cortina segue');
  const g = gaveta(w.partes.root, w.El, true);
  w.avancar(1000);  // a tentativa dos 4 s
  checar(!w.cortina() && w.body.classList.contains('pdv-so-chat'), 'T-02 na tentativa seguinte (4 s) a gaveta nova foi isolada e a cortina caiu');
  checar(!!g.gaveta.parentElement, 'T-03 a gaveta continua no DOM');
  w.avancar(60000);
  checar(w.das('cortina').length === 0, 'T-04 sem aviso de prazo');
}

// ── 8. o Fale com o iFood sobe a cortina de novo e ela TAMBEM cai em 20 s ──
{
  const w = mundo(function (body, El) { return gestor(body, El); });
  w.avancar(25000);
  checar(!w.cortina() && w.das('cortina').length === 1, 'F-01 a primeira cortina caiu aos 20 s');
  w.roda('window.pdvFaleComIfood("1234")');
  checar(!!w.cortina(), 'F-02 o Fale com o iFood levantou a cortina de novo');
  w.avancar(20000);
  checar(!w.cortina(), 'F-03 e ela caiu sozinha em 20 s (sem pedido achado)');
}

// ── 9. 1.0.60: a ajuda da impressora (Gestor 9.351) prendia o chat no spinner ──
{
  const w = mundo(function (body, El) { return gestor(body, El); });
  w.ctx.location.hash = '#/home/help?entry=howToConfigurePrinter';
  w.avancar(3500);
  checar(w.ctx.location.hash === '#/home/order-display/expedition', 'H-01 no modo so o chat a pagina sai da ajuda presa e volta para a expedicao');
  checar(w.das('saiudaajuda').length === 1, 'H-02 e avisa o C# uma vez');
  const w2 = mundo(function (body, El) { return gestor(body, El); });
  w2.roda('window.__pdvSemHolofote = true');
  w2.ctx.location.hash = '#/home/help?entry=howToConfigurePrinter';
  w2.avancar(6500);
  checar(w2.ctx.location.hash === '#/home/help?entry=howToConfigurePrinter', 'H-03 com o Gestor inteiro liberado a ajuda fica');
  const w3 = mundo(function (body, El) { return gestor(body, El); });
  w3.roda('window.pdvFaleComIfood("1234")');
  w3.ctx.location.hash = '#/home/help';
  w3.avancar(6500);
  checar(w3.das('saiudaajuda').length === 0, 'H-04 logo depois do Fale com o iFood a trava da ajuda nao age (quem manda e o Fale com o iFood)');
  const w4 = mundo(function (body, El) {
    const p = gestor(body, El, { botaoAttrs: {} });
    const aviso = p.topo.appendChild(new El('div', { className: 'printing-feedback-toast', rect: { left: 1316, top: 4, width: 50, height: 48 } }));
    p.link = aviso.appendChild(new El('a', { attrs: { href: '#/home/help?entry=howToConfigurePrinter' }, rect: { left: 1320, top: 10, width: 44, height: 40 } }));
    p.aviso = aviso;
    return p;
  });
  checar(w4.roda('window.pdvAbrirConversas()') === true && w4.partes.botao.clicks >= 1 && w4.partes.aviso.clicks === 0 && w4.partes.link.clicks === 0,
    'H-05 o clique pela posicao acha o botao do chat e nunca o aviso da impressora mais a direita');
}

console.log(JSON.stringify(res));
process.exit(res.every(function (r) { return r.ok; }) ? 0 : 1);
