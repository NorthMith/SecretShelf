using System.Text;
using System.Text.RegularExpressions;

namespace SecretShelf.Core.Agents;

/// <summary>Найденный ключ: где он и как называется. Значения здесь нет никогда.</summary>
public sealed record SecretFinding(string Path, string Kind, string Name, int? Line = null);

/// <summary>
/// Опись ключей в папке: .env-файлы (имена переменных), файлы приватных ключей SSH,
/// известные виды токенов в коде и конфигах (файл и строка). Наружу уходят только
/// пути, имена и виды — значения не читаются в результат.
/// </summary>
public static class SecretScanner
{
    private const int MaxFiles = 5000;
    private const long MaxFileSize = 512 * 1024;

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea", "__pycache__", "venv", ".venv",
        "dist", "build", ".gradle", "packages", "Library", "Temp",
    };

    private static readonly HashSet<string> CodeExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".js", ".ts", ".cs", ".json", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        ".ps1", ".sh", ".bat", ".cmd", ".txt", ".md", ".dart", ".kt", ".java", ".go", ".rb", ".php",
    };

    /// <summary>Имена переменных, похожие на секреты.</summary>
    private static readonly Regex SecretName = new(@"(KEY|TOKEN|SECRET|PASS|PWD|AUTH|CREDENTIAL|PRIVATE)", RegexOptions.IgnoreCase);

    private static readonly (string kind, Regex re)[] TokenPatterns =
    {
        ("Anthropic API key", new Regex(@"sk-ant-api\d{2}-[A-Za-z0-9_\-]{20,}")),
        ("OpenAI-style key", new Regex(@"\bsk-(?:proj-)?[A-Za-z0-9_\-]{20,}")),
        ("GitHub token", new Regex(@"\b(?:ghp|gho|ghu|ghs|github_pat)_[A-Za-z0-9_]{20,}")),
        ("AWS access key", new Regex(@"\bAKIA[0-9A-Z]{16}\b")),
        ("Google API key", new Regex(@"\bAIza[0-9A-Za-z_\-]{35}\b")),
        ("Slack token", new Regex(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}")),
        ("Telegram bot token", new Regex(@"\b\d{8,10}:AA[A-Za-z0-9_\-]{30,}")),
        ("Private key block", new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")),
    };

    /// <summary>
    /// Пути, которые не трогаются никогда: учётные данные самих ИИ-клиентов
    /// (их нельзя вынимать для автоматики — это нарушение условий Anthropic) и само хранилище.
    /// </summary>
    public static bool IsForbidden(string path)
    {
        string p = System.IO.Path.GetFullPath(path).Replace('/', '\\');
        string name = System.IO.Path.GetFileName(p);
        if (name.Equals(".credentials.json", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.EndsWith(".kdbx", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Contains(@"\SecretShelf\quick-unlock", StringComparison.OrdinalIgnoreCase)) return true;
        // Папки ИИ-клиентов целиком, кроме Telegram-хаба (там токены ботов владельца).
        if (p.Contains(@"\.claude\hub\", StringComparison.OrdinalIgnoreCase)) return false;
        string[] clientDirs = { @"\.claude\", @"\.codex\", @"\.config\anthropic\", @"\AppData\Roaming\Claude\", @"\AppData\Local\Claude-Data\" };
        return clientDirs.Any(d => p.Contains(d, StringComparison.OrdinalIgnoreCase));
    }

    public static List<SecretFinding> Scan(string directory, int maxDepth = 6)
    {
        var found = new List<SecretFinding>();
        int files = 0;
        Walk(new DirectoryInfo(directory), 0);
        return found;

        void Walk(DirectoryInfo dir, int depth)
        {
            if (depth > maxDepth || files >= MaxFiles) return;
            FileInfo[] fs;
            DirectoryInfo[] ds;
            try { fs = dir.GetFiles(); ds = dir.GetDirectories(); }
            catch (Exception) { return; } // нет доступа — молча пропустить

            foreach (var f in fs)
            {
                if (++files > MaxFiles) return;
                if (IsForbidden(f.FullName) || f.Length > MaxFileSize) continue;
                try { ScanFile(f, found); } catch (Exception) { /* занят или нечитаем — пропустить */ }
            }
            foreach (var d in ds)
                if (!SkipDirs.Contains(d.Name) && (d.Attributes & FileAttributes.ReparsePoint) == 0)
                    Walk(d, depth + 1);
        }
    }

    private static void ScanFile(FileInfo f, List<SecretFinding> found)
    {
        string name = f.Name;
        bool envFile = name.Equals(".env", StringComparison.OrdinalIgnoreCase)
                       || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
                       || name.EndsWith(".env", StringComparison.OrdinalIgnoreCase);
        if (envFile)
        {
            foreach (var key in SecretText.ParseEnv(File.ReadAllText(f.FullName)).Keys)
                if (SecretName.IsMatch(key))
                    found.Add(new SecretFinding(f.FullName, "env", key));
            return;
        }

        // Приватный ключ SSH и подобные: распознаём по первой строке.
        if (f.Extension.Length == 0 || f.Extension.Equals(".pem", StringComparison.OrdinalIgnoreCase)
                                    || f.Extension.Equals(".key", StringComparison.OrdinalIgnoreCase))
        {
            using var r = new StreamReader(f.FullName);
            string? first = r.ReadLine();
            if (first != null && first.StartsWith("-----BEGIN", StringComparison.Ordinal) && first.Contains("PRIVATE KEY"))
            {
                found.Add(new SecretFinding(f.FullName, "private_key", name));
                return;
            }
        }

        if (!CodeExt.Contains(f.Extension)) return;
        int lineNo = 0;
        foreach (var line in File.ReadLines(f.FullName))
        {
            lineNo++;
            foreach (var (kind, re) in TokenPatterns)
                if (re.IsMatch(line))
                {
                    found.Add(new SecretFinding(f.FullName, "token_in_file", kind, lineNo));
                    break;
                }
        }
    }

    /// <summary>Отчёт для агента: только пути, имена и виды.</summary>
    public static string Report(string directory, List<SecretFinding> found)
    {
        if (found.Count == 0) return $"В {directory} ключей не найдено.";
        var sb = new StringBuilder($"Найдено в {directory} (значения не показываются):\n");
        foreach (var g in found.GroupBy(x => x.Path))
        {
            sb.Append("- ").Append(g.Key).Append(": ");
            sb.AppendLine(string.Join(", ", g.Select(x => x.Kind switch
            {
                "env" => $"переменная {x.Name}",
                "private_key" => "приватный ключ",
                _ => $"{x.Name} (строка {x.Line})",
            })));
        }
        return sb.ToString();
    }
}
