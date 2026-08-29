using System.IO;
using System.IO.Compression;
using System.Text;

namespace Pdv.Instalador;

/// <summary>
/// O PAYLOAD VIAJA NA CAUDA DO PRÓPRIO EXE.
///
/// O instalador precisa entregar ~265 MB (a pasta do PDV, 165 MB, e o paygo.exe,
/// 108 MB) num arquivo só — quem instala está numa loja, baixa uma vez, e não pode
/// receber uma pasta com instruções de "mantenha os arquivos juntos".
///
/// Duas formas foram medidas antes de escolher. Embutir como recurso do .NET FUNCIONA
/// (não estoura o compilador), mas custa 55% mais tempo de build, 59% mais disco no
/// projeto e 155 MB a mais de memória no PC da loja durante a instalação — para
/// entregar um exe do mesmo tamanho. A cauda ganhou, e ainda deixa trocar o payload
/// sem recompilar nada.
///
/// POR QUE ANEXAR NÃO QUEBRA O EXE: o host de arquivo único do .NET acha o bundle por
/// um marcador gravado DENTRO do host nativo, com o offset absoluto do cabeçalho — ele
/// nunca procura nada a partir do fim do arquivo. Medido: um Pdv.exe com 276 MB de
/// cauda (412 MB no total) abre e roda igual.
///
/// ⚠️ ASSINATURA DIGITAL É INCOMPATÍVEL COM ISTO, e é a única armadilha séria. Anexar
/// bytes invalida o Authenticode (testado: um exe assinado vira NotSigned). Hoje não
/// morde — nada aqui é assinado. Se um dia for, assinar DEPOIS de anexar põe o bloco
/// de certificado no fim do arquivo e o trailer deixa de ser os últimos 32 bytes;
/// nesse dia, ou se lê o diretório de segurança do PE para achar onde o trailer parou,
/// ou se volta para o recurso embutido, que é imune.
///
/// ⚠️ E O ZIP É LIDO SOBRE UMA FATIA, nunca sobre o arquivo inteiro. Entregar o
/// FileStream do exe para o ZipArchive falha com "Number of entries expected in End Of
/// Central Directory does not correspond" — o zip tem offsets relativos ao começo DELE,
/// não ao começo do exe. <see cref="Fatia"/> não é elegância: é requisito.
/// </summary>
public static class Pacote
{
    private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("PDVPAY01");
    public const int TamanhoTrailer = 32;
    public const uint VersaoFormato = 1;

    /// <summary>Onde o payload começa dentro do exe, quanto ele tem, e como conferir.</summary>
    public sealed record Cauda(long Offset, long Tamanho, uint Crc, uint Versao);

    // ------------------------------------------------------------- gravar

    /// <summary>Os 32 bytes do fim, tudo little-endian. Escrito e lido pelo MESMO
    /// código de propósito: formato de arquivo com duas implementações diverge.</summary>
    public static byte[] MontarTrailer(long offset, long tamanho, uint crc)
    {
        var t = new byte[TamanhoTrailer];
        MagicBytes.CopyTo(t, 0);
        BitConverter.GetBytes(offset).CopyTo(t, 8);
        BitConverter.GetBytes(tamanho).CopyTo(t, 16);
        BitConverter.GetBytes(crc).CopyTo(t, 24);
        BitConverter.GetBytes(VersaoFormato).CopyTo(t, 28);
        return t;
    }

    /// <summary>
    /// Monta o instalador final: uma cópia deste exe + o zip do payload + o trailer.
    /// Chamado pelo build (<c>--empacotar</c>), não pela loja.
    /// </summary>
    public static string? Empacotar(string exeBase, string pastaPdv, string? paygoExe, string saida,
                                    Action<string>? progresso = null)
    {
        try
        {
            if (!File.Exists(exeBase)) return "Não achei o exe base: " + exeBase;
            if (Instalacao.ConferirOrigem(pastaPdv) is { } ruim) return "Payload recusado: " + ruim;

            var zipTmp = Path.Combine(Path.GetTempPath(), "pdvpay-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
            try
            {
                progresso?.Invoke("Compactando o PDV…");
                using (var zip = ZipFile.Open(zipTmp, ZipArchiveMode.Create))
                {
                    foreach (var rel in Instalacao.ArquivosParaCopiar(pastaPdv))
                        zip.CreateEntryFromFile(Path.Combine(pastaPdv, rel),
                            "pdv/" + rel.Replace('\\', '/'), NivelDe(rel));

                    if (paygoExe is not null && File.Exists(paygoExe))
                    {
                        progresso?.Invoke("Juntando o PayGo…");
                        // ⚠️ SEM COMPRESSÃO. Medido: o paygo.exe cai de 108,5 para
                        // 108,0 MB e cobra ~9 s de build por isso. Ele já vem
                        // comprimido por dentro; insistir é gastar tempo à toa.
                        zip.CreateEntryFromFile(paygoExe, "paygo.exe", CompressionLevel.NoCompression);
                    }
                }

                progresso?.Invoke("Montando o instalador…");
                File.Copy(exeBase, saida, overwrite: true);
                long offset;
                uint crc;
                using (var fs = new FileStream(saida, FileMode.Open, FileAccess.ReadWrite))
                {
                    fs.Seek(0, SeekOrigin.End);
                    offset = fs.Position;
                    using (var zs = File.OpenRead(zipTmp))
                    {
                        crc = Crc32(zs);
                        zs.Position = 0;
                        zs.CopyTo(fs);
                    }
                    fs.Write(MontarTrailer(offset, fs.Length - offset, crc));
                }
                progresso?.Invoke($"Pronto: {new FileInfo(saida).Length / 1024 / 1024} MB.");
                return null;
            }
            finally { try { File.Delete(zipTmp); } catch { } }
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>O Pdv.exe é 156 MB e encolhe para ~63; o resto são DLLs pequenas.
    /// Comprimir o que já é pequeno não paga o tempo de build.</summary>
    private static CompressionLevel NivelDe(string rel) =>
        new FileInfo(rel).Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            ? CompressionLevel.Optimal
            : CompressionLevel.Fastest;

    // --------------------------------------------------------------- ler

    /// <summary>
    /// Lê o trailer do fim do stream. Devolve null quando não há payload — e isso é
    /// situação NORMAL: o exe recém-publicado, antes de empacotar, não tem cauda.
    /// </summary>
    public static Cauda? LerTrailer(Stream s)
    {
        if (!s.CanSeek || s.Length < TamanhoTrailer) return null;
        s.Seek(-TamanhoTrailer, SeekOrigin.End);
        var t = new byte[TamanhoTrailer];
        s.ReadExactly(t, 0, TamanhoTrailer);

        for (var i = 0; i < MagicBytes.Length; i++)
            if (t[i] != MagicBytes[i]) return null;

        var offset = BitConverter.ToInt64(t, 8);
        var tamanho = BitConverter.ToInt64(t, 16);
        var crc = BitConverter.ToUInt32(t, 24);
        var versao = BitConverter.ToUInt32(t, 28);

        // A geometria tem que fechar com o tamanho do arquivo. Sem esta conferência,
        // um exe truncado no meio do download viraria um Seek para lugar nenhum e um
        // erro incompreensível na tela de quem está instalando.
        if (offset <= 0 || tamanho <= 0 || offset + tamanho + TamanhoTrailer != s.Length)
            return null;

        return new Cauda(offset, tamanho, crc, versao);
    }

    public static Cauda? LerTrailerDoProprioExe()
    {
        try
        {
            var eu = Environment.ProcessPath;
            if (eu is null) return null;
            using var fs = new FileStream(eu, FileMode.Open, FileAccess.Read, FileShare.Read);
            return LerTrailer(fs);
        }
        catch { return null; }
    }

    public static bool TemPayload() => LerTrailerDoProprioExe() is not null;

    /// <summary>
    /// Extrai o payload para <paramref name="destino"/>. Devolve null no sucesso.
    /// Depois disso, <c>destino\pdv</c> é a pasta do PDV e <c>destino\paygo.exe</c> é
    /// o instalador do TEF (quando veio junto).
    ///
    /// <paramref name="deQualExe"/> existe para PODER SER TESTADO e para o build
    /// conferir o exe que acabou de gerar: sem ele, a extração só saberia ler a si
    /// mesma, e a única forma de descobrir que um pacote saiu quebrado seria uma loja
    /// clicando nele.
    /// </summary>
    public static string? Extrair(string destino, Action<string>? progresso = null, string? deQualExe = null)
    {
        var eu = deQualExe ?? Environment.ProcessPath;
        if (eu is null) return "Não consegui localizar o próprio instalador.";
        try
        {
            using var fs = new FileStream(eu, FileMode.Open, FileAccess.Read, FileShare.Read);
            var cauda = LerTrailer(fs);
            if (cauda is null) return "Este instalador veio sem o programa dentro.";

            Directory.CreateDirectory(destino);
            using var fatia = new Fatia(fs, cauda.Offset, cauda.Tamanho);
            using var zip = new ZipArchive(fatia, ZipArchiveMode.Read);

            var total = zip.Entries.Count;
            var n = 0;
            foreach (var e in zip.Entries)
            {
                if (string.IsNullOrEmpty(e.Name)) continue; // entrada de pasta
                // Caminho de dentro do zip nunca sai da pasta de destino: um ".." numa
                // entrada escreveria em qualquer lugar do disco com privilégio de admin.
                var alvo = Path.GetFullPath(Path.Combine(destino, e.FullName));
                if (!alvo.StartsWith(Path.GetFullPath(destino), StringComparison.OrdinalIgnoreCase))
                    return "O pacote deste instalador está corrompido.";

                Directory.CreateDirectory(Path.GetDirectoryName(alvo)!);
                e.ExtractToFile(alvo, overwrite: true);
                progresso?.Invoke($"Abrindo o pacote… {++n} de {total}");
            }
            return null;
        }
        catch (Exception ex) { return "Não consegui abrir o pacote: " + ex.Message; }
    }

    // -------------------------------------------------------------- apoio

    /// <summary>
    /// Janela somente-leitura sobre um pedaço de outro stream. Ver o ⚠️ do topo:
    /// sem ela o ZipArchive lê o exe inteiro como se fosse o zip e falha na conta das
    /// entradas do diretório central.
    /// </summary>
    private sealed class Fatia : Stream
    {
        private readonly Stream _base;
        private readonly long _offset, _tamanho;
        private long _pos;

        public Fatia(Stream b, long offset, long tamanho)
        {
            _base = b; _offset = offset; _tamanho = tamanho;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _tamanho;
        public override long Position { get => _pos; set => _pos = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _tamanho) return 0;
            count = (int)Math.Min(count, _tamanho - _pos);
            _base.Position = _offset + _pos;
            var lidos = _base.Read(buffer, offset, count);
            _pos += lidos;
            return lidos;
        }

        public override long Seek(long v, SeekOrigin origem)
        {
            _pos = origem switch
            {
                SeekOrigin.Begin => v,
                SeekOrigin.Current => _pos + v,
                _ => _tamanho + v,
            };
            return _pos;
        }

        public override void Flush() { }
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    /// <summary>CRC32 do payload — para o instalador saber que baixou inteiro antes de
    /// tentar descompactar. Tabela montada na hora: são 256 entradas.</summary>
    public static uint Crc32(Stream s)
    {
        var tabela = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            tabela[i] = c;
        }
        var crc = 0xFFFFFFFFu;
        var buf = new byte[81920];
        int lidos;
        while ((lidos = s.Read(buf, 0, buf.Length)) > 0)
            for (var i = 0; i < lidos; i++)
                crc = tabela[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
