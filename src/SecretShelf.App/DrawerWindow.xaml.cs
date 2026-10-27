using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SecretShelf.App.Services;
using SecretShelf.Core;

namespace SecretShelf.App;

/// <summary>
/// Шторка у правого края экрана: создание хранилища, отпирание, список записей
/// с перетаскиванием, редактор записи, настройки. Здесь же живут службы
/// (трей, горячая клавиша, автоблокировка): шторка — единственное окно приложения.
/// </summary>
public partial class DrawerWindow : Window, ISettingsHost, IImportHost
{
    private readonly AppSettings _settings;
    private readonly SecureClipboard _clipboard = new();
    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly ObservableCollection<EntryVm> _entries = new();
    private readonly ICollectionView _view;
    private readonly PeekWindow _peek = new();
    private readonly TrayIcon _tray = new();
    private readonly GlobalHotkey _hotkey = new();
    private readonly AutoLock _autoLock;
    private readonly QuickUnlockStore _quick = new(Path.Combine(AppSettings.Dir, "quick-unlock.json"));
    private readonly HelloSigner _hello = new();
    private bool _forceMaster; // «Войти мастер-паролем» или быстрый вход сломался
    private bool _lockBusy;
    private int _slideToken; // защищает от наложения анимаций выезда и уезда

    private Vault? _vault;
    private EditorVm? _editor;
    private Task _saveTask = Task.CompletedTask;
    private string _saveStatus = "";
    private int? _clipboardLeft;

    // перетаскивание
    private EntryVm? _dragCandidate;
    private Point _dragStart;
    private bool _exiting;

    public DrawerWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Width = settings.DrawerWidth;

        _view = CollectionViewSource.GetDefaultView(_entries);
        EntryList.ItemsSource = _view;

        _saveDebounce.Tick += async (_, _) => { _saveDebounce.Stop(); await SaveInBackground(); };
        _clipboard.Countdown += left => { _clipboardLeft = left; UpdateStatus(); };
        _peek.ExpandRequested += (_, _) => Expand();

        _tray.ToggleRequested += (_, _) => Toggle();
        _tray.OpenRequested += (_, _) => Expand();
        _tray.LockRequested += async (_, _) => await LockAsync();
        _tray.SettingsRequested += (_, _) => OpenSettings();
        _tray.ExitRequested += async (_, _) => await ExitAsync();
        _hotkey.Pressed += (_, _) => Toggle();
        _autoLock = new AutoLock(settings);
        _autoLock.LockRequested += async (_, reason) =>
        {
            if (_vault == null) return;
            await LockAsync();
            _saveStatus = $"Заблокировано: {reason}";
            UpdateStatus();
        };
        Deactivated += (_, _) =>
        {
            if (_settings.HideOnDeactivate && IsVisible && _editor == null && !SettingsPanel.IsCapturing)
                CollapseDrawer();
        };

        Loaded += (_, _) =>
        {
            PlaceAtRightEdge();
            FirstRunDefaults();
            ApplySettings();
            if (!_hotkey.TrySet(_settings.Hotkey))
            {
                _saveStatus = $"Горячая клавиша {_settings.Hotkey} занята — выберите другую в настройках";
                UpdateStatus();
            }
            ShowStartPanel();
            // «Без пароля» открывает сразу; Hello при запуске не дёргаем — только когда шторку позвали.
            if (LockPanel.IsVisible && QuickActive && _quick.Mode == QuickUnlockMode.NoPassword)
                NoPassUnlock_Click(this, new RoutedEventArgs());
        };
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea)) { PlaceAtRightEdge(); _peek.PlaceAtCorner(); }
        };
    }

    // =============== положение и выезд ===============

    private void PlaceAtRightEdge()
    {
        Rect wa = SystemParameters.WorkArea;
        Height = wa.Height;
        Top = wa.Top;
        Left = wa.Right - Width;
    }

    /// <summary>Показать шторку (из язычка, трея, горячей клавиши, второго запуска).</summary>
    public void Expand()
    {
        _slideToken++;
        _peek.Hide();
        PlaceAtRightEdge();
        if (!IsVisible) Show();
        Activate();
        Slide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(Width, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        FocusDefault();
        AutoQuickUnlock();
    }

    /// <summary>Шторку позвали, а она заперта: Hello и «без пароля» срабатывают сами.</summary>
    private void AutoQuickUnlock()
    {
        if (!LockPanel.IsVisible || _lockBusy || !QuickActive) return;
        if (_quick.Mode == QuickUnlockMode.WindowsHello) HelloUnlock_Click(this, new RoutedEventArgs());
        else if (_quick.Mode == QuickUnlockMode.NoPassword) NoPassUnlock_Click(this, new RoutedEventArgs());
    }

    private void CollapseDrawer()
    {
        if (!IsVisible) return;
        int token = ++_slideToken;
        var anim = new DoubleAnimation(0, Width, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        anim.Completed += (_, _) =>
        {
            if (token != _slideToken) return; // пока уезжала, её позвали обратно
            Hide();
            Slide.BeginAnimation(TranslateTransform.XProperty, null);
            if (_settings.ShowPeekTab) _peek.ShowAtCorner();
        };
        Slide.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    /// <summary>Горячая клавиша и левый щелчок по значку в трее.</summary>
    public void Toggle()
    {
        if (IsVisible) CollapseDrawer();
        else Expand();
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) => CollapseDrawer();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (ImportPanel.IsVisible) CloseImport();
        else if (SettingsPanel.IsVisible)
        {
            if (SettingsPanel.IsCapturing) return; // Esc отменяет захват сочетания
            CloseSettings();
        }
        else if (_editor != null) CloseEditor();
        else if (SearchText.Text.Length > 0) SearchText.Text = "";
        else CollapseDrawer();
        e.Handled = true;
    }

    // =============== режимы: создание / замок / список ===============

    private void ShowStartPanel()
    {
        if (File.Exists(_settings.VaultPath)) ShowPanel(LockPanel);
        else
        {
            SetupPathText.Text = "Файл: " + _settings.VaultPath;
            ShowPanel(SetupPanel);
        }
    }

    private void ShowPanel(FrameworkElement panel)
    {
        foreach (var p in new FrameworkElement[] { SetupPanel, LockPanel, ListPanel, EditorPanel, SettingsPanel, ImportPanel })
            p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;

        if (panel == LockPanel) UpdateLockPanel();
        bool unlocked = panel != SetupPanel && panel != LockPanel;
        bool list = panel == ListPanel;
        Footer.Visibility = list || panel == SetupPanel || panel == LockPanel ? Visibility.Visible : Visibility.Collapsed;
        MenuButton.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        AddButton.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        CaptionText.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        _tray.SetUnlocked(unlocked);
        FocusDefault();
    }

    private void FocusDefault()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (SetupPanel.IsVisible) SetupPass1.Focus();
            else if (LockPanel.IsVisible)
            {
                if (QuickPinBox.IsVisible) PinBox.Focus();
                else if (MasterBox.IsVisible) UnlockPass.Focus();
            }
            else if (EditorPanel.IsVisible) EditorTitle.Focus();
            else if (ListPanel.IsVisible) SearchText.Focus();
        });
    }

    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        string p1 = SetupPass1.Password, p2 = SetupPass2.Password;
        if (p1.Length < 8) { SetupError.Text = "Пароль короче 8 символов."; return; }
        if (p1 != p2) { SetupError.Text = "Пароли не совпадают."; return; }

        SetupError.Text = "";
        SetupButton.IsEnabled = false;
        SetupButton.Content = "Подбираю защиту под этот компьютер…";
        try
        {
            string path = _settings.VaultPath;
            var vault = await Task.Run(() => Vault.Create(path, p1));
            SetupPass1.Clear(); SetupPass2.Clear();
            OnUnlocked(vault);
        }
        catch (Exception ex)
        {
            SetupError.Text = "Не удалось создать хранилище: " + ex.Message;
        }
        finally
        {
            SetupButton.IsEnabled = true;
            SetupButton.Content = "Создать хранилище";
        }
    }

    private void SetupPass_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Setup_Click(sender, e);
    }

    // =============== отпирание ===============

    /// <summary>Быстрый вход включён и сейчас разрешён (не пора вводить мастер-пароль).</summary>
    private bool QuickActive =>
        _quick.Mode != QuickUnlockMode.Off && !_forceMaster && !_quick.IsMasterDue(_settings.MasterPasswordEveryDays);

    private void UpdateLockPanel()
    {
        var m = _quick.Mode;
        bool quick = QuickActive;
        HelloButton.Visibility = quick && m == QuickUnlockMode.WindowsHello ? Visibility.Visible : Visibility.Collapsed;
        QuickPinBox.Visibility = quick && m == QuickUnlockMode.Pin ? Visibility.Visible : Visibility.Collapsed;
        NoPassButton.Visibility = quick && m == QuickUnlockMode.NoPassword ? Visibility.Visible : Visibility.Collapsed;
        MasterBox.Visibility = quick ? Visibility.Collapsed : Visibility.Visible;
        UseMasterLink.Visibility = quick ? Visibility.Visible : Visibility.Collapsed;

        MasterHint.Text = m != QuickUnlockMode.Off && !_forceMaster && _quick.IsMasterDue(_settings.MasterPasswordEveryDays)
            ? $"Раз в {_settings.MasterPasswordEveryDays} дней — мастер-пароль, чтобы он не забылся."
            : m != QuickUnlockMode.Off ? "Мастер-пароль" : "";
        MasterHint.Visibility = MasterHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UseMaster_Click(object sender, RoutedEventArgs e)
    {
        _forceMaster = true;
        UnlockError.Text = "";
        UpdateLockPanel();
        FocusDefault();
    }

    private void SetLockBusy(bool busy, string? caption = null)
    {
        _lockBusy = busy;
        foreach (var b in new Button[] { UnlockButton, HelloButton, PinButton, NoPassButton }) b.IsEnabled = !busy;
        UnlockButton.Content = busy && MasterBox.IsVisible ? caption ?? "Открываю…" : "Открыть";
    }

    /// <summary>Общий путь: открыть хранилище паролем (введённым или из быстрого входа).</summary>
    private async Task OpenWith(string password, bool viaMaster)
    {
        try
        {
            string path = _settings.VaultPath;
            var vault = await Task.Run(() => Vault.Open(path, password));
            if (viaMaster) _quick.MarkMasterVerified();
            _forceMaster = false;
            UnlockPass.Clear();
            PinBox.Clear();
            OnUnlocked(vault);
        }
        catch (WrongPasswordException)
        {
            if (viaMaster)
            {
                UnlockError.Text = "Неверный пароль.";
                UnlockPass.SelectAll();
                UnlockPass.Focus();
            }
            else
            {
                // Пароль, сохранённый для быстрого входа, не подошёл: файл меняли другой программой.
                DisableQuickUnlock();
                _forceMaster = true;
                UpdateLockPanel();
                UnlockError.Text = "Сохранённый для быстрого входа пароль не подошёл — быстрый вход выключен. Войдите мастер-паролем.";
            }
        }
        catch (Exception ex)
        {
            UnlockError.Text = "Не удалось открыть файл: " + ex.Message;
        }
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        string pass = UnlockPass.Password;
        if (pass.Length == 0 || _lockBusy) return;
        UnlockError.Text = "";
        SetLockBusy(true);
        try { await OpenWith(pass, viaMaster: true); }
        finally { SetLockBusy(false); }
    }

    private async void PinUnlock_Click(object sender, RoutedEventArgs e)
    {
        string pin = PinBox.Password;
        if (pin.Length == 0 || _lockBusy) return;
        UnlockError.Text = "";
        SetLockBusy(true);
        try
        {
            string pass = await Task.Run(() => _quick.UnlockAsync(pin: pin));
            await OpenWith(pass, viaMaster: false);
        }
        catch (WrongPinException ex)
        {
            UnlockError.Text = $"Неверный ПИН-код. Осталось попыток: {ex.AttemptsLeft}.";
            PinBox.Clear();
            PinBox.Focus();
        }
        catch (QuickUnlockException ex) { QuickFailed(ex.Message); }
        finally { SetLockBusy(false); }
    }

    private void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) PinUnlock_Click(sender, e);
    }

    private async void HelloUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (_lockBusy) return;
        UnlockError.Text = "";
        SetLockBusy(true);
        try
        {
            string pass = await WithoutTopmost(() => _quick.UnlockAsync(signer: _hello));
            await OpenWith(pass, viaMaster: false);
        }
        catch (OperationCanceledException) { UnlockError.Text = "Вход отменён."; }
        catch (QuickUnlockException ex) { QuickFailed(ex.Message); }
        catch (Exception ex) { UnlockError.Text = "Windows Hello не сработал: " + ex.Message; }
        finally { SetLockBusy(false); }
    }

    private async void NoPassUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (_lockBusy) return;
        UnlockError.Text = "";
        SetLockBusy(true);
        try
        {
            string pass = await _quick.UnlockAsync();
            await OpenWith(pass, viaMaster: false);
        }
        catch (QuickUnlockException ex) { QuickFailed(ex.Message); }
        finally { SetLockBusy(false); }
    }

    private void QuickFailed(string message)
    {
        _forceMaster = true;
        UpdateLockPanel();
        UnlockError.Text = message;
        FocusDefault();
    }

    /// <summary>
    /// Окно Windows Hello может оказаться под шторкой (она поверх всех окон),
    /// поэтому на время запроса шторка перестаёт быть «поверх всех».
    /// </summary>
    private async Task<T> WithoutTopmost<T>(Func<Task<T>> action)
    {
        Topmost = false;
        try { return await action(); }
        finally
        {
            Topmost = true;
            if (IsVisible) Activate();
        }
    }

    private void UnlockPass_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Unlock_Click(sender, e);
    }

    private void OnUnlocked(Vault vault)
    {
        _vault = vault;
        _vault.Changed += (_, _) => Dispatcher.BeginInvoke(() => { _saveDebounce.Stop(); _saveDebounce.Start(); });
        _saveStatus = "";
        ReloadEntries();
        ShowPanel(ListPanel);
    }

    /// <summary>Блокировка: сохранить, очистить буфер, забыть всё расшифрованное.</summary>
    public async Task LockAsync()
    {
        if (_vault == null) return;
        if (_editor != null) CloseEditor();
        _clipboard.ClearNow();
        _saveDebounce.Stop();
        var vault = _vault;
        _vault = null;
        _entries.Clear();
        SearchText.Text = "";
        ShowPanel(LockPanel);
        await _saveTask;
        await Task.Run(vault.Dispose); // сохранит несохранённое
        _saveStatus = "";
        UpdateStatus();
    }

    // =============== список ===============

    private void ReloadEntries()
    {
        if (_vault == null) return;
        var vault = _vault;
        _entries.Clear();
        foreach (var e in vault.GetEntries())
            _entries.Add(new EntryVm(e, f => vault.GetValue(e.Id, f.Key)));
        EmptyHint.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchText_TextChanged(object sender, TextChangedEventArgs e)
    {
        string q = SearchText.Text.Trim();
        SearchHint.Visibility = SearchText.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Ищем по названию, подписям полей и открытым (несекретным) значениям.
        _view.Filter = q.Length == 0 ? null : o =>
        {
            var vm = (EntryVm)o;
            return vm.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                   vm.Fields.Any(f => f.Label.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                                      (!f.IsSecret && f.Display.Contains(q, StringComparison.CurrentCultureIgnoreCase)));
        };
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_vault == null || ((FrameworkElement)sender).DataContext is not FieldVm f) return;
        if (f.IsRevealed) f.Conceal();
        else f.Reveal(_vault.GetValue(f.Owner.Id, f.Field.Key));
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyField(((FrameworkElement)sender).DataContext as FieldVm);

    private void FieldValue_Click(object sender, MouseButtonEventArgs e) => CopyField(((FrameworkElement)sender).DataContext as FieldVm);

    private void CopyField(FieldVm? f)
    {
        if (_vault == null || f == null) return;
        try
        {
            _clipboard.Copy(_vault.GetValue(f.Owner.Id, f.Field.Key), _settings.ClipboardClearSeconds);
            _saveStatus = $"Скопировано: {f.Owner.Title} · {f.Label}";
        }
        catch (Exception ex)
        {
            _saveStatus = "Буфер обмена занят: " + ex.Message;
        }
        UpdateStatus();
    }

    // =============== перетаскивание ===============

    private void DragHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_view.Filter != null) { _saveStatus = "Порядок меняется без поиска — очистите поиск"; UpdateStatus(); return; }
        _dragCandidate = ((FrameworkElement)sender).DataContext as EntryVm;
        _dragStart = e.GetPosition(EntryList);
    }

    private void EntryList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _dragCandidate = null; return; }

        Vector d = e.GetPosition(EntryList) - _dragStart;
        if (Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance &&
            Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance) return;

        var item = _dragCandidate;
        _dragCandidate = null;
        int oldIndex = _entries.IndexOf(item);
        item.IsDragging = true;
        try
        {
            DragDrop.DoDragDrop(EntryList, new DataObject(typeof(EntryVm), item), DragDropEffects.Move);
        }
        finally
        {
            item.IsDragging = false;
        }

        int newIndex = _entries.IndexOf(item);
        if (_vault != null && newIndex >= 0 && newIndex != oldIndex)
            _vault.MoveEntry(item.Id, newIndex);
    }

    private void EntryList_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(EntryVm)) is not EntryVm dragged) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;

        // Живой порядок: карточка уезжает на место той, над которой курсор.
        if (FindItemUnder(e.OriginalSource as DependencyObject) is EntryVm target && target != dragged)
        {
            int from = _entries.IndexOf(dragged), to = _entries.IndexOf(target);
            if (from >= 0 && to >= 0) _entries.Move(from, to);
        }

        // Автопрокрутка у краёв списка.
        if (FindChild<ScrollViewer>(EntryList) is { } sv)
        {
            double y = e.GetPosition(EntryList).Y;
            if (y < 36) sv.ScrollToVerticalOffset(sv.VerticalOffset - 12);
            else if (y > EntryList.ActualHeight - 36) sv.ScrollToVerticalOffset(sv.VerticalOffset + 12);
        }
    }

    private void EntryList_Drop(object sender, DragEventArgs e) => e.Handled = true;

    private static EntryVm? FindItemUnder(DependencyObject? d)
    {
        while (d != null && d is not ListBoxItem)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return (d as ListBoxItem)?.DataContext as EntryVm;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var c = VisualTreeHelper.GetChild(parent, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } r) return r;
        }
        return null;
    }

    // =============== редактор ===============

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var ed = new EditorVm();
        ed.ApplyTemplate("login");
        OpenEditor(ed);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_vault == null || ((FrameworkElement)sender).DataContext is not EntryVm vm) return;
        var ed = new EditorVm { EntryId = vm.Id, Title = vm.Title };
        foreach (var f in vm.Model.Fields)
            ed.Fields.Add(new FieldDraftVm
            {
                Key = f.Key, Label = f.Label, Value = _vault.GetValue(vm.Id, f.Key), IsSecret = f.IsSecret,
                Access = f.Access, AllowedTargets = f.AllowedTargets.ToList(),
            });
        OpenEditor(ed);
    }

    private void OpenEditor(EditorVm ed)
    {
        _editor = ed;
        EditorPanel.DataContext = ed;
        EditorError.Text = "";
        ShowPanel(EditorPanel);
    }

    private void CloseEditor()
    {
        _editor = null;
        EditorPanel.DataContext = null; // значения из редактора больше не держим
        ShowPanel(ListPanel);
    }

    private void Template_Click(object sender, RoutedEventArgs e) => _editor?.ApplyTemplate((string)((FrameworkElement)sender).Tag);

    private void AddField_Click(object sender, RoutedEventArgs e)
    {
        if (_editor == null) return;
        var f = new FieldDraftVm { Label = $"Поле {_editor.Fields.Count + 1}", IsRenaming = true };
        _editor.Fields.Add(f);
    }

    private void RemoveField_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FieldDraftVm f) _editor?.Fields.Remove(f);
    }

    // --- переименование поля: щелчок по подписи открывает поле ввода ---

    private void FieldLabel_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FieldDraftVm f) f.IsRenaming = true;
    }

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var tb = (TextBox)sender;
        if (!tb.IsVisible) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { tb.Focus(); tb.SelectAll(); });
    }

    private void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndRename(sender);

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        EndRename(sender);
        e.Handled = true;
    }

    private static void EndRename(object sender)
    {
        if (((FrameworkElement)sender).DataContext is not FieldDraftVm f) return;
        if (string.IsNullOrWhiteSpace(f.Label)) f.Label = "Поле";
        f.IsRenaming = false;
    }

    // --- меню поля «⋯» ---

    private void FieldMenu_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        var menu = btn.ContextMenu!;
        menu.DataContext = btn.DataContext;
        menu.PlacementTarget = btn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void FieldMenuRename_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FieldDraftVm f) f.IsRenaming = true;
    }

    private void FieldMenuUp_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FieldDraftVm f) _editor?.MoveField(f, -1);
    }

    private void FieldMenuDown_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FieldDraftVm f) _editor?.MoveField(f, +1);
    }

    /// <summary>Случайный пароль: 20 символов, криптостойкий генератор, без похожих символов (O/0, l/1).</summary>
    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not FieldDraftVm f) return;
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789-_.!@#%";
        f.Value = System.Security.Cryptography.RandomNumberGenerator.GetString(alphabet, 20);
        f.IsRevealed = true; // показать, что пароль появился
    }

    private void DraftReveal_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FieldDraftVm f) f.IsRevealed = !f.IsRevealed;
    }

    // PasswordBox не умеет привязку — синхронизируем вручную.
    private void DraftPassword_Loaded(object sender, RoutedEventArgs e)
    {
        var pb = (PasswordBox)sender;
        if (pb.DataContext is FieldDraftVm f && pb.Password != f.Value) pb.Password = f.Value;
        DataObject.RemovePastingHandler(pb, DraftPassword_Pasting);
        DataObject.AddPastingHandler(pb, DraftPassword_Pasting);
    }

    private void DraftPassword_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var pb = (PasswordBox)sender;
        if (pb.IsVisible && pb.DataContext is FieldDraftVm f && pb.Password != f.Value) pb.Password = f.Value;
    }

    private void DraftPassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        var pb = (PasswordBox)sender;
        if (pb.IsVisible && pb.DataContext is FieldDraftVm f) f.Value = pb.Password;
    }

    /// <summary>
    /// Однострочное поле пароля выкинуло бы переводы строк (SSH-ключ испортился бы молча),
    /// поэтому многострочная вставка открывает поле и вставляет текст целиком.
    /// </summary>
    private void DraftPassword_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !text.Contains('\n')) return;
        e.CancelCommand();
        if (((PasswordBox)sender).DataContext is FieldDraftVm f)
        {
            f.Value = text;
            f.IsRevealed = true;
        }
    }

    private void SaveEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_vault == null || _editor == null) return;
        if (string.IsNullOrWhiteSpace(_editor.Title)) { EditorError.Text = "Укажите название."; return; }
        if (_editor.Fields.Count == 0) { EditorError.Text = "Нужно хотя бы одно поле."; return; }

        var drafts = _editor.Fields.Select(f => f.ToDraft()).ToList();
        try
        {
            if (_editor.IsNew) _vault.AddEntry(_editor.Title, drafts);
            else _vault.UpdateEntry(_editor.EntryId!, _editor.Title, drafts);
        }
        catch (Exception ex)
        {
            EditorError.Text = "Не сохранилось: " + ex.Message;
            return;
        }
        bool isNew = _editor.IsNew;
        CloseEditor();
        ReloadEntries();
        if (isNew && FindChild<ScrollViewer>(EntryList) is { } sv) sv.ScrollToEnd();
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e) => CloseEditor();

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (_vault == null || _editor?.EntryId == null) return;
        if (!_editor.ConfirmDelete) { _editor.ConfirmDelete = true; return; }
        _vault.DeleteEntry(_editor.EntryId);
        CloseEditor();
        ReloadEntries();
    }

    // =============== сохранение и строка состояния ===============

    private async Task SaveInBackground()
    {
        if (_vault == null) return;
        var vault = _vault;
        await _saveTask;
        _saveStatus = "Сохраняю…";
        UpdateStatus();
        _saveTask = Task.Run(vault.SaveIfDirty);
        try
        {
            await _saveTask;
            _saveStatus = "Сохранено " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            _saveStatus = "НЕ СОХРАНЕНО: " + ex.Message;
            StatusText.Foreground = (Brush)FindResource("Danger");
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (!_saveStatus.StartsWith("НЕ СОХРАНЕНО"))
            StatusText.Foreground = (Brush)FindResource("TextDim");
        string clip = _clipboardLeft is int s ? $" · буфер очистится через {s} с" : "";
        StatusText.Text = _clipboardLeft is int ? $"Скопировано{clip}" : _saveStatus;
        StatusText.ToolTip = _saveStatus;
    }

    // =============== настройки (ISettingsHost) ===============

    public AppSettings Settings => _settings;

    public string KdfDescription => _vault?.DescribeKdf() ?? "";

    private void Menu_Click(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>Настройки открываются только при открытом хранилище.</summary>
    public void OpenSettings()
    {
        if (!IsVisible) Expand();
        if (_vault == null) return;
        if (_editor != null) CloseEditor();
        SettingsPanel.Load(this);
        ShowPanel(SettingsPanel);
    }

    public void CloseSettings() => ShowPanel(_vault != null ? ListPanel : LockPanel);

    /// <summary>Автозапуск включается один раз — при первом запуске приложения.</summary>
    private void FirstRunDefaults()
    {
        if (_settings.AutostartInitialized)
        {
            try { Autostart.FollowMovedExe(); } catch (Exception) { /* не вышло — переключатель покажет */ }
            return;
        }
        try { Autostart.Set(true); } catch (Exception) { /* не вышло — переключатель в настройках покажет */ }
        _settings.AutostartInitialized = true;
        _settings.Save();
    }

    public void ApplySettings()
    {
        _tray.Visible = _settings.ShowTrayIcon;
        if (!_settings.ShowPeekTab) _peek.Hide();
        else if (!IsVisible) _peek.ShowAtCorner();
        try { _settings.Save(); }
        catch (Exception ex) { _saveStatus = "Настройки не сохранились: " + ex.Message; UpdateStatus(); }
    }

    public bool TryApplyHotkey(string gesture) => _hotkey.TrySet(gesture);

    public async Task<string?> ChangePasswordAsync(string oldPassword, string newPassword)
    {
        if (_vault == null) return "Хранилище заблокировано.";
        var vault = _vault;
        await _saveTask;
        try
        {
            await Task.Run(() => vault.ChangePassword(oldPassword, newPassword));
            // Быстрый вход хранит старый пароль — он больше не годится.
            if (_quick.Mode != QuickUnlockMode.Off) DisableQuickUnlock();
            return null;
        }
        catch (WrongPasswordException) { return "Текущий пароль неверный."; }
        catch (Exception ex) { return "Не удалось сменить пароль: " + ex.Message; }
    }

    public void OpenVaultFolder()
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(_settings.VaultPath))!;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private async void ExitLink_Click(object sender, RoutedEventArgs e) => await ExitAsync();

    public QuickUnlockMode QuickMode => _quick.Mode;

    public Task<bool> IsHelloSupportedAsync() => _hello.IsSupportedAsync();

    public async Task<string?> EnableQuickUnlockAsync(QuickUnlockMode mode, string masterPassword, string? pin)
    {
        if (_vault == null) return "Хранилище заблокировано.";
        if (!_vault.CheckPassword(masterPassword)) return "Мастер-пароль неверный.";
        try
        {
            if (mode == QuickUnlockMode.WindowsHello)
                await WithoutTopmost(async () => { await _quick.EnableAsync(mode, masterPassword, signer: _hello); return 0; });
            else
                await Task.Run(() => _quick.EnableAsync(mode, masterPassword, pin));
            return null;
        }
        catch (QuickUnlockException ex) { return ex.Message; }
        catch (OperationCanceledException) { return "Отменено в окне Windows Hello."; }
        catch (Exception ex) { return "Не удалось включить: " + ex.Message; }
    }

    public void DisableQuickUnlock()
    {
        if (_quick.Mode == QuickUnlockMode.WindowsHello) _ = _hello.DeleteKeyAsync();
        _quick.Disable();
    }

    // =============== импорт (IImportHost) ===============

    public void OpenImport()
    {
        if (_vault == null) return;
        ImportPanel.Start(this);
        ShowPanel(ImportPanel);
    }

    public void CloseImport() => ShowPanel(_vault != null ? ListPanel : LockPanel);

    public HashSet<string> ExistingLoginKeys() => _vault?.ExistingLoginKeys() ?? new();

    public int Import(IReadOnlyList<ImportedLogin> logins)
    {
        if (_vault == null) return 0;
        int n = _vault.Import(logins);
        ReloadEntries();
        return n;
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        await LockAsync();
        DisposeServices();
        _peek.Close();
        Application.Current.Shutdown();
    }

    private void DisposeServices()
    {
        _autoLock.Dispose();
        _hotkey.Dispose();
        _tray.Dispose(); // иначе значок висит в трее до наведения мыши
    }

    /// <summary>Синхронное сохранение при выходе из Windows.</summary>
    public void FlushForSessionEnd()
    {
        _clipboard.ClearNow();
        try { _saveTask.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _vault?.Dispose();
        _vault = null;
        DisposeServices();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 и закрытие системой — тоже через сохранение.
        if (!_exiting)
        {
            e.Cancel = true;
            _ = ExitAsync();
        }
        base.OnClosing(e);
    }
}
