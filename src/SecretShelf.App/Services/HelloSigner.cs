using SecretShelf.Core;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace SecretShelf.App.Services;

/// <summary>
/// Windows Hello через KeyCredentialManager: ключ RSA живёт в защищённом хранилище Windows
/// (TPM, если он есть) и подписывает «загадку» только после пальца, лица или ПИН-кода Windows.
/// </summary>
public sealed class HelloSigner : IHelloSigner
{
    private const string KeyName = "SecretShelf.QuickUnlock";

    public async Task<bool> IsSupportedAsync()
    {
        try { return await KeyCredentialManager.IsSupportedAsync(); }
        catch (Exception) { return false; }
    }

    public async Task CreateKeyAsync()
    {
        var r = await KeyCredentialManager.RequestCreateAsync(KeyName, KeyCredentialCreationOption.ReplaceExisting);
        Check(r.Status);
    }

    public async Task<byte[]> SignAsync(byte[] challenge)
    {
        var open = await KeyCredentialManager.OpenAsync(KeyName);
        Check(open.Status);
        var r = await open.Credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(challenge));
        Check(r.Status);
        CryptographicBuffer.CopyToByteArray(r.Result, out byte[] signature);
        return signature;
    }

    public async Task DeleteKeyAsync()
    {
        try { await KeyCredentialManager.DeleteAsync(KeyName); }
        catch (Exception) { /* ключа нет — удалять нечего */ }
    }

    private static void Check(KeyCredentialStatus status)
    {
        switch (status)
        {
            case KeyCredentialStatus.Success: return;
            case KeyCredentialStatus.UserCanceled: throw new OperationCanceledException("Вход по Windows Hello отменён.");
            case KeyCredentialStatus.NotFound:
                throw new QuickUnlockException("Ключ Windows Hello не найден. Войдите мастер-паролем и включите вход по Hello заново.");
            case KeyCredentialStatus.UserPrefersPassword:
                throw new OperationCanceledException("Выбран вход паролем.");
            default:
                throw new QuickUnlockException($"Windows Hello не сработал ({status}).");
        }
    }
}
