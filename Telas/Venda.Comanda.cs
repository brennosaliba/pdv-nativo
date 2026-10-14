using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// COMANDA ABERTA NA TELA DE VENDA (13/10/2026). Pedido do dono: "agora vamos adicionar a funcao
/// comanda no PDV, abre a comanda e fecha pra pagamento.. e a comanda tambem permite solicitar
/// itens da cozinha!"
///
/// A tela de venda tem DOIS MODOS, e e a mesma tela:
///  · BALCAO (hoje): <see cref="_contaAberta"/> e nulo. Carrinho solto, rascunho, Finalizar.
///  · COMANDA: <see cref="_contaAberta"/> aponta para uma conta de ContasAbertas. O carrinho em
///    memoria E a conta carregada: cada PintarComanda grava em conta_aberta (e apaga o rascunho),
///    o cabecalho vira "12 · Mesa 5" numa faixa ciano, a linha do cupom ganha Guardar e "Cozinha N",
///    e o Finalizar vira "Fechar conta" (o Pagamento de sempre, com a conta inteira).
///
/// Tudo que decide mora no nucleo (ContasAbertas): aqui so se escolhe, se pinta e se pergunta. Os
/// textos vem de ContasAbertas.Textos. O nome "comanda" no codigo continua sendo o carrinho
/// (ItemComanda, _comanda, PintarComanda); a entidade nova e a CONTA (_contaAberta).
/// </summary>
public partial class Venda
{
    /// <summary>A conta carregada na tela (null = balcao). A coluna da direita E ela.</summary>
    private ContaAberta? _contaAberta;
    /// <summary>As conferencias do Fechar conta ja passaram nesta chamada do Finalizar.</summary>
    private bool _contaConferida;
    private bool _conferindoConta;
    private DispatcherTimer? _cutucaCozinha;

    private bool TurnoDeTeste => _homologacao || _sessao.Teste;

    // ── handlers do XAML ────────────────────────────────────────────────────

    /// <summary>Botao "Comandas" do cabecalho da coluna: a lista das abertas, ou a face Nova comanda.</summary>
    private void AbrirComandas(object sender, RoutedEventArgs e) => AbrirDialogoComandas();

    /// <summary>Botao "Guardar": solta a conta e o caixa volta ao balcao com o carrinho vazio.</summary>
    private void GuardarComanda(object sender, RoutedEventArgs e)
    {
        if (_contaAberta is null) return;
        var numero = _contaAberta.Numero;
        GuardarConta();
        AvisoLeve(ContasAbertas.Textos.ContaGuardada(numero), "neutro");
    }

    /// <summary>Botao "Cozinha N": o lote com o que ainda nao foi pedido.</summary>
    private void PedirCozinhaDaComanda(object sender, RoutedEventArgs e) => PedirCozinhaDaConta();

    /// <summary>
    /// A coluna da direita mudou de largura: os toasts (delivery, chat, WhatsApp, brinde, aviso)
    /// ancoram na coluna dos PRODUTOS, 14 px a esquerda da coluna da comanda. Nenhum toast cobre
    /// o botao Comandas nem o titulo da conta.
    /// </summary>
    private void ColunaComandaRedimensionou(object sender, SizeChangedEventArgs e) => AncorarToasts(e.NewSize.Width);

    private void AncorarToasts(double larguraDaColuna)
    {
        var direita = Math.Max(14, larguraDaColuna + 14);
        foreach (var t in new[] { ToastKds, ToastWhatsApp, AvisoWhatsAppCaido, ToastChat, ToastBrinde, ToastConversa, ToastAviso })
            t.Margin = new Thickness(0, t.Margin.Top, direita, 0);
    }

    /// <summary>AvisoLeve com tom: "ok" (verde), "erro" (vermelho) ou "neutro" (a borda da casa).</summary>
    private void AvisoLeve(string texto, string tom)
    {
        AvisoLeve(texto);
        ToastAviso.SetResourceReference(Border.BorderBrushProperty, tom switch { "ok" => "Ok", "erro" => "Erro", _ => "Borda" });
    }

    // ── o dialogo Comandas ─────────────────────────────────────────────────

    private void AbrirDialogoComandas()
    {
        var dono = Window.GetWindow(this)!;
        if (TefEmAndamento(dono)) return;
        var teste = TurnoDeTeste;
        int mesas;
        try
        {
            using var cx = Banco.Abrir();
            mesas = ContasAbertas.Mesas(cx);
            // a regra opcional conta_vazia_horas: conta vazia sem toque ha mais que isso some sozinha
            if (ContasAbertas.PrazoContaVazia(cx) is { } prazo) ContasAbertas.CancelarVaziasAutomatico(cx, _operador, prazo);
        }
        catch { mesas = ContasAbertas.MesasPadrao; }

        IReadOnlyList<LinhaDaLista> Linhas()
        {
            try
            {
                using var cx = Banco.Abrir();
                return ContasAbertas.LinhasDaLista(cx, _sessao, _contaAberta?.Id, teste);
            }
            catch { return Array.Empty<LinhaDaLista>(); }
        }
        string? MandarDeNovo(string loteId)
        {
            try
            {
                using var cx = Banco.Abrir();
                ContasAbertas.ReenviarLote(cx, loteId, _operador);
                CutucarDreno();
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // revisao 14/10: UNIDADES, como o chip da tela ("3 itens"), nao linhas
        var carrinhoSolto = _contaAberta is null ? _comanda.Sum(i => i.Qtd.Milesimos) / 1000m : 0m;
        var decisao = DialogoComandas.Mostrar(dono, Linhas, mesas, carrinhoSolto, _contaAberta, MandarDeNovo);
        // revisao 14/10: o Mandar de novo da comanda que esta na tela muda o botao Cozinha e as sub-linhas
        if (_contaAberta is not null) PintarComanda();
        switch (decisao)
        {
            case DialogoComandas.Carregar c: TocouNaConta(c.ContaId); break;
            case DialogoComandas.Nova n: AbrirConta(n.Nome, n.Consumo, n.ComCarrinho); break;
            case DialogoComandas.CancelarConta x: CancelarContaComAutorizacao(x.ContaId); break;
        }
    }

    /// <summary>Toque numa linha da lista: a conta ja na tela so fecha o dialogo; outra conta e carregada.</summary>
    private void TocouNaConta(string id)
    {
        if (_contaAberta?.Id == id) return;   // uma olhada na lista nao mata a liberacao 2FA
        var dono = Window.GetWindow(this)!;
        if (_contaAberta is null && _comanda.Count > 0)
        {
            // carrinho solto cheio: nunca entra na outra conta em silencio
            ContaAberta? alvo;
            using (var cx = Banco.Abrir()) alvo = ContasAbertas.Ler(cx, id);
            var numero = alvo?.Numero ?? 0;
            // revisao 14/10: so mexe no carrinho se a comanda ainda carrega (aberta, livre, do mesmo modo)
            if (alvo is null || !alvo.Aberta || ContasAbertas.EmUsoPorOutroPdv(alvo) || alvo.Teste != TurnoDeTeste)
            {
                CarregarConta(id);   // so para dizer o motivo; o carrinho fica como esta
                return;
            }
            var unidades = _comanda.Sum(i => i.Qtd.Milesimos) / 1000m;
            var escolha = Dialogo.Escolher(dono, $"Comanda {numero}", ContasAbertas.Textos.CarrinhoTem(unidades),
                ContasAbertas.Textos.GuardarEAbrir(numero), ContasAbertas.Textos.LevarPara(numero));
            if (escolha < 0) return;
            if (escolha == 0)
            {
                // o carrinho vira uma conta nova automatica (numero novo, sem nome, na loja) e fica guardada
                var guardada = GuardarCarrinhoNumaComanda(dono, "Comandas");
                if (guardada is null) return;
                if (CarregarConta(id)) AvisoLeve(ContasAbertas.Textos.CarrinhoFicouNa(guardada.Value), "neutro");
                else Dialogo.Avisar(dono, "Comandas", ContasAbertas.Textos.CarrinhoFicouNa(guardada.Value), "erro");
                return;
            }
            // "Levar para a N": os itens do carrinho entram na conta carregada. Revisao 14/10: o cupom do
            // carrinho vai junto (a conta so guarda um: com outro cupom la, nao leva) e item simples soma
            // na linha que a conta ja tem, como o toque no produto faz (linha repetida dobrava o cupom).
            var cupomDoCarrinho = _cortesiaCodigo;
            var coberturaDoCarrinho = new Dictionary<string, int>(_cortesiaCobertura);
            if (cupomDoCarrinho is not null && alvo.CortesiaCodigo is not null
                && !string.Equals(alvo.CortesiaCodigo, cupomDoCarrinho, StringComparison.OrdinalIgnoreCase))
            {
                Dialogo.Avisar(dono, $"Comanda {numero}", ContasAbertas.Textos.ComandaJaTemCupom(numero), "erro");
                return;
            }
            var levar = _comanda.Select(i => new ItemComanda
            {
                Produto = i.Produto, Qtd = i.Qtd, Escolhas = i.Escolhas, ForaDoCardapio = i.ForaDoCardapio,
            }).ToList();
            if (!CarregarConta(id)) return;
            foreach (var i in levar)
            {
                var mesma = i.EhCombo ? null : _comanda.FirstOrDefault(x => x.Produto.Id == i.Produto.Id && !x.EhCombo);
                if (mesma is not null) mesma.Qtd = new Quantidade(mesma.Qtd.Milesimos + i.Qtd.Milesimos);
                else _comanda.Insert(0, i);
            }
            if (cupomDoCarrinho is not null && _cortesiaCodigo is null)
            {
                _cortesiaCodigo = cupomDoCarrinho;
                foreach (var kv in coberturaDoCarrinho) _cortesiaCobertura[kv.Key] = kv.Value;
                MostrarCortesiaNaTela();
            }
            PintarComanda();
            return;
        }
        if (_contaAberta is not null) GuardarConta();
        CarregarConta(id);
    }

    /// <summary>"Nova comanda": nome duplicado pergunta; com carrinho solto, "Abrir com os N itens" leva o carrinho.</summary>
    private void AbrirConta(string? nome, DestinoCozinha consumo, bool comCarrinho)
    {
        var dono = Window.GetWindow(this)!;
        ContaAberta? igual = null;
        try { using var cx = Banco.Abrir(); igual = ContasAbertas.PorNome(cx, nome, TurnoDeTeste); } catch { }
        if (igual is not null)
        {
            var escolha = Dialogo.Escolher(dono, ContasAbertas.Textos.JaEstaAberta(igual.Nome ?? nome ?? ""),
                $"Comanda {igual.Numero}, {ContasAbertas.Textos.ItensEValor(igual.Itens.Sum(i => i.QtdMilesimos) / 1000m, igual.ValorTabela)}",
                $"Abrir a {igual.Numero}", ContasAbertas.Textos.CriarOutra(igual.Nome ?? nome ?? ""));
            if (escolha < 0) return;
            if (escolha == 0) { TocouNaConta(igual.Id); return; }
        }
        if (_contaAberta is not null) GuardarConta();
        // "Abrir vazia" com o carrinho solto cheio: o carrinho vira uma comanda automatica ANTES da nova,
        // e se isso falhar nada abre (revisao 13/10: antes o erro era engolido e o carrinho sumia calado)
        long? carrinhoFicouNa = null;
        if (!comCarrinho && _contaAberta is null && _comanda.Count > 0)
        {
            carrinhoFicouNa = GuardarCarrinhoNumaComanda(dono, ContasAbertas.Textos.NovaComanda);
            if (carrinhoFicouNa is null) return;
        }
        // revisao 14/10: depois de o carrinho ir para a comanda automatica, qualquer erro diz onde ele ficou
        string ComOndeFicou(string texto) => carrinhoFicouNa is { } f ? $"{texto} {ContasAbertas.Textos.CarrinhoFicouNa(f)}" : texto;
        var itens = comCarrinho ? ItensDoDisco() : null;
        var codigo = comCarrinho ? _cortesiaCodigo : null;
        var cobertura = comCarrinho ? new Dictionary<string, int>(_cortesiaCobertura) : null;
        ContaAberta conta;
        try
        {
            using var cx = Banco.Abrir();
            conta = ContasAbertas.Abrir(cx, _sessao, _operador, nome, consumo, itens, codigo, cobertura);
        }
        catch (Exception ex) { Dialogo.Avisar(dono, ContasAbertas.Textos.NovaComanda, ComOndeFicou(ex.Message), "erro"); return; }
        if (CarregarConta(conta.Id)) AvisoLeve(ComOndeFicou(ContasAbertas.Textos.ComandaAberta(conta.Numero)), "neutro");
        else if (carrinhoFicouNa is { } f) Dialogo.Avisar(dono, ContasAbertas.Textos.NovaComanda, ContasAbertas.Textos.CarrinhoFicouNa(f), "erro");
    }

    /// <summary>
    /// Revisao 14/10: o carrinho SOLTO vira uma comanda automatica guardada (numero novo, sem nome, na
    /// loja, com o cupom dele). Devolve o numero, ou null se nada mudou (erro avisado). Depois que o
    /// Abrir gravou, o resto (soltar, apagar o rascunho) e conforto: falha ali NAO deixa o carrinho
    /// tambem na tela (antes: itens duplicados e, no toque seguinte, uma terceira copia).
    /// </summary>
    private long? GuardarCarrinhoNumaComanda(Window dono, string titulo)
    {
        ContaAberta guardada;
        try
        {
            using var cx = Banco.Abrir();
            guardada = ContasAbertas.Abrir(cx, _sessao, _operador, null, DestinoCozinha.Loja, ItensDoDisco(), _cortesiaCodigo, _cortesiaCobertura);
        }
        catch (Exception ex) { Dialogo.Avisar(dono, titulo, ex.Message, "erro"); return null; }
        try { using var cx = Banco.Abrir(); ContasAbertas.Soltar(cx, guardada.Id); } catch { /* o dono morto tambem libera */ }
        try { using var cx = Banco.Abrir(); Rascunho.Apagar(cx); } catch { /* a repintura abaixo regrava o rascunho vazio */ }
        EsvaziarComanda();
        _cortesiaCodigo = null; _cortesiaCobertura.Clear(); CaixaCortesia.Visibility = Visibility.Collapsed;
        PintarComanda();
        return guardada.Numero;
    }

    /// <summary>A caixa "Cortesia X aplicada" com os itens cobertos (carga da conta e Levar para a N).</summary>
    private void MostrarCortesiaNaTela()
    {
        TxtCortesiaTitulo.Text = $"Cortesia {_cortesiaCodigo} aplicada";
        TxtCortesiaItens.Text = string.Join(", ",
            _comanda.Where(i => CoberturaDe(i) > 0).Select(i => $"{CoberturaDe(i)}× {i.Produto.Nome} grátis"));
        CaixaCortesia.Visibility = Visibility.Visible;
    }

    // ── carregar, gravar, guardar ───────────────────────────────────────────

    /// <summary>
    /// Carrega a conta na tela pelo MESMO caminho do rascunho (EsvaziarComanda + Add + PintarComanda,
    /// FP-3/FP-4). Preco e fiscal vem SEMPRE do catalogo atual; o produto que saiu do catalogo entra
    /// marcado "saiu do cardápio". Conta carregada noutro Pdv.exe vivo, fechada ou de outro modo
    /// (teste/real) nao carrega. Antes de carregar uma conta cujo dono morreu com o Pagamento aberto,
    /// o aviso da maquininha (o mesmo do rascunho).
    /// </summary>
    private bool CarregarConta(string id)
    {
        var dono = Window.GetWindow(this)!;
        ContaAberta? conta;
        string? avisoMaquininha = null;
        long? cupomEmOutra = null;
        try
        {
            using var cx = Banco.Abrir();
            var lida = ContasAbertas.Ler(cx, id);
            if (lida is null) { AvisoLeve(ContasAbertas.Textos.NaoEncontrada); return false; }
            if (!lida.Aberta)
            {
                AvisoLeve(lida.Status == "cancelada" ? ContasAbertas.Textos.JaFoiCancelada(lida.Numero) : ContasAbertas.Textos.JaFoiFechada(lida.Numero));
                return false;
            }
            if (ContasAbertas.EmUsoPorOutroPdv(lida)) { AvisoLeve(ContasAbertas.Textos.EmUsoNoOutroCaixa); return false; }
            if (lida.Teste != TurnoDeTeste)
            {
                AvisoLeve(TurnoDeTeste ? ContasAbertas.Textos.RealEmTurnoDeTeste : ContasAbertas.Textos.TesteEmTurnoReal);
                return false;
            }
            // o caixa caiu com o Pagamento desta conta aberto (fechando_em de pe, sem venda): o cartao
            // pode ter passado. O aviso da cobranca do turno inteiro sai no Fechar conta.
            if (lida.FechandoEm is not null && lida.VendaId is null)
                avisoMaquininha = ContasAbertas.AvisoDeMaquininha(cx, _sessao, lida);
            conta = ContasAbertas.Carregar(cx, id);
            if (conta is null) { AvisoLeve(ContasAbertas.Textos.EmUsoNoOutroCaixa); return false; }
            if (conta.CortesiaCodigo is not null) cupomEmOutra = ContasAbertas.CodigoEmOutraConta(cx, conta.CortesiaCodigo, conta.Id);
        }
        catch (Exception ex) { Dialogo.Avisar(dono, "Comandas", ex.Message, "erro"); return false; }
        if (avisoMaquininha is not null) Dialogo.Avisar(dono, "Confira a maquininha", avisoMaquininha, "erro");

        EsvaziarComanda();
        foreach (var i in conta.Itens)
        {
            var atual = _catalogo.FirstOrDefault(c => c.Id == i.ProdutoId);
            var fora = atual is null && !Nucleo.Promocoes.ForaDoMotor(i.ProdutoId);
            _comanda.Add(new ItemComanda
            {
                // preco de TABELA do catalogo atual (o snapshot so serve para o nome, quando saiu)
                Produto = atual ?? new Produto(i.ProdutoId, i.Plu, i.Nome, i.Categoria, new Dinheiro(i.PrecoCent),
                    i.Unidade, i.Ncm, i.Cest, i.Csosn, i.Origem, i.Foto),
                Qtd = new Quantidade(i.QtdMilesimos),
                Escolhas = Nucleo.Combos.DeJson(i.EscolhasJson),
                LinhaId = i.LinhaId ?? Guid.NewGuid().ToString("N"),
                CozinhaMilesimos = i.CozinhaMilesimos,
                ForaDoCardapio = fora,
            });
        }
        _cortesiaCodigo = null;
        _cortesiaCobertura.Clear();
        CaixaCortesia.Visibility = Visibility.Collapsed;
        if (conta.CortesiaCodigo is not null)
        {
            if (cupomEmOutra is { } outra) AvisoLeve(ContasAbertas.Textos.CupomJaNaComanda(outra));
            else
            {
                _cortesiaCodigo = conta.CortesiaCodigo;
                foreach (var kv in conta.CortesiaCobertura) _cortesiaCobertura[kv.Key] = kv.Value;
                MostrarCortesiaNaTela();
            }
        }
        _contaAberta = conta;
        _contaConferida = false;
        PintarComanda();
        try
        {
            using var cx = Banco.Abrir();
            Caixa.Auditar(cx, null, "conta_carregada", _operador.Id, null, $"comanda={conta.Numero} itens={_comanda.Count}");
        }
        catch { }
        return true;
    }

    /// <summary>
    /// O SalvarRascunho em modo comanda: grava o carrinho na conta e apaga o rascunho. O disco e
    /// quem sabe o que ja foi pedido (CozinhaMilesimos volta dele para a tela). A regra do item ja
    /// pedido mora no nucleo: se a tela pedir o que ele recusa, a conta volta do disco.
    /// </summary>
    private void GravarNaConta()
    {
        var conta = _contaAberta;
        if (conta is null) return;
        try
        {
            using var cx = Banco.Abrir();
            ContasAbertas.Gravar(cx, conta.Id, ItensDoDisco(), _cortesiaCodigo, _cortesiaCobertura, _operador);
            Rascunho.Apagar(cx);
            var lida = ContasAbertas.Ler(cx, conta.Id);
            if (lida is null) return;
            foreach (var item in _comanda)
            {
                var doDisco = lida.Itens.FirstOrDefault(x => x.LinhaId == item.LinhaId);
                if (doDisco is not null) item.CozinhaMilesimos = doDisco.CozinhaMilesimos;
            }
            _contaAberta = lida;
        }
        catch (InvalidOperationException ex)
        {
            AvisoLeve(ex.Message);
            var id = conta.Id;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() => { if (_contaAberta?.Id == id) CarregarConta(id); }));
        }
        catch { /* a conta e conforto ate o fechamento; nunca derruba a tela */ }
    }

    /// <summary>
    /// Guardar: solta o dono (a conta continua aberta no banco, para qualquer tela carregar) e o
    /// caixa volta ao balcao com o carrinho vazio. Sem nome, sem item e com menos de 1 min de vida,
    /// foi aberta por engano e some sozinha. _contaAberta = null vem ANTES de EsvaziarComanda: a
    /// repintura grava o rascunho vazio (apaga), nao a conta.
    /// </summary>
    private void GuardarConta()
    {
        var conta = _contaAberta;
        if (conta is null) return;
        _contaConferida = false;
        try
        {
            using var cx = Banco.Abrir();
            var lida = ContasAbertas.Ler(cx, conta.Id);
            if (lida is { Aberta: true } && ContasAbertas.AbertaPorEngano(lida, DateTime.Now))
                ContasAbertas.Cancelar(cx, conta.Id, _operador, "aberta por engano", null, automatico: true);
            else if (lida is { Aberta: true })
            {
                ContasAbertas.Soltar(cx, conta.Id);
                Caixa.Auditar(cx, null, "conta_guardada", _operador.Id, null, $"comanda={conta.Numero} itens={_comanda.Count}");
            }
        }
        catch { /* soltar o dono e cortesia: o processo morto tambem libera */ }
        _contaAberta = null;
        EsvaziarComanda();
        _cortesiaCodigo = null;
        _cortesiaCobertura.Clear();
        CaixaCortesia.Visibility = Visibility.Collapsed;
        PintarComanda();
    }

    /// <summary>O Limpar em modo comanda: tira so o que ainda nao foi pedido; o que ja foi fica.</summary>
    private void LimparSoOPendente()
    {
        var ficaram = 0;
        foreach (var item in _comanda.ToList())
        {
            if (item.CozinhaMilesimos <= 0) { _comanda.Remove(item); continue; }
            ficaram++;
            if (item.Qtd.Milesimos > item.CozinhaMilesimos) item.Qtd = new Quantidade(item.CozinhaMilesimos);
        }
        PintarComanda();
        if (ficaram > 0) AvisoLeve(ContasAbertas.Textos.JaPedidosFicam);
    }

    /// <summary>O cupom ja esta em OUTRA conta aberta? (reserva por conta) Devolve o numero dela.</summary>
    private long? CupomPresoEmOutraConta(string codigo)
    {
        try { using var cx = Banco.Abrir(); return ContasAbertas.CodigoEmOutraConta(cx, codigo, _contaAberta?.Id); }
        catch { return null; }
    }

    // ── tirar item ja pedido: PIN de OUTRO gerente, numa janela so ──────────

    /// <summary>
    /// O toque cruzou para baixo do que ja foi para a cozinha. UMA janela de PIN: Cancelar volta,
    /// PIN errado (ou do proprio operador) avisa e REABRE a janela. Autorizado: o nucleo baixa a
    /// quantidade e a marca, refaz o lote que ainda esta na fila, audita e enfileira o rastro.
    /// </summary>
    private void TirarProduzido(ItemComanda item, long novaQtdMilesimos)
    {
        var conta = _contaAberta;
        if (conta is null) return;
        var sup = PedirOutroGerente(ContasAbertas.Textos.ItemJaPedido, ContasAbertas.Textos.PinDeOutroGerente);
        if (sup is null) return;
        var dono = Window.GetWindow(this)!;
        try
        {
            using var cx = Banco.Abrir();
            ContasAbertas.RetirarProduzido(cx, conta.Id, item.LinhaId, novaQtdMilesimos, _operador, sup);
        }
        catch (InvalidOperationException ex) { Dialogo.Avisar(dono, ContasAbertas.Textos.ItemJaPedido, ex.Message, "erro"); return; }
        if (novaQtdMilesimos <= 0) { _comanda.Remove(item); PintarComanda(); }
        else
        {
            item.Qtd = new Quantidade(novaQtdMilesimos);
            item.CozinhaMilesimos = Math.Min(item.CozinhaMilesimos, novaQtdMilesimos);
            PintarComanda();
        }
        CutucarDreno();
    }

    /// <summary>
    /// PIN de gerente que NAO seja quem opera (a regra da sangria), numa janela so: PIN errado ou
    /// do proprio operador avisa e pergunta de novo; Cancelar devolve null.
    /// </summary>
    private Operador? PedirOutroGerente(string titulo, string rotulo)
    {
        var dono = Window.GetWindow(this)!;
        while (true)
        {
            var pin = PedirSenha.Mostrar(dono, titulo, rotulo);
            if (pin is null) return null;
            Operador? sup;
            using (var cx = Banco.Abrir()) sup = Operadores.AutorizarSupervisor(cx, pin);
            if (sup is null)
            {
                Dialogo.Avisar(dono, ContasAbertas.Textos.PinNaoConfere,
                    "Esse PIN não é de gerente, ou saiu errado. Peça para o gerente digitar de novo.", "erro");
                continue;
            }
            if (sup.Id == _operador.Id)
            {
                Dialogo.Avisar(dono, "Precisa de outro gerente", ContasAbertas.Textos.ChameOutroGerente, "erro");
                continue;
            }
            return sup;
        }
    }

    // ── pedir a cozinha ─────────────────────────────────────────────────────

    /// <summary>
    /// "Cozinha N": o lote com o que ainda nao foi pedido, numa transacao so e sem rede no toque.
    /// Depois a conta e GUARDADA SOZINHA (decisao do dono, padrao ligado: o proximo cliente nao cai
    /// na mesa). O toast e honesto: "Pedido na cozinha. Comanda 12 guardada." na hora e, se a TV
    /// confirmar em ate 2 s, "Cozinha recebeu o pedido.".
    /// </summary>
    private void PedirCozinhaDaConta()
    {
        var conta = _contaAberta;
        if (conta is null) return;
        var dono = Window.GetWindow(this)!;
        if (_comanda.FirstOrDefault(i => i.ForaDoCardapio) is { } fora)
        {
            Dialogo.Avisar(dono, "Item fora do cardápio", $"{fora.Produto.Nome}: {ContasAbertas.Textos.SaiuDoCardapio}", "erro");
            return;
        }
        if (ComboIncompleto(dono)) return;
        var numero = conta.Numero;
        ContaLote? lote;
        try
        {
            using var cx = Banco.Abrir();
            lote = ContasAbertas.PedirCozinha(cx, conta.Id, conta.Destino, _operador, TurnoDeTeste);
        }
        catch (InvalidOperationException ex) { Dialogo.Avisar(dono, "Cozinha", ex.Message, "erro"); return; }
        catch (Exception ex) { Dialogo.Avisar(dono, "Cozinha", ex.Message, "erro"); return; }
        // revisao 14/10: o botao pode estar velho (Mandar de novo noutro lugar): repinta antes do aviso
        if (lote is null) { PintarComanda(); AvisoLeve(ContasAbertas.Textos.NadaNovo, "neutro"); return; }
        CutucarDreno();
        GuardarConta();
        AvisoLeve(ContasAbertas.Textos.PedidoNaCozinhaGuardada(numero), "neutro");
        if (!lote.Homologacao) CutucarCozinha(lote.Id, numero);
    }

    /// <summary>Combo sem os sabores barra a cozinha como barra o Finalizar: a TV receberia a caixa vazia.</summary>
    private bool ComboIncompleto(Window dono)
    {
        var catalogoLocal = CatalogoLocal();
        foreach (var i in _comanda)
        {
            if (!_combos.TryGetValue(i.Produto.Id, out var defCombo)) continue;
            if (Nucleo.Combos.Pendencia(defCombo, i.Escolhas, catalogoLocal) is string pendencia)
            {
                Dialogo.Avisar(dono, "Combo incompleto", pendencia);
                return true;
            }
        }
        return false;
    }

    /// <summary>Espera ate 2 s (500 ms por vez, sem travar a tela) pela confirmacao da TV e troca o toast.</summary>
    private void CutucarCozinha(string loteId, long numero)
    {
        _cutucaCozinha?.Stop();
        var vezes = 0;
        _cutucaCozinha = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _cutucaCozinha.Tick += (_, _) =>
        {
            vezes++;
            EstadoLote estado;
            try { using var cx = Banco.Abrir(); estado = ContasAbertas.EstadoDoLote(cx, loteId); }
            catch { estado = EstadoLote.NaFila; }
            if (estado == EstadoLote.NaTv) { _cutucaCozinha?.Stop(); AvisoLeve(ContasAbertas.Textos.CozinhaRecebeuEGuardou(numero), "ok"); return; }
            if (estado != EstadoLote.NaFila || vezes >= 4) _cutucaCozinha?.Stop();
        };
        _cutucaCozinha.Start();
    }

    private static void CutucarDreno()
    {
        try { Servicos.Dreno()?.Cutucar(); } catch { /* sem dreno (foto, suite): a fila espera a varredura */ }
    }

    // ── fechar conta: as conferencias antes do dinheiro ─────────────────────

    /// <summary>
    /// As pre-conferencias do "Fechar conta", todas ANTES de qualquer dinheiro: a conta ainda esta
    /// aberta; o modo (teste/real) bate; nenhuma linha fora do cardapio; o cupom ainda vale (morreu =
    /// tirado com aviso, e cobra); o aviso da maquininha quando pode haver cartao vivo sem venda.
    /// Depois volta ao Finalizar de sempre com _contaConferida de pe.
    /// </summary>
    private async Task ConferirContaEFinalizarAsync()
    {
        if (_conferindoConta) return;
        _conferindoConta = true;
        try
        {
            var conta = _contaAberta;
            if (conta is null) return;
            var dono = Window.GetWindow(this)!;
            string? avisoMaquininha;
            using (var cx = Banco.Abrir())
            {
                var lida = ContasAbertas.Ler(cx, conta.Id);
                if (lida is null || !lida.Aberta)
                {
                    AvisoLeve(lida is { Status: "cancelada" } ? ContasAbertas.Textos.JaFoiCancelada(conta.Numero) : ContasAbertas.Textos.JaFoiFechada(conta.Numero));
                    _contaAberta = null;
                    EsvaziarComanda();
                    PintarComanda();
                    return;
                }
                if (lida.Teste != TurnoDeTeste)
                {
                    Dialogo.Avisar(dono, "Comanda", TurnoDeTeste ? ContasAbertas.Textos.RealEmTurnoDeTeste : ContasAbertas.Textos.TesteEmTurnoReal, "erro");
                    return;
                }
                avisoMaquininha = ContasAbertas.AvisoDeMaquininha(cx, _sessao, lida);
            }
            if (_comanda.FirstOrDefault(i => i.ForaDoCardapio) is { } fora)
            {
                Dialogo.Avisar(dono, "Item fora do cardápio", $"{fora.Produto.Nome}: {ContasAbertas.Textos.SaiuDoCardapio}", "erro");
                return;
            }
            // o cupom que esta comanda ja queimou (fechamento em cortesia que falhou) nao se valida de novo
            if (_cortesiaCodigo is { } codigo && !CortesiaJaUsadaNesta(conta.Id, codigo))
            {
                // o cupom pode ter morrido enquanto a comanda ficou guardada (usado, vencido, cancelado).
                // Revisao 14/10: a tela fica travada na espera (ate 12 s com a rede lenta) e, se mesmo assim
                // a comanda ou o cupom mudaram, nada e feito (antes tirava o cupom de OUTRA comanda)
                Cortesia? c = null;
                IsEnabled = false;
                try { c = await _cortesias.ValidarAsync(codigo); } catch { /* sem rede: segue com a cortesia */ }
                finally { IsEnabled = true; }
                if (_contaAberta?.Id != conta.Id || _cortesiaCodigo != codigo) return;
                if (c is { Ok: false } && c.Erro is "expired" or "already_redeemed" or "cancelled" or "not_found")
                {
                    RemoverCortesia(this, new RoutedEventArgs());
                    AvisoLeve(ContasAbertas.Textos.CupomNaoValeMais);
                }
            }
            if (avisoMaquininha is not null) Dialogo.Avisar(dono, "Confira a maquininha", avisoMaquininha, "erro");
            _contaConferida = true;
            try { Finalizar(this, new RoutedEventArgs()); }
            finally { _contaConferida = false; }
        }
        finally { _conferindoConta = false; }
    }

    /// <summary>
    /// O que o Pagamento recebe da conta (numero, nome, consumo e o que ainda nao foi pedido), e o
    /// fechando_em marcado ANTES de qualquer cobranca: se o caixa cair com o Pagamento aberto, a
    /// lista mostra "confira a maquininha".
    /// </summary>
    private ContaNoPagamento? ContaParaOPagamento()
    {
        var conta = _contaAberta;
        if (conta is null) return null;
        long pendentes;
        try
        {
            using var cx = Banco.Abrir();
            ContasAbertas.MarcarFechando(cx, conta.Id);
            var lida = ContasAbertas.Ler(cx, conta.Id) ?? conta;
            pendentes = ContasAbertas.Pendentes(cx, lida).Sum(p => p.Milesimos);
        }
        catch { pendentes = _comanda.Sum(i => i.PendenteMilesimos); }
        return new ContaNoPagamento(conta.Id, conta.Numero, conta.Nome, conta.Destino, pendentes);
    }

    /// <summary>Encerrou(Concluida): a conta fechou dentro da transacao da venda; a tela so a solta.</summary>
    private void ContaVirouVenda()
    {
        _contaAberta = null;
        _contaConferida = false;
    }

    /// <summary>"Voltar para a comanda": a conta continua carregada e o fechando_em sai.</summary>
    private void ContaDesistiuDoPagamento()
    {
        var conta = _contaAberta;
        if (conta is null) return;
        try { using var cx = Banco.Abrir(); ContasAbertas.LimparFechando(cx, conta.Id); } catch { }
    }

    // ── cancelar comanda (dois toques, fora do rodape) ──────────────────────

    /// <summary>
    /// Conta vazia: confirmacao simples. Com QUALQUER item: motivo + PIN de OUTRO gerente. O nucleo
    /// grava, audita e enfileira o rastro; o lote ainda na fila e descartado pelo dreno antes do
    /// POST, e o que ja esta na TV e aviso de boca.
    /// </summary>
    private void CancelarContaComAutorizacao(string id)
    {
        var dono = Window.GetWindow(this)!;
        ContaAberta? conta;
        try { using var cx = Banco.Abrir(); conta = ContasAbertas.Ler(cx, id); } catch { conta = null; }
        if (conta is null || !conta.Aberta) { AvisoLeve(ContasAbertas.Textos.NaoEncontrada); return; }
        var naTela = _contaAberta?.Id == id;
        var temItem = conta.Itens.Count > 0 || (naTela && _comanda.Count > 0);
        var numero = conta.Numero;
        try
        {
            if (!temItem)
            {
                if (!Dialogo.Confirmar(dono, ContasAbertas.Textos.CancelarComanda(numero), ContasAbertas.Textos.CancelarPergunta(numero),
                        "Cancelar comanda", "Voltar", perigo: true)) return;
                using var cx = Banco.Abrir();
                ContasAbertas.Cancelar(cx, id, _operador, null, null);
            }
            else
            {
                // revisao 13/10: a caixa vem VAZIA (antes vinha "cliente desistiu" pronto e um toque passava)
                var motivo = PedirTexto.Mostrar(dono, ContasAbertas.Textos.CancelarComanda(numero),
                    ContasAbertas.Textos.MotivoDoCancelamento, "", seVazio: ContasAbertas.Textos.MotivoObrigatorio);
                if (string.IsNullOrWhiteSpace(motivo)) return;
                var sup = PedirOutroGerente(ContasAbertas.Textos.CancelarComanda(numero), ContasAbertas.Textos.AvisoAutorizador);
                if (sup is null) return;
                if (naTela) GravarNaConta();   // o rastro leva o que esta na tela agora
                using var cx = Banco.Abrir();
                ContasAbertas.Cancelar(cx, id, _operador, motivo, sup);
            }
        }
        catch (Exception ex) { Dialogo.Avisar(dono, ContasAbertas.Textos.CancelarComanda(numero), ex.Message, "erro"); return; }
        if (naTela)
        {
            _contaAberta = null;
            _contaConferida = false;
            EsvaziarComanda();
            _cortesiaCodigo = null;
            _cortesiaCobertura.Clear();
            CaixaCortesia.Visibility = Visibility.Collapsed;
            PintarComanda();
        }
        else PintarModoComanda();
        AvisoLeve(ContasAbertas.Textos.ContaCancelada(numero), "neutro");
        CutucarDreno();
    }

    // ── fechamento do caixa ─────────────────────────────────────────────────

    /// <summary>
    /// Antes da contagem: cancela sozinho as contas vazias sem lote e, se restarem abertas, avisa
    /// "Comandas abertas: N (R$ X)" com Fechar mesmo assim. Nunca bloqueia, nunca transfere.
    /// </summary>
    private bool ComandasDeixamFechar(Window dono)
    {
        try
        {
            using var cx = Banco.Abrir();
            ContasAbertas.CancelarVaziasAutomatico(cx, _operador);
            var resumo = ContasAbertas.ResumoDoTurno(cx, _sessao);
            if (resumo.AbertasN == 0) return true;
            return Dialogo.Confirmar(dono, ContasAbertas.Textos.ComandasAbertas(resumo.AbertasN, resumo.Abertas),
                ContasAbertas.Textos.ContinuamProximoTurno, ContasAbertas.Textos.FecharMesmoAssim, "Voltar");
        }
        catch { return true; }   // a comanda nunca prende o caixa
    }

    /// <summary>
    /// Depois do Caixa.Fechar: cada conta que atravessa gera o conta_evento, e as duas linhas do
    /// papel e da tela saem daqui. Null quando o turno nao teve comanda nenhuma (nem cancelada nem
    /// aberta): a loja que nao usa comanda nao ganha duas linhas de zero.
    /// </summary>
    private IReadOnlyList<string>? ComandasAtravessaram(Microsoft.Data.Sqlite.SqliteConnection cx)
    {
        try
        {
            ContasAbertas.RegistrarAtravessamento(cx, _sessao, _operador);
            var resumo = ContasAbertas.ResumoDoTurno(cx, _sessao);
            if (resumo.AbertasN == 0 && resumo.CanceladasN == 0) return null;
            return ContasAbertas.LinhasDoFechamento(cx, _sessao);
        }
        catch { return null; }
    }

    // ── a pintura do modo ───────────────────────────────────────────────────

    /// <summary>
    /// AplicarLargura avisou que a largura mudou: as medidas a 1024 (coluna de 236 px, margem 14+14,
    /// botao Comandas so com o glifo e o numero, "Fechar conta" a 15 px sem a seta, cupom so com o
    /// icone) estao aqui, escritas, nao descobertas na foto.
    /// </summary>
    private void AplicarModoComanda(bool estreita) => PintarModoComanda();

    /// <summary>O cabecalho, a faixa, o botao Comandas, a linha do cupom e o Finalizar seguem o modo.</summary>
    private void PintarModoComanda()
    {
        var conta = _contaAberta;
        var modo = conta is not null;
        var estreita = _estreita;

        // cabecalho da coluna: "Comanda" + chip no balcao; "12 · Mesa 5" na faixa ciano em modo comanda
        if (conta is not null)
        {
            var temNome = !string.IsNullOrWhiteSpace(conta.Nome);
            RunNumeroComanda.Text = temNome ? $"{conta.Numero} · " : conta.Numero.ToString();
            RunNomeComanda.Text = temNome ? conta.Nome! : "";
            TxtTituloComanda.SetResourceReference(TextBlock.ForegroundProperty, "Ciano");
            ChipItens.Visibility = Visibility.Collapsed;
            FaixaComanda.SetResourceReference(Border.BackgroundProperty, "ChipInfoFundo");
            FaixaComanda.SetResourceReference(Border.BorderBrushProperty, "ChipInfoBorda");
            FaixaComanda.BorderThickness = new Thickness(1);
            FaixaComanda.Padding = new Thickness(10, 5, 6, 5);
            ColunaComanda.BorderThickness = new Thickness(4, 0, 0, 0);
            ColunaComanda.SetResourceReference(Border.BorderBrushProperty, "Ciano");
        }
        else
        {
            RunNumeroComanda.Text = "Comanda";
            RunNomeComanda.Text = "";
            TxtTituloComanda.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
            ChipItens.Visibility = _comanda.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            FaixaComanda.Background = null;
            FaixaComanda.BorderBrush = null;
            FaixaComanda.BorderThickness = new Thickness(0);
            FaixaComanda.Padding = new Thickness(0);
            ColunaComanda.BorderThickness = new Thickness(1, 0, 0, 0);
            ColunaComanda.SetResourceReference(Border.BorderBrushProperty, "Borda");
        }
        TxtTituloComanda.FontSize = estreita ? 17 : 20;
        FaixaComanda.Margin = estreita
            ? (modo ? new Thickness(10, 10, 10, 8) : new Thickness(14, 12, 14, 8))
            : (modo ? new Thickness(14, 12, 14, 8) : new Thickness(18, 16, 18, 10));

        // o botao Comandas: a 1024 so o glifo e o numero de abertas no modo atual
        var abertas = 0;
        try { using var cx = Banco.Abrir(); abertas = ContasAbertas.ContarAbertas(cx, TurnoDeTeste); } catch { }
        TxtNumComandas.Text = abertas > 0 ? abertas.ToString() : "";
        TxtNumComandas.Visibility = abertas > 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtRotuloComandas.Visibility = estreita ? Visibility.Collapsed : Visibility.Visible;
        BtnComandas.Padding = estreita ? new Thickness(8, 0, 8, 0) : new Thickness(10, 0, 10, 0);
        BtnComandas.MinWidth = estreita ? 44 : 0;

        // o rodape inteiro: a 1024 o padding lateral cai de 18 para 12 (a coluna tem 236 px; e o que
        // faz a linha do cupom caber com Guardar e "Cozinha N", e da 12 px a mais ao Finalizar)
        RodapeComanda.Padding = estreita ? new Thickness(12, 14, 12, 16) : new Thickness(18, 16, 18, 18);

        // A LINHA DO CUPOM: cortesia | Guardar | Cozinha N (os dois so em modo comanda). Medidas a
        // 1024 (236 px de coluna, 4 de borda, 12+12 de padding = 208 uteis): cupom so com o icone
        // (11+20+11 = 42), Guardar (6+52+6 = 64), Cozinha 2 (6+62+6 = 74) e dois espacos de 8 = 196.
        // A coluna da cozinha e a elastica (*): o que sobrar e dela, e o rotulo corta com reticencias
        // em vez de sair da tela. No balcao a cortesia volta a ocupar a linha inteira (*).
        BtnGuardar.Visibility = modo ? Visibility.Visible : Visibility.Collapsed;
        BtnCozinha.Visibility = modo ? Visibility.Visible : Visibility.Collapsed;
        LarguraCupom.Width = modo ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        LarguraCozinha.Width = modo ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        var soIcone = modo && estreita;
        TxtCupomRotulo.Visibility = soIcone ? Visibility.Collapsed : Visibility.Visible;
        TxtCupomRotulo.Text = modo ? "Cupom" : estreita ? "Cupom de cortesia" : "Aplicar cupom de cortesia";
        TxtIconeCupom.Margin = soIcone ? new Thickness(0) : new Thickness(0, 0, 8, 0);
        if (soIcone) BtnCortesia.Padding = new Thickness(11, 0, 11, 0);
        else if (modo) BtnCortesia.Padding = new Thickness(12, 0, 12, 0);
        else BtnCortesia.ClearValue(PaddingProperty);
        BtnCortesia.MinWidth = soIcone ? 42 : 0;
        TxtGuardarRotulo.FontSize = estreita ? 13 : 15;
        TxtCozinhaRotulo.FontSize = estreita ? 13 : 15;
        BtnGuardar.Padding = estreita ? new Thickness(6, 0, 6, 0) : new Thickness(10, 0, 10, 0);
        BtnCozinha.Padding = estreita ? new Thickness(6, 0, 6, 0) : new Thickness(10, 0, 10, 0);
        if (conta is not null)
        {
            long pendentes;
            var naoChegou = false;
            try
            {
                using var cx = Banco.Abrir();
                var lida = ContasAbertas.Ler(cx, conta.Id) ?? conta;
                pendentes = ContasAbertas.Pendentes(cx, lida).Sum(p => p.Milesimos) / 1000;
                naoChegou = ContasAbertas.UltimoLoteNaoChegou(cx, conta.Id);
            }
            catch { pendentes = _comanda.Sum(i => i.PendenteMilesimos) / 1000; }
            // revisoes 13 e 14/10: a 1024 o "⚠ Cozinha 12" cortava e o "⚠ 12" nao dizia de que era o botao
            TxtCozinhaRotulo.Text = naoChegou && estreita ? $"⚠ {ContasAbertas.Textos.CozinhaCurta(pendentes)}"
                : (naoChegou ? "⚠ " : "") + ContasAbertas.Textos.CozinhaN(pendentes);
            BtnCozinha.IsEnabled = _comanda.Count > 0;
        }

        // Finalizar vira "Fechar conta" (a 1024: 15 px e sem a seta)
        TxtFinalizarRotulo.Text = modo ? ContasAbertas.Textos.FecharConta : "Finalizar";
        TxtFinalizarRotulo.FontSize = modo && estreita ? 15 : estreita ? 17 : 19;
        TxtSetaFinalizar.Visibility = modo && estreita ? Visibility.Collapsed : Visibility.Visible;
    }
}
