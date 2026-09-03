using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

// ════════════════════════════════════════════════════════════════════════════
//  AUTORIZAÇÃO DE ESTORNO — token de 6 dígitos por WhatsApp, PIN como saída
//
//  Antes: o estorno de cartão/PIX pedia o PIN do supervisor numa tela local. O
//  PIN mora no banco DESTE caixa: quem opera e quem autoriza podiam ser a mesma
//  pessoa, e a nuvem nunca ficava sabendo. Agora o caminho normal é um código de
//  6 dígitos que a edge `pdv-autorizacao` manda no WhatsApp da gerente geral e
//  do dono — vale o primeiro que responder, qualquer valor.
//
//  O PIN CONTINUA VALENDO COMO SAÍDA (decisão do dono, não reabrir): se a edge
//  não responder em ~15 s, o caixa cai para o PIN e a auditoria grava um evento
//  DISTINTO (<see cref="Autorizacao.EventoSemAprovacaoRemota"/>), para o dono
//  conseguir listar depois quais estornos escaparam do token. Cliente no balcão
//  não pode ficar sem estorno porque a internet caiu.
//
//  POR QUE ISTO VIVE NO NÚCLEO E NÃO NO CODE-BEHIND DA TELA: é uma máquina de
//  estados com dinheiro no fim (timeout, token queimado, referência trocada,
//  desistência, PIN recusado). Dentro de um .xaml.cs ela só seria exercitada por
//  gente clicando. Aqui a suíte roda todos os caminhos sem abrir janela: a tela
//  entra por ITelaAutorizacao e a nuvem por IAutorizacaoRemota.
//
//  A CHAVE USADA É A PÚBLICA (Nuvem.AnonKey), a mesma que qualquer um extrai do
//  .exe da loja. Ela é EXIGIDA (a edge roda com verify_jwt = true, e quem confere
//  a assinatura é a plataforma), mas não é segredo: quem se defende de verdade é
//  a edge — rate limit em dois baldes estanques, código que nunca volta no corpo
//  da resposta e máquina de estados numa RPC com FOR UPDATE.
//
//  Por que "dois baldes", e por que isso importa DESTE lado: até 24/08/2026 o
//  limite era contado por `terminal` — uma string que o CHAMADOR escolhe. Quem
//  trocasse o nome a cada pedido ganhava um balde novo, estourava o teto global
//  compartilhado, e os caixas de verdade passavam a receber 429. Como 429 é
//  veredito DEFINITIVO aqui (ver MotivoDaSolicitacao), alguém de fora da loja
//  escolhia a hora em que a rede inteira voltava a estornar só com o PIN. Agora
//  o balde vem do cadastro (pdv_terminais), e o tráfego desconhecido tem um
//  balde apertado que é só dele.
//
//  Contrato da edge: supabase/functions/pdv-autorizacao/index.ts
//  Banco:            supabase/migrations/20260824170000_pdv_autorizacao_token.sql
//  Testes:           Pdv.Testes/TestesAutorizacao.cs (contra FakeAutorizacao)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>Por onde o estorno passou. Vira linha de auditoria — não é enfeite de UI.</summary>
public enum ViaAutorizacao
{
    /// <summary>Ninguém autorizou: operador desistiu, PIN recusado ou token morto sem saída.</summary>
    Recusada,
    /// <summary>
    /// Histórico. O modo de homologação foi removido quando a operação começou
    /// (ele autorizava sem PIN e sem token). O valor fica para LER auditoria
    /// antiga — nada mais o produz.
    /// </summary>
    Homologacao,
    /// <summary>Aprovado remotamente por quem recebeu o código no WhatsApp.</summary>
    Token,
    /// <summary>Saída de emergência: PIN do supervisor, SEM aprovação remota.</summary>
    Pin,
}

/// <summary>O que o operador escolheu na tela do código.</summary>
public enum AcaoCodigo { Confirmar, NovoCodigo, Pin, Cancelar }

/// <summary>Saída quando o token morreu (queimado/expirado/usado) — o operador nunca fica preso.</summary>
public enum EscolhaAposFalha { NovoCodigo, Pin, Desistir }

public sealed record RespostaCodigo(AcaoCodigo Acao, string? Codigo);

/// <summary>Para quem o código foi — nunca o código, e nem o número inteiro.</summary>
public sealed record DestinatarioAutorizacao(string Nome, string Scope, string Telefone, bool Enviado);

/// <summary>
/// Resposta do `solicitar`. <see cref="Definitiva"/> é a distinção que decide o
/// fluxo: com JSON na mão (inclusive 429/502) o PDV já sabe o veredito e cai
/// para o PIN AGORA; só timeout e erro de rede são "não sei".
/// </summary>
public sealed record RespostaSolicitacao(
    bool Ok, bool Definitiva, string? Motivo, string? Id, DateTime? ExpiraEm,
    int ValidadeSegundos, int MaxTentativas, int Entregues,
    IReadOnlyList<DestinatarioAutorizacao> Destinatarios)
{
    /// <summary>
    /// A nuvem devolveu um token que JÁ EXISTIA — o código que está no celular
    /// de quem aprova é o de antes e NÃO saiu mensagem nova. A tela precisa
    /// saber: senão o operador fica esperando um WhatsApp que não vem.
    ///
    /// É o desfecho normal da segunda tentativa do mesmo estorno depois de a edge
    /// ter demorado mais que os 15 s — ver o bloco "TOKEN FANTASMA" acima.
    /// </summary>
    public bool Reaproveitado { get; init; }
}

public sealed record RespostaValidacao(
    bool Ok, bool Definitiva, string? Motivo, string? AprovadoPor, string? Referencia, long ValorCent);

/// <summary>
/// O que a edge precisa para a pessoa decidir sem ligar para a loja. `Referencia`
/// é o que amarra o token AQUELE estorno — ver <see cref="Autorizacao.Referencia"/>.
/// </summary>
public sealed record PedidoAutorizacao(
    string Terminal, string Referencia, long ValorCent,
    string? Loja = null, string? Operador = null, string? Venda = null,
    string? Forma = null, string? Nsu = null, string? Bandeira = null)
{
    /// <summary>
    /// O que está sendo autorizado: "estorno" (dinheiro de volta) ou
    /// "configuracao" (abrir a tela que muda série fiscal, ambiente e TEF).
    /// A nuvem usa isto para escrever a mensagem certa no WhatsApp — quem
    /// aprova precisa saber o que está aprovando, senão vira carimbo.
    /// </summary>
    public string Tipo { get; init; } = "estorno";
}

/// <summary>A nuvem. Implementação real: <see cref="ClienteAutorizacao"/>.</summary>
public interface IAutorizacaoRemota
{
    /// <param name="reenviar">
    /// true SÓ quando o operador apertou "não recebi": a nuvem queima o token
    /// anterior daquele estorno, sorteia códigos novos e manda outro WhatsApp.
    ///
    /// Com false (o caminho normal) o `solicitar` é IDEMPOTENTE por
    /// (terminal, referência) enquanto o token estiver vivo: a segunda tentativa
    /// do mesmo estorno recebe o token que já existe, sem gastar outra vaga do
    /// rate limit e sem acender o celular de quem aprova de novo. É o que
    /// transforma o token fantasma (criado depois de o caixa cair para o PIN) no
    /// token daquele estorno, em vez de desperdício.
    /// </param>
    Task<RespostaSolicitacao> SolicitarAsync(PedidoAutorizacao pedido, CancellationToken ct,
        bool reenviar = false);
    /// <summary>`referencia` vai junto de propósito: é a amarra do token AQUELE estorno.</summary>
    Task<RespostaValidacao> ValidarAsync(string id, string codigo, string? referencia, CancellationToken ct);
}

/// <summary>
/// A tela. Tudo que precisa de janela sai por aqui, para a máquina de estados
/// poder ser exercitada sem WPF.
/// </summary>
public interface ITelaAutorizacao
{
    /// <summary>Aviso de espera ("Enviando pedido…"). O Dispose fecha.</summary>
    IDisposable Aguardando(string mensagem);
    Task<RespostaCodigo> PedirCodigoAsync(RespostaSolicitacao pedido, string? aviso);
    Task<EscolhaAposFalha> EscolherAposFalhaAsync(string mensagem);
    /// <summary>PIN do supervisor. null = não autorizado (a tela já avisou).</summary>
    Task<Operador?> PedirPinAsync(string motivo);
}

/// <summary>
/// Como o estorno foi autorizado. É o que a auditoria grava — por isso guarda
/// QUEM aprovou e por qual caminho, não só um bool.
/// </summary>
public sealed record DesfechoAutorizacao(
    ViaAutorizacao Via, Operador? Supervisor, string? AprovadoPor, string? TokenId, string Motivo)
{
    public bool Autorizado => Via != ViaAutorizacao.Recusada;

    /// <summary>
    /// A tela já explicou ao operador por que não seguiu (ele cancelou, ou o PIN
    /// não conferiu e o aviso apareceu). Sem isto o caixa levaria dois avisos
    /// seguidos dizendo a mesma coisa — e o operador aprende a fechar sem ler.
    /// </summary>
    public bool Avisado { get; init; }

    /// <summary>O estorno saiu sem que ninguém de fora da loja aprovasse.</summary>
    public bool SemAprovacaoRemota => Via is ViaAutorizacao.Pin or ViaAutorizacao.Homologacao;

    /// <summary>
    /// O que vai na coluna `autorizador` da auditoria. No caminho do token não
    /// existe operador local que assine: entra uma marca sintética com o id do
    /// token, e o nome de quem aprovou vai no detalhe. (A coluna é TEXT livre e
    /// não sai daqui — `Vendas.Cancelar` só manda `por` para a nuvem, e um id
    /// inventado na fila de sincronização é justamente o incidente das 16 vendas
    /// recusadas com 409.)
    /// </summary>
    public string? Autorizador => Via == ViaAutorizacao.Token && TokenId is { Length: > 0 }
        ? "remoto:" + TokenId[..Math.Min(8, TokenId.Length)]
        : Supervisor?.Id;
}

public static class Autorizacao
{
    /// <summary>
    /// Evento DISTINTO do `tef_estorno` de sempre: é a lista que o dono precisa
    /// conseguir puxar — "quais estornos escaparam do token". Vem JUNTO com a
    /// linha normal, nunca no lugar dela (relatório de dinheiro não perde linha).
    /// </summary>
    public const string EventoSemAprovacaoRemota = "tef_estorno_sem_aprovacao_remota";

    /// <summary>
    /// Tipo da linha no outbox. A auditoria mora no SQLite DAQUELE caixa e nenhuma
    /// tela do PDV a lê: sem passar pela fila, a lista do dono só existe indo até a
    /// loja com o SQLite na mão. Ver <see cref="AuditarSemAprovacaoRemota"/>.
    /// </summary>
    public const string TipoNaFila = "estorno_sem_aprovacao";

    /// <summary>Quantos códigos o operador pode pedir antes de sobrar só o PIN (a edge deixa 5 em 10 min).</summary>
    public const int MaxSolicitacoes = 3;

    /// <summary>
    /// AMARRA O TOKEN AQUELE ESTORNO. Se o operador desistir e estornar OUTRA
    /// venda, o token anterior não pode servir — por isso entram os quatro dados
    /// que identificam a transação, e não só o NSU: NSU é contador curto e
    /// repete entre dias e redes (é o mesmo motivo pelo qual a lista de estorno
    /// casa NSU só dentro do turno, por valor e forma).
    ///
    /// A edge corta `referencia` em 200 caracteres; isto aqui fica em ~130 no
    /// pior caso, então os dois lados comparam exatamente a mesma string.
    /// </summary>
    public static string Referencia(string tefId, string nsu, long valorCent, long numeroVenda)
    {
        static string Limpo(string? s, int max)
            => new((s ?? "").Where(char.IsLetterOrDigit).Take(max).ToArray());
        return $"estorno:{Limpo(tefId, 48)}:v{numeroVenda}:nsu{Limpo(nsu, 24)}:c{valorCent}";
    }

    /// <summary>
    /// AMARRA O TOKEN ÀQUELE CANCELAMENTO DE VENDA. Prefixo próprio, e não o de
    /// estorno, porque são atos diferentes: um código aprovado para cancelar a
    /// venda #12 não pode servir para devolver dinheiro no cartão — e a conferência
    /// de referência (aqui e na edge) é o que impede isso.
    ///
    /// Aqui não existe NSU nem transação de TEF (cancelar venda e cancelar nota não
    /// passam pela maquininha), então o que identifica é a venda: id, número e
    /// valor. Fica em ~90 caracteres, bem abaixo dos 200 que a edge corta.
    /// </summary>
    public static string ReferenciaCancelamento(string vendaId, long numeroVenda, long valorCent)
    {
        static string Limpo(string? s, int max)
            => new((s ?? "").Where(char.IsLetterOrDigit).Take(max).ToArray());
        return $"cancelamento:{Limpo(vendaId, 48)}:v{numeroVenda}:c{valorCent}";
    }

    /// <summary>Nome com que este caixa se apresenta à nuvem (cadastro `pdv_terminais.nome`).</summary>
    public static string NomeDoTerminal(SqliteConnection cx)
    {
        // MachineName e não loja_nome: duas máquinas na mesma loja são dois caixas,
        // e o rate limit da edge é POR TERMINAL. Se o caixa não estiver no cadastro
        // de pareados, a mensagem do WhatsApp avisa quem aprova — nunca bloqueia.
        var config = Vendas.Config(cx, "terminal_nome");
        return string.IsNullOrWhiteSpace(config) ? Environment.MachineName : config!.Trim();
    }

    /// <summary>
    /// Sufixo da linha de auditoria do estorno: quem aprovou, ou o aviso em
    /// maiúsculas de que ninguém de fora aprovou.
    /// </summary>
    public static string Trilha(DesfechoAutorizacao d) => d.Via switch
    {
        ViaAutorizacao.Token => $" · autorizado por {d.AprovadoPor} (token {Curto(d.TokenId)})",
        ViaAutorizacao.Pin => $" · SEM APROVAÇÃO REMOTA ({d.Motivo}); liberado pelo PIN do supervisor",
        ViaAutorizacao.Homologacao => " · SEM APROVAÇÃO REMOTA (modo homologação)",
        _ => "",
    };

    /// <summary>
    /// A linha que o dono vai procurar. Só grava quando o estorno realmente
    /// escapou do token — e o chamador só chama com o estorno CONSUMADO, senão a
    /// lista encheria de estorno que nem aconteceu.
    ///
    /// GRAVA EM DOIS LUGARES, NA MESMA TRANSAÇÃO — e o segundo é o que faz a
    /// promessa valer. A tabela `auditoria` mora no SQLite DAQUELE caixa
    /// (C:/ProgramData/PdvNativo/pdv.db), nenhuma tela do PDV a lê e NADA a
    /// sincroniza: quem sobe para a nuvem é o outbox. Sem a linha na fila, "o
    /// dono consegue listar os estornos que escaparam do token" só se cumpre se
    /// ele for até a loja, abrir o banco do caixa certo e rodar SQL na mão — e
    /// o cenário que interessa é justamente aquele em que a internet caiu, que
    /// é quando o caixa está mais longe de qualquer painel.
    ///
    /// E a nuvem não reconstrói essa lista sozinha: quando a rede cai ANTES do
    /// `solicitar`, não nasce nem linha em pdv_autorizacao_token; e um token que
    /// ficou aberto é indistinguível entre "o operador cancelou" e "caiu para o
    /// PIN". O fato só existe aqui — por isso ele tem que viajar daqui.
    ///
    /// client_key = a referência do estorno (transação + venda + NSU + valor): é
    /// única por estorno, então o replay da fila não vira linha dobrada no painel.
    /// </summary>
    /// <param name="tx">
    /// A transação do estorno, quando o chamador já tem uma. Sem ela o método
    /// abre a sua: as duas linhas nascem juntas ou não nascem — fato auditado que
    /// não foi enfileirado é exatamente o buraco que isto fecha.
    /// </param>
    public static void AuditarSemAprovacaoRemota(SqliteConnection cx, DesfechoAutorizacao d,
        string operadorId, string? detalhe, PedidoAutorizacao pedido, string vendaId,
        SqliteTransaction? tx = null)
    {
        if (!d.SemAprovacaoRemota) return;

        using var propria = tx is null ? cx.BeginTransaction() : null;
        var t = tx ?? propria!;

        Caixa.Auditar(cx, t, EventoSemAprovacaoRemota, operadorId, d.Autorizador,
            $"{detalhe} · o token de WhatsApp não autorizou este estorno: {d.Motivo}");

        // O QUE VIAJA: só o suficiente para o painel montar a linha sem ligar para
        // a loja. O código de 6 dígitos NÃO está aqui (nem em lugar nenhum fora do
        // WhatsApp de quem aprova) — nem o PIN do supervisor, que é o segredo do
        // caixa: o que sobe é QUEM liberou, não COM O QUE.
        Caixa.Enfileirar(cx, t, TipoNaFila, vendaId, pedido.Referencia, new Dictionary<string, object?>
        {
            ["client_key"] = pedido.Referencia,
            ["store"] = pedido.Loja,
            ["terminal"] = pedido.Terminal,
            ["venda_id"] = vendaId,
            ["venda"] = pedido.Venda,
            ["referencia"] = pedido.Referencia,
            ["valor_cent"] = pedido.ValorCent,
            ["forma"] = pedido.Forma,
            ["nsu"] = pedido.Nsu,
            ["bandeira"] = pedido.Bandeira,
            // "pin" e "homologacao" não são a mesma coisa e não podem virar uma
            // coluna só: um é a saída de emergência com o cliente no balcão, o
            // outro é o roteiro PayGo rodando com a loja fechada.
            ["via"] = d.Via == ViaAutorizacao.Homologacao ? "homologacao" : "pin",
            ["motivo"] = d.Motivo,
            ["operador_id"] = operadorId,
            ["operador_nome"] = pedido.Operador,
            ["autorizado_por"] = d.Autorizador,
            ["autorizador_nome"] = d.Supervisor?.Nome,
            // Quando existiu token e ele morreu (queimado/expirado), o id amarra
            // esta linha à do pdv_autorizacao_token — é o que separa "a nuvem nem
            // foi chamada" de "o código saiu e não valeu".
            ["token_id"] = d.TokenId,
            ["detalhe"] = detalhe,
            ["criado_em"] = DateTime.Now.ToString("o"),
        });

        propria?.Commit();
    }

    private static string Curto(string? id) => id is { Length: > 8 } ? id[..8] : id ?? "?";

    /// <summary>
    /// A máquina de estados da autorização. NUNCA deixa o operador sem saída: todo
    /// caminho termina em autorizado (token/PIN/homologação) ou numa recusa que ele
    /// escolheu.
    ///
    /// NENHUM await AQUI DENTRO LEVA `ConfigureAwait(false)` — e isso é regra, não
    /// esquecimento (o teste UI-3 vigia). Quem chama é a thread de UI do WPF, e a
    /// continuação de cada await encosta em janela: o `Dispose` do `using
    /// (tela.Aguardando(...))` faz `_dono.IsEnabled = true`, e as telas seguintes
    /// fazem `new Window`. Janela do WPF só aceita ser tocada pela thread que a
    /// criou; com ConfigureAwait(false) a continuação volta numa thread do pool e
    /// vira InvalidOperationException. Como EstornarTefAsync não tinha catch, o
    /// `async void` do menu também não e o App não tem DispatcherUnhandledException,
    /// isso ENCERRAVA O PROCESSO no primeiro estorno de verdade — sem nem chegar a
    /// oferecer o PIN, que é justamente a saída que o dono exigiu. Ficava invisível
    /// na suíte porque a tela falsa aceita qualquer thread, e na bancada porque o
    /// modo de homologação retorna antes de criar a primeira Espera.
    ///
    /// O ConfigureAwait(false) do <see cref="ClienteAutorizacao"/> CONTINUA certo:
    /// lá ninguém encosta em janela.
    /// </summary>
    public static async Task<DesfechoAutorizacao> ResolverAsync(
        SqliteConnection cx, IAutorizacaoRemota? remota, PedidoAutorizacao pedido,
        Operador operador, ITelaAutorizacao tela, CancellationToken ct = default)
    {


        // A SAÍDA. Fica aqui dentro porque todo caminho de falha desemboca nela.
        async Task<DesfechoAutorizacao> PelaSaidaDoPin(string motivo)
        {
            var sup = await tela.PedirPinAsync(motivo);
            return sup is null
                ? new DesfechoAutorizacao(ViaAutorizacao.Recusada, null, null, null,
                    motivo + " · e o PIN não autorizou") { Avisado = true }
                : new DesfechoAutorizacao(ViaAutorizacao.Pin, sup, null, null, motivo);
        }

        if (remota is null) return await PelaSaidaDoPin("este caixa não tem nuvem configurada");

        var solicitacoes = 0;
        // TOKEN FANTASMA. Quando a edge demora mais que os 15 s, o caixa cai para o
        // PIN mas a nuvem termina o serviço: a linha do token nasce e o WhatsApp
        // sai. Na tentativa seguinte do MESMO estorno (o supervisor não estava por
        // perto, ou o operador simplesmente repetiu) este `false` é o que faz a
        // nuvem devolver AQUELE token em vez de criar outro — o código já está no
        // celular de quem aprova, e a vaga do rate limit não é gasta duas vezes.
        // Só o "não recebi" liga o reenvio, e aí a mensagem nova é o que se quer.
        var reenviar = false;
        while (true)
        {
            RespostaSolicitacao r;
            // A chamada é await de verdade (não .Result): a tela continua desenhando
            // e o caixa não congela enquanto a nuvem pensa. Sem ConfigureAwait: o
            // Dispose deste `using` é que fecha a janela de espera.
            using (tela.Aguardando("Enviando pedido de autorização para a gerência…"))
                r = await remota.SolicitarAsync(pedido, ct, reenviar);
            solicitacoes++;

            if (!r.Ok || r.Id is not { Length: > 0 })
                return await PelaSaidaDoPin(MotivoDaSolicitacao(r));

            string? aviso = null;
            var chutes = 0;
            var teto = Math.Max(r.MaxTentativas, 1) + 2;   // rede de segurança contra laço infinito
            var pedirOutro = false;

            while (!pedirOutro)
            {
                var escolha = await tela.PedirCodigoAsync(r, aviso);
                if (escolha.Acao == AcaoCodigo.Cancelar)
                    return new DesfechoAutorizacao(ViaAutorizacao.Recusada, null, null, r.Id,
                        "o operador desistiu da autorização") { Avisado = true };
                if (escolha.Acao == AcaoCodigo.Pin)
                    return await PelaSaidaDoPin("o operador preferiu o PIN do supervisor");
                // "Não recebi": daqui para a frente todo pedido é reenvio deliberado
                // — a nuvem queima o anterior e manda código NOVO.
                if (escolha.Acao == AcaoCodigo.NovoCodigo) { reenviar = true; break; }

                RespostaValidacao v;
                using (tela.Aguardando("Conferindo o código…"))
                    v = await remota.ValidarAsync(r.Id, escolha.Codigo ?? "", pedido.Referencia, ct);

                if (v.Ok)
                {
                    // CINTO E SUSPENSÓRIO. A edge já confere a amarra, mas quem manda o
                    // estorno para a adquirente é este processo: se o que voltou não for
                    // deste estorno, ninguém estorna nada — nem cai para o PIN, porque
                    // token trocado não é falha de rede, é sinal de coisa errada.
                    if ((v.Referencia is { Length: > 0 } && v.Referencia != pedido.Referencia)
                        || v.ValorCent != pedido.ValorCent)
                        return new DesfechoAutorizacao(ViaAutorizacao.Recusada, null, v.AprovadoPor, r.Id,
                            "o token aprovado não é deste estorno");

                    return new DesfechoAutorizacao(ViaAutorizacao.Token, null, v.AprovadoPor, r.Id,
                        "aprovado por " + (v.AprovadoPor ?? "quem recebeu o código"));
                }

                if (!v.Definitiva)
                {
                    // "Não sei" não gasta tentativa do token: quem falhou foi a rede.
                    aviso = "A nuvem não respondeu. Tente de novo ou use o PIN do supervisor.";
                    continue;
                }

                if (v.Motivo == "codigo_invalido" && ++chutes < teto)
                {
                    aviso = "Código não confere. Confira a mensagem no WhatsApp e digite de novo.";
                    continue;
                }

                // Token morto (queimado, expirado, já usado, de outro estorno) ou chutes
                // demais: o operador escolhe, mas NUNCA fica preso aqui.
                var saida = await tela.EscolherAposFalhaAsync(TextoDeMorte(v.Motivo));
                if (saida == EscolhaAposFalha.Pin)
                    return await PelaSaidaDoPin("o código de autorização não valeu (" + (v.Motivo ?? "recusado") + ")");
                if (saida == EscolhaAposFalha.Desistir)
                    return new DesfechoAutorizacao(ViaAutorizacao.Recusada, null, null, r.Id,
                        "o operador desistiu depois de o código não valer") { Avisado = true };
                // Token morto e o operador quer outro: também é reenvio deliberado.
                reenviar = true;
                pedirOutro = true;
            }

            if (solicitacoes >= MaxSolicitacoes)
                return await PelaSaidaDoPin($"a autorização por WhatsApp não se completou em {solicitacoes} tentativas");
        }
    }

    private static string MotivoDaSolicitacao(RespostaSolicitacao r)
    {
        if (!r.Definitiva) return "a nuvem não respondeu ao pedido de autorização";
        return r.Motivo switch
        {
            "muitas_solicitacoes" => "a nuvem recusou: pedidos demais deste caixa em pouco tempo",
            "sem_aprovadores" => "não há aprovador cadastrado para receber o código",
            "falha_no_envio" => "o WhatsApp não chegou a nenhum aprovador",
            "indisponivel" or "erro_interno" => "a nuvem está indisponível",
            null or "" => "a nuvem recusou o pedido de autorização",
            _ => $"a nuvem recusou o pedido de autorização ({r.Motivo})",
        };
    }

    private static string TextoDeMorte(string? motivo) => motivo switch
    {
        "bloqueado" => "Este código foi bloqueado por tentativas erradas demais.",
        "expirado" => "O código expirou.",
        "ja_usado" => "Este código já foi usado.",
        "referencia_divergente" => "Este código é de OUTRO estorno.",
        "nao_encontrado" => "A nuvem não reconhece mais este pedido de autorização.",
        _ => "O código de autorização não vale mais.",
    };
}

/// <summary>
/// Cliente HTTP da edge `pdv-autorizacao`. NUNCA lança por erro de rede: num
/// caixa de loja, rede caída é estado esperado — e é justamente ele que precisa
/// virar "não sei" para o PDV cair no PIN em vez de morrer com exceção na tela.
/// </summary>
public sealed class ClienteAutorizacao : IAutorizacaoRemota
{
    /// <summary>15 s é o contrato com o dono: passou disso, o caixa cai para o PIN.</summary>
    public static readonly TimeSpan TempoPadrao = TimeSpan.FromSeconds(15);

    private readonly string _endpoint;
    private readonly string _anonKey;
    private readonly TimeSpan _tempo;

    /// <param name="anonKey">
    /// A chave PÚBLICA. O .exe fica numa loja: service_role aqui seria entregar o
    /// banco inteiro a quem copiar o arquivo. Ela vai nos DOIS headers (`apikey` e
    /// `Authorization: Bearer`) porque a edge roda com verify_jwt = true — a anon
    /// key é um JWT assinado pelo projeto, e a plataforma confere a assinatura
    /// antes de a função rodar. Se esta chave for rotacionada sem atualizar o
    /// .exe, o estorno não trava: a edge responde 401, o PDV lê como veredito
    /// definitivo e cai para o PIN do supervisor.
    /// </param>
    public ClienteAutorizacao(string? urlBase = null, string? anonKey = null, TimeSpan? tempo = null)
    {
        _endpoint = (urlBase ?? Nuvem.UrlPadrao).TrimEnd('/') + "/functions/v1/pdv-autorizacao";
        _anonKey = anonKey ?? Nuvem.AnonKey;
        _tempo = tempo ?? TempoPadrao;
    }

    public async Task<RespostaSolicitacao> SolicitarAsync(PedidoAutorizacao p, CancellationToken ct,
        bool reenviar = false)
    {
        // Nomes de campo são CONTRATO com a edge (index.ts). `dry_run` não é
        // mandado de propósito: ele existe só para a suíte do backend.
        var (corpo, transportou) = await EnviarAsync(new
        {
            acao = "solicitar",
            terminal = p.Terminal,
            tipo = p.Tipo,
            referencia = p.Referencia,
            valor_cent = p.ValorCent,
            operador = p.Operador,
            loja = p.Loja,
            venda = p.Venda,
            forma = p.Forma,
            nsu = p.Nsu,
            bandeira = p.Bandeira,
            // Só quem apertou "não recebi" pede código NOVO. Sem isto a nuvem
            // devolveria o token que já existe e o botão viraria enfeite.
            reenviar,
        }, ct).ConfigureAwait(false);

        if (!transportou || corpo is not { } r)
            return new RespostaSolicitacao(false, false, "sem_resposta", null, null, 0, 0, 0,
                Array.Empty<DestinatarioAutorizacao>());

        var ok = Bool(r, "ok");
        var validade = (int)Num(r, "validade_segundos", 300);
        var id = Str(r, "id");
        if (ok && string.IsNullOrWhiteSpace(id))
            // Resposta positiva sem token é pior que uma recusa: sem id não há o que validar.
            return new RespostaSolicitacao(false, true, "resposta_incompleta", null, null, 0, 0, 0,
                Array.Empty<DestinatarioAutorizacao>());

        var destinatarios = new List<DestinatarioAutorizacao>();
        if (r.TryGetProperty("destinatarios", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var d in arr.EnumerateArray())
                destinatarios.Add(new DestinatarioAutorizacao(
                    Str(d, "nome") ?? "(sem nome)", Str(d, "scope") ?? "", Str(d, "telefone") ?? "",
                    Bool(d, "enviado")));

        // O relógio que vale é o DAQUI. O PC do caixa atrasa e adianta (já houve
        // terminal com meia hora de diferença); ancorar a contagem no `expira_em`
        // do servidor faria a tela mostrar "expira em -3 min" com o código válido
        // na mão do gerente.
        return new RespostaSolicitacao(ok, true, Str(r, "motivo"), id,
            ok ? DateTime.Now.AddSeconds(validade) : null,
            validade, (int)Num(r, "max_tentativas", 5), (int)Num(r, "entregues", 0), destinatarios)
        {
            // Token que já existia: nenhum WhatsApp novo saiu, e `validade_segundos`
            // já vem como o que SOBRA da vida dele.
            Reaproveitado = Bool(r, "reaproveitado"),
        };
    }

    public async Task<RespostaValidacao> ValidarAsync(string id, string codigo, string? referencia,
        CancellationToken ct)
    {
        var (corpo, transportou) = await EnviarAsync(new
        {
            acao = "validar",
            id,
            codigo,
            referencia,
        }, ct).ConfigureAwait(false);

        if (!transportou || corpo is not { } r)
            return new RespostaValidacao(false, false, "sem_resposta", null, null, 0);

        return new RespostaValidacao(Bool(r, "ok"), true, Str(r, "motivo"),
            Str(r, "aprovado_por"), Str(r, "referencia"), Num(r, "valor_cent", 0));
    }

    /// <summary>
    /// Devolve (corpo, transportou). `transportou = false` é o único "não sei" —
    /// timeout, DNS, recusa de conexão ou corpo que não é JSON. Um 429 ou 502 COM
    /// JSON é veredito e volta como transportado, para o PDV decidir na hora.
    /// </summary>
    private async Task<(JsonElement? Corpo, bool Transportou)> EnviarAsync(object corpo, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_tempo);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            req.Headers.TryAddWithoutValidation("apikey", _anonKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _anonKey);
            req.Content = new StringContent(JsonSerializer.Serialize(corpo), Encoding.UTF8, "application/json");
            // Fiscal.Http: UM cliente por processo (porta efêmera não se esgota) e a
            // única fonte de proxy do PDV. O tempo de cada rota vem do CTS acima.
            using var resp = await Fiscal.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var texto = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return (JsonDocument.Parse(texto).RootElement.Clone(), true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return (null, false); }
    }

    private static bool Bool(JsonElement e, string nome)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v)
           && v.ValueKind == JsonValueKind.True;

    private static string? Str(JsonElement e, string nome)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Num(JsonElement e, string nome, long padrao)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v)
           && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : padrao;
}
