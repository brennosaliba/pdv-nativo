using System.Text.Json;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// CODIGOS DE EMERGENCIA (1.0.56, decisao do dono de 27/10/2026; SQL 228 no ERP).
///
/// Sem internet, o leitor de digital falhou e o 2FA nao tem como ser conferido: o caixa aceita um
/// codigo de 8 digitos de uso unico, conferido contra o pacote de hashes (PBKDF2) guardado cifrado.
/// O que esta suite protege, em ordem de estrago:
///  · SO OFFLINE: com rede (codigo errado, recusa da nuvem, autenticador nao configurado) a tela de
///    emergencia nem aparece e a guarda nem e consultada;
///  · SO OS ATOS DA REGRA: entrada/fechamento pela reserva, sangria ate o teto, fechar sem contar.
///    Estorno, cancelamento, comanda, promocao e o resto: "Sem internet." e nada mais;
///  · USO UNICO: o codigo vale uma vez neste caixa (marcado na hora), mesmo que o pacote novo da
///    nuvem ainda o traga; e o codigo nunca vai para fila, log ou auditoria;
///  · o teto da sangria (padrao R$ 300; o do painel), a trava de 5 erros em 10 minutos;
///  · a fila sobe o uso (idempotente, espera o SQL 228) e o PBKDF2 bate com o do banco.
/// </summary>
public static class TestesEmergencia
{
    private const string Terminal = "Caixa Savassi 1";
    private const string TermUuid = "t-sav1";
    private const int Iter = 10000;
    private const string Cpf = "52998224725";

    private sealed class Remota : IAutorizacaoRemota
    {
        public Func<string, RespostaTotp> Responder = _ => new RespostaTotp(false, false, "sem_resposta", null, null);
        public int Chamadas;
        public Task<RespostaTotp> ValidarTotpAsync(string codigo, string referencia, string tipo,
            IReadOnlyDictionary<string, object?>? detalhe, string nivel, CancellationToken ct)
        {
            Chamadas++;
            return Task.FromResult(Responder(codigo));
        }
    }

    private sealed class Tela : ITelaAutorizacao
    {
        public readonly Queue<string?> Codigos = new();
        public readonly Queue<string?> Emergencias = new();
        public readonly List<string?> AvisosEmergencia = new();
        public int PediuCodigo, PediuEmergencia;
        private sealed class Nada : IDisposable { public void Dispose() { } }
        public IDisposable Aguardando(string mensagem) => new Nada();
        public Task<string?> PedirCodigoAsync(string? aviso, string nivel)
        {
            PediuCodigo++;
            return Task.FromResult(Codigos.Count > 0 ? Codigos.Dequeue() : null);
        }
        public Task<string?> PedirCodigoEmergenciaAsync(string? aviso)
        {
            PediuEmergencia++;
            AvisosEmergencia.Add(aviso);
            return Task.FromResult(Emergencias.Count > 0 ? Emergencias.Dequeue() : null);
        }
    }

    /// <summary>A tela de antes (sem o metodo novo): o padrao da interface e cancelar.</summary>
    private sealed class TelaAntiga : ITelaAutorizacao
    {
        private sealed class Nada : IDisposable { public void Dispose() { } }
        public IDisposable Aguardando(string mensagem) => new Nada();
        public Task<string?> PedirCodigoAsync(string? aviso, string nivel) => Task.FromResult<string?>("123456");
    }

    private sealed class Contadora : ICodigosEmergencia
    {
        public int Situacoes, Conferencias;
        public SituacaoEmergencia Situacao(PedidoAutorizacao pedido, DateTime agora) { Situacoes++; return SituacaoEmergencia.Pronta; }
        public ConferenciaEmergencia Conferir(string codigo, PedidoAutorizacao pedido, DateTime agora)
        { Conferencias++; return new ConferenciaEmergencia(true, "nunca-deveria"); }
    }

    private sealed class SemLeitor : ILeitorDigital
    {
        public string Nome => "sem leitor";
        public Task<SondaLeitor> SondarAsync(CancellationToken ct) => Task.FromResult(new SondaLeitor(EstadoLeitor.SemLeitor, "teste", Array.Empty<string>()));
        public Task<Captura> IdentificarAsync(CancellationToken ct) => Task.FromResult(Captura.Cancelada);
        public Task<Cadastro> CadastrarAsync(int vaga, IProgress<int>? amostra, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ApagarAsync(string chave, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class TelaDigital : ITelaDigital
    {
        public string? Cpf;
        public Task<LeituraNaTela> LerAsync(string titulo, string? aviso, bool ofertaPrimeiraVez, Func<CancellationToken, Task<Captura>> ler)
            => Task.FromResult(new LeituraNaTela(null, EscolhaNaLeitura.Desistiu));
        public Task<Cadastro> CadastrarAsync(string titulo, Func<IProgress<string>, CancellationToken, Task<Cadastro>> cadastrar) => throw new NotSupportedException();
        public Task<string?> PedirCpfAsync(string titulo, string? aviso) => Task.FromResult(Cpf);
        public void Avisar(string titulo, string texto) { }
    }

    private static readonly RespostaTotp SemRede = new(false, false, "sem_resposta", null, null);
    private static readonly RespostaTotp CodigoErrado = new(false, true, Autorizacao.MotivoCodigoInvalido, null, null);

    private static string Sal(int n) => Convert.ToHexString(Enumerable.Range(n, 16).Select(i => (byte)i).ToArray()).ToLowerInvariant();

    /// <summary>O pacote como a RPC manda: (id, codigo em claro, terminal) viram id + hash + sal.</summary>
    private static string Pacote(long teto, params (string Id, string Codigo, string? Term)[] cods)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = true, ["loja"] = "American Day Savassi", ["terminal_uuid"] = TermUuid, ["teto_sangria_cent"] = teto,
            ["gerado_em"] = "2026-10-27T15:00:00Z",
            ["codigos"] = cods.Select((c, i) => new Dictionary<string, object?>
            {
                ["id"] = c.Id, ["lote"] = "lote-1", ["hash"] = Emergencia.Hash(c.Codigo, Sal(i), Iter), ["sal"] = Sal(i),
                ["iteracoes"] = Iter, ["terminal_uuid"] = c.Term,
            }).ToList(),
        });

    private static PedidoAutorizacao Pedido(string tipo, long valor = 0, string? finalidade = null)
        => new(Terminal, $"{tipo}:ref:{Guid.NewGuid():N}", valor)
        {
            Tipo = tipo, Nivel = Autorizacao.NivelGerente, Operador = "ANA", CpfHash = Digitais.CpfHash(Cpf), Finalidade = finalidade,
        };

    private static Task<DesfechoAutorizacao> Resolver(Remota r, Tela t, PedidoAutorizacao p, ICodigosEmergencia em)
        => Autorizacao.ResolverAsync(r, p, t, CancellationToken.None, em);

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"emergencia_teste_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        var emAnterior = Autorizacao.EmergenciaDoCaixa;
        var agoraAnterior = Autorizacao.Agora;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            var guarda = new GuardaEmergencia(() => Banco.Abrir(arquivo), () => TermUuid);
            // relogio que anda 11 minutos a cada leitura: os erros de proposito de uma secao nunca
            // somam na trava de 10 minutos da outra (a secao B fixa o relogio para testar a trava)
            var passo = 0;
            var base0 = new DateTime(2031, 1, 1, 8, 0, 0);
            Autorizacao.Agora = () => base0.AddMinutes(11 * Interlocked.Increment(ref passo));

            // ── P. o PBKDF2 e o mesmo do banco (SQL 228) ───────────────────────
            checar(Emergencia.Hash("12345678", "000102030405060708090a0b0c0d0e0f", 30000)
                   == "e805c8b5f549078802277cbb8fc6c10259868faf0399477441cb0eed688950f7",
                "P-01 PBKDF2 cruzado com o banco: 12345678, sal 00..0f, 30000 = o vetor do testes/db/388 (P-03)");
            checar(Emergencia.Hash("password", Convert.ToHexString("salt"u8.ToArray()), 1)
                   == "120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b",
                "P-02 PBKDF2-HMAC-SHA256 vetor padrao (password, salt, 1)");

            // ── R. regras puras ────────────────────────────────────────────────
            checar(Emergencia.TiposQueValem.SetEquals(new[] { "turno_sem_digital", "sangria", "caixa_sem_contagem" }),
                "R-01 so tres atos aceitam codigo de emergencia: entrada/fechamento pela reserva, sangria e fechar sem contar");
            var proibidos = new[] { "estorno", "cancelamento", "promocao", Autorizacao.TipoComandaGerente, Autorizacao.TipoGestorInteiro,
                "fechar_pdv", Autorizacao.TipoDigitalCadastro, Autorizacao.TipoDigitalRecadastro, Autorizacao.TipoResgateDesfazer, "", null };
            checar(proibidos.All(t => !Emergencia.TipoVale(t)),
                "R-02 estorno, cancelamento, promocao, comanda, Gestor, fechar o PDV, cadastro da digital, resgate: NAO valem");
            checar(Emergencia.FormatoValido("01234567") && !Emergencia.FormatoValido("1234567") && !Emergencia.FormatoValido("123456789")
                   && !Emergencia.FormatoValido("1234567a") && !Emergencia.FormatoValido(null),
                "R-03 o codigo tem exatamente 8 digitos");
            checar(Emergencia.LerPacote("{\"ok\":false,\"motivo\":\"sem_terminal\"}") is null && Emergencia.LerPacote("lixo") is null
                   && Emergencia.LerPacote(null) is null,
                "R-04 resposta que nao e pacote (ok false, lixo) = null: o caixa fica com o que tem");
            var torto = Emergencia.LerPacote("""
                {"ok":true,"codigos":[{"id":"a","hash":"zz","sal":"00","iteracoes":30000},
                  {"id":"b","hash":"{H}","sal":"000102030405060708090a0b0c0d0e0f","iteracoes":5},
                  {"id":"c","hash":"{H}","sal":"000102030405060708090a0b0c0d0e0f","iteracoes":30000}]}
                """.Replace("{H}", new string('a', 64)));
            checar(torto is not null && torto.Codigos.Count == 1 && torto.Codigos[0].Id == "c" && torto.TetoSangriaCent == Emergencia.TetoPadraoCent,
                "R-05 linha fora do contrato (hash, sal, iteracoes) fica de fora; sem teto = R$ 300 (padrao)");
            var agora = new DateTime(2026, 10, 27, 15, 0, 0);
            checar(Emergencia.PacoteVencido(null, agora) && Emergencia.PacoteVencido(agora.AddMinutes(-15).ToString("o"), agora)
                   && !Emergencia.PacoteVencido(agora.AddMinutes(-14).ToString("o"), agora)
                   && Emergencia.PacoteVencido(agora.AddHours(2).ToString("o"), agora),
                "R-06 o pacote desce de novo a cada 15 min com sinal (relogio no futuro tambem pede)");
            checar(Emergencia.Reais(30000) == "R$ 300,00" && Emergencia.MotivoAcimaDoTeto(30000).Contains("R$ 300,00"),
                "R-07 o teto aparece em reais na recusa");

            // ── G. a guarda: pacote cifrado, substituido inteiro ───────────────
            using (var cx = Banco.Abrir(arquivo))
            {
                var mudou = GuardaEmergencia.AplicarPacote(cx, Pacote(30000, ("c1", "11111111", null), ("c2", "22222222", null),
                    ("c3", "33333333", null), ("c4", "44444444", null), ("c5", "55555555", null), ("c9", "99999999", "t-outro")), agora);
                var cifrado = cx.ExecuteScalar<string>("SELECT cifrado FROM emergencia_pacote WHERE id = 1");
                checar(mudou is { Length: > 0 } && cifrado.StartsWith("dpapi:", StringComparison.Ordinal)
                       && !cifrado.Contains(Emergencia.Hash("11111111", Sal(0), Iter), StringComparison.Ordinal),
                    "G-01 o pacote fica cifrado pelo DPAPI (nem o hash aparece em claro no SQLite)");
                checar(GuardaEmergencia.PacoteGuardado(cx)?.Codigos.Count == 6,
                    "G-02 o pacote guardado volta inteiro");
                checar(GuardaEmergencia.AplicarPacote(cx, "{\"ok\":false}", agora) is null && GuardaEmergencia.PacoteGuardado(cx)?.Codigos.Count == 6,
                    "G-03 resposta ruim nao apaga o guardado");
            }

            // ── O. SO OFFLINE ──────────────────────────────────────────────────
            {
                var r = new Remota { Responder = _ => CodigoErrado };
                var t = new Tela();
                t.Codigos.Enqueue("111111"); t.Codigos.Enqueue("222222"); t.Codigos.Enqueue("333333");
                var cont = new Contadora();
                var d = await Resolver(r, t, Pedido(Autorizacao.TipoTurnoSemDigital), cont);
                checar(!d.Autorizado && t.PediuEmergencia == 0 && cont.Situacoes == 0 && cont.Conferencias == 0,
                    "O-01 COM REDE e codigo errado: a tela de emergencia nao aparece e a guarda nem e consultada");

                r.Responder = _ => new RespostaTotp(false, true, "autenticador nao configurado", null, null);
                var t2 = new Tela(); t2.Codigos.Enqueue("111111");
                var d2 = await Resolver(r, t2, Pedido(Autorizacao.TipoSangria, 1000), cont);
                checar(!d2.Autorizado && t2.PediuEmergencia == 0 && cont.Situacoes == 0,
                    "O-02 recusa definitiva da nuvem (autenticador nao configurado): nada de emergencia");

                r.Responder = _ => new RespostaTotp(true, true, null, "log-1", "Ingrid");
                var t3 = new Tela(); t3.Codigos.Enqueue("111111");
                var d3 = await Resolver(r, t3, Pedido(Autorizacao.TipoSangria, 1000), cont);
                checar(d3.Via == ViaAutorizacao.Totp && t3.PediuEmergencia == 0 && cont.Situacoes == 0,
                    "O-03 com rede e codigo certo: o 2FA de sempre (Via=Totp), a emergencia nao entra");

                var t4 = new Tela(); t4.Codigos.Enqueue("11111111");
                r.Responder = _ => CodigoErrado;
                var d4 = await Resolver(r, t4, Pedido(Autorizacao.TipoTurnoSemDigital), guarda);
                checar(!d4.Autorizado && t4.PediuEmergencia == 0 && r.Chamadas >= 1,
                    "O-04 o codigo de emergencia digitado na tela normal COM rede vai para a nuvem como 2FA e nao vale");
            }

            // ── T. os tipos proibidos, mesmo sem rede ──────────────────────────
            {
                var cont = new Contadora();
                var tiposRuins = new[] { "estorno", "cancelamento", "promocao", Autorizacao.TipoComandaGerente, Autorizacao.TipoGestorInteiro,
                    Autorizacao.TipoDigitalCadastro, Autorizacao.TipoDigitalRecadastro, "fechar_pdv" };
                var todosRecusados = true;
                foreach (var tipo in tiposRuins)
                {
                    var t = new Tela(); t.Codigos.Enqueue("123456");
                    var d = await Resolver(new Remota { Responder = _ => SemRede }, t, Pedido(tipo, 1000), cont);
                    todosRecusados &= !d.Autorizado && d.Via == ViaAutorizacao.Recusada && t.PediuEmergencia == 0 && d.Motivo.StartsWith("Sem internet.");
                }
                checar(todosRecusados && cont.Situacoes == 0 && cont.Conferencias == 0,
                    "T-01 SEM REDE, estorno/cancelamento/promocao/comanda/Gestor/digital/fechar o PDV: 'Sem internet.' e a guarda nem e consultada");
                Autorizacao.EmergenciaDoCaixa = () => cont;
                var tE = new Tela(); tE.Codigos.Enqueue("123456");
                var dE = await Autorizacao.ResolverAsync(new Remota { Responder = _ => SemRede }, Pedido("estorno", 1000), tE);
                checar(!dE.Autorizado && cont.Situacoes == 0 && tE.PediuEmergencia == 0,
                    "T-02 nem a emergencia do caixa (Servicos) e oferecida no estorno");
                Autorizacao.EmergenciaDoCaixa = emAnterior;
            }

            // ── F. o caminho feliz e o uso unico ───────────────────────────────
            {
                var r = new Remota { Responder = _ => SemRede };
                var t = new Tela(); t.Codigos.Enqueue("123456"); t.Emergencias.Enqueue("11111111");
                var p = Pedido(Autorizacao.TipoTurnoSemDigital, finalidade: "entrada");
                var d = await Resolver(r, t, p, guarda);
                checar(d.Autorizado && d.Via == ViaAutorizacao.Emergencia && d.TokenId == "c1" && d.Autorizador == "emerg:c1"
                       && d.AprovadoPor == Emergencia.AprovadoPor && t.AvisosEmergencia[0] == Emergencia.AvisoDigite,
                    "F-01 SEM REDE na entrada pela reserva: o codigo certo autoriza (Via=Emergencia, registro emerg:c1)");
                using (var cx = Banco.Abrir(arquivo))
                {
                    var uso = cx.QueryFirstOrDefault("SELECT * FROM emergencia_uso WHERE codigo_id = 'c1'");
                    checar(uso is not null && (string)uso.usado_para == "turno_sem_digital" && (string)uso.finalidade == "entrada"
                           && (string)uso.cpf_hash == Digitais.CpfHash(Cpf),
                        "F-02 o uso fica marcado NA HORA neste caixa, com o hash do CPF e para que");
                    var fila = cx.QueryFirstOrDefault("SELECT * FROM outbox WHERE tipo = @T", new { T = Emergencia.TipoNaFila });
                    checar(fila is not null && (string)fila.ref_id == "c1" && ((string)fila.payload).Contains("\"id\":\"c1\"")
                           && !((string)fila.payload).Contains("11111111"),
                        "F-03 a fila leva o uso (id, para que, referencia) e NUNCA o codigo");
                    var aud = cx.Query<string>("SELECT COALESCE(detalhe,'') || COALESCE(autorizador,'') FROM auditoria WHERE evento LIKE 'emergencia%'").ToList();
                    checar(aud.Count == 1 && aud.All(a => !a.Contains("11111111")) && aud[0].Contains("emerg:c1"),
                        "F-04 auditoria local do uso, sem o codigo");
                }

                var t2 = new Tela(); t2.Codigos.Enqueue("123456");
                t2.Emergencias.Enqueue("11111111"); t2.Emergencias.Enqueue("11111111"); t2.Emergencias.Enqueue("11111111");
                var d2 = await Resolver(r, t2, Pedido(Autorizacao.TipoTurnoSemDigital), guarda);
                checar(!d2.Autorizado && t2.PediuEmergencia == 3 && d2.Motivo.Contains(Emergencia.MotivoInvalidoVezes(3))
                       && t2.AvisosEmergencia[1] == Emergencia.AvisoInvalido,
                    "F-05 USO UNICO: o mesmo codigo de novo neste caixa nao vale (3 vezes e desiste)");

                using (var cx = Banco.Abrir(arquivo))
                    GuardaEmergencia.AplicarPacote(cx, Pacote(30000, ("c1", "11111111", null), ("c2", "22222222", null),
                        ("c3", "33333333", null), ("c4", "44444444", null), ("c5", "55555555", null), ("c9", "99999999", "t-outro")), agora.AddMinutes(1));
                var t3 = new Tela(); t3.Codigos.Enqueue("123456"); t3.Emergencias.Enqueue("11111111");
                var d3 = await Resolver(r, t3, Pedido(Autorizacao.TipoCaixaSemContagem), guarda);
                checar(!d3.Autorizado, "F-06 o pacote novo ainda traz o usado (a nuvem nao soube): o uso local vale, nao passa");

                var t4 = new Tela(); t4.Codigos.Enqueue("123456"); t4.Emergencias.Enqueue("99999999");
                var d4 = await Resolver(r, t4, Pedido(Autorizacao.TipoCaixaSemContagem), guarda);
                checar(!d4.Autorizado, "F-07 codigo gerado para OUTRO caixa nao vale aqui");

                var t5 = new Tela(); t5.Codigos.Enqueue("123456"); t5.Emergencias.Enqueue("22222222");
                var d5 = await Resolver(r, t5, Pedido(Autorizacao.TipoCaixaSemContagem, finalidade: "fechar sem contar"), guarda);
                checar(d5.Via == ViaAutorizacao.Emergencia && d5.TokenId == "c2", "F-08 fechar sem contar sem internet: o codigo vale");

                var t6 = new Tela(); t6.Codigos.Enqueue("123456");
                var d6 = await Resolver(r, t6, Pedido(Autorizacao.TipoTurnoSemDigital), guarda);
                checar(!d6.Autorizado && d6.Avisado && t6.PediuEmergencia == 1,
                    "F-09 o operador cancela a tela de emergencia: nao autoriza, sem segundo aviso");

                var d7 = await Autorizacao.ResolverAsync(r, Pedido(Autorizacao.TipoTurnoSemDigital), new TelaAntiga(), CancellationToken.None, guarda);
                checar(!d7.Autorizado, "F-10 tela que nao sabe pedir o codigo de emergencia: o padrao e cancelar, nunca aprovar");
            }

            // ── S. o teto da sangria ───────────────────────────────────────────
            {
                var r = new Remota { Responder = _ => SemRede };
                var t = new Tela(); t.Codigos.Enqueue("123456");
                var d = await Resolver(r, t, Pedido(Autorizacao.TipoSangria, 30001), guarda);
                checar(!d.Autorizado && t.PediuEmergencia == 0 && d.Motivo.Contains("R$ 300,00"),
                    "S-01 sangria de R$ 300,01 sem internet: acima do teto, nem pede o codigo");
                var t0 = new Tela(); t0.Codigos.Enqueue("123456");
                var d0 = await Resolver(r, t0, Pedido(Autorizacao.TipoSangria, 0), guarda);
                checar(!d0.Autorizado && t0.PediuEmergencia == 0, "S-02 sangria sem valor: nao sai (nunca no escuro)");
                var t1 = new Tela(); t1.Codigos.Enqueue("123456"); t1.Emergencias.Enqueue("33333333");
                var d1 = await Resolver(r, t1, Pedido(Autorizacao.TipoSangria, 30000), guarda);
                checar(d1.Via == ViaAutorizacao.Emergencia && d1.TokenId == "c3", "S-03 sangria de R$ 300,00 (no teto): vale");
                using (var cx = Banco.Abrir(arquivo))
                {
                    checar(cx.ExecuteScalar<long>("SELECT valor_cent FROM emergencia_uso WHERE codigo_id = 'c3'") == 30000,
                        "S-04 o valor da sangria vai junto do uso");
                    GuardaEmergencia.AplicarPacote(cx, Pacote(15000, ("c4", "44444444", null), ("c5", "55555555", null)), agora.AddMinutes(2));
                }
                var t2 = new Tela(); t2.Codigos.Enqueue("123456");
                var d2 = await Resolver(r, t2, Pedido(Autorizacao.TipoSangria, 20000), guarda);
                checar(!d2.Autorizado && t2.PediuEmergencia == 0 && d2.Motivo.Contains("R$ 150,00"),
                    "S-05 o teto do painel (R$ 150) vale: R$ 200 nao sai");
            }

            // ── B. a trava de 5 erros em 10 minutos ────────────────────────────
            {
                var r = new Remota { Responder = _ => SemRede };
                var fixo = new DateTime(2035, 6, 1, 15, 0, 0);
                Autorizacao.Agora = () => fixo;
                for (var i = 0; i < 2; i++)
                {
                    var t = new Tela(); t.Codigos.Enqueue("123456");
                    for (var k = 0; k < 3; k++) t.Emergencias.Enqueue("00000000");
                    await Resolver(r, t, Pedido(Autorizacao.TipoTurnoSemDigital), guarda);
                }
                var tb = new Tela(); tb.Codigos.Enqueue("123456"); tb.Emergencias.Enqueue("44444444");
                var db = await Resolver(r, tb, Pedido(Autorizacao.TipoTurnoSemDigital), guarda);
                checar(!db.Autorizado && tb.PediuEmergencia == 0 && db.Motivo.Contains(Emergencia.MotivoBloqueada),
                    "B-01 seis codigos errados: a emergencia trava (nem o certo passa) por 10 minutos");
                Autorizacao.Agora = () => fixo.AddMinutes(11);
                var tc = new Tela(); tc.Codigos.Enqueue("123456"); tc.Emergencias.Enqueue("44444444");
                var dc = await Resolver(r, tc, Pedido(Autorizacao.TipoTurnoSemDigital), guarda);
                checar(dc.Via == ViaAutorizacao.Emergencia && dc.TokenId == "c4", "B-02 passados 10 minutos, o codigo certo volta a valer");
                Autorizacao.Agora = () => base0.AddMinutes(11 * Interlocked.Increment(ref passo));
                using var cx = Banco.Abrir(arquivo);
                checar(cx.Query<string>("SELECT detalhe FROM auditoria WHERE evento = 'emergencia_recusada'").All(x => !x.Contains("00000000")),
                    "B-03 a recusa audita sem o codigo digitado");
            }

            // ── Z. sem pacote ──────────────────────────────────────────────────
            {
                var vazio = Path.Combine(Path.GetTempPath(), $"emergencia_vazio_{Guid.NewGuid():N}.db");
                Banco.Migrar(vazio);
                var g0 = new GuardaEmergencia(() => Banco.Abrir(vazio), () => TermUuid);
                var t = new Tela(); t.Codigos.Enqueue("123456");
                var d = await Resolver(new Remota { Responder = _ => SemRede }, t, Pedido(Autorizacao.TipoTurnoSemDigital), g0);
                checar(!d.Autorizado && t.PediuEmergencia == 0 && d.Motivo.Contains(Emergencia.MotivoSemCodigos),
                    "Z-01 caixa sem pacote: 'sem codigo de emergencia neste caixa', nem pede");
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { File.Delete(vazio); } catch { }
            }

            // ── C. o cliente da nuvem: token vencido sem rede = "nao sei" ──────
            {
                var semRedeCli = new ClienteAutorizacao(_ => Task.FromResult<string?>(null), "http://127.0.0.1:9", "anon", TimeSpan.FromSeconds(1),
                    () => TermUuid, semRede: () => true);
                var v1 = await semRedeCli.ValidarTotpAsync("123456", "turno:x", Autorizacao.TipoTurnoSemDigital, null, "gerente", CancellationToken.None);
                var comRedeCli = new ClienteAutorizacao(_ => Task.FromResult<string?>(null), "http://127.0.0.1:9", "anon", TimeSpan.FromSeconds(1),
                    () => TermUuid, semRede: () => false);
                var v2 = await comRedeCli.ValidarTotpAsync("123456", "turno:x", Autorizacao.TipoTurnoSemDigital, null, "gerente", CancellationToken.None);
                checar(!v1.Ok && !v1.Definitiva && v2.Definitiva && v2.Motivo == ClienteAutorizacao.MotivoSemSessao,
                    "C-01 sem token porque a renovacao nao achou a rede: 'nao sei' (abre a emergencia); sem token por outro motivo: 'sem sessao'");
            }

            // ── E. a reserva da digital e o gerente, ponta a ponta ─────────────
            {
                Autorizacao.EmergenciaDoCaixa = () => guarda;
                var opAna = new Operador("op-ana", "ANA", "operador");
                var telaD = new TelaDigital { Cpf = Cpf };
                var tt = new Tela(); tt.Codigos.Enqueue("123456"); tt.Emergencias.Enqueue("55555555");
                var dd = await PortaoDigital.ReservaAsync(telaD, cpf => cpf == Cpf ? opAna : null, new Remota { Responder = _ => SemRede }, tt,
                    Terminal, "fechamento", "sem leitor");
                checar(dd.Via == ViaDigital.Reserva && dd.Operador?.Nome == "ANA" && dd.NivelCodigo == DesfechoDigital.NivelEmergencia
                       && dd.MotivoAuditado.Contains("codigo de emergencia sem internet") && dd.Registro == "c5",
                    "E-01 fechamento pela reserva sem internet: CPF + codigo de emergencia, auditado como emergencia");
                using (var cx = Banco.Abrir(arquivo))
                    checar(cx.ExecuteScalar<string>("SELECT finalidade FROM emergencia_uso WHERE codigo_id = 'c5'") == "fechamento"
                           && cx.ExecuteScalar<string>("SELECT cpf_hash FROM emergencia_uso WHERE codigo_id = 'c5'") == Digitais.CpfHash(Cpf),
                        "E-02 o uso diz fechamento e quem (hash do CPF)");

                using (var cx = Banco.Abrir(arquivo))
                    GuardaEmergencia.AplicarPacote(cx, Pacote(30000, ("c6", "66666666", null)), agora.AddMinutes(3));
                var tg = new Tela(); tg.Codigos.Enqueue("123456"); tg.Emergencias.Enqueue("66666666");
                var pg = new PedidoAutorizacao(Terminal, Autorizacao.ReferenciaSangria("sess-1", agora), 12000) { Tipo = Autorizacao.TipoSangria, Operador = "ANA" };
                var ag = await PortaoGerente.AutorizarAsync(new SemLeitor(), telaD, _ => null, null, new Remota { Responder = _ => SemRede }, tg, pg, "sangria");
                checar(ag.Autorizado && ag.Via == AutorizacaoGerente.ViaEmergencia && ag.Gerente?.Id == "emerg:c6"
                       && ag.Trilha.Contains("codigo de emergencia sem internet"),
                    "E-03 sangria de R$ 120 sem leitor e sem internet: o portao do gerente aceita o codigo de emergencia");
                using (var cx = Banco.Abrir(arquivo))
                {
                    PortaoGerente.Registrar(cx, ag, Autorizacao.TipoSangria, "op-ana");
                    checar(PortaoGerente.NomeDoCodigo(cx, "emerg:c6") == Emergencia.AprovadoPor,
                        "E-04 a fila da sangria acha o nome de quem autorizou ('codigo de emergencia')");
                }
                var tx = new Tela(); tx.Codigos.Enqueue("123456"); tx.Emergencias.Enqueue("66666666");
                var px = new PedidoAutorizacao(Terminal, Autorizacao.ReferenciaComanda("conta-1", "cancelar", agora), 0) { Tipo = Autorizacao.TipoComandaGerente };
                var ax = await PortaoGerente.AutorizarAsync(new SemLeitor(), telaD, _ => null, null, new Remota { Responder = _ => SemRede }, tx, px, "comanda");
                checar(!ax.Autorizado && tx.PediuEmergencia == 0, "E-05 cancelar comanda sem internet: o codigo de emergencia NAO vale");
                Autorizacao.EmergenciaDoCaixa = emAnterior;
            }

            // ── Q. a fila ──────────────────────────────────────────────────────
            {
                string? corpoVisto = null;
                var ok = await Emergencia.ResolverNaFilaAsync("{\"id\":\"c1\",\"usado_para\":\"sangria\",\"referencia\":\"r\"}", TermUuid,
                    (nome, corpo) => { corpoVisto = corpo; return Task.FromResult((200, (string?)"{\"ok\":true,\"registrados\":1}")); });
                using var doc = JsonDocument.Parse(corpoVisto!);
                checar(ok.Ok == true && doc.RootElement.GetProperty("_terminal_uuid").GetString() == TermUuid
                       && doc.RootElement.GetProperty("_usos")[0].GetProperty("id").GetString() == "c1",
                    "Q-01 a fila manda {_terminal_uuid, _usos:[uso]} e a nuvem aceitando tira da fila");
                var sem228 = await Emergencia.ResolverNaFilaAsync("{\"id\":\"c1\"}", TermUuid,
                    (_, _) => Task.FromResult((404, (string?)"{\"code\":\"PGRST202\"}")));
                checar(sem228.Ok is null && sem228.Erro!.Contains("SQL 228"), "Q-02 sem o SQL 228 a linha espera (nao vira dead-letter)");
                var semTerm = await Emergencia.ResolverNaFilaAsync("{\"id\":\"c1\"}", TermUuid,
                    (_, _) => Task.FromResult((200, (string?)"{\"ok\":false,\"motivo\":\"sem_terminal\"}")));
                var rede = await Emergencia.ResolverNaFilaAsync("{\"id\":\"c1\"}", TermUuid, (_, _) => Task.FromResult((0, (string?)null)));
                var lixo = await Emergencia.ResolverNaFilaAsync("lixo", TermUuid, (_, _) => Task.FromResult((200, (string?)"{}")));
                checar(semTerm.Ok is null && rede.Ok is null && lixo.Ok == false,
                    "Q-03 sem terminal ou sem rede: espera; payload ilegivel: recusa");
                checar(Drenagem.TiposComHandler.Contains(Emergencia.TipoNaFila)
                       && Drenagem.JanelaPropria.Any(j => j.Tipo == Emergencia.TipoNaFila)
                       && Drenagem.PrazoDoTransitorio(Emergencia.TipoNaFila) == ContasAbertas.SemPrazo,
                    "Q-04 a fila tem handler, janela propria (nunca na frente da venda) e nunca desiste por idade");
            }

            // ── V. as telas e o servico usam isto ──────────────────────────────
            {
                string Ler(params string[] partes)
                {
                    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                        if (File.Exists(Path.Combine(d.FullName, "Pdv.csproj")))
                            return File.ReadAllText(Path.Combine(new[] { d.FullName }.Concat(partes).ToArray()));
                    return "";
                }
                var telaAut = Ler("Telas", "TelaAutorizacao.cs");
                var pedir = Ler("Telas", "PedirCodigo.cs");
                var servicos = Ler("Servicos.cs");
                var venda = Ler("Telas", "Venda.xaml.cs");
                var sinc = Ler("Pdv.Nucleo", "Sincronizacao.cs");
                checar(telaAut.Contains("PedirCodigoEmergenciaAsync") && telaAut.Contains("emergencia: true")
                       && pedir.Contains("Código de emergência (8 dígitos, vale uma vez)"),
                    "V-01 a tela de verdade pede o codigo de emergencia de 8 digitos");
                checar(servicos.Contains("Autorizacao.EmergenciaDoCaixa ??= () => new GuardaEmergencia(")
                       && servicos.Contains("semRede: () => Nuvem().UltimaFalhaFoiRede")
                       && servicos.Contains("Drenagem.ComSessao ="),
                    "V-02 o servico liga a guarda, o 'sem rede' da sessao e o pacote a cada 15 min com sinal");
                checar(venda.Contains("valorCent: valor.Value.Centavos"), "V-03 a sangria manda o valor (o teto e conferido)");
                checar(sinc.Contains("BaixarPacoteEmergenciaAsync"), "V-04 o Atualizar baixa o pacote");
                var textos = new[] { Emergencia.AvisoDigite, Emergencia.AvisoInvalido, Emergencia.MotivoSemCodigos, Emergencia.MotivoBloqueada,
                    Emergencia.MotivoAcimaDoTeto(30000), Emergencia.MotivoInvalidoVezes(3) };
                checar(textos.All(x => !x.Contains('—') && !x.Contains('–') && !x.Contains('\n') && x.Length <= 70),
                    "V-05 textos de uma linha, curtos e sem travessao");
            }
        }
        finally
        {
            Autorizacao.EmergenciaDoCaixa = emAnterior;
            Autorizacao.Agora = agoraAnterior;
            Banco.CaminhoForcado = anterior;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }
}
