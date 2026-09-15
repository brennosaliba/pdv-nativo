using System.Diagnostics;

namespace Pdv.Nucleo;

/// <summary>O que aconteceu ao tentar abrir uma porta serial.</summary>
public enum AberturaPorta { Abriu, NaoExiste, Ocupada, Falhou }

/// <summary>Uma porta serial aberta pelo teste do pinpad. Fecha no Dispose.</summary>
public interface IPortaAberta : IDisposable
{
    /// <summary>Manda um byte e espera outro até o prazo. true = o byte esperado chegou.</summary>
    bool EnviarEsperar(byte enviar, byte esperado, int prazoMs);
}

/// <summary>
/// O acesso às portas seriais da máquina. Existe como interface para a bateria simular
/// porta que não existe, porta ocupada, pinpad mudo e pinpad que responde, sem hardware.
/// A implementação de verdade é <see cref="SerialWindows"/>.
/// </summary>
public interface IAcessoSerial
{
    IReadOnlyList<PortaSerial> Listar();
    AberturaPorta Abrir(string com, out IPortaAberta? porta);
    /// <summary>Programas de maquininha abertos agora, com o nome para a tela. Vazio = nenhum reconhecido.</summary>
    IReadOnlyList<string> ProgramasDeMaquininhaAbertos();
}

public enum SituacaoPinpad
{
    /// <summary>O pinpad respondeu ao primeiro sinal do protocolo.</summary>
    Respondeu,
    /// <summary>A porta está aberta pela própria biblioteca deste caixa. Não é defeito.</summary>
    EmUsoPeloCaixa,
    /// <summary>Outro programa segura a porta.</summary>
    Ocupada,
    /// <summary>A porta abriu, mas ninguém respondeu.</summary>
    NaoRespondeu,
    /// <summary>A porta escolhida na Configuração não existe neste computador.</summary>
    PortaNaoExiste,
    /// <summary>Nenhuma porta serial que possa ser pinpad.</summary>
    NenhumPinpad,
    /// <summary>O teste passou do prazo total.</summary>
    PassouDoPrazo,
}

/// <summary>O resultado do teste do pinpad, com a frase de uma linha que a tela mostra.</summary>
/// <param name="Programa">O programa que segura a porta, quando dá para saber (só em <see cref="SituacaoPinpad.Ocupada"/>).</param>
public sealed record ResultadoTestePinpad(SituacaoPinpad Situacao, string Frase, PortaSerial? Porta = null, string? Programa = null)
{
    /// <summary>Pode seguir para a biblioteca?</summary>
    public bool Ok => Situacao is SituacaoPinpad.Respondeu or SituacaoPinpad.EmUsoPeloCaixa;

    /// <summary>O número da porta ("3"), quando o teste sabe qual é.</summary>
    public string? Numero => Porta?.Numero is { Length: > 0 } n ? n : null;
}

/// <summary>
/// TESTAR O PINPAD ANTES DE CHAMAR A BIBLIOTECA.
///
/// O QUE ACONTECEU (14/09/2026, loja Castelo). O dono tocou em "Instalar ponto de captura"
/// e o caixa ficou mais de cinco minutos preso, duas vezes, até ele matar o programa. O log
/// da biblioteca mostra por quê: ela abria a única porta serial da máquina, mandava o
/// primeiro sinal do protocolo do pinpad (CAN, 18h) e esperava a resposta (EOT, 04h) por
/// 20 segundos, de novo e de novo, dentro de UMA chamada que não volta. O pinpad nunca
/// respondeu. Pedido do dono no mesmo dia: "add opção de teste do pinpad" e "SAAS não
/// posso ficar dependendo disso".
///
/// Este teste faz exatamente o primeiro passo que a biblioteca faz, com prazo curto e fora
/// da tela: abre a porta, manda CAN, espera EOT e fecha. Pinpad bom responde em dezenas de
/// milissegundos (medido: 37 ms num Gertec PPC-930). Se não responder aqui, não adianta
/// chamar a biblioteca.
///
/// ⚠️ Nunca abrir a porta com uma operação da biblioteca em voo: quem chama confere antes.
/// </summary>
public static class TestePinpad
{
    public const byte Can = 0x18;
    public const byte Eot = 0x04;

    /// <summary>Teto do teste inteiro, listagem incluída. O dono pediu até 10 s.</summary>
    public const int PrazoTotalMs = 10_000;

    /// <summary>Quanto esperar o EOT a cada CAN.</summary>
    public const int PrazoRespostaMs = 1_500;

    public const string FraseNenhum = "Nenhum pinpad ligado neste computador. Ligue o cabo USB do pinpad e toque em Testar de novo.";
    public const string FrasePrazo = "O teste passou de 10 segundos sem resposta. Tire o cabo USB do pinpad, espere 10 segundos e ligue de novo.";

    /// <summary>Portas que nunca recebem o sinal: abrir porta Bluetooth tenta conectar e demora; impressora e modem não são pinpad.</summary>
    private static readonly string[] NuncaTestar = { "bluetooth", "modem", "impressora", "printer", "fax" };

    public static bool PodeTestar(PortaSerial p)
    {
        var d = (p.Descricao ?? "").ToLowerInvariant();
        return !NuncaTestar.Any(n => d.Contains(n, StringComparison.Ordinal));
    }

    /// <summary>O nome que o Windows dá à porta serial da própria placa-mãe (em português e em inglês).</summary>
    private static readonly string[] NomesDaPortaDaPlaca = { "porta de comunicação", "porta de comunicacao", "communications port" };

    /// <summary>
    /// A porta é a serial da placa-mãe? Ela ainda é testada (pinpad de cabo serial existe), mas
    /// calada ela não é "o pinpad": 14/09/2026, prova com o hardware desta máquina, Gertec
    /// desligado e só a COM1 da placa, e a frase dizia "O pinpad está na COM1 mas não respondeu".
    /// </summary>
    public static bool EhPortaDaPlaca(PortaSerial p)
    {
        var d = (p.Descricao ?? "").ToLowerInvariant();
        return NomesDaPortaDaPlaca.Any(n => d.Contains(n, StringComparison.Ordinal));
    }

    /// <summary>
    /// As portas que o teste vai tentar, na ordem. Porta escolhida na Configuração: só ela.
    /// Automática: primeiro as que parecem pinpad pelo nome, depois as outras que podem ser.
    /// </summary>
    public static IReadOnlyList<PortaSerial> Candidatas(IReadOnlyList<PortaSerial>? portas, string? configurada)
    {
        var lista = (portas ?? Array.Empty<PortaSerial>()).Where(p => p is not null && p.Numero.Length > 0).ToList();
        var alvo = PortaDoPinpad.Normalizar(configurada);
        if (alvo != PortaDoPinpad.Automatica) return lista.Where(p => p.Numero == alvo).Take(1).ToList();
        return lista.Where(p => PortaDoPinpad.PareceDePinpad(p.Descricao))
            .Concat(lista.Where(p => !PortaDoPinpad.PareceDePinpad(p.Descricao) && PodeTestar(p)))
            .ToList();
    }

    public static string FraseRespondeu(PortaSerial p)
    {
        var d = (p.Descricao ?? "").Trim();
        if (d.Length == 0) return $"O pinpad respondeu na {p.Com}.";
        return PortaDoPinpad.PareceDePinpad(d) && d.ToLowerInvariant() is var l && (l.Contains("pinpad") || l.Contains("pin pad"))
            ? $"{d} respondeu na {p.Com}."
            : $"Pinpad {d} respondeu na {p.Com}.";
    }

    public static string FraseOcupada(PortaSerial p, string? programa)
        => programa is { Length: > 0 }
            ? $"A {p.Com} está ocupada pelo programa {programa}. Feche o {programa} e toque em Testar de novo."
            : $"A {p.Com} está ocupada por outro programa. Feche o programa da Gertec e o PayGo Windows se estiverem abertos e toque em Testar de novo.";

    public static string FraseMudo(PortaSerial p)
        => $"O pinpad está na {p.Com} mas não respondeu. Tire o cabo USB, espere 10 segundos e ligue de novo.";

    /// <summary>
    /// O teste, síncrono. Quem está na tela chama <see cref="TestarAsync"/>, que roda isto fora
    /// da tela e corta no prazo mesmo que um driver não respeite o tempo da porta.
    /// </summary>
    /// <param name="bibliotecaNoCaixa">A PGWebLib já está carregada neste processo: porta ocupada sem outro programa é ela.</param>
    public static ResultadoTestePinpad Testar(IAcessoSerial acesso, string? configurada, bool bibliotecaNoCaixa,
        int prazoRespostaMs = PrazoRespostaMs, int prazoTotalMs = PrazoTotalMs)
    {
        var relogio = Stopwatch.StartNew();
        IReadOnlyList<PortaSerial> portas;
        try { portas = acesso.Listar() ?? Array.Empty<PortaSerial>(); }
        catch { portas = Array.Empty<PortaSerial>(); }

        var alvo = PortaDoPinpad.Normalizar(configurada);
        var especifica = alvo != PortaDoPinpad.Automatica;
        if (especifica && !portas.Any(p => p.Numero == alvo))
            return portas.Count == 0
                ? new(SituacaoPinpad.NenhumPinpad, FraseNenhum)
                : new(SituacaoPinpad.PortaNaoExiste,
                    $"A COM{alvo} não existe neste computador. Portas encontradas: {string.Join(", ", portas.Select(p => p.Com))}. Deixe a porta em Automática e toque em Testar de novo.");

        var candidatas = Candidatas(portas, configurada);
        if (candidatas.Count == 0) return new(SituacaoPinpad.NenhumPinpad, FraseNenhum);

        var ocupadas = new List<PortaSerial>();
        var mudas = new List<PortaSerial>();
        var estourou = false;
        foreach (var p in candidatas)
        {
            if (relogio.ElapsedMilliseconds >= prazoTotalMs) { estourou = true; break; }
            AberturaPorta ab;
            IPortaAberta? aberta = null;
            try { ab = acesso.Abrir(p.Com, out aberta); }
            catch { ab = AberturaPorta.Falhou; }
            if (ab == AberturaPorta.Ocupada) { aberta?.Dispose(); ocupadas.Add(p); continue; }
            if (ab != AberturaPorta.Abriu || aberta is null) { aberta?.Dispose(); continue; }

            var respondeu = false;
            try
            {
                // Porta que parece pinpad (ou a escolhida) ganha duas chances, como a biblioteca faz.
                var tentativas = especifica || PortaDoPinpad.PareceDePinpad(p.Descricao) ? 2 : 1;
                for (var i = 0; i < tentativas && !respondeu; i++)
                {
                    var prazo = (int)Math.Min(prazoRespostaMs, prazoTotalMs - relogio.ElapsedMilliseconds);
                    if (prazo <= 0) { estourou = true; break; }
                    respondeu = aberta.EnviarEsperar(Can, Eot, prazo);
                }
            }
            catch { respondeu = false; }
            finally { try { aberta.Dispose(); } catch { /* fechar é melhor esforço */ } }

            if (respondeu) return new(SituacaoPinpad.Respondeu, FraseRespondeu(p), p);
            mudas.Add(p);
        }

        bool Pinpad(PortaSerial p) => especifica || PortaDoPinpad.PareceDePinpad(p.Descricao);

        ResultadoTestePinpad Ocupada(PortaSerial p)
        {
            IReadOnlyList<string> programas;
            try { programas = acesso.ProgramasDeMaquininhaAbertos() ?? Array.Empty<string>(); }
            catch { programas = Array.Empty<string>(); }
            if (programas.Count == 0 && bibliotecaNoCaixa)
                return new(SituacaoPinpad.EmUsoPeloCaixa, $"A {p.Com} já está aberta pela maquininha deste caixa. Pode seguir.", p);
            var um = programas.Count == 1 ? programas[0] : null;
            return new(SituacaoPinpad.Ocupada, FraseOcupada(p, um), p, um);
        }

        // A ordem é a do que mais ajuda: porta que parece pinpad antes de porta genérica.
        if (ocupadas.FirstOrDefault(Pinpad) is { } op) return Ocupada(op);
        if (mudas.FirstOrDefault(Pinpad) is { } mp) return new(SituacaoPinpad.NaoRespondeu, FraseMudo(mp), mp);
        if (ocupadas.Count > 0) return Ocupada(ocupadas[0]);
        // A porta da placa-mãe calada não vira "o pinpad está na COMx" (ver EhPortaDaPlaca).
        var mudasForaDaPlaca = mudas.Where(p => !EhPortaDaPlaca(p)).ToList();
        if (mudasForaDaPlaca.Count == 1) return new(SituacaoPinpad.NaoRespondeu, FraseMudo(mudasForaDaPlaca[0]), mudasForaDaPlaca[0]);
        if (mudasForaDaPlaca.Count > 1)
            return new(SituacaoPinpad.NaoRespondeu,
                $"Nenhuma porta respondeu como pinpad ({string.Join(", ", mudasForaDaPlaca.Select(p => p.Com))}). Tire o cabo USB do pinpad, espere 10 segundos e ligue de novo.");
        if (mudas.Count > 0) return new(SituacaoPinpad.NenhumPinpad, FraseNenhum);
        if (estourou) return new(SituacaoPinpad.PassouDoPrazo, FrasePrazo);
        return new(SituacaoPinpad.NenhumPinpad, FraseNenhum);
    }

    /// <summary>
    /// O teste fora da tela, com teto. Um driver que ignora o prazo da porta deixa a thread do
    /// teste para trás, mas a tela recebe a resposta no prazo do mesmo jeito.
    /// </summary>
    public static async Task<ResultadoTestePinpad> TestarAsync(IAcessoSerial acesso, string? configurada, bool bibliotecaNoCaixa,
        CancellationToken ct = default, int prazoRespostaMs = PrazoRespostaMs, int prazoTotalMs = PrazoTotalMs)
    {
        var trabalho = Task.Run(() => Testar(acesso, configurada, bibliotecaNoCaixa, prazoRespostaMs, prazoTotalMs), CancellationToken.None);
        try
        {
            return await trabalho.WaitAsync(TimeSpan.FromMilliseconds(prazoTotalMs + 1_000), ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { return new(SituacaoPinpad.PassouDoPrazo, FrasePrazo); }
        catch (OperationCanceledException) { return new(SituacaoPinpad.PassouDoPrazo, "Teste do pinpad cancelado."); }
    }
}

/// <summary>
/// Os programas de maquininha que costumam segurar a porta do pinpad. A lista é por trecho
/// do nome do processo, em minúsculas. <see cref="PodeFechar"/> diz se o caixa oferece fechar
/// o programa (sempre com confirmação): serviço de TEF de outra empresa não se derruba daqui.
/// </summary>
public static class ProgramasDeMaquininha
{
    private static readonly (string Trecho, string Nome, bool PodeFechar)[] Conhecidos =
    {
        ("paygolauncher", "PayGo Windows", true),
        ("paygo", "PayGo Windows", true),
        ("controlpay", "ControlPay", true),
        ("serialdevicemanager", "Gertec", true),
        ("gertec", "Gertec", true),
        ("clisitef", "SiTef", false),
        ("sitef", "SiTef", false),
        ("linx", "Linx", false),
    };

    /// <summary>O nome para a tela, ou null quando o processo não é programa de maquininha.</summary>
    public static string? Reconhecer(string? processo)
    {
        var p = (processo ?? "").Trim().ToLowerInvariant();
        if (p.Length == 0) return null;
        foreach (var (trecho, nome, _) in Conhecidos)
            if (p.Contains(trecho, StringComparison.Ordinal)) return nome;
        return null;
    }

    public static bool PodeFechar(string? nome)
        => Conhecidos.Any(c => c.PodeFechar && string.Equals(c.Nome, nome, StringComparison.Ordinal));

    /// <summary>Os nomes (sem repetir) dos programas reconhecidos numa lista de processos.</summary>
    public static IReadOnlyList<string> Nomes(IEnumerable<string> processos)
        => processos.Select(Reconhecer).Where(n => n is not null).Select(n => n!).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Os programas de maquininha abertos agora nesta máquina (fora o próprio caixa).</summary>
    public static IReadOnlyList<string> AbertosAgora()
    {
        var eu = Environment.ProcessId;
        var nomes = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.Id != eu) nomes.Add(p.ProcessName); }
            catch { /* processo que sumiu ou que não deixa ler */ }
            finally { p.Dispose(); }
        }
        return Nomes(nomes);
    }

    /// <summary>
    /// Fecha o programa pelo nome da tela: primeiro pede para a janela fechar, depois encerra.
    /// Null = fechou; senão a frase para a tela. Só chamar depois de a pessoa confirmar.
    /// </summary>
    public static string? Fechar(string nome)
    {
        if (!PodeFechar(nome)) return $"O caixa não fecha o {nome}. Feche pelo Gerenciador de Tarefas.";
        var falhou = false;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId || Reconhecer(p.ProcessName) != nome) continue;
                if (p.CloseMainWindow() && p.WaitForExit(3_000)) continue;
                p.Kill(entireProcessTree: true);
                if (!p.WaitForExit(3_000)) falhou = true;
            }
            catch { falhou = true; }
            finally { p.Dispose(); }
        }
        return falhou ? $"Não consegui fechar o {nome}. Feche pelo Gerenciador de Tarefas e toque em Testar de novo." : null;
    }
}
