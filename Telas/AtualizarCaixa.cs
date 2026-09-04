using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Printing;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Dapper;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// O botão "Atualizar" da barra — a parte que mexe na máquina.
///
/// A DECISÃO toda (comparar versão, validar o manifesto, recusar, montar o texto)
/// mora em <see cref="Nucleo.Atualizacao"/>, onde a suíte alcança. Aqui fica só o que
/// só existe com WPF na frente: ler o estado do caixa, mostrar o progresso e ENTREGAR
/// o instalador.
///
/// A entrega é a parte que precisa ser dita em voz alta: este arquivo NÃO instala
/// nada. Ele baixa o InstalarPdv.exe para o TEMP, prova que o arquivo é o que devia
/// ser, chama o instalador (que sobe o UAC sozinho pelo manifesto dele) e fecha o
/// PDV. Quem troca arquivo em uso, preserva C:\ProgramData e confere que o caixa ABRE
/// antes de gravar registro e atalho é o instalador — código que já existe, já foi
/// testado e não pode ter uma segunda versão morando aqui.
/// </summary>
public static class AtualizarCaixa
{
    /// <summary>
    /// HttpClient próprio e de vida longa. Não é o do Fiscal de propósito: aquele tem
    /// timeout infinito (a SEFAZ demora) e handler afinado para nota fiscal. Baixar
    /// 265 MB e emitir nota não têm nada em comum além do protocolo.
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Onde perguntar quando o caixa ainda não fala com o painel. Config por loja
    /// (`atualizacao_url`) com o servidor da MMTech como padrão — o mesmo executável
    /// atende clientes diferentes.
    ///
    /// ⚠️ ESTE ENDEREÇO TAMBÉM É A ÂNCORA DE SEGURANÇA do caminho do painel: o host
    /// do instalador é conferido contra ELE, e não contra quem respondeu a consulta.
    /// É o que garante que a RPC escolha QUAL versão e QUANDO, e nunca DE ONDE — o exe
    /// continua obrigado a morar no servidor que ESTE caixa foi configurado para
    /// confiar, que é um dado local e não um campo vindo da rede.
    /// </summary>
    public static string UrlDoManifesto()
    {
        try
        {
            using var cx = Banco.Abrir();
            var u = Vendas.Config(cx, "atualizacao_url");
            return string.IsNullOrWhiteSpace(u) ? Atualizacao.UrlPadrao : u!.Trim();
        }
        catch { return Atualizacao.UrlPadrao; }
    }

    // ── O QUE O CAIXA ESTÁ VIVENDO ────────────────────────────────────────────

    /// <summary>
    /// Junta o estado que decide se dá para atualizar agora. A comanda e a maquininha
    /// vêm da TELA (só ela sabe); o resto vem do banco e do spooler.
    /// </summary>
    public static Atualizacao.EstadoDoCaixa EstadoAgora(int itensNaComanda, bool maquininhaOcupada)
    {
        var cobrancas = 0;
        var caixaAberto = false;
        var vendas = 0;
        var incerto = false;
        try
        {
            using var cx = Banco.Abrir();
            // 'criando'/'aguardando' = o pinpad está com o cliente AGORA (ou o PDV
            // morreu no meio de uma cobrança e ninguém reconciliou). Fechar o caixa
            // por cima disso é como uma cobrança fica órfã: o cliente pagou e a venda
            // não existe aqui. É a mesma leitura que o religamento do TEF usa no boot.
            cobrancas = cx.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM tef_transacao WHERE situacao IN ('criando','aguardando')");
            caixaAberto = Caixa.SessaoAberta(cx) is not null;
        }
        catch
        {
            // Banco indisponível: no caminho MANUAL os outros portões continuam valendo
            // (tem gente olhando a loja). No AUTOMÁTICO isto vira recusa — não dá para
            // jurar que não tem cobrança no pinpad sem conseguir ler a tabela dela.
            incerto = true;
        }

        try { vendas = Sincronizacao.VendasNaoEntregues().Total; } catch { vendas = 0; }

        // A fila em uma linha para o painel (04/09: ela parou por 3 h e o heartbeat
        // não dizia nada). Falhou a leitura? Vai nulo e o relato segue como sempre.
        string? fila = null;
        try { fila = Sincronizacao.ResumoDaFila(); } catch { fila = null; }

        return new Atualizacao.EstadoDoCaixa(
            ItensNaComanda: itensNaComanda,
            MaquininhaOcupada: maquininhaOcupada,
            CobrancasNoPinpad: cobrancas,
            PapeisNaFila: PapeisNaFila(),
            CaixaAberto: caixaAberto,
            VendasPorSubir: vendas,
            EstadoIncerto: incerto,
            Fila: fila);
    }

    // ── O ESTADO QUANDO NÃO TEM TELA PARA PERGUNTAR ───────────────────────────

    /// <summary>
    /// A comanda e a maquininha, do jeito que a TELA sabe. Quem tem a comanda na mão é
    /// ela; este delegate é o único jeito de o caminho automático (que roda sem clique)
    /// alcançar esse número.
    ///
    /// Fica opcional de propósito: sem ele o caixa ainda decide, só que pelo disco (ver
    /// <see cref="EstadoSemTela"/>), e com a régua estrita. Registrar é uma linha na
    /// tela de venda — e enquanto ela não existir nada aqui quebra.
    /// </summary>
    public static Func<(int ItensNaComanda, bool MaquininhaOcupada)>? OQueATelaViveAgora { get; set; }

    /// <summary>
    /// O estado do caixa para o caminho AUTOMÁTICO, sem depender de ninguém estar de
    /// frente para a tela.
    ///
    /// Duas fontes, nesta ordem:
    ///  1. a TELA, se ela se registrou. É a verdade: tem a comanda na memória;
    ///  2. o DISCO. A comanda em andamento é gravada a cada bipe em `comanda_rascunho`
    ///     (foi feita para sobreviver a queda de energia), então a linha existir já é
    ///     "tem gente digitando" — e no automático isso basta para não mexer.
    ///
    /// ⚠️ A REGRA DO DISCO É "NA DÚVIDA, BARRA", e ela é o oposto da do botão. Linha
    /// velha de um turno que já fechou também barra: ela some sozinha no próximo login
    /// (Rascunho.Ler apaga rascunho de outra sessão), então o custo é uma noite a mais
    /// na versão antiga. O erro no outro sentido custa a frente de caixa.
    /// </summary>
    public static Atualizacao.EstadoDoCaixa EstadoSemTela()
    {
        if (OQueATelaViveAgora is { } perguntar)
        {
            try
            {
                var (itens, maquininha) = perguntar();
                return EstadoAgora(itens, maquininha);
            }
            catch { /* tela morrendo no meio da pergunta: cai para o disco */ }
        }

        int itensNoDisco;
        try
        {
            using var cx = Banco.Abrir();
            itensNoDisco = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM comanda_rascunho WHERE id = 1");
        }
        catch { itensNoDisco = -1; }

        // A maquininha sem a tela: quem responde é a tabela `tef_transacao`, já lida
        // por EstadoAgora — toda cobrança nasce como linha 'criando' ANTES de o cartão
        // encostar no pinpad, então o portão que importa está coberto. O que fica de
        // fora é operação administrativa do pinpad que não cria transação; ver o
        // relatório (fecharia com um `Servicos.TefSeJaExiste()`, que é de outra frente).
        var e = EstadoAgora(Math.Max(itensNoDisco, 0), maquininhaOcupada: false);
        return itensNoDisco < 0 ? e with { EstadoIncerto = true } : e;
    }

    /// <summary>
    /// Papéis esperando nas filas que este caixa usa (cupom e comanda). -1 = não deu
    /// para ler.
    ///
    /// ⚠️ Isto lê o SPOOLER DO WINDOWS, que é a fila DEPOIS que o trabalho saiu do PDV.
    /// A fila de DENTRO do processo (o semáforo da bobina em Impressao) não é visível
    /// daqui — está privada, e o arquivo está sendo mexido por outra frente. Na prática
    /// a janela cega é de milissegundos (o trabalho entra no spooler quase junto), e os
    /// outros portões (comanda, pinpad) cobrem o caso que importa. Ver o relatório: o
    /// que fecharia isso é um `Impressao.ImprimindoAgora` público.
    ///
    /// Prazo curto e falha silenciosa de propósito: consultar impressora de rede fora
    /// do ar trava por segundos, e "não consegui ler a fila" não pode virar "não pode
    /// atualizar" — seria o botão morrendo por causa de um driver.
    /// </summary>
    public static int PapeisNaFila()
    {
        var tarefa = Task.Run(() =>
        {
            var nomes = new List<string?>();
            try
            {
                using var cx = Banco.Abrir();
                nomes.Add(Vendas.Config(cx, "impressora"));
                nomes.Add(Vendas.Config(cx, "kds_comanda_impressora"));
            }
            catch { }
            // null/vazio = "padrão do Windows": resolve para o nome real e desduplica,
            // senão a mesma fila é contada duas vezes e vira bloqueio fantasma.
            var padrao = Impressao.ImpressoraPadrao();
            var alvos = nomes
                .Select(n => string.IsNullOrWhiteSpace(n) ? padrao : n!.Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (alvos.Count == 0) return 0;

            var total = 0;
            using var servidor = new LocalPrintServer();
            foreach (var nome in alvos)
            {
                using var fila = servidor.GetPrintQueue(nome);
                fila.Refresh();
                total += fila.NumberOfJobs;
            }
            return total;
        });

        try { return tarefa.Wait(TimeSpan.FromSeconds(2)) ? tarefa.Result : -1; }
        catch { return -1; }
    }

    // ── QUEM MANDA NA VERSÃO ──────────────────────────────────────────────────

    /// <summary>
    /// O NOME DA RPC do painel. Sai de config (`atualizacao_rpc`) para que renomear a
    /// função no banco não exija republicar 40 caixas.
    /// </summary>
    public const string RpcPadrao = "pdv_versao_do_terminal";

    /// <summary>
    /// ⚠️ ESTE É O ÚNICO PONTO QUE FALA COM O PAINEL. A RPC está sendo escrita por
    /// outra frente; quando ela existir de verdade, é ESTA função que muda — e só ela.
    ///
    /// O CONTRATO QUE ESTE LADO ASSUMIU (declarado aqui de propósito, para conferir
    /// contra o outro lado em vez de descobrir na loja):
    ///
    ///   POST /rest/v1/rpc/pdv_versao_do_terminal
    ///   corpo: { "_produto": "pdv", "_terminal_uuid": "...", "_loja_id": "...",
    ///            "_versao": "0.3.0",
    ///            "_estado": { "pode_trocar_agora": true, "impedimento": "nenhum",
    ///                         "turno_aberto": false, "vendas_por_subir": 0,
    ///                         "desvio_relogio_seg": -3 } }
    ///
    ///   resposta: objeto, lista de UM objeto, ou null.
    ///     { "produto": "pdv",                  ← obrigatório quando há versão
    ///       "versao": "0.4.0",                 ← ausente/null = nada para este terminal
    ///       "url": "https://.../InstalarPdv-0.4.0.exe",
    ///       "sha256": "…64 hex…", "tamanho": 265123456,
    ///       "notas": "…", "obrigatoria": false,
    ///       "janela_inicio": "05:00", "janela_fim": "07:00",
    ///       "agora": "2026-08-29T05:12:03-03:00",   ← relógio DA LOJA, com o fuso dela
    ///       "atualizar_agora": false }
    ///
    /// TRÊS COISAS DO CONTRATO QUE NÃO SÃO ENFEITE:
    ///  · a MESMA chamada pergunta e REPORTA. `_versao` precisa ir para o painel poder
    ///    liberar loja por loja; então relatar sai de graça, e o painel nunca fica mais
    ///    de um ciclo (15 min) atrasado sobre qual versão cada caixa roda;
    ///  · `agora` vem no fuso DA LOJA, calculado pelo servidor. O caixa não carrega
    ///    banco de fusos horários e não confia no relógio da máquina de balcão;
    ///  · a `url` continua sendo conferida contra a `atualizacao_url` DESTE caixa
    ///    (ver <see cref="UrlDoManifesto"/>). O painel manda na versão, não no domínio.
    ///
    /// Devolve null quando ESTE CAIXA NÃO FALA COM O PAINEL — sem credencial de nuvem,
    /// sem pareamento, ou a função ainda não existe no banco (404/PGRST202). Aí quem
    /// responde é o versao.json, e é por isso que este arquivo já pode subir antes de
    /// a RPC nascer.
    /// </summary>
    private static async Task<Atualizacao.LeituraInstrucao?> ConsultarPainelAsync(
        Atualizacao.EstadoDoCaixa? estado, TimeSpan? desvio, CancellationToken ct)
    {
        try
        {
            if (!Servicos.TemContaDeNuvem()) return null;

            string? terminalUuid, lojaId, rpc, urlNuvem;
            using (var cx = Banco.Abrir())
            {
                var t = cx.QueryFirstOrDefault("SELECT terminal_uuid, loja_id FROM terminal LIMIT 1");
                terminalUuid = t?.terminal_uuid as string;
                lojaId = t?.loja_id as string;
                rpc = Vendas.Config(cx, "atualizacao_rpc", RpcPadrao);
                urlNuvem = Vendas.Config(cx, "supabase_url");
            }
            // Caixa recém instalado, ainda não pareado: ele não tem de quem receber
            // ordem. O arquivo público é exatamente o caminho dele.
            if (string.IsNullOrWhiteSpace(terminalUuid)) return null;

            var baseUrl = (string.IsNullOrWhiteSpace(urlNuvem) ? Nuvem.UrlPadrao : urlNuvem!).TrimEnd('/');
            var token = await Servicos.Nuvem().TokenAsync(ct).ConfigureAwait(false);
            if (token is null) return null;      // sessão não renovou: não é hora de decidir nada

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Atualizacao.PrazoDoManifesto);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/rest/v1/rpc/{rpc}");
            req.Headers.TryAddWithoutValidation("apikey", Nuvem.AnonKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(
                Atualizacao.CorpoDaPergunta(terminalUuid, lojaId, Atualizacao.VersaoInstalada(), estado, desvio),
                Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);

            // A função ainda não existe (a outra frente está escrevendo ela): isto NÃO é
            // erro de atualização, é "o painel ainda não sabe responder". Cai para o
            // arquivo em silêncio, sem acender alarme em 40 lojas.
            if (resp.StatusCode is HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode)
                return new Atualizacao.LeituraInstrucao(null,
                    $"O painel respondeu {(int)resp.StatusCode} ao ser perguntado sobre a versão deste caixa.");

            var corpo = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return Atualizacao.LerInstrucao(corpo, UrlDoManifesto());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch { return null; }    // painel inalcançável: o arquivo assume
    }

    /// <summary>
    /// De onde vem a ordem: PAINEL primeiro, arquivo depois.
    ///
    /// O arquivo continua existindo por um motivo concreto e não por nostalgia: o caixa
    /// recém-instalado, ainda sem pareamento, não tem identidade para o painel
    /// reconhecer — e é justamente ele quem mais precisa se atualizar. O que o arquivo
    /// NÃO faz é agendar: quem vem por ele nasce com <c>Origem.Arquivo</c>, e
    /// <see cref="Atualizacao.DecidirSozinho"/> recusa autonomia a essa origem.
    /// </summary>
    public static async Task<Atualizacao.LeituraInstrucao> ConsultarAsync(
        Atualizacao.EstadoDoCaixa? estado = null, TimeSpan? desvio = null, CancellationToken ct = default)
    {
        if (await ConsultarPainelAsync(estado, desvio, ct).ConfigureAwait(false) is { } doPainel)
            return doPainel;

        var arquivo = await Atualizacao.ConsultarAsync(Http, UrlDoManifesto(), ct).ConfigureAwait(false);
        return new Atualizacao.LeituraInstrucao(
            arquivo.Ok is { } m ? Atualizacao.DoArquivo(m) : null, arquivo.Erro);
    }

    // ── A CHECAGEM SILENCIOSA (o "tem atualização" do TeamViewer) ─────────────

    /// <summary>
    /// Pergunta sem abrir nada na tela. É o que acende o selo no botão: o dono pediu
    /// para o caixa AVISAR, não para alguém ter que ir procurar. Devolve a versão nova,
    /// ou null (em dia, sem rede, servidor fora — tanto faz: checagem silenciosa que
    /// falha tem que falhar em silêncio mesmo).
    ///
    /// Ela também é quem LIGA o vigia da janela, e isso é de propósito: o vigia é esta
    /// mesma checagem, no relógio dela. Ligar aqui faz a janela passar a existir sem
    /// precisar de linha nova em <c>Venda.xaml.cs</c>, que está com outra frente.
    /// </summary>
    public static async Task<Atualizacao.Manifesto?> ProcurarNoSilencioAsync()
    {
        Vigiar();
        try
        {
            var leitura = await ConsultarAsync(EstadoSemTela()).ConfigureAwait(false);
            if (leitura.Ok?.Manifesto is not { } m) return null;
            return Atualizacao.Comparar(Atualizacao.VersaoInstalada(), m.Versao) < 0 ? m : null;
        }
        catch { return null; }
    }

    // ── O BOTÃO ───────────────────────────────────────────────────────────────

    /// <summary>
    /// O clique inteiro: portão → consulta → confirmação → download → conferência →
    /// portão DE NOVO → entrega ao instalador → o PDV sai de cena.
    ///
    /// Devolve true quando o instalador foi entregue e o PDV está fechando — a tela
    /// não deve fazer mais nada depois disso.
    /// </summary>
    public static async Task<bool> ExecutarAsync(Window dono, int itensNaComanda, bool maquininhaOcupada)
    {
        // Uma troca de cada vez. Sem isto, o vigia da janela e o dedo do operador podem
        // baixar o mesmo arquivo em paralelo e — pior — entregar o instalador duas
        // vezes. Quem chega depois desiste em silêncio: no botão, o operador vê o
        // diálogo do vigia; no vigia, o próximo ciclo tenta de novo em 15 min.
        if (!await _umaDeCadaVez.WaitAsync(0).ConfigureAwait(true))
        {
            Dialogo.Avisar(dono, "Atualização em andamento", "Espere terminar.", "erro");
            return false;
        }
        try
        {
            return await ExecutarInternoAsync(dono, itensNaComanda, maquininhaOcupada).ConfigureAwait(true);
        }
        finally { _umaDeCadaVez.Release(); }
    }

    private static async Task<bool> ExecutarInternoAsync(Window dono, int itensNaComanda, bool maquininhaOcupada)
    {
        // 1. PORTÃO PRIMEIRO. Antes da rede, antes de qualquer coisa: se tem cliente no
        //    balcão, a resposta é não, e não interessa se existe versão nova. Perguntar
        //    ao servidor primeiro só serviria para transformar uma recusa clara numa
        //    conversa mais longa com o mesmo fim.
        // Fora da thread da tela: ler a fila do spooler pode custar até 2 s (impressora
        // de rede fora do ar), e travar a UI é como um botão vira "não fez nada".
        var estado = await Task.Run(() => EstadoAgora(itensNaComanda, maquininhaOcupada));
        var leitura = Atualizacao.Impede(estado) != Atualizacao.Impedimento.Nenhum
            ? new Atualizacao.LeituraManifesto(null, null)      // nem consulta: já recusou
            : ParaOBotao(await ConsultarAsync(estado, DesvioAgora()));

        var v = Atualizacao.Decidir(estado, Atualizacao.VersaoInstalada(), leitura);
        if (v.Situacao != Atualizacao.Situacao.Disponivel)
        {
            Dialogo.Avisar(dono, v.Titulo, v.Mensagem,
                v.Situacao == Atualizacao.Situacao.EmDia ? "ok" : "erro");
            return false;
        }

        // 2. O SIM do operador. É aqui que ele fica sabendo que o caixa vai fechar.
        if (!Dialogo.Confirmar(dono, v.Titulo, v.Mensagem, v.TextoSim, v.TextoNao))
        {
            Auditar("atualizacao_recusada", $"{Atualizacao.VersaoInstalada()} → {v.Manifesto!.Versao}"
                + (v.Obrigatoria ? " (obrigatória)" : ""));
            return false;
        }

        // 3. DOWNLOAD. Janela modal com Cancelar que funciona de verdade.
        //
        //    Modal de propósito: enquanto baixa, o caixa não vende. Parece duro e é o
        //    contrário — este é o único momento em que a loja tem a rede inteira para
        //    o download. Deixar vender por baixo colocaria 265 MB disputando a mesma
        //    internet ruim com a autorização da NFC-e e com a cobrança do cartão, que
        //    é como uma venda trava por um motivo que ninguém liga ao botão que
        //    apertaram. Chegou cliente? Cancelar — o pedaço baixado fica guardado e a
        //    próxima tentativa continua de onde parou.
        var (baixa, cancelou) = await BaixarComJanelaAsync(dono, v.Manifesto!);
        if (cancelou) return false;
        if (!baixa.Ok)
        {
            Auditar("atualizacao_falhou", baixa.Erro);
            Dialogo.Avisar(dono, "A atualização não terminou",
                baixa.Erro + "\nO caixa continua na versão " + Atualizacao.VersaoInstalada() + ".", "erro");
            return false;
        }

        // 4. PORTÃO DE NOVO. Entre o primeiro portão e aqui passaram minutos de
        //    download — tempo de sobra para um cliente chegar no balcão, o operador
        //    bipar um produto e o pinpad estar com o cartão de alguém. O arquivo já
        //    está pronto no TEMP; trocar agora é escolha, não obrigação.
        var agora = await Task.Run(() => EstadoAgora(itensNaComanda, maquininhaOcupada));
        var impede = Atualizacao.Impede(agora);
        if (impede != Atualizacao.Impedimento.Nenhum)
        {
            var (t, msg) = Atualizacao.Explicar(impede, agora);
            Dialogo.Avisar(dono, t, msg + "\nA versão nova já está baixada.", "erro");
            return false;
        }

        // 5. O PONTO SEM VOLTA, dito com todas as letras. Vale a segunda pergunta:
        //    entre o "sim" lá de cima e este instante passaram minutos, e sumir da
        //    tela sem avisar é diferente de fechar porque alguém mandou.
        // texto do dono (03/09): curto e direto
        if (!Dialogo.Confirmar(dono, "Atualização concluída", "Deseja reiniciar o PDV?",
                "Atualizar e reiniciar", "Ainda não"))
            return false;

        // 6. ENTREGA.
        var erro = EntregarAoInstalador(baixa.Caminho!);
        if (erro is not null)
        {
            Auditar("atualizacao_entrega_falhou", erro);
            Dialogo.Avisar(dono, "Não consegui abrir o instalador",
                erro + "\nO caixa continua funcionando. Arquivo: " + baixa.Caminho, "erro");
            return false;
        }

        Auditar("atualizacao_entregue", $"{Atualizacao.VersaoInstalada()} → {v.Manifesto.Versao}");
        // O instalador já está de pé e elevado. Sair AGORA é o que deixa a troca
        // limpa: menos arquivo em uso para renomear, e o teste de abertura que o
        // instalador faz no fim roda contra a máquina sem o PDV velho por cima.
        Application.Current.Shutdown();
        return true;
    }

    /// <summary>
    /// Chama o instalador e devolve null quando ele subiu.
    ///
    /// <c>UseShellExecute = true</c> NÃO é detalhe: o InstalarPdv.exe tem
    /// requireAdministrator no manifesto, e sem o shell o Windows recusa iniciar o
    /// processo em vez de mostrar o UAC. E é justamente por isso que a recusa do UAC
    /// chega aqui como Win32Exception 1223 — que não é falha nenhuma, é o dono dizendo
    /// não. Confundir os dois faria o caixa fechar depois de uma recusa.
    /// </summary>
    public static string? EntregarAoInstalador(string exe)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                // 03/09: modo silencioso. O instalador troca o programa e reabre o
                // caixa sem mostrar o assistente (ver Pdv.Instalador/App.xaml.cs).
                Arguments = "--atualizar",
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe)!,
                UseShellExecute = true,
            });
            return p is null ? "O Windows não abriu o instalador." : null;
        }
        catch (Win32Exception w) when (w.NativeErrorCode == 1223)
        {
            return "Permissão do Windows recusada. Toque em Atualizar de novo e responda Sim.";
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static void Auditar(string evento, string? detalhe)
    {
        try
        {
            using var cx = Banco.Abrir();
            Caixa.Auditar(cx, null, evento, null, null, detalhe);
        }
        catch { /* o rastro é bom ter, não é motivo para travar a atualização */ }
    }

    // ── A JANELA DE PROGRESSO ─────────────────────────────────────────────────

    /// <summary>
    /// Baixa mostrando MB reais. Devolve (resultado, cancelouOOperador).
    /// </summary>
    private static async Task<(Atualizacao.Baixa Baixa, bool Cancelou)> BaixarComJanelaAsync(
        Window dono, Atualizacao.Manifesto m)
    {
        using var cts = new CancellationTokenSource();
        var janela = Dialogo.Base(dono, 480);
        var pilha = new StackPanel();

        pilha.Children.Add(new TextBlock
        {
            Text = $"Baixando a versão {m.Versao}",
            FontSize = 22, FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["Texto"],
        });

        var linha = new TextBlock
        {
            Text = "Conectando…",
            FontSize = 15, Foreground = (Brush)Application.Current.Resources["TextoFraco"],
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 10),
        };
        pilha.Children.Add(linha);

        var barra = new ProgressBar
        {
            Height = 8, Minimum = 0, Maximum = 100, IsIndeterminate = true,
            BorderThickness = new Thickness(0),
            Background = (Brush)Application.Current.Resources["PainelAlto"],
            Foreground = (Brush)Application.Current.Resources["Rosa"],
        };
        pilha.Children.Add(barra);

        pilha.Children.Add(new TextBlock
        {
            Text = "Pode cancelar. O que já baixou fica guardado.",
            FontSize = 13, Foreground = (Brush)Application.Current.Resources["TextoFraco"],
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 18),
        });

        var cancelar = new Button
        {
            Content = "Cancelar",
            Style = (Style)Application.Current.Resources["BotaoBase"],
            MinHeight = 52, FontSize = 16,
            Background = (Brush)Application.Current.Resources["PainelAlto"],
        };
        var pediuCancelar = false;
        cancelar.Click += (_, _) => { pediuCancelar = true; cts.Cancel(); };
        pilha.Children.Add(cancelar);

        janela.Content = Dialogo.Moldura(pilha);
        // Escape = cancelar. Fechar no X não existe (a janela não tem barra), e é bom
        // assim: sumir com a janela sem cancelar deixaria o download órfão rodando.
        janela.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { pediuCancelar = true; cts.Cancel(); }
        };

        var andamento = new Progress<Atualizacao.Andamento>(a =>
        {
            linha.Text = Atualizacao.TextoDoProgresso(a);
            if (a.Porcento is { } p) { barra.IsIndeterminate = false; barra.Value = p; }
        });

        Atualizacao.Baixa? resultado = null;
        // A janela só abre DEPOIS de o download estar de pé (ShowDialog bloqueia).
        janela.Loaded += async (_, _) =>
        {
            // async void por baixo: exceção que escape daqui não tem quem pegue e derruba
            // o processo — ou seja, derruba a FRENTE DE CAIXA por causa de um download.
            try { resultado = await Atualizacao.BaixarAsync(Http, m, null, andamento, cts.Token); }
            catch (Exception ex) { resultado = new Atualizacao.Baixa(null, ex.Message); }
            janela.Close();
        };
        janela.ShowDialog();

        return (resultado ?? new Atualizacao.Baixa(null, "O download não chegou a começar."),
                pediuCancelar);
    }

    // ══ O VIGIA DA JANELA ═════════════════════════════════════════════════════
    //
    // "COMO VAI FUNCIONAR A ATUALIZAÇÃO REMOTA" — e o que "remota" pode significar aqui.
    //
    // Não pode significar FORÇAR. O TeamViewer reinicia a máquina de alguém quando quer
    // porque ninguém perde dinheiro nisso; atualizar o caixa FECHA O CAIXA, e fechar o
    // caixa com um cliente no balcão é prejuízo com hora marcada. Então remoto aqui
    // quer dizer AGENDAR: o painel manda, e o caixa cumpre no próximo momento seguro.
    //
    // DUAS PERGUNTAS, DUAS FUNÇÕES, E AS DUAS PRECISAM DIZER SIM:
    //   · a JANELA responde "POSSO agora?"  → Atualizacao.DecidirSozinho
    //   · o PORTÃO responde "é SEGURO agora?" → Atualizacao.ImpedeSozinho
    // A janela não é exceção ao portão; ela é uma condição A MAIS. Um caixa dentro da
    // janela com comanda aberta não se atualiza, ponto — e é por isso que as duas não
    // foram fundidas numa função só, onde mais cedo ou mais tarde alguém acrescentaria
    // um `||`.
    //
    // E AS DUAS SÃO CONFERIDAS DUAS VEZES: antes de baixar e antes de trocar. Entre uma
    // coisa e outra passam 20 a 40 minutos de download na internet de uma loja — tempo
    // de sobra para a janela virar, para o dono cancelar a onda no painel, e para
    // alguém chegar e começar a vender.

    /// <summary>Uma troca por vez, entre o botão e o vigia.</summary>
    private static readonly SemaphoreSlim _umaDeCadaVez = new(1, 1);

    /// <summary>O relógio da loja, ancorado na última resposta do painel. Ver
    /// <see cref="Atualizacao.RelogioDaLoja"/> para por que não é <c>DateTime.Now</c>.</summary>
    private static Atualizacao.RelogioDaLoja? _relogio;

    /// <summary>De quanto em quanto tempo o vigia pergunta. 15 min é o que faz uma
    /// janela de 2 h ser encontrada com folga (o ciclo de 6 h do selo do botão passaria
    /// direto por ela) sem virar tráfego de fundo: a pergunta é ~1 KB.</summary>
    public static readonly TimeSpan IntervaloDoVigia = TimeSpan.FromMinutes(15);

    /// <summary>A primeira pergunta não é imediata: quem acabou de abrir a tela de venda
    /// já está consultando, e duas chamadas juntas no boot só atrasam a tela.</summary>
    private static readonly TimeSpan EsperaInicial = TimeSpan.FromMinutes(2);

    /// <summary>Quanto tempo o "agora não" do operador vale. Ver o veto no ciclo.</summary>
    public static readonly TimeSpan EsperaDepoisDoVeto = TimeSpan.FromHours(2);

    private static int _vigiaLigado;
    private static long _vetadoAte;
    private static string _ultimoMotivo = "";

    /// <summary>
    /// Liga o vigia. Idempotente: chamar de novo não cria um segundo relógio.
    ///
    /// Hoje quem chama é <see cref="ProcurarNoSilencioAsync"/>. Quando a tela de venda
    /// puder receber uma linha nova, chamar daqui explicitamente no boot é melhor — o
    /// vigia passa a existir mesmo com o caixa parado na tela de login.
    /// </summary>
    public static void Vigiar()
    {
        if (Interlocked.Exchange(ref _vigiaLigado, 1) == 1) return;
        _ = Task.Run(VigiaAsync);
    }

    private static async Task VigiaAsync()
    {
        await Task.Delay(EsperaInicial).ConfigureAwait(false);
        while (true)
        {
            // Nada aqui pode escapar: exceção solta numa Task de fundo derruba o
            // processo no finalizador — ou seja, derruba a frente de caixa por causa
            // de uma checagem de versão.
            try { await UmCicloAsync().ConfigureAwait(false); }
            catch { }
            await Task.Delay(IntervaloDoVigia).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Uma volta do vigia: pergunta, decide, baixa, RE-pergunta, RE-decide, troca.
    /// </summary>
    private static async Task UmCicloAsync()
    {
        // O botão está no meio de uma troca: sai sem fazer nada e tenta na próxima.
        if (!await _umaDeCadaVez.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var estado = EstadoSemTela();
            var leitura = await ConsultarAsync(estado, DesvioAgora()).ConfigureAwait(false);
            _relogio = Atualizacao.RelogioDaLoja.Ancorar(leitura.Ok?.AgoraNaLoja) ?? _relogio;

            var v = Atualizacao.DecidirSozinho(
                estado, Atualizacao.VersaoInstalada(), leitura.Ok, _relogio?.HoraDaLoja);
            if (!v.Pode) { Registrar(v); return; }

            // ── Cabe no que sobrou de janela?
            // Arquivo já baixado só precisa de 2 min (entregar e sair). Baixar 265 MB
            // na internet de loja não cabe em 5, e começar sabendo que não cabe satura
            // o link bem na hora em que a loja abre. Não é desperdício adiar: o pedaço
            // já baixado fica no disco e a noite seguinte continua de onde parou.
            var m = v.Manifesto!;
            var pronto = Atualizacao.JaBaixado(m);
            if (!Atualizacao.CabeNaJanela(v.MinutosDeJanela, pronto is not null))
            {
                Registrar(v with { Motivo = $"só restam {v.MinutosDeJanela} min de janela" });
                return;
            }

            // ── DOWNLOAD COM PRAZO. O prazo é o FIM DA JANELA, menos o tempo da troca.
            //
            // Esta é a resposta para "e se a janela virar no meio do download": o
            // download é CANCELADO, e cancelar aqui não custa nada — o `.parcial` fica
            // no disco e a próxima janela manda `Range: bytes=N-` e continua de onde
            // parou. Um instalador que não cabe numa noite entra em duas. O que NÃO
            // pode acontecer é a troca (que fecha o caixa) rodar fora da hora
            // combinada só porque o download atrasou.
            if (pronto is null)
            {
                using var cts = new CancellationTokenSource(
                    TimeSpan.FromMinutes(Math.Max(1, v.MinutosDeJanela - Atualizacao.MinimoParaTrocar)));
                var baixa = await Atualizacao.BaixarAsync(Http, m, ct: cts.Token).ConfigureAwait(false);
                if (!baixa.Ok)
                {
                    Auditar("atualizacao_sozinha_download", baixa.Erro);
                    return;
                }
                pronto = baixa.Caminho;
            }

            // ── SEGUNDA RODADA. Pergunta de novo ao painel: re-ancora o relógio (a
            //    janela pode ter virado), reconfere que a versão ainda é essa e que o
            //    dono não cancelou a onda no painel enquanto os 265 MB desciam — com 40
            //    lojas, cancelar a onda no meio é o caso NORMAL, não o excepcional.
            var estado2 = EstadoSemTela();
            var leitura2 = await ConsultarAsync(estado2, DesvioAgora()).ConfigureAwait(false);
            _relogio = Atualizacao.RelogioDaLoja.Ancorar(leitura2.Ok?.AgoraNaLoja) ?? _relogio;

            var v2 = Atualizacao.DecidirSozinho(
                estado2, Atualizacao.VersaoInstalada(), leitura2.Ok, _relogio?.HoraDaLoja);
            if (!v2.Pode || v2.Manifesto?.Versao != m.Versao)
            {
                // O arquivo continua guardado: quando a janela abrir de novo (ou quando
                // alguém tocar no botão) a troca é imediata.
                Registrar(v2 with { Motivo = v2.Motivo + " (na segunda conferência)" });
                return;
            }
            if (!Atualizacao.CabeNaJanela(v2.MinutosDeJanela, jaBaixado: true))
            {
                Registrar(v2 with { Motivo = "a janela fechou durante o download" });
                return;
            }

            // ── O VETO DE QUEM ESTIVER LÁ.
            //
            //    Turno aberto = tem gente. Nesse caso o caixa avisa e espera 30 s antes
            //    de fechar — não porque o portão esteja em dúvida (ele já disse que é
            //    seguro), mas porque sumir da tela sem avisar é diferente de fechar
            //    depois de ter avisado, e a diferença entre as duas é a confiança do
            //    operador no sistema. Ninguém lá? A contagem termina sozinha e a troca
            //    segue — que é o caso das 5 da manhã, para o qual isto tudo existe.
            if (estado2.CaixaAberto)
            {
                // Recusou há pouco: não pergunta de novo. Sem esta espera, um turno
                // aberto dentro de uma janela de 2 h renderia a MESMA pergunta oito
                // vezes — e aviso que repete é aviso que se aprende a fechar sem ler,
                // que é exatamente o hábito que esta tela não pode criar.
                if (Environment.TickCount64 < _vetadoAte) return;
                if (!DeixarVetar(m.Versao, segundos: 30))
                {
                    _vetadoAte = Environment.TickCount64 + (long)EsperaDepoisDoVeto.TotalMilliseconds;
                    Auditar("atualizacao_sozinha_vetada", $"{Atualizacao.VersaoInstalada()} → {m.Versao}");
                    return;
                }
            }

            // ── ÚLTIMA OLHADA NO PORTÃO, e ela é local (nada de rede).
            //
            //    Entre a conferência de cima e aqui passaram até 30 s de contagem. A
            //    janela modal impede bipar item, mas não impede a maquininha responder
            //    nem o cupom entrar na fila. Custa milissegundos e fecha o último vão.
            if (Atualizacao.ImpedeSozinho(EstadoSemTela()) != Atualizacao.Impedimento.Nenhum)
            {
                Auditar("atualizacao_sozinha_barrada", "portão fechou durante o aviso de 30 s");
                return;
            }

            // ── A TROCA.
            var erro = EntregarAoInstalador(pronto!);
            if (erro is not null)
            {
                // ⚠️ O CASO DAS 5 DA MANHÃ SEM NINGUÉM NA MÁQUINA CAI AQUI. O PDV não
                // roda elevado; o instalador exige administrador; a caixa de diálogo do
                // Windows aparece na máquina vazia, ninguém responde, ela expira e volta
                // como recusa (1223). Nada é alterado — e nada se atualiza. A correção
                // de verdade está fora destes dois arquivos: ver o relatório (tarefa
                // agendada ou serviço, registrado pelo instalador, rodando como SYSTEM).
                Auditar("atualizacao_sozinha_entrega", erro);
                return;
            }

            Auditar("atualizacao_sozinha_entregue",
                $"{Atualizacao.VersaoInstalada()} → {m.Versao} ({v2.Motivo})");
            Application.Current?.Dispatcher.Invoke(() => Application.Current.Shutdown());
        }
        finally { _umaDeCadaVez.Release(); }
    }

    /// <summary>
    /// Registra POR QUE o caixa não se atualizou sozinho — e só quando o motivo MUDA.
    ///
    /// A cada 15 minutos, um caixa em dia produziria 96 linhas de auditoria por dia
    /// dizendo a mesma coisa, e uma trilha que ninguém consegue ler é uma trilha que
    /// não existe. O motivo também sobe ao painel a cada pergunta (ver
    /// <see cref="Atualizacao.CorpoDaPergunta"/>) — lá é estado atual, aqui é história.
    /// </summary>
    private static void Registrar(Atualizacao.VeredictoSozinho v)
    {
        var linha = $"{v.Autonomia}: {v.Motivo}";
        if (linha == _ultimoMotivo) return;
        _ultimoMotivo = linha;
        // "Em dia" é o estado normal de 40 caixas em 39 dias de 40: não vira linha.
        if (v.Autonomia is Atualizacao.Autonomia.EmDia) return;
        Auditar("atualizacao_sozinha_barrada", linha);
    }

    /// <summary>A instrução do painel no formato que o botão manual entende.</summary>
    private static Atualizacao.LeituraManifesto ParaOBotao(Atualizacao.LeituraInstrucao l)
        => new(l.Ok?.Manifesto, l.Erro);

    /// <summary>
    /// O erro do relógio DESTA máquina, medido contra a última resposta do painel.
    /// null enquanto ninguém tiver dito que horas são.
    ///
    /// Vai no relatório, e não no portão: a janela já não depende deste relógio (usa o
    /// horário do servidor adiantado por contador monotônico), então barrar por desvio
    /// seria barrar por um número que não é usado para nada aqui. Ele é útil ADIANTE:
    /// é a mesma diferença que faz a SEFAZ rejeitar a NFC-e da loja, e o painel
    /// consegue ordenar por ela e achar a máquina antes de a nota ser recusada.
    /// </summary>
    private static TimeSpan? DesvioAgora()
        => Atualizacao.DesvioDoRelogio(_relogio?.AgoraNaLoja, DateTimeOffset.Now);

    /// <summary>
    /// Avisa e deixa vetar, com contagem regressiva. true = pode seguir.
    ///
    /// Sem janela principal visível (ninguém na máquina) responde true na hora: a
    /// contagem existe para dar chance a quem está lá, não para atrasar quem não está.
    /// </summary>
    private static bool DeixarVetar(string versao, int segundos)
    {
        var app = Application.Current;
        if (app is null) return true;
        try
        {
            return app.Dispatcher.Invoke(() =>
            {
                var dono = app.MainWindow;
                if (dono is null || !dono.IsVisible) return true;

                var seguir = true;
                var janela = Dialogo.Base(dono, 480);
                var pilha = new StackPanel();

                pilha.Children.Add(new TextBlock
                {
                    Text = "O caixa vai se atualizar agora",
                    FontSize = 22, FontWeight = FontWeights.Bold,
                    Foreground = (Brush)app.Resources["Texto"],
                    TextWrapping = TextWrapping.Wrap,
                });
                pilha.Children.Add(new TextBlock
                {
                    Text = $"A versão {versao} já está baixada e conferida, e esta é a hora "
                         + "combinada para a troca.\n\n"
                         + "O caixa fecha e abre de novo sozinho. O turno continua aberto e o "
                         + "fechamento não muda. Quando ele voltar, entre com o seu PIN.\n\n"
                         + "Se tiver cliente chegando, toque em \"Agora não\".",
                    FontSize = 15, Foreground = (Brush)app.Resources["TextoFraco"],
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 22),
                });

                var nao = new Button
                {
                    Content = $"Agora não ({segundos}s)",
                    Style = (Style)app.Resources["BotaoBase"],
                    MinHeight = 58, FontSize = 17,
                    Background = (Brush)app.Resources["PainelAlto"],
                };
                nao.Click += (_, _) => { seguir = false; janela.Close(); };
                pilha.Children.Add(nao);

                var resta = segundos;
                var relogio = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                relogio.Tick += (_, _) =>
                {
                    resta--;
                    if (resta <= 0) { relogio.Stop(); janela.Close(); }
                    else nao.Content = $"Agora não ({resta}s)";
                };

                janela.Content = Dialogo.Moldura(pilha);
                janela.KeyDown += (_, e) =>
                {
                    // Escape = "agora não". O contrário (Enter = pode) não existe de
                    // propósito: quem só quer tirar a janela da frente não deve conseguir
                    // FECHAR O CAIXA com a mesma tecla que usa o dia inteiro.
                    if (e.Key == System.Windows.Input.Key.Escape) { seguir = false; janela.Close(); }
                };
                janela.Loaded += (_, _) => relogio.Start();
                janela.Closed += (_, _) => relogio.Stop();
                janela.ShowDialog();
                return seguir;
            });
        }
        catch { return true; }   // sem tela para perguntar: não é motivo para não atualizar
    }
}
