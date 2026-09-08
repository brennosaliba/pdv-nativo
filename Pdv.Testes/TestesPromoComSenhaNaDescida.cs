using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// PROMOÇÃO COM SENHA NÃO PODE DESCER PARA CAIXA QUE NÃO SABE COBRAR A SENHA.
///
/// O INCIDENTE (08/09/2026, Savassi). Foi criada a promoção DESCONTO FUNCIONARIO: 30%
/// em todos os produtos, com `config.autorizacao = gerente`. A intenção era valer para
/// UMA comanda, depois do código do gerente.
///
/// O portão de senha entrou no caixa na 0.5.9. A Savassi roda a 0.5.6, e para essa
/// versão `config.autorizacao` é um campo que ela nunca leu: sobrou uma promoção comum
/// de 30% em tudo, aplicada sozinha, sem código nenhum. O dono viu o desconto caindo na
/// comanda inteira e desativou a promoção no painel.
///
/// A LIÇÃO, maior que o caso: regra de segurança que mora SÓ no caixa vale só nas
/// versões que a conhecem, e toda loja que ficou para trás vira o buraco. O servidor é
/// um só; o parque de caixas não é. Quem tem que fechar é o servidor.
///
/// O corte de verdade está na RPC (migration 20260908180000): promoção com senha só sai
/// para quem DECLARA que sabe cobrá-la, e versão velha chama com um argumento só. Este
/// arquivo vigia a outra metade: que esta versão declare, e que a queda para o servidor
/// antigo continue sendo segura.
/// </summary>
public static class TestesPromoComSenhaNaDescida
{
    public static async Task RodarAsync(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"promo-descida-{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        var porta = 4733;
        var corpos = new List<string>();
        var recusarDoisArgumentos = true;

        var ouvinte = new HttpListener();
        ouvinte.Prefixes.Add($"http://127.0.0.1:{porta}/");
        ouvinte.Start();
        var servindo = Task.Run(async () =>
        {
            while (ouvinte.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await ouvinte.GetContextAsync(); } catch { return; }
                string corpo;
                using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    corpo = await sr.ReadToEndAsync();

                var caminho = ctx.Request.Url!.AbsolutePath;
                string resposta;
                if (caminho.EndsWith("/token", StringComparison.Ordinal))
                    resposta = """{"access_token":"t","refresh_token":"r","expires_in":3600}""";
                else if (caminho.EndsWith("pdv_promocoes_ativas", StringComparison.Ordinal))
                {
                    corpos.Add(corpo);
                    if (recusarDoisArgumentos && corpo.Contains("_com_senha", StringComparison.Ordinal))
                    {
                        // É assim que um servidor sem a migration responde: ele não tem
                        // essa assinatura de função.
                        ctx.Response.StatusCode = 404;
                        resposta = """{"code":"PGRST202","message":"function not found"}""";
                    }
                    else resposta = "[]";
                }
                else resposta = "[]";

                var b = Encoding.UTF8.GetBytes(resposta);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(b);
                ctx.Response.Close();
            }
        });

        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir(arquivo);
            var nuvem = new Nuvem($"http://127.0.0.1:{porta}");
            checar(await nuvem.EntrarAsync("x@y.com", "s"), "nuvem de mentira autentica");

            // ── 1. ESTA VERSÃO DECLARA QUE SABE COBRAR A SENHA ──────────────
            recusarDoisArgumentos = false;
            corpos.Clear();
            await nuvem.BaixarPromocoesAsync(cx, "American Day Savassi");
            checar(corpos.Count == 1 && corpos[0].Contains("\"_com_senha\":true", StringComparison.Ordinal),
                $"o caixa DECLARA que sabe cobrar a senha da promoção (mandou: {string.Join(" | ", corpos)})");

            // ── 2. SERVIDOR ANTIGO: CAI PARA A CHAMADA DE UM ARGUMENTO ──────
            // Não é desistir: na chamada antiga a promoção com senha também não desce,
            // que é exatamente o que se quer. O que não pode é a loja ficar SEM as
            // promoções normais só porque o servidor ainda não subiu a migration.
            recusarDoisArgumentos = true;
            corpos.Clear();
            var n = await nuvem.BaixarPromocoesAsync(cx, "American Day Savassi");
            checar(corpos.Count == 2, $"servidor antigo: tenta com a declaração e cai para a chamada antiga (viu {corpos.Count} tentativas)");
            checar(corpos.Count == 2 && !corpos[1].Contains("_com_senha", StringComparison.Ordinal),
                "…e a segunda tentativa vai sem a declaração, que é a assinatura que ele conhece");
            checar(n >= 0, $"…e a descida CONCLUI, em vez de deixar a loja sem promoção nenhuma (viu {n})");
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            try { ouvinte.Stop(); } catch { }
            try { await servindo.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }
}
