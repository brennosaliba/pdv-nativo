using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pdv.Nucleo;

/// <summary>
/// Ponte com o backend (Supabase). O caixa NUNCA espera a nuvem pra vender: tudo que
/// ele precisa (catálogo, operadores) é espelhado no SQLite local. A nuvem entra
/// para (a) trazer o que mudou e (b) receber o que foi vendido.
///
/// A credencial é do TERMINAL, não da pessoa — o operador não deve ter conta no
/// sistema de gestão. Ela fica cifrada na máquina e o app renova a sessão sozinho.
/// </summary>
public sealed class Nuvem : IFeedKds
{
    public const string ProjectRef = "ctwjedradxhlmqmdmbif";
    public const string UrlPadrao = "https://" + ProjectRef + ".supabase.co";
    public const string AnonKey =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImN0d2plZHJhZHhobG1xbWRtYmlmIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NzYxMDY4OTYsImV4cCI6MjA5MTY4Mjg5Nn0.bApYhWCP5lYHBGe5WFpSI1ga5pQv3G4kCHkwQhmLnwI";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly SemaphoreSlim _renovando = new(1, 1);
    private string? _token;
    private string? _refresh;
    private DateTime _expira = DateTime.MinValue;
    private readonly string _url;

    public Nuvem(string? url = null) => _url = (url ?? UrlPadrao).TrimEnd('/');

    /// <summary>
    /// De onde vêm e-mail e senha do TERMINAL para o re-login silencioso, quando nem o
    /// refresh_token serve mais. Fica como delegate porque as credenciais são cifradas
    /// com DPAPI lá no app, e o núcleo não conhece essa camada.
    /// </summary>
    public Func<(string email, string senha)?>? Credenciais { get; set; }

    public async Task<bool> EntrarAsync(string email, string senha)
        => await AutenticarAsync("password",
            new { email = email.Trim(), password = senha }, CancellationToken.None);

    private async Task<bool> AutenticarAsync(string grant, object corpo, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_url}/auth/v1/token?grant_type={grant}");
            req.Headers.TryAddWithoutValidation("apikey", AnonKey);
            req.Content = new StringContent(JsonSerializer.Serialize(corpo), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // refresh_token recusado não volta a funcionar sozinho: descarta, senão
                // toda chamada seguinte gasta um round-trip para levar o mesmo 400.
                if (grant == "refresh_token") _refresh = null;
                return false;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            _token = doc.RootElement.GetProperty("access_token").GetString();
            _refresh = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : _refresh;
            var seg = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            _expira = DateTime.Now.AddSeconds(seg - 120);   // renova antes de vencer
            return _token is not null;
        }
        catch { return false; }
    }

    public bool Autenticado => _token is not null && DateTime.Now < _expira;

    /// <summary>
    /// Garante sessão válida antes de qualquer chamada autenticada.
    ///
    /// O caixa entra às 8h e fica logado o dia inteiro, mas o token vale ~1h. Sem isso,
    /// a primeira venda no cartão depois das 9h leva 401 — e, pior, um 401 tratado como
    /// "sem internet" jogaria o PDV em contingência sem necessidade, queimando numeração
    /// da série local e criando trabalho de sincronização à toa.
    ///
    /// Ordem: token válido → refresh_token → re-login com a credencial do terminal.
    /// </summary>
    public async Task<bool> SessaoOkAsync(CancellationToken ct = default)
    {
        if (Autenticado) return true;

        // Uma renovação por vez: emissão e TEF podem pedir ao mesmo tempo, e duas
        // trocas simultâneas invalidam o refresh_token uma da outra.
        await _renovando.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Autenticado) return true;                       // outro já renovou enquanto esperava
            if (_refresh is { Length: > 0 }
                && await AutenticarAsync("refresh_token", new { refresh_token = _refresh }, ct)) return true;

            var cred = Credenciais?.Invoke();
            if (cred is null) return false;
            return await AutenticarAsync("password",
                new { email = cred.Value.email.Trim(), password = cred.Value.senha }, ct);
        }
        finally { _renovando.Release(); }
    }

    /// <summary>Token pronto para o cabeçalho Authorization, renovando se precisar.</summary>
    public async Task<string?> TokenAsync(CancellationToken ct = default)
        => await SessaoOkAsync(ct).ConfigureAwait(false) ? _token : null;

    private HttpRequestMessage Montar(HttpMethod m, string caminho)
    {
        var req = new HttpRequestMessage(m, $"{_url}{caminho}");
        req.Headers.TryAddWithoutValidation("apikey", AnonKey);
        // Sem sessão, vai com a chave pública: o servidor decide o que ela pode ler
        // (hoje, o catálogo). Escrever continua exigindo identidade — de propósito:
        // chave embutida em EXE é extraível por qualquer um com o arquivo.
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token ?? AnonKey);
        return req;
    }

    /// <summary>
    /// Traz o catálogo pro SQLite local. Só o que a venda precisa — e com o bloco
    /// fiscal junto, porque errar NCM/CSOSN na hora da venda vira rejeição da SEFAZ.
    /// Devolve quantos produtos entraram.
    /// </summary>
    public async Task<int> BaixarProdutosAsync(SqliteConnection cx)
    {
        // Catálogo desce sem login: leitura é liberada pela chave pública do app.
        // (Se a política do servidor não existir, a resposta vem vazia — não é erro.)
        const string campos = "id,degust_code,name,grupo,unit_price,unidade,ncm,cest,csosn,origem,image_url,ativo";

        // PREÇO POR LOJA: a view `pdv_catalogo_loja` resolve preço próprio da loja
        // com queda para o preço base. Ler dela em vez de pdv_products é o que faz
        // uma loja poder divergir de preço sem duplicar o cadastro.
        //
        // Sem nome de loja (caixa ainda não pareado), volta para a tabela crua —
        // aí o preço é o base, que é o mesmo de hoje. O pior caso desta troca é
        // vender pelo preço base; nunca pelo preço de OUTRA loja.
        var loja = cx.QueryFirstOrDefault<string>("SELECT loja_nome FROM terminal LIMIT 1");
        // canal=eq.pdv é OBRIGATÓRIO: desde a migração de canais a view devolve UMA
        // LINHA POR CANAL (pdv|ifood). Sem este filtro, produto vendido também no
        // iFood volta DUPLICADO — e o caixa poderia pegar o preço do iFood (com a
        // comissão do marketplace embutida), emitindo NFC-e com valor a maior.
        var caminho = string.IsNullOrWhiteSpace(loja)
            ? $"/rest/v1/pdv_products?select={campos}&ativo=eq.true&channel_pdv=eq.true&unit_price=gt.0&order=grupo.asc,name.asc"
            : $"/rest/v1/pdv_catalogo_loja?select={campos}&store=eq.{Uri.EscapeDataString(loja)}" +
              "&canal=eq.pdv&ativo=eq.true&channel_pdv=eq.true&unit_price=gt.0&order=grupo.asc,name.asc";

        using var req = Montar(HttpMethod.Get, caminho);
        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Não consegui baixar o catálogo ({(int)resp.StatusCode}).");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        // Lista vazia NÃO desativa nada: pode ser a política de leitura ausente no
        // servidor, não um catálogo de fato vazio. Zerar a loja por causa disso
        // deixaria o caixa sem nenhum produto para vender.
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return 0;

        var agora = DateTime.Now.ToString("o");
        var n = 0;

        // FOTO do catalogo ANTES de gravar. O relatorio da sincronizacao dizia
        // "83 produtos" — verdade e inutil: quem trocou UM preco quer ver O
        // preco. Com o retrato anterior em memoria da para dizer o que mudou.
        var antes = cx.Query("SELECT id, nome, preco_cent, ativo FROM produto")
            .ToDictionary(r => (string)r.id, r => ((string)r.nome, (long)r.preco_cent, (long)r.ativo));
        MudancasDoCatalogo.Clear();

        using var tx = cx.BeginTransaction();
        // Some da lista = inativo aqui. Não apaga: venda antiga referencia o produto,
        // e apagar quebraria o histórico.
        cx.Execute("UPDATE produto SET ativo = 0", transaction: tx);

        foreach (var p in doc.RootElement.EnumerateArray())
        {
            string? S(string nome) => p.TryGetProperty(nome, out var v) && v.ValueKind != JsonValueKind.Null
                ? (v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString()) : null;

            var precoTxt = S("unit_price") ?? "0";
            if (!decimal.TryParse(precoTxt, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var preco) || preco <= 0) continue;

            var idProd = S("id") ?? "";
            var nomeProd = S("name") ?? "PRODUTO";
            var precoCent = Dinheiro.DeReais(preco).Centavos;
            if (antes.TryGetValue(idProd, out var velho))
            {
                if (velho.Item2 != precoCent)
                    MudancasDoCatalogo.Add($"{nomeProd}: {new Dinheiro(velho.Item2).Formatado()} → {new Dinheiro(precoCent).Formatado()}");
                else if (velho.Item1 != nomeProd)
                    MudancasDoCatalogo.Add($"{velho.Item1} → {nomeProd}");
                else if (velho.Item3 == 0)
                    MudancasDoCatalogo.Add($"{nomeProd}: voltou ao cardapio");
            }
            else MudancasDoCatalogo.Add($"{nomeProd}: novo no cardapio");

            cx.Execute("""
                INSERT INTO produto (id, plu, ean, nome, categoria, preco_cent, unidade, foto_local,
                                     ncm, cest, csosn, cfop, origem, pesavel, ativo, atualizado)
                VALUES (@Id,@Plu,NULL,@Nome,@Cat,@Preco,@Un,@Foto,@Ncm,@Cest,@Csosn,NULL,@Orig,0,1,@Em)
                ON CONFLICT(id) DO UPDATE SET
                    plu=@Plu, nome=@Nome, categoria=@Cat, preco_cent=@Preco, unidade=@Un,
                    foto_local=@Foto, ncm=@Ncm, cest=@Cest, csosn=@Csosn, origem=@Orig,
                    ativo=1, atualizado=@Em
                """,
                new
                {
                    Id = S("id"), Plu = S("degust_code"), Nome = S("name") ?? "PRODUTO",
                    Cat = S("grupo") ?? "Outros", Preco = Dinheiro.DeReais(preco).Centavos,
                    Un = S("unidade") ?? "UN", Foto = S("image_url"),
                    Ncm = S("ncm"), Cest = S("cest"), Csosn = S("csosn"),
                    Orig = int.TryParse(S("origem"), out var o) ? o : 0, Em = agora,
                }, tx);
            n++;
        }
        // Quem sumiu da lista foi desativado la em cima; so vale contar quem
        // ESTAVA ativo — produto inativo que continua inativo nao e novidade.
        var sumiram = cx.Query<string>(
            "SELECT nome FROM produto WHERE ativo = 0 AND atualizado <> @Em", new { Em = agora },
            transaction: tx).ToList();
        foreach (var nome in sumiram)
            if (antes.Values.Any(v => v.Item1 == nome && v.Item3 == 1))
                MudancasDoCatalogo.Add($"{nome}: saiu do cardapio");

        tx.Commit();
        return n;
    }

    /// <summary>
    /// O que mudou no catalogo na ULTIMA descida — preenchido por
    /// BaixarCatalogoAsync e lido pela tela. Lista curta de propósito: o
    /// operador quer conferir o que pediu, nao auditar 83 linhas.
    /// </summary>
    public static readonly List<string> MudancasDoCatalogo = new();

    /// <summary>
    /// Traz os operadores do painel para o caixa. O hash da senha vem PRONTO no
    /// formato do contrato (PBKDF2 idêntico ao Operadores.cs) — aqui só se copia.
    ///
    /// Operadores criados localmente (suporte, primeira instalação) NÃO são tocados:
    /// só as linhas marcadas como vindas da nuvem são regidas pelo painel — inclusive
    /// a desativação de quem sumiu de lá (demitido some do caixa no próximo Sincronizar).
    ///
    /// A ÚNICA EXCEÇÃO É A MESMA PESSOA. "Não tocar no local" virou, na prática, deixar
    /// dois cadastros vivos para quem instalou o caixa: o assistente cria o primeiro
    /// operador (passo 1) antes do pareamento (passo 5), com um id que só existe nesta
    /// máquina, e o painel manda o dele depois. O local continuava logando e assinando
    /// vendas que a nuvem recusava com 409 — 16 delas, R$ 102.626,50, no caixa da
    /// Savassi. Quando o CPF é o mesmo, os dois viram UMA identidade aqui dentro:
    /// <see cref="Operadores.ReconciliarComNuvem"/>.
    /// </summary>
    public async Task<int> BaixarOperadoresAsync(SqliteConnection cx)
    {
        if (!await SessaoOkAsync()) return 0;
        try
        {
            using var req = Montar(HttpMethod.Post, "/rest/v1/rpc/pdv_operadores_sync");
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return 0;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return 0;

            var agora = DateTime.Now.ToString("o");
            var vieram = new List<string>();
            var n = 0;

            using var tx = cx.BeginTransaction();
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                string? S(string nome) => o.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;
                var id = S("id");
                var hash = S("pin_hash");
                var salt = S("pin_salt");
                var cpfCru = S("cpf");
                if (id is null || hash is null || salt is null) continue;

                // O CPF é gravado NORMALIZADO (só dígitos, zero à esquerda reposto). O
                // painel manda do jeito que foi digitado lá, e o login por CPF limpa a
                // pontuação antes de consultar: guardar "529.982.247-25" fazia a linha
                // do painel nunca ser encontrada, e a identidade forte morria em silêncio.
                var cpf = string.IsNullOrWhiteSpace(cpfCru)
                    ? null
                    : Operadores.CpfChave(cpfCru) ?? Documentos.SoDigitos(cpfCru);

                // perfil do painel → perfil do caixa (gerente autoriza sangria/cancelamento)
                var perfil = S("perfil") == "gerente" ? "gerente" : "operador";
                var ativo = !o.TryGetProperty("ativo", out var a) || a.ValueKind != JsonValueKind.False;

                cx.Execute("""
                    INSERT INTO operador (id, nome, pin_hash, pin_salt, perfil, cpf, ativo, da_nuvem,
                                          pin_nuvem_hash, pin_nuvem_salt, atualizado)
                    VALUES (@Id,@N,@H,@S,@P,@C,@A,1,@H,@S,@Em)
                    ON CONFLICT(id) DO UPDATE SET
                        nome=@N, perfil=@P, cpf=@C, ativo=@A, da_nuvem=1, atualizado=@Em,
                        -- A SENHA SÓ É REESCRITA QUANDO O PAINEL DE FATO A TROCOU.
                        -- `pin_nuvem_hash` é o que o painel mandou da última vez; comparar
                        -- com ele separa "o ciclo de sincronização passou de novo" (a cada
                        -- poucos minutos) de "alguém trocou a senha lá" (ato deliberado).
                        -- Sem essa distinção, a senha que a loja digita todo dia —
                        -- preservada na reconciliação logo abaixo — morreria na
                        -- sincronização seguinte, e amanhã ninguém entraria no caixa.
                        -- `IS` e não `=` porque linha antiga tem pin_nuvem_hash NULL, e
                        -- NULL = @H não é falso, é nulo: o CASE cairia no ELSE por acidente
                        -- (que por sorte é o comportamento antigo, mas por acidente).
                        pin_hash = CASE WHEN operador.pin_nuvem_hash IS @H THEN operador.pin_hash ELSE @H END,
                        pin_salt = CASE WHEN operador.pin_nuvem_hash IS @H THEN operador.pin_salt ELSE @S END,
                        pin_nuvem_hash = @H, pin_nuvem_salt = @S
                    """,
                    new { Id = id, N = S("nome") ?? "OPERADOR", H = hash, S = salt, P = perfil,
                          C = cpf, A = ativo ? 1 : 0, Em = agora }, tx);

                // MESMA PESSOA, DOIS CADASTROS: aqui, na MESMA transação da descida.
                // Roda depois do upsert de propósito — a linha do painel precisa já
                // existir (a lápide aponta para ela) e já ter o `pin_nuvem_hash` do
                // ciclo, para o CASE acima preservar a senha da loja no próximo.
                Operadores.ReconciliarComNuvem(cx, tx, id, cpfCru, ativo, agora);

                vieram.Add(id);
                n++;
            }

            // quem é da nuvem e não veio mais, desativa — demissão vale no caixa também
            if (vieram.Count == 0)
                cx.Execute("UPDATE operador SET ativo = 0, atualizado = @Em WHERE da_nuvem = 1 AND ativo = 1",
                    new { Em = agora }, tx);
            else
                cx.Execute($"""
                    UPDATE operador SET ativo = 0, atualizado = @Em
                     WHERE da_nuvem = 1 AND ativo = 1
                       AND id NOT IN ({string.Join(',', vieram.Select((_, i) => "@P" + i))})
                    """,
                    BuildParams(vieram, agora), tx);

            // ⚠️ PISO: A DESCIDA NUNCA DEIXA O CAIXA SEM NINGUÉM QUE ABRA O TURNO.
            //
            // Antes da reconciliação, o operador criado no caixa era a última rede: a
            // sincronização não o tocava. Agora ele vira lápide (ativo=0) e a única
            // identidade viva é a do painel — o que resolve o defeito das vendas
            // recusadas, mas abre um pior: se o painel responder 200 com LISTA VAZIA
            // (não é rede caindo; rede caindo cai no catch e não commita), ou se alguém
            // simplesmente desligar o dono lá, este UPDATE apaga o último acesso e a
            // LOJA NÃO ABRE AMANHÃ. E a saída de emergência fechou junto: o _admin_
            // nasce inativo, e recadastrar pelo CPF esbarra na guarda nova.
            //
            // Ninguém no balcão tem como sair disso às 7h da manhã. Então a regra é:
            // demissão vale no caixa, MENOS a última. Se a desativação zeraria o
            // acesso, o mais recente volta a valer e fica o rastro na auditoria — o
            // painel continua sendo a verdade sobre QUEM é quem, mas não sobre se a
            // loja consegue vender hoje.
            var abrem = cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM operador WHERE ativo = 1", transaction: tx);
            if (abrem == 0)
            {
                var reerguido = cx.ExecuteScalar<string?>("""
                    SELECT id FROM operador
                     WHERE pin_hash IS NOT NULL AND pin_hash <> ''
                     ORDER BY atualizado DESC LIMIT 1
                    """, transaction: tx);
                if (reerguido is not null)
                {
                    cx.Execute("UPDATE operador SET ativo = 1, atualizado = @Em WHERE id = @Id",
                        new { Em = agora, Id = reerguido }, tx);
                    Caixa.Auditar(cx, tx, "operador_piso_reerguido", null, null,
                        $"a sincronizacao deixaria o caixa sem nenhum operador ativo; "
                        + $"'{reerguido}' foi mantido para a loja conseguir abrir. "
                        + "Confira o cadastro de funcionarios no painel.");
                }
            }

            tx.Commit();
            return n;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Pedidos de delivery do dia (ifood_orders), para o KDS do quiosque. Leitura
    /// leve: so o que a tela de preparo mostra. Falha de rede devolve lista VAZIA -
    /// o KDS continua com o que ja tem no SQLite, que e o contrato do PDV inteiro.
    /// </summary>
    public async Task<List<PedidoDelivery>> BaixarPedidosDeliveryAsync(string loja, int janelaMin = 45)
        => (await FeedKdsAsync(loja, janelaMin).ConfigureAwait(false)).Pedidos;

    /// <summary>
    /// O feed do quadro COM confiabilidade explicita.
    ///
    /// Por que a confiabilidade passou a viajar junto (04/09/2026): a RPC virou
    /// ESPELHO do conjunto ABERTO, e o exe passou a poder concluir coisas da
    /// AUSENCIA de um pedido no feed. Enquanto ninguem inferia fechamento da
    /// ausencia, devolver lista vazia para sem-sessao, HTTP nao 2xx, excecao e
    /// JSON ilegivel era inofensivo. Agora e a falha mais cara possivel: uma
    /// queda de wi-fi de 30 segundos limparia o quadro com a cozinha cheia.
    ///
    /// Confiavel = true SOMENTE com sessao valida, HTTP 2xx e corpo que fez
    /// parse como array JSON. Qualquer outra coisa e "nao sei", e "nao sei"
    /// preserva o quadro exatamente como esta.
    ///
    /// Lista vazia por SUCESSO (loja sem pedido aberto) e coisa diferente de
    /// lista vazia por FALHA, e agora o tipo carrega essa diferenca.
    /// </summary>
    public async Task<(bool Confiavel, List<PedidoDelivery> Pedidos)> FeedKdsAsync(
        string loja, int janelaMin = 45)
    {
        try
        {
            // RPC com escopo, nao a tabela: ifood_orders tem RLS de gestor, e o
            // terminal pareado nao e gestor. A funcao devolve so o conjunto aberto
            // da PROPRIA loja - e exige sessao valida (o anon le zero, de proposito).
            if (!await SessaoOkAsync().ConfigureAwait(false)) return (false, new());
            using var req = Montar(HttpMethod.Post, "/rest/v1/rpc/pdv_kds_pedidos");
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { _loja = loja, _janela_min = janelaMin }),
                Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (false, new());
            var corpo = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return TentarLerFeedKds(corpo, out var pedidos) ? (true, pedidos) : (false, new());
        }
        catch { return (false, new()); }
    }

    /// <summary>
    /// O JSON da RPC pdv_kds_pedidos, campo a campo e TOLERANTE: coluna que o
    /// servidor ainda nao manda vira o padrao de sempre (entrega, imediato), e
    /// JSON ilegivel devolve lista vazia — nunca derruba a puxada. Separado da
    /// chamada HTTP para a suite provar o contrato sem rede.
    /// </summary>
    public static List<PedidoDelivery> LerFeedKds(string json)
    {
        TentarLerFeedKds(json, out var r);
        return r;
    }

    /// <summary>
    /// Igual a <see cref="LerFeedKds"/>, mas DIZ se o corpo era mesmo um array
    /// JSON. false = "nao sei o que a nuvem respondeu", e quem chama nao pode
    /// concluir NADA da lista vazia que vem junto.
    /// </summary>
    public static bool TentarLerFeedKds(string json, out List<PedidoDelivery> pedidos)
    {
        var r = new List<PedidoDelivery>();
        pedidos = r;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var id = e.TryGetProperty("order_id", out var oid) ? oid.GetString() : null;
                if (id is null) continue;
                r.Add(new PedidoDelivery(
                    id,
                    e.TryGetProperty("display_id", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString()! : id[..Math.Min(4, id.Length)],
                    e.TryGetProperty("customer_name", out var c) && c.ValueKind == JsonValueKind.String
                        ? c.GetString() : null,
                    e.TryGetProperty("itens", out var it) && it.ValueKind is JsonValueKind.Array or JsonValueKind.Object
                        ? it.GetRawText() : "[]",
                    e.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                        ? st.GetString() ?? "" : "",
                    e.TryGetProperty("recebido_em", out var rc) && rc.ValueKind == JsonValueKind.String
                        ? rc.GetString() : null,
                    e.TryGetProperty("preparo_ate", out var pa) && pa.ValueKind == JsonValueKind.String
                        ? pa.GetString() : null,
                    // ENTREGA ou RETIRADA. A RPC passou a devolver isto para o card
                    // parar de afirmar "esperando o entregador" em pedido de retirada.
                    // Servidor antigo nao manda o campo: ausencia = entrega, que e o
                    // padrao seguro (o balcao continua esperando o motoboy).
                    e.TryGetProperty("retirada", out var rt) && rt.ValueKind == JsonValueKind.True,
                    // AGENDADO (04/09): o cliente marcou hora. RPC antiga nao manda os
                    // campos: ausencia = imediato, que e o que sempre foi.
                    e.TryGetProperty("agendado", out var ag) && ag.ValueKind == JsonValueKind.True,
                    e.TryGetProperty("agendado_para", out var ap) && ap.ValueKind == JsonValueKind.String
                        ? ap.GetString() : null,
                    e.TryGetProperty("agendado_ate", out var aa) && aa.ValueKind == JsonValueKind.String
                        ? aa.GetString() : null));
            }
        }
        catch { /* JSON quebrado: o que deu; a fila local continua valendo */ return false; }
        return true;
    }

    /// <summary>
    /// Espelha as promocoes VIGENTES da loja no SQLite (substitui tudo: o
    /// servidor ja filtrou vigencia e loja; dia/hora quem decide e o motor
    /// local). Sem sessao ou sem rede devolve -1 e o espelho anterior fica.
    /// </summary>
    public async Task<int> BaixarPromocoesAsync(SqliteConnection cx, string loja)
    {
        try
        {
            if (!await SessaoOkAsync().ConfigureAwait(false)) return -1;
            using var req = Montar(HttpMethod.Post, "/rest/v1/rpc/pdv_promocoes_ativas");
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { _loja = loja }), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return -1;

            var corpo = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(corpo);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return -1;

            var agora = DateTime.Now.ToString("o");
            using var tx = cx.BeginTransaction();
            cx.Execute("DELETE FROM promo", transaction: tx);
            var n = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var id = e.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (id is null) continue;
                cx.Execute("INSERT INTO promo (id, payload, atualizado_em) VALUES (@i, @p, @a)",
                    new { i = id, p = e.GetRawText(), a = agora }, tx);
                n++;
            }
            tx.Commit();
            return n;
        }
        catch { return -1; }
    }

    /// <summary>Status atual (efetivo) de pedidos ESPECIFICOS - a reconciliacao
    /// dos tickets que ficaram fora da janela do feed. Falha devolve lista
    /// vazia: os tickets ficam como estao ate o proximo ciclo.</summary>
    public async Task<List<(string OrderId, string Status, string? PreparoAte)>> StatusPedidosAsync(
        IReadOnlyList<string> orderIds)
        => (await StatusKdsAsync(orderIds).ConfigureAwait(false)).Itens;

    /// <summary>
    /// Igual a <see cref="StatusPedidosAsync"/>, mas dizendo se a resposta e
    /// CONFIAVEL. Sem isto, "a nuvem nao devolveu linha para este pedido"
    /// (que a reconciliacao le como "a nuvem nao conhece este pedido") seria
    /// indistinguivel de "a chamada falhou" — e o exe fecharia cards por
    /// causa de um timeout.
    ///
    /// Lote VAZIO conta como confiavel: nao havia o que perguntar, e nao ha
    /// duvida nenhuma a resolver.
    /// </summary>
    public async Task<(bool Confiavel, List<(string OrderId, string Status, string? PreparoAte)> Itens)>
        StatusKdsAsync(IReadOnlyList<string> orderIds)
    {
        try
        {
            if (orderIds.Count == 0) return (true, new());
            if (!await SessaoOkAsync().ConfigureAwait(false)) return (false, new());
            using var req = Montar(HttpMethod.Post, "/rest/v1/rpc/pdv_kds_status");
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { _order_ids = orderIds }),
                Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (false, new());

            var r = new List<(string, string, string?)>();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return (false, new());
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var id = e.TryGetProperty("order_id", out var i) ? i.GetString() : null;
                var st = e.TryGetProperty("status", out var sv) ? sv.GetString() : null;
                if (id is null || st is null) continue;
                r.Add((id, st,
                    e.TryGetProperty("preparo_ate", out var pa) && pa.ValueKind == JsonValueKind.String
                        ? pa.GetString() : null));
            }
            return (true, r);
        }
        catch { return (false, new()); }
    }

    private static Dapper.DynamicParameters BuildParams(List<string> ids, string agora)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("Em", agora);
        for (var i = 0; i < ids.Count; i++) p.Add("P" + i, ids[i]);
        return p;
    }
}
