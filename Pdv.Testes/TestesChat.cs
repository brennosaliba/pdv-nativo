using System.Text;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// Testes da LÓGICA PURA do painel de chat do iFood. O WebView2/CDP não é
/// testável (precisa do Chromium e da página logada), então o que quebra dinheiro
/// ou confunde o operador foi extraído para o Núcleo e é exercitado aqui:
///
///  - leitura do número de não lidas a partir do texto do DOM (variações e ruído);
///  - "só avisa na subida" (máquina de estado do contador);
///  - normalizador dos quadros do WebSocket (espelhando o formato conhecido);
///  - mascaramento: nenhum token/segredo pode escapar para o diagnóstico.
/// </summary>
public static class TestesChat
{
    public static void Rodar(Action<bool, string> checar)
    {
        // ── não lidas: leitura do texto/aria-label ──────────────────────────
        checar(ChatContagem.Ler("1 mensagem") == 1, "não lidas: '1 mensagem' = 1");
        checar(ChatContagem.Ler("9 mensagens") == 9, "não lidas: '9 mensagens' = 9");
        checar(ChatContagem.Ler("10 mensagens") == 10, "não lidas: '10 mensagens' = 10 (multi-dígito)");
        checar(ChatContagem.Ler("3 mensagem(s) do Atendimento não lida(s)") == 3,
            "não lidas: aria-label do Gestor = 3");
        checar(ChatContagem.Ler("9+") == 9, "não lidas: badge '9+' = 9");
        checar(ChatContagem.Ler(null) == 0, "não lidas: nulo = 0");
        checar(ChatContagem.Ler("") == 0, "não lidas: vazio = 0");
        checar(ChatContagem.Ler("   ") == 0, "não lidas: só espaço = 0");
        checar(ChatContagem.Ler("Conversas com clientes") == 0, "não lidas: sem número = 0");
        checar(ChatContagem.Ler("mensagens") == 0, "não lidas: palavra sem número = 0");
        checar(ChatContagem.Ler("999999999999") == 9999, "não lidas: número absurdo é limitado (id vazado)");

        // ── número de pedido para a busca de conversa ───────────────────────
        checar(ChatContagem.NumeroPedidoValido("5592"), "pedido: '5592' é válido");
        checar(!ChatContagem.NumeroPedidoValido(""), "pedido: vazio é inválido");
        checar(!ChatContagem.NumeroPedidoValido(null), "pedido: nulo é inválido");
        checar(!ChatContagem.NumeroPedidoValido("55a2"), "pedido: com letra é inválido");
        checar(!ChatContagem.NumeroPedidoValido("55\"; alert(1)//"), "pedido: injeção de JS é recusada");
        checar(!ChatContagem.NumeroPedidoValido("1234567890123"), "pedido: longo demais é recusado");

        // ── só avisa na SUBIDA (máquina de estado) ──────────────────────────
        {
            var a = new ChatAviso();
            var r0 = a.Observar(0);
            checar(!r0.Avisar && r0.Total == 0, "aviso: primeira leitura (0) não toca (linha de base)");
            var r1 = a.Observar(2);
            checar(r1.Avisar && r1.Delta == 2, "aviso: 0 -> 2 toca (chegou mensagem)");
            var r2 = a.Observar(2);
            checar(!r2.Avisar, "aviso: 2 -> 2 não toca (sem novidade)");
            var r3 = a.Observar(1);
            checar(!r3.Avisar && r3.Total == 1, "aviso: 2 -> 1 não toca (operador leu uma)");
            var r4 = a.Observar(5);
            checar(r4.Avisar && r4.Delta == 4, "aviso: 1 -> 5 toca de novo (subiu)");
            var r5 = a.Observar(-3);
            checar(!r5.Avisar && r5.Total == 0, "aviso: negativo vira 0 e não toca");
        }
        {
            var b = new ChatAviso();
            var p = b.Observar(3);
            checar(!p.Avisar && b.Atual == 3, "aviso: abrir com 3 não lidas NÃO toca sozinho");
            checar(b.Observar(4).Avisar, "aviso: depois disso, 3 -> 4 toca");
            b.Zerar();
            checar(!b.Observar(7).Avisar, "aviso: após recarregar (Zerar), a 1ª leitura é base de novo");
        }

        // ── normalizador de quadros (formato conhecido: texto+autor+hora) ────
        {
            var frame = """
                {"type":"message","conversationId":"conv-9","senderId":"cli-1",
                 "text":"oi, cadê meu pedido?","timestamp":1788888888000}
                """;
            var m = ChatCaptura.NormalizarFrame(frame);
            checar(m is not null, "frame: mensagem simples é normalizada");
            checar(m!.Texto == "oi, cadê meu pedido?", "frame: texto extraído");
            checar(m.ConversaId == "conv-9", "frame: conversa extraída");
            checar(m.Autor == "cli-1", "frame: autor extraído");
            checar(m.Quando is not null && m.Quando.Value.Year == 2026, "frame: hora (epoch ms) convertida");

            var aninhado = """{"event":"chat","data":{"payload":{"body":"aninhado","from":42}}}""";
            var m2 = ChatCaptura.NormalizarFrame(aninhado);
            checar(m2 is not null && m2.Texto == "aninhado" && m2.Autor == "42",
                "frame: texto aninhado em data/payload é achado; autor numérico vira texto");

            checar(ChatCaptura.NormalizarFrame("""{"event":"heartbeat"}""") is null,
                "frame: pulso/controle (sem texto) não vira mensagem");
            checar(ChatCaptura.NormalizarFrame("não é json") is null, "frame: lixo não-JSON não explode");
            checar(ChatCaptura.NormalizarFrame(null) is null, "frame: nulo não explode");

            var lista = new[]
            {
                ChatCaptura.NormalizarFrame("""{"text":"a","conversationId":"c1"}"""),
                ChatCaptura.NormalizarFrame("""{"text":"b","conversationId":"c1"}"""),
                ChatCaptura.NormalizarFrame("""{"text":"c","conversationId":"c2"}"""),
            }.Where(x => x is not null).Select(x => x!).ToList();
            var conversas = ChatCaptura.AgruparConversas(lista);
            checar(conversas.Count == 2, "agrupar: 3 mensagens em 2 conversas");
            checar(conversas[0].Id == "c1" && conversas[0].Mensagens.Count == 2, "agrupar: c1 tem 2 mensagens");
        }

        // ── JWT {u,v,e}: lê o FORMATO, mascara o userId ─────────────────────
        {
            var jwt = MontarJwt("""{"alg":"HS256","typ":"JWT"}""", """{"u":"user-123","v":2,"e":1788888888}""");
            var f = ChatCaptura.LerFormatoToken(jwt);
            checar(f is not null && f.EhJwt && f.Segmentos == 3, "jwt: reconhece 3 segmentos");
            checar(f!.UserIdMascarado == "XXXX", "jwt: userId (u) sai MASCARADO");
            checar(f.V == 2, "jwt: claim v (versão) lido");
            checar(f.E == 1788888888, "jwt: claim e (expiração) lido");
            checar(f.Expira is not null && f.Expira.Value.Year == 2026, "jwt: expiração vira data");
            checar(ChatCaptura.LerFormatoToken("abc.def") is { EhJwt: false }, "jwt: 2 segmentos não é JWT");
            checar(ChatCaptura.LerFormatoToken(null) is null, "jwt: nulo é nulo");
        }

        // ── mascaramento: nada de segredo no diagnóstico ────────────────────
        {
            var jwt = MontarJwt("""{"alg":"HS256"}""", """{"u":"u1","v":1,"e":1}""");
            checar(ChatCaptura.ParaceJwt(jwt), "máscara: reconhece cara de JWT");
            checar(!ChatCaptura.ParaceJwt("oi.mundo"), "máscara: texto curto com ponto não é JWT");
            checar(ChatCaptura.Mascarar("qualquer-segredo") == "XXXX", "máscara: valor vira XXXX");

            var comToken = $$"""{"access_token":"{{jwt}}","user":"joao","ok":true}""";
            var mascarado = ChatCaptura.MascararJson(comToken);
            checar(!mascarado.Contains(jwt), "máscara: o JWT NÃO aparece no JSON mascarado");
            checar(mascarado.Contains("XXXX"), "máscara: o valor sensível virou XXXX");
            checar(mascarado.Contains("\"user\":\"joao\""), "máscara: campo não-sensível é preservado");
            checar(mascarado.Contains("access_token"), "máscara: a CHAVE é preservada (o dono precisa do shape)");

            var soJwtNoValor = $$"""{"campo_qualquer":"{{jwt}}"}""";
            checar(!ChatCaptura.MascararJson(soJwtNoValor).Contains(jwt),
                "máscara: string com cara de JWT some mesmo sem chave sensível");

            checar(ChatCaptura.MascararJson("não-json").Contains("omitido") ,
                "máscara: quadro não-JSON é omitido, não vazado cru");

            var url = "wss://chat.ifood.com.br/ws?token=" + jwt + "&room=42";
            var urlMasc = ChatCaptura.MascararUrl(url);
            checar(!urlMasc.Contains(jwt), "máscara: token na query da URL some");
            checar(urlMasc.Contains("room=42"), "máscara: parâmetro não-sensível fica");
            checar(urlMasc.Contains("wss://chat.ifood.com.br/ws"), "máscara: host e caminho ficam");
        }

        // ── acumulador: o token nunca escapa em claro para o diagnóstico ────
        {
            var jwt = MontarJwt("""{"alg":"HS256"}""", """{"u":"segredo-do-dono","v":3,"e":1788888888}""");
            var acc = new ChatCaptura.Acumulador();
            acc.RegistrarWebSocket("wss://chat.ifood.com.br/socket?token=" + jwt);
            acc.RegistrarToken(jwt);
            acc.RegistrarAuthResposta($$"""{"expiresAt":1788888888,"token":"{{jwt}}"}""");
            acc.RegistrarFrame("""{"type":"message","conversationId":"c1","text":"ola","senderId":"cli"}""", enviado: false);
            acc.RegistrarFrame("""{"event":"ack","ref":1}""", enviado: true);

            checar(acc.TokenEmMemoria == jwt, "acc: token FICA em memória (para o parser nativo)");
            var diag = acc.MontarDiagnostico();
            checar(!diag.Contains(jwt), "acc: o diagnóstico NÃO contém o token em claro");
            checar(!diag.Contains("segredo-do-dono"), "acc: o userId não vaza no diagnóstico");
            checar(diag.Contains("XXXX"), "acc: o diagnóstico mostra XXXX no lugar do segredo");
            checar(diag.Contains("shape:"), "acc: o diagnóstico descreve o SHAPE dos quadros");
            checar(diag.Contains("wss://chat.ifood.com.br/socket"), "acc: a URL do WS (mascarada) está no diagnóstico");
            checar(acc.Mensagens().Count == 1, "acc: 1 quadro vira mensagem normalizada (o ack não)");
        }
    }

    /// <summary>Monta um JWT de teste (base64url) a partir de header e payload JSON.</summary>
    private static string MontarJwt(string headerJson, string payloadJson)
    {
        string B64(string s)
        {
            var b = Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
            return b.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        return B64(headerJson) + "." + B64(payloadJson) + "." + B64("assinatura-de-mentira");
    }
}
