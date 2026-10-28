using System.Text.Json;

namespace SecretShelf.Core.Agents;

/// <summary>Строка журнала: кто, что, с каким ключом, чем кончилось. Значений нет.</summary>
public sealed record AgentLogEntry(DateTime Time, string Client, string Op, string Target, string Outcome);

/// <summary>Журнал запросов агентов: agent-log.jsonl, последние 2000 строк.</summary>
public sealed class AgentLog
{
    private const int Keep = 2000;
    private readonly string _path;
    private readonly object _sync = new();

    public AgentLog(string path) => _path = path;

    public event Action<AgentLogEntry>? Written;

    public void Write(string client, string op, string target, string outcome)
    {
        var e = new AgentLogEntry(DateTime.Now, client, op, target, outcome);
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.AppendAllText(_path, JsonSerializer.Serialize(e) + "\n");
            var lines = File.ReadAllLines(_path);
            if (lines.Length > Keep * 2) File.WriteAllLines(_path, lines[^Keep..]);
        }
        Written?.Invoke(e);
    }

    public List<AgentLogEntry> Tail(int n)
    {
        lock (_sync)
        {
            if (!File.Exists(_path)) return new();
            return File.ReadLines(_path).TakeLast(n)
                .Select(l => { try { return JsonSerializer.Deserialize<AgentLogEntry>(l); } catch (JsonException) { return null; } })
                .Where(e => e != null).Select(e => e!).Reverse().ToList();
        }
    }
}
