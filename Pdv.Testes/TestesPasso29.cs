using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// Passos 28 e 29 do roteiro v20260819, "Solicitação de dado genérico digitado".
///
/// O passo 28 faz uma venda de R$ 1001,00 no autorizador C6PAY e a biblioteca pede um dado
/// DIGITADO na tag 0x2F (PWDAT_TYPED). O passo 29 manda digitar ABC123 e cobra três coisas:
/// venda aprovada e confirmada, recibo impresso e "Mensagem para o operador: TRANSAÇÃO APROVADA".
///
/// A terceira parava a gravação. O caixa capturava o dado, mandava em PW_iAddParam e aprovava,
/// mas o desfecho de venda paga voltava com `Motivo` nulo (a administrativa já devolvia
/// `r.Mensagem`, a venda não), `MensagemParaTela` respondia vazio em tudo que estivesse pago, e
/// a tela de pagamento só lançava a parte e seguia. O operador nunca lia a resposta da rede.
///
/// O que se prova aqui:
///  · biblioteca: a tag 0x2F chega como dado digitado, o que o operador escreveu vai aparado, a
///    venda aprova, o recibo sai e o CNF vai embora;
///  · desfecho: venda paga carrega a frase da REDE, e venda paga sem frase não vira o texto de
///    falha ("não foi possível concluir o pagamento" numa venda aprovada faz cobrar de novo);
///  · digitação: o tamanho é o que a biblioteca mandou, o texto é aparado nas pontas e errar
///    faz o caixa perguntar de novo em vez de chutar;
///  · tela: a frase fica à vista no topo, some quando começa outra cobrança e nunca é inventada.
/// </summary>
public static class TestesPasso29
{
    public static void Rodar(Action<bool, string> checar)
    {
        NaBiblioteca(checar);
        NoDesfecho(checar);
        NaDigitacao(checar);
        NaTela(checar);
    }

    // ── 1. passos 28 e 29 contra a biblioteca ────────────────────────────────

    private static readonly OpcoesPGWebLib Opcoes =
        new("Pdv.AmericanDay", "0.5.9", "American Day", RedeCartao: "REDE", RedePix: "PIX ITAU");

    private static void NaBiblioteca(Action<bool, string> checar)
    {
        Directory.CreateDirectory(TestesPGWebLib.PastaTeste);

        var f = new FakePGWebLib { DadoDigitadoVezes = 1 };
        var pedidos = new List<PwGetData>();
        var impressos = new List<TransacaoPayGo>();
        var guardadas = new List<TransacaoPayGo>();
        var p = new ProvedorPGWebLib(f, TestesPGWebLib.PastaTeste, Opcoes)
        {
            IntervaloPollMs = 5,
            TempoMaxExecMs = 2000,
            TempoMaxCapturaMs = 2000,
            TempoPerguntaMs = 500,
            Guardar = t => { guardadas.Add(t); return true; },
            ImprimirComprovante = t => { impressos.Add(t); return Task.FromResult(true); },
            // O operador digita com espaço sobrando, que é o que acontece no balcão. A resposta
            // passa pela MESMA regra da tela (RespostaDaTela.Digitado, que é o que Servicos liga
            // no provedor): apara nas pontas e confere contra o tamanho que a biblioteca mandou.
            Perguntar = (g, _) => { pedidos.Add(g); return Task.FromResult(RespostaDaTela.Digitado(g, "  ABC123  ").Valor); },
        };

        var d = p.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(1001m), null, 1, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        var digitados = pedidos.Where(g => g.Identificador == FakePGWebLib.IdDadoGenerico).ToList();
        checar(digitados.Count == 1 && digitados[0].EhDigitado && !digitados[0].EhMenu,
            $"passo 28: a tag 0x2F chega ao caixa como dado DIGITADO, não como menu (chegaram {digitados.Count})");
        checar(digitados.Count == 1 && digitados[0].Prompt == "DIGITE O DADO:" && digitados[0].TamanhoMaximo == 6,
            "o prompt e o tamanho da caixa são os que a biblioteca mandou, não os da casa");

        var enviado = f.Ultima?.Params.GetValueOrDefault(FakePGWebLib.IdDadoGenerico);
        checar(enviado == "ABC123", $"passo 29: ABC123 vai aparado em PW_iAddParam(0x2F) (foi \"{enviado ?? "(nada)"}\")");
        checar(f.Chamadas.IndexOf($"AddParam({FakePGWebLib.IdDadoGenerico}=ABC123)") > f.Chamadas.IndexOf("ExecTransac"),
            "a tag 0x2F só é enviada DEPOIS de pedida (o roteiro reprova com DemoErroTeste3 quem adianta)");

        checar(d.Pago && d.PaymentStatus == "pago", $"passo 29: a venda aprova depois do dado digitado ({d.Situacao})");
        checar(guardadas.Any(g => g.Situacao == "pago"), "a linha 'pago' está gravada no caixa");
        checar(impressos.Count == 1, $"o recibo saiu (o roteiro pede recibo impresso): {impressos.Count}");
        checar(f.Confirmadas.Count == 1 && f.Confirmadas[0].ReqNum == f.UltimoReqNum,
            "a transação foi CONFIRMADA para a automação comercial (PW_iConfirmation com o REQNUM)");

        // ⭐ O buraco do passo 29: a venda aprovada voltava muda.
        checar(d.Motivo == "TRANSACAO AUTORIZADA",
            $"⭐ passo 29: a venda aprovada devolve a mensagem da rede (RESULTMSG), não nulo (veio \"{d.Motivo ?? "(nulo)"}\")");
        checar(d.MensagemParaTela == "TRANSACAO AUTORIZADA",
            $"⭐ e ela chega pronta para a tela (veio \"{d.MensagemParaTela}\")");
    }

    // ── 2. o desfecho que a tela lê ──────────────────────────────────────────

    private static void NoDesfecho(Action<bool, string> checar)
    {
        var pago = new DesfechoTef(SituacaoTef.Pago, "1", "chg", null, "TRANSACAO APROVADA", false)
        { Codigo = CodigoTef.Pago };
        checar(pago.MensagemParaTela == "TRANSACAO APROVADA",
            $"⭐ venda paga entrega a frase da REDE para a tela (entregou \"{pago.MensagemParaTela}\")");

        var pagoMudo = new DesfechoTef(SituacaoTef.Pago, "1", "chg", null, null, false) { Codigo = CodigoTef.Pago };
        checar(pagoMudo.MensagemParaTela.Length == 0,
            "venda paga sem frase da rede não inventa nada, e NUNCA o texto de falha");

        var recusado = new DesfechoTef(SituacaoTef.Recusado, "1", "chg", null, null, false)
        { Codigo = CodigoTef.Recusado };
        checar(recusado.MensagemParaTela == "não foi possível concluir o pagamento",
            "recusa sem motivo continua com o texto de sempre");

        var ocupado = new DesfechoTef(SituacaoTef.Timeout, "1", "chg", null, "tempo esgotado", true)
        { Codigo = CodigoTef.Timeout };
        checar(ocupado.MensagemParaTela.StartsWith("tempo esgotado", StringComparison.Ordinal)
               && ocupado.MensagemParaTela.Length > "tempo esgotado".Length,
            "o aviso da maquininha possivelmente ocupada continua colado no fim");
    }

    // ── 3. o que o operador digita ───────────────────────────────────────────

    private static void NaDigitacao(Action<bool, string> checar)
    {
        // Como o passo 28 chega: dado digitado na tag 0x2F, de 1 a 6 caracteres.
        var d = new PwGetData(PW.PWDAT_TYPED, 0x2F, "DIGITE O DADO:", TamanhoMinimo: 1, TamanhoMaximo: 6);

        checar(!RespostaDaTela.Ocultar(d) && !RespostaDaTela.EhParcelas(d),
            "dado genérico é caixa de texto à vista, não senha nem pergunta de parcelas");
        checar(RespostaDaTela.Rotulo(d) == "DIGITE O DADO:", "o rótulo da caixa é o prompt da biblioteca");
        checar(RespostaDaTela.Sugestao(d).Length == 0, "a caixa abre vazia: o valor é do operador, não do caixa");

        var ok = RespostaDaTela.Digitado(d, "  ABC123  ");
        checar(ok.Valor == "ABC123" && ok.Erro is null, $"ABC123 com espaço sobrando vai aparado (\"{ok.Valor}\")");

        var grande = RespostaDaTela.Digitado(d, "ABC1234");
        checar(grande.Valor is null && grande.Erro is not null,
            "passou do tamanho que a biblioteca mandou: avisa e pergunta de novo, não chuta");
        checar(grande.Erro is not null && !grande.Erro.Contains('—') && !grande.Erro.Contains("PWDAT")
               && !grande.Erro.Contains("0x2F"), $"o aviso é humano, sem travessão e sem jargão: \"{grande.Erro}\"");

        var vazio = RespostaDaTela.Digitado(d, "   ");
        checar(vazio.Valor is null && vazio.Erro is not null, "em branco não vai para a biblioteca");

        var desistiu = RespostaDaTela.Digitado(d, null);
        checar(desistiu.Valor is null && desistiu.Erro is null, "desistir é desistir: sem valor e sem aviso de erro");
    }

    // ── 4. a tela do caixa ───────────────────────────────────────────────────

    private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void NaTela(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-teste-passo29-{Guid.NewGuid():N}.db");
        Banco.Migrar(arquivo);
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        Exception? erro = null;
        try { HostWpf.Executar(() => PassosDaTela(checar)); }
        catch (Exception ex) { erro = ex; }
        Banco.CaminhoForcado = anterior;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(arquivo); } catch { }
        checar(erro is null, "tela: a tela de pagamento subiu e os passos rodaram (" + (erro?.ToString() ?? "ok") + ")");
    }

    private static void PassosDaTela(Action<bool, string> checar)
    {
        var op = new Operador("passo29", "Tela", "operador");
        var sessao = new Sessao("sessao-passo29", Caixa.DiaOperacional(), op.Id, op.Nome, DateTime.Now, Dinheiro.Zero);
        var itens = new List<LinhaVenda>
        {
            new("p1", "SKU1", "DONUT", Quantidade.Um, Dinheiro.DeReais(10), Dinheiro.DeReais(10), "UN", null, null, null, null, 0),
        };
        var tef = new TefDeRoteiro();
        var host = new Window
        {
            Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0,
        };
        var tela = new Pdv.Telas.Pagamento(op, sessao, itens, new EmissorMudo(), tef, "Loja", null);
        host.Content = tela;
        host.Show();

        var linha = (TextBlock)typeof(Pdv.Telas.Pagamento).GetField("TxtRecadoTef", Priv)!.GetValue(tela)!;
        checar(linha.Visibility == Visibility.Collapsed && linha.Text.Length == 0,
            "antes de cobrar, a tela não fala em nome da maquininha");

        // R$ 4,00 de R$ 10,00: a venda NÃO fecha, então a tela fica parada onde o operador a vê.
        tef.Proximo = new DesfechoTef(SituacaoTef.Pago, "pid-1", "chg-1", null, "TRANSACAO APROVADA", false)
        { Codigo = CodigoTef.Pago, PaymentStatus = "pago" };
        Cobrar(tela, "debito", Dinheiro.DeReais(4));
        checar(linha.Visibility == Visibility.Visible && linha.Text == "✓ TRANSACAO APROVADA",
            $"⭐ passo 29: depois de aprovar, a tela mostra a frase da rede (mostrou \"{linha.Text}\")");
        checar(Partes(tela).Count == 1, "e o pagamento foi lançado do mesmo jeito de sempre");

        // Cobrança nova apaga a frase da anterior: "aprovada" em cima de uma recusa faria o
        // operador entregar o produto sem receber.
        tef.Proximo = new DesfechoTef(SituacaoTef.Recusado, null, "chg-2", null, "SALDO INSUFICIENTE", false)
        { Codigo = CodigoTef.Recusado };
        Cobrar(tela, "debito", Dinheiro.DeReais(3));
        checar(linha.Visibility == Visibility.Collapsed,
            $"cobrança recusada não herda o \"aprovada\" da anterior (ficou \"{linha.Text}\")");
        checar(Partes(tela).Count == 1, "e a recusa não lança pagamento nenhum");

        // Provedor que aprova sem mandar frase: a tela cala a boca em vez de inventar uma.
        tef.Proximo = new DesfechoTef(SituacaoTef.Pago, "pid-3", "chg-3", null, null, false)
        { Codigo = CodigoTef.Pago, PaymentStatus = "pago" };
        Cobrar(tela, "debito", Dinheiro.DeReais(3));
        checar(linha.Visibility == Visibility.Collapsed && Partes(tela).Count == 2,
            $"provedor que não mandou frase não faz a tela inventar uma (ficou \"{linha.Text}\")");

        host.Close();
    }

    private static List<PagamentoVenda> Partes(Pdv.Telas.Pagamento tela)
        => (List<PagamentoVenda>)typeof(Pdv.Telas.Pagamento).GetField("_partes", Priv)!.GetValue(tela)!;

    /// <summary>
    /// Chama a cobrança de TEF da tela e espera ela terminar sem travar o Dispatcher: o
    /// `Progress&lt;AndamentoTef&gt;` da tela devolve pela fila da UI, então é preciso deixar a fila
    /// andar em vez de bloquear na Task.
    /// </summary>
    private static void Cobrar(Pdv.Telas.Pagamento tela, string forma, Dinheiro valor)
    {
        var metodo = typeof(Pdv.Telas.Pagamento).GetMethod("CobrarNoTefAsync", Priv)!;
        var t = (Task)metodo.Invoke(tela, new object?[] { forma, valor, 1 })!;
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (!t.IsCompleted && DateTime.UtcNow < limite)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        t.GetAwaiter().GetResult();
        // Deixa a fila da UI terminar (o Progress reporta por Post).
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    /// <summary>Maquininha de mentira: devolve o desfecho que o teste armou.</summary>
    private sealed class TefDeRoteiro : IProvedorTef
    {
        public DesfechoTef Proximo = new(SituacaoTef.Recusado, null, "chg-0", null, "nada armado", false)
        { Codigo = CodigoTef.Recusado };

        public string Nome => "roteiro";

        public Task<DesfechoTef> CobrarAsync(TipoTef tipo, Dinheiro valor, string? documento,
            int parcelas, IProgress<AndamentoTef>? andamento, CancellationToken ct)
            => Task.FromResult(Proximo);
    }

    /// <summary>Emissor que nunca é chamado nestes passos; existe porque a tela exige um.</summary>
    private sealed class EmissorMudo : IEmissorFiscal
    {
        public Task<SaudeEmissor> SondarAsync(CancellationToken ct)
            => Task.FromResult(new SaudeEmissor(false, null, null, null, 0, null, "mudo"));

        public Task<ResultadoEmissao> EmitirAsync(IReadOnlyList<ItemFiscal> itens,
            IReadOnlyList<PagamentoFiscal> pagamentos, string? documento, CancellationToken ct)
            => Task.FromResult(ResultadoEmissao.ForaDoAr("teste", "mudo"));
    }
}
