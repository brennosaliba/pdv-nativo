using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A DIGITAL NO LUGAR DA SENHA (1.0.52, 26/10/2026).
///
/// O que esta suite protege, em ordem de estrago:
///  · a maquina de estados do portao (PortaoDigital) em todos os caminhos, sem leitor e sem
///    janela: reconheceu, 3 leituras sem reconhecer -> reserva, leitor ausente -> reserva,
///    cancelou, primeira vez, digital desconhecida, demitido nao entra, reserva recusada;
///  · a reserva e o TOTP do gerente (tipo turno_sem_digital, nivel gerente) + a escolha na
///    lista, e tudo vira motivo "pela reserva: ...";
///  · a guarda local: CPF cifrado pelo DPAPI (nunca em claro no banco), hash estavel, um CPF
///    uma digital, vagas 1..10, apagar deixa a remocao pendente para o ERP;
///  · o turno grava via/cpf_hash na abertura e no fechamento e a fila sobe as colunas (e cai
///    para o corpo de sempre quando a nuvem ainda nao tem o SQL 224: PGRST204);
///  · a chave do painel: nasce LIGADA; o painel so desliga; a marca do painel so com a chave;
///  · o registro no ERP: corpo da RPC sem CPF em claro, 404 = fica pendente;
///  · o WinBio de verdade carrega nesta maquina (enumeracao sem travar; sem leitor = vazio);
///  · o diagnostico do driver (U.are.U 4500, USB 05BA) e puro e diz a frase certa;
///  · os textos de tela sem travessao, e a versao 1.0.52.
/// </summary>
public static class TestesBiometria
{
    // ── o leitor de mentira: roteirizavel ─────────────────────────────────────
    private sealed class LeitorFalso : ILeitorDigital
    {
        public string Nome => "falso";
        public EstadoLeitor Estado = EstadoLeitor.Pronto;
        public Queue<Captura> Leituras = new();
        public Cadastro ProximoCadastro = Cadastro.Feito("winbio:3:12:aabb:dedo:1", 3);
        public int Sondas, Identificacoes, Cadastros;
        public List<int> VagasPedidas = new();
        public List<string> Apagadas = new();
        public int AmostrasDoCadastro = 3;
        /// <summary>Segura a leitura ate o token cancelar (para o botao da tela).</summary>
        public bool EsperaCancelamento;

        public Task<SondaLeitor> SondarAsync(CancellationToken ct)
        {
            Sondas++;
            return Task.FromResult(new SondaLeitor(Estado, Estado.ToString(), Estado == EstadoLeitor.Pronto ? new[] { "#1 Leitor falso" } : Array.Empty<string>()));
        }

        public async Task<Captura> IdentificarAsync(CancellationToken ct)
        {
            Identificacoes++;
            if (EsperaCancelamento)
            {
                try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
                return Captura.Cancelada;
            }
            return Leituras.Count > 0 ? Leituras.Dequeue() : Captura.NaoReconhecida;
        }

        public Task<Cadastro> CadastrarAsync(int vaga, IProgress<int>? amostra, CancellationToken ct)
        {
            Cadastros++;
            VagasPedidas.Add(vaga);
            if (ProximoCadastro.Resultado == ResultadoCadastro.Ok)
                for (var i = 1; i <= AmostrasDoCadastro; i++) amostra?.Report(i);
            return Task.FromResult(ProximoCadastro);
        }

        public Task<bool> ApagarAsync(string chave, CancellationToken ct) { Apagadas.Add(chave); return Task.FromResult(true); }
    }

    // ── a tela de mentira ─────────────────────────────────────────────────────
    private sealed class TelaFalsa : ITelaDigital
    {
        public readonly List<string> Titulos = new();
        public readonly List<string?> Avisos = new();
        public readonly List<bool> OfertasPrimeiraVez = new();
        public readonly List<(string Titulo, string Texto)> AvisosDeCaixa = new();
        public readonly List<string> Progresso = new();
        public Queue<EscolhaNaLeitura> Escolhas = new();
        public Func<IReadOnlyList<Operador>, Operador?> AoEscolher = l => l.FirstOrDefault();
        public int VezesEscolheu;

        public async Task<LeituraNaTela> LerAsync(string titulo, string? aviso, bool ofertaPrimeiraVez, Func<CancellationToken, Task<Captura>> ler)
        {
            Titulos.Add(titulo); Avisos.Add(aviso); OfertasPrimeiraVez.Add(ofertaPrimeiraVez);
            var escolha = Escolhas.Count > 0 ? Escolhas.Dequeue() : EscolhaNaLeitura.Nenhuma;
            using var cts = new CancellationTokenSource();
            if (escolha != EscolhaNaLeitura.Nenhuma) cts.Cancel();   // o botao da tela cancela a leitura
            var c = await ler(cts.Token);
            return new LeituraNaTela(c, escolha);
        }

        public Task<Cadastro> CadastrarAsync(string titulo, Func<IProgress<string>, CancellationToken, Task<Cadastro>> cadastrar)
        {
            Titulos.Add(titulo);
            return cadastrar(new PortaoDigital.ProgressoDireto<string>(Progresso.Add), CancellationToken.None);
        }

        public Task<Operador?> EscolherFuncionarioAsync(string titulo, IReadOnlyList<Operador> lista)
        {
            VezesEscolheu++;
            Titulos.Add(titulo);
            return Task.FromResult(AoEscolher(lista));
        }

        public void Avisar(string titulo, string texto) => AvisosDeCaixa.Add((titulo, texto));
    }

    // ── a nuvem do TOTP de mentira (sem HTTP): aprova ou recusa conforme roteiro ──
    private sealed class RemotaFalsa : IAutorizacaoRemota
    {
        public bool Aprova = true;
        public bool SemInternet;
        public readonly List<(string Codigo, string Tipo, string Nivel, string Referencia)> Chamadas = new();
        public Task<RespostaTotp> ValidarTotpAsync(string codigo, string referencia, string tipo,
            IReadOnlyDictionary<string, object?>? detalhe, string nivel, CancellationToken ct)
        {
            Chamadas.Add((codigo, tipo, nivel, referencia));
            if (SemInternet) return Task.FromResult(new RespostaTotp(false, false, "sem_resposta", null, null));
            return Task.FromResult(Aprova
                ? new RespostaTotp(true, true, null, "log-0001-abcd", "Marcos")
                : new RespostaTotp(false, true, Autorizacao.MotivoCodigoInvalido, null, null));
        }
    }

    private sealed class TelaTotpFalsa : ITelaAutorizacao
    {
        public int Pedidos;
        public string? Codigo = "287082";
        public readonly List<string> Niveis = new();
        private sealed class Nada : IDisposable { public void Dispose() { } }
        public IDisposable Aguardando(string mensagem) => new Nada();
        public Task<string?> PedirCodigoAsync(string? aviso, string nivel) { Pedidos++; Niveis.Add(nivel); return Task.FromResult(Codigo); }
    }

    private static readonly Operador Ana = new("op-ana", "ANA", "operador");
    private static readonly Operador Bia = new("op-bia", "BIA", "gerente");
    private const string CpfAna = "52998224725";
    private const string CpfBia = "11144477735";

    private static SqliteConnection BancoLimpo(out string arquivo)
    {
        arquivo = Path.Combine(Path.GetTempPath(), $"biometria-{Guid.NewGuid():N}.db");
        Banco.Migrar(arquivo);
        var cx = Banco.Abrir(arquivo);
        var (h, s) = Operadores.GerarHash("4725");
        cx.Execute("""
            INSERT INTO operador (id, nome, pin_hash, pin_salt, perfil, cpf, ativo, da_nuvem, atualizado)
            VALUES ('op-ana','ANA',@H,@S,'operador',@C1,1,1,@Em), ('op-bia','BIA',@H,@S,'gerente',@C2,1,1,@Em)
            """, new { H = h, S = s, C1 = CpfAna, C2 = CpfBia, Em = DateTime.Now.ToString("o") });
        cx.Execute("INSERT OR REPLACE INTO terminal (id, terminal_uuid, loja_id, loja_nome, cnpj, serie_nfce, ambiente, criado_em) VALUES (1,'t-1','l-1','American Day Savassi','00000000000191',3,2,@Em)",
            new { Em = DateTime.Now.ToString("o") });
        return cx;
    }

    private static Task<DesfechoDigital> Rodar(LeitorFalso leitor, TelaFalsa tela, SqliteConnection cx, RemotaFalsa? remota,
        TelaTotpFalsa totp, string finalidade = "entrada", bool primeiraVez = true)
        => PortaoDigital.IdentificarAsync(leitor, tela, ch => Digitais.OperadorDaChave(cx, ch), op => Digitais.CpfDoOperador(cx, op.Id),
            Digitais.ListaDaReserva(cx), remota, totp, "CAIXA-01", finalidade, primeiraVez, () => new DateTime(2026, 10, 26, 9, 0, 0));

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        var anterior = Banco.CaminhoForcado;
        using var cx = BancoLimpo(out var arquivo);
        Banco.CaminhoForcado = arquivo;
        try
        {
            // ── A. a guarda local ──────────────────────────────────────────────────
            var g = Digitais.Guardar(cx, "winbio:3:12:aabb:dedo:1", "529.982.247-25", Ana.Id, Ana.Nome, 1, null);
            checar(g.Cpf == CpfAna && g.CpfHash == Digitais.CpfHash(CpfAna) && g.Vaga == 1, "A-01 guardar normaliza o CPF e calcula o hash");
            var cifrado = cx.ExecuteScalar<string>("SELECT cpf_cifrado FROM digital WHERE chave = 'winbio:3:12:aabb:dedo:1'")!;
            checar(!cifrado.Contains(CpfAna) && cifrado.StartsWith("dpapi:"), "A-02 o CPF nao fica em claro no banco (DPAPI da maquina)");
            checar(Digitais.Decifrar(cifrado) == CpfAna, "A-03 este Windows decifra o que cifrou");
            checar(Digitais.Decifrar("dpapi:AAAA") is null && Digitais.Decifrar(null) is null, "A-04 cifra de outro PC ou lixo = null, sem excecao");
            checar(Digitais.PorChave(cx, "winbio:3:12:aabb:dedo:1")?.Nome == "ANA" && Digitais.PorCpf(cx, "52998224725")?.Chave == "winbio:3:12:aabb:dedo:1", "A-05 acha por chave e por CPF");
            checar(Digitais.OperadorDaChave(cx, "winbio:3:12:aabb:dedo:1")?.Id == Ana.Id, "A-06 a chave leva ao operador ativo pelo CPF");
            checar(Digitais.OperadorDaChave(cx, "winbio:3:12:ffff:dedo:9") is null, "A-07 chave desconhecida = ninguem");
            checar(Digitais.CpfHash(CpfAna).Length == 64 && Digitais.CpfHash(CpfAna) == Digitais.CpfHash("529.982.247-25"), "A-08 o hash e SHA-256 hex e ignora a pontuacao");
            checar(Digitais.CpfHashOuNulo("123") is null, "A-09 CPF invalido nao tem hash");
            checar(Digitais.ProximaVaga(cx) == 2, "A-10 a proxima vaga livre e a 2");
            Digitais.Guardar(cx, "winbio:3:12:aabb:dedo:2", CpfAna, Ana.Id, Ana.Nome, 2, "Marcos");
            checar(Digitais.Listar(cx).Count(d => d.Cpf == CpfAna) == 1 && Digitais.PorCpf(cx, CpfAna)!.Chave.EndsWith("dedo:2"), "A-11 recadastro: um CPF, uma digital (a antiga sai)");
            checar(Digitais.PendentesDeRegistro(cx).Count == 1, "A-12 a nova fica pendente de registro no ERP");
            var aud = cx.ExecuteScalar<string>("SELECT detalhe FROM auditoria WHERE evento='digital_cadastrada' ORDER BY id DESC LIMIT 1")!;
            checar(aud.Contains("529.***.***-25") && !aud.Contains(CpfAna) && aud.Contains("por Marcos"), "A-13 a auditoria leva o CPF mascarado e quem cadastrou");
            Digitais.MarcarRegistrada(cx, Digitais.PorCpf(cx, CpfAna)!.Chave);
            checar(Digitais.PendentesDeRegistro(cx).Count == 0, "A-14 marcada como registrada, sai da pendencia");
            var apagada = Digitais.Apagar(cx, CpfAna, "Marcos");
            checar(apagada is not null && Digitais.PorCpf(cx, CpfAna) is null && Digitais.RemocoesPendentes(cx).Count == 1, "A-15 apagar some do local e deixa a remocao pendente para o ERP");
            checar(Digitais.Apagar(cx, CpfAna, null) is null, "A-16 apagar de novo = nada (sem excecao)");
            checar(Digitais.Mascarar(CpfAna) == "529.***.***-25", "A-17 a mascara mostra so as pontas");
            bool lancou = false;
            try { Digitais.Guardar(cx, "k", "000", null, "X", 1, null); } catch (InvalidOperationException) { lancou = true; }
            checar(lancou, "A-18 guardar com CPF invalido recusa");
            for (var v = 1; v <= 10; v++) Digitais.Guardar(cx, $"winbio:3:12:cc{v:00}:dedo:{v}", CpfValido(v), null, $"P{v}", v, null);
            checar(Digitais.ProximaVaga(cx) is null, "A-19 dez digitais = sem vaga");
            cx.Execute("DELETE FROM digital; DELETE FROM digital_removida;");

            // ── B. o portao: reconheceu ─────────────────────────────────────────────
            Digitais.Guardar(cx, "winbio:3:12:aabb:dedo:1", CpfAna, Ana.Id, Ana.Nome, 1, null);
            var leitor = new LeitorFalso();
            leitor.Leituras.Enqueue(Captura.Reconhecida("winbio:3:12:aabb:dedo:1"));
            var tela = new TelaFalsa();
            var remota = new RemotaFalsa();
            var totp = new TelaTotpFalsa();
            var d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Digital && d.Operador?.Id == Ana.Id && d.Cpf == CpfAna && d.Entrou, "B-01 digital reconhecida entra como a ANA");
            checar(d.CpfHash == Digitais.CpfHash(CpfAna) && d.ViaTexto == "digital", "B-02 o desfecho carrega o hash e a via para o turno");
            checar(tela.Titulos[0] == Biometria.TituloEntrada && tela.Avisos[0] is null && tela.OfertasPrimeiraVez[0], "B-03 a tela de uma linha: Encoste o dedo no leitor, com Primeira vez");
            checar(totp.Pedidos == 0 && remota.Chamadas.Count == 0 && tela.VezesEscolheu == 0, "B-04 reconhecida nao pede codigo nem lista");
            checar(leitor.Sondas == 1 && leitor.Identificacoes == 1, "B-05 uma sonda, uma leitura");

            // fechamento: outro titulo, sem Primeira vez
            leitor = new LeitorFalso(); leitor.Leituras.Enqueue(Captura.Reconhecida("winbio:3:12:aabb:dedo:1"));
            tela = new TelaFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp, "fechamento", primeiraVez: false);
            checar(d.Via == ViaDigital.Digital && tela.Titulos[0] == Biometria.TituloFechamento && !tela.OfertasPrimeiraVez[0], "B-06 no fechamento o titulo muda e nao ha Primeira vez");

            // ── C. tres leituras sem reconhecer -> reserva (codigo do gerente + lista) ──
            leitor = new LeitorFalso(); tela = new TelaFalsa(); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            tela.AoEscolher = l => l.First(o => o.Id == Bia.Id);
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Reserva && d.Operador?.Id == Bia.Id && d.Cpf == CpfBia, "C-01 3 sem reconhecer: a reserva entra com quem foi escolhido");
            checar(leitor.Identificacoes == 3 && tela.Avisos[1] == Biometria.AvisoNaoReconheci(1) && tela.Avisos[2] == Biometria.AvisoNaoReconheci(2), "C-02 tres leituras, com o aviso N de 3 entre elas");
            checar(remota.Chamadas.Count == 1 && remota.Chamadas[0].Tipo == Autorizacao.TipoTurnoSemDigital && remota.Chamadas[0].Nivel == Autorizacao.NivelGerente, "C-03 a reserva e o TOTP tipo turno_sem_digital no nivel gerente");
            checar(remota.Chamadas[0].Referencia == "turno:CAIXA01:entrada:20261026090000", "C-04 a referencia do log amarra terminal, finalidade e hora");
            checar(d.Motivo == "pela reserva: 3 leituras sem reconhecer" && d.Autorizador == "Marcos" && d.Registro == "log-0001-abcd", "C-05 o motivo diz pela reserva e a trilha leva quem aprovou");
            checar(tela.AvisosDeCaixa.Count == 1 && tela.AvisosDeCaixa[0].Texto.Contains("Entrada pela reserva"), "C-06 um aviso de uma linha antes da reserva");
            checar(AssinaturaDigital.De(d).Via == "reserva" && AssinaturaDigital.De(d).Motivo == d.Motivo && AssinaturaDigital.De(d).CpfHash == Digitais.CpfHash(CpfBia), "C-07 a assinatura do turno leva via, hash e motivo");

            // ── D. leitor ausente / sem driver / sem servico -> reserva direto ─────────
            foreach (var (estado, aviso, causa) in new[] {
                (EstadoLeitor.SemLeitor, Biometria.AvisoSemLeitor, "sem leitor"),
                (EstadoLeitor.SemDriver, Biometria.AvisoSemDriver, "leitor sem driver WBF"),
                (EstadoLeitor.SemServico, Biometria.AvisoSemServico, "servico de biometria parado") })
            {
                leitor = new LeitorFalso { Estado = estado }; tela = new TelaFalsa(); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
                d = await Rodar(leitor, tela, cx, remota, totp);
                checar(d.Via == ViaDigital.Reserva && leitor.Identificacoes == 0 && tela.AvisosDeCaixa[0].Texto == aviso && d.Motivo == "pela reserva: " + causa,
                    $"D-01 {estado}: avisa em uma linha, nao tenta ler e cai na reserva ({causa})");
            }

            // ── E. a reserva recusada: sem internet, codigo errado 3x, desistiu, ninguem escolhido ──
            leitor = new LeitorFalso { Estado = EstadoLeitor.SemLeitor }; tela = new TelaFalsa(); remota = new RemotaFalsa { SemInternet = true }; totp = new TelaTotpFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Cancelado && !d.Entrou && d.Motivo.Contains("Sem internet") && tela.VezesEscolheu == 0, "E-01 sem internet nao ha reserva: ninguem entra");
            leitor = new LeitorFalso { Estado = EstadoLeitor.SemLeitor }; tela = new TelaFalsa(); remota = new RemotaFalsa { Aprova = false }; totp = new TelaTotpFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Cancelado && totp.Pedidos == Autorizacao.MaxTentativas && d.Motivo.Contains("3 vezes"), "E-02 codigo errado 3 vezes: recusa");
            leitor = new LeitorFalso { Estado = EstadoLeitor.SemLeitor }; tela = new TelaFalsa(); remota = new RemotaFalsa(); totp = new TelaTotpFalsa { Codigo = null };
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Cancelado && d.Avisado, "E-03 desistiu do codigo: cancelado e a tela ja avisou");
            leitor = new LeitorFalso { Estado = EstadoLeitor.SemLeitor }; tela = new TelaFalsa { AoEscolher = _ => null }; remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Cancelado && d.Motivo.Contains("ninguem escolhido"), "E-04 codigo certo mas ninguem escolhido: cancelado");
            leitor = new LeitorFalso { Estado = EstadoLeitor.SemLeitor }; tela = new TelaFalsa(); totp = new TelaTotpFalsa();
            d = await Rodar(leitor, tela, cx, null, totp);
            checar(d.Via == ViaDigital.Cancelado && d.Motivo.Contains("nuvem"), "E-05 caixa sem nuvem: a reserva nao existe");

            // ── F. cancelou, primeira vez, sem digital, falha do leitor, desconhecida, demitido ──
            leitor = new LeitorFalso { EsperaCancelamento = true }; tela = new TelaFalsa(); tela.Escolhas.Enqueue(EscolhaNaLeitura.Desistiu); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Cancelado && d.Avisado && !d.PrimeiraVez && remota.Chamadas.Count == 0, "F-01 Cancelar na tela: a leitura e cancelada e ninguem entra");
            leitor = new LeitorFalso { EsperaCancelamento = true }; tela = new TelaFalsa(); tela.Escolhas.Enqueue(EscolhaNaLeitura.PrimeiraVez); totp = new TelaTotpFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Cancelado && d.PrimeiraVez && d.Avisado, "F-02 Primeira vez: devolve o sinal para a tela seguir com CPF e senha");
            leitor = new LeitorFalso { EsperaCancelamento = true }; tela = new TelaFalsa(); tela.Escolhas.Enqueue(EscolhaNaLeitura.SemDigital); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Reserva && d.Motivo == "pela reserva: o operador pediu a reserva", "F-03 Sem digital: vai para a reserva na hora");
            leitor = new LeitorFalso(); leitor.Leituras.Enqueue(Captura.Falhou("WinBio 0x80098010")); leitor.Leituras.Enqueue(Captura.Reconhecida("winbio:3:12:aabb:dedo:1")); tela = new TelaFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Digital && tela.Avisos[1]!.Contains("O leitor falhou"), "F-04 falha do leitor conta uma tentativa com aviso, e a seguinte entra");
            leitor = new LeitorFalso(); leitor.Leituras.Enqueue(Captura.Reconhecida("winbio:3:12:ffff:dedo:9")); leitor.Leituras.Enqueue(Captura.Reconhecida("winbio:3:12:aabb:dedo:1")); tela = new TelaFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Digital && tela.Avisos[1] == Biometria.AvisoDigitalDesconhecida, "F-05 digital que o Windows conhece mas o caixa nao: avisa e tenta de novo");
            cx.Execute("UPDATE operador SET ativo = 0 WHERE id = 'op-ana'");
            leitor = new LeitorFalso(); leitor.Leituras.Enqueue(Captura.Reconhecida("winbio:3:12:aabb:dedo:1")); tela = new TelaFalsa(); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            tela.AoEscolher = _ => null;
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Cancelado && tela.Avisos[1] == Biometria.AvisoDigitalDesconhecida && !Digitais.ListaDaReserva(cx).Any(o => o.Id == Ana.Id), "F-06 demitida no painel: a digital guardada nao entra e ela sai da lista da reserva");
            cx.Execute("UPDATE operador SET ativo = 1 WHERE id = 'op-ana'");
            leitor = new LeitorFalso(); leitor.Leituras.Enqueue(new Captura(ResultadoCaptura.SemLeitor)); tela = new TelaFalsa(); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Reserva && d.Motivo.Contains("sumiu"), "F-07 leitor desplugado no meio: reserva");

            // ── G. o cadastro: 3 leituras, progresso, vaga, guarda ─────────────────────
            leitor = new LeitorFalso { ProximoCadastro = Cadastro.Feito("winbio:3:12:bbbb:dedo:3", 3) }; tela = new TelaFalsa();
            var cad = await PortaoDigital.CadastrarAsync(leitor, tela, Biometria.TituloPrimeiraVez, 3);
            checar(cad.Resultado == ResultadoCadastro.Ok && cad.Chave == "winbio:3:12:bbbb:dedo:3" && leitor.VagasPedidas[0] == 3, "G-01 o cadastro pede a vaga e devolve a chave");
            checar(tela.Titulos[0] == Biometria.TituloPrimeiraVez && tela.Progresso.SequenceEqual(new[] { "Leitura 1 de 3", "Leitura 2 de 3", "Leitura 3 de 3" }), "G-02 a tela diz Primeira vez: encoste o dedo 3 vezes e conta 1, 2, 3");
            checar(Biometria.ProgressoCadastro(4) == "Mais uma vez (4)", "G-03 o Windows pedindo a quarta: Mais uma vez");
            leitor = new LeitorFalso { ProximoCadastro = Cadastro.Cancelado }; tela = new TelaFalsa();
            cad = await PortaoDigital.CadastrarAsync(leitor, tela, Biometria.TituloPrimeiraVez, 4);
            checar(cad.Resultado == ResultadoCadastro.Cancelado && cad.Chave is null, "G-04 cadastro cancelado nao devolve chave");

            // ── H. o turno grava a via e o hash; a fila sobe as colunas e cai para o corpo antigo sem o 224 ──
            var ass = new AssinaturaDigital("digital", Digitais.CpfHash(CpfAna));
            var sessao = Caixa.Abrir(cx, Ana, new Dinheiro(10000), ass);
            var linha = cx.QueryFirst("SELECT aberto_via, aberto_cpf_hash, via_motivo FROM caixa_sessao WHERE id = @Id", new { sessao.Id });
            checar((string)linha.aberto_via == "digital" && (string)linha.aberto_cpf_hash == ass.CpfHash && linha.via_motivo is null, "H-01 a abertura grava aberto_via e o hash");
            checar(cx.ExecuteScalar<string>("SELECT detalhe FROM auditoria WHERE evento='caixa_aberto' ORDER BY id DESC LIMIT 1")!.Contains("digital"), "H-02 a auditoria da abertura diz digital");
            var fech = Caixa.Fechar(cx, sessao, new Dictionary<string, Dinheiro> { ["dinheiro"] = new Dinheiro(10000) }, Bia, new Dinheiro(200),
                "fechamento de teste da digital, caixa sem venda", assinatura: new AssinaturaDigital("reserva", Digitais.CpfHash(CpfBia), "pela reserva: sem leitor"));
            linha = cx.QueryFirst("SELECT fechado_via, fechado_cpf_hash, via_motivo, fechado_por FROM caixa_sessao WHERE id = @Id", new { sessao.Id });
            checar((string)linha.fechado_via == "reserva" && (string)linha.fechado_cpf_hash == Digitais.CpfHash(CpfBia) && (string)linha.via_motivo == "pela reserva: sem leitor" && (string)linha.fechado_por == Bia.Id,
                "H-03 o fechamento grava fechado_via, o hash de quem fechou e o motivo da reserva");
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM auditoria WHERE evento='caixa_fechado_reserva'") == 1, "H-04 fechamento pela reserva tem auditoria propria");
            var s2 = Caixa.Abrir(cx, Ana, new Dinheiro(10000));
            linha = cx.QueryFirst("SELECT aberto_via, aberto_cpf_hash FROM caixa_sessao WHERE id = @Id", new { s2.Id });
            checar(linha.aberto_via is null && linha.aberto_cpf_hash is null, "H-05 sem assinatura (biometria desligada) as colunas ficam nulas, como antes");
            Caixa.Fechar(cx, s2, new Dictionary<string, Dinheiro> { ["dinheiro"] = new Dinheiro(10000) }, Ana, new Dinheiro(200), "fechamento de teste da digital, caixa sem venda");

            var corpoBase = JsonSerializer.Serialize(new Dictionary<string, object?> { ["client_key"] = "k", ["store"] = "Savassi", ["fundo_cent"] = 100L });
            checar(Drenagem.ComAssinaturaDigital(corpoBase, null, null, null, null, null) is null, "H-06 turno sem digital: o corpo de sempre, sem coluna nova");
            var com = Drenagem.ComAssinaturaDigital(corpoBase, "digital", "h1", "reserva", "h2", "pela reserva: x")!;
            using (var doc = JsonDocument.Parse(com))
                checar(doc.RootElement.GetProperty("aberto_via").GetString() == "digital" && doc.RootElement.GetProperty("fechado_cpf_hash").GetString() == "h2"
                       && doc.RootElement.GetProperty("via_motivo").GetString() == "pela reserva: x" && doc.RootElement.GetProperty("fundo_cent").GetInt64() == 100,
                    "H-07 com digital o corpo leva as cinco colunas e mantem as de sempre");
            checar(Drenagem.SemColunaNaNuvem("HTTP 400: {\"code\":\"PGRST204\",\"message\":\"Could not find the 'aberto_via' column of 'pdv_caixa_sessoes' in the schema cache\"}")
                   && !Drenagem.SemColunaNaNuvem("HTTP 409: duplicate") && !Drenagem.SemColunaNaNuvem(null), "H-08 PGRST204 = a nuvem ainda nao tem o SQL 224 (e so ele)");
            var fonteDren = File.ReadAllText(Caminho("Pdv.Nucleo/Drenagem.cs"));
            checar(fonteDren.Contains("SemColunaNaNuvem(r.Erro)") && fonteDren.Contains("aberto_via, aberto_cpf_hash, fechado_via, fechado_cpf_hash, via_motivo"), "H-09 a fila tenta com as colunas e, no PGRST204, manda o corpo de sempre");

            // ── I. a chave do painel: nasce ligada; so o painel desliga ────────────────
            checar(Biometria.Ligada(cx) && !Biometria.PainelConhece(cx), "I-01 sem chave nenhuma: ligada, e o painel ainda nao falou");
            var linhas = ConfigLojaPainel.Ler("""[{"store":"American Day Savassi","chat_respostas":null,"biometria_turno":false}]""");
            checar(linhas[0].BiometriaTurno == false, "I-02 o painel manda biometria_turno");
            var mudou = ConfigLojaPainel.Aplicar(cx, linhas[0], new DateTime(2026, 10, 26, 10, 0, 0));
            checar(!Biometria.Ligada(cx) && Biometria.PainelConhece(cx) && mudou.Contains("digital desligada"), "I-03 painel desliga: desligada, marca do painel gravada, e o Atualizar diz o que mudou");
            mudou = ConfigLojaPainel.Aplicar(cx, ConfigLojaPainel.Ler("""[{"store":"American Day Savassi","biometria_turno":true}]""")[0], DateTime.Now);
            checar(Biometria.Ligada(cx) && mudou.Contains("digital ligada"), "I-04 painel liga de volta");
            cx.Execute("DELETE FROM config WHERE chave IN ('biometria_turno','biometria_painel_em')");
            mudou = ConfigLojaPainel.Aplicar(cx, ConfigLojaPainel.Ler("""[{"store":"American Day Savassi","chat_respostas":null}]""")[0], DateTime.Now);
            checar(Biometria.Ligada(cx) && !Biometria.PainelConhece(cx) && !mudou.Contains("digital"), "I-05 painel sem o 224 (campo ausente): continua ligada e sem a marca");

            // ── J. o registro no ERP: corpo sem CPF, 404 fica pendente ────────────────
            Digitais.Guardar(cx, "winbio:3:12:cccc:dedo:5", CpfBia, Bia.Id, Bia.Nome, 5, "Marcos", new DateTime(2026, 10, 26, 9, 30, 0, DateTimeKind.Local));
            Digitais.Apagar(cx, CpfAna, "Marcos");
            var pend = RegistroDigital.Pendentes(cx);
            checar(pend.Count == 2 && pend[0].Acao == "cadastro" && pend[1].Acao == "remocao", "J-01 cadastro e remocao pendentes, nessa ordem");
            using (var doc = JsonDocument.Parse(pend[0].Json))
            {
                var r = doc.RootElement;
                checar(r.GetProperty("_cpf_hash").GetString() == Digitais.CpfHash(CpfBia) && r.GetProperty("_nome").GetString() == "BIA" && r.GetProperty("_por").GetString() == "Marcos"
                       && r.GetProperty("_acao").GetString() == "cadastro" && r.GetProperty("_funcionario_id").ValueKind == JsonValueKind.Null, "J-02 o corpo leva hash, nome e quem cadastrou; id que nao e uuid vai nulo");
                checar(!pend[0].Json.Contains(CpfBia) && !pend[0].Json.Contains("winbio:"), "J-03 nem o CPF nem a chave do Windows viajam");
            }
            checar(RegistroDigital.Corpo("cadastro", "h", "0f0e0d0c-0b0a-4908-8706-050403020100", "N", null, DateTime.Now).Contains("\"_funcionario_id\":\"0f0e0d0c-0b0a-4908-8706-050403020100\""), "J-04 id do painel (uuid) vai como funcionario_id");
            checar(RegistroDigital.Ler(200, "{\"ok\":true,\"id\":\"x\"}") == RegistroDigital.Desfecho.Aceito && RegistroDigital.Ler(404, "{}") == RegistroDigital.Desfecho.SemRpc
                   && RegistroDigital.Ler(0, null) == RegistroDigital.Desfecho.SemResposta && RegistroDigital.Ler(200, "{\"ok\":false,\"motivo\":\"sem_terminal\"}") == RegistroDigital.Desfecho.Recusado
                   && RegistroDigital.Ler(200, "lixo") == RegistroDigital.Desfecho.Recusado, "J-05 a leitura da resposta: aceito, 404 sem RPC, sem resposta, recusado");
            RegistroDigital.Marcar(cx, pend[0]); RegistroDigital.Marcar(cx, pend[1]);
            checar(RegistroDigital.Pendentes(cx).Count == 0, "J-06 marcadas, nada pendente");
            var fonteSinc = File.ReadAllText(Caminho("Pdv.Nucleo/Sincronizacao.cs"));
            checar(fonteSinc.Contains("RegistrarDigitaisAsync"), "J-07 o Atualizar registra as digitais no painel");

            // ── K. o WinBio de verdade carrega nesta maquina ───────────────────────────
            var (unidades, hr, erro) = LeitorWinBio.Enumerar();
            checar(erro is null || !erro.Contains("winbio.dll nao carregou"), $"K-01 winbio.dll carrega e enumera sem travar (hr=0x{hr:X8}, {unidades.Count} unidade(s){(erro is null ? "" : ", " + erro)})");
            var real = new LeitorWinBio();
            var sondaReal = await real.SondarAsync(CancellationToken.None);
            checar(sondaReal.Pronto == (unidades.Count > 0), $"K-02 a sonda real: {sondaReal.Estado} ({sondaReal.Detalhe})");
            checar(LeitorWinBio.Texto(unchecked((int)0x80098005)) == "WinBio: nao reconheceu" && LeitorWinBio.Texto(unchecked((int)0x80098ABC)).StartsWith("WinBio 0x"), "K-03 os codigos do Windows viram uma linha");
            checar(LeitorWinBio.ChaveCurta("winbio:3:12:aabb:dedo:7") == "winbio dedo 7" && LeitorWinBio.ChaveCurta("x") == "?", "K-04 a chave curta do diagnostico nao mostra o SID");

            // ── L. o diagnostico do driver (U.are.U 4500, USB 05BA) ───────────────────
            var semNada = DriverDigitalPersona.Avaliar(Array.Empty<DriverDigitalPersona.Aparelho>());
            checar(semNada.Estado == EstadoLeitor.SemLeitor, "L-01 nenhum aparelho 05BA = sem leitor");
            var semDriver = DriverDigitalPersona.Avaliar(new[] { new DriverDigitalPersona.Aparelho("PID_000A", "USB", true) });
            checar(semDriver.Estado == EstadoLeitor.SemDriver && semDriver.Detalhe.Contains("sem o driver WBF") && semDriver.Detalhe.Contains("Windows Update"), "L-02 4500 plugado como USB comum = falta o driver WBF, com o passo");
            var comDriver = DriverDigitalPersona.Avaliar(new[] { new DriverDigitalPersona.Aparelho("PID_000A", "Biometric", true) });
            checar(comDriver.Estado == EstadoLeitor.SemServico, "L-03 plugado com classe Biometric e o WinBio sem listar = servico");
            var desplugado = DriverDigitalPersona.Avaliar(new[] { new DriverDigitalPersona.Aparelho("PID_000A", "Biometric", false) });
            checar(desplugado.Estado == EstadoLeitor.SemLeitor && desplugado.Detalhe.Contains("nao esta plugado"), "L-04 so no historico = desplugado");
            var diag = DriverDigitalPersona.LinhasDeDiagnostico(Array.Empty<string>(), 0, null, new[] { new DriverDigitalPersona.Aparelho("PID_000A", "USB", true) });
            checar(diag.Contains("Leitor encontrado, falta o driver do Windows (DigitalPersona WBF).") && diag.Any(l => l.Contains("Windows Update")), "L-05 a frase pedida pelo dono e o passo do driver");
            var diagOk = DriverDigitalPersona.LinhasDeDiagnostico(new[] { "#1 DigitalPersona U.are.U 4500" }, 0, null, Array.Empty<DriverDigitalPersona.Aparelho>());
            checar(diagOk[0].StartsWith("Leitor pronto") && diagOk[1].Contains("4500"), "L-06 com unidade: Leitor pronto e a lista");
            var lidos = DriverDigitalPersona.LerAparelhos();
            checar(lidos is not null, $"L-07 o registro desta maquina le sem lancar ({lidos!.Count} aparelho(s) 05BA)");

            // ── M. textos de tela sem travessao; a versao ───────────────────────────────
            var textos = new[] { Biometria.TituloEntrada, Biometria.TituloFechamento, Biometria.TituloPrimeiraVez, Biometria.TituloRecadastro,
                Biometria.AvisoSemLeitor, Biometria.AvisoSemDriver, Biometria.AvisoSemServico, Biometria.AvisoDigitalDesconhecida, Biometria.AvisoJaTemDigital,
                Biometria.AvisoCadastroCancelado, Biometria.AvisoVagasEsgotadas, Biometria.AvisoNaoReconheci(2), DriverDigitalPersona.PassoDoDriver };
            checar(textos.All(t => !t.Contains((char)0x2014) && !t.Contains((char)0x2013)), "M-01 nenhum texto da digital com travessao");
            checar(textos.All(t => t.Split('\n').Length == 1), "M-02 todos os textos sao de uma linha");
            var novos = new[] { "Pdv.Nucleo/Biometria.cs", "Pdv.Nucleo/LeitorWinBio.cs", "Pdv.Nucleo/RegistroDigital.cs", "Telas/TelaDigital.cs", "Pdv.Testes/TestesBiometria.cs" }
                .Select(a => File.Exists(Caminho(a)) ? File.ReadAllText(Caminho(a)) : "").ToList();
            checar(novos.All(t => t.Length > 0 && !t.Contains((char)0x2014) && !t.Contains((char)0x2013)), "M-03 os arquivos novos existem e nao tem travessao");
            checar(File.ReadAllText(Caminho("Pdv.csproj")).Contains("<Version>1.0.52</Version>"), "M-04 a versao do caixa e 1.0.52 (a digital no lugar da senha)");
            var fonteAut = File.ReadAllText(Caminho("Pdv.Nucleo/Autorizacao.cs"));
            checar(fonteAut.Contains("TipoTurnoSemDigital => (\"Entrada sem digital\"") && Autorizacao.TipoTurnoSemDigital == "turno_sem_digital" && Autorizacao.TipoDigitalRecadastro == "digital_recadastro", "M-05 os tipos novos do TOTP e a frase da recusa");
            var fonteLogin = File.ReadAllText(Caminho("Telas/Login.xaml.cs"));
            var fonteVenda = File.ReadAllText(Caminho("Telas/Venda.xaml.cs"));
            var fonteAbert = File.ReadAllText(Caminho("Telas/AberturaCaixa.xaml.cs"));
            checar(fonteLogin.Contains("PortaoDigitalDaTela.IdentificarAsync(dono, \"entrada\"") && fonteLogin.Contains("Biometria.Ligada("), "M-06 a entrada passa pelo portao da digital quando ligada");
            checar(fonteVenda.Contains("PortaoDigitalDaTela.IdentificarAsync(dono, \"fechamento\"") && fonteVenda.Contains("Biometria.Ligada("), "M-07 o fechamento do caixa passa pelo portao");
            checar(fonteAbert.Contains("PortaoDigitalDaTela.IdentificarAsync(dono, \"fechamento\"") && fonteAbert.Contains("EntradaDigital.Tomar()"), "M-08 fechar o caixa esquecido (na abertura) tambem passa pelo portao");
            checar(fonteLogin.Contains("Biometria.AvisoJaTemDigital"), "M-09 quem ja tem digital nao entra mais pela senha");
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            cx.Close();
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }

    /// <summary>CPFs validos distintos para encher as dez vagas.</summary>
    private static string CpfValido(int n)
    {
        var b = $"7654321{n:00}";
        int d1 = 0, d2 = 0;
        for (var i = 0; i < 9; i++) d1 += (b[i] - '0') * (10 - i);
        d1 = d1 * 10 % 11; if (d1 == 10) d1 = 0;
        for (var i = 0; i < 9; i++) d2 += (b[i] - '0') * (11 - i);
        d2 += d1 * 2; d2 = d2 * 10 % 11; if (d2 == 10) d2 = 0;
        return b + d1 + d2;
    }

    private static string Caminho(string relativo)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var tenta = Path.Combine(dir, relativo);
            if (File.Exists(tenta) || File.Exists(Path.Combine(dir, "Pdv.csproj"))) return Path.Combine(dir, relativo);
            dir = Path.GetDirectoryName(dir);
        }
        return relativo;
    }
}
