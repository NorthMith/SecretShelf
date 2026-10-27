using System.Diagnostics;
using System.Runtime;
using System.Text.Json;
using KeePassLib;
using KeePassLib.Cryptography.KeyDerivation;
using KeePassLib.Keys;
using KeePassLib.Security;
using KeePassLib.Serialization;

namespace SecretShelf.Core;

/// <summary>
/// Хранилище SecretShelf поверх файла KDBX 4 (формат KeePass).
/// Файл открывается в KeePass/KeePassXC — это запасной выход, если с приложением что-то случится.
///
/// Раскладка в KDBX:
///  - каждая запись — PwEntry в корневой группе, порядок записей = порядок в группе;
///  - значения полей — строки записи (ProtectedString, секретные защищены в памяти);
///  - порядок полей, подписи и уровни доступа для ИИ — JSON в CustomData записи.
///
/// Все методы потокобезопасны: UI правит из своего потока, сохранение идёт в фоне.
/// </summary>
public sealed class Vault : IDisposable
{
    internal const string FieldsMetaKey = "SecretShelf.Fields";
    private const int BackupsToKeep = 30;
    private static readonly TimeSpan BackupInterval = TimeSpan.FromMinutes(10);

    private readonly object _sync = new();
    private readonly PwDatabase _db;
    private readonly string _path;
    private DateTime _lastBackupUtc = DateTime.MinValue;
    private bool _dirty;

    private Vault(PwDatabase db, string path)
    {
        _db = db;
        _path = path;
    }

    public string FilePath => _path;

    public bool IsDirty
    {
        get { lock (_sync) return _dirty; }
    }

    /// <summary>Raised after any change of entries (not after save).</summary>
    public event EventHandler? Changed;

    // ---------- создание и открытие ----------

    /// <summary>
    /// Создаёт новое хранилище. Ключ из пароля получается через Argon2id;
    /// число итераций подбирается так, чтобы отпирание занимало около <paramref name="kdfTargetMs"/>.
    /// </summary>
    public static Vault Create(string path, string password, int kdfTargetMs = 400)
    {
        if (File.Exists(path)) throw new IOException($"Файл уже существует: {path}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var db = new PwDatabase();
        db.New(IOConnectionInfo.FromPath(path), MakeKey(password));
        db.Name = "SecretShelf";
        db.RootGroup.Name = "SecretShelf";
        db.RecycleBinEnabled = false; // удаление подтверждает пользователь, откат — через резервные копии
        db.KdfParameters = CalibrateArgon2(kdfTargetMs);
        // Защищать в памяти все стандартные поля, а не только пароль.
        db.MemoryProtection.ProtectUserName = true;

        var vault = new Vault(db, path);
        vault.SaveCore(makeBackup: false);
        ReleaseKdfMemory();
        return vault;
    }

    public static Vault Open(string path, string password)
    {
        var db = new PwDatabase();
        try
        {
            db.Open(IOConnectionInfo.FromPath(path), MakeKey(password), null);
        }
        catch (InvalidCompositeKeyException ex)
        {
            throw new WrongPasswordException(ex);
        }
        finally
        {
            ReleaseKdfMemory();
        }
        return new Vault(db, path);
    }

    /// <summary>
    /// Argon2 берёт 64 МиБ на каждый подсчёт (открытие, сохранение). Ядро массив обнуляет,
    /// но сборщик мусора держит эту память за процессом: замер 27.10.2026 —
    /// 143 МБ частной памяти после открытия, 12 МБ после сборки с уплотнением.
    /// Подсчёт редкий (раз на открытие или сохранение), поэтому собираем принудительно.
    /// </summary>
    private static void ReleaseKdfMemory()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private static CompositeKey MakeKey(string password)
    {
        var key = new CompositeKey();
        key.AddUserKey(new KcpPassword(password));
        return key;
    }

    /// <summary>Argon2id, 64 МиБ, 2 потока; итерации — под заданное время на этой машине.</summary>
    internal static KdfParameters CalibrateArgon2(int targetMs)
    {
        var kdf = new Argon2Kdf(Argon2Type.ID);
        KdfParameters p = kdf.GetDefaultParameters();
        p.SetUInt64(Argon2Kdf.ParamMemory, 64UL * 1024 * 1024);
        p.SetUInt32(Argon2Kdf.ParamParallelism, 2);
        p.SetUInt64(Argon2Kdf.ParamIterations, 1);
        kdf.Randomize(p);

        var sw = Stopwatch.StartNew();
        kdf.Transform(new byte[32], p);
        double perIteration = Math.Max(1, sw.Elapsed.TotalMilliseconds);

        ulong iterations = (ulong)Math.Clamp(Math.Round(targetMs / perIteration), 2, 50);
        p.SetUInt64(Argon2Kdf.ParamIterations, iterations);
        return p;
    }

    /// <summary>Текущие параметры KDF — для экрана настроек и отладки.</summary>
    public string DescribeKdf()
    {
        lock (_sync)
        {
            var p = _db.KdfParameters;
            if (!p.KdfUuid.Equals(new Argon2Kdf().Uuid) && !p.KdfUuid.Equals(new Argon2Kdf(Argon2Type.ID).Uuid))
                return "не Argon2 (файл создан другой программой)";
            return $"Argon2, {p.GetUInt64(Argon2Kdf.ParamMemory, 0) / 1024 / 1024} МиБ, " +
                   $"{p.GetUInt32(Argon2Kdf.ParamParallelism, 0)} потока, " +
                   $"{p.GetUInt64(Argon2Kdf.ParamIterations, 0)} итераций";
        }
    }

    // ---------- чтение ----------

    public IReadOnlyList<VaultEntry> GetEntries()
    {
        lock (_sync)
            return _db.RootGroup.Entries.Select(ToModel).ToList();
    }

    /// <summary>
    /// Значение поля открытым текстом. Строки .NET нельзя затереть, поэтому
    /// значение берётся только по запросу (показать/скопировать) и не хранится в модели.
    /// </summary>
    public string GetValue(string entryId, string fieldKey)
    {
        lock (_sync)
            return FindEntry(entryId).Strings.ReadSafe(fieldKey);
    }

    // ---------- изменение ----------

    public VaultEntry AddEntry(string title, IEnumerable<VaultFieldDraft> fields)
    {
        lock (_sync)
        {
            var pe = new PwEntry(true, true);
            _db.RootGroup.AddEntry(pe, true);
            ApplyDraft(pe, title, fields.ToList());
            MarkDirty();
            return ToModel(pe);
        }
    }

    public VaultEntry UpdateEntry(string entryId, string title, IEnumerable<VaultFieldDraft> fields)
    {
        lock (_sync)
        {
            var pe = FindEntry(entryId);
            pe.CreateBackup(_db); // история правок записи, как в KeePass
            ApplyDraft(pe, title, fields.ToList());
            pe.Touch(true, false);
            MarkDirty();
            return ToModel(pe);
        }
    }

    public void DeleteEntry(string entryId)
    {
        lock (_sync)
        {
            var pe = FindEntry(entryId);
            _db.RootGroup.Entries.Remove(pe);
            _db.DeletedObjects.Add(new PwDeletedObject(pe.Uuid, DateTime.UtcNow));
            MarkDirty();
        }
    }

    /// <summary>Перемещает запись на новую позицию (перетаскивание в списке).</summary>
    public void MoveEntry(string entryId, int newIndex)
    {
        lock (_sync)
        {
            var list = _db.RootGroup.Entries;
            var pe = FindEntry(entryId);
            int oldIndex = list.IndexOf(pe);
            newIndex = Math.Clamp(newIndex, 0, (int)list.UCount - 1);
            if (oldIndex == newIndex) return;
            list.RemoveAt((uint)oldIndex);
            list.Insert((uint)newIndex, pe);
            _db.RootGroup.Touch(true, false);
            MarkDirty();
        }
    }

    // ---------- сохранение ----------

    /// <summary>
    /// Сохраняет файл, если были изменения. Медленно (KeePassLib при каждом сохранении
    /// заново считает Argon2 с новой солью), поэтому вызывать из фонового потока.
    /// </summary>
    public void SaveIfDirty()
    {
        lock (_sync)
        {
            if (!_dirty) return;
            SaveCore(makeBackup: true);
        }
    }

    private void SaveCore(bool makeBackup)
    {
        if (makeBackup) BackupIfDue();
        _db.Save(null);
        _dirty = false;
        ReleaseKdfMemory();
    }

    /// <summary>
    /// Смена мастер-пароля. Старый проверяется, перед перезаписью делается копия файла
    /// (вне очереди, независимо от интервала копий), сохранение — сразу.
    /// </summary>
    public void ChangePassword(string oldPassword, string newPassword)
    {
        lock (_sync)
        {
            if (!_db.MasterKey.EqualsValue(MakeKey(oldPassword)))
                throw new WrongPasswordException(new InvalidCompositeKeyException());
            _lastBackupUtc = DateTime.MinValue;
            BackupIfDue();
            _db.MasterKey = MakeKey(newPassword);
            _db.MasterKeyChanged = DateTime.UtcNow;
            SaveCore(makeBackup: false);
        }
    }

    /// <summary>
    /// Копия файла перед перезаписью — не чаще раза в 10 минут, хранится последние 30.
    /// Каталог: рядом с файлом, подпапка backups.
    /// </summary>
    private void BackupIfDue()
    {
        if (!File.Exists(_path)) return;
        if (DateTime.UtcNow - _lastBackupUtc < BackupInterval) return;

        string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_path))!, "backups");
        Directory.CreateDirectory(dir);
        string name = Path.GetFileNameWithoutExtension(_path);
        File.Copy(_path, Path.Combine(dir, $"{name}.bak-{DateTime.Now:yyyyMMdd-HHmmss}.kdbx"), overwrite: true);
        _lastBackupUtc = DateTime.UtcNow;

        foreach (var old in new DirectoryInfo(dir).GetFiles($"{name}.bak-*.kdbx")
                     .OrderByDescending(f => f.Name).Skip(BackupsToKeep))
            old.Delete();
    }

    private void MarkDirty()
    {
        _dirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_dirty) SaveCore(makeBackup: true);
            _db.Close();
        }
    }

    // ---------- раскладка полей ----------

    private PwEntry FindEntry(string entryId)
    {
        foreach (var pe in _db.RootGroup.Entries)
            if (pe.Uuid.ToHexString() == entryId) return pe;
        throw new KeyNotFoundException($"Запись {entryId} не найдена.");
    }

    private sealed record FieldMeta(string k, string l, bool s, FieldAccess a, List<string>? t);

    private static void ApplyDraft(PwEntry pe, string title, List<VaultFieldDraft> drafts)
    {
        // Убрать все прежние значения, кроме служебных стандартных полей.
        foreach (var key in pe.Strings.GetKeys())
            if (key != PwDefs.TitleField) pe.Strings.Remove(key);

        pe.Strings.Set(PwDefs.TitleField, new ProtectedString(false, title.Trim()));

        var used = new HashSet<string>(StringComparer.Ordinal) { PwDefs.TitleField };
        var metas = new List<FieldMeta>();
        bool userNameFree = true, passwordFree = true;

        // Поля, ключи которых уже существуют, сохраняют их; новым ключ выдаётся ниже.
        foreach (var d in drafts.Where(d => d.Key != null)) used.Add(d.Key!);
        if (used.Contains(PwDefs.UserNameField)) userNameFree = false;
        if (used.Contains(PwDefs.PasswordField)) passwordFree = false;

        foreach (var d in drafts)
        {
            string label = string.IsNullOrWhiteSpace(d.Label) ? "Поле" : d.Label.Trim();
            string key = d.Key ?? "";
            if (key.Length == 0)
            {
                // Первое открытое поле — UserName, первое секретное — Password:
                // тогда в KeePass/KeePassXC они видны в стандартных колонках.
                if (!d.IsSecret && userNameFree) { key = PwDefs.UserNameField; userNameFree = false; }
                else if (d.IsSecret && passwordFree) { key = PwDefs.PasswordField; passwordFree = false; }
                else key = UniqueKey(label, used);
                used.Add(key);
            }
            pe.Strings.Set(key, new ProtectedString(d.IsSecret, d.Value ?? ""));
            metas.Add(new FieldMeta(key, label, d.IsSecret, d.Access, d.AllowedTargets.Count > 0 ? d.AllowedTargets : null));
        }

        pe.CustomData.Set(FieldsMetaKey, JsonSerializer.Serialize(metas));
    }

    private static string UniqueKey(string label, HashSet<string> used)
    {
        string key = label;
        for (int i = 2; used.Contains(key) || PwDefs.IsStandardField(key); i++)
            key = $"{label} ({i})";
        return key;
    }

    private static VaultEntry ToModel(PwEntry pe)
    {
        string id = pe.Uuid.ToHexString();
        string title = pe.Strings.ReadSafe(PwDefs.TitleField);
        string? json = pe.CustomData.Get(FieldsMetaKey);

        List<VaultField> fields = new();
        if (!string.IsNullOrEmpty(json))
        {
            var metas = JsonSerializer.Deserialize<List<FieldMeta>>(json) ?? new();
            foreach (var m in metas)
                if (pe.Strings.Exists(m.k))
                    fields.Add(new VaultField(m.k, m.l, m.s, m.a, (IReadOnlyList<string>?)m.t ?? Array.Empty<string>()));
        }
        else
        {
            // Запись создана в KeePass/KeePassXC: показать её стандартные и свои поля.
            fields.AddRange(FromForeignEntry(pe));
        }
        return new VaultEntry(id, title, fields);
    }

    private static IEnumerable<VaultField> FromForeignEntry(PwEntry pe)
    {
        var none = Array.Empty<string>();
        (string key, string label)[] std =
        {
            (PwDefs.UserNameField, "Логин"), (PwDefs.PasswordField, "Пароль"),
            (PwDefs.UrlField, "Адрес"), (PwDefs.NotesField, "Заметки"),
        };
        foreach (var (key, label) in std)
        {
            var ps = pe.Strings.Get(key);
            if (ps == null || (ps.IsEmpty && key != PwDefs.PasswordField)) continue;
            yield return new VaultField(key, label, ps.IsProtected, FieldAccess.Hidden, none);
        }
        foreach (var kv in pe.Strings)
            if (!PwDefs.IsStandardField(kv.Key))
                yield return new VaultField(kv.Key, kv.Key, kv.Value.IsProtected, FieldAccess.Hidden, none);
    }
}
