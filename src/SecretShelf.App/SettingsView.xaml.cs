using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SecretShelf.App.Services;

namespace SecretShelf.App;

/// <summary>Что экрану настроек нужно от шторки.</summary>
public interface ISettingsHost
{
    AppSettings Settings { get; }
    string KdfDescription { get; }
    /// <summary>Применить настройки (язычок, трей, …) и сохранить settings.json.</summary>
    void ApplySettings();
    /// <summary>Зарегистрировать горячую клавишу; false — сочетание занято.</summary>
    bool TryApplyHotkey(string gesture);
    /// <summary>Сменить мастер-пароль; возвращает текст ошибки или null.</summary>
    Task<string?> ChangePasswordAsync(string oldPassword, string newPassword);
    void CloseSettings();
    void OpenVaultFolder();
    Task LockAsync();
    Task ExitAsync();
}

public partial class SettingsView : UserControl
{
    private static readonly (int minutes, string text)[] IdleChoices =
        { (0, "не блокировать"), (5, "5 минут"), (15, "15 минут"), (30, "30 минут"), (60, "1 час") };
    private static readonly (int seconds, string text)[] ClipboardChoices =
        { (10, "10 с"), (25, "25 с"), (45, "45 с"), (60, "1 мин"), (90, "1,5 мин") };

    private ISettingsHost? _host;
    private bool _loading;
    private bool _capturing;

    public SettingsView()
    {
        InitializeComponent();
        foreach (var (_, text) in IdleChoices) IdleBox.Items.Add(text);
        foreach (var (_, text) in ClipboardChoices) ClipboardBox.Items.Add(text);
    }

    private AppSettings S => _host!.Settings;

    /// <summary>Идёт захват нового сочетания клавиш — Esc принадлежит ему.</summary>
    public bool IsCapturing => _capturing;

    /// <summary>Заполнить экран из текущих настроек (при каждом открытии).</summary>
    public void Load(ISettingsHost host)
    {
        _host = host;
        _loading = true;
        IdleBox.SelectedIndex = IndexOfNearest(IdleChoices.Select(c => c.minutes), S.IdleLockMinutes);
        ClipboardBox.SelectedIndex = IndexOfNearest(ClipboardChoices.Select(c => c.seconds), S.ClipboardClearSeconds);
        LockOnSessionBox.IsChecked = S.LockOnSessionLock;
        PeekBox.IsChecked = S.ShowPeekTab;
        TrayBox.IsChecked = S.ShowTrayIcon;
        HideOnDeactivateBox.IsChecked = S.HideOnDeactivate;
        AutostartBox.IsChecked = Autostart.IsEnabled;
        HotkeyButton.Content = HotkeyText(S.Hotkey);
        KdfInfo.Text = "Защита файла: " + host.KdfDescription;
        CallMessage.Text = "";
        CloseChangePassword();
        _loading = false;
    }

    private static int IndexOfNearest(IEnumerable<int> values, int value)
    {
        var list = values.ToList();
        int i = list.IndexOf(value);
        return i >= 0 ? i : list.Select((v, k) => (d: Math.Abs(v - value), k)).Min().k;
    }

    private static string HotkeyText(string g) => string.IsNullOrEmpty(g) ? "выключена" : g;

    private void Back_Click(object sender, RoutedEventArgs e) => _host?.CloseSettings();

    // ---------- блокировка, буфер ----------

    private void Idle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _host == null || IdleBox.SelectedIndex < 0) return;
        S.IdleLockMinutes = IdleChoices[IdleBox.SelectedIndex].minutes;
        _host.ApplySettings();
    }

    private void Clipboard_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _host == null || ClipboardBox.SelectedIndex < 0) return;
        S.ClipboardClearSeconds = ClipboardChoices[ClipboardBox.SelectedIndex].seconds;
        _host.ApplySettings();
    }

    // ---------- тумблеры ----------

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        CallMessage.Text = "";
        bool peek = PeekBox.IsChecked == true, tray = TrayBox.IsChecked == true;

        // Должен остаться хотя бы один способ открыть свёрнутую шторку.
        if (!peek && !tray && string.IsNullOrEmpty(S.Hotkey))
        {
            ((CheckBox)sender).IsChecked = true;
            CallMessage.Text = "Нужен хотя бы один способ открыть шторку: язычок, значок в трее или горячая клавиша.";
            return;
        }

        S.ShowPeekTab = peek;
        S.ShowTrayIcon = tray;
        S.LockOnSessionLock = LockOnSessionBox.IsChecked == true;
        S.HideOnDeactivate = HideOnDeactivateBox.IsChecked == true;
        _host.ApplySettings();
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Autostart.Set(AutostartBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            CallMessage.Text = "Не удалось изменить автозапуск: " + ex.Message;
        }
        AutostartBox.IsChecked = Autostart.IsEnabled; // показать то, что реально в реестре
    }

    // ---------- горячая клавиша: щёлкнуть и нажать сочетание ----------

    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        _capturing = true;
        CallMessage.Text = "";
        HotkeyButton.Content = "нажмите сочетание…";
        HotkeyButton.Focus();
    }

    private void Hotkey_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndCapture();

    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing || _host == null) return;
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape) { EndCapture(); return; }
        if (key is Key.Back or Key.Delete)
        {
            if (S.ShowPeekTab || S.ShowTrayIcon) Commit("");
            else CallMessage.Text = "Без горячей клавиши шторку будет нечем открыть: включите язычок или значок в трее.";
            EndCapture();
            return;
        }

        string? gesture = GlobalHotkey.Format(Keyboard.Modifiers, key);
        if (gesture == null) return; // нажат пока только модификатор — ждём клавишу
        Commit(gesture);
        EndCapture();
    }

    private void Commit(string gesture)
    {
        string old = S.Hotkey;
        if (_host!.TryApplyHotkey(gesture))
        {
            S.Hotkey = gesture;
            _host.ApplySettings();
        }
        else
        {
            _host.TryApplyHotkey(old); // вернуть прежнее сочетание
            CallMessage.Text = $"Сочетание {gesture} занято другой программой. Выберите другое.";
        }
    }

    private void EndCapture()
    {
        _capturing = false;
        if (_host != null) HotkeyButton.Content = HotkeyText(S.Hotkey);
    }

    // ---------- смена мастер-пароля ----------

    private void ChangePassToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ChangePassBlock.Visibility == Visibility.Visible) CloseChangePassword();
        else
        {
            ChangePassBlock.Visibility = Visibility.Visible;
            ChangePassButton.Visibility = Visibility.Collapsed;
            OldPass.Focus();
        }
    }

    private void CloseChangePassword()
    {
        OldPass.Clear(); NewPass1.Clear(); NewPass2.Clear();
        ChangePassMessage.Text = "";
        ChangePassBlock.Visibility = Visibility.Collapsed;
        ChangePassButton.Visibility = Visibility.Visible;
    }

    private async void ChangePassSave_Click(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        ChangePassMessage.Foreground = (Brush)FindResource("Danger");
        if (NewPass1.Password.Length < 8) { ChangePassMessage.Text = "Новый пароль короче 8 символов."; return; }
        if (NewPass1.Password != NewPass2.Password) { ChangePassMessage.Text = "Новые пароли не совпадают."; return; }

        ChangePassSave.IsEnabled = false;
        ChangePassMessage.Foreground = (Brush)FindResource("TextDim");
        ChangePassMessage.Text = "Меняю…";
        string? error = await _host.ChangePasswordAsync(OldPass.Password, NewPass1.Password);
        ChangePassSave.IsEnabled = true;
        if (error != null)
        {
            ChangePassMessage.Foreground = (Brush)FindResource("Danger");
            ChangePassMessage.Text = error;
            return;
        }
        CloseChangePassword();
        ChangePassMessage.Text = "";
        CallMessage.Text = "";
        KdfInfo.Text = "Пароль сменён. Копия со старым паролем — в папке backups. Защита файла: " + _host.KdfDescription;
    }

    // ---------- данные и выход ----------

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _host?.OpenVaultFolder();

    private async void Lock_Click(object sender, RoutedEventArgs e)
    {
        if (_host != null) await _host.LockAsync();
    }

    private async void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (_host != null) await _host.ExitAsync();
    }
}
