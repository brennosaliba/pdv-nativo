using System.Text.RegularExpressions;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// RETOMADA DO TURNO SEM LOGIN (11/09/2026, pedido do dono: "ao fechar, atualizar o exe
/// ou algo assim, não precisa ir para a tela de login de novo: já que não fechou o caixa,
/// pode entrar na sessão aberta"). E o som do pedido novo do iFood com 1 a 2 segundos.
///
/// A regra é pura (Caixa.PodeRetomar): turno de gente, do dia de hoje, com atividade nos
/// últimos 15 minutos. O que só existe na tela (MainWindow entrando direto, o heartbeat
/// do relógio e do fechamento do app) é travado pelo fonte.
/// </summary>
public static class TestesRetomada
{
    public static void Rodar(Action<bool, string> checar)
    {
        var agora = new DateTime(2026, 9, 11, 21, 30, 0);
        var dia = Caixa.DiaOperacional(agora);
        Sessao S(string d, bool teste = false) => new("s1", d, "op1", "Maria", agora.AddHours(-3), Dinheiro.DeReais(200m), teste);

        checar(Caixa.PodeRetomar(S(dia), agora.AddMinutes(-2), agora), "turno de hoje com atividade há 2 min: retoma sem login");
        checar(Caixa.PodeRetomar(S(dia), agora.AddMinutes(-14), agora), "há 14 min: ainda retoma");
        checar(!Caixa.PodeRetomar(S(dia), agora.AddMinutes(-16), agora), "há 16 min: pede login (alguém pode ter saído do balcão)");
        checar(!Caixa.PodeRetomar(S(dia), null, agora), "sem registro de atividade (caixa antigo): pede login");
        checar(!Caixa.PodeRetomar(null, agora, agora), "sem turno aberto: pede login");
        checar(!Caixa.PodeRetomar(S("2026-09-10"), agora.AddMinutes(-1), agora), "turno de outro dia: pede login (a abertura explica e manda fechar)");
        checar(!Caixa.PodeRetomar(S(dia, teste: true), agora.AddMinutes(-1), agora), "turno de teste (homologação) não entra por aqui");
        checar(Caixa.JanelaRetomada == TimeSpan.FromMinutes(15), "a janela é de 15 minutos");

        var main = Fonte("MainWindow.xaml.cs") ?? "";
        var venda = Fonte(Path.Combine("Telas", "Venda.xaml.cs")) ?? "";
        var app = Fonte("App.xaml.cs") ?? "";
        var alerta = Fonte("Alerta.cs") ?? "";
        var rot = Trecho(main, "if (ModoHomologacao.EhOperadorDeTeste(_operador?.Id)) _operador = null;", "if (_operador is null) { MostrarLogin(cx); return; }");
        checar(rot.Contains("Caixa.PodeRetomar(") && rot.Contains("Operadores.PorId(cx, aberta.OperadorId)") && rot.Contains("\"sessao_retomada\"") && rot.Contains("MostrarVenda(); return;"),
            "o MainWindow retoma ANTES do login: turno aberto + atividade recente + operador ativo, com auditoria");
        checar(venda.Contains("Caixa.MarcarAtividade(") && app.Contains("Caixa.MarcarAtividade("),
            "o relógio da venda e o fechamento do app gravam a última atividade (é o que a retomada olha)");

        // som do pedido novo: entre 1 e 2 segundos (era 0,7 s)
        var corpo = Trecho(alerta, "public static void PedidoNovo()", "public static void MensagemChat()");
        var ms = Regex.Matches(corpo, @"Console\.Beep\(\d+,\s*(\d+)\)").Select(m => int.Parse(m.Groups[1].Value)).Sum()
               + Regex.Matches(corpo, @"Thread\.Sleep\((\d+)\)").Select(m => int.Parse(m.Groups[1].Value)).Sum();
        checar(ms >= 1000 && ms <= 2000, $"o som do pedido novo dura entre 1 e 2 s (soma dos beeps: {ms} ms)");
    }

    private static string Trecho(string todo, string de, string ate)
    {
        var i = todo.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var f = todo.IndexOf(ate, i + de.Length, StringComparison.Ordinal);
        return f < 0 ? todo[i..] : todo[i..f];
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
