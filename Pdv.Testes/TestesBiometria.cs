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
///  · os textos de tela sem travessao, e a versao;
///  · O. 1.0.55: BIOMETRIA OBRIGATORIA (o painel nao desliga) e a AUTORIZACAO DO GERENTE sem PIN
///    (digital do gerente, conferida pelo papel, ou codigo do gerente geral/dono);
///  · N. SO DIGITAL (1.0.54, 27/10): nenhuma senha entra; biometria desligada = reserva em toda
///    entrada; na reserva e no primeiro cadastro o funcionario digita o CPF (operador ativo da
///    loja; o que nao confere e recusado sem pedir codigo) e o gerente geral ou o dono autoriza
///    (turno_sem_digital / digital_cadastro, nivel gerente); o master segue na Configuracao.
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
        /// <summary>O CPF que o funcionario digita (ajuste do dono, 27/10). Fila primeiro; vazia = CpfPadrao (null = desistiu).</summary>
        public Queue<string?> Cpfs = new();
        public string? CpfPadrao = CpfAna;
        public int VezesPediuCpf;
        public readonly List<string?> AvisosCpf = new();

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

        public Task<string?> PedirCpfAsync(string titulo, string? aviso)
        {
            VezesPediuCpf++;
            Titulos.Add(titulo);
            AvisosCpf.Add(aviso);
            return Task.FromResult(Cpfs.Count > 0 ? Cpfs.Dequeue() : CpfPadrao);
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
            cpf => Digitais.OperadorDoCpf(cx, cpf), remota, totp, "CAIXA-01", finalidade, primeiraVez, () => new DateTime(2026, 10, 26, 9, 0, 0));

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
            checar(totp.Pedidos == 0 && remota.Chamadas.Count == 0 && tela.VezesPediuCpf == 0, "B-04 reconhecida nao pede codigo nem CPF");
            checar(leitor.Sondas == 1 && leitor.Identificacoes == 1, "B-05 uma sonda, uma leitura");

            // fechamento: outro titulo, sem Primeira vez
            leitor = new LeitorFalso(); leitor.Leituras.Enqueue(Captura.Reconhecida("winbio:3:12:aabb:dedo:1"));
            tela = new TelaFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp, "fechamento", primeiraVez: false);
            checar(d.Via == ViaDigital.Digital && tela.Titulos[0] == Biometria.TituloFechamento && !tela.OfertasPrimeiraVez[0], "B-06 no fechamento o titulo muda e nao ha Primeira vez");

            // ── C. tres leituras sem reconhecer -> reserva (o funcionario digita o CPF + codigo do gerente) ──
            leitor = new LeitorFalso(); tela = new TelaFalsa { CpfPadrao = "111.444.777-35" }; remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Reserva && d.Operador?.Id == Bia.Id && d.Cpf == CpfBia, "C-01 3 sem reconhecer: a reserva entra com o CPF que a BIA digitou");
            checar(leitor.Identificacoes == 3 && tela.Avisos[1] == Biometria.AvisoNaoReconheci(1) && tela.Avisos[2] == Biometria.AvisoNaoReconheci(2), "C-02 tres leituras, com o aviso N de 3 entre elas");
            checar(remota.Chamadas.Count == 1 && remota.Chamadas[0].Tipo == Autorizacao.TipoTurnoSemDigital && remota.Chamadas[0].Nivel == Autorizacao.NivelGerente, "C-03 a reserva e o TOTP tipo turno_sem_digital no nivel gerente");
            checar(remota.Chamadas[0].Referencia == "turno:CAIXA01:entrada:20261026090000", "C-04 a referencia do log amarra terminal, finalidade e hora");
            checar(d.Motivo == "pela reserva: 3 leituras sem reconhecer" && d.Autorizador == "Marcos" && d.Registro == "log-0001-abcd", "C-05 o motivo diz pela reserva e a trilha leva quem aprovou");
            checar(tela.AvisosDeCaixa.Count == 1 && tela.AvisosDeCaixa[0].Texto.Contains("Entrada pela reserva"), "C-06 um aviso de uma linha antes da reserva");
            checar(AssinaturaDigital.De(d).Via == "reserva" && AssinaturaDigital.De(d).Motivo == "pela reserva: 3 leituras sem reconhecer · autorizado por Marcos (codigo nivel gerente)"
                   && AssinaturaDigital.De(d).CpfHash == Digitais.CpfHash(CpfBia), "C-07 a assinatura do turno leva via, hash, motivo e quem autorizou com o nivel do codigo");
            checar(tela.VezesPediuCpf == 1 && tela.Titulos.Contains(Biometria.TituloCpfEntrada) && remota.Chamadas[0].Referencia.StartsWith("turno:"), "C-08 o CPF vem antes do codigo, com o titulo Digite seu CPF");
            var linhaAud = d.LinhaDeAuditoria;
            checar(linhaAud.Contains("BIA") && linhaAud.Contains("cpf " + Digitais.CpfHash(CpfBia)[..12]) && !linhaAud.Contains(CpfBia) && linhaAud.Contains("pela reserva: 3 leituras sem reconhecer")
                   && linhaAud.Contains("autorizado por Marcos (codigo nivel gerente)"), "C-09 a auditoria leva nome, hash do CPF (nunca o CPF), pela reserva, motivo e quem autorizou");

            // CPF que nao e operador ativo desta loja: recusa em uma linha e NAO pede codigo
            leitor = new LeitorFalso { Estado = EstadoLeitor.SemLeitor }; tela = new TelaFalsa { CpfPadrao = "390.533.447-05" }; remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(!d.Entrou && d.Motivo == Biometria.AvisoCpfNaoEhFuncionario && !d.Avisado && remota.Chamadas.Count == 0 && totp.Pedidos == 0
                   && tela.VezesPediuCpf == Biometria.MaxTentativas && tela.AvisosCpf[1] == Biometria.AvisoCpfNaoEhFuncionario, "C-10 CPF de quem nao e funcionario ativo: recusa em uma linha, sem pedir codigo");
            leitor = new LeitorFalso { Estado = EstadoLeitor.SemLeitor }; tela = new TelaFalsa(); tela.Cpfs.Enqueue("52998224700"); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Reserva && d.Operador?.Id == Ana.Id && tela.AvisosCpf[1] == Biometria.AvisoCpfInvalido && remota.Chamadas.Count == 1, "C-11 digito errado: Esse CPF nao confere, e o certo na segunda entra");
            checar(Biometria.MascaraCpf("") == "___.___.___-__" && Biometria.MascaraCpf("5299822") == "529.982.2" && Biometria.MascaraCpf(CpfAna) == "529.982.247-25"
                   && Biometria.MascaraCpf("529982247251") == "529.982.247-25", "C-12 a mascara do campo: 000.000.000-00, enquanto digita");
            leitor = new LeitorFalso { Estado = EstadoLeitor.SemLeitor }; tela = new TelaFalsa(); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp, "fechamento", primeiraVez: false);
            checar(d.Via == ViaDigital.Reserva && tela.Titulos.Contains(Biometria.TituloCpfFechamento) && remota.Chamadas[0].Referencia == "turno:CAIXA01:fechamento:20261026090000"
                   && remota.Chamadas[0].Nivel == Autorizacao.NivelGerente, "C-13 no fechamento: Fechamento: digite seu CPF, e o codigo do gerente geral ou do dono");

            // ── D. leitor ausente / sem driver / sem servico -> reserva direto ─────────
            foreach (var (estado, aviso, causa) in new[] {
                (EstadoLeitor.SemLeitor, Biometria.AvisoSemLeitor, "sem leitor"),
                (EstadoLeitor.SemDriver, Biometria.AvisoSemDriver, "leitor sem driver (WBF ou U.are.U RTE)"),
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
            checar(d.Via == ViaDigital.Cancelado && !d.Entrou && d.Motivo.Contains("Sem internet") && tela.VezesPediuCpf == 1, "E-01 sem internet nao ha reserva: ninguem entra");
            leitor = new LeitorFalso { Estado = EstadoLeitor.SemLeitor }; tela = new TelaFalsa(); remota = new RemotaFalsa { Aprova = false }; totp = new TelaTotpFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Cancelado && totp.Pedidos == Autorizacao.MaxTentativas && d.Motivo.Contains("3 vezes"), "E-02 codigo errado 3 vezes: recusa");
            leitor = new LeitorFalso { Estado = EstadoLeitor.SemLeitor }; tela = new TelaFalsa(); remota = new RemotaFalsa(); totp = new TelaTotpFalsa { Codigo = null };
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Cancelado && d.Avisado, "E-03 desistiu do codigo: cancelado e a tela ja avisou");
            leitor = new LeitorFalso { Estado = EstadoLeitor.SemLeitor }; tela = new TelaFalsa { CpfPadrao = null }; remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Cancelado && d.Avisado && remota.Chamadas.Count == 0, "E-04 desistiu do CPF: cancelado, nenhum codigo pedido");
            leitor = new LeitorFalso { Estado = EstadoLeitor.SemLeitor }; tela = new TelaFalsa(); totp = new TelaTotpFalsa();
            d = await Rodar(leitor, tela, cx, null, totp);
            checar(d.Via == ViaDigital.Cancelado && d.Motivo.Contains("nuvem"), "E-05 caixa sem nuvem: a reserva nao existe");

            // ── F. cancelou, primeira vez, sem digital, falha do leitor, desconhecida, demitido ──
            leitor = new LeitorFalso { EsperaCancelamento = true }; tela = new TelaFalsa(); tela.Escolhas.Enqueue(EscolhaNaLeitura.Desistiu); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Cancelado && d.Avisado && !d.PrimeiraVez && remota.Chamadas.Count == 0, "F-01 Cancelar na tela: a leitura e cancelada e ninguem entra");
            leitor = new LeitorFalso { EsperaCancelamento = true }; tela = new TelaFalsa(); tela.Escolhas.Enqueue(EscolhaNaLeitura.PrimeiraVez); totp = new TelaTotpFalsa();
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Cancelado && d.PrimeiraVez && d.Avisado, "F-02 Primeira vez: devolve o sinal para o primeiro cadastro");
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
            d = await Rodar(leitor, tela, cx, remota, totp);
            checar(d.Via == ViaDigital.Cancelado && tela.Avisos[1] == Biometria.AvisoDigitalDesconhecida && !Digitais.ListaDaReserva(cx).Any(o => o.Id == Ana.Id)
                   && d.Motivo == Biometria.AvisoCpfNaoEhFuncionario && remota.Chamadas.Count == 0, "F-06 demitida no painel: a digital guardada nao entra e o CPF dela nao passa na reserva");
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
            checar(!Biometria.PainelConhece(cx), "I-01 sem chave nenhuma: o painel ainda nao falou");
            var linhas = ConfigLojaPainel.Ler("""[{"store":"American Day Savassi","chat_respostas":null,"biometria_turno":false}]""");
            checar(linhas[0].BiometriaTurno == false, "I-02 o painel manda biometria_turno");
            var mudou = ConfigLojaPainel.Aplicar(cx, linhas[0], new DateTime(2026, 10, 26, 10, 0, 0));
            checar(Biometria.PainelConhece(cx) && !mudou.Contains("digital") && Vendas.Config(cx, Biometria.ChaveLigada) is null,
                "I-03 1.0.55: o painel mandando false NAO desliga nada (biometria obrigatoria); so a marca do painel e gravada");
            mudou = ConfigLojaPainel.Aplicar(cx, ConfigLojaPainel.Ler("""[{"store":"American Day Savassi","biometria_turno":true}]""")[0], DateTime.Now);
            checar(!mudou.Contains("digital") && Vendas.Config(cx, Biometria.ChaveLigada) is null, "I-04 true tambem nao muda nada");
            cx.Execute("DELETE FROM config WHERE chave IN ('biometria_turno','biometria_painel_em')");
            mudou = ConfigLojaPainel.Aplicar(cx, ConfigLojaPainel.Ler("""[{"store":"American Day Savassi","chat_respostas":null}]""")[0], DateTime.Now);
            checar(!Biometria.PainelConhece(cx) && !mudou.Contains("digital"), "I-05 painel sem o 224 (campo ausente): sem a marca");
            Vendas.GravarConfig(cx, Biometria.ChaveLigada, "0");
            ConfigLojaPainel.Aplicar(cx, ConfigLojaPainel.Ler("""[{"store":"American Day Savassi","biometria_turno":false}]""")[0], DateTime.Now);
            checar(Vendas.Config(cx, Biometria.ChaveLigada) is null, "I-06 a chave antiga \"0\" de um caixa 1.0.54 e apagada no Atualizar");

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

            // ── N. SO DIGITAL (1.0.54, 27/10/2026, decisao do dono) ─────────────────────
            cx.Execute("DELETE FROM digital; DELETE FROM digital_removida;");
            Digitais.Guardar(cx, "winbio:3:12:bbbb:dedo:2", CpfBia, Bia.Id, Bia.Nome, 2, null);
            var guarda = new GuardaDigitalSqlite(() => Banco.Abrir(arquivo), () => "winbio");
            var agora27 = () => new DateTime(2026, 10, 27, 9, 0, 0);
            Task<DesfechoDigital> Entrar(bool _, LeitorFalso l, TelaFalsa t, RemotaFalsa? r, TelaTotpFalsa tt)
                => PortaoDigital.EntrarAsync(l, t, guarda, ch => Digitais.OperadorDaChave(cx, ch), r, tt, "CAIXA-01", agora27);

            // N1. 1.0.55: BIOMETRIA OBRIGATORIA. Mesmo com o painel mandando false, a entrada pede a digital
            Vendas.GravarConfig(cx, Biometria.ChaveLigada, "0");
            leitor = new LeitorFalso(); leitor.Leituras.Enqueue(Captura.Reconhecida("winbio:3:12:bbbb:dedo:2")); tela = new TelaFalsa(); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Entrar(false, leitor, tela, remota, totp);
            checar(d.Via == ViaDigital.Digital && d.Operador?.Id == Bia.Id && leitor.Sondas == 1 && remota.Chamadas.Count == 0, "N-01 com a chave antiga em \"0\" a entrada continua pela digital (nao existe desligar)");
            cx.Execute("DELETE FROM config WHERE chave = @C", new { C = Biometria.ChaveLigada });
            leitor = new LeitorFalso { Estado = EstadoLeitor.SemLeitor }; tela = new TelaFalsa { CpfPadrao = CpfAna }; remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Entrar(true, leitor, tela, remota, totp);
            checar(d.Via == ViaDigital.Reserva && d.Operador?.Id == Ana.Id && remota.Chamadas.Count == 1 && remota.Chamadas[0].Tipo == Autorizacao.TipoTurnoSemDigital
                   && remota.Chamadas[0].Nivel == Autorizacao.NivelGerente && totp.Niveis.SequenceEqual(new[] { "gerente" }), "N-02 sem leitor: a reserva (CPF + codigo do gerente geral ou do dono)");
            checar(AssinaturaDigital.De(d).Via == "reserva" && AssinaturaDigital.De(d).Motivo == "pela reserva: sem leitor · autorizado por Marcos (codigo nivel gerente)", "N-03 o turno grava reserva, o motivo e quem autorizou");
            leitor = new LeitorFalso { Estado = EstadoLeitor.SemLeitor }; tela = new TelaFalsa(); remota = new RemotaFalsa { SemInternet = true }; totp = new TelaTotpFalsa();
            d = await Entrar(true, leitor, tela, remota, totp);
            checar(!d.Entrou && d.Via == ViaDigital.Cancelado && tela.VezesPediuCpf == 1, "N-04 sem leitor e sem internet: ninguem entra (nao existe senha de volta)");

            // N2. ligada: a digital reconhecida entra sem codigo
            leitor = new LeitorFalso(); leitor.Leituras.Enqueue(Captura.Reconhecida("winbio:3:12:bbbb:dedo:2")); tela = new TelaFalsa(); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Entrar(true, leitor, tela, remota, totp);
            checar(d.Via == ViaDigital.Digital && d.Operador?.Id == Bia.Id && !d.PrimeiroCadastro && remota.Chamadas.Count == 0 && totp.Pedidos == 0 && tela.VezesPediuCpf == 0,
                "N-05 ligada: a digital da BIA entra sem CPF e sem codigo");
            checar(tela.OfertasPrimeiraVez[0] && d.LinhaDeAuditoria == "BIA (digital)", "N-06 a entrada oferece Primeira vez; a auditoria da digital e o nome");

            // N3. ligada, leitor com problema: a reserva
            leitor = new LeitorFalso { Estado = EstadoLeitor.SemDriver }; tela = new TelaFalsa { CpfPadrao = CpfBia }; remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Entrar(true, leitor, tela, remota, totp);
            checar(d.Via == ViaDigital.Reserva && d.Operador?.Id == Bia.Id && d.Motivo.StartsWith("pela reserva: leitor sem driver") && remota.Chamadas[0].Tipo == Autorizacao.TipoTurnoSemDigital, "N-07 ligada e leitor sem driver: a reserva com o CPF dela");
            leitor = new LeitorFalso { EsperaCancelamento = true }; tela = new TelaFalsa { CpfPadrao = CpfBia }; tela.Escolhas.Enqueue(EscolhaNaLeitura.SemDigital); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Entrar(true, leitor, tela, remota, totp);
            checar(d.Via == ViaDigital.Reserva && d.Motivo == "pela reserva: o operador pediu a reserva", "N-08 dedo machucado (Sem digital): a reserva");

            // N4. o PRIMEIRO CADASTRO: o funcionario digita o CPF, codigo do gerente, 3 leituras, guarda
            leitor = new LeitorFalso { EsperaCancelamento = true, ProximoCadastro = Cadastro.Feito("winbio:3:12:aaaa:dedo:1", 3) };
            tela = new TelaFalsa { CpfPadrao = "529.982.247-25" }; tela.Escolhas.Enqueue(EscolhaNaLeitura.PrimeiraVez); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Entrar(true, leitor, tela, remota, totp);
            checar(d.Entrou && d.Via == ViaDigital.Digital && d.PrimeiroCadastro && d.Operador?.Id == Ana.Id && d.Cpf == CpfAna, "N-09 primeira vez: a ANA entra pelo primeiro cadastro");
            checar(remota.Chamadas.Count == 1 && remota.Chamadas[0].Tipo == "digital_cadastro" && remota.Chamadas[0].Tipo == Autorizacao.TipoDigitalCadastro
                   && remota.Chamadas[0].Nivel == Autorizacao.NivelGerente && remota.Chamadas[0].Referencia == "cadastro:CAIXA01:20261027090000", "N-10 o codigo e digital_cadastro, nivel gerente, referencia com terminal e hora");
            checar(tela.VezesPediuCpf == 1 && tela.Titulos.IndexOf(Biometria.TituloCpfCadastro) >= 0 && tela.Titulos.IndexOf(Biometria.TituloCpfCadastro) < tela.Titulos.IndexOf(Biometria.TituloPrimeiraVez),
                "N-11 o mesmo comeco da reserva: Primeira vez: digite seu CPF, antes das leituras");
            checar(leitor.Cadastros == 1 && leitor.VagasPedidas[0] == 1 && tela.Progresso.Count == 3, "N-12 o caixa le o dedo 3 vezes na vaga livre");
            var daAna = Digitais.PorCpf(cx, CpfAna);
            checar(daAna is { Chave: "winbio:3:12:aaaa:dedo:1", CadastradaPor: "Marcos" } && daAna.Vaga == 1, "N-13 a digital fica guardada ligada ao CPF, com quem autorizou");
            checar(d.Motivo == Biometria.MotivoPrimeiroCadastro && d.Autorizador == "Marcos" && d.Registro == "log-0001-abcd"
                   && d.MotivoAuditado == "primeiro cadastro · autorizado por Marcos (codigo nivel gerente)"
                   && AssinaturaDigital.De(d).Via == "digital" && AssinaturaDigital.De(d).Motivo == d.MotivoAuditado, "N-14 o turno grava digital, e o motivo diz primeiro cadastro e quem autorizou");
            leitor = new LeitorFalso(); leitor.Leituras.Enqueue(Captura.Reconhecida("winbio:3:12:aaaa:dedo:1")); tela = new TelaFalsa(); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Entrar(true, leitor, tela, remota, totp);
            checar(d.Via == ViaDigital.Digital && d.Operador?.Id == Ana.Id && !d.PrimeiroCadastro && remota.Chamadas.Count == 0, "N-15 depois do cadastro, so digital: a ANA entra sem codigo");

            // N5. o primeiro cadastro recusado ou sem saida: nada guardado, nunca senha
            leitor = new LeitorFalso { EsperaCancelamento = true }; tela = new TelaFalsa { CpfPadrao = CpfAna }; tela.Escolhas.Enqueue(EscolhaNaLeitura.PrimeiraVez); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Entrar(true, leitor, tela, remota, totp);
            checar(!d.Entrou && d.Motivo == Biometria.AvisoJaTemDigital && remota.Chamadas.Count == 0 && leitor.Cadastros == 0, "N-16 CPF de quem ja tem digital: recusa e nem pede o codigo");
            leitor = new LeitorFalso { EsperaCancelamento = true }; tela = new TelaFalsa { CpfPadrao = "390.533.447-05" }; tela.Escolhas.Enqueue(EscolhaNaLeitura.PrimeiraVez); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Entrar(true, leitor, tela, remota, totp);
            checar(!d.Entrou && d.Motivo == Biometria.AvisoCpfNaoEhFuncionario && remota.Chamadas.Count == 0 && leitor.Cadastros == 0, "N-17 primeiro cadastro com CPF de fora da loja: recusa em uma linha, sem codigo");
            cx.Execute("DELETE FROM digital WHERE cpf_hash = @H", new { H = Digitais.CpfHash(CpfAna) });
            leitor = new LeitorFalso { EsperaCancelamento = true }; tela = new TelaFalsa { CpfPadrao = CpfAna }; tela.Escolhas.Enqueue(EscolhaNaLeitura.PrimeiraVez); remota = new RemotaFalsa { Aprova = false }; totp = new TelaTotpFalsa();
            d = await Entrar(true, leitor, tela, remota, totp);
            checar(!d.Entrou && leitor.Cadastros == 0 && Digitais.PorCpf(cx, CpfAna) is null && d.Motivo.Contains("Cadastro da digital não autorizado") && tela.VezesPediuCpf == 1, "N-18 codigo recusado: nao cadastra nem entra");
            leitor = new LeitorFalso { EsperaCancelamento = true, ProximoCadastro = Cadastro.Cancelado }; tela = new TelaFalsa { CpfPadrao = CpfAna }; tela.Escolhas.Enqueue(EscolhaNaLeitura.PrimeiraVez); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Entrar(true, leitor, tela, remota, totp);
            checar(!d.Entrou && Digitais.PorCpf(cx, CpfAna) is null && tela.AvisosDeCaixa.Any(a => a.Texto == Biometria.AvisoCadastroCancelado), "N-19 leitura do cadastro cancelada: nada guardado");
            leitor = new LeitorFalso { EsperaCancelamento = true, ProximoCadastro = Cadastro.Feito("winbio:3:12:bbbb:dedo:2", 3) with { ChaveParecida = "winbio:3:12:bbbb:dedo:2" } };
            tela = new TelaFalsa { CpfPadrao = CpfAna }; tela.Escolhas.Enqueue(EscolhaNaLeitura.PrimeiraVez); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Entrar(true, leitor, tela, remota, totp);
            checar(!d.Entrou && Digitais.PorCpf(cx, CpfAna) is null && Digitais.PorCpf(cx, CpfBia) is not null && tela.AvisosDeCaixa.Any(a => a.Texto == Biometria.AvisoDigitalDeOutro("BIA")), "N-20 o dedo ja e da BIA: barra e nao mexe na digital dela");
            leitor = new LeitorFalso { EsperaCancelamento = true }; tela = new TelaFalsa { CpfPadrao = null }; tela.Escolhas.Enqueue(EscolhaNaLeitura.PrimeiraVez); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await Entrar(true, leitor, tela, remota, totp);
            checar(!d.Entrou && d.Avisado && leitor.Cadastros == 0 && remota.Chamadas.Count == 0, "N-21 desistiu do CPF: nada, e nenhum codigo pedido");
            leitor = new LeitorFalso { Estado = EstadoLeitor.SemLeitor }; tela = new TelaFalsa { CpfPadrao = CpfAna }; remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            d = await PortaoDigital.PrimeiroCadastroAsync(leitor, tela, guarda, remota, totp, "CAIXA-01", agora27);
            checar(d.Via == ViaDigital.Reserva && remota.Chamadas.Count == 1 && remota.Chamadas[0].Tipo == Autorizacao.TipoTurnoSemDigital && leitor.Cadastros == 0, "N-22 primeiro cadastro sem leitor: cai na reserva, um codigo so");
            cx.Execute("UPDATE operador SET cpf = NULL WHERE id = 'op-ana'");
            checar(guarda.PorCpf(CpfAna) is null && guarda.PorCpf(CpfBia)?.Id == Bia.Id, "N-23 sem CPF no cadastro do painel o CPF digitado nao acha ninguem");
            cx.Execute("UPDATE operador SET cpf = @C WHERE id = 'op-ana'", new { C = CpfAna });
            var guardaHid = new GuardaDigitalSqlite(() => Banco.Abrir(arquivo), () => LeitorHidUareU.MotorTexto);
            checar(!guardaHid.JaTem(CpfBia) && guarda.JaTem(CpfBia), "N-24 digital do WinBio nao serve no leitor da HID: ali a BIA pode cadastrar");


            // ── O. 1.0.55: A AUTORIZACAO DO GERENTE SEM PIN (digital do gerente ou codigo) ──────
            Task<AutorizacaoGerente> Gerente(LeitorFalso l, TelaFalsa t, RemotaFalsa? r, TelaTotpFalsa tt, Operador? quemOpera, string tipo = "sangria")
                => PortaoGerente.AutorizarAsync(l, t, ch => Digitais.OperadorDaChave(cx, ch),
                    quemOpera is null ? null : op => Operadores.IdCanonico(cx, op.Id) == Operadores.IdCanonico(cx, quemOpera.Id),
                    r, tt, new PedidoAutorizacao("CAIXA-01", Autorizacao.ReferenciaSangria("ses-1", new DateTime(2026, 10, 27, 9, 0, 0)), 0) { Tipo = tipo }, "sangria");
            // a BIA (perfil gerente) tem digital aaaa? nao: a chave dela e bbbb:2; a ANA (operador) tem aaaa:1
            Digitais.Guardar(cx, "winbio:3:12:aaaa:dedo:1", CpfAna, Ana.Id, Ana.Nome, 1, null);
            leitor = new LeitorFalso(); leitor.Leituras.Enqueue(Captura.Reconhecida("winbio:3:12:bbbb:dedo:2")); tela = new TelaFalsa(); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            var ag = await Gerente(leitor, tela, remota, totp, Ana);
            checar(ag.Autorizado && ag.Via == AutorizacaoGerente.ViaDigital && ag.Gerente?.Id == Bia.Id && remota.Chamadas.Count == 0 && ag.Trilha == "digital do gerente BIA",
                "O-01 a digital da BIA (gerente) autoriza a sangria da ANA, sem codigo; a trilha diz a via");
            checar(tela.Titulos[0] == PortaoGerente.Titulo("sangria") && !tela.OfertasPrimeiraVez[0], "O-02 a leitura diz Gerente: encoste o dedo, sem Primeira vez");
            leitor = new LeitorFalso(); leitor.Leituras.Enqueue(Captura.Reconhecida("winbio:3:12:aaaa:dedo:1")); leitor.Leituras.Enqueue(Captura.Reconhecida("winbio:3:12:bbbb:dedo:2"));
            tela = new TelaFalsa(); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            ag = await Gerente(leitor, tela, remota, totp, Ana);
            checar(ag.Autorizado && ag.Gerente?.Id == Bia.Id && tela.Avisos[1] == PortaoGerente.AvisoNaoEGerente, "O-03 digital de funcionario comum (ANA) nao autoriza: Essa digital nao e de gerente");
            leitor = new LeitorFalso(); leitor.Leituras.Enqueue(Captura.Reconhecida("winbio:3:12:bbbb:dedo:2")); tela = new TelaFalsa(); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            for (var i = 0; i < 2; i++) leitor.Leituras.Enqueue(Captura.Reconhecida("winbio:3:12:bbbb:dedo:2"));
            ag = await Gerente(leitor, tela, remota, totp, Bia);
            checar(ag.Autorizado && ag.Via == AutorizacaoGerente.ViaCodigo && tela.Avisos[1] == PortaoGerente.AvisoQuemOpera && remota.Chamadas.Count == 1,
                "O-04 a gerente que opera nao autoriza a propria sangria com a digital: 3 vezes e cai no codigo");
            checar(remota.Chamadas[0].Tipo == Autorizacao.TipoSangria && remota.Chamadas[0].Nivel == Autorizacao.NivelGerente && remota.Chamadas[0].Referencia == "sangria:ses-1:20261027090000",
                "O-05 o codigo e do tipo sangria, nivel gerente (gerente geral ou dono), referencia com o turno");
            checar(ag.Gerente is { Nome: "Marcos", Perfil: "gerente" } && ag.Gerente.Id.StartsWith("totp:") && ag.Gerente.ESupervisor && ag.Trilha.StartsWith("codigo do gerente: Marcos (nivel gerente"),
                "O-06 pelo codigo, o gerente vira um autorizador com id totp: e o nome de quem aprovou");
            leitor = new LeitorFalso { EsperaCancelamento = true }; tela = new TelaFalsa(); tela.Escolhas.Enqueue(EscolhaNaLeitura.SemDigital); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            ag = await Gerente(leitor, tela, remota, totp, Ana, Autorizacao.TipoCaixaSemContagem);
            checar(ag.Autorizado && ag.Via == AutorizacaoGerente.ViaCodigo && remota.Chamadas[0].Tipo == "caixa_sem_contagem", "O-07 Sem digital na tela: direto ao codigo (fechar sem contar)");
            leitor = new LeitorFalso { Estado = EstadoLeitor.SemDriver }; tela = new TelaFalsa(); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            ag = await Gerente(leitor, tela, remota, totp, Ana, Autorizacao.TipoComandaGerente);
            checar(ag.Autorizado && ag.Via == AutorizacaoGerente.ViaCodigo && leitor.Identificacoes == 0 && remota.Chamadas[0].Tipo == "comanda_gerente", "O-08 leitor sem driver: avisa e vai ao codigo (comanda)");
            leitor = new LeitorFalso { Estado = EstadoLeitor.SemLeitor }; tela = new TelaFalsa(); remota = new RemotaFalsa { Aprova = false }; totp = new TelaTotpFalsa();
            ag = await Gerente(leitor, tela, remota, totp, Ana);
            checar(!ag.Autorizado && ag.Gerente is null && ag.Motivo.Contains("Sangria não autorizada"), "O-09 codigo recusado: ninguem autoriza, e a frase diz Sangria nao autorizada");
            leitor = new LeitorFalso { EsperaCancelamento = true }; tela = new TelaFalsa(); tela.Escolhas.Enqueue(EscolhaNaLeitura.Desistiu); remota = new RemotaFalsa(); totp = new TelaTotpFalsa();
            ag = await Gerente(leitor, tela, remota, totp, Ana);
            checar(!ag.Autorizado && ag.Avisado && remota.Chamadas.Count == 0, "O-10 Cancelar na leitura: nada autorizado, nenhum codigo pedido");
            cx.Execute("UPDATE operador SET ativo = 0 WHERE id = 'op-bia'");
            leitor = new LeitorFalso { Estado = EstadoLeitor.Pronto }; for (var i = 0; i < 3; i++) leitor.Leituras.Enqueue(Captura.Reconhecida("winbio:3:12:bbbb:dedo:2"));
            tela = new TelaFalsa(); remota = new RemotaFalsa { SemInternet = true }; totp = new TelaTotpFalsa();
            ag = await Gerente(leitor, tela, remota, totp, Ana);
            checar(!ag.Autorizado && tela.Avisos[1] == Biometria.AvisoDigitalDesconhecida, "O-11 gerente desligada no painel: a digital dela nao autoriza mais");
            cx.Execute("UPDATE operador SET ativo = 1 WHERE id = 'op-bia'");
            // o registro: o nome de quem autorizou pelo codigo fica guardado para a fila e o papel
            var porCodigo = new AutorizacaoGerente(true, AutorizacaoGerente.ViaCodigo, new Operador("totp:log-0001", "Marcos", "gerente"), "codigo do gerente", "log-0001-abcd", "gerente");
            PortaoGerente.Registrar(cx, porCodigo, Autorizacao.TipoSangria, Ana.Id);
            checar(PortaoGerente.NomeDoCodigo(cx, "totp:log-0001") == "Marcos" && PortaoGerente.NomeDoCodigo(cx, Bia.Id) is null, "O-12 o nome de quem autorizou pelo codigo fica guardado");
            var audG = cx.ExecuteScalar<string>("SELECT detalhe FROM auditoria WHERE evento='autorizacao_gerente' ORDER BY id DESC LIMIT 1")!;
            checar(audG.Contains("sangria") && audG.Contains("codigo do gerente: Marcos"), "O-13 a auditoria diz o ato, quem autorizou e por qual via");
            var sesG = Caixa.Abrir(cx, Ana, new Dinheiro(50000));
            Caixa.Movimentar(cx, sesG, "suprimento", new Dinheiro(10000), "troco", Ana);
            Caixa.Movimentar(cx, sesG, "sangria", new Dinheiro(5000), "cofre", Ana, porCodigo.Gerente!.Id, "cofre");
            checar(cx.ExecuteScalar<string>("SELECT autorizado_por FROM caixa_movimento WHERE tipo='sangria' ORDER BY criado_em DESC LIMIT 1") == "totp:log-0001",
                "O-14 a sangria autorizada pelo codigo grava o autorizador totp: (e a fila acha o nome pelo registro)");
            bool lancouS = false;
            try { Caixa.Movimentar(cx, sesG, "sangria", new Dinheiro(100), "cofre", Ana, Ana.Id, "cofre"); } catch (InvalidOperationException) { lancouS = true; }
            checar(lancouS, "O-15 o nucleo continua barrando a propria sangria");
            Caixa.Fechar(cx, sesG, new Dictionary<string, Dinheiro> { ["dinheiro"] = Caixa.Apurado(cx, sesG)["dinheiro"] }, Ana, new Dinheiro(200), "fechamento de teste do gerente sem pin");
            var fonteDrenG = File.ReadAllText(Caminho("Pdv.Nucleo/Drenagem.cs"));
            checar(fonteDrenG.Contains("PortaoGerente.NomeDoCodigo(cx, ap)"), "O-16 a fila da sangria manda o nome de quem autorizou pelo codigo");
            var telasG = new[] { "Telas/Venda.xaml.cs", "Telas/Venda.Comanda.cs", "Telas/AberturaCaixa.xaml.cs" }.Select(a => File.ReadAllText(Caminho(a))).ToList();
            checar(telasG.All(t => !t.Contains("AutorizarSupervisor(") && !t.Contains("PedirSenha.Mostrar(")), "O-17 nenhuma tela do turno pede PIN ou senha de gerente");
            checar(telasG[0].Contains("Autorizacao.TipoSangria") && telasG[1].Contains("Autorizacao.TipoComandaGerente") && telasG[2].Contains("Autorizacao.TipoCaixaSemContagem")
                   && telasG.All(t => t.Contains("PortaoGerenteDaTela.AutorizarAsync(")), "O-18 sangria, comanda e fechar sem contar passam pela digital ou codigo do gerente");
            var fonteTelaDigG = File.ReadAllText(Caminho("Telas/TelaDigital.cs"));
            checar(fonteTelaDigG.Contains("PortaoGerente.Registrar(cx, a, tipo") && fonteTelaDigG.Contains("autorizacao_gerente_negada"), "O-19 a tela registra quem autorizou (e a recusa)");
            checar(Autorizacao.TipoSangria == "sangria" && Autorizacao.TipoCaixaSemContagem == "caixa_sem_contagem" && Autorizacao.TipoComandaGerente == "comanda_gerente"
                   && Autorizacao.ReferenciaCaixaSemContagem("ses-1") == "semcontagem:ses-1", "O-20 os tipos novos do codigo (SQL 225)");
            cx.Execute("DELETE FROM digital WHERE chave = 'winbio:3:12:aaaa:dedo:1'");

            // N6. nenhuma senha no caminho do turno; o master segue na Configuracao
            var fonteLoginN = File.ReadAllText(Caminho("Telas/Login.xaml.cs"));
            var xamlLogin = File.ReadAllText(Caminho("Telas/Login.xaml"));
            checar(!fonteLoginN.Contains("Operadores.Entrar") && !fonteLoginN.Contains("EntrarComCpf") && !fonteLoginN.Contains("_senha") && !fonteLoginN.Contains("PinValido"),
                "N-25 a tela de entrada nao confere senha nem PIN de operador (CPF e senha e PIN legado fora do caixa)");
            checar(!xamlLogin.Contains("TecladoNumerico") && !xamlLogin.Contains("TxtPin") && !xamlLogin.Contains("BtnEntrar") && xamlLogin.Contains("BtnConfig"),
                "N-26 a tela nao tem teclado nem campo de senha; o botao Configuracao continua");
            var fonteMain = File.ReadAllText(Caminho("MainWindow.xaml.cs"));
            checar(fonteMain.Contains("t.PediuConfig += AbrirConfigProtegida") && fonteMain.Contains("AcessoDoMaster(\"Configuração do PDV\"") && fonteMain.Contains("UsuarioMaster.Conferir(cx, senha)"),
                "N-27 o master (admin da rede, nao e operador de turno) entra na Configuracao pela senha dele, como antes");
            var (mh, ms) = HashDeSenha.Gerar("98765432");
            UsuarioMaster.Aplicar(cx, new UsuarioMaster.Master("Brenno", mh, ms, new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc)));
            checar(UsuarioMaster.Conferir(cx, "98765432").Liberado && !UsuarioMaster.Conferir(cx, "4725").Liberado,
                "N-28 com a digital ligada a senha do master abre a Configuracao e a senha de operador nao abre");
            var textosN = new[] { Biometria.BotaoPrimeiraVez, Biometria.BotaoSemDigital, PortaoGerente.AvisoNaoEGerente, PortaoGerente.AvisoQuemOpera, PortaoGerente.Titulo("sangria"),
                Biometria.TituloCpfEntrada, Biometria.TituloCpfFechamento, Biometria.TituloCpfCadastro, Biometria.AvisoCpfInvalido, Biometria.AvisoCpfNaoEhFuncionario, Biometria.TituloEntrada, Biometria.TituloFechamento, Biometria.TituloPrimeiraVez };
            checar(textosN.All(t => !t.Contains("senha", StringComparison.OrdinalIgnoreCase) && t.Split('\n').Length == 1 && !t.Contains((char)0x2014) && !t.Contains((char)0x2013)),
                "N-29 os textos da entrada sao de uma linha, sem travessao e sem a palavra senha");
            checar(!File.ReadAllText(Caminho("Telas/TelaDigital.cs")).Contains("CPF e senha"), "N-30 a tela da leitura nao oferece mais CPF e senha");
            var fonteAutN = File.ReadAllText(Caminho("Pdv.Nucleo/Autorizacao.cs"));
            checar(fonteAutN.Contains("TipoDigitalCadastro => (\"Cadastro da digital\""), "N-31 a recusa do codigo diz Cadastro da digital nao autorizado");

            // ── M. textos de tela sem travessao; a versao ───────────────────────────────
            var textos = new[] { Biometria.TituloEntrada, Biometria.TituloFechamento, Biometria.TituloPrimeiraVez, Biometria.TituloRecadastro,
                Biometria.AvisoSemLeitor, Biometria.AvisoSemDriver, Biometria.AvisoSemServico, Biometria.AvisoDigitalDesconhecida, Biometria.AvisoJaTemDigital,
                Biometria.AvisoCadastroCancelado, Biometria.AvisoVagasEsgotadas, Biometria.AvisoNaoReconheci(2), DriverDigitalPersona.PassoDoDriver };
            checar(textos.All(t => !t.Contains((char)0x2014) && !t.Contains((char)0x2013)), "M-01 nenhum texto da digital com travessao");
            checar(textos.All(t => t.Split('\n').Length == 1), "M-02 todos os textos sao de uma linha");
            var novos = new[] { "Pdv.Nucleo/Biometria.cs", "Pdv.Nucleo/LeitorWinBio.cs", "Pdv.Nucleo/RegistroDigital.cs", "Telas/TelaDigital.cs", "Pdv.Testes/TestesBiometria.cs" }
                .Select(a => File.Exists(Caminho(a)) ? File.ReadAllText(Caminho(a)) : "").ToList();
            checar(novos.All(t => t.Length > 0 && !t.Contains((char)0x2014) && !t.Contains((char)0x2013)), "M-03 os arquivos novos existem e nao tem travessao");
            checar(File.ReadAllText(Caminho("Pdv.csproj")).Contains("<Version>1.0.57</Version>"), "M-04 a versao do caixa e 1.0.57 (texto do leitor)");
            var fonteAut = File.ReadAllText(Caminho("Pdv.Nucleo/Autorizacao.cs"));
            checar(fonteAut.Contains("TipoTurnoSemDigital => (\"Entrada sem digital\"") && Autorizacao.TipoTurnoSemDigital == "turno_sem_digital" && Autorizacao.TipoDigitalRecadastro == "digital_recadastro", "M-05 os tipos novos do TOTP e a frase da recusa");
            var fonteLogin = File.ReadAllText(Caminho("Telas/Login.xaml.cs"));
            var fonteVenda = File.ReadAllText(Caminho("Telas/Venda.xaml.cs"));
            var fonteAbert = File.ReadAllText(Caminho("Telas/AberturaCaixa.xaml.cs"));
            checar(fonteLogin.Contains("PortaoDigitalDaTela.EntrarAsync(dono)") && !fonteLogin.Contains("Biometria.Ligada("), "M-06 a entrada passa pelo portao, sem chave de desligar");
            checar(fonteVenda.Contains("PortaoDigitalDaTela.IdentificarAsync(dono, \"fechamento\"") && !fonteVenda.Contains("Biometria.Ligada(") && !fonteAbert.Contains("Biometria.Ligada("),
                "M-07 o fechamento do caixa sempre passa pelo portao (biometria obrigatoria)");
            checar(fonteAbert.Contains("PortaoDigitalDaTela.IdentificarAsync(dono, \"fechamento\"") && fonteAbert.Contains("EntradaDigital.Tomar()"), "M-08 fechar o caixa esquecido (na abertura) tambem passa pelo portao");
            var fonteTelaDig = File.ReadAllText(Caminho("Telas/TelaDigital.cs"));
            checar(fonteTelaDig.Contains("new GuardaDigitalSqlite(") && fonteTelaDig.Contains("Servicos.MotorDoLeitor"), "M-09 o primeiro cadastro usa a guarda de verdade e o motor em uso");
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
