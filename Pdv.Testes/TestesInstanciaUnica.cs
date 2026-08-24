using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A 2ª INSTÂNCIA DO PDV CONTRA A COBRANÇA QUE ESTÁ NO PINPAD.
///
/// Cenário real: o pinpad está esperando o cartão/senha do cliente, a tela do PDV
/// parece parada, o operador acha que "travou" e clica de novo no ícone. Sobe um
/// SEGUNDO Pdv.exe. Ele attacha no mesmo turno aberto (MainWindow.Roteia →
/// `Caixa.SessaoAberta` → `MostrarVenda`) e, no boot, roda o religamento do TEF.
///
/// E o religamento decide o que é "cobrança abandonada" comparando `criado_em` com
/// o START DESTE PROCESSO:
///   • Servicos.ResolverPendenciasTefAsync — `criado_em &lt; @Inicio`, com
///     `Inicio = Process.GetCurrentProcess().StartTime`, nos dois blocos (PayGo e ControlPay);
///   • ControlPay.ReconciliarAsync — `if (… em >= boot) continue;`, mesmo `boot`.
/// Para a 2ª instância, a cobrança VIVA da 1ª nasceu antes do boot dela: é sempre
/// "abandonada". O `SemaphoreSlim _um` do ClienteControlPay não salva — é campo de
/// instância, morre na fronteira do processo.
///
/// O desfecho é o pior possível: a cobrança legítima (que pode ter sido APROVADA no
/// pinpad) vira `orfa` com "confira no PayGo e estorne se aprovou" — e o operador
/// estorna dinheiro que era da loja.
///
/// A trava é INSTÂNCIA ÚNICA: o 2º Pdv.exe não boota. Este teste prova isso com DOIS
/// PROCESSOS DE VERDADE (a SONDA 4), não com duas chamadas no mesmo processo — o furo
/// é justamente que toda a defesa de hoje é intra-processo.
/// </summary>
public static class TestesInstanciaUnica
{
    /// <summary>Código de saída do processo-filho quando a trava recusou o boot.</summary>
    public const int Recusada = 3;

    public static void Rodar(Action<bool, string> checar)
    {
        // ── 1. A TRAVA EM SI ────────────────────────────────────────────────
        // Nome próprio por execução: o Pdv.exe de verdade pode estar aberto nesta
        // máquina (e vai estar, na loja) — o teste não pode brigar com ele.
        var nome = "PdvNativo.Teste." + Guid.NewGuid().ToString("N");
        var primeira = InstanciaUnica.Tentar(nome);
        checar(primeira is not null, "o 1º PDV pega a trava do terminal");

        var segunda = InstanciaUnica.Tentar(nome);
        checar(segunda is null, "com um PDV aberto, o 2º é RECUSADO pela trava");
        segunda?.Dispose();
        primeira?.Dispose();

        var religou = InstanciaUnica.Tentar(nome);
        checar(religou is not null, "fechado o 1º, o caixa religa normalmente (a trava não fica presa)");
        religou?.Dispose();

        // ── 2. SONDA 4: DOIS PROCESSOS DE VERDADE ───────────────────────────
        var arquivo = Path.Combine(Path.GetTempPath(), $"instancia_teste_{Guid.NewGuid():N}.db");
        var travaSonda = "PdvNativo.Teste." + Guid.NewGuid().ToString("N");
        try
        {
            Banco.Migrar(arquivo);
            SemearCobrancasVivas(arquivo);

            using (var dono = InstanciaUnica.Tentar(travaSonda))
            {
                checar(dono is not null, "sonda: a 1ª instância (cliente no pinpad) está com a trava");

                var (codigo, saida) = SubirSegundaInstancia(arquivo, travaSonda);
                checar(codigo == Recusada,
                    $"a 2ª instância sai sem bootar (código {codigo}, esperado {Recusada}){saida}");

                var (paygo, cpay) = Situacoes(arquivo);
                checar(paygo == "aguardando",
                    $"a cobrança PayGo de R$ 500,00 continua 'aguardando' no pinpad (ficou '{paygo}')");
                checar(cpay == "criando",
                    $"a intenção ControlPay em voo continua 'criando' (ficou '{cpay}')");
            }

            // ── 3. CONTROLE NEGATIVO ────────────────────────────────────────
            // Sem a trava na mão, o MESMO processo-filho, no MESMO banco, declara as
            // duas órfãs. É o que provava a SONDA 4 — e é o que o teste acima impede.
            // Sem esta parte, o teste passaria mesmo que o religamento tivesse virado
            // um no-op por acidente.
            {
                var (codigo, saida) = SubirSegundaInstancia(arquivo, travaSonda);
                checar(codigo == 0, $"solta a trava e a instância boota de verdade (código {codigo}){saida}");

                var (paygo, cpay) = Situacoes(arquivo);
                checar(paygo == "orfa" && cpay == "orfa",
                    $"controle negativo: SEM a trava o religamento carimba órfã a cobrança viva "
                    + $"(paygo='{paygo}', cpay='{cpay}') — é exatamente isto que a trava impede");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(arquivo); } catch { }
        }

        // ── 4. O BOOT DO PDV REALMENTE PASSA PELA TRAVA ─────────────────────
        // A trava só vale se estiver no caminho do boot, ANTES de o processo mexer no
        // banco e de disparar o religamento do TEF. Isto é code-behind de WPF: não dá
        // para instanciar num teste, então se confere a fonte.
        {
            string? fonte = null;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                var alvo = Path.Combine(d.FullName, "App.xaml.cs");
                if (File.Exists(alvo)) { fonte = File.ReadAllText(alvo); break; }
            }
            checar(fonte is not null, "achei a fonte do boot (App.xaml.cs) para conferir a trava");

            var f = fonte ?? "";
            var trava = f.IndexOf("InstanciaUnica.Tentar", StringComparison.Ordinal);
            checar(trava >= 0, "o boot do PDV pega a trava de instância única");

            var religamento = f.IndexOf("ResolverPendenciasTefAsync", StringComparison.Ordinal);
            checar(trava >= 0 && religamento > trava,
                "a trava vem ANTES de o boot disparar o religamento do TEF");

            // O `--cupom-teste`/`--imprimir-teste` roda e sai sem abrir o caixa: aquele
            // Migrar pode conviver com o PDV aberto. O que a trava tem de cobrir é o
            // Migrar do caminho normal — o ÚLTIMO do arquivo.
            var migrar = f.LastIndexOf("Banco.Migrar()", StringComparison.Ordinal);
            checar(trava >= 0 && migrar > trava,
                "a trava vem ANTES de a 2ª instância tocar no banco do caixa");

            // RECUSADA TEM QUE SER MORTE, NÃO PEDIDO DE SAÍDA.
            // Medido num app WPF de teste: com `Shutdown(0); return;` dentro de OnStartup,
            // o WPF AINDA constrói o StartupUri (MainWindow) — Shutdown só posta a saída no
            // dispatcher. E o construtor da MainWindow chama Roteia(), que abre o banco do
            // caixa e attacha no turno aberto. Ou seja: com Shutdown a trava não trava nada.
            var recusada = f.IndexOf("if (_trava is null)", StringComparison.Ordinal);
            var fim = recusada < 0 ? -1 : f.IndexOf("\n        }", recusada, StringComparison.Ordinal);
            var corpo = recusada < 0 || fim < 0 ? "" : f[recusada..fim];
            checar(corpo.Contains("Environment.Exit", StringComparison.Ordinal),
                "a instância recusada MORRE ali (Environment.Exit) — não segue para a MainWindow");
            checar(corpo.Length > 0 && !corpo.Contains("Shutdown(", StringComparison.Ordinal),
                "a recusa não confia no Shutdown() do WPF, que ainda constrói a MainWindow");
        }
    }

    /// <summary>
    /// Duas cobranças VIVAS, do jeito que ficam com o cliente no pinpad: uma do PayGo
    /// já 'aguardando' resposta e uma intenção do ControlPay ainda 'criando'. `criado_em`
    /// é AGORA — o que, para um processo que nasce depois, é sempre "antes do boot".
    /// </summary>
    private static void SemearCobrancasVivas(string arquivo)
    {
        using var cx = Banco.Abrir(arquivo);
        var agora = DateTime.Now.ToString("o");
        cx.Execute("""
            INSERT INTO tef_transacao (id, charge_id, identificacao, tipo, valor_cent, parcelas,
                                       situacao, provedor, criado_em, atualizado_em)
            VALUES ('t-paygo', 'paygo-viva', '167601', 'credito', 50000, 1, 'aguardando', 'paygo', @Em, @Em),
                   ('t-cpay',  'cpay-viva',  '167602', 'credito', 50000, 1, 'criando',    'controlpay', @Em, @Em)
            """, new { Em = agora });
    }

    private static (string Paygo, string Cpay) Situacoes(string arquivo)
    {
        SqliteConnection.ClearAllPools();
        using var cx = Banco.Abrir(arquivo);
        return (cx.ExecuteScalar<string>("SELECT situacao FROM tef_transacao WHERE id = 't-paygo'") ?? "?",
                cx.ExecuteScalar<string>("SELECT situacao FROM tef_transacao WHERE id = 't-cpay'") ?? "?");
    }

    /// <summary>
    /// Sobe um processo DE VERDADE que se comporta como um Pdv.exe recém-aberto
    /// (modo `--sonda-2a-instancia` do Program.cs). Devolve o código de saída e,
    /// para o diagnóstico, o que ele escreveu.
    /// </summary>
    private static (int Codigo, string Saida) SubirSegundaInstancia(string banco, string trava)
    {
        var exe = Environment.ProcessPath ?? "dotnet";
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // `dotnet Pdv.Testes.dll` em vez do apphost: o .dll entra como 1º argumento.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "");
        psi.ArgumentList.Add("--sonda-2a-instancia");
        psi.ArgumentList.Add(banco);
        psi.ArgumentList.Add(trava);

        using var p = Process.Start(psi)!;
        var saida = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(60_000)) { try { p.Kill(true); } catch { } return (-1, " [a 2ª instância não terminou]"); }
        return (p.ExitCode, saida.Trim().Length == 0 ? "" : " — " + saida.Trim().Replace("\r", "").Replace("\n", " | "));
    }
}
