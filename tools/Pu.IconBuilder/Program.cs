using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

if (args.Length != 3)
{
    Console.Error.WriteLine("用法: Pu.IconBuilder <source.png> <transparent.png> <output.ico>");
    return 2;
}

var sourcePath = Path.GetFullPath(args[0]);
var logoPath = Path.GetFullPath(args[1]);
var iconPath = Path.GetFullPath(args[2]);

using var source = new Bitmap(sourcePath);
using var transparent = RemoveWhiteMatte(source);
var crop = FindInkBounds(transparent);
using var logo = ResizeSquare(transparent, crop, 512);

Directory.CreateDirectory(Path.GetDirectoryName(logoPath)!);
logo.Save(logoPath, ImageFormat.Png);
WriteIcon(logo, iconPath);

Console.WriteLine($"透明 Logo：{logoPath}（512×512）");
Console.WriteLine($"应用图标：{iconPath}（16–256 px）");
return 0;

static Bitmap RemoveWhiteMatte(Bitmap source)
{
    var output = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
    for (var y = 0; y < source.Height; y++)
    {
        for (var x = 0; x < source.Width; x++)
        {
            var pixel = source.GetPixel(x, y);
            var distance = 255 - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
            var blueSignal = pixel.B - Math.Max(pixel.R, pixel.G);
            var matteAlpha = distance <= 5 ? 0 : Math.Clamp((distance - 5) * 255 / 72, 0, 255);
            var blueAlpha = blueSignal <= 2 ? 0 : Math.Clamp((blueSignal - 2) * 255 / 72, 0, 255);
            var alpha = Math.Min(matteAlpha, blueAlpha);
            if (alpha == 0)
            {
                output.SetPixel(x, y, Color.Transparent);
                continue;
            }

            // 原图边缘已经与白色背景混合；反算一次白色蒙版，避免透明边缘出现白边。
            var r = Unmatte(pixel.R, alpha);
            var g = Unmatte(pixel.G, alpha);
            var b = Unmatte(pixel.B, alpha);
            output.SetPixel(x, y, Color.FromArgb(alpha, r, g, b));
        }
    }
    return output;
}

static int Unmatte(int channel, int alpha)
{
    if (alpha >= 255) return channel;
    var value = (channel * 255 - 255 * (255 - alpha)) / Math.Max(1, alpha);
    return Math.Clamp(value, 0, 255);
}

static Rectangle FindInkBounds(Bitmap bitmap)
{
    var left = bitmap.Width;
    var top = bitmap.Height;
    var right = -1;
    var bottom = -1;
    for (var y = 0; y < bitmap.Height; y++)
    {
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, y).A < 20) continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }
    }

    if (right < left || bottom < top) throw new InvalidOperationException("原图中没有找到可见笔触");

    var inkWidth = right - left + 1;
    var inkHeight = bottom - top + 1;
    var side = Math.Max(inkWidth, inkHeight);
    var padding = Math.Max(24, (int)Math.Round(side * 0.09));
    side = Math.Min(Math.Min(bitmap.Width, bitmap.Height), side + padding * 2);
    var centerX = (left + right) / 2;
    var centerY = (top + bottom) / 2;
    var x0 = Math.Clamp(centerX - side / 2, 0, bitmap.Width - side);
    var y0 = Math.Clamp(centerY - side / 2, 0, bitmap.Height - side);
    return new Rectangle(x0, y0, side, side);
}

static Bitmap ResizeSquare(Bitmap source, Rectangle crop, int size)
{
    var output = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    output.SetResolution(96, 96);
    using var graphics = Graphics.FromImage(output);
    graphics.Clear(Color.Transparent);
    graphics.CompositingMode = CompositingMode.SourceCopy;
    graphics.CompositingQuality = CompositingQuality.HighQuality;
    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    graphics.SmoothingMode = SmoothingMode.HighQuality;
    graphics.DrawImage(source, new Rectangle(0, 0, size, size), crop, GraphicsUnit.Pixel);
    return output;
}

static void WriteIcon(Bitmap source, string outputPath)
{
    int[] sizes = [16, 20, 24, 32, 48, 64, 128, 256];
    var entries = new List<(int Size, byte[] Png)>();
    foreach (var size in sizes)
    {
        using var bitmap = ResizeSquare(source, new Rectangle(0, 0, source.Width, source.Height), size);
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        entries.Add((size, png.ToArray()));
    }

    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write((short)0);
    writer.Write((short)1);
    writer.Write((short)entries.Count);
    var offset = 6 + 16 * entries.Count;
    foreach (var entry in entries)
    {
        var dimension = entry.Size >= 256 ? (byte)0 : (byte)entry.Size;
        writer.Write(dimension);
        writer.Write(dimension);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(entry.Png.Length);
        writer.Write(offset);
        offset += entry.Png.Length;
    }
    foreach (var entry in entries) writer.Write(entry.Png);
    File.WriteAllBytes(outputPath, stream.ToArray());
}
