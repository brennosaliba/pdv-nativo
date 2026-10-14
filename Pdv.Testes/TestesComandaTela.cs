using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// COMANDA ABERTA, A TELA (13/10/2026, etapa Telas). A suite do nucleo (TestesContaAberta) prova a
/// conta, o lote e o fechamento; esta prova o que a TELA faz com isso:
///  · as linhas do dialogo Comandas (selos, cozinha N de M, Mandar de novo) e o corpo do aviso do
///    rascunho, pelo nucleo (ContasAbertas.LinhasDaLista, Rascunho.CorpoDoAviso), sem WPF;
///  · as duas linhas de comanda no papel do fechamento (PapelDeCaixa) e o portao da atualizacao
///    (CA-23: conta aberta no banco nao barra a troca do exe);
///  · os vigias de fonte (CA-16): a conta e guardada ANTES das travas de "Comanda aberta"; FP-3
///    continua com um unico _comanda.Clear(); a carga passa por EsvaziarComanda e PintarComanda;
///    Reprecificar e a restauracao copiam LinhaId e CozinhaMilesimos; o rascunho desvia para a conta;
///  · os textos da tela (XAML, dialogo, Pagamento, FotoComanda e esta suite) sem travessao;
///  · a tela de VERDADE no HostWpf, a 1024x768: carregar, lancar, o PIN de outro gerente numa
///    janela so, Guardar, Pedir a cozinha (guarda sozinho), Fechar conta (Pagamento com a comanda e
///    o bloco da cozinha so com pendentes), o dialogo Comandas com os selos, a face Nova e o
///    cancelamento com motivo e PIN.
/// </summary>
public static class TestesComandaTela
{
    private const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string Ninho = "p-ninho", Homer = "p-homer", Agua = "p-agua";

    public static void Rodar(Action<bool, string> checar)
    {
        Fontes(checar);
        Textos(checar);
        NoNucleo(checar);
        NaTela(checar);
    }

    // ── CA-16 e os vigias de fonte ──────────────────────────────────────────
    private static void Fontes(Action<bool, string> checar)
    {
        var venda = Fonte("Telas", "Venda.xaml.cs");
        var comanda = Fonte("Telas", "Venda.Comanda.cs");
        var xaml = Fonte("Telas", "Venda.xaml");
        var dialogo = Fonte("Telas", "DialogoComandas.cs");
        var pag = Fonte("Telas", "Pagamento.xaml.cs");
        var pagXaml = Fonte("Telas", "Pagamento.xaml");
        checar(venda.Length > 0 && comanda.Length > 0 && xaml.Length > 0 && dialogo.Length > 0 && pag.Length > 0 && pagXaml.Length > 0,
            "CT-0 achei Venda.xaml(.cs), Venda.Comanda.cs, DialogoComandas.cs e Pagamento.xaml(.cs)");

        // CA-16: a conta e guardada ANTES das travas do carrinho solto
        foreach (var (nome, de, ate) in new[]
                 {
                     ("FecharCaixa", "private async void FecharCaixa(object sender, RoutedEventArgs e)", "_fechandoCaixa = true;"),
                     ("Sair", "private void Sair(object sender, RoutedEventArgs e)", "Deslogou?.Invoke();"),
                     ("MenuCancelamento", "private async void MenuCancelamento", "case MenuBarra.Cancelar"),
                     ("AbrirConfiguracao", "private void AbrirConfiguracao(", "PediuConfig?.Invoke();"),
                 })
        {
            var trecho = Trecho(venda, de, ate);
            checar(trecho.Length > 0 && Ordem(trecho, "if (_contaAberta is not null) GuardarConta();", "_comanda.Count > 0"),
                $"CA-16 {nome}: a comanda carregada e guardada (_contaAberta) ANTES da trava do carrinho solto");
        }
        checar(Ordem(Trecho(venda, "private async void FecharCaixa(object sender, RoutedEventArgs e)", "_fechandoCaixa = true;"), "Comanda aberta", "ComandasDeixamFechar(dono)"),
            "CA-16 FecharCaixa: depois da trava, cancela as vazias e avisa 'Comandas abertas: N' (ComandasDeixamFechar)");
        checar(Ordem(Trecho(venda, "private async void AtualizarOCaixa(", "private static void MostrarOQueDesceu("), "if (_contaAberta is not null) GuardarConta();", "AtualizarCaixa.ExecutarAsync(dono, _comanda.Count, _tefOcupado)"),
            "CA-16 AtualizarOCaixa: com versao nova a comanda e guardada antes do portao da troca");
        checar(Regex.Matches(venda, @"_comanda\.Clear\(\)").Count == 1 && !comanda.Contains("_comanda.Clear()", StringComparison.Ordinal),
            "CA-16 FP-3 continua: um unico _comanda.Clear() (em EsvaziarComanda), nenhum em Venda.Comanda.cs");
        var carregar = Metodo(comanda, "private bool CarregarConta(string id)");
        checar(carregar.Contains("EsvaziarComanda();", StringComparison.Ordinal) && Ordem(carregar, "EsvaziarComanda();", "_contaAberta = conta;")
               && Ordem(carregar, "_contaAberta = conta;", "PintarComanda();"),
            "CA-16 CarregarConta passa por EsvaziarComanda, poe a conta na tela e so entao PintarComanda (FP-4)");
        checar(carregar.Contains("CozinhaMilesimos = i.CozinhaMilesimos", StringComparison.Ordinal) && carregar.Contains("LinhaId = i.LinhaId", StringComparison.Ordinal)
               && carregar.Contains("ForaDoCardapio = fora", StringComparison.Ordinal),
            "CA-16 a carga copia LinhaId, CozinhaMilesimos e marca a linha fora do cardapio");
        var reprecificar = Metodo(venda, "private void ReprecificarComanda()");
        checar(reprecificar.Contains("LinhaId = item.LinhaId", StringComparison.Ordinal) && reprecificar.Contains("CozinhaMilesimos = item.CozinhaMilesimos", StringComparison.Ordinal),
            "CA-16 ReprecificarComanda copia LinhaId e CozinhaMilesimos (a cozinha nao recebe tudo de novo)");
        var rascunho = Metodo(venda, "private void OferecerRascunho()");
        checar(rascunho.Contains("LinhaId = i.LinhaId", StringComparison.Ordinal) && rascunho.Contains("CozinhaMilesimos = i.CozinhaMilesimos", StringComparison.Ordinal)
               && rascunho.Contains("Rascunho.CorpoDoAviso(", StringComparison.Ordinal) && rascunho.Contains("ContasAbertas.ContarAbertas(", StringComparison.Ordinal),
            "CA-24 a restauracao do rascunho copia os dois campos e o corpo do dialogo vem de Rascunho.CorpoDoAviso com a conta no banco");
        var salvar = Metodo(venda, "private void SalvarRascunho()");
        var gravar = Metodo(comanda, "private void GravarNaConta()");
        checar(salvar.Contains("if (_contaAberta is not null) { GravarNaConta(); return; }", StringComparison.Ordinal)
               && gravar.Contains("ContasAbertas.Gravar(", StringComparison.Ordinal) && gravar.Contains("Rascunho.Apagar(cx);", StringComparison.Ordinal),
            "CA-4 com a conta carregada o SalvarRascunho grava na conta e apaga comanda_rascunho");
        var guardar = Metodo(comanda, "private void GuardarConta()");
        checar(Ordem(guardar, "ContasAbertas.Soltar(", "_contaAberta = null;") && Ordem(guardar, "_contaAberta = null;", "EsvaziarComanda();"),
            "CA-16 GuardarConta solta o dono e zera _contaAberta ANTES de EsvaziarComanda (a repintura apaga o rascunho, nao a conta)");
        var finalizar = Metodo(venda, "private void Finalizar(object sender, RoutedEventArgs e)");
        checar(finalizar.Contains("if (_contaAberta is not null && !_contaConferida) { _ = ConferirContaEFinalizarAsync(); return; }", StringComparison.Ordinal)
               && finalizar.Contains("var contaNoPagamento = ContaParaOPagamento();", StringComparison.Ordinal)
               && finalizar.Contains("_loja, _lojaId, contaNoPagamento);", StringComparison.Ordinal)
               && Ordem(finalizar, "ContaVirouVenda();", "EsvaziarComanda();") && finalizar.Contains("else ContaDesistiuDoPagamento();", StringComparison.Ordinal),
            "CA-12 Fechar conta: conferencias antes, fechando_em ao abrir o Pagamento, solta a conta ANTES de esvaziar, Desistiu limpa fechando_em");
        var conferir = Metodo(comanda, "private async Task ConferirContaEFinalizarAsync()");
        checar(conferir.Contains("lida.Teste != TurnoDeTeste", StringComparison.Ordinal) && conferir.Contains("ForaDoCardapio", StringComparison.Ordinal)
               && conferir.Contains("_cortesias.ValidarAsync(codigo)", StringComparison.Ordinal) && conferir.Contains("AvisoDeMaquininha(", StringComparison.Ordinal)
               && Ordem(conferir, "AvisoDeMaquininha(", "Finalizar(this, new RoutedEventArgs())"),
            "CA-12 as pre-conferencias: aberta, turno de teste, cardapio, cupom revalidado e maquininha, todas antes do dinheiro");
        checar(Metodo(comanda, "private ContaNoPagamento? ContaParaOPagamento()").Contains("ContasAbertas.MarcarFechando(cx, conta.Id);", StringComparison.Ordinal)
               && Metodo(comanda, "private void ContaDesistiuDoPagamento()").Contains("ContasAbertas.LimparFechando(cx, conta.Id);", StringComparison.Ordinal),
            "CA-12 fechando_em marcado ao abrir o Pagamento e limpo no Voltar para a comanda");
        var pin = Metodo(comanda, "private Operador? PedirOutroGerente(string titulo, string rotulo)");
        checar(pin.Contains("while (true)", StringComparison.Ordinal) && pin.Contains("PedirSenha.Mostrar(", StringComparison.Ordinal)
               && pin.Contains("Operadores.AutorizarSupervisor(cx, pin)", StringComparison.Ordinal) && pin.Contains("sup.Id == _operador.Id", StringComparison.Ordinal)
               && Regex.Matches(pin, "continue;").Count == 2,
            "CA-18 o PIN e UMA janela: errado ou do proprio operador avisa e reabre; Cancelar volta");
        var passo = Trecho(venda, "Button Passo(string txt, int delta)", "var controles = new StackPanel");
        checar(passo.Contains("if (novo < item.CozinhaMilesimos) { TirarProduzido(item, Math.Max(0, novo)); return; }", StringComparison.Ordinal)
               && venda.Contains("if (item.CozinhaMilesimos > 0) { TirarProduzido(item, 0); return; }", StringComparison.Ordinal),
            "CA-18 menos e lixeira cruzando o ja pedido caem em TirarProduzido (PIN); acima do pedido continua livre");
        checar(Metodo(venda, "private void LimparComanda(object sender, RoutedEventArgs e)").Contains("if (_contaAberta is not null) { LimparSoOPendente(); return; }", StringComparison.Ordinal)
               && Metodo(comanda, "private void LimparSoOPendente()").Contains("JaPedidosFicam", StringComparison.Ordinal),
            "CA-7 o Limpar em modo comanda tira so o que nao foi pedido (nunca vira cancelar comanda)");
        var pedir = Metodo(comanda, "private void PedirCozinhaDaConta()");
        checar(pedir.Contains("ContasAbertas.PedirCozinha(cx, conta.Id, conta.Destino, _operador, TurnoDeTeste)", StringComparison.Ordinal)
               && Ordem(pedir, "ContasAbertas.PedirCozinha(", "GuardarConta();") && pedir.Contains("PedidoNaCozinhaGuardada(numero)", StringComparison.Ordinal)
               && pedir.Contains("ComboIncompleto(dono)", StringComparison.Ordinal) && pedir.Contains("NadaNovo", StringComparison.Ordinal),
            "CA-5 Pedir a cozinha: barra combo e cardapio, lote pelo nucleo, guarda sozinho e avisa; nada novo = 'Nada novo para a cozinha.'");
        checar(Metodo(comanda, "private void CutucarCozinha(string loteId, long numero)").Contains("DispatcherTimer", StringComparison.Ordinal)
               && comanda.Contains("Textos.CozinhaRecebeuEGuardou(numero), \"ok\"", StringComparison.Ordinal),
            "CA-5 o toast honesto: a confirmacao da TV vem por timer (a tela segue viva) e vira 'Cozinha recebeu. Comanda N guardada.'");
        var cancelar = Metodo(comanda, "private void CancelarContaComAutorizacao(string id)");
        checar(cancelar.Contains("PedirTexto.Mostrar(", StringComparison.Ordinal) && cancelar.Contains("PedirOutroGerente(", StringComparison.Ordinal)
               && cancelar.Contains("ContasAbertas.Cancelar(cx, id, _operador, null, null);", StringComparison.Ordinal)
               && cancelar.Contains("ContasAbertas.Cancelar(cx, id, _operador, motivo, sup);", StringComparison.Ordinal),
            "CA-17 cancelar: vazia confirma; com item pede motivo e o PIN de outro gerente");
        var fechamento = Metodo(comanda, "private bool ComandasDeixamFechar(Window dono)");
        checar(fechamento.Contains("CancelarVaziasAutomatico(cx, _operador)", StringComparison.Ordinal) && fechamento.Contains("ComandasAbertas(resumo.AbertasN, resumo.Abertas)", StringComparison.Ordinal)
               && fechamento.Contains("FecharMesmoAssim", StringComparison.Ordinal) && fechamento.Contains("catch { return true; }", StringComparison.Ordinal),
            "CA-19 o fechamento cancela as vazias, avisa com 'Fechar mesmo assim' e nunca prende o caixa");
        checar(Metodo(comanda, "private IReadOnlyList<string>? ComandasAtravessaram(Microsoft.Data.Sqlite.SqliteConnection cx)").Contains("RegistrarAtravessamento(", StringComparison.Ordinal)
               && venda.Contains("MostrarResultado(dono, linhasFech, null, divergencias, comandas, resumoTeste, folha, papel, tefDisponivel);", StringComparison.Ordinal)
               && venda.Contains("destino.Papel.Colunas, comandas);", StringComparison.Ordinal),
            "CA-19 as contas que atravessam geram o evento e as duas linhas vao para a tela e para o papel");
        checar(Metodo(venda, "private async void AplicarCortesia(object sender, RoutedEventArgs e)").Contains("CupomPresoEmOutraConta(", StringComparison.Ordinal),
            "CA-15 AplicarCortesia recusa o cupom que ja esta em outra comanda");

        // a tela: os nomes e os clicks que o modo comanda precisa
        foreach (var nome in new[] { "BtnComandas", "TxtTituloComanda", "RunNumeroComanda", "RunNomeComanda", "FaixaComanda", "ColunaComanda",
                                     "LinhaCupom", "LarguraCupom", "LarguraCozinha", "BtnGuardar", "BtnCozinha", "TxtCozinhaRotulo", "TxtSetaFinalizar", "TxtIconeCupom" })
            checar(xaml.Contains($"x:Name=\"{nome}\"", StringComparison.Ordinal), $"CT-1 Venda.xaml tem {nome}");
        foreach (var click in new[] { "AbrirComandas", "GuardarComanda", "PedirCozinhaDaComanda" })
            checar(xaml.Contains($"Click=\"{click}\"", StringComparison.Ordinal), $"CT-1 Venda.xaml liga {click}");
        checar(xaml.Contains("SizeChanged=\"ColunaComandaRedimensionou\"", StringComparison.Ordinal)
               && Metodo(comanda, "private void AncorarToasts(double larguraDaColuna)").Contains("ToastAviso", StringComparison.Ordinal),
            "CT-1 os toasts ancoram na coluna dos produtos (nenhum cobre o botao Comandas)");
        var modo = Metodo(comanda, "private void PintarModoComanda()");
        checar(modo.Contains("TxtFinalizarRotulo.Text = modo ? ContasAbertas.Textos.FecharConta : \"Finalizar\";", StringComparison.Ordinal)
               && modo.Contains("TxtSetaFinalizar.Visibility = modo && estreita ? Visibility.Collapsed : Visibility.Visible;", StringComparison.Ordinal)
               && modo.Contains("ChipItens.Visibility = Visibility.Collapsed;", StringComparison.Ordinal)
               && modo.Contains("ColunaComanda.BorderThickness = new Thickness(4, 0, 0, 0);", StringComparison.Ordinal),
            "CT-1 em modo comanda: 'Fechar conta' (sem seta a 1024), chip oculto, faixa e borda ciano de 4 px");

        // o dialogo e o pagamento
        foreach (var t in new[] { "Textos.NovaComanda", "Textos.MandarDeNovo", "Textos.CancelarComanda(", "Textos.NaLoja", "Textos.ParaViagem",
                                  "Textos.NomeOuMesa", "Textos.AbrirComItens(", "Textos.GuardarCarrinhoEAbrirVazia", "Textos.Abrir,", "Textos.CarrinhoTem(", "Textos.Mesa(", "Textos.Balcao" })
            checar(dialogo.Contains(t, StringComparison.Ordinal), $"CT-2 DialogoComandas usa {t}");
        checar(dialogo.Contains("Dialogo.Base(dono, Largura)", StringComparison.Ordinal) && dialogo.Contains("Dialogo.Moldura(", StringComparison.Ordinal)
               && dialogo.Contains("MinHeight = 58, FontSize = 17", StringComparison.Ordinal) && !dialogo.Contains("\"Comanda aberta\"", StringComparison.Ordinal)
               && dialogo.Contains("AbrirTecladoVirtualSeTouch", StringComparison.Ordinal) && dialogo.Contains("if (atuais.Count == 0) MostrarNova(); else MostrarLista();", StringComparison.Ordinal),
            "CT-2 o dialogo e da casa (Base + Moldura, botoes de 58/17), nao se chama 'Comanda aberta', abre o teclado e com zero abertas vai direto a face Nova");
        checar(pag.Contains("ContaNoPagamento? conta = null", StringComparison.Ordinal) && pag.Contains("ResumoDoPagamento(c.Numero, c.Nome, _itens.Count, qtd)", StringComparison.Ordinal)
               && pag.Contains("ligado = ligado && conta.PendentesMilesimos > 0;", StringComparison.Ordinal) && pag.Contains("if (ligado) _cozinha = conta.Consumo;", StringComparison.Ordinal)
               && pag.Contains("_conta is null ? _cozinha : null, _conta?.Nome,", StringComparison.Ordinal) && pag.Contains("new ContaParaFechar(_conta.Id, _cozinha)", StringComparison.Ordinal),
            "CT-3 Pagamento: conta opcional, resumo 'Comanda 12 · Mesa 5 · ...', bloco da cozinha so com pendentes e pre-marcado, Finalizar com a conta");
        checar(pagXaml.Contains("Content=\"Voltar para a comanda\"", StringComparison.Ordinal) && pagXaml.Contains("Text=\"Para a cozinha\"", StringComparison.Ordinal),
            "CT-3 'Voltar para a comanda' e 'Para a cozinha' continuam intactos no XAML do pagamento");
    }

    // ── CA-22 (tela): nada de travessao no que e novo ───────────────────────
    private static void Textos(Action<bool, string> checar)
    {
        foreach (var (nome, texto) in new[]
                 {
                     ("Venda.Comanda.cs", Fonte("Telas", "Venda.Comanda.cs")), ("DialogoComandas.cs", Fonte("Telas", "DialogoComandas.cs")),
                     ("FotoComanda.cs", Fonte("Pdv.Testes", "FotoComanda.cs")), ("TestesComandaTela.cs", Fonte("Pdv.Testes", "TestesComandaTela.cs")),
                 })
            checar(texto.Length > 0 && !texto.Contains((char)0x2014) && !texto.Contains((char)0x2013), $"CA-22 nenhum travessao em {nome}");
        foreach (var (nome, texto) in new[] { ("Venda.xaml", Fonte("Telas", "Venda.xaml")), ("Pagamento.xaml", Fonte("Telas", "Pagamento.xaml")) })
        {
            var atributos = Regex.Matches(texto, @"(?:Text|ToolTip|Content)=""([^""]*)""").Select(m => m.Groups[1].Value).ToList();
            checar(atributos.Count > 0 && atributos.All(t => !t.Contains((char)0x2014) && !t.Contains((char)0x2013)),
                $"CA-22 nenhum Text/ToolTip/Content de {nome} tem travessao");
        }
        // as linhas NOVAS de Venda.xaml.cs e Pagamento.xaml.cs (as que falam da conta): sem travessao
        var novas = Fonte("Telas", "Venda.xaml.cs").Split('\n').Where(l => l.Contains("_contaAberta") || l.Contains("ContasAbertas") || l.Contains("COMANDA ABERTA (13/10"))
            .Concat(Fonte("Telas", "Pagamento.xaml.cs").Split('\n').Where(l => l.Contains("_conta") || l.Contains("ContaNoPagamento") || l.Contains("COMANDA ABERTA")))
            .ToList();
        checar(novas.Count >= 20 && novas.All(l => !l.Contains((char)0x2014) && !l.Contains((char)0x2013)),
            $"CA-22 as linhas novas de Venda.xaml.cs e Pagamento.xaml.cs nao tem travessao ({novas.Count} linhas)");
        checar(ContasAbertas.Textos.ResumoDoPagamento(12, "Mesa 5", 3, 5) == "Comanda 12 · Mesa 5 · 3 produtos · 5 itens"
               && ContasAbertas.Textos.ResumoDoPagamento(7, null, 1, 1) == "Comanda 7 · 1 produto · 1 item"
               && ContasAbertas.Textos.NaCozinha(2) == "2 na cozinha" && ContasAbertas.Textos.NaCozinha(1) == "1 na cozinha"
               && ContasAbertas.Textos.ContaGuardada(12) == "Comanda 12 guardada." && ContasAbertas.Textos.JaPedidosFicam.Length <= 60,
            "CA-22 os textos novos da tela: resumo do Pagamento com a contagem, 'N na cozinha', 'Comanda N guardada.'");
    }

    // ── o que da para provar sem janela ─────────────────────────────────────
    private static void NoNucleo(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"comanda_tela_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            using var cx = Banco.Abrir(arquivo);
            var op = new Operador("op-tela", "Cleide", "operador");
            Operadores.Salvar(cx, op.Id, op.Nome, "2468", "operador");
            var gerente = new Operador("ger-tela", "Marcos", "gerente");
            Operadores.Salvar(cx, gerente.Id, gerente.Nome, "1357", "gerente");
            SemearCatalogo(cx);
            Vendas.GravarConfig(cx, PedidoCozinha.ChaveToken, "chave-da-foto-0123456789abcdef");
            var sessao = Caixa.Abrir(cx, op, Dinheiro.DeReais(100));

            // CA-24: o corpo do dialogo do rascunho
            var corpo = Rascunho.CorpoDoAviso("14:32", 3, Dinheiro.DeReais(45), Rascunho.AvisoDeCobranca(Dinheiro.Zero), comandasGuardadas: true);
            checar(corpo.StartsWith($"O caixa parou às 14:32 com uma venda de balcão na tela: 3 itens · {Dinheiro.DeReais(45).Formatado()}. As comandas das mesas estão guardadas.", StringComparison.Ordinal)
                   && corpo.Contains("nenhum valor foi cobrado") && corpo.Contains("Ao continuar, os itens voltam"),
                "CA-24 com comanda aberta no banco o corpo diz 'As comandas das mesas estão guardadas.'");
            checar(!Rascunho.CorpoDoAviso("14:32", 1, Dinheiro.DeReais(9), "x", false).Contains("mesas") && Rascunho.CorpoDoAviso("14:32", 1, Dinheiro.DeReais(9), "x", false).Contains($"1 item · {Dinheiro.DeReais(9).Formatado()}."),
                "CA-24 sem comanda aberta a frase das mesas nao aparece (e 1 item no singular)");
            checar(corpo.Split('\n').All(l => l.Length < 200) && !corpo.Contains((char)0x2014), "CA-24 texto humano, sem travessao");

            // CA-23: conta aberta no banco NAO barra a atualizacao (so o rascunho barra)
            var conta = ContasAbertas.Abrir(cx, sessao, op, "Mesa 5", itens: new List<ItemRascunho> { Item(Ninho, "DONUT NINHO", 1200, 2000) });
            checar(Rascunho.Ler(cx, sessao.Id) is null && Pdv.Telas.AtualizarCaixa.OQueATelaViveAgora is null,
                "CA-23 com a conta no banco o rascunho esta vazio (e a tela nao se registrou no portao)");
            var estado = Pdv.Telas.AtualizarCaixa.EstadoSemTela();
            checar(estado.ItensNaComanda == 0 && !estado.EstadoIncerto, $"CA-23 AtualizarCaixa.EstadoSemTela com conta aberta e rascunho vazio: 0 itens na comanda, nada barra ({estado.ItensNaComanda})");

            // as linhas da lista, com todos os selos
            var lote1 = ContasAbertas.PedirCozinha(cx, conta.Id, null, op, false)!;   // 2 Ninho pedidos
            var mais = ContasAbertas.Ler(cx, conta.Id)!.Itens.ToList();
            mais[0] = mais[0] with { QtdMilesimos = 3000 };                             // +1 Ninho pendente
            ContasAbertas.Gravar(cx, conta.Id, mais, null, null, op);
            cx.Execute("UPDATE outbox SET enviado_em=@e WHERE tipo=@t AND ref_id=@r", new { e = DateTime.Now.ToString("o"), t = PedidoCozinha.TipoNaFila, r = lote1.Id });
            var naTv = ContasAbertas.Abrir(cx, sessao, op, "Joana", itens: new List<ItemRascunho> { Item(Agua, "AGUA 500ML", 500, 1000) });
            var lTv = ContasAbertas.PedirCozinha(cx, naTv.Id, null, op, false)!;
            cx.Execute("UPDATE outbox SET enviado_em=@e WHERE tipo=@t AND ref_id=@r", new { e = DateTime.Now.ToString("o"), t = PedidoCozinha.TipoNaFila, r = lTv.Id });
            var fila = ContasAbertas.Abrir(cx, sessao, op, "Mesa 2", itens: new List<ItemRascunho> { Item(Homer, "DONUT HOMER", 1200, 1000) });
            ContasAbertas.PedirCozinha(cx, fila.Id, null, op, false);
            var naoFoi = ContasAbertas.Abrir(cx, sessao, op, "Balcão", itens: new List<ItemRascunho> { Item(Agua, "AGUA 500ML", 500, 2000) });
            var lNao = ContasAbertas.PedirCozinha(cx, naoFoi.Id, null, op, false)!;
            cx.Execute("UPDATE outbox SET desistido_em=@e WHERE tipo=@t AND ref_id=@r", new { e = DateTime.Now.ToString("o"), t = PedidoCozinha.TipoNaFila, r = lNao.Id });
            var maquininha = ContasAbertas.Abrir(cx, sessao, op, "Mesa 7", itens: new List<ItemRascunho> { Item(Ninho, "DONUT NINHO", 1200, 1000) });
            ContasAbertas.MarcarFechando(cx, maquininha.Id);
            var vazia = ContasAbertas.Abrir(cx, sessao, op, null);

            var linhas = ContasAbertas.LinhasDaLista(cx, sessao, conta.Id, teste: false);
            string Selo(string id) => linhas.First(l => l.Conta.Id == id).Selo;
            checar(linhas.Count == 6 && linhas.Select(l => l.Conta.Numero).SequenceEqual(linhas.Select(l => l.Conta.Numero).OrderBy(n => n)),
                $"CT-4 LinhasDaLista: uma linha por conta aberta, por numero ({linhas.Count})");
            var mesa5 = linhas.First(l => l.Conta.Id == conta.Id);
            checar(mesa5.NaTela && mesa5.Selo == ContasAbertas.Textos.NaTela && mesa5.Titulo == $"{conta.Numero} · Mesa 5"
                   && mesa5.Itens == ContasAbertas.Textos.ItensEValor(3, new Dinheiro(3600)) && mesa5.Cozinha == "cozinha 2 de 3" && mesa5.LoteParaMandarDeNovo is null,
                $"CT-4 a conta na tela: 'na tela', '3 itens · R$ 36,00', 'cozinha 2 de 3' ({mesa5.Itens} / {mesa5.Cozinha} / {mesa5.Selo})");
            checar(Selo(naTv.Id) == ContasAbertas.Textos.NaTv && Selo(fila.Id) == ContasAbertas.Textos.SeloFila && Selo(naoFoi.Id) == ContasAbertas.Textos.SeloNaoFoi,
                $"CT-4 os selos do lote: na TV, fila, nao foi ({Selo(naTv.Id)} / {Selo(fila.Id)} / {Selo(naoFoi.Id)})");
            checar(linhas.First(l => l.Conta.Id == naoFoi.Id).LoteParaMandarDeNovo == lNao.Id && linhas.First(l => l.Conta.Id == fila.Id).LoteParaMandarDeNovo is null,
                "CT-4 so a linha 'nao foi' ganha o [Mandar de novo], com o lote perdido");
            checar(Selo(maquininha.Id) == ContasAbertas.Textos.ConfiraMaquininha && linhas.First(l => l.Conta.Id == maquininha.Id).ConferirMaquininha,
                "CT-4 fechando_em de pe sem venda = 'confira a maquininha' (o caixa caiu com o Pagamento aberto)");
            var linhaVazia = linhas.First(l => l.Conta.Id == vazia.Id);
            checar(linhaVazia.Itens == ContasAbertas.Textos.ZeroItens && linhaVazia.Selo == "" && linhaVazia.Cozinha == "" && linhaVazia.Titulo == vazia.Numero.ToString(),
                "CT-4 conta vazia sem nome: '0 itens', sem selo, titulo so com o numero");
            using (var outro = OutroProcessoVivo())
            {
                cx.Execute("UPDATE conta_aberta SET dono = @d WHERE id = @id", new { d = outro.Dono, id = naTv.Id });
                var l2 = ContasAbertas.LinhasDaLista(cx, sessao, conta.Id, false).First(l => l.Conta.Id == naTv.Id);
                checar(l2.EmUso && l2.Selo == ContasAbertas.Textos.EmUsoNoOutroCaixa, "CT-4 conta carregada noutro Pdv.exe vivo: 'em uso no outro caixa'");
            }
            cx.Execute("UPDATE conta_aberta SET dono = NULL WHERE id = @id", new { id = naTv.Id });
            foreach (var l in linhas)
                checar(!l.Titulo.Contains('\n') && !l.Itens.Contains((char)0x2014) && l.Selo.Length <= 24 && l.Cozinha.Length <= 24, $"CT-4 textos curtos da linha {l.Titulo}");

            // o papel do fechamento com as duas linhas
            var papel = PapelDeCaixa.Fechamento("American Day Savassi", new DateTime(2026, 10, 13, 23, 41, 0), sessao, "Cleide",
                ResumoFechamento.Linhas(new[] { new LinhaFechamento("dinheiro", Dinheiro.DeReais(100), Dinheiro.DeReais(100)) }),
                Dinheiro.Zero, new List<string>(), null, null, null, semContagem: false, autorizador: null, 32,
                ContasAbertas.LinhasDoFechamento(cx, sessao));
            checar(papel.Any(l => l.StartsWith("Comandas abertas: 6", StringComparison.Ordinal)) && papel.Any(l => l.StartsWith("Comandas canceladas no turno: 0", StringComparison.Ordinal))
                   && papel.All(l => l.Length <= 32),
                "CA-19 o papel do fechamento leva as duas linhas de comanda e nenhuma estoura 32 colunas");
            var semComanda = PapelDeCaixa.Fechamento("American Day Savassi", new DateTime(2026, 10, 13, 23, 41, 0), sessao, "Cleide",
                ResumoFechamento.Linhas(new[] { new LinhaFechamento("dinheiro", Dinheiro.DeReais(100), Dinheiro.DeReais(100)) }),
                Dinheiro.Zero, new List<string>(), null, null, null, semContagem: false, autorizador: null, 32);
            checar(!semComanda.Any(l => l.Contains("Comandas")), "CA-19 sem as linhas (loja sem comanda) o papel sai como sempre");

            foreach (var c in ContasAbertas.Listar(cx, false)) ContasAbertas.Cancelar(cx, c.Id, op, "teste", c.Itens.Count > 0 ? gerente : null);
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }

    // ── a tela de verdade, no HostWpf ───────────────────────────────────────
    private static void NaTela(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"comanda_tela_wpf_{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        Exception? erro = null;
        try
        {
            Banco.Migrar(arquivo);
            SemearTela(arquivo);
            try { HostWpf.Executar(() => Passos(checar)); }
            catch (Exception ex) { erro = ex; }
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
        checar(erro is null, "CT-5 a tela de venda com comanda subiu e os passos rodaram (" + (erro?.ToString() ?? "ok") + ")");
    }

    private static void SemearTela(string arquivo)
    {
        using var cx = Banco.Abrir(arquivo);
        var agora = DateTime.Now.ToString("o");
        cx.Execute("""
            INSERT INTO terminal (id, terminal_uuid, loja_id, loja_nome, cnpj, serie_nfce, ambiente, api_base, criado_em)
            VALUES (1, 'term-comanda', 'loja-1', 'Loja Teste', '00000000000000', 1, 2, 'http://127.0.0.1:9', @a)
            """, new { a = agora });
        // Sem isto o teste pergunta a versao a PRODUCAO com o uuid de teste,
        // e nasce um terminal fantasma na frota do dono. Ver Isolamento.cs.
        Isolamento.SemNuvem(cx);
        Operadores.Salvar(cx, "op-ui", "Cleide", "2468", "operador");
        Operadores.Salvar(cx, "ger-ui", "Marcos", "1357", "gerente");
        Operadores.Salvar(cx, "ger-ui-2", "Paula", "9753", "gerente");
        SemearCatalogo(cx);
        Vendas.GravarConfig(cx, PedidoCozinha.ChaveToken, "chave-da-foto-0123456789abcdef");
        Vendas.GravarConfig(cx, PedidoCozinha.ChaveUrl, Isolamento.UrlMorta);
        Vendas.GravarConfig(cx, "modo_fiscal", "recibo");
    }

    private static void Passos(Action<bool, string> checar)
    {
        var host = new Window
        {
            Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0,
        };
        host.Show();
        Operador op; Sessao sessao;
        using (var cx = Banco.Abrir())
        {
            op = Operadores.PorId(cx, "op-ui")!;
            sessao = Caixa.Abrir(cx, op, Dinheiro.DeReais(100));
        }
        var tela = new Pdv.Telas.Venda(op, sessao);
        host.Content = tela;
        host.UpdateLayout();
        Definir(tela, "_rascunhoOferecido", true);   // sem isto a tela nao grava (ver Venda.SalvarRascunho)
        var catalogo = Campo<System.Collections.IList>(tela, "_catalogo").Cast<Pdv.Telas.Produto>().ToList();
        var comanda = Campo<List<Pdv.Telas.ItemComanda>>(tela, "_comanda");
        string Titulo() => Campo<TextBlock>(tela, "TxtTituloComanda").Text;
        string Toast() => Campo<TextBlock>(tela, "TxtToastAviso").Text;
        Pdv.Nucleo.ContaAberta? Conta() => Campo<Pdv.Nucleo.ContaAberta?>(tela, "_contaAberta");

        // ── balcao: nada regrediu, e o botao Comandas esta la ──────────────
        checar(Titulo() == "Comanda" && Campo<Button>(tela, "BtnComandas").Visibility == Visibility.Visible
               && Campo<TextBlock>(tela, "TxtRotuloComandas").Visibility == Visibility.Collapsed
               && Campo<Button>(tela, "BtnGuardar").Visibility == Visibility.Collapsed && Campo<Button>(tela, "BtnCozinha").Visibility == Visibility.Collapsed
               && Campo<TextBlock>(tela, "TxtFinalizarRotulo").Text == "Finalizar" && Campo<Border>(tela, "ColunaComanda").BorderThickness.Left == 1,
            "CT-5 balcao a 1024: titulo 'Comanda', botao Comandas so com o glifo, sem Guardar/Cozinha, 'Finalizar', borda de 1 px");
        var agua = catalogo.First(p => p.Id == Agua);
        var ninho = catalogo.First(p => p.Id == Ninho);
        Invocar(tela, "Adicionar", agua);
        host.UpdateLayout();
        checar(Campo<Border>(tela, "ChipItens").Visibility == Visibility.Visible && comanda[0].LinhaId.Length == 32 && comanda[0].CozinhaMilesimos == 0,
            "CT-5 balcao: o chip 'N itens' aparece e a linha nasce com LinhaId e sem marca de cozinha");
        using (var cx = Banco.Abrir())
            checar(Rascunho.Ler(cx, sessao.Id) is { Itens.Count: 1 } && ContasAbertas.ContarAbertas(cx, false) == 0, "CT-5 balcao: o carrinho vai para o rascunho, como sempre");

        // ── a conta: aberta no banco com 3 itens, 2 pedidos, e carregada na tela ─
        string contaId; long numero;
        using (var cx = Banco.Abrir())
        {
            var c = ContasAbertas.Abrir(cx, sessao, op, "Mesa 5", itens: new List<ItemRascunho>
            {
                Item(Ninho, "DONUT NINHO", 1200, 2000), Item(Homer, "DONUT HOMER", 1200, 1000),
            });
            var lote1 = ContasAbertas.PedirCozinha(cx, c.Id, null, op, false)!;
            // o lote 1 ja esta na TV: o botao Cozinha nao ganha o aviso
            cx.Execute("UPDATE outbox SET enviado_em=@e WHERE tipo=@t AND ref_id=@r", new { e = DateTime.Now.ToString("o"), t = PedidoCozinha.TipoNaFila, r = lote1.Id });
            var itens = ContasAbertas.Ler(cx, c.Id)!.Itens.ToList();
            itens[0] = itens[0] with { QtdMilesimos = 3000 };     // 3 Ninho, 2 na cozinha
            itens.Add(Item(Agua, "AGUA 500ML", 500, 1000));          // 1 agua, nunca pedida
            ContasAbertas.Gravar(cx, c.Id, itens, null, null, op);
            ContasAbertas.Soltar(cx, c.Id);
            contaId = c.Id; numero = c.Numero;
        }
        // o carrinho solto tem 1 item: a carga direta (sem o dialogo) o descarta; aqui o teste esvazia antes
        comanda.Clear();
        Invocar(tela, "PintarComanda");
        var carregou = (bool)Invocar(tela, "CarregarConta", contaId)!;
        host.UpdateLayout();
        checar(carregou && Conta()?.Id == contaId && comanda.Count == 3, "CT-6 CarregarConta carrega a conta: 3 linhas na tela");
        var linhaNinho = comanda.First(i => i.Produto.Id == Ninho);
        var linhaHomer = comanda.First(i => i.Produto.Id == Homer);
        checar(linhaNinho.Qtd.Milesimos == 3000 && linhaNinho.CozinhaMilesimos == 2000 && linhaHomer.CozinhaMilesimos == 1000
               && comanda.First(i => i.Produto.Id == Agua).CozinhaMilesimos == 0,
            "CT-6 CozinhaMilesimos veio do disco (3 Ninho com 2 na cozinha, 1 Homer pedido, agua nunca pedida)");
        checar(Titulo() == $"{numero} · Mesa 5" && Campo<Border>(tela, "ChipItens").Visibility == Visibility.Collapsed
               && Campo<Border>(tela, "ColunaComanda").BorderThickness.Left == 4 && Campo<Border>(tela, "FaixaComanda").BorderThickness.Left == 1,
            $"CT-6 cabecalho em modo comanda: '{numero} · Mesa 5', chip oculto, borda de 4 px e faixa pintada (titulo '{Titulo()}')");
        checar(Campo<Button>(tela, "BtnGuardar").Visibility == Visibility.Visible && Campo<Button>(tela, "BtnCozinha").Visibility == Visibility.Visible
               && Campo<TextBlock>(tela, "TxtCozinhaRotulo").Text == "Cozinha 2"
               && Campo<TextBlock>(tela, "TxtFinalizarRotulo").Text == "Fechar conta" && Campo<TextBlock>(tela, "TxtSetaFinalizar").Visibility == Visibility.Collapsed
               && Campo<TextBlock>(tela, "TxtCupomRotulo").Visibility == Visibility.Collapsed,
            $"CT-6 rodape em modo comanda a 1024: Guardar, 'Cozinha 2' (1 Ninho + 1 agua pendentes), 'Fechar conta' sem seta, cupom so com o icone ({Campo<TextBlock>(tela, "TxtCozinhaRotulo").Text})");
        checar(SubLinha(tela, ninho.Nome)?.Contains("2 na cozinha") == true && SubLinha(tela, agua.Nome)?.Contains("na cozinha") != true,
            $"CT-6 a sub-linha diz '· 2 na cozinha' so na linha pedida ({SubLinha(tela, ninho.Nome)})");
        using (var cx = Banco.Abrir())
            checar(Rascunho.Ler(cx, sessao.Id) is null && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM comanda_rascunho") == 0
                   && ContasAbertas.Ler(cx, contaId)!.Dono is { Length: > 0 } && ContasAbertas.Ler(cx, contaId)!.Itens.Count == 3,
                "CA-4 com a conta carregada comanda_rascunho esta VAZIA e a conta tem o dono deste processo");

        // ── lancar: cada mudanca grava na conta, nao no rascunho ───────────
        Invocar(tela, "Adicionar", agua);
        host.UpdateLayout();
        using (var cx = Banco.Abrir())
        {
            var lida = ContasAbertas.Ler(cx, contaId)!;
            checar(lida.Itens.First(i => i.ProdutoId == Agua).QtdMilesimos == 2000 && Rascunho.Ler(cx, sessao.Id) is null,
                "CT-7 lancar na comanda grava na conta (2 aguas) e o rascunho continua vazio");
        }
        checar(Campo<TextBlock>(tela, "TxtCozinhaRotulo").Text == "Cozinha 3", $"CT-7 o botao Cozinha conta o pendente novo ({Campo<TextBlock>(tela, "TxtCozinhaRotulo").Text})");

        // ── menos cruzando o pedido: PIN de OUTRO gerente, uma janela ──────
        var menos = BotaoDaComanda(tela, "Diminuir " + ninho.Nome);
        Clicar(menos);   // 3 -> 2: ainda acima dos 2 pedidos, livre
        host.UpdateLayout();
        checar(linhaNinho.Qtd.Milesimos == 2000 && linhaNinho.CozinhaMilesimos == 2000, "CT-8 descer ate o que ja foi pedido e livre (3 -> 2)");
        // 2 -> 1 cruza: PIN. 1) Cancelar: nada muda. 2) PIN errado: aviso e a janela reabre; o proprio gerente nao vale; 3) outro gerente tira.
        Roteiro(host, d => Clicar(Botao(d, "Cancelar")));
        Clicar(BotaoDaComanda(tela, "Diminuir " + ninho.Nome));
        checar(linhaNinho.Qtd.Milesimos == 2000, "CT-8 Cancelar na janela do PIN: a linha fica como estava");
        var janelasPin = 0;
        Roteiro(host,
            d => { janelasPin++; Senha(d, "0000"); Clicar(Botao(d, "Confirmar")); },                 // PIN errado
            d => Clicar(Botao(d, "Entendi")),                                                           // "PIN nao confere"
            d => { janelasPin++; Senha(d, "1357"); Clicar(Botao(d, "Confirmar")); },                 // Marcos, gerente, mas vale (ele nao opera)
            d => { /* so se abrir outra */ });
        Clicar(BotaoDaComanda(tela, "Diminuir " + ninho.Nome));
        host.UpdateLayout();
        checar(janelasPin == 2 && linhaNinho.Qtd.Milesimos == 1000 && linhaNinho.CozinhaMilesimos == 1000,
            $"CT-8 PIN errado avisa e reabre; o PIN de outro gerente tira: 2 -> 1 e a marca baixa junto ({janelasPin} janelas, qtd {linhaNinho.Qtd.Milesimos})");
        using (var cx = Banco.Abrir())
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM auditoria WHERE evento='conta_item_retirado_apos_cozinha'") == 1
                   && ContasAbertas.Ler(cx, contaId)!.Itens.First(i => i.ProdutoId == Ninho).QtdMilesimos == 1000,
                "CT-8 a retirada ficou na auditoria e no disco");

        // ── Limpar em modo comanda: so o que nao foi pedido ────────────────
        Roteiro(host, d => Clicar(Botao(d, "Limpar comanda")));
        Invocar(tela, "LimparComanda", null, new RoutedEventArgs());
        host.UpdateLayout();
        checar(comanda.Count == 2 && comanda.All(i => i.CozinhaMilesimos > 0) && comanda.All(i => i.Qtd.Milesimos == i.CozinhaMilesimos)
               && Toast() == ContasAbertas.Textos.JaPedidosFicam && Conta() is not null,
            $"CT-9 Limpar tira a agua (nunca pedida) e avisa que o pedido fica ({comanda.Count} linhas, toast '{Toast()}')");

        // ── Guardar: solta e volta ao balcao ───────────────────────────────
        Invocar(tela, "GuardarConta");
        host.UpdateLayout();
        checar(Conta() is null && comanda.Count == 0 && Titulo() == "Comanda" && Campo<Button>(tela, "BtnGuardar").Visibility == Visibility.Collapsed
               && Campo<TextBlock>(tela, "TxtFinalizarRotulo").Text == "Finalizar",
            "CT-10 Guardar: a tela volta ao balcao vazio");
        using (var cx = Banco.Abrir())
        {
            var lida = ContasAbertas.Ler(cx, contaId)!;
            checar(lida.Aberta && lida.Dono is null && lida.Itens.Count == 2 && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM comanda_rascunho") == 0,
                "CT-10 a conta continua aberta no banco, sem dono, com os 2 itens, e o rascunho continua vazio");
        }

        // ── Pedir a cozinha: lote, guarda sozinho, toast honesto ───────────
        Invocar(tela, "CarregarConta", contaId);
        Invocar(tela, "Adicionar", agua);
        host.UpdateLayout();
        var lotesAntes = 0;
        using (var cx = Banco.Abrir()) lotesAntes = ContasAbertas.Lotes(cx, contaId).Count;
        Invocar(tela, "PedirCozinhaDaConta");
        host.UpdateLayout();
        using (var cx = Banco.Abrir())
        {
            var lotes = ContasAbertas.Lotes(cx, contaId);
            checar(lotes.Count == lotesAntes + 1 && lotes[^1].Itens.Count == 1 && lotes[^1].Itens[0].Nome == "AGUA 500ML"
                   && ContasAbertas.Ler(cx, contaId)!.Dono is null,
                "CT-11 Pedir a cozinha: o lote leva so a agua (o pendente) e a conta e guardada sozinha");
        }
        checar(Conta() is null && Toast() == ContasAbertas.Textos.PedidoNaCozinhaGuardada(numero), $"CT-11 toast 'Pedido na cozinha. Comanda {numero} guardada.' ('{Toast()}')");
        Invocar(tela, "CarregarConta", contaId);
        host.UpdateLayout();
        Invocar(tela, "PedirCozinhaDaConta");
        checar(Conta() is not null && Toast() == ContasAbertas.Textos.NadaNovo, $"CT-11 sem pendente: 'Nada novo para a cozinha.' e a conta fica ('{Toast()}')");
        // revisoes 13 e 14/10: a 1024 o rotulo com aviso fica "⚠ Coz. N" (o "⚠ Cozinha N" cortava e o "⚠ N" nao dizia de que era)
        var rotuloCozinha = Campo<TextBlock>(tela, "TxtCozinhaRotulo");
        checar(rotuloCozinha.Text == $"⚠ {ContasAbertas.Textos.CozinhaCurta(0)}",
            $"CT-11 com o ultimo lote ainda na fila o botao diz '⚠ Coz. 0' a 1024 ({rotuloCozinha.Text})");
        var rotuloDeAntes = rotuloCozinha.Text;
        foreach (var t in new[] { $"⚠ {ContasAbertas.Textos.CozinhaCurta(0)}", $"⚠ {ContasAbertas.Textos.CozinhaCurta(12)}" })
        {
            rotuloCozinha.Text = t;
            host.UpdateLayout();
            var natural = new FormattedText(t, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(rotuloCozinha.FontFamily, rotuloCozinha.FontStyle, rotuloCozinha.FontWeight, rotuloCozinha.FontStretch),
                rotuloCozinha.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(rotuloCozinha).PixelsPerDip).WidthIncludingTrailingWhitespace;
            checar(natural <= rotuloCozinha.ActualWidth + 0.5, $"CT-11 '{t}' cabe inteiro no botao a 1024 ({natural:0.0} de {rotuloCozinha.ActualWidth:0.0} px)");
        }
        rotuloCozinha.Text = rotuloDeAntes;
        host.UpdateLayout();

        // ── Fechar conta: o Pagamento com a comanda ────────────────────────
        Invocar(tela, "Adicionar", agua);   // 1 pendente para o bloco da cozinha aparecer
        host.UpdateLayout();
        Invocar(tela, "Finalizar", null, new RoutedEventArgs());
        host.UpdateLayout();
        var painel = Campo<ContentControl>(tela, "PainelPagamento");
        var pg = painel.Content as Pdv.Telas.Pagamento;
        checar(pg is not null && painel.Visibility == Visibility.Visible, "CT-12 Fechar conta abre o Pagamento");
        if (pg is not null)
        {
            var resumo = Campo<TextBlock>(pg, "TxtResumo").Text;
            checar(resumo.StartsWith($"Comanda {numero} · Mesa 5 · 3 produtos · 4 itens", StringComparison.Ordinal), $"CT-12 TxtResumo: 'Comanda {numero} · Mesa 5 · 3 produtos · 4 itens' ('{resumo}')");
            var bloco = Campo<Border>(pg, "BlocoCozinha");
            var loja = Campo<Button>(pg, "BtnCozinhaLoja");
            checar(bloco.Visibility == Visibility.Visible && loja.Style == (Style)Application.Current.Resources["BotaoPrincipal"]
                   && Campo<Pdv.Nucleo.DestinoCozinha?>(pg, "_cozinha") == Pdv.Nucleo.DestinoCozinha.Loja,
                "CT-12 o bloco 'Para a cozinha' aparece (ha 1 pendente) pre-marcado com o consumo da conta (na loja)");
            using (var cx = Banco.Abrir())
                checar(ContasAbertas.Ler(cx, contaId)!.FechandoEm is not null, "CA-12 fechando_em marcado ao abrir o Pagamento");
            // Voltar para a comanda
            Invocar(pg, "Cancelar", null, new RoutedEventArgs());
            host.UpdateLayout();
            using (var cx = Banco.Abrir())
                checar(ContasAbertas.Ler(cx, contaId)!.FechandoEm is null && Conta()?.Id == contaId && painel.Visibility == Visibility.Collapsed,
                    "CA-12 Voltar para a comanda limpa fechando_em e a conta continua carregada");
        }
        // tudo ja pedido: o bloco da cozinha some
        Invocar(tela, "PedirCozinhaDaConta");
        Invocar(tela, "CarregarConta", contaId);
        host.UpdateLayout();
        Invocar(tela, "Finalizar", null, new RoutedEventArgs());
        host.UpdateLayout();
        var pg2 = painel.Content as Pdv.Telas.Pagamento;
        checar(pg2 is not null && Campo<Border>(pg2, "BlocoCozinha").Visibility == Visibility.Collapsed,
            "CT-12 com tudo ja pedido o bloco 'Para a cozinha' some (nada vai de novo)");
        // a venda de verdade pelo nucleo (CA-9 prova a transacao); a tela so recebe o Encerrou
        if (pg2 is not null)
        {
            var itensVenda = Campo<IReadOnlyList<LinhaVenda>>(pg2, "_itens");
            using (var cx = Banco.Abrir())
            {
                var total = new Dinheiro(itensVenda.Sum(i => i.Total.Centavos));
                var v = Vendas.Finalizar(cx, sessao, op, itensVenda, new[] { new PagamentoVenda("dinheiro", total, Dinheiro.Zero) }, null, "Loja Teste", null,
                    null, "Mesa 5", new ContaParaFechar(contaId, null));
                checar(ContasAbertas.Ler(cx, contaId)!.Status == "fechada" && ContasAbertas.Ler(cx, contaId)!.VendaId == v.Id, "CT-13 a conta fechou na transacao da venda");
            }
            var encerrou = (Action<Pdv.Telas.DesfechoVenda>?)typeof(Pdv.Telas.Pagamento).GetField("Encerrou", P)!.GetValue(pg2);
            encerrou?.Invoke(Pdv.Telas.DesfechoVenda.Concluida);
            host.UpdateLayout();
            checar(Conta() is null && comanda.Count == 0 && Titulo() == "Comanda" && painel.Visibility == Visibility.Collapsed,
                "CT-13 Encerrou(Concluida): a conta e solta antes de esvaziar e a tela volta ao balcao");
            using (var cx = Banco.Abrir())
                checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM comanda_rascunho") == 0 && ContasAbertas.Ler(cx, contaId)!.Status == "fechada",
                    "CT-13 nada foi regravado numa conta fechada e o rascunho continua vazio");
        }

        // ── o dialogo Comandas: lista com selos, face Nova, cancelar ───────
        string naTvId, naoFoiId;
        using (var cx = Banco.Abrir())
        {
            var naTv = ContasAbertas.Abrir(cx, sessao, op, "Joana", itens: new List<ItemRascunho> { Item(Agua, "AGUA 500ML", 500, 1000) });
            var l1 = ContasAbertas.PedirCozinha(cx, naTv.Id, null, op, false)!;
            cx.Execute("UPDATE outbox SET enviado_em=@e WHERE tipo=@t AND ref_id=@r", new { e = DateTime.Now.ToString("o"), t = PedidoCozinha.TipoNaFila, r = l1.Id });
            var naoFoi = ContasAbertas.Abrir(cx, sessao, op, "Mesa 2", itens: new List<ItemRascunho> { Item(Homer, "DONUT HOMER", 1200, 2000) });
            var l2 = ContasAbertas.PedirCozinha(cx, naoFoi.Id, null, op, false)!;
            cx.Execute("UPDATE outbox SET desistido_em=@e WHERE tipo=@t AND ref_id=@r", new { e = DateTime.Now.ToString("o"), t = PedidoCozinha.TipoNaFila, r = l2.Id });
            ContasAbertas.Soltar(cx, naTv.Id); ContasAbertas.Soltar(cx, naoFoi.Id);
            naTvId = naTv.Id; naoFoiId = naoFoi.Id;
        }
        long numTv, numNaoFoi;
        using (var cx = Banco.Abrir()) { numTv = ContasAbertas.Ler(cx, naTvId)!.Numero; numNaoFoi = ContasAbertas.Ler(cx, naoFoiId)!.Numero; }
        Invocar(tela, "CarregarConta", naTvId);
        host.UpdateLayout();
        List<string> selos = new(); var linhasNoDialogo = 0; var temCancelar = false; var temMandar = false;
        Roteiro(host, d =>
        {
            linhasNoDialogo = Descendentes<Button>(d).Count(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Comanda "));
            selos = Descendentes<TextBlock>(d).Where(t => AutomationProperties.GetName(t) == "Selo").Select(t => t.Text).ToList();
            temCancelar = Descendentes<Button>(d).Any(b => b.Content as string == ContasAbertas.Textos.CancelarComanda(numTv));
            temMandar = Descendentes<Button>(d).Any(b => b.Content as string == ContasAbertas.Textos.MandarDeNovo);
            Clicar(Botao(d, ContasAbertas.Textos.NovaComanda));
            // face Nova: chips, campo, consumo, Abrir
            var chips = Descendentes<Button>(d).Select(b => b.Content as string ?? "").Where(c => c.StartsWith("Mesa ") || c == ContasAbertas.Textos.Balcao).ToList();
            var caixa = Descendentes<TextBox>(d).First(t => AutomationProperties.GetName(t) == ContasAbertas.Textos.NomeOuMesa);
            Clicar(Botao(d, "Mesa 3"));
            var nome = caixa.Text;
            Clicar(Botao(d, ContasAbertas.Textos.ParaViagem));
            checar(chips.Count == ContasAbertas.MesasPadrao + 1 && chips[0] == "Mesa 1" && chips[^1] == ContasAbertas.Textos.Balcao && nome == "Mesa 3",
                $"CT-15 face Nova: {chips.Count} chips (Mesa 1 a Mesa 8 e Balcão) e o toque preenche 'Nome ou mesa' ('{nome}')");
            checar(Descendentes<TextBlock>(d).All(t => AutomationProperties.GetName(t) != "Carrinho"), "CT-15 com conta na tela nao ha carrinho solto: sem a linha 'O carrinho tem N itens.'");
            Clicar(Botao(d, ContasAbertas.Textos.Abrir));
        });
        Invocar(tela, "AbrirDialogoComandas");
        host.UpdateLayout();
        checar(linhasNoDialogo == 2 && selos.Contains(ContasAbertas.Textos.NaTela) && selos.Contains(ContasAbertas.Textos.SeloNaoFoi) && temCancelar && temMandar,
            $"CT-14 a lista: 2 linhas, selos 'na tela' e 'nao foi', [Cancelar comanda N] no rodape e [Mandar de novo] na linha perdida ({linhasNoDialogo} linhas, selos {string.Join("/", selos)})");
        checar(Conta() is { Nome: "Mesa 3", Consumo: "viagem" } && Toast().StartsWith("Comanda ", StringComparison.Ordinal) && Toast().EndsWith(" aberta.", StringComparison.Ordinal),
            $"CT-15 Abrir cria 'Mesa 3' para viagem e carrega ('{Toast()}', conta {Conta()?.Nome}/{Conta()?.Consumo})");
        using (var cx = Banco.Abrir())
            checar(ContasAbertas.Ler(cx, naTvId)!.Dono is null && ContasAbertas.ContarAbertas(cx, false) == 3, "CT-15 a conta de antes foi guardada (sem dono) e agora ha 3 abertas");

        // nome duplicado: "Mesa 3 ja esta aberta" -> Abrir a N (carrega a que existe)
        var idMesa3 = Conta()!.Id;
        var numMesa3 = Conta()!.Numero;
        Invocar(tela, "GuardarConta");
        Roteiro(host,
            d => { Clicar(Botao(d, ContasAbertas.Textos.NovaComanda)); Clicar(Botao(d, "Mesa 3")); Clicar(Botao(d, ContasAbertas.Textos.Abrir)); },
            d => Clicar(Botao(d, $"Abrir a {numMesa3}")));
        Invocar(tela, "AbrirDialogoComandas");
        host.UpdateLayout();
        using (var cx = Banco.Abrir())
            checar(Conta()?.Id == idMesa3 && ContasAbertas.ContarAbertas(cx, false) == 3, "CT-15 nome duplicado: 'Mesa 3 já está aberta' e 'Abrir a N' carrega a que existe (nenhuma nova)");

        // carrinho solto cheio ao tocar outra conta: Guardar e abrir (o carrinho vira uma conta nova)
        Invocar(tela, "GuardarConta");
        Invocar(tela, "Adicionar", agua);
        host.UpdateLayout();
        Roteiro(host,
            d => Clicar(Botao(d, $"Comanda {numTv}")),
            d => Clicar(Botao(d, ContasAbertas.Textos.GuardarEAbrir(numTv))));
        Invocar(tela, "AbrirDialogoComandas");
        host.UpdateLayout();
        using (var cx = Banco.Abrir())
        {
            var abertas = ContasAbertas.Listar(cx, false);
            checar(Conta()?.Id == naTvId && abertas.Count == 4 && abertas.Any(c => c.Nome is null && c.Itens.Count == 1 && c.Itens[0].ProdutoId == Agua && c.Dono is null)
                   && Rascunho.Ler(cx, sessao.Id) is null,
                "CT-16 'Guardar e abrir a N': o carrinho solto vira uma conta automatica guardada e a N e carregada; rascunho vazio");
        }

        // cancelar a conta na tela: motivo + PIN de outro gerente
        var cancelarRotulo = ContasAbertas.Textos.CancelarComanda(Conta()!.Numero);
        Roteiro(host,
            d => Clicar(Botao(d, cancelarRotulo)),
            d => { Descendentes<TextBox>(d).First().Text = "cliente foi embora"; Clicar(Botao(d, "Confirmar")); },
            d => { Senha(d, "9753"); Clicar(Botao(d, "Confirmar")); });
        Invocar(tela, "AbrirDialogoComandas");
        host.UpdateLayout();
        using (var cx = Banco.Abrir())
        {
            var c = ContasAbertas.Ler(cx, naTvId)!;
            checar(c.Status == "cancelada" && c.CancelMotivo == "cliente foi embora" && c.CancelAutorizador == "ger-ui-2" && Conta() is null && comanda.Count == 0,
                $"CT-17 Cancelar comanda com item: motivo e PIN de outro gerente, a tela volta ao balcao ({c.Status}, {c.CancelMotivo}, {c.CancelAutorizador})");
        }

        // Mandar de novo, de dentro da lista
        Roteiro(host, d => { Clicar(Botao(d, $"{ContasAbertas.Textos.MandarDeNovo} {numNaoFoi}")); Clicar(Botao(d, "Voltar")); });
        Invocar(tela, "AbrirDialogoComandas");
        host.UpdateLayout();
        using (var cx = Banco.Abrir())
        {
            var lotes = ContasAbertas.Lotes(cx, naoFoiId);
            checar(lotes.Count == 2 && lotes[0].SubstituidoPor == lotes[1].Id && ContasAbertas.EstadoDoLote(cx, lotes[1].Id) == EstadoLote.NaFila,
                "CT-18 [Mandar de novo] na linha perdida cria o lote n+1 e marca o velho como substituido");
        }

        // CT-19 (revisao 14/10): "Levar para a N" soma na linha que a conta ja tem e leva o cupom do carrinho
        var chaveAgua = (string)typeof(Pdv.Telas.Venda).GetMethod("NormalizarNome", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { agua.Nome })!;
        string autoId; long numAuto;
        using (var cx = Banco.Abrir())
        {
            var auto = ContasAbertas.Listar(cx, false).First(c => c.Nome is null && c.Itens.Count == 1 && c.Itens[0].ProdutoId == Agua);
            autoId = auto.Id; numAuto = auto.Numero;
        }
        if (Conta() is not null) Invocar(tela, "GuardarConta");
        Invocar(tela, "Adicionar", agua);
        Definir(tela, "_cortesiaCodigo", "AD-LEVA01");
        Campo<Dictionary<string, int>>(tela, "_cortesiaCobertura")[chaveAgua] = 1;
        host.UpdateLayout();
        Roteiro(host,
            d => Clicar(Botao(d, $"Comanda {numAuto}")),
            d => Clicar(Botao(d, ContasAbertas.Textos.LevarPara(numAuto))));
        Invocar(tela, "AbrirDialogoComandas");
        host.UpdateLayout();
        var linhasAgua = comanda.Where(i => i.Produto.Id == Agua).ToList();
        checar(Conta()?.Id == autoId && linhasAgua.Count == 1 && linhasAgua[0].Qtd.Milesimos == 2000,
            $"CT-19 'Levar para a N' soma a agua na linha que a conta ja tinha ({linhasAgua.Count} linha(s), {linhasAgua.Sum(i => i.Qtd.Milesimos)})");
        using (var cx = Banco.Abrir())
            checar(Campo<string>(tela, "_cortesiaCodigo") == "AD-LEVA01" && ContasAbertas.Ler(cx, autoId)!.CortesiaCodigo == "AD-LEVA01",
                "CT-19 o cupom do carrinho vai junto para a comanda (tela e disco)");
        checar(linhasAgua.Count == 1 && (int)Invocar(tela, "CoberturaDe", linhasAgua[0])! == 1, "CT-19 o cupom de 1 cobre 1 das 2 aguas");
        var extra = new Pdv.Telas.ItemComanda { Produto = agua, Qtd = new Quantidade(1000) };
        comanda.Insert(0, extra);
        var somaCobertura = comanda.Where(i => i.Produto.Id == Agua).Sum(i => (int)Invocar(tela, "CoberturaDe", i)!);
        comanda.Remove(extra);
        checar(somaCobertura == 1, $"CT-19 duas linhas do mesmo nome com cupom de 1: a cobertura total e 1, nao 2 ({somaCobertura})");

        // CT-20 (revisao 14/10): comanda INTEIRA em cortesia, de ponta a ponta com o servidor de mentira
        using var fakeCupom = new FakePostgrest(4747);
        Definir(tela, "_cortesias", new Cortesias(fakeCupom.Url));
        string idCortesia;
        using (var cx = Banco.Abrir())
        {
            var c = ContasAbertas.Abrir(cx, sessao, op, "Mesa 6", itens: new List<ItemRascunho> { Item(Agua, "AGUA 500ML", 500, 1000) });
            ContasAbertas.Soltar(cx, c.Id);
            idCortesia = c.Id;
        }
        Invocar(tela, "GuardarConta");
        Invocar(tela, "CarregarConta", idCortesia);
        Definir(tela, "_cortesiaCodigo", "AD-TODA01");
        Campo<Dictionary<string, int>>(tela, "_cortesiaCobertura")[chaveAgua] = 1;
        Invocar(tela, "PintarComanda");
        host.UpdateLayout();
        var avisoFinal = "";
        var travou = false;
        Task? resgate = null;
        Roteiro(host, d => { avisoFinal = string.Join(" ", Descendentes<TextBlock>(d).Select(t => t.Text)); Clicar(Botao(d, "Entendi")); });
        var quadro = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke((Action)(() =>
        {
            resgate = (Task)Invocar(tela, "ResgatarEConcluirBrindeAsync")!;
            travou = !tela.IsEnabled;
            resgate.ContinueWith(_ => quadro.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
        }));
        var limite = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        limite.Tick += (_, _) => { limite.Stop(); quadro.Continue = false; };
        limite.Start();
        Dispatcher.PushFrame(quadro);
        limite.Stop();
        host.UpdateLayout();
        using (var cx = Banco.Abrir())
        {
            var c = ContasAbertas.Ler(cx, idCortesia)!;
            var lotesCortesia = ContasAbertas.Lotes(cx, idCortesia);
            checar(resgate?.IsCompleted == true && travou && tela.IsEnabled, $"CT-20 a tela fica travada durante o resgate e volta no fim (travou {travou})");
            checar(c.Status == "fechada" && c.VendaId is null && Conta() is null && comanda.Count == 0,
                $"CT-20 a comanda toda em cortesia fecha sem venda e a tela volta ao balcao ({c.Status})");
            checar(lotesCortesia.Count == 1 && cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox WHERE tipo=@t AND ref_id=@r",
                       new { t = PedidoCozinha.TipoNaFila, r = lotesCortesia[0].Id }) == 1,
                "CT-20 a agua que ainda nao tinha ido foi para a cozinha (lote na fila)");
            checar(fakeCupom.Resgatadas.ContainsKey("AD-TODA01"), "CT-20 o cupom foi queimado no servidor");
            checar(avisoFinal.Contains(ContasAbertas.Textos.CortesiaFoiParaCozinha, StringComparison.Ordinal),
                $"CT-20 o aviso final diz que o que faltava foi para a cozinha ('{avisoFinal}')");
        }

        // CT-21 (revisao 14/10): lote que nao chegou: a linha diz "1 não chegou"; Mandar de novo da comanda
        // que esta na tela repinta o botao e a linha
        string idPerdida; long numPerdida;
        using (var cx = Banco.Abrir())
        {
            var c = ContasAbertas.Abrir(cx, sessao, op, "Mesa 7", itens: new List<ItemRascunho> { Item(Homer, "DONUT HOMER", 1200, 1000) });
            var l = ContasAbertas.PedirCozinha(cx, c.Id, null, op, false)!;
            cx.Execute("UPDATE outbox SET desistido_em=@e WHERE tipo=@t AND ref_id=@r", new { e = DateTime.Now.ToString("o"), t = PedidoCozinha.TipoNaFila, r = l.Id });
            ContasAbertas.Soltar(cx, c.Id);
            idPerdida = c.Id; numPerdida = c.Numero;
        }
        if (Conta() is not null) Invocar(tela, "GuardarConta");
        Invocar(tela, "CarregarConta", idPerdida);
        host.UpdateLayout();
        var nomeHomer = comanda[0].Produto.Nome;
        var rotuloPerdido = Campo<TextBlock>(tela, "TxtCozinhaRotulo").Text;
        var subPerdida = SubLinha(tela, nomeHomer) ?? "";
        checar(rotuloPerdido == $"⚠ {ContasAbertas.Textos.CozinhaCurta(1)}" && subPerdida.Contains(ContasAbertas.Textos.NaoChegou(1), StringComparison.Ordinal)
               && !subPerdida.Contains(ContasAbertas.Textos.NaCozinha(1), StringComparison.Ordinal),
            $"CT-21 lote que nao chegou: botao '⚠ Coz. 1' e a linha diz '1 não chegou' ({rotuloPerdido} / {subPerdida})");
        Roteiro(host, d => { Clicar(Botao(d, $"{ContasAbertas.Textos.MandarDeNovo} {numPerdida}")); Clicar(Botao(d, "Voltar")); });
        Invocar(tela, "AbrirDialogoComandas");
        host.UpdateLayout();
        var rotuloReenviado = Campo<TextBlock>(tela, "TxtCozinhaRotulo").Text;
        var subReenviada = SubLinha(tela, nomeHomer) ?? "";
        checar(rotuloReenviado == $"⚠ {ContasAbertas.Textos.CozinhaCurta(0)}" && subReenviada.Contains(ContasAbertas.Textos.NaCozinha(1), StringComparison.Ordinal)
               && !subReenviada.Contains(ContasAbertas.Textos.NaoChegou(1), StringComparison.Ordinal),
            $"CT-21 depois do Mandar de novo a tela repinta: '⚠ Coz. 0' e '1 na cozinha' ({rotuloReenviado} / {subReenviada})");

        using (var cx = Banco.Abrir())
            foreach (var c in ContasAbertas.Listar(cx, false)) ContasAbertas.Cancelar(cx, c.Id, op, "fim do teste", c.Itens.Count > 0 ? Operadores.PorId(cx, "ger-ui") : null);

        host.Content = null;
        host.Close();
    }

    // ── ajudantes ───────────────────────────────────────────────────────────
    private static ItemRascunho Item(string id, string nome, long preco, long qtd)
        => new(id, null, nome, "Cat", preco, qtd, "UN", "19053100", null, "102", 0, null, null);

    private static void SemearCatalogo(SqliteConnection cx)
    {
        var agora = DateTime.Now.ToString("o");
        foreach (var (id, plu, nome, cat, preco) in new[]
        {
            (Ninho, "4", "DONUT NINHO", "Donuts", 1200L), (Homer, "1", "DONUT HOMER", "Donuts", 1200L), (Agua, "56", "AGUA 500ML", "Bebidas", 500L),
        })
            cx.Execute("""
                INSERT INTO produto (id, plu, nome, categoria, preco_cent, unidade, ativo, atualizado, csosn, ncm)
                VALUES (@id, @plu, @nome, @cat, @preco, 'UN', 1, @a, '102', '19053100')
                """, new { id, plu, nome, cat, preco, a = agora });
    }

    private static Button Botao(DependencyObject raiz, string nome)
        => Descendentes<Button>(raiz).First(b => AutomationProperties.GetName(b) == nome || b.Content as string == nome);

    private static Button BotaoDaComanda(Pdv.Telas.Venda tela, string nome) => Botao(Campo<ItemsControl>(tela, "ListaComanda"), nome);

    /// <summary>O texto da sub-linha do unitario da primeira linha daquele produto ("3 × R$ 12,00 · 2 na cozinha").</summary>
    private static string? SubLinha(Pdv.Telas.Venda tela, string nomeProduto)
        => Descendentes<TextBlock>(Campo<ItemsControl>(tela, "ListaComanda")).FirstOrDefault(t => AutomationProperties.GetName(t) == "Unitario " + nomeProduto)?.Text;

    private static void Senha(DependencyObject d, string pin)
    {
        var caixa = Descendentes<PasswordBox>(d).First();
        caixa.Password = pin;
    }

    private static T Campo<T>(object alvo, string nome) => (T)alvo.GetType().GetField(nome, P)!.GetValue(alvo)!;

    private static void Definir(object alvo, string nome, object valor) => alvo.GetType().GetField(nome, P)!.SetValue(alvo, valor);

    private static object? Invocar(object alvo, string metodo, params object?[] args)
        => alvo.GetType().GetMethods(P).First(m => m.Name == metodo && m.GetParameters().Length == args.Length).Invoke(alvo, args);

    private static void Clicar(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    /// <summary>
    /// Os dialogos modais abrem em sequencia (lista, motivo, PIN, aviso): cada passo age no PROXIMO
    /// dialogo novo que aparecer sobre o host (o timer dispara dentro do laco do ShowDialog).
    /// </summary>
    private static void Roteiro(Window host, params Action<Window>[] passos)
    {
        var vistos = new HashSet<Window>();
        var i = 0;
        var tentativas = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            if (i >= passos.Length || ++tentativas > 80) { timer.Stop(); return; }
            var d = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w != host && w.IsVisible && !vistos.Contains(w) && Dono(w) == host);
            if (d is null) return;
            vistos.Add(d);
            var passo = passos[i++];
            tentativas = 0;
            passo(d);
        };
        timer.Start();
    }

    /// <summary>O host, subindo pelos donos (o aviso do PIN e dono da janela do PIN, que e dona do host).</summary>
    private static Window? Dono(Window w)
    {
        var atual = w;
        for (var k = 0; k < 6 && atual?.Owner is not null; k++) atual = atual.Owner;
        return atual == w ? null : atual;
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

    private static string Trecho(string todo, string de, string ate)
    {
        var i = todo.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var f = todo.IndexOf(ate, i + de.Length, StringComparison.Ordinal);
        return f < 0 ? "" : todo[i..f];
    }

    private static string Metodo(string fonte, string cabecalho) => Trecho(fonte, cabecalho, "\n    }\n");

    private static bool Ordem(string corpo, string primeiro, string depois)
    {
        var a = corpo.IndexOf(primeiro, StringComparison.Ordinal);
        var b = corpo.IndexOf(depois, StringComparison.Ordinal);
        return a >= 0 && b > a;
    }

    private static string Fonte(params string[] partes)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj")))
            {
                var c = Path.Combine(new[] { dir.FullName }.Concat(partes).ToArray());
                // CRLF do checkout (core.autocrlf) ou LF da arvore de trabalho: os trechos procuram "\n"
                return File.Exists(c) ? File.ReadAllText(c).Replace("\r\n", "\n") : "";
            }
        return "";
    }

    private static ProcessoVivo OutroProcessoVivo() => new();

    private sealed class ProcessoVivo : IDisposable
    {
        private readonly System.Diagnostics.Process _p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ping", "127.0.0.1 -n 120")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        public string Dono => $"{_p.Id}:{_p.StartTime.Ticks}";
        public void Dispose() { try { _p.Kill(); } catch { } _p.Dispose(); }
    }
}
