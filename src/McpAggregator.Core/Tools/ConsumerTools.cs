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
    [Description("Search every registered downstream MCP server for tools and prompt templates matching a query (name, description, or server). Returns the matching typed tools with their exact names ('{server}__{tool}') and full input schemas, plus matching prompts ('{server}__{prompt}') with their arguments, and makes them callable in your tool and prompt lists. Call the returned tool directly with the listed parameters — this is the preferred way to invoke downstream tools.")]
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

        var prompts = result.Prompts.Select(m => new
        {
            prompt = m.Wrapper.ProtocolPrompt.Name,
            server = m.Server.Name,
            serverId = m.Server.Id,
            downstreamPrompt = m.Wrapper.PromptName,
            description = m.Detail.Description,
            arguments = m.Detail.Arguments,
            activated = catalog.IsPromptActive(m.Wrapper.ProtocolPrompt.Name, server)
        }).ToList();

        var hint = matches.Count == 0 && prompts.Count == 0
            ? "No downstream tool or prompt matched. Try different words, or call list_services to browse every server and its tools. Administrative tools (register/update/unregister servers, skills, summaries, enable/disable) are not searched here; call show_admin_tools for those."
            : catalog.Mode == WrapperToolMode.Lazy
                ? "Call the 'tool' name directly with the parameters in its inputSchema; tools/list_changed was sent to this session. If your client rejects the name as not found (its tool index has not refreshed), do not retry it: call invoke_tool(serverName: server, toolName: downstreamTool, arguments: <JSON object as a string>) with the same arguments instead. Prompts are requested through prompts/get by their 'prompt' name (prompts/list_changed was sent); get_prompt(serverName: server, promptName: downstreamPrompt, arguments) is the fallback."
                : "Call the 'tool' name directly with the parameters in its inputSchema. If your client rejects the name as not found, call invoke_tool(serverName: server, toolName: downstreamTool, arguments: <JSON object as a string>) with the same arguments instead. Prompts are requested through prompts/get by their 'prompt' name; get_prompt(serverName: server, promptName: downstreamPrompt, arguments) is the fallback.";

        return JsonSerializer.Serialize(new
        {
            mode = catalog.Mode.ToString(),
            matches,
            prompts,
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
    [Description("Get full tool schemas (including input parameters) and prompt templates for a specific registered MCP server, and add that server's typed '{server}__{tool}' tools and '{server}__{prompt}' prompts to your tool and prompt lists.")]
    public static async Task<string> GetServiceDetails(
        ToolIndex toolIndex,
        WrapperToolCatalog catalog,
        McpServer server,
        [Description("The name of the registered server")] string serverName,
        CancellationToken ct)
    {
        var details = await toolIndex.GetDetailsAsync(serverName, ct);

        if (catalog.Mode == WrapperToolMode.Lazy && details.Enabled && details.Id != ToolIndex.SelfId)
        {
            // Drilling into a server is a strong signal the caller intends to use it, so expose
            // its wrappers to this session now rather than requiring a separate find_tools call.
            // (The aggregator's own entry has no wrappers; its tools are already on this connection.)
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
    [Description("Invoke a downstream tool through the generic proxy. The typed '{server}__{tool}' tools (see find_tools) take the tool's real parameters; use this when your client rejects a typed name as not found. Downstream servers only: the aggregator's own tools (find_tools, update_skill, ...) are called directly. 'arguments' must be the tool's argument object encoded as a JSON string.")]
    public static async Task<CallToolResult> InvokeTool(
        ToolProxyHandler proxy,
        ServerRegistry registry,
        [Description("The name of the registered server (from list_services), e.g. 'adjutant'")] string serverName,
        [Description("The downstream tool's own name (from list_services or get_service_details), e.g. 'send_email' — not the typed '{server}__{tool}' name")] string toolName,
        [Description("The tool's argument object encoded as a JSON string, e.g. \"{\\\"query\\\": \\\"...\\\"}\"")] string? arguments = null,
        CancellationToken ct = default)
    {
        // list_services advertises the aggregator itself (so its skill document is discoverable),
        // which invites exactly this call. Its tools are ordinary MCP tools on this connection.
        if (string.Equals(serverName, registry.SelfName, StringComparison.OrdinalIgnoreCase))
        {
            return ErrorResult($"'{serverName}' is this aggregator, not a downstream server, so invoke_tool cannot reach its tools. " +
                               $"Call '{toolName}' directly as a tool on this connection with the same arguments. " +
                               "If your client does not list it, call show_admin_tools (administrative tools) or find_tools first; " +
                               "the aggregator also accepts its own tools by name whether or not they are listed.");
        }

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
    [Description("Escape hatch: retrieve a rendered prompt template from a downstream MCP server. Downstream prompts are also exposed as MCP prompts named '{server}__{prompt}' (see find_tools or get_service_details); use those through prompts/get when your client supports prompts, and this tool when it does not. Returns the prompt description and messages ready for use in a conversation. 'arguments' must be the prompt's argument object encoded as a JSON string.")]
    public static async Task<CallToolResult> GetPrompt(
        ToolProxyHandler proxy,
        ServerRegistry registry,
        [Description("The name of the registered server (from list_services), e.g. 'adjutant'")] string serverName,
        [Description("The downstream prompt's own name (from get_service_details), e.g. 'plan_release' — not the '{server}__{prompt}' name")] string promptName,
        [Description("JSON object of string argument values for the prompt template, e.g. \"{\\\"topic\\\": \\\"...\\\"}\", or null if no arguments needed")] string? arguments = null,
        CancellationToken ct = default)
    {
        if (string.Equals(serverName, registry.SelfName, StringComparison.OrdinalIgnoreCase))
        {
            return ErrorResult($"'{serverName}' is this aggregator, not a downstream server, and it has no prompts of its own. " +
                               "Downstream prompts are listed by find_tools and get_service_details.");
        }

        try
        {
            IReadOnlyDictionary<string, object?>? args = null;
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments);
                if (raw is not null)
                    args = raw.ToDictionary(kvp => kvp.Key, kvp => (object?)(kvp.Value.ValueKind == JsonValueKind.String ? kvp.Value.GetString() : kvp.Value.GetRawText()));
            }

            var result = await proxy.GetPromptAsync(serverName, promptName, args, InvocationPath.GetPrompt, ct);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result, JsonOptions) }]
            };
        }
        catch (ServerNotFoundException ex) when (!ct.IsCancellationRequested)
        {
            await registry.EnsureLoadedAsync(ct);
            var registered = registry.GetAll().Where(s => s.Enabled).Select(s => s.Name).Order(StringComparer.OrdinalIgnoreCase);
            return ErrorResult($"{ex.Message} Registered servers: [{string.Join(", ", registered)}]. " +
                               "Re-invoke with one of those as serverName, or call find_tools to locate the prompt.");
        }
        catch (AggregatorException ex) when (!ct.IsCancellationRequested)
        {
            return ErrorResult(ex.Message);
        }
        catch (JsonException ex) when (!ct.IsCancellationRequested)
        {
            return ErrorResult(
                $"'arguments' must be the prompt's argument object encoded as a JSON string, for example " +
                $"\"{{\\\"topic\\\": \\\"text\\\"}}\". You sent: {Truncate(arguments, 200)}. Parse error: {ex.Message}");
        }
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

    [McpServerTool(Name = "show_admin_tools")]
    [Description("Add the aggregator's administrative tools to your tool list: register_server, update_server, unregister_server, update_skill, regenerate_summary, enable_service, disable_service. They are hidden by default so ordinary sessions do not carry them. They can also be called by name without this step.")]
    public static async Task<string> ShowAdminTools(
        WrapperToolCatalog catalog,
        McpServer server,
        CancellationToken ct)
    {
        await catalog.ActivateAdminToolsAsync(server, ct);

        var tools = catalog.AdminTools
            .OrderBy(t => t.ProtocolTool.Name, StringComparer.Ordinal)
            .Select(t => new { name = t.ProtocolTool.Name, description = t.ProtocolTool.Description })
            .ToList();

        var hint = catalog.Mode == WrapperToolMode.Lazy
            ? "These tools are now in this session's tool list (tools/list_changed was sent). If your client rejects one of these names as not found, its tool index has not refreshed; the aggregator's own tools still work by name."
            : "These tools are always listed in Eager mode.";

        return JsonSerializer.Serialize(new { tools, hint }, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
