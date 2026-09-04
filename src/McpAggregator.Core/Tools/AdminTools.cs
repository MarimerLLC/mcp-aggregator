using System.ComponentModel;
using System.Text.Json;
using McpAggregator.Core.Models;
using McpAggregator.Core.Services;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tools;

[McpServerToolType]
public class AdminTools
{
    [McpServerTool(Name = "register_server")]
    [Description("Register a new downstream MCP server with the aggregator.")]
    public static async Task<string> RegisterServer(
        ServerRegistry registry,
        ConnectionManager connectionManager,
        SummaryGenerator summaryGenerator,
        [Description("Unique name for the server")] string name,
        [Description("Transport type: 'Stdio' or 'Http'")] string transportType,
        [Description("For Stdio: the command to run. For Http: the server URL.")] string endpoint,
        [Description("Optional display name")] string? displayName = null,
        [Description("Optional description")] string? description = null,
        [Description("For Stdio: JSON array of command arguments")] string? arguments = null,
        [Description("For Stdio: JSON object of environment variables")] string? environment = null,
        [Description("For Http: JSON object of HTTP headers. Values may reference environment variables as ${VAR}, resolved at connect time.")] string? headers = null,
        [Description("For Http: connection timeout in seconds")] int? connectionTimeoutSeconds = null,
        CancellationToken ct = default)
    {
        var transport = new TransportConfig();

        if (Enum.TryParse<TransportType>(transportType, ignoreCase: true, out var tt))
            transport.Type = tt;
        else
            return $"Invalid transport type '{transportType}'. Use 'Stdio' or 'Http'.";

        switch (transport.Type)
        {
            case TransportType.Stdio:
                transport.Command = endpoint;
                transport.Arguments = arguments is not null
                    ? JsonSerializer.Deserialize<string[]>(arguments)
                    : null;
                transport.Environment = environment is not null
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(environment)
                    : null;
                break;
            case TransportType.Http:
                transport.Url = endpoint;
                transport.Headers = headers is not null
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(headers)
                    : null;
                transport.ConnectionTimeout = connectionTimeoutSeconds is { } seconds
                    ? TimeSpan.FromSeconds(seconds)
                    : null;
                break;
        }

        var server = new RegisteredServer
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            Transport = transport
        };

        await registry.EnsureLoadedAsync(ct);
        await registry.RegisterAsync(server, ct);

        // Generate AI summary if available
        var summary = await GenerateSummaryForServerAsync(
            server, registry, connectionManager, summaryGenerator, ct);

        var result = $"Server '{name}' registered successfully.";
        if (summary is not null)
            result += $" AI summary: {summary}";

        return result;
    }

    [McpServerTool(Name = "update_server")]
    [Description("Update a registered server's transport configuration or metadata, preserving its skill document and AI summary.")]
    public static async Task<string> UpdateServer(
        ServerRegistry registry,
        ConnectionManager connectionManager,
        [Description("The name of the registered server")] string serverName,
        [Description("Transport type: 'Stdio' or 'Http'. Required together with endpoint to replace the transport.")] string? transportType = null,
        [Description("For Stdio: the command to run. For Http: the server URL. Required together with transportType.")] string? endpoint = null,
        [Description("New display name")] string? displayName = null,
        [Description("New description")] string? description = null,
        [Description("For Stdio: JSON array of command arguments")] string? arguments = null,
        [Description("For Stdio: JSON object of environment variables")] string? environment = null,
        [Description("For Http: JSON object of HTTP headers. Values may reference environment variables as ${VAR}, resolved at connect time.")] string? headers = null,
        [Description("For Http: connection timeout in seconds")] int? connectionTimeoutSeconds = null,
        CancellationToken ct = default)
    {
        await registry.EnsureLoadedAsync(ct);

        TransportConfig? transport = null;

        if (transportType is not null || endpoint is not null)
        {
            if (transportType is null || endpoint is null)
                return "Both 'transportType' and 'endpoint' must be supplied to replace the transport configuration.";

            if (!Enum.TryParse<TransportType>(transportType, ignoreCase: true, out var tt))
                return $"Invalid transport type '{transportType}'. Use 'Stdio' or 'Http'.";

            transport = new TransportConfig { Type = tt };

            switch (tt)
            {
                case TransportType.Stdio:
                    transport.Command = endpoint;
                    transport.Arguments = arguments is not null
                        ? JsonSerializer.Deserialize<string[]>(arguments)
                        : null;
                    transport.Environment = environment is not null
                        ? JsonSerializer.Deserialize<Dictionary<string, string>>(environment)
                        : null;
                    break;
                case TransportType.Http:
                    transport.Url = endpoint;
                    transport.Headers = headers is not null
                        ? JsonSerializer.Deserialize<Dictionary<string, string>>(headers)
                        : null;
                    transport.ConnectionTimeout = connectionTimeoutSeconds is { } seconds
                        ? TimeSpan.FromSeconds(seconds)
                        : null;
                    break;
            }
        }

        await registry.UpdateServerAsync(serverName, transport, displayName, description, ct);

        // Drop any live connection so the next call reconnects with the new configuration.
        await connectionManager.DisconnectAsync(serverName);

        return $"Server '{serverName}' updated successfully.";
    }

    [McpServerTool(Name = "regenerate_summary")]
    [Description("Re-generate the AI summary for an existing registered server.")]
    public static async Task<string> RegenerateSummary(
        ServerRegistry registry,
        ConnectionManager connectionManager,
        SummaryGenerator summaryGenerator,
        [Description("The name of the registered server")] string serverName,
        CancellationToken ct)
    {
        await registry.EnsureLoadedAsync(ct);
        var server = registry.Get(serverName);

        if (!summaryGenerator.IsAvailable)
            return "AI summary generation is not configured. Set McpAggregator:AI:Enabled to true and provide an endpoint.";

        var summary = await GenerateSummaryForServerAsync(
            server, registry, connectionManager, summaryGenerator, ct);

        return summary is not null
            ? $"Summary updated for '{serverName}': {summary}"
            : $"Failed to generate summary for '{serverName}'.";
    }

    [McpServerTool(Name = "unregister_server")]
    [Description("Remove a registered MCP server from the aggregator.")]
    public static async Task<string> UnregisterServer(
        ServerRegistry registry,
        ConnectionManager connectionManager,
        SkillStore skillStore,
        [Description("The name of the server to remove")] string name,
        CancellationToken ct)
    {
        await registry.EnsureLoadedAsync(ct);
        await connectionManager.DisconnectAsync(name);
        skillStore.Delete(name);
        await registry.UnregisterAsync(name, ct);
        return $"Server '{name}' unregistered successfully.";
    }

    [McpServerTool(Name = "update_skill")]
    [Description("Set or update the skill document (markdown) for a registered server.")]
    public static async Task<string> UpdateSkill(
        ServerRegistry registry,
        SkillStore skillStore,
        ToolIndex toolIndex,
        [Description("The name of the registered server")] string serverName,
        [Description("Markdown content for the skill document")] string markdown,
        CancellationToken ct)
    {
        await registry.EnsureLoadedAsync(ct);
        registry.Get(serverName); // Validate server exists
        await skillStore.SetAsync(serverName, markdown, ct);
        await registry.UpdateSkillFlagAsync(serverName, true, ct);
        await SkillSnapshot.CaptureAsync(registry, toolIndex, serverName, ct);
        return $"Skill document updated for '{serverName}'.";
    }

    private static async Task<string?> GenerateSummaryForServerAsync(
        RegisteredServer server,
        ServerRegistry registry,
        ConnectionManager connectionManager,
        SummaryGenerator summaryGenerator,
        CancellationToken ct)
    {
        if (!summaryGenerator.IsAvailable)
            return null;

        try
        {
            var mcpTools = await connectionManager.ExecuteWithRetryAsync<IList<McpClientTool>>(server.Name,
                async (client, token) => await client.ListToolsAsync(cancellationToken: token), ct);

            var toolSummaries = mcpTools.Select(t => new ToolSummary
            {
                Name = t.Name,
                Description = t.Description
            }).ToList();

            List<PromptDetail> promptDetails = [];
            try
            {
                var mcpPrompts = await connectionManager.ExecuteWithRetryAsync<IList<McpClientPrompt>>(server.Name,
                    async (client, token) => await client.ListPromptsAsync(cancellationToken: token), ct);

                promptDetails = mcpPrompts.Select(p => new PromptDetail
                {
                    Name = p.Name,
                    Description = p.Description,
                    Arguments = p.ProtocolPrompt.Arguments?.Select(a => new PromptArgumentDetail
                    {
                        Name = a.Name,
                        Description = a.Description,
                        Required = a.Required ?? false
                    }).ToList() ?? []
                }).ToList();
            }
            catch
            {
                // Prompt listing is best-effort; some servers may not support it
            }

            var summary = await summaryGenerator.GenerateSummaryAsync(server, toolSummaries, promptDetails, ct);

            if (summary is not null)
            {
                await registry.UpdateSummaryAsync(server.Name, summary, ct);
            }

            return summary;
        }
        catch
        {
            // Summary generation is best-effort; don't fail registration
            return null;
        }
    }
}
