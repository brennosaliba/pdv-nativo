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
        // 1.0.35: o SDK que RECUSA, nas tres formas que o script trata (onFailed, then rejeitado, throw)
        if (c.falha && c.modo === 'lanca') throw c.falha;
        if (c.falha && c.modo === 'promessa') return Promise.reject(c.falha);
        if (c.falha) return { onSucceeded: function () { return this; }, onFailed: function (cb) { cb(c.falha, { errorCode: c.falha.code, message: params.message }); return this; } };
        return { onSucceeded: function (cb) { cb({ messageId: 991 }); return this; }, onFailed: function () { return this; } };
      } };
    // 1.0.35: a forma antiga, com callback (msg, err)
    if (c.modo === 'callback') o[c.url].sendUserMessage = function (params, cb) {
      __enviadas.push({ por: quem, canal: c.url, texto: params.message });
      cb({ message: params.message }, c.falha);
    };
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
          if (tp && !p.manter) tp.currentUser = null;   // 1.0.38: 'manter' deixa a conexao anterior viva (o caso do 15/10)
          var sb = new za(p);
          sb.apiHost = "https://api-".concat(p.appId || "app", ".sendbird.com");
          sb.nome = p.nome;
          sb.currentUser = { userId: p.userId };
          sb.connectionState = p.estado || 'OPEN';
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
function bootGestor(nome, userId, lista, nomeInst, estado) {
  var req = montarRuntime(nome, modulosDoGestor());
  var SB = req('45369').ZP;
  req('1001');
  var inst = SB.init({ userId: userId, nome: nomeInst || 'gestor', canais: canais(lista, nomeInst || 'gestor'), estado: estado });
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

  // ── MUNDO G (1.0.35, caso #8039): o SDK recusa e o MOTIVO chega ao caixa, limpo ──
  {
    const U = 'aa11bb22-3c4d-4e5f-8a9b-0c1d2e3f4a5b';
    const URL_G = 'sendbird_gc_cm_' + U + '_ff493e25-f413-42e5-a46a-96c0b788813f';
    const sb = mundo();
    rodar(sb, "function Erro(c, m) { var e = new Error(m); e.name = 'SendbirdError'; e.code = c; return e; }"
      + "var G = bootGestor('webpackChunkgestor_pedidos', 'loja-1', ["
      + "{ url: '" + URL_G + "', uuid: '" + U + "', falha: Erro(900100, 'Not a member of " + URL_G + " (user loja-1, tel 31994412725, 31 99441-2725, joao@x.com, https://api-x.sendbird.com/v3?token=abc)') },"
      + "{ url: 'canal-cb', uuid: 'uuid-cb', modo: 'callback', falha: Erro(800180, 'Request timeout') },"
      + "{ url: 'canal-pr', uuid: 'uuid-pr', modo: 'promessa', falha: Erro('400111', 'Invalid parameter') },"
      + "{ url: 'canal-th', uuid: 'uuid-th', modo: 'lanca', falha: new TypeError('x is not a function') },"
      + "{ url: 'canal-ok', uuid: 'uuid-ok' }], 'gestor');");
    carregar(sb);
    const TEXTO = 'Oi, Heitor! Seu premio e 1 Cookie Classico. Qual sabor?';
    const TOKEN = 'tk-0123456789abcdef0123456789abcdef';
    const e1 = await enviar(sb, 'g1', TOKEN, URL_G, U, TEXTO, 'loja-1');
    const d1 = (e1 && e1.detalhe) || {};
    checar(e1 && e1.ok === false && e1.erro === 'erro_envio', '80 o SDK recusou (onFailed): o erro do contrato continua erro_envio');
    checar(d1.etapa === 'falhou_sdk' && d1.codigo === 900100 && d1.codigo_msg === 900100 && d1.nome === 'SendbirdError',
      '81 o detalhe leva a etapa falhou_sdk, o codigo do Sendbird (900100), o errorCode da mensagem e o nome do erro (' + JSON.stringify(d1) + ')');
    checar(typeof d1.msg === 'string' && d1.msg.indexOf('31994412725') < 0 && d1.msg.indexOf('99441') < 0 && !/[0-9]{6,}/.test(d1.msg)
      && d1.msg.indexOf('@') < 0 && d1.msg.indexOf('http') < 0 && d1.msg.indexOf(U) < 0 && d1.msg.indexOf('sendbird_gc') < 0
      && d1.msg.indexOf('loja-1') < 0 && d1.msg.length <= 120 && d1.msg.indexOf('Not a member of') === 0,
      '82 a mensagem do erro chega limpa: sem telefone, e-mail, endereco, canal, uuid nem usuario (' + d1.msg + ')');
    checar(d1.conexao === 'OPEN' && d1.instancia === 0 && d1.instancias === 1 && d1.uid_igual_ws === true && typeof d1.ms === 'number' && d1.ms >= 0,
      '83 e o estado da conexao da instancia usada, qual instancia, quantas, se o usuario bate com o WebSocket e o tempo');
    const tudo = JSON.stringify(e1);
    checar(tudo.indexOf('loja-1') < 0 && tudo.indexOf('Heitor') < 0 && tudo.indexOf(U) < 0 && tudo.indexOf(TOKEN) < 0
      && tudo.indexOf('0123456789abcdef') < 0 && tudo.indexOf('stack') < 0,
      '84 nunca o texto, o canal, o pedido, o usuario, o token nem a pilha de chamadas');
    const e2 = await enviar(sb, 'g2', 'tk-g2', 'canal-cb', 'uuid-cb', 'texto', 'loja-1');
    checar(e2 && e2.ok === false && e2.erro === 'erro_envio' && e2.detalhe && e2.detalhe.etapa === 'falhou_sdk' && e2.detalhe.codigo === 800180
      && e2.detalhe.msg === 'Request timeout', '85 a forma antiga com callback (msg, err) tambem leva o motivo');
    const e3 = await enviar(sb, 'g3', 'tk-g3', 'canal-pr', 'uuid-pr', 'texto', 'loja-1');
    checar(e3 && e3.ok === false && e3.erro === 'erro_envio' && e3.detalhe && e3.detalhe.etapa === 'promessa' && e3.detalhe.codigo === 400111,
      '86 a promessa rejeitada vira etapa promessa (codigo em texto vira inteiro)');
    const e4 = await enviar(sb, 'g4', 'tk-g4', 'canal-th', 'uuid-th', 'texto', 'loja-1');
    checar(e4 && e4.ok === false && e4.erro === 'erro_envio' && e4.detalhe && e4.detalhe.etapa === 'excecao' && e4.detalhe.nome === 'TypeError'
      && e4.detalhe.codigo === null, '87 o throw no envio vira etapa excecao, com o nome do erro e sem codigo');
    sb.__arg = { id: 'g5', token: 'tk-sem-c', canal: 'canal-ok', orderUuid: 'uuid-ok', texto: 't', ws: 'loja-1' };
    rodar(sb, 'window.pdvSb.enviar(__arg)');
    const e5 = await esperar(sb, 'g5');
    checar(e5 && e5.erro === 'token' && e5.detalhe && e5.detalhe.etapa === 'token' && e5.detalhe.instancias === 0 && !('conexao' in e5.detalhe),
      '88 sem o token: etapa token, antes de procurar o SDK');
    const e6 = await enviar(sb, 'g6', 'tk-g6', 'canal-ok', 'uuid-outro', 't', 'loja-1');
    checar(e6 && e6.erro === 'canal_errado' && e6.detalhe && e6.detalhe.etapa === 'canal_errado' && e6.detalhe.instancia === 0 && e6.detalhe.conexao === 'OPEN',
      '89 canal de outro pedido: etapa canal_errado, com a instancia que achou o canal');
    const e7 = await enviar(sb, 'g7', 'tk-g7', 'canal-ok', 'uuid-ok', 't', 'loja-1');
    checar(e7 && e7.ok === true && e7.msgId === '991' && !('detalhe' in e7), '90 o envio que deu continua como era (sem detalhe)');
    for (let i = 0; i < 15; i++) await enviar(sb, 'gl' + i, 'tk-gl' + i, 'canal-ok', 'uuid-ok', 't', 'loja-1');
    const e8 = await enviar(sb, 'g8', 'tk-g8', 'canal-ok', 'uuid-ok', 't', 'loja-1');
    checar(e8 && e8.erro === 'limite' && e8.detalhe && e8.detalhe.etapa === 'limite', '91 o 21o envio do minuto: etapa limite');
    const dg = await diag(sb, 'gd', 'loja-1', []);
    checar(dg && dg.conexao === 'OPEN' && dg.achou === true, '92 o diagnostico leva o estado da conexao (o plano B espera OPEN)');
  }

  // ── MUNDO H (1.0.35): a conexao fechada aparece no detalhe e no diagnostico ──
  {
    const sb = mundo();
    rodar(sb, "var G = bootGestor('webpackChunkgestor_pedidos', 'loja-1', [{ url: 'canal-1', uuid: 'uuid-1', "
      + "falha: (function () { var e = new Error('Connection is closed.'); e.code = 800200; return e; })() }], 'gestor', 'CLOSED');");
    carregar(sb);
    const e1 = await enviar(sb, 'h1', 'tk-h1', 'canal-1', 'uuid-1', 'texto', 'loja-1');
    checar(e1 && e1.erro === 'erro_envio' && e1.detalhe && e1.detalhe.conexao === 'CLOSED' && e1.detalhe.codigo === 800200 && e1.detalhe.nome === 'Error',
      '93 conexao fechada: o detalhe diz CLOSED e o codigo 800200');
    rodar(sb, "G.inst.connectionState = 'esquisito';");
    const dh = await diag(sb, 'hd', 'loja-1', []);
    checar(dh && dh.conexao === 'OUTRO', '94 estado que nao e do contrato vira OUTRO');
  }

  // ── MUNDO E: o script carregado duas vezes na mesma pagina nao duplica ──
  {
    const sb = mundo();
    carregar(sb);
    const primeiro = rodar(sb, 'window.pdvSb');
    carregar(sb);
    checar(rodar(sb, 'window.pdvSb') === primeiro, '60 o script injetado duas vezes nao troca o pdvSb da pagina');
  }

  // ── MUNDO SAVASSI 15/10 (1.0.38, camada A): o modulo achado mas o singleton invalido; a heap salva ──
  {
    const sb = mundo();
    rodar(sb, "var G = bootGestor('webpackChunkgestor_pedidos', 'loja-1', [{ url: 'canal-1', uuid: 'uuid-1' }], 'primeira');"
      + "var prim = G.inst;"                                                    // a conexao viva de verdade
      + "G.SB.init({ userId: 'loja-1', nome: 'segunda', canais: {}, manter: true });"   // o singleton vira a segunda, a primeira segue viva
      + "G.SB.instance.currentUser = null;");                                   // e a segunda perde o usuario: singleton invalido
    carregar(sb);
    const d0 = await diag(sb, 'bh0', 'loja-1', ['canal-1']);
    checar(d0 && d0.modulos === 1 && d0.runtimes === 1 && d0.via_webpack === false && d0.via_react === false && d0.achou === false && d0.instancias === 0,
      '100 Savassi 15/10: o webpack acha o modulo mas o singleton e invalido (via_webpack:false, instancias:0)');
    checar(d0 && d0.singleton === 'sem_usuario' && d0.heap_validas === 0 && d0.via_heap === false,
      '101 o diagnostico diz o motivo: singleton sem_usuario, berco vazio (' + (d0 && d0.singleton) + ')');
    // a heap do C# (Runtime.queryObjects) entrega a instancia conectada ao berco (pdvSb.registrar)
    checar(rodar(sb, 'window.pdvSb.registrar([prim])') === 1, '102 a heap entrega a instancia conectada ao berco (camada A)');
    const d1 = await diag(sb, 'bh1', 'loja-1', ['canal-1']);
    checar(d1 && d1.achou === true && d1.instancias === 1 && d1.via_heap === true && d1.heap_validas === 1 && d1.heap_abertas === 1 && d1.via_webpack === false,
      '103 com a heap, o SDK e achado (via_heap, instancias:1) mesmo com o singleton invalido');
    const e1 = await enviar(sb, 'bh2', 'tk-bh', 'canal-1', 'uuid-1', 'Oi, Ana!', 'loja-1');
    checar(e1 && e1.ok === true && sb.__enviadas[sb.__enviadas.length - 1].por === 'primeira',
      '104 e a resposta sai pela instancia do berco (a conexao viva), nao pelo singleton');
    checar(rodar(sb, 'window.pdvSb.registrar([prim])') === 0, '105 registrar a mesma instancia de novo nao duplica');
  }

  // ── MUNDO PROTOTIPOS (1.0.38, camada A): os prototipos candidatos para a heap do C# ──
  {
    const sb = mundo();
    rodar(sb, "var G = bootGestor('webpackChunkgestor_pedidos', 'loja-1', [{ url: 'canal-1', uuid: 'uuid-1' }], 'p');");
    carregar(sb);
    checar(rodar(sb, '(window.pdvSb.prototipos() || []).length') >= 1, '110 prototipos() devolve ao menos um candidato (o C# pede queryObjects de cada)');
    checar(rodar(sb, "window.pdvSb.prototipos().some(function (p) { return p === G.SB.prototype; })") === true,
      '111 o prototipo candidato e o da classe SendbirdChat achada no modulo');
  }

  // ── MUNDO BERCO NO NASCIMENTO (1.0.38, camada B): o init embrulhado registra a conexao nova ──
  {
    const sb = mundo();
    rodar(sb, "var R = montarRuntime('webpackChunkberco', __marca({"
      + "'7001': function (module, exports, req) {"
      + "  __exec.push('7001');"
      + "  var host = 'wss://ws-app.sendbird.com';"
      + "  function SendbirdChat(){}"
      + "  SendbirdChat.prototype.connect = function(){ return Promise.resolve(); };"
      + "  Object.defineProperty(SendbirdChat, 'instance', { get: function(){ return null; }, configurable: true });"   // singleton sempre vazio
      + "  SendbirdChat.init = function (p) {"
      + "    var s = new SendbirdChat();"
      + "    s.currentUser = { userId: p.userId };"
      + "    s.connectionState = 'OPEN';"
      + "    s.groupChannel = { getChannel: function (u) { return Promise.resolve({ url: u, cachedMetaData: { orderUuid: p.uuid }, isFrozen: false,"
      + "      members: [{ metaData: { userType: 'CUSTOMER' } }],"
      + "      sendUserMessage: function (pp) { __enviadas.push({ por: 'berco', canal: u, texto: pp.message }); return { onSucceeded: function (cb) { cb({ messageId: 7 }); return this; }, onFailed: function () { return this; } }; } }); } };"
      + "    return s;"
      + "  };"
      + "  exports.ZP = SendbirdChat;"
      + "} }));");
    carregar(sb);
    const d0 = await diag(sb, 'bw0', 'loja-1', []);
    checar(d0 && d0.modulos === 1 && d0.via_berco === false && d0.heap_inst === 0 && d0.singleton === 'vazio',
      '120 o modulo e achado (singleton vazio); o init ainda nao aconteceu, berco vazio');
    rodar(sb, "R('7001').ZP.init({ userId: 'loja-1', uuid: 'uuid-7' });");   // troca de token: nasce uma conexao
    const d1 = await diag(sb, 'bw1', 'loja-1', []);
    checar(d1 && d1.achou === true && d1.instancias === 1 && d1.via_berco === true && d1.via_webpack === false && d1.heap_validas === 1,
      '121 o init embrulhado (camada B) registra a conexao nova no berco (via_berco), mesmo com o singleton vazio');
    const e1 = await enviar(sb, 'bw2', 'tk-bw', 'canal-7', 'uuid-7', 'Oi!', 'loja-1');
    checar(e1 && e1.ok === true && sb.__enviadas[sb.__enviadas.length - 1].por === 'berco', '122 e a resposta sai pela conexao do berco');
  }

  // ── MUNDO WEBSOCKET (1.0.38, camada B): o Proxy conta os sockets do Sendbird sem mudar nada ──
  {
    const sb = mundo();
    rodar(sb, "function FakeWS(url){ this.url = url; this.readyState = 1; } FakeWS.CONNECTING = 0; FakeWS.OPEN = 1; FakeWS.CLOSED = 3; window.WebSocket = FakeWS;");
    carregar(sb);
    await diag(sb, 'pw0', 'loja-1', []);   // instala o Proxy
    checar(rodar(sb, "(function(){ var w = new window.WebSocket('wss://ws-app.sendbird.com/?x=1'); var o = new window.WebSocket('wss://outro.example.com'); return (w instanceof FakeWS) && (window.WebSocket.OPEN === 1); })()") === true,
      '130 o Proxy do WebSocket mantem instanceof e as constantes');
    const d = await diag(sb, 'pw1', 'loja-1', []);
    checar(d && d.ws_sb_doc === 1 && d.ws_sb_abertos === 1, '131 o diagnostico conta so o socket do Sendbird (1 aberto), ignorando o de outro host');
  }

  // ── MUNDO PRIVACIDADE DO DIAG (1.0.38): os campos novos nunca levam segredo ──
  {
    const sb = mundo();
    rodar(sb, "var G = bootGestor('webpackChunkgestor_pedidos', 'loja-31994412725', [{ url: 'canal-1', uuid: 'uuid-1' }], 'p'); G.inst.token = 'tk-super-secreto';");
    carregar(sb);
    const d = await diag(sb, 'dp0', 'loja-31994412725', ['canal-1']);
    const novos = JSON.stringify({ singleton: d.singleton, singleton_conexao: d.singleton_conexao, versao_sdk: d.versao_sdk, bundle: d.bundle, globais_wp: d.globais_wp });
    checar(novos.indexOf('31994412725') < 0 && novos.indexOf('tk-super-secreto') < 0, '140 os campos de texto do diag novo nunca levam segredo (telefone, token)');
    checar(typeof d.singleton === 'string' && typeof d.fabricas === 'number' && typeof d.via_heap === 'boolean' && typeof d.mf === 'boolean' && typeof d.ws_sb_doc === 'number',
      '141 os campos novos existem com o tipo do contrato (enum, inteiro, booleano)');
    checar(d.versao_sdk === '4.19.9' && d.globais_wp === 'webpackChunkgestor_pedidos', '142 versao_sdk e globais_wp saem do bundle, nao de dado pessoal (' + d.versao_sdk + '/' + d.globais_wp + ')');
  }

  console.log(JSON.stringify(res));
})().catch(function (e) { console.error('harness: ' + (e && e.stack || e)); process.exit(3); });
""";
}
