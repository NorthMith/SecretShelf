using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace SecretShelf.App;

/// <summary>
/// Копирование секрета в буфер обмена:
///  - помечается форматами Windows, исключающими его из журнала буфера (Win+V)
///    и из облачной синхронизации буфера;
///  - через заданное время буфер очищается, если в нём всё ещё наше значение.
/// Значение не хранится — для сверки держим только его SHA-256.
/// </summary>
public sealed class SecureClipboard
{
    private readonly DispatcherTimer _timer = new();
    private byte[]? _hash;
    private DateTime _clearAt;

    public SecureClipboard()
    {
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Сколько секунд до очистки; null — нечего очищать.</summary>
    public event Action<int?>? Countdown;

    public void Copy(string value, int clearAfterSeconds)
    {
        var data = new DataObject();
        data.SetText(value, TextDataFormat.UnicodeText);
        // Документированные форматы Windows: не попадать в журнал и облако буфера.
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 0 }));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        Retry(() => Clipboard.SetDataObject(data, copy: true));

        _hash = Hash(value);
        _clearAt = DateTime.UtcNow.AddSeconds(clearAfterSeconds);
        _timer.Start();
        Countdown?.Invoke(clearAfterSeconds);
    }

    /// <summary>Очистить сейчас (при блокировке и выходе).</summary>
    public void ClearNow()
    {
        if (_hash == null) return;
        ClearIfOurs();
    }

    private void Tick()
    {
        int left = (int)Math.Ceiling((_clearAt - DateTime.UtcNow).TotalSeconds);
        if (left > 0) { Countdown?.Invoke(left); return; }
        ClearIfOurs();
    }

    private void ClearIfOurs()
    {
        _timer.Stop();
        try
        {
            if (Clipboard.ContainsText() && _hash != null &&
                CryptographicOperations.FixedTimeEquals(Hash(Clipboard.GetText()), _hash))
                Retry(Clipboard.Clear);
        }
        catch (COMException) { /* буфер занят другой программой — не трогаем чужое */ }
        _hash = null;
        Countdown?.Invoke(null);
    }

    private static byte[] Hash(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s));

    private static void Retry(Action a)
    {
        // Буфер обмена бывает занят другой программой — несколько коротких попыток.
        for (int i = 0; ; i++)
        {
            try { a(); return; }
            catch (COMException) when (i < 5) { Thread.Sleep(30); }
        }
    }
}
