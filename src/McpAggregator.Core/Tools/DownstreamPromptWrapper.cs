using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tools;

/// <summary>
/// A first-class MCP prompt on the aggregator that stands in for one downstream prompt. Its
/// <see cref="ProtocolPrompt"/> is the downstream prompt renamed to <c>{server}__{prompt}</c> with
/// the downstream argument list carried through unchanged, so a host that surfaces prompts
/// natively shows the real arguments instead of <c>get_prompt</c>'s stringified <c>arguments</c>
/// blob (issue #40).
/// <para>
/// Subclassing <see cref="McpServerPrompt"/> directly means no SDK argument binding runs:
/// arguments arrive raw in <c>request.Params.Arguments</c> and are forwarded through
/// <see cref="ToolProxyHandler.GetPromptAsync"/>, which keeps timeout, retry and telemetry shared
/// with the escape hatch. The only wrapper-side check is a pre-flight for missing required
/// arguments, so that failure names the argument without a downstream round-trip. Prompts have no
/// <c>isError</c> result, so every failure here is a JSON-RPC error carrying a readable message.
/// </para>
/// </summary>
public sealed class DownstreamPromptWrapper : McpServerPrompt
{
    private const string MetaKey = "mcpAggregator";

    private readonly ToolProxyHandler _proxy;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<string> _requiredArguments;

    public DownstreamPromptWrapper(RegisteredServer server, Prompt downstream, ToolProxyHandler proxy, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(downstream);

        _proxy = proxy;
        _logger = logger;

        ServerName = server.Name;
        ServerId = server.Id;
        PromptName = downstream.Name;
        ArgumentsFingerprint = Fingerprint(downstream);
        _requiredArguments = downstream.Arguments?
            .Where(a => a.Required == true)
            .Select(a => a.Name)
            .ToList() ?? [];

        var meta = downstream.Meta is { } existing
            ? (JsonObject)JsonNode.Parse(existing.ToJsonString())!
            : new JsonObject();
        meta[MetaKey] = new JsonObject
        {
            ["serverId"] = server.Id,
            ["serverName"] = server.Name,
            ["promptName"] = downstream.Name,
        };

        ProtocolPrompt = new Prompt
        {
            Name = WrapperNaming.For(server.Name, downstream.Name),
            Title = downstream.Title,
            Description = BuildDescription(server, downstream),
            Arguments = downstream.Arguments,
            Icons = downstream.Icons,
            Meta = meta,
        };
    }

    public override Prompt ProtocolPrompt { get; }

    public override IReadOnlyList<object> Metadata { get; } = [];

    /// <summary>Registered name of the downstream server this wrapper routes to.</summary>
    public string ServerName { get; }

    /// <summary>Immutable id of the downstream server at the time the wrapper was built.</summary>
    public string? ServerId { get; }

    /// <summary>The downstream prompt's own name.</summary>
    public string PromptName { get; }

    /// <summary>
    /// Hash of the downstream prompt's title, description and argument list. The catalog reuses a
    /// wrapper instance when the name and fingerprint are unchanged so the prompt collection is not
    /// churned on every refresh.
    /// </summary>
    public string ArgumentsFingerprint { get; }

    public override async ValueTask<GetPromptResult> GetAsync(
        RequestContext<GetPromptRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var provided = request.Params?.Arguments;

        var missing = _requiredArguments
            .Where(a => provided is null || !provided.ContainsKey(a))
            .ToList();

        if (missing.Count > 0)
        {
            _logger.LogWarning(
                "Prompt '{Prompt}' requested without required argument(s) [{Missing}]",
                ProtocolPrompt.Name, string.Join(", ", missing));

            throw new McpProtocolException(
                $"Missing required argument(s): [{string.Join(", ", missing)}] for prompt '{ProtocolPrompt.Name}'. " +
                $"Arguments: {DescribeArguments()}",
                McpErrorCode.InvalidParams);
        }

        // Prompt argument values are strings by spec; anything else is passed through as its JSON text.
        IReadOnlyDictionary<string, object?>? args = provided is null
            ? null
            : provided.ToDictionary(kvp => kvp.Key, kvp => (object?)(kvp.Value.ValueKind == JsonValueKind.String ? kvp.Value.GetString() : kvp.Value.GetRawText()));

        try
        {
            return await _proxy.GetPromptAsync(ServerName, PromptName, args, InvocationPath.Wrapper, cancellationToken);
        }
        catch (AggregatorException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // "Server 'x' is unavailable." / "timed out after 30s" are written for the caller.
            // Rethrown as a protocol error so the message reaches the client instead of the
            // SDK's generic internal-error text.
            _logger.LogWarning(ex, "Prompt '{Prompt}' failed: {Message}", ProtocolPrompt.Name, ex.Message);
            throw new McpProtocolException(ex.Message, McpErrorCode.InternalError);
        }
    }

    public override string ToString() => ProtocolPrompt.Name;

    private string DescribeArguments()
    {
        var arguments = ProtocolPrompt.Arguments;
        if (arguments is null || arguments.Count == 0)
            return "(none)";

        return "[" + string.Join(", ", arguments.Select(a =>
            a.Name + (a.Required == true ? " (required)" : " (optional)") +
            (string.IsNullOrWhiteSpace(a.Description) ? string.Empty : $": {a.Description.Trim()}"))) + "]";
    }

    private static string BuildDescription(RegisteredServer server, Prompt downstream)
    {
        var description = string.IsNullOrWhiteSpace(downstream.Description)
            ? $"Prompt '{downstream.Name}' on downstream MCP server '{server.Name}'."
            : downstream.Description.Trim();
        return $"[{server.Name}] {description}";
    }

    internal static string Fingerprint(Prompt prompt)
    {
        var sb = new StringBuilder();
        sb.Append(prompt.Title ?? string.Empty).Append('');
        sb.Append(prompt.Description ?? string.Empty).Append('');
        if (prompt.Arguments is { } args)
            sb.Append(JsonSerializer.Serialize(args, McpJsonUtilities.DefaultOptions));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }
}
