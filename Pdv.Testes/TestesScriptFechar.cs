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
///  · a vigia de 2 s: a gaveta que some sem o operador ter saído é reaberta pelo mesmo caminho do
///    aquecimento (pdvAquecerLista), no máximo uma vez por 10 s, e a tela é avisada (painelfechou).
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
const document = {
  body: body, head: head, documentElement: body,
  addEventListener: function (tipo, fn, captura) { (ouvintes[tipo] = ouvintes[tipo] || []).push({ fn: fn, captura: !!captura }); },
  querySelectorAll: function (s) { return body.querySelectorAll(s); },
  querySelector: function (s) { return body.querySelector(s); },
  getElementById: function (id) { return body.descendentes().concat(head.descendentes()).find(function (e) { return e.id === id; }) || null; },
  createElement: function (tag) { return new El(tag); },
};

// ── a gaveta como o Gestor a monta: título "Conversas", o cabeçalho da conversa (seta, nome, X) e os balões ──
let gaveta, seta, x, xSemRotulo, titulo, cabecalho;
function montarGaveta(opts) {
  opts = opts || {};
  var dx = opts.dx || 0, dy = opts.dy || 0;   // 1.0.42: o cabecalho em outro lugar da tela
  function R(l, t, w, h) { return { left: l + dx, top: t + dy, width: w, height: h }; }
  body.children = [];
  gaveta = body.appendChild(new El('div', { className: 'drawer', rect: R(600, 0, 420, 768) }));
  titulo = gaveta.appendChild(new El('h1', { text: 'Conversas', rect: R(610, 10, 400, 30) }));
  cabecalho = gaveta.appendChild(new El('div', { className: 'chat-header', rect: R(600, 50, 420, 56) }));
  seta = cabecalho.appendChild(new El('button', { attrs: opts.setaSemRotulo ? {} : { 'aria-label': 'Voltar' }, rect: R(608, 58, 40, 40) }));
  seta.appendChild(new El('svg', { className: opts.setaSemRotulo ? 'ifdl-icon-arrow-left' : 'ifdl-icon', rect: R(616, 66, 24, 24) }));
  cabecalho.appendChild(new El('span', { text: '#5041 Maluma Lopes', rect: R(660, 62, 250, 30) }));
  if (opts.xSemRotulo) {
    xSemRotulo = cabecalho.appendChild(new El('button', { rect: R(972, 58, 40, 40) }));
    xSemRotulo.appendChild(new El('svg', { className: 'ifdl-icon', rect: R(980, 66, 24, 24) }));
    x = xSemRotulo;
  } else {
    x = cabecalho.appendChild(new El('button', { attrs: { 'aria-label': 'Fechar' }, rect: R(972, 58, 40, 40) }));
    x.appendChild(new El('svg', { className: 'ifdl-icon-close', rect: R(980, 66, 24, 24) }));
  }
  const baloes = gaveta.appendChild(new El('div', { className: 'messages', rect: { left: 600, top: 110, width: 420, height: 600 } }));
  baloes.appendChild(new El('div', { text: 'oi, ganhei na raspadinha', rect: { left: 610, top: 120, width: 200, height: 40 } }));
  const enviar = gaveta.appendChild(new El('button', { attrs: { 'aria-label': 'Enviar' }, rect: { left: 960, top: 720, width: 50, height: 40 } }));
  return gaveta;
}
function clique(alvo) {
  let parou = false, prevenido = false;
  const ev = { target: alvo, stopImmediatePropagation: function () { parou = true; }, preventDefault: function () { prevenido = true; } };
  (ouvintes.click || []).forEach(function (o) { if (o.captura) o.fn(ev); });
  if (!parou) alvo.clicks++;
  return { parou: parou, prevenido: prevenido };
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
  checar(css && /^\[data-pdv-fechar-painel\]\{display:none!important;pointer-events:none!important\}$/.test(css.textContent),
    '11 o CSS esconde o X marcado (display none + pointer-events none) nos DOIS modos (1.0.42: sem o body.pdv-so-chat)');
  checar(intervalos.some(function (i) { return i.ms === 2000; }) && intervalos.some(function (i) { return i.ms === 1000; }) && typeof sb.__observador === 'function',
    '12 a vigia roda a cada 2 s, a blindagem a cada 1 s e o DOM é observado');

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

  // 50: a mutação do DOM re-marca (o React re-renderiza)
  montarGaveta();
  sb.__observador([]);
  setTimeout(function () {
    checar(x.hasAttribute('data-pdv-fechar-painel'), '50 depois de uma mutação do DOM o X recriado é marcado de novo (debounce)');

    // 60: a vigia da gaveta
    const v0 = sb.pdvVigiarPainel();
    checar(v0 === 'aberta' && aquecimentos === 0, '60 com a gaveta na tela a vigia só re-blinda');
    gavetaVisivel = false;
    const v1 = sb.pdvVigiarPainel();
    const pf = msgs.filter(function (m) { return m.tipo === 'painelfechou'; });
    checar(v1 === 'reabriu:abriu' && aquecimentos === 1 && pf.length === 1 && pf[0].resultado === 'abriu',
      '61 a gaveta sumiu sem o operador sair: reabre pelo caminho do aquecimento e avisa a tela (painelfechou)');
    agora += 2000;
    const v2 = sb.pdvVigiarPainel();
    checar(v2 === 'esperando' && aquecimentos === 1, '62 2 s depois ainda sumida: espera (no máximo 1 vez por 10 s)');
    agora += 8000;
    const v3 = sb.pdvVigiarPainel();
    checar(v3 === 'reabriu:abriu' && aquecimentos === 2, '63 passados 10 s tenta de novo');
    sb.__pdvSemHolofote = true;
    agora += 10000;
    checar(sb.pdvVigiarPainel() === 'gestor_inteiro' && aquecimentos === 2, '64 com o Gestor inteiro pedido pelo operador, não reabre');
    sb.__pdvSemHolofote = false;
    classes.delete('pdv-so-chat');
    checar(sb.pdvVigiarPainel() === 'fora_do_chat' && aquecimentos === 2, '65 fora do modo "só o chat", não reabre');
    classes.add('pdv-so-chat');
    gavetaVisivel = true;
    checar(sb.pdvVigiarPainel() === 'aberta', '66 a gaveta voltou: a vigia descansa');
    const texto = JSON.stringify(msgs);
    checar(texto.indexOf('Maluma') < 0 && texto.indexOf('raspadinha') < 0, '67 o aviso para a tela não leva nome de cliente nem texto da conversa');

    // 70: carregado duas vezes não duplica
    const antes = intervalos.length;
    vm.runInContext(codigo, sb);
    checar(intervalos.length === antes, '70 o script injetado duas vezes na mesma página não cria segunda vigia');
    console.log(JSON.stringify(res));
  }, 500);
})();
""";
}
