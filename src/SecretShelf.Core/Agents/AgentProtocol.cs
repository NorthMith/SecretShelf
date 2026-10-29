using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecretShelf.Core.Agents;

/// <summary>Ссылка на поле хранилища: «Запись / Поле» по названиям, как их видит владелец.</summary>
public sealed record SecretRef(string Entry, string Field);

/// <summary>
/// Секрет, передаваемый запускаемой программе переменной окружения:
/// поле «Запись / Поле» или временный секрет (<paramref name="Temp"/>).
/// </summary>
public sealed record EnvSecret(string Name, string? Entry = null, string? Field = null, bool Temp = false);

/// <summary>
/// Запрос MCP-сервера к шторке. Одна строка JSON на подключение к каналу.
/// Поля заполняются по смыслу операции, лишние игнорируются.
/// </summary>
public sealed class AgentRequest
{
    /// <summary>list | get | run | write_file | store | delete | scan | temp_take | temp_put</summary>
    public string Op { get; set; } = "";
    /// <summary>Имя агента, как он сам себя назвал (не проверяется).</summary>
    public string Client { get; set; } = "";

    public string? Entry { get; set; }
    public string? Field { get; set; }

    // run
    public string? Command { get; set; }
    public List<string> Args { get; set; } = new();
    public List<EnvSecret> Env { get; set; } = new();
    /// <summary>Текст на стандартный ввод программы, с подстановками {{Запись/Поле}}.</summary>
    public string? StdinTemplate { get; set; }
    public string? Cwd { get; set; }
    public int TimeoutSeconds { get; set; } = 60;

    // write_file
    public string? Path { get; set; }
    public string? Template { get; set; }

    // store, temp_put: source = file | env_file | command
    public string? Source { get; set; }
    public string? Variable { get; set; }
    /// <summary>Новое поле секретное (скрыто точками). Для логинов и адресов — false.</summary>
    public bool Secret { get; set; } = true;

    // scan
    public string? Directory { get; set; }

    /// <summary>
    /// Процесс, подключившийся к каналу (MCP-сервер сессии агента). Заполняет шторка по данным
    /// Windows, а не клиент: в JSON поле не читается и не пишется — подделать его нельзя.
    /// </summary>
    [JsonIgnore]
    public int ClientProcessId { get; set; }

    /// <summary>Только для тестов: ключ сессии без реального процесса.</summary>
    [JsonIgnore]
    internal string? SessionKeyOverride { get; set; }
}

public sealed class AgentResponse
{
    public bool Ok { get; set; }
    public string Text { get; set; } = "";

    public static AgentResponse Success(string text) => new() { Ok = true, Text = text };
    public static AgentResponse Fail(string text) => new() { Ok = false, Text = text };
}

public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
