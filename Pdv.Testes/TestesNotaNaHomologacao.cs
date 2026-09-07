using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// PASSO 30 DO ROTEIRO v20260819 — "Solicitação de menu genérico 1", venda de R$ 1.002,00 no
/// autorizador C6PAY — e o que a venda de teste ainda fazia de verdade no caixa.
///
/// O passo manda cobrar R$ 1.002,00 e conferir que a maquininha devolve um menu com duas opções
/// ("123456" e "ABCDEF") e o prompt "SELECIONAR:". Para chegar lá o caixa precisa de duas coisas:
/// montar o valor exato (o botão do valor de teste na comanda) e cobrar essa venda como cobra
/// qualquer outra. A segunda parte trazia junto um efeito que ninguém tinha medido.
///
/// MEDIDO NO CAIXA DE HOMOLOGAÇÃO EM 07/09/2026, na tabela `terminal` de
/// C:\ProgramData\PdvNativo\pdv.db: `ambiente` = 1 (PRODUÇÃO), `cnpj` = 62177839000238 (o CNPJ
/// real da Savassi), `serie_nfce` = 3, e `config.modo_fiscal` = nfce. Ou seja: cada venda do
/// roteiro — vinte e poucas, uma por passo, com a linha "Venda de teste" sem NCM — ia bater na
/// SEFAZ de PRODUÇÃO com a série da loja. Rejeitada, o operador teria que dispensar "Nota não
/// autorizada" em todo passo, com número de série queimado a cada tentativa; AUTORIZADA, seria
/// uma NFC-e válida, no CNPJ da loja, de um produto que não existe. Já aconteceu parecido: uma
/// venda de teste consumiu 18 números da série 2 em 06/08.
///
/// O modo de homologação (config `homologacao` = 1) sempre significou "venda de TESTE": ela não
/// entra na fila da nuvem (Pdv.Nucleo/Vendas.cs, Finalizar), fica fora do fechamento do caixa
/// (Pdv.Nucleo/Caixa.cs) e fora do contador de pendências (TestesPendencias). A NOTA era o único
/// lugar em que ela ainda valia como venda de verdade. Agora não é mais: a decisão de emitir mora
/// em <see cref="Vendas.SemNota"/>, uma regra só, e a tela de pagamento pergunta a ela.
///
/// O que se prova aqui:
///  · a regra: quatro combinações de `modo_fiscal` × `homologacao`, sem WPF;
///  · uma cópia só: a tela de pagamento usa a regra do núcleo e não repete a comparação;
///  · o passo 30 inteiro no núcleo: R$ 1.002,00 exatos gravados, marcados como teste, fora da
///    fila e com rastro na auditoria;
///  · e o controle que importa: na LOJA (homologação desligada) a nota continua saindo.
/// </summary>
public static class TestesNotaNaHomologacao
{
    /// <summary>O valor do passo 30, em centavos.</summary>
    private const long Passo30 = 100_200;

    public static void Rodar(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-nota-homolog-{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir(arquivo);
            Regra(cx, checar);
            UmaCopiaSo(checar);
            VendaDoPasso30(cx, checar);
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }

    // ── 1. a regra, sem tela ────────────────────────────────────────────────
    private static void Regra(SqliteConnection cx, Action<bool, string> checar)
    {
        void Cfg(string fiscal, string homolog)
        {
            Vendas.GravarConfig(cx, "modo_fiscal", fiscal);
            Vendas.GravarConfig(cx, "homologacao", homolog);
        }

        Cfg("nfce", "0");
        checar(!Vendas.SemNota(cx), "loja normal (modo_fiscal nfce, homologação desligada): a nota SAI");

        Cfg("nfce", "1");
        checar(Vendas.SemNota(cx),
            "caixa de homologação com modo_fiscal nfce: a venda de teste NÃO emite nota");

        Cfg("recibo", "0");
        checar(Vendas.SemNota(cx), "loja em modo recibo: sem nota, como sempre foi");

        Cfg("recibo", "1");
        checar(Vendas.SemNota(cx), "recibo + homologação: sem nota (as duas razões juntas não brigam)");

        // Config ausente é o estado de um caixa recém-instalado: sem `homologacao` gravado,
        // a nota tem que sair. Uma regra que falhasse aberta aqui deixaria a loja sem NFC-e.
        cx.Execute("DELETE FROM config WHERE chave IN ('modo_fiscal','homologacao')");
        checar(!Vendas.SemNota(cx), "caixa sem as duas chaves gravadas: a nota SAI (o padrão é a loja)");
    }

    // ── 2. a decisão mora num lugar só ──────────────────────────────────────
    // Duas cópias da mesma regra divergem no dia 1: a tela pergunta ao núcleo.
    private static void UmaCopiaSo(Action<bool, string> checar)
    {
        var fonte = Arquivo("Telas", "Pagamento.xaml.cs");
        if (fonte is null) { checar(false, "achei Telas/Pagamento.xaml.cs"); return; }
        checar(fonte.Contains("Vendas.SemNota(cx)", StringComparison.Ordinal),
            "a tela de pagamento decide a nota por Vendas.SemNota");
        checar(!fonte.Contains("modo_fiscal", StringComparison.Ordinal),
            "e não guarda mais uma cópia da comparação com modo_fiscal");
    }

    // ── 3. o passo 30 no núcleo: R$ 1.002,00 exatos, e é uma venda de TESTE ─
    private static void VendaDoPasso30(SqliteConnection cx, Action<bool, string> checar)
    {
        var op = new Operador("op-h30", "Homologa", "gerente");
        Operadores.Salvar(cx, op.Id, op.Nome, "9182", "gerente");
        var sessao = Caixa.Abrir(cx, op, Dinheiro.DeReais(0));

        // O caixa de homologação como ele está: terminal em PRODUÇÃO e modo fiscal nfce.
        cx.Execute("""
            INSERT INTO terminal (id, terminal_uuid, loja_id, loja_nome, cnpj, serie_nfce, ambiente, api_base, criado_em)
            VALUES (1, 'term-h30', 'loja-1', 'American Day Savassi', '62177839000238', 3, 1, 'http://127.0.0.1:9', @a)
            """, new { a = DateTime.Now.ToString("o") });
        Vendas.GravarConfig(cx, "modo_fiscal", "nfce");
        Vendas.GravarConfig(cx, "homologacao", "1");

        var ambiente = cx.ExecuteScalar<long>("SELECT ambiente FROM terminal LIMIT 1");
        checar(ambiente == 1 && Vendas.Config(cx, "modo_fiscal") == "nfce",
            "cenário: o caixa que grava o roteiro tem o terminal em PRODUÇÃO e emite NFC-e");
        checar(Vendas.SemNota(cx),
            "passo 30: mesmo assim a venda de R$ 1.002,00 sai sem nota, porque é venda de teste");

        var valor = new Dinheiro(Passo30);
        var venda = Vendas.Finalizar(cx, sessao, op,
            new[] { new LinhaVenda("teste-passo30", "teste-passo30", "Venda de teste",
                                   Quantidade.Um, valor, valor, "UN", null, null, null, null, 0) },
            new[] { new PagamentoVenda("credito", valor, Dinheiro.Zero, Aut: "123456", Nsu: "000030") },
            null, "American Day Savassi", null);

        checar(venda.Total.Centavos == Passo30 && valor.Formatado() == new Dinheiro(100_200).Formatado(),
            $"a venda gravada é de {valor.Formatado()} exatos, que é o valor que vai para a maquininha");
        var linha = cx.QueryFirst("SELECT total_cent, homologacao, fiscal_status FROM venda WHERE id = @V", new { V = venda.Id });
        checar((long)linha.total_cent == Passo30 && (long)linha.homologacao == 1,
            "…e ela nasce marcada como venda de teste");
        checar((string)linha.fiscal_status == "pendente",
            "o desfecho fiscal fica 'pendente' e ninguém volta para emitir: não existe fila de reemissão");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE ref_id = @V", new { V = venda.Id }) == 0,
            "a venda do roteiro não entra na fila da nuvem (regra que já existia)");
        checar(cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM auditoria WHERE evento = 'venda_homologacao' AND detalhe LIKE @D",
                new { D = "%" + valor.Formatado() + "%" }) == 1,
            "e deixa rastro na auditoria com o valor por extenso, para conferir a gravação depois");

        // Controle: a MESMA venda numa loja de verdade continua indo para a nota.
        Vendas.GravarConfig(cx, "homologacao", "0");
        checar(!Vendas.SemNota(cx),
            "controle: desligada a homologação, a loja volta a emitir NFC-e (o conserto não desliga nota de ninguém)");
    }

    // ── leitura de fonte (mesma receita das outras suítes) ──────────────────
    private static string? Arquivo(params string[] partes)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var alvo = Path.Combine(new[] { d.FullName }.Concat(partes).ToArray());
            if (File.Exists(alvo)) return File.ReadAllText(alvo);
            d = d.Parent;
        }
        return null;
    }
}
