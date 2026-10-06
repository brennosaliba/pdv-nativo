using System.IO;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A COMANDA EM ETIQUETA 10x15 E O PRONTO PELO BIPE (05/10/2026, pedido do dono).
///
/// Três coisas que quebram longe da vista:
///   1. o conteúdo da etiqueta (quadradinho no sabor e NÃO no combo, nome longo cortado,
///      QR com o prefixo certo): erro aqui é sacola saindo errada sem ninguém notar;
///   2. o leitor USB (um teclado): pegar digitação de gente, ou não pegar a rajada do
///      leitor, ou roubar tecla de campo de texto;
///   3. o bipe chamando o Liberar: bipe duplo não pode chamar duas vezes, e o caminho tem
///      de ser o MESMO do botão (status + outbox kds_pronto).
/// </summary>
public static class TestesEtiquetaKds
{
    /// <summary>
    /// O pedido REAL iFood #6066 (Castelo, 06/10/2026), exatamente como está em
    /// ifood_orders.itens e como a RPC pdv_kds_pedidos entrega ao caixa.
    /// </summary>
    public const string Itens6066 =
        "[{\"qtd\":1,\"descricao\":\"Combo Box 4un\",\"complements\":[{\"qtd\":1,\"nome\":\"Donut Brigadeiro\"}," +
        "{\"qtd\":1,\"nome\":\"Donut Ovomaltine\"},{\"qtd\":1,\"nome\":\"Donut Morango c/ Ninho\"}," +
        "{\"qtd\":1,\"nome\":\"Donut Ninho c/ Nutella\"},{\"qtd\":1,\"nome\":\"Caixinha Extra\"}],\"valor_unitario\":74.9}]";

    private static Ticket Exemplo(string numero = "8149", string? cliente = "Ana Beatriz Souza",
                                  string status = Kds.Recebido, string refId = "0b1f6c2e-aaaa-4bbb-8ccc-1234567890ab")
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new TicketItem("Combo Box 4un", 1000, "sem granulado",
                new[] { "Clássicos: 2x Donut Ninho", "1x Donut Homer", "1x Donut Red Velvet" }),
            new TicketItem("Cookie Duplo", 2000, null),
        });
        return new Ticket("tid-1", "ifood", refId, numero, cliente, json, status,
                          new DateTime(2026, 10, 5, 17, 37, 0), null, null);
    }

    public static void Rodar(Action<bool, string> checar)
    {
        // ── 0. O PEDIDO REAL #6066 (06/10, "ainda está mostrando o nome do combo") ──
        // O JSON EXATO de ifood_orders.itens, pelo MESMO caminho da sincronização:
        // ItensDeJson -> Serialize (é o que fica em kds_ticket.itens_json) -> Ticket.Itens.
        {
            var itens = Kds.ItensDeJson(Itens6066);
            checar(itens.Count == 1 && itens[0].Escolhas is { Count: 5 },
                "#6066: os 5 complements chegam como escolhas do combo (nada se perde no parser)");
            var t = new Ticket("t-6066", "ifood", "8dbd41e5-551f-4842-ba2c-cf1e0d93d0d1", "6066", "coxa killer",
                System.Text.Json.JsonSerializer.Serialize(itens), Kds.Recebido,
                new DateTime(2026, 10, 6, 15, 56, 0), null, null);
            var e = EtiquetaKds.Montar(t, new DateTime(2026, 10, 6));
            var lidas = string.Join(" | ", e.Linhas.Select(l => l.Lida));
            checar(e.Linhas.All(l => !l.Texto.Contains("Combo Box")), "#6066: o Combo Box 4un NÃO sai na etiqueta: " + lidas);
            var donuts = new[] { "Donut Brigadeiro", "Donut Ovomaltine", "Donut Morango c/ Ninho", "Donut Ninho c/ Nutella" };
            checar(donuts.All(d => e.Linhas.Any(l => l.Texto == d && l.Tipo == TipoLinhaEtiqueta.Subitem && l.Caixa && l.Qtd == "1×")),
                "#6066: os 4 donuts saem, cada um com o próprio quadradinho e 1×: " + lidas);
            var caixinha = e.Linhas.SingleOrDefault(l => l.Texto == "Caixinha Extra");
            checar(caixinha is { Tipo: TipoLinhaEtiqueta.Embalagem, Caixa: false, Nivel: 1, Qtd: "1×" },
                "#6066: a Caixinha Extra sai como EMBALAGEM, sem quadradinho e recuada");
            checar(e.Linhas.Count == 5 && e.Linhas.Count(l => l.Caixa) == 4 && e.Linhas[^1].Texto == "Caixinha Extra",
                "#6066: 5 linhas, 4 quadradinhos, a embalagem por último: " + lidas);

            // a bobina e o card usam a MESMA lista: o combo também some da comanda de texto
            var bob = Kds.ComandaLinhas(t, 40, new DateTime(2026, 10, 6)).Select(LinhaEscala.Limpa).ToList();
            var bobTxt = string.Join(" | ", bob);
            checar(!bobTxt.Contains("Combo Box"), "#6066: a comanda de bobina não imprime o Combo Box 4un");
            checar(donuts.All(d => bob.Any(l => l.Contains("[ ] 1x " + d))),
                "#6066: a bobina tem os 4 donuts com quadradinho: " + string.Join(" / ", bob.Where(l => l.Contains("Donut") || l.Contains("Caixinha")).Select(l => l.Trim())));
            checar(bob.Any(l => l.Contains("1x Caixinha Extra") && !l.Contains("[ ]")),
                "#6066: a Caixinha Extra sai na bobina sem quadradinho");

            // a configuração: sem palavra de embalagem que case, a caixinha vira item comum
            var semCaixinha = EtiquetaKds.Montar(t, new DateTime(2026, 10, 6), EtiquetaKds.PalavrasEmbalagem("Sacola"));
            checar(semCaixinha.Linhas.Single(l => l.Texto == "Caixinha Extra") is { Tipo: TipoLinhaEtiqueta.Subitem, Caixa: true },
                "embalagem é configurável: com só \"Sacola\" a Caixinha Extra volta a ter quadradinho");
        }
        {
            checar(EtiquetaKds.PalavrasEmbalagem(null).SequenceEqual(new[] { "Caixinha", "Embalagem", "Sacola" })
                   && EtiquetaKds.PalavrasEmbalagem("  ; , ").SequenceEqual(EtiquetaKds.EmbalagemPadrao),
                "config de embalagem ausente ou vazia = Caixinha, Embalagem, Sacola");
            checar(EtiquetaKds.PalavrasEmbalagem(" Sacola ; Fita,Laço ").SequenceEqual(new[] { "Sacola", "Fita", "Laço" }),
                "config de embalagem aceita vírgula e ponto e vírgula e apara espaços");
            checar(EtiquetaKds.EEmbalagem("CAIXINHA extra") && EtiquetaKds.EEmbalagem("Caixinhas de presente")
                   && EtiquetaKds.EEmbalagem("Sacola kraft") && EtiquetaKds.EEmbalagem("Laco de fita", new[] { "Laço" }),
                "embalagem casa sem caixa, sem acento e pelo começo da palavra");
            checar(!EtiquetaKds.EEmbalagem("Donut Ninho") && !EtiquetaKds.EEmbalagem("Recaixinha") && !EtiquetaKds.EEmbalagem(""),
                "sabor não é embalagem, e a palavra não casa no meio de outra");

            // item cujo ÚNICO subitem é embalagem: o pai FICA (sumir com ele seria sumir com o donut)
            var json = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new TicketItem("Donut Homer", 2000, null, new[] { "1x Sacola" }),
                new TicketItem("Sacola Kraft", 1000, null),
            });
            var t = new Ticket("t-emb", "ifood", "ref-emb", "7001", null, json, Kds.Recebido,
                               new DateTime(2026, 10, 6, 15, 0, 0), null, null);
            var e = EtiquetaKds.Montar(t, new DateTime(2026, 10, 6));
            var lidas = string.Join(" | ", e.Linhas.Select(l => l.Lida));
            checar(e.Linhas.Any(l => l is { Tipo: TipoLinhaEtiqueta.Item, Texto: "Donut Homer", Qtd: "2×", Caixa: true }),
                "item com só embalagem de subitem continua saindo, com quadradinho: " + lidas);
            checar(e.Linhas.Count(l => l.Tipo == TipoLinhaEtiqueta.Embalagem && !l.Caixa) == 2,
                "a sacola do donut (2× pelo pai) e a sacola avulsa saem sem quadradinho: " + lidas);
        }

        // ── 1. LAYOUT PURO ──────────────────────────────────────────────────
        {
            var e = EtiquetaKds.Montar(Exemplo(), new DateTime(2026, 10, 5));
            // O PAI com subitens não sai (06/10, dono): só os subitens, cada um com caixa.
            checar(e.Linhas.All(l => !l.Texto.Contains("Combo Box")) && e.Linhas.All(l => l.Tipo != TipoLinhaEtiqueta.Observacao || l.Texto != "Combo Box"),
                "o nome do combo (produto pai) NÃO sai na etiqueta: " + string.Join(" | ", e.Linhas.Select(l => l.Lida)));
            var subs = e.Linhas.Where(l => l.Tipo == TipoLinhaEtiqueta.Subitem).ToList();
            checar(subs.Count == 3 && subs.All(s => s.Caixa && s.Nivel == 0),
                $"cada subitem do combo tem o PRÓPRIO quadradinho, como linha de conferência (achei {subs.Count})");
            checar(subs.Any(s => s.Qtd == "2×" && s.Texto.Contains("Donut Ninho")),
                "subitem com grupo e quantidade sai como 2× Clássicos: Donut Ninho");
            var simples = e.Linhas.FirstOrDefault(l => l.Texto == "Cookie Duplo");
            checar(simples is { Tipo: TipoLinhaEtiqueta.Item, Caixa: true, Qtd: "2×" },
                "item sem subitem continua com quadradinho no próprio item");
            var obs = e.Linhas.FirstOrDefault(l => l.Tipo == TipoLinhaEtiqueta.Observacao);
            checar(obs is { Caixa: false, Nivel: 1, Texto: "sem granulado" },
                "observação do item pai sai recuada e sem quadradinho");
            var lista = e.Linhas.ToList();
            var iObs = lista.IndexOf(obs!);
            var iUltSub = lista.IndexOf(subs[^1]);
            var iCookie = lista.IndexOf(simples!);
            checar(iUltSub == iObs - 1 && iObs < iCookie,
                "a observação do combo fica logo abaixo dos subitens dele, antes do próximo item");
            checar(e.Linhas.Count(l => l.Caixa) == 4, "4 caixas: 3 sabores + 1 item simples");
            checar(e.Numero == "8149" && e.Origem == "iFOOD", "número e origem iFOOD no topo");
            checar(e.Chegou == "Chegou 17:37", "hora de chegada vai na etiqueta");

            // 2 combos: cada subitem vale 2 vezes (2 combos com 2 Homer = 4× Homer)
            var dois = Exemplo() with
            {
                ItensJson = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new TicketItem("Combo Box 4un", 2000, null, new[] { "2x Donut Homer", "Premium: 1x Donut Pistache", "Sem cobertura" }),
                }),
            };
            var ed = EtiquetaKds.Montar(dois).Linhas;
            checar(ed.Any(l => l.Qtd == "4×" && l.Texto == "Donut Homer"), "2 combos com 2 Homer = 4× Donut Homer: "
                + string.Join(" | ", ed.Select(l => l.Lida)));
            checar(ed.Any(l => l.Qtd == "2×" && l.Texto == "Premium: Donut Pistache"), "2 combos com 1 Pistache = 2×");
            checar(ed.Any(l => l.Qtd == "2×" && l.Texto == "Sem cobertura"),
                "escolha sem número, em 2 combos, ganha a quantidade dos combos (2×)");
            checar(ed.Count == 3 && ed.All(l => l.Caixa), "2 combos: só as 3 linhas dos subitens, todas com caixa");
            var um = EtiquetaKds.Montar(Exemplo() with
            {
                ItensJson = System.Text.Json.JsonSerializer.Serialize(new[] { new TicketItem("Combo", 1000, null, new[] { "Sem cobertura" }) }),
            }).Linhas;
            checar(um.Count == 1 && um[0].Qtd == "", "1 combo: escolha sem número não ganha 1× inventado");
            // balcão já grava as escolhas multiplicadas (Kds.DoBalcao): não multiplica de novo
            var balcao = dois with { Origem = "balcao" };
            checar(EtiquetaKds.Montar(balcao).Linhas.Any(l => l.Qtd == "2×" && l.Texto == "Donut Homer"),
                "no balcão a escolha já vem multiplicada e não é multiplicada de novo");
            // escolhas vazias: o pai volta, para o item não sumir da etiqueta
            var vazio = Exemplo() with
            {
                ItensJson = System.Text.Json.JsonSerializer.Serialize(new[] { new TicketItem("Combo X", 1000, null, new[] { " " }) }),
            };
            checar(EtiquetaKds.Montar(vazio).Linhas is [{ Tipo: TipoLinhaEtiqueta.Item, Texto: "Combo X" }],
                "combo com escolhas em branco não some: sai o item");

            var cd = EtiquetaKds.Montar(Exemplo(numero: "CD-1234"));
            checar(cd.Origem == "CARDÁPIO WEB", "número CD- é CARDÁPIO WEB, como na comanda de bobina");

            // QR: prefixo fixo + order_id
            checar(e.Qr == "ADKDS:0b1f6c2e-aaaa-4bbb-8ccc-1234567890ab", $"QR leva ADKDS: + order_id ({e.Qr})");
            checar(LeitorKds.Id(e.Qr) == "0b1f6c2e-aaaa-4bbb-8ccc-1234567890ab",
                "o que o QR carrega volta inteiro pelo parser do leitor (ida e volta)");
            checar(EtiquetaKds.QrLadoMm >= 35, "QR com pelo menos 35 mm de lado");

            // nome do cliente: o texto
            checar(e.Cliente == "Ana Beatriz Souza", "nome curto vai inteiro");
            checar(EtiquetaKds.NomeParaEtiqueta("  Ana   Souza ") == "Ana Souza", "espaços do nome normalizados");
            var teto = EtiquetaKds.NomeParaEtiqueta("Maria Aparecida dos Santos Albuquerque Figueiredo de Andrade");
            checar(teto is { Length: <= EtiquetaKds.ClienteMaxCaracteres } && teto.EndsWith("…") && !teto.Contains("Figueir…"),
                "nome enorme cortado no teto entre palavras, com reticências: " + teto);
            checar(EtiquetaKds.NomeParaEtiqueta(null) is null && EtiquetaKds.NomeParaEtiqueta("   ") is null,
                "sem nome: nada de cliente");

            // nome do cliente: o MAIOR tamanho que cabe (medida falsa: 0,5 em por letra)
            static double Larg(string s) => s.Length * 0.5;
            const double W = 200, H = 120;
            var curto = EtiquetaKds.AjustarNome("Ana", W, H, Larg, 1.2);
            var medio = EtiquetaKds.AjustarNome("Ana Beatriz Souza", W, H, Larg, 1.2);
            var grande = EtiquetaKds.AjustarNome("Maria Aparecida dos Santos Albuquerque", W, H, Larg, 1.2);
            bool Cabe(AjusteNome a) => a.Linhas.Count * 1.2 * a.Tamanho <= H + 0.01
                                       && a.Linhas.Max(Larg) * a.Tamanho * a.EscalaX <= W + 0.01;
            checar(Cabe(curto) && Cabe(medio) && Cabe(grande), "o nome ajustado sempre cabe na área (largura e altura)");
            checar(curto.Linhas.Count == 1 && curto.Tamanho == H / 1.2, $"nome curto: uma linha, na altura inteira ({curto.Tamanho:0})");
            checar(curto.Tamanho > medio.Tamanho && medio.Tamanho > grande.Tamanho,
                $"nome curto fica enorme e o longo diminui ({curto.Tamanho:0} > {medio.Tamanho:0} > {grande.Tamanho:0})");
            checar(medio.Linhas.Count == 2, "nome médio quebra em 2 linhas antes de diminuir: " + string.Join(" / ", medio.Linhas));
            checar(grande.Linhas.Count == 2 && string.Join(" ", grande.Linhas) == "Maria Aparecida dos Santos Albuquerque",
                "nome longo: 2 linhas, nenhuma letra cortada (as linhas juntas são o nome inteiro)");
            checar(new[] { curto, medio, grande }.All(a => a.EscalaX >= 0.6 - 1e-9 && a.EscalaX <= 1),
                "aperto horizontal nunca abaixo de 0,6");
            var semEspaco = EtiquetaKds.AjustarNome("Aparecidaalbuquerquefigueiredo", W, H, Larg, 1.2);
            checar(Cabe(semEspaco) && semEspaco.Linhas.Count == 1 && semEspaco.EscalaX >= 0.6 - 1e-9,
                "nome sem espaço: uma linha só, cabe encolhendo, sem partir a palavra");

            // com a fonte de verdade (Bahnschrift Condensed ou o substituto): cabe na área do cabeçalho
            var real = Impressao.AjusteDoNome("Maria Aparecida dos Santos", 220, 113);
            checar(real.Linhas.Count == 2 && real.Tamanho > 30 && real.EscalaX >= 0.6 - 1e-9,
                $"fonte real: nome de 4 palavras em 2 linhas, letra {real.Tamanho:0} px, aperto {real.EscalaX:0.00}");

            // configuração: nasce em bobina
            checar(EtiquetaKds.Formato(null) == FormatoComanda.Bobina && EtiquetaKds.Formato("") == FormatoComanda.Bobina
                   && EtiquetaKds.Formato("lixo") == FormatoComanda.Bobina,
                "sem escolha do dono a comanda continua na BOBINA");
            checar(EtiquetaKds.Formato("etiqueta") == FormatoComanda.Etiqueta, "etiqueta só quando gravada etiqueta");
            checar(EtiquetaKds.Giro("45") == 0 && EtiquetaKds.Giro("180") == 180 && EtiquetaKds.Giro(null) == 0,
                "giro só 0/90/180/270; lixo é 0");
            var f0 = EtiquetaKds.Folha(0);
            var f90 = EtiquetaKds.Folha(90);
            checar(f0 is { LarguraMm: 100, AlturaMm: 150, Giro: 0 } && f90 is { LarguraMm: 150, AlturaMm: 100, Giro: 90 }
                   && EtiquetaKds.Folha(180) is { LarguraMm: 100, AlturaMm: 150 },
                "folha em pé 100x150 (0/180) ou deitada 150x100 (90/270)");
        }

        // ── 1b. O DESENHO DE VERDADE (WPF) ─────────────────────────────────
        {
            var png = Path.Combine(Path.GetTempPath(), $"etq_{Guid.NewGuid():N}.png");
            var png90 = Path.Combine(Path.GetTempPath(), $"etq90_{Guid.NewGuid():N}.png");
            try
            {
                var erro = Impressao.PreVisualizarEtiquetaAsync(Exemplo(), png, 0, 1.0).GetAwaiter().GetResult();
                var erro90 = Impressao.PreVisualizarEtiquetaAsync(Exemplo(), png90, 90, 1.0).GetAwaiter().GetResult();
                var (w, h) = Tamanho(png);
                var (w9, h9) = Tamanho(png90);
                // 100 x 150 mm a 96 dpi = 378 x 567 (+2 do contorno da foto)
                checar(erro is null && Math.Abs(w - 380) <= 2 && Math.Abs(h - 569) <= 2,
                    $"a etiqueta desenha em 100x150 mm ({w}x{h} px a 96 dpi)");
                checar(erro90 is null && Math.Abs(w9 - 569) <= 2 && Math.Abs(h9 - 380) <= 2,
                    $"girada 90 vira folha deitada 150x100 ({w9}x{h9})");
            }
            finally { try { File.Delete(png); File.Delete(png90); } catch { } }
        }

        // ── 1c. O TAMANHO DOS ITENS (06/10, dono): o maior que cabe no miolo ──
        {
            Ticket ComItens(int n) => Exemplo() with
            {
                ItensJson = System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(1, n)
                    .Select(k => new TicketItem($"Donut Sabor Numero {k}", 1000, null)).ToArray()),
            };
            var t3 = Impressao.MedirItensAsync(ComItens(3)).GetAwaiter().GetResult();
            var t8 = Impressao.MedirItensAsync(ComItens(8)).GetAwaiter().GetResult();
            var t40 = Impressao.MedirItensAsync(ComItens(40)).GetAwaiter().GetResult();
            checar(t3.Fonte > t8.Fonte, $"fonte dos itens MAIOR com 3 itens que com 8 ({t3.Fonte:0.0} > {t8.Fonte:0.0})");
            checar(t3.Fonte > 19 * 1.3, $"pedido curto ganha letra bem maior que a antiga de 19 px ({t3.Fonte:0.0})");
            checar(t3.Escala <= Impressao.EscalaItensMax + 1e-9 && t8.Escala >= Impressao.EscalaItensMin - 1e-9,
                "a escala fica entre o piso e o teto");
            checar(Math.Abs(t3.LadoCaixa / t3.Fonte - t8.LadoCaixa / t8.Fonte) < 1e-9 && t3.LadoCaixa > t8.LadoCaixa,
                $"quadradinho proporcional à fonte ({t3.LadoCaixa:0.0} px com {t3.Fonte:0.0}; {t8.LadoCaixa:0.0} com {t8.Fonte:0.0})");
            checar(t3.Traco > t8.Traco && t8.Traco >= 1.6, "traço do quadradinho engrossa com a fonte e nunca fica fino");
            checar(t3.QrMm == EtiquetaKds.QrLadoMm && t40.QrMm == EtiquetaKds.QrLadoMinMm && t40.QrMm >= 35,
                $"QR de 40 mm; só pedido enorme o leva a 35 mm, nunca menos ({t40.QrMm})");
            checar(t40.Escala == Impressao.EscalaItensMin, "pedido enorme fica no piso legível (o resto encolhe, não corta)");
        }

        // ── 2. O LEITOR USB (teclado) ──────────────────────────────────────
        {
            string? Rajada(LeitorKds l, string texto, int passoMs, bool campo = false, long t0 = 1000)
            {
                var t = t0;
                foreach (var c in texto) { l.Caractere(c.ToString(), t, campo); t += passoMs; }
                return l.Enter(t, campo);
            }

            var leitor = new LeitorKds();
            checar(Rajada(leitor, "ADKDS:0b1f6c2e-aaaa", 8) == "0b1f6c2e-aaaa", "rajada rápida com prefixo e Enter é reconhecida");
            checar(Rajada(leitor, "ADKDS:abc", 150) is null, "a mesma sequência digitada devagar (150 ms) é ignorada");
            checar(Rajada(leitor, "XXKDS:abc", 8) is null, "prefixo errado é ignorado");
            checar(Rajada(leitor, "7891234567890", 8) is null, "código de barras de produto (sem prefixo) é ignorado");
            checar(Rajada(leitor, "ADKDS:abc", 8, campo: true) is null, "com o foco num campo de texto nada dispara");

            // em campo de texto nenhuma tecla é engolida
            var l2 = new LeitorKds();
            var engoliu = false;
            long tt = 0;
            foreach (var c in "ADKDS:abc") engoliu |= l2.Caractere(c.ToString(), tt += 5, emCampoDeTexto: true);
            checar(!engoliu, "em campo de texto o leitor não engole tecla nenhuma (o operador não perde digitação)");

            // fora de campo, a rajada é engolida (o Enter vai junto, quem chama engole)
            var l3 = new LeitorKds();
            var todas = true;
            tt = 0;
            foreach (var c in "ADKDS:ab") todas &= l3.Caractere(c.ToString(), tt += 5, false);
            checar(todas, "fora de campo de texto a rajada da etiqueta é engolida tecla a tecla");
            checar(!new LeitorKds().Caractere("q", 10, false), "tecla solta que não começa etiqueta não é engolida");

            // Enter sem nada antes
            checar(new LeitorKds().Enter(10, false) is null, "Enter sozinho não é bipe");
            // Enter atrasado depois da rajada
            var l4 = new LeitorKds();
            tt = 0;
            foreach (var c in "ADKDS:abc") l4.Caractere(c.ToString(), tt += 5, false);
            checar(l4.Enter(tt + 400, false) is null, "Enter muito depois da rajada não fecha a etiqueta");
            // só o prefixo
            checar(Rajada(new LeitorKds(), "ADKDS:", 5) is null, "só o prefixo, sem id, não é etiqueta");
            // lixo antes da rajada (tecla perdida) não estraga o bipe
            checar(Rajada(new LeitorKds(), "zADKDS:abc", 5) == "abc", "tecla solta antes da rajada não estraga o bipe");
            // pausa longa no meio reinicia: o resto não vira etiqueta
            var l5 = new LeitorKds();
            l5.Caractere("A", 0, false); l5.Caractere("D", 5, false);
            foreach (var (c, i) in "KDS:abc".Select((c, i) => (c, i))) l5.Caractere(c.ToString(), 500 + i * 5, false);
            checar(l5.Enter(540, false) is null, "pausa no meio da sequência descarta a etiqueta");
            // leitor em inglês num Windows ABNT2: ':' sai como 'Ç'; Caps Lock no leitor
            checar(Rajada(new LeitorKds(), "ADKDSÇ0B1F-AA", 5) == "0b1f-aa",
                "leitor configurado em inglês (':' vira 'Ç' no ABNT2) e maiúsculas ainda funcionam");
            checar(Rajada(new LeitorKds(), "adkds:abc", 5) == "abc", "prefixo em minúsculas (Caps Lock) funciona");
            checar(Rajada(new LeitorKds(), "ADKDS:ab c", 5) is null, "espaço no id não é etiqueta");
            // duas etiquetas seguidas
            var l6 = new LeitorKds();
            var a = Rajada(l6, "ADKDS:um", 5, t0: 0);
            var b = Rajada(l6, "ADKDS:dois", 5, t0: 1000);
            checar(a == "um" && b == "dois", "duas etiquetas seguidas, cada uma com seu Enter");
        }

        // ── 3. O BIPE CHAMANDO O LIBERAR ───────────────────────────────────
        {
            // com dublês: conta as chamadas
            var chamadas = 0;
            var t = Exemplo(status: Kds.Preparando);
            var bipe = new BipeKds(_ => t, _ => { chamadas++; return true; });
            var agora = new DateTime(2026, 10, 5, 18, 0, 0);
            var r1 = bipe.Processar(t.RefId, agora);
            var r2 = bipe.Processar(t.RefId, agora.AddMilliseconds(900));
            checar(r1.Desfecho == DesfechoBipe.Pronto && r1.Mensagem == "PRONTO 8149", "bipe em preparo vira PRONTO 8149");
            checar(r2.Desfecho == DesfechoBipe.Repetido && chamadas == 1,
                $"bipe duplo em menos de 2 s NÃO libera duas vezes (Liberar chamado {chamadas}x)");

            var chamadasB = 0;
            var tPronto = Exemplo(status: Kds.Pronto);
            var bipeB = new BipeKds(_ => tPronto, _ => { chamadasB++; return true; });
            var rp = bipeB.Processar(tPronto.RefId, agora);
            checar(rp.Desfecho == DesfechoBipe.JaPronto && rp.Mensagem == "8149 já estava pronto" && chamadasB == 0,
                "já pronto: avisa 8149 já estava pronto e não chama Liberar");
            var rn = new BipeKds(_ => null, _ => true).Processar("nao-existe", agora);
            checar(rn.Desfecho == DesfechoBipe.NaoReconhecida && rn.Mensagem == "Etiqueta não reconhecida",
                "código que não é de pedido nenhum: Etiqueta não reconhecida");
            var rc = new BipeKds(_ => Exemplo(status: Kds.Cancelado), _ => throw new Exception("não pode"))
                .Processar("x", agora);
            checar(rc.Desfecho == DesfechoBipe.Cancelado, "pedido cancelado não vai para coleta pelo bipe");
            var rt = new BipeKds(_ => throw new Exception("não pode consultar"), _ => true)
                .Processar(EtiquetaKds.IdTeste, agora);
            checar(rt.Desfecho == DesfechoBipe.Teste, "a etiqueta de TESTE responde leitor OK sem marcar nada");
            checar(Servicos.ComandaDeExemplo().RefId == EtiquetaKds.IdTeste,
                "a etiqueta de exemplo da Configuração carrega o código de teste");

            // de verdade, no SQLite: o caminho do botão (status + outbox) e uma vez só
            var arquivo = Path.Combine(Path.GetTempPath(), $"etq_bipe_{Guid.NewGuid():N}.db");
            var anterior = Banco.CaminhoForcado;
            Banco.CaminhoForcado = arquivo;
            try
            {
                Banco.Migrar(arquivo);
                var orderId = "order-bipe-" + Guid.NewGuid().ToString("N")[..8];
                var id = Kds.DoDelivery(orderId, "8149", "Ana",
                    Kds.ItensDeJson("[{\"qtd\":1,\"descricao\":\"DONUT\"}]"))!;
                var real = BipeKds.Padrao();
                var x1 = real.Processar(orderId.ToUpperInvariant(), agora);   // o leitor pode mandar maiúsculas
                var x2 = real.Processar(orderId, agora.AddSeconds(1));
                var x3 = real.Processar(orderId, agora.AddSeconds(5));
                using var cx = Banco.Abrir();
                var status = cx.ExecuteScalar<string>("SELECT status FROM kds_ticket WHERE id=@id", new { id });
                var avisos = cx.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM outbox WHERE tipo='kds_pronto' AND ref_id=@r", new { r = orderId });
                checar(x1.Desfecho == DesfechoBipe.Pronto && status == Kds.Pronto,
                    $"bipe num pedido A PREPARAR leva direto a PRONTO no SQLite ({status})");
                checar(avisos == 1, $"o bipe enfileira o kds_pronto para o iFood, uma vez só ({avisos})");
                checar(cx.ExecuteScalar<string>("SELECT preparo_em FROM kds_ticket WHERE id=@id", new { id }) is not null,
                    "pular a fila também carimba a hora de início (o tempo de preparo não fica nulo)");
                checar(x2.Desfecho == DesfechoBipe.Repetido && x3.Desfecho == DesfechoBipe.JaPronto,
                    "bipe repetido em 1 s some; 5 s depois responde já estava pronto");
                // pelo id local também acha
                var porId = BipeKds.Localizar(id);
                checar(porId?.Id == id, "a etiqueta também acha o pedido pelo id local do ticket");
                checar(BipeKds.Localizar("ninguem") is null, "código desconhecido não acha ticket");
            }
            finally
            {
                Banco.CaminhoForcado = anterior;
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { File.Delete(arquivo); } catch { }
            }
        }
    }

    private static (int W, int H) Tamanho(string png)
    {
        using var fs = File.OpenRead(png);
        var dec = new System.Windows.Media.Imaging.PngBitmapDecoder(fs,
            System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        return (dec.Frames[0].PixelWidth, dec.Frames[0].PixelHeight);
    }
}
