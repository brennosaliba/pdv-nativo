using System.IO;

namespace Pdv.Nucleo;

/// <summary>
/// REINICIAR O COMPUTADOR PELO CAIXA (1.0.48, 22/10/2026, pedido do dono: "quando o PC trava
/// ficar fácil reiniciar sem sair do PDV").
///
/// A opção mora no mesmo lugar do Fechar / Sair (menu da venda e o Fechar da janela, normal e
/// quiosque). O que ESTE arquivo decide, sem WPF, para a suíte provar:
///  · se pode (cancelamento/estorno em andamento ou cobrança no pinpad barram: o cliente está com
///    o cartão na mão), e se a venda em andamento com itens pede uma segunda confirmação;
///  · as frases, de uma linha, com as palavras do dono;
///  · o programa e os argumentos: <c>shutdown.exe /r /t 3</c>, da pasta do sistema, SEM /f. O PDV
///    fecha antes, de forma limpa (OnExit: TEF, impressora, chat), e o Windows reinicia 3 s depois.
///    O /c leva o motivo para o log de eventos do Windows (1074);
///  · a resposta do Windows (código de saída): 0 aceitou; qualquer outro é recusa com texto para a
///    tela, e o PDV NÃO fecha (fechar antes de provar deixaria a tela preta no quiosque);
///  · as linhas da auditoria (SQLite) e do log local (inicio-windows.txt): quem, de onde, o motivo.
///
/// Sem código do gerente, de propósito: reiniciar não é risco (o PC volta no PDV, pelo Shell do
/// quiosque ou pelo Run do início automático, e nada disto mexe no registro).
/// </summary>
public static class ReinicioDoPc
{
    public const string Programa = "shutdown.exe";
    public const int Segundos = 3;
    public const string Motivo = "reiniciar pelo PDV";
    public const string Evento = "reiniciar_pc";

    public const string Titulo = "Reiniciar o computador";
    public const string Pergunta = "Reiniciar o computador agora? O PDV volta sozinho.";
    public const string BotaoSim = "Reiniciar";
    public const string BotaoNao = "Voltar";
    public const string TituloBarrado = "Espere terminar";
    public const string Barrado = "Tem uma cobrança na maquininha em andamento. Termine ela antes de reiniciar.";

    /// <summary>Os argumentos do shutdown.exe. Sem /f: o PDV já fechou sozinho quando o prazo vence.</summary>
    public static string Argumentos => $"/r /t {Segundos} /c \"{Motivo}\"";

    public static string CaminhoDoShutdown(string sistema) => Path.Combine(sistema, Programa);

    public static string AvisoVenda(int itens)
        => $"A venda em andamento com {Atualizacao.Plural(itens, "item", "itens")} vai ser perdida. Reiniciar mesmo?";

    public enum Caminho { Barrado, Confirmar, ConfirmarDuasVezes }

    /// <summary>O pinpad decide antes da comanda: cobrança em andamento barra; comanda com itens só avisa.</summary>
    public static Caminho Avaliar(bool tefOcupado, int cobrancasNoPinpad, int itensNaComanda)
    {
        if (tefOcupado || cobrancasNoPinpad > 0) return Caminho.Barrado;
        return itensNaComanda > 0 ? Caminho.ConfirmarDuasVezes : Caminho.Confirmar;
    }

    /// <summary>Só roda o shutdown.exe da pasta do sistema (formato estrito), e só se ele existir.</summary>
    public static string? MotivoParaNaoReiniciar(string? sistema, Func<string, bool> existe)
    {
        if (!TravaDoCtrlAltDel.PastaDoSistema(sistema)) return "pasta do sistema fora do esperado";
        if (!existe(CaminhoDoShutdown(sistema!))) return Programa + " não está em " + sistema;
        return null;
    }

    /// <summary>A recusa do Windows pelo código de saída do shutdown.exe, ou null quando aceitou.</summary>
    public static string? Recusa(int codigo) => codigo switch
    {
        0 => null,
        1190 => "O Windows já tem um reinício marcado.",
        5 => "O Windows não deixou reiniciar (sem permissão).",
        _ => $"O Windows não aceitou reiniciar (código {codigo}).",
    };

    public static string DetalheAuditoria(string? operadorNome, string origem, int itensNaComanda)
        => $"{Motivo}; pedido por {operadorNome ?? "(sem operador)"} em {origem}; itens na comanda: {itensNaComanda}";

    public static string LinhaDeLog(string? operadorNome, string origem, string resultado)
        => $"[reiniciar] {Motivo}: {operadorNome ?? "(sem operador)"} em {origem}: {resultado}";
}
