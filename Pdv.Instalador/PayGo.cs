using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Pdv.Instalador;

/// <summary>
/// A ETAPA DO PAYGO — instalar o software de TEF logo depois do PDV, para o dono da
/// loja não precisar caçar um segundo instalador.
///
/// O paygo.exe é um Inno Setup 6.3.3 assinado pela Setis, e ele NÃO se comporta como
/// um instalador comum. Três coisas foram descobertas lendo os logs de instalações
/// reais desta máquina, e as três mudam o código:
///
///  1. O PROCESSO QUE VOCÊ LANÇA NÃO É O QUE INSTALA. A instância lançada relança a
///     si mesma e morre em ~130 ms com "InitializeSetup returned False". Quem instala
///     é outro PID, que dura 14 s ou mais. Ou seja: WaitForExit volta quase na hora e
///     não diz nada sobre o resultado, e o código de saída dele é lixo. Só serve
///     EVIDÊNCIA no fim — registro e arquivo no disco.
///
///  2. SÃO DOIS INSTALADORES ENCADEADOS, com AppId diferente. O de fora se instala em
///     C:\ProgramData\PayGo e dispara o de dentro, que é quem põe o PayGo.exe em
///     Program Files (x86). Esta máquina prova que dá para ter o de fora novo e o de
///     dentro velho: em 28/08 o segundo estágio falhou em "Acesso negado" e reverteu.
///     Por isso a detecção olha o AppId DO APP, não o do pacote — olhar o de fora
///     responderia "instalado" para uma máquina sem PayGo funcionando.
///
///  3. /SUPPRESSMSGBOXES TEM UM LADO RUIM. Sem ele, uma caixa de diálogo de erro trava
///     a instalação para sempre esperando um clique que ninguém vai dar (foi o que
///     aconteceu em 28/08). Com ele, o Inno responde "Abort" sozinho — e reverte EM
///     SILÊNCIO. Não existe escolha boa: por isso o silencioso aqui é sempre seguido
///     de conferência por evidência, e o fracasso cai no caminho visível, onde o dono
///     vê o assistente da Setis e a mensagem de erro de verdade.
/// </summary>
public static class PayGo
{
    /// <summary>O app de verdade (segundo estágio). É esta chave que responde
    /// "o PayGo está instalado nesta máquina".</summary>
    private const string ChaveApp =
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{CF467F95-F927-4498-BBC3-F9DD0AC476A0}_is1";

    /// <summary>Pasta de troca de arquivos com o PDV. Tem que ser a MESMA dos dois
    /// lados: aqui e na configuração do PayGo Windows.</summary>
    public const string PastaTroca = @"C:\PAYGO";

    /// <summary>Processos que o instalador do PayGo derruba no meio do caminho. Se um
    /// deles estiver de pé com uma venda no pinpad, a venda morre junto.</summary>
    public static readonly string[] ProcessosTef = { "PayGo", "PayGoLauncher", "ControlPay" };

    public sealed record Presenca(bool Instalado, string? Versao, string? Pasta);

    /// <summary>O que fazer com a etapa do PayGo. Decidido por
    /// <see cref="Decidir"/>, que é código puro justamente para poder ser testado —
    /// esta decisão acontece uma vez por máquina e não dá para ensaiar na loja.</summary>
    public enum Acao
    {
        /// <summary>Máquina limpa: instalar.</summary>
        Instalar,
        /// <summary>Já está aí. Reinstalar por cima só acumula desinstalador
        /// (unins000, unins001, unins002…) e arrisca derrubar um TEF que funciona.</summary>
        JaInstalado,
        /// <summary>Tem TEF de pé. O instalador do PayGo mata esses processos —
        /// com uma venda no pinpad, isso é dinheiro no meio do caminho.</summary>
        FecharTefPrimeiro,
        /// <summary>O arquivo do PayGo não veio junto. Não é erro fatal: o PDV
        /// instala e funciona; o TEF é que fica para depois.</summary>
        SemArquivo,
    }

    /// <summary>
    /// A decisão da etapa, sem tocar em nada. Ordem importa: TEF rodando vence
    /// "já instalado", porque mesmo quem só vai reinstalar precisa fechar antes.
    /// </summary>
    public static Acao Decidir(bool arquivoPresente, bool jaInstalado, bool tefRodando)
    {
        if (tefRodando) return Acao.FecharTefPrimeiro;
        if (jaInstalado) return Acao.JaInstalado;
        return arquivoPresente ? Acao.Instalar : Acao.SemArquivo;
    }

    /// <summary>O que o dono lê na tela para cada decisão. Fica junto da decisão de
    /// propósito: mensagem e regra que moram longe uma da outra sempre divergem.</summary>
    public static string Explicar(Acao a) => a switch
    {
        Acao.Instalar => "Vou instalar o PayGo agora. Leva alguns minutos e a tela pode piscar.",
        Acao.JaInstalado => "O PayGo já está instalado nesta máquina. Vou deixar como está.",
        Acao.FecharTefPrimeiro => "Feche o PayGo antes de continuar: a instalação dele fecha o programa "
                                + "à força, e se houver uma venda no pinpad ela se perde.",
        Acao.SemArquivo => "O PayGo não veio junto neste instalador. O caixa funciona; "
                         + "o cartão é que só depois de instalar o PayGo.",
        _ => "",
    };

    /// <summary>Está instalado? Olha o AppId DO APP (ver o comentário de cima).</summary>
    public static Presenca Detectar()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(ChaveApp);
            if (k is null) return new Presenca(false, null, null);
            var pasta = k.GetValue("InstallLocation") as string;
            // Chave sem o programa no disco é instalação revertida pela metade —
            // e para nós isso é "não instalado", senão pulamos a etapa que faltava.
            var temExe = !string.IsNullOrWhiteSpace(pasta)
                         && File.Exists(Path.Combine(pasta, "PayGo.exe"));
            return new Presenca(temExe, k.GetValue("DisplayVersion") as string, pasta);
        }
        catch { return new Presenca(false, null, null); }
    }

    public static bool TefRodando()
    {
        foreach (var nome in ProcessosTef)
        {
            try { if (Process.GetProcessesByName(nome).Length > 0) return true; }
            catch { /* sem permissão de enumerar: não dá para afirmar que está rodando */ }
        }
        return false;
    }

    /// <summary>
    /// As pastas por onde PDV e PayGo conversam. O PDV cria sozinho quando precisa,
    /// mas criar aqui, com o instalador já elevado, é o que garante que elas nasçam
    /// com permissão de escrita para a conta do caixa — que costuma ser limitada.
    /// ⚠️ Se já existirem, NÃO limpe: um arquivo em Req\ é uma transação pendente.
    /// </summary>
    public static void PrepararPastaTroca()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(PastaTroca, "Req"));
            Directory.CreateDirectory(Path.Combine(PastaTroca, "Resp"));
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "icacls",
                Arguments = $"\"{PastaTroca}\" /grant *S-1-5-32-545:(OI)(CI)M /T",
                UseShellExecute = false, CreateNoWindow = true,
            });
            p?.WaitForExit(30_000);
        }
        catch { /* o PDV ainda cria na 1ª cobrança; não é motivo para parar a instalação */ }
    }

    /// <summary>
    /// Abre o instalador do PayGo, com a cara dele mesmo, e devolve o controle.
    ///
    /// ⚠️ POR QUE NÃO SILENCIOSO — a pergunta óbvia, e a resposta é o item 3 lá de
    /// cima. Existe sim o modo `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`, e ele até
    /// funciona no caso feliz. Mas quando dá errado ele dá errado do pior jeito
    /// possível: o Inno responde "Abort" sozinho na caixa de erro, reverte, e a tela
    /// não mostra nada. ESTA MÁQUINA É A PROVA — em 28/08 o segundo estágio abortou
    /// num "Acesso negado" e reverteu; o pacote ficou registrado na versão nova e o
    /// PayGo.exe no disco continuou na antiga. Um instalador silencioso que mente
    /// sobre ter instalado é pior do que um que pede um clique.
    ///
    /// Então o desenho é o simples e honesto: o caixa instala primeiro, sozinho e
    /// inteiro; depois o assistente da Setis aparece e o dono segue. Se ele fechar no
    /// meio, o caixa continua instalado e funcionando — só o cartão fica para depois.
    /// </summary>
    public static string? Instalar(string exe)
    {
        if (!File.Exists(exe)) return "O arquivo do PayGo não veio junto com este instalador.";
        try
        {
            // UseShellExecute: o Inno pede a própria elevação quando precisa.
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
            PrepararPastaTroca();
            return null;
        }
        catch (Exception ex) { return "Não consegui abrir o instalador do PayGo: " + ex.Message; }
    }
}
