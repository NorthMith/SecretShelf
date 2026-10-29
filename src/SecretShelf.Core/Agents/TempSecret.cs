namespace SecretShelf.Core.Agents;

/// <summary>Кто положил временный секрет.</summary>
public enum TempSecretSource
{
    None,
    /// <summary>Владелец вставил в шторке — забирает агент.</summary>
    Owner,
    /// <summary>Агент положил — забирает владелец копированием.</summary>
    Agent,
}

/// <summary>
/// Временный секрет — одна передача между владельцем и агентом, мимо переписки.
/// Живёт только в памяти, стирается при первом использовании и при блокировке шторки.
/// Положенное владельцем забирает агент, положенное агентом — только владелец:
/// так агенты не передают друг другу значения через шторку.
/// </summary>
public sealed class TempSecret
{
    private readonly object _lock = new();
    private readonly Func<DateTime> _now;
    private string? _value;

    public TempSecret(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.Now);

    public TempSecretSource Source { get; private set; }
    /// <summary>Агент, положивший значение (для подписи в шторке).</summary>
    public string? PutBy { get; private set; }
    public DateTime? PutAt { get; private set; }
    /// <summary>Что случилось последним — «забрал claude-code в 14:02», для строки в шторке.</summary>
    public string? LastEvent { get; private set; }

    public bool IsEmpty { get { lock (_lock) return _value == null; } }

    /// <summary>Значение, состояние или подпись изменились — шторке пора перерисовать строку.</summary>
    public event Action? Changed;

    public void PutByOwner(string value) => Put(value, TempSecretSource.Owner, null, "вставлено вами");

    public void PutByAgent(string value, string client) => Put(value, TempSecretSource.Agent, client, $"положил {client}");

    private void Put(string value, TempSecretSource source, string? by, string what)
    {
        if (string.IsNullOrEmpty(value)) throw new ArgumentException("Пустое значение.", nameof(value));
        lock (_lock)
        {
            _value = value;
            Source = source;
            PutBy = by;
            PutAt = _now();
            LastEvent = $"{what} в {PutAt:HH:mm}";
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Забрать значение агентом: только положенное владельцем. После вызова секрет стёрт.
    /// null — забирать нечего; причина в <paramref name="why"/>.
    /// </summary>
    public string? TakeByAgent(string client, out string why)
    {
        string? v;
        lock (_lock)
        {
            if (_value == null) { why = "Временный секрет пуст. Попросите владельца вставить значение в верхнюю строку шторки."; return null; }
            if (Source != TempSecretSource.Owner)
            {
                why = "Во временном секрете лежит значение, которое положил агент, — забрать его может только владелец.";
                return null;
            }
            v = _value;
            ClearLocked($"забрал {client} в {_now():HH:mm}");
        }
        why = "";
        Changed?.Invoke();
        return v;
    }

    /// <summary>Посмотреть, есть ли что забрать агенту, не забирая (для проверки до запуска программы).</summary>
    public bool AvailableToAgent(out string why)
    {
        lock (_lock)
        {
            if (_value == null) { why = "Временный секрет пуст. Попросите владельца вставить значение в верхнюю строку шторки."; return false; }
            if (Source != TempSecretSource.Owner)
            {
                why = "Во временном секрете лежит значение, которое положил агент, — забрать его может только владелец.";
                return false;
            }
        }
        why = "";
        return true;
    }

    /// <summary>Владелец смотрит значение («Показать») — это не использование, секрет остаётся.</summary>
    public string? PeekByOwner()
    {
        lock (_lock) return _value;
    }

    /// <summary>Длина значения — для подписи «53 символа» без показа.</summary>
    public int Length { get { lock (_lock) return _value?.Length ?? 0; } }

    /// <summary>Владелец копирует значение (любое — своё или от агента). После вызова секрет стёрт.</summary>
    public string? TakeByOwner()
    {
        string? v;
        lock (_lock)
        {
            if (_value == null) return null;
            v = _value;
            ClearLocked($"скопировано вами в {_now():HH:mm}");
        }
        Changed?.Invoke();
        return v;
    }

    /// <summary>Стереть без использования (кнопка «Стереть», блокировка шторки).</summary>
    public void Clear(string reason)
    {
        lock (_lock)
        {
            if (_value == null) return;
            ClearLocked(reason);
        }
        Changed?.Invoke();
    }

    private void ClearLocked(string? lastEvent)
    {
        _value = null;
        Source = TempSecretSource.None;
        PutBy = null;
        PutAt = null;
        LastEvent = lastEvent;
    }
}
