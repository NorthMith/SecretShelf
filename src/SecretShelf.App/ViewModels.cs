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

    public string? Key { get; init; }
    public FieldAccess Access { get; init; }
    public List<string> AllowedTargets { get; init; } = new();

    public string Label { get => _label; set => Set(ref _label, value); }
    public string Value { get => _value; set => Set(ref _value, value); }

    public bool IsSecret
    {
        get => _isSecret;
        set { if (Set(ref _isSecret, value)) Raise(nameof(ShowPlain)); }
    }

    /// <summary>Секрет показан открыто (и редактируется многострочным полем).</summary>
    public bool IsRevealed
    {
        get => _isRevealed;
        set { if (Set(ref _isRevealed, value)) Raise(nameof(ShowPlain)); }
    }

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

    public string? EntryId { get; init; }
    public bool IsNew => EntryId == null;
    public string Header => IsNew ? "Новая запись" : "Правка записи";

    public string Title { get => _title; set => Set(ref _title, value); }
    public ObservableCollection<FieldDraftVm> Fields { get; } = new();

    public bool ConfirmDelete { get => _confirmDelete; set => Set(ref _confirmDelete, value); }

    /// <summary>Заготовки для новой записи — сколько полей и какие.</summary>
    public void ApplyTemplate(string name)
    {
        Fields.Clear();
        (string label, bool secret)[] fields = name switch
        {
            "login" => new[] { ("Логин", false), ("Пароль", true) },
            "api" => new[] { ("Сервис", false), ("Ключ API", true) },
            "server" => new[] { ("Адрес", false), ("Пользователь", false), ("SSH-ключ", true) },
            _ => new[] { ("Поле", false) },
        };
        foreach (var (label, secret) in fields)
            Fields.Add(new FieldDraftVm { Label = label, IsSecret = secret, IsRevealed = false });
    }
}
