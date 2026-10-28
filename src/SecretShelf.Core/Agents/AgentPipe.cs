using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SecretShelf.Core.Agents;

/// <summary>
/// Локальный канал «MCP-сервер → шторка» (named pipe). Доступ — только текущему пользователю
/// Windows, подключения по сети запрещены. Одна строка JSON запроса, одна строка ответа.
/// </summary>
public static class AgentPipe
{
    /// <summary>Имя канала; SECRETSHELF_PIPE переопределяет его для тестов.</summary>
    public static string Name =>
        Environment.GetEnvironmentVariable("SECRETSHELF_PIPE") is { Length: > 0 } custom
            ? custom
            : "SecretShelf.Agents." + WindowsIdentity.GetCurrent().User!.Value;

    // ---------- шторка ----------

    /// <summary>Принимать запросы, пока не отменят. Каждое подключение обслуживается отдельно.</summary>
    public static async Task ServeAsync(Func<AgentRequest, CancellationToken, Task<AgentResponse>> handle, CancellationToken ct, string? name = null)
    {
        name ??= Name;
        while (!ct.IsCancellationRequested)
        {
            var pipe = CreateServer(name);
            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            _ = Task.Run(() => ServeOneAsync(pipe, handle, ct), ct);
        }
    }

    private static NamedPipeServerStream CreateServer(string name)
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, sec);
    }

    private static async Task ServeOneAsync(NamedPipeServerStream pipe, Func<AgentRequest, CancellationToken, Task<AgentResponse>> handle, CancellationToken ct)
    {
        await using (pipe)
        {
            AgentResponse resp;
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                string? line = await reader.ReadLineAsync(ct);
                var req = line == null ? null : JsonSerializer.Deserialize<AgentRequest>(line, AgentJson.Options);
                resp = req == null ? AgentResponse.Fail("Пустой запрос.") : await handle(req, ct);
            }
            catch (JsonException) { resp = AgentResponse.Fail("Неразборчивый запрос."); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { return; }

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(resp, AgentJson.Options) + "\n");
                await pipe.WriteAsync(bytes, ct);
                await pipe.FlushAsync(ct);
            }
            catch (IOException) { /* клиент ушёл, не дождавшись */ }
        }
    }

    // ---------- MCP-сервер ----------

    /// <summary>Отправить запрос шторке. Шторка не запущена — понятный ответ, а не исключение.</summary>
    public static async Task<AgentResponse> SendAsync(AgentRequest request, CancellationToken ct, string? name = null)
    {
        await using var pipe = new NamedPipeClientStream(".", name ?? Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(3000, ct);
        }
        catch (TimeoutException)
        {
            return AgentResponse.Fail("SecretShelf не запущен. Попросите владельца запустить шторку.");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, AgentJson.Options) + "\n");
        await pipe.WriteAsync(bytes, ct);
        await pipe.FlushAsync(ct);
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        // Ответ может ждать владельца у окна подтверждения — без таймаута здесь, его держит шторка.
        string? line = await reader.ReadLineAsync(ct);
        return line == null
            ? AgentResponse.Fail("Шторка закрыла соединение без ответа.")
            : JsonSerializer.Deserialize<AgentResponse>(line, AgentJson.Options) ?? AgentResponse.Fail("Пустой ответ шторки.");
    }
}
