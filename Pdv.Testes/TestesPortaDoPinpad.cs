using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// A PORTA DO PINPAD, E O AVISO QUE FALTOU.
///
/// 09/09/2026: o dono passou uma hora atrás de "PROBLEMA ARQUIVO DE PARAMETROS"
/// no meio da homologação do TEF. Não era arquivo nenhum. O PDV estava na COM5,
/// e a máquina só tem COM1 e COM3; o pinpad é "Gertec PIN Pad PPC (COM3)".
///
/// O log repetia `PP_iOpen Error #3! 30` dezenas de vezes e a biblioteca mandava
/// para a tela o texto genérico dela, que manda a pessoa caçar a coisa errada.
///
/// SE ESTES TESTES QUEBRAREM, é isso que volta: porta que não existe, silêncio na
/// tela, e uma mensagem que fala de arquivo quando o problema é cabo.
/// </summary>
public static class TestesPortaDoPinpad
{
    public static void Rodar(Action<bool, string> checar)
    {
        // O que esta máquina tem de verdade, lido do Windows em 09/09/2026.
        var reais = new List<PortaSerial>
        {
            new("COM1", "Porta de comunicação"),
            new("COM3", "Gertec PIN Pad PPC"),
        };

        // ── O NÚMERO QUE A BIBLIOTECA QUER ──────────────────────────────────
        checar(new PortaSerial("COM3", "x").Numero == "3", "COM3 vira 3");
        checar(new PortaSerial("COM12", "x").Numero == "12", "COM12 vira 12");
        checar(new PortaSerial("COM03", "x").Numero == "3", "zero à esquerda não vira porta diferente");

        // ── RECONHECER O APARELHO ───────────────────────────────────────────
        checar(PortaDoPinpad.PareceDePinpad("Gertec PIN Pad PPC"), "Gertec PIN Pad é pinpad");
        checar(PortaDoPinpad.PareceDePinpad("INGENICO iPP320"), "Ingenico é pinpad");
        checar(PortaDoPinpad.PareceDePinpad("VeriFone VX 820"), "Verifone é pinpad");
        checar(!PortaDoPinpad.PareceDePinpad("Porta de comunicação"), "porta serial da placa-mãe não é pinpad");
        checar(!PortaDoPinpad.PareceDePinpad(""), "sem nome não se chuta que é pinpad");
        checar(!PortaDoPinpad.PareceDePinpad(null), "nulo não quebra e não é pinpad");

        // ── ACHAR SOZINHO ───────────────────────────────────────────────────
        var achada = PortaDoPinpad.Detectar(reais);
        checar(achada is not null && achada.Com == "COM3", $"acha o pinpad na COM3 ({achada?.Com ?? "nada"})");

        checar(PortaDoPinpad.Detectar(new List<PortaSerial> { new("COM1", "Porta de comunicação") }) is null,
            "sem nenhum candidato, não inventa porta");

        // DOIS pinpads: escolher no chute é mandar comando para o aparelho errado.
        var doisPinpads = new List<PortaSerial>
        {
            new("COM3", "Gertec PIN Pad PPC"),
            new("COM7", "INGENICO iPP320"),
        };
        checar(PortaDoPinpad.Detectar(doisPinpads) is null,
            "com dois pinpads na máquina, não escolhe sozinho");

        checar(PortaDoPinpad.Detectar(Array.Empty<PortaSerial>()) is null, "lista vazia não quebra");

        // ── O AVISO QUE TERIA POUPADO A HORA ────────────────────────────────
        var aviso = PortaDoPinpad.Aviso("5", reais)!;
        checar(aviso.Contains("COM5") && aviso.Contains("não existe"),
            $"diz que a porta configurada não existe ({aviso})");
        checar(aviso.Contains("COM1") && aviso.Contains("COM3"), "e diz quais existem");
        checar(aviso.Contains("Gertec"), "e aponta onde o pinpad parece estar");

        checar(PortaDoPinpad.Aviso("3", reais) is null,
            "porta certa não gera aviso nenhum");

        var trocada = PortaDoPinpad.Aviso("1", reais)!;
        checar(trocada.Contains("COM3"), $"porta que existe mas não é o pinpad vira aviso ({trocada})");
        checar(!trocada.Contains("não existe"), "e esse aviso não mente dizendo que a porta não existe");

        // Automática é escolha legítima: informa, não reclama.
        foreach (var auto in new[] { "0", "", "  " })
        {
            var t = PortaDoPinpad.Aviso(auto, reais);
            checar(t is not null && t.StartsWith("Automática"), $"automática informa o que achou ({auto})");
            checar(t is not null && !t.Contains("não existe"), "e não acusa nada de errado");
        }

        checar(PortaDoPinpad.Aviso("3", Array.Empty<PortaSerial>())!.Contains("Nenhuma porta serial"),
            "máquina sem porta nenhuma fala do cabo, não da configuração");

        // ── O QUE REALMENTE TRAVA ───────────────────────────────────────────
        checar(PortaDoPinpad.ImpedeFuncionar("5", reais), "COM5 inexistente impede o TEF de funcionar");
        checar(!PortaDoPinpad.ImpedeFuncionar("3", reais), "COM3 não impede");
        checar(!PortaDoPinpad.ImpedeFuncionar("0", reais), "automática nunca impede");
        checar(!PortaDoPinpad.ImpedeFuncionar("", reais), "vazio é automática, não impede");
        // Sem conseguir listar as portas, não se acusa a configuração de nada.
        checar(!PortaDoPinpad.ImpedeFuncionar("5", Array.Empty<PortaSerial>()),
            "sem lista de portas, não acusa a configuração");

        // ── COMO APARECE NA TELA ────────────────────────────────────────────
        checar(PortaDoPinpad.Rotulo(new PortaSerial("COM3", "Gertec PIN Pad PPC")) == "COM3 · Gertec PIN Pad PPC",
            "o rótulo mostra a porta e o aparelho");
        checar(PortaDoPinpad.Rotulo(new PortaSerial("COM9", "")) == "COM9",
            "porta sem nome aparece só com o número, sem separador solto");

        // ── SEM TRAVESSÃO ───────────────────────────────────────────────────
        foreach (var t in new[] { aviso, trocada, PortaDoPinpad.Aviso("0", reais)! })
            checar(!t.Contains('—'), "nenhum aviso usa travessão");
    }
}
