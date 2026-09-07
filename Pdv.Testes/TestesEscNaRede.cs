using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O Esc no menu de seleção da rede: passo 05 do roteiro de homologação v20260819, "Venda negada,
/// rede desconhecida". O procedimento é curto e não deixa margem: "realizar uma venda e no menu de
/// seleção da rede, pressionar a tecla 'Esc' em uma solução Windows". O esperado é venda negada,
/// rede não informada, com "OPERAÇÃO CANCELADA" para a automação.
///
/// Dois buracos impediam a gravação desse passo, e é o que esta suíte segura:
///
///   1. O caixa respondia o menu sozinho quando a biblioteca mandava UMA opção só
///      (ProvedorPGWebLib.Predefinido). Numa loja de homologação com um autorizador instalado o
///      menu nunca chegava à tela, e não havia onde apertar Esc: o passo era impossível de
///      executar como está escrito, e pior, a rede saía escolhida por nós numa venda de verdade.
///      Rede gravada na Configuração continua sendo respondida sem perguntar — essa decisão é do
///      lojista, e passa pelo Conhecidos, não por esse atalho.
///
///   2. Com 5 redes ou mais o menu vira a lista rolável Venda.EscolherOpcao, que era a única
///      janela da casa sem tratamento de Esc: só o botão Voltar saía dela. Todo o resto (Dialogo,
///      PedirSenha, PedirValor, SeletorComanda, TelaQrTef) já fechava no Esc.
///
/// O caminho inteiro, para quem for reler: Servicos.PerguntarNaTelaAsync escolhe o diálogo pelo
/// número de opções, o índice -1 vira null em RespostaDaTela.Menu, e null no menu faz o provedor
/// encerrar com "operação cancelada pelo operador" — que é o que a tela de pagamento mostra como
/// "Cobrança cancelada" e "O cliente NÃO foi cobrado".
/// </summary>
public static class TestesEscNaRede
{
    public static void Rodar(Action<bool, string> checar)
    {
        var pasta = TestesPGWebLib.PastaTeste;
        Directory.CreateDirectory(pasta);

        static OpcoesPGWebLib Op(string? rede = null)
            => new("Pdv.AmericanDay", "0.5.9", "American Day", RedeCartao: rede);

        ProvedorPGWebLib Provedor(IPGWebLib f, Func<PwGetData, CancellationToken, Task<string?>> perguntar,
            Func<TransacaoPayGo, bool>? guardar = null, string? rede = null)
            => new(f, pasta, Op(rede))
            {
                IntervaloPollMs = 5,
                TempoMaxExecMs = 2000,
                TempoMaxCapturaMs = 2000,
                TempoPerguntaMs = 500,
                Guardar = guardar ?? (_ => true),
                Perguntar = perguntar,
            };

        // ── 1. rede única: o menu chega ao operador, e o Esc dele nega a venda ──
        {
            var f = new FakePGWebLib();
            var perguntas = new List<PwGetData>();
            var guardadas = new List<TransacaoPayGo>();
            // Perguntar devolvendo null é exatamente o que a tela faz no Esc: RespostaDaTela.Menu
            // com índice -1 devolve null.
            var p = Provedor(new UmaRedeSo(f), (g, _) => { perguntas.Add(g); return Task.FromResult<string?>(null); },
                guardar: t => { guardadas.Add(t); return true; });
            var d = p.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(10m), null, 1, null, CancellationToken.None)
                     .GetAwaiter().GetResult();

            checar(perguntas.Count == 1 && perguntas[0].EhMenu && perguntas[0].Identificador == PW.PWINFO_AUTHSYST
                   && perguntas[0].Opcoes is { Count: 1 },
                "menu de redes com UMA opção ainda vai para o operador (perguntas: " + perguntas.Count + ")");
            checar(!d.Pago && d.Situacao == SituacaoTef.Cancelado && d.Codigo == CodigoTef.Cancelado,
                "Esc no menu de rede única: venda negada, não aprovada (" + d.Situacao + ")");
            checar(d.Motivo == "operação cancelada pelo operador",
                "e o motivo é o que o roteiro espera ver: " + d.Motivo);
            checar(d.Motivo is not null && !d.Motivo.Contains('—'), "motivo sem travessão: " + d.Motivo);
            checar(guardadas.Count > 0 && guardadas[^1].Situacao == "cancelado",
                "a linha em tef_transacao fica 'cancelado': " + (guardadas.Count > 0 ? guardadas[^1].Situacao : "nenhuma"));
            checar(f.Confirmadas.Count == 0, "e nenhum CNF foi mandado à biblioteca");
        }

        // ── 2. o atalho continua valendo para quem GRAVOU a rede na Configuração ──
        {
            var f = new FakePGWebLib { SempreMenuRede = true };
            var perguntas = new List<PwGetData>();
            var p = Provedor(new UmaRedeSo(f), (g, _) => { perguntas.Add(g); return Task.FromResult<string?>(null); }, rede: "REDE");
            var d = p.CobrarAsync(TipoTef.Credito, Dinheiro.DeReais(10m), null, 1, null, CancellationToken.None)
                     .GetAwaiter().GetResult();

            checar(d.Pago && perguntas.Count == 0,
                "com a rede gravada na Configuração o menu de uma opção não para a venda (" + d.Motivo + ")");
            checar(f.Ultima?.Params.GetValueOrDefault(PW.PWINFO_AUTHSYST) == "REDE",
                "e a rede que foi para a biblioteca é a do lojista");
        }

        // ── 3. o Esc nas duas janelas por onde o menu de redes pode sair ──
        // A escolha entre elas é do Servicos.PerguntarNaTelaAsync: até 4 opções o diálogo da casa,
        // 5 ou mais a lista rolável da venda. Homologação lista o que o terminal tiver instalado,
        // então as duas têm que sair no Esc.
        Exception? erro = null;
        try { HostWpf.Executar(() => Janelas(checar)); }
        catch (Exception ex) { erro = ex; }
        checar(erro is null, "tela: as janelas de escolha subiram e os passos rodaram (" + (erro?.ToString() ?? "ok") + ")");
    }

    private static void Janelas(Action<bool, string> checar)
    {
        var host = new Window
        {
            Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0,
        };
        host.Show();
        try
        {
            // 5 redes = a lista rolável (Venda.EscolherOpcao)
            var muitas = new[] { "REDE", "CIELO", "STONE", "GETNET", "PIX ITAU" };
            var abriu = false;
            var saiuNoEsc = false;
            var precisouDoBotao = false;
            QuandoAbrir(host, d =>
            {
                abriu = true;
                var fechou = false;
                d.Closed += (_, _) => fechou = true;
                Escapar(d);
                saiuNoEsc = fechou;
                // Sem o Esc a janela fica de pé e o ShowDialog nunca volta: o teste ficaria
                // pendurado em vez de falhar. Sai pelo botão e acusa.
                if (!fechou)
                {
                    precisouDoBotao = true;
                    Clicar(Descendentes<Button>(d).First(b => (string)b.Content == "Voltar"));
                }
            });
            var idx = EscolherOpcao(host, "Rede da maquininha", "Qual rede vai levar esta venda?", muitas);
            checar(abriu, "a lista rolável de redes abriu sobre a venda");
            checar(saiuNoEsc && !precisouDoBotao,
                "Esc fecha a lista rolável de redes (passo 05)" + (precisouDoBotao ? ": só o botão Voltar fechou" : ""));
            checar(idx == -1, "e a escolha volta -1, que é o que cancela a cobrança: " + idx);

            // até 4 redes = o diálogo da casa, que já fechava no Esc; fica aqui para o passo 05
            // não depender de quantas credenciadoras o terminal listar
            var poucas = new[] { "REDE", "CIELO", "STONE", "GETNET" };
            var saiuNoEsc4 = false;
            var precisouDoBotao4 = false;
            QuandoAbrir(host, d =>
            {
                var fechou = false;
                d.Closed += (_, _) => fechou = true;
                Escapar(d);
                saiuNoEsc4 = fechou;
                if (!fechou)
                {
                    precisouDoBotao4 = true;
                    Clicar(Descendentes<Button>(d).First(b => (string)b.Content == "Voltar"));
                }
            });
            var idx4 = Pdv.Telas.Dialogo.Escolher(host, "Rede da maquininha", "Qual rede vai levar esta venda?", poucas);
            checar(saiuNoEsc4 && !precisouDoBotao4, "Esc também fecha o diálogo de até 4 redes");
            checar(idx4 == -1, "e devolve -1 do mesmo jeito: " + idx4);

            // o -1 das duas é o que o provedor lê como cancelamento
            var menu = new PwGetData(PW.PWDAT_MENU, PW.PWINFO_AUTHSYST, "REDE",
                muitas.Select(r => new PwOpcaoMenu(r, r)).ToList());
            checar(RespostaDaTela.Menu(menu, idx) is null && RespostaDaTela.Menu(menu, idx4) is null,
                "índice -1 vira null, e null no menu é o que faz o provedor cancelar");
        }
        finally { host.Close(); }
    }

    /// <summary>
    /// A biblioteca de mentira, só que a loja tem UMA credenciadora instalada: o menu de redes
    /// chega com uma opção. É a condição do passo 05 numa loja de homologação recém instalada, e
    /// mora aqui em vez de no FakePGWebLib porque é deste passo — o resto da bateria continua
    /// vendo as três redes de sempre.
    /// </summary>
    private sealed class UmaRedeSo : IPGWebLib
    {
        private readonly FakePGWebLib _f;
        public UmaRedeSo(FakePGWebLib f) => _f = f;

        public short ExecTransac(out IReadOnlyList<PwGetData> pedidos)
        {
            var r = _f.ExecTransac(out pedidos);
            pedidos = pedidos
                .Select(p => p.EhMenu && p.Identificador == PW.PWINFO_AUTHSYST && p.Opcoes is { Count: > 1 }
                    ? p with { Opcoes = new[] { p.Opcoes[0] } }
                    : p)
                .ToList();
            return r;
        }

        public short Init(string diretorioTrabalho) => _f.Init(diretorioTrabalho);
        public void End() => _f.End();
        public short NewTransac(byte operacao) => _f.NewTransac(operacao);
        public short AddParam(ushort info, string valor) => _f.AddParam(info, valor);
        public short GetResult(ushort info, out string valor) => _f.GetResult(info, out valor);
        public short Confirmation(uint resultado, string reqNum, string locRef, string extRef, string virtMerch, string authSyst)
            => _f.Confirmation(resultado, reqNum, locRef, extRef, virtMerch, authSyst);
        public short WaitConfirmation() => _f.WaitConfirmation();
        public short IdleProc() => _f.IdleProc();
        public short GetOperations(byte tipoOperacao, out IReadOnlyList<PwOperacao> operacoes) => _f.GetOperations(tipoOperacao, out operacoes);
        public short SetEnvironment(short ambiente) => _f.SetEnvironment(ambiente);
        public short PPGetCard(ushort indice) => _f.PPGetCard(indice);
        public short PPGetPIN(ushort indice) => _f.PPGetPIN(indice);
        public short PPGetData(ushort indice) => _f.PPGetData(indice);
        public short PPGoOnChip(ushort indice) => _f.PPGoOnChip(indice);
        public short PPFinishChip(ushort indice) => _f.PPFinishChip(indice);
        public short PPConfirmData(ushort indice) => _f.PPConfirmData(indice);
        public short PPPositiveConfirmation(ushort indice) => _f.PPPositiveConfirmation(indice);
        public short PPRemoveCard() => _f.PPRemoveCard();
        public short PPGenericCMD(ushort indice) => _f.PPGenericCMD(indice);
        public short PPEventLoop(out string display) => _f.PPEventLoop(out display);
        public short PPAbort() => _f.PPAbort();
        public short TransactionInquiry(string xmlRequisicao, out string xmlResposta) => _f.TransactionInquiry(xmlRequisicao, out xmlResposta);
    }

    /// <summary>Venda.EscolherOpcao é internal: a suíte é outro assembly.</summary>
    private static int EscolherOpcao(Window dono, string titulo, string pergunta, string[] opcoes)
    {
        var m = typeof(Pdv.Telas.Venda).GetMethod("EscolherOpcao", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException("Pdv.Telas.Venda", "EscolherOpcao");
        return (int)m.Invoke(null, new object?[] { dono, titulo, pergunta, opcoes })!;
    }

    /// <summary>
    /// Aperta Esc na janela. O evento nasce no primeiro botão, não na janela: assim ele tem que
    /// SUBIR até o KeyDown dela, que é o caminho que o dedo do operador faz.
    /// </summary>
    private static void Escapar(Window d)
    {
        var fonte = PresentationSource.FromVisual(d)
                    ?? throw new InvalidOperationException("a janela não tem PresentationSource (não chegou a aparecer)");
        UIElement alvo = Descendentes<Button>(d).FirstOrDefault() ?? (UIElement)d;
        alvo.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, fonte, 0, Key.Escape)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        });
    }

    private static void Clicar(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    /// <summary>
    /// Espera o próximo diálogo modal abrir sobre o host e age nele. Timer do Dispatcher porque o
    /// ShowDialog bloqueia quem chamou: o laço aninhado dele é o que dispara o timer.
    /// </summary>
    private static void QuandoAbrir(Window host, Action<Window> acao)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        var tentativas = 0;
        timer.Tick += (_, _) =>
        {
            var d = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w != host && w.Owner == host && w.IsVisible);
            if (d is null)
            {
                if (++tentativas > 50) timer.Stop();
                return;
            }
            timer.Stop();
            acao(d);
        };
        timer.Start();
    }

    private static IEnumerable<T> Descendentes<T>(DependencyObject raiz) where T : DependencyObject
    {
        foreach (var filho in LogicalTreeHelper.GetChildren(raiz))
        {
            if (filho is not DependencyObject d) continue;
            if (d is T t) yield return t;
            foreach (var neto in Descendentes<T>(d)) yield return neto;
        }
    }
}
