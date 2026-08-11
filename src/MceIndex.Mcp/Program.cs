using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MceIndex.Mcp.Configuration;
using MceIndex.Mcp.Crawling;
using MceIndex.Mcp.Parsing;
using MceIndex.Mcp.Persistence;
using MceIndex.Mcp.Services;
using MceIndex.Mcp.Tools;

var options = MceIndexOptions.Load();
var toolJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};
toolJsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MceIndexParser>();
builder.Services.AddSingleton(serviceProvider =>
    new MceIndexStore(serviceProvider.GetRequiredService<MceIndexOptions>().DatabasePath));
builder.Services.AddSingleton<IMceIndexCrawler, MceIndexCrawler>();
builder.Services.AddSingleton<RefreshCoordinator>();
builder.Services.AddSingleton<MceIndexService>();

builder.Services
    .AddMcpServer(server => server.ServerInfo = new Implementation
    {
        Name = "mceindex-mcp",
        Title = "MCEIndex",
        Version = typeof(MceIndexTools).Assembly.GetName().Version?.ToString(3) ?? "unknown",
        Description = "Discover and query locally indexed MCEIndex data. Use discover_data for broad or exploratory questions.",
    })
    .WithStdioServerTransport()
    .WithTools<MceIndexTools>(toolJsonOptions);

await builder.Build().RunAsync().ConfigureAwait(false);
