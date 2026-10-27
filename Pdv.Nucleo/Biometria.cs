using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

// ════════════════════════════════════════════════════════════════════════════
//  A DIGITAL NO LUGAR DA SENHA (1.0.52, 26/10/2026, pedido do dono)
//
//  O dono comprou um leitor HID DigitalPersona U.are.U 4500. Na ENTRADA do
//  operador (que e o que abre o turno) e no FECHAMENTO do caixa, o caixa passa a
//  pedir a digital, ligada ao CPF do funcionario, em vez da senha.
//
//  Como funciona, em quatro linhas:
//   · a digital vive SO neste PC, dentro do Windows (Windows Biometric Framework,
//     o mesmo cofre do Windows Hello). O caixa nunca ve a imagem nem o molde: o
//     Windows devolve uma identidade (quem) e um dedo (qual), e e isso que o
//     caixa guarda, ligado ao CPF, na tabela `digital` do SQLite, com o CPF
//     cifrado pelo DPAPI da maquina (so este Windows abre);
//   · primeira vez de cada funcionario neste caixa: ele entra como hoje (CPF e
//     senha), o caixa colhe a digital ali mesmo e, dali em diante, so digital;
//   · sem leitor, sem driver, ou tres leituras sem reconhecer: a RESERVA, que e
//     o codigo do autenticador do gerente ou do dono (pdv_autorizacao_totp, tipo
//     'turno_sem_digital', nivel gerente: SQL 224) mais a escolha do funcionario
//     na lista, tudo auditado com "pela reserva: motivo";
//   · o ERP so fica sabendo QUE aquele CPF tem digital naquele terminal (SQL
//     224, pdv_biometria_terminal): a digital em si nao sai do PC.
//
//  A regra mora aqui, no Nucleo, e nao no code-behind das telas, pelo mesmo
//  motivo da autorizacao do estorno: e uma maquina de estados com dinheiro no
//  fim, e a suite precisa rodar todos os caminhos sem leitor e sem janela. O
//  leitor entra por ILeitorDigital (LeitorWinBio e o de verdade; a suite usa um
//  de mentira) e a tela por ITelaDigital.
//
//  SO DIGITAL (1.0.54, 27/10/2026, decisao do dono): o caixa NAO usa mais senha
//  de operador para entrar nem para fechar o turno. Tres caminhos, e so eles:
//   · DIGITAL: o leitor reconhece (entrada e fechamento);
//   · RESERVA: problema no leitor (ausente, sem driver, 3 leituras sem
//     reconhecer, dedo machucado) = o FUNCIONARIO digita o proprio CPF (confere
//     contra os operadores ativos da loja; sem pedir codigo se nao conferir) e o
//     gerente geral ou o dono autoriza com o codigo ('turno_sem_digital'), na
//     abertura e no fechamento. 1.0.55: a biometria e OBRIGATORIA (o painel
//     nao desliga mais); o que pedia o PIN do gerente pede a digital do
//     gerente ou o codigo (PortaoGerente.cs);
//   · PRIMEIRO CADASTRO: quem ainda nao tem digital neste caixa. O mesmo
//     comeco: o funcionario digita o CPF, o gerente geral ou o dono autoriza com
//     o codigo ('digital_cadastro', nivel gerente, SQL 225) e o caixa le o dedo
//     3 vezes. Dali em diante, so digital.
//  Auditoria: hash do CPF, nome, "pela reserva: motivo" ou "primeiro cadastro",
//  e quem autorizou com o nivel do codigo (DesfechoDigital.MotivoAuditado).
//  A tela de CPF e senha e o PIN legado sairam do caixa (os dados ficam). O
//  usuario MASTER (admin da rede) nao e operador de turno: continua abrindo a
//  Configuracao pela senha dele (UsuarioMaster.Conferir), como antes.
//  Regra: PortaoDigital.EntrarAsync e PortaoDigital.PrimeiroCadastroAsync.
//
//  Testes: Pdv.Testes/TestesBiometria.cs (N. so digital)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>O que a sonda do leitor devolve antes de qualquer leitura.</summary>
public enum EstadoLeitor
{
    /// <summary>Ha pelo menos uma unidade de impressao digital no Windows.</summary>
    Pronto,
    /// <summary>O Windows nao enumera nenhum leitor (nem plugado, nem driver).</summary>
    SemLeitor,
    /// <summary>O leitor esta plugado, mas sem o driver WBF: o Windows nao o enxerga como biometria.</summary>
    SemDriver,
    /// <summary>O servico de biometria do Windows nao respondeu (WbioSrvc parado, politica, erro).</summary>
    SemServico,
}

/// <summary>A sonda: o estado, uma linha para a tela e as unidades que o Windows listou.</summary>
public sealed record SondaLeitor(EstadoLeitor Estado, string Detalhe, IReadOnlyList<string> Unidades)
{
    public bool Pronto => Estado == EstadoLeitor.Pronto;
}

public enum ResultadoCaptura { Reconhecido, NaoReconhecido, Cancelado, Falha, SemLeitor }

/// <summary>Uma leitura de identificacao. <see cref="Chave"/> so vem no Reconhecido.</summary>
public sealed record Captura(ResultadoCaptura Resultado, string? Chave = null, string? Motivo = null)
{
    public static Captura Reconhecida(string chave) => new(ResultadoCaptura.Reconhecido, chave);
    public static readonly Captura NaoReconhecida = new(ResultadoCaptura.NaoReconhecido);
    public static readonly Captura Cancelada = new(ResultadoCaptura.Cancelado);
    public static Captura Falhou(string motivo) => new(ResultadoCaptura.Falha, null, motivo);
}

public enum ResultadoCadastro { Ok, Cancelado, Falha, SemLeitor }

/// <summary>Um cadastro (varias leituras do mesmo dedo). <see cref="Chave"/> so no Ok.</summary>
public sealed record Cadastro(ResultadoCadastro Resultado, string? Chave = null, int Amostras = 0, string? Motivo = null)
{
    public static Cadastro Feito(string chave, int amostras) => new(ResultadoCadastro.Ok, chave, amostras);
    public static readonly Cadastro Cancelado = new(ResultadoCadastro.Cancelado);
    public static Cadastro Falhou(string motivo) => new(ResultadoCadastro.Falha, null, 0, motivo);

    /// <summary>"winbio" (o molde fica no Windows) ou "hid" (o molde vem em <see cref="Molde"/> e vai para a guarda). 1.0.53.</summary>
    public string Motor { get; init; } = "winbio";

    /// <summary>So no motor hid: o molde (FMD ISO) que a guarda cifra e usa para identificar.</summary>
    public byte[]? Molde { get; init; }

    /// <summary>A chave de uma digital JA guardada que bate com este dedo (o mesmo dedo em outra pessoa). 1.0.53.</summary>
    public string? ChaveParecida { get; init; }
}

/// <summary>
/// O leitor. As implementacoes reais sao <see cref="LeitorWinBio"/> (winbio.dll, driver WBF)
/// e <see cref="LeitorHidUareU"/> (runtime da HID, U.are.U RTE), escolhidas a cada sonda por
/// <see cref="LeitorAutomatico"/> (1.0.53). A maquina de estados e as telas nao mudam.
/// </summary>
public interface ILeitorDigital
{
    /// <summary>Nome curto para o diagnostico ("WinBio", "falso").</summary>
    string Nome { get; }

    /// <summary>Ha leitor? Nunca lanca: sem leitor e estado, nao excecao.</summary>
    Task<SondaLeitor> SondarAsync(CancellationToken ct);

    /// <summary>Espera um dedo e diz de quem e (a chave que o cadastro devolveu). Cancelar pelo token.</summary>
    Task<Captura> IdentificarAsync(CancellationToken ct);

    /// <summary>
    /// Cadastra um dedo na <paramref name="vaga"/> (1..10; o Windows guarda uma digital por
    /// dedo da conta, e cada funcionario ocupa um "dedo"). <paramref name="amostra"/> recebe
    /// 1, 2, 3... a cada leitura aceita.
    /// </summary>
    Task<Cadastro> CadastrarAsync(int vaga, IProgress<int>? amostra, CancellationToken ct);

    /// <summary>Apaga a digital daquela chave no Windows. false = nao estava la (ja serve).</summary>
    Task<bool> ApagarAsync(string chave, CancellationToken ct);
}

/// <summary>O que o operador pode tocar enquanto o leitor espera o dedo.</summary>
public enum EscolhaNaLeitura { Nenhuma, PrimeiraVez, SemDigital, Desistiu }

/// <summary>O que a tela devolve de uma leitura: o que o leitor disse, ou o botao que o operador tocou.</summary>
public sealed record LeituraNaTela(Captura? Captura, EscolhaNaLeitura Escolha = EscolhaNaLeitura.Nenhuma);

/// <summary>
/// A tela. Tudo que precisa de janela sai por aqui, para a maquina de estados rodar
/// na suite sem WPF. A implementacao real e Telas/TelaDigital.cs.
/// </summary>
public interface ITelaDigital
{
    /// <summary>
    /// Mostra "<paramref name="titulo"/>" (uma linha: "Encoste o dedo no leitor") enquanto
    /// <paramref name="ler"/> roda; <paramref name="aviso"/> e a linha vermelha entre
    /// tentativas. Os botoes cancelam o token de <paramref name="ler"/> e marcam a escolha.
    /// <paramref name="ofertaPrimeiraVez"/> mostra o botao "Primeira vez" (so na entrada).
    /// </summary>
    Task<LeituraNaTela> LerAsync(string titulo, string? aviso, bool ofertaPrimeiraVez,
        Func<CancellationToken, Task<Captura>> ler);

    /// <summary>
    /// Mostra "Primeira vez: encoste o dedo 3 vezes" enquanto <paramref name="cadastrar"/>
    /// roda; o progresso recebe "Leitura 1 de 3"... O unico botao e Cancelar.
    /// </summary>
    Task<Cadastro> CadastrarAsync(string titulo, Func<IProgress<string>, CancellationToken, Task<Cadastro>> cadastrar);

    /// <summary>
    /// O funcionario digita o proprio CPF (campo com mascara 000.000.000-00), na reserva e no
    /// primeiro cadastro (ajuste do dono, 27/10). <paramref name="aviso"/> e a linha vermelha da
    /// tentativa anterior. Devolve os digitos, ou null = desistiu.
    /// </summary>
    Task<string?> PedirCpfAsync(string titulo, string? aviso);

    /// <summary>Uma caixa de aviso de uma linha.</summary>
    void Avisar(string titulo, string texto);
}

/// <summary>Por onde a pessoa passou. Vira a coluna `via` do turno e a linha de auditoria.</summary>
public enum ViaDigital
{
    /// <summary>O leitor reconheceu a digital.</summary>
    Digital,
    /// <summary>O funcionario digita o CPF e o gerente geral ou o dono autoriza com o codigo.</summary>
    Reserva,
    /// <summary>Ninguem entrou: desistiu, codigo recusado, ou pediu a primeira vez (ver <see cref="DesfechoDigital.PrimeiraVez"/>).</summary>
    Cancelado,
}

/// <summary>
/// O desfecho do portao. <see cref="Operador"/> e <see cref="Cpf"/> so vem quando a Via e
/// Digital ou Reserva. <see cref="Motivo"/> e o que vai para a auditoria ("pela reserva: sem
/// leitor"). <see cref="Autorizador"/> e <see cref="Registro"/> sao a trilha do TOTP.
/// </summary>
public sealed record DesfechoDigital(ViaDigital Via, Operador? Operador, string? Cpf, string Motivo,
    string? Autorizador = null, string? Registro = null)
{
    public bool Entrou => Via != ViaDigital.Cancelado && Operador is not null;

    /// <summary>O operador tocou em "Primeira vez": o portao segue para o primeiro cadastro (codigo do gerente, 1.0.54).</summary>
    public bool PrimeiraVez { get; init; }

    /// <summary>Entrou pelo primeiro cadastro (codigo do gerente + 3 leituras). Via = Digital. 1.0.54.</summary>
    public bool PrimeiroCadastro { get; init; }

    /// <summary>A tela ja explicou por que nao seguiu (ele cancelou): nao avisar de novo.</summary>
    public bool Avisado { get; init; }

    /// <summary>"digital" ou "reserva": o que vai na coluna do turno e na nuvem.</summary>
    public string ViaTexto => Via == ViaDigital.Digital ? Biometria.ViaDigitalTexto : Biometria.ViaReservaTexto;

    /// <summary>O hash do CPF para o turno e a nuvem (null sem CPF).</summary>
    public string? CpfHash => Cpf is null ? null : Digitais.CpfHash(Cpf);

    /// <summary>O nivel do codigo que autorizou ("gerente" = gerente geral ou dono; "dono"). Null sem codigo.</summary>
    public string? NivelCodigo { get; init; }

    /// <summary>
    /// O motivo com quem autorizou (vai no via_motivo do turno e na auditoria):
    /// "pela reserva: sem leitor · autorizado por Marcos (codigo nivel gerente)".
    /// </summary>
    public string MotivoAuditado => Autorizador is null || (Via != ViaDigital.Reserva && !PrimeiroCadastro)
        ? Motivo
        : NivelCodigo == NivelEmergencia
            ? $"{Motivo} · autorizado por codigo de emergencia sem internet (registro {(Registro is { Length: > 8 } r ? r[..8] : Registro)})"
            : $"{Motivo} · autorizado por {Autorizador} (codigo nivel {NivelCodigo ?? "gerente"})";

    /// <summary>1.0.56: o NivelCodigo de quem entrou pelo codigo de emergencia (sem internet).</summary>
    public const string NivelEmergencia = "emergencia";

    /// <summary>A linha da auditoria da entrada: nome, hash curto do CPF e o motivo auditado.</summary>
    public string LinhaDeAuditoria => Operador is null ? Motivo
        : Via == ViaDigital.Digital && !PrimeiroCadastro ? $"{Operador.Nome} (digital)"
        : $"{Operador.Nome} · cpf {(CpfHash is null ? "?" : CpfHash[..12])} · {MotivoAuditado}";
}

/// <summary>Uma linha da tabela local `digital`.</summary>
public sealed record DigitalGuardada(string Chave, string Cpf, string CpfHash, string? OperadorId, string Nome,
    DateTime CadastradaEm, string? CadastradaPor, int Vaga, bool Registrada)
{
    /// <summary>"winbio" ou "hid" (coluna motor, 1.0.53).</summary>
    public string Motor { get; init; } = "winbio";
}

/// <summary>Configuracao e constantes da biometria.</summary>
public static class Biometria
{
    /// <summary>
    /// A chave de ligar/desligar por loja, copiada do painel (pdv_loja_config.biometria_turno,
    /// SQL 224). NASCE LIGADA (pedido do dono): sem a chave, vale ligada; o cartao do painel
    /// serve para desligar.
    /// </summary>
    public const string ChaveLigada = "biometria_turno";

    /// <summary>Quando o painel mandou a chave pela ultima vez: prova de que o SQL 224 esta no ar.</summary>
    public const string ChavePainelEm = "biometria_painel_em";

    public const string ViaDigitalTexto = "digital";
    public const string ViaReservaTexto = "reserva";

    /// <summary>Quantas leituras o cadastro pede, no minimo (o Windows pode pedir mais).</summary>
    public const int LeiturasDoCadastro = 3;

    /// <summary>Quantas leituras sem reconhecer antes de cair na reserva.</summary>
    public const int MaxTentativas = 3;

    /// <summary>Quantos dedos uma conta do Windows guarda: e o teto de funcionarios com digital por caixa.</summary>
    public const int Vagas = 10;

    // Os textos de uma linha que as telas mostram. Sem travessao (regra do dono).
    public const string TituloEntrada = "Encoste o dedo no leitor";
    public const string TituloFechamento = "Fechamento: encoste o dedo no leitor";
    public const string TituloPrimeiraVez = "Primeira vez: encoste o dedo 3 vezes";
    public const string TituloRecadastro = "Recadastro: encoste o dedo 3 vezes";
    public const string AvisoSemLeitor = "Sem leitor de digital. Entrada pela reserva: codigo do gerente ou do dono.";
    /// <summary>A frase do dono (26/10, 1.0.53) quando nem o Windows nem o runtime da HID acham o leitor plugado.</summary>
    public const string AvisoLeitorNaoEncontrado = "Leitor não encontrado. Instale o driver WBF da HID ou o U.are.U RTE.";
    public const string AvisoSemDriver = AvisoLeitorNaoEncontrado + " Entrada pela reserva.";
    public const string AvisoSemServico = "O servico de biometria do Windows nao respondeu. Entrada pela reserva.";
    public const string AvisoDigitalDesconhecida = "Essa digital nao esta cadastrada neste caixa.";
    public const string AvisoJaTemDigital = "Voce ja tem digital neste caixa. Encoste o dedo no leitor.";
    public const string AvisoCadastroCancelado = "Cadastro da digital cancelado. Entre de novo.";
    public const string AvisoVagasEsgotadas = "Este caixa ja tem 10 digitais cadastradas. Apague uma na Configuracao.";

    // 1.0.54 (27/10): so digital. Uma linha cada, sem travessao, sem a palavra senha.
    /// <summary>O botao da leitura que leva ao primeiro cadastro.</summary>
    public const string BotaoPrimeiraVez = "Primeira vez: código do gerente";
    /// <summary>O botao da leitura que leva a reserva.</summary>
    public const string BotaoSemDigital = "Sem digital: código do gerente";
    /// <summary>Os titulos do campo de CPF (ajuste do dono, 27/10: o funcionario digita o proprio CPF).</summary>
    public const string TituloCpfEntrada = "Digite seu CPF";
    public const string TituloCpfFechamento = "Fechamento: digite seu CPF";
    public const string TituloCpfCadastro = "Primeira vez: digite seu CPF";
    public const string AvisoCpfInvalido = "Esse CPF não confere. Confira os números.";
    public const string AvisoCpfNaoEhFuncionario = "Esse CPF não é de funcionário ativo desta loja.";
    /// <summary>O motivo de auditoria do primeiro cadastro (quem autorizou vai em MotivoAuditado).</summary>
    public const string MotivoPrimeiroCadastro = "primeiro cadastro";

    /// <summary>A mascara do campo de CPF enquanto digita: "529.982.2", "529.982.247-25"; vazio = "___.___.___-__".</summary>
    public static string MascaraCpf(string? digitos)
    {
        var d = new string((digitos ?? "").Where(char.IsDigit).Take(11).ToArray());
        if (d.Length == 0) return "___.___.___-__";
        var s = d;
        if (s.Length > 3) s = s.Insert(3, ".");
        if (s.Length > 7) s = s.Insert(7, ".");
        if (s.Length > 11) s = s.Insert(11, "-");
        return s;
    }

    /// <summary>O mesmo dedo ja esta guardado em outra pessoa (motor hid, 1.0.53).</summary>
    public static string AvisoDigitalDeOutro(string nome) => $"Essa digital já está cadastrada para {nome}.";

    // 1.0.55 (27/10, decisao do dono): BIOMETRIA OBRIGATORIA. Nao ha mais "desligada": o caixa nao
    // le a chave biometria_turno (o painel deixou de ter o botao). Entrada e fechamento sempre pela
    // digital, ou pela reserva (CPF do funcionario + codigo do gerente geral ou do dono).

    /// <summary>O painel ja mandou a chave (= o SQL 224 esta no ar e as colunas do turno existem na nuvem).</summary>
    public static bool PainelConhece(SqliteConnection cx) => Vendas.Config(cx, ChavePainelEm) is not null;

    /// <summary>A frase da reserva entre tentativas: "Nao reconheci (2 de 3). Tente de novo."</summary>
    public static string AvisoNaoReconheci(int tentativa) => $"Nao reconheci ({tentativa} de {MaxTentativas}). Tente de novo.";

    /// <summary>A linha do progresso do cadastro: "Leitura 2 de 3" (passando de 3, "Mais uma vez (4)").</summary>
    public static string ProgressoCadastro(int amostra) => amostra <= LeiturasDoCadastro
        ? $"Leitura {amostra} de {LeiturasDoCadastro}"
        : $"Mais uma vez ({amostra})";

    /// <summary>O motivo de auditoria da reserva: "pela reserva: sem leitor".</summary>
    public static string MotivoReserva(string causa) => "pela reserva: " + causa;

    /// <summary>O aviso de uma linha para cada estado do leitor que nao e Pronto.</summary>
    public static string AvisoDoEstado(EstadoLeitor estado) => estado switch
    {
        EstadoLeitor.SemDriver => AvisoSemDriver,
        EstadoLeitor.SemServico => AvisoSemServico,
        _ => AvisoSemLeitor,
    };

    /// <summary>A causa curta (para a auditoria) de cada estado do leitor que nao e Pronto.</summary>
    public static string CausaDoEstado(EstadoLeitor estado) => estado switch
    {
        EstadoLeitor.SemDriver => "leitor sem driver (WBF ou U.are.U RTE)",
        EstadoLeitor.SemServico => "servico de biometria parado",
        _ => "sem leitor",
    };
}

/// <summary>
/// A guarda local: chave do Windows ligada ao CPF. O CPF vai cifrado pelo DPAPI da MAQUINA
/// (so este Windows abre; o banco copiado para outro PC nao revela CPF), e ao lado vai o
/// hash (SHA-256 dos 11 digitos) que e o que sobe para a nuvem e vai na coluna do turno.
/// </summary>
public static class Digitais
{
    /// <summary>Entropia fixa do DPAPI: separa este cofre de qualquer outro programa da maquina.</summary>
    private static readonly byte[] Entropia = Encoding.UTF8.GetBytes("PdvNativo.digital.v1");

    /// <summary>SHA-256 hex dos 11 digitos. Null para CPF que nao e CPF.</summary>
    public static string? CpfHashOuNulo(string? cpf)
    {
        var chave = Operadores.CpfChave(cpf);
        return chave is null ? null : CpfHash(chave);
    }

    /// <summary>SHA-256 hex dos digitos (sem validar): e a chave de identidade na nuvem.</summary>
    public static string CpfHash(string cpf)
        => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(Documentos.SoDigitos(cpf)))).ToLowerInvariant();

    /// <summary>Cifra pelo DPAPI da maquina. Fora do Windows (nunca em producao) guarda marcado em claro.</summary>
    public static string Cifrar(string texto)
    {
        var bytes = Encoding.UTF8.GetBytes(texto);
        if (OperatingSystem.IsWindows())
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(bytes, Entropia, DataProtectionScope.LocalMachine));
        return "claro:" + Convert.ToBase64String(bytes);
    }

    /// <summary>O inverso de <see cref="Cifrar"/>. Null quando este Windows nao abre (banco de outro PC).</summary>
    public static string? Decifrar(string? guardado)
    {
        if (string.IsNullOrEmpty(guardado)) return null;
        try
        {
            if (guardado.StartsWith("dpapi:", StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows()) return null;
                var b = ProtectedData.Unprotect(Convert.FromBase64String(guardado[6..]), Entropia, DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(b);
            }
            if (guardado.StartsWith("claro:", StringComparison.Ordinal))
                return Encoding.UTF8.GetString(Convert.FromBase64String(guardado[6..]));
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Guarda (ou substitui) a digital daquele CPF. Um CPF, uma digital: recadastrar troca a
    /// chave; a linha antiga vai embora. Devolve a linha guardada.
    /// </summary>
    public static DigitalGuardada Guardar(SqliteConnection cx, string chave, string cpf, string? operadorId,
        string nome, int vaga, string? cadastradaPor, DateTime? agora = null, string motor = "winbio", byte[]? molde = null)
    {
        if (motor == LeitorHidUareU.MotorTexto && (molde is null || molde.Length == 0))
            throw new InvalidOperationException("A digital da HID veio sem molde.");
        var cpfChave = Operadores.CpfChave(cpf) ?? throw new InvalidOperationException("CPF invalido para a digital.");
        if (string.IsNullOrWhiteSpace(chave)) throw new InvalidOperationException("A digital veio sem chave.");
        var em = (agora ?? DateTime.Now).ToString("o");
        var hash = CpfHash(cpfChave);
        using var tx = cx.BeginTransaction();
        // a mesma pessoa de novo (recadastro) e a mesma chave reaproveitada: as duas saem antes
        cx.Execute("DELETE FROM digital WHERE cpf_hash = @H OR chave = @K", new { H = hash, K = chave }, tx);
        cx.Execute("""
            INSERT INTO digital (chave, cpf_cifrado, cpf_hash, operador_id, nome, vaga, cadastrada_em, cadastrada_por, registrada_em, motor, molde_cifrado)
            VALUES (@K, @C, @H, @Op, @N, @V, @Em, @Por, NULL, @Mo, @Ml)
            """, new { K = chave, C = Cifrar(cpfChave), H = hash, Op = operadorId, N = nome, V = vaga, Em = em, Por = cadastradaPor,
                  Mo = motor, Ml = molde is null ? null : CifrarBytes(molde) }, tx);
        Caixa.Auditar(cx, tx, "digital_cadastrada", operadorId, null,
            $"{nome} (cpf {Mascarar(cpfChave)}) vaga {vaga} {motor}" + (cadastradaPor is null ? "" : $" · por {cadastradaPor}"));
        tx.Commit();
        return new DigitalGuardada(chave, cpfChave, hash, operadorId, nome, DateTime.Parse(em), cadastradaPor, vaga, false) { Motor = motor };
    }

    /// <summary>Cifra bytes pelo DPAPI da maquina (o molde da HID). Mesmo formato de <see cref="Cifrar"/>.</summary>
    public static string CifrarBytes(byte[] bytes)
    {
        if (OperatingSystem.IsWindows())
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(bytes, Entropia, DataProtectionScope.LocalMachine));
        return "claro:" + Convert.ToBase64String(bytes);
    }

    /// <summary>O inverso de <see cref="CifrarBytes"/>. Null quando este Windows nao abre.</summary>
    public static byte[]? DecifrarBytes(string? guardado)
    {
        if (string.IsNullOrEmpty(guardado)) return null;
        try
        {
            if (guardado.StartsWith("dpapi:", StringComparison.Ordinal))
                return OperatingSystem.IsWindows()
                    ? ProtectedData.Unprotect(Convert.FromBase64String(guardado[6..]), Entropia, DataProtectionScope.LocalMachine)
                    : null;
            if (guardado.StartsWith("claro:", StringComparison.Ordinal)) return Convert.FromBase64String(guardado[6..]);
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Os moldes da HID desta guarda, decifrados: e contra eles que o LeitorHidUareU compara
    /// (ate 10 por caixa). Linha de outro PC (nao decifra) fica de fora.
    /// </summary>
    public static IReadOnlyList<MoldeGuardado> Moldes(SqliteConnection cx)
    {
        var saida = new List<MoldeGuardado>();
        foreach (var r in cx.Query("SELECT chave, molde_cifrado FROM digital WHERE motor = @M AND molde_cifrado IS NOT NULL ORDER BY vaga",
                     new { M = LeitorHidUareU.MotorTexto }))
        {
            var m = DecifrarBytes((string?)r.molde_cifrado);
            if (m is { Length: > 0 }) saida.Add(new MoldeGuardado((string)r.chave, m));
        }
        return saida;
    }

    /// <summary>
    /// O CPF ja tem digital no motor em uso? (<paramref name="motor"/> null = em qualquer um.)
    /// A digital do WinBio nao serve no leitor da HID: quem so tem aquela pode cadastrar de novo.
    /// </summary>
    public static bool JaTem(SqliteConnection cx, string cpf, string? motor)
    {
        var d = PorCpf(cx, cpf);
        return d is not null && (motor is null || d.Motor == motor);
    }

    /// <summary>
    /// O cadastro achou o mesmo dedo ja guardado (ChaveParecida)? Devolve o NOME de quem tem,
    /// se for outra pessoa; null se nao ha parecida ou se e o proprio CPF (recadastro).
    /// </summary>
    public static string? DonoDaParecida(SqliteConnection cx, Cadastro cad, string cpf)
    {
        if (cad.ChaveParecida is null) return null;
        var d = PorChave(cx, cad.ChaveParecida);
        if (d is null) return null;
        return d.CpfHash == CpfHash(cpf) ? null : d.Nome;
    }

    /// <summary>"529.***.***-25": o CPF nunca vai inteiro para a auditoria.</summary>
    public static string Mascarar(string cpf)
    {
        var d = Documentos.SoDigitos(cpf);
        return d.Length == 11 ? $"{d[..3]}.***.***-{d[9..]}" : "***";
    }

    public static DigitalGuardada? PorChave(SqliteConnection cx, string chave)
        => Ler(cx, "WHERE chave = @K", new { K = chave }).FirstOrDefault();

    public static DigitalGuardada? PorCpf(SqliteConnection cx, string cpf)
    {
        var h = CpfHashOuNulo(cpf);
        return h is null ? null : Ler(cx, "WHERE cpf_hash = @H", new { H = h }).FirstOrDefault();
    }

    public static IReadOnlyList<DigitalGuardada> Listar(SqliteConnection cx) => Ler(cx, "", null);

    /// <summary>As que o ERP ainda nao sabe (registrada_em nula): o que o Atualizar manda.</summary>
    public static IReadOnlyList<DigitalGuardada> PendentesDeRegistro(SqliteConnection cx)
        => Ler(cx, "WHERE registrada_em IS NULL", null);

    public static void MarcarRegistrada(SqliteConnection cx, string chave, DateTime? agora = null)
        => cx.Execute("UPDATE digital SET registrada_em = @Em WHERE chave = @K",
            new { Em = (agora ?? DateTime.Now).ToString("o"), K = chave });

    /// <summary>
    /// Apaga a digital daquele CPF. A remocao fica anotada em `digital_removida` para o
    /// Atualizar avisar o ERP (e para o Windows ser limpo pela chave).
    /// </summary>
    public static DigitalGuardada? Apagar(SqliteConnection cx, string cpf, string? por, DateTime? agora = null)
    {
        var d = PorCpf(cx, cpf);
        if (d is null) return null;
        var em = (agora ?? DateTime.Now).ToString("o");
        using var tx = cx.BeginTransaction();
        cx.Execute("DELETE FROM digital WHERE chave = @K", new { K = d.Chave }, tx);
        cx.Execute("INSERT INTO digital_removida (chave, cpf_hash, nome, removida_em, removida_por) VALUES (@K,@H,@N,@Em,@Por)",
            new { K = d.Chave, H = d.CpfHash, N = d.Nome, Em = em, Por = por }, tx);
        Caixa.Auditar(cx, tx, "digital_apagada", d.OperadorId, null, $"{d.Nome} (cpf {Mascarar(d.Cpf)})" + (por is null ? "" : $" · por {por}"));
        tx.Commit();
        return d;
    }

    /// <summary>As remocoes que o ERP ainda nao sabe.</summary>
    public static IReadOnlyList<(string Chave, string CpfHash, string Nome, DateTime RemovidaEm, string? Por)> RemocoesPendentes(SqliteConnection cx)
        => cx.Query("SELECT chave, cpf_hash, nome, removida_em, removida_por FROM digital_removida WHERE registrada_em IS NULL")
             .Select(r => ((string)r.chave, (string)r.cpf_hash, (string)r.nome, DateTime.Parse((string)r.removida_em), (string?)r.removida_por))
             .ToList();

    public static void MarcarRemocaoRegistrada(SqliteConnection cx, string chave, DateTime? agora = null)
        => cx.Execute("UPDATE digital_removida SET registrada_em = @Em WHERE chave = @K AND registrada_em IS NULL",
            new { Em = (agora ?? DateTime.Now).ToString("o"), K = chave });

    /// <summary>A primeira vaga (1..10) livre; null = as dez ocupadas.</summary>
    public static int? ProximaVaga(SqliteConnection cx)
    {
        var usadas = cx.Query<int>("SELECT vaga FROM digital").ToHashSet();
        for (var v = 1; v <= Biometria.Vagas; v++) if (!usadas.Contains(v)) return v;
        return null;
    }

    /// <summary>
    /// O operador ATIVO daquela chave: a linha da digital diz o CPF, e o cadastro de operadores
    /// diz quem e hoje (demitido no painel = digital nao entra mais, mesmo guardada).
    /// </summary>
    public static Operador? OperadorDaChave(SqliteConnection cx, string chave)
    {
        var d = PorChave(cx, chave);
        return d is null ? null : OperadorDoCpf(cx, d.Cpf);
    }

    public static Operador? OperadorDoCpf(SqliteConnection cx, string cpf)
    {
        var limpo = Operadores.CpfChave(cpf);
        if (limpo is null) return null;
        return cx.QueryFirstOrDefault<Operador>("""
            SELECT id AS Id, nome AS Nome, perfil AS Perfil FROM operador
             WHERE ativo = 1 AND id <> '_admin_' AND mesmo_que IS NULL AND cpf IS NOT NULL AND cpf <> ''
               AND replace(replace(replace(cpf,'.',''),'-',''),' ','') = @C
             ORDER BY da_nuvem DESC LIMIT 1
            """, new { C = limpo });
    }

    /// <summary>O CPF de um operador ativo (para o cadastro na primeira vez). Null sem CPF valido.</summary>
    public static string? CpfDoOperador(SqliteConnection cx, string operadorId)
        => Operadores.CpfChave(cx.ExecuteScalar<string?>("SELECT cpf FROM operador WHERE id = @Id", new { Id = operadorId }));

    /// <summary>Os operadores ativos com CPF, por nome: a lista da reserva.</summary>
    public static IReadOnlyList<Operador> ListaDaReserva(SqliteConnection cx)
        => cx.Query<Operador>("""
            SELECT id AS Id, nome AS Nome, perfil AS Perfil FROM operador
             WHERE ativo = 1 AND id <> '_admin_' AND mesmo_que IS NULL
             ORDER BY nome
            """).ToList();

    private static List<DigitalGuardada> Ler(SqliteConnection cx, string where, object? p)
    {
        var saida = new List<DigitalGuardada>();
        foreach (var r in cx.Query($"SELECT chave, cpf_cifrado, cpf_hash, operador_id, nome, vaga, cadastrada_em, cadastrada_por, registrada_em, motor FROM digital {where} ORDER BY nome", p))
        {
            var cpf = Decifrar((string?)r.cpf_cifrado);
            if (cpf is null) continue;   // banco de outro PC: a linha nao vale aqui
            saida.Add(new DigitalGuardada((string)r.chave, cpf, (string)r.cpf_hash, (string?)r.operador_id, (string)r.nome,
                DateTime.Parse((string)r.cadastrada_em), (string?)r.cadastrada_por, (int)(long)r.vaga, r.registrada_em is not null)
                { Motor = (string?)r.motor ?? "winbio" });
        }
        return saida;
    }
}

/// <summary>
/// O que o portao precisa da guarda (1.0.54). A real e <see cref="GuardaDigitalSqlite"/>; a
/// interface existe para o portao rodar sem segurar conexao do SQLite atravessando await.
/// </summary>
public interface IGuardaDigital
{
    /// <summary>O operador ATIVO desta loja com esse CPF (o cadastro local e o da loja); null = nao e funcionario ativo.</summary>
    Operador? PorCpf(string cpf);

    /// <summary>O CPF ja tem digital no leitor em uso neste caixa?</summary>
    bool JaTem(string cpf);

    /// <summary>O CPF (11 digitos) do operador; null sem CPF valido.</summary>
    string? Cpf(Operador op);

    /// <summary>A vaga do CPF (a dele, se ja teve) ou a primeira livre; null = dez ocupadas.</summary>
    int? Vaga(string cpf);

    /// <summary>Guarda o cadastro ligado ao CPF. null = guardou; texto = o aviso de uma linha (o dedo e de outra pessoa).</summary>
    string? Guardar(Operador op, string cpf, int vaga, Cadastro cad, string? por);
}

/// <summary>A guarda de verdade: cada chamada abre e fecha a propria conexao.</summary>
public sealed class GuardaDigitalSqlite : IGuardaDigital
{
    private readonly Func<SqliteConnection> _abrir;
    private readonly Func<string?> _motor;

    /// <param name="motor">o motor em uso depois da sonda ("winbio", "hid"; null = qualquer um).</param>
    public GuardaDigitalSqlite(Func<SqliteConnection> abrir, Func<string?> motor)
    {
        _abrir = abrir;
        _motor = motor;
    }

    public Operador? PorCpf(string cpf)
    {
        using var cx = _abrir();
        return Digitais.OperadorDoCpf(cx, cpf);
    }

    public bool JaTem(string cpf)
    {
        using var cx = _abrir();
        return Digitais.JaTem(cx, cpf, _motor());
    }

    public string? Cpf(Operador op)
    {
        using var cx = _abrir();
        return Digitais.CpfDoOperador(cx, op.Id);
    }

    public int? Vaga(string cpf)
    {
        using var cx = _abrir();
        return Digitais.PorCpf(cx, cpf)?.Vaga ?? Digitais.ProximaVaga(cx);
    }

    public string? Guardar(Operador op, string cpf, int vaga, Cadastro cad, string? por)
    {
        using var cx = _abrir();
        if (Digitais.DonoDaParecida(cx, cad, cpf) is { } outro) return Biometria.AvisoDigitalDeOutro(outro);
        Digitais.Guardar(cx, cad.Chave!, cpf, op.Id, op.Nome, vaga, por, null, cad.Motor, cad.Molde);
        return null;
    }
}

/// <summary>
/// A MAQUINA DE ESTADOS DO PORTAO. Todo caminho termina em Digital (o leitor reconheceu, ou o
/// primeiro cadastro), Reserva (o funcionario digita o CPF e o gerente geral ou o dono autoriza
/// com o codigo) ou Cancelado. Nenhum await aqui leva ConfigureAwait(false): quem chama e a
/// thread de UI e as continuacoes encostam em janela (mesma regra de Autorizacao.ResolverAsync).
/// </summary>
public static class PortaoDigital
{
    /// <summary>
    /// Identifica quem esta no caixa pela digital.
    /// </summary>
    /// <param name="porChave">chave do Windows -> operador ativo (null = digital desconhecida ou pessoa demitida).</param>
    /// <param name="porCpf">CPF digitado na reserva -> operador ativo desta loja (null = recusa sem pedir codigo).</param>
    /// <param name="finalidade">"entrada" ou "fechamento": muda o titulo e vai na referencia do TOTP.</param>
    /// <param name="ofertaPrimeiraVez">so na entrada: o botao Primeira vez, que leva ao primeiro cadastro pelo codigo do gerente (1.0.54).</param>
    public static async Task<DesfechoDigital> IdentificarAsync(ILeitorDigital leitor, ITelaDigital tela,
        Func<string, Operador?> porChave, Func<Operador, string?> cpfDe, Func<string, Operador?> porCpf,
        IAutorizacaoRemota? remota, ITelaAutorizacao telaTotp, string terminal, string finalidade,
        bool ofertaPrimeiraVez, Func<DateTime>? agora = null, CancellationToken ct = default)
    {
        var titulo = finalidade == "fechamento" ? Biometria.TituloFechamento : Biometria.TituloEntrada;
        var sonda = await leitor.SondarAsync(ct);
        if (!sonda.Pronto)
        {
            tela.Avisar("Leitor de digital", Biometria.AvisoDoEstado(sonda.Estado));
            return await ReservaAsync(tela, porCpf, remota, telaTotp, terminal, finalidade,
                Biometria.CausaDoEstado(sonda.Estado), agora, ct);
        }

        string? aviso = null;
        for (var tentativa = 1; tentativa <= Biometria.MaxTentativas; tentativa++)
        {
            var leitura = await tela.LerAsync(titulo, aviso, ofertaPrimeiraVez, leitor.IdentificarAsync);
            switch (leitura.Escolha)
            {
                case EscolhaNaLeitura.PrimeiraVez:
                    return new DesfechoDigital(ViaDigital.Cancelado, null, null, "primeira vez: codigo do gerente") { PrimeiraVez = true, Avisado = true };
                case EscolhaNaLeitura.SemDigital:
                    return await ReservaAsync(tela, porCpf, remota, telaTotp, terminal, finalidade, "o operador pediu a reserva", agora, ct);
                case EscolhaNaLeitura.Desistiu:
                    return new DesfechoDigital(ViaDigital.Cancelado, null, null, "o operador desistiu") { Avisado = true };
            }

            var c = leitura.Captura ?? Captura.Cancelada;
            switch (c.Resultado)
            {
                case ResultadoCaptura.Reconhecido:
                {
                    var op = porChave(c.Chave!);
                    if (op is not null)
                        return new DesfechoDigital(ViaDigital.Digital, op, cpfDe(op), "digital reconhecida");
                    aviso = Biometria.AvisoDigitalDesconhecida;
                    break;
                }
                case ResultadoCaptura.SemLeitor:
                    tela.Avisar("Leitor de digital", Biometria.AvisoSemLeitor);
                    return await ReservaAsync(tela, porCpf, remota, telaTotp, terminal, finalidade, "o leitor sumiu no meio", agora, ct);
                case ResultadoCaptura.Cancelado:
                    return new DesfechoDigital(ViaDigital.Cancelado, null, null, "o operador desistiu") { Avisado = true };
                case ResultadoCaptura.Falha:
                    aviso = $"O leitor falhou ({c.Motivo}). Tente de novo.";
                    break;
                default:
                    aviso = Biometria.AvisoNaoReconheci(tentativa);
                    break;
            }
        }

        tela.Avisar("Digital nao reconhecida", $"{Biometria.MaxTentativas} leituras sem reconhecer. Entrada pela reserva: codigo do gerente ou do dono.");
        return await ReservaAsync(tela, porCpf, remota, telaTotp, terminal, finalidade,
            $"{Biometria.MaxTentativas} leituras sem reconhecer", agora, ct);
    }

    /// <summary>
    /// O CPF do funcionario, digitado por ele (ajuste do dono, 27/10). Confere o digito e o
    /// cadastro de operadores ATIVOS desta loja; o que nao confere volta na mesma tela com uma
    /// linha vermelha, ate <see cref="Biometria.MaxTentativas"/> vezes. Nenhum codigo e pedido
    /// antes de o CPF conferir. Devolve (operador, cpf) ou (null, motivo).
    /// </summary>
    public static async Task<(Operador? Op, string? Cpf, string? Recusa, bool Desistiu)> CpfDoFuncionarioAsync(
        ITelaDigital tela, string titulo, Func<string, Operador?> porCpf)
    {
        string? aviso = null;
        for (var tentativa = 1; tentativa <= Biometria.MaxTentativas; tentativa++)
        {
            var digitado = await tela.PedirCpfAsync(titulo, aviso);
            if (digitado is null) return (null, null, "o funcionario desistiu do CPF", true);
            var cpf = Operadores.CpfChave(digitado);
            if (cpf is null || !Documentos.CpfValido(cpf)) { aviso = Biometria.AvisoCpfInvalido; continue; }
            var op = porCpf(cpf);
            if (op is null) { aviso = Biometria.AvisoCpfNaoEhFuncionario; continue; }
            return (op, cpf, null, false);
        }
        return (null, null, aviso ?? Biometria.AvisoCpfNaoEhFuncionario, false);
    }

    /// <summary>
    /// A RESERVA (ajuste do dono, 27/10): o FUNCIONARIO digita o proprio CPF (confere contra os
    /// operadores ativos desta loja; o que nao confere e recusado sem pedir codigo) e o caixa pede
    /// o codigo do autenticador do GERENTE GERAL ou do DONO (tipo 'turno_sem_digital', nivel
    /// gerente), na abertura e no fechamento. Sem o SQL 224 a nuvem so aceita o codigo do dono.
    /// Sem internet nao ha reserva: a regra do TOTP. Nunca senha.
    /// </summary>
    public static async Task<DesfechoDigital> ReservaAsync(ITelaDigital tela, Func<string, Operador?> porCpf,
        IAutorizacaoRemota? remota, ITelaAutorizacao telaTotp, string terminal,
        string finalidade, string causa, Func<DateTime>? agora = null, CancellationToken ct = default)
    {
        var titulo = finalidade == "fechamento" ? Biometria.TituloCpfFechamento : Biometria.TituloCpfEntrada;
        var (op, cpf, recusa, desistiu) = await CpfDoFuncionarioAsync(tela, titulo, porCpf);
        if (op is null)
            return new DesfechoDigital(ViaDigital.Cancelado, null, null, desistiu ? Biometria.MotivoReserva(causa) + " · " + recusa : recusa!) { Avisado = desistiu };

        var pedido = new PedidoAutorizacao(terminal, Autorizacao.ReferenciaTurnoSemDigital(terminal, finalidade, (agora ?? (() => DateTime.Now))()), 0)
        {
            Tipo = Autorizacao.TipoTurnoSemDigital,
            Nivel = Autorizacao.NivelGerente,
            Operador = $"{op.Nome} ({causa})",
            // 1.0.56: sem internet o codigo de emergencia sobe com quem entrou e para que
            CpfHash = cpf is null ? null : Digitais.CpfHash(cpf),
            Finalidade = finalidade,
        };
        var aut = await Autorizacao.ResolverAsync(remota, pedido, telaTotp, ct);
        if (!aut.Autorizado)
            return new DesfechoDigital(ViaDigital.Cancelado, null, null, Biometria.MotivoReserva(causa) + " · " + aut.Motivo) { Avisado = aut.Avisado };

        return new DesfechoDigital(ViaDigital.Reserva, op, cpf, Biometria.MotivoReserva(causa), aut.AprovadoPor, aut.TokenId)
            { NivelCodigo = aut.Via == ViaAutorizacao.Emergencia ? DesfechoDigital.NivelEmergencia : aut.Nivel ?? Autorizacao.NivelGerente };
    }

    /// <summary>
    /// A ENTRADA DO CAIXA, SO DIGITAL (1.0.54) E OBRIGATORIA (1.0.55, decisao do dono de 27/10). Nao
    /// existe senha nem biometria desligada: a digital (IdentificarAsync, com o botao Primeira vez);
    /// problema no leitor cai na reserva; Primeira vez segue para o PRIMEIRO CADASTRO.
    /// </summary>
    public static async Task<DesfechoDigital> EntrarAsync(ILeitorDigital leitor, ITelaDigital tela,
        IGuardaDigital guarda, Func<string, Operador?> porChave,
        IAutorizacaoRemota? remota, ITelaAutorizacao telaTotp, string terminal, Func<DateTime>? agora = null, CancellationToken ct = default)
    {
        var d = await IdentificarAsync(leitor, tela, porChave, guarda.Cpf, guarda.PorCpf, remota, telaTotp, terminal, "entrada",
            ofertaPrimeiraVez: true, agora, ct);
        if (!d.PrimeiraVez) return d;
        return await PrimeiroCadastroAsync(leitor, tela, guarda, remota, telaTotp, terminal, agora, ct);
    }

    /// <summary>
    /// O PRIMEIRO CADASTRO (1.0.54): quem ainda nao tem digital neste caixa. Sem senha e com o
    /// mesmo comeco da reserva (ajuste do dono, 27/10): o FUNCIONARIO digita o CPF (operador ativo
    /// desta loja, ainda sem digital no leitor em uso), o gerente geral ou o dono autoriza com o
    /// codigo (tipo 'digital_cadastro', nivel gerente; antes do SQL 225 a nuvem so aceita o do
    /// dono) e o caixa le o dedo 3 vezes, guarda ligado ao CPF e entra. Leitor com problema antes
    /// de comecar: a reserva (um codigo so, nao dois).
    /// </summary>
    public static async Task<DesfechoDigital> PrimeiroCadastroAsync(ILeitorDigital leitor, ITelaDigital tela, IGuardaDigital guarda,
        IAutorizacaoRemota? remota, ITelaAutorizacao telaTotp, string terminal,
        Func<DateTime>? agora = null, CancellationToken ct = default)
    {
        static DesfechoDigital Nao(string motivo, bool avisado = true) => new(ViaDigital.Cancelado, null, null, motivo) { Avisado = avisado };

        var sonda = await leitor.SondarAsync(ct);
        if (!sonda.Pronto)
        {
            tela.Avisar("Leitor de digital", Biometria.AvisoDoEstado(sonda.Estado));
            return await ReservaAsync(tela, guarda.PorCpf, remota, telaTotp, terminal, "entrada",
                Biometria.CausaDoEstado(sonda.Estado), agora, ct);
        }

        var (op, cpf, recusa, desistiu) = await CpfDoFuncionarioAsync(tela, Biometria.TituloCpfCadastro, guarda.PorCpf);
        if (op is null || cpf is null) return Nao(desistiu ? "primeiro cadastro: " + recusa : recusa!, desistiu);
        // quem ja tem digital entra pela digital: o codigo do gerente nao e gasto
        if (guarda.JaTem(cpf)) return Nao(Biometria.AvisoJaTemDigital, false);
        var vaga = guarda.Vaga(cpf);
        if (vaga is null) return Nao(Biometria.AvisoVagasEsgotadas, false);

        var pedido = new PedidoAutorizacao(terminal, Autorizacao.ReferenciaDigitalCadastro(terminal, (agora ?? (() => DateTime.Now))()), 0)
        {
            Tipo = Autorizacao.TipoDigitalCadastro,
            Nivel = Autorizacao.NivelGerente,
            Operador = $"{op.Nome} (primeiro cadastro)",
        };
        var aut = await Autorizacao.ResolverAsync(remota, pedido, telaTotp, ct);
        if (!aut.Autorizado) return Nao("primeiro cadastro · " + aut.Motivo, aut.Avisado);

        var cad = await CadastrarAsync(leitor, tela, Biometria.TituloPrimeiraVez, vaga.Value, ct);
        switch (cad.Resultado)
        {
            case ResultadoCadastro.Ok:
                if (guarda.Guardar(op, cpf, vaga.Value, cad, aut.AprovadoPor) is { } erro)
                {
                    tela.Avisar("Digital", erro);
                    return Nao("primeiro cadastro: " + erro);
                }
                return new DesfechoDigital(ViaDigital.Digital, op, cpf, Biometria.MotivoPrimeiroCadastro,
                    aut.AprovadoPor, aut.TokenId) { PrimeiroCadastro = true, NivelCodigo = aut.Nivel ?? Autorizacao.NivelGerente };
            case ResultadoCadastro.Cancelado:
                tela.Avisar("Digital", Biometria.AvisoCadastroCancelado);
                return Nao("primeiro cadastro: cancelado");
            case ResultadoCadastro.SemLeitor:
                tela.Avisar("Digital", Biometria.AvisoSemLeitor);
                return Nao("primeiro cadastro: sem leitor");
            default:
                tela.Avisar("Digital", "O leitor falhou (" + cad.Motivo + "). Tente de novo.");
                return Nao("primeiro cadastro: o leitor falhou");
        }
    }

    /// <summary>
    /// O cadastro: o minimo de 3 leituras (o Windows pode pedir mais), e a guarda ligada ao CPF.
    /// Devolve o cadastro do leitor; quem chama grava com <see cref="Digitais.Guardar"/> quando Ok.
    /// </summary>
    public static Task<Cadastro> CadastrarAsync(ILeitorDigital leitor, ITelaDigital tela, string titulo, int vaga,
        CancellationToken ct = default)
        => tela.CadastrarAsync(titulo, (progresso, token) =>
            leitor.CadastrarAsync(vaga, new ProgressoDireto<int>(n => progresso.Report(Biometria.ProgressoCadastro(n))), token));

    /// <summary>
    /// IProgress que avisa NA HORA e na mesma thread. O Progress&lt;T&gt; do .NET posta no
    /// SynchronizationContext capturado (fora da UI, na thread pool, depois): a suite veria
    /// "Leitura 3 de 3" chegar depois do fim, e a tela de WPF ja faz o Invoke que precisa.
    /// </summary>
    public sealed class ProgressoDireto<T> : IProgress<T>
    {
        private readonly Action<T> _ao;
        public ProgressoDireto(Action<T> ao) => _ao = ao;
        public void Report(T value) => _ao(value);
    }
}
