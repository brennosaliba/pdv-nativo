using System.Text.Json;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A DICA NA RECUSA DO CÓDIGO (15/09/2026, Castelo).
///
/// Em 15/09, às 13:55:41, 13:55:47 e 13:57:12, o caixa do Castelo levou três "código
/// inválido" seguidos num estorno e num cancelamento. O log não guarda o código, então
/// ninguém sabe se era um código repassado por mensagem e digitado depois de vencer, ou o
/// código da entrada do GERENTE no Authenticator (no estorno só vale o do dono). O operador
/// também não sabia, e tentou de novo do mesmo jeito.
///
/// O contrato novo (ERP e PDV em paralelo): na recusa "codigo invalido" a RPC pode mandar
/// <c>dica</c> = 'vencido' (bateu com o segredo certo, mas num passo que já passou) ou
/// 'outro_autenticador' (bateu com o segredo de quem não pode aprovar este nível). A dica
/// NUNCA aprova nada e conta no limite de tentativas igual. O caixa só troca a frase.
///
/// O que esta suíte protege:
///  · o cliente lê a dica e só aceita os dois valores do contrato;
///  · a frase certa para cada dica, curta e de uma linha;
///  · servidor sem dica (a RPC de hoje) e dica desconhecida continuam com a frase de hoje;
///  · dica fora do "codigo invalido" (rate limit) é ignorada;
///  · nada disso aprova um código, e o código continua fora do diagnóstico.
/// </summary>
public static class TestesTotpDica
{
    private sealed class TelaFalsa : ITelaAutorizacao
    {
        public Func<string?, string?>? AoPedirCodigo;
        public int VezesPediuCodigo;
        public readonly List<string?> Avisos = new();
        private sealed class Nada : IDisposable { public void Dispose() { } }
        public IDisposable Aguardando(string mensagem) => new Nada();
        public Task<string?> PedirCodigoAsync(string? aviso, string nivel)
        {
            VezesPediuCodigo++; Avisos.Add(aviso);
            return Task.FromResult(AoPedirCodigo?.Invoke(aviso));
        }
    }

    private const string TerminalUuid = "9a1c0c2e-0000-4000-8000-terminal0003";
    private const string TextoDeHoje = "Código inválido. Tente de novo.";
    private const string TextoVencido = "Esse código já venceu. Digite o que está na tela agora.";
    private const string TextoDoGerente = "Esse código é do gerente. Aqui vale o do dono.";

    public static async Task RodarAsync(Action<bool, string> checar)
    {
        using var fake = new FakeTotp();
        var relogio = DateTimeOffset.FromUnixTimeSeconds(1_757_000_010);   // meio de um passo de 30 s
        fake.Relogio = () => relogio;
        void ProximoPasso() => relogio = relogio.AddSeconds(30);

        var diagnostico = new List<string>();
        var cli = new ClienteAutorizacao(_ => Task.FromResult<string?>(fake.Token), fake.Url, fake.AnonKey,
            TimeSpan.FromSeconds(5), () => TerminalUuid);
        cli.Diagnostico = l => { lock (diagnostico) diagnostico.Add(l); };

        PedidoAutorizacao Estorno(long venda) => new("Caixa Castelo", Autorizacao.Referencia("tef-d", "000" + venda, 1500, venda), 1500,
            Loja: "American Day Castelo", Operador: "Lucas", Venda: venda.ToString(), Forma: "credito", Nsu: "000" + venda);

        // ── 1. O CLIENTE LÊ A DICA ─────────────────────────────────────────────
        fake.EmiteDica = true;
        {
            fake.ZerarBaldes();
            var v = await cli.ValidarTotpAsync(fake.CodigoAgora(-2), "estorno:d1", "estorno", null, "dono", CancellationToken.None);
            checar(!v.Ok && v.Definitiva && v.Motivo == "codigo invalido" && v.Dica == "vencido" && v.Id is null && v.Autorizador is null,
                $"DC-1 código do dono de 1 minuto atrás (T-2): recusa com dica 'vencido', sem id e sem nome (dica={v.Dica})");

            fake.ZerarBaldes();
            var v20 = await cli.ValidarTotpAsync(fake.CodigoAgora(-20), "estorno:d2", "estorno", null, "dono", CancellationToken.None);
            var v21 = await cli.ValidarTotpAsync(fake.CodigoAgora(-21), "estorno:d3", "estorno", null, "dono", CancellationToken.None);
            checar(!v20.Ok && v20.Dica == "vencido" && !v21.Ok && v21.Dica is null && v21.Motivo == "codigo invalido",
                $"DC-2 T-20 ainda é 'vencido'; T-21 já é código inválido sem dica (T-20={v20.Dica ?? "null"}, T-21={v21.Dica ?? "null"})");

            fake.ZerarBaldes();
            var vGer = await cli.ValidarTotpAsync(fake.CodigoAgoraGerente(), "estorno:d4", "estorno", null, "dono", CancellationToken.None);
            checar(!vGer.Ok && vGer.Motivo == "codigo invalido" && vGer.Dica == "outro_autenticador" && vGer.Autorizador is null,
                $"DC-3 código do GERENTE num estorno: recusa com dica 'outro_autenticador', sem aprovar (dica={vGer.Dica})");

            fake.ZerarBaldes();
            var vErrado = await cli.ValidarTotpAsync(CodigoQueNaoBate(fake), "estorno:d5", "estorno", null, "dono", CancellationToken.None);
            checar(!vErrado.Ok && vErrado.Dica is null && fake.Log.LastOrDefault() is { Ok: false, Dica: null },
                "DC-4 código que não bate com segredo nenhum: recusa sem dica");

            fake.ZerarBaldes(); ProximoPasso();
            var vOk = await cli.ValidarTotpAsync(fake.CodigoAgora(), "estorno:d6", "estorno", null, "dono", CancellationToken.None);
            var vReplay = await cli.ValidarTotpAsync(fake.CodigoAgora(), "estorno:d7", "estorno", null, "dono", CancellationToken.None);
            checar(vOk.Ok && vOk.Dica is null && !vReplay.Ok && vReplay.Motivo == "codigo invalido" && vReplay.Dica is null,
                "DC-5 aprovado não tem dica, e o replay (o mesmo código de novo) é inválido SEM dica");
        }

        // DC-6 a dica conta no limite de tentativas como qualquer código inválido
        {
            fake.ZerarBaldes();
            for (var i = 0; i < 5; i++)
                await cli.ValidarTotpAsync(fake.CodigoAgoraGerente(), $"estorno:rl{i}", "estorno", null, "dono", CancellationToken.None);
            var depois = await cli.ValidarTotpAsync(fake.CodigoAgora(), "estorno:rl-certo", "estorno", null, "dono", CancellationToken.None);
            checar(!depois.Ok && depois.Motivo == "muitas tentativas, aguarde" && depois.Dica is null,
                $"DC-6 cinco códigos do gerente num estorno gastam o limite: o sexto, mesmo certo, é recusado sem ser testado (motivo={depois.Motivo})");
            fake.ZerarBaldes();
        }

        // ── 2. A FRASE NA TELA ─────────────────────────────────────────────────
        // DC-7 vencido → frase do vencido, e o código de agora autoriza na segunda
        {
            fake.ZerarBaldes(); ProximoPasso();
            var vez = 0;
            var tela = new TelaFalsa { AoPedirCodigo = _ => ++vez == 1 ? fake.CodigoAgora(-3) : fake.CodigoAgora() };
            var d = await Autorizacao.ResolverAsync(cli, Estorno(401), tela);
            checar(d.Autorizado && d.Via == ViaAutorizacao.Totp && tela.VezesPediuCodigo == 2
                   && tela.Avisos.Count == 2 && tela.Avisos[0] is null && tela.Avisos[1] == TextoVencido,
                $"DC-7 código vencido: a tela diz '{TextoVencido}' e o código de agora autoriza (aviso={tela.Avisos.ElementAtOrDefault(1)})");
        }

        // DC-8 estorno com o código do gerente → a frase diz de quem é e qual vale
        {
            fake.ZerarBaldes(); ProximoPasso();
            var tela = new TelaFalsa { AoPedirCodigo = _ => fake.CodigoAgoraGerente() };
            var d = await Autorizacao.ResolverAsync(cli, Estorno(402), tela);
            checar(!d.Autorizado && tela.VezesPediuCodigo == 3 && tela.Avisos.Skip(1).All(a => a == TextoDoGerente)
                   && d.Motivo == "Código inválido 3 vezes. Estorno não autorizado.",
                $"DC-8 estorno com o código do gerente: '{TextoDoGerente}' a cada nova tentativa, e na terceira desiste (avisos={string.Join(" | ", tela.Avisos)})");
        }

        // DC-9 cancelamento com o código do gerente: mesma frase (o cancelamento também é do dono)
        {
            fake.ZerarBaldes(); ProximoPasso();
            var vez = 0;
            var pedido = new PedidoAutorizacao("Caixa Castelo", Autorizacao.ReferenciaCancelamento("v-9", 409, 900), 900) { Tipo = "cancelamento" };
            var tela = new TelaFalsa { AoPedirCodigo = _ => ++vez == 1 ? fake.CodigoAgoraGerente() : null };
            var d = await Autorizacao.ResolverAsync(cli, pedido, tela);
            checar(!d.Autorizado && d.Avisado && tela.Avisos.ElementAtOrDefault(1) == TextoDoGerente,
                "DC-9 cancelamento com o código do gerente: a mesma frase, e o operador pode desistir");
        }

        // DC-10 promoção de nível DONO com o código do gerente: mesma frase
        {
            fake.ZerarBaldes(); ProximoPasso();
            var vez = 0;
            var pedido = new PedidoAutorizacao("Caixa Castelo", Autorizacao.ReferenciaPromocao("cmd-1", "promo-dono"), 0)
            { Tipo = "promocao", Nivel = Autorizacao.NivelDono, PromocaoId = "promo-dono", PromocaoNome = "25% dono" };
            var tela = new TelaFalsa { AoPedirCodigo = _ => ++vez == 1 ? fake.CodigoAgoraGerente() : fake.CodigoAgora() };
            var d = await Autorizacao.ResolverAsync(cli, pedido, tela);
            checar(d.Autorizado && tela.Avisos.ElementAtOrDefault(1) == TextoDoGerente,
                $"DC-10 promoção do dono com o código do gerente: '{TextoDoGerente}', e o código do dono libera em seguida");
        }

        // DC-11 nível GERENTE com 'outro_autenticador' (não acontece no servidor: lá o dono é candidato) → frase de hoje
        {
            fake.ZerarBaldes(); ProximoPasso();
            fake.DicaForcada = "outro_autenticador";
            try
            {
                var vez = 0;
                var pedido = new PedidoAutorizacao("Caixa Castelo", Autorizacao.ReferenciaPromocao("cmd-2", "promo-ger"), 0)
                { Tipo = "promocao", Nivel = Autorizacao.NivelGerente };
                var tela = new TelaFalsa { AoPedirCodigo = _ => ++vez == 1 ? "000000" : null };
                await Autorizacao.ResolverAsync(cli, pedido, tela);
                checar(tela.Avisos.ElementAtOrDefault(1) == TextoDeHoje,
                    $"DC-11 no nível gerente, 'outro_autenticador' não inventa frase: fica '{TextoDeHoje}' (aviso={tela.Avisos.ElementAtOrDefault(1)})");
            }
            finally { fake.DicaForcada = null; }
        }

        // DC-12 dica que o caixa não conhece, e "dica": null explícito → frase de hoje
        {
            foreach (var (nome, forcada, nula) in new[] { ("desconhecida", (string?)"relogio_adiantado", false), ("null explícito", null, true) })
            {
                fake.ZerarBaldes(); ProximoPasso();
                fake.DicaForcada = forcada; fake.DicaNulaNoCorpo = nula;
                try
                {
                    var vez = 0;
                    var tela = new TelaFalsa { AoPedirCodigo = _ => ++vez == 1 ? "000000" : null };
                    await Autorizacao.ResolverAsync(cli, Estorno(412), tela);
                    var v = await cli.ValidarTotpAsync("000000", "estorno:dx", "estorno", null, "dono", CancellationToken.None);
                    checar(tela.Avisos.ElementAtOrDefault(1) == TextoDeHoje && v.Dica is null,
                        $"DC-12 dica {nome}: o cliente descarta e a tela fica com '{TextoDeHoje}' (dica lida={v.Dica ?? "null"})");
                }
                finally { fake.DicaForcada = null; fake.DicaNulaNoCorpo = false; }
            }
        }

        // DC-13 servidor sem dica (a RPC de hoje, caixas 1.0.9 e 1.0.10): tudo como sempre
        {
            fake.EmiteDica = false;
            fake.ZerarBaldes(); ProximoPasso();
            var vez = 0;
            var tela = new TelaFalsa { AoPedirCodigo = _ => ++vez == 1 ? fake.CodigoAgoraGerente() : null };
            await Autorizacao.ResolverAsync(cli, Estorno(413), tela);
            checar(tela.Avisos.ElementAtOrDefault(1) == TextoDeHoje,
                "DC-13 servidor sem dica: código do gerente num estorno continua 'Código inválido. Tente de novo.'");
            fake.EmiteDica = true;
        }

        // DC-14 dica junto de outra recusa (rate limit): ignorada, e a tela não insiste
        {
            fake.ZerarBaldes(); ProximoPasso();
            fake.DicaEmTodaRecusa = "vencido";
            try
            {
                for (var i = 0; i < 5; i++)
                    await cli.ValidarTotpAsync("000000", $"estorno:x{i}", "estorno", null, "dono", CancellationToken.None);
                var tela = new TelaFalsa { AoPedirCodigo = _ => fake.CodigoAgora() };
                var d = await Autorizacao.ResolverAsync(cli, Estorno(414), tela);
                checar(!d.Autorizado && tela.VezesPediuCodigo == 1 && d.Motivo == "Muitas tentativas. Aguarde 10 minutos. Estorno não autorizado.",
                    $"DC-14 'muitas tentativas' com dica: a dica é ignorada e a tela não pede outro código (motivo={d.Motivo})");
            }
            finally { fake.DicaEmTodaRecusa = null; fake.ZerarBaldes(); }
        }

        // DC-15 a dica nunca aprova e nunca gasta o contador: depois de um 'vencido', o código de agora vale
        {
            fake.ZerarBaldes(); ProximoPasso();
            var contadorAntes = fake.UltimoContador;
            var vVenc = await cli.ValidarTotpAsync(fake.CodigoAgora(-5), "estorno:c1", "estorno", null, "dono", CancellationToken.None);
            var vGer = await cli.ValidarTotpAsync(fake.CodigoAgoraGerente(), "estorno:c2", "estorno", null, "dono", CancellationToken.None);
            var contadorDepois = fake.UltimoContador;
            var vAgora = await cli.ValidarTotpAsync(fake.CodigoAgora(), "estorno:c3", "estorno", null, "dono", CancellationToken.None);
            var vGerNaPromo = await cli.ValidarTotpAsync(fake.CodigoAgoraGerente(), "promocao:c4", "promocao", null, "gerente", CancellationToken.None);
            checar(!vVenc.Ok && vVenc.Dica == "vencido" && !vGer.Ok && vGer.Dica == "outro_autenticador"
                   && contadorDepois == contadorAntes && vAgora.Ok && vGerNaPromo.Ok,
                "DC-15 'vencido' e 'outro_autenticador' não aprovam nem gastam contador (o código do gerente ainda vale na promoção dele)");
            fake.ZerarBaldes();
        }

        // ── 3. TEXTO E DIAGNÓSTICO ─────────────────────────────────────────────
        foreach (var t in new[] { TextoVencido, TextoDoGerente })
            checar(t.Length < 60 && !t.Contains('\n') && !t.Contains('—') && !t.Contains('–') && !t.Any(char.IsDigit),
                $"DC-16 '{t}': uma linha, menos de 60 letras, sem travessão e sem número ({t.Length})");

        var linhas = diagnostico.ToArray();
        checar(linhas.Any(l => l.Contains("dica=vencido", StringComparison.Ordinal))
               && linhas.Any(l => l.Contains("dica=outro_autenticador", StringComparison.Ordinal))
               && linhas.All(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"\b\d{6}\b")),
            "DC-17 o diagnóstico do cliente anota a dica (a próxima recusa no Castelo diz qual foi), e nunca o código");
    }

    /// <summary>Um código de 6 dígitos que não bate com o dono nem com o gerente em nenhum passo de -25..+5.</summary>
    private static string CodigoQueNaoBate(FakeTotp f)
    {
        var usados = new HashSet<string>();
        for (var d = -25; d <= 5; d++) { usados.Add(f.CodigoAgora(d)); usados.Add(f.CodigoAgoraGerente(d)); }
        for (var n = 0; ; n++) { var c = n.ToString("D6"); if (!usados.Contains(c)) return c; }
    }
}
