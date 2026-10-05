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
        // ── 1. LAYOUT PURO ──────────────────────────────────────────────────
        {
            var e = EtiquetaKds.Montar(Exemplo(), new DateTime(2026, 10, 5));
            var grupo = e.Linhas.FirstOrDefault(l => l.Tipo == TipoLinhaEtiqueta.Grupo);
            checar(grupo is not null && !grupo.Caixa && grupo.Texto.Contains("Combo Box") && grupo.Qtd == "1×",
                "combo vira TÍTULO do grupo, com a quantidade e SEM quadradinho");
            var subs = e.Linhas.Where(l => l.Tipo == TipoLinhaEtiqueta.Subitem).ToList();
            checar(subs.Count == 3 && subs.All(s => s.Caixa && s.Nivel == 1),
                $"cada subitem do combo tem o PRÓPRIO quadradinho, recuado (achei {subs.Count})");
            checar(subs.Any(s => s.Qtd == "2×" && s.Texto.Contains("Donut Ninho")),
                "subitem com grupo e quantidade sai como 2× Clássicos: Donut Ninho");
            var simples = e.Linhas.FirstOrDefault(l => l.Texto == "Cookie Duplo");
            checar(simples is { Tipo: TipoLinhaEtiqueta.Item, Caixa: true, Qtd: "2×" },
                "item sem subitem tem quadradinho no próprio item");
            var obs = e.Linhas.FirstOrDefault(l => l.Tipo == TipoLinhaEtiqueta.Observacao);
            checar(obs is { Caixa: false, Nivel: 1, Texto: "sem granulado" },
                "observação do item sai recuada e sem quadradinho");
            var iObs = e.Linhas.ToList().IndexOf(obs!);
            var iGrupo = e.Linhas.ToList().IndexOf(grupo!);
            var iCookie = e.Linhas.ToList().IndexOf(simples!);
            checar(iGrupo < iObs && iObs < iCookie, "a observação fica logo abaixo do item dela, antes do próximo item");
            checar(e.Linhas.Count(l => l.Caixa) == 4, "4 caixas: 3 sabores + 1 item simples (o combo não conta)");
            checar(e.Numero == "8149" && e.Origem == "iFOOD", "número e origem iFOOD no topo");
            checar(e.Chegou == "Chegou 17:37", "hora de chegada vai na etiqueta");

            var cd = EtiquetaKds.Montar(Exemplo(numero: "CD-1234"));
            checar(cd.Origem == "CARDÁPIO WEB", "número CD- é CARDÁPIO WEB, como na comanda de bobina");

            // QR: prefixo fixo + order_id
            checar(e.Qr == "ADKDS:0b1f6c2e-aaaa-4bbb-8ccc-1234567890ab", $"QR leva ADKDS: + order_id ({e.Qr})");
            checar(LeitorKds.Id(e.Qr) == "0b1f6c2e-aaaa-4bbb-8ccc-1234567890ab",
                "o que o QR carrega volta inteiro pelo parser do leitor (ida e volta)");
            checar(EtiquetaKds.QrLadoMm >= 35, "QR com pelo menos 35 mm de lado");

            // nome do cliente
            checar(e.Cliente.Count == 1 && e.Cliente[0] == "Ana Beatriz Souza", "nome curto sai inteiro numa linha");
            var longo = EtiquetaKds.CortarNome("Maria Aparecida dos Santos Albuquerque Figueiredo", 18, 2);
            checar(longo.Count == 2 && longo.All(l => l.Length <= 18) && longo[^1].EndsWith("…"),
                "nome longo: no máximo 2 linhas de 18, a última com reticências: " + string.Join(" | ", longo));
            checar(longo[0] == "Maria Aparecida", "quebra entre palavras, não no meio do nome: " + longo[0]);
            var duas = EtiquetaKds.CortarNome("Maria Aparecida dos Santos", 18, 2);
            checar(duas.Count == 2 && !duas[^1].EndsWith("…"), "nome que cabe em 2 linhas não ganha reticências");
            var colado = EtiquetaKds.CortarNome("ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOP", 18, 2);
            checar(colado.Count == 2 && colado.All(l => l.Length <= 18) && colado[^1].EndsWith("…"),
                "nome sem espaço maior que a linha é partido, não estoura o papel");
            checar(EtiquetaKds.CortarNome(null, 18, 2).Count == 0 && EtiquetaKds.CortarNome("   ", 18, 2).Count == 0,
                "sem nome: nenhuma linha de cliente");

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
