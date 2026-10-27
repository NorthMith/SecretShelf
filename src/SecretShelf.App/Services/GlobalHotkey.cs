using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace SecretShelf.App.Services;

/// <summary>
/// Системная горячая клавиша через RegisterHotKey. Сообщения принимает невидимое
/// окно-приёмник (HWND_MESSAGE), поэтому клавиша работает и при скрытой шторке.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x5353; // «SS»
    private const uint ModAlt = 0x1, ModCtrl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _sink;
    private bool _registered;

    public GlobalHotkey()
    {
        _sink = new HwndSource(new HwndSourceParameters("SecretShelf hotkey sink")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: окно только для сообщений
            WindowStyle = 0,
        });
        _sink.AddHook(WndProc);
    }

    public event EventHandler? Pressed;

    /// <summary>
    /// Зарегистрировать сочетание. false — оно занято другой программой или записано неверно.
    /// Пустая строка снимает клавишу.
    /// </summary>
    public bool TrySet(string gesture)
    {
        Clear();
        if (string.IsNullOrWhiteSpace(gesture)) return true;
        if (!TryParse(gesture, out uint mods, out uint vk)) return false;
        _registered = RegisterHotKey(_sink.Handle, HotkeyId, mods | ModNoRepeat, vk);
        return _registered;
    }

    private void Clear()
    {
        if (_registered) UnregisterHotKey(_sink.Handle, HotkeyId);
        _registered = false;
    }

    /// <summary>«Ctrl+Alt+P» → модификаторы и код клавиши. Нужен хотя бы один модификатор.</summary>
    public static bool TryParse(string gesture, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        Key key = Key.None;
        foreach (var part in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": mods |= ModCtrl; break;
                case "alt": mods |= ModAlt; break;
                case "shift": mods |= ModShift; break;
                case "win": mods |= ModWin; break;
                default:
                    if (key != Key.None || !Enum.TryParse(part, ignoreCase: true, out key)) return false;
                    break;
            }
        }
        if (mods == 0 || key == Key.None) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    /// <summary>Текст сочетания из нажатых клавиш (для поля захвата в настройках). null — ещё не сочетание.</summary>
    public static string? Format(ModifierKeys mods, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift
            or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None) return null;
        if (mods == ModifierKeys.None) return null;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Clear();
        _sink.Dispose();
    }
}
