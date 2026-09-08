using System.ComponentModel;
using System.Text.Json;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tools;

[McpServerToolType]
public class ConsumerTools
{
    [McpServerTool(Name = "find_tools")]
    [Description("Search every registered downstream MCP server for tools matching a query (tool name, description, or server). Returns the matching typed tools with their exact names ('{server}__{tool}') and full input schemas, and makes them callable in your tool list. Call the returned tool directly with the listed parameters — this is the preferred way to invoke downstream tools.")]
    public static async Task<string> FindTools(
        WrapperToolCatalog catalog,
        McpServer server,
        [Description("What you are looking for, e.g. 'send email', 'docs search', or an exact tool name")] string query,
        [Description("Maximum number of matches to return (default 10)")] int? limit = null,
        CancellationToken ct = default)
    {
        var result = await catalog.FindAsync(query, limit ?? 10, server, ct);

        var matches = result.Matches.Select(m => new
        {
            tool = m.Wrapper.ProtocolTool.Name,
            server = m.Server.Name,
            serverId = m.Server.Id,
            downstreamTool = m.Wrapper.ToolName,
            description = m.Detail.Description,
            inputSchema = m.Detail.InputSchema,
            activated = catalog.IsActive(m.Wrapper.ProtocolTool.Name, server)
        }).ToList();

        var hint = matches.Count == 0
            ? "No downstream tool matched. Try different words, or call list_services to browse every server and its tools."
            : catalog.Mode == WrapperToolMode.Lazy
                ? "Call the 'tool' name directly with the parameters in its inputSchema. These tools are callable by name now, whether or not your client has refreshed its tool list (tools/list_changed was sent to this session). If your client refuses a tool it has not listed, call invoke_tool(serverName: server, toolName: downstreamTool, arguments: <JSON object as a string>) as a fallback."
                : "Call the 'tool' name directly with the parameters in its inputSchema. If it is not in your tool list, call invoke_tool(serverName: server, toolName: downstreamTool, arguments: <JSON object as a string>) as a fallback.";

        return JsonSerializer.Serialize(new
        {
            mode = catalog.Mode.ToString(),
            matches,
            skippedServers = result.SkippedServers,
            hint
        }, JsonOptions);
    }

    [McpServerTool(Name = "list_services")]
    [Description("List all registered MCP servers with a concise summary of each server's available tools. Each tool entry includes its typed wrapper name ('{server}__{tool}'); use find_tools or get_service_details to make those callable and get their schemas.")]
    public static async Task<string> ListServices(
        ServerRegistry registry,
        ToolIndex toolIndex,
        CancellationToken ct)
    {
        await registry.EnsureLoadedAsync(ct);
        var index = await toolIndex.GetIndexAsync(ct);
        return JsonSerializer.Serialize(index, JsonOptions);
    }

    [McpServerTool(Name = "get_service_details")]
    [Description("Get full tool schemas (including input parameters) and prompt templates for a specific registered MCP server, and add that server's typed '{server}__{tool}' tools to your tool list.")]
    public static async Task<string> GetServiceDetails(
        ToolIndex toolIndex,
        WrapperToolCatalog catalog,
        McpServer server,
        [Description("The name of the registered server")] string serverName,
        CancellationToken ct)
    {
        var details = await toolIndex.GetDetailsAsync(serverName, ct);

        if (catalog.Mode == WrapperToolMode.Lazy && details.Enabled)
        {
            // Drilling into a server is a strong signal the caller intends to use it, so expose
            // its wrappers to this session now rather than requiring a separate find_tools call.
            await catalog.ActivateServerAsync(server, serverName, ct);
        }

        return JsonSerializer.Serialize(details, JsonOptions);
    }

    [McpServerTool(Name = "get_service_skill")]
    [Description("Get the skill document (markdown) for a specific server, describing best practices for using its tools.")]
    public static async Task<string> GetServiceSkill(
        SkillStore skillStore,
        [Description("The name of the registered server")] string serverName,
        CancellationToken ct)
    {
        var skill = await skillStore.GetAsync(serverName, ct);
        return skill ?? "No skill document available for this server.";
    }

    [McpServerTool(Name = "invoke_tool")]
    [Description("Escape hatch: invoke a downstream tool through the generic proxy. Prefer the typed '{server}__{tool}' tools (see find_tools), which take the tool's real parameters. Use this only when the typed tool is not in your tool list. 'arguments' must be the tool's argument object encoded as a JSON string.")]
    public static async Task<CallToolResult> InvokeTool(
        ToolProxyHandler proxy,
        ServerRegistry registry,
        [Description("The name of the registered server (from list_services), e.g. 'adjutant'")] string serverName,
        [Description("The downstream tool's own name (from list_services or get_service_details), e.g. 'send_email' — not the typed '{server}__{tool}' name")] string toolName,
        [Description("The tool's argument object encoded as a JSON string, e.g. \"{\\\"query\\\": \\\"...\\\"}\"")] string? arguments = null,
        CancellationToken ct = default)
    {
        try
        {
            return await proxy.InvokeAsync(serverName, toolName, arguments, ct);
        }
        catch (ServerNotFoundException ex) when (!ct.IsCancellationRequested)
        {
            // A small model that skipped discovery guessed the name. Name the real ones so the
            // retry needs no further round-trip.
            await registry.EnsureLoadedAsync(ct);
            var registered = registry.GetAll().Where(s => s.Enabled).Select(s => s.Name).Order(StringComparer.OrdinalIgnoreCase);
            return ErrorResult($"{ex.Message} Registered servers: [{string.Join(", ", registered)}]. " +
                               "Re-invoke with one of those as serverName, or call find_tools to locate the tool.");
        }
        catch (AggregatorException ex) when (!ct.IsCancellationRequested)
        {
            // "is unavailable." / "timed out" are written for the caller. Thrown, the SDK would
            // replace them with "An error occurred invoking 'invoke_tool'." — which is what sends
            // a small model off guessing.
            return ErrorResult(ex.Message);
        }
        catch (JsonException ex) when (!ct.IsCancellationRequested)
        {
            return ErrorResult(
                $"'arguments' must be the tool's argument object encoded as a JSON string, for example " +
                $"\"{{\\\"query\\\": \\\"text\\\"}}\". You sent: {Truncate(arguments, 200)}. Parse error: {ex.Message}");
        }
    }

    private static CallToolResult ErrorResult(string text)
        => new() { IsError = true, Content = [new TextContentBlock { Text = text }] };

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "(nothing)" : s.Length <= max ? s : s[..max] + "…";

    [McpServerTool(Name = "get_prompt")]
    [Description("Escape hatch: retrieve a rendered prompt from a downstream MCP server. Returns the prompt description and messages ready for use in a conversation. 'arguments' must be the prompt's argument object encoded as a JSON string.")]
    public static async Task<string> GetPrompt(
        ConnectionManager connectionManager,
        [Description("The name of the registered server")] string serverName,
        [Description("The name of the prompt to retrieve")] string promptName,
        [Description("JSON object of string argument values for the prompt template, or null if no arguments needed")] string? arguments = null,
        CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, object?>? args = null;
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments);
            if (raw is not null)
                args = raw.ToDictionary(kvp => kvp.Key, kvp => (object?)(kvp.Value.GetString() ?? kvp.Value.GetRawText()));
        }

        var result = await connectionManager.ExecuteWithRetryAsync<GetPromptResult>(serverName,
            async (client, token) => await client.GetPromptAsync(promptName, args, cancellationToken: token), ct);

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "refresh_service")]
    [Description("Drop the cached connection, ServerInfo, tool list, and prompt list for a registered MCP server so the next call re-fetches them from the downstream, and rebuild its typed '{server}__{tool}' tools. Does NOT touch the skill document — that is admin-authored via update_skill. Use this after a downstream server has been upgraded or restarted.")]
    public static async Task<string> RefreshService(
        ToolIndex toolIndex,
        ConnectionManager connectionManager,
        [Description("The name of the registered server")] string serverName,
        CancellationToken ct)
    {
        toolIndex.InvalidateCache(serverName);
        await connectionManager.DisconnectAsync(serverName);
        return $"Cleared cached connection, ServerInfo, tools, and prompts for '{serverName}'. Skill document was not modified. Metadata will be reloaded on next use.";
    }

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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
