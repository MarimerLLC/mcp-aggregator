using System.ComponentModel;
using McpAggregator.Core;
using McpAggregator.Core.Configuration;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Spectre.Console.Cli;

namespace McpAggregator.HttpServer;

public sealed class ServeSettings : CommandSettings
{
    [CommandOption("--data-dir")]
    [Description("Path to the data directory (registry, skills). Defaults to 'data' relative to the binary.")]
    public string? DataDir { get; set; }

    [CommandOption("--log-dir")]
    [Description("Path to the log directory. Defaults to 'logs' relative to the binary.")]
    public string? LogDir { get; set; }

    [CommandOption("--port")]
    [Description("HTTP listen port. Defaults to the value in appsettings or 5000.")]
    public int? Port { get; set; }
}

public sealed class ServeCommand : AsyncCommand<ServeSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ServeSettings settings, CancellationToken cancellation)
    {
        var logDir = settings.LogDir ?? Path.Combine(AppContext.BaseDirectory, "logs");

        var builder = WebApplication.CreateBuilder();

        // Load user secrets regardless of environment
        builder.Configuration.AddUserSecrets<ServeCommand>(optional: true);

        // CLI overrides for configuration
        if (settings.DataDir is not null)
            builder.Configuration[$"{AggregatorOptions.SectionName}:DataDirectory"] = settings.DataDir;

        if (settings.Port is not null)
            builder.Configuration["Urls"] = $"http://localhost:{settings.Port}";

        // Serilog: console + file + OTLP
        var logConfig = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                path: Path.Combine(logDir, "http-server-.log"),
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
        builder.Host.UseSerilog();

        // OpenTelemetry tracing & metrics
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("mcp-aggregator"))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation();
                t.AddSource(AggregatorTelemetry.ServiceName);
                if (!string.IsNullOrEmpty(otlpEndpoint))
                    t.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation();
                m.AddMeter(AggregatorTelemetry.ServiceName);
                if (!string.IsNullOrEmpty(otlpEndpoint))
                    m.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            });

        HttpHost.ConfigureServices(builder);

        var app = builder.Build();
        HttpHost.ConfigurePipeline(app);

        await app.RunAsync();

        return 0;
    }
}
