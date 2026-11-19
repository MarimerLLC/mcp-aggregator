using Azure.AI.Inference;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Tools;
using McpAggregator.HttpServer.Middleware;
using McpAggregator.HttpServer.PublicEndpoint;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using Scalar.AspNetCore;

namespace McpAggregator.HttpServer;

/// <summary>
/// The HTTP host's services and request pipeline, separate from <see cref="ServeCommand"/>'s logging
/// and telemetry setup so tests can build the real application.
/// </summary>
public static class HttpHost
{
    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        // Core services
        builder.Services.AddAggregatorCore(builder.Configuration);

        // Conditionally register AI chat client for summary generation
        var aiOptions = builder.Configuration.GetSection(AiOptions.SectionName).Get<AiOptions>();
        if (aiOptions?.Enabled == true && !string.IsNullOrEmpty(aiOptions.Endpoint))
        {
            builder.Services.AddSingleton<IChatClient>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<AiOptions>>().Value;
                var credential = new Azure.AzureKeyCredential(options.ApiKey);
                var inferenceClient = new ChatCompletionsClient(new Uri(options.Endpoint), credential);
                return inferenceClient.AsIChatClient(options.Model);
            });

            // Skill revision (issue #57) may use a stronger model on the same endpoint.
            var skillModel = builder.Configuration.GetSection(SkillMaintenanceOptions.SectionName)
                .Get<SkillMaintenanceOptions>()?.Model;
            if (!string.IsNullOrWhiteSpace(skillModel))
            {
                builder.Services.AddKeyedSingleton<IChatClient>(SkillMaintenanceOptions.ChatClientKey, (sp, _) =>
                {
                    var options = sp.GetRequiredService<IOptions<AiOptions>>().Value;
                    var credential = new Azure.AzureKeyCredential(options.ApiKey);
                    var inferenceClient = new ChatCompletionsClient(new Uri(options.Endpoint), credential);
                    return inferenceClient.AsIChatClient(skillModel);
                });
            }
        }

        // MCP over Streamable HTTP. The 2026-07-28 revision removed protocol sessions
        // (SEP-2567): 2026 clients are stateless and call typed tools by name after find_tools.
        // Clients that still use the initialize handshake — Claude Desktop through mcp-remote,
        // Claude Code, rockbot — only call tools they have listed, and the only way their list can
        // grow is a session that receives tools/list_changed. StatefulForInitializeClients gives
        // exactly those clients a session and everyone else stateless requests.
        var sessionMode = builder.Configuration.GetValue<HttpServerSessionMode?>("McpAggregator:Http:SessionMode")
            ?? HttpServerSessionMode.StatefulForInitializeClients;
        builder.Services.AddAggregatorMcpServer()
            .WithHttpTransport(options => options.SessionMode = sessionMode)
            .WithToolsFromAssembly(typeof(ConsumerTools).Assembly);

        // REST API + OpenAPI
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(Program).Assembly);
        builder.Services.AddOpenApi();

        // Internet-facing listener with OAuth / personal access tokens (off unless configured).
        builder.AddPublicEndpoint();
    }

    public static void ConfigurePipeline(WebApplication app)
    {
        app.UseMiddleware<ErrorHandlingMiddleware>();

        if (app.Services.GetService<TokenService>() is not null)
            app.UsePublicEndpoint();

        // Health check
        app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

        // MCP endpoints
        app.MapMcp();

        // REST API
        app.MapGet("/api", (IOptions<AggregatorOptions> opts) =>
        {
            var o = opts.Value;
            return Results.Ok(new
            {
                name = o.SelfName,
                description = o.SelfApiDescription,
                api = "/api/services",
                skill = $"/api/services/{o.SelfName}/skill"
            });
        });
        app.MapControllers();

        // OpenAPI + Scalar
        app.MapOpenApi();
        app.MapScalarApiReference();
    }
}
