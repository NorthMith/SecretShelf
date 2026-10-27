using System.IO;
using System.Text.Json;

namespace SecretShelf.App;

/// <summary>
/// Несекретные настройки приложения: %APPDATA%\SecretShelf\settings.json.
/// Способы отпирания, вызова шторки и автоблокировка — заход 2.
/// </summary>
public sealed class AppSettings
{
    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SecretShelf");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public string VaultPath { get; set; } = Path.Combine(Dir, "vault.kdbx");
    public int ClipboardClearSeconds { get; set; } = 25;
    public double DrawerWidth { get; set; } = 360;

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

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
