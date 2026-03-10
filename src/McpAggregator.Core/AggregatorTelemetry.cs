using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace McpAggregator.Core;

public static class AggregatorTelemetry
{
    public const string ServiceName = "mcp-aggregator";

    public static readonly ActivitySource ActivitySource = new(ServiceName);
    public static readonly Meter Meter = new(ServiceName);

    /// <summary>Total tool invocations; tags: server_name, tool_name, result (success|error|timeout|cancelled)</summary>
    public static readonly Counter<long> ToolInvocations =
        Meter.CreateCounter<long>("mcp_tool_invocations_total", description: "Total tool invocations routed to downstream servers");

    /// <summary>Tool invocation wall-clock duration; tags: server_name, tool_name</summary>
    public static readonly Histogram<double> ToolInvocationDuration =
        Meter.CreateHistogram<double>("mcp_tool_invocation_duration_seconds", unit: "s", description: "Wall-clock duration of tool invocations");

    /// <summary>Downstream server connection attempts; tags: server_name, result (connected|failed)</summary>
    public static readonly Counter<long> ConnectionAttempts =
        Meter.CreateCounter<long>("mcp_connection_attempts_total", description: "Total connection attempts to downstream MCP servers");

    /// <summary>Retry attempts after a broken connection; tags: server_name</summary>
    public static readonly Counter<long> ConnectionRetries =
        Meter.CreateCounter<long>("mcp_connection_retries_total", description: "Total reconnect retries for downstream servers");

    /// <summary>Currently active downstream connections; tags: server_name</summary>
    public static readonly UpDownCounter<long> ActiveConnections =
        Meter.CreateUpDownCounter<long>("mcp_active_connections", description: "Number of active downstream MCP server connections");
}
