using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O LADO DO APARELHO DO VIGIA (caixa 1.0.50, 25/10/2026, SQL 220; docs/VIGIA_CONTRATO.md do ERP). As regras puras
/// de <see cref="VigiaDoAparelho"/>: a leitura da tela do chat (casca, login, cortina, conversa) virando tela_chat;
/// o sinal (as chaves de sempre do sinal da raspadinha mais as novas, o teto de 8 KB, o print de ate 1 MB); a
/// regra do print (toda vez fora do ok, a cada 10 min no ok, nunca no pagamento); os erros do console sem segredo;
/// a resposta do sinal (comandos pendentes, pausado, 404 sem_sql_220); a decisao de cada comando (venda, pinpad,
/// impressao, nao suportado, expirado); o concluir (recusa com "recusado: ", quem mata o programa conclui antes).
/// E os fontes: a tela e o servico ligados como o contrato pede.
/// </summary>
public static class TestesVigiaDoAparelho
{
    public static Task RodarAsync(Action<bool, string> checar)
    {
        Tela(checar);
        Sinal(checar);
        Resposta(checar);
        Comandos(checar);
        Fontes(checar);
        return Task.CompletedTask;
    }

    private static void Tela(Action<bool, string> checar)
    {
        var l = VigiaDoAparelho.LerTela("\"{\\\"casca\\\":false,\\\"login\\\":true,\\\"cortina\\\":false,\\\"conversa\\\":false}\"");
        checar(l.Leu && l.Login && !l.Casca, "VG-1 a leitura da pagina vem como JSON com aspas por fora e e lida");
        checar(!VigiaDoAparelho.LerTela("lixo").Leu && !VigiaDoAparelho.LerTela(null).Leu && !VigiaDoAparelho.LerTela("\"{}\"").Leu, "VG-1a lixo, nulo e {} = nao leu");
        var ok = new VigiaDoAparelho.LeituraDaTela(false, false, false, true, true);
        checar(VigiaDoAparelho.TelaChat(true, ok, false) == "ok", "VG-2 conversa na tela = ok");
        checar(VigiaDoAparelho.TelaChat(true, new(false, false, false, false, true), true) == "ok", "VG-2a sem a gaveta mas com o socket vivo = ok");
        checar(VigiaDoAparelho.TelaChat(true, new(false, false, false, false, true), false) == "outro", "VG-2b sem conversa e sem socket = outro");
        checar(VigiaDoAparelho.TelaChat(true, new(true, false, false, false, true), true) == "casca", "VG-2c o #root vazio = casca (mesmo com socket)");
        checar(VigiaDoAparelho.TelaChat(true, new(true, true, false, false, true), true) == "login", "VG-2d o login ganha da casca (a pagina do Keycloak nao tem #root)");
        checar(VigiaDoAparelho.TelaChat(true, new(false, false, true, true, true), true) == "cortina", "VG-2e a nossa cortina na tela = cortina");
        checar(VigiaDoAparelho.TelaChat(false, ok, true) == "outro" && VigiaDoAparelho.TelaChat(true, new(false, false, false, false, false), true) == "outro", "VG-2f sem navegador ou sem leitura = outro");
        checar(VigiaDoAparelho.JsLeituraDaTela.Contains("getElementById('root')") && VigiaDoAparelho.JsLeituraDaTela.Contains("kcContext") && VigiaDoAparelho.JsLeituraDaTela.Contains("pdv-cortina")
               && !Regex.IsMatch(VigiaDoAparelho.JsLeituraDaTela, @"querySelector(All)?\(\s*'\."), "VG-3 o script da leitura so le: #root, kcContext/partners-auth, #pdv-cortina, a gaveta; nunca por classe");

        var t = new DateTime(2026, 10, 25, 10, 0, 0);
        checar(VigiaDoAparelho.QuerPrint("casca", t.AddSeconds(-30), t, false) && VigiaDoAparelho.QuerPrint("login", t, t, false), "VG-4 fora do ok: print a cada sinal");
        checar(VigiaDoAparelho.QuerPrint("ok", null, t, false) && !VigiaDoAparelho.QuerPrint("ok", t.AddMinutes(-9), t, false) && VigiaDoAparelho.QuerPrint("ok", t.AddMinutes(-10), t, false), "VG-4a no ok: a cada 10 min");
        checar(!VigiaDoAparelho.QuerPrint("casca", null, t, true), "VG-4b nunca na tela de pagamento");
    }

    private static void Sinal(Action<bool, string> checar)
    {
        var baseJson = "{\"gestor\":\"logado\",\"ws_vivo\":true,\"ultimo_quadro_em\":\"2026-10-25T13:00:00Z\",\"fila\":0,\"quadros\":{\"MESG\":3},\"sdk\":{\"bundle\":\"x\"}}";
        var erros = VigiaDoAparelho.AnotarErro(Array.Empty<string>(), "Failed to load https://api.x/?token=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnop 401");
        erros = VigiaDoAparelho.AnotarErro(erros, "Failed to load https://api.x/?token=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnop 401");
        erros = VigiaDoAparelho.AnotarErro(erros, "   ");
        erros = VigiaDoAparelho.AnotarErro(erros, "Authorization: Bearer abcdefghij.klmnop x-chat-aparelho: ABCDEFGH23456789 chave 0123456789abcdef0123456789abcdef dono@x.com");
        checar(erros.Count == 2 && !erros[0].Contains("eyJ", StringComparison.Ordinal) && erros[0].Contains("token=<segredo>", StringComparison.Ordinal),
            "VG-5 os erros do console: repetido seguido e vazio nao entram; o JWT e o token= somem (" + erros[0] + ")");
        checar(!erros[1].Contains("abcdefghij", StringComparison.Ordinal) && !erros[1].Contains("ABCDEFGH23456789", StringComparison.Ordinal) && !erros[1].Contains("0123456789abcdef0123456789abcdef", StringComparison.Ordinal)
               && !erros[1].Contains("dono@", StringComparison.Ordinal),
            "VG-5a Bearer, x-chat-aparelho, hex longo e e-mail somem (" + erros[1] + ")");
        var muitos = (IReadOnlyList<string>)Array.Empty<string>();
        for (var i = 0; i < 30; i++) muitos = VigiaDoAparelho.AnotarErro(muitos, "erro " + i);
        checar(muitos.Count == 20 && muitos[0] == "erro 10" && muitos[^1] == "erro 29", "VG-5b guarda os 20 mais novos");
        checar(VigiaDoAparelho.MascararErro(new string('x', 500)).Length == 300, "VG-5c uma linha de erro tem ate 300 letras");

        var l = new VigiaDoAparelho.Leitura("1.0.50", "a0420449-0000-0000-0000-000000000000", "casca", new DateTime(2026, 10, 25, 12, 58, 0, DateTimeKind.Utc), false, new DateTime(2026, 10, 25, 13, 0, 0, DateTimeKind.Utc),
            2, 37.46, 512.5, 3600, erros, true, VigiaDoAparelho.ComandosDoCaixa, baseJson, Convert.ToBase64String(new byte[300]));
        var corpo = JsonDocument.Parse(VigiaDoAparelho.CorpoSinal(l)).RootElement;
        checar(corpo.GetProperty("acao").GetString() == "sinal" && corpo.GetProperty("terminal_uuid").GetString()!.StartsWith("a0420449", StringComparison.Ordinal) && corpo.GetProperty("versao").GetString() == "1.0.50",
            "VG-6 o corpo do sinal: acao, terminal_uuid (so o caixa), versao");
        var e = corpo.GetProperty("estado");
        checar(e.GetProperty("gestor").GetString() == "logado" && e.GetProperty("quadros").GetProperty("MESG").GetInt32() == 3 && e.GetProperty("sdk").GetProperty("bundle").GetString() == "x",
            "VG-6a as chaves de sempre do sinal da raspadinha continuam no estado");
        checar(e.GetProperty("tela_chat").GetString() == "casca" && e.GetProperty("casca_desde").GetString() == "2026-10-25T12:58:00Z" && e.GetProperty("ws_vivo").ValueKind == JsonValueKind.False
               && e.GetProperty("ultimo_quadro_em").GetString() == "2026-10-25T13:00:00Z" && e.GetProperty("recargas_hora").GetInt32() == 2,
            "VG-6b as chaves novas: tela_chat, casca_desde (ISO, Z), ws_vivo (false explicito), ultimo_quadro_em, recargas_hora");
        checar(e.GetProperty("cpu_pct").GetDouble() == 37.5 && e.GetProperty("mem_mb").GetDouble() == 512.5 && e.GetProperty("uptime_s").GetInt64() == 3600 && e.GetProperty("venda_em_andamento").ValueKind == JsonValueKind.True,
            "VG-6c cpu_pct (1 casa), mem_mb, uptime_s, venda_em_andamento");
        var sup = e.GetProperty("suporta").EnumerateArray().Select(x => x.GetString()).ToArray();
        checar(sup.SequenceEqual(VigiaDoAparelho.ComandosDoCaixa) && sup.Contains("relogar_gestor") && sup.Length == 9, "VG-6d suporta leva os 9 comandos do caixa, relogar_gestor inclusive (" + string.Join(",", sup) + ")");
        checar(e.GetProperty("erros_console").GetArrayLength() == 2 && !e.GetProperty("erros_console")[0].GetString()!.Contains("eyJ", StringComparison.Ordinal), "VG-6e erros_console vai mascarado");
        checar(corpo.GetProperty("print_b64").GetString()!.Length == 400, "VG-6f o print entra em base64");
        var semPrint = new VigiaDoAparelho.Leitura("1.0.50", null, "ok", null, true, null, 0, null, null, 10, Array.Empty<string>(), null, VigiaDoAparelho.ComandosDaTv, "lixo{", null);
        var c2 = JsonDocument.Parse(VigiaDoAparelho.CorpoSinal(semPrint)).RootElement;
        checar(!c2.TryGetProperty("terminal_uuid", out _) && !c2.TryGetProperty("print_b64", out _) && c2.GetProperty("estado").GetProperty("casca_desde").ValueKind == JsonValueKind.Null
               && !c2.GetProperty("estado").TryGetProperty("cpu_pct", out _) && !c2.GetProperty("estado").TryGetProperty("venda_em_andamento", out _),
            "VG-6g a TV: sem terminal_uuid, sem print, casca_desde nulo no ok, sem cpu quando nao mediu, sem venda; estado base torto vira so as chaves novas");
        var grande = Convert.ToBase64String(new byte[VigiaDoAparelho.PrintMaximoBytes + 1]);
        checar(!VigiaDoAparelho.PrintCabe(grande) && VigiaDoAparelho.PrintCabe(Convert.ToBase64String(new byte[VigiaDoAparelho.PrintMaximoBytes])) && VigiaDoAparelho.PrintCabe("data:image/jpeg;base64," + Convert.ToBase64String(new byte[10])),
            "VG-6h o print de mais de 1 MB nao vai; o prefixo data: e aceito");
        var pesado = "{\"quadros\":{\"a\":\"" + new string('q', 9000) + "\"},\"sdk\":{}}";
        var c3 = JsonDocument.Parse(VigiaDoAparelho.CorpoSinal(semPrint with { EstadoBaseJson = pesado })).RootElement.GetProperty("estado");
        checar(!c3.TryGetProperty("quadros", out _) && c3.GetProperty("tela_chat").GetString() == "ok" && System.Text.Encoding.UTF8.GetByteCount(c3.GetRawText()) <= VigiaDoAparelho.EstadoMaximoBytes,
            "VG-6i o estado passa de 8 KB: quadros sai primeiro e o resto fica");
        checar(VigiaDoAparelho.Estado(semPrint with { TelaChat = "qualquer" })["tela_chat"]!.GetValue<string>() == "outro", "VG-6j tela_chat fora da lista vira outro");
    }

    private static void Resposta(Action<bool, string> checar)
    {
        var r = VigiaDoAparelho.LerSinal(200, "{\"ok\":true,\"aparelho\":\"caixa:a04\",\"loja\":\"American Day Savassi\",\"pausado\":false,\"comandos\":[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"comando\":\"recarga_forte\",\"args\":{\"motivo\":\"casca ha 4 min\"},\"criado_por\":\"vigia\",\"expira_em\":\"2026-10-25T19:10:00Z\"},{\"id\":\"2\",\"comando\":\"relogar_gestor\",\"args\":{}}],\"proximo_sinal_s\":60}");
        checar(r.Ok && !r.Pausado && r.Comandos.Count == 2 && r.Comandos[0].Nome == "recarga_forte" && r.Comandos[0].Motivo == "casca ha 4 min" && r.Comandos[0].ExpiraEm == new DateTime(2026, 10, 25, 19, 10, 0, DateTimeKind.Utc)
               && r.Comandos[1].Nome == "relogar_gestor" && r.Comandos[1].Motivo is null && r.ProximoSinalS == 60,
            "VG-7 a resposta do sinal: os comandos na ordem, com motivo e expira_em");
        checar(VigiaDoAparelho.LerSinal(200, "{\"ok\":true,\"pausado\":true,\"comandos\":[],\"proximo_sinal_s\":5}").Pausado && VigiaDoAparelho.LerSinal(200, "{\"ok\":true,\"comandos\":[],\"proximo_sinal_s\":5}").ProximoSinalS == 30,
            "VG-7a pausado e lido; proximo_sinal_s fica entre 30 s e 10 min");
        var sem = VigiaDoAparelho.LerSinal(404, "{\"ok\":false,\"motivo\":\"sem_sql_220\"}");
        checar(!sem.Ok && sem.SemSql220 && sem.Motivo == "sem_sql_220" && sem.Comandos.Count == 0, "VG-7b 404 = banco sem o 220: segue mandando, sem comando");
        checar(VigiaDoAparelho.LerSinal(401, "{\"ok\":false,\"motivo\":\"nao_autorizado\"}").Motivo == "nao_autorizado" && VigiaDoAparelho.LerSinal(0, "").Motivo == "sem rede" && VigiaDoAparelho.LerSinal(503, "{\"ok\":false,\"motivo\":\"erro_no_servidor\"}").Motivo == "erro_no_servidor",
            "VG-7c 401, sem rede e 503 viram motivo");
        checar(VigiaDoAparelho.LerSinal(200, "lixo").Motivo == "resposta invalida" && VigiaDoAparelho.LerSinal(200, "{\"ok\":true,\"comandos\":[{\"id\":\"x\",\"comando\":\"rm -rf /\"}]}").Comandos.Count == 0,
            "VG-7d lixo e comando com nome fora do padrao (shell) nao passam");
        checar(VigiaDoAparelho.ProximoIntervalo(r) == TimeSpan.FromSeconds(60) && VigiaDoAparelho.ProximoIntervalo(sem) == TimeSpan.FromSeconds(60) && VigiaDoAparelho.ProximoIntervalo(VigiaDoAparelho.LerSinal(200, "{\"ok\":true,\"comandos\":[],\"proximo_sinal_s\":120}")) == TimeSpan.FromSeconds(120),
            "VG-7e o proximo intervalo segue a edge (ou 60 s)");
        checar(VigiaDoAparelho.LerConcluir(200, "{\"ok\":true,\"id\":\"x\"}").Ok && VigiaDoAparelho.LerConcluir(200, "{\"ok\":true,\"id\":\"x\",\"repetido\":true}").Ok
               && VigiaDoAparelho.LerConcluir(400, "{\"ok\":false,\"motivo\":\"comando_desconhecido\"}").Motivo == "comando_desconhecido",
            "VG-7f o concluir: ok, repetido, comando_desconhecido");
    }

    private static void Comandos(Action<bool, string> checar)
    {
        var livre = new VigiaDoAparelho.Contexto(false, false, false, VigiaDoAparelho.ComandosDoCaixa);
        foreach (var c in VigiaDoAparelho.ComandosDoCaixa)
            checar(VigiaDoAparelho.Decidir(c, livre).Decisao == VigiaDoAparelho.Decisao.Executar, "VG-8 com o caixa livre, " + c + " executa");
        var venda = livre with { VendaEmAndamento = true };
        checar(VigiaDoAparelho.Decidir(VigiaDoAparelho.ReiniciarApp, venda) == (VigiaDoAparelho.Decisao.Recusar, "venda em andamento")
               && VigiaDoAparelho.Decidir(VigiaDoAparelho.ReiniciarPc, venda).Decisao == VigiaDoAparelho.Decisao.Recusar
               && VigiaDoAparelho.Decidir(VigiaDoAparelho.Atualizar, venda).Decisao == VigiaDoAparelho.Decisao.Recusar,
            "VG-8a venda em andamento recusa reiniciar_app, reiniciar_pc e atualizar");
        checar(VigiaDoAparelho.Decidir(VigiaDoAparelho.RecargaForte, venda).Decisao == VigiaDoAparelho.Decisao.Executar && VigiaDoAparelho.Decidir(VigiaDoAparelho.RelogarGestor, venda).Decisao == VigiaDoAparelho.Decisao.Executar,
            "VG-8b com venda, recarregar e relogar seguem (nao mexem na venda)");
        var pinpad = livre with { PinpadOcupado = true };
        checar(VigiaDoAparelho.Decidir(VigiaDoAparelho.ReiniciarPc, pinpad) == (VigiaDoAparelho.Decisao.Recusar, "pinpad ocupado") && VigiaDoAparelho.Decidir(VigiaDoAparelho.ReiniciarApp, pinpad).Decisao == VigiaDoAparelho.Decisao.Executar,
            "VG-8c pinpad ocupado recusa reiniciar_pc (e atualizar); reiniciar_app passa");
        var papel = livre with { ImpressaoNaFila = true };
        checar(VigiaDoAparelho.Decidir(VigiaDoAparelho.ReiniciarPc, papel) == (VigiaDoAparelho.Decisao.Recusar, "impressao na fila"), "VG-8d impressao na fila recusa reiniciar_pc");
        checar(VigiaDoAparelho.Decidir("pausar_vigia", livre).Decisao == VigiaDoAparelho.Decisao.Desconhecido && VigiaDoAparelho.Decidir("formatar", livre).Decisao == VigiaDoAparelho.Decisao.Desconhecido,
            "VG-8e comando fora de suporta (pausar_vigia nao vai ao aparelho; shell nunca) = desconhecido");
        checar(VigiaDoAparelho.Decidir(VigiaDoAparelho.Recarregar, livre with { Expirado = true }) == (VigiaDoAparelho.Decisao.Recusar, "expirou antes de executar"), "VG-8f comando vencido nem roda");
        checar(VigiaDoAparelho.MataOPrograma(VigiaDoAparelho.ReiniciarApp) && VigiaDoAparelho.MataOPrograma(VigiaDoAparelho.ReiniciarPc) && !VigiaDoAparelho.MataOPrograma(VigiaDoAparelho.Atualizar) && !VigiaDoAparelho.MataOPrograma(VigiaDoAparelho.RecargaForte),
            "VG-8g quem mata o programa (reiniciar_app, reiniciar_pc) conclui antes de executar");

        var c1 = JsonDocument.Parse(VigiaDoAparelho.CorpoConcluir("a04", "11111111-1111-1111-1111-111111111111", new JsonObject { ["recarregou"] = true, ["tela_chat_depois"] = "ok" }, null)).RootElement;
        checar(c1.GetProperty("acao").GetString() == "concluir" && c1.GetProperty("terminal_uuid").GetString() == "a04" && c1.GetProperty("comando_id").GetString()!.StartsWith("1111", StringComparison.Ordinal)
               && c1.GetProperty("resultado").GetProperty("tela_chat_depois").GetString() == "ok" && c1.GetProperty("erro").ValueKind == JsonValueKind.Null,
            "VG-9 o concluir com sucesso: resultado e erro nulo");
        var c2 = JsonDocument.Parse(VigiaDoAparelho.CorpoConcluir(null, "2", null, VigiaDoAparelho.ErroDeRecusa("venda em andamento"))).RootElement;
        checar(!c2.TryGetProperty("terminal_uuid", out _) && c2.GetProperty("erro").GetString() == "recusado: venda em andamento" && c2.GetProperty("resultado").ValueKind == JsonValueKind.Object,
            "VG-9a a recusa: erro comeca por 'recusado: ', resultado {} (a TV sem terminal_uuid)");
        var c3 = JsonDocument.Parse(VigiaDoAparelho.CorpoConcluir("a04", "3", null, "falhou com o token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnop " + new string('e', 400))).RootElement;
        checar(c3.GetProperty("erro").GetString()!.Length <= 300 && !c3.GetProperty("erro").GetString()!.Contains("eyJ", StringComparison.Ordinal), "VG-9b o erro do concluir vai mascarado e ate 300 letras");
        var c4 = JsonDocument.Parse(VigiaDoAparelho.CorpoConcluir("a04", "4", new JsonObject { ["x"] = new string('z', 5000) }, null)).RootElement;
        checar(c4.GetProperty("resultado").TryGetProperty("resumo", out _), "VG-9c resultado de mais de 4 KB vira so o resumo");
        var d = VigiaDoAparelho.ResultadoDoDiagnostico("C:\\x\\gestor-diagnostico-101010.txt", string.Join("\n", Enumerable.Range(0, 60).Select(i => "linha " + i + " dono@x.com")));
        checar(d["arquivo"]!.GetValue<string>() == "gestor-diagnostico-101010.txt" && d["linhas"]!.AsArray().Count == 40 && !d["linhas"]![0]!.GetValue<string>().Contains("dono@", StringComparison.Ordinal),
            "VG-9d o resultado de coletar_diagnostico: o nome do arquivo e as 40 primeiras linhas, mascaradas");
        var recargas = new List<DateTime> { new(2026, 10, 25, 7, 30, 0), new(2026, 10, 25, 9, 30, 0), new(2026, 10, 25, 9, 50, 0) };
        checar(VigiaDoAparelho.RecargasNaHora(recargas, new DateTime(2026, 10, 25, 10, 0, 0)) == 2 && recargas.Count == 2, "VG-9e recargas_hora conta a ultima hora e esquece as de mais de 2 h");
    }

    private static void Fontes(Action<bool, string> checar)
    {
        var tela = Ler("Telas/ChatIfood.xaml.cs");
        var parte = Ler("Telas/ChatIfood.Vigia.cs");
        var servico = Ler("ServicoVigia.cs");
        var nucleo = Ler("Pdv.Nucleo/VigiaDoAparelho.cs");
        var conversa = Ler("ServicoConversaChat.cs");
        var pagamento = Ler("Telas/Pagamento.xaml.cs");
        checar(tela.Contains("LigarVigia(core);") && parte.Contains("private void LigarVigia(CoreWebView2 core)") && parte.Contains("\"Log.entryAdded\"") && parte.Contains("ServicoVigia.Ligar();"),
            "VG-10 a tela do chat liga o Vigia quando o WebView2 nasce (erros do console pelo CDP e o relogio)");
        checar(conversa.Contains("ServicoVigia.EstadoDoChat = estado;"), "VG-10a o estado do sinal da raspadinha-chat e guardado para o sinal do Vigia (as chaves de sempre)");
        checar(pagamento.Contains("public static volatile bool NaTela;") && pagamento.Contains("Loaded += (_, _) => NaTela = true;") && servico.Contains("Telas.Pagamento.NaTela"),
            "VG-10b a tela de pagamento avisa que esta na tela e o Vigia nao tira print nela");
        checar(servico.Contains("VigiaDoAparelho.Edge, corpo, VigiaDoAparelho.Prazo") && servico.Contains("Servicos.Nuvem().FuncaoAsync("), "VG-10c o sinal e o concluir vao pela sessao do terminal (Authorization Bearer) para a edge vigia");
        checar(servico.Contains("SELECT terminal_uuid FROM terminal LIMIT 1"), "VG-10d o terminal_uuid vem do SQLite do caixa (a mesma leitura da fila)");
        checar(servico.Contains("CapturePreviewAsync") is false && parte.Contains("CoreWebView2CapturePreviewImageFormat.Jpeg"), "VG-10e o print e o CapturePreview do WebView2 em JPEG, na thread da tela");
        checar(servico.Contains("await ConcluirAsync(c, new JsonObject { [\"vai_executar\"] = true }, null).ConfigureAwait(false);\n                    ReabrirOCaixa();") || servico.Contains("ConcluirAsync(c, new JsonObject { [\"vai_executar\"] = true }, null)"),
            "VG-10f reiniciar_app e reiniciar_pc concluem com vai_executar=true ANTES de executar");
        checar(servico.Contains("LoginDoGestor.EntrarAgora()") && servico.Contains("VigiaSairDoGestorAsync"), "VG-10g relogar_gestor sai da conta (se logado) e entra pelo LoginDoGestor.EntrarAgora");
        checar(servico.Contains("ReinicioDoPc.CaminhoDoShutdown(") && servico.Contains("ReinicioDoPc.Argumentos") && !Regex.IsMatch(servico, @"Process\.Start\(\s*""(cmd|powershell)[^""]*"",\s*[a-z]"), "VG-10h reiniciar_pc usa o shutdown do 1.0.48; nenhum shell com texto vindo de fora");
        checar(servico.Contains("if (_falhasSeguidas == 1 || _falhasSeguidas % 30 == 0)"), "VG-10i sem o SQL 220 (404) o diag nao enche: uma linha e depois a cada 30 min");
        checar(servico.Contains("[DllImport(\"kernel32.dll\"") && servico.Contains("GetSystemTimes"), "VG-10j a CPU do PC pelo GetSystemTimes (sem contador de desempenho lento)");
        foreach (var t in new[] { parte, servico, nucleo, Ler("Pdv.Testes/TestesVigiaDoAparelho.cs") })
            if (t.Contains((char)0x2014) || t.Contains((char)0x2013)) { checar(false, "VG-11 os arquivos novos nao tem travessao"); return; }
        checar(true, "VG-11 os arquivos novos nao tem travessao");
        checar(Ler("Pdv.Testes/Program.cs").Contains("TestesVigiaDoAparelho.RodarAsync("), "VG-12 a suite esta registrada no gate");
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
