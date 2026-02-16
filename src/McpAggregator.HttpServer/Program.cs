using McpAggregator.Core.Configuration;
using McpAggregator.Core.Tools;
using McpAggregator.HttpServer.Middleware;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog: console + file + OTLP
var logConfig = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(AppContext.BaseDirectory, "logs", "http-server-.log"),
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

app.Run();
