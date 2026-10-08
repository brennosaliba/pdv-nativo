using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Pdv.Testes;

/// <summary>
/// O SCRIPT DO SDK DO SENDBIRD RODANDO DE VERDADE (1.0.20, desenho da aprovação pelo WhatsApp, seção 3).
///
/// Na Savassi o 1.0.19 mandava <c>sdk.achou=false</c> e <c>instancias=0</c>: o runtime do Gestor não
/// expõe o cache de módulos do webpack (<c>req.c</c>), só as fábricas (<c>req.m</c>). O 1.0.20 acha a
/// fábrica do <c>@sendbird/chat</c> pelo texto do corpo e pede os exports ao require do próprio Gestor.
///
/// O WebView2 não roda na bateria, mas o script é JavaScript puro: aqui ele é tirado da constante
/// <c>ScriptSendbird</c> de <c>Telas/ChatIfood.xaml.cs</c> (o texto que vai para a loja, byte a byte) e
/// roda no node contra um runtime FALSO do webpack 5, igual ao do Gestor no que importa: <c>m</c>
/// exposto, cache privado e o push da fila de chunks tratado pelo runtime. Prova que:
///  · acha a instância conectada pelo módulo, uma vez, e guarda o id (a segunda procura não lê fábrica);
///  · NUNCA executa uma fábrica que não casou com a assinatura (nem a que só tem o getter, nem a que
///    só chama sendUserMessage, nem a que só tem a versão, nem a que só tem a marca sendbird.com);
///  · acha o SDK repartido como no Gestor de verdade (revisão 07/10, bundle order-manager-web
///    9.346.3-0 capturado na loja): a classe com o getter <c>instance</c> em ES5
///    (<c>Object.defineProperty</c>) e a marca <c>sendbird.com</c> num módulo, a versão no núcleo e o
///    <c>sendUserMessage</c> no canal. O módulo falso de antes juntava tudo num corpo só e escondia
///    que o 1.0.20 não casava com o bundle real;
///  · a instância nova depois de o Gestor iniciar de novo (token renovado) é achada sozinha;
///  · o require é pego na hora da procura, inclusive quando o runtime sobe depois do script;
///  · a cópia 4.14.6 de outro runtime não atrapalha o envio;
///  · a árvore do React, descendo em shadowRoot aberto, continua de segunda via;
///  · o envio sai pela instância achada, com o token de uso único.
/// Sem node na máquina a suíte FALHA (não pula): esta é a única prova do script antes da loja.
/// </summary>
public static class TestesScriptSendbird
{
    public static void Rodar(Action<bool, string> checar)
    {
        var raiz = Raiz();
        var fonte = raiz is null ? "" : LerSeExiste(Path.Combine(raiz, "Telas", "ChatIfood.xaml.cs"));
        var script = ExtrairRaw(fonte, "ScriptSendbird");
        checar(script is { Length: > 1000 }, "SB-0 o ScriptSendbird sai da constante de Telas/ChatIfood.xaml.cs");
        if (script is null) return;
        checar(!script.Contains((char)0x2014) && !script.Contains((char)0x2013), "SB-0b o script não tem travessão");

        var pasta = Path.Combine(Path.GetTempPath(), "pdv-sb-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(pasta);
        try
        {
            var arqScript = Path.Combine(pasta, "script-sendbird.js");
            var arqTeste = Path.Combine(pasta, "teste-sendbird.js");
            File.WriteAllText(arqScript, script, new UTF8Encoding(false));
            File.WriteAllText(arqTeste, Harness, new UTF8Encoding(false));

            var check = Node(pasta, "--check", arqScript);
            checar(check.Exit == 0, $"SB-1 o script injetado passa no node --check ({check.Erro.Trim()})");

            var r = Node(pasta, arqTeste, arqScript);
            List<ItemHarness>? itens = null;
            try
            {
                var ultima = r.Saida.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("[", StringComparison.Ordinal));
                if (ultima is not null) itens = JsonSerializer.Deserialize<List<ItemHarness>>(ultima);
            }
            catch { itens = null; }
            checar(r.Exit == 0 && itens is { Count: > 20 },
                $"SB-2 o node rodou o script contra o runtime falso do webpack 5 (saida {r.Exit}, {itens?.Count ?? 0} conferências) {Corta(r.Erro)}");
            foreach (var i in itens ?? new List<ItemHarness>()) checar(i.ok, "SB-" + i.nome);
        }
        finally
        {
            try { Directory.Delete(pasta, true); } catch { }
        }
    }

    private sealed record ItemHarness(bool ok, string nome);

    /// <summary>
    /// O conteúdo de um raw string <c>"""</c> do C#: as linhas entre a abertura e a linha do fecho, sem
    /// o recuo da linha do fecho (a regra da linguagem). Devolve null se não achar.
    /// </summary>
    public static string? ExtrairRaw(string fonte, string nome)
    {
        var marca = "private const string " + nome + " = \"\"\"";
        var i = fonte.IndexOf(marca, StringComparison.Ordinal);
        if (i < 0) return null;
        var inicio = fonte.IndexOf('\n', i);
        if (inicio < 0) return null;
        var linhas = fonte[(inicio + 1)..].Replace("\r\n", "\n").Split('\n');
        var fim = Array.FindIndex(linhas, l => l.Trim() == "\"\"\";");
        if (fim < 0) return null;
        var recuo = linhas[fim][..linhas[fim].IndexOf('"')];
        var sb = new StringBuilder();
        for (var k = 0; k < fim; k++)
        {
            var l = linhas[k];
            sb.Append(l.StartsWith(recuo, StringComparison.Ordinal) ? l[recuo.Length..] : l.TrimStart()).Append('\n');
        }
        return sb.ToString();
    }

    private static (int Exit, string Saida, string Erro) Node(string pasta, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("node")
            {
                WorkingDirectory = pasta,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var saida = p.StandardOutput.ReadToEndAsync();
            var erro = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60_000)) { try { p.Kill(true); } catch { } return (-2, "", "o node passou de 60 s"); }
            return (p.ExitCode, saida.Result, erro.Result);
        }
        catch (Exception ex) { return (-1, "", "node ausente nesta máquina: " + ex.GetType().Name); }
    }

    private static string Corta(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim();

    private static string LerSeExiste(string p) => File.Exists(p) ? File.ReadAllText(p) : "";

    private static string? Raiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj"))) return dir.FullName;
        return null;
    }

    // ── o harness em node ────────────────────────────────────────────────────
    // Cada mundo é um contexto novo do vm (window, self e document próprios). O runtime falso tem
    // o cache PRIVADO (o require não tem .c), expõe .m, e trata o push da fila como o jsonp do
    // webpack 5. Cada fábrica conta quando é executada e quantas vezes o toString leu o corpo dela.
    private const string Harness = """
'use strict';
const fs = require('fs');
const vm = require('vm');
const codigo = fs.readFileSync(process.argv[2], 'utf8');
const res = [];
function checar(ok, nome) { res.push({ ok: !!ok, nome: nome }); }

const BASE = `
var __msgs = [];
var __exec = [];
var __lidas = {};
var __enviadas = [];
var __agora = 1791400000000;
Date.now = function () { return __agora; };
var chrome = { webview: { postMessage: function (s) { __msgs.push(JSON.parse(s)); } } };
var __toStr = Function.prototype.toString;
Function.prototype.toString = function () {
  if (this && this.__pdvId) __lidas[this.__pdvId] = (__lidas[this.__pdvId] || 0) + 1;
  return __toStr.call(this);
};
function __marca(mods) { for (var k in mods) mods[k].__pdvId = k; return mods; }
function montarRuntime(nome, modulos) {
  var cache = {};
  function req(id) {
    var c = cache[id];
    if (c !== undefined) return c.exports;
    var module = cache[id] = { exports: {} };
    modulos[id](module, module.exports, req);
    return module.exports;
  }
  req.m = modulos;
  req.d = function (ex, def) { for (var k in def) if (!Object.prototype.hasOwnProperty.call(ex, k)) Object.defineProperty(ex, k, { enumerable: true, get: def[k] }); };
  req.r = function (ex) { Object.defineProperty(ex, '__esModule', { value: true }); };
  var instalados = {};
  function jsonp(pai, data) {
    var ids = data[0], mais = data[1], rt = data[2];
    if (ids.some(function (i) { return instalados[i] !== 0; })) {
      for (var k in mais) if (Object.prototype.hasOwnProperty.call(mais, k)) { mais[k].__pdvId = k; modulos[k] = mais[k]; }
      if (rt) rt(req);
    }
    if (pai) pai(data);
    ids.forEach(function (i) { instalados[i] = 0; });
  }
  var fila = self[nome] = self[nome] || [];
  fila.forEach(jsonp.bind(null, 0));
  fila.push = jsonp.bind(null, fila.push.bind(fila));
  return req;
}
function canais(lista, quem) {
  var o = {};
  lista.forEach(function (c) {
    // 1.0.24: metaDepois = N: o metadata falha N vezes e responde na seguinte (o SDK frio logo depois de carregar)
    var faltam = c.metaDepois || 0;
    o[c.url] = { url: c.url, cachedMetaData: c.semMeta ? {} : { orderUuid: c.uuid }, isFrozen: !!c.congelada,
      getMetaData: c.metaRede ? function (k) { if (faltam > 0) { faltam--; return Promise.reject(new Error('frio')); } return Promise.resolve({ orderUuid: c.uuid }); } : undefined,
      members: [{ metaData: { userType: 'CUSTOMER' } }, { metaData: { userType: 'MERCHANT' } }],
      sendUserMessage: function (params) {
        __enviadas.push({ por: quem, canal: c.url, texto: params.message });
        return { onSucceeded: function (cb) { cb({ messageId: 991 }); return this; }, onFailed: function () { return this; } };
      } };
  });
  return o;
}
function modulosDoGestor() {
  return __marca({
    // o @sendbird/chat 4.19.9 repartido como no Gestor de verdade (order-manager-web 9.346.3-0):
    // o nucleo (22135) tem a versao, o canal (1403) tem o envio, e a classe SendbirdChat (45369)
    // tem o getter instance em ES5 (Object.defineProperty) e a marca sendbird.com, sem a versao
    // e sem o envio no corpo dela. Exporta a classe como ZP, pelo d() do runtime.
    '22135': function (module, exports, req) {
      __exec.push('22135');
      exports.v = "4.19.9";
    },
    '1403': function (module, exports, req) {
      __exec.push('1403');
      function GroupChannel() {}
      GroupChannel.prototype.sendUserMessage = function (p) { return p; };
      exports.Y = GroupChannel;
    },
    '45369': function (module, exports, req) {
      __exec.push('45369');
      req.d(exports, { ZP: function () { return ap; } });
      var oo = req('22135'), ho = req('1403');
      var tp;
      var ap = function () {
        function za(p) { this.appId = p.appId; }
        za.init = function (p) {
          if (tp) tp.currentUser = null;
          var sb = new za(p);
          sb.apiHost = "https://api-".concat(p.appId || "app", ".sendbird.com");
          sb.nome = p.nome;
          sb.currentUser = { userId: p.userId };
          var cs = p.canais;
          sb.groupChannel = { getChannel: function (url) { return cs[url] ? Promise.resolve(cs[url]) : Promise.reject(new Error('nao achei')); } };
          tp = sb;
          return sb;
        };
        Object.defineProperty(za, "instance", { get: function () { return tp; }, enumerable: !1, configurable: !0 });
        Object.defineProperty(za, "version", { get: function () { return oo.v; }, enumerable: !1, configurable: !0 });
        return za;
      }();
    },
    // carregado pelo Gestor, sem nada do SDK
    '1001': function (module, exports, req) { __exec.push('1001'); exports.algo = 1; },
    // NAO carregado: so o getter instance, sem a marca do Sendbird (como o plugin do Sentry no Gestor)
    '1002': function (module, exports, req) { __exec.push('1002'); class Unico { static get instance() { return 1; } } exports.Z = Unico; },
    // NAO carregado: so chama o envio (sem o getter)
    '1003': function (module, exports, req) { __exec.push('1003'); exports.mandar = function (c) { return c.sendUserMessage({ message: 'x' }); }; },
    // NAO carregado: so a versao (sem o getter)
    '1004': function (module, exports, req) { __exec.push('1004'); exports.v = "4.19.9"; },
    // NAO carregado: a marca sendbird.com sem o getter instance
    '1006': function (module, exports, req) { __exec.push('1006'); exports.host = "wss://ws-".concat("app", ".sendbird.com"); }
  });
}
function bootGestor(nome, userId, lista, nomeInst) {
  var req = montarRuntime(nome, modulosDoGestor());
  var SB = req('45369').ZP;
  req('1001');
  var inst = SB.init({ userId: userId, nome: nomeInst || 'gestor', canais: canais(lista, nomeInst || 'gestor') });
  return { req: req, SB: SB, inst: inst };
}
`;

function mundo(documento) {
  const sb = { console: console };
  sb.self = sb;
  sb.window = sb;
  sb.__pdvRespiroMs = 1;   // 1.0.24: as tentativas do SDK frio respiram 1 ms aqui (1,2 s e 1,5 s na loja)
  sb.setTimeout = setTimeout;   // o contexto do vm nasce sem relogio; o do node serve
  sb.document = documento || {
    querySelector: function () { return null; },
    querySelectorAll: function () { return []; }
  };
  vm.createContext(sb);
  vm.runInContext(BASE, sb);
  return sb;
}
function carregar(sb) { vm.runInContext(codigo, sb); }
function rodar(sb, js) { return vm.runInContext(js, sb); }
async function esperar(sb, id) {
  for (let i = 0; i < 400; i++) {
    const m = sb.__msgs.find(function (x) { return x.id === id; });
    if (m) return m;
    await new Promise(function (r) { setTimeout(r, 2); });
  }
  return null;
}
async function diag(sb, id, ws, lista) {
  sb.__arg = { id: id, ws: ws, canais: lista || ['canal-1'] };
  rodar(sb, 'window.pdvSb.diag(__arg)');
  return await esperar(sb, id);
}
async function enviar(sb, id, token, canal, uuid, texto, ws) {
  sb.__arg = { id: id, token: token, canal: canal, orderUuid: uuid, texto: texto, ws: ws };
  rodar(sb, 'window.__pdvEnvioToken = ' + JSON.stringify(token) + '; window.pdvSb.enviar(__arg)');
  return await esperar(sb, id);
}
function lidas(sb) { return JSON.parse(JSON.stringify(sb.__lidas)); }
function exec(sb) { return Array.from(sb.__exec).join(','); }

(async function () {
  // ── MUNDO A: o Gestor carregado, o SDK conectado, o script chega depois ──
  {
    const sb = mundo();
    rodar(sb, "var G = bootGestor('webpackChunkgestor_pedidos', 'loja-1', [{ url: 'canal-1', uuid: 'uuid-1' }], 'primeira');");
    const execAntes = exec(sb);
    const lidasAntes = lidas(sb);
    carregar(sb);
    checar(Object.keys(lidasAntes).length === 0, '10 nada leu fabrica antes do script (a contagem e do script)');
    const d1 = await diag(sb, 'd1', 'loja-1');
    checar(d1 && d1.achou === true && d1.instancias === 1, '11 acha a instancia conectada pelo modulo do webpack (achou e instancias=1)');
    checar(d1 && d1.user_id_igual_ws === true && d1.uid === 'loja-1', '12 o usuario da instancia e o mesmo do WebSocket (user_id_igual_ws)');
    checar(d1 && d1.via_webpack === true && d1.via_react === false && d1.modulos === 1 && d1.runtimes === 1,
      '13 o diagnostico conta por onde achou: webpack, 1 modulo, 1 runtime');
    checar(d1 && d1.conferidos === 1 && d1.tem_order_uuid === true && d1.congeladas === 0, '14 confere o canal pelo SDK achado (orderUuid, nao congelada)');
    checar(exec(sb) === execAntes && execAntes === '45369,22135,1403,1001',
      '15 NAO executa fabrica que nao casou com a assinatura, nem executa o SDK de novo (' + exec(sb) + ')');
    const l1 = lidas(sb);
    checar(['45369', '22135', '1403', '1001', '1002', '1003', '1004', '1006'].every(function (k) { return l1[k] === 1; }),
      '16 cada fabrica e lida uma vez so na primeira procura (' + JSON.stringify(l1) + ')');

    sb.__agora += 61000;
    const d2 = await diag(sb, 'd2', 'loja-1');
    checar(d2 && d2.achou === true && d2.instancias === 1, '17 passado o minuto do cache, continua achando');
    checar(JSON.stringify(lidas(sb)) === JSON.stringify(l1), '18 a segunda procura vai pelo id guardado: nenhuma fabrica lida de novo');

    const e1 = await enviar(sb, 'e1', 'tk-1', 'canal-1', 'uuid-1', 'Oi, Ana! Seu premio vai junto.', 'loja-1');
    checar(e1 && e1.ok === true && e1.msgId === '991' && sb.__enviadas.length === 1 && sb.__enviadas[0].por === 'primeira',
      '19 o envio sai pela instancia achada no modulo, com o msgId de volta');
    const e2 = await enviar(sb, 'e2', 'outro', 'canal-1', 'uuid-1', 'texto', 'loja-1');
    sb.__arg = { id: 'e3', token: 'tk-1', canal: 'canal-1', orderUuid: 'uuid-1', texto: 'de novo', ws: 'loja-1' };
    rodar(sb, 'window.pdvSb.enviar(__arg)');
    const e3 = await esperar(sb, 'e3');
    checar(e2 && e2.ok === true && e3 && e3.ok === false && e3.erro === 'token',
      '20 o token e de uso unico: sem o C# por de novo, a pagina recusa');
    const e4 = await enviar(sb, 'e4', 'tk-4', 'canal-1', 'uuid-errado', 'texto', 'loja-1');
    checar(e4 && e4.ok === false && e4.erro === 'canal_errado', '21 o canal de outro pedido continua recusado (canal_errado)');

    // o Gestor iniciou o SDK de novo (token renovado): a instancia velha perde o usuario
    rodar(sb, "G.SB.init({ userId: 'loja-1', nome: 'segunda', canais: canais([{ url: 'canal-1', uuid: 'uuid-1' }], 'segunda') });");
    const d3 = await diag(sb, 'd3', 'loja-1');
    checar(d3 && d3.achou === true && d3.instancias === 1 && JSON.stringify(lidas(sb)) === JSON.stringify(l1),
      '22 a instancia nova depois de o Gestor iniciar de novo e achada sozinha, pelo mesmo id, sem ler fabrica');
    const e5 = await enviar(sb, 'e5', 'tk-5', 'canal-1', 'uuid-1', 'Depois da renovacao.', 'loja-1');
    checar(e5 && e5.ok === true && sb.__enviadas[sb.__enviadas.length - 1].por === 'segunda',
      '23 e o envio sai pela instancia nova');

    // chunk novo carregado depois, e a instancia deixou de valer: so a fabrica nova e lida
    rodar(sb, "self.webpackChunkgestor_pedidos.push([['lazy-1'], { '1005': function (module, exports) { __exec.push('1005'); exports.z = 1; } }]);");
    rodar(sb, 'G.SB.instance.currentUser = null;');
    sb.__agora += 61000;
    const d4 = await diag(sb, 'd4', 'loja-1');
    const l4 = lidas(sb);
    checar(d4 && d4.achou === false && d4.instancias === 0, '24 sem instancia valida (desconectada), achou=false');
    checar(l4['1005'] === 1 && ['45369', '22135', '1403', '1001', '1002', '1003', '1004', '1006'].every(function (k) { return l4[k] === 1; }),
      '25 a procura de novo le so a fabrica que chegou depois (' + JSON.stringify(l4) + ')');
    checar(exec(sb).indexOf('1005') < 0 && exec(sb).indexOf('1002') < 0 && exec(sb).indexOf('1006') < 0,
      '26 e nao executa a fabrica nova que nao casou, nem a do getter sem a marca, nem a da marca sem o getter');
    const e6 = await enviar(sb, 'e6', 'tk-6', 'canal-1', 'uuid-1', 'texto', 'loja-1');
    checar(e6 && e6.ok === false && e6.erro === 'sdk_ausente', '27 sem instancia valida o envio recusa com sdk_ausente');
  }

  // ── MUNDO B: os chunks chegam antes do runtime; o script procura antes de ele subir ──
  {
    const sb = mundo();
    rodar(sb, "self.webpackChunkgestor_pedidos = [[['cedo'], {}]];");
    carregar(sb);
    const d1 = await diag(sb, 'b1', 'loja-1');
    checar(d1 && d1.achou === false && d1.runtimes === 0, '30 antes do runtime subir nada e achado (o push fica na fila)');
    rodar(sb, "var G = bootGestor('webpackChunkgestor_pedidos', 'loja-1', [{ url: 'canal-1', uuid: 'uuid-1' }], 'b');");
    const d2 = await diag(sb, 'b2', 'loja-1');
    checar(d2 && d2.achou === true && d2.runtimes === 1 && d2.modulos === 1,
      '31 o runtime que sobe depois trata o pedido que ficou na fila e o SDK e achado');
    checar(exec(sb) === '45369,22135,1403,1001', '32 de novo sem executar fabrica que nao casou (' + exec(sb) + ')');
  }

  // ── MUNDO C: dois runtimes; o segundo tem a copia 4.14.6 que nao conhece a conversa ──
  {
    const sb = mundo();
    rodar(sb, `
      var R2 = montarRuntime('webpackChunkchat_remoto', __marca({
        '9001': function (module, exports, req) {
          __exec.push('9001');
          var host = "wss://ws-".concat("app", ".sendbird.com");
          class GroupChannel { sendUserMessage(p) { return p; } }
          class SendbirdChat {
            static get instance() { return SendbirdChat._i; }
            static init(p) { var s = new SendbirdChat(); s.nome = 'copia-4.14.6'; s.currentUser = { userId: p.userId };
              s.groupChannel = { getChannel: function () { throw new Error('canal desconhecido'); } }; SendbirdChat._i = s; return s; }
          }
          exports.default = SendbirdChat;
        }
      }));
      R2('9001').default.init({ userId: 'loja-1' });
      var G = bootGestor('webpackChunkgestor_pedidos', 'loja-1', [{ url: 'canal-1', uuid: 'uuid-1' }], 'gestor-4.19.9');
    `);
    carregar(sb);
    const d1 = await diag(sb, 'c1', 'loja-1');
    checar(d1 && d1.achou === true && d1.instancias === 2 && d1.modulos === 2 && d1.runtimes === 2,
      '40 com dois runtimes acha as duas copias do SDK (4.19.9 e 4.14.6)');
    const e1 = await enviar(sb, 'c2', 'tk-c', 'canal-1', 'uuid-1', 'Oi do caixa.', 'loja-1');
    checar(e1 && e1.ok === true && sb.__enviadas.length === 1 && sb.__enviadas[0].por === 'gestor-4.19.9',
      '41 a copia 4.14.6 que nao conhece a conversa nao atrapalha: o envio sai pela 4.19.9');
  }

  // ── MUNDO D: sem webpack; o chat dentro de um shadowRoot aberto (segunda via, React) ──
  {
    const instReact = { currentUser: { userId: 'loja-1' }, groupChannel: { getChannel: function () { return Promise.resolve(null); } } };
    const el = { '__reactFiber$abc': { memoizedProps: { className: 'x' }, return: { memoizedProps: { value: { stores: { sdkStore: { sdk: instReact } } } }, return: null } } };
    const semShadow = { shadowRoot: null };
    const host = { shadowRoot: {
      querySelector: function (sel) { return sel.indexOf('sendbird-') >= 0 ? el : null; },
      querySelectorAll: function () { return []; } } };
    const doc = { querySelector: function () { return null; }, querySelectorAll: function () { return [semShadow, host]; } };
    const sb = mundo(doc);
    carregar(sb);
    const d1 = await diag(sb, 'r1', 'loja-1');
    checar(d1 && d1.achou === true && d1.instancias === 1 && d1.via_react === true && d1.via_webpack === false && d1.user_id_igual_ws === true,
      '50 sem o modulo, a arvore do React dentro do shadowRoot aberto acha o SDK (segunda via)');
  }

  // ── MUNDO M (1.0.21): o canal vem sem o metadata no cache, como o sinal da loja mostrou ──
  {
    const U = '3fa0c6c2-1b2e-4c8a-9d10-aa22bb33cc44', OUTRO = '9b8e7d6c-5a4b-4c3d-8e2f-001122334455';
    const URL_PEDIDO = 'sendbird_gc_cm_' + U + '_ff493e25-f413-42e5-a46a-96c0b788813f';
    const sb = mundo();
    rodar(sb, "var G = bootGestor('webpackChunkgestor_pedidos', 'loja-1', ["
      + "{ url: '" + URL_PEDIDO + "', uuid: '" + U + "', semMeta: true },"
      + "{ url: 'canal-rede', uuid: 'uuid-rede', semMeta: true, metaRede: true },"
      + "{ url: 'canal-mudo', uuid: 'uuid-mudo', semMeta: true }], 'gestor');");
    carregar(sb);
    const d = await diag(sb, 'm0', 'loja-1', [URL_PEDIDO]);
    checar(d && d.conferidos === 1 && d.tem_order_uuid === true, '70 o diagnostico le o pedido pelo endereco do canal quando o metadata nao veio');
    const e1 = await enviar(sb, 'm1', 'tk-m1', URL_PEDIDO, U, 'Oi, Ana!', 'loja-1');
    checar(e1 && e1.ok === true, '71 sem metadata no cache, o endereco do canal do iFood prova o pedido e a resposta sai');
    const e2 = await enviar(sb, 'm2', 'tk-m2', URL_PEDIDO, OUTRO, 'texto', 'loja-1');
    checar(e2 && e2.ok === false && e2.erro === 'canal_errado', '72 o endereco de OUTRO pedido continua recusado (canal_errado)');
    const e3 = await enviar(sb, 'm3', 'tk-m3', 'canal-rede', 'uuid-rede', 'texto', 'loja-1');
    checar(e3 && e3.ok === true, '73 sem cache e sem o pedido no endereco, pergunta o metadata ao canal e manda');
    const e4 = await enviar(sb, 'm4', 'tk-m4', 'canal-mudo', 'uuid-mudo', 'texto', 'loja-1');
    checar(e4 && e4.ok === false && e4.erro === 'canal_errado', '74 sem cache, sem endereco e sem metadata, nao manda (canal_errado)');
  }

  // ── MUNDO F (1.0.24): o SDK frio logo depois de o Gestor carregar ──
  {
    const sb = mundo();
    rodar(sb, "var G = bootGestor('webpackChunkgestor_pedidos', 'loja-1', ["
      + "{ url: 'canal-frio', uuid: 'uuid-frio', semMeta: true, metaRede: true, metaDepois: 2 },"
      + "{ url: 'canal-gelado', uuid: 'uuid-gelado', semMeta: true, metaRede: true, metaDepois: 3 }], 'gestor');");
    carregar(sb);
    const e1 = await enviar(sb, 'f1', 'tk-f1', 'canal-frio', 'uuid-frio', 'Oi, Rodrigo!', 'loja-1');
    checar(e1 && e1.ok === true, '75 o metadata falha duas vezes e responde na terceira: a resposta sai (antes caia em canal_errado)');
    const e2 = await enviar(sb, 'f2', 'tk-f2', 'canal-gelado', 'uuid-gelado', 'texto', 'loja-1');
    checar(e2 && e2.ok === false && e2.erro === 'canal_errado', '76 tres falhas seguidas do metadata: canal_errado, sem insistir para sempre');
    const e3 = await enviar(sb, 'f3', 'tk-f3', 'canal-que-nao-existe', 'uuid-x', 'texto', 'loja-1');
    checar(e3 && e3.ok === false && e3.erro === 'canal_errado', '77 canal que nao existe: canal_errado depois das tentativas');
  }

  // ── MUNDO E: o script carregado duas vezes na mesma pagina nao duplica ──
  {
    const sb = mundo();
    carregar(sb);
    const primeiro = rodar(sb, 'window.pdvSb');
    carregar(sb);
    checar(rodar(sb, 'window.pdvSb') === primeiro, '60 o script injetado duas vezes nao troca o pdvSb da pagina');
  }

  console.log(JSON.stringify(res));
})().catch(function (e) { console.error('harness: ' + (e && e.stack || e)); process.exit(3); });
""";
}
