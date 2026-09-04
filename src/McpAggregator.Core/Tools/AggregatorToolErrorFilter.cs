using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tools;

/// <summary>
/// A <c>tools/call</c> filter that turns a parameter-binding failure on one of the aggregator's own
/// tools into a self-correcting error result.
/// <para>
/// <see cref="ToolProxyHandler"/> already gives this treatment to calls proxied to downstream
/// servers (#29, #36), but the aggregator's own tools had no equivalent: the SDK sanitizes a binding
/// <see cref="ArgumentException"/> down to "An error occurred invoking 'X'" and confines the detail
/// to the server log, so a caller cannot see what to fix. This filter reports the specific mismatch
/// and attaches the authoritative input schema instead.
/// </para>
/// <para>
/// Note that the binding <see cref="ArgumentException"/> raised by the SDK carries
/// <c>ParamName == "arguments"</c> — the name of the SDK's own arguments dictionary, not of the
/// parameter that failed to bind. The offending parameter is therefore identified by diffing the
/// supplied arguments against the tool's input schema rather than by trusting the exception.
/// </para>
/// </summary>
public static class AggregatorToolErrorFilter
{
    private static readonly JsonSerializerOptions SchemaHintJsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// Wraps the <c>tools/call</c> pipeline. Only an <see cref="ArgumentException"/> raised while
    /// binding arguments for a matched aggregator tool is converted; everything else — including the
    /// protocol faults and <c>AggregatorException</c>s raised by proxied downstream calls —
    /// propagates untouched.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        ILogger logger)
    {
        return async (request, ct) =>
        {
            try
            {
                return await next(request, ct);
            }
            catch (ArgumentException ex) when (!ct.IsCancellationRequested)
            {
                var toolName = request.Params?.Name ?? "(unknown)";
                var schema = (request.MatchedPrimitive as McpServerTool)?.ProtocolTool.InputSchema;

                var hint = TryBuildBindingHint(toolName, schema, request.Params?.Arguments, ex.Message);
                if (hint is null)
                    throw;

                logger.LogWarning(ex, "Argument binding failed for aggregator tool '{Tool}'", toolName);

                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = hint }]
                };
            }
        };
    }

    /// <summary>
    /// Builds the corrective message for a binding failure: the specific schema mismatch, the keys
    /// the caller actually sent, and the tool's full input schema. Returns <see langword="null"/> —
    /// meaning "rethrow, we have nothing useful to add" — when no usable object schema is available,
    /// which is the case for anything that is not one of the aggregator's own matched tools.
    /// </summary>
    internal static string? TryBuildBindingHint(
        string toolName,
        JsonElement? inputSchema,
        IDictionary<string, JsonElement>? providedArgs,
        string underlyingMessage)
    {
        if (inputSchema is not { } schema || schema.ValueKind != JsonValueKind.Object)
            return null;

        var propertyNames = schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
            ? props.EnumerateObject().Select(p => p.Name).ToList()
            : [];

        if (propertyNames.Count == 0)
            return null;

        var required = schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
            ? req.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];

        var providedKeys = providedArgs?.Keys.ToList() ?? [];

        var missingRequired = required.Where(r => !providedKeys.Contains(r, StringComparer.Ordinal)).ToList();
        var unknownKeys = providedKeys.Where(k => !propertyNames.Contains(k, StringComparer.Ordinal)).ToList();

        var schemaText = JsonSerializer.Serialize(schema, SchemaHintJsonOptions);

        var sb = new StringBuilder();
        sb.Append("Argument binding failed for aggregator tool '").Append(toolName).Append("'. ");
        if (missingRequired.Count > 0)
            sb.Append("Missing required parameter(s): [").Append(string.Join(", ", missingRequired)).Append("]. ");
        if (unknownKeys.Count > 0)
            sb.Append("Unrecognized argument key(s): [").Append(string.Join(", ", unknownKeys)).Append("]. ");
        if (missingRequired.Count == 0 && unknownKeys.Count == 0)
            sb.Append("Every required key was present, so a supplied value did not match its declared type. ");
        sb.Append("You sent: [").Append(providedKeys.Count > 0 ? string.Join(", ", providedKeys) : "(no arguments)").Append("]. ");
        sb.Append("Parameters absent from the schema's 'required' list may be omitted entirely. ");
        sb.Append("Re-invoke with arguments matching this input schema: ").Append(schemaText).Append(' ');
        sb.Append("Underlying error: ").Append(underlyingMessage);

        return sb.ToString();
    }
}
