using McpAggregator.Core.Configuration;
using McpAggregator.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

// CRITICAL: StdioServer must NEVER write to stdout/stderr.
// Those streams ARE the MCP transport. Any output corrupts the protocol.

var builder = Host.CreateApplicationBuilder(args);

// Clear all default logging providers (removes console logger)
builder.Logging.ClearProviders();

// Configure Serilog: file + OTLP only, NEVER console
var logConfig = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        path: Path.Combine(AppContext.BaseDirectory, "logs", "stdio-server-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7);

var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
if (!string.IsNullOrEmpty(otlpEndpoint))
{
    logConfig.WriteTo.OpenTelemetry(options =>
    {
        options.Endpoint = otlpEndpoint;
    });
}

Log.Logger = logConfig.CreateLogger();
builder.Services.AddSerilog();

// Core services
builder.Services.AddAggregatorCore(builder.Configuration);

// MCP server with stdio transport
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(ConsumerTools).Assembly);

try
{
    var app = builder.Build();
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "StdioServer terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
