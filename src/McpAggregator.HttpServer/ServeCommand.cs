using System.ComponentModel;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Tools;
using McpAggregator.HttpServer.Middleware;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
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
    public override async Task<int> ExecuteAsync(CommandContext context, ServeSettings settings, CancellationToken cancellation)
    {
        var logDir = settings.LogDir ?? Path.Combine(AppContext.BaseDirectory, "logs");

        var builder = WebApplication.CreateBuilder();

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
            .WithTracing(t => t.AddAspNetCoreInstrumentation())
            .WithMetrics(m => m.AddAspNetCoreInstrumentation());

        // Core services
        builder.Services.AddAggregatorCore(builder.Configuration);

        // MCP server with HTTP transport
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly(typeof(ConsumerTools).Assembly);

        // REST API + OpenAPI
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(Program).Assembly);
        builder.Services.AddOpenApi();

        var app = builder.Build();

        app.UseMiddleware<ErrorHandlingMiddleware>();

        // Health check
        app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

        // MCP endpoints
        app.MapMcp();

        // REST API
        app.MapControllers();

        // OpenAPI + Scalar
        app.MapOpenApi();
        app.MapScalarApiReference();

        await app.RunAsync();

        return 0;
    }
}
