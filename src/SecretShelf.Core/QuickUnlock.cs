using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeePassLib.Cryptography.KeyDerivation;

namespace SecretShelf.Core;

/// <summary>Способ быстрого входа поверх мастер-пароля.</summary>
public enum QuickUnlockMode
{
    /// <summary>Только мастер-пароль, на диске ничего не хранится.</summary>
    Off = 0,
    WindowsHello = 1,
    Pin = 2,
    /// <summary>Без пароля: защищает только учётная запись Windows (DPAPI).</summary>
    NoPassword = 3,
}

/// <summary>
/// Подпись ключом Windows Hello. Реализация — в приложении (WinRT), в тестах — подделка.
/// Подпись RSA PKCS#1 v1.5 детерминирована: одна и та же «загадка» даёт одну и ту же подпись,
/// из неё и получается ключ шифрования. Детерминизм проверяется при включении.
/// </summary>
public interface IHelloSigner
{
    Task<bool> IsSupportedAsync();
    Task CreateKeyAsync();
    /// <summary>Показывает запрос Windows Hello; отказ пользователя — исключение.</summary>
    Task<byte[]> SignAsync(byte[] challenge);
    Task DeleteKeyAsync();
}

public sealed class QuickUnlockException : Exception
{
    public QuickUnlockException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class WrongPinException : Exception
{
    public WrongPinException(int attemptsLeft) : base("Неверный ПИН-код.") => AttemptsLeft = attemptsLeft;
    public int AttemptsLeft { get; }
}

/// <summary>
/// Хранит мастер-пароль, зашифрованный для быстрого входа. Файл quick-unlock.json:
///  - внешний слой всегда DPAPI (только эта учётная запись Windows на этом компьютере);
///  - внутренний для Hello и ПИН — AES-256-GCM ключом из подписи Hello или из ПИН-кода (Argon2id);
///  - «без пароля» — только DPAPI.
/// Отключение режима стирает файл. Смена мастер-пароля сбрасывает быстрый вход.
/// </summary>
public sealed class QuickUnlockStore
{
    public const int MaxPinFailures = 5;
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("SecretShelf.QuickUnlock.v1");

    private readonly string _path;
    private State _state;

    public QuickUnlockStore(string path)
    {
        _path = path;
        _state = Load(path);
    }

    public QuickUnlockMode Mode => _state.Mode;
    public int PinFailures => _state.PinFailures;
    public DateTime MasterVerifiedUtc => _state.MasterVerifiedUtc;

    /// <summary>Пора спросить мастер-пароль, чтобы он не забылся. 0 дней — никогда.</summary>
    public bool IsMasterDue(int everyDays) =>
        everyDays > 0 && DateTime.UtcNow - _state.MasterVerifiedUtc > TimeSpan.FromDays(everyDays);

    /// <summary>Вход мастер-паролем удался — сбросить счётчик «раз в N дней».</summary>
    public void MarkMasterVerified()
    {
        if (_state.Mode == QuickUnlockMode.Off) return;
        _state.MasterVerifiedUtc = DateTime.UtcNow;
        Save();
    }

    // ---------- включение ----------

    public async Task EnableAsync(QuickUnlockMode mode, string masterPassword, string? pin = null, IHelloSigner? signer = null)
    {
        byte[] secret = Encoding.UTF8.GetBytes(masterPassword);
        var st = new State { Mode = mode, MasterVerifiedUtc = DateTime.UtcNow };
        try
        {
            byte[] inner;
            switch (mode)
            {
                case QuickUnlockMode.NoPassword:
                    inner = (byte[])secret.Clone();
                    break;

                case QuickUnlockMode.Pin:
                    if (pin == null || pin.Length < 4 || pin.Length > 12 || !pin.All(char.IsAsciiDigit))
                        throw new QuickUnlockException("ПИН-код — от 4 до 12 цифр.");
                    st.Salt = RandomNumberGenerator.GetBytes(32);
                    inner = Seal(PinKey(pin, st.Salt), secret);
                    break;

                case QuickUnlockMode.WindowsHello:
                    if (signer == null || !await signer.IsSupportedAsync())
                        throw new QuickUnlockException("Windows Hello на этом компьютере не настроен.");
                    await signer.CreateKeyAsync();
                    st.Challenge = RandomNumberGenerator.GetBytes(32);
                    byte[] sig1 = await signer.SignAsync(st.Challenge);
                    byte[] sig2 = await signer.SignAsync(st.Challenge);
                    if (!sig1.AsSpan().SequenceEqual(sig2))
                    {
                        await signer.DeleteKeyAsync();
                        throw new QuickUnlockException("Windows Hello на этом компьютере даёт разные подписи — такой способ входа здесь не сработает.");
                    }
                    inner = Seal(SHA256.HashData(sig1), secret);
                    break;

                default:
                    Disable();
                    return;
            }
            st.Blob = ProtectedData.Protect(inner, DpapiEntropy, DataProtectionScope.CurrentUser);
            _state = st;
            Save();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Выключить быстрый вход: файл с зашифрованным паролем удаляется.</summary>
    public void Disable()
    {
        _state = new State();
        if (File.Exists(_path)) File.Delete(_path);
    }

    // ---------- вход ----------

    /// <summary>Достаёт мастер-пароль. ПИН — только для режима ПИН, signer — только для Hello.</summary>
    public async Task<string> UnlockAsync(string? pin = null, IHelloSigner? signer = null)
    {
        if (_state.Mode == QuickUnlockMode.Off || _state.Blob == null)
            throw new QuickUnlockException("Быстрый вход не включён.");

        byte[] inner;
        try
        {
            inner = ProtectedData.Unprotect(_state.Blob, DpapiEntropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            // Другая учётная запись или переустановленная Windows — расшифровать нечем.
            Disable();
            throw new QuickUnlockException("Быстрый вход больше не действует на этой учётной записи Windows. Войдите мастер-паролем.", ex);
        }

        byte[] secret;
        switch (_state.Mode)
        {
            case QuickUnlockMode.NoPassword:
                secret = inner;
                break;

            case QuickUnlockMode.Pin:
                try
                {
                    secret = Open(PinKey(pin ?? "", _state.Salt!), inner);
                }
                catch (CryptographicException)
                {
                    _state.PinFailures++;
                    if (_state.PinFailures >= MaxPinFailures)
                    {
                        Disable();
                        throw new QuickUnlockException($"{MaxPinFailures} неверных ПИН-кодов подряд — быстрый вход выключен. Войдите мастер-паролем.");
                    }
                    Save();
                    throw new WrongPinException(MaxPinFailures - _state.PinFailures);
                }
                if (_state.PinFailures != 0) { _state.PinFailures = 0; Save(); }
                break;

            case QuickUnlockMode.WindowsHello:
                if (signer == null) throw new QuickUnlockException("Windows Hello недоступен.");
                byte[] sig = await signer.SignAsync(_state.Challenge!);
                try
                {
                    secret = Open(SHA256.HashData(sig), inner);
                }
                catch (CryptographicException ex)
                {
                    throw new QuickUnlockException("Ключ Windows Hello изменился. Войдите мастер-паролем и включите вход по Hello заново.", ex);
                }
                break;

            default:
                throw new QuickUnlockException("Неизвестный способ входа.");
        }

        try { return Encoding.UTF8.GetString(secret); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    // ---------- шифрование ----------

    /// <summary>Ключ из ПИН-кода: Argon2id, 32 МиБ, 3 прохода — подбор каждого ПИН-кода дорог.</summary>
    private static byte[] PinKey(string pin, byte[] salt)
    {
        var kdf = new Argon2Kdf(Argon2Type.ID);
        var p = kdf.GetDefaultParameters();
        p.SetByteArray(Argon2Kdf.ParamSalt, salt);
        p.SetUInt64(Argon2Kdf.ParamMemory, 32UL * 1024 * 1024);
        p.SetUInt64(Argon2Kdf.ParamIterations, 3);
        p.SetUInt32(Argon2Kdf.ParamParallelism, 2);
        byte[] key = kdf.Transform(SHA256.HashData(Encoding.UTF8.GetBytes(pin)), p);
        Vault.ReleaseKdfMemory();
        return key;
    }

    /// <summary>AES-256-GCM: [12 байт nonce][16 байт метка][шифртекст].</summary>
    private static byte[] Seal(byte[] key, byte[] plain)
    {
        byte[] result = new byte[12 + 16 + plain.Length];
        RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(result.AsSpan(0, 12), plain, result.AsSpan(28), result.AsSpan(12, 16));
        CryptographicOperations.ZeroMemory(key);
        return result;
    }

    private static byte[] Open(byte[] key, byte[] sealedData)
    {
        byte[] plain = new byte[sealedData.Length - 28];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(sealedData.AsSpan(0, 12), sealedData.AsSpan(28), sealedData.AsSpan(12, 16), plain);
            return plain;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // ---------- файл ----------

    private sealed class State
    {
        public QuickUnlockMode Mode { get; set; }
        public byte[]? Blob { get; set; }
        public byte[]? Salt { get; set; }
        public byte[]? Challenge { get; set; }
        public int PinFailures { get; set; }
        public DateTime MasterVerifiedUtc { get; set; }
    }

    private static State Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new();
        }
        catch (JsonException) { /* испорченный файл — как будто быстрого входа нет */ }
        return new();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state));
        File.Move(tmp, _path, overwrite: true);
    }
}
