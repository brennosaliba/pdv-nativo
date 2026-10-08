using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// 1.0.24 (08/10/2026): o chat do iFood tem que estar vivo desde que o caixa abre, sem ninguem
/// abrir a aba. O que a Savassi viveu hoje: o caixa abriu as 11:00:51 ("ambiente ok"), a tela do
/// chat ficou recolhida (Collapsed) e o WebView2 nunca ganhou janela; o servico so ligou as
/// 12:21:36, quando alguem tocou em Chat. De manha (07:21 as 10:34) a mesma coisa, e ninguem
/// soube. Quando voltou, 5 envios seguidos cairam em canal_errado/erro_envio com o Gestor recem
/// carregado e pausaram as mensagens do pedido. E a "solicitacao de alteracao" do iFood (cartao
/// do sistema, 5 min para responder) passava em branco pelo leitor da raspadinha.
/// </summary>
public static class TestesChatSempreVivo
{
    public static void Rodar(Action<bool, string> checar)
    {
        var mainXaml = Fonte("MainWindow.xaml") ?? "";
        var main = Fonte("MainWindow.xaml.cs") ?? "";
        var chat = Fonte(Path.Combine("Telas", "ChatIfood.xaml.cs")) ?? "";
        var servico = Fonte("ServicoConversaChat.cs") ?? "";
        var arquivo = Fonte("ServicoArquivoChat.cs") ?? "";
        var vendaXaml = Fonte(Path.Combine("Telas", "Venda.xaml")) ?? "";
        var venda = Fonte(Path.Combine("Telas", "Venda.xaml.cs")) ?? "";

        // ── 1. a camada do chat nunca mais e Collapsed: fora da janela, como o WhatsApp ──
        var iChat = mainXaml.IndexOf("x:Name=\"CamadaChat\"", StringComparison.Ordinal);
        var trechoChat = iChat < 0 ? "" : mainXaml.Substring(Math.Max(0, iChat - 200), Math.Min(900, mainXaml.Length - Math.Max(0, iChat - 200)));
        checar(iChat >= 0 && !trechoChat.Contains("Visibility=\"Collapsed\"") && trechoChat.Contains("TranslateTransform X=\"30000\"")
               && trechoChat.Contains("IsHitTestVisible=\"False\""),
            "CV-1 MainWindow.xaml: a CamadaChat nasce fora da janela (TranslateTransform 30000, sem toque), nao Collapsed");
        checar(!main.Contains("CamadaChat.Visibility"), "CV-2 MainWindow.xaml.cs nao mexe mais em CamadaChat.Visibility");
        checar(main.Contains("private void EsconderChat(bool devolverTeclado)")
               && main.Contains("CamadaChat.RenderTransform = new System.Windows.Media.TranslateTransform(30000, 0)")
               && main.Contains("CamadaChat.RenderTransform = new System.Windows.Media.TranslateTransform(0, 0)")
               && main.Contains("CamadaChat.IsHitTestVisible = true"),
            "CV-3 mostrar traz a camada para o lugar; esconder empurra para fora e tira o toque");
        checar(main.Contains("&& !CamadaChat.IsHitTestVisible") && main.Contains("CamadaChat.Voltou += () => EsconderChat(devolverTeclado: true)"),
            "CV-4 o leitor de etiqueta e o Voltar olham o toque da camada, nao a Visibility");

        // ── 2. a inicializacao deixa rastro e tem vigia ──
        foreach (var passo in new[] { "Passo(\"scripts\")", "Passo(\"captura\")", "Passo(\"navegar\")", "Passo(\"pronto\")" })
            checar(chat.Contains(passo), $"CV-5 a inicializacao anota o passo {passo}");
        checar(chat.Contains("private async Task VigiarInicioAsync(int minha)") && chat.Contains("_ = VigiarInicioAsync(minha);")
               && chat.Contains("inicializacao parada em") && chat.Contains("await RecriarAsync(\"inicializacao parada em \" + _passo)"),
            "CV-6 o vigia anota em 90 s em que passo parou e recria o controle em 180 s");
        var iVigia = chat.IndexOf("private async Task VigiarInicioAsync", StringComparison.Ordinal);
        var vigia = iVigia < 0 ? "" : chat.Substring(iVigia, Math.Min(1200, chat.Length - iVigia));
        checar(vigia.Contains("if (_pronto || minha != _tentativa) return;"), "CV-7 o vigia desiste quando a tela ficou pronta ou o controle foi trocado");

        // ── 3. o script insiste com o SDK frio ──
        var iAchar = chat.IndexOf("async function acharCanal(lista, canal){", StringComparison.Ordinal);
        var achar = iAchar < 0 ? "" : chat.Substring(iAchar, Math.Min(400, chat.Length - iAchar));
        checar(achar.Contains("for (var t = 0; t < 3; t++)") && achar.Contains("await espera(1500)"), "CV-8 acharCanal tenta 3 vezes com 1,5 s de respiro");
        var iPedido = chat.IndexOf("async function pedidoDoCanal(ch){", StringComparison.Ordinal);
        var pedido = iPedido < 0 ? "" : chat.Substring(iPedido, Math.Min(500, chat.Length - iPedido));
        checar(pedido.Contains("for (var t = 0; t < 3; t++)") && pedido.Contains("await espera(1200)"), "CV-9 o metadata do canal e pedido ate 3 vezes");

        // ── 4. o envio tenta de novo antes de dizer falhou ──
        checar(ServicoConversaChat.TentativasDeEnvio == 3 && ServicoConversaChat.RespiroEntreTentativas == TimeSpan.FromSeconds(6),
            "CV-10 3 tentativas com 6 s entre elas (1.0.25: o aquecimento vem antes do respiro; com 15 s a terceira estourava a reserva)");
        var iEnviar = servico.IndexOf("private static async Task EnviarUmaAsync(", StringComparison.Ordinal);
        var enviar = iEnviar < 0 ? "" : servico.Substring(iEnviar, Math.Min(7000, servico.Length - iEnviar));
        checar(enviar.Contains("for (var tentativa = 1; tentativa <= TentativasDeEnvio; tentativa++)")
               && enviar.Contains("Portao.Decidir(s, ultimo.Sinal, ultimo.Em, recebida, DateTime.Now)")
               && enviar.Contains("Portao.Repetir(primeira, recebida, DateTime.Now)")
               && enviar.Contains("if (erro is \"congelada\" or \"sem_cliente\" || tentativa == TentativasDeEnvio) break;")
               && enviar.Contains("await AquecerAsync(ponte, s.PedidoNumero, s.IfoodOrderId)")
               && enviar.Contains("await RelatarAsync(s.Id, \"falhou\", erro ?? \"erro_envio\", null, s)"),
            "CV-11 a 1a tentativa passa pelo portao; as seguintes pelo Repetir (token novo, sem o eco) depois de aquecer; congelada e sem_cliente nao tentam de novo; so no fim relata falhou");
        // 1.0.25: o SDK frio e reconhecido pelo diagnostico, e o caixa aquece sozinho
        {
            var frio = System.Text.Json.Nodes.JsonNode.Parse("{\"achou\":true,\"conferidos\":0,\"canais_cm_por_merchant\":{}}")!.AsObject();
            var quente = System.Text.Json.Nodes.JsonNode.Parse("{\"achou\":true,\"conferidos\":4}")!.AsObject();
            var semSdk = System.Text.Json.Nodes.JsonNode.Parse("{\"achou\":false,\"conferidos\":0}")!.AsObject();
            checar(ServicoConversaChat.SdkFrio(frio, 3) && !ServicoConversaChat.SdkFrio(quente, 3) && !ServicoConversaChat.SdkFrio(frio, 0)
                   && !ServicoConversaChat.SdkFrio(semSdk, 3) && !ServicoConversaChat.SdkFrio(null, 3),
                "CV-11b SdkFrio: achou e conferidos=0 com canais para conferir; quente, sem canais, sem instancia ou sem diagnostico nao e frio");
            checar(ServicoConversaChat.IntervaloDoAquecimento == TimeSpan.FromMinutes(2)
                   && servico.Contains("if (SdkFrio(sdk, Contadores.AlgunsCanais(5).Count) && DateTime.Now - _ultimoAquecimento > IntervaloDoAquecimento)"),
                "CV-11c o sinal aquece pela lista de conversas no maximo a cada 2 min");
            checar(chat.Contains("public async Task<bool> AquecerConversaAsync(string? numero, string? orderUuid)") && chat.Contains("AquecerConversa = AquecerConversaAsync,")
                   && chat.Contains("window.pdvAbrirConversas ? window.pdvAbrirConversas() : false") && chat.Contains("window.pdvBuscarConversa ? window.pdvBuscarConversa("),
                "CV-11d a tela aquece pela lista e pela conversa do pedido, com as funcoes que o Fale com o iFood ja usa");
            checar(chat.Contains("_ = AquecerDepoisDoInicioAsync(minha);") && chat.Contains("if (minha != _tentativa || !_pronto) return;"),
                "CV-11e logo depois de o Gestor carregar o caixa aquece uma vez sozinho, e desiste se o controle foi recriado");
            checar(typeof(PonteDoChat).GetProperty("AquecerConversa") is { } pa
                   && !pa.GetCustomAttributes(typeof(System.Runtime.CompilerServices.RequiredMemberAttribute), false).Any(),
                "CV-11f a porta do aquecimento e opcional na ponte (a bateria e o teste nao precisam dela)");
        }
        checar(enviar.Contains("if (res.Ok || res.Erro is \"sem_resposta\") return;") && enviar.Contains("if (visto == true) { await RelatarAsync(s.Id, \"incerta\""),
            "CV-12 a confirmacao pelo quadro e o 'incerta' continuam como antes dentro do laco");

        // ── 5. a solicitacao de alteracao do iFood vira aviso com som e botao ──
        checar(ChatArquivo.EhSolicitacaoDoCliente(Msg("BRDM", "summary", "Quero adicionar observação ao pedido")), "CV-13 cartao summary 'Quero adicionar observacao' e solicitacao");
        checar(ChatArquivo.EhSolicitacaoDoCliente(Msg("BRDM", "summary", "Quero cancelar o pedido")), "CV-14 'Quero cancelar o pedido' e solicitacao");
        checar(!ChatArquivo.EhSolicitacaoDoCliente(Msg("BRDM", "plain", "*Oba! A loja aceitou sua solicitação.* ✅")), "CV-15 a resposta do iFood (plain) nao e solicitacao");
        checar(!ChatArquivo.EhSolicitacaoDoCliente(Msg("BRDM", "buttonList", "Analise a solicitação e selecione uma opção em até 5 min.")), "CV-16 o cartao dos botoes nao e solicitacao");
        checar(!ChatArquivo.EhSolicitacaoDoCliente(Msg("MESG", null, "Quero minha raspadinha", LadoArquivo.Cliente)), "CV-17 texto do cliente que comeca com Quero nao e solicitacao (vai ao leitor da raspadinha)");
        var texto = ChatArquivo.TextoDaSolicitacao("4661", "Quero adicionar observação ao pedido");
        checar(texto == "#4661: o cliente quer adicionar observação ao pedido. Responda no Gestor em até 5 min.", $"CV-18 o texto do aviso ({texto})");
        var semNumero = ChatArquivo.TextoDaSolicitacao(null, "Quero cancelar o pedido");
        checar(semNumero == "Pedido do iFood: o cliente quer cancelar o pedido. Responda no Gestor em até 5 min.", $"CV-19 sem o numero local ({semNumero})");
        var longo = ChatArquivo.TextoDaSolicitacao("1", "Quero " + new string('x', 200));
        checar(longo.Length <= 160 && ConversaRaspadinha.TextoLimpo(longo, 160), "CV-20 texto longo e cortado e continua limpo");
        checar(!texto.Contains((char)0x2014) && !texto.Contains((char)0x2013) && !ChatArquivo.TextoDaSolicitacao("2", "Quero trocar " + (char)0x2014 + " item").Contains((char)0x2014),
            "CV-21 nenhum travessao no aviso, nem vindo do iFood");
        checar(arquivo.Contains("if (ChatArquivo.EhSolicitacaoDoCliente(t.Mensagem!))") && arquivo.Contains("ServicoConversaChat.AvisoLocal(\"solicitacao\"")
               && arquivo.Contains("SELECT numero FROM kds_ticket WHERE origem = 'ifood' AND ref_id = @r"),
            "CV-22 o arquivo do chat emite o aviso local com o numero do pedido do KDS");
        checar(arquivo.Contains("antes > agora.AddMinutes(-10)"), "CV-23 um aviso por conversa a cada 10 min");
        checar(servico.Contains("or \"humano\" or \"solicitacao\") Alerta.MensagemChat();") && servico.Contains("public static void AvisoLocal(string tipo, string texto, string? orderUuid, string? numero)"),
            "CV-24 o aviso de solicitacao toca o som do chat e passa pelo filtro dos avisos");
        checar(vendaXaml.Contains("x:Name=\"BtnConversaAbrirChat\" Content=\"Abrir o chat\"") && venda.Contains("var abrirChat = a.Tipo is \"solicitacao\" or \"humano\";")
               && venda.Contains("private void AbrirChatDoToast(object sender, RoutedEventArgs e)"),
            "CV-25 o toast ganha 'Abrir o chat' para solicitacao e humano");

        // ── 6. nada novo com travessao ──
        foreach (var (nome, fonte) in new[] { ("MainWindow.xaml", mainXaml), ("ChatArquivo.cs", Fonte(Path.Combine("Pdv.Nucleo", "ChatArquivo.cs")) ?? "") })
        {
            var i = fonte.IndexOf("1.0.24", StringComparison.Ordinal);
            var trecho = i < 0 ? "" : fonte.Substring(i, Math.Min(1500, fonte.Length - i));
            checar(i >= 0 && !trecho.Contains((char)0x2014) && !trecho.Contains((char)0x2013), $"CV-26 {nome}: o trecho novo nao tem travessao");
        }
    }

    private static MensagemArquivo Msg(string cmd, string? custom, string texto, LadoArquivo lado = LadoArquivo.Ifood)
        => new(cmd, "sendbird_gc_cm_ee928383-8981-4f3a-9c1e-0a1b2c3d4e5f_ff493e25-f413-42e5-a46a-96c0b788813f", "1", null, null,
            lado == LadoArquivo.Cliente ? "CUSTOMER" : null, lado, custom, texto, 1759939200000, false, false);

    private static string? Fonte(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj")))
            {
                var c = Path.Combine(dir.FullName, relativo);
                return File.Exists(c) ? File.ReadAllText(c) : null;
            }
        return null;
    }
}
