using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O CAIXA DE HOMOLOGAÇÃO SEM LOGIN E SEM CAIXA (07/09/2026, pedido do dono).
///
/// "Tire função fechamento de caixa pra não ficar mostrando toda hora que fecha e
/// abre já que será somente teste. Login operador de caixa também remover porque
/// vamos pra teste."
///
/// O roteiro do TEF são 58 passos e o dono repete a mesma venda mais de vinte vezes.
/// A cada volta o caixa pedia login, abertura de caixa com contagem da gaveta e
/// fechamento cego. Nenhum dos 58 passos fala de caixa ou de operador: essas telas
/// não provam nada no roteiro e só cobram tempo de quem grava a evidência.
///
/// ⚠️ O QUE ESTA SUÍTE EXISTE PARA IMPEDIR: que qualquer pedaço disso valha na LOJA.
/// Um caixa de loja sem login de operador e sem fechamento é auditoria impossível e
/// dinheiro sem dono. Por isso metade dos testes daqui roda com `homologacao` = 0 e
/// cobra que TUDO continue exigindo login, abertura e fechamento.
///
/// O que se prova:
///  · loja (homologacao = 0): a entrada direta não existe, nenhum operador de teste
///    nasce, nenhum turno abre sozinho e a sobra do teste não fica de pé;
///  · homologação: entra sozinho com o operador de teste, num turno de teste, sem
///    contagem e sem nada na fila da nuvem;
///  · o operador de teste não é um acesso: inativo, sem senha, fora do login, fora da
///    autorização de supervisor e fora do piso da sincronização;
///  · a venda de teste continua fora do apurado do turno e fora da nuvem;
///  · desligar o modo encerra o turno de teste sem deixar fundo esperado errado para o
///    caixa da loja no dia seguinte;
///  · turno de VERDADE aberto tira o modo de cena: dinheiro de gente manda;
///  · a faixa de aviso na janela e o sumiço do "Fechar / Sair" na tela de venda.
/// </summary>
public static class TestesCaixaDeHomologacao
{
    private const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Rodar(Action<bool, string> checar)
    {
        NoBanco(checar);
        Fonte(checar);
        Tela(checar);
    }

    // ── 1. as regras, num banco de verdade ─────────────────────────────────
    private static void NoBanco(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-homolog-caixa-{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        try
        {
            Banco.Migrar(arquivo);
            using (var cx = Banco.Abrir(arquivo)) Semear(cx);

            ALoja(checar, arquivo);
            OCaixaDeTeste(checar, arquivo);
            AVendaDeTeste(checar, arquivo);
            DeVoltaParaALoja(checar, arquivo);
            ViradaDoDia(checar, arquivo);
            DinheiroDeVerdadeManda(checar, arquivo);
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
    }

    // ── A LOJA: com a chave desligada, nada disso existe ───────────────────
    private static void ALoja(Action<bool, string> checar, string arquivo)
    {
        using var cx = Banco.Abrir(arquivo);
        Vendas.GravarConfig(cx, "homologacao", "0");

        checar(!ModoHomologacao.Ligado(cx), "loja: o modo de homologação está desligado");
        checar(ModoHomologacao.EntradaDireta(cx) is null,
            "loja: não existe entrada direta, então o caixa segue para o LOGIN como sempre");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM operador WHERE id = @Id",
                   new { Id = ModoHomologacao.IdOperador }) == 0,
            "loja: nem a linha do operador de teste chega a nascer");
        checar(Caixa.SessaoAberta(cx) is null,
            "loja: nenhum turno abre sozinho, a ABERTURA DE CAIXA continua sendo exigida");
        checar(ModoHomologacao.EncerrarSobras(cx) == 0, "loja: sem turno de teste, não há o que encerrar");
    }

    // ── O CAIXA DE TESTE ───────────────────────────────────────────────────
    private static void OCaixaDeTeste(Action<bool, string> checar, string arquivo)
    {
        using var cx = Banco.Abrir(arquivo);
        Vendas.GravarConfig(cx, "homologacao", "1");

        var entrada = ModoHomologacao.EntradaDireta(cx);
        checar(entrada is not null, "homologação: o caixa entra sozinho, sem passar pelo login");
        if (entrada is null) return;
        var (op, sessao) = entrada.Value;

        checar(op.Id == ModoHomologacao.IdOperador, "quem entra é o operador de teste (id fixo)");
        checar(op.Nome == "Teste de homologação",
            $"e o nome dele diz o que é, na tela e na auditoria (veio '{op.Nome}')");

        // ⚠️ o operador de teste NÃO é um acesso
        var linha = cx.QueryFirstOrDefault("SELECT ativo, pin_hash, pin_salt, perfil FROM operador WHERE id = @Id",
            new { Id = ModoHomologacao.IdOperador });
        checar(linha is not null && (long)linha.ativo == 0,
            "a linha do operador de teste nasce INATIVA: ninguém entra com ela no caixa da loja");
        checar(linha is not null && (string)linha.pin_hash == "" && (string)linha.pin_salt == "",
            "e sem senha nenhuma: não há PIN que a abra, e o piso da sincronização (que só reergue quem tem senha) nunca a escolhe");
        checar(linha is not null && (string)linha.perfil == "operador",
            "perfil de operador, não de gerente: caixa de teste não ganha poder de autorizar nada");
        checar(Operadores.PrimeiroAtivo(cx)?.Id != ModoHomologacao.IdOperador,
            "ele não aparece como operador ativo do caixa");
        var achouNoLogin = false;
        foreach (var pin in new[] { "0000", "1234", "9999", "123456", "" })
            achouNoLogin |= Operadores.Entrar(cx, pin)?.Id == ModoHomologacao.IdOperador
                         || Operadores.AutorizarSupervisor(cx, pin)?.Id == ModoHomologacao.IdOperador;
        checar(!achouNoLogin, "nenhum PIN entra como operador de teste nem autoriza no nome dele");

        // o TURNO
        checar(sessao.Teste, "a venda corre num TURNO DE TESTE, marcado como tal no banco");
        checar(sessao.FundoTroco.Centavos == 0, "turno de teste abre com fundo zero: não há gaveta para contar");
        checar(sessao.BusinessDate == Caixa.DiaOperacional(), "e no dia operacional de hoje");
        var doBanco = Caixa.SessaoAberta(cx);
        checar(doBanco is not null && doBanco.Id == sessao.Id && doBanco.Teste,
            "a marca de teste volta do banco: quem ler o turno depois sabe que ele foi teste");

        var denovo = ModoHomologacao.EntradaDireta(cx);
        checar(denovo is not null && denovo.Value.Sessao.Id == sessao.Id,
            "voltar para a tela não abre outro turno: o roteiro inteiro cabe num turno só");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM caixa_sessao") == 1,
            "e o banco tem UM turno, não um por venda");

        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox") == 0,
            "nada do turno de teste entra na fila da nuvem (o painel recusaria e viraria dead-letter)");
        var rastro = cx.ExecuteScalar<string?>(
            "SELECT detalhe FROM auditoria WHERE evento = 'caixa_teste_aberto' ORDER BY id DESC LIMIT 1");
        checar(rastro is not null && rastro.Contains("Teste de homologação"),
            "a auditoria guarda a abertura do turno de teste, com o nome que diz o que ele é");
    }

    // ── A VENDA DE TESTE NESSE TURNO ───────────────────────────────────────
    private static void AVendaDeTeste(Action<bool, string> checar, string arquivo)
    {
        using var cx = Banco.Abrir(arquivo);
        if (ModoHomologacao.EntradaDireta(cx) is not { } entrada)
        {
            checar(false, "venda de teste: sem entrada direta não há turno de teste onde vender");
            return;
        }
        var total = Dinheiro.DeReais(1002m);
        var venda = Vendas.Finalizar(cx, entrada.Sessao, entrada.Operador,
            new[] { new LinhaVenda(null, "TESTE", "Venda de teste", Quantidade.Um, total, total,
                                   "UN", "19053100", null, "102", null, 0) },
            new[] { new PagamentoVenda("credito", total, Dinheiro.Zero, Aut: "123456", Nsu: "000001") },
            null, "Loja Teste", null);

        checar(cx.ExecuteScalar<long>("SELECT homologacao FROM venda WHERE id = @Id", new { Id = venda.Id }) == 1,
            "a venda do roteiro nasce marcada como venda de teste");
        var apurado = Caixa.Apurado(cx, entrada.Sessao);
        checar(apurado.Values.Sum(v => v.Centavos) == 0,
            "ela fica FORA do apurado do turno: o roteiro não vira faturamento");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox") == 0,
            "e continua sem nada na fila da nuvem: não vira receita na DRE");
        checar(Caixa.FundoEsperado(cx) is null,
            "e não deixa fundo esperado para a abertura seguinte (nenhum fechamento foi gravado)");

        // ⚠️ A CHAVE DESLIGADA COM O PDV AINDA NO TURNO DE TESTE.
        //
        // É assim que o roteiro acaba: o dono desliga `homologacao` na mão, no SQLite, com
        // o caixa aberto na tela de venda. A tela leu a chave UMA vez, ao abrir, então ela
        // continua mostrando o caixa de teste (sem Fechar / Sair, com o valor livre) e o
        // operador dá mais uma passada. Se a marca da venda seguisse a config do momento,
        // essa venda nasceria de VERDADE: iria para a fila da nuvem assinada por um
        // operador que o painel não conhece e entraria no apurado de um turno sem gaveta.
        Vendas.GravarConfig(cx, "homologacao", "0");
        var depois = Vendas.Finalizar(cx, entrada.Sessao, entrada.Operador,
            new[] { new LinhaVenda(null, "TESTE", "Venda de teste", Quantidade.Um, total, total,
                                   "UN", "19053100", null, "102", null, 0) },
            new[] { new PagamentoVenda("credito", total, Dinheiro.Zero, Aut: "123456", Nsu: "000002") },
            null, "Loja Teste", null);
        checar(cx.ExecuteScalar<long>("SELECT homologacao FROM venda WHERE id = @Id", new { Id = depois.Id }) == 1,
            "chave desligada no meio do roteiro: a venda no turno de teste continua sendo de teste");
        checar(Caixa.Apurado(cx, entrada.Sessao).Values.Sum(v => v.Centavos) == 0,
            "e continua fora do apurado: turno de teste não vira faturamento por causa de uma chave");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox") == 0,
            "nem sobe para a nuvem assinada por quem não é gente");
        Vendas.GravarConfig(cx, "homologacao", "1");
    }

    // ── DE VOLTA PARA A LOJA ───────────────────────────────────────────────
    private static void DeVoltaParaALoja(Action<bool, string> checar, string arquivo)
    {
        using var cx = Banco.Abrir(arquivo);
        Vendas.GravarConfig(cx, "homologacao", "0");

        checar(ModoHomologacao.EncerrarSobras(cx) == 1,
            "desligar o modo encerra o turno de teste que ficou aberto");
        checar(Caixa.SessaoAberta(cx) is null,
            "sem turno aberto, o caixa da loja volta a EXIGIR abertura de caixa");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM caixa_fechamento") == 0,
            "encerrar o teste não grava contagem nenhuma");
        checar(Caixa.FundoEsperado(cx) is null,
            "e não deixa fundo esperado: a loja não abre amanhã acusando diferença de um teste");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox") == 0,
            "nem manda fechamento de mentira para a nuvem");
        var rastro = cx.ExecuteScalar<string?>(
            "SELECT detalhe FROM auditoria WHERE evento = 'caixa_teste_encerrado' ORDER BY id DESC LIMIT 1");
        checar(rastro is not null, "e o encerramento fica na auditoria");
        checar(ModoHomologacao.EntradaDireta(cx) is null && Caixa.SessaoAberta(cx) is null,
            "com o modo desligado nenhum turno volta a abrir sozinho");
        checar(ModoHomologacao.EncerrarSobras(cx) == 0, "encerrar duas vezes não faz nada");

        // ⚠️ O QUE FICOU NA MÃO DE QUEM ESTAVA OPERANDO.
        //
        // Desligar o modo com o PDV JÁ ABERTO no turno de teste faz exatamente o que
        // acabou de acontecer aqui: a sobra é encerrada e a entrada direta deixa de
        // valer. Só que o operador de TESTE continua sendo quem está logado, e o próximo
        // passo do caixa é a tela de ABERTURA DE CAIXA. Sem barreira, o dia inteiro da
        // loja nasce assinado por "Teste de homologação": turno na fila da nuvem com um
        // id que o painel não conhece (409 até virar dead-letter), vendas e fechamento
        // com dono que não é gente. É o vazamento que a chave inteira existe para evitar.
        checar(ModoHomologacao.EhOperadorDeTeste(ModoHomologacao.IdOperador)
               && !ModoHomologacao.EhOperadorDeTeste("op-real")
               && !ModoHomologacao.EhOperadorDeTeste(null),
            "o caixa reconhece o operador de teste pelo id, e só ele");

        var deTeste = ModoHomologacao.OperadorDeTeste(cx);
        var recusa = "";
        try { Caixa.Abrir(cx, deTeste, new Dinheiro(20_000)); }
        catch (InvalidOperationException ex) { recusa = ex.Message; }
        checar(recusa.Length > 0,
            "o operador de teste NÃO abre caixa da loja, nem chamado direto no Núcleo");
        checar(Caixa.SessaoAberta(cx) is null,
            "e a tentativa não deixou turno de verdade nenhum aberto");
        checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM outbox") == 0,
            "nem foi para a fila da nuvem um turno assinado por quem o painel não conhece");
        checar(recusa.Contains("login") && !recusa.Contains('—'),
            "e o aviso diz o que fazer, em uma frase: entrar com o próprio login");
    }

    // ── VIRADA DO DIA COM O TESTE ABERTO ───────────────────────────────────
    private static void ViradaDoDia(Action<bool, string> checar, string arquivo)
    {
        using var cx = Banco.Abrir(arquivo);
        Vendas.GravarConfig(cx, "homologacao", "1");
        if (ModoHomologacao.EntradaDireta(cx) is not { } primeira)
        {
            checar(false, "virada do dia: sem entrada direta não há turno de teste para envelhecer");
            return;
        }
        var ontem = primeira.Sessao;
        // envelhece o turno na marra: é o caixa que ficou ligado do dia anterior
        cx.Execute("UPDATE caixa_sessao SET business_date = @D WHERE id = @Id",
            new { D = DateTime.Parse(Caixa.DiaOperacional()).AddDays(-1).ToString("yyyy-MM-dd"), Id = ontem.Id });

        var hoje = ModoHomologacao.EntradaDireta(cx)!.Value.Sessao;
        checar(hoje.Id != ontem.Id && hoje.BusinessDate == Caixa.DiaOperacional(),
            "virou o dia: o teste abre o turno de hoje sem perguntar nada");
        checar(cx.ExecuteScalar<string?>("SELECT status FROM caixa_sessao WHERE id = @Id", new { Id = ontem.Id }) == "fechado",
            "e o turno de teste de ontem se encerra sozinho, sem contagem");
        checar(Caixa.FundoEsperado(cx) is null, "a virada do teste também não inventa fundo esperado");

        // limpa a mesa para o teste seguinte
        Vendas.GravarConfig(cx, "homologacao", "0");
        ModoHomologacao.EncerrarSobras(cx);
    }

    // ── DINHEIRO DE VERDADE MANDA ──────────────────────────────────────────
    private static void DinheiroDeVerdadeManda(Action<bool, string> checar, string arquivo)
    {
        using var cx = Banco.Abrir(arquivo);
        var gente = new Operador("op-real", "Maria", "gerente");
        var turnoReal = Caixa.Abrir(cx, gente, Dinheiro.DeReais(300m));
        Vendas.GravarConfig(cx, "homologacao", "1");

        checar(ModoHomologacao.EntradaDireta(cx) is null,
            "turno de VERDADE aberto: o modo sai de cena e o caixa volta ao caminho normal (com login e fechamento)");
        checar(Caixa.SessaoAberta(cx)?.Id == turnoReal.Id && !Caixa.SessaoAberta(cx)!.Teste,
            "o turno de gente continua aberto e intocado");
        checar(ModoHomologacao.BloqueadoPorTurnoDeVerdade(cx),
            "e a faixa da janela avisa por que o login continua aparecendo, em vez de deixar o dono achar que não funcionou");

        Vendas.GravarConfig(cx, "homologacao", "0");
        checar(ModoHomologacao.EncerrarSobras(cx) == 0 && Caixa.SessaoAberta(cx)?.Id == turnoReal.Id,
            "e nem desligando o modo o turno de gente é encerrado por baixo do pano");
        checar(!ModoHomologacao.BloqueadoPorTurnoDeVerdade(cx),
            "com o modo desligado não há aviso nenhum a dar: a loja não sabe que isso existe");
    }

    // ── 2. FONTE: a casca e a tela ─────────────────────────────────────────
    private static void Fonte(Action<bool, string> checar)
    {
        var casca = Arquivo("MainWindow.xaml.cs");
        var cascaXaml = Arquivo("MainWindow.xaml");
        var venda = Arquivo("Telas", "Venda.xaml.cs");
        if (casca is null || cascaXaml is null || venda is null)
        {
            checar(false, "achei MainWindow.xaml(.cs) e Telas/Venda.xaml.cs");
            return;
        }

        // (a) a entrada direta vem ANTES do login: é isso que tira o login do caminho
        var iEntrada = casca.IndexOf("ModoHomologacao.EntradaDireta", StringComparison.Ordinal);
        var iLogin = casca.IndexOf("MostrarLogin(cx)", StringComparison.Ordinal);
        checar(iEntrada > 0 && iLogin > iEntrada,
            "a casca resolve a entrada direta ANTES de mostrar o login");
        checar(casca.Contains("ModoHomologacao.EncerrarSobras", StringComparison.Ordinal),
            "e encerra a sobra do teste no boot, para o turno de teste não segurar a abertura da loja");

        // (a2) e, quando a entrada direta para de valer com o PDV aberto, o operador de
        // teste é solto ANTES do login. Senão a casca cai na tela de abertura de caixa
        // com ele no alto, e a loja abre o dia em nome de um operador que não é gente.
        var iSolta = casca.IndexOf("ModoHomologacao.EhOperadorDeTeste", StringComparison.Ordinal);
        checar(iSolta > iEntrada && iSolta < iLogin,
            "a casca solta o operador de teste antes de seguir para o login da loja");

        // (b) a faixa de aviso, fixa e nascida escondida
        checar(cascaXaml.Contains("x:Name=\"FaixaHomologacao\"", StringComparison.Ordinal)
               && cascaXaml.Contains("Visibility=\"Collapsed\"", StringComparison.Ordinal),
            "a janela tem a faixa do modo de homologação, escondida por padrão");
        checar(cascaXaml.Contains("<RowDefinition Height=\"Auto\"/>", StringComparison.Ordinal)
               && cascaXaml.Contains("Grid.Row=\"1\" x:Name=\"Conteudo\"", StringComparison.Ordinal),
            "a faixa EMPURRA o caixa para baixo: nenhuma tela ou diálogo passa por cima dela");
        checar(casca.Contains("ModoHomologacao.TituloFaixa", StringComparison.Ordinal)
               && casca.Contains("ModoHomologacao.DetalheFaixa", StringComparison.Ordinal),
            "o texto da faixa vem do Núcleo: uma frase só para a tela e para a suíte");
        checar(casca.Contains("ModoHomologacao.BloqueadoPorTurnoDeVerdade", StringComparison.Ordinal)
               && casca.Contains("ModoHomologacao.DetalheBloqueado", StringComparison.Ordinal),
            "e a faixa troca de frase quando um turno de gente segura o login no lugar");

        // (c) a tela de venda: o Fechar / Sair some, e o cinto está no handler
        checar(venda.Contains("_semFechamento = _homologacao && sessao.Teste", StringComparison.Ordinal),
            "o sumiço do fechamento exige as DUAS coisas: o modo ligado E o turno ser de teste");
        checar(venda.Contains("BtnFecharSair.Visibility = _semFechamento ? Visibility.Collapsed : Visibility.Visible",
                   StringComparison.Ordinal),
            "no turno de teste o botão Fechar / Sair não é desenhado");
        foreach (var (nome, trecho) in new[]
                 {
                     ("FecharCaixa", Trecho(venda, "private async void FecharCaixa", "_fechandoCaixa = true;")),
                     ("MenuFecharSair", Trecho(venda, "private async void MenuFecharSair", "AbrirMenu(BtnFecharSair")),
                     ("Sair", Trecho(venda, "private void Sair(object sender", "Deslogou?.Invoke();")),
                 })
            checar(trecho.Contains("if (_semFechamento) return;", StringComparison.Ordinal),
                $"e o {nome} recusa por dentro, não só escondendo o botão");

        // (d) texto de tela: o dono lê tudo e reprova travessão
        foreach (var t in new[] { ModoHomologacao.NomeOperador, ModoHomologacao.TituloFaixa,
                                  ModoHomologacao.DetalheFaixa, ModoHomologacao.LinhaDoTurno,
                                  ModoHomologacao.DetalheBloqueado })
        {
            checar(t.Length > 0 && !t.Contains('—') && !t.Contains('–'), $"sem travessão em: {t}");
            checar(!t.Contains("caixa_sessao") && !t.Contains("homologacao ="),
                $"sem nome de coluna nem código na frase: {t}");
        }
        checar(ModoHomologacao.DetalheFaixa.Contains("teste"),
            "a faixa diz, com todas as letras, que a venda é de teste");
    }

    // ── 3. A TELA DE VENDA, em STA ─────────────────────────────────────────
    private static void Tela(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-homolog-tela-{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        Exception? erro = null;
        try
        {
            Banco.Migrar(arquivo);
            using (var cx = Banco.Abrir(arquivo))
            {
                Semear(cx);
                Vendas.GravarConfig(cx, "modo_fiscal", "recibo");
            }
            try { HostWpf.Executar(() => Passos(checar)); }
            catch (Exception ex) { erro = ex; }
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
        checar(erro is null, "tela: a venda subiu e os passos rodaram (" + (erro?.ToString() ?? "ok") + ")");
    }

    private static void Passos(Action<bool, string> checar)
    {
        var host = new Window
        {
            Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0,
        };
        host.Show();

        // (a) A LOJA: turno de gente, com fechamento
        Config("homologacao", "0");
        var gente = new Operador("op-real", "Maria", "gerente");
        var turnoReal = new Sessao("sessao-real", Caixa.DiaOperacional(), gente.Id, gente.Nome,
            DateTime.Now, Dinheiro.DeReais(300m));
        var loja = new Pdv.Telas.Venda(gente, turnoReal);
        host.Content = loja;
        host.UpdateLayout();
        checar(Campo<Button>(loja, "BtnFecharSair").Visibility == Visibility.Visible,
            "loja: o botão Fechar / Sair continua na barra, com o fechamento de caixa dentro");
        checar(Campo<TextBlock>(loja, "TxtSessao").Text.StartsWith("Caixa aberto às"),
            "loja: o topo continua dizendo a que horas o caixa abriu");
        host.Content = null;

        // (b) O CAIXA DE TESTE
        Config("homologacao", "1");
        using (var cx = Banco.Abrir())
        {
            if (ModoHomologacao.EntradaDireta(cx) is not { } entrada)
            {
                checar(false, "tela: sem entrada direta o caixa de teste nem chega à venda");
                host.Close();
                return;
            }
            var tela = new Pdv.Telas.Venda(entrada.Operador, entrada.Sessao);
            host.Content = tela;
            host.UpdateLayout();

            checar(Campo<Button>(tela, "BtnFecharSair").Visibility != Visibility.Visible,
                "homologação: o Fechar / Sair some da barra, e com ele o fechamento de caixa");
            checar(Campo<TextBlock>(tela, "TxtOperador").Text == ModoHomologacao.NomeOperador,
                "o topo mostra quem está operando: o operador de teste, com esse nome");
            checar(Campo<TextBlock>(tela, "TxtSessao").Text == ModoHomologacao.LinhaDoTurno,
                "e, no lugar da hora da abertura, diz que o turno é de teste e não fecha");

            // O CINTO: chamar o fechamento na marra não pode fechar nada. A vigia fica
            // armada durante a chamada só para o teste não travar num ShowDialog se o
            // cinto sumir um dia.
            var vigia = FechaQualquerDialogo(host);
            Invocar(tela, "FecharCaixa", null, new RoutedEventArgs());
            vigia.Stop();
            checar(!Campo<bool>(tela, "_fechandoCaixa"),
                "e chamar o fechamento na marra nem começa: o handler recusa antes de tudo");
            checar(Caixa.SessaoAberta(cx)?.Teste == true,
                "o turno de teste continua aberto depois disso");
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM caixa_fechamento") == 0,
                "e nenhuma contagem foi gravada");

            // O MESMO PARA O SAIR, e chamado de verdade. Ele não leva a lugar nenhum
            // neste modo (o login não existe aqui) e jogaria fora a comanda do roteiro em
            // silêncio. Conferir por grep que a linha do cinto está escrita no arquivo não
            // prova que ela roda: só chamar prova.
            var deslogou = false;
            void Marca() => deslogou = true;
            tela.Deslogou += Marca;
            Invocar(tela, "Sair", null, new RoutedEventArgs());
            tela.Deslogou -= Marca;
            checar(!deslogou,
                "e o Sair, chamado na marra, não desloga ninguém no turno de teste");
        }

        // (c) MODO LIGADO, MAS COM TURNO DE GENTE ABERTO.
        //
        // É a situação DESTA máquina hoje: a chave já está em 1 e há um caixa de verdade
        // aberto. Aqui o fechamento NÃO some, porque ele é daquele turno, e turno de
        // gente nunca perde o fechamento dele.
        //
        // Este passo existe porque é o único que DISCORDA. Nos casos (a) e (b) "modo
        // ligado E turno de teste" e "modo ligado" dão a mesma resposta: trocar as duas
        // condições por só a config passaria pelos dois sem acusar nada, e a prova de que
        // são duas viraria uma linha de texto conferida a grep. Aqui as duas versões
        // divergem, e o botão do fechamento é quem responde.
        host.Content = null;
        var comGente = new Pdv.Telas.Venda(gente, turnoReal);
        host.Content = comGente;
        host.UpdateLayout();
        checar(Campo<Button>(comGente, "BtnFecharSair").Visibility == Visibility.Visible,
            "modo ligado com turno de gente aberto: o Fechar / Sair CONTINUA na barra");
        checar(Campo<TextBlock>(comGente, "TxtSessao").Text.StartsWith("Caixa aberto às"),
            "e o topo segue dizendo a hora da abertura, não a frase do turno de teste");
        checar(!Campo<bool>(comGente, "_semFechamento"),
            "dinheiro de gente manda: a config sozinha não tira o fechamento de um turno de verdade");

        host.Content = null;
        host.Close();
    }

    // ── ajudantes ──────────────────────────────────────────────────────────
    private static void Semear(SqliteConnection cx)
    {
        var agora = DateTime.Now.ToString("o");
        cx.Execute("""
            INSERT INTO terminal (id, terminal_uuid, loja_id, loja_nome, cnpj, serie_nfce, ambiente, api_base, criado_em)
            VALUES (1, 'term-homolog', 'loja-1', 'Loja Teste', '00000000000000', 1, 2, 'http://127.0.0.1:9', @a)
            """, new { a = agora });
        var (h, s) = Operadores.GerarHash("4321");
        cx.Execute("INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,ativo,atualizado) VALUES ('op-real','Maria',@H,@S,'gerente',1,@a)",
            new { H = h, S = s, a = agora });
        cx.Execute("""
            INSERT INTO produto (id, plu, nome, categoria, preco_cent, unidade, ativo, atualizado, csosn)
            VALUES ('d-ninho','4','DONUT NINHO','Donuts',900,'UN',1,@a,'102')
            """, new { a = agora });
    }

    private static void Config(string chave, string valor)
    {
        using var cx = Banco.Abrir();
        Vendas.GravarConfig(cx, chave, valor);
    }

    /// <summary>
    /// Fecha QUALQUER diálogo que abrir, quantos abrirem, até alguém parar o timer.
    /// Rede de segurança: sem ela, um cinto que sumisse faria a suíte travar dentro de
    /// um ShowDialog em vez de acusar a falha.
    /// </summary>
    private static System.Windows.Threading.DispatcherTimer FechaQualquerDialogo(Window host)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            foreach (var d in Application.Current.Windows.OfType<Window>()
                         .Where(w => w != host && w.Owner == host && w.IsVisible).ToList())
                d.Close();
        };
        timer.Start();
        return timer;
    }

    private static T Campo<T>(object alvo, string nome)
        => (T)alvo.GetType().GetField(nome, P)!.GetValue(alvo)!;

    private static void Invocar(object alvo, string metodo, params object?[] args)
        => alvo.GetType().GetMethods(P).First(m => m.Name == metodo && m.GetParameters().Length == args.Length)
            .Invoke(alvo, args);

    private static string? Arquivo(params string[] partes)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var alvo = Path.Combine(new[] { d.FullName }.Concat(partes).ToArray());
            if (File.Exists(alvo)) return File.ReadAllText(alvo);
            d = d.Parent;
        }
        return null;
    }

    private static string Trecho(string fonte, string de, string ate)
    {
        var i = fonte.IndexOf(de, StringComparison.Ordinal);
        if (i < 0) return "";
        var j = fonte.IndexOf(ate, i, StringComparison.Ordinal);
        return j < 0 ? fonte[i..] : fonte[i..(j + ate.Length)];
    }
}
