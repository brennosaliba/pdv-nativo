using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O RELÓGIO CORRIGIDO PELO SERVIDOR (1.0.42, 16/10/2026, Savassi). O PC do caixa estava 566 s atrasado: toda fala
/// do cliente chegava "no futuro" além dos 2 min de folga e as peneiras (Recente e Triagem "antiga") a descartavam
/// com DateTime.Now. O robô ficou cego o dia inteiro. Agora o sinal devolve a hora do servidor, o caixa guarda o
/// desvio e compara as horas de fala pelo relógio corrigido.
///  RC  o helper puro: nunca lança, desvio -566 s aceita a fala "do futuro", desvio 0 é o de hoje, 40 min recusa
///  RS  o "agora" do sinal e a medição do desvio
///  RE  o estado do caixa: a última medição válida fica; null não apaga
///  RF  o fonte: quem compara hora de fala usa o relógio corrigido; o sinal aprende e manda relogio_seg; sem travessão
/// </summary>
public static class TestesRelogioCorrigido
{
    private static readonly DateTime T0 = new(2026, 10, 16, 12, 22, 0);
    private const string Loja = "ff490000-0000-4000-8000-000000000001";
    private const string Canal = "sendbird_gc_cm_11111111-2222-4333-8444-555555555555_" + Loja;

    public static void Rodar(Action<bool, string> checar)
    {
        OHelper(checar);
        OSinal(checar);
        OEstado(checar);
        PeloFonte(checar);
    }

    private static void OHelper(Action<bool, string> checar)
    {
        var atrasado = TimeSpan.FromSeconds(-566);
        checar(RelogioCorrigido.Agora(T0, null) == T0, "RC-1 sem desvio o relógio corrigido é o do PC");
        checar(RelogioCorrigido.Agora(T0, atrasado) == T0.AddSeconds(566), "RC-2 o PC 566 s atrasado: o corrigido anda 566 s para a frente");
        checar(RelogioCorrigido.Agora(T0, TimeSpan.FromSeconds(90)) == T0.AddSeconds(-90), "RC-2b o PC 90 s adiantado: o corrigido volta 90 s");
        checar(RelogioCorrigido.Agora(T0, TimeSpan.FromDays(400)) == T0 && RelogioCorrigido.Agora(T0, TimeSpan.FromDays(-400)) == T0,
            "RC-3 desvio de mais de um ano é lixo: o relógio fica o do PC");
        checar(RelogioCorrigido.Agora(DateTime.MinValue, TimeSpan.FromDays(10)) == DateTime.MinValue && RelogioCorrigido.Agora(DateTime.MaxValue, TimeSpan.FromDays(-10)) == DateTime.MaxValue,
            "RC-4 nas bordas do DateTime o helper nunca lança (devolve o local)");

        // a fala "do futuro" (o servidor marcou 17:02; o PC ainda acha que são 16:52:34)
        var hora = T0.AddSeconds(566);
        var m = new MensagemChat("conv-1", "cliente", "oi ganhei na raspadinha", new DateTimeOffset(hora), false);
        checar(!ChatRaspadinha.Recente(m, RelogioCorrigido.Agora(T0, TimeSpan.Zero)), "RC-5 com desvio 0 a fala 566 s no futuro é recusada (o de hoje)");
        checar(ChatRaspadinha.Recente(m, RelogioCorrigido.Agora(T0, atrasado)), "RC-5b com desvio -566 s a mesma fala é aceita");
        var velha = m with { Quando = new DateTimeOffset(T0.AddMinutes(-40)) };
        checar(!ChatRaspadinha.Recente(velha, RelogioCorrigido.Agora(T0, TimeSpan.Zero)) && !ChatRaspadinha.Recente(velha, RelogioCorrigido.Agora(T0, atrasado)),
            "RC-5c a fala de 40 min atrás é recusada nos dois casos");

        var sinal = ConversaRaspadinha.LerSinal("{\"ok\":true,\"modo\":\"automatico\",\"envio\":\"sdk\",\"merchant_ids\":[\"" + Loja + "\"],\"intervalo_s\":60}");
        var ms = new DateTimeOffset(hora).ToUnixTimeMilliseconds();
        var q = new MensagemSendbird("MESG", Canal, "7001", null, "cliente-1", "CUSTOMER", "ganhei um brownie", ms, false);
        var canais = new CacheDeCanais();
        var eco = new MemoriaDeEco();
        var t0 = ConversaRaspadinha.Triagem(q, sinal, canais, eco, "loja-1", null, RelogioCorrigido.Agora(T0, TimeSpan.Zero));
        checar(!t0.Entra && t0.Motivo == "antiga", "RC-6 a triagem com desvio 0 descarta a fala do futuro como antiga (o de hoje: " + t0.Motivo + ")");
        var t1 = ConversaRaspadinha.Triagem(q, sinal, canais, eco, "loja-1", null, RelogioCorrigido.Agora(T0, atrasado));
        checar(t1.Entra, "RC-6b com desvio -566 s a triagem aceita (" + t1.Motivo + ")");
        var qVelha = q with { CriadoEmMs = new DateTimeOffset(T0.AddMinutes(-40)).ToUnixTimeMilliseconds() };
        checar(!ConversaRaspadinha.Triagem(qVelha, sinal, canais, eco, "loja-1", null, RelogioCorrigido.Agora(T0, TimeSpan.Zero)).Entra
               && !ConversaRaspadinha.Triagem(qVelha, sinal, canais, eco, "loja-1", null, RelogioCorrigido.Agora(T0, atrasado)).Entra,
            "RC-6c a fala de 40 min atrás é recusada pela triagem nos dois casos");
    }

    private static void OSinal(Action<bool, string> checar)
    {
        var servidor = new DateTimeOffset(2026, 10, 16, 20, 37, 50, 332, TimeSpan.Zero);
        var corpo = "{\"ok\":true,\"agora\":\"2026-10-16T20:37:50.332929+00:00\",\"modo\":\"automatico\"}";
        var lido = RelogioCorrigido.AgoraDoSinal(corpo);
        checar(lido is { } l && (l - servidor).Duration() < TimeSpan.FromMilliseconds(2), "RS-1 o 'agora' do sinal é lido com fuso (" + lido + ")");
        checar(RelogioCorrigido.AgoraDoSinal("{\"ok\":true}") is null && RelogioCorrigido.AgoraDoSinal("{\"agora\":\"ontem\"}") is null
               && RelogioCorrigido.AgoraDoSinal("lixo{") is null && RelogioCorrigido.AgoraDoSinal(null) is null && RelogioCorrigido.AgoraDoSinal("[1]") is null,
            "RS-2 sem 'agora', com texto que não é hora, lixo, nulo ou array: null, sem lançar");
        // a resposta chegou com o PC marcando 566 s a menos
        var local = servidor.AddSeconds(-566);
        var d = RelogioCorrigido.Medir(servidor, local);
        checar(d == TimeSpan.FromSeconds(-566), "RS-3 o desvio é o local menos o servidor: -566 s (o PC atrasado)");
        checar(RelogioCorrigido.Medir(null, local) is null && RelogioCorrigido.Medir(servidor.AddDays(400), local) is null, "RS-4 sem hora do servidor ou com um ano de diferença: sem medição");
        checar(RelogioCorrigido.Segundos(d) == -566 && RelogioCorrigido.Segundos(null) is null, "RS-5 relogio_seg é o desvio em segundos inteiros (positivo = adiantado)");
        checar(RelogioCorrigido.Agora(local.LocalDateTime, d) == servidor.LocalDateTime, "RS-6 corrigido pelo desvio medido, o relógio do caixa é o do servidor");
    }

    private static void OEstado(Action<bool, string> checar)
    {
        RelogioCorrigido.Esquecer();
        checar(RelogioCorrigido.Desvio is null && RelogioCorrigido.MedidoEm is null, "RE-1 ao subir não há desvio");
        var antes = DateTime.Now;
        checar((RelogioCorrigido.AgoraCorrigido - antes).Duration() < TimeSpan.FromSeconds(2), "RE-2 sem desvio o AgoraCorrigido é o DateTime.Now");
        RelogioCorrigido.Aprender(TimeSpan.FromSeconds(-566), T0);
        checar(RelogioCorrigido.Desvio == TimeSpan.FromSeconds(-566) && RelogioCorrigido.MedidoEm == T0, "RE-3 a medição fica guardada com a hora");
        checar((RelogioCorrigido.AgoraCorrigido - DateTime.Now.AddSeconds(566)).Duration() < TimeSpan.FromSeconds(2), "RE-4 o AgoraCorrigido anda 566 s");
        RelogioCorrigido.Aprender(null, T0.AddMinutes(1));
        RelogioCorrigido.Aprender(TimeSpan.FromDays(500), T0.AddMinutes(1));
        checar(RelogioCorrigido.Desvio == TimeSpan.FromSeconds(-566) && RelogioCorrigido.MedidoEm == T0, "RE-5 uma leitura nula ou lixo NÃO apaga a última válida");
        var servidor = new DateTimeOffset(T0.AddSeconds(30));
        var d = RelogioCorrigido.AprenderDoSinal("{\"ok\":true,\"agora\":\"" + servidor.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz") + "\"}", servidor.AddSeconds(-90));
        checar(d == TimeSpan.FromSeconds(-90) && RelogioCorrigido.Desvio == TimeSpan.FromSeconds(-90), "RE-6 AprenderDoSinal mede e guarda numa chamada (-90 s)");
        checar(RelogioCorrigido.AprenderDoSinal("{\"ok\":true}", servidor) is null && RelogioCorrigido.Desvio == TimeSpan.FromSeconds(-90), "RE-7 o sinal sem 'agora' não mexe no desvio");
        RelogioCorrigido.Esquecer();
    }

    private static void PeloFonte(Action<bool, string> checar)
    {
        var servico = Ler("ServicoConversaChat.cs");
        var i = servico.IndexOf("private static async Task SinalAsync()", StringComparison.Ordinal);
        var corpo = i < 0 ? "" : servico[i..];
        var fim = corpo.IndexOf("\n    }\n", StringComparison.Ordinal);
        if (fim > 0) corpo = corpo[..fim];
        checar(corpo.Contains("RelogioCorrigido.AprenderDoSinal(resp, DateTimeOffset.Now)"), "RF-1 o sinal aprende o desvio na resposta (AprenderDoSinal)");
        checar(corpo.Contains("RelogioCorrigido.Segundos(RelogioCorrigido.Desvio)"), "RF-2 relogio_seg sai do relógio corrigido pelo servidor (o do painel é a reserva)");
        checar(corpo.Contains("Telas.AtualizarCaixa.VigiaDoRelogio.Registrar("), "RF-3 cada sinal é uma leitura do vigia do relógio (a linha da Configuração)");
        foreach (var metodo in new[] { "public static void Quadro(", "public static void QuadroRecuperado(", "private static void Tique()", "public static void PeloSocket(" })
        {
            var j = servico.IndexOf(metodo, StringComparison.Ordinal);
            var trecho = j < 0 ? "" : servico[j..Math.Min(servico.Length, j + 1500)];
            checar(trecho.Contains("RelogioCorrigido.AgoraCorrigido") && !trecho.Contains("var agora = DateTime.Now"),
                "RF-4 compara hora de fala pelo relógio corrigido: " + metodo.Replace("(", "").Replace("public static void ", "").Replace("private static void ", ""));
        }
        checar(servico.Contains("Agora = () => RelogioCorrigido.AgoraCorrigido,") && servico.Contains("Recarregou = () => ContarRecarga(RelogioCorrigido.AgoraCorrigido),"),
            "RF-5 o laço do envio (eco, incerta, reserva) corre no relógio corrigido");
        checar(servico.Contains("Lotes.Prontos(RelogioCorrigido.AgoraCorrigido)"), "RF-6 os lotes de falas também");
        var tela = Ler("Telas/ChatIfood.xaml.cs");
        checar(tela.Contains("ChatRaspadinha.Recente(m, RelogioCorrigido.AgoraCorrigido,"), "RF-7 o leitor antigo (OuvirMensagem) usa o relógio corrigido na peneira Recente");
        checar(Ler("ServicoRaspadinhaChat.cs").Contains("ChatRaspadinha.Recente(m, RelogioCorrigido.AgoraCorrigido)"), "RF-8 o leitor do arquivo também");
        checar(Ler("ServicoRecuperacaoChat.cs").Contains("Agora = () => RelogioCorrigido.AgoraCorrigido,"), "RF-9 a recuperação (marca d'água, janela de 12 h) corre no relógio corrigido");
        foreach (var arq in new[] { "Pdv.Nucleo/RelogioCorrigido.cs", "Pdv.Testes/TestesRelogioCorrigido.cs" })
        {
            var t = Ler(arq);
            checar(t.Length > 0 && !t.Contains((char)0x2014) && !t.Contains((char)0x2013), "RF-10 sem travessão: " + arq);
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
