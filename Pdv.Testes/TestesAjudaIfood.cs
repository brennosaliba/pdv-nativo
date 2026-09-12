using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// FALE COM O iFOOD (12/09/2026, pedido do dono: "no Gestor tem a opção Fale com o
/// iFood; tem como adicionar isso no KDS? E abre um chat na aba chat, no meio: ajuda
/// iFood entre respostas prontas e conversas").
///
/// A regra de quem tem o botão é pura (AjudaIfood). O caminho na tela (botão no detalhe
/// do KDS, o número indo para o chat, a gaveta do Atendimento isolada ao lado do chat,
/// a busca do pedido no Gestor) é travado pelo fonte, com os marcadores do DOM que o
/// diagnóstico da loja de 11/09 20:42 provou existir.
/// </summary>
public static class TestesAjudaIfood
{
    public static void Rodar(Action<bool, string> checar)
    {
        checar(AjudaIfood.PodePedirAjuda("ifood", "5077") && AjudaIfood.PodePedirAjuda("ifood", "#5077"), "pedido do iFood tem o botão (com ou sem #)");
        checar(!AjudaIfood.PodePedirAjuda("balcao", "12") && !AjudaIfood.PodePedirAjuda("encomenda", "12"), "balcão e encomenda não têm atendimento do iFood");
        checar(!AjudaIfood.PodePedirAjuda("ifood", "CD-1234") && !AjudaIfood.PodePedirAjuda("ifood", "#cd-9"), "pedido do Cardápio Digital (CD-) não é do iFood, mesmo vindo pelo mesmo canal");
        checar(!AjudaIfood.PodePedirAjuda("ifood", "") && !AjudaIfood.PodePedirAjuda("ifood", null) && !AjudaIfood.PodePedirAjuda("ifood", "#"), "sem número não há o que procurar no Gestor");
        checar(AjudaIfood.SoDigitos("#5077") == "5077" && AjudaIfood.SoDigitos(" 12-34 ") == "1234" && AjudaIfood.SoDigitos(null) == "", "o Gestor busca só pelos dígitos");
        checar(AjudaIfood.TextoBotao == "Fale com o iFood" && !AjudaIfood.Abrindo("#77").Contains('—'), "o botão tem o nome que o Gestor usa; sem travessão");

        var chat = Fonte(Path.Combine("Telas", "ChatIfood.xaml.cs")) ?? "";
        var chatXaml = Fonte(Path.Combine("Telas", "ChatIfood.xaml")) ?? "";
        var kds = Fonte(Path.Combine("Telas", "Kds.xaml.cs")) ?? "";
        var det = Fonte(Path.Combine("Telas", "DetalhePedidoKds.cs")) ?? "";
        var main = Fonte("MainWindow.xaml.cs") ?? "";
        checar(chatXaml.Contains("Click=\"AbrirAjuda\"") && chat.Contains("window.pdvAbrirAjuda"),
            "a barra do chat tem o botão Ajuda iFood, que abre o Atendimento do iFood");
        checar(chat.Contains("help-center__") && chat.Contains("icon-customer-service"),
            "a gaveta é achada pelos marcadores do Gestor (help-center__*, ícone customer-service), não por posição");
        checar(chat.Contains("var alvos = [chat, ajuda].filter(Boolean)") && chat.Contains("data-pdv-veu") && chat.Contains("data-pdv-ajuda"),
            "o holofote isola DOIS alvos (chat e ajuda), com a cadeia do cabeçalho invisível e a gaveta reposicionada");
        checar(chat.Contains("if (ajudaCandidato()) return;          // ajuda aberta sem chat"),
            "a vigia não clica no chat com a ajuda aberta (poderia fechá-la)");
        checar(chat.Contains("window.pdvFaleComIfood = function (numero)") && chat.Contains("order-search") && chat.Contains("fale com o ifood$/i") && chat.Contains("gestorInteiro:true"),
            "do KDS: busca o pedido no Gestor (campo, card, link) e, se não achar, deixa o Gestor inteiro com a dica");
        checar(chat.Contains("public async Task<bool> FaleComIfoodAsync(string numero)") && chat.Contains("AjudaIfood.PodePedirAjuda(\"ifood\", numero)"),
            "a ponte do KDS para o chat recusa número inválido antes de mexer na página");
        checar(kds.Contains("PediuAjudaIfood") && kds.Contains("AjudaIfood.PodePedirAjuda(t.Origem, t.Numero)"),
            "o KDS só oferece o botão para pedido do iFood (regra pura)");
        checar(det.Contains("Action? faleComIfood = null") && det.Contains("AjudaIfood.TextoBotao") && det.Contains("Resources[\"BotaoBase\"]"),
            "o detalhe do pedido mostra Fale com o iFood ao lado do Fechar (estilo de app, não da Venda)");
        checar(main.Contains("k.PediuAjudaIfood += numero => { MostrarChat(); _ = CamadaChat.FaleComIfoodAsync(numero); };"),
            "o MainWindow leva o pedido do KDS para a aba do chat");
    }

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
