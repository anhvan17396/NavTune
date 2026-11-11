using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NavTune.Infrastructure;

// Renders the NavTune icon at every size Windows asks for and packs the PNGs into one .ico file.
internal static class Program
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: IconGen <output.ico> [preview.png]");
            return 1;
        }
        var images = Sizes.Select(size => (size, Png(Render(size)))).ToList();
        using (var output = File.Create(args[0]))
        using (var writer = new BinaryWriter(output))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)images.Count);
            int offset = 6 + 16 * images.Count;
            foreach (var (size, data) in images)
            {
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(data.Length);
                writer.Write(offset);
                offset += data.Length;
            }
            foreach (var (_, data) in images) writer.Write(data);
        }
        if (args.Length > 1) File.WriteAllBytes(args[1], Png(Preview()));
        Console.WriteLine($"Wrote {args[0]} ({string.Join(", ", Sizes)} px)");
        return 0;
    }

    private static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) BrandIcon.Draw(dc, size);
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    /// <summary>All sizes side by side, enabled and disabled, on light and dark backgrounds.</summary>
    private static BitmapSource Preview()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 700, 300));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 32)), null, new Rect(0, 150, 700, 150));
            for (int row = 0; row < 2; row++)
            {
                double x = 10;
                foreach (int size in Sizes)
                {
                    dc.PushTransform(new TranslateTransform(x, row * 150 + 10));
                    BrandIcon.Draw(dc, size, enabled: true);
                    dc.Pop();
                    if (size <= 32)
                    {
                        dc.PushTransform(new TranslateTransform(x, row * 150 + 110));
                        BrandIcon.Draw(dc, size, enabled: false);
                        dc.Pop();
                    }
                    x += Math.Min(size, 128) + 10;
                }
            }
        }
        var bitmap = new RenderTargetBitmap(700, 300, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static byte[] Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
