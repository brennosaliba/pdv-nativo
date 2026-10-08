using System.Threading.Channels;
using Pdv.Nucleo;

namespace Pdv;

/// <summary>
/// O BANCO DE CONVERSAS DO CHAT DO iFOOD, O SERVICO VIVO DO CAIXA (08/10/2026, SQL 155).
///
/// O segundo ouvinte dos quadros do WebSocket do Sendbird, independente do resgate
/// (<see cref="ServicoConversaChat"/>): le TUDO que e mensagem, mascara, grava no SQLite com a fila e
/// manda em lote (ate 50 por 20 s) para a edge <c>ifood-chat-arquivo</c>. Nunca atrasa o resgate: a
/// leitura e a triagem sao puras e rodam na thread da tela; a gravacao e o envio vao para outra.
/// Fila propria, chamada propria, edge propria, tabela propria. Se o arquivo cair, o resgate nem
/// percebe; se o resgate cair, o arquivo continua.
///
/// O arquivo e de MAO UNICA: este servico nao le classe, texto nem decisao. So <c>captura</c> e as
/// contagens. Com <c>captura:false</c> (a loja esta desligada no ERP) descarta o que leu e fica 10 min
/// sem mandar. Os merchants da loja vem do sinal do chat (<see cref="ServicoConversaChat.Sinal"/>): sem
/// eles a mensagem fica guardada esperando (ate 2 dias), nada sai.
///
/// Nunca lanca: chat nao derruba caixa.
/// </summary>
public static class ServicoArquivoChat
{
    private static readonly AgrupadorDoArquivo Lote = new();
    private static readonly SemaphoreSlim UmaChamadaPorVez = new(1, 1);
    /// <summary>UM escritor so: as gravacoes saem da thread da tela para uma fila e entram no SQLite uma a uma, sem disputa.</summary>
    private static readonly Channel<(CanalDaFala Canal, MensagemArquivo Msg, DateTime Agora)> Gravacoes =
        Channel.CreateUnbounded<(CanalDaFala, MensagemArquivo, DateTime)>(new UnboundedChannelOptions { SingleReader = true });
    private static int _naFila;
    private static System.Threading.Timer? _tique;
    private static int _iniciado;
    private static int _noTique;
    private static DateTime _proximaFaxina = DateTime.MinValue;
    private static readonly System.Diagnostics.Stopwatch Relogio = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Os merchants da loja (do sinal do chat). Os testes trocam.</summary>
    public static Func<IReadOnlyList<string>> Merchants = () => ServicoConversaChat.Sinal.MerchantIds;

    /// <summary>A chamada da borda (nome, corpo) -> (status, corpo). Os testes trocam.</summary>
    public static Func<string, string, Task<(int Status, string Corpo)>> Chamada =
        (nome, corpo) => Servicos.Nuvem().FuncaoAsync(nome, corpo, ChatArquivo.Prazo);

    /// <summary>Quantas mensagens o arquivo ja leu nesta sessao (so diagnostico).</summary>
    public static long Lidas => Interlocked.Read(ref _lidas);

    /// <summary>Quantas mensagens esperam a gravacao (so diagnostico e testes).</summary>
    public static int NaFila => Volatile.Read(ref _naFila);
    private static long _lidas;

    private static string Terminal => ConversaRaspadinha.NomeDoTerminal();

    private static string Versao
    {
        get
        {
            var v = typeof(ServicoArquivoChat).Assembly.GetName().Version;
            return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>
    /// Um quadro do WebSocket capturado pelo CDP. Chamado na thread da tela, entao e barato: le, faz a
    /// triagem e passa adiante para gravar fora da tela. So o quadro RECEBIDO vira mensagem (o que a
    /// loja envia volta do servidor como MESG recebido com msg_id: sem eco dobrado).
    /// </summary>
    public static void Quadro(string? payload, bool enviado, string? conexao, string? wsUserId)
    {
        try
        {
            if (enviado || string.IsNullOrEmpty(payload)) return;
            var cmd = QuadroSendbird.Comando(payload);
            if (cmd is null || !ChatArquivo.Comandos.Contains(cmd)) return;
            Ligar();
            var agora = DateTime.Now;
            var m = ChatArquivo.LerQuadro(payload, wsUserId, null);
            var t = ChatArquivo.Triagem(m, enviado, Merchants(), agora);
            if (!t.Entra) return;
            // 1.0.24 (08/10/2026): a solicitacao de alteracao do iFood (cartao do sistema com
            // custom_type "summary": "Quero cancelar o pedido", "Quero adicionar observacao ao
            // pedido") tem 5 min para a loja responder no Gestor; o leitor da raspadinha nao a
            // enxerga. Aqui ela vira aviso com som na tela de venda, com o botao Abrir o chat.
            if (ChatArquivo.EhSolicitacaoDoCliente(t.Mensagem!))
                AvisarSolicitacao(t.Canal!, t.Mensagem!);
            Interlocked.Increment(ref _lidas);
            Interlocked.Increment(ref _naFila);
            if (!Gravacoes.Writer.TryWrite((t.Canal!, t.Mensagem!, agora))) Interlocked.Decrement(ref _naFila);
        }
        catch (Exception ex) { Diag("quadro: " + ex.GetType().Name); }
    }

    /// <summary>O aviso da solicitacao: uma vez por canal a cada 10 min, com o numero do pedido quando o KDS local o tem.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> SolicitacoesAvisadas = new(StringComparer.Ordinal);

    private static void AvisarSolicitacao(CanalDaFala canal, MensagemArquivo m)
    {
        var agora = DateTime.Now;
        if (SolicitacoesAvisadas.TryGetValue(canal.Canal, out var antes) && antes > agora.AddMinutes(-10)) return;
        SolicitacoesAvisadas[canal.Canal] = agora;
        if (SolicitacoesAvisadas.Count > 500)
            foreach (var k in SolicitacoesAvisadas.Where(x => x.Value < agora.AddHours(-2)).Select(x => x.Key).ToList())
                SolicitacoesAvisadas.TryRemove(k, out _);
        string? numero = null;
        try
        {
            using var cx = Banco.Abrir();
            numero = Dapper.SqlMapper.ExecuteScalar<string?>(cx,
                "SELECT numero FROM kds_ticket WHERE origem = 'ifood' AND ref_id = @r LIMIT 1", new { r = canal.OrderId });
        }
        catch { /* sem o numero o aviso sai mesmo assim */ }
        ServicoConversaChat.AvisoLocal("solicitacao", ChatArquivo.TextoDaSolicitacao(numero, m.Texto), canal.OrderId, numero);
        Diag($"solicitacao do cliente pedido={numero ?? "?"} len={m.Texto.Length}");
    }

    /// <summary>Os relogios nascem no primeiro quadro, uma vez so.</summary>
    private static void Ligar()
    {
        if (Interlocked.Exchange(ref _iniciado, 1) == 1) return;
        _tique = new System.Threading.Timer(_ => Tique(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _ = Task.Run(LoopDeGravacaoAsync);
        Diag("ligado");
    }

    private static async Task LoopDeGravacaoAsync()
    {
        await foreach (var (canal, m, agora) in Gravacoes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { Guardar(canal, m, agora); }
            catch (Exception ex) { Diag("gravar: " + ex.GetType().Name); }
            finally { Interlocked.Decrement(ref _naFila); }
        }
    }

    private static void Guardar(CanalDaFala canal, MensagemArquivo m, DateTime agora)
    {
        try
        {
            bool nova;
            using (var cx = Banco.Abrir()) nova = ChatArquivo.Registrar(cx, canal, m, agora);
            if (!nova) return;
            // o rastro diz O QUE aconteceu, nunca o que a pessoa escreveu
            Diag($"msg {m.Comando} {ChatArquivo.LadoTexto(m.Lado)} chave={Curta(ChatArquivo.Chave(m))} pedido={Curta(canal.OrderId)} len={m.Texto.Length}");
            if (Lote.Adicionar(agora)) _ = DescarregarAsync();
        }
        catch (Exception ex) { Diag("guardar: " + ex.GetType().Name); }
    }

    private static void Tique()
    {
        if (Interlocked.Exchange(ref _noTique, 1) == 1) return;
        try
        {
            var agora = DateTime.Now;
            if (Lote.Pronto(agora)) _ = DescarregarAsync();
            if (agora >= _proximaFaxina)
            {
                _proximaFaxina = agora.AddHours(1);
                try { using var cx = Banco.Abrir(); ChatArquivo.Faxina(cx, agora); } catch { }
            }
        }
        catch { }
        finally { Interlocked.Exchange(ref _noTique, 0); }
    }

    /// <summary>Manda as pendentes em lotes de 50, da mais antiga para a mais nova. Uma chamada por vez.</summary>
    public static async Task DescarregarAsync()
    {
        if (!await UmaChamadaPorVez.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var agora = DateTime.Now;
            if (ChatArquivo.EmSilencio(agora)) { Diag("lote segurado: captura desligada no ERP"); return; }
            var merchants = Merchants();
            if (merchants.Count == 0) { Diag("lote segurado: sem os merchants da loja (sinal do chat)"); return; }
            string corpo; IReadOnlyList<string> chaves;
            using (var cx = Banco.Abrir())
            {
                var linhas = ChatArquivo.Pendentes(cx, merchants);
                if (linhas.Count == 0) return;
                (corpo, chaves) = ChatArquivo.CorpoLote(Terminal, Versao, linhas);
            }
            var (st, resp) = await Chamada(ChatArquivo.Edge, corpo).ConfigureAwait(false);
            var r = ChatArquivo.LerRespostaLote(st, resp);
            using (var cx = Banco.Abrir()) ChatArquivo.Aplicar(cx, chaves, r, DateTime.Now);
            Diag($"lote {chaves.Count} msg(s) http={st} status={r.Status} captura={r.Captura} guardadas={r.Guardadas} repetidas={r.Repetidas} recusadas={r.Recusadas.Count}");
        }
        catch (Exception ex) { Diag("lote: " + ex.GetType().Name); }
        finally { UmaChamadaPorVez.Release(); }
    }

    /// <summary>ProgramData\PdvNativo\chat-arquivo.txt: uma linha por lote ou por mensagem. ⚠️ Nunca o texto da pessoa.</summary>
    private static void Diag(string texto)
    {
        try { Telas.HospedeWebView2.Anotar("chat-arquivo.txt", texto, Relogio.Elapsed); } catch { }
    }

    private static string Curta(string? s) => string.IsNullOrEmpty(s) ? "-" : s.Length <= 12 ? s : s[..12];
}
