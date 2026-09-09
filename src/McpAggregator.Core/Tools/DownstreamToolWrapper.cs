using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tools;

/// <summary>
/// A first-class MCP tool on the aggregator that stands in for one downstream tool. Its
/// <see cref="ProtocolTool"/> is the downstream tool renamed to <c>{server}__{tool}</c> with the
/// downstream <c>inputSchema</c> carried through unchanged, so a client sees the real parameters
/// instead of <c>invoke_tool</c>'s stringified <c>arguments</c> blob (issue #39).
/// <para>
/// Subclassing <see cref="McpServerTool"/> directly means no SDK argument binding runs: arguments
/// arrive raw in <c>request.Params.Arguments</c> and are forwarded verbatim through
/// <see cref="ToolProxyHandler"/>, which keeps timeout, retry, telemetry, the argument-schema hint
/// and <c>isError</c> propagation shared with the generic path. The only wrapper-side check is a
/// pre-flight for missing <c>required</c> keys, so that failure names the parameter without a
/// downstream round-trip (the #37 problem, for wrappers).
/// </para>
/// </summary>
public sealed class DownstreamToolWrapper : McpServerTool
{
    private const string MetaKey = "mcpAggregator";

    private readonly ToolProxyHandler _proxy;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<string> _requiredKeys;

    public DownstreamToolWrapper(RegisteredServer server, Tool downstream, ToolProxyHandler proxy, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(downstream);

        _proxy = proxy;
        _logger = logger;

        ServerName = server.Name;
        ServerId = server.Id;
        ToolName = downstream.Name;
        SchemaFingerprint = Fingerprint(downstream.InputSchema);
        _requiredKeys = RequiredKeys(downstream.InputSchema);

        var meta = downstream.Meta is { } existing
            ? (JsonObject)JsonNode.Parse(existing.ToJsonString())!
            : new JsonObject();
        meta[MetaKey] = new JsonObject
        {
            ["serverId"] = server.Id,
            ["serverName"] = server.Name,
            ["toolName"] = downstream.Name,
        };

        ProtocolTool = new Tool
        {
            Name = WrapperNaming.For(server.Name, downstream.Name),
            Title = downstream.Title,
            Description = BuildDescription(server, downstream),
            InputSchema = downstream.InputSchema,
            OutputSchema = downstream.OutputSchema,
            Annotations = downstream.Annotations,
            Icons = downstream.Icons,
            Meta = meta,
        };
    }

    public override Tool ProtocolTool { get; }

    public override IReadOnlyList<object> Metadata { get; } = [];

    /// <summary>Registered name of the downstream server this wrapper routes to.</summary>
    public string ServerName { get; }

    /// <summary>Immutable id of the downstream server at the time the wrapper was built.</summary>
    public string? ServerId { get; }

    /// <summary>The downstream tool's own name.</summary>
    public string ToolName { get; }

    /// <summary>
    /// Hash of the downstream input schema. The catalog reuses a wrapper instance when the name and
    /// fingerprint are unchanged so the tool collection is not churned on every refresh.
    /// </summary>
    public string SchemaFingerprint { get; }

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var provided = request.Params?.Arguments;

        var missing = _requiredKeys
            .Where(k => provided is null || !provided.ContainsKey(k))
            .ToList();

        if (missing.Count > 0)
        {
            _logger.LogWarning(
                "Wrapper '{Wrapper}' called without required parameter(s) [{Missing}]",
                ProtocolTool.Name, string.Join(", ", missing));

            var hint = AggregatorToolErrorFilter.TryBuildBindingHint(
                ProtocolTool.Name, ProtocolTool.InputSchema, provided, "Required parameter(s) not provided.")
                ?? $"Missing required parameter(s): [{string.Join(", ", missing)}] for tool '{ProtocolTool.Name}'. " +
                   $"Re-invoke with arguments matching this input schema: {ProtocolTool.InputSchema.GetRawText()}";

            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = hint }]
            };
        }

        IReadOnlyDictionary<string, object?>? args = provided is null
            ? null
            : provided.ToDictionary(kvp => kvp.Key, kvp => ToolProxyHandler.ConvertJsonElement(kvp.Value));

        try
        {
            return await _proxy.InvokeAsync(ServerName, ToolName, args, InvocationPath.Wrapper, cancellationToken);
        }
        catch (AggregatorException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // "Server 'x' is unavailable." / "timed out after 30s" are written for the caller.
            // Surface them as an error result rather than letting the SDK turn them into a bare
            // JSON-RPC internal error.
            _logger.LogWarning(ex, "Wrapper '{Wrapper}' failed: {Message}", ProtocolTool.Name, ex.Message);
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = ex.Message }]
            };
        }
    }

    public override string ToString() => ProtocolTool.Name;

    private static string BuildDescription(RegisteredServer server, Tool downstream)
    {
        var description = string.IsNullOrWhiteSpace(downstream.Description)
            ? $"Tool '{downstream.Name}' on downstream MCP server '{server.Name}'."
            : downstream.Description.Trim();
        return $"[{server.Name}] {description}";
    }

    private static IReadOnlyList<string> RequiredKeys(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return [];

        return schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
            ? req.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
    }

    internal static string Fingerprint(JsonElement schema)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(schema.GetRawText()));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }
}
