using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SecretShelf.App.Services;

/// <summary>
/// Автоблокировка: по бездействию всей системы (не только шторки — смысл в том, что
/// человек отошёл), по блокировке сеанса Windows (Win+L) и при уходе в сон.
/// </summary>
public sealed class AutoLock : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };

    public AutoLock(AppSettings settings)
    {
        _settings = settings;
        _timer.Tick += (_, _) => CheckIdle();
        _timer.Start();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>Пора заблокировать. Обработчик вызывается в потоке интерфейса.</summary>
    public event EventHandler<string>? LockRequested;

    public static TimeSpan SystemIdle
    {
        get
        {
            var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
        }
    }

    private void CheckIdle()
    {
        if (_settings.IdleLockMinutes <= 0) return;
        if (SystemIdle >= TimeSpan.FromMinutes(_settings.IdleLockMinutes))
            LockRequested?.Invoke(this, "бездействие");
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (_settings.LockOnSessionLock && e.Reason == SessionSwitchReason.SessionLock)
            Raise("блокировка Windows");
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_settings.LockOnSessionLock && e.Mode == PowerModes.Suspend)
            Raise("сон");
    }

    // События SystemEvents приходят из служебного потока — переносим в поток интерфейса.
    private void Raise(string reason) =>
        _timer.Dispatcher.BeginInvoke(() => LockRequested?.Invoke(this, reason));

    public void Dispose()
    {
        _timer.Stop();
        // Статические события: без отписки объект живёт до конца процесса.
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
