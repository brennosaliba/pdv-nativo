using System.Text.Json.Nodes;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A MENSAGEM QUE FALHOU NAO VIRA ECO (1.0.63, 30/10/2026). Na Savassi, das 15:36 as 17:11, 51 das 64 mensagens do
/// pedido ficaram "incerta/eco": a primeira tentativa falhou sem sair (o SDK do chat nao estava ao alcance), o ERP
/// reservou de novo a mesma mensagem e o portao barrou a segunda como repetida, porque a primeira tinha ficado na
/// memoria do eco. Agora o relato "falhou" (com certeza nao saiu) tira o texto da memoria; o que saiu ou pode ter
/// saido continua barrado (nunca duas vezes).
///
///  EC-1..3 o portao: a mesma mensagem duas vezes e eco; depois de Esquecer, passa; Esquecer sem canal nao quebra
///  EC-4..6 o laco: o SDK falha, o relato e "falhou" e a nova reserva da mesma mensagem vai ao SDK de novo
///  EC-7    sem travessao
/// </summary>
public static class TestesEcoDepoisDaFalha
{
    private static readonly DateTime T0 = new(2026, 10, 30, 16, 10, 0);
    private const string Loja = "ff490000-0000-4000-8000-000000000001";
    private const string Pedido = "33333333-2222-4333-8444-555555555555";
    private const string Canal = "sendbird_gc_cm_" + Pedido + "_" + Loja;

    private static SaidaConversa Saida() =>
        new(1000000000777, "msg_pedido_obrigado", 1, "Oi, Maria! Obrigado pelo pedido", "sdk", "reservada", Canal, Pedido, "3072", false, null);

    private static SinalConversa Sinal() => ConversaRaspadinha.LerSinal(
        "{\"ok\":true,\"modo\":\"automatico\",\"palavra_modo\":\"desligado\",\"envio\":\"sdk\",\"comanda_onde\":\"kds\","
        + "\"merchant_ids\":[\"" + Loja + "\"],\"intervalo_s\":60,\"pausado\":null,\"avisos\":[],\"saidas\":[],\"comandas\":[]}");

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        // ── o portao ──
        var portao = new PortaoDeEnvio(new MemoriaDeEco(), new LimiteDeEnvio());
        var (p1, m1) = portao.Decidir(Saida(), Sinal(), T0, T0, T0.AddSeconds(1));
        var (p2, m2) = portao.Decidir(Saida(), Sinal(), T0.AddMinutes(1), T0.AddMinutes(1), T0.AddMinutes(1).AddSeconds(1));
        checar(p1 is not null && m1 == MotivoPortao.Liberado && p2 is null && m2 == MotivoPortao.Eco,
            "EC-1 a mesma mensagem decidida duas vezes: a segunda e eco (o que saiu nunca sai duas vezes)");
        portao.Esquecer(Saida());
        var (p3, m3) = portao.Decidir(Saida(), Sinal(), T0.AddMinutes(2), T0.AddMinutes(2), T0.AddMinutes(2).AddSeconds(1));
        checar(p3 is not null && m3 == MotivoPortao.Liberado, "EC-2 depois de Esquecer (nao saiu), a mesma mensagem passa de novo");
        var semCanal = Saida() with { Canal = null };
        var quebrou = false;
        try { portao.Esquecer(semCanal); } catch { quebrou = true; }
        checar(!quebrou, "EC-3 Esquecer sem canal nao quebra");

        // ── o laco: o SDK falha nas duas reservas; a segunda chega ao SDK (nao e eco) ──
        var cena = new Cena();
        await cena.Rodar(T0);
        var depoisDaPrimeira = cena.Enviados.Count;
        checar(depoisDaPrimeira >= 1 && cena.Relatos.Count == 1 && cena.Relatos[0].Resultado == "falhou",
            $"EC-4 o SDK falhou: relato falhou ({string.Join(",", cena.Relatos.Select(r => r.Resultado + "/" + r.Erro))})");
        await cena.Rodar(T0.AddMinutes(1));
        checar(cena.Enviados.Count > depoisDaPrimeira, "EC-5 a nova reserva da mesma mensagem vai ao SDK de novo (antes: barrada como eco)");
        checar(cena.Relatos.Count == 2 && cena.Relatos[1].Resultado == "falhou" && cena.Relatos[1].Erro != "eco",
            $"EC-6 a segunda tambem relata falhou, nunca incerta/eco ({cena.Relatos.LastOrDefault().Resultado}/{cena.Relatos.LastOrDefault().Erro})");

        var fonte = Ler("Pdv.Testes/TestesEcoDepoisDaFalha.cs");
        checar(fonte.Length > 0 && !fonte.Contains((char)0x2014) && !fonte.Contains((char)0x2013), "EC-7 sem travessao");
    }

    private sealed class Cena
    {
        public DateTime Agora;
        public readonly PortaoDeEnvio Portao = new(new MemoriaDeEco(), new LimiteDeEnvio());
        public readonly ConfirmacaoDeEnvio Confirmacoes = new();
        public readonly List<string> Enviados = new();
        public readonly List<(string Resultado, string? Erro)> Relatos = new();

        public Task Rodar(DateTime recebida)
        {
            Agora = recebida.AddSeconds(1);
            var sinal = Sinal();
            var ponte = new PonteDoChat
            {
                EnviarPeloSdk = p => { Enviados.Add(p.Texto); Agora += TimeSpan.FromSeconds(1); return Task.FromResult(new ResultadoDoScript(false, "congelada", null, null)); },
                AbrirEColar = (_, _, _) => Task.FromResult(false),
                DiagSdk = (_, _) => Task.FromResult<JsonObject?>(null),
                Gestor = () => "logado",
                AquecerConversa = (_, _) => Task.FromResult(true),
            };
            return ServicoConversaChat.EnviarComTentativasAsync(Saida(), recebida, new LacoDeEnvio
            {
                Portao = Portao,
                Confirmacoes = Confirmacoes,
                Sinal = () => (sinal, recebida),
                Ponte = () => ponte,
                Relatar = (_, resultado, erro, _, _) => { Relatos.Add((resultado, erro)); return Task.CompletedTask; },
                Esperar = t => { Agora += t; return Task.CompletedTask; },
                Agora = () => Agora,
                Diag = _ => { },
                Versao = "1.0.63",
            });
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
