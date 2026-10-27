namespace SecretShelf.Core;

/// <summary>
/// Что разрешено ИИ-агенту делать с полем (используется MCP-сервером, заход 3).
/// По умолчанию поле для агентов закрыто.
/// </summary>
public enum FieldAccess
{
    /// <summary>Агент поля не видит вообще.</summary>
    Hidden = 0,
    /// <summary>Агент видит название поля, но значение подставить не может.</summary>
    NameOnly = 1,
    /// <summary>Агент может подставить значение в команду или файл, но не увидеть его.</summary>
    Inject = 2,
    /// <summary>Агент может получить значение. Только для несекретного: логин, адрес.</summary>
    Reveal = 3,
}

/// <summary>Описание поля без значения. Значение живёт в защищённой памяти KeePassLib.</summary>
public sealed record VaultField(
    string Key,
    string Label,
    bool IsSecret,
    FieldAccess Access,
    IReadOnlyList<string> AllowedTargets);

/// <summary>Запись хранилища: название и упорядоченный список полей.</summary>
public sealed record VaultEntry(string Id, string Title, IReadOnlyList<VaultField> Fields);

/// <summary>Поле при создании или правке записи. Key == null — новое поле.</summary>
public sealed class VaultFieldDraft
{
    public string? Key { get; set; }
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public bool IsSecret { get; set; }
    public FieldAccess Access { get; set; } = FieldAccess.Hidden;
    public List<string> AllowedTargets { get; set; } = new();
}

public sealed class WrongPasswordException : Exception
{
    public WrongPasswordException(Exception inner) : base("Неверный пароль хранилища.", inner) { }
}
