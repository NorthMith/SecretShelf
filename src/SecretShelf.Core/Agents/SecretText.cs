using System.Text;
using System.Text.RegularExpressions;

namespace SecretShelf.Core.Agents;

/// <summary>Маскировка секретов в выводе программ и разбор .env-файлов.</summary>
public static class SecretText
{
    public const string Mask = "***";

    /// <summary>
    /// Заменяет в тексте значения секретов на ***. Ловит только точное совпадение
    /// (целиком и построчно для многострочных ключей). Перекодированное значение
    /// (base64, hex) не поймает — это граница защиты, о ней сказано владельцу.
    /// </summary>
    public static string MaskAll(string text, IEnumerable<string> secrets)
    {
        var parts = new List<string>();
        foreach (var s in secrets)
        {
            if (string.IsNullOrEmpty(s)) continue;
            string v = s.Trim();
            if (v.Length >= 4) parts.Add(v);
            // Многострочный ключ: программа может напечатать его по строкам.
            foreach (var line in v.Split('\n'))
            {
                string l = line.Trim();
                if (l.Length >= 8) parts.Add(l);
            }
        }
        // Длинные первыми, чтобы целое значение заменилось раньше своих кусков.
        foreach (var p in parts.Distinct().OrderByDescending(p => p.Length))
            text = text.Replace(p, Mask, StringComparison.Ordinal);
        return text;
    }

    /// <summary>Обрезать длинный вывод: агенту не нужны мегабайты, а переписка их не выдержит.</summary>
    public static string Truncate(string text, int max = 20_000) =>
        text.Length <= max ? text : text[..max] + $"\n… [обрезано, всего {text.Length} символов]";

    private static readonly Regex EnvLine = new(@"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_.-]*)\s*=\s*(.*)$");

    /// <summary>Разбор .env: КЛЮЧ=значение, кавычки снимаются, комментарии пропускаются.</summary>
    public static Dictionary<string, string> ParseEnv(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith('#')) continue;
            var m = EnvLine.Match(line);
            if (!m.Success) continue;
            string v = m.Groups[2].Value.Trim();
            if (v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\''))
                v = v[1..^1];
            else
            {
                int hash = v.IndexOf(" #", StringComparison.Ordinal); // комментарий в конце строки
                if (hash >= 0) v = v[..hash].TrimEnd();
            }
            result[m.Groups[1].Value] = v;
        }
        return result;
    }

    private static readonly Regex Placeholder = new(@"\{\{\s*([^{}/]+?)\s*/\s*([^{}]+?)\s*\}\}");

    /// <summary>Ссылки {{Запись/Поле}} в шаблоне.</summary>
    public static List<SecretRef> FindRefs(string template) =>
        Placeholder.Matches(template).Select(m => new SecretRef(m.Groups[1].Value, m.Groups[2].Value)).Distinct().ToList();

    /// <summary>Подставить значения в шаблон {{Запись/Поле}}.</summary>
    public static string Render(string template, Func<SecretRef, string> resolve) =>
        Placeholder.Replace(template, m => resolve(new SecretRef(m.Groups[1].Value, m.Groups[2].Value)));

    public const string TempRefName = "Временный";
    private static readonly Regex TempPlaceholder = new(@"\{\{\s*" + TempRefName + @"\s*\}\}", RegexOptions.IgnoreCase);

    /// <summary>Есть ли в шаблоне ссылка на временный секрет {{Временный}}.</summary>
    public static bool HasTempRef(string? template) => template != null && TempPlaceholder.IsMatch(template);

    /// <summary>Подставить временный секрет вместо {{Временный}}.</summary>
    public static string RenderTemp(string template, string value) => TempPlaceholder.Replace(template, _ => value);
}
