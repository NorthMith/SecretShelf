using System.Threading;
using System.Windows;

namespace SecretShelf.App;

public partial class App : Application
{
    // Имена объектов привязаны к пользователю: у другого пользователя Windows — свой экземпляр.
    private static readonly string InstanceName = "SecretShelf.Instance." + Environment.UserName;
    private static readonly string ShowSignalName = "SecretShelf.Show." + Environment.UserName;

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private DrawerWindow? _drawer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            // Уже запущено: попросить первый экземпляр показать шторку и выйти.
            try { EventWaitHandle.OpenExisting(ShowSignalName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        var listener = new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(() => _drawer?.Expand());
        }) { IsBackground = true, Name = "SecretShelf show-signal" };
        listener.Start();

        _drawer = new DrawerWindow(AppSettings.Load());
        _drawer.Show();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Выход из Windows: сохранить синхронно, асинхронный путь не успеет.
        _drawer?.FlushForSessionEnd();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
