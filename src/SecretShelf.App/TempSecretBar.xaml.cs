using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SecretShelf.Core.Agents;

namespace SecretShelf.App;

/// <summary>
/// Верхняя строка шторки — временный секрет. Владелец вставляет ключ для агента или
/// копирует ключ, положенный агентом. Значение живёт до первого использования.
/// </summary>
public partial class TempSecretBar : UserControl
{
    private TempSecret? _temp;
    private Func<string, bool>? _copy;
    private Action<string>? _toEntry;
    private bool _shown;
    private int _lastLength;

    public TempSecretBar() => InitializeComponent();

    /// <param name="copy">Положить значение в буфер обмена (с автоочисткой); false — не вышло.</param>
    /// <param name="toEntry">Открыть редактор новой записи с этим значением.</param>
    public void Attach(TempSecret temp, Func<string, bool> copy, Action<string> toEntry)
    {
        _temp = temp;
        _copy = copy;
        _toEntry = toEntry;
        temp.Changed += () => Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    public void Refresh()
    {
        if (_temp == null) return;
        var source = _temp.Source;
        if (source == TempSecretSource.None) _shown = false;

        EmptyBox.Visibility = source == TempSecretSource.None ? Visibility.Visible : Visibility.Collapsed;
        FilledBox.Visibility = source == TempSecretSource.None ? Visibility.Collapsed : Visibility.Visible;
        CopyButton.Visibility = SaveButton.Visibility = source == TempSecretSource.Agent ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = source == TempSecretSource.Owner ? Visibility.Visible : Visibility.Collapsed;

        Brush text = (Brush)FindResource("Text");
        switch (source)
        {
            case TempSecretSource.None:
                Frame.Background = (Brush)FindResource("Card");
                Frame.BorderBrush = (Brush)FindResource("LineStrong");
                Frame.BorderThickness = new Thickness(1);
                HeadIcon.Text = "";
                HeadText.Text = "Временный секрет";
                HeadIcon.Foreground = HeadText.Foreground = text;
                StatusText.Text = _temp.LastEvent ?? "пусто";
                StatusText.Foreground = (Brush)FindResource("TextDim");
                break;
            case TempSecretSource.Owner:
                Frame.Background = (Brush)FindResource("Card");
                Frame.BorderBrush = (Brush)FindResource("Accent");
                HeadIcon.Text = "";
                HeadText.Text = "Временный секрет";
                HeadIcon.Foreground = HeadText.Foreground = (Brush)FindResource("AccentText");
                StatusText.Text = $"ждёт агента · {_temp.PutAt:HH:mm}";
                StatusText.Foreground = (Brush)FindResource("TextDim");
                break;
            case TempSecretSource.Agent:
                Frame.Background = (Brush)FindResource("OkBg");
                Frame.BorderBrush = (Brush)FindResource("OkLine");
                HeadIcon.Text = "";
                HeadText.Text = $"{_temp.PutBy} положил ключ";
                HeadIcon.Foreground = HeadText.Foreground = (Brush)FindResource("Ok");
                StatusText.Text = $"{_temp.PutAt:HH:mm}";
                StatusText.Foreground = (Brush)FindResource("Ok");
                break;
        }

        ShowLabel.Text = _shown ? "Скрыть" : "Показать";
        int n = _temp.Length;
        ValueText.Text = _shown ? _temp.PeekByOwner() ?? "" : $"•••••••••••••••• {n} {Plural(n)}";
    }

    private static string Plural(int n) =>
        (n % 10 == 1 && n % 100 != 11) ? "символ" :
        (n % 10 is >= 2 and <= 4 && (n % 100 < 12 || n % 100 > 14)) ? "символа" : "символов";

    // ---------- вставка ----------

    private void PasteBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        int len = PasteBox.Password.Length;
        PastePlaceholder.Visibility = len == 0 ? Visibility.Visible : Visibility.Collapsed;
        PasteCommit.Visibility = len == 0 ? Visibility.Collapsed : Visibility.Visible;
        // Вставили сразу кусок (Ctrl+V, меню) — передаём без лишнего нажатия; набор руками ждёт Enter.
        bool pasted = len - _lastLength > 1;
        _lastLength = len;
        if (pasted) Commit();
    }

    private void PasteBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Commit();
        else if (e.Key == Key.Escape) PasteBox.Clear();
    }

    private void PasteCommit_Click(object sender, RoutedEventArgs e) => Commit();

    private void Commit()
    {
        string v = PasteBox.Password.Trim();
        _lastLength = 0;
        PasteBox.Clear();
        if (v.Length == 0 || _temp == null) return;
        _temp.PutByOwner(v);
    }

    // ---------- кнопки ----------

    private void Show_Click(object sender, RoutedEventArgs e)
    {
        _shown = !_shown;
        Refresh();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        // Стираем только после удачного копирования: занятый буфер не должен съесть ключ.
        if (_temp?.PeekByOwner() is { } v && _copy?.Invoke(v) == true) _temp.TakeByOwner();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_temp?.TakeByOwner() is { } v) _toEntry?.Invoke(v);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _temp?.Clear($"стёрто вами в {DateTime.Now:HH:mm}");
}
