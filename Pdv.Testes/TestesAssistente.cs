using Pdv.Telas;

namespace Pdv.Testes;

/// <summary>
/// Testes do ASSISTENTE DE CONFIGURAÇÃO (5 passos: Loja → Nota fiscal → Impressora →
/// Maquininha → Pareamento, mais a revisão).
///
/// O que quebra aqui não aparece numa venda: aparece na INSTALAÇÃO, que acontece uma
/// vez, longe de quem escreveu o código, com o dono da loja sozinho na frente da tela.
/// Dois defeitos são caros e silenciosos:
///  · deixar passar (Avançar habilitado com o passo torto) — o caixa nasce sem PIX
///    configurado, ou com IE em branco, e ninguém descobre até a primeira recusa;
///  · travar sem dizer por quê (Avançar desligado e nenhuma frase na tela) — a
///    instalação para e vira ligação pro suporte.
/// Por isso todo teste aqui é sobre o PAR (bloqueia? e o que a tela diz).
///
/// As regras vivem em <c>AssistenteConfig</c>, fora do WPF, exatamente para poderem
/// ser exercitadas: a tela de verdade só existe com janela, SQLite e cofre DPAPI de pé.
/// </summary>
public static class TestesAssistente
{
    // CNPJ e CPF de dígito verificador VÁLIDO — as regras chamam o validador de verdade.
    private const string CnpjOk = "62177839000238";
    private const string CpfOk = "52998224725";

    public static void Rodar(Action<bool, string> checar)
    {
        Ordem(checar);
        PassoLoja(checar);
        InscricaoEstadual(checar);
        PassoFiscal(checar);
        SerieQueDaErro(checar);
        PassoImpressora(checar);
        ComandaDoDelivery(checar);
        PassoMaquininha(checar);
        PassoPareamento(checar);
        PortaUnica(checar);
        TelaDeResumo(checar);
        LarguraDoPapel(checar);
    }

    /// <summary>Instalação completa e válida: a base de comparação de todo teste daqui.</summary>
    private static DadosAssistente Pronta() => new()
    {
        Loja = "AMERICAN DAY SAVASSI",
        Cnpj = CnpjOk,
        Ie = "0012345670098",
        Recibo = false,
        Serie = "1",
        Ambiente = 1,
        TemCertificado = true,
        Impressora = "EPSON TM-T20",
        ImprimirAuto = true,
        PapelMm = 80,
        Tef = 0,
        Pareado = true,
        PedeAdmin = false,
    };

    /// <summary>A ordem dos passos é a que o dono pediu — e é ela que o Salvar percorre.</summary>
    private static void Ordem(Action<bool, string> checar)
    {
        checar((int)PassoConfig.Loja == 0 && (int)PassoConfig.Fiscal == 1 && (int)PassoConfig.Impressora == 2
               && (int)PassoConfig.Maquininha == 3 && (int)PassoConfig.Pareamento == 4,
            "os passos são Loja → Nota fiscal → Impressora → Maquininha → Pareamento");
        checar(AssistenteConfig.TotalPassos == 5 && (int)PassoConfig.Resumo == 5,
            "são 5 passos e a revisão vem depois deles (não conta como passo)");

        checar(AssistenteConfig.Indicador(PassoConfig.Loja).Contains("1")
               && AssistenteConfig.Indicador(PassoConfig.Loja).Contains("5")
               && AssistenteConfig.Indicador(PassoConfig.Pareamento).Contains("5 DE 5"),
            "o indicador diz em qual passo está E quantos são ('PASSO 1 DE 5')");

        for (var i = 0; i <= (int)PassoConfig.Resumo; i++)
            checar(AssistenteConfig.Nome((PassoConfig)i).Length > 2
                   && AssistenteConfig.Explicacao((PassoConfig)i).Length > 10,
                $"passo {i} tem nome e uma frase explicando o que se faz nele");
    }

    private static void PassoLoja(Action<bool, string> checar)
    {
        string? Bloq(DadosAssistente d) => AssistenteConfig.Bloqueio(PassoConfig.Loja, d);

        checar(Bloq(Pronta()) is null, "loja preenchida por completo libera o Avançar");
        checar(Bloq(Pronta() with { Loja = " " }) is not null, "sem nome da loja não avança");

        // O que falta tem que estar na frase: "CNPJ inválido" com 9 dígitos digitados
        // manda o operador conferir o que ele nem terminou de escrever.
        var faltando = Bloq(Pronta() with { Cnpj = "621778390" });
        checar(faltando is not null && faltando.Contains("5"),
            $"CNPJ pela metade diz QUANTOS dígitos faltam ({faltando})");

        var dvErrado = Bloq(Pronta() with { Cnpj = "62177839000239" });
        checar(dvErrado is not null && dvErrado.Contains("verificador"),
            "CNPJ de 14 dígitos com DV errado é barrado ANTES da Rejeição 207 da SEFAZ");

        // A máscara da tela manda pontuação junto; o validador tem que enxergar o número.
        checar(Bloq(Pronta() with { Cnpj = "62.177.839/0002-38" }) is null,
            "CNPJ com máscara é o mesmo CNPJ");

        // IE: obrigatória para emitir NFC-e, opcional em "só recibo".
        var semIe = Bloq(Pronta() with { Ie = "" });
        checar(semIe is not null && semIe.Contains("ISENTO"),
            "cupom fiscal sem IE não avança, e a frase mostra a saída (o botão ISENTO)");
        checar(Bloq(Pronta() with { Ie = "", Recibo = true }) is null,
            "em SÓ RECIBO a IE deixa de ser obrigatória — não há nota para a SEFAZ conferir");
        checar(Bloq(Pronta() with { Ie = "123", Recibo = true }) is not null,
            "mas IE digitada torta continua barrada em só recibo: número que não é de ninguém sai impresso no papel");
        checar(Bloq(Pronta() with { Ie = "ISENTO" }) is null, "ISENTO vale como inscrição estadual");
    }

    private static void InscricaoEstadual(Action<bool, string> checar)
    {
        checar(AssistenteConfig.NormalizarIe(" 001.234.567/0098 ") == "0012345670098",
            "a IE é guardada só com os dígitos — pontuação é enfeite de tela");
        foreach (var jeito in new[] { "isento", " Isento ", "ISENTA", "isentO" })
            checar(AssistenteConfig.NormalizarIe(jeito) == AssistenteConfig.IeIsento,
                $"'{jeito}' vira a palavra ISENTO, que é o que sai impresso no cupom");

        checar(AssistenteConfig.IeValida("ISENTO"), "ISENTO é válida");
        checar(AssistenteConfig.IeValida("0012345670098"), "IE de MG (13 dígitos) é válida");
        checar(AssistenteConfig.IeValida("12345678"), "IE de 8 dígitos (RJ) é válida");
        checar(!AssistenteConfig.IeValida("123"), "IE de 3 dígitos é erro de digitação, não inscrição");
        checar(!AssistenteConfig.IeValida("012345678901234567"), "IE longa demais não passa");
        checar(!AssistenteConfig.IeValida(""), "IE vazia não é válida (quem não tem usa ISENTO)");
        // SP usa letra na inscrição de produtor rural: recusá-la travaria a instalação
        // de um contribuinte legítimo, e a nota rejeitada explica melhor que a tela.
        checar(AssistenteConfig.IeValida("P011004243002"),
            "IE com letra (produtor rural de SP) passa — a tela não é a autoridade sobre a regra de cada estado");
    }

    private static void PassoFiscal(Action<bool, string> checar)
    {
        string? Bloq(DadosAssistente d) => AssistenteConfig.Bloqueio(PassoConfig.Fiscal, d);

        checar(Bloq(Pronta()) is null, "série 1 avança");
        // Série 1 é o padrão de um caixa novo (era 3, herança da primeira loja).
        checar(Bloq(Pronta() with { Serie = "999" }) is null && Bloq(Pronta() with { Serie = "1000" }) is not null,
            "a série vai de 1 a 999");
        checar(Bloq(Pronta() with { Serie = "0" }) is not null, "série 0 não existe na NFC-e");
        checar(Bloq(Pronta() with { Serie = "" }) is not null && Bloq(Pronta() with { Serie = "três" }) is not null,
            "série vazia ou por extenso não avança");
        checar(Bloq(Pronta() with { Serie = "2", Recibo = true }) is null,
            "a série continua valendo em só recibo — ela identifica o CAIXA para o dia em que a NFC-e ligar");

        // Certificado é AVISO, não impedimento: dá pra instalar o caixa inteiro antes de
        // o contador entregar o .pfx. Quem cobra isso é o teste da tela e o Salvar.
        checar(Bloq(Pronta() with { TemCertificado = false, Ambiente = 2 }) is null,
            "sem certificado o assistente segue (homologação instala antes de o contador entregar o .pfx)");
        checar(Bloq(Pronta() with { TemCertificado = false, Ambiente = 1 }) is null,
            "e mesmo em produção o passo não trava — o aviso de 'sem certificado não emite' é do Salvar");
    }

    /// <summary>
    /// A SÉRIE QUE DÁ ERRO (29/08 — pedido do dono: "se der erro mostra o erro por causa
    /// da série e altera para uma que seja possível").
    ///
    /// Dois defeitos, e os dois já custaram caro:
    ///  · o erro chegava CRU ("Duplicidade de NF-e com diferenca na chave de acesso") e
    ///    não dizia que o culpado era a série — nem que dava para trocá-la;
    ///  · e não havia como trocar por uma que funcionasse.
    /// Por isso todo teste aqui olha o PAR (a frase nomeia a série? e o que ela oferece?),
    /// com um cuidado extra: sugestão que não se possa GARANTIR não pode existir — ela
    /// vira a colisão seguinte, descoberta só na venda.
    /// </summary>
    private static void SerieQueDaErro(Action<bool, string> checar)
    {
        static DiagnosticoSerie D(string serie, int? nuvem = null, int? reservada = null,
            int? emissor = null, RecusaFiscal? recusa = null)
            => AssistenteConfig.ConferirSerie(serie, nuvem, reservada, emissor, recusa);

        // ── o que a SEFAZ devolve: quem decide é o CÓDIGO ───────────────────
        checar(AssistenteConfig.RecusaEhDeSerie(539, "Duplicidade de NF-e com diferenca na chave de acesso"),
            "cStat 539 (duplicidade com chave diferente) é problema de série");
        checar(AssistenteConfig.RecusaEhDeSerie(204, "Duplicidade de NF-e"),
            "cStat 204 (duplicidade) também é problema de série");
        // O xMotivo é texto livre, muda de estado para estado e chega truncado por quem
        // repassa. Com o código na mão, adivinhar pelo texto é como se acusa a série por
        // uma rejeição que era de outra coisa.
        checar(!AssistenteConfig.RecusaEhDeSerie(217, "NF-e nao consta na base de dados da SEFAZ (duplicidade?)"),
            "com código na mão, o texto livre NÃO decide: 217 não é série mesmo falando em duplicidade");
        checar(!AssistenteConfig.RecusaEhDeSerie(297, "Assinatura difere do calculado"),
            "rejeição de assinatura não vira acusação contra a série");
        checar(AssistenteConfig.RecusaEhDeSerie(null, "Duplicidade de NF-e"),
            "sem código, o texto é o que há — e 'duplicidade' aponta a série");
        checar(!AssistenteConfig.RecusaEhDeSerie(null, null) && !AssistenteConfig.RecusaEhDeSerie(0, ""),
            "sem código e sem texto não se acusa ninguém");

        // ── série fora da faixa ─────────────────────────────────────────────
        foreach (var ruim in new[] { "0", "1000", "", "  ", "três", "-1" })
        {
            var d = D(ruim);
            checar(d.Nivel == 2 && d.Texto.Contains("1 a 999"),
                $"série '{ruim}' é recusada dizendo qual é a faixa");
        }
        checar(D("1").Nivel == 0 && D("999").Nivel == 0, "1 e 999 são séries boas");

        // ── colisão com a série da NUVEM (prova local, sem esperar a venda) ──
        var colide = D("7", nuvem: 7);
        checar(colide.Nivel == 2 && colide.Texto.Contains("série 7") && colide.Texto.Contains("539"),
            $"série igual à da nuvem é barrada, nomeando a série e a rejeição que viria ({colide.Texto})");
        checar(D("8", nuvem: 7).Nivel == 0, "série diferente da nuvem passa");

        // ── a sugestão: só número GARANTIDO, nunca chute ────────────────────
        var comReserva = D("7", nuvem: 7, reservada: 4);
        checar(comReserva.Sugestao == 4 && comReserva.Texto.Contains("série 4"),
            "havendo série reservada pelo painel, é ELA que a tela oferece");
        var semReserva = D("7", nuvem: 7);
        checar(semReserva.Sugestao is null,
            "sem reserva do painel, NÃO se inventa número — sugestão errada vira a colisão seguinte");
        checar(semReserva.Texto.Contains("Parear") && semReserva.Texto.Contains("painel"),
            $"e o texto ensina onde achar a série certa em vez de deixar o dono parado ({semReserva.Texto})");
        checar(D("7", nuvem: 7, reservada: 7).Sugestao is null,
            "reserva igual à série da nuvem não é oferecida: seria trocar um erro por outro");
        checar(D("7", nuvem: 7, reservada: 1000).Sugestao is null,
            "reserva fora da faixa 1..999 também não é oferecida");

        // ── a recusa que já aconteceu (série tomada por outro caixa) ────────
        var tomada = D("3", recusa: new RecusaFiscal(539, "Duplicidade de NF-e com diferenca na chave de acesso", 3));
        checar(tomada.Nivel == 2 && tomada.Texto.Contains("SÉRIE 3"),
            $"recusa 539 nesta série vira erro que NOMEIA a série ({tomada.Texto})");
        checar(tomada.Texto.Contains("539") && tomada.Texto.Contains("Duplicidade"),
            "e leva junto o código e o motivo da SEFAZ, para quem for atrás com o contador");
        checar(D("4", recusa: new RecusaFiscal(539, "Duplicidade", 3)).Nivel == 0,
            "recusa de OUTRA série não acusa a série que está na tela");
        checar(D("3", recusa: new RecusaFiscal(297, "Assinatura difere do calculado", 3)).Nivel == 0,
            "recusa que não é de série não vira acusação contra a série");
        // Recusa barrada localmente não grava série (numero/serie ficam nulos). Acusar a
        // série por causa dela travaria o Salvar para sempre, sem saída nenhuma.
        checar(D("3", recusa: new RecusaFiscal(539, "Duplicidade", null)).Nivel == 0,
            "recusa sem série gravada não acusa a série da tela (senão o Salvar trava para sempre)");

        // ── quem numera é o EMISSOR, não este campo ─────────────────────────
        // O dono trocou 9→4 aqui e a nota continuou saindo na 3: o campo é rótulo, e o
        // PDV o realinha sozinho com o /health. Calar isso é deixar a troca parecer feita.
        var rotulo = D("4", emissor: 3);
        checar(rotulo.Nivel == 1 && rotulo.Sugestao == 3 && rotulo.Texto.Contains("emissor local"),
            $"série diferente da que o emissor local numera vira aviso com a série real ({rotulo.Texto})");
        checar(rotulo.Texto.Contains("agent-config.json"),
            "e diz onde se muda a série de verdade, em vez de prometer que este campo muda");
        checar(D("3", emissor: 3).Nivel == 0, "série igual à do emissor não avisa nada");
        checar(D("4", emissor: 7, nuvem: 7).Sugestao is null,
            "série do emissor que colide com a da nuvem não é oferecida como saída");
        // Havendo erro de verdade e DUAS fontes, vale a reserva do painel: só ela enxerga
        // os outros caixas da loja; o /health só sabe desta máquina.
        checar(D("7", nuvem: 7, reservada: 4, emissor: 5).Sugestao == 4,
            "com as duas fontes, a sugestão é a reserva do painel");
        checar(D("7", nuvem: 7, emissor: 5).Sugestao == 5,
            "sem reserva, o que o emissor local DE FATO numera também é número garantido");

        // ── e o que a tela faz com isso ─────────────────────────────────────
        string? Fiscal(DadosAssistente d) => AssistenteConfig.Bloqueio(PassoConfig.Fiscal, d);
        var travada = Pronta() with { Serie = "7", SerieNuvem = 7, SerieReservada = 4 };
        checar(Fiscal(travada) is { } m && m.Contains("série 7") && m.Contains("série 4"),
            "o passo 2 trava com a frase inteira: qual série está errada e qual usar");
        checar(AssistenteConfig.PrimeiroBloqueio(travada)?.Passo == PassoConfig.Fiscal,
            "e o Salvar manda o operador para o passo 2, onde está o campo culpado");
        checar(Fiscal(Pronta() with { Serie = "4", SerieEmissorLocal = 3 }) is null,
            "aviso (nível 1) NÃO trava a instalação: rótulo desalinhado não impede vender");
        checar(Fiscal(Pronta() with { SerieNuvem = 9, SerieReservada = 1 }) is null,
            "série boa continua passando com as duas informações novas no bolso");
    }

    private static void PassoImpressora(Action<bool, string> checar)
    {
        // Tudo aqui tem padrão que funciona: impressora do Windows e bobina de 80 mm.
        // Um passo que nunca bloqueia é uma decisão, não um esquecimento.
        checar(AssistenteConfig.Bloqueio(PassoConfig.Impressora,
                   Pronta() with { Impressora = null, ImprimirAuto = false, PapelMm = 58 }) is null,
            "o passo da impressora nunca trava a instalação: sem impressora escolhida, usa a padrão do Windows");

        // A lista de impressoras chega depois da tela. Enquanto não chegou, o combo só
        // tem "(padrão do Windows)" — que grava null, ou seja, APAGA a impressora da
        // loja. Reconfigurando, o Salvar está no rodapé desde o passo 1: abrir e salvar
        // em um segundo é gesto normal, e o cupom passaria a sair noutra impressora sem
        // ninguém ter tocado no campo.
        checar(!AssistenteConfig.PodeGravarImpressora(false, null),
            "com a lista ainda carregando, o Salvar NÃO apaga a impressora já configurada");
        checar(AssistenteConfig.PodeGravarImpressora(true, null),
            "com a lista na tela, escolher \"(padrão do Windows)\" apaga mesmo — aí é escolha do operador");
        checar(AssistenteConfig.PodeGravarImpressora(true, "EPSON TM-T20")
               && AssistenteConfig.PodeGravarImpressora(false, "EPSON TM-T20"),
            "impressora escolhida sempre grava: aí há decisão, com lista ou sem");
    }

    /// <summary>
    /// COMANDA DO DELIVERY em impressora própria (29/08 — pedido do dono: "pode ser q
    /// delivery use uma e cupom fiscal use outra, entao sao configuracoes individuais").
    ///
    /// A regra que não pode quebrar está no primeiro teste: quem NÃO liga a opção segue
    /// imprimindo tudo onde já imprime. Instalar um caixa não pode passar a exigir a
    /// escolha de duas impressoras.
    /// </summary>
    private static void ComandaDoDelivery(Action<bool, string> checar)
    {
        static string Comanda(DadosAssistente d) =>
            AssistenteConfig.Resumo(d).First(l => l.Titulo.Contains("Comanda")).Valor;

        var junto = Pronta() with { ComandaAuto = true, ComandaSeparada = false };
        checar(Comanda(junto).Contains("MESMA impressora do cupom"),
            $"sem a opção ligada, a revisão diz que a comanda sai na mesma impressora do cupom ({Comanda(junto)})");

        var separada = Pronta() with
        {
            ComandaAuto = true, ComandaSeparada = true,
            ImpressoraComanda = "ELGIN I9 COZINHA", ComandaPapelMm = 58,
        };
        checar(Comanda(separada).Contains("ELGIN I9 COZINHA") && Comanda(separada).Contains("58 mm"),
            $"ligada, a revisão diz a impressora E a bobina da comanda ({Comanda(separada)})");
        checar(Comanda(separada).Contains("32 colunas"),
            "e traduz a bobina em colunas, que é o que explica a comanda sair mais comprida");
        checar(Comanda(separada with { ImpressoraComanda = null }).Contains("padrão do Windows"),
            "sem impressora escolhida na opção ligada, a revisão diz que vai na padrão do Windows");

        // As duas larguras são independentes: é o ponto do pedido. Cupom em 80 e comanda
        // em 58 tem que aparecer como duas linhas diferentes na revisão.
        var duasBobinas = separada with { PapelMm = 80 };
        checar(AssistenteConfig.Resumo(duasBobinas).Any(l => l.Titulo.Contains("cupom") && l.Valor.Contains("80 mm"))
               && Comanda(duasBobinas).Contains("58 mm"),
            "cupom em 80 mm e comanda em 58 mm convivem na mesma revisão");

        checar(Comanda(Pronta() with { ComandaAuto = false }).Contains("🖨"),
            "sem impressão automática, a revisão lembra do botão que tira a comanda à mão");

        // E nada disso pode travar a instalação: o passo da impressora nunca bloqueia.
        checar(AssistenteConfig.Bloqueio(PassoConfig.Impressora, separada) is null
               && AssistenteConfig.PrimeiroBloqueio(separada) is null,
            "comanda em impressora e bobina próprias não bloqueia o assistente");
    }

    private static void PassoMaquininha(Action<bool, string> checar)
    {
        string? Bloq(DadosAssistente d) => AssistenteConfig.Bloqueio(PassoConfig.Maquininha, d);

        checar(Bloq(Pronta() with { Tef = 0 }) is null, "\"Sem maquininha\" avança sem pedir nada");
        checar(Bloq(Pronta() with { Tef = 1 }) is null,
            "\"Venda no POS\" avança sem serial (vazio = terminal padrão da conta)");

        // PayGo sem pasta é o caixa que não acha o TEF: a cobrança some no vazio.
        var semPasta = Bloq(Pronta() with { Tef = 2, PayGoPasta = "  " });
        checar(semPasta is not null && semPasta.Contains("PayGo Windows"),
            "PayGo sem pasta não avança, e a frase diz que a pasta é a MESMA do PayGo Windows");
        checar(Bloq(Pronta() with { Tef = 2, PayGoPasta = @"C:\PAYGO" }) is null, "com a pasta, o PayGo avança");

        // ControlPay: os três dados vêm de lugares diferentes do portal, então cada
        // pendência é cobrada sozinha e diz ONDE achar aquele dado.
        var cpay = Pronta() with { Tef = 3, CpayChave = "", CpayPessoa = "", CpayTerminal = "" };
        var semChave = Bloq(cpay);
        checar(semChave is not null && semChave.Contains("chave"), "ControlPay sem chave de integração não avança");
        var semPessoa = Bloq(cpay with { CpayChave = "abc" });
        checar(semPessoa is not null && semPessoa.Contains("pessoa"), "depois da chave, cobra o ID da pessoa");
        var semTerminal = Bloq(cpay with { CpayChave = "abc", CpayPessoa = "12247" });
        checar(semTerminal is not null && semTerminal.Contains("Testar"),
            "sem o ID do terminal, a frase manda usar o botão que LISTA os terminais da conta");
        checar(Bloq(cpay with { CpayChave = "abc", CpayPessoa = "12247", CpayTerminal = "6408" }) is null,
            "com chave, pessoa e terminal o ControlPay avança");

        // Rede vazia é o padrão RECOMENDADO em produção (quem escolhe é a PayGo) —
        // exigir uma seria empurrar a loja para o erro que a lista fechada evita.
        checar(Bloq(Pronta() with { Tef = 2, PayGoPasta = @"C:\PAYGO", PayGoRedeCartao = "", PayGoRedePix = "" }) is null,
            "rede em branco não bloqueia: é assim que a PayGo escolhe o roteamento");
    }

    private static void PassoPareamento(Action<bool, string> checar)
    {
        string? Bloq(DadosAssistente d) => AssistenteConfig.Bloqueio(PassoConfig.Pareamento, d);

        var naoPareado = Bloq(Pronta() with { Pareado = false });
        checar(naoPareado is not null && naoPareado.Contains("6 dígitos"),
            "sem pareamento não conclui, e a frase diz de onde vem o código");
        checar(Bloq(Pronta()) is null, "pareado e sem operador a cadastrar: pode concluir");

        // Primeira instalação: o dono nasce aqui, e o CPF dele é o login no caixa.
        var comAdmin = Pronta() with { PedeAdmin = true, AdminNome = "", AdminCpf = "", AdminPin = "" };
        checar(Bloq(comAdmin) is not null, "primeira instalação exige o nome do administrador");
        checar(Bloq(comAdmin with { AdminNome = "Breno", AdminCpf = "11111111111", AdminPin = "1234" }) is not null,
            "CPF inválido do dono é barrado — é com ele que o dono entra no caixa");
        var pinCurto = Bloq(comAdmin with { AdminNome = "Breno", AdminCpf = CpfOk, AdminPin = "12" });
        checar(pinCurto is not null && pinCurto.Contains("4 a 6"),
            "senha de 2 dígitos não passa, e a frase diz o tamanho certo");
        checar(Bloq(comAdmin with { AdminNome = "Breno", AdminCpf = CpfOk, AdminPin = "1234" }) is null,
            "nome + CPF válido + senha de 4 dígitos conclui a primeira instalação");
    }

    /// <summary>
    /// O Salvar é a porta única de escrita e pergunta ao assistente antes de tocar no
    /// banco. Reconfigurando dá pra pular direto pro passo 4 — o que falta pode estar
    /// num passo que nem foi aberto, e a tela precisa saber PARA ONDE pular.
    /// </summary>
    private static void PortaUnica(Action<bool, string> checar)
    {
        checar(AssistenteConfig.PrimeiroBloqueio(Pronta()) is null,
            "instalação completa não tem nada bloqueando: o Salvar grava");

        var b = AssistenteConfig.PrimeiroBloqueio(Pronta() with { Pareado = false });
        checar(b?.Passo == PassoConfig.Pareamento,
            "faltando só o pareamento, o Salvar aponta o passo 5 — não um erro genérico no rodapé");

        // Ordem importa: com dois passos tortos, o assistente manda para o PRIMEIRO.
        // Mandar para o último faria o operador consertar de trás pra frente.
        var doisErros = AssistenteConfig.PrimeiroBloqueio(Pronta() with { Loja = "", Pareado = false });
        checar(doisErros?.Passo == PassoConfig.Loja,
            "com dois passos pendentes, vai para o primeiro deles (passo 1), não para o último");

        var cpaySemChave = AssistenteConfig.PrimeiroBloqueio(
            Pronta() with { Tef = 3, CpayChave = "", CpayPessoa = "", CpayTerminal = "" });
        checar(cpaySemChave?.Passo == PassoConfig.Maquininha,
            "maquininha pela metade também impede o Salvar, e o passo apontado é o 4");

        // Na tela de revisão o bloqueio precisa NOMEAR o passo culpado: ali não há
        // campo nenhum à vista, só a lista do que ficou configurado.
        var naRevisao = AssistenteConfig.Bloqueio(PassoConfig.Resumo, Pronta() with { Pareado = false });
        checar(naRevisao is not null && naRevisao.Contains("Pareamento"),
            $"na revisão, o que falta vem com o NOME do passo ({naRevisao})");
        checar(AssistenteConfig.Bloqueio(PassoConfig.Resumo, Pronta()) is null,
            "revisão sem pendência libera o botão de gravar");
    }

    private static void TelaDeResumo(Action<bool, string> checar)
    {
        static string Tudo(DadosAssistente d) =>
            string.Join(" | ", AssistenteConfig.Resumo(d).Select(l => l.Titulo + ": " + l.Valor));
        static bool Chama(DadosAssistente d, string trecho) =>
            AssistenteConfig.Resumo(d).Any(l => l.Valor.Contains(trecho, StringComparison.OrdinalIgnoreCase));
        static bool Atencao(DadosAssistente d, string trecho) =>
            AssistenteConfig.Resumo(d).Any(l => l.Atencao && l.Valor.Contains(trecho, StringComparison.OrdinalIgnoreCase));

        var completa = AssistenteConfig.Resumo(Pronta());
        checar(completa.Count >= 6, $"a revisão cobre os cinco assuntos e a cozinha ({completa.Count} linhas)");
        checar(completa.All(l => l.Valor.Trim().Length > 0), "nenhuma linha da revisão sai vazia");
        checar(!completa.Any(l => l.Atencao),
            $"instalação completa e em produção não pisca atenção nenhuma ({Tudo(Pronta())})");

        // As três escolhas que somem depois de gravadas e custam caro: precisam estar
        // escritas E marcadas na revisão.
        checar(Atencao(Pronta() with { Recibo = true }, "SEM VALOR FISCAL"),
            "só recibo aparece marcado: é a loja operando sem emitir nota");
        // A revisão diz "MODO TESTE" (a mesma palavra do rodapé do caixa), não
        // "HOMOLOGAÇÃO": o que importa é que a nota emitida assim não vale.
        checar(Atencao(Pronta() with { Ambiente = 2 }, "MODO TESTE")
               && Atencao(Pronta() with { Ambiente = 2 }, "não valem"),
            "ambiente de teste aparece marcado — nota emitida assim não vale nada");
        checar(Atencao(Pronta() with { Tef = 3, CpayTerminal = "6408", CpaySandbox = true }, "TESTE"),
            "sandbox do ControlPay aparece marcado: nenhuma cobrança ali é de verdade");
        checar(Atencao(Pronta() with { Pareado = false }, "pareado"), "falta de pareamento aparece marcada");
        checar(Atencao(Pronta() with { ImprimirAuto = false }, "DESLIGADA"),
            "impressão automática desligada é escolha rara o bastante para ser lembrada");

        // A revisão fala do que o operador ESCOLHEU, com as palavras dele.
        checar(Chama(Pronta() with { PapelMm = 58 }, "58 mm") && Chama(Pronta() with { PapelMm = 58 }, "32 colunas"),
            "a bobina escolhida aparece em mm E em colunas");
        checar(Chama(Pronta() with { Impressora = null }, "padrão do Windows"),
            "sem impressora escolhida, a revisão diz que vai na padrão do Windows");
        checar(Chama(Pronta() with { Tef = 2, PayGoPasta = @"C:\PAYGO" }, @"C:\PAYGO"),
            "a pasta do PayGo aparece na revisão — é o campo que mais é digitado errado");
        checar(Chama(Pronta() with { Tef = 2, PayGoPasta = @"C:\PAYGO", PayGoRedePix = "PIX ITAU" }, "PIX ITAU")
               && Chama(Pronta() with { Tef = 2, PayGoPasta = @"C:\PAYGO" }, "a PayGo escolhe"),
            "a rede do PIX aparece, e em branco a revisão diz que quem escolhe é a PayGo");

        // O texto antigo do cartão dizia que "sem TEF" era "cartão registrado na mão",
        // o que está ERRADO: a forma cartão continua existindo no PDV; o que não existe
        // é a cobrança sair daqui.
        checar(!Chama(Pronta() with { Tef = 0 }, "na mão"),
            "\"Sem maquininha\" não é \"cartão registrado na mão\" — o texto errado saiu");
        checar(Chama(Pronta() with { Tef = 0 }, "não cobra"),
            "e a revisão explica o que realmente muda: o caixa registra a forma, mas não cobra o cartão");
    }

    private static void LarguraDoPapel(Action<bool, string> checar)
    {
        var ops = AssistenteConfig.OpcoesPapel();
        checar(ops.Select(o => o.Mm).SequenceEqual(Pdv.Impressao.BobinasSuportadas),
            "o combo oferece exatamente as bobinas que a impressão sabe montar");
        checar(ops.All(o => o.ToString().Contains("mm") && o.ToString().Contains("colunas")),
            "cada opção traduz milímetros em colunas — '58 mm' sozinho não diz nada ao dono");

        // SelectedIndex nunca pode ser -1: caixa vazia se lê como "nada escolhido", e o
        // que a impressão faz sem escolha é imprimir em 80 mm.
        var padrao = AssistenteConfig.IndicePapel(null);
        checar(ops[padrao].Mm == Pdv.Impressao.PapelPadrao.BobinaMm,
            "sem nada gravado, o combo já vem em 80 mm (que é o que a impressão faz)");
        checar(ops[AssistenteConfig.IndicePapel("58")].Mm == 58,
            "papel_mm = 58 seleciona a bobina de 58 mm");
        checar(ops[AssistenteConfig.IndicePapel("58,0")].Mm == 58,
            "valor gravado à mão com vírgula seleciona a mesma bobina");
        checar(AssistenteConfig.IndicePapel("76") == padrao && AssistenteConfig.IndicePapel("lixo") == padrao,
            "bobina desconhecida cai no 80 mm em vez de deixar a caixa vazia");

        checar(AssistenteConfig.TextoPapel(58) == "58" && AssistenteConfig.TextoPapel(80) == "80",
            "o que vai para config['papel_mm'] é o número puro, sem vírgula (é lido por Impressao.Papel.De)");
    }
}
