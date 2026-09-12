using Dapper;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// O QUE O PAINEL DEFINE E O CAIXA COPIA (12/09/2026): respostas prontas do chat, senha de
/// administrador e música da loja. As regras (ConfigLojaPainel) são provadas pelo valor e
/// num SQLite de verdade; a chamada no Atualizar, pelo fonte.
/// </summary>
public static class TestesConfigLojaPainel
{
    public static void Rodar(Action<bool, string> checar)
    {
        // ── leitura da RPC ─────────────────────────────────────────────────────
        var json = """
            [{"store":"Savassi","chat_respostas":"Pedido frio\nDesculpe!","admin_pin_hash":"H1","admin_pin_salt":"S1",
              "admin_pin_atualizado":"2026-09-12T03:10:00+00:00","playlist_uri":"spotify:playlist:abc","playlist_nome":"Loja",
              "device_id":"dev1","device_nome":"Caixa Savassi","volume":45,"musica_atualizado":null},
             {"store":"Castelo","chat_respostas":null,"admin_pin_hash":null,"admin_pin_salt":null,"admin_pin_atualizado":null,
              "playlist_uri":null,"playlist_nome":null,"device_id":null,"device_nome":null,"volume":60,"musica_atualizado":null}]
            """;
        var linhas = ConfigLojaPainel.Ler(json);
        checar(linhas.Count == 2 && linhas[0].Store == "Savassi" && linhas[0].Volume == 45 && linhas[0].PlaylistUri == "spotify:playlist:abc",
            "a RPC vira linhas com todos os campos");
        checar(linhas[0].AdminEm is { } em && em.ToUniversalTime() == new DateTime(2026, 9, 12, 3, 10, 0, DateTimeKind.Utc),
            "a data da senha é lida com fuso (ISO com offset)");
        checar(ConfigLojaPainel.Ler("{}").Count == 0 && ConfigLojaPainel.Ler("nao e json").Count == 0 && ConfigLojaPainel.Ler(null).Count == 0,
            "JSON estranho = nenhuma linha, nunca exceção");

        // ── a linha desta loja ─────────────────────────────────────────────────
        checar(ConfigLojaPainel.EscolherLinha(linhas, "savassi")?.Store == "Savassi", "pelo nome do terminal, sem diferenciar caixa");
        checar(ConfigLojaPainel.EscolherLinha(linhas, "SAVASSÍ")?.Store == "Savassi", "acento não separa");
        checar(ConfigLojaPainel.EscolherLinha(linhas, "American Day Savassi")?.Store == "Savassi", "nome da loja contido no nome do terminal");
        checar(ConfigLojaPainel.EscolherLinha(linhas, "Centro") is null, "loja que não está na lista (e há mais de uma): nada");
        checar(ConfigLojaPainel.EscolherLinha(new[] { linhas[1] }, "Centro")?.Store == "Castelo", "só uma linha ao alcance (terminal da loja): é ela");

        // ── senha: só quando o painel definiu depois da última aplicada ────────
        var t = new DateTime(2026, 9, 12, 3, 0, 0);
        checar(ConfigLojaPainel.AdminPinNovo(t, null), "nunca aplicada: aplica");
        checar(ConfigLojaPainel.AdminPinNovo(t.AddMinutes(5), t), "painel mais novo: aplica");
        checar(!ConfigLojaPainel.AdminPinNovo(t, t), "mesma data: não reescreve (o ciclo passa a toda hora)");
        checar(!ConfigLojaPainel.AdminPinNovo(null, t) && !ConfigLojaPainel.AdminPinNovo(null, null), "painel sem senha: nada");

        // ── respostas: painel manda = vale; painel vazio = mantém ───────────────
        checar(ConfigLojaPainel.RespostasAAplicar(null, "Local\ntexto") is null && ConfigLojaPainel.RespostasAAplicar("  ", null) is null,
            "painel vazio não apaga o que a loja editou no caixa");
        var novo = ConfigLojaPainel.RespostasAAplicar("Pedido frio\nDesculpe!", null);
        checar(novo is not null && RespostasProntas.Ler(novo).Count == 1 && RespostasProntas.Ler(novo)[0].Titulo == "Pedido frio",
            "texto do painel entra normalizado");
        checar(ConfigLojaPainel.RespostasAAplicar("Pedido frio\nDesculpe!", novo) is null, "igual ao que já está: nada a gravar");

        // ── num SQLite de verdade ──────────────────────────────────────────────
        var db = Path.Combine(Path.GetTempPath(), "config-painel-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        try
        {
            Banco.Migrar(db);
            using var cx = Banco.Abrir(db);
            cx.Execute("INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,ativo,atualizado) VALUES ('_admin_','Administrador','VELHO','SAL','gerente',0,'x')");
            var agora = new DateTime(2026, 9, 12, 8, 0, 0);
            var mudou = ConfigLojaPainel.Aplicar(cx, linhas[0], agora);
            var hash = cx.ExecuteScalar<string>("SELECT pin_hash FROM operador WHERE id='_admin_'");
            checar(hash == "H1", $"a senha do painel entrou na linha _admin_ (hash={hash})");
            checar(Vendas.Config(cx, ConfigLojaPainel.ChaveAdminAplicadoEm) is { Length: > 0 }, "a data da senha aplicada fica gravada");
            checar(RespostasProntas.Ler(Vendas.Config(cx, RespostasProntas.Chave))[0].Titulo == "Pedido frio", "as respostas do painel entraram");
            checar(Vendas.Config(cx, ConfigLojaPainel.ChavePlaylistUri) == "spotify:playlist:abc" && Vendas.Config(cx, ConfigLojaPainel.ChaveVolume) == "45"
                   && Vendas.Config(cx, ConfigLojaPainel.ChaveDeviceNome) == "Caixa Savassi", "a música do painel entrou");
            checar(mudou.Contains("senha") && mudou.Contains("respostas") && mudou.Contains("música"), $"o resumo diz o que mudou ({mudou})");

            // segunda passada igual: nada muda, a senha não é reescrita
            cx.Execute("UPDATE operador SET pin_hash='MEXIDO' WHERE id='_admin_'");
            var mudou2 = ConfigLojaPainel.Aplicar(cx, linhas[0], agora.AddMinutes(10));
            checar(mudou2 == "" && cx.ExecuteScalar<string>("SELECT pin_hash FROM operador WHERE id='_admin_'") == "MEXIDO",
                "o ciclo seguinte não reescreve a senha nem nada (idempotente)");

            // o painel tirou a música: as chaves somem; respostas vazias no painel: as locais ficam
            var semMusica = linhas[1] with { Store = "Savassi" };
            ConfigLojaPainel.Aplicar(cx, semMusica, agora.AddMinutes(20));
            checar(Vendas.Config(cx, ConfigLojaPainel.ChavePlaylistUri) is null && Vendas.Config(cx, ConfigLojaPainel.ChaveDeviceId) is null,
                "painel sem playlist/aparelho: as chaves somem (o painel é a verdade da música)");
            checar(RespostasProntas.Ler(Vendas.Config(cx, RespostasProntas.Chave))[0].Titulo == "Pedido frio", "painel sem respostas: as do caixa ficam");
            checar(cx.ExecuteScalar<int>("SELECT COUNT(*) FROM auditoria WHERE evento IN ('senha_admin_do_painel','respostas_chat_do_painel')") == 2,
                "auditoria: senha e respostas vindas do painel, uma vez cada");
        }
        finally { try { File.Delete(db); } catch { } }

        var nuvem = Fonte(Path.Combine("Pdv.Nucleo", "Nuvem.cs")) ?? "";
        var sinc = Fonte(Path.Combine("Pdv.Nucleo", "Sincronizacao.cs")) ?? "";
        checar(nuvem.Contains("/rest/v1/rpc/pdv_loja_config_caixa") && nuvem.Contains("ConfigLojaPainel.EscolherLinha(linhas, loja)"),
            "a Nuvem chama a RPC e escolhe a linha pelo nome do terminal");
        checar(sinc.Contains("await nuvem.BaixarConfigLojaAsync(cx)"), "o Atualizar (Sincronizacao) puxa a config da loja");
    }

    private static string? Fonte(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pdv.csproj")))
            {
                var c = Path.Combine(dir.FullName, relativo);
                return File.Exists(c) ? File.ReadAllText(c) : null;
            }
        return null;
    }
}
