using System.ComponentModel;
using System.Text.Json;
using McpAggregator.Core.Models;
using McpAggregator.Core.Services;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tools;

/// <summary>
/// Tools that change what the aggregator is, rather than use it. Deliberately <b>not</b> marked
/// <c>[McpServerToolType]</c>: <see cref="AdminToolSet"/> builds them so the SDK never adds them to
/// the shared tool collection on its own. In <see cref="Configuration.WrapperToolMode.Lazy"/> mode
/// they stay out of every session's initial <c>tools/list</c> and are disclosed per session by
/// <c>show_admin_tools</c> (or by calling one by name); in Eager mode they are listed as usual.
/// </summary>
public class AdminTools
{
    /// <summary>
    /// The names of every tool declared on this class plus the enable/disable pair. This is the
    /// set <see cref="Services.WrapperToolCatalog"/> hides in Lazy mode; a test keeps it equal to
    /// the <see cref="McpServerToolAttribute"/> names actually declared here.
    /// </summary>
    public static readonly IReadOnlySet<string> ToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "register_server", "update_server", "unregister_server", "update_skill", "regenerate_summary",
        "enable_service", "disable_service",
    };

    /// <summary>Name and description of every tool declared here, in declaration order.</summary>
    public static IReadOnlyList<(string Name, string? Description)> Describe()
        => typeof(AdminTools)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(m => (
                Name: m.GetCustomAttributes(typeof(McpServerToolAttribute), false).OfType<McpServerToolAttribute>().FirstOrDefault()?.Name,
                Description: m.GetCustomAttributes(typeof(DescriptionAttribute), false).OfType<DescriptionAttribute>().FirstOrDefault()?.Description))
            .Where(t => t.Name is not null)
            .Select(t => (t.Name!, t.Description))
            .ToList();

    [McpServerTool(Name = "enable_service")]
    [Description("Enable a registered MCP server, allowing its tools to be invoked.")]
    public static async Task<string> EnableService(
        ServerRegistry registry,
        [Description("The name of the registered server")] string serverName,
        CancellationToken ct)
    {
        await registry.EnsureLoadedAsync(ct);
        await registry.SetEnabledAsync(serverName, true, ct);
        return $"Server '{serverName}' enabled.";
    }

    [McpServerTool(Name = "disable_service")]
    [Description("Disable a registered MCP server, preventing its tools from being invoked.")]
    public static async Task<string> DisableService(
        ServerRegistry registry,
        [Description("The name of the registered server")] string serverName,
        CancellationToken ct)
    {
        await registry.EnsureLoadedAsync(ct);
        await registry.SetEnabledAsync(serverName, false, ct);
        return $"Server '{serverName}' disabled.";
    }

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
        ToolIndex toolIndex,
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

        // Drop any live connection so the next call reconnects with the new configuration, and the
        // cached schemas so the typed wrapper tools are rebuilt from whatever the new transport serves.
        await connectionManager.DisconnectAsync(serverName);
        if (transport is not null)
            toolIndex.InvalidateCache(serverName);

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
        [Description("The name of the registered server to remove")] string serverName,
        CancellationToken ct)
    {
        await registry.EnsureLoadedAsync(ct);
        await connectionManager.DisconnectAsync(serverName);
        skillStore.Delete(serverName);
        await registry.UnregisterAsync(serverName, ct);
        return $"Server '{serverName}' unregistered successfully.";
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
        var baselineRecorded = await SkillSnapshot.CaptureAsync(registry, toolIndex, serverName, ct);
        return baselineRecorded
            ? $"Skill document updated for '{serverName}'."
            : $"Skill document updated for '{serverName}'. {SkillSnapshot.NoBaselineNote}";
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
