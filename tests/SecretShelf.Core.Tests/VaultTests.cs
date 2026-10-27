using System.Diagnostics;
using SecretShelf.Core;

namespace SecretShelf.Core.Tests;

public sealed class VaultTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "secretshelf-tests-" + Guid.NewGuid().ToString("N"));
    private string VaultPath => Path.Combine(_dir, "vault.kdbx");

    public VaultTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static VaultFieldDraft F(string label, string value, bool secret = false) =>
        new() { Label = label, Value = value, IsSecret = secret };

    [Fact]
    public void Entries_and_field_order_survive_reopen()
    {
        using (var v = Vault.Create(VaultPath, "pass", kdfTargetMs: 50))
        {
            v.AddEntry("Mail A", new[] { F("Логин", "a@x.ru"), F("Пароль", "p1", true) });
            v.AddEntry("Server", new[] { F("Адрес", "10.0.0.1"), F("Пользователь", "root"), F("SSH-ключ", "KEY", true), F("Пароль sudo", "s", true) });
            v.SaveIfDirty();
        }

        using var r = Vault.Open(VaultPath, "pass");
        var entries = r.GetEntries();
        Assert.Equal(new[] { "Mail A", "Server" }, entries.Select(e => e.Title));

        var server = entries[1];
        Assert.Equal(new[] { "Адрес", "Пользователь", "SSH-ключ", "Пароль sudo" }, server.Fields.Select(f => f.Label));
        Assert.Equal(new[] { false, false, true, true }, server.Fields.Select(f => f.IsSecret));
        Assert.Equal("KEY", r.GetValue(server.Id, server.Fields[2].Key));
        // Первое открытое поле — UserName, первое секретное — Password (совместимость с KeePass).
        Assert.Equal("UserName", server.Fields[0].Key);
        Assert.Equal("Password", server.Fields[2].Key);
    }

    [Fact]
    public void Reorder_is_persisted()
    {
        using (var v = Vault.Create(VaultPath, "pass", kdfTargetMs: 50))
        {
            foreach (var t in new[] { "A", "B", "C", "D" }) v.AddEntry(t, new[] { F("x", t) });
            var d = v.GetEntries()[3];
            v.MoveEntry(d.Id, 0);
            v.MoveEntry(v.GetEntries()[1].Id, 3); // A в конец
            v.SaveIfDirty();
        }
        using var r = Vault.Open(VaultPath, "pass");
        Assert.Equal(new[] { "D", "B", "C", "A" }, r.GetEntries().Select(e => e.Title));
    }

    [Fact]
    public void Wrong_password_is_reported()
    {
        Vault.Create(VaultPath, "right", kdfTargetMs: 50).Dispose();
        Assert.Throws<WrongPasswordException>(() => Vault.Open(VaultPath, "wrong"));
    }

    [Fact]
    public void Update_keeps_keys_and_access_level()
    {
        using var v = Vault.Create(VaultPath, "pass", kdfTargetMs: 50);
        var e = v.AddEntry("API A", new[] { F("Ключ", "k1", true) });
        var drafts = e.Fields.Select(f => new VaultFieldDraft
        {
            Key = f.Key, Label = "API key", Value = "k2", IsSecret = true,
            Access = FieldAccess.Inject, AllowedTargets = { "api.anthropic.com" },
        }).ToList();
        drafts.Add(F("Аккаунт", "me"));
        var u = v.UpdateEntry(e.Id, "API A", drafts);

        Assert.Equal(e.Fields[0].Key, u.Fields[0].Key);
        Assert.Equal(FieldAccess.Inject, u.Fields[0].Access);
        Assert.Equal(new[] { "api.anthropic.com" }, u.Fields[0].AllowedTargets);
        Assert.Equal("k2", v.GetValue(u.Id, u.Fields[0].Key));
        Assert.Equal("me", v.GetValue(u.Id, u.Fields[1].Key));
    }

    [Fact]
    public void Delete_removes_entry()
    {
        using (var v = Vault.Create(VaultPath, "pass", kdfTargetMs: 50))
        {
            var a = v.AddEntry("A", new[] { F("x", "1") });
            v.AddEntry("B", new[] { F("x", "2") });
            v.DeleteEntry(a.Id);
        }
        using var r = Vault.Open(VaultPath, "pass");
        Assert.Equal(new[] { "B" }, r.GetEntries().Select(e => e.Title));
    }

    [Fact]
    public void Backup_is_made_before_overwrite()
    {
        using var v = Vault.Create(VaultPath, "pass", kdfTargetMs: 50);
        v.AddEntry("A", new[] { F("x", "1") });
        v.SaveIfDirty();
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "backups"), "vault.bak-*.kdbx"));
    }

    [Fact]
    public void Change_password_needs_old_and_makes_backup()
    {
        using (var v = Vault.Create(VaultPath, "old-pass", kdfTargetMs: 50))
        {
            v.AddEntry("A", new[] { F("x", "1") });
            v.SaveIfDirty();
            Assert.Throws<WrongPasswordException>(() => v.ChangePassword("not-it", "new-pass"));
            v.ChangePassword("old-pass", "new-pass");
        }
        Assert.Throws<WrongPasswordException>(() => Vault.Open(VaultPath, "old-pass"));
        using var r = Vault.Open(VaultPath, "new-pass");
        Assert.Equal(new[] { "A" }, r.GetEntries().Select(e => e.Title));
        // Копия со старым паролем лежит в backups — откат возможен.
        var backups = Directory.GetFiles(Path.Combine(_dir, "backups"), "vault.bak-*.kdbx");
        Assert.NotEmpty(backups);
        Vault.Open(backups.OrderBy(b => b).Last(), "old-pass").Dispose();
    }

    [Fact]
    public void Kdf_calibration_hits_target_roughly()
    {
        // Замер на этой машине: сколько реально идёт открытие при цели 400 мс.
        Vault.Create(VaultPath, "pass", kdfTargetMs: 400).Dispose();
        var sw = Stopwatch.StartNew();
        Vault.Open(VaultPath, "pass").Dispose();
        sw.Stop();
        Console.WriteLine($"open: {sw.ElapsedMilliseconds} ms");
        Assert.InRange(sw.ElapsedMilliseconds, 100, 2000);
    }
}
