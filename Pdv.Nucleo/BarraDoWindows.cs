using System.Globalization;
using System.Security;
using System.Text;

namespace Pdv.Nucleo;

/// <summary>
/// A BARRA DO WINDOWS NO CAIXA (1.0.58, 29/10/2026, pedido do dono): "tem como adicionar a opção de
/// ver a barra de tarefa sem menu iniciar nem nada, somente configuração de wifi e som?"
///
/// No quiosque o PDV é o shell do Windows: sem Explorer não existe barra de tarefas, nem o ícone do
/// Wi-Fi, nem o do volume. A barra é do próprio PDV, embaixo da tela, com só esses dois. Quem fala
/// com o Windows é o exe (wlanapi e o Core Audio); aqui ficam as regras, para a bateria provar:
/// quando a barra aparece, a lista de redes, o perfil que o Windows grava e o que a tela escreve.
/// </summary>
public static class BarraDoWindows
{
    /// <summary>Config local do caixa: "1" mostra, "0" esconde, sem valor = automático.</summary>
    public const string Chave = "barra_windows";

    /// <summary>
    /// Mostra a barra? Escolha na Configuração vale sempre. Sem escolha, aparece quando o Windows
    /// está sem a barra dele (o quiosque, sem Explorer), que é justamente quando não há outro jeito
    /// de trocar o Wi-Fi ou o volume.
    /// </summary>
    public static bool Mostrar(string? opcao, bool windowsSemBarra) => opcao switch
    {
        "1" => true,
        "0" => false,
        _ => windowsSemBarra,
    };

    /// <summary>O que a caixa da Configuração mostra marcado quando ainda não houve escolha.</summary>
    public static bool MarcadaNaConfiguracao(string? opcao, bool quiosqueLigado) => opcao switch
    {
        "1" => true,
        "0" => false,
        _ => quiosqueLigado,
    };

    // ── Wi-Fi ──────────────────────────────────────────────────────────────────

    /// <summary>Uma rede da lista do Windows (WLAN_AVAILABLE_NETWORK).</summary>
    /// <param name="Sinal">wlanSignalQuality, 0 a 100.</param>
    /// <param name="Autenticacao">DOT11_AUTH_ALGORITHM.</param>
    /// <param name="Cifra">DOT11_CIPHER_ALGORITHM.</param>
    /// <param name="Perfil">Nome do perfil salvo no Windows, quando há.</param>
    public sealed record Rede(byte[] SsidBytes, int Sinal, bool Segura, int Autenticacao, int Cifra,
                              string? Perfil, bool Conectada)
    {
        public string Nome => NomeDoSsid(SsidBytes);
        public bool Salva => !string.IsNullOrEmpty(Perfil);
    }

    /// <summary>O nome que a tela mostra. SSID é bytes; quase sempre UTF-8.</summary>
    public static string NomeDoSsid(byte[] ssid)
    {
        if (ssid.Length == 0) return "";
        try { return new UTF8Encoding(false, true).GetString(ssid); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(ssid); }
    }

    /// <summary>
    /// A lista da tela: sem rede oculta (sem nome), uma linha por nome (o Windows devolve a mesma
    /// rede uma vez com o perfil salvo e outra sem), a conectada primeiro e depois pelo sinal.
    /// </summary>
    public static IReadOnlyList<Rede> Organizar(IEnumerable<Rede> redes)
    {
        var porNome = new Dictionary<string, Rede>(StringComparer.Ordinal);
        foreach (var r in redes)
        {
            var nome = r.Nome;
            if (string.IsNullOrWhiteSpace(nome)) continue;
            if (!porNome.TryGetValue(nome, out var atual)) { porNome[nome] = r; continue; }
            // a que vale mais: conectada, depois a salva, depois a de sinal maior
            var melhor = Peso(r) > Peso(atual) ? r : atual;
            var outra = ReferenceEquals(melhor, r) ? atual : r;
            porNome[nome] = melhor with
            {
                Sinal = Math.Max(melhor.Sinal, outra.Sinal),
                Perfil = melhor.Perfil ?? outra.Perfil,
                Conectada = melhor.Conectada || outra.Conectada,
            };
        }
        return porNome.Values
            .OrderByDescending(r => r.Conectada)
            .ThenByDescending(r => r.Sinal)
            .ThenBy(r => r.Nome, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        static int Peso(Rede r) => (r.Conectada ? 4 : 0) + (r.Salva ? 2 : 0);
    }

    /// <summary>Quantos arcos do leque acendem: 0 a 3.</summary>
    public static int Arcos(int sinal) => sinal switch
    {
        >= 67 => 3,
        >= 34 => 2,
        > 0 => 1,
        _ => 0,
    };

    /// <summary>O texto da barra ao lado do leque.</summary>
    public static string TextoDoWifi(EstadoWifi e) => e.Situacao switch
    {
        SituacaoWifi.Conectado => string.IsNullOrWhiteSpace(e.Rede) ? "Wi-Fi" : e.Rede!,
        SituacaoWifi.Conectando => "Conectando...",
        SituacaoWifi.Desconectado => "Sem Wi-Fi",
        SituacaoWifi.Desligado => "Wi-Fi desligado",
        SituacaoWifi.SemPlaca => e.Cabo ? "Internet pelo cabo" : "Sem rede",
        _ => "Wi-Fi",
    };

    public enum SituacaoWifi { Conectado, Conectando, Desconectado, Desligado, SemPlaca }

    /// <param name="Cabo">Sem placa de Wi-Fi, mas com cabo de rede ligado.</param>
    public sealed record EstadoWifi(SituacaoWifi Situacao, string? Rede, int Sinal, bool Cabo);

    /// <summary>WLAN_INTERFACE_STATE para a situação da barra.</summary>
    public static SituacaoWifi SituacaoDaPlaca(int estado) => estado switch
    {
        1 => SituacaoWifi.Conectado,                 // connected
        5 or 6 or 7 => SituacaoWifi.Conectando,      // associating, discovering, authenticating
        _ => SituacaoWifi.Desconectado,              // not_ready, ad_hoc, disconnecting, disconnected
    };

    /// <summary>Como o Windows deve gravar a rede nova. Null em Motivo = dá para conectar daqui.</summary>
    public sealed record ComoConectar(string? Autenticacao, string? Cifra, bool PedeSenha, string? Motivo);

    // DOT11_AUTH_ALGORITHM e DOT11_CIPHER_ALGORITHM (wlantypes.h)
    private const int AuthAberta = 1, AuthChaveCompartilhada = 2, AuthWpa = 3, AuthWpaPsk = 4,
                      AuthRsna = 6, AuthRsnaPsk = 7, AuthWpa3Ent192 = 8, AuthWpa3Sae = 9, AuthOwe = 10, AuthWpa3Ent = 11;
    private const int CifraNenhuma = 0, CifraWep40 = 1, CifraTkip = 2, CifraWep104 = 5, CifraWep = 0x101;

    public const string MotivoEmpresa = "Essa rede pede usuário e senha de empresa. Chame o suporte.";
    public const string MotivoAntiga = "Essa rede usa uma segurança antiga que o caixa não aceita.";

    public static ComoConectar Conexao(int autenticacao, int cifra)
    {
        if (cifra is CifraWep40 or CifraWep104 or CifraWep || autenticacao == AuthChaveCompartilhada)
            return new(null, null, false, MotivoAntiga);
        return autenticacao switch
        {
            AuthAberta when cifra == CifraNenhuma => new("open", "none", false, null),
            AuthWpaPsk => new("WPAPSK", cifra == CifraTkip ? "TKIP" : "AES", true, null),
            AuthRsnaPsk => new("WPA2PSK", cifra == CifraTkip ? "TKIP" : "AES", true, null),
            AuthWpa3Sae => new("WPA3SAE", "AES", true, null),
            AuthWpa or AuthRsna or AuthWpa3Ent192 or AuthWpa3Ent => new(null, null, false, MotivoEmpresa),
            AuthOwe => new(null, null, false, MotivoAntiga),
            _ => new(null, null, false, MotivoAntiga),
        };
    }

    /// <summary>A senha serve para WPA? Null = serve; senão, a frase para a tela.</summary>
    public static string? ProblemaNaSenha(string? senha)
    {
        if (string.IsNullOrEmpty(senha)) return "Digite a senha do Wi-Fi.";
        if (EhChaveHex(senha)) return null;
        if (senha.Length < 8) return "A senha do Wi-Fi tem pelo menos 8 letras ou números.";
        if (senha.Length > 63) return "Essa senha é longa demais para Wi-Fi.";
        return null;
    }

    private static bool EhChaveHex(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);

    /// <summary>
    /// O perfil que o Windows grava (WlanSetProfile). O nome do perfil é o da rede, como o Windows faz;
    /// o SSID vai também em hexa, que é o que vale para nomes com acento ou símbolo.
    /// </summary>
    public static string PerfilXml(byte[] ssid, ComoConectar como, string? senha)
    {
        if (como.Motivo is not null || como.Autenticacao is null || como.Cifra is null)
            throw new ArgumentException("rede sem perfil possível: " + como.Motivo);
        var nome = SecurityElement.Escape(NomeDoSsid(ssid));
        var hex = Convert.ToHexString(ssid);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?>\n");
        sb.Append("<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">\n");
        sb.Append("  <name>").Append(nome).Append("</name>\n");
        sb.Append("  <SSIDConfig><SSID><hex>").Append(hex).Append("</hex><name>").Append(nome).Append("</name></SSID></SSIDConfig>\n");
        sb.Append("  <connectionType>ESS</connectionType>\n");
        sb.Append("  <connectionMode>auto</connectionMode>\n");
        sb.Append("  <MSM><security>\n");
        sb.Append("    <authEncryption><authentication>").Append(como.Autenticacao)
          .Append("</authentication><encryption>").Append(como.Cifra)
          .Append("</encryption><useOneX>false</useOneX></authEncryption>\n");
        if (como.PedeSenha)
        {
            if (ProblemaNaSenha(senha) is { } p) throw new ArgumentException(p);
            sb.Append("    <sharedKey><keyType>").Append(EhChaveHex(senha!) ? "networkKey" : "passPhrase")
              .Append("</keyType><protected>false</protected><keyMaterial>")
              .Append(SecurityElement.Escape(senha)).Append("</keyMaterial></sharedKey>\n");
        }
        sb.Append("  </security></MSM>\n");
        sb.Append("</WLANProfile>\n");
        return sb.ToString();
    }

    /// <summary>O que a linha da rede diz à direita.</summary>
    public static string Rotulo(Rede r)
    {
        if (r.Conectada) return "Conectado";
        if (r.Salva) return "Salva";
        return r.Segura ? "Com senha" : "Aberta";
    }

    // ── Som ────────────────────────────────────────────────────────────────────

    /// <summary>O volume do Windows (0 a 1) em porcentagem inteira.</summary>
    public static int Porcentagem(float escalar)
        => (int)Math.Round(Math.Clamp(escalar, 0f, 1f) * 100f, MidpointRounding.AwayFromZero);

    public static float Escalar(int porcentagem) => Math.Clamp(porcentagem, 0, 100) / 100f;

    /// <summary>O botão − ou +: anda de 10 em 10, para em 0 e 100.</summary>
    public static int Passo(int atual, int delta) => Math.Clamp(atual + delta, 0, 100);

    /// <summary>Ondas do alto-falante: -1 = mudo (um X), 0 a 2 ondas pelo volume.</summary>
    public static int Ondas(int porcentagem, bool mudo) => mudo ? -1 : porcentagem switch
    {
        <= 0 => 0,
        < 50 => 1,
        _ => 2,
    };

    /// <summary>O texto da barra ao lado do alto-falante.</summary>
    public static string TextoDoSom(bool achou, int porcentagem, bool mudo)
        => !achou ? "Sem som" : mudo ? "Mudo" : porcentagem.ToString(CultureInfo.InvariantCulture) + "%";
}
