using Microsoft.Win32;

namespace SecretShelf.App.Services;

/// <summary>
/// Запуск вместе с Windows: значение в HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// Раздел пользовательский, прав администратора не нужно; снимается тем же переключателем.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SecretShelf";

    private static string ExePath => Environment.ProcessPath!;

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string v && v.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{ExePath}\"");
        else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName);
    }
}
