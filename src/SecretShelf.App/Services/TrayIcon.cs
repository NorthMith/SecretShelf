using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SecretShelf.App.Services;

/// <summary>
/// Значок в трее (штатный NotifyIcon). Левый щелчок — открыть/свернуть шторку,
/// правый — меню. Значок рисуется кодом: отдельного файла .ico проекту не нужно.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly NotifyIcon _icon;
    private readonly IntPtr _hIcon;
    private readonly ToolStripMenuItem _lockItem;

    public TrayIcon()
    {
        _icon = new NotifyIcon();
        using var bmp = DrawIcon();
        _hIcon = bmp.GetHicon();
        _icon.Icon = Icon.FromHandle(_hIcon);
        _icon.Text = "SecretShelf";

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть шторку", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        _lockItem = new ToolStripMenuItem("Заблокировать", null, (_, _) => LockRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(_lockItem);
        menu.Items.Add("Настройки", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        _icon.ContextMenuStrip = menu;

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? ToggleRequested;
    public event EventHandler? OpenRequested;
    public event EventHandler? LockRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    /// <summary>Всплывающее уведомление у значка (например, агент просит ключ, а хранилище заперто).</summary>
    public void Notify(string title, string text)
    {
        if (_icon.Visible) _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
    }

    /// <summary>«Заблокировать» доступно только при открытом хранилище.</summary>
    public void SetUnlocked(bool unlocked) => _lockItem.Enabled = unlocked;

    /// <summary>Синий скруглённый квадрат с белым замком (глиф Segoe Fluent Icons).</summary>
    private static Bitmap DrawIcon()
    {
        var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var path = RoundedRect(new RectangleF(1, 1, 30, 30), 7))
        using (var fill = new SolidBrush(Color.FromArgb(0x2F, 0x6F, 0xD6)))
            g.FillPath(fill, path);

        using var font = new Font("Segoe Fluent Icons", 17, GraphicsUnit.Pixel);
        var glyph = font.Name == "Segoe Fluent Icons" ? "" : "S";
        using var white = new SolidBrush(Color.White);
        var size = g.MeasureString(glyph, font);
        g.DrawString(glyph, font, white, (32 - size.Width) / 2 + 0.5f, (32 - size.Height) / 2 + 1);
        return bmp;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }
}
