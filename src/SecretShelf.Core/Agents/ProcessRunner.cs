using System.Diagnostics;
using System.Text;

namespace SecretShelf.Core.Agents;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

/// <summary>
/// Запуск программы для агента. Без командной оболочки: аргументы передаются списком,
/// секреты — переменными окружения или на стандартный ввод, в командную строку не попадают.
/// </summary>
public static class ProcessRunner
{
    /// <summary>Полный путь к программе: как есть, если путь полный, иначе поиск по PATH с PATHEXT.</summary>
    public static string? Resolve(string command)
    {
        command = command.Trim().Trim('"');
        if (Path.IsPathFullyQualified(command)) return File.Exists(command) ? command : null;

        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string baseName;
            try { baseName = Path.Combine(dir.Trim().Trim('"'), command); } catch (ArgumentException) { continue; }
            if (Path.HasExtension(command) && File.Exists(baseName)) return baseName;
            foreach (var ext in exts)
                if (File.Exists(baseName + ext)) return baseName + ext;
        }
        return null;
    }

    /// <summary>Команда целиком для окна подтверждения.</summary>
    public static string Display(string exe, IEnumerable<string> args) =>
        string.Join(" ", new[] { Path.GetFileName(exe) }.Concat(args.Select(a => a.Contains(' ') || a.Length == 0 ? $"\"{a}\"" : a)));

    public static async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, Dictionary<string, string> env,
                                                     string? stdin, string? cwd, int timeoutSeconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException($"Папки {cwd} нет.");
            psi.WorkingDirectory = cwd;
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Программа не запустилась.");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        if (stdin != null) await p.StandardInput.WriteAsync(stdin);
        p.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        bool timedOut = false;
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (!timedOut) throw;
        }
        string so = await outTask, se = await errTask;
        return new ProcessResult(timedOut ? -1 : p.ExitCode, so, se, timedOut);
    }
}
