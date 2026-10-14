using System.Text.Json.Nodes;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// 1.0.33 (14/10/2026): o chat do iFood sempre vivo. Em 13/10 às 20:41 o Gestor da Savassi pediu
/// login e o chat ficou parado até 14/10 16:10; o caixa continuou dizendo "logado" ao ERP (o login
/// só era lido no diagnóstico do SDK, que sem o chat carregado estourava o prazo) e ninguém soube.
///  VG  a vigia do Núcleo (pura): parado, login, cura sozinha, aviso, teto e recuo
///  GS  o gestor para o sinal: "logado" só com a página falando
///  ES  as chaves novas do sinal (lista branca) e o tamanho no pior caso (abaixo de 8000)
///  CV  o código da tela do chat: flags do navegador, login a cada 5 s, diagnóstico rápido,
///      navegação que tenta de novo, vigia desde o construtor, Reabrir
///  VD  o aviso fixo na venda: empilhado com o do WhatsApp, Reabrir e Depois, o chat_fora do ERP
/// </summary>
public static class TestesVigiaChat
{
    private static readonly DateTime T0 = new(2026, 10, 14, 15, 0, 0);

    private static VigiaChatIfood.Leitura L(bool pronto = true, bool login = false, DateTime? falou = null, bool naTela = false)
        => new(pronto, login, falou, naTela);

    public static void Rodar(Action<bool, string> checar)
    {
        Vigia(checar);
        GestorDoSinal(checar);
        EstadoDoSinal(checar);
        Codigo(checar);
    }

    private static void Vigia(Action<bool, string> checar)
    {
        // VG-1 página falando, Gestor logado: ok, nada a fazer
        {
            var v = new VigiaChatIfood(T0);
            var r = v.Bater(L(falou: T0), T0);
            var r2 = v.Bater(L(falou: T0.AddSeconds(25)), T0.AddSeconds(30));
            checar(r2.Estado == EstadoChatIfood.Ok && !r2.Avisar && !r2.Recarregar && !r2.Recriar && v.ForaDesde is null && v.Fase == "ok",
                "VG-1 página falando e logada: ok, sem aviso, sem recarga");
        }
        // VG-2 login: aviso aos 2 min, sem recarregar nem recriar, e repete a cada 15 min
        {
            var v = new VigiaChatIfood(T0);
            v.Bater(L(falou: T0), T0);
            var avisos = new List<DateTime>(); var mexeu = false;
            for (var s = 15; s <= 40 * 60; s += 15)
            {
                var t = T0.AddSeconds(s);
                var r = v.Bater(L(login: true, falou: t.AddSeconds(-3)), t);
                if (r.Avisar) avisos.Add(t);
                mexeu |= r.Recarregar || r.Recriar;
            }
            checar(avisos.Count == 3 && avisos[0] == T0.AddSeconds(135) && avisos[1] == T0.AddSeconds(17 * 60 + 15) && avisos[2] == T0.AddSeconds(32 * 60 + 15) && !mexeu
                   && v.Motivo == EstadoChatIfood.Login && v.Fase == "login",
                $"VG-2 login (visto as 15:00:15): aviso 2 min depois e a cada 15 min, nunca recarrega nem recria ({string.Join(",", avisos.Select(a => a.ToString("HH:mm:ss")))})");
        }
        // VG-3 página muda: recarga uma vez aos 90 s, controle novo aos 3 min, aviso aos 2 min (contados da última fala)
        {
            var v = new VigiaChatIfood(T0);
            v.Bater(L(falou: T0), T0);
            DateTime? recarga = null, recriou = null, aviso = null; var recargas = 0;
            for (var s = 15; s <= 4 * 60; s += 15)
            {
                var t = T0.AddSeconds(s);
                var r = v.Bater(L(falou: T0), t);
                if (r.Recarregar) { recargas++; recarga ??= t; }
                if (r.Recriar) { recriou ??= t; break; }
                if (r.Avisar) aviso ??= t;
            }
            checar(recarga == T0.AddSeconds(90) && recargas == 1 && aviso == T0.AddMinutes(2) && recriou == T0.AddMinutes(3)
                   && v.ForaDesde == T0 && v.Motivo == EstadoChatIfood.Parado,
                $"VG-3 página muda: recarga aos 90 s, aviso aos 2 min, controle novo aos 3 min (recarga {recarga:HH:mm:ss}, aviso {aviso:HH:mm:ss}, novo {recriou:HH:mm:ss})");
        }
        // VG-4 um quadro (a página falou de novo, logada) cura e o aviso some
        {
            var v = new VigiaChatIfood(T0);
            v.Bater(L(falou: T0), T0);
            for (var s = 15; s <= 150; s += 15) v.Bater(L(falou: T0), T0.AddSeconds(s));
            var r = v.Bater(L(falou: T0.AddSeconds(160)), T0.AddSeconds(165));
            checar(r.Estado == EstadoChatIfood.Ok && r.Voltou && v.ForaDesde is null && v.UltimoAviso is null,
                "VG-4 a página voltou a falar: ok, Voltou (o aviso some), a queda fecha");
        }
        // VG-5 o operador na tela do chat: nada de recarregar nem recriar (pode estar digitando)
        {
            var v = new VigiaChatIfood(T0);
            v.Bater(L(falou: T0), T0);
            var mexeu = false; var avisou = false;
            for (var s = 15; s <= 10 * 60; s += 15)
            {
                var r = v.Bater(L(falou: T0, naTela: true), T0.AddSeconds(s));
                mexeu |= r.Recarregar || r.Recriar; avisou |= r.Avisar;
            }
            checar(!mexeu && avisou, "VG-5 com o operador na tela do chat a vigia só avisa, não recarrega nem recria");
        }
        // VG-6 teto: no máximo 4 controles novos por hora, com recuo de 3, 6 e 12 min
        {
            var v = new VigiaChatIfood(T0);
            v.Bater(L(falou: T0), T0);
            var recriacoes = new List<DateTime>();
            var pronto = true;
            DateTime? prontoDe = T0;
            for (var s = 15; s <= 120 * 60; s += 15)
            {
                var t = T0.AddSeconds(s);
                // o controle novo some por 30 s e volta pronto, mas a página nunca mais fala
                if (!pronto && t - recriacoes[^1] >= TimeSpan.FromSeconds(30)) { pronto = true; prontoDe = t; }
                var r = v.Bater(L(pronto: pronto, falou: T0), t);
                if (r.Recriar) { recriacoes.Add(t); pronto = false; }
            }
            var naPrimeiraHora = recriacoes.Count(t => t < T0.AddMinutes(60));
            var intervalos = recriacoes.Zip(recriacoes.Skip(1), (a, b) => (b - a).TotalMinutes).ToList();
            checar(naPrimeiraHora <= VigiaChatIfood.TetoRecriarPorHora && recriacoes.Count >= 4
                   && intervalos.Take(3).Zip(new[] { 3.0, 6.0, 12.0 }, (i, min) => i >= min).All(x => x),
                $"VG-6 teto de 4 por hora e recuo de 3, 6 e 12 min entre controles novos ({string.Join(",", recriacoes.Select(r => r.ToString("HH:mm")))})");
        }
        // VG-7 "Depois" cala 30 min; a queda continua
        {
            var v = new VigiaChatIfood(T0);
            v.Bater(L(falou: T0), T0);
            var avisos = new List<DateTime>();
            for (var s = 15; s <= 60 * 60; s += 15)
            {
                var t = T0.AddSeconds(s);
                if (t == T0.AddMinutes(3)) v.Adiar(t);
                if (v.Bater(L(login: true, falou: t), t).Avisar) avisos.Add(t);
            }
            checar(avisos.Count >= 2 && avisos[0] == T0.AddSeconds(135) && avisos[1] == T0.AddMinutes(33) && v.ForaDesde is not null,
                $"VG-7 Depois cala por 30 min e o aviso volta depois ({string.Join(",", avisos.Select(a => a.ToString("HH:mm")))})");
        }
        // VG-8 abrindo pela primeira vez: sem aviso nos primeiros 3 min; depois disso é parado e avisa
        {
            var v = new VigiaChatIfood(T0);
            DateTime? aviso = null;
            for (var s = 15; s <= 6 * 60; s += 15)
            {
                var t = T0.AddSeconds(s);
                var r = v.Bater(L(pronto: false), t);
                if (r.Avisar) aviso ??= t;
                checar(!r.Recriar && !r.Recarregar, "VG-8a o controle que nunca abriu é do vigia da inicialização: a vigia não recria");
                if (r.Recriar || r.Recarregar) break;
            }
            checar(aviso == T0.AddMinutes(3) && v.Motivo == EstadoChatIfood.Parado && v.ForaDesde == T0,
                $"VG-8 controle que não abre: parado aos 3 min, fora desde que começou a abrir, aviso na hora ({aviso:HH:mm:ss})");
        }
        // VG-9 pronto mas a página nunca falou: abrindo até 2 min, depois parado
        {
            var v = new VigiaChatIfood(T0);
            var r1 = v.Bater(L(falou: null), T0.AddSeconds(15));
            var r2 = v.Bater(L(falou: null), T0.AddSeconds(15 + 120));
            checar(r1.Estado == EstadoChatIfood.Abrindo && r2.Estado == EstadoChatIfood.Parado,
                "VG-9 pronto sem nenhuma fala: abrindo, e parado depois de 2 min");
        }
        // VG-10 a fala de um controle anterior não conta para o controle novo
        {
            var v = new VigiaChatIfood(T0);
            v.Bater(L(falou: T0), T0);
            v.Bater(L(pronto: false, falou: T0), T0.AddMinutes(1));
            var r = v.Bater(L(pronto: true, falou: T0), T0.AddMinutes(1).AddSeconds(15));
            var r2 = v.Bater(L(pronto: true, falou: T0), T0.AddMinutes(3).AddSeconds(30));
            checar(r.Estado == EstadoChatIfood.Abrindo && r2.Estado == EstadoChatIfood.Parado,
                "VG-10 controle novo: a fala de antes não prova nada; sem fala nova em 2 min é parado");
        }
        // VG-11 relógio que volta atrás não gera rajada de avisos
        {
            var v = new VigiaChatIfood(T0);
            v.Bater(L(falou: T0), T0);
            var n = 0;
            for (var s = 15; s <= 5 * 60; s += 15) if (v.Bater(L(login: true, falou: T0.AddSeconds(s)), T0.AddSeconds(s)).Avisar) n++;
            for (var s = 15; s <= 5 * 60; s += 15) if (v.Bater(L(login: true, falou: T0.AddSeconds(s)), T0.AddSeconds(s)).Avisar) n++;
            checar(n == 1, $"VG-11 o relógio voltando atrás não repete o aviso ({n})");
        }
        // VG-12 os textos: curtos, sem travessão, login pede a conta da loja, parado oferece Reabrir
        {
            var (tl, al) = VigiaChatIfood.Aviso(EstadoChatIfood.Login);
            var (tp, ap) = VigiaChatIfood.Aviso(EstadoChatIfood.Parado);
            var todos = new[] { tl, al, tp, ap };
            checar(todos.All(t => t.Length <= 60 && !t.Contains('—') && !t.Contains('–'))
                   && al.Contains("conta da loja") && ap.Contains("Reabrir") && !al.Contains("Reabrir"),
                "VG-12 textos do aviso: até 60 caracteres, sem travessão; login pede a conta da loja, parado pede Reabrir");
        }
    }

    private static void GestorDoSinal(Action<bool, string> checar)
    {
        checar(VigiaChatIfood.GestorParaOSinal(true, false, T0, T0.AddSeconds(30)) == "logado"
               && VigiaChatIfood.GestorParaOSinal(true, true, T0, T0.AddSeconds(30)) == "login"
               && VigiaChatIfood.GestorParaOSinal(true, false, T0, T0.AddMinutes(3)) == "ausente"
               && VigiaChatIfood.GestorParaOSinal(true, false, null, T0) == "ausente"
               && VigiaChatIfood.GestorParaOSinal(false, false, T0, T0) == "ausente",
            "GS-1 'logado' só com a página falando há menos de 2 min; login é login; página muda, sem fala ou sem controle é 'ausente'");
    }

    private static void EstadoDoSinal(Action<bool, string> checar)
    {
        var saude = new ConversaRaspadinha.SaudeDoChat(true, "ok", "aviso", 3);
        var e = JsonNode.Parse(ConversaRaspadinha.EstadoDoSinal("logado", true, T0, 0, null, null, null, saude))!.AsObject();
        checar(e["pagina_viva"]!.GetValue<bool>() && e["navegacao"]!.ToString() == "ok"
               && e["vigia"]!["fase"]!.ToString() == "aviso" && e["vigia"]!["recriacoes_hora"]!.GetValue<int>() == 3,
            "ES-5 as chaves novas saem com os valores do contrato (pagina_viva, navegacao, vigia.fase, vigia.recriacoes_hora)");
        var sujo = new ConversaRaspadinha.SaudeDoChat(false, "<script>x</script>", "pedido 1234 da Maria", 999);
        var e2 = JsonNode.Parse(ConversaRaspadinha.EstadoDoSinal("logado", true, T0, 0, null, null, null, sujo))!.AsObject();
        checar(e2["navegacao"]!.ToString() == "carregando" && e2["vigia"]!["fase"]!.ToString() == "abrindo"
               && e2["vigia"]!["recriacoes_hora"]!.GetValue<int>() == 100,
            "ES-6 valor fora do contrato vira o padrão: nenhum texto ou id vaza pelas chaves novas");
        var sem = JsonNode.Parse(ConversaRaspadinha.EstadoDoSinal("logado", true, T0, 0, null, null, null))!.AsObject();
        checar(!sem.ContainsKey("vigia") && !sem.ContainsKey("pagina_viva"),
            "ES-6b sem a saúde (PDV que não tem a vigia) o estado não ganha a chave 'vigia' (o ERP só pede Reabrir de quem tem)");
        // ES-7 o pior caso: o sinal com tudo cheio fica abaixo de 8000 caracteres (acima disso o ERP descarta o estado)
        var quadros = new JsonObject();
        for (var i = 0; i < 40; i++) quadros["campo_" + i.ToString("00")] = 100_000;
        quadros["campos_raiz"] = new JsonArray(Enumerable.Range(0, 60).Select(i => (JsonNode?)JsonValue.Create("campo_raiz_" + i.ToString("00"))).ToArray());
        var sdk = new JsonObject
        {
            ["achou"] = true, ["user_id_igual_ws"] = true, ["tem_order_uuid"] = true, ["via_webpack"] = true, ["via_react"] = true,
            ["instancias"] = 100_000, ["congeladas"] = 100_000, ["conferidos"] = 100_000, ["modulos"] = 100_000, ["runtimes"] = 100_000,
        };
        var canais = Enumerable.Range(0, 20).ToDictionary(i => i.ToString("x4"), _ => 100_000);
        var pior = ConversaRaspadinha.EstadoDoSinal("logado", true, T0, int.MaxValue, quadros, sdk, canais, saude);
        checar(pior.Length < 8000, $"ES-7 o estado do sinal no pior caso tem {pior.Length} caracteres (abaixo de 8000)");
    }

    private static void Codigo(Action<bool, string> checar)
    {
        var chat = Fonte(Path.Combine("Telas", "ChatIfood.xaml.cs")) ?? "";
        var whats = Fonte(Path.Combine("Telas", "ChatWhatsApp.xaml.cs")) ?? "";
        var servico = Fonte("ServicoConversaChat.cs") ?? "";
        var vendaXaml = Fonte(Path.Combine("Telas", "Venda.xaml")) ?? "";
        var venda = Fonte(Path.Combine("Telas", "Venda.xaml.cs")) ?? "";

        checar(chat.Contains("\"--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows\"")
               && chat.Contains("() => new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = ArgumentosDoNavegador });"),
            "CV-36 o chat passa ao ambiente os 3 flags que mantêm o Gestor acordado (timers, renderer, janela coberta)");
        checar(whats.Contains("--autoplay-policy=no-user-gesture-required --disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows"),
            "CV-36b o WhatsApp continua com o autoplay e os dois flags, e ganha o da janela coberta");
        checar(chat.Contains("envia({ tipo: 'naolidas', texto: txt, login: !!document.querySelector('input[type=\"password\"]') });")
               && chat.Contains("_gestorPedeLogin = lg.ValueKind == JsonValueKind.True;"),
            "CV-37 o login do Gestor vem com o contador de não lidas (a cada 5 s), não só do diagnóstico do SDK");
        checar(chat.Contains("Interlocked.Exchange(ref _falouTicks, DateTime.Now.Ticks);")
               && chat.Contains("Gestor = () => VigiaChatIfood.GestorParaOSinal(_pronto, _gestorPedeLogin, PaginaFalouEm, DateTime.Now),"),
            "CV-37b toda fala da página conta como vida, e o gestor do sinal é o da vigia (sem 'logado' com a página morta)");
        checar(chat.Contains("if (!lista.length) { r.conferidos = 0; envia(r); return; }")
               && chat.Contains("Task.Delay(TimeSpan.FromSeconds(10))")
               && servico.Contains(".WaitAsync(TimeSpan.FromSeconds(12))"),
            "SB-78 sem instância do SDK o diagnóstico responde na hora; prazos de 10 s (tela) e 12 s (sinal)");
        checar(chat.Contains("_navegacao = e.IsSuccess ? \"ok\" : \"falhou\";")
               && chat.Contains("Diag(e.IsSuccess ? \"navegou: ok\" : $\"navegou: falhou {e.WebErrorStatus}\");")
               && chat.Contains("else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) _ = NavegarDeNovoAsync(minha);")
               && chat.Contains("TimeSpan.FromSeconds(30 * (1 << Math.Min(_recuoNavegacao, 2)))"),
            "CV-38 a navegação anota o resultado e a que falhou tenta de novo em 30, 60 e 120 s (cancelada não conta)");
        var iCtor = chat.IndexOf("public ChatIfood()", StringComparison.Ordinal);
        var ctor = iCtor < 0 ? "" : chat.Substring(iCtor, Math.Min(2500, chat.Length - iCtor));
        checar(ctor.Contains("_relogioVigia.Tick += (_, _) => BaterVigia();") && ctor.Contains("_relogioVigia.Start();")
               && ctor.Contains("ServicoVigiaChat.AoReabrir = ReabrirAsync;"),
            "CV-40 o relógio da vigia nasce no construtor (roda com o Gestor ainda não pronto) e o Reabrir da venda chega na tela do chat");
        checar(chat.Contains("_vigia.Bater(new VigiaChatIfood.Leitura(_pronto, _gestorPedeLogin, PaginaFalouEm, IsHitTestVisible), agora)")
               && chat.Contains("_ = RecriarAsync(\"vigia: pagina muda\");") && chat.Contains("await RecriarAsync(\"reabrir pelo aviso\", porGente: true);")
               && chat.Contains("if (!porGente && !_tetoRecriar.Permitir(DateTime.UtcNow))"),
            "CV-41 a vigia lê o operador na tela (o toque da camada); o controle novo da vigia passa pelo teto, o Reabrir de gente não");

        var stack = System.Text.RegularExpressions.Regex.Match(vendaXaml, @"<StackPanel x:Name=""AvisosFixos"".*?</Border>\s*</StackPanel>",
            System.Text.RegularExpressions.RegexOptions.Singleline).Value;
        checar(stack.Contains("x:Name=\"AvisoWhatsAppCaido\"") && stack.Contains("x:Name=\"AvisoChatDesligado\"")
               && stack.Contains("Margin=\"0,134,14,0\"") && stack.Contains("Panel.ZIndex=\"99\""),
            "VD-1 os dois avisos fixos (WhatsApp caído e chat fora) empilhados no mesmo lugar");
        var aviso = System.Text.RegularExpressions.Regex.Match(vendaXaml, @"<Border x:Name=""AvisoChatDesligado"".*?</Border>",
            System.Text.RegularExpressions.RegexOptions.Singleline).Value;
        checar(aviso.Contains("Text=\"🔴\"") && aviso.Contains("Click=\"ReabrirChatPeloAviso\"") && aviso.Contains("Click=\"AdiarAvisoChat\"")
               && aviso.Contains("MouseLeftButtonUp=\"AbrirChatPeloAviso\"") && aviso.Contains("Visibility=\"Collapsed\""),
            "VD-2 o aviso do chat é vermelho, nasce escondido, o toque abre o chat e tem Reabrir e Depois");
        foreach (var ev in new[] { "Caiu", "Voltou" })
            checar(venda.Contains($"ServicoVigiaChat.{ev} += ") && venda.Contains($"ServicoVigiaChat.{ev} -= "),
                $"VD-3 a venda escuta ServicoVigiaChat.{ev} ao entrar e solta ao sair");
        checar(venda.Contains("if (_comanda.Count == 0) Alerta.WhatsAppCaiu();")
               && venda.Contains("BtnChatReabrir.Visibility = motivo == Nucleo.EstadoChatIfood.Login ? Visibility.Collapsed : Visibility.Visible;"),
            "VD-4 o som só com a comanda vazia; no login não há Reabrir (só a pessoa no Gestor resolve)");
        var iConv = venda.IndexOf("private void AvisoDaConversa(AvisoDoChat a)", StringComparison.Ordinal);
        var conv = iConv < 0 ? "" : venda.Substring(iConv, Math.Min(1600, venda.Length - iConv));
        var iToast = conv.IndexOf("ToastConversa.Visibility", StringComparison.Ordinal);
        checar(conv.Contains("if (a.Tipo == \"chat_fora\")") && conv.Contains("if (a.Tipo == \"chat_voltou\")")
               && conv.IndexOf("if (a.Tipo == \"chat_fora\")", StringComparison.Ordinal) < (iToast < 0 ? int.MaxValue : iToast),
            "VD-6 o chat_fora e o chat_voltou do ERP vão para o aviso fixo, antes do toast da conversa (nunca dois avisos)");
        checar(venda.Contains("ToastConversa.Margin = new Thickness(0, 204 + desce, 14, 0);") && venda.Contains("ToastWhatsApp.Margin = new Thickness(0, 134 + desce, 14, 0);"),
            "VD-7 com aviso fixo na tela, os toasts do WhatsApp e da conversa descem para baixo dele");
    }

    private static string? Fonte(string relativo)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var c = Path.Combine(d.FullName, relativo);
            if (File.Exists(c) && File.Exists(Path.Combine(d.FullName, "Pdv.csproj"))) return File.ReadAllText(c).Replace("\r\n", "\n");
            d = d.Parent;
        }
        return null;
    }
}
