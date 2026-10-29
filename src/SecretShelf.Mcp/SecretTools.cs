using System.ComponentModel;
using ModelContextProtocol.Server;
using SecretShelf.Core.Agents;

namespace SecretShelf.Mcp;

/// <summary>Переменная окружения для запускаемой программы, значение — из поля шторки или временный секрет.</summary>
public sealed record EnvSecretArg(
    [property: Description("Environment variable name, e.g. ANTHROPIC_API_KEY")] string Name,
    [property: Description("Entry title in SecretShelf, e.g. \"API A\" (omit when temp=true)")] string? Entry = null,
    [property: Description("Field label inside the entry, e.g. \"Ключ API\" (omit when temp=true)")] string? Field = null,
    [property: Description("true: use the temporary secret the owner pasted into the top row of SecretShelf")] bool Temp = false);

/// <summary>
/// Инструменты MCP. Описания — по-английски: их читают модели разных производителей.
/// Ответы шторки — по-русски: их читает и владелец.
/// </summary>
[McpServerToolType]
public static class SecretTools
{
    private const string Rules =
        " Secret values are not returned to you; never ask the user to paste secrets into the chat.";

    private const string TempRef =
        " {{Временный}} is the temporary secret the owner pasted into the top row of SecretShelf: " +
        "it is used once without confirmation and then wiped.";

    [McpServerTool(Name = "take_temp_secret"),
     Description("Return the value of the temporary secret the owner pasted into the top row of SecretShelf, and wipe it. " +
                 "No confirmation. Prefer NOT to read it: inject it blind with write_secret_file / run_with_secrets using " +
                 "{{Временный}} (or env temp=true), so the value never enters the conversation. Read it only when there is no " +
                 "other way (e.g. it must be typed into a web form) or the user asked. Works once; ask the owner to paste again if needed.")]
    public static Task<string> TakeTempSecret(ModelContextProtocol.Server.McpServer server, CancellationToken ct) =>
        Send(server, new AgentRequest { Op = "temp_take" }, ct);

    [McpServerTool(Name = "put_temp_secret", Destructive = true),
     Description("Hand a value to the owner through the temporary secret (top row of SecretShelf) without it entering the chat: " +
                 "read from a file (source=file), a .env variable (source=env_file + variable) or a program's output (source=command). " +
                 "No confirmation; replaces a previous temporary value. Then tell the owner to copy it from SecretShelf — it is wiped after copying.")]
    public static Task<string> PutTempSecret(ModelContextProtocol.Server.McpServer server,
        [Description("file | env_file | command")] string source,
        [Description("Full path for source=file or source=env_file")] string? path = null,
        [Description("Variable name for source=env_file")] string? variable = null,
        [Description("Program for source=command")] string? command = null,
        [Description("Arguments for source=command")] string[]? args = null,
        CancellationToken ct = default) =>
        Send(server, new AgentRequest
        {
            Op = "temp_put", Source = source, Path = path, Variable = variable, Command = command, Args = args?.ToList() ?? new(),
        }, ct);

    [McpServerTool(Name = "list_secrets", ReadOnly = true),
     Description("List SecretShelf entries and fields the owner has opened to AI agents, with their access level " +
                 "(name only / inject / reveal). Values are not included. Call this first to learn exact entry and field names.")]
    public static Task<string> ListSecrets(ModelContextProtocol.Server.McpServer server, CancellationToken ct) =>
        Send(server, new AgentRequest { Op = "list" }, ct);

    [McpServerTool(Name = "get_secret_value", ReadOnly = true),
     Description("Return the value of a field whose access level is 'reveal' (non-secret data such as a login or a server address). " +
                 "Fields with 'inject' level cannot be read — use run_with_secrets or write_secret_file instead.")]
    public static Task<string> GetSecretValue(ModelContextProtocol.Server.McpServer server,
        [Description("Entry title")] string entry,
        [Description("Field label")] string field,
        CancellationToken ct) =>
        Send(server, new AgentRequest { Op = "get", Entry = entry, Field = field }, ct);

    [McpServerTool(Name = "run_with_secrets", Destructive = true),
     Description("Run a program with secrets passed as environment variables and/or rendered into its standard input. " +
                 "No shell is used: give the program and its arguments separately; a $VAR in arguments is NOT expanded. " +
                 "Prefer programs that read the key from an environment variable; for curl pass headers via stdin " +
                 "(args [\"-H\", \"@-\", url], stdin_template \"x-api-key: {{Entry/Field}}\"). " +
                 "The owner may be asked to confirm. Output comes back with secret values replaced by ***." + TempRef + Rules)]
    public static Task<string> RunWithSecrets(ModelContextProtocol.Server.McpServer server,
        [Description("Program name or full path, e.g. curl, python, ssh")] string command,
        [Description("Arguments, one item per argument")] string[]? args = null,
        [Description("Secrets to pass as environment variables")] EnvSecretArg[]? env = null,
        [Description("Optional text for the program's standard input with {{Entry/Field}} placeholders")] string? stdin_template = null,
        [Description("Working directory (full path)")] string? cwd = null,
        [Description("Timeout in seconds, 1-600, default 60")] int timeout_seconds = 60,
        CancellationToken ct = default) =>
        Send(server, new AgentRequest
        {
            Op = "run", Command = command, Args = args?.ToList() ?? new(),
            Env = env?.Select(e => new EnvSecret(e.Name, e.Entry, e.Field, e.Temp)).ToList() ?? new(),
            StdinTemplate = stdin_template, Cwd = cwd, TimeoutSeconds = timeout_seconds,
        }, ct);

    [McpServerTool(Name = "write_secret_file", Destructive = true),
     Description("Write a file (for example a .env or a config) from a template with {{Entry/Field}} placeholders. " +
                 "The file is readable only by the current Windows user; an existing file is backed up next to it. " +
                 "The owner may be asked to confirm." + TempRef + Rules)]
    public static Task<string> WriteSecretFile(ModelContextProtocol.Server.McpServer server,
        [Description("Full path of the file to write")] string path,
        [Description("File content with {{Entry/Field}} or {{Временный}} placeholders, e.g. \"ANTHROPIC_API_KEY={{API A/Ключ API}}\\n\"")] string template,
        CancellationToken ct) =>
        Send(server, new AgentRequest { Op = "write_file", Path = path, Template = template }, ct);

    [McpServerTool(Name = "store_secret", Destructive = true),
     Description("Put a secret into SecretShelf without seeing it: read it from a file (source=file, e.g. an SSH private key), " +
                 "from a variable in a .env file (source=env_file + variable) or from a program's output (source=command, e.g. a key generator). " +
                 "Creates the entry/field if missing (the new field gets 'inject' access) or replaces the value (old value stays in entry history). " +
                 "The original file is left untouched. Do this only when the user asked for it; the owner confirms." + Rules)]
    public static Task<string> StoreSecret(ModelContextProtocol.Server.McpServer server,
        [Description("Entry title to store into, e.g. \"VDS Moscow\"")] string entry,
        [Description("Field label, e.g. \"SSH key\"")] string field,
        [Description("file | env_file | command")] string source,
        [Description("Full path for source=file or source=env_file")] string? path = null,
        [Description("Variable name for source=env_file")] string? variable = null,
        [Description("Program for source=command")] string? command = null,
        [Description("Arguments for source=command")] string[]? args = null,
        [Description("true (default) to hide the value behind dots; false for a login or an address")] bool secret = true,
        CancellationToken ct = default) =>
        Send(server, new AgentRequest
        {
            Op = "store", Entry = entry, Field = field, Source = source, Path = path, Variable = variable,
            Command = command, Args = args?.ToList() ?? new(), Secret = secret,
        }, ct);

    [McpServerTool(Name = "delete_secret", Destructive = true),
     Description("Delete a field (or a whole entry if field is omitted) from SecretShelf. Always asks the owner, " +
                 "regardless of settings. Do this only when the user explicitly asked to remove it.")]
    public static Task<string> DeleteSecret(ModelContextProtocol.Server.McpServer server,
        [Description("Entry title")] string entry,
        [Description("Field label; omit to delete the whole entry")] string? field = null,
        CancellationToken ct = default) =>
        Send(server, new AgentRequest { Op = "delete", Entry = entry, Field = field }, ct);

    [McpServerTool(Name = "scan_for_secrets", ReadOnly = true),
     Description("Find where keys are stored in a folder: .env variables that look like secrets, SSH private key files and " +
                 "known token formats in code/config files. Returns file paths, variable names and line numbers only — never values. " +
                 "Always asks the owner. Ask the user which folders to scan (and which to skip) before calling.")]
    public static Task<string> ScanForSecrets(ModelContextProtocol.Server.McpServer server,
        [Description("Full path of the folder to scan (subfolders up to 6 levels)")] string directory,
        CancellationToken ct) =>
        Send(server, new AgentRequest { Op = "scan", Directory = directory }, ct);

    private static async Task<string> Send(ModelContextProtocol.Server.McpServer server, AgentRequest r, CancellationToken ct)
    {
        r.Client = server.ClientInfo?.Name is { Length: > 0 } name ? name : "MCP-клиент";
        var resp = await AgentPipe.SendAsync(r, ct);
        return resp.Ok ? resp.Text : "Отказ SecretShelf: " + resp.Text;
    }
}
