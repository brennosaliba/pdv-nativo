using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O BOTÃO DO MENU DO TEF, MEDIDO NA TELA (07/09/2026).
///
/// A suíte do menu do TEF já prova muita coisa, mas a garantia de que ele NÃO aparece
/// no caixa da loja é feita de `Contains` no fonte de Venda.xaml.cs: que o XAML nasce
/// com Visibility="Collapsed", que existe uma atribuição só e que ela vem de
/// MenuTef.Aparece(_homologacao). Isso prova que as linhas estão escritas. Não prova
/// que o botão fica escondido.
///
/// A diferença não é acadêmica. `_homologacao` é um campo: quem mudar de onde ele vem
/// (uma segunda razão para o menu aparecer, uma chave nova, um `||` a mais) deixa TODAS
/// aquelas linhas idênticas e acende o botão no caixa da loja. Foi medido: alargar
/// `_homologacao` no construtor da venda mantém as checagens de fonte do menu do TEF
/// verdes, uma por uma, com o botão visível na loja.
///
/// Aqui a tela de venda sobe de verdade, com a chave em 0, e o botão é MEDIDO. O caso
/// com a chave em 1 vem junto de propósito: sem ele, esconder o botão para sempre
/// também passaria, e a checagem não valeria nada.
///
/// É o mesmo formato que o botão do valor de teste já tinha — e é por isso que o valor
/// de teste acusou aquela mudança e o menu do TEF não.
/// </summary>
public static class TestesMenuTefNaTela
{
    private const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Rodar(Action<bool, string> checar)
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-menutef-tela-{Guid.NewGuid():N}.db");
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
        checar(erro is null, "a venda subiu e os passos rodaram (" + (erro?.ToString() ?? "ok") + ")");
    }

    private static void Passos(Action<bool, string> checar)
    {
        var host = new Window
        {
            Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0,
        };
        host.Show();
        try
        {
            var gente = new Operador("op-real", "Maria", "gerente");
            var turno = new Sessao("sessao-loja", Caixa.DiaOperacional(), gente.Id, gente.Nome,
                DateTime.Now, Dinheiro.DeReais(300m));

            // (a) A LOJA. É a checagem que faltava: no botão, não no fonte.
            Config("homologacao", "0");
            var loja = new Pdv.Telas.Venda(gente, turno);
            host.Content = loja;
            host.UpdateLayout();
            checar(Campo<Button>(loja, "BtnMenuTef").Visibility != Visibility.Visible,
                "loja: o botão do menu do TEF não aparece na barra da venda");
            host.Content = null;

            // (b) A HOMOLOGAÇÃO: o outro lado da mesma medida.
            Config("homologacao", "1");
            var teste = new Pdv.Telas.Venda(gente, turno);
            host.Content = teste;
            host.UpdateLayout();
            checar(Campo<Button>(teste, "BtnMenuTef").Visibility == Visibility.Visible,
                "homologação: aí sim o botão do menu do TEF aparece");
            host.Content = null;
        }
        finally { host.Close(); }
    }

    private static void Semear(SqliteConnection cx)
    {
        var agora = DateTime.Now.ToString("o");
        cx.Execute("""
            INSERT INTO terminal (id, terminal_uuid, loja_id, loja_nome, cnpj, serie_nfce, ambiente, api_base, criado_em)
            VALUES (1, 'term-menutef', 'loja-1', 'Loja Teste', '00000000000000', 1, 2, 'http://127.0.0.1:9', @a)
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

    private static T Campo<T>(object alvo, string nome)
        => (T)alvo.GetType().GetField(nome, P)!.GetValue(alvo)!;
}
