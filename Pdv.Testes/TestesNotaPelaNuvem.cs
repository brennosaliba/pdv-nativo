using Pdv.Nucleo;
using Pdv.Telas;

namespace Pdv.Testes;

/// <summary>
/// CERTIFICADO E CSC NUM LUGAR SÓ (14/09/2026, loja Castelo).
///
/// A Configuração do caixa pedia certificado e CSC no passo 2, mas eles só servem ao emissor
/// LOCAL, que não está instalado na Castelo: a nota sai pela nuvem (edge nfce-emitir, EC2), com o
/// certificado e o CSC cadastrados no painel em Empresa &amp; Fiscal. O dono cadastrou no caixa e a
/// nota falhou com "Sem CSC de prod para o CNPJ". Sem o emissor local, o caixa não pede mais nada
/// disso e mostra em uma linha o que o PAINEL tem, lido por uma RPC que devolve só metadados.
/// </summary>
public static class TestesNotaPelaNuvem
{
    // Meio-dia de Brasília em UTC: a data não muda em nenhum fuso do Brasil.
    private const string CscCastelo = "2026-09-14T15:07:08.635+00:00";
    private static readonly DateTime Hoje = new(2026, 9, 14, 21, 0, 0);

    public static void Rodar(Action<bool, string> checar)
    {
        QuemPedeCertificado(checar);
        Leitura(checar);
        Linhas(checar);
        Resumo(checar);
        Telas(checar);
    }

    private static void QuemPedeCertificado(Action<bool, string> checar)
    {
        checar(!NotaPelaNuvem.PedirCertificadoNoCaixa(emissorLocalInstalado: false, modoRecibo: false),
            "sem o emissor local a nota sai pela nuvem: o caixa NÃO pede certificado nem CSC");
        checar(NotaPelaNuvem.PedirCertificadoNoCaixa(emissorLocalInstalado: true, modoRecibo: false),
            "com o emissor local instalado o fluxo de hoje continua (certificado e CSC no caixa)");
        checar(!NotaPelaNuvem.PedirCertificadoNoCaixa(true, true) && !NotaPelaNuvem.PedirCertificadoNoCaixa(false, true),
            "só recibo não pede nada");
        checar(NotaPelaNuvem.Rpc == "pdv_fiscal_da_loja", "a RPC do painel tem o nome da migration");
    }

    private static void Leitura(Action<bool, string> checar)
    {
        var json = $$"""
            [{"store":"American Day Castelo","cnpj":"62177839000319","cert_valido_ate":null,"cert_atualizado_em":null,
              "csc_prod_em":"{{CscCastelo}}","csc_homolog_em":"2026-09-14T15:06:32+00:00","tp_amb":1,"emissor_ativo":true},
             {"store":"American Day Savassi","cnpj":"62177839000238","cert_valido_ate":"2027-03-10","cert_atualizado_em":"2026-03-11T13:00:00+00:00",
              "csc_prod_em":null,"csc_homolog_em":null,"tp_amb":1,"emissor_ativo":true}]
            """;
        var linhas = NotaPelaNuvem.Ler(json);
        checar(linhas.Count == 2, $"leitura: as duas lojas da resposta ({linhas.Count})");
        var castelo = NotaPelaNuvem.Escolher(linhas, "62.177.839/0003-19");
        checar(castelo?.Loja == "American Day Castelo" && castelo.TpAmb == 1 && castelo.EmissorAtivo && castelo.CscProdEm is not null,
            "leitura: acha a loja pelo CNPJ do caixa, com ou sem máscara");
        var savassi = NotaPelaNuvem.Escolher(linhas, "62177839000238");
        checar(savassi?.CertValidoAte == new DateTime(2027, 3, 10), "leitura: validade do certificado como data");
        checar(NotaPelaNuvem.Escolher(linhas, "11222333000181") is null, "leitura: CNPJ que não está na resposta não pega outra loja");
        checar(NotaPelaNuvem.Escolher(linhas, "") is null, "leitura: sem CNPJ no caixa e duas lojas, não chuta");
        checar(NotaPelaNuvem.Escolher(new[] { castelo! }, null)?.Loja == "American Day Castelo",
            "leitura: sem CNPJ no caixa e uma loja só (a do terminal), é ela");
        checar(NotaPelaNuvem.Ler(null).Count == 0 && NotaPelaNuvem.Ler("{ruim").Count == 0 && NotaPelaNuvem.Ler("{}").Count == 0,
            "leitura: resposta vazia ou quebrada vira lista vazia, sem derrubar a tela");
        checar(!typeof(FiscalDaLoja).GetProperties().Any(p => p.Name.Equals("Csc", StringComparison.OrdinalIgnoreCase)
                                                             || p.Name.Contains("Senha", StringComparison.OrdinalIgnoreCase)),
            "leitura: o caixa não tem onde guardar segredo nenhum vindo do painel");
    }

    private static FiscalDaLoja Loja(DateTime? certAte = null, DateTime? csProd = null, DateTime? csHomolog = null, int? tpAmb = 1, bool ativo = true)
        => new("American Day Castelo", "62177839000319", certAte, null, csProd, csHomolog, tpAmb, ativo);

    private static void Linhas(Action<bool, string> checar)
    {
        var cscEm = DateTimeOffset.Parse(CscCastelo).LocalDateTime;

        var castelo = NotaPelaNuvem.Linha(Loja(csProd: cscEm), ConsultaFiscal.Respondeu, Hoje);
        checar(castelo.Nivel == 0 && castelo.Texto == "Nota pela nuvem: CSC de produção cadastrado em 14/09.",
            "Castelo hoje: CSC de produção cadastrado, sem inventar nada sobre o certificado: " + castelo.Texto);

        var tudo = NotaPelaNuvem.Linha(Loja(certAte: new DateTime(2027, 3, 10), csProd: cscEm), ConsultaFiscal.Respondeu, Hoje);
        checar(tudo.Nivel == 0 && tudo.Texto == "Nota pela nuvem: certificado ok até 10/03/2027, CSC de produção cadastrado em 14/09.",
            "com o certificado no painel: " + tudo.Texto);

        var semCsc = NotaPelaNuvem.Linha(Loja(certAte: new DateTime(2027, 3, 10)), ConsultaFiscal.Respondeu, Hoje);
        checar(semCsc.Nivel == 2 && semCsc.Texto == "Nota pela nuvem: falta o CSC de produção. Cadastre no painel em Empresa & Fiscal.",
            "sem CSC de produção (o erro da Castelo): " + semCsc.Texto);

        var homolog = NotaPelaNuvem.Linha(Loja(csProd: cscEm, tpAmb: 2), ConsultaFiscal.Respondeu, Hoje);
        checar(homolog.Nivel == 2 && homolog.Texto.Contains("falta o CSC de homologação", StringComparison.Ordinal),
            "emissor em homologação cobra o CSC de homologação: " + homolog.Texto);

        var vencido = NotaPelaNuvem.Linha(Loja(certAte: new DateTime(2026, 9, 1), csProd: cscEm), ConsultaFiscal.Respondeu, Hoje);
        checar(vencido.Nivel == 2 && vencido.Texto == "Nota pela nuvem: o certificado venceu em 01/09/2026. Cadastre o novo no painel em Empresa & Fiscal.",
            "certificado vencido: " + vencido.Texto);

        var vence = NotaPelaNuvem.Linha(Loja(certAte: new DateTime(2026, 10, 1), csProd: cscEm), ConsultaFiscal.Respondeu, Hoje);
        checar(vence.Nivel == 1 && vence.Texto == "Nota pela nuvem: o certificado vence em 01/10/2026. Cadastre o novo no painel em Empresa & Fiscal.",
            "certificado vencendo em menos de 30 dias: " + vence.Texto);

        var fora = NotaPelaNuvem.Linha(null, ConsultaFiscal.Respondeu, Hoje);
        checar(fora.Nivel == 2 && fora.Texto == "Nota pela nuvem: o CNPJ deste caixa não está no painel. Cadastre a loja em Empresa & Fiscal.",
            "loja fora do painel: " + fora.Texto);

        var semEmissor = NotaPelaNuvem.Linha(Loja(csProd: cscEm, tpAmb: null), ConsultaFiscal.Respondeu, Hoje);
        checar(semEmissor.Nivel == 2 && semEmissor.Texto.Contains("não tem emissor na nuvem", StringComparison.Ordinal),
            "loja sem emissor configurado na nuvem: " + semEmissor.Texto);
        var desligado = NotaPelaNuvem.Linha(Loja(csProd: cscEm, ativo: false), ConsultaFiscal.Respondeu, Hoje);
        checar(desligado.Nivel == 2 && desligado.Texto.Contains("desligado", StringComparison.Ordinal),
            "emissor da loja desligado: " + desligado.Texto);

        var semSessao = NotaPelaNuvem.Linha(null, ConsultaFiscal.SemSessao, Hoje);
        checar(semSessao.Nivel == 1 && semSessao.Texto.Contains("pareie o caixa", StringComparison.Ordinal),
            "caixa ainda não pareado: " + semSessao.Texto);
        var semRede = NotaPelaNuvem.Linha(null, ConsultaFiscal.SemRede, Hoje);
        checar(semRede.Nivel == 1 && semRede.Texto.Contains("sem internet", StringComparison.Ordinal), "sem internet: " + semRede.Texto);
        var caiu = NotaPelaNuvem.Linha(null, ConsultaFiscal.NaoDisponivel, Hoje);
        checar(caiu.Nivel == 1 && caiu.Texto.Contains("Empresa & Fiscal", StringComparison.Ordinal),
            "painel sem a conferência (RPC ainda não publicada): aviso, não erro: " + caiu.Texto);

        var todas = new[] { castelo, tudo, semCsc, homolog, vencido, vence, fora, semEmissor, desligado, semSessao, semRede, caiu };
        checar(todas.All(l => l.Texto.StartsWith("Nota pela nuvem: ", StringComparison.Ordinal) && !l.Texto.Contains('—')),
            "toda linha começa igual e não tem travessão");
    }

    private static void Resumo(Action<bool, string> checar)
    {
        var d = new DadosAssistente { Loja = "AMERICAN DAY CASTELO", Cnpj = "62177839000319", Ie = "ISENTO", Serie = "2", Ambiente = 1,
                                      TemCertificado = false, NotaPelaNuvem = true };
        var nota = AssistenteConfig.Resumo(d).FirstOrDefault(l => l.Titulo == "Nota fiscal");
        checar(nota is not null && !nota.Atencao && !nota.Valor.Contains("sem certificado", StringComparison.Ordinal)
               && nota.Valor.Contains("painel", StringComparison.Ordinal),
            "resumo: nota pela nuvem em produção não acusa 'sem certificado' do caixa: " + (nota?.Valor ?? "-"));
        var local = AssistenteConfig.Resumo(d with { NotaPelaNuvem = false }).First(l => l.Titulo == "Nota fiscal");
        checar(local.Atencao && local.Valor.Contains("sem certificado", StringComparison.Ordinal),
            "resumo: com o emissor local e sem certificado, o aviso de hoje continua");
    }

    private static void Telas(Action<bool, string> checar)
    {
        var config = Fonte("Telas", "Configuracao.xaml.cs") ?? "";
        checar(config.Length > 0 && !config.Contains(@"C:\kiosk\agent\pdv-agent.cjs", StringComparison.Ordinal),
            "a Configuração não procura mais o emissor só na pasta antiga (o instalador põe ao lado do caixa)");
        checar(config.Contains("NotaPelaNuvem.PedirCertificadoNoCaixa(", StringComparison.Ordinal),
            "a Configuração decide pedir certificado e CSC pela regra testada aqui");
        var i = config.IndexOf("\"Falta o certificado\"", StringComparison.Ordinal);
        var antes = i < 0 ? "" : config[Math.Max(0, i - 400)..i];
        checar(i >= 0 && antes.Contains("PedirCertificadoNoCaixa", StringComparison.Ordinal),
            "o aviso 'Falta o certificado' do Salvar só aparece quando o caixa pede certificado");
        var xaml = Fonte("Telas", "Configuracao.xaml") ?? "";
        checar(xaml.Contains("x:Name=\"TxtNotaNuvem\"", StringComparison.Ordinal), "o passo 2 tem a linha da nota pela nuvem");
        var nuvem = Fonte("Pdv.Nucleo", "Nuvem.cs") ?? "";
        checar(nuvem.Contains("NotaPelaNuvem.Rpc", StringComparison.Ordinal), "o caixa lê pela RPC do painel (sessão do terminal)");
    }

    private static string? Fonte(params string[] partes)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var alvo = Path.Combine(new[] { dir.FullName }.Concat(partes).ToArray());
            if (File.Exists(alvo)) return File.ReadAllText(alvo);
        }
        return null;
    }
}
