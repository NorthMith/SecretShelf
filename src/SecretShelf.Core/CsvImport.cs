using System.Text;

namespace SecretShelf.Core;

/// <summary>Логин из выгрузки браузера.</summary>
public sealed record ImportedLogin(string Title, string Url, string UserName, string Password, string Note)
{
    /// <summary>Ключ для отсева дублей: сайт + логин.</summary>
    public string DedupKey => CsvImport.DedupKey(CsvImport.Host(Url).Length > 0 ? CsvImport.Host(Url) : Title, UserName);
}

/// <summary>
/// Импорт паролей из CSV-выгрузки браузера. Колонки ищутся по заголовку, поэтому
/// подходят Chrome/Edge (name,url,username,password,note), Firefox (url,username,password,…)
/// и Safari (Title,URL,Username,Password,Notes,…).
/// </summary>
public static class CsvImport
{
    private static readonly string[] TitleCols = { "name", "title" };
    private static readonly string[] UrlCols = { "url", "origin", "website", "login_uri" };
    private static readonly string[] UserCols = { "username", "login", "login_username", "user" };
    private static readonly string[] PassCols = { "password", "login_password" };
    private static readonly string[] NoteCols = { "note", "notes", "comment" };

    public static List<ImportedLogin> Parse(string text)
    {
        var rows = ReadRows(text);
        if (rows.Count == 0) throw new FormatException("Файл пустой.");

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string[] names) => header.FindIndex(names.Contains);
        int title = Col(TitleCols), url = Col(UrlCols), user = Col(UserCols), pass = Col(PassCols), note = Col(NoteCols);
        if (pass < 0 || (url < 0 && title < 0))
            throw new FormatException("Не похоже на выгрузку паролей браузера: нет колонок password и url/name.");

        string Get(List<string> r, int i) => i >= 0 && i < r.Count ? r[i].Trim() : "";
        var result = new List<ImportedLogin>();
        foreach (var r in rows.Skip(1))
        {
            if (r.All(string.IsNullOrWhiteSpace)) continue;
            string u = Get(r, url), t = Get(r, title);
            if (t.Length == 0) t = Host(u);
            if (t.Length == 0) t = "Без названия";
            result.Add(new ImportedLogin(t, u, Get(r, user), Get(r, pass), Get(r, note)));
        }
        return result;
    }

    /// <summary>«https://www.mail.ru/login» → «mail.ru». Не адрес — пустая строка.</summary>
    public static string Host(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        string u = url.Contains("://") ? url : "https://" + url;
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return "";
        string h = uri.Host.ToLowerInvariant();
        return h.StartsWith("www.") ? h[4..] : h;
    }

    public static string DedupKey(string site, string user) =>
        site.Trim().ToLowerInvariant() + "\u0001" + user.Trim().ToLowerInvariant();

    /// <summary>CSV по RFC 4180: кавычки, удвоенные кавычки, запятые и переводы строк внутри полей.</summary>
    internal static List<List<string>> ReadRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    rows.Add(row); row = new List<string>();
                    break;
                case '﻿' when i == 0: break; // BOM
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}
