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

    public async Task<string> InvokeAsync(
        string serverName,
        string toolName,
        string? argumentsJson,
        CancellationToken ct = default)
    {
        var client = await _connectionManager.GetClientAsync(serverName, ct);

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
            var result = await client.CallToolAsync(toolName, args, cancellationToken: cts.Token);

            var textContent = result.Content
                .OfType<TextContentBlock>()
                .Select(c => c.Text)
                .ToList();

            if (textContent.Count == 0)
                return (result.IsError ?? false) ? "Tool returned an error with no text content." : "Tool completed with no text content.";

            var response = string.Join("\n", textContent);

            if (result.IsError ?? false)
            {
                _logger.LogWarning("Tool '{Tool}' on '{Server}' returned error: {Error}", toolName, serverName, response);
                return $"[Error] {response}";
            }

            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AggregatorException($"Tool '{toolName}' on '{serverName}' timed out after {_options.DefaultToolTimeout.TotalSeconds}s.");
        }
    }
}
