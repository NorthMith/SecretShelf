using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SecretShelf.Core;

namespace SecretShelf.App;

/// <summary>Что экрану импорта нужно от шторки.</summary>
public interface IImportHost
{
    HashSet<string> ExistingLoginKeys();
    int Import(IReadOnlyList<ImportedLogin> logins);
    void CloseImport();
}

public partial class ImportView : UserControl
{
    private const int PreviewRows = 30;

    private IImportHost? _host;
    private string? _file;
    private List<ImportedLogin> _fresh = new();
    private bool _confirmDelete;

    public ImportView() => InitializeComponent();

    private sealed record Row(string Title, string Login, Brush Brush);

    /// <summary>Открыть экран заново: прежний выбор файла забывается.</summary>
    public void Start(IImportHost host)
    {
        _host = host;
        _file = null;
        _fresh = new();
        _confirmDelete = false;
        FileText.Text = ErrorText.Text = "";
        PreviewBlock.Visibility = DoneBlock.Visibility = Visibility.Collapsed;
        PreviewList.ItemsSource = null;
        ImportButton.Visibility = Visibility.Visible;
        ImportButton.IsEnabled = false;
        ImportButton.Content = "Добавить";
        CancelButton.Content = "Отмена";
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _host?.CloseImport();

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Выгрузка паролей (*.csv)|*.csv", Title = "Файл CSV с паролями" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        LoadFile(dlg.FileName);
    }

    private void LoadFile(string path)
    {
        if (_host == null) return;
        ErrorText.Text = "";
        PreviewBlock.Visibility = DoneBlock.Visibility = Visibility.Collapsed;
        ImportButton.IsEnabled = false;
        _file = path;
        FileText.Text = Path.GetFileName(path);

        List<ImportedLogin> all;
        try
        {
            all = CsvImport.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = ex.Message;
            return;
        }

        var existing = _host.ExistingLoginKeys();
        var seen = new HashSet<string>(existing);
        _fresh = new();
        var rows = new List<Row>();
        var dim = (Brush)FindResource("TextMuted");
        var text = (Brush)FindResource("Text");
        int dup = 0;
        foreach (var l in all)
        {
            bool isNew = seen.Add(l.DedupKey); // дубль — и с хранилищем, и внутри самого файла
            if (isNew) _fresh.Add(l); else dup++;
            if (rows.Count < PreviewRows)
                rows.Add(new Row(isNew ? l.Title : l.Title + " · уже есть", l.UserName, isNew ? text : dim));
        }

        SummaryText.Text = $"Найдено {all.Count} · новых {_fresh.Count} · уже есть {dup}";
        PreviewList.ItemsSource = rows;
        MoreText.Text = all.Count > PreviewRows ? $"…и ещё {all.Count - PreviewRows}" : "";
        PreviewBlock.Visibility = Visibility.Visible;
        ImportButton.Content = $"Добавить {_fresh.Count}";
        ImportButton.IsEnabled = _fresh.Count > 0;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_host == null || _fresh.Count == 0) return;
        int n;
        try
        {
            n = _host.Import(_fresh);
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Импорт не удался: " + ex.Message;
            return;
        }
        _fresh = new();
        DoneText.Text = $"Добавлено записей: {n}";
        DoneBlock.Visibility = Visibility.Visible;
        DeleteCsvButton.Visibility = Visibility.Visible;
        DeleteCsvButton.Content = "Удалить файл CSV";
        DeleteText.Text = "";
        ImportButton.Visibility = Visibility.Collapsed;
        CancelButton.Content = "Готово";
    }

    /// <summary>Удаление файла — в два щелчка: «Удалить» → «Точно удалить?».</summary>
    private void DeleteCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_file == null) return;
        if (!_confirmDelete)
        {
            _confirmDelete = true;
            DeleteCsvButton.Content = $"Точно удалить {Path.GetFileName(_file)}?";
            return;
        }
        try
        {
            File.Delete(_file); // мимо корзины: в корзине файл остался бы открытым текстом
            DeleteCsvButton.Visibility = Visibility.Collapsed;
            DeleteText.Text = "Файл удалён, мимо корзины. На SSD Windows не гарантирует физическое затирание — " +
                              "проверьте, не осталось ли копий в «Загрузках».";
        }
        catch (Exception ex)
        {
            DeleteText.Text = "Не удалось удалить: " + ex.Message;
        }
        _confirmDelete = false;
    }
}
