using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

// ════════════════════════════════════════════════════════════════════════════
//  AUTORIZAÇÃO DE ESTORNO — código do autenticador do dono (TOTP), e só ele
//
//  Histórico curto, porque explica o desenho:
//   · 1ª versão: PIN do supervisor numa tela local. O PIN mora no banco DESTE
//     caixa: quem opera e quem autoriza podiam ser a mesma pessoa.
//   · 2ª versão: código de 6 dígitos mandado por mensagem para a gerência, com o
//     PIN como saída quando a nuvem não respondia. A saída virou o caminho: um
//     funcionário com a senha do admin copiada estornava à vontade.
//   · Agora (04/09/2026, pedido do dono): UM caminho só. O dono escaneia um QR
//     no painel do ERP com o Google Authenticator; o segredo fica SÓ no servidor
//     (tabela sem policy, RPC SECURITY DEFINER). O caixa pede o código de 6
//     dígitos que está no celular do dono e manda para a RPC
//     `pdv_autorizacao_totp`, que confere (RFC 6238, janela ±1 passo, replay
//     recusado, rate limit de 5 falhas em 10 min por terminal) e grava o log.
//
//  O QUE NÃO EXISTE MAIS, de propósito: saída pelo PIN, "estorno sem aprovação
//  remota", código por mensagem, "não recebi". Sem internet, sem sessão ou sem
//  autenticador configurado, o estorno simplesmente NÃO SAI. É a regra que o
//  dono pediu, e a suíte garante por FONTE que nenhum arquivo do .exe fabrica um
//  DesfechoAutorizacao aprovado fora daqui.
//
//  O CAIXA NUNCA VÊ O SEGREDO e nunca calcula TOTP: manda o código digitado e
//  recebe sim/não. O código não vai para log em claro (Mascarar) nem para a
//  auditoria; o que a auditoria grava é QUEM (o dono) e o id do registro na nuvem.
//
//  POR QUE ISTO VIVE NO NÚCLEO E NÃO NO CODE-BEHIND DA TELA: é uma máquina de
//  estados com dinheiro no fim. Dentro de um .xaml.cs ela só seria exercitada
//  por gente clicando. Aqui a suíte roda todos os caminhos sem abrir janela: a
//  tela entra por ITelaAutorizacao e a nuvem por IAutorizacaoRemota.
//
//  Contrato da RPC: supabase/migrations/20260904210000_pdv_totp.sql (ERP)
//  Testes:          Pdv.Testes/TestesAutorizacao.cs (contra FakeTotp)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>Por onde o estorno passou. Vira linha de auditoria, não é enfeite de UI.</summary>
public enum ViaAutorizacao
{
    /// <summary>Ninguém autorizou: operador desistiu, código inválido, sem internet, sem autenticador.</summary>
    Recusada,
    /// <summary>
    /// Histórico. O modo de homologação foi removido quando a operação começou
    /// (ele autorizava sem conferir nada). O valor fica para LER auditoria
    /// antiga; nada mais o produz.
    /// </summary>
    Homologacao,
    /// <summary>Código do autenticador do dono (Google Authenticator), conferido pela nuvem.</summary>
    Totp,
}

/// <summary>
/// Resposta da RPC `pdv_autorizacao_totp`. <see cref="Definitiva"/> é a distinção
/// que decide o fluxo: com JSON na mão (inclusive 401) o PDV já sabe o veredito;
/// só timeout e erro de rede são "não sei", e "não sei" também recusa.
/// </summary>
public sealed record RespostaTotp(bool Ok, bool Definitiva, string? Motivo, string? Id, string? Autorizador);

/// <summary>
/// O que identifica o ato que está sendo autorizado. `Referencia` é o que amarra
/// o registro na nuvem ÀQUELE estorno; ver <see cref="Autorizacao.Referencia"/>.
/// </summary>
public sealed record PedidoAutorizacao(
    string Terminal, string Referencia, long ValorCent,
    string? Loja = null, string? Operador = null, string? Venda = null,
    string? Forma = null, string? Nsu = null, string? Bandeira = null)
{
    /// <summary>
    /// "estorno" (dinheiro de volta no cartão), "cancelamento" (venda/nota,
    /// sem devolução pelo PDV) ou "promocao" (promoção com 2FA no caixa, 05/09).
    /// Vai para o log da nuvem e muda o texto da recusa.
    /// </summary>
    public string Tipo { get; init; } = "estorno";

    /// <summary>
    /// Quem pode aprovar: "dono" (só autenticador de owner) ou "gerente" (manager OU
    /// owner). Estorno e cancelamento NÃO mexem nisto: ficam no padrão, dono. Só a
    /// promoção com config.autorizacao='gerente' desce para o gerente.
    /// </summary>
    public string Nivel { get; init; } = Autorizacao.NivelDono;

    /// <summary>Tipo "promocao": a promoção que pediu o código (vai no detalhe do log).</summary>
    public string? PromocaoId { get; init; }
    public string? PromocaoNome { get; init; }
}

/// <summary>A nuvem. Implementação real: <see cref="ClienteAutorizacao"/>.</summary>
public interface IAutorizacaoRemota
{
    /// <param name="detalhe">O que vai para o log da nuvem (venda, valor, operador); nunca o código.</param>
    /// <param name="nivel">"dono" ou "gerente": de quem a nuvem aceita o código (parâmetro _nivel da RPC).</param>
    Task<RespostaTotp> ValidarTotpAsync(string codigo, string referencia, string tipo,
        IReadOnlyDictionary<string, object?>? detalhe, string nivel, CancellationToken ct);
}

/// <summary>
/// A tela. Tudo que precisa de janela sai por aqui, para a máquina de estados
/// poder ser exercitada sem WPF.
/// </summary>
public interface ITelaAutorizacao
{
    /// <summary>Aviso de espera ("Conferindo o código…"). O Dispose fecha.</summary>
    IDisposable Aguardando(string mensagem);
    /// <summary>
    /// Pede o código de 6 dígitos do autenticador. null = o operador cancelou.
    /// <paramref name="nivel"/> só muda o rótulo ("do gerente" / "do dono"): quem confere é a nuvem.
    /// </summary>
    Task<string?> PedirCodigoAsync(string? aviso, string nivel);
}

/// <summary>
/// Como o estorno foi autorizado. É o que a auditoria grava: QUEM aprovou e por
/// qual caminho, não só um bool.
/// </summary>
public sealed record DesfechoAutorizacao(ViaAutorizacao Via, string? AprovadoPor, string? TokenId, string Motivo)
{
    public bool Autorizado => Via != ViaAutorizacao.Recusada;

    /// <summary>Nível pedido à nuvem ("dono" ou "gerente"); só informa a trilha.</summary>
    public string Nivel { get; init; } = Autorizacao.NivelDono;

    /// <summary>
    /// A tela já explicou ao operador por que não seguiu (ele cancelou). Sem isto
    /// o caixa levaria dois avisos seguidos dizendo a mesma coisa.
    /// </summary>
    public bool Avisado { get; init; }

    /// <summary>Histórico: só a homologação antiga saía sem ninguém de fora aprovar.</summary>
    public bool SemAprovacaoRemota => Via == ViaAutorizacao.Homologacao;

    /// <summary>
    /// O que vai na coluna `autorizador` da auditoria. Não existe operador local
    /// que assine: entra uma marca sintética com o id do registro da nuvem, e o
    /// nome do dono vai no detalhe. (A coluna é TEXT livre e não sai daqui.)
    /// </summary>
    public string? Autorizador => Via == ViaAutorizacao.Totp && TokenId is { Length: > 0 }
        ? "totp:" + TokenId[..Math.Min(8, TokenId.Length)]
        : null;
}

public static class Autorizacao
{
    /// <summary>
    /// Histórico: tipo da linha no outbox dos estornos que saíam pelo PIN. Nada
    /// mais enfileira isto; a constante fica só para a fila drenar linhas antigas
    /// que ainda estejam num caixa (Drenagem.TiposComHandler).
    /// </summary>
    public const string TipoNaFila = "estorno_sem_aprovacao";

    /// <summary>Quantos códigos o operador pode digitar antes de a tela desistir.</summary>
    public const int MaxTentativas = 3;

    /// <summary>O motivo que a RPC devolve para código errado (e para replay).</summary>
    public const string MotivoCodigoInvalido = "codigo invalido";

    /// <summary>Os dois níveis que a RPC conhece (parâmetro _nivel, default 'dono').</summary>
    public const string NivelDono = "dono";
    public const string NivelGerente = "gerente";

    /// <summary>"gerente" ou "dono", para rótulo e trilha.</summary>
    public static string Papel(string? nivel) => nivel == NivelGerente ? "gerente" : "dono";

    /// <summary>
    /// AMARRA O REGISTRO ÀQUELE ESTORNO. Se o operador desistir e estornar OUTRA
    /// venda, o log da nuvem precisa dizer qual foi: entram os quatro dados que
    /// identificam a transação, e não só o NSU (NSU é contador curto e repete).
    /// </summary>
    public static string Referencia(string tefId, string nsu, long valorCent, long numeroVenda)
    {
        static string Limpo(string? s, int max)
            => new((s ?? "").Where(char.IsLetterOrDigit).Take(max).ToArray());
        return $"estorno:{Limpo(tefId, 48)}:v{numeroVenda}:nsu{Limpo(nsu, 24)}:c{valorCent}";
    }

    /// <summary>
    /// AMARRA O REGISTRO ÀQUELE CANCELAMENTO DE VENDA. Prefixo próprio porque são
    /// atos diferentes: cancelar a venda #12 não é devolver dinheiro no cartão.
    /// </summary>
    public static string ReferenciaCancelamento(string vendaId, long numeroVenda, long valorCent)
    {
        static string Limpo(string? s, int max)
            => new((s ?? "").Where(char.IsLetterOrDigit).Take(max).ToArray());
        return $"cancelamento:{Limpo(vendaId, 48)}:v{numeroVenda}:c{valorCent}";
    }

    /// <summary>
    /// AMARRA O REGISTRO À PROMOÇÃO NESTA COMANDA (05/09). A comanda ainda não é venda
    /// (não tem número): o id é o da comanda em andamento, que nasce com ela e morre
    /// com ela; a mesma promoção em outra comanda é outro registro.
    /// </summary>
    public static string ReferenciaPromocao(string comandaId, string promoId)
    {
        static string Limpo(string? s, int max)
            => new((s ?? "").Where(char.IsLetterOrDigit).Take(max).ToArray());
        return $"promocao:{Limpo(comandaId, 48)}:{Limpo(promoId, 48)}";
    }

    /// <summary>Nome com que este caixa se apresenta à nuvem (cadastro `pdv_terminais.nome`).</summary>
    public static string NomeDoTerminal(SqliteConnection cx)
    {
        var config = Vendas.Config(cx, "terminal_nome");
        return string.IsNullOrWhiteSpace(config) ? Environment.MachineName : config!.Trim();
    }

    /// <summary>
    /// O código digitado NUNCA aparece em claro fora da janela onde foi digitado.
    /// A máscara não deixa dígito nenhum: só o tamanho, para o diagnóstico dizer
    /// "veio um código de 6" sem dizer qual.
    /// </summary>
    public static string Mascarar(string? codigo) => new('*', (codigo ?? "").Length);

    /// <summary>O que vai para o log da nuvem junto da tentativa. Nunca o código.</summary>
    public static IReadOnlyDictionary<string, object?> Detalhe(PedidoAutorizacao p)
        => new Dictionary<string, object?>
        {
            ["terminal"] = p.Terminal,
            ["loja"] = p.Loja,
            ["operador"] = p.Operador,
            ["venda"] = p.Venda,
            ["valor_cent"] = p.ValorCent,
            ["forma"] = p.Forma,
            ["nsu"] = p.Nsu,
            ["bandeira"] = p.Bandeira,
            ["nivel"] = p.Nivel,
            ["promocao_id"] = p.PromocaoId,
            ["promocao"] = p.PromocaoNome,
        };

    /// <summary>Sufixo da linha de auditoria do estorno: quem aprovou.</summary>
    public static string Trilha(DesfechoAutorizacao d) => d.Via switch
    {
        ViaAutorizacao.Totp => $" · autorizado pelo autenticador do {Papel(d.Nivel)} ({d.AprovadoPor}, registro {Curto(d.TokenId)})",
        ViaAutorizacao.Homologacao => " · SEM APROVAÇÃO REMOTA (modo homologação)",
        _ => "",
    };

    private static string Curto(string? id) => id is { Length: > 8 } ? id[..8] : id ?? "?";

    /// <summary>
    /// A máquina de estados da autorização. Todo caminho termina em Via=Totp
    /// (a nuvem conferiu o código do dono) ou numa recusa: não existe terceira via.
    ///
    /// NENHUM await AQUI DENTRO LEVA `ConfigureAwait(false)`, e isso é regra, não
    /// esquecimento (o teste UI-3 vigia). Quem chama é a thread de UI do WPF, e a
    /// continuação de cada await encosta em janela: o `Dispose` do `using
    /// (tela.Aguardando(...))` faz `_dono.IsEnabled = true`, e a tela seguinte faz
    /// `new Window`. Com ConfigureAwait(false) a continuação volta numa thread do
    /// pool e vira InvalidOperationException, que já derrubou o Pdv.exe no balcão.
    /// O ConfigureAwait(false) do <see cref="ClienteAutorizacao"/> CONTINUA certo:
    /// lá ninguém encosta em janela.
    /// </summary>
    public static async Task<DesfechoAutorizacao> ResolverAsync(
        IAutorizacaoRemota? remota, PedidoAutorizacao pedido, ITelaAutorizacao tela,
        CancellationToken ct = default)
    {
        // O texto da recusa é o que a tela mostra, em UMA linha: "<motivo> <Ato> não autorizado."
        var (ato, naoAutorizado) = pedido.Tipo switch
        {
            "cancelamento" => ("Cancelamento", "não autorizado."),
            "promocao" => ("Promoção", "não autorizada."),
            _ => ("Estorno", "não autorizado."),
        };
        var papel = Papel(pedido.Nivel);
        DesfechoAutorizacao Nao(string motivo) =>
            new(ViaAutorizacao.Recusada, null, null, $"{motivo} {ato} {naoAutorizado}") { Nivel = pedido.Nivel };

        if (remota is null) return Nao("Este caixa não tem nuvem configurada.");

        string? aviso = null;
        for (var tentativa = 1; tentativa <= MaxTentativas; tentativa++)
        {
            var codigo = await tela.PedirCodigoAsync(aviso, pedido.Nivel);
            if (codigo is null)
                return new DesfechoAutorizacao(ViaAutorizacao.Recusada, null, null,
                    "o operador desistiu da autorização") { Avisado = true };

            RespostaTotp v;
            // await de verdade (não .Result): a tela continua desenhando enquanto a
            // nuvem confere. Sem ConfigureAwait: o Dispose deste `using` fecha a janela.
            using (tela.Aguardando("Conferindo o código…"))
                v = await remota.ValidarTotpAsync(codigo, pedido.Referencia, pedido.Tipo, Detalhe(pedido), pedido.Nivel, ct);

            if (v.Ok)
                return new DesfechoAutorizacao(ViaAutorizacao.Totp, v.Autorizador, v.Id,
                    $"aprovado pelo autenticador do {papel} (" + (v.Autorizador ?? papel) + ")") { Nivel = pedido.Nivel };

            // Rede caída ou nuvem muda: não é veredito, mas também não é autorização.
            if (!v.Definitiva) return Nao("Sem internet.");

            if (Normal(v.Motivo) == MotivoCodigoInvalido)
            {
                aviso = "Código inválido. Tente de novo.";
                continue;
            }

            // Recusa que não depende do código (rate limit, autenticador não
            // configurado, sessão): insistir não muda nada, então nem pede outro.
            return Nao(TextoDoMotivo(v.Motivo, papel));
        }

        return Nao($"Código inválido {MaxTentativas} vezes.");
    }

    private static string TextoDoMotivo(string? motivo, string papel) => Normal(motivo) switch
    {
        "muitas tentativas, aguarde" => "Muitas tentativas. Aguarde 10 minutos.",
        "autenticador nao configurado" => $"Autenticador do {papel} não configurado.",
        ClienteAutorizacao.MotivoSemSessao => "Este caixa está sem sessão na nuvem.",
        "" => "A nuvem recusou.",
        var m => $"A nuvem recusou ({m}).",
    };

    /// <summary>Minúsculas, sem acento e sem sobra: o servidor escreve "codigo invalido", alguém pode escrever "Código inválido".</summary>
    private static string Normal(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var forma = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(forma.Length);
        foreach (var c in forma)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
    }
}

/// <summary>
/// Cliente HTTP da RPC `pdv_autorizacao_totp` (PostgREST). NUNCA lança por erro
/// de rede: num caixa de loja, rede caída é estado esperado, e aqui ela vira
/// "não sei" (que recusa) em vez de exceção na tela.
/// </summary>
public sealed class ClienteAutorizacao : IAutorizacaoRemota
{
    /// <summary>Curto de propósito: o cliente está no balcão esperando o dinheiro.</summary>
    public static readonly TimeSpan TempoPadrao = TimeSpan.FromSeconds(10);

    public const string Rpc = "pdv_autorizacao_totp";

    /// <summary>Motivo devolvido quando o terminal não tem sessão na nuvem (nem vai à rede).</summary>
    public const string MotivoSemSessao = "sem sessao na nuvem";

    private readonly Func<CancellationToken, Task<string?>> _obterToken;
    private readonly Func<string?>? _terminalUuid;
    private readonly string _endpoint;
    private readonly string _anonKey;
    private readonly TimeSpan _tempo;

    /// <summary>
    /// Uma linha por tentativa (status HTTP, veredito, motivo) para o diagnóstico
    /// da loja. Recebe o código MASCARADO, nunca em claro (teste CT-13).
    /// </summary>
    public Action<string>? Diagnostico { get; set; }

    /// <param name="obterToken">
    /// O bearer da SESSÃO do terminal (Nuvem.TokenAsync). A RPC é executável por
    /// `authenticated`, não por `anon`: a chave pública vai só no header apikey,
    /// como em toda chamada ao PostgREST. Sem sessão o cliente nem vai à rede.
    /// </param>
    /// <param name="terminalUuid">O `terminal.terminal_uuid`: é o balde do rate limit na nuvem.</param>
    public ClienteAutorizacao(Func<CancellationToken, Task<string?>> obterToken, string? urlBase = null,
        string? anonKey = null, TimeSpan? tempo = null, Func<string?>? terminalUuid = null)
    {
        _obterToken = obterToken;
        _endpoint = (urlBase ?? Nuvem.UrlPadrao).TrimEnd('/') + "/rest/v1/rpc/" + Rpc;
        _anonKey = anonKey ?? Nuvem.AnonKey;
        _tempo = tempo ?? TempoPadrao;
        _terminalUuid = terminalUuid;
    }

    public async Task<RespostaTotp> ValidarTotpAsync(string codigo, string referencia, string tipo,
        IReadOnlyDictionary<string, object?>? detalhe, string nivel, CancellationToken ct)
    {
        var mascara = Autorizacao.Mascarar(codigo);
        string? token;
        try { token = await _obterToken(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { token = null; }

        if (string.IsNullOrWhiteSpace(token))
        {
            Anotar($"{tipo} {referencia} código {mascara}: {MotivoSemSessao}");
            return new RespostaTotp(false, true, MotivoSemSessao, null, null);
        }

        // Nomes de campo são CONTRATO com a RPC (parâmetros com underscore). `_agora`
        // não é mandado de propósito: o relógio que vale é o do servidor.
        var corpo = new Dictionary<string, object?>
        {
            ["_codigo"] = codigo,
            ["_referencia"] = referencia,
            ["_tipo"] = tipo,
            ["_detalhe"] = detalhe,
            ["_terminal_uuid"] = _terminalUuid?.Invoke(),
        };
        // 05/09: 'dono' (só owner) ou 'gerente' (manager ou owner). A chave `_nivel`
        // SÓ vai no corpo quando é 'gerente': o PostgREST casa a RPC pelo conjunto de
        // NOMES dos parâmetros, e a pdv_autorizacao_totp de produção anterior à
        // migration 20260905120000 não tem `_nivel` (com a chave, 404 PGRST202 e o
        // estorno morreria). Estorno, cancelamento e promoção do dono mandam o corpo
        // de sempre e caem no default 'dono' nas duas versões da RPC; o nível gerente
        // só existe a partir da migration (antes dela a promoção fica excluída).
        if (nivel is Autorizacao.NivelGerente) corpo["_nivel"] = Autorizacao.NivelGerente;
        var (status, texto) = await EnviarAsync(corpo, token, ct).ConfigureAwait(false);

        if (status == 0)
        {
            Anotar($"{tipo} {referencia} código {mascara}: sem resposta");
            return new RespostaTotp(false, false, "sem_resposta", null, null);
        }

        JsonElement r;
        try { r = JsonDocument.Parse(texto ?? "").RootElement.Clone(); }
        catch
        {
            Anotar($"{tipo} {referencia} código {mascara}: HTTP {status} corpo ilegível");
            return new RespostaTotp(false, true, $"resposta ilegível (HTTP {status})", null, null);
        }

        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("ok", out var okProp)
            && okProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            var ok = okProp.ValueKind == JsonValueKind.True;
            var motivo = ok ? null : Str(r, "motivo") ?? "recusado";
            Anotar($"{tipo} {referencia} código {mascara}: HTTP {status} ok={ok}" + (ok ? "" : $" motivo={motivo}"));
            return ok
                ? new RespostaTotp(true, true, null, Str(r, "id"), Str(r, "autorizador"))
                : new RespostaTotp(false, true, motivo, null, null);
        }

        // Erro do PostgREST ({code, message}): 401 de sessão vencida, 404 de RPC
        // que não existe, 42501 sem grant. Veredito definitivo, com o HTTP no motivo.
        var mensagem = Str(r, "message") ?? Str(r, "hint") ?? "";
        Anotar($"{tipo} {referencia} código {mascara}: HTTP {status} {mensagem}");
        return new RespostaTotp(false, true, $"HTTP {status}" + (mensagem.Length > 0 ? ": " + mensagem : ""), null, null);
    }

    /// <summary>
    /// Devolve (status, corpo). Status 0 é o único "não sei": timeout, DNS, recusa
    /// de conexão. Um 401 ou 404 COM corpo é veredito e volta com o status.
    /// </summary>
    private async Task<(int Status, string? Corpo)> EnviarAsync(object corpo, string token, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_tempo);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            req.Headers.TryAddWithoutValidation("apikey", _anonKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(JsonSerializer.Serialize(corpo), Encoding.UTF8, "application/json");
            // Fiscal.Http: UM cliente por processo e a única fonte de proxy do PDV.
            using var resp = await Fiscal.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var texto = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return ((int)resp.StatusCode, texto);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return (0, null); }
    }

    private void Anotar(string linha)
    {
        try { Diagnostico?.Invoke(linha); } catch { /* diagnóstico nunca atrapalha a autorização */ }
    }

    private static string? Str(JsonElement e, string nome)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
