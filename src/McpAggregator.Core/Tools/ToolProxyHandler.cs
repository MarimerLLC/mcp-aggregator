using System.Text.Json;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace McpAggregator.Core.Tools;

public class ToolProxyHandler
{
    private readonly ConnectionManager _connectionManager;
    private readonly AggregatorOptions _options;
    private readonly ILogger<ToolProxyHandler> _logger;

    public ToolProxyHandler(
        ConnectionManager connectionManager,
        IOptions<AggregatorOptions> options,
        ILogger<ToolProxyHandler> logger)
    {
        _connectionManager = connectionManager;
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

    public async Task<string> InvokeAsync(
        string serverName,
        string toolName,
        string? argumentsJson,
        CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, object?>? args = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson);
        }

        _logger.LogInformation("Invoking tool '{Tool}' on '{Server}'", toolName, serverName);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.DefaultToolTimeout);

        try
        {
            var result = await _connectionManager.ExecuteWithRetryAsync<CallToolResult>(serverName,
                async (client, token) => await client.CallToolAsync(toolName, args, cancellationToken: token), cts.Token);

            LogCallToolResult(toolName, serverName, result);

            var parts = new List<string>();

            foreach (var block in result.Content)
            {
                if (block is TextContentBlock text)
                {
                    parts.Add(text.Text);
                }
                else
                {
                    parts.Add($"[{block.Type ?? "unknown"} content block]");
                }
            }

            if (parts.Count == 0)
            {
                if (result.IsError ?? false)
                {
                    _logger.LogWarning("Tool '{Tool}' on '{Server}' returned error with no content blocks", toolName, serverName);
                    throw new ToolExecutionException(serverName, toolName, "No error details provided");
                }
                return "Tool completed with no content.";
            }

            var response = string.Join("\n", parts);

            if (result.IsError ?? false)
            {
                _logger.LogWarning("Tool '{Tool}' on '{Server}' returned error: {Error}", toolName, serverName, response);
                throw new ToolExecutionException(serverName, toolName, response);
            }

            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AggregatorException($"Tool '{toolName}' on '{serverName}' timed out after {_options.DefaultToolTimeout.TotalSeconds}s.");
        }
    }
}
