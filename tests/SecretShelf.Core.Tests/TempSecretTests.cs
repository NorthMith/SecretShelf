using SecretShelf.Core;
using SecretShelf.Core.Agents;

namespace SecretShelf.Core.Tests;

/// <summary>Временный секрет: одна передача между владельцем и агентом, без подтверждений.</summary>
public sealed class TempSecretTests : IDisposable
{
    private const string Pasted = "tmp-key-0123456789abcdef";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "secretshelf-temp-" + Guid.NewGuid().ToString("N"));
    private readonly Vault _vault;
    private readonly List<ConfirmRequest> _asked = new();
    private readonly AgentGateway _gw;

    private sealed class CountingConfirmer : IConfirmer
    {
        private readonly List<ConfirmRequest> _asked;
        public CountingConfirmer(List<ConfirmRequest> asked) => _asked = asked;
        public Task<ConfirmDecision> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
        {
            _asked.Add(request);
            return Task.FromResult(ConfirmDecision.AllowOnce);
        }
    }

    public TempSecretTests()
    {
        Directory.CreateDirectory(_dir);
        _vault = Vault.Create(Path.Combine(_dir, "vault.kdbx"), "pass", kdfTargetMs: 50);
        _vault.AddEntry("API A", new[] { new VaultFieldDraft { Label = "Ключ API", Value = "vault-key-abcdef123", IsSecret = true, Access = FieldAccess.Inject } });
        _gw = new AgentGateway(() => _vault, new CountingConfirmer(_asked), new AgentLog(Path.Combine(_dir, "log.jsonl")), () => true);
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

    [Fact]
    public async Task Take_returns_owner_value_once_without_window()
    {
        _gw.Temp.PutByOwner(Pasted);
        var r = await Call(new AgentRequest { Op = "temp_take" });
        Assert.True(r.Ok);
        Assert.Equal(Pasted, r.Text);
        Assert.Empty(_asked);
        Assert.True(_gw.Temp.IsEmpty);
        Assert.Contains("забрал test-agent", _gw.Temp.LastEvent);

        var again = await Call(new AgentRequest { Op = "temp_take" });
        Assert.False(again.Ok);
        Assert.Contains("пуст", again.Text);
    }

    [Fact]
    public async Task Run_injects_temp_by_env_and_stdin_masks_and_clears()
    {
        _gw.Temp.PutByOwner(Pasted);
        var r = await Call(new AgentRequest
        {
            Op = "run", Command = "cmd", Args = { "/c", "echo value=%SS_KEY%" },
            Env = { new EnvSecret("SS_KEY", Temp: true) },
        });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("value=***", r.Text);
        Assert.DoesNotContain(Pasted, r.Text);
        Assert.Empty(_asked);
        Assert.True(_gw.Temp.IsEmpty);

        _gw.Temp.PutByOwner(Pasted);
        var s = await Call(new AgentRequest
        {
            Op = "run", Command = "findstr", Args = { "x" }, StdinTemplate = "x {{Временный}}\n",
        });
        Assert.True(s.Ok, s.Text);
        Assert.Contains("x ***", s.Text);
        Assert.True(_gw.Temp.IsEmpty);
    }

    [Fact]
    public async Task Write_file_with_temp_ref_writes_value_and_clears()
    {
        string path = Path.Combine(_dir, "bot", ".env");
        _gw.Temp.PutByOwner(Pasted);
        var r = await Call(new AgentRequest { Op = "write_file", Path = path, Template = "BOT_TOKEN={{Временный}}\nMODE=prod\n" });
        Assert.True(r.Ok, r.Text);
        Assert.Equal($"BOT_TOKEN={Pasted}\nMODE=prod\n", File.ReadAllText(path));
        Assert.Empty(_asked);
        Assert.True(_gw.Temp.IsEmpty);
    }

    [Fact]
    public async Task Mixed_with_vault_field_asks_only_about_the_field()
    {
        _gw.Temp.PutByOwner(Pasted);
        var r = await Call(new AgentRequest
        {
            Op = "write_file", Path = Path.Combine(_dir, "mix.env"),
            Template = "A={{Временный}}\nB={{API A/Ключ API}}\n",
        });
        Assert.True(r.Ok, r.Text);
        var asked = Assert.Single(_asked);
        Assert.Equal(new[] { "Ключ API" }, asked.Secrets);
    }

    [Fact]
    public async Task Empty_temp_refuses_before_running()
    {
        string path = Path.Combine(_dir, "never.env");
        var r = await Call(new AgentRequest { Op = "write_file", Path = path, Template = "K={{Временный}}" });
        Assert.False(r.Ok);
        Assert.Contains("пуст", r.Text);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Put_from_env_file_is_for_owner_only()
    {
        string env = Path.Combine(_dir, "svc.env");
        File.WriteAllText(env, "SERVICE_TOKEN=generated-zzz-777\n");
        var r = await Call(new AgentRequest { Op = "temp_put", Source = "env_file", Path = env, Variable = "SERVICE_TOKEN" });
        Assert.True(r.Ok, r.Text);
        Assert.DoesNotContain("generated-zzz-777", r.Text);
        Assert.Empty(_asked);
        Assert.Equal(TempSecretSource.Agent, _gw.Temp.Source);

        // Положенное агентом другой агент не заберёт — только владелец.
        var take = await Call(new AgentRequest { Op = "temp_take" });
        Assert.False(take.Ok);
        Assert.DoesNotContain("generated-zzz-777", take.Text);
        Assert.Equal("generated-zzz-777", _gw.Temp.TakeByOwner());
        Assert.True(_gw.Temp.IsEmpty);
    }

    [Fact]
    public void Clear_on_lock_wipes_value()
    {
        var t = new TempSecret();
        int changes = 0;
        t.Changed += () => changes++;
        t.PutByOwner(Pasted);
        t.Clear("стёрто при блокировке");
        Assert.True(t.IsEmpty);
        Assert.Null(t.TakeByOwner());
        Assert.Equal("стёрто при блокировке", t.LastEvent);
        Assert.Equal(2, changes);
    }
}
