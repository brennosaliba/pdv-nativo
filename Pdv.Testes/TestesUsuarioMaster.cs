using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Instalador;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// USUÁRIO MASTER DA REDE (15/09/2026; decisão do dono em 14/09).
///
/// No Castelo a senha do Lucas, funcionário do caixa, abriu a Configuração: a senha de admin era
/// uma cópia da senha de uma pessoa (a linha `_admin_`) e nada a trocava. Agora o master é da
/// empresa, criado no painel, desce no Atualizar, fica guardado e é a ÚNICA senha das ações de admin.
/// Sem master, vale a senha da instalação. Operador nunca.
///
/// Prova pelo valor (vetor do painel, JSON da RPC), num SQLite de verdade, pelo Atualizar contra o
/// PostgREST falso, no instalador (apagar dados) e pela fiação no fonte.
/// </summary>
public static class TestesUsuarioMaster
{
    private const int Porta = 4693;

    // O MESMO vetor de erp-american-day src/test/pinCaixa.test.ts
    private const string HashVetor = "zXEe8seaNwtLmNvAYvfpAmiMOk6AXt6Jn4slCkkKXHE=";
    private const string SalVetor = "AQIDBAUGBwgJCgsMDQ4PEA==";

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        Vetor(checar);
        Leitura(checar);
        NoBanco(checar);
        await PeloAtualizar(checar);
        NoInstalador(checar);
        Fiacao(checar);
    }

    private static string Resposta(string nome, string hash, string salt, string em)
        => JsonSerializer.Serialize(new[] { new { nome, senha_hash = hash, senha_salt = salt, atualizado_em = em } });

    private static UsuarioMaster.Master Novo(string nome, string senha, DateTime emUtc)
    {
        var (h, s) = HashDeSenha.Gerar(senha);
        return new UsuarioMaster.Master(nome, h, s, DateTime.SpecifyKind(emUtc, DateTimeKind.Utc));
    }

    private static void GravarAdminDaInstalacao(SqliteConnection cx, string senha)
    {
        var (h, s) = Operadores.GerarHash(senha);
        cx.Execute("""
            INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,ativo,atualizado)
            VALUES ('_admin_','Administrador',@H,@S,'gerente',0,'x')
            ON CONFLICT(id) DO UPDATE SET pin_hash=@H, pin_salt=@S
            """, new { H = h, S = s });
    }

    // ── o cálculo é o do painel ────────────────────────────────────────────────
    private static void Vetor(Action<bool, string> checar)
    {
        var sal = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        checar(HashDeSenha.Calcular("1234", sal) == HashVetor && Convert.ToBase64String(sal) == SalVetor,
            "o hash do caixa bate com o do painel (senha 1234, sal 01..10)");
        checar(HashDeSenha.Confere("1234", HashVetor, SalVetor) && !HashDeSenha.Confere("1235", HashVetor, SalVetor),
            "a senha certa confere com o hash feito no navegador; um dígito errado não");
        checar(!HashDeSenha.Confere("", HashVetor, SalVetor) && !HashDeSenha.Confere(null, HashVetor, SalVetor)
               && !HashDeSenha.Confere("1234", "torto", SalVetor) && !HashDeSenha.Confere("1234", HashVetor, null),
            "senha vazia, hash torto ou sal ausente nunca conferem (nem lançam)");
        var (h, s) = Operadores.GerarHash("4321");
        checar(Operadores.Confere("4321", h, s) && HashDeSenha.Confere("4321", h, s) && HashDeSenha.FormatoValido(h, s),
            "o PIN do operador usa o mesmo cálculo (um lugar só) e no formato que o painel grava");
    }

    // ── o JSON da RPC ──────────────────────────────────────────────────────────
    private static void Leitura(Action<bool, string> checar)
    {
        var m = UsuarioMaster.Ler(Resposta("Brenno", HashVetor, SalVetor, "2026-09-15T01:10:00+00:00"));
        checar(m is { Nome: "Brenno" } && m.Hash == HashVetor && m.Salt == SalVetor
               && m.Em == new DateTime(2026, 9, 15, 1, 10, 0, DateTimeKind.Utc),
            "a RPC vira master com nome, hash, sal e data (em UTC)");
        checar(UsuarioMaster.Ler("""{"nome":"Brenno","senha_hash":"zXEe8seaNwtLmNvAYvfpAmiMOk6AXt6Jn4slCkkKXHE=","senha_salt":"AQIDBAUGBwgJCgsMDQ4PEA==","atualizado_em":"2026-09-15T01:10:00Z"}""") is not null,
            "objeto solto também é lido");
        checar(UsuarioMaster.Ler("[]") is null, "painel sem master (zero linhas): nada");
        checar(UsuarioMaster.Ler(null) is null && UsuarioMaster.Ler("nao e json") is null && UsuarioMaster.Ler("{}") is null
               && UsuarioMaster.Ler("[1]") is null,
            "JSON torto: nada, nunca exceção");
        checar(UsuarioMaster.Ler(Resposta("Brenno", "curto", SalVetor, "2026-09-15T01:10:00Z")) is null
               && UsuarioMaster.Ler(Resposta("Brenno", HashVetor, "AQID", "2026-09-15T01:10:00Z")) is null,
            "hash ou sal fora do formato não é guardado (trancaria a Configuração de todo mundo)");
        checar(UsuarioMaster.Ler(Resposta("  ", HashVetor, SalVetor, "2026-09-15T01:10:00Z")) is null
               && UsuarioMaster.Ler(Resposta("Brenno", HashVetor, SalVetor, "ontem")) is null,
            "sem nome ou sem data legível: nada");
        var duas = "[" + Resposta("A", HashVetor, SalVetor, "2026-09-15T01:10:00Z").Trim('[', ']') + ","
                   + Resposta("B", HashVetor, SalVetor, "2026-09-15T01:10:00Z").Trim('[', ']') + "]";
        checar(UsuarioMaster.Ler(duas) is null, "duas linhas: não se escolhe no escuro");
    }

    // ── num SQLite de verdade ──────────────────────────────────────────────────
    private static void NoBanco(Action<bool, string> checar)
    {
        var db = Path.Combine(Path.GetTempPath(), "master-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        try
        {
            Banco.Migrar(db);
            using (var cx = Banco.Abrir(db))
            {
                // o que a instalação deixa: o dono (operador) e a `_admin_` com a mesma senha
                Operadores.Salvar(cx, "dono-local", "Dono", "4321", "gerente", "529.982.247-25");
                GravarAdminDaInstalacao(cx, "4321");
                // o gerente que veio do painel e uma operadora comum
                Operadores.Salvar(cx, "lucas", "Lucas", "5555", "gerente");
                Operadores.Salvar(cx, "bia", "Bia", "6666", "operador");

                // ── sem master: a senha da instalação, e só ela ──
                var c = UsuarioMaster.Conferir(cx, "4321");
                checar(c.Liberado && c.Via == UsuarioMaster.Via.Instalador,
                    "sem master: a senha criada na instalação abre a Configuração");
                checar(!UsuarioMaster.Conferir(cx, "5555").Liberado,
                    "sem master: a senha do operador GERENTE não abre a Configuração");
                checar(!UsuarioMaster.Conferir(cx, "6666").Liberado,
                    "sem master: a senha da operadora comum não abre a Configuração");
                checar(!UsuarioMaster.Conferir(cx, "").Liberado && !UsuarioMaster.Conferir(cx, null).Liberado,
                    "senha vazia não abre nada");
                checar(UsuarioMaster.Rotulo(cx) == "Senha de administrador" && UsuarioMaster.Estado(cx).Contains("Ainda sem usuário master"),
                    "sem master: a caixa pede a senha de administrador e a Configuração diz que vale a da instalação");

                // ── o Castelo: a `_admin_` virou cópia da senha do Lucas ──
                GravarAdminDaInstalacao(cx, "5555");
                checar(UsuarioMaster.Conferir(cx, "5555").Liberado,
                    "(montagem) sem master, a cópia na _admin_ abre com a senha do Lucas: foi o Castelo");

                // ── o master chega pelo Atualizar ──
                var t1 = new DateTime(2026, 9, 15, 1, 0, 0, DateTimeKind.Utc);
                var master = Novo("Brenno", "9090", t1);
                checar(UsuarioMaster.Aplicar(cx, master) == "usuário master", "o master do painel é guardado e o resumo diz");
                var cm = UsuarioMaster.Conferir(cx, "9090");
                checar(cm.Liberado && cm.Via == UsuarioMaster.Via.Master && cm.Nome == "Brenno",
                    "com master: a senha dele abre, e a auditoria sabe que foi o master");
                checar(!UsuarioMaster.Conferir(cx, "5555").Liberado,
                    "com master: a senha do Lucas não abre mais, mesmo copiada na _admin_");
                checar(!UsuarioMaster.Conferir(cx, "4321").Liberado,
                    "com master: a senha da instalação deixa de valer");
                checar(!UsuarioMaster.Conferir(cx, "6666").Liberado, "com master: operadora comum também não");
                checar(UsuarioMaster.Rotulo(cx) == "Senha do usuário master"
                       && UsuarioMaster.NaoConfere(cx) == "A senha do usuário master não confere."
                       && UsuarioMaster.Estado(cx).Contains("Brenno"),
                    "com master: a caixa pede a senha do usuário master e a Configuração diz quem é");

                checar(UsuarioMaster.Aplicar(cx, master) == "", "o mesmo master de novo: nada muda (o Atualizar passa a toda hora)");
                checar(UsuarioMaster.Aplicar(cx, null) == "" && UsuarioMaster.Conferir(cx, "9090").Liberado,
                    "painel sem resposta ou sem master: o guardado continua (voltar à senha da instalação reabriria a porta)");

                // ── troca no painel ──
                var trocado = Novo("Brenno", "7070", t1.AddHours(3));
                checar(UsuarioMaster.Aplicar(cx, trocado) == "usuário master"
                       && UsuarioMaster.Conferir(cx, "7070").Liberado && !UsuarioMaster.Conferir(cx, "9090").Liberado,
                    "troca no painel vale no Atualizar seguinte: a nova abre, a antiga não");

                var detalhes = cx.Query<string>("SELECT detalhe FROM auditoria WHERE evento = 'master_do_painel'").ToList();
                checar(detalhes.Count == 2, $"auditoria: uma linha por master que chegou ({detalhes.Count})");
                checar(detalhes.All(d => !d.Contains(trocado.Hash) && !d.Contains(trocado.Salt) && !d.Contains(master.Hash)),
                    "auditoria sem hash nem sal");
            }

            SqliteConnection.ClearAllPools();
            using (var cx2 = Banco.Abrir(db))
                checar(UsuarioMaster.Conferir(cx2, "7070").Liberado && !UsuarioMaster.Conferir(cx2, "5555").Liberado,
                    "o master fica no banco do caixa: vale depois de reabrir, sem internet");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { db, db + "-wal", db + "-shm" }) try { File.Delete(f); } catch { }
        }
    }

    // ── pelo Atualizar, contra o PostgREST falso ──────────────────────────────
    private static async Task PeloAtualizar(Action<bool, string> checar)
    {
        var db = Path.Combine(Path.GetTempPath(), "master-nuvem-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        try
        {
            Banco.Migrar(db);
            using var cx = Banco.Abrir(db);
            GravarAdminDaInstalacao(cx, "4321");

            using var fake = new FakePostgrest(Porta);
            var nuvem = new Nuvem(fake.Url);
            checar(await nuvem.EntrarAsync("master@teste.com", "x"), "nuvem fake autentica");

            fake.MasterDoPainel = null;   // servidor sem a migration: 404
            checar(await nuvem.BaixarMasterAsync(cx) is null && UsuarioMaster.Conferir(cx, "4321").Liberado,
                "servidor sem a RPC (404): o caixa segue com a senha da instalação");

            fake.MasterDoPainel = "[]";
            checar(await nuvem.BaixarMasterAsync(cx) == "" && UsuarioMaster.Conferir(cx, "4321").Liberado,
                "instalação nova, painel sem master: vale a senha da instalação");

            var (h, s) = HashDeSenha.Gerar("9090");
            fake.MasterDoPainel = Resposta("Brenno", h, s, "2026-09-15T02:00:00+00:00");
            checar(await nuvem.BaixarMasterAsync(cx) == "usuário master"
                   && UsuarioMaster.Conferir(cx, "9090").Liberado && !UsuarioMaster.Conferir(cx, "4321").Liberado,
                "o master desce no Atualizar e a senha da instalação para de valer");

            fake.MasterDoPainel = null;
            checar(await nuvem.BaixarMasterAsync(cx) is null && UsuarioMaster.Conferir(cx, "9090").Liberado,
                "painel fora do ar depois: o master guardado continua valendo");

            checar(fake.ChamadasPorRpc.TryGetValue("pdv_master_caixa", out var n) && n == 4,
                $"o caixa pergunta pela RPC pdv_master_caixa ({n} chamadas)");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { db, db + "-wal", db + "-shm" }) try { File.Delete(f); } catch { }
        }
    }

    // ── o instalador: apagar os dados ──────────────────────────────────────────
    private static void NoInstalador(Action<bool, string> checar)
    {
        var pasta = Path.Combine(Path.GetTempPath(), "master-dados-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            checar(Instalacao.LerMaster(pasta) is null && Instalacao.MasterLibera(null, null),
                "sem banco: nada a conferir, o apagar segue as perguntas de sempre");

            Directory.CreateDirectory(pasta);
            var db = Path.Combine(pasta, "pdv.db");
            Banco.Migrar(db);
            using (var cx = Banco.Abrir(db)) GravarAdminDaInstalacao(cx, "4321");
            SqliteConnection.ClearAllPools();
            checar(Instalacao.LerMaster(pasta) is null, "banco sem master: o instalador não pede senha");

            using (var cx = Banco.Abrir(db)) UsuarioMaster.Aplicar(cx, Novo("Brenno", "9090", new DateTime(2026, 9, 15, 3, 0, 0)));
            SqliteConnection.ClearAllPools();
            var m = Instalacao.LerMaster(pasta);
            checar(m is { Nome: "Brenno" }, "o instalador lê o master do banco do caixa");
            checar(Instalacao.MasterLibera(m, "9090") && !Instalacao.MasterLibera(m, "4321")
                   && !Instalacao.MasterLibera(m, "") && !Instalacao.MasterLibera(m, null),
                "com master, só a senha dele libera apagar os dados (a da instalação não)");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(pasta, true); } catch { }
        }
    }

    // ── a fiação, pelo fonte ───────────────────────────────────────────────────
    private static void Fiacao(Action<bool, string> checar)
    {
        var main = Fonte("MainWindow.xaml.cs") ?? "";
        var porta = Trecho(main, "private bool AcessoDoMaster(", 2000);
        checar(porta.Contains("UsuarioMaster.Conferir(", StringComparison.Ordinal)
               && !porta.Contains("Operadores.", StringComparison.Ordinal)
               && !porta.Contains("FROM operador", StringComparison.Ordinal),
            "a porta das ações de admin confere pelo UsuarioMaster e nunca consulta operador");
        checar(Trecho(main, "private void AbrirConfigProtegida()", 400).Contains("AcessoDoMaster(", StringComparison.Ordinal),
            "a Configuração (login, venda e menu do TEF chegam por aqui) passa pela porta do master");
        var fechar = Trecho(main, "private void Fechar(object sender", 2000);
        var sair = fechar.IndexOf("else if (o == 1)", StringComparison.Ordinal);
        var ramo = sair < 0 ? "" : fechar[sair..];
        var portaNoRamo = ramo.IndexOf("AcessoDoMaster(", StringComparison.Ordinal);
        var explorer = ramo.IndexOf("Quiosque.AbrirExplorer()", StringComparison.Ordinal);
        checar(portaNoRamo >= 0 && explorer > portaNoRamo,
            "sair do quiosque para o Windows pede o master antes de abrir a área de trabalho");

        var raiz = Raiz();
        var leitores = raiz is null ? new List<string>() : Directory
            .EnumerateFiles(raiz, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "Pdv.Testes" + Path.DirectorySeparatorChar))
            .Where(f => { var t = File.ReadAllText(f); return t.Contains("SenhaAdminConfere", StringComparison.Ordinal)
                                                             || t.Contains("SELECT pin_hash, pin_salt FROM operador WHERE id = '_admin_'", StringComparison.Ordinal); })
            .Select(Path.GetFileName).ToList()!;
        checar(raiz is not null && leitores.Count == 1 && leitores[0] == "UsuarioMaster.cs",
            $"só o UsuarioMaster lê a senha da instalação para liberar admin (achados: {string.Join(", ", leitores)})");

        var cfg = Fonte(Path.Combine("Telas", "Configuracao.xaml.cs")) ?? "";
        var xaml = Fonte(Path.Combine("Telas", "Configuracao.xaml")) ?? "";
        checar(cfg.Contains("TxtAvisoMaster.Text = UsuarioMaster.AvisoInstalador", StringComparison.Ordinal)
               && xaml.Contains("x:Name=\"TxtAvisoMaster\"", StringComparison.Ordinal),
            "o assistente diz, embaixo da senha, que ela vale só até o Atualizar trazer o master");
        checar(cfg.Contains("TxtMaster.Text = UsuarioMaster.Estado(cx)", StringComparison.Ordinal)
               && xaml.Contains("x:Name=\"TxtMaster\"", StringComparison.Ordinal),
            "reconfigurando, a Configuração diz qual senha vale neste caixa");
        checar(!xaml.Contains("a senha dele passa a ser a senha desta tela", StringComparison.Ordinal),
            "a frase antiga (senha do dono é a senha desta tela) saiu");

        var nuvem = Fonte(Path.Combine("Pdv.Nucleo", "Nuvem.cs")) ?? "";
        var sinc = Fonte(Path.Combine("Pdv.Nucleo", "Sincronizacao.cs")) ?? "";
        checar(UsuarioMaster.Rpc == "pdv_master_caixa" && nuvem.Contains("\"/rest/v1/rpc/\" + UsuarioMaster.Rpc", StringComparison.Ordinal),
            "a Nuvem chama a RPC pdv_master_caixa");
        var iCfg = sinc.IndexOf("await nuvem.BaixarConfigLojaAsync(cx)", StringComparison.Ordinal);
        var iMaster = sinc.IndexOf("await nuvem.BaixarMasterAsync(cx)", StringComparison.Ordinal);
        checar(iCfg >= 0 && iMaster > iCfg, "o Atualizar (Sincronizacao) baixa o master junto da config da loja");

        // revisão 15/09: a senha por loja do painel (admin_pin_*) é gravável por gerente pela API.
        // Nenhum caminho da nuvem escreve na `_admin_`: um caixa ainda sem master abriria a
        // Configuração com a senha que o gerente escolheu.
        var cfgPainel = Fonte(Path.Combine("Pdv.Nucleo", "ConfigLojaPainel.cs")) ?? "";
        var codigoNuvem = string.Join("\n", nuvem, sinc, cfgPainel)
            .Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
        checar(cfgPainel.Length > 0 && !codigoNuvem.Any(l => l.Contains("_admin_", StringComparison.Ordinal)),
            "nada que desce do painel escreve na senha da instalação (_admin_)");

        var app = Fonte(Path.Combine("Pdv.Instalador", "App.xaml.cs")) ?? "";
        var janela = Fonte(Path.Combine("Pdv.Instalador", "JanelaInstalador.xaml.cs")) ?? "";
        var csproj = Fonte(Path.Combine("Pdv.Instalador", "Pdv.Instalador.csproj")) ?? "";
        var pergunta = Trecho(app, "internal static bool PerguntarApagarDados(", 2500);
        var iLer = pergunta.IndexOf("Instalacao.LerMaster(Instalacao.PastaDados)", StringComparison.Ordinal);
        var iLibera = pergunta.IndexOf("Instalacao.MasterLibera(", StringComparison.Ordinal);
        var iAviso = pergunta.IndexOf("Instalacao.AvisoAntesDeApagar(", StringComparison.Ordinal);
        checar(iLer >= 0 && iLibera > iLer && iAviso > iLibera,
            "apagar os dados pergunta a senha do master antes de liberar");
        checar(app.Contains("var apagarDados = PerguntarApagarDados();", StringComparison.Ordinal)
               && janela.Contains("App.PerguntarApagarDados(this)", StringComparison.Ordinal),
            "os dois caminhos que apagam dados (desinstalar e começar do zero) passam pela pergunta");
        checar(csproj.Contains("Include=\"..\\Pdv.Nucleo\\HashDeSenha.cs\"", StringComparison.Ordinal),
            "o instalador compila o MESMO arquivo de hash do caixa");

        using var semBanco = new SqliteConnection("Data Source=:memory:");
        var textos = new[]
        {
            UsuarioMaster.AvisoInstalador, "Senha do usuário master", "Senha de administrador",
            "A senha do usuário master não confere.", "A senha de administrador não confere.",
            Trecho(app, "private static string? PedirSenhaMaster(", 3000), Trecho(app, "const string recusa", 200),
        };
        checar(textos.All(t => !t.Contains('—') && !t.Contains('–')), "texto de tela novo sem travessão");
    }

    private static string Trecho(string fonte, string inicio, int tamanho)
    {
        var i = fonte.IndexOf(inicio, StringComparison.Ordinal);
        return i < 0 ? "" : fonte[i..Math.Min(fonte.Length, i + tamanho)];
    }

    private static string? Raiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj"))) return dir.FullName;
        return null;
    }

    private static string? Fonte(string relativo)
    {
        var raiz = Raiz();
        if (raiz is null) return null;
        var c = Path.Combine(raiz, relativo);
        return File.Exists(c) ? File.ReadAllText(c) : null;
    }
}
