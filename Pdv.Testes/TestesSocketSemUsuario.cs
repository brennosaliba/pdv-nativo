using System.Text.Json.Nodes;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O SOCKET SEM USUÁRIO (1.0.42, 16/10/2026). A TV da Savassi (2.0.10) ficou com 2 sockets abertos SEM user_id na
/// URL depois das reconexões do Gestor (sockets_sem_usuario 2, socket_escolha ambiguo, socket_ativo_s null): a
/// reconexão do SDK do Sendbird vai com ...&amp;key=&lt;sessão&gt; sem user_id. O caixa tem o mesmo risco. Agora o
/// livro das conexões aprende o usuário também pelo LOGI RECEBIDO naquela conexão (user_id) e por qualquer quadro
/// de canal de cliente recebido nela (o usuário da loja); a escolha do socket, sockets_usuarios/sem_usuario e
/// socket_ativo_s passam a usar esse usuário.
///  LV  o livro: aprende pelo LOGI e pelo canal de cliente; reconexão troca o requestId e mantém a atribuição
///  ES  a escolha: URL sem user_id + LOGI da loja = usuario/unico, não ambiguo; o entregador nunca é escolhido
///  DG  o diag: sockets_sem_usuario conta só o que ninguém sabe de quem é
///  FO  o fonte: a tela alimenta o livro e passa os usuários à escolha; sem travessão
/// </summary>
public static class TestesSocketSemUsuario
{
    private static readonly DateTime T0 = new(2026, 10, 16, 17, 2, 0);
    private const string Loja = "loja-ff49";
    private const string Entregador = "entregador-77";
    private const string ComUsuario = "wss://ws-abc.sendbird.com/?p=JS&user_id=" + Loja + "&access_token=t1&key=k1";
    private const string Reconexao = "wss://ws-abc.sendbird.com/?p=JS&key=k2&Request-Sent-Timestamp=1760630000000";
    private const string ReconexaoDoEntregador = "wss://ws-abc.sendbird.com/?p=JS&key=k3&Request-Sent-Timestamp=1760630001000";
    private const string Canal = "sendbird_gc_cm_11111111-2222-4333-8444-555555555555_ff490000-0000-4000-8000-000000000001";

    private static string Logi(string usuario) => "LOGI{\"key\":\"k2k2k2k2k2k2k2k2k2k2k2k2k2k2\",\"user_id\":\"" + usuario + "\",\"ekey\":\"e\"}\n";

    public static void Rodar(Action<bool, string> checar)
    {
        OLivro(checar);
        AEscolha(checar);
        ODiag(checar);
        PeloFonte(checar);
    }

    private static void OLivro(Action<bool, string> checar)
    {
        var livro = new ConexoesDoSocket();
        checar(livro.Abriu("r1", ComUsuario) && livro.Usuario("r1") == Loja, "LV-1 a URL com user_id ensina o usuário, como sempre");
        checar(livro.Abriu("r2", Reconexao) && livro.Usuario("r2") is null, "LV-2 a reconexão com key= e sem user_id abre sem usuário");
        checar(QuadroSendbird.UsuarioDoLogi(Logi(Loja)) == Loja && QuadroSendbird.UsuarioDoLogi("LOGI{\"key\":\"k\",\"user_id\":1234}\n") == "1234"
               && QuadroSendbird.UsuarioDoLogi("MESG{\"user_id\":\"x\"}") is null && QuadroSendbird.UsuarioDoLogi("LOGI{\"key\":\"k\"}") is null && QuadroSendbird.UsuarioDoLogi(null) is null,
            "LV-3 o user_id do LOGI é lido (texto ou número); MESG, LOGI sem user_id e nulo não ensinam");
        checar(livro.Aprendeu("r2", QuadroSendbird.UsuarioDoLogi(Logi(Loja)), porCima: true) && livro.Usuario("r2") == Loja,
            "LV-4 o LOGI recebido na conexão ensina o usuário dela");
        checar(!livro.Aprendeu("r9", Loja) && !livro.Aprendeu("r2", "") && !livro.Aprendeu(null, Loja), "LV-5 conexão desconhecida, usuário vazio ou requestId nulo: nada");
        livro.Fechou("r2");
        checar(livro.Usuario("r2") is null && !livro.Aprendeu("r2", Loja, porCima: true), "LV-6 a conexão fechada esquece e não aprende mais");
        // a reconexão seguinte: outro requestId, sem user_id, e o LOGI dela ensina de novo
        checar(livro.Abriu("r3", Reconexao) && livro.Usuario("r3") is null && livro.Aprendeu("r3", Loja, porCima: true) && livro.Usuario("r3") == Loja,
            "LV-7 a reconexão troca o requestId e a atribuição é refeita pelo LOGI dela");
        // pelo canal de cliente: só quando a conexão ainda não tem usuário
        checar(livro.Abriu("r4", Reconexao) && livro.Aprendeu("r4", Loja) && livro.Usuario("r4") == Loja, "LV-8 o quadro de canal de cliente ensina o usuário da loja na conexão sem usuário");
        checar(livro.Abriu("r5", ComUsuario.Replace(Loja, Entregador)) && !livro.Aprendeu("r5", Loja) && livro.Usuario("r5") == Entregador,
            "LV-9 mas não escreve por cima de um usuário que a URL já disse (porCima só no LOGI)");
        checar(livro.Aprendeu("r5", Loja, porCima: true) && livro.Usuario("r5") == Loja, "LV-9b o LOGI (porCima) é a verdade do servidor e vale por cima da URL");
        checar(livro.UsuarioPorUrl(Reconexao) == Loja && livro.UsuarioPorUrl(ComUsuario) == Loja && livro.UsuarioPorUrl("wss://ws-abc.sendbird.com/?p=JS&key=zz") is null && livro.UsuarioPorUrl(null) is null,
            "LV-10 UsuarioPorUrl acha o usuário aprendido pela URL do socket da heap (a mesma chave de sempre)");
    }

    private static void AEscolha(Action<bool, string> checar)
    {
        var abertos = new[] { Reconexao, ReconexaoDoEntregador };
        var semLivro = EnvioPeloSocket.Escolher(abertos, null, Loja, null);
        checar(semLivro.Escolha == EnvioPeloSocket.Ambiguo && semLivro.Indice == -1, "ES-1 sem o livro, dois sockets sem user_id e a loja conhecida: ambiguo (o 2.0.10 da Savassi)");
        var livro = new ConexoesDoSocket();
        livro.Abriu("r1", Reconexao); livro.Aprendeu("r1", Loja, porCima: true);
        livro.Abriu("r2", ReconexaoDoEntregador); livro.Aprendeu("r2", Entregador, porCima: true);
        var usuarios = EnvioPeloSocket.UsuariosDosAbertos(abertos, livro.UsuarioPorUrl);
        checar(usuarios.Count == 2 && usuarios[0] == Loja && usuarios[1] == Entregador, "ES-2 os usuários dos abertos vêm do livro, na ordem da heap");
        var comLivro = EnvioPeloSocket.Escolher(abertos, null, Loja, null, usuarios);
        checar(comLivro.Escolha == EnvioPeloSocket.PorUsuario && comLivro.Indice == 0, "ES-3 com o LOGI aprendido: o socket da loja, por usuário");
        var soLoja = EnvioPeloSocket.Escolher(new[] { Reconexao }, null, null, null, new[] { Loja });
        checar(soLoja.Escolha == EnvioPeloSocket.Unico && soLoja.Indice == 0, "ES-4 um socket só, sem user_id na URL mas com o LOGI, loja desconhecida: unico");
        var soEntregador = EnvioPeloSocket.Escolher(new[] { ReconexaoDoEntregador }, null, Loja, null, new[] { Entregador });
        checar(soEntregador.Indice == -1 && soEntregador.Escolha == EnvioPeloSocket.Ambiguo, "ES-5 só o socket do entregador (pelo LOGI) com a loja conhecida: ninguém (nunca mandar como o entregador)");
        var dois = EnvioPeloSocket.Escolher(abertos, null, null, null, usuarios);
        checar(dois.Escolha == EnvioPeloSocket.Ambiguo, "ES-6 dois usuários distintos pelo LOGI e a loja desconhecida: ambiguo");
        var urlVence = EnvioPeloSocket.Escolher(new[] { ComUsuario.Replace(Loja, Entregador) }, null, Loja, null, new[] { Loja });
        checar(urlVence.Indice == -1, "ES-7 a URL que diz user_id vale por cima do aprendido (o livro só completa o que falta)");
        // a atividade segue desempatando entre dois sockets da loja aprendidos pelo LOGI
        var doisDaLoja = new[] { Reconexao, ReconexaoDoEntregador };
        var ativ = new DateTime?[] { T0.AddSeconds(-60), T0.AddSeconds(-2) };
        var maisAtivo = EnvioPeloSocket.Escolher(doisDaLoja, null, Loja, ativ, new[] { Loja, Loja });
        checar(maisAtivo.Indice == 1 && maisAtivo.Escolha == EnvioPeloSocket.PorUsuario, "ES-8 entre dois sockets da loja (pelo LOGI) vence o mais ativo");
        // pelo pedido (EnviarAsync): a porta Usuario entra na escolha
        var pedido = new PedidoPeloSocket(Canal, "11111111-2222-4333-8444-555555555555", "oi", "pdv1-1-1", null, Loja, Atividade: null, Usuario: livro.UsuarioPorUrl);
        checar(pedido.Usuario is not null && pedido.Usuario(Reconexao) == Loja, "ES-9 o pedido da camada C leva a porta do usuário por URL");
    }

    private static void ODiag(Action<bool, string> checar)
    {
        var abertos = new[] { Reconexao, ReconexaoDoEntregador };
        var antes = EnvioPeloSocket.Resumir(abertos, -1, EnvioPeloSocket.Ambiguo, null);
        checar(antes.SemUsuario == 2 && antes.Usuarios == 0, "DG-1 sem o livro: 2 sem usuário, 0 usuários (o sinal da TV em 16/10)");
        var depois = EnvioPeloSocket.Resumir(abertos, 0, EnvioPeloSocket.PorUsuario, new DateTime?[] { T0.AddSeconds(-12), null }, new[] { Loja, Entregador });
        checar(depois.SemUsuario == 0 && depois.Usuarios == 2 && depois.AtivoEm == T0.AddSeconds(-12), "DG-2 com o livro: 0 sem usuário, 2 usuários, e o escolhido tem a atividade");
        var o = new JsonObject();
        EnvioPeloSocket.DiagDosSockets(o, depois, T0);
        checar(o["sockets_sem_usuario"]?.GetValue<int>() == 0 && o["sockets_usuarios"]?.GetValue<int>() == 2 && o["socket_ativo_s"]?.GetValue<int>() == 12 && o["socket_escolha"]?.GetValue<string>() == "usuario",
            "DG-3 o diag do sinal diz usuario, 2 usuários, 0 sem usuário e socket_ativo_s 12");
        var meio = EnvioPeloSocket.Resumir(abertos, 0, EnvioPeloSocket.PorUsuario, null, new[] { Loja, null });
        checar(meio.SemUsuario == 1 && meio.Usuarios == 1, "DG-4 o socket que ninguém sabe de quem é continua contado em sem_usuario");
    }

    private static void PeloFonte(Action<bool, string> checar)
    {
        var tela = Ler("Telas/ChatIfood.xaml.cs");
        checar(tela.Contains("_conexoes.Aprendeu(conexao, QuadroSendbird.UsuarioDoLogi(p), porCima: true);"), "FO-1 o LOGI recebido ensina o usuário da conexão ao livro");
        checar(tela.Contains("if (CanalIfood.Ler(canalDoQuadro) is not null) _conexoes.Aprendeu(conexao, ServicoConversaChat.UsuarioDaLoja);"),
            "FO-2 o quadro de canal de cliente ensina o usuário da loja à conexão sem usuário");
        checar(tela.Contains("Usuario: _conexoes.UsuarioPorUrl"), "FO-3 o envio pelo socket leva o usuário aprendido");
        checar(tela.Contains("EnvioPeloSocket.UsuariosDosAbertos(urls, _conexoes.UsuarioPorUrl)") && tela.Contains("EnvioPeloSocket.Resumir(urls, indice, escolha, atividade, usuarios)"),
            "FO-4 a contagem para o sinal escolhe e resume com os usuários do livro");
        var nucleo = Ler("Pdv.Nucleo/EnvioPeloSocket.cs");
        checar(nucleo.Contains("var usuarios = UsuariosDosAbertos(urls, p.Usuario);") && nucleo.Contains("Escolher(urls, p.UrlDoCanal, p.UsuarioDaLoja, atividade, usuarios)"),
            "FO-5 o EnviarAsync da camada C usa os usuários do livro");
        foreach (var arq in new[] { "Pdv.Testes/TestesSocketSemUsuario.cs" })
        {
            var t = Ler(arq);
            checar(t.Length > 0 && !t.Contains((char)0x2014) && !t.Contains((char)0x2013), "FO-6 sem travessão: " + arq);
        }
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
