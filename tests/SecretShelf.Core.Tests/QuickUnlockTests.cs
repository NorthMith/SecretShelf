using System.Security.Cryptography;
using SecretShelf.Core;

namespace SecretShelf.Core.Tests;

public sealed class QuickUnlockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "secretshelf-qu-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_dir, "quick-unlock.json");

    public QuickUnlockTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Подделка Windows Hello: детерминированная подпись HMAC фиксированным ключом.</summary>
    private sealed class FakeHello : IHelloSigner
    {
        public byte[] Key = RandomNumberGenerator.GetBytes(32);
        public bool Random;
        public Task<bool> IsSupportedAsync() => Task.FromResult(true);
        public Task CreateKeyAsync() => Task.CompletedTask;
        public Task<byte[]> SignAsync(byte[] challenge) =>
            Task.FromResult(Random ? RandomNumberGenerator.GetBytes(32) : HMACSHA256.HashData(Key, challenge));
        public Task DeleteKeyAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task No_password_mode_round_trips_and_file_has_no_plaintext()
    {
        var s = new QuickUnlockStore(StorePath);
        await s.EnableAsync(QuickUnlockMode.NoPassword, "мастер-пароль-123");
        Assert.DoesNotContain("мастер", File.ReadAllText(StorePath));

        var again = new QuickUnlockStore(StorePath);
        Assert.Equal(QuickUnlockMode.NoPassword, again.Mode);
        Assert.Equal("мастер-пароль-123", await again.UnlockAsync());
    }

    [Fact]
    public async Task Pin_right_wrong_and_lockout_after_five()
    {
        var s = new QuickUnlockStore(StorePath);
        await s.EnableAsync(QuickUnlockMode.Pin, "master", pin: "4821");
        Assert.Equal("master", await s.UnlockAsync(pin: "4821"));

        var ex = await Assert.ThrowsAsync<WrongPinException>(() => s.UnlockAsync(pin: "0000"));
        Assert.Equal(4, ex.AttemptsLeft);
        Assert.Equal("master", await s.UnlockAsync(pin: "4821")); // верный ПИН сбрасывает счётчик
        Assert.Equal(0, s.PinFailures);

        for (int i = 0; i < QuickUnlockStore.MaxPinFailures - 1; i++)
            await Assert.ThrowsAsync<WrongPinException>(() => s.UnlockAsync(pin: "1111"));
        await Assert.ThrowsAsync<QuickUnlockException>(() => s.UnlockAsync(pin: "1111"));
        Assert.Equal(QuickUnlockMode.Off, s.Mode);
        Assert.False(File.Exists(StorePath)); // файл с зашифрованным паролем стёрт
    }

    [Theory]
    [InlineData("123")]
    [InlineData("12ab")]
    [InlineData("1234567890123")]
    public async Task Pin_must_be_4_to_12_digits(string pin)
    {
        var s = new QuickUnlockStore(StorePath);
        await Assert.ThrowsAsync<QuickUnlockException>(() => s.EnableAsync(QuickUnlockMode.Pin, "m", pin: pin));
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public async Task Hello_round_trips_and_other_key_fails()
    {
        var hello = new FakeHello();
        var s = new QuickUnlockStore(StorePath);
        await s.EnableAsync(QuickUnlockMode.WindowsHello, "master", signer: hello);
        Assert.Equal("master", await new QuickUnlockStore(StorePath).UnlockAsync(signer: hello));

        var other = new FakeHello(); // ключ Hello пересоздан — расшифровать нельзя
        await Assert.ThrowsAsync<QuickUnlockException>(() => new QuickUnlockStore(StorePath).UnlockAsync(signer: other));
    }

    [Fact]
    public async Task Hello_with_nondeterministic_signature_is_refused()
    {
        var s = new QuickUnlockStore(StorePath);
        await Assert.ThrowsAsync<QuickUnlockException>(() =>
            s.EnableAsync(QuickUnlockMode.WindowsHello, "master", signer: new FakeHello { Random = true }));
        Assert.Equal(QuickUnlockMode.Off, s.Mode);
    }

    [Fact]
    public async Task Master_password_reminder()
    {
        var s = new QuickUnlockStore(StorePath);
        await s.EnableAsync(QuickUnlockMode.NoPassword, "m");
        Assert.False(s.IsMasterDue(14));
        Assert.False(s.IsMasterDue(0)); // 0 — не напоминать

        // Подделать давнюю отметку в файле и перечитать.
        string json = File.ReadAllText(StorePath);
        string stamp = System.Text.Json.JsonSerializer.Serialize(s.MasterVerifiedUtc);
        File.WriteAllText(StorePath, json.Replace(stamp, System.Text.Json.JsonSerializer.Serialize(DateTime.UtcNow.AddDays(-15))));
        var old = new QuickUnlockStore(StorePath);
        Assert.True(old.IsMasterDue(14));
        old.MarkMasterVerified();
        Assert.False(new QuickUnlockStore(StorePath).IsMasterDue(14));
    }

    [Fact]
    public async Task Disable_removes_file()
    {
        var s = new QuickUnlockStore(StorePath);
        await s.EnableAsync(QuickUnlockMode.NoPassword, "m");
        s.Disable();
        Assert.False(File.Exists(StorePath));
        await Assert.ThrowsAsync<QuickUnlockException>(() => s.UnlockAsync());
    }
}
