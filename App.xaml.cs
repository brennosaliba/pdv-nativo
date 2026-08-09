using System.IO;
using System.Windows;
using Dapper;
using Pdv.Nucleo;

namespace Pdv;

public partial class App : Application
{
    /// <summary>
    /// Modos de linha de comando, para instalação e suporte. Rodam e saem — nenhum
    /// deles abre a frente de caixa.
    ///
    ///   Pdv.exe --cupom-teste [arquivo.png]   desenha o cupom de exemplo numa imagem
    ///   Pdv.exe --imprimir-teste ["Impressora"] manda o cupom de exemplo para o papel
    ///
    /// Os dois existem para separar problema de LAYOUT de problema de EMISSÃO: cupom
    /// torto descoberto junto com a primeira nota real vira dois problemas confundidos
    /// num só, e ainda gasta numeração fiscal para descobrir.
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        var args = e.Args;
        if (args.Length > 0 && (args[0] == "--cupom-teste" || args[0] == "--imprimir-teste"))
        {
            Banco.Migrar();
            using var cx = Banco.Abrir();
            var t = cx.QueryFirstOrDefault("SELECT loja_nome, cnpj, serie_nfce FROM terminal LIMIT 1");
            var dados = Servicos.CupomDeExemplo(
                (t?.loja_nome as string) ?? "",
                (t?.cnpj as string) ?? "",
                t is null ? 0 : Convert.ToInt32(t.serie_nfce));

            string? erro;
            string onde;
            if (args[0] == "--cupom-teste")
            {
                onde = args.Length > 1 ? args[1]
                    : Path.Combine(Path.GetTempPath(), "cupom-teste.png");
                erro = await Impressao.PreVisualizarAsync(dados, onde);
            }
            else
            {
                onde = args.Length > 1 ? args[1] : (Impressao.ImpressoraPadrao() ?? "(padrão)");
                erro = await Impressao.ImprimirAsync(dados, args.Length > 1 ? args[1] : null);
            }

            // console anexado: WinExe não tem stdout próprio, mas herda o do terminal
            // que o chamou — sem isso o comando roda em silêncio e ninguém sabe o resultado
            Console.WriteLine(erro is null ? $"ok: {onde}" : $"FALHOU: {erro}");
            Shutdown(erro is null ? 0 : 1);
            return;
        }

        // O emissor fiscal local nasce com o PDV e morre com ele. Janela solta de
        // terminal, alguém fecha — e a loja fica sem nota sem ninguém saber por quê.
        Agente.IniciarVigia();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Agente.Encerrar();
        base.OnExit(e);
    }
}
