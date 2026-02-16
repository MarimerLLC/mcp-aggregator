using System.ComponentModel;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Spectre.Console.Cli;

namespace McpAggregator.StdioServer;

public sealed class ServeSettings : CommandSettings
{
    [CommandOption("--data-dir")]
    [Description("Path to the data directory (registry, skills). Defaults to 'data' relative to the binary.")]
    public string? DataDir { get; set; }

    [CommandOption("--log-dir")]
    [Description("Path to the log directory. Defaults to 'logs' relative to the binary.")]
    public string? LogDir { get; set; }
}

public sealed class ServeCommand : AsyncCommand<ServeSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ServeSettings settings, CancellationToken cancellation)
    {
        var logDir = settings.LogDir ?? Path.Combine(AppContext.BaseDirectory, "logs");

        var builder = Host.CreateApplicationBuilder();

        // CLI overrides for configuration
        if (settings.DataDir is not null)
            builder.Configuration[$"{AggregatorOptions.SectionName}:DataDirectory"] = settings.DataDir;

        // Clear all default logging providers (removes console logger)
        builder.Logging.ClearProviders();

        // Configure Serilog: file + OTLP only, NEVER console
        var logConfig = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(logDir, "stdio-server-.log"),
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

        return 0;
    }
}
