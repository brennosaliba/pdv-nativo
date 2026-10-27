using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O LOGIN DO GESTOR DE PEDIDOS SOZINHO (1.0.44, 17/10/2026; 1.0.50, 25/10/2026: a tela real do Gestor 9.350, um
/// aparelho por vez com a TV, captcha e cadastro da verificacao param e avisam). Quando a sessao do Gestor cai, o
/// caixa pede as credenciais da loja ao ERP (edge gestor-login, com o terminal), preenche o e-mail, depois a senha,
/// escolhe "E-mail" na escolha do canal do codigo, pede o codigo ao ERP (que o pede ao dono pelo WhatsApp), consulta a
/// cada 5 s por ate 15 min, digita e confirma. Nada e feito com o Gestor logado.
///
/// Tres pecas, cada uma provada aqui:
///  · <see cref="LoginDoGestor"/> (Nucleo, puro): a maquina de estados, os tetos (1 tentativa a cada 10 min; 3
///    falhas seguidas = 2 h de pausa e aviso ao dono pela edge; captcha/cadastro = pausa na hora), o contrato (corpos
///    e leituras das 4 acoes) e o mascaramento por formato (e-mail, codigo) e por valor (a senha) de qualquer linha de diag;
///  · <see cref="FluxoLoginGestor"/> (Nucleo, async): o roteiro inteiro, aqui com a nuvem e a pagina DE MENTIRA
///    e o relogio parado (a espera avanca o relogio em vez de dormir);
///  · o ScriptLogin de Telas/ChatIfood.xaml.cs, no node contra um DOM falso com as telas do Keycloak do iFood
///    (Pdv.Testes/script_login.test.cjs): e-mail, senha, as duas juntas, a escolha do canal (radios otpChannel), o
///    codigo (OTPInput com o oculto name=otpCode), o cadastro da verificacao, o captcha, a pagina expirada.
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
            "LG-1a o corpo de credenciais leva acao e o terminal (SQL 222: um aparelho por vez por loja)");
        var c0 = JsonDocument.Parse(LoginDoGestor.CorpoCredenciais()).RootElement;
        checar(c0.GetProperty("acao").GetString() == "credenciais" && !c0.TryGetProperty("terminal", out _), "LG-1a2 sem terminal o corpo e so {acao} (o contrato do 193)");
        var p = JsonDocument.Parse(LoginDoGestor.CorpoPedirCodigo("CAIXA-01")).RootElement;
        checar(p.GetProperty("acao").GetString() == "pedir_codigo" && p.GetProperty("terminal").GetString() == "CAIXA-01",
            "LG-1b o corpo de pedir_codigo leva acao e terminal");
        var k = JsonDocument.Parse(LoginDoGestor.CorpoCodigo("7")).RootElement;
        checar(k.GetProperty("acao").GetString() == "codigo" && k.GetProperty("id").ValueKind == JsonValueKind.Number && k.GetProperty("id").GetInt64() == 7,
            "LG-1c o corpo de codigo leva o id do pedido como NUMERO (identity do ERP)");
        checar(JsonDocument.Parse(LoginDoGestor.CorpoCodigo("ped-x")).RootElement.GetProperty("id").GetString() == "ped-x", "LG-1c2 um id que nao e numero vai como texto");
        var u = JsonDocument.Parse(LoginDoGestor.CorpoUsado("CAIXA-01", "7", false, "codigo recusado", new DateTime(2026, 10, 17, 12, 0, 0))).RootElement;
        checar(u.GetProperty("acao").GetString() == "usado" && u.GetProperty("id").GetInt64() == 7 && u.GetProperty("terminal").GetString() == "CAIXA-01"
               && u.GetProperty("ok").ValueKind == JsonValueKind.False && u.GetProperty("motivo").GetString() == "codigo recusado"
               && u.GetProperty("pausado_ate").GetString()!.StartsWith("2026-10-17T12:00:00", StringComparison.Ordinal),
            "LG-1d o corpo de usado leva id (numero), terminal, ok, motivo e pausado_ate (o ERP avisa o dono quando ha pausa)");
        var pa = u.GetProperty("pausado_ate").GetString()!;
        checar(Regex.IsMatch(pa, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}$") && pa == "2026-10-17T12:00:00",
            "LG-1d2 pausado_ate sai exatamente como yyyy-MM-ddTHH:mm:ss, sem Z e sem fuso (" + pa + ")");
        var local = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Local);
        var emUtc = local.ToUniversalTime();
        checar(JsonDocument.Parse(LoginDoGestor.CorpoUsado("CAIXA-01", "7", false, "x", emUtc)).RootElement.GetProperty("pausado_ate").GetString() == "2026-10-17T12:00:00",
            "LG-1d3 um DateTime em UTC vira a hora local antes de sair (a hora que o caixa mostra)");
        var u2 = JsonDocument.Parse(LoginDoGestor.CorpoUsado("CAIXA-01", null, true, null, null)).RootElement;
        checar(u2.GetProperty("ok").ValueKind == JsonValueKind.True && u2.GetProperty("id").ValueKind == JsonValueKind.Null
               && u2.GetProperty("pausado_ate").ValueKind == JsonValueKind.Null,
            "LG-1e usado sem pedido de codigo (entrou sem codigo) leva id nulo e ok");

        var cr = LoginDoGestor.LerCredenciais(200, "{\"ok\":true,\"email\":\"loja@americanday.com.br\",\"senha\":\"S3nh@!\",\"codigo_via\":\"whatsapp\"}");
        checar(cr.Ok && cr.Email == "loja@americanday.com.br" && cr.Senha == "S3nh@!" && !cr.Desligado && !cr.NaoConta, "LG-2 credenciais {ok:true, email, senha, codigo_via}");
        var des = LoginDoGestor.LerCredenciais(200, "{\"ok\":false,\"motivo\":\"desligado\"}");
        checar(!des.Ok && des.Desligado && des.Motivo == "desligado no ERP", "LG-2a {ok:false, motivo:desligado} e desligado no ERP, nao e falha");
        var semCad = LoginDoGestor.LerCredenciais(200, "{\"ok\":false,\"motivo\":\"sem_cadastro\"}");
        checar(!semCad.Ok && semCad.NaoConta && semCad.Motivo == "sem cadastro no ERP", "LG-2b sem_cadastro = sem login automatico, nao conta falha");
        var outro = LoginDoGestor.LerCredenciais(200, "{\"ok\":false,\"motivo\":\"outro_aparelho_entrando\",\"terminal\":\"TV DESKTOP\",\"desde\":\"2026-10-25T15:01:00Z\"}");
        checar(!outro.Ok && outro.NaoConta && !outro.Desligado && outro.Motivo == "outro aparelho esta entrando",
            "LG-2b2 SQL 222: outro_aparelho_entrando = a vez e do outro (caixa ou TV), nao conta falha (" + outro.Motivo + ")");
        checar(LoginDoGestor.LerCredenciais(200, "{\"ok\":false,\"motivo\":\"outra_coisa\"}") is { Ok: false, NaoConta: false, Motivo: "outra_coisa" }, "LG-2b3 outro motivo conta como falha");
        checar(LoginDoGestor.LerCredenciais(200, "{\"ok\":true,\"email\":\"\",\"senha\":\"x\"}").Motivo == "credenciais incompletas", "LG-2c e-mail vazio e credenciais incompletas");
        checar(LoginDoGestor.LerCredenciais(404, "").Motivo == "a borda gestor-login ainda nao esta no ar" && LoginDoGestor.LerCredenciais(404, "").NaoConta,
            "LG-2d 404 sem corpo e a borda fora do ar (nao conta falha)");
        var sql = LoginDoGestor.LerCredenciais(404, "{\"ok\":false,\"motivo\":\"sem_sql_193\"}");
        checar(sql.NaoConta && sql.Motivo == "sem login automatico (SQL 193 nao rodou)", "LG-2d2 404 sem_sql_193 = sem login automatico, nao conta falha");
        checar(LoginDoGestor.LerCredenciais(-1, "").Motivo == "sem rede" && LoginDoGestor.LerCredenciais(0, "").Motivo == "sem resposta da nuvem", "LG-2e -1 e sem rede; 0 e sem resposta");
        checar(LoginDoGestor.LerCredenciais(401, "{\"ok\":false,\"motivo\":\"nao_autorizado\"}").Motivo == "sem sessao na nuvem" && LoginDoGestor.LerCredenciais(401, "").NaoConta, "LG-2g 401 nao_autorizado e sem sessao");
        checar(LoginDoGestor.LerCredenciais(200, "lixo{").Motivo == "resposta invalida", "LG-2i lixo e resposta invalida, sem lancar");

        var ped = LoginDoGestor.LerPedido(200, "{\"ok\":true,\"id\":7,\"ja_aberto\":false,\"aviso\":{\"enviado\":true,\"motivo\":null}}");
        checar(ped.Id == "7" && ped.Motivo is null && !ped.JaAberto && ped.AvisoFalhou is null, "LG-3 pedir_codigo devolve o id inteiro e o aviso enviado");
        checar(LoginDoGestor.LerPedido(200, "{\"ok\":true,\"id\":8,\"ja_aberto\":true,\"aviso\":{\"enviado\":true}}").JaAberto, "LG-3a2 ja_aberto e lido");
        var semAviso = LoginDoGestor.LerPedido(200, "{\"ok\":true,\"id\":9,\"ja_aberto\":false,\"aviso\":{\"enviado\":false,\"motivo\":\"sem telefone do dono\"}}");
        checar(semAviso.Id == "9" && semAviso.AvisoFalhou == "sem telefone do dono", "LG-3a3 aviso.enviado=false traz o motivo (o dono nao recebeu)");
        checar(LoginDoGestor.LerPedido(200, "{\"ok\":false,\"motivo\":\"desligado\"}").Desligado && LoginDoGestor.LerPedido(200, "{\"ok\":false,\"motivo\":\"sem_cadastro\"}").NaoConta,
            "LG-3a4 pedir_codigo recusado por desligado/sem_cadastro nao conta falha");
        var cod = LoginDoGestor.LerCodigo(200, "{\"ok\":true,\"codigo\":\"482913\",\"id\":7}");
        checar(cod.Codigo == "482913" && !cod.Esperando && !cod.Expirado, "LG-4 codigo chegou");
        checar(LoginDoGestor.LerCodigo(200, "{\"ok\":true,\"esperando\":true,\"id\":7}").Esperando, "LG-4a esperando");
        checar(LoginDoGestor.LerCodigo(200, "{\"ok\":true,\"expirado\":true,\"id\":7}").Expirado, "LG-4b expirado");
        checar(LoginDoGestor.LerCodigo(200, "{\"ok\":false,\"motivo\":\"sem_pedido\"}").Motivo == "sem_pedido", "LG-4b2 {ok:false, motivo:sem_pedido} e recusa (nao e esperar)");
        checar(LoginDoGestor.LerCodigo(200, "{\"ok\":true,\"codigo\":\"ab\"}").Motivo == "codigo fora do formato", "LG-4c codigo que nao e de 4 a 8 digitos e recusado aqui");
        checar(LoginDoGestor.LerCodigo(0, "").Esperando, "LG-4d sem resposta numa consulta e so esperar (a proxima consulta vem em 5 s)");
    }

    // ── 2. o mascaramento ─────────────────────────────────────────────────────
    private static void Mascara(Action<bool, string> checar)
    {
        var m = LoginDoGestor.Mascarar("entrou com loja.savassi+x@americanday.com.br senha Abc123!x codigo 482913 e 4829 e 12345678", "Abc123!x");
        checar(!m.Contains("loja.savassi", StringComparison.Ordinal) && m.Contains("email@####", StringComparison.Ordinal), "LG-5 o e-mail vira email@#### pelo formato (" + m + ")");
        checar(!m.Contains("Abc123!x", StringComparison.Ordinal) && m.Contains("***", StringComparison.Ordinal), "LG-5a a senha some pelo valor");
        checar(!m.Contains("482913", StringComparison.Ordinal) && !m.Contains("4829", StringComparison.Ordinal) && !m.Contains("12345678", StringComparison.Ordinal), "LG-5b todo numero de 4 a 8 digitos vira ####");
        checar(LoginDoGestor.Mascarar("ok 10:12 em 3 s", null) == "ok 10:12 em 3 s", "LG-5c hora e numeros curtos ficam");
        checar(LoginDoGestor.Mascarar("a senha e 4829 mesmo", "4829") == "a senha e *** mesmo", "LG-5e o segredo e trocado ANTES do formato (nao vira ####)");
    }

    // ── 3. a maquina de estados e os tetos ────────────────────────────────────
    private static void Maquina(Action<bool, string> checar)
    {
        var t0 = new DateTime(2026, 10, 17, 10, 0, 0);
        var e = new LoginDoGestor();
        checar(e.Fase == FaseLogin.Ocioso && e.PorQueNao(t0, false) is null && !e.EmAndamento, "LG-6 nasce ocioso e pode tentar");
        e.Comecar(t0);
        checar(e.Fase == FaseLogin.Credenciais && e.EmAndamento && e.UltimaTentativa == t0, "LG-6b Comecar vai para credenciais e marca a hora");
        checar(e.PorQueNao(t0.AddSeconds(5), false) == "ja esta entrando", "LG-6c em andamento nao comeca outra");
        e.Preencheu(); e.SenhaEnviada(); checar(e.Fase == FaseLogin.SenhaEnviada, "LG-6d e-mail preenchido, senha enviada");
        e.CanalEmail(); e.PedirCodigo(); e.EsperandoCodigo("ped-1", t0.AddSeconds(20));
        checar(e.Fase == FaseLogin.EsperandoCodigo && e.PedidoId == "ped-1", "LG-6g esperando o codigo com o id");
        checar(!e.CodigoExpirou(t0.AddSeconds(20).AddMinutes(14)) && e.CodigoExpirou(t0.AddSeconds(20).AddMinutes(15).AddSeconds(1)), "LG-6h o codigo expira em 15 min");
        e.Digitou(); e.Entrou(t0.AddMinutes(2));
        checar(e.Fase == FaseLogin.Entrou && !e.EmAndamento && e.FalhasSeguidas == 0 && e.Ultimo == "entrou 10:02" && e.PedidoId is null, "LG-6j entrou: zera as falhas e guarda 'entrou 10:02'");
        checar(e.PorQueNao(t0.AddMinutes(3), false) == "ja tentei ha 3 min (1 a cada 10 min)" && e.PorQueNao(t0.AddMinutes(10), false) is null, "LG-7 1 tentativa a cada 10 min");
        checar(e.PorQueNao(t0.AddMinutes(3), true) is null, "LG-7a Entrar agora (forcado) passa pelo teto");

        var t1 = t0.AddMinutes(10);
        e.Comecar(t1); var p1 = e.Falhou("senha recusada", t1.AddSeconds(40));
        checar(!p1 && e.FalhasSeguidas == 1 && e.PausadoAte is null, "LG-8 a primeira falha so conta");
        var t2 = t1.AddMinutes(10); e.Comecar(t2); e.Falhou("codigo nao chegou em 15 min", t2.AddMinutes(15));
        var t3 = t2.AddMinutes(20); e.Comecar(t3); var p3 = e.Falhou("codigo recusado", t3.AddMinutes(1));
        checar(p3 && e.FalhasSeguidas == 3 && e.PausadoAte == t3.AddMinutes(1).AddHours(2), "LG-8b a terceira pausa o automatico por 2 h");
        checar(e.PorQueNao(t3.AddMinutes(30), false) == "pausado ate 12:41 (3 falhas seguidas)" && e.PorQueNao(t3.AddMinutes(30), true) is null, "LG-8c pausado diz ate quando; Entrar agora passa");
        e.Comecar(t3.AddHours(3)); e.Entrou(t3.AddHours(3).AddMinutes(1));
        checar(e.FalhasSeguidas == 0 && e.PausadoAte is null, "LG-8g entrar zera as falhas e a pausa");

        // 1.0.50: captcha e cadastro da verificacao pausam NA HORA
        var cap = new LoginDoGestor();
        cap.Comecar(t0); var pc = cap.Falhou("captcha na tela de login", t0.AddSeconds(3), pausarAgora: true);
        checar(pc && cap.FalhasSeguidas == 1 && cap.PausadoAte == t0.AddSeconds(3).AddHours(2) && cap.Ultimo == "falhou 10:00 (captcha na tela de login)",
            "LG-8h captcha: a primeira falha ja pausa por 2 h (insistir nao adianta) (" + cap.Ultimo + ")");
        checar(cap.PorQueNao(t0.AddMinutes(30), false) is not null && cap.PorQueNao(t0.AddMinutes(30), true) is null, "LG-8i pausado pelo captcha; Entrar agora passa");

        var d = new LoginDoGestor();
        d.Comecar(t0); d.Desligado(t0.AddSeconds(1));
        checar(d.Fase == FaseLogin.Ocioso && d.FalhasSeguidas == 0 && d.DesligadoNoErp && d.LinhaDaConfiguracao().StartsWith("Login do Gestor: desligado no ERP | ", StringComparison.Ordinal), "LG-9 desligado no ERP: ocioso, sem falha");
        d.Comecar(t0.AddMinutes(10)); d.SemNuvem("sem rede", t0.AddMinutes(10).AddSeconds(2));
        checar(d.FalhasSeguidas == 0 && d.Ultimo == "sem rede 10:10", "LG-9b sem rede: ocioso, sem falha, com o registro");
        checar(LoginDoGestor.TelasDeEntrada.SequenceEqual(new[] { "email", "senha", "login", "canal", "codigo" }) && LoginDoGestor.TelasQueParam.SequenceEqual(new[] { "captcha", "mfa_cadastro" }),
            "LG-9d as telas que o roteiro atravessa e as que o param");
        checar(LoginDoGestor.EsperaDaTv == TimeSpan.FromMinutes(3), "LG-9e a TV espera 3 min depois de ver o login antes de tentar (o caixa vai primeiro)");
    }

    // ── 4. o fluxo com a nuvem e a pagina de mentira (as telas do Gestor 9.350) ──
    private sealed class Mundo
    {
        public DateTime Agora = new(2026, 10, 17, 10, 0, 0);
        public string Tela = "email";
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
        public bool EmailErrado;
        public bool CodigoErrado;
        public bool AlguemEntrouNaEspera;
        public bool CaptchaDepoisDaSenha;
        public bool MfaDepoisDaSenha;
        public bool CarregandoDepoisDoCodigo;
        public string ResultadoEmail = "enviado";
        public string ResultadoSenha = "enviado";
        public string ResultadoDigitar = "enviado";
        public string Mensagem = "";
        public TimeSpan Esperado = TimeSpan.Zero;
        public int Voltas;

        public Mundo()
        {
            var consultas = 0;
            Nuvem = corpo =>
            {
                var acao = JsonDocument.Parse(corpo).RootElement.GetProperty("acao").GetString();
                switch (acao)
                {
                    case "credenciais": return (200, "{\"ok\":true,\"email\":\"loja@americanday.com.br\",\"senha\":\"S3nh@!xyz\",\"codigo_via\":\"whatsapp\"}");
                    case "pedir_codigo": return (200, "{\"ok\":true,\"id\":7,\"ja_aberto\":false,\"aviso\":{\"enviado\":true,\"motivo\":null}}");
                    case "codigo":
                        consultas++;
                        if (AlguemEntrouNaEspera) { Tela = "logado"; return (200, "{\"ok\":true,\"esperando\":true,\"id\":7}"); }
                        return consultas > ConsultasAteOCodigo ? (200, "{\"ok\":true,\"codigo\":\"" + Codigo + "\",\"id\":7}") : (200, "{\"ok\":true,\"esperando\":true,\"id\":7}");
                    case "usado": return (200, "{\"ok\":true,\"id\":7,\"tinha_pedido\":true}");
                }
                return (400, "");
            };
        }

        private int _carregando;

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
                if (js.Contains("pdvLoginTela", StringComparison.Ordinal))
                {
                    // "carregando" segura a tela por duas leituras e depois mostra o Gestor logado
                    if (Tela == "carregando" && ++_carregando >= 2) { Tela = "logado"; _carregando = 0; }
                    return Task.FromResult("\"" + Tela + "\"");
                }
                if (js.Contains("pdvLoginMensagem", StringComparison.Ordinal)) return Task.FromResult("\"" + Mensagem + "\"");
                if (js.Contains("pdvLoginVoltar", StringComparison.Ordinal)) { Voltas++; Tela = "email"; return Task.FromResult("true"); }
                if (js.Contains("pdvLoginEmail", StringComparison.Ordinal))
                {
                    if (ResultadoEmail == "enviado") Tela = EmailErrado ? "email" : "senha";
                    return Task.FromResult("\"" + ResultadoEmail + "\"");
                }
                if (js.Contains("pdvLoginPreencher", StringComparison.Ordinal) || js.Contains("pdvLoginSenha", StringComparison.Ordinal))
                {
                    if (ResultadoSenha == "enviado")
                        Tela = SenhaErrada ? "senha" : CaptchaDepoisDaSenha ? "captcha" : MfaDepoisDaSenha ? "mfa_cadastro" : (CanalNaTela ? "canal" : "codigo");
                    return Task.FromResult("\"" + ResultadoSenha + "\"");
                }
                if (js.Contains("pdvLoginEscolherEmail", StringComparison.Ordinal)) { Tela = "codigo"; return Task.FromResult("true"); }
                if (js.Contains("pdvLoginDigitar", StringComparison.Ordinal))
                {
                    if (ResultadoDigitar == "enviado") Tela = CodigoErrado ? "codigo" : CarregandoDepoisDoCodigo ? "carregando" : "logado";
                    return Task.FromResult("\"" + ResultadoDigitar + "\"");
                }
                return Task.FromResult("null");
            },
            Diag.Add, () => Agora,
            (ts, ct) => { Agora += ts; Esperado += ts; return ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask; });
    }

    private static async Task FluxoAsync(Action<bool, string> checar)
    {
        // o caminho feliz da tela real: e-mail, senha, escolha do canal, codigo, logado
        var m = new Mundo();
        var ok = await m.Fluxo().TentarAsync(false, CancellationToken.None);
        checar(ok && m.Estado.Fase == FaseLogin.Entrou && m.Estado.Ultimo == "entrou 10:00", "LG-10 o caminho feliz entra (" + string.Join(" | ", m.Diag) + ")");
        checar(string.Join(",", m.Acoes) == "credenciais,pedir_codigo,codigo,codigo,codigo,usado",
            "LG-10a as chamadas, na ordem: credenciais, pedir_codigo, codigo ate chegar, usado (" + string.Join(",", m.Acoes) + ")");
        checar(JsonDocument.Parse(m.Corpos[0]).RootElement.GetProperty("terminal").GetString() == "CAIXA-01", "LG-10a2 credenciais leva o terminal (um aparelho por vez)");
        var ordem = m.Js.Where(j => j.Contains("pdvLoginEmail(", StringComparison.Ordinal) || j.Contains("pdvLoginSenha(", StringComparison.Ordinal) || j.Contains("pdvLoginEscolherEmail", StringComparison.Ordinal) || j.Contains("pdvLoginDigitar(", StringComparison.Ordinal))
            .Select(j => j.Contains("pdvLoginEmail(", StringComparison.Ordinal) ? "email" : j.Contains("pdvLoginSenha(", StringComparison.Ordinal) ? "senha" : j.Contains("Escolher", StringComparison.Ordinal) ? "canal" : "codigo").ToList();
        checar(string.Join(",", ordem) == "email,senha,canal,codigo", "LG-10b a pagina recebe e-mail, senha, a escolha do e-mail e o codigo, nessa ordem (" + string.Join(",", ordem) + ")");
        checar(m.Js.Any(j => j.Contains("pdvLoginEmail(", StringComparison.Ordinal) && j.Contains("loja@americanday.com.br", StringComparison.Ordinal))
               && m.Js.Any(j => j.Contains("pdvLoginSenha(", StringComparison.Ordinal) && j.Contains("S3nh@!xyz", StringComparison.Ordinal) && !j.Contains("loja@", StringComparison.Ordinal)),
            "LG-10c o e-mail vai na tela do e-mail e so a senha na tela da senha");
        var tudo = string.Join("\n", m.Diag);
        checar(m.Diag.Any(d => d == "login do gestor: pedido") && m.Diag.Any(d => d.StartsWith("login do gestor: codigo pedido", StringComparison.Ordinal))
               && m.Diag.Any(d => d.StartsWith("login do gestor: codigo recebido", StringComparison.Ordinal)) && m.Diag.Any(d => d.StartsWith("login do gestor: entrou", StringComparison.Ordinal)),
            "LG-10d o diag conta pedido, codigo pedido, codigo recebido e entrou");
        checar(!tudo.Contains("loja@", StringComparison.Ordinal) && !tudo.Contains("S3nh@", StringComparison.Ordinal) && !tudo.Contains("482913", StringComparison.Ordinal), "LG-10e nenhum segredo no diag");
        checar(m.Esperado >= TimeSpan.FromSeconds(10) && m.Esperado < TimeSpan.FromMinutes(2), "LG-10g esperou os 5 s entre as consultas (" + m.Esperado + ")");
        checar(!m.Corpos.Any(c => c.Contains("S3nh@", StringComparison.Ordinal) || c.Contains("482913", StringComparison.Ordinal)), "LG-10h nenhum corpo mandado leva senha ou codigo");

        var m2 = new Mundo { CanalNaTela = false };
        checar(await m2.Fluxo().TentarAsync(false, CancellationToken.None) && !m2.Js.Any(j => j.Contains("pdvLoginEscolherEmail", StringComparison.Ordinal)), "LG-11 sem a tela do canal nao ha escolha, e entra do mesmo jeito");

        // as duas juntas (login.ftl): pdvLoginPreencher com e-mail e senha
        var mj = new Mundo { Tela = "login" };
        checar(await mj.Fluxo().TentarAsync(false, CancellationToken.None) && mj.Js.Any(j => j.Contains("pdvLoginPreencher(", StringComparison.Ordinal) && j.Contains("loja@", StringComparison.Ordinal) && j.Contains("S3nh@", StringComparison.Ordinal))
               && !mj.Js.Any(j => j.Contains("pdvLoginEmail(", StringComparison.Ordinal)),
            "LG-11a a tela com e-mail e senha juntos (login.ftl) e preenchida de uma vez");

        // ja na tela da senha (alguem digitou o e-mail): pede as credenciais e preenche so a senha
        var ms = new Mundo { Tela = "senha" };
        checar(await ms.Fluxo().TentarAsync(false, CancellationToken.None) && ms.Acoes.First() == "credenciais" && !ms.Js.Any(j => j.Contains("pdvLoginEmail(", StringComparison.Ordinal)) && ms.Js.Any(j => j.Contains("pdvLoginSenha(", StringComparison.Ordinal)),
            "LG-11b ja na tela da senha: credenciais e so a senha");

        var m3 = new Mundo { ConsultasAteOCodigo = int.MaxValue };
        var r3 = await m3.Fluxo().TentarAsync(false, CancellationToken.None);
        checar(!r3 && m3.Estado.FalhasSeguidas == 1 && m3.Estado.Ultimo == "falhou 10:15 (codigo nao chegou em 15 min)", "LG-12 codigo que nao chega em 15 min e falha (" + m3.Estado.Ultimo + ")");
        checar(m3.Acoes.Count(a => a == "codigo") is >= 170 and <= 182, "LG-12b consultou a cada 5 s por 15 min (" + m3.Acoes.Count(a => a == "codigo") + ")");

        var m4 = new Mundo();
        m4.Nuvem = corpo => JsonDocument.Parse(corpo).RootElement.GetProperty("acao").GetString() switch
        {
            "credenciais" => (200, "{\"ok\":true,\"email\":\"a@b.c\",\"senha\":\"x\"}"),
            "pedir_codigo" => (200, "{\"ok\":true,\"id\":8,\"aviso\":{\"enviado\":true}}"),
            "codigo" => (200, "{\"ok\":true,\"expirado\":true,\"id\":8}"),
            _ => (200, "{\"ok\":true}"),
        };
        checar(!await m4.Fluxo().TentarAsync(false, CancellationToken.None) && m4.Estado.Ultimo == "falhou 10:00 (codigo expirou no ERP)", "LG-13 expirado no ERP e falha");

        // senha errada: a tela da senha continua e a pagina diz por que
        var m5 = new Mundo { SenhaErrada = true, Mensagem = "E-mail ou nome do usuário ou senha inválidos." };
        checar(!await m5.Fluxo().TentarAsync(false, CancellationToken.None) && m5.Estado.Ultimo == "falhou 10:00 (senha recusada (a pagina diz: E-mail ou nome do usuário ou senha inválidos.))" && !m5.Acoes.Contains("pedir_codigo"),
            "LG-14 ainda na tela da senha depois de 30 s e falha com o que a pagina diz, sem pedir codigo (" + m5.Estado.Ultimo + ")");
        checar(m5.Esperado >= TimeSpan.FromSeconds(30) && m5.Esperado <= TimeSpan.FromSeconds(40), "LG-14a esperou os 30 s da tela (" + m5.Esperado + ")");
        var m5b = new Mundo { EmailErrado = true, Mensagem = "Conta nao encontrada para loja@x.com" };
        checar(!await m5b.Fluxo().TentarAsync(false, CancellationToken.None) && m5b.Estado.Ultimo!.StartsWith("falhou 10:00 (e-mail recusado (a pagina diz: Conta nao encontrada para email@####", StringComparison.Ordinal),
            "LG-14b e-mail recusado: falha com a mensagem mascarada (" + m5b.Estado.Ultimo + ")");

        var m6 = new Mundo { CodigoErrado = true, Mensagem = "Código inválido." };
        checar(!await m6.Fluxo().TentarAsync(false, CancellationToken.None) && m6.Estado.Ultimo == "falhou 10:00 (codigo recusado (a pagina diz: Código inválido.))", "LG-15 codigo recusado e falha (" + m6.Estado.Ultimo + ")");

        // tres falhas seguidas: pausa de 2 h, o usado leva pausado_ate e o motivo "3 falhas seguidas (...)"
        var m7 = new Mundo { SenhaErrada = true };
        await m7.Fluxo().TentarAsync(false, CancellationToken.None);
        m7.Agora = m7.Agora.AddMinutes(10); await m7.Fluxo().TentarAsync(false, CancellationToken.None);
        m7.Agora = m7.Agora.AddMinutes(10); await m7.Fluxo().TentarAsync(false, CancellationToken.None);
        checar(m7.Estado.FalhasSeguidas == 3 && m7.Estado.PausadoAte is not null, "LG-16 tres falhas seguidas pausam");
        var u7 = JsonDocument.Parse(m7.Corpos.Last()).RootElement;
        checar(u7.GetProperty("acao").GetString() == "usado" && u7.GetProperty("pausado_ate").ValueKind == JsonValueKind.String
               && u7.GetProperty("motivo").GetString() == "3 falhas seguidas (senha recusada)",
            "LG-16a o ultimo usado leva pausado_ate e o motivo pronto para o dono: '3 falhas seguidas (senha recusada)' (" + u7.GetProperty("motivo").GetString() + ")");
        checar(m7.Diag.Last().Contains("pausado por 2 h", StringComparison.Ordinal) && m7.Diag.Last().Contains("dono avisado", StringComparison.Ordinal), "LG-16b o diag diz da pausa e do aviso");
        m7.Agora = m7.Agora.AddMinutes(10);
        var antes = m7.Acoes.Count;
        checar(!await m7.Fluxo().TentarAsync(false, CancellationToken.None) && m7.Acoes.Count == antes && m7.Diag.Last().StartsWith("login do gestor: nao agora (pausado ate", StringComparison.Ordinal), "LG-16c pausado: a tentativa automatica nem chama a nuvem");
        m7.SenhaErrada = false;
        checar(await m7.Fluxo().TentarAsync(true, CancellationToken.None) && m7.Estado.FalhasSeguidas == 0 && m7.Estado.PausadoAte is null, "LG-16d Entrar agora passa pela pausa e, entrando, zera tudo");

        // 1.0.50: CAPTCHA depois da senha: desiste na hora, pausa 2 h, o usado leva pausado_ate e o motivo "captcha na tela de login"
        var mc = new Mundo { CaptchaDepoisDaSenha = true };
        checar(!await mc.Fluxo().TentarAsync(false, CancellationToken.None) && mc.Estado.PausadoAte == mc.Agora.AddHours(2) && mc.Estado.FalhasSeguidas == 1
               && !mc.Acoes.Contains("pedir_codigo") && mc.Acoes.Last() == "usado",
            "LG-16e captcha depois da senha: para na hora, pausa 2 h, sem pedir codigo (" + mc.Estado.Ultimo + ")");
        var uc = JsonDocument.Parse(mc.Corpos.Last()).RootElement;
        checar(uc.GetProperty("motivo").GetString() == "captcha na tela de login" && uc.GetProperty("pausado_ate").ValueKind == JsonValueKind.String,
            "LG-16f o usado do captcha leva o motivo como e (sem '3 falhas') e pausado_ate: o ERP avisa o dono (" + uc.GetProperty("motivo").GetString() + ")");
        checar(mc.Diag.Last() == "login do gestor: falhou (captcha na tela de login), automatico pausado por 2 h, dono avisado", "LG-16g o diag diz do captcha e do aviso (" + mc.Diag.Last() + ")");
        // captcha logo na primeira tela: nem pede as credenciais
        var mc2 = new Mundo { Tela = "captcha" };
        checar(!await mc2.Fluxo().TentarAsync(false, CancellationToken.None) && !mc2.Acoes.Contains("credenciais") && mc2.Acoes.SequenceEqual(new[] { "usado" }) && mc2.Estado.PausadoAte is not null,
            "LG-16h captcha logo de cara: nem pede credenciais; so o usado com a pausa (" + string.Join(",", mc2.Acoes) + ")");
        // o cadastro da verificacao em 2 etapas (enforce-mfa) tambem para
        var mm = new Mundo { MfaDepoisDaSenha = true };
        checar(!await mm.Fluxo().TentarAsync(false, CancellationToken.None) && mm.Estado.PausadoAte is not null
               && JsonDocument.Parse(mm.Corpos.Last()).RootElement.GetProperty("motivo").GetString() == "o iFood pede o cadastro da verificacao em 2 etapas",
            "LG-16i o iFood pedindo o cadastro da verificacao: para, pausa e o dono e avisado com esse motivo");

        // o teto de 10 min entre tentativas automaticas
        var m8 = new Mundo { SenhaErrada = true };
        await m8.Fluxo().TentarAsync(false, CancellationToken.None);
        m8.Agora = m8.Agora.AddMinutes(2);
        var antes8 = m8.Acoes.Count;
        checar(!await m8.Fluxo().TentarAsync(false, CancellationToken.None) && m8.Acoes.Count == antes8 && m8.Diag.Last() == "login do gestor: nao agora (ja tentei ha 2 min (1 a cada 10 min))",
            "LG-17 2 min depois de uma tentativa, a automatica espera");

        var m9 = new Mundo();
        m9.Nuvem = _ => (200, "{\"ok\":false,\"motivo\":\"desligado\"}");
        checar(!await m9.Fluxo().TentarAsync(false, CancellationToken.None) && m9.Estado.DesligadoNoErp && m9.Estado.FalhasSeguidas == 0 && m9.Acoes.Count == 1 && m9.Diag.Last() == "login do gestor: desligado no ERP",
            "LG-18 desligado no ERP: uma chamada, sem falha");
        var m9b = new Mundo();
        m9b.Nuvem = _ => (200, "{\"ok\":false,\"motivo\":\"sem_cadastro\"}");
        checar(!await m9b.Fluxo().TentarAsync(false, CancellationToken.None) && m9b.Estado.FalhasSeguidas == 0 && m9b.Acoes.Count == 1 && m9b.Estado.Ultimo == "sem cadastro no ERP 10:00",
            "LG-18a sem cadastro no ERP: sem falha, sem usado, registro na linha");
        var m9c = new Mundo();
        m9c.Nuvem = _ => (404, "{\"ok\":false,\"motivo\":\"sem_sql_193\"}");
        checar(!await m9c.Fluxo().TentarAsync(false, CancellationToken.None) && m9c.Estado.FalhasSeguidas == 0 && m9c.Diag.Last() == "login do gestor: sem login automatico (SQL 193 nao rodou)",
            "LG-18b 404 sem_sql_193: sem login automatico, sem falha");
        // 1.0.50 (SQL 222): o outro aparelho esta entrando: nao e falha, nem usado; tenta de novo no proximo teto
        var m9o = new Mundo();
        m9o.Nuvem = _ => (200, "{\"ok\":false,\"motivo\":\"outro_aparelho_entrando\",\"terminal\":\"TV DESKTOP\"}");
        checar(!await m9o.Fluxo().TentarAsync(false, CancellationToken.None) && m9o.Estado.FalhasSeguidas == 0 && m9o.Acoes.SequenceEqual(new[] { "credenciais" })
               && m9o.Estado.Ultimo == "outro aparelho esta entrando 10:00" && m9o.Diag.Last() == "login do gestor: outro aparelho esta entrando",
            "LG-18b2 outro aparelho entrando: so a chamada de credenciais, sem falha, sem usado (" + m9o.Diag.Last() + ")");
        checar(m9o.Estado.PorQueNao(m9o.Agora.AddMinutes(3), false) is not null && m9o.Estado.PorQueNao(m9o.Agora.AddMinutes(10), false) is null, "LG-18b3 e respeita o teto: tenta de novo em 10 min");

        var m9d = new Mundo();
        var padrao9d = m9d.Nuvem;
        m9d.Nuvem = corpo => JsonDocument.Parse(corpo).RootElement.GetProperty("acao").GetString() == "pedir_codigo"
            ? (200, "{\"ok\":true,\"id\":9,\"ja_aberto\":false,\"aviso\":{\"enviado\":false,\"motivo\":\"sem telefone do dono\"}}")
            : padrao9d(corpo);
        checar(!await m9d.Fluxo().TentarAsync(false, CancellationToken.None) && m9d.Estado.FalhasSeguidas == 0 && !m9d.Acoes.Contains("codigo") && m9d.Acoes.Last() == "usado"
               && m9d.Diag.Any(d => d == "login do gestor: dono nao avisado (sem telefone do dono)"),
            "LG-18c dono nao avisado: desiste na hora, sem falha, usado com o motivo");
        var m9e = new Mundo();
        var padrao9e = m9e.Nuvem;
        m9e.Nuvem = corpo => JsonDocument.Parse(corpo).RootElement.GetProperty("acao").GetString() == "pedir_codigo"
            ? (200, "{\"ok\":true,\"id\":7,\"ja_aberto\":true,\"aviso\":{\"enviado\":true}}") : padrao9e(corpo);
        checar(await m9e.Fluxo().TentarAsync(false, CancellationToken.None) && m9e.Diag.Any(d => d == "login do gestor: codigo pedido ao dono (pedido ja aberto)"), "LG-18e pedido ja aberto: segue esperando o mesmo id");
        var m9f = new Mundo();
        var padrao9f = m9f.Nuvem;
        m9f.Nuvem = corpo => JsonDocument.Parse(corpo).RootElement.GetProperty("acao").GetString() == "codigo" ? (200, "{\"ok\":false,\"motivo\":\"sem_pedido\"}") : padrao9f(corpo);
        checar(!await m9f.Fluxo().TentarAsync(false, CancellationToken.None) && m9f.Estado.Ultimo == "falhou 10:00 (sem_pedido)", "LG-18f consulta recusada (sem_pedido) e falha");

        var m10 = new Mundo();
        m10.Nuvem = _ => (-1, "");
        checar(!await m10.Fluxo().TentarAsync(false, CancellationToken.None) && m10.Estado.FalhasSeguidas == 0 && m10.Acoes.Count == 1 && m10.Diag.Last() == "login do gestor: sem rede", "LG-19 sem rede: nao conta falha e nao manda usado");

        var m11 = new Mundo { Tela = "logado" };
        checar(!await m11.Fluxo().TentarAsync(false, CancellationToken.None) && m11.Acoes.Count == 0 && m11.Estado.UltimaTentativa is null && m11.Diag.Last() == "login do gestor: o Gestor ja esta logado", "LG-20 ja logado: nenhuma chamada");
        var m11b = new Mundo { Tela = "sem_script" };
        checar(!await m11b.Fluxo().TentarAsync(false, CancellationToken.None) && m11b.Acoes.Count == 0 && m11b.Diag.Last() == "login do gestor: nao agora (a pagina diz sem_script)", "LG-20a sem o script na pagina nao ha tentativa");
        var m11c = new Mundo { Tela = "carregando" };
        checar(!await m11c.Fluxo().TentarAsync(false, CancellationToken.None) && m11c.Acoes.Count == 0 && m11c.Diag.Last() == "login do gestor: nao agora (a pagina diz carregando)", "LG-20b a pagina carregando espera a proxima leitura");

        var m12 = new Mundo { AlguemEntrouNaEspera = true };
        checar(await m12.Fluxo().TentarAsync(false, CancellationToken.None) && m12.Estado.Fase == FaseLogin.Entrou && m12.Diag.Any(d => d.Contains("entrou (alguem entrou na mao)", StringComparison.Ordinal)), "LG-21 alguem logou na mao durante a espera: entrou");

        var m13 = new Mundo { ResultadoEmail = "sem_campo" };
        checar(!await m13.Fluxo().TentarAsync(false, CancellationToken.None) && m13.Estado.Ultimo == "falhou 10:00 (a pagina diz sem_campo)" && !m13.Acoes.Contains("pedir_codigo"), "LG-22 a pagina sem o campo e falha");

        var m14 = new Mundo();
        var tcs = new TaskCompletionSource<(int, string)>();
        var f14 = new FluxoLoginGestor(m14.Estado, "CAIXA-01", _ => tcs.Task, js => Task.FromResult("\"email\""), m14.Diag.Add, () => m14.Agora, (_, _) => Task.CompletedTask);
        var primeira = f14.TentarAsync(false, CancellationToken.None);
        var segunda = await f14.TentarAsync(false, CancellationToken.None);
        checar(!segunda && f14.Rodando && m14.Diag.Last() == "login do gestor: nao agora (ja esta entrando)", "LG-23 sem reentrada");
        tcs.SetResult((-1, ""));
        await primeira;
        checar(!f14.Rodando, "LG-23a terminou e soltou");

        var m15 = new Mundo { ConsultasAteOCodigo = int.MaxValue, CanalNaTela = false };
        using var cts = new CancellationTokenSource();
        var n = 0;
        var padrao15 = m15.Nuvem;
        m15.Nuvem = corpo => { if (++n == 3) cts.Cancel(); return padrao15(corpo); };
        var r15 = await m15.Fluxo().TentarAsync(false, cts.Token);
        checar(!r15 && m15.Estado.FalhasSeguidas == 0 && m15.Estado.Fase == FaseLogin.Ocioso && m15.Diag.Last() == "login do gestor: cancelado" && !m15.Acoes.Contains("usado"), "LG-24 cancelado no meio: ocioso, sem falha, sem usado");

        var m16 = new Mundo { Tela = "codigo" };
        checar(await m16.Fluxo().TentarAsync(false, CancellationToken.None) && !m16.Acoes.Contains("credenciais") && m16.Acoes.First() == "pedir_codigo", "LG-25 ja na tela do codigo: pede o codigo direto, sem credenciais");

        // 1.0.50: a pagina expirou: Voltar e segue pela tela do e-mail
        var m17 = new Mundo { Tela = "expirada" };
        checar(await m17.Fluxo().TentarAsync(false, CancellationToken.None) && m17.Voltas == 1 && m17.Diag.Any(d => d == "login do gestor: a pagina diz expirada: toquei em Voltar") && m17.Acoes.First() == "credenciais",
            "LG-26 a pagina expirada: toca em Voltar e entra pela tela do e-mail (" + string.Join("|", m17.Diag) + ")");
        // depois do codigo o Gestor ainda carregando: espera e entra
        var m18 = new Mundo { CarregandoDepoisDoCodigo = true };
        checar(await m18.Fluxo().TentarAsync(false, CancellationToken.None) && m18.Estado.Fase == FaseLogin.Entrou, "LG-27 depois do codigo a pagina carregando e esperada ate o Gestor montar (" + string.Join("|", m18.Diag) + ")");

        // o ponto de entrada do Vigia
        LoginDoGestor.ComandoEntrarAgora = null;
        checar(await LoginDoGestor.EntrarAgora() == "o chat do iFood ainda nao abriu", "LG-28 LoginDoGestor.EntrarAgora sem a tela ligada diz que o chat nao abriu");
        LoginDoGestor.ComandoEntrarAgora = () => Task.FromResult("entrou 10:00");
        checar(await LoginDoGestor.EntrarAgora() == "entrou 10:00", "LG-28a com a tela ligada, EntrarAgora chama o Entrar agora do servico (o relogar_gestor do Vigia entra por aqui)");
        LoginDoGestor.ComandoEntrarAgora = () => throw new InvalidOperationException("x");
        checar((await LoginDoGestor.EntrarAgora()).StartsWith("nao deu agora", StringComparison.Ordinal), "LG-28b EntrarAgora nunca lanca");
        LoginDoGestor.ComandoEntrarAgora = null;
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
        checar(tela.Contains("ServicoLoginGestor.Ligar(") && tela.Contains("LoginDoGestor.Edge") && tela.Contains("LoginDoGestor.Prazo"), "LG-30b a tela liga o servico com a nuvem (edge gestor-login, com prazo) e a pagina");
        checar(servico.Contains("public static void Observar(bool pedeLogin)") && servico.Contains("Task<string> EntrarAgoraAsync()")
               && servico.Contains("LoginDoGestor.Mascarar(") && servico.Contains("() => RelogioCorrigido.AgoraCorrigido")
               && servico.Contains("LoginDoGestor.ComandoEntrarAgora = EntrarAgoraAsync;"),
            "LG-30c o servico observa, tem o Entrar agora, mascara, usa a hora corrigida e pendura o ponto de entrada do Vigia");
        checar(confXaml.Contains("x:Name=\"BlocoLoginGestor\"") && confXaml.Contains("x:Name=\"BtnEntrarAgora\"") && confXaml.Contains("Text=\"Entrar agora\"") && confXaml.Contains("Click=\"EntrarAgora\""),
            "LG-30d a Configuracao tem a linha do login do Gestor e o botao Entrar agora");
        checar(confCs.Contains("private async void EntrarAgora(object sender, RoutedEventArgs e)") && confCs.Contains("ServicoLoginGestor.EntrarAgoraAsync()"), "LG-30e o botao chama o servico");
        // 1.0.50: a deteccao do login na tela real (Keycloak): kcContext e o host partners-auth no naolidas e no diag;
        // no pdvsbdiag so o kcContext (o script do envio nunca toca em location: FN-7 e o harness do Sendbird)
        checar(Regex.Matches(tela, @"!!window\.kcContext \|\| /partners-auth\\\.ifood/i\.test\(String\(location\.host \|\| ''\)\)").Count >= 2
               && tela.Contains("var r = { tipo: 'pdvsbdiag', id: id, login: !!document.querySelector('input[type=\"password\"]') || !!window.kcContext };", StringComparison.Ordinal),
            "LG-30f a leitura do login (naolidas, diag) reconhece o Keycloak do iFood (kcContext ou o host partners-auth); o pdvsbdiag so pelo kcContext");
        checar(Ler("Pdv.csproj").Contains("<Version>1.0.55</Version>", StringComparison.Ordinal), "LG-31 a versao do caixa e 1.0.55");
        var novos = new[] { servico, nucleo, testes };
        checar(novos.All(t => t.Length > 0 && !t.Contains((char)0x2014) && !t.Contains((char)0x2013)), "LG-32 os arquivos novos nao tem travessao");
        var script = TestesScriptSendbird.ExtrairRaw(tela, "ScriptLogin") ?? "";
        checar(script.Length > 2000 && !script.Contains((char)0x2014) && !script.Contains((char)0x2013), "LG-33 o ScriptLogin existe e nao tem travessao");
        checar(!Regex.IsMatch(script, @"querySelector(All)?\(\s*['""]\.") && !script.Contains("[class", StringComparison.Ordinal) && !script.Contains("className", StringComparison.Ordinal)
               && !script.Contains("getElementsByClassName", StringComparison.Ordinal),
            "LG-33a o script nunca procura por classe (so por id, name, atributo e texto)");
        checar(script.Contains("kcContext") && script.Contains("input#username") && script.Contains("otpChannel") && script.Contains("data-is-otp-input") && script.Contains("login-actions/restart"),
            "LG-33b o script conhece a tela real: kcContext.pageId, #username, os radios otpChannel, o OTPInput e o Voltar");
        checar(Ler("Pdv.Testes/Program.cs").Contains("TestesLoginGestor.RodarAsync("), "LG-34 a suite esta registrada no gate");
        checar(File.Exists(Path.Combine(Raiz(), "Pdv.Testes", "script_login.test.cjs")), "LG-35 o harness do DOM falso esta em Pdv.Testes/script_login.test.cjs (o mesmo da TV)");
    }

    // ── 6. o script no node contra o DOM falso (as telas do Keycloak do iFood) ──
    private static void Script(Action<bool, string> checar)
    {
        var tela = Ler("Telas/ChatIfood.xaml.cs");
        var script = TestesScriptSendbird.ExtrairRaw(tela, "ScriptLogin");
        if (script is null) { checar(false, "LG-40 o ScriptLogin sai da constante de Telas/ChatIfood.xaml.cs"); return; }
        var harness = Path.Combine(Raiz(), "Pdv.Testes", "script_login.test.cjs");
        var pasta = Path.Combine(Path.GetTempPath(), "pdv-lg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(pasta);
        try
        {
            var arqScript = Path.Combine(pasta, "script-login.js");
            File.WriteAllText(arqScript, script, new UTF8Encoding(false));
            var check = TestesScriptSendbird.Node(pasta, "--check", arqScript);
            checar(check.Exit == 0, $"LG-40 o script injetado passa no node --check ({check.Erro.Trim()})");
            var r = TestesScriptSendbird.Node(pasta, harness, arqScript);
            List<ItemHarness>? itens = null;
            try
            {
                var ultima = r.Saida.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("[", StringComparison.Ordinal));
                if (ultima is not null) itens = JsonSerializer.Deserialize<List<ItemHarness>>(ultima);
            }
            catch { itens = null; }
            checar(r.Exit == 0 && itens is { Count: >= 50 }, $"LG-41 o node rodou o script contra as telas falsas do Keycloak (saida {r.Exit}, {itens?.Count ?? 0} conferencias) {Corta(r.Erro)}");
            foreach (var i in itens ?? new List<ItemHarness>()) checar(i.ok, "LG-" + i.nome);
        }
        finally
        {
            try { Directory.Delete(pasta, true); } catch { }
        }
    }

    private sealed record ItemHarness(bool ok, string nome);
    private static string Corta(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim();

    private static string Raiz()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Pdv.csproj"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return AppContext.BaseDirectory;
    }

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
}
