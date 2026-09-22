using Pdv.Nucleo;

namespace Pdv;

/// <summary>
/// O SERVIÇO VIVO do código da raspadinha no chat do iFood (22/09/2026, pedido do dono):
/// "capturar quando o cliente enviar o código de resgate da raspadinha, validar e gerar uma
/// comanda avisando do bônus".
///
/// Quem ALIMENTA é a tela do chat: cada mensagem nova do cliente (o quadro do WebSocket que o
/// CDP já capturava e, com o chat aberto, também o DOM) cai aqui. Quem DECIDE é o servidor: este
/// arquivo não procura código em texto nenhum, não valida formato e não resgata nada. Ele grava a
/// mensagem, manda para a borda e age pela resposta.
///
/// O QUE ELE FAZ COM A RESPOSTA:
///  · tem bônus: tira a COMANDA (o papel é o pedido do dono) e avisa na tela, porque papel falta;
///  · não tinha código: silêncio absoluto, que é a conversa normal do cliente;
///  · tinha código e não vale: avisa na tela em uma linha, sem papel;
///  · sem internet: a mensagem fica na fila e sai quando a rede voltar (Drenagem), e a comanda
///    sai na varredura de impressão seguinte.
///
/// NÃO MARTELAR é regra desta camada: a mesma mensagem não vira duas chamadas (a chave é PK no
/// SQLite) e o mesmo bônus não vira duas comandas (a linha do bônus é PK e a impressão é
/// reivindicada antes do papel). O servidor também é idempotente, mas isso é a segunda rede,
/// não a primeira.
///
/// DESLIGADO NA LOJA = NADA ACONTECE. A chave vem do painel; sem ela, nem a mensagem é gravada.
/// </summary>
public static class ServicoRaspadinhaChat
{
    /// <summary>Uma linha para o operador ler (toast da tela de venda). Só quando há o que dizer.</summary>
    public static event Action<string>? Avisou;

    /// <summary>Um bônus novo entrou. A tela do chat usa para oferecer a reimpressão.</summary>
    public static event Action<BonusRaspadinha>? BonusNovo;

    /// <summary>Uma chamada por vez: duas capturas quase juntas não viram duas idas à rede.</summary>
    private static readonly SemaphoreSlim UmaPorVez = new(1, 1);
    private static readonly SemaphoreSlim UmPapelPorVez = new(1, 1);

    private static bool _ligado;
    private static DateTime _ligadoAte = DateTime.MinValue;

    /// <summary>
    /// A loja ligou a captura? A tela do chat pergunta a cada 7 s, e abrir o banco nesse ritmo na
    /// thread da tela é custo à toa: a resposta vale por 30 s. Ligar no painel demora no máximo
    /// isso para valer aqui, e a sincronização com o painel é bem mais lenta que 30 s.
    ///
    /// Isto é só um PORTÃO BARATO. Quem decide de verdade é o <c>Registrar</c>, que relê a config
    /// dentro da própria transação: a janela do cache nunca grava mensagem de loja desligada.
    /// </summary>
    public static bool Ligado()
    {
        if (DateTime.Now < _ligadoAte) return _ligado;
        try
        {
            using var cx = Banco.Abrir();
            _ligado = ChatRaspadinha.LigadoNaLoja(cx);
        }
        catch { _ligado = false; }
        _ligadoAte = DateTime.Now.AddSeconds(30);
        return _ligado;
    }

    private static string? _loja;
    private static DateTime _lojaAte = DateTime.MinValue;

    /// <summary>
    /// O nome da loja deste terminal (vai no corpo da chamada). Null quando não pareado.
    ///
    /// Em cache por 30 s pelo mesmo motivo do <see cref="Ligado"/>: cada mensagem capturada abria o
    /// banco só para reler uma coluna que não muda, e o leitor de DOM manda a conversa visível a
    /// cada 7 s. Pareamento de terminal não acontece no meio do expediente.
    /// </summary>
    private static string? Loja()
    {
        if (DateTime.Now < _lojaAte) return _loja;
        try
        {
            using var cx = Banco.Abrir();
            _loja = Dapper.SqlMapper.ExecuteScalar<string?>(cx, "SELECT loja_nome FROM terminal LIMIT 1");
        }
        catch { _loja = null; }
        _lojaAte = DateTime.Now.AddSeconds(30);
        return _loja;
    }

    /// <summary>
    /// O número curto do iFood só vai para a borda quando ESTA loja tem esse pedido.
    ///
    /// ⚠️ A loja mandada na chamada é sempre a do TERMINAL, e a mesma conta do Gestor enxerga
    /// Savassi e Castelo: a conversa do Castelo aberta num caixa da Savassi manda o número do
    /// Castelo com a loja da Savassi. Se o mesmo número curto existir nas duas, o servidor casa o
    /// bônus com o pedido errado e a comanda manda entregar o brinde na sacola de outra pessoa.
    ///
    /// A conferência é contra os pedidos de delivery que ESTE caixa recebeu (kds_ticket, origem
    /// ifood): número que não está aqui não vai. Sem número, a comanda diz "Pedido: confira no
    /// chat", que é ruim, mas é MUITO melhor do que o número de outra pessoa.
    /// </summary>
    private static string? PedidoDestaLoja(string? pedido)
    {
        var n = SoDigitos(pedido);
        if (n.Length == 0) return null;
        try
        {
            using var cx = Banco.Abrir();
            // o número do card pode vir com enfeite ("#5592", "iFood 5592"): compara só os dígitos,
            // do mesmo jeito que a busca do quadro do KDS já compara
            var numeros = Dapper.SqlMapper.Query<string>(cx,
                "SELECT numero FROM kds_ticket WHERE origem = 'ifood' AND criado_em >= @Desde",
                new { Desde = DateTime.Now.AddDays(-2).ToString("o") });
            return numeros.Any(x => SoDigitos(x) == n) ? n : null;
        }
        catch { return null; }
    }

    private static string SoDigitos(string? s) => new string((s ?? "").Where(char.IsDigit).ToArray());

    /// <summary>
    /// Uma mensagem capturada no chat. Devolve a resposta (ou null quando nada foi feito: a loja
    /// não ligou, a fala é da própria loja, ou esta mensagem já tinha sido vista). NUNCA lança:
    /// captura de chat não pode derrubar o caixa.
    /// </summary>
    public static async Task<RespostaChat?> OuvirAsync(MensagemChat? m, string? meuUserId,
        string? pedido = null, string? cliente = null, string origem = ChatRaspadinha.OrigemCaixa)
    {
        try
        {
            if (m is null || !ChatRaspadinha.DoCliente(m, meuUserId)) return null;
            // A FALA VELHA NÃO RESGATA. O leitor de DOM manda a rolagem visível inteira, e abrir
            // hoje uma conversa de três dias atrás queimaria um código que ninguém tratou na época.
            if (!ChatRaspadinha.Recente(m, DateTime.Now)) return null;
            if (!Ligado()) return null;

            var pedida = new MensagemDoChat(ChatRaspadinha.Chave(m), m.Texto, Loja(),
                PedidoDestaLoja(pedido), null, cliente, origem);
            // grava e enfileira na mesma transação; false = já vista (ou desligada): nada a fazer
            if (!ChatRaspadinha.Registrar(pedida)) return null;

            return await EnviarEAgirAsync(pedida.Chave).ConfigureAwait(false);
        }
        catch { return null; }
    }

    /// <summary>
    /// Manda a mensagem já gravada e age pela resposta: papel quando há bônus, uma linha na tela
    /// quando há o que dizer, silêncio quando não havia código.
    /// </summary>
    private static async Task<RespostaChat?> EnviarEAgirAsync(string chave)
    {
        await UmaPorVez.WaitAsync().ConfigureAwait(false);
        RespostaChat r;
        try { r = await new ChatRaspadinha(Servicos.Nuvem()).EnviarAsync(chave).ConfigureAwait(false); }
        catch { return null; }
        finally { UmaPorVez.Release(); }

        if (r.Desfecho == DesfechoChat.Bonus && r.Bonus is { } b)
        {
            BonusNovo?.Invoke(b);
            await ImprimirPendentesAsync().ConfigureAwait(false);
        }
        // O aviso vem DEPOIS do papel: se a impressora falhar, a linha da tela já conta o que
        // aconteceu, e ela é a única coisa que sobra quando falta bobina.
        var linha = ChatRaspadinha.TextoDeTela(r);
        if (linha is not null) Avisou?.Invoke(linha);
        return r;
    }

    /// <summary>
    /// Tira o papel dos bônus que ainda não saíram. Roda depois de cada resposta com bônus e
    /// também nas puxadas do delivery, porque o bônus pode ter nascido na FILA (a mensagem foi
    /// capturada sem internet) e aí ninguém estava olhando a resposta.
    ///
    /// Devolve a frase do primeiro papel que não saiu, ou null. Nunca lança: imprimir é conforto,
    /// o registro no servidor é a verdade.
    /// </summary>
    public static async Task<string?> ImprimirPendentesAsync()
    {
        if (!await UmPapelPorVez.WaitAsync(0).ConfigureAwait(false)) return null;
        try
        {
            var pendentes = ChatRaspadinha.ParaImprimir();
            if (pendentes.Count == 0) return null;

            Impressao.Destino destino; PoliticaImpressao politica;
            using (var cx = Banco.Abrir())
            {
                // "NÃO IMPRIMIR" VALE TAMBÉM PARA O BRINDE. A loja que desligou a comanda via o
                // papel do brinde sair assim mesmo, e o claim queimava junto: o bônus sumia da
                // lista de pendentes sem nada ter saído.
                //
                // ⚠️ "PERGUNTAR" CONTINUA IMPRIMINDO, e é de propósito. Na comanda de cozinha
                // "perguntar" significa "o 🖨 do card está aí"; o brinde não tem card nenhum, e o
                // pedido do dono é justamente que a comanda SAIA quando o código chega. Tratar
                // "perguntar" como "não" desligaria o recurso inteiro em toda loja que nunca
                // configurou nada, porque a chave antiga ausente cai em "perguntar".
                politica = Impressoes.Politica(cx, Impressoes.Comanda);
                destino = Servicos.DestinoDaComanda(cx);
            }
            if (politica == PoliticaImpressao.Nao) return null;

            string? falha = null;
            foreach (var b in pendentes)
            {
                // claim ANTES do papel: a puxada do delivery e a volta da rede se sobrepõem, e
                // comanda dobrada é brinde dobrado. Falhou depois do claim, o claim VOLTA (com a
                // tentativa contada, ver ChatRaspadinha.AnotarFalhaDeImpressao) e a varredura
                // seguinte tenta de novo até o teto; passado o teto, o botão Reimprimir recupera.
                if (!ChatRaspadinha.ReivindicarImpressao(b.Id)) continue;
                var erro = await ImprimirAsync(b, destino).ConfigureAwait(false);
                if (erro is null) { ChatRaspadinha.ConfirmarImpressao(b.Id); continue; }
                ChatRaspadinha.AnotarFalhaDeImpressao(b.Id, erro);
                falha ??= "A comanda do brinde não saiu. Anote o código e fale com o gerente.";
            }
            if (falha is not null) Avisou?.Invoke(falha);
            return falha;
        }
        catch { return null; }
        finally { UmPapelPorVez.Release(); }
    }

    /// <summary>
    /// O que o botão "Reimprimir brinde" faz: tira o papel de TODOS os bônus de hoje que não estão
    /// no papel, do mais antigo para o mais novo. Devolve quantos saíram e a frase do erro.
    ///
    /// Era o ÚLTIMO bônus, um só. Numa sexta com a bobina acabada chegavam três códigos em vinte
    /// minutos, a impressão falhava nos três, e trocada a bobina o botão trazia só o terceiro: os
    /// dois primeiros existiam no banco, com o brinde já queimado no servidor, e não havia tela
    /// nenhuma que os mostrasse.
    /// </summary>
    public static async Task<(int Saiu, string? Erro)> ReimprimirPendentesDoDiaAsync()
    {
        try
        {
            var lista = ChatRaspadinha.SemPapelHoje();
            if (lista.Count == 0) return (0, null);
            Impressao.Destino destino;
            using (var cx = Banco.Abrir()) destino = Servicos.DestinoDaComanda(cx);
            var saiu = 0; string? erro = null;
            foreach (var b in lista)
            {
                var e = await ImprimirAsync(b, destino).ConfigureAwait(false);
                if (e is null) { saiu++; ChatRaspadinha.ConfirmarImpressao(b.Id); }
                else erro ??= e;
            }
            return (saiu, erro);
        }
        catch { return (0, "A comanda do brinde não saiu. Tente de novo."); }
    }

    /// <summary>
    /// Reimpressão manual de UM bônus. NÃO passa pelo claim: aqui quem pede é uma pessoa olhando o
    /// papel que não saiu, e ela pode pedir quantas vezes precisar.
    /// </summary>
    public static async Task<string?> ReimprimirAsync(BonusRaspadinha b)
    {
        try
        {
            Impressao.Destino destino;
            using (var cx = Banco.Abrir()) destino = Servicos.DestinoDaComanda(cx);
            return await ImprimirAsync(b, destino).ConfigureAwait(false);
        }
        catch { return "A comanda do brinde não saiu. Tente de novo."; }
    }

    /// <summary>O papel em si: mesma largura e mesmo caminho da comanda de cozinha.</summary>
    private static Task<string?> ImprimirAsync(BonusRaspadinha b, Impressao.Destino destino)
        => Impressao.ImprimirTextoAsync(
            $"Brinde da raspadinha {b.Codigo ?? b.Id}",
            new[] { ChatRaspadinha.ComandaLinhas(b, Nucleo.Kds.ColunasComanda(destino.Papel.Colunas)) },
            destino);
}
