using System.Diagnostics;
using System.Text;
using System.Text.Json;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace McpAggregator.Core.Tools;

/// <summary>
/// Values of the <c>via</c> telemetry tag: which aggregator surface a downstream call came through.
/// </summary>
public static class InvocationPath
{
    /// <summary>The generic <c>invoke_tool</c> proxy (or the REST invoke endpoint).</summary>
    public const string InvokeTool = "invoke_tool";

    /// <summary>A typed <c>{server}__{tool}</c> wrapper tool.</summary>
    public const string Wrapper = "wrapper";
}

public class ToolProxyHandler
{
    private readonly ConnectionManager _connectionManager;
    private readonly ToolIndex _toolIndex;
    private readonly AggregatorOptions _options;
    private readonly ILogger<ToolProxyHandler> _logger;

    private static readonly JsonSerializerOptions SchemaHintJsonOptions = new() { WriteIndented = false };

    public ToolProxyHandler(
        ConnectionManager connectionManager,
        ToolIndex toolIndex,
        IOptions<AggregatorOptions> options,
        ILogger<ToolProxyHandler> logger)
    {
        _connectionManager = connectionManager;
        _toolIndex = toolIndex;
        _options = options.Value;
        _logger = logger;
    }

    private void LogCallToolResult(string toolName, string serverName, CallToolResult result)
    {
        var contentSummary = string.Join(", ", result.Content
            .GroupBy(b => b.Type ?? "unknown")
            .Select(g => $"{g.Key}:{g.Count()}"));

        _logger.LogDebug(
            "Tool '{Tool}' on '{Server}' returned IsError={IsError}, content=[{Content}]",
            toolName, serverName, result.IsError, contentSummary);

        if (result.IsError is null)
        {
            _logger.LogDebug(
                "Tool '{Tool}' on '{Server}' has IsError=null (downstream did not set error flag explicitly)",
                toolName, serverName);
        }
    }

    /// <summary>
    /// When a tool call comes back as an error, checks the supplied arguments against the
    /// downstream tool's input schema. Returns a corrective message (listing the missing/unknown
    /// keys and the full schema) when there is an actual argument mismatch, or when the keys all
    /// match but the downstream's error reads like an argument-binding failure (a value of the
    /// wrong type — a string where the schema wants an array is the classic small-model slip).
    /// Genuine tool-side errors on schema-valid arguments are left untouched. Returns null when no
    /// hint applies.
    /// </summary>
    private async Task<string?> TryBuildArgumentHintAsync(
        string serverName,
        string toolName,
        IReadOnlyDictionary<string, object?>? providedArgs,
        string errorText,
        CancellationToken ct)
    {
        try
        {
            var tools = await _toolIndex.GetToolsForServerAsync(serverName, ct);
            var tool = tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));

            if (tool?.InputSchema is not JsonElement schema || schema.ValueKind != JsonValueKind.Object)
                return null;

            var propertyNames = schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
                ? props.EnumerateObject().Select(p => p.Name).ToList()
                : [];

            var required = schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
                ? req.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : [];

            var providedKeys = providedArgs?.Keys.ToList() ?? [];

            var missingRequired = required.Where(r => !providedKeys.Contains(r, StringComparer.Ordinal)).ToList();
            // Only flag unknown keys when the schema actually declares its properties.
            var unknownKeys = propertyNames.Count > 0
                ? providedKeys.Where(k => !propertyNames.Contains(k, StringComparer.Ordinal)).ToList()
                : [];

            var keysMatch = missingRequired.Count == 0 && unknownKeys.Count == 0;
            if (keysMatch && !LooksLikeBindingFailure(errorText))
                return null;

            var schemaText = JsonSerializer.Serialize(schema, SchemaHintJsonOptions);

            var sb = new StringBuilder();
            sb.Append("Argument mismatch for tool '").Append(toolName).Append("' on '").Append(serverName).Append("'. ");
            if (missingRequired.Count > 0)
                sb.Append("Missing required parameter(s): [").Append(string.Join(", ", missingRequired)).Append("]. ");
            if (unknownKeys.Count > 0)
                sb.Append("Unrecognized argument key(s): [").Append(string.Join(", ", unknownKeys)).Append("]. ");
            if (keysMatch)
                sb.Append("Every key matched the schema, so a supplied value did not match its declared type (for example a string where an array is required). ");
            if (providedKeys.Count > 0)
                sb.Append("You sent: [").Append(string.Join(", ", providedKeys)).Append("]. ");
            sb.Append("Re-invoke with arguments matching this input schema: ").Append(schemaText);

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to build argument hint for '{Tool}' on '{Server}'", toolName, serverName);
            return null;
        }
    }

    /// <summary>
    /// When a call fails at the protocol level, checks whether the server declares the tool at
    /// all. Returns a corrective message naming the tools it does declare when it does not, or
    /// null when the tool is known — so a genuine downstream fault keeps propagating untouched.
    /// </summary>
    private async Task<string?> TryBuildUnknownToolHintAsync(
        string serverName,
        string toolName,
        Exception cause,
        CancellationToken ct)
    {
        try
        {
            var tools = await _toolIndex.GetToolsForServerAsync(serverName, ct);

            if (tools.Any(t => string.Equals(t.Name, toolName, StringComparison.Ordinal)))
                return null;

            var available = tools.Count > 0
                ? string.Join(", ", tools.Select(t => t.Name).Order(StringComparer.Ordinal))
                : "(none)";

            // A common slip once typed wrappers exist: passing the '{server}__{tool}' name as the
            // downstream toolName. Say so, and name the downstream tool it maps to.
            var wrapperSlip = WrapperNaming.TryParse(toolName, out var prefix, out var downstreamName)
                && string.Equals(prefix, serverName, StringComparison.OrdinalIgnoreCase)
                && tools.Any(t => string.Equals(t.Name, downstreamName, StringComparison.Ordinal))
                ? $"'{toolName}' is the typed tool name; call it directly as a tool, or pass toolName: \"{downstreamName}\" here. "
                : string.Empty;

            return $"Unknown tool '{toolName}' on server '{serverName}'. {wrapperSlip}" +
                   $"Available tools: [{available}]. " +
                   $"Re-invoke with one of those names, or call get_service_details(serverName: \"{serverName}\") " +
                   $"for their input schemas. Underlying error: {cause.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to build unknown-tool hint for '{Tool}' on '{Server}'", toolName, serverName);
            return null;
        }
    }

    /// <summary>
    /// The C# SDK reports a downstream binding failure as "An error occurred invoking '{tool}'."
    /// with the detail confined to the server log; other SDKs say "invalid arguments" or
    /// "validation". None of them name the parameter, which is why the schema is attached.
    /// </summary>
    private static bool LooksLikeBindingFailure(string errorText)
        => errorText.Contains("An error occurred invoking", StringComparison.OrdinalIgnoreCase)
           || errorText.Contains("invalid argument", StringComparison.OrdinalIgnoreCase)
           || errorText.Contains("invalid params", StringComparison.OrdinalIgnoreCase)
           || errorText.Contains("validation", StringComparison.OrdinalIgnoreCase);

    internal static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray()
                .Select(ConvertJsonElement)
                .ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            _ => element.GetRawText(),
        };
    }

    /// <summary>
    /// Generic proxy path used by <c>invoke_tool</c> and the REST invoke endpoint: parses the
    /// stringified JSON argument object, then shares everything else with the typed-wrapper path.
    /// </summary>
    public async Task<CallToolResult> InvokeAsync(
        string serverName,
        string toolName,
        string? argumentsJson,
        CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, object?>? args = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson);
            if (raw is not null)
            {
                args = raw.ToDictionary(kvp => kvp.Key, kvp => ConvertJsonElement(kvp.Value));
            }
        }

        return await InvokeAsync(serverName, toolName, args, InvocationPath.InvokeTool, ct);
    }

    /// <summary>
    /// The single downstream call path. Timeout, retry, telemetry, the argument-schema hint on a
    /// downstream <c>isError</c>, the unknown-tool hint, and <c>isError</c> propagation all live
    /// here so the generic <c>invoke_tool</c> proxy and the typed wrapper tools behave identically.
    /// </summary>
    /// <param name="via">
    /// Which surface the call arrived through — one of the <see cref="InvocationPath"/> constants.
    /// Recorded as the <c>via</c> tag on the invocation metric and activity so reliability can be
    /// compared between the two paths.
    /// </param>
    public async Task<CallToolResult> InvokeAsync(
        string serverName,
        string toolName,
        IReadOnlyDictionary<string, object?>? args,
        string via,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Invoking tool '{Tool}' on '{Server}' via {Via}", toolName, serverName, via);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.DefaultToolTimeout);

        using var activity = AggregatorTelemetry.ActivitySource.StartActivity("mcp.tool_invoke");
        activity?.SetTag("server_name", serverName);
        activity?.SetTag("tool_name", toolName);
        activity?.SetTag("via", via);

        var sw = Stopwatch.StartNew();
        // Pessimistic default; success path overwrites before returning.
        string resultLabel = "error";

        try
        {
            var result = await _connectionManager.ExecuteWithRetryAsync<CallToolResult>(serverName,
                async (client, token) => await client.CallToolAsync(toolName, args, cancellationToken: token), cts.Token);

            LogCallToolResult(toolName, serverName, result);

            if (result.IsError ?? false)
            {
                var errorText = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
                _logger.LogWarning("Tool '{Tool}' on '{Server}' returned error: {Error}", toolName, serverName, errorText);

                // Self-correction: a common failure is the caller guessing parameter names
                // (e.g. from a tool description) instead of the actual input schema. Downstream
                // SDKs sanitize the binding error to a generic message, so attach the authoritative
                // schema and the specific mismatch so the model can retry without a separate
                // get_service_details round-trip.
                var hint = await TryBuildArgumentHintAsync(serverName, toolName, args, errorText, ct);
                if (hint is not null)
                {
                    result.Content = [.. result.Content, new TextContentBlock { Text = hint }];
                    _logger.LogInformation(
                        "Attached argument-schema hint to error result for '{Tool}' on '{Server}'",
                        toolName, serverName);
                }
            }

            resultLabel = "success";
            return result;
        }
        catch (McpProtocolException ex) when (!ct.IsCancellationRequested)
        {
            // A downstream rejects an unknown tool name at the protocol level, so the call throws
            // rather than returning an error result — which means the argument-schema
            // self-correction above never runs and a raw JSON-RPC fault escapes as an unhandled
            // exception. Give the caller the same self-correcting treatment: a structured error
            // result naming the tools that do exist. Genuine downstream faults (the tool exists
            // but the call failed) still propagate.
            var hint = await TryBuildUnknownToolHintAsync(serverName, toolName, ex, ct);
            if (hint is null)
                throw;

            _logger.LogWarning("Unknown tool '{Tool}' requested on '{Server}': {Error}",
                toolName, serverName, ex.Message);

            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = hint }]
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            resultLabel = "cancelled";
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            resultLabel = "timeout";
            throw new AggregatorException($"Tool '{toolName}' on '{serverName}' timed out after {_options.DefaultToolTimeout.TotalSeconds}s.");
        }
        finally
        {
            sw.Stop();

            if (resultLabel == "success")
                activity?.SetStatus(ActivityStatusCode.Ok);
            else if (resultLabel is "error" or "timeout")
                activity?.SetStatus(ActivityStatusCode.Error, resultLabel);

            var tags = new TagList
            {
                { "server_name", serverName },
                { "tool_name", toolName },
                { "result", resultLabel },
                { "via", via }
            };
            AggregatorTelemetry.ToolInvocations.Add(1, tags);
            AggregatorTelemetry.ToolInvocationDuration.Record(sw.Elapsed.TotalSeconds,
                new TagList { { "server_name", serverName }, { "tool_name", toolName }, { "via", via } });
        }
    }
}
