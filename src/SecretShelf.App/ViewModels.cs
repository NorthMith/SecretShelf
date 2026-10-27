using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SecretShelf.Core;

namespace SecretShelf.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Поле в карточке списка. Секретное значение подгружается только по «глазу».</summary>
public sealed class FieldVm : Observable
{
    private const string Mask = "••••••••••";
    private string _display;
    private bool _isRevealed;

    public FieldVm(EntryVm owner, VaultField field, string? openValue)
    {
        Owner = owner;
        Field = field;
        _display = field.IsSecret ? Mask : openValue ?? "";
    }

    public EntryVm Owner { get; }
    public VaultField Field { get; }
    public string Label => Field.Label;
    public bool IsSecret => Field.IsSecret;

    public string Display
    {
        get => _display;
        private set => Set(ref _display, value);
    }

    public bool IsRevealed
    {
        get => _isRevealed;
        private set => Set(ref _isRevealed, value);
    }

    public void Reveal(string value) { Display = value; IsRevealed = true; }
    public void Conceal() { if (IsSecret) { Display = Mask; IsRevealed = false; } }
}

public sealed class EntryVm : Observable
{
    public EntryVm(VaultEntry model, Func<VaultField, string> readOpenValue)
    {
        Model = model;
        Fields = new ObservableCollection<FieldVm>(
            model.Fields.Select(f => new FieldVm(this, f, f.IsSecret ? null : readOpenValue(f))));
    }

    private bool _isDragging;

    public VaultEntry Model { get; }
    public string Id => Model.Id;
    public bool IsDragging { get => _isDragging; set => Set(ref _isDragging, value); }
    public string Title => Model.Title;
    public ObservableCollection<FieldVm> Fields { get; }
}

/// <summary>Поле в редакторе записи.</summary>
public sealed class FieldDraftVm : Observable
{
    private string _label = "";
    private string _value = "";
    private bool _isSecret;
    private bool _isRevealed;
    private bool _isRenaming;
    private string? _placeholder;

    public string? Key { get; init; }
    public FieldAccess Access { get; init; }
    public List<string> AllowedTargets { get; init; } = new();

    public string Label { get => _label; set => Set(ref _label, value); }
    public string Value { get => _value; set => Set(ref _value, value); }

    /// <summary>Серая подсказка-пример в пустом поле.</summary>
    public string Placeholder
    {
        get => _placeholder ?? (IsSecret ? "будет скрыт" : "значение");
        set => Set(ref _placeholder, value);
    }

    public bool IsSecret
    {
        get => _isSecret;
        set
        {
            if (!Set(ref _isSecret, value)) return;
            Raise(nameof(ShowPlain));
            Raise(nameof(Placeholder));
        }
    }

    /// <summary>Секрет показан открыто (и редактируется многострочным полем).</summary>
    public bool IsRevealed
    {
        get => _isRevealed;
        set { if (Set(ref _isRevealed, value)) Raise(nameof(ShowPlain)); }
    }

    /// <summary>Подпись поля редактируется (щелчок по карандашу).</summary>
    public bool IsRenaming { get => _isRenaming; set => Set(ref _isRenaming, value); }

    public bool ShowPlain => !IsSecret || IsRevealed;

    public VaultFieldDraft ToDraft() => new()
    {
        Key = Key, Label = Label, Value = Value, IsSecret = IsSecret,
        Access = Access, AllowedTargets = AllowedTargets,
    };
}

public sealed class EditorVm : Observable
{
    private string _title = "";
    private bool _confirmDelete;
    private string _template = "";

    public string? EntryId { get; init; }
    public bool IsNew => EntryId == null;
    public string Header => IsNew ? "Новая запись" : "Правка записи";

    public string Title { get => _title; set => Set(ref _title, value); }
    public ObservableCollection<FieldDraftVm> Fields { get; } = new();

    public bool ConfirmDelete { get => _confirmDelete; set => Set(ref _confirmDelete, value); }

    /// <summary>Выбранная заготовка — для подсветки переключателя.</summary>
    public string Template { get => _template; private set => Set(ref _template, value); }

    private static readonly Dictionary<string, (string label, bool secret, string hint)[]> Templates = new()
    {
        ["login"] = new[] { ("Логин", false, "ivan@yandex.ru"), ("Пароль", true, "будет скрыт") },
        ["api"] = new[] { ("Сервис", false, "Anthropic"), ("Ключ API", true, "sk-… будет скрыт") },
        ["server"] = new[] { ("Адрес", false, "203.0.113.10"), ("Пользователь", false, "root"), ("SSH-ключ", true, "вставьте ключ целиком, будет скрыт") },
        ["custom"] = new[] { ("Поле", false, "значение") },
    };

    /// <summary>
    /// Заготовка задаёт набор полей. Уже введённые значения переносятся
    /// в поля с тем же названием, чтобы переключение не стирало набранное.
    /// </summary>
    public void ApplyTemplate(string name)
    {
        var typed = Fields.Where(f => f.Value.Length > 0)
                          .GroupBy(f => f.Label).ToDictionary(g => g.Key, g => g.First().Value);
        Fields.Clear();
        foreach (var (label, secret, hint) in Templates[name])
            Fields.Add(new FieldDraftVm
            {
                Label = label, IsSecret = secret, Placeholder = hint,
                Value = typed.TryGetValue(label, out var v) ? v : "",
            });
        Template = name;
    }

    public void MoveField(FieldDraftVm f, int delta)
    {
        int i = Fields.IndexOf(f), j = i + delta;
        if (i < 0 || j < 0 || j >= Fields.Count) return;
        Fields.Move(i, j);
    }
}
