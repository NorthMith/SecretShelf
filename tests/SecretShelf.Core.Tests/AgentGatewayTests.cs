using SecretShelf.Core;
using SecretShelf.Core.Agents;

namespace SecretShelf.Core.Tests;

public sealed class AgentGatewayTests : IDisposable
{
    private const string Key = "sk-test-0123456789abcdef";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "secretshelf-agent-" + Guid.NewGuid().ToString("N"));
    private readonly Vault _vault;
    private readonly FakeConfirmer _confirm = new();
    private readonly AgentGateway _gw;
    private Vault? _current;
    private bool _enabled = true;

    private sealed class FakeConfirmer : IConfirmer
    {
        public ConfirmDecision Answer = ConfirmDecision.AllowOnce;
        public List<ConfirmRequest> Asked = new();
        public Task<ConfirmDecision> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
        {
            Asked.Add(request);
            return Task.FromResult(Answer);
        }
    }

    private DateTime _clock = new(2026, 10, 28, 12, 0, 0);

    public AgentGatewayTests()
    {
        Directory.CreateDirectory(_dir);
        _vault = Vault.Create(Path.Combine(_dir, "vault.kdbx"), "pass", kdfTargetMs: 50);
        _current = _vault;
        _gw = new AgentGateway(() => _current, _confirm, new AgentLog(Path.Combine(_dir, "log.jsonl")), () => _enabled, () => _clock);

        _vault.AddEntry("API A", new[]
        {
            new VaultFieldDraft { Label = "Сервис", Value = "Anthropic", Access = FieldAccess.Reveal },
            new VaultFieldDraft { Label = "Ключ API", Value = Key, IsSecret = true, Access = FieldAccess.Inject },
        });
        _vault.AddEntry("Банк", new[] { new VaultFieldDraft { Label = "Пароль", Value = "bank-secret-999", IsSecret = true } });
    }

    public void Dispose()
    {
        _vault.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private Task<AgentResponse> Call(AgentRequest r)
    {
        r.Client = "test-agent";
        return _gw.HandleAsync(r);
    }

    private static AgentRequest RunEcho(string entry = "API A", string field = "Ключ API") => new()
    {
        Op = "run", Command = "cmd", Args = { "/c", "echo value=%SS_KEY%" },
        Env = { new EnvSecret("SS_KEY", entry, field) },
    };

    [Fact]
    public async Task List_shows_only_open_fields_and_no_values()
    {
        var r = await Call(new AgentRequest { Op = "list" });
        Assert.True(r.Ok);
        Assert.Contains("API A / Ключ API", r.Text);
        Assert.DoesNotContain("Банк", r.Text);   // поле закрыто — агент о нём не знает
        Assert.DoesNotContain(Key, r.Text);
    }

    [Fact]
    public async Task Get_only_for_open_fields_and_hidden_looks_missing()
    {
        Assert.Equal("Anthropic", (await Call(new AgentRequest { Op = "get", Entry = "API A", Field = "Сервис" })).Text);
        var inject = await Call(new AgentRequest { Op = "get", Entry = "API A", Field = "Ключ API" });
        Assert.False(inject.Ok);
        Assert.DoesNotContain(Key, inject.Text);

        var hidden = await Call(new AgentRequest { Op = "get", Entry = "Банк", Field = "Пароль" });
        var missing = await Call(new AgentRequest { Op = "get", Entry = "Нет такой", Field = "Пароль" });
        Assert.False(hidden.Ok);
        Assert.Equal(missing.Text.Replace("Нет такой", "Банк"), hidden.Text);
    }

    [Fact]
    public async Task Run_injects_secret_masks_output_and_asks_owner()
    {
        var r = await Call(RunEcho());
        Assert.True(r.Ok, r.Text);
        Assert.Contains("value=***", r.Text);
        Assert.DoesNotContain(Key, r.Text);
        var asked = Assert.Single(_confirm.Asked);
        Assert.Contains("$SS_KEY ← API A / Ключ API", asked.Details);
        Assert.DoesNotContain(Key, asked.Details); // и владельцу значение не показывается
    }

    [Fact]
    public async Task Run_denied_by_owner_does_not_run()
    {
        _confirm.Answer = ConfirmDecision.Deny;
        string marker = Path.Combine(_dir, "ran.txt");
        var r = await Call(new AgentRequest
        {
            Op = "run", Command = "cmd", Args = { "/c", $"echo x> \"{marker}\"" },
            Env = { new EnvSecret("SS_KEY", "API A", "Ключ API") },
        });
        Assert.False(r.Ok);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task No_confirm_flag_skips_the_window()
    {
        SetField("API A", "Ключ API", d => d.NoConfirm = true);
        var r = await Call(RunEcho());
        Assert.True(r.Ok, r.Text);
        Assert.Empty(_confirm.Asked);
    }

    [Fact]
    public async Task Allowed_programs_are_enforced()
    {
        SetField("API A", "Ключ API", d => d.AllowedTargets = new() { "python" });
        var r = await Call(RunEcho());
        Assert.False(r.Ok);
        Assert.Contains("python", r.Text);
        Assert.Empty(_confirm.Asked); // отказ ещё до окна
    }

    [Fact]
    public async Task Store_from_env_file_creates_injectable_field_without_showing_value()
    {
        string env = Path.Combine(_dir, ".env");
        File.WriteAllText(env, "# comment\nOPENAI_API_KEY=\"sk-new-value-777\"\nOTHER=1\n");
        var r = await Call(new AgentRequest { Op = "store", Entry = "OpenAI", Field = "Ключ API", Source = "env_file", Path = env, Variable = "OPENAI_API_KEY" });
        Assert.True(r.Ok, r.Text);
        Assert.DoesNotContain("sk-new-value-777", r.Text);
        Assert.Single(_confirm.Asked);

        var e = _vault.GetEntries().Single(x => x.Title == "OpenAI");
        var f = Assert.Single(e.Fields);
        Assert.Equal(FieldAccess.Inject, f.Access);
        Assert.True(f.IsSecret);
        Assert.Equal("sk-new-value-777", _vault.GetValue(e.Id, f.Key));
        Assert.True(File.Exists(env)); // исходник не трогаем
    }

    [Fact]
    public async Task Store_from_file_keeps_multiline_key_intact()
    {
        string key = "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAAB3Nza\nC1yc2EAAA\n-----END OPENSSH PRIVATE KEY-----\n";
        string path = Path.Combine(_dir, "id_test");
        File.WriteAllText(path, key);
        var r = await Call(new AgentRequest { Op = "store", Entry = "VDS", Field = "SSH-ключ", Source = "file", Path = path });
        Assert.True(r.Ok, r.Text);
        var e = _vault.GetEntries().Single(x => x.Title == "VDS");
        Assert.Equal(key, _vault.GetValue(e.Id, e.Fields[0].Key));
    }

    [Fact]
    public async Task Store_cannot_overwrite_hidden_field()
    {
        string path = Path.Combine(_dir, "v.txt");
        File.WriteAllText(path, "x");
        var r = await Call(new AgentRequest { Op = "store", Entry = "Банк", Field = "Пароль", Source = "file", Path = path });
        Assert.False(r.Ok);
        var e = _vault.GetEntries().Single(x => x.Title == "Банк");
        Assert.Equal("bank-secret-999", _vault.GetValue(e.Id, e.Fields[0].Key));
    }

    [Fact]
    public async Task Delete_always_asks_even_with_no_confirm_flag()
    {
        SetField("API A", "Ключ API", d => d.NoConfirm = true);
        var r = await Call(new AgentRequest { Op = "delete", Entry = "API A", Field = "Ключ API" });
        Assert.True(r.Ok, r.Text);
        Assert.True(Assert.Single(_confirm.Asked).Destructive);
        Assert.Equal(new[] { "Сервис" }, _vault.GetEntries().Single(x => x.Title == "API A").Fields.Select(f => f.Label));
    }

    [Fact]
    public async Task Delete_entry_with_hidden_fields_is_refused()
    {
        var r = await Call(new AgentRequest { Op = "delete", Entry = "Банк" });
        Assert.False(r.Ok);
        Assert.Contains(_vault.GetEntries(), e => e.Title == "Банк");
    }

    [Fact]
    public async Task Write_file_renders_template_and_backs_up_old_file()
    {
        string path = Path.Combine(_dir, "app", ".env");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "OLD=1\n");
        var r = await Call(new AgentRequest { Op = "write_file", Path = path, Template = "ANTHROPIC_API_KEY={{API A/Ключ API}}\n" });
        Assert.True(r.Ok, r.Text);
        Assert.DoesNotContain(Key, r.Text);
        Assert.Equal($"ANTHROPIC_API_KEY={Key}\n", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, ".env.bak-*"));
    }

    [Fact]
    public async Task Scan_reports_names_and_never_values()
    {
        string proj = Path.Combine(_dir, "proj");
        Directory.CreateDirectory(proj);
        File.WriteAllText(Path.Combine(proj, ".env"), "TELEGRAM_TOKEN=123456789:AAsecretsecretsecretsecretsecretsecret\nDEBUG=1\n");
        File.WriteAllText(Path.Combine(proj, "config.py"), "KEY = 'ghp_abcdefghijklmnopqrstuvwxyz0123'\n");
        var r = await Call(new AgentRequest { Op = "scan", Directory = proj });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("TELEGRAM_TOKEN", r.Text);
        Assert.DoesNotContain("DEBUG", r.Text);
        Assert.Contains("GitHub token (строка 1)", r.Text);
        Assert.DoesNotContain("AAsecret", r.Text);
        Assert.DoesNotContain("ghp_abc", r.Text);
        Assert.Single(_confirm.Asked);
    }

    [Fact]
    public void Claude_credentials_are_forbidden()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.True(SecretScanner.IsForbidden(Path.Combine(home, ".claude", ".credentials.json")));
        Assert.True(SecretScanner.IsForbidden(Path.Combine(home, ".claude", "settings.json")));
        Assert.False(SecretScanner.IsForbidden(Path.Combine(home, ".claude", "hub", ".env")));
    }

    [Fact]
    public async Task Locked_or_disabled_gives_clear_answer()
    {
        _current = null;
        Assert.Contains("заблокировано", (await Call(new AgentRequest { Op = "list" })).Text);
        _current = _vault;
        _enabled = false;
        Assert.Contains("выключен", (await Call(new AgentRequest { Op = "list" })).Text);
    }

    [Fact]
    public async Task Pipe_round_trip()
    {
        string name = "SecretShelf.Test." + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        var server = AgentPipe.ServeAsync(_gw.HandleAsync, cts.Token, name);
        var r = await AgentPipe.SendAsync(new AgentRequest { Op = "get", Client = "pipe", Entry = "API A", Field = "Сервис" }, CancellationToken.None, name);
        Assert.True(r.Ok, r.Text);
        Assert.Equal("Anthropic", r.Text);
        cts.Cancel();
        await server;
    }

    // ---------- доверие сессии на час ----------

    private Task<AgentResponse> CallAs(string session, AgentRequest r)
    {
        r.Client = "test-agent";
        r.SessionKeyOverride = session;
        return _gw.HandleAsync(r);
    }

    [Fact]
    public async Task Trust_hour_skips_window_for_same_session_only()
    {
        _confirm.Answer = ConfirmDecision.TrustHour;
        Assert.True((await CallAs("s1", RunEcho())).Ok);
        Assert.True(Assert.Single(_confirm.Asked).CanTrust);

        Assert.True((await CallAs("s1", RunEcho())).Ok);
        Assert.Single(_confirm.Asked); // вторая команда той же сессии — без окна

        _confirm.Answer = ConfirmDecision.AllowOnce;
        Assert.True((await CallAs("s2", RunEcho())).Ok);
        Assert.Equal(2, _confirm.Asked.Count); // другая сессия того же агента — спрашивает
        Assert.Equal(new[] { "test-agent" }, _gw.ActiveTrust().Select(t => t.Client));
    }

    [Fact]
    public async Task Trust_covers_store_but_not_delete_or_scan()
    {
        _confirm.Answer = ConfirmDecision.TrustHour;
        await CallAs("s1", RunEcho());
        _confirm.Asked.Clear();

        string env = Path.Combine(_dir, ".env");
        File.WriteAllText(env, "X_TOKEN=abc12345\n");
        Assert.True((await CallAs("s1", new AgentRequest { Op = "store", Entry = "X", Field = "Token", Source = "env_file", Path = env, Variable = "X_TOKEN" })).Ok);
        Assert.Empty(_confirm.Asked); // добавление — под доверием

        _confirm.Answer = ConfirmDecision.AllowOnce;
        Assert.True((await CallAs("s1", new AgentRequest { Op = "delete", Entry = "X", Field = "Token" })).Ok);
        var del = Assert.Single(_confirm.Asked);
        Assert.False(del.CanTrust); // удаление — всегда окно и без кнопки доверия

        Assert.True((await CallAs("s1", new AgentRequest { Op = "scan", Directory = _dir })).Ok);
        Assert.Equal(2, _confirm.Asked.Count);
        Assert.False(_confirm.Asked[1].CanTrust);
    }

    [Fact]
    public async Task Trust_expires_after_an_hour_and_can_be_revoked()
    {
        _confirm.Answer = ConfirmDecision.TrustHour;
        await CallAs("s1", RunEcho());
        _clock = _clock.AddMinutes(59);
        await CallAs("s1", RunEcho());
        Assert.Single(_confirm.Asked);

        _clock = _clock.AddMinutes(2); // прошёл час с минутой
        _confirm.Answer = ConfirmDecision.TrustHour;
        await CallAs("s1", RunEcho());
        Assert.Equal(2, _confirm.Asked.Count);

        _gw.RevokeTrust();
        Assert.Empty(_gw.ActiveTrust());
        await CallAs("s1", RunEcho());
        Assert.Equal(3, _confirm.Asked.Count);
    }

    [Fact]
    public async Task Unknown_session_gets_no_trust_button()
    {
        _confirm.Answer = ConfirmDecision.TrustHour; // даже если окно вернёт «доверять» — не запоминать
        await Call(RunEcho());
        await Call(RunEcho());
        Assert.Equal(2, _confirm.Asked.Count);
        Assert.All(_confirm.Asked, a => Assert.False(a.CanTrust));
    }

    [Fact]
    public async Task Pipe_reports_real_client_process()
    {
        string name = "SecretShelf.Test." + Guid.NewGuid().ToString("N");
        int seen = 0;
        using var cts = new CancellationTokenSource();
        var server = AgentPipe.ServeAsync((req, ct) =>
        {
            seen = req.ClientProcessId;
            return Task.FromResult(AgentResponse.Success("ok"));
        }, cts.Token, name);
        await AgentPipe.SendAsync(new AgentRequest { Op = "list" }, CancellationToken.None, name);
        Assert.Equal(Environment.ProcessId, seen); // клиент — этот же тестовый процесс
        Assert.NotNull(AgentGateway.SessionKey(seen));
        cts.Cancel();
        await server;
    }

    private void SetField(string entry, string field, Action<VaultFieldDraft> change)
    {
        var e = _vault.GetEntries().Single(x => x.Title == entry);
        var drafts = e.Fields.Select(f => new VaultFieldDraft
        {
            Key = f.Key, Label = f.Label, Value = _vault.GetValue(e.Id, f.Key), IsSecret = f.IsSecret,
            Access = f.Access, AllowedTargets = f.AllowedTargets.ToList(), NoConfirm = f.NoConfirm,
        }).ToList();
        change(drafts.Single(d => d.Label == field));
        _vault.UpdateEntry(e.Id, e.Title, drafts);
    }
}
