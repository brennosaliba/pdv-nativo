using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O LOGIN DO GESTOR DE PEDIDOS SOZINHO (1.0.44, 17/10/2026, pedido do dono). Quando a sessao do Gestor cai, o
/// caixa pede as credenciais da loja ao ERP (edge gestor-login), preenche e envia a tela de login, escolhe
/// "e-mail" na escolha do canal do codigo, pede o codigo ao ERP (que o pede ao dono pelo WhatsApp), consulta a
/// cada 5 s por ate 15 min, digita e confirma. Nada e feito com o Gestor logado.
///
/// Tres pecas, cada uma provada aqui:
///  · <see cref="LoginDoGestor"/> (Nucleo, puro): a maquina de estados, os tetos (1 tentativa a cada 10 min; 3
///    falhas seguidas = 2 h de pausa e aviso ao dono pela edge), o contrato (corpos e leituras das 4 acoes) e o
///    mascaramento por formato (e-mail, codigo) e por valor (a senha) de qualquer linha de diag;
///  · <see cref="FluxoLoginGestor"/> (Nucleo, async): o roteiro inteiro, aqui com a nuvem e a pagina DE MENTIRA
///    e o relogio parado (a espera avanca o relogio em vez de dormir);
///  · o ScriptLogin de Telas/ChatIfood.xaml.cs, no node contra um DOM falso: a tela de login (campos por tipo,
///    nunca por classe), a escolha do canal por texto, a tela do codigo (one-time-code, 4 a 8 digitos, ou um
///    campo por digito).
/// Sem node na maquina a suite FALHA (nao pula).
/// </summary>
public static class TestesLoginGestor
{
    public static async Task RodarAsync(Action<bool, string> checar)
    {
        Contrato(checar);
        Mascara(checar);
        Maquina(checar);
        await FluxoAsync(checar);
        Fontes(checar);
        Script(checar);
    }

    // ── 1. o contrato com a edge gestor-login ─────────────────────────────────
    private static void Contrato(Action<bool, string> checar)
    {
        checar(LoginDoGestor.Edge == "gestor-login", "LG-1 a edge e gestor-login");
        var c = JsonDocument.Parse(LoginDoGestor.CorpoCredenciais("CAIXA-01")).RootElement;
        checar(c.GetProperty("acao").GetString() == "credenciais" && c.GetProperty("terminal").GetString() == "CAIXA-01",
            "LG-1a o corpo de credenciais leva acao e terminal");
        var p = JsonDocument.Parse(LoginDoGestor.CorpoPedirCodigo("CAIXA-01")).RootElement;
        checar(p.GetProperty("acao").GetString() == "pedir_codigo" && p.GetProperty("terminal").GetString() == "CAIXA-01",
            "LG-1b o corpo de pedir_codigo leva acao e terminal");
        var k = JsonDocument.Parse(LoginDoGestor.CorpoCodigo("CAIXA-01", "ped-7")).RootElement;
        checar(k.GetProperty("acao").GetString() == "codigo" && k.GetProperty("id").GetString() == "ped-7",
            "LG-1c o corpo de codigo leva o id do pedido");
        var u = JsonDocument.Parse(LoginDoGestor.CorpoUsado("CAIXA-01", "ped-7", false, "codigo recusado", new DateTime(2026, 10, 17, 12, 0, 0))).RootElement;
        checar(u.GetProperty("acao").GetString() == "usado" && u.GetProperty("id").GetString() == "ped-7"
               && u.GetProperty("ok").ValueKind == JsonValueKind.False && u.GetProperty("motivo").GetString() == "codigo recusado"
               && u.GetProperty("pausado_ate").GetString()!.StartsWith("2026-10-17T12:00:00", StringComparison.Ordinal),
            "LG-1d o corpo de usado leva id, ok, motivo e pausado_ate (o ERP avisa o dono quando ha pausa)");
        var u2 = JsonDocument.Parse(LoginDoGestor.CorpoUsado("CAIXA-01", null, true, null, null)).RootElement;
        checar(u2.GetProperty("ok").ValueKind == JsonValueKind.True && u2.GetProperty("id").ValueKind == JsonValueKind.Null
               && u2.GetProperty("pausado_ate").ValueKind == JsonValueKind.Null,
            "LG-1e usado sem pedido de codigo (entrou sem codigo) leva id nulo e ok");

        var cr = LoginDoGestor.LerCredenciais(200, "{\"email\":\"loja@americanday.com.br\",\"senha\":\"S3nh@!\"}");
        checar(cr.Ok && cr.Email == "loja@americanday.com.br" && cr.Senha == "S3nh@!" && !cr.Desligado, "LG-2 credenciais 200 com email e senha");
        var des = LoginDoGestor.LerCredenciais(200, "{\"ligado\":false}");
        checar(!des.Ok && des.Desligado && des.Motivo == "desligado no ERP", "LG-2a {ligado:false} e desligado no ERP, nao e falha");
        checar(!LoginDoGestor.LerCredenciais(200, "{\"erro\":\"sem_credenciais\"}").Ok
               && LoginDoGestor.LerCredenciais(200, "{\"erro\":\"sem_credenciais\"}").Motivo == "sem_credenciais",
            "LG-2b {erro} vira o motivo");
        checar(LoginDoGestor.LerCredenciais(200, "{\"email\":\"\",\"senha\":\"x\"}").Motivo == "credenciais incompletas", "LG-2c e-mail vazio e credenciais incompletas");
        checar(LoginDoGestor.LerCredenciais(404, "").Motivo == "a borda gestor-login ainda nao esta no ar" && LoginDoGestor.LerCredenciais(404, "").SemNuvem,
            "LG-2d 404 e a borda fora do ar (sem nuvem: nao conta falha)");
        checar(LoginDoGestor.LerCredenciais(-1, "").Motivo == "sem rede" && LoginDoGestor.LerCredenciais(-1, "").SemNuvem, "LG-2e -1 e sem rede");
        checar(LoginDoGestor.LerCredenciais(0, "").Motivo == "sem resposta da nuvem" && LoginDoGestor.LerCredenciais(0, "").SemNuvem, "LG-2f 0 e sem resposta");
        checar(LoginDoGestor.LerCredenciais(401, "").Motivo == "sem sessao na nuvem" && LoginDoGestor.LerCredenciais(401, "").SemNuvem, "LG-2g 401 e sem sessao");
        checar(LoginDoGestor.LerCredenciais(403, "{\"erro\":\"terminal sem loja\"}").Motivo == "terminal sem loja" && !LoginDoGestor.LerCredenciais(403, "").SemNuvem,
            "LG-2h 403 e recusa da borda, com o erro dela");
        checar(LoginDoGestor.LerCredenciais(200, "lixo{").Motivo == "resposta invalida", "LG-2i lixo e resposta invalida, sem lancar");
        checar(LoginDoGestor.LerCredenciais(500, "").Motivo == "http 500", "LG-2j 5xx vira http N");

        checar(LoginDoGestor.LerPedido(200, "{\"id\":\"ped-7\"}").Id == "ped-7", "LG-3 pedir_codigo devolve o id");
        checar(LoginDoGestor.LerPedido(200, "{}").Id is null && LoginDoGestor.LerPedido(200, "{}").Motivo == "pedido sem id", "LG-3a pedido sem id e motivo");
        checar(LoginDoGestor.LerPedido(-1, "").Motivo == "sem rede", "LG-3b pedido sem rede");
        var cod = LoginDoGestor.LerCodigo(200, "{\"codigo\":\"482913\"}");
        checar(cod.Codigo == "482913" && !cod.Esperando && !cod.Expirado, "LG-4 codigo chegou");
        checar(LoginDoGestor.LerCodigo(200, "{\"esperando\":true}").Esperando, "LG-4a esperando");
        checar(LoginDoGestor.LerCodigo(200, "{\"expirado\":true}").Expirado, "LG-4b expirado");
        var ruim = LoginDoGestor.LerCodigo(200, "{\"codigo\":\"ab\"}");
        checar(ruim.Codigo is null && ruim.Motivo == "codigo fora do formato", "LG-4c codigo que nao e de 4 a 8 digitos e recusado aqui");
        checar(LoginDoGestor.LerCodigo(0, "").Esperando && LoginDoGestor.LerCodigo(0, "").Motivo == "sem resposta da nuvem",
            "LG-4d sem resposta numa consulta e so esperar (a proxima consulta vem em 5 s)");
        checar(LoginDoGestor.LerCodigo(200, "{\"esperando\":true,\"codigo\":\"123456\"}").Codigo == "123456", "LG-4e o codigo vale mesmo com esperando junto");
    }

    // ── 2. o mascaramento ─────────────────────────────────────────────────────
    private static void Mascara(Action<bool, string> checar)
    {
        var m = LoginDoGestor.Mascarar("entrou com loja.savassi+x@americanday.com.br senha Abc123!x codigo 482913 e 4829 e 12345678", "Abc123!x");
        checar(!m.Contains("loja.savassi", StringComparison.Ordinal) && !m.Contains("americanday", StringComparison.Ordinal) && m.Contains("email@####", StringComparison.Ordinal),
            "LG-5 o e-mail vira email@#### pelo formato (" + m + ")");
        checar(!m.Contains("Abc123!x", StringComparison.Ordinal) && m.Contains("***", StringComparison.Ordinal), "LG-5a a senha some pelo valor (" + m + ")");
        checar(!m.Contains("482913", StringComparison.Ordinal) && !m.Contains("4829", StringComparison.Ordinal) && !m.Contains("12345678", StringComparison.Ordinal),
            "LG-5b todo numero de 4 a 8 digitos vira #### (" + m + ")");
        checar(LoginDoGestor.Mascarar("ok 10:12 em 3 s", null) == "ok 10:12 em 3 s", "LG-5c hora e numeros curtos ficam");
        checar(LoginDoGestor.Mascarar(null) == "" && LoginDoGestor.Mascarar("x", "", null) == "x", "LG-5d nulo e segredo vazio nao quebram");
        checar(LoginDoGestor.Mascarar("a senha e 4829 mesmo", "4829") == "a senha e *** mesmo", "LG-5e o segredo e trocado ANTES do formato (nao vira ####)");
    }

    // ── 3. a maquina de estados e os tetos ────────────────────────────────────
    private static void Maquina(Action<bool, string> checar)
    {
        var t0 = new DateTime(2026, 10, 17, 10, 0, 0);
        var e = new LoginDoGestor();
        checar(e.Fase == FaseLogin.Ocioso && e.PorQueNao(t0, false) is null && !e.EmAndamento, "LG-6 nasce ocioso e pode tentar");
        checar(e.LinhaDaConfiguracao() == "Login do Gestor: automatico (ERP) | ultimo: nenhum", "LG-6a a linha da Configuracao sem historico");
        e.Comecar(t0);
        checar(e.Fase == FaseLogin.Credenciais && e.EmAndamento && e.UltimaTentativa == t0, "LG-6b Comecar vai para credenciais e marca a hora");
        checar(e.PorQueNao(t0.AddSeconds(5), false) == "ja esta entrando", "LG-6c em andamento nao comeca outra");
        e.Preencheu(); checar(e.Fase == FaseLogin.Preencheu, "LG-6d preencheu");
        e.CanalEmail(); checar(e.Fase == FaseLogin.CanalEmail, "LG-6e canal e-mail");
        e.PedirCodigo(); checar(e.Fase == FaseLogin.PedirCodigo, "LG-6f pedindo o codigo");
        e.EsperandoCodigo("ped-1", t0.AddSeconds(20));
        checar(e.Fase == FaseLogin.EsperandoCodigo && e.PedidoId == "ped-1" && e.EsperandoDesde == t0.AddSeconds(20), "LG-6g esperando o codigo com o id");
        checar(!e.CodigoExpirou(t0.AddSeconds(20).AddMinutes(14)) && e.CodigoExpirou(t0.AddSeconds(20).AddMinutes(15).AddSeconds(1)), "LG-6h o codigo expira em 15 min");
        e.Digitou(); checar(e.Fase == FaseLogin.Digitou, "LG-6i digitou");
        e.Entrou(t0.AddMinutes(2));
        checar(e.Fase == FaseLogin.Entrou && !e.EmAndamento && e.FalhasSeguidas == 0 && e.Ultimo == "entrou 10:02" && e.PedidoId is null,
            "LG-6j entrou: zera as falhas, guarda 'entrou 10:02' e solta o pedido");
        checar(e.LinhaDaConfiguracao() == "Login do Gestor: automatico (ERP) | ultimo: entrou 10:02", "LG-6k a linha da Configuracao depois de entrar");
        checar(e.PorQueNao(t0.AddMinutes(3), false) == "ja tentei ha 3 min (1 a cada 10 min)" && e.PorQueNao(t0.AddMinutes(10), false) is null,
            "LG-7 1 tentativa a cada 10 min, contada do comeco da tentativa");
        checar(e.PorQueNao(t0.AddMinutes(3), true) is null, "LG-7a Entrar agora (forcado) passa pelo teto");

        // tres falhas seguidas = pausa de 2 h
        var t1 = t0.AddMinutes(10);
        e.Comecar(t1); var p1 = e.Falhou("ainda na tela de login", t1.AddSeconds(40));
        checar(!p1 && e.Fase == FaseLogin.Falhou && e.FalhasSeguidas == 1 && e.Ultimo == "falhou 10:10 (ainda na tela de login)" && e.PausadoAte is null,
            "LG-8 a primeira falha so conta (" + e.Ultimo + ")");
        var t2 = t1.AddMinutes(10); e.Comecar(t2); var p2 = e.Falhou("codigo nao chegou em 15 min", t2.AddMinutes(15));
        checar(!p2 && e.FalhasSeguidas == 2, "LG-8a a segunda tambem");
        var t3 = t2.AddMinutes(20); e.Comecar(t3); var p3 = e.Falhou("codigo recusado", t3.AddMinutes(1));
        checar(p3 && e.FalhasSeguidas == 3 && e.PausadoAte == t3.AddMinutes(1).AddHours(2), "LG-8b a terceira pausa o automatico por 2 h");
        checar(e.PorQueNao(t3.AddMinutes(30), false) == "pausado ate 12:41 (3 falhas seguidas)", "LG-8c pausado diz ate quando (" + e.PorQueNao(t3.AddMinutes(30), false) + ")");
        checar(e.PorQueNao(t3.AddMinutes(30), true) is null, "LG-8d Entrar agora passa pela pausa");
        checar(e.PorQueNao(t3.AddMinutes(1).AddHours(2).AddSeconds(1), false) is null, "LG-8e passadas as 2 h volta sozinho");
        checar(e.LinhaDaConfiguracao() == "Login do Gestor: automatico (ERP) | ultimo: falhou 10:41 (codigo recusado) | pausado ate 12:41",
            "LG-8f a linha mostra a pausa (" + e.LinhaDaConfiguracao() + ")");
        e.Comecar(t3.AddHours(3)); e.Entrou(t3.AddHours(3).AddMinutes(1));
        checar(e.FalhasSeguidas == 0 && e.PausadoAte is null, "LG-8g entrar zera as falhas e a pausa");

        // desligado no ERP e sem nuvem: nao sao falhas
        var d = new LoginDoGestor();
        d.Comecar(t0); d.Desligado(t0.AddSeconds(1));
        checar(d.Fase == FaseLogin.Ocioso && d.FalhasSeguidas == 0 && d.DesligadoNoErp && d.LinhaDaConfiguracao().StartsWith("Login do Gestor: desligado no ERP | ", StringComparison.Ordinal),
            "LG-9 desligado no ERP: fica ocioso, sem falha, e a linha diz desligado (" + d.LinhaDaConfiguracao() + ")");
        checar(d.PorQueNao(t0.AddMinutes(3), false) == "ja tentei ha 3 min (1 a cada 10 min)", "LG-9a desligado tambem respeita o teto (nao pergunta ao ERP a cada 5 s)");
        d.Comecar(t0.AddMinutes(10)); d.SemNuvem("sem rede", t0.AddMinutes(10).AddSeconds(2));
        checar(d.Fase == FaseLogin.Ocioso && d.FalhasSeguidas == 0 && d.Ultimo == "sem rede 10:10", "LG-9b sem rede: ocioso, sem falha, com o registro (" + d.Ultimo + ")");
        var j = new LoginDoGestor();
        j.JaLogado();
        checar(j.Fase == FaseLogin.Ocioso && j.UltimaTentativa is null && j.Ultimo is null, "LG-9c o Gestor ja logado nao conta tentativa nem muda nada");
    }

    // ── 4. o fluxo com a nuvem e a pagina de mentira ──────────────────────────
    private sealed class Mundo
    {
        public DateTime Agora = new(2026, 10, 17, 10, 0, 0);
        public string Tela = "login";
        public readonly List<string> Acoes = new();
        public readonly List<string> Corpos = new();
        public readonly List<string> Js = new();
        public readonly List<string> Diag = new();
        public readonly LoginDoGestor Estado = new();
        public Func<string, (int, string)> Nuvem = _ => (500, "");
        public int ConsultasAteOCodigo = 2;
        public string Codigo = "482913";
        public bool CanalNaTela = true;
        public bool SenhaErrada;
        public bool CodigoErrado;
        public bool AlguemEntrouNaEspera;
        public string ResultadoPreencher = "enviado";
        public string ResultadoDigitar = "enviado";
        public TimeSpan Esperado = TimeSpan.Zero;

        public Mundo()
        {
            var consultas = 0;
            Nuvem = corpo =>
            {
                var acao = JsonDocument.Parse(corpo).RootElement.GetProperty("acao").GetString();
                switch (acao)
                {
                    case "credenciais": return (200, "{\"email\":\"loja@americanday.com.br\",\"senha\":\"S3nh@!xyz\"}");
                    case "pedir_codigo": return (200, "{\"id\":\"ped-7\"}");
                    case "codigo":
                        consultas++;
                        if (AlguemEntrouNaEspera) { Tela = "logado"; return (200, "{\"esperando\":true}"); }
                        return consultas > ConsultasAteOCodigo ? (200, "{\"codigo\":\"" + Codigo + "\"}") : (200, "{\"esperando\":true}");
                    case "usado": return (200, "{\"ok\":true}");
                }
                return (400, "");
            };
        }

        public FluxoLoginGestor Fluxo() => new(Estado, "CAIXA-01",
            corpo =>
            {
                Corpos.Add(corpo);
                Acoes.Add(JsonDocument.Parse(corpo).RootElement.GetProperty("acao").GetString()!);
                return Task.FromResult(Nuvem(corpo));
            },
            js =>
            {
                Js.Add(js);
                if (js.Contains("pdvLoginTela", StringComparison.Ordinal)) return Task.FromResult("\"" + Tela + "\"");
                if (js.Contains("pdvLoginPreencher", StringComparison.Ordinal))
                {
                    if (ResultadoPreencher == "enviado") Tela = SenhaErrada ? "login" : (CanalNaTela ? "canal" : "codigo");
                    return Task.FromResult("\"" + ResultadoPreencher + "\"");
                }
                if (js.Contains("pdvLoginEscolherEmail", StringComparison.Ordinal)) { Tela = "codigo"; return Task.FromResult("true"); }
                if (js.Contains("pdvLoginDigitar", StringComparison.Ordinal))
                {
                    if (ResultadoDigitar == "enviado") Tela = CodigoErrado ? "codigo" : "logado";
                    return Task.FromResult("\"" + ResultadoDigitar + "\"");
                }
                return Task.FromResult("null");
            },
            Diag.Add, () => Agora,
            (ts, ct) => { Agora += ts; Esperado += ts; return ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask; });
    }

    private static async Task FluxoAsync(Action<bool, string> checar)
    {
        // o caminho feliz, com a escolha do canal
        var m = new Mundo();
        var ok = await m.Fluxo().TentarAsync(false, CancellationToken.None);
        checar(ok && m.Estado.Fase == FaseLogin.Entrou && m.Estado.Ultimo == "entrou 10:00", "LG-10 o caminho feliz entra (" + string.Join(" | ", m.Diag) + ")");
        checar(string.Join(",", m.Acoes) == "credenciais,pedir_codigo,codigo,codigo,codigo,usado",
            "LG-10a as chamadas, na ordem: credenciais, pedir_codigo, codigo ate chegar, usado (" + string.Join(",", m.Acoes) + ")");
        checar(m.Js.Any(j => j.Contains("pdvLoginPreencher", StringComparison.Ordinal) && j.Contains("loja@americanday.com.br", StringComparison.Ordinal) && j.Contains("S3nh@!xyz", StringComparison.Ordinal)),
            "LG-10b o e-mail e a senha chegam a pagina (so a ela)");
        checar(m.Js.Any(j => j.Contains("pdvLoginEscolherEmail", StringComparison.Ordinal)) && m.Js.Any(j => j.Contains("pdvLoginDigitar", StringComparison.Ordinal) && j.Contains("482913", StringComparison.Ordinal)),
            "LG-10c escolhe o e-mail e digita o codigo");
        var tudo = string.Join("\n", m.Diag);
        checar(m.Diag.Any(d => d == "login do gestor: pedido") && m.Diag.Any(d => d.StartsWith("login do gestor: codigo pedido", StringComparison.Ordinal))
               && m.Diag.Any(d => d.StartsWith("login do gestor: codigo recebido", StringComparison.Ordinal)) && m.Diag.Any(d => d.StartsWith("login do gestor: entrou", StringComparison.Ordinal)),
            "LG-10d o diag conta pedido, codigo pedido, codigo recebido e entrou (" + tudo.Replace('\n', '|') + ")");
        checar(!tudo.Contains("loja@", StringComparison.Ordinal) && !tudo.Contains("S3nh@", StringComparison.Ordinal) && !tudo.Contains("482913", StringComparison.Ordinal),
            "LG-10e nenhum segredo no diag");
        var usado = JsonDocument.Parse(m.Corpos.Last()).RootElement;
        checar(usado.GetProperty("ok").ValueKind == JsonValueKind.True && usado.GetProperty("id").GetString() == "ped-7", "LG-10f usado com ok e o id do pedido");
        checar(m.Esperado >= TimeSpan.FromSeconds(10) && m.Esperado < TimeSpan.FromMinutes(2), "LG-10g esperou os 5 s entre as consultas (" + m.Esperado + ")");
        checar(!m.Corpos.Any(c => c.Contains("S3nh@", StringComparison.Ordinal) || c.Contains("482913", StringComparison.Ordinal)), "LG-10h nenhum corpo mandado leva senha ou codigo");

        // sem a escolha do canal: da tela de login direto para a do codigo
        var m2 = new Mundo { CanalNaTela = false };
        checar(await m2.Fluxo().TentarAsync(false, CancellationToken.None) && !m2.Js.Any(j => j.Contains("pdvLoginEscolherEmail", StringComparison.Ordinal)),
            "LG-11 sem a tela do canal nao ha escolha, e entra do mesmo jeito");

        // o codigo nunca chega: 15 min e falha, usado com ok=false
        var m3 = new Mundo { ConsultasAteOCodigo = int.MaxValue };
        var r3 = await m3.Fluxo().TentarAsync(false, CancellationToken.None);
        checar(!r3 && m3.Estado.Fase == FaseLogin.Falhou && m3.Estado.FalhasSeguidas == 1 && m3.Estado.Ultimo == "falhou 10:15 (codigo nao chegou em 15 min)",
            "LG-12 codigo que nao chega em 15 min e falha (" + m3.Estado.Ultimo + ")");
        var u3 = JsonDocument.Parse(m3.Corpos.Last()).RootElement;
        checar(m3.Acoes.Last() == "usado" && u3.GetProperty("ok").ValueKind == JsonValueKind.False && u3.GetProperty("motivo").GetString() == "codigo nao chegou em 15 min",
            "LG-12a usado leva ok=false e o motivo");
        checar(m3.Acoes.Count(a => a == "codigo") is >= 170 and <= 182, "LG-12b consultou a cada 5 s por 15 min (" + m3.Acoes.Count(a => a == "codigo") + ")");

        // o ERP diz expirado
        var m4 = new Mundo();
        m4.Nuvem = corpo => JsonDocument.Parse(corpo).RootElement.GetProperty("acao").GetString() switch
        {
            "credenciais" => (200, "{\"email\":\"a@b.c\",\"senha\":\"x\"}"),
            "pedir_codigo" => (200, "{\"id\":\"ped-8\"}"),
            "codigo" => (200, "{\"expirado\":true}"),
            _ => (200, "{}"),
        };
        checar(!await m4.Fluxo().TentarAsync(false, CancellationToken.None) && m4.Estado.Ultimo == "falhou 10:00 (codigo expirou no ERP)", "LG-13 expirado no ERP e falha (" + m4.Estado.Ultimo + ")");

        // senha errada: a tela de login continua
        var m5 = new Mundo { SenhaErrada = true };
        checar(!await m5.Fluxo().TentarAsync(false, CancellationToken.None) && m5.Estado.Ultimo == "falhou 10:00 (ainda na tela de login)" && !m5.Acoes.Contains("pedir_codigo"),
            "LG-14 ainda na tela de login depois de 30 s e falha, sem pedir codigo (" + m5.Estado.Ultimo + ")");
        checar(m5.Esperado >= TimeSpan.FromSeconds(30) && m5.Esperado <= TimeSpan.FromSeconds(40), "LG-14a esperou os 30 s da tela (" + m5.Esperado + ")");

        // codigo recusado
        var m6 = new Mundo { CodigoErrado = true };
        checar(!await m6.Fluxo().TentarAsync(false, CancellationToken.None) && m6.Estado.Ultimo == "falhou 10:00 (codigo recusado)", "LG-15 codigo recusado e falha (" + m6.Estado.Ultimo + ")");

        // tres falhas seguidas: pausa de 2 h, usado leva pausado_ate, diag avisa; a quarta automatica nao roda; a forcada roda
        var m7 = new Mundo { SenhaErrada = true };
        await m7.Fluxo().TentarAsync(false, CancellationToken.None);
        m7.Agora = m7.Agora.AddMinutes(10); await m7.Fluxo().TentarAsync(false, CancellationToken.None);
        m7.Agora = m7.Agora.AddMinutes(10); await m7.Fluxo().TentarAsync(false, CancellationToken.None);
        checar(m7.Estado.FalhasSeguidas == 3 && m7.Estado.PausadoAte is not null, "LG-16 tres falhas seguidas pausam");
        var u7 = JsonDocument.Parse(m7.Corpos.Last()).RootElement;
        checar(u7.GetProperty("acao").GetString() == "usado" && u7.GetProperty("pausado_ate").ValueKind == JsonValueKind.String, "LG-16a o ultimo usado leva pausado_ate (o ERP avisa o dono)");
        checar(m7.Diag.Last().Contains("pausado por 2 h", StringComparison.Ordinal) && m7.Diag.Last().Contains("dono avisado", StringComparison.Ordinal), "LG-16b o diag diz da pausa e do aviso (" + m7.Diag.Last() + ")");
        m7.Agora = m7.Agora.AddMinutes(10);
        var antes = m7.Acoes.Count;
        checar(!await m7.Fluxo().TentarAsync(false, CancellationToken.None) && m7.Acoes.Count == antes && m7.Diag.Last().StartsWith("login do gestor: nao agora (pausado ate", StringComparison.Ordinal),
            "LG-16c pausado: a tentativa automatica nem chama a nuvem (" + m7.Diag.Last() + ")");
        m7.SenhaErrada = false;
        checar(await m7.Fluxo().TentarAsync(true, CancellationToken.None) && m7.Estado.FalhasSeguidas == 0 && m7.Estado.PausadoAte is null, "LG-16d Entrar agora passa pela pausa e, entrando, zera tudo");

        // o teto de 10 min entre tentativas automaticas
        var m8 = new Mundo { SenhaErrada = true };
        await m8.Fluxo().TentarAsync(false, CancellationToken.None);
        m8.Agora = m8.Agora.AddMinutes(2);
        var antes8 = m8.Acoes.Count;
        checar(!await m8.Fluxo().TentarAsync(false, CancellationToken.None) && m8.Acoes.Count == antes8 && m8.Diag.Last() == "login do gestor: nao agora (ja tentei ha 2 min (1 a cada 10 min))",
            "LG-17 2 min depois de uma tentativa, a automatica espera (" + m8.Diag.Last() + ")");

        // desligado no ERP: nao e falha, a linha diz desligado
        var m9 = new Mundo();
        m9.Nuvem = _ => (200, "{\"ligado\":false}");
        checar(!await m9.Fluxo().TentarAsync(false, CancellationToken.None) && m9.Estado.DesligadoNoErp && m9.Estado.FalhasSeguidas == 0 && m9.Acoes.Count == 1
               && m9.Diag.Last() == "login do gestor: desligado no ERP",
            "LG-18 desligado no ERP: uma chamada, sem falha, diag diz (" + m9.Diag.Last() + ")");

        // sem rede: nao e falha, nem usado
        var m10 = new Mundo();
        m10.Nuvem = _ => (-1, "");
        checar(!await m10.Fluxo().TentarAsync(false, CancellationToken.None) && m10.Estado.FalhasSeguidas == 0 && m10.Estado.Fase == FaseLogin.Ocioso && m10.Acoes.Count == 1
               && m10.Diag.Last() == "login do gestor: sem rede",
            "LG-19 sem rede: nao conta falha e nao manda usado (" + m10.Diag.Last() + ")");

        // o Gestor ja esta logado: nada
        var m11 = new Mundo { Tela = "logado" };
        checar(!await m11.Fluxo().TentarAsync(false, CancellationToken.None) && m11.Acoes.Count == 0 && m11.Estado.UltimaTentativa is null && m11.Diag.Last() == "login do gestor: o Gestor ja esta logado",
            "LG-20 ja logado: nenhuma chamada e nenhuma tentativa contada");
        var m11b = new Mundo { Tela = "sem_script" };
        checar(!await m11b.Fluxo().TentarAsync(false, CancellationToken.None) && m11b.Acoes.Count == 0 && m11b.Diag.Last() == "login do gestor: nao agora (a pagina diz sem_script)",
            "LG-20a sem o script na pagina nao ha tentativa (" + m11b.Diag.Last() + ")");

        // alguem entrou na mao enquanto esperavamos o codigo: entrou, usado ok
        var m12 = new Mundo { AlguemEntrouNaEspera = true };
        checar(await m12.Fluxo().TentarAsync(false, CancellationToken.None) && m12.Estado.Fase == FaseLogin.Entrou && m12.Acoes.Last() == "usado"
               && m12.Diag.Any(d => d.Contains("entrou (alguem entrou na mao)", StringComparison.Ordinal)),
            "LG-21 alguem logou na mao durante a espera: entrou, usado ok (" + string.Join("|", m12.Diag) + ")");

        // a pagina sem os campos
        var m13 = new Mundo { ResultadoPreencher = "sem_campos" };
        checar(!await m13.Fluxo().TentarAsync(false, CancellationToken.None) && m13.Estado.Ultimo == "falhou 10:00 (a pagina diz sem_campos)" && !m13.Acoes.Contains("pedir_codigo"),
            "LG-22 a pagina sem os campos e falha (" + m13.Estado.Ultimo + ")");

        // sem reentrada: enquanto roda, a segunda chamada volta na hora
        var m14 = new Mundo();
        var tcs = new TaskCompletionSource<(int, string)>();
        var f14 = new FluxoLoginGestor(m14.Estado, "CAIXA-01", _ => tcs.Task, js => Task.FromResult("\"login\""), m14.Diag.Add, () => m14.Agora, (_, _) => Task.CompletedTask);
        var primeira = f14.TentarAsync(false, CancellationToken.None);
        var segunda = await f14.TentarAsync(false, CancellationToken.None);
        checar(!segunda && f14.Rodando && m14.Diag.Last() == "login do gestor: nao agora (ja esta entrando)", "LG-23 sem reentrada (" + m14.Diag.Last() + ")");
        tcs.SetResult((-1, ""));
        await primeira;
        checar(!f14.Rodando, "LG-23a terminou e soltou");

        // cancelado no meio (o controle foi recriado): nao conta falha
        var m15 = new Mundo { ConsultasAteOCodigo = int.MaxValue, CanalNaTela = false };
        using var cts = new CancellationTokenSource();
        var n = 0;
        var padrao15 = m15.Nuvem;
        m15.Nuvem = corpo => { if (++n == 3) cts.Cancel(); return padrao15(corpo); };
        var r15 = await m15.Fluxo().TentarAsync(false, cts.Token);
        checar(!r15 && m15.Estado.FalhasSeguidas == 0 && m15.Estado.Fase == FaseLogin.Ocioso && m15.Diag.Last() == "login do gestor: cancelado" && !m15.Acoes.Contains("usado"),
            "LG-24 cancelado no meio: ocioso, sem falha, sem usado (" + m15.Diag.Last() + ")");

        // a pagina ja na tela do codigo (alguem digitou a senha e saiu): segue dali, sem pedir credenciais
        var m16 = new Mundo { Tela = "codigo" };
        checar(await m16.Fluxo().TentarAsync(false, CancellationToken.None) && !m16.Acoes.Contains("credenciais") && m16.Acoes.First() == "pedir_codigo" && m16.Estado.Fase == FaseLogin.Entrou,
            "LG-25 ja na tela do codigo: pede o codigo direto, sem credenciais (" + string.Join(",", m16.Acoes) + ")");
    }

    // ── 5. os fontes: a tela, o servico, a Configuracao, a versao ─────────────
    private static void Fontes(Action<bool, string> checar)
    {
        var tela = Ler("Telas/ChatIfood.xaml.cs");
        var servico = Ler("ServicoLoginGestor.cs");
        var nucleo = Ler("Pdv.Nucleo/LoginDoGestor.cs");
        var confXaml = Ler("Telas/Configuracao.xaml");
        var confCs = Ler("Telas/Configuracao.xaml.cs");
        var testes = Ler("Pdv.Testes/TestesLoginGestor.cs");
        checar(tela.Contains("await core.AddScriptToExecuteOnDocumentCreatedAsync(ScriptLogin);")
               && tela.IndexOf("AddScriptToExecuteOnDocumentCreatedAsync(ScriptSendbird)", StringComparison.Ordinal) < tela.IndexOf("AddScriptToExecuteOnDocumentCreatedAsync(ScriptLogin)", StringComparison.Ordinal),
            "LG-30 a tela injeta o ScriptLogin depois do ScriptSendbird");
        checar(tela.Contains("ServicoLoginGestor.Observar(_gestorPedeLogin);"), "LG-30a cada leitura do login (a cada 5 s) chega ao servico");
        checar(tela.Contains("ServicoLoginGestor.Ligar(") && tela.Contains("LoginDoGestor.Edge") && tela.Contains("LoginDoGestor.Prazo"),
            "LG-30b a tela liga o servico com a nuvem (edge gestor-login, com prazo) e a pagina");
        checar(servico.Contains("public static void Observar(bool pedeLogin)") && servico.Contains("Task<string> EntrarAgoraAsync()")
               && servico.Contains("LoginDoGestor.Mascarar("),
            "LG-30c o servico observa, tem o Entrar agora e mascara o que anota");
        checar(confXaml.Contains("x:Name=\"BlocoLoginGestor\"") && confXaml.Contains("x:Name=\"TxtLoginGestor\"") && confXaml.Contains("x:Name=\"BtnEntrarAgora\"")
               && confXaml.Contains("Text=\"Entrar agora\"") && confXaml.Contains("Click=\"EntrarAgora\""),
            "LG-30d a Configuracao tem a linha do login do Gestor e o botao Entrar agora");
        checar(confCs.Contains("private async void EntrarAgora(object sender, RoutedEventArgs e)") && confCs.Contains("ServicoLoginGestor.EntrarAgoraAsync()")
               && confCs.Contains("ServicoLoginGestor.Linha"),
            "LG-30e o botao chama o servico e a linha vem dele");
        checar(Ler("Pdv.csproj").Contains("<Version>1.0.44</Version>", StringComparison.Ordinal), "LG-31 a versao do caixa e 1.0.44 (o login do Gestor sozinho)");
        var novos = new[] { servico, nucleo, testes };
        checar(novos.All(t => t.Length > 0 && !t.Contains((char)0x2014) && !t.Contains((char)0x2013)), "LG-32 os arquivos novos nao tem travessao");
        var script = TestesScriptSendbird.ExtrairRaw(tela, "ScriptLogin") ?? "";
        checar(script.Length > 500 && !script.Contains((char)0x2014) && !script.Contains((char)0x2013), "LG-33 o ScriptLogin existe e nao tem travessao");
        checar(!Regex.IsMatch(script, @"querySelector(All)?\(\s*['""]\.") && !script.Contains("[class", StringComparison.Ordinal) && !script.Contains("className", StringComparison.Ordinal)
               && !script.Contains("getElementsByClassName", StringComparison.Ordinal),
            "LG-33a o script nunca procura por classe (so por tipo, atributo e texto)");
        checar(!Regex.IsMatch(servico, @"Diag\((?!LoginDoGestor\.Mascarar)") || servico.Contains("Mascarar("), "LG-33b toda anotacao do servico passa pelo mascaramento");
        checar(Ler("Pdv.Testes/Program.cs").Contains("TestesLoginGestor.RodarAsync("), "LG-34 a suite esta registrada no gate");
    }

    // ── 6. o script no node contra o DOM falso ────────────────────────────────
    private static void Script(Action<bool, string> checar)
    {
        var tela = Ler("Telas/ChatIfood.xaml.cs");
        var script = TestesScriptSendbird.ExtrairRaw(tela, "ScriptLogin");
        if (script is null) { checar(false, "LG-40 o ScriptLogin sai da constante de Telas/ChatIfood.xaml.cs"); return; }
        var pasta = Path.Combine(Path.GetTempPath(), "pdv-lg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(pasta);
        try
        {
            var arqScript = Path.Combine(pasta, "script-login.js");
            var arqTeste = Path.Combine(pasta, "teste-login.js");
            File.WriteAllText(arqScript, script, new UTF8Encoding(false));
            File.WriteAllText(arqTeste, Harness, new UTF8Encoding(false));
            var check = TestesScriptSendbird.Node(pasta, "--check", arqScript);
            checar(check.Exit == 0, $"LG-40 o script injetado passa no node --check ({check.Erro.Trim()})");
            var r = TestesScriptSendbird.Node(pasta, arqTeste, arqScript);
            List<ItemHarness>? itens = null;
            try
            {
                var ultima = r.Saida.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("[", StringComparison.Ordinal));
                if (ultima is not null) itens = JsonSerializer.Deserialize<List<ItemHarness>>(ultima);
            }
            catch { itens = null; }
            checar(r.Exit == 0 && itens is { Count: > 10 }, $"LG-41 o node rodou o script contra o DOM falso (saida {r.Exit}, {itens?.Count ?? 0} conferencias) {Corta(r.Erro)}");
            foreach (var i in itens ?? new List<ItemHarness>()) checar(i.ok, "LG-" + i.nome);
        }
        finally
        {
            try { Directory.Delete(pasta, true); } catch { }
        }
    }

    private sealed record ItemHarness(bool ok, string nome);
    private static string Corta(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim();

    private static string Ler(string relativo)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var caminho = Path.Combine(dir, relativo);
            if (File.Exists(caminho)) return File.ReadAllText(caminho);
            dir = Path.GetDirectoryName(dir);
        }
        return "";
    }

    // ── o harness em node: um DOM de mentira so com o que o script toca ──────
    private const string Harness = """
'use strict';
const fs = require('fs');
const vm = require('vm');
const codigo = fs.readFileSync(process.argv[2], 'utf8');
const res = [];
function checar(ok, nome) { res.push({ ok: !!ok, nome: nome }); }

let seq = 0;
function El(tag, opts) {
  opts = opts || {};
  this.tagName = tag.toUpperCase();
  this.id = opts.id || '';
  this._attrs = Object.assign({}, opts.attrs || {});
  this._text = opts.text || '';
  this._rect = opts.rect || { left: 0, top: 0, width: 0, height: 0 };
  this.children = [];
  this.parentElement = null;
  this.clicks = 0; this.focos = 0; this.eventos = []; this.value = ''; this.checked = false;
  this._seq = seq++;
}
El.prototype.appendChild = function (c) { c.parentElement = this; this.children.push(c); return c; };
El.prototype.getAttribute = function (n) { return Object.prototype.hasOwnProperty.call(this._attrs, n) ? this._attrs[n] : null; };
El.prototype.setAttribute = function (n, v) { this._attrs[n] = String(v); };
El.prototype.hasAttribute = function (n) { return Object.prototype.hasOwnProperty.call(this._attrs, n); };
El.prototype.getBoundingClientRect = function () { const r = this._rect; return { left: r.left, top: r.top, width: r.width, height: r.height, right: r.left + r.width, bottom: r.top + r.height }; };
Object.defineProperty(El.prototype, 'textContent', {
  get: function () { let t = this._text; this.children.forEach(function (c) { t += c.textContent; }); return t; },
  set: function (v) { this._text = String(v); this.children = []; },
});
Object.defineProperty(El.prototype, 'type', { get: function () { return this.getAttribute('type') || (this.tagName === 'INPUT' ? 'text' : ''); } });
Object.defineProperty(El.prototype, 'maxLength', { get: function () { const m = this.getAttribute('maxlength'); return m === null ? -1 : parseInt(m, 10); } });
Object.defineProperty(El.prototype, 'disabled', { get: function () { return this.hasAttribute('disabled'); } });
Object.defineProperty(El.prototype, 'form', { get: function () { let n = this.parentElement; while (n) { if (n.tagName === 'FORM') return n; n = n.parentElement; } return null; } });
El.prototype.contains = function (x) { let n = x; while (n) { if (n === this) return true; n = n.parentElement; } return false; };
El.prototype.descendentes = function () { const out = []; (function walk(n) { n.children.forEach(function (c) { out.push(c); walk(c); }); })(this); return out; };
El.prototype.click = function () { this.clicks++; if (this.tagName === 'LABEL' && this.getAttribute('for')) { const alvo = document.getElementById(this.getAttribute('for')); if (alvo) { alvo.clicks++; alvo.checked = true; } } if (this.type === 'radio') this.checked = true; };
El.prototype.focus = function () { this.focos++; };
El.prototype.dispatchEvent = function (ev) { this.eventos.push(ev.type); return true; };
El.prototype.requestSubmit = function () { this.clicks++; };
El.prototype.submit = function () { this.clicks++; };
function casaUm(el, sel) {
  sel = sel.trim();
  if (sel === '*') return true;
  const m = /^([a-zA-Z0-9]*)((?:\[[^\]]+\])*)$/.exec(sel);
  if (!m) throw new Error('seletor nao suportado pelo harness: ' + sel);
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
const document = {
  body: body, documentElement: body,
  querySelectorAll: function (s) { return body.querySelectorAll(s); },
  querySelector: function (s) { return body.querySelector(s); },
  getElementById: function (id) { return body.descendentes().find(function (e) { return e.id === id; }) || null; },
  createElement: function (tag) { return new El(tag); },
  addEventListener: function () {},
};
function Event(tipo, opts) { this.type = tipo; this.bubbles = !!(opts && opts.bubbles); }
function KeyboardEvent(tipo, opts) { this.type = tipo; this.key = opts && opts.key; this.bubbles = !!(opts && opts.bubbles); }
const R = function (l, t, w, h) { return { left: l, top: t, width: w, height: h }; };
const sb = {
  console: console, document: document, Event: Event, KeyboardEvent: KeyboardEvent,
  location: { href: 'https://gestordepedidos.ifood.com.br/' },
  setTimeout: function (fn) { fn(); return 1; },
};
sb.window = sb; sb.self = sb;
vm.createContext(sb);
vm.runInContext(codigo, sb);

// ── as telas do Gestor, como o login do iFood as monta (sem classe nenhuma de proposito) ──
let email, senha, entrar, form, oculto;
function telaLogin(opts) {
  opts = opts || {};
  body.children = [];
  const topo = body.appendChild(new El('div', { rect: R(0, 0, 1024, 60) }));
  topo.appendChild(new El('img', { attrs: { alt: 'iFood' }, rect: R(20, 10, 80, 40) }));
  form = body.appendChild(new El('form', { rect: R(300, 100, 400, 400) }));
  form.appendChild(new El('h1', { text: 'Entrar no Gestor de Pedidos', rect: R(300, 100, 400, 40) }));
  const lbl = form.appendChild(new El('label', { text: 'E-mail', rect: R(300, 150, 400, 20) }));
  email = form.appendChild(new El('input', { attrs: opts.emailComoTexto ? { type: 'text', name: 'email', placeholder: 'Digite seu e-mail' } : { type: 'email', name: 'email' }, rect: R(300, 170, 400, 44) }));
  form.appendChild(new El('label', { text: 'Senha', rect: R(300, 230, 400, 20) }));
  senha = form.appendChild(new El('input', { attrs: { type: 'password', name: 'password' }, rect: R(300, 250, 400, 44) }));
  form.appendChild(new El('a', { text: 'Esqueci minha senha', attrs: { href: '/recuperar' }, rect: R(300, 300, 150, 20) }));
  entrar = form.appendChild(new El('button', { text: opts.textoDoBotao || 'Entrar', attrs: opts.semTipoSubmit ? {} : { type: 'submit' }, rect: R(300, 340, 400, 48) }));
  form.appendChild(new El('button', { text: 'Criar conta', attrs: { type: 'button' }, rect: R(300, 400, 400, 48) }));
  return form;
}
let btnEmail, btnSms, continuar, radioEmail, radioSms, lblEmail;
function telaCanal(opts) {
  opts = opts || {};
  body.children = [];
  const box = body.appendChild(new El('div', { rect: R(300, 100, 400, 400) }));
  box.appendChild(new El('h2', { text: 'Confirme que e voce', rect: R(300, 100, 400, 40) }));
  box.appendChild(new El('p', { text: 'Escolha como quer receber o codigo de verificacao', rect: R(300, 150, 400, 40) }));
  if (opts.radios) {
    radioSms = box.appendChild(new El('input', { id: 'op-sms', attrs: { type: 'radio', name: 'canal', value: 'sms' }, rect: R(300, 200, 20, 20) }));
    box.appendChild(new El('label', { text: 'SMS para (31) *****-1234', attrs: { for: 'op-sms' }, rect: R(330, 200, 300, 20) }));
    radioEmail = box.appendChild(new El('input', { id: 'op-email', attrs: { type: 'radio', name: 'canal', value: 'email' }, rect: R(300, 240, 20, 20) }));
    lblEmail = box.appendChild(new El('label', { text: 'E-mail para l***@americanday.com.br', attrs: { for: 'op-email' }, rect: R(330, 240, 300, 20) }));
    continuar = box.appendChild(new El('button', { text: 'Continuar', attrs: { type: 'submit' }, rect: R(300, 300, 400, 48) }));
    btnEmail = null; btnSms = null;
  } else {
    btnSms = box.appendChild(new El('button', { attrs: { type: 'button' }, rect: R(300, 200, 400, 56) }));
    btnSms.appendChild(new El('span', { text: 'Receber por SMS', rect: R(310, 210, 200, 20) }));
    btnEmail = box.appendChild(new El('button', { attrs: { type: 'button' }, rect: R(300, 270, 400, 56) }));
    btnEmail.appendChild(new El('span', { text: opts.semHifen ? 'Receber por email' : 'Receber por e-mail', rect: R(310, 280, 200, 20) }));
    continuar = null; radioEmail = null; radioSms = null;
  }
}
let campoCod, digitos, confirmar, reenviar;
function telaCodigo(opts) {
  opts = opts || {};
  body.children = [];
  const box = body.appendChild(new El('div', { rect: R(300, 100, 400, 400) }));
  box.appendChild(new El('h2', { text: 'Digite o codigo que enviamos para o seu e-mail', rect: R(300, 100, 400, 40) }));
  digitos = [];
  if (opts.umPorDigito) {
    campoCod = null;
    for (let i = 0; i < 6; i++) digitos.push(box.appendChild(new El('input', { attrs: { type: 'text', inputmode: 'numeric', maxlength: '1', 'aria-label': 'digito ' + (i + 1) }, rect: R(300 + i * 60, 170, 48, 56) })));
  } else {
    campoCod = box.appendChild(new El('input', { attrs: opts.semOneTime ? { type: 'tel', inputmode: 'numeric', maxlength: '6', name: 'code' } : { type: 'text', autocomplete: 'one-time-code', inputmode: 'numeric', maxlength: '6' }, rect: R(300, 170, 400, 56) }));
  }
  reenviar = box.appendChild(new El('button', { text: 'Reenviar codigo', attrs: { type: 'button' }, rect: R(300, 240, 200, 40) }));
  confirmar = box.appendChild(new El('button', { text: opts.textoDoBotao || 'Confirmar', attrs: opts.semTipoSubmit ? { type: 'button' } : { type: 'submit' }, rect: R(300, 300, 400, 48) }));
}
function telaLogada() {
  body.children = [];
  const nav = body.appendChild(new El('nav', { rect: R(0, 0, 1024, 60) }));
  nav.appendChild(new El('button', { text: 'Pedidos', attrs: { type: 'button' }, rect: R(20, 10, 100, 40) }));
  nav.appendChild(new El('button', { attrs: { 'aria-label': 'Conversas' }, rect: R(960, 10, 44, 40) }));
  const busca = body.appendChild(new El('input', { attrs: { type: 'text', placeholder: 'Buscar pedido' }, rect: R(200, 10, 300, 40) }));
  body.appendChild(new El('div', { text: 'Pedidos de hoje', rect: R(0, 80, 1024, 600) }));
  return busca;
}

(function () {
  checar(['pdvLoginTela', 'pdvLoginPreencher', 'pdvLoginEscolherEmail', 'pdvLoginDigitar'].every(function (f) { return typeof sb[f] === 'function'; }),
    '50 o script expoe pdvLoginTela, pdvLoginPreencher, pdvLoginEscolherEmail e pdvLoginDigitar');

  // a tela de login
  telaLogin();
  checar(sb.pdvLoginTela() === 'login', '51 a tela com input[type=password] e login (' + sb.pdvLoginTela() + ')');
  const p1 = sb.pdvLoginPreencher('loja@americanday.com.br', 'S3nh@!xyz');
  checar(p1 === 'enviado' && email.value === 'loja@americanday.com.br' && senha.value === 'S3nh@!xyz', '52 preenche o e-mail (type=email) e a senha pelos tipos e devolve enviado (' + p1 + ')');
  checar(email.eventos.indexOf('input') >= 0 && senha.eventos.indexOf('input') >= 0 && email.focos > 0, '52a cada campo recebe foco e o evento input (o React ouve)');
  checar(entrar.clicks === 1 && form.querySelectorAll('button')[1].clicks === 0, '52b toca no botao de enviar (type=submit), nao no Criar conta');
  checar(typeof p1 === 'string' && p1.indexOf('@') < 0 && p1.indexOf('S3nh') < 0, '52c a resposta nunca leva o que foi digitado');

  telaLogin({ emailComoTexto: true, semTipoSubmit: true, textoDoBotao: 'Acessar' });
  const p2 = sb.pdvLoginPreencher('loja@americanday.com.br', 'x');
  checar(p2 === 'enviado' && email.value === 'loja@americanday.com.br' && senha.value === 'x' && entrar.clicks === 1,
    '53 com o e-mail como input[type=text] (nome/placeholder) e o botao sem type=submit (texto Acessar) tambem preenche e envia (' + p2 + ')');

  telaLogin({ semTipoSubmit: true, textoDoBotao: 'Ir' });
  const p3 = sb.pdvLoginPreencher('a@b.c', 'x');
  checar(p3 === 'enviado' && (form.clicks === 1 || senha.eventos.indexOf('keydown') >= 0), '54 sem botao reconhecivel, envia o form (requestSubmit) ou o Enter na senha (' + p3 + ')');

  telaLogada();
  checar(sb.pdvLoginTela() === 'logado', '55 a tela do Gestor logado (sem senha, sem codigo) e logado (' + sb.pdvLoginTela() + ')');
  checar(sb.pdvLoginPreencher('a@b.c', 'x') === 'sem_campos', '55a preencher sem os campos devolve sem_campos');

  // a escolha do canal
  telaCanal();
  checar(sb.pdvLoginTela() === 'canal', '56 a tela com "Receber por e-mail" e canal (' + sb.pdvLoginTela() + ')');
  const c1 = sb.pdvLoginEscolherEmail();
  checar(c1 === true && btnEmail.clicks === 1 && btnSms.clicks === 0, '56a escolhe o botao do e-mail pelo texto, nunca o do SMS (' + c1 + ')');
  telaCanal({ semHifen: true });
  checar(sb.pdvLoginTela() === 'canal' && sb.pdvLoginEscolherEmail() === true && btnEmail.clicks === 1 && btnSms.clicks === 0, '56b "email" sem hifen tambem');
  telaCanal({ radios: true });
  checar(sb.pdvLoginTela() === 'canal', '56c a tela com opcoes (radio + rotulo) e canal (' + sb.pdvLoginTela() + ')');
  const c3 = sb.pdvLoginEscolherEmail();
  checar(c3 === true && (radioEmail.clicks + lblEmail.clicks) >= 1 && radioSms.clicks === 0 && continuar.clicks === 1,
    '56d marca a opcao do e-mail (radio ou rotulo) e toca em Continuar; o SMS fica (' + c3 + ')');
  telaLogada();
  checar(sb.pdvLoginEscolherEmail() === false, '56e sem a opcao do e-mail devolve false');

  // a tela do codigo
  telaCodigo();
  checar(sb.pdvLoginTela() === 'codigo', '57 o input autocomplete=one-time-code e a tela do codigo (' + sb.pdvLoginTela() + ')');
  const d1 = sb.pdvLoginDigitar('482913');
  checar(d1 === 'enviado' && campoCod.value === '482913' && confirmar.clicks === 1 && reenviar.clicks === 0, '57a digita o codigo e toca em Confirmar, nunca em Reenviar (' + d1 + ')');
  telaCodigo({ semOneTime: true });
  checar(sb.pdvLoginTela() === 'codigo' && sb.pdvLoginDigitar('4829') === 'enviado' && campoCod.value === '4829', '57b o input numerico com maxlength 6 (sem one-time-code) tambem e o codigo');
  telaCodigo({ umPorDigito: true });
  checar(sb.pdvLoginTela() === 'codigo', '57c seis campos de um digito sao a tela do codigo (' + sb.pdvLoginTela() + ')');
  const d3 = sb.pdvLoginDigitar('482913');
  checar(d3 === 'enviado' && digitos.map(function (d) { return d.value; }).join('') === '482913' && confirmar.clicks === 1, '57d um digito em cada campo, na ordem, e Confirmar (' + d3 + ')');
  telaCodigo({ semTipoSubmit: true, textoDoBotao: 'Verificar' });
  checar(sb.pdvLoginDigitar('482913') === 'enviado' && confirmar.clicks === 1, '57e o botao pelo texto (Verificar) quando nao ha type=submit');
  telaLogada();
  checar(sb.pdvLoginDigitar('482913') === 'sem_campo', '57f sem o campo do codigo devolve sem_campo');
  checar(sb.pdvLoginDigitar('12') === 'codigo_invalido' && sb.pdvLoginDigitar('abc123') === 'codigo_invalido', '57g codigo fora de 4 a 8 digitos nao e digitado');

  // o que esta escondido nao conta
  telaLogada();
  oculto = body.appendChild(new El('input', { attrs: { type: 'password' }, rect: R(0, 0, 0, 0) }));
  checar(sb.pdvLoginTela() === 'logado', '58 um campo de senha sem tamanho (escondido) nao faz a tela ser login (' + sb.pdvLoginTela() + ')');
  checar(sb.pdvLoginPreencher('a@b.c', 'x') === 'sem_campos', '58a nem e preenchido');

  // a tela de login pela URL, sem campos ainda (a pagina carregando)
  body.children = [];
  sb.location.href = 'https://gestordepedidos.ifood.com.br/login';
  checar(sb.pdvLoginTela() === 'carregando', '59 a URL de login sem campos na tela e carregando (' + sb.pdvLoginTela() + ')');
  sb.location.href = 'https://gestordepedidos.ifood.com.br/';
  checar(sb.pdvLoginTela() === 'outra', '59a sem nada na tela e sem URL de login e outra (' + sb.pdvLoginTela() + ')');

  // carregado duas vezes nao quebra
  let estourou = false;
  try { vm.runInContext(codigo, sb); } catch (e) { estourou = true; }
  telaLogin();
  checar(!estourou && sb.pdvLoginTela() === 'login', '60 o script injetado duas vezes na mesma pagina continua funcionando');
  console.log(JSON.stringify(res));
})();
""";
}
