using Dapper;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A POLÍTICA DE IMPRESSÃO POR DOCUMENTO (pedido do dono, 03/09).
///
/// Quatro papéis (cupom, comanda do delivery, via do CLIENTE do cartão e via do
/// ESTABELECIMENTO) e três respostas para cada um: sai sozinho, aparece botão na tela,
/// ou não sai.
///
/// O que não pode quebrar aqui: uma loja que nunca abrir esta opção tem que continuar
/// imprimindo exatamente o que imprime hoje. Antes as quatro decisões eram três
/// booleanos com PADRÕES DIFERENTES entre si, então o fallback de cada chave antiga é
/// a metade do arquivo — e é a metade que a atualização pode quebrar em silêncio.
/// </summary>
public static class TestesPoliticaImpressao
{
    public static void Rodar(Action<bool, string> checar)
    {
        ChaveNova(checar);
        Fallback(checar);
        Sincronia(checar);
        NaTela(checar);
        ViasDoCartao(checar);
        Revisao(checar);
        UmLeitorSo(checar);
    }

    // ── (a) a chave nova manda ──────────────────────────────────────────────
    private static void ChaveNova(Action<bool, string> checar)
    {
        foreach (var doc in Impressoes.Documentos)
        {
            checar(Impressoes.Politica(doc, "auto", null) == PoliticaImpressao.Automatico,
                $"{doc}: 'auto' na chave nova é imprimir sozinho");
            checar(Impressoes.Politica(doc, "perguntar", null) == PoliticaImpressao.Perguntar,
                $"{doc}: 'perguntar' na chave nova é botão na tela");
            checar(Impressoes.Politica(doc, "nao", null) == PoliticaImpressao.Nao,
                $"{doc}: 'nao' na chave nova é não imprimir");

            // A chave NOVA manda mesmo com a antiga dizendo outra coisa: é ela que a
            // tela grava, e a antiga é só o rastro para quem ainda lê o booleano.
            checar(Impressoes.Politica(doc, "nao", "1") == PoliticaImpressao.Nao,
                $"{doc}: a chave nova ganha da antiga");
            checar(Impressoes.Politica(doc, "  AUTO  ", "0") == PoliticaImpressao.Automatico,
                $"{doc}: espaço e caixa não mudam a resposta da chave nova");
            checar(Impressoes.Texto(Impressoes.Politica(doc, Impressoes.Texto(PoliticaImpressao.Nao), null))
                   == "nao",
                $"{doc}: o texto gravado volta como a mesma política (ida e volta)");
        }

        // Lixo na chave nova não pode virar "não imprime": cai no fallback, que é o
        // comportamento de hoje.
        checar(Impressoes.Politica(Impressoes.Cupom, "sim", null) == PoliticaImpressao.Automatico,
            "valor desconhecido na chave nova cai no fallback e o cupom continua saindo");

        // As chaves têm nome próprio: trocá-las embaralha as políticas dos quatro papéis.
        checar(Impressoes.Chave(Impressoes.Cupom) == "imp_cupom"
               && Impressoes.Chave(Impressoes.Comanda) == "imp_comanda"
               && Impressoes.Chave(Impressoes.ViaCliente) == "imp_via_cliente"
               && Impressoes.Chave(Impressoes.ViaEstabelecimento) == "imp_via_estabelecimento",
            "cada papel tem a sua chave nova em config");
    }

    // ── (b) o fallback de cada chave antiga ─────────────────────────────────
    private static void Fallback(Action<bool, string> checar)
    {
        // CUPOM: nascia LIGADO, e o "0" nunca significou "não imprimir" — significava
        // "não sai sozinho, o botão Imprimir recibo está aí". Traduzir para Nao apagaria
        // o botão que as lojas usam hoje.
        checar(Impressoes.Politica(Impressoes.Cupom, null, null) == PoliticaImpressao.Automatico,
            "cupom sem chave nenhuma continua saindo sozinho (era o padrão ligado)");
        checar(Impressoes.Politica(Impressoes.Cupom, null, "1") == PoliticaImpressao.Automatico,
            "cupom com imprimir_automatico=1 sai sozinho");
        checar(Impressoes.Politica(Impressoes.Cupom, null, "0") == PoliticaImpressao.Perguntar,
            "⭐ cupom com imprimir_automatico=0 vira PERGUNTAR, não Nao (o botão tem que continuar aparecendo)");

        // COMANDA: nascia DESLIGADA (opt-in) e já tem o 🖨 por card na tela Delivery,
        // então ausente e "0" são a mesma coisa: perguntar.
        checar(Impressoes.Politica(Impressoes.Comanda, null, null) == PoliticaImpressao.Perguntar,
            "comanda sem chave nenhuma NÃO sai sozinha (opt-in), e o 🖨 do card continua lá");
        checar(Impressoes.Politica(Impressoes.Comanda, null, "0") == PoliticaImpressao.Perguntar,
            "comanda com kds_comanda_auto=0 é perguntar");
        checar(Impressoes.Politica(Impressoes.Comanda, null, "1") == PoliticaImpressao.Automatico,
            "comanda com kds_comanda_auto=1 sai sozinha quando o pedido chega");

        // VIAS: uma chave só para as duas, e desligada não deixava botão nenhum na tela
        // da venda. É a ÚNICA que vira Nao no fallback — senão a loja que desligou volta
        // a gastar papel na primeira atualização.
        foreach (var via in new[] { Impressoes.ViaCliente, Impressoes.ViaEstabelecimento })
        {
            checar(Impressoes.Politica(via, null, null) == PoliticaImpressao.Automatico,
                $"{via} sem chave nenhuma sai sozinha (era o padrão ligado)");
            checar(Impressoes.Politica(via, null, "1") == PoliticaImpressao.Automatico,
                $"{via} com tef_paygo_imprimir_vias=1 sai sozinha");
            checar(Impressoes.Politica(via, null, "0") == PoliticaImpressao.Nao,
                $"⭐ {via} com tef_paygo_imprimir_vias=0 vira NÃO IMPRIMIR (quem desligou continua desligado)");
        }

        checar(Impressoes.ChaveAntiga(Impressoes.ViaCliente) == "tef_paygo_imprimir_vias"
               && Impressoes.ChaveAntiga(Impressoes.ViaEstabelecimento) == "tef_paygo_imprimir_vias",
            "as duas vias vinham da MESMA chave antiga: era um interruptor só");
    }

    // ── (c) gravar pela tela mantém a chave antiga em sincronia ─────────────
    private static void Sincronia(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"politica_teste_{Guid.NewGuid():N}.db");
        Banco.Migrar(arquivo);
        using var cx = Banco.Abrir(arquivo);

        // Banco novo, nada gravado: a leitura pelo banco tem que dar o mesmo que a
        // função pura, senão a tela mostra uma coisa e o caixa faz outra.
        checar(Impressoes.Politica(cx, Impressoes.Cupom) == PoliticaImpressao.Automatico
               && Impressoes.Politica(cx, Impressoes.Comanda) == PoliticaImpressao.Perguntar
               && Impressoes.Politica(cx, Impressoes.ViaCliente) == PoliticaImpressao.Automatico,
            "caixa novo lê pelo banco a mesma política que a regra pura devolve");

        // Caixa que já rodava com a impressão automática desligada.
        Vendas.GravarConfig(cx, "imprimir_automatico", "0");
        checar(Impressoes.Politica(cx, Impressoes.Cupom) == PoliticaImpressao.Perguntar,
            "banco com imprimir_automatico=0 e sem chave nova lê PERGUNTAR");

        // ⭐ O QUE NÃO PODE FALTAR: gravar pela tela escreve a NOVA e alinha a ANTIGA.
        // Sem isto o diálogo 🖨 da barra da venda e o resto do código que ainda lê o
        // booleano ficariam com a resposta velha.
        Impressoes.Gravar(cx, Impressoes.Cupom, PoliticaImpressao.Automatico);
        checar(Vendas.Config(cx, "imp_cupom") == "auto" && Vendas.Config(cx, "imprimir_automatico") == "1",
            "gravar cupom=automático grava a chave nova E acende a antiga");
        Impressoes.Gravar(cx, Impressoes.Cupom, PoliticaImpressao.Perguntar);
        checar(Vendas.Config(cx, "imp_cupom") == "perguntar" && Vendas.Config(cx, "imprimir_automatico") == "0",
            "gravar cupom=perguntar apaga a antiga (quem lê o booleano não imprime sozinho)");
        Impressoes.Gravar(cx, Impressoes.Cupom, PoliticaImpressao.Nao);
        checar(Vendas.Config(cx, "imp_cupom") == "nao" && Vendas.Config(cx, "imprimir_automatico") == "0"
               && Impressoes.Politica(cx, Impressoes.Cupom) == PoliticaImpressao.Nao,
            "gravar cupom=não imprimir deixa a antiga em 0 e a nova guarda o terceiro estado");

        Impressoes.Gravar(cx, Impressoes.Comanda, PoliticaImpressao.Automatico);
        checar(Vendas.Config(cx, "imp_comanda") == "auto" && Vendas.Config(cx, "kds_comanda_auto") == "1",
            "gravar comanda=automático acende kds_comanda_auto");
        Impressoes.Gravar(cx, Impressoes.Comanda, PoliticaImpressao.Nao);
        checar(Vendas.Config(cx, "kds_comanda_auto") == "0",
            "gravar comanda=não imprimir apaga kds_comanda_auto");

        // AS DUAS VIAS DIVIDEM UMA CHAVE ANTIGA: ela só desliga quando as duas estão em
        // "não imprimir". Desligar por causa de uma apagaria a outra para quem lê o
        // booleano.
        Impressoes.Gravar(cx, Impressoes.ViaCliente, PoliticaImpressao.Nao);
        checar(Vendas.Config(cx, "tef_paygo_imprimir_vias") == "1",
            "só a via do cliente em 'não imprimir' NÃO desliga a chave antiga (a do estabelecimento continua saindo)");
        Impressoes.Gravar(cx, Impressoes.ViaEstabelecimento, PoliticaImpressao.Nao);
        checar(Vendas.Config(cx, "tef_paygo_imprimir_vias") == "0",
            "com as DUAS em 'não imprimir', a chave antiga desliga");
        Impressoes.Gravar(cx, Impressoes.ViaEstabelecimento, PoliticaImpressao.Perguntar);
        checar(Vendas.Config(cx, "tef_paygo_imprimir_vias") == "1"
               && Impressoes.Politica(cx, Impressoes.ViaCliente) == PoliticaImpressao.Nao,
            "religar uma via religa a chave antiga sem mexer na política da outra");

        try { File.Delete(arquivo); } catch { }
    }

    // ── (d) e (e): o que a tela faz com cada política ───────────────────────
    private static void NaTela(Action<bool, string> checar)
    {
        // CUPOM — a mesma decisão para os dois modos fiscais (recibo e NFC-e).
        var auto = Impressoes.DecidirCupom(PoliticaImpressao.Automatico, forcado: false);
        checar(auto.Imprime && !auto.MostraBotao,
            "cupom automático: sai sozinho e não precisa de botão");

        var perg = Impressoes.DecidirCupom(PoliticaImpressao.Perguntar, forcado: false);
        checar(!perg.Imprime && perg.MostraBotao,
            "⭐ cupom perguntar: NÃO imprime sozinho e o botão fica na tela");

        var nao = Impressoes.DecidirCupom(PoliticaImpressao.Nao, forcado: false);
        checar(!nao.Imprime && !nao.MostraBotao,
            "⭐ cupom não imprimir: não sai papel e não aparece botão");

        // Dedo humano imprime em qualquer política: é o "Reimprimir" do cupom entalado.
        foreach (var p in new[] { PoliticaImpressao.Automatico, PoliticaImpressao.Perguntar, PoliticaImpressao.Nao })
        {
            var f = Impressoes.DecidirCupom(p, forcado: true);
            checar(f.Imprime && !f.MostraBotao,
                $"cupom em '{Impressoes.Texto(p)}': o toque no botão imprime e o botão sai da frente");
        }

        // COMANDA — o 🖨 do card é o "perguntar", e também o socorro da automática que
        // falhou. Só "não imprimir" apaga o botão.
        checar(Impressoes.MostraBotaoComanda(PoliticaImpressao.Perguntar),
            "comanda perguntar: o 🖨 do card do Delivery fica visível");
        checar(Impressoes.MostraBotaoComanda(PoliticaImpressao.Automatico),
            "comanda automática: o 🖨 continua, é o socorro de quando o papel não sai");
        checar(!Impressoes.MostraBotaoComanda(PoliticaImpressao.Nao),
            "⭐ comanda não imprimir: o 🖨 do card some");

        // ⭐ E o gate de verdade: com a comanda em "perguntar" ou "não imprimir", o
        // pedido pendente NÃO é reivindicado nem impresso pelo sino/timer.
        var arquivo = Path.Combine(Path.GetTempPath(), $"politica_kds_{Guid.NewGuid():N}.db");
        var antes = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            foreach (var politica in new[] { PoliticaImpressao.Perguntar, PoliticaImpressao.Nao })
            {
                using (var cx = Banco.Abrir(arquivo))
                {
                    cx.Execute("DELETE FROM kds_ticket");
                    Impressoes.Gravar(cx, Impressoes.Comanda, politica);
                    cx.Execute("""
                        INSERT INTO kds_ticket (id, origem, ref_id, numero, cliente, itens_json, status, criado_em)
                        VALUES ('p-1','ifood','r-1','9001','TESTE','[]',@S,@Em)
                        """, new { S = Nucleo.Kds.Recebido, Em = DateTime.Now.ToString("o") });
                }
                var falha = Servicos.ImprimirComandasPendentesAsync().GetAwaiter().GetResult();
                using (var cx = Banco.Abrir(arquivo))
                {
                    var impresso = cx.ExecuteScalar<string?>("SELECT impresso_em FROM kds_ticket WHERE id='p-1'");
                    checar(falha is null && impresso is null,
                        $"⭐ comanda em '{Impressoes.Texto(politica)}': o pedido pendente não é impresso sozinho");
                }
            }
        }
        finally
        {
            Banco.CaminhoForcado = antes;
            try { File.Delete(arquivo); } catch { }
        }
    }

    // ── as duas vias do cartão, separadas de verdade ────────────────────────
    private static void ViasDoCartao(Action<bool, string> checar)
    {
        static RespostaPayGo Resposta(int vias, bool cliente = true, bool estabelecimento = true, bool unica = false)
        {
            var l = new List<string> { "000-000 = \"CRT\"", "009-000 = \"0\"", $"737-000 = \"{vias}\"" };
            if (cliente) { l.Add("712-000 = \"1\""); l.Add("713-001 = \"VIA CLIENTE\""); }
            if (estabelecimento) { l.Add("714-000 = \"1\""); l.Add("715-001 = \"VIA ESTABELECIMENTO\""); }
            if (unica) { l.Add("028-000 = \"1\""); l.Add("029-001 = \"VIA UNICA\""); }
            return RespostaPayGo.Analisar(string.Join("\r\n", l));
        }

        var r = Resposta(3);
        var rotuladas = Servicos.ViasRotuladas(r);
        checar(rotuladas.Count == 2
               && rotuladas[0].Qual == Servicos.ViaTef.Cliente
               && rotuladas[1].Qual == Servicos.ViaTef.Estabelecimento,
            "as duas vias saem rotuladas e na ordem [cliente, estabelecimento]");

        // Regressão: a lista sem rótulo continua igual à de antes (é a que a reimpressão
        // manual e o diálogo "Qual via você precisa?" usam).
        checar(Servicos.ViasParaImprimir(r).Count == 2
               && Servicos.ViasParaImprimir(Resposta(0)).Count == 0,
            "ViasParaImprimir não mudou: duas vias com 737=3 e nenhuma com 737=0");

        var todasAuto = Servicos.ViasAutomaticas(r, PoliticaImpressao.Automatico, PoliticaImpressao.Automatico);
        checar(todasAuto.Count == 2,
            "com as duas em automático, saem as duas vias (é o que este caixa faz hoje)");

        // ⭐ O PEDIDO DO DONO: uma via de cada vez.
        var soCliente = Servicos.ViasAutomaticas(r, PoliticaImpressao.Automatico, PoliticaImpressao.Nao);
        checar(soCliente.Count == 1 && soCliente[0][0].Contains("VIA CLIENTE"),
            "⭐ estabelecimento em 'não imprimir': sai só a via do cliente");
        var soLoja = Servicos.ViasAutomaticas(r, PoliticaImpressao.Nao, PoliticaImpressao.Automatico);
        checar(soLoja.Count == 1 && soLoja[0][0].Contains("VIA ESTABELECIMENTO"),
            "⭐ cliente em 'não imprimir': sai só a via do estabelecimento");

        // "Perguntar" não imprime SOZINHO — o operador tira em TEF → Reimprimir.
        var pergunta = Servicos.ViasAutomaticas(r, PoliticaImpressao.Perguntar, PoliticaImpressao.Perguntar);
        checar(pergunta.Count == 0,
            "⭐ as duas em 'perguntar': nada sai sozinho (a reimpressão manual continua à mão)");
        var clientePergunta = Servicos.ViasAutomaticas(r, PoliticaImpressao.Perguntar, PoliticaImpressao.Automatico);
        checar(clientePergunta.Count == 1 && clientePergunta[0][0].Contains("VIA ESTABELECIMENTO"),
            "cliente em 'perguntar' segura só a via dele; a da loja continua saindo");

        // Nada configurado = as duas em automático = as duas vias, como sempre foi.
        checar(Servicos.ViasAutomaticas(r,
                   Impressoes.Politica(Impressoes.ViaCliente, null, null),
                   Impressoes.Politica(Impressoes.ViaEstabelecimento, null, null)).Count == 2,
            "caixa que nunca abriu esta opção continua com as duas vias no papel");
        checar(Servicos.ViasAutomaticas(r,
                   Impressoes.Politica(Impressoes.ViaCliente, null, "0"),
                   Impressoes.Politica(Impressoes.ViaEstabelecimento, null, "0")).Count == 0,
            "caixa com tef_paygo_imprimir_vias=0 continua sem imprimir via nenhuma");

        // VIA ÚNICA SEM DONO: quando não veio 713 nem 715, o bloco que sobra é o papel
        // que vai para a MÃO DO CLIENTE — quem manda nele é a política da via do cliente.
        var unica = Resposta(3, cliente: false, estabelecimento: false, unica: true);
        var rotUnica = Servicos.ViasRotuladas(unica);
        checar(rotUnica.Count == 1 && rotUnica[0].Qual == Servicos.ViaTef.Unica,
            "sem 713 e sem 715, sobra um bloco único e ele é rotulado como via única");
        checar(Servicos.ViasAutomaticas(unica, PoliticaImpressao.Automatico, PoliticaImpressao.Nao).Count == 1,
            "a via única obedece à política da via do CLIENTE (é o papel que o cliente leva)");
        checar(Servicos.ViasAutomaticas(unica, PoliticaImpressao.Nao, PoliticaImpressao.Automatico).Count == 0,
            "e com a via do cliente em 'não imprimir', a via única também não sai");

        // 737 manda em quem existe: 1 = só cliente, 2 = só estabelecimento.
        checar(Servicos.ViasRotuladas(Resposta(1)).Count == 1
               && Servicos.ViasRotuladas(Resposta(1))[0].Qual == Servicos.ViaTef.Cliente,
            "737=1: a rede mandou só a via do cliente");
        checar(Servicos.ViasRotuladas(Resposta(2))[0].Qual == Servicos.ViaTef.Estabelecimento,
            "737=2: a rede mandou só a via do estabelecimento");
    }

    // ── a revisão do assistente conta o que foi escolhido ───────────────────
    private static void Revisao(Action<bool, string> checar)
    {
        static Pdv.Telas.DadosAssistente Base() => new()
        {
            Loja = "AMERICAN DAY SAVASSI", Cnpj = "62177839000238", Ie = "0012345670098",
            Serie = "1", Ambiente = 1, TemCertificado = true,
            Impressora = "EPSON TM-T20", PapelMm = 80, Tef = 2, Pareado = true,
        };
        static string Linha(Pdv.Telas.DadosAssistente d, string titulo)
            => Pdv.Telas.AssistenteConfig.Resumo(d).FirstOrDefault(l => l.Titulo.Contains(titulo))?.Valor ?? "";
        static bool Atencao(Pdv.Telas.DadosAssistente d, string titulo)
            => Pdv.Telas.AssistenteConfig.Resumo(d).FirstOrDefault(l => l.Titulo.Contains(titulo))?.Atencao == true;

        // ⚠️ Escolha invisível depois de gravada é o defeito que a revisão existe para
        // evitar. "Não imprimir" é a mais cara das três: some papel sem ninguém notar.
        checar(Linha(Base() with { PoliticaCupomEscolhida = PoliticaImpressao.Nao }, "cupom fiscal").Contains("NÃO É IMPRESSO")
               && Atencao(Base() with { PoliticaCupomEscolhida = PoliticaImpressao.Nao }, "cupom fiscal"),
            "⭐ cupom em 'não imprimir' aparece marcado na revisão");
        checar(Linha(Base() with { PoliticaComandaEscolhida = PoliticaImpressao.Nao }, "Comanda").Contains("NÃO É IMPRESSA")
               && Atencao(Base() with { PoliticaComandaEscolhida = PoliticaImpressao.Nao }, "Comanda"),
            "⭐ comanda em 'não imprimir' aparece marcada na revisão");
        checar(Linha(Base() with { PoliticaViaCliente = PoliticaImpressao.Nao }, "Comprovante").Contains("NÃO SAI")
               && Atencao(Base() with { PoliticaViaCliente = PoliticaImpressao.Nao }, "Comprovante"),
            "⭐ via do cliente desligada aparece marcada na revisão");
        checar(Linha(Base() with { PoliticaViaEstabelecimento = PoliticaImpressao.Perguntar }, "Comprovante")
                   .Contains("estabelecimento: só em TEF"),
            "as duas vias aparecem separadas na revisão, cada uma com o seu desfecho");

        // Maquininha avulsa não tem via para o caixa imprimir: a linha não pode existir.
        checar(Pdv.Telas.AssistenteConfig.Resumo(Base() with { Tef = 0 }).All(l => !l.Titulo.Contains("Comprovante")),
            "sem maquininha de cabo a revisão não fala das vias (não existe via para decidir)");

        // Regressão do atalho: quem preenche só o booleano antigo continua vendo o que via.
        checar(Base() with { ImprimirAuto = false } is { PoliticaCupom: PoliticaImpressao.Perguntar },
            "ImprimirAuto = false continua significando PERGUNTAR, não 'não imprimir'");
        checar(Base() with { ComandaAuto = true } is { PoliticaComanda: PoliticaImpressao.Automatico },
            "ComandaAuto = true continua significando imprimir sozinha");
    }

    // ── UM leitor só para cada chave ────────────────────────────────────────
    private static void UmLeitorSo(Action<bool, string> checar)
    {
        // A regra tem que morar em Impressoes e em nenhum outro lugar. Duas cópias da
        // mesma decisão divergem no primeiro dia — foi o que aconteceu com o cupom, que
        // tinha a leitura escrita duas vezes em Pagamento.xaml.cs e já discordava de si
        // mesma no auto-avanço. Este teste é sobre o FONTE porque a decisão vive na tela.
        string? Fonte(params string[] caminho)
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                var alvo = Path.Combine(new[] { d.FullName }.Concat(caminho).ToArray());
                if (File.Exists(alvo)) return File.ReadAllText(alvo);
            }
            return null;
        }

        foreach (var (arquivo, caminho) in new[]
        {
            ("Pagamento.xaml.cs", new[] { "Telas", "Pagamento.xaml.cs" }),
            ("Venda.xaml.cs", new[] { "Telas", "Venda.xaml.cs" }),
            ("Kds.xaml.cs", new[] { "Telas", "Kds.xaml.cs" }),
            ("Servicos.cs", new[] { "Servicos.cs" }),
        })
        {
            var fonte = Fonte(caminho);
            checar(fonte is not null, $"achei a fonte de {arquivo}");
            checar(fonte?.Contains("\"imprimir_automatico\"", StringComparison.Ordinal) != true
                   && fonte?.Contains("\"kds_comanda_auto\"", StringComparison.Ordinal) != true
                   && fonte?.Contains("\"tef_paygo_imprimir_vias\"", StringComparison.Ordinal) != true,
                $"⭐ {arquivo} não lê mais as chaves antigas na mão: quem responde é Impressoes");
        }

        var pagamento = Fonte("Telas", "Pagamento.xaml.cs") ?? "";
        checar(System.Text.RegularExpressions.Regex.Matches(pagamento, @"Impressoes\.DecidirCupom\(").Count == 2,
            "os dois modos fiscais (recibo e NFC-e) decidem o cupom pela MESMA função");
    }
}
