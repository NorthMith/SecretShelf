// Значок SecretShelf: синий скруглённый квадрат, белая «шторка» у правого края с замком.
// Пишет .ico с PNG-кадрами всех нужных Windows размеров и превью-картинку.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

string outIco = args[0], outPreview = args[1];
int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
var frames = sizes.Select(s => (s, Png(Draw(s)))).ToList();

using (var fs = File.Create(outIco))
using (var w = new BinaryWriter(fs))
{
    w.Write((short)0); w.Write((short)1); w.Write((short)frames.Count);
    int offset = 6 + 16 * frames.Count;
    foreach (var (s, png) in frames)
    {
        w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
        w.Write(png.Length); w.Write(offset); offset += png.Length;
    }
    foreach (var (_, png) in frames) w.Write(png);
}

// Превью: все размеры в ряд на светлом и тёмном фоне.
using (var prev = new Bitmap(760, 300))
using (var g = Graphics.FromImage(prev))
{
    g.FillRectangle(new SolidBrush(Color.FromArgb(246, 246, 243)), 0, 0, 760, 150);
    g.FillRectangle(new SolidBrush(Color.FromArgb(32, 32, 32)), 0, 150, 760, 150);
    int x = 10;
    foreach (var s in new[] { 16, 24, 32, 48, 64, 128 })
    {
        using var b1 = Draw(s); using var b2 = Draw(s);
        g.DrawImage(b1, x, 75 - s / 2, s, s); g.DrawImage(b2, x, 225 - s / 2, s, s);
        x += s + 30;
    }
    prev.Save(outPreview, ImageFormat.Png);
}
Console.WriteLine("ok");

static Bitmap Draw(int size)
{
    var bmp = new Bitmap(size, size);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    float k = size / 32f;

    using (var bg = Round(new RectangleF(0.5f * k, 0.5f * k, 31 * k, 31 * k), 7 * k))
    using (var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(0, size), Color.FromArgb(0x3B, 0x7D, 0xE6), Color.FromArgb(0x25, 0x5F, 0xC2)))
        g.FillPath(fill, bg);

    // «Шторка»: белая панель у правого края, скруглена слева, как сама шторка на экране.
    var panel = new RectangleF(15 * k, 6 * k, 13 * k, 20 * k);
    using (var p = RoundLeft(panel, 3.5f * k))
    using (var white = new SolidBrush(Color.White))
        g.FillPath(white, p);
    // Строки списка в шторке — только там, где они различимы.
    if (size >= 24)
        using (var line = new SolidBrush(Color.FromArgb(0xC9, 0xDA, 0xF5)))
            foreach (var y in new[] { 9f, 12.5f, 16f })
                g.FillRectangle(line, 18 * k, y * k, 7.5f * k, 1.6f * k);

    // Замок слева от шторки.
    using (var lockBody = new SolidBrush(Color.White))
    using (var shackle = new Pen(Color.White, Math.Max(1.2f, 1.9f * k)))
    {
        g.DrawArc(shackle, 5.2f * k, 11 * k, 5.6f * k, 6.5f * k, 180, 180);
        g.DrawLine(shackle, 5.2f * k, 14.2f * k, 5.2f * k, 16 * k);
        g.DrawLine(shackle, 10.8f * k, 14.2f * k, 10.8f * k, 16 * k);
        using var body = Round(new RectangleF(4 * k, 15.5f * k, 8 * k, 7 * k), 1.5f * k);
        g.FillPath(lockBody, body);
    }
    if (size >= 32)
        using (var hole = new SolidBrush(Color.FromArgb(0x2B, 0x67, 0xCC)))
            g.FillEllipse(hole, 7.1f * k, 17.6f * k, 1.8f * k, 1.8f * k);
    return bmp;
}

static byte[] Png(Bitmap b) { using var ms = new MemoryStream(); b.Save(ms, ImageFormat.Png); b.Dispose(); return ms.ToArray(); }

static GraphicsPath Round(RectangleF r, float rad)
{
    var p = new GraphicsPath(); float d = rad * 2;
    p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure(); return p;
}

static GraphicsPath RoundLeft(RectangleF r, float rad)
{
    var p = new GraphicsPath(); float d = rad * 2;
    p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddLine(r.Right, r.Y, r.Right, r.Bottom);
    p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
}
