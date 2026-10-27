using SecretShelf.Core;

namespace SecretShelf.Core.Tests;

public sealed class CsvImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "secretshelf-csv-" + Guid.NewGuid().ToString("N"));

    public CsvImportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Chrome_format_with_quotes_commas_and_newlines()
    {
        string csv = "﻿name,url,username,password,note\r\n" +
                     "mail.ru,https://www.mail.ru/login,ivan@mail.ru,\"p,a\"\"ss\",\r\n" +
                     ",https://github.com/,northta,gh,\"строка 1\nстрока 2\"\r\n";
        var rows = CsvImport.Parse(csv);
        Assert.Equal(2, rows.Count);
        Assert.Equal("mail.ru", rows[0].Title);
        Assert.Equal("p,a\"ss", rows[0].Password);
        Assert.Equal("github.com", rows[1].Title); // пустое имя — берётся сайт
        Assert.Equal("строка 1\nстрока 2", rows[1].Note);
    }

    [Fact]
    public void Firefox_format_is_recognized()
    {
        string csv = "\"url\",\"username\",\"password\",\"httpRealm\",\"formActionOrigin\",\"guid\",\"timeCreated\",\"timeLastUsed\",\"timePasswordChanged\"\n" +
                     "\"https://ya.ru\",\"ivan\",\"secret\",,\"https://ya.ru\",\"{1}\",\"1\",\"1\",\"1\"\n";
        var r = Assert.Single(CsvImport.Parse(csv));
        Assert.Equal(("ya.ru", "ivan", "secret"), (r.Title, r.UserName, r.Password));
    }

    [Fact]
    public void Not_a_password_export_is_rejected()
    {
        Assert.Throws<FormatException>(() => CsvImport.Parse("a,b,c\n1,2,3\n"));
    }

    [Fact]
    public void Import_adds_entries_and_dedup_sees_them()
    {
        string path = Path.Combine(_dir, "vault.kdbx");
        using var v = Vault.Create(path, "pass", kdfTargetMs: 50);
        var logins = CsvImport.Parse("name,url,username,password\nmail.ru,https://mail.ru,ivan,p1\n");

        Assert.DoesNotContain(logins[0].DedupKey, v.ExistingLoginKeys());
        Assert.Equal(1, v.Import(logins));
        Assert.Contains(logins[0].DedupKey, v.ExistingLoginKeys());

        var e = Assert.Single(v.GetEntries());
        Assert.Equal(new[] { "Логин", "Пароль", "Адрес" }, e.Fields.Select(f => f.Label));
        Assert.Equal(new[] { "UserName", "Password", "URL" }, e.Fields.Select(f => f.Key)); // поля KeePass
        Assert.True(e.Fields[1].IsSecret);
        Assert.Equal("p1", v.GetValue(e.Id, "Password"));
    }
}
