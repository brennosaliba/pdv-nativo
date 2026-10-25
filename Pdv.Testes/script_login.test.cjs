'use strict';
// ══════════════════════════════════════════════════════════════════════════════════════════════════
//  O LOGIN DO GESTOR DE PEDIDOS NUM DOM FALSO (25/10/2026, Gestor 9.350 / partners-auth.ifood.com.br)
//
//  Roda o ScriptLogin (a constante de Telas/ChatIfood.xaml.cs no caixa; ScriptsDoChatPainel.Login na TV)
//  contra as telas do login do iFood como o bundle publico do tema Keycloak as monta
//  (partners-auth-default-theme, lido em 25/10/2026, SEM logar):
//    login-username: form#login-page-form, input#username[name=username][type=text][autocomplete=email],
//                    hidden deviceHash/deviceId, button[type=submit][name=login] "Continuar"
//    login-password: form#kc-form-login, input#password[name=password], input#kc-login[type=submit][value=Entrar]
//    login (as duas): form#login-page-form com #username (disabled quando ja veio) e #password
//    select-otp-method: radios input[name=otpChannel][value=EMAIL|PHONE|WHATSAPP] em label[for], "Continuar"
//    enter-otp: OTPInput = 6 inputs[data-is-otp-input][maxlength=6][inputmode=numeric][autocomplete=one-time-code]
//               + input[hidden][data-is-otp-hidden-input][name=otpCode]; o valor inteiro entra pela caixa em foco
//    enforce-mfa-method (cadastro da verificacao), login-page-expired e error (so o Voltar)
//  e tambem: um captcha na tela, a tela do Gestor logado, a casca do Gestor carregando.
//
//    node script_login.test.cjs <script-login.js>
//  Saida: a ultima linha e um JSON [{ok, nome}] que o C# le (o resto e livre).
// ══════════════════════════════════════════════════════════════════════════════════════════════════
const fs = require('fs');
const vm = require('vm');
const codigo = fs.readFileSync(process.argv[2], 'utf8');
const res = [];
function checar(ok, nome) { res.push({ ok: !!ok, nome: nome }); if (!ok) console.error('FALHA ' + nome); }

let seq = 0;
function El(tag, opts) {
  opts = opts || {};
  this.tagName = tag.toUpperCase();
  this.id = opts.id || '';
  this._attrs = Object.assign({}, opts.attrs || {});
  if (this.id) this._attrs.id = this.id;
  this._text = opts.text || '';
  this._rect = opts.rect || { left: 0, top: 0, width: 0, height: 0 };
  this.children = [];
  this.parentElement = null;
  this.clicks = 0; this.focos = 0; this.eventos = []; this._value = opts.value || ''; this.checked = !!opts.checked;
  this.hidden = !!opts.hidden;
  this.aoInput = null; // o "React" da tela: roda no evento input
  this._seq = seq++;
}
El.prototype.appendChild = function (c) { c.parentElement = this; this.children.push(c); return c; };
El.prototype.getAttribute = function (n) { return Object.prototype.hasOwnProperty.call(this._attrs, n) ? this._attrs[n] : null; };
El.prototype.setAttribute = function (n, v) { this._attrs[n] = String(v); };
El.prototype.hasAttribute = function (n) { return Object.prototype.hasOwnProperty.call(this._attrs, n); };
El.prototype.getBoundingClientRect = function () { const r = this.hidden ? { left: 0, top: 0, width: 0, height: 0 } : this._rect; return { left: r.left, top: r.top, width: r.width, height: r.height, right: r.left + r.width, bottom: r.top + r.height }; };
Object.defineProperty(El.prototype, 'textContent', {
  get: function () { let t = this._text; this.children.forEach(function (c) { t += ' ' + c.textContent; }); return t; },
  set: function (v) { this._text = String(v); this.children = []; },
});
Object.defineProperty(El.prototype, 'value', { get: function () { return this._value; }, set: function (v) { this._value = String(v); } });
Object.defineProperty(El.prototype, 'type', { get: function () { return this.getAttribute('type') || (this.tagName === 'INPUT' ? 'text' : ''); } });
Object.defineProperty(El.prototype, 'maxLength', { get: function () { const m = this.getAttribute('maxlength'); return m === null ? -1 : parseInt(m, 10); } });
Object.defineProperty(El.prototype, 'disabled', { get: function () { return this.hasAttribute('disabled'); } });
Object.defineProperty(El.prototype, 'form', { get: function () { let n = this.parentElement; while (n) { if (n.tagName === 'FORM') return n; n = n.parentElement; } return null; } });
El.prototype.contains = function (x) { let n = x; while (n) { if (n === this) return true; n = n.parentElement; } return false; };
El.prototype.descendentes = function () { const out = []; (function walk(n) { n.children.forEach(function (c) { out.push(c); walk(c); }); })(this); return out; };
El.prototype.click = function () {
  this.clicks++;
  if (this.tagName === 'LABEL' && this.getAttribute('for')) { const alvo = document.getElementById(this.getAttribute('for')); if (alvo) { alvo.clicks++; alvo.checked = true; } }
  if (this.type === 'radio') this.checked = true;
};
El.prototype.focus = function () { this.focos++; if (this.aoFoco) this.aoFoco(); };
El.prototype.dispatchEvent = function (ev) { this.eventos.push(ev.type); if (ev.type === 'input' && this.aoInput) this.aoInput(this); return true; };
El.prototype.requestSubmit = function () { this.clicks++; };
El.prototype.submit = function () { this.clicks++; };
function casaUm(el, sel) {
  sel = sel.trim();
  if (sel === '*') return true;
  const m = /^([a-zA-Z0-9]*)(#[\w-]+)?((?:\[[^\]]+\])*)$/.exec(sel);
  if (!m) throw new Error('seletor nao suportado pelo harness: ' + sel);
  if (m[1] && el.tagName !== m[1].toUpperCase()) return false;
  if (m[2] && el.id !== m[2].slice(1)) return false;
  const attrs = m[3].match(/\[[^\]]+\]/g) || [];
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
const document = {
  body: body, documentElement: body, readyState: 'complete',
  querySelectorAll: function (s) { return body.querySelectorAll(s); },
  querySelector: function (s) { return body.querySelector(s); },
  getElementById: function (id) { return body.descendentes().find(function (e) { return e.id === id; }) || null; },
  createElement: function (tag) { return new El(tag); },
  addEventListener: function () {},
};
function Event(tipo, opts) { this.type = tipo; this.bubbles = !!(opts && opts.bubbles); }
function KeyboardEvent(tipo, opts) { this.type = tipo; this.key = opts && opts.key; this.bubbles = !!(opts && opts.bubbles); }
// o setter nativo do input (o script o chama antes de disparar o input, como o React quer)
function HTMLInputElement() {}
Object.defineProperty(HTMLInputElement.prototype, 'value', { get: function () { return this._value; }, set: function (v) { this._value = String(v); }, configurable: true });
const R = function (l, t, w, h) { return { left: l, top: t, width: w, height: h }; };
const sb = {
  console: console, document: document, Event: Event, KeyboardEvent: KeyboardEvent, HTMLInputElement: HTMLInputElement,
  location: { href: 'https://gestordepedidos.ifood.com.br/', host: 'gestordepedidos.ifood.com.br' },
  setTimeout: function (fn) { fn(); return 1; },
  kcContext: undefined,
};
sb.window = sb; sb.self = sb;
vm.createContext(sb);
vm.runInContext(codigo, sb);

const AUTH = 'partners-auth.ifood.com.br';
const RESTART = '/realms/partners/login-actions/restart?client_id=order-manager&tab_id=x';
function auth(pageId, message) {
  sb.location.host = AUTH;
  sb.location.href = 'https://' + AUTH + '/realms/partners/login-actions/authenticate?client_id=order-manager&tab_id=x&execution=y';
  sb.kcContext = { pageId: pageId, url: { loginRestartFlowUrl: RESTART }, message: message || null };
  body.children = [];
}
function gestor() {
  sb.location.host = 'gestordepedidos.ifood.com.br';
  sb.location.href = 'https://gestordepedidos.ifood.com.br/#/orders';
  sb.kcContext = undefined;
  body.children = [];
}

// ── as telas, como o tema as monta ──
let username, continuar, formU, voltar, password, entrar, formS, radios, otpInstancias, otpOculto, formO, reenviar, ajuda;
function cabecalho(titulo) {
  const h = body.appendChild(new El('header', { rect: R(0, 0, 1024, 80) }));
  h.appendChild(new El('img', { attrs: { alt: 'iFood' }, rect: R(20, 20, 80, 40) }));
  const b = body.appendChild(new El('div', { rect: R(312, 120, 400, 500) }));
  b.appendChild(new El('h1', { text: titulo, rect: R(312, 120, 400, 40) }));
  return b;
}
function telaUsername(opts) {
  opts = opts || {};
  auth('login-username.ftl', opts.message);
  const b = cabecalho('Entre na sua conta');
  formU = b.appendChild(new El('form', { id: 'login-page-form', attrs: { method: 'post', action: '/realms/partners/login-actions/authenticate' }, rect: R(312, 170, 400, 300) }));
  formU.appendChild(new El('input', { attrs: { type: 'hidden', name: 'webAuthnPlatformAuthenticatorSupported', value: 'false' }, hidden: true }));
  formU.appendChild(new El('input', { attrs: { type: 'hidden', name: 'deviceHash', value: 'abc' }, hidden: true }));
  if (opts.message) b.appendChild(new El('div', { attrs: { 'aria-live': 'polite' }, text: opts.message.summary, rect: R(312, 170, 400, 30) }));
  formU.appendChild(new El('label', { text: 'E-mail ou nome de usuário', attrs: { for: 'username' }, rect: R(312, 200, 400, 20) }));
  username = formU.appendChild(new El('input', { id: 'username', attrs: { type: 'text', name: 'username', autocomplete: 'email', placeholder: 'nome@email.com.br' }, rect: R(312, 220, 400, 48) }));
  const botoes = formU.appendChild(new El('div', { rect: R(312, 300, 400, 120) }));
  continuar = botoes.appendChild(new El('button', { text: 'Continuar', attrs: { type: 'submit', name: 'login', form: 'login-page-form' }, rect: R(312, 300, 400, 48) }));
  if (opts.passkey) botoes.appendChild(new El('button', { id: 'authenticateWebAuthnButton', text: 'Entrar com chave de acesso', attrs: { type: 'button' }, rect: R(312, 360, 400, 48) }));
  const rodape = b.appendChild(new El('div', { attrs: { 'data-testid': 'footer-do-need-help-accessing' }, rect: R(312, 440, 400, 30) }));
  ajuda = rodape.appendChild(new El('a', { text: 'Preciso de ajuda para acessar', attrs: { href: '/realms/partners/login-actions/reset-credentials?x' }, rect: R(312, 440, 300, 24) }));
  if (opts.captcha) b.appendChild(new El('iframe', { attrs: { src: 'https://www.google.com/recaptcha/api2/anchor?k=xyz', title: 'reCAPTCHA' }, rect: R(312, 480, 300, 78) }));
}
function telaPassword(opts) {
  opts = opts || {};
  auth('login-password.ftl', opts.message);
  const b = cabecalho('Entre na sua conta');
  const kc = b.appendChild(new El('div', { id: 'kc-form', rect: R(312, 170, 400, 300) }));
  formS = kc.appendChild(new El('form', { id: 'kc-form-login', attrs: { method: 'post', action: '/x' }, rect: R(312, 170, 400, 300) }));
  formS.appendChild(new El('label', { text: 'Senha', attrs: { for: 'password' }, rect: R(312, 200, 400, 20) }));
  password = formS.appendChild(new El('input', { id: 'password', attrs: { type: 'password', name: 'password', autocomplete: 'on', tabindex: '2' }, rect: R(312, 220, 360, 48) }));
  formS.appendChild(new El('button', { attrs: { type: 'button', 'aria-label': 'Mostrar senha', 'aria-controls': 'password' }, rect: R(676, 220, 36, 48) }));
  if (opts.message) formS.appendChild(new El('span', { id: 'input-error-password', attrs: { 'aria-live': 'polite' }, text: opts.message.summary, rect: R(312, 270, 400, 20) }));
  formS.appendChild(new El('a', { text: 'Esqueceu sua senha?', attrs: { href: '/reset', tabindex: '5' }, rect: R(312, 300, 200, 20) }));
  entrar = formS.appendChild(new El('input', { id: 'kc-login', attrs: { type: 'submit', name: 'login', value: 'Entrar', tabindex: '4' }, rect: R(312, 340, 400, 48) }));
}
function telaLoginJunta() {
  auth('login.ftl');
  const b = cabecalho('Entre na sua conta');
  formU = b.appendChild(new El('form', { id: 'login-page-form', attrs: { method: 'post', action: '/x' }, rect: R(312, 170, 400, 400) }));
  username = formU.appendChild(new El('input', { id: 'username', attrs: { type: 'text', name: 'username', autocomplete: 'email', disabled: '' }, value: 'loja@americanday.com.br', rect: R(312, 200, 400, 48) }));
  password = formU.appendChild(new El('input', { id: 'password', attrs: { type: 'password', name: 'password', autocomplete: 'password' }, rect: R(312, 260, 400, 48) }));
  formU.appendChild(new El('input', { id: 'id-hidden-input', attrs: { type: 'hidden', name: 'credentialId' }, hidden: true }));
  continuar = formU.appendChild(new El('button', { text: 'Continuar', attrs: { type: 'submit', name: 'login' }, rect: R(312, 330, 400, 48) }));
  formU.appendChild(new El('a', { text: 'Voltar', attrs: { href: RESTART }, rect: R(312, 390, 400, 48) }));
}
function telaCanal(opts) {
  opts = opts || {};
  auth('select-otp-method.ftl');
  const b = cabecalho('Verificação de 2 etapas');
  const det = b.appendChild(new El('div', { rect: R(312, 170, 400, 400) }));
  formO = det.appendChild(new El('form', { id: 'login-page-form', attrs: { method: 'post', action: '/x' }, rect: R(312, 170, 400, 400) }));
  formO.appendChild(new El('p', { text: 'Siga o passo a passo para a autenticação, mais segurança pra você e sua loja.', rect: R(312, 170, 400, 40) }));
  const grupo = formO.appendChild(new El('div', { attrs: { role: 'group', 'data-testid': 'otp-options' }, rect: R(312, 220, 400, 160) }));
  radios = {};
  const ops = opts.semEmail ? [['PHONE', '(31) *****-1234', 'SMS'], ['WHATSAPP', '(31) *****-1234', 'WhatsApp']]
    : (opts.soEmail ? [['EMAIL', 'l***@americanday.com.br', 'E-mail']]
      : [['PHONE', '(31) *****-1234', 'SMS'], ['EMAIL', 'l***@americanday.com.br', 'E-mail'], ['WHATSAPP', '(31) *****-1234', 'WhatsApp']]);
  ops.forEach(function (o, i) {
    const lab = grupo.appendChild(new El('label', { attrs: { for: o[0] + '-' + o[1] }, rect: R(312, 220 + i * 50, 400, 44) }));
    const r = lab.appendChild(new El('input', { id: o[0] + '-' + o[1], attrs: { type: 'radio', name: 'otpChannel', value: o[0], 'data-testid': 'otp-option__radio-' + o[0] + '-' + o[1] }, checked: i === 0, rect: R(320, 230 + i * 50, 20, 20) }));
    lab.appendChild(new El('span', { text: o[2] + ' ' + o[1], rect: R(350, 230 + i * 50, 300, 20) }));
    radios[o[0]] = { radio: r, label: lab };
  });
  continuar = formO.appendChild(new El('button', { text: 'Continuar', attrs: { type: 'submit', name: 'login' }, rect: R(312, 400, 400, 48) }));
  formO.appendChild(new El('a', { text: 'Voltar', attrs: { type: 'submit', href: RESTART }, rect: R(312, 460, 400, 48) }));
  const rodape = b.appendChild(new El('div', { attrs: { 'data-testid': 'footer-channel-no-access' }, rect: R(312, 520, 400, 30) }));
  rodape.appendChild(new El('a', { text: 'Não tenho mais acesso', attrs: { href: 'https://portal.ifood.com.br/ajuda' }, rect: R(312, 520, 300, 24) }));
}
function telaCodigo(opts) {
  opts = opts || {};
  auth(opts.pageId || 'enter-otp.ftl', opts.message);
  const b = cabecalho('Verifique seu E-mail');
  formO = b.appendChild(new El('form', { id: 'login-page-form', attrs: { method: 'post', action: '/x' }, rect: R(312, 170, 400, 400) }));
  formO.appendChild(new El('p', { text: 'Digite o código de 6 dígitos que enviamos para: l***@americanday.com.br', rect: R(312, 170, 400, 40) }));
  const wrap = formO.appendChild(new El('div', { rect: R(312, 220, 400, 120) }));
  wrap.appendChild(new El('label', { text: 'Código de verificação', rect: R(312, 220, 400, 20) }));
  otpOculto = wrap.appendChild(new El('input', { id: 'otp-x', attrs: { type: 'text', name: 'otpCode', autocomplete: 'one-time-code', tabindex: '-1', 'data-is-otp-hidden-input': 'true' }, hidden: true }));
  const caixa = wrap.appendChild(new El('div', { rect: R(312, 250, 400, 56) }));
  otpInstancias = [];
  let foco = null;
  const estado = []; // o useOTPState do Aboyeur: so insere com uma caixa em foco
  function refletir() { otpOculto._value = estado.join(''); otpInstancias.forEach(function (c, i) { c._value = estado[i] || ''; }); }
  for (let i = 0; i < 6; i++) {
    const inst = caixa.appendChild(new El('input', { attrs: { type: 'input', inputmode: 'numeric', maxlength: '6', autocomplete: 'one-time-code', 'aria-label': 'Código de verificação dígito ' + (i + 1), 'data-is-otp-input': 'true' }, rect: R(312 + i * 66, 250, 56, 56) }));
    inst.aoFoco = function () { foco = i; };
    inst.aoInput = function (el) {
      const v = String(el._value || '').replace(/\D/g, '');
      if (foco === null) { el._value = ''; return; }
      for (let k = 0; k < v.length && foco + k < 6; k++) estado[foco + k] = v.charAt(k);
      foco = Math.min(5, foco + v.length);
      refletir();
    };
    otpInstancias.push(inst);
  }
  if (opts.message) formO.appendChild(new El('span', { attrs: { 'aria-live': 'polite' }, text: opts.message.summary, rect: R(312, 310, 400, 20) }));
  reenviar = formO.appendChild(new El('p', { text: 'Reenviar código (00:45)', rect: R(312, 320, 400, 20) }));
  continuar = formO.appendChild(new El('button', { text: 'Continuar', attrs: { type: 'submit', name: 'login' }, rect: R(312, 360, 400, 48) }));
  formO.appendChild(new El('a', { text: 'Voltar', attrs: { type: 'submit', href: RESTART }, rect: R(312, 420, 400, 48) }));
}
function telaOtpPadrao() {
  auth('login-otp.ftl');
  const b = cabecalho('Autenticador');
  formO = b.appendChild(new El('form', { id: 'kc-otp-login-form', attrs: { method: 'post', action: '/x' }, rect: R(312, 170, 400, 300) }));
  otpInstancias = null; otpOculto = null;
  const campo = formO.appendChild(new El('input', { id: 'otp', attrs: { type: 'text', name: 'otp', autocomplete: 'off' }, rect: R(312, 220, 400, 48) }));
  continuar = formO.appendChild(new El('input', { id: 'kc-login', attrs: { type: 'submit', name: 'login', value: 'Entrar' }, rect: R(312, 300, 400, 48) }));
  return campo;
}
function telaMfaCadastro() {
  auth('enforce-mfa-method.ftl');
  const b = cabecalho('Ative a verificação em duas etapas');
  formO = b.appendChild(new El('form', { id: 'login-page-form', attrs: { role: 'form', method: 'post', action: '/x' }, rect: R(312, 170, 400, 300) }));
  continuar = formO.appendChild(new El('button', { text: 'Continuar', attrs: { type: 'submit' }, rect: R(312, 300, 400, 48) }));
  formO.appendChild(new El('a', { text: 'Voltar', attrs: { href: RESTART }, rect: R(312, 360, 400, 48) }));
}
function telaExpirada(pageId) {
  auth(pageId || 'login-page-expired.ftl');
  const b = cabecalho(pageId === 'error.ftl' ? 'Desculpe, tente novamente' : 'A página expirou');
  b.appendChild(new El('p', { text: 'A página expirou. Volte e tente de novo.', rect: R(312, 170, 400, 40) }));
  voltar = b.appendChild(new El('a', { text: 'Voltar', attrs: { href: RESTART }, rect: R(312, 240, 400, 48) }));
}
function telaLogada() {
  gestor();
  const nav = body.appendChild(new El('nav', { rect: R(0, 0, 1024, 60) }));
  nav.appendChild(new El('button', { text: 'Pedidos', attrs: { type: 'button' }, rect: R(20, 10, 100, 40) }));
  nav.appendChild(new El('button', { attrs: { 'aria-label': 'Conversas com clientes' }, rect: R(960, 10, 44, 40) }));
  const root = body.appendChild(new El('div', { id: 'root', rect: R(0, 60, 1024, 700) }));
  root.appendChild(new El('div', { text: 'Pedidos de hoje', rect: R(0, 80, 1024, 600) }));
  body.appendChild(new El('input', { attrs: { type: 'text', placeholder: 'Buscar pedido', id: 'order-search' }, rect: R(200, 10, 300, 40) }));
}
function telaCasca() {
  gestor();
  body.appendChild(new El('div', { attrs: { class: 'Initial' }, rect: R(0, 0, 1024, 768) }));
  body.appendChild(new El('div', { id: 'root', rect: R(0, 0, 0, 0) }));
}

(function () {
  const S = sb;
  checar(['pdvLoginTela', 'pdvLoginEmail', 'pdvLoginSenha', 'pdvLoginPreencher', 'pdvLoginEscolherEmail', 'pdvLoginDigitar', 'pdvLoginVoltar', 'pdvLoginMensagem']
    .every(function (f) { return typeof S[f] === 'function'; }), '50 o script expoe tela, email, senha, preencher, escolherEmail, digitar, voltar e mensagem');

  // ── 1. o e-mail (login-username) ──
  telaUsername();
  checar(S.pdvLoginTela() === 'email', '51 login-username.ftl com o #username e a tela email (' + S.pdvLoginTela() + ')');
  const e1 = S.pdvLoginEmail('loja@americanday.com.br');
  checar(e1 === 'enviado' && username.value === 'loja@americanday.com.br', '52 preenche o #username e devolve enviado (' + e1 + ')');
  checar(username.focos > 0 && username.eventos.indexOf('input') >= 0, '52a o campo recebe foco e o evento input (o react-hook-form ouve)');
  checar(continuar.clicks === 1 && ajuda.clicks === 0, '52b toca no Continuar (submit name=login), nunca no "Preciso de ajuda"');
  checar(e1.indexOf('@') < 0, '52c a resposta nunca leva o que foi digitado');
  telaUsername({ passkey: true });
  S.pdvLoginEmail('a@b.c');
  checar(continuar.clicks === 1 && body.querySelector('button#authenticateWebAuthnButton').clicks === 0, '53 com o botao da chave de acesso na tela, o Continuar continua sendo o escolhido');
  checar(S.pdvLoginSenha('x') === 'sem_campo', '53a na tela do e-mail nao ha senha para preencher');
  checar(S.pdvLoginPreencher('a@b.c', 'x') === 'enviado' && username.value === 'a@b.c', '53b pdvLoginPreencher na tela do e-mail preenche so o e-mail (o roteiro antigo ainda anda)');

  // ── 2. a senha (login-password, tema padrao do Keycloakify) ──
  telaPassword();
  checar(S.pdvLoginTela() === 'senha', '54 login-password.ftl com o #password e a tela senha (' + S.pdvLoginTela() + ')');
  const s1 = S.pdvLoginSenha('S3nh@!xyz');
  checar(s1 === 'enviado' && password.value === 'S3nh@!xyz' && password.eventos.indexOf('input') >= 0, '55 preenche o #password pelo setter nativo e avisa o input (' + s1 + ')');
  checar(entrar.clicks === 1 && formS.querySelector('button').clicks === 0 && formS.querySelector('a').clicks === 0, '55a toca no #kc-login (Entrar), nunca no Mostrar senha nem no Esqueceu');
  checar(s1.indexOf('S3nh') < 0, '55b a resposta nunca leva a senha');
  checar(S.pdvLoginEmail('a@b.c') === 'sem_campo', '55c na tela da senha nao ha e-mail para preencher');
  telaPassword({ message: { type: 'error', summary: 'E-mail ou nome do usuário ou senha inválidos.' } });
  checar(S.pdvLoginTela() === 'senha', '56 a senha recusada volta para a tela da senha (continua senha)');
  const msg = S.pdvLoginMensagem();
  checar(/inv.lidos/.test(msg) && msg.indexOf('@') < 0, '56a pdvLoginMensagem le o erro da pagina (' + msg + ')');
  telaUsername({ message: { type: 'error', summary: 'Conta bloqueada: ligue para 31 99999-1234 ou escreva para loja@x.com' } });
  const msg2 = S.pdvLoginMensagem();
  checar(msg2.indexOf('99999') < 0 && msg2.indexOf('loja@') < 0 && msg2.indexOf('####') >= 0, '56b a mensagem sai mascarada: sem numero longo, sem e-mail (' + msg2 + ')');

  // ── 3. as duas juntas (login.ftl) ──
  telaLoginJunta();
  checar(S.pdvLoginTela() === 'login', '57 login.ftl com #username e #password e a tela login (' + S.pdvLoginTela() + ')');
  const j1 = S.pdvLoginPreencher('outro@x.com', 'S3nh@!');
  checar(j1 === 'enviado' && username.value === 'loja@americanday.com.br' && password.value === 'S3nh@!' && continuar.clicks === 1,
    '57a preenche a senha, respeita o e-mail travado (disabled) e toca em Continuar (' + j1 + ')');

  // ── 4. a escolha do canal (select-otp-method) ──
  telaCanal();
  checar(S.pdvLoginTela() === 'canal', '58 select-otp-method.ftl com os radios otpChannel e a tela canal (' + S.pdvLoginTela() + ')');
  const c1 = S.pdvLoginEscolherEmail();
  checar(c1 === true && radios.EMAIL.radio.checked && (radios.EMAIL.radio.clicks + radios.EMAIL.label.clicks) >= 1, '58a marca o radio EMAIL (pelo value, nunca pelo texto) (' + c1 + ')');
  checar(radios.PHONE.radio.clicks === 0 && radios.WHATSAPP.radio.clicks === 0 && radios.WHATSAPP.label.clicks === 0, '58b SMS e WhatsApp ficam quietos');
  checar(continuar.clicks === 1 && formO.querySelector('a').clicks === 0, '58c toca em Continuar, nunca em Voltar');
  telaCanal({ soEmail: true });
  checar(S.pdvLoginEscolherEmail() === true && radios.EMAIL.radio.checked && continuar.clicks === 1, '58d so o e-mail na lista: marca e continua');
  telaCanal({ semEmail: true });
  checar(S.pdvLoginTela() === 'canal' && S.pdvLoginEscolherEmail() === false && continuar.clicks === 0, '58e sem a opcao EMAIL devolve false e nao toca em nada (o roteiro desiste e avisa)');

  // ── 5. o codigo (enter-otp com o OTPInput do Aboyeur) ──
  telaCodigo();
  checar(S.pdvLoginTela() === 'codigo', '59 enter-otp.ftl com as caixas [data-is-otp-input] e a tela codigo (' + S.pdvLoginTela() + ')');
  const d1 = S.pdvLoginDigitar('482913');
  checar(d1 === 'enviado' && otpOculto.value === '482913', '59a o codigo inteiro entra pela 1a caixa em foco e o oculto name=otpCode reflete 482913 (' + d1 + ', oculto=' + otpOculto.value + ')');
  checar(otpInstancias.map(function (c) { return c.value; }).join('') === '482913', '59b as seis caixas mostram um digito cada');
  checar(continuar.clicks === 1 && formO.querySelector('a').clicks === 0, '59c toca em Continuar, nunca em Voltar nem no Reenviar');
  telaCodigo({ pageId: 'enforce-mfa-code.ftl' });
  checar(S.pdvLoginTela() === 'codigo' && S.pdvLoginDigitar('123456') === 'enviado' && otpOculto.value === '123456', '59d enforce-mfa-code.ftl (o mesmo OTPInput) tambem e a tela do codigo');
  telaCodigo({ message: { type: 'error', summary: 'Código inválido.' } });
  checar(S.pdvLoginTela() === 'codigo' && /inv.lido/.test(S.pdvLoginMensagem()), '59e codigo recusado: continua na tela do codigo e a mensagem diz');
  const campoOtp = telaOtpPadrao();
  checar(S.pdvLoginTela() === 'codigo' && S.pdvLoginDigitar('4829') === 'enviado' && campoOtp.value === '4829' && continuar.clicks === 1, '59f login-otp.ftl (input#otp do Keycloak) tambem e codigo, e envia pelo #kc-login');
  telaCodigo();
  checar(S.pdvLoginDigitar('12') === 'codigo_invalido' && S.pdvLoginDigitar('abc123') === 'codigo_invalido' && otpOculto.value === '', '59g codigo fora de 4 a 8 digitos nao e digitado');
  telaLogada();
  checar(S.pdvLoginDigitar('482913') === 'sem_campo', '59h sem o campo do codigo devolve sem_campo');

  // ── 6. o que faz o roteiro parar e avisar ──
  telaMfaCadastro();
  checar(S.pdvLoginTela() === 'mfa_cadastro', '60 enforce-mfa-method.ftl (cadastrar a verificacao) e mfa_cadastro: nao se entra sozinho (' + S.pdvLoginTela() + ')');
  telaUsername({ captcha: true });
  checar(S.pdvLoginTela() === 'captcha', '61 um reCAPTCHA na tela de login e captcha: o roteiro desiste e avisa (' + S.pdvLoginTela() + ')');
  checar(S.pdvLoginEmail('a@b.c') === 'enviado', '61a (se mesmo assim mandarem preencher, a pagina aceita; quem decide parar e o roteiro)');
  telaExpirada();
  checar(S.pdvLoginTela() === 'expirada', '62 login-page-expired.ftl e expirada (' + S.pdvLoginTela() + ')');
  checar(S.pdvLoginVoltar() === true && voltar.clicks === 1, '62a pdvLoginVoltar toca no Voltar (login-actions/restart)');
  telaExpirada('error.ftl');
  checar(S.pdvLoginTela() === 'erro' && S.pdvLoginVoltar() === true, '62b error.ftl e erro e tambem volta');
  telaLogada();
  checar(S.pdvLoginVoltar() === false, '62c no Gestor logado nao ha Voltar');

  // ── 7. o Gestor ──
  telaLogada();
  checar(S.pdvLoginTela() === 'logado', '63 o Gestor com o app montado e logado (' + S.pdvLoginTela() + ')');
  checar(S.pdvLoginPreencher('a@b.c', 'x') === 'sem_campos' && S.pdvLoginEmail('a') === 'sem_campo', '63a nada para preencher no Gestor logado');
  telaCasca();
  checar(S.pdvLoginTela() === 'carregando', '63b a casca do Gestor (.Initial com o root vazio) e carregando, nunca logado (' + S.pdvLoginTela() + ')');
  gestor(); sb.location.href = 'https://gestordepedidos.ifood.com.br/#/login';
  checar(S.pdvLoginTela() === 'carregando', '63c a rota #/login do Gestor sem campos e carregando');
  // a pagina do Keycloak sem o kcContext (ou antes dele): pelos campos e pelo host
  auth('login-username.ftl'); sb.kcContext = undefined;
  checar(S.pdvLoginTela() === 'carregando', '64 o host partners-auth sem campo nenhum e carregando (' + S.pdvLoginTela() + ')');
  telaUsername(); sb.kcContext = undefined;
  checar(S.pdvLoginTela() === 'email', '64a sem o kcContext, o host partners-auth com o campo de e-mail e email');
  telaPassword(); sb.kcContext = undefined;
  checar(S.pdvLoginTela() === 'senha', '64b sem o kcContext, o campo de senha sozinho e senha');
  telaCodigo(); sb.kcContext = undefined;
  checar(S.pdvLoginTela() === 'codigo', '64c sem o kcContext, as caixas do OTP sao codigo');
  telaCanal(); sb.kcContext = undefined;
  checar(S.pdvLoginTela() === 'canal', '64d sem o kcContext, os radios otpChannel sao canal');
  telaExpirada(); sb.kcContext = undefined;
  checar(S.pdvLoginTela() === 'expirada', '64e sem o kcContext, so o Voltar no host partners-auth e expirada');
  // o que esta escondido nao conta
  telaLogada();
  body.appendChild(new El('input', { attrs: { type: 'password' }, rect: R(0, 0, 0, 0) }));
  checar(S.pdvLoginTela() === 'logado', '65 um campo de senha sem tamanho (escondido) nao faz a tela ser login');

  // ── 8. carregado duas vezes ──
  let estourou = false;
  try { vm.runInContext(codigo, sb); } catch (e) { estourou = true; }
  telaUsername();
  checar(!estourou && S.pdvLoginTela() === 'email', '66 o script injetado duas vezes na mesma pagina continua funcionando');
  console.log(JSON.stringify(res));
})();
