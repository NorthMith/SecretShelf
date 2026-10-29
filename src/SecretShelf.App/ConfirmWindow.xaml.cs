using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SecretShelf.Core.Agents;

namespace SecretShelf.App;

public partial class ConfirmWindow : Window
{
    private const int TimeoutSeconds = 60;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TaskCompletionSource<ConfirmDecision> _answer = new();
    private int _left = TimeoutSeconds;

    public ConfirmWindow(ConfirmRequest r)
    {
        InitializeComponent();
        TitleText.Text = r.Title;
        ClientText.Text = $"Агент называет себя «{r.Client}» — это его слова, а не проверка.";
        SecretsText.Text = r.Secrets.Count > 0 ? string.Join(", ", r.Secrets) : "—";
        SecretsLabel.Visibility = SecretsText.Visibility = r.Secrets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailsText.Text = r.Details;
        TrustButton.Visibility = TrustHint.Visibility = r.CanTrust ? Visibility.Visible : Visibility.Collapsed;
        if (r.Destructive)
        {
            HeaderIcon.Text = "";
            HeaderIcon.Foreground = (Brush)FindResource("Danger");
            AllowButton.Content = "Удалить";
            AllowButton.Background = AllowButton.BorderBrush = (Brush)FindResource("Danger");
        }

        _timer.Tick += (_, _) =>
        {
            if (--_left <= 0) { Finish(ConfirmDecision.Deny); return; }
            CountdownText.Text = $"Нет ответа {_left} с — отказ";
            if (_left == RemindAtSeconds) Alert(); // пропустил первый сигнал — второй, пока не поздно
        };
        CountdownText.Text = $"Нет ответа {_left} с — отказ";
        Loaded += (_, _) =>
        {
            Rect wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 8;
            Top = wa.Bottom - ActualHeight - 8;
            _timer.Start();
            DenyButton.Focus(); // Enter по привычке не должен разрешать
            Alert();
        };
    }

    private const int RemindAtSeconds = 20;
    private static readonly string AlertSound =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify System Generic.wav");

    /// <summary>Звук и мигание кнопки на панели задач: окно в углу легко не заметить.</summary>
    private void Alert()
    {
        try
        {
            // Свой файл звучит и при схеме «Без звука», где системные события молчат.
            if (File.Exists(AlertSound)) new SoundPlayer(AlertSound).Play();
            else SystemSounds.Exclamation.Play();
        }
        catch { SystemSounds.Exclamation.Play(); }

        var fi = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new WindowInteropHelper(this).Handle,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
        };
        FlashWindowEx(ref fi);
    }

    private const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    public Task<ConfirmDecision> Answer => _answer.Task;

    private void Allow_Click(object sender, RoutedEventArgs e) => Finish(ConfirmDecision.AllowOnce);
    private void Trust_Click(object sender, RoutedEventArgs e) => Finish(ConfirmDecision.TrustHour);
    private void Deny_Click(object sender, RoutedEventArgs e) => Finish(ConfirmDecision.Deny);

    public void Finish(ConfirmDecision decision)
    {
        _timer.Stop();
        _answer.TrySetResult(decision);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _answer.TrySetResult(ConfirmDecision.Deny); // закрыли крестиком или Alt+F4 — отказ
        base.OnClosed(e);
    }
}

/// <summary>
/// Подтверждения по одному: два агента сразу не получат два окна друг на друге.
/// </summary>
public sealed class WindowConfirmer : IConfirmer
{
    private readonly Dispatcher _ui;
    private readonly SemaphoreSlim _one = new(1, 1);

    public WindowConfirmer(Dispatcher ui) => _ui = ui;

    public async Task<ConfirmDecision> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            ConfirmWindow? w = null;
            var shown = await _ui.InvokeAsync(() =>
            {
                w = new ConfirmWindow(request);
                w.Show();
                w.Activate();
                return w.Answer;
            });
            using (ct.Register(() => _ui.BeginInvoke(() => w?.Finish(ConfirmDecision.Deny)))) // агент ушёл — окно убрать
                return await shown;
        }
        finally
        {
            _one.Release();
        }
    }
}
