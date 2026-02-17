using System.ComponentModel;
using System.Text.Json;
using McpAggregator.Core.Services;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tools;

[McpServerToolType]
public class ConsumerTools
{
    [McpServerTool(Name = "list_services")]
    [Description("List all registered MCP servers with a concise summary of each server's available tools.")]
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
    [Description("Get full tool schemas (including input parameters) for a specific registered MCP server.")]
    public static async Task<string> GetServiceDetails(
        ToolIndex toolIndex,
        [Description("The name of the registered server")] string serverName,
        CancellationToken ct)
    {
        var details = await toolIndex.GetDetailsAsync(serverName, ct);
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
    [Description("Invoke a tool on a downstream MCP server by name. Returns the tool's response.")]
    public static async Task<string> InvokeTool(
        ToolProxyHandler proxy,
        [Description("The name of the registered server")] string serverName,
        [Description("The name of the tool to invoke")] string toolName,
        [Description("JSON object of arguments to pass to the tool")] string? arguments,
        CancellationToken ct)
    {
        return await proxy.InvokeAsync(serverName, toolName, arguments, ct);
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
