using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Pdv.Nucleo;

// TEF PayGo Windows por TROCA DE ARQUIVOS (docs/TEF_PAYGO_protocolo.md).
//
// Não é DLL nem HTTP: o PayGo roda em segundo plano e o PDV conversa com ele gravando
// `Req\intpos.001` e lendo `Resp\intpos.sts` (ack) + `Resp\intpos.001` (resposta). O que
// importa aqui, e que NÃO pode ser "simplificado":
//
//   1. depois do .sts NÃO HÁ TIMEOUT — o PayGo está com o cliente no pinpad;
//   2. venda aprovada é TWO-PHASE: só vale depois do CNF; sem CNF o concentrador segura
//      e o próprio PayGo cobra "confirmar ou desfazer" no próximo comando;
//   3. o CNF sai DEPOIS de a transação estar gravada em disco (tef_transacao) — é a
//      "memória não volátil" da spec; se não der para gravar, NCN;
//   4. arquivo é gravado como .tmp + rename (o PayGo não pode ler pela metade), a
//      leitura tolera antivírus segurando o arquivo, e o polling é 4×/s no máximo;
//   5. toda resposta é conferida pela IDENTIFICAÇÃO (001-000 é eco): resposta de outra
//      transação (tardia, abandonada) é tratada como órfã — desfeita se aprovada — e o
//      PDV continua esperando a SUA. Sem isso, a aprovação atrasada da venda anterior
//      vira "pago" na venda seguinte, com NSU e valor errados;
//   6. CNF/NCN sem ack (.sts) não é esquecido: fica 'cnf_sem_ack'/'ncn_sem_ack' e é
//      reenviado antes do próximo comando e no religamento. NCN esquecido é o caso que
//      faz o PayGo perguntar "Confirmar/Desfazer" ao OPERADOR — que a spec proíbe.

/// <summary>
/// Arquivo `intpos` (texto). Linhas `AAA-BBB = valor`, CRLF, ASCII 20h–7Eh. Parse e
/// serialização PUROS — é aqui que os testes batem com os arquivos verbatim da doc.
/// </summary>
public static class ArquivoIntpos
{
    public const string Finalizador = "999-999";
    public const string LinhaFinal = "999-999 = 0";

    /// <summary>
    /// Monta o texto do arquivo. `999-999 = 0` é sempre a última linha (acrescentado se faltar);
    /// acento e qualquer byte fora de 20h–7Eh são removidos — o PayGo rejeita o arquivo inteiro.
    /// Campo com valor VAZIO é omitido (`738-000 = ` sem nada é "inconsistência" para o PayGo).
    /// </summary>
    public static string Serializar(IEnumerable<KeyValuePair<string, string>> campos)
    {
        var sb = new StringBuilder();
        foreach (var kv in campos)
        {
            if (kv.Key == Finalizador) continue;
            var v = Ascii(kv.Value);
            if (v.Length == 0) continue;
            sb.Append(kv.Key).Append(" = ").Append(v).Append("\r\n");
        }
        sb.Append(LinhaFinal).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>O arquivo chegou inteiro? (última linha = `999-999 = 0`). Pela metade = o PayGo ainda está gravando.</summary>
    public static bool Completo(string? texto)
        => texto is not null && texto.TrimEnd('\r', '\n', ' ').EndsWith(LinhaFinal, StringComparison.Ordinal);

    /// <summary>
    /// Lê o arquivo. Tolerante de propósito: linha sem ` = ` é ignorada, campo repetido fica com
    /// a PRIMEIRA ocorrência, campo desconhecido não é erro (compat com versões futuras).
    /// Linhas de comprovante vêm entre aspas — as aspas saem, o conteúdo (com espaços) fica.
    /// </summary>
    public static Dictionary<string, string> Analisar(string? texto)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(texto)) return d;
        foreach (var bruta in texto.Split('\n'))
        {
            var linha = bruta.TrimEnd('\r');
            var i = linha.IndexOf(" = ", StringComparison.Ordinal);
            if (i < 7) continue;                        // "AAA-BBB" tem 7 chars
            var chave = linha[..i].Trim();
            if (chave.Length != 7 || chave[3] != '-') continue;
            var valor = linha[(i + 3)..];
            if (valor.Length >= 2 && valor[0] == '"' && valor[^1] == '"') valor = valor[1..^1];
            d.TryAdd(chave, valor);
        }
        return d;
    }

    /// <summary>Linhas de um comprovante: `713-001`, `713-002`… em ordem. `713-000` é o contador e fica de fora.</summary>
    public static IReadOnlyList<string> Linhas(IReadOnlyDictionary<string, string> campos, string prefixo)
        => campos.Where(kv => kv.Key.StartsWith(prefixo + "-", StringComparison.Ordinal) && !kv.Key.EndsWith("-000", StringComparison.Ordinal))
                 .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                 .Select(kv => kv.Value)
                 .ToList();

    /// <summary>Só ASCII imprimível. "Ã" vira "A", "ç" vira "c"; o que não tem equivalente some.</summary>
    public static string Ascii(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var norm = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (var ch in norm)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (ch >= 0x20 && ch <= 0x7E) sb.Append(ch);
        }
        return sb.ToString();
    }
}

/// <summary>Resposta do PayGo (`Resp\intpos.001`) já interpretada. Os campos crus ficam em <see cref="Campos"/>.</summary>
public sealed class RespostaPayGo
{
    public IReadOnlyDictionary<string, string> Campos { get; }

    /// <summary>O arquivo cru, como veio — vai para `tef_transacao.resposta_txt` (auditoria e religamento).</summary>
    public string Texto { get; }

    private RespostaPayGo(string texto) { Texto = texto; Campos = ArquivoIntpos.Analisar(texto); }

    public static RespostaPayGo Analisar(string? texto) => new(texto ?? "");

    public string? Comando => Campo("000-000");
    public string? Identificacao => Campo("001-000");
    public int? Status => Inteiro("009-000");
    public bool Aprovada => Status == 0;
    public string Mensagem => Campo("030-000") ?? (Aprovada ? "TRANSACAO AUTORIZADA" : "transacao nao autorizada");

    /// <summary>Negada porque o cliente/operador cancelou no pinpad ou no PayGo (030 "OPERACAO CANCELADA") — não é recusa do cartão.</summary>
    public bool Cancelada => !Aprovada && Mensagem.ToUpperInvariant().Contains("CANCELAD");

    public string? CodigoControle => Campo("027-000");
    public string? Nsu => Campo("012-000");
    public string? Autorizacao => Campo("013-000");
    public string? Rede => Campo("010-000");
    public string? NomeCartao => Campo("040-000");
    public string? Produto => Campo("748-000");
    public long? ValorCent => long.TryParse(Campo("003-000"), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    public int? Parcelas => Inteiro("018-000");
    public int? TipoCartao => Inteiro("731-000");
    public int? Financiamento => Inteiro("732-000");
    public string? Terminal => Campo("718-000");
    public string? Estabelecimento => Campo("719-000");
    public string? CartaoMascarado => Campo("740-000");
    public string? Data => Campo("022-000");
    public string? Hora => Campo("023-000");
    public int? Vias => Inteiro("737-000");

    /// <summary>
    /// `729-000`: 2 = requer CNF/NCN; 1 = não requer. Ausente mas com comprovante = requer
    /// (compat com PayGo antigo, regra da própria spec).
    /// </summary>
    public bool RequerConfirmacao
    {
        get
        {
            var c = Inteiro("729-000");
            if (c is not null) return c == 2;
            return Aprovada && (ViaCliente.Count > 0 || ViaUnica.Count > 0 || ViaEstabelecimento.Count > 0);
        }
    }

    public IReadOnlyList<string> CupomReduzido => ArquivoIntpos.Linhas(Campos, "711");
    public IReadOnlyList<string> ViaCliente => ArquivoIntpos.Linhas(Campos, "713");
    public IReadOnlyList<string> ViaEstabelecimento => ArquivoIntpos.Linhas(Campos, "715");
    public IReadOnlyList<string> ViaUnica => ArquivoIntpos.Linhas(Campos, "029");

    /// <summary>Veio algum comprovante? Sem vias não há o que imprimir — e o CNF não depende de impressora.</summary>
    public bool TemVias => ViaCliente.Count > 0 || ViaEstabelecimento.Count > 0 || CupomReduzido.Count > 0 || ViaUnica.Count > 0;

    /// <summary>As vias num JSON só, para `tef_transacao.vias_json` (reimpressão e auditoria).</summary>
    public string ViasJson() => JsonSerializer.Serialize(new
    {
        reduzido = CupomReduzido, cliente = ViaCliente, estabelecimento = ViaEstabelecimento, unica = ViaUnica,
    });

    private string? Campo(string k) => Campos.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
    private int? Inteiro(string k) => int.TryParse(Campo(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}

/// <summary>
/// O que o PDV precisa GUARDAR de uma transação PayGo — é a linha de `tef_transacao`.
/// `Situacao`: aguardando → aprovada → pago | cnf_sem_ack | desfeita | ncn_sem_ack | recusado | orfa |
/// estornado (CNC) | estornada (venda original de um CNC).
/// </summary>
public sealed record TransacaoPayGo(string ChargeId, string Identificacao, TipoTef Tipo, long ValorCent,
    int Parcelas, string Situacao, RespostaPayGo? Resposta, string? Motivo = null)
{
    public string? CodigoControle => Resposta?.CodigoControle;

    /// <summary>É um cancelamento (CNC)? Decide o estado final: `estornado`, nunca `pago`.</summary>
    public bool EhCancelamento => Resposta?.Comando == "CNC" || ChargeId.StartsWith("paygo-cnc-", StringComparison.Ordinal);

    /// <summary>
    /// Estado final quando o CNF é acusado. Só VENDA vira `pago` — é `pago` que o fechamento
    /// de caixa soma (Caixa.DivergenciasTef); estorno, pendência devolvida pela rede e
    /// administrativa têm estado próprio para não virar "venda fantasma".
    /// </summary>
    public string AposConfirmar => EhCancelamento ? "estornado"
        : ChargeId.StartsWith("paygo-pend-", StringComparison.Ordinal) ? "confirmada"
        : ChargeId.StartsWith("paygo-adm-", StringComparison.Ordinal) ? "adm"
        : "pago";
}

/// <summary>Identidade da automação nos arquivos (campos 716/733/735/736/738) e as capacidades (706).</summary>
/// <param name="RedeCartao">Rede pré-selecionada para cartão (010-000, ex.: `C6PAY`). Null = o PayGo mostra o menu de redes.</param>
/// <param name="RedePix">Rede pré-selecionada para Pix (010-000, ex.: `PIX C6 BANK`). Null = menu.</param>
/// <param name="VersaoInterface">733-000. A doc é "2.25" mas todos os exemplos oficiais mandam `210`; se o sandbox acusar, tentar `225`.</param>
public sealed record OpcoesPayGo(string Empresa, string NomeAutomacao, string VersaoAutomacao, string Registro,
    int Capacidades = ClientePayGo.CapacidadesPadrao, string VersaoInterface = "210",
    string? RedeCartao = null, string? RedePix = null);

/// <summary>
/// Cliente do PayGo Windows por troca de arquivos. Um por processo; as chamadas são
/// serializadas por um semáforo porque a pasta é UMA (dois comandos ao mesmo tempo
/// sobrescrevem o `intpos.001` um do outro).
/// </summary>
public sealed class ClientePayGo : IProvedorTefOperavel
{
    public string Nome => "paygo";

    public const string PastaPadrao = @"C:\PAYGO";

    /// <summary>4 (fixo) + 8 (vias diferenciadas) + 16 (cupom reduzido) + 128 (NSU 40 chars, exigido p/ cancelar Pix).</summary>
    public const int CapacidadesPadrao = 4 + 8 + 16 + 128;

    /// <summary>Espera pelo ack (.sts). A spec manda 7 s; sem ele o PayGo está inativo.</summary>
    public int TempoStsMs { get; init; } = 7_000;

    /// <summary>Cadência do polling de arquivo. A spec manda NO MÁXIMO 4×/s (250 ms).</summary>
    public int IntervaloPollMs { get; init; } = 250;

    /// <summary>
    /// Depois do .sts não há timeout (spec). A ÚNICA exceção é quando o operador já pediu para
    /// cancelar e o PayGo não respondeu por este tempo: o PDV desiste, grava ÓRFÃ e avisa; se a
    /// resposta chegar tarde, é reconhecida pela identificação e desfeita (NCN).
    /// </summary>
    public int TempoDesistirAposCancelarMs { get; init; } = 90_000;

    /// <summary>
    /// Grava a transação em disco. É chamado ANTES do CNF — se devolver false (ou lançar), a
    /// transação é DESFEITA (NCN): dinheiro sem registro é o pior desfecho possível.
    /// </summary>
    public Func<TransacaoPayGo, bool> Guardar { get; init; } = _ => true;

    /// <summary>CNPJ da credenciadora pelo nome da rede (010-000) — vai no &lt;card&gt; da NFC-e. Null = tpIntegra=2.</summary>
    public Func<string, string?>? CnpjDaRede { get; init; }

    /// <summary>
    /// "Transação pendente" (roteiro P31–34): a venda seguinte volta NEGADA trazendo o código de
    /// controle de uma transação que a rede ainda segura. Se ESTE caixa já a confirmou (está em
    /// `tef_transacao` como paga) → CNF; se não a conhece → NCN. Null = nunca conhece (sempre NCN).
    /// </summary>
    public Func<string, bool>? ConhecidaConfirmada { get; init; }

    /// <summary>Trilha: CNF/NCN enviados e acusados, órfãs desfeitas, respostas de outra transação ignoradas. A PayGo pede log.</summary>
    public Action<string>? Auditar { get; init; }

    /// <summary>
    /// A identificação (001) foi emitida por NÓS em outro processo (está em `tef_transacao`,
    /// como `identificacao` ou embutida no `charge_id`)? Complementa a lista em memória.
    /// Regra: eco ≠ id atual E é um 001 que nós emitimos ⇒ alheia (tardia/abandonada: desfeita
    /// se aprovada, ou .sts ignorado); 001 desconhecido ⇒ aceito como nosso — um PayGo que não
    /// ecoe o 001 não pode derrubar a loja na primeira venda — com alarme na auditoria.
    /// </summary>
    public Func<string, bool>? EhNossaIdentificacao { get; init; }

    /// <summary>
    /// Imprime o comprovante (vias 713/715 — ou 711/029) ANTES do CNF e diz se SAIU. É a regra
    /// da spec: o comprovante impresso é o gatilho do commit; não saiu e o operador desistiu →
    /// NCN, com a mensagem literal "Transação TEF cancelada: Rede/NSU/Valor" (o cliente NÃO é
    /// cobrado). Null = este terminal não imprime comprovante de TEF (nada pode falhar → CNF
    /// direto). Só é chamado quando a resposta pede confirmação (729 = 2) e traz vias; com
    /// 729 = 1 a rede já efetivou — imprime em melhor-esforço, sem NCN. Quem implementa decide
    /// quantas vezes tenta (é ele quem fala com a impressora e com o operador); exceção = false.
    /// Roda dentro do semáforo da pasta: enquanto imprime, nenhum outro comando TEF sai.
    /// </summary>
    public Func<TransacaoPayGo, Task<bool>>? ImprimirComprovante { get; init; }

    /// <summary>Mensagem literal da spec (Mensagens de erro → falha na impressão) quando a transação foi desfeita por falta do comprovante.</summary>
    public static string MsgCancelada(string? rede, string? nsu, long? valorCent)
        => $"Transação TEF cancelada: Rede: {rede ?? "-"} NSU: {nsu ?? "-"} Valor: {new Dinheiro(valorCent ?? 0).Formatado()}";

    /// <summary>Hook "seguro": sem delegate ou sem vias = nada a imprimir (true); exceção = não imprimiu (false).</summary>
    private async Task<bool> ImprimirSeguroAsync(TransacaoPayGo tx)
    {
        if (ImprimirComprovante is null || tx.Resposta is null || !tx.Resposta.TemVias) return true;
        try { return await ImprimirComprovante(tx).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Auditar?.Invoke($"paygo: impressão do comprovante {tx.ChargeId} lançou: {ex.Message}");
            return false;
        }
    }

    /// <summary>Todo 001 gerado neste processo (CRT/CNC/ADM/ATV/CNF/NCN/pendência).</summary>
    private readonly HashSet<string> _emitidas = new(StringComparer.Ordinal);

    private bool NossaIdentificacao(string id)
    {
        if (_emitidas.Contains(id)) return true;
        try { return EhNossaIdentificacao?.Invoke(id) == true; } catch { return false; }
    }

    /// <summary>Identificação nova JÁ registrada como nossa (para reconhecer eco tardio dela).</summary>
    private string NovaId()
    {
        var id = NovaIdentificacao();
        _emitidas.Add(id);
        return id;
    }

    public const string MsgTefNaoResponde = "TEF não responde — abra o PayGo Windows e tente de novo";

    /// <summary>Mensagem padronizada da spec quando a resposta não tem o campo esperado.</summary>
    public static string MsgInconsistencia(string campo, string arquivo = "intpos.001")
        => $"Inconsistência no campo {campo} do arquivo {arquivo} gerado pelo TEF";

    private readonly string _req, _resp;
    private readonly OpcoesPayGo _op;
    private readonly SemaphoreSlim _um = new(1, 1);
    private static long _ultimaIdentificacao;

    /// <summary>CNF/NCN que o PayGo não acusou neste processo: reenviados antes do próximo comando (o boot cobre os de antes do restart).</summary>
    private readonly List<(TransacaoPayGo Tx, string Cmd, string Depois)> _reenvios = new();

    public ClientePayGo(string pasta, OpcoesPayGo opcoes)
    {
        _req = Path.Combine(pasta, "Req");
        _resp = Path.Combine(pasta, "Resp");
        _op = opcoes ?? throw new ArgumentNullException(nameof(opcoes));
    }

    public string PastaReq => _req;
    public string PastaResp => _resp;

    /// <summary>Há comando TEF em voo nesta instância (CRT/CNC/ADM/ATV/CNF/NCN)? Quem troca a instância precisa olhar isto antes.</summary>
    public bool Ocupado => _um.CurrentCount == 0;

    public string Descricao => "PayGo Windows (troca de arquivos) em " + Path.GetDirectoryName(_req);

    // ------------------------------------------------------------------ ATV

    /// <summary>O PayGo está de pé? (apaga Resp, grava ATV, espera o .sts até <see cref="TempoStsMs"/>.)</summary>
    public async Task<bool> AtivoAsync(CancellationToken ct)
    {
        await _um.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AntesDoComandoAsync().ConfigureAwait(false);
            var id = NovaId();
            Gravar(Campos("ATV", id));
            var ok = await EsperarStsAsync(id).ConfigureAwait(false);
            LimparResp();
            if (!ok) ApagarReq();
            return ok;
        }
        finally { _um.Release(); }
    }

    // ------------------------------------------------------------------ CRT

    public async Task<DesfechoTef> CobrarAsync(TipoTef tipo, Dinheiro valor, string? documento, int parcelas,
        IProgress<AndamentoTef>? andamento, CancellationToken ct)
    {
        var id = NovaId();
        var chargeId = "paygo-" + id;
        if (!valor.Positivo)
            return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "valor da cobrança tem que ser maior que zero");

        var parc = tipo == TipoTef.Credito ? Math.Max(1, parcelas) : 1;
        andamento?.Report(new AndamentoTef(FaseTef.Criando, chargeId, null, "Enviando a cobrança para o TEF…"));

        // Entrar na fila ANTES de tocar na pasta; o token do operador vale só até aqui.
        try { await _um.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Falha(SituacaoTef.Cancelado, chargeId, CodigoTef.Cancelado, "cobrança cancelada pelo operador"); }

        try
        {
            await AntesDoComandoAsync().ConfigureAwait(false);

            var campos = Campos("CRT", id);
            campos["003-000"] = valor.Centavos.ToString(CultureInfo.InvariantCulture);
            campos["004-000"] = "0";
            campos["706-000"] = _op.Capacidades.ToString(CultureInfo.InvariantCulture);
            campos["716-000"] = _op.Empresa;
            switch (tipo)
            {
                case TipoTef.Credito:
                    campos["749-000"] = "1";                          // 1 = cartão
                    campos["731-000"] = "1";
                    campos["732-000"] = parc > 1 ? "3" : "1";     // 3 = parcelado estabelecimento
                    if (parc > 1) campos["018-000"] = parc.ToString(CultureInfo.InvariantCulture);
                    break;
                case TipoTef.Debito:
                    campos["749-000"] = "1";
                    campos["731-000"] = "2";
                    campos["732-000"] = "1";
                    break;
                default:
                    // Pix = carteira digital (749=8) com QR dinâmico (750=4). O QR aparece no
                    // pinpad (ou na tela do PayGo) — o PDV não desenha nada. Conferir no sandbox.
                    campos["731-000"] = "0";
                    campos["749-000"] = "8";
                    campos["750-000"] = "4";
                    break;
            }
            // Rede pré-selecionada (roteiro P3/P11): sem ela o PayGo abre o menu de redes.
            var rede = tipo == TipoTef.Pix ? _op.RedePix : _op.RedeCartao;
            if (!string.IsNullOrWhiteSpace(rede)) campos["010-000"] = rede!;

            Gravar(campos);

            if (!await EsperarStsAsync(id).ConfigureAwait(false))
            {
                // Sem ack = PayGo inativo. Nada foi armado: limpar o Req (senão ele é processado
                // quando alguém abrir o PayGo, com o operador já em outra venda). Se o PayGo pegou
                // o Req no último instante, a resposta chega com ESTA identificação e é tratada
                // como órfã (desfeita) pela conferência de identidade.
                ApagarReq();
                return Falha(SituacaoTef.Erro, chargeId, CodigoTef.TefNaoResponde, MsgTefNaoResponde);
            }

            Guardar(new TransacaoPayGo(chargeId, id, tipo, valor.Centavos, parc, "aguardando", null));
            andamento?.Report(new AndamentoTef(FaseTef.Aguardando, chargeId, id,
                tipo == TipoTef.Pix ? "Peça ao cliente para ler o QR no pinpad…" : "Aproxime, insira ou passe o cartão no pinpad…"));

            var texto = await EsperarRespostaAsync(chargeId, id, andamento, ct).ConfigureAwait(false);
            if (texto is null)
            {
                // Operador cancelou e o PayGo ficou mudo: órfã (a resposta tardia é reconhecida
                // pela identificação e desfeita). O aviso da maquininha aqui é INFORMAÇÃO.
                Guardar(new TransacaoPayGo(chargeId, id, tipo, valor.Centavos, parc, "orfa", null, "PayGo não respondeu após o cancelamento"));
                return new DesfechoTef(SituacaoTef.Cancelado, id, chargeId, null,
                    "cobrança cancelada pelo operador — confira no PayGo se o cliente concluiu", true)
                { Codigo = CodigoTef.Cancelado };
            }

            var r = RespostaPayGo.Analisar(texto);

            // A resposta (com o 027) é PERSISTIDA ANTES de o arquivo ser apagado: crash entre o
            // apagar e o gravar deixaria a transação pendente no concentrador sem o PDV ter o
            // 027 em lugar nenhum — é exatamente o P51 ("recebeu o resultado, desligou antes de
            // confirmar"). Com a linha 'aprovada' em disco, o boot resolve sozinho.
            TransacaoPayGo? tx = null;
            var gravou = false;
            if (r.Aprovada)
            {
                tx = new TransacaoPayGo(chargeId, id, tipo, valor.Centavos, parc, "aprovada", r);
                gravou = GuardarSeguro(tx);
            }
            LimparResp();

            if (r.Status is null)
            {
                // Sem 009-000 não há como saber o que aconteceu. A spec tem a mensagem pronta;
                // não confirma nem desfaz (não há o que) — o operador confere no PayGo.
                var msg = MsgInconsistencia("009-000");
                Guardar(new TransacaoPayGo(chargeId, id, tipo, valor.Centavos, parc, "erro", r, msg));
                return new DesfechoTef(SituacaoTef.Erro, id, chargeId, null, msg, true) { Codigo = CodigoTef.Plataforma };
            }

            if (!r.Aprovada && !string.IsNullOrWhiteSpace(r.CodigoControle))
            {
                // "Transação pendente" (roteiro P31–34): a rede segura uma transação anterior e
                // devolve esta venda NEGADA com os dados dela (027). A automação resolve a
                // pendência — CNF se este caixa já a confirmou, NCN se não a reconhece — sem
                // imprimir nada, e a venda ATUAL conta como não realizada (cobrar de novo).
                // Identidade da pendente = 027 (+ 010 rede); o 001 da resposta é só eco da venda
                // atual, então o CNF/NCN vai com identificação NOVA e o 027/010 recebidos.
                var ctrl = r.CodigoControle!;
                var conhecida = ConhecidaSegura(ctrl);
                var pend = new TransacaoPayGo("paygo-pend-" + ctrl, NovaId(), tipo, r.ValorCent ?? 0, 1,
                    "aprovada", r, "pendência devolvida pela rede");
                // 'confirmada', não 'pago': a venda original já tem a linha 'pago' dela; esta é só
                // o registro da confirmação (o 003 aqui é o da venda ATUAL, negada).
                if (conhecida) await ConfirmarAsync(pend, "confirmada").ConfigureAwait(false);
                else await DesfazerAsync(pend, "desfeita").ConfigureAwait(false);
                var motivo = r.Mensagem + (conhecida
                    ? " — pendência anterior confirmada; cobre de novo"
                    : " — pendência anterior desfeita; cobre de novo");
                Guardar(new TransacaoPayGo(chargeId, id, tipo, valor.Centavos, parc, "recusado", r, motivo));
                return new DesfechoTef(SituacaoTef.Erro, id, chargeId, null, motivo, false) { Codigo = CodigoTef.Pendencia, Desfeita = true };
            }

            if (!r.Aprovada)
            {
                Guardar(new TransacaoPayGo(chargeId, id, tipo, valor.Centavos, parc, r.Cancelada ? "cancelado" : "recusado", r, r.Mensagem));
                // "OPERACAO CANCELADA" (Esc no pinpad/QR) não é recusa do cartão: é cancelamento.
                return r.Cancelada
                    ? new DesfechoTef(SituacaoTef.Cancelado, id, chargeId, null, r.Mensagem, false) { Codigo = CodigoTef.Cancelado }
                    : new DesfechoTef(SituacaoTef.Recusado, id, chargeId, null, r.Mensagem, false) { Codigo = CodigoTef.Recusado };
            }

            tx ??= new TransacaoPayGo(chargeId, id, tipo, valor.Centavos, parc, "aprovada", r);

            // Aprovada. Daqui até o CNF qualquer desistência é NCN — e o cliente NÃO é cobrado.
            if (ct.IsCancellationRequested)
            {
                await DesfazerAsync(tx with { Motivo = "cancelada pelo operador (NCN)" }, "desfeita").ConfigureAwait(false);
                return new DesfechoTef(SituacaoTef.Cancelado, id, chargeId, null, "cobrança cancelada pelo operador — transação desfeita", false)
                { Codigo = CodigoTef.Cancelado, Desfeita = true };
            }

            if (r.ValorCent is { } cobrado && cobrado != valor.Centavos)
            {
                // Não declaramos troco/desconto/valor devido em 706: valor diferente é anomalia.
                // Desfazer é o único caminho que não deixa dinheiro sem venda.
                await DesfazerAsync(tx with { Motivo = "valor divergente (NCN)" }, "desfeita").ConfigureAwait(false);
                return new DesfechoTef(SituacaoTef.Erro, id, chargeId, Cartao(r),
                    $"o TEF aprovou {new Dinheiro(cobrado).Formatado()} e a venda é de {valor.Formatado()} — transação desfeita, cobre de novo", false)
                { Codigo = CodigoTef.ValorDivergente, Desfeita = true };
            }

            var cartao = Cartao(r);

            // MEMÓRIA NÃO VOLÁTIL ANTES DO CNF (gravou lá em cima, antes de apagar o arquivo).
            // Se não deu para gravar, desfaz: a alternativa é um cliente cobrado e um PDV que não sabe.
            if (!gravou)
            {
                await DesfazerAsync(tx with { Motivo = "não gravou no caixa (NCN)" }, "desfeita").ConfigureAwait(false);
                return new DesfechoTef(SituacaoTef.Erro, id, chargeId, cartao,
                    "não consegui gravar a transação no caixa — transação desfeita, cobre de novo", false)
                { Codigo = CodigoTef.Plataforma, Desfeita = true };
            }

            var situacao = "pago";
            if (r.RequerConfirmacao)
            {
                // COMPROVANTE ANTES DO CNF (spec: a impressão decide o commit). Não saiu e o
                // operador desistiu → NCN: o cliente NÃO é cobrado, e a mensagem é a literal da spec.
                if (!await ImprimirSeguroAsync(tx).ConfigureAwait(false))
                {
                    await DesfazerAsync(tx with { Motivo = "comprovante não impresso (NCN)" }, "desfeita").ConfigureAwait(false);
                    Auditar?.Invoke($"paygo: {chargeId} desfeita — comprovante não saiu");
                    return new DesfechoTef(SituacaoTef.Cancelado, id, chargeId, cartao,
                        MsgCancelada(r.Rede, r.Nsu, r.ValorCent ?? valor.Centavos), false)
                    { Codigo = CodigoTef.Cancelado, Desfeita = true };
                }
                // Sem ack o PayGo pode estar segurando a confirmação: fica 'cnf_sem_ack' e o
                // próximo comando / o boot reenvia. Para o caixa, está pago: a aprovação veio e
                // a decisão de confirmar já está em disco.
                situacao = await ConfirmarAsync(tx, "pago").ConfigureAwait(false) ? "pago" : "cnf_sem_ack";
            }
            else
            {
                Guardar(tx with { Situacao = "pago" });
                // 729 = 1: a rede já efetivou; o papel é melhor-esforço (não existe NCN aqui).
                if (!await ImprimirSeguroAsync(tx).ConfigureAwait(false))
                    Auditar?.Invoke($"paygo: {chargeId} paga (729=1) com comprovante não impresso — reimprimir pelas vias");
            }

            return new DesfechoTef(SituacaoTef.Pago, id, chargeId, cartao, null, false)
            { Codigo = CodigoTef.Pago, PaymentStatus = situacao };
        }
        finally { _um.Release(); }
    }

    // ------------------------------------------------------------------ CNC (estorno)

    /// <summary>
    /// Cancela (estorna) uma venda já confirmada. O PayGo pede a senha lojista e, dependendo da
    /// rede, o cartão de novo. Two-phase como a venda: aprovou → grava → CNF. A linha do CNC
    /// termina `estornado` (nunca `pago` — senão o fechamento soma o estorno como venda) e a
    /// venda original vira `estornada`.
    /// </summary>
    public async Task<DesfechoTef> CancelarAsync(TransacaoPayGo original, CancellationToken ct)
    {
        var r0 = original.Resposta;
        var id = NovaId();
        var chargeId = "paygo-cnc-" + id;
        if (r0 is null || string.IsNullOrWhiteSpace(r0.Nsu))
            return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "transação original sem NSU — cancele pelo menu do PayGo");

        await _um.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AntesDoComandoAsync().ConfigureAwait(false);
            var c = Campos("CNC", id);
            c["003-000"] = original.ValorCent.ToString(CultureInfo.InvariantCulture);
            c["004-000"] = "0";
            c["012-000"] = r0.Nsu!;
            if (r0.Autorizacao is not null) c["013-000"] = r0.Autorizacao;
            if (r0.Data is not null) c["022-000"] = r0.Data;
            if (r0.Hora is not null) c["023-000"] = r0.Hora;
            if (r0.CodigoControle is not null) c["027-000"] = r0.CodigoControle;
            if (r0.Rede is not null) c["010-000"] = r0.Rede;
            c["706-000"] = _op.Capacidades.ToString(CultureInfo.InvariantCulture);
            c["716-000"] = _op.Empresa;
            Gravar(c);

            if (!await EsperarStsAsync(id).ConfigureAwait(false))
            {
                ApagarReq();
                return Falha(SituacaoTef.Erro, chargeId, CodigoTef.TefNaoResponde, MsgTefNaoResponde);
            }
            var texto = await EsperarRespostaAsync(chargeId, id, null, CancellationToken.None).ConfigureAwait(false);
            var r = RespostaPayGo.Analisar(texto);
            // persistir antes de apagar o arquivo (mesma razão do CRT: P51)
            var tx = new TransacaoPayGo(chargeId, id, original.Tipo, original.ValorCent, original.Parcelas, "aprovada", r, "cancelamento de " + original.ChargeId);
            var gravou = r.Aprovada && GuardarSeguro(tx);
            LimparResp();
            if (r.Status is null)
                return new DesfechoTef(SituacaoTef.Erro, id, chargeId, null, MsgInconsistencia("009-000"), true) { Codigo = CodigoTef.Plataforma };
            if (!r.Aprovada)
                return new DesfechoTef(r.Cancelada ? SituacaoTef.Cancelado : SituacaoTef.Recusado, id, chargeId, null, r.Mensagem, false)
                { Codigo = r.Cancelada ? CodigoTef.Cancelado : CodigoTef.Recusado };

            if (!gravou)
            {
                await DesfazerAsync(tx with { Motivo = "não gravou o cancelamento (NCN)" }, "desfeita").ConfigureAwait(false);
                return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "não consegui gravar o cancelamento — desfeito");
            }
            var sit = "estornado";
            if (r.RequerConfirmacao)
            {
                // comprovante do cancelamento antes do CNF — mesma regra da venda
                if (!await ImprimirSeguroAsync(tx).ConfigureAwait(false))
                {
                    await DesfazerAsync(tx with { Motivo = "comprovante do cancelamento não impresso (NCN)" }, "desfeita").ConfigureAwait(false);
                    return new DesfechoTef(SituacaoTef.Cancelado, id, chargeId, Cartao(r),
                        MsgCancelada(r.Rede, r.Nsu, r.ValorCent ?? original.ValorCent), false)
                    { Codigo = CodigoTef.Cancelado, Desfeita = true };
                }
                sit = await ConfirmarAsync(tx, "estornado").ConfigureAwait(false) ? "estornado" : "cnf_sem_ack";
            }
            else
            {
                Guardar(tx with { Situacao = "estornado" });
                await ImprimirSeguroAsync(tx).ConfigureAwait(false);   // melhor-esforço (729=1)
            }
            Guardar(original with { Situacao = "estornada", Motivo = "estornada por " + chargeId });
            return new DesfechoTef(SituacaoTef.Pago, id, chargeId, Cartao(r), null, false) { Codigo = CodigoTef.Pago, PaymentStatus = sit };
        }
        finally { _um.Release(); }
    }

    // ------------------------------------------------------------------ ADM

    /// <summary>
    /// Menu administrativo do PayGo (roteiro P12–16/23: teste de comunicação, reimpressão,
    /// cancelamento pelo menu…). Mesma mecânica da venda, sem valor; two-phase se a operação
    /// escolhida devolver comprovante com 729=2. `Pago` aqui significa "concluída".
    /// </summary>
    public async Task<DesfechoTef> AdministrativaAsync(CancellationToken ct)
    {
        var id = NovaId();
        var chargeId = "paygo-adm-" + id;
        await _um.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AntesDoComandoAsync().ConfigureAwait(false);
            var c = Campos("ADM", id);
            c["706-000"] = _op.Capacidades.ToString(CultureInfo.InvariantCulture);
            c["716-000"] = _op.Empresa;
            Gravar(c);
            if (!await EsperarStsAsync(id).ConfigureAwait(false))
            {
                ApagarReq();
                return Falha(SituacaoTef.Erro, chargeId, CodigoTef.TefNaoResponde, MsgTefNaoResponde);
            }
            var texto = await EsperarRespostaAsync(chargeId, id, null, CancellationToken.None).ConfigureAwait(false);
            var r = RespostaPayGo.Analisar(texto);
            // persistir antes de apagar o arquivo (P51), só quando há o que confirmar
            var tx = new TransacaoPayGo(chargeId, id, TipoTef.Credito, r.ValorCent ?? 0, 1, "aprovada", r, "administrativa");
            var gravou = r.Aprovada && r.RequerConfirmacao && GuardarSeguro(tx);
            LimparResp();
            if (r.Status is null)
                return new DesfechoTef(SituacaoTef.Erro, id, chargeId, null, MsgInconsistencia("009-000"), true) { Codigo = CodigoTef.Plataforma };
            if (!r.Aprovada)
                return new DesfechoTef(r.Cancelada ? SituacaoTef.Cancelado : SituacaoTef.Recusado, id, chargeId, null, r.Mensagem, false)
                { Codigo = r.Cancelada ? CodigoTef.Cancelado : CodigoTef.Recusado };

            // 'adm', nunca 'pago': o 003 de uma administrativa (ex.: cancelamento pelo menu) não é venda.
            var sit = "adm";
            if (r.RequerConfirmacao)
            {
                if (!gravou)
                {
                    await DesfazerAsync(tx with { Motivo = "não gravou (NCN)" }, "desfeita").ConfigureAwait(false);
                    return Falha(SituacaoTef.Erro, chargeId, CodigoTef.Plataforma, "não consegui gravar a operação — desfeita");
                }
                if (!await ImprimirSeguroAsync(tx).ConfigureAwait(false))
                {
                    await DesfazerAsync(tx with { Motivo = "comprovante não impresso (NCN)" }, "desfeita").ConfigureAwait(false);
                    return new DesfechoTef(SituacaoTef.Cancelado, id, chargeId, null,
                        MsgCancelada(r.Rede, r.Nsu, r.ValorCent), false)
                    { Codigo = CodigoTef.Cancelado, Desfeita = true };
                }
                sit = await ConfirmarAsync(tx, "adm").ConfigureAwait(false) ? "adm" : "cnf_sem_ack";
            }
            else
                await ImprimirSeguroAsync(tx).ConfigureAwait(false);   // relatório/consulta sem confirmação: só papel
            return new DesfechoTef(SituacaoTef.Pago, id, chargeId, null, r.Mensagem, false) { Codigo = CodigoTef.Pago, PaymentStatus = sit };
        }
        finally { _um.Release(); }
    }

    // ------------------------------------------------------------------ pendências

    /// <summary>
    /// Varredura do boot: transações que ficaram entre a aprovação e o CNF/NCN (queda de energia,
    /// app fechado). Regra da spec: quem decide é o PDV, nunca o operador — venda concluída → CNF;
    /// venda que não existe → NCN; CNF/NCN sem ack → reenvia. Devolve quantas resolveu.
    /// </summary>
    public async Task<int> ResolverPendenciasAsync(IReadOnlyList<(TransacaoPayGo Tx, bool VendaConcluida)> pendentes)
    {
        var n = 0;
        await _um.WaitAsync().ConfigureAwait(false);
        try
        {
            // Queda de energia ANTES de o PayGo pegar o comando (roteiro P24/25): um
            // `Req\intpos.001` órfão seria processado quando o PayGo subisse, com o operador
            // em outra venda. No boot ninguém está cobrando — apagar é sempre certo.
            ApagarReq();
            await AntesDoComandoAsync().ConfigureAwait(false);
            var vistas = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (tx, concluida) in pendentes)
            {
                if (tx.Resposta is null || !vistas.Add(tx.ChargeId)) continue;
                if (tx.Situacao is not ("aprovada" or "cnf_sem_ack" or "ncn_sem_ack")) continue;
                if (string.IsNullOrWhiteSpace(tx.CodigoControle))
                {
                    // Sem 027 o PayGo entende CNF/NCN como "a ÚLTIMA transação" — que pode ser
                    // outra. Nunca enviar às cegas: fica órfã para estorno/conferência manual.
                    Auditar?.Invoke($"paygo: pendência {tx.ChargeId} sem 027 — não dá para CNF/NCN; marcada órfã");
                    GuardarSeguro(tx with { Situacao = "orfa", Motivo = "aprovada sem código de controle (027); confira no PayGo" });
                    n++;
                    continue;
                }
                switch (tx.Situacao)
                {
                    case "aprovada" when concluida:
                    case "cnf_sem_ack":
                        await ConfirmarAsync(tx, tx.AposConfirmar).ConfigureAwait(false);
                        n++;
                        break;
                    case "aprovada":
                        await DesfazerAsync(tx with { Motivo = "sem venda concluída no religamento (NCN)" }, "desfeita").ConfigureAwait(false);
                        n++;
                        break;
                    case "ncn_sem_ack":
                        await DesfazerAsync(tx, "desfeita").ConfigureAwait(false);
                        n++;
                        break;
                }
            }
        }
        finally { _um.Release(); }
        return n;
    }

    /// <summary>
    /// Um `Resp\intpos.001` esquecido (resposta que chegou depois de o PDV desistir, ou crash no
    /// meio) é uma transação que NINGUÉM tratou. Se estiver aprovada e pedindo confirmação, é
    /// DESFEITA — não existe venda para ela — e o desfecho é gravado na linha dela (mesma
    /// identificação) para o caixa não ficar com 'aguardando' eterno. Chamado antes de todo
    /// comando e no boot. Devolve true se desfez algo.
    /// </summary>
    public async Task<bool> ResolverRespostaOrfaAsync()
    {
        await _um.WaitAsync().ConfigureAwait(false);
        try { return await ResolverRespostaOrfaSemTravaAsync().ConfigureAwait(false); }
        finally { _um.Release(); }
    }

    private async Task<bool> ResolverRespostaOrfaSemTravaAsync()
    {
        var caminho = Path.Combine(_resp, "intpos.001");
        if (!File.Exists(caminho)) return false;
        // A órfã pode estar sendo ESCRITA agora (tardia chegando no início do próximo comando):
        // ler pela metade daria "recusado" + apagar uma aprovação. Espera inteira/estável.
        string? texto;
        var relogio = Stopwatch.StartNew();
        while ((texto = LerResposta(caminho)) is null)
        {
            if (!File.Exists(caminho)) return false;
            if (relogio.ElapsedMilliseconds > TempoEstavelMs + 2_000)
            {
                Auditar?.Invoke("paygo: órfã em Resp nunca ficou inteira/estável — lida como está (fallback)");
                texto = LerComRetentativa(caminho);
                break;
            }
            await Task.Delay(IntervaloPollMs).ConfigureAwait(false);
        }
        LimparResp();
        return await TratarRespostaAlheiaAsync(RespostaPayGo.Analisar(texto), "órfã na pasta").ConfigureAwait(false);
    }

    /// <summary>
    /// Resposta que não é da transação em curso (órfã, tardia, de outro processo). Aprovada e
    /// pedindo confirmação → NCN (não há venda para ela); aprovada SEM pedir (729=1, já
    /// efetivada na rede) → ÓRFÃ (dinheiro entrou sem venda: estornar); sem 027 → órfã (não dá
    /// para desfazer às cegas); negada → só registra. A linha gravada usa a identificação DELA,
    /// para encerrar um 'aguardando' deixado pelo crash.
    /// </summary>
    private async Task<bool> TratarRespostaAlheiaAsync(RespostaPayGo r, string origem)
    {
        if (r.Comando is not ("CRT" or "CNC" or "ADM")) return false;
        var id = r.Identificacao ?? NovaId();
        var chargeId = (r.Comando == "CNC" ? "paygo-cnc-" : "paygo-") + id;
        var tipo = r.TipoCartao switch { 2 => TipoTef.Debito, 0 => TipoTef.Pix, _ => TipoTef.Credito };
        var tx = new TransacaoPayGo(chargeId, id, tipo, r.ValorCent ?? 0, r.Parcelas ?? 1, "aprovada", r, "resposta " + origem);
        Auditar?.Invoke($"paygo: resposta {origem} (001={id}, 009={r.Status}, 027={r.CodigoControle ?? "-"}, 729={(r.RequerConfirmacao ? 2 : 1)})");
        if (!r.Aprovada)
        {
            GuardarSeguro(tx with { Situacao = r.Cancelada ? "cancelado" : "recusado", Motivo = $"resposta {origem}: {r.Mensagem}" });
            return false;
        }
        if (!r.RequerConfirmacao)
        {
            GuardarSeguro(tx with { Situacao = "orfa", Motivo = $"resposta {origem} aprovada sem confirmação pendente — dinheiro entrou sem venda; estornar" });
            return false;
        }
        if (string.IsNullOrWhiteSpace(r.CodigoControle))
        {
            GuardarSeguro(tx with { Situacao = "orfa", Motivo = $"resposta {origem} aprovada sem 027 — não dá para desfazer às cegas; confira no PayGo" });
            return false;
        }
        await DesfazerAsync(tx with { Motivo = $"resposta {origem} sem venda (NCN)" }, "desfeita").ConfigureAwait(false);
        return true;
    }

    /// <summary>Antes de todo comando: reenvia CNF/NCN sem ack e desfaz resposta órfã. Roda DENTRO do semáforo.</summary>
    private async Task AntesDoComandoAsync()
    {
        if (_reenvios.Count > 0)
        {
            var fila = _reenvios.ToList();
            _reenvios.Clear();
            foreach (var (tx, cmd, depois) in fila)
            {
                if (cmd == "CNF") await ConfirmarAsync(tx, depois).ConfigureAwait(false);
                else await DesfazerAsync(tx, depois).ConfigureAwait(false);
            }
        }
        await ResolverRespostaOrfaSemTravaAsync().ConfigureAwait(false);
        LimparResp();
    }

    // ------------------------------------------------------------------ CNF / NCN

    /// <summary>CNF. True = acusado → grava `depois`; false → 'cnf_sem_ack' + fila de reenvio.</summary>
    private async Task<bool> ConfirmarAsync(TransacaoPayGo tx, string depois)
    {
        var ok = await EnviarSimplesAsync("CNF", tx.Identificacao, tx.CodigoControle, tx.Resposta?.Rede).ConfigureAwait(false);
        Auditar?.Invoke($"paygo: CNF {tx.ChargeId} 027={tx.CodigoControle ?? "-"} {(ok ? "acusado" : "SEM ACK")}");
        if (ok) GuardarSeguro(tx with { Situacao = depois });
        else
        {
            GuardarSeguro(tx with { Situacao = "cnf_sem_ack" });
            _reenvios.Add((tx, "CNF", depois));
        }
        return ok;
    }

    /// <summary>NCN. True = acusado → grava `depois` (normalmente 'desfeita'); false → 'ncn_sem_ack' + fila de reenvio.</summary>
    private async Task<bool> DesfazerAsync(TransacaoPayGo tx, string depois)
    {
        var ok = await EnviarSimplesAsync("NCN", tx.Identificacao, tx.CodigoControle, tx.Resposta?.Rede).ConfigureAwait(false);
        Auditar?.Invoke($"paygo: NCN {tx.ChargeId} 027={tx.CodigoControle ?? "-"} {(ok ? "acusado" : "SEM ACK")}");
        if (ok) GuardarSeguro(tx with { Situacao = depois });
        else
        {
            GuardarSeguro(tx with { Situacao = "ncn_sem_ack" });
            _reenvios.Add((tx, "NCN", depois));
        }
        return ok;
    }

    /// <summary>
    /// CNF/NCN: só gera .sts. True = o PayGo acusou recebimento. Mexe SÓ no .sts: um NCN pode
    /// sair no meio de uma venda em curso (resposta tardia de outra transação) e o `.001` da
    /// venda em curso pode chegar enquanto se espera o ack — apagá-lo perderia a aprovação.
    /// </summary>
    private async Task<bool> EnviarSimplesAsync(string cmd, string id, string? controle, string? rede)
    {
        ApagarSts();
        var c = Campos(cmd, id);
        if (!string.IsNullOrWhiteSpace(controle)) c["027-000"] = controle!;
        if (!string.IsNullOrWhiteSpace(rede)) c["010-000"] = rede!;
        Gravar(c);
        var ok = await EsperarStsAsync(id).ConfigureAwait(false);
        ApagarSts();
        if (!ok) ApagarReq();
        return ok;
    }

    // ------------------------------------------------------------------ espera

    /// <summary>
    /// Espera o `.sts` da NOSSA requisição até <see cref="TempoStsMs"/>. Um .sts de outra
    /// identificação (tardio) é descartado e a espera continua.
    /// </summary>
    private async Task<bool> EsperarStsAsync(string id)
    {
        var caminho = Path.Combine(_resp, "intpos.sts");
        var relogio = Stopwatch.StartNew();
        while (true)
        {
            if (File.Exists(caminho))
            {
                var t = LerComRetentativa(caminho);
                if (t is not null)
                {
                    var eco = ArquivoIntpos.Analisar(t).GetValueOrDefault("001-000");
                    if (string.IsNullOrWhiteSpace(eco) || eco == id) return true;
                    if (!NossaIdentificacao(eco!))
                    {
                        // 001 desconhecido: PayGo que não ecoa? Aceitar como nosso — travar a
                        // venda por isso seria pior. Fica o alarme para a homologação.
                        Auditar?.Invoke($"paygo: ALARME .sts com 001={eco} (esperava {id}) aceito como nosso");
                        return true;
                    }
                    // .sts tardio de um comando NOSSO anterior (CNF/NCN que o PayGo processou
                    // depois): não é o ack deste — ignorar e seguir esperando o nosso.
                    Auditar?.Invoke($"paygo: .sts de outro comando nosso ignorado (001={eco}, esperava {id})");
                    ApagarSts();
                }
            }
            if (relogio.ElapsedMilliseconds >= TempoStsMs) return false;
            await Task.Delay(IntervaloPollMs).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Espera o `.001` da NOSSA transação. SEM TIMEOUT por contrato: quem está com o cliente é o
    /// pinpad. Resposta de outra identificação é tratada como alheia (desfeita se aprovada) e a
    /// espera continua. Cancelamento do operador não interrompe o TEF — só avisa e, se o PayGo
    /// ficar mudo por <see cref="TempoDesistirAposCancelarMs"/>, devolve null (órfã).
    /// </summary>
    private async Task<string?> EsperarRespostaAsync(string chargeId, string id, IProgress<AndamentoTef>? andamento, CancellationToken ct)
    {
        var caminho = Path.Combine(_resp, "intpos.001");
        Stopwatch? desdeCancelamento = null;
        while (true)
        {
            var t = LerResposta(caminho);
            if (t is not null)
            {
                var eco = ArquivoIntpos.Analisar(t).GetValueOrDefault("001-000");
                if (string.IsNullOrWhiteSpace(eco) || eco == id) return t;
                if (!NossaIdentificacao(eco!))
                {
                    // 001 desconhecido (não é nenhuma identificação que nós emitimos): só pode
                    // ser a nossa com um PayGo que não ecoa. Aceitar — e alarmar na auditoria.
                    Auditar?.Invoke($"paygo: ALARME .001 com 001={eco} (esperava {id}) aceito como nosso");
                    return t;
                }
                // É uma transação nossa anterior (abandonada) respondendo tarde. Desfazer se
                // aprovada e seguir esperando a nossa — sem isso ela viraria "pago" aqui.
                LimparResp();
                await TratarRespostaAlheiaAsync(RespostaPayGo.Analisar(t), $"tardia (001={eco}) durante {id}").ConfigureAwait(false);
                continue;
            }
            if (desdeCancelamento is null && ct.IsCancellationRequested)
            {
                desdeCancelamento = Stopwatch.StartNew();
                andamento?.Report(new AndamentoTef(FaseTef.Recado, chargeId, id,
                    "Cancelamento pedido — o TEF não pode ser interrompido pelo caixa; aguardando o pinpad…"));
            }
            if (desdeCancelamento is not null && desdeCancelamento.ElapsedMilliseconds >= TempoDesistirAposCancelarMs)
                return null;
            await Task.Delay(IntervaloPollMs).ConfigureAwait(false);
        }
    }

    /// <summary>Arquivo `.001` incompleto (sem `999-999 = 0`) e parado por este tempo é aceito assim mesmo (PayGo que gravou torto).</summary>
    public int TempoEstavelMs { get; init; } = 2_000;

    private string? _ultimaLeitura;
    private Stopwatch? _estavelDesde;

    /// <summary>
    /// Lê o `.001` só quando ele está INTEIRO (última linha `999-999 = 0`). Se o PayGo gravar
    /// sem rename, o arquivo existe antes de terminar — ler pela metade daria "inconsistência"
    /// e apagaria uma aprovação. Fallback: conteúdo parado por <see cref="TempoEstavelMs"/> é aceito.
    /// </summary>
    private string? LerResposta(string caminho)
    {
        if (!File.Exists(caminho)) { _ultimaLeitura = null; _estavelDesde = null; return null; }
        var t = LerComRetentativa(caminho);
        if (t is null) return null;
        if (ArquivoIntpos.Completo(t)) { _ultimaLeitura = null; _estavelDesde = null; return t; }
        if (t != _ultimaLeitura || _estavelDesde is null) { _ultimaLeitura = t; _estavelDesde = Stopwatch.StartNew(); return null; }
        if (_estavelDesde.ElapsedMilliseconds >= TempoEstavelMs) { _ultimaLeitura = null; _estavelDesde = null; return t; }
        return null;
    }

    // ------------------------------------------------------------------ arquivos

    /// <summary>
    /// Grava `Req\intpos.001` do jeito que a spec manda: `.tmp` → flush → rename. O PayGo
    /// monitora a pasta e um arquivo pela metade seria lido como comando truncado.
    /// </summary>
    private void Gravar(Dictionary<string, string> campos)
    {
        Directory.CreateDirectory(_req);
        var tmp = Path.Combine(_req, "intpos.tmp");
        var fim = Path.Combine(_req, "intpos.001");
        var texto = ArquivoIntpos.Serializar(campos);
        var bytes = Encoding.ASCII.GetBytes(texto);
        for (var tentativa = 0; ; tentativa++)
        {
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }
                File.Move(tmp, fim, overwrite: true);
                return;
            }
            catch (IOException) when (tentativa < 20) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) when (tentativa < 20) { Thread.Sleep(50); }
        }
    }

    /// <summary>Antivírus pode segurar o arquivo por um instante: re-tenta em frações de segundo (spec).</summary>
    private static string? LerComRetentativa(string caminho)
    {
        for (var tentativa = 0; tentativa < 40; tentativa++)
        {
            try
            {
                using var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.ASCII);
                return sr.ReadToEnd();
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
        return null;
    }

    private void LimparResp()
    {
        Directory.CreateDirectory(_resp);
        foreach (var nome in new[] { "intpos.sts", "intpos.001" })
            ApagarComRetentativa(Path.Combine(_resp, nome));
    }

    private void ApagarSts() => ApagarComRetentativa(Path.Combine(_resp, "intpos.sts"));

    private void ApagarReq()
    {
        ApagarComRetentativa(Path.Combine(_req, "intpos.001"));
        ApagarComRetentativa(Path.Combine(_req, "intpos.tmp"));
    }

    private static void ApagarComRetentativa(string caminho)
    {
        for (var tentativa = 0; tentativa < 20; tentativa++)
        {
            try { if (File.Exists(caminho)) File.Delete(caminho); return; }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }

    // ------------------------------------------------------------------ campos

    private Dictionary<string, string> Campos(string cmd, string id)
    {
        var c = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["000-000"] = cmd,
            ["001-000"] = id,
            ["733-000"] = _op.VersaoInterface,
            ["735-000"] = _op.NomeAutomacao,
            ["736-000"] = _op.VersaoAutomacao,
        };
        // 738 vazio é omitido (a linha `738-000 = ` sem valor é inconsistência para o PayGo).
        if (!string.IsNullOrWhiteSpace(_op.Registro)) c["738-000"] = _op.Registro;
        return c;
    }

    /// <summary>
    /// `001-000` é n..10 e único por operação. Epoch em segundos tem 10 dígitos até 2286; o
    /// max() com a última garante unicidade mesmo com dois comandos no mesmo segundo.
    /// </summary>
    public static string NovaIdentificacao()
    {
        while (true)
        {
            var agora = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var ultima = Interlocked.Read(ref _ultimaIdentificacao);
            var nova = Math.Max(agora, ultima + 1);
            if (Interlocked.CompareExchange(ref _ultimaIdentificacao, nova, ultima) == ultima)
                return nova.ToString(CultureInfo.InvariantCulture);
        }
    }

    private CartaoTef Cartao(RespostaPayGo r)
    {
        var rede = r.Rede;
        string? cnpj = null;
        if (!string.IsNullOrWhiteSpace(rede))
        {
            try { cnpj = CnpjDaRede?.Invoke(rede!); } catch { cnpj = null; }
            cnpj ??= CnpjConhecido(rede!);
        }
        var nome = r.Produto ?? r.NomeCartao;
        return new CartaoTef(
            CAut: r.Autorizacao,
            Cnpj: cnpj,
            TBand: TBand(nome),
            Bandeira: r.NomeCartao ?? r.Produto,
            Adquirente: rede,
            Nsu: r.Nsu,
            Parcelas: r.Parcelas,
            Terminal: r.Terminal,
            Valor: r.ValorCent is { } c ? c / 100m : null);
    }

    /// <summary>tBand da NFC-e a partir do nome do cartão/produto (040/748). 99 = outros.</summary>
    public static string TBand(string? nome)
    {
        var n = (nome ?? "").ToUpperInvariant();
        if (n.Contains("VISA")) return "01";
        if (n.Contains("MASTER")) return "02";
        if (n.Contains("AMEX") || n.Contains("AMERICAN")) return "03";
        if (n.Contains("SOROCRED")) return "04";
        if (n.Contains("DINERS")) return "05";
        if (n.Contains("ELO")) return "06";
        if (n.Contains("HIPER")) return "07";
        if (n.Contains("AURA")) return "08";
        if (n.Contains("CABAL")) return "09";
        return "99";
    }

    /// <summary>
    /// CNPJ das credenciadoras mais comuns (tabela inicial; `config['tef_cnpj_rede_<rede>']`
    /// sobrepõe). Rede desconhecida → null → a NFC-e sai com tpIntegra=2, que é honesto.
    /// </summary>
    public static string? CnpjConhecido(string rede) => rede.Trim().ToUpperInvariant() switch
    {
        "REDE" or "REDECARD" => "01425787000104",
        "CIELO" or "VISANET" => "01027058000191",
        "GETNET" => "10440482000154",
        "STONE" => "16501555000157",
        "PAGSEGURO" => "08561701000101",
        "SAFRA" or "SAFRAPAY" => "58160789000128",
        "VERO" or "BANRISUL" => "92934215000106",
        _ => null,
    };

    private bool GuardarSeguro(TransacaoPayGo t)
    {
        try { return Guardar(t); }
        catch { return false; }
    }

    /// <summary>Delegate quebrado não pode virar CNF por engano: erro = "não conheço" = NCN.</summary>
    private bool ConhecidaSegura(string codControle)
    {
        try { return ConhecidaConfirmada?.Invoke(codControle) == true; }
        catch { return false; }
    }

    private static DesfechoTef Falha(SituacaoTef s, string chargeId, string codigo, string motivo, bool posOcupado = false)
        => new(s, null, chargeId, null, motivo, posOcupado) { Codigo = codigo };
}
