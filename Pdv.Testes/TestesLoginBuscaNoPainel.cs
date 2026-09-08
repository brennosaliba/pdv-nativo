using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// FUNCIONÁRIO NOVO NO PAINEL TEM QUE CONSEGUIR ENTRAR NO CAIXA (08/09/2026).
///
/// O QUE ACONTECEU NA SAVASSI. O dono cadastrou David, Bianca e Laiza no painel de
/// madrugada e, no balcão, os três levaram "Senha errada" com CPF e senha certos.
/// Conferi no banco de produção: as três linhas existem, ativas, com pin_hash e
/// pin_salt do tamanho certo, loja "American Day Savassi", e o hash confere com os
/// quatro últimos dígitos do CPF de cada um. O painel estava certo.
///
/// O caixa é que não sabia deles. Operador só desce em <c>Sincronizacao.ExecutarAsync</c>,
/// que roda em dois lugares: o botão Sincronizar e o aviso do painel de catálogo novo.
/// Os DOIS moram na tela de venda, que só existe depois do login. Ou seja: um caixa
/// que ficou sem ninguém logado, ou que abriu com gente nova, não tinha como aprender
/// quem entrou na folha. Ovo e galinha, com a loja parada e fila no balcão.
///
/// A regra que este arquivo vigia: quando o login não bate, o caixa PERGUNTA AO PAINEL
/// uma vez e tenta de novo. Uma vez só, e nunca deixando de funcionar sem internet.
/// </summary>
public static class TestesLoginBuscaNoPainel
{
    public static async Task RodarAsync(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"login-painel-{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir(arquivo);

            // Um CPF válido de teste, e a senha que a Savassi usa: os 4 últimos dígitos.
            const string Cpf = "52998224725";
            const string Senha = "4725";

            // ── 1. SEM PAINEL, NADA MUDA ────────────────────────────────────
            // Caixa offline: a busca nem existe, e o login continua sendo local.
            var (semPainel, buscou0) = await Operadores.EntrarComCpfAsync(cx, Cpf, Senha, null);
            checar(semPainel is null && !buscou0,
                "sem painel para perguntar, o login segue local e recusa como sempre");

            // ── 2. O FUNCIONÁRIO NOVO ENTRA ─────────────────────────────────
            // O caixa não conhece este CPF. A primeira tentativa falha localmente, o
            // caixa pergunta ao painel, o painel entrega, e a segunda tentativa passa.
            var vezes = 0;
            Task<int> Baixar()
            {
                vezes++;
                var (h, s) = Operadores.GerarHash(Senha);
                cx.Execute("""
                    INSERT INTO operador (id, nome, pin_hash, pin_salt, perfil, cpf, ativo, da_nuvem,
                                          pin_nuvem_hash, pin_nuvem_salt, atualizado)
                    VALUES ('op-novo','DAVID MATEUS',@H,@S,'operador',@C,1,1,@H,@S,@Em)
                    ON CONFLICT(id) DO NOTHING
                    """, new { H = h, S = s, C = Cpf, Em = DateTime.Now.ToString("o") });
                return Task.FromResult(1);
            }

            var (novo, buscou) = await Operadores.EntrarComCpfAsync(cx, Cpf, Senha, Baixar);
            checar(novo is not null && novo.Nome == "DAVID MATEUS" && buscou && vezes == 1,
                $"funcionário novo entra depois de UMA busca no painel (viu {novo?.Nome ?? "<ninguém>"}, {vezes} busca(s))");

            // ── 3. QUEM JÁ ESTÁ AQUI NÃO CUSTA REDE ─────────────────────────
            // Login que bate localmente não pode chamar o painel: seria uma ida à rede
            // por login, no balcão, com fila.
            vezes = 0;
            var (deNovo, buscou2) = await Operadores.EntrarComCpfAsync(cx, Cpf, Senha, Baixar);
            checar(deNovo is not null && !buscou2 && vezes == 0,
                "login que já bate localmente NÃO vai à rede");

            // ── 4. UMA BUSCA POR TENTATIVA, NUNCA UM LAÇO ───────────────────
            vezes = 0;
            var (errada, buscou3) = await Operadores.EntrarComCpfAsync(cx, Cpf, "0000", Baixar);
            checar(errada is null && buscou3 && vezes == 1,
                $"senha errada: pergunta ao painel UMA vez e desiste (viu {vezes} busca(s))");

            // ── 5. PAINEL QUE FALHA NÃO DERRUBA O LOGIN ─────────────────────
            // Rede caindo no meio não pode virar exceção na tela de login: o caixa
            // ficaria sem entrada nenhuma, que é pior do que o problema original.
            var (caiu, _) = await Operadores.EntrarComCpfAsync(cx, "11144477735", Senha,
                () => throw new HttpRequestException("sem rede"));
            checar(caiu is null, "painel fora do ar recusa o login, mas não lança na cara do operador");

            // ── 5b. REDE PENDURADA NÃO SEGURA O CAIXA ───────────────────────
            // Rede que não responde (portal de wi-fi, DNS pendurado) é pior que rede
            // caída: não dá erro, fica esperando. O HttpClient da nuvem espera 25 s.
            // Sem teto, o operador ficaria 25 s parado a CADA tentativa, com fila.
            var relogio = System.Diagnostics.Stopwatch.StartNew();
            var (pendurou, _) = await Operadores.EntrarComCpfAsync(cx, "11144477735", Senha,
                async () => { await Task.Delay(TimeSpan.FromSeconds(30)); return 1; },
                teto: TimeSpan.FromMilliseconds(300));
            relogio.Stop();
            checar(pendurou is null && relogio.ElapsedMilliseconds < 3_000,
                $"rede pendurada: o login desiste no teto e devolve a tela (levou {relogio.ElapsedMilliseconds} ms)");

            // ── 6. PAINEL QUE NÃO TROUXE NINGUÉM NÃO TENTA DE NOVO ──────────
            vezes = 0;
            var (vazio, buscou4) = await Operadores.EntrarComCpfAsync(cx, "11144477735", Senha,
                () => { vezes++; return Task.FromResult(0); });
            checar(vazio is null && buscou4 && vezes == 1,
                "painel que respondeu sem novidade também custa uma busca só");
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }
}
