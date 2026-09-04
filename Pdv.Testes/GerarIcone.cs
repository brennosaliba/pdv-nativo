using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pdv.Testes;

/// <summary>
/// Gera o icone do exe (mmtech) SEM depender de ImageMagick: desenha o conceito
/// escolhido pelo dono (Opcao B, "mm" grafite com barra teal) em WPF, em varios
/// tamanhos, e empacota tudo num .ico multi-resolucao (frames PNG, suportados pelo
/// Windows 10/11). Cada tamanho e desenhado do zero para ficar nitido no seu tamanho.
///
/// Uso: Pdv.Testes.exe --gerar-icone saida.ico
///
/// O desenho tem que bater com o preview aprovado: fundo grafite arredondado,
/// "mm" branco em negrito, barra teal por baixo.
/// </summary>
public static class GerarIcone
{
    // As cores do conceito B. Fixas (marca), nao seguem tema.
    private static readonly Color Grafite = Color.FromRgb(0x0F, 0x17, 0x2A);
    private static readonly Color Teal = Color.FromRgb(0x14, 0xB8, 0xA6);
    private static readonly Color Branco = Colors.White;

    // Tamanhos que o Windows usa (lista, taskbar, atalho grande, alt-tab).
    private static readonly int[] Tamanhos = { 16, 24, 32, 48, 64, 128, 256 };

    public static int Rodar(string saida)
    {
        var codigo = 1;
        var t = new Thread(() =>
        {
            try { codigo = Empacotar(saida); }
            catch (Exception ex) { Console.Error.WriteLine("gerar-icone: " + ex); codigo = 1; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return codigo;
    }

    private static int Empacotar(string saida)
    {
        var frames = new List<byte[]>();
        foreach (var s in Tamanhos) frames.Add(DesenharPng(s));

        // ICO: ICONDIR (6) + N x ICONDIRENTRY (16) + N blobs PNG.
        using var fs = File.Create(saida);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0);                 // reservado
        w.Write((ushort)1);                 // tipo: 1 = icone
        w.Write((ushort)frames.Count);      // quantas imagens

        var offset = 6 + frames.Count * 16; // onde comeca o 1o blob
        for (var i = 0; i < frames.Count; i++)
        {
            var s = Tamanhos[i];
            w.Write((byte)(s >= 256 ? 0 : s)); // largura (0 = 256)
            w.Write((byte)(s >= 256 ? 0 : s)); // altura  (0 = 256)
            w.Write((byte)0);                  // paleta (0 = sem paleta)
            w.Write((byte)0);                  // reservado
            w.Write((ushort)1);                // planos
            w.Write((ushort)32);               // bits por pixel
            w.Write((uint)frames[i].Length);   // tamanho do blob
            w.Write((uint)offset);             // deslocamento do blob
            offset += frames[i].Length;
        }
        foreach (var blob in frames) w.Write(blob);
        w.Flush();

        Console.WriteLine($"icone gerado: {saida} ({frames.Count} tamanhos, {new FileInfo(saida).Length} bytes)");
        return 0;
    }

    /// <summary>Desenha o conceito B no tamanho pedido e devolve o PNG.</summary>
    private static byte[] DesenharPng(int s)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Fundo arredondado grafite, ocupando o quadrado inteiro.
            var raio = s * 0.22;
            dc.DrawRoundedRectangle(new SolidColorBrush(Grafite), null, new Rect(0, 0, s, s), raio, raio);

            // "mm" branco, negrito, centralizado um pouco acima do meio.
            var face = new Typeface(new FontFamily("Segoe UI, Arial"),
                FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var ft = new FormattedText("mm", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                face, s * 0.42, new SolidColorBrush(Branco), 1.0)
            { TextAlignment = TextAlignment.Center };
            // Origem do FormattedText e o topo-esquerda; centralizamos pelo bloco de texto.
            var cx = s / 2.0;
            var cy = s * 0.44;
            dc.DrawText(ft, new Point(cx, cy - ft.Height / 2));

            // Barra teal por baixo do "mm".
            var barW = s * 0.50;
            var barH = Math.Max(2, s * 0.065);
            var barX = (s - barW) / 2;
            var barY = s * 0.72;
            var barR = barH / 2;
            dc.DrawRoundedRectangle(new SolidColorBrush(Teal), null,
                new Rect(barX, barY, barW, barH), barR, barR);
        }

        var bmp = new RenderTargetBitmap(s, s, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
