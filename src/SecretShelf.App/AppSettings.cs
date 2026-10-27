using System.IO;
using System.Text.Json;

namespace SecretShelf.App;

/// <summary>
/// Несекретные настройки приложения: %APPDATA%\SecretShelf\settings.json.
/// Сам способ быстрого входа хранится не здесь, а в quick-unlock.json (QuickUnlockStore).
/// </summary>
public sealed class AppSettings
{
    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SecretShelf");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public string VaultPath { get; set; } = Path.Combine(Dir, "vault.kdbx");
    public double DrawerWidth { get; set; } = 360;

    // Буфер обмена
    public int ClipboardClearSeconds { get; set; } = 25;

    // Вход
    /// <summary>При быстром входе раз в столько дней спрашивать мастер-пароль; 0 — не спрашивать.</summary>
    public int MasterPasswordEveryDays { get; set; } = 14;

    // Блокировка
    /// <summary>Минуты бездействия всей системы до блокировки; 0 — не блокировать.</summary>
    public int IdleLockMinutes { get; set; } = 15;
    public bool LockOnSessionLock { get; set; } = true;

    // Вызов шторки
    public bool ShowPeekTab { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    /// <summary>Пусто — горячая клавиша выключена.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+P";
    public bool HideOnDeactivate { get; set; }

    /// <summary>Автозапуск включается один раз при первом запуске (так одобрено в макете).</summary>
    public bool AutostartInitialized { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (JsonException) { /* испорченный файл — работаем на умолчаниях, файл не трогаем */ }
        return new();
    }

    /// <summary>Запись через временный файл: обрыв на середине не испортит настройки.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Dir);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
