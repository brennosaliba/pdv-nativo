using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// Configuração do terminal. Aparece uma vez (1ª execução) e depois só por dentro,
/// com senha de administrador. Reabrir vem PREENCHIDO — reconfigurar não pode
/// significar redigitar tudo.
/// </summary>
public partial class Configuracao : UserControl
{
    public event Action? Concluiu;
    private readonly bool _jaConfigurado;
    private string? _pfxEscolhido;

    public Configuracao()
    {
        InitializeComponent();
        using var cx = Banco.Abrir();
        _jaConfigurado = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM terminal") > 0;
        _ = CarregarImpressorasAsync(Vendas.Config(cx, "impressora"));
        _ = CarregarImpressorasComandaAsync(Vendas.Config(cx, "kds_comanda_impressora"));
        ChkComandaAuto.IsChecked = Vendas.Config(cx, "kds_comanda_auto") == "1";

        // Tema: preferência da MÁQUINA, carrega mesmo antes da primeira configuração
        // e grava na hora (não espera o Salvar — o Salvar valida identidade fiscal,
        // e uma preferência de luz não pode ficar refém do pareamento).
        _carregandoTema = true;
        CboTema.SelectedIndex = Vendas.Config(cx, "tema") switch { "claro" => 1, "auto" => 2, _ => 0 };
        TxtTemaDe.Text = Vendas.Config(cx, "tema_claro_de", "06:00");
        TxtTemaAte.Text = Vendas.Config(cx, "tema_claro_ate", "18:00");
        BlocoJanelaTema.Visibility = CboTema.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        _carregandoTema = false;

        // TEF: também é preferência da MÁQUINA (qual maquininha/PayGo este caixa usa) —
        // carrega sempre e reabre preenchido.
        TefModo = Vendas.Config(cx, "tef_habilitado") != "1" ? 0
            : Vendas.Config(cx, "tef_provedor") switch { "paygo" => 2, "controlpay" => 3, _ => 1 };
        CmbCpayAmbiente.SelectedIndex = Vendas.Config(cx, "tef_cpay_ambiente") == "producao" ? 1 : 0;
        TxtCpayTerminal.Text = Vendas.Config(cx, "tef_cpay_terminal", "");
        TxtCpayPessoa.Text = Vendas.Config(cx, "tef_cpay_pessoa", "");
        TxtCpayAdquirente.Text = Vendas.Config(cx, "tef_cpay_adquirente", "");
        TxtCpayAdquirentePix.Text = Vendas.Config(cx, "tef_cpay_adquirente_pix", "");
        {
            // segredos do ControlPay no cofre DPAPI (reabrir vem preenchido)
            var segTef = LerSegredos();
            PwdCpayChave.Password = segTef.GetValueOrDefault("cpayChave", "");
            PwdCpaySenha.Password = segTef.GetValueOrDefault("cpaySenhaTecnica", "");
        }
        TxtPayGoPasta.Text = Vendas.Config(cx, "tef_paygo_pasta", "");
        TxtPayGoRegistro.Text = Vendas.Config(cx, "tef_paygo_registro", "");
        TxtPayGoEmpresa.Text = Vendas.Config(cx, "tef_paygo_empresa", "");
        TxtPayGoRede.Text = Vendas.Config(cx, "tef_paygo_rede", "");
        TxtPayGoRedePix.Text = Vendas.Config(cx, "tef_paygo_rede_pix", "");
        ChkPayGoVias.IsChecked = Vendas.Config(cx, "tef_paygo_imprimir_vias", "1") != "0";
        ChkTefParcelas.IsChecked = Vendas.Config(cx, "tef_perguntar_parcelas", "0") == "1";
        TxtTefSerial.Text = Vendas.Config(cx, "tef_serial_pos", "");
        PintarBlocosTef();
        // Testar/ADM gravam as chaves para rodar com o que está na tela; se o operador sair
        // por "Voltar" sem salvar, volta TUDO ao que era (senão "testei e o caixa ligou o PayGo").
        foreach (var k in ChavesTef) _tefOriginal[k] = Vendas.Config(cx, k);

        if (_jaConfigurado)
        {
            var t = cx.QueryFirst("SELECT loja_nome, cnpj, serie_nfce, ambiente, api_base FROM terminal LIMIT 1");
            TxtLoja.Text = (string)t.loja_nome;
            TxtCnpj.Text = (string)t.cnpj;
            TxtSerie.Text = ((long)t.serie_nfce).ToString();
            // modo recibo (sem emissão) vive na config, por cima do ambiente da SEFAZ
            CmbAmbiente.SelectedIndex = Vendas.Config(cx, "modo_fiscal") == "recibo" ? 2
                : (long)t.ambiente == 1 ? 1 : 0;
            ChkImprimirAuto.IsChecked = Vendas.Config(cx, "imprimir_automatico", "1") != "0";
            TxtApi.Text = t.api_base as string ?? "";
            BtnVoltar.Visibility = Visibility.Visible;
            // reabrir vem preenchido: reconfigurar não pode ser redigitar tudo
            var seg = LerSegredos();
            TxtSenhaPfx.Password = seg.GetValueOrDefault("senhaPfx", "");
            TxtCsc.Password = seg.GetValueOrDefault("csc", "");
            TxtIdCsc.Text = seg.GetValueOrDefault("idCsc", "000001");
            if (File.Exists(ArqCert)) { TxtPfx.Text = "cert.pfx (já configurado)"; ConferirCert(this, new RoutedEventArgs()); }
            // status do pareamento na PRÓPRIA seção (a bateria de teste não fala dele)
            if (LerSegredos().ContainsKey("nuvemEmail"))
            {
                TxtStatusPareamento.Text = "✓ Este caixa já está pareado com o painel — vendas e notas sobem no Sincronizar.";
                TxtStatusPareamento.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Ok"];
            }
            // operador já existe: não pede de novo
            if (Operadores.ExisteAlgum(cx))
            {
                TxtPrimeiroOperador.Visibility = Visibility.Collapsed;
                BlocoOperador.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            TxtSerie.Text = "3";
            TxtApi.Text = "http://54.232.6.39";
            TxtIdCsc.Text = "000001";
        }
    }

    /// <summary>Senha de admin guardada como hash (mesmo PBKDF2 do PIN), nunca em claro.</summary>
    public static bool SenhaAdminConfere(SqliteConnection cx, string senha)
    {
        var r = cx.QueryFirstOrDefault("SELECT pin_hash, pin_salt FROM operador WHERE id = '_admin_'");
        if (r is null) return false;
        return Operadores.Confere(senha, (string)r.pin_hash, (string)r.pin_salt);
    }

    // ── SEGREDOS DA MÁQUINA ─────────────────────────────────────────────────
    // Certificado, senha dele, CSC e a credencial da nuvem ficam cifrados por DPAPI,
    // amarrados a ESTA máquina. Mesmo copiando o arquivo, em outro PC não abre.
    private static string PastaSegredos => Path.Combine(Banco.Pasta, "seg");
    private static string ArqSegredos => Path.Combine(PastaSegredos, "seg.dat");
    public static string ArqCert => Path.Combine(PastaSegredos, "cert.pfx");

    /// <summary>
    /// Lista as impressoras fora da thread de UI. Enumerar filas de rede bloqueia no
    /// timeout de cada servidor de impressão inalcançável — segundos por servidor, com
    /// a tela congelada.
    /// </summary>
    private async Task CarregarImpressorasAsync(string? escolhida)
    {
        CboImpressora.Items.Add("(padrão do Windows)");
        CboImpressora.SelectedIndex = 0;
        try
        {
            var lista = await Impressao.ImpressorasAsync();
            foreach (var nome in lista) CboImpressora.Items.Add(nome);
            if (escolhida is { Length: > 0 } && CboImpressora.Items.Contains(escolhida))
                CboImpressora.SelectedItem = escolhida;
            else if (escolhida is { Length: > 0 })
            {
                // impressora salva que não está mais instalada: mantém à vista em vez de
                // silenciosamente cair na padrão, senão o cupom sai noutro lugar sem aviso
                CboImpressora.Items.Add(escolhida + "  (não encontrada)");
                CboImpressora.SelectedIndex = CboImpressora.Items.Count - 1;
            }
        }
        catch (Exception ex)
        {
            TxtStatusImpressao.Text = "Não consegui listar as impressoras: " + ex.Message;
        }
    }

    private string? ImpressoraEscolhida()
    {
        var s = CboImpressora.SelectedItem as string;
        if (s is null || s.StartsWith("(padrão")) return null;
        var i = s.IndexOf("  (não encontrada)", StringComparison.Ordinal);
        return i > 0 ? s[..i] : s;
    }

    /// <summary>Espelho de <see cref="CarregarImpressorasAsync"/> pro combo da
    /// COMANDA da cozinha — impressora própria, separada da bobina do caixa.</summary>
    private async Task CarregarImpressorasComandaAsync(string? escolhida)
    {
        CboImpressoraComanda.Items.Add("(padrão do Windows)");
        CboImpressoraComanda.SelectedIndex = 0;
        try
        {
            var lista = await Impressao.ImpressorasAsync();
            foreach (var nome in lista) CboImpressoraComanda.Items.Add(nome);
            if (escolhida is { Length: > 0 } && CboImpressoraComanda.Items.Contains(escolhida))
                CboImpressoraComanda.SelectedItem = escolhida;
            else if (escolhida is { Length: > 0 })
            {
                CboImpressoraComanda.Items.Add(escolhida + "  (não encontrada)");
                CboImpressoraComanda.SelectedIndex = CboImpressoraComanda.Items.Count - 1;
            }
        }
        catch { /* a lista do combo de cima já mostrou o erro do spooler */ }
    }

    private string? ImpressoraComandaEscolhida()
    {
        var s = CboImpressoraComanda.SelectedItem as string;
        if (s is null || s.StartsWith("(padrão")) return null;
        var i = s.IndexOf("  (não encontrada)", StringComparison.Ordinal);
        return i > 0 ? s[..i] : s;
    }

    /// <summary>
    /// Cupom de teste com dados falsos. Existe para validar layout, corte e QR SEM
    /// emitir documento fiscal — descobrir que a impressão está torta junto com a
    /// primeira nota real confunde dois problemas num só.
    /// </summary>
    private async void TestarImpressao(object sender, RoutedEventArgs e)
    {
        BtnTesteImpressao.IsEnabled = false;
        TxtStatusImpressao.Text = "Imprimindo…";
        try
        {
            var dados = new DadosCupom(
                EmitenteNome: TxtLoja.Text.Length > 0 ? TxtLoja.Text : "LOJA DE TESTE",
                EmitenteCnpj: TxtCnpj.Text.Length > 0 ? TxtCnpj.Text : "00000000000000",
                EmitenteIe: "ISENTO",
                EmitenteEndereco: "RUA DE TESTE, 100 - CENTRO - BELO HORIZONTE/MG",
                Numero: 0, Serie: int.TryParse(TxtSerie.Text, out var sr) ? sr : 0,
                Chave: new string('0', 44),
                Emissao: DateTime.Now,
                // conteúdo qualquer só pra exercitar o desenho do QR
                QrCode: "https://portalsped.fazenda.mg.gov.br/portalnfce/sistema/qrcode.xhtml?p=TESTE",
                TpAmb: 2,
                Itens: new[]
                {
                    new ItemCupom("SKU1", "COOKIE TRADICIONAL", Quantidade.Um, "UN",
                        Dinheiro.DeReais(12), Dinheiro.DeReais(12)),
                    new ItemCupom("SKU2", "AGUA MINERAL 500ML", new Quantidade(2000), "UN",
                        Dinheiro.DeReais(6), Dinheiro.DeReais(12)),
                },
                Total: Dinheiro.DeReais(24),
                VNf: 24m,
                Pagamentos: new[] { new PagamentoCupom("Dinheiro", Dinheiro.DeReais(24)) },
                Recebido: Dinheiro.DeReais(50),
                Documento: null,
                Contingencia: false,
                Operador: "TESTE DE IMPRESSÃO");

            var erro = await Impressao.ImprimirAsync(dados, ImpressoraEscolhida());
            TxtStatusImpressao.Text = erro is null
                ? "Cupom de teste enviado. Confira o papel: margens, corte e o QR."
                : "Não imprimiu: " + erro;
            TxtStatusImpressao.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[
                erro is null ? "Ok" : "Erro"];
        }
        finally { BtnTesteImpressao.IsEnabled = true; }
    }

    public static Dictionary<string, string> LerSegredos()
    {
        try
        {
            var b = ProtectedData.Unprotect(File.ReadAllBytes(ArqSegredos), null, DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(b))!;
        }
        catch { return new Dictionary<string, string>(); }
    }

    private static void GravarSegredos(Dictionary<string, string> d)
    {
        Directory.CreateDirectory(PastaSegredos);
        var b = ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)), null, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(ArqSegredos, b);
    }

    /// <summary>
    /// Pareia o caixa com o painel: o gerente gera um código de 6 dígitos no painel, o
    /// caixa digita aqui UMA vez e recebe a identidade própria (criada no servidor),
    /// que fica cifrada nesta máquina. Nenhuma senha é digitada no balcão, e revogar
    /// um caixa é um clique no painel — é o que destrava a subida de vendas e notas.
    /// </summary>
    private async void Parear(object sender, RoutedEventArgs e)
    {
        var dono = Window.GetWindow(this)!;
        var codigo = PedirTexto.Mostrar(dono, "Parear com o painel",
            "Digite o código de 6 dígitos mostrado no painel", "");
        if (string.IsNullOrWhiteSpace(codigo)) return;

        BtnParear.IsEnabled = false;
        TxtStatusPareamento.Text = "Falando com o servidor…";
        try
        {
            using var cx = Banco.Abrir();
            var t = cx.QueryFirstOrDefault("SELECT terminal_uuid, loja_nome FROM terminal LIMIT 1");
            // nomes de campo são CONTRATO com a edge pdv-pareamento: `code` e `nome`
            var corpo = JsonSerializer.Serialize(new
            {
                acao = "resgatar",
                code = codigo.Trim(),
                nome = (t?.loja_nome as string) ?? Environment.MachineName,
            });

            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post,
                Nuvem.UrlPadrao + "/functions/v1/pdv-pareamento");
            req.Headers.TryAddWithoutValidation("apikey", Nuvem.AnonKey);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Nuvem.AnonKey);
            req.Content = new System.Net.Http.StringContent(corpo, Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req);
            var texto = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(texto);
            var r = doc.RootElement;
            if (!resp.IsSuccessStatusCode
                || !r.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
                || !r.TryGetProperty("email", out var em) || !r.TryGetProperty("senha", out var sn))
            {
                var erro = r.TryGetProperty("error", out var er) ? er.GetString() : $"HTTP {(int)resp.StatusCode}";
                TxtStatusPareamento.Text = "✗ " + (erro ?? "não deu certo — gere um código novo no painel");
                return;
            }

            // a identidade do TERMINAL fica cifrada nesta máquina; ninguém a digita nem vê
            var seg = LerSegredos();
            seg["nuvemEmail"] = em.GetString()!;
            seg["nuvemSenha"] = sn.GetString()!;
            GravarSegredos(seg);

            // A IDENTIDADE DA LOJA vem junto do código: CNPJ, razão social, ambiente e
            // — o que mais importa — a SÉRIE alocada pelo servidor. Digitar isso à mão
            // era a origem de dois erros caros: CNPJ errado = nota fiscal emitida em
            // nome de outra loja; série repetida entre dois caixas = Rejeição 539 em
            // cascata, descoberta só com cliente no balcão.
            var resumo = AplicarIdentidade(cx, r);
            TxtStatusPareamento.Text = "✓ Caixa pareado. " + resumo;
        }
        catch (Exception ex)
        {
            TxtStatusPareamento.Text = "✗ " + ex.Message;
        }
        finally { BtnParear.IsEnabled = true; }
    }

    /// <summary>
    /// Grava a identidade que veio do pareamento nos campos da tela E na tabela
    /// `terminal`. É o que transforma a primeira instalação em "digitou 6 dígitos,
    /// acabou" — antes, loja/CNPJ/série eram digitados aqui, e os dois erros que
    /// isso causava são caros: CNPJ errado emite nota em nome de outra loja, e
    /// série repetida entre caixas gera Rejeição 539 em cascata.
    ///
    /// Campos ausentes na resposta são IGNORADOS (edge antiga continua parenado
    /// normal, só sem preencher). Devolve o resumo para a tela.
    /// </summary>
    private string AplicarIdentidade(SqliteConnection cx, JsonElement r)
    {
        string? Txt(string nome) =>
            r.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Num(string nome) =>
            r.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

        var loja = Txt("loja_nome");
        var lojaId = Txt("loja_id");
        var cnpj = new string((Txt("cnpj") ?? "").Where(char.IsLetterOrDigit).ToArray());
        var serieLocal = Num("serie_local");
        var serieNuvem = Num("serie_nuvem");
        var ambiente = Num("ambiente");

        if (loja is { Length: > 0 }) TxtLoja.Text = loja;
        if (cnpj.Length == 14) TxtCnpj.Text = cnpj;
        if (serieLocal is int sl) TxtSerie.Text = sl.ToString();
        if (ambiente is int amb) CmbAmbiente.SelectedIndex = amb == 1 ? 1 : 0;

        // A série da NUVEM é outro contador (nfce_config). Guardar aqui é o que
        // permite ao resolvedor provar que ela é diferente da série local antes
        // de deixar a contingência assumir.
        if (serieNuvem is int sn) Vendas.GravarConfig(cx, "serie_nuvem", sn.ToString());

        var jaTem = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM terminal") > 0;
        if (jaTem && (loja is { Length: > 0 } || cnpj.Length == 14 || serieLocal is not null))
        {
            cx.Execute("""
                UPDATE terminal SET
                  loja_nome  = COALESCE(@Loja, loja_nome),
                  loja_id    = COALESCE(@LojaId, loja_id),
                  cnpj       = COALESCE(@Cnpj, cnpj),
                  serie_nfce = COALESCE(@Serie, serie_nfce),
                  ambiente   = COALESCE(@Amb, ambiente)
                WHERE id = 1
                """,
                new
                {
                    Loja = loja is { Length: > 0 } ? loja : null,
                    LojaId = lojaId is { Length: > 0 } ? lojaId : null,
                    Cnpj = cnpj.Length == 14 ? cnpj : null,
                    Serie = serieLocal,
                    Amb = ambiente,
                });
        }

        var partes = new List<string>();
        if (loja is { Length: > 0 }) partes.Add(loja);
        if (serieLocal is int s2) partes.Add($"série {s2}");
        if (ambiente == 2) partes.Add("HOMOLOGAÇÃO");
        return partes.Count == 0
            ? "Vendas e notas passam a subir no Sincronizar."
            : string.Join(" · ", partes) + " — confira e salve.";
    }

    private void EscolherPfx(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Certificado (*.pfx;*.p12)|*.pfx;*.p12" };
        if (d.ShowDialog() != true) return;
        _pfxEscolhido = d.FileName;
        TxtPfx.Text = Path.GetFileName(d.FileName);
        ConferirCert(this, new RoutedEventArgs());
        if (TxtSenhaPfx.Password.Length == 0) TxtSenhaPfx.Focus();
    }

    // ── CNPJ: máscara + algoritmo em tempo real ─────────────────────────────
    // O dígito verificador pega CNPJ digitado errado AQUI, não na Rejeição 207
    // da SEFAZ com cliente no balcão. A máscara (00.000.000/0000-00) elimina a
    // dúvida "com ou sem pontos?" — aceita dos dois jeitos e exibe formatado.
    private bool _formatandoCnpj;

    private void FormatarCnpj(object sender, TextChangedEventArgs e)
    {
        if (_formatandoCnpj) return;
        _formatandoCnpj = true;
        try
        {
            var dig = new string(TxtCnpj.Text.Where(char.IsDigit).Take(14).ToArray());
            var sb = new StringBuilder(18);
            for (var i = 0; i < dig.Length; i++)
            {
                if (i == 2 || i == 5) sb.Append('.');
                else if (i == 8) sb.Append('/');
                else if (i == 12) sb.Append('-');
                sb.Append(dig[i]);
            }
            TxtCnpj.Text = sb.ToString();
            TxtCnpj.CaretIndex = TxtCnpj.Text.Length; // digitação de CNPJ é sempre "no fim"

            if (dig.Length == 0) { TxtStatusCnpj.Text = ""; }
            else if (dig.Length < 14)
            {
                TxtStatusCnpj.Text = $"… {dig.Length}/14 dígitos";
                TxtStatusCnpj.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextoFraco"];
            }
            else if (Documentos.CnpjValido(dig))
            {
                TxtStatusCnpj.Text = "✓ CNPJ válido";
                TxtStatusCnpj.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Ok"];
            }
            else
            {
                TxtStatusCnpj.Text = "✗ Dígitos verificadores não conferem — confira número por número.";
                TxtStatusCnpj.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Erro"];
            }
            // CNPJ mudou: se já há certificado carregado, refaz a comparação
            if (TxtSenhaPfx.Password.Length > 0) ConferirCert(this, new RoutedEventArgs());
        }
        finally { _formatandoCnpj = false; }
    }

    /// <summary>CNPJ do titular do certificado ICP-Brasil (CN = "RAZÃO SOCIAL:CNPJ").</summary>
    private static string? CnpjDoCertificado(X509Certificate2 c)
    {
        var cn = c.GetNameInfo(X509NameType.SimpleName, false) ?? "";
        var i = cn.LastIndexOf(':');
        if (i >= 0)
        {
            var dig = new string(cn[(i + 1)..].Where(char.IsDigit).ToArray());
            if (dig.Length == 14) return dig;
        }
        // fallback: qualquer sequência de 14 dígitos no Subject
        var m = System.Text.RegularExpressions.Regex.Match(c.Subject, @"\d{14}");
        return m.Success ? m.Value : null;
    }

    /// <summary>
    /// Abre o .pfx com a senha digitada — pega senha errada AQUI, não na 1ª venda.
    /// Roda a cada tecla da senha (PasswordChanged) e também confere se o CNPJ do
    /// certificado é o MESMO da loja: certificado de outro CNPJ emite nota que a
    /// SEFAZ rejeita (ou pior, autoriza em nome de outra empresa).
    /// </summary>
    private void ConferirCert(object sender, RoutedEventArgs e)
    {
        var caminho = _pfxEscolhido ?? (File.Exists(ArqCert) ? ArqCert : null);
        if (caminho is null || TxtSenhaPfx.Password.Length == 0) { TxtStatusCert.Text = ""; return; }
        try
        {
            using var c = new X509Certificate2(caminho, TxtSenhaPfx.Password);
            var dias = (int)(c.NotAfter - DateTime.Now).TotalDays;
            if (dias < 0)
            {
                TxtStatusCert.Text = $"✗ Certificado VENCIDO em {c.NotAfter:dd/MM/yyyy} — a SEFAZ recusa as notas.";
                TxtStatusCert.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Erro"];
                return;
            }
            var texto = $"✓ {c.GetNameInfo(X509NameType.SimpleName, false)} · válido até {c.NotAfter:dd/MM/yyyy}"
                        + (dias <= 30 ? $" (faltam {dias} dias)" : "");
            var chave = dias <= 30 ? "Erro" : "Ok";

            // Comparação pela RAIZ (8 primeiros dígitos = a empresa): certificado da
            // MATRIZ 0001 assina nota das FILIAIS 0002/0003... — a SEFAZ aceita.
            // Erro de verdade é raiz DIFERENTE (outra empresa).
            var cnpjCert = CnpjDoCertificado(c);
            var cnpjLoja = new string(TxtCnpj.Text.Where(char.IsDigit).ToArray());
            if (cnpjCert is not null && cnpjLoja.Length == 14)
            {
                if (cnpjCert == cnpjLoja) texto += " · CNPJ confere com a loja";
                else if (cnpjCert[..8] == cnpjLoja[..8])
                    texto += $" · certificado da matriz/outra filial ({Documentos.Formatar(cnpjCert)}) — mesma empresa, a SEFAZ aceita";
                else
                {
                    texto += $"\n✗ Certificado de OUTRA EMPRESA ({Documentos.Formatar(cnpjCert)}) — a raiz do CNPJ não confere; nota sairia em nome de outra empresa.";
                    chave = "Erro";
                }
            }
            TxtStatusCert.Text = texto;
            TxtStatusCert.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[chave];
        }
        catch
        {
            TxtStatusCert.Text = "✗ Senha incorreta para este certificado.";
            TxtStatusCert.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Erro"];
        }
    }

    // ── TESTE GERAL ─────────────────────────────────────────────────────────
    /// <summary>
    /// Confere a configuração inteira de uma vez, na ordem em que as coisas
    /// quebram na vida real: CNPJ → certificado → CSC → emissor local → servidor
    /// fiscal → pareamento. Cada linha é ✓/⚠/✗ com o motivo — pra descobrir o
    /// problema AGORA, não na primeira venda com cliente esperando.
    /// </summary>
    private async void TestarConfiguracao(object sender, RoutedEventArgs e)
    {
        BtnTestarTudo.IsEnabled = false;
        TxtStatusTeste.Text = "Testando…";
        var linhas = new List<string>();
        var pior = 0; // 0 ok, 1 aviso, 2 erro
        void Add(int nivel, string msg) { linhas.Add(msg); pior = Math.Max(pior, nivel); }

        try
        {
            // 1. CNPJ (algoritmo)
            var cnpj = new string(TxtCnpj.Text.Where(char.IsDigit).ToArray());
            if (cnpj.Length == 14 && Documentos.CnpjValido(cnpj)) Add(0, "✓ CNPJ válido");
            else Add(2, cnpj.Length == 14 ? "✗ CNPJ com dígito verificador errado" : "✗ CNPJ incompleto");

            // Modo RECIBO (sem emissão): certificado/CSC/emissor não são exigidos —
            // testar e reprovar por eles confundiria (a loja ESCOLHEU não emitir).
            var modoRecibo = CmbAmbiente.SelectedIndex == 2;
            if (modoRecibo)
                Add(1, "ℹ Modo RECIBO (sem emissão fiscal): certificado, CSC e emissor não são exigidos");

            // 2. Certificado (abre? validade? CNPJ bate?)
            var caminhoCert = _pfxEscolhido ?? (File.Exists(ArqCert) ? ArqCert : null);
            var producao = CmbAmbiente.SelectedIndex == 1;
            if (modoRecibo) { /* pula certificado/CSC/emissor — segue pros testes de rede */ }
            else
            if (caminhoCert is null || TxtSenhaPfx.Password.Length == 0)
                Add(producao ? 2 : 1, producao
                    ? "✗ Sem certificado/senha — produção NÃO emite nota"
                    : "⚠ Sem certificado (ok em homologação, obrigatório pra produção)");
            else
            {
                try
                {
                    using var c = new X509Certificate2(caminhoCert, TxtSenhaPfx.Password);
                    var dias = (int)(c.NotAfter - DateTime.Now).TotalDays;
                    if (dias < 0) Add(2, $"✗ Certificado VENCIDO em {c.NotAfter:dd/MM/yyyy}");
                    else if (dias <= 30) Add(1, $"⚠ Certificado vence em {dias} dias ({c.NotAfter:dd/MM/yyyy}) — renove");
                    else Add(0, $"✓ Certificado ok (válido até {c.NotAfter:dd/MM/yyyy})");
                    var cnpjCert = CnpjDoCertificado(c);
                    if (cnpjCert is not null && cnpj.Length == 14)
                    {
                        if (cnpjCert == cnpj) Add(0, "✓ CNPJ do certificado confere com a loja");
                        else if (cnpjCert[..8] == cnpj[..8])
                            Add(0, $"✓ Certificado da matriz/outra filial ({Documentos.Formatar(cnpjCert)}) — mesma empresa, aceito");
                        else Add(2, $"✗ Certificado de OUTRA EMPRESA ({Documentos.Formatar(cnpjCert)}) — raiz do CNPJ não confere");
                    }
                }
                catch { Add(2, "✗ Senha do certificado incorreta"); }
            }

            // 3. CSC + ID (a SEFAZ só valida o CSC de verdade na 1ª emissão)
            if (!modoRecibo)
            {
                if (TxtCsc.Password.Length >= 16) Add(0, "✓ CSC preenchido (validação final acontece na 1ª nota)");
                else Add(producao ? 2 : 1, TxtCsc.Password.Length == 0
                    ? (producao ? "✗ CSC vazio — produção NÃO gera o QR Code" : "⚠ CSC vazio (necessário pra emitir)")
                    : "⚠ CSC muito curto — confira no portal da SEFAZ");
                if (!TxtIdCsc.Text.Trim().All(char.IsDigit) || TxtIdCsc.Text.Trim().Length == 0)
                    Add(1, "⚠ ID do CSC deve ser numérico (ex.: 000001)");
            }

            // 4. Emissor fiscal local (o vigia sobe junto com o PDV — MAS só nas
            // máquinas de caixa, onde C:\kiosk\agent está instalado; ver Agente.cs)
            if (modoRecibo) { /* recibo não usa emissor */ }
            else if (!File.Exists(@"C:\kiosk\agent\pdv-agent.cjs"))
            {
                Add(1, "⚠ O programa que emite a nota não está instalado nesta máquina. É normal num PC "
                     + "que não é o caixa da loja. Se as vendas forem sair DAQUI, ele precisa ser instalado "
                     + "antes de ligar a emissão — senão a venda grava e a nota não sai.");
            }
            else
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                try
                {
                    var r = await http.GetAsync("http://127.0.0.1:4610/health");
                    Add(r.IsSuccessStatusCode ? 0 : 1, r.IsSuccessStatusCode
                        ? "✓ Emissor fiscal local no ar"
                        : $"⚠ Emissor local respondeu HTTP {(int)r.StatusCode}");
                }
                catch { Add(1, "⚠ Emissor fiscal local fora do ar — feche e abra o PDV (o vigia religa em 30s)"); }
            }

            // 5. Servidor fiscal (api_base)
            var api = TxtApi.Text.Trim();
            if (api.Length == 0) Add(1, "⚠ Endereço do servidor vazio");
            else
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                try
                {
                    using var r = await http.GetAsync(api);
                    Add(0, $"✓ Servidor fiscal alcançável ({api})"); // qualquer resposta HTTP = rede ok
                }
                catch (Exception ex) { Add(2, $"✗ Servidor fiscal inalcançável: {ex.Message.Split('\n')[0]}"); }
            }

            // Pareamento NÃO entra nesta bateria: o botão de teste fica ANTES da
            // seção de pareamento (ele confere a COMUNICAÇÃO fiscal) — acusar "não
            // pareado" aqui era ruído óbvio. O status do pareamento vive na própria
            // seção (TxtStatusPareamento, atualizado no load e ao parear).

            // 6. Impressora (o teste REAL de papel é o botão acima)
            var imp = ImpressoraEscolhida();
            Add(0, imp is null ? "✓ Impressora: padrão do Windows" : $"✓ Impressora: {imp}");
            linhas.Add("   (papel/corte/QR: use o botão \"Imprimir cupom de teste\")");
        }
        catch (Exception ex)
        {
            Add(2, "✗ Teste interrompido: " + ex.Message);
        }
        finally
        {
            TxtStatusTeste.Text = string.Join("\n", linhas);
            TxtStatusTeste.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[
                pior == 2 ? "Erro" : pior == 1 ? "TextoFraco" : "Ok"];
            BtnTestarTudo.IsEnabled = true;
        }
    }

    private void Salvar(object sender, RoutedEventArgs e)
    {
        try
        {
            var loja = TxtLoja.Text.Trim();
            var cnpj = new string(TxtCnpj.Text.Where(char.IsDigit).ToArray());
            if (loja.Length < 2) throw new InvalidOperationException("Informe o nome da loja.");
            if (cnpj.Length != 14) throw new InvalidOperationException("O CNPJ precisa ter 14 dígitos.");
            // Dígito verificador AQUI, não na Rejeição 207 da SEFAZ com cliente no balcão.
            if (!Documentos.CnpjValido(cnpj))
                throw new InvalidOperationException("CNPJ inválido — os dígitos verificadores não conferem.");
            if (!int.TryParse(TxtSerie.Text.Trim(), out var serie) || serie < 1 || serie > 999)
                throw new InvalidOperationException("Série deve ser um número de 1 a 999.");
            // Índice 2 = "Sem emissão — só recibo": a venda NÃO chama o emissor e o papel
            // sai como recibo (SEM VALOR FISCAL). O ambiente da SEFAZ fica em homologação
            // por segurança — se religarem a emissão sem revisar, nada sobe pra produção.
            var modoRecibo = CmbAmbiente.SelectedIndex == 2;
            var ambiente = CmbAmbiente.SelectedIndex == 1 ? 1 : 2;

            // INTEGRAÇÃO É OBRIGATÓRIA (decisão do dono): sem parear com o painel,
            // vendas e notas ficariam presas neste PC — o Salvar não conclui.
            if (!LerSegredos().ContainsKey("nuvemEmail"))
                throw new InvalidOperationException(
                    "Pareamento obrigatório: gere o código de 6 dígitos no painel e use o botão " +
                    "\"Parear com o painel\" (última seção) antes de salvar.");

            using var cx = Banco.Abrir();
            using var tx = cx.BeginTransaction();

            var temAdmin = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM operador WHERE id='_admin_'", transaction: tx) > 0;
            var adminNasceuPadrao = false;

            // ADMINISTRADOR DA LOJA (dono) — só quando ainda não há nenhum operador.
            // Ele entra com TODOS os privilégios e a senha dele passa a ser a senha
            // desta tela de configuração (o "_admin_" espelha o hash do dono —
            // morre o 1234 padrão).
            if (BlocoOperador.Visibility == Visibility.Visible)
            {
                var nome = TxtOpNome.Text.Trim();
                var pin = TxtOpPin.Text.Trim();
                var cpfOp = Documentos.SoDigitos(TxtOpCpf.Text);
                if (nome.Length < 2) throw new InvalidOperationException("Informe o nome do administrador (dono).");
                // CPF é o login: sem ele, a abertura de caixa não tem dono de verdade
                if (!Documentos.CpfValido(cpfOp))
                    throw new InvalidOperationException("CPF do administrador inválido — ele é o login dele no caixa.");
                if (!Operadores.PinValido(pin)) throw new InvalidOperationException("A senha deve ter de 4 a 6 dígitos.");
                var (h, s) = Operadores.GerarHash(pin);
                cx.Execute("""
                    INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,cpf,ativo,atualizado)
                    VALUES (@Id,@N,@H,@S,'gerente',@Cpf,1,@Em)
                    """, new { Id = Guid.NewGuid().ToString(), N = nome, H = h, S = s, Cpf = cpfOp, Em = DateTime.Now.ToString("o") }, tx);
                // a senha do DONO é a senha da configuração (upsert do _admin_ espelhando o hash)
                cx.Execute("""
                    INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,ativo,atualizado)
                    VALUES ('_admin_',@N,@H,@S,'gerente',0,@Em)
                    ON CONFLICT(id) DO UPDATE SET nome=@N, pin_hash=@H, pin_salt=@S, atualizado=@Em
                    """, new { N = "Administrador (" + nome + ")", H = h, S = s, Em = DateTime.Now.ToString("o") }, tx);
                Caixa.Auditar(cx, tx, "admin_definido", null, null, $"dono {nome} — senha da configuração é a dele");
            }
            else if (!temAdmin)
            {
                // instalação sem bloco de operador (já havia operadores) e sem admin:
                // fallback raro — nasce 1234 pra tela não ficar aberta a qualquer um.
                var (h, s) = Operadores.GerarHash("1234");
                cx.Execute("""
                    INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,ativo,atualizado)
                    VALUES ('_admin_','Administrador',@H,@S,'gerente',0,@Em)
                    """, new { H = h, S = s, Em = DateTime.Now.ToString("o") }, tx);
                Caixa.Auditar(cx, tx, "senha_admin_padrao", null, null, "sem dono cadastrado — senha 1234");
                adminNasceuPadrao = true;
            }

            var agora = DateTime.Now.ToString("o");
            cx.Execute("""
                INSERT INTO terminal (id, terminal_uuid, loja_id, loja_nome, cnpj, serie_nfce, ambiente, api_base, criado_em)
                VALUES (1, @Uuid, @Loja, @Loja, @Cnpj, @Serie, @Amb, @Api, @Em)
                ON CONFLICT(id) DO UPDATE SET loja_nome=@Loja, cnpj=@Cnpj, serie_nfce=@Serie,
                                              ambiente=@Amb, api_base=@Api
                """,
                new { Uuid = Guid.NewGuid().ToString(), Loja = loja, Cnpj = cnpj, Serie = serie,
                      Amb = ambiente, Api = TxtApi.Text.Trim(), Em = agora }, tx);

            Caixa.Auditar(cx, tx, _jaConfigurado ? "config_alterada" : "config_inicial", null, null,
                $"serie={serie} ambiente={(ambiente == 1 ? "producao" : "homologacao")}");
            tx.Commit();

            // Impressora fora da tabela `terminal` porque `Migrar()` não faz ALTER: coluna
            // nova só existiria em máquina nova, e a que já roda ficaria sem.
            var impressora = ImpressoraEscolhida();
            if (impressora is null) cx.Execute("DELETE FROM config WHERE chave='impressora'");
            else Vendas.GravarConfig(cx, "impressora", impressora);
            Vendas.GravarConfig(cx, "modo_fiscal", modoRecibo ? "recibo" : "nfce");
            Vendas.GravarConfig(cx, "imprimir_automatico", ChkImprimirAuto.IsChecked == false ? "0" : "1");
            var impComanda = ImpressoraComandaEscolhida();
            if (impComanda is null) cx.Execute("DELETE FROM config WHERE chave='kds_comanda_impressora'");
            else Vendas.GravarConfig(cx, "kds_comanda_impressora", impComanda);
            Vendas.GravarConfig(cx, "kds_comanda_auto", ChkComandaAuto.IsChecked == true ? "1" : "0");
            GravarTef(cx);
            _tefSalvo = true;

            // Segredos fora do banco e cifrados pela máquina (DPAPI). Em produção o
            // certificado é obrigatório — sem ele não sai nota.
            var seg = LerSegredos();
            if (_pfxEscolhido is not null)
            {
                Directory.CreateDirectory(PastaSegredos);
                File.Copy(_pfxEscolhido, ArqCert, true);
            }
            if (TxtSenhaPfx.Password.Length > 0) seg["senhaPfx"] = TxtSenhaPfx.Password;
            if (TxtCsc.Password.Length > 0) seg["csc"] = TxtCsc.Password;
            seg["idCsc"] = TxtIdCsc.Text.Trim() is { Length: > 0 } i ? i : "000001";
            GravarSegredos(seg);

            if (!modoRecibo && ambiente == 1 && (!File.Exists(ArqCert) || !seg.ContainsKey("csc")))
            {
                Dialogo.Avisar(Window.GetWindow(this)!, "Falta o certificado",
                    "Salvo, mas sem o certificado e/ou o CSC o caixa não consegue emitir nota em produção.", "erro");
            }
            if (adminNasceuPadrao)
                Dialogo.Avisar(Window.GetWindow(this)!, "Senha desta tela: 1234",
                    "A senha de administrador nasceu com o padrão 1234. " +
                    "Troque assim que possível (pelo painel, quando existir, ou pelo suporte).", "ok");
            Concluiu?.Invoke();
        }
        catch (Exception ex)
        {
            TxtErro.Text = ex.Message;
        }
    }

    // ── TEF ─────────────────────────────────────────────────────────────────

    private static readonly string[] ChavesTef =
    {
        "tef_habilitado", "tef_provedor", "tef_paygo_pasta", "tef_paygo_registro", "tef_paygo_empresa",
        "tef_paygo_rede", "tef_paygo_rede_pix", "tef_paygo_imprimir_vias", "tef_perguntar_parcelas", "tef_serial_pos",
        "tef_cpay_ambiente", "tef_cpay_terminal", "tef_cpay_pessoa",
    };
    private readonly Dictionary<string, string?> _tefOriginal = new();
    private bool _tefGravadoPeloTeste;   // Testar/ADM gravaram sem Salvar
    private bool _tefSalvo;              // Salvar passou por GravarTef

    /// <summary>Voltar sem salvar depois de Testar/ADM: restaura as chaves TEF como estavam.</summary>
    private void RestaurarTefSeNaoSalvou()
    {
        if (!_tefGravadoPeloTeste || _tefSalvo) return;
        try
        {
            using var cx = Banco.Abrir();
            foreach (var (k, v) in _tefOriginal)
            {
                if (v is null) cx.Execute("DELETE FROM config WHERE chave=@C", new { C = k });
                else Vendas.GravarConfig(cx, k, v);
            }
            Servicos.RecarregarTef();
        }
        catch { /* melhor esforço: o pior caso é a config do teste ficar — e ela está na tela */ }
    }

    /// <summary>Bloqueia Salvar/Voltar/provedor enquanto um comando TEF (ATV/ADM) está em voo — trocar o provedor com o PayGo ocupado deixaria dois clientes na mesma pasta.</summary>
    private void TravarTef(bool ocupado)
    {
        BtnTestarPayGo.IsEnabled = !ocupado;
        BtnTestarCpay.IsEnabled = !ocupado;
        BtnSalvar.IsEnabled = !ocupado;
        BtnVoltar.IsEnabled = !ocupado;
        foreach (var op in OpcoesTef) op.IsEnabled = !ocupado;
    }

    /// <summary>
    /// Chaves `tef_*` em `config` — é exatamente o que Servicos.Tef() e Caixa.FormasContadas
    /// leem. Campo em branco APAGA a chave (o cliente usa o padrão dele). No fim, zera o cache
    /// do provedor: salvar sem isso era "salvou mas não mudou" até reiniciar o PDV.
    /// </summary>
    private void GravarTef(SqliteConnection cx)
    {
        var modo = TefModo;
        Vendas.GravarConfig(cx, "tef_habilitado", modo <= 0 ? "0" : "1");
        Vendas.GravarConfig(cx, "tef_provedor", modo switch { 2 => "paygo", 3 => "controlpay", _ => "nuvem" });
        void Chave(string chave, string valor)
        {
            valor = valor.Trim();
            if (valor.Length == 0) cx.Execute("DELETE FROM config WHERE chave=@C", new { C = chave });
            else Vendas.GravarConfig(cx, chave, valor);
        }
        // ControlPay: ambiente/terminal/pessoa em config; chave e senha técnica no cofre DPAPI
        Vendas.GravarConfig(cx, "tef_cpay_ambiente", CmbCpayAmbiente.SelectedIndex == 1 ? "producao" : "sandbox");
        Chave("tef_cpay_terminal", TxtCpayTerminal.Text);
        Chave("tef_cpay_pessoa", TxtCpayPessoa.Text);
        Chave("tef_cpay_adquirente", TxtCpayAdquirente.Text);
        Chave("tef_cpay_adquirente_pix", TxtCpayAdquirentePix.Text);
        {
            var segTef = LerSegredos();
            var chaveCpay = PwdCpayChave.Password.Trim();
            // aceita colada URL-encoded (%2f, %2b, %3d) — guardamos decodificada e codificamos ao enviar
            if (chaveCpay.Contains('%')) { try { chaveCpay = Uri.UnescapeDataString(chaveCpay); } catch { } }
            if (chaveCpay.Length > 0) segTef["cpayChave"] = chaveCpay; else segTef.Remove("cpayChave");
            var senhaTec = PwdCpaySenha.Password.Trim();
            if (senhaTec.Length > 0) segTef["cpaySenhaTecnica"] = senhaTec; else segTef.Remove("cpaySenhaTecnica");
            GravarSegredos(segTef);
        }
        Chave("tef_paygo_pasta", TxtPayGoPasta.Text);
        Chave("tef_paygo_registro", TxtPayGoRegistro.Text);
        Chave("tef_paygo_empresa", TxtPayGoEmpresa.Text);
        Chave("tef_paygo_rede", TxtPayGoRede.Text);
        Chave("tef_paygo_rede_pix", TxtPayGoRedePix.Text);
        Vendas.GravarConfig(cx, "tef_paygo_imprimir_vias", ChkPayGoVias.IsChecked == false ? "0" : "1");
        Vendas.GravarConfig(cx, "tef_perguntar_parcelas", ChkTefParcelas.IsChecked == true ? "1" : "0");
        Chave("tef_serial_pos", TxtTefSerial.Text);
        Servicos.RecarregarTef();
    }

    private void TefMudou(object sender, RoutedEventArgs e) => PintarBlocosTef();

    private RadioButton[] OpcoesTef => new[] { OpTefNenhum, OpTefNuvem, OpTefPayGo, OpTefControlPay };

    /// <summary>
    /// Qual TEF este caixa usa, no mesmo código que a config já gravava
    /// (0 nenhum · 1 nuvem · 2 PayGo · 3 ControlPay). Virou botão em vez de
    /// dropdown a pedido do dono: no balcão, ver as opções vale mais que escondê-las.
    /// </summary>
    private int TefModo
    {
        get => OpTefControlPay?.IsChecked == true ? 3
             : OpTefPayGo?.IsChecked == true ? 2
             : OpTefNuvem?.IsChecked == true ? 1 : 0;
        set
        {
            OpTefNenhum.IsChecked = value == 0;
            OpTefNuvem.IsChecked = value == 1;
            OpTefPayGo.IsChecked = value == 2;
            OpTefControlPay.IsChecked = value == 3;
        }
    }

    /// <summary>Revelação progressiva do bloco do provedor. Guard de null: o ComboBox dispara no InitializeComponent.</summary>
    private void PintarBlocosTef()
    {
        if (BlocoPayGo is null || BlocoTefNuvem is null || BlocoControlPay is null
            || BlocoTefOpcoes is null || OpTefControlPay is null) return;
        var modo = TefModo;
        BlocoPayGo.Visibility = modo == 2 ? Visibility.Visible : Visibility.Collapsed;
        BlocoControlPay.Visibility = modo == 3 ? Visibility.Visible : Visibility.Collapsed;
        BlocoTefNuvem.Visibility = modo == 1 ? Visibility.Visible : Visibility.Collapsed;
        BlocoTefOpcoes.Visibility = modo is 2 or 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Testa a conexão com a PayGo usando os campos DA TELA (instância efêmera, não grava
    /// nada) e explica o resultado em português de gente: quem vai cobrar, em qual
    /// maquininha, e o que pedir a eles se faltar algo. Nenhuma mensagem carrega a chave.
    /// </summary>
    private async void TestarControlPay(object sender, RoutedEventArgs e)
    {
        TravarTef(true);
        StatusTef("Falando com a PayGo…", null);
        try
        {
            var chave = PwdCpayChave.Password.Trim();
            if (chave.Contains('%')) { try { chave = Uri.UnescapeDataString(chave); } catch { } }
            if (chave.Length == 0) { StatusTef("Falta a chave de integração — pegue no portal do ControlPay, em Integrações.", "Erro"); return; }
            if (TxtCpayPessoa.Text.Trim().Length == 0) { StatusTef("Falta o ID da pessoa. Ele fica no portal do ControlPay, junto do seu login.", "Erro"); return; }

            var producao = CmbCpayAmbiente.SelectedIndex == 1;
            var ondeEstou = producao ? "produção" : "teste (sandbox)";
            using var cli = new ClienteControlPay(new OpcoesControlPay(
                OpcoesControlPay.UrlDoAmbiente(producao ? "producao" : "sandbox"),
                chave, PwdCpaySenha.Password.Trim(), TxtCpayTerminal.Text.Trim(), TxtCpayPessoa.Text.Trim()));
            var terminais = await cli.ListarTerminaisAsync(CancellationToken.None);

            if (terminais.Count == 0)
            {
                StatusTef($"A PayGo aceitou a chave, mas esta conta não tem nenhuma maquininha em {ondeEstou}." +
                    "\n\nO que fazer: peça à PayGo para vincular a maquininha da loja a esta conta.", "Erro");
                return;
            }

            // O campo pode ter sobrado de outra conta (o número do sandbox, por exemplo).
            // Nesse caso o certo é corrigir sozinho, e DIZER que corrigiu — deixar o número
            // velho ali só produz um "escolha um terminal" que ninguém entende.
            var digitado = TxtCpayTerminal.Text.Trim();
            var comMaquininha = terminais.Where(t => t.TerminalFisico is not null).ToList();
            var trocou = false;
            if (terminais.All(t => t.Id != digitado) && comMaquininha.Count == 1)
            {
                TxtCpayTerminal.Text = comMaquininha[0].Id;
                trocou = digitado.Length > 0;
            }
            var escolhido = terminais.FirstOrDefault(t => t.Id == TxtCpayTerminal.Text.Trim());

            var lista = string.Join("\n", terminais.Select(t =>
                $"   • {t.Id} — {t.Nome}" + (t.TerminalFisico is null
                    ? "  (sem maquininha vinculada)"
                    : $"  ·  maquininha {t.TerminalFisico}  ·  instalação {t.InstalacaoId}")));

            if (escolhido is null)
            {
                StatusTef($"Conectado à PayGo em {ondeEstou}. Encontrei {(terminais.Count == 1 ? "1 caixa" : terminais.Count + " caixas")} nesta conta:\n\n" +
                    lista + "\n\nO que fazer: escreva um desses números no campo \"ID do terminal\" aqui em cima e salve.", "Erro");
                return;
            }

            // Pendências que só a PayGo resolve no cadastro dela — o PDV não contorna
            // nenhuma, então o texto tem que dizer exatamente o que pedir a eles.
            var pendencias = new List<string>();
            if (escolhido.TerminalFisico is null)
                pendencias.Add("• Este caixa não tem maquininha vinculada, então a cobrança não tem para onde ir.\n" +
                               "  Peça à PayGo para vincular a maquininha da loja a este terminal.");
            if (!escolhido.AguardaTef)
                pendencias.Add("• A PayGo não ligou o \"aguarda TEF\" neste caixa. Sem isso a cobrança sai daqui\n" +
                               "  mas não acende na maquininha: a venda fica esperando e acaba expirando.\n" +
                               "  Peça à PayGo para ligar o \"aguarda TEF\".");
            if (!escolhido.VendaPorValor)
                pendencias.Add("• A \"venda por valor\" está desligada neste caixa. É ela que deixa o PDV mandar\n" +
                               "  o valor da compra. Peça à PayGo para ligar.");

            var cabeca = $"Conectado à PayGo em {ondeEstou}.\n\n" +
                $"Este caixa vai cobrar pelo terminal {escolhido.Id} ({escolhido.Nome})" +
                (escolhido.TerminalFisico is null
                    ? ".\n"
                    : $",\nna maquininha {escolhido.TerminalFisico}, da instalação {escolhido.InstalacaoId}.\n") +
                (trocou ? $"\nObs.: o campo estava com o número {digitado}, que não existe nesta conta — troquei pelo certo.\n" : "");

            if (pendencias.Count == 0)
            {
                StatusTef(cabeca + "\nEstá tudo pronto para cobrar no cartão. Clique em Salvar para manter." +
                    (producao ? "" : "\n\nAtenção: você ainda está no ambiente de TESTE. Nenhuma cobrança aqui é de verdade."), "Ok");
                return;
            }

            StatusTef(cabeca + "\nFalta a PayGo acertar isto no cadastro deles:\n\n" +
                string.Join("\n\n", pendencias) +
                "\n\nPode salvar assim mesmo: o número do caixa fica guardado e volta a funcionar\nassim que eles acertarem.", "Erro");
        }
        catch (Exception ex) { StatusTef("Não consegui falar com a PayGo: " + ex.Message, "Erro"); }
        finally { TravarTef(false); }
    }

    /// <summary>
    /// ATV contra a pasta digitada. Grava as chaves TEF antes (o teste é do que está NA TELA,
    /// como o de impressão) — o Salvar exige pareamento/CNPJ e não pode ser pré-requisito de
    /// um teste de comunicação.
    /// </summary>
    private async void TestarPayGo(object sender, RoutedEventArgs e)
    {
        TravarTef(true);
        StatusTef("Chamando o PayGo (ATV)… até 7 s.", null);
        try
        {
            if (TefModo != 2) { StatusTef("Selecione \"PayGo Windows\" acima para testar.", "Erro"); return; }
            using (var cx = Banco.Abrir()) GravarTef(cx);
            _tefGravadoPeloTeste = true;
            if (Servicos.PayGo() is not { } cli) { StatusTef("TEF desligado — selecione o PayGo e tente de novo.", "Erro"); return; }
            var ok = await cli.AtivoAsync(CancellationToken.None);
            StatusTef(ok
                ? $"✓ PayGo respondeu em {cli.PastaReq} — pronto para cobrar. (Salve para manter esta configuração.)"
                : $"✗ {ClientePayGo.MsgTefNaoResponde} Pasta: {cli.PastaReq}. Confira se o PayGo Windows está aberto e se a pasta é a mesma configurada nele.",
                ok ? "Ok" : "Erro");
        }
        catch (Exception ex) { StatusTef("✗ " + ex.Message, "Erro"); }
        finally { TravarTef(false); }
    }

    private void StatusTef(string texto, string? tom)
    {
        TxtStatusTef.Text = texto;
        TxtStatusTef.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[tom ?? "TextoFraco"];
    }

    /// <summary>
    /// Revelação progressiva: "Sem emissão — só recibo" esconde certificado/CSC/ID
    /// (não fazem sentido sem NFC-e). O guard de null cobre o disparo que o WPF dá
    /// durante o InitializeComponent, antes de BlocoCertificado existir.
    /// </summary>
    private void AmbienteMudou(object sender, SelectionChangedEventArgs e)
    {
        if (BlocoCertificado is null) return;
        BlocoCertificado.Visibility = CmbAmbiente.SelectedIndex == 2
            ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── tema ────────────────────────────────────────────────────────────────
    private bool _carregandoTema;

    private void TemaSelecionado(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_carregandoTema || BlocoJanelaTema is null) return;
        BlocoJanelaTema.Visibility = CboTema.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        using var cx = Banco.Abrir();
        Vendas.GravarConfig(cx, "tema", CboTema.SelectedIndex switch { 1 => "claro", 2 => "auto", _ => "escuro" });
        Aparencia.Aplicar(Aparencia.Resolver(cx));   // preview imediato: ver é decidir
    }

    private void JanelaTemaMudou(object sender, RoutedEventArgs e)
    {
        if (_carregandoTema) return;
        using var cx = Banco.Abrir();
        Vendas.GravarConfig(cx, "tema_claro_de", TxtTemaDe.Text.Trim());
        Vendas.GravarConfig(cx, "tema_claro_ate", TxtTemaAte.Text.Trim());
        if (CboTema.SelectedIndex == 2) Aparencia.Aplicar(Aparencia.Resolver(cx));
    }

    private void Voltar(object sender, RoutedEventArgs e)
    {
        RestaurarTefSeNaoSalvou();
        Concluiu?.Invoke();
    }
}
