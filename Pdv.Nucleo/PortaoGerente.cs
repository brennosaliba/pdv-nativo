using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

// ════════════════════════════════════════════════════════════════════════════
//  A AUTORIZACAO DO GERENTE SEM PIN (1.0.55, 27/10/2026, decisao do dono)
//
//  Tudo que pedia o PIN do gerente (a senha de operador dele) passa a pedir:
//   · a DIGITAL do gerente, reconhecida pelo leitor e conferida pelo PAPEL do
//     funcionario no cadastro de operadores do painel (pdv_operadores.perfil
//     'gerente', que o caixa guarda como Operador.ESupervisor). Digital de
//     funcionario comum nao autoriza; onde a regra pede OUTRO gerente (sangria,
//     comanda), a digital de quem opera tambem nao;
//   · OU o CODIGO do autenticador do gerente geral ou do dono
//     (pdv_autorizacao_totp, nivel gerente; tipos sangria, caixa_sem_contagem e
//     comanda_gerente, SQL 225). Sem o 225 a nuvem so aceita o do dono.
//  Pontos: sangria, fechar sem contar a gaveta (caixa de outro dia), tirar item
//  ja pedido a cozinha e cancelar comanda com item.
//  Auditoria: quem autorizou e por qual via (Trilha), evento autorizacao_gerente.
//
//  O caixa nao conhece o papel do ERP (user_roles): o operador do caixa nao tem
//  ligacao com a conta do painel. O "papel" que ele conhece e o perfil do
//  cadastro de operadores ('gerente' ou 'caixa'). Por isso o codigo 2FA e o
//  caminho do gerente geral de verdade (manager/owner no servidor).
//
//  Testes: Pdv.Testes/TestesBiometria.cs (secao G2).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>O desfecho da autorizacao do gerente. <see cref="Gerente"/> so no autorizado.</summary>
public sealed record AutorizacaoGerente(bool Autorizado, string Via, Operador? Gerente, string Motivo,
    string? Registro = null, string? Nivel = null)
{
    public const string ViaDigital = "digital";
    public const string ViaCodigo = "codigo";

    /// <summary>A tela ja explicou por que nao seguiu: nao avisar de novo.</summary>
    public bool Avisado { get; init; }

    /// <summary>"digital do gerente BIA" ou "codigo do gerente: Marcos (nivel gerente, registro log-0001)".</summary>
    public string Trilha => !Autorizado || Gerente is null ? Motivo
        : Via == ViaDigital ? $"digital do gerente {Gerente.Nome}"
        : $"codigo do gerente: {Gerente.Nome} (nivel {Nivel ?? Autorizacao.NivelGerente}, registro {Curto(Registro)})";

    private static string Curto(string? id) => id is { Length: > 8 } ? id[..8] : id ?? "?";
}

public static class PortaoGerente
{
    public const string AvisoNaoEGerente = "Essa digital não é de gerente.";
    public const string AvisoQuemOpera = "Quem opera não autoriza. Chame outro gerente.";

    /// <summary>O titulo da leitura: "Gerente: encoste o dedo no leitor".</summary>
    public static string Titulo(string acao) => $"Gerente: encoste o dedo ({acao})";

    /// <summary>
    /// A digital do gerente; "Sem digital" na tela, leitor com problema ou 3 leituras sem
    /// autorizar = o codigo do gerente geral ou do dono (pedido.Tipo, nivel gerente).
    /// </summary>
    /// <param name="porChave">chave do leitor -> operador ativo (null = desconhecida).</param>
    /// <param name="mesmaPessoa">compara dois operadores pelo id canonico (null = nao exige outro gerente).</param>
    public static async Task<AutorizacaoGerente> AutorizarAsync(ILeitorDigital leitor, ITelaDigital tela,
        Func<string, Operador?> porChave, Func<Operador, bool>? mesmaPessoa, IAutorizacaoRemota? remota,
        ITelaAutorizacao telaTotp, PedidoAutorizacao pedido, string acao, CancellationToken ct = default)
    {
        var sonda = await leitor.SondarAsync(ct);
        if (sonda.Pronto)
        {
            string? aviso = null;
            for (var tentativa = 1; tentativa <= Biometria.MaxTentativas; tentativa++)
            {
                var leitura = await tela.LerAsync(Titulo(acao), aviso, false, leitor.IdentificarAsync);
                if (leitura.Escolha == EscolhaNaLeitura.SemDigital) break;
                if (leitura.Escolha is EscolhaNaLeitura.Desistiu or EscolhaNaLeitura.PrimeiraVez)
                    return new AutorizacaoGerente(false, ViaDigitalTexto, null, "desistiu da autorizacao") { Avisado = true };
                var c = leitura.Captura ?? Captura.Cancelada;
                if (c.Resultado == ResultadoCaptura.Cancelado)
                    return new AutorizacaoGerente(false, ViaDigitalTexto, null, "desistiu da autorizacao") { Avisado = true };
                if (c.Resultado == ResultadoCaptura.SemLeitor) { tela.Avisar("Leitor de digital", Biometria.AvisoSemLeitor); break; }
                if (c.Resultado == ResultadoCaptura.Reconhecido)
                {
                    var op = porChave(c.Chave!);
                    if (op is null) aviso = Biometria.AvisoDigitalDesconhecida;
                    else if (!op.ESupervisor) aviso = AvisoNaoEGerente;
                    else if (mesmaPessoa?.Invoke(op) == true) aviso = AvisoQuemOpera;
                    else return new AutorizacaoGerente(true, AutorizacaoGerente.ViaDigital, op, "digital do gerente");
                    continue;
                }
                aviso = c.Resultado == ResultadoCaptura.Falha ? $"O leitor falhou ({c.Motivo}). Tente de novo." : Biometria.AvisoNaoReconheci(tentativa);
            }
        }
        else tela.Avisar("Leitor de digital", Biometria.AvisoDoEstado(sonda.Estado));

        var p = pedido with { Nivel = Autorizacao.NivelGerente };
        var aut = await Autorizacao.ResolverAsync(remota, p, telaTotp, ct);
        if (!aut.Autorizado)
            return new AutorizacaoGerente(false, AutorizacaoGerente.ViaCodigo, null, aut.Motivo) { Avisado = aut.Avisado };
        var gerente = new Operador(aut.Autorizador ?? "totp:?", aut.AprovadoPor ?? "gerente", "gerente");
        return new AutorizacaoGerente(true, AutorizacaoGerente.ViaCodigo, gerente, "codigo do gerente", aut.TokenId, aut.Nivel ?? Autorizacao.NivelGerente);
    }

    private const string ViaDigitalTexto = AutorizacaoGerente.ViaDigital;

    /// <summary>
    /// Guarda o nome de quem autorizou pelo codigo (id "totp:xxxxxxxx"), para a fila e o papel
    /// dizerem o nome: ele nao e operador do caixa. E audita a autorizacao com a trilha.
    /// </summary>
    public static void Registrar(SqliteConnection cx, AutorizacaoGerente a, string tipo, string? operadorId)
    {
        if (!a.Autorizado || a.Gerente is null) return;
        if (a.Via == AutorizacaoGerente.ViaCodigo)
            cx.Execute("""
                INSERT INTO autorizador_codigo (id, nome, registro, nivel, em) VALUES (@Id, @N, @R, @Nv, @Em)
                ON CONFLICT(id) DO UPDATE SET nome = @N, registro = @R, nivel = @Nv, em = @Em
                """, new { Id = a.Gerente.Id, N = a.Gerente.Nome, R = a.Registro, Nv = a.Nivel, Em = DateTime.Now.ToString("o") });
        Caixa.Auditar(cx, null, "autorizacao_gerente", operadorId, a.Gerente.Id, $"{tipo} · {a.Trilha}");
    }

    /// <summary>O nome de quem autorizou pelo codigo (null = nao foi pelo codigo).</summary>
    public static string? NomeDoCodigo(SqliteConnection cx, string? id)
        => id is null ? null : cx.ExecuteScalar<string?>("SELECT nome FROM autorizador_codigo WHERE id = @Id", new { Id = id });
}
