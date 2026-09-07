using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Pdv.Nucleo;

// Harness de fumaça da PGWebLib.dll DE VERDADE, em 32 bits, SEM venda.
//
// O que ele prova: que o binding P/Invoke do PDV (PGWebLibNativa, assinaturas do exemplo
// oficial) carrega a DLL instalada pelo PayGo Windows, que cada símbolo existe, o que a
// DLL responde a PW_iInit / PW_iGetResult / PW_iGetOperations e, pelo MESMO provedor que o
// caixa usa (ProvedorPGWebLib.AdministrativaAsync), o menu administrativo com o teste de
// comunicação. Nunca responde nada que dispare transação financeira: menu sem opção segura
// é cancelado; dado digitado fora da instalação é cancelado; senha do lojista é cancelada.
//
// Uso: Pdv.SmokePGWebLib.exe [pastaDll] [pastaTrabalho] [portaPinpad] [--sem-adm] [--instalar]
//   pastaDll       padrão C:\Program Files (x86)\PayGo\PGWebLib
//   pastaTrabalho  padrão C:\ProgramData\PayGo\PGWebLib (a mesma do cliente PayGo)
//   portaPinpad    padrão 5
//   --sem-adm      para depois do PW_iInit do provedor (não chama PW_iNewTransac)
//   --instalar     roda PWOPER_INSTALL antes da administrativa (só faz sentido em Demonstração)
//   --sem-exports  não faz NativeLibrary.Load à parte: a DLL entra só pelo DllImport (como no Pdv.exe)
//   --sem-end      NÃO chama ProvedorPGWebLib.Encerrar() (PW_End) na saída: reproduz o fail-fast
//                  0xC0000409 que a DLL dispara no DLL_PROCESS_DETACH quando foi iniciada e não encerrada
//   --descarregar  experimento: FreeLibrary até a DLL sair do processo, antes de sair
//   --terminar     experimento: TerminateProcess(0) no fim (nenhum detach roda)
//   --so-end       experimento: PW_End sem nenhum PW_iInit (a DLL encerra a instância que contou na carga; exit 0)
//
// O exit code do processo é a medida: 0 = saiu limpo; -1073740791 (0xC0000409) = a DLL abortou
// o processo no detach.

Console.OutputEncoding = Encoding.UTF8;
var posicionais = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
var pastaDll = posicionais.ElementAtOrDefault(0) ?? @"C:\Program Files (x86)\PayGo\PGWebLib";
var pastaTrabalho = posicionais.ElementAtOrDefault(1) ?? @"C:\ProgramData\PayGo\PGWebLib";
var porta = posicionais.ElementAtOrDefault(2) ?? "5";
var semAdm = args.Contains("--sem-adm");
var instalarPedido = args.Contains("--instalar");

Console.WriteLine($"processo: {(Environment.Is64BitProcess ? "64" : "32")} bits ({RuntimeInformation.ProcessArchitecture}), .NET {Environment.Version}, IntPtr {IntPtr.Size} bytes");
Console.WriteLine($"pasta da DLL:      {pastaDll}");
Console.WriteLine($"pasta de trabalho: {pastaTrabalho}");
Console.WriteLine($"porta do pinpad:   {porta}");

// ── 1. o arquivo ────────────────────────────────────────────────────────────
var caminhoDll = Path.Combine(pastaDll, "PGWebLib.dll");
Console.WriteLine();
Console.WriteLine("[1] arquivo");
if (!File.Exists(caminhoDll)) { Console.WriteLine("   NÃO EXISTE: " + caminhoDll); return 2; }
var fvi = FileVersionInfo.GetVersionInfo(caminhoDll);
Console.WriteLine($"   {caminhoDll}");
Console.WriteLine($"   FileVersion={fvi.FileVersion} ProductVersion={fvi.ProductVersion} tamanho={new FileInfo(caminhoDll).Length} bytes");
Console.WriteLine($"   máquina PE: {MaquinaPe(caminhoDll)}");

// ── 2. LoadLibrary direto + tabela de exports ───────────────────────────────
// --sem-exports pula esta parte: a DLL entra no processo SÓ pelo DllImport do PDV (uma
// instância), que é como o Pdv.exe a carrega. Serve para separar o que é do harness.
Console.WriteLine();
if (args.Contains("--sem-exports"))
    Console.WriteLine("[2] pulado (--sem-exports): a DLL entra só pelo DllImport do PDV");
else
{
    Console.WriteLine("[2] carga e símbolos (NativeLibrary.Load + TryGetExport)");
    IntPtr handle;
    try { handle = NativeLibrary.Load(caminhoDll); Console.WriteLine($"   LoadLibrary OK (handle 0x{handle:X})"); }
    catch (Exception ex) { Console.WriteLine($"   LoadLibrary FALHOU: {ex.GetType().Name}: {ex.Message}"); return 3; }
    string[] simbolosDoBinding =
    {
        "PW_iInit", "PW_iNewTransac", "PW_iAddParam", "PW_iExecTransac", "PW_iGetResult", "PW_iConfirmation",
        "PW_iWaitConfirmation", "PW_iIdleProc", "PW_iGetOperations", "PW_iPPAbort", "PW_iPPEventLoop", "PW_iPPGetCard",
        "PW_iPPGetPIN", "PW_iPPGetData", "PW_iPPGoOnChip", "PW_iPPFinishChip", "PW_iPPConfirmData",
        "PW_iPPPositiveConfirmation", "PW_iPPRemoveCard", "PW_iPPGenericCMD", "PW_iTransactionInquiry",
        "PW_End",   // fora do exemplo oficial: encerramento que evita o fail-fast no detach (07/09/2026)
    };
    var faltando = new List<string>();
    foreach (var s in simbolosDoBinding)
    {
        var tem = NativeLibrary.TryGetExport(handle, s, out _);
        if (!tem) faltando.Add(s);
        Console.WriteLine($"   {(tem ? "existe" : "FALTA ")} {s}");
    }
    Console.WriteLine($"   binding: {simbolosDoBinding.Length - faltando.Count}/{simbolosDoBinding.Length} símbolos encontrados" + (faltando.Count > 0 ? " (faltam: " + string.Join(", ", faltando) + ")" : ""));
    string[] extras = { "PW_iPPDisplay", "PW_iPPWaitEvent", "PW_iPPTestKey", "PW_iPPCommTest", "PW_iPPGetUserData", "PW_iPPStartPIN", "PW_iGetVersion", "PW_iPPGetPINBlock",
                        // 07/09/2026: o kit sem Warsaw escolhe produção ou homologação por
                        // PW_iSetEnvironment (ENVRMNT_PROD=0, ENVRMNT_TEST=1), e não mais pelo
                        // instalador do PayGo Windows. Precisa ser chamada ANTES de o ponto de
                        // captura estar instalado.
                        "PW_iSetEnvironment", "PW_iRegisterEvent" };
    Console.WriteLine("   extras (não usados pelo PDV): " + string.Join(", ", extras.Select(s => $"{s}={(NativeLibrary.TryGetExport(handle, s, out _) ? "sim" : "não")}")));
}

// ── 3. o binding do PDV: UsarPasta + Init + leituras ────────────────────────
Console.WriteLine();
Console.WriteLine("[3] binding do PDV (PGWebLibNativa)");
PGWebLibNativa.UsarPasta(pastaDll);
if (args.Contains("--so-end"))
{
    // Experimento: PW_End sem NENHUM PW_iInit neste processo (o fallback de Servicos.EncerrarTef
    // quando a instância atual não foi quem iniciou a DLL). Medido: a DLL conta a instância desde a
    // carga e o PW_End a encerra inteira (PGWLib_End no log); exit 0.
    new PGWebLibNativa().End();
    Console.WriteLine($"   --so-end: PW_End sem PW_iInit voltou (DLL carregada={PGWebLibNativa.Carregada()}); saindo");
    return 0;
}
var lib = new Espia(new PGWebLibNativa());
short init;
try { init = lib.Init(pastaTrabalho); }
catch (Exception ex)
{
    Console.WriteLine($"   PW_iInit LANÇOU {ex.GetType().Name}: {ex.Message}");
    if (ex is EntryPointNotFoundException) Console.WriteLine("   (símbolo não encontrado: o nome/decoração do export não bate com o DllImport)");
    if (ex is BadImageFormatException) Console.WriteLine("   (bitness errada: DLL x86 num processo x64 ou vice-versa)");
    return 4;
}
Console.WriteLine($"   PW_iInit => {PW.Nome(init)}");
lib.GetResult(PW.PWINFO_IDLEPROCTIME, out var idle);
Console.WriteLine($"   PWINFO_IDLEPROCTIME = \"{idle}\"");
lib.GetResult(PW.PWINFO_PNDREQNUM, out _);
lib.GetResult(PW.PWINFO_PNDAUTHSYST, out _);
foreach (byte tipo in new byte[] { 1, 2, 3 })
{
    var r = lib.GetOperations(tipo, out var ops);
    Console.WriteLine($"   PW_iGetOperations({tipo}) => {PW.Nome(r)}, {ops.Count} operação(ões)");
    foreach (var o in ops) Console.WriteLine($"      tipo={o.Codigo} texto=\"{o.Texto}\" valor=\"{o.Valor}\"");
}
lib.Descarrega();

// ── 4. pelo provedor do caixa: PW_iInit (cria a pasta de trabalho) e a administrativa ─
// O provedor é o mesmo do Pdv.exe: a pasta de trabalho inexistente (PWRET_WRITERR no [3]) é
// criada por ele antes do PW_iInit; --sem-adm para logo depois disso.
Console.WriteLine();
Console.WriteLine("[4] ProvedorPGWebLib (o mesmo do caixa)");
var instalando = false;
var opcoes = new OpcoesPGWebLib("Pdv.AmericanDay", "0.5.9", "American Day", PortaPinpad: porta);
using var pg = new ProvedorPGWebLib(lib, pastaTrabalho, opcoes)
{
    IntervaloPollMs = 200,
    TempoMaxExecMs = 120_000,
    TempoMaxCapturaMs = 90_000,
    TempoPerguntaMs = 30_000,
    Auditar = m => { lib.Descarrega(); Console.WriteLine("   auditoria: " + m); },
    Guardar = t => { lib.Descarrega(); Console.WriteLine($"   Guardar: {Corta(t.ToString(), 600)}"); return true; },
    Perguntar = (d, _) => Task.FromResult(Responder(d, instalando)),
};
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(240));
var ativo = await pg.AtivoAsync(cts.Token);
lib.Descarrega();
Console.WriteLine($"   AtivoAsync => {ativo}  (pasta {pg.PastaTrabalho} existe={Directory.Exists(pg.PastaTrabalho)}; ProximoIdle={pg.ProximoIdle?.ToString("s") ?? "-"}; motivo={pg.MotivoIndisponivel ?? "-"})");
if (semAdm) { Console.WriteLine("   --sem-adm: parando antes de PW_iNewTransac"); return Sair(pg); }

if (instalarPedido)
{
    instalando = true;
    Console.WriteLine("   --instalar: PWOPER_INSTALL");
    Mostra(await pg.InstalarAsync(cts.Token));
    instalando = false;
}

Console.WriteLine("   AdministrativaAsync (PWOPER_ADMIN)...");
var adm = await pg.AdministrativaAsync(cts.Token);
Mostra(adm);
if (adm.Motivo == ProvedorPGWebLib.MsgNaoInstalado && !instalarPedido)
{
    // Terminal sem instalação neste diretório de trabalho: ambiente é Demonstração, então
    // pode instalar com as credenciais do sandbox e tentar a administrativa de novo.
    instalando = true;
    Console.WriteLine("   PWRET_NOTINST: rodando PWOPER_INSTALL com as credenciais do sandbox");
    Mostra(await pg.InstalarAsync(cts.Token));
    instalando = false;
    Console.WriteLine("   AdministrativaAsync de novo...");
    Mostra(await pg.AdministrativaAsync(cts.Token));
}
lib.Descarrega();
Console.WriteLine();
Console.WriteLine("fim do harness");
return Sair(pg);

// ── saída: o que o Pdv.exe faz no fechamento, e os experimentos ─────────────
// Medido em 07/09/2026 (PGWebLib.dll 4.1.50.24, x86): com a biblioteca iniciada e NÃO
// encerrada, o DLL_PROCESS_DETACH roda PGWLib_End -> warsaw_sdk::Initialize e aborta o
// processo (fail-fast 0xC0000409) DEPOIS do Main devolver 0. PW_End é a mesma função,
// chamada com o processo vivo; ela zera o flag "iniciada" e o detach vira no-op.
int Sair(ProvedorPGWebLib provedor)
{
    if (args.Contains("--sem-end"))
        Console.WriteLine("   --sem-end: saindo SEM PW_End (espera-se 0xC0000409 no detach)");
    else
    {
        var r = provedor.Encerrar();
        lib.Descarrega();
        Console.WriteLine($"   Encerrar() (PW_End) => {r}");
    }
    if (args.Contains("--descarregar"))
    {
        var n = 0;
        while (GetModuleHandleW("PGWebLib.dll") is var h && h != IntPtr.Zero && n < 10) { NativeLibrary.Free(h); n++; }
        Console.WriteLine($"   --descarregar: FreeLibrary x{n}; ainda carregada={GetModuleHandleW("PGWebLib.dll") != IntPtr.Zero}");
    }
    if (args.Contains("--terminar"))
    {
        Console.WriteLine("   --terminar: TerminateProcess(0)");
        Console.Out.Flush();
        TerminateProcess(GetCurrentProcess(), 0);
    }
    return 0;
}

[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string nome);
[DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
[DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint codigo);

// ── auxiliares ──────────────────────────────────────────────────────────────

static void Mostra(DesfechoTef d)
{
    Console.WriteLine($"   DESFECHO: situação={d.Situacao} código={d.Codigo} motivo=\"{d.Motivo}\" chargeId={d.ChargeId} paymentStatus={d.PaymentStatus} desfeita={d.Desfeita} posOcupado={d.PosPodeTerFicadoOcupado}");
}

/// <summary>A resposta ao que a biblioteca pede. Nunca dispara nada financeiro.</summary>
static string? Responder(PwGetData d, bool instalando)
{
    static string N(string? s) => ArquivoIntpos.Ascii(s).ToUpperInvariant();
    static string? Resp(string o, string v) { Console.WriteLine($"   RESPONDO ({o}): \"{v}\""); return v; }
    if (d.EhMenu && d.Opcoes is { Count: > 0 })
    {
        var alvo = d.Opcoes.FirstOrDefault(o => N(o.Texto).Contains("COMUNIC") || N(o.Valor).Contains("COMUNIC"));
        if (alvo is null && instalando)
            alvo = d.Opcoes.FirstOrDefault(o => N(o.Texto).Contains("DEMONSTRA") || N(o.Valor).Contains("DEMONSTRA"));
        if (alvo is not null) return Resp("menu " + d.Identificador, alvo.Valor);
        Console.WriteLine($"   menu {d.Identificador} sem opção segura: CANCELO (devolvo nulo)");
        return null;
    }
    if (d.Tipo == PW.PWDAT_USERAUTH || d.Ocultar)
    {
        // Na instalação a DLL pede a senha com prompt vazio (id 246): é a senha do sandbox.
        if (instalando) return Resp($"senha de instalação, id {d.Identificador}", "3D590EE2");
        Console.WriteLine($"   senha pedida ({d.Identificador} \"{d.Prompt}\"): CANCELO");
        return null;
    }
    if (instalando)
    {
        var p = N(d.Prompt);
        if (p.Contains("CNPJ") || p.Contains("CPF")) return Resp("CNPJ", "62177839000157");
        if (p.Contains("SENHA")) return Resp("senha", "3D590EE2");
        if (p.Contains("PONTO") || p.Contains("CAPTURA") || p.Contains("PDC")) return Resp("ponto de captura", "114975");
        // Servidor do ambiente Demonstração: o mesmo que a DLL gravou no log do PayGo desta máquina (0x1B).
        if (p.Contains("SERVIDOR")) return Resp("servidor do sandbox", "pos-transac-sb.tpgweb.io:31735");
        if (p.Contains("INSTAL") || p.Contains("IDENTIF")) return Resp("id de instalação", "127310");
    }
    Console.WriteLine($"   dado digitado não previsto ({d.Identificador} \"{d.Prompt}\"): CANCELO");
    return null;
}

static string MaquinaPe(string caminho)
{
    using var f = File.OpenRead(caminho);
    using var br = new BinaryReader(f);
    f.Position = 0x3C;
    var lfanew = br.ReadInt32();
    f.Position = lfanew;
    if (br.ReadUInt32() != 0x00004550) return "sem assinatura PE";
    var maquina = br.ReadUInt16();
    return maquina switch { 0x014C => "0x14C i386 (32 bits)", 0x8664 => "0x8664 x64 (64 bits)", _ => $"0x{maquina:X}" };
}

static string Corta(string s, int n) => s.Length <= n ? s : s[..n] + "…";

/// <summary>Decorador que imprime cada chamada à DLL, o retorno e cada PW_GetData pedido.</summary>
sealed class Espia : IPGWebLib
{
    private readonly IPGWebLib _d;
    private string? _ultima;
    private int _rep;
    public Espia(IPGWebLib d) { _d = d; }

    private void Linha(string s)
    {
        if (s == _ultima) { _rep++; return; }
        Descarrega();
        Console.WriteLine(s);
        _ultima = s;
    }

    public void Descarrega()
    {
        if (_rep > 0) Console.WriteLine($"      (linha anterior repetida mais {_rep}x)");
        _rep = 0;
        _ultima = null;
    }

    private short Chama(string nome, Func<short> f)
    {
        short r;
        try { r = f(); }
        catch (Exception ex) { Descarrega(); Console.WriteLine($"   {nome} LANÇOU {ex.GetType().Name}: {ex.Message}"); throw; }
        Linha($"   {nome} -> {PW.Nome(r)}");
        return r;
    }

    private static string NomeTipo(ushort t) => t switch
    {
        PW.PWDAT_MENU => "PWDAT_MENU", PW.PWDAT_TYPED => "PWDAT_TYPED", PW.PWDAT_CARDINF => "PWDAT_CARDINF",
        PW.PWDAT_PPENTRY => "PWDAT_PPENTRY", PW.PWDAT_PPENCPIN => "PWDAT_PPENCPIN", PW.PWDAT_CARDOFF => "PWDAT_CARDOFF",
        PW.PWDAT_CARDONL => "PWDAT_CARDONL", PW.PWDAT_PPCONF => "PWDAT_PPCONF", PW.PWDAT_BARCODE => "PWDAT_BARCODE",
        PW.PWDAT_PPREMCRD => "PWDAT_PPREMCRD", PW.PWDAT_PPGENCMD => "PWDAT_PPGENCMD", PW.PWDAT_PPDATAPOSCNF => "PWDAT_PPDATAPOSCNF",
        PW.PWDAT_USERAUTH => "PWDAT_USERAUTH", PW.PWDAT_DSPCHECKOUT => "PWDAT_DSPCHECKOUT", PW.PWDAT_DSPQRCODE => "PWDAT_DSPQRCODE",
        _ => "PWDAT_?",
    } + $" ({t})";

    public short Init(string d) => Chama($"PW_iInit(\"{d}\")", () => _d.Init(d));

    public void End()
    {
        Descarrega();
        Console.WriteLine("   PW_End()...");
        try { _d.End(); }
        catch (Exception ex) { Console.WriteLine($"   PW_End LANÇOU {ex.GetType().Name}: {ex.Message}"); throw; }
        Console.WriteLine("   PW_End voltou");
    }

    public short NewTransac(byte op) => Chama($"PW_iNewTransac({op})", () => _d.NewTransac(op));
    public short AddParam(ushort info, string valor) => Chama($"PW_iAddParam({info}, \"{valor}\")", () => _d.AddParam(info, valor));

    public short ExecTransac(out IReadOnlyList<PwGetData> pedidos)
    {
        IReadOnlyList<PwGetData> p = Array.Empty<PwGetData>();
        var r = Chama("PW_iExecTransac()", () => _d.ExecTransac(out p));
        pedidos = p;
        foreach (var d in p)
        {
            Descarrega();
            Console.WriteLine($"      PEDIDO {NomeTipo(d.Tipo)} id={d.Identificador} prompt=\"{d.Prompt}\" min={d.TamanhoMinimo} max={d.TamanhoMaximo} máscara=\"{d.Mascara}\" inicial=\"{d.ValorInicial}\" ocultar={d.Ocultar} aceitaNulo={d.AceitaNulo}");
            if (d.Opcoes is not null)
                for (var k = 0; k < d.Opcoes.Count; k++)
                    Console.WriteLine($"         opção {k}: texto=\"{d.Opcoes[k].Texto}\" valor=\"{d.Opcoes[k].Valor}\"");
        }
        return r;
    }

    public short GetResult(ushort info, out string valor)
    {
        var v = "";
        var r = Chama($"PW_iGetResult({info})", () => _d.GetResult(info, out v));
        valor = v;
        if (r == PW.PWRET_OK)
        {
            Descarrega();
            var mostrado = info == PW.PWINFO_CARDFULLPAN ? "***" : v.Replace("\r", "\\r").Replace("\n", "\\n");
            Console.WriteLine($"      = \"{(mostrado.Length > 300 ? mostrado[..300] + "…" : mostrado)}\"");
        }
        return r;
    }

    public short Confirmation(uint resultado, string reqNum, string locRef, string extRef, string virtMerch, string authSyst)
        => Chama($"PW_iConfirmation({resultado}, req={reqNum}, loc={locRef}, ext={extRef}, vm={virtMerch}, as={authSyst})",
            () => _d.Confirmation(resultado, reqNum, locRef, extRef, virtMerch, authSyst));
    public short WaitConfirmation() => Chama("PW_iWaitConfirmation()", _d.WaitConfirmation);
    public short IdleProc() => Chama("PW_iIdleProc()", _d.IdleProc);

    public short GetOperations(byte tipo, out IReadOnlyList<PwOperacao> operacoes)
    {
        IReadOnlyList<PwOperacao> ops = Array.Empty<PwOperacao>();
        var r = Chama($"PW_iGetOperations({tipo})", () => _d.GetOperations(tipo, out ops));
        operacoes = ops;
        return r;
    }

    public short PPGetCard(ushort i) => Chama($"PW_iPPGetCard({i})", () => _d.PPGetCard(i));
    public short PPGetPIN(ushort i) => Chama($"PW_iPPGetPIN({i})", () => _d.PPGetPIN(i));
    public short PPGetData(ushort i) => Chama($"PW_iPPGetData({i})", () => _d.PPGetData(i));
    public short PPGoOnChip(ushort i) => Chama($"PW_iPPGoOnChip({i})", () => _d.PPGoOnChip(i));
    public short PPFinishChip(ushort i) => Chama($"PW_iPPFinishChip({i})", () => _d.PPFinishChip(i));
    public short PPConfirmData(ushort i) => Chama($"PW_iPPConfirmData({i})", () => _d.PPConfirmData(i));
    public short PPPositiveConfirmation(ushort i) => Chama($"PW_iPPPositiveConfirmation({i})", () => _d.PPPositiveConfirmation(i));
    public short PPRemoveCard() => Chama("PW_iPPRemoveCard()", _d.PPRemoveCard);
    public short PPGenericCMD(ushort i) => Chama($"PW_iPPGenericCMD({i})", () => _d.PPGenericCMD(i));

    public short PPEventLoop(out string display)
    {
        var d = "";
        short r;
        try { r = _d.PPEventLoop(out d); }
        catch (Exception ex) { Descarrega(); Console.WriteLine($"   PW_iPPEventLoop LANÇOU {ex.GetType().Name}: {ex.Message}"); throw; }
        display = d;
        Linha(r == PW.PWRET_DISPLAY
            ? $"   PW_iPPEventLoop() -> PWRET_DISPLAY \"{d.Replace("\r", "|").Replace("\n", "|")}\""
            : $"   PW_iPPEventLoop() -> {PW.Nome(r)}");
        return r;
    }

    public short PPAbort() => Chama("PW_iPPAbort()", _d.PPAbort);

    public short TransactionInquiry(string xml, out string resposta)
    {
        var s = "";
        var r = Chama("PW_iTransactionInquiry(...)", () => _d.TransactionInquiry(xml, out s));
        resposta = s;
        return r;
    }
}
