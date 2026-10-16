using System.Globalization;
using System.Text.RegularExpressions;

namespace Pdv.Nucleo;

/// <summary>
/// O RELÓGIO DO PC ACERTADO SOZINHO (1.0.41, 16/10/2026, pedido do dono: "coloca no PDV para sempre
/// sincronizar o PC com relógio atômico, automaticamente").
///
/// O PC da Savassi estava 566 s atrasado (medido por Atualizacao.DesvioDoRelogio): a SEFAZ
/// recusa nota com relógio torto, o chat lê "fala antiga" o que acabou de chegar, e o KDS mostra atraso que
/// não existe. O Windows já tem o serviço de hora (w32time); o que faltava era configurá-lo com servidores
/// de verdade (os do NTP.br, que são relógio atômico, e o pool.ntp.org de reserva), deixá-lo ligado e pedir
/// a sincronização toda hora e ao iniciar.
///
/// Quem faz isso é o INSTALADOR (roda como administrador) em toda instalação e atualização; o caixa só
/// mede o desvio, avisa na Configuração e, numa instalação antiga sem a tarefa, pede ao Windows (um aviso
/// do UAC) para registrar e sincronizar. Como na trava do Ctrl+Alt+Del: só programas do Windows, por
/// caminho absoluto de System32, com a linha montada só de constantes. Falha nunca aborta a instalação
/// nem derruba o caixa: só fica anotada.
///
/// Aqui mora só a regra pura: os servidores, as linhas de comando, a regra do aviso (2 leituras seguidas
/// com mais de 60 s) e os textos da tela. O processo de verdade fica no instalador e em Quiosque.cs.
/// </summary>
public static class RelogioDoWindows
{
    /// <summary>Os servidores de hora: dois do NTP.br (relógio atômico do Observatório Nacional) e o pool de reserva.</summary>
    public const string Servidores = "a.st1.ntp.br b.st1.ntp.br pool.ntp.org";

    /// <summary>A tarefa agendada que sincroniza toda hora.</summary>
    public const string NomeDaTarefa = "PdvNativo-Relogio";
    /// <summary>A tarefa irmã que sincroniza ao iniciar o Windows (o schtasks só aceita UM gatilho por /create).</summary>
    public const string NomeDaTarefaDeInicio = "PdvNativo-Relogio-Inicio";

    public const string ProgramaW32tm = "w32tm.exe";
    public const string ProgramaSc = "sc.exe";
    public const string ProgramaNet = "net.exe";
    public const string ProgramaSchtasks = "schtasks.exe";

    /// <summary>Acima disto o relógio está torto de verdade (não é ruído de rede).</summary>
    public static readonly TimeSpan Limiar = TimeSpan.FromSeconds(60);
    /// <summary>Quantas leituras seguidas acima do limiar antes de avisar (uma só pode ser a rede lenta).</summary>
    public const int LeiturasParaAvisar = 2;

    /// <summary>O que o Windows roda em cada etapa: o programa de System32 e os argumentos. Nunca lança.</summary>
    public sealed record Passo(string Nome, string Programa, string Argumentos);

    /// <summary>
    /// AS SEIS ETAPAS DA INSTALAÇÃO, na ordem: configurar os servidores, deixar o serviço automático,
    /// ligá-lo, registrar a tarefa de toda hora, a tarefa de início e sincronizar agora. Cada uma por
    /// caminho absoluto de System32 e só com constantes.
    /// </summary>
    public static IReadOnlyList<Passo> PassosDaInstalacao(string pastaDoSistema)
    {
        if (!TravaDoCtrlAltDel.PastaDoSistema(pastaDoSistema)) throw new ArgumentException("pasta do sistema fora do formato", nameof(pastaDoSistema));
        return new[]
        {
            new Passo("servidores", pastaDoSistema + @"\" + ProgramaW32tm, ArgumentosConfigurar),
            new Passo("servico_automatico", pastaDoSistema + @"\" + ProgramaSc, ArgumentosServicoAutomatico),
            new Passo("servico_ligado", pastaDoSistema + @"\" + ProgramaNet, ArgumentosLigarServico),
            new Passo("tarefa_hora", pastaDoSistema + @"\" + ProgramaSchtasks, ArgumentosTarefaDeHora),
            new Passo("tarefa_inicio", pastaDoSistema + @"\" + ProgramaSchtasks, ArgumentosTarefaDeInicio),
            new Passo("sincronizar", pastaDoSistema + @"\" + ProgramaW32tm, ArgumentosSincronizar),
        };
    }

    public const string ArgumentosConfigurar = "/config /manualpeerlist:\"" + Servidores + "\" /syncfromflags:manual /update";
    public const string ArgumentosServicoAutomatico = "config w32time start= auto";
    public const string ArgumentosLigarServico = "start w32time";
    public const string ArgumentosTarefaDeHora = "/create /f /ru SYSTEM /sc hourly /tn " + NomeDaTarefa + " /tr \"w32tm /resync /nowait\"";
    public const string ArgumentosTarefaDeInicio = "/create /f /ru SYSTEM /sc onstart /tn " + NomeDaTarefaDeInicio + " /tr \"w32tm /resync /nowait\"";
    public const string ArgumentosSincronizar = "/resync";
    /// <summary>A consulta que diz se a tarefa existe (código de saída 0). Roda sem administrador.</summary>
    public const string ArgumentosConsultarTarefa = "/query /tn " + NomeDaTarefa;

    /// <summary>A tarefa existe? O schtasks /query devolve 0 quando acha e 1 quando não.</summary>
    public static bool TarefaExiste(int codigoDeSaida) => codigoDeSaida == 0;

    /// <summary>"net start" devolve 2 quando o serviço JÁ estava ligado: não é falha.</summary>
    public static bool PassoDeu(Passo p, int codigoDeSaida)
        => codigoDeSaida == 0 || (p.Nome == "servico_ligado" && codigoDeSaida == 2);

    /// <summary>
    /// A LINHA DO CMD ELEVADO para o botão da Configuração (instalação antiga, sem a tarefa): as mesmas
    /// seis etapas, com <c>&amp;</c> (uma que falhe não para as outras), no mesmo molde da trava do
    /// Ctrl+Alt+Del: <c>/d /v:off /s /c "..."</c>. Só constantes e a pasta do sistema; fora do formato, lança.
    /// </summary>
    public static string MontarComandoDoCaixa(string pastaDoSistema)
    {
        var partes = PassosDaInstalacao(pastaDoSistema).Select(p => "\"" + p.Programa + "\" " + p.Argumentos);
        var comando = TravaDoCtrlAltDel.ChavesDoCmd + " \"" + string.Join(" & ", partes) + "\"";
        if (!ComandoLimpo(comando)) throw new InvalidOperationException("a linha do cmd saiu com caractere fora do esperado");
        return comando;
    }

    /// <summary>
    /// A linha só tem letra, número, espaço, \ : . " / _ - = e o &amp; que separa as etapas. Nada de %
    /// (variável), ^ (escape), |, &lt;, &gt; (redirecionamento), ! (expansão) nem quebra de linha.
    /// </summary>
    public static bool ComandoLimpo(string? comando)
        => comando is not null && Regex.IsMatch(comando, @"^[A-Za-z0-9 \\:.""/_&=\-]+\z", RegexOptions.CultureInvariant);

    // ── a regra do aviso ─────────────────────────────────────────────────────

    /// <summary>
    /// O VIGIA DO DESVIO: conta as leituras seguidas com |desvio| acima de 60 s. Uma leitura dentro do
    /// limiar zera; uma leitura nula (o painel não disse a hora) não conta nem zera. Avisa a partir da
    /// segunda seguida. Seguro entre threads.
    /// </summary>
    public sealed class VigiaDoDesvio
    {
        private readonly object _trava = new();
        private int _seguidas;
        private TimeSpan? _ultimo;

        /// <summary>Uma leitura. Devolve se a tela deve avisar agora.</summary>
        public bool Registrar(TimeSpan? desvio)
        {
            lock (_trava)
            {
                if (desvio is not { } d) return _seguidas >= LeiturasParaAvisar;
                _ultimo = d;
                _seguidas = d.Duration() > Limiar ? _seguidas + 1 : 0;
                return _seguidas >= LeiturasParaAvisar;
            }
        }

        public bool Avisar { get { lock (_trava) return _seguidas >= LeiturasParaAvisar; } }
        public TimeSpan? UltimoDesvio { get { lock (_trava) return _ultimo; } }
        public int Seguidas { get { lock (_trava) return _seguidas; } }

        /// <summary>Só a bateria.</summary>
        public void Zerar() { lock (_trava) { _seguidas = 0; _ultimo = null; } }
    }

    /// <summary>O desvio em segundos inteiros para o sinal (positivo = o PC adiantado), ou null.</summary>
    public static int? Segundos(TimeSpan? desvio)
        => desvio is { } d ? (int)Math.Clamp(Math.Round(d.TotalSeconds), -86_400 * 366, 86_400 * 366) : null;

    // ── textos de tela (curtos, sem travessão) ───────────────────────────────

    /// <summary>"Relógio do PC 9 min atrasado. O Windows acerta sozinho em até 1 h." (positivo = adiantado).</summary>
    public static string Frase(TimeSpan desvio)
    {
        var min = Math.Max(1, (int)Math.Round(desvio.Duration().TotalMinutes));
        var lado = desvio < TimeSpan.Zero ? "atrasado" : "adiantado";
        return "Relógio do PC " + min.ToString(CultureInfo.InvariantCulture) + " min " + lado + ". O Windows acerta sozinho em até 1 h.";
    }

    public const string TextoDoBotao = "Acertar o relógio agora";
    public const string FraseSemTarefa = "Este PC foi instalado antes do acerto automático. Toque no botão para ligar.";
    public const string FraseAcertou = "O acerto automático do relógio foi ligado.";
    public const string FraseRecusou = "Sem a permissão do Windows, o relógio não foi acertado.";
    public const string FraseFalhou = "Não consegui ligar o acerto do relógio.";
}
