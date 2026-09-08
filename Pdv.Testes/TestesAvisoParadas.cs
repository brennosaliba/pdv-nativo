using Dapper;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O AVISO DE VENDA PARADA NÃO PODE MANDAR FAZER O QUE NÃO ADIANTA.
///
/// O QUE ACONTECEU (08/09/2026). O dono leu na tela do caixa um bloco de onze linhas
/// dizendo que 8 vendas de R$ 100.089,00 não tinham subido, e no fim: "chame o gerente
/// para resolver esse motivo no painel e, depois, toque em Sincronizar". Ele reclamou do
/// tamanho — "pessimo, tanto de clareza quanto de quantidade de informacao" — e o
/// tamanho era o menor dos problemas.
///
/// As 8 vendas eram de teste desta máquina, de 21/08, e o painel as recusava com
/// 22P02: o registro leva `pdv_product_id = "saas-teste-6d29aca8-…"` onde o servidor
/// espera um identificador de verdade. O texto está GRAVADO na fila. Nenhum cadastro
/// que o gerente faça no painel muda uma letra dele, e cada toque em Sincronizar
/// reabria as 8, gastava 8 chamadas que não podiam dar certo e as devolvia ao mesmo
/// estado com o contador maior. O aviso não era só feio: era um moedor.
///
/// As regras que este arquivo vigia:
///  1. o caixa SEPARA a recusa que gente conserta da que não tem conserto;
///  2. o aviso do caso sem conserto não manda tocar em Sincronizar;
///  3. o reenvio manual NÃO reabre o que não tem conserto;
///  4. existe uma saída de verdade (dispensar), e ela zera o aviso;
///  5. o aviso cabe na tela: no máximo 4 linhas, sem código de erro e sem gritar.
/// </summary>
public static class TestesAvisoParadas
{
    /// <summary>O rastro REAL das 8 vendas paradas nesta máquina, byte a byte.</summary>
    private const string Rastro22P02 =
        "desistido após 19 tentativas — HTTP 400: {\"code\":\"22P02\",\"details\":null,\"hint\":null,"
        + "\"message\":\"invalid input syntax for type uuid: \\\"saas-teste-6d29aca8-4365-5ae5-bc7c-95370fafd129\\\"\"}";

    /// <summary>O rastro real do caixa da Savassi: operador que não existe em employees.</summary>
    private const string RastroOperador =
        """desistido após 12 tentativas — HTTP 409: {"code":"23503","details":"Key """
        + """(operator_id)=(003e0aa7-99e2-4453-98ea-8cb129a4b0e9) is not present in table \"employees\".""";

    private const string RastroSemRede = "desistido: 8 dias falhando sem conseguir enviar";

    public static void Rodar(Action<bool, string> checar)
    {
        // ── 1. O CAIXA SABE A DIFERENÇA ─────────────────────────────────────
        checar(Sincronizacao.SaidaDoErro(Rastro22P02) == SaidaParada.SemConserto,
            $"22P02 é recusa sem conserto (viu {Sincronizacao.SaidaDoErro(Rastro22P02)})");
        checar(Sincronizacao.SaidaDoErro(RastroOperador) == SaidaParada.Resolver,
            $"operador ausente no painel é coisa que gente resolve (viu {Sincronizacao.SaidaDoErro(RastroOperador)})");
        checar(Sincronizacao.SaidaDoErro(RastroSemRede) == SaidaParada.Espera,
            $"dias sem rede é esperar, não chamar gerente (viu {Sincronizacao.SaidaDoErro(RastroSemRede)})");

        // O motivo em português não pode ser código de erro. Hoje o 22P02 caía no
        // fallback "o painel recusou o envio (HTTP 400)" e o dono lia isso no balcão.
        var motivo = Sincronizacao.MotivoHumano(Rastro22P02) ?? "";
        checar(!motivo.Contains("HTTP", StringComparison.OrdinalIgnoreCase)
               && !motivo.Contains("22P02", StringComparison.Ordinal)
               && !motivo.Contains("uuid", StringComparison.OrdinalIgnoreCase),
            $"o 22P02 vira frase de gente, sem código de erro (viu: {motivo})");

        // ── 2. O QUE NÃO TEM CONSERTO NÃO VAI PARA A TELA DO CAIXA ──────────
        // O dono leu a primeira versão deste aviso e perguntou, sobre cada linha:
        // "o que isso quer dizer? qual impacto pro funcionário? é necessário?" e,
        // sobre a saída pela Configuração, "totalmente sem sentido". Está certo: o
        // operador não pode consertar, não entra na Configuração (é senha de
        // administrador) e não faz nada com o número. Recado sem ação é ruído, e
        // ruído é o que ensina a ignorar o próximo recado.
        var semConserto = new VendasParadas(
            Aguardando: 0, Desistidas: 8, Valor: new Dinheiro(10008900),
            ValorParado: new Dinheiro(10008900), Motivo: motivo,
            Lista: Enumerable.Range(1, 8).Select(n => new VendaParada(n, "2026-08-21", true)).ToList(),
            Saida: SaidaParada.SemConserto);
        checar(semConserto.Resumo is null,
            $"o que não tem conserto NÃO aparece na tela do caixa (viu: {semConserto.Resumo})");

        // ── 3. TAMANHO E TOM ────────────────────────────────────────────────
        // 11 linhas foi o que o dono reprovou. O teto entra aqui para não voltar
        // em silêncio na próxima vez que alguém tiver uma boa ideia.
        foreach (var (nome, v) in Casos(motivo))
        {
            var t = v.Resumo ?? "";
            var linhas = t.Split('\n').Length;
            checar(linhas <= 2, $"{nome}: cabe em 2 linhas (viu {linhas})");
            checar(!System.Text.RegularExpressions.Regex.IsMatch(t, @"HTTP \d{3}"),
                $"{nome}: nenhum código de erro na tela");
            checar(!t.Contains("—", StringComparison.Ordinal),
                $"{nome}: nenhum travessão (o dono lê como texto de robô)");
            checar(!System.Text.RegularExpressions.Regex.IsMatch(t, @"\b[A-ZÀ-Ú]{4,}\b"),
                $"{nome}: nenhuma palavra gritando em caixa alta (viu: {t})");
            // A garantia agora está na PALAVRA, não numa linha extra: o que não subiu
            // é o REGISTRO. Dizer "3 vendas não subiram" faz o operador entender que
            // três vendas se perderam, e no susto ele cancela venda certa.
            checar(t.StartsWith("O registro de", StringComparison.Ordinal),
                $"{nome}: a primeira linha diz que o que ficou para trás é o registro (viu: {t.Split('\n')[0]})");
            var primeiroReal = t.IndexOf("R$", StringComparison.Ordinal);
            checar(primeiroReal < 0 || t.IndexOf("registro", StringComparison.Ordinal) < primeiroReal,
                $"{nome}: isso vem antes do primeiro valor");
            checar(!t.Contains("Configuração", StringComparison.Ordinal),
                $"{nome}: o caixa nunca é mandado para uma tela que ele não abre (viu: {t})");
        }

        // O valor do que está TRAVADO não pode englobar o que sobe sozinho: misturar
        // os dois faz o dono achar que R$ 100 mil precisam de gente quando só R$ 24,00
        // precisam.
        var misto = new VendasParadas(
            Aguardando: 3, Desistidas: 2, Valor: new Dinheiro(17250), ValorParado: new Dinheiro(2400),
            Motivo: "o operador que fez a venda não está cadastrado lá", Lista: null,
            Saida: SaidaParada.Resolver);
        var textoMisto = misto.Resumo ?? "";
        checar(textoMisto.Contains("24,00", StringComparison.Ordinal)
               && !textoMisto.Contains("172,50", StringComparison.Ordinal),
            $"no caso misto o aviso mostra o que TRAVOU, não a soma com as que sobem sozinhas (viu: {textoMisto})");

        // ── 4. O REENVIO MANUAL NÃO PODE MOER ───────────────────────────────
        var arquivo = Path.Combine(Path.GetTempPath(), $"aviso_paradas_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir(arquivo);

            void Morta(string id, string erro) => cx.Execute("""
                INSERT INTO outbox (tipo, ref_id, client_key, payload, tentativas, ultimo_erro,
                                    criado_em, desistido_em)
                VALUES ('venda', @Id, @Id, '{}', 19, @E, @Agora, @Agora)
                """, new { Id = id, E = erro, Agora = DateTime.Now.ToString("o") });

            Morta("v-sem-conserto", Rastro22P02);
            Morta("v-do-gerente", RastroOperador);

            Drenagem.ReabrirDesistidas();

            var aindaMorta = cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM outbox WHERE ref_id = 'v-sem-conserto' AND desistido_em IS NOT NULL");
            var reaberta = cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM outbox WHERE ref_id = 'v-do-gerente' AND desistido_em IS NULL");
            checar(aindaMorta == 1,
                "o toque em Sincronizar NÃO reabre a linha sem conserto (nada de gastar chamada que não pode dar certo)");
            checar(reaberta == 1,
                "…e continua reabrindo a que o gerente pode ter resolvido");

            // ── 4b. O CAIXA FICA MUDO SOBRE ELA ─────────────────────────────
            // Não basta o texto sumir: o número no balão do botão Sincronizar também
            // some, senão o operador continua vendo "1" e perguntando o que é.
            var vendaMorta = Guid.NewGuid().ToString();
            Operadores.Salvar(cx, "op", "Bia", "4321", "operador");
            cx.Execute("""
                INSERT INTO caixa_sessao (id, business_date, operador_id, operador_nome,
                                          abertura_em, fundo_troco_cent, status)
                VALUES ('s-morta', '2026-08-21', 'op', 'Bia', @Agora, 0, 'fechado')
                """, new { Agora = DateTime.Now.ToString("o") });
            cx.Execute("""
                INSERT INTO venda (id, client_key, sessao_id, business_date, numero_local, operador_id,
                                   subtotal_cent, total_cent, status, homologacao, criada_em)
                VALUES (@Id, @Id, 's-morta', '2026-08-21', 77, 'op', 10000000, 10000000, 'finalizada', 0, @Agora)
                """, new { Id = vendaMorta, Agora = DateTime.Now.ToString("o") });
            cx.Execute("""
                INSERT INTO outbox (tipo, ref_id, client_key, payload, tentativas, ultimo_erro,
                                    criado_em, desistido_em)
                VALUES ('venda', @Id, @Id, @P, 19, @E, @Agora, @Agora)
                """, new
            {
                Id = vendaMorta, E = Rastro22P02, Agora = DateTime.Now.ToString("o"),
                P = "{\"p_itens\":[{\"descricao\":\"P02 VALOR MAXIMO\",\"codigo\":\"900\"}]}",
            });

            var paradas = Sincronizacao.VendasNaoEntregues();
            checar(paradas.Total == 0 && paradas.Resumo is null,
                $"a venda que o painel nunca vai aceitar não conta no aviso nem no balão (viu {paradas.Total})");

            // …e aparece inteira para quem abre a Configuração, com nome e produto:
            // "que produto?" foi a pergunta do dono, e o aviso não respondia.
            var travadas = Sincronizacao.Travadas();
            checar(travadas is { Quantas: 1 } && travadas.Valor.Centavos == 10000000,
                $"a Configuração vê quantas são e quanto somam (viu {travadas?.Quantas.ToString() ?? "<nulo>"})");
            checar(travadas!.Vendas.Any(x => x.Contains("77", StringComparison.Ordinal)
                                             && x.Contains("21/08", StringComparison.Ordinal)),
                $"…com o número da venda e o dia (viu: {string.Join(" | ", travadas.Vendas)})");
            checar(travadas.Produtos.Contains("P02 VALOR MAXIMO"),
                $"…e o nome do produto que o painel recusou (viu: {string.Join(" | ", travadas.Produtos)})");

            // ── 5. EXISTE SAÍDA ─────────────────────────────────────────────
            var quantas = Sincronizacao.Dispensar();
            checar(quantas == 2, $"dispensar tira da fila só o que não tem conserto (viu {quantas})");
            checar(cx.ExecuteScalar<int>(
                       "SELECT COUNT(*) FROM outbox WHERE ref_id = 'v-sem-conserto' AND descartado_em IS NOT NULL") == 1,
                "a linha dispensada fica marcada, não apagada (o histórico continua lá)");
            checar(cx.ExecuteScalar<int>(
                       "SELECT COUNT(*) FROM auditoria WHERE evento = 'outbox_dispensado'") >= 1,
                "…e a auditoria registra quem tirou o quê");
            checar(cx.ExecuteScalar<int>(
                       "SELECT COUNT(*) FROM outbox WHERE ref_id = 'v-do-gerente' AND descartado_em IS NULL") == 1,
                "dispensar não encosta no que ainda pode subir");

            // Dispensada não volta pelo reenvio manual: senão o aviso ressuscita.
            Drenagem.ReabrirDesistidas();
            checar(cx.ExecuteScalar<int>(
                       "SELECT COUNT(*) FROM outbox WHERE ref_id = 'v-sem-conserto' AND descartado_em IS NOT NULL") == 1,
                "o que foi dispensado não volta no próximo toque em Sincronizar");
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            try { File.Delete(arquivo); } catch { }
        }
    }

    private static IEnumerable<(string Nome, VendasParadas V)> Casos(string motivo)
    {
        var lista = new[] { new VendaParada(41, "2026-09-08", true), new VendaParada(42, "2026-09-08", true) };
        yield return ("só na fila", new VendasParadas(3, 0, new Dinheiro(14850), Dinheiro.Zero, null, null, SaidaParada.Sozinha));
        yield return ("gerente resolve", new VendasParadas(0, 2, new Dinheiro(14850), new Dinheiro(14850),
            "o operador que fez a venda não está cadastrado lá", lista, SaidaParada.Resolver));
        yield return ("sem rede", new VendasParadas(0, 2, new Dinheiro(14850), new Dinheiro(14850),
            "ficou dias sem conseguir falar com o painel", lista, SaidaParada.Espera));
        // O caso sem conserto não entra: ele não produz texto nenhum para o caixa.
    }
}
