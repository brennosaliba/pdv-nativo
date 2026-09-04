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

        // ── INCIDENTE REAL: JWT em claro na query do WebSocket do firefly ────
        // O mascaramento era LISTA NEGRA (nome conhecido de parâmetro): pegou
        // "token=" do userpilot e deixou passar x-firefly-access-key e
        // x-amz-customauthorizer-signature. Agora a regra é LISTA BRANCA: some
        // por nome suspeito E por formato de segredo, venha com que nome vier.
        {
            var mf = ChatCaptura.MascararUrl(UrlFirefly);
            checar(!mf.Contains(CabecaJwt), "firefly: o JWT da query NÃO sobra na URL mascarada");
            checar(mf.Contains("x-firefly-access-key=XXXX"),
                "firefly: x-firefly-access-key mantém o NOME e perde o valor");
            checar(!mf.Contains("FT7aThrZ"), "firefly: a assinatura AWS da query some");
            checar(mf.Contains("x-amz-customauthorizer-signature=XXXX"),
                "firefly: x-amz-customauthorizer-signature mantém o NOME e perde o valor");
            checar(mf.Contains("wss://firefly-api.ifood.com.br/"),
                "firefly: host e caminho continuam legíveis (é o que vale no diagnóstico)");

            // valor com cara de segredo some mesmo sob nome nunca previsto
            checar(ChatCaptura.MascararUrl("wss://x.ifood.com.br/ws?carimbo=" + AssinaturaAws)
                    .Contains("carimbo=XXXX"),
                "valor: assinatura longa some sob nome desconhecido (carimbo)");

            // a URL inteira dentro de um campo de quadro (foi por aqui que passou)
            var frameComUrl = $$"""{"tipo":"handshake","endpoint":"{{UrlFirefly}}"}""";
            var mj = ChatCaptura.MascararJson(frameComUrl);
            checar(!mj.Contains(CabecaJwt), "frame: URL com JWT dentro de um campo é mascarada");
            checar(!mj.Contains("FT7aThrZ"), "frame: assinatura dentro da URL de um campo some");

            // JWT solto (com prefixo Bearer) num campo que ninguém previu
            var frameSolto = $$"""{"tipo":"novo","campoQueNinguemPreviu":"Bearer {{JwtFirefly}}"}""";
            checar(!ChatCaptura.MascararJson(frameSolto).Contains(CabecaJwt),
                "frame: JWT solto no meio do corpo é mascarado");

            // legibilidade: o que NÃO é segredo tem que continuar visível
            var comCaminho = ChatCaptura.MascararUrl(
                "wss://firefly-api.ifood.com.br/chat/v1.0/socket?ai=2D7B4CDB&x-firefly-access-key=" + JwtFirefly);
            checar(comCaminho.Contains("wss://firefly-api.ifood.com.br/chat/v1.0/socket"),
                "URL: o caminho continua legível");
            checar(comCaminho.Contains("ai=2D7B4CDB"), "URL: parâmetro público continua legível");

            var ms = ChatCaptura.MascararUrl(UrlSendbird);
            checar(ms.Contains("ai=2D7B4CDB-9012-4A3B-8C5D-6E7F8A9B0C1D"),
                "sendbird: o app_id (ai) continua legível");
            checar(ms.Contains("pv=3.1.6") && ms.Contains("sv=4.9.11"),
                "sendbird: a versão do SDK continua legível");
            checar(ms.Contains("user_id=ifood-9911"), "sendbird: o user_id continua legível");
            checar(ms.Contains("access_token=XXXX") && !ms.Contains("eyJhbGciOiJIUzI1NiJ9"),
                "sendbird: o access_token vira XXXX");
        }

        // ── rede de segurança: o texto INTEIRO do diagnóstico é varrido ──────
        {
            var acc = new ChatCaptura.Acumulador();
            acc.RegistrarWebSocket(UrlFirefly);
            acc.RegistrarFrame(
                $$"""{"tipo":"novo","campoQueNinguemPreviu":"{{JwtFirefly}}","carimbo":"{{AssinaturaAws}}"}""",
                enviado: false);
            var diag = acc.MontarDiagnostico();
            checar(!diag.Contains(CabecaJwt), "rede: token em campo NOVO não aparece no diagnóstico");
            checar(!diag.Contains("FT7aThrZ"), "rede: assinatura em campo NOVO não aparece no diagnóstico");
            checar(diag.Contains("firefly-api.ifood.com.br"), "rede: o host continua no diagnóstico");
            checar(diag.Contains("x-firefly-access-key"), "rede: o NOME do parâmetro continua no diagnóstico");
            checar(diag.Contains("campoQueNinguemPreviu"), "rede: o NOME do campo novo continua legível");

            // seção que ninguém escreveu ainda: a varredura do texto final é a
            // última linha de defesa, e ela não depende de conhecer o campo
            var inventado = "-- secao que nao existe hoje --\n"
                + "  campo_novo: " + JwtFirefly + "\n"
                + "  carimbo: " + AssinaturaAws + "\n"
                + "  endpoint: " + UrlFirefly;
            var limpo = ChatCaptura.MascararTexto(inventado);
            checar(!limpo.Contains(CabecaJwt), "rede: JWT em texto solto é varrido");
            checar(!limpo.Contains("FT7aThrZ"), "rede: assinatura longa em texto solto é varrida");
            checar(limpo.Contains("campo_novo:") && limpo.Contains("carimbo:"),
                "rede: os NOMES dos campos sobrevivem à varredura");
            checar(limpo.Contains("wss://firefly-api.ifood.com.br/")
                && limpo.Contains("x-firefly-access-key=XXXX"),
                "rede: a URL sai por parâmetro (host e nome ficam, valor some)");
            checar(ChatCaptura.MascararTexto(limpo) == limpo, "rede: varrer duas vezes dá o mesmo texto");

            // o que é público continua legível depois da varredura do texto inteiro
            checar(ChatCaptura.MascararTexto("  " + UrlSendbird)
                    .Contains("ai=2D7B4CDB-9012-4A3B-8C5D-6E7F8A9B0C1D"),
                "rede: a varredura não come o app_id do Sendbird");
            checar(ChatCaptura.MascararTexto("  " + UrlSendbird).Contains("sv=4.9.11"),
                "rede: a varredura não come a versão do SDK");

            // a regra em si, exercitada de frente
            checar(ChatCaptura.NomeSensivel("x-firefly-access-key")
                && ChatCaptura.NomeSensivel("x-amz-customauthorizer-signature")
                && ChatCaptura.NomeSensivel("X-Session-Id"),
                "regra: nome suspeito é por PEDAÇO do nome, sem caixa");
            checar(!ChatCaptura.NomeSensivel("ai") && !ChatCaptura.NomeSensivel("user_id")
                && !ChatCaptura.NomeSensivel("sv"),
                "regra: app_id, user_id e versão do SDK não são nomes suspeitos");
            checar(ChatCaptura.ValorSensivel(AssinaturaAws) && ChatCaptura.ValorSensivel(JwtFirefly),
                "regra: valor com cara de segredo cai mesmo sem nome");
            checar(!ChatCaptura.ValorSensivel("2D7B4CDB-9012-4A3B-8C5D-6E7F8A9B0C1D")
                && !ChatCaptura.ValorSensivel("3.1.6") && !ChatCaptura.ValorSensivel("ifood-9911"),
                "regra: id público curto, versão e user_id não são tratados como segredo");
        }
    }

    // ── material do incidente (encurtado, mesmo formato do arquivo real) ─────
    private const string JwtFirefly =
        "eyJhbGciOiJSUzI1NiIsImtpZCI6IjRkOTBhYiJ9"
        + ".eyJzdWIiOiJtZXJjaGFudC05OTExIiwiZXhwIjoxNzg4ODg4ODg4fQ"
        + ".RlQ3YVRoclo5a1FtWG8yYlYxc1BxUjh1WXRHaEprTG1OcFFyU3RVdld4WXo";

    /// <summary>Cabeça do JWT: se ISTO aparecer no diagnóstico, vazou.</summary>
    private const string CabecaJwt = "eyJhbGciOiJSUzI1NiIsImtpZCI6";

    private const string AssinaturaAws =
        "FT7aThrZ9kQmXo2bV1sPqR8uYtGhJkLmNpQrStUvWxYz0123456789%2FabcdEFGH%2BijkLMNO%3D";

    private const string UrlFirefly =
        "wss://firefly-api.ifood.com.br/?x-firefly-access-key=" + JwtFirefly
        + "&x-amz-customauthorizer-signature=" + AssinaturaAws;

    private const string UrlSendbird =
        "wss://ws-2D7B4CDB.sendbird.com/?p=JS&pv=3.1.6&sv=4.9.11"
        + "&ai=2D7B4CDB-9012-4A3B-8C5D-6E7F8A9B0C1D&user_id=ifood-9911"
        + "&access_token=eyJhbGciOiJIUzI1NiJ9.eyJ1IjoidTEiLCJ2IjoxfQ.YXNzaW5hdHVyYS1kZS1tZW50aXJh&active=1";

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
