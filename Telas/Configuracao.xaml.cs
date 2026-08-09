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

        if (_jaConfigurado)
        {
            var t = cx.QueryFirst("SELECT loja_nome, cnpj, serie_nfce, ambiente, api_base FROM terminal LIMIT 1");
            TxtLoja.Text = (string)t.loja_nome;
            TxtCnpj.Text = (string)t.cnpj;
            TxtSerie.Text = ((long)t.serie_nfce).ToString();
            CmbAmbiente.SelectedIndex = (long)t.ambiente == 1 ? 1 : 0;
            TxtApi.Text = t.api_base as string ?? "";
            BtnVoltar.Visibility = Visibility.Visible;
            // reabrir vem preenchido: reconfigurar não pode ser redigitar tudo
            var seg = LerSegredos();
            TxtSenhaPfx.Password = seg.GetValueOrDefault("senhaPfx", "");
            TxtCsc.Password = seg.GetValueOrDefault("csc", "");
            TxtIdCsc.Text = seg.GetValueOrDefault("idCsc", "000001");
            if (File.Exists(ArqCert)) { TxtPfx.Text = "cert.pfx (já configurado)"; ConferirCert(this, new RoutedEventArgs()); }
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

    /// <summary>Abre o .pfx com a senha digitada — pega senha errada aqui, não na 1ª venda.</summary>
    private void ConferirCert(object sender, RoutedEventArgs e)
    {
        var caminho = _pfxEscolhido ?? (File.Exists(ArqCert) ? ArqCert : null);
        if (caminho is null || TxtSenhaPfx.Password.Length == 0) { TxtStatusCert.Text = ""; return; }
        try
        {
            using var c = new X509Certificate2(caminho, TxtSenhaPfx.Password);
            var dias = (int)(c.NotAfter - DateTime.Now).TotalDays;
            TxtStatusCert.Text = dias < 0
                ? $"✗ Certificado VENCIDO em {c.NotAfter:dd/MM/yyyy} — a SEFAZ recusa as notas."
                : $"✓ {c.GetNameInfo(X509NameType.SimpleName, false)} · válido até {c.NotAfter:dd/MM/yyyy}"
                  + (dias <= 30 ? $" (faltam {dias} dias)" : "");
            TxtStatusCert.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[dias < 0 ? "Erro" : "Ok"];
        }
        catch
        {
            TxtStatusCert.Text = "✗ Senha incorreta para este certificado.";
            TxtStatusCert.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Erro"];
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
            if (!int.TryParse(TxtSerie.Text.Trim(), out var serie) || serie < 1 || serie > 999)
                throw new InvalidOperationException("Série deve ser um número de 1 a 999.");
            var ambiente = CmbAmbiente.SelectedIndex == 1 ? 1 : 2;

            using var cx = Banco.Abrir();
            using var tx = cx.BeginTransaction();

            // Senha de admin não se digita mais aqui (vai ser gerida pelo painel).
            // Na 1ª instalação nasce com o padrão 1234 — trocável pelo comando de
            // suporte (`dotnet run -- admin X`) ou, adiante, pelo painel. Não nascer
            // com senha nenhuma deixaria a tela de configuração aberta a qualquer um.
            var temAdmin = cx.ExecuteScalar<int>("SELECT COUNT(*) FROM operador WHERE id='_admin_'", transaction: tx) > 0;
            if (!temAdmin)
            {
                var (h, s) = Operadores.GerarHash("1234");
                cx.Execute("""
                    INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,ativo,atualizado)
                    VALUES ('_admin_','Administrador',@H,@S,'gerente',0,@Em)
                    """, new { H = h, S = s, Em = DateTime.Now.ToString("o") }, tx);
                Caixa.Auditar(cx, tx, "senha_admin_padrao", null, null, "primeira instalação — senha 1234");
            }

            // primeiro operador (só quando ainda não há nenhum)
            if (BlocoOperador.Visibility == Visibility.Visible)
            {
                var nome = TxtOpNome.Text.Trim();
                var pin = TxtOpPin.Text.Trim();
                var cpfOp = Documentos.SoDigitos(TxtOpCpf.Text);
                if (nome.Length < 2) throw new InvalidOperationException("Informe o nome do primeiro operador.");
                // CPF é o login do funcionário: sem ele, a abertura de caixa não tem dono de verdade
                if (!Documentos.CpfValido(cpfOp))
                    throw new InvalidOperationException("CPF do operador inválido — ele é o login dele no caixa.");
                if (!Operadores.PinValido(pin)) throw new InvalidOperationException("A senha deve ter de 4 a 6 dígitos.");
                var (h, s) = Operadores.GerarHash(pin);
                cx.Execute("""
                    INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,cpf,ativo,atualizado)
                    VALUES (@Id,@N,@H,@S,'gerente',@Cpf,1,@Em)
                    """, new { Id = Guid.NewGuid().ToString(), N = nome, H = h, S = s, Cpf = cpfOp, Em = DateTime.Now.ToString("o") }, tx);
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

            if (ambiente == 1 && (!File.Exists(ArqCert) || !seg.ContainsKey("csc")))
            {
                Dialogo.Avisar(Window.GetWindow(this)!, "Falta o certificado",
                    "Salvo, mas sem o certificado e/ou o CSC o caixa não consegue emitir nota em produção.", "erro");
            }
            if (!temAdmin)
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

    private void Voltar(object sender, RoutedEventArgs e) => Concluiu?.Invoke();
}
