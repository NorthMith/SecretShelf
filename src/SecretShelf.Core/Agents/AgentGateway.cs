using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace SecretShelf.Core.Agents;

/// <summary>Что показать владельцу в окне подтверждения. Значений секретов здесь нет.</summary>
/// <param name="Destructive">Удаление — окно красное, кнопка «Удалить».</param>
public sealed record ConfirmRequest(string Client, string Title, string Details, IReadOnlyList<string> Secrets, bool Destructive = false);

/// <summary>Окно подтверждения. Реализация — в шторке; в тестах — заглушка.</summary>
public interface IConfirmer
{
    /// <returns>true — владелец разрешил.</returns>
    Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct);
}

internal sealed class AgentError : Exception
{
    public AgentError(string message) : base(message) { }
}

/// <summary>Владелец нажал «Отказать» или не ответил вовремя (в журнал уже записано).</summary>
internal sealed class AgentDenied : Exception
{
}

/// <summary>
/// Шлюз между агентами и хранилищем. Правила:
///  - агент видит только поля с уровнем «Видно название» и выше;
///  - значение отдаётся только полям «Открыто»;
///  - подстановка — полям «Подстановка» и «Открыто», и только разрешённым программам поля;
///  - каждое действие с секретом — с подтверждением владельца, если у поля не стоит
///    «без подтверждения»; новые записи, поиск по папкам и удаление — с подтверждением всегда;
///  - вывод программ возвращается с замаскированными значениями.
/// </summary>
public sealed class AgentGateway
{
    public const int MaxStoreBytes = 64 * 1024;

    private readonly Func<Vault?> _vault;
    private readonly IConfirmer _confirmer;
    private readonly AgentLog _log;
    private readonly Func<bool> _enabled;

    public AgentGateway(Func<Vault?> vault, IConfirmer confirmer, AgentLog log, Func<bool> enabled)
    {
        _vault = vault;
        _confirmer = confirmer;
        _log = log;
        _enabled = enabled;
    }

    /// <summary>Агент обратился к запертому хранилищу — шторка может напомнить владельцу.</summary>
    public event Action<string>? LockedRequest;

    /// <summary>Агент изменил хранилище — шторке пора обновить список.</summary>
    public event Action? VaultChanged;

    public async Task<AgentResponse> HandleAsync(AgentRequest r, CancellationToken ct = default)
    {
        string client = string.IsNullOrWhiteSpace(r.Client) ? "неизвестный агент" : r.Client.Trim();
        if (!_enabled()) return AgentResponse.Fail("Доступ агентов выключен в настройках SecretShelf.");
        var vault = _vault();
        if (vault == null)
        {
            LockedRequest?.Invoke(client);
            _log.Write(client, r.Op, Describe(r), "хранилище заперто");
            return AgentResponse.Fail("Хранилище SecretShelf заблокировано. Попросите владельца открыть шторку и повторите.");
        }

        try
        {
            return r.Op switch
            {
                "list" => List(vault, client),
                "get" => Get(vault, client, r),
                "run" => await RunAsync(vault, client, r, ct),
                "write_file" => await WriteFileAsync(vault, client, r, ct),
                "store" => await StoreAsync(vault, client, r, ct),
                "delete" => await DeleteAsync(vault, client, r, ct),
                "scan" => await ScanAsync(client, r, ct),
                _ => AgentResponse.Fail($"Неизвестная операция «{r.Op}»."),
            };
        }
        catch (AgentDenied)
        {
            return AgentResponse.Fail("Владелец отказал в доступе. Не повторяйте запрос без его просьбы.");
        }
        catch (AgentError ex)
        {
            _log.Write(client, r.Op, Describe(r), "отказ: " + ex.Message);
            return AgentResponse.Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            _log.Write(client, r.Op, Describe(r), "отменено");
            return AgentResponse.Fail("Запрос отменён.");
        }
        catch (Exception ex)
        {
            _log.Write(client, r.Op, Describe(r), "ошибка: " + ex.Message);
            return AgentResponse.Fail("Ошибка: " + ex.Message);
        }
    }

    // ================= операции =================

    private AgentResponse List(Vault v, string client)
    {
        var sb = new StringBuilder();
        foreach (var e in v.GetEntries())
            foreach (var f in e.Fields.Where(f => f.Access >= FieldAccess.NameOnly))
            {
                sb.Append($"- {e.Title} / {f.Label} — {AccessName(f.Access)}");
                if (f.NoConfirm) sb.Append(", без подтверждения");
                if (f.AllowedTargets.Count > 0) sb.Append(", только: " + string.Join(", ", f.AllowedTargets));
                sb.AppendLine();
            }
        _log.Write(client, "list", "", "выдан список");
        return AgentResponse.Success(sb.Length > 0
            ? "Поля, доступные агентам (значений здесь нет):\n" + sb
            : "Нет полей, открытых для агентов. Владелец открывает доступ в шторке: «⋯» у поля → «Доступ для ИИ».");
    }

    private AgentResponse Get(Vault v, string client, AgentRequest r)
    {
        var (e, f) = Resolve(v, r.Entry, r.Field, FieldAccess.Reveal);
        _log.Write(client, "get", $"{e.Title} / {f.Label}", "выдано (поле открыто)");
        return AgentResponse.Success(v.GetValue(e.Id, f.Key));
    }

    private async Task<AgentResponse> RunAsync(Vault v, string client, AgentRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Command)) throw new AgentError("Не указана программа (command).");
        string exe = ProcessRunner.Resolve(r.Command)
                     ?? throw new AgentError($"Программа «{r.Command}» не найдена.");
        string program = Path.GetFileNameWithoutExtension(exe);

        var used = new List<(EnvSecret env, VaultEntry e, VaultField f)>();
        foreach (var env in r.Env)
        {
            if (string.IsNullOrWhiteSpace(env.Name)) throw new AgentError("У переменной окружения нет имени.");
            var (e, f) = Resolve(v, env.Entry, env.Field, FieldAccess.Inject);
            CheckTarget(e, f, program);
            used.Add((env, e, f));
        }
        var stdinRefs = r.StdinTemplate == null ? new() : ResolveRefs(v, SecretText.FindRefs(r.StdinTemplate), program);
        var all = used.Select(u => (u.e, u.f)).Concat(stdinRefs).ToList();
        if (all.Count == 0) throw new AgentError("Нет ни одного секрета для подстановки — обычные команды запускайте без SecretShelf.");

        var details = new StringBuilder();
        details.AppendLine(ProcessRunner.Display(exe, r.Args));
        if (!string.IsNullOrEmpty(r.Cwd)) details.AppendLine("Папка: " + r.Cwd);
        foreach (var u in used) details.AppendLine($"${u.env.Name} ← {u.e.Title} / {u.f.Label}");
        if (r.StdinTemplate != null) details.AppendLine("На ввод программы: " + r.StdinTemplate);
        string target = string.Join(", ", all.Select(x => $"{x.e.Title} / {x.f.Label}").Distinct());

        await Approve(client, "run", target, all.Select(x => x.f), $"{client} запускает программу с ключом", details.ToString(), ct);

        var envValues = used.ToDictionary(u => u.env.Name, u => v.GetValue(u.e.Id, u.f.Key));
        string? stdin = r.StdinTemplate == null ? null : SecretText.Render(r.StdinTemplate, rf => ValueOf(v, rf));
        var secrets = all.Select(x => v.GetValue(x.e.Id, x.f.Key)).ToList();

        var res = await ProcessRunner.RunAsync(exe, r.Args, envValues, stdin, r.Cwd, Math.Clamp(r.TimeoutSeconds, 1, 600), ct);
        string text = $"Код выхода: {res.ExitCode}{(res.TimedOut ? " (остановлено по таймауту)" : "")}\n" +
                      $"--- stdout ---\n{SecretText.Truncate(SecretText.MaskAll(res.Stdout, secrets))}\n" +
                      $"--- stderr ---\n{SecretText.Truncate(SecretText.MaskAll(res.Stderr, secrets))}";
        return AgentResponse.Success(text);
    }

    private async Task<AgentResponse> WriteFileAsync(Vault v, string client, AgentRequest r, CancellationToken ct)
    {
        string path = CheckPath(r.Path, mustExist: false);
        if (string.IsNullOrEmpty(r.Template)) throw new AgentError("Пустой шаблон (template).");
        var refs = ResolveRefs(v, SecretText.FindRefs(r.Template), "file");
        if (refs.Count == 0) throw new AgentError("В шаблоне нет ссылок {{Запись/Поле}} — обычные файлы пишите без SecretShelf.");

        bool exists = File.Exists(path);
        string details = $"Файл: {path}{(exists ? "\nФайл уже есть — будет перезаписан, рядом останется копия .bak" : "")}\n" +
                         $"Шаблон:\n{SecretText.Truncate(r.Template, 2000)}";
        string target = string.Join(", ", refs.Select(x => $"{x.e.Title} / {x.f.Label}").Distinct());
        await Approve(client, "write_file", target, refs.Select(x => x.f), $"{client} записывает ключ в файл", details, ct);

        string content = SecretText.Render(r.Template, rf => ValueOf(v, rf));
        string? backup = null;
        if (exists)
        {
            backup = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmm}";
            File.Copy(path, backup, overwrite: true);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        RestrictToCurrentUser(path);
        return AgentResponse.Success($"Записан {path} ({Encoding.UTF8.GetByteCount(content)} байт), доступ только у текущего пользователя Windows." +
                                     (backup != null ? $" Прежний файл сохранён как {backup}." : ""));
    }

    private async Task<AgentResponse> StoreAsync(Vault v, string client, AgentRequest r, CancellationToken ct)
    {
        string entry = (r.Entry ?? "").Trim(), field = (r.Field ?? "").Trim();
        if (entry.Length == 0 || field.Length == 0) throw new AgentError("Укажите запись (entry) и поле (field).");

        string sourceText;
        Func<Task<string>> read;
        switch (r.Source)
        {
            case "file":
                string file = CheckPath(r.Path, mustExist: true);
                if (new FileInfo(file).Length > MaxStoreBytes) throw new AgentError("Файл больше 64 КБ — на ключ не похоже.");
                sourceText = "содержимое файла " + file;
                read = async () => NormalizeStored(await File.ReadAllTextAsync(file, ct));
                break;
            case "env_file":
                string envFile = CheckPath(r.Path, mustExist: true);
                if (string.IsNullOrWhiteSpace(r.Variable)) throw new AgentError("Укажите имя переменной (variable).");
                sourceText = $"переменная {r.Variable} из файла {envFile}";
                read = async () =>
                {
                    var vars = SecretText.ParseEnv(await File.ReadAllTextAsync(envFile, ct));
                    return vars.TryGetValue(r.Variable!, out var val) ? val
                        : throw new AgentError($"В {envFile} нет переменной {r.Variable}.");
                };
                break;
            case "command":
                if (string.IsNullOrWhiteSpace(r.Command)) throw new AgentError("Не указана программа (command).");
                string exe = ProcessRunner.Resolve(r.Command) ?? throw new AgentError($"Программа «{r.Command}» не найдена.");
                sourceText = "вывод команды " + ProcessRunner.Display(exe, r.Args);
                read = async () =>
                {
                    var res = await ProcessRunner.RunAsync(exe, r.Args, new(), null, r.Cwd, Math.Clamp(r.TimeoutSeconds, 1, 600), ct);
                    if (res.ExitCode != 0 || res.TimedOut)
                        throw new AgentError($"Команда завершилась с кодом {res.ExitCode}; stderr: {SecretText.Truncate(res.Stderr, 500)}");
                    return res.Stdout.Trim();
                };
                break;
            default:
                throw new AgentError("Источник (source): file, env_file или command.");
        }

        var existing = TryResolve(v, entry, field);
        if (existing is { } ex && ex.f.Access < FieldAccess.NameOnly)
            throw new AgentError($"Поле «{entry} / {field}» закрыто для агентов — перезаписать его может только владелец.");

        string what = existing == null ? "новое поле" : "заменить значение (прежнее останется в истории записи)";
        await Approve(client, "store", $"{entry} / {field}", existing == null ? Array.Empty<VaultField>() : new[] { existing.Value.f },
            $"{client} кладёт ключ в шторку", $"Куда: {entry} / {field} — {what}\nОткуда: {sourceText}", ct,
            alwaysConfirm: existing == null);

        string value = await read();
        if (value.Length == 0) throw new AgentError("Получилось пустое значение — сохранять нечего.");

        if (existing is { } x)
        {
            var drafts = DraftsOf(v, x.e);
            drafts.First(d => d.Key == x.f.Key).Value = value;
            v.UpdateEntry(x.e.Id, x.e.Title, drafts);
        }
        else
        {
            var target = FindEntryByTitle(v, entry);
            var newField = new VaultFieldDraft { Label = field, Value = value, IsSecret = r.Secret, Access = FieldAccess.Inject };
            if (target == null) v.AddEntry(entry, new[] { newField });
            else
            {
                var drafts = DraftsOf(v, target);
                drafts.Add(newField);
                v.UpdateEntry(target.Id, target.Title, drafts);
            }
        }
        VaultChanged?.Invoke();
        return AgentResponse.Success($"Сохранено в «{entry} / {field}» ({value.Length} символов). Значение агенту не показывалось. " +
                                     "Исходный файл не тронут — удалять его или нет, решает владелец.");
    }

    private async Task<AgentResponse> DeleteAsync(Vault v, string client, AgentRequest r, CancellationToken ct)
    {
        string entryName = (r.Entry ?? "").Trim();
        var e = FindEntryByTitle(v, entryName) ?? throw new AgentError($"Записи «{entryName}» нет.");

        if (string.IsNullOrWhiteSpace(r.Field))
        {
            if (e.Fields.Any(f => f.Access < FieldAccess.NameOnly))
                throw new AgentError($"В записи «{e.Title}» есть закрытые для агентов поля — удалить её может только владелец.");
            await Approve(client, "delete", e.Title, e.Fields, $"{client} удаляет запись", $"Запись «{e.Title}» целиком: " +
                string.Join(", ", e.Fields.Select(f => f.Label)), ct, alwaysConfirm: true, destructive: true);
            v.DeleteEntry(e.Id);
        }
        else
        {
            var (_, f) = Resolve(v, r.Entry, r.Field, FieldAccess.NameOnly);
            await Approve(client, "delete", $"{e.Title} / {f.Label}", new[] { f }, $"{client} удаляет поле",
                $"Поле «{f.Label}» из записи «{e.Title}» (прежнее значение останется в истории записи)", ct,
                alwaysConfirm: true, destructive: true);
            var drafts = DraftsOf(v, e).Where(d => d.Key != f.Key).ToList();
            if (drafts.Count == 0) v.DeleteEntry(e.Id); // последнее поле — запись без полей не нужна
            else v.UpdateEntry(e.Id, e.Title, drafts);
        }
        VaultChanged?.Invoke();
        return AgentResponse.Success("Удалено. Копия хранилища до удаления — в папке backups у владельца.");
    }

    private async Task<AgentResponse> ScanAsync(string client, AgentRequest r, CancellationToken ct)
    {
        string dir = (r.Directory ?? "").Trim();
        if (!Path.IsPathFullyQualified(dir) || !System.IO.Directory.Exists(dir)) throw new AgentError($"Папки «{dir}» нет (нужен полный путь).");
        if (SecretScanner.IsForbidden(dir + Path.DirectorySeparatorChar)) throw new AgentError("Эту папку просматривать нельзя.");
        await Approve(client, "scan", dir, Array.Empty<VaultField>(), $"{client} ищет ключи в папке",
            $"Папка: {dir} и вложенные (до 6 уровней).\nАгент увидит пути файлов и имена переменных, значения — нет.", ct, alwaysConfirm: true);
        var found = await Task.Run(() => SecretScanner.Scan(dir), ct);
        return AgentResponse.Success(SecretScanner.Report(dir, found));
    }

    // ================= подтверждение =================

    private async Task Approve(string client, string op, string target, IEnumerable<VaultField> fields, string title, string details,
                               CancellationToken ct, bool alwaysConfirm = false, bool destructive = false)
    {
        var list = fields.ToList();
        bool need = alwaysConfirm || list.Count == 0 || list.Any(f => !f.NoConfirm);
        if (!need)
        {
            _log.Write(client, op, target, "без подтверждения (галочка у поля)");
            return;
        }
        bool ok = await _confirmer.ConfirmAsync(new ConfirmRequest(client, title, details.TrimEnd(), list.Select(f => f.Label).ToList(), destructive), ct);
        _log.Write(client, op, target, ok ? "разрешено владельцем" : "отказано владельцем");
        if (!ok) throw new AgentDenied();
    }

    // ================= поиск полей =================

    private static VaultEntry? FindEntryByTitle(Vault v, string title)
    {
        var matches = v.GetEntries().Where(e => e.Title.Trim().Equals(title.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (matches.Count > 1) throw new AgentError($"Записей «{title}» несколько — переименуйте их в шторке, чтобы различать.");
        return matches.FirstOrDefault();
    }

    private static (VaultEntry e, VaultField f)? TryResolve(Vault v, string entry, string field)
    {
        var e = FindEntryByTitle(v, entry);
        var f = e?.Fields.FirstOrDefault(x => x.Label.Trim().Equals(field.Trim(), StringComparison.CurrentCultureIgnoreCase));
        return e != null && f != null ? (e, f) : null;
    }

    private static (VaultEntry e, VaultField f) Resolve(Vault v, string? entry, string? field, FieldAccess need)
    {
        if (string.IsNullOrWhiteSpace(entry) || string.IsNullOrWhiteSpace(field))
            throw new AgentError("Укажите запись и поле.");
        var found = TryResolve(v, entry, field);
        // Закрытое поле для агента не существует — тот же ответ, что и для отсутствующего.
        if (found is not { } x || x.f.Access < FieldAccess.NameOnly)
            throw new AgentError($"Поля «{entry} / {field}» нет среди доступных агентам. Список — инструмент list_secrets.");
        if (x.f.Access < need)
            throw new AgentError($"У поля «{x.e.Title} / {x.f.Label}» уровень «{AccessName(x.f.Access)}», нужно «{AccessName(need)}». " +
                                 "Изменить может только владелец в шторке.");
        return x;
    }

    private static List<(VaultEntry e, VaultField f)> ResolveRefs(Vault v, List<SecretRef> refs, string program)
    {
        var list = new List<(VaultEntry, VaultField)>();
        foreach (var rf in refs)
        {
            var (e, f) = Resolve(v, rf.Entry, rf.Field, FieldAccess.Inject);
            CheckTarget(e, f, program);
            list.Add((e, f));
        }
        return list;
    }

    private static void CheckTarget(VaultEntry e, VaultField f, string program)
    {
        if (f.AllowedTargets.Count == 0) return;
        if (f.AllowedTargets.Any(t => t.Trim().Equals(program, StringComparison.OrdinalIgnoreCase))) return;
        string what = program == "file" ? "запись в файл" : $"программа «{program}»";
        throw new AgentError($"Для «{e.Title} / {f.Label}» владелец разрешил только: {string.Join(", ", f.AllowedTargets)}. {what} не входит.");
    }

    private static string ValueOf(Vault v, SecretRef rf)
    {
        var (e, f) = Resolve(v, rf.Entry, rf.Field, FieldAccess.Inject);
        return v.GetValue(e.Id, f.Key);
    }

    private static List<VaultFieldDraft> DraftsOf(Vault v, VaultEntry e) =>
        e.Fields.Select(f => new VaultFieldDraft
        {
            Key = f.Key, Label = f.Label, Value = v.GetValue(e.Id, f.Key), IsSecret = f.IsSecret,
            Access = f.Access, AllowedTargets = f.AllowedTargets.ToList(), NoConfirm = f.NoConfirm,
        }).ToList();

    // ================= мелочи =================

    private static string CheckPath(string? path, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new AgentError("Нужен полный путь к файлу (path).");
        string full = Path.GetFullPath(path);
        if (SecretScanner.IsForbidden(full)) throw new AgentError("Этот файл трогать нельзя.");
        if (mustExist && !File.Exists(full)) throw new AgentError($"Файла {full} нет.");
        return full;
    }

    /// <summary>Однострочное значение — без перевода строки в конце; многострочный ключ — как есть.</summary>
    private static string NormalizeStored(string text)
    {
        string t = text.TrimEnd('\r', '\n');
        return t.Contains('\n') ? text : t;
    }

    private static void RestrictToCurrentUser(string path)
    {
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(sec);
    }

    public static string AccessName(FieldAccess a) => a switch
    {
        FieldAccess.Hidden => "Закрыто",
        FieldAccess.NameOnly => "Видно название",
        FieldAccess.Inject => "Подстановка",
        _ => "Открыто",
    };

    private static string Describe(AgentRequest r) =>
        string.Join(" ", new[] { r.Entry, r.Field, r.Command, r.Path, r.Directory }.Where(s => !string.IsNullOrEmpty(s)));
}
