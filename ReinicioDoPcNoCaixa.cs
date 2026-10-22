using System.Diagnostics;
using System.IO;
using System.Windows;
using Dapper;
using Pdv.Nucleo;
using Pdv.Telas;

namespace Pdv;

/// <summary>
/// REINICIAR O COMPUTADOR PELO CAIXA (1.0.48, 22/10/2026). O caminho na tela, nos dois lugares
/// do Fechar / Sair (o menu da venda e o Fechar da janela, normal e quiosque):
///  1. a regra do núcleo (Nucleo/ReinicioDoPc) decide: barrado, uma confirmação ou duas;
///  2. pede ao Windows pelo shutdown.exe da pasta do sistema (/r /t 3, sem /f) e ESPERA o código
///     de saída: só o 0 é "aceitou". Recusa vira aviso na tela e o PDV fica de pé (fechar antes de
///     provar deixaria a tela preta no quiosque);
///  3. aceitou: auditoria no SQLite (quem, de onde, motivo) + uma linha em inicio-windows.txt;
///  4. só então o PDV fecha, limpo (OnExit solta TEF, impressora e chat), e o Windows reinicia.
/// Nada aqui toca o registro: o Shell do quiosque e o Run do início automático continuam, e por
/// eles o PDV volta sozinho depois do reinício. Sem código do gerente (decisão do dono, 22/10).
/// </summary>
public static class ReinicioDoPcNoCaixa
{
    /// <param name="origem">De onde veio o toque (para a auditoria): "menu Fechar / Sair", "Fechar", "Fechar (quiosque)".</param>
    /// <returns>true se o Windows aceitou e o PDV está fechando.</returns>
    public static bool Pedir(Window dono, int itensNaComanda, bool tefOcupado, string? operadorId, string? operadorNome, string origem)
    {
        switch (ReinicioDoPc.Avaliar(tefOcupado, CobrancasNoPinpad(), itensNaComanda))
        {
            case ReinicioDoPc.Caminho.Barrado:
                Dialogo.Avisar(dono, ReinicioDoPc.TituloBarrado, ReinicioDoPc.Barrado, "erro");
                return false;
            case ReinicioDoPc.Caminho.ConfirmarDuasVezes:
                if (!Dialogo.Confirmar(dono, ReinicioDoPc.Titulo, ReinicioDoPc.Pergunta, ReinicioDoPc.BotaoSim, ReinicioDoPc.BotaoNao, perigo: true)) return false;
                if (!Dialogo.Confirmar(dono, ReinicioDoPc.Titulo, ReinicioDoPc.AvisoVenda(itensNaComanda), ReinicioDoPc.BotaoSim, ReinicioDoPc.BotaoNao, perigo: true)) return false;
                break;
            default:
                if (!Dialogo.Confirmar(dono, ReinicioDoPc.Titulo, ReinicioDoPc.Pergunta, ReinicioDoPc.BotaoSim, ReinicioDoPc.BotaoNao, perigo: true)) return false;
                break;
        }
        return Executar(dono, itensNaComanda, operadorId, operadorNome, origem);
    }

    /// <summary>
    /// 'criando'/'aguardando' = o pinpad está com o cliente agora (a mesma leitura do portão da
    /// atualização). Banco ilegível devolve 0 de propósito: um PC travado é exatamente quando o
    /// operador precisa reiniciar, e a confirmação continua valendo.
    /// </summary>
    private static int CobrancasNoPinpad()
    {
        try
        {
            using var cx = Banco.Abrir();
            return cx.ExecuteScalar<int>("SELECT COUNT(*) FROM tef_transacao WHERE situacao IN ('criando','aguardando')");
        }
        catch { return 0; }
    }

    private static bool Executar(Window dono, int itensNaComanda, string? operadorId, string? operadorNome, string origem)
    {
        var sistema = Environment.SystemDirectory;
        if (ReinicioDoPc.MotivoParaNaoReiniciar(sistema, File.Exists) is { } motivo)
        {
            InicioDoWindowsNoCaixa.Anotar(ReinicioDoPc.LinhaDeLog(operadorNome, origem, "não pedi: " + motivo));
            Dialogo.Avisar(dono, ReinicioDoPc.Titulo, "Não deu para pedir o reinício ao Windows: " + motivo, "erro");
            return false;
        }

        int codigo;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ReinicioDoPc.CaminhoDoShutdown(sistema), ReinicioDoPc.Argumentos)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = sistema,
            });
            if (p is null || !p.WaitForExit(10_000))
            {
                InicioDoWindowsNoCaixa.Anotar(ReinicioDoPc.LinhaDeLog(operadorNome, origem, "o shutdown.exe não respondeu em 10 s"));
                Dialogo.Avisar(dono, ReinicioDoPc.Titulo, "O Windows não respondeu ao pedido de reiniciar.", "erro");
                return false;
            }
            codigo = p.ExitCode;
        }
        catch (Exception ex)
        {
            InicioDoWindowsNoCaixa.Anotar(ReinicioDoPc.LinhaDeLog(operadorNome, origem, "falhou: " + TravaDoCtrlAltDel.Tecnico(ex)));
            Dialogo.Avisar(dono, ReinicioDoPc.Titulo, "Não deu para pedir o reinício ao Windows (" + ex.GetType().Name + ").", "erro");
            return false;
        }

        if (ReinicioDoPc.Recusa(codigo) is { } recusa)
        {
            InicioDoWindowsNoCaixa.Anotar(ReinicioDoPc.LinhaDeLog(operadorNome, origem, "recusado, código " + codigo));
            Dialogo.Avisar(dono, ReinicioDoPc.Titulo, recusa, "erro");
            return false;
        }

        // O Windows aceitou: a auditoria e o log ANTES de fechar (o processo some em instantes)
        try
        {
            using var cx = Banco.Abrir();
            Caixa.Auditar(cx, null, ReinicioDoPc.Evento, operadorId, null, ReinicioDoPc.DetalheAuditoria(operadorNome, origem, itensNaComanda));
        }
        catch { /* a auditoria nunca segura o reinício que o Windows já marcou */ }
        InicioDoWindowsNoCaixa.Anotar(ReinicioDoPc.LinhaDeLog(operadorNome, origem, $"o Windows aceitou, reinicia em {ReinicioDoPc.Segundos} s; o PDV fecha agora"));
        Application.Current.Shutdown();
        return true;
    }
}
