import re, io

DOC = r"C:\Users\Waz\pdv-nativo\docs\HOMOLOGACAO_58_PASSOS.md"
SAIDA = r"C:\Users\Waz\pdv-nativo\Pdv.Nucleo\RoteiroTef.cs"

doc = io.open(DOC, encoding="utf-8").read()
blocos = re.split(r"\n### Passo ", doc)[1:]

OBR = {n: "SIM" for n in range(1, 59)}
for n in [9, 13, 14, 15, 19, 20, 22, 24, 25, 38, 40]:
    OBR[n] = "OPCIONAL"
for n in [41, 42]:
    OBR[n] = "AUTOATENDIMENTO"
for n in [49, 50, 51, 52, 53]:
    OBR[n] = "CONTROLPAY"
OBR[58] = "C6PAY_ANDROID"

BARRA = chr(92)
ASPA = chr(34)


def cs(s):
    s = (s or "")
    s = s.replace(BARRA, BARRA + BARRA)
    s = s.replace(ASPA, BARRA + ASPA)
    s = re.sub(r"\s+", " ", s).strip()
    return ASPA + s + ASPA


def campo(b, nome):
    m = re.search(r"\*\*" + nome + r":\*\*\s*(.+?)(?=\n\*\*|\n### |\Z)", b, re.S)
    return re.sub(r"\s+", " ", m.group(1)).strip() if m else ""


linhas = []
for b in blocos:
    num = int(b[:2])
    titulo = b.split("\n", 1)[0][4:].strip()
    faz = campo(b, "O que você faz")
    conf = campo(b, "O que conferir")
    situ = campo(b, "Situação")
    mv = re.search(r"R\$ ?([\d.]+,\d\d)", titulo) or re.search(r"R\$ ?([\d.]+,\d\d)", faz)
    valor = mv.group(1) if mv else ""
    linhas.append(
        "        new(%d, %s, %s, %s, %s,\n            %s,\n            %s),"
        % (num, cs(titulo), cs(OBR[num]), cs(valor), cs(situ), cs(faz), cs(conf))
    )

CABECALHO = '''namespace Pdv.Nucleo;

/// <summary>Um passo do roteiro de homologacao do TEF PayGo (planilha v20260819).</summary>
/// <param name="Numero">1 a 58, a mesma numeracao da planilha oficial.</param>
/// <param name="Obrigatoriedade">SIM, OPCIONAL, ou o publico especifico (CONTROLPAY, AUTOATENDIMENTO, C6PAY_ANDROID).</param>
/// <param name="Valor">O valor que o roteiro manda cobrar, em reais com virgula. Vazio quando o passo nao e de venda.</param>
/// <param name="Situacao">Como o caixa estava no levantamento de 07/09/2026.</param>
public sealed record PassoTef(
    int Numero, string Titulo, string Obrigatoriedade, string Valor, string Situacao,
    string OQueFazer, string OQueConferir);

/// <summary>
/// O ROTEIRO DE HOMOLOGACAO DO TEF, DENTRO DO CAIXA.
///
/// GERADO de docs/HOMOLOGACAO_58_PASSOS.md, que foi levantado lendo o roteiro
/// oficial passo a passo contra o codigo. Nao edite este arquivo a mao: mude o
/// documento e gere de novo, senao as duas verdades se separam.
///
/// POR QUE ELE EXISTE (09/09/2026). O dono acabou de fazer a primeira transacao
/// aprovada e pediu: "cria o menu tef de homologacao com cada passo, cada valor".
/// Ate aqui o roteiro morava num PDF, a planilha noutro arquivo, e os valores
/// exatos (R$ 1.000,01 na venda negada, R$ 100.000,00 no valor maximo) eram
/// digitados a mao. Errar um centavo faz o passo inteiro voltar.
///
/// A planilha oficial exige, para integracao por DLL, que a coluna "Retorno do
/// teste" leve o PWINFO_REQNUM de cada transacao. Por isso cada passo guarda o
/// REQNUM junto com o resultado: o que o caixa registra vira a planilha.
/// </summary>
public static class RoteiroTef
{
    /// <summary>O que a planilha exige na coluna "Retorno do teste" nesta integracao.</summary>
    public const string RetornoExigido = "PWINFO_REQNUM";

    public static readonly IReadOnlyList<PassoTef> Passos = new PassoTef[]
    {
'''

RODAPE = '''    };

    /// <summary>
    /// Os passos que valem para ESTA automacao (integracao por biblioteca Windows).
    ///
    /// Ficam de fora os 5 de ControlPay (outra integracao), os 2 de autoatendimento
    /// e o 58, que e C6PAY Android. Rodar passo que nao se aplica gasta cartao de
    /// teste e suja o placar.
    /// </summary>
    public static IReadOnlyList<PassoTef> ParaBibliotecaWindows()
        => Passos.Where(p => p.Obrigatoriedade is "SIM" or "OPCIONAL").ToList();

    /// <summary>Os obrigatorios, que sao os que travam a homologacao.</summary>
    public static IReadOnlyList<PassoTef> Obrigatorios()
        => Passos.Where(p => p.Obrigatoriedade == "SIM").ToList();

    /// <summary>
    /// O valor do passo em centavos, ou null quando o passo nao e de venda.
    ///
    /// Sai do texto do roteiro ("R$ 1.000,01" vira 100001) para o operador nao
    /// digitar centavo nenhum: e digitando que se erra e se perde o passo.
    /// </summary>
    public static long? ValorCent(PassoTef p)
    {
        var v = (p.Valor ?? "").Replace(".", "").Replace(",", "").Trim();
        return v.Length > 0 && long.TryParse(v, out var c) ? c : null;
    }
}
'''

io.open(SAIDA, "w", encoding="utf-8", newline="\n").write(CABECALHO + "\n".join(linhas) + "\n" + RODAPE)
print("gerado com", len(linhas), "passos")
