using System.ComponentModel;
using System.Text.Json;
using McpAggregator.Core.Models;
using McpAggregator.Core.Services;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tools;

[McpServerToolType]
public class AdminTools
{
    [McpServerTool(Name = "register_server")]
    [Description("Register a new downstream MCP server with the aggregator.")]
    public static async Task<string> RegisterServer(
        ServerRegistry registry,
        [Description("Unique name for the server")] string name,
        [Description("Transport type: 'Stdio' or 'Http'")] string transportType,
        [Description("For Stdio: the command to run. For Http: the server URL.")] string endpoint,
        [Description("Optional display name")] string? displayName,
        [Description("Optional description")] string? description,
        [Description("For Stdio: JSON array of command arguments")] string? arguments,
        [Description("For Stdio: JSON object of environment variables")] string? environment,
        CancellationToken ct)
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
                break;
        }

        var server = new RegisteredServer
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            Transport = transport
        };

        await registry.RegisterAsync(server, ct);
        return $"Server '{name}' registered successfully.";
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
        [Description("The name of the registered server")] string serverName,
        [Description("Markdown content for the skill document")] string markdown,
        CancellationToken ct)
    {
        registry.Get(serverName); // Validate server exists
        await skillStore.SetAsync(serverName, markdown, ct);
        await registry.UpdateSkillFlagAsync(serverName, true, ct);
        return $"Skill document updated for '{serverName}'.";
    }
}
