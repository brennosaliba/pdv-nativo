using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pdv;

/// <summary>
/// Sobe e vigia o emissor fiscal local (Node) junto com o PDV.
///
/// Ele rodava numa janela de terminal solta — e janela solta alguém fecha. Aí a venda
/// grava, a nota não sai, e o operador vê "não consegui falar com o emissor" sem fazer
/// ideia de que o problema foi um X clicado em outra janela. O emissor é parte do PDV:
/// nasce com ele, é vigiado por ele e morre com ele.
/// </summary>
public static class Agente
{
    private static Process? _proc;
    private static Timer? _vigia;
    private static readonly object Trava = new();

    private const string Pasta = @"C:\kiosk\agent";
    private const string Health = "http://127.0.0.1:4610/health";

    /// <summary>Garante o agente de pé. Chamado no boot e pela vigia a cada 30s.</summary>
    public static void Garantir()
    {
        lock (Trava)
        {
            if (!File.Exists(Path.Combine(Pasta, "pdv-agent.cjs"))) return;   // máquina sem agente: nada a fazer
            if (RespondeAsync().GetAwaiter().GetResult()) return;

            try
            {
                // Outro processo pode estar vivo mas surdo — mata antes de subir outro,
                // senão dois brigam pela mesma porta e pelo mesmo contador.
                if (_proc is { HasExited: false }) { try { _proc.Kill(entireProcessTree: true); } catch { } }

                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Pasta, "node.exe"),
                    Arguments = "pdv-agent.cjs",
                    WorkingDirectory = Pasta,
                    UseShellExecute = false,
                    CreateNoWindow = true,          // sem janela = sem X para fecharem
                };

                // Segredos (senha do cert, CSC) saem do DPAPI direto pro ambiente do
                // filho. Nunca tocam disco em claro, nunca aparecem em janela.
                foreach (var (k, v) in LerSegredos())
                    psi.Environment[k] = v;

                _proc = Process.Start(psi);
            }
            catch { /* a vigia tenta de novo em 30s; a venda em dinheiro não depende disso */ }
        }
    }

    public static void IniciarVigia()
    {
        Garantir();
        _vigia = new Timer(_ => Garantir(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>O agente morre com o PDV — processo órfão segurando a porta vira bug fantasma.</summary>
    public static void Encerrar()
    {
        _vigia?.Dispose();
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
    }

    private static async Task<bool> RespondeAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1500) };
            using var r = await http.GetAsync(Health).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode) return false;
            AlinharSerie(await r.Content.ReadAsStringAsync().ConfigureAwait(false));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Mantém a série exibida igual à série REAL (a do emissor). O campo editável da
    /// configuração já enganou o dono uma vez: ele trocou 9→4 e achou que a nota mudou
    /// de série — ela continuou saindo na 3, porque quem numera é o emissor. Rótulo que
    /// mente é pior que rótulo nenhum, então o rótulo passa a copiar a verdade.
    /// </summary>
    private static void AlinharSerie(string corpoHealth)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(corpoHealth);
            if (!doc.RootElement.TryGetProperty("serie", out var s) || !s.TryGetInt32(out var serie)) return;
            using var cx = Pdv.Nucleo.Banco.Abrir();
            Dapper.SqlMapper.Execute(cx,
                "UPDATE terminal SET serie_nfce = @S WHERE id = 1 AND serie_nfce <> @S", new { S = serie });
        }
        catch { /* rótulo desalinhado não pode impedir o emissor de subir */ }
    }

    private static Dictionary<string, string> LerSegredos()
    {
        var vazio = new Dictionary<string, string>();
        try
        {
            var arq = Path.Combine(Pasta, "secrets.dat");
            if (!File.Exists(arq)) return vazio;
            var plano = ProtectedData.Unprotect(File.ReadAllBytes(arq), null, DataProtectionScope.LocalMachine);
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(plano));
            var r = new Dictionary<string, string>();
            if (doc.RootElement.TryGetProperty("senha", out var s) && s.GetString() is { Length: > 0 } senha)
                r["PDV_CERT_SENHA"] = senha;
            if (doc.RootElement.TryGetProperty("csc", out var c) && c.GetString() is { Length: > 0 } csc)
                r["PDV_CSC"] = csc;
            return r;
        }
        catch { return vazio; }
    }
}
