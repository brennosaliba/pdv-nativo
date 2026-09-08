using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// PROMOÇÃO COM SENHA É OFERECIDA, NUNCA IMPOSTA (08/09/2026, pedido do dono).
///
/// "pq esta mostrando promocao desconto funcionario nao aplicada? ela deveria pedir um
/// poup com token somente qdo eh selacionado e nao qdo entra na aba promocao"
///
/// O QUE ACONTECIA. A promoção DESCONTO FUNCIONARIO é 30% em `alvo: todos`, sem dia e
/// sem janela, e exige código do gerente. Ou seja: ela vence sempre, contra qualquer
/// comanda. E quem disparava a pergunta era a PINTURA DA COMANDA, que roda a cada item
/// bipado. Resultado no balcão: o primeiro produto da venda abria a janela do código do
/// gerente, em TODA venda. O operador fechava, e levava "Promoção Desconto funcionário
/// não aplicada". Na auditoria desta máquina, três vezes só no dia 08/09.
///
/// O defeito não é o texto: é a promoção se impor. Uma promoção que precisa de senha é
/// exceção (desconto de funcionário, desconto do dono), e exceção se PEDE.
///
/// O que esta suíte prova, com a tela de venda de verdade montada:
///  1. bipar um produto NÃO abre janela nenhuma e NÃO decide nada sobre a promoção;
///  2. o botão aparece, com o nome dela, quando ela valeria para esta comanda;
///  3. o botão some quando não há promoção com senha alcançando a comanda;
///  4. a promoção continua PENDENTE até alguém tocar no botão (nem aplicada, nem
///     recusada em silêncio).
/// </summary>
public static class TestesPromoOferecida
{
    private const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Rodar(Action<bool, string> checar)
    {
        // ── o rótulo do botão (puro) ────────────────────────────────────────
        checar(PortaoPromocao.RotuloDoBotao(new[] { "Desconto funcionário" }) == "Aplicar Desconto funcionário",
            "uma promoção: o botão diz o nome dela, que é o que o operador confere com o cliente");
        checar(PortaoPromocao.RotuloDoBotao(new[] { "Desconto funcionário", "Desconto do dono" }) == "Aplicar promoção (2)",
            "duas ou mais: só o número, senão a linha vira parede");
        checar(PortaoPromocao.RotuloDoBotao(Array.Empty<string>()) == "",
            "nenhuma: nada a dizer");

        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-promo-oferecida-{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        Exception? erro = null;
        try
        {
            Banco.Migrar(arquivo);
            using (var cx = Banco.Abrir(arquivo)) Semear(cx);
            try { HostWpf.Executar(() => NaTela(checar)); }
            catch (Exception ex) { erro = ex; }
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
        checar(erro is null, "tela: os passos rodaram (" + (erro?.ToString() ?? "ok") + ")");
    }

    private static void NaTela(Action<bool, string> checar)
    {
        var host = new Window
        {
            Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0,
        };
        host.Show();
        // Rede de seguranca: se alguma janela abrir (e e exatamente isso que este
        // arquivo existe para impedir), ela e fechada em vez de travar a suite dentro
        // de um ShowDialog. Quem acusa a falha e a asserção, nao um teste pendurado.
        var fecha = FechaQualquerDialogo(host);

        var op = new Operador("op-real", "Maria", "gerente");
        var turno = new Sessao("sessao-real", Caixa.DiaOperacional(), op.Id, op.Nome,
            DateTime.Now, Dinheiro.DeReais(300m));
        var venda = new Pdv.Telas.Venda(op, turno);
        host.Content = venda;
        host.UpdateLayout();

        var botao = Campo<Button>(venda, "BtnPromoComSenha");
        var rotulo = Campo<TextBlock>(venda, "TxtPromoComSenha");
        checar(botao.Visibility != Visibility.Visible,
            "comanda vazia: nenhum botão de promoção com senha");

        // ── BIPA UM PRODUTO ────────────────────────────────────────────────
        // É AQUI que a janela do código abria sozinha. Depois disto não pode haver
        // nenhuma janela aberta, e a promoção não pode ter sido decidida.
        Invocar(venda, "Adicionar", ProdutoDoBanco());
        host.UpdateLayout();
        // ⚠️ BOMBEAR O DISPATCHER. O disparo antigo era um Dispatcher.BeginInvoke, que
        // NAO roda com UpdateLayout: sem esta bomba o teste passava mesmo com o defeito
        // de volta (medido em 08/09, ao provar o vermelho). Aqui a fila anda de verdade.
        Bombear(600);

        var ctx = Campo<Promocoes.ContextoAutorizacao>(venda, "_autorizacao");
        checar(ctx.Pendente(IdPromo),
            "…e a promoção continua PENDENTE: não foi aplicada nem recusada em silêncio");
        checar(!ctx.Excluida(IdPromo),
            "…principalmente: não entrou em Excluídas, que era o que gerava o 'não aplicada'");

        // ── O CONVITE ──────────────────────────────────────────────────────
        checar(botao.Visibility == Visibility.Visible,
            "o botão aparece, porque a promoção valeria para esta comanda");
        checar(rotulo.Text == "Aplicar Desconto Funcionario",
            $"…e diz o nome dela (viu: {rotulo.Text})");

        // ── ESVAZIOU, SUMIU ────────────────────────────────────────────────
        Invocar(venda, "EsvaziarComanda");
        Invocar(venda, "PintarComanda");
        host.UpdateLayout();
        checar(botao.Visibility != Visibility.Visible,
            "comanda limpa: o botão some junto (a promoção não alcança nada)");

        fecha.Stop();
        host.Content = null;
        host.Close();
    }

    /// <summary>Deixa a fila do dispatcher andar por `ms`, para o que foi agendado rodar.</summary>
    private static void Bombear(int ms)
    {
        var fim = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < fim)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            Application.Current.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(20);
        }
    }

    private static System.Windows.Threading.DispatcherTimer FechaQualquerDialogo(Window host)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            foreach (var d in Application.Current.Windows.OfType<Window>()
                         .Where(w => w != host && w.Owner == host && w.IsVisible).ToList())
                d.Close();
        };
        timer.Start();
        return timer;
    }

    private const string IdPromo = "promo-func";

    private static Pdv.Telas.Produto ProdutoDoBanco()
    {
        using var cx = Banco.Abrir();
        var p = cx.QueryFirst("SELECT id, plu, nome, categoria, preco_cent, unidade, csosn FROM produto LIMIT 1");
        return new Pdv.Telas.Produto((string)p.id, (string?)p.plu, (string)p.nome, (string)p.categoria,
            new Dinheiro((long)p.preco_cent), (string)p.unidade, null, null, (string?)p.csosn, 0, null);
    }

    private static void Semear(SqliteConnection cx)
    {
        var agora = DateTime.Now.ToString("o");
        cx.Execute("""
            INSERT INTO terminal (id, terminal_uuid, loja_id, loja_nome, cnpj, serie_nfce, ambiente, api_base, criado_em)
            VALUES (1, 'term-promo', 'loja-1', 'Loja Teste', '00000000000000', 1, 2, 'http://127.0.0.1:9', @a)
            """, new { a = agora });
        var (h, s) = Operadores.GerarHash("4321");
        cx.Execute("INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,ativo,atualizado) VALUES ('op-real','Maria',@H,@S,'gerente',1,@a)",
            new { H = h, S = s, a = agora });
        cx.Execute("""
            INSERT INTO produto (id, plu, nome, categoria, preco_cent, unidade, ativo, atualizado, csosn)
            VALUES ('d-ninho','4','DONUT NINHO','Donuts',900,'UN',1,@a,'102')
            """, new { a = agora });
        // A promoção do incidente, copiada do banco desta máquina: 30% em TODOS os
        // produtos, sem dia e sem janela, com código do gerente.
        cx.Execute("INSERT INTO promo (id, payload, atualizado_em) VALUES (@Id, @P, @a)", new
        {
            Id = IdPromo, a = agora,
            P = """
                {"id":"promo-func","nome":"DESCONTO FUNCIONARIO","tipo":"percentual","alvo":"todos",
                 "ativa":true,"percentual":30,"config":{"autorizacao":"gerente"},
                 "produto_ids":null,"categorias":null,"dias_semana":null,"inicio":null,"fim":null,
                 "hora_inicio":null,"hora_fim":null,"valor_desconto_cent":null,"leve":null,"pague":null,
                 "combo":null,"regras_semana":null}
                """,
        });
        Vendas.GravarConfig(cx, "modo_fiscal", "recibo");
    }

    private static T Campo<T>(object alvo, string nome)
        => (T)alvo.GetType().GetField(nome, P)!.GetValue(alvo)!;

    private static void Invocar(object alvo, string metodo, params object?[] args)
        => alvo.GetType().GetMethods(P).First(m => m.Name == metodo && m.GetParameters().Length == args.Length)
            .Invoke(alvo, args);
}
