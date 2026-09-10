using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// FECHAMENTO: MAQUININHA AVULSA E JUSTIFICATIVA DE VERDADE (10/09/2026, pedidos do dono).
///
/// 1. "No fechamento do PDV perguntar se teve venda POS; se sim, abrir campo pra preencher
///    pix, crédito, débito, voucher." O que o operador digita para a maquininha avulsa vale
///    como contagem MESMO quando o roteiro (FormasContadas) não pediu aquela forma, e a
///    venda que só a maquininha viu aparece como SOBRA com apurado zero. Zero digitado
///    não inventa diferença nenhuma.
/// 2. "Aplicar no PDV mínimo de 15 caracteres na justificativa, evitar justificativa a, b
///    ou c." O painel do dono estava cheio de "a", "s", "gh". A régua mora no Núcleo, para
///    valer também para quem fechar por fora da tela.
/// </summary>
public static class TestesFechamentoPos
{
    public static void Rodar(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-teste-pos-{Guid.NewGuid():N}.db");
        Banco.Migrar(arquivo);
        try
        {
            using var cx = Banco.Abrir(arquivo);
            var op = new Operador("pos-op", "Fechador", "operador");
            Operadores.Salvar(cx, op.Id, op.Nome, "1111", "operador");
            Vendas.GravarConfig(cx, "tef_habilitado", "1");

            Regua(checar);
            VendaQueSoAMaquininhaViu(cx, op, checar);
            PosZeradoNaoInventaNada(cx, op, checar);
            SemRespostaNadaMuda(cx, op, checar);
            JustificativaCurtaNaoFecha(cx, op, checar);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
        Telas(checar);
    }

    // ── a régua da justificativa ──────────────────────────────────────────────
    private static void Regua(Action<bool, string> checar)
    {
        checar(Caixa.JustificativaMinima == 15, "a régua é 15 letras, a mesma que a SEFAZ usa no cancelamento");
        checar(!Caixa.JustificativaAceitavel("a") && !Caixa.JustificativaAceitavel("gh") && !Caixa.JustificativaAceitavel("   ") && !Caixa.JustificativaAceitavel(null),
            "'a', 'gh', espaço e nulo não são justificativa");
        checar(!Caixa.JustificativaAceitavel("aaaaaaaaaaaaaaaaaaaaaaaa"), "vinte letras iguais numa palavra só também não");
        checar(!Caixa.JustificativaAceitavel("troco errado"), "12 letras é pouco");
        checar(Caixa.JustificativaAceitavel("troco errado no pix"), "'troco errado no pix' passa");
        checar(Caixa.JustificativaAceitavel("  cliente pagou a mais e saiu correndo  "), "espaço nas pontas não conta a favor nem contra");
        checar(Caixa.MsgJustificativaCurta.Contains("Justifique") && Caixa.MsgJustificativaCurta.Contains("15"),
            "a frase da régua diz o número e termina com a palavra que reabre o campo");
    }

    // ── TEF ligado, nada de cartão no PDV. O operador diz que a maquininha avulsa
    //    vendeu R$ 30,00 no PIX: a forma nem estava no roteiro e mesmo assim vira
    //    linha contada, com sobra. ─────────────────────────────────────────────
    private static void VendaQueSoAMaquininhaViu(SqliteConnection cx, Operador op, Action<bool, string> checar)
    {
        var s = Caixa.Abrir(cx, op, Dinheiro.DeReais(100));
        Pagar(cx, s, op, "dinheiro", 5000, null);
        var plano = Caixa.PlanoDeConferencia(cx, s);
        checar(!plano.Any(p => p.Conta && p.Forma != "dinheiro"),
            "com TEF e sem cartão no PDV o roteiro só pede o dinheiro (é aí que a pergunta do POS entra)");

        var contagem = new Dictionary<string, Dinheiro>
        {
            ["dinheiro"] = Dinheiro.DeReais(150),
            ["pix"] = Dinheiro.DeReais(30),
            ["credito"] = Dinheiro.Zero, ["debito"] = Dinheiro.Zero, ["voucher"] = Dinheiro.Zero,
        };
        string? erro = null;
        try { Caixa.Fechar(cx, s, contagem, op, new Dinheiro(200)); }
        catch (InvalidOperationException ex) { erro = ex.Message; }
        checar(erro is not null && erro.Contains("Justifique") && erro.Contains("pix") && erro.Contains("sobra"),
            "a venda que só o POS viu vira SOBRA e pede justificativa: " + erro);

        var linhas = Caixa.Fechar(cx, s, contagem, op, new Dinheiro(200), "cliente pagou no pix da maquininha e nao passou no caixa");
        var pix = linhas.Single(l => l.Forma == "pix");
        checar(pix.Contada && pix.Apurado.Centavos == 0 && pix.Declarado.Centavos == 3000 && pix.Situacao == "sobra",
            "PIX: contada, apurado zero, declarado R$ 30,00, SOBRA (a venda fora do PDV aparece)");
        checar(linhas.Where(l => l.Forma is "credito" or "debito" or "voucher")
                     .All(l => l.Contada && l.Diferenca.Centavos == 0 && l.Situacao == "confere"),
            "as formas com zero digitado fecham conferidas, sem inventar diferença");
        var gravadas = cx.Query<(string forma, long decl, long apur, long dif)>(
            "SELECT forma, declarado_cent, apurado_cent, diferenca_cent FROM caixa_fechamento WHERE sessao_id=@S", new { S = s.Id }).ToList();
        checar(gravadas.Any(g => g.forma == "pix" && g.decl == 3000 && g.apur == 0 && g.dif == 3000),
            "o banco guarda a linha do PIX com a sobra de R$ 30,00");
    }

    // ── TEF ligado com crédito integrado de R$ 100. "Teve POS" e crédito zero: o
    //    declarado é só a parte do TEF, e a linha confere. ────────────────────
    private static void PosZeradoNaoInventaNada(SqliteConnection cx, Operador op, Action<bool, string> checar)
    {
        var s = Caixa.Abrir(cx, op, Dinheiro.DeReais(100));
        Pagar(cx, s, op, "credito", 10000, "AUT-TEF");
        var contagem = new Dictionary<string, Dinheiro>
        {
            ["dinheiro"] = Dinheiro.DeReais(100),
            ["pix"] = Dinheiro.Zero, ["credito"] = Dinheiro.Zero, ["debito"] = Dinheiro.Zero, ["voucher"] = Dinheiro.Zero,
        };
        var linhas = Caixa.Fechar(cx, s, contagem, op, new Dinheiro(200));
        var cred = linhas.Single(l => l.Forma == "credito");
        checar(cred.Contada && cred.PeloTef.Centavos == 10000 && cred.Declarado.Centavos == 10000
               && cred.Diferenca.Centavos == 0 && cred.Situacao == "confere",
            "crédito: zero na avulsa + R$ 100 do TEF = declarado R$ 100, confere (a parte do TEF entra sozinha)");
        checar(linhas.All(l => l.Diferenca.Centavos == 0), "nenhuma forma inventou diferença");
    }

    // ── O operador disse "não teve POS": nada entra em contagem e tudo fecha como
    //    antes (linha automática, declarado = apurado). ───────────────────────
    private static void SemRespostaNadaMuda(SqliteConnection cx, Operador op, Action<bool, string> checar)
    {
        var s = Caixa.Abrir(cx, op, Dinheiro.DeReais(100));
        Pagar(cx, s, op, "credito", 4000, "AUT-TEF");
        var linhas = Caixa.Fechar(cx, s, new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.DeReais(100) }, op, new Dinheiro(200));
        var cred = linhas.Single(l => l.Forma == "credito");
        checar(!cred.Contada && cred.Declarado.Centavos == 4000 && cred.Situacao == "confere",
            "sem resposta do POS o crédito fecha sozinho pelo TEF, como sempre");
        checar(!linhas.Any(l => l.Forma is "pix" or "debito" or "voucher"), "e as formas que ninguém citou nem aparecem");
    }

    // ── sobra de R$ 50 no dinheiro: "a" não fecha, vinte letras iguais não fecham,
    //    uma frase fecha. Enquanto isso o turno continua aberto e nada foi gravado. ─
    private static void JustificativaCurtaNaoFecha(SqliteConnection cx, Operador op, Action<bool, string> checar)
    {
        var s = Caixa.Abrir(cx, op, Dinheiro.DeReais(100));
        var contagem = new Dictionary<string, Dinheiro> { ["dinheiro"] = Dinheiro.DeReais(150) };
        foreach (var ruim in new[] { "a", "gh", "aaaaaaaaaaaaaaaaaaaaaa", "troco errado" })
        {
            string? erro = null;
            try { Caixa.Fechar(cx, s, contagem, op, new Dinheiro(200), ruim); }
            catch (InvalidOperationException ex) { erro = ex.Message; }
            checar(erro == Caixa.MsgJustificativaCurta, $"'{ruim}' não fecha o caixa: {erro}");
        }
        var n = cx.ExecuteScalar<int>("SELECT count(*) FROM caixa_fechamento WHERE sessao_id=@S", new { S = s.Id });
        checar(n == 0 && Caixa.SessaoAberta(cx)?.Id == s.Id, "as tentativas recusadas não gravaram nada e o turno segue aberto");

        var linhas = Caixa.Fechar(cx, s, contagem, op, new Dinheiro(200), "sobrou troco de cliente que nao esperou");
        checar(linhas.Single(l => l.Forma == "dinheiro").Situacao == "sobra" && Caixa.SessaoAberta(cx) is null,
            "com uma frase de verdade o caixa fecha");
    }

    // ── as telas usam a régua e fazem a pergunta do POS ──────────────────────
    private static void Telas(Action<bool, string> checar)
    {
        var venda = Fonte(Path.Combine("Telas", "Venda.xaml.cs")) ?? "";
        var abertura = Fonte(Path.Combine("Telas", "AberturaCaixa.xaml.cs")) ?? "";
        var pedir = Fonte(Path.Combine("Telas", "PedirValor.cs")) ?? "";

        checar(venda.Contains("PedirTexto.Justificativa(dono, \"Diferença no caixa\", ex.Message)")
               && abertura.Contains("PedirTexto.Justificativa(dono, \"Diferença no caixa\", ex.Message)"),
            "as duas telas de fechamento pedem a justificativa pela régua, não pelo campo cru");
        checar(pedir.Contains("Caixa.JustificativaAceitavel(j)") && pedir.Contains("Caixa.MsgJustificativaCurta"),
            "o campo repete a pergunta com a régua até a justificativa servir");
        checar(venda.Contains("Teve venda na maquininha avulsa (POS) hoje?")
               && venda.Contains("PerguntarMaquininhaAvulsa(dono, plano, contagem, \"Fechamento de caixa\""),
            "a tela de venda faz a pergunta do POS depois da contagem do roteiro");
        checar(abertura.Contains("Venda.PerguntarMaquininhaAvulsa(dono, plano, contagem")
               && abertura.Contains("naquele dia?"),
            "o fechamento do caixa esquecido faz a mesma pergunta, no passado");
        checar(venda.Contains("if (plano.Any(p => p.Conta && p.Forma != \"dinheiro\")) return true;"),
            "a pergunta do POS só existe quando o roteiro não pediu cartão (senão seria perguntar duas vezes)");
        checar(venda.Contains("FormasDaMaquininha = { \"pix\", \"credito\", \"debito\", \"voucher\" }"),
            "os quatro campos são pix, crédito, débito e voucher, nessa ordem");
    }

    private static string? Fonte(string relativo)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidato = Path.Combine(dir, relativo);
            if (File.Exists(Path.Combine(dir, "Pdv.csproj")) && File.Exists(candidato)) return File.ReadAllText(candidato);
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static void Pagar(SqliteConnection cx, Sessao s, Operador op, string forma, long cent,
        string? aut, string? nsu = null)
    {
        var id = Guid.NewGuid().ToString();
        cx.Execute("""
            INSERT INTO venda (id, client_key, sessao_id, business_date, numero_local, operador_id,
                               subtotal_cent, total_cent, status, criada_em, finalizada_em)
            VALUES (@Id,@K,@S,@Bd,@N,@Op,@T,@T,'finalizada',@Em,@Em)
            """,
            new { Id = id, K = id, S = s.Id, Bd = s.BusinessDate,
                  N = cx.ExecuteScalar<int>("SELECT COALESCE(MAX(numero_local),0)+1 FROM venda WHERE business_date=@B",
                          new { B = s.BusinessDate }),
                  Op = op.Id, T = cent, Em = DateTime.Now.ToString("o") });
        cx.Execute("INSERT INTO venda_pagamento (id,venda_id,forma,valor_cent,troco_cent,tef_aut,tef_nsu) VALUES (@i,@v,@f,@c,0,@a,@n)",
            new { i = Guid.NewGuid().ToString(), v = id, f = forma, c = cent, a = aut, n = nsu });
    }
}
