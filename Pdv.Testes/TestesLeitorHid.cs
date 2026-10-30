using System.Runtime.InteropServices;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O SEGUNDO LEITOR: o runtime da HID (U.are.U RTE, dpfpdd.dll + dpfj.dll), caixa 1.0.53, 26/10/2026.
///
/// Problema real: na loja o U.are.U 4500 estava plugado e com driver, e o caixa 1.0.52 dizia
/// que nao havia leitor. O driver instalado era o da HID (nao o WBF), entao o WinBio do Windows
/// nao enumera nada. Esta suite protege, sem leitor fisico:
///  · LeitorHidUareU sobre uma API falsa: sonda (sem runtime, sem aparelho, pronto), a
///    identificacao contra os moldes da guarda com o limiar de 1/100000 (estritamente menor),
///    o melhor de varios, leitura ruim, cancelamento, o cadastro (3 leituras no minimo, o que
///    o runtime pedir a mais, leituras ruins puladas) e a digital repetida de outra pessoa;
///  · a escolha automatica (LeitorAutomatico): WinBio primeiro, HID depois, e a frase do dono
///    quando nenhum dos dois acha o leitor; o apagar vai para o motor da chave;
///  · a guarda: molde cifrado pelo DPAPI, coluna motor, so os moldes da HID vao para a
///    comparacao, "ja tem digital" so conta no motor em uso;
///  · o texto do "Conferir o leitor" (motor, unidades, dispositivos, versao, USB 05BA com
///    classe e driver) e a regra do driver nao WBF;
///  · a API de verdade: carrega sem lancar nesta maquina (sem RTE = diz o que falta), o
///    layout das estruturas da dpfpdd.h, e nenhuma DLL da HID no instalador.
/// </summary>
public static class TestesLeitorHid
{
    // ── a API da HID de mentira ───────────────────────────────────────────────
    private sealed class ApiHidFalsa : IApiHid
    {
        public RuntimeHid Runtime = new(true, @"C:\Windows\System32\dpfpdd.dll", "3.2.1", null);
        public List<DispositivoHid> Lista = new() { new(@"\\?\usb#vid_05ba&pid_000a#1", "DigitalPersona", "U.are.U 4500", "SN-1", 0x05BA, 0x000A) };
        public string? ErroDaLista;
        public Queue<CapturaHid> Capturas = new();
        public Func<byte[], byte[]?> Extrai = fid => fid;
        /// <summary>Distancia por par: iguais = 0, diferentes = nunca.</summary>
        public Func<byte[], byte[], uint> Compara = (a, b) => a.SequenceEqual(b) ? 0u : LeitorHidUareU.ProbabilidadeUm;
        /// <summary>Quantas amostras o runtime pede para fechar o cadastro.</summary>
        public int ProntoCom = 3;
        public bool EsperaCancelamento;
        public int Cargas, Capturados, Juntadas, Comparacoes;

        public RuntimeHid Carregar() { Cargas++; return Runtime; }

        public IReadOnlyList<DispositivoHid> Dispositivos(out string? erro) { erro = ErroDaLista; return Runtime.Carregado ? Lista : Array.Empty<DispositivoHid>(); }

        public CapturaHid Capturar(string dispositivo, CancellationToken ct)
        {
            Capturados++;
            if (EsperaCancelamento) { ct.WaitHandle.WaitOne(); return new CapturaHid(QualidadeHid.Cancelada, null); }
            return Capturas.Count > 0 ? Capturas.Dequeue() : new CapturaHid(QualidadeHid.SemDedo, null, "sem dedo");
        }

        public byte[]? Extrair(byte[] fid, out string? erro) { erro = null; var f = Extrai(fid); if (f is null) erro = "sem minucias"; return f; }

        public byte[]? JuntarCadastro(IReadOnlyList<byte[]> fmds, out bool faltaAmostra, out string? erro)
        {
            Juntadas++;
            erro = null;
            if (fmds.Count < ProntoCom) { faltaAmostra = true; return null; }
            faltaAmostra = false;
            return fmds[0].ToArray();
        }

        public uint Comparar(byte[] a, byte[] b) { Comparacoes++; return Compara(a, b); }
    }

    // ── um leitor de mentira para o lado do Windows ───────────────────────────
    private sealed class WindowsFalso : ILeitorDigital
    {
        public string Nome => "WinBio";
        public EstadoLeitor Estado = EstadoLeitor.SemLeitor;
        public string Detalhe = "nenhum leitor de digital no Windows";
        public int Sondas, Identificacoes;
        public List<string> Apagadas = new();
        public Task<SondaLeitor> SondarAsync(CancellationToken ct)
        {
            Sondas++;
            return Task.FromResult(new SondaLeitor(Estado, Detalhe, Estado == EstadoLeitor.Pronto ? new[] { "#1 Leitor do Windows" } : Array.Empty<string>()));
        }
        public Task<Captura> IdentificarAsync(CancellationToken ct) { Identificacoes++; return Task.FromResult(Captura.Reconhecida("winbio:3:12:aabb:dedo:1")); }
        public Task<Cadastro> CadastrarAsync(int vaga, IProgress<int>? amostra, CancellationToken ct) => Task.FromResult(Cadastro.Feito("winbio:3:12:aabb:dedo:" + vaga, 3));
        public Task<bool> ApagarAsync(string chave, CancellationToken ct) { Apagadas.Add(chave); return Task.FromResult(true); }
    }

    private sealed class Contador : IProgress<int> { public readonly List<int> Vistos = new(); public void Report(int value) => Vistos.Add(value); }

    private static CapturaHid Boa(params byte[] b) => new(QualidadeHid.Boa, b);

    private const string CpfAna = "52998224725";
    private const string CpfBia = "11144477735";

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"leitor-hid-{Guid.NewGuid():N}.db");
        Banco.Migrar(arquivo);
        var cx = Banco.Abrir(arquivo);
        try
        {
            var (h, s) = Operadores.GerarHash("4725");
            cx.Execute("""
                INSERT INTO operador (id, nome, pin_hash, pin_salt, perfil, cpf, ativo, da_nuvem, atualizado)
                VALUES ('op-ana','ANA',@H,@S,'operador',@C1,1,1,@Em), ('op-bia','BIA',@H,@S,'gerente',@C2,1,1,@Em)
                """, new { H = h, S = s, C1 = CpfAna, C2 = CpfBia, Em = DateTime.Now.ToString("o") });

            // ── N. o leitor da HID sobre a API falsa ─────────────────────────────────
            checar(LeitorHidUareU.Limiar == 0x7FFFFFFFu / 100000u && LeitorHidUareU.Limiar == 21474u, "N-01 o limiar e o falso aceite de 1 em 100000 da propria dpfj (0x7FFFFFFF / 100000)");

            var api = new ApiHidFalsa { Runtime = new RuntimeHid(false, null, null, "dpfpdd.dll nao achada") };
            var moldes = new List<MoldeGuardado>();
            var hid = new LeitorHidUareU(api, () => moldes);
            var sonda = await hid.SondarAsync(CancellationToken.None);
            checar(!sonda.Pronto && sonda.Estado == EstadoLeitor.SemLeitor && sonda.Detalhe.Contains("U.are.U RTE"), "N-02 sem o runtime da HID: sem leitor, e o detalhe diz U.are.U RTE");
            checar(hid.Nome == "HID" && LeitorHidUareU.MotorTexto == "hid", "N-03 o nome do motor e HID (coluna motor = hid)");

            api = new ApiHidFalsa { Lista = new() };
            hid = new LeitorHidUareU(api, () => moldes);
            sonda = await hid.SondarAsync(CancellationToken.None);
            checar(!sonda.Pronto && sonda.Estado == EstadoLeitor.SemLeitor && sonda.Detalhe.Contains("3.2.1"), "N-04 runtime instalado sem leitor plugado: sem leitor, com a versao do runtime");

            api = new ApiHidFalsa();
            hid = new LeitorHidUareU(api, () => moldes);
            sonda = await hid.SondarAsync(CancellationToken.None);
            checar(sonda.Pronto && sonda.Unidades.Count == 1 && sonda.Unidades[0].Contains("U.are.U 4500"), "N-05 runtime com um leitor: pronto, e a unidade tem o nome do aparelho");

            moldes.Add(new MoldeGuardado("hid:ana", new byte[] { 1, 1, 1 }));
            moldes.Add(new MoldeGuardado("hid:bia", new byte[] { 2, 2, 2 }));
            api.Capturas.Enqueue(Boa(1, 1, 1));
            var c = await hid.IdentificarAsync(CancellationToken.None);
            checar(c.Resultado == ResultadoCaptura.Reconhecido && c.Chave == "hid:ana" && api.Comparacoes == 2, "N-06 o dedo bate com o molde da ANA: reconhecida pela chave dela (comparou com os dois)");

            api.Capturas.Enqueue(Boa(9, 9, 9));
            c = await hid.IdentificarAsync(CancellationToken.None);
            checar(c.Resultado == ResultadoCaptura.NaoReconhecido && c.Chave is null, "N-07 dedo de ninguem: nao reconhecida");

            api.Compara = (a, b) => LeitorHidUareU.Limiar;
            api.Capturas.Enqueue(Boa(1, 1, 1));
            c = await hid.IdentificarAsync(CancellationToken.None);
            checar(c.Resultado == ResultadoCaptura.NaoReconhecido, "N-08 distancia igual ao limiar nao entra (estritamente menor)");

            api.Compara = (a, b) => b[0] == 1 ? 900u : 15u;
            api.Capturas.Enqueue(Boa(5));
            c = await hid.IdentificarAsync(CancellationToken.None);
            checar(c.Chave == "hid:bia", "N-09 dois abaixo do limiar: vence o mais parecido (menor distancia)");
            api.Compara = (a, b) => a.SequenceEqual(b) ? 0u : LeitorHidUareU.ProbabilidadeUm;

            api.Capturas.Enqueue(new CapturaHid(QualidadeHid.Cancelada, null));
            checar((await hid.IdentificarAsync(CancellationToken.None)).Resultado == ResultadoCaptura.Cancelado, "N-10 captura cancelada: cancelada");
            api.Capturas.Enqueue(new CapturaHid(QualidadeHid.Ruim, null, "dedo fora do centro"));
            c = await hid.IdentificarAsync(CancellationToken.None);
            checar(c.Resultado == ResultadoCaptura.NaoReconhecido && c.Motivo!.Contains("fora do centro"), "N-11 leitura ruim conta como nao reconhecida, com o motivo");
            api.Capturas.Enqueue(new CapturaHid(QualidadeHid.Falha, null, "HID DEVICE_FAILURE"));
            c = await hid.IdentificarAsync(CancellationToken.None);
            checar(c.Resultado == ResultadoCaptura.Falha && c.Motivo!.Contains("DEVICE_FAILURE"), "N-12 falha do aparelho: falha com o codigo");
            api.Extrai = _ => null;
            api.Capturas.Enqueue(Boa(1, 1, 1));
            c = await hid.IdentificarAsync(CancellationToken.None);
            checar(c.Resultado == ResultadoCaptura.NaoReconhecido && c.Motivo!.Contains("sem minucias"), "N-13 a dpfj nao tirou o molde da imagem: nao reconhecida, sem excecao");
            api.Extrai = fid => fid;

            var vazio = new LeitorHidUareU(api, () => Array.Empty<MoldeGuardado>());
            api.Capturas.Enqueue(Boa(1, 1, 1));
            var antes = api.Comparacoes;
            c = await vazio.IdentificarAsync(CancellationToken.None);
            checar(c.Resultado == ResultadoCaptura.NaoReconhecido && api.Comparacoes == antes, "N-14 guarda sem molde da HID: le o dedo e nao reconhece, sem comparar");

            api.EsperaCancelamento = true;
            using (var cts = new CancellationTokenSource(150))
                c = await hid.IdentificarAsync(cts.Token);
            checar(c.Resultado == ResultadoCaptura.Cancelado, "N-15 o botao da tela cancela a leitura presa (token)");
            api.EsperaCancelamento = false;

            var semAparelho = new LeitorHidUareU(new ApiHidFalsa { Lista = new() }, () => moldes);
            checar((await semAparelho.IdentificarAsync(CancellationToken.None)).Resultado == ResultadoCaptura.SemLeitor
                   && (await semAparelho.CadastrarAsync(1, null, CancellationToken.None)).Resultado == ResultadoCadastro.SemLeitor, "N-16 leitor desplugado: identificar e cadastrar dizem sem leitor");

            // cadastro
            api = new ApiHidFalsa();
            hid = new LeitorHidUareU(api, () => moldes);
            api.Capturas.Enqueue(Boa(7, 7)); api.Capturas.Enqueue(Boa(7, 7)); api.Capturas.Enqueue(Boa(7, 7));
            var prog = new Contador();
            var cad = await hid.CadastrarAsync(3, prog, CancellationToken.None);
            checar(cad.Resultado == ResultadoCadastro.Ok && cad.Chave!.StartsWith("hid:") && cad.Motor == "hid" && cad.Molde is { Length: > 0 } && cad.Amostras == 3,
                "N-17 tres leituras boas: cadastro feito com chave hid:, motor hid e o molde");
            checar(prog.Vistos.SequenceEqual(new[] { 1, 2, 3 }) && api.Capturados == 3 && cad.ChaveParecida is null, "N-18 o progresso conta 1, 2, 3 e nao ha digital parecida");

            api = new ApiHidFalsa { ProntoCom = 4 };
            hid = new LeitorHidUareU(api, () => moldes);
            api.Capturas.Enqueue(Boa(7)); api.Capturas.Enqueue(new CapturaHid(QualidadeHid.Ruim, null, "rapido")); api.Capturas.Enqueue(Boa(7));
            api.Capturas.Enqueue(Boa(7)); api.Capturas.Enqueue(Boa(7));
            prog = new Contador();
            cad = await hid.CadastrarAsync(3, prog, CancellationToken.None);
            checar(cad.Resultado == ResultadoCadastro.Ok && cad.Amostras == 4 && prog.Vistos.SequenceEqual(new[] { 1, 2, 3, 4 }) && api.Capturados == 5,
                "N-19 o runtime pediu a quarta: leitura ruim pulada, 4 amostras boas");

            api = new ApiHidFalsa();
            hid = new LeitorHidUareU(api, () => moldes);
            api.Capturas.Enqueue(Boa(7)); api.Capturas.Enqueue(new CapturaHid(QualidadeHid.Cancelada, null));
            checar((await hid.CadastrarAsync(3, null, CancellationToken.None)).Resultado == ResultadoCadastro.Cancelado, "N-20 cancelou no meio: cadastro cancelado");

            api = new ApiHidFalsa { ProntoCom = int.MaxValue };
            hid = new LeitorHidUareU(api, () => moldes);
            for (var i = 0; i < 40; i++) api.Capturas.Enqueue(Boa(7));
            cad = await hid.CadastrarAsync(3, null, CancellationToken.None);
            checar(cad.Resultado == ResultadoCadastro.Falha && api.Capturados == LeitorHidUareU.MaxLeiturasDoCadastro, "N-21 o runtime nunca fecha: para no teto de leituras e falha (nao fica em laco)");

            api = new ApiHidFalsa();
            hid = new LeitorHidUareU(api, () => moldes);
            api.Capturas.Enqueue(Boa(2, 2, 2)); api.Capturas.Enqueue(Boa(2, 2, 2)); api.Capturas.Enqueue(Boa(2, 2, 2));
            cad = await hid.CadastrarAsync(3, null, CancellationToken.None);
            checar(cad.Resultado == ResultadoCadastro.Ok && cad.ChaveParecida == "hid:bia", "N-22 o dedo novo ja esta na guarda (BIA): o cadastro devolve a chave parecida");

            checar(await hid.ApagarAsync("hid:ana", CancellationToken.None) && api.Capturados == 3, "N-23 apagar na HID nao toca o aparelho (o molde mora na guarda)");

            // ── O. a escolha automatica ─────────────────────────────────────────────
            var win = new WindowsFalso { Estado = EstadoLeitor.Pronto };
            api = new ApiHidFalsa();
            var auto = new LeitorAutomatico(win, new LeitorHidUareU(api, () => moldes));
            sonda = await auto.SondarAsync(CancellationToken.None);
            checar(sonda.Pronto && auto.MotorEmUso == "winbio" && api.Cargas == 0, "O-01 WinBio com unidade: usa o Windows e nem carrega o runtime da HID");
            c = await auto.IdentificarAsync(CancellationToken.None);
            checar(win.Identificacoes == 1 && c.Chave!.StartsWith("winbio:"), "O-02 a leitura vai para o Windows");

            win = new WindowsFalso { Estado = EstadoLeitor.SemDriver, Detalhe = "leitor plugado sem WBF" };
            api = new ApiHidFalsa();
            auto = new LeitorAutomatico(win, new LeitorHidUareU(api, () => moldes));
            sonda = await auto.SondarAsync(CancellationToken.None);
            checar(sonda.Pronto && auto.MotorEmUso == "hid" && auto.Nome == "HID", "O-03 o caso da loja: WinBio vazio e o runtime da HID com o leitor: usa a HID");
            api.Capturas.Enqueue(Boa(1, 1, 1));
            c = await auto.IdentificarAsync(CancellationToken.None);
            checar(c.Chave == "hid:ana" && win.Identificacoes == 0, "O-04 a leitura vai para a HID");
            api.Capturas.Enqueue(Boa(3)); api.Capturas.Enqueue(Boa(3)); api.Capturas.Enqueue(Boa(3));
            cad = await auto.CadastrarAsync(4, null, CancellationToken.None);
            checar(cad.Resultado == ResultadoCadastro.Ok && cad.Motor == "hid", "O-05 o cadastro vai para a HID");
            await auto.ApagarAsync("winbio:3:12:aabb:dedo:1", CancellationToken.None);
            await auto.ApagarAsync("hid:abc", CancellationToken.None);
            checar(win.Apagadas.SequenceEqual(new[] { "winbio:3:12:aabb:dedo:1" }), "O-06 apagar vai para o motor da chave (winbio: no Windows, hid: na guarda)");

            win = new WindowsFalso { Estado = EstadoLeitor.SemServico, Detalhe = "servico parado" };
            auto = new LeitorAutomatico(win, new LeitorHidUareU(new ApiHidFalsa(), () => moldes));
            checar((await auto.SondarAsync(CancellationToken.None)).Pronto && auto.MotorEmUso == "hid", "O-07 servico de biometria parado mas o runtime da HID ve o leitor: usa a HID");

            win = new WindowsFalso { Estado = EstadoLeitor.SemDriver, Detalhe = "leitor plugado sem WBF" };
            auto = new LeitorAutomatico(win, new LeitorHidUareU(new ApiHidFalsa { Runtime = new RuntimeHid(false, null, null, "dpfpdd.dll nao achada") }, () => moldes));
            sonda = await auto.SondarAsync(CancellationToken.None);
            checar(!sonda.Pronto && sonda.Estado == EstadoLeitor.SemDriver && auto.MotorEmUso is null && auto.Nome == "nenhum", "O-08 plugado sem WBF e sem RTE: sem driver, nenhum motor");
            checar(Biometria.AvisoDoEstado(sonda.Estado).StartsWith(Biometria.AvisoLeitorNaoEncontrado) && Biometria.AvisoLeitorNaoEncontrado == "Leitor não encontrado. Instale o driver WBF da HID ou o U.are.U RTE.",
                "O-09 a frase do dono em uma linha: Leitor nao encontrado. Instale o driver WBF da HID ou o U.are.U RTE.");
            checar(sonda.Detalhe.Contains("Windows:") && sonda.Detalhe.Contains("HID:"), "O-10 o detalhe diz o que cada motor achou");
            checar((await auto.IdentificarAsync(CancellationToken.None)).Resultado == ResultadoCaptura.SemLeitor, "O-11 sem motor, a leitura diz sem leitor (o portao cai na reserva)");

            win = new WindowsFalso { Estado = EstadoLeitor.SemLeitor };
            auto = new LeitorAutomatico(win, new LeitorHidUareU(new ApiHidFalsa { Lista = new() }, () => moldes));
            sonda = await auto.SondarAsync(CancellationToken.None);
            checar(sonda.Estado == EstadoLeitor.SemLeitor && Biometria.AvisoDoEstado(sonda.Estado) == Biometria.AvisoSemLeitor, "O-12 nada plugado: sem leitor (plugue o leitor)");

            // ── P. a guarda com o molde ─────────────────────────────────────────────
            var molde = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
            var g = Digitais.Guardar(cx, "hid:ana-1", CpfAna, "op-ana", "ANA", 1, null, null, LeitorHidUareU.MotorTexto, molde);
            var cifrado = cx.ExecuteScalar<string>("SELECT molde_cifrado FROM digital WHERE chave = 'hid:ana-1'")!;
            checar(cifrado.StartsWith("dpapi:") && !cifrado.Contains(Convert.ToBase64String(molde)), "P-01 o molde fica cifrado pelo DPAPI da maquina (nunca o molde em claro)");
            checar(g.Motor == "hid" && Digitais.PorCpf(cx, CpfAna)!.Motor == "hid", "P-02 a linha guarda o motor hid");
            Digitais.Guardar(cx, "winbio:3:12:bbbb:dedo:2", CpfBia, "op-bia", "BIA", 2, null);
            var lidos = Digitais.Moldes(cx);
            checar(lidos.Count == 1 && lidos[0].Chave == "hid:ana-1" && lidos[0].Fmd.SequenceEqual(molde), "P-03 so os moldes da HID vao para a comparacao, decifrados iguais");
            checar(Digitais.PorCpf(cx, CpfBia)!.Motor == "winbio", "P-04 sem motor dito, a linha e do WinBio (como na 1.0.52)");
            checar(Digitais.JaTem(cx, CpfBia, null) && !Digitais.JaTem(cx, CpfBia, "hid") && Digitais.JaTem(cx, CpfAna, "hid"),
                "P-05 ja tem digital: so conta no motor em uso (digital do WinBio nao trava a primeira vez na HID)");
            var cadParecido = Cadastro.Feito("hid:novo", 3) with { ChaveParecida = "hid:ana-1" };
            checar(Digitais.DonoDaParecida(cx, cadParecido, CpfBia) == "ANA" && Digitais.DonoDaParecida(cx, cadParecido, CpfAna) is null
                   && Digitais.DonoDaParecida(cx, Cadastro.Feito("hid:novo", 3), CpfBia) is null, "P-06 dedo que ja e de outra pessoa: devolve o nome dela; o mesmo dono (recadastro) passa");
            checar(Biometria.AvisoDigitalDeOutro("ANA") == "Essa digital já está cadastrada para ANA.", "P-07 o aviso da digital repetida em uma linha");
            var colunas = cx.Query<string>("SELECT name FROM pragma_table_info('digital')").ToList();
            checar(colunas.Contains("molde_cifrado") && colunas.Contains("motor"), "P-08 a migracao acrescenta molde_cifrado e motor na tabela digital");
            cx.Execute("UPDATE digital SET molde_cifrado = 'dpapi:AAAA' WHERE chave = 'hid:ana-1'");
            checar(Digitais.Moldes(cx).Count == 0, "P-09 molde de outro PC (nao decifra) fica de fora, sem excecao");
            cx.Execute("DELETE FROM digital");

            // o portao inteiro com a HID: da leitura ao operador
            Digitais.Guardar(cx, "hid:ana-2", CpfAna, "op-ana", "ANA", 1, null, null, LeitorHidUareU.MotorTexto, new byte[] { 4, 4, 4 });
            api = new ApiHidFalsa();
            api.Capturas.Enqueue(Boa(4, 4, 4));
            auto = new LeitorAutomatico(new WindowsFalso(), new LeitorHidUareU(api, () => Digitais.Moldes(cx)));
            var d = await PortaoDigital.IdentificarAsync(auto, new TelaQueLe(), ch => Digitais.OperadorDaChave(cx, ch), op => Digitais.CpfDoOperador(cx, op.Id),
                cpf => Digitais.OperadorDoCpf(cx, cpf), null, new TotpNada(), "CAIXA-01", "entrada", true);
            checar(d.Via == ViaDigital.Digital && d.Operador?.Id == "op-ana" && d.Cpf == CpfAna, "P-10 o portao com o leitor da HID: a ANA entra pela digital");
            cx.Execute("DELETE FROM digital");

            // ── Q. o Conferir o leitor ──────────────────────────────────────────────
            var usb = new[]
            {
                new DriverDigitalPersona.Aparelho("PID_000A", "Biometric", true, "usbdpfp", "U.are.U 4500 Fingerprint Reader", "4.1.1.221", "HID Global"),
                new DriverDigitalPersona.Aparelho("PID_000B", "", false),
            };
            var linhas = DiagnosticoLeitor.Linhas("hid", Array.Empty<string>(), 0, null,
                new RuntimeHid(true, @"C:\Windows\System32\dpfpdd.dll", "3.2.1", null), new ApiHidFalsa().Lista, null, usb, "1.0.53", new DateTime(2026, 10, 26, 14, 5, 0));
            var texto = string.Join("\n", linhas);
            checar(linhas[0].Contains("1.0.53") && linhas[0].Contains("26/10/2026 14:05"), "Q-01 a primeira linha leva a versao do caixa e a hora (para a foto)");
            checar(texto.Contains("Motor em uso: HID (U.are.U RTE)"), "Q-02 diz o motor em uso");
            checar(texto.Contains("Windows: nenhuma unidade"), "Q-03 diz o que o Windows achou");
            checar(texto.Contains("U.are.U RTE 3.2.1") && texto.Contains(@"C:\Windows\System32\dpfpdd.dll") && texto.Contains("U.are.U 4500") && texto.Contains("05BA"), "Q-04 diz a versao do runtime, de onde carregou e o aparelho");
            checar(texto.Contains("PID_000A") && texto.Contains("classe Biometric") && texto.Contains("driver usbdpfp 4.1.1.221 (HID Global)") && texto.Contains("plugado")
                   && texto.Contains("PID_000B") && texto.Contains("desplugado"), "Q-05 as entradas USB VID_05BA com classe, driver e se esta plugado");
            checar(!texto.Contains(Biometria.AvisoLeitorNaoEncontrado), "Q-06 com motor em uso nao ha a frase de leitor nao encontrado");

            linhas = DiagnosticoLeitor.Linhas(null, Array.Empty<string>(), 0, null, new RuntimeHid(false, null, null, "dpfpdd.dll nao achada"),
                Array.Empty<DispositivoHid>(), null, Array.Empty<DriverDigitalPersona.Aparelho>(), "1.0.53", DateTime.Now);
            texto = string.Join("\n", linhas);
            checar(texto.Contains("Motor em uso: nenhum") && texto.Contains(Biometria.AvisoLeitorNaoEncontrado) && texto.Contains("hidglobal.com")
                   && texto.Contains("U.are.U RTE não instalado"), "Q-07 sem nada: a frase do dono, o passo com o site da HID e o que falta");
            linhas = DiagnosticoLeitor.Linhas("winbio", new[] { "#1 DigitalPersona U.are.U 4500" }, 0, null, new RuntimeHid(false, null, null, "x"),
                Array.Empty<DispositivoHid>(), null, Array.Empty<DriverDigitalPersona.Aparelho>(), "1.0.53", DateTime.Now);
            texto = string.Join("\n", linhas);
            checar(texto.Contains("Motor em uso: Windows (WinBio)") && texto.Contains("Windows: 1 unidade") && texto.Contains("#1 DigitalPersona U.are.U 4500"), "Q-08 com o WBF: Windows e a unidade");
            checar(texto.Split('\n').All(l => !l.Contains((char)0x2014) && !l.Contains((char)0x2013)), "Q-09 o texto do diagnostico nao tem travessao");

            var naoWbf = DriverDigitalPersona.Avaliar(new[] { new DriverDigitalPersona.Aparelho("PID_000A", "Biometric", true, "usbdpfp") });
            checar(naoWbf.Estado == EstadoLeitor.SemDriver && naoWbf.Detalhe.Contains("usbdpfp"), "Q-10 classe Biometric com o driver da HID (usbdpfp, nao WBF): falta driver para o Windows, nao e o servico");
            var wbf = DriverDigitalPersona.Avaliar(new[] { new DriverDigitalPersona.Aparelho("PID_000A", "Biometric", true, "WUDFRd") });
            checar(wbf.Estado == EstadoLeitor.SemServico, "Q-11 classe Biometric com o driver WBF (WUDFRd) e o WinBio sem listar: e o servico");
            // o PC da loja (26/10, Get-PnpDevice VID_05BA): dois, Status OK, classe "Authentication Devices"
            var loja = new[]
            {
                new DriverDigitalPersona.Aparelho("PID_000A", "Authentication Devices", true, "", "Leitora de Impressão Digital, U.are.U® 4500"),
                new DriverDigitalPersona.Aparelho("PID_000A", "Authentication Devices", true, "", "Leitora de Impressão Digital, U.are.U® 4500"),
            };
            var avLoja = DriverDigitalPersona.Avaliar(loja);
            checar(avLoja.Estado == EstadoLeitor.SemDriver && avLoja.Detalhe.Contains("driver da HID") && avLoja.Detalhe.Contains("runtime da HID")
                   && DriverDigitalPersona.EhDriverDaHid(loja[0]) && !DriverDigitalPersona.EhWbf(loja[0]),
                "Q-13 classe Authentication Devices = driver proprio da HID: o WinBio nao ve, quem le e o runtime da HID");
            texto = string.Join("\n", DiagnosticoLeitor.Linhas("hid", Array.Empty<string>(), 0, null, new RuntimeHid(true, @"C:\Windows\System32\dpfpdd.dll", "3.2.1", null),
                new ApiHidFalsa().Lista, null, loja, "1.0.53", DateTime.Now));
            checar(texto.Contains("Driver da HID instalado (classe Authentication Devices") && texto.Contains("usando o runtime da HID") && texto.Contains("USB VID_05BA: 2 entrada(s)"),
                "Q-14 o diagnostico da loja: driver da HID instalado, usando o runtime da HID, e as duas entradas");
            texto = string.Join("\n", DiagnosticoLeitor.Linhas(null, Array.Empty<string>(), 0, null, new RuntimeHid(false, null, null, "dpfpdd.dll nao achada"),
                Array.Empty<DispositivoHid>(), null, loja, "1.0.53", DateTime.Now));
            checar(texto.Contains(DiagnosticoLeitor.LinhaDriverHidSemRuntime) && !texto.Contains(Biometria.AvisoLeitorNaoEncontrado)
                   && DiagnosticoLeitor.LinhaDriverHidSemRuntime == "Leitor plugado com o driver da HID, sem o runtime. Instale o driver do Windows (Biometric) ou o U.are.U RTE.",
                "Q-15 o PC da loja (driver da HID, sem dpfpdd/dpfj): uma linha diz isso e o que instalar");

            // o bug da 1.0.52: plugado, Status OK, e o caixa dizia "ja esteve aqui, mas nao esta" (sem a subchave Control)
            const string idLoja = @"USB\VID_05BA&PID_000A\5&2B3C4D5E&0&3";
            var doRegistro = new[] { new DriverDigitalPersona.Aparelho("PID_000A", "", false, "", "", "", "", idLoja),
                                     new DriverDigitalPersona.Aparelho("PID_000A", "Biometric", false, "WUDFRd", "", "5.0.0.5", "HID Global", @"USB\VID_05BA&PID_000A\ANTIGO") };
            var presentesLoja = new[] { new DriverDigitalPersona.Aparelho("PID_000A", "Authentication Devices", true, "", "Leitora de Impressão Digital, U.are.U® 4500", "", "", idLoja) };
            var juntos = DriverDigitalPersona.Juntar(doRegistro, presentesLoja);
            checar(juntos.Count == 2 && juntos[0].Presente && juntos[0].Classe == "Authentication Devices" && !juntos[1].Presente,
                "Q-16 plugado vem do SetupAPI (DIGCF_PRESENT), nao da subchave Control: o da loja aparece plugado com a classe real");
            var avJuntos = DriverDigitalPersona.Avaliar(juntos);
            checar(avJuntos.Estado == EstadoLeitor.SemDriver && !avJuntos.Detalhe.Contains("nao esta plugado") && avJuntos.Detalhe.Contains("driver da HID"),
                "Q-17 cenario 1 (loja): Authentication Devices plugado = driver da HID, nunca 'ja esteve aqui'");
            var presentesWbf = new[] { new DriverDigitalPersona.Aparelho("PID_000A", "Biometric", true, "WUDFRd", "U.are.U 4500 Fingerprint Reader", "", "", @"USB\VID_05BA&PID_000A\ANTIGO") };
            var juntosWbf = DriverDigitalPersona.Juntar(doRegistro, presentesWbf);
            checar(juntosWbf.First(a => a.Presente).Classe == "Biometric" && juntosWbf.First(a => a.Presente).VersaoDriver == "5.0.0.5"
                   && DriverDigitalPersona.Avaliar(juntosWbf).Estado == EstadoLeitor.SemServico,
                "Q-18 cenario 2 (WBF): Biometric plugado, o registro completa a versao do driver; WinBio sem listar = servico");
            checar(DriverDigitalPersona.Linha(juntos[0]).Contains("classe Authentication Devices") && DriverDigitalPersona.Linha(juntos[0]).Contains("plugado")
                   && DriverDigitalPersona.Linha(juntos[0]).Contains(idLoja), "Q-19 a linha mostra a classe real e o id da instancia (duas entradas = dois ids)");
            var lidosAqui = DriverDigitalPersona.LerAparelhos();
            checar(lidosAqui is not null, $"Q-20 o SetupAPI desta maquina le sem lancar ({lidosAqui.Count} aparelho(s) 05BA)");
            var todos = DriverDigitalPersona.ContarPresentesNoSistema();
            checar(todos > 10, $"Q-21 o SetupAPI de verdade (DIGCF_PRESENT) responde nesta maquina: {todos} aparelhos presentes");
            checar(DriverDigitalPersona.PassoDoDriver.Contains("hidglobal.com") && DriverDigitalPersona.PassoDoDriver.Contains("U.are.U RTE")
                   && DriverDigitalPersona.PassoDoDriver.Contains("Windows Update"), "Q-12 o passo cita o site da HID, o RTE e o Windows Update");

            // ── Q2. o resumo em linguagem de loja (1.0.57) ──────────────────────────
            // o caso da foto do dono (27/10, 16:12): o Windows le, o RTE da HID nao esta instalado
            var unidadesFoto = new[] { "#3 U.are.U® 4500 Fingerprint Reader (WBF) (CROSSMATCH U.are.U Fingerprint Reader)" };
            var hidFoto = new RuntimeHid(false, null, null, @"dpfpdd.dll nao achada no System32, no PATH nem em Program Files\DigitalPersona");
            var usbFoto = new[] { new DriverDigitalPersona.Aparelho("PID_000A", "Biometric", true, "WUDFRd", "U.are.U® 4500 Fingerprint Reader (WBF)", "5.0.0.5", "CROSSMATCH", @"USB\VID_05BA&PID_000A\{...}") };
            var foto = DiagnosticoLeitor.Montar("winbio", unidadesFoto, 0, null, hidFoto, Array.Empty<DispositivoHid>(), null, usbFoto, "1.0.57", new DateTime(2026, 10, 27, 16, 12, 0));
            checar(foto.Janela[0] == "Caixa 1.0.57, 27/10/2026 16:12" && foto.Janela[1] == "Leitor funcionando." && foto.Estado == "Leitor funcionando.",
                "Q-22 a foto do dono: a janela abre com a versao e a hora e logo depois \"Leitor funcionando.\"");
            checar(foto.Resumo.SequenceEqual(new[] { "Leitor funcionando.", "O caixa lê a digital pelo Windows.", "Não precisa fazer nada.", "Leitor: U.are.U 4500, plugado" }),
                "Q-23 a foto do dono: o resumo diz que funciona, que le pelo Windows, que nao precisa fazer nada e o nome curto do leitor");
            var assusta = new[] { "erro", "falh", "instal", "encontrad", "não está", "nenhum", "falta", "errado", "driver", "RTE", "dll", "WinBio", "HID", "parad", "respond" };
            checar(foto.Resumo.All(l => assusta.All(p => !l.Contains(p, StringComparison.OrdinalIgnoreCase))),
                "Q-24 a foto do dono: nenhuma palavra no resumo que pareca erro (nao instalado, falta, driver, HID, RTE...)");
            var iDet = foto.Janela.ToList().IndexOf(DiagnosticoLeitor.TituloDetalhes);
            checar(DiagnosticoLeitor.TituloDetalhes == "Detalhes técnicos (para o suporte):" && iDet == foto.Resumo.Count + 2 && foto.Janela[iDet - 1] == ""
                   && foto.Janela.Skip(iDet + 1).SequenceEqual(foto.Tecnicas.Skip(1)) && foto.Janela.Count(l => l.StartsWith("Caixa 1.0.57")) == 1,
                "Q-25 depois do resumo: linha em branco, \"Detalhes técnicos (para o suporte):\" e as linhas tecnicas (a da versao nao repete)");
            checar(foto.Tecnicas.Contains("HID: U.are.U RTE não instalado (não faz falta, o Windows já lê)") && !string.Join("\n", foto.Janela).Contains("dpfpdd.dll nao achada")
                   && foto.Tecnicas.Contains("Motor em uso: Windows (WinBio)") && foto.Tecnicas.Any(l => l.Contains("PID_000A, classe Biometric, driver WUDFRd 5.0.0.5 (CROSSMATCH)")),
                "Q-26 com o Windows lendo, a linha do RTE da HID diz que nao faz falta (sem o caminho da dll); o resto dos detalhes continua");

            var rHid = DiagnosticoLeitor.Resumo("hid", Array.Empty<string>(), new RuntimeHid(true, @"C:\Windows\System32\dpfpdd.dll", "3.2.1", null), new ApiHidFalsa().Lista, loja);
            checar(rHid.Take(3).SequenceEqual(new[] { "Leitor funcionando.", "O caixa lê a digital pelo programa da HID.", "Não precisa fazer nada." }),
                "Q-27 motor da HID: \"Leitor funcionando.\" e le pelo programa da HID, nao precisa fazer nada");
            var tecHid = DiagnosticoLeitor.Linhas("hid", Array.Empty<string>(), 0, null, new RuntimeHid(true, @"C:\Windows\System32\dpfpdd.dll", "3.2.1", null), new ApiHidFalsa().Lista, null, loja, "1.0.57", DateTime.Now);
            checar(!tecHid.Contains(DiagnosticoLeitor.RteNaoFazFalta), "Q-28 com a HID lendo, a linha 'nao faz falta' nao aparece");

            var rErrado = DiagnosticoLeitor.Resumo(null, Array.Empty<string>(), new RuntimeHid(false, null, null, "dpfpdd.dll nao achada"), Array.Empty<DispositivoHid>(), loja);
            checar(rErrado[0] == "O leitor não está funcionando." && rErrado[1] == "Ele está plugado, mas com o driver errado."
                   && rErrado.Any(l => l.Contains("Atualizar driver")) && rErrado.Any(l => l.Contains("Com Disco"))
                   && rErrado.Any(l => l.Contains("\"U.are.U 4500 Fingerprint Reader (WBF)\"")) && rErrado.Any(l => l.Contains("Reinicie o computador")),
                "Q-29 PC da loja (driver da HID, sem o programa): nao funciona, driver errado, e o passo do WBF (Atualizar driver, Com Disco, reiniciar)");
            var rServico = DiagnosticoLeitor.Resumo(null, Array.Empty<string>(), new RuntimeHid(false, null, null, "x"), Array.Empty<DispositivoHid>(),
                new[] { new DriverDigitalPersona.Aparelho("PID_000A", "Biometric", true, "WUDFRd", "U.are.U 4500 Fingerprint Reader (WBF)") });
            checar(rServico.SequenceEqual(new[] { "O leitor não está funcionando.", "O serviço de biometria do Windows não respondeu.", "Reinicie o computador." }),
                "Q-30 WBF plugado e o Windows sem listar: o servico de biometria nao respondeu, reinicie o computador");
            var rSemDriver = DiagnosticoLeitor.Resumo(null, Array.Empty<string>(), new RuntimeHid(false, null, null, "x"), Array.Empty<DispositivoHid>(),
                new[] { new DriverDigitalPersona.Aparelho("PID_000A", "", true) });
            checar(rSemDriver[0] == "O leitor não está funcionando." && rSemDriver[1].Contains("falta o driver") && rSemDriver.Skip(2).SequenceEqual(DiagnosticoLeitor.PassoDriverWbf),
                "Q-31 plugado sem driver: nao funciona, falta o driver, e o passo");
            var rNada = DiagnosticoLeitor.Resumo(null, Array.Empty<string>(), new RuntimeHid(false, null, null, "x"), Array.Empty<DispositivoHid>(), Array.Empty<DriverDigitalPersona.Aparelho>());
            var rJaEsteve = DiagnosticoLeitor.Resumo(null, Array.Empty<string>(), new RuntimeHid(false, null, null, "x"), Array.Empty<DispositivoHid>(),
                new[] { new DriverDigitalPersona.Aparelho("PID_000A", "Biometric", false, "WUDFRd") });
            var cabo = new[] { "Nenhum leitor plugado neste caixa.", "Confira o cabo USB ou troque de porta." };
            checar(rNada.SequenceEqual(cabo) && rJaEsteve.SequenceEqual(cabo), "Q-32 nada plugado (ou so um que ja esteve aqui): nenhum leitor plugado, confira o cabo USB");
            var rHidSemAparelho = DiagnosticoLeitor.Resumo(null, Array.Empty<string>(), new RuntimeHid(true, @"C:\Windows\System32\dpfpdd.dll", "3.2.1", null), Array.Empty<DispositivoHid>(), loja);
            checar(rHidSemAparelho[0] == "O leitor não está funcionando." && rHidSemAparelho.Any(l => l.Contains("plugue de novo")),
                "Q-33 driver da HID com o programa da HID que nao acha o leitor: replugar e reiniciar");
            checar(DiagnosticoLeitor.NomeCurto("Leitora de Impressão Digital, U.are.U® 4500") == "U.are.U 4500" && DiagnosticoLeitor.NomeCurto(unidadesFoto[0]) == "U.are.U 4500"
                   && DiagnosticoLeitor.NomeCurto("") is null && DiagnosticoLeitor.NomeCurto("(WBF)") is null, "Q-34 o nome curto do leitor tira marca, numero e parenteses");
            var todosResumos = new[] { foto.Resumo, rHid, rErrado, rServico, rSemDriver, rNada, rHidSemAparelho }.SelectMany(x => x).Concat(foto.Janela).ToList();
            checar(todosResumos.All(l => !l.Contains((char)0x2014) && !l.Contains((char)0x2013)), "Q-35 o resumo e a janela nao tem travessao");
            checar(new[] { foto.Resumo, rHid, rErrado, rServico, rSemDriver, rNada, rHidSemAparelho }.SelectMany(x => x).All(l => l.Length <= 130),
                "Q-36 frases curtas: nenhuma linha do resumo passa de 130 letras");
            var confTela = File.ReadAllText(Caminho("Telas/Configuracao.xaml.cs"));
            checar(confTela.Contains("conferencia.Estado") && confTela.Contains("conferencia.Janela") && !confTela.Contains("linhas[1]") && confTela.Contains("\"Leitor de digital\""),
                "Q-37 a Configuracao mostra a primeira frase do resumo na linha de estado e a janela inteira no relatorio");

            // ── R. a API de verdade, sem leitor ─────────────────────────────────────
            var real = new ApiHidUareU();
            RuntimeHid rt = new(false, null, null, "nao rodou");
            var lancou = false;
            try { rt = real.Carregar(); } catch { lancou = true; }
            checar(!lancou && (rt.Carregado || (rt.Erro ?? "").Contains("dpfpdd.dll")), $"R-01 carregar o runtime da HID nao lanca (aqui: {(rt.Carregado ? "carregou " + rt.Caminho + " " + rt.Versao : rt.Erro)})");
            var sondaReal = await new LeitorHidUareU(real, () => Array.Empty<MoldeGuardado>()).SondarAsync(CancellationToken.None);
            checar(rt.Carregado || sondaReal.Estado == EstadoLeitor.SemLeitor, $"R-02 a sonda real sem runtime diz sem leitor ({sondaReal.Detalhe})");
            checar(ApiHidUareU.PastasCandidatas().Any(p => p.EndsWith(@"DigitalPersona\U.are.U SDK\Windows\Lib\x64", StringComparison.OrdinalIgnoreCase))
                   && ApiHidUareU.PastasCandidatas().Contains(Environment.SystemDirectory, StringComparer.OrdinalIgnoreCase), "R-03 procura no SDK (Lib\\x64), no System32 e no PATH");
            checar(ApiHidUareU.TamanhoDevInfo == 1452 && ApiHidUareU.TamanhoDevInfoMarshal == 1452 && ApiHidUareU.DeslocamentoVid == 1412,
                "R-04 DPFPDD_DEV_INFO como na dpfpdd.h: 1452 bytes, vendor_id em 1412 (a conferir com o leitor)");
            checar(ApiHidUareU.TamanhoCaptureParam == 16 && ApiHidUareU.TamanhoCaptureResult == 36 && ApiHidUareU.TamanhoVersao == 28,
                "R-05 DPFPDD_CAPTURE_PARAM 16, DPFPDD_CAPTURE_RESULT 36, DPFPDD_VERSION 28");
            var buf = new byte[ApiHidUareU.TamanhoDevInfo * 2];
            Escrever(buf, 0, @"\\?\usb#vid_05ba&pid_000a#5&1", "DigitalPersona, Inc.", "U.are.U 4500 Fingerprint Reader", "{ABC}", 0x05BA, 0x000A);
            Escrever(buf, ApiHidUareU.TamanhoDevInfo, "dev2", "HID", "4500", "", 0x05BA, 0x000B);
            var lidosDev = ApiHidUareU.LerDispositivos(buf, 2);
            checar(lidosDev.Count == 2 && lidosDev[0].Nome == @"\\?\usb#vid_05ba&pid_000a#5&1" && lidosDev[0].Produto == "U.are.U 4500 Fingerprint Reader"
                   && lidosDev[0].Vid == 0x05BA && lidosDev[0].Pid == 0x000A && lidosDev[1].Pid == 0x000B, "R-06 a leitura do vetor de DPFPDD_DEV_INFO (nome, produto, VID, PID)");
            checar(ApiHidUareU.ErroHid(0x0d) == 0x05BA000D && ApiHidUareU.Texto(0x05BA000D).Contains("MORE_DATA") && ApiHidUareU.Texto(0x05BA001E).Contains("ocupado")
                   && ApiHidUareU.Texto(0x05BA0999).StartsWith("HID 0x"), "R-07 os codigos da HID (facility 05BA) viram uma linha");
            checar(ApiHidUareU.TextoQualidade(4).Contains("sem dedo") && ApiHidUareU.TextoQualidade(1 << 8).Contains("centro"), "R-08 a qualidade da captura vira palavra");

            var fonte = File.ReadAllText(Caminho("Pdv.Nucleo/LeitorHidUareU.cs"));
            checar(new[] { "dpfpdd_init", "dpfpdd_query_devices", "dpfpdd_open", "dpfpdd_capture", "dpfpdd_get_device_capabilities", "dpfpdd_cancel", "dpfpdd_close",
                           "dpfj_create_fmd_from_fid", "dpfj_compare", "dpfj_start_enrollment", "dpfj_add_to_enrollment", "dpfj_create_enrollment_fmd", "dpfj_finish_enrollment" }
                       .All(f => fonte.Contains("\"" + f + "\"")) && fonte.Contains("a conferir com o leitor") && fonte.Contains("dpfpdd.h") && fonte.Contains("dpfj.h"),
                "R-09 as assinaturas usadas estao documentadas com a fonte e marcadas a conferir");
            var csproj = File.ReadAllText(Caminho("Pdv.csproj")) + File.ReadAllText(Caminho("Pdv.Nucleo/Pdv.Nucleo.csproj"));
            var instalador = string.Join("\n", Directory.GetFiles(Caminho("Pdv.Instalador"), "*.cs*", SearchOption.TopDirectoryOnly).Select(File.ReadAllText))
                             + File.ReadAllText(Caminho("scripts/gerar-instalador.ps1"));
            checar(!csproj.Contains("dpfpdd", StringComparison.OrdinalIgnoreCase) && !csproj.Contains("dpfj", StringComparison.OrdinalIgnoreCase)
                   && !instalador.Contains("dpfpdd", StringComparison.OrdinalIgnoreCase), "R-10 nenhuma DLL da HID no projeto nem no instalador (vem do RTE do PC)");

            // ── S. a ligacao nas telas e a versao ───────────────────────────────────
            var servicos = File.ReadAllText(Caminho("Servicos.cs"));
            checar(servicos.Contains("new Pdv.Nucleo.LeitorAutomatico(") && servicos.Contains("new Pdv.Nucleo.LeitorHidUareU(") && servicos.Contains("Digitais.Moldes("),
                "S-01 o caixa usa a escolha automatica (WinBio, depois HID com os moldes da guarda)");
            var telaDig = File.ReadAllText(Caminho("Telas/TelaDigital.cs"));
            checar(telaDig.Contains("cad.Motor") && telaDig.Contains("cad.Molde") && telaDig.Contains("Digitais.DonoDaParecida("), "S-02 o cadastro guarda motor e molde e barra a digital de outra pessoa");
            var login = File.ReadAllText(Caminho("Telas/Login.xaml.cs"));
            var bio = File.ReadAllText(Caminho("Pdv.Nucleo/Biometria.cs"));
            checar(bio.Contains("Digitais.JaTem(cx, cpf, _motor())") && telaDig.Contains("Servicos.MotorDoLeitor") && login.Contains("PortaoDigitalDaTela.EntrarAsync("), "S-03 ja tem digital olha o motor em uso (o primeiro cadastro, 1.0.54)");
            var conf = File.ReadAllText(Caminho("Telas/Configuracao.xaml.cs"));
            checar(conf.Contains("DiagnosticoLeitor.ColherAsync(") && conf.Contains("Dialogo.Relatorio("), "S-04 Conferir o leitor mostra o diagnostico completo numa janela para fotografar");
            checar(File.ReadAllText(Caminho("Pdv.csproj")).Contains("<Version>1.0.60</Version>"), "S-05 a versao do caixa e 1.0.60 (chat sai da ajuda presa)");
            var novos = new[] { "Pdv.Nucleo/LeitorHidUareU.cs", "Pdv.Nucleo/LeitorAutomatico.cs", "Pdv.Testes/TestesLeitorHid.cs" }.Select(a => File.ReadAllText(Caminho(a)));
            checar(novos.All(t => !t.Contains((char)0x2014) && !t.Contains((char)0x2013)), "S-06 os arquivos novos nao tem travessao");
        }
        finally
        {
            cx.Close();
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }

    private static void Escrever(byte[] buf, int off, string nome, string fab, string prod, string serie, ushort vid, ushort pid)
    {
        BitConverter.GetBytes((uint)ApiHidUareU.TamanhoDevInfo).CopyTo(buf, off);
        System.Text.Encoding.ASCII.GetBytes(nome).CopyTo(buf, off + 4);
        System.Text.Encoding.ASCII.GetBytes(fab).CopyTo(buf, off + 1028);
        System.Text.Encoding.ASCII.GetBytes(prod).CopyTo(buf, off + 1156);
        System.Text.Encoding.ASCII.GetBytes(serie).CopyTo(buf, off + 1284);
        BitConverter.GetBytes(vid).CopyTo(buf, off + 1412);
        BitConverter.GetBytes(pid).CopyTo(buf, off + 1414);
    }

    /// <summary>Tela que so le (nenhum botao tocado).</summary>
    private sealed class TelaQueLe : ITelaDigital
    {
        public async Task<LeituraNaTela> LerAsync(string titulo, string? aviso, bool ofertaPrimeiraVez, Func<CancellationToken, Task<Captura>> ler)
            => new(await ler(CancellationToken.None));
        public Task<Cadastro> CadastrarAsync(string titulo, Func<IProgress<string>, CancellationToken, Task<Cadastro>> cadastrar) => cadastrar(new Progress<string>(), CancellationToken.None);
        public Task<string?> PedirCpfAsync(string titulo, string? aviso) => Task.FromResult<string?>(null);
        public void Avisar(string titulo, string texto) { }
    }

    private sealed class TotpNada : ITelaAutorizacao
    {
        private sealed class Nada : IDisposable { public void Dispose() { } }
        public IDisposable Aguardando(string mensagem) => new Nada();
        public Task<string?> PedirCodigoAsync(string? aviso, string nivel) => Task.FromResult<string?>(null);
    }

    private static string Caminho(string relativo)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Pdv.csproj"))) return Path.Combine(dir, relativo);
            dir = Path.GetDirectoryName(dir);
        }
        return relativo;
    }
}
