using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// PROMOÇÃO COM CÓDIGO NA CATEGORIA PROMOÇÃO (13/09/2026, Savassi).
///
/// O dono, com o print da categoria PROMOÇÃO mostrando só "Donuts do Dia": "PROMOÇÃO
/// FUNCIONÁRIO ATIVA E NÃO APARECE NO PDV". Ela existia, mas só como o botão ao lado do
/// total, e só depois do primeiro item. A categoria PROMOÇÃO foi feita para nunca mostrar
/// promoção com código (e alvo "todos" nem tem produto para listar).
///
/// O que esta suíte prova:
///  1. o núcleo lista a DESCONTO FUNCIONARIO real (payload copiado da produção) para a
///     categoria, e não lista a que está fora da vigência, do dia ou da janela, nem a livre;
///  2. o toque no card decide sem abrir janela quando não há o que liberar (comanda vazia,
///     já aplicada, outra promoção maior, item fora dela), com aviso de uma linha;
///  3. com item, o toque pede o código pelo MESMO portão (PortaoPromocao.ResolverAsync),
///     contra o FakeTotp: código certo aplica, cancelar e errar não aplicam;
///  4. na tela de venda de verdade, 1024x768: o card aparece, o toque com a comanda vazia não
///     pede código, com item pede e só aplica com o código (R$ 9,00 vira R$ 6,30), a recusa
///     não aplica, a auditoria é a mesma do botão, a promoção sem código continua como antes
///     e o nome no botão ao lado do total não passa mais da borda.
/// </summary>
public static class TestesPromoComSenhaNaVitrine
{
    private const BindingFlags P = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string IdFunc = "3037fa4b-0518-4879-817e-eb42fdf8331b";
    private const string TerminalUuid = "9a1c0c2e-0000-4000-8000-terminal0913";

    // copiado de producao em 13/09/2026 (to_jsonb(pdv_promocoes) - 'criada_em')
    private const string PayloadReal = """
        {"id": "3037fa4b-0518-4879-817e-eb42fdf8331b", "fim": null, "alvo": "todos", "leve": null, "nome": "DESCONTO FUNCIONARIO", "tipo": "percentual", "ativa": true, "combo": null, "lojas": ["American Day Savassi"], "pague": null, "store": "American Day Savassi", "config": {"autorizacao": "gerente"}, "inicio": "2026-09-08", "hora_fim": null, "categorias": null, "percentual": 30, "dias_semana": null, "hora_inicio": null, "produto_ids": null, "regras_semana": null, "valor_desconto_cent": null}
        """;

    private const string Duo = """
        {"id":"duo","nome":"promocao duo gourmet","tipo":"leve_x_pague_y","alvo":"produtos","leve":2,"pague":1,
         "produto_ids":["d-ninho"],"config":{},"inicio":"2026-08-20"}
        """;

    private sealed class TelaFalsa : ITelaAutorizacao
    {
        public Func<string?, string?>? AoPedirCodigo;
        public int VezesPediuCodigo;
        public readonly List<string> Niveis = new();
        private sealed class Nada : IDisposable { public void Dispose() { } }
        public IDisposable Aguardando(string mensagem) => new Nada();
        public Task<string?> PedirCodigoAsync(string? aviso, string nivel)
        {
            VezesPediuCodigo++; Niveis.Add(nivel);
            return Task.FromResult(AoPedirCodigo?.Invoke(aviso));
        }
    }

    private static Promocoes.Promo Pr(string json) => Promocoes.Parsear(json)
        ?? throw new InvalidOperationException("payload de teste nao parseou: " + json);

    private static bool UmaLinhaCurta(string s, int max = 45)
        => s.Length > 0 && s.Length <= max && !s.Contains('\n') && !s.Contains('—') && !s.Contains('–');

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        var domingo = new DateTime(2026, 9, 13, 18, 0, 0);   // o dia e a hora do print
        var func = Pr(PayloadReal);

        // ── 1. O QUE A CATEGORIA PROMOÇÃO LISTA ─────────────────────────────
        {
            var lista = Promocoes.PromocoesComSenhaNaVitrine(new[] { func }, domingo);
            checar(lista.Count == 1 && lista[0].PromoId == IdFunc && lista[0].Nome == "DESCONTO FUNCIONARIO"
                   && lista[0].Regra == "30% de desconto" && lista[0].Nivel == Promocoes.NivelGerente,
                $"CS-1 payload real: a DESCONTO FUNCIONARIO entra na categoria PROMOÇÃO ({lista.Count}: {string.Join(", ", lista)})");
            checar(Promocoes.ProdutosEmPromocao(new[] { func }, domingo).Count == 0,
                "CS-2 a lista de PRODUTOS continua sem ela (alvo todos não enumera o cardápio)");
            checar(Promocoes.PromocoesComSenhaNaVitrine(new[] { func }, new DateTime(2026, 9, 7, 18, 0, 0)).Count == 0,
                "CS-3 antes do início (07/09) não aparece");
            var fimOntem = Pr(PayloadReal.Replace("\"fim\": null", "\"fim\": \"2026-09-12\""));
            checar(Promocoes.PromocoesComSenhaNaVitrine(new[] { fimOntem }, domingo).Count == 0,
                "CS-4 depois do fim não aparece");
            var soSegunda = Pr(PayloadReal.Replace("\"dias_semana\": null", "\"dias_semana\": [1]"));
            checar(Promocoes.PromocoesComSenhaNaVitrine(new[] { soSegunda }, domingo).Count == 0
                   && Promocoes.PromocoesComSenhaNaVitrine(new[] { soSegunda }, domingo.AddDays(1)).Count == 1,
                "CS-5 dias_semana só segunda: some no domingo e aparece na segunda");
            var regraQuarta = Pr(PayloadReal.Replace("\"regras_semana\": null", "\"regras_semana\": [{\"dias\": [3], \"percentual\": 30}]"));
            checar(Promocoes.PromocoesComSenhaNaVitrine(new[] { regraQuarta }, domingo).Count == 0
                   && Promocoes.PromocoesComSenhaNaVitrine(new[] { regraQuarta }, domingo.AddDays(3)).Count == 1,
                "CS-6 regra da semana só na quarta: some no domingo e aparece na quarta");
            var manha = Pr(PayloadReal.Replace("\"hora_inicio\": null", "\"hora_inicio\": \"08:00\"").Replace("\"hora_fim\": null", "\"hora_fim\": \"12:00\""));
            checar(Promocoes.PromocoesComSenhaNaVitrine(new[] { manha }, domingo).Count == 0
                   && Promocoes.PromocoesComSenhaNaVitrine(new[] { manha }, domingo.Date.AddHours(9)).Count == 1,
                "CS-7 janela das 8h às 12h: some às 18h e aparece às 9h");
            var livre = Pr(PayloadReal.Replace("{\"autorizacao\": \"gerente\"}", "{}"));
            checar(!livre.ExigeAutorizacao && Promocoes.PromocoesComSenhaNaVitrine(new[] { livre, Pr(Duo) }, domingo).Count == 0,
                "CS-8 promoção sem código nunca vira card com chave");
            var dono = Pr(PayloadReal.Replace("\"gerente\"", "\"dono\"").Replace("DESCONTO FUNCIONARIO", "ABATIMENTO DO DONO").Replace(IdFunc, "dono-1"));
            var duas = Promocoes.PromocoesComSenhaNaVitrine(new[] { func, dono }, domingo);
            checar(duas.Select(d => d.Nome).SequenceEqual(new[] { "ABATIMENTO DO DONO", "DESCONTO FUNCIONARIO" })
                   && duas[0].Nivel == Promocoes.NivelDono,
                "CS-9 duas com código: uma linha cada, em ordem de nome, cada uma com o seu nível");

            var linhaG = PortaoPromocao.LinhaDoCard(lista[0].Regra, lista[0].Nivel);
            var linhaD = PortaoPromocao.LinhaDoCard(duas[0].Regra, duas[0].Nivel);
            checar(linhaG == "30% de desconto · código do gerente" && linhaD == "30% de desconto · código do dono",
                $"CS-10 a linha do card diz a regra e de quem é o código ({linhaG} / {linhaD})");
            checar(UmaLinhaCurta(linhaG) && UmaLinhaCurta(linhaD), "CS-11 a linha do card é curta, uma linha e sem travessão");
        }

        // ── 2. O TOQUE NO CARD (núcleo) ─────────────────────────────────────
        var um = new[] { new Promocoes.ItemCarrinho("d-ninho", "Donuts", 900, 1000) };
        {
            var ctx = new Promocoes.ContextoAutorizacao();
            var promos = new List<Promocoes.Promo> { func };
            var t = PortaoPromocao.Tocar(IdFunc, promos, Array.Empty<Promocoes.ItemCarrinho>(), domingo, ctx);
            checar(t.Acao == PortaoPromocao.Toque.ComandaVazia && t.Pendente is null
                   && ctx.Pendente(IdFunc) && ctx.Autorizadas.Count == 0 && ctx.Excluidas.Count == 0,
                "TQ-1 comanda vazia: não pergunta e não mexe no que a venda decidiu");
            t = PortaoPromocao.Tocar(IdFunc, promos, um, domingo, ctx);
            checar(t.Acao == PortaoPromocao.Toque.Perguntar && t.Pendente is { DescontoCent: 270, Nivel: "gerente" }
                   && t.Nome == "DESCONTO FUNCIONARIO",
                $"TQ-2 com 1 donut de R$ 9,00: pergunta o código do gerente para R$ 2,70 ({t.Acao} {t.Pendente})");
            var av = Promocoes.AvaliarCarrinho(promos, um, domingo, ctx);
            checar(av.TotalCent == 0 && !ctx.Autorizada(IdFunc), "TQ-3 tocar não aplica nada: sem código, preço de tabela");

            var duo = Pr(Duo);
            var dois = new[] { new Promocoes.ItemCarrinho("d-ninho", "Donuts", 900, 2000) };
            t = PortaoPromocao.Tocar(IdFunc, new List<Promocoes.Promo> { func, duo }, dois, domingo, ctx);
            checar(t.Acao == PortaoPromocao.Toque.OutraValeMais && t.Pendente is null,
                "TQ-4 duo gourmet dá R$ 9,00 contra R$ 5,40: 'outra vale mais', sem pedir código");
            var soAgua = Pr(PayloadReal.Replace("\"alvo\": \"todos\"", "\"alvo\": \"produtos\"").Replace("\"produto_ids\": null", "\"produto_ids\": [\"agua\"]"));
            t = PortaoPromocao.Tocar(IdFunc, new List<Promocoes.Promo> { soAgua }, um, domingo, ctx);
            checar(t.Acao == PortaoPromocao.Toque.NaoAlcanca, "TQ-5 promoção que não pega o item da comanda: avisa, não pergunta");
            checar(PortaoPromocao.Tocar("nao-existe", promos, um, domingo, ctx).Acao == PortaoPromocao.Toque.Indisponivel
                   && PortaoPromocao.Tocar(IdFunc, promos, um, new DateTime(2026, 9, 7, 18, 0, 0), ctx).Acao == PortaoPromocao.Toque.Indisponivel,
                "TQ-6 promoção que sumiu do caixa ou saiu da vigência: indisponível");
            var livre = Pr(PayloadReal.Replace("{\"autorizacao\": \"gerente\"}", "{}"));
            checar(PortaoPromocao.Tocar(IdFunc, new List<Promocoes.Promo> { livre }, um, domingo, ctx).Acao == PortaoPromocao.Toque.Indisponivel,
                "TQ-7 o toque só serve para promoção com código (a livre não passa por aqui)");

            ctx.Autorizar(IdFunc, "log-1", "Marcos");
            t = PortaoPromocao.Tocar(IdFunc, promos, um, domingo, ctx);
            checar(t.Acao == PortaoPromocao.Toque.JaAplicada && ctx.Autorizada(IdFunc),
                "TQ-8 já liberada nesta venda: não pede de novo");

            ctx.Excluir(IdFunc);
            t = PortaoPromocao.Tocar(IdFunc, promos, Array.Empty<Promocoes.ItemCarrinho>(), domingo, ctx);
            checar(t.Acao == PortaoPromocao.Toque.ComandaVazia && ctx.Excluida(IdFunc),
                "TQ-9 recusada e comanda vazia: continua recusada (só reabre quando vai perguntar)");
            t = PortaoPromocao.Tocar(IdFunc, promos, um, domingo, ctx);
            checar(t.Acao == PortaoPromocao.Toque.Perguntar && ctx.Pendente(IdFunc) && !ctx.Autorizada(IdFunc),
                "TQ-10 recusada antes (sem internet, 3 erros) e tocada de novo: volta a PENDENTE, nunca a autorizada");
            checar(Promocoes.AvaliarCarrinho(promos, um, domingo, ctx).TotalCent == 0,
                "TQ-11 reaberta continua sem desconto até o código");

            var copia = ctx.Copia();
            copia.Excluir(IdFunc);
            checar(ctx.Pendente(IdFunc) && copia.Excluida(IdFunc), "TQ-12 a simulação usa cópia: o contexto da venda não muda");

            var avisos = Enum.GetValues<PortaoPromocao.Toque>().Where(x => x != PortaoPromocao.Toque.Perguntar)
                .Select(PortaoPromocao.AvisoDoToque).ToList();
            checar(avisos.All(a => UmaLinhaCurta(a)) && avisos.Distinct().Count() == avisos.Count,
                $"TQ-13 cada aviso do toque é uma linha curta, sem travessão, e diferente dos outros ({string.Join(" | ", avisos)})");
            checar(PortaoPromocao.AvisoDoToque(PortaoPromocao.Toque.ComandaVazia) == "Adicione um item antes.",
                "TQ-14 comanda vazia: 'Adicione um item antes.'");

            // revisao 13/09: TQ-12 so prova que Copia() e independente; aqui prova que o Tocar
            // simula NA COPIA. Recusada que so leva aviso (outra vale mais, nao alcanca) continua recusada.
            ctx.Excluir(IdFunc);
            var t15a = PortaoPromocao.Tocar(IdFunc, new List<Promocoes.Promo> { func, duo }, dois, domingo, ctx);
            var t15b = PortaoPromocao.Tocar(IdFunc, new List<Promocoes.Promo> { soAgua }, um, domingo, ctx);
            checar(t15a.Acao == PortaoPromocao.Toque.OutraValeMais && t15b.Acao == PortaoPromocao.Toque.NaoAlcanca
                   && ctx.Excluida(IdFunc) && !ctx.Autorizada(IdFunc),
                $"TQ-15 recusada e tocada quando só cabe aviso ({t15a.Acao}, {t15b.Acao}): continua recusada, o toque não reabre");
        }

        // ── 3. O PORTÃO DE SEMPRE, CONTRA O FakeTotp ─────────────────────────
        using (var fake = new FakeTotp())
        {
            var relogio = DateTimeOffset.FromUnixTimeSeconds(1234567890);
            fake.Relogio = () => relogio;
            var cli = new ClienteAutorizacao(_ => Task.FromResult<string?>(fake.Token), fake.Url, fake.AnonKey,
                TimeSpan.FromSeconds(5), () => TerminalUuid);
            var comanda = new PortaoPromocao.Comanda("c-func", "Caixa Savassi", "American Day Savassi", "Maria");
            var promos = new List<Promocoes.Promo> { func };
            var errado = CodigoErrado(fake);

            // cancelou: não aplica, segue oferecida, nem vai à nuvem
            {
                var ctx = new Promocoes.ContextoAutorizacao();
                var t = PortaoPromocao.Tocar(IdFunc, promos, um, domingo, ctx);
                var chamadas = fake.Chamadas.Count;
                var tela = new TelaFalsa { AoPedirCodigo = _ => null };
                var r = await PortaoPromocao.ResolverAsync(new[] { t.Pendente! }, ctx, comanda, cli, tela);
                checar(r.Count == 1 && !r[0].Autorizada && ctx.Pendente(IdFunc) && fake.Chamadas.Count == chamadas
                       && Promocoes.AvaliarCarrinho(promos, um, domingo, ctx).TotalCent == 0,
                    "PG-1 cancelou o código: não aplica, segue oferecida e a nuvem nem é chamada");
            }
            // código errado 3 vezes: recusa e não aplica; tocar de novo com o certo libera
            {
                fake.ZerarBaldes();
                var ctx = new Promocoes.ContextoAutorizacao();
                var t = PortaoPromocao.Tocar(IdFunc, promos, um, domingo, ctx);
                var tela = new TelaFalsa { AoPedirCodigo = _ => errado };
                var antes = fake.Log.Count;
                var r = await PortaoPromocao.ResolverAsync(new[] { t.Pendente! }, ctx, comanda, cli, tela);
                var falhas = fake.Log.Skip(antes).ToList();
                checar(!r[0].Autorizada && tela.VezesPediuCodigo == 3 && ctx.Excluida(IdFunc)
                       && falhas.Count == 3 && falhas.All(f => f is { Ok: false, Tipo: "promocao", Nivel: "gerente" })
                       && Promocoes.AvaliarCarrinho(promos, um, domingo, ctx).TotalCent == 0,
                    "PG-2 código errado 3 vezes: recusada, 3 falhas no log da nuvem (promocao, gerente) e nada aplicado");

                t = PortaoPromocao.Tocar(IdFunc, promos, um, domingo, ctx);
                var telaOk = new TelaFalsa { AoPedirCodigo = _ => fake.CodigoAgoraGerente() };
                r = await PortaoPromocao.ResolverAsync(new[] { t.Pendente! }, ctx, comanda, cli, telaOk);
                var ultimo = fake.Log.LastOrDefault();
                var av = Promocoes.AvaliarCarrinho(promos, um, domingo, ctx);
                checar(t.Acao == PortaoPromocao.Toque.Perguntar && r[0].Autorizada && ctx.Autorizadas[IdFunc].Autorizador == "Marcos"
                       && av.PromoId == IdFunc && av.TotalCent == 270,
                    $"PG-3 tocou de novo com o código do gerente: liberada e aplicada, R$ 2,70 (total={av.TotalCent})");
                checar(ultimo is { Ok: true, Tipo: "promocao", Nivel: "gerente", Autorizador: "Marcos" }
                       && (ultimo.Referencia ?? "").StartsWith("promocao:cfunc:", StringComparison.Ordinal)
                       && (ultimo.Referencia ?? "").EndsWith(IdFunc.Replace("-", ""), StringComparison.Ordinal)
                       && telaOk.Niveis.SequenceEqual(new[] { "gerente" }),
                    "PG-4 o registro na nuvem é o de sempre: tipo promocao, nível gerente, referência comanda+promoção");
            }
        }

        // ── 4. A TELA DE VENDA DE VERDADE, 1024x768 ──────────────────────────
        var arquivo = Path.Combine(Path.GetTempPath(), $"pdv-promo-card-senha-{Guid.NewGuid():N}.db");
        var anterior = Banco.CaminhoForcado;
        Banco.CaminhoForcado = arquivo;
        Exception? erro = null;
        try
        {
            Banco.Migrar(arquivo);
            using (var cx = Banco.Abrir(arquivo)) Semear(cx);
            using var fake = new FakeTotp();
            try { HostWpf.Executar(() => NaTela(checar, fake)); }
            catch (Exception ex) { erro = ex; }
        }
        finally
        {
            Banco.CaminhoForcado = anterior;
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }
        checar(erro is null, "TL-0 tela: os passos rodaram (" + (erro?.ToString() ?? "ok") + ")");

        // ── 5. A PORTA DE TESTE NAO VAZA PARA O CAIXA (revisao 13/09) ────────
        // _portaoDeTeste troca a nuvem e a tela do codigo. So a suite pode preencher (por
        // reflexao). Na fonte do caixa: declarado nulo, lido num lugar so, e mais nada.
        var raiz = Raiz();
        var fonteVenda = raiz is null ? "" : File.ReadAllText(Path.Combine(raiz, "Telas", "Venda.xaml.cs"));
        var foraDaSuite = raiz is null ? new List<string>() : Directory.EnumerateFiles(raiz, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "Pdv.Testes" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && File.ReadAllText(f).Contains("_portaoDeTeste", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(raiz, f)).ToList();
        checar(fonteVenda.Length > 0
               && Regex.Matches(fonteVenda, @"\b_portaoDeTeste\b").Count == 2
               && fonteVenda.Contains("? _portaoDeTeste = null;", StringComparison.Ordinal)
               && fonteVenda.Contains("if (_portaoDeTeste is { } deTeste)", StringComparison.Ordinal)
               && foraDaSuite.SequenceEqual(new[] { Path.Combine("Telas", "Venda.xaml.cs") }),
            $"PT-1 a porta de teste só existe declarada nula e lida uma vez em Venda.xaml.cs ({string.Join(", ", foraDaSuite)})");
    }

    private static string? Raiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj"))) return dir.FullName;
        return null;
    }

    private static string CodigoErrado(FakeTotp fake)
    {
        var validos = new[] { -1, 0, 1 }.SelectMany(d => new[] { fake.CodigoAgora(d), fake.CodigoAgoraGerente(d) }).ToHashSet();
        return new[] { "123456", "654321", "111111", "222222" }.First(c => !validos.Contains(c));
    }

    private static void NaTela(Action<bool, string> checar, FakeTotp fake)
    {
        var relogio = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        fake.Relogio = () => relogio;
        var cli = new ClienteAutorizacao(_ => Task.FromResult<string?>(fake.Token), fake.Url, fake.AnonKey,
            TimeSpan.FromSeconds(5), () => TerminalUuid);
        var tela = new TelaFalsa();

        var host = new Window
        {
            Width = 1024, Height = 768, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = -20000, Opacity = 0,
        };
        host.Show();
        // Rede de seguranca: janela de verdade (PedirCodigo) nao pode abrir aqui. Se abrir,
        // fecha e conta, em vez de pendurar a suite dentro de um ShowDialog.
        var janelasAbertas = 0;
        var fecha = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        fecha.Tick += (_, _) =>
        {
            foreach (var d in Application.Current.Windows.OfType<Window>()
                         .Where(w => w != host && w.Owner == host && w.IsVisible).ToList())
            { janelasAbertas++; d.Close(); }
        };
        fecha.Start();

        var op = new Operador("op-real", "Maria", "operador");
        var turno = new Sessao("sessao-card", Caixa.DiaOperacional(), op.Id, op.Nome, DateTime.Now, Dinheiro.DeReais(300m));
        var venda = new Pdv.Telas.Venda(op, turno);
        typeof(Pdv.Telas.Venda).GetField("_portaoDeTeste", P)!.SetValue(venda,
            new Func<Window, (IAutorizacaoRemota?, ITelaAutorizacao)>(_ => (cli, tela)));
        host.Content = venda;
        host.UpdateLayout();

        var catPromo = (string)typeof(Pdv.Telas.Venda).GetField("CategoriaPromo", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        var contagem = Campo<Dictionary<string, int>>(venda, "_quantosPorCategoria");
        checar(contagem.GetValueOrDefault(catPromo) == 3,
            $"TL-1 a categoria PROMOÇÃO existe e conta o card junto dos produtos (2 produtos + 1 card, viu {contagem.GetValueOrDefault(catPromo)})");

        Button? Card()
        {
            typeof(Pdv.Telas.Venda).GetField("_categoriaAtual", P)!.SetValue(venda, catPromo);
            Invocar(venda, "PintarProdutos");
            host.UpdateLayout();
            return Campo<ItemsControl>(venda, "ListaProdutos").Items.OfType<Button>().FirstOrDefault(b => b.Tag as string == IdFunc);
        }
        void Tocar(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        bool Perguntando() => Campo<bool>(venda, "_perguntandoPromo");
        string Total() => Campo<TextBlock>(venda, "TxtTotal").Text.Replace((char)0xA0, ' ');
        string Toast() => Campo<TextBlock>(venda, "TxtToastAviso").Text;
        var ctx = Campo<Promocoes.ContextoAutorizacao>(venda, "_autorizacao");
        long Auditoria(string evento)
        {
            using var cx = Banco.Abrir();
            return cx.ExecuteScalar<long>("SELECT COUNT(*) FROM auditoria WHERE evento = @e", new { e = evento });
        }

        // ── o card ─────────────────────────────────────────────────────────
        var card = Card();
        var lista = Campo<ItemsControl>(venda, "ListaProdutos");
        var textos = lista.Items.OfType<DependencyObject>().SelectMany(Textos).ToList();
        Console.WriteLine($"      [medido] categoria PROMOCAO: {string.Join(" / ", textos)}");
        checar(card is not null && lista.Items.IndexOf(card) == 0,
            "TL-2 a categoria PROMOÇÃO mostra o card da Desconto Funcionario, antes das seções de produto");
        if (card is null) { Fechar(host, fecha, venda); return; }
        var textosCard = Textos(card).ToList();
        checar(textosCard.Contains("Desconto Funcionario") && textosCard.Contains("30% de desconto · código do gerente"),
            $"TL-3 o card diz o nome e '30% de desconto · código do gerente' ({string.Join(" | ", textosCard)})");
        var pos = card.TranslatePoint(new Point(0, 0), host);
        checar(card.ActualHeight > 0 && pos.X >= 0 && pos.Y >= 0 && pos.X + card.ActualWidth <= 1024 && pos.Y + card.ActualHeight <= 768,
            $"TL-4 o card cabe na tela de 1024x768 (x={pos.X:0} y={pos.Y:0} {card.ActualWidth:0}x{card.ActualHeight:0})");
        checar(textos.Any(t => t.EndsWith("Donuts do Dia", StringComparison.Ordinal)) && textos.Contains(Promocoes.SemProdutoHoje)
               && textos.Any(t => t.EndsWith("Promocao Duo Gourmet", StringComparison.Ordinal)) &&Campo<TextBlock>(venda, "TxtContagem").Text == "2 itens",
            $"TL-5 promoção sem código continua como antes: seção do donut com a linha curta e a duo com o produto ({Campo<TextBlock>(venda, "TxtContagem").Text})");

        // ── toque com a comanda vazia ──────────────────────────────────────
        Tocar(card);
        Bombear(150);
        checar(tela.VezesPediuCodigo == 0 && !Perguntando() && Campo<System.Collections.IList>(venda, "_comanda").Count == 0,
            "TL-6 comanda vazia: tocar no card NÃO pede código");
        checar(Campo<Border>(venda, "ToastAviso").Visibility == Visibility.Visible && Toast() == "Adicione um item antes.",
            $"TL-7 …e avisa numa linha: '{Toast()}'");
        checar(ctx.Pendente(IdFunc) && ctx.Autorizadas.Count == 0 && ctx.Excluidas.Count == 0,
            "TL-8 …e não decide nada sobre a promoção");

        // ── com item: cancelar não aplica ──────────────────────────────────
        Invocar(venda, "Adicionar", Produto("d-ninho"));
        host.UpdateLayout();
        Bombear(300);
        checar(tela.VezesPediuCodigo == 0 && Total() == "R$ 9,00", $"TL-9 adicionar o donut não pede código e cobra R$ 9,00 ({Total()})");
        // revisao 13/09: dois toques. Com a janela do codigo aberta, tocar de novo no card
        // e no botao ao lado do total nao pode abrir outra pergunta (trava _perguntandoPromo).
        var retoques = 0;
        tela.AoPedirCodigo = _ =>
        {
            if (retoques++ == 0)
            {
                Tocar(Card()!);
                Campo<Button>(venda, "BtnPromoComSenha").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            return null;
        };
        Tocar(Card()!);
        EsperarAte(() => !Perguntando(), 8000);
        checar(tela.VezesPediuCodigo == 1 && tela.Niveis.LastOrDefault() == "gerente",
            $"TL-10 com item, tocar no card pede o código do GERENTE uma vez só, mesmo tocando de novo no card e no botão com a janela aberta ({tela.VezesPediuCodigo})");
        checar(!ctx.Autorizada(IdFunc) && ctx.Pendente(IdFunc) && Total() == "R$ 9,00",
            $"TL-11 cancelou: nada aplicado, total R$ 9,00, e a promoção segue oferecida ({Total()})");

        // ── código errado 3 vezes: recusa não aplica ───────────────────────
        fake.ZerarBaldes();
        var errado = CodigoErrado(fake);
        tela.AoPedirCodigo = _ => errado;
        var logAntes = fake.Log.Count;
        Tocar(Card()!);
        EsperarAte(() => !Perguntando(), 8000);
        var falhas = fake.Log.Skip(logAntes).ToList();
        checar(tela.VezesPediuCodigo == 4 && ctx.Excluida(IdFunc) && Total() == "R$ 9,00"
               && falhas.Count == 3 && falhas.All(f => f is { Ok: false, Tipo: "promocao", Nivel: "gerente" }),
            $"TL-12 código errado 3 vezes: recusada, sem desconto ({Total()}), 3 falhas no log da nuvem");
        checar(Toast() == "Promoção Desconto Funcionario não aplicada" && Auditoria("promo_nao_autorizada") == 2,
            $"TL-13 …avisa numa linha e audita como o botão ('{Toast()}', {Auditoria("promo_nao_autorizada")} registros)");
        checar(Campo<Button>(venda, "BtnPromoComSenha").Visibility != Visibility.Visible,
            "TL-14 recusada, o botão ao lado do total some (como antes)");

        // ── tocou de novo, código certo: aplica ────────────────────────────
        tela.AoPedirCodigo = _ => fake.CodigoAgoraGerente();
        Tocar(Card()!);
        EsperarAte(() => !Perguntando(), 8000);
        var ultimo = fake.Log.LastOrDefault();
        checar(tela.VezesPediuCodigo == 5 && ctx.Autorizada(IdFunc) && ctx.Autorizadas[IdFunc].Autorizador == "Marcos",
            "TL-15 recusada antes, tocar no card pergunta de novo e o código do gerente libera");
        checar(Total() == "R$ 6,30" && Campo<TextBlock>(venda, "TxtPromo").Text.Replace((char)0xA0, ' ') == "Desconto Funcionario: -R$ 2,70",
            $"TL-16 os 30% entram: R$ 9,00 vira R$ 6,30 ({Total()} / {Campo<TextBlock>(venda, "TxtPromo").Text})");
        using (var cx = Banco.Abrir())
        {
            var det = cx.ExecuteScalar<string>("SELECT detalhe FROM auditoria WHERE evento = 'promo_autorizada' ORDER BY id DESC LIMIT 1") ?? "";
            checar(det.Contains("promo=" + IdFunc, StringComparison.Ordinal) && det.Contains("nivel=gerente", StringComparison.Ordinal)
                   && det.Contains("Marcos", StringComparison.Ordinal) && ultimo is { Ok: true, Tipo: "promocao", Nivel: "gerente" },
                "TL-17 a auditoria local e o registro na nuvem são os do botão (promo, nível, quem liberou)");
        }

        // ── já aplicada ────────────────────────────────────────────────────
        Tocar(Card()!);
        Bombear(150);
        checar(tela.VezesPediuCodigo == 5 && Toast() == "Promoção já aplicada nesta venda." && Total() == "R$ 6,30",
            $"TL-18 tocar de novo depois de aplicada não pede código ('{Toast()}')");

        // ── promoção sem código continua como antes; e 'outra vale mais' ───
        Invocar(venda, "EsvaziarComanda");
        Invocar(venda, "PintarComanda");
        Invocar(venda, "Adicionar", Produto("agua"));
        Invocar(venda, "Adicionar", Produto("agua"));
        host.UpdateLayout();
        Bombear(200);
        var av = Campo<Promocoes.Avaliacao>(venda, "_avaliacao");
        checar(av.PromoId == "duo" && Total() == "R$ 5,00" && tela.VezesPediuCodigo == 5,
            $"TL-19 duas águas: a duo gourmet (sem código) aplica sozinha, como sempre ({Total()})");
        Tocar(Card()!);
        Bombear(150);
        checar(tela.VezesPediuCodigo == 5 && Toast() == "Outra promoção vale mais nesta venda." && Total() == "R$ 5,00",
            $"TL-20 tocar na Desconto Funcionario com a duo valendo mais: avisa e não pede código ('{Toast()}')");

        // ── o botão ao lado do total: mesmo portão, e o nome cabe ──────────
        Invocar(venda, "EsvaziarComanda");
        Invocar(venda, "PintarComanda");
        Invocar(venda, "Adicionar", Produto("d-ninho"));
        host.UpdateLayout();
        Bombear(300);
        host.UpdateLayout();
        var botao = Campo<Button>(venda, "BtnPromoComSenha");
        var rotulo = Campo<TextBlock>(venda, "TxtPromoComSenha");
        var fimTexto = rotulo.TranslatePoint(new Point(rotulo.ActualWidth, 0), botao);
        Console.WriteLine($"      [medido] botao {botao.ActualWidth:0}px, texto termina em {fimTexto.X:0.0}px");
        checar(botao.Visibility == Visibility.Visible && rotulo.Text == "Aplicar Desconto Funcionario"
               && fimTexto.X <= botao.ActualWidth - botao.BorderThickness.Right,
            $"TL-21 o nome no botão ao lado do total termina dentro do botão (reticências, não corte seco): {fimTexto.X:0.0} de {botao.ActualWidth:0}");
        relogio = relogio.AddSeconds(30);
        tela.AoPedirCodigo = _ => fake.CodigoAgoraGerente();
        botao.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        EsperarAte(() => !Perguntando(), 8000);
        checar(tela.VezesPediuCodigo == 6 && ctx.Autorizada(IdFunc) && Total() == "R$ 6,30" && Auditoria("promo_autorizada") == 2,
            $"TL-22 o botão continua funcionando pelo mesmo portão ({Total()})");
        checar(janelasAbertas == 0, "TL-23 nenhuma janela de verdade abriu durante a suíte");

        Fechar(host, fecha, venda);
    }

    private static void Fechar(Window host, System.Windows.Threading.DispatcherTimer fecha, object venda)
    {
        fecha.Stop();
        host.Content = null;
        host.Close();
    }

    private static IEnumerable<string> Textos(DependencyObject o)
    {
        if (o is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text)) yield return tb.Text;
        foreach (var c in LogicalTreeHelper.GetChildren(o).OfType<DependencyObject>())
            foreach (var t in Textos(c)) yield return t;
    }

    private static void Bombear(int ms)
    {
        var fim = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < fim)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(20);
        }
    }

    private static bool EsperarAte(Func<bool> cond, int ms)
    {
        var fim = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < fim)
        {
            Bombear(40);
            if (cond()) return true;
        }
        return cond();
    }

    private static Pdv.Telas.Produto Produto(string id)
    {
        using var cx = Banco.Abrir();
        var p = cx.QueryFirst("SELECT id, plu, nome, categoria, preco_cent, unidade, csosn FROM produto WHERE id=@id", new { id });
        return new Pdv.Telas.Produto((string)p.id, (string?)p.plu, (string)p.nome, (string)p.categoria,
            new Dinheiro((long)p.preco_cent), (string)p.unidade, null, null, (string?)p.csosn, 0, null);
    }

    private static void Semear(SqliteConnection cx)
    {
        var agora = DateTime.Now.ToString("o");
        cx.Execute("""
            INSERT INTO terminal (id, terminal_uuid, loja_id, loja_nome, cnpj, serie_nfce, ambiente, api_base, criado_em)
            VALUES (1, 'term-card-senha', 'loja-1', 'American Day Savassi', '00000000000000', 1, 2, 'http://127.0.0.1:9', @a)
            """, new { a = agora });
        // Sem isto o teste pergunta a versao a PRODUCAO com o uuid de teste. Ver Isolamento.cs.
        Isolamento.SemNuvem(cx);
        var (h, s) = Operadores.GerarHash("4321");
        cx.Execute("INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,ativo,atualizado) VALUES ('op-real','Maria',@H,@S,'operador',1,@a)",
            new { H = h, S = s, a = agora });
        cx.Execute("""
            INSERT INTO produto (id, plu, nome, categoria, preco_cent, unidade, ativo, atualizado, csosn) VALUES
              ('d-ninho','4','DONUT NINHO','Donuts',900,'UN',1,@a,'102'),
              ('agua','7','AGUA MINERAL 500ML','Bebidas',500,'UN',1,@a,'102')
            """, new { a = agora });
        cx.Execute("INSERT INTO promo (id, payload, atualizado_em) VALUES (@Id, @P, @a)",
            new { Id = IdFunc, P = PayloadReal.Trim(), a = agora });
        // "donuts do dia" como no print: produto por id, regra num dia que NAO e hoje
        var outroDia = Promocoes.DiaIso(DateTime.Now) % 7 + 1;
        cx.Execute("INSERT INTO promo (id, payload, atualizado_em) VALUES ('donuts-dia', @P, @a)", new
        {
            a = agora,
            P = "{\"id\":\"donuts-dia\",\"nome\":\"donuts do dia\",\"tipo\":\"percentual\",\"alvo\":\"produtos\",\"percentual\":20,"
              + "\"produto_ids\":[\"d-ninho\"],\"inicio\":\"2026-08-06\",\"regras_semana\":[{\"dias\":[" + outroDia + "],\"produto_ids\":[\"d-ninho\"],\"percentual\":20}]}",
        });
        // a duo gourmet de verdade: leve 2 pague 1 na agua, sem codigo
        cx.Execute("INSERT INTO promo (id, payload, atualizado_em) VALUES ('duo', @P, @a)", new
        {
            a = agora,
            P = "{\"id\":\"duo\",\"nome\":\"promocao duo gourmet\",\"tipo\":\"leve_x_pague_y\",\"alvo\":\"produtos\",\"leve\":2,\"pague\":1,"
              + "\"produto_ids\":[\"agua\"],\"config\":{},\"inicio\":\"2026-08-20\"}",
        });
        Vendas.GravarConfig(cx, "modo_fiscal", "recibo");
    }

    private static T Campo<T>(object alvo, string nome)
        => (T)alvo.GetType().GetField(nome, P)!.GetValue(alvo)!;

    private static void Invocar(object alvo, string metodo, params object?[] args)
        => alvo.GetType().GetMethods(P).First(m => m.Name == metodo && m.GetParameters().Length == args.Length)
            .Invoke(alvo, args);
}
