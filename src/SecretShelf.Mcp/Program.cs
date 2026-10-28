// SecretShelf MCP-сервер: инструменты для ИИ-агентов поверх шторки SecretShelf.
// Ключей у сервера нет — каждый запрос уходит в шторку по локальному каналу, там проверяются
// уровни доступа и спрашивается подтверждение владельца. Протокол MCP идёт через stdin/stdout,
// поэтому журнал сервера — только в stderr.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "secretshelf", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
