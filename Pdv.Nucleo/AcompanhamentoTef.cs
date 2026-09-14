using System.Globalization;

namespace Pdv.Nucleo;

/// <summary>
/// OS TEXTOS DA TELA QUE ACOMPANHA UMA OPERAÇÃO DO TEF (a instalação do ponto de captura).
///
/// 14/09/2026, Castelo: "Instalar ponto de captura" ficou mais de cinco minutos com a tela
/// parada, duas vezes, sem dizer nada e sem jeito de sair. O dono matou o caixa pelo
/// Gerenciador de Tarefas. O que a tela passa a mostrar:
///  · um cronômetro ("Instalando... 1 min 20 s"), porque tela parada sem relógio é
///    indistinguível de travamento;
///  · a última mensagem que a biblioteca mandou;
///  · o prazo máximo em texto simples;
///  · e, quando a biblioteca está presa numa chamada há mais de 30 s, o que resolve: tirar
///    o cabo USB do pinpad (medido no mesmo log: a chamada voltou em 0,3 s).
///
/// ⚠️ SEM PORCENTAGEM. A biblioteca não informa progresso nenhum; barra que anda sozinha
/// seria mentira.
/// </summary>
public static class AcompanhamentoTef
{
    /// <summary>Prazo da instalação: teste do pinpad, conversa com a PayGo e as perguntas da tela.</summary>
    public static readonly TimeSpan PrazoInstalacao = TimeSpan.FromMinutes(3);

    /// <summary>A partir de quantos segundos presa na mesma chamada a tela passa a sugerir o cabo.</summary>
    public const int SemRespostaSegundos = 30;

    /// <summary>"8 s", "1 min", "1 min 20 s".</summary>
    public static string Duracao(TimeSpan t)
    {
        var s = (long)Math.Max(0, Math.Floor(t.TotalSeconds));
        if (s < 60) return s.ToString(CultureInfo.InvariantCulture) + " s";
        var m = s / 60;
        var r = s % 60;
        return r == 0
            ? $"{m.ToString(CultureInfo.InvariantCulture)} min"
            : $"{m.ToString(CultureInfo.InvariantCulture)} min {r.ToString(CultureInfo.InvariantCulture)} s";
    }

    /// <summary>"Instalando... 1 min 20 s".</summary>
    public static string Cronometro(string verbo, TimeSpan decorrido) => $"{verbo}... {Duracao(decorrido)}";

    /// <summary>"Prazo máximo: 3 minutos. Passou disso, toque em Cancelar."</summary>
    public static string Prazo(TimeSpan prazo)
    {
        var minutos = prazo.TotalMinutes;
        var texto = minutos >= 1 && Math.Abs(minutos - Math.Round(minutos)) < 0.001
            ? (Math.Round(minutos) == 1 ? "1 minuto" : $"{Math.Round(minutos).ToString(CultureInfo.InvariantCulture)} minutos")
            : Duracao(prazo);
        return $"Prazo máximo: {texto}. Passou disso, toque em Cancelar.";
    }

    public static bool PassouDoPrazo(TimeSpan decorrido, TimeSpan prazo) => decorrido >= prazo;

    public const string TextoPassouDoPrazo = "Passou do prazo. Toque em Cancelar.";

    /// <summary>A linha do meio: o que a biblioteca disse por último, ou o aviso de chamada presa.</summary>
    public static string Recado(string? ultimo, ProvedorPGWebLib.ChamadaNativa? emVoo, DateTime agoraUtc)
    {
        if (emVoo is { } v && agoraUtc - v.DesdeUtc >= TimeSpan.FromSeconds(SemRespostaSegundos))
            return $"A maquininha não responde há {Duracao(agoraUtc - v.DesdeUtc)}. Tire o cabo USB do pinpad por 10 segundos: isso encerra a tentativa.";
        return string.IsNullOrWhiteSpace(ultimo)
            ? "Esperando a maquininha responder."
            : "Última mensagem da maquininha: " + ultimo.Trim();
    }

    /// <summary>O que a Configuração diz depois do Cancelar.</summary>
    public static string Cancelada(string oQue)
        => $"{oQue} cancelada. Se o pinpad continuar ocupado, tire o cabo USB por 10 segundos antes de tentar de novo.";
}
